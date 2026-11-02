using System.Text.Json.Nodes;

namespace NewsReview.Tests;

public class FeedbackTests : IDisposable
{
    readonly TempDirectory directory = new();
    readonly Store store;
    readonly long source;
    readonly long story;

    public FeedbackTests()
    {
        store = new Store(directory.File("news.db"));
        source = Fixtures.AddSource(store)["id"]!.AsLong();
        store.InsertStory(source, Fixtures.Entry("Practical SDR project", "https://example.com/sdr", null, "Useful practical SDR details."), "Radio", "2026-10-25");
        story = First(store)["id"]!.AsLong();
    }

    public void Dispose() => directory.Dispose();

    static JsonNode First(Store from) => from.Stories(new())["items"]![0]!;

    [Fact]
    public void RatingReasonEditRemoveAndRestart()
    {
        store.RateStory(story, 1, "More cheap hardware projects");
        var value = store.FeedbackState();
        Assert.Single(value["ratings"]!.AsObject());
        Assert.NotEqual(value["revision"]!.AsLong(), value["profile_revision"]!.AsLong());
        store.RateStory(story, 1, "More cheap hardware projects");
        Assert.Equal(value["revision"]!.AsLong(), store.FeedbackState()["revision"]!.AsLong());
        var reopened = First(new Store(store.Path));
        Assert.Equal(1, reopened["feedback_vote"]!.AsLong());
        Assert.Equal("More cheap hardware projects", (string)reopened["feedback_reason"]!);
        store.RateStory(story, -1, "Topic is good; article is a shopping list");
        Assert.Single(store.FeedbackState()["ratings"]!.AsObject());
        store.RateStory(story, 0, "");
        Assert.Equal(0, First(store)["feedback_vote"]!.AsLong());
        Assert.Equal("", (string)store.FeedbackState()["profile"]!);
    }

    [Fact]
    public void ProfileOnlyContextPriorityAndStaleGeneration()
    {
        store.RateStory(story, -1, "Keep this private reason out of each article review");
        var value = store.FeedbackState();
        Assert.True(store.SaveFeedbackProfile("Prefer projects over shopping lists.", revision: value["revision"]!.AsLong()));
        var interests = (JsonObject)store.Settings()["interests"]!;
        var context = Feedback.ForModel(interests);
        Assert.Equal((string)interests["guidance"]!, (string)context["guidance"]!);
        Assert.True(context.ContainsKey("learned_profile"));
        Assert.DoesNotContain("ratings", context.ToJsonString());
        Assert.DoesNotContain("private reason", context.ToJsonString());
        store.SaveFeedbackProfile("Manual preferences", enabled: false, autoUpdate: false);
        Assert.False(store.SaveFeedbackProfile("Stale generated profile", revision: value["revision"]!.AsLong()));
        Assert.False(Feedback.ForModel((JsonObject)store.Settings()["interests"]!).ContainsKey("learned_profile"));
        Assert.Equal("Manual preferences", (string)store.FeedbackState()["profile"]!);
    }

    [Fact]
    public void HistoryResetPreservesFeedbackExplicitResetClearsItAndZipRoundtrip()
    {
        store.RateStory(story, 1, "Useful project");
        store.SaveFeedbackProfile("Favor practical projects.");
        var saved = store.Settings();
        var archive = directory.File("feedback.zip");
        Transfer.CreateArchive(store, archive);
        store.Reset(new JsonObject { ["history"] = true, ["bookmarks"] = true });
        Assert.True(JsonNode.DeepEquals(saved, store.Settings()));
        store.Reset(new JsonObject { ["feedback"] = true });
        Assert.True(JsonNode.DeepEquals(Feedback.Default(), store.FeedbackState()));
        Transfer.RestoreArchive(store, archive);
        Assert.True(JsonNode.DeepEquals(saved, store.Settings()));
        Assert.Equal(1, First(store)["feedback_vote"]!.AsLong());
    }

    [Fact]
    public void InterestSavesPreserveRatingsAndInvalidBackupPreferencesReject()
    {
        store.RateStory(story, 1, "Practical");
        var interests = (JsonObject)store.Settings()["interests"]!.DeepClone();
        interests.Remove("feedback");
        interests["guidance"] = "Explicit manual guidance";
        store.SaveSettings(Validation.Settings(new JsonObject { ["interests"] = interests }, store.Settings()));
        Assert.Single(store.FeedbackState()["ratings"]!.AsObject());
        var value = (JsonObject)store.Settings()["interests"]!.DeepClone();
        value["feedback"]!["enabled"] = "yes";
        Assert.Throws<ValidationException>(() => Validation.Settings(new JsonObject { ["interests"] = value }, Defaults.Settings()));
    }

    [Fact]
    public void ProfileRefreshInputIsBoundedToLatestTenRatings()
    {
        for (var number = 0; number < 15; number++)
        {
            var url = $"https://example.com/feedback-{number}";
            store.InsertStory(source, Fixtures.Entry(number.ToString(), url, null, new string('x', 1000)), "Radio", "2026-10-25");
            var id = store.Read(db => db.Long("SELECT id FROM stories WHERE url=?", url));
            store.RateStory(id, 1, new string('r', 320));
        }
        var selected = Feedback.Examples(store.FeedbackState());
        Assert.Equal(10, selected.Count);
        Assert.Equal("14", (string)selected[0]!["headline"]!);
        Assert.Equal("5", (string)selected[^1]!["headline"]!);
        Assert.All(selected, item => Assert.True(((string)item!["reason"]!).Length <= 320 && ((string)item["summary"]!).Length <= 200));
    }

    [Fact]
    public void AllScoresUseSummaryOnly()
    {
        foreach (var score in new[] { 0, 39, 40, 100 })
        {
            var result = Reviewer.ValidateReview(Fixtures.Review(r => r["score"] = score), new HashSet<long>());
            Assert.False(result.ContainsKey("why_it_matters"));
            Assert.NotEmpty((string)result["summary"]!);
        }
    }
}
