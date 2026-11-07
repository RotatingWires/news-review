using Microsoft.AspNetCore.ResponseCompression;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using NewsReview;

if (args is ["backup", .. var backupArgs]) return Transfer.BackupCommand(backupArgs);
if (args is ["--version"])
{
    Console.WriteLine(Transfer.Version);
    return 0;
}

// NewsReview options are read here; everything else (such as --urls) goes to ASP.NET Core.
var hostArgs = new List<string>();
string? dataDirectoryArgument = null;
var schedulerEnabled = true;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--data-dir" && i + 1 < args.Length) dataDirectoryArgument = args[++i];
    else if (args[i] == "--no-scheduler") schedulerEnabled = false;
    else hostArgs.Add(args[i]);
}

var builder = WebApplication.CreateBuilder(hostArgs.ToArray());
if (dataDirectoryArgument is not null) builder.Configuration["NewsReview:DataDirectory"] = dataDirectoryArgument;
if (!schedulerEnabled) builder.Configuration["NewsReview:Scheduler"] = "false";
// Loopback unless --urls, ASPNETCORE_URLS, or an appsettings "urls" value chooses another address.
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:8714");
builder.Logging.AddProvider(new SingleFileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "logs", "console.log"), 10 * 1024 * 1024));

static string DataDirectory(IServiceProvider services)
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var directory = configuration["NewsReview:DataDirectory"] ?? Environment.GetEnvironmentVariable("NEWSREVIEW_DATA_DIR")
        ?? Path.Combine(services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "data");
    directory = Path.GetFullPath(directory);
    Directory.CreateDirectory(directory);
    return directory;
}

builder.Services.AddSingleton(services => new Store(Path.Combine(DataDirectory(services), "newsreview.db")));
builder.Services.AddSingleton(services => new PasswordStore(Path.Combine(DataDirectory(services), "auth.json")));
builder.Services.AddSingleton(services => new ModelKeys(Path.Combine(DataDirectory(services), "model-credentials.json")));
// Model servers may be on the LAN; news sources must be public internet addresses.
builder.Services.AddSingleton<IFetcher>(services => new Fetcher(publicOnly: false, services.GetRequiredService<ILogger<Fetcher>>()));
builder.Services.AddSingleton(services => new SourceFetcher(new Fetcher(publicOnly: true)));
builder.Services.AddSingleton<Reviewer>();
builder.Services.AddSingleton<Collector>();

// Session-cookie keys live in the data folder so sign-ins survive restarts even when the service
// account has no usable profile (systemd ProtectHome, Task Scheduler without a loaded profile).
// Windows uses machine-scope DPAPI: user-scope DPAPI fails when the account's profile isn't loaded.
builder.Services.AddDataProtection().SetApplicationName("NewsReview");
builder.Services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((options, services) =>
{
    var loggers = services.GetRequiredService<ILoggerFactory>();
    options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.Combine(DataDirectory(services), "keys")), loggers);
    if (OperatingSystem.IsWindows()) options.XmlEncryptor = new DpapiXmlEncryptor(protectToLocalMachine: true, loggers);
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "NewsReview.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/login.html";
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, cancel) => new ValueTask(
        Api.Write(context.HttpContext, new JsonObject { ["error"] = "Too many attempts. Try again in one minute." }, 429));
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

// Brotli or gzip for text responses (data, scripts, styles), so they cross the network several times smaller.
// HTTPS responses are left uncompressed, ASP.NET's default, which avoids compression side-channel attacks there.
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml", "application/manifest+json"]);
});

var app = builder.Build();
app.UseResponseCompression();
Store store;
PasswordStore passwords;
Collector collector;
try
{
    store = app.Services.GetRequiredService<Store>();
    passwords = app.Services.GetRequiredService<PasswordStore>();
    app.Services.GetRequiredService<ModelKeys>();
    collector = app.Services.GetRequiredService<Collector>();
}
catch (Exception error) when (error is StorageUnavailableException or InvalidOperationException or SqliteException)
{
    app.Logger.LogError("{Error}", error.Message);
    return 1;
}

if (passwords.SetupToken is { } setupToken)
{
    // Console only: the setup token is never sent through the file logger.
    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine("NEWSREVIEW FIRST-RUN SETUP TOKEN");
    Console.WriteLine();
    Console.WriteLine($"  {setupToken}");
    Console.WriteLine();
    Console.WriteLine("Enter this token on the Create Password screen.");
    Console.WriteLine("It changes each time NewsReview restarts until setup is complete.");
    Console.WriteLine("============================================================");
    Console.WriteLine();
}

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        return Task.CompletedTask;
    });
    await next();
});
// Cross-origin writes are refused before rate limiting, so they never use up login attempts.
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var host = context.Request.Host.Value ?? "";
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site" || (origin.Length > 0 && UrlTools.Split(origin).Netloc != host))
        {
            await Api.Write(context, Api.Error("Cross-origin writes are not allowed"), 403);
            return;
        }
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    var write = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
    if (!path.StartsWithSegments("/api"))
    {
        if (write) { await Api.Write(context, Api.Error("Method not allowed"), 405); return; }
        if ((path == "/" || path == "/index.html") && !authenticated) { context.Response.Redirect("/login.html"); return; }
        if (path == "/login.html" && authenticated) { context.Response.Redirect("/"); return; }
        await next();
        return;
    }
    try
    {
        var open = path == "/api/auth/status" || (path == "/api/auth/login" || path == "/api/auth/setup") && HttpMethods.IsPost(context.Request.Method);
        if (!open && !(authenticated && passwords.Configured))
        {
            await Api.Write(context, Api.Error("Sign in to continue"), 401);
            return;
        }
        if (!open && write && context.Request.Headers["X-NewsReview-Revision"] is { Count: > 0 } revision && revision.ToString() != store.Revision())
        {
            await Api.Write(context, new JsonObject { ["error"] = "Data changed after a restore or reset. Reload before making changes.", ["code"] = "revision_changed" }, 409);
            return;
        }
        await next();
    }
    catch (StorageUnavailableException error) { await Api.Write(context, new JsonObject { ["error"] = error.Message, ["code"] = "storage_unavailable" }, 503); }
    catch (ValidationException error) { await Api.Write(context, Api.Error(PyText.Truncate(error.Message, 500)), 400); }
    catch (SqliteException error) when (error.SqliteErrorCode == 19) { await Api.Write(context, Api.Error("That source URL is already in your list"), 409); }
    catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError(error, "Request failed");
        await Api.Write(context, Api.Error("An internal error occurred. See the server log."), 500);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

var version = Transfer.Version;

app.MapGet("/api/auth/status", (HttpContext context) => Api.Send(new JsonObject
{
    ["authenticated"] = context.User.Identity?.IsAuthenticated == true && passwords.Configured,
    ["configured"] = passwords.Configured, ["password_required"] = true, ["version"] = version,
}));
app.MapPost("/api/auth/setup", async (HttpContext context) =>
{
    var data = await Api.Body(context.Request);
    try { passwords.Setup(Api.OptionalString(data, "setupToken"), Api.OptionalString(data, "password"), Api.OptionalString(data, "confirmPassword")); }
    catch (SetupConflictException error) { return Api.Send(Api.Error(error.Message), 409); }
    catch (SetupTokenException error) { return Api.Send(Api.Error(error.Message), 401); }
    await Api.SignIn(context);
    return Api.Send(new JsonObject { ["ok"] = true });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (HttpContext context) =>
{
    var data = await Api.Body(context.Request);
    if (!passwords.Verify(Api.OptionalString(data, "password") ?? "", out var upgraded)) return Api.Send(Api.Error("Incorrect password"), 401);
    if (upgraded) app.Logger.LogInformation("Upgraded the saved password hash to {Iterations} PBKDF2 iterations.", PasswordStore.Iterations);
    await Api.SignIn(context);
    return Api.Send(new JsonObject { ["ok"] = true });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await Api.Body(context.Request);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Api.Send(new JsonObject { ["ok"] = true });
});

app.MapGet("/api/stories", (HttpRequest request, CancellationToken cancellation) =>
{
    string First(string key) => request.Query[key].Where(value => value is { Length: > 0 }).FirstOrDefault() ?? "";
    List<string> All(string key) => request.Query[key].Where(value => value is { Length: > 0 }).Select(value => value!).ToList();
    var values = new Dictionary<string, string> { ["day"] = First("day"), ["query"] = First("query"), ["start"] = First("start"), ["end"] = First("end"), ["tag"] = First("tag") };
    if (values.Values.Any(value => PyText.Length(value) > 500)) throw new ValidationException("Search is too long");
    foreach (var key in new[] { "day", "start", "end" }) if (values[key].Length > 0) Search.ParseIsoDate(values[key]);
    if (values["start"].Length > 0 && values["end"].Length > 0 && string.CompareOrdinal(values["start"], values["end"]) > 0)
        throw new ValidationException("Start date must be before end date");
    var dateText = First("date_range");
    var timeText = First("time_range");
    if (PyText.Length(dateText) > 100 || PyText.Length(timeText) > 100) throw new ValidationException("Date/time range is too long");
    if (dateText.Trim().Length > 0) (values["start"], values["end"]) = Search.DateRange(dateText);
    var (timeStart, timeEnd) = Search.TimeRange(timeText);
    var combined = First("period");
    if (PyText.Length(combined) > 200) throw new ValidationException("Date/time range is too long");
    if (combined.Trim().Length > 0) (values["start"], values["end"], timeStart, timeEnd) = Search.DateTimeRange(combined);
    // An explicit date range widens browsing; keywords and time-only filters keep the selected day.
    if (values["start"].Length > 0 || values["end"].Length > 0) values["day"] = "";
    var tags = All("tag");
    if (tags.Count > 100 || tags.Any(tag => PyText.Length(tag) > 60)) throw new ValidationException("Choose at most 100 valid topics");
    var excluded = All("exclude_relevance");
    if (excluded.Count > 4) throw new ValidationException("Choose at most four relevance filters");
    var pageText = First("page");
    var page = Validation.Integer(Api.ParseInteger(pageText.Length > 0 ? pageText : "1"), "Page", 1, 100000);
    var sort = First("sort");
    return Api.Send(store.Stories(new Store.StoryQuery
    {
        Day = values["day"], Query = values["query"], Start = values["start"], End = values["end"], Tags = tags,
        TimeStart = timeStart, TimeEnd = timeEnd, TimeZone = First("time_zone"), ExcludedRelevance = excluded,
        Bookmarks = request.Query["bookmarks"] is [ "1" ], ShowHidden = request.Query["hidden"] is [ "1" ], Page = page,
        Sort = sort.Length > 0 ? sort : "rank",
    }, cancellation));
});
app.MapGet("/api/export", () =>
{
    var output = Path.Combine(Path.GetTempPath(), "newsreview-download-" + Guid.NewGuid().ToString("N") + ".zip");
    try { Transfer.CreateArchive(store, output); }
    catch
    {
        File.Delete(output);
        throw;
    }
    var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.DeleteOnClose);
    return Results.Stream(stream, "application/zip", "newsreview-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip");
});
app.MapGet("/api/overview", () =>
{
    var overview = store.Overview();
    overview["collector"] = collector.Status();
    overview["today"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    overview["timezone"] = TimeZoneInfo.Local.Id;
    overview["version"] = version;
    return Api.Send(overview);
});
app.MapGet("/api/maintenance/duplicates", () => Api.Send(new JsonObject { ["count"] = store.DuplicatePurgeCount() }));
app.MapGet("/api/feedback", () =>
{
    var value = store.FeedbackState();
    var count = ((JsonObject)value["ratings"]!).Count;
    value.Remove("ratings");
    value["count"] = count;
    return Api.Send(value);
});
app.MapGet("/api/settings", () => Api.Send(store.Settings()));
app.MapGet("/api/topics", () => Api.Send(store.Topics()));
app.MapGet("/api/sources", () => Api.Send(store.Sources()));
app.MapGet("/api/related/{id}", (string id) => Api.Send(store.Related(Api.ParseInteger(id))));

app.MapPost("/api/import/backup", async (HttpContext context) =>
{
    var request = context.Request;
    if (request.Headers["X-NewsReview-Confirm"] != "RESTORE") throw new ValidationException("Confirm that this restore replaces the current dataset");
    if (Api.MediaType(request) != "application/zip" || request.Headers.TransferEncoding.Count > 0) throw new ValidationException("Upload a ZIP with a Content-Length header");
    var length = request.ContentLength ?? 0;
    if (length is <= 0 or > Transfer.MaxUpload) return Api.Send(Api.Error("Backup ZIP must be between 1 byte and 256 MiB"), 413);
    if (!collector.TryAcquire()) return Api.Send(Api.Error("Wait for collection/review to finish before restoring a backup"), 409);
    var directory = Directory.CreateTempSubdirectory("newsreview-upload-");
    try
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit) bodyLimit.MaxRequestBodySize = Transfer.MaxUpload;
        var upload = Path.Combine(directory.FullName, "upload.zip");
        await using (var destination = File.Create(upload))
        {
            var buffer = new byte[65536];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), context.RequestAborted);
                if (read == 0) throw new ValidationException("Upload ended before the complete ZIP was received");
                await destination.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                remaining -= read;
            }
        }
        var result = Transfer.RestoreArchive(store, upload);
        collector.Update(("message", "Backup restored. Reading history and preferences were reloaded."));
        return Api.Send(result);
    }
    finally
    {
        collector.Release();
        directory.Delete(recursive: true);
    }
});

// Runs work while holding the shared job lock, or answers 409 with the given explanation.
IResult Exclusive(string busy, Func<IResult> work)
{
    if (!collector.TryAcquire()) return Api.Send(Api.Error(busy), 409);
    try { return work(); }
    finally { collector.Release(); }
}

async Task<IResult> ExclusiveAsync(string busy, Func<Task<IResult>> work)
{
    if (!collector.TryAcquire()) return Api.Send(Api.Error(busy), 409);
    try { return await work(); }
    finally { collector.Release(); }
}

app.MapPost("/api/model-key/status", async (HttpRequest request) => Api.Send(collector.ModelKeys.Status(await Api.Body(request))));
app.MapPut("/api/model-key", async (HttpRequest request) =>
{
    var data = await Api.Body(request);
    return Api.Send(collector.ModelKeys.Save(data, Json.Get(data, "key")));
});
app.MapPost("/api/model-key/remove", async (HttpRequest request) =>
{
    var data = await Api.Body(request);
    if (!Api.Confirmed(data, "REMOVE MODEL KEY")) throw new ValidationException("Confirm removing the saved API key before continuing");
    return Api.Send(collector.ModelKeys.Remove(data));
});
app.MapPut("/api/topics", async (HttpRequest request) =>
{
    var data = await Api.Body(request);
    if (data["topics"] is not JsonArray list) throw new ValidationException("Send the topic list");
    var topics = list.Select(topic => Validation.Text(topic, "Topic", 60)).ToList();
    var renames = new Dictionary<string, string>(StringComparer.Ordinal);
    if (Json.Get(data, "renames", new JsonObject()) is not JsonObject renameObject) throw new ValidationException("Renames must map old to new topic names");
    foreach (var (from, to) in renameObject) renames[Validation.Text(JsonValue.Create(from), "Topic", 60)] = Validation.Text(to, "Topic", 60);
    var removalConfirmed = Api.Confirmed(data, "DELETE TOPIC");
    return Exclusive("Wait for collection/review or restore to finish before editing topics",
        () => Api.Send(store.SaveTopics(topics, renames, removalConfirmed)));
});
app.MapPost("/api/maintenance/duplicates/purge", async (HttpRequest request) =>
{
    if (!Api.Confirmed(await Api.Body(request), "PURGE DUPLICATES")) throw new ValidationException("Confirm duplicate purging before continuing");
    return Exclusive("Wait for collection/review or restore to finish before purging duplicates", () => Api.Send(store.PurgeDuplicates()));
});
app.MapPost("/api/reset", async (HttpRequest request) =>
{
    var data = await Api.Body(request);
    if (!Api.Confirmed(data, "RESET SELECTED")) throw new ValidationException("Type RESET SELECTED to confirm");
    return Exclusive("Wait for collection/review or restore to finish before resetting data", () =>
    {
        var result = store.Reset(data["selections"]);
        collector.Update(("message", "Selected data was reset. Other data and preferences were kept."));
        return Api.Send(result);
    });
});
app.MapPost("/api/read/{id}", async (string id, HttpRequest request) =>
{
    await Api.Body(request);
    return Api.Send(new JsonObject { ["read_at"] = store.MarkRead(Api.ParseInteger(id)) });
});
app.MapPut("/api/feedback/story/{id}", async (string id, HttpRequest request) =>
{
    var data = await Api.Body(request);
    var vote = Validation.Integer(data["vote"], "Vote", -1, 1);
    var reason = Validation.Text(Json.Get(data, "reason", ""), "Feedback reason", 320, empty: true);
    store.RateStory(Api.ParseInteger(id), vote, reason);
    return Api.Send(new JsonObject { ["ok"] = true });
});
app.MapPut("/api/feedback/profile", async (HttpRequest request) =>
{
    var data = await Api.Body(request);
    store.SaveFeedbackProfile(Validation.Text(data["profile"], "Learned preferences", 2400, empty: true),
        enabled: Validation.Boolean(data["enabled"], "Use feedback"), autoUpdate: Validation.Boolean(data["auto_update"], "Automatic profile updates"));
    return Api.Send(new JsonObject { ["ok"] = true });
});
app.MapPost("/api/feedback/rebuild", async (HttpRequest request) =>
{
    await Api.Body(request);
    return await ExclusiveAsync("Wait for the active job before updating learned preferences", async () =>
    {
        var value = store.FeedbackState();
        if (((JsonObject)value["ratings"]!).Count == 0) throw new ValidationException("Rate at least one article before updating learned preferences");
        var ai = (JsonObject)store.Settings()["ai"]!;
        var profile = await collector.Reviewer.LearnPreferences(ai, value, apiKey: collector.ModelKeys.Get(ai));
        var saved = store.SaveFeedbackProfile(profile, revision: value["revision"]!.AsLong());
        return Api.Send(new JsonObject { ["ok"] = saved, ["error"] = saved ? null : "Feedback changed during profile generation. Please update again." }, saved ? 200 : 409);
    });
});
app.MapPut("/api/bookmarks/{id}", async (string id, HttpRequest request) =>
{
    var data = await Api.Body(request);
    store.Bookmark(Api.ParseInteger(id), Validation.Boolean(data["bookmarked"], "Bookmarked"));
    return Api.Send(new JsonObject { ["ok"] = true });
});
app.MapPut("/api/settings", async (HttpRequest request) =>
{
    store.SaveSettings(Validation.Settings(await Api.Body(request), store.Settings()));
    return Api.Send(new JsonObject { ["ok"] = true });
});
app.MapPost("/api/sources", async (HttpRequest request) =>
    Api.Send(new JsonObject { ["id"] = store.SaveSource(Validation.Source(await Api.Body(request))) }, 201));
app.MapPut("/api/sources/{id}", async (string id, HttpRequest request) =>
    Api.Send(new JsonObject { ["id"] = store.SaveSource(Validation.Source(await Api.Body(request)), Api.ParseInteger(id)) }));
app.MapPost("/api/discover", async (HttpContext context) =>
{
    var url = Validation.Url((await Api.Body(context.Request))["url"]);
    return Api.Send(new JsonObject { ["feed_url"] = await Feeds.Discover(collector.Fetcher, url, context.RequestAborted) });
});
app.MapPost("/api/preview-source", async (HttpContext context) =>
{
    var source = Validation.Source(await Api.Body(context.Request));
    var result = await Sources.Read(collector.Fetcher, source, cancel: context.RequestAborted);
    return Api.Send(new JsonObject
    {
        ["entries"] = Api.Entries(result.Entries.Take(8)), ["method"] = result.Method, ["feed_url"] = result.FeedUrl, ["note"] = result.Note,
    });
});
app.MapPost("/api/test-model", async (HttpContext context) =>
{
    var config = (JsonObject)Validation.Settings(new JsonObject { ["ai"] = await Api.Body(context.Request) }, store.Settings())["ai"]!;
    return await ExclusiveAsync("Wait for collection/review or maintenance to finish before testing a model", async () =>
        Api.Send(await collector.Reviewer.Test(config, store.Topics().Select(t => (string)t!).ToList(), collector.ModelKeys.Get(config), context.RequestAborted)));
});
app.MapPost("/api/jobs/stop-review", async (HttpRequest request) =>
{
    await Api.Body(request);
    var stopped = collector.StopReview();
    return Api.Send(new JsonObject { ["ok"] = stopped, ["error"] = stopped ? null : "No AI review is currently running" }, stopped ? 202 : 409);
});
app.MapPost("/api/jobs/start", async (HttpRequest request) =>
{
    var data = await Api.Body(request);
    if (!Json.IsString(Json.Get(data, "kind", "collect"), out var kind) || kind is not ("collect" or "review")) throw new ValidationException("Unknown job type");
    var uncheckedOnly = Validation.Boolean(Json.Get(data, "unchecked_only", false), "Unchecked only");
    var sourceNode = Json.Get(data, "source_id", null);
    long? sourceId = sourceNode is null ? null : Validation.Integer(sourceNode, "Source", 1, long.MaxValue);
    if ((uncheckedOnly || sourceId is not null) && kind != "collect") throw new ValidationException("Source checks require a collection job");
    var started = collector.Start(kind, uncheckedOnly: uncheckedOnly, sourceId: sourceId);
    return Api.Send(new JsonObject { ["ok"] = started, ["error"] = started ? null : "A job is already running" }, started ? 202 : 409);
});
app.MapPost("/api/requeue", async (HttpRequest request) =>
{
    var data = await Api.Body(request);
    // The job lock keeps requeue and review decisions from racing.
    return Exclusive("Wait for the current collection to finish", () =>
        Api.Send(new JsonObject { ["count"] = store.Requeue(Validation.Boolean(Json.Get(data, "failed_only", true), "Failed only")) }));
});
app.MapFallback("/api/{**path}", () => Api.Send(Api.Error("Not found"), 404));

if (app.Configuration["NewsReview:Scheduler"] != "false")
    _ = Task.Run(() => collector.Schedule(app.Lifetime.ApplicationStopping));
app.Lifetime.ApplicationStopping.Register(collector.Stop);
app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("NewsReview {Version} at {Urls} · data: {Directory}", version, string.Join(", ", app.Urls), Path.GetDirectoryName(store.Path)));

app.Run();
return 0;

/// <summary>Request parsing and JSON responses shared by the API endpoints.</summary>
static class Api
{
    public static JsonObject Error(string message) => new() { ["error"] = message };

    public static IResult Send(JsonNode? body, int status = 200) =>
        Results.Content(body?.ToJsonString(Json.Compact) ?? "null", "application/json; charset=utf-8", Encoding.UTF8, status);

    public static async Task Write(HttpContext context, JsonNode body, int status)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(body.ToJsonString(Json.Compact));
    }

    public static string? MediaType(HttpRequest request) =>
        request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();

    /// <summary>A JSON object body of 1–64000 bytes with a Content-Length header.</summary>
    public static async Task<JsonObject> Body(HttpRequest request)
    {
        if (request.Headers.TransferEncoding.Count > 0) throw new ValidationException("Chunked request bodies are not supported");
        if (MediaType(request) != "application/json") throw new ValidationException("Use application/json");
        var length = request.ContentLength ?? 0;
        if (length is < 1 or > 64000) throw new ValidationException("Request body must be 1–64000 bytes");
        var buffer = new byte[length];
        await request.Body.ReadExactlyAsync(buffer, request.HttpContext.RequestAborted);
        JsonNode? value;
        try { value = JsonNode.Parse(buffer); }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw new ValidationException("Invalid JSON"); }
        return value as JsonObject ?? throw new ValidationException("Expected a JSON object");
    }

    public static string? OptionalString(JsonObject data, string key) => Json.IsString(data[key], out var text) ? text : null;

    public static bool Confirmed(JsonObject data, string phrase) => Json.IsString(data["confirmation"], out var text) && text == phrase;

    public static long ParseInteger(string text) =>
        long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ValidationException($"invalid literal for int() with base 10: '{text}'");

    public static JsonArray Entries(IEnumerable<Entry> entries) => new(entries.Select(entry => (JsonNode)new JsonObject
    {
        ["headline"] = entry.Headline, ["url"] = entry.Url, ["excerpt"] = entry.Excerpt, ["published_at"] = entry.PublishedAt,
    }).ToArray());

    public static async Task SignIn(HttpContext context)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(ClaimTypes.Name, "Owner")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, AllowRefresh = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    }
}
