using System.Text.Json.Nodes;

namespace NewsReview;

/// <summary>First-run settings. No sources are bundled; users add their own in Settings.</summary>
static class Defaults
{
    public static readonly string[] SettingKeys = ["ai", "collection", "interests"];

    public static JsonObject Interests() => new()
    {
        ["guidance"] = "Summarize what changed factually and concisely. Favor substantive reporting over rumors, marketing, and promotional content.",
        ["topics"] = new JsonArray(),
        ["exclusions"] = new JsonArray("Deals and coupons", "Sponsored content", "Buying guides"),
    };

    public static JsonObject Settings() => new()
    {
        ["ai"] = new JsonObject
        {
            ["provider"] = "ollama", ["base_url"] = "http://127.0.0.1:11434", ["model"] = "",
            ["timeout_seconds"] = 180, ["max_input_chars"] = 12000, ["max_output_tokens"] = 2400, ["unload_after_use"] = true,
        },
        ["collection"] = new JsonObject
        {
            ["enabled"] = true, ["interval_hours"] = 24, ["hour"] = 7, ["minute"] = 0, ["items_per_source"] = 8,
            ["max_reviews"] = 40, ["lookback_days"] = 3,
        },
        ["interests"] = Interests(),
    };
}
