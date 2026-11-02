using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NewsReview.Tests;

public class ParsingTests
{
    [Fact]
    public void RssNamespacesDatesTrackingAndHtml()
    {
        var items = Feeds.ParseFeed("""
            <rss xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel><item>
            <title>New &amp; useful</title><link>https://example.com/article?utm_source=rss&amp;x=1#top</link>
            <pubDate>Thu, 15 Oct 2026 09:00:00 GMT</pubDate>
            <content:encoded><![CDATA[<p>Article <b>details</b>.</p><script>bad()</script>]]></content:encoded>
            </item></channel></rss>
            """, "https://example.com/feed");
        Assert.Equal("https://example.com/article?x=1", items[0].Url);
        Assert.Equal("Article details.", items[0].Excerpt);
        Assert.Equal("2026-10-15T09:00:00+00:00", items[0].PublishedAt);
    }

    [Fact]
    public void AtomRelativeLinksAndSummary()
    {
        var items = Feeds.ParseFeed("""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>Protocol</title>
            <link rel="self" href="/entry.atom"/><link href="/article"/><summary>Details</summary>
            <published>2026-10-15T11:00:00+02:00</published></entry></feed>
            """, "https://example.com/feed");
        Assert.Equal("https://example.com/article", items[0].Url);
        Assert.Equal("2026-10-15T09:00:00+00:00", items[0].PublishedAt);
    }

    [Theory]
    [InlineData("<html><p>Blocked</p></html>")]
    [InlineData("<!DOCTYPE rss><rss/>")]
    [InlineData("<rss>")]
    public void RejectsNonFeedAndDtd(string content) =>
        Assert.Throws<ValidationException>(() => Feeds.ParseFeed(content, "https://example.com"));

    [Fact]
    public void ArticleExtractionAndExcerptFallback()
    {
        var body = string.Concat(Enumerable.Repeat("The proposed image format offers experimental compression features. ", 8));
        var html = $"<nav><p>{string.Concat(Enumerable.Repeat("Menu ", 100))}</p></nav><article><p>{body}</p><aside><p>{string.Concat(Enumerable.Repeat("Ads ", 100))}</p></aside></article>";
        Assert.Equal(body.Trim(), Feeds.ExtractArticle(html));
        Assert.Equal("", Feeds.ExtractArticle("<p>Please subscribe to read more.</p>"));
        var jsonLd = new JsonObject { ["@graph"] = new JsonArray(new JsonObject { ["articleBody"] = body }) };
        Assert.Equal(body.Trim(), Feeds.ExtractArticle($"<script type=\"application/ld+json\">{jsonLd.ToJsonString()}</script>"));
    }

    [Fact]
    public void PlainAndMissingDates()
    {
        Assert.Equal("Hello world & radio", Feeds.Plain("<p>Hello</p><p>world &amp; radio</p>"));
        Assert.Null(Feeds.ParseDate("not a date"));
        Assert.Null(Feeds.ParseDate(null));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://user:password@example.com")]
    [InlineData("https://example.com:bad")]
    [InlineData("https://example.com/\nfoo")]
    public void UrlValidationRejects(string url) => Assert.Throws<ValidationException>(() => UrlTools.Valid(url));

    [Fact]
    public async Task PrivateAddressesAndCanonicalUrls()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Fetcher.CheckPublic("http://127.0.0.1"));
        Assert.Equal("https://example.com/?q=test", UrlTools.Canonical("https://example.com/?q=test&utm_medium=rss"));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("192.0.0.9", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void GlobalAddressRules(string address, bool global) =>
        Assert.Equal(global, Fetcher.IsGlobal(System.Net.IPAddress.Parse(address)));
}

public class ReviewTests
{
    [Fact]
    public void ValidOutputAndCandidateReferences()
    {
        Assert.Equal("reviewed", (string)Reviewer.ValidateReview(Fixtures.Review(), new HashSet<long>())["status"]!);
        Assert.Equal("filtered", (string)Reviewer.ValidateReview(Fixtures.Review(r => r["relevant"] = false), new HashSet<long>())["status"]!);
        Assert.Equal("duplicate", (string)Reviewer.ValidateReview(Fixtures.Review(r => r["duplicate_of"] = 8), new HashSet<long> { 8 })["status"]!);
    }

    public static TheoryData<string> InvalidChanges => ["score=true", "score=101", "relevant=\"yes\"", "summary=\"\"",
        "milestone=\"released probably\"", "duplicate_of=99", "extra=\"unexpected\"", "tags=[42]", "score=80.0", "why_it_matters=\"Obsolete explanation.\""];

    [Theory]
    [MemberData(nameof(InvalidChanges))]
    public void InvalidModelOutputsAreNotPersisted(string change)
    {
        var (key, value) = (change[..change.IndexOf('=')], change[(change.IndexOf('=') + 1)..]);
        var review = Fixtures.Review(r => r[key] = JsonNode.Parse(value));
        Assert.Throws<ValidationException>(() => Reviewer.ValidateReview(review, new HashSet<long> { 8 }));
    }

    [Fact]
    public void ReviewsHaveOnlySummaryProse() =>
        Assert.False(Reviewer.ValidateReview(Fixtures.Review(), new HashSet<long>()).ContainsKey("why_it_matters"));

    [Fact]
    public void CandidatesRequireSharedTerms()
    {
        var recent = new List<JsonObject>
        {
            new() { ["id"] = 1, ["headline"] = "Browser adds experimental image format", ["summary"] = "A" },
            new() { ["id"] = 2, ["headline"] = "Phone pricing changes", ["summary"] = "B" },
        };
        Assert.Equal([1L], Reviewer.CandidatesFor("New experimental browser format arrives", recent).Select(i => i["id"]!.AsLong()));
    }
}

public class StoreTests : IDisposable
{
    readonly TempDirectory directory = new();
    readonly Store store;
    readonly long source;

    public StoreTests()
    {
        store = new Store(directory.File("news.db"));
        source = Fixtures.AddSource(store)["id"]!.AsLong();
    }

    public void Dispose() => directory.Dispose();

    bool Insert(string headline = "New HTTP method", string url = "https://example.com/story", string day = "2026-10-15") =>
        store.InsertStory(source, Fixtures.Entry(headline, url, "2026-10-14T08:00:00+00:00", "Useful details"), "Protocols", day);

    JsonObject Stories(Store.StoryQuery? query = null) => store.Stories(query ?? new Store.StoryQuery());
    long Total(Store.StoryQuery query) => Stories(query)["total"]!.AsLong();

    [Fact]
    public void PublicationCalendarDatesAndUndatedFallback()
    {
        store.InsertStory(source, Fixtures.Entry("Midnight boundary", "https://example.com/boundary", "2026-10-17T01:30:00+00:00", ""), "Protocols", "2026-10-18");
        Assert.Equal(1, Total(new() { Day = "2026-10-16", TimeZone = "America/New_York" }));
        Assert.Equal(1, Total(new() { Day = "2026-10-17", TimeZone = "UTC" }));
        Assert.Equal(0, Total(new() { Day = "2026-10-18", TimeZone = "UTC" }));
        Assert.Equal(1, Total(new() { Start = "2026-10-16", End = "2026-10-16", TimeZone = "America/New_York" }));
        store.InsertStory(source, Fixtures.Entry("Undated", "https://example.com/undated", null, ""), "Protocols", "2020-01-01");
        store.Write(db =>
        {
            db.Execute("UPDATE stories SET discovered_at='2026-10-18T10:00:00+00:00' WHERE published_at IS NULL");
            Store.BackfillStoryTimes(db);
        });
        Assert.Equal(["Undated"], Stories(new() { Day = "2026-10-18", TimeZone = "UTC" })["items"]!.AsArray().Select(s => (string)s!["headline"]!));
        Assert.Equal("Undated", (string)Stories(new() { TimeZone = "UTC" })["items"]![0]!["headline"]!);
    }

    [Fact]
    public void NoBundledSourcesAndPersistentFirstRunDefaults()
    {
        var fresh = new Store(directory.File("fresh.db"));
        Assert.Empty(fresh.Sources());
        Assert.Empty(fresh.Topics());
        Assert.Empty(fresh.Settings()["interests"]!["topics"]!.AsArray());
        var ai = (JsonObject)store.Settings()["ai"]!;
        ai["model"] = "local-test";
        store.SaveSettings(new JsonObject { ["ai"] = ai.DeepClone() });
        var saved = (JsonObject)store.Sources()[0]!;
        saved["enabled"] = false;
        store.SaveSource(saved, saved["id"]!.AsLong());
        var reopened = new Store(store.Path);
        Assert.Equal("local-test", (string)reopened.Settings()["ai"]!["model"]!);
        Assert.Equal(0, reopened.Sources()[0]!["enabled"]!.AsLong());
        Assert.Single(reopened.Sources());
    }

    [Fact]
    public void RelevanceFiltersMatchTiersAndCombineWithVisibility()
    {
        foreach (var score in new[] { 0, 39, 40, 74, 75, 100 })
        {
            Insert($"Score {score}", $"https://example.com/score/{score}");
            Fixtures.Sql(store, "UPDATE stories SET status='reviewed',score=? WHERE headline=?", score, $"Score {score}");
        }
        Insert("Pending", "https://example.com/pending");
        Assert.Equal(5, Total(new() { ExcludedRelevance = ["high"] }));
        Assert.Equal(5, Total(new() { ExcludedRelevance = ["medium"] }));
        Assert.Equal(5, Total(new() { ExcludedRelevance = ["low"] }));
        Assert.Equal(6, Total(new() { ExcludedRelevance = ["pending"] }));
        Assert.Equal(0, Total(new() { ExcludedRelevance = ["high", "medium", "low", "pending"] }));
        Fixtures.Sql(store, "UPDATE stories SET bookmarked=1 WHERE score=0");
        Fixtures.Sql(store, "UPDATE stories SET status='duplicate' WHERE score=100");
        Assert.Equal(0, Total(new() { Bookmarks = true, ExcludedRelevance = ["low"] }));
        Assert.Equal(7, Total(new() { ShowHidden = true }));
        Assert.Equal(5, Total(new() { ShowHidden = true, ExcludedRelevance = ["high"] }));
        Assert.Equal(2, Total(new() { Query = "Score", ExcludedRelevance = ["high", "low"] }));
        Assert.Throws<ValidationException>(() => Stories(new() { ExcludedRelevance = ["unknown"] }));
    }

    [Fact]
    public void UniqueUrlHistorySearchBookmarkAndReassessment()
    {
        Assert.True(Insert());
        Assert.False(Insert());
        var story = Stories(new() { Day = "2026-10-14", TimeZone = "UTC" })["items"]![0]!;
        Assert.Equal(0, Total(new() { Day = "2026-10-15", TimeZone = "UTC" }));
        Assert.StartsWith("2026-10-14", (string)story["published_at"]!);
        var id = story["id"]!.AsLong();
        store.Bookmark(id, true);
        var result = Reviewer.ValidateReview(Fixtures.Review(), new HashSet<long>());
        var defaults = Defaults.Settings();
        store.ReviewResult(id, result, (JsonObject)defaults["ai"]!, (JsonObject)defaults["interests"]!, "article", "article");
        Assert.Equal(1, Total(new() { Query = "experimental" }));
        Assert.Equal(0, Total(new() { Query = "%" }));
        Assert.Equal(1, Total(new() { Tags = ["HTTP"] }));
        Assert.Equal(0, Total(new() { Start = "2026-10-16" }));
        store.Requeue(false);
        var saved = Stories(new() { Bookmarks = true })["items"]![0]!;
        Assert.True(saved["bookmarked"]!.GetValue<bool>());
        Assert.Equal((string)result["summary"]!, (string)saved["summary"]!);
        Assert.Equal("2026-10-15", (string)saved["day"]!);
        Assert.Equal("pending", (string)saved["status"]!);
    }

    [Fact]
    public void DuplicateCoverageHiddenButBookmarksRetained()
    {
        Insert();
        Insert("HTTP method arrives", "https://example.com/other");
        var items = Stories()["items"]!.AsArray();
        var (original, duplicate) = (items[0]!["id"]!.AsLong(), items[1]!["id"]!.AsLong());
        var defaults = Defaults.Settings();
        store.ReviewResult(duplicate, Reviewer.ValidateReview(Fixtures.Review(r => r["duplicate_of"] = original), new HashSet<long> { original }),
            (JsonObject)defaults["ai"]!, (JsonObject)defaults["interests"]!, "text", "excerpt");
        Assert.Equal(1, Total(new()));
        Assert.Single(store.Related(original));
        store.Bookmark(duplicate, true);
        Assert.Equal(1, Total(new() { Bookmarks = true }));
    }

    [Fact]
    public void QueueAttemptLimitAndReset()
    {
        Insert();
        var story = store.Pending(1)[0];
        for (var i = 0; i < 3; i++) store.ReviewFailed(story["id"]!.AsLong(), "offline", "excerpt", "excerpt");
        Assert.Empty(store.Pending(10));
        Assert.Equal(1, store.Requeue(true));
        Assert.Single(store.Pending(10));
    }

    [Fact]
    public void CannotOpenWalIsActionableDuringConnectionSetupAndStartup()
    {
        // A real filesystem obstruction: the connection opens, then SQLite cannot create its WAL file.
        Insert();
        var wal = store.Path + "-wal";
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(wal)) File.Delete(wal);
        Directory.CreateDirectory(wal);
        try
        {
            var error = Assert.Throws<StorageUnavailableException>(() => store.Settings());
            Assert.Contains("--data-dir", error.Message);
            Assert.Contains(store.Path, error.Message);
            var startup = Path.Combine(directory.Path, "startup");
            var obstructed = new Store(Path.Combine(startup, "newsreview.db"));
            Directory.CreateDirectory(obstructed.Path + "-wal");
            var app = typeof(Program).Assembly.Location;
            using var process = Process.Start(new ProcessStartInfo("dotnet", [app, "--data-dir", startup, "--no-scheduler", "--urls", "http://127.0.0.1:0"])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.Path,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(20000));
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("SQLite can create/open its -wal and -shm", output);
            Assert.DoesNotContain("Unhandled exception", output);
        }
        finally
        {
            Directory.Delete(wal);
        }
        Assert.Equal(1, Total(new()));
    }

    [Fact]
    public void ThreeHourIntervalRespectsElapsedTimeAndRestart()
    {
        var config = (JsonObject)store.Settings()["collection"]!;
        config["interval_hours"] = 3;
        store.SaveSettings(new JsonObject { ["collection"] = config.DeepClone() });
        var now = new DateTimeOffset(2026, 10, 15, 23, 0, 0, TimeSpan.Zero);
        var runId = store.StartRun("collect", new JsonObject());
        Fixtures.Sql(store, "UPDATE runs SET started_at=? WHERE id=?", PyText.FormatSeconds(now), runId);
        store.FinishRun(runId, "failed", new JsonObject());
        var collector = Fixtures.Collector(new Store(store.Path));
        Assert.False(collector.ScheduleDue(now + TimeSpan.FromMinutes(179)));
        Assert.True(collector.ScheduleDue(now + TimeSpan.FromHours(3)));
        Assert.True(collector.ScheduleDue(now + TimeSpan.FromDays(3)));
        foreach (var value in new[] { "0", "-1", "1.5", "8761", "true" })
        {
            var invalid = (JsonObject)config.DeepClone();
            invalid["interval_hours"] = JsonNode.Parse(value);
            Assert.Throws<ValidationException>(() => Validation.Settings(new JsonObject { ["collection"] = invalid }, Defaults.Settings()));
        }
        Assert.Equal(3, Validation.Settings(new JsonObject { ["collection"] = config.DeepClone() }, Defaults.Settings())["collection"]!["interval_hours"]!.AsLong());
        var legacy = (JsonObject)config.DeepClone();
        legacy.Remove("interval_hours");
        Assert.Equal(24, Validation.Settings(new JsonObject { ["collection"] = legacy }, Defaults.Settings())["collection"]!["interval_hours"]!.AsLong());
    }

    [Fact]
    public void IntervalSchedulePersistsAndHandlesInterruption()
    {
        var collector = Fixtures.Collector(store);
        var now = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        Assert.True(collector.ScheduleDue(now.AddHours(-2)));
        Assert.True(collector.ScheduleDue(now));
        var runId = store.StartRun("review", new JsonObject());
        store.FinishRun(runId, "complete", new JsonObject());
        Assert.True(collector.ScheduleDue(now)); // Review-only runs do not satisfy the collection schedule.
        runId = store.StartRun("collect", new JsonObject());
        Fixtures.Sql(store, "UPDATE runs SET started_at=? WHERE id=?", PyText.FormatSeconds(now), runId);
        store.FinishRun(runId, "partial", new JsonObject());
        Assert.False(collector.ScheduleDue(now)); // An attempted run must not repeat every 20 seconds.
        Assert.True(collector.ScheduleDue(now.AddDays(1)));
        store.FinishRun(runId, "interrupted", new JsonObject());
        Assert.True(collector.ScheduleDue(now));
        var config = (JsonObject)store.Settings()["collection"]!;
        config["enabled"] = false;
        store.SaveSettings(new JsonObject { ["collection"] = config.DeepClone() });
        Assert.False(collector.ScheduleDue(now));
    }
}

public class ValidationTests
{
    [Fact]
    public void PreferencesAndLocalEndpoint()
    {
        var defaults = Defaults.Settings();
        var ai = (JsonObject)defaults["ai"]!.DeepClone();
        ai["base_url"] = "http://192.168.1.42:11434";
        ai["model"] = "test";
        Assert.Equal("test", (string)Validation.Settings(new JsonObject { ["ai"] = ai }, defaults)["ai"]!["model"]!);
        var interests = (JsonObject)defaults["interests"]!.DeepClone();
        interests["topics"]!.AsArray().Add(new JsonObject { ["name"] = "Example", ["preference"] = "exclude" });
        Assert.Equal("exclude", (string)Validation.Settings(new JsonObject { ["interests"] = interests }, defaults)["interests"]!["topics"]![0]!["preference"]!);
        var collection = (JsonObject)defaults["collection"]!.DeepClone();
        collection["max_reviews"] = -1;
        Assert.Throws<ValidationException>(() => Validation.Settings(new JsonObject { ["collection"] = collection }, defaults));
        Assert.Throws<ValidationException>(() => Validation.Source(new JsonObject { ["name"] = "Bad", ["url"] = "file:///etc/passwd", ["category"] = "Security" }));
    }
}
