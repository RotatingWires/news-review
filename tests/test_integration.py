"""Exercise collection, model transports and the real HTTP API end to end."""

from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import shutil
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
        elif self.path in ('/stale-feed', '/undated-feed', '/empty-feed'):
            items = '' if self.path == '/empty-feed' else f"""<item><title>A useful original feed story</title><link>{root}/article</link><description>Feed-only excerpt.</description>{'<pubDate>2020-01-01T00:00:00Z</pubDate>' if self.path != '/undated-feed' else ''}</item>
                <item><title>A second original feed story</title><link>{root}/old</link><pubDate>2020-01-01T00:00:00Z</pubDate></item>"""
            if self.path == '/undated-feed':
                items = items.replace('<pubDate>2020-01-01T00:00:00Z</pubDate>', '')
            self.respond(f'<rss><channel>{items}</channel></rss>', 'application/rss+xml')
        elif self.path == '/article':
            self.respond('<article><p>' + ('A browser added experimental support for a new HTTP feature in a development build. ' * 8) + '</p></article>')
        elif self.path == '/listing':
            today = datetime.now(timezone.utc).isoformat()
            self.respond(f'''<html><nav><a href="/unwanted">Navigation should never be a story</a></nav><main>
                <article><header><h2><a href="/article">New radio protocol reaches experimental testing</a></h2></header>
                <time datetime="{today}"></time><p>A useful source excerpt from the article card.</p></article>
                <article><h2><a href="/excerpt">Another practical radio project is available</a></h2></article></main></html>''')
        elif self.path == '/excerpt':
            self.respond('Subscription required', status=403)
        else:
            self.respond('Not found', status=404)

    def do_POST(self):
        payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        self.server.requests.append((self.path, payload))
        if payload.get('model') == 'blocking-review' and self.path == '/api/chat':
            self.server.review_entered.set()
            self.server.review_release.wait(8)
        if self.path == '/api/generate' and payload.get('keep_alive') == 0:
            if payload.get('model') == 'unload-error':
                self.respond('Unload failed', status=503)
                return
            self.respond('{"done":true,"done_reason":"unload"}', 'application/json')
            return
        if payload.get('model', '').startswith('http-error'):
            model = payload['model']
            message = 'Input exceeds the model context window. Reduce input length.'
            error = {'error': {'message': message}} if self.path == '/v1/chat/completions' else {'error': message}
            body = json.dumps(error)
            if model == 'http-error-message':
                body = json.dumps({'message': 'Unsupported parameter:\n\tresponse_format'})
            elif model == 'http-error-html':
                body = '<html><body>Private proxy error page</body></html>'
            elif model == 'http-error-huge':
                body = json.dumps({'error': 'x' * 10000})
            elif model == 'http-error-shape':
                body = json.dumps({'error': ['Unexpected error shape']})
            elif model == 'http-error-long':
                body = json.dumps({'error': 'x' * 1000})
            self.respond(body, 'application/json', status=400)
            return
        schema = payload.get('format') or json.loads(payload.get('messages', [{}, {}])[1].get('content', '{}')).get('schema', {})
        allowed_tags = schema.get('properties', {}).get('tags', {}).get('items', {}).get('enum', ['HTTP', 'Standards'])
        if schema.get('properties', {}).get('tags', {}).get('maxItems') == 0:
            allowed_tags = []
        if payload.get('model') in ('truncated', 'empty-thinking', 'fenced'):
            model = payload['model']
            content = '{"summary":' if model == 'truncated' else '' if model == 'empty-thinking' else '```json\n' + json.dumps(review(tags=allowed_tags[:1])) + '\n```'
            self.respond(json.dumps({'message': {'content': content, 'thinking': 'fixture reasoning' if model == 'empty-thinking' else ''},
                                     'done_reason': 'length' if model == 'truncated' else 'stop'}), 'application/json')
            return
        if payload.get('model') == 'broken':
            self.respond('{"message":{"content":"not JSON"}}', 'application/json')
            return
        result = {'profile': 'Favor practical SDR projects. Avoid shopping lists, while retaining interest in radio technology.'} if payload.get('messages', [{}])[0].get('content', '').startswith('Summarize article ratings') else review(tags=allowed_tags[:1])
        if payload.get('model') == 'invented-topic':
            result = review(tags=['Invented tag that must not be saved'])
        if payload.get('model') == 'bad-profile' and payload.get('messages', [{}])[0].get('content', '').startswith('Summarize article ratings'):
            result = {'profile': 42}
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

    def start_api(self, password=None):
        self.server = AppServer(('127.0.0.1', 0), self.store, self.collector, password or 'fixture-password')
        self.api_cookie = 'newsreview_session=' + self.server.session() if password is None else ''
        self.server_thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.server_thread.start()
        self.api_root = f'http://127.0.0.1:{self.server.server_port}'

    def request(self, path, method='GET', data=None, headers=None):
        request_headers = {'Content-Type': 'application/json', 'Cookie': self.api_cookie, **(headers or {})}
        body = json.dumps(data).encode() if data is not None else None
        try:
            response = urlopen(Request(self.api_root + path, data=body, headers=request_headers, method=method), timeout=5)
        except HTTPError as error:
            response = error
        with response:
            content = response.read().decode()
            value = json.loads(content) if response.headers.get_content_type() == 'application/json' else content
            return response.status, value, response.headers

    def test_keyword_and_time_search_remain_day_scoped_until_date_range(self):
        self.start_api()
        for day in ('2026-10-15', '2026-10-16'):
            self.store.insert_story(next(source['id'] for source in self.store.sources() if source['enabled']), {'headline': 'Shared protocol news', 'url': 'https://example.com/' + day,
                                    'excerpt': '', 'published_at': day + 'T15:00:00+00:00', 'category': 'Protocols'}, day)
        base = '/api/stories?day=2026-10-16&query=Shared&time_zone=UTC'
        self.assertEqual(self.request(base)[1]['total'], 1)
        self.assertEqual(self.request(base + '&period=10am%20-%2012pm')[1]['total'], 0)
        self.assertEqual(self.request(base + '&period=10/15/26%20-%2010/16/26')[1]['total'], 2)
        self.assertEqual(self.request(base + '&period=10/15/26%20-%2010/16/26,%202pm%20-%204pm')[1]['total'], 2)

    def test_duplicate_purge_confirmation_lock_bookmarks_references_and_compaction(self):
        self.start_api()
        source = next(item['id'] for item in self.store.sources() if item['enabled'])
        identifiers = {}
        for name in ('main', 'delete', 'bookmark', 'parent', 'child', 'pending', 'filtered'):
            self.store.insert_story(source, {'headline': name, 'url': 'https://example.com/purge/' + name,
                                            'excerpt': '', 'published_at': None, 'category': 'Security'}, '2026-10-21')
            with self.store.connect() as db:
                identifiers[name] = db.execute('SELECT id FROM stories WHERE headline=?', (name,)).fetchone()[0]
        with self.store.connect() as db:
            for name in ('delete', 'bookmark', 'parent', 'child'):
                db.execute("UPDATE stories SET status='duplicate', duplicate_of=? WHERE id=?", (identifiers['main'], identifiers[name]))
            db.execute("UPDATE stories SET bookmarked=1 WHERE id IN (?,?)", (identifiers['bookmark'], identifiers['child']))
            db.execute('UPDATE stories SET duplicate_of=? WHERE id=?', (identifiers['parent'], identifiers['child']))
            db.execute("UPDATE stories SET status='filtered' WHERE id=?", (identifiers['filtered'],))
            db.execute("UPDATE stories SET article_text=? WHERE id=?", ('Redundant article text ' * 30000, identifiers['delete']))
        settings = self.store.settings()
        revision = self.store.overview()['revision']
        with self.store.connect() as db:
            before_pages = db.execute('PRAGMA page_count').fetchone()[0]
        self.assertEqual(self.request('/api/maintenance/duplicates')[1]['count'], 1)
        path = '/api/maintenance/duplicates/purge'
        self.assertEqual(self.request(path, 'POST', {})[0], 400)
        data = {'confirmation': 'PURGE DUPLICATES'}
        self.collector.lock.acquire()
        try:
            self.assertEqual(self.request(path, 'POST', data)[0], 409)
        finally:
            self.collector.lock.release()
        self.assertEqual(self.request(path, 'POST', data, {'Origin': 'https://evil.example'})[0], 403)
        status, result, _ = self.request(path, 'POST', data)
        self.assertEqual(status, 200)
        self.assertEqual(result, {'deleted': 1, 'compacted': True})
        self.assertEqual(self.store.settings(), settings)
        self.assertNotEqual(self.store.overview()['revision'], revision)
        self.assertEqual(self.store.stories(show_hidden=True)['total'], 6)
        self.assertEqual(self.store.stories(bookmarks=True)['total'], 2)
        with self.store.connect() as db:
            self.assertEqual(db.execute('PRAGMA foreign_key_check').fetchall(), [])
            self.assertLess(db.execute('PRAGMA page_count').fetchone()[0], before_pages)
        self.assertEqual(self.request('/api/maintenance/duplicates')[1]['count'], 0)
        self.assertEqual(self.request(path, 'POST', data)[1]['deleted'], 0)
        self.assertEqual(self.request(path, 'POST', data, {'Cookie': ''})[0], 401)

    def test_topics_api_preserves_existing_topics_and_guards_edits(self):
        self.start_api()
        existing = self.request('/api/topics')[1]
        self.assertEqual(existing, self.store.topics())
        add = {'action': 'add', 'name': 'Custom topic'}
        self.assertEqual(self.request('/api/topics', 'POST', add, {'Cookie': ''})[0], 401)
        self.collector.lock.acquire()
        try:
            self.assertEqual(self.request('/api/topics', 'POST', add)[0], 409)
        finally:
            self.collector.lock.release()
        status, result, _ = self.request('/api/topics', 'POST', add)
        self.assertEqual(status, 201)
        self.assertTrue(set(existing) <= set(result['topics']))
        self.assertEqual(self.request('/api/topics', 'POST', {'action': 'add', 'name': 'custom TOPIC'})[0], 400)
        self.assertEqual(self.request('/api/topics', 'POST', {'action': 'add', 'name': ''})[0], 400)
        source = next(item for item in self.store.sources() if item['enabled'])
        self.store.insert_story(source['id'], {'headline': 'A useful protocol update', 'url': self.root + '/article',
                                             'excerpt': 'Source details.', 'published_at': None, 'category': 'Standards'}, '2026-10-25')
        story = self.store.stories()['items'][0]
        self.store.review_result(story['id'], review(tags=['Custom topic'], status='reviewed'),
                                self.model_config(), self.store.settings()['interests'], 'Article text.', 'article')
        self.store.bookmark(story['id'], True)
        rename = {'action': 'rename', 'name': 'Custom topic', 'new_name': "SDR / scanner's projects"}
        status, result, _ = self.request('/api/topics', 'POST', rename)
        self.assertEqual((status, result['updated_stories']), (200, 1))
        self.assertEqual(self.store.stories(bookmarks=True)['items'][0]['tags'], [rename['new_name']])
        delete = {'action': 'delete', 'name': rename['new_name']}
        self.assertEqual(self.request('/api/topics', 'POST', delete)[0], 400)
        delete['confirmation'] = 'DELETE TOPIC'
        self.assertEqual(self.request('/api/topics', 'POST', delete)[0], 200)
        self.assertEqual(self.store.stories(bookmarks=True)['items'][0]['tags'], [])
        self.assertEqual(self.store.stories()['total'], 1)
        self.assertTrue(set(existing) <= set(self.store.topics()))

    def test_empty_and_custom_owned_topic_lists_work_with_model_review(self):
        for name in self.store.topics():
            self.store.change_topic('delete', name)
        for provider in ('ollama', 'compatible'):
            with self.subTest(provider=provider):
                result = self.reviewer.test(self.model_config(provider), topics=[])
                self.assertEqual(result['tags'], [])
        self.store.change_topic('add', 'Only my custom label')
        self.store.save_settings({'ai': self.model_config()})
        self.collect()
        self.assertEqual(self.store.topics(), ['Only my custom label'])
        self.assertTrue(all(story['tags'] == ['Only my custom label'] for story in self.store.stories()['items']))
        requests = [payload for path, payload in self.fixture.requests if path == '/api/chat' and payload.get('model') == 'fixture']
        self.assertEqual(requests[-1]['format']['properties']['tags']['items']['enum'], ['Only my custom label'])

    def test_model_tags_are_constrained_in_both_transports(self):
        for provider in ('ollama', 'compatible'):
            with self.subTest(provider=provider):
                result = self.reviewer.test(self.model_config(provider))
                self.assertEqual(result['tags'], ['Web Platform'])
                path, payload = self.fixture.requests[-1]
                schema = payload['format'] if provider == 'ollama' else json.loads(payload['messages'][1]['content'])['schema']
                self.assertEqual(schema['properties']['tags']['items']['enum'], ['Web Platform'])
                with self.assertRaisesRegex(ValueError, 'unconfigured topic'):
                    self.reviewer.test(self.model_config(provider, model='invented-topic'))

    def test_relevance_filter_api_validation(self):
        self.start_api()
        for query in ('exclude_relevance=unknown', 'exclude_relevance=high&' * 5):
            self.assertEqual(self.request('/api/stories?' + query)[0], 400)
        self.assertEqual(self.request('/api/stories?exclude_relevance=high&exclude_relevance=pending')[0], 200)

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

    def test_unchecked_collection_skips_checked_disabled_and_unrelated_review_queue(self):
        checked = next(source for source in self.store.sources() if source['name'] == 'Fixture')
        self.store.source_checked(checked['id'], 'Previously failed')
        checked_before = next(source for source in self.store.sources() if source['id'] == checked['id'])
        self.store.insert_story(checked['id'], {'headline': 'Old queued story', 'url': self.root + '/old-queued',
            'excerpt': 'Old text', 'published_at': None, 'category': 'Standards'}, '2026-10-15')
        fresh_id = self.store.save_source({'name': 'New listing', 'url': self.root + '/listing', 'feed_url': '',
            'category': 'Radio', 'mode': 'page', 'enabled': True})
        disabled_id = self.store.save_source({'name': 'Disabled new', 'url': self.root + '/disabled', 'feed_url': '',
            'category': 'Radio', 'enabled': False})
        self.store.save_settings({'ai': self.model_config()})
        self.start_api()
        self.assertEqual(self.request('/api/jobs/start', 'POST', {'kind': 'review', 'unchecked_only': True})[0], 400)
        self.assertEqual(self.request('/api/jobs/start', 'POST', {'unchecked_only': 'yes'})[0], 400)
        self.assertEqual(self.request('/api/jobs/start', 'POST', {'unchecked_only': True})[0], 202)
        self.collector.worker.join(10)
        self.assertFalse(self.collector.status()['running'])
        sources = {source['id']: source for source in self.store.sources()}
        self.assertEqual(sources[checked['id']]['last_checked'], checked_before['last_checked'])
        self.assertEqual(sources[checked['id']]['last_error'], 'Previously failed')
        self.assertIsNotNone(sources[fresh_id]['last_checked'])
        self.assertIsNone(sources[disabled_id]['last_checked'])
        old_story = next(item for item in self.store.stories()['items'] if item['headline'] == 'Old queued story')
        self.assertEqual(old_story['status'], 'pending')
        self.assertEqual(old_story['attempts'], 0)
        run = self.store.overview()['runs'][0]
        self.assertEqual(run['details']['scope'], 'unchecked')
        self.assertEqual(run['details']['sources_checked'], 1)
        self.assertEqual(run['details']['added'], 2)
        self.assertEqual(run['details']['reviewed'], 1)
        self.assertTrue(run['details']['errors'], 'The second listing fixture has no readable article/excerpt')
        self.assertTrue(self.collector.schedule_due(datetime.now().astimezone().replace(hour=23, minute=59)))
        self.assertTrue(self.collector.start(unchecked_only=True))
        self.collector.worker.join(10)
        self.assertEqual(self.store.overview()['runs'][0]['details']['sources_checked'], 0)
        self.assertEqual(next(item for item in self.store.stories()['items'] if item['headline'] == 'Old queued story')['attempts'], 0)

    def test_one_source_failure_does_not_abort_others(self):
        self.store.save_source({'name': 'Broken', 'url': self.root + '/missing', 'feed_url': self.root + '/missing', 'category': 'Test', 'enabled': True})
        self.collect()
        self.assertEqual(self.store.stories()['total'], 2)
        self.assertEqual(self.store.overview()['runs'][0]['status'], 'partial')

    def test_ollama_diagnostics_and_fenced_json_validation(self):
        for model, message in [('truncated', 'cut off'), ('empty-thinking', 'thinking output'), ('broken', 'invalid JSON')]:
            with self.subTest(model=model):
                with self.assertRaisesRegex(ValueError, message):
                    self.reviewer.test(self.model_config(model=model))
                self.assertEqual(self.fixture.requests[-1][1]['keep_alive'], 0)
        self.assertEqual(self.reviewer.test(self.model_config(model='fenced'))['status'], 'reviewed')
        config = {**self.model_config(), 'unload_after_use': False, 'max_output_tokens': 3000}
        self.reviewer.test(config)
        payload = self.fixture.requests[-1][1]
        self.assertNotIn('keep_alive', payload)
        self.assertEqual(payload['options']['num_predict'], 3000)

    def test_batch_keeps_ollama_warm_and_unloads_after_success_or_failure(self):
        for model in ('fixture', 'broken'):
            config = self.model_config(model=model)
            self.store.save_settings({'ai': config})
            source = next(item['id'] for item in self.store.sources() if item['enabled'])
            self.store.insert_story(source, {'headline': 'A useful protocol update', 'url': self.root + '/article?case=' + model,
                                            'excerpt': 'A useful protocol update supports experimental deployment.', 'published_at': None, 'category': 'Protocols'}, '2026-10-24')
            start = len(self.fixture.requests)
            self.collect('review')
            requests = self.fixture.requests[start:]
            chats = [payload for path, payload in requests if path == '/api/chat']
            self.assertTrue(chats)
            self.assertTrue(all(payload['keep_alive'] == '5m' for payload in chats))
            self.assertEqual(requests[-1][0], '/api/generate')
            self.assertEqual(requests[-1][1]['keep_alive'], 0)
            self.assertEqual(requests[-1][1]['model'], model)

    def test_model_http_errors_preserve_bounded_json_details(self):
        for provider in ('ollama', 'compatible'):
            with self.subTest(provider=provider), self.assertLogs('newsreview.network', level='WARNING') as logs:
                with self.assertRaisesRegex(ValueError, 'HTTP 400 from 127.0.0.1: Input exceeds the model context window'):
                    self.reviewer.test(self.model_config(provider, model='http-error'))
                self.assertIn('Reduce input length.', logs.output[0])
        with self.assertRaisesRegex(ValueError, 'Unsupported parameter: response_format'):
            self.reviewer.test(self.model_config(model='http-error-message'))
        for model in ('http-error-html', 'http-error-huge', 'http-error-shape'):
            with self.subTest(model=model), self.assertRaises(ValueError) as error:
                self.reviewer.test(self.model_config(model=model))
            self.assertEqual(str(error.exception), 'HTTP 400 from 127.0.0.1')
        with self.assertRaises(ValueError) as error:
            self.reviewer.test(self.model_config(model='http-error-long'))
        self.assertEqual(str(error.exception), 'HTTP 400 from 127.0.0.1: ' + 'x' * 240)

    def test_rejected_model_requests_keep_queue_and_store_explanation(self):
        self.store.save_settings({'ai': self.model_config(model='http-error')})
        source = next(item['id'] for item in self.store.sources() if item['enabled'])
        for number in range(4):
            self.store.insert_story(source, {'headline': f'Protocol update {number}', 'url': self.root + f'/excerpt?error={number}',
                                            'excerpt': 'An experimental protocol feature is available.', 'published_at': None,
                                            'category': 'Protocols'}, '2026-10-25')
        self.collect('review')
        run = self.store.overview()['runs'][0]
        self.assertEqual(run['status'], 'partial')
        self.assertEqual(len(run['details']['errors']), 3)
        self.assertTrue(all('Input exceeds the model context window' in error for error in run['details']['errors']))
        self.assertIn('Check the run errors and Local AI settings', run['details']['note'])
        stories = self.store.stories()['items']
        self.assertEqual(len(stories), 4)
        self.assertTrue(all(story['status'] == 'pending' for story in stories))
        self.assertEqual(sorted(story['attempts'] for story in stories), [0, 1, 1, 1])
        self.assertEqual(self.fixture.requests[-1][0], '/api/generate')

    def test_unload_failure_does_not_discard_successful_reviews(self):
        self.store.save_settings({'ai': self.model_config(model='unload-error')})
        source = next(item['id'] for item in self.store.sources() if item['enabled'])
        self.store.insert_story(source, {'headline': 'Protocol testing update', 'url': self.root + '/excerpt',
                                        'excerpt': 'This protocol has added support for experimental features.', 'published_at': None, 'category': 'Protocols'}, '2026-10-24')
        self.collect('review')
        self.assertEqual(self.store.stories()['items'][0]['status'], 'reviewed')
        self.assertIn('Model unload failed', self.collector.status()['message'])
        self.assertFalse(self.collector.lock.locked())

    def test_model_test_cannot_unload_an_active_review_batch(self):
        self.start_api()
        self.collector.lock.acquire()
        try:
            self.assertEqual(self.request('/api/test-model', 'POST', self.model_config())[0], 409)
        finally:
            self.collector.lock.release()

    def test_stop_review_finishes_current_article_preserves_queue_and_can_resume(self):
        self.fixture.review_entered = threading.Event()
        self.fixture.review_release = threading.Event()
        config = self.model_config(model='blocking-review')
        self.store.save_settings({'ai': config})
        source = next(item['id'] for item in self.store.sources() if item['enabled'])
        for number in range(3):
            self.store.insert_story(source, {'headline': f'Protocol progress {number}', 'url': self.root + f'/article?stop={number}',
                                            'excerpt': 'This is a substantive technical development with experimental support.', 'published_at': None, 'category': 'Protocols'}, '2026-10-25')
        settings = self.store.settings()
        self.start_api()
        self.assertEqual(self.request('/api/jobs/stop-review', 'POST', {})[0], 409)
        self.assertTrue(self.collector.start('review'))
        try:
            self.assertTrue(self.fixture.review_entered.wait(5))
            self.assertTrue(self.collector.status()['reviewing'])
            self.assertEqual(self.request('/api/jobs/stop-review', 'POST', {}, {'Cookie': ''})[0], 401)
            self.assertEqual(self.request('/api/jobs/stop-review', 'POST', {}, {'Origin': 'https://evil.example'})[0], 403)
            self.assertEqual(self.request('/api/jobs/stop-review', 'POST', {})[0], 202)
            self.assertTrue(self.collector.status()['review_stop_requested'])
            self.assertFalse(self.collector.stop_event.is_set())
        finally:
            self.fixture.review_release.set()
            self.collector.worker.join(10)
        self.assertFalse(self.collector.status()['running'])
        self.assertEqual(len(self.store.pending(10)), 2)
        self.assertEqual(self.store.overview()['runs'][0]['details']['reviewed'], 1)
        self.assertTrue(self.store.overview()['runs'][0]['details']['review_stopped'])
        self.assertEqual(self.fixture.requests[-1][0], '/api/generate')
        self.assertEqual(self.store.settings(), settings)
        self.collect('review')
        self.assertEqual(len(self.store.pending(10)), 0)
        self.assertFalse(self.collector.review_stop_event.is_set())

    def test_stop_during_collection_review_keeps_source_results_and_schedule_anchor(self):
        self.fixture.review_entered = threading.Event()
        self.fixture.review_release = threading.Event()
        self.store.save_settings({'ai': self.model_config(model='blocking-review')})
        self.start_api()
        self.assertTrue(self.collector.start('collect'))
        try:
            self.assertTrue(self.fixture.review_entered.wait(5))
            self.assertEqual(self.request('/api/jobs/stop-review', 'POST', {})[0], 202)
        finally:
            self.fixture.review_release.set()
            self.collector.worker.join(10)
        run = self.store.overview()['runs'][0]
        self.assertEqual(run['kind'], 'collect')
        self.assertEqual(run['details']['added'], 2)
        self.assertEqual(run['details']['reviewed'], 1)
        self.assertEqual(run['status'], 'partial')
        self.assertEqual(len(self.store.pending(10)), 1)
        self.assertFalse(self.collector.schedule_due(datetime.now().astimezone()))

    def test_article_feedback_api_and_cached_profile_in_model_requests(self):
        self.start_api()
        self.store.save_settings({'ai': self.model_config()})
        source = next(item['id'] for item in self.store.sources() if item['enabled'])
        self.store.insert_story(source, {'headline': 'An SDR hardware project', 'url': self.root + '/rated',
                                        'excerpt': 'Practical radio hardware details.', 'published_at': None, 'category': 'Radio'}, '2026-10-25')
        with self.store.connect() as db:
            identifier = db.execute("SELECT id FROM stories WHERE url=?", (self.root + '/rated',)).fetchone()[0]
            db.execute("UPDATE stories SET status='reviewed' WHERE id=?", (identifier,))
        path = '/api/feedback/story/' + str(identifier)
        self.assertEqual(self.request(path, 'PUT', {'vote': True})[0], 400)
        self.assertEqual(self.request(path, 'PUT', {'vote': 1, 'reason': 'x' * 1001})[0], 400)
        self.assertEqual(self.request(path, 'PUT', {'vote': 1}, {'Cookie': ''})[0], 401)
        self.assertEqual(self.request(path, 'PUT', {'vote': 1}, {'Origin': 'https://other.example'})[0], 403)
        self.assertEqual(self.request(path, 'PUT', {'vote': 1, 'reason': 'I like cheap practical hardware projects'})[0], 200)
        self.assertEqual(self.request('/api/feedback')[1]['count'], 1)
        for number in range(2):
            self.store.insert_story(source, {'headline': f'Experimental protocol {number}', 'url': self.root + f'/review-feedback-{number}',
                                            'excerpt': 'A substantive experimental technical feature is available for testing.', 'published_at': None, 'category': 'Protocols'}, '2026-10-25')
        start = len(self.fixture.requests)
        self.collect('review')
        chats = [payload for url, payload in self.fixture.requests[start:] if url == '/api/chat']
        self.assertEqual(len(chats), 3)
        profile_request = json.loads(chats[0]['messages'][1]['content'])
        self.assertEqual(len(profile_request['feedback']), 1)
        self.assertEqual(profile_request['feedback'][0]['vote'], 1)
        for payload in chats[1:]:
            interests = json.loads(payload['messages'][1]['content'])['reader_interests']
            self.assertIn('learned_profile', interests)
            self.assertNotIn('feedback', interests)
            self.assertNotIn('I like cheap practical hardware projects', json.dumps(interests))
        value = self.store.feedback_state()
        self.assertEqual(value['profile_revision'], value['revision'])
        self.store.requeue(False)
        start = len(self.fixture.requests)
        self.collect('review')
        chats = [payload for url, payload in self.fixture.requests[start:] if url == '/api/chat']
        self.assertTrue(chats)
        self.assertTrue(all(not payload['messages'][0]['content'].startswith('Summarize article ratings') for payload in chats))
        self.assertEqual(self.request('/api/feedback/profile', 'PUT', {'profile': 'Manual profile', 'enabled': True, 'auto_update': False})[0], 200)
        self.assertEqual(self.request('/api/feedback')[1]['profile'], 'Manual profile')

    def test_failed_feedback_profile_preserves_existing_preferences_and_reviews(self):
        self.store.save_settings({'ai': self.model_config(model='bad-profile')})
        source = next(item['id'] for item in self.store.sources() if item['enabled'])
        self.store.insert_story(source, {'headline': 'A technical update', 'url': self.root + '/profile-failure',
                                        'excerpt': 'A technical improvement is available for experimental use.', 'published_at': None, 'category': 'Protocols'}, '2026-10-25')
        identifier = self.store.stories()['items'][0]['id']
        self.store.save_feedback_profile('Keep an existing preference profile')
        self.store.rate_story(identifier, 1, 'More useful protocols')
        self.collect('review')
        self.assertEqual(self.store.feedback_state()['profile'], 'Keep an existing preference profile')
        self.assertEqual(self.store.stories()['items'][0]['status'], 'reviewed')
        self.assertIn('Feedback profile update failed', self.store.overview()['runs'][0]['details']['errors'][0])

    def test_read_markers_are_persistent_idempotent_and_guarded(self):
        self.start_api()
        source = next(item['id'] for item in self.store.sources() if item['enabled'])
        self.store.insert_story(source, {'headline': 'Read tracking fixture', 'url': 'https://example.com/read',
                                        'excerpt': 'Useful details.', 'published_at': None, 'category': 'Protocols'}, '2026-10-25')
        story = self.store.stories()['items'][0]
        self.assertIsNone(story['read_at'])
        path = '/api/read/' + str(story['id'])
        self.assertEqual(self.request(path, 'POST', {}, {'Cookie': ''})[0], 401)
        self.assertEqual(self.request(path, 'POST', {}, {'Origin': 'https://other.example'})[0], 403)
        status, result, _ = self.request(path, 'POST', {})
        self.assertEqual(status, 200)
        self.assertTrue(result['read_at'])
        self.assertEqual(self.request(path, 'POST', {})[1], result)
        self.store.bookmark(story['id'], True)
        self.store.requeue(False)
        self.assertEqual(Store(self.store.path).stories()['items'][0]['read_at'], result['read_at'])
        self.assertEqual(self.request('/api/read/99999999', 'POST', {})[0], 400)

    def test_model_test_uses_real_adapter_contract(self):
        for provider in ('ollama', 'compatible'):
            with self.subTest(provider=provider):
                result = self.reviewer.test(self.model_config(provider))
                self.assertEqual(result['status'], 'reviewed')
                path, payload = self.fixture.requests[-1]
                self.assertFalse(payload['stream'])
                self.assertNotIn('tools', payload)
                if provider == 'ollama':
                    self.assertEqual(path, '/api/chat')
                    self.assertIs(payload['think'], False)
                    self.assertEqual(payload['keep_alive'], 0)
                    self.assertEqual(payload['options']['num_predict'], 2400)
                    self.assertEqual(payload['format']['type'], 'object')
                    self.assertNotIn('why_it_matters', payload['format']['properties'])
                else:
                    self.assertEqual(path, '/v1/chat/completions')
                    self.assertNotIn('think', payload)
                    self.assertEqual(payload['response_format'], {'type': 'json_object'})

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
        self.assertEqual(self.request('/api/jobs/start', 'POST', {}, {'Cookie': cookie, 'Sec-Fetch-Site': 'cross-site'})[0], 403)
        for _ in range(3):
            self.request('/api/login', 'POST', {'password': 'wrong'})
        self.assertEqual(self.request('/api/login', 'POST', {'password': 'wrong'})[0], 429)

    def test_loopback_guard_and_static_security_headers(self):
        self.start_api()
        self.assertEqual(self.request('/api/overview', headers={'Host': 'attacker.example', 'Cookie': ''})[0], 401)
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

    def test_zip_api_confirmation_lock_revision_and_atomic_restore(self):
        self.collect()
        self.start_api()
        with urlopen(Request(self.api_root + '/api/export', headers={'Cookie': self.api_cookie}), timeout=5) as response:
            self.assertEqual(response.headers.get_content_type(), 'application/zip')
            archive = response.read()
        revision = self.store.overview()['revision']

        def upload(headers=None):
            request = Request(self.api_root + '/api/import/backup', data=archive, method='POST',
                              headers={'Content-Type': 'application/zip', 'Cookie': self.api_cookie, **(headers or {})})
            try:
                response = urlopen(request, timeout=5)
            except HTTPError as error:
                response = error
            with response:
                return response.status, json.loads(response.read())

        self.assertEqual(upload()[0], 400)
        self.assertEqual(upload({'X-NewsReview-Confirm': 'RESTORE', 'Origin': 'https://other.example'})[0], 403)
        self.collector.lock.acquire()
        try:
            self.assertEqual(upload({'X-NewsReview-Confirm': 'RESTORE'})[0], 409)
        finally:
            self.collector.lock.release()
        self.assertEqual(self.request('/api/reset', 'POST', {'confirmation': 'wrong', 'selections': {'history': True}})[0], 400)
        self.assertEqual(self.request('/api/reset', 'POST', {'confirmation': 'RESET SELECTED', 'selections': {'history': True}})[0], 200)
        self.assertEqual(self.store.stories()['total'], 0)
        self.assertEqual(upload({'X-NewsReview-Confirm': 'RESTORE', 'X-NewsReview-Revision': revision})[0], 409)
        status, result = upload({'X-NewsReview-Confirm': 'RESTORE'})
        self.assertEqual(status, 200)
        self.assertTrue(result['restored'])
        self.assertEqual(self.store.stories()['total'], 2)
        self.assertEqual(self.request('/api/stories?period=1%2F1%2F2000+-+1%2F2%2F2000%2C+9am+-+3pm')[1]['total'], 0)

    def test_stale_automatic_feed_checks_website_and_retains_usable_entries(self):
        from newsreview.sources import read_source
        config = {'name': 'Stale feed', 'url': self.root + '/listing', 'feed_url': self.root + '/stale-feed',
                  'mode': 'auto', 'stale_days': 14, 'link_pattern': ''}
        result = read_source(self.fetcher, config)
        self.assertEqual(result['method'], 'feed+page')
        self.assertEqual(len(result['entries']), 3, 'Overlapping article URLs must appear once')
        self.assertIn(self.root + '/old', {entry['url'] for entry in result['entries']})
        self.assertIn(self.root + '/excerpt', {entry['url'] for entry in result['entries']})
        self.assertEqual(result['feed_url'], config['feed_url'])
        article = next(item for item in result['entries'] if item['url'] == self.root + '/article')
        self.assertTrue(article['published_at'].startswith(str(datetime.now(timezone.utc).year)))
        self.assertEqual(article['excerpt'], 'A useful source excerpt from the article card.')
        self.assertIn('Website listing also checked', result['note'])
        strict = read_source(self.fetcher, {**config, 'mode': 'feed', 'url': self.root + '/missing'})
        self.assertEqual(strict['method'], 'feed')
        self.assertEqual(strict['note'], '')
        fresh = read_source(self.fetcher, {**config, 'feed_url': self.root + '/feed', 'url': self.root + '/missing'})
        self.assertEqual(fresh['note'], '')
        boundary = datetime(2020, 1, 15, tzinfo=timezone.utc)
        at_limit = read_source(self.fetcher, config, now=boundary)
        self.assertEqual(at_limit['method'], 'feed+page')
        below_limit = read_source(self.fetcher, {**config, 'stale_days': 15, 'url': self.root + '/missing'}, now=boundary)
        self.assertEqual(below_limit['note'], '')
        failed_page = read_source(self.fetcher, {**config, 'url': self.root + '/missing'})
        self.assertEqual(len(failed_page['entries']), 2)
        self.assertEqual(failed_page['method'], 'feed')
        self.assertIn('Retaining available feed entries', failed_page['note'])
        undated = read_source(self.fetcher, {**config, 'feed_url': self.root + '/undated-feed', 'first_checked': '2020-01-01T00:00:00+00:00'})
        self.assertEqual(undated['method'], 'feed+page')
        empty = read_source(self.fetcher, {**config, 'feed_url': self.root + '/empty-feed'})
        self.assertEqual(empty['method'], 'feed+page')
        self.start_api()
        status, preview, _ = self.request('/api/preview-source', 'POST', {**config, 'category': 'Radio', 'enabled': True})
        self.assertEqual(status, 200)
        self.assertEqual(preview['method'], 'feed+page')
        self.assertEqual(self.store.stories()['total'], 0)
        for source in self.store.sources():
            self.store.save_source({**source, 'enabled': False}, source['id'])
        source_id = self.store.save_source({**config, 'category': 'Radio', 'enabled': True})
        self.collect()
        self.assertEqual(self.store.stories()['total'], 2, 'Normal lookback still excludes stale feed-only stories')
        saved = next(item for item in self.store.sources() if item['id'] == source_id)
        self.assertEqual(saved['last_method'], 'feed+page')
        self.assertTrue(saved['newest_published'].startswith(str(datetime.now(timezone.utc).year)))

    def test_no_feed_site_preview_collection_and_fallback(self):
        for existing in self.store.sources():
            self.store.save_source({**existing, 'enabled': False}, existing['id'])
        source = {'name': 'Website source', 'url': self.root + '/listing', 'feed_url': '', 'category': 'Radio', 'enabled': True,
                  'mode': 'auto', 'link_pattern': '', 'stale_days': 30}
        source_id = self.store.save_source(source)
        self.start_api()
        status, preview, _ = self.request('/api/preview-source', 'POST', source)
        self.assertEqual(status, 200)
        self.assertEqual(preview['method'], 'page')
        self.assertEqual(len(preview['entries']), 2)
        self.assertEqual(self.store.stories()['total'], 0, 'Preview must not add stories')
        self.collect()
        self.assertEqual(self.store.stories()['total'], 2)
        saved = next(s for s in self.store.sources() if s['id'] == source_id)
        self.assertEqual(saved['last_method'], 'page')
        self.assertEqual(saved['health']['status'], 'healthy')
        self.store.save_source({**source, 'feed_url': self.root + '/missing'}, source_id)
        self.collect()
        saved = next(s for s in self.store.sources() if s['id'] == source_id)
        self.assertEqual(saved['health']['status'], 'warning')
        self.assertIn('Feed unavailable', saved['collection_note'])
        self.store.save_source({**source, 'mode': 'feed', 'feed_url': self.root + '/missing'}, source_id)
        self.collect()
        self.assertEqual(next(s for s in self.store.sources() if s['id'] == source_id)['health']['status'], 'error')

    def test_deleted_data_folder_returns_actionable_error_and_restart_recovers(self):
        self.start_api()
        shutil.rmtree(self.directory.name)
        for path, method, body in [('/api/sources', 'GET', None), ('/api/overview', 'GET', None), ('/api/collect', 'POST', {})]:
            status, result, _ = self.request(path, method, body)
            self.assertEqual(status, 503)
            self.assertEqual(result['code'], 'storage_unavailable')
            self.assertIn('restart', result['error'])
        self.assertFalse(self.store.path.exists(), 'Requests must not create an empty SQLite database behind the app')
        fresh = Store(self.store.path)
        self.assertEqual(len(fresh.sources()), 39)


if __name__ == '__main__':
    unittest.main()
