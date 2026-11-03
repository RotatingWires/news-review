using System.Text.Json.Nodes;

namespace NewsReview.Tests;

public class RangeTests
{
    [Fact]
    public void TaskListDateFormatsAndSeparators()
    {
        Assert.Equal(("2026-10-03", "2026-10-08"), Search.DateRange("10/3 - 10/8", 2026));
        Assert.Equal(("2026-10-03", "2026-10-08"), Search.DateRange("10/3/26 – 10/8/2026"));
        Assert.Equal(("2025-12-30", "2026-01-02"), Search.DateRange("12/30/2025—1/2/2026"));
        Assert.Equal(("", ""), Search.DateRange(""));
        foreach (var value in new[] { "2/30 - 3/1", "10/8 - 10/3", "10/3", "2026-10-03 - 2026-10-08" })
            Assert.Throws<ValidationException>(() => Search.DateRange(value, 2026));
    }

    [Fact]
    public void TaskListTimeFormatsAndOvernight()
    {
        Assert.Equal((540, 1260), Search.TimeRange("9am - 9pm"));
        Assert.Equal((1290, 125), Search.TimeRange("9:30 p.m. – 2:05 a.m."));
        Assert.Equal((0, 720), Search.TimeRange("12am - 12pm"));
        Assert.Equal((540, 0), Search.TimeRange("9 - 12"));
        Assert.Equal(((int?)null, (int?)null), Search.TimeRange(""));
        foreach (var value in new[] { "13pm - 2pm", "9:60 - 10pm", "9pm", "24:00 - 3:00" })
            Assert.Throws<ValidationException>(() => Search.TimeRange(value));
    }

    [Fact]
    public void CombinedDateTimeRanges()
    {
        Assert.Equal(("2026-09-01", "2026-10-01", 600, 900), Search.DateTimeRange("9/1/26 - 10/1/26, 10am - 3pm"));
        Assert.Equal(("2026-09-01", "2026-10-01", (int?)null, (int?)null), Search.DateTimeRange("9/1/26 - 10/1/26"));
        Assert.Equal(("", "", 600, 900), Search.DateTimeRange("10am - 3pm"));
        Assert.Throws<ValidationException>(() => Search.DateTimeRange("1/1 - 1/2, 9am - 5pm, extra"));
    }
}

public class ArchiveTests : IDisposable
{
    readonly TempDirectory directory = new();
    readonly Store store;
    readonly long source;

    public ArchiveTests()
    {
        store = new Store(directory.File("newsreview.db"));
        source = Fixtures.AddSource(store)["id"]!.AsLong();
        foreach (var name in new[] { "Radio", "Science", "Standards" }) Fixtures.ChangeTopic(store, "add", name);
    }

    public void Dispose() => directory.Dispose();

    void Insert(int number, string category, int hour)
    {
        store.InsertStory(source, Fixtures.Entry($"Story {number}", $"https://example.com/{number}"), category, "2026-10-15");
        var local = Zones.FromLocal(new DateTime(2026, 10, 15, hour, 30, 0), TimeZoneInfo.Local).ToUniversalTime();
        store.Write(db =>
        {
            db.Execute("UPDATE stories SET discovered_at=? WHERE url=?", PyText.FormatSeconds(local), $"https://example.com/{number}");
            Store.BackfillStoryTimes(db);
        });
    }

    long Total(Store.StoryQuery query) => store.Stories(query)["total"]!.AsLong();

    [Fact]
    public void MultipleTopicsMatchAnyAndCombineWithClockRange()
    {
        Insert(1, "Radio", 10);
        Insert(2, "Standards", 22);
        Insert(3, "Science", 1);
        Assert.Equal(2, Total(new() { Tags = ["Radio", "Standards"] }));
        Assert.Equal(1, Total(new() { Tags = ["Radio", "Standards"], TimeStart = 9 * 60, TimeEnd = 21 * 60 }));
        Assert.Equal(2, Total(new() { TimeStart = 21 * 60, TimeEnd = 2 * 60 }));
        Assert.Equal(0, Total(new() { Tags = ["' OR 1=1--"] }));
    }

    [Fact]
    public void ClockRangeMatchesPublicationInBrowserTimezoneAndUndatedFallback()
    {
        for (var number = 1; number <= 5; number++) Insert(number, "Radio", 1);
        string[] publications = ["2026-10-15T14:00:00+00:00", "2026-10-15T16:00:45+00:00", "2026-10-15T16:01:00+00:00", "2026-01-15T15:30:00+00:00"];
        store.Write(db =>
        {
            for (var i = 0; i < publications.Length; i++)
                db.Execute("UPDATE stories SET published_at=? WHERE url=?", publications[i], $"https://example.com/{i + 1}");
            db.Execute("UPDATE stories SET discovered_at='2026-10-15T15:00:00+00:00' WHERE url='https://example.com/5'");
            Store.BackfillStoryTimes(db);
        });
        var (start, end) = Search.TimeRange("10am - 12pm");
        var result = store.Stories(new() { TimeStart = start, TimeEnd = end, TimeZone = "America/New_York" });
        Assert.Equal(new HashSet<string> { "Story 1", "Story 2", "Story 4", "Story 5" }, result["items"]!.AsArray().Select(i => (string)i!["headline"]!).ToHashSet());
        Assert.Equal(0, Total(new() { TimeStart = start, TimeEnd = end, TimeZone = "UTC" }));
        Assert.Equal(3, Total(new() { Start = "2026-10-15", End = "2026-10-15", TimeStart = start, TimeEnd = end, TimeZone = "America/New_York" }));
        Assert.Throws<ValidationException>(() => store.Stories(new() { TimeStart = start, TimeEnd = end, TimeZone = "Not/AZone" }));
    }

    [Fact]
    public void KeywordSearchMatchesSourceNamesAndCombinesFilters()
    {
        Insert(1, "Radio", 10);
        Insert(2, "Science", 11);
        var configured = (JsonObject)store.Sources().Cast<JsonObject>().First(s => s["id"]!.AsLong() == source).DeepClone();
        configured["name"] = "Example 100%_ Source";
        configured["enabled"] = true;
        store.SaveSource(configured, source);
        Assert.Equal(2, Total(new() { Query = "example 100%_ source" }));
        Assert.Equal(1, Total(new() { Query = "100%", Tags = ["Radio"] }));
        Assert.Equal(0, Total(new() { Query = "100_" })); // SQL wildcards remain literal.
        Assert.Equal(0, Total(new() { Query = "Example", Start = "2000-01-01", End = "2000-01-02" }));
        Fixtures.Sql(store, "UPDATE sources SET removed=1,enabled=0 WHERE id=?", source);
        Assert.Equal(2, Total(new() { Query = "Example" })); // Archived source names stay searchable.
    }

    [Fact]
    public void ResetRemovesArchiveAndPreservesAllPreferences()
    {
        Insert(1, "Radio", 10);
        Insert(2, "Radio", 11);
        var items = store.Stories(new())["items"]!.AsArray();
        var (first, second) = (items[0]!["id"]!.AsLong(), items[1]!["id"]!.AsLong());
        store.Bookmark(first, true);
        var settings = store.Settings();
        store.ReviewResult(second, Reviewer.ValidateReview(Fixtures.Review(r => r["duplicate_of"] = first), new HashSet<long> { first }),
            (JsonObject)settings["ai"]!, (JsonObject)settings["interests"]!, "text", "excerpt");
        store.FinishRun(store.StartRun("collect", new JsonObject()), "complete", new JsonObject());
        var preferences = store.Settings();
        JsonArray Config() => new(store.Sources().Cast<JsonObject>().Select(s =>
        {
            var copy = (JsonObject)s.DeepClone();
            copy.Remove("last_checked");
            copy.Remove("last_error");
            return (JsonNode)copy;
        }).ToArray());
        var sourceConfig = Config();
        var result = store.Reset(new JsonObject { ["history"] = true, ["bookmarks"] = true, ["runs"] = true });
        Assert.Equal(2, result["deleted_stories"]!.AsLong());
        Assert.Equal(0, Total(new() { ShowHidden = true }));
        Assert.Equal(0, Total(new() { Bookmarks = true }));
        Assert.Empty(store.Overview()["runs"]!.AsArray());
        Assert.True(JsonNode.DeepEquals(store.Overview()["tags"], store.Topics()));
        Assert.True(JsonNode.DeepEquals(preferences, store.Settings()));
        Assert.True(JsonNode.DeepEquals(sourceConfig, Config()));
        var collector = Fixtures.Collector(store);
        var now = DateTimeOffset.Now;
        Assert.False(collector.ScheduleDue(now));
        Assert.True(collector.ScheduleDue(now.AddDays(1)));
        Assert.False(collector.Start(scheduled: true));
        Assert.True(JsonNode.DeepEquals(preferences, new Store(store.Path).Settings()));
    }
}
