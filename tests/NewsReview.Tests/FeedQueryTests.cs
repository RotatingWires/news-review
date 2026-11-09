using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace NewsReview.Tests;

public class FeedQueryTests : IDisposable
{
    readonly TempDirectory directory = new();
    readonly Store store;
    readonly long source;

    public FeedQueryTests()
    {
        store = new Store(directory.File("newsreview.db"));
        source = Fixtures.AddSource(store)["id"]!.AsLong();
    }

    public void Dispose() => directory.Dispose();

    void Insert(string name, string published, int score = 50)
    {
        store.InsertStory(source, Fixtures.Entry(name, "https://example.com/" + name, published, "Source details"), "Technology", "2026-10-31");
        Fixtures.Sql(store, "UPDATE stories SET status='reviewed',score=?,bookmarked=1 WHERE headline=?", score, name);
    }

    List<string> Names(Store.StoryQuery query) => store.Stories(query)["items"]!.AsArray().Select(r => (string)r!["headline"]!).ToList();

    [Fact]
    public void SortsApplyToWholeFilteredSetAndBookmarks()
    {
        Insert("old-high", "2026-10-30T10:00:00+00:00", 90);
        Insert("recent-low", "2026-10-31T14:00:00+00:00", 10);
        Insert("recent-medium", "2026-10-31T08:00:00+00:00", 50);
        var expected = new Dictionary<string, string[]>
        {
            ["rank"] = ["old-high", "recent-medium", "recent-low"],
            ["rank_asc"] = ["recent-low", "recent-medium", "old-high"],
            ["published"] = ["recent-low", "recent-medium", "old-high"],
            ["published_asc"] = ["old-high", "recent-medium", "recent-low"],
        };
        foreach (var (sort, names) in expected)
            foreach (var bookmarks in new[] { false, true })
                Assert.Equal(names, Names(new() { Sort = sort, Bookmarks = bookmarks }));
        Assert.Equal(expected["rank"], Names(new()));
        Assert.Equal(["recent-medium", "recent-low"], Names(new() { Day = "2026-10-31", TimeZone = "UTC", Sort = "published_asc" }));
        Assert.Equal(["recent-medium"], Names(new() { Query = "medium", Sort = "published" }));
        Assert.Contains("sort order", Assert.Throws<ValidationException>(() => store.Stories(new() { Sort = "score; DROP TABLE stories" })).Message);
    }

    [Fact]
    public void ExactOffsetAndMicrosecondBoundaries()
    {
        foreach (var (name, timestamp) in new[] { ("before", "2026-10-30T23:59:59.999999+00:00"), ("start", "2026-10-31T00:00:00+00:00"),
                     ("offset", "2026-10-31T05:45:00+05:45"), ("last", "2026-10-31T23:59:59.999999+00:00"), ("after", "2026-11-01T00:00:00+00:00") })
            Insert(name, timestamp);
        Assert.Equal(new HashSet<string> { "start", "offset", "last" }, Names(new() { Day = "2026-10-31", TimeZone = "UTC" }).ToHashSet());
        Assert.Equal("last", Names(new() { Day = "2026-10-31", TimeZone = "UTC", Sort = "published" })[0]);
        Assert.Equal(new HashSet<string> { "start", "offset", "last" }, Names(new() { Start = "2026-10-31", End = "2026-10-31", TimeZone = "UTC" }).ToHashSet());
        Assert.Equal(new HashSet<string> { "last", "after" }, Names(new() { Day = "2026-11-01", TimeZone = "Asia/Kathmandu" }).ToHashSet());
    }

    [Fact]
    public void DstDaysAndRepeatedClockHours()
    {
        foreach (var (name, timestamp) in new[] {
                     ("spring-before", "2026-03-08T04:59:59.999999+00:00"), ("spring-start", "2026-03-08T05:00:00+00:00"),
                     ("spring-last", "2026-03-09T03:59:59.999999+00:00"), ("spring-after", "2026-03-09T04:00:00+00:00"),
                     ("fall-first", "2026-11-01T05:30:00+00:00"), ("fall-second", "2026-11-01T06:30:00+00:00"),
                     ("fall-last", "2026-11-02T04:59:59.999999+00:00"), ("fall-after", "2026-11-02T05:00:00+00:00") })
            Insert(name, timestamp);
        Assert.Equal(new HashSet<string> { "spring-start", "spring-last" }, Names(new() { Day = "2026-03-08", TimeZone = "America/New_York" }).ToHashSet());
        Assert.Equal(new HashSet<string> { "fall-first", "fall-second", "fall-last" }, Names(new() { Day = "2026-11-01", TimeZone = "America/New_York" }).ToHashSet());
        Assert.Equal(["fall-first", "fall-second"], Names(new() { Day = "2026-11-01", TimeZone = "America/New_York", TimeStart = 90, TimeEnd = 90, Sort = "published_asc" }));
    }

    [Fact]
    public void EqualScoresAndTimesHaveStablePages()
    {
        for (var number = 0; number < 105; number++) Insert($"story-{number}", "2026-10-31T12:00:00+00:00");
        foreach (var sort in new[] { "rank", "rank_asc", "published", "published_asc" })
        {
            var names = new[] { 1, 2, 3 }.SelectMany(page => Names(new() { Sort = sort, Page = page })).ToList();
            var expected = sort == "published_asc" ? Enumerable.Range(0, 105) : Enumerable.Range(0, 105).Reverse();
            Assert.Equal(expected.Select(n => $"story-{n}"), names);
        }
    }

    [Fact]
    public void DailyFeedUsesBoundedCoveringIndexesAndNeverSelectsArticleBodies()
    {
        store.Write(db =>
        {
            for (var number = 0; number < 6000; number++)
            {
                var stamp = number < 60 ? "2026-10-31T12:00:00+00:00" : "2025-01-01T12:00:00+00:00";
                db.Execute("INSERT INTO stories(headline,url,published_at,discovered_at,day,sort_timestamp,article_text,interests_snapshot) VALUES (?,?,?,?,'2026-10-31',?,?,?)",
                    $"Story {number}", $"https://example.com/{number}", stamp, stamp, Store.PublicationTime(stamp),
                    string.Concat(Enumerable.Repeat("Article body ", 200)), string.Concat(Enumerable.Repeat("Private snapshot ", 100)));
            }
        });
        var statements = new List<string>();
        var steps = 0;
        // Instrument the real queries: trace their SQL, count VM steps, and deny reads of article bodies.
        store.Opened = connection =>
        {
            var handle = connection.Handle!;
            raw.sqlite3_trace(handle, (_, sql) => { lock (statements) statements.Add(sql.utf8_to_string()); }, null);
            raw.sqlite3_progress_handler(handle, 100, _ => { steps += 100; return 0; }, null);
            raw.sqlite3_set_authorizer(handle, (_, action, table, column, _, _) =>
                action == raw.SQLITE_READ && table.utf8_to_string() == "stories" &&
                column.utf8_to_string() is "article_text" or "interests_snapshot" or "why_it_matters" ? raw.SQLITE_DENY : raw.SQLITE_OK, null);
        };
        foreach (var sort in new[] { "rank", "rank_asc", "published", "published_asc" })
        {
            statements.Clear();
            steps = 0;
            var result = store.Stories(new() { Day = "2026-10-31", TimeZone = "UTC", Sort = sort });
            Assert.Equal((60L, 48), (result["total"]!.AsLong(), result["items"]!.AsArray().Count));
            Assert.True(steps < 12000, $"Daily feed work grew with the unrelated archive: {steps} steps");
            List<string> queries;
            lock (statements) queries = statements.Where(sql => sql.Contains("SELECT COUNT(*) FROM stories s WHERE") || sql.Contains("WITH page AS MATERIALIZED")).ToList();
            Assert.Equal(2, queries.Count);
            store.Read(db =>
            {
                foreach (var query in queries)
                {
                    var plan = db.Rows("EXPLAIN QUERY PLAN " + query).Select(row => (string)row["detail"]!).ToList();
                    Assert.Contains(plan, step => step.Contains("SEARCH s USING COVERING INDEX stories_publication"));
                    Assert.DoesNotContain("SCAN s", plan);
                }
                return 0;
            });
            Assert.False(((JsonObject)result["items"]![0]!).ContainsKey("article_text"));
        }
    }

    void InsertArchive(int count) => store.Write(db =>
    {
        for (var number = 0; number < count; number++)
        {
            var stamp = "2025-01-01T12:00:00+00:00";
            db.Execute("""
                INSERT INTO stories(source_id,headline,url,published_at,discovered_at,day,sort_timestamp,summary,tags,status,article_text,interests_snapshot)
                    VALUES (?,?,?,?,?,'2025-01-01',?,?,?,'reviewed',?,?)
                """, source, $"Story {number}", $"https://example.com/{number}", stamp, stamp, Store.PublicationTime(stamp),
                number % 100 == 0 ? "KDE Plasma release" : "Other news", number % 2 == 0 ? "[\"Linux\"]" : "[\"AI\"]",
                string.Concat(Enumerable.Repeat("Article body ", 200)), string.Concat(Enumerable.Repeat("Private snapshot ", 100)));
        }
    });

    [Fact]
    public void ArchiveSearchesScanTheSearchIndexInsteadOfStoryRows()
    {
        InsertArchive(3000);
        var statements = new List<string>();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // Pooled connections would keep an earlier trace.
        store.Opened = connection => raw.sqlite3_trace(connection.Handle!, (_, sql) => { lock (statements) statements.Add(sql.utf8_to_string()); }, null);
        foreach (var (query, expected) in new (Store.StoryQuery, long)[]
        {
            (new() { Query = "kde" }, 30), (new() { Tags = ["Linux"] }, 1500),
            (new() { Query = "KDE", Tags = ["Linux"], ExcludedRelevance = ["low"], Sort = "published" }, 30),
        })
        {
            statements.Clear();
            Assert.Equal(expected, store.Stories(query)["total"]!.AsLong());
            List<string> queries;
            lock (statements) queries = statements.Where(sql => sql.Contains("SELECT COUNT(*) FROM stories s WHERE") || sql.Contains("WITH page AS MATERIALIZED")).Distinct().ToList();
            Assert.Equal(2, queries.Count);
            store.Read(db =>
            {
                foreach (var sql in queries)
                {
                    var plan = db.Rows("EXPLAIN QUERY PLAN " + sql).Select(row => (string)row["detail"]!).ToList();
                    Assert.Contains(plan, step => step.Contains("USING COVERING INDEX stories_search"));
                    Assert.DoesNotContain("SCAN s", plan);
                }
                return 0;
            });
        }
    }

    [Fact]
    public void AbandonedSearchInterruptsItsScan()
    {
        InsertArchive(2000);
        using var cancel = new CancellationTokenSource();
        store.Opened = connection => raw.sqlite3_progress_handler(connection.Handle!, 1000, _ => { cancel.Cancel(); return 0; }, null);
        var error = Assert.Throws<SqliteException>(() => store.Stories(new() { Query = "absent" }, cancel.Token));
        Assert.Equal(raw.SQLITE_INTERRUPT, error.SqliteErrorCode);
    }

    [Fact]
    public void SchemaThreeUpgradeAndZipRestorePreserveOriginalRecords()
    {
        Insert("offset", "2026-10-31T02:00:00.123456+02:00", 95);
        store.MarkRead(1);
        store.RateStory(1, 1, "Keep all preferences");
        var settings = store.Settings();
        var original = store.Read(db => db.Row("SELECT * FROM stories"))!;
        store.Write(db =>
        {
            db.Execute("DROP INDEX stories_publication");
            db.Execute("DROP INDEX stories_bookmarks");
            db.Execute("DROP INDEX stories_search");
            db.Execute("ALTER TABLE stories DROP COLUMN sort_timestamp");
        });
        store.Read(db => db.Execute("PRAGMA user_version=3"));
        var legacy = directory.File("legacy.zip");
        Transfer.CreateArchive(store, legacy);
        var upgraded = new Store(store.Path);
        Assert.True(JsonNode.DeepEquals(original, upgraded.Read(db => db.Row("SELECT * FROM stories"))));
        Assert.True(JsonNode.DeepEquals(settings, upgraded.Settings()));
        upgraded.Reset(new JsonObject { ["history"] = true, ["bookmarks"] = true });
        Transfer.RestoreArchive(upgraded, legacy);
        Assert.True(JsonNode.DeepEquals(settings, upgraded.Settings()));
        Assert.Equal(["offset"], Names(new() { Day = "2026-10-31", TimeZone = "UTC" }));
        Assert.True(JsonNode.DeepEquals(original, upgraded.Read(db => db.Row("SELECT * FROM stories"))));
        Assert.Equal(original["sort_timestamp"]!.AsLong(), Store.PublicationTime("2026-10-31T00:00:00.123456Z"));
    }

    [Fact]
    public void BackfillCoversAllRowsAndRollsBackFailedUpgrade()
    {
        store.Write(db =>
        {
            db.Execute("DROP INDEX stories_publication");
            db.Execute("DROP INDEX stories_bookmarks");
            db.Execute("DROP INDEX stories_search");
            db.Execute("ALTER TABLE stories DROP COLUMN sort_timestamp");
            for (var number = 0; number < 1005; number++)
                db.Execute("INSERT INTO stories(headline,url,published_at,discovered_at,day) VALUES (?,?,?,?, '2026-10-31')",
                    $"Story {number}", $"https://example.com/{number}", number < 700 ? "2026-10-31T12:00:00+00:00" : null, "2026-10-31T13:00:00.123456+00:00");
            db.Execute("UPDATE stories SET published_at='bad date' WHERE id=1005");
        });
        store.Read(db => db.Execute("PRAGMA user_version=3"));
        Assert.ThrowsAny<FormatException>(() => new Store(store.Path));
        store.Read(db =>
        {
            Assert.DoesNotContain("sort_timestamp", db.Rows("PRAGMA table_info(stories)").Select(row => (string)row["name"]!));
            Assert.Equal(3, db.Long("PRAGMA user_version"));
            return db.Execute("UPDATE stories SET published_at=NULL WHERE id=1005");
        });
        var upgraded = new Store(store.Path);
        upgraded.Read(db =>
        {
            Assert.Equal(700, db.Long("SELECT COUNT(*) FROM stories WHERE sort_timestamp=?", Store.PublicationTime("2026-10-31T12:00:00+00:00")));
            Assert.Equal(305, db.Long("SELECT COUNT(*) FROM stories WHERE sort_timestamp=?", Store.PublicationTime("2026-10-31T13:00:00.123456+00:00")));
            return 0;
        });
    }
}
