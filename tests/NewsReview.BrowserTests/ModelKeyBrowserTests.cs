using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace NewsReview.BrowserTests;

public class ModelKeyBrowserTests : BrowserTest
{
    readonly NewsFixture fixture = new();

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        fixture.Dispose();
    }

    string LastAuthorization() { lock (fixture.ModelRequests) return fixture.Authorizations[^1]; }

    [Fact]
    public async Task SaveReplaceScopeReloadRemoveEnvironmentFallbackAndMobileDialogs()
    {
        const string first = "fake-ui-first-private-key", second = "fake-ui-replacement-private-key";
        var page = await NewPage(1440, 1000);
        var responses = new List<Task<string>>();
        page.Response += (_, response) =>
        {
            if (response.Url.Contains("/api/") && (response.Headers.GetValueOrDefault("content-type") ?? "").StartsWith("application/json"))
                lock (responses) responses.Add(response.TextAsync());
        };
        await page.GotoAsync(Root);
        ILocator Button(string name) => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
        async Task OpenAi()
        {
            await page.Tab("Settings").ClickAsync();
            await Button("AI").ClickAsync();
        }
        async Task ChooseProvider(string option)
        {
            await Button("Server type").ClickAsync();
            await page.GetByRole(AriaRole.Option, new() { Name = option, Exact = true }).ClickAsync();
        }
        var status = page.Locator("#modelKeyStatus");
        var password = page.Locator("#modelApiKey");
        await OpenAi();
        await Expect(page.Locator("#modelKeyPanel")).Not.ToBeVisibleAsync();
        await ChooseProvider("OpenRouter");
        await page.Locator("#aiUrl").FillAsync(fixture.Root + "/v1");
        await page.Locator("#aiModel").FillAsync("openrouter-fixture");
        await Expect(status).ToHaveTextAsync("No key configured.");
        await Expect(password).ToHaveAttributeAsync("type", "password");
        await password.FillAsync(first);
        await page.Locator("#modelKeySave").ClickAsync();
        await Expect(password).ToHaveValueAsync("");
        await Expect(status).ToHaveTextAsync("Key configured.");
        await Expect(page.Locator("#modelKeySave")).ToHaveTextAsync("Replace Key");
        await Expect(page.Locator("#modelKeyRemove")).ToBeEnabledAsync();
        await Button("Test with a Sample Article").ClickAsync();
        await Expect(page.Locator("#modelTestResult")).ToContainTextAsync("validation passed");
        Assert.Equal("Bearer " + first, LastAuthorization());
        await password.FillAsync(second);
        await page.Locator("#modelKeySave").ClickAsync();
        await Expect(password).ToHaveValueAsync("");
        await Button("Test with a Sample Article").ClickAsync();
        await Expect(page.Locator("#modelTestResult")).ToContainTextAsync("validation passed");
        Assert.Equal("Bearer " + second, LastAuthorization());
        await page.Locator("#aiForm").GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.Locator("#statusMessage")).ToHaveTextAsync("AI settings saved.");
        await page.ReloadAsync();
        await OpenAi();
        await Expect(status).ToHaveTextAsync("Key configured.");
        await Expect(password).ToHaveValueAsync("");
        await page.Locator("#aiUrl").FillAsync(fixture.Root + "/different/v1");
        await password.FocusAsync();
        await Expect(status).ToHaveTextAsync("No key configured.");
        await Expect(page.Locator("#modelKeyRemove")).ToBeDisabledAsync();
        await page.Locator("#aiUrl").FillAsync(fixture.Root + "/v1");
        await password.FocusAsync();
        await Expect(status).ToHaveTextAsync("Key configured.");
        await ChooseProvider("Compatible chat server (e.g. LM Studio)");
        await Expect(status).ToHaveTextAsync("No key configured.");
        await ChooseProvider("OpenRouter");
        await Expect(status).ToHaveTextAsync("Key configured.");
        await page.Locator("#modelKeyRemove").ClickAsync();
        var dialog = page.Locator("#modelKeyRemoveDialog");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        var config = new JsonObject { ["provider"] = "openrouter", ["base_url"] = fixture.Root + "/v1" };
        Assert.Equal(second, host.Collector.ModelKeys.Get(config));
        await page.SetViewportSizeAsync(390, 844);
        await page.Locator("#modelKeyRemove").ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true })).ToBeInViewportAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Expect(status).ToHaveTextAsync("No key configured.");
        Assert.Equal("", host.Collector.ModelKeys.Get(config));
        host.Environment["NEWSREVIEW_MODEL_API_KEY"] = "fake-environment-key";
        await page.ReloadAsync();
        await OpenAi();
        await Expect(status).ToHaveTextAsync("Key configured from the server environment.");
        await Expect(page.Locator("#modelKeyRemove")).ToBeDisabledAsync();
        await password.FillAsync(first);
        await page.Locator("#modelKeySave").ClickAsync();
        await Expect(password).ToHaveValueAsync("");
        await page.Locator("#modelKeyRemove").ClickAsync();
        await Expect(page.Locator("#modelKeyEnvironmentFallback")).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Expect(status).ToHaveTextAsync("Key configured from the server environment.");
        Assert.Equal("fake-environment-key", host.Collector.ModelKeys.Get(config));
        Task<string>[] pending;
        lock (responses) pending = responses.ToArray();
        var bodies = await Task.WhenAll(pending);
        Assert.All(bodies, body => Assert.True(!body.Contains(first) && !body.Contains(second)));
        Assert.True(await page.Locator("body").EvaluateAsync<bool>("el => el.scrollWidth <= innerWidth"));
        AssertNoErrors();
    }
}
