using System.Text.Json.Nodes;

namespace NewsReview.Tests;

public class TopicTests : IDisposable
{
    readonly TempDirectory directory = new();
    Store store;
    readonly JsonObject source;
    readonly long identifier;

    public TopicTests()
    {
        store = new Store(directory.File("news.db"));
        source = Fixtures.AddSource(store);
        store.InsertStory(source["id"]!.AsLong(), Fixtures.Entry("A useful technical development", "https://example.com/topics", null, "Source details."),
            (string)source["category"]!, "2026-10-25");
        identifier = Stories(new())[0]!["id"]!.AsLong();
        var config = store.Settings();
        store.ReviewResult(identifier, Fixtures.Review(r => { r["tags"] = new JsonArray("One-off product", "Strange synonym"); r["status"] = "reviewed"; }),
            (JsonObject)config["ai"]!, (JsonObject)config["interests"]!, "Full article details.", "article");
    }

    public void Dispose() => directory.Dispose();

    JsonArray Stories(Store.StoryQuery query) => store.Stories(query)["items"]!.AsArray();
    List<string> Topics(Store? from = null) => (from ?? store).Topics().Select(t => (string)t!).ToList();
    static List<string> Tags(JsonNode story) => story["tags"]!.AsArray().Select(t => (string)t!).ToList();
    string Category => (string)source["category"]!;

    [Fact]
    public void UpgradeKeepsEveryExistingTopicAndStoryUnchanged()
    {
        store.Bookmark(identifier, true);
        store.MarkRead(identifier);
        store.RateStory(identifier, 1, "Useful coverage");
        var before = Stories(new());
        var preferences = store.Settings();
        Fixtures.Sql(store, "DELETE FROM settings WHERE key='topics'");
        var reopened = new Store(store.Path);
        Assert.Subset(Topics(reopened).ToHashSet(), new HashSet<string> { "One-off product", "Strange synonym", Category });
        Assert.True(JsonNode.DeepEquals(before, reopened.Stories(new())["items"]));
        Assert.True(JsonNode.DeepEquals(preferences, reopened.Settings()));
        Fixtures.ChangeTopic(reopened, "delete", "One-off product");
        Assert.DoesNotContain("One-off product", Topics(new Store(store.Path)));
    }

    [Fact]
    public void AddRenameDeleteUpdateLabelsAndKeepReadingData()
    {
        Fixtures.ChangeTopic(store, "add", "One-off product");
        Fixtures.ChangeTopic(store, "add", "Strange synonym");
        store.Bookmark(identifier, true);
        store.MarkRead(identifier);
        store.RateStory(identifier, 1, "Keep this feedback");
        var before = (JsonObject)Stories(new())[0]!.DeepClone();
        var settings = store.Settings();
        var sources = store.Sources();
        var result = Fixtures.ChangeTopic(store, "rename", "One-off product", "Projects");
        Assert.Equal(1, result["updated_stories"]!.AsLong());
        Assert.Equal(["Projects", "Strange synonym"], Tags(Stories(new())[0]!));
        Assert.Equal(1, store.Stories(new() { Tags = ["Projects"] })["total"]!.AsLong());
        Assert.Equal(0, store.Stories(new() { Tags = ["One-off product"] })["total"]!.AsLong());
        Fixtures.ChangeTopic(store, "delete", "Projects");
        var after = (JsonObject)Stories(new())[0]!.DeepClone();
        Assert.Equal(["Strange synonym"], Tags(after));
        after.Remove("tags");
        before.Remove("tags");
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.True(JsonNode.DeepEquals(settings, store.Settings()));
        Assert.True(JsonNode.DeepEquals(sources, store.Sources()));
        Assert.Contains("already exists", Assert.Throws<ValidationException>(() => Fixtures.ChangeTopic(store, "add", "strange SYNONYM")).Message);
        Assert.Contains("already exists", Assert.Throws<ValidationException>(() => Fixtures.ChangeTopic(store, "rename", "Strange synonym", Category)).Message);
    }

    [Fact]
    public void SourceEditsAndNewStoriesCannotCreateTopics()
    {
        var original = Topics();
        var edited = (JsonObject)source.DeepClone();
        edited["category"] = "Unconfigured source category";
        edited["enabled"] = true;
        store.SaveSource(edited, source["id"]!.AsLong());
        store.InsertStory(source["id"]!.AsLong(), Fixtures.Entry("Another story", "https://example.com/another", null, ""), "Unconfigured source category", "2026-10-25");
        Assert.Equal(original, Topics());
        Assert.Empty(Tags(Stories(new() { Query = "Another story" })[0]!));
        Fixtures.ChangeTopic(store, "delete", Category);
        Assert.DoesNotContain(Category, Topics(new Store(store.Path)));
    }

    [Fact]
    public void EmptyListPersistsAndValidationAllowsUntaggedReviews()
    {
        foreach (var name in Topics()) Fixtures.ChangeTopic(store, "delete", name);
        Assert.Empty(Topics(new Store(store.Path)));
        Assert.Empty(store.Overview()["tags"]!.AsArray());
        Assert.Empty(Tags(Reviewer.ValidateReview(Fixtures.Review(r => r["tags"] = new JsonArray()), new HashSet<long>(), [])));
        Assert.Empty(Tags(Reviewer.ValidateReview(Fixtures.Review(r => r["tags"] = new JsonArray("Invented")), new HashSet<long>(), [])));
    }

    [Fact]
    public void VocabularyValidationDiscardsUnknownNamesKeepsValidTagsAndSummary()
    {
        Fixtures.ChangeTopic(store, "add", "My topic");
        Assert.Contains("My topic", store.Overview()["tags"]!.AsArray().Select(t => (string)t!));
        Assert.DoesNotContain("One-off product", Topics());
        var result = Reviewer.ValidateReview(Fixtures.Review(r => r["tags"] = new JsonArray(" security ", "Invented product", "Security")), new HashSet<long>(), ["Security"]);
        Assert.Equal(["Security"], Tags(result));
        result = Reviewer.ValidateReview(Fixtures.Review(r => r["tags"] = new JsonArray("Invented product tag")), new HashSet<long>(), Topics());
        Assert.Empty(Tags(result));
        Assert.Equal((string)Fixtures.Review()["summary"]!, (string)result["summary"]!);
        Assert.Equal("reviewed", (string)result["status"]!);
        Assert.Empty(Tags(Reviewer.ValidateReview(Fixtures.Review(r => r["tags"] = new JsonArray()), new HashSet<long>(), ["Security"])));
        foreach (var tags in new[] { "[\"\"]", "[123]", "\"Security\"", $"[\"{new string('x', 61)}\"]", "[\"Security\",\"Security\",\"Security\",\"Security\",\"Security\",\"Security\",\"Security\"]" })
            Assert.Contains("Invalid tags", Assert.Throws<ValidationException>(() =>
                Reviewer.ValidateReview(Fixtures.Review(r => r["tags"] = JsonNode.Parse(tags)), new HashSet<long>(), ["Security"])).Message);
        foreach (var value in new[] { "\"wrong type\"", "[\"\"]", $"[\"{new string('x', 61)}\"]", "[\"same\",\"same\"]", "[123]" })
            Assert.Throws<ValidationException>(() => Validation.TopicNames(JsonNode.Parse(value)));
    }

    [Fact]
    public void ZipPreservesOwnedListAndOlderBackupsImportAllLabels()
    {
        Fixtures.ChangeTopic(store, "add", "My custom topic");
        var archive = directory.File("owned.zip");
        Transfer.CreateArchive(store, archive);
        var saved = Topics();
        Fixtures.ChangeTopic(store, "delete", "My custom topic");
        Transfer.RestoreArchive(store, archive);
        Assert.Equal(saved, Topics());
        Fixtures.Sql(store, "DELETE FROM settings WHERE key='topics'");
        var legacy = directory.File("legacy.zip");
        Transfer.CreateArchive(store, legacy);
        store = new Store(store.Path);
        Fixtures.ChangeTopic(store, "add", "Should be replaced by restore");
        Transfer.RestoreArchive(store, legacy);
        Assert.Subset(Topics().ToHashSet(), new HashSet<string> { "One-off product", "Strange synonym" });
        Assert.DoesNotContain("Should be replaced by restore", Topics());
    }

    [Fact]
    public void ZipRejectsInvalidTopicList()
    {
        Fixtures.Sql(store, "UPDATE settings SET value='\"invalid\"' WHERE key='topics'");
        var archive = directory.File("invalid.zip");
        Transfer.CreateArchive(store, archive);
        Assert.Contains("Topics must be a list", Assert.Throws<ValidationException>(() => Transfer.RestoreArchive(store, archive)).Message);
    }
}
