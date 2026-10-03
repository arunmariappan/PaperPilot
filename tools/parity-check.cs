#:property PublishAot=false

// Parity check (phase 8): runs the query set in tests/fixtures/parity-queries.json against an API, and compares the
// Python stack's answers with PaperPilot's in docs/parity-report.md.
//
//   dotnet run tools/parity-check.cs -- record  --base http://localhost:8000 --out docs/parity/python.json
//   dotnet run tools/parity-check.cs -- compare --base http://localhost:8100 --recorded docs/parity/python.json
//   dotnet run tools/parity-check.cs -- report  --recorded docs/parity/python.json --paperpilot docs/parity/paperpilot.json
//
// compare = record PaperPilot (--out, default docs/parity/paperpilot.json) + report (--report, default
// docs/parity-report.md). report rebuilds the report from two recordings, e.g. after adding an explanation.
// Run the stacks one after the other, never together, against the same index and the same Ollama model.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static JsonText;

var mode = args.FirstOrDefault() ?? "";
var options = ParseArgs(args.Skip(1).ToArray());
var queriesPath = options.GetValueOrDefault("queries", "tests/fixtures/parity-queries.json");
var queries = JsonNode.Parse(File.ReadAllText(queriesPath))!.AsObject();

switch (mode)
{
    case "record":
        Save(await RecordAsync(Required("base"), queries), options.GetValueOrDefault("out", "docs/parity/python.json"));
        return 0;
    case "compare":
    {
        var python = Load(Required("recorded"));
        var paperpilotPath = options.GetValueOrDefault("out", "docs/parity/paperpilot.json");
        var paperpilot = await RecordAsync(Required("base"), queries);
        Save(paperpilot, paperpilotPath);
        WriteReport(python, paperpilot, queries, queriesPath, options.GetValueOrDefault("report", "docs/parity-report.md"));
        return 0;
    }
    case "report":
        WriteReport(Load(Required("recorded")), Load(Required("paperpilot")), queries, queriesPath,
            options.GetValueOrDefault("report", "docs/parity-report.md"));
        return 0;
    default:
        Console.Error.WriteLine("Usage: parity-check.cs record|compare|report [--base URL] [--out FILE] [--recorded FILE] "
            + "[--paperpilot FILE] [--report FILE] [--queries FILE]");
        return 2;
}

string Required(string name) =>
    options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"--{name} is required for {mode}");

// ---------------------------------------------------------------------------------------------------------------------
// Recording

static async Task<JsonObject> RecordAsync(string baseUrl, JsonObject queries)
{
    using var http = new HttpClient
    {
        BaseAddress = new Uri($"{baseUrl.TrimEnd('/')}/api/v1/"),
        Timeout = TimeSpan.FromMinutes(20),
    };

    var recording = new JsonObject
    {
        ["base"] = baseUrl,
        ["recorded_at"] = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture),
        ["health"] = (await CallAsync(http, HttpMethod.Get, "health", null))["body"]?.DeepClone(),
    };

    var search = new JsonArray();
    foreach (var query in queries["search"]!.AsArray())
    {
        var result = await CallAsync(http, HttpMethod.Post, "hybrid-search/", query!["request"]);
        search.Add(new JsonObject { ["id"] = Id(query), ["result"] = result });
        Progress(query, result);
    }

    var ask = new JsonArray();
    foreach (var query in queries["ask"]!.AsArray())
    {
        var miss = await CallAsync(http, HttpMethod.Post, "ask", query!["request"]);
        Progress(query, miss);
        var hit = await CallAsync(http, HttpMethod.Post, "ask", query["request"]);
        Progress(query, hit);
        ask.Add(new JsonObject { ["id"] = Id(query), ["miss"] = miss, ["hit"] = hit });
    }

    var agentic = new JsonArray();
    foreach (var query in queries["agentic"]!.AsArray())
    {
        var result = await CallAsync(http, HttpMethod.Post, "ask-agentic", new JsonObject { ["query"] = Text(query!["query"]) });
        agentic.Add(new JsonObject { ["id"] = Id(query), ["result"] = result });
        Progress(query, result);
    }

    recording["search"] = search;
    recording["ask"] = ask;
    recording["agentic"] = agentic;
    return recording;
}

static async Task<JsonObject> CallAsync(HttpClient http, HttpMethod method, string path, JsonNode? body)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null)
    {
        request.Content = JsonContent.Create(body);
    }

    var stopwatch = Stopwatch.StartNew();
    try
    {
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            json = JsonValue.Create(text);
        }

        return new JsonObject
        {
            ["status"] = (int)response.StatusCode,
            ["ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds),
            ["body"] = json,
        };
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return new JsonObject { ["status"] = 0, ["ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds), ["body"] = ex.Message };
    }
}

static void Progress(JsonNode query, JsonObject result) =>
    Console.WriteLine($"{Id(query)}  {Num(result["status"])}  {Num(result["ms"]) / 1000:0.0} s");

// ---------------------------------------------------------------------------------------------------------------------
// Report

static void WriteReport(JsonObject python, JsonObject paperpilot, JsonObject queries, string queriesPath, string path)
{
    var report = new Report(python, paperpilot, queries, queriesPath);
    var markdown = report.Build();
    File.WriteAllText(path, markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
    Console.WriteLine($"Wrote {path}: {report.Differences.Count} differences, {report.Unexplained} unexplained");
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            parsed[args[i][2..]] = args[++i];
        }
    }

    return parsed;
}

static JsonObject Load(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

static void Save(JsonObject recording, string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, recording.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine($"Saved {path}");
}

internal static class JsonText
{
    public static string Id(JsonNode? query) => Text(query?["id"]);

    public static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>A number from parsed JSON (a <c>JsonElement</c>) or from one this tool built (an <c>int</c> or <c>double</c>).</summary>
    public static double Num(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value when value.TryGetValue<int>(out var integer) => integer,
        _ => 0,
    };
}

/// <summary>A difference between the stacks (or from a label) and the behaviour change that explains it, if any.</summary>
internal sealed record Difference(string Key, string What, string? Explanation);

internal sealed partial class Report(JsonObject python, JsonObject paperpilot, JsonObject queries, string queriesPath)
{
    private const double MinJaccard = 0.8;

    private readonly StringBuilder _md = new();

    public List<Difference> Differences { get; } = [];

    public int Unexplained => Differences.Count(d => d.Explanation is null);

    public string Build()
    {
        var search = SearchSection();
        var ask = AskSection();
        var agentic = AgenticSection();

        Line("# Parity report: PaperPilot and the Python stack");
        Line();
        Line($"Generated by `tools/parity-check.cs` from `{queriesPath.Replace('\\', '/')}`. Python recorded "
            + $"{Text(python["recorded_at"])} from {Text(python["base"])}; PaperPilot recorded "
            + $"{Text(paperpilot["recorded_at"])} from {Text(paperpilot["base"])}. The raw responses are in "
            + "[docs/parity/](parity/).");
        Line();
        Line("## How it was run");
        Line();
        foreach (var line in queries["setup"]?.AsArray() ?? [])
        {
            Line($"- {Text(line)}");
        }

        Line();
        Line("## Summary");
        Line();
        Line("| Check | Expected | Python | PaperPilot | Result |");
        Line("|---|---|---|---|---|");
        foreach (var row in search.Concat(ask).Concat(agentic))
        {
            Line(row);
        }

        Line();
        Line($"**Differences:** {Differences.Count}, of which **{Unexplained} unexplained**. Each one is listed under "
            + "[Differences](#differences) with the behaviour change that explains it "
            + "([behaviour-changes.md](plan/behaviour-changes.md)).");
        Line();
        Line(_details.ToString());
        Line("## Differences");
        Line();
        if (Differences.Count == 0)
        {
            Line("None.");
        }

        foreach (var difference in Differences)
        {
            Line($"- `{difference.Key}`: {difference.What} — "
                + (difference.Explanation is { } why ? why : "**unexplained**"));
        }

        Line();
        Line("## Answers side by side");
        Line();
        Line("LLM text isn't deterministic, so answers are compared by reading them, not by a rule.");
        Line();
        Line(_answers.ToString());
        return _md.ToString().TrimEnd() + "\n";
    }

    private readonly StringBuilder _details = new();
    private readonly StringBuilder _answers = new();

    // --- /hybrid-search/ ---------------------------------------------------------------------------------------------

    private IEnumerable<string> SearchSection()
    {
        var bm25Identical = 0;
        var bm25Count = 0;
        var jaccards = new List<double>();

        Detail("## `/hybrid-search/`");
        Detail();
        Detail("BM25 results must come back in the same order; hybrid results must overlap by at least 0.8 (Jaccard on "
            + "chunk IDs). Totals are the number of matches each API reports.");
        Detail();
        Detail("| ID | Query | Request | Python total / mode | PaperPilot total / mode | Result |");
        Detail("|---|---|---|---|---|---|");

        foreach (var query in queries["search"]!.AsArray())
        {
            var id = Text(query!["id"]);
            var request = query["request"]!.AsObject();
            var hybrid = request["use_hybrid"]?.GetValue<bool>() ?? true;
            var py = Result(python, "search", id);
            var pp = Result(paperpilot, "search", id);
            var pyHits = Hits(py);
            var ppHits = Hits(pp);
            string result;

            if (Status(py) != 200 || Status(pp) != 200)
            {
                result = $"status {Status(py)} / {Status(pp)}";
                Diff($"{id}.status", $"status {Status(py)} (Python) vs {Status(pp)} (PaperPilot)");
            }
            else if (!hybrid)
            {
                bm25Count++;
                var firstDifference = Enumerable.Range(0, Math.Max(pyHits.Count, ppHits.Count))
                    .FirstOrDefault(i => i >= pyHits.Count || i >= ppHits.Count || pyHits[i] != ppHits[i], -1);
                if (firstDifference < 0)
                {
                    bm25Identical++;
                    result = $"identical ({ppHits.Count} hits)";
                }
                else
                {
                    result = $"differs from #{firstDifference + 1}";
                    Diff($"{id}.order", $"BM25 order differs from hit {firstDifference + 1}");
                }
            }
            else
            {
                var jaccard = Jaccard(pyHits, ppHits);
                jaccards.Add(jaccard);
                result = $"Jaccard {jaccard:0.00}";
                if (jaccard < 1)
                {
                    Diff($"{id}.overlap", $"hybrid overlap {jaccard:0.00} ({pyHits.Count} vs {ppHits.Count} hits)");
                }
            }

            var pyTotal = Num(Body(py)?["total"]);
            var ppTotal = Num(Body(pp)?["total"]);
            if (pyTotal != ppTotal && Status(py) == 200 && Status(pp) == 200)
            {
                Diff($"{id}.total", $"total {pyTotal} (Python) vs {ppTotal} (PaperPilot)");
            }

            var pyMode = Text(Body(py)?["search_mode"]);
            var ppMode = Text(Body(pp)?["search_mode"]);
            if (pyMode != ppMode)
            {
                Diff($"{id}.mode", $"search_mode `{pyMode}` (Python) vs `{ppMode}` (PaperPilot)");
            }

            Detail($"| {id} | {Cell(Text(request["query"]))} | {Cell(Describe(request))} | {pyTotal} / {pyMode} | "
                + $"{ppTotal} / {ppMode} | {result} |");
        }

        Detail();
        yield return $"| `/hybrid-search/` BM25 order | identical | | {bm25Identical} of {bm25Count} identical | "
            + Pass(bm25Identical == bm25Count) + " |";
        var minimum = jaccards.Count > 0 ? jaccards.Min() : 1;
        yield return $"| `/hybrid-search/` hybrid overlap | ≥ {MinJaccard} Jaccard | | min {minimum:0.00}, mean "
            + $"{(jaccards.Count > 0 ? jaccards.Average() : 1):0.00} over {jaccards.Count} queries | "
            + Pass(minimum >= MinJaccard) + " |";
    }

    private static List<string> Hits(JsonObject? result) =>
        Body(result)?["hits"]?.AsArray().Select(h => Text(h!["chunk_id"]) is { Length: > 0 } chunk
            ? chunk
            : $"{Text(h["arxiv_id"])}:{Text(h["chunk_text"]).GetHashCode(StringComparison.Ordinal)}").ToList() ?? [];

    private static double Jaccard(List<string> a, List<string> b)
    {
        var union = a.Union(b).Count();
        return union == 0 ? 1 : (double)a.Intersect(b).Count() / union;
    }

    private static string Describe(JsonObject request)
    {
        var parts = new List<string> { (request["use_hybrid"]?.GetValue<bool>() ?? true) ? "hybrid" : "BM25" };
        parts.Add($"size {Num(request["size"] ?? JsonValue.Create(10))}");
        if (request["from"] is { } from && Num(from) > 0)
        {
            parts.Add($"from {Num(from)}");
        }

        if (request["categories"] is JsonArray categories)
        {
            parts.Add(string.Join(", ", categories.Select(c => Text(c))));
        }

        if (request["latest_papers"]?.GetValue<bool>() == true)
        {
            parts.Add("latest");
        }

        return string.Join(" · ", parts);
    }

    // --- /ask ----------------------------------------------------------------------------------------------------------

    private IEnumerable<string> AskSection()
    {
        var chunksIdentical = 0;
        var count = 0;
        var pyMiss = new List<double>();
        var ppMiss = new List<double>();
        var pyHit = new List<double>();
        var ppHit = new List<double>();

        Detail("## `/ask`");
        Detail();
        Detail("Retrieval must match: the same number of chunks and the same source papers. Latency is the first call "
            + "(cache miss) and the second (cache hit).");
        Detail();
        Detail("| ID | Question | Chunks Py / PP | Same sources | Mode Py / PP | Miss Py / PP | Hit Py / PP |");
        Detail("|---|---|---|---|---|---|---|");

        foreach (var query in queries["ask"]!.AsArray())
        {
            var id = Text(query!["id"]);
            var question = Text(query["request"]!["query"]);
            var py = Entry(python, "ask", id);
            var pp = Entry(paperpilot, "ask", id);
            var pyBody = Body(py["miss"]);
            var ppBody = Body(pp["miss"]);
            count++;

            var pyChunks = Num(pyBody?["chunks_used"]);
            var ppChunks = Num(ppBody?["chunks_used"]);
            if (Status(py["miss"]) == 200 && Status(pp["miss"]) == 200 && pyChunks == ppChunks)
            {
                chunksIdentical++;
            }
            else
            {
                Diff($"{id}.chunks", $"chunks used {pyChunks} (Python, status {Status(py["miss"])}) vs {ppChunks} "
                    + $"(PaperPilot, status {Status(pp["miss"])})");
            }

            var pySources = Sources(pyBody);
            var ppSources = Sources(ppBody);
            var sameSources = pySources.ToHashSet().SetEquals(ppSources);
            if (!sameSources)
            {
                Diff($"{id}.sources", $"sources differ: Python [{string.Join(", ", pySources)}], PaperPilot "
                    + $"[{string.Join(", ", ppSources)}]");
            }

            var pyMode = Text(pyBody?["search_mode"]);
            var ppMode = Text(ppBody?["search_mode"]);
            if (pyMode != ppMode)
            {
                Diff($"{id}.mode", $"search_mode `{pyMode}` (Python) vs `{ppMode}` (PaperPilot)");
            }

            pyMiss.Add(Num(py["miss"]?["ms"]));
            ppMiss.Add(Num(pp["miss"]?["ms"]));
            pyHit.Add(Num(py["hit"]?["ms"]));
            ppHit.Add(Num(pp["hit"]?["ms"]));

            Detail($"| {id} | {Cell(question)} | {pyChunks} / {ppChunks} | {(sameSources ? "yes" : "no")} | {pyMode} / "
                + $"{ppMode} | {Seconds(Num(py["miss"]?["ms"]))} / {Seconds(Num(pp["miss"]?["ms"]))} | "
                + $"{Seconds(Num(py["hit"]?["ms"]))} / {Seconds(Num(pp["hit"]?["ms"]))} |");

            Answer($"<details><summary><code>{id}</code> {Html(question)}</summary>");
            Answer();
            Answer("**Python**");
            Answer();
            Answer(Quote(Text(pyBody?["answer"]) is { Length: > 0 } a ? a : $"(status {Status(py["miss"])})"));
            Answer();
            Answer("**PaperPilot**");
            Answer();
            Answer(Quote(Text(ppBody?["answer"]) is { Length: > 0 } b ? b : $"(status {Status(pp["miss"])})"));
            Answer();
            Answer("</details>");
            Answer();
        }

        Detail();
        var missBetter = Median(ppMiss) <= Median(pyMiss);
        var hitBetter = Median(ppHit) <= Median(pyHit);
        if (!missBetter)
        {
            Diff("latency.miss", $"PaperPilot's p50 for a cache miss ({Seconds(Median(ppMiss))}) is slower than "
                + $"Python's ({Seconds(Median(pyMiss))})");
        }

        if (!hitBetter)
        {
            Diff("latency.hit", $"PaperPilot's p50 for a cache hit ({Seconds(Median(ppHit))}) is slower than "
                + $"Python's ({Seconds(Median(pyHit))})");
        }

        yield return $"| `/ask` chunks used | identical | | {chunksIdentical} of {count} identical | "
            + Pass(chunksIdentical == count) + " |";
        yield return $"| `/ask` latency p50, cache miss | same or better | {Seconds(Median(pyMiss))} | "
            + $"{Seconds(Median(ppMiss))} | {Pass(missBetter)} |";
        yield return $"| `/ask` latency p50, cache hit | same or better | {Seconds(Median(pyHit))} | "
            + $"{Seconds(Median(ppHit))} | {Pass(hitBetter)} |";
    }

    private static List<string> Sources(JsonNode? body) =>
        body?["sources"] is JsonArray sources ? [.. sources.Select(s => Text(s))] : [];

    // --- /ask-agentic ----------------------------------------------------------------------------------------------

    private IEnumerable<string> AgenticSection()
    {
        var pyAgree = 0;
        var ppAgree = 0;
        var labelled = 0;
        var pyAnswered = 0;
        var ppAnswered = 0;
        var pyWithSources = 0;
        var ppWithSources = 0;

        Detail("## `/ask-agentic`");
        Detail();
        Detail("Each question is labelled in scope or out of scope. A stack counts a question as in scope when it went on "
            + "to retrieve (`retrieval_attempts > 0`). An answer is \"answered\" when it is in scope and isn't the "
            + "max-attempts or search-unavailable apology; answered questions should list sources (B1).");
        Detail();
        Detail("| ID | Question | Label | Python score → decision | PaperPilot score → decision | Attempts Py / PP | "
            + "Sources Py / PP | Time Py / PP |");
        Detail("|---|---|---|---|---|---|---|---|");

        foreach (var query in queries["agentic"]!.AsArray())
        {
            var id = Text(query!["id"]);
            var question = Text(query["query"]);
            var label = query["in_scope"]!.GetValue<bool>();
            var py = Result(python, "agentic", id);
            var pp = Result(paperpilot, "agentic", id);
            labelled++;

            var (pyScore, pyInScope) = Decision(py);
            var (ppScore, ppInScope) = Decision(pp);
            if (pyInScope == label)
            {
                pyAgree++;
            }

            if (ppInScope == label)
            {
                ppAgree++;
            }
            else
            {
                Diff($"{id}.label", $"PaperPilot judged \"{question}\" {(ppInScope ? "in" : "out of")} scope "
                    + $"(score {ppScore}), against the label");
            }

            if (pyInScope != ppInScope)
            {
                Diff($"{id}.decision", $"the stacks disagree on scope: Python {pyScore}, PaperPilot {ppScore}");
            }

            var pySources = Sources(Body(py)).Count;
            var ppSources = Sources(Body(pp)).Count;
            if (Answered(py, pyInScope))
            {
                pyAnswered++;
                pyWithSources += pySources > 0 ? 1 : 0;
            }

            if (Answered(pp, ppInScope))
            {
                ppAnswered++;
                if (ppSources > 0)
                {
                    ppWithSources++;
                }
                else
                {
                    Diff($"{id}.sources", "PaperPilot answered without sources");
                }
            }

            Detail($"| {id} | {Cell(question)} | {(label ? "in" : "out")} | {pyScore} → {Scope(pyInScope)} | "
                + $"{ppScore} → {Scope(ppInScope)} | {Num(Body(py)?["retrieval_attempts"])} / "
                + $"{Num(Body(pp)?["retrieval_attempts"])} | {pySources} / {ppSources} | {Seconds(Num(py?["ms"]))} / "
                + $"{Seconds(Num(pp?["ms"]))} |");
        }

        Detail();
        if (pyAnswered > 0 && pyWithSources < pyAnswered)
        {
            Diff("agentic.sources", $"Python answered {pyAnswered} questions with sources on {pyWithSources}");
        }

        yield return $"| `/ask-agentic` guardrail agrees with the labels | ≥ Python | {pyAgree} of {labelled} | "
            + $"{ppAgree} of {labelled} | {Pass(ppAgree >= pyAgree)} |";
        yield return $"| `/ask-agentic` sources when answered | yes (B1) | {pyWithSources} of {pyAnswered} | "
            + $"{ppWithSources} of {ppAnswered} | {Pass(ppWithSources == ppAnswered)} |";
    }

    private static (string Score, bool InScope) Decision(JsonObject? result)
    {
        var body = Body(result);
        var steps = body?["reasoning_steps"] is JsonArray array ? array.Select(s => Text(s)).ToList() : [];
        var score = steps.Select(s => ScoreStep().Match(s)).FirstOrDefault(m => m.Success)?.Groups[1].Value ?? "?";
        return (score, Num(body?["retrieval_attempts"]) > 0);
    }

    private static bool Answered(JsonObject? result, bool inScope)
    {
        var answer = Text(Body(result)?["answer"]);
        return Status(result) == 200 && inScope
            && !answer.StartsWith("I apologize, but I couldn't find relevant", StringComparison.Ordinal)
            && !answer.Contains("search is unavailable", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"score: (\d+)/100")]
    private static partial Regex ScoreStep();

    // --- helpers -----------------------------------------------------------------------------------------------------

    private void Diff(string key, string what) => Differences.Add(new Difference(key, what, Explain(key)));

    /// <summary>An explanation for <c>s04.total</c> is looked up as <c>s04.total</c>, then <c>search.total</c>.</summary>
    private string? Explain(string key)
    {
        var explanations = queries["explanations"]?.AsObject();
        if (explanations is null)
        {
            return null;
        }

        if (explanations[key] is { } exact)
        {
            return Text(exact);
        }

        var (id, check) = (key[..key.IndexOf('.', StringComparison.Ordinal)], key[(key.IndexOf('.', StringComparison.Ordinal) + 1)..]);
        var section = id[0] switch { 's' => "search", 'a' => "ask", 'g' => "agentic", _ => id };
        return explanations[$"{section}.{check}"] is { } general ? Text(general) : null;
    }

    private static JsonObject? Result(JsonObject recording, string section, string id) =>
        Entry(recording, section, id)["result"]?.AsObject();

    private static JsonObject Entry(JsonObject recording, string section, string id) =>
        recording[section]!.AsArray().First(e => Text(e!["id"]) == id)!.AsObject();

    private static JsonNode? Body(JsonNode? result) => result?["body"];

    private static int Status(JsonNode? result) => (int)Num(result?["status"]);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static string Seconds(double ms) =>
        ms < 1000 ? $"{ms:0} ms" : $"{(ms / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s";

    private static string Pass(bool pass) => pass ? "pass" : "**FAIL**";

    private static string Scope(bool inScope) => inScope ? "in" : "out";

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Trim() is { Length: > 0 } t ? t : "(blank)";

    private static string Html(string text) => System.Net.WebUtility.HtmlEncode(text);

    private static string Quote(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => $"> {l}".TrimEnd()));

    private void Line(string text = "") => _md.Append(text).Append('\n');

    private void Detail(string text = "") => _details.Append(text).Append('\n');

    private void Answer(string text = "") => _answers.Append(text).Append('\n');
}
