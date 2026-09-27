# V5 — The agent: tools, and what a 7B model actually does with them

> Article draft · AI Database Agent series · Part 5

## The idea

Give the model tools and let it work like an analyst: look up definitions, inspect tables, check how values are
spelled, run a draft query, look at the rows, fix, answer. Built on Microsoft.Extensions.AI's
`FunctionInvokingChatClient`; every call is traced into the response and the UI.

| Tool | What it does | Safety |
|---|---|---|
| `list_tables` | tables/views with one-line descriptions | schema as the agent sees it |
| `describe_table` | columns, types, keys, join paths, allowed values | same annotated DDL as V2 |
| `search_glossary` | V4's hybrid retrieval, on demand | read-only |
| `sample_values` | distinct values of a column | names checked against the schema and bracket-quoted by the tool; runs as `agent_reader` |
| `run_query` | runs a draft query, returns 20 rows or the exact problem | full V3 gate (allow-list, schema, cost), then `agent_reader` |

The final answer still passes V3's gate (validate → cost → execute → repair), so V5 can't be less safe than V3.

## Step 0: can the model call tools at all?

`qwen2.5-coder:7b` — the model behind V1–V4 — advertises tool support in Ollama, but asked to use a
`describe_table` tool it wrote `{"name": "GetTableColumns", ...}` into its *text*: no structured call, and a
tool name that doesn't exist. `llama3.2` (3B) produced a proper call but is too weak at SQL. V5 uses
**`qwen2.5:7b`** (same family, general instruct), which calls tools natively. To keep comparisons fair, every
version was also benchmarked on `qwen2.5:7b` (`--model`).

## Three designs, measured

All on `qwen2.5:7b`, development set (32) / held-out set (12), two runs each where noted:

| Design | Development | **Held-out** | Avg time | Tool calls / question |
|---|---|---|---|---|
| V4 (no tools), same model | 88% | 75% | 2.0 s | 0 |
| **V5a — pure agent**: table names only, everything via tools | 47% | 42% | 4.1 s | not recorded |
| **V5b — full context + tools**: V4's prompt, tools to verify | 94% (×2) | 75% (×2) | 2.2 s | **0** (in a 22-question sample) |
| **V5c — V5b + harness self-check** (default) | 97% (×2) | 75% (×2) | 2.4 s | 0.92 (held-out; mostly the harness's own check) |

**V5a — the pure agent — was much worse.** It declined basic questions within a second without calling a
single tool ("Customer count is not tracked"), and when it did explore it assembled weaker queries than V4 gets
handed. A 7B model is not yet a good investigator; the precise, pre-assembled context of V4 is worth more than
the freedom to look things up. (It's kept as `Agent:V5Context = Minimal` so the result is reproducible.)

**V5b — full context plus tools — looked like an improvement until the tool trace was checked.** Sampling 22
benchmark questions: **0 used any tool.** Given enough context, the model simply answers. Its score difference
from V4 is noise, not tools. Requiring a tool call (`ChatToolMode.RequireAny`) changed nothing — Ollama's chat
API has no "tool required" option to pass it to.

**V5c — the harness makes the check itself.** If the model won't test its query, the agent does: after the
answer passes the V3 gate, it runs the query through `run_query` (traced like any tool call), shows the model
the first rows and asks it to confirm or correct — "the right grain? the right number of rows?". Development
set: 97%. Held-out: unchanged at 75%, the same three misses.

## Why self-checking didn't fix the held-out misses

"How many active customers do we have?" — the model wrote `COUNT(*) … GROUP BY c.CustomerId`, i.e. one row per
customer. The self-check showed it a column of `1, 1, 1, …` and "(first 20 rows; more exist)". It answered
**CONFIRMED**. A 7B model rubber-stamps its own output; it can't reliably judge that what it sees contradicts the
question. The other two misses are definitional (integer `AVG` of hours; declining/miscomputing amounts owed)
and produce plausible numbers — nothing for a check to catch.

## What the V5 work did fix

- Safety and guidance in the tools themselves: `run_query` refuses writes and `master` without executing,
  `sample_values` can't be steered into arbitrary SQL, unknown tables/columns come back as guidance.
- Two more refusal rules, now shared by the agent: a refusal **without investigating** gets one push back (look
  first); a refusal **right after a failed test query** gets one push back — unless the glossary said the data
  is NOT TRACKED, or the failure was a security rejection (then declining is right).
- A real leak, found by a V5 integration test: the view description read "Staff directory (hr.Employees without
  confidential pay data)" — naming the hidden table to every prompt. Fixed in the database script.

## Verdict

| | Development | Held-out | Unsafe SQL executed |
|---|---|---|---|
| V4 (API default, `qwen2.5-coder:7b`) | 100% | 75% | 0 |
| V5c (`qwen2.5:7b`) | 97% | 75% | 0 |

V5 matches V4 on questions it was never tuned on, at a slightly higher cost. With a local 7B model the leverage
is in **context** (schema, definitions) and **gates** (validation, least privilege), not in autonomy. V4 stays
the default; V5 is there — and is the version that should improve most with a stronger model, since its tools
and checks are model-independent. That's the next experiment: the same benchmark with a larger local model or a
hosted one, behind the same `IChatClient`.
