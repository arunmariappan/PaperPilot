# CLAUDE.md

PaperPilot is a .NET 10 / Aspire 13 arXiv paper curator (hybrid search, classic RAG, agentic RAG). The plan is in
[docs/plan/](docs/plan/README.md): decisions, phases, the Python → .NET file map, and every intended behaviour change
from the Python version (`B`/`C`/`N` IDs).

## Commands

```bash
dotnet build                                      # warnings are errors
dotnet test --project tests/PaperPilot.UnitTests  # Microsoft.Testing.Platform (global.json), not VSTest
dotnet format --verify-no-changes                 # CI runs this
dotnet test --project tests/PaperPilot.IntegrationTests   # needs Docker (Testcontainers, postgres:18.3)
dotnet run tools/seed-opensearch.cs -- --source http://localhost:9200 --target http://localhost:9210
                                                  # copy the Python stack's chunk index (start PaperPilot first)
dotnet tool restore                               # dotnet-ef, pinned in dotnet-tools.json
dotnet ef migrations add <Name> --project src/PaperPilot.Infrastructure   --startup-project src/PaperPilot.MigrationService --output-dir Persistence/Migrations
dotnet run --project src/PaperPilot.AppHost       # whole stack in the foreground; or `aspire run`
aspire start --apphost src/PaperPilot.AppHost     # in the background (PowerShell); `aspire describe`, `aspire logs <resource>`
aspire stop --apphost src/PaperPilot.AppHost
aspire otel traces api --apphost src/PaperPilot.AppHost --trace-id <id> --format Json   # a trace from the dashboard
curl -X POST "localhost:8102/ingestion/run?from=20261001&to=20261001"   # manual ingestion run / backfill (N3)
curl localhost:8102/ingestion/runs                                       # recent runs (N2)
curl -X POST localhost:8100/api/v1/ask-agentic -H 'Content-Type: application/json' -d '{"query":"What are transformer architectures?"}'
                                                  # agentic RAG; minutes on a local GPU (3-4 LLM calls)
dotnet run tests/fixtures/docling/make-sample-paper.cs                   # regenerate the synthetic Docling fixture PDF
```

Stop the AppHost with Ctrl+C or `aspire stop`. Killing the process leaves session containers (docling, Langfuse)
running, and the next start then fails on their pinned ports; remove them with `docker rm -f`.

- Required secret (Api and Worker wait until it's set):
  `dotnet user-secrets set Parameters:jina-api-key <value> --project src/PaperPilot.AppHost`
- Optional: `Parameters:telegram-bot-token`, `Langfuse:Enabled=true` (same command). Langfuse keys and passwords are
  generated on first run and saved to the same user-secrets store.
- Stop the Python stack first (`docker compose stop` in the Python repo): Docker Desktop has 8 GB.

## Architecture

```
Core  ←  Infrastructure  ←  Rag, Ingestion  ←  Api, Worker, MigrationService
                                            ←  Web (calls Api over HTTP only; references Core for contracts)
ServiceDefaults ← every host
```

| Resource | Port | Notes |
|---|---|---|
| Aspire dashboard | 17205 | login URL is printed at startup |
| `api` | 8100 | `/api/v1/*`, API docs at `/docs`, OpenAPI at `/openapi/v1.json` |
| `web` | 8101 | Blazor UI |
| `worker` | 8102 | Hangfire dashboard at `/hangfire`, `POST /ingestion/run`, `GET /ingestion/runs` |
| `postgres` | 5442 | persistent container, volume `paperpilot-postgres-data` |
| `redis` | 6390 (TLS) | see gotchas |
| `opensearch` | 9210 | persistent, 512 MB heap, volume `paperpilot-opensearch-data` |
| `opensearch-dashboards` | 5610 | explicit start from the Aspire dashboard |
| `docling` | 5011 | `/docs`, `/ui` |
| `langfuse-web` | 3010 | only with `Langfuse:Enabled=true` |
| Ollama | 11434 | Windows host install; `Ollama:UseContainer=true` for a container |

## Gotchas

- **Resilience (plan R1).** `AddServiceDefaults` puts the standard resilience handler on every `HttpClient`
  (10 s per attempt, ~30 s total). Clients whose calls take minutes must use
  `AddLongRunningResilienceHandler(name, totalTimeout, pipeline => …)` from `PaperPilot.Infrastructure.Http`: it removes the
  standard handler (`RemoveAllResilienceHandlers`, experimental `EXTEXP0001`), sets `HttpClient.Timeout` to
  infinite (its 100 s default applies on top of any pipeline), and adds a pipeline whose outermost strategy is the
  total timeout. Strategies added in `configure` run inside it. `ServiceDefaults/LongRunningResilienceTests` proves
  it against a 12 s WireMock endpoint.
- **AppHost uses NuGet DCP and dashboard.** The template sets `AspireUseCliBundle=true`, which needs an Aspire CLI on
  `PATH` at build time (CI has none). It's `false` here, with `ASPIRE010` suppressed. The Aspire CLI is a global
  .NET tool on this machine, on PowerShell's `PATH` but not Git Bash's.
- **Redis is TLS.** Aspire 13.6's `AddRedis` serves `rediss://` on the pinned port 6390 and plain TCP on a second,
  random port. `WithReference(redis)` hands clients a TLS connection string, so this only matters for manual tools.
- **Postgres 18.** Aspire 13.6 runs `postgres:18.x`. To inspect it without handling the password:
  `docker exec <postgres-container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d papers -c "\d papers"'`.
- **Persistence.** `AddPaperPilotDatabase()` registers a pooled `PaperPilotDbContext` (snake_case naming, timestamp
  interceptor) and Aspire's enrichment (retries, health check, tracing). Npgsql only writes `DateTimeOffset` with
  offset 0, so convert to UTC before saving (the repository does this for `PublishedDate`). String lists are `text[]`;
  sections and parser metadata are `jsonb`. `Persistence/Migrations` is marked `generated_code` in `.editorconfig`.
- **First migration log noise:** on a fresh database, EF logs a failed `SELECT … FROM "__EFMigrationsHistory"` before
  it creates that table. It's harmless.
- **Line endings are LF** (`.gitattributes`, `.editorconfig`). Git on this machine has `core.autocrlf=true`, and
  `dotnet format` checks `end_of_line`, so new files from templates or editors may need converting.
- **docling-serve image** is `ghcr.io/docling-project/docling-serve-cpu:v1.35.0` (CPU only, several GB). The Python
  stack used docling 2.52.
- **Optional parameters.** Aspire waits for any parameter without a value, so optional secrets are only added
  when configuration has them (see `AppHost.cs`).
- **Langfuse** (v3.225 when first run) initialises headlessly: org `paperpilot-org`, project `paperpilot`, user
  `admin@example.com`, password in user secrets as `Parameters:langfuse-admin-password`. Session containers reach the
  persistent Postgres over the container network, so `DATABASE_URL` comes from `UriExpression`.
- **Search.** `OpenSearchClient` throws `SearchUnavailableException` (→ 503) or `SearchQueryException` (→ 500) instead
  of returning empty results (B6). Golden tests compare `QueryBuilder` with JSON recorded from the Python code
  (`tests/fixtures/python-parity`, written by `scripts/dump_parity_fixtures.py` in the Python repo).
- **Seeding and parity.** To seed, start only the Python OpenSearch (`docker compose up -d opensearch` in the Python
  repo), start PaperPilot (the API creates the index), run the seed tool, then `docker compose stop`. The Python
  index keeps deleted chunks in its BM25 statistics, so compare against it only after
  `POST /arxiv-papers-chunks/_forcemerge?only_expunge_deletes=true`, or run the Python code against port 9210.
- **Testcontainers:** an HTTP wait strategy's `ForPath` must not contain a query string (it never matches); the
  OpenSearch fixture waits on `/_cluster/health` and then polls for green/yellow. Container tests share one xUnit
  collection (`Containers`), so they run sequentially against one Postgres and one OpenSearch.
- **LLM (plan R5).** `AddPaperPilotLlm()` registers OllamaSharp as `IChatClient` (OTel spans with prompts and
  completions from source `PaperPilot.Llm`, plus logging). Build `ChatOptions` with `ChatOptionsFactory`: `think` goes
  through `AdditionalProperties["think"]`, sampling settings into Ollama's `options`. Structured output
  (`GetResponseAsync<T>`) works with `qwen3.5:9b`. The `ollama` HTTP client has no retries; its timeout is
  `Ollama:TimeoutSeconds`.
- **Tracing (plan R6).**
  - RAG spans come from `PaperPilot.Rag` (`RagTelemetry`) and carry `langfuse.*` attributes. Langfuse gets its own tracer
    provider (`LangfuseExporter`) that listens only to `PaperPilot.*` and `Microsoft.Agents.AI*`.
  - Don't subclass `CompositeProcessor` to filter spans: the SDK nests every later processor inside a root
    `CompositeProcessor`, which cut the Aspire dashboard down to the filtered spans.
  - In Langfuse the chat span is a generation with token usage. `rag_request` points at the ASP.NET span as its parent,
    which Langfuse never gets.
  - To query Langfuse, read the keys from the AppHost user secrets inside a script; never print them.
- **Agent Framework workflows (plan R4, `Microsoft.Agents.AI.Workflows` 1.23).**
  - A node is an `Executor<TIn, TOut>` overriding `ValueTask<TOut> HandleAsync(TIn, IWorkflowContext, CancellationToken)`;
    the return value goes along the node's outgoing edges. A terminal node is an `Executor<TIn>` that calls
    `context.YieldOutputAsync(value)`. It needs `[YieldsOutput(typeof(T))]` (otherwise: "Cannot output object of type
    T. Expecting one of []") and must be listed in `WorkflowBuilder.WithOutputFrom(...)`.
  - Conditional edges: `AddEdge<T>(from, to, predicate)`. Every edge whose predicate is true gets the message; if
    none is, the run goes idle without output. A loop is just an edge back to an earlier node.
  - Run with `InProcessExecution.Concurrent.RunAsync(workflow, input, cancellationToken: ct)` and read
    `run.OutgoingEvents` (`WorkflowOutputEvent.Is<T>(out var value)`). One built `Workflow` serves concurrent runs only
    in the `Concurrent` environment and only if every executor passes `declareCrossRunShareable: true`; the default
    environment throws "already owned by another runner".
  - A handler exception doesn't make `RunAsync` throw: the run emits `ExecutorFailedEvent` (`Data` is the original
    exception) and `WorkflowErrorEvent`, then ends without output.
  - Cancellation reaches the handlers, but `RunAsync` returns normally (status `Running`, no output) and `DisposeAsync`
    waits for the handler to stop. Check the token after the run.
  - `Activity.Current` inside a handler is the caller's span, so node spans nest under the request span. The
    framework's own spans are off unless `WithOpenTelemetry()` is called; they add `workflow.build`,
    `workflow.session`, `workflow_invoke`, `executor.process {id}`, `message.send` and `edge_group.process` (source
    `Microsoft.Agents.AI.Workflows`), several per node, so PaperPilot leaves them off.
  - The agent lives in `PaperPilot.Rag/Agentic`: one `AgenticRagWorkflow` singleton, stateless executors, and an
    immutable `AgentRunState`. Test stubs tell its four LLM calls apart by the prompt's first line; match the question on
    `User Query: …`, because the guardrail prompt's own examples include "What is 2+2?".
- **Telegram bot** (`PaperPilot.Api/Telegram`) runs only with `Telegram:Enabled` and a token (the AppHost sets both
  when `Parameters:telegram-bot-token` is in its user secrets).
  - Only one process may poll a token, so stop the Python stack's bot first (Telegram answers 409 otherwise).
  - The token is part of every Bot API URL. Never give the client a factory `HttpClient` (it logs URLs), and keep
    `TelegramRegistration.HideBotToken` in place: it drops `getUpdates` spans and redacts the token in the rest.
- **Web UI** (`PaperPilot.Web`): the chat page is Interactive Server without prerendering, and talks to the API only
  through `PaperPilotApiClient` (no resilience handlers, 15-minute timeout; Stop cancels).
  - Razor passes a **string** component parameter literally unless it starts with `@`: `Message="_formMessage"` sends
    the text "_formMessage" (and the field shows up as unused, CS0414). Write `Message="@_formMessage"`.
  - Answers go through `AnswerMarkdown` (Markdig with HTML disabled, http/https/mailto links only). Don't switch it to
    `UseAdvancedExtensions()`: generic attributes would let model output add `onclick`.
  - bUnit tests use the async event methods (`SubmitAsync`, `ClickAsync`, `InputAsync`) and `WaitForAssertion`; the
    synchronous ones can return before the handler has run when the whole suite runs in parallel.
- **Rebuilding while the stack runs:** running projects lock their `bin` folders. Stop just those resources, build,
  and start them again: `aspire resource api stop --apphost src/PaperPilot.AppHost` (also `worker`, `web`), then
  `... start`. A change to Core or Infrastructure needs `api` and `worker` stopped; the AppHost itself stays locked, so
  build the projects or tests rather than the whole solution.
- **Don't start the AppHost from VS Code's terminal** with `dotnet run`: the AppHost then asks VS Code to launch the
  projects, VS Code answers 400, and `api`, `web`, `worker` and `migrations` stay at FailedToStart. Use another
  terminal or `aspire start`.
- **Activities in async iterators:** `Activity.Current` resets at every `yield`, so spans started after one lose their
  parent. `RagService.StreamAsync` runs the pipeline in a normal async method that writes to a channel.
- **WireMock** adds a request to `LogEntries` only after the client already has the response, so assert on it after
  the call returns (or poll briefly), never in a test that aborts the request.
- **`HealthChecks` is ambiguous** in projects that reference `Aspire.StackExchange.Redis` (it brings a root
  `HealthChecks.*` namespace); write `PaperPilot.Infrastructure.HealthChecks.ApiTag`.
- **Ingestion.**
  - `DailyIngestionJob` runs setup → fetch → index → report → cleanup and writes an `ingestion_runs` row per attempt.
  - Hangfire retries a failed run twice, 5 minutes apart. A retry that is due while the Worker is down runs when it
    starts again.
  - PDFs are cached in `%LOCALAPPDATA%/PaperPilot/arxiv_pdfs`. All arXiv traffic shares one 3-second gate
    (`ArxivRateLimiter`), so a 15-paper run takes about 6 minutes, mostly docling parsing one PDF at a time.
- **docling-serve** must get `pdf_backend=pypdfium2` (`Docling:PdfBackend`). Its default backend runs words together
  in headings and text.
- **Python Docling on Windows** (for fixtures): the Hugging Face cache needs symlinks (Developer Mode), so download
  the models with `.venv/Scripts/docling-tools models download layout tableformer` and set
  `DOCLING_ARTIFACTS_PATH=%USERPROFILE%\.cache\docling\models`.
- **Hangfire.Core** depends on Newtonsoft.Json 11.0.1 (vulnerable, so NU1903 fails the build); `Directory.Packages.props`
  pins 13.0.4 and `PaperPilot.Ingestion` references it directly.
- **WireMock priorities:** a mapping without `AtPriority` has priority 0, the highest. Give default stubs a number
  (e.g. 10) when a test overrides them with `AtPriority(1)`.
- **`dotnet ef` writes CRLF** migration files. Git stores them as LF (`.gitattributes`); if `git status` shows them
  modified, run `sed -i 's/\r$//'` on them and re-add.
- **Known log noise:** at startup the AppHost may log one `crit` from `DcpExecutor` ("Watch task over Kubernetes
  ContainerExec resources terminated unexpectedly", a 1-minute timeout). Resources are unaffected.

## Measured

| Configuration | Docker memory (idle, 2026-10-02) |
|---|---|
| Without Langfuse | 2.05 GiB (OpenSearch 1.05, docling 0.96, Postgres 0.06, Redis 0.01) |
| With Langfuse | 3.9 GiB (+ langfuse-web 1.1, langfuse-worker 0.46, ClickHouse 0.34, MinIO 0.06) |

The docling-serve-cpu image is 7.65 GB on disk. The R1 unit tests take ~35 s (they wait for slow responses).
