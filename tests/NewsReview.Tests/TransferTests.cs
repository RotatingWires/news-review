using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace NewsReview.Tests;

public class TransferTests : IDisposable
{
    readonly TempDirectory directory = new();
    readonly Store store;
    readonly long story;
    readonly string archive;

    public TransferTests()
    {
        store = new Store(directory.File("newsreview.db"));
        store.InsertStory(Fixtures.AddSource(store)["id"]!.AsLong(), Fixtures.Entry("Protocol update", "https://example.com/story"), "Protocols", "2026-10-15");
        story = store.Stories(new())["items"]![0]!["id"]!.AsLong();
        store.Bookmark(story, true);
        archive = directory.File("export.zip");
        Transfer.CreateArchive(store, archive);
    }

    public void Dispose() => directory.Dispose();

    long Total(Store.StoryQuery query) => store.Stories(query)["total"]!.AsLong();

    static void Execute(string path, params string[] statements)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    string WriteZip(string name, JsonObject manifest, byte[] database, bool extra = false)
    {
        var path = directory.File(name);
        File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(manifest.ToJsonString());
        using (var stream = zip.CreateEntry("newsreview.db").Open()) stream.Write(database);
        if (extra) using (var writer = new StreamWriter(zip.CreateEntry("../escape").Open())) writer.Write("unexpected");
        return path;
    }

    static (JsonObject Manifest, byte[] Database) ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        var manifest = (JsonObject)JsonNode.Parse(reader.ReadToEnd())!;
        using var memory = new MemoryStream();
        using (var stream = zip.GetEntry("newsreview.db")!.Open()) stream.CopyTo(memory);
        return (manifest, memory.ToArray());
    }

    [Fact]
    public void OpenRouterSettingsRoundtrip()
    {
        var config = (JsonObject)store.Settings()["ai"]!;
        config["provider"] = "openrouter";
        config["base_url"] = "https://openrouter.ai/api/v1";
        config["model"] = "anthropic/claude-haiku-5.5";
        store.SaveSettings(new JsonObject { ["ai"] = config.DeepClone() });
        Transfer.CreateArchive(store, archive);
        var blank = (JsonObject)config.DeepClone();
        blank["model"] = "";
        store.SaveSettings(new JsonObject { ["ai"] = blank });
        Transfer.RestoreArchive(store, archive);
        Assert.True(JsonNode.DeepEquals(config, store.Settings()["ai"]));
    }

    [Fact]
    public void RoundtripAndRecoverySnapshot()
    {
        var preferences = store.Settings();
        store.Reset(new JsonObject { ["history"] = true, ["bookmarks"] = true, ["guidance"] = true });
        var result = Transfer.RestoreArchive(store, archive);
        Assert.Equal(1, Total(new() { Bookmarks = true }));
        Assert.True(JsonNode.DeepEquals(preferences, store.Settings()));
        var recovery = Path.Combine(directory.Path, "backups", (string)result["recoveryBackup"]!);
        Assert.True(File.Exists(recovery));
        Transfer.RestoreArchive(store, recovery);
        Assert.Equal(0, Total(new() { ShowHidden = true }));
    }

    [Fact]
    public void BackupsFromInstallsWithBundledSourceMarkersRestore()
    {
        Fixtures.Sql(store, "INSERT INTO settings VALUES ('sources_seeded','true'),('source_additions_062','true')");
        Transfer.CreateArchive(store, archive);
        store.Reset(new JsonObject { ["history"] = true, ["bookmarks"] = true });
        Transfer.RestoreArchive(store, archive);
        Assert.Equal(story, store.Stories(new() { Bookmarks = true })["items"]![0]!["id"]!.AsLong());
        Fixtures.Sql(store, "UPDATE settings SET value='false' WHERE key='sources_seeded'");
        Transfer.CreateArchive(store, archive);
        Assert.Contains("invalid source marker", Assert.Throws<ValidationException>(() => Transfer.RestoreArchive(store, archive)).Message);
    }

    [Fact]
    public void ReadMarkersRoundtripAndSchemaTwoBackupsStillRestore()
    {
        var timestamp = store.MarkRead(story);
        Assert.Equal(timestamp, store.MarkRead(story));
        var current = directory.File("read.zip");
        Transfer.CreateArchive(store, current);
        store.Reset(new JsonObject { ["history"] = true, ["bookmarks"] = true });
        Transfer.RestoreArchive(store, current);
        Assert.Equal(timestamp, (string)store.Stories(new())["items"]![0]!["read_at"]!);
        var (manifest, database) = ReadZip(current);
        var legacy = directory.File("legacy.db");
        File.WriteAllBytes(legacy, database);
        Execute(legacy, "DROP INDEX stories_publication", "DROP INDEX stories_bookmarks", "DROP INDEX stories_search", "ALTER TABLE stories DROP COLUMN sort_timestamp",
            "ALTER TABLE stories DROP COLUMN read_at", "PRAGMA user_version=2");
        var legacyBytes = File.ReadAllBytes(legacy);
        manifest["schemaVersion"] = 2;
        manifest["applicationVersion"] = "0.9.2";
        manifest["sha256"] = Sha256(legacyBytes);
        Transfer.RestoreArchive(store, WriteZip("legacy.zip", manifest, legacyBytes));
        Assert.Null(store.Stories(new())["items"]![0]!["read_at"]);
        Assert.Equal(Store.SchemaVersion, store.Read(db => db.Long("PRAGMA user_version")));
        // The same version-2 file also upgrades normally when starting the app.
        Assert.Null(new Store(legacy).Stories(new())["items"]![0]!["read_at"]);
    }

    [Fact]
    public void ManifestUsesCamelCaseKeysAndSnakeCaseBackupsStillRestore()
    {
        var (manifest, database) = ReadZip(archive);
        Assert.Equal(["applicationVersion", "exportedAt", "format", "formatVersion", "schemaVersion", "sha256", "totals"],
            manifest.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());

        // Backups written before v1.0.10 spell these keys in snake_case.
        var legacy = new JsonObject();
        foreach (var (key, value) in manifest)
        {
            var name = key switch
            {
                "formatVersion" => "format_version",
                "applicationVersion" => "application_version",
                "schemaVersion" => "schema_version",
                "exportedAt" => "exported_at",
                _ => key
            };
            legacy[name] = value?.DeepClone();
        }
        Transfer.RestoreArchive(store, WriteZip("snake-case.zip", legacy, database));
        Assert.Equal(1, Total(new()));

        legacy["formatVersion"] = 1;
        Assert.Throws<ValidationException>(() => Transfer.RestoreArchive(store, WriteZip("both-names.zip", legacy, database)));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("extra")]
    [InlineData("version")]
    [InlineData("view")]
    [InlineData("sort_timestamp")]
    public void RejectsBadChecksumExtraFilesNewerVersionAndSqlObjects(string mutation)
    {
        var (manifest, database) = ReadZip(archive);
        if (mutation == "checksum") manifest["sha256"] = new string('0', 64);
        if (mutation == "version") manifest["applicationVersion"] = "99.0.0";
        if (mutation is "view" or "sort_timestamp")
        {
            var path = directory.File("mutant.db");
            File.WriteAllBytes(path, database);
            Execute(path, mutation == "view" ? "CREATE VIEW unwanted AS SELECT * FROM stories" : "UPDATE stories SET sort_timestamp=sort_timestamp+1");
            database = File.ReadAllBytes(path);
            manifest["sha256"] = Sha256(database);
        }
        var altered = WriteZip("altered.zip", manifest, database, extra: mutation == "extra");
        Assert.Throws<ValidationException>(() => Transfer.RestoreArchive(store, altered));
        Assert.Equal(1, Total(new() { Bookmarks = true }));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(directory.Path)!, "escape")));
    }

    [Fact]
    public void SelectiveResetRetainsBookmarksAndUnselectedPreferences()
    {
        var preferences = store.Settings();
        store.Reset(new JsonObject { ["history"] = true });
        Assert.Equal(1, Total(new() { Bookmarks = true }));
        Assert.True(JsonNode.DeepEquals(preferences, store.Settings()));
        store.Reset(new JsonObject { ["bookmarks"] = true });
        Assert.Equal(1, Total(new()));
        Assert.Equal(0, Total(new() { Bookmarks = true }));
        store.Reset(new JsonObject { ["topics"] = true });
        Assert.Empty(store.Settings()["interests"]!["topics"]!.AsArray());
        Assert.Equal((string)preferences["interests"]!["guidance"]!, (string)store.Settings()["interests"]!["guidance"]!);
    }

    [Fact]
    public void BackupCommandMakesConsistentOnlineCopyAndRefusesOverwrite()
    {
        var output = directory.File("backup.db");
        string[] command = [typeof(Program).Assembly.Location, "backup", "--database", store.Path, "--output", output];
        (int, string) Run()
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", command) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, text);
        }
        // Keep a reader connection open so this also exercises a live WAL database.
        using (var live = store.Open())
        {
            Assert.Equal("wal", (string)live.Scalar("PRAGMA journal_mode")!);
            var (code, text) = Run();
            Assert.True(code == 0, text);
        }
        var saved = new Store(output).Stories(new())["items"]![0]!;
        Assert.Equal("Protocol update", (string)saved["headline"]!);
        Assert.True(saved["bookmarked"]!.GetValue<bool>());
        var (refusedCode, refusedText) = Run();
        Assert.NotEqual(0, refusedCode);
        Assert.Contains("already exists", refusedText);
    }
}
