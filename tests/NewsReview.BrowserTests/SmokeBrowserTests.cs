using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace NewsReview.BrowserTests;

/// <summary>Broad QA of the whole interface: feeds, search, filters, settings, sources, AI, backups, resets, and layouts.</summary>
public class SmokeBrowserTests : BrowserTest
{
    readonly NewsFixture fixture = new();

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        fixture.Dispose();
    }

    static readonly (string Headline, string Category, string Summary)[] Samples =
    [
        ("Experimental HTTP method reaches a browser development build", "Protocols", "A proposed HTTP method is now available for developer testing. Stable browser releases do not yet include it."),
        ("Open-source SDR decoder adds new P25 analysis tools", "Radio & SDR", "The project introduces control-channel analysis and improved diagnostic output for compatible receivers."),
        ("FreePBX update tightens module authentication", "PBX & VoIP", "A module update changes how administrative requests are authenticated. The release notes explain the affected configurations."),
        ("Image compression project publishes experimental encoder", "Formats", "A research project released an encoder for a proposed image format, along with code and test material."),
    ];

    [Fact]
    public async Task WholeInterface()
    {
        var source = AddSource();
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yesterday = DateTime.Now.AddDays(-1);
        int[] scores = [90, 60, 25, 75];
        for (var index = 0; index < Samples.Length; index++)
        {
            var (headline, category, summary) = Samples[index];
            var tags = new[] { category }.Concat(Enumerable.Range(0, 5).Select(n => $"Topic {index}-{n}")).ToList();
            foreach (var name in tags)
                if (!Store.Topics().Any(t => (string)t! == name)) Fixtures.ChangeTopic(Store, "add", name);
            Store.InsertStory(source, Fixtures.Entry(headline, $"https://example.com/{index}", null, summary), category, today);
            var id = Store.Read(db => db.Long("SELECT id FROM stories WHERE url=$url", ("$url", $"https://example.com/{index}")));
            var review = Reviewer.ValidateReview(Fixtures.Review(r =>
            {
                r["summary"] = summary;
                r["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray());
                r["score"] = scores[index];
            }), new HashSet<long>());
            var ai = (JsonObject)Store.Settings()["ai"]!.DeepClone();
            ai["model"] = "fixture-model";
            Store.ReviewResult(id, review, ai, (JsonObject)Store.Settings()["interests"]!, summary, "article");
        }
        Store.InsertStory(source, Fixtures.Entry("Yesterday's protocol update", "https://example.com/yesterday",
            yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T12:00:00+00:00", "A source excerpt from the previous day."),
            "Protocols", yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var page = await NewPage(1440, 1050, timezone: "America/New_York");
        var apiRequests = new List<string>();
        page.Request += (_, request) => { if (request.Method == "GET") lock (apiRequests) apiRequests.Add(request.Url[(request.Url.LastIndexOf('/') + 1)..]); };
        ILocator Button(string name, bool exact = true) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = exact });
        ILocator Stories() => page.Locator(".story");
        async Task ChooseSort(string label)
        {
            await Button("Sort stories").ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = label, Exact = true }).ClickAsync();
        }
        async Task ClickRowEdge(ILocator row, double y)
        {
            var box = await Box(row);
            await row.ClickAsync(new() { Position = new() { X = (float)box.Width - 4, Y = (float)y } });
        }
        async Task<string> Style(string selector, string property) => await page.Locator(selector).First.EvaluateAsync<string>($"el => getComputedStyle(el).{property}");

        await page.GotoAsync(Root);
        await page.WaitForSelectorAsync(".story");
        Assert.Equal(4, await Stories().CountAsync());
        await Expect(page.Locator(".significance")).ToHaveCountAsync(0);
        await Expect(page.Locator(".relevance-indicator.relevance-high")).ToHaveCountAsync(2);
        await Expect(page.Locator(".relevance-indicator.relevance-medium")).ToHaveCountAsync(1);
        await Expect(page.Locator(".relevance-indicator.relevance-low")).ToHaveCountAsync(1);
        await Expect(page.Locator("#relevanceKey .relevance-key-item")).ToHaveCountAsync(4);
        await ChooseSort("Rank: lowest first");
        await Expect(page.Locator(".story h3").First).ToContainTextAsync("FreePBX");
        await ChooseSort("Published: newest first");
        await Expect(page.Locator(".story h3").First).ToContainTextAsync("Image compression");
        await ChooseSort("Published: oldest first");
        await Expect(page.Locator(".story h3").First).ToContainTextAsync("Experimental HTTP");
        await ChooseSort("Rank: lowest first");
        await page.ReloadAsync();
        await Expect(Button("Sort stories")).ToContainTextAsync("Rank: lowest first");
        await Expect(page.Locator(".story h3").First).ToContainTextAsync("FreePBX");
        var identifiers = Store.Read(db => db.Rows("SELECT headline,id FROM stories")).ToDictionary(r => (string)r["headline"]!, r => r["id"]!.AsLong());
        foreach (var name in new[] { Samples[0].Headline, Samples[2].Headline }) Store.Bookmark(identifiers[name], true);
        await page.Tab("Bookmarks").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(2);
        await Expect(page.Locator(".story h3").First).ToContainTextAsync("FreePBX");
        await ChooseSort("Published: oldest first");
        await Expect(page.Locator(".story h3").First).ToContainTextAsync("Experimental HTTP");
        foreach (var name in new[] { Samples[0].Headline, Samples[2].Headline }) Store.Bookmark(identifiers[name], false);
        await page.Tab("Daily Feed").ClickAsync();
        await ChooseSort("Rank: highest first");
        await Expect(Stories()).ToHaveCountAsync(4);
        await Expect(page.Locator(".story h3").First).ToContainTextAsync("Experimental HTTP");
        await page.Locator("#topicButton").ClickAsync();
        await Expect(page.Locator("#topicLabel")).ToHaveTextAsync("Filter...");
        await ClickRowEdge(page.Locator("input[name=\"relevance\"][value=\"low\"]").Locator(".."), 12);
        await Expect(page.Locator("input[name=\"relevance\"][value=\"low\"]")).Not.ToBeCheckedAsync();
        var hiddenRow = page.Locator("#showHidden").Locator("..");
        await ClickRowEdge(hiddenRow, 12);
        await Expect(page.Locator("#showHidden")).ToBeCheckedAsync();
        await ClickRowEdge(hiddenRow, 12);
        await Expect(page.Locator("#showHidden")).Not.ToBeCheckedAsync();
        await Expect(Stories()).ToHaveCountAsync(3);
        await page.Locator("input[name=\"relevance\"][value=\"high\"]").UncheckAsync();
        await Expect(Stories()).ToHaveCountAsync(1);
        await page.Locator("input[name=\"relevance\"][value=\"medium\"]").UncheckAsync();
        await Expect(Stories()).ToHaveCountAsync(0);
        await page.Locator("#topicButton").PressAsync("Escape");
        await Button("Clear Search").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(4);
        var indicator = await Box(page.Locator(".relevance-indicator").First);
        var bookmark = await Box(page.Locator(".bookmark").First);
        Assert.True(indicator.Width == indicator.Height && indicator.Height == bookmark.Width && bookmark.Width == bookmark.Height);
        Assert.True(bookmark.X + bookmark.Width < indicator.X);
        var countBox = await Box(page.Locator("#storyCount"));
        Assert.True((await Box(page.Locator("#relevanceKey"))).Y >= countBox.Y + countBox.Height);
        await Expect(page.Locator(".relevance-indicator").First).ToHaveAttributeAsync("title", "Highly relevant: AI interest score 90/100");
        await Expect(page.Tab("Daily Feed")).ToHaveAttributeAsync("aria-selected", "true");
        Assert.Equal("14px", await Style(".titlebar-caption", "fontSize"));
        Assert.Equal("14px", await Style("#storyCount", "fontSize"));
        await Expect(page.Locator("#resultsHeading")).ToHaveTextAsync("Today's Edition");
        var heading = await Box(page.Locator("#resultsHeading"));
        countBox = await Box(page.Locator("#storyCount"));
        Assert.True(countBox.Y >= heading.Y + heading.Height);
        Assert.Equal(heading.X, countBox.X);
        var titleButtons = await page.Locator(".title-buttons > span").EvaluateAllAsync<double[][]>("els => els.map(el => { const r = el.getBoundingClientRect(); return [r.top, r.height]; })");
        Assert.Equal(3, titleButtons.Length);
        Assert.All(titleButtons, box => Assert.Equal(titleButtons[0], box));
        Assert.Equal("none", await Style("body", "userSelect"));
        Assert.Equal("text", await Style(".story", "userSelect"));
        Assert.Equal("none", await Style(".bookmark", "userSelect"));
        Assert.Equal("text", await Style("#guidance", "userSelect"));
        Assert.Equal(1, await page.Locator("#topicButton .dropdown-arrow").CountAsync());
        await page.Keyboard.PressAsync("Control+a");
        var selected = await page.EvaluateAsync<string>("getSelection().toString()");
        Assert.Contains("Experimental HTTP method", selected);
        Assert.DoesNotContain("Daily Feed", selected);
        Assert.DoesNotContain("☆", selected);
        await page.EvaluateAsync("getSelection().removeAllRanges()");
        Assert.Equal(0, await page.Locator("#collector-status").CountAsync());
        Assert.Equal(1, await page.Locator("footer .status-dot").CountAsync());
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-desktop.png"), FullPage = true });

        await Button("Help").ClickAsync();
        await Expect(page.Locator("#helpDialog")).ToBeVisibleAsync();
        await Expect(page.Locator("#helpDialog .help-content > p").Last).ToHaveTextAsync(new Regex(@"^about\.lehighradio\.comNewsReview v\d+\.\d+\.\d+$"));
        const string positionsScript = "el => [el.querySelector('.dialog-heading').getBoundingClientRect().top, el.querySelector('.dialog-actions').getBoundingClientRect().bottom]";
        var positions = await page.Locator("#helpDialog").EvaluateAsync<double[]>(positionsScript);
        await page.Locator("#helpDialog .dialog-body").EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
        Assert.Equal(positions, await page.Locator("#helpDialog").EvaluateAsync<double[]>(positionsScript));
        await page.Locator("#helpDialog .dialog-actions button").ClickAsync();
        await page.Tab("Settings").ClickAsync();
        await Button("Collection & History").ClickAsync();
        await page.Locator("#scheduleInterval").FillAsync("3");
        await page.Locator("#collectionForm button[type=submit]").ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToHaveTextAsync("Collection settings saved.");
        Assert.Equal(3, Store.Settings()["collection"]!["interval_hours"]!.AsLong());
        await page.MenuAsync("File", "Import/Export...");
        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator("#exportBackup").ClickAsync());
        var backupPath = host.Directory.File("browser-export.zip");
        await download.SaveAsAsync(backupPath);
        var chooser = await page.RunAndWaitForFileChooserAsync(() => page.Locator("#chooseBackup").ClickAsync());
        await chooser.SetFilesAsync(backupPath);
        // Like the other apps, choosing the file closes Import/Export and asks for confirmation, Cancel first.
        await Expect(page.Locator("#restoreDialog .dialog-actions button")).ToHaveTextAsync(new[] { "Cancel", "Restore" });
        await page.Locator("#restoreDialog").GetByRole(AriaRole.Button, new() { Name = "Restore", Exact = true }).ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Restored", new() { Timeout = 15000 });
        await page.Tab("Daily Feed").ClickAsync();
        var bookmarkStates = new List<(string Icon, string Status)>();
        async Task InspectBookmarkRequest(IRoute route)
        {
            bookmarkStates.Add((await page.Locator(".bookmark").First.InnerTextAsync(), await page.Locator("#statusMessage").InnerTextAsync()));
            await route.ContinueAsync();
        }
        await page.RouteAsync("**/api/bookmarks/*", InspectBookmarkRequest);
        await page.Locator(".bookmark").First.ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Saved to bookmarks");
        await page.UnrouteAsync("**/api/bookmarks/*", InspectBookmarkRequest);
        Assert.Equal("☆", bookmarkStates.Single().Icon);
        Assert.Contains("Saving bookmark", bookmarkStates.Single().Status);
        Assert.Equal(0, await page.Locator(".read-link").CountAsync());
        await page.Tab("Bookmarks").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(1);
        Assert.Contains("HTTP", await page.Locator(".story h3").InnerTextAsync());
        await page.Tab("Daily Feed").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(4);
        await page.GetByLabel("Previous day", new() { Exact = true }).ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(1);
        Assert.Contains("Yesterday", await page.Locator(".story h3").InnerTextAsync());
        await Expect(page.Locator(".relevance-indicator.relevance-pending")).ToHaveCountAsync(1);
        await Button("Today").ClickAsync();
        await page.GetByLabel("Search history").FillAsync("Example News");
        await Button("Search").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(4);
        await Button("Clear Search", exact: false).ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(4);
        await page.GetByLabel("Search history").FillAsync("FreePBX");
        await page.Locator("#resultsHeading").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(1);
        Assert.Contains("FreePBX", await page.Locator(".story h3").InnerTextAsync());
        await page.GetByLabel("Search history").FillAsync("");
        await page.Locator("#resultsHeading").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(4);

        Assert.True((await Box(page.Locator("#topicButton"))).Width <= 170);
        await page.Locator("#topicButton").PressAsync("ArrowDown");
        await Expect(page.Locator("#showHidden")).ToBeFocusedAsync();
        await page.Locator("#showHidden").PressAsync("Escape");
        await Expect(page.Locator("#topicMenu")).ToBeHiddenAsync();
        await page.Locator("#topicButton").ClickAsync();
        var menu = await Box(page.Locator("#topicMenu"));
        Assert.True(menu.Width <= 310 && menu.Height <= 460);
        Assert.True(await page.Locator("#topicOptions").EvaluateAsync<bool>("el => el.scrollHeight <= el.clientHeight"));
        Assert.True(await page.Locator("#topicOptions").EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"));
        await page.Locator("#topicOptions").EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
        await Expect(page.Locator("#topicAll")).ToBeVisibleAsync();
        await page.Locator("#topicOptions").EvaluateAsync("el => { el.scrollTop = 0; }");
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-topics.png") });
        ILocator Checkbox(string name) => page.GetByRole(AriaRole.Checkbox, new() { Name = name, Exact = true });
        await page.Locator("#topicOptions .topic-name").GetByText("Protocols", new() { Exact = true }).ClickAsync();
        await Expect(Checkbox("Protocols")).ToBeCheckedAsync();
        await Expect(Stories()).ToHaveCountAsync(1);
        await ClickRowEdge(page.Locator("#topicOptions .topic-row").Filter(new() { HasText = "Protocols" }), 10);
        await Expect(Checkbox("Protocols")).Not.ToBeCheckedAsync();
        await Expect(Stories()).ToHaveCountAsync(4);
        await Checkbox("Protocols").CheckAsync();
        await Expect(Stories()).ToHaveCountAsync(1);
        await page.Locator("#topicOptions .topic-name").GetByText("Radio & SDR", new() { Exact = true }).ClickAsync();
        await Expect(Checkbox("Radio & SDR")).ToBeCheckedAsync();
        await Expect(Stories()).ToHaveCountAsync(2);
        await page.Locator("#topicAll").ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(4);
        await page.Locator("#topicButton").ClickAsync();
        var now = DateTime.Now;
        var dateText = $"{yesterday.Month}/{yesterday.Day}/{yesterday.Year} - {now.Month}/{now.Day}/{now.Year}";
        Store.Write(db =>
        {
            db.Execute("UPDATE stories SET discovered_at='2026-10-16T02:00:00+00:00'");
            db.Execute("UPDATE stories SET published_at='2026-10-15T15:00:00+00:00', discovered_at='2026-10-16T02:00:00+00:00' WHERE url='https://example.com/0'");
            Store.BackfillStoryTimes(db);
        });
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        var range = page.GetByLabel("Date/time range", new() { Exact = true });
        await range.FillAsync("10am - 12pm");
        await range.PressAsync("Enter");
        await Expect(Stories()).ToHaveCountAsync(0);
        await range.FillAsync("10/15/26 - 10/15/26, 10am - 12pm");
        await range.PressAsync("Enter");
        await Expect(Stories()).ToHaveCountAsync(1);
        await Expect(page.Locator(".story h3")).ToContainTextAsync("Experimental HTTP method");
        Store.Write(db =>
        {
            db.Execute("UPDATE stories SET published_at=NULL, discovered_at=$discoveredAt WHERE url != 'https://example.com/yesterday'", ("$discoveredAt", today + "T12:00:00+00:00"));
            Store.BackfillStoryTimes(db);
        });
        await range.FillAsync(dateText);
        await range.PressAsync("Enter");
        await Expect(Stories()).ToHaveCountAsync(5);
        await range.FillAsync(dateText + ", 12am - 11:59pm");
        await range.PressAsync("Enter");
        await Expect(Stories()).ToHaveCountAsync(5);
        await Button("Clear Search", exact: false).ClickAsync();
        await Expect(Stories()).ToHaveCountAsync(4);

        await page.Tab("Settings").ClickAsync();
        await Button("Interests").ClickAsync();
        await page.WaitForSelectorAsync("#guidance");
        await page.Locator("#guidance").FillAsync("Favor RF projects and protocol changes.");
        await page.Locator("#interestsForm").GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Interests saved");
        Assert.Equal("Favor RF projects and protocol changes.", (string)Store.Settings()["interests"]!["guidance"]!);
        await Button("Sources").ClickAsync();
        await Button("+ Add Source").ClickAsync();
        Assert.Equal(["Cancel", "Save"], await page.Locator("#sourceDialog .dialog-actions button").AllTextContentsAsync());
        var categoryBox = await Box(page.Locator("#sourceCategory"));
        Assert.True(categoryBox.Width <= 225);
        var arrowBox = await Box(page.Locator(".editable-dropdown .dropdown-arrow"));
        Assert.True(Math.Abs(categoryBox.Y + categoryBox.Height / 2 - (arrowBox.Y + arrowBox.Height / 2)) < 1);
        await Expect(page.Locator("#sourceCategory")).ToHaveValueAsync((string)Store.Topics()[0]!);
        await Button("Choose default category").ClickAsync();
        Assert.Equal(Store.Topics().Select(t => (string)t!), await page.Locator("#sourceDialog .editable-dropdown .dropdown-option span:not(.dropdown-check)").AllTextContentsAsync());
        await page.GetByRole(AriaRole.Option, new() { Name = "Protocols", Exact = true }).ClickAsync();
        await Expect(page.Locator("#sourceCategory")).ToHaveValueAsync("Protocols");
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-source-dialog.png") });
        await page.Locator("#sourceCategory").FillAsync("Custom radio category");
        await Button("Choose default category").ClickAsync();
        await Button("Choose default category").PressAsync("Escape");
        await Expect(page.Locator("#sourceDialog")).ToBeVisibleAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Fixture source");
        await page.GetByLabel("Website URL", new() { Exact = true }).FillAsync(fixture.Root + "/");
        await Button("Find RSS/Atom Feed", exact: false).ClickAsync();
        await Expect(page.Locator("#sourceFeed")).ToHaveValueAsync(new Regex("/feed$"));
        await page.Locator("#sourceFeed").FillAsync(fixture.Root + "/stale-feed");
        await Button("Preview Stories").ClickAsync();
        await Expect(page.Locator("#sourcePreview")).ToContainTextAsync("Website fallback could not be read");
        await page.Locator("#sourceUrl").FillAsync(fixture.Root + "/listing");
        await Button("Preview Stories").ClickAsync();
        await Expect(page.Locator("#sourcePreview")).ToContainTextAsync("RSS/Atom + website");
        await page.Locator("#sourceFeed").FillAsync(fixture.Root + "/feed");
        await page.Locator("#sourceUrl").FillAsync(fixture.Root);
        await Button("Save").ClickAsync();
        await Expect(page.Locator(".source-row")).ToHaveCountAsync(2);
        await Button("+ Add Source").ClickAsync();
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Website without RSS");
        await page.GetByLabel("Website URL", new() { Exact = true }).FillAsync(fixture.Root + "/listing");
        await Button("Collection method").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Website page — no RSS needed", Exact = true }).ClickAsync();
        await Button("Preview Stories").ClickAsync();
        await Expect(page.Locator("#sourcePreview")).ToContainTextAsync("Website page");
        await Expect(page.Locator("#sourcePreview a")).ToHaveCountAsync(2);
        await Button("Save").ClickAsync();
        await Expect(page.Locator(".source-row")).ToHaveCountAsync(3);
        await Button("AI").ClickAsync();
        await Button("Server type").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Compatible chat server (e.g. LM Studio)", Exact = true }).ClickAsync();
        await Expect(page.Locator("#aiProvider")).ToHaveValueAsync("compatible");
        await Button("Server type").PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Home");
        await page.Keyboard.PressAsync("Enter");
        await Expect(page.Locator("#aiProvider")).ToHaveValueAsync("ollama");
        await Button("Server type").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "OpenRouter", Exact = true }).ClickAsync();
        await Expect(page.Locator("#aiProvider")).ToHaveValueAsync("openrouter");
        await Expect(page.Locator("#aiUrl")).ToHaveValueAsync("https://openrouter.ai/api/v1");
        await Expect(page.Locator("#aiHelp")).ToContainTextAsync("Thinking is disabled");
        await Expect(page.Locator("#aiUnloadRow")).Not.ToBeVisibleAsync();
        await page.Locator("#aiUrl").FillAsync(fixture.Root + "/v1");
        await page.Locator("#aiModel").FillAsync("openrouter-fixture");
        await Button("Test with a Sample Article", exact: false).ClickAsync();
        await Expect(page.Locator("#modelTestResult")).ToContainTextAsync("validation passed");
        (string Path, JsonNode? Payload) last;
        lock (fixture.ModelRequests) last = fixture.ModelRequests[^1];
        Assert.Equal("/v1/chat/completions", last.Path);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["enabled"] = false }, last.Payload!["reasoning"]) && !((JsonObject)last.Payload).ContainsKey("temperature"));
        await Button("Server type").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Ollama", Exact = true }).ClickAsync();
        await page.Locator("#aiUrl").FillAsync(fixture.Root);
        await page.Locator("#aiModel").FillAsync("fixture");
        await Button("Test with a Sample Article", exact: false).ClickAsync();
        await Expect(page.Locator("#modelTestResult")).ToContainTextAsync("validation passed");
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-ai-settings.png") });
        await page.Locator("#aiForm").GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("AI settings saved");
        Assert.Equal("fixture", (string)Store.Settings()["ai"]!["model"]!);

        // Keep this test entirely on the fixture server when testing Refresh.
        foreach (var configured in Store.Sources().Cast<JsonObject>().Where(s => !((string)s["url"]!).StartsWith(fixture.Root)).ToList())
        {
            var disabled = (JsonObject)configured.DeepClone();
            disabled["enabled"] = false;
            Store.SaveSource(disabled, configured["id"]!.AsLong());
        }
        await Button("Sources").ClickAsync();
        await Expect(page.Locator("#checkNewSources")).ToBeEnabledAsync();
        await Expect(page.Locator("#checkNewSources")).ToHaveTextAsync("Check New Sources (2)");
        await page.Locator("#checkNewSources").ClickAsync();
        await Expect(page.Locator("#sourceRefreshStatus")).ToContainTextAsync("Last collection: complete", new() { Timeout = 15000 });
        await Expect(page.Locator("#checkNewSources")).ToHaveTextAsync("Check New Sources (0)");
        await Expect(page.Locator("#checkNewSources")).ToBeDisabledAsync();
        await Expect(page.Locator(".source-state.healthy")).ToHaveCountAsync(2);
        await Expect(page.Locator(".check-source[data-enabled=false]").First).ToBeDisabledAsync();
        await Button("Check Fixture source").ClickAsync();
        await Expect(page.Locator("#sourceRefreshStatus")).ToContainTextAsync("Checked Fixture source · Saved", new() { Timeout = 15000 });
        await Expect(Button("Check Fixture source")).ToBeEnabledAsync();
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-sources.png") });
        await page.RouteAsync("**/api/jobs/start", route => route.AbortAsync("blockedbyclient"));
        await Button("Refresh Sources").ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Could not send POST /api/jobs/start");
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("ERR_BLOCKED_BY_CLIENT");
        await Expect(page.Locator("#dismissStatus")).ToBeVisibleAsync();
        Assert.Equal("normal", await Style("#statusMessage", "whiteSpace"));
        await page.SetViewportSizeAsync(390, 844);
        Assert.True(await page.Locator("#statusMessage").EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth"));
        await Expect(page.Locator("#dismissStatus")).ToBeInViewportAsync();
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-blocked-request.png") });
        await page.SetViewportSizeAsync(1440, 1050);
        await Button("Interests").ClickAsync();
        await Button("Sources").ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Could not send POST /api/jobs/start");
        await page.Locator("#dismissStatus").ClickAsync();
        await Expect(page.Locator("#statusMessage")).Not.ToContainTextAsync("Could not send POST");
        await page.UnrouteAsync("**/api/jobs/start");
        await page.Context.SetOfflineAsync(true);
        await Button("Refresh Sources").ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Cannot reach the NewsReview server");
        await Expect(page.Locator("#sourceRefreshStatus")).ToContainTextAsync("not confirmed");
        await page.Context.SetOfflineAsync(false);
        await Expect(page.Locator("#sourceRefreshStatus")).ToContainTextAsync("No newer collection was recorded", new() { Timeout = 15000 });
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Could not send POST /api/jobs/start");
        await page.Locator("#dismissStatus").ClickAsync();
        await Expect(page.Locator("#statusMessage")).Not.ToContainTextAsync("Could not send POST");
        await Expect(page.Locator("#sourceRefreshStatus")).ToContainTextAsync("No newer collection was recorded");

        await page.Tab("Daily Feed").ClickAsync();
        await page.WaitForSelectorAsync(".story");
        await page.SetViewportSizeAsync(390, 844);
        await ChooseSort("Published: newest first");
        await Expect(Button("Sort stories")).ToContainTextAsync("Published: newest first");
        await ChooseSort("Rank: highest first");
        const string chromeScript = "el => [el.querySelector('.menubar').getBoundingClientRect().top, el.querySelector('footer').getBoundingClientRect().bottom]";
        var chrome = await page.Locator(".window").EvaluateAsync<double[]>(chromeScript);
        await page.Locator("main").EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
        Assert.Equal(chrome, await page.Locator(".window").EvaluateAsync<double[]>(chromeScript));
        Assert.True(await page.EvaluateAsync<bool>("scrollY === 0"));
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-mobile.png"), FullPage = true });
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), "Mobile page overflows horizontally");
        await page.MenuAsync("View", "Dark");
        Assert.Equal("dark", await page.Locator("html").GetAttributeAsync("data-theme"));
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-dark.png"), FullPage = true });
        await page.Tab("Settings").ClickAsync();
        await Button("Collection & History").ClickAsync();
        var historyCount = Store.Stories(new())["total"]!.AsLong();
        await page.MenuAsync("File", "Fresh Start...");
        await page.Locator("#resetHistory").UncheckAsync();
        await page.Locator("#resetBookmarks").CheckAsync();
        await page.Locator("#resetTopics").CheckAsync();
        await page.Locator("#clearHistoryDialog").GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        Assert.Equal(historyCount, Store.Stories(new())["total"]!.AsLong());
        await page.MenuAsync("File", "Fresh Start...");
        await Expect(page.Locator("#resetHistory")).ToBeCheckedAsync();
        await Expect(page.Locator("#resetRuns")).ToBeCheckedAsync();
        await Expect(page.Locator("#resetBookmarks")).Not.ToBeCheckedAsync();
        await Expect(page.Locator("#resetTopics")).Not.ToBeCheckedAsync();
        await page.GetByLabel("Type RESET SELECTED to confirm", new() { Exact = true }).FillAsync("wrong");
        await Button("Reset Selected Data").ClickAsync();
        Assert.Equal(historyCount, Store.Stories(new())["total"]!.AsLong());
        var preferences = Store.Settings();
        await page.Locator("#resetBookmarks").CheckAsync();
        lock (apiRequests) apiRequests.Clear();
        await page.GetByLabel("Type RESET SELECTED to confirm", new() { Exact = true }).FillAsync("RESET SELECTED");
        await Button("Reset Selected Data").ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToContainTextAsync("Fresh Start:");
        lock (apiRequests) Assert.True(apiRequests.Count(r => r == "sources") == 1, "Reset fetched source settings more than once");
        Assert.Equal(0, Store.Stories(new())["total"]!.AsLong());
        Assert.True(JsonNode.DeepEquals(preferences, Store.Settings()));
        Assert.Equal(3, Store.Sources().Count);
        await page.Tab("Daily Feed").ClickAsync();
        await Expect(page.Locator("#empty")).ToBeVisibleAsync();
        AssertNoErrors();
    }
}
