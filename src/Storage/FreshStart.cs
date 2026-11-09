using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace NewsReview;

/// <summary>
/// File > Fresh Start...: what each option would clear, clearing only the chosen data, and compacting the database
/// afterward. Its own file like TaskList's and SpendTracker's FreshStart.cs; the rest of Store is in Store.cs.
/// </summary>
sealed partial class Store
{
    static readonly string[] ResetOptions = ["history", "bookmarks", "runs", "topics", "guidance", "exclusions", "sources", "ai", "collection", "feedback"];

    /// <summary>What each Fresh Start option would clear, like TaskList's and SpendTracker's counts.</summary>
    public JsonObject FreshStartSummary() => Read(db =>
    {
        var interests = (JsonObject)db.SettingJson("interests")!;
        static long Count(JsonNode? node) => node is JsonArray array ? array.Count : node is JsonObject obj ? obj.Count : 0;
        return new JsonObject
        {
            ["history"] = db.Long("SELECT COUNT(*) FROM stories WHERE bookmarked=0"),
            ["bookmarks"] = db.Long("SELECT COUNT(*) FROM stories WHERE bookmarked=1"),
            ["runs"] = db.Long("SELECT COUNT(*) FROM runs"),
            ["topics"] = Count(interests["topics"]),
            ["feedback"] = Count(Feedback.State(interests)["ratings"]),
            ["exclusions"] = Count(interests["exclusions"]),
            ["sources"] = db.Long("SELECT COUNT(*) FROM sources WHERE removed=0"),
        };
    });

    /// <summary>The database file plus its write-ahead log, for Fresh Start's size and freed space.</summary>
    public long DatabaseBytes() => new FileInfo(Path).Length + (File.Exists(Path + "-wal") ? new FileInfo(Path + "-wal").Length : 0);

    /// <summary>Rewrites the database without its free pages, like TaskList's and SpendTracker's Fresh Start.</summary>
    public void Compact() => Read(db => db.Execute("VACUUM"));

    public JsonObject Reset(JsonNode? selectionsNode)
    {
        if (selectionsNode is not JsonObject selectionsObject || selectionsObject.Any(pair => !ResetOptions.Contains(pair.Key))
            || selectionsObject.Any(pair => !Json.IsBool(pair.Value, out _)) || !selectionsObject.Any(pair => pair.Value!.GetValue<bool>()))
            throw new ValidationException("Choose at least one valid reset option");
        bool Selected(string key) => selectionsObject[key]?.GetValue<bool>() == true;
        return Write(db =>
        {
            var result = new JsonObject { ["deleted_stories"] = 0, ["cleared_bookmarks"] = 0 };
            if (Selected("history"))
            {
                var where = Selected("bookmarks") ? "1=1" : "bookmarked=0";
                db.Execute($"""
                    UPDATE stories SET duplicate_of=NULL,
                        status=CASE WHEN status='duplicate' THEN CASE WHEN summary='' THEN 'pending' ELSE 'reviewed' END ELSE status END
                        WHERE duplicate_of IN (SELECT id FROM stories WHERE {where})
                    """);
                result["deleted_stories"] = db.Execute($"DELETE FROM stories WHERE {where}");
            }
            if (Selected("bookmarks")) result["cleared_bookmarks"] = db.Execute("UPDATE stories SET bookmarked=0 WHERE bookmarked=1");
            if (Selected("runs"))
            {
                db.Execute("DELETE FROM runs");
                db.Execute("""
                    UPDATE sources SET last_checked=NULL,last_error=NULL,last_success=NULL,first_checked=NULL,
                        newest_published=NULL,last_new_story=NULL,last_count=NULL,last_method=NULL,consecutive_failures=0,collection_note=''
                    """);
            }
            var interests = (JsonObject)db.SettingJson("interests")!;
            if (Selected("feedback")) interests["feedback"] = Feedback.Default();
            if (Selected("topics")) interests["topics"] = new JsonArray();
            if (Selected("guidance")) interests["guidance"] = "";
            if (Selected("exclusions")) interests["exclusions"] = new JsonArray();
            db.Execute("UPDATE settings SET value=$value WHERE key='interests'", ("$value", interests));
            var defaults = Defaults.Settings();
            foreach (var key in new[] { "ai", "collection" })
                if (Selected(key)) db.Execute("UPDATE settings SET value=$value WHERE key=$key", ("$value", defaults[key]), ("$key", key));
            if (Selected("sources")) db.Execute("UPDATE sources SET removed=1,enabled=0");
            if (Selected("history") || Selected("runs"))
                db.Execute("INSERT INTO settings(key,value) VALUES ('history_cleared_at',$now) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$now", Json.Dump(PyText.UtcNow())));
            db.Execute("UPDATE settings SET value=$value WHERE key='data_revision'", ("$value", Json.Dump(NewRevision())));
            return result;
        });
    }
}
