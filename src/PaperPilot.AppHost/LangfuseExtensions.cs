using System.Security.Cryptography;
using Aspire.Hosting.Publishing;

namespace PaperPilot.AppHost;

/// <summary>
/// The optional Langfuse v3 stack, ported from the Python repo's compose.yml.
/// Langfuse gets a database on the main Postgres server, but its own Redis: its BullMQ queues need
/// <c>maxmemory-policy noeviction</c>, while the answer cache uses <c>allkeys-lru</c> (plan D13).
/// </summary>
internal static class LangfuseExtensions
{
    private const int WebPort = 3010;
    private const int MinioPort = 9190;
    private const string WebUrl = "http://localhost:3010";
    private const string MinioUrl = "http://localhost:9190";

    public static void AddLangfuse(
        this IDistributedApplicationBuilder builder,
        IResourceBuilder<PostgresServerResource> postgres,
        params IResourceBuilder<ProjectResource>[] consumers)
    {
        // Generated on first run and saved to the AppHost's user secrets, so they stay stable across runs.
        var publicKey = builder.AddParameter("langfuse-public-key", new PrefixedTokenDefault("pk-lf-"), secret: true, persist: true);
        var secretKey = builder.AddParameter("langfuse-secret-key", new PrefixedTokenDefault("sk-lf-"), secret: true, persist: true);
        var nextAuthSecret = builder.AddParameter("langfuse-nextauth-secret", new PrefixedTokenDefault(""), secret: true, persist: true);
        var salt = builder.AddParameter("langfuse-salt", new PrefixedTokenDefault(""), secret: true, persist: true);
        var encryptionKey = builder.AddParameter("langfuse-encryption-key", new PrefixedTokenDefault(""), secret: true, persist: true);
        var adminPassword = builder.AddParameter("langfuse-admin-password", new PrefixedTokenDefault(""), secret: true, persist: true);
        var clickhousePassword = builder.AddParameter("langfuse-clickhouse-password", new PrefixedTokenDefault(""), secret: true, persist: true);
        var minioPassword = builder.AddParameter("langfuse-minio-password", new PrefixedTokenDefault(""), secret: true, persist: true);
        var redisPassword = builder.AddParameter("langfuse-redis-password", new PrefixedTokenDefault(""), secret: true, persist: true);

        var database = postgres.AddDatabase("langfuse");

        var redis = builder.AddContainer("langfuse-redis", "redis", "7")
            .WithImageRegistry("docker.io")
            .WithEndpoint(targetPort: 6379, name: "tcp")
            .WithArgs(context =>
            {
                context.Args.Add("--requirepass");
                context.Args.Add(redisPassword);
                context.Args.Add("--maxmemory-policy");
                context.Args.Add("noeviction");
            });

        var clickhouse = builder.AddContainer("clickhouse", "clickhouse/clickhouse-server", "24.8-alpine")
            .WithHttpEndpoint(targetPort: 8123, name: "http")
            .WithEndpoint(targetPort: 9000, name: "native")
            .WithEnvironment("CLICKHOUSE_DB", "langfuse")
            .WithEnvironment("CLICKHOUSE_USER", "langfuse")
            .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
            .WithEnvironment("CLICKHOUSE_PASSWORD", clickhousePassword)
            .WithVolume("paperpilot-clickhouse-data", "/var/lib/clickhouse")
            .WithHttpHealthCheck("/ping", endpointName: "http");

        // minio/minio is no longer published on Docker Hub.
        var minio = builder.AddContainer("langfuse-minio", "chainguard/minio", "latest")
            .WithImageRegistry("cgr.dev")
            .WithEntrypoint("sh")
            .WithArgs("-c", "mkdir -p /data/langfuse && minio server --address \":9000\" /data")
            .WithHttpEndpoint(port: MinioPort, targetPort: 9000, name: "http")
            .WithEnvironment("MINIO_ROOT_USER", "langfuse_minio")
            .WithEnvironment("MINIO_ROOT_PASSWORD", minioPassword)
            .WithVolume("paperpilot-langfuse-minio-data", "/data")
            .WithHttpHealthCheck("/minio/health/live", endpointName: "http");

        var redisEndpoint = redis.GetEndpoint("tcp");
        var clickhouseHttp = clickhouse.GetEndpoint("http");
        var clickhouseNative = clickhouse.GetEndpoint("native");
        var minioEndpoint = minio.GetEndpoint("http");

        void ConfigureShared(IResourceBuilder<ContainerResource> langfuse) => langfuse
            .WithEnvironment("DATABASE_URL", database.Resource.UriExpression)
            .WithEnvironment("SALT", salt)
            .WithEnvironment("ENCRYPTION_KEY", encryptionKey)
            .WithEnvironment("TELEMETRY_ENABLED", "false")
            .WithEnvironment("LANGFUSE_ENABLE_EXPERIMENTAL_FEATURES", "true")
            .WithEnvironment("NEXTAUTH_URL", WebUrl)
            .WithEnvironment("CLICKHOUSE_MIGRATION_URL", ReferenceExpression.Create(
                $"clickhouse://{clickhouseNative.Property(EndpointProperty.Host)}:{clickhouseNative.Property(EndpointProperty.Port)}"))
            .WithEnvironment("CLICKHOUSE_URL", clickhouseHttp)
            .WithEnvironment("CLICKHOUSE_USER", "langfuse")
            .WithEnvironment("CLICKHOUSE_PASSWORD", clickhousePassword)
            .WithEnvironment("CLICKHOUSE_CLUSTER_ENABLED", "false")
            .WithEnvironment("LANGFUSE_USE_AZURE_BLOB", "false")
            .WithEnvironment("LANGFUSE_S3_EVENT_UPLOAD_BUCKET", "langfuse")
            .WithEnvironment("LANGFUSE_S3_EVENT_UPLOAD_REGION", "auto")
            .WithEnvironment("LANGFUSE_S3_EVENT_UPLOAD_ACCESS_KEY_ID", "langfuse_minio")
            .WithEnvironment("LANGFUSE_S3_EVENT_UPLOAD_SECRET_ACCESS_KEY", minioPassword)
            .WithEnvironment("LANGFUSE_S3_EVENT_UPLOAD_ENDPOINT", minioEndpoint)
            .WithEnvironment("LANGFUSE_S3_EVENT_UPLOAD_FORCE_PATH_STYLE", "true")
            .WithEnvironment("LANGFUSE_S3_EVENT_UPLOAD_PREFIX", "events/")
            .WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_BUCKET", "langfuse")
            .WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_REGION", "auto")
            .WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_ACCESS_KEY_ID", "langfuse_minio")
            .WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_SECRET_ACCESS_KEY", minioPassword)
            // The browser uploads media directly, so this is the host-mapped port.
            .WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_ENDPOINT", MinioUrl)
            .WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_FORCE_PATH_STYLE", "true")
            .WithEnvironment("LANGFUSE_S3_MEDIA_UPLOAD_PREFIX", "media/")
            .WithEnvironment("REDIS_HOST", redisEndpoint.Property(EndpointProperty.Host))
            .WithEnvironment("REDIS_PORT", redisEndpoint.Property(EndpointProperty.Port))
            .WithEnvironment("REDIS_AUTH", redisPassword)
            .WithEnvironment("REDIS_TLS_ENABLED", "false")
            .WaitFor(database)
            .WaitFor(redis)
            .WaitFor(clickhouse)
            .WaitFor(minio);

        var langfuseWorker = builder.AddContainer("langfuse-worker", "langfuse/langfuse-worker", "3")
            .WithImageRegistry("docker.io");
        ConfigureShared(langfuseWorker);

        var langfuseWeb = builder.AddContainer("langfuse-web", "langfuse/langfuse", "3")
            .WithImageRegistry("docker.io")
            .WithHttpEndpoint(port: WebPort, targetPort: 3000, name: "http")
            .WithEnvironment("NEXTAUTH_SECRET", nextAuthSecret)
            // Headless init: the project and its API keys exist on first start and match what Api and Worker send.
            .WithEnvironment("LANGFUSE_INIT_ORG_ID", "paperpilot-org")
            .WithEnvironment("LANGFUSE_INIT_ORG_NAME", "PaperPilot")
            .WithEnvironment("LANGFUSE_INIT_PROJECT_ID", "paperpilot")
            .WithEnvironment("LANGFUSE_INIT_PROJECT_NAME", "PaperPilot")
            .WithEnvironment("LANGFUSE_INIT_PROJECT_PUBLIC_KEY", publicKey)
            .WithEnvironment("LANGFUSE_INIT_PROJECT_SECRET_KEY", secretKey)
            .WithEnvironment("LANGFUSE_INIT_USER_EMAIL", "admin@example.com")
            .WithEnvironment("LANGFUSE_INIT_USER_NAME", "Admin")
            .WithEnvironment("LANGFUSE_INIT_USER_PASSWORD", adminPassword)
            .WithHttpHealthCheck("/api/public/health", endpointName: "http");
        ConfigureShared(langfuseWeb);

        var webEndpoint = langfuseWeb.GetEndpoint("http");
        foreach (var consumer in consumers)
        {
            consumer
                .WithEnvironment("Langfuse__Enabled", "true")
                .WithEnvironment("Langfuse__BaseUrl", webEndpoint)
                .WithEnvironment("Langfuse__PublicKey", publicKey)
                .WithEnvironment("Langfuse__SecretKey", secretKey);
        }
    }

    /// <summary>A random 32-byte hex token with an optional prefix. Langfuse's ENCRYPTION_KEY must be 64 hex characters.</summary>
    private sealed class PrefixedTokenDefault(string prefix) : ParameterDefault
    {
        public override string GetDefaultValue() => prefix + RandomNumberGenerator.GetHexString(64, lowercase: true);

        public override void WriteToManifest(ManifestPublishingContext context) =>
            throw new NotSupportedException("Langfuse is a local-only resource and isn't published.");
    }
}
