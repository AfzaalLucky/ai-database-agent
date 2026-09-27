# V4 — Knowledge-augmented: retrieve the business definitions

> Article draft · AI Database Agent series · Part 4

## The problem V3 left

V3's remaining wrong answers were valid, safe, plausible SQL built on the wrong *definition*: "last month" as
the last 30 days, average order value over order lines, revenue by subcategory instead of top-level category,
and a "conversion rate" invented from unrelated columns. No validator can know what a business means by
"average order value". A data team writes that down — in a metrics glossary.

## The knowledge base

`knowledge/foundrytrade.json`, versioned with the code — 40 entries:

- **25 definitions** — revenue, bookings, AOV, margin, calendar periods, growth, the category tree, active
  customers, return rate, fulfilment time, amounts outstanding, inventory value, stock-outs, …
- **5 "not tracked" notes** — website analytics, marketing spend, salaries, contact details, reviews. They
  give the model a documented reason to decline instead of inventing a metric.
- **10 verified examples** — question → SQL pairs, none copied from the benchmark.

Every SQL snippet in it is tested: it must pass the V3 validator and run as `agent_reader`.

## Retrieval: dense alone wasn't enough

Entries are embedded with `nomic-embed-text` (Ollama) into Qdrant. The first measurement, on six probe
questions, found only 3 of 6 relevant definitions in the top five. Short glossary texts give compressed
similarity scores (0.52–0.77): "How much do our warehouse staff earn?" didn't reach the salary note (0.52),
"last month" ranked the calendar-period definition sixth.

Two changes, both standard practice:

1. **"Also known as" terms** on every definition — the vocabulary people use ("AOV", "earn", "main
   category", "YTD", "owed"). Embedded with the entry.
2. **Hybrid retrieval** — a literal match of a title or alias in the question (whole words, case-insensitive)
   includes that entry first; dense search fills the remaining slots for paraphrases. Deterministic and
   explainable: the UI shows *term "last month"* or *similarity 0.71* next to each definition.

To keep that honest, 12 new paraphrases were written *after* the aliases were final and never used to tune
them. Recall on those: **10/12** (the misses: "people work in Finance", "worth of the goods sitting in Leeds").

## What goes into the prompt

The top six definitions (≤ 4 by term match) as a "Business definitions that apply to this question" section
— NOT TRACKED entries flagged — and up to two retrieved examples as extra worked turns.

Examples turned out to be double-edged. With a loose threshold (0.60), a related-but-different example
added `Segment <> 'Consumer'` to "new customers in 2025", and another made the model write `TOP (5)` at the
*end* of a query. Measured similarities: true paraphrases of an example score 0.77–0.88, topical but
different questions up to 0.77. The threshold is now **0.80** — near-duplicates only.

## One more refusal rule

V3 already refused to accept a `CANNOT_ANSWER` when the validator had shown the data exists. V4 exposed a
second case: after a plain syntax error the model declined ("should use `TOP (n)` instead of `TOP (n)`").
The rule is now general — a refusal right after a *fixable* problem (syntax, compile or runtime error,
wrong-table column) gets one more turn; a refusal after missing data or a security rejection is accepted.

## Results

Two benchmarks ([eval/README.md](../../eval/README.md)): the original 32 questions — which, after this work,
served as the *development* set (the example threshold and refusal rule were fixed against its failures) —
and 12 held-out questions written after the knowledge base was frozen.

| | V1 | V2 | V3 | **V4** |
|---|---|---|---|---|
| Development set (32), two runs | 53% | 81% | 84% / 88% | **100% / 100%** |
| **Held-out set (12)**, two runs | 33% | 50% | 50% / 50% | **75% / 75%** |

The number to quote is the held-out one: **+25 points over V3 on questions it was never tuned on**, and V4
never ran unsafe SQL. On the development set V4 fixes three of V3's four original failures outright (calendar
"last month", the category roll-up, declining "conversion rate"); 100% there is expected after tuning and is
not a generalisation claim. V3's own score moved between runs (84% / 88%) at temperature 0 — local GPU
inference is not perfectly deterministic, so single-question differences are noise.

## What V4 still gets wrong

- **Retrieved ≠ followed.** Asked "What is the average order value for each customer segment?", V4 retrieved
  the AOV definition by exact term match — and still averaged order lines. The benchmark's phrasing ("…(revenue
  per order)…") is answered correctly. A 7B model weighs a one-line definition in a long system prompt less
  than its habits.
- Held-out misses: "active customers" also excluded returned orders (mixing in the revenue rule); "amount
  outstanding" was declined over an imaginary `OrderTotal` column; average fulfilment hours used integer `AVG`
  (the glossary warns about truncation for days, not hours — a gap in the definition itself).

Next experiments, in order: move the retrieved definitions from the system prompt into the user turn (closer
to the question); a larger local model; then V5 — let the agent look things up itself through tools.
