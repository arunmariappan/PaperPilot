# Phase 1: Domain, options and persistence

**Goal:** typed configuration, the domain model, the public API contracts, and the `papers` table created by an EF Core migration.

**Size:** S · **Depends on:** phase 0

## Tasks

### 1.1 Options (`PaperPilot.Core/Options`)
- [ ] One class per section, as in README §5: `AppOptions`, `ArxivOptions`, `PdfParserOptions`, `DoclingOptions`, `ChunkingOptions`, `OpenSearchOptions`, `JinaOptions`, `OllamaOptions`, `CacheOptions`, `LangfuseOptions`, `TelegramOptions`. Copy the Python defaults exactly (e.g. `Arxiv.MaxResults = 15`, `Arxiv.RateLimitDelaySeconds = 3.0`, `Chunking.ChunkSize = 600`).
- [ ] DataAnnotations for the ranges and rules that Python enforced or assumed: `Chunking.OverlapSize < ChunkSize` (Python raised `ValueError`), `OpenSearch.VectorDimension > 0`, `Ollama.TimeoutSeconds >= 1`.
- [ ] `OpenSearchOptions.ChunkIndexName => $"{IndexName}-{ChunkIndexSuffix}"` (= `arxiv-papers-chunks`).
- [ ] An `AddPaperPilotOptions(this IHostApplicationBuilder)` extension that binds every section with `.ValidateDataAnnotations().ValidateOnStart()`.
- [ ] `Jina.ApiKey`, `Telegram.BotToken` and `Langfuse.*Key` are **not** `[Required]`. Python ran without them, and only the features that need them degrade. Log one warning at startup for each missing key.

### 1.2 Domain (`PaperPilot.Core/Domain`)
- [ ] `ArxivPaper` record: `ArxivId`, `Title`, `Authors`, `Abstract`, `Categories`, `PublishedDate` (`DateTimeOffset`), `PdfUrl`.
- [ ] `PaperSection(string Title, string Content, int Level = 1)`, `PdfContent(IReadOnlyList<PaperSection> Sections, string RawText, string ParserUsed, IReadOnlyDictionary<string, object?> Metadata)`.
- [ ] `Paper` entity: `Id` (Guid), `ArxivId`, `Title`, `Authors` (`List<string>`), `Abstract`, `Categories` (`List<string>`), `PublishedDate`, `PdfUrl`, `RawText?`, `Sections?` (`List<PaperSection>`), `References?` (`List<string>`), `ParserUsed?`, `ParserMetadata?` (`JsonDocument` or `Dictionary<string, object?>`), `PdfProcessed`, `PdfProcessingDate?`, `CreatedAt`, `UpdatedAt`.
- [ ] **`ArxivId` static helper (B8):** `StripVersion("2510.01234v2") == "2510.01234"`, `StripVersion("solv-int/9901001v1") == "solv-int/9901001"`, `ToPdfUrl(id) => $"https://arxiv.org/pdf/{StripVersion(id)}.pdf"`, `ToAbsUrl(id)`. Every other piece of code uses these helpers instead of formatting URLs itself.

### 1.3 API contracts (`PaperPilot.Core/Contracts`)
Records, snake_case on the wire through `JsonNamingPolicy.SnakeCaseLower`:
- [ ] `AskRequest(string Query, int TopK = 3, bool UseHybrid = true, string? Model = null, IReadOnlyList<string>? Categories = null)`, with `[StringLength(1000, MinimumLength = 1)]`, a `[NotBlank]` attribute that rejects whitespace-only queries (**C13**), and `[Range(1, 10)]`. `Model` is nullable because a record can't read configuration; the services fall back to `Ollama:Model` (Python used a `default_factory` for the same thing).
- [ ] `AskResponse(Query, Answer, Sources, ChunksUsed, SearchMode)`, and `AgenticAskResponse : AskResponse` + `ReasoningSteps`, `RetrievalAttempts`, `TraceId?`.
- [ ] `FeedbackRequest(TraceId, [Range(-1, 1)] Score, [StringLength(1000)] Comment?)`, `FeedbackResponse(Success, Message)`.
- [ ] `HybridSearchRequest(Query [1..500], Size = 10 [1..100], [JsonPropertyName("from")] From = 0, Categories?, LatestPapers = false, UseHybrid = true, MinScore = 0.0)`.
- [ ] `SearchHit`, `SearchResponse` (with `[JsonPropertyName("from")]`, `SearchMode?`, `Error?`), `HealthResponse`, `ServiceStatus`.
- [ ] Enable .NET 10 minimal-API validation (`builder.Services.AddValidation()`). Invalid requests return 400 `ProblemDetails` (C1).

### 1.4 EF Core (`PaperPilot.Infrastructure/Persistence`)
- [ ] Packages: `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions` (`UseSnakeCaseNamingConvention()`, so the columns read like the Python table), `Microsoft.EntityFrameworkCore.Design` (MigrationService only).
- [ ] `PaperPilotDbContext` with `DbSet<Paper> Papers`.
- [ ] `PaperConfiguration`: table `papers`, unique index on `arxiv_id`, `jsonb` for `authors`, `categories`, `sections`, `references` and `parser_metadata` (EF Core 10 JSON mapping), `timestamptz` everywhere (B21), and `created_at`/`updated_at` set in `SaveChangesAsync` (an interceptor or an override).
- [ ] `PaperRepository` (scoped). Port everything in `repositories/paper.py`: `GetByArxivIdAsync`, `GetByIdAsync`, `GetAllAsync(limit, offset)`, `CountAsync`, `GetProcessedAsync`, `GetUnprocessedAsync`, `GetWithRawTextAsync`, `GetProcessingStatsAsync`, `UpsertAsync(PaperUpsert)`. Add `GetByIdsAsync(IReadOnlyCollection<Guid>)`, which phase 4 needs (B13).
- [ ] **Upsert rule:** metadata fields are always updated. Parsed-content fields (`raw_text`, `sections`, `parser_*`, `pdf_processed`, `pdf_processing_date`) are only updated when the new parse **succeeded**. A failed re-parse never downgrades a paper that was parsed before (B23).

### 1.5 MigrationService
- [ ] A `BackgroundService` that runs `db.Database.MigrateAsync()` inside `CreateExecutionStrategy()` (it retries while Postgres is still starting), then calls `IHostApplicationLifetime.StopApplication()`. This is the standard Aspire migration-service pattern.
- [ ] `dotnet ef migrations add Initial --project src/PaperPilot.Infrastructure --startup-project src/PaperPilot.MigrationService`.

## Tests
- [ ] Unit: `ArxivId` (new-style, old-style, with and without version, IDs containing `v`), options validation (overlap ≥ chunk size fails), and contract JSON round trips. The Python `json_schema_extra` examples should deserialize into the C# records and serialize back with the same snake_case keys.
- [ ] Integration (Testcontainers PostgreSQL): migration applies cleanly, upsert insert then update, unique `arxiv_id`, jsonb round trip of sections, UTC preserved, and a failed re-parse doesn't overwrite earlier parsed content.

## Done when
- [ ] `aspire run`: `migrations` turns *Finished*, then `api` and `worker` start.
- [ ] `psql -h localhost -p 5442 -U postgres -d papers -c '\d papers'` shows the snake_case schema with `jsonb` and `timestamptz` columns.
- [ ] Unit and integration tests pass.
