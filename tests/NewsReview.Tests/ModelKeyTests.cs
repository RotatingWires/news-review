using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NewsReview.Tests;

/// <summary>Collects log messages so tests can check what reached the log.</summary>
sealed class ListLogger : ILogger
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages) Messages.Add(formatter(state, exception));
    }
}

public class ModelKeyTests : IDisposable
{
    readonly TempDirectory directory = new();
    readonly string path;
    readonly Dictionary<string, string> environment = [];
    readonly ModelKeys keys;
    static JsonObject Config(string provider = "openrouter", string baseUrl = "https://openrouter.ai/api/v1") =>
        new() { ["provider"] = provider, ["base_url"] = baseUrl };

    public ModelKeyTests()
    {
        path = directory.File("model-credentials.json");
        keys = new ModelKeys(path, Fixtures.Protection(directory.Path), name => environment.GetValueOrDefault(name));
    }

    public void Dispose() => directory.Dispose();

    /// <summary>The file as a restarted app reads it, with the same data/keys.</summary>
    ModelKeys Reload(string path) => new(path, Fixtures.Protection(directory.Path), _ => null);

    [Fact]
    public void SaveReplaceRemoveRestartAndPermissions()
    {
        Assert.False(keys.Status(Config())["configured"]!.GetValue<bool>());
        var result = keys.Save(Config(), "fake-first-key");
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["configured"] = true, ["saved"] = true, ["environment"] = false, ["source"] = "saved" }, result));
        Assert.DoesNotContain("fake-first-key", result.ToJsonString());
        Assert.DoesNotContain("fake-first-key", File.ReadAllText(path));
        Assert.Equal("fake-first-key", Reload(path).Get(Config()));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        keys.Save(Config(), "fake-second-key");
        Assert.Equal("fake-second-key", keys.Get(Config()));
        Assert.DoesNotContain("fake-first-key", File.ReadAllText(path));
        Assert.False(keys.Remove(Config())["configured"]!.GetValue<bool>());
        Assert.Equal("", Reload(path).Get(Config()));
        Assert.Empty(Directory.GetFiles(directory.Path, ".model-credentials-*"));
    }

    [Fact]
    public void PlainVersionOneFileIsEncryptedOnceOnLoad()
    {
        File.WriteAllText(path, """{"version":1,"keys":[{"provider":"openrouter","base_url":"https://openrouter.ai/api/v1","key":"fake-plain-key"}]}""");
        var logger = new ListLogger();
        var loaded = new ModelKeys(path, Fixtures.Protection(directory.Path), _ => null, logger);
        Assert.Equal("fake-plain-key", loaded.Get(Config()));
        var written = File.ReadAllText(path);
        Assert.DoesNotContain("fake-plain-key", written);
        Assert.Equal(2, JsonNode.Parse(written)!["version"]!.GetValue<int>());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Contains(logger.Messages, m => m.Contains("Encrypted the saved model API keys"));
        // Already encrypted: a restart reads it without writing it again.
        var before = File.GetLastWriteTimeUtc(path);
        Assert.Equal("fake-plain-key", Reload(path).Get(Config()));
        Assert.Equal(written, File.ReadAllText(path));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void KeysThatCannotBeDecryptedAreTreatedAsNotSaved()
    {
        keys.Save(Config(), "fake-lost-key");
        // A different keys folder stands in for a deleted data/keys or a data folder copied to another machine.
        var logger = new ListLogger();
        var elsewhere = new ModelKeys(path, Fixtures.Protection(directory.File("other")), _ => null, logger);
        Assert.False(elsewhere.Status(Config())["saved"]!.GetValue<bool>());
        Assert.Contains(logger.Messages, m => m.Contains("cannot be decrypted"));
        Assert.DoesNotContain("fake-lost-key", string.Join('\n', logger.Messages));
        elsewhere.Save(Config(), "fake-new-key");
        Assert.Equal("fake-new-key", new ModelKeys(path, Fixtures.Protection(directory.File("other")), _ => null).Get(Config()));
    }

    [Fact]
    public void ProviderServerAndPathScopesAndEnvironmentFallback()
    {
        environment["NEWSREVIEW_MODEL_API_KEY"] = "fake-environment-key";
        keys.Save(Config(), "fake-saved-key");
        Assert.Equal("fake-saved-key", keys.Get(Config()));
        var equivalent = Config(baseUrl: "https://OPENROUTER.ai:443/api/v1/");
        Assert.Equal(ModelKeys.Scope(Config()), ModelKeys.Scope(equivalent));
        Assert.Equal("fake-saved-key", keys.Get(equivalent));
        foreach (var config in new[] { Config("compatible"), Config(baseUrl: "https://another.example/api/v1"),
                     Config(baseUrl: "https://openrouter.ai/other"), Config(baseUrl: "http://openrouter.ai/api/v1") })
            Assert.Equal("fake-environment-key", keys.Get(config));
        Assert.Equal("", keys.Get(new JsonObject { ["provider"] = "ollama" }));
        Assert.Equal("environment", (string)keys.Remove(Config())["source"]!);
        Assert.Equal("fake-environment-key", keys.Get(Config()));
    }

    [Fact]
    public async Task ConcurrentUpdatesKeepOtherServerKeys()
    {
        var configs = Enumerable.Range(0, 12).Select(n => Config(baseUrl: $"https://server-{n}.example/v1")).ToList();
        await Parallel.ForEachAsync(Enumerable.Range(0, 12), new ParallelOptions { MaxDegreeOfParallelism = 4 },
            (n, _) => { keys.Save(configs[n], $"fake-key-{n}"); return ValueTask.CompletedTask; });
        var reloaded = Reload(path);
        Assert.Equal(Enumerable.Range(0, 12).Select(n => $"fake-key-{n}"), configs.Select(reloaded.Get));
    }

    [Fact]
    public void InvalidInputAndFileErrorsNeverEchoKeys()
    {
        foreach (var key in new JsonNode?[] { "", "fake-secret\r\nInjected:header", "fake secret", "é-secret", new string('x', 4097), null })
        {
            var error = Assert.Throws<ValidationException>(() => keys.Save(Config(), key));
            if (key is JsonValue value && (string)value! is { Length: > 0 } text) Assert.DoesNotContain(text, error.Message);
        }
        foreach (var baseUrl in new[] { "https://user:password@example.com/v1", "https://example.com/v1?token=secret", "https://example.com/v1#fragment" })
            Assert.Throws<ValidationException>(() => keys.Save(Config(baseUrl: baseUrl), "fake-key"));
        File.WriteAllText(path, """{"version":1,"keys":[{"key":"fake-private-value"}]}""");
        Assert.DoesNotContain("fake-private-value", Assert.Throws<InvalidOperationException>(() => Reload(path)).Message);
        var missing = Reload(Path.Combine(directory.Path, "missing", "model-credentials.json"));
        Assert.DoesNotContain("fake-private-value", Assert.Throws<ValidationException>(() => missing.Save(Config(), "fake-private-value")).Message);
        Assert.False(missing.Status(Config())["saved"]!.GetValue<bool>());
    }

    [Fact]
    public void FailedReplacementPreservesPreviousKey()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        keys.Save(Config(), "fake-previous-key");
        var mode = File.GetUnixFileMode(directory.Path);
        // A read-only data folder makes the temporary-file write fail before anything is replaced.
        File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Throws<ValidationException>(() => keys.Save(Config(), "fake-new-key"));
        }
        finally
        {
            File.SetUnixFileMode(directory.Path, mode);
        }
        Assert.Equal("fake-previous-key", keys.Get(Config()));
        Assert.Equal("fake-previous-key", Reload(path).Get(Config()));
        Assert.Empty(Directory.GetFiles(directory.Path, ".model-credentials-*"));
    }
}

public class CredentialTransportTests : IDisposable
{
    readonly FixtureServer first = new(), second = new();
    readonly List<string?> firstSeen = [], secondSeen = [];
    readonly Dictionary<string, string> headers = new() { ["Authorization"] = "Bearer fake-private-key" };
    string target = "";

    public CredentialTransportTests()
    {
        foreach (var (server, seen) in new[] { (first, firstSeen), (second, secondSeen) })
            server.Handler = (path, body, request, response) =>
            {
                lock (seen) seen.Add(request.Headers["Authorization"]);
                if (request.HttpMethod == "POST")
                    return (400, new JsonObject { ["error"] = "Rejected " + request.Headers["Authorization"] }.ToJsonString(), "application/json");
                if (path == "/redirect")
                {
                    response.Headers["Location"] = target;
                    return (302, "", "text/plain");
                }
                return (200, """{"ok":true}""", "application/json");
            };
    }

    public void Dispose()
    {
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task ErrorMessagesAndLogsRedactTheAuthorizationSecret()
    {
        var logger = new ListLogger();
        var fetcher = new Fetcher(publicOnly: false, logger);
        var error = await Assert.ThrowsAsync<ValidationException>(() => ((IFetcher)fetcher).Json(first.Root, new JsonObject(), headers: headers));
        Assert.Contains("[redacted]", error.Message);
        Assert.DoesNotContain("fake-private-key", error.Message + string.Join('\n', logger.Messages));
        Assert.NotEmpty(logger.Messages);
    }

    [Fact]
    public async Task CrossOriginRedirectDoesNotForwardKey()
    {
        target = second.Root + "/ok";
        var error = await Assert.ThrowsAsync<ValidationException>(() => new Fetcher(publicOnly: false).Request(first.Root + "/redirect", headers: headers));
        Assert.Contains("cannot redirect", error.Message);
        Assert.Empty(secondSeen);
    }

    [Fact]
    public async Task SameOriginRedirectCanUseTheKey()
    {
        target = first.Root + "/ok";
        var result = await new Fetcher(publicOnly: false).Request(first.Root + "/redirect", headers: headers);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"ok":true}"""), JsonNode.Parse(result.Text)));
        Assert.Equal(["Bearer fake-private-key", "Bearer fake-private-key"], firstSeen);
    }
}
