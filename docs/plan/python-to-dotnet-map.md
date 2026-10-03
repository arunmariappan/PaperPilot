# Python → .NET file map

Every file in the Python repo that holds behaviour, and where that behaviour goes in PaperPilot. Paths on the right are relative to the PaperPilot repo root. "Dropped" means there's nothing to port, and the reason is given.

## Application wiring

| Python | PaperPilot | Notes |
|---|---|---|
| `src/main.py` (`lifespan`, router includes) | `src/PaperPilot.Api/Program.cs` | Startup checks (OpenSearch health, index + pipeline setup) move to `SearchIndexInitializer : IHostedService`. Telegram start/stop runs as a `BackgroundService`. |
| `src/dependencies.py` (`*Dep` aliases) | `src/PaperPilot.Infrastructure/DependencyInjection.cs` (`AddPaperPilotInfrastructure()`), `src/PaperPilot.Rag/DependencyInjection.cs` | Endpoints take services as parameters, and DI resolves them. |
| `src/config.py` | `src/PaperPilot.Core/Options/*.cs` | One options class per section. See README §5. |
| `src/exceptions.py` | `src/PaperPilot.Core/Exceptions/*.cs` | Only the ones that are caught somewhere: `ArxivApiException`, `PdfValidationException`, `PdfParsingException`, `SearchUnavailableException`. |
| `src/middlewares.py` | Dropped | ASP.NET request logging and OTel HTTP instrumentation already cover it. |
| `src/database.py`, `src/db/factory.py`, `src/db/interfaces/*` | `src/PaperPilot.Infrastructure/Persistence/PaperPilotDbContext.cs`, `src/PaperPilot.MigrationService/` | Connection comes from Aspire `AddNpgsqlDbContext<PaperPilotDbContext>("papers")`. |
| `src/schemas/database/config.py` | Dropped | The Aspire connection string replaces it. |
| `Dockerfile`, `compose.yml`, `Makefile` | `src/PaperPilot.AppHost/Program.cs`, `README.md` | `make start` becomes `aspire run`. |

## API surface

| Python | PaperPilot | Notes |
|---|---|---|
| `src/routers/ping.py` | `src/PaperPilot.Api/Endpoints/HealthEndpoints.cs` | Same JSON shape. Checks: database, opensearch, ollama. |
| `src/routers/hybrid_search.py` | `src/PaperPilot.Api/Endpoints/SearchEndpoints.cs` | `POST /api/v1/hybrid-search/` |
| `src/routers/ask.py` | `src/PaperPilot.Api/Endpoints/AskEndpoints.cs` → `src/PaperPilot.Rag/RagService.cs` | Endpoints stay thin. Retrieval, prompt, generation and caching live in `RagService`, which Telegram reuses. |
| `src/routers/agentic_ask.py` | `src/PaperPilot.Api/Endpoints/AgenticAskEndpoints.cs`, `FeedbackEndpoints.cs` | |
| `src/schemas/api/ask.py`, `search.py`, `health.py` | `src/PaperPilot.Core/Contracts/*.cs` | `record` types with DataAnnotations (`[Range(1,10)]`, `[StringLength(1000, MinimumLength = 1)]`). |
| (none) | `src/PaperPilot.Api/Endpoints/ModelsEndpoints.cs` | N1 |

## Search and embeddings

| Python | PaperPilot | Notes |
|---|---|---|
| `src/services/opensearch/client.py` | `src/PaperPilot.Infrastructure/Search/OpenSearchClient.cs` (typed `HttpClient`) | Methods: `HealthAsync`, `GetIndexStatsAsync`, `EnsureIndexAsync`, `EnsureRrfPipelineAsync`, `SearchAsync` (unified), `BulkIndexChunksAsync`, `DeletePaperChunksAsync`, `GetChunksByPaperAsync`. |
| `src/services/opensearch/query_builder.py` | `src/PaperPilot.Core/Search/QueryBuilder.cs` | Pure function → `JsonObject`. Has golden tests against Python output. |
| `src/services/opensearch/index_config_hybrid.py` | `src/PaperPilot.Infrastructure/Search/IndexDefinitions.cs` (+ `chunks-index.json` as an embedded resource) | Dimension and space type are filled in from options (B11). |
| `src/services/opensearch/factory.py` | Dropped | DI singleton. |
| `src/services/embeddings/jina_client.py`, `src/schemas/embeddings/jina.py` | `src/PaperPilot.Infrastructure/Embeddings/JinaEmbeddingService.cs`, `JinaModels.cs` | Implements `IEmbeddingService { EmbedQueryAsync, EmbedPassagesAsync }`. Two named HttpClients with different 429 retry budgets (2 vs 6). |

## LLM, prompts, cache, tracing

| Python | PaperPilot | Notes |
|---|---|---|
| `src/services/ollama/client.py` | `OllamaSharp` `IChatClient` (registered in `src/PaperPilot.Infrastructure/Llm/LlmRegistration.cs`), `ChatOptionsFactory.cs`, `OllamaModelCatalog.cs`; the health check is in `src/PaperPilot.Infrastructure/HealthChecks.cs` | The hand-written usage-metadata parsing goes away: `ChatResponse.Usage` plus OTel GenAI attributes cover it. |
| `src/services/ollama/prompts.py`, `prompts/rag_system.txt` | `src/PaperPilot.Rag/Prompts/RagPromptBuilder.cs`, `Prompts/rag_system.txt` (embedded) | C3, C4 |
| `src/schemas/ollama.py` | Dropped | C4 |
| `src/services/cache/client.py` | `src/PaperPilot.Infrastructure/Caching/AnswerCache.cs` + `src/PaperPilot.Core/Caching/CacheKey.cs` | C6 |
| `src/services/langfuse/client.py`, `tracer.py` | `src/PaperPilot.ServiceDefaults/LangfuseExporter.cs` (Langfuse OTLP exporter), `src/PaperPilot.Rag/Telemetry/RagTelemetry.cs`, `src/PaperPilot.Infrastructure/Observability/LangfuseScoresClient.cs` | Spans become `Activity` objects. Feedback goes to Langfuse `POST /api/public/scores`. |

## Agentic RAG

| Python | PaperPilot | Notes |
|---|---|---|
| `src/services/agents/agentic_rag.py` | `src/PaperPilot.Rag/Agentic/AgenticRagService.cs`, `AgenticRagWorkflow.cs` | The graph is built once with `WorkflowBuilder` and run with `InProcessExecution.Concurrent`. |
| `src/services/agents/state.py` | `src/PaperPilot.Rag/Agentic/AgentRunState.cs` | An immutable record passed between executors. |
| `src/services/agents/context.py`, `config.py`, `factory.py` | `src/PaperPilot.Core/Options/AgenticRagOptions.cs` + DI | Per-request values (model, top_k, use_hybrid) go into the initial state (B2). |
| `src/services/agents/models.py` | `src/PaperPilot.Rag/Agentic/AgentModels.cs` | `GuardrailScoring`, `GradeDocuments`, `QueryRewriteOutput`, `GradingResult`. `SourceItem`, `ToolArtefact`, `RoutingDecision` and `ReasoningStep` are dropped: sources are URLs (B1) and routing lives in `AgentRouting`. |
| `src/services/agents/prompts.py` | `src/PaperPilot.Rag/Prompts/{guardrail,grade_documents,rewrite,generate_answer}.txt` (embedded, verbatim) + `Agentic/AgentPrompts.cs` | Only the prompts that are actually used: `GUARDRAIL`, `GRADE_DOCUMENTS`, `REWRITE`, `GENERATE_ANSWER`. `SYSTEM_MESSAGE`, `DECISION_PROMPT` and `DIRECT_RESPONSE_PROMPT` are dropped because nothing uses them. |
| `src/services/agents/tools.py` | `src/PaperPilot.Rag/Retrieval/PaperRetriever.cs` | Shared by classic and agentic RAG (B3). |
| `src/services/agents/nodes/*.py` | `src/PaperPilot.Rag/Agentic/Executors/*Executor.cs` | One executor per node, plus `MaxAttemptsExecutor` (B5) and `SearchUnavailableExecutor` (B6). |
| `src/services/agents/nodes/utils.py` | Dropped | Message-list helpers aren't needed once state is typed. |

## Ingestion

| Python | PaperPilot | Notes |
|---|---|---|
| `src/services/arxiv/client.py` | `src/PaperPilot.Infrastructure/Arxiv/ArxivClient.cs`, `ArxivAtomParser.cs`, `ArxivQueryBuilder.cs` | URL encoding must match Python exactly (golden test). |
| `src/schemas/arxiv/paper.py` | `src/PaperPilot.Core/Domain/ArxivPaper.cs`, `Paper.cs` | |
| `src/services/pdf_parser/docling.py`, `parser.py`, `factory.py` | `src/PaperPilot.Infrastructure/Pdf/DoclingServeClient.cs`, `PdfValidator.cs` (PdfPig), `PdfParser.cs` (with `DoclingSectionExtractor`) | The section algorithm is a 1:1 port over `json_content.texts[]`. |
| `src/schemas/pdf_parser/models.py` | `src/PaperPilot.Core/Domain/PdfContent.cs` | Figures and tables are dropped; Python always left them empty. |
| `src/services/metadata_fetcher.py` | `src/PaperPilot.Ingestion/PaperFetchService.cs` | `SemaphoreSlim` for download (5) and parse (1) concurrency. |
| `src/services/indexing/text_chunker.py`, `src/schemas/indexing/models.py` | `src/PaperPilot.Core/Indexing/TextChunker.cs` (with `TextChunk`), `src/PaperPilot.Core/Text/PythonText.cs` | Golden tests, with B9, B10, B11 and B31 applied. |
| `src/services/indexing/hybrid_indexer.py`, `factory.py` | `src/PaperPilot.Ingestion/HybridIndexer.cs` | |
| `src/repositories/paper.py`, `src/models/paper.py` | `src/PaperPilot.Infrastructure/Persistence/PaperRepository.cs`, `Configurations/PaperConfiguration.cs` | |
| `airflow/dags/arxiv_paper_ingestion.py` + `arxiv_ingestion/{setup,fetching,indexing,reporting}.py` | `src/PaperPilot.Ingestion/DailyIngestionJob.cs` (steps), `IngestionRules.cs` (window, failure rule, cleanup), `src/PaperPilot.Worker/Program.cs` (recurring job registration) | C9 |
| `airflow/Dockerfile`, `entrypoint.sh`, `requirements-airflow.txt` | Dropped | No second dependency set and no pip/sqlalchemy pin to keep in sync. |
| `airflow/dags/hello_world_dag.py` | Dropped | |

## Bot and UI

| Python | PaperPilot | Notes |
|---|---|---|
| `src/services/telegram/bot.py`, `factory.py` | `src/PaperPilot.Api/Telegram/TelegramBotService.cs`, `TelegramUpdateHandler.cs`, `TelegramMessageFormatter.cs`, `TelegramSender.cs`, `TelegramRegistration.cs` | C12, B19, N5 |
| `src/gradio_app.py`, `gradio_launcher.py` | `src/PaperPilot.Web/Components/Pages/Chat.razor` and `Components/Chat/*` (+ `Api/PaperPilotApiClient.cs`) | B35, C15, N6 |

## Tests

| Python | PaperPilot |
|---|---|
| `tests/unit/services/test_opensearch_query_builder.py` | `tests/PaperPilot.UnitTests/Search/QueryBuilderTests.cs` (+ parity fixtures) |
| `tests/unit/services/test_arxiv_client.py` | `tests/PaperPilot.UnitTests/Arxiv/*Tests.cs` |
| `tests/unit/services/test_pdf_parser.py` | `tests/PaperPilot.UnitTests/Pdf/PdfParsingTests.cs` |
| `tests/unit/services/test_metadata_fetcher.py` | `tests/PaperPilot.IntegrationTests/Ingestion/IngestionJobTests.cs` |
| `tests/unit/services/test_telegram.py` | `tests/PaperPilot.UnitTests/Telegram/*Tests.cs` |
| `tests/unit/services/agents/*` (stale: fixtures that don't exist) | `tests/PaperPilot.UnitTests/Rag/Agentic/*Tests.cs`, written fresh with a scripted `IChatClient` |
| `tests/unit/test_config.py`, `schemas/test_search.py` | `tests/PaperPilot.UnitTests/Options/*`, `Contracts/*` |
| `tests/api/routers/*` | `tests/PaperPilot.IntegrationTests/Api/*` (exact status codes, B20) |
| `tests/integration/test_services.py` | `tests/PaperPilot.IntegrationTests/Infrastructure/*` (Testcontainers) |
