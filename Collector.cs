using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NewsReview;

/// <summary>Fetches news sources; only public internet addresses are allowed in production.</summary>
sealed class SourceFetcher(IFetcher inner)
{
    public IFetcher Fetcher { get; } = inner;
}

/// <summary>One bounded background queue shared by scheduled and manual collection.</summary>
sealed class Collector(Store store, SourceFetcher sources, Reviewer reviewer, ModelKeys modelKeys, ILogger<Collector> logger)
{
    static readonly Regex Noise = new(@"\b(sponsored|coupon|promo code|best deals|daily deals)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    readonly SemaphoreSlim jobLock = new(1, 1);
    readonly CancellationTokenSource stopping = new();
    readonly Lock stateGate = new();
    readonly JsonObject state = new()
    {
        ["running"] = false, ["message"] = "Ready", ["kind"] = null, ["reviewing"] = false, ["review_stop_requested"] = false,
    };
    volatile bool reviewStop;

    public Reviewer Reviewer => reviewer;
    public ModelKeys ModelKeys => modelKeys;
    public IFetcher Fetcher => sources.Fetcher;

    /// <summary>Hold the shared job lock for a maintenance action; false when collection, review or another action is active.</summary>
    public bool TryAcquire() => jobLock.Wait(0);

    public void Release() => jobLock.Release();

    public JsonObject Status()
    {
        lock (stateGate) return (JsonObject)state.DeepClone();
    }

    public void Update(params (string Key, JsonNode? Value)[] values)
    {
        lock (stateGate)
            foreach (var (key, value) in values) state[key] = value;
    }

    string Message() => (string)Status()["message"]!;

    /// <summary>Starts a job; a manual collection can be limited to unchecked sources or to one enabled source.</summary>
    public bool Start(string kind = "collect", bool scheduled = false, bool uncheckedOnly = false, long? sourceId = null)
    {
        if ((uncheckedOnly || sourceId is not null) && (kind != "collect" || scheduled)) throw new ValidationException("Source checks must be a manual collection");
        if (uncheckedOnly && sourceId is not null) throw new ValidationException("Check new sources or one source, not both");
        // Validate storage synchronously so a start request cannot be acknowledged for a job
        // which can only fail because its data folder was removed.
        store.Settings();
        if (sourceId is not null)
        {
            var source = store.Sources().Cast<JsonObject>().FirstOrDefault(s => s["id"]!.AsLong() == sourceId) ?? throw new ValidationException("Source not found");
            if (source["enabled"]!.AsLong() == 0) throw new ValidationException("Enable the source before checking it");
        }
        if (!jobLock.Wait(0)) return false;
        if (scheduled && !ScheduleDue(DateTimeOffset.Now))
        {
            jobLock.Release();
            return false;
        }
        reviewStop = false;
        Update(("running", true), ("kind", kind), ("reviewing", false), ("review_stop_requested", false), ("message", "Starting…"));
        _ = Task.Run(() => Run(kind, uncheckedOnly, sourceId));
        return true;
    }

    public bool StopReview()
    {
        lock (stateGate)
        {
            if (!state["running"]!.GetValue<bool>() || !state["reviewing"]!.GetValue<bool>()) return false;
            reviewStop = true;
            state["review_stop_requested"] = true;
            state["message"] = "Stopping AI review after the current article; remaining stories stay queued.";
            return true;
        }
    }

    async Task Run(string kind, bool uncheckedOnly, long? sourceId)
    {
        var scope = sourceId is not null ? "source" : uncheckedOnly ? "unchecked" : "all";
        long? runId = null;
        var modelUsed = false;
        JsonObject? ai = null;
        var errors = new JsonArray();
        var details = new JsonObject
        {
            ["added"] = 0, ["reviewed"] = 0, ["duplicates"] = 0, ["filtered"] = 0, ["errors"] = errors,
            ["scope"] = scope, ["sources_checked"] = 0,
        };
        var cancel = stopping.Token;
        try
        {
            runId = store.StartRun(kind, details);
            var settings = store.Settings();
            var config = (JsonObject)settings["collection"]!;
            var now = DateTimeOffset.Now;
            var cutoff = now.ToUniversalTime().AddDays(-config["lookback_days"]!.AsLong());
            var sourceIds = new List<long>();
            if (kind == "collect")
            {
                var enabled = store.Sources().Cast<JsonObject>()
                    .Where(s => s["enabled"]!.AsLong() != 0 && (!uncheckedOnly || s["last_checked"] is null) &&
                                (sourceId is null || s["id"]!.AsLong() == sourceId)).ToList();
                sourceIds = enabled.Select(s => s["id"]!.AsLong()).ToList();
                if (sourceId is not null) details["source"] = enabled.Select(s => (string)s["name"]!).FirstOrDefault() ?? "";
                var number = 0;
                foreach (var source in enabled)
                {
                    if (cancel.IsCancellationRequested) break;
                    number++;
                    Increment(details, "sources_checked");
                    var name = (string)source["name"]!;
                    Update(("message", $"Checking {name} ({number}/{enabled.Count})"));
                    try
                    {
                        var result = await Sources.Read(sources.Fetcher, source, cancel: cancel);
                        // Newest first, keeping source order for undated items.
                        var entries = result.Entries.OrderByDescending(item => item.PublishedAt ?? "", StringComparer.Ordinal).ToList();
                        var eligible = entries.Where(item => item.PublishedAt is null || PyText.ParseInstant(item.PublishedAt) >= cutoff);
                        long added = 0;
                        foreach (var item in eligible.Take((int)config["items_per_source"]!.AsLong()))
                            if (store.InsertStory(source["id"]!.AsLong(), item, (string)source["category"]!, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) added++;
                        Increment(details, "added", added);
                        var newest = entries.Select(item => item.PublishedAt).Where(value => value is not null).Max(StringComparer.Ordinal);
                        store.SourceChecked(source["id"]!.AsLong(), feedUrl: result.FeedUrl, count: entries.Count, newest: newest,
                            added: added, method: result.Method, note: result.Note);
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !cancel.IsCancellationRequested)
                    {
                        logger.LogWarning("Source {Name} failed: {Error}", name, error.Message);
                        store.SourceChecked(source["id"]!.AsLong(), PyText.Truncate(error.Message, 500));
                        errors.Add($"{name}: {PyText.Truncate(error.Message, 300)}");
                    }
                    try { await Task.Delay(250, cancel); }
                    catch (OperationCanceledException) { break; }
                }
            }
            ai = (JsonObject)settings["ai"]!;
            if (((string)ai["model"]!).Trim().Length == 0)
                details["note"] = "No model configured. Stories are saved with source excerpts; configure AI and run Review pending.";
            else
                modelUsed = await Review(ai, config, scope != "all" ? sourceIds : null, details, errors, cancel);
            Update(("reviewing", false));
            if (reviewStop)
            {
                details["review_stopped"] = true;
                details["note"] = "AI review stopped by the user. The current article finished; remaining stories are still queued.";
            }
            var status = cancel.IsCancellationRequested ? "interrupted" : errors.Count > 0 || reviewStop ? "partial" : "complete";
            store.FinishRun(runId.Value, status, details);
            var checkedPrefix = scope switch
            {
                "unchecked" => $"Checked {details["sources_checked"]} new sources · ",
                "source" => $"Checked {details["source"]} · ",
                _ => "",
            };
            Update(("message", checkedPrefix +
                $"Saved {details["added"]} new stories · {details["reviewed"]} reviewed" +
                (errors.Count > 0 ? " · See run details for errors" : "") +
                (reviewStop ? " · AI review stopped; remaining stories are queued" : "")));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Collection failed");
            if (runId is { } id)
            {
                errors.Add(PyText.Truncate(error.Message, 500));
                try { store.FinishRun(id, "failed", details); }
                catch (StorageUnavailableException) { }
            }
            Update(("message", error is StorageUnavailableException ? error.Message : "Collection failed. See run history and server log."));
        }
        finally
        {
            if (modelUsed && ai is not null)
            {
                try { await reviewer.Unload(ai); }
                catch (Exception error)
                {
                    logger.LogWarning("Ollama model unload failed: {Error}", error.Message);
                    Update(("message", Message() + " · Model unload failed; see logs/console.log"));
                }
            }
            Update(("running", false), ("reviewing", false), ("review_stop_requested", false));
            jobLock.Release();
        }
    }

    async Task<bool> Review(JsonObject ai, JsonObject config, List<long>? sourceIds, JsonObject details, JsonArray errors, CancellationToken cancel)
    {
        var modelUsed = false;
        var recent = store.Recent();
        var pending = store.Pending(config["max_reviews"]!.AsLong(), sourceIds);
        var consecutiveFailures = 0;
        Update(("reviewing", pending.Count > 0));
        var feedback = store.FeedbackState();
        if (pending.Count > 0 && feedback["enabled"]!.GetValue<bool>() && feedback["auto_update"]!.GetValue<bool>() &&
            ((JsonObject)feedback["ratings"]!).Count > 0 && feedback["profile_revision"]!.AsLong() != feedback["revision"]!.AsLong())
        {
            Update(("message", "Updating learned preferences from recent feedback"));
            modelUsed = true;
            try
            {
                var profile = await reviewer.LearnPreferences(ai, feedback, keepLoaded: true, apiKey: modelKeys.Get(ai), cancel: cancel);
                store.SaveFeedbackProfile(profile, revision: feedback["revision"]!.AsLong());
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                logger.LogWarning("Feedback profile update failed: {Error}", error.Message);
                errors.Add("Feedback profile update failed; existing preferences kept: " + PyText.Truncate(error.Message, 200));
            }
        }
        var editorialInterests = Feedback.ForModel((JsonObject)store.Settings()["interests"]!);
        var topics = store.Topics().Select(t => (string)t!).ToList();
        var number = 0;
        foreach (var story in pending)
        {
            if (cancel.IsCancellationRequested || reviewStop) break;
            number++;
            var id = story["id"]!.AsLong();
            var headline = (string)story["headline"]!;
            Update(("message", $"Reviewing {number}/{pending.Count} · {headline}"));
            var text = (string)story["article_text"]!;
            var basis = (string)story["content_basis"]!;
            string? articleError = null;
            if (text.Length == 0)
            {
                try
                {
                    var page = await sources.Fetcher.Request((string)story["url"]!, cancel: cancel);
                    text = Feeds.ExtractArticle(page.Text);
                    basis = text.Length > 0 ? "article" : "excerpt";
                }
                catch (ValidationException error)
                {
                    basis = "excerpt";
                    articleError = PyText.Truncate(error.Message, 200);
                }
                if (text.Length == 0) text = (string)story["excerpt"]!;
            }
            var stored = PyText.Truncate(text, 100000);
            try
            {
                if (text.Trim().Length == 0)
                {
                    // Keep why the article failed: a blocked or timed-out page needs a different fix than an empty one.
                    var unreadable = (articleError is null ? "Article page had no readable text" : $"Article could not be read ({articleError})") +
                        " and the source gave no excerpt; showing headline and link only";
                    store.ReviewFailed(id, unreadable, stored, basis);
                    errors.Add($"Review #{id}: {unreadable}");
                    continue;
                }
                JsonObject result;
                var category = (string?)story["category"] ?? "";
                if (Noise.IsMatch(headline))
                    result = new JsonObject
                    {
                        ["summary"] = "Excluded by the headline filter.",
                        ["tags"] = new JsonArray(topics.Where(t => PyText.FoldEquals(t, category)).Take(1).Select(t => (JsonNode)t).ToArray()),
                        ["status"] = "filtered", ["score"] = 0, ["milestone"] = "not applicable",
                    };
                else
                {
                    var candidates = Reviewer.CandidatesFor(headline, recent);
                    modelUsed = true;
                    result = await reviewer.Review(ai, editorialInterests, new JsonObject
                    {
                        ["headline"] = headline, ["text"] = PyText.Truncate(text, (int)ai["max_input_chars"]!.AsLong()),
                        ["content_basis"] = basis, ["published_at"] = story["published_at"]?.DeepClone(), ["category"] = story["category"]?.DeepClone(),
                    }, candidates, topics, keepLoaded: true, apiKey: modelKeys.Get(ai), cancel: cancel);
                }
                store.ReviewResult(id, result, ai, editorialInterests, stored, basis);
                var status = (string)result["status"]!;
                Increment(details, status switch { "reviewed" => "reviewed", "duplicate" => "duplicates", _ => "filtered" });
                if (status == "reviewed")
                {
                    recent.Insert(0, new JsonObject { ["id"] = id, ["headline"] = headline, ["summary"] = result["summary"]?.DeepClone() });
                    if (recent.Count > 150) recent.RemoveRange(150, recent.Count - 150);
                }
                consecutiveFailures = 0;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancel.IsCancellationRequested)
            {
                store.ReviewFailed(id, PyText.Truncate(error.Message, 500), stored, basis);
                errors.Add($"Review #{id}: {PyText.Truncate(error.Message, 300)}");
                if (++consecutiveFailures >= 3)
                {
                    details["note"] = "Stopped review after three consecutive model-review failures. Check the run errors and AI settings; remaining stories are queued.";
                    break;
                }
            }
        }
        return modelUsed;
    }

    static void Increment(JsonObject details, string key, long amount = 1) => details[key] = details[key]!.AsLong() + amount;

    /// <summary>Due when the interval has elapsed since the last full collection (or history reset), in UTC.</summary>
    public bool ScheduleDue(DateTimeOffset now)
    {
        var config = (JsonObject)store.Settings()["collection"]!;
        if (!config["enabled"]!.GetValue<bool>()) return false;
        var (lastCollection, historyCleared) = store.ScheduleAnchors();
        var anchors = new[] { lastCollection, historyCleared }.Where(value => value is not null).Select(value => PyText.ParseInstant(value)!.Value).ToList();
        if (anchors.Count == 0) return true;
        var interval = config["interval_hours"]?.AsLong() ?? 24;
        return now.ToUniversalTime() - anchors.Max() >= TimeSpan.FromHours(interval);
    }

    /// <summary>
    /// Elapsed UTC time survives restarts and daylight-saving changes. Missed intervals trigger one
    /// catch-up run, without replaying a backlog.
    /// </summary>
    public async Task Schedule(CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, stopping.Token);
        while (true)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), linked.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (ScheduleDue(DateTimeOffset.Now)) Start(scheduled: true);
            }
            catch (StorageUnavailableException error) { Update(("message", error.Message)); }
            catch (Exception error) { logger.LogError(error, "Scheduled collection check failed"); }
        }
    }

    public void Stop() => stopping.Cancel();
}
