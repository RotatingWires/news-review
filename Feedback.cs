using System.Text.Json.Nodes;

namespace NewsReview;

/// <summary>Bounded feedback data and the compact editorial context sent to models.</summary>
static class Feedback
{
    static readonly string[] Keys = ["enabled", "auto_update", "profile", "revision", "profile_revision", "ratings"];

    public static JsonObject Default() => new()
    {
        ["enabled"] = true, ["auto_update"] = true, ["profile"] = "", ["revision"] = 0, ["profile_revision"] = 0, ["ratings"] = new JsonObject(),
    };

    public static JsonObject State(JsonObject interests) =>
        interests.TryGetPropertyValue("feedback", out var value) && value is JsonObject feedback ? (JsonObject)feedback.DeepClone() : Default();

    public static JsonObject Validate(JsonNode? node)
    {
        if (node is not JsonObject value || value.Count != Keys.Length || Keys.Any(key => !value.ContainsKey(key)))
            throw new ValidationException("Invalid feedback preferences");
        if (!Json.IsBool(value["enabled"], out _) || !Json.IsBool(value["auto_update"], out _))
            throw new ValidationException("Invalid feedback flags");
        if (!Json.IsString(value["profile"], out var profile) || PyText.Length(profile) > 2400)
            throw new ValidationException("Learned preferences must be at most 2400 characters");
        long revision = 0, profileRevision = 0;
        foreach (var key in new[] { "revision", "profile_revision" })
        {
            if (!Json.IsInteger(value[key], out var number) || number < 0 || number > 1_000_000_000)
                throw new ValidationException("Invalid feedback revision");
            if (key == "revision") revision = number; else profileRevision = number;
        }
        if (profileRevision > revision) throw new ValidationException("Invalid feedback profile revision");
        if (value["ratings"] is not JsonObject ratings || ratings.Count > 10000)
            throw new ValidationException("Use at most 10000 saved article ratings");
        foreach (var (url, node2) in ratings)
        {
            if (PyText.Length(url) is < 1 or > 2048 || node2 is not JsonObject rating) throw new ValidationException("Invalid article feedback");
            if (!Json.IsInteger(Json.Get(rating, "vote"), out var vote) || vote is not (-1 or 1)) throw new ValidationException("Choose thumbs up or down");
            foreach (var (key, limit) in new[] { ("reason", 320), ("headline", 500), ("source", 100), ("summary", 1200) })
                if (!Json.IsString(Json.Get(rating, key), out var text) || PyText.Length(text) > limit)
                    throw new ValidationException("Invalid feedback " + key);
            if (Json.Get(rating, "tags") is not JsonArray tags || tags.Count > 100 ||
                tags.Any(tag => !Json.IsString(tag, out var name) || PyText.Length(name) > 60))
                throw new ValidationException("Invalid feedback tags");
            if (!Json.IsInteger(Json.Get(rating, "sequence"), out var sequence) || sequence <= 0 || sequence > revision)
                throw new ValidationException("Invalid feedback sequence");
            if (!Json.IsString(Json.Get(rating, "updated"), out var updated) || PyText.ParseAware(updated) is null)
                throw new ValidationException("Invalid feedback date");
        }
        return value;
    }

    /// <summary>Interests as sent to the model; explicit interests always accompany any learned profile.</summary>
    public static JsonObject ForModel(JsonObject interests)
    {
        var result = new JsonObject
        {
            ["guidance"] = Json.Get(interests, "guidance", "")?.DeepClone(),
            ["topics"] = Json.Get(interests, "topics", new JsonArray())?.DeepClone(),
            ["exclusions"] = Json.Get(interests, "exclusions", new JsonArray())?.DeepClone(),
        };
        var profile = "";
        if (interests["feedback"] is JsonObject value)
        {
            if (value["enabled"]!.GetValue<bool>()) profile = (string)value["profile"]!;
        }
        else profile = (string?)interests["learned_profile"] ?? "";
        if (profile.Length > 0) result["learned_profile"] = profile;
        return result;
    }

    /// <summary>The ten most recent ratings, trimmed, for profile generation.</summary>
    public static JsonArray Examples(JsonObject feedback)
    {
        var recent = ((JsonObject)feedback["ratings"]!).Select(pair => (JsonObject)pair.Value!)
            .OrderByDescending(item => item["sequence"]!.AsLong()).Take(10);
        return new JsonArray(recent.Select(item => (JsonNode)new JsonObject
        {
            ["vote"] = item["vote"]!.AsLong(),
            ["reason"] = PyText.Truncate((string)item["reason"]!, 320),
            ["headline"] = PyText.Truncate((string)item["headline"]!, 180),
            ["source"] = PyText.Truncate((string)item["source"]!, 80),
            ["tags"] = new JsonArray(((JsonArray)item["tags"]!).Take(4).Select(tag => tag?.DeepClone()).ToArray()),
            ["summary"] = PyText.Truncate((string)item["summary"]!, 200),
        }).ToArray());
    }
}
