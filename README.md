# AI Database Agent

Ask a SQL Server database a question in plain English and get back a **validated, schema-aware SQL answer** —
built version by version, from a naïve Text-to-SQL prototype to a secured, self-repairing agent, with every
step measured.

Part of **AI Engineering Foundry** (Phase 2 flagship project). Stack: .NET 10 · ASP.NET Core · SQL Server ·
Microsoft.Extensions.AI · Ollama (`qwen2.5-coder:7b`, `qwen2.5:7b`, `nomic-embed-text`, local) · Qdrant · ScriptDom ·
OpenTelemetry · Docker.

```
"Which 10 customers generated the most revenue in 2025?"
   → retrieve business definitions → schema-aware prompt → model writes T-SQL → parser allow-list +
     schema check → cost estimate → executed as a least-privilege database user
   → result, SQL, the knowledge used, and a trace of every attempt
```

## Results so far

Scored by **execution accuracy** against hand-written gold SQL ([eval/README.md](eval/README.md),
[reports](eval/results/)). Two runs each on `qwen2.5-coder:7b`:

| Version | What it adds | Development set (32) | **Held-out set (12)** | Unsafe SQL executed |
|---|---|---|---|---|
| **V1** Text-to-SQL | table + column names, run the SQL | 53% | 33% | 2 |
| **V2** Schema-aware | types, keys, join paths, descriptions, allowed values, rules, examples, may decline | 81% | 50% | 0 |
| **V3** Validated | T-SQL parser allow-list, schema check, cost check, error-driven repair | 84–88% | 50% | 0 |
| **V4** Knowledge | business glossary + verified examples, hybrid (term + vector) retrieval from Qdrant | 100% | **75%** | 0 |
| **V5** Agent | tools (describe, glossary, sample values, test queries) + harness self-check, on `qwen2.5:7b` | 97% | 75% | 0 |

The held-out questions were written after V4's knowledge base was frozen and never used to tune anything —
that column is the generalisation claim. The development set was used to fix V4's failures, so its 100% is
not. *Unsafe SQL executed*: V1 ran an `UPDATE` and a `DROP TABLE`; only the database's permissions stopped
them. V1–V4 run on `qwen2.5-coder:7b`; V5 needs native tool calls, which that model doesn't emit, so it runs on
`qwen2.5:7b` — on which V4 also scores 88% / 75% (every version was benchmarked on it too). With a local 7B model
the tools add little: a pure agent scored 47% / 42%, and given full context the model rarely calls a tool. V4 is
the default. Details: [V4](docs/versions/v4-knowledge.md#what-v4-still-gets-wrong) · [V5](docs/versions/v5-agent.md).

## How it stays safe

Model-generated SQL is untrusted input. Three independent layers:

1. **Database** — every query runs as `agent_reader`, a user *without login* that can only `SELECT` the
   business schemas and a salary-free `hr.EmployeeDirectory` view. The API impersonates it per query
   (`EXECUTE AS USER … WITH COOKIE`) and always reverts.
2. **Visibility** — the schema shown to the model is discovered *as that user*, so it never sees objects or
   columns it can't read.
3. **Validation (V3)** — Microsoft's T-SQL parser checks the query is exactly one `SELECT` over known tables
   and columns, with no other databases, linked servers, `OPENROWSET`, variables or UDFs; then the optimizer's
   estimate must be under a cost limit. Row cap and timeout apply to everything.

See [docs/architecture.md](docs/architecture.md) and the [decision records](docs/decisions/).

## How it's operated

- **Traces and metrics** (OpenTelemetry, OTLP): one question is one trace — retrieval, each model call with its
  tokens, validation, cost estimate, execution. Metrics for outcomes, per-stage latency, repairs, validator
  findings and tokens. Question text never goes on spans.
- **Audit log**: every question is a row in a separate `AgentOps` database, keyed by trace id — the agent never
  writes to the database it queries, and a failing audit database never costs an answer.
- **Container**: chiseled, non-root .NET 10 image; forwarded headers, per-client and concurrency limits for
  running behind Nginx; CI builds, tests and builds the image.

On a 132-question workload the model was 96% of the time; the first trace found a 2-second IPv6 `localhost`
penalty on every new connection to Ollama and Qdrant (fixed). [Part 6](docs/versions/v6-production.md) ·
[deployment guide](docs/deployment.md).

## Quick start

Requires the .NET 10 SDK, SQL Server (developed and tested on SQL Server 2025 Developer edition), `sqlcmd`,
PowerShell 7 for the setup script, and [Ollama](https://ollama.com).

```powershell
ollama pull qwen2.5-coder:7b
ollama pull nomic-embed-text                           # V4 embeddings
ollama pull qwen2.5:7b                                 # V5 tool calling
docker compose up -d                                   # Qdrant for V4 (V1–V3 work without it)
./database/setup.ps1                                   # FoundryTrade (dropped and rebuilt) + AgentOps (audit log, kept)
dotnet run --project src/AiDatabaseAgent.Api          # http://localhost:5121
```

Open http://localhost:5121, or `POST /api/ask` with `{ "question": "...", "version": "V4" }`.
Full instructions, configuration and troubleshooting: [docs/setup.md](docs/setup.md).

To see traces and metrics locally:

```powershell
docker compose --profile observability up -d          # Aspire dashboard: http://localhost:18888
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4317"
dotnet run --project src/AiDatabaseAgent.Api
```

Container and production setup (SQL logins, Nginx, grants): [docs/deployment.md](docs/deployment.md).

```powershell
dotnet test                                            # 160 unit + integration tests (database, Ollama, Qdrant)
dotnet run --project tools/AiDatabaseAgent.Eval        # benchmark V1–V5 on the development set
dotnet run --project tools/AiDatabaseAgent.Eval -- --questions eval/questions-holdout.json
```

## Repository map

| Path | Contents |
|---|---|
| `src/AiDatabaseAgent.Core` | Agent orchestration, prompts, response parsing, SQL validator, schema model/assembly, result comparer. No I/O. |
| `src/AiDatabaseAgent.Infrastructure` | SQL Server schema discovery and impersonated executor, Ollama chat client wiring, health checks. |
| `src/AiDatabaseAgent.Api` | ASP.NET Core minimal API, configuration (prompt notes, few-shot examples), and the web UI (`wwwroot`). |
| `tests/` | Unit tests (Core) and integration tests against a real FoundryTrade database. |
| `tools/AiDatabaseAgent.Eval` | Benchmark runner. |
| `eval/` | Development and held-out question sets with gold SQL, scoring rules, and result reports. |
| `knowledge/` | V4 business glossary (definitions, not-tracked notes, verified examples) — versioned and tested like code. |
| `docker-compose.yml` | Qdrant (default); profiles `observability` (Aspire dashboard) and `app` (the API container). Localhost-only ports. |
| `Dockerfile`, `.github/workflows/` | Container image; CI (build, unit tests, image build). |
| `database/` | FoundryTrade schema, deterministic seed, security script, verification queries, setup script; `ops/` — the AgentOps audit database and report queries. |
| `docs/` | Setup, architecture, decision records, and one write-up per version. |

## Documentation

- [Setup guide](docs/setup.md)
- [Deployment and operations](docs/deployment.md)
- [Architecture](docs/architecture.md)
- Decision records: [docs/decisions](docs/decisions/)
- Version write-ups: [V1](docs/versions/v1-text-to-sql.md) · [V2](docs/versions/v2-schema-aware.md) · [V3](docs/versions/v3-validated.md) · [V4](docs/versions/v4-knowledge.md) · [V5](docs/versions/v5-agent.md) · [Part 6: observability and packaging](docs/versions/v6-production.md)
- [Evaluation](eval/README.md)

## Roadmap

- **Next:** deploy the container to the VPS as a read-only public demo — needs a SQL login on the server and a
  decision on where the model runs (GPU host, smaller model, or a hosted one behind the same `IChatClient`).
- **Next experiment:** the same benchmark with a stronger model (a larger local one, or a hosted one behind the
  same `IChatClient`) — V5's tools and checks should gain the most from it. Then retrieved definitions in the user
  turn for V4.
- Later phases (per the Foundry plan): accounts on SQL Server, subscriptions, hosted multi-tenant SaaS.
