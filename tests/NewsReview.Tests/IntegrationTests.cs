using System.Text.Json.Nodes;

namespace NewsReview.Tests;

/// <summary>Shared setup: the fixture news site and model servers, the in-memory app, and a signed-in client.</summary>
public abstract class IntegrationBase : IAsyncLifetime
{
    internal readonly NewsFixture fixture = new();
    internal readonly AppHost host = new();
    internal HttpClient client = null!;
    internal Store store = null!;
    internal Collector collector = null!;
    internal string Root => fixture.Root;

    public async Task InitializeAsync()
    {
        store = host.Store;
        collector = host.Collector;
        Fixtures.ChangeTopic(store, "add", "Standards");
        store.SaveSource(new JsonObject { ["name"] = "Fixture", ["url"] = Root, ["feed_url"] = "", ["category"] = "Standards", ["enabled"] = true });
        client = await host.SignedIn();
    }

    public async Task DisposeAsync()
    {
        collector.Stop();
        await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>(), 20);
        fixture.ReviewRelease.Set();
        await host.DisposeAsync();
        fixture.Dispose();
    }

    internal JsonObject ModelConfig(string provider = "ollama", string model = "fixture")
    {
        var config = (JsonObject)store.Settings()["ai"]!.DeepClone();
        config["provider"] = provider;
        config["base_url"] = Root + (provider == "ollama" ? "" : "/v1");
        config["model"] = model;
        return config;
    }

    internal async Task Collect(string kind = "collect")
    {
        Assert.True(collector.Start(kind));
        await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>());
    }

    internal Task<(int Status, JsonNode? Body)> Request(string path, string method = "GET", object? data = null, Dictionary<string, string>? headers = null) =>
        client.Json(path, new HttpMethod(method), data, headers);

    internal Task<(int Status, JsonNode? Body)> Anonymous(string path, string method = "GET", object? data = null) =>
        host.CreateClient().Json(path, new HttpMethod(method), data);

    internal long Total(Store.StoryQuery query) => store.Stories(query)["total"]!.AsLong();

    internal long EnabledSource() => store.Sources().Cast<JsonObject>().First(s => s["enabled"]!.AsLong() != 0)["id"]!.AsLong();

    internal static List<string> Tags(JsonNode story) => story["tags"]!.AsArray().Select(t => (string)t!).ToList();

    internal (string Path, JsonObject Payload) LastModelRequest()
    {
        lock (fixture.ModelRequests) return (fixture.ModelRequests[^1].Path, (JsonObject)fixture.ModelRequests[^1].Payload!);
    }
}

public class ApiTests : IntegrationBase
{
    [Fact]
    public async Task KeywordAndTimeSearchRemainDayScopedUntilDateRange()
    {
        foreach (var day in new[] { "2026-10-15", "2026-10-16" })
            store.InsertStory(EnabledSource(), Fixtures.Entry("Shared protocol news", "https://example.com/" + day, day + "T15:00:00+00:00", ""), "Protocols", day);
        const string query = "/api/stories?day=2026-10-16&query=Shared&time_zone=UTC";
        Assert.Equal(1, (await Request(query)).Body!["total"]!.AsLong());
        Assert.Equal(0, (await Request(query + "&period=10am%20-%2012pm")).Body!["total"]!.AsLong());
        Assert.Equal(2, (await Request(query + "&period=10/15/26%20-%2010/16/26")).Body!["total"]!.AsLong());
        Assert.Equal(2, (await Request(query + "&period=10/15/26%20-%2010/16/26,%202pm%20-%204pm")).Body!["total"]!.AsLong());
    }

    [Fact]
    public async Task StorySortApiUsesPublicationTimesAndValidatesOrder()
    {
        foreach (var (name, time, score) in new[] { ("older-high", "08:00:00", 95), ("newer-low", "17:00:00", 20) })
        {
            store.InsertStory(EnabledSource(), Fixtures.Entry(name, "https://example.com/" + name, "2026-10-31T" + time + "+00:00"), "Technology", "2026-10-31");
            Fixtures.Sql(store, "UPDATE stories SET score=?,bookmarked=1 WHERE headline=?", score, name);
        }
        foreach (var (sort, first) in new[] { ("rank", "older-high"), ("rank_asc", "newer-low"), ("published", "newer-low"), ("published_asc", "older-high") })
        {
            var (status, result) = await Request("/api/stories?day=2026-10-31&time_zone=UTC&sort=" + sort);
            Assert.Equal(200, status);
            Assert.Equal(first, (string)result!["items"]![0]!["headline"]!);
            (status, result) = await Request("/api/stories?bookmarks=1&sort=" + sort);
            Assert.Equal((200, first), (status, (string)result!["items"]![0]!["headline"]!));
            var keys = ((JsonObject)result["items"]![0]!).Select(pair => pair.Key).ToHashSet();
            Assert.Empty(keys.Intersect(["article_text", "interests_snapshot", "why_it_matters", "sort_timestamp"]));
        }
        Assert.Equal(400, (await Request("/api/stories?sort=unknown")).Status);
    }

    [Fact]
    public async Task DuplicatePurgeConfirmationLockBookmarksReferencesAndCompaction()
    {
        var ids = new Dictionary<string, long>();
        foreach (var name in new[] { "main", "delete", "bookmark", "parent", "child", "pending", "filtered" })
        {
            store.InsertStory(EnabledSource(), Fixtures.Entry(name, "https://example.com/purge/" + name, null, ""), "Security", "2026-10-21");
            ids[name] = store.Read(db => db.Long("SELECT id FROM stories WHERE headline=?", name));
        }
        store.Write(db =>
        {
            foreach (var name in new[] { "delete", "bookmark", "parent", "child" })
                db.Execute("UPDATE stories SET status='duplicate', duplicate_of=? WHERE id=?", ids["main"], ids[name]);
            db.Execute("UPDATE stories SET bookmarked=1 WHERE id IN (?,?)", ids["bookmark"], ids["child"]);
            db.Execute("UPDATE stories SET duplicate_of=? WHERE id=?", ids["parent"], ids["child"]);
            db.Execute("UPDATE stories SET status='filtered' WHERE id=?", ids["filtered"]);
            db.Execute("UPDATE stories SET article_text=? WHERE id=?", string.Concat(Enumerable.Repeat("Redundant article text ", 30000)), ids["delete"]);
        });
        var settings = store.Settings();
        var revision = (string)store.Overview()["revision"]!;
        var beforePages = store.Read(db => db.Long("PRAGMA page_count"));
        Assert.Equal(1, (await Request("/api/maintenance/duplicates")).Body!["count"]!.AsLong());
        const string path = "/api/maintenance/duplicates/purge";
        Assert.Equal(400, (await Request(path, "POST", new { })).Status);
        var data = new { confirmation = "PURGE DUPLICATES" };
        Assert.True(collector.TryAcquire());
        try { Assert.Equal(409, (await Request(path, "POST", data)).Status); }
        finally { collector.Release(); }
        Assert.Equal(403, (await Request(path, "POST", data, new() { ["Origin"] = "https://evil.example" })).Status);
        var (status, result) = await Request(path, "POST", data);
        Assert.Equal(200, status);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["deleted"] = 1, ["compacted"] = true }, result));
        Assert.True(JsonNode.DeepEquals(settings, store.Settings()));
        Assert.NotEqual(revision, (string)store.Overview()["revision"]!);
        Assert.Equal(6, Total(new() { ShowHidden = true }));
        Assert.Equal(2, Total(new() { Bookmarks = true }));
        store.Read(db =>
        {
            Assert.Empty(db.Rows("PRAGMA foreign_key_check"));
            Assert.True(db.Long("PRAGMA page_count") < beforePages);
            return 0;
        });
        Assert.Equal(0, (await Request("/api/maintenance/duplicates")).Body!["count"]!.AsLong());
        Assert.Equal(0, (await Request(path, "POST", data)).Body!["deleted"]!.AsLong());
        Assert.Equal(401, (await Anonymous(path, "POST", data)).Status);
    }

    [Fact]
    public async Task TopicsApiSavesTheListWithRenamesAndConfirmedRemovals()
    {
        var existing = (await Request("/api/topics")).Body!.AsArray().Select(t => (string)t!).ToList();
        Assert.Equal(existing, store.Topics().Select(t => (string)t!));
        object Save(IEnumerable<string> topics, object? renames = null, string? confirmation = null) =>
            new { topics = topics.ToArray(), renames = renames ?? new { }, confirmation };
        var add = Save([.. existing, "Custom topic"]);
        Assert.Equal(401, (await Anonymous("/api/topics", "PUT", add)).Status);
        Assert.True(collector.TryAcquire());
        try { Assert.Equal(409, (await Request("/api/topics", "PUT", add)).Status); }
        finally { collector.Release(); }
        var (status, result) = await Request("/api/topics", "PUT", add);
        Assert.Equal(200, status);
        Assert.Subset(result!["topics"]!.AsArray().Select(t => (string)t!).ToHashSet(), existing.ToHashSet());
        List<string> Current() => store.Topics().Select(t => (string)t!).ToList();
        Assert.Equal(400, (await Request("/api/topics", "PUT", Save([.. Current(), "custom TOPIC"]))).Status);
        Assert.Equal(400, (await Request("/api/topics", "PUT", Save([.. Current(), ""]))).Status);
        store.InsertStory(EnabledSource(), Fixtures.Entry("A useful protocol update", Root + "/article", null, "Source details."), "Standards", "2026-10-25");
        var story = store.Stories(new())["items"]![0]!["id"]!.AsLong();
        store.ReviewResult(story, Fixtures.Review(r => { r["tags"] = new JsonArray("Custom topic", "Standards"); r["status"] = "reviewed"; }),
            ModelConfig(), (JsonObject)store.Settings()["interests"]!, "Article text.", "article");
        store.Bookmark(story, true);
        List<string> StoryTags() => Tags(store.Stories(new() { Bookmarks = true })["items"]![0]!);
        const string renamed = "SDR / scanner's projects";
        IEnumerable<string> Replace(string from, string to) => Current().Select(n => n == from ? to : n);
        Assert.Equal(400, (await Request("/api/topics", "PUT", Save(Replace("Custom topic", "Standards").Distinct(), new Dictionary<string, string> { ["Custom topic"] = "Standards" }))).Status);
        (status, result) = await Request("/api/topics", "PUT", Save(Replace("Custom topic", renamed), new Dictionary<string, string> { ["Custom topic"] = renamed }));
        Assert.Equal((200, 1L), (status, result!["updated_stories"]!.AsLong()));
        Assert.Equal([renamed, "Standards"], StoryTags());
        // A case-only edit is a rename even without an explicit mapping.
        Assert.Equal(200, (await Request("/api/topics", "PUT", Save(Replace(renamed, renamed.ToUpperInvariant())))).Status);
        Assert.Equal([renamed.ToUpperInvariant(), "Standards"], StoryTags());
        var without = Current().Where(n => n != renamed.ToUpperInvariant()).ToList();
        Assert.Equal(400, (await Request("/api/topics", "PUT", Save(without))).Status);
        Assert.Equal([renamed.ToUpperInvariant(), "Standards"], StoryTags());
        (status, result) = await Request("/api/topics", "PUT", Save(without, confirmation: "DELETE TOPIC"));
        Assert.Equal(200, status);
        Assert.Equal([renamed.ToUpperInvariant()], result!["removed"]!.AsArray().Select(t => (string)t!));
        Assert.Equal(["Standards"], StoryTags());
        Assert.Equal(1, Total(new()));
        Assert.Subset(Current().ToHashSet(), existing.ToHashSet());
    }
}

public class ModelTransportTests : IntegrationBase
{
    [Fact]
    public async Task EmptyAndCustomOwnedTopicListsWorkWithModelReview()
    {
        foreach (var name in store.Topics().Select(t => (string)t!).ToList()) Fixtures.ChangeTopic(store, "delete", name);
        foreach (var provider in new[] { "ollama", "compatible", "openrouter" })
            Assert.Empty(Tags(await collector.Reviewer.Test(ModelConfig(provider), [])));
        Fixtures.ChangeTopic(store, "add", "Only my custom label");
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig() });
        await Collect();
        Assert.Equal(["Only my custom label"], store.Topics().Select(t => (string)t!));
        Assert.All(store.Stories(new())["items"]!.AsArray(), story => Assert.Equal(["Only my custom label"], Tags(story!)));
        List<JsonObject> requests;
        lock (fixture.ModelRequests)
            requests = fixture.ModelRequests.Where(r => r.Path == "/api/chat" && (string?)r.Payload!["model"] == "fixture").Select(r => (JsonObject)r.Payload!).ToList();
        Assert.Equal(["Only my custom label"], requests[^1]["format"]!["properties"]!["tags"]!["items"]!["enum"]!.AsArray().Select(t => (string)t!));
    }

    [Fact]
    public async Task ModelTagsAreConstrainedInAllTransports()
    {
        foreach (var provider in new[] { "ollama", "compatible", "openrouter" })
        {
            var result = await collector.Reviewer.Test(ModelConfig(provider));
            Assert.Equal(["Web Platform"], Tags(result));
            var (_, payload) = LastModelRequest();
            var schema = provider == "ollama" ? payload["format"]! : JsonNode.Parse((string)payload["messages"]![1]!["content"]!)!["schema"]!;
            Assert.Equal(["Web Platform"], schema["properties"]!["tags"]!["items"]!["enum"]!.AsArray().Select(t => (string)t!));
            result = await collector.Reviewer.Test(ModelConfig(provider, "invented-topic"));
            Assert.Empty(Tags(result));
            Assert.Equal((string)Fixtures.Review()["summary"]!, (string)result["summary"]!);
        }
    }
}

public class CollectionTests : IntegrationBase
{
    List<(string Path, JsonObject Payload)> RequestsSince(int start)
    {
        lock (fixture.ModelRequests) return fixture.ModelRequests.Skip(start).Select(r => (r.Path, (JsonObject)r.Payload!)).ToList();
    }

    int RequestCount() { lock (fixture.ModelRequests) return fixture.ModelRequests.Count; }

    JsonObject LatestRun() => (JsonObject)store.Overview()["runs"]![0]!;

    JsonArray StoryItems() => store.Stories(new())["items"]!.AsArray();

    [Fact]
    public async Task RelevanceFilterApiValidation()
    {
        Assert.Equal(400, (await Request("/api/stories?exclude_relevance=unknown")).Status);
        Assert.Equal(400, (await Request("/api/stories?" + string.Concat(Enumerable.Repeat("exclude_relevance=high&", 5)))).Status);
        Assert.Equal(200, (await Request("/api/stories?exclude_relevance=high&exclude_relevance=pending")).Status);
    }

    [Fact]
    public async Task DiscoveryCollectionModelReviewAndIdempotence()
    {
        Assert.Equal(Root + "/feed", await Feeds.Discover(collector.Fetcher, Root));
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig() });
        await Collect();
        Assert.Equal(2, Total(new()));
        var stories = StoryItems();
        Assert.All(stories, s => Assert.Equal("reviewed", (string)s!["status"]!));
        Assert.Equal(new HashSet<string> { "article", "excerpt" }, stories.Select(s => (string)s!["content_basis"]!).ToHashSet());
        Assert.All(stories, s => Assert.Equal(100, s!["input_tokens"]!.AsLong()));
        await Collect();
        Assert.Equal(2, Total(new()));
        Assert.Equal(0, LatestRun()["details"]!["added"]!.AsLong());
    }

    [Fact]
    public async Task CollectionWithoutModelAndLaterCompatibleReview()
    {
        await Collect();
        Assert.Equal(2, store.Overview()["counts"]!["pending"]!.AsLong());
        Assert.Contains("No model configured", (string)LatestRun()["details"]!["note"]!);
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig("compatible") });
        await Collect("review");
        Assert.All(StoryItems(), s => Assert.Equal(("compatible", 25L), ((string)s!["provider"]!, s["output_tokens"]!.AsLong())));
        Assert.Equal("/v1/chat/completions", LastModelRequest().Path);
    }

    [Fact]
    public async Task BrokenModelPreservesExcerptsAndRecordsFailure()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "broken") });
        await Collect();
        var stories = StoryItems();
        Assert.Equal(2, stories.Count);
        Assert.All(stories, s => Assert.True((string)s!["status"]! == "pending" && !string.IsNullOrEmpty((string?)s["review_error"])));
        Assert.Equal("partial", (string)LatestRun()["status"]!);
    }

    [Fact]
    public async Task UncheckedCollectionSkipsCheckedDisabledAndUnrelatedReviewQueue()
    {
        var checkedId = store.Sources().Cast<JsonObject>().First(s => (string)s["name"]! == "Fixture")["id"]!.AsLong();
        store.SourceChecked(checkedId, "Previously failed");
        var checkedBefore = (string)store.Sources().Cast<JsonObject>().First(s => s["id"]!.AsLong() == checkedId)["last_checked"]!;
        store.InsertStory(checkedId, Fixtures.Entry("Old queued story", Root + "/old-queued", null, "Old text"), "Standards", "2026-10-15");
        var freshId = store.SaveSource(new JsonObject { ["name"] = "New listing", ["url"] = Root + "/listing", ["feed_url"] = "", ["category"] = "Radio", ["mode"] = "page", ["enabled"] = true });
        var disabledId = store.SaveSource(new JsonObject { ["name"] = "Disabled new", ["url"] = Root + "/disabled", ["feed_url"] = "", ["category"] = "Radio", ["enabled"] = false });
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig() });
        Assert.Equal(400, (await Request("/api/jobs/start", "POST", new { kind = "review", unchecked_only = true })).Status);
        Assert.Equal(400, (await Request("/api/jobs/start", "POST", new { unchecked_only = "yes" })).Status);
        Assert.Equal(202, (await Request("/api/jobs/start", "POST", new { unchecked_only = true })).Status);
        await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>());
        var sources = store.Sources().Cast<JsonObject>().ToDictionary(s => s["id"]!.AsLong());
        Assert.Equal(checkedBefore, (string)sources[checkedId]["last_checked"]!);
        Assert.Equal("Previously failed", (string)sources[checkedId]["last_error"]!);
        Assert.NotNull(sources[freshId]["last_checked"]);
        Assert.Null(sources[disabledId]["last_checked"]);
        var old = StoryItems().First(s => (string)s!["headline"]! == "Old queued story")!;
        Assert.Equal(("pending", 0L), ((string)old["status"]!, old["attempts"]!.AsLong()));
        var details = LatestRun()["details"]!;
        Assert.Equal("unchecked", (string)details["scope"]!);
        Assert.Equal(1, details["sources_checked"]!.AsLong());
        Assert.Equal(2, details["added"]!.AsLong());
        Assert.Equal(1, details["reviewed"]!.AsLong());
        Assert.NotEmpty(details["errors"]!.AsArray()); // The second listing fixture has no readable article/excerpt.
        Assert.True(collector.ScheduleDue(DateTimeOffset.Now));
        Assert.True(collector.Start(uncheckedOnly: true));
        await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>());
        Assert.Equal(0, LatestRun()["details"]!["sources_checked"]!.AsLong());
        Assert.Equal(0, StoryItems().First(s => (string)s!["headline"]! == "Old queued story")!["attempts"]!.AsLong());
    }

    [Fact]
    public async Task SingleSourceCheckCollectsAndReviewsOnlyThatSource()
    {
        var otherId = store.Sources().Cast<JsonObject>().First(s => (string)s["name"]! == "Fixture")["id"]!.AsLong();
        store.SourceChecked(otherId, "Previously failed");
        var otherBefore = (string)store.Sources().Cast<JsonObject>().First(s => s["id"]!.AsLong() == otherId)["last_checked"]!;
        store.InsertStory(otherId, Fixtures.Entry("Other queued story", Root + "/other-queued", null, "Other text"), "Standards", "2026-10-15");
        var targetId = store.SaveSource(new JsonObject { ["name"] = "Chosen listing", ["url"] = Root + "/listing", ["feed_url"] = "", ["category"] = "Radio", ["mode"] = "page", ["enabled"] = true });
        var disabledId = store.SaveSource(new JsonObject { ["name"] = "Disabled", ["url"] = Root + "/disabled", ["feed_url"] = "", ["category"] = "Radio", ["enabled"] = false });
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig() });
        Assert.Equal(400, (await Request("/api/jobs/start", "POST", new { kind = "review", source_id = targetId })).Status);
        Assert.Equal(400, (await Request("/api/jobs/start", "POST", new { source_id = "one" })).Status);
        Assert.Equal(400, (await Request("/api/jobs/start", "POST", new { source_id = 999999 })).Status);
        Assert.Equal(400, (await Request("/api/jobs/start", "POST", new { source_id = disabledId })).Status);
        Assert.Equal(400, (await Request("/api/jobs/start", "POST", new { unchecked_only = true, source_id = targetId })).Status);
        Assert.Equal(202, (await Request("/api/jobs/start", "POST", new { source_id = targetId })).Status);
        await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>());
        var sources = store.Sources().Cast<JsonObject>().ToDictionary(s => s["id"]!.AsLong());
        Assert.NotNull(sources[targetId]["last_checked"]);
        Assert.Equal(otherBefore, (string)sources[otherId]["last_checked"]!);
        Assert.Null(sources[disabledId]["last_checked"]);
        var other = StoryItems().First(s => (string)s!["headline"]! == "Other queued story")!;
        Assert.Equal(("pending", 0L), ((string)other["status"]!, other["attempts"]!.AsLong()));
        var details = LatestRun()["details"]!;
        Assert.Equal(("source", "Chosen listing", 1L), ((string)details["scope"]!, (string)details["source"]!, details["sources_checked"]!.AsLong()));
        Assert.Equal(2, details["added"]!.AsLong());
        Assert.StartsWith("Checked Chosen listing · Saved 2 new stories", (string)collector.Status()["message"]!);
        // A one-source check is not a full collection, so it does not restart the schedule.
        Assert.True(collector.ScheduleDue(DateTimeOffset.Now));
    }

    [Fact]
    public async Task OneSourceFailureDoesNotAbortOthers()
    {
        store.SaveSource(new JsonObject { ["name"] = "Broken", ["url"] = Root + "/missing", ["feed_url"] = Root + "/missing", ["category"] = "Test", ["enabled"] = true });
        await Collect();
        Assert.Equal(2, Total(new()));
        Assert.Equal("partial", (string)LatestRun()["status"]!);
    }

    [Fact]
    public async Task OllamaDiagnosticsAndFencedJsonValidation()
    {
        foreach (var (model, message) in new[] { ("truncated", "cut off"), ("empty-thinking", "thinking output"), ("broken", "invalid JSON") })
        {
            Assert.Contains(message, (await Assert.ThrowsAsync<ValidationException>(() => collector.Reviewer.Test(ModelConfig(model: model)))).Message);
            Assert.Equal("0", LastModelRequest().Payload["keep_alive"]!.ToJsonString());
        }
        Assert.Equal("reviewed", (string)(await collector.Reviewer.Test(ModelConfig(model: "fenced")))["status"]!);
        var config = ModelConfig();
        config["unload_after_use"] = false;
        config["max_output_tokens"] = 3000;
        await collector.Reviewer.Test(config);
        var payload = LastModelRequest().Payload;
        Assert.False(payload.ContainsKey("keep_alive"));
        Assert.Equal(3000, payload["options"]!["num_predict"]!.AsLong());
    }

    [Fact]
    public async Task BatchKeepsOllamaWarmAndUnloadsAfterSuccessOrFailure()
    {
        foreach (var model in new[] { "fixture", "broken" })
        {
            store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: model) });
            store.InsertStory(EnabledSource(), Fixtures.Entry("A useful protocol update", Root + "/article?case=" + model, null,
                "A useful protocol update supports experimental deployment."), "Protocols", "2026-10-24");
            var start = RequestCount();
            await Collect("review");
            var requests = RequestsSince(start);
            var chats = requests.Where(r => r.Path == "/api/chat").Select(r => r.Payload).ToList();
            Assert.NotEmpty(chats);
            Assert.All(chats, payload => Assert.Equal("5m", (string)payload["keep_alive"]!));
            Assert.Equal("/api/generate", requests[^1].Path);
            Assert.Equal("0", requests[^1].Payload["keep_alive"]!.ToJsonString());
            Assert.Equal(model, (string)requests[^1].Payload["model"]!);
        }
    }

    [Fact]
    public async Task ModelHttpErrorsPreserveBoundedJsonDetails()
    {
        var logger = new ListLogger();
        var reviewer = new Reviewer(new Fetcher(publicOnly: false, logger), Microsoft.Extensions.Logging.Abstractions.NullLogger<Reviewer>.Instance);
        foreach (var provider in new[] { "ollama", "compatible", "openrouter" })
        {
            logger.Messages.Clear();
            var error = await Assert.ThrowsAsync<ValidationException>(() => reviewer.Test(ModelConfig(provider, "http-error")));
            Assert.StartsWith("HTTP 400 from 127.0.0.1: Input exceeds the model context window", error.Message);
            Assert.Contains("Reduce input length.", logger.Messages[0]);
        }
        Assert.Contains("Unsupported parameter: response_format", (await Assert.ThrowsAsync<ValidationException>(() => reviewer.Test(ModelConfig(model: "http-error-message")))).Message);
        foreach (var model in new[] { "http-error-html", "http-error-huge", "http-error-shape" })
            Assert.Equal("HTTP 400 from 127.0.0.1", (await Assert.ThrowsAsync<ValidationException>(() => reviewer.Test(ModelConfig(model: model)))).Message);
        Assert.Equal("HTTP 400 from 127.0.0.1: " + new string('x', 240),
            (await Assert.ThrowsAsync<ValidationException>(() => reviewer.Test(ModelConfig(model: "http-error-long")))).Message);
    }

    [Fact]
    public async Task RejectedModelRequestsKeepQueueAndStoreExplanation()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "http-error") });
        for (var number = 0; number < 4; number++)
            store.InsertStory(EnabledSource(), Fixtures.Entry($"Protocol update {number}", Root + $"/excerpt?error={number}", null,
                "An experimental protocol feature is available."), "Protocols", "2026-10-25");
        await Collect("review");
        var run = LatestRun();
        Assert.Equal("partial", (string)run["status"]!);
        var errors = run["details"]!["errors"]!.AsArray();
        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.Contains("Input exceeds the model context window", (string)e!));
        Assert.Contains("Check the run errors and AI settings", (string)run["details"]!["note"]!);
        var stories = StoryItems();
        Assert.Equal(4, stories.Count);
        Assert.All(stories, s => Assert.Equal("pending", (string)s!["status"]!));
        Assert.Equal([0L, 1L, 1L, 1L], stories.Select(s => s!["attempts"]!.AsLong()).Order());
        Assert.Equal("/api/generate", LastModelRequest().Path);
    }

    [Fact]
    public async Task UnreadableArticlesDoNotStopBatchOrCallModelAndRemainRetryable()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig() });
        for (var number = 0; number < 5; number++)
            store.InsertStory(EnabledSource(), Fixtures.Entry($"Protocol article {number}", Root + (number == 3 ? "/blank" : $"/excerpt?empty={number}"), null,
                number < 4 ? "" : "A readable source excerpt with technical details."), "Protocols", "2026-10-29");
        var before = RequestCount();
        await Collect("review");
        var run = LatestRun();
        Assert.Equal("partial", (string)run["status"]!);
        Assert.Equal(1, run["details"]!["reviewed"]!.AsLong());
        Assert.Equal(4, run["details"]!["errors"]!.AsArray().Count);
        Assert.False(((JsonObject)run["details"]!).ContainsKey("note"));
        Assert.Equal(["/api/chat", "/api/generate"], RequestsSince(before).Select(r => r.Path));
        var pending = store.Pending(10);
        Assert.Equal(4, pending.Count);
        Assert.All(pending, s => Assert.True(s["attempts"]!.AsLong() == 1 && ((string)s["summary"]!).Length == 0));
        const string noExcerpt = " and the source gave no excerpt; showing headline and link only";
        Assert.Equal([.. Enumerable.Repeat("Article could not be read (HTTP 404 from 127.0.0.1)" + noExcerpt, 3), "Article page had no readable text" + noExcerpt],
            pending.OrderBy(s => (string)s["headline"]!).Select(s => (string)s["review_error"]!));
        await Collect("review");
        await Collect("review");
        Assert.Empty(store.Pending(10));
        Assert.Equal(before + 2, RequestCount());
        Assert.Equal(4, store.Requeue(true));
        Assert.Equal(4, store.Pending(10).Count);
    }

    [Fact]
    public async Task UnreadablePagesDoNotResetModelFailureCutoff()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "http-error") });
        for (var number = 0; number < 7; number++)
            store.InsertStory(EnabledSource(), Fixtures.Entry($"Protocol article {number}", Root + $"/excerpt?mixed={number}", null,
                number % 2 == 0 ? "Readable technical details." : ""), "Protocols", "2026-10-29");
        var before = RequestCount();
        await Collect("review");
        var run = LatestRun();
        Assert.Equal(5, run["details"]!["errors"]!.AsArray().Count);
        Assert.Contains("three consecutive model-review failures", (string)run["details"]!["note"]!);
        Assert.Equal(3, RequestsSince(before).Count(r => r.Path == "/api/chat"));
        Assert.Equal([0L, 0L, 1L, 1L, 1L, 1L, 1L], store.Pending(10).Select(s => s["attempts"]!.AsLong()).Order());
    }
}

public class WorkflowTests : IntegrationBase
{
    int AuthorizationCount() { lock (fixture.ModelRequests) return fixture.Authorizations.Count; }
    /// <summary>Unknown addresses load the app page, like the other apps; the private file itself never comes back.</summary>
    async Task AssertNotServed(string path, string privateText)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(privateText, body);
        if (response.IsSuccessStatusCode) Assert.Contains("<title>NewsReview</title>", body);
    }
    string LastAuthorization() { lock (fixture.ModelRequests) return fixture.Authorizations[^1]; }
    List<string> AuthorizationsSince(int start) { lock (fixture.ModelRequests) return fixture.Authorizations.Skip(start).ToList(); }
    List<(string Path, JsonObject Payload)> RequestsSince(int start)
    {
        lock (fixture.ModelRequests) return fixture.ModelRequests.Skip(start).Select(r => (r.Path, (JsonObject)r.Payload!)).ToList();
    }
    int RequestCount() { lock (fixture.ModelRequests) return fixture.ModelRequests.Count; }
    JsonObject LatestRun() => (JsonObject)store.Overview()["runs"]![0]!;
    JsonArray StoryItems() => store.Stories(new())["items"]!.AsArray();
    static JsonObject With(JsonObject value, string key, JsonNode? node)
    {
        var copy = (JsonObject)value.DeepClone();
        copy[key] = node;
        return copy;
    }
    void InsertStories(string headline, string urlPrefix, int count, string excerpt, string category = "Protocols", string day = "2026-10-25")
    {
        for (var number = 0; number < count; number++)
            store.InsertStory(EnabledSource(), Fixtures.Entry($"{headline} {number}", $"{urlPrefix}{number}", null, excerpt), category, day);
    }

    [Fact]
    public async Task UnknownModelTopicsDoNotLoseSavedSummariesOrExpandTopics()
    {
        var topics = store.Topics();
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "invented-topic") });
        InsertStories("Protocol article", Root + "/excerpt?tags=", 4, "A source excerpt with technical details.", day: "2026-10-29");
        await Collect("review");
        var run = LatestRun();
        Assert.Equal("complete", (string)run["status"]!);
        Assert.Equal(4, run["details"]!["reviewed"]!.AsLong());
        Assert.True(JsonNode.DeepEquals(topics, store.Topics()));
        Assert.All(StoryItems(), s => Assert.True(Tags(s!).Count == 0 && (string)s!["summary"]! == (string)Fixtures.Review()["summary"]!));
        Assert.All(StoryItems(), s => Assert.True(s!["review_error"] is null && s["attempts"]!.AsLong() == 1));
    }

    [Fact]
    public async Task UnloadFailureDoesNotDiscardSuccessfulReviews()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "unload-error") });
        store.InsertStory(EnabledSource(), Fixtures.Entry("Protocol testing update", Root + "/excerpt", null, "This protocol has added support for experimental features."), "Protocols", "2026-10-24");
        await Collect("review");
        Assert.Equal("reviewed", (string)StoryItems()[0]!["status"]!);
        Assert.Contains("Model unload failed", (string)collector.Status()["message"]!);
        Assert.True(collector.TryAcquire());
        collector.Release();
    }

    [Fact]
    public async Task ModelTestCannotUnloadAnActiveReviewBatch()
    {
        Assert.True(collector.TryAcquire());
        try { Assert.Equal(409, (await Request("/api/test-model", "POST", ModelConfig())).Status); }
        finally { collector.Release(); }
    }

    [Fact]
    public async Task StopReviewFinishesCurrentArticlePreservesQueueAndCanResume()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "blocking-review") });
        for (var number = 0; number < 3; number++)
            store.InsertStory(EnabledSource(), Fixtures.Entry($"Protocol progress {number}", Root + $"/article?stop={number}", null,
                "This is a substantive technical development with experimental support."), "Protocols", "2026-10-25");
        var settings = store.Settings();
        Assert.Equal(409, (await Request("/api/jobs/stop-review", "POST", new { })).Status);
        Assert.True(collector.Start("review"));
        try
        {
            Assert.True(fixture.ReviewEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(collector.Status()["reviewing"]!.GetValue<bool>());
            Assert.Equal(401, (await Anonymous("/api/jobs/stop-review", "POST", new { })).Status);
            Assert.Equal(403, (await Request("/api/jobs/stop-review", "POST", new { }, new() { ["Origin"] = "https://evil.example" })).Status);
            Assert.Equal(202, (await Request("/api/jobs/stop-review", "POST", new { })).Status);
            Assert.True(collector.Status()["review_stop_requested"]!.GetValue<bool>());
        }
        finally
        {
            fixture.ReviewRelease.Set();
            await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>());
        }
        Assert.Equal(2, store.Pending(10).Count);
        Assert.Equal(1, LatestRun()["details"]!["reviewed"]!.AsLong());
        Assert.True(LatestRun()["details"]!["review_stopped"]!.GetValue<bool>());
        Assert.Equal("/api/generate", LastModelRequest().Path);
        Assert.True(JsonNode.DeepEquals(settings, store.Settings()));
        await Collect("review");
        Assert.Empty(store.Pending(10));
    }

    [Fact]
    public async Task StopDuringCollectionReviewKeepsSourceResultsAndScheduleAnchor()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "blocking-review") });
        Assert.True(collector.Start("collect"));
        try
        {
            Assert.True(fixture.ReviewEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(202, (await Request("/api/jobs/stop-review", "POST", new { })).Status);
        }
        finally
        {
            fixture.ReviewRelease.Set();
            await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>());
        }
        var run = LatestRun();
        Assert.Equal("collect", (string)run["kind"]!);
        Assert.Equal(2, run["details"]!["added"]!.AsLong());
        Assert.Equal(1, run["details"]!["reviewed"]!.AsLong());
        Assert.Equal("partial", (string)run["status"]!);
        Assert.Single(store.Pending(10));
        Assert.False(collector.ScheduleDue(DateTimeOffset.Now));
    }

    [Fact]
    public async Task ArticleFeedbackApiAndCachedProfileInModelRequests()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig() });
        store.InsertStory(EnabledSource(), Fixtures.Entry("An SDR hardware project", Root + "/rated", null, "Practical radio hardware details."), "Radio", "2026-10-25");
        var id = store.Read(db => db.Long("SELECT id FROM stories WHERE url=?", Root + "/rated"));
        Fixtures.Sql(store, "UPDATE stories SET status='reviewed' WHERE id=?", id);
        var path = "/api/feedback/story/" + id;
        Assert.Equal(400, (await Request(path, "PUT", new { vote = true })).Status);
        Assert.Equal(400, (await Request(path, "PUT", new { vote = 1, reason = new string('x', 1001) })).Status);
        Assert.Equal(401, (await Anonymous(path, "PUT", new { vote = 1 })).Status);
        Assert.Equal(403, (await Request(path, "PUT", new { vote = 1 }, new() { ["Origin"] = "https://other.example" })).Status);
        Assert.Equal(200, (await Request(path, "PUT", new { vote = 1, reason = "I like cheap practical hardware projects" })).Status);
        Assert.Equal(1, (await Request("/api/feedback")).Body!["count"]!.AsLong());
        InsertStories("Experimental protocol", Root + "/review-feedback-", 2, "A substantive experimental technical feature is available for testing.");
        var start = RequestCount();
        await Collect("review");
        var chats = RequestsSince(start).Where(r => r.Path == "/api/chat").Select(r => r.Payload).ToList();
        Assert.Equal(3, chats.Count);
        var profileRequest = JsonNode.Parse((string)chats[0]["messages"]![1]!["content"]!)!;
        Assert.Single(profileRequest["feedback"]!.AsArray());
        Assert.Equal(1, profileRequest["feedback"]![0]!["vote"]!.AsLong());
        foreach (var payload in chats.Skip(1))
        {
            var interests = (JsonObject)JsonNode.Parse((string)payload["messages"]![1]!["content"]!)!["reader_interests"]!;
            Assert.True(interests.ContainsKey("learned_profile"));
            Assert.False(interests.ContainsKey("feedback"));
            Assert.DoesNotContain("I like cheap practical hardware projects", interests.ToJsonString());
        }
        var value = store.FeedbackState();
        Assert.Equal(value["revision"]!.AsLong(), value["profile_revision"]!.AsLong());
        store.Requeue(false);
        start = RequestCount();
        await Collect("review");
        chats = RequestsSince(start).Where(r => r.Path == "/api/chat").Select(r => r.Payload).ToList();
        Assert.NotEmpty(chats);
        Assert.All(chats, payload => Assert.False(((string)payload["messages"]![0]!["content"]!).StartsWith("Summarize article ratings")));
        Assert.Equal(200, (await Request("/api/feedback/profile", "PUT", new { profile = "Manual profile", enabled = true, auto_update = false })).Status);
        Assert.Equal("Manual profile", (string)(await Request("/api/feedback")).Body!["profile"]!);
    }

    [Fact]
    public async Task FailedFeedbackProfilePreservesExistingPreferencesAndReviews()
    {
        store.SaveSettings(new JsonObject { ["ai"] = ModelConfig(model: "bad-profile") });
        store.InsertStory(EnabledSource(), Fixtures.Entry("A technical update", Root + "/profile-failure", null, "A technical improvement is available for experimental use."), "Protocols", "2026-10-25");
        var id = StoryItems()[0]!["id"]!.AsLong();
        store.SaveFeedbackProfile("Keep an existing preference profile");
        store.RateStory(id, 1, "More useful protocols");
        await Collect("review");
        Assert.Equal("Keep an existing preference profile", (string)store.FeedbackState()["profile"]!);
        Assert.Equal("reviewed", (string)StoryItems()[0]!["status"]!);
        Assert.Contains("Feedback profile update failed", (string)LatestRun()["details"]!["errors"]![0]!);
    }

    [Fact]
    public async Task ReadMarkersArePersistentIdempotentAndGuarded()
    {
        store.InsertStory(EnabledSource(), Fixtures.Entry("Read tracking fixture", "https://example.com/read", null, "Useful details."), "Protocols", "2026-10-25");
        var story = StoryItems()[0]!;
        Assert.Null(story["read_at"]);
        var path = "/api/read/" + story["id"]!.AsLong();
        Assert.Equal(401, (await Anonymous(path, "POST", new { })).Status);
        Assert.Equal(403, (await Request(path, "POST", new { }, new() { ["Origin"] = "https://other.example" })).Status);
        var (status, result) = await Request(path, "POST", new { });
        Assert.Equal(200, status);
        Assert.False(string.IsNullOrEmpty((string?)result!["read_at"]));
        Assert.True(JsonNode.DeepEquals(result, (await Request(path, "POST", new { })).Body));
        store.Bookmark(story["id"]!.AsLong(), true);
        store.Requeue(false);
        Assert.Equal((string)result["read_at"]!, (string)new Store(store.Path).Stories(new())["items"]![0]!["read_at"]!);
        Assert.Equal(400, (await Request("/api/read/99999999", "POST", new { })).Status);
    }

    [Fact]
    public async Task ModelKeyApiScopesAuthenticationAndImmediateReplacement()
    {
        var config = ModelConfig("openrouter");
        var scope = new JsonObject { ["provider"] = config["provider"]!.DeepClone(), ["base_url"] = config["base_url"]!.DeepClone() };
        var data = With(scope, "key", "fake-first-private-key");
        Assert.False((await Request("/api/model-key/status", "POST", scope)).Body!["configured"]!.GetValue<bool>());
        Assert.Equal(401, (await Anonymous("/api/model-key", "PUT", data)).Status);
        Assert.Equal(403, (await Request("/api/model-key", "PUT", data, new() { ["Origin"] = "https://other.example" })).Status);
        JsonNode? saved;
        Assert.True(collector.TryAcquire());
        try
        {
            int status;
            (status, saved) = await Request("/api/model-key", "PUT", data);
            Assert.Equal(200, status); // Credential updates must work while a job holds the collector lock.
        }
        finally { collector.Release(); }
        Assert.True(saved!["saved"]!.GetValue<bool>());
        Assert.DoesNotContain("fake-first-private-key", saved.ToJsonString());
        Assert.Equal(200, (await Request("/api/test-model", "POST", config)).Status);
        Assert.Equal("Bearer fake-first-private-key", LastAuthorization());
        data["key"] = "fake-replacement-private-key";
        Assert.Equal(200, (await Request("/api/model-key", "PUT", data)).Status);
        Assert.Equal(200, (await Request("/api/test-model", "POST", config)).Status);
        Assert.Equal("Bearer fake-replacement-private-key", LastAuthorization());
        Assert.False((await Request("/api/model-key/status", "POST", With(scope, "provider", "compatible"))).Body!["configured"]!.GetValue<bool>());
        Assert.False((await Request("/api/model-key/status", "POST", With(scope, "base_url", Root + "/other"))).Body!["configured"]!.GetValue<bool>());
        Assert.Equal(200, (await Request("/api/test-model", "POST", With(config, "provider", "compatible"))).Status);
        Assert.Equal("", LastAuthorization());
        Assert.Equal(400, (await Request("/api/model-key/remove", "POST", scope)).Status);
        Assert.False((await Request("/api/model-key/remove", "POST", With(scope, "confirmation", "REMOVE MODEL KEY"))).Body!["configured"]!.GetValue<bool>());
        Assert.Equal(200, (await Request("/api/test-model", "POST", config)).Status);
        Assert.Equal("", LastAuthorization());
        await AssertNotServed("/model-credentials.json", "fake-replacement-private-key");
        Assert.NotEqual(200, (await Request("/api/model-key")).Status);
    }

    [Fact]
    public async Task ModelKeyEnvironmentFallbackIsVisibleAndSavedKeyWins()
    {
        var config = ModelConfig("compatible");
        host.Environment["NEWSREVIEW_MODEL_API_KEY"] = "fake-environment-key";
        var status = (await Request("/api/model-key/status", "POST", config)).Body!;
        Assert.Equal("environment", (string)status["source"]!);
        Assert.False(status["saved"]!.GetValue<bool>());
        Assert.Equal(200, (await Request("/api/test-model", "POST", config)).Status);
        Assert.Equal("Bearer fake-environment-key", LastAuthorization());
        await Request("/api/model-key", "PUT", With(config, "key", "fake-saved-key"));
        Assert.Equal(200, (await Request("/api/test-model", "POST", config)).Status);
        Assert.Equal("Bearer fake-saved-key", LastAuthorization());
        Assert.Equal("environment", (string)(await Request("/api/model-key/remove", "POST", With(config, "confirmation", "REMOVE MODEL KEY"))).Body!["source"]!);
        Assert.Equal(200, (await Request("/api/test-model", "POST", config)).Status);
        Assert.Equal("Bearer fake-environment-key", LastAuthorization());
    }

    [Fact]
    public async Task ModelKeysStayOutOfDatabaseLogsBackupsAndSurviveRestore()
    {
        var config = ModelConfig("openrouter", "key-error");
        const string secret = "fake-private-key-that-must-stay-out-of-reading-data";
        Assert.Equal(200, (await Request("/api/model-key", "PUT", With(config, "key", secret))).Status);
        Assert.Equal(200, (await Request("/api/settings", "PUT", new JsonObject { ["ai"] = config.DeepClone() })).Status);
        Assert.Equal(400, (await Request("/api/test-model", "POST", config)).Status);
        store.InsertStory(EnabledSource(), Fixtures.Entry("A useful update", Root + "/article", null, "Source details."), "Standards", "2026-10-28");
        await Collect("review");
        lock (host.Logs.Messages) Assert.DoesNotContain(secret, string.Join('\n', host.Logs.Messages));
        Assert.NotEmpty(host.Logs.Messages);
        Assert.DoesNotContain(secret, store.Overview().ToJsonString());
        Assert.Contains("[redacted]", (string)LatestRun()["details"]!["errors"]![0]!);
        var archive = host.Directory.File("keys-excluded.zip");
        Transfer.CreateArchive(store, archive);
        using (var exported = System.IO.Compression.ZipFile.OpenRead(archive))
        {
            Assert.Equal(new HashSet<string> { "newsreview.db", "manifest.json" }, exported.Entries.Select(e => e.FullName).ToHashSet());
            using var stream = exported.GetEntry("newsreview.db")!.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(memory.ToArray()));
        }
        var credentialPath = Path.Combine(host.DataPath, "model-credentials.json");
        var credentialFile = File.ReadAllBytes(credentialPath);
        store.Reset(new JsonObject { ["history"] = true });
        Transfer.RestoreArchive(store, archive);
        Assert.Equal(credentialFile, File.ReadAllBytes(credentialPath));
        Assert.Equal(secret, collector.ModelKeys.Get(config));
        Assert.Equal(secret, new ModelKeys(credentialPath, _ => null).Get(config));
    }

    [Fact]
    public async Task KeyReplacementDuringReviewAppliesToTheNextArticle()
    {
        var config = ModelConfig("openrouter", "blocking-review");
        store.SaveSettings(new JsonObject { ["ai"] = config.DeepClone() });
        await Request("/api/model-key", "PUT", With(config, "key", "fake-key-before-replacement"));
        InsertStories("Technical development", Root + "/excerpt?key=", 2, "A substantial development is available.", "Standards", "2026-10-28");
        var start = AuthorizationCount();
        Assert.True(collector.Start("review"));
        try
        {
            Assert.True(fixture.ReviewEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(200, (await Request("/api/model-key", "PUT", With(config, "key", "fake-key-after-replacement"))).Status);
        }
        finally
        {
            fixture.ReviewRelease.Set();
            await Fixtures.WaitUntil(() => !collector.Status()["running"]!.GetValue<bool>());
        }
        Assert.Equal(["Bearer fake-key-before-replacement", "Bearer fake-key-after-replacement"], AuthorizationsSince(start));
        Assert.All(StoryItems(), s => Assert.Equal("reviewed", (string)s!["status"]!));
    }

    [Fact]
    public async Task OpenRouterDisablesThinkingForSamplesProfilesAndArticleReviews()
    {
        var config = ModelConfig("openrouter", "openrouter-fixture");
        Assert.Equal(200, (await Request("/api/settings", "PUT", new JsonObject { ["ai"] = config.DeepClone() })).Status);
        Assert.Equal(200, (await Request("/api/model-key", "PUT", With(config, "key", "fake-openrouter-generation-key"))).Status);
        var authorizationStart = AuthorizationCount();
        var requestStart = RequestCount();
        Assert.Equal("openrouter", (string)store.Settings()["ai"]!["provider"]!);
        Assert.Equal(200, (await Request("/api/test-model", "POST", config)).Status);
        store.InsertStory(EnabledSource(), Fixtures.Entry("A useful technical update", Root + "/article", null, "A practical protocol development."), "Standards", "2026-10-28");
        store.RateStory(StoryItems()[0]!["id"]!.AsLong(), 1, "More practical developments");
        Assert.Equal(200, (await Request("/api/feedback/rebuild", "POST", new { })).Status);
        await Collect("review");
        var requests = RequestsSince(requestStart).Where(r => (string?)r.Payload["model"] == "openrouter-fixture").ToList();
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal("/v1/chat/completions", r.Path));
        Assert.All(requests, r => Assert.True(JsonNode.DeepEquals(new JsonObject { ["enabled"] = false }, r.Payload["reasoning"]) && !r.Payload.ContainsKey("temperature")));
        Assert.Contains(requests, r => ((string)r.Payload["messages"]![0]!["content"]!).StartsWith("Summarize article ratings"));
        Assert.Equal(Enumerable.Repeat("Bearer fake-openrouter-generation-key", 3), AuthorizationsSince(authorizationStart));
        var story = StoryItems()[0]!;
        Assert.Equal(("openrouter", "reviewed", 25L), ((string)story["provider"]!, (string)story["status"]!, story["output_tokens"]!.AsLong()));
    }

    [Fact]
    public async Task ModelTestUsesRealAdapterContract()
    {
        foreach (var provider in new[] { "ollama", "compatible", "openrouter" })
        {
            var result = await collector.Reviewer.Test(ModelConfig(provider));
            Assert.Equal("reviewed", (string)result["status"]!);
            var (path, payload) = LastModelRequest();
            Assert.False(payload["stream"]!.GetValue<bool>());
            Assert.False(payload.ContainsKey("tools"));
            if (provider == "ollama")
            {
                Assert.Equal("/api/chat", path);
                Assert.False(payload["think"]!.GetValue<bool>());
                Assert.Equal("0", payload["keep_alive"]!.ToJsonString());
                Assert.Equal(2400, payload["options"]!["num_predict"]!.AsLong());
                Assert.Equal("object", (string)payload["format"]!["type"]!);
                Assert.False(((JsonObject)payload["format"]!["properties"]!).ContainsKey("why_it_matters"));
            }
            else
            {
                Assert.Equal("/v1/chat/completions", path);
                Assert.False(payload.ContainsKey("think"));
                Assert.True(JsonNode.DeepEquals(new JsonObject { ["type"] = "json_object" }, payload["response_format"]));
                if (provider == "openrouter")
                {
                    Assert.True(JsonNode.DeepEquals(new JsonObject { ["enabled"] = false }, payload["reasoning"]));
                    Assert.False(payload.ContainsKey("temperature"));
                }
                else
                {
                    Assert.False(payload.ContainsKey("reasoning"));
                    Assert.Equal(0, payload["temperature"]!.AsLong());
                }
            }
        }
    }

    [Fact]
    public async Task ApiSearchBookmarksSettingsSourcesAndValidation()
    {
        await Collect();
        var (status, result) = await Request("/api/stories?query=HTTP");
        Assert.Equal(200, status);
        Assert.Equal(1, result!["total"]!.AsLong());
        Assert.False(((JsonObject)result["items"]![0]!).ContainsKey("article_text"));
        var storyId = result["items"]![0]!["id"]!.AsLong();
        Assert.Equal(200, (await Request($"/api/bookmarks/{storyId}", "PUT", new { bookmarked = true })).Status);
        Assert.Equal(1, (await Request("/api/stories?bookmarks=1")).Body!["total"]!.AsLong());
        Assert.Equal(400, (await Request("/api/stories?start=2026-10-16&end=2026-10-01")).Status);
        Assert.Equal(200, (await Request("/api/settings", "PUT", new JsonObject { ["ai"] = ModelConfig() })).Status);
        Assert.Equal("fixture", (string)(await Request("/api/settings")).Body!["ai"]!["model"]!);
        Assert.Equal("fixture", (string)store.Settings()["ai"]!["model"]!);
        var interests = With((JsonObject)store.Settings()["interests"]!, "guidance", "Favor radio projects.");
        Assert.Equal(200, (await Request("/api/settings", "PUT", new JsonObject { ["interests"] = interests })).Status);
        Assert.Equal("Favor radio projects.", (string)store.Settings()["interests"]!["guidance"]!);
        Assert.Equal(200, (await Request("/api/test-model", "POST", ModelConfig())).Status);
        var source = new { name = "New source", url = Root + "/new", feed_url = Root + "/feed", category = "Radio", enabled = true };
        Assert.Equal(201, (await Request("/api/sources", "POST", source)).Status);
        Assert.Equal(409, (await Request("/api/sources", "POST", source)).Status);
        Assert.Equal(400, (await Request("/api/bookmarks/1", "PUT", new { bookmarked = "yes" })).Status);
        Assert.Equal(0, (await Request("/api/stories?query=%27%20OR%201%3D1--")).Body!["total"]!.AsLong());
        await AssertNotServed("/data/newsreview.db", "SQLite format 3");
        await AssertNotServed("/../Program.cs", "WebApplication.CreateBuilder");
    }

    [Fact]
    public async Task AuthenticationOriginCookieAndRateLimit()
    {
        var anonymous = host.CreateClient(new() { HandleCookies = false });
        Assert.Equal(401, (await anonymous.Json("/api/stories")).Status);
        Assert.False((await anonymous.Json("/api/auth/status")).Body!["authenticated"]!.GetValue<bool>());
        // SignedIn() used one login attempt; 4 more are allowed this minute.
        Assert.Equal(401, (await anonymous.Json("/api/auth/login", HttpMethod.Post, new { password = "wrong" })).Status);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = HttpExtensions.Body(new { password = "correct horse" }) };
        var response = await anonymous.SendAsync(login);
        Assert.Equal(200, (int)response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", setCookie);
        var cookie = new Dictionary<string, string> { ["Cookie"] = setCookie.Split(';')[0] };
        Assert.Equal(200, (await anonymous.Json("/api/stories", headers: cookie)).Status);
        Assert.Equal(403, (await anonymous.Json("/api/jobs/start", HttpMethod.Post, new { }, new(cookie) { ["Origin"] = "https://evil.example" })).Status);
        Assert.Equal(403, (await anonymous.Json("/api/jobs/start", HttpMethod.Post, new { }, new(cookie) { ["Sec-Fetch-Site"] = "cross-site" })).Status);
        Assert.Equal(401, (await anonymous.Json("/api/auth/login", HttpMethod.Post, new { password = "wrong" })).Status);
        Assert.Equal(401, (await anonymous.Json("/api/auth/login", HttpMethod.Post, new { password = "wrong" })).Status);
        Assert.Equal(429, (await anonymous.Json("/api/auth/login", HttpMethod.Post, new { password = "wrong" })).Status);
    }

    [Fact]
    public async Task UntrustedHostIsStillUnauthorizedAndPagesHaveSecurityHeaders()
    {
        var anonymous = host.CreateClient(new() { HandleCookies = false });
        Assert.Equal(401, (await anonymous.Json("/api/overview", headers: new() { ["Host"] = "attacker.example" })).Status);
        var page = await client.GetAsync("/");
        Assert.Equal(200, (int)page.StatusCode);
        Assert.Contains("NewsReview", await page.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task MultitopicDateTimeApiAndConfirmedReset()
    {
        await Collect();
        Assert.Equal(2, (await Request("/api/stories?tag=Standards&tag=Radio")).Body!["total"]!.AsLong());
        Assert.Equal(0, (await Request("/api/stories?date_range=1%2F1%2F2000+-+1%2F2%2F2000")).Body!["total"]!.AsLong());
        Assert.Equal(200, (await Request("/api/stories?time_range=9am+-+9pm")).Status);
        Assert.Equal(400, (await Request("/api/stories?time_range=nonsense")).Status);
        var everything = new JsonObject { ["history"] = true, ["bookmarks"] = true, ["runs"] = true };
        Assert.Equal(400, (await Request("/api/fresh-start", "POST", new JsonObject { ["selections"] = everything.DeepClone() })).Status);
        Assert.Equal(2, Total(new()));
        Assert.True(collector.TryAcquire());
        try { Assert.Equal(409, (await Request("/api/fresh-start", "POST", new JsonObject { ["confirmation"] = "RESET SELECTED", ["selections"] = everything.DeepClone() })).Status); }
        finally { collector.Release(); }
        var settings = store.Settings();
        var (status, result) = await Request("/api/fresh-start", "POST", new JsonObject { ["confirmation"] = "RESET SELECTED", ["selections"] = everything.DeepClone() });
        Assert.Equal(200, status);
        Assert.Equal(2, result!["deleted_stories"]!.AsLong());
        Assert.True(JsonNode.DeepEquals(settings, store.Settings()));
        Assert.Equal(0, (await Request("/api/stories")).Body!["total"]!.AsLong());
        Assert.Empty((await Request("/api/overview")).Body!["runs"]!.AsArray());
    }

    [Fact]
    public async Task ZipApiConfirmationLockRevisionAndAtomicRestore()
    {
        await Collect();
        var export = await client.GetAsync("/api/backup");
        Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);
        var archive = await export.Content.ReadAsByteArrayAsync();
        var revision = (string)store.Overview()["revision"]!;
        async Task<(int, JsonNode?)> Upload(Dictionary<string, string>? headers = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/restore") { Content = new ByteArrayContent(archive) };
            request.Content.Headers.ContentType = new("application/zip");
            foreach (var (name, value) in headers ?? []) request.Headers.TryAddWithoutValidation(name, value);
            var response = await client.SendAsync(request);
            return ((int)response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync()));
        }
        Assert.Equal(400, (await Upload()).Item1);
        Assert.Equal(403, (await Upload(new() { ["X-NewsReview-Confirm"] = "RESTORE", ["Origin"] = "https://other.example" })).Item1);
        Assert.True(collector.TryAcquire());
        try { Assert.Equal(409, (await Upload(new() { ["X-NewsReview-Confirm"] = "RESTORE" })).Item1); }
        finally { collector.Release(); }
        Assert.Equal(400, (await Request("/api/fresh-start", "POST", new { confirmation = "wrong", selections = new { history = true } })).Status);
        Assert.Equal(200, (await Request("/api/fresh-start", "POST", new { confirmation = "RESET SELECTED", selections = new { history = true } })).Status);
        Assert.Equal(0, Total(new()));
        Assert.Equal(409, (await Upload(new() { ["X-NewsReview-Confirm"] = "RESTORE", ["X-NewsReview-Revision"] = revision })).Item1);
        var (status, result) = await Upload(new() { ["X-NewsReview-Confirm"] = "RESTORE" });
        Assert.Equal(200, status);
        Assert.True(result!["restored"]!.GetValue<bool>());
        Assert.Equal(2, Total(new()));
        Assert.Equal(0, (await Request("/api/stories?period=1%2F1%2F2000+-+1%2F2%2F2000%2C+9am+-+3pm")).Body!["total"]!.AsLong());
    }

    [Fact]
    public async Task StaleAutomaticFeedChecksWebsiteAndRetainsUsableEntries()
    {
        var config = new JsonObject
        {
            ["name"] = "Stale feed", ["url"] = Root + "/listing", ["feed_url"] = Root + "/stale-feed", ["mode"] = "auto", ["stale_days"] = 14, ["link_pattern"] = "",
        };
        var fetcher = collector.Fetcher;
        var result = await Sources.Read(fetcher, config);
        Assert.Equal("feed+page", result.Method);
        Assert.Equal(3, result.Entries.Count); // Overlapping article URLs must appear once.
        Assert.Contains(Root + "/old", result.Entries.Select(e => e.Url));
        Assert.Contains(Root + "/excerpt", result.Entries.Select(e => e.Url));
        Assert.Equal(Root + "/stale-feed", result.FeedUrl);
        var article = result.Entries.First(e => e.Url == Root + "/article");
        Assert.StartsWith(DateTime.UtcNow.Year.ToString(), article.PublishedAt);
        Assert.Equal("A useful source excerpt from the article card.", article.Excerpt);
        Assert.Contains("Website listing also checked", result.Note);
        var strict = await Sources.Read(fetcher, With(With(config, "mode", "feed"), "url", Root + "/missing"));
        Assert.Equal(("feed", ""), (strict.Method, strict.Note));
        Assert.Equal("", (await Sources.Read(fetcher, With(With(config, "feed_url", Root + "/feed"), "url", Root + "/missing"))).Note);
        var boundary = new DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("feed+page", (await Sources.Read(fetcher, config, boundary)).Method);
        Assert.Equal("", (await Sources.Read(fetcher, With(With(config, "stale_days", 15), "url", Root + "/missing"), boundary)).Note);
        var failedPage = await Sources.Read(fetcher, With(config, "url", Root + "/missing"));
        Assert.Equal((2, "feed"), (failedPage.Entries.Count, failedPage.Method));
        Assert.Contains("Retaining available feed entries", failedPage.Note);
        Assert.Equal("feed+page", (await Sources.Read(fetcher, With(With(config, "feed_url", Root + "/undated-feed"), "first_checked", "2020-01-01T00:00:00+00:00"))).Method);
        Assert.Equal("feed+page", (await Sources.Read(fetcher, With(config, "feed_url", Root + "/empty-feed"))).Method);
        var (status, preview) = await Request("/api/preview-source", "POST", With(With(config, "category", "Radio"), "enabled", true));
        Assert.Equal(200, status);
        Assert.Equal("feed+page", (string)preview!["method"]!);
        Assert.Equal(0, Total(new()));
        foreach (var existing in store.Sources().Cast<JsonObject>().ToList())
            store.SaveSource(With(With(existing, "enabled", false), "feed_url", existing["feed_url"]!.DeepClone()), existing["id"]!.AsLong());
        var sourceId = store.SaveSource(With(With(config, "category", "Radio"), "enabled", true));
        await Collect();
        Assert.Equal(2, Total(new())); // Normal lookback still excludes stale feed-only stories.
        var saved = store.Sources().Cast<JsonObject>().First(s => s["id"]!.AsLong() == sourceId);
        Assert.Equal("feed+page", (string)saved["last_method"]!);
        Assert.StartsWith(DateTime.UtcNow.Year.ToString(), (string)saved["newest_published"]!);
    }

    [Fact]
    public async Task NoFeedSitePreviewCollectionAndFallback()
    {
        foreach (var existing in store.Sources().Cast<JsonObject>().ToList()) store.SaveSource(With(existing, "enabled", false), existing["id"]!.AsLong());
        var source = new JsonObject
        {
            ["name"] = "Website source", ["url"] = Root + "/listing", ["feed_url"] = "", ["category"] = "Radio", ["enabled"] = true,
            ["mode"] = "auto", ["link_pattern"] = "", ["stale_days"] = 30,
        };
        var sourceId = store.SaveSource(source);
        var (status, preview) = await Request("/api/preview-source", "POST", source);
        Assert.Equal(200, status);
        Assert.Equal("page", (string)preview!["method"]!);
        Assert.Equal(2, preview["entries"]!.AsArray().Count);
        Assert.Equal(0, Total(new())); // Preview must not add stories.
        await Collect();
        Assert.Equal(2, Total(new()));
        JsonObject Saved() => store.Sources().Cast<JsonObject>().First(s => s["id"]!.AsLong() == sourceId);
        Assert.Equal(("page", "healthy"), ((string)Saved()["last_method"]!, (string)Saved()["health"]!["status"]!));
        store.SaveSource(With(source, "feed_url", Root + "/missing"), sourceId);
        await Collect();
        Assert.Equal("warning", (string)Saved()["health"]!["status"]!);
        Assert.Contains("Feed unavailable", (string)Saved()["collection_note"]!);
        store.SaveSource(With(With(source, "mode", "feed"), "feed_url", Root + "/missing"), sourceId);
        await Collect();
        Assert.Equal("error", (string)Saved()["health"]!["status"]!);
    }

    [Fact]
    public async Task DeletedDataFolderReturnsActionableErrorAndRestartRecovers()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(host.DataPath, recursive: true);
        foreach (var (path, method, body) in new (string, string, object?)[] { ("/api/sources", "GET", null), ("/api/overview", "GET", null), ("/api/jobs/start", "POST", new { }) })
        {
            var (status, result) = await Request(path, method, body);
            Assert.Equal(503, status);
            Assert.Equal("storage_unavailable", (string)result!["code"]!);
            Assert.Contains("restart", (string)result["error"]!);
        }
        Assert.False(File.Exists(store.Path)); // Requests must not create an empty database behind the app.
        Assert.Empty(new Store(store.Path).Sources());
    }
}
