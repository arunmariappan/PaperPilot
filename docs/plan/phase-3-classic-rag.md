# Phase 3: Classic RAG, cache, tracing and feedback

**Goal:** `POST /api/v1/ask`, `POST /api/v1/stream`, `POST /api/v1/feedback` and `GET /api/v1/models` work against the seeded index with your local Ollama. Answers are cached in Redis, and every request is traced (Aspire dashboard always; Langfuse when enabled).

**Size:** M · **Depends on:** phase 2

## Behaviour that must be preserved

From `routers/ask.py`, `services/ollama/*`, `services/cache/client.py` and `services/langfuse/tracer.py`:
- **Pipeline:** cache lookup → (embed → search) → build prompt → generate → store in cache. A cache failure is logged and never fails the request.
- **No chunks:** `/ask` answers `"I couldn't find any relevant information in the papers to answer your question."` with `sources: []` and `chunks_used: 0`, and this answer **isn't cached**. `/stream` sends a single `{"answer": "No relevant information found.", "sources": [], "done": true}`.
- **Sources:** de-duplicated `https://arxiv.org/pdf/{id-without-version}.pdf` per chunk. PaperPilot keeps first-seen order; Python used a `set`, so its order was arbitrary.
- **Generation parameters:** `temperature 0.7`, `top_p 0.9`, `think = Ollama:Think` (false), model = `request.model ?? Ollama:Model`, timeout `Ollama:TimeoutSeconds` (300).
- **Prompt:** `rag_system.txt` verbatim. Then `### Context from Papers:\n\n`, and for each chunk `[{i}. arXiv:{arxiv_id}]\n{chunk_text}\n\n`, then `### Question:\n{query}\n\n### Answer:\nProvide a natural, conversational response (not JSON) and cite sources using [arXiv:id] format.\n\n`. C3: the system prompt goes in the system message and the rest in the user message.
- **`/stream` event order:** `{sources, chunks_used, search_mode}` → `{chunk}`… → `{answer, done: true}`, or `{error}` on exception. A cache hit replays the same shapes.
- **Cache key:** sha256 of canonical JSON `{categories (sorted, [] if none), model, query, top_k, use_hybrid}` with sorted keys, first 16 hex characters, prefix `paperpilot:ask:` (C6). TTL `Cache:TtlHours` (6). The stored value is the `AskResponse` JSON.
- **Feedback:** score named `user-feedback`, value in −1..1, optional comment, attached to `trace_id`.

## Tasks

### 3.1 Ollama `IChatClient` (`PaperPilot.Infrastructure/Llm`)
- [ ] Register `OllamaApiClient` as `IChatClient` through `CommunityToolkit.Aspire.OllamaSharp` (`builder.AddOllamaApiClient("ollama").AddChatClient()`), or manually if you need control over the `HttpClient`. Either way: **timeout = `Ollama:TimeoutSeconds`, no standard resilience handler** (R1). A retried 2-minute generation is worse than a clean failure.
- [ ] Pipeline: `.UseOpenTelemetry(sourceName: "PaperPilot.Llm", configure: c => c.EnableSensitiveData = true)` (prompts and completions in spans, which Langfuse needs; fine for a local POC) and `.UseLogging()`.
- [ ] A `ChatOptionsFactory` that sets `ModelId`, `Temperature`, `TopP`, and **`think`**. Map think through `ChatOptions.AdditionalProperties` if OllamaSharp honours it there; otherwise use `ChatOptions.RawRepresentationFactory = _ => new ChatRequest { Think = ... }`.
- [ ] `OllamaModelCatalog`: `ListModelsAsync()` through `OllamaApiClient.ListLocalModelsAsync()` (N1).

### 3.2 Spike (R5 + R6), before building the rest
- [ ] Point the chat client at **WireMock**, capture the request JSON, and assert `"think": false`, `options.temperature = 0.7` and `options.top_p = 0.9`.
- [ ] Against real Ollama, check that `GetResponseAsync<GuardrailScoring>(prompt)` returns a valid instance with `qwen3.5:9b` (phase 5 depends on this).
- [ ] With Langfuse on: one traced call shows up in Langfuse as a **generation** with input, output, model and token usage. Note whether filtering out the ASP.NET root span leaves a usable trace tree; if it doesn't, also export the server span for `/api/v1/*` routes. Write the findings in `CLAUDE.md`.

### 3.3 Prompts (`PaperPilot.Rag/Prompts`)
- [ ] `rag_system.txt` as an embedded resource, byte-for-byte copied from Python.
- [ ] `RagPromptBuilder.Build(query, chunks)` → `(string System, string User)`. Golden test: `System + "\n\n" + User` equals Python's `create_rag_prompt` output (fixture added to the phase-2 parity script).

### 3.4 Shared retrieval (`PaperPilot.Rag/Retrieval/PaperRetriever.cs`)
- [ ] `RetrieveAsync(query, topK, useHybrid, categories)` → `RetrievalResult(Chunks, Sources, ArxivIds, TotalHits, SearchMode)`.
- [ ] Embeds only when `useHybrid`. If embedding fails, it logs a warning, tags the span and uses BM25. `SearchMode` reports the mode **actually used** (**B24**).
- [ ] Classic RAG, the agent (phase 5) and Telegram (phase 6) all use this one class.

### 3.5 `RagService` (`PaperPilot.Rag/RagService.cs`)
- [ ] `AskAsync(AskRequest)` → `AskResponse`, following the behaviour above.
- [ ] `StreamAsync(AskRequest)` → `IAsyncEnumerable<RagStreamEvent>`, using `IChatClient.GetStreamingResponseAsync`. A cache hit replays the answer in ~40-character slices **that keep whitespace** (**B17**).
- [ ] Search outage (**B6**): `SearchUnavailableException` propagates. `/ask` → 503, `/stream` → `{error}` event.

### 3.6 Answer cache (`PaperPilot.Infrastructure/Caching/AnswerCache.cs`)
- [ ] `Aspire.StackExchange.Redis` → `IConnectionMultiplexer`. `TryGetAsync(request, model)` and `StoreAsync(request, model, response)`, with exceptions caught and logged.
- [ ] `CacheKey.Compute(...)` in Core, so it can be unit-tested.
- [ ] Optional: start Redis with `--maxmemory 256mb --maxmemory-policy allkeys-lru` (as in Python's compose) through the AppHost. This is safe because Langfuse has its own Redis (D13).

### 3.7 Endpoints (`PaperPilot.Api/Endpoints`)
- [ ] `POST /api/v1/ask` → `RagService.AskAsync`. Unexpected exceptions → 500 `ProblemDetails` with the message, like Python's `HTTPException(500, str(e))`.
- [ ] `POST /api/v1/stream` → `TypedResults.ServerSentEvents(...)` (C2), with `Cache-Control: no-cache`. Each event's `data` is the JSON object. Check with `curl -N` that tokens arrive one at a time and aren't buffered.
- [ ] `GET /api/v1/models` → `{ default_model, models: [names] }` (N1).
- [ ] `POST /api/v1/feedback` → `LangfuseScoresClient.SubmitAsync(traceId, score, comment)`: `POST {Langfuse:BaseUrl}/api/public/scores`, Basic auth (public:secret), body `{ traceId, name: "user-feedback", value, comment, dataType: "NUMERIC" }`. Returns 503 if Langfuse is disabled (**B16**), 500 on a Langfuse error, otherwise 200 `{success: true, message: "Feedback recorded successfully"}`.

### 3.8 Tracing (`ServiceDefaults` + `PaperPilot.Rag/Telemetry`)
- [ ] `ActivitySource "PaperPilot.Rag"` with spans named as in Python: `rag_request` (root of the RAG work), `cache_lookup`, `query_embedding`, `search_retrieval`, `prompt_construction`. Generation is the `IChatClient` OTel span.
- [ ] Set Langfuse-mapped attributes (check the exact names in the Langfuse OTel docs): `langfuse.trace.name`, `langfuse.user.id = "api_user"`, `langfuse.session.id`, `langfuse.trace.input/output`, and `langfuse.observation.input/output` per span. Use the same payloads Python logged, e.g. the search output `{chunks_returned, unique_papers, total_hits, arxiv_ids}`, the prompt output `{prompt_length, prompt_preview}`, and the request output `{answer, total_duration_seconds, response_length}`.
- [ ] **Langfuse exporter** (only when `Langfuse:Enabled`): an OTLP exporter with `Protocol = HttpProtobuf`, `Endpoint = {BaseUrl}/api/public/otel/v1/traces` (with an explicit endpoint, the .NET exporter doesn't append `/v1/traces` itself), and header `Authorization=Basic base64(pk:sk)`. Wrap it in a **source-filtering processor** that forwards only `PaperPilot.*`, the `IChatClient` source and `Microsoft.Agents.AI*` spans, so HTTP/EF/Redis noise stays in the Aspire dashboard only.
- [ ] `trace_id` exposed to clients = `Activity.Current.TraceId.ToHexString()` (C8).

## Tests
- [ ] Unit: prompt golden test; cache key (stable, ignores category order, changes with model); `RagService` with a `FakeChatClient` and a fake retriever: no chunks → canned answer, not cached; cache hit → no LLM call; embedding failure → `search_mode: "bm25"`; stream event order; cached replay keeps `\n\n`.
- [ ] Integration: Redis + OpenSearch containers, with WireMock standing in for Ollama `/api/chat` (non-streaming JSON and streaming NDJSON). Exact status codes and shapes for `/ask` (200/400/503), the `/stream` event sequence, `/feedback` 503 when disabled, and 200 with the captured body against a WireMock Langfuse.

## Done when
- [ ] `curl -X POST localhost:8100/api/v1/ask -d '{"query":"What are transformers?"}'` (with the JSON content-type header) returns a grounded answer citing `[arXiv:…]`, plus sources.
- [ ] `curl -N -X POST localhost:8100/api/v1/stream ...` streams tokens.
- [ ] Repeating the same `/ask` is served from the cache (the trace shows a `cache_lookup` hit and no generation span).
- [ ] The Aspire dashboard shows the span tree for each request. With Langfuse on, the trace appears there with token usage, and `/feedback` with its `trace_id` attaches a score.
