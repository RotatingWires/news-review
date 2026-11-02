using System.Text.Json.Nodes;

namespace NewsReview.Tests;

/// <summary>
/// Stored story URLs are deduplication keys and dates are stored text, so results must match the values
/// the earlier Python release produced. PythonCompatibility.json holds Python's outputs for edge cases.
/// </summary>
public class CompatibilityTests
{
    static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PythonCompatibility.json")))!;

    static JsonNode Outcome(Func<string> work)
    {
        try { return new JsonObject { ["ok"] = work() }; }
        catch (ValidationException) { return new JsonObject { ["error"] = "ValueError" }; }
    }

    static void Check(string section, Func<JsonNode, JsonNode?> actual)
    {
        foreach (var item in Golden[section]!.AsArray())
        {
            var expected = item!["expected"];
            var result = actual(item);
            if (expected is JsonObject e && e.ContainsKey("error")) Assert.True(result is JsonObject r && r.ContainsKey("error"), $"{section} {item.ToJsonString()} → {result?.ToJsonString()}");
            else Assert.True(JsonNode.DeepEquals(expected, result), $"{section} {item.ToJsonString()} → {result?.ToJsonString()}");
        }
    }

    [Fact] public void CanonicalUrls() => Check("canonical", item => Outcome(() => UrlTools.Canonical((string)item["input"]!)));
    [Fact] public void UrlValidation() => Check("valid", item => Outcome(() => UrlTools.Valid((string)item["input"]!)));
    [Fact] public void RelativeUrlJoins() => Check("join", item => UrlTools.Join((string)item["base"]!, (string)item["url"]!));
    [Fact] public void FeedDates() => Check("dates", item => Feeds.ParseDate((string)item["input"]!));
    [Fact] public void PlainText() => Check("plain", item => Feeds.Plain((string)item["input"]!));
}
