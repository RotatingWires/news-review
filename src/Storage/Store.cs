using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace NewsReview;

/// <summary>The data folder or database cannot be used; reported to clients as HTTP 503.</summary>
sealed class StorageUnavailableException(string message) : Exception(message);

/// <summary>One open SQLite connection with an optional transaction; SQL uses positional ? parameters.</summary>
sealed class Db : IDisposable
{
    public SqliteConnection Connection { get; }
    public SqliteTransaction? Transaction { get; private set; }

    public Db(SqliteConnection connection) => Connection = connection;

    public void Begin() => Transaction = Connection.BeginTransaction(deferred: false);

    public void Commit()
    {
        Transaction?.Commit();
        Transaction = null;
    }

    public void Rollback()
    {
        Transaction?.Dispose();
        Transaction = null;
    }

    public void Dispose()
    {
        Transaction?.Dispose();
        Connection.Dispose();
    }

    /// <summary>Named parameters ($id, $name), written the same way as in TaskList, TaskList Stats, and SpendTracker.</summary>
    public SqliteCommand Command(string sql, IEnumerable<(string Name, object? Value)> args)
    {
        var command = Connection.CreateCommand();
        command.Transaction = Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, Value(value));
        return command;
    }

    static object Value(object? value) => value switch
    {
        null => DBNull.Value,
        bool flag => flag ? 1L : 0L,
        JsonNode node => Json.Dump(node),
        _ => value,
    };

    public int Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var command = Command(sql, args);
        return command.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var command = Command(sql, args);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public long Long(string sql, params (string Name, object? Value)[] args) => Convert.ToInt64(Scalar(sql, args), CultureInfo.InvariantCulture);

    public List<JsonObject> Rows(string sql, params (string Name, object? Value)[] args)
    {
        using var command = Command(sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<JsonObject>();
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                {
                    long number => JsonValue.Create(number),
                    double real => JsonValue.Create(real),
                    string text => JsonValue.Create(text),
                    var other => JsonValue.Create(other.ToString()),
                };
            rows.Add(row);
        }
        return rows;
    }

    public JsonObject? Row(string sql, params (string Name, object? Value)[] args) => Rows(sql, args).FirstOrDefault();

    public string Setting(string key) => (string)Scalar("SELECT value FROM settings WHERE key=$key", ("$key", key))!;

    public JsonNode? SettingJson(string key) => Json.Load(Setting(key));
}

/// <summary>SQLite persistence. Each operation owns its connection and transaction.</summary>
sealed partial class Store
{
    public const int SchemaVersion = 4;
    static readonly Dictionary<string, string> StorySorts = new()
    {
        ["rank"] = "s.score DESC,s.sort_timestamp DESC,s.id DESC",
        ["rank_asc"] = "s.score ASC,s.sort_timestamp DESC,s.id DESC",
        ["published"] = "s.sort_timestamp DESC,s.id DESC",
        ["published_asc"] = "s.sort_timestamp ASC,s.id ASC",
    };
    const string FeedColumns = """
        s.id,s.source_id,s.headline,s.url,s.excerpt,s.content_basis,
            s.published_at,s.discovered_at,s.day,s.summary,s.tags,s.status,s.score,s.bookmarked,
            s.milestone,s.duplicate_of,s.review_error,s.attempts,s.reviewed_at,s.provider,s.model,
            s.input_tokens,s.output_tokens,s.read_at
        """;
    const string PurgeableDuplicates = """
        s.status='duplicate' AND s.bookmarked=0
            AND NOT EXISTS (SELECT 1 FROM stories child WHERE child.duplicate_of=s.id)
        """;

    public string Path { get; }
    public string JournalMode { get; } = "unknown";
    bool initialized;

    /// <summary>Called for each opened connection; tests use it to trace and bound real queries.</summary>
    internal Action<SqliteConnection>? Opened { get; set; }

    public Store(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var db = Open();
        JournalMode = Convert.ToString(db.Scalar("PRAGMA journal_mode=WAL"), CultureInfo.InvariantCulture) ?? "unknown";
        var version = db.Long("PRAGMA user_version");
        if (version > SchemaVersion) throw new InvalidOperationException("This database was made by a newer NewsReview version; update NewsReview first.");
        // The schema statements are kept identical to earlier releases: ZIP restores compare table layouts exactly.
        db.Execute("""
            CREATE TABLE IF NOT EXISTS sources (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, url TEXT NOT NULL UNIQUE,
                feed_url TEXT NOT NULL DEFAULT '', category TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1, last_checked TEXT, last_error TEXT);
            """);
        db.Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        db.Execute("""
            CREATE TABLE IF NOT EXISTS stories (
                id INTEGER PRIMARY KEY, source_id INTEGER REFERENCES sources(id),
                headline TEXT NOT NULL, url TEXT NOT NULL UNIQUE, excerpt TEXT NOT NULL DEFAULT '',
                article_text TEXT NOT NULL DEFAULT '', content_basis TEXT NOT NULL DEFAULT 'excerpt',
                published_at TEXT, discovered_at TEXT NOT NULL, day TEXT NOT NULL,
                summary TEXT NOT NULL DEFAULT '', why_it_matters TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '[]', status TEXT NOT NULL DEFAULT 'pending',
                score INTEGER NOT NULL DEFAULT 50, bookmarked INTEGER NOT NULL DEFAULT 0,
                milestone TEXT NOT NULL DEFAULT 'not applicable', duplicate_of INTEGER REFERENCES stories(id),
                review_error TEXT, attempts INTEGER NOT NULL DEFAULT 0, reviewed_at TEXT,
                provider TEXT, model TEXT, interests_snapshot TEXT, input_tokens INTEGER, output_tokens INTEGER, read_at TEXT);
            """);
        db.Execute("CREATE INDEX IF NOT EXISTS stories_day ON stories(day);");
        db.Execute("CREATE INDEX IF NOT EXISTS stories_status ON stories(status, attempts);");
        db.Execute("CREATE INDEX IF NOT EXISTS stories_duplicate ON stories(duplicate_of);");
        db.Execute("""
            CREATE TABLE IF NOT EXISTS runs (
                id INTEGER PRIMARY KEY, kind TEXT NOT NULL, started_at TEXT NOT NULL,
                finished_at TEXT, status TEXT NOT NULL, details TEXT NOT NULL DEFAULT '{}');
            """);
        var storyColumns = Columns(db, "stories");
        if (version < 3 && !storyColumns.Contains("read_at")) db.Execute("ALTER TABLE stories ADD COLUMN read_at TEXT");
        if (!storyColumns.Contains("sort_timestamp"))
        {
            db.Begin();
            db.Execute("ALTER TABLE stories ADD COLUMN sort_timestamp INTEGER NOT NULL DEFAULT 0");
            BackfillStoryTimes(db);
            db.Commit();
        }
        db.Execute("CREATE INDEX IF NOT EXISTS stories_publication ON stories(sort_timestamp,id,score,status,bookmarked)");
        db.Execute("CREATE INDEX IF NOT EXISTS stories_bookmarks ON stories(bookmarked,sort_timestamp,id,score,status)");
        // Keyword, topic, and undated searches scan every story. Covering their filter and sort columns keeps that scan
        // out of the table rows, whose article bodies and review snapshots would otherwise be read from disk for each story.
        db.Execute("CREATE INDEX IF NOT EXISTS stories_search ON stories(sort_timestamp,id,score,status,bookmarked,source_id,tags,headline,summary,excerpt)");
        var sourceColumns = Columns(db, "sources");
        foreach (var (name, definition) in new (string, string)[]
        {
            ("mode", "TEXT NOT NULL DEFAULT 'auto'"), ("link_pattern", "TEXT NOT NULL DEFAULT ''"),
            ("stale_days", "INTEGER NOT NULL DEFAULT 14"), ("removed", "INTEGER NOT NULL DEFAULT 0"),
            ("last_success", "TEXT"), ("first_checked", "TEXT"), ("newest_published", "TEXT"),
            ("last_new_story", "TEXT"), ("last_count", "INTEGER"), ("last_method", "TEXT"),
            ("consecutive_failures", "INTEGER NOT NULL DEFAULT 0"), ("collection_note", "TEXT NOT NULL DEFAULT ''"),
        })
            if (!sourceColumns.Contains(name)) db.Execute($"ALTER TABLE sources ADD COLUMN {name} {definition}");
        AddRules(db);
        db.Begin();
        foreach (var (key, value) in Defaults.Settings()) db.Execute("INSERT OR IGNORE INTO settings VALUES ($key, $value)", ("$key", key), ("$value", value));
        db.Execute("INSERT OR IGNORE INTO settings VALUES ('data_revision',$revision)", ("$revision", Json.Dump(NewRevision())));
        InitializeTopics(db);
        if (version < 2)
            db.Execute("""
                UPDATE sources SET first_checked=last_checked,
                    last_success=CASE WHEN last_error IS NULL THEN last_checked ELSE NULL END,
                    consecutive_failures=CASE WHEN last_error IS NULL THEN 0 ELSE 1 END
                """);
        db.Execute("UPDATE runs SET status='interrupted',finished_at=$now WHERE status='running'", ("$now", PyText.UtcNow()));
        db.Commit();
        db.Execute($"PRAGMA user_version={SchemaVersion}");
        initialized = true;
    }

    /// <summary>CHECK rules like TaskList's and SpendTracker's, matching what backup validation already requires.</summary>
    static readonly (string Table, string[] Rules)[] TableRules =
    [
        ("sources", ["enabled IN (0, 1)", "removed IN (0, 1)", "mode IN ('auto', 'feed', 'page')", "stale_days BETWEEN 1 AND 365", "consecutive_failures >= 0"]),
        ("stories", ["status IN ('pending', 'reviewed', 'filtered', 'duplicate')", "bookmarked IN (0, 1)", "score BETWEEN 0 AND 100", "attempts >= 0"]),
        ("runs", ["kind IN ('collect', 'review')", "status IN ('running', 'complete', 'partial', 'failed', 'interrupted')"]),
    ];

    /// <summary>Set when a table kept no CHECK rules because existing rows break them; the server logs it.</summary>
    public string? RulesWarning { get; private set; }

    /// <summary>
    /// SQLite adds CHECK rules only by rebuilding a table, so a table without them is rebuilt once from its own stored
    /// CREATE statement with the rules appended. Its column layout, which backup restores compare, stays the same.
    /// </summary>
    void AddRules(Db db)
    {
        var pending = new List<(string Table, string Sql, string[] Rules)>();
        foreach (var (table, rules) in TableRules)
        {
            var sql = (string)db.Scalar("SELECT sql FROM sqlite_master WHERE type='table' AND name=$name", ("$name", table))!;
            if (sql.Contains("CHECK(", StringComparison.Ordinal)) continue;
            var broken = db.Long($"SELECT COUNT(*) FROM {table} WHERE NOT ({string.Join(" AND ", rules)})");
            if (broken > 0) RulesWarning = $"{RulesWarning}{broken} {table} row(s) break the new CHECK rules, so {table} keeps none for now. ";
            else pending.Add((table, sql, rules));
        }
        if (pending.Count == 0) return;
        db.Execute("PRAGMA foreign_keys=OFF");
        try
        {
            db.Begin();
            foreach (var (table, sql, rules) in pending)
            {
                var indexes = db.Rows("SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=$name AND sql IS NOT NULL", ("$name", table))
                    .Select(row => (string)row["sql"]!).ToList();
                var close = sql.LastIndexOf(')');
                var created = Regex.Replace(sql[..close], @"^CREATE TABLE\s+(IF NOT EXISTS\s+)?""?\w+""?", $"CREATE TABLE {table}_rules")
                    + ",\n    " + string.Join(", ", rules.Select(rule => $"CHECK({rule})")) + sql[close..];
                db.Execute(created);
                db.Execute($"INSERT INTO {table}_rules SELECT * FROM {table}");
                db.Execute($"DROP TABLE {table}");
                db.Execute($"ALTER TABLE {table}_rules RENAME TO {table}");
                foreach (var index in indexes) db.Execute(index);
            }
            if (db.Rows("PRAGMA foreign_key_check").Count > 0) throw new InvalidOperationException("Adding CHECK rules would break relationships");
            db.Commit();
        }
        finally
        {
            db.Rollback();
            db.Execute("PRAGMA foreign_keys=ON");
        }
    }

    static HashSet<string> Columns(Db db, string table) =>
        db.Rows($"PRAGMA table_info({table})").Select(row => (string)row["name"]!).ToHashSet();

    public static string NewRevision() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static long PublicationTime(string value) => PyText.Microseconds(PyText.ParseInstant(value) ?? throw new FormatException($"Invalid timestamp: {value}"));

    public static void BackfillStoryTimes(Db db)
    {
        foreach (var row in db.Rows("SELECT id,COALESCE(published_at,discovered_at) AS timestamp FROM stories ORDER BY id"))
            db.Execute("UPDATE stories SET sort_timestamp=$sortTimestamp WHERE id=$id", ("$sortTimestamp", PublicationTime((string)row["timestamp"]!)), ("$id", row["id"]!.AsLong()));
    }

    /// <summary>Import existing labels once, without promoting future source/model output.</summary>
    public static void InitializeTopics(Db db)
    {
        if (db.Scalar("SELECT 1 FROM settings WHERE key='topics'") is not null) return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in db.Rows("SELECT category FROM sources")) names.Add((string)row["category"]!);
        foreach (var row in db.Rows("SELECT DISTINCT value FROM stories,json_each(stories.tags)")) names.Add((string)row["value"]!);
        db.Execute("INSERT INTO settings VALUES ('topics',$topics)", ("$topics", new JsonArray(PyText.SortFolded(names).Select(n => (JsonNode)n).ToArray())));
    }

    public Db Open()
    {
        if (initialized && !File.Exists(Path))
            throw new StorageUnavailableException("The NewsReview database or data folder was removed while the server was running. Stop and restart NewsReview to initialize a fresh database, or restore your backup. Use Fresh start in Settings to clear history without removing preferences.");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = initialized ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            var db = new Db(connection);
            db.Execute("PRAGMA busy_timeout=5000");
            db.Execute("PRAGMA synchronous=NORMAL");
            Opened?.Invoke(connection);
            return db;
        }
        catch (SqliteException error) when ((error.SqliteErrorCode & 0xff) == 14)
        {
            connection.Dispose();
            throw new StorageUnavailableException(
                $"NewsReview cannot open its database at {Path}. Check that the data folder is writable " +
                "and SQLite can create/open its -wal and -shm files there. Stop other NewsReview processes. " +
                "If using a pooled or network drive, test a copy of the data folder on a regular local disk " +
                "with --data-dir. Keep the original database and companion files together; do not delete them.");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Run <paramref name="work"/> in one IMMEDIATE transaction, committing on success.</summary>
    public T Write<T>(Func<Db, T> work)
    {
        using var db = Open();
        db.Begin();
        var result = work(db);
        db.Commit();
        return result;
    }

    public void Write(Action<Db> work) => Write(db => { work(db); return 0; });

    public T Read<T>(Func<Db, T> work)
    {
        using var db = Open();
        return work(db);
    }

    public JsonObject Settings() => Read(db =>
    {
        var values = new JsonObject();
        foreach (var row in db.Rows("SELECT * FROM settings"))
            if (Defaults.SettingKeys.Contains((string)row["key"]!)) values[(string)row["key"]!] = Json.Load((string)row["value"]!);
        var interests = (JsonObject)values["interests"]!;
        if (!interests.ContainsKey("feedback")) interests["feedback"] = Feedback.State(interests);
        return values;
    });

    public void SaveSettings(JsonObject values) => Write(db =>
    {
        foreach (var (key, node) in values)
        {
            var value = node!.DeepClone();
            if (key == "interests" && value is JsonObject interests && !interests.ContainsKey("feedback"))
                interests["feedback"] = Feedback.State((JsonObject)db.SettingJson("interests")!);
            db.Execute("UPDATE settings SET value=$value WHERE key=$key", ("$value", value), ("$key", key));
        }
    });

    public JsonArray Sources() => Read(db => new JsonArray(db.Rows("SELECT * FROM sources WHERE removed=0 ORDER BY name COLLATE NOCASE")
        .Select(row => { row["health"] = Health.For(row); return (JsonNode)row; }).ToArray()));

    public long SaveSource(JsonObject data, long? sourceId = null) => Write(db =>
    {
        (string, object?)[] values = [("$name", (string)data["name"]!), ("$url", (string)data["url"]!), ("$feedUrl", (string)data["feed_url"]!),
            ("$category", (string)data["category"]!), ("$enabled", data["enabled"]!.GetValue<bool>() ? 1L : 0L), ("$mode", (string?)data["mode"] ?? "auto"),
            ("$linkPattern", (string?)data["link_pattern"] ?? ""), ("$staleDays", data["stale_days"]?.AsLong() ?? 14L)];
        if (sourceId is null)
        {
            if (db.Scalar("SELECT id FROM sources WHERE url=$url AND removed=1", ("$url", (string)data["url"]!)) is long retired) sourceId = retired;
            else
            {
                db.Execute("INSERT INTO sources(name,url,feed_url,category,enabled,mode,link_pattern,stale_days) VALUES ($name,$url,$feedUrl,$category,$enabled,$mode,$linkPattern,$staleDays)", values);
                return db.Long("SELECT last_insert_rowid()");
            }
        }
        if (db.Execute("UPDATE sources SET name=$name,url=$url,feed_url=$feedUrl,category=$category,enabled=$enabled,mode=$mode,link_pattern=$linkPattern,stale_days=$staleDays,removed=0 WHERE id=$id", [.. values, ("$id", sourceId)]) == 0)
            throw new NotFoundException("Source not found");
        return sourceId.Value;
    });

    public void SourceChecked(long sourceId, string? error = null, string? feedUrl = null, long? count = null, string? newest = null,
        long added = 0, string? method = null, string note = "") => Write(db =>
    {
        var now = PyText.UtcNow();
        db.Execute("""
            UPDATE sources SET last_checked=$now,first_checked=COALESCE(first_checked,$now),last_error=$error,
                feed_url=COALESCE($feedUrl,feed_url),consecutive_failures=CASE WHEN $error IS NULL THEN 0 ELSE consecutive_failures+1 END,
                last_success=CASE WHEN $error IS NULL THEN $now ELSE last_success END,last_count=$count,
                newest_published=CASE WHEN $error IS NULL THEN $newest ELSE newest_published END,
                last_new_story=CASE WHEN $added>0 THEN $now ELSE last_new_story END,last_method=COALESCE($method,last_method),collection_note=$note WHERE id=$id
            """, ("$now", now), ("$error", error), ("$feedUrl", feedUrl), ("$count", count), ("$newest", newest), ("$added", added),
            ("$method", method), ("$note", note), ("$id", sourceId));
    });

    public bool InsertStory(long sourceId, Entry item, string category, string day) => Write(db =>
    {
        var topics = ((JsonArray)db.SettingJson("topics")!).Select(t => (string)t!);
        var tags = topics.Where(name => PyText.FoldEquals(name, category)).Take(1).Select(n => (JsonNode)n).ToArray();
        var discovered = PyText.UtcNow();
        return db.Execute("""
            INSERT OR IGNORE INTO stories
                (source_id,headline,url,excerpt,published_at,discovered_at,day,tags,sort_timestamp)
                VALUES ($sourceId,$headline,$url,$excerpt,$publishedAt,$discoveredAt,$day,$tags,$sortTimestamp)
            """, ("$sourceId", sourceId), ("$headline", item.Headline), ("$url", item.Url), ("$excerpt", item.Excerpt),
            ("$publishedAt", item.PublishedAt), ("$discoveredAt", discovered), ("$day", day), ("$tags", new JsonArray(tags)),
            ("$sortTimestamp", PublicationTime(item.PublishedAt ?? discovered))) > 0;
    });

    static JsonObject Story(JsonObject row)
    {
        row["tags"] = Json.Load((string)row["tags"]!);
        row["bookmarked"] = row["bookmarked"]!.AsLong() != 0;
        return row;
    }

    static long CalendarBoundary(string value, TimeZoneInfo? zone, bool following)
    {
        var day = Search.ParseIsoDate(value);
        if (following)
        {
            if (day == DateOnly.MaxValue) return long.MaxValue;
            day = day.AddDays(1);
        }
        return PyText.Microseconds(Zones.FromLocal(day.ToDateTime(TimeOnly.MinValue), zone ?? TimeZoneInfo.Local));
    }

    public sealed record StoryQuery
    {
        public string Day { get; init; } = "";
        public string Query { get; init; } = "";
        public string Start { get; init; } = "";
        public string End { get; init; } = "";
        public IReadOnlyList<string> Tags { get; init; } = [];
        public int? TimeStart { get; init; }
        public int? TimeEnd { get; init; }
        public string TimeZone { get; init; } = "";
        public bool Bookmarks { get; init; }
        public bool ShowHidden { get; init; }
        public IReadOnlyList<string> ExcludedRelevance { get; init; } = [];
        public long Page { get; init; } = 1;
        public string Sort { get; init; } = "rank";
    }

    public JsonObject Stories(StoryQuery q, CancellationToken cancellation = default)
    {
        if (!StorySorts.TryGetValue(q.Sort, out var order)) throw new ValidationException("Choose a valid story sort order");
        TimeZoneInfo? zone = null;
        if (q.TimeZone.Length > 0)
        {
            if (PyText.Length(q.TimeZone) > 100) throw new ValidationException("Invalid search timezone");
            zone = Zones.Find(q.TimeZone);
        }
        var clauses = new List<string> { "1=1" };
        var args = new List<(string Name, object? Value)>();
        if (!q.ShowHidden) clauses.Add("(s.status NOT IN ('filtered','duplicate') OR s.bookmarked=1)");
        foreach (var (value, following) in new[] { (q.Day, false), (q.Day, true), (q.Start, false), (q.End, true) })
        {
            if (value.Length == 0) continue;
            var name = "$boundary" + args.Count;
            clauses.Add("s.sort_timestamp" + (following ? "<" : ">=") + name);
            args.Add((name, CalendarBoundary(value, zone, following)));
        }
        var excluded = q.ExcludedRelevance.Distinct().ToList();
        if (excluded.Any(value => value is not ("high" or "medium" or "low" or "pending"))) throw new ValidationException("Invalid relevance filter");
        if (excluded.Count > 0)
        {
            const string bucket = """
                CASE WHEN s.status='pending' OR typeof(s.score)!='integer' OR s.score<0 THEN 'pending'
                    WHEN s.score>=75 THEN 'high' WHEN s.score>=40 THEN 'medium' ELSE 'low' END
                """;
            clauses.Add($"({bucket}) NOT IN ({string.Join(',', excluded.Select((_, i) => "$relevance" + i))})");
            args.AddRange(excluded.Select((value, i) => ("$relevance" + i, (object?)value)));
        }
        var tags = q.Tags.Distinct().ToList();
        if (tags.Count > 0)
        {
            clauses.Add($"EXISTS (SELECT 1 FROM json_each(s.tags) WHERE value IN ({string.Join(',', tags.Select((_, i) => "$tag" + i))}))");
            args.AddRange(tags.Select((tag, i) => ("$tag" + i, (object?)tag)));
        }
        if (q.TimeStart is { } timeStart && q.TimeEnd is { } timeEnd)
        {
            const string minutes = "newsreview_clock(s.sort_timestamp)";
            clauses.Add(timeStart <= timeEnd ? $"({minutes}>=$timeStart AND {minutes}<=$timeEnd)" : $"({minutes}>=$timeStart OR {minutes}<=$timeEnd)");
            args.Add(("$timeStart", timeStart));
            args.Add(("$timeEnd", timeEnd));
        }
        if (q.Query.Length > 0)
        {
            clauses.Add("""(s.headline LIKE $pattern ESCAPE '\' OR s.summary LIKE $pattern ESCAPE '\' OR s.excerpt LIKE $pattern ESCAPE '\' OR s.tags LIKE $pattern ESCAPE '\' OR EXISTS (SELECT 1 FROM sources search_source WHERE search_source.id=s.source_id AND search_source.name LIKE $pattern ESCAPE '\'))""");
            args.Add(("$pattern", "%" + q.Query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        }
        if (q.Bookmarks) clauses.Add("s.bookmarked=1");
        var where = string.Join(" AND ", clauses);
        return Read(db =>
        {
            var clockZone = zone ?? TimeZoneInfo.Local;
            db.Connection.CreateFunction("newsreview_clock", (long timestamp) =>
            {
                var local = TimeZoneInfo.ConvertTime(PyText.FromMicroseconds(timestamp), clockZone);
                return (long)(local.Hour * 60 + local.Minute);
            }, isDeterministic: true);
            // Stop reading once the browser abandons the request instead of finishing a scan nobody will see.
            using var interrupt = cancellation.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Connection.Handle));
            var total = db.Long($"SELECT COUNT(*) FROM stories s WHERE {where}", [.. args]);
            // Materialize only the page IDs before reading display fields. Article bodies and
            // review snapshots never enter the feed query or sort.
            var rows = db.Rows($"""
                WITH page AS MATERIALIZED (
                    SELECT s.id FROM stories s WHERE {where}
                    ORDER BY {order} LIMIT 48 OFFSET $offset)
                    SELECT {FeedColumns},src.name AS source_name,src.category AS source_category,
                    (SELECT COUNT(*) FROM stories d WHERE d.duplicate_of=s.id) AS related_count
                    FROM page JOIN stories s ON s.id=page.id LEFT JOIN sources src ON src.id=s.source_id
                    ORDER BY {order}
                """, [.. args, ("$offset", (q.Page - 1) * 48)]);
            var interests = (JsonObject)db.SettingJson("interests")!;
            var ratings = (interests["feedback"] as JsonObject)?["ratings"] as JsonObject;
            var items = new JsonArray();
            foreach (var row in rows)
            {
                var story = Story(row);
                var rating = ratings?[(string)story["url"]!] as JsonObject;
                story["feedback_vote"] = rating?["vote"]?.DeepClone() ?? 0;
                story["feedback_reason"] = rating?["reason"]?.DeepClone() ?? "";
                items.Add(story);
            }
            return new JsonObject { ["items"] = items, ["total"] = total, ["page"] = q.Page, ["page_size"] = 48 };
        });
    }

    public JsonArray Related(long storyId) => Read(db => new JsonArray(db.Rows("""
        SELECT s.id,s.headline,s.url,src.name AS source_name
            FROM stories s LEFT JOIN sources src ON src.id=s.source_id WHERE s.duplicate_of=$id
        """, ("$id", storyId)).Select(row => (JsonNode)row).ToArray()));

    public void Bookmark(long storyId, bool value) => Write(db =>
    {
        if (db.Execute("UPDATE stories SET bookmarked=$bookmarked WHERE id=$id", ("$bookmarked", value), ("$id", storyId)) == 0) throw new NotFoundException("Story not found");
    });

    public string MarkRead(long storyId) => Write(db =>
    {
        if (db.Execute("UPDATE stories SET read_at=COALESCE(read_at,$now) WHERE id=$id", ("$now", PyText.UtcNow()), ("$id", storyId)) == 0)
            throw new NotFoundException("Story not found");
        return (string)db.Scalar("SELECT read_at FROM stories WHERE id=$id", ("$id", storyId))!;
    });

    public JsonObject FeedbackState() => Feedback.State((JsonObject)Settings()["interests"]!);

    public void RateStory(long storyId, long vote, string reason) => Write(db =>
    {
        var story = db.Row("SELECT s.*,src.name AS source_name FROM stories s LEFT JOIN sources src ON src.id=s.source_id WHERE s.id=$id", ("$id", storyId))
            ?? throw new NotFoundException("Story not found");
        var interests = (JsonObject)db.SettingJson("interests")!;
        var value = Feedback.State(interests);
        var ratings = (JsonObject)value["ratings"]!;
        var url = (string)story["url"]!;
        var previous = ratings[url] as JsonObject;
        if ((vote == 0 && previous is null) ||
            (previous is not null && previous["vote"]!.AsLong() == vote && (string)previous["reason"]! == reason)) return;
        var revision = value["revision"]!.AsLong() + 1;
        value["revision"] = revision;
        if (vote != 0)
        {
            var summary = (string)story["summary"]!;
            ratings[url] = new JsonObject
            {
                ["vote"] = vote, ["reason"] = reason, ["headline"] = (string)story["headline"]!,
                ["source"] = (string?)story["source_name"] ?? "", ["tags"] = Json.Load((string)story["tags"]!),
                ["summary"] = PyText.Truncate(summary.Length > 0 ? summary : (string)story["excerpt"]!, 1200),
                ["updated"] = PyText.UtcNow(), ["sequence"] = revision,
            };
        }
        else ratings.Remove(url);
        if (ratings.Count == 0)
        {
            value["profile"] = "";
            value["profile_revision"] = revision;
        }
        Feedback.Validate(value);
        interests["feedback"] = value;
        db.Execute("UPDATE settings SET value=$value WHERE key='interests'", ("$value", interests));
    });

    public bool SaveFeedbackProfile(string profile, long? revision = null, bool? enabled = null, bool? autoUpdate = null) => Write(db =>
    {
        var interests = (JsonObject)db.SettingJson("interests")!;
        var value = Feedback.State(interests);
        var current = value["revision"]!.AsLong();
        if (revision is not null && revision != current) return false;
        if (revision is null) value["revision"] = ++current;
        value["profile"] = profile;
        value["profile_revision"] = current;
        if (enabled is not null) value["enabled"] = enabled;
        if (autoUpdate is not null) value["auto_update"] = autoUpdate;
        interests["feedback"] = Feedback.Validate(value);
        db.Execute("UPDATE settings SET value=$value WHERE key='interests'", ("$value", interests));
        return true;
    });

    public JsonArray Topics() => Read(db => (JsonArray)db.SettingJson("topics")!);

    /// <summary>
    /// Replaces the managed list. A rename keeps saved story labels under the new name (a case-only change is a rename);
    /// a name left out of the list and not renamed is removed from saved story labels.
    /// </summary>
    public JsonObject SaveTopics(IReadOnlyList<string> topics, IReadOnlyDictionary<string, string> renames, bool removalConfirmed = false) => Write(db =>
    {
        if (topics.Count > 1000) throw new ValidationException("Keep the topic list to at most 1,000 topics");
        for (var index = 0; index < topics.Count; index++)
            if (topics.Take(index).FirstOrDefault(earlier => PyText.FoldEquals(earlier, topics[index])) is { } duplicate)
                throw new ValidationException($"A topic named \u201c{duplicate}\u201d already exists");
        var current = ((JsonArray)db.SettingJson("topics")!).Select(t => (string)t!).ToList();
        var mapping = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (from, to) in renames)
        {
            if (!current.Contains(from) || topics.Contains(from)) throw new ValidationException("Topic list changed; reload the topics and try again");
            if (!topics.Contains(to) || mapping.ContainsValue(to)) throw new ValidationException("Each renamed topic needs one new name in the list");
            if (current.Contains(to) && !renames.ContainsKey(to)) throw new ValidationException($"A topic named \u201c{to}\u201d already exists");
            mapping[from] = to;
        }
        foreach (var name in current.Where(name => !topics.Contains(name) && !mapping.ContainsKey(name)))
            mapping[name] = topics.FirstOrDefault(topic => PyText.FoldEquals(topic, name) && !current.Contains(topic) && !mapping.ContainsValue(topic));
        if (!removalConfirmed && mapping.ContainsValue(null)) throw new ValidationException("Confirm removing topics before continuing");
        long updated = 0;
        if (mapping.Count > 0)
        {
            var rows = db.Rows($"SELECT id,tags FROM stories WHERE EXISTS (SELECT 1 FROM json_each(stories.tags) WHERE value IN ({string.Join(',', mapping.Keys.Select((_, i) => "$topic" + i))}))",
                [.. mapping.Keys.Select((topic, i) => ("$topic" + i, (object?)topic))]);
            foreach (var row in rows)
            {
                var tags = ((JsonArray)Json.Load((string)row["tags"]!)!).Select(t => (string)t!)
                    .Select(value => mapping.TryGetValue(value, out var target) ? target : value).OfType<string>().Distinct()
                    .Select(t => (JsonNode)t).ToArray();
                db.Execute("UPDATE stories SET tags=$tags WHERE id=$id", ("$tags", new JsonArray(tags)), ("$id", row["id"]!.AsLong()));
            }
            updated = rows.Count;
        }
        var sorted = new JsonArray(PyText.SortFolded(topics).Select(n => (JsonNode)n).ToArray());
        db.Execute("UPDATE settings SET value=$value WHERE key='topics'", ("$value", sorted));
        return new JsonObject
        {
            ["topics"] = sorted.DeepClone(), ["updated_stories"] = updated,
            ["renamed"] = new JsonObject(mapping.Where(pair => pair.Value is not null).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
            ["removed"] = new JsonArray(mapping.Where(pair => pair.Value is null).Select(pair => (JsonNode)pair.Key).ToArray()),
        };
    });

    public JsonObject Overview() => Read(db =>
    {
        var counts = new JsonObject();
        foreach (var row in db.Rows("SELECT status,COUNT(*) AS count FROM stories GROUP BY status")) counts[(string)row["status"]!] = row["count"]!.DeepClone();
        var runs = db.Rows("SELECT * FROM runs ORDER BY id DESC LIMIT 8");
        foreach (var run in runs) run["details"] = Json.Load((string)run["details"]!);
        return new JsonObject
        {
            ["dates"] = new JsonArray(db.Rows("SELECT DISTINCT day FROM stories ORDER BY day DESC").Select(r => r["day"]!.DeepClone()).ToArray()),
            ["tags"] = db.SettingJson("topics"),
            ["counts"] = counts,
            ["runs"] = new JsonArray(runs.Select(r => (JsonNode)r).ToArray()),
            ["revision"] = db.SettingJson("data_revision"),
        };
    });

    public string Revision() => Read(db => (string)db.SettingJson("data_revision")!);

    public List<JsonObject> Pending(long limit, IReadOnlyList<long>? sourceIds = null)
    {
        if (sourceIds is { Count: 0 }) return [];
        var scope = sourceIds is null ? "" : " AND s.source_id IN (" + string.Join(',', sourceIds.Select((_, i) => "$source" + i)) + ")";
        return Read(db => db.Rows("""
            SELECT s.*,src.category FROM stories s
                LEFT JOIN sources src ON s.source_id=src.id WHERE s.status='pending' AND s.attempts<3
            """ + scope + """
                 ORDER BY s.attempts,s.discovered_at DESC,s.id LIMIT $limit
            """, [.. (sourceIds ?? []).Select((id, i) => ("$source" + i, (object?)id)), ("$limit", limit)]));
    }

    public List<JsonObject> Recent() => Read(db => db.Rows("""
        SELECT id,headline,summary FROM stories
            WHERE status='reviewed' ORDER BY discovered_at DESC LIMIT 150
        """));

    public void ReviewResult(long storyId, JsonObject result, JsonObject config, JsonObject interests, string text, string basis) => Write(db =>
    {
        db.Execute("""
            UPDATE stories SET summary=$summary,why_it_matters='',tags=$tags,status=$status,score=$score,milestone=$milestone,
                duplicate_of=$duplicateOf,review_error=NULL,attempts=attempts+1,reviewed_at=$now,provider=$provider,model=$model,
                interests_snapshot=$interests,article_text=$text,content_basis=$basis,input_tokens=$inputTokens,output_tokens=$outputTokens WHERE id=$id
            """, ("$summary", (string)result["summary"]!), ("$tags", result["tags"]), ("$status", (string)result["status"]!),
            ("$score", result["score"]!.AsLong()), ("$milestone", (string)result["milestone"]!), ("$duplicateOf", result["duplicate_of"]?.AsLong()),
            ("$now", PyText.UtcNow()), ("$provider", (string)config["provider"]!), ("$model", (string)config["model"]!), ("$interests", interests),
            ("$text", text), ("$basis", basis), ("$inputTokens", result["input_tokens"]?.AsLong()), ("$outputTokens", result["output_tokens"]?.AsLong()),
            ("$id", storyId));
    });

    public void ReviewFailed(long storyId, string error, string text, string basis) => Write(db =>
    {
        db.Execute("UPDATE stories SET review_error=$error,attempts=attempts+1,article_text=$text,content_basis=$basis WHERE id=$id", ("$error", error), ("$text", text), ("$basis", basis), ("$id", storyId));
    });

    public long Requeue(bool failedOnly) => Write(db =>
        (long)db.Execute($"UPDATE stories SET status='pending',attempts=0,review_error=NULL,duplicate_of=NULL WHERE {(failedOnly ? "review_error IS NOT NULL" : "1=1")}"));

    public long DuplicatePurgeCount() => Read(db => db.Long($"SELECT COUNT(*) FROM stories s WHERE {PurgeableDuplicates}"));

    public JsonObject PurgeDuplicates()
    {
        var identifiers = Write(db =>
        {
            var ids = db.Rows($"SELECT s.id FROM stories s WHERE {PurgeableDuplicates}").Select(row => row["id"]!.AsLong()).ToList();
            foreach (var id in ids) db.Execute("DELETE FROM stories WHERE id=$id", ("$id", id));
            if (ids.Count > 0) db.Execute("UPDATE settings SET value=$value WHERE key='data_revision'", ("$value", Json.Dump(NewRevision())));
            return ids;
        });
        var result = new JsonObject { ["deleted"] = identifiers.Count, ["compacted"] = false };
        if (identifiers.Count > 0)
        {
            try
            {
                Read(db => db.Execute("VACUUM"));
                result["compacted"] = true;
            }
            catch (SqliteException)
            {
                result["warning"] = "Duplicates were removed, but database compaction failed. Freed pages can still be reused; check available disk space.";
            }
        }
        return result;
    }

    public long StartRun(string kind, JsonObject details) => Write(db =>
    {
        db.Execute("INSERT INTO runs(kind,started_at,status,details) VALUES ($kind,$now,'running',$details)", ("$kind", kind), ("$now", PyText.UtcNow()), ("$details", details));
        return db.Long("SELECT last_insert_rowid()");
    });

    public void FinishRun(long runId, string status, JsonObject details) => Write(db =>
    {
        db.Execute("UPDATE runs SET finished_at=$now,status=$status,details=$details WHERE id=$id", ("$now", PyText.UtcNow()), ("$status", status), ("$details", details), ("$id", runId));
    });

    /// <summary>The newest full-collection start and the last history reset, which anchor the interval schedule.</summary>
    public (string? LastCollection, string? HistoryCleared) ScheduleAnchors() => Read(db => (
        (string?)db.Scalar("SELECT started_at FROM runs WHERE kind='collect' AND status!='interrupted' AND COALESCE(json_extract(details,'$.scope'),'all')='all' ORDER BY id DESC LIMIT 1"),
        db.Scalar("SELECT value FROM settings WHERE key='history_cleared_at'") is string reset ? (string?)Json.Load(reset) : null));
}
