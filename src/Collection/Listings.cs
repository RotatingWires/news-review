using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewsReview;

/// <summary>Extract article candidates from downloaded listing HTML without executing scripts.</summary>
static class Listings
{
    sealed class Card
    {
        public string? Date;
        public List<string> Paragraphs { get; } = [];
        public bool HasHeadline;
    }

    sealed class Link
    {
        public required string Href;
        public StringBuilder Text { get; } = new();
        public string Title = "";
        public bool Main;
        public bool Heading;
        public bool Semantic;
        public Card? Card;
    }

    sealed class ListingParser : HtmlHandler
    {
        static readonly HashSet<string> Void = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"];
        static readonly HashSet<string> Skip = ["nav", "footer", "aside", "script", "style", "form"];
        static readonly string[] Headings = ["h1", "h2", "h3", "h4"];
        readonly List<string> stack = [];
        List<(int Depth, Card Card)> cards = [];
        Link? anchor;
        StringBuilder? paragraph;
        public List<Link> Links { get; } = [];

        public override void StartTag(string tag, IReadOnlyDictionary<string, string?> attrs)
        {
            if (tag == "article") cards.Add((stack.Count, new Card()));
            if (tag == "time" && cards.Count > 0 && PageParser.Attr(attrs, "datetime") is { Length: > 0 } datetime)
                cards[^1].Card.Date = Feeds.ParseDate(datetime);
            if (tag == "a" && PageParser.Attr(attrs, "href") is { Length: > 0 } href && !stack.Any(Skip.Contains))
            {
                var inHeading = stack.Any(t => Headings.Contains(t));
                var heading = inHeading || (PageParser.Attr(attrs, "rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("bookmark")
                    || PageParser.Attr(attrs, "itemprop") == "headline";
                if (heading && cards.Count > 0) cards[^1].Card.HasHeadline = true;
                anchor = new Link
                {
                    Href = href, Title = PageParser.Attr(attrs, "title") ?? "", Main = stack.Contains("main"), Heading = heading,
                    Semantic = cards.Count > 0 || inHeading, Card = cards.Count > 0 ? cards[^1].Card : null,
                };
            }
            if (Headings.Contains(tag) && anchor is not null)
            {
                anchor.Semantic = true;
                anchor.Heading = true;
                if (anchor.Card is not null) anchor.Card.HasHeadline = true;
            }
            if (tag == "p" && cards.Count > 0) paragraph = new StringBuilder();
            if (!Void.Contains(tag)) stack.Add(tag);
        }

        public override void EndTag(string tag)
        {
            if (tag == "a" && anchor is not null)
            {
                Links.Add(anchor);
                anchor = null;
            }
            if (tag == "p" && paragraph is not null)
            {
                if (cards.Count > 0) cards[^1].Card.Paragraphs.Add(paragraph.ToString());
                paragraph = null;
            }
            var depth = stack.LastIndexOf(tag);
            if (depth >= 0)
            {
                stack.RemoveRange(depth, stack.Count - depth);
                cards = cards.Where(card => card.Depth < depth).ToList();
            }
        }

        public override void Data(string text)
        {
            if (stack.Any(Skip.Contains)) return;
            anchor?.Text.Append(text);
            paragraph?.Append(text);
        }
    }

    sealed record Candidate(JsonElement? Url, JsonElement? Headline, JsonElement? Excerpt, string? PublishedAt)
    {
        public static Candidate FromLink(string url, string headline, string excerpt, string? published) =>
            new(Element(url), Element(headline), Element(excerpt), published);

        static JsonElement Element(string value) => JsonSerializer.SerializeToElement(value);
    }

    static readonly string[] ArticleTypes = ["NewsArticle", "Article", "BlogPosting", "DiscussionForumPosting"];

    // Python's `a or b`: the first value that is present and truthy.
    static JsonElement? Or(params JsonElement?[] values) =>
        values.FirstOrDefault(value => value is { } v && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined)
            && !(v.ValueKind == JsonValueKind.String && v.GetString()!.Length == 0)
            && !(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0)
            && !(v.ValueKind == JsonValueKind.Object && !v.EnumerateObject().Any())
            && !(v.ValueKind == JsonValueKind.Number && v.GetDouble() == 0)) ?? values[^1];

    static JsonElement? Get(JsonElement value, string name) => value.TryGetProperty(name, out var found) ? found : null;

    static IEnumerable<Candidate> Structured(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var kind = Get(value, "@type");
            var kinds = kind is { ValueKind: JsonValueKind.String } single ? [single.GetString()!]
                : kind is { ValueKind: JsonValueKind.Array } many ? many.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!).ToArray()
                : [];
            if (kinds.Any(ArticleTypes.Contains))
            {
                var url = Or(Get(value, "url"), Get(value, "mainEntityOfPage"));
                if (url is { ValueKind: JsonValueKind.Object } page) url = Or(Get(page, "@id"), Get(page, "url"));
                var published = Get(value, "datePublished") is { ValueKind: JsonValueKind.String } date ? Feeds.ParseDate(date.GetString()) : null;
                yield return new Candidate(url, Or(Get(value, "headline"), Get(value, "name")), Get(value, "description") ?? JsonSerializer.SerializeToElement(""), published);
            }
            if (kind is { ValueKind: JsonValueKind.String } listItem && listItem.GetString() == "ListItem")
            {
                var item = Get(value, "item") ?? value;
                if (item.ValueKind == JsonValueKind.Object)
                    yield return new Candidate(Get(item, "url"), Or(Get(item, "headline"), Get(item, "name")),
                        Get(item, "description") ?? JsonSerializer.SerializeToElement(""), null);
            }
            foreach (var child in value.EnumerateObject())
                foreach (var found in Structured(child.Value)) yield return found;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
                foreach (var found in Structured(child)) yield return found;
        }
    }

    static readonly Regex NonArticlePath = new(@"/(?:tags?|categor(?:y|ies)|author|login|sign-?in|subscribe|privacy|contact|about|search)(?:/|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<Entry> Parse(string text, string baseUrl, string linkPattern = "")
    {
        var parser = new ListingParser();
        parser.Feed(text);
        var metadata = new PageParser();
        metadata.Feed(text);
        var candidates = new List<Candidate>();
        var documents = new List<JsonDocument>();
        try
        {
            foreach (var block in metadata.JsonBlocks)
            {
                try
                {
                    var document = JsonDocument.Parse(block);
                    documents.Add(document);
                    candidates.AddRange(Structured(document.RootElement));
                }
                catch (JsonException) { }
                catch (InvalidOperationException) { }
            }
            var links = parser.Links.Any(link => link.Main) ? parser.Links.Where(link => link.Main).ToList() : parser.Links;
            var hasCards = links.Any(link => link.Card is not null);
            foreach (var link in links)
            {
                if (link.Card is { HasHeadline: true } && !link.Heading) continue;
                // With article cards available, do not turn unrelated sidebar headings into stories.
                // An explicit URL filter can opt into another page layout.
                if (hasCards && link.Card is null && linkPattern.Length == 0) continue;
                if (!link.Semantic && linkPattern.Length == 0) continue;
                var headline = link.Text.ToString().Trim();
                candidates.Add(Candidate.FromLink(link.Href, headline.Length > 0 ? headline : link.Title,
                    string.Join(' ', link.Card?.Paragraphs ?? []), link.Card?.Date));
            }
            var host = UrlTools.Split(baseUrl).Hostname;
            var baseCanonical = UrlTools.Canonical(baseUrl);
            var results = new Dictionary<string, Entry>();
            foreach (var item in candidates.Take(3000))
            {
                if (item.Url is not { ValueKind: JsonValueKind.String } urlValue || item.Headline is not { ValueKind: JsonValueKind.String } headlineValue) continue;
                string url;
                try { url = UrlTools.Canonical(UrlTools.Join(baseUrl, urlValue.GetString()!)); }
                catch (ValidationException) { continue; }
                var parts = UrlTools.Split(url);
                var headline = PyText.Truncate(Feeds.Plain(headlineValue.GetString()), 500);
                if (parts.Hostname != host || url == baseCanonical || PyText.Length(headline) < 12) continue;
                if (linkPattern.Length > 0 && !url.Contains(linkPattern, StringComparison.Ordinal)) continue;
                if (NonArticlePath.IsMatch(parts.Path)) continue;
                var excerpt = item.Excerpt is { ValueKind: JsonValueKind.String } excerptValue ? PyText.Truncate(Feeds.Plain(excerptValue.GetString()), 16000) : "";
                if (!results.TryGetValue(url, out var existing))
                    results[url] = new Entry { Url = url, Headline = headline, Excerpt = excerpt, PublishedAt = item.PublishedAt };
                else
                {
                    if (existing.PublishedAt is null && item.PublishedAt is not null) existing.PublishedAt = item.PublishedAt;
                    if (existing.Excerpt.Length == 0 && excerpt.Length > 0) existing.Excerpt = excerpt;
                }
            }
            if (results.Count == 0)
                throw new ValidationException("No article links found in the downloaded page. Try a news/category listing and an Article URL filter. JavaScript-only pages need a custom adapter.");
            return results.Values.ToList();
        }
        finally
        {
            foreach (var document in documents) document.Dispose();
        }
    }
}
