# 0007 — Measure versions by execution accuracy, and don't tune the prompt to the benchmark

- Status: accepted
- Date: 2026-09-27

## Context

Each version needs an objective score, and future changes need a regression check. Comparing SQL text is
useless (many correct queries per question). The first benchmark run left V3 with four wrong answers, all of
them missing business definitions — it would be easy to add a prompt rule for each.

## Decision

- Score by **execution accuracy**: run the gold SQL and the agent's SQL, compare results. Gold queries return
  only the columns that define correctness; the agent's result must contain a distinct matching column for
  each (extra columns and names ignored), with numeric tolerance for rounding and optional sign-insensitivity
  (see [eval/README](../../eval/README.md)).
- Report **silently wrong** separately from **failed**: a wrong answer that looks right is worse than a
  visible error.
- Report **unsafe SQL executed** for V1/V2 using V3's validator as the judge.
- Record judgement calls in the question set (`notes`), e.g. "spent the most" means the revenue rule.
- **Do not add prompt rules targeted at failing benchmark questions.** The four V3 misses (calendar
  "last month", average order value per order, category-tree roll-up, a hallucinated "conversion rate") are
  business definitions; they belong in a retrieved glossary (V4), which must then improve them without
  having been fitted to them.

## Consequences

- Baseline (`qwen2.5-coder:7b`): V1 53%, V2 81%, V3 88%; V3 0 failed, 0 unsafe.
- One run at temperature 0; run-to-run variance hasn't been measured yet.
- Few-shot examples must never appear in the question set.
