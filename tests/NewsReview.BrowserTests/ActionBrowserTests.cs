using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace NewsReview.BrowserTests;

public class PurgeBrowserTests : BrowserTest
{
    [Fact]
    public async Task CancelConfirmBookmarkRetentionAndEmptyState()
    {
        var source = AddSource();
        foreach (var name in new[] { "Main", "Duplicate", "Saved duplicate" })
            Store.InsertStory(source, Fixtures.Entry(name, "https://example.com/" + name.Replace(' ', '-'), null, "Fixture article."), "Security", "2026-10-21");
        Store.Write(db =>
        {
            var main = db.Long("SELECT id FROM stories WHERE headline='Main'");
            db.Execute("UPDATE stories SET status='duplicate',duplicate_of=$duplicateOf WHERE headline!='Main'", ("$duplicateOf", main));
            db.Execute("UPDATE stories SET bookmarked=1 WHERE headline='Saved duplicate'");
        });
        var page = await NewPage(390, 844);
        await page.GotoAsync(Root);
        await page.Tab("Settings").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Collection & History", Exact = true }).ClickAsync();
        await page.Locator("#purge-duplicates").ClickAsync();
        await Expect(page.Locator("#purge-duplicates-count")).ToHaveTextAsync("1 duplicate story can be removed.");
        await page.Locator("#purge-duplicates-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        Assert.Equal(3, Store.Stories(new() { ShowHidden = true })["total"]!.AsLong());
        await page.Locator("#purge-duplicates").ClickAsync();
        await page.Locator("#confirm-purge-duplicates").ClickAsync();
        await Expect(page.Locator("#purge-duplicates-dialog")).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#status-message")).ToContainTextAsync("1 duplicate story removed.");
        Assert.Equal(2, Store.Stories(new() { ShowHidden = true })["total"]!.AsLong());
        Assert.Equal(1, Store.Stories(new() { Bookmarks = true })["total"]!.AsLong());
        await page.Locator("#purge-duplicates").ClickAsync();
        await Expect(page.Locator("#purge-duplicates-count")).ToHaveTextAsync("0 duplicate stories can be removed.");
        await Expect(page.Locator("#confirm-purge-duplicates")).ToBeDisabledAsync();
        AssertNoErrors();
    }
}

public class ReviewStopBrowserTests : BrowserTest
{
    readonly NewsFixture fixture = new();

    public override async Task DisposeAsync()
    {
        fixture.ReviewRelease.Set();
        await base.DisposeAsync();
        fixture.Dispose();
    }

    [Fact]
    public async Task ActiveButtonStoppingStateQueuedStoriesAndResume()
    {
        var source = AddSource();
        var ai = (JsonObject)Store.Settings()["ai"]!.DeepClone();
        ai["base_url"] = fixture.Root;
        ai["model"] = "blocking-review";
        Store.SaveSettings(new JsonObject { ["ai"] = ai });
        for (var number = 0; number < 3; number++)
            Store.InsertStory(source, Fixtures.Entry($"Experimental protocol {number}", fixture.Root + $"/unreadable?{number}", null,
                "Experimental support enables testing of a new protocol feature."), "Protocols", "2026-10-25");
        var page = await NewPage();
        await page.GotoAsync(Root);
        await page.GetByRole(AriaRole.Button, new() { Name = "Review Pending", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Stop Review", Exact = true })).ToBeEnabledAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop Review", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Stopping Review...", Exact = true })).ToBeDisabledAsync();
        fixture.ReviewRelease.Set();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Review Pending", Exact = true })).ToBeEnabledAsync();
        Assert.Equal(2, Store.Pending(10).Count);
        await Expect(page.Locator("#status-message")).ToContainTextAsync("AI review stopped", new() { Timeout = 12000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Review Pending", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Review Pending", Exact = true })).ToBeDisabledAsync();
        await page.WaitForFunctionAsync("async () => { const value = await (await fetch('/api/overview')).json(); return !value.collector.running && !value.counts.pending; }");
        Assert.Empty(Store.Pending(10));
        AssertNoErrors();
    }
}

public class FeedbackBrowserTests : BrowserTest
{
    readonly NewsFixture fixture = new();

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        fixture.Dispose();
    }

    [Fact]
    public async Task RatingsProfileGenerationEditResetAndSummaryOnlyTiles()
    {
        var source = AddSource();
        var ai = (JsonObject)Store.Settings()["ai"]!.DeepClone();
        ai["base_url"] = fixture.Root;
        ai["model"] = "fixture";
        Store.SaveSettings(new JsonObject { ["ai"] = ai.DeepClone() });
        Store.InsertStory(source, Fixtures.Entry("Practical SDR project", "https://example.com/project", null, "Useful radio hardware details."), "Radio", "2026-10-25");
        var id = Stories()[0]!["id"]!.AsLong();
        Store.ReviewResult(id, Reviewer.ValidateReview(Fixtures.Review(r => r["score"] = 25), new HashSet<long>()), ai,
            (JsonObject)Store.Settings()["interests"]!, "Text", "article");
        // Legacy explanations remain in storage for compatibility but are never displayed.
        Fixtures.Sql(Store, "UPDATE stories SET why_it_matters='Legacy significance.'");
        var page = await NewPage(390, 844);
        await page.GotoAsync(Root);
        await page.Locator(".story").WaitForAsync();
        await Expect(page.Locator(".feedback-button img")).ToHaveCountAsync(2);
        await page.WaitForFunctionAsync("[...document.querySelectorAll('.feedback-button img')].every(img => img.complete && img.naturalWidth > 0)");
        await Expect(page.Locator(".feedback-button img").First).ToHaveAttributeAsync("src", "/thumb-up.png");
        await Expect(page.Locator(".significance")).ToHaveCountAsync(0);
        ILocator Like() => page.GetByRole(AriaRole.Button, new() { Name = "Like: Practical SDR project", Exact = true });
        ILocator Dislike() => page.GetByRole(AriaRole.Button, new() { Name = "Dislike: Practical SDR project", Exact = true });
        ILocator Save(string form) => page.Locator(form).GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await Like().ClickAsync();
        await page.Locator("#feedback-reason").FillAsync("More practical low-cost radio projects");
        await Save("#feedback-form").ClickAsync();
        await Expect(Like()).ToHaveAttributeAsync("aria-pressed", "true");
        await page.ReloadAsync();
        await Expect(Like()).ToHaveAttributeAsync("aria-pressed", "true");
        await Dislike().ClickAsync();
        await Expect(page.Locator("#feedback-reason")).ToHaveValueAsync("More practical low-cost radio projects");
        await page.Locator("#feedback-reason").FillAsync("Topic is interesting, but avoid shopping lists");
        await Save("#feedback-form").ClickAsync();
        await Expect(Dislike()).ToHaveAttributeAsync("aria-pressed", "true");
        await page.Tab("Settings").ClickAsync();
        await Expect(page.Locator("#feedback-profile-status")).ToContainTextAsync("1 saved rating");
        await page.Locator("#feedback-rebuild").ClickAsync();
        await Expect(page.Locator("#feedback-profile")).ToHaveValueAsync("Favor practical SDR projects. Avoid shopping lists, while retaining interest in radio technology.");
        await page.Locator("#feedback-profile").FillAsync("My manual preference profile");
        await page.Locator("#feedback-auto").UncheckAsync();
        await Save("#feedback-profile-form").ClickAsync();
        await Expect(page.Locator("#status-message")).ToHaveTextAsync("Learned preferences saved.");
        await Expect(page.Locator("#feedback-profile")).ToHaveValueAsync("My manual preference profile");
        await page.Locator("#feedback-reset").ClickAsync();
        await page.Locator("#feedback-reset-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        Assert.Single(Store.FeedbackState()["ratings"]!.AsObject());
        await page.Locator("#feedback-reset").ClickAsync();
        await page.Locator("#feedback-confirm-reset").ClickAsync();
        await Expect(page.Locator("#feedback-profile-status")).ToContainTextAsync("0 saved ratings");
        Assert.Empty(Store.FeedbackState()["ratings"]!.AsObject());
        Assert.Equal(1, Store.Stories(new())["total"]!.AsLong());
        AssertNoErrors();
    }
}
