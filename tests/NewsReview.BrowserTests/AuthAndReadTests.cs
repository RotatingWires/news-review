using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace NewsReview.BrowserTests;

public class AuthBrowserTests : BrowserTest
{
    [Fact]
    public async Task SetupLogoutLoginAndMobileLayout()
    {
        var page = await NewPage(signedIn: false);
        await page.GotoAsync(Root);
        await Expect(page).ToHaveURLAsync(Root + "/login.html");
        await Expect(page.Locator("#loginWindowTitle")).ToHaveTextAsync("Create Password");
        await page.Locator("#setupToken").FillAsync("wrong");
        await page.Locator("#password").FillAsync("browser-password");
        await page.Locator("#confirmPassword").FillAsync("browser-password");
        await page.Locator("#loginButton").ClickAsync();
        await Expect(page.Locator("#loginError")).ToContainTextAsync("Invalid setup token");
        await page.Locator("#setupToken").FillAsync(host.Passwords.SetupToken!);
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-setup.png") });
        await page.Locator("#loginButton").ClickAsync();
        await Expect(page.Locator("#version")).ToBeVisibleAsync();
        // A new database asks to restore a backup first, in the same box as TaskList: Start Fresh, then Import/Export....
        await Expect(page.Locator("#firstRunDialog .dialog-actions button")).ToHaveTextAsync(new[] { "Start Fresh", "Import/Export..." });
        await page.Locator("#firstRunContinue").ClickAsync();
        await page.MenuAsync("File", "Log Out");
        await Expect(page.Locator("#loginWindowTitle")).ToHaveTextAsync("Log In");
        await Expect(page.Locator("#setupTokenGroup")).ToBeHiddenAsync();
        await page.Locator("#password").FillAsync("wrong-password");
        await page.Locator("#loginButton").ClickAsync();
        await Expect(page.Locator("#loginError")).ToContainTextAsync("Incorrect password");
        await page.Locator("#password").FillAsync("browser-password");
        await page.Locator("#loginButton").ClickAsync();
        await Expect(page.Locator("#version")).ToBeVisibleAsync();
        await page.MenuAsync("File", "Log Out");
        await Expect(page.Locator("#loginWindowTitle")).ToHaveTextAsync("Log In");
        await page.SetViewportSizeAsync(390, 700);
        await page.EvaluateAsync("localStorage.setItem('newsreview-theme', 'dark')");
        await page.ReloadAsync();
        await Expect(page.Locator("#loginButton")).ToBeVisibleAsync();
        var box = await Box(page.Locator(".login-window"));
        Assert.True(box.Width <= 390 && box.Height < 700);
        await page.ScreenshotAsync(new() { Path = Screenshot("newsreview-login-mobile.png") });
        Assert.True(new PasswordStore(Path.Combine(host.DataPath, "auth.json")).Verify("browser-password", out _));
        AssertNoErrors();
    }
}

public class ReadMarkerBrowserTests : BrowserTest
{
    [Fact]
    public async Task HeadlineActivationMarksReadBeneathPublisher()
    {
        var source = AddSource();
        for (var number = 0; number < 4; number++)
            Store.InsertStory(source, Fixtures.Entry($"Read marker example {number}", $"https://example.com/read/{number}", null, "Fixture excerpt."), "Protocols", "2026-10-25");
        var page = await NewPage(390, 844);
        await page.Context.RouteAsync("https://example.com/**", route => route.FulfillAsync(new() { Body = "<p>Article fixture</p>", ContentType = "text/html" }));
        await page.GotoAsync(Root);
        await Expect(page.Locator(".story")).ToHaveCountAsync(4);
        await Expect(page.Locator(".read-label")).ToHaveCountAsync(0);
        ILocator Story(int number) => page.Locator(".story").Filter(new() { Has = page.GetByRole(AriaRole.Link, new() { Name = $"Read marker example {number}", Exact = true }) });
        foreach (var (number, action) in new[] { (0, "click"), (1, "middle"), (2, "keyboard") })
        {
            var link = Story(number).Locator("h3 a");
            var popup = await page.Context.RunAndWaitForPageAsync(async () =>
            {
                if (action == "keyboard")
                {
                    await link.FocusAsync();
                    await link.PressAsync("Enter");
                }
                else await link.ClickAsync(new() { Button = action == "middle" ? MouseButton.Middle : MouseButton.Left });
            });
            await popup.CloseAsync();
            await Expect(Story(number).Locator(".read-label")).ToHaveTextAsync("Read");
            var label = await Box(Story(number).Locator(".read-label"));
            var publisher = await Box(Story(number).Locator(".source-name"));
            Assert.True(label.Y >= publisher.Y + publisher.Height);
        }
        await page.ReloadAsync();
        await Expect(page.Locator(".read-label")).ToHaveCountAsync(3);
        await Expect(Story(3).Locator(".read-label")).ToHaveCountAsync(0);
        await Story(0).Locator(".bookmark").ClickAsync();
        await Expect(Story(0).Locator(".bookmark")).ToHaveAttributeAsync("aria-pressed", "true");
        await page.Tab("Bookmarks").ClickAsync();
        await Expect(page.Locator(".read-label")).ToHaveCountAsync(1);
        AssertNoErrors();
    }
}
