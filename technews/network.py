"""Bounded HTTP transport; source URLs cannot target a private network."""

import ipaddress
import json
import socket
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit, urlunsplit, parse_qsl, urlencode
from urllib.request import Request, build_opener, HTTPRedirectHandler, ProxyHandler

USER_AGENT = "TechNewsFeed/0.1 (+self-hosted personal RSS reader)"


def valid_url(value):
    if not isinstance(value, str) or len(value) > 2048 or any(ord(c) < 33 for c in value):
        raise ValueError("Enter a valid HTTP or HTTPS URL")
    parts = urlsplit(value)
    if parts.scheme not in ("http", "https") or not parts.hostname or parts.username or parts.password:
        raise ValueError("Use an HTTP or HTTPS URL without embedded credentials")
    try:
        parts.port
    except ValueError as exc:
        raise ValueError("Invalid port") from exc
    return value


def canonical_url(value):
    valid_url(value)
    p = urlsplit(value)
    query = [(k, v) for k, v in parse_qsl(p.query, keep_blank_values=True)
             if not k.lower().startswith("utm_") and k.lower() not in ("fbclid", "gclid", "mc_cid", "mc_eid")]
    return urlunsplit((p.scheme.lower(), p.netloc.lower(), p.path or "/", urlencode(query), ""))


def check_public(url):
    valid_url(url)
    parts = urlsplit(url)
    try:
        addresses = socket.getaddrinfo(parts.hostname, parts.port or (443 if parts.scheme == "https" else 80), type=socket.SOCK_STREAM)
    except OSError as exc:
        raise ValueError(f"Cannot resolve news source hostname: {parts.hostname}") from exc
    if not addresses or any(not ipaddress.ip_address(info[4][0]).is_global for info in addresses):
        raise ValueError("News sources must use public internet addresses")


class CheckedRedirect(HTTPRedirectHandler):
    def __init__(self, public):
        self.public = public

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        if self.public:
            check_public(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)


class Fetcher:
    def __init__(self, public=True):
        self.public = public
        self.opener = build_opener(ProxyHandler({}), CheckedRedirect(public))

    def request(self, url, *, payload=None, timeout=25, limit=3_000_000, headers=None):
        valid_url(url)
        if self.public:
            check_public(url)
        request_headers = {"User-Agent": USER_AGENT, "Accept": "application/atom+xml, application/rss+xml, text/html, application/json"}
        request_headers.update(headers or {})
        body = None
        if payload is not None:
            body = json.dumps(payload).encode()
            request_headers["Content-Type"] = "application/json"
        try:
            with self.opener.open(Request(url, data=body, headers=request_headers), timeout=timeout) as response:
                content = response.read(limit + 1)
                if len(content) > limit:
                    raise ValueError("Response exceeds the download size limit")
                charset = response.headers.get_content_charset() or "utf-8"
                return content.decode(charset, errors="replace"), response.geturl(), response.headers.get_content_type()
        except HTTPError as exc:
            exc.close()
            raise ValueError(f"HTTP {exc.code} from {urlsplit(url).hostname}") from exc
        except (URLError, TimeoutError, OSError) as exc:
            raise ValueError(f"Cannot reach {urlsplit(url).hostname}: {exc.reason if isinstance(exc, URLError) else type(exc).__name__}") from exc

    def json(self, url, payload, timeout=180, headers=None):
        text, _, _ = self.request(url, payload=payload, timeout=timeout, limit=1_000_000, headers=headers)
        try:
            return json.loads(text)
        except json.JSONDecodeError as exc:
            raise ValueError("Model server returned invalid JSON") from exc
