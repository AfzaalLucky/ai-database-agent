# 0005 — Microsoft.Extensions.AI `IChatClient`, with a local Ollama model by default

- Status: accepted
- Date: 2026-09-26

## Context

The Foundry brand is model-independent, and the project should run for free on a developer machine. V5 will
need tool calling.

## Decision

- `Core` depends only on `IChatClient` from `Microsoft.Extensions.AI.Abstractions` — the standard .NET LLM
  abstraction, which also provides function-invocation middleware for V5.
- `Infrastructure` registers OllamaSharp's `OllamaApiClient` through `AddChatClient(...)` with two pieces of
  middleware: one that sets Ollama's `num_ctx` on every request (Ollama defaults to 2,048 tokens; the V2
  prompt is about 2,100), and request logging.
- Default model **`qwen2.5-coder:7b`** (chosen by the project owner over the already-installed `llama3.2` 3B,
  which is too small for reliable SQL). Temperature 0.
- Health check: Ollama reachable and the model pulled.

## Consequences

- Switching to OpenAI, Azure OpenAI or another provider is a registration change in `Infrastructure`
  (`Llm:Provider` currently accepts only `Ollama`, and says so at start-up).
- Measured on the local model: 0.5–3 s per answer after the model is loaded; the first request of a session
  takes longer while Ollama loads it.
- The benchmark records the model name in every report, so results from different models are comparable.
