# 0003 — Two-pass schema discovery, with keys carried through views

- Status: accepted
- Date: 2026-09-26

## Context

The model must see exactly what the agent can query — no more (it would write queries that fail, or learn
that confidential tables exist), no less (it couldn't answer). But the agent's own identity can't see the
metadata that makes a schema useful: `hr.Employees` is invisible to it, so `sales.Orders.SalesRepId`'s
foreign key to it disappears, and the view's columns have no descriptions of their own.

## Decision

1. **Metadata pass**, as the API identity (`VIEW DEFINITION`): tables and views, descriptions, primary keys,
   foreign keys, column CHECK constraints.
2. **Visibility pass**, impersonating the agent user: every column it can SELECT (`HAS_PERMS_BY_NAME` per
   column), and view column lineage (`sys.dm_exec_describe_first_result_set` over `SELECT * FROM <view>` in
   browse mode). Lineage runs here because describing a view needs SELECT on it — found when verifying the
   minimal production grants, where the API identity got no rows.
3. **`SchemaAssembler`** (pure, unit-tested) combines them: keep selectable columns only; view columns inherit
   the base column's description and allowed values; a view inherits its base table's primary key; a foreign
   key pointing at a hidden table is redirected to a visible view exposing the referenced columns.

Enum-like CHECK constraints (stored by SQL Server as `([Status]='B' OR [Status]='A')`) become each column's
allowed values.

## Consequences

- The model sees `sales.Orders.SalesRepId -> hr.EmployeeDirectory.EmployeeId`, and the view's self-reference
  `ManagerId -> hr.EmployeeDirectory.EmployeeId`, without ever seeing `hr.Employees`.
- The API identity needs no data access at all.
- `sys.dm_exec_describe_first_result_set_for_object` looked right but only describes procedures and triggers
  (for a view it returns a single null row) — caught by an integration test.
- Discovery results are cached (default 10 minutes).
