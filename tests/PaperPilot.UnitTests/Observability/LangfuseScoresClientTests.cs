using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Observability;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Observability;

public sealed class LangfuseScoresClientTests : IDisposable
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    private readonly WireMockServer _langfuse = WireMockServer.Start();
    private readonly List<IHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IRequestBuilder ScoresRequest => Request.Create().WithPath("/api/public/scores").UsingPost();

    [Fact]
    public async Task A_score_is_posted_with_basic_auth()
    {
        _langfuse.Given(ScoresRequest).RespondWith(Response.Create().WithBody("""{"id":"x"}"""));

        await Client().SubmitAsync(TraceId, -1, "Wrong paper.", Ct);

        var request = _langfuse.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Authorization"].ShouldBe([$"Basic {Convert.ToBase64String("pk-lf-test:sk-lf-test"u8)}"]);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        body.Select(p => p.Key).ShouldBe(["id", "traceId", "name", "value", "dataType", "comment"], ignoreOrder: true);
        Guid.TryParse(body["id"]!.GetValue<string>(), out _).ShouldBeTrue();
        (body["traceId"]!.GetValue<string>(), body["name"]!.GetValue<string>(), body["dataType"]!.GetValue<string>())
            .ShouldBe((TraceId, "user-feedback", "NUMERIC"));
        (body["value"]!.GetValue<double>(), body["comment"]!.GetValue<string>()).ShouldBe((-1, "Wrong paper."));
    }

    [Fact]
    public async Task A_missing_comment_is_left_out()
    {
        _langfuse.Given(ScoresRequest).RespondWith(Response.Create().WithBody("""{"id":"x"}"""));

        await Client().SubmitAsync(TraceId, 1, null, Ct);

        JsonNode.Parse(_langfuse.LogEntries.Single().RequestMessage!.Body!)!.AsObject().ContainsKey("comment").ShouldBeFalse();
    }

    [Fact]
    public async Task A_rejected_score_throws_with_langfuses_reason()
    {
        _langfuse.Given(ScoresRequest).RespondWith(Response.Create().WithStatusCode(401).WithBody("""{"message":"Invalid credentials"}"""));

        var error = await Should.ThrowAsync<LangfuseException>(() => Client().SubmitAsync(TraceId, 1, null, Ct));

        error.Message.ShouldBe("""Langfuse returned 401: {"message":"Invalid credentials"}""");
    }

    [Theory]
    [InlineData("false", "pk-lf-test")]
    [InlineData("true", "")]
    public void It_is_disabled_without_langfuse_or_its_keys(string enabled, string publicKey) =>
        Client(new() { ["Langfuse:Enabled"] = enabled, ["Langfuse:PublicKey"] = publicKey }).IsEnabled.ShouldBeFalse();

    public void Dispose()
    {
        _hosts.ForEach(h => h.Dispose());
        _langfuse.Dispose();
    }

    private LangfuseScoresClient Client(Dictionary<string, string?>? settings = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Langfuse:Enabled"] = "true",
            ["Langfuse:BaseUrl"] = _langfuse.Url,
            ["Langfuse:PublicKey"] = "pk-lf-test",
            ["Langfuse:SecretKey"] = "sk-lf-test",
        });
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.AddPaperPilotOptions();
        builder.AddPaperPilotLangfuseScores();
        var host = builder.Build();
        _hosts.Add(host);
        return host.Services.GetRequiredService<LangfuseScoresClient>();
    }
}
