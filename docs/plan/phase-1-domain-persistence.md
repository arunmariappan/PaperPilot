# Phase 1: Domain, options and persistence

**Goal:** typed configuration, the domain model, the public API contracts, and the `papers` table created by an EF Core migration.

**Size:** S · **Depends on:** phase 0

## Tasks

### 1.1 Options (`PaperPilot.Core/Options`)
- [x] One class per section, as in README §5: `AppOptions`, `ArxivOptions`, `PdfParserOptions`, `DoclingOptions`, `ChunkingOptions`, `OpenSearchOptions`, `JinaOptions`, `OllamaOptions`, `CacheOptions`, `LangfuseOptions`, `TelegramOptions`. Copy the Python defaults exactly (e.g. `Arxiv.MaxResults = 15`, `Arxiv.RateLimitDelaySeconds = 3.0`, `Chunking.ChunkSize = 600`).
- [x] DataAnnotations for the ranges and rules that Python enforced or assumed: `Chunking.OverlapSize < ChunkSize` (Python raised `ValueError`), `OpenSearch.VectorDimension > 0`, `Ollama.TimeoutSeconds >= 1`.
- [x] `OpenSearchOptions.ChunkIndexName => $"{IndexName}-{ChunkIndexSuffix}"` (= `arxiv-papers-chunks`).
- [x] An `AddPaperPilotOptions(this IHostApplicationBuilder)` extension that binds every section with `.ValidateDataAnnotations().ValidateOnStart()`.
- [x] `Jina.ApiKey`, `Telegram.BotToken` and `Langfuse.*Key` are **not** `[Required]`. Python ran without them, and only the features that need them degrade. Log one warning at startup for each missing key. *(Telegram and Langfuse keys are only reported when their feature is enabled.)*

### 1.2 Domain (`PaperPilot.Core/Domain`)
- [x] `ArxivPaper` record: `ArxivId`, `Title`, `Authors`, `Abstract`, `Categories`, `PublishedDate` (`DateTimeOffset`), `PdfUrl`.
- [x] `PaperSection(string Title, string Content, int Level = 1)`, `PdfContent(IReadOnlyList<PaperSection> Sections, string RawText, string ParserUsed, IReadOnlyDictionary<string, object?> Metadata)`.
- [x] `Paper` entity: `Id` (Guid), `ArxivId`, `Title`, `Authors` (`List<string>`), `Abstract`, `Categories` (`List<string>`), `PublishedDate`, `PdfUrl`, `RawText?`, `Sections?` (`List<PaperSection>`), `References?` (`List<string>`), `ParserUsed?`, `ParserMetadata?` (`JsonDocument` or `Dictionary<string, object?>`), `PdfProcessed`, `PdfProcessingDate?`, `CreatedAt`, `UpdatedAt`.
- [x] **`ArxivId` static helper (B8):** `StripVersion("2510.01234v2") == "2510.01234"`, `StripVersion("solv-int/9901001v1") == "solv-int/9901001"`, `ToPdfUrl(id) => $"https://arxiv.org/pdf/{StripVersion(id)}.pdf"`, `ToAbsUrl(id)`. Every other piece of code uses these helpers instead of formatting URLs itself.

### 1.3 API contracts (`PaperPilot.Core/Contracts`)
Records, snake_case on the wire through `JsonNamingPolicy.SnakeCaseLower`:
- [x] `AskRequest(string Query, int TopK = 3, bool UseHybrid = true, string? Model = null, IReadOnlyList<string>? Categories = null)`, with `[StringLength(1000, MinimumLength = 1)]`, `[Required]` (which already rejects whitespace-only strings, so it implements **C13** without a custom attribute), and `[Range(1, 10)]`. `Model` is nullable because a record can't read configuration; the services fall back to `Ollama:Model` (Python used a `default_factory` for the same thing).
- [x] `AskResponse(Query, Answer, Sources, ChunksUsed, SearchMode)`, and `AgenticAskResponse : AskResponse` + `ReasoningSteps`, `RetrievalAttempts`, `TraceId?`.
- [x] `FeedbackRequest(TraceId, [Range(-1, 1)] Score, [StringLength(1000)] Comment?)`, `FeedbackResponse(Success, Message)`.
- [x] `HybridSearchRequest(Query [1..500], Size = 10 [1..100], [JsonPropertyName("from")] From = 0, Categories?, LatestPapers = false, UseHybrid = true, MinScore = 0.0)`.
- [x] `SearchHit`, `SearchResponse` (with `[JsonPropertyName("from")]`, `SearchMode?`, `Error?`), `HealthResponse`, `ServiceStatus`.
- [x] Enable .NET 10 minimal-API validation (`builder.Services.AddValidation()`). Invalid requests return 400 `ProblemDetails` (C1).

### 1.4 EF Core (`PaperPilot.Infrastructure/Persistence`)
- [x] Packages: `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions` (`UseSnakeCaseNamingConvention()`, so the columns read like the Python table), `Microsoft.EntityFrameworkCore.Design` (MigrationService only).
- [x] `PaperPilotDbContext` with `DbSet<Paper> Papers`.
- [x] `PaperConfiguration`: table `papers`, unique index on `arxiv_id`, `jsonb` for `sections` (EF Core 10 complex collection with `ToJson`) and `parser_metadata` (`JsonDocument`), native `text[]` for `authors`, `categories` and `references`, `timestamptz` everywhere (B21), and `created_at`/`updated_at` set in `SaveChangesAsync` (an interceptor or an override).
- [x] `PaperRepository` (scoped). Port everything in `repositories/paper.py`: `GetByArxivIdAsync`, `GetByIdAsync`, `GetAllAsync(limit, offset)`, `CountAsync`, `GetProcessedAsync`, `GetUnprocessedAsync`, `GetWithRawTextAsync`, `GetProcessingStatsAsync`, `UpsertAsync(PaperUpsert)`. Add `GetByIdsAsync(IReadOnlyCollection<Guid>)`, which phase 4 needs (B13).
- [x] **Upsert rule:** metadata fields are always updated. Parsed-content fields (`raw_text`, `sections`, `parser_*`, `pdf_processed`, `pdf_processing_date`) are only updated when the new parse **succeeded**. A failed re-parse never downgrades a paper that was parsed before (B23).

### 1.5 MigrationService
- [x] A `BackgroundService` that runs `db.Database.MigrateAsync()` inside `CreateExecutionStrategy()` (it retries while Postgres is still starting), then calls `IHostApplicationLifetime.StopApplication()`. This is the standard Aspire migration-service pattern.
- [x] `dotnet ef migrations add Initial --project src/PaperPilot.Infrastructure --startup-project src/PaperPilot.MigrationService`.

## As built

- **String lists are `text[]`, not `jsonb`.** Npgsql maps `List<string>` to native arrays, and forcing `jsonb` onto a CLR type with `HasColumnType` is deprecated in Npgsql 10. Arrays are also the better Postgres type for `categories` filters. Sections keep lowercase JSON keys (`title`, `content`, `level`), as in the Python table.
- **Contracts are records with `init` properties**, so DataAnnotations sit on properties where both `Validator` and .NET 10 minimal-API validation read them. Phase 2 confirmed that minimal-API validation covers types from `PaperPilot.Core`.
- **Options:** `Jina:BaseUrl` and `Jina:Model` are new (Python hard-coded them; WireMock tests need the URL). `Langfuse:Enabled` defaults to `false`, because the AppHost turns it on along with the Langfuse stack. Python's `opensearch.max_text_size` is not ported: nothing read it.
- **`PaperUpsert(ArxivPaper Metadata, PdfContent? Content)`** is the repository's upsert input; `Content == null` means the PDF was skipped or failed.
- **The migration service** sets exit code 1 when a migration fails, so Aspire's `WaitForCompletion` doesn't start Api and Worker on an unmigrated database.

## Tests
- [x] Unit: `ArxivId` (new-style, old-style, with and without version, IDs containing `v`), options validation (overlap ≥ chunk size fails), and contract JSON round trips. The Python `json_schema_extra` examples should deserialize into the C# records and serialize back with the same snake_case keys.
- [x] Integration (Testcontainers PostgreSQL): migration applies cleanly, upsert insert then update, unique `arxiv_id`, jsonb round trip of sections, UTC preserved, and a failed re-parse doesn't overwrite earlier parsed content.

## Done when
- [x] `aspire run`: `migrations` turns *Finished*, then `api` and `worker` start.
- [x] `psql -h localhost -p 5442 -U postgres -d papers -c '\d papers'` shows the snake_case schema with `jsonb` and `timestamptz` columns.
- [x] Unit and integration tests pass.
