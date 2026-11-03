using System.Text.Json.Nodes;

namespace NewsReview.Tests;

/// <summary>
/// A local news site plus Ollama/compatible/OpenRouter model APIs. Special model names select
/// failure modes, so tests exercise the real transports and validation without a real model.
/// </summary>
sealed class NewsFixture : IDisposable
{
    readonly FixtureServer server = new();
    public string Root => server.Root;
    public List<(string Path, JsonNode? Payload)> ModelRequests { get; } = [];
    public List<string> Authorizations { get; } = [];
    public ManualResetEventSlim ReviewEntered { get; } = new();
    public ManualResetEventSlim ReviewRelease { get; } = new();

    public NewsFixture() => server.Handler = (path, body, request, _) => request.HttpMethod == "POST" ? Post(path, body, request) : Get(path);

    public void Dispose() => server.Dispose();

    static (int, string, string) Html(string content, int status = 200) => (status, content, "text/html");

    (int, string, string) Get(string path)
    {
        var root = Root;
        var today = PyText.Format(DateTimeOffset.UtcNow);
        switch (path)
        {
            case "/":
                return Html("""<html><head><link rel="alternate" type="application/rss+xml" href="/feed"/></head></html>""");
            case "/feed":
                return (200, $"""
                    <rss><channel><item><title>HTTP browser experimental format</title><link>{root}/article</link>
                    <description>Source excerpt with enough useful details.</description><pubDate>{today}</pubDate></item>
                    <item><title>Another useful radio project</title><link>{root}/excerpt</link>
                    <description>Experimental radio project supports new decoder features.</description></item>
                    <item><title>Old news</title><link>{root}/old</link><pubDate>2020-01-01T00:00:00Z</pubDate></item>
                    </channel></rss>
                    """, "application/rss+xml");
            case "/stale-feed" or "/undated-feed" or "/empty-feed":
                var items = path == "/empty-feed" ? "" :
                    $"""<item><title>A useful original feed story</title><link>{root}/article</link><description>Feed-only excerpt.</description>{(path != "/undated-feed" ? "<pubDate>2020-01-01T00:00:00Z</pubDate>" : "")}</item>""" +
                    $"""<item><title>A second original feed story</title><link>{root}/old</link><pubDate>2020-01-01T00:00:00Z</pubDate></item>""";
                if (path == "/undated-feed") items = items.Replace("<pubDate>2020-01-01T00:00:00Z</pubDate>", "");
                return (200, $"<rss><channel>{items}</channel></rss>", "application/rss+xml");
            case "/article":
                return Html("<article><p>" + string.Concat(Enumerable.Repeat("A browser added experimental support for a new HTTP feature in a development build. ", 8)) + "</p></article>");
            case "/listing":
                return Html($"""
                    <html><nav><a href="/unwanted">Navigation should never be a story</a></nav><main>
                    <article><header><h2><a href="/article">New radio protocol reaches experimental testing</a></h2></header>
                    <time datetime="{today}"></time><p>A useful source excerpt from the article card.</p></article>
                    <article><h2><a href="/excerpt">Another practical radio project is available</a></h2></article></main></html>
                    """);
            case "/excerpt":
                return Html("Subscription required", 403);
            case "/blank":
                return Html("<html><body><nav><a href=\"/\">Home</a></nav></body></html>");
            default:
                return Html("Not found", 404);
        }
    }

    (int, string, string) Post(string path, JsonNode? body, System.Net.HttpListenerRequest request)
    {
        var payload = body as JsonObject ?? new JsonObject();
        lock (ModelRequests)
        {
            ModelRequests.Add((path, payload.DeepClone()));
            Authorizations.Add(request.Headers["Authorization"] ?? "");
        }
        var model = (string?)(payload["model"] as JsonValue) ?? "";
        (int, string, string) Json(JsonNode value, int status = 200) => (status, value.ToJsonString(), "application/json");
        if (model == "key-error") return Json(new JsonObject { ["error"] = "Rejected " + request.Headers["Authorization"] }, 400);
        if (model == "openrouter-fixture" && (!JsonNode.DeepEquals(payload["reasoning"], new JsonObject { ["enabled"] = false }) || payload.ContainsKey("temperature")))
            return Json(new JsonObject { ["error"] = "OpenRouter fixture requires thinking disabled and temperature omitted" }, 400);
        if (model == "blocking-review" && path is "/api/chat" or "/v1/chat/completions")
        {
            ReviewEntered.Set();
            ReviewRelease.Wait(TimeSpan.FromSeconds(8));
        }
        if (path == "/api/generate" && payload["keep_alive"] is JsonValue keep && keep.ToJsonString() == "0")
            return model == "unload-error" ? (503, "Unload failed", "text/plain") : Json(new JsonObject { ["done"] = true, ["done_reason"] = "unload" });
        if (model.StartsWith("http-error", StringComparison.Ordinal))
        {
            const string message = "Input exceeds the model context window. Reduce input length.";
            var error = path == "/v1/chat/completions" ? new JsonObject { ["error"] = new JsonObject { ["message"] = message } } : new JsonObject { ["error"] = message };
            var text = model switch
            {
                "http-error-message" => new JsonObject { ["message"] = "Unsupported parameter:\n\tresponse_format" }.ToJsonString(),
                "http-error-html" => "<html><body>Private proxy error page</body></html>",
                "http-error-huge" => new JsonObject { ["error"] = new string('x', 10000) }.ToJsonString(),
                "http-error-shape" => new JsonObject { ["error"] = new JsonArray("Unexpected error shape") }.ToJsonString(),
                "http-error-long" => new JsonObject { ["error"] = new string('x', 1000) }.ToJsonString(),
                _ => error.ToJsonString(),
            };
            return (400, text, "application/json");
        }
        var messages = payload["messages"] as JsonArray;
        var schema = payload["format"] as JsonObject
            ?? JsonNode.Parse((string?)messages?[1]?["content"] ?? "{}")?["schema"] as JsonObject ?? new JsonObject();
        var tagSchema = schema["properties"]?["tags"];
        var allowedTags = (tagSchema?["items"]?["enum"] as JsonArray)?.Select(t => (string)t!).ToList() ?? ["HTTP", "Standards"];
        if (tagSchema?["maxItems"] is JsonValue maxItems && maxItems.ToJsonString() == "0") allowedTags = [];
        JsonObject Review(IEnumerable<string> tags) => Fixtures.Review(r => r["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()));
        if (model is "truncated" or "empty-thinking" or "fenced")
        {
            var content = model == "truncated" ? "{\"summary\":" : model == "empty-thinking" ? "" : "```json\n" + Review(allowedTags.Take(1)).ToJsonString() + "\n```";
            return Json(new JsonObject
            {
                ["message"] = new JsonObject { ["content"] = content, ["thinking"] = model == "empty-thinking" ? "fixture reasoning" : "" },
                ["done_reason"] = model == "truncated" ? "length" : "stop",
            });
        }
        if (model == "broken") return Json(new JsonObject { ["message"] = new JsonObject { ["content"] = "not JSON" } });
        var learning = ((string?)messages?[0]?["content"] ?? "").StartsWith("Summarize article ratings", StringComparison.Ordinal);
        JsonNode result = learning
            ? new JsonObject { ["profile"] = "Favor practical SDR projects. Avoid shopping lists, while retaining interest in radio technology." }
            : Review(allowedTags.Take(1));
        if (model == "invented-topic") result = Review(["Invented tag that must not be saved"]);
        if (model == "bad-profile" && learning) result = new JsonObject { ["profile"] = 42 };
        return path switch
        {
            "/api/chat" => Json(new JsonObject { ["message"] = new JsonObject { ["content"] = result.ToJsonString() }, ["prompt_eval_count"] = 100, ["eval_count"] = 30 }),
            "/v1/chat/completions" => Json(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = result.ToJsonString() } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 90, ["completion_tokens"] = 25 },
            }),
            _ => (404, "Not found", "text/plain"),
        };
    }
}
