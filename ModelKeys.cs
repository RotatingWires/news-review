using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewsReview;

/// <summary>Write-only UI credentials, stored separately from reading data and backups.</summary>
sealed class ModelKeys
{
    const int MaxFileBytes = 1_000_000;
    const string EnvironmentKey = "NEWSREVIEW_MODEL_API_KEY";
    readonly string path;
    readonly Func<string, string?> environment;
    readonly Lock gate = new();
    // Ordinal order, matching the file layout written by Python's sorted().
    static readonly Comparer<(string, string)> Ordinal = Comparer<(string, string)>.Create((a, b) =>
        string.CompareOrdinal(a.Item1, b.Item1) is var first and not 0 ? first : string.CompareOrdinal(a.Item2, b.Item2));
    SortedDictionary<(string Provider, string BaseUrl), string> records;

    public ModelKeys(string path, Func<string, string?>? environment = null)
    {
        this.path = path;
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        records = Load();
    }

    public static string ValidateKey(JsonNode? value)
    {
        if (!Json.IsString(value, out var text)) throw new ValidationException("Enter an API key");
        text = text.Trim();
        if (text.Length is < 1 or > 4096 || text.Any(c => c is < (char)33 or > (char)126))
            throw new ValidationException("API keys must contain 1–4096 visible ASCII characters without spaces");
        return text;
    }

    /// <summary>A key belongs to a provider and normalized server base URL (scheme, host, non-default port, path).</summary>
    public static (string Provider, string BaseUrl) Scope(JsonObject config)
    {
        if (!Json.IsString(config["provider"], out var provider) || provider is not ("compatible" or "openrouter"))
            throw new ValidationException("Choose a compatible server or OpenRouter to configure an API key");
        var parts = UrlTools.Split(Validation.Url(config["base_url"]));
        if (parts.Query.Length > 0 || parts.Fragment.Length > 0) throw new ValidationException("Use a model server base URL without a query or fragment");
        var host = parts.Hostname!.ToLowerInvariant();
        if (host.Contains(':')) host = "[" + host + "]";
        if (parts.Port is { } port && port != (parts.Scheme == "https" ? 443 : 80)) host += ":" + port;
        return (provider, UrlTools.Unsplit(parts.Scheme.ToLowerInvariant(), host, parts.Path.TrimEnd('/'), "", ""));
    }

    SortedDictionary<(string, string), string> Load()
    {
        var result = new SortedDictionary<(string, string), string>(Ordinal);
        if (!File.Exists(path)) return result;
        const string unreadable = "Cannot read model-credentials.json. Restore a valid credentials file or move it aside before restarting.";
        try
        {
            var content = File.ReadAllBytes(path);
            if (content.Length > MaxFileBytes) throw new ValidationException("Oversized credentials file");
            if (JsonNode.Parse(content) is not JsonObject value || value.Count != 2 || !Json.IsInteger(value["version"], out var version) ||
                version != 1 || value["keys"] is not JsonArray keys)
                throw new ValidationException("Invalid credentials format");
            foreach (var item in keys)
            {
                if (item is not JsonObject entry || entry.Count != 3 || !entry.ContainsKey("provider") || !entry.ContainsKey("base_url") || !entry.ContainsKey("key"))
                    throw new ValidationException("Invalid credentials entry");
                var scope = Scope(entry);
                if (!result.TryAdd(scope, ValidateKey(entry["key"]))) throw new ValidationException("Duplicate credentials entry");
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ValidationException or InvalidOperationException)
        {
            throw new InvalidOperationException(unreadable);
        }
    }

    void Write(SortedDictionary<(string, string), string> updated)
    {
        var document = new JsonObject
        {
            ["version"] = 1,
            ["keys"] = new JsonArray(updated.Select(pair => (JsonNode)new JsonObject
            {
                ["provider"] = pair.Key.Item1, ["base_url"] = pair.Key.Item2, ["key"] = pair.Value,
            }).ToArray()),
        };
        var content = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions(Json.Compact) { WriteIndented = true }));
        if (content.Length > MaxFileBytes) throw new ValidationException("The saved model credentials exceed the file size limit");
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, ".model-credentials-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ValidationException("Cannot save the API key. Check the data folder and its write permissions.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        records = updated;
    }

    public string Get(JsonObject config)
    {
        if ((string?)config["provider"] == "ollama") return "";
        var scope = Scope(config);
        lock (gate)
        {
            var value = records.GetValueOrDefault(scope) is { Length: > 0 } saved ? saved : environment(EnvironmentKey) ?? "";
            return value.Length > 0 ? ValidateKey(value) : "";
        }
    }

    public JsonObject Status(JsonObject config)
    {
        var scope = Scope(config);
        lock (gate)
        {
            var saved = records.ContainsKey(scope);
            var fromEnvironment = !string.IsNullOrEmpty(environment(EnvironmentKey));
            return new JsonObject
            {
                ["configured"] = saved || fromEnvironment, ["saved"] = saved, ["environment"] = fromEnvironment,
                ["source"] = saved ? "saved" : fromEnvironment ? "environment" : "none",
            };
        }
    }

    public JsonObject Save(JsonObject config, JsonNode? key)
    {
        var scope = Scope(config);
        var value = ValidateKey(key);
        lock (gate)
        {
            Write(new SortedDictionary<(string, string), string>(records, Ordinal) { [scope] = value });
            return Status(config);
        }
    }

    public JsonObject Remove(JsonObject config)
    {
        var scope = Scope(config);
        lock (gate)
        {
            if (records.ContainsKey(scope))
            {
                var updated = new SortedDictionary<(string, string), string>(records, Ordinal);
                updated.Remove(scope);
                Write(updated);
            }
            return Status(config);
        }
    }
}
