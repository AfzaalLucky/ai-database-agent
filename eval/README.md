# Evaluation

A fixed benchmark that measures each agent version against hand-written gold SQL, so claims like
"V3 is more accurate" are numbers, and so any change to prompts, models or validation can be checked for
regressions.

## Run it

Prerequisites: the FoundryTrade database (`database/setup.ps1`) and Ollama with the configured model.

```powershell
dotnet run --project tools/AiDatabaseAgent.Eval                                  # V1–V5 × the development set
dotnet run --project tools/AiDatabaseAgent.Eval -- --versions V3                 # one version
dotnet run --project tools/AiDatabaseAgent.Eval -- --filter decline              # a category or id fragment
dotnet run --project tools/AiDatabaseAgent.Eval -- --model qwen2.5:7b            # every version on one model
```

The runner loads the API's own `appsettings.json` (+ `appsettings.Development.json`), so it evaluates the
agent with exactly the prompt notes, examples, limits and model the API uses. Each run writes
`results/<date>-<model>.md` (readable report) and `.json` (every answer, SQL and verdict).

## How answers are scored

**Execution accuracy**, not SQL text: the gold query and the agent's query are both run (as the restricted
`agent_reader` user) and their results compared.

- Gold SQL returns only the columns that *define* a correct answer, often just the measure. The agent's
  result must contain a distinct column matching each gold column; names and extra columns are ignored, so
  returning `FirstName, LastName, OrdersHandled` for a gold `COUNT(*)` is fine.
- Row counts must match. Rows are compared as a multiset, or in sequence for questions marked `ordered`.
- Numbers match within 0.006 absolute (2-decimal rounding passes) or 1e-6 relative. `absolute` questions
  ignore the sign (refunds are stored as negative amounts; a positive total is equally right).
- Strings compare case-insensitively; dates by value; numeric strings as numbers.

The comparison is unit-tested in `tests/AiDatabaseAgent.Core.Tests/Evaluation/ResultComparerTests.cs`.

## Verdicts

| Verdict | Meaning |
|---|---|
| **Correct** | The result matches gold — or the question should be declined and was. |
| **Silently wrong** | The agent answered, with the wrong data (or answered something it should have declined). The most dangerous failure: nothing tells the user. |
| **Wrongly declined** | The agent said it couldn't answer a question the schema supports. |
| **Failed** | An error, a rejected query, or no SQL — visible to the user, not misleading. |

Separately, 🛑 **unsafe SQL executed** marks V1/V2 answers whose SQL V3's validator refuses on security
grounds (writes, other databases, …). Those were stopped only by the database's permissions.

## Two question sets

| File | Role |
|---|---|
| `questions.json` (32) | **Development set.** V4's example threshold and refusal policy were chosen against its failures, so V4's score here is not a generalisation claim. |
| `questions-holdout.json` (12) | **Held-out set.** Written after V4's knowledge base was frozen; never used to tune prompts, glossary entries or aliases. Quote this one. |

Once a change is tuned against a held-out failure, that question is no longer held out — write new ones.

## The development set

`questions.json` — 32 questions in 13 categories: simple counts, aggregation, dates (including relative
"last month"), business rules (the revenue definition), top-N, joins, a self-referencing hierarchy, a
category tree, inventory, payments, anti-joins, ratios, and 6 questions that must be declined (data not in
the schema, a write request, a prompt-injection attempt). Every gold query was run as `agent_reader` and
checked for ties at top-N boundaries.

Rules for editing it:

- Never reuse a few-shot example from `appsettings.json`, or an example from `knowledge/*.json`, as an
  evaluation question (an integration test enforces the latter).
- Phrase questions so a careful analyst would write one answer; record any judgement call in `notes`
  (they appear in the report under *Scoring decisions*).
- Relative dates in gold SQL use `GETDATE()`, like the agent, so both move together.
