using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewsReview;

record FetchResult(string Text, string Url, string ContentType);

interface IFetcher
{
    Task<FetchResult> Request(string url, JsonNode? payload = null, int timeoutSeconds = 25, int limit = 3_000_000,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancel = default);

    async Task<JsonNode?> Json(string url, JsonNode payload, int timeoutSeconds = 180,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancel = default)
    {
        var response = await Request(url, payload, timeoutSeconds, 1_000_000, headers, cancel);
        try { return JsonNode.Parse(response.Text); }
        catch (JsonException) { throw new ValidationException("Model server returned invalid JSON"); }
    }
}

/// <summary>
/// Bounded HTTP transport. A public fetcher only connects to global internet addresses, checked on the
/// address actually dialed and again for every redirect. The model fetcher may reach LAN servers.
/// </summary>
sealed class Fetcher : IFetcher, IDisposable
{
    public static readonly string UserAgent = $"NewsReview/{typeof(Fetcher).Assembly.GetName().Version?.ToString(3)} (+self-hosted personal news reader)";
    const int MaxRedirects = 10;
    readonly bool publicOnly;
    readonly HttpClient client;
    readonly ILogger? logger;

    static Fetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public Fetcher(bool publicOnly, ILogger? logger = null)
    {
        this.publicOnly = publicOnly;
        this.logger = logger;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (publicOnly) handler.ConnectCallback = ConnectPublic;
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => client.Dispose();

    static async ValueTask<Stream> ConnectPublic(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var addresses = await ResolvePublic(context.DnsEndPoint.Host, cancel);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static async Task<IPAddress[]> ResolvePublic(string host, CancellationToken cancel)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancel);
        }
        catch (SocketException) { throw new ValidationException($"Cannot resolve news source hostname: {host}"); }
        if (addresses.Length == 0 || addresses.Any(address => !IsGlobal(address)))
            throw new ValidationException("News sources must use public internet addresses");
        return addresses;
    }

    /// <summary>network.check_public, used before each request and redirect so errors name the problem.</summary>
    public static async Task CheckPublic(string url, CancellationToken cancel = default)
    {
        UrlTools.Valid(url);
        await ResolvePublic(UrlTools.Split(url).Hostname!, cancel);
    }

    public async Task<FetchResult> Request(string url, JsonNode? payload = null, int timeoutSeconds = 25, int limit = 3_000_000,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancel = default)
    {
        UrlTools.Valid(url);
        if (publicOnly) await CheckPublic(url, cancel);
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Accept"] = "application/atom+xml, application/rss+xml, text/html, application/json",
        };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) requestHeaders[name] = value;
        var body = payload?.ToJsonString();
        var hostname = UrlTools.Split(url).Hostname;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var current = url;
        var method = body is null ? HttpMethod.Get : HttpMethod.Post;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(method, current);
                foreach (var (name, value) in requestHeaders)
                    if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation(name, value);
                if (method == HttpMethod.Post && body is not null)
                    request.Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (redirects >= MaxRedirects) throw new ValidationException($"HTTP {status} from {UrlTools.Split(current).Hostname}");
                    var next = UrlTools.Join(current, location.OriginalString);
                    UrlTools.Valid(next);
                    if (requestHeaders.ContainsKey("Authorization") && Origin(current) != Origin(next))
                        throw new ValidationException("Authenticated model requests cannot redirect to a different server");
                    if (publicOnly) await CheckPublic(next, timeout.Token);
                    if (method == HttpMethod.Post && status is 307 or 308) throw new ValidationException($"HTTP {status} from {UrlTools.Split(current).Hostname}");
                    if (status is not (307 or 308)) method = HttpMethod.Get;
                    current = next;
                    continue;
                }
                if (status is < 200 or >= 300)
                    throw new ValidationException(await HttpError(response, status, hostname, body is not null, requestHeaders, timeout.Token));
                var content = await ReadLimited(response.Content, limit, timeout.Token);
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/plain";
                return new FetchResult(Decode(content, charset), current, mediaType);
            }
        }
        catch (ValidationException) { throw; }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new ValidationException($"Cannot reach {hostname}: timed out");
        }
        catch (HttpRequestException error) when (error.InnerException is ValidationException inner) { throw inner; }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException)
        {
            var reason = error.InnerException?.Message ?? error.Message;
            throw new ValidationException(Redact($"Cannot reach {hostname}: {reason}", requestHeaders));
        }
    }

    async Task<string> HttpError(HttpResponseMessage response, int status, string? hostname, bool hasPayload,
        Dictionary<string, string> requestHeaders, CancellationToken cancel)
    {
        var message = $"HTTP {status} from {hostname}";
        // Model APIs return useful JSON errors; never surface arbitrary publisher HTML or request bodies.
        var modelRequest = hasPayload && !publicOnly;
        if (!modelRequest) return message;
        try
        {
            var bytes = await ReadLimited(response.Content, 8192, cancel, truncate: true);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                JsonElement detail = default;
                var found = root.TryGetProperty("error", out detail) || root.TryGetProperty("message", out detail);
                if (found && detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("message", out var nested)) detail = nested;
                if (found && detail.ValueKind == JsonValueKind.String)
                {
                    var text = new string(detail.GetString()!.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray());
                    text = Redact(PyText.CollapseWhitespace(text), requestHeaders);
                    if (text.Length > 0) message += ": " + PyText.Truncate(text, 240);
                }
            }
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException) { }
        logger?.LogWarning("Model server request failed: {Message}", message);
        return message;
    }

    static async Task<byte[]> ReadLimited(HttpContent content, int limit, CancellationToken cancel, bool truncate = false)
    {
        await using var stream = await content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                if (truncate) return buffer.ToArray()[..limit];
                throw new ValidationException("Response exceeds the download size limit");
            }
        }
        return buffer.ToArray();
    }

    static string Decode(byte[] content, string? charset)
    {
        Encoding encoding;
        try { encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset) : Encoding.UTF8; }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(content);
    }

    static (string, string?, int) Origin(string url)
    {
        var parts = UrlTools.Split(url);
        return (parts.Scheme.ToLowerInvariant(), parts.Hostname, parts.Port ?? (parts.Scheme == "https" ? 443 : 80));
    }

    static string Redact(string message, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (!name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || value.Length == 0) continue;
            message = message.Replace(value, "[redacted]");
            var space = value.IndexOf(' ');
            if (space >= 0 && space + 1 < value.Length) message = message.Replace(value[(space + 1)..], "[redacted]");
        }
        return message;
    }

    static readonly (string Network, int Prefix)[] PrivateV4 = [
        ("0.0.0.0", 8), ("10.0.0.0", 8), ("127.0.0.0", 8), ("169.254.0.0", 16), ("172.16.0.0", 12), ("192.0.0.0", 24),
        ("192.0.0.170", 31), ("192.0.2.0", 24), ("192.168.0.0", 16), ("198.18.0.0", 15), ("198.51.100.0", 24),
        ("203.0.113.0", 24), ("240.0.0.0", 4), ("255.255.255.255", 32), ("100.64.0.0", 10)];
    static readonly (string Network, int Prefix)[] PrivateV4Exceptions = [("192.0.0.9", 32), ("192.0.0.10", 32)];
    static readonly (string Network, int Prefix)[] PrivateV6 = [
        ("::1", 128), ("::", 128), ("::ffff:0:0", 96), ("64:ff9b:1::", 48), ("100::", 64), ("2001::", 23), ("2001:db8::", 32),
        ("2002::", 16), ("3fff::", 20), ("fc00::", 7), ("fe80::", 10)];
    static readonly (string Network, int Prefix)[] PrivateV6Exceptions = [
        ("2001:1::1", 128), ("2001:1::2", 128), ("2001:3::", 32), ("2001:4:112::", 48), ("2001:20::", 28), ("2001:30::", 28)];

    /// <summary>Python ipaddress is_global: not a private, shared, documentation, or reserved range.</summary>
    public static bool IsGlobal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var (ranges, exceptions) = address.AddressFamily == AddressFamily.InterNetwork ? (PrivateV4, PrivateV4Exceptions) : (PrivateV6, PrivateV6Exceptions);
        return !(ranges.Any(range => Contains(range, address)) && !exceptions.Any(range => Contains(range, address)));
    }

    static bool Contains((string Network, int Prefix) range, IPAddress address)
    {
        var network = IPAddress.Parse(range.Network).GetAddressBytes();
        var bytes = address.GetAddressBytes();
        if (network.Length != bytes.Length) return false;
        for (var bit = 0; bit < range.Prefix; bit++)
        {
            var mask = 0x80 >> (bit % 8);
            if ((network[bit / 8] & mask) != (bytes[bit / 8] & mask)) return false;
        }
        return true;
    }
}
