# Decisions

The choices that shape PaperPilot, and why. The first table was settled before any code was written
([plan §1](plan/README.md#1-decisions)); the rest were decided while building it, and each links to where it is
explained in full. Intended differences from the Python version have their own list:
[behaviour-changes.md](plan/behaviour-changes.md).

## Up front

| # | Decision | Choice | Why |
|---|---|---|---|
| D1 | Scope | The finished Python system only | Its notebooks and intermediate versions aren't ported. |
| D2 | Repository | A standalone repo, work on `main` | One developer; small conventional commits instead of feature branches. |
| D3 | Runtime | .NET 10 (LTS), C# 14, SDK pinned in `global.json` | Long-term support. |
| D4 | Local orchestration | Aspire 13 AppHost instead of `compose.yml` | One command starts everything, and the dashboard shows logs, traces and metrics. |
| D5 | Agent graph | Microsoft Agent Framework Workflows instead of LangGraph | The .NET equivalent: typed executors and conditional edges. |
| D6 | Scheduler | Hangfire on Postgres instead of Airflow | A dashboard and retries without a separate scheduler stack. |
| D7 | UI | Blazor Web App (Interactive Server) instead of Gradio | Streams over the existing SignalR circuit; no JavaScript build. |
| D8 | Python bugs | Fixed while porting, each one listed | A port that copies bugs only moves them. |
| D9 | API contract | The same routes and snake_case JSON as Python | Existing clients keep working; the parity check can compare responses directly. |
| D10 | PDF parsing | docling-serve as a container, PdfPig for page counts | Docling only exists in Python. |
| D11 | LLM | The host's Ollama, `qwen3.5:9b`, thinking off | Uses the GPU directly; thinking made answers several times slower. |
| D12 | Observability | OpenTelemetry to the Aspire dashboard, and to Langfuse over OTLP | No Langfuse .NET SDK is needed. |
| D13 | Langfuse storage | Its database on the main Postgres, its own small Redis | Saves memory; Langfuse's queues need `noeviction`, the answer cache doesn't. |
| D14 | Blank questions | Rejected with 400 | A blank question can't produce a useful answer (C13). |
| D15 | License | Apache-2.0 | |

## Made along the way

### Infrastructure

- **Long-running HTTP calls replace Aspire's standard resilience handler** (risk R1). Its ~30-second total timeout
  would cut off Ollama generation, docling parsing and Jina back-off. Each such client gets its own pipeline from
  `PaperPilot.Infrastructure.Http` (a total timeout, plus a 429 retry where it helps); LLM calls are never retried,
  because a retry repeats a whole generation. The UI's API client has no handler at all. ([phase 0](plan/phase-0-bootstrap.md),
  [phase 2](plan/phase-2-search.md))
- **Fixed ports that don't clash with the Python stack** (API 8100, UI 8101, Worker 8102, OpenSearch 9210, Postgres
  5442, Redis 6390), so both stacks can be installed side by side, though only one runs at a time on 8 GB of Docker
  memory.
- **Persistent containers and named volumes** for Postgres, Redis and OpenSearch: they keep running across AppHost
  restarts, so the data stays and OpenSearch doesn't have to start again each time.
- **`localhost` is tried over IPv4 first** by every factory `HttpClient` (`LoopbackConnect` in ServiceDefaults).
  Container ports and Ollama listen on `127.0.0.1` only, and on Windows each new connection otherwise spent about
  2 seconds on `::1` first. The parity check found it. ([phase 8](plan/phase-8-hardening.md))
- **String lists are Postgres arrays (`text[]`)**, not `jsonb`, and timestamps are `timestamptz` in UTC (B21). A
  separate migration service applies EF Core migrations before the API and Worker start (B22).
  ([phase 1](plan/phase-1-domain-persistence.md))

### Search and RAG

- **Chunk IDs are deterministic** (`{arxiv_id}:{chunk_index}`, C5), so re-indexing replaces chunks instead of
  duplicating them, and a paper's old chunks are deleted only once its new embeddings are ready (B34).
- **Hybrid search keeps Python's design**: BM25 and k-NN combined by OpenSearch's RRF search pipeline. The category
  filter now applies to both arms (B30).
- **Failures are explicit.** Search outages return 503 or an `{error}` event instead of looking like "no results"
  (B6); an embedding failure falls back to BM25 and `search_mode` says so (B24).
- **The Ollama client is registered by hand** (OllamaSharp behind `IChatClient`) rather than through the Aspire
  community integration, which pinned an older OllamaSharp. Sampling settings go in Ollama's `options`, where they
  take effect (B29). ([phase 3](plan/phase-3-classic-rag.md))
- **The answer cache is exact-match** in Redis for 6 hours, keyed by the whole request and the model. A Redis failure
  counts as a miss. A semantic cache is a possible follow-up.

### Agentic RAG

- **One workflow singleton with stateless executors and an immutable run state**, run in the framework's concurrent
  environment so requests don't block each other. The agent calls retrieval directly instead of through a synthetic
  tool call (C10). The framework's own spans stay off; PaperPilot's node spans carry the Langfuse attributes.
  ([phase 5](plan/phase-5-agentic-rag.md))
- **Structured output** (`GetResponseAsync<T>`, a JSON schema in Ollama's `format`) replaces Python's
  `with_structured_output`; parse failures fall back to the same defaults Python used.

### Tracing

- **Langfuse gets its own tracer provider** that listens only to PaperPilot's and the agent framework's sources, so
  HTTP and database spans stay in the Aspire dashboard. A filtering processor broke the dashboard export.
- **`trace_id` is the OpenTelemetry trace id** (C8), returned even with Langfuse off (C14), so the same id finds the
  trace in both places.

### Ingestion

- **One Hangfire job with five logged steps** (C9), retried twice 5 minutes apart, and an `ingestion_runs` row per
  attempt (N2). A run fails visibly when nothing useful happened (B15). Scheduled runs catch up on up to three missed
  days. ([phase 4](plan/phase-4-ingestion.md))
- **docling-serve uses the `pypdfium2` backend**: its default ran words together in headings.
- **One arXiv rate limiter** (a request start every 3 seconds) shared by the API client and all PDF downloads (B26).

### Telegram and UI

- **The Telegram bot runs inside the API**, as in Python, with long polling (no public URL) and the shared
  `RagService` (C12). Its client has its own `HttpClient`, and the token is redacted from every span, because it is
  part of each Bot API URL. ([phase 6](plan/phase-6-telegram.md))
- **The chat page renders without prerendering**, and treats model output as untrusted: Markdown with raw HTML
  disabled and only `http`, `https` and `mailto` links. ([phase 7](plan/phase-7-blazor-ui.md))

### Testing and CI

- **xUnit v3 on Microsoft.Testing.Platform** (`global.json`), Shouldly, WireMock for HTTP dependencies, Testcontainers
  for Postgres, Redis and OpenSearch, bUnit for components.
- **Golden fixtures recorded from the Python code** (query JSON, prompts, chunker output, fallbacks) pin parity at
  the unit level; `tools/parity-check.cs` checks it end to end ([parity report](parity-report.md)).
- **Coverage uses `Microsoft.Testing.Extensions.CodeCoverage`**, not `coverlet.collector`, which is a VSTest data
  collector and doesn't run on Microsoft.Testing.Platform.
- **The AppHost smoke test is manual** (a `workflow_dispatch` workflow): it pulls about 10 GB of images.

## Follow-ups (out of scope)

- Deployment: `aspire publish` to Docker Compose or Azure Container Apps, images in GitHub Container Registry.
- Authentication on the API and the Hangfire dashboard. Neither has any, as in Python; don't expose them beyond
  localhost.
- A semantic (embedding-similarity) answer cache in place of exact match.
