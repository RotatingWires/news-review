"""Credential persistence and first-run HTTP setup, without real news sources."""
import json
import os
from pathlib import Path
import tempfile
import socket
import subprocess
import sys
import re
import time
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from newsreview.auth import Auth
from newsreview.collector import Collector
from newsreview.server import AppServer
from newsreview.store import Store


class AuthTests(unittest.TestCase):
    def test_password_persistence_token_consumption_and_bootstrap_precedence(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'auth.json'
            auth = Auth(path)
            token = auth.setup_token
            self.assertNotEqual(token, Auth(path).setup_token)
            with self.assertRaises(PermissionError):
                auth.setup('wrong', 'password123', 'password123')
            with self.assertRaises(ValueError):
                auth.setup(token, 'short', 'short')
            with self.assertRaises(ValueError):
                auth.setup(token, 'password123', 'different')
            auth.setup(token.lower().replace('-', ' '), 'password123', 'password123')
            self.assertIsNone(auth.setup_token)
            self.assertNotIn('password123', path.read_text())
            self.assertNotIn(token, path.read_text())
            if os.name == 'posix':
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            loaded = Auth(path, 'different-bootstrap')
            self.assertTrue(loaded.verify('password123'))
            self.assertFalse(loaded.verify('different-bootstrap'))
            self.assertFalse(loaded.verify(None))
            with self.assertRaises(FileExistsError):
                loaded.setup(token, 'changed-password', 'changed-password')

    def test_setup_does_not_require_hard_links(self):
        # An audit hook rejects hard links without replacing application or OS code.
        with tempfile.TemporaryDirectory() as directory:
            script = """
import sys
from pathlib import Path
from newsreview.auth import Auth
def reject_links(event, args):
    if event == 'os.link':
        raise OSError(50, 'Hard links are unsupported')
sys.addaudithook(reject_links)
path = Path(sys.argv[1]) / 'auth.json'
auth = Auth(path)
auth.setup(auth.setup_token, 'portable-password', 'portable-password')
assert Auth(path).verify('portable-password')
assert not list(path.parent.glob('.auth-*'))
"""
            result = subprocess.run([sys.executable, '-c', script, directory], capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_malformed_credentials_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'auth.json'
            for contents in ('bad json', '{}', 'null', '[]', '{"version": 999}'):
                path.write_text(contents)
                with self.assertRaises(ValueError):
                    Auth(path, 'bootstrap-password')
                self.assertEqual(path.read_text(), contents)

    def test_simultaneous_instances_cannot_overwrite_credentials(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'auth.json'
            first, second = Auth(path), Auth(path)
            first.setup(first.setup_token, 'first-password', 'first-password')
            with self.assertRaises(FileExistsError):
                second.setup(second.setup_token, 'second-password', 'second-password')
            self.assertTrue(Auth(path).verify('first-password'))

    def test_cli_allows_lan_and_prints_token_only_to_console(self):
        with tempfile.TemporaryDirectory() as directory:
            with socket.socket() as reservation:
                reservation.bind(('127.0.0.1', 0))
                port = reservation.getsockname()[1]
            environment = {key: value for key, value in os.environ.items() if key != 'NEWSREVIEW_PASSWORD'}
            process = subprocess.Popen([sys.executable, '-m', 'newsreview', '--host', '0.0.0.0',
                                        '--port', str(port), '--data-dir', directory, '--no-scheduler'],
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment)
            try:
                for _ in range(100):
                    if process.poll() is not None:
                        self.fail(process.stderr.read())
                    try:
                        with urlopen(f'http://127.0.0.1:{port}/api/auth/status', timeout=0.1) as response:
                            self.assertFalse(json.load(response)['configured'])
                        break
                    except OSError:
                        time.sleep(0.05)
                else:
                    self.fail('Server did not start')
            finally:
                process.terminate()
                output, errors = process.communicate(timeout=5)
            token = re.search(r'setup token: ([A-F0-9-]+)', output)
            self.assertIsNotNone(token, output + errors)
            self.assertNotIn(token[1], (Path(directory) / 'server.log').read_text())
            self.assertFalse((Path(directory) / 'auth.json').exists())

    def test_first_run_http_setup_protects_lan_and_persists(self):
        with tempfile.TemporaryDirectory() as directory:
            store = Store(Path(directory) / 'news.db')
            collector = Collector(store)
            server = AppServer(('127.0.0.1', 0), store, collector)
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            root = f'http://127.0.0.1:{server.server_port}'
            def request(path, body=None, cookie='', **headers):
                req = Request(root + path, data=json.dumps(body).encode() if body is not None else None,
                              headers={'Content-Type': 'application/json', 'Host': '192.168.1.10:8714', 'Cookie': cookie, **headers})
                try:
                    response = urlopen(req, timeout=5)
                except HTTPError as error:
                    response = error
                with response:
                    return response.status, json.loads(response.read()), response.headers
            try:
                self.assertFalse(request('/api/auth/status')[1]['configured'])
                for path in ('/api/overview', '/api/stories', '/api/sources', '/api/export'):
                    self.assertEqual(request(path)[0], 401)
                data = {'setupToken': server.auth.setup_token, 'password': 'test-password', 'confirmPassword': 'test-password'}
                self.assertEqual(request('/api/auth/setup', data, Origin='https://evil.example')[0], 403)
                self.assertEqual(request('/api/auth/setup', {**data, 'setupToken': 'wrong'})[0], 401)
                self.assertEqual(request('/api/auth/setup', {**data, 'confirmPassword': 'different'})[0], 400)
                status, _, headers = request('/api/auth/setup', data)
                self.assertEqual(status, 200)
                cookie = headers['Set-Cookie'].split(';')[0]
                self.assertIn('HttpOnly', headers['Set-Cookie'])
                self.assertTrue(request('/api/auth/status', cookie=cookie)[1]['authenticated'])
                self.assertEqual(request('/api/overview', cookie=cookie)[0], 200)
                self.assertEqual(request('/api/auth/setup', data)[0], 409)
                self.assertEqual(request('/api/auth/login', {'password': 'wrong'})[0], 401)
                self.assertEqual(request('/api/auth/login', {'password': 'test-password'})[0], 429)
                self.assertTrue(Auth(Path(directory) / 'auth.json').verify('test-password'))
            finally:
                server.shutdown()
                server.server_close()
                worker.join()
                collector.stop()
