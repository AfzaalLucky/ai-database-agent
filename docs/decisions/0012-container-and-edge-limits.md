# 0012 — A chiseled container for the API; limits for running behind a proxy

- Status: accepted
- Date: 2026-09-27

## Context

The Foundry plan deploys to a VPS the project owner controls (Nginx in front, containers or systemd behind,
GitHub Actions). Until now the API only ran with `dotnet run`, used Windows authentication to SQL Server, and
rate-limited `/api/ask` by `RemoteIpAddress` — which, behind Nginx, is the proxy's address for every visitor.

## Decision

- **Container image** (`Dockerfile`): multi-stage; restore from project files first (cached layer), publish
  Release, run on `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` — no shell or package manager,
  non-root user, ICU and time zones included for SqlClient. Listens on 8080. The knowledge file ships in the
  image; Development settings do not (`.dockerignore`).
- **Configuration only through the environment**: SQL Server connection strings (SQL logins, since a Linux
  container has no Windows authentication), the Ollama endpoint, the Qdrant host, OTLP. `.env.example` shows
  the shape; real values stay in `.env` (git-ignored) or the host's secret store.
- **docker-compose profiles**: the default is still Qdrant only; `observability` adds the Aspire dashboard;
  `app` adds the API container. Every port is bound to `127.0.0.1`: on the VPS only Nginx is public.
- **Forwarded headers, explicitly**: when `ReverseProxy:KnownProxies` lists the proxy's address, the API
  trusts `X-Forwarded-For`/`-Proto` from it (one hop) and nothing else, so the per-client rate limit sees the
  real client and a client cannot choose its own partition by sending the header.
- **Limits at the edge of the API**: per-client fixed window (`RateLimit:AskPerMinute`, as before) plus a
  global concurrency limit on `/api/ask` (`RateLimit:AskConcurrency` = 4, `AskQueue` = 16; then 429) because a
  local model serves requests one after another; request bodies are capped at 64 KB.
- **CI** (GitHub Actions): restore, Release build (warnings are errors, so analyzers and style are enforced),
  unit tests, and a container build. The integration suite needs SQL Server, Ollama and Qdrant and runs locally.

## Consequences

- The image is 267 MB. Verified: it starts as a non-root user, serves the UI, reaches Ollama and Qdrant, and
  reports the database check correctly (a rejected login shows as `Unhealthy` with SQL Server's message).
- Not yet verified: a full question answered from inside the container, which needs a SQL login on the server
  (the development machine uses Windows authentication only). That is the first step of the VPS deployment.
- The model is outside the container. On a CPU-only VPS a 7B model is slow; the deployment decision (GPU host,
  smaller model, or a hosted model behind the same `IChatClient`) is still open.
