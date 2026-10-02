# Phase 4: Ingestion pipeline and Hangfire job

**Goal:** a Hangfire recurring job, `arxiv-daily-ingestion` (weekdays 06:00 UTC), that fetches the day's cs.AI papers from arXiv, downloads and parses PDFs through docling-serve, upserts them into Postgres, chunks and embeds them, and indexes them into OpenSearch. Each run writes a summary row, and the job **fails visibly** when nothing useful happened.

**Size:** L · **Depends on:** phase 2 (search client, Jina); phase 1 (repository)

## Behaviour that must be preserved

From `services/arxiv/client.py`, `services/metadata_fetcher.py`, `services/pdf_parser/*`, `services/indexing/*` and `airflow/dags/arxiv_ingestion/*`.

**arXiv API**
- Query: `cat:{SearchCategory}`, plus ` AND submittedDate:[{from}0000+TO+{to}2359]` when dates are given (dates as `yyyyMMdd`). Params: `search_query`, `start`, `max_results = min(max, 2000)`, `sortBy=submittedDate`, `sortOrder=descending`.
- URL encoding: percent-encode everything **except** `:+[]`, so spaces become `%20`. Golden-test it against Python's `urlencode(..., quote_via=quote, safe=":+[]")`.
- At least `RateLimitDelaySeconds` (3 s) between arXiv requests. Timeout `TimeoutSeconds` (30).
- Atom parsing: `atom:entry` → `id` (last path segment, e.g. `2510.01234v1`), `title` and `summary` (trimmed, `\n` → space), `published`, `author/name`, `category@term`, and the `link[@type='application/pdf']@href` with `http://arxiv.org/` → `https://arxiv.org/`. An entry that fails to parse is skipped and logged, not fatal.

**PDF download**
- Cache path `{PdfCacheDir}/{arxivId with '/'→'_'}.pdf`. Reuse a cached file unless forced.
- Retries `DownloadMaxRetries` (3) with a delay of `DownloadRetryDelayBaseSeconds × (attempt+1)`. Stream to disk and delete partial files on failure.
- Concurrency: `MaxConcurrentDownloads` (5) downloads and `MaxConcurrentParsing` (1) parses. Parsing starts as soon as each download finishes.

**Parsing**
- Validation: an empty file → error. Size > `MaxFileSizeMb` or pages > `MaxPages` (30) → **skip** (stored as metadata only; on purpose). A missing `%PDF-` header → error.
- Docling options: OCR off, table structure on.
- **Section algorithm** (port 1:1): walk `texts[]` in order. The current section starts as `{title: "Content", content: ""}`. When `label ∈ {title, section_header}`, push the current section if its content isn't blank, then start a new one titled with the trimmed text. Otherwise append `text + "\n"`. After the loop, push the last non-blank section. Trim the content of every section.
- `raw_text` = Docling's plain-text export. `parser_used = "docling"`. `parser_metadata = {source: "docling", note: "Content extracted from PDF, metadata comes from arXiv API"}`. `references = []`.

**Storage**
- Upsert by `arxiv_id`. Parsed → content fields + `pdf_processed = true` + `pdf_processing_date = now`. Not parsed → `parser_metadata = {note: "PDF processing not available or failed"}`, with B23 applied.

**Chunking** (`TextChunker`, golden-tested against Python)
- Section-based first (when `Chunking:SectionBased`). Parse sections from a list of `{title|heading, content|text}`. Filter out empty sections, metadata titles (exact `content/header/authors/author/affiliation/email/arxiv/preprint/submitted/received/accepted`, any title under 5 characters, or a title under 20 characters containing one of those words), sections that duplicate the abstract (substring either way, or >80% word overlap when the abstract has >10 words), and short metadata-only content (<20 words with ≥2 of `@, arxiv:, university, institute, department, college, gmail.com, edu, ac.uk, preprint`).
- Header = `"{title}\n\nAbstract: {abstract}\n\n"`. Sections under `SectionMinWords` (100) are buffered and flushed when the next section has ≥100 words or the list ends. A flushed buffer whose words plus header words come to <200 is **merged into the previous chunk** (title `"{prev} + Combined"`). Otherwise it becomes a combined chunk titled with up to 3 section names, plus `" + N more"`. Sections of 100–800 words are one chunk: `header + "Section: {title}\n\n{content}"`. Sections over `SectionMaxWords` (800) are split by word chunking, with the header prepended to each part (title `"{title} (Part n)"`).
- Fallback word chunking: 600-word windows, step 500 (overlap 100), `start_char`/`end_char` = length of the space-joined prefix. A text under `MinChunkSize` words becomes one chunk (**B9**). Blank text → no chunks.
- Word splitting uses Unicode whitespace (`\S+`), matching Python's `str.split()`.

**Indexing**
- For each paper: delete its existing chunks (`replace_existing`), chunk it, embed passages in batches of 50, check that the counts match (a mismatch is an error for that paper), then bulk-index documents with `arxiv_id, paper_id, chunk_index, chunk_text, chunk_word_count, start_char, end_char, section_title, embedding_model = "jina-embeddings-v3", title, authors (", "-joined), abstract, categories, published_date, embedding` (+ C5 fields).

## Tasks

### 4.1 Parity fixtures (extend the phase-2 Python script)
- [ ] arXiv URLs for a few `(category, from, to, max_results)` cases.
- [ ] `TextChunker.chunk_paper` outputs (text, `chunk_index`, `word_count`, `section_title`, `start_char`, `end_char`) for: no sections (1,500-word text), a mix of small, medium and large sections, filtered metadata and abstract-duplicate sections. **Exclude** the B9/B10 paths from the Python fixtures, since Python crashes or corrupts there. Write those expectations by hand from the spec.
- [ ] Docling sections from Python `DoclingParser` for 1–2 real cached PDFs (≤30 pages), to compare with the docling-serve result (see 4.3).

### 4.2 arXiv client (`PaperPilot.Infrastructure/Arxiv`)
- [ ] `ArxivQueryBuilder.BuildUrl(...)`, golden-tested.
- [ ] `ArxivAtomParser.Parse(string xml)` using `XDocument` with the Atom, OpenSearch and arXiv namespaces. Test against a recorded feed fixture.
- [ ] `ArxivClient`: `FetchPapersAsync(maxResults, from, to)`, `FetchPaperByIdAsync(id)`, `DownloadPdfAsync(paper, force)`.
- [ ] **One shared rate limiter for all arXiv traffic** (API queries **and** PDF downloads): a `DelegatingHandler` around a `System.Threading.RateLimiting` limiter that allows one request start every 3 s (**B26**). Transfers can still overlap.
- [ ] HttpClient with its own retry policy for downloads (no standard handler, R1).

### 4.3 PDF parsing (`PaperPilot.Infrastructure/Pdf`)
- [ ] `PdfValidator`: file size, `%PDF-` header, page count through **PdfPig** (`PdfDocument.Open(path).NumberOfPages`). Returns `Valid | Skip(reason) | Invalid(reason)`.
- [ ] `DoclingServeClient.ConvertAsync(path)`: `POST {Docling:BaseUrl}/v1/convert/file`, multipart with `files`, `to_formats=json`, `to_formats=text`, `do_ocr=false`, `do_table_structure=true`, `image_export_mode=placeholder` (stops base64 images bloating the JSON), and a page range capped at `MaxPages`. **Check the field names against `/docs` of the pinned image.** Timeout `Docling:TimeoutSeconds` (600), one retry on connection failure only.
- [ ] Response: `status ∈ {success, partial_success}` is OK, otherwise `PdfParsingException(errors)`. Read `document.json_content.texts[].{label,text}` and `document.text_content`, and ignore everything else (R3).
- [ ] `DoclingSectionExtractor.Extract(JsonElement texts)`: the 1:1 algorithm above.
- [ ] Record one real docling-serve response into `tests/fixtures/docling/`. Compare its section titles with the Python Docling fixture from 4.1. Minor differences are acceptable across Docling versions; write down what you see.

### 4.4 Fetch service (`PaperPilot.Ingestion/PaperFetchService.cs`)
- [ ] `FetchAndStoreAsync(DateOnly from, DateOnly to)` → `FetchResult(PapersFetched, PdfsDownloaded, PdfsParsed, PapersStored, UpsertedPaperIds, Errors)`.
- [ ] Overlapping download and parse pipelines with two `SemaphoreSlim`s, as in Python. One paper's failure is recorded in `Errors` and never stops the others.
- [ ] Upsert through `PaperRepository` (B23).

### 4.5 Chunker and indexer
- [ ] `PaperPilot.Core/Indexing/TextChunker.cs`, a pure class configured by `ChunkingOptions`, with B9, B10 and B11 applied.
- [ ] `PaperPilot.Ingestion/HybridIndexer.cs`: `IndexPapersAsync(IReadOnlyCollection<Guid> paperIds, bool replaceExisting = true)` → `IndexResult(PapersProcessed, ChunksCreated, ChunksIndexed, EmbeddingsGenerated, Errors)`. It loads papers with `GetByIdsAsync` (**B13**). A paper with no content gets 0 chunks; that isn't an error.

### 4.6 Run history (N2)
- [ ] Entity and migration for `ingestion_runs`: `id, target_from, target_to, trigger (scheduled|manual), started_at, finished_at, status (running|succeeded|failed), papers_fetched, pdfs_downloaded, pdfs_parsed, papers_stored, chunks_created, chunks_indexed, embeddings_generated, errors (jsonb), index_doc_count_after, hangfire_job_id`.

### 4.7 The job (`PaperPilot.Ingestion/DailyIngestionJob.cs`)
- [ ] `RunAsync(DateOnly? from, DateOnly? to, string trigger, CancellationToken ct)`, with `[AutomaticRetry(Attempts = 2, DelaysInSeconds = [300, 300])]` and `[DisableConcurrentExecution(3600)]` (C9).
- [ ] **Target window (B25):** by default `to` = yesterday **relative to the actual run time in UTC**. `from` = the day after the last *succeeded* run's `target_to`, capped to 3 days back, so a Monday run covers Fri–Sun. If there's no prior run, use yesterday. Explicit dates override this. `Arxiv:MaxResults` applies per run.
- [ ] Steps, each logged and wrapped in an `Activity` (`ActivitySource "PaperPilot.Ingestion"`):
  1. **Setup:** `SELECT 1`, OpenSearch health + `EnsureIndex` + `EnsureRrfPipeline`, docling `/health`, Jina key present. Fail fast with a clear message.
  2. **Fetch:** `PaperFetchService.FetchAndStoreAsync`.
  3. **Index:** `HybridIndexer.IndexPapersAsync(fetch.UpsertedPaperIds)`.
  4. **Report:** write the `ingestion_runs` row and log a JSON summary, including total papers in the DB and index doc count and size (as Python's report did).
  5. **Cleanup:** delete cached PDFs older than 30 days from `Arxiv:PdfCacheDir` (**B14**).
- [ ] **Failure rule (B15):** after writing the run row, throw `IngestionFailedException` (the job turns red) when `papers_fetched > 0 && pdfs_parsed == 0`, or when papers had content but `chunks_indexed == 0`.
- [ ] Metrics (`Meter "PaperPilot.Ingestion"`): counters for papers fetched, parsed and indexed, and chunks indexed. They show up in the Aspire dashboard.

### 4.8 Worker host (`PaperPilot.Worker/Program.cs`)
- [ ] Hangfire: `UsePostgreSqlStorage(...)` against the `papers` connection with `SchemaName = "hangfire"`, `PrepareSchemaIfNecessary = true`, **`InvisibilityTimeout = 3h`** (R7). `AddHangfireServer(o => o.WorkerCount = 1)`. Dashboard at `/hangfire` (the default local-only authorization is fine).
- [ ] On startup: `IRecurringJobManager.AddOrUpdate<DailyIngestionJob>("arxiv-daily-ingestion", j => j.RunAsync(null, null, "scheduled", CancellationToken.None), "0 6 * * 1-5", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc })`.
- [ ] `POST /ingestion/run?from=yyyyMMdd&to=yyyyMMdd` → `BackgroundJob.Enqueue(...)` with trigger `manual` (N3). `GET /ingestion/runs?limit=20` (N2).
- [ ] Optional: `Hangfire.Console`, so step logs show inside the dashboard's job page.
- [ ] Also register `SearchIndexInitializer` (phase 2).

## Tests
- [ ] Unit: arXiv URL golden; Atom parser fixture; `PdfValidator` (build tiny PDFs in the test with PdfPig's `PdfDocumentBuilder`: 1 page OK, 31 pages → skip, non-PDF → invalid); `DoclingSectionExtractor` fixture; `TextChunker` golden fixtures plus hand-written B9/B10 cases; target-window logic (Mon after a Fri run → Fri..Sun; first run → yesterday); failure rule; cleanup (temp dir with old and new files).
- [ ] Integration: Postgres + OpenSearch containers, WireMock for the arXiv API, PDFs, docling-serve (fixture JSON) and Jina (deterministic vectors). Run `DailyIngestionJob.RunAsync` for a fixed date and assert DB rows, chunk count = `_count`, and the `ingestion_runs` row. Run it again and assert the chunk count is unchanged (idempotent).

## Done when
- [ ] Triggering `arxiv-daily-ingestion` from `/hangfire` for real succeeds. The `ingestion_runs` row has non-zero `pdfs_parsed` and `chunks_indexed`, and `_count` went up by `chunks_indexed`.
- [ ] Papers over 30 pages appear in `papers` with `pdf_processed = false` and no chunks.
- [ ] Stopping `docling` in the dashboard and triggering again turns the job **red** (after retries), with `status = failed` and errors recorded.
- [ ] Re-running the same date gives the same chunk count and no duplicates.
- [ ] `/ask` (phase 3) answers questions about the newly ingested papers.
