# Phase 5: Agentic RAG with Microsoft Agent Framework

**Goal:** `POST /api/v1/ask-agentic` runs the guardrail → retrieve → grade → (rewrite → retrieve)* → generate graph as a Microsoft Agent Framework workflow. It keeps every non-raising LLM fallback and returns `AgenticAskResponse` with sources, reasoning steps and a `trace_id` usable with `/feedback`.

**Size:** L · **Depends on:** phase 3 (`IChatClient`, `PaperRetriever`, tracing)

## Behaviour that must be preserved

From `services/agents/**` and `routers/agentic_ask.py`:

| Setting | Value |
|---|---|
| `max_retrieval_attempts` | 2 |
| `guardrail_threshold` | 60 (continue when `score >= 60`) |
| Temperatures | guardrail 0.0, grading 0.0, rewrite 0.3, generate 0.0 |
| `top_k` / `use_hybrid` / model | from the request (**B2**); defaults 3 / true / `Ollama:Model` |

| Node | Behaviour | Non-raising fallback |
|---|---|---|
| guardrail | `GUARDRAIL_PROMPT` → structured `GuardrailScoring { score: 0..100, reason }` | `score = 50`, reason "LLM validation failed, using conservative default: …". With threshold 60 this routes to **out of scope**; keep that. |
| out_of_scope | Fixed message, verbatim from `out_of_scope_node.py`, including `Your question: '{question}'` | none (no LLM) |
| retrieve | `attempts += 1`, then retrieve with the **current** query (original, or rewritten after a rewrite) | B3: BM25 if embedding fails |
| grade_documents | No context → rewrite. Otherwise `GRADE_DOCUMENTS_PROMPT` → `GradeDocuments { binary_score: "yes" or "no", reasoning }` | heuristic `context.Trim().Length > 50`, reasoning "Fallback heuristic (LLM failed): …" |
| rewrite_query | `REWRITE_PROMPT` with the **original** question → `QueryRewriteOutput { rewritten_query, reasoning }`, rejected if blank | `"{original} research paper arxiv machine learning"` |
| generate_answer | `GENERATE_ANSWER_PROMPT` with context (or `"No relevant documents found."`) | `"I apologize, but I encountered an error while generating the answer: {e}\n\nPlease try again or rephrase your question."` |
| max attempts | Fixed message, verbatim from `retrieve_node.py` ("I apologize, but I couldn't find relevant research papers after {n} attempts. …") | none |

- **Reasoning steps:** keep the strings word for word: `Validated query scope (score: {s}/100)`, `Retrieved documents ({n} attempt(s))`, `Graded documents ({k} relevant)`, `Rewritten query for better results`, and as the last step `Generated answer from context`. Python appended that last one even for out-of-scope and max-attempts endings. PaperPilot uses `Responded as out of scope`, `Stopped after {n} retrieval attempts` or `Search was unavailable` for those (**B28**).
- **Errors:** an empty or whitespace query → 400 (C1, C13; Python raised `ValueError` → 422). Anything unexpected → 500 with detail.
- **Tracing:** root span `agentic_rag_request` with metadata `{env, service: "agentic_rag", top_k, use_hybrid, model}`, user `api_user`. Child spans `guardrail_validation`, `document_retrieval_initiation`, `document_grading`, `query_rewriting`, `answer_generation`, each with the same input/output payloads Python logged. `trace_id` in the response.

## Fixes applied in this phase
B1 (sources filled in), B2 (request options honoured, real `chunks_used` and `search_mode`), B3 (BM25 fallback), B4 (clean context formatting), B5 (no wasted rewrite), B6 (search outage → explicit answer), C10 (direct retrieval, no synthetic tool call), and two new ones:
- **B27:** grading and answer generation use the user's **original** question. Python used the latest message, so after a rewrite the user got an answer to the *rewritten* question. The rewritten query is still used for retrieval.
- **B28:** the final reasoning step reflects how the run actually ended (see above).

## Target workflow

```mermaid
flowchart TD
    S([start]) --> G[GuardrailExecutor]
    G -- "score ≥ threshold" --> R[RetrieveExecutor]
    G -- "score < threshold" --> O[OutOfScopeExecutor]:::term
    R -- "search unavailable" --> U[SearchUnavailableExecutor]:::term
    R --> GR[GradeExecutor]
    GR -- "relevant" --> GEN[GenerateExecutor]:::term
    GR -- "not relevant, attempts < max" --> RW[RewriteExecutor]
    GR -- "not relevant, attempts = max" --> M[MaxAttemptsExecutor]:::term
    RW --> R
    classDef term fill:#eee,stroke:#999
```

Terminal executors yield the final `AgentRunState` as the workflow output.

## Tasks

### 5.1 Spike (R4), half a day, before anything else
- [ ] A throwaway console app (`dotnet run spike.cs`) with `Microsoft.Agents.AI.Workflows`. It has three executors passing a record, a conditional edge, a loop back edge, and a terminal executor that yields output. It runs in-process and reads the output event.
- [ ] Settle these and write them in `CLAUDE.md`: the executor base type and handler signature, how conditional edges are declared (`AddEdge(source, target, condition)` or a switch), how output is yielded and read, whether a built `Workflow` can be **reused** across concurrent runs (if not, build one per request), how cancellation flows, and what OTel spans the framework emits by itself.

### 5.2 Types (`PaperPilot.Rag/Agentic`)
- [ ] `AgenticRagOptions` (bound from `Agentic` config): `MaxRetrievalAttempts`, `GuardrailThreshold`, temperatures.
- [ ] `AgentRunState` (immutable record, updated with `with`): `OriginalQuestion`, `CurrentQuery`, `Model`, `TopK`, `UseHybrid`, `Categories`, `Guardrail?`, `RetrievalAttempts`, `LastRetrieval?` (`RetrievalResult`), `Gradings` (list), `RewrittenQuery?`, `Answer?`, `Ending?` (`Answered | OutOfScope | MaxAttempts | SearchUnavailable`).
- [ ] Structured-output records: `GuardrailScoring`, `GradeDocuments`, `QueryRewriteOutput`, `GradingResult`.
- [ ] Prompts as embedded `.txt` files, copied verbatim: `guardrail.txt`, `grade_documents.txt`, `rewrite.txt`, `generate_answer.txt`. Use `{question}` / `{context}` placeholders with simple replacement (no `string.Format`, so braces in paper text can't break anything).
- [ ] `AgentContextFormatter.Format(RetrievalResult)` → `[n] arXiv:{id} — {title}\n{chunk_text}` blocks (B4).

### 5.3 Executors (`PaperPilot.Rag/Agentic/Executors`)
- [ ] One class per node in the table, plus `MaxAttemptsExecutor` and `SearchUnavailableExecutor`. Each one starts its named `Activity`, calls the LLM through `IChatClient.GetResponseAsync<T>(prompt, options)` (structured output) or `PaperRetriever`, applies its fallback on **any** exception or invalid value (e.g. a score outside 0–100), and returns the updated state.
- [ ] Executors are stateless. Services come from DI, and per-request values come from the state.

### 5.4 Workflow and service
- [ ] `AgenticRagWorkflowFactory.Build()` wires the edges from the diagram with `WorkflowBuilder`.
- [ ] `AgenticRagService : IAgenticRagService`, method `AskAsync(AskRequest, CancellationToken)` → `AgenticAskResponse`:
  - Validate the query, build the initial state (B2 defaults), start `agentic_rag_request`, run the workflow in-process, and read the output state.
  - `answer` = `state.Answer`. `sources` = de-duplicated `ArxivId.ToPdfUrl` from `LastRetrieval` when the ending is `Answered`, otherwise `[]` (B1). `chunks_used` = number of chunks in `LastRetrieval` (B2). `search_mode` = `LastRetrieval.SearchMode`, or the requested mode if retrieval never ran. `reasoning_steps` per the rules above. `retrieval_attempts`, `trace_id`.
- [ ] Keep the Agent Framework types behind `IAgenticRagService`, so a framework API change only touches `PaperPilot.Rag/Agentic`.

### 5.5 Endpoint
- [ ] `POST /api/v1/ask-agentic` → `IAgenticRagService.AskAsync`. The input contract is the same `AskRequest`.

## Tests
- [ ] Unit (scripted `FakeChatClient` that routes on the prompt's first line, plus a fake `IPaperRetriever`):
  - out of scope (score 20) → out-of-scope message, 0 attempts, retriever never called, last step `Responded as out of scope`
  - relevant on the first try → 1 attempt, sources filled in, `chunks_used` = retrieved count
  - irrelevant twice → exactly **one** rewrite call (B5), 2 attempts, max-attempts message
  - irrelevant then relevant → second retrieval uses the rewritten query, and the generate prompt contains the **original** question (B27)
  - each LLM failure → its fallback (guardrail 50 → out of scope; grading heuristic; rewrite keywords; generate error text)
  - embedding failure → BM25 (B3); search outage → `SearchUnavailable` ending
  - request `top_k: 5`, `model: "x"` reach the retriever and `ChatOptions.ModelId` (B2)
  - reasoning-step strings match exactly
- [ ] Integration: `/ask-agentic` with WireMock standing in for Ollama (scripted JSON for each prompt): 200 shape, `trace_id` is 32 hex characters, 400 on an empty query.

## Done when
- [ ] "What are transformer architectures?" → in scope, relevant, answer with `[arXiv:…]` citations, non-empty `sources`.
- [ ] "What is 2+2?" → out-of-scope message, no retrieval.
- [ ] A query with no matching papers → one rewrite, then the max-attempts message.
- [ ] The Aspire dashboard shows the node spans in order, and Langfuse (when enabled) shows the same tree with each LLM call as a generation.
- [ ] `/feedback` with the returned `trace_id` attaches a score in Langfuse.
