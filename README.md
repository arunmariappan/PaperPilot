# PaperPilot

An arXiv CS.AI paper curator built on .NET 10 and Aspire: daily ingestion of new papers, hybrid search
(BM25 + vectors with reciprocal rank fusion), classic RAG and agentic RAG over a local Ollama model.

The design, decisions and roadmap are in [docs/plan/](docs/plan/README.md).

> **Status:** phase 1 (domain and persistence). The infrastructure starts and the `papers` table is migrated; the API has no endpoints yet.

## Prerequisites

- .NET 10 SDK
- Docker Desktop (8 GB is enough without Langfuse)
- Ollama on the host with `qwen3.5:9b` pulled
- A [Jina AI](https://jina.ai/) API key for embeddings

## Run it

```bash
dotnet user-secrets set Parameters:jina-api-key <your-key> --project src/PaperPilot.AppHost
dotnet run --project src/PaperPilot.AppHost      # or: aspire run
```

The Aspire dashboard opens at <https://localhost:17205> (the login link is printed in the console).

| What | URL |
|---|---|
| API | <http://localhost:8100> |
| Web UI | <http://localhost:8101> |
| Worker | <http://localhost:8102> |
| OpenSearch | <http://localhost:9210> |
| docling-serve | <http://localhost:5011/docs> |

Optional: set `Parameters:telegram-bot-token` for the Telegram bot, or `Langfuse:Enabled` to `true` for the
Langfuse tracing stack (<http://localhost:3010>), with the same `dotnet user-secrets set` command.

## License

[Apache-2.0](LICENSE)
