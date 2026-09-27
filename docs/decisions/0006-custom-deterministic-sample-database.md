# 0006 — A purpose-built, deterministic sample database (FoundryTrade)

- Status: accepted
- Date: 2026-09-26

## Context

The agent needs business data to query. Options: Microsoft's AdventureWorksLT or WideWorldImporters, the
project owner's own data, or a purpose-built database.

## Decision

A custom database designed for Text-to-SQL (chosen by the project owner): four schemas, self-referencing and
tree relationships, enums as CHECK constraints, a computed column, refunds as negative payments, a
confidential column, and an `MS_Description` on every table and column. Seed values come from a SHA-256 of
(row number, salt), so every build produces identical rows (fingerprint `622378009` in `05-verify.sql`).
Email addresses use reserved `example` domains (RFC 2606).

## Consequences

- Article examples and benchmark answers are reproducible on any machine.
- The data has the realistic traps that Text-to-SQL gets wrong (revenue excludes cancelled/returned orders;
  averages per order vs per line; category roll-ups; relative dates), which is what the benchmark measures.
- Business rules the schema can't express are written once as `Agent:DomainNotes` and in table descriptions.
- Integrity checks (orders after customer creation, lines after product launch, no shipment after the data
  cut-off, sales reps hired before their orders) all return 0.
