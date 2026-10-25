"""Model topics use source categories rather than accumulated article tags."""
from pathlib import Path
import tempfile
import unittest

from newsreview.reviewer import validate_review
from newsreview.store import Store
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
        self.store.review_result(self.identifier, validate_review(review(tags=['One-off product', 'Strange synonym']), set()),
                                 config['ai'], config['interests'], 'Full article details.', 'article')

    def tearDown(self):
        self.directory.cleanup()

    def test_archived_sources_remain_in_topic_vocabulary(self):
        with self.store.connect() as db:
            db.execute('UPDATE sources SET removed=1,enabled=0 WHERE id=?', (self.source['id'],))
        self.assertIn(self.source['category'], self.store.topic_categories())

    def test_vocabulary_uses_source_categories_and_rejects_invented_model_tags(self):
        categories = self.store.topic_categories()
        self.assertNotIn('One-off product', categories)
        result = validate_review(review(tags=[' security ', 'Security']), set(), allowed_tags=['Security'])
        self.assertEqual(result['tags'], ['Security'])
        with self.assertRaisesRegex(ValueError, 'unconfigured topic'):
            validate_review(review(tags=['Invented product tag']), set(), allowed_tags=categories)
