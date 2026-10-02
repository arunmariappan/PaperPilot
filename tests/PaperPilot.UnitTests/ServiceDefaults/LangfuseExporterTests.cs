using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.ServiceDefaults;

public sealed class LangfuseExporterTests : IDisposable
{
    private readonly WireMockServer _langfuse = WireMockServer.Start();
    private readonly ActivitySource _paperPilot = new("PaperPilot.Rag.ExporterTests");
    private readonly ActivitySource _agent = new("Microsoft.Agents.AI.Workflows.ExporterTests");
    private readonly ActivitySource _aspNetCore = new("Microsoft.AspNetCore.ExporterTests");
    private readonly ActivitySource _ingestion = new("PaperPilot.Ingestion.ExporterTests");
    private readonly CapturingProcessor _mainPipeline = new();

    private Dictionary<string, string?> Enabled => new()
    {
        ["Langfuse:Enabled"] = "true",
        ["Langfuse:BaseUrl"] = _langfuse.Url + "/",
        ["Langfuse:PublicKey"] = "pk-lf-test",
        ["Langfuse:SecretKey"] = "sk-lf-test",
    };

    [Fact]
    public async Task Only_rag_llm_and_agent_spans_are_posted_to_langfuse()
    {
        _langfuse.Given(Request.Create().WithPath("/api/public/otel/v1/traces").UsingPost()).RespondWith(Response.Create());
        using var host = Host(Enabled);
        var langfuse = host.Services.GetRequiredService<LangfuseTracing>();

        EmitSpans();

        langfuse.Provider.ForceFlush(10_000).ShouldBeTrue();
        await WhenLogged();
        var request = _langfuse.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Authorization"].ShouldBe([$"Basic {Convert.ToBase64String("pk-lf-test:sk-lf-test"u8)}"]);
        request.Headers["Content-Type"].ShouldBe(["application/x-protobuf"]);
        var payload = Encoding.Latin1.GetString(request.BodyAsBytes!);
        payload.ShouldContain("paperpilot-span");
        payload.ShouldContain("agent-span");
        payload.ShouldNotContain("aspnet-span");
        payload.ShouldNotContain("ingestion-span");
    }

    [Fact]
    public void The_main_pipeline_still_gets_every_span_when_langfuse_is_on()
    {
        _langfuse.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create());
        using var host = Host(Enabled);
        host.Services.GetRequiredService<LangfuseTracing>();
        host.Services.GetRequiredService<TracerProvider>();

        EmitSpans();

        _mainPipeline.Ended.ShouldBe(["ingestion-span", "agent-span", "paperpilot-span", "aspnet-span"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("false", "pk", "sk")]
    [InlineData("true", "", "sk")]
    [InlineData("true", "pk", "")]
    public void Without_langfuse_or_its_keys_there_is_no_langfuse_exporter(string enabled, string publicKey, string secretKey)
    {
        using var host = Host(new()
        {
            ["Langfuse:Enabled"] = enabled,
            ["Langfuse:BaseUrl"] = _langfuse.Url,
            ["Langfuse:PublicKey"] = publicKey,
            ["Langfuse:SecretKey"] = secretKey,
        });

        host.Services.GetService<LangfuseTracing>().ShouldBeNull();
    }

    public void Dispose()
    {
        _paperPilot.Dispose();
        _agent.Dispose();
        _ingestion.Dispose();
        _aspNetCore.Dispose();
        _langfuse.Dispose();
    }

    private void EmitSpans()
    {
        using (_aspNetCore.StartActivity("aspnet-span"))
        using (_paperPilot.StartActivity("paperpilot-span"))
        using (_agent.StartActivity("agent-span"))
        using (_ingestion.StartActivity("ingestion-span"))
        {
        }
    }

    /// <summary>WireMock logs a request only after the client already has the response.</summary>
    private async Task WhenLogged()
    {
        for (var i = 0; i < 50 && _langfuse.LogEntries.Count == 0; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A host whose main tracer provider, like ServiceDefaults', listens to everything.</summary>
    private IHost Host(Dictionary<string, string?> settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource(_paperPilot.Name, _agent.Name, _aspNetCore.Name, _ingestion.Name)
            .AddProcessor(_mainPipeline));
        builder.AddLangfuseExporter();
        return builder.Build();
    }

    private sealed class CapturingProcessor : BaseProcessor<Activity>
    {
        public List<string> Ended { get; } = [];

        public override void OnEnd(Activity data) => Ended.Add(data.DisplayName);
    }
}
