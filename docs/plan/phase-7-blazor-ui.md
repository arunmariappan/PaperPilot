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
- [ ] Blazor Web App with **Interactive Server** render mode (from phase 0). Add `ServiceDefaults`.
- [ ] `PaperPilotApiClient`, a typed `HttpClient` with `BaseAddress = "https+http://api"` (Aspire service discovery). **No standard resilience timeout** on this client (R1): streams and agentic calls can take minutes. Use `HttpCompletionOption.ResponseHeadersRead` for `/stream`.

### 7.2 API client
- [ ] `StreamAskAsync(AskRequest, CancellationToken)` → `IAsyncEnumerable<StreamEvent>`. Parse the response with `System.Net.ServerSentEvents.SseParser` and deserialize each `data` payload into `Metadata | Chunk | Done | Error` (snake_case JSON).
- [ ] `AskAgenticAsync`, `SubmitFeedbackAsync`, `GetModelsAsync`.

### 7.3 Components (`Components/Pages/Chat.razor` at `/`, plus pieces under `Components/Chat/`)
- [ ] `QuestionForm`, `AdvancedOptions`, `ExamplesList`, `AnswerView`, `SourcesList`, `ReasoningSteps`, `FeedbackBar`.
- [ ] `AnswerView` renders Markdown with **Markdig with raw HTML disabled** (`.DisableHtml()`), because LLM output must never inject markup or script. Sources render as `arXiv:{id}` links to the abs page, with a PDF link.
- [ ] Throttle re-renders while streaming: append tokens to a buffer and call `StateHasChanged` at most every ~50 ms.
- [ ] Keep it simple: one Q&A at a time, as in Gradio, with an optional collapsible list of earlier answers from this browser session (in memory only).
- [ ] Plain CSS (no component library), readable at phone width.

## Tests
- [ ] Unit: the SSE client parses a recorded `/stream` body (metadata, chunks, done; and the error variant); categories text → list (trimmed, empties dropped).
- [ ] Optional component tests with **bUnit**: `AnswerView` doesn't render raw `<script>`; `FeedbackBar` hides without a `trace_id`.

## Done when
- [ ] `http://localhost:8101` streams a classic answer token by token, with sources, and **Stop** cancels it.
- [ ] Agentic mode shows the answer, reasoning steps and sources. 👍 creates a score in Langfuse (when enabled).
- [ ] Each example fills the form correctly. The model dropdown lists your installed Ollama models.
- [ ] Stopping `opensearch` in the Aspire dashboard produces a readable "Search is unavailable" message, not a stack trace.
