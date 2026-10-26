"""Owned topics preserve existing labels and remain independent of sources."""
from pathlib import Path
import tempfile
import unittest

from newsreview.reviewer import validate_review
from newsreview.store import Store
from newsreview.transfer import create_archive, restore_archive
from newsreview.validation import topic_names
from tests.test_core import review


class TopicTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.directory.name) / 'news.db')
        self.source = self.store.sources()[0]
        self.store.insert_story(self.source['id'], {
            'headline': 'A useful technical development', 'url': 'https://example.com/topics',
            'excerpt': 'Source details.', 'published_at': None, 'category': self.source['category'],
        }, '2026-10-25')
        self.identifier = self.store.stories()['items'][0]['id']
        config = self.store.settings()
        self.store.review_result(self.identifier, review(tags=['One-off product', 'Strange synonym'], status='reviewed'),
                                 config['ai'], config['interests'], 'Full article details.', 'article')

    def tearDown(self):
        self.directory.cleanup()

    def test_upgrade_keeps_every_existing_topic_and_story_unchanged(self):
        self.store.bookmark(self.identifier, True)
        self.store.mark_read(self.identifier)
        self.store.rate_story(self.identifier, 1, 'Useful coverage')
        before = self.store.stories()['items']
        preferences = self.store.settings()
        with self.store.connect() as db:
            db.execute("DELETE FROM settings WHERE key='topics'")
        reopened = Store(self.store.path)
        self.assertTrue({'One-off product', 'Strange synonym', self.source['category']} <= set(reopened.topics()))
        self.assertEqual(reopened.stories()['items'], before)
        self.assertEqual(reopened.settings(), preferences)
        reopened.change_topic('delete', 'One-off product')
        self.assertNotIn('One-off product', Store(self.store.path).topics())

    def test_add_rename_delete_update_labels_and_keep_reading_data(self):
        self.store.change_topic('add', 'One-off product')
        self.store.change_topic('add', 'Strange synonym')
        self.store.bookmark(self.identifier, True)
        self.store.mark_read(self.identifier)
        self.store.rate_story(self.identifier, 1, 'Keep this feedback')
        before = self.store.stories()['items'][0]
        settings = self.store.settings()
        sources = self.store.sources()
        result = self.store.change_topic('rename', 'One-off product', 'Projects')
        self.assertEqual(result['updated_stories'], 1)
        self.assertEqual(self.store.stories()['items'][0]['tags'], ['Projects', 'Strange synonym'])
        self.assertEqual(self.store.stories(tags=['Projects'])['total'], 1)
        self.assertEqual(self.store.stories(tags=['One-off product'])['total'], 0)
        self.store.change_topic('delete', 'Projects')
        after = self.store.stories()['items'][0]
        self.assertEqual(after['tags'], ['Strange synonym'])
        self.assertEqual({k: v for k, v in after.items() if k != 'tags'},
                         {k: v for k, v in before.items() if k != 'tags'})
        self.assertEqual(self.store.settings(), settings)
        self.assertEqual(self.store.sources(), sources)
        with self.assertRaisesRegex(ValueError, 'already exists'):
            self.store.change_topic('add', 'strange SYNONYM')
        with self.assertRaisesRegex(ValueError, 'already exists'):
            self.store.change_topic('rename', 'Strange synonym', self.source['category'])

    def test_source_edits_and_new_stories_cannot_create_topics(self):
        original = self.store.topics()
        self.store.save_source({**self.source, 'category': 'Unconfigured source category'}, self.source['id'])
        self.store.insert_story(self.source['id'], {
            'headline': 'Another story', 'url': 'https://example.com/another', 'excerpt': '',
            'published_at': None, 'category': 'Unconfigured source category',
        }, '2026-10-25')
        self.assertEqual(self.store.topics(), original)
        self.assertEqual(self.store.stories(query='Another story')['items'][0]['tags'], [])
        self.store.change_topic('delete', self.source['category'])
        self.assertNotIn(self.source['category'], Store(self.store.path).topics())

    def test_empty_list_persists_and_validation_allows_untagged_reviews(self):
        for name in self.store.topics():
            self.store.change_topic('delete', name)
        self.assertEqual(Store(self.store.path).topics(), [])
        self.assertEqual(self.store.overview()['tags'], [])
        self.assertEqual(validate_review(review(tags=[]), set(), allowed_tags=[])['tags'], [])
        with self.assertRaisesRegex(ValueError, 'unconfigured topic'):
            validate_review(review(tags=['Invented']), set(), allowed_tags=[])

    def test_vocabulary_validation_rejects_invented_model_tags(self):
        self.store.change_topic('add', 'My topic')
        self.assertIn('My topic', self.store.overview()['tags'])
        self.assertNotIn('One-off product', self.store.topics())
        result = validate_review(review(tags=[' security ', 'Security']), set(), allowed_tags=['Security'])
        self.assertEqual(result['tags'], ['Security'])
        with self.assertRaisesRegex(ValueError, 'unconfigured topic'):
            validate_review(review(tags=['Invented product tag']), set(), allowed_tags=self.store.topics())
        for value in ('wrong type', [''], ['x' * 61], ['same', 'same'], [123]):
            with self.subTest(value=value), self.assertRaises(ValueError):
                topic_names(value)

    def test_zip_preserves_owned_list_and_older_backups_import_all_labels(self):
        self.store.change_topic('add', 'My custom topic')
        archive = Path(self.directory.name) / 'owned.zip'
        create_archive(self.store, archive)
        saved = self.store.topics()
        self.store.change_topic('delete', 'My custom topic')
        restore_archive(self.store, archive)
        self.assertEqual(self.store.topics(), saved)
        with self.store.connect() as db:
            db.execute("DELETE FROM settings WHERE key='topics'")
        legacy = Path(self.directory.name) / 'legacy.zip'
        create_archive(self.store, legacy)
        self.store = Store(self.store.path)
        self.store.change_topic('add', 'Should be replaced by restore')
        restore_archive(self.store, legacy)
        self.assertTrue({'One-off product', 'Strange synonym'} <= set(self.store.topics()))
        self.assertNotIn('Should be replaced by restore', self.store.topics())

    def test_zip_rejects_invalid_topic_list(self):
        with self.store.connect() as db:
            db.execute("UPDATE settings SET value='\"invalid\"' WHERE key='topics'")
        archive = Path(self.directory.name) / 'invalid.zip'
        create_archive(self.store, archive)
        with self.assertRaisesRegex(ValueError, 'Topics must be a list'):
            restore_archive(self.store, archive)
