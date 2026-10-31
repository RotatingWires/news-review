"""Real SQLite roundtrips and rejection of damaged/unexpected ZIP contents."""
from contextlib import closing
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from newsreview.search import date_time_range
from newsreview.store import Store, SCHEMA_VERSION
from newsreview.transfer import create_archive, restore_archive


class TransferTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.store = Store(self.root / 'newsreview.db')
        self.store.insert_story(self.store.sources()[0]['id'], {
            'headline': 'Protocol update', 'url': 'https://example.com/story',
            'excerpt': 'Details', 'published_at': None, 'category': 'Protocols'}, '2026-10-15')
        self.story = self.store.stories()['items'][0]['id']
        self.store.bookmark(self.story, True)
        self.archive = self.root / 'export.zip'
        create_archive(self.store, self.archive)

    def tearDown(self):
        self.temp.cleanup()

    def test_openrouter_settings_roundtrip(self):
        config = {**self.store.settings()['ai'], 'provider': 'openrouter',
                  'base_url': 'https://openrouter.ai/api/v1', 'model': 'anthropic/claude-haiku-5.5'}
        self.store.save_settings({'ai': config})
        create_archive(self.store, self.archive)
        self.store.save_settings({'ai': {**config, 'model': ''}})
        restore_archive(self.store, self.archive)
        self.assertEqual(self.store.settings()['ai'], config)

    def test_roundtrip_and_recovery_snapshot(self):
        preferences = self.store.settings()
        self.store.reset({'history': True, 'bookmarks': True, 'guidance': True})
        result = restore_archive(self.store, self.archive)
        self.assertEqual(self.store.stories(bookmarks=True)['total'], 1)
        self.assertEqual(self.store.settings(), preferences)
        recovery = self.root / 'backups' / result['recoveryBackup']
        self.assertTrue(recovery.exists())
        restore_archive(self.store, recovery)
        self.assertEqual(self.store.stories(show_hidden=True)['total'], 0)

    def test_read_markers_roundtrip_and_schema_two_backups_still_restore(self):
        timestamp = self.store.mark_read(self.story)
        self.assertEqual(self.store.mark_read(self.story), timestamp)
        current = self.root / 'read.zip'
        create_archive(self.store, current)
        self.store.reset({'history': True, 'bookmarks': True})
        restore_archive(self.store, current)
        self.assertEqual(self.store.stories()['items'][0]['read_at'], timestamp)
        import sqlite3
        with zipfile.ZipFile(current) as archive:
            manifest = json.loads(archive.read('manifest.json'))
            legacy = self.root / 'legacy.db'
            legacy.write_bytes(archive.read('newsreview.db'))
        with closing(sqlite3.connect(legacy)) as database:
            database.execute('DROP INDEX stories_publication')
            database.execute('DROP INDEX stories_bookmarks')
            database.execute('ALTER TABLE stories DROP COLUMN sort_timestamp')
            database.execute('ALTER TABLE stories DROP COLUMN read_at')
            database.execute('PRAGMA user_version=2')
            database.commit()
        manifest['schema_version'] = 2
        manifest['application_version'] = '0.9.2'
        manifest['sha256'] = hashlib.sha256(legacy.read_bytes()).hexdigest()
        upload = self.root / 'legacy.zip'
        with zipfile.ZipFile(upload, 'w') as archive:
            archive.write(legacy, 'newsreview.db')
            archive.writestr('manifest.json', json.dumps(manifest))
        restore_archive(self.store, upload)
        self.assertIsNone(self.store.stories()['items'][0]['read_at'])
        with self.store.connect() as database:
            self.assertEqual(database.execute('PRAGMA user_version').fetchone()[0], SCHEMA_VERSION)
        # The same version-2 file also upgrades normally when starting the app.
        upgraded = Store(legacy)
        self.assertIsNone(upgraded.stories()['items'][0]['read_at'])

    def test_rejects_bad_checksum_extra_files_newer_version_and_sql_objects(self):
        with zipfile.ZipFile(self.archive) as archive:
            original_db = archive.read('newsreview.db')
            original_manifest = json.loads(archive.read('manifest.json'))
        for mutation in ('checksum', 'extra', 'version', 'view', 'sort_timestamp'):
            with self.subTest(mutation=mutation):
                manifest = dict(original_manifest)
                database = original_db
                if mutation == 'checksum':
                    manifest['sha256'] = '0' * 64
                if mutation == 'version':
                    manifest['application_version'] = '99.0.0'
                if mutation in ('view', 'sort_timestamp'):
                    import sqlite3
                    path = self.root / 'mutant.db'
                    path.write_bytes(database)
                    with closing(sqlite3.connect(path)) as db:
                        db.execute('CREATE VIEW unwanted AS SELECT * FROM stories' if mutation == 'view' else
                                   'UPDATE stories SET sort_timestamp=sort_timestamp+1')
                        db.commit()
                    database = path.read_bytes()
                    manifest['sha256'] = hashlib.sha256(database).hexdigest()
                altered = self.root / 'altered.zip'
                with zipfile.ZipFile(altered, 'w') as archive:
                    archive.writestr('manifest.json', json.dumps(manifest))
                    archive.writestr('newsreview.db', database)
                    if mutation == 'extra':
                        archive.writestr('../escape', 'unexpected')
                with self.assertRaises(ValueError):
                    restore_archive(self.store, altered)
                self.assertEqual(self.store.stories(bookmarks=True)['total'], 1)
        self.assertFalse((self.root.parent / 'escape').exists())

    def test_selective_reset_retains_bookmarks_and_unselected_preferences(self):
        preferences = self.store.settings()
        self.store.reset({'history': True})
        self.assertEqual(self.store.stories(bookmarks=True)['total'], 1)
        self.assertEqual(self.store.settings(), preferences)
        self.store.reset({'bookmarks': True})
        self.assertEqual(self.store.stories()['total'], 1)
        self.assertEqual(self.store.stories(bookmarks=True)['total'], 0)
        self.store.reset({'topics': True})
        self.assertEqual(self.store.settings()['interests']['topics'], [])
        self.assertEqual(self.store.settings()['interests']['guidance'], preferences['interests']['guidance'])
        self.store.reset({'sources': True})
        self.assertEqual(Store(self.store.path).sources(), [])
        self.assertEqual(self.store.stories()['total'], 1)
        for invalid in ({}, {'history': 1}, {'unknown': True}):
            with self.assertRaises(ValueError):
                self.store.reset(invalid)

    def test_combined_search(self):
        self.assertEqual(date_time_range('9/1/26 - 10/1/26, 10am - 3pm'),
                         dict(start='2026-09-01', end='2026-10-01', time_start=600, time_end=900))
        self.assertEqual(date_time_range('10pm - 2am')['time_end'], 120)
        self.assertIsNone(date_time_range('9/1/26 - 10/1/26')['time_start'])
        with self.assertRaises(ValueError):
            date_time_range('9/1 - 10/1, 9am - 2pm, extra')
