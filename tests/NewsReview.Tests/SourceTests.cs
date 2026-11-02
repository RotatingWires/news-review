using System.Text.Json.Nodes;

namespace NewsReview.Tests;

public class ListingTests
{
    [Fact]
    public void CardExcerptsDatesHeadingLinksAndExclusions()
    {
        const string html = """
            <nav><h2><a href="/nav">A navigation link is not news</a></h2></nav><main>
            <article><header><h2><a href="/news/one?utm_source=home">A useful new protocol ships</a></h2></header>
            <p>An article excerpt.</p><time datetime="2026-10-15"></time><a href="/2026/10/15/">October 15, 2026</a>
            <p><a href="/news/old">A previously mentioned project</a></p></article>
            <h3><a href="/news/two">A useful radio project ships</a></h3>
            <h2><a href="https://evil.example/story">An external website link</a></h2>
            <h2><a href="/category/radio">A radio category archive</a></h2></main>
            """;
        var items = Listings.Parse(html, "https://example.com/");
        Assert.Equal(["https://example.com/news/one"], items.Select(s => s.Url));
        Assert.Single(Listings.Parse("<h3><a href=\"/news/two\">A useful radio project ships</a></h3>", "https://example.com/"));
        Assert.Equal("An article excerpt. A previously mentioned project", items[0].Excerpt);
        Assert.Equal("2026-10-15T00:00:00+00:00", items[0].PublishedAt);
    }

    [Fact]
    public void ExplicitUrlFilterAndStructuredLists()
    {
        Assert.Single(Listings.Parse("<div><a href=\"/posts/one\">A new release is available</a><a href=\"/other\">An unrelated page is here</a></div>",
            "https://example.com/", "/posts/"));
        var data = new JsonObject
        {
            ["@type"] = "ItemList",
            ["itemListElement"] = new JsonArray(new JsonObject
            {
                ["@type"] = "ListItem",
                ["item"] = new JsonObject
                {
                    ["@type"] = "NewsArticle", ["url"] = "/science/new", ["headline"] = "New scientific research released",
                    ["datePublished"] = "2026-10-15", ["description"] = "Research details.",
                },
            }),
        };
        var items = Listings.Parse($"<script type=\"application/ld+json\">{data.ToJsonString()}</script>", "https://example.com/");
        Assert.Single(items);
        Assert.StartsWith("2026-10-15", items[0].PublishedAt);
        foreach (var html in new[] { "<html>Enable JavaScript to view stories</html>", "<h2><a href=\"javascript:bad()\">This must never be followed</a></h2>" })
            Assert.Throws<ValidationException>(() => Listings.Parse(html, "https://example.com/"));
    }
}

public class SourceTests : IDisposable
{
    readonly TempDirectory directory = new();
    readonly Store store;
    readonly long sourceId;

    public SourceTests()
    {
        store = new Store(directory.File("newsreview.db"));
        sourceId = Fixtures.AddSource(store)["id"]!.AsLong();
    }

    public void Dispose() => directory.Dispose();

    JsonObject Configured() => store.Sources().Cast<JsonObject>().First(s => s["id"]!.AsLong() == sourceId);

    [Fact]
    public void HealthDistinguishesObservedInactivityFailuresAndStaleChecks()
    {
        store.SourceChecked(sourceId, count: 5, newest: "2026-09-01T00:00:00+00:00");
        var config = Configured();
        config["last_checked"] = "2026-10-15T00:00:00+00:00";
        config["first_checked"] = "2026-10-01T00:00:00+00:00";
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var health = Health.For(config, now);
        Assert.Equal("warning", (string)health["status"]!);
        Assert.Contains(health["warnings"]!.AsArray(), w => ((string)w!).Contains("may be inactive"));
        config["stale_days"] = 90;
        Assert.Equal("healthy", (string)Health.For(config, now)["status"]!);
        config["newest_published"] = null;
        config["last_checked"] = "2026-06-01T00:00:00+00:00";
        Assert.Contains(Health.For(config, now)["warnings"]!.AsArray(), w => ((string)w!).Contains("health is stale"));
        for (var i = 0; i < 3; i++) store.SourceChecked(sourceId, "HTTP 403");
        Assert.Equal("broken", (string)Configured()["health"]!["status"]!);
        store.SourceChecked(sourceId, count: 0);
        config = Configured();
        Assert.Equal(0, config["consecutive_failures"]!.AsLong());
        Assert.Contains(config["health"]!["warnings"]!.AsArray(), w => ((string)w!).Contains("no story entries"));
    }

    [Fact]
    public void ExistingSourcesAndLegacyMarkersSurviveRestartWithoutReseeding()
    {
        Fixtures.Sql(store, "INSERT INTO settings VALUES ('sources_seeded','true'),('source_additions_048','true')");
        Fixtures.Sql(store, "UPDATE sources SET removed=1,enabled=0 WHERE id=?", sourceId);
        var kept = store.SaveSource(new JsonObject
        {
            ["name"] = "Kept source", ["url"] = "https://example.org/", ["feed_url"] = "", ["category"] = "Custom", ["enabled"] = false,
        });
        var preferences = store.Settings();
        var reopened = new Store(store.Path);
        var sources = reopened.Sources().Cast<JsonObject>().Select(s => (s["id"]!.AsLong(), (string)s["name"]!, s["enabled"]!.AsLong())).ToList();
        Assert.Equal([(kept, "Kept source", 0L)], sources);
        Assert.True(JsonNode.DeepEquals(preferences, reopened.Settings()));
    }

    [Fact]
    public void SourceModesAndIntervalsAreValidated()
    {
        JsonObject Config() => new() { ["name"] = "Website", ["url"] = "https://example.com/", ["category"] = "News", ["mode"] = "page", ["stale_days"] = 30 };
        Assert.Equal("page", (string)Validation.Source(Config())["mode"]!);
        foreach (var (key, value) in new (string, JsonNode)[] { ("mode", "unknown"), ("stale_days", 0), ("link_pattern", 42) })
        {
            var config = Config();
            config[key] = value;
            Assert.Throws<ValidationException>(() => Validation.Source(config));
        }
    }
}
