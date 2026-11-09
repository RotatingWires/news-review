using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace NewsReview;

/// <summary>Bounded ZIP backup/restore; imported SQL, triggers and views are never installed.</summary>
static class Transfer
{
    public const long MaxUpload = 256L * 1024 * 1024;
    const long MaxDatabase = 1024L * 1024 * 1024;
    static readonly string[] Tables = ["sources", "settings", "stories", "runs"];
    const string Format = "newsreview-backup";
    // Markers left by releases that seeded bundled sources. Existing databases and their
    // backups still contain them; they are restored as-is and otherwise unused.
    static readonly HashSet<string> LegacyMarkers = ["sources_seeded", "source_additions_0411", "source_additions_048",
        "source_additions_056", "source_additions_061", "source_additions_062"];

    public static string Version => typeof(Transfer).Assembly.GetName().Version!.ToString(3);

    static Db ReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        var db = new Db(connection);
        db.Execute("PRAGMA trusted_schema=OFF");
        return db;
    }

    static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Snapshot committed WAL data; no authentication or environment secrets are included.</summary>
    public static JsonObject CreateArchive(Store store, string output)
    {
        store.Settings(); // Report a missing runtime database before opening the snapshot.
        var directory = Directory.CreateTempSubdirectory("newsreview-export-");
        try
        {
            var snapshot = Path.Combine(directory.FullName, "newsreview.db");
            long version;
            var totals = new JsonObject();
            using (var source = ReadOnly(store.Path))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString()))
            {
                destination.Open();
                source.Connection.BackupDatabase(destination);
                var copy = new Db(destination);
                copy.Execute("PRAGMA journal_mode=DELETE");
                version = copy.Long("PRAGMA user_version");
                foreach (var table in Tables) totals[table] = copy.Long($"SELECT COUNT(*) FROM {table}");
            }
            if (new FileInfo(snapshot).Length > MaxDatabase)
                throw new ValidationException("The database exceeds the 1 GiB ZIP restore limit; use the offline SQLite backup command instead");
            var manifest = new JsonObject
            {
                ["format"] = Format, ["formatVersion"] = 1, ["applicationVersion"] = Version, ["schemaVersion"] = version,
                ["exportedAt"] = PyText.Format(DateTimeOffset.UtcNow), ["sha256"] = Digest(snapshot), ["totals"] = totals,
            };
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(snapshot, "newsreview.db", CompressionLevel.Optimal);
                using var writer = new StreamWriter(archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                writer.Write(manifest.ToJsonString(new JsonSerializerOptions(Json.Compact) { WriteIndented = true }));
            }
            if (new FileInfo(output).Length > MaxUpload)
                throw new ValidationException("The compressed backup exceeds the 256 MiB ZIP upload limit; use the offline SQLite backup command instead");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return manifest;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    static readonly (string Legacy, string Current)[] LegacyManifestKeys =
        [("format_version", "formatVersion"), ("application_version", "applicationVersion"), ("schema_version", "schemaVersion"), ("exported_at", "exportedAt")];

    /// <summary>
    /// Backups made before v1.0.10 spell manifest keys in snake_case. Existing ZIPs and recovery copies must
    /// still restore, so their keys are renamed once here and every check below uses the current names.
    /// </summary>
    static JsonObject CurrentManifestKeys(JsonObject manifest)
    {
        foreach (var (legacy, current) in LegacyManifestKeys)
        {
            if (!manifest.ContainsKey(legacy)) continue;
            if (manifest.ContainsKey(current)) throw new ValidationException("Backup manifest repeats a field under two names");
            var value = manifest[legacy];
            manifest.Remove(legacy);
            manifest[current] = value;
        }
        return manifest;
    }

    static int[] ParseVersion(JsonNode? value)
    {
        if (!Json.IsString(value, out var text) || text.Length == 0 || text.Split('.').Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
            throw new ValidationException("Backup has an invalid application version");
        return text.Split('.').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
    }

    static int CompareVersions(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            if (left[i] != right[i]) return left[i].CompareTo(right[i]);
        return left.Length.CompareTo(right.Length);
    }

    static void CheckDate(JsonNode? value, bool required = false)
    {
        if (value is null && !required) return;
        if (!Json.IsString(value, out var text) || PyText.Length(text) > 80 || PyText.ParseAware(text) is null)
            throw new ValidationException("Backup contains an invalid timestamp");
    }

    static void CheckText(JsonNode? value, int limit, string name, bool nullable = false)
    {
        if (value is null && nullable) return;
        if (!Json.IsString(value, out var text) || PyText.Length(text) > limit) throw new ValidationException($"Backup contains invalid {name}");
    }

    static void ValidateRows(Db database)
    {
        var settings = new JsonObject();
        foreach (var row in database.Rows("SELECT * FROM settings")) settings[(string)row["key"]!] = Json.Load((string)row["value"]!);
        var allowed = new HashSet<string>([.. Defaults.SettingKeys, "topics", "history_cleared_at", "data_revision", .. LegacyMarkers]);
        if (settings.Any(pair => !allowed.Contains(pair.Key)) || Defaults.SettingKeys.Any(key => !settings.ContainsKey(key)))
            throw new ValidationException("Backup settings are incomplete or unsupported");
        var selected = new JsonObject();
        foreach (var key in Defaults.SettingKeys) selected[key] = settings[key]?.DeepClone();
        Validation.Settings(selected, Defaults.Settings());
        if (settings.ContainsKey("topics")) Validation.TopicNames(settings["topics"]);
        if (LegacyMarkers.Any(marker => settings.ContainsKey(marker) && !(Json.IsBool(settings[marker], out var flag) && flag)))
            throw new ValidationException("Backup has an invalid source marker");
        if (settings.ContainsKey("history_cleared_at")) CheckDate(settings["history_cleared_at"], required: true);
        foreach (var value in database.Rows("SELECT * FROM sources"))
        {
            var enabled = value["enabled"]!.AsLong();
            if (enabled is not (0 or 1) || value["removed"]!.AsLong() is not (0 or 1))
                throw new ValidationException("Backup contains invalid source flags");
            var source = (JsonObject)value.DeepClone();
            source["enabled"] = enabled == 1;
            Validation.Source(source);
            foreach (var key in new[] { "last_checked", "last_success", "first_checked", "newest_published", "last_new_story" }) CheckDate(value[key]);
            foreach (var key in new[] { "last_error", "collection_note" }) CheckText(value[key], 2000, "source diagnostics", nullable: true);
            if (!Json.IsInteger(value["consecutive_failures"], out var failures) || failures < 0)
                throw new ValidationException("Backup contains invalid failure counts");
        }
        foreach (var value in database.Rows("SELECT * FROM stories"))
        {
            foreach (var (key, limit) in new[] { ("headline", 500), ("url", 2048), ("excerpt", 16000), ("article_text", 100000), ("summary", 2400), ("why_it_matters", 1000) })
                CheckText(value[key], limit, key);
            UrlTools.Valid((string)value["url"]!);
            if (Json.Load((string)value["tags"]!) is not JsonArray tags || tags.Count > 100 || tags.Any(t => !Json.IsString(t, out var tag) || PyText.Length(tag) > 60))
                throw new ValidationException("Backup contains invalid story tags");
            if (!Json.IsString(value["status"], out var status) || status is not ("pending" or "reviewed" or "filtered" or "duplicate") ||
                !Json.IsInteger(value["bookmarked"], out var bookmarked) || bookmarked is not (0 or 1))
                throw new ValidationException("Backup contains invalid story state");
            if (!Json.IsInteger(value["score"], out var score) || score is < 0 or > 100)
                throw new ValidationException("Backup contains invalid relevance scores");
            CheckDate(value["discovered_at"], required: true);
            CheckDate(value["published_at"]);
            if (value.ContainsKey("sort_timestamp") && (!Json.IsInteger(value["sort_timestamp"], out var sortTimestamp) ||
                sortTimestamp != Store.PublicationTime((string?)value["published_at"] ?? (string)value["discovered_at"]!)))
                throw new ValidationException("Backup contains an invalid publication sort timestamp");
            CheckDate(value["reviewed_at"]);
            CheckDate(value["read_at"]);
            if (!Json.IsString(value["day"], out var day) || !DateOnly.TryParseExact(day, "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ValidationException("Backup contains an invalid story day");
        }
        foreach (var row in database.Rows("SELECT * FROM runs"))
        {
            if (!Json.IsString(row["status"], out var status) || status is not ("running" or "complete" or "partial" or "failed" or "interrupted") ||
                !Json.IsString(row["kind"], out var kind) || kind is not ("collect" or "review"))
                throw new ValidationException("Backup contains invalid run history");
            CheckDate(row["started_at"], required: true);
            CheckDate(row["finished_at"]);
            var details = Json.Load((string)row["details"]!);
            if (details is not JsonObject detailObject || (detailObject.ContainsKey("errors") &&
                (detailObject["errors"] is not JsonArray errors || errors.Any(error => !Json.IsString(error, out _)))))
                throw new ValidationException("Backup contains invalid run details");
        }
    }

    static List<string> TableLayout(Db db, string table) =>
        db.Rows($"PRAGMA table_info({table})").Select(row => string.Join('\u0001', row.Select(pair => Json.Dump(pair.Value)))).ToList();

    /// <summary>Only the two expected members are extracted, to controlled paths, with size bounds.</summary>
    static (string Database, JsonObject Totals, long SchemaVersion) ExtractAndValidate(Store store, string upload, string directory)
    {
        var databasePath = Path.Combine(directory, "restore.db");
        try
        {
            JsonObject manifest;
            using (var archive = ZipFile.OpenRead(upload))
            {
                var members = archive.Entries;
                if (members.Count != 2 || !members.Select(m => m.FullName).ToHashSet().SetEquals(["manifest.json", "newsreview.db"]))
                    throw new ValidationException("Choose a NewsReview export ZIP containing only manifest.json and newsreview.db");
                foreach (var member in members)
                {
                    var limit = member.FullName == "manifest.json" ? 64000 : MaxDatabase;
                    if (member.Length > limit || member.IsEncrypted)
                        throw new ValidationException("Backup member is encrypted, unsupported, or exceeds its size limit");
                }
                using (var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open(), Encoding.UTF8))
                    manifest = CurrentManifestKeys(JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new ValidationException("This is not a supported NewsReview backup format"));
                if (!Json.IsString(manifest["format"], out var format) || format != Format || !Json.IsInteger(manifest["formatVersion"], out var formatVersion) ||
                    formatVersion != 1 || !Json.IsInteger(manifest["schemaVersion"], out var schema) || schema is not (2 or 3 or Store.SchemaVersion))
                    throw new ValidationException("This is not a supported NewsReview backup format");
                if (CompareVersions(ParseVersion(manifest["applicationVersion"]), ParseVersion(Version)) > 0)
                    throw new ValidationException("This backup is from a newer NewsReview version. Update NewsReview first.");
                using var source = archive.GetEntry("newsreview.db")!.Open();
                using var destination = File.Create(databasePath);
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxDatabase) throw new ValidationException("Uncompressed backup database exceeds 1 GiB");
                    destination.Write(buffer, 0, read);
                }
            }
            if (!Json.IsString(manifest["sha256"], out var checksum) || Digest(databasePath) != checksum)
                throw new ValidationException("Backup database checksum does not match its manifest");
            var schemaVersion = manifest["schemaVersion"]!.AsLong();
            using var imported = ReadOnly(databasePath);
            using var live = store.Open();
            var objects = imported.Rows("SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'");
            if (objects.Any(r => (string)r["type"]! is not ("table" or "index")) ||
                !objects.Where(r => (string)r["type"]! == "table").Select(r => (string)r["name"]!).ToHashSet().SetEquals(Tables))
                throw new ValidationException("Backup has unexpected tables, triggers, or views");
            if (objects.Any(r => ((string?)r["sql"] ?? "").ToUpperInvariant().Contains("VIRTUAL TABLE")))
                throw new ValidationException("Virtual tables are not accepted in backups");
            if (imported.Long("PRAGMA user_version") != schemaVersion || (string?)imported.Scalar("PRAGMA integrity_check") != "ok")
                throw new ValidationException("Backup database schema/integrity check failed");
            if (imported.Rows("PRAGMA foreign_key_check").Count > 0)
                throw new ValidationException("Backup contains invalid source/duplicate relationships");
            foreach (var table in Tables)
            {
                var expectedRows = live.Rows($"PRAGMA table_info({table})");
                if (table == "stories")
                    expectedRows = expectedRows.Where(column => !((string)column["name"]! == "read_at" && schemaVersion < 3 ||
                                                                  (string)column["name"]! == "sort_timestamp" && schemaVersion < 4)).ToList();
                var expected = expectedRows.Select(row => string.Join('\u0001', row.Select(pair => Json.Dump(pair.Value)))).ToList();
                if (!expected.SequenceEqual(TableLayout(imported, table))) throw new ValidationException($"Backup table layout does not match: {table}");
            }
            ValidateRows(imported);
            var totals = new JsonObject();
            foreach (var table in Tables) totals[table] = imported.Long($"SELECT COUNT(*) FROM {table}");
            if (manifest["totals"] is not JsonObject declared || declared.Count != totals.Count ||
                totals.Any(pair => !Json.IsInteger(declared[pair.Key], out var count) || count != pair.Value!.AsLong()))
                throw new ValidationException("Backup totals do not match the database");
            return (databasePath, totals, schemaVersion);
        }
        catch (Exception error) when (error is InvalidDataException or SqliteException or JsonException or DecoderFallbackException
                                          or KeyNotFoundException or InvalidCastException or InvalidOperationException or OverflowException or FormatException)
        {
            throw new ValidationException("Invalid or damaged NewsReview backup ZIP");
        }
    }

    /// <summary>Caller owns the collector lock. Restores rows transactionally, keeping the live schema.</summary>
    public static JsonObject RestoreArchive(Store store, string upload)
    {
        var directory = Directory.CreateTempSubdirectory("newsreview-import-");
        try
        {
            var (importedPath, totals, schemaVersion) = ExtractAndValidate(store, upload, directory.FullName);
            var recoveryDirectory = Path.Combine(Path.GetDirectoryName(store.Path)!, "backups");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(recoveryDirectory);
            else Directory.CreateDirectory(recoveryDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var name = "before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                       Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".zip";
            using var imported = ReadOnly(importedPath);
            using var live = store.Open();
            live.Begin();
            // Other writers wait; a separate WAL reader snapshots the committed, pre-transaction
            // dataset. Failure here leaves the live archive intact.
            CreateArchive(store, Path.Combine(recoveryDirectory, name));
            live.Execute("PRAGMA defer_foreign_keys=ON");
            live.Execute("UPDATE stories SET duplicate_of=NULL");
            foreach (var table in new[] { "stories", "runs", "sources", "settings" }) live.Execute($"DELETE FROM {table}");
            foreach (var table in Tables)
            {
                var columns = live.Rows($"PRAGMA table_info({table})").Select(row => (string)row["name"]!).ToList();
                var importedColumns = imported.Rows($"PRAGMA table_info({table})").Select(row => (string)row["name"]!).ToHashSet();
                var names = string.Join(',', columns.Select(column => '"' + column + '"'));
                var selected = string.Join(',', columns.Select(column => importedColumns.Contains(column)
                    ? '"' + column + '"' : (column == "sort_timestamp" ? "0" : "NULL") + " AS \"" + column + '"'));
                var placeholders = string.Join(',', columns.Select(_ => "?"));
                using var command = imported.Command($"SELECT {selected} FROM {table}", []);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var values = new object?[reader.FieldCount];
                    for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    live.Execute($"INSERT INTO {table}({names}) VALUES ({placeholders})", values);
                }
            }
            if (schemaVersion < 4) Store.BackfillStoryTimes(live);
            Store.InitializeTopics(live);
            live.Execute("INSERT INTO settings(key,value) VALUES ('data_revision',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", Json.Dump(Store.NewRevision()));
            live.Execute("UPDATE runs SET status='interrupted',finished_at=? WHERE status='running'", PyText.Format(DateTimeOffset.UtcNow));
            if (live.Rows("PRAGMA foreign_key_check").Count > 0) throw new ValidationException("Restored relationships are invalid; changes were rolled back");
            live.Commit();
            return new JsonObject { ["restored"] = true, ["totals"] = totals, ["recoveryBackup"] = name };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    /// <summary>`backup --database PATH --output PATH`: a consistent online copy that never overwrites a file.</summary>
    public static int BackupCommand(string[] args)
    {
        string database = Path.Combine("data", "newsreview.db"), output = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--database" && i + 1 < args.Length) database = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else return Usage($"unrecognized arguments: {args[i]}");
        }
        if (output.Length == 0) return Usage("the following arguments are required: --output");
        var source = Path.GetFullPath(database);
        var destination = Path.GetFullPath(output);
        if (!File.Exists(source)) return Usage("Source database does not exist");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try { File.Open(destination, FileMode.CreateNew).Dispose(); }
        catch (IOException) when (File.Exists(destination)) { return Usage("Output already exists; choose a new filename"); }
        try
        {
            using (var src = ReadOnly(source))
            using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString()))
            {
                dst.Open();
                src.Connection.BackupDatabase(dst);
                var result = (string?)new Db(dst).Scalar("PRAGMA integrity_check");
                if (result != "ok") throw new InvalidOperationException($"Backup integrity check failed: {result}");
            }
        }
        catch
        {
            // Only the new, exclusively created destination belongs to this operation.
            File.Delete(destination);
            throw;
        }
        Console.WriteLine($"Backup created: {destination}");
        return 0;
    }

    static int Usage(string message)
    {
        Console.Error.WriteLine("usage: NewsReview backup [--database DATABASE] --output OUTPUT");
        Console.Error.WriteLine($"NewsReview backup: error: {message}");
        return 2;
    }
}
