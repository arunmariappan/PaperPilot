# Phase 8: Parity check, CI, docs and cleanup

**Goal:** show that PaperPilot matches the Python system, apart from the documented changes; run all tests in CI; and make the repo usable from a clean clone.

**Size:** M · **Depends on:** phases 0–7

## Tasks

### 8.1 Parity check against the Python stack
- [x] `tests/fixtures/parity-queries.json`: about 15 search queries (mixed categories, `latest`, BM25 and hybrid), about 10 `/ask` questions, and about 20 agentic questions **labelled** in scope or out of scope (10 each, e.g. "How does BERT work?" vs "What is a dog?").
- [x] `tools/parity-check.cs` (a .NET 10 file-based app) with two modes:
  - `record --base http://localhost:8000` runs the query set against the **Python** API and saves the responses to JSON.
  - `compare --base http://localhost:8100 --recorded python.json` runs the same set against PaperPilot and writes `docs/parity-report.md`.
- [x] Run the stacks **one after the other**, not together (8 GB Docker limit). Before recording, re-seed PaperPilot's index from Python (`tools/seed-opensearch.cs`) so both use identical data. Both use the same host Ollama model with `think=false`.
- [x] Report criteria:

| Check | Expected |
|---|---|
| `/hybrid-search/` BM25: sequence of `(arxiv_id, chunk_index)` | identical |
| `/hybrid-search/` hybrid: top-k overlap | ≥ 0.8 Jaccard (embeddings are deterministic) |
| `/ask`: chunks used (retrieval) | identical; answers placed side by side for you to review (LLM text isn't deterministic) |
| `/ask-agentic`: guardrail decision on the labelled set | agreement with the labels ≥ Python's agreement |
| `/ask-agentic`: `sources` non-empty when answered | yes (B1) |
| Latency p50 for `/ask` (cache miss and hit) | same or better than Python |

- [x] **Every difference** in the report is either explained by a `B`/`C` ID in [behaviour-changes.md](behaviour-changes.md) or filed as a regression and fixed.

### 8.2 CI
- [x] Extend `.github/workflows/ci.yml`: integration tests on `ubuntu-latest` (Testcontainers needs Docker, which hosted runners have), NuGet cache, test-result and coverage artifacts (`coverlet.collector`).
- [x] Coverage targets (guidance, not a gate): Core ≥ 90%, Rag ≥ 80%, everything else ≥ 60%.
- [x] Optional manual workflow (`workflow_dispatch`): an `Aspire.Hosting.Testing` smoke test that boots the AppHost and checks `/api/v1/health`. It pulls large images, so it doesn't run on every push.

### 8.3 Docs
- [x] `README.md` quick start: prerequisites, setting secrets (`dotnet user-secrets set Parameters:jina-api-key ...`), `aspire run`, a table of URLs (Aspire dashboard, API `/docs`, Web, Hangfire `/hangfire`, OpenSearch, docling `/ui`, Langfuse), how to trigger ingestion, example `curl` calls.
- [x] Finish `CLAUDE.md`: commands, architecture, and every gotcha collected during the phases (R1 resilience pattern, docling-serve field names for the pinned tag, the Agent Framework API notes from the phase-5 spike, Langfuse attribute names, Hangfire invisibility timeout, memory numbers).
- [x] `docs/decisions.md`: README §1, plus anything decided along the way.
- [x] Keep `docs/plan/` and tick off completed items, or mark the plan as historical.

### 8.4 Cleanup
- [x] Zero analyzer warnings, `dotnet format --verify-no-changes` clean, no dead code or unused packages (`dotnet list package --outdated` / `--deprecated` / `--vulnerable`).
- [x] Secret scan: `git grep -nE "sk-lf-|pk-lf-|jina_[A-Za-z0-9]{10,}|[0-9]{8,}:[A-Za-z0-9_-]{30,}"` finds nothing.
- [ ] **Fresh-clone test:** clone into a new folder, set secrets, run `aspire run`, ingest one day, then ask a question. Fix anything the README missed.

### 8.5 Out of scope (record as follow-ups only)
- `aspire publish` to Docker Compose or Azure Container Apps, container images in GitHub Container Registry.
- Authentication on the API and Hangfire dashboard.
- Semantic (embedding-similarity) cache in place of exact-match.

## As built

- **Parity check** (`tools/parity-check.cs`, `tests/fixtures/parity-queries.json`, [parity report](../parity-report.md)):
  - **Same data without re-seeding.** The Python API ran against PaperPilot's OpenSearch: a compose override set
    `OPENSEARCH_HOST=http://host.docker.internal:9210`, and only `api`, `postgres` and `redis` were started
    (`docker compose up -d --no-deps`). Both stacks searched one index, so no copy could drift.
  - **Three modes:** `record`, `compare` (record PaperPilot, then report) and `report`, which rebuilds the report from
    two recordings after an explanation is added. Every difference has a key (`s04.overlap`, `a03.sources`,
    `g12.label`, `latency.miss`). The query file's `explanations` map a key, or `section.check` for a whole check, to
    the behaviour change behind it. A difference without one is printed as **unexplained**.
  - **Query set:** 15 searches, 10 `/ask` questions (each asked twice: a cache miss, then a hit) and 20 agentic
    questions, 10 labelled in scope and 10 out of scope.
  - **Results (2026-10-03): every check passes.**
    - BM25 order was identical for 8 of 8 queries, and hybrid overlap was 1.00 on all 7.
    - `/ask` used the same chunks for 10 of 10 questions.
    - Cache-miss p50 was 27.5 s against Python's 33.4 s; cache-hit p50 was 2 ms against 10 ms.
    - The guardrail agreed with all 20 labels on both stacks.
    - PaperPilot listed sources for 8 of 8 answered agentic questions, Python for none. That is B1, and the report's
      only difference.
  - **A regression it found.** The first PaperPilot run was slower on cache misses than Python. Ollama generated at
    the same speed for both (~7.3 tokens/s), but three requests had spent 2 s before reaching OpenSearch or Ollama.
    Windows resolves `localhost` to `::1` first, the containers and Ollama listen on `127.0.0.1` only, and a refused
    connection takes about 2 s there. `LoopbackConnect` (ServiceDefaults) now connects every factory `HttpClient`
    to `localhost` over IPv4 first. Its tests include an IPv4-only server reached in under a second.
- **CI** (`.github/workflows/ci.yml`): two jobs on `ubuntu-latest` sharing a NuGet cache (`NUGET_PACKAGES`, keyed on
  `global.json`, `Directory.Packages.props` and the project files).
  - *Build and unit tests*: restore, Release build (warnings are errors), `dotnet format --verify-no-changes`, unit
    tests.
  - *Integration tests*: Testcontainers starts Postgres, Redis and OpenSearch in the runner's Docker.
  - Both upload TRX results and a Cobertura file. Coverage uses `Microsoft.Testing.Extensions.CodeCoverage` with
    `coverage.config`, not `coverlet.collector`, which is a VSTest data collector and doesn't run on
    Microsoft.Testing.Platform. An XML comment in that file must not contain `--`: the tool then exits with code 5.
- **Smoke test** (`tests/PaperPilot.SmokeTests`, workflow *Smoke test*, `workflow_dispatch` only): boots the whole
  AppHost with `Aspire.Hosting.Testing` and waits for `api` to be healthy, which needs Postgres (after the
  migrations) and OpenSearch. It pulls about 10 GB of images.
- **Coverage** (line, unit and integration merged, 2026-10-03): Core 92%, Rag 99%, Api 91%, Infrastructure 91%,
  Ingestion 92%, ServiceDefaults 96%, Web 76%. Worker, MigrationService and AppHost are composition roots (79, 63 and
  254 lines) that no test loads; the ingestion job they host is in Ingestion, and the smoke test starts all three.
- **Cleanup** (2026-10-03):
  - Release build: 0 warnings with `TreatWarningsAsErrors` and `AnalysisLevel` `latest-recommended`. A one-off build
    with IDE0005, IDE0051, IDE0052 and IDE0060 raised to warnings found 8 unused `using`s and an ambiguous `cref`
    (fixed), and no unused members or parameters. Every central package version is referenced.
  - Packages: none vulnerable, none outdated at the top level. The only deprecated ones are
    `Microsoft.IdentityModel.*` 6.34 (marked *Legacy*), pulled into the test projects by WireMock.Net 2.18, its latest
    version.
  - Secret scan: the pattern above matches only test placeholders (`pk-lf-test`, `sk-lf-test`) and the `pk-lf-` /
    `sk-lf-` prefixes the AppHost uses to generate Langfuse keys. The whole history (`git log -p --all`) has no
    key-shaped match.
  - A flaky test: the four bUnit tests that wait for a WireMock response failed once on a cold run, because bUnit
    waits only 1 s by default. They now wait up to 10 s.
- **Docs:** the README is a quick start (prerequisites, the one required secret, URLs, ingestion, `curl` examples,
  optional features). `docs/decisions.md` collects the decisions, `CLAUDE.md` the commands and gotchas, and the plan
  stays as the record of how it was built.

## Done when
- [x] `docs/parity-report.md` is committed and every difference is explained.
  *Checked 2026-10-03:* 1 difference (`agentic.sources`, explained by B1), 0 unexplained.
- [x] CI is green with unit and integration tests.
  *Checked 2026-10-03:* CI green on every push from `ec5e028` (which added the integration job) to `a2977e5`.
- [ ] The fresh-clone test passes by following only the README.
- [ ] README §9 "Definition of done (whole project)" is fully checked.
