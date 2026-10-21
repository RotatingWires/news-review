"""Deterministic coverage with real SQLite and local HTTP fixtures; no monkey patches."""

from copy import deepcopy
from datetime import datetime, timezone, timedelta
import json
from pathlib import Path
import tempfile
import unittest

from newsreview.defaults import SETTINGS
from newsreview.collector import Collector
from newsreview.feeds import extract_article, parse_date, parse_feed, plain
from newsreview.network import canonical_url, check_public, valid_url
from newsreview.reviewer import candidates_for, validate_review
from newsreview.store import Store
from newsreview.validation import settings, source


def review(**values):
    return {"relevant": True, "score": 80, "summary": "An experimental feature is available in development builds.",
            "why_it_matters": "It allows early testing.", "tags": ["HTTP", "Standards"], "milestone": "experimental",
            "duplicate_of": None, **values}


class ParsingTests(unittest.TestCase):
    def test_rss_namespaces_dates_tracking_and_html(self):
        items = parse_feed('''<rss xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel><item>
            <title>New &amp; useful</title><link>https://example.com/article?utm_source=rss&amp;x=1#top</link>
            <pubDate>Thu, 15 Oct 2026 09:00:00 GMT</pubDate>
            <content:encoded><![CDATA[<p>Article <b>details</b>.</p><script>bad()</script>]]></content:encoded>
            </item></channel></rss>''', "https://example.com/feed")
        self.assertEqual(items[0]["url"], "https://example.com/article?x=1")
        self.assertEqual(items[0]["excerpt"], "Article details.")
        self.assertEqual(items[0]["published_at"], "2026-10-15T09:00:00+00:00")

    def test_atom_relative_links_and_summary(self):
        items = parse_feed('''<feed xmlns="http://www.w3.org/2005/Atom"><entry><title>Protocol</title>
            <link rel="self" href="/entry.atom"/><link href="/article"/><summary>Details</summary>
            <published>2026-10-15T11:00:00+02:00</published></entry></feed>''', "https://example.com/feed")
        self.assertEqual(items[0]["url"], "https://example.com/article")
        self.assertEqual(items[0]["published_at"], "2026-10-15T09:00:00+00:00")

    def test_rejects_non_feed_and_dtd(self):
        for content in ('<html><p>Blocked</p></html>', '<!DOCTYPE rss><rss/>', '<rss>'):
            with self.subTest(content=content), self.assertRaises(ValueError):
                parse_feed(content, "https://example.com")

    def test_article_extraction_and_excerpt_fallback(self):
        body = "The proposed image format offers experimental compression features. " * 8
        html = f'<nav><p>{"Menu " * 100}</p></nav><article><p>{body}</p><aside><p>{"Ads " * 100}</p></aside></article>'
        self.assertEqual(extract_article(html), body.strip())
        self.assertEqual(extract_article('<p>Please subscribe to read more.</p>'), '')
        self.assertEqual(extract_article('<script type="application/ld+json">' + json.dumps({"@graph": [{"articleBody": body}]}) + '</script>'), body.strip())

    def test_plain_and_missing_dates(self):
        self.assertEqual(plain('<p>Hello</p><p>world &amp; radio</p>'), 'Hello world & radio')
        self.assertIsNone(parse_date('not a date'))
        self.assertIsNone(parse_date(None))

    def test_url_validation(self):
        for url in ('file:///etc/passwd', 'javascript:alert(1)', 'http://user:password@example.com', 'https://example.com:bad', 'https://example.com/\nfoo'):
            with self.subTest(url=url), self.assertRaises(ValueError):
                valid_url(url)
        with self.assertRaises(ValueError):
            check_public('http://127.0.0.1')
        self.assertEqual(canonical_url('https://example.com/?q=test&utm_medium=rss'), 'https://example.com/?q=test')


class ReviewTests(unittest.TestCase):
    def test_valid_output_and_candidate_references(self):
        self.assertEqual(validate_review(review(), set())["status"], "reviewed")
        self.assertEqual(validate_review(review(relevant=False), set())["status"], "filtered")
        self.assertEqual(validate_review(review(duplicate_of=8), {8})["status"], "duplicate")

    def test_invalid_model_outputs_are_not_persisted(self):
        invalid = [review(score=True), review(score=101), review(relevant="yes"), review(tags=[]),
                   review(summary=""), review(milestone="released probably"), review(duplicate_of=99),
                   review(extra="unexpected"), review(tags=[42])]
        for value in invalid:
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_review(value, {8})

    def test_candidates_require_shared_terms(self):
        recent = [{"id": 1, "headline": "Browser adds experimental image format", "summary": "A"},
                  {"id": 2, "headline": "Phone pricing changes", "summary": "B"}]
        self.assertEqual([i["id"] for i in candidates_for('New experimental browser format arrives', recent)], [1])


class StoreTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.directory.name) / 'news.db')
        self.source = self.store.sources()[0]["id"]

    def tearDown(self):
        self.directory.cleanup()

    def insert(self, headline='New HTTP method', url='https://example.com/story', day='2026-10-15'):
        return self.store.insert_story(self.source, {"headline": headline, "url": url, "excerpt": "Useful details", "published_at": "2026-10-14T08:00:00+00:00", "category": "Protocols"}, day)

    def test_publication_calendar_dates_and_undated_fallback(self):
        self.store.insert_story(self.source, {"headline": "Midnight boundary", "url": "https://example.com/boundary", "excerpt": "", "published_at": "2026-10-17T01:30:00+00:00", "category": "Protocols"}, '2026-10-18')
        self.assertEqual(self.store.stories(day='2026-10-16', time_zone='America/New_York')['total'], 1)
        self.assertEqual(self.store.stories(day='2026-10-17', time_zone='UTC')['total'], 1)
        self.assertEqual(self.store.stories(day='2026-10-18', time_zone='UTC')['total'], 0)
        self.assertEqual(self.store.stories(start='2026-10-16', end='2026-10-16', time_zone='America/New_York')['total'], 1)
        self.store.insert_story(self.source, {"headline": "Undated", "url": "https://example.com/undated", "excerpt": "", "published_at": None, "category": "Protocols"}, '2020-01-01')
        with self.store.connect() as db:
            db.execute("UPDATE stories SET discovered_at='2026-10-18T10:00:00+00:00' WHERE published_at IS NULL")
        result = self.store.stories(day='2026-10-18', time_zone='UTC')
        self.assertEqual([story['headline'] for story in result['items']], ['Undated'])
        self.assertEqual(self.store.stories(time_zone='UTC')['items'][0]['headline'], 'Undated')

    def test_exact_source_list_and_persistent_first_run_defaults(self):
        self.assertEqual(len(self.store.sources()), 37)
        settings = self.store.settings()
        settings["ai"]["model"] = 'local-test'
        self.store.save_settings({"ai": settings["ai"]})
        source = self.store.sources()[0]
        self.store.save_source({**source, "enabled": False}, source["id"])
        reopened = Store(self.store.path)
        self.assertEqual(reopened.settings()["ai"]["model"], 'local-test')
        self.assertEqual(reopened.sources()[0]["enabled"], 0)
        self.assertEqual(len(reopened.sources()), 37)

    def test_unique_url_history_search_bookmark_and_reassessment(self):
        self.assertTrue(self.insert())
        self.assertFalse(self.insert())
        story = self.store.stories(day='2026-10-14', time_zone='UTC')["items"][0]
        self.assertEqual(self.store.stories(day='2026-10-15', time_zone='UTC')['total'], 0)
        self.assertEqual(story["published_at"][:10], '2026-10-14')
        self.store.bookmark(story["id"], True)
        result = validate_review(review(), set())
        self.store.review_result(story["id"], result, SETTINGS["ai"], SETTINGS["interests"], 'article', 'article')
        self.assertEqual(self.store.stories(query='experimental')["total"], 1)
        self.assertEqual(self.store.stories(query='%')["total"], 0)
        self.assertEqual(self.store.stories(tag='HTTP')["total"], 1)
        self.assertEqual(self.store.stories(start='2026-10-16')["total"], 0)
        self.store.requeue(False)
        saved = self.store.stories(bookmarks=True)["items"][0]
        self.assertTrue(saved["bookmarked"])
        self.assertEqual(saved["summary"], result["summary"])
        self.assertEqual(saved["day"], '2026-10-15')
        self.assertEqual(saved["status"], 'pending')

    def test_duplicate_coverage_hidden_but_bookmarks_retained(self):
        self.insert()
        self.insert('HTTP method arrives', 'https://example.com/other')
        items = self.store.stories()["items"]
        original, duplicate = items[0]["id"], items[1]["id"]
        self.store.review_result(duplicate, validate_review(review(duplicate_of=original), {original}), SETTINGS["ai"], SETTINGS["interests"], 'text', 'excerpt')
        self.assertEqual(self.store.stories()["total"], 1)
        self.assertEqual(len(self.store.related(original)), 1)
        self.store.bookmark(duplicate, True)
        self.assertEqual(self.store.stories(bookmarks=True)["total"], 1)

    def test_queue_attempt_limit_and_reset(self):
        self.insert()
        story = self.store.pending(1)[0]
        for _ in range(3):
            self.store.review_failed(story["id"], 'offline', 'excerpt', 'excerpt')
        self.assertEqual(self.store.pending(10), [])
        self.assertEqual(self.store.requeue(True), 1)
        self.assertEqual(len(self.store.pending(10)), 1)

    def test_three_hour_interval_respects_elapsed_time_and_restart(self):
        config = {**self.store.settings()['collection'], 'interval_hours': 3}
        self.store.save_settings({'collection': config})
        now = datetime(2026, 10, 15, 23, 0, tzinfo=timezone.utc)
        run_id = self.store.start_run('collect')
        with self.store.connect() as db:
            db.execute('UPDATE runs SET started_at=? WHERE id=?', (now.isoformat(), run_id))
        self.store.finish_run(run_id, 'failed', {})
        collector = Collector(Store(self.store.path))
        self.assertFalse(collector.schedule_due(now + timedelta(hours=2, minutes=59)))
        self.assertTrue(collector.schedule_due(now + timedelta(hours=3)))
        self.assertTrue(collector.schedule_due(now + timedelta(days=3)))
        for value in (0, -1, 1.5, 8761, True):
            with self.assertRaises(ValueError):
                settings({'collection': {**config, 'interval_hours': value}}, SETTINGS)
        self.assertEqual(settings({'collection': config}, SETTINGS)['collection']['interval_hours'], 3)
        legacy = {key: value for key, value in config.items() if key != 'interval_hours'}
        self.assertEqual(settings({'collection': legacy}, SETTINGS)['collection']['interval_hours'], 24)

    def test_interval_schedule_persists_and_handles_interruption(self):
        collector = Collector(self.store)
        now = datetime(2026, 10, 15, 8, 0, tzinfo=timezone.utc)
        self.assertTrue(collector.schedule_due(now.replace(hour=6)))
        self.assertTrue(collector.schedule_due(now))
        run_id = self.store.start_run('review')
        self.store.finish_run(run_id, 'complete', {})
        self.assertTrue(collector.schedule_due(now), 'Review-only runs do not satisfy the daily collection')
        run_id = self.store.start_run('collect')
        with self.store.connect() as db:
            db.execute('UPDATE runs SET started_at=? WHERE id=?', (now.isoformat(), run_id))
        self.store.finish_run(run_id, 'partial', {})
        self.assertFalse(collector.schedule_due(now), 'An attempted run must not repeat every 20 seconds')
        self.assertTrue(collector.schedule_due(now + timedelta(days=1)))
        self.store.finish_run(run_id, 'interrupted', {})
        self.assertTrue(collector.schedule_due(now))
        config = {**self.store.settings()['collection'], 'enabled': False}
        self.store.save_settings({'collection': config})
        self.assertFalse(collector.schedule_due(now))


class ValidationTests(unittest.TestCase):
    def test_preferences_and_local_endpoint(self):
        ai = {**SETTINGS["ai"], "base_url": "http://192.168.1.42:11434", "model": "test"}
        self.assertEqual(settings({"ai": ai}, SETTINGS)["ai"]["model"], 'test')
        i = deepcopy(SETTINGS["interests"])
        i["topics"].append({"name": "DMR", "preference": "exclude"})
        self.assertEqual(settings({"interests": i}, SETTINGS)["interests"]["topics"][-1]["preference"], 'exclude')
        with self.assertRaises(ValueError):
            settings({"collection": {**SETTINGS["collection"], "max_reviews": -1}}, SETTINGS)
        with self.assertRaises(ValueError):
            source({"name": "Bad", "url": "file:///etc/passwd", "category": "Security"})


if __name__ == '__main__':
    unittest.main()
