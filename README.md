# PaperPilot

An arXiv paper curator built on .NET 10 and Aspire 13. Every weekday it fetches new cs.AI papers from arXiv, parses
their PDFs and indexes them. You can then search them (BM25 plus vectors, fused with reciprocal rank fusion) and ask
questions answered from them by a local Ollama model, either with classic RAG or with an agent that checks the
question is in scope, grades what it finds and rewrites the query when it needs to.

- **API** (`/api/v1`): hybrid search, ask, streaming ask, agentic ask, feedback, models, health.
- **Chat UI** (Blazor): streams answers, or runs the agent and shows its reasoning steps, with sources and feedback.
- **Telegram bot** (optional): questions, `/search` and `/agent` from your phone.
- **Ingestion** (Hangfire): a daily job, plus manual runs and backfills.
- **Tracing**: OpenTelemetry in the Aspire dashboard, and Langfuse (optional) with feedback scores.

How it was designed, and how it differs from the Python system it replaces, is in [docs/](#documentation).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.301 or later; see `global.json`). Trust its HTTPS
  development certificate once, since the Aspire dashboard uses it: `dotnet dev-certs https --trust`.
- **Docker Desktop** with at least 8 GB of memory. PaperPilot uses about 2 GB idle, or 4 GB with Langfuse.
- **[Ollama](https://ollama.com/)** on the host, with the model pulled: `ollama pull qwen3.5:9b` (6.6 GB). A GPU makes
  answers take seconds instead of minutes.
- A **[Jina AI](https://jina.ai/) API key** for embeddings (the free tier is enough).
- Optional: the Aspire CLI (`dotnet tool install -g aspire.cli`) for `aspire run`, `aspire start` and `aspire describe`.

The first start pulls the container images, about 10 GB in total, most of it docling-serve (7.6 GB).

## Quick start

```bash
git clone https://github.com/arunmariappan/PaperPilot.git
cd PaperPilot

# The only required secret. It stays in your user-secrets store, never in the repository.
dotnet user-secrets set Parameters:jina-api-key <your-jina-key> --project src/PaperPilot.AppHost

dotnet run --project src/PaperPilot.AppHost    # or: aspire run
```

Start it from a normal terminal, not from VS Code's integrated terminal: there the AppHost hands project launches to
VS Code, which refuses them, and `api`, `web`, `worker` and `migrations` stay at *FailedToStart*.

The console prints a login link for the **Aspire dashboard** (<https://localhost:17205>). Wait until every resource is
*Running* (`opensearch-dashboards` stays *NotStarted* until you start it there). Then:

1. **Ingest some papers.** The daily job runs on weekdays at 06:00 UTC, and catches up on up to three missed days.
   To fill the index now, run it for a recent weekday:

   ```bash
   curl -X POST "http://localhost:8102/ingestion/run?from=20261001&to=20261001"
   ```

   Or open the Hangfire dashboard (<http://localhost:8102/hangfire>) → *Recurring jobs* → `arxiv-daily-ingestion` →
   *Trigger now*. A run takes about 6 minutes for 15 papers, mostly PDF parsing. Check it with
   `curl http://localhost:8102/ingestion/runs`.
2. **Ask a question** in the chat UI at <http://localhost:8101>, or with `curl` (below).

## URLs

| What | URL | Notes |
|---|---|---|
| Aspire dashboard | <https://localhost:17205> | resources, logs, traces; login link in the console |
| API docs | <http://localhost:8100/docs> | Scalar; OpenAPI at `/openapi/v1.json` |
| Chat UI | <http://localhost:8101> | |
| Hangfire | <http://localhost:8102/hangfire> | ingestion jobs |
| OpenSearch | <http://localhost:9210> | index `arxiv-papers-chunks` |
| OpenSearch Dashboards | <http://localhost:5610> | start it from the Aspire dashboard first |
| docling-serve | <http://localhost:5011/ui> | PDF parsing; API docs at `/docs` |
| Langfuse | <http://localhost:3010> | only with Langfuse on (below); user `admin@example.com` |

## Example calls

```bash
curl http://localhost:8100/api/v1/health

# Hybrid search (use_hybrid false = BM25 only). A blank query with latest_papers lists the newest papers.
curl -X POST http://localhost:8100/api/v1/hybrid-search/ -H "Content-Type: application/json" \
  -d '{"query": "diffusion language models", "size": 5, "use_hybrid": true, "categories": ["cs.CL"]}'

# Classic RAG: retrieve top_k chunks, answer from them (answers are cached for 6 hours).
curl -X POST http://localhost:8100/api/v1/ask -H "Content-Type: application/json" \
  -d '{"query": "What are transformer architectures?", "top_k": 3, "use_hybrid": true}'

# The same, streamed as server-sent events.
curl -N -X POST http://localhost:8100/api/v1/stream -H "Content-Type: application/json" \
  -d '{"query": "What are transformer architectures?"}'

# Agentic RAG: scope check, retrieval, grading and query rewriting. Takes a few LLM calls.
curl -X POST http://localhost:8100/api/v1/ask-agentic -H "Content-Type: application/json" \
  -d '{"query": "How does BERT work?"}'

# Rate an agentic answer by its trace_id (needs Langfuse on).
curl -X POST http://localhost:8100/api/v1/feedback -H "Content-Type: application/json" \
  -d '{"trace_id": "<trace_id from ask-agentic>", "score": 1, "comment": "Useful"}'

curl http://localhost:8100/api/v1/models
```

## Optional features

All of these use the same user-secrets store as the Jina key
(`dotnet user-secrets set <name> <value> --project src/PaperPilot.AppHost`):

| Setting | Effect |
|---|---|
| `Parameters:telegram-bot-token` | Runs the Telegram bot inside the API (create a bot with @BotFather). Commands: `/start`, `/help`, `/search <keywords>`, `/agent <question>`; anything else is a question. Only one process can poll a bot token at a time. |
| `Langfuse:Enabled` = `true` | Starts Langfuse (web, worker, ClickHouse, MinIO, Redis; about 2 GB more memory) and sends traces to it. Its keys and admin password are generated on the first start and saved to the same store. `/feedback` needs it. |
| `Ollama:UseContainer` = `true` | Runs Ollama in Docker (CPU only) instead of using the host's. |
| `Ollama:Model` | Another model than `qwen3.5:9b` (it must be pulled). |

## Development

```bash
dotnet build                                          # warnings are errors
dotnet test --project tests/PaperPilot.UnitTests      # no Docker needed
dotnet test --project tests/PaperPilot.IntegrationTests   # Docker: Testcontainers for Postgres, Redis, OpenSearch
dotnet format --verify-no-changes
```

CI (`.github/workflows/ci.yml`) builds, checks formatting and runs both test projects with coverage. A manual
*Smoke test* workflow boots the whole AppHost and checks `/api/v1/health`. More commands and the pitfalls found along
the way are in [CLAUDE.md](CLAUDE.md).

| Project | What it is |
|---|---|
| `src/PaperPilot.AppHost` | Aspire orchestration: containers, projects, secrets, ports |
| `src/PaperPilot.Api` | ASP.NET Core API and the Telegram bot |
| `src/PaperPilot.Worker` | Hangfire server, dashboard and ingestion endpoints |
| `src/PaperPilot.Web` | Blazor chat UI (calls the API over HTTP only) |
| `src/PaperPilot.MigrationService` | applies EF Core migrations, then exits |
| `src/PaperPilot.Rag` | classic RAG and the agentic workflow (Microsoft Agent Framework) |
| `src/PaperPilot.Ingestion` | arXiv fetching, PDF parsing, chunking, indexing |
| `src/PaperPilot.Infrastructure` | Postgres, Redis, OpenSearch, Jina, Ollama, Langfuse clients |
| `src/PaperPilot.Core` | domain types, contracts, options |
| `tools/` | `seed-opensearch.cs` (copy an index), `parity-check.cs` (compare with the Python stack) |

## Documentation

- [docs/decisions.md](docs/decisions.md): the main design decisions and why.
- [docs/parity-report.md](docs/parity-report.md): PaperPilot against the Python stack on a fixed query set.
- [docs/plan/](docs/plan/README.md): the phase-by-phase plan and what was built in each phase.
- [docs/plan/behaviour-changes.md](docs/plan/behaviour-changes.md): every intended difference from the Python version.

## License

[Apache-2.0](LICENSE)
