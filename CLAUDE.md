# CLAUDE.md

PaperPilot is a .NET 10 / Aspire 13 arXiv paper curator (hybrid search, classic RAG, agentic RAG). The plan is in
[docs/plan/](docs/plan/README.md): decisions, phases, the Python → .NET file map, and every intended behaviour change
from the Python version (`B`/`C`/`N` IDs).

## Commands

```bash
dotnet build                                      # warnings are errors
dotnet test --project tests/PaperPilot.UnitTests  # Microsoft.Testing.Platform (global.json), not VSTest
dotnet format --verify-no-changes                 # CI runs this
dotnet run --project src/PaperPilot.AppHost       # whole stack; or `aspire run`
```

- Required secret (Api and Worker wait until it's set):
  `dotnet user-secrets set Parameters:jina-api-key <value> --project src/PaperPilot.AppHost`
- Optional: `Parameters:telegram-bot-token`, `Langfuse:Enabled=true` (same command). Langfuse keys and passwords are
  generated on first run and saved to the same user-secrets store.
- Stop the Python stack first (`docker compose stop` in the Python repo): Docker Desktop has 8 GB.

## Architecture

```
Core  ←  Infrastructure  ←  Rag, Ingestion  ←  Api, Worker, MigrationService
                                            ←  Web (calls Api over HTTP only; references Core for contracts)
ServiceDefaults ← every host
```

| Resource | Port | Notes |
|---|---|---|
| Aspire dashboard | 17205 | login URL is printed at startup |
| `api` | 8100 | `/api/v1/*` |
| `web` | 8101 | Blazor UI |
| `worker` | 8102 | Hangfire dashboard at `/hangfire` (phase 4) |
| `postgres` | 5442 | persistent container, volume `paperpilot-postgres-data` |
| `redis` | 6390 (TLS) | see gotchas |
| `opensearch` | 9210 | persistent, 512 MB heap, volume `paperpilot-opensearch-data` |
| `opensearch-dashboards` | 5610 | explicit start from the Aspire dashboard |
| `docling` | 5011 | `/docs`, `/ui` |
| `langfuse-web` | 3010 | only with `Langfuse:Enabled=true` |
| Ollama | 11434 | Windows host install; `Ollama:UseContainer=true` for a container |

## Gotchas

- **Resilience (plan R1).** `AddServiceDefaults` puts the standard resilience handler on every `HttpClient`
  (10 s per attempt, ~30 s total). Clients whose calls take minutes must use
  `AddLongRunningResilienceHandler(name, totalTimeout, pipeline => …)` from ServiceDefaults: it removes the
  standard handler (`RemoveAllResilienceHandlers`, experimental `EXTEXP0001`), sets `HttpClient.Timeout` to
  infinite (its 100 s default applies on top of any pipeline), and adds a pipeline whose outermost strategy is the
  total timeout. Strategies added in `configure` run inside it. `ServiceDefaults/LongRunningResilienceTests` proves
  it against a 12 s WireMock endpoint.
- **AppHost uses NuGet DCP and dashboard.** The template sets `AspireUseCliBundle=true`, which needs an Aspire CLI on
  `PATH` at build time (CI has none). It's `false` here, with `ASPIRE010` suppressed. The Aspire CLI is a global
  .NET tool on this machine, on PowerShell's `PATH` but not Git Bash's.
- **Redis is TLS.** Aspire 13.6's `AddRedis` serves `rediss://` on the pinned port 6390 and plain TCP on a second,
  random port. `WithReference(redis)` hands clients a TLS connection string, so this only matters for manual tools.
- **Postgres 18.** Aspire 13.6 runs `postgres:18.x`.
- **Line endings are LF** (`.gitattributes`, `.editorconfig`). Git on this machine has `core.autocrlf=true`, and
  `dotnet format` checks `end_of_line`, so new files from templates or editors may need converting.
- **docling-serve image** is `ghcr.io/docling-project/docling-serve-cpu:v1.35.0` (CPU only, several GB). The Python
  stack used docling 2.52.
- **Optional parameters.** Aspire waits for any parameter without a value, so optional secrets are only added
  when configuration has them (see `AppHost.cs`).
