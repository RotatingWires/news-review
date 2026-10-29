"""Server-scoped credentials, atomic persistence, and authenticated HTTP safety."""
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest

from newsreview.model_keys import ModelKeys, credential_scope
from newsreview.network import Fetcher


class ModelKeyTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / 'model-credentials.json'
        self.environment = {}
        self.keys = ModelKeys(self.path, environment=self.environment)
        self.config = {'provider': 'openrouter', 'base_url': 'https://openrouter.ai/api/v1'}

    def tearDown(self):
        self.directory.cleanup()

    def test_save_replace_remove_restart_and_permissions(self):
        self.assertFalse(self.keys.status(self.config)['configured'])
        result = self.keys.save(self.config, 'fake-first-key')
        self.assertEqual(result, {'configured': True, 'saved': True, 'environment': False, 'source': 'saved'})
        self.assertNotIn('fake-first-key', json.dumps(result))
        self.assertEqual(ModelKeys(self.path, environment={}).get(self.config), 'fake-first-key')
        if os.name == 'posix':
            self.assertEqual(self.path.stat().st_mode & 0o777, 0o600)
        self.keys.save(self.config, 'fake-second-key')
        self.assertEqual(self.keys.get(self.config), 'fake-second-key')
        self.assertNotIn('fake-first-key', self.path.read_text())
        self.assertFalse(self.keys.remove(self.config)['configured'])
        self.assertEqual(ModelKeys(self.path, environment={}).get(self.config), '')
        self.assertEqual(list(self.path.parent.glob('.model-credentials-*')), [])

    def test_provider_server_and_path_scopes_and_environment_fallback(self):
        self.environment['NEWSREVIEW_MODEL_API_KEY'] = 'fake-environment-key'
        self.keys.save(self.config, 'fake-saved-key')
        self.assertEqual(self.keys.get(self.config), 'fake-saved-key')
        equivalent = {**self.config, 'base_url': 'https://OPENROUTER.ai:443/api/v1/'}
        self.assertEqual(credential_scope(self.config), credential_scope(equivalent))
        self.assertEqual(self.keys.get(equivalent), 'fake-saved-key')
        for config in ({**self.config, 'provider': 'compatible'},
                       {**self.config, 'base_url': 'https://another.example/api/v1'},
                       {**self.config, 'base_url': 'https://openrouter.ai/other'},
                       {**self.config, 'base_url': 'http://openrouter.ai/api/v1'}):
            self.assertEqual(self.keys.get(config), 'fake-environment-key')
        self.assertEqual(self.keys.get({'provider': 'ollama'}), '')
        result = self.keys.remove(self.config)
        self.assertEqual(result['source'], 'environment')
        self.assertEqual(self.keys.get(self.config), 'fake-environment-key')

    def test_concurrent_updates_keep_other_server_keys(self):
        configs = [{**self.config, 'base_url': f'https://server-{number}.example/v1'} for number in range(12)]
        with ThreadPoolExecutor(max_workers=4) as pool:
            list(pool.map(lambda item: self.keys.save(item[1], f'fake-key-{item[0]}'), enumerate(configs)))
        reloaded = ModelKeys(self.path, environment={})
        self.assertEqual([reloaded.get(config) for config in configs], [f'fake-key-{n}' for n in range(12)])

    def test_invalid_input_and_file_errors_never_echo_keys(self):
        for key in ('', 'fake-secret\r\nInjected:header', 'fake secret', 'é-secret', 'x' * 4097, None):
            with self.subTest(type=type(key).__name__), self.assertRaises(ValueError) as error:
                self.keys.save(self.config, key)
            if isinstance(key, str) and key:
                self.assertNotIn(key, str(error.exception))
        for base in ('https://user:password@example.com/v1', 'https://example.com/v1?token=secret', 'https://example.com/v1#fragment'):
            with self.assertRaises(ValueError):
                self.keys.save({**self.config, 'base_url': base}, 'fake-key')
        self.path.write_text('{"version":1,"keys":[{"key":"fake-private-value"}]}')
        with self.assertRaises(ValueError) as error:
            ModelKeys(self.path, environment={})
        self.assertNotIn('fake-private-value', str(error.exception))
        missing = ModelKeys(self.path.parent / 'missing' / 'model-credentials.json', environment={})
        with self.assertRaises(ValueError) as error:
            missing.save(self.config, 'fake-private-value')
        self.assertNotIn('fake-private-value', str(error.exception))
        self.assertFalse(missing.status(self.config)['saved'])

    def test_failed_atomic_replace_preserves_previous_key_without_hard_links(self):
        script = '''
import sys
from pathlib import Path
from newsreview.model_keys import ModelKeys
path = Path(sys.argv[1]) / 'model-credentials.json'
config = {'provider':'openrouter','base_url':'https://openrouter.ai/api/v1'}
def no_hard_links(event, args):
    if event == 'os.link':
        raise OSError(50, 'Hard links unsupported')
sys.addaudithook(no_hard_links)
keys = ModelKeys(path, environment={})
keys.save(config, 'fake-previous-key')
def fail_replace(event, args):
    if event == 'os.rename':
        raise PermissionError('Fixture denied replacement')
sys.addaudithook(fail_replace)
try:
    keys.save(config, 'fake-new-key')
except ValueError:
    pass
else:
    raise AssertionError('Expected failed replacement')
assert keys.get(config) == 'fake-previous-key'
assert ModelKeys(path, environment={}).get(config) == 'fake-previous-key'
assert not list(path.parent.glob('.model-credentials-*'))
'''
        subprocess.run([sys.executable, '-c', script, self.directory.name], check=True, capture_output=True, text=True)


class CredentialHTTPFixture(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        self.server.seen.append(self.headers.get('Authorization'))
        if self.path == '/redirect':
            self.send_response(302)
            self.send_header('Location', self.server.target)
            self.end_headers()
        else:
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'{"ok":true}')

    def do_POST(self):
        self.rfile.read(int(self.headers['Content-Length']))
        self.server.seen.append(self.headers.get('Authorization'))
        self.send_response(400)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps({'error': 'Rejected ' + self.headers.get('Authorization', '')}).encode())


class CredentialTransportTests(unittest.TestCase):
    def setUp(self):
        self.servers = [ThreadingHTTPServer(('127.0.0.1', 0), CredentialHTTPFixture) for _ in range(2)]
        self.workers = []
        for server in self.servers:
            server.seen = []
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            self.workers.append(worker)
        self.root = f'http://127.0.0.1:{self.servers[0].server_port}'
        self.headers = {'Authorization': 'Bearer fake-private-key'}
        self.fetcher = Fetcher(public=False)

    def tearDown(self):
        for server, worker in zip(self.servers, self.workers):
            server.shutdown()
            server.server_close()
            worker.join()

    def test_error_messages_and_logs_redact_the_authorization_secret(self):
        with self.assertLogs('newsreview.network', level='WARNING') as logs:
            with self.assertRaises(ValueError) as error:
                self.fetcher.json(self.root, {}, headers=self.headers)
        self.assertIn('[redacted]', str(error.exception))
        self.assertNotIn('fake-private-key', str(error.exception) + '\n'.join(logs.output))

    def test_cross_origin_redirect_does_not_forward_key(self):
        self.servers[0].target = f'http://127.0.0.1:{self.servers[1].server_port}/ok'
        with self.assertRaisesRegex(ValueError, 'cannot redirect'):
            self.fetcher.request(self.root + '/redirect', headers=self.headers)
        self.assertEqual(self.servers[1].seen, [])

    def test_same_origin_redirect_can_use_the_key(self):
        self.servers[0].target = self.root + '/ok'
        text, _, _ = self.fetcher.request(self.root + '/redirect', headers=self.headers)
        self.assertEqual(json.loads(text), {'ok': True})
        self.assertEqual(self.servers[0].seen, ['Bearer fake-private-key', 'Bearer fake-private-key'])
