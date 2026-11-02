using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NewsReview;

/// <summary>
/// URL splitting, joining and canonicalization with Python urllib.parse semantics.
/// Stored story URLs are unique keys, so canonical forms must stay byte-identical to
/// those written by the earlier Python release; System.Uri normalizes differently.
/// </summary>
static class UrlTools
{
    public readonly record struct Parts(string Scheme, string Netloc, string Path, string Query, string Fragment)
    {
        (string Host, string? Port) HostInfo
        {
            get
            {
                var at = Netloc.LastIndexOf('@');
                var hostinfo = at >= 0 ? Netloc[(at + 1)..] : Netloc;
                var open = hostinfo.IndexOf('[');
                string host, port;
                if (open >= 0)
                {
                    var bracketed = hostinfo[(open + 1)..];
                    var close = bracketed.IndexOf(']');
                    host = close >= 0 ? bracketed[..close] : bracketed;
                    var rest = close >= 0 ? bracketed[(close + 1)..] : "";
                    var colon = rest.IndexOf(':');
                    port = colon >= 0 ? rest[(colon + 1)..] : "";
                }
                else
                {
                    var colon = hostinfo.IndexOf(':');
                    host = colon >= 0 ? hostinfo[..colon] : hostinfo;
                    port = colon >= 0 ? hostinfo[(colon + 1)..] : "";
                }
                return (host, port.Length == 0 ? null : port);
            }
        }

        public string? Hostname
        {
            get
            {
                var host = HostInfo.Host;
                if (host.Length == 0) return null;
                var percent = host.IndexOf('%');
                return percent < 0 ? host.ToLowerInvariant() : host[..percent].ToLowerInvariant() + host[percent..];
            }
        }

        /// <summary>Throws ValidationException for a non-numeric or out-of-range port.</summary>
        public int? Port
        {
            get
            {
                var port = HostInfo.Port;
                if (port is null) return null;
                if (!port.All(c => c is >= '0' and <= '9') || !int.TryParse(port, out var value) || value > 65535)
                    throw new ValidationException("Invalid port");
                return value;
            }
        }

        public bool HasCredentials
        {
            get
            {
                var at = Netloc.LastIndexOf('@');
                if (at < 0) return false;
                var userinfo = Netloc[..at];
                var colon = userinfo.IndexOf(':');
                return colon >= 0 ? colon > 0 || userinfo.Length > colon + 1 : userinfo.Length > 0;
            }
        }
    }

    const string SchemeChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.";
    static readonly HashSet<string> UsesRelative = ["", "ftp", "http", "gopher", "nntp", "imap", "wais", "file", "https", "shttp", "mms", "prospero", "rtsp", "rtsps", "rtspu", "sftp", "svn", "svn+ssh", "ws", "wss"];
    static readonly HashSet<string> UsesNetloc = ["", "ftp", "http", "gopher", "nntp", "telnet", "imap", "wais", "file", "mms", "https", "shttp", "snews", "prospero", "rtsp", "rtsps", "rtspu", "rsync", "svn", "svn+ssh", "sftp", "nfs", "git", "git+ssh", "ws", "wss", "itms-services"];

    public static Parts Split(string url, string scheme = "")
    {
        url = url.TrimStart(Enumerable.Range(0, 33).Select(i => (char)i).ToArray());
        url = url.Replace("\t", "").Replace("\r", "").Replace("\n", "");
        string netloc = "", query = "", fragment = "";
        var colon = url.IndexOf(':');
        if (colon > 0 && url[0] < 128 && char.IsLetter(url[0]) && url[..colon].All(c => SchemeChars.Contains(c)))
        {
            scheme = url[..colon].ToLowerInvariant();
            url = url[(colon + 1)..];
        }
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            var delimiter = url.Length;
            foreach (var c in "/?#")
            {
                var found = url.IndexOf(c, 2);
                if (found >= 0) delimiter = Math.Min(delimiter, found);
            }
            netloc = url[2..delimiter];
            url = url[delimiter..];
            if (netloc.Contains('[') != netloc.Contains(']')) throw new ValidationException("Invalid IPv6 URL");
            if (netloc.Contains('['))
            {
                var inside = netloc[(netloc.IndexOf('[') + 1)..netloc.IndexOf(']')];
                var address = inside.Split('%')[0];
                if (!(inside.StartsWith('v') || inside.StartsWith('V')) &&
                    !(IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
                    throw new ValidationException("Invalid IPv6 URL");
            }
        }
        var hash = url.IndexOf('#');
        if (hash >= 0) { fragment = url[(hash + 1)..]; url = url[..hash]; }
        var question = url.IndexOf('?');
        if (question >= 0) { query = url[(question + 1)..]; url = url[..question]; }
        return new Parts(scheme, netloc, url, query, fragment);
    }

    public static string Unsplit(string scheme, string netloc, string path, string query, string fragment)
    {
        var url = path;
        if (netloc.Length > 0)
        {
            if (url.Length > 0 && url[0] != '/') url = "/" + url;
            url = "//" + netloc + url;
        }
        else if (url.StartsWith("//", StringComparison.Ordinal)) url = "//" + url;
        else if (scheme.Length > 0 && UsesNetloc.Contains(scheme) && (url.Length == 0 || url[0] == '/')) url = "//" + url;
        if (scheme.Length > 0) url = scheme + ":" + url;
        if (query.Length > 0) url += "?" + query;
        if (fragment.Length > 0) url += "#" + fragment;
        return url;
    }

    public static string Join(string baseUrl, string url)
    {
        if (baseUrl.Length == 0) return url;
        if (url.Length == 0) return baseUrl;
        var b = Split(baseUrl);
        var u = Split(url, b.Scheme);
        if (u.Scheme != b.Scheme || !UsesRelative.Contains(u.Scheme)) return url;
        var netloc = u.Netloc;
        if (UsesNetloc.Contains(u.Scheme))
        {
            if (netloc.Length > 0) return Unsplit(u.Scheme, netloc, u.Path, u.Query, u.Fragment);
            netloc = b.Netloc;
        }
        if (u.Path.Length == 0)
            return Unsplit(u.Scheme, netloc, b.Path, u.Query.Length == 0 ? b.Query : u.Query, u.Fragment);

        var baseParts = b.Path.Split('/').ToList();
        if (baseParts[^1] != "") baseParts.RemoveAt(baseParts.Count - 1);
        List<string> segments;
        if (u.Path.StartsWith('/')) segments = u.Path.Split('/').ToList();
        else
        {
            segments = baseParts.Concat(u.Path.Split('/')).ToList();
            // Drop empty interior segments that would otherwise create redundant slashes.
            if (segments.Count > 2)
                segments = [segments[0], .. segments.Skip(1).Take(segments.Count - 2).Where(s => s.Length > 0), segments[^1]];
        }
        var resolved = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == "..") { if (resolved.Count > 0) resolved.RemoveAt(resolved.Count - 1); }
            else if (segment != ".") resolved.Add(segment);
        }
        if (segments[^1] is "." or "..") resolved.Add("");
        var path = string.Join('/', resolved);
        return Unsplit(u.Scheme, netloc, path.Length == 0 ? "/" : path, u.Query, u.Fragment);
    }

    /// <summary>network.valid_url: HTTP(S), a hostname, no credentials, a numeric port, no spaces/controls.</summary>
    public static string Valid(string? value)
    {
        if (value is null || value.Length > 2048 || value.Any(c => c < 33)) throw new ValidationException("Enter a valid HTTP or HTTPS URL");
        var parts = Split(value);
        if (parts.Scheme is not ("http" or "https") || parts.Hostname is null || parts.HasCredentials)
            throw new ValidationException("Use an HTTP or HTTPS URL without embedded credentials");
        _ = parts.Port;
        return value;
    }

    /// <summary>network.canonical_url: drop fragments and tracking parameters; re-encode the query.</summary>
    public static string Canonical(string value)
    {
        Valid(value);
        var p = Split(value);
        var query = ParseQueryPairs(p.Query)
            .Where(pair => !pair.Name.ToLowerInvariant().StartsWith("utm_", StringComparison.Ordinal) &&
                           pair.Name.ToLowerInvariant() is not ("fbclid" or "gclid" or "mc_cid" or "mc_eid"))
            .Select(pair => QuotePlus(pair.Name) + "=" + QuotePlus(pair.Value));
        return Unsplit(p.Scheme.ToLowerInvariant(), p.Netloc.ToLowerInvariant(), p.Path.Length == 0 ? "/" : p.Path, string.Join('&', query), "");
    }

    /// <summary>parse_qsl(query, keep_blank_values=True).</summary>
    public static List<(string Name, string Value)> ParseQueryPairs(string query)
    {
        var result = new List<(string, string)>();
        if (query.Length == 0) return result;
        foreach (var field in query.Split('&'))
        {
            if (field.Length == 0) continue;
            var equals = field.IndexOf('=');
            var name = equals >= 0 ? field[..equals] : field;
            var value = equals >= 0 ? field[(equals + 1)..] : "";
            result.Add((Unquote(name.Replace('+', ' ')), Unquote(value.Replace('+', ' '))));
        }
        return result;
    }

    static readonly Regex PercentRun = new("(?:%[0-9A-Fa-f]{2})+", RegexOptions.CultureInvariant);

    public static string Unquote(string value) =>
        value.Contains('%')
            ? PercentRun.Replace(value, match => Encoding.UTF8.GetString(Enumerable.Range(0, match.Length / 3)
                .Select(i => Convert.ToByte(match.Value.Substring(i * 3 + 1, 2), 16)).ToArray()))
            : value;

    public static string QuotePlus(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-' or '~') builder.Append(c);
            else if (c == ' ') builder.Append('+');
            else builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
