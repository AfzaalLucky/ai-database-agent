# Architecture

## Components

```
                ┌──────────────────────────── AiDatabaseAgent.Api ─────────────────────────────┐
 browser ──────▶│ wwwroot UI   POST /api/ask  GET /api/prompt  GET /api/schema  GET /health    │
                │ rate + concurrency limits · forwarded headers · CSP · OpenTelemetry · audit    │
                └───────────────┬───────────────────────────────────────────────────────────────┘
                                │
                ┌───────────────▼──────────── AiDatabaseAgent.Core (no I/O) ────────────────────┐
                │ TextToSqlAgent ── prompts (V1 basic, V2/V3 schema-aware) ── ModelResponseParser │
                │        │         SqlValidator (ScriptDom allow-list)   SchemaAssembler          │
                │        │         ResultComparer (evaluation)                                    │
                │  ports: IChatClient · ISchemaProvider · IQueryExecutor                          │
                └────────┼──────────────────────┬───────────────────────────┬──────────────────┘
                         │                      │                           │
      ┌──────────────────▼──────┐   ┌───────────▼─────────────┐   ┌─────────▼───────────────┐
      │ OllamaSharp IChatClient │   │ SqlServerSchemaProvider │   │ SqlServerQueryExecutor  │
      │ + num_ctx, logging      │   │ (cached, two passes)    │   │ (impersonated, capped)  │
      └──────────┬──────────────┘   └───────────┬─────────────┘   └─────────┬───────────────┘
                 ▼                              ▼                           ▼
        Ollama qwen2.5-coder:7b          SQL Server · FoundryTrade (agent_reader, agent_readers role)
```

`Core` depends only on abstractions: `IChatClient` (Microsoft.Extensions.AI), `ISchemaProvider`,
`IQueryExecutor`, and (V4) `IKnowledgeRetriever`. `Infrastructure` implements them — the retriever is
`QdrantKnowledgeStore`, backed by Qdrant and an `IEmbeddingGenerator` (Ollama `nomic-embed-text`); `Api`
composes everything; `tools/AiDatabaseAgent.Eval` composes the same services to benchmark.

## V4: knowledge retrieval

```
knowledge/foundrytrade.json ──(content hash diff)──▶ embed changed entries ──▶ Qdrant collection
                                                                                    │
question ─┬─▶ GlossaryMatcher: title/alias found in the question? (≤ 4, longest term first)
          └─▶ embed "search_query: …" ──▶ Qdrant: definitions ≥ 0.55 · examples ≥ 0.80
                    ▼
     up to 6 definitions + 2 examples ──▶ "Business definitions" section + extra worked turns ──▶ V3 pipeline
```

Indexing runs at start-up in the background (`KnowledgeWarmup`), lazily on the first V4 question, and
explicitly in the benchmark. See [ADR 0008](decisions/0008-hybrid-retrieval-business-glossary.md).

## Request flow (V3)

1. **Guard the input** — non-empty, ≤ 500 characters, rate limited.
2. **Schema** — from cache (10 min) or discovered (below).
3. **Prompt** — system rules + the schema as annotated DDL + domain notes + today's date; few-shot examples
   as prior chat turns; then the question.
4. **Model** — temperature 0, `num_ctx` 8192. The reply is parsed for a ```` ```sql ```` block (tolerating
   missing fences) or `CANNOT_ANSWER: <reason>`.
5. **Validate** — ScriptDom `TSql170Parser`; allow-list rules; every table/column resolved against the schema.
6. **Estimate** — `SET SHOWPLAN_XML ON` as the agent user; refuse above `MaxEstimatedCost`.
7. **Execute** — as the agent user; stop reading at `MaxRows`; command timeout.
8. **Repair** — a failure at 4, 5, 6 (compile) or 7 is fed back to the model as a new chat turn, up to
   `MaxRepairAttempts` times. Timeouts are not retried. A `CANNOT_ANSWER` is accepted, unless it comes right
   after a fixable problem — a syntax error, a compile/runtime error, or a column taken from the wrong table
   (the data exists) — then the model is told so and asked again ([ADR 0009](decisions/0009-examples-and-refusals.md)).
9. **Respond** — outcome (`Answered`, `NotAnswerable`, `Rejected`, `Failed`), SQL, explanation, rows,
   estimate, and every attempt with its issues, timings and token counts.

V1 skips 3's rich context and 5–6 and 8; V2 skips 5–6 and 8. V4 adds retrieval before 3 (below), and the
response also lists the knowledge used. V5 wraps step 4 in `FunctionInvokingChatClient` with five tools
(`list_tables`, `describe_table`, `search_glossary`, `sample_values`, `run_query` — the last applies steps 5–7 with
a 20-row preview), then test-runs the answer itself and asks the model to confirm or correct it before step 7
([ADR 0010](decisions/0010-v5-tools-complement-context.md)).

## Security layers

| Layer | Stops | Where |
|---|---|---|
| Least-privilege identity | writes, DDL, `EXEC`, other schemas, the salary column, `REVERT` of the impersonation | `database/04-security.sql`, `ImpersonatedSession` |
| Visibility | the model learning about objects it can't use | two-pass discovery, `SchemaAssembler` |
| Validation (V3) | multiple statements, non-`SELECT`, `SELECT INTO`, other databases/linked servers, `OPENROWSET`/`OPENQUERY`/`OPENDATASOURCE`, variables, system variables, UDFs, temp tables, risky hints, unknown tables/columns | `SqlValidator` |
| Cost check (V3) | pathological plans (e.g. accidental cross joins) | `EstimateAsync` |
| Limits | runaway result sets and long queries | `QueryLimits` (rows, timeout) |
| API | abuse, clickjacking, script injection in the UI | rate limiter, CSP and headers, UI renders data with `textContent` only |

The database layer is the one that holds even if everything above it is wrong; the others make failures
early, explainable and repairable.

## Schema discovery

Two passes, then pure assembly:

| Pass | Identity | Reads |
|---|---|---|
| Metadata | the API (`VIEW DEFINITION`) | tables/views, `MS_Description`s, primary keys, foreign keys, column CHECK constraints |
| Visibility | impersonated `agent_reader` | every column it may `SELECT` (`HAS_PERMS_BY_NAME`), and view column lineage (`sys.dm_exec_describe_first_result_set` in browse mode) |

`SchemaAssembler` (unit-tested, no database) then keeps only selectable columns, lets view columns inherit
base-column descriptions and allowed values, gives a view its base table's primary key, and redirects foreign
keys that point at hidden tables onto the view that exposes them — so `sales.Orders.SalesRepId` joins to
`hr.EmployeeDirectory.EmployeeId` even though `hr.Employees` is invisible.

## The sample database

`FoundryTrade`: a fictional distributor of workshop tools. 11 tables and 1 view across `catalog`, `sales`,
`inventory`, `hr`; self-referencing manager hierarchy; a two-level category tree; status enums enforced by
CHECK constraints; a computed `LineTotal`; refunds as negative payments. The seed derives every value from
a SHA-256 of (row, salt), so data is identical on every build.

## Observability

([ADR 0011](decisions/0011-opentelemetry-and-audit-log.md), operations guide: [deployment.md](deployment.md#4-watching-it))

- **Traces** — `Core/Observability/AgentTelemetry` (BCL `ActivitySource`, no SDK in Core). One question is one
  trace: `POST /api/ask` → `agent.ask` → `agent.retrieve` → a GenAI `chat` span per model call (from
  Microsoft.Extensions.AI's `UseOpenTelemetry()`) → `agent.validate` → `db.estimate` → `db.execute`, plus the
  HTTP calls to Ollama and Qdrant. Generated SQL is on the `db.*` spans; question text never is.
- **Metrics** — questions by version and outcome, end-to-end and per-stage latency, attempts (repairs),
  validator findings by kind, tokens, dropped audit records; plus ASP.NET Core, HttpClient and GenAI metrics.
- **Export** — the API adds the OpenTelemetry SDK and exports traces, metrics and logs over OTLP when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set (locally: the Aspire dashboard, docker-compose profile `observability`).
- **Audit log** — every question (including infrastructure errors) is a row in `AgentOps.audit.QueryLog`, a
  separate database from the one the agent queries, keyed by trace id. Written from a bounded queue by a
  background service: a slow or missing audit database drops records, never answers.
- **Per response** — per-attempt model time and tokens, query time, the cost estimate and the trace id, shown in
  the UI's trace panel and recorded by the benchmark.
- **`/health`** — the full database path (connect → impersonate → query → revert), models pulled, the Qdrant
  collection populated, and the audit log writable (`Degraded`, not `Unhealthy`, for the last two: answers
  don't depend on them).
