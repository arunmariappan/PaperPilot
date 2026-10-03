# Phase 7: Blazor chat UI

**Goal:** `PaperPilot.Web` covers everything the Gradio app did (streaming answers, advanced options, examples) and adds an agentic mode with reasoning steps and feedback. It talks to the API only over HTTP.

**Size:** M · **Depends on:** phase 3 (`/stream`, `/models`, `/feedback`), phase 5 (`/ask-agentic`)

## Behaviour that must be preserved (from `src/gradio_app.py`)
- A question textbox and an **Ask** button. Enter submits.
- **Advanced options** (collapsed by default): `top_k` slider 1–10 (default 3), **hybrid search** checkbox (default on), **model** dropdown (default = the configured model), **categories** text field (comma-separated, e.g. `cs.AI, cs.LG`).
- The answer streams in as Markdown. Sources are listed after it.
- **Examples**, one click to fill the form, copied from Gradio: "What are transformers in machine learning?" (3, hybrid, `cs.AI, cs.LG`), "How do convolutional neural networks work?" (5, hybrid, `cs.CV, cs.LG`), "What is attention mechanism in deep learning?" (4, BM25, `cs.AI`), "Explain reinforcement learning algorithms" (3, hybrid, `cs.LG, cs.AI`), "What are the latest developments in NLP?" (5, hybrid, `cs.CL`). Use the configured default model instead of the hard-coded `llama3.2:1b`.

## Additions
- A **mode switch**: *Classic (streaming)* → `/api/v1/stream`; *Agentic* → `/api/v1/ask-agentic`. Agentic mode shows a progress indicator, then the answer, **reasoning steps**, retrieval attempts and sources.
- **Feedback** (agentic mode, when a `trace_id` is returned): 👍 / 👎 plus an optional comment → `/api/v1/feedback` (scores +1 / −1). Hidden when the API returns 503 (Langfuse off).
- The **model dropdown** is filled from `GET /api/v1/models` (N1), falling back to the configured default.
- A **Stop** button cancels an in-flight stream.
- Clear error states: SSE `{error}` event, 503 "Search is unavailable", network failure.

## Tasks

### 7.1 Project setup
- [x] Blazor Web App with **Interactive Server** render mode (from phase 0). Add `ServiceDefaults`.
- [x] `PaperPilotApiClient`, a typed `HttpClient` with `BaseAddress = "https+http://api"` (Aspire service discovery). **No standard resilience timeout** on this client (R1): streams and agentic calls can take minutes. Use `HttpCompletionOption.ResponseHeadersRead` for `/stream`.

### 7.2 API client
- [x] `StreamAskAsync(AskRequest, CancellationToken)` → `IAsyncEnumerable<StreamEvent>`. Parse the response with `System.Net.ServerSentEvents.SseParser` and deserialize each `data` payload into `Metadata | Chunk | Done | Error` (snake_case JSON).
- [x] `AskAgenticAsync`, `SubmitFeedbackAsync`, `GetModelsAsync`.

### 7.3 Components (`Components/Pages/Chat.razor` at `/`, plus pieces under `Components/Chat/`)
- [x] `QuestionForm`, `AdvancedOptions`, `ExamplesList`, `AnswerView`, `SourcesList`, `ReasoningSteps`, `FeedbackBar`.
- [x] `AnswerView` renders Markdown with **Markdig with raw HTML disabled** (`.DisableHtml()`), because LLM output must never inject markup or script. Sources render as `arXiv:{id}` links to the abs page, with a PDF link.
- [x] Throttle re-renders while streaming: append tokens to a buffer and call `StateHasChanged` at most every ~50 ms.
- [x] Keep it simple: one Q&A at a time, as in Gradio, with an optional collapsible list of earlier answers from this browser session (in memory only).
- [x] Plain CSS (no component library), readable at phone width.

## Tests
- [x] Unit: the SSE client parses a recorded `/stream` body (metadata, chunks, done; and the error variant); categories text → list (trimmed, empties dropped).
- [x] Optional component tests with **bUnit**: `AnswerView` doesn't render raw `<script>`; `FeedbackBar` hides without a `trace_id`.

## As built

- **Render mode:** the chat page is Interactive Server with **prerendering off**. A prerendered form would post to the
  server if submitted before the circuit connects, and `/models` would be fetched twice.
- **`PaperPilotApiClient`** (`Api/`) is a typed client on `https+http://api`.
  - Its resilience handlers are removed (R1) and `HttpClient.Timeout` is 15 minutes, which only catches a hung request.
    Stop, or closing the page, cancels a request.
  - `/stream` is read with `ResponseHeadersRead` and `SseParser`. Each `data` payload is deserialised into the shared
    `RagStreamEvent` contract and mapped to `StreamMetadata | StreamChunk | StreamDone | StreamError`. `done` is checked
    before `sources`, because the no-results event carries both (B35).
  - Errors become an `ApiException` whose message is the problem's `detail`, its first validation error, its `title`,
    or the status code. When the API can't be reached, the message is "Cannot reach the PaperPilot API: …".
- **Markdown:** Markdig 1.4 with `DisableHtml()`. It does not use `UseAdvancedExtensions()`, because its generic-attributes
  extension (`{onclick=…}`) would let the text add any attribute. Links keep only `http`, `https` and `mailto` URLs
  (other links become their text), images become links, and links open in a new tab with `rel="noopener noreferrer"`.
  LaTeX such as `$\beta_0$` is shown as written.
- **Streaming:** tokens go into the answer as they arrive, and the page re-renders at most every 50 ms. A one-second
  ticker also updates the elapsed time and shows any token that hasn't been drawn yet.
- **Enter submits, Shift+Enter adds a new line,** through a 10-line `wwwroot/app.js` (Blazor can't cancel a key press
  conditionally without JavaScript).
- **Feedback:** 👍 / 👎 plus an optional comment, recorded on the answer, so it survives the answer moving into
  "Earlier answers". A 503 hides every feedback bar and shows "Feedback is off because Langfuse tracing is disabled."
- **Search outages:** `/stream` reports them as an `{error}` event without a type, so the UI shows its text as it is,
  for example "Error: OpenSearch is unreachable at http://localhost:9210/: No connection could be made…". Agentic mode
  answers "…the paper search is unavailable right now…" with the step "Search was unavailable".
- **Sources** are `arXiv:{id}` (abstract page) and `PDF` links. `ArxivId.FromPdfUrl` moved to Core so the Telegram bot
  and the UI read IDs from source URLs the same way (old-style IDs such as `cs/0112017` keep their archive, B8).
- **Tests** (39, in `tests/PaperPilot.UnitTests/Web`):
  - the SSE reader against two `/stream` bodies recorded from the real API (`tests/fixtures/web/*.sse`);
  - categories parsing, form defaults and examples;
  - Markdown safety: raw HTML, `javascript:`/`data:` links, script autolinks, images and attribute syntax;
  - the API client against WireMock: snake_case bodies, problem details, an unreachable API, feedback 503;
  - bUnit (2.11): `AnswerView` never renders `<script>`; `FeedbackBar` hides without a `trace_id` and sends a score;
    the page streams a recorded answer and fills the form from an example. Use bUnit's async event methods
    (`SubmitAsync`, `ClickAsync`): under a full parallel test run the synchronous ones can return before the handler
    has run.

## Done when
- [x] `http://localhost:8101` streams a classic answer token by token, with sources, and **Stop** cancels it.
  *Checked 2026-10-03* (headless Chromium): the answer grew 466 → 1,014 → 1,619 → 1,843 characters and finished in
  35 s, with 3 sources. Stop at 18 s froze another answer at 107 characters, and its API request span ended at 17.8 s,
  so generation stopped too.
- [x] Agentic mode shows the answer, reasoning steps and sources. 👍 creates a score in Langfuse (when enabled).
  *Checked 2026-10-03:* an answer with 4 reasoning steps and 1 source in 53 s. 👍 with a comment created a
  `user-feedback` score of 1, with the comment, on the answer's trace (Langfuse scores API).
- [x] Each example fills the form correctly. The model dropdown lists your installed Ollama models.
  *Checked 2026-10-03:* the BM25 example set top_k 4, hybrid off and `cs.AI`, with `qwen3.5:9b`. The dropdown lists
  `qwen3.5:9b`, the only model `ollama list` shows.
- [x] Stopping `opensearch` in the Aspire dashboard produces a readable "Search is unavailable" message, not a stack trace.
  *Checked 2026-10-03:* classic mode showed "Error: OpenSearch is unreachable at http://localhost:9210/: No connection
  could be made because the target machine actively refused it." Agentic mode answered that the paper search is
  unavailable. Neither showed a stack trace (see As built for why the classic text isn't reworded).
