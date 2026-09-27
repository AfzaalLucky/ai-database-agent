# 0008 — A versioned business glossary, retrieved with hybrid (term + vector) search

- Status: accepted
- Date: 2026-09-27

## Context

V3's remaining errors were business definitions, not SQL ([0007](0007-execution-accuracy-evaluation.md)).
The project plan puts Qdrant and Ollama in the stack. Qdrant and `nomic-embed-text` were already installed;
the machine's existing Qdrant containers belonged to other projects, so the project owner chose a dedicated
container.

## Decision

- **Knowledge as code**: `knowledge/foundrytrade.json` (definitions with aliases, not-tracked notes, verified
  examples), reviewed and versioned like source. Integration tests require every SQL snippet to pass the V3
  validator and run as `agent_reader`, and forbid benchmark questions inside it.
- **Store**: Qdrant `v1.18.3` via `docker-compose.yml` (own container and volume, ports bound to 127.0.0.1),
  .NET client `Qdrant.Client` 1.18.1 (matching the server's minor version), gRPC.
- **Embeddings**: `nomic-embed-text` through Microsoft.Extensions.AI `IEmbeddingGenerator` (OllamaSharp),
  registered as a *keyed* service so it can't be confused with the chat client; `search_document:` /
  `search_query:` prefixes as the model expects.
- **Indexing**: idempotent and incremental — each point stores its entry's content hash; only new or changed
  entries are embedded, removed ones deleted; the collection is recreated if the embedding size changes. Runs in
  the background at start-up and lazily on the first V4 question; the benchmark re-indexes before measuring.
- **Retrieval**: hybrid. Literal title/alias matches (whole words, case-insensitive, longest term first, ≤ 4)
  come first; cosine search (≥ 0.55) fills up to six definitions. Examples by cosine only, ≥ 0.80, at most two.

## Why hybrid

Measured on probe questions, dense search alone found 3/6 relevant definitions: short texts compress
similarity scores into 0.52–0.77, so "earn" never reached the salary note and "last month" ranked the
calendar-period definition sixth. Aliases improved the embeddings only partly. Literal term matching is
precise exactly where dense search is weak, and it is explainable. Recall on 12 paraphrases written after the
aliases were final: 10/12.

## Consequences

- Held-out benchmark: V4 75% vs V3 50%, reproduced over two runs.
- The UI shows every retrieved entry with its reason (matched term or similarity).
- Qdrant is optional at run time: V1–V3 work without it; `/health` reports `knowledge` as Degraded and V4
  requests fail clearly.
- With 40 entries the whole glossary would fit in a prompt; retrieval is built for the glossary to grow
  (per-table notes, more examples, multiple databases) without the prompt growing with it.
- Lexical matching has no notion of negation ("excluding returned orders" matches *return rate*); the cost
  is an extra, irrelevant definition in the prompt.
