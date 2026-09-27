# 0011 — OpenTelemetry for the pipeline; an audit log in the agent's own database

- Status: accepted
- Date: 2026-09-27

## Context

Up to V5 the agent was observable only per request: the response (and the UI's trace panel) carried
per-attempt model time, tokens, query time and the cost estimate, and the API logged one line per question.
That answers "what happened to *this* question", not "how is the agent doing": no latency distribution per
stage, no rate of repairs or validator rejections, nothing kept after the response was sent, and no way to
follow one question through Ollama, Qdrant and SQL Server.

## Decision

1. **Instrument with the BCL, export with OpenTelemetry.** `Core/Observability/AgentTelemetry` defines an
   `ActivitySource` and a `Meter` named `AiDatabaseAgent`. Core stays free of SDKs and exporters; the API adds
   the OpenTelemetry SDK and exports over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
   - Spans: `agent.ask` per question, with `agent.retrieve`, `agent.validate`, `db.estimate`, `db.execute`
     children. Model and embedding calls get OpenTelemetry GenAI spans from Microsoft.Extensions.AI's
     `UseOpenTelemetry()`; ASP.NET Core and HttpClient instrumentation cover the request and the calls to
     Ollama and Qdrant. One question is one trace.
   - Metrics: `agent.questions` (by version and outcome), `agent.question.duration`, `agent.stage.duration`
     (retrieval, model, validation, estimate, execution), `agent.attempts`, `agent.validation.issues` (by issue
     kind, flagged when security-relevant), `agent.tokens`, `agent.audit.dropped`.
   - **Question text is never a span attribute or metric tag.** It is free user input. Generated SQL *is*
     recorded on the `db.*` spans: it is what an operator needs, and it comes from the model, not the user.
     Model prompts and replies are not recorded (`EnableSensitiveData` stays off).
2. **Every question goes to an audit log, `audit.QueryLog`, in a separate `AgentOps` database.** Not in
   FoundryTrade: that stands in for a customer's business database, which the agent only reads (as
   `agent_reader`) and which `setup.ps1` drops and re-seeds. The API's identity needs only `INSERT` on the log.
   Rows carry the W3C trace id, so a row in the log leads to its trace and vice versa (the UI shows it too).
3. **The audit log can never cost an answer.** Requests enqueue into a bounded in-memory channel; a background
   writer drains it in batches. A full queue or a failing database drops records (counted in
   `agent.audit.dropped`, logged as a warning) instead of slowing or failing requests. The health check reports
   an unreachable audit database as `Degraded`, not `Unhealthy`. Infrastructure failures (model or knowledge
   base unreachable) are logged with outcome `Error`.
4. **Local viewing: the .NET Aspire dashboard** as an opt-in docker-compose profile (`observability`), bound to
   localhost. Production points the same OTLP endpoint at any OpenTelemetry collector.

## Consequences

- One question can be followed end to end: HTTP request → retrieval (and the embedding call) → each model call
  (with tokens) → validation → cost estimate → execution, with the SQL on the database spans.
- Repairs, refusals and validator findings are counted, not inferred from logs. `database/ops/audit-report.sql`
  answers the common questions (outcome mix, p50/p95 latency, the review queue of non-answers, slowest questions).
- The benchmark still reads results directly from the agent and is unaffected; the audit log records traffic
  through the API only.
- Retention is not automated yet: `audit.QueryLog` grows until someone deletes old rows. Fine for a demo; a
  hosted version needs a retention job and a decision on how long questions may be kept.
