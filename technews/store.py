"""SQLite persistence. Each operation owns its connection and transaction."""

from contextlib import contextmanager
from datetime import datetime, timezone
import json
from pathlib import Path
import sqlite3

from .defaults import SETTINGS, SOURCES


def utcnow():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Store:
    def __init__(self, path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.connect() as db:
            db.execute("PRAGMA journal_mode=WAL")
            version = db.execute("PRAGMA user_version").fetchone()[0]
            if version > 1:
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
                PRAGMA user_version=1;
            """)
            for key, value in SETTINGS.items():
                db.execute("INSERT OR IGNORE INTO settings VALUES (?, ?)", (key, json.dumps(value)))
            if not db.execute("SELECT 1 FROM settings WHERE key='sources_seeded'").fetchone():
                db.executemany("INSERT OR IGNORE INTO sources(name,url,feed_url,category) VALUES (?,?,?,?)", SOURCES)
                db.execute("INSERT INTO settings VALUES ('sources_seeded', 'true')")
            db.execute("UPDATE runs SET status='interrupted',finished_at=? WHERE status='running'", (utcnow(),))

    @contextmanager
    def connect(self):
        db = sqlite3.connect(self.path, timeout=10)
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
            return {row["key"]: json.loads(row["value"]) for row in db.execute("SELECT * FROM settings") if row["key"] != "sources_seeded"}

    def save_settings(self, values):
        with self.connect() as db:
            for key, value in values.items():
                db.execute("UPDATE settings SET value=? WHERE key=?", (json.dumps(value), key))

    def sources(self):
        with self.connect() as db:
            return [dict(r) for r in db.execute("SELECT * FROM sources ORDER BY name COLLATE NOCASE")]

    def save_source(self, data, source_id=None):
        values = (data["name"], data["url"], data["feed_url"], data["category"], int(data["enabled"]))
        with self.connect() as db:
            if source_id is None:
                return db.execute("INSERT INTO sources(name,url,feed_url,category,enabled) VALUES (?,?,?,?,?)", values).lastrowid
            if not db.execute("UPDATE sources SET name=?,url=?,feed_url=?,category=?,enabled=? WHERE id=?", (*values, source_id)).rowcount:
                raise ValueError("Source not found")
        return source_id

    def source_checked(self, source_id, error=None, feed_url=None):
        with self.connect() as db:
            db.execute("UPDATE sources SET last_checked=?,last_error=?,feed_url=COALESCE(?,feed_url) WHERE id=?", (utcnow(), error, feed_url, source_id))

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

    def stories(self, *, day="", query="", start="", end="", tag="", bookmarks=False, show_hidden=False, page=1):
        clauses, args = ["1=1"], []
        if not show_hidden:
            clauses.append("(s.status NOT IN ('filtered','duplicate') OR s.bookmarked=1)")
        for value, clause in [(day, "s.day=?"), (start, "s.day>=?"), (end, "s.day<=?"), (tag, "EXISTS (SELECT 1 FROM json_each(s.tags) WHERE value=?)")]:
            if value:
                clauses.append(clause)
                args.append(value)
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

    def start_run(self, kind):
        with self.connect() as db:
            return db.execute("INSERT INTO runs(kind,started_at,status) VALUES (?,?,'running')", (kind, utcnow())).lastrowid

    def finish_run(self, run_id, status, details):
        with self.connect() as db:
            db.execute("UPDATE runs SET finished_at=?,status=?,details=? WHERE id=?", (utcnow(), status, json.dumps(details), run_id))
