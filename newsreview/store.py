"""SQLite persistence. Each operation owns its connection and transaction."""

from contextlib import contextmanager
from datetime import date, datetime, time, timedelta, timezone
import json
from pathlib import Path
import sqlite3
import secrets
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from .defaults import SETTINGS, SOURCES, SOURCE_UPDATES
from .health import source_health
from . import feedback

SCHEMA_VERSION = 4
EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)
STORY_SORTS = {
    'rank': 's.score DESC,s.sort_timestamp DESC,s.id DESC',
    'rank_asc': 's.score ASC,s.sort_timestamp DESC,s.id DESC',
    'published': 's.sort_timestamp DESC,s.id DESC',
    'published_asc': 's.sort_timestamp ASC,s.id ASC',
}
FEED_COLUMNS = '''s.id,s.source_id,s.headline,s.url,s.excerpt,s.content_basis,
    s.published_at,s.discovered_at,s.day,s.summary,s.tags,s.status,s.score,s.bookmarked,
    s.milestone,s.duplicate_of,s.review_error,s.attempts,s.reviewed_at,s.provider,s.model,
    s.input_tokens,s.output_tokens,s.read_at'''
PURGEABLE_DUPLICATES = """s.status='duplicate' AND s.bookmarked=0
    AND NOT EXISTS (SELECT 1 FROM stories child WHERE child.duplicate_of=s.id)"""


class StorageUnavailable(RuntimeError):
    pass


def utcnow():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def publication_time(value):
    """Exact UTC microseconds, retaining original timestamp strings separately."""
    delta = datetime.fromisoformat(value).astimezone(timezone.utc) - EPOCH
    return (delta.days * 86400 + delta.seconds) * 1000000 + delta.microseconds


def backfill_story_times(db):
    rows = db.execute('SELECT id,COALESCE(published_at,discovered_at) FROM stories ORDER BY id')
    while batch := rows.fetchmany(500):
        db.executemany('UPDATE stories SET sort_timestamp=? WHERE id=?',
                       ((publication_time(timestamp), identifier) for identifier, timestamp in batch))


def calendar_boundary(value, zone, *, following=False):
    day = date.fromisoformat(value)
    if following:
        if day == date.max:
            return 2**63 - 1
        day += timedelta(days=1)
    midnight = datetime.combine(day, time.min)
    if zone is not None:
        midnight = midnight.replace(tzinfo=zone)
    return publication_time(midnight.isoformat())


def initialize_topics(db):
    """Import existing labels once, without promoting future source/model output."""
    if db.execute("SELECT 1 FROM settings WHERE key='topics'").fetchone():
        return
    names = {row[0] for row in db.execute('SELECT category FROM sources')}
    names.update(row[0] for row in db.execute('SELECT DISTINCT value FROM stories,json_each(stories.tags)'))
    db.execute("INSERT INTO settings VALUES ('topics',?)", (json.dumps(sorted(names, key=str.casefold)),))


class Store:
    def __init__(self, path):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.initialized = False
        with self.connect() as db:
            db.execute("PRAGMA journal_mode=WAL")
            version = db.execute("PRAGMA user_version").fetchone()[0]
            if version > SCHEMA_VERSION:
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
                    provider TEXT, model TEXT, interests_snapshot TEXT, input_tokens INTEGER, output_tokens INTEGER, read_at TEXT);
                CREATE INDEX IF NOT EXISTS stories_day ON stories(day);
                CREATE INDEX IF NOT EXISTS stories_status ON stories(status, attempts);
                CREATE INDEX IF NOT EXISTS stories_duplicate ON stories(duplicate_of);
                CREATE TABLE IF NOT EXISTS runs (
                    id INTEGER PRIMARY KEY, kind TEXT NOT NULL, started_at TEXT NOT NULL,
                    finished_at TEXT, status TEXT NOT NULL, details TEXT NOT NULL DEFAULT '{}');
            """)
            if version < 3 and 'read_at' not in {row['name'] for row in db.execute('PRAGMA table_info(stories)')}:
                db.execute('ALTER TABLE stories ADD COLUMN read_at TEXT')
            if 'sort_timestamp' not in {row['name'] for row in db.execute('PRAGMA table_info(stories)')}:
                db.execute('BEGIN IMMEDIATE')
                db.execute('ALTER TABLE stories ADD COLUMN sort_timestamp INTEGER NOT NULL DEFAULT 0')
                backfill_story_times(db)
            db.execute('CREATE INDEX IF NOT EXISTS stories_publication ON stories(sort_timestamp,id,score,status,bookmarked)')
            db.execute('CREATE INDEX IF NOT EXISTS stories_bookmarks ON stories(bookmarked,sort_timestamp,id,score,status)')
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
            db.execute("INSERT OR IGNORE INTO settings VALUES ('data_revision',?)", (json.dumps(secrets.token_hex(16)),))
            if not db.execute("SELECT 1 FROM settings WHERE key='sources_seeded'").fetchone():
                db.executemany("INSERT OR IGNORE INTO sources(name,url,feed_url,category) VALUES (?,?,?,?)", SOURCES)
                db.execute("INSERT INTO settings VALUES ('sources_seeded', 'true')")
            for marker, additions in SOURCE_UPDATES.items():
                if not db.execute("SELECT 1 FROM settings WHERE key=?", (marker,)).fetchone():
                    db.executemany("INSERT OR IGNORE INTO sources(name,url,feed_url,category) VALUES (?,?,?,?)", additions)
                    db.execute("INSERT INTO settings VALUES (?, 'true')", (marker,))
            initialize_topics(db)
            if version < 2:
                # Retain historical source names/relationships, but remove these
                # requested sources from active configuration on existing installs.
                db.execute("UPDATE sources SET removed=1,enabled=0 WHERE url IN ('https://daniel.haxx.se/blog/','https://lwn.net/')")
                db.execute("""UPDATE sources SET first_checked=last_checked,
                    last_success=CASE WHEN last_error IS NULL THEN last_checked ELSE NULL END,
                    consecutive_failures=CASE WHEN last_error IS NULL THEN 0 ELSE 1 END""")
            db.execute(f'PRAGMA user_version={SCHEMA_VERSION}')
            db.execute("UPDATE runs SET status='interrupted',finished_at=? WHERE status='running'", (utcnow(),))
        self.initialized = True

    @contextmanager
    def connect(self):
        if self.initialized and not self.path.is_file():
            raise StorageUnavailable('The NewsReview database or data folder was removed while the server was running. Stop and restart NewsReview to initialize a fresh database, or restore your backup. Use Fresh start in Settings to clear history without removing preferences.')
        uri = self.path.resolve().as_uri() + ('?mode=rw' if self.initialized else '?mode=rwc')
        db = None
        try:
            db = sqlite3.connect(uri, uri=True, timeout=10)
            db.row_factory = sqlite3.Row
            db.execute("PRAGMA foreign_keys=ON")
            db.execute("PRAGMA busy_timeout=10000")
            db.execute("PRAGMA synchronous=NORMAL")
            with db:
                yield db
        except sqlite3.OperationalError as exc:
            if getattr(exc, 'sqlite_errorcode', 0) & 0xff == sqlite3.SQLITE_CANTOPEN:
                raise StorageUnavailable(
                    f'NewsReview cannot open its database at {self.path}. Check that the data folder is writable '
                    'and SQLite can create/open its -wal and -shm files there. Stop other NewsReview processes. '
                    'If using a pooled or network drive, test a copy of the data folder on a regular local disk '
                    'with --data-dir. Keep the original database and companion files together; do not delete them.'
                ) from exc
            raise
        finally:
            if db is not None:
                db.close()

    def settings(self):
        with self.connect() as db:
            values = {row["key"]: json.loads(row["value"]) for row in db.execute("SELECT * FROM settings") if row["key"] in SETTINGS}
            values["interests"].setdefault("feedback", feedback.state(values["interests"]))
            return values

    def save_settings(self, values):
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            for key, value in values.items():
                if key == 'interests' and 'feedback' not in value:
                    current = json.loads(db.execute("SELECT value FROM settings WHERE key='interests'").fetchone()[0])
                    value = {**value, 'feedback': feedback.state(current)}
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
            topics = json.loads(db.execute("SELECT value FROM settings WHERE key='topics'").fetchone()[0])
            tags = [name for name in topics if name.casefold() == item['category'].casefold()][:1]
            discovered = utcnow()
            return bool(db.execute("""INSERT OR IGNORE INTO stories
                (source_id,headline,url,excerpt,published_at,discovered_at,day,tags,sort_timestamp)
                VALUES (?,?,?,?,?,?,?,?,?)""", (source_id, item["headline"], item["url"], item["excerpt"],
                item["published_at"], discovered, day, json.dumps(tags), publication_time(item['published_at'] or discovered))).rowcount)

    @staticmethod
    def story(row):
        result = dict(row)
        result["tags"] = json.loads(result["tags"])
        result["bookmarked"] = bool(result["bookmarked"])
        return result

    def stories(self, *, day="", query="", start="", end="", tag="", tags=None,
                time_start=None, time_end=None, time_zone="", bookmarks=False, show_hidden=False, excluded_relevance=None, page=1, sort='rank'):
        if sort not in STORY_SORTS:
            raise ValueError('Choose a valid story sort order')
        zone = None
        if time_zone:
            if not isinstance(time_zone, str) or len(time_zone) > 100:
                raise ValueError('Invalid search timezone')
            try:
                zone = ZoneInfo(time_zone)
            except (ZoneInfoNotFoundError, ValueError) as exc:
                raise ValueError('Search timezone is unavailable; install system timezone data or the Python tzdata package') from exc

        def clock_minutes(timestamp):
            local = (EPOCH + timedelta(microseconds=timestamp)).astimezone(zone)
            return local.hour * 60 + local.minute
        clauses, args = ["1=1"], []
        if not show_hidden:
            clauses.append("(s.status NOT IN ('filtered','duplicate') OR s.bookmarked=1)")
        for value, following in [(day, False), (day, True), (start, False), (end, True)]:
            if value:
                clauses.append('s.sort_timestamp' + ('<?' if following else '>=?'))
                args.append(calendar_boundary(value, zone, following=following))
        excluded = list(dict.fromkeys(excluded_relevance or []))
        if any(value not in ('high', 'medium', 'low', 'pending') for value in excluded):
            raise ValueError('Invalid relevance filter')
        if excluded:
            bucket = """CASE WHEN s.status='pending' OR typeof(s.score)!='integer' OR s.score<0 THEN 'pending'
                WHEN s.score>=75 THEN 'high' WHEN s.score>=40 THEN 'medium' ELSE 'low' END"""
            placeholders = ','.join('?' for _ in excluded)
            clauses.append(f"({bucket}) NOT IN ({placeholders})")
            args.extend(excluded)
        selected_tags = list(dict.fromkeys([*(tags or []), *([tag] if tag else [])]))
        if selected_tags:
            placeholders = ','.join('?' for _ in selected_tags)
            clauses.append(f"EXISTS (SELECT 1 FROM json_each(s.tags) WHERE value IN ({placeholders}))")
            args.extend(selected_tags)
        if time_start is not None:
            minutes = "newsreview_clock(s.sort_timestamp)"
            if time_start <= time_end:
                clauses.append(f"({minutes}>=? AND {minutes}<=?)")
            else:
                clauses.append(f"({minutes}>=? OR {minutes}<=?)")
            args.extend([time_start, time_end])
        if query:
            clauses.append("(s.headline LIKE ? ESCAPE '\\' OR s.summary LIKE ? ESCAPE '\\' OR s.excerpt LIKE ? ESCAPE '\\' OR s.tags LIKE ? ESCAPE '\\' OR EXISTS (SELECT 1 FROM sources search_source WHERE search_source.id=s.source_id AND search_source.name LIKE ? ESCAPE '\\'))")
            pattern = "%" + query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
            args.extend([pattern] * 5)
        if bookmarks:
            clauses.append("s.bookmarked=1")
        where = " AND ".join(clauses)
        with self.connect() as db:
            db.create_function("newsreview_clock", 1, clock_minutes, deterministic=True)
            total = db.execute(f"SELECT COUNT(*) FROM stories s WHERE {where}", args).fetchone()[0]
            # Materialize only the page IDs before reading display fields. Article
            # bodies and review snapshots never enter the feed query or sort.
            order = STORY_SORTS[sort]
            rows = db.execute(f"""WITH page AS MATERIALIZED (
                SELECT s.id FROM stories s WHERE {where}
                ORDER BY {order} LIMIT 48 OFFSET ?)
                SELECT {FEED_COLUMNS},src.name AS source_name,src.category AS source_category,
                (SELECT COUNT(*) FROM stories d WHERE d.duplicate_of=s.id) AS related_count
                FROM page JOIN stories s ON s.id=page.id LEFT JOIN sources src ON src.id=s.source_id
                ORDER BY {order}""", (*args, (page - 1) * 48))
            items = [self.story(row) for row in rows]
            interests = json.loads(db.execute("SELECT value FROM settings WHERE key='interests'").fetchone()[0])
            ratings = interests.get('feedback', {}).get('ratings', {})
            for item in items:
                rating = ratings.get(item['url'], {})
                item.update(feedback_vote=rating.get('vote', 0), feedback_reason=rating.get('reason', ''))
            return {"items": items, "total": total, "page": page, "page_size": 48}

    def related(self, story_id):
        with self.connect() as db:
            return [dict(r) for r in db.execute("""SELECT s.id,s.headline,s.url,src.name AS source_name
                FROM stories s LEFT JOIN sources src ON src.id=s.source_id WHERE s.duplicate_of=?""", (story_id,))]

    def bookmark(self, story_id, value):
        with self.connect() as db:
            if not db.execute("UPDATE stories SET bookmarked=? WHERE id=?", (int(value), story_id)).rowcount:
                raise ValueError("Story not found")

    def mark_read(self, story_id):
        with self.connect() as db:
            if not db.execute('UPDATE stories SET read_at=COALESCE(read_at,?) WHERE id=?', (utcnow(), story_id)).rowcount:
                raise ValueError('Story not found')
            return db.execute('SELECT read_at FROM stories WHERE id=?', (story_id,)).fetchone()[0]

    def feedback_state(self):
        return feedback.state(self.settings()['interests'])

    def rate_story(self, story_id, vote, reason):
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            story = db.execute("SELECT s.*,src.name AS source_name FROM stories s LEFT JOIN sources src ON src.id=s.source_id WHERE s.id=?", (story_id,)).fetchone()
            if story is None:
                raise ValueError('Story not found')
            interests = json.loads(db.execute("SELECT value FROM settings WHERE key='interests'").fetchone()[0])
            value = feedback.state(interests)
            previous = value['ratings'].get(story['url'])
            if (vote == 0 and previous is None) or (previous and previous['vote'] == vote and previous['reason'] == reason):
                return
            value['revision'] += 1
            if vote:
                value['ratings'][story['url']] = {'vote': vote, 'reason': reason, 'headline': story['headline'],
                    'source': story['source_name'] or '', 'tags': json.loads(story['tags']),
                    'summary': (story['summary'] or story['excerpt'])[:1200], 'updated': utcnow(), 'sequence': value['revision']}
            else:
                value['ratings'].pop(story['url'], None)
            if not value['ratings']:
                value.update(profile='', profile_revision=value['revision'])
            feedback.validate(value)
            interests['feedback'] = value
            db.execute("UPDATE settings SET value=? WHERE key='interests'", (json.dumps(interests),))

    def save_feedback_profile(self, profile, *, revision=None, enabled=None, auto_update=None):
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            interests = json.loads(db.execute("SELECT value FROM settings WHERE key='interests'").fetchone()[0])
            value = feedback.state(interests)
            if revision is not None and revision != value['revision']:
                return False
            if revision is None:
                value['revision'] += 1
            value.update(profile=profile, profile_revision=value['revision'])
            if enabled is not None:
                value['enabled'] = enabled
            if auto_update is not None:
                value['auto_update'] = auto_update
            interests['feedback'] = feedback.validate(value)
            db.execute("UPDATE settings SET value=? WHERE key='interests'", (json.dumps(interests),))
            return True

    def topics(self):
        with self.connect() as db:
            return json.loads(db.execute("SELECT value FROM settings WHERE key='topics'").fetchone()[0])

    def change_topic(self, action, name, replacement=None):
        if action not in ('add', 'rename', 'delete'):
            raise ValueError('Choose add, rename, or delete')
        with self.connect() as db:
            db.execute('BEGIN IMMEDIATE')
            names = json.loads(db.execute("SELECT value FROM settings WHERE key='topics'").fetchone()[0])
            if action != 'add' and name not in names:
                raise ValueError('Topic not found; reload the topic list')
            if action != 'delete':
                target = replacement if action == 'rename' else name
                if any(value.casefold() == target.casefold() for value in names if action == 'add' or value != name):
                    raise ValueError('A topic with that name already exists')
                names = [target if value == name and action == 'rename' else value for value in names]
                if action == 'add':
                    names.append(target)
            else:
                target = None
                names.remove(name)
            updated = 0
            if action != 'add':
                rows = db.execute("SELECT id,tags FROM stories WHERE EXISTS (SELECT 1 FROM json_each(stories.tags) WHERE value=?)", (name,)).fetchall()
                for row in rows:
                    tags = [target if value == name else value for value in json.loads(row['tags']) if value != name or target is not None]
                    db.execute('UPDATE stories SET tags=? WHERE id=?', (json.dumps(list(dict.fromkeys(tags))), row['id']))
                updated = len(rows)
            names.sort(key=str.casefold)
            db.execute("UPDATE settings SET value=? WHERE key='topics'", (json.dumps(names),))
            return {'topics': names, 'updated_stories': updated}

    def overview(self):
        with self.connect() as db:
            dates = [r[0] for r in db.execute("SELECT DISTINCT day FROM stories ORDER BY day DESC")]
            tags = json.loads(db.execute("SELECT value FROM settings WHERE key='topics'").fetchone()[0])
            counts = dict(db.execute("SELECT status,COUNT(*) FROM stories GROUP BY status"))
            runs = [dict(r) for r in db.execute("SELECT * FROM runs ORDER BY id DESC LIMIT 8")]
            for run in runs:
                run["details"] = json.loads(run["details"])
            revision = json.loads(db.execute("SELECT value FROM settings WHERE key='data_revision'").fetchone()[0])
            return {"dates": dates, "tags": tags, "counts": counts, "runs": runs, 'revision': revision}

    def pending(self, limit, *, source_ids=None):
        if source_ids is not None and not source_ids:
            return []
        scope = '' if source_ids is None else ' AND s.source_id IN (' + ','.join('?' for _ in source_ids) + ')'
        with self.connect() as db:
            return [dict(r) for r in db.execute("""SELECT s.*,src.category FROM stories s
                LEFT JOIN sources src ON s.source_id=src.id WHERE s.status='pending' AND s.attempts<3""" + scope + """
                ORDER BY s.attempts,s.discovered_at DESC,s.id LIMIT ?""", (*(source_ids or []), limit))]

    def recent(self):
        with self.connect() as db:
            return [dict(r) for r in db.execute("""SELECT id,headline,summary FROM stories
                WHERE status='reviewed' ORDER BY discovered_at DESC LIMIT 150""")]

    def review_result(self, story_id, result, config, interests, text, basis):
        with self.connect() as db:
            db.execute("""UPDATE stories SET summary=?,why_it_matters=?,tags=?,status=?,score=?,milestone=?,
                duplicate_of=?,review_error=NULL,attempts=attempts+1,reviewed_at=?,provider=?,model=?,
                interests_snapshot=?,article_text=?,content_basis=?,input_tokens=?,output_tokens=? WHERE id=?""",
                (result["summary"], "", json.dumps(result["tags"]), result["status"],
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
        return self.reset({'history': True, 'bookmarks': True, 'runs': True})['deleted_stories']

    def duplicate_purge_count(self):
        with self.connect() as db:
            return db.execute(f"SELECT COUNT(*) FROM stories s WHERE {PURGEABLE_DUPLICATES}").fetchone()[0]

    def purge_duplicates(self):
        with self.connect() as db:
            db.execute("BEGIN IMMEDIATE")
            identifiers = [row[0] for row in db.execute(f"SELECT s.id FROM stories s WHERE {PURGEABLE_DUPLICATES}")]
            db.executemany("DELETE FROM stories WHERE id=?", [(identifier,) for identifier in identifiers])
            if identifiers:
                db.execute("UPDATE settings SET value=? WHERE key='data_revision'", (json.dumps(secrets.token_hex(16)),))
        result = {'deleted': len(identifiers), 'compacted': False}
        if identifiers:
            try:
                with self.connect() as db:
                    db.execute("VACUUM")
                result['compacted'] = True
            except sqlite3.Error:
                result['warning'] = 'Duplicates were removed, but database compaction failed. Freed pages can still be reused; check available disk space.'
        return result

    def reset(self, selections):
        allowed = {'history', 'bookmarks', 'runs', 'topics', 'guidance', 'exclusions', 'sources', 'ai', 'collection', 'feedback'}
        if not isinstance(selections, dict) or set(selections) - allowed or not any(selections.values()) or any(type(v) is not bool for v in selections.values()):
            raise ValueError('Choose at least one valid reset option')
        with self.connect() as db:
            result = {'deleted_stories': 0, 'cleared_bookmarks': 0}
            if selections.get('history'):
                where = '1=1' if selections.get('bookmarks') else 'bookmarked=0'
                db.execute(f"""UPDATE stories SET duplicate_of=NULL,
                    status=CASE WHEN status='duplicate' THEN CASE WHEN summary='' THEN 'pending' ELSE 'reviewed' END ELSE status END
                    WHERE duplicate_of IN (SELECT id FROM stories WHERE {where})""")
                result['deleted_stories'] = db.execute(f'DELETE FROM stories WHERE {where}').rowcount
            if selections.get('bookmarks'):
                result['cleared_bookmarks'] = db.execute('UPDATE stories SET bookmarked=0 WHERE bookmarked=1').rowcount
            if selections.get('runs'):
                db.execute('DELETE FROM runs')
                db.execute("""UPDATE sources SET last_checked=NULL,last_error=NULL,last_success=NULL,first_checked=NULL,
                    newest_published=NULL,last_new_story=NULL,last_count=NULL,last_method=NULL,consecutive_failures=0,collection_note=''""")
            interests = json.loads(db.execute("SELECT value FROM settings WHERE key='interests'").fetchone()[0])
            if selections.get('feedback'):
                interests['feedback'] = feedback.state({})
            for option, key, empty in [('topics', 'topics', []), ('guidance', 'guidance', ''), ('exclusions', 'exclusions', [])]:
                if selections.get(option):
                    interests[key] = empty
            db.execute("UPDATE settings SET value=? WHERE key='interests'", (json.dumps(interests),))
            for key in ('ai', 'collection'):
                if selections.get(key):
                    db.execute('UPDATE settings SET value=? WHERE key=?', (json.dumps(SETTINGS[key]), key))
            if selections.get('sources'):
                db.execute('UPDATE sources SET removed=1,enabled=0')
            if selections.get('history') or selections.get('runs'):
                db.execute("INSERT INTO settings(key,value) VALUES ('history_cleared_at',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (json.dumps(utcnow()),))
            db.execute("UPDATE settings SET value=? WHERE key='data_revision'", (json.dumps(secrets.token_hex(16)),))
            return result

    def start_run(self, kind, details=None):
        with self.connect() as db:
            return db.execute("INSERT INTO runs(kind,started_at,status,details) VALUES (?,?,'running',?)", (kind, utcnow(), json.dumps(details or {}))).lastrowid

    def finish_run(self, run_id, status, details):
        with self.connect() as db:
            db.execute("UPDATE runs SET finished_at=?,status=?,details=? WHERE id=?", (utcnow(), status, json.dumps(details), run_id))
