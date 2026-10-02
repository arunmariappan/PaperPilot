# Phase 2: Search foundation

**Goal:** PaperPilot creates and owns the chunk index and the RRF pipeline, embeds queries with Jina, and serves `POST /api/v1/hybrid-search/` and `GET /api/v1/health`. The index is seeded with the Python stack's data, so search results can be compared on identical data before ingestion is ported.

**Size:** M · **Depends on:** phase 1

## Behaviour that must be preserved

From `services/opensearch/*` and `routers/hybrid_search.py`:
- **One index**, `{IndexName}-{ChunkIndexSuffix}` = `arxiv-papers-chunks`. The mapping is `dynamic: strict`, 1 shard, 0 replicas, `index.knn: true`, with analyzers `standard_analyzer` (standard + `_english_` stopwords) and `text_analyzer` (standard tokenizer, lowercase/stop/snowball). Fields: `chunk_id, arxiv_id, paper_id, chunk_index, chunk_text (+keyword), chunk_word_count, start_char, end_char, embedding (knn_vector 1024, hnsw, cosinesimil, nmslib, ef_construction 512, m 16), title (+keyword), authors (+keyword), abstract, categories, published_date, section_title, embedding_model, created_at, updated_at`. Copy `index_config_hybrid.py` into `chunks-index.json`, filling `dimension` and `space_type` from options (B11).
- **RRF search pipeline** `hybrid-rrf-pipeline`: `phase_results_processors: [{ "score-ranker-processor": { "combination": { "technique": "rrf", "rank_constant": 60 } } }]`.
- **BM25 query** (`QueryBuilder`, chunk mode only; the paper-level mode is dead code and isn't ported):
  - `bool.must = [multi_match { query, fields: ["chunk_text^3","title^2","abstract^1"], type: best_fields, operator: or, fuzziness: AUTO, prefix_length: 2 }]`, or `match_all` when the query is blank.
  - `bool.filter = [terms { categories }]` when categories are given.
  - `size`, `from`, `track_total_hits: true`, `_source: { excludes: ["embedding"] }`.
  - Highlight: `chunk_text` (150/2 fragments), `title` (0/0), `abstract` (150/1), all with `<mark>` tags and `require_field_match: false`.
  - Sort `[{published_date: desc}, "_score"]` when `latest` is set **or** the query is blank. Otherwise relevance.
- **Hybrid query:** `{ hybrid: { queries: [ <bm25 bool query built with size*multiplier>, { knn: { embedding: { vector, k: size*multiplier } } } ] } }`, `size`, `_source` and `highlight` from the BM25 body, run with `?search_pipeline=hybrid-rrf-pipeline`. **Hybrid ignores `from` and `latest`**, as in Python. Note that in the OpenAPI description. `min_score` filters hits afterwards, and for hybrid, `total` is the number of hits *after* filtering.
- **Mode selection:** hybrid only when `use_hybrid` is true **and** a query embedding exists. If Jina fails, fall back to BM25 and report `search_mode: "bm25"`.
- `/hybrid-search/` returns **503** when the cluster health isn't green or yellow.

## Tasks

### 2.1 Parity fixtures (in the Python repo)
- [x] Add `scripts/dump_parity_fixtures.py` to the Python repo. It writes JSON for:
  - `QueryBuilder(...).build()` over a matrix of cases: query blank or present, categories none or `["cs.AI","cs.LG"]`, latest true or false, size/from variations.
  - The hybrid body from `_search_hybrid_native` (patch `client.search` to capture the body, using a 4-dimensional fake vector).
  - `ARXIV_PAPERS_CHUNKS_MAPPING` and `HYBRID_RRF_PIPELINE`.
- [x] Copy the output to PaperPilot `tests/fixtures/python-parity/search/`. Phase 4 adds chunker and arXiv fixtures with the same script.

### 2.2 `QueryBuilder` (`PaperPilot.Core/Search`)
- [x] A pure static builder that returns `JsonObject`: `BuildBm25(SearchQuery)` and `BuildHybrid(SearchQuery, float[] embedding, int multiplier)`.
- [x] Golden tests: for every fixture, `JsonNode.DeepEquals(expected, actual)`.

### 2.3 `OpenSearchClient` (`PaperPilot.Infrastructure/Search`)
A typed `HttpClient` with base address `OpenSearch:Host`. It uses the standard resilience handler with a 2-minute total timeout for bulk calls. See R1 for how to override it.

| Method | Request | Notes |
|---|---|---|
| `HealthAsync` | `GET /_cluster/health` | true for green or yellow |
| `GetIndexStatsAsync` | `HEAD /{index}`, `GET /{index}/_stats` | `document_count`, `deleted_count`, `size_in_bytes` |
| `EnsureIndexAsync(force)` | `HEAD`, optional `DELETE`, `PUT /{index}` | treat `resource_already_exists_exception` as "exists" (race between Api and Worker) |
| `EnsureRrfPipelineAsync(force)` | `GET /_search/pipeline/{id}`, then `PUT` if 404 | **B12** |
| `SearchAsync(SearchQuery, float[]? embedding)` | `POST /{index}/_search[?search_pipeline=]` | returns `SearchResult(Total, IReadOnlyList<ChunkHit>)`; throws on failure (**B6**) |
| `BulkIndexChunksAsync(IEnumerable<ChunkDocument>)` | `POST /_bulk?refresh=wait_for` (NDJSON) | `_id = {arxiv_id}:{chunk_index}` (**C5**); returns `(success, failed)` from the per-item results |
| `DeletePaperChunksAsync(arxivId)` | `POST /{index}/_delete_by_query?refresh=true` with `term arxiv_id` | |
| `GetChunksByPaperAsync(arxivId)` | `term` query, size 1000, sort `chunk_index` asc | |
| `CountAsync`, `CountUniquePapersAsync` | `GET /{index}/_count`, cardinality agg on `arxiv_id` | used by health and the ingestion report |

- [x] `ChunkHit` holds `ChunkId` (`_id`), `ArxivId`, `Title`, `Authors`, `Abstract`, `PublishedDate`, `ChunkText`, `ChunkIndex`, `SectionTitle`, `Score`, `Highlights`.
- [x] Throw `SearchUnavailableException` for connection or health failures and `SearchQueryException` (carrying OpenSearch's `error.reason`) for 4xx/5xx responses.

### 2.4 Index bootstrap
- [x] `SearchIndexInitializer : IHostedService` in both **Api** and **Worker**: health check, `EnsureIndexAsync(force: false)`, `EnsureRrfPipelineAsync(force: false)`, then log the document count. If OpenSearch is down, log a warning and keep running, as Python's lifespan did.

### 2.5 Jina embeddings (`PaperPilot.Infrastructure/Embeddings`)
- [x] `IEmbeddingService` with `EmbedQueryAsync(string)` → `float[]` and `EmbedPassagesAsync(IReadOnlyList<string>, int batchSize = 50)` → `IReadOnlyList<float[]>`.
- [x] Request body exactly as Python: `{ model: "jina-embeddings-v3", task: "retrieval.query" | "retrieval.passage", dimensions: 1024, late_chunking: false, embedding_type: "float", input: [...] }`, sent as a bearer-token `POST https://api.jina.ai/v1/embeddings`.
- [x] **429 handling:** two named clients, `jina-query` and `jina-passages`, each with **its own** resilience handler and the standard handler removed (R1). Retry only on 429, honour `Retry-After`, otherwise exponential 5 s → 10 s → 20 s … capped at 60 s. Max retries: **2** for queries (interactive, BM25 fallback exists) and **6** for passages. The total timeout must cover the full back-off (about 5 minutes for passages).
- [x] A missing `Jina:ApiKey` throws `EmbeddingUnavailableException` immediately. Callers fall back (query) or fail the paper (passages).
- [x] Check that the number of returned vectors matches the number of inputs, and that each has `VectorDimension` floats.

### 2.6 `/api/v1/hybrid-search/` (`Api/Endpoints/SearchEndpoints.cs`)
- [x] `POST`, `HybridSearchRequest` → `SearchResponse`. Order: health (503) → optional embed with BM25 fallback → `SearchAsync` → map hits.
- [x] Hit mapping (**B7**): `section_name = SectionTitle`, `pdf_url = ArxivId.ToPdfUrl(ArxivId)`, `authors` is the comma-joined string as stored.
- [x] Errors: `SearchUnavailableException` → 503, `SearchQueryException` → 500 with detail. Validation → 400 (C1).
- [x] ASP.NET routing accepts both `/hybrid-search` and `/hybrid-search/`. Add a test for both.

### 2.7 `/api/v1/health`
- [x] `IHealthCheck` implementations: `PostgresHealthCheck` (`SELECT 1` through `DbContext`), `OpenSearchHealthCheck` (message `Index 'arxiv-papers-chunks' with N documents`), `OllamaHealthCheck` (`GET {endpoint}/api/version`, message `Ollama service is running`). Tag them `api`.
- [x] Register them for the Aspire `/health` endpoint too, so the dashboard shows real dependency health.
- [x] `/api/v1/health` runs the `api`-tagged checks and maps the report to the Python shape `{ status: ok|degraded, version, environment, service_name, services: { database, opensearch, ollama: { status: healthy|unhealthy, message } } }`. It always returns 200, as Python did.

### 2.8 Seed tool (N4)
- [x] `tools/seed-opensearch.cs`, a .NET 10 file-based app (`dotnet run tools/seed-opensearch.cs -- --source http://localhost:9200 --target http://localhost:9210`). It scroll-reads `arxiv-papers-chunks` from the Python stack **including `embedding`** and bulk-writes into PaperPilot with `_id = {arxiv_id}:{chunk_index}`.
- [x] Usage note for `CLAUDE.md`: start **only** the Python OpenSearch (`docker compose up -d opensearch` in the Python repo), seed, then stop it.

## As built

- **Parity result.** Running the Python search code against PaperPilot's seeded cluster gives the same hits *and scores* as PaperPilot's API for all five queries, BM25 and hybrid. Against the Python stack's own index, BM25 totals match but scores and the order of near-ties differ: that index still counts 244 deleted chunks (from earlier re-ingestion) in its term statistics. Phase 8 must either run `POST /arxiv-papers-chunks/_forcemerge?only_expunge_deletes=true` on the Python index first, or compare as above.
- **Fixture script:** `scripts/dump_parity_fixtures.py` in the Python repo (left uncommitted there). It writes 36 BM25 cases (blank, whitespace and text queries; no, some and empty categories; latest on/off; two page sizes), 3 hybrid cases, the mapping and the pipeline.
- **The resilience helper moved** from ServiceDefaults to `PaperPilot.Infrastructure.Http`, where the HTTP clients are registered. It now also has `ReplaceStandardResilienceHandler` and `WithoutResilience` (for health probes).
- **OpenSearch client resilience:** standard handler with 2 retries 250 ms apart, 60 s per attempt, 2 min total. On Windows a refused connection takes about 2 s per attempt, so an unreachable cluster becomes a 503 after about 6.5 s (instantly on Linux).
- **Jina:** every failure (no key, non-429 error, timeout, malformed or short response) surfaces as `EmbeddingUnavailableException`. `dimensions` comes from `OpenSearch:VectorDimension`. Vectors are ordered by the response's `index`.
- **Validation:** .NET 10 minimal-API validation does cover request types from `PaperPilot.Core`. Error keys are converted to the snake_case wire names (`top_k`, not `TopK`).
- **Health:** the Ollama check reports `Degraded` on failure, so Aspire doesn't mark the API unhealthy when only Ollama is down; `/api/v1/health` still shows it as `unhealthy`.
- **API docs:** `/docs` (Scalar) and `/openapi/v1.json` are live from this phase (C11). OpenAPI lists the path as `/api/v1/hybrid-search`.
- **Seed tool** also sets `chunk_id` on each copied document. It needs `#:property PublishAot=false`, because file-based apps default to AOT analysis and the repo treats warnings as errors.

## Tests
- [x] Unit: QueryBuilder golden fixtures, mapping JSON equals the Python fixture once dimension and space type are filled in, hit mapping (B7), and Jina retry using WireMock (429 + `Retry-After: 1`, then 200, succeeds; three 429s on a query throws after 2 retries; a missing key throws without an HTTP call).
- [x] Integration (Testcontainers `opensearchproject/opensearch:2.19.0` with security disabled): `EnsureIndex` and `EnsureRrfPipeline` are idempotent, bulk-indexing 3 chunks with random 1024-dim vectors works, BM25 finds them by keyword, hybrid returns them through the pipeline, and delete-by-query removes them.
- [x] API (`WebApplicationFactory` + the container): 200 with the right shape, 400 on an empty query, 503 when OpenSearch is unreachable, trailing slash accepted, and `/api/v1/health` shape.

## Done when
- [x] The index has been seeded from the Python stack, and `GET localhost:9210/arxiv-papers-chunks/_count` matches the Python index.
- [x] For 5 fixed queries with `use_hybrid: false`, PaperPilot and Python `/hybrid-search/` return the same `(arxiv_id, chunk_index)` sequence. With `use_hybrid: true`, `search_mode` is `hybrid` and the top results overlap substantially.
- [x] `GET localhost:8100/api/v1/health` reports database, opensearch and ollama as healthy.
