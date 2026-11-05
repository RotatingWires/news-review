using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NewsReview;

/// <summary>JSON helpers that preserve the strict typing of the original Python validation.</summary>
static class Json
{
    static readonly Regex IntegerToken = new(@"\A-?\d+\z", RegexOptions.CultureInvariant);
    public static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>dict.get(key, fallback): a missing key yields the fallback, an explicit null stays null.</summary>
    public static JsonNode? Get(JsonObject value, string key, JsonNode? fallback = null) =>
        value.TryGetPropertyValue(key, out var node) ? node : fallback?.DeepClone();

    public static bool IsString(JsonNode? node, out string text)
    {
        text = "";
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) { text = value.GetValue<string>(); return true; }
        return false;
    }

    /// <summary>Python `type(value) is int`: an integral JSON number, never a boolean or float.</summary>
    public static bool IsInteger(JsonNode? node, out long number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue<JsonElement>(out var element))
        {
            // Parsed JSON: "5" is an integer; "5.0" and "5e0" are floats, as in Python's json module.
            var raw = element.GetRawText();
            return IntegerToken.IsMatch(raw) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
        }
        if (value.TryGetValue<long>(out number)) return true;
        if (value.TryGetValue<int>(out var small)) { number = small; return true; }
        return false;
    }

    public static bool IsBool(JsonNode? node, out bool flag)
    {
        flag = false;
        if (node is not JsonValue value || value.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return false;
        flag = value.GetValue<bool>();
        return true;
    }

    /// <summary>An integer node as long, whether parsed from JSON text or created as int/long.</summary>
    public static long AsLong(this JsonNode node) =>
        IsInteger(node, out var number) ? number : throw new InvalidOperationException($"Expected an integer, found {node.ToJsonString()}");

    public static string Dump(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    public static JsonNode? Load(string text) => JsonNode.Parse(text);
}

/// <summary>Validate UI inputs before they reach persistence or network adapters.</summary>
static class Validation
{
    public static string Text(JsonNode? value, string name, int limit, bool empty = false)
    {
        if (!Json.IsString(value, out var text) || PyText.Length(text) > limit || (!empty && text.Trim().Length == 0))
            throw new ValidationException($"{name} must be {(empty ? "0" : "1")}–{limit} characters");
        return text.Trim();
    }

    public static long Integer(JsonNode? value, string name, long low, long high)
    {
        if (!Json.IsInteger(value, out var number) || number < low || number > high)
            throw new ValidationException($"{name} must be an integer from {low} to {high}");
        return number;
    }

    public static bool Boolean(JsonNode? value, string name)
    {
        if (!Json.IsBool(value, out var flag)) throw new ValidationException($"{name} must be true or false");
        return flag;
    }

    public static JsonArray TopicNames(JsonNode? value)
    {
        if (value is not JsonArray names || names.Any(name => !Json.IsString(name, out var text) || text.Trim().Length == 0 || PyText.Length(text) > 60))
            throw new ValidationException("Topics must be a list of names, each 1–60 characters");
        var texts = names.Select(name => (string)name!).ToList();
        if (texts.Distinct(StringComparer.Ordinal).Count() != texts.Count) throw new ValidationException("Topic names must be unique");
        return names;
    }

    public static JsonObject Source(JsonNode? value)
    {
        if (value is not JsonObject source) throw new ValidationException("Expected a source object");
        var feed = Text(Json.Get(source, "feed_url", ""), "Feed URL", 2048, empty: true);
        var mode = Json.Get(source, "mode", "auto");
        if (!Json.IsString(mode, out var modeText) || modeText is not ("auto" or "feed" or "page"))
            throw new ValidationException("Choose Automatic, RSS/Atom, or Website page");
        return new JsonObject
        {
            ["name"] = Text(Json.Get(source, "name"), "Source name", 100),
            ["url"] = Url(Json.Get(source, "url")),
            ["feed_url"] = feed.Length > 0 ? UrlTools.Valid(feed) : "",
            ["category"] = Text(Json.Get(source, "category"), "Category", 60),
            ["enabled"] = Boolean(Json.Get(source, "enabled", true), "Enabled"),
            ["mode"] = modeText,
            ["link_pattern"] = Text(Json.Get(source, "link_pattern", ""), "Article URL filter", 200, empty: true),
            ["stale_days"] = Integer(Json.Get(source, "stale_days", 14), "Inactivity warning days", 1, 365),
        };
    }

    public static string Url(JsonNode? value) => UrlTools.Valid(Json.IsString(value, out var text) ? text : null);

    public static JsonObject Settings(JsonNode? value, JsonObject current)
    {
        if (value is not JsonObject input || input.Any(pair => pair.Key is not ("ai" or "collection" or "interests")))
            throw new ValidationException("Unknown settings section");
        var result = (JsonObject)current.DeepClone();
        if (input.ContainsKey("ai"))
        {
            if (input["ai"] is not JsonObject ai || !Json.IsString(Json.Get(ai, "provider"), out var provider) || provider is not ("ollama" or "compatible" or "openrouter"))
                throw new ValidationException("Choose Ollama, a compatible server, or OpenRouter");
            result["ai"] = new JsonObject
            {
                ["provider"] = provider,
                ["base_url"] = Url(Json.Get(ai, "base_url")).TrimEnd('/'),
                ["model"] = Text(Json.Get(ai, "model"), "Model", 200, empty: true),
                ["timeout_seconds"] = Integer(Json.Get(ai, "timeout_seconds"), "Model timeout", 10, 600),
                ["max_input_chars"] = Integer(Json.Get(ai, "max_input_chars"), "Article input length", 2000, 40000),
                ["max_output_tokens"] = Integer(Json.Get(ai, "max_output_tokens", 2400), "Response tokens", 256, 8192),
                ["unload_after_use"] = Boolean(Json.Get(ai, "unload_after_use", true), "Unload after use"),
            };
        }
        if (input.ContainsKey("collection"))
        {
            if (input["collection"] is not JsonObject c) throw new ValidationException("Invalid collection settings");
            result["collection"] = new JsonObject
            {
                ["enabled"] = Boolean(Json.Get(c, "enabled"), "Schedule enabled"),
                ["interval_hours"] = Integer(Json.Get(c, "interval_hours", 24), "Collection interval (hours)", 1, 8760),
                ["hour"] = Integer(Json.Get(c, "hour", 7), "Hour", 0, 23),
                ["minute"] = Integer(Json.Get(c, "minute", 0), "Minute", 0, 59),
                ["items_per_source"] = Integer(Json.Get(c, "items_per_source"), "Articles per source", 1, 50),
                ["max_reviews"] = Integer(Json.Get(c, "max_reviews"), "Reviews per run", 1, 500),
                ["lookback_days"] = Integer(Json.Get(c, "lookback_days"), "Lookback days", 1, 30),
            };
        }
        if (input.ContainsKey("interests"))
        {
            if (input["interests"] is not JsonObject i || Json.Get(i, "topics") is not JsonArray topics || topics.Count > 100)
                throw new ValidationException("Use at most 100 interests");
            var validTopics = new JsonArray();
            foreach (var topic in topics)
            {
                if (topic is not JsonObject item || !Json.IsString(Json.Get(item, "preference"), out var preference) || preference is not ("favor" or "normal" or "less" or "exclude"))
                    throw new ValidationException("Invalid interest preference");
                validTopics.Add(new JsonObject { ["name"] = Text(Json.Get(item, "name"), "Interest", 120), ["preference"] = preference });
            }
            if (Json.Get(i, "exclusions") is not JsonArray exclusions || exclusions.Count > 100) throw new ValidationException("Use at most 100 exclusions");
            var interests = new JsonObject
            {
                ["guidance"] = Text(Json.Get(i, "guidance"), "Editorial guidance", 6000, empty: true),
                ["topics"] = validTopics,
                ["exclusions"] = new JsonArray(exclusions.Select(e => (JsonNode)Text(e, "Exclusion", 200)).ToArray()),
            };
            if (i.ContainsKey("feedback")) interests["feedback"] = Feedback.Validate(i["feedback"]?.DeepClone());
            result["interests"] = interests;
        }
        var selected = new JsonObject();
        foreach (var key in input.Select(pair => pair.Key)) selected[key] = result[key]?.DeepClone();
        return selected;
    }
}
