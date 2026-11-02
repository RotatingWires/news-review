using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace NewsReview.BrowserTests;

/// <summary>
/// The real application on a Kestrel socket (with fixture-friendly services) driven by Chromium.
/// Screenshots are written to the system temp folder for manual review.
/// </summary>
public abstract class BrowserTest : IAsyncLifetime
{
    internal readonly AppHost host = new();
    internal string Root = "";
    internal readonly List<string> errors = [];
    IPlaywright playwright = null!;
    IBrowser browser = null!;

    internal Store Store => host.Store;
    internal static string Screenshot(string name) => Path.Combine(Path.GetTempPath(), name);

    public virtual async Task InitializeAsync()
    {
        host.UseKestrel(0);
        host.StartServer();
        Root = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync();
        Assertions.SetDefaultExpectTimeout(10_000);
    }

    public virtual async Task DisposeAsync()
    {
        await browser.DisposeAsync();
        playwright.Dispose();
        host.Collector.Stop();
        await host.DisposeAsync();
    }

    /// <summary>A page that records uncaught script errors; signed in through the real setup/login API unless asked not to.</summary>
    internal async Task<IPage> NewPage(int width = 1280, int height = 900, bool signedIn = true, string? timezone = null)
    {
        var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, TimezoneId = timezone });
        var page = await context.NewPageAsync();
        page.PageError += (_, error) => { lock (errors) errors.Add(error); };
        if (signedIn)
        {
            var response = host.Passwords.Configured
                ? await context.APIRequest.PostAsync(Root + "/api/auth/login", new() { DataObject = new { password = "correct horse" } })
                : await context.APIRequest.PostAsync(Root + "/api/auth/setup", new()
                {
                    DataObject = new { setupToken = host.Passwords.SetupToken, password = "correct horse", confirmPassword = "correct horse" },
                });
            Assert.True(response.Ok, await response.TextAsync());
        }
        return page;
    }

    internal void AssertNoErrors()
    {
        lock (errors) Assert.True(errors.Count == 0, string.Join('\n', errors));
    }

    internal long AddSource(string category = "Protocols") => Fixtures.AddSource(Store, category)["id"]!.AsLong();

    internal static async Task<LocatorBoundingBoxResult> Box(ILocator locator) => (await locator.BoundingBoxAsync())!;

    internal JsonArray Stories(Store.StoryQuery? query = null) => Store.Stories(query ?? new())["items"]!.AsArray();
}
