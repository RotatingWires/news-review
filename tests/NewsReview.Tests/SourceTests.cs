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
    public void HealthReportsAStaleFeedOnceWhenTheCollectionNoteExplainsIt()
    {
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var config = Configured();
        config["last_checked"] = "2026-10-15T00:00:00+00:00";
        config["first_checked"] = "2026-10-01T00:00:00+00:00";
        config["newest_published"] = "2026-09-01T00:00:00+00:00";
        List<string> Warnings() => Health.For(config, now)["warnings"]!.AsArray().Select(w => (string)w!).ToList();
        foreach (var reason in new[] { Sources.StaleFeedReason(14), Sources.UndatedFeedReason(14) })
        {
            var note = reason + ". Website listing also checked; combined website and feed entries.";
            config["collection_note"] = note;
            Assert.Equal([note], Warnings());
        }
        config["collection_note"] = "Using website-page collection. Feed unavailable: HTTP 404 from example.com";
        var warnings = Warnings();
        Assert.Equal(2, warnings.Count);
        Assert.Contains("may be inactive", warnings[0]);
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

public class PublisherFetchTests : IDisposable
{
    readonly FixtureServer server = new();

    public PublisherFetchTests() => server.Handler = (path, _, _, response) =>
    {
        // HTTP/2 challenges carry cf-mitigated; HTTP/1.1 ones only the Cloudflare server and challenge page.
        if (path == "/challenge") response.Headers["cf-mitigated"] = "challenge";
        if (path is "/challenge-page" or "/cloudflare-refusal") response.Headers["Server"] = "cloudflare";
        if (path is "/challenge" or "/challenge-page" or "/forbidden") return (403, "<html><title>Just a moment...</title></html>", "text/html");
        if (path == "/cloudflare-refusal") return (403, "<html><title>Access denied</title></html>", "text/html");
        var items = path switch { "/feed-4mb" => 8500, "/feed-11mb" => 23500, _ => 0 };
        var feed = string.Concat(Enumerable.Range(0, items).Select(n =>
            $"<item><title>Full-content story {n}</title><link>https://example.com/{n}</link><description>{new string('x', 400)}</description></item>"));
        return (200, $"<rss><channel>{feed}</channel></rss>", "application/rss+xml");
    };

    public void Dispose() => server.Dispose();

    [Fact]
    public async Task CloudflareChallengesAreExplainedAndPlainRefusalsAreNot()
    {
        var fetcher = new Fetcher(publicOnly: false);
        foreach (var path in new[] { "/challenge", "/challenge-page" })
        {
            var challenge = await Assert.ThrowsAsync<ValidationException>(() => fetcher.Request(server.Root + path));
            Assert.Equal("HTTP 403 from 127.0.0.1: the site requires a Cloudflare browser check that NewsReview cannot complete", challenge.Message);
        }
        foreach (var path in new[] { "/forbidden", "/cloudflare-refusal" })
            Assert.Equal("HTTP 403 from 127.0.0.1", (await Assert.ThrowsAsync<ValidationException>(() => fetcher.Request(server.Root + path))).Message);
    }

    [Fact]
    public async Task FullContentFeedsUpToTheDownloadLimitAreRead()
    {
        var fetcher = new Fetcher(publicOnly: false);
        var feed = await fetcher.Request(server.Root + "/feed-4mb");
        Assert.True(feed.Text.Length > 3_000_000);
        Assert.Equal(8500, Feeds.ParseFeed(feed.Text, feed.Url).Count);
        var error = await Assert.ThrowsAsync<ValidationException>(() => fetcher.Request(server.Root + "/feed-11mb"));
        Assert.Equal("Response exceeds the 10 MB download size limit", error.Message);
    }
}
