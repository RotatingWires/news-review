"""Verify that backups preserve archive state and never overwrite an existing file."""

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from newsreview.store import Store
from tests.fixtures import add_source


class BackupTests(unittest.TestCase):
    def test_consistent_online_copy_and_overwrite_refusal(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            store = Store(path / 'source.db')
            source_id = add_source(store)['id']
            store.insert_story(source_id, {'headline': 'Radio project', 'url': 'https://example.com/radio',
                                          'excerpt': 'A useful project.', 'published_at': None, 'category': 'Radio'}, '2026-10-15')
            story = store.stories()['items'][0]
            store.bookmark(story['id'], True)
            command = [sys.executable, '-m', 'newsreview.backup', '--database', str(store.path), '--output', str(path / 'backup.db')]
            # Keep a reader connection open so this also exercises a live WAL database.
            with store.connect() as live:
                self.assertEqual(live.execute('PRAGMA journal_mode').fetchone()[0], 'wal')
                result = subprocess.run(command, capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)
            backup = Store(path / 'backup.db')
            saved = backup.stories()['items'][0]
            self.assertEqual(saved['headline'], 'Radio project')
            self.assertTrue(saved['bookmarked'])
            refused = subprocess.run(command, capture_output=True, text=True, timeout=10)
            self.assertNotEqual(refused.returncode, 0)
            self.assertIn('already exists', refused.stderr)


if __name__ == '__main__':
    unittest.main()
