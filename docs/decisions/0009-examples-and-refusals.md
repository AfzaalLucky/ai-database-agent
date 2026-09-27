# 0009 — Near-duplicate examples only; refusals after fixable errors are pushed back

- Status: accepted
- Date: 2026-09-27

## Context

The first V4 benchmark run fixed three of V3's four original failures but introduced new ones:

- "How many new customers joined in 2025?" gained `Segment <> 'Consumer'` — copied from a retrieved example
  about business customers (similarity 0.63).
- "Top 5 products by units" produced `ORDER BY … TOP (5)` — the retrieved example (0.655) had no `TOP`. The
  parser rejected it; the model then answered `CANNOT_ANSWER: … should use TOP (n) instead of TOP (n)`.

## Decision

1. **Examples must be near-duplicates**: minimum similarity 0.80. Measured: paraphrases of an example
   0.77–0.88; the benchmark's topical-but-different questions up to 0.77. A misleading example costs more than
   a missing one.
2. **Refusal policy** (V3+): a `CANNOT_ANSWER` immediately after a *fixable* problem — a syntax error, a
   compile or runtime error from SQL Server, or columns taken from the wrong table — is answered with one more
   turn ("this was an error in the SQL, not missing data"). Refusals after missing data, unknown tables or
   security rejections are accepted. This generalises the earlier wrong-table rule from V3.

## Consequences

- Both fixes were chosen on the development benchmark's failures; the held-out benchmark (written before
  them) is what measures their effect on generalisation — V4 held out at 75% before and after.
- Deterministic unit tests cover both refusal cases; retrieval tests check that close questions do, and
  unrelated ones don't, retrieve examples.
- With fewer examples retrieved, V4 relies more on definitions — whose adherence by a 7B model is the next
  limitation (see [V4 write-up](../versions/v4-knowledge.md#what-v4-still-gets-wrong)).
