using System.Text.Json.Nodes;

namespace NewsReview;

/// <summary>What one source check found: the entries, the method that worked, the feed address, and a note for the source list.</summary>
sealed record SourceResult(List<Entry> Entries, string Method, string? FeedUrl, string Note);

/// <summary>Feed-first collection with website checks for failed or stale Automatic feeds.</summary>
static class Sources
{
    // Source health reports these same observations from stored dates, so it omits its own copy when a note leads with one.
    public static string StaleFeedReason(long days) => $"Feed's newest publication is at least {days} days old";
    public static string UndatedFeedReason(long days) => $"Undated feed has no newly observed stories for at least {days} days";

    static string StaleReason(List<Entry> entries, JsonObject source, DateTimeOffset now)
    {
        var days = source["stale_days"]?.AsLong() ?? 14;
        var dates = entries.Where(item => item.PublishedAt is not null).Select(item => PyText.ParseInstant(item.PublishedAt)!.Value).ToList();
        if (entries.Count == 0) return "Feed returned no story entries";
        if (dates.Count > 0 && (now - dates.Max()).TotalSeconds >= days * 86400)
            return StaleFeedReason(days);
        if (dates.Count == 0)
        {
            var observed = (string?)source["last_new_story"] ?? (string?)source["first_checked"];
            if (observed is not null && (now - PyText.ParseInstant(observed)!.Value).TotalSeconds >= days * 86400)
                return UndatedFeedReason(days);
        }
        return "";
    }

    static async Task<SourceResult> FeedResult(IFetcher fetcher, JsonObject source, List<Entry> entries, string feedUrl,
        FetchResult? page, DateTimeOffset now, CancellationToken cancel)
    {
        var result = new SourceResult(entries, "feed", feedUrl, "");
        if (((string?)source["mode"] ?? "auto") != "auto") return result;
        var reason = StaleReason(entries, source, now);
        if (reason.Length == 0) return result;
        List<Entry> website;
        try
        {
            page ??= await fetcher.Request((string)source["url"]!, cancel: cancel);
            website = Listings.Parse(page.Text, page.Url, (string?)source["link_pattern"] ?? "");
        }
        catch (ValidationException error)
        {
            var detail = PyText.Truncate(error.Message, 500).TrimEnd('.');
            return result with { Note = $"{reason}. Website fallback could not be read: {detail}. Retaining available feed entries." };
        }
        // Website order leads previews; preserve feed dates/excerpts when the page omits them and keep
        // feed-only stories. Existing URL history still deduplicates.
        var merged = new Dictionary<string, Entry>();
        foreach (var item in website) merged[item.Url] = Copy(item);
        foreach (var item in entries)
        {
            if (!merged.TryGetValue(item.Url, out var existing)) merged[item.Url] = Copy(item);
            else
            {
                existing.PublishedAt ??= item.PublishedAt;
                if (existing.Excerpt.Length == 0) existing.Excerpt = item.Excerpt;
            }
        }
        return new SourceResult(merged.Values.ToList(), "feed+page", feedUrl,
            $"{reason}. Website listing also checked; combined website and feed entries.");
    }

    static Entry Copy(Entry item) => new() { Headline = item.Headline, Url = item.Url, Excerpt = item.Excerpt, PublishedAt = item.PublishedAt };

    public static async Task<SourceResult> Read(IFetcher fetcher, JsonObject source, DateTimeOffset? now = null, CancellationToken cancel = default)
    {
        var moment = now ?? DateTimeOffset.UtcNow;
        var mode = (string?)source["mode"] ?? "auto";
        var feedUrl = (string?)source["feed_url"] ?? "";
        var url = (string)source["url"]!;
        FetchResult? page = null;
        string? feedError = null;
        if (mode != "page")
        {
            try
            {
                if (feedUrl.Length == 0)
                {
                    page = await fetcher.Request(url, cancel: cancel);
                    List<Entry>? direct = null;
                    try { direct = Feeds.ParseFeed(page.Text, page.Url); }
                    catch (ValidationException)
                    {
                        var parser = new PageParser();
                        parser.Feed(page.Text);
                        if (parser.Feeds.Count == 0) throw new ValidationException("No RSS/Atom link advertised by the website");
                        feedUrl = UrlTools.Canonical(UrlTools.Join(page.Url, parser.Feeds[0]));
                    }
                    if (direct is not null) return await FeedResult(fetcher, source, direct, page.Url, null, moment, cancel);
                }
                var feed = await fetcher.Request(feedUrl, cancel: cancel);
                return await FeedResult(fetcher, source, Feeds.ParseFeed(feed.Text, feed.Url), feedUrl, page, moment, cancel);
            }
            catch (ValidationException error)
            {
                if (mode == "feed") throw;
                feedError = error.Message;
            }
        }
        page ??= await fetcher.Request(url, cancel: cancel);
        List<Entry> entries;
        try { entries = Listings.Parse(page.Text, page.Url, (string?)source["link_pattern"] ?? ""); }
        catch (ValidationException error)
        {
            var prefix = feedError is not null ? $"Feed: {feedError}. " : "";
            throw new ValidationException(prefix + error.Message);
        }
        var note = feedError is not null && ((string?)source["feed_url"] ?? "").Length > 0 ? $"Using website-page collection. Feed unavailable: {feedError}" : "";
        return new SourceResult(entries, "page", null, note);
    }
}

/// <summary>Source health describes observations, not a claim that a publisher is dead.</summary>
static class Health
{
    public static JsonObject For(JsonObject source, DateTimeOffset? now = null)
    {
        var moment = now ?? DateTimeOffset.UtcNow;
        var threshold = source["stale_days"]!.AsLong();
        var warnings = new JsonArray();
        double Age(string? value) => value is null ? 0 : Math.Max(0, (moment - PyText.ParseInstant(value)!.Value).TotalSeconds / 86400);
        var lastError = (string?)source["last_error"];
        var failures = source["consecutive_failures"]!.AsLong();
        if (source["enabled"]!.AsLong() == 0) return new JsonObject { ["status"] = "disabled", ["warnings"] = new JsonArray() };
        if (!string.IsNullOrEmpty(lastError)) warnings.Add($"Last check failed ({failures} consecutive): {lastError}");
        var lastChecked = (string?)source["last_checked"];
        if (string.IsNullOrEmpty(lastChecked)) return new JsonObject { ["status"] = "unchecked", ["warnings"] = new JsonArray("Refresh sources to test it.") };
        if (Age(lastChecked) >= threshold) warnings.Add($"Not checked in at least {threshold} days; source health is stale.");
        var newest = (string?)source["newest_published"];
        var note = (string?)source["collection_note"] ?? "";
        var noteReportsAge = note.StartsWith(Sources.StaleFeedReason(threshold), StringComparison.Ordinal) ||
                             note.StartsWith(Sources.UndatedFeedReason(threshold), StringComparison.Ordinal);
        if (!noteReportsAge)
        {
            if (!string.IsNullOrEmpty(newest) && Age(newest) >= threshold)
                warnings.Add($"Newest reported publication is over {threshold} days old; this source may be inactive or its feed may be stale.");
            else if (string.IsNullOrEmpty(newest) && Age(NullIfEmpty((string?)source["last_new_story"]) ?? (string?)source["first_checked"]) >= threshold)
                warnings.Add($"No new stories observed for at least {threshold} days. Publication dates are unavailable; activity is uncertain.");
        }
        if (source["last_count"] is { } count && count.AsLong() == 0 && string.IsNullOrEmpty(lastError))
            warnings.Add("The last check returned no story entries.");
        if (note.Length > 0) warnings.Add(note);
        var status = failures >= 3 ? "broken" : !string.IsNullOrEmpty(lastError) ? "error" : warnings.Count > 0 ? "warning" : "healthy";
        return new JsonObject { ["status"] = status, ["warnings"] = warnings };
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
