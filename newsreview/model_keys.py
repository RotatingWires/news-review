"""Write-only UI credentials, stored separately from reading data and backups."""

import json
import os
from pathlib import Path
import tempfile
import threading
from urllib.parse import urlsplit, urlunsplit

from .network import valid_url

MAX_FILE_BYTES = 1_000_000


def validate_key(value):
    if not isinstance(value, str):
        raise ValueError('Enter an API key')
    value = value.strip()
    if not 1 <= len(value) <= 4096 or any(not 33 <= ord(char) <= 126 for char in value):
        raise ValueError('API keys must contain 1–4096 visible ASCII characters without spaces')
    return value


def credential_scope(config):
    provider = config.get('provider')
    if provider not in ('compatible', 'openrouter'):
        raise ValueError('Choose a compatible server or OpenRouter to configure an API key')
    parts = urlsplit(valid_url(config.get('base_url')))
    if parts.query or parts.fragment:
        raise ValueError('Use a model server base URL without a query or fragment')
    host = parts.hostname.lower()
    if ':' in host:
        host = '[' + host + ']'
    port = parts.port
    if port is not None and port != (443 if parts.scheme == 'https' else 80):
        host += ':' + str(port)
    return provider, urlunsplit((parts.scheme.lower(), host, parts.path.rstrip('/'), '', ''))


class ModelKeys:
    def __init__(self, path, *, environment=None):
        self.path = Path(path)
        self.environment = os.environ if environment is None else environment
        self.lock = threading.RLock()
        self.records = self._load()

    def _load(self):
        if not self.path.exists():
            return {}
        try:
            with self.path.open('rb') as stream:
                content = stream.read(MAX_FILE_BYTES + 1)
            if len(content) > MAX_FILE_BYTES:
                raise ValueError('Oversized credentials file')
            value = json.loads(content)
            if not isinstance(value, dict) or set(value) != {'version', 'keys'} or type(value['version']) is not int or value['version'] != 1 or not isinstance(value['keys'], list):
                raise ValueError('Invalid credentials format')
            records = {}
            for item in value['keys']:
                if not isinstance(item, dict) or set(item) != {'provider', 'base_url', 'key'}:
                    raise ValueError('Invalid credentials entry')
                scope = credential_scope(item)
                if scope in records:
                    raise ValueError('Duplicate credentials entry')
                records[scope] = validate_key(item['key'])
            return records
        except (OSError, ValueError, TypeError, KeyError):
            raise ValueError('Cannot read model-credentials.json. Restore a valid credentials file or move it aside before restarting.') from None

    def _write(self, records):
        content = json.dumps({'version': 1, 'keys': [
            {'provider': provider, 'base_url': base, 'key': key}
            for (provider, base), key in sorted(records.items())
        ]}, indent=2).encode('utf-8')
        if len(content) > MAX_FILE_BYTES:
            raise ValueError('The saved model credentials exceed the file size limit')
        temporary = None
        try:
            fd, temporary = tempfile.mkstemp(prefix='.model-credentials-', dir=self.path.parent)
            with os.fdopen(fd, 'wb') as stream:
                stream.write(content)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.path)
        except OSError:
            raise ValueError('Cannot save the API key. Check the data folder and its write permissions.') from None
        finally:
            if temporary is not None:
                Path(temporary).unlink(missing_ok=True)
        self.records = records

    def get(self, config):
        if config.get('provider') == 'ollama':
            return ''
        scope = credential_scope(config)
        with self.lock:
            value = self.records.get(scope) or self.environment.get('NEWSREVIEW_MODEL_API_KEY', '')
            return validate_key(value) if value else ''

    def status(self, config):
        scope = credential_scope(config)
        with self.lock:
            saved = bool(self.records.get(scope))
            environment = bool(self.environment.get('NEWSREVIEW_MODEL_API_KEY'))
            return {'configured': saved or environment, 'saved': saved, 'environment': environment,
                    'source': 'saved' if saved else 'environment' if environment else 'none'}

    def save(self, config, key):
        scope = credential_scope(config)
        key = validate_key(key)
        with self.lock:
            self._write({**self.records, scope: key})
            return self.status(config)

    def remove(self, config):
        scope = credential_scope(config)
        with self.lock:
            if scope in self.records:
                self._write({item: key for item, key in self.records.items() if item != scope})
            return self.status(config)
