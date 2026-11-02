using System.Globalization;
using System.Net;
using System.Text;

namespace NewsReview;

/// <summary>
/// A tolerant tag/text tokenizer modeled on Python's html.parser.HTMLParser, which the article,
/// feed-discovery and listing extractors were written against. It never builds a DOM or runs scripts.
/// </summary>
abstract class HtmlHandler
{
    public virtual void StartTag(string tag, IReadOnlyDictionary<string, string?> attrs) { }
    public virtual void EndTag(string tag) { }
    public virtual void Data(string text) { }

    /// <summary>Tokenize <paramref name="html"/>; text and attribute values have character references decoded.</summary>
    public void Feed(string html)
    {
        var i = 0;
        var text = new StringBuilder();
        string? rawElement = null;

        void FlushText()
        {
            if (text.Length == 0) return;
            Data(rawElement is null ? HtmlTokenizer.Unescape(text.ToString()) : text.ToString());
            text.Clear();
        }

        while (i < html.Length)
        {
            if (rawElement is not null)
            {
                // script/style content is raw text up to the matching end tag.
                var end = IndexOfEndTag(html, rawElement, i);
                text.Append(html, i, (end < 0 ? html.Length : end) - i);
                FlushText();
                if (end < 0) return;
                i = end;
                rawElement = null;
                continue;
            }
            var lt = html.IndexOf('<', i);
            if (lt < 0) { text.Append(html, i, html.Length - i); break; }
            text.Append(html, i, lt - i);
            i = lt;
            if (i + 1 >= html.Length) { text.Append('<'); i++; break; }
            var next = html[i + 1];
            if (char.IsAsciiLetter(next))
            {
                FlushText();
                i = ParseStartTag(html, i, out var tag, out var attrs, out var selfClosing);
                StartTag(tag, attrs);
                if (selfClosing) EndTag(tag);
                else if (tag is "script" or "style") rawElement = tag;
            }
            else if (next == '/')
            {
                var close = html.IndexOf('>', i + 2);
                if (close < 0) { text.Append(html, i, html.Length - i); i = html.Length; break; }
                FlushText();
                var name = html[(i + 2)..close].Trim();
                var cut = name.IndexOfAny([' ', '\t', '\n', '\r', '\f', '/']);
                if (cut >= 0) name = name[..cut];
                if (name.Length > 0 && char.IsAsciiLetter(name[0])) EndTag(name.ToLowerInvariant());
                i = close + 1;
            }
            else if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                FlushText();
                var close = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
            }
            else if (next is '!' or '?')
            {
                FlushText();
                var close = html.IndexOf('>', i + 2);
                i = close < 0 ? html.Length : close + 1;
            }
            else { text.Append('<'); i++; }
        }
        FlushText();
    }

    static int IndexOfEndTag(string html, string tag, int start)
    {
        var position = start;
        while ((position = html.IndexOf("</", position, StringComparison.Ordinal)) >= 0)
        {
            if (position + 2 + tag.Length <= html.Length &&
                string.Compare(html, position + 2, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var after = position + 2 + tag.Length;
                if (after == html.Length || html[after] is '>' or ' ' or '\t' or '\n' or '\r' or '\f' or '/') return position;
            }
            position += 2;
        }
        return -1;
    }

    static int ParseStartTag(string html, int start, out string tag, out Dictionary<string, string?> attrs, out bool selfClosing)
    {
        var i = start + 1;
        var nameStart = i;
        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('/' or '>')) i++;
        tag = html[nameStart..i].ToLowerInvariant();
        attrs = new Dictionary<string, string?>(StringComparer.Ordinal);
        selfClosing = false;
        while (i < html.Length)
        {
            while (i < html.Length && (char.IsWhiteSpace(html[i]) || (html[i] == '/' && (i + 1 >= html.Length || html[i + 1] != '>')))) i++;
            if (i >= html.Length) break;
            if (html[i] == '>') { i++; return i; }
            if (html[i] == '/') { selfClosing = true; i += 2; return i; }
            var attrStart = i;
            i++;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('/' or '=' or '>')) i++;
            var name = html[attrStart..i].ToLowerInvariant();
            var probe = i;
            while (probe < html.Length && char.IsWhiteSpace(html[probe])) probe++;
            string? value = null;
            if (probe < html.Length && html[probe] == '=')
            {
                i = probe;
                while (i < html.Length && html[i] == '=') i++;
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var quote = html[i];
                    var close = html.IndexOf(quote, i + 1);
                    value = close < 0 ? html[(i + 1)..] : html[(i + 1)..close];
                    i = close < 0 ? html.Length : close + 1;
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>') i++;
                    value = html[valueStart..i];
                }
                value = HtmlTokenizer.Unescape(value);
            }
            attrs[name] = value;
        }
        return i;
    }
}

static class HtmlTokenizer
{
    static readonly string[] Windows1252 = [
        "€", "\u0081", "‚", "ƒ", "„", "…", "†", "‡", "ˆ", "‰", "Š", "‹", "Œ", "\u008d", "Ž", "\u008f",
        "\u0090", "‘", "’", "“", "”", "•", "–", "—", "˜", "™", "š", "›", "œ", "\u009d", "ž", "Ÿ"];

    /// <summary>html.unescape: numeric references follow the HTML5 replacement rules; named references use the HTML entity table.</summary>
    public static string Unescape(string value)
    {
        if (!value.Contains('&')) return value;
        var builder = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var amp = value.IndexOf('&', i);
            if (amp < 0) { builder.Append(value, i, value.Length - i); break; }
            builder.Append(value, i, amp - i);
            i = amp;
            if (i + 1 < value.Length && value[i + 1] == '#')
            {
                var j = i + 2;
                var hex = j < value.Length && value[j] is 'x' or 'X';
                if (hex) j++;
                var digitsStart = j;
                while (j < value.Length && (hex ? char.IsAsciiHexDigit(value[j]) : char.IsAsciiDigit(value[j]))) j++;
                if (j > digitsStart)
                {
                    var digits = value[digitsStart..j];
                    var number = long.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : long.MaxValue;
                    builder.Append(CodePoint(number));
                    i = j < value.Length && value[j] == ';' ? j + 1 : j;
                    continue;
                }
            }
            else
            {
                var j = i + 1;
                while (j < value.Length && char.IsAsciiLetterOrDigit(value[j])) j++;
                if (j > i + 1)
                {
                    var withSemicolon = j < value.Length && value[j] == ';';
                    var entity = value[i..j] + ";";
                    var decoded = WebUtility.HtmlDecode(entity);
                    if (decoded != entity && (withSemicolon || value[(i + 1)..j] is "amp" or "lt" or "gt" or "quot" or "nbsp" or "copy" or "reg"))
                    {
                        builder.Append(decoded);
                        i = withSemicolon ? j + 1 : j;
                        continue;
                    }
                }
            }
            builder.Append('&');
            i++;
        }
        return builder.ToString();
    }

    static string CodePoint(long number)
    {
        if (number is >= 0x80 and <= 0x9f) return Windows1252[number - 0x80];
        if (number == 0 || number > 0x10FFFF || number is >= 0xD800 and <= 0xDFFF) return "�";
        return char.ConvertFromUtf32((int)number);
    }
}
