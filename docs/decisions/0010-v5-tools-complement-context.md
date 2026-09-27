# 0010 — V5: tools complement context; the harness verifies; V4 stays the default

- Status: accepted
- Date: 2026-09-27

## Context

V5 adds tool calling. Facts established before and during the build:

- `qwen2.5-coder:7b` does not emit structured tool calls through Ollama (it writes JSON into its text and
  invented a tool name in the probe). The project owner chose `qwen2.5:7b` for V5; all versions were also
  benchmarked on it for a like-for-like comparison.
- On `qwen2.5:7b`, a pure agent (table names only, everything via tools) scored 47% / 42% (development /
  held-out) vs V4's 88% / 75% with the same model.
- With V4's full context plus tools, the model called **no tools** on 22 of 22 sampled questions.
  `ChatToolMode.RequireAny` had no effect: Ollama has no required-tool option.

## Decision

- V5's default is **full context + tools** (`Agent:V5Context = Full`): V4's prompt with tool guidance appended.
  The pure agent stays available (`Minimal`) to keep the result reproducible.
- **Harness self-check** (`Agent:V5SelfCheck = true`): after the answer passes the V3 gate, the agent runs it
  through `run_query` itself, shows the model the first rows and asks it to confirm or correct. One check per
  question; a correction goes through the gate without a second preview.
- Tools are safe by construction: `run_query` applies the full V3 gate before executing; `sample_values` builds
  its SQL from schema names only (bracket-quoted); everything runs as `agent_reader`; results are capped
  (20 rows, 4,000 characters); tools return guidance instead of throwing.
- Refusal rules extended: a refusal with no investigation, or right after a fixable failed test query, gets one
  push back, unless a NOT TRACKED glossary entry or a security rejection justifies it.
- **`Agent:DefaultVersion` stays V4.** The benchmark reports average tool calls per version.

## Consequences

- Measured (two runs): V5 97% development / 75% held-out, 2.4 s average; V4 100% / 75%, faster.
- The self-check does not catch semantic errors a human would: shown a column of `1`s for "how many active
  customers?", the model confirmed. Self-verification by a 7B model is unreliable.
- V5's tools and checks are model-independent; it is the version expected to benefit most from a stronger model.
- The UI shows every tool call (arguments, result, time); the API returns them in `toolCalls`.
