# 0004 — Validate generated SQL with Microsoft's T-SQL parser, as an allow-list

- Status: accepted
- Date: 2026-09-27

## Context

V2 ran whatever SQL the model produced; only the database stopped harm. That is safe but unhelpful: an
invented column or a stray `UPDATE` only surfaces as a database error, and some things the database allows
(guest reads of `master`) should not be reachable at all.

Options: regular expressions / keyword deny-lists (trivially bypassed by comments, casing, nesting); asking a
second LLM to judge the SQL (non-deterministic, slow); a real parser.

## Decision

`SqlValidator` parses with `Microsoft.SqlServer.TransactSql.ScriptDom` (`TSql170Parser`, SQL Server 2025
syntax — the parser used by SSMS/DacFx) and walks the tree:

- exactly one statement, and it must be a `SelectStatement` (CTEs allowed); no `SELECT … INTO`;
- table sources on an **allow-list**: named tables/views, derived tables, `VALUES`, joins, `OPENJSON`,
  `STRING_SPLIT`, `GENERATE_SERIES`. Anything else — `OPENROWSET`, `OPENQUERY`, `OPENDATASOURCE`, `OPENXML`,
  table variables, table-valued functions — is refused;
- no linked servers, no database other than the configured one, no temp tables;
- no variables, no system variables (`@@VERSION`), no schema-qualified (user-defined) functions;
- table hints limited to `NOLOCK`/`READUNCOMMITTED`, query hints to `RECOMPILE`/`MAXDOP`;
- every table must exist in the agent's schema, and every column on the table (or CTE/derived table) its
  qualifier resolves to. Feedback lists the table's real columns and, when the column exists on another
  table, where — "It exists on inventory.Stock (alias s), which is already in the query."

Issues are classified (`Syntax`, `NotReadOnly`, `Forbidden`, `UnknownTable`, `UnknownColumn`); security
findings are logged as warnings. Failures feed the V3 repair loop.

## Consequences

- 46 unit tests: 16 legitimate query shapes that must pass (CTEs, window functions, correlated `EXISTS`,
  `UNION`, `DATEPART(month, …)` whose first argument parses like a column, bare table qualifiers, the
  database's own three-part name) and 30 that must fail with the right category — plus wrong-table hints.
- On the benchmark V3 executed 0 unsafe statements and had 0 failures (V1: 2 unsafe, 8 failed).
- Column resolution is deliberately conservative: with `SELECT *` in a CTE or an `OPENJSON` source, bare
  column names are not checked (their origin can't be known statically). The database remains the backstop.
- The parser version must track the SQL Server version for new syntax.
