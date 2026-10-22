"""HTTP API and static file server. Intended for a single trusted household."""

from datetime import date, datetime
import hashlib
import hmac
from http.cookies import SimpleCookie, CookieError
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import logging
import mimetypes
import os
from pathlib import Path
import secrets
import sqlite3
import threading
import time
import tempfile
from urllib.parse import parse_qs, urlsplit

from . import __version__
from . import validation
from .auth import Auth
from .feeds import discover
from .search import date_range, time_range, date_time_range
from .sources import read_source
from .store import StorageUnavailable
from .transfer import create_archive, restore_archive, MAX_UPLOAD

logger = logging.getLogger(__name__)
STATIC = Path(__file__).resolve().parent.parent / "static"


class AppServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, store, collector, password=""):
        self.store, self.collector = store, collector
        self.auth = Auth(store.path.parent / "auth.json", password)
        self.session_key = secrets.token_bytes(32)
        self.login_attempts = {}
        self.login_lock = threading.Lock()
        super().__init__(address, Handler)

    def session(self):
        expires = str(int(time.time()) + 7 * 86400)
        return expires + "." + hmac.new(self.session_key, expires.encode(), hashlib.sha256).hexdigest()

    def valid_session(self, value):
        try:
            expires, signature = value.split(".", 1)
            return int(expires) > time.time() and hmac.compare_digest(signature, hmac.new(self.session_key, expires.encode(), hashlib.sha256).hexdigest())
        except (ValueError, TypeError):
            return False


class Handler(BaseHTTPRequestHandler):
    server_version = "NewsReview"

    def log_message(self, fmt, *args):
        logger.info("%s %s", self.client_address[0], fmt % args)

    def send(self, status, data, content_type="application/json; charset=utf-8", headers=None):
        body = json.dumps(data, ensure_ascii=False).encode() if content_type.startswith("application/json") else data
        self.send_response(status)
        for key, value in {
            "Content-Type": content_type, "Content-Length": str(len(body)), "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer",
            "Content-Security-Policy": "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
            **(headers or {}),
        }.items():
            self.send_header(key, value)
        self.end_headers()
        self.wfile.write(body)

    def body(self):
        if self.headers.get("Transfer-Encoding"):
            raise ValueError("Chunked request bodies are not supported")
        if self.headers.get_content_type() != "application/json":
            raise ValueError("Use application/json")
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError as exc:
            raise ValueError("Invalid content length") from exc
        if not 0 < length <= 64000:
            raise ValueError("Request body must be 1–64000 bytes")
        try:
            result = json.loads(self.rfile.read(length))
        except (ValueError, UnicodeError) as exc:
            raise ValueError("Invalid JSON") from exc
        if not isinstance(result, dict):
            raise ValueError("Expected a JSON object")
        return result

    def send_file(self, path, filename):
        self.send_response(200)
        self.send_header('Content-Type', 'application/zip')
        self.send_header('Content-Length', str(path.stat().st_size))
        self.send_header('Content-Disposition', f'attachment; filename="{filename}"')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.end_headers()
        with path.open('rb') as source:
            while chunk := source.read(65536):
                self.wfile.write(chunk)

    def import_backup(self):
        if self.headers.get('X-NewsReview-Confirm') != 'RESTORE':
            raise ValueError('Confirm that this restore replaces the current dataset')
        if self.headers.get_content_type() != 'application/zip' or self.headers.get('Transfer-Encoding'):
            raise ValueError('Upload a ZIP with a Content-Length header')
        length = int(self.headers.get('Content-Length', '0'))
        if not 0 < length <= MAX_UPLOAD:
            self.send(413, {'error': 'Backup ZIP must be between 1 byte and 256 MiB'})
            return
        if not self.server.collector.lock.acquire(blocking=False):
            self.send(409, {'error': 'Wait for collection/review to finish before restoring a backup'})
            return
        try:
            with tempfile.TemporaryDirectory(prefix='newsreview-upload-') as directory:
                path = Path(directory) / 'upload.zip'
                with path.open('wb') as destination:
                    remaining = length
                    while remaining:
                        chunk = self.rfile.read(min(65536, remaining))
                        if not chunk:
                            raise ValueError('Upload ended before the complete ZIP was received')
                        destination.write(chunk)
                        remaining -= len(chunk)
                result = restore_archive(self.server.store, path)
                self.server.collector.update(message='Backup restored. Reading history and preferences were reloaded.')
                self.send(200, result)
        finally:
            self.server.collector.lock.release()

    def authorized(self):
        if not self.server.auth.configured:
            return False
        try:
            cookie = SimpleCookie(self.headers.get("Cookie", ""))
            return "newsreview_session" in cookie and self.server.valid_session(cookie["newsreview_session"].value)
        except CookieError:
            return False

    def guard(self, write=False):
        host = self.headers.get("Host", "")
        try:
            hostname = urlsplit("http://" + host).hostname
        except ValueError:
            hostname = None
        if not hostname:
            self.send(403, {"error": "Invalid Host header"})
            return False
        if write:
            origin = self.headers.get("Origin")
            if self.headers.get("Sec-Fetch-Site") == "cross-site" or (origin and urlsplit(origin).netloc != host):
                self.send(403, {"error": "Cross-origin writes are not allowed"})
                return False
        return True

    def do_GET(self):
        self.dispatch(False)

    def do_POST(self):
        self.dispatch(True)

    def do_PUT(self):
        self.dispatch(True)

    def dispatch(self, write):
        self.connection.settimeout(20)
        try:
            if not self.guard(write):
                return
            path = urlsplit(self.path).path
            if not write and path in ("/api/session", "/api/auth/status"):
                self.send(200, {"authenticated": self.authorized(), "configured": self.server.auth.configured, "password_required": True, "version": __version__})
                return
            if self.command == "POST" and path in ("/api/login", "/api/auth/login", "/api/auth/setup"):
                self.login(setup=path == "/api/auth/setup")
                return
            if not path.startswith("/api/"):
                if write:
                    self.send(405, {"error": "Method not allowed"})
                else:
                    self.static(path)
                return
            if not self.authorized():
                self.send(401, {"error": "Sign in to continue"})
                return
            if write and self.headers.get('X-NewsReview-Revision'):
                with self.server.store.connect() as db:
                    revision = json.loads(db.execute("SELECT value FROM settings WHERE key='data_revision'").fetchone()[0])
                if self.headers['X-NewsReview-Revision'] != revision:
                    self.send(409, {'error': 'Data changed after a restore or reset. Reload before making changes.', 'code': 'revision_changed'})
                    return
            if self.command == 'POST' and path == '/api/import/backup':
                self.import_backup()
                return
            if write:
                self.mutate(path, self.body())
            else:
                self.read(path, parse_qs(urlsplit(self.path).query))
        except StorageUnavailable as exc:
            self.send(503, {'error': str(exc), 'code': 'storage_unavailable'})
        except (ValueError, KeyError, TypeError) as exc:
            self.send(400, {"error": str(exc)[:500]})
        except sqlite3.IntegrityError:
            self.send(409, {"error": "That source URL is already in your list"})
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            return
        except Exception:
            logger.exception("Request failed")
            self.send(500, {"error": "An internal error occurred. See the server log."})

    def login(self, setup=False):
        data = self.body()
        ip = self.client_address[0]
        now = time.time()
        with self.server.login_lock:
            self.server.login_attempts = {key: values for key, values in self.server.login_attempts.items() if values and values[-1] > now - 60}
            attempts = [t for t in self.server.login_attempts.get(ip, []) if now - t < 60]
            if len(attempts) >= 5:
                self.send(429, {"error": "Too many attempts. Try again in one minute."})
                return
            attempts.append(now)
            self.server.login_attempts[ip] = attempts
        password = data.get("password", "")
        if setup:
            try:
                self.server.auth.setup(data.get("setupToken"), password, data.get("confirmPassword"))
            except FileExistsError:
                self.send(409, {"error": "A password is already configured."})
                return
            except PermissionError:
                self.send(401, {"error": "Incorrect setup token."})
                return
        elif not self.server.auth.verify(password):
            self.send(401, {"error": "Incorrect password"})
            return
        secure = "; Secure" if os.environ.get("NEWSREVIEW_SECURE_COOKIE") == "1" else ""
        self.send(200, {"ok": True}, headers={"Set-Cookie": f"newsreview_session={self.server.session()}; HttpOnly; SameSite=Strict; Path=/; Max-Age=604800{secure}"})

    def static(self, path):
        name = "index.html" if path == "/" else path.lstrip("/")
        if (name == "index.html" and not self.authorized()) or (name == "login.html" and self.authorized()):
            target = "/login.html" if name == "index.html" else "/"
            self.send(302, b"", "text/plain", {"Location": target})
            return
        # Explicit flat asset allowlist: never serve runtime data, source or dotfiles.
        if name not in {"index.html", "style.css", "app.js", "shared.js", "feed.js", "settings.js", "transfer.js", "dropdowns.js", "icon.svg", "login.html", "login.js", "auth.css", "manifest.webmanifest", "apple-touch-icon.png", "icon-192.png", "icon-512.png"}:
            self.send(404, {"error": "Not found"})
            return
        content_type = "application/manifest+json" if name == "manifest.webmanifest" else mimetypes.guess_type(name)[0] or "application/octet-stream"
        self.send(200, (STATIC / name).read_bytes(), content_type + "; charset=utf-8")

    def read(self, path, params):
        store = self.server.store
        if path == "/api/stories":
            values = {key: params.get(key, [""])[0] for key in ("day", "query", "start", "end", "tag")}
            if any(len(v) > 500 for v in values.values()):
                raise ValueError("Search is too long")
            for key in ("day", "start", "end"):
                if values[key]:
                    date.fromisoformat(values[key])
            if values["start"] and values["end"] and values["start"] > values["end"]:
                raise ValueError("Start date must be before end date")
            date_text = params.get("date_range", [""])[0]
            time_text = params.get("time_range", [""])[0]
            if len(date_text) > 100 or len(time_text) > 100:
                raise ValueError("Date/time range is too long")
            if date_text.strip():
                values["start"], values["end"] = date_range(date_text)
            time_start, time_end = time_range(time_text)
            combined = params.get('period', [''])[0]
            if len(combined) > 200:
                raise ValueError('Date/time range is too long')
            if combined.strip():
                period = date_time_range(combined)
                values['start'], values['end'] = period['start'], period['end']
                time_start, time_end = period['time_start'], period['time_end']
            # An explicit date range widens browsing; keywords and time-only
            # filters retain the selected day supplied by the reader.
            if values['start'] or values['end']:
                values['day'] = ''
            tags = params.get("tag", [])
            if len(tags) > 100 or any(len(t) > 60 for t in tags):
                raise ValueError("Choose at most 100 valid topics")
            values.pop("tag")
            page = validation.integer(int(params.get("page", ["1"])[0]), "Page", 1, 100000)
            result = store.stories(**values, tags=tags, time_start=time_start, time_end=time_end, time_zone=params.get("time_zone", [""])[0],
                                   bookmarks=params.get("bookmarks") == ["1"], show_hidden=params.get("hidden") == ["1"], page=page)
            for item in result["items"]:
                for field in ("article_text", "interests_snapshot"):
                    item.pop(field, None)
            self.send(200, result)
        elif path == '/api/export':
            with tempfile.TemporaryDirectory(prefix='newsreview-download-') as directory:
                path = Path(directory) / 'export.zip'
                create_archive(store, path)
                self.send_file(path, 'newsreview-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '.zip')
        elif path == "/api/overview":
            self.send(200, {**store.overview(), "collector": self.server.collector.status(), "today": date.today().isoformat(),
                            "timezone": str(datetime.now().astimezone().tzinfo), "version": __version__})
        elif path == '/api/maintenance/duplicates':
            self.send(200, {'count': store.duplicate_purge_count()})
        elif path == "/api/settings":
            self.send(200, store.settings())
        elif path == "/api/sources":
            self.send(200, store.sources())
        elif path.startswith("/api/related/"):
            self.send(200, store.related(int(path.rsplit("/", 1)[1])))
        else:
            self.send(404, {"error": "Not found"})

    def mutate(self, path, data):
        store = self.server.store
        if path == '/api/maintenance/duplicates/purge' and self.command == 'POST':
            if data.get('confirmation') != 'PURGE DUPLICATES':
                raise ValueError('Confirm duplicate purging before continuing')
            if not self.server.collector.lock.acquire(blocking=False):
                self.send(409, {'error': 'Wait for collection/review or restore to finish before purging duplicates'})
                return
            try:
                result = store.purge_duplicates()
                self.send(200, result)
            finally:
                self.server.collector.lock.release()
        elif path == '/api/reset' and self.command == 'POST':
            if data.get('confirmation') != 'RESET SELECTED':
                raise ValueError('Type RESET SELECTED to confirm')
            if not self.server.collector.lock.acquire(blocking=False):
                self.send(409, {'error': 'Wait for collection/review or restore to finish before resetting data'})
                return
            try:
                result = store.reset(data.get('selections'))
                self.server.collector.update(message='Selected data was reset. Other data and preferences were kept.')
                self.send(200, result)
            finally:
                self.server.collector.lock.release()
        elif path == "/api/history/clear" and self.command == "POST":
            if data.get("confirmation") != "DELETE HISTORY":
                raise ValueError("Type DELETE HISTORY to confirm")
            if not self.server.collector.lock.acquire(blocking=False):
                self.send(409, {"error": "Wait for the current collection to finish before clearing history"})
                return
            try:
                count = store.clear_history()
                self.server.collector.update(message="History cleared. Check sources when you want a new edition.")
                self.send(200, {"deleted": count})
            finally:
                self.server.collector.lock.release()
        elif path in ("/api/logout", "/api/auth/logout") and self.command == "POST":
            self.send(200, {"ok": True}, headers={"Set-Cookie": "newsreview_session=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0"})
        elif path.startswith("/api/bookmarks/") and self.command == "PUT":
            store.bookmark(int(path.rsplit("/", 1)[1]), validation.boolean(data.get("bookmarked"), "Bookmarked"))
            self.send(200, {"ok": True})
        elif path == "/api/settings" and self.command == "PUT":
            values = validation.settings(data, store.settings())
            store.save_settings(values)
            self.send(200, {"ok": True})
        elif path == "/api/sources" and self.command == "POST":
            self.send(201, {"id": store.save_source(validation.source(data))})
        elif path.startswith("/api/sources/") and self.command == "PUT":
            self.send(200, {"id": store.save_source(validation.source(data), int(path.rsplit("/", 1)[1]))})
        elif path == "/api/discover" and self.command == "POST":
            url = validation.valid_url(data.get("url"))
            self.send(200, {"feed_url": discover(self.server.collector.fetcher, url)})
        elif path == '/api/preview-source' and self.command == 'POST':
            source = validation.source(data)
            result = read_source(self.server.collector.fetcher, source)
            self.send(200, {**result, 'entries': result['entries'][:8]})
        elif path == "/api/test-model" and self.command == "POST":
            config = validation.settings({"ai": data}, store.settings())["ai"]
            self.send(200, self.server.collector.reviewer.test(config))
        elif path in ("/api/jobs/start", "/api/collect") and self.command == "POST":
            kind = data.get("kind", "collect")
            if kind not in ("collect", "review"):
                raise ValueError("Unknown job type")
            unchecked_only = validation.boolean(data.get("unchecked_only", False), "Unchecked only")
            if unchecked_only and kind != "collect":
                raise ValueError("New-source checks require a collection job")
            started = self.server.collector.start(kind, unchecked_only=unchecked_only)
            self.send(202 if started else 409, {"ok": started, "error": "A job is already running" if not started else None})
        elif path == "/api/requeue" and self.command == "POST":
            # The queue lock keeps requeue and review decisions from racing.
            if not self.server.collector.lock.acquire(blocking=False):
                self.send(409, {"error": "Wait for the current collection to finish"})
                return
            try:
                failed_only = validation.boolean(data.get("failed_only", True), "Failed only")
                self.send(200, {"count": store.requeue(failed_only)})
            finally:
                self.server.collector.lock.release()
        else:
            self.send(404, {"error": "Not found"})
