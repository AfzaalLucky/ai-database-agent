# Deployment and operations

How to run the API as a container behind a reverse proxy, what it needs, and how to watch it. For a
development setup see [setup.md](setup.md); for the reasoning, ADRs [0011](decisions/0011-opentelemetry-and-audit-log.md)
and [0012](decisions/0012-container-and-edge-limits.md).

```
 internet ──▶ Nginx (TLS) ──▶ ai-database-agent container :8080 ──▶ SQL Server  FoundryTrade (read, as agent_reader)
                                        │        │                            AgentOps     (audit log, INSERT only)
                                        │        ├──▶ Ollama (chat + embeddings)
                                        │        └──▶ Qdrant (knowledge base)
                                        └── OTLP ──▶ OpenTelemetry collector / Aspire dashboard
```

## 1. Databases

On the SQL Server the API will use:

```powershell
./database/setup.ps1 -Server "<server>" -SqlAuth -User sa     # FoundryTrade (drop + rebuild) and AgentOps (idempotent)
```

Then the API's own login, with nothing but what it needs:

```sql
CREATE LOGIN agent_api WITH PASSWORD = '<from your secret store>';

USE FoundryTrade;
CREATE USER agent_api FOR LOGIN agent_api;
GRANT VIEW DEFINITION TO agent_api;                  -- schema discovery
GRANT IMPERSONATE ON USER::agent_reader TO agent_api; -- every generated query runs as agent_reader

USE AgentOps;
CREATE USER agent_api FOR LOGIN agent_api;
GRANT INSERT ON audit.QueryLog TO agent_api;          -- the audit log; the API never reads it back
```

`agent_api` cannot read any business data itself; it can only impersonate `agent_reader`, which can only
`SELECT` the business schemas (see [setup.md](setup.md#production-database-identity)).

## 2. The container

```bash
docker build -t ai-database-agent .
docker run -d --name ai-database-agent --restart unless-stopped \
  -p 127.0.0.1:8080:8080 \
  -e SqlServer__ConnectionString="Server=<sql>;Database=FoundryTrade;User ID=agent_api;Password=<secret>;Encrypt=True" \
  -e Audit__ConnectionString="Server=<sql>;Database=AgentOps;User ID=agent_api;Password=<secret>;Encrypt=True" \
  -e Llm__Endpoint="http://<ollama-host>:11434" \
  -e Knowledge__QdrantHost="<qdrant-host>" \
  -e ReverseProxy__KnownProxies__0="172.17.0.1" \
  -e OTEL_EXPORTER_OTLP_ENDPOINT="http://<collector>:4317" \
  ai-database-agent
```

Or with docker-compose on one host: put the connection strings in `.env` (copy `.env.example`) and run
`docker compose --profile app --profile observability up -d --build`.

| Setting (environment variable) | Required | Notes |
|---|---|---|
| `SqlServer__ConnectionString` | yes | SQL login (`agent_api`). Use `Encrypt=True`; drop `TrustServerCertificate` once the server has a trusted certificate. |
| `Audit__ConnectionString` | no | Empty disables the audit log (`/health` → `audit` says so). |
| `Llm__Endpoint` | yes | Ollama. Must have the models pulled (see `/health` → `llm`). |
| `Knowledge__QdrantHost` / `Knowledge__QdrantGrpcPort` | V4+ | Qdrant gRPC (6334). |
| `ReverseProxy__KnownProxies__0` (`__1`, …) | behind a proxy | The proxy's address **as the container sees it** (on Docker's default bridge, the gateway `172.17.0.1`). Without it, every client shares the proxy's rate-limit partition. |
| `RateLimit__AskPerMinute` | no | Per client, default 20. |
| `RateLimit__AskConcurrency` / `RateLimit__AskQueue` | no | Questions in flight at once (default 4) and waiting (16); beyond that, 429. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | OTLP/gRPC endpoint; unset = no export. `OTEL_SERVICE_NAME` defaults to `ai-database-agent`. |
| `ASPNETCORE_ENVIRONMENT` | no | `Production` in the image. |

## 3. Nginx

```nginx
server {
    listen 443 ssl;
    server_name agent.example.com;            # your domain
    # ssl_certificate / ssl_certificate_key ...

    client_max_body_size 64k;                 # the API caps request bodies at 64 KB as well
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;   # one hop: the API trusts exactly one
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;              # a first question may wait for the model to load
    }
    location = /health { proxy_pass http://127.0.0.1:8080; allow 127.0.0.1; deny all; }
}
```

## 4. Watching it

- **`/health`** — `database` (connect, impersonate, query, revert), `llm` (models pulled), `knowledge` (Qdrant
  collection populated), `audit` (log writable). 503 only when something answers depend on is down; the
  knowledge base and audit log report `Degraded`.
- **Traces and metrics** — OTLP to any collector. Locally: `docker compose --profile observability up -d`, run
  the API with `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317`, open http://localhost:18888.
  One question is one trace: `POST /api/ask` → `agent.ask` → `agent.retrieve` (+ embedding call) →
  `chat <model>` per attempt → `agent.validate` → `db.estimate` → `db.execute`.

  | Metric | Tags | Use |
  |---|---|---|
  | `agent.questions` | version, outcome | Traffic and outcome mix (Answered, NotAnswerable, Rejected, Failed, Error). |
  | `agent.question.duration` | version, outcome | End-to-end latency. |
  | `agent.stage.duration` | stage, version | Where the time goes (retrieval, model, validation, estimate, execution). |
  | `agent.attempts` | version | Repairs: anything above 1. |
  | `agent.validation.issues` | issue kind, security | What the validator catches; `agent.security=true` is allow-list findings. |
  | `agent.tokens` | version, direction | Token use. |
  | `agent.audit.dropped` | reason | Audit records lost (queue full or database down). |
  | `gen_ai.client.*` | model | Model call duration and tokens (Microsoft.Extensions.AI). |

- **Audit log** — `AgentOps.audit.QueryLog`, one row per question with the trace id.
  `database/ops/audit-report.sql` has the standard queries. Retention is manual for now.
- **Privacy** — question text is stored in the audit log but never put on spans or metric tags; model prompts and
  replies are not exported. Generated SQL is on the `db.*` spans.
