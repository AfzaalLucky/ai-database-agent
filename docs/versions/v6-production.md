# Part 6 — Observability and production packaging: what the traces showed

> Article draft · AI Database Agent series · Part 6

## The gap

After V5 the agent was accurate enough to talk about and safe by construction, but it could only be observed
one request at a time. The response carried model time, tokens and query time; the API logged one line per
question. Nothing answered "how is it doing?", nothing was kept, and a slow question couldn't be taken apart.
This part adds the operational layer — no accuracy changes — and then uses it.

## What was added

**Traces.** A BCL `ActivitySource` in Core (no SDK there), exported by the API with OpenTelemetry over OTLP.
One question is one trace:

```text
POST /api/ask
└─ agent.ask                       version, outcome, attempts, rows
   ├─ agent.retrieve               definitions/examples retrieved (V4+)
   │  └─ embeddings nomic-embed-text      (Microsoft.Extensions.AI, GenAI conventions)
   ├─ chat qwen2.5-coder:7b        one per model call, with token counts
   ├─ agent.validate               valid?, the issues found
   ├─ db.estimate                  estimated cost — the SQL is on the span
   └─ db.execute                   rows returned, SQL Server error number on failure
```

Question text is never a span attribute (it is free user input); the generated SQL is.

**Metrics.** Questions by version and outcome, end-to-end and per-stage latency, attempts per question,
validator findings by kind (flagged when security-relevant), tokens, dropped audit records.

**An audit log** — one row per question, in a separate `AgentOps` database: the agent reads the business
database as a least-privilege user and never writes to it. Rows carry the trace id. Writes go through a bounded
queue and a background writer, so a slow or missing audit database costs records, never answers.

**Packaging.** A multi-stage Dockerfile on a chiseled (distroless, non-root) .NET 10 image, 267 MB;
docker-compose profiles for the API and an Aspire dashboard; forwarded headers trusted only from configured
proxies (behind Nginx, every visitor would otherwise share the proxy's rate-limit bucket); a concurrency cap on
`/api/ask` because a local model serves one request at a time; GitHub Actions for build, unit tests and the
image.

## A real workload

The 44 benchmark questions (development + held-out) sent through the API for V3, V4 and V5 — 132 requests in
four minutes on one machine (Ollama on the local GPU), straight from the audit log:

| | Answered | Declined | Rejected / failed | p50 | p95 | Avg input tokens | Non-model time (avg) |
|---|---|---|---|---|---|---|---|
| V3 | 35 | 8 | 1 | 1.32 s | 2.68 s | 2,171 | 36 ms |
| V4 | 35 | 9 | 0 | 1.66 s | 3.25 s | 2,651 | 157 ms |
| V5 | 36 | 8 | 0 | 2.10 s | 6.14 s | 7,697 | 52 ms |

(Declines include the six questions that *should* be declined — writes, salaries, prompt injection. Accuracy is
the benchmark's job; this is operational data.)

- **The model is 96% of the time.** Retrieval, validation, the cost estimate and execution together average
  36–157 ms. Optimising anything but the model call is pointless at this scale.
- **V5 costs 2.9× V4's input tokens** for the same held-out accuracy: tool definitions and the self-check turn.
  Its "more than one attempt" count (38 of 44) is the self-check, not repairs — a metric that needs its
  definition next to it.
- **V3's one failure:** "Who is the highest paid employee?" — three replies without SQL or a refusal. V4 and V5
  declined it, citing the glossary's "salaries are confidential" note.

## What the first trace found

The slowest V4 question was "How many customers do we have?" — 5.05 s, with a 0.89 s model call. The audit
log could say *that* it was slow, not *why*. The trace could:

```text
agent.ask                               5.05 s
├─ agent.retrieve                       4.14 s
│  └─ embeddings nomic-embed-text       2.04 s
│     (…1.9 s with no span, then two Qdrant calls of 15 ms and 1 ms)
├─ chat qwen2.5-coder:7b                0.89 s
└─ validate + estimate + execute        0.01 s
```

Two gaps of almost exactly two seconds, both on the first connection to a service. The cause:

```text
localhost:11434   2055 ms        127.0.0.1:11434   25 ms
localhost:6334    2043 ms        127.0.0.1:6334     0.3 ms
```

On Windows, `localhost` resolves to `::1` first; Ollama and the Qdrant container listen on IPv4 only, so every
*new* connection waited ~2 s for IPv6 to fail. HTTP connections are pooled but closed when idle, so this wasn't
only a start-up cost: after a quiet spell, the next question could pay it again. The fix was configuration
(`127.0.0.1` in the defaults). The same cold request afterwards: **retrieval 4.14 s → 55 ms**, embeddings
2.04 s → 36 ms.

The same trace showed what was left: 9.65 s in one `chat` span — Ollama loading `qwen2.5-coder:7b` again after V5
had swapped in `qwen2.5:7b`. Two models on one GPU means alternating versions pay a model load. That's a
deployment decision (one model, or enough memory for both), now visible instead of guessed at.

## What's verified, and what isn't

- 160 tests pass (125 unit, 35 integration), including spans and metrics from the real agent loop and audit
  rows written to a real AgentOps database.
- The container image builds, runs as a non-root user, serves the UI and reaches Ollama and Qdrant; a
  deliberately wrong SQL login shows as `Unhealthy` with SQL Server's own message. A full answer from inside the
  container needs a SQL login on the server — the first step of the VPS deployment, not done yet.
- Where the model runs in production (GPU host, smaller model, or a hosted model behind the same `IChatClient`)
  is still open. The traces will make that comparison concrete.
