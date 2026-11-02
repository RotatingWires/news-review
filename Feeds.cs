using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NewsReview;

/// <summary>A story candidate found in a feed or listing page.</summary>
sealed class Entry
{
    public required string Headline { get; set; }
    public required string Url { get; set; }
    public string Excerpt { get; set; } = "";
    public string? PublishedAt { get; set; }
}

/// <summary>Collects feed links, JSON-LD blocks and article paragraphs from a page.</summary>
sealed class PageParser : HtmlHandler
{
    static readonly HashSet<string> Void = ["meta", "link", "img", "br", "hr", "input", "source", "wbr", "area", "base", "embed", "param", "track", "col"];
    static readonly string[] Unread = ["script", "style", "nav", "footer", "header", "aside"];
    readonly List<string> stack = [];
    StringBuilder? current;
    StringBuilder? json;
    public List<string> Feeds { get; } = [];
    public List<string> JsonBlocks { get; } = [];
    public List<string> Semantic { get; } = [];

    public override void StartTag(string tag, IReadOnlyDictionary<string, string?> attrs)
    {
        if (tag == "link" && (Attr(attrs, "rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("alternate") &&
            ((Attr(attrs, "type") ?? "").Contains("rss") || (Attr(attrs, "type") ?? "").Contains("atom")))
            Feeds.Add(Attr(attrs, "href") ?? "");
        if (Void.Contains(tag)) return;
        stack.Add(tag);
        if (tag == "script" && Attr(attrs, "type") == "application/ld+json") json = new StringBuilder();
        if (tag == "p") current = new StringBuilder();
    }

    public override void EndTag(string tag)
    {
        if (tag == "script" && json is not null)
        {
            JsonBlocks.Add(json.ToString());
            json = null;
        }
        if (tag == "p" && current is not null)
        {
            var text = PyText.CollapseWhitespace(current.ToString());
            if (PyText.Length(text) >= 60 && (stack.Contains("article") || stack.Contains("main"))) Semantic.Add(text);
            current = null;
        }
        var index = stack.LastIndexOf(tag);
        if (index >= 0) stack.RemoveRange(index, stack.Count - index);
    }

    public override void Data(string text)
    {
        json?.Append(text);
        if (current is not null && !Unread.Any(stack.Contains)) current.Append(text);
    }

    internal static string? Attr(IReadOnlyDictionary<string, string?> attrs, string name) =>
        attrs.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Visible text of an HTML fragment, without scripts or styles.</summary>
sealed class PlainText : HtmlHandler
{
    readonly StringBuilder parts = new();
    int skip;

    public override void StartTag(string tag, IReadOnlyDictionary<string, string?> attrs)
    {
        if (tag is "script" or "style") skip++;
        if (tag is "p" or "br" or "div" or "li") parts.Append(' ');
    }

    public override void EndTag(string tag)
    {
        if (tag is "script" or "style") skip = Math.Max(0, skip - 1);
        if (tag is "p" or "div" or "li") parts.Append(' ');
    }

    public override void Data(string text)
    {
        if (skip == 0) parts.Append(text);
    }

    public string Text => parts.ToString();
}

static class Feeds
{
    public static string Plain(string? value)
    {
        var parser = new PlainText();
        parser.Feed(value ?? "");
        return PyText.CollapseWhitespace(HtmlTokenizer.Unescape(parser.Text));
    }

    /// <summary>A UTC ISO timestamp from an ISO 8601 or RFC 2822 date; null when unparseable.</summary>
    public static string? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        DateTimeOffset? parsed = null;
        var iso = PyText.ParseIso(value.Trim().Replace("Z", "+00:00"));
        if (iso is { } isoValue)
            parsed = Aware(isoValue.Local, isoValue.Offset);
        else if (Rfc2822(value) is { } rfc)
            parsed = Aware(rfc.Local, rfc.Offset);
        return parsed is null ? null : PyText.FormatSeconds(parsed.Value.ToUniversalTime());
    }

    // Naive feed dates are treated as UTC.
    static DateTimeOffset? Aware(DateTime local, TimeSpan? offset)
    {
        try { return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified) - (offset ?? TimeSpan.Zero), TimeSpan.Zero); }
        catch (ArgumentException) { return null; }
    }

    static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
    static readonly string[] DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    static readonly Dictionary<string, int> NamedZones = new()
    {
        ["UT"] = 0, ["UTC"] = 0, ["GMT"] = 0, ["Z"] = 0, ["AST"] = -400, ["ADT"] = -300, ["EST"] = -500, ["EDT"] = -400,
        ["CST"] = -600, ["CDT"] = -500, ["MST"] = -700, ["MDT"] = -600, ["PST"] = -800, ["PDT"] = -700,
    };

    /// <summary>email.utils._parsedate_tz, including its tolerance for common non-compliant dates.</summary>
    static (DateTime Local, TimeSpan? Offset)? Rfc2822(string value)
    {
        var data = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (data.Count == 0) return null;
        if (data[0].EndsWith(',') || DayNames.Contains(data[0].ToLowerInvariant())) data.RemoveAt(0);
        else
        {
            var comma = data[0].LastIndexOf(',');
            if (comma >= 0) data[0] = data[0][(comma + 1)..];
        }
        if (data.Count == 3)
        {
            var stuff = data[0].Split('-');
            if (stuff.Length == 3) data = [.. stuff, .. data.Skip(1)];
        }
        if (data.Count == 4)
        {
            var s = data[3];
            var sign = s.IndexOf('+');
            if (sign == -1) sign = s.IndexOf('-');
            if (sign > 0) { data[3] = s[..sign]; data.Add(s[sign..]); }
            else data.Add("");
        }
        if (data.Count < 5) return null;
        string dd = data[0], mm = data[1], yy = data[2], tm = data[3], tz = data[4];
        if (dd.Length == 0 || mm.Length == 0 || yy.Length == 0) return null;
        mm = mm.ToLowerInvariant();
        if (!MonthNames.Contains(mm))
        {
            (dd, mm) = (mm, dd.ToLowerInvariant());
            if (!MonthNames.Contains(mm)) return null;
        }
        var month = Array.IndexOf(MonthNames, mm) + 1;
        if (month > 12) month -= 12;
        if (dd.EndsWith(',')) dd = dd[..^1];
        if (yy.IndexOf(':') > 0) (yy, tm) = (tm, yy);
        if (yy.EndsWith(','))
        {
            yy = yy[..^1];
            if (yy.Length == 0) return null;
        }
        if (yy.Length == 0 || !char.IsDigit(yy[0])) (yy, tz) = (tz, yy);
        if (tm.EndsWith(','))
        {
            tm = tm[..^1];
            if (tm.Length == 0) return null;
        }
        var clock = tm.Split(':');
        if (clock.Length == 1 && tm.Contains('.')) clock = tm.Split('.');
        if (clock.Length is not (2 or 3)) return null;
        if (!int.TryParse(yy, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(dd, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(clock[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(clock[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minute))
            return null;
        var second = 0;
        if (clock.Length == 3 && !int.TryParse(clock[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out second)) return null;
        if (year < 100) year += year > 68 ? 1900 : 2000;
        int? zone = null;
        tz = tz.ToUpperInvariant();
        if (NamedZones.TryGetValue(tz, out var named)) zone = named;
        else if (int.TryParse(tz, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var numeric))
            zone = numeric == 0 && tz.StartsWith('-') ? null : numeric;
        TimeSpan? offset = null;
        if (zone is { } z)
        {
            var magnitude = Math.Abs(z);
            offset = TimeSpan.FromSeconds(Math.Sign(z) * ((magnitude / 100) * 3600 + (magnitude % 100) * 60));
        }
        try { return (new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified), offset); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static readonly Regex DeclarationPattern = new(@"<!\s*(DOCTYPE|ENTITY)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<Entry> ParseFeed(string text, string baseUrl)
    {
        if (DeclarationPattern.IsMatch(text)) throw new ValidationException("DTD/entity declarations are not accepted in feeds");
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XDocument.Load(reader).Root ?? throw new XmlException("No root element");
        }
        catch (XmlException) { throw new ValidationException("Not a valid RSS/Atom feed; check the feed URL"); }
        if (root.Name.LocalName is not ("rss" or "feed" or "RDF")) throw new ValidationException("Source returned a page instead of RSS/Atom");
        var result = new List<Entry>();
        foreach (var entry in root.DescendantsAndSelf())
        {
            if (entry.Name.LocalName is not ("item" or "entry")) continue;
            var fields = new Dictionary<string, string>();
            var link = "";
            foreach (var child in entry.Elements())
            {
                var key = child.Name.LocalName;
                fields[key] = child.Value;
                if (key == "link" && ((string?)child.Attribute("rel") ?? "alternate") == "alternate")
                    link = (string?)child.Attribute("href") is { Length: > 0 } href ? href : child.Value;
            }
            if (link.Length == 0) link = fields.GetValueOrDefault("guid", "");
            var title = PyText.Truncate(Plain(fields.GetValueOrDefault("title", "")), 500);
            string url;
            try { url = UrlTools.Canonical(UrlTools.Join(baseUrl, link.Trim())); }
            catch (ValidationException) { continue; }
            if (title.Length == 0 || link.Length == 0) continue;
            var body = First(fields, "encoded", "content", "description", "summary");
            result.Add(new Entry
            {
                Headline = title, Url = url, Excerpt = PyText.Truncate(Plain(body), 16000),
                PublishedAt = ParseDate(First(fields, "pubDate", "published", "date", "updated")),
            });
        }
        return result;
    }

    // Python's `a or b or c`: the first non-empty field.
    static string First(Dictionary<string, string> fields, params string[] keys) =>
        keys.Select(key => fields.GetValueOrDefault(key, "")).FirstOrDefault(value => value.Length > 0) ?? "";

    public static async Task<string> Discover(IFetcher fetcher, string url, CancellationToken cancel = default)
    {
        var page = await fetcher.Request(url, cancel: cancel);
        try
        {
            ParseFeed(page.Text, page.Url);
            return page.Url;
        }
        catch (ValidationException)
        {
            var parser = new PageParser();
            parser.Feed(page.Text);
            if (parser.Feeds.Count == 0)
                throw new ValidationException("No advertised RSS/Atom feed found. Use Automatic or Website page mode to collect from a news listing, or enter a known feed URL.");
            return UrlTools.Canonical(UrlTools.Join(page.Url, parser.Feeds[0]));
        }
    }

    static string JsonBody(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("articleBody", out var body) && body.ValueKind == JsonValueKind.String && PyText.Length(body.GetString()!) >= 300)
                return Plain(body.GetString());
            foreach (var child in value.EnumerateObject())
            {
                var found = JsonBody(child.Value);
                if (found.Length > 0) return found;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                var found = JsonBody(child);
                if (found.Length > 0) return found;
            }
        }
        return "";
    }

    /// <summary>Readable article text from JSON-LD or semantic paragraphs; empty when the page is not confidently an article.</summary>
    public static string ExtractArticle(string text)
    {
        var parser = new PageParser();
        parser.Feed(text);
        foreach (var block in parser.JsonBlocks)
        {
            try
            {
                using var document = JsonDocument.Parse(block);
                var body = JsonBody(document.RootElement);
                if (body.Length > 0) return body;
            }
            catch (JsonException) { }
        }
        // Do not treat navigation/cookie/paywall text as article content.
        var article = string.Join("\n\n", parser.Semantic.Distinct());
        var opening = PyText.Truncate(article.ToLowerInvariant(), 800);
        if (PyText.Length(article) < 300 || opening.Contains("verify you are human") || opening.Contains("enable javascript and cookies")) return "";
        return article;
    }
}
