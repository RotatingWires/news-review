using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NewsReview.Tests;

/// <summary>A temporary directory removed after the test.</summary>
sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("newsreview-test-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

static class Fixtures
{
    public static JsonObject Review(Action<JsonObject>? change = null)
    {
        var value = new JsonObject
        {
            ["relevant"] = true, ["score"] = 80, ["summary"] = "An experimental feature is available in development builds.",
            ["tags"] = new JsonArray("HTTP", "Standards"), ["milestone"] = "experimental", ["duplicate_of"] = null,
        };
        change?.Invoke(value);
        return value;
    }

    /// <summary>One topic edit expressed as the full-list save the Topics editor sends.</summary>
    public static JsonObject ChangeTopic(Store store, string action, string name, string? replacement = null)
    {
        var names = store.Topics().Select(t => (string)t!).ToList();
        return action switch
        {
            "add" => store.SaveTopics([.. names, name], new Dictionary<string, string>()),
            "rename" => store.SaveTopics(names.Select(n => n == name ? replacement! : n).ToList(), new Dictionary<string, string> { [name] = replacement! }),
            "delete" => store.SaveTopics(names.Where(n => n != name).ToList(), new Dictionary<string, string>(), removalConfirmed: true),
            _ => throw new ArgumentException(action),
        };
    }

    /// <summary>An enabled source whose default category is also a managed topic.</summary>
    public static JsonObject AddSource(Store store, string category = "Technology")
    {
        if (!store.Topics().Any(t => (string)t! == category)) Fixtures.ChangeTopic(store, "add", category);
        var id = store.SaveSource(new JsonObject
        {
            ["name"] = "Example News", ["url"] = "https://example.com/news/", ["feed_url"] = "", ["category"] = category, ["enabled"] = true,
        });
        return store.Sources().Cast<JsonObject>().First(s => s["id"]!.AsLong() == id);
    }

    public static Entry Entry(string headline, string url, string? published = null, string excerpt = "Details") =>
        new() { Headline = headline, Url = url, Excerpt = excerpt, PublishedAt = published };

    public static void Sql(Store store, string sql, params object?[] args) => store.Write(db => { db.Execute(sql, args); });

    public static Collector Collector(Store store, IFetcher? fetcher = null, Func<string, string?>? environment = null)
    {
        fetcher ??= new Fetcher(publicOnly: false);
        return new Collector(store, new SourceFetcher(fetcher), new Reviewer(fetcher, NullLogger<Reviewer>.Instance),
            new ModelKeys(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(store.Path)!, "model-credentials.json"), environment ?? (_ => null)),
            NullLogger<Collector>.Instance);
    }

    public static async Task WaitUntil(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time");
            await Task.Delay(50);
        }
    }
}

/// <summary>A real local HTTP server for feeds, pages, and model APIs. Handlers receive the path and JSON body.</summary>
sealed class FixtureServer : IDisposable
{
    readonly HttpListener listener = new();
    readonly Task loop;
    public string Root { get; }
    public List<(string Path, JsonNode? Body, string? Authorization)> Requests { get; } = [];
    /// <summary>Receives the path, parsed JSON body, request and response (for extra headers); returns the reply.</summary>
    public Func<string, JsonNode?, HttpListenerRequest, HttpListenerResponse, (int Status, string Body, string ContentType)> Handler { get; set; } =
        (_, _, _, _) => (404, "missing", "text/plain");

    public FixtureServer()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            Root = $"http://127.0.0.1:{port}";
            listener.Prefixes.Clear();
            listener.Prefixes.Add(Root + "/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 20) { listener = new HttpListener(); }
        }
        loop = Task.Run(Serve);
    }

    async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => Respond(context));
        }
    }

    void Respond(HttpListenerContext context)
    {
        try
        {
            JsonNode? body = null;
            if (context.Request.HasEntityBody)
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                try { body = JsonNode.Parse(text); } catch { body = JsonValue.Create(text); }
            }
            var path = context.Request.Url!.PathAndQuery;
            lock (Requests) Requests.Add((path, body, context.Request.Headers["Authorization"]));
            var (status, content, type) = Handler(path, body, context.Request, context.Response);
            var bytes = Encoding.UTF8.GetBytes(content);
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        listener.Stop();
        listener.Close();
    }
}

/// <summary>The real application, hosted in memory, with fixture-friendly services.</summary>
sealed class AppHost : WebApplicationFactory<Program>
{
    public TempDirectory Directory { get; } = new();
    public IFetcher SourceFetcher { get; set; } = new Fetcher(publicOnly: false);
    /// <summary>Environment variables seen by the model-key store, instead of the real process environment.</summary>
    public Dictionary<string, string> Environment { get; } = [];
    public ListLogger Logs { get; } = new();
    readonly string? dataPath;
    public string DataPath => dataPath ?? Directory.File("data");

    /// <summary>A fresh temporary data folder, or an existing one to simulate restarting the app.</summary>
    public AppHost(string? dataPath = null) => this.dataPath = dataPath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("NewsReview:DataDirectory", DataPath);
        builder.UseSetting("NewsReview:Scheduler", "false");
        builder.ConfigureLogging(logging => logging.AddProvider(new ListLoggerProvider(Logs)));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new SourceFetcher(SourceFetcher));
            services.AddSingleton(new ModelKeys(System.IO.Path.Combine(DataPath, "model-credentials.json"), name => Environment.GetValueOrDefault(name)));
        });
    }

    public Store Store => Services.GetRequiredService<Store>();
    public Collector Collector => Services.GetRequiredService<Collector>();
    public PasswordStore Passwords => Services.GetRequiredService<PasswordStore>();

    /// <summary>A client signed in through the real first-run setup flow.</summary>
    public async Task<HttpClient> SignedIn(string password = "correct horse")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var response = Passwords.Configured
            ? await client.PostAsync("/api/auth/login", HttpExtensions.Body(new { password }))
            : await client.PostAsync("/api/auth/setup", HttpExtensions.Body(new { setupToken = Passwords.SetupToken, password, confirmPassword = password }));
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) Directory.Dispose();
    }
}

sealed class ListLoggerProvider(ListLogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => logger;
    public void Dispose() { }
}

static class HttpExtensions
{
    /// <summary>A JSON body with Content-Length, as browsers send; the API rejects chunked bodies.</summary>
    public static StringContent Body(object body) =>
        new(body is JsonNode node ? node.ToJsonString() : System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static async Task<(int Status, JsonNode? Body)> Json(this HttpClient client, string path, HttpMethod? method = null, object? body = null,
        Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (body is not null) request.Content = Body(body);
        foreach (var (name, value) in headers ?? []) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }
}
