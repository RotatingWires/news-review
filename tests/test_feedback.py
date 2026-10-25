"""Feedback persistence, model-context bounds, and backup compatibility."""
from copy import deepcopy
from pathlib import Path
import tempfile
import unittest

from newsreview import feedback
from newsreview.defaults import SETTINGS
from newsreview.reviewer import validate_review
from newsreview.store import Store
from newsreview.transfer import create_archive, restore_archive
from newsreview.validation import settings
from tests.test_core import review


class FeedbackTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.directory.name) / 'news.db')
        self.source = self.store.sources()[0]['id']
        self.store.insert_story(self.source, {'headline': 'Practical SDR project', 'url': 'https://example.com/sdr',
                                            'excerpt': 'Useful practical SDR details.', 'published_at': None, 'category': 'Radio'}, '2026-10-25')
        self.story = self.store.stories()['items'][0]['id']

    def tearDown(self):
        self.directory.cleanup()

    def test_rating_reason_edit_remove_and_restart(self):
        self.store.rate_story(self.story, 1, 'More cheap hardware projects')
        value = self.store.feedback_state()
        self.assertEqual(len(value['ratings']), 1)
        self.assertNotEqual(value['revision'], value['profile_revision'])
        self.store.rate_story(self.story, 1, 'More cheap hardware projects')
        self.assertEqual(self.store.feedback_state()['revision'], value['revision'])
        reopened = Store(self.store.path)
        story = reopened.stories()['items'][0]
        self.assertEqual(story['feedback_vote'], 1)
        self.assertEqual(story['feedback_reason'], 'More cheap hardware projects')
        self.store.rate_story(self.story, -1, 'Topic is good; article is a shopping list')
        self.assertEqual(len(self.store.feedback_state()['ratings']), 1)
        self.store.rate_story(self.story, 0, '')
        self.assertEqual(self.store.stories()['items'][0]['feedback_vote'], 0)
        self.assertEqual(self.store.feedback_state()['profile'], '')

    def test_profile_only_context_priority_and_stale_generation(self):
        self.store.rate_story(self.story, -1, 'Keep this private reason out of each article review')
        value = self.store.feedback_state()
        self.assertTrue(self.store.save_feedback_profile('Prefer projects over shopping lists.', revision=value['revision']))
        context = feedback.for_model(self.store.settings()['interests'])
        self.assertEqual(context['guidance'], self.store.settings()['interests']['guidance'])
        self.assertIn('learned_profile', context)
        self.assertNotIn('ratings', str(context))
        self.assertNotIn('private reason', str(context))
        self.store.save_feedback_profile('Manual preferences', enabled=False, auto_update=False)
        self.assertFalse(self.store.save_feedback_profile('Stale generated profile', revision=value['revision']))
        self.assertNotIn('learned_profile', feedback.for_model(self.store.settings()['interests']))
        self.assertEqual(self.store.feedback_state()['profile'], 'Manual preferences')

    def test_history_reset_preserves_feedback_explicit_reset_clears_it_and_zip_roundtrip(self):
        self.store.rate_story(self.story, 1, 'Useful project')
        self.store.save_feedback_profile('Favor practical projects.')
        saved = self.store.settings()
        archive = Path(self.directory.name) / 'feedback.zip'
        create_archive(self.store, archive)
        self.store.reset({'history': True, 'bookmarks': True})
        self.assertEqual(self.store.settings(), saved)
        self.store.reset({'feedback': True})
        self.assertEqual(self.store.feedback_state(), feedback.DEFAULT_FEEDBACK)
        restore_archive(self.store, archive)
        self.assertEqual(self.store.settings(), saved)
        self.assertEqual(self.store.stories()['items'][0]['feedback_vote'], 1)

    def test_interest_saves_preserve_ratings_and_invalid_backup_preferences_reject(self):
        self.store.rate_story(self.story, 1, 'Practical')
        interests = deepcopy(self.store.settings()['interests'])
        interests.pop('feedback')
        interests['guidance'] = 'Explicit manual guidance'
        self.store.save_settings(settings({'interests': interests}, self.store.settings()))
        self.assertEqual(len(self.store.feedback_state()['ratings']), 1)
        value = self.store.settings()['interests']
        value['feedback']['enabled'] = 'yes'
        with self.assertRaises(ValueError):
            settings({'interests': value}, SETTINGS)

    def test_profile_refresh_input_is_bounded_to_latest_ten_ratings(self):
        for number in range(15):
            url = f'https://example.com/feedback-{number}'
            self.store.insert_story(self.source, {'headline': str(number), 'url': url, 'excerpt': 'x' * 1000,
                                                'published_at': None, 'category': 'Radio'}, '2026-10-25')
            with self.store.connect() as db:
                identifier = db.execute('SELECT id FROM stories WHERE url=?', (url,)).fetchone()[0]
            self.store.rate_story(identifier, 1, 'r' * 320)
        selected = feedback.examples(self.store.feedback_state())
        self.assertEqual(len(selected), 10)
        self.assertEqual(selected[0]['headline'], '14')
        self.assertEqual(selected[-1]['headline'], '5')
        self.assertTrue(all(len(item['reason']) <= 320 and len(item['summary']) <= 200 for item in selected))

    def test_all_scores_use_summary_only(self):
        for score in (0, 39, 40, 100):
            result = validate_review(review(score=score), set())
            self.assertNotIn('why_it_matters', result)
            self.assertTrue(result['summary'])
