"""SQLite persistence. Each operation owns its connection and transaction."""

from contextlib import contextmanager
from datetime import datetime, timezone
import json
from pathlib import Path
import sqlite3

from .defaults import SETTINGS, SOURCES
from .health import source_health


class StorageUnavailable(RuntimeError):
    pass


def utcnow():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Store:
    def __init__(self, path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.initialized = False
        with self.connect() as db:
            db.execute("PRAGMA journal_mode=WAL")
            version = db.execute("PRAGMA user_version").fetchone()[0]
            if version > 2:
                raise RuntimeError("Database is newer than this app; upgrade the application.")
            db.executescript("""
                CREATE TABLE IF NOT EXISTS sources (
                    id INTEGER PRIMARY KEY, name TEXT NOT NULL, url TEXT NOT NULL UNIQUE,
                    feed_url TEXT NOT NULL DEFAULT '', category TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1, last_checked TEXT, last_error TEXT);
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
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
                    provider TEXT, model TEXT, interests_snapshot TEXT, input_tokens INTEGER, output_tokens INTEGER);
                CREATE INDEX IF NOT EXISTS stories_day ON stories(day);
                CREATE INDEX IF NOT EXISTS stories_status ON stories(status, attempts);
                CREATE INDEX IF NOT EXISTS stories_duplicate ON stories(duplicate_of);
                CREATE TABLE IF NOT EXISTS runs (
                    id INTEGER PRIMARY KEY, kind TEXT NOT NULL, started_at TEXT NOT NULL,
                    finished_at TEXT, status TEXT NOT NULL, details TEXT NOT NULL DEFAULT '{}');
            """)
            columns = {r['name'] for r in db.execute('PRAGMA table_info(sources)')}
            additions = {
                'mode': "TEXT NOT NULL DEFAULT 'auto'", 'link_pattern': "TEXT NOT NULL DEFAULT ''",
                'stale_days': 'INTEGER NOT NULL DEFAULT 14', 'removed': 'INTEGER NOT NULL DEFAULT 0',
                'last_success': 'TEXT', 'first_checked': 'TEXT', 'newest_published': 'TEXT',
                'last_new_story': 'TEXT', 'last_count': 'INTEGER', 'last_method': 'TEXT',
                'consecutive_failures': 'INTEGER NOT NULL DEFAULT 0', 'collection_note': "TEXT NOT NULL DEFAULT ''",
            }
            for name, definition in additions.items():
                if name not in columns:
                    db.execute(f'ALTER TABLE sources ADD COLUMN {name} {definition}')
            for key, value in SETTINGS.items():
                db.execute("INSERT OR IGNORE INTO settings VALUES (?, ?)", (key, json.dumps(value)))
            if not db.execute("SELECT 1 FROM settings WHERE key='sources_seeded'").fetchone():
                db.executemany("INSERT OR IGNORE INTO sources(name,url,feed_url,category) VALUES (?,?,?,?)", SOURCES)
                db.execute("INSERT INTO settings VALUES ('sources_seeded', 'true')")
            if version < 2:
                # Retain historical source names/relationships, but remove these
                # requested sources from active configuration on existing installs.
                db.execute("UPDATE sources SET removed=1,enabled=0 WHERE url IN ('https://daniel.haxx.se/blog/','https://lwn.net/')")
                db.execute("""UPDATE sources SET first_checked=last_checked,
                    last_success=CASE WHEN last_error IS NULL THEN last_checked ELSE NULL END,
                    consecutive_failures=CASE WHEN last_error IS NULL THEN 0 ELSE 1 END""")
            db.execute('PRAGMA user_version=2')
            db.execute("UPDATE runs SET status='interrupted',finished_at=? WHERE status='running'", (utcnow(),))
        self.initialized = True

    @contextmanager
    def connect(self):
        if self.initialized and not self.path.is_file():
            raise StorageUnavailable('The NewsReview database or data folder was removed while the server was running. Stop and restart NewsReview to initialize a fresh database, or restore your backup. Use Fresh start in Settings to clear history without removing preferences.')
        uri = self.path.resolve().as_uri() + ('?mode=rw' if self.initialized else '?mode=rwc')
        try:
            db = sqlite3.connect(uri, uri=True, timeout=10)
        except sqlite3.OperationalError as exc:
            raise StorageUnavailable('NewsReview cannot open its database. Check that the data folder exists and is writable, then restart the server.') from exc
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA foreign_keys=ON")
        db.execute("PRAGMA busy_timeout=10000")
        db.execute("PRAGMA synchronous=NORMAL")
        try:
            with db:
                yield db
        finally:
            db.close()

    def settings(self):
        with self.connect() as db:
            return {row["key"]: json.loads(row["value"]) for row in db.execute("SELECT * FROM settings") if row["key"] not in ("sources_seeded", "history_cleared_at")}

    def save_settings(self, values):
        with self.connect() as db:
            for key, value in values.items():
                db.execute("UPDATE settings SET value=? WHERE key=?", (json.dumps(value), key))

    def sources(self):
        with self.connect() as db:
            return [{**dict(r), 'health': source_health(dict(r))} for r in db.execute("SELECT * FROM sources WHERE removed=0 ORDER BY name COLLATE NOCASE")]

    def save_source(self, data, source_id=None):
        values = (data["name"], data["url"], data["feed_url"], data["category"], int(data["enabled"]),
                  data.get('mode', 'auto'), data.get('link_pattern', ''), data.get('stale_days', 14))
        with self.connect() as db:
            if source_id is None:
                retired = db.execute('SELECT id FROM sources WHERE url=? AND removed=1', (data['url'],)).fetchone()
                if retired:
                    source_id = retired[0]
                else:
                    return db.execute("INSERT INTO sources(name,url,feed_url,category,enabled,mode,link_pattern,stale_days) VALUES (?,?,?,?,?,?,?,?)", values).lastrowid
            if not db.execute("UPDATE sources SET name=?,url=?,feed_url=?,category=?,enabled=?,mode=?,link_pattern=?,stale_days=?,removed=0 WHERE id=?", (*values, source_id)).rowcount:
                raise ValueError("Source not found")
        return source_id

    def source_checked(self, source_id, error=None, feed_url=None, *, count=None, newest=None, added=0, method=None, note=''):
        with self.connect() as db:
            now = utcnow()
            db.execute("""UPDATE sources SET last_checked=?,first_checked=COALESCE(first_checked,?),last_error=?,
                feed_url=COALESCE(?,feed_url),consecutive_failures=CASE WHEN ? IS NULL THEN 0 ELSE consecutive_failures+1 END,
                last_success=CASE WHEN ? IS NULL THEN ? ELSE last_success END,last_count=?,
                newest_published=CASE WHEN ? IS NULL THEN ? ELSE newest_published END,
                last_new_story=CASE WHEN ?>0 THEN ? ELSE last_new_story END,last_method=COALESCE(?,last_method),collection_note=? WHERE id=?""",
                (now, now, error, feed_url, error, error, now, count, error, newest, added, now, method, note, source_id))

    def insert_story(self, source_id, item, day):
        with self.connect() as db:
            return bool(db.execute("""INSERT OR IGNORE INTO stories
                (source_id,headline,url,excerpt,published_at,discovered_at,day,tags)
                VALUES (?,?,?,?,?,?,?,?)""", (source_id, item["headline"], item["url"], item["excerpt"],
                item["published_at"], utcnow(), day, json.dumps([item["category"]]))).rowcount)

    @staticmethod
    def story(row):
        result = dict(row)
        result["tags"] = json.loads(result["tags"])
        result["bookmarked"] = bool(result["bookmarked"])
        return result

    def stories(self, *, day="", query="", start="", end="", tag="", tags=None,
                time_start=None, time_end=None, bookmarks=False, show_hidden=False, page=1):
        clauses, args = ["1=1"], []
        if not show_hidden:
            clauses.append("(s.status NOT IN ('filtered','duplicate') OR s.bookmarked=1)")
        for value, clause in [(day, "s.day=?"), (start, "s.day>=?"), (end, "s.day<=?")]:
            if value:
                clauses.append(clause)
                args.append(value)
        selected_tags = list(dict.fromkeys([*(tags or []), *([tag] if tag else [])]))
        if selected_tags:
            placeholders = ','.join('?' for _ in selected_tags)
            clauses.append(f"EXISTS (SELECT 1 FROM json_each(s.tags) WHERE value IN ({placeholders}))")
            args.extend(selected_tags)
        if time_start is not None:
            minutes = "(CAST(strftime('%H',s.discovered_at,'localtime') AS INTEGER)*60 + CAST(strftime('%M',s.discovered_at,'localtime') AS INTEGER))"
            if time_start <= time_end:
                clauses.append(f"({minutes}>=? AND {minutes}<=?)")
            else:
                clauses.append(f"({minutes}>=? OR {minutes}<=?)")
            args.extend([time_start, time_end])
        if query:
            clauses.append("(s.headline LIKE ? ESCAPE '\\' OR s.summary LIKE ? ESCAPE '\\' OR s.excerpt LIKE ? ESCAPE '\\' OR s.tags LIKE ? ESCAPE '\\')")
            pattern = "%" + query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
            args.extend([pattern] * 4)
        if bookmarks:
            clauses.append("s.bookmarked=1")
        where = " AND ".join(clauses)
        with self.connect() as db:
            total = db.execute(f"SELECT COUNT(*) FROM stories s WHERE {where}", args).fetchone()[0]
            rows = db.execute(f"""SELECT s.*,src.name AS source_name,src.category AS source_category,
                (SELECT COUNT(*) FROM stories d WHERE d.duplicate_of=s.id) AS related_count
                FROM stories s LEFT JOIN sources src ON src.id=s.source_id WHERE {where}
                ORDER BY s.day DESC,s.score DESC,s.discovered_at DESC,s.id DESC LIMIT 48 OFFSET ?""", (*args, (page - 1) * 48))
            return {"items": [self.story(r) for r in rows], "total": total, "page": page, "page_size": 48}

    def related(self, story_id):
        with self.connect() as db:
            return [dict(r) for r in db.execute("""SELECT s.id,s.headline,s.url,src.name AS source_name
                FROM stories s LEFT JOIN sources src ON src.id=s.source_id WHERE s.duplicate_of=?""", (story_id,))]

    def bookmark(self, story_id, value):
        with self.connect() as db:
            if not db.execute("UPDATE stories SET bookmarked=? WHERE id=?", (int(value), story_id)).rowcount:
                raise ValueError("Story not found")

    def overview(self):
        with self.connect() as db:
            dates = [r[0] for r in db.execute("SELECT DISTINCT day FROM stories ORDER BY day DESC")]
            tags = [r[0] for r in db.execute("SELECT DISTINCT value FROM stories,json_each(stories.tags) ORDER BY value COLLATE NOCASE")]
            counts = dict(db.execute("SELECT status,COUNT(*) FROM stories GROUP BY status"))
            runs = [dict(r) for r in db.execute("SELECT * FROM runs ORDER BY id DESC LIMIT 8")]
            for run in runs:
                run["details"] = json.loads(run["details"])
            return {"dates": dates, "tags": tags, "counts": counts, "runs": runs}

    def pending(self, limit):
        with self.connect() as db:
            return [dict(r) for r in db.execute("""SELECT s.*,src.category FROM stories s
                LEFT JOIN sources src ON s.source_id=src.id WHERE s.status='pending' AND s.attempts<3
                ORDER BY s.attempts,s.discovered_at DESC,s.id LIMIT ?""", (limit,))]

    def recent(self):
        with self.connect() as db:
            return [dict(r) for r in db.execute("""SELECT id,headline,summary FROM stories
                WHERE status='reviewed' ORDER BY discovered_at DESC LIMIT 150""")]

    def review_result(self, story_id, result, config, interests, text, basis):
        with self.connect() as db:
            db.execute("""UPDATE stories SET summary=?,why_it_matters=?,tags=?,status=?,score=?,milestone=?,
                duplicate_of=?,review_error=NULL,attempts=attempts+1,reviewed_at=?,provider=?,model=?,
                interests_snapshot=?,article_text=?,content_basis=?,input_tokens=?,output_tokens=? WHERE id=?""",
                (result["summary"], result["why_it_matters"], json.dumps(result["tags"]), result["status"],
                 result["score"], result["milestone"], result.get("duplicate_of"), utcnow(), config["provider"],
                 config["model"], json.dumps(interests), text, basis, result.get("input_tokens"), result.get("output_tokens"), story_id))

    def review_failed(self, story_id, error, text, basis):
        with self.connect() as db:
            db.execute("UPDATE stories SET review_error=?,attempts=attempts+1,article_text=?,content_basis=? WHERE id=?", (error, text, basis, story_id))

    def requeue(self, failed_only=True):
        with self.connect() as db:
            where = "review_error IS NOT NULL" if failed_only else "1=1"
            return db.execute(f"UPDATE stories SET status='pending',attempts=0,review_error=NULL,duplicate_of=NULL WHERE {where}").rowcount

    def clear_history(self):
        """Remove the archive in one transaction, preserving every user setting."""
        with self.connect() as db:
            count = db.execute("SELECT COUNT(*) FROM stories").fetchone()[0]
            # Break self-references before deletion, including any related coverage.
            db.execute("UPDATE stories SET duplicate_of=NULL")
            db.execute("DELETE FROM stories")
            db.execute("DELETE FROM runs")
            db.execute("""UPDATE sources SET last_checked=NULL,last_error=NULL,last_success=NULL,first_checked=NULL,
                newest_published=NULL,last_new_story=NULL,last_count=NULL,last_method=NULL,consecutive_failures=0,collection_note=''""")
            db.execute("INSERT INTO settings(key,value) VALUES ('history_cleared_at',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (json.dumps(utcnow()),))
            return count

    def start_run(self, kind):
        with self.connect() as db:
            return db.execute("INSERT INTO runs(kind,started_at,status) VALUES (?,?,'running')", (kind, utcnow())).lastrowid

    def finish_run(self, run_id, status, details):
        with self.connect() as db:
            db.execute("UPDATE runs SET finished_at=?,status=?,details=? WHERE id=?", (utcnow(), status, json.dumps(details), run_id))
