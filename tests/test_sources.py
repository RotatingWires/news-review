"""Website extraction, source-health observations, and configuration migrations."""

from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import unittest

from newsreview.health import source_health
from newsreview.listings import parse_listing
from newsreview.store import Store
from newsreview.validation import source


class ListingTests(unittest.TestCase):
    def test_card_excerpts_dates_heading_links_and_exclusions(self):
        html = '''<nav><h2><a href="/nav">A navigation link is not news</a></h2></nav><main>
            <article><header><h2><a href="/news/one?utm_source=home">A useful new protocol ships</a></h2></header>
            <p>An article excerpt.</p><time datetime="2026-10-15"></time><a href="/2026/10/15/">October 15, 2026</a>
            <p><a href="/news/old">A previously mentioned project</a></p></article>
            <h3><a href="/news/two">A useful radio project ships</a></h3>
            <h2><a href="https://evil.example/story">An external website link</a></h2>
            <h2><a href="/category/radio">A radio category archive</a></h2></main>'''
        items = parse_listing(html, 'https://example.com/')
        self.assertEqual([s['url'] for s in items], ['https://example.com/news/one'])
        self.assertEqual(len(parse_listing('<h3><a href="/news/two">A useful radio project ships</a></h3>', 'https://example.com/')), 1)
        self.assertEqual(items[0]['excerpt'], 'An article excerpt. A previously mentioned project')
        self.assertEqual(items[0]['published_at'], '2026-10-15T00:00:00+00:00')

    def test_explicit_url_filter_and_structured_lists(self):
        html = '<div><a href="/posts/one">A new release is available</a><a href="/other">An unrelated page is here</a></div>'
        self.assertEqual(len(parse_listing(html, 'https://example.com/', '/posts/')), 1)
        data = {'@type': 'ItemList', 'itemListElement': [{'@type': 'ListItem', 'item': {
            '@type': 'NewsArticle', 'url': '/science/new', 'headline': 'New scientific research released', 'datePublished': '2026-10-15', 'description': 'Research details.'}}]}
        items = parse_listing('<script type="application/ld+json">' + json.dumps(data) + '</script>', 'https://example.com/')
        self.assertEqual(len(items), 1)
        self.assertEqual(items[0]['published_at'][:10], '2026-10-15')
        for html in ['<html>Enable JavaScript to view stories</html>', '<h2><a href="javascript:bad()">This must never be followed</a></h2>']:
            with self.assertRaises(ValueError):
                parse_listing(html, 'https://example.com/')


class SourceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.directory.name) / 'newsreview.db')
        self.source_id = self.store.sources()[0]['id']

    def tearDown(self):
        self.directory.cleanup()

    def test_health_distinguishes_observed_inactivity_failures_and_stale_checks(self):
        self.store.source_checked(self.source_id, count=5, newest='2026-09-01T00:00:00+00:00')
        config = next(s for s in self.store.sources() if s['id'] == self.source_id)
        config.update(last_checked='2026-10-15T00:00:00+00:00', first_checked='2026-10-01T00:00:00+00:00')
        now = datetime(2026, 10, 15, 12, tzinfo=timezone.utc)
        health = source_health(config, now)
        self.assertEqual(health['status'], 'warning')
        self.assertTrue(any('may be inactive' in w for w in health['warnings']))
        config['stale_days'] = 90
        self.assertEqual(source_health(config, now)['status'], 'healthy')
        config.update(newest_published=None, last_checked='2026-06-01T00:00:00+00:00')
        self.assertTrue(any('health is stale' in w for w in source_health(config, now)['warnings']))
        for _ in range(3):
            self.store.source_checked(self.source_id, 'HTTP 403')
        config = next(s for s in self.store.sources() if s['id'] == self.source_id)
        self.assertEqual(config['health']['status'], 'broken')
        self.store.source_checked(self.source_id, count=0)
        config = next(s for s in self.store.sources() if s['id'] == self.source_id)
        self.assertEqual(config['consecutive_failures'], 0)
        self.assertTrue(any('no story entries' in w for w in config['health']['warnings']))

    def test_removed_defaults_migrate_existing_installs_without_deleting_stories(self):
        with self.store.connect() as db:
            source_id = db.execute("INSERT INTO sources(name,url,feed_url,category) VALUES ('Daniel Stenberg','https://daniel.haxx.se/blog/','','Protocols')").lastrowid
            db.execute('PRAGMA user_version=1')
        self.store.insert_story(source_id, {'headline': 'An archived protocol article', 'url': 'https://example.com/old',
            'excerpt': 'Text', 'published_at': None, 'category': 'Protocols'}, '2026-10-15')
        reopened = Store(self.store.path)
        self.assertEqual(len(reopened.sources()), 25)
        self.assertEqual(reopened.stories()['items'][0]['source_name'], 'Daniel Stenberg')
        self.assertEqual(reopened.stories()['total'], 1)
        restored = reopened.save_source({'name': 'Manually restored', 'url': 'https://daniel.haxx.se/blog/', 'feed_url': '', 'category': 'News', 'enabled': True})
        self.assertEqual(restored, source_id)
        self.assertEqual(len(Store(self.store.path).sources()), 26, 'One-time migration must not repeatedly remove a manually restored source')

    def test_source_modes_and_intervals_are_validated(self):
        config = {'name': 'Website', 'url': 'https://example.com/', 'category': 'News', 'mode': 'page', 'stale_days': 30}
        self.assertEqual(source(config)['mode'], 'page')
        for changes in [{'mode': 'unknown'}, {'stale_days': 0}, {'link_pattern': 42}]:
            with self.assertRaises(ValueError):
                source({**config, **changes})


if __name__ == '__main__':
    unittest.main()
