using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace NewsReview.BrowserTests;

public class PollingBrowserTests : BrowserTest
{
    [Fact]
    public async Task IdleSilenceFocusRefreshProgressUnsavedSettingsAndRecovery()
    {
        var page = await NewPage();
        await page.Clock.InstallAsync();
        var requests = new List<string>();
        int Count() { lock (requests) return requests.Count; }
        page.Request += (_, request) => { if (request.Url.EndsWith("/api/overview")) lock (requests) requests.Add(request.Url); };
        await page.GotoAsync(Root);
        await Expect(page.Locator("#version")).ToHaveTextAsync("v" + Transfer.Version);
        await Expect(page.Locator("#empty")).ToBeVisibleAsync();
        async Task Advance(int milliseconds)
        {
            await page.Clock.FastForwardAsync(milliseconds);
            await page.WaitForTimeoutAsync(100);
        }
        var collector = host.Collector;

        var count = Count();
        await Advance(35000);
        Assert.True(Count() == count, "Idle tab polled the server");

        // A server-side/scheduled job can start without browser requests.
        collector.Update(("running", true), ("message", "Fixture job started"));
        await Advance(35000);
        Assert.True(Count() == count, "Idle tab kept polling for external jobs");
        await page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
        await Advance(150);
        await Expect(page.Locator("footer")).ToHaveAttributeAsync("aria-busy", "true");
        await Expect(page.Locator("#status-message")).ToContainTextAsync("Fixture job started");

        collector.Update(("message", "Fixture job progressed"));
        await Advance(2600);
        await Expect(page.Locator("#status-message")).ToContainTextAsync("Fixture job progressed");
        Assert.True(Count() > count);
        collector.Update(("running", false), ("message", "Fixture job finished"));
        await Advance(2600);
        await Expect(page.Locator("footer")).ToHaveAttributeAsync("aria-busy", "false");
        await Expect(page.Locator("#status-message")).ToContainTextAsync("Fixture job finished");
        count = Count();
        await Advance(35000);
        Assert.True(Count() == count, "Polling continued after the job finished");

        await page.GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
        await page.Locator("#guidance").FillAsync("Unsaved editorial guidance");
        collector.Update(("message", "Changed elsewhere"));
        await page.EvaluateAsync("window.dispatchEvent(new Event('focus')); document.dispatchEvent(new Event('visibilitychange'))");
        await Advance(150);
        await Expect(page.Locator("#status-message")).ToContainTextAsync("Changed elsewhere");
        await Expect(page.Locator("#guidance")).ToHaveValueAsync("Unsaved editorial guidance");

        await page.Context.SetOfflineAsync(true);
        await page.EvaluateAsync("window.dispatchEvent(new Event('focus'))");
        await Advance(150);
        await Expect(page.Locator("#status-message")).ToContainTextAsync("Cannot reach");
        count = Count();
        await Advance(5100);
        Assert.True(Count() > count, "Connection failure was not retried");
        await page.Context.SetOfflineAsync(false);
        await Advance(5100);
        await Expect(page.Locator("#status-message")).Not.ToContainTextAsync("Cannot reach");
        count = Count();
        await Advance(35000);
        Assert.True(Count() == count, "Recovery polling continued after reconnecting");
        AssertNoErrors();
    }
}
