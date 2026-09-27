# 0002 — Hide sensitive columns behind a view, not a column-level DENY

- Status: accepted
- Date: 2026-09-26

## Context

`hr.Employees.Salary` is confidential. The agent should still answer staff questions (head counts,
managers, departments). The first implementation granted SELECT on the other `hr.Employees` columns and
`DENY SELECT` on `Salary`.

Testing showed that with a column-level DENY in place, a plain `SELECT COUNT(*) FROM hr.Employees` fails with
error **230** — so "how many employees do we have?" breaks, and the model has no way to know why.

## Decision

Deny the base table entirely and expose `hr.EmployeeDirectory`, a view with every column except `Salary`,
with its own `MS_Description`. `agent_readers` gets SELECT on the view and DENY on `hr.Employees`.

## Consequences

- Ordinary questions work (`COUNT(*)` on the view), and the sensitive column exists neither in the agent's
  permissions nor in the schema shown to the model.
- The model loses the base table's foreign keys and column descriptions unless discovery carries them
  through the view — handled by [0003](0003-two-pass-schema-discovery.md).
- Questions about salaries end as `NotAnswerable` in V3: the model may still *guess* a `Salary` column, the
  validator reports that the view has no such column (listing the real ones), and the model declines.
- Pattern for any future sensitive data: a purpose-built view per audience, not column-level denies.
