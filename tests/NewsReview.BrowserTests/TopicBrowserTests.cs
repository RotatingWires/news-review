using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace NewsReview.BrowserTests;

public class TopicBrowserTests : BrowserTest
{
    List<string> Topics() => Store.Topics().Select(t => (string)t!).ToList();

    [Fact]
    public async Task PreservedLabelsAddDuplicateRenameDeleteReloadAndMobileDialogs()
    {
        var source = Fixtures.AddSource(Store);
        var category = (string)source["category"]!;
        for (var number = 0; number < 2; number++)
            Store.InsertStory(source["id"]!.AsLong(), Fixtures.Entry($"Useful development {number}", $"https://example.com/topics-{number}", null, "Source details."), category, "2026-10-25");
        var first = Stories()[0]!["id"]!.AsLong();
        var settings = Store.Settings();
        Store.ReviewResult(first, Fixtures.Review(r => { r["tags"] = new JsonArray(category, "Existing custom topic"); r["status"] = "reviewed"; }),
            (JsonObject)settings["ai"]!, (JsonObject)settings["interests"]!, "Article text.", "article");
        Store.Bookmark(first, true);
        Store.MarkRead(first);
        // Recreate the topic list as an upgrade would: import every existing label once.
        Store.Write(db =>
        {
            db.Execute("DELETE FROM settings WHERE key='topics'");
            Store.InitializeTopics(db);
        });
        var original = Topics();
        var page = await NewPage(1440, 950);
        await page.GotoAsync(Root);
        await page.Locator("#topic-button").ClickAsync();
        await page.Locator("#topic-options").GetByRole(AriaRole.Checkbox, new() { Name = category, Exact = true }).CheckAsync();
        await page.Locator("#topic-button").PressAsync("Escape");
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        async Task OpenTopics()
        {
            await page.Tab("Settings").ClickAsync();
            await Button("Topics").ClickAsync();
            await Expect(page.Locator("#panel-topics")).ToBeVisibleAsync();
        }
        // Line edits become explicit renames/removals; moving a line never removes it.
        var edits = await page.EvaluateAsync<string>(@"async () => {
            const { topicEdits } = await import('/settings.js');
            return JSON.stringify([
                topicEdits(['A', 'B', 'C'], ['C', 'A', 'B']),
                topicEdits(['A', 'B', 'C'], ['A', 'New', 'B2', 'C']),
                topicEdits(['ai', 'Linux'], ['AI', 'Linux']),
                topicEdits(['A', 'B', 'C'], ['A', 'C', 'D']),
                topicEdits(['A', 'B', 'C'], ['A', 'X', 'Y']),
            ]);
        }");
        Assert.Equal("""[{"renames":{},"removed":[]},{"renames":{"B":"New"},"removed":[]},{"renames":{"ai":"AI"},"removed":[]},{"renames":{},"removed":["B"]},{"renames":{"B":"X","C":"Y"},"removed":[]}]""",
            edits);
        await OpenTopics();
        var text = page.Locator("#topics-text");
        string Lines(IEnumerable<string> names) => string.Join("\n", names);
        await Expect(text).ToHaveValueAsync(Lines(original));
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-topic-editor.png") });
        var save = page.Locator("#topics-form").GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await text.FillAsync(Lines([.. original, "  SDR Projects  ", ""]));
        await save.ClickAsync();
        await Fixtures.WaitUntil(() => Topics().Contains("SDR Projects"));
        await Expect(page.Locator("#status-message")).ToContainTextAsync("Topics saved");
        Assert.Subset(Topics().ToHashSet(), original.ToHashSet());
        await text.FillAsync(Lines([.. Topics(), "sdr PROJECTS"]));
        await save.ClickAsync();
        await Expect(page.Locator("#status-message")).ToContainTextAsync("already exists");
        Assert.DoesNotContain("sdr PROJECTS", Topics());

        // Editing a line in place renames it: saved labels and the selected feed filter follow.
        await text.FillAsync(Lines(Topics().Select(name => name == category ? "Device Engineering" : name)));
        await save.ClickAsync();
        await Fixtures.WaitUntil(() => Topics().Contains("Device Engineering"));
        await Expect(page.Locator("#topic-remove-dialog")).Not.ToBeVisibleAsync();
        Assert.DoesNotContain(category, Topics());
        // Default category suggestions follow the managed list; the source keeps its own stored category.
        await Button("Sources").ClickAsync();
        await Button("Edit " + (string)source["name"]!).ClickAsync();
        await Expect(page.Locator("#source-category")).ToHaveValueAsync(category);
        await Button("Choose default category").ClickAsync();
        Assert.Equal(Topics(), await page.Locator("#source-dialog .editable-dropdown .dropdown-option span:not(.dropdown-check)").AllTextContentsAsync());
        await page.Locator("#source-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await page.Tab("Daily Feed").ClickAsync();
        await Expect(page.Locator(".story")).ToHaveCountAsync(2);
        await page.Locator("#topic-button").ClickAsync();
        await Expect(page.Locator("#topic-options").GetByRole(AriaRole.Checkbox, new() { Name = "Device Engineering", Exact = true })).ToBeCheckedAsync();
        await page.Locator("#topic-button").PressAsync("Escape");
        Assert.Equal(category, (string)Store.Sources()[0]!["category"]!);
        Assert.Equal(1, Store.Stories(new() { Bookmarks = true })["total"]!.AsLong());
        await Expect(page.Locator(".read-label")).ToHaveCountAsync(1);

        // Deleting a line asks first; Cancel keeps everything.
        await OpenTopics();
        await text.FillAsync(Lines(Topics().Where(name => name != "Device Engineering")));
        await save.ClickAsync();
        var removal = page.Locator("#topic-remove-dialog");
        await Expect(removal).ToBeVisibleAsync();
        await Expect(page.Locator("#topic-remove-names li")).ToHaveTextAsync(["Device Engineering"]);
        await removal.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        Assert.Contains("Device Engineering", Topics());
        await save.ClickAsync();
        await page.SetViewportSizeAsync(390, 844);
        await Expect(removal.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true })).ToBeInViewportAsync();
        // The feed reloads only after the saved list has been rendered back into the text box.
        await page.RunAndWaitForRequestAsync(() => removal.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync(),
            request => request.Url.Contains("/api/stories"));
        await Expect(removal).Not.ToBeVisibleAsync();
        await Expect(text).ToHaveValueAsync(Lines(Topics()));
        Assert.Contains("Existing custom topic", Topics());
        Assert.Equal(2, Store.Stories(new())["total"]!.AsLong());
        Assert.Equal(1, Store.Stories(new() { Bookmarks = true })["total"]!.AsLong());

        // Unsaved edits survive switching tabs; a reload shows the saved list.
        await text.FillAsync(Lines([.. Topics(), "Radio, scanners, antennas and homebrew projects"]));
        await Button("Sources").ClickAsync();
        await Button("Topics").ClickAsync();
        await Expect(text).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("homebrew projects$"));
        await save.ClickAsync();
        await Fixtures.WaitUntil(() => Topics().Contains("Radio, scanners, antennas and homebrew projects"));
        await page.ReloadAsync();
        await OpenTopics();
        await Expect(text).ToHaveValueAsync(Lines(Topics()));
        Assert.Contains("Radio, scanners, antennas and homebrew projects", Topics());
        Assert.Contains("SDR Projects", Topics());
        Assert.True(await page.Locator("body").EvaluateAsync<bool>("el => el.scrollWidth <= innerWidth"));
        AssertNoErrors();
    }
}
