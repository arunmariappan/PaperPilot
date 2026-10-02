#:property PublishAot=false

// Copies the chunk index, embeddings included, from one OpenSearch cluster into PaperPilot's (N4).
// The target index must already exist: start PaperPilot first, so the API creates it with PaperPilot's mapping.
//
//   dotnet run tools/seed-opensearch.cs -- --source http://localhost:9200 --target http://localhost:9210
//
// Options: --index (default arxiv-papers-chunks), --batch (default 200).
// Documents get PaperPilot's deterministic id {arxiv_id}:{chunk_index} (C5), so running it twice is harmless.

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

var options = ParseArgs(args);
var source = options.GetValueOrDefault("source", "http://localhost:9200").TrimEnd('/');
var target = options.GetValueOrDefault("target", "http://localhost:9210").TrimEnd('/');
var index = options.GetValueOrDefault("index", "arxiv-papers-chunks");
var batchSize = int.Parse(options.GetValueOrDefault("batch", "200"), CultureInfo.InvariantCulture);

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

var sourceCount = await CountAsync(http, source, index);
if (sourceCount is null)
{
    return Fail($"Source index {source}/{index} doesn't exist.");
}

if (await CountAsync(http, target, index) is null)
{
    return Fail($"Target index {target}/{index} doesn't exist. Start PaperPilot first so the API creates it.");
}

Console.WriteLine($"Copying {sourceCount} documents from {source}/{index} to {target}/{index}");

var copied = 0;
var failed = 0;
var page = await PostAsync(http, $"{source}/{index}/_search?scroll=2m", new JsonObject
{
    ["size"] = batchSize,
    ["sort"] = new JsonArray("_doc"),
    ["query"] = new JsonObject { ["match_all"] = new JsonObject() },
});

while (page["hits"]!["hits"]!.AsArray() is { Count: > 0 } hits)
{
    var bulk = new StringBuilder();
    foreach (var hit in hits)
    {
        var document = hit!["_source"]!.AsObject().DeepClone().AsObject();
        var id = $"{document["arxiv_id"]}:{document["chunk_index"]}";
        document["chunk_id"] = id;
        bulk.Append(new JsonObject { ["index"] = new JsonObject { ["_index"] = index, ["_id"] = id } }.ToJsonString()).Append('\n');
        bulk.Append(document.ToJsonString()).Append('\n');
    }

    using var content = new StringContent(bulk.ToString(), Encoding.UTF8, "application/x-ndjson");
    using var response = await http.PostAsync(new Uri($"{target}/_bulk?refresh=wait_for"), content);
    response.EnsureSuccessStatusCode();
    var result = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    foreach (var item in result["items"]!.AsArray())
    {
        var status = item!["index"]!["status"]!.GetValue<int>();
        if (status is >= 200 and < 300)
        {
            copied++;
        }
        else
        {
            failed++;
            if (failed <= 5)
            {
                Console.Error.WriteLine($"  {item["index"]!["_id"]}: {item["index"]!["error"]?["reason"]}");
            }
        }
    }

    Console.WriteLine($"  {copied + failed}/{sourceCount}");
    page = await PostAsync(http, $"{source}/_search/scroll", new JsonObject
    {
        ["scroll"] = "2m",
        ["scroll_id"] = page["_scroll_id"]!.GetValue<string>(),
    });
}

using (var clear = new HttpRequestMessage(HttpMethod.Delete, new Uri($"{source}/_search/scroll"))
{
    Content = JsonContent.Create(new JsonObject { ["scroll_id"] = page["_scroll_id"]!.GetValue<string>() }),
})
{
    using var _ = await http.SendAsync(clear);
}

var targetCount = await CountAsync(http, target, index);
Console.WriteLine($"Copied {copied}, failed {failed}. Source has {sourceCount} documents, target has {targetCount}.");
return failed == 0 && targetCount == sourceCount ? 0 : 1;

static async Task<long?> CountAsync(HttpClient http, string host, string index)
{
    using var response = await http.GetAsync(new Uri($"{host}/{index}/_count"));
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null;
    }

    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonObject>())!["count"]!.GetValue<long>();
}

static async Task<JsonObject> PostAsync(HttpClient http, string url, JsonObject body)
{
    using var response = await http.PostAsJsonAsync(new Uri(url), body);
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var options = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        options[args[i].TrimStart('-')] = args[i + 1];
    }

    return options;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
