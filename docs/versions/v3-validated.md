# V3 — Secured and validated: check the SQL, then let the model fix it

> Article draft · AI Database Agent series · Part 3

## The idea

Treat generated SQL like any untrusted input: parse it, check it against an allow-list and the real schema,
estimate its cost — and when something is wrong, tell the model precisely what, and let it try again.

## The pipeline

```
model reply → parse (ScriptDom) → allow-list + schema check → cost estimate → execute (least privilege)
      ▲                 │                     │                     │             │
      └──── repair: the error, verbatim, as a new chat turn (up to 2 rounds) ◀──────┘
```

### 1. Parse and check — `SqlValidator`

Microsoft's own T-SQL parser (ScriptDom, `TSql170Parser`) builds the syntax tree, and the validator walks it
([ADR 0004](../decisions/0004-scriptdom-allow-list-validation.md)):

- exactly one `SELECT` (CTEs welcome); no `SELECT INTO`;
- only known-safe table sources; no other databases or linked servers, no `OPENROWSET`/`OPENQUERY`, no
  variables, UDFs or temp tables;
- every table in the agent's schema, every column on the table its alias points to.

Regular expressions would have been easy to fool — `SELECT 1 -- ; DROP TABLE x` is a harmless comment, and
`SELECT 1; DROP TABLE x` is two statements. A parser knows the difference.

### 2. Estimate — `SET SHOWPLAN_XML ON`

The optimizer's estimated cost, obtained as the agent user without running the query. Anything above the
limit is refused with a hint to narrow the question. Typical FoundryTrade questions estimate between 0.01
and 1; the default limit is 200.

### 3. Repair

A validation issue, compile error or runtime error goes back to the model as the next chat turn:

```text
That query was not accepted:
- Column 'Salary' does not exist on hr.EmployeeDirectory. Its columns are: EmployeeId, FirstName, LastName,
  Email, JobTitle, DepartmentId, ManagerId, HireDate, TerminationDate.

Write a corrected query using only tables and columns from the schema. If a column was taken from the wrong
table, take it from the table that has it (joining it if needed). Reply with CANNOT_ANSWER: <reason> only if
the data needed does not exist anywhere in the schema.
```

For the salary question the model now answers `CANNOT_ANSWER: The 'Salary' column does not exist…` — the
invented column never reaches the database, and the user gets an explanation.

## A failure found by using it

Building the UI turned up a regression. For "Which products are below their reorder level in the Leeds
Depot?" the model wrote `p.ReorderLevel` (Products) instead of `s.ReorderLevel` (Stock, already joined).
The validator said "no such column on catalog.Products" — and the model concluded the data didn't exist and
declined. It even wrote: *"It exists in the inventory.Stock table, but the query already joins
inventory.Stock as 's'."* — and declined anyway.

Two generic fixes, both unit-tested:

1. The validator says where the column really is: *"It exists on inventory.Stock (alias s), which is already
   in the query."*
2. The agent doesn't accept a refusal that contradicts the validator: when every rejected column exists
   elsewhere, a `CANNOT_ANSWER` gets one more turn — "the data DOES exist, take each column from the table
   that has it." The question now answers correctly on the third attempt.

## Results

| Version | Correct | Silently wrong | Failed | Declined correctly | Unsafe SQL executed | Avg time |
|---|---|---|---|---|---|---|
| V1 | 17/32 (53%) | 7 | 8 | 0 of 6 | 2 | 1.0 s |
| V2 | 26/32 (81%) | 4 | 2 | 4 of 6 | 0 | 1.2 s |
| **V3** | **28/32 (88%)** | 4 | **0** | 5 of 6 | **0** | 1.1 s |

Validation adds no model calls unless a repair is needed, and repairs were rare (1.03 attempts per
question on average); V3's average time is no higher than V2's.

## What V3 can't fix

All four remaining misses are wrong *definitions*, not wrong SQL:

| Question | V3's reading | What's meant |
|---|---|---|
| Orders last month | the last 30 days | the previous calendar month |
| Average order value by segment | average of order *lines* | average of order totals |
| Revenue by top-level category | grouped by subcategory | rolled up the category tree |
| Website conversion rate | invented from `SalesRepId IS NULL` | no such data — should decline |

Each query is valid, runs, and returns plausible numbers — the "silently wrong" kind. A validator can't know
what "average order value" means for this business. That knowledge belongs in a business glossary retrieved
per question — which is V4.
