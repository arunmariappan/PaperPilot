# Phase 0: Bootstrap the repo and the Aspire AppHost

**Goal:** a new `PaperPilot` repo where `aspire run` starts Postgres, Redis, OpenSearch, docling-serve and (optionally) Langfuse, and where every project builds, even though they're mostly empty.

**Size:** M · **Depends on:** nothing

## Prerequisites

- .NET 10 SDK (`dotnet --version` should show `10.0.x`)
- Aspire CLI 13.x (`aspire --version`). Install it with the script from aspire.dev or with `dotnet tool install -g Aspire.Cli`.
- Docker Desktop (8 GB). **Stop the Python stack first** (`docker compose down` in the Python repo), because the two together won't fit in memory.
- Windows Ollama running with `qwen3.5:9b` pulled.

## Tasks

### 0.1 Repository
- [x] `D:\ai_workspace\PaperPilot` on top of the GitHub repo `arunmariappan/PaperPilot` (public, created with an Apache-2.0 `LICENSE`, the .NET `.gitignore` and a one-line `README.md`). Commit as `Arun Mariappan Karunanithi <2525449+arunmariappan@users.noreply.github.com>` (repo-local `git config`, as in the Python repo). All work is committed on `main` and pushed.
- [x] Move the `plan/` folder to `docs/plan/`, so the plan sits next to the code.
- [x] `CLAUDE.md` for the new repo: commands, architecture summary and gotchas. Start small and add to it as phases land.

### 0.2 Build configuration
- [x] `global.json`: pin SDK `10.0.x` with `"rollForward": "latestFeature"`.
- [x] `Directory.Build.props`:
  ```xml
  <Project>
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <Nullable>enable</Nullable>
      <ImplicitUsings>enable</ImplicitUsings>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
      <AnalysisLevel>latest-recommended</AnalysisLevel>
      <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    </PropertyGroup>
  </Project>
  ```
- [x] `Directory.Packages.props` with `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`. Each phase adds the packages it needs (README §4).
- [x] `.editorconfig`: file-scoped namespaces, `var` where the type is obvious, 4-space indentation, 130 max line length (same as the Python ruff config).

### 0.3 Projects
```bash
dotnet new sln -n PaperPilot                              # creates PaperPilot.slnx on .NET 10
dotnet new aspire-apphost        -o src/PaperPilot.AppHost
dotnet new aspire-servicedefaults -o src/PaperPilot.ServiceDefaults
dotnet new classlib -o src/PaperPilot.Core
dotnet new classlib -o src/PaperPilot.Infrastructure
dotnet new classlib -o src/PaperPilot.Rag
dotnet new classlib -o src/PaperPilot.Ingestion
dotnet new web      -o src/PaperPilot.Api
dotnet new web      -o src/PaperPilot.Worker               # ASP.NET host, needed for the Hangfire dashboard
dotnet new worker   -o src/PaperPilot.MigrationService
dotnet new blazor   -o src/PaperPilot.Web --interactivity Server --empty
dotnet new install xunit.v3.templates
dotnet new xunit3   -o tests/PaperPilot.UnitTests
dotnet new xunit3   -o tests/PaperPilot.IntegrationTests
dotnet sln add (all of the above)
```
- [x] Add project references in the direction shown in README §2. Add `ServiceDefaults` to every host and call `builder.AddServiceDefaults()` / `app.MapDefaultEndpoints()`.
- [x] Pin HTTP ports in each host's `launchSettings.json`, so they're predictable and don't clash with the Python stack (8000, 5432, 6379, 9200, 5601, 3001, 8080):

| Resource | Host port |
|---|---|
| `api` | 8100 |
| `web` | 8101 |
| `worker` (Hangfire dashboard at `/hangfire`) | 8102 |
| `postgres` | 5442 |
| `redis` | 6390 |
| `opensearch` | 9210 |
| `opensearch-dashboards` (explicit start) | 5610 |
| `docling` (`/docs` and `/ui`) | 5011 |
| `langfuse-web` (optional) | 3010 |

### 0.4 AppHost resources
`src/PaperPilot.AppHost/Program.cs`, roughly. Check the exact method names against Aspire 13 when you implement it:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

// Secrets (stored with: dotnet user-secrets set Parameters:jina-api-key <value> --project src/PaperPilot.AppHost)
var jinaKey        = builder.AddParameter("jina-api-key", secret: true);
var telegramToken  = builder.AddParameter("telegram-bot-token", secret: true);
var langfuseOn     = builder.Configuration.GetValue("Langfuse:Enabled", false);

var postgres = builder.AddPostgres("postgres", port: 5442).WithDataVolume().WithLifetime(ContainerLifetime.Persistent);
var papersDb = postgres.AddDatabase("papers");

var redis = builder.AddRedis("redis", port: 6390).WithDataVolume().WithLifetime(ContainerLifetime.Persistent);

var opensearch = builder.AddContainer("opensearch", "opensearchproject/opensearch", "2.19.0")
    .WithHttpEndpoint(port: 9210, targetPort: 9200, name: "http")
    .WithEnvironment("discovery.type", "single-node")
    .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
    .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
    .WithVolume("paperpilot-opensearch", "/usr/share/opensearch/data")
    .WithHttpHealthCheck("/_cluster/health")
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddContainer("opensearch-dashboards", "opensearchproject/opensearch-dashboards", "2.19.0")
    .WithHttpEndpoint(port: 5610, targetPort: 5601)
    .WithEnvironment("OPENSEARCH_HOSTS", "http://opensearch:9200")
    .WithEnvironment("DISABLE_SECURITY_DASHBOARDS_PLUGIN", "true")
    .WithExplicitStart();                                   // start from the dashboard when needed

var docling = builder.AddContainer("docling", "docling-project/docling-serve-cpu", "v1.35.0")   // ghcr.io, CPU-only image
    .WithHttpEndpoint(port: 5011, targetPort: 5001, name: "http")
    .WithEnvironment("DOCLING_SERVE_MAX_SYNC_WAIT", "600")
    .WithEnvironment("DOCLING_SERVE_ENABLE_UI", "true")
    .WithHttpHealthCheck("/health");

var ollama = builder.AddConnectionString("ollama");        // "Endpoint=http://localhost:11434" in AppHost appsettings

var migrations = builder.AddProject<Projects.PaperPilot_MigrationService>("migrations")
    .WithReference(papersDb).WaitFor(papersDb);

var api = builder.AddProject<Projects.PaperPilot_Api>("api")
    .WithReference(papersDb).WithReference(redis).WithReference(ollama)
    .WithEnvironment("OpenSearch__Host", opensearch.GetEndpoint("http"))
    .WithEnvironment("Jina__ApiKey", jinaKey)
    .WithEnvironment("Telegram__BotToken", telegramToken)
    .WaitForCompletion(migrations).WaitFor(opensearch).WaitFor(redis);

var worker = builder.AddProject<Projects.PaperPilot_Worker>("worker")
    .WithReference(papersDb)
    .WithEnvironment("OpenSearch__Host", opensearch.GetEndpoint("http"))
    .WithEnvironment("Docling__BaseUrl", docling.GetEndpoint("http"))
    .WithEnvironment("Jina__ApiKey", jinaKey)
    .WaitForCompletion(migrations).WaitFor(opensearch).WaitFor(docling);

builder.AddProject<Projects.PaperPilot_Web>("web")
    .WithReference(api).WaitFor(api).WithExternalHttpEndpoints();

if (langfuseOn) builder.AddLangfuse(postgres, api, worker);   // see 0.5 (own Redis, D13)

builder.Build().Run();
```

- [x] **Optional secrets must not block startup.** Aspire waits (and asks in the dashboard) for any parameter that has no value, so the sketch's unconditional `telegram-bot-token` would hold `api` back until it's set. Read the optional values (`Parameters:telegram-bot-token`, Langfuse keys) from `builder.Configuration`, and only add the parameter and `WithEnvironment(...)` (plus `Telegram__Enabled=true`) when a value exists. `jina-api-key` can stay required, because hybrid search and ingestion need it.
- [x] An optional Ollama container (`CommunityToolkit.Aspire.Hosting.Ollama`) behind `Ollama:UseContainer=false`. The default stays the Windows host Ollama. *(Built, not yet run.)*
- [x] Persistent container lifetimes, so restarting the AppHost doesn't restart OpenSearch and Postgres, and named volumes, so data survives.

### 0.5 Optional Langfuse stack (`AppHost/LangfuseExtensions.cs`)
- [x] `AddLangfuse(...)` adds `clickhouse` (24.8-alpine), `langfuse-minio` (`cgr.dev/chainguard/minio`), `langfuse-worker` (`langfuse/langfuse-worker:3`) and `langfuse-web` (`langfuse/langfuse:3`, port 3010). Copy the environment from the Python repo's `compose.yml`.
- [x] **Reuse** the main Postgres server (a `langfuse` database) instead of a separate container. That saves most of the ~300 MB.
- [x] Give Langfuse its **own** small Redis (`langfuse-redis`, `--maxmemory-policy noeviction`, no volume needed). Langfuse's BullMQ queues require `noeviction`, and the main Redis uses `allkeys-lru` for the answer cache (D13).
- [x] Headless init through `LANGFUSE_INIT_ORG_ID`, `LANGFUSE_INIT_PROJECT_ID`, `LANGFUSE_INIT_PROJECT_PUBLIC_KEY` and `LANGFUSE_INIT_PROJECT_SECRET_KEY`, so the project keys come from AppHost secret parameters and match what Api and Worker send. This is the same idea as your `compose.override.yml` today.
- [x] Pass `Langfuse__Enabled=true`, `Langfuse__BaseUrl`, `Langfuse__PublicKey` and `Langfuse__SecretKey` to `api` and `worker` only when it's enabled.

### 0.6 ServiceDefaults
- [x] Keep the template's OTel, health-check and service-discovery setup.
- [x] Add `ConfigureHttpJsonOptions` with `JsonNamingPolicy.SnakeCaseLower` (used by Api).
- [x] **R1 spike:** the template calls `http.AddStandardResilienceHandler()` for **every** HttpClient (total timeout about 30 s). Write a 20-line test that registers a named client, replaces its resilience handler with a custom one (5-minute timeout), and calls an endpoint that takes 45 s (WireMock with a delay). Record the working pattern in `CLAUDE.md`, because phases 3 and 4 depend on it for Ollama, docling and Jina.

### 0.7 CI skeleton
- [x] `.github/workflows/ci.yml`: checkout, `actions/setup-dotnet` (from global.json), `dotnet restore`, `dotnet build -c Release`, `dotnet format --verify-no-changes`, `dotnet test tests/PaperPilot.UnitTests`. Integration tests are added in phase 8.

## Done when
- [x] `aspire run` opens the dashboard. `postgres`, `redis`, `opensearch` and `docling` are healthy, and `migrations` runs and exits (no migrations yet).
- [x] `curl http://localhost:9210/_cluster/health` returns green or yellow, and `http://localhost:5011/docs` shows the docling-serve API.
- [x] `docker stats` shows total container memory below about 5 GB without Langfuse. Write down the actual number in `CLAUDE.md`. *(2.05 GiB idle; 3.9 GiB with Langfuse.)*
- [x] The R1 spike pattern is written down.
- [x] CI is green on `main`.
