# Decision records

Short records of decisions that shaped the project: the context, what was decided, and what followed —
including the evidence (errors, test results, benchmark numbers) that drove them.

| # | Decision |
|---|---|
| [0001](0001-least-privilege-impersonation.md) | Run generated SQL as a login-less, least-privilege user via `EXECUTE AS USER … WITH COOKIE` |
| [0002](0002-sensitive-data-behind-a-view.md) | Hide sensitive columns behind a view, not a column-level DENY |
| [0003](0003-two-pass-schema-discovery.md) | Two-pass schema discovery, with keys carried through views |
| [0004](0004-scriptdom-allow-list-validation.md) | Validate generated SQL with Microsoft's T-SQL parser, as an allow-list |
| [0005](0005-provider-agnostic-llm-ollama.md) | Microsoft.Extensions.AI `IChatClient`, with a local Ollama model by default |
| [0006](0006-custom-deterministic-sample-database.md) | A purpose-built, deterministic sample database |
| [0007](0007-execution-accuracy-evaluation.md) | Measure versions by execution accuracy; don't tune the prompt to the benchmark |
| [0008](0008-hybrid-retrieval-business-glossary.md) | A versioned business glossary, retrieved with hybrid (term + vector) search |
| [0009](0009-examples-and-refusals.md) | Near-duplicate examples only; refusals after fixable errors are pushed back |
| [0010](0010-v5-tools-complement-context.md) | V5: tools complement context; the harness verifies; V4 stays the default |
| [0011](0011-opentelemetry-and-audit-log.md) | OpenTelemetry for the pipeline; an audit log in the agent's own database |
| [0012](0012-container-and-edge-limits.md) | A chiseled container for the API; limits for running behind a proxy |

Stack choices inherited from the AI Engineering Foundry plan (ASP.NET Core, SQL Server rather than Postgres,
self-hosted VPS deployment) are recorded there, not repeated here.
