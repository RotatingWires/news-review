using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// The API speaks camelCase like TaskList, TaskList Stats, and SpendTracker, while the code, settings, and database keep
/// their snake_case names. Only identifier-shaped keys change, so URLs and other data used as keys pass through as-is.
/// </summary>
static partial class JsonCase
{
    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)+$")] private static partial Regex SnakeKey();
    [GeneratedRegex("^[a-z][a-z0-9]*([A-Z][a-z0-9]*)+$")] private static partial Regex CamelKey();

    /// <summary>Request objects whose keys are the user's own text (topic renames), copied without renaming.</summary>
    static readonly HashSet<string> UserKeyed = ["renames"];

    public static string Camel(string key)
    {
        if (!SnakeKey().IsMatch(key)) return key;
        var parts = key.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    public static string Snake(string key)
    {
        if (!CamelKey().IsMatch(key)) return key;
        var builder = new StringBuilder(key.Length + 4);
        foreach (var c in key)
        {
            if (char.IsAsciiLetterUpper(c)) builder.Append('_').Append(char.ToLowerInvariant(c));
            else builder.Append(c);
        }
        return builder.ToString();
    }

    public static JsonNode? ToCamel(JsonNode? node) => Rename(node, Camel);

    public static JsonNode? ToSnake(JsonNode? node) => Rename(node, Snake);

    static JsonNode? Rename(JsonNode? node, Func<string, string> rename) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(
            rename(pair.Key),
            UserKeyed.Contains(pair.Key) ? pair.Value?.DeepClone() : Rename(pair.Value, rename)))),
        JsonArray array => new JsonArray(array.Select(item => Rename(item, rename)).ToArray()),
        _ => node?.DeepClone(),
    };
}
