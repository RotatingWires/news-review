"""Publication boundaries, stable sorting, upgrades, and real SQLite query work."""

from contextlib import contextmanager
from pathlib import Path
import sqlite3
import tempfile
import unittest

from newsreview.store import Store, publication_time
from newsreview.transfer import create_archive, restore_archive


class MeasuredStore(Store):
    """Instrument real inherited queries without replacing SQL or results."""
    measure = False

    @contextmanager
    def connect(self):
        with super().connect() as db:
            if self.measure:
                db.set_trace_callback(self.statements.append)
                db.set_progress_handler(self.progress, 100)
                def authorize(action, table, column, *unused):
                    if action == sqlite3.SQLITE_READ and table == 'stories' and column in (
                            'article_text', 'interests_snapshot', 'why_it_matters'):
                        return sqlite3.SQLITE_DENY
                    return sqlite3.SQLITE_OK
                db.set_authorizer(authorize)
            yield db

    def progress(self):
        self.steps += 100
        return 0


class FeedQueryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.store = MeasuredStore(self.root / 'newsreview.db')
        self.source = self.store.sources()[0]['id']

    def tearDown(self):
        self.directory.cleanup()

    def insert(self, name, published, score=50):
        self.store.insert_story(self.source, {'headline': name, 'url': 'https://example.com/' + name,
                                            'excerpt': 'Source details', 'published_at': published,
                                            'category': 'Technology'}, '2026-10-31')
        with self.store.connect() as db:
            db.execute("UPDATE stories SET status='reviewed',score=?,bookmarked=1 WHERE headline=?", (score, name))

    def names(self, **options):
        return [row['headline'] for row in self.store.stories(**options)['items']]

    def test_sorts_apply_to_whole_filtered_set_and_bookmarks(self):
        self.insert('old-high', '2026-10-30T10:00:00+00:00', 90)
        self.insert('recent-low', '2026-10-31T14:00:00+00:00', 10)
        self.insert('recent-medium', '2026-10-31T08:00:00+00:00', 50)
        expected = {
            'rank': ['old-high', 'recent-medium', 'recent-low'],
            'rank_asc': ['recent-low', 'recent-medium', 'old-high'],
            'published': ['recent-low', 'recent-medium', 'old-high'],
            'published_asc': ['old-high', 'recent-medium', 'recent-low'],
        }
        for sort, names in expected.items():
            for bookmarks in (False, True):
                with self.subTest(sort=sort, bookmarks=bookmarks):
                    self.assertEqual(self.names(sort=sort, bookmarks=bookmarks), names)
        self.assertEqual(self.names(), expected['rank'])
        self.assertEqual(self.names(day='2026-10-31', time_zone='UTC', sort='published_asc'), ['recent-medium', 'recent-low'])
        self.assertEqual(self.names(query='medium', sort='published'), ['recent-medium'])
        with self.assertRaisesRegex(ValueError, 'sort order'):
            self.store.stories(sort='score; DROP TABLE stories')

    def test_exact_offset_and_microsecond_boundaries(self):
        for name, timestamp in [('before', '2026-10-30T23:59:59.999999+00:00'),
                                ('start', '2026-10-31T00:00:00+00:00'),
                                ('offset', '2026-10-31T05:45:00+05:45'),
                                ('last', '2026-10-31T23:59:59.999999+00:00'),
                                ('after', '2026-11-01T00:00:00+00:00')]:
            self.insert(name, timestamp)
        self.assertEqual(set(self.names(day='2026-10-31', time_zone='UTC')), {'start', 'offset', 'last'})
        self.assertEqual(self.names(day='2026-10-31', time_zone='UTC', sort='published')[0], 'last')
        self.assertEqual(set(self.names(start='2026-10-31', end='2026-10-31', time_zone='UTC')), {'start', 'offset', 'last'})
        self.assertEqual(set(self.names(day='2026-11-01', time_zone='Asia/Kathmandu')), {'last', 'after'})

    def test_dst_days_and_repeated_clock_hours(self):
        samples = [('spring-before', '2026-03-08T04:59:59.999999+00:00'),
                   ('spring-start', '2026-03-08T05:00:00+00:00'),
                   ('spring-last', '2026-03-09T03:59:59.999999+00:00'),
                   ('spring-after', '2026-03-09T04:00:00+00:00'),
                   ('fall-first', '2026-11-01T05:30:00+00:00'),
                   ('fall-second', '2026-11-01T06:30:00+00:00'),
                   ('fall-last', '2026-11-02T04:59:59.999999+00:00'),
                   ('fall-after', '2026-11-02T05:00:00+00:00')]
        for name, timestamp in samples:
            self.insert(name, timestamp)
        self.assertEqual(set(self.names(day='2026-03-08', time_zone='America/New_York')), {'spring-start', 'spring-last'})
        self.assertEqual(set(self.names(day='2026-11-01', time_zone='America/New_York')), {'fall-first', 'fall-second', 'fall-last'})
        self.assertEqual(self.names(day='2026-11-01', time_zone='America/New_York', time_start=90, time_end=90,
                                    sort='published_asc'), ['fall-first', 'fall-second'])

    def test_equal_scores_and_times_have_stable_pages(self):
        for number in range(105):
            self.insert(f'story-{number}', '2026-10-31T12:00:00+00:00')
        for sort in ('rank', 'rank_asc', 'published', 'published_asc'):
            with self.subTest(sort=sort):
                names = [name for page in (1, 2, 3) for name in self.names(sort=sort, page=page)]
                expected = list(range(105)) if sort == 'published_asc' else list(reversed(range(105)))
                self.assertEqual(names, [f'story-{number}' for number in expected])

    def test_daily_feed_uses_bounded_covering_indexes_and_never_selects_article_bodies(self):
        with self.store.connect() as db:
            def rows():
                for number in range(6000):
                    stamp = '2026-10-31T12:00:00+00:00' if number < 60 else '2025-01-01T12:00:00+00:00'
                    yield (f'Story {number}', f'https://example.com/{number}', stamp, stamp, publication_time(stamp),
                           'Article body ' * 200, 'Private snapshot ' * 100)
            db.executemany('''INSERT INTO stories(headline,url,published_at,discovered_at,day,sort_timestamp,article_text,interests_snapshot)
                              VALUES (?,?,?,?,'2026-10-31',?,?,?)''', rows())
        self.store.measure = True
        for sort in ('rank', 'rank_asc', 'published', 'published_asc'):
            self.store.statements, self.store.steps = [], 0
            result = self.store.stories(day='2026-10-31', time_zone='UTC', sort=sort)
            self.assertEqual((result['total'], len(result['items'])), (60, 48))
            self.assertLess(self.store.steps, 12000, 'Daily feed work grew with the unrelated archive')
            queries = [sql for sql in self.store.statements if 'SELECT COUNT(*) FROM stories s WHERE' in sql or 'WITH page AS MATERIALIZED' in sql]
            self.assertEqual(len(queries), 2)
            with sqlite3.connect(self.store.path) as db:
                for query in queries:
                    plan = [row[3] for row in db.execute('EXPLAIN QUERY PLAN ' + query)]
                    self.assertTrue(any('SEARCH s USING COVERING INDEX stories_publication' in step for step in plan), plan)
                    self.assertFalse(any(step == 'SCAN s' for step in plan), plan)
            self.assertNotIn('article_text', result['items'][0])

    def test_schema_three_upgrade_and_zip_restore_preserve_original_records(self):
        self.insert('offset', '2026-10-31T02:00:00.123456+02:00', 95)
        self.store.mark_read(1)
        self.store.rate_story(1, 1, 'Keep all preferences')
        settings = self.store.settings()
        with self.store.connect() as db:
            original = dict(db.execute('SELECT * FROM stories').fetchone())
            db.execute('DROP INDEX stories_publication')
            db.execute('DROP INDEX stories_bookmarks')
            db.execute('ALTER TABLE stories DROP COLUMN sort_timestamp')
            db.execute('PRAGMA user_version=3')
        legacy = self.root / 'legacy.zip'
        create_archive(self.store, legacy)
        upgraded = Store(self.store.path)
        with upgraded.connect() as db:
            self.assertEqual(dict(db.execute('SELECT * FROM stories').fetchone()), original)
        self.assertEqual(upgraded.settings(), settings)
        upgraded.reset({'history': True, 'bookmarks': True})
        restore_archive(upgraded, legacy)
        self.assertEqual(upgraded.settings(), settings)
        self.assertEqual(self.names(day='2026-10-31', time_zone='UTC'), ['offset'])
        with upgraded.connect() as db:
            self.assertEqual(dict(db.execute('SELECT * FROM stories').fetchone()), original)
        self.assertEqual(publication_time('2026-10-31T00:00:00.123456Z'), original['sort_timestamp'])

    def test_backfill_covers_multiple_batches_and_rolls_back_failed_upgrade(self):
        with self.store.connect() as db:
            db.execute('DROP INDEX stories_publication')
            db.execute('DROP INDEX stories_bookmarks')
            db.execute('ALTER TABLE stories DROP COLUMN sort_timestamp')
            db.execute('PRAGMA user_version=3')
            db.executemany("""INSERT INTO stories(headline,url,published_at,discovered_at,day)
                              VALUES (?,?,?,?, '2026-10-31')""",
                           ((f'Story {number}', f'https://example.com/{number}',
                             '2026-10-31T12:00:00+00:00' if number < 700 else None,
                             '2026-10-31T13:00:00.123456+00:00') for number in range(1005)))
            db.execute("UPDATE stories SET published_at='bad date' WHERE id=1005")
        with self.assertRaises(ValueError):
            Store(self.store.path)
        with self.store.connect() as db:
            self.assertNotIn('sort_timestamp', {row['name'] for row in db.execute('PRAGMA table_info(stories)')})
            self.assertEqual(db.execute('PRAGMA user_version').fetchone()[0], 3)
            db.execute('UPDATE stories SET published_at=NULL WHERE id=1005')
        upgraded = Store(self.store.path)
        with upgraded.connect() as db:
            self.assertEqual(db.execute('SELECT COUNT(*) FROM stories WHERE sort_timestamp=?',
                                        (publication_time('2026-10-31T12:00:00+00:00'),)).fetchone()[0], 700)
            self.assertEqual(db.execute('SELECT COUNT(*) FROM stories WHERE sort_timestamp=?',
                                        (publication_time('2026-10-31T13:00:00.123456+00:00'),)).fetchone()[0], 305)
