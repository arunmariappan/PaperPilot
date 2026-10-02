# Phase 8: Parity check, CI, docs and cleanup

**Goal:** show that PaperPilot matches the Python system, apart from the documented changes; run all tests in CI; and make the repo usable from a clean clone.

**Size:** M · **Depends on:** phases 0–7

## Tasks

### 8.1 Parity check against the Python stack
- [ ] `tests/fixtures/parity-queries.json`: about 15 search queries (mixed categories, `latest`, BM25 and hybrid), about 10 `/ask` questions, and about 20 agentic questions **labelled** in scope or out of scope (10 each, e.g. "How does BERT work?" vs "What is a dog?").
- [ ] `tools/parity-check.cs` (a .NET 10 file-based app) with two modes:
  - `record --base http://localhost:8000` runs the query set against the **Python** API and saves the responses to JSON.
  - `compare --base http://localhost:8100 --recorded python.json` runs the same set against PaperPilot and writes `docs/parity-report.md`.
- [ ] Run the stacks **one after the other**, not together (8 GB Docker limit). Before recording, re-seed PaperPilot's index from Python (`tools/seed-opensearch.cs`) so both use identical data. Both use the same host Ollama model with `think=false`.
- [ ] Report criteria:

| Check | Expected |
|---|---|
| `/hybrid-search/` BM25: sequence of `(arxiv_id, chunk_index)` | identical |
| `/hybrid-search/` hybrid: top-k overlap | ≥ 0.8 Jaccard (embeddings are deterministic) |
| `/ask`: chunks used (retrieval) | identical; answers placed side by side for you to review (LLM text isn't deterministic) |
| `/ask-agentic`: guardrail decision on the labelled set | agreement with the labels ≥ Python's agreement |
| `/ask-agentic`: `sources` non-empty when answered | yes (B1) |
| Latency p50 for `/ask` (cache miss and hit) | same or better than Python |

- [ ] **Every difference** in the report is either explained by a `B`/`C` ID in [behaviour-changes.md](behaviour-changes.md) or filed as a regression and fixed.

### 8.2 CI
- [ ] Extend `.github/workflows/ci.yml`: integration tests on `ubuntu-latest` (Testcontainers needs Docker, which hosted runners have), NuGet cache, test-result and coverage artifacts (`coverlet.collector`).
- [ ] Coverage targets (guidance, not a gate): Core ≥ 90%, Rag ≥ 80%, everything else ≥ 60%.
- [ ] Optional manual workflow (`workflow_dispatch`): an `Aspire.Hosting.Testing` smoke test that boots the AppHost and checks `/api/v1/health`. It pulls large images, so it doesn't run on every push.

### 8.3 Docs
- [ ] `README.md` quick start: prerequisites, setting secrets (`dotnet user-secrets set Parameters:jina-api-key ...`), `aspire run`, a table of URLs (Aspire dashboard, API `/docs`, Web, Hangfire `/hangfire`, OpenSearch, docling `/ui`, Langfuse), how to trigger ingestion, example `curl` calls.
- [ ] Finish `CLAUDE.md`: commands, architecture, and every gotcha collected during the phases (R1 resilience pattern, docling-serve field names for the pinned tag, the Agent Framework API notes from the phase-5 spike, Langfuse attribute names, Hangfire invisibility timeout, memory numbers).
- [ ] `docs/decisions.md`: README §1, plus anything decided along the way.
- [ ] Keep `docs/plan/` and tick off completed items, or mark the plan as historical.

### 8.4 Cleanup
- [ ] Zero analyzer warnings, `dotnet format --verify-no-changes` clean, no dead code or unused packages (`dotnet list package --outdated` / `--deprecated` / `--vulnerable`).
- [ ] Secret scan: `git grep -nE "sk-lf-|pk-lf-|jina_[A-Za-z0-9]{10,}|[0-9]{8,}:[A-Za-z0-9_-]{30,}"` finds nothing.
- [ ] **Fresh-clone test:** clone into a new folder, set secrets, run `aspire run`, ingest one day, then ask a question. Fix anything the README missed.

### 8.5 Out of scope (record as follow-ups only)
- `aspire publish` to Docker Compose or Azure Container Apps, container images in GitHub Container Registry.
- Authentication on the API and Hangfire dashboard.
- Semantic (embedding-similarity) cache in place of exact-match.

## Done when
- [ ] `docs/parity-report.md` is committed and every difference is explained.
- [ ] CI is green with unit and integration tests.
- [ ] The fresh-clone test passes by following only the README.
- [ ] README §9 "Definition of done (whole project)" is fully checked.
