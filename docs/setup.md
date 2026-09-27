# Setup

From nothing to a running, tested, benchmarked agent.

## 1. Prerequisites

| Tool | Version used | Check |
|---|---|---|
| .NET SDK | 10.0 (pinned by `global.json`, rolls forward to later 10.0.x feature bands) | `dotnet --list-sdks` |
| SQL Server | 2025 Developer edition (local, Windows authentication) | `sqlcmd -S localhost -E -C -Q "SELECT @@VERSION"` |
| sqlcmd | go-sqlcmd 1.10 (the ODBC sqlcmd works too) | `sqlcmd --version` |
| PowerShell | 7 (`pwsh`) — only for `database/setup.ps1` | `pwsh -v` |
| Ollama | 0.34 | `ollama --version` |
| Docker | 29 (Docker Desktop) — only for Qdrant (V4) | `docker --version` |

Pull the models (about 4.7 GB, 274 MB and 4.7 GB):

```powershell
ollama pull qwen2.5-coder:7b
ollama pull nomic-embed-text
ollama pull qwen2.5:7b          # V5: emits native tool calls (qwen2.5-coder doesn't)
```

Start the project's Qdrant (V4's vector store; its own container `ai-database-agent-qdrant` and volume,
ports bound to 127.0.0.1 only):

```powershell
docker compose up -d
```

Only one Qdrant can listen on 6333/6334 at a time — stop any other project's Qdrant container first.

## 2. Create the sample database

```powershell
./database/setup.ps1                                        # local server, Windows authentication
$env:SQLCMDPASSWORD = "<password>"                          # SQL authentication: sqlcmd reads the password from here
./database/setup.ps1 -Server "localhost,1433" -SqlAuth -User sa
```

This **drops and recreates `FoundryTrade`** (no other database is touched) and runs, in order:

| Script | What it does |
|---|---|
| `01-create-database.sql` | Creates the database (simple recovery, read-committed snapshot). |
| `02-schema.sql` | Schemas `catalog`, `sales`, `inventory`, `hr`; tables, keys, CHECK constraints; an `MS_Description` on every table and column. |
| `03-seed.sql` | Deterministic data: 52 employees, 150 products, 600 customers, 6,000 orders, 16,877 order lines, 5,831 payments, orders 2023-01-01 → 2026-08-31. Re-running produces identical rows. |
| `04-security.sql` | The `hr.EmployeeDirectory` view (no salaries), role `agent_readers`, and the login-less user `agent_reader`. |

Sanity-check it at any time (read-only):

```powershell
sqlcmd -S localhost -E -C -i database/05-verify.sql
```

Expect row counts as above, every integrity check `0`, the fingerprint `622378009`, and — while impersonating
`agent_reader` — errors 229/262/15591 for salary access, writes, DDL and `REVERT`.

## 3. Run the API and UI

```powershell
dotnet run --project src/AiDatabaseAgent.Api
```

- UI: http://localhost:5121
- Health: http://localhost:5121/health — `database`, `llm` and `knowledge` should be `Healthy`
  (`knowledge` is `Degraded` while Qdrant is down; V1–V3 still work)
- OpenAPI (Development only): http://localhost:5121/openapi/v1.json
- Requests to try: `src/AiDatabaseAgent.Api/AiDatabaseAgent.Api.http`

The first question after start-up takes longer (Ollama loads the model into memory); later ones take 1–3 s
on a machine with a capable GPU.

## 4. Test

```powershell
dotnet test
```

- `AiDatabaseAgent.Core.Tests` — no database or model needed.
- `AiDatabaseAgent.IntegrationTests` — needs FoundryTrade. Uses `Server=localhost;Database=FoundryTrade;Integrated Security=True;...`
  unless `FOUNDRYTRADE_CONNECTION` is set. The knowledge tests also need Ollama (`nomic-embed-text`) and
  Qdrant; they use throw-away collections and delete them afterwards.

## 5. Benchmark

```powershell
dotnet run --project tools/AiDatabaseAgent.Eval                          # V1–V5 × 32 development questions (~4 min)
dotnet run --project tools/AiDatabaseAgent.Eval -- --model qwen2.5:7b    # every version on one model
dotnet run --project tools/AiDatabaseAgent.Eval -- --questions eval/questions-holdout.json
dotnet run --project tools/AiDatabaseAgent.Eval -- --versions V4 --filter decline
```

Reports go to `eval/results/`. See [eval/README.md](../eval/README.md) for the scoring rules.

## Configuration

All in `src/AiDatabaseAgent.Api/appsettings.json` (the benchmark reads the same file). Override any value
with environment variables, e.g. `SqlServer__ConnectionString`, `Llm__Model`, `Agent__DefaultVersion`.

| Key | Default | Meaning |
|---|---|---|
| `SqlServer:ConnectionString` | set in `appsettings.Development.json` | The API's own identity. Needs `CONNECT`, `VIEW DEFINITION` and `IMPERSONATE` on the agent user — nothing else. |
| `SqlServer:AgentUser` | `agent_reader` | The database user every generated query runs as. |
| `SqlServer:SchemaCacheMinutes` | `10` | How long discovered schema is reused. |
| `Llm:Endpoint` / `Llm:Model` | `http://localhost:11434` / `qwen2.5-coder:7b` | Ollama server and model. |
| `Llm:ContextWindow` | `8192` | Sent as Ollama `num_ctx` (Ollama's default of 2048 is too small for the V2 prompt). |
| `Agent:DefaultVersion` | `V4` | Version used when a request doesn't name one. |
| `Agent:MaxRows` / `QueryTimeoutSeconds` | `500` / `15` | Hard limits on every query. |
| `Agent:ToolModel` | `qwen2.5:7b` | V5's model (must emit native tool calls through Ollama). |
| `Agent:MaxToolIterations` | `12` | V5 tool-calling round trips per turn. |
| `Agent:V5Context` | `Full` | `Full` = V4's context + tools; `Minimal` = table names only (the pure-agent experiment). |
| `Agent:V5SelfCheck` | `true` | V5: the agent test-runs the answer and asks the model to confirm or correct it. |
| `Agent:MaxRepairAttempts` | `2` | V3 repair rounds after a validation or database error. |
| `Agent:MaxEstimatedCost` | `200` | V3 refuses queries whose estimated optimizer cost is higher. |
| `Agent:DomainNotes` | FoundryTrade facts | Facts the schema can't express (data period, currency, revenue rule). |
| `Agent:Examples` | 3 examples | Few-shot question / explanation / SQL. Keep out of the benchmark. |
| `Knowledge:File` | `knowledge/foundrytrade.json` | V4 knowledge base (copied next to the API and benchmark binaries). |
| `Knowledge:QdrantHost` / `QdrantGrpcPort` / `Collection` | `localhost` / `6334` / `foundrytrade_knowledge` | Qdrant (gRPC). |
| `Knowledge:EmbeddingModel` | `nomic-embed-text` | Ollama embedding model (with `search_document:`/`search_query:` prefixes). |
| `Knowledge:DefinitionsTopK` / `MaxLexicalMatches` | `6` / `4` | Definitions per question, of which at most 4 by literal term match. |
| `Knowledge:MinDefinitionScore` / `MinExampleScore` | `0.55` / `0.8` | Cosine thresholds; examples must be near-duplicates. |
| `Knowledge:ExamplesTopK` | `2` | Retrieved worked examples per question. |
| `RateLimit:AskPerMinute` | `20` (`120` in Development) | Per-client-IP limit on `/api/ask`. |

No secrets are stored: development uses Windows authentication. For SQL authentication use
`dotnet user-secrets` or an environment variable for the connection string.

### Production database identity

In production the API should not connect as a sysadmin. A minimal login for it:

```sql
CREATE LOGIN agent_api WITH PASSWORD = '<from your secret store>';
USE FoundryTrade;
CREATE USER agent_api FOR LOGIN agent_api;
GRANT VIEW DEFINITION TO agent_api;                -- schema discovery (descriptions, keys, constraints)
GRANT IMPERSONATE ON USER::agent_reader TO agent_api;
```

These grants were verified with a stand-in user: it can read all metadata and impersonate `agent_reader`,
but cannot read any business data itself (error 229). View column lineage is read *as* `agent_reader`,
because describing a view needs SELECT on it.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `/health` → `llm` unhealthy, "not pulled" | `ollama pull qwen2.5-coder:7b`, or set `Llm:Model` to a model you have. |
| `/health` → `llm` Degraded, "V5 tool model … is not pulled" | `ollama pull qwen2.5:7b` (V1–V4 still work). |
| V5 never calls tools | Expected with full context and a 7B model; the harness self-check still test-runs every answer (see ADR 0010). |
| `/health` → `llm` unhealthy, "not reachable" | Start Ollama (`ollama serve`) or fix `Llm:Endpoint`. |
| `/api/ask` returns 503 "language model is not reachable" | Same as above. |
| SQL error **15590** "Can only use the 'No Revert' or 'Cookie' options with the 'Execute As' statement at the adhoc level" | Something sent `EXECUTE AS … WITH COOKIE` as a parameterised command (via `sp_executesql`). It must be an ad hoc batch — see [ADR 0001](decisions/0001-least-privilege-impersonation.md). |
| Error **229** permission denied in results | Expected when a query touches `hr.Employees` or writes: the agent user can't. V3 normally rejects these before the database sees them. |
| `setup.ps1`: `'-': Unknown command` | An older copy of the script splatted a one-element array; use the current script (`[string[]] $auth`). |
| First answer takes a minute | The model is loading; subsequent answers are fast. |
| `/health` → `knowledge` Degraded / V4 returns 503 "knowledge base (Qdrant) is not reachable" | `docker compose up -d`; check nothing else holds ports 6333/6334 (`docker ps`). |
| Knowledge edits don't show up | The index updates at API start-up and before each benchmark run; restart the API after editing `knowledge/*.json`. |
