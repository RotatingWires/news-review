"""Coverage for TaskList-style search, multi-topic matching, reset, and rebrand import."""

from datetime import datetime, timezone, timedelta
from pathlib import Path
import tempfile
import unittest

from newsreview.collector import Collector
from newsreview.migration import database_path
from newsreview.search import date_range, time_range
from newsreview.store import Store
from tests.test_core import review
from newsreview.reviewer import validate_review


class RangeTests(unittest.TestCase):
    def test_tasklist_date_formats_and_separators(self):
        self.assertEqual(date_range('10/3 - 10/8', 2026), ('2026-10-03', '2026-10-08'))
        self.assertEqual(date_range('10/3/26 – 10/8/2026'), ('2026-10-03', '2026-10-08'))
        self.assertEqual(date_range('12/30/2025—1/2/2026'), ('2025-12-30', '2026-01-02'))
        self.assertEqual(date_range(''), ('', ''))
        for value in ['2/30 - 3/1', '10/8 - 10/3', '10/3', '2026-10-03 - 2026-10-08']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                date_range(value, 2026)

    def test_tasklist_time_formats_and_overnight(self):
        self.assertEqual(time_range('9am - 9pm'), (540, 1260))
        self.assertEqual(time_range('9:30 p.m. – 2:05 a.m.'), (1290, 125))
        self.assertEqual(time_range('12am - 12pm'), (0, 720))
        self.assertEqual(time_range('9 - 12'), (540, 0))
        self.assertEqual(time_range(''), (None, None))
        for value in ['13pm - 2pm', '9:60 - 10pm', '9pm', '24:00 - 3:00']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                time_range(value)


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.directory.name) / 'newsreview.db')
        self.source = self.store.sources()[0]['id']

    def tearDown(self):
        self.directory.cleanup()

    def insert(self, number, category, hour):
        self.store.insert_story(self.source, {'headline': f'Story {number}', 'url': f'https://example.com/{number}',
                                            'excerpt': 'Details', 'published_at': None, 'category': category}, '2026-10-15')
        local = datetime(2026, 10, 15, hour, 30).astimezone().astimezone(timezone.utc)
        with self.store.connect() as db:
            db.execute('UPDATE stories SET discovered_at=? WHERE url=?', (local.isoformat(), f'https://example.com/{number}'))

    def test_multiple_topics_match_any_and_combine_with_clock_range(self):
        self.insert(1, 'Radio', 10)
        self.insert(2, 'Standards', 22)
        self.insert(3, 'Science', 1)
        self.assertEqual(self.store.stories(tags=['Radio', 'Standards'])['total'], 2)
        self.assertEqual(self.store.stories(tags=['Radio', 'Standards'], time_start=9*60, time_end=21*60)['total'], 1)
        self.assertEqual(self.store.stories(time_start=21*60, time_end=2*60)['total'], 2)
        self.assertEqual(self.store.stories(tags=["' OR 1=1--"])['total'], 0)

    def test_clock_range_matches_publication_in_browser_timezone_and_undated_fallback(self):
        for number in range(1, 6):
            self.insert(number, 'Radio', 1)
        publications = ['2026-10-15T14:00:00+00:00', '2026-10-15T16:00:45+00:00',
                        '2026-10-15T16:01:00+00:00', '2026-01-15T15:30:00+00:00']
        with self.store.connect() as db:
            for number, published in enumerate(publications, 1):
                db.execute('UPDATE stories SET published_at=? WHERE url=?', (published, f'https://example.com/{number}'))
            db.execute("UPDATE stories SET discovered_at='2026-10-15T15:00:00+00:00' WHERE url='https://example.com/5'")
        start, end = time_range('10am - 12pm')
        result = self.store.stories(time_start=start, time_end=end, time_zone='America/New_York')
        self.assertEqual({item['headline'] for item in result['items']}, {'Story 1', 'Story 2', 'Story 4', 'Story 5'})
        self.assertEqual(self.store.stories(time_start=start, time_end=end, time_zone='UTC')['total'], 0)
        self.assertEqual(self.store.stories(start='2026-10-15', end='2026-10-15', time_start=start, time_end=end,
                                          time_zone='America/New_York')['total'], 3)
        with self.assertRaises(ValueError):
            self.store.stories(time_start=start, time_end=end, time_zone='Not/AZone')

    def test_keyword_search_matches_source_names_and_combines_filters(self):
        self.insert(1, 'Radio', 10)
        self.insert(2, 'Science', 11)
        source = next(item for item in self.store.sources() if item['id'] == self.source)
        self.store.save_source({**source, 'name': 'Example 100%_ Source'}, self.source)
        self.assertEqual(self.store.stories(query='example 100%_ source')['total'], 2)
        self.assertEqual(self.store.stories(query='100%', tags=['Radio'])['total'], 1)
        self.assertEqual(self.store.stories(query='100_')['total'], 0, 'SQL wildcards remain literal')
        self.assertEqual(self.store.stories(query='Example', start='2000-01-01', end='2000-01-02')['total'], 0)
        with self.store.connect() as db:
            db.execute('UPDATE sources SET removed=1,enabled=0 WHERE id=?', (self.source,))
        self.assertEqual(self.store.stories(query='Example')['total'], 2, 'Archived source names stay searchable')

    def test_reset_removes_archive_and_preserves_all_preferences(self):
        self.insert(1, 'Radio', 10)
        self.insert(2, 'Radio', 11)
        items = self.store.stories()['items']
        self.store.bookmark(items[0]['id'], True)
        self.store.review_result(items[1]['id'], validate_review(review(duplicate_of=items[0]['id']), {items[0]['id']}),
                                self.store.settings()['ai'], self.store.settings()['interests'], 'text', 'excerpt')
        run_id = self.store.start_run('collect')
        self.store.finish_run(run_id, 'complete', {})
        preferences = self.store.settings()
        source_config = [{k: v for k, v in s.items() if k not in ('last_checked', 'last_error')} for s in self.store.sources()]
        self.assertEqual(self.store.clear_history(), 2)
        self.assertEqual(self.store.stories(show_hidden=True)['total'], 0)
        self.assertEqual(self.store.stories(bookmarks=True)['total'], 0)
        self.assertEqual(self.store.overview()['runs'], [])
        self.assertEqual(self.store.overview()['tags'], [])
        self.assertEqual(self.store.settings(), preferences)
        self.assertEqual([{k: v for k, v in s.items() if k not in ('last_checked', 'last_error')} for s in self.store.sources()], source_config)
        collector = Collector(self.store)
        now = datetime.now().astimezone().replace(hour=23, minute=59)
        self.assertFalse(collector.schedule_due(now))
        self.assertTrue(collector.schedule_due(now + timedelta(days=1)))
        self.assertFalse(collector.start(scheduled=True))
        self.assertEqual(Store(self.store.path).settings(), preferences)

    def test_imports_previous_name_database_without_losing_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            legacy = Store(path / 'tech-news.db')
            interests = {**legacy.settings()['interests'], 'guidance': 'Favor local history.'}
            legacy.save_settings({'interests': interests})
            legacy.insert_story(legacy.sources()[0]['id'], {'headline': 'Old story', 'url': 'https://example.com/old',
                                 'excerpt': 'Text', 'published_at': None, 'category': 'History'}, '2026-10-15')
            migrated = Store(database_path(path))
            self.assertEqual(migrated.stories()['total'], 1)
            self.assertEqual(migrated.settings()['interests']['guidance'], 'Favor local history.')
            migrated.clear_history()
            self.assertEqual(Store(database_path(path)).stories()['total'], 0, 'Never reimport old history once the new database exists')
            self.assertTrue((path / 'tech-news.db').exists())


if __name__ == '__main__':
    unittest.main()
