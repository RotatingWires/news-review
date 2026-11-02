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
            await Button("Settings").ClickAsync();
            await Button("Topics").ClickAsync();
            await Expect(page.Locator("#panel-topics")).ToBeVisibleAsync();
        }
        await OpenTopics();
        await Expect(page.Locator("#topic-rows .topic-row")).ToHaveCountAsync(original.Count);
        await Expect(page.Locator("#topic-rows")).ToContainTextAsync("Existing custom topic");
        var editor = page.Locator("#topic-edit-dialog");
        ILocator EditorButton(string name) => editor.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        await page.Locator("#add-topic").ClickAsync();
        await EditorButton("Cancel").ClickAsync();
        Assert.Equal(original, Topics());
        await page.Locator("#add-topic").ClickAsync();
        await page.Locator("#topic-name-input").FillAsync("SDR Projects");
        await EditorButton("Save").ClickAsync();
        await Expect(editor).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#topic-rows")).ToContainTextAsync("SDR Projects");
        Assert.Subset(Topics().ToHashSet(), original.ToHashSet());
        await page.Locator("#add-topic").ClickAsync();
        await page.Locator("#topic-name-input").FillAsync("sdr PROJECTS");
        await EditorButton("Save").ClickAsync();
        await Expect(page.Locator("#status-message")).ToContainTextAsync("already exists");
        await Expect(editor).ToBeVisibleAsync();
        await EditorButton("Cancel").ClickAsync();

        await Button("Rename topic " + category).ClickAsync();
        await page.Locator("#topic-name-input").FillAsync("Device Engineering");
        await EditorButton("Save").ClickAsync();
        await Expect(editor).Not.ToBeVisibleAsync();
        await Button("Daily Feed").ClickAsync();
        await Expect(page.Locator(".story")).ToHaveCountAsync(2);
        await page.Locator("#topic-button").ClickAsync();
        await Expect(page.Locator("#topic-options").GetByRole(AriaRole.Checkbox, new() { Name = "Device Engineering", Exact = true })).ToBeCheckedAsync();
        await page.Locator("#topic-button").PressAsync("Escape");
        Assert.Equal(category, (string)Store.Sources()[0]!["category"]!);
        Assert.Equal(1, Store.Stories(new() { Bookmarks = true })["total"]!.AsLong());
        await Expect(page.Locator(".read-label")).ToHaveCountAsync(1);

        await OpenTopics();
        await Button("Delete topic Device Engineering").ClickAsync();
        var deletion = page.Locator("#topic-delete-dialog");
        await deletion.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        Assert.Contains("Device Engineering", Topics());
        await Button("Delete topic Device Engineering").ClickAsync();
        await page.SetViewportSizeAsync(390, 844);
        await Expect(deletion.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true })).ToBeInViewportAsync();
        await deletion.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(deletion).Not.ToBeVisibleAsync();
        Assert.DoesNotContain("Device Engineering", Topics());
        Assert.Contains("Existing custom topic", Topics());
        Assert.Equal(2, Store.Stories(new())["total"]!.AsLong());
        Assert.Equal(1, Store.Stories(new() { Bookmarks = true })["total"]!.AsLong());
        await page.ReloadAsync();
        await OpenTopics();
        await Expect(page.Locator("#topic-rows")).ToContainTextAsync("Existing custom topic");
        await Expect(page.Locator("#topic-rows")).ToContainTextAsync("SDR Projects");
        await Button("Rename topic SDR Projects").ClickAsync();
        await page.Locator("#topic-name-input").FillAsync("Radio, scanners, antennas and homebrew projects");
        await EditorButton("Save").ClickAsync();
        await Expect(editor).Not.ToBeVisibleAsync();
        await Expect(page.Locator("#topic-rows")).ToContainTextAsync("Radio, scanners, antennas and homebrew projects");
        Assert.True(await page.Locator("body").EvaluateAsync<bool>("el => el.scrollWidth <= innerWidth"));
        AssertNoErrors();
    }
}
