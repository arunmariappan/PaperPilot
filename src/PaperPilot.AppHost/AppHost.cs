using Microsoft.Extensions.Configuration;
using PaperPilot.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// Required secret. Set it with:
//   dotnet user-secrets set Parameters:jina-api-key <value> --project src/PaperPilot.AppHost
var jinaKey = builder.AddParameter("jina-api-key", secret: true);

var postgres = builder.AddPostgres("postgres", port: 5442)
    .WithDataVolume("paperpilot-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);
var papersDb = postgres.AddDatabase("papers");

var redis = builder.AddRedis("redis", port: 6390)
    .WithDataVolume("paperpilot-redis-data")
    .WithLifetime(ContainerLifetime.Persistent);

var opensearch = builder.AddContainer("opensearch", "opensearchproject/opensearch", "2.19.0")
    .WithHttpEndpoint(port: 9210, targetPort: 9200, name: "http")
    .WithEnvironment("discovery.type", "single-node")
    .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
    .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
    .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
    .WithVolume("paperpilot-opensearch-data", "/usr/share/opensearch/data")
    // 408 until the cluster is at least yellow, so "healthy" means searchable.
    .WithHttpHealthCheck("/_cluster/health?wait_for_status=yellow&timeout=1s", endpointName: "http")
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddContainer("opensearch-dashboards", "opensearchproject/opensearch-dashboards", "2.19.0")
    .WithHttpEndpoint(port: 5610, targetPort: 5601, name: "http")
    .WithEnvironment("OPENSEARCH_HOSTS", opensearch.GetEndpoint("http"))
    .WithEnvironment("DISABLE_SECURITY_DASHBOARDS_PLUGIN", "true")
    .WaitFor(opensearch)
    .WithExplicitStart();

var docling = builder.AddContainer("docling", "docling-project/docling-serve-cpu", "v1.35.0")
    .WithImageRegistry("ghcr.io")
    .WithHttpEndpoint(port: 5011, targetPort: 5001, name: "http")
    .WithEnvironment("DOCLING_SERVE_MAX_SYNC_WAIT", "600")
    .WithEnvironment("DOCLING_SERVE_ENABLE_UI", "true")
    .WithHttpHealthCheck("/health", endpointName: "http");

// The Windows host Ollama by default ("Endpoint=http://localhost:11434" in appsettings.json).
// Set Ollama:UseContainer=true to run Ollama in Docker instead (CPU only, pulls the model on first start).
// Ollama:Model is passed on to the API, which otherwise uses its own default.
var ollamaModel = builder.Configuration["Ollama:Model"] ?? "qwen3.5:9b";
IResourceBuilder<IResourceWithConnectionString> ollama = builder.Configuration.GetValue("Ollama:UseContainer", false)
    ? builder.AddOllama("ollama-container")
        .WithDataVolume("paperpilot-ollama-data")
        .AddModel("ollama", ollamaModel)
    : builder.AddConnectionString("ollama");

var migrations = builder.AddProject<Projects.PaperPilot_MigrationService>("migrations")
    .WithReference(papersDb)
    .WaitFor(papersDb);

var api = builder.AddProject<Projects.PaperPilot_Api>("api")
    .WithReference(papersDb)
    .WithReference(redis)
    .WithReference(ollama)
    .WithEnvironment("Ollama__Model", ollamaModel)
    .WithEnvironment("OpenSearch__Host", opensearch.GetEndpoint("http"))
    .WithEnvironment("Jina__ApiKey", jinaKey)
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrations)
    .WaitFor(opensearch)
    .WaitFor(redis);

var worker = builder.AddProject<Projects.PaperPilot_Worker>("worker")
    .WithReference(papersDb)
    .WithEnvironment("OpenSearch__Host", opensearch.GetEndpoint("http"))
    // Outside the repository; PDFs older than 30 days are deleted after each run.
    .WithEnvironment("Arxiv__PdfCacheDir", Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperPilot", "arxiv_pdfs"))
    .WithEnvironment("Docling__BaseUrl", docling.GetEndpoint("http"))
    .WithEnvironment("Jina__ApiKey", jinaKey)
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrations)
    .WaitFor(opensearch)
    .WaitFor(docling);

builder.AddProject<Projects.PaperPilot_Web>("web")
    .WithReference(api)
    .WithHttpHealthCheck("/health")
    .WaitFor(api)
    .WithExternalHttpEndpoints();

// Optional secrets must not block startup: Aspire waits for any parameter without a value,
// so the parameter is only added when it has been set.
//   dotnet user-secrets set Parameters:telegram-bot-token <value> --project src/PaperPilot.AppHost
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:telegram-bot-token"]))
{
    var telegramToken = builder.AddParameter("telegram-bot-token", secret: true);
    api.WithEnvironment("Telegram__BotToken", telegramToken)
        .WithEnvironment("Telegram__Enabled", "true");
}

// Off by default to save Docker memory (docs/plan/README.md R2). Turn it on with:
//   dotnet user-secrets set Langfuse:Enabled true --project src/PaperPilot.AppHost
if (builder.Configuration.GetValue("Langfuse:Enabled", false))
{
    builder.AddLangfuse(postgres, api, worker);
}

builder.Build().Run();
