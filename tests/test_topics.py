"""Topic cleanup preserves reading data and model tags use a controlled vocabulary."""
from copy import deepcopy
from pathlib import Path
import tempfile
import unittest

from newsreview.reviewer import SCHEMA, validate_review
from newsreview.store import Store
from tests.test_core import review


class TopicTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.directory.name) / 'news.db')
        self.source = self.store.sources()[0]
        self.store.insert_story(self.source['id'], {
            'headline': 'A useful technical development', 'url': 'https://example.com/topic-cleanup',
            'excerpt': 'Source details.', 'published_at': None, 'category': self.source['category'],
        }, '2026-10-25')
        self.identifier = self.store.stories()['items'][0]['id']
        config = self.store.settings()
        self.store.review_result(self.identifier, validate_review(review(tags=['One-off product', 'Strange synonym']), set()),
                                 config['ai'], config['interests'], 'Full article details.', 'article')

    def tearDown(self):
        self.directory.cleanup()

    def test_clear_topics_keeps_all_other_story_and_preference_data(self):
        self.store.bookmark(self.identifier, True)
        self.store.mark_read(self.identifier)
        self.store.rate_story(self.identifier, 1, 'Useful coverage')
        before = self.store.stories(show_hidden=True)['items'][0]
        settings = self.store.settings()
        self.assertEqual(self.store.clear_generated_topics(), 1)
        after = self.store.stories(show_hidden=True)['items'][0]
        self.assertEqual(after['tags'], [self.source['category']])
        self.assertEqual({k: v for k, v in before.items() if k != 'tags'},
                         {k: v for k, v in after.items() if k != 'tags'})
        self.assertEqual(self.store.settings(), settings)
        self.assertEqual(self.store.overview()['tags'], [self.source['category']])
        self.assertEqual(self.store.clear_generated_topics(), 0)
        self.assertEqual(self.store.stories(tags=[self.source['category']])['total'], 1)

    def test_archived_sources_and_missing_source_have_a_category(self):
        with self.store.connect() as db:
            db.execute('UPDATE sources SET removed=1,enabled=0 WHERE id=?', (self.source['id'],))
        self.assertIn(self.source['category'], self.store.topic_categories())
        self.assertEqual(self.store.clear_generated_topics(), 1)
        self.assertEqual(self.store.stories()['items'][0]['tags'], [self.source['category']])
        with self.store.connect() as db:
            db.execute('UPDATE stories SET source_id=NULL WHERE id=?', (self.identifier,))
        self.assertEqual(self.store.clear_generated_topics(), 1)
        self.assertEqual(self.store.stories()['items'][0]['tags'], ['General'])

    def test_vocabulary_uses_source_categories_and_rejects_invented_model_tags(self):
        categories = self.store.topic_categories()
        self.assertNotIn('One-off product', categories)
        schema = deepcopy(SCHEMA)
        result = validate_review(review(tags=[' security ', 'Security']), set(), allowed_tags=['Security'])
        self.assertEqual(result['tags'], ['Security'])
        with self.assertRaisesRegex(ValueError, 'unconfigured topic'):
            validate_review(review(tags=['Invented product tag']), set(), allowed_tags=categories)
        self.assertEqual(SCHEMA, schema)
