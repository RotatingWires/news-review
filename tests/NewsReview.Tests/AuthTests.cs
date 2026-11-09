using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace NewsReview.Tests;

public class AuthTests : IDisposable
{
    readonly TempDirectory directory = new();
    string AuthPath => directory.File("auth.json");

    public void Dispose() => directory.Dispose();

    [Fact]
    public void PasswordPersistenceAndTokenConsumption()
    {
        var auth = new PasswordStore(AuthPath);
        var token = auth.SetupToken!;
        Assert.NotEqual(token, new PasswordStore(AuthPath).SetupToken);
        Assert.Throws<SetupTokenException>(() => auth.Setup("wrong", "password123", "password123"));
        Assert.Throws<ValidationException>(() => auth.Setup(token, "short", "short"));
        Assert.Throws<ValidationException>(() => auth.Setup(token, "password123", "different"));
        auth.Setup(token.ToLowerInvariant().Replace('-', ' '), "password123", "password123");
        Assert.Null(auth.SetupToken);
        var saved = File.ReadAllText(AuthPath);
        Assert.DoesNotContain("password123", saved);
        Assert.DoesNotContain(token, saved);
        Assert.Equal(600000, JsonNode.Parse(saved)!["Iterations"]!.AsLong());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(AuthPath));
        var loaded = new PasswordStore(AuthPath);
        Assert.True(loaded.Verify("password123", out var upgraded));
        Assert.False(upgraded);
        Assert.False(loaded.Verify("different", out _));
        Assert.False(loaded.Verify(null, out _));
        Assert.Throws<SetupConflictException>(() => loaded.Setup(token, "changed-password", "changed-password"));
        Assert.DoesNotContain(Directory.GetFiles(directory.Path), f => f != AuthPath);
    }

    [Fact]
    public void ExistingPythonCredentialsVerifyAndUpgradeOnce()
    {
        // The earlier Python release wrote this format; older TaskList-style files used fewer iterations.
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2("legacy password", salt, 210_000, HashAlgorithmName.SHA256, 32);
        File.WriteAllText(AuthPath, $$"""{"version": 1, "iterations": 210000, "salt": "{{Convert.ToBase64String(salt)}}", "hash": "{{Convert.ToBase64String(hash)}}"}""");
        var before = File.ReadAllText(AuthPath);
        var auth = new PasswordStore(AuthPath);
        Assert.True(auth.Configured);
        Assert.False(auth.Verify("wrong password", out _));
        Assert.Equal(before, File.ReadAllText(AuthPath));
        Assert.True(auth.Verify("legacy password", out var upgraded));
        Assert.True(upgraded);
        var after = File.ReadAllText(AuthPath);
        Assert.Equal(600000, JsonNode.Parse(after)!["Iterations"]!.AsLong());
        Assert.True(auth.Verify("legacy password", out upgraded));
        Assert.False(upgraded);
        Assert.Equal(after, File.ReadAllText(AuthPath));
        Assert.True(new PasswordStore(AuthPath).Verify("legacy password", out upgraded));
        Assert.False(upgraded);
        Assert.False(File.Exists(AuthPath + ".tmp"));
    }

    [Theory]
    [InlineData("bad json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\": 999}")]
    public void MalformedCredentialsFailClosed(string contents)
    {
        // Like the other apps: an unreadable auth.json signs nobody in and is left for the owner to fix.
        File.WriteAllText(AuthPath, contents);
        var auth = new PasswordStore(AuthPath);
        Assert.True(auth.Configured);
        Assert.False(auth.Verify("any password", out _));
        Assert.Equal(contents, File.ReadAllText(AuthPath));
    }

    [Fact]
    public void SimultaneousInstancesCannotOverwriteCredentials()
    {
        PasswordStore first = new(AuthPath), second = new(AuthPath);
        first.Setup(first.SetupToken, "first-password", "first-password");
        Assert.Throws<SetupConflictException>(() => second.Setup(second.SetupToken, "second-password", "second-password"));
        Assert.True(new PasswordStore(AuthPath).Verify("first-password", out _));
    }

    [Fact]
    public async Task CommandLineAllowsLanAndPrintsTokenOnlyToConsole()
    {
        int port;
        using (var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }
        // A file named "logs" blocks the log directory; the service must still run with console output.
        File.WriteAllText(directory.File("logs"), "A file blocks directory creation");
        using var process = Process.Start(new ProcessStartInfo("dotnet",
            [typeof(Program).Assembly.Location, "--urls", $"http://0.0.0.0:{port}", "--data-dir", directory.File("data"), "--no-scheduler"])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory.Path,
        })!;
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            using var client = new HttpClient();
            JsonNode? status = null;
            for (var attempt = 0; attempt < 100 && status is null; attempt++)
            {
                if (process.HasExited) Assert.Fail(output.ToString());
                try { status = await client.GetFromJsonAsync<JsonNode>($"http://127.0.0.1:{port}/api/auth/status"); }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Assert.NotNull(status);
            Assert.False(status!["configured"]!.GetValue<bool>());
        }
        finally
        {
            process.Kill();
            await process.WaitForExitAsync();
        }
        var token = Regex.Match(output.ToString(), "[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}");
        Assert.True(token.Success, output.ToString());
        Assert.False(File.Exists(directory.File(Path.Combine("data", "auth.json"))));
        Assert.True(File.Exists(directory.File("logs"))); // still the blocking file, not a directory
    }

    [Fact]
    public async Task FirstRunHttpSetupProtectsDataAndPersists()
    {
        using var host = new AppHost();
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        async Task<HttpResponseMessage> Send(string path, object? body = null, string? cookie = null, string? origin = null)
        {
            var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
            if (body is not null) request.Content = HttpExtensions.Body(body);
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            if (origin is not null) request.Headers.Add("Origin", origin);
            return await client.SendAsync(request);
        }
        Assert.False((await (await Send("/api/auth/status")).Content.ReadFromJsonAsync<JsonNode>())!["configured"]!.GetValue<bool>());
        foreach (var path in new[] { "/api/overview", "/api/stories", "/api/sources", "/api/backup" })
        {
            var response = await Send(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Sign in to continue", (string)(await response.Content.ReadFromJsonAsync<JsonNode>())!["error"]!);
        }
        var token = host.Passwords.SetupToken;
        object Data(string? setupToken = null, string confirm = "test-password") =>
            new { setupToken = setupToken ?? token, password = "test-password", confirmPassword = confirm };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send("/api/auth/setup", Data(), origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("/api/auth/setup", Data("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("/api/auth/setup", Data(confirm: "different"))).StatusCode);
        var setup = await Send("/api/auth/setup", Data());
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var setCookie = setup.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", setCookie);
        Assert.Contains("samesite=strict", setCookie);
        Assert.Contains("expires=", setCookie);
        var cookie = setCookie.Split(';')[0];
        Assert.True((await (await Send("/api/auth/status", cookie: cookie)).Content.ReadFromJsonAsync<JsonNode>())!["authenticated"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await Send("/api/overview", cookie: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send("/api/auth/setup", Data())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("/api/auth/login", new { password = "wrong" })).StatusCode);
        var limited = await Send("/api/auth/login", new { password = "test-password" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("", await limited.Content.ReadAsStringAsync());
        Assert.True(new PasswordStore(Path.Combine(host.DataPath, "auth.json")).Verify("test-password", out _));
    }

    [Fact]
    public async Task SessionsSurviveRestartAndExpireAfterThirtyIdleDays()
    {
        string cookie;
        using (var first = new AppHost(directory.File("data")))
        {
            var client = first.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var setup = await client.PostAsync("/api/auth/setup", HttpExtensions.Body(new
            {
                setupToken = first.Passwords.SetupToken, password = "restart-password", confirmPassword = "restart-password",
            }));
            var setCookie = setup.Headers.GetValues("Set-Cookie").Single();
            var expires = DateTimeOffset.Parse(Regex.Match(setCookie, "expires=([^;]+)").Groups[1].Value);
            Assert.InRange((expires - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
            cookie = setCookie.Split(';')[0];
        }
        Assert.NotEmpty(Directory.GetFiles(directory.File(Path.Combine("data", "keys"))));
        using var second = new AppHost(directory.File("data"));
        var restarted = second.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var (status, _) = await restarted.Json("/api/overview", headers: new() { ["Cookie"] = cookie });
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task StaticPagesRedirectBetweenLoginAndApp()
    {
        using var host = new AppHost();
        var anonymous = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var redirect = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/login.html", redirect.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/login.html")).StatusCode);
        var signedIn = await host.SignedIn();
        Assert.Equal("/", (await signedIn.GetAsync("/login.html")).Headers.Location!.OriginalString);
        var page = await signedIn.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await signedIn.GetAsync("/api/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await signedIn.GetAsync("/newsreview.db")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await signedIn.PostAsync("/index.html", HttpExtensions.Body(new { }))).StatusCode);
    }
}

public class LoggingTests : IDisposable
{
    readonly TempDirectory directory = new();

    public void Dispose() => directory.Dispose();

    static void Write(string path, long maxBytes, Action<ILogger> work)
    {
        using var provider = new SingleFileLoggerProvider(path, maxBytes);
        work(provider.CreateLogger("fixture"));
    }

    [Fact]
    public void TruncatesSameFileByUtf8BytesWithoutRotatedCopies()
    {
        var path = Path.Combine(directory.Path, "logs", "console.log");
        // The second entry (about 205 bytes) would push the file past 250 bytes, so the file restarts.
        Write(path, 250, logger =>
        {
            logger.LogInformation("old");
            logger.LogInformation("{Text}", new string('€', 50));
        });
        var content = File.ReadAllText(path);
        Assert.DoesNotContain("old", content);
        Assert.Contains(new string('€', 50), content);
        Assert.True(new FileInfo(path).Length <= 250);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void AppendsAcrossRestartsAndRecordsExceptions()
    {
        var path = directory.File("console.log");
        File.WriteAllText(path, "previous run\n");
        Write(path, 10 * 1024 * 1024, logger =>
        {
            try { throw new InvalidOperationException("fixture failure"); }
            catch (InvalidOperationException error) { logger.LogError(error, "Request failed"); }
        });
        var content = File.ReadAllText(path);
        Assert.StartsWith("previous run\n", content);
        Assert.Contains("Request failed", content);
        Assert.Contains("InvalidOperationException: fixture failure", content);
    }

    [Fact]
    public void UnwritableLogLocationDoesNotRaise()
    {
        File.WriteAllText(directory.File("logs"), "A file blocks directory creation");
        Write(Path.Combine(directory.File("logs"), "console.log"), 1024, logger =>
        {
            logger.LogInformation("Storage became unavailable");
            logger.LogInformation("Service can continue");
        });
    }
}
