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
- [x] Register `OllamaApiClient` as `IChatClient` through `CommunityToolkit.Aspire.OllamaSharp` (`builder.AddOllamaApiClient("ollama").AddChatClient()`), or manually if you need control over the `HttpClient`. Either way: **timeout = `Ollama:TimeoutSeconds`, no standard resilience handler** (R1). A retried 2-minute generation is worse than a clean failure.
- [x] Pipeline: `.UseOpenTelemetry(sourceName: "PaperPilot.Llm", configure: c => c.EnableSensitiveData = true)` (prompts and completions in spans, which Langfuse needs; fine for a local POC) and `.UseLogging()`.
- [x] A `ChatOptionsFactory` that sets `ModelId`, `Temperature`, `TopP`, and **`think`**. Map think through `ChatOptions.AdditionalProperties` if OllamaSharp honours it there; otherwise use `ChatOptions.RawRepresentationFactory = _ => new ChatRequest { Think = ... }`.
- [x] `OllamaModelCatalog`: `ListModelsAsync()` through `OllamaApiClient.ListLocalModelsAsync()` (N1).

### 3.2 Spike (R5 + R6), before building the rest
- [x] Point the chat client at **WireMock**, capture the request JSON, and assert `"think": false`, `options.temperature = 0.7` and `options.top_p = 0.9`.
- [x] Against real Ollama, check that `GetResponseAsync<GuardrailScoring>(prompt)` returns a valid instance with `qwen3.5:9b` (phase 5 depends on this).
- [x] With Langfuse on: one traced call shows up in Langfuse as a **generation** with input, output, model and token usage. Note whether filtering out the ASP.NET root span leaves a usable trace tree; if it doesn't, also export the server span for `/api/v1/*` routes. Write the findings in `CLAUDE.md`.

### 3.3 Prompts (`PaperPilot.Rag/Prompts`)
- [x] `rag_system.txt` as an embedded resource, byte-for-byte copied from Python.
- [x] `RagPromptBuilder.Build(query, chunks)` → `(string System, string User)`. Golden test: `System + "\n\n" + User` equals Python's `create_rag_prompt` output (fixture added to the phase-2 parity script).

### 3.4 Shared retrieval (`PaperPilot.Rag/Retrieval/PaperRetriever.cs`)
- [x] `RetrieveAsync(query, topK, useHybrid, categories)` → `RetrievalResult(Chunks, Sources, ArxivIds, TotalHits, SearchMode)`.
- [x] Embeds only when `useHybrid`. If embedding fails, it logs a warning, tags the span and uses BM25. `SearchMode` reports the mode **actually used** (**B24**).
- [x] Classic RAG, the agent (phase 5) and Telegram (phase 6) all use this one class.

### 3.5 `RagService` (`PaperPilot.Rag/RagService.cs`)
- [x] `AskAsync(AskRequest)` → `AskResponse`, following the behaviour above.
- [x] `StreamAsync(AskRequest)` → `IAsyncEnumerable<RagStreamEvent>`, using `IChatClient.GetStreamingResponseAsync`. A cache hit replays the answer in ~40-character slices **that keep whitespace** (**B17**).
- [x] Search outage (**B6**): `SearchUnavailableException` propagates. `/ask` → 503, `/stream` → `{error}` event.

### 3.6 Answer cache (`PaperPilot.Infrastructure/Caching/AnswerCache.cs`)
- [x] `Aspire.StackExchange.Redis` → `IConnectionMultiplexer`. `TryGetAsync(request, model)` and `StoreAsync(request, model, response)`, with exceptions caught and logged.
- [x] `CacheKey.Compute(...)` in Core, so it can be unit-tested.
- [ ] Optional: start Redis with `--maxmemory 256mb --maxmemory-policy allkeys-lru` (as in Python's compose) through the AppHost. This is safe because Langfuse has its own Redis (D13). **Not done:** Aspire 13.6 runs Redis as `/bin/sh -c "redis-server …"` with a command line it builds itself, so extra arguments can't be appended without rewriting that string. Entries expire after `Cache:TtlHours` anyway.

### 3.7 Endpoints (`PaperPilot.Api/Endpoints`)
- [x] `POST /api/v1/ask` → `RagService.AskAsync`. Unexpected exceptions → 500 `ProblemDetails` with the message, like Python's `HTTPException(500, str(e))`.
- [x] `POST /api/v1/stream` → `TypedResults.ServerSentEvents(...)` (C2), with `Cache-Control: no-cache`. Each event's `data` is the JSON object. Check with `curl -N` that tokens arrive one at a time and aren't buffered.
- [x] `GET /api/v1/models` → `{ default_model, models: [names] }` (N1).
- [x] `POST /api/v1/feedback` → `LangfuseScoresClient.SubmitAsync(traceId, score, comment)`: `POST {Langfuse:BaseUrl}/api/public/scores`, Basic auth (public:secret), body `{ traceId, name: "user-feedback", value, comment, dataType: "NUMERIC" }`. Returns 503 if Langfuse is disabled (**B16**), 500 on a Langfuse error, otherwise 200 `{success: true, message: "Feedback recorded successfully"}`.

### 3.8 Tracing (`ServiceDefaults` + `PaperPilot.Rag/Telemetry`)
- [x] `ActivitySource "PaperPilot.Rag"` with spans named as in Python: `rag_request` (root of the RAG work), `cache_lookup`, `query_embedding`, `search_retrieval`, `prompt_construction`. Generation is the `IChatClient` OTel span.
- [x] Set Langfuse-mapped attributes (check the exact names in the Langfuse OTel docs): `langfuse.trace.name`, `langfuse.user.id = "api_user"`, `langfuse.session.id`, `langfuse.trace.input/output`, and `langfuse.observation.input/output` per span. Use the same payloads Python logged, e.g. the search output `{chunks_returned, unique_papers, total_hits, arxiv_ids}`, the prompt output `{prompt_length, prompt_preview}`, and the request output `{answer, total_duration_seconds, response_length}`.
- [x] **Langfuse exporter** (only when `Langfuse:Enabled`): an OTLP exporter with `Protocol = HttpProtobuf`, `Endpoint = {BaseUrl}/api/public/otel/v1/traces` (with an explicit endpoint, the .NET exporter doesn't append `/v1/traces` itself), and header `Authorization=Basic base64(pk:sk)`. Wrap it in a **source-filtering processor** that forwards only `PaperPilot.*`, the `IChatClient` source and `Microsoft.Agents.AI*` spans, so HTTP/EF/Redis noise stays in the Aspire dashboard only. **As built:** a separate tracer provider that listens only to those sources, instead of a filtering processor (see As built).
- [x] `trace_id` exposed to clients = `Activity.Current.TraceId.ToHexString()` (C8). Langfuse uses the same 32-hex id. No phase 3 response carries it, because `AskResponse` keeps Python's shape; `/ask-agentic` returns it in phase 5.

## As built

- **Ollama client** is registered by hand (`AddPaperPilotLlm`) rather than through `CommunityToolkit.Aspire.OllamaSharp`. That integration pins an older OllamaSharp, and the manual version controls the `HttpClient` directly. It uses OllamaSharp 5.5 and `Microsoft.Extensions.AI` 10.10. The named client `ollama` has no resilience handler, and its `HttpClient.Timeout` comes from `Ollama:TimeoutSeconds` through the new `WithoutResilience(Func<IServiceProvider, TimeSpan>)` overload.
- **R5 spike:**
  - `think` works through `ChatOptions.AdditionalProperties["think"]`; `RawRepresentationFactory` works too. OllamaSharp sends it at the top level and `temperature`/`top_p` inside `options`.
  - Against real `qwen3.5:9b`, `GetResponseAsync<GuardrailScoring>` returned valid scores in 1.5–4 s (95, 0 and 95 for the three probe questions), using Ollama's JSON-schema `format`, with token usage.
- **B29 found during the spike:** Python passed `temperature=0.7, top_p=0.9` as top-level keys of `/api/generate`, where Ollama ignores them. A top-level `num_predict: 5` produced 141 tokens; the same value in `options` produced 5. Python's answers therefore used the model's own defaults (temperature 1, top_p 0.95 for `qwen3.5:9b`). PaperPilot's values actually apply.
- **B30 found by the integration tests:** Python's hybrid query filters categories in the BM25 arm only, so the k-NN arm returns chunks from any category. The k-NN arm is now wrapped in the same filter. The nmslib engine can't filter inside `knn`, so this is a post-filter on the k candidates. The hybrid golden test applies this one change to the Python fixture before comparing.
- **R6 spike (Langfuse 3.225):**
  - The chat client's span arrives as a **generation** with model, model parameters, input and output messages (`gen_ai.input.messages` / `gen_ai.output.messages`) and token usage. For example: 1766 in, 295 out.
  - Every pipeline span carries Python's input and output payloads, and the trace gets name, user, session, input and output from `langfuse.*` attributes.
  - `/feedback` with the W3C trace id attached a `user-feedback` score to that trace.
  - `rag_request`'s parent is the ASP.NET request span, which Langfuse never receives, so its `parentObservationId` dangles. Langfuse's docs say such observations "appear disconnected". Here everything nests under `rag_request`, so it is the visible root.
  - The v4 app-root marker (`langfuse.internal.is_app_root`) isn't available in v3. If the UI renders this badly, also export the server span.
- **Langfuse gets its own tracer provider,** listening only to `PaperPilot.*` and `Microsoft.Agents.AI*`. The planned filtering processor, a `CompositeProcessor` subclass, broke the Aspire dashboard. The SDK appends later processors *into* a root processor that is a `CompositeProcessor`, so the dashboard exporter ended up behind the Langfuse filter. A unit test now checks that the main pipeline still gets every span.
- **One trace** sent while the Langfuse stack was still starting lost its early spans; later traces, including the first after an API restart, were complete.
- **`/stream` runs the pipeline in an ordinary async method that writes to a channel.** Inside an async iterator, `Activity.Current` resets at every `yield`, and later spans (including the chat span) would lose their `rag_request` parent. A unit test checks the parenting.
- **Caching:** a blank answer isn't cached; it means generation went wrong. Redis failures are logged and treated as misses: `/ask` still answers with `ConnectionStrings:redis` pointing at a closed port. The hash part of the key equals Python's for the same request (golden-tested); only the prefix differs (C6).
- **`/models`** sorts names and returns 503 `Cannot list Ollama models: …` when Ollama is down. `/ask` returns 500 with Ollama's error (e.g. `model '…' not found`), like Python.
- **SSE:** `TypedResults.ServerSentEvents` sends `Cache-Control: no-cache,no-store`. Tokens arrived one at a time in `curl -N`: metadata after 2.4 s, then tokens about 20 ms apart.
- **Metrics:** the chat client's GenAI metrics (token usage, durations) go to the Aspire dashboard (`AddMeter("PaperPilot.*")`).
- **Real stack, 2026-10-02:**
  - `/ask` "What are transformers?" took 25 s and correctly said the 10 seeded papers don't define transformers, citing the two it drew on.
  - The repeat came from the cache in 77 ms; its trace has `cache_lookup` (hit) and no generation.
  - The dashboard shows `POST /api/v1/ask` → `rag_request` → Redis `GET`/`SETEX`, Jina, OpenSearch and Ollama HTTP spans.

## Tests
- [x] Unit: prompt golden test; cache key (stable, ignores category order, changes with model); `RagService` with a `FakeChatClient` and a fake retriever: no chunks → canned answer, not cached; cache hit → no LLM call; embedding failure → `search_mode: "bm25"`; stream event order; cached replay keeps `\n\n`.
- [x] Integration: Redis + OpenSearch containers, with WireMock standing in for Ollama `/api/chat` (non-streaming JSON and streaming NDJSON). Exact status codes and shapes for `/ask` (200/400/503), the `/stream` event sequence, `/feedback` 503 when disabled, and 200 with the captured body against a WireMock Langfuse.

## Done when
- [x] `curl -X POST localhost:8100/api/v1/ask -d '{"query":"What are transformers?"}'` (with the JSON content-type header) returns a grounded answer citing `[arXiv:…]`, plus sources.
- [x] `curl -N -X POST localhost:8100/api/v1/stream ...` streams tokens.
- [x] Repeating the same `/ask` is served from the cache (the trace shows a `cache_lookup` hit and no generation span).
- [x] The Aspire dashboard shows the span tree for each request. With Langfuse on, the trace appears there with token usage, and `/feedback` with its `trace_id` attaches a score.
