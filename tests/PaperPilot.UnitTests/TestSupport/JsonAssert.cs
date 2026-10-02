using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperPilot.UnitTests.TestSupport;

/// <summary>Structural JSON comparison: object key order is ignored and numbers compare by value (1.0 == 1).</summary>
internal static class JsonAssert
{
    public static void Equivalent(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        switch (expected)
        {
            case null:
                actual.ShouldBeNull(path);
                break;
            case JsonObject obj:
                var actualObject = actual.ShouldBeOfType<JsonObject>(path);
                actualObject.Select(p => p.Key).ShouldBe(obj.Select(p => p.Key), ignoreOrder: true, path);
                foreach (var (key, node) in obj)
                {
                    Equivalent(node, actualObject[key], $"{path}.{key}");
                }

                break;
            case JsonArray array:
                var actualArray = actual.ShouldBeOfType<JsonArray>(path);
                actualArray.Count.ShouldBe(array.Count, path);
                for (var i = 0; i < array.Count; i++)
                {
                    Equivalent(array[i], actualArray[i], $"{path}[{i}]");
                }

                break;
            default:
                actual.ShouldNotBeNull(path);
                if (expected.GetValueKind() == JsonValueKind.Number)
                {
                    actual.GetValueKind().ShouldBe(JsonValueKind.Number, path);
                    Number(actual).ShouldBe(Number(expected), path);
                }
                else
                {
                    actual.ToJsonString().ShouldBe(expected.ToJsonString(), path);
                }

                break;
        }
    }

    private static decimal Number(JsonNode node) => decimal.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>Reads the JSON fixtures recorded from the Python implementation (tests/fixtures/python-parity).</summary>
internal static class ParityFixtures
{
    public static JsonNode Load(string relativePath) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "python-parity", relativePath)))!;
}
