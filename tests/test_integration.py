"""Exercise collection, model transports and the real HTTP API end to end."""

from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from newsreview.collector import Collector
from newsreview.feeds import discover
from newsreview.network import Fetcher
from newsreview.reviewer import Reviewer
from newsreview.server import AppServer
from newsreview.store import Store
from tests.test_core import review


class FixtureHandler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def respond(self, content, content_type='text/html', status=200):
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.end_headers()
        self.wfile.write(content.encode())

    def do_GET(self):
        root = f'http://127.0.0.1:{self.server.server_port}'
        if self.path == '/':
            self.respond('<html><head><link rel="alternate" type="application/rss+xml" href="/feed"/></head></html>')
        elif self.path == '/feed':
            today = datetime.now(timezone.utc).isoformat()
            self.respond(f'''<rss><channel><item><title>HTTP browser experimental format</title><link>{root}/article</link>
                <description>Source excerpt with enough useful details.</description><pubDate>{today}</pubDate></item>
                <item><title>Another useful radio project</title><link>{root}/excerpt</link>
                <description>Experimental radio project supports new decoder features.</description></item>
                <item><title>Old news</title><link>{root}/old</link><pubDate>2020-01-01T00:00:00Z</pubDate></item>
                </channel></rss>''', 'application/rss+xml')
        elif self.path == '/article':
            self.respond('<article><p>' + ('A browser added experimental support for a new HTTP feature in a development build. ' * 8) + '</p></article>')
        elif self.path == '/excerpt':
            self.respond('Subscription required', status=403)
        else:
            self.respond('Not found', status=404)

    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        self.server.requests.append((self.path, payload))
        if payload.get('model') == 'broken':
            self.respond('{"message":{"content":"not JSON"}}', 'application/json')
            return
        result = review()
        if self.path == '/api/chat':
            value = {"message": {"content": json.dumps(result)}, "prompt_eval_count": 100, "eval_count": 30}
        elif self.path == '/v1/chat/completions':
            value = {"choices": [{"message": {"content": json.dumps(result)}}], "usage": {"prompt_tokens": 90, "completion_tokens": 25}}
        else:
            self.respond('Not found', status=404)
            return
        self.respond(json.dumps(value), 'application/json')


class IntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        cls.fixture.requests = []
        cls.fixture_thread = threading.Thread(target=cls.fixture.serve_forever, daemon=True)
        cls.fixture_thread.start()
        cls.root = f'http://127.0.0.1:{cls.fixture.server_port}'

    @classmethod
    def tearDownClass(cls):
        cls.fixture.shutdown()
        cls.fixture.server_close()
        cls.fixture_thread.join()

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Path(self.directory.name) / 'news.db')
        self.fetcher = Fetcher(public=False)  # Explicit fixture dependency, never the production source policy.
        self.reviewer = Reviewer(self.fetcher)
        self.collector = Collector(self.store, self.fetcher, self.reviewer)
        self.server = None
        for source in self.store.sources():
            self.store.save_source({**source, 'enabled': False}, source['id'])
        self.store.save_source({'name': 'Fixture', 'url': self.root, 'feed_url': '', 'category': 'Standards', 'enabled': True})

    def tearDown(self):
        self.collector.stop()
        if self.collector.worker:
            self.collector.worker.join(10)
        if self.server:
            self.server.shutdown()
            self.server.server_close()
            self.server_thread.join()
        self.directory.cleanup()

    def model_config(self, provider='ollama', model='fixture'):
        return {**self.store.settings()['ai'], 'provider': provider,
                'base_url': self.root + ('/v1' if provider == 'compatible' else ''), 'model': model}

    def collect(self, kind='collect'):
        self.assertTrue(self.collector.start(kind))
        self.collector.worker.join(10)
        self.assertFalse(self.collector.status()['running'])

    def start_api(self, password=''):
        self.server = AppServer(('127.0.0.1', 0), self.store, self.collector, password)
        self.server_thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.server_thread.start()
        self.api_root = f'http://127.0.0.1:{self.server.server_port}'

    def request(self, path, method='GET', data=None, headers=None):
        request_headers = {'Content-Type': 'application/json', **(headers or {})}
        body = json.dumps(data).encode() if data is not None else None
        try:
            response = urlopen(Request(self.api_root + path, data=body, headers=request_headers, method=method), timeout=5)
        except HTTPError as error:
            response = error
        with response:
            content = response.read().decode()
            value = json.loads(content) if response.headers.get_content_type() == 'application/json' else content
            return response.status, value, response.headers

    def test_discovery_collection_model_review_and_idempotence(self):
        self.assertEqual(discover(self.fetcher, self.root), self.root + '/feed')
        self.store.save_settings({'ai': self.model_config()})
        self.collect()
        self.assertEqual(self.store.stories()['total'], 2)
        stories = self.store.stories()['items']
        self.assertTrue(all(s['status'] == 'reviewed' for s in stories))
        self.assertEqual({s['content_basis'] for s in stories}, {'article', 'excerpt'})
        self.assertTrue(all(s['input_tokens'] == 100 for s in stories))
        self.collect()
        self.assertEqual(self.store.stories()['total'], 2)
        self.assertEqual(self.store.overview()['runs'][0]['details']['added'], 0)

    def test_collection_without_model_and_later_compatible_review(self):
        self.collect()
        self.assertEqual(self.store.overview()['counts']['pending'], 2)
        self.assertIn('No model configured', self.store.overview()['runs'][0]['details']['note'])
        self.store.save_settings({'ai': self.model_config('compatible')})
        self.collect('review')
        self.assertTrue(all(s['provider'] == 'compatible' and s['output_tokens'] == 25 for s in self.store.stories()['items']))
        self.assertEqual(self.fixture.requests[-1][0], '/v1/chat/completions')

    def test_broken_model_preserves_excerpts_and_records_failure(self):
        self.store.save_settings({'ai': self.model_config(model='broken')})
        self.collect()
        stories = self.store.stories()['items']
        self.assertEqual(len(stories), 2)
        self.assertTrue(all(s['status'] == 'pending' and s['review_error'] for s in stories))
        self.assertEqual(self.store.overview()['runs'][0]['status'], 'partial')

    def test_one_source_failure_does_not_abort_others(self):
        self.store.save_source({'name': 'Broken', 'url': self.root + '/missing', 'feed_url': self.root + '/missing', 'category': 'Test', 'enabled': True})
        self.collect()
        self.assertEqual(self.store.stories()['total'], 2)
        self.assertEqual(self.store.overview()['runs'][0]['status'], 'partial')

    def test_model_test_uses_real_adapter_contract(self):
        result = self.reviewer.test(self.model_config())
        self.assertEqual(result['status'], 'reviewed')
        payload = self.fixture.requests[-1][1]
        self.assertFalse(payload['stream'])
        self.assertEqual(payload['format']['type'], 'object')
        self.assertNotIn('tools', payload)

    def test_api_search_bookmarks_settings_sources_and_validation(self):
        self.collect()
        self.start_api()
        status, result, _ = self.request('/api/stories?query=HTTP')
        self.assertEqual(status, 200)
        self.assertEqual(result['total'], 1)
        self.assertNotIn('article_text', result['items'][0])
        story_id = result['items'][0]['id']
        self.assertEqual(self.request(f'/api/bookmarks/{story_id}', 'PUT', {'bookmarked': True})[0], 200)
        self.assertEqual(self.request('/api/stories?bookmarks=1')[1]['total'], 1)
        self.assertEqual(self.request('/api/stories?start=2026-10-16&end=2026-10-01')[0], 400)
        self.assertEqual(self.request('/api/settings', 'PUT', {'ai': self.model_config()})[0], 200)
        self.assertEqual(self.request('/api/settings')[1]['ai']['model'], 'fixture')
        self.assertEqual(self.store.settings()['ai']['model'], 'fixture')
        interests = {**self.store.settings()['interests'], 'guidance': 'Favor radio projects.'}
        self.assertEqual(self.request('/api/settings', 'PUT', {'interests': interests})[0], 200)
        self.assertEqual(self.store.settings()['interests']['guidance'], 'Favor radio projects.')
        self.assertEqual(self.request('/api/test-model', 'POST', self.model_config())[0], 200)
        source = {'name': 'New source', 'url': self.root + '/new', 'feed_url': self.root + '/feed', 'category': 'Radio', 'enabled': True}
        self.assertEqual(self.request('/api/sources', 'POST', source)[0], 201)
        self.assertEqual(self.request('/api/sources', 'POST', source)[0], 409)
        self.assertEqual(self.request('/api/bookmarks/1', 'PUT', {'bookmarked': 'yes'})[0], 400)
        self.assertEqual(self.request('/api/stories?query=%27%20OR%201%3D1--')[1]['total'], 0)
        self.assertEqual(self.request('/data/newsreview.db')[0], 404)
        self.assertEqual(self.request('/../newsreview/server.py')[0], 404)

    def test_authentication_origin_cookie_and_rate_limit(self):
        self.start_api('long-test-password')
        self.assertEqual(self.request('/api/stories')[0], 401)
        self.assertFalse(self.request('/api/session')[1]['authenticated'])
        self.assertEqual(self.request('/api/login', 'POST', {'password': 'wrong'})[0], 401)
        status, _, headers = self.request('/api/login', 'POST', {'password': 'long-test-password'})
        self.assertEqual(status, 200)
        self.assertIn('HttpOnly', headers['Set-Cookie'])
        cookie = headers['Set-Cookie'].split(';', 1)[0]
        self.assertEqual(self.request('/api/stories', headers={'Cookie': cookie})[0], 200)
        self.assertEqual(self.request('/api/collect', 'POST', {}, {'Cookie': cookie, 'Origin': 'https://evil.example'})[0], 403)
        self.assertEqual(self.request('/api/collect', 'POST', {}, {'Cookie': cookie, 'Sec-Fetch-Site': 'cross-site'})[0], 403)
        for _ in range(3):
            self.request('/api/login', 'POST', {'password': 'wrong'})
        self.assertEqual(self.request('/api/login', 'POST', {'password': 'wrong'})[0], 429)

    def test_loopback_guard_and_static_security_headers(self):
        self.start_api()
        self.assertEqual(self.request('/api/overview', headers={'Host': 'attacker.example'})[0], 403)
        status, html, headers = self.request('/')
        self.assertEqual(status, 200)
        self.assertIn('NewsReview', html)
        self.assertIn("script-src 'self'", headers['Content-Security-Policy'])
        self.assertEqual(headers['X-Content-Type-Options'], 'nosniff')

    def test_multitopic_date_time_api_and_confirmed_reset(self):
        self.collect()
        self.start_api()
        self.assertEqual(self.request('/api/stories?tag=Standards&tag=Radio')[1]['total'], 2)
        self.assertEqual(self.request('/api/stories?date_range=1%2F1%2F2000+-+1%2F2%2F2000')[1]['total'], 0)
        self.assertEqual(self.request('/api/stories?time_range=9am+-+9pm')[0], 200)
        self.assertEqual(self.request('/api/stories?time_range=nonsense')[0], 400)
        self.assertEqual(self.request('/api/history/clear', 'POST', {})[0], 400)
        self.assertEqual(self.store.stories()['total'], 2)
        self.collector.lock.acquire()
        try:
            self.assertEqual(self.request('/api/history/clear', 'POST', {'confirmation': 'DELETE HISTORY'})[0], 409)
        finally:
            self.collector.lock.release()
        settings = self.store.settings()
        status, result, _ = self.request('/api/history/clear', 'POST', {'confirmation': 'DELETE HISTORY'})
        self.assertEqual(status, 200)
        self.assertEqual(result['deleted'], 2)
        self.assertEqual(self.store.settings(), settings)
        self.assertEqual(self.request('/api/stories')[1]['total'], 0)
        self.assertEqual(self.request('/api/overview')[1]['runs'], [])


if __name__ == '__main__':
    unittest.main()
