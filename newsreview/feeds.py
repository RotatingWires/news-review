"""RSS/Atom discovery, source excerpts, and conservative article extraction."""

from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from html import unescape
from html.parser import HTMLParser
import json
import re
from urllib.parse import urljoin
import xml.etree.ElementTree as ET

from .network import canonical_url


class PageParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack = []
        self.links = []
        self.paragraphs = []
        self.semantic = []
        self.current = None
        self.feeds = []
        self.description = ""
        self.json_parts = []
        self.json_blocks = []
        self.in_json = False

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "link" and "alternate" in attrs.get("rel", "").split() and any(x in attrs.get("type", "") for x in ("rss", "atom")):
            self.feeds.append(attrs.get("href", ""))
        if tag == "meta" and (attrs.get("name") == "description" or attrs.get("property") == "og:description"):
            self.description = attrs.get("content", "")
        if tag in ("meta", "link", "img", "br", "hr", "input", "source", "wbr", "area", "base", "embed", "param", "track", "col"):
            return
        self.stack.append(tag)
        if tag == "script" and attrs.get("type") == "application/ld+json":
            self.in_json, self.json_parts = True, []
        if tag == "p":
            self.current = []

    def handle_endtag(self, tag):
        if tag == "script" and self.in_json:
            self.json_blocks.append("".join(self.json_parts))
            self.in_json = False
        if tag == "p" and self.current is not None:
            text = " ".join("".join(self.current).split())
            if len(text) >= 60:
                self.paragraphs.append(text)
                if "article" in self.stack or "main" in self.stack:
                    self.semantic.append(text)
            self.current = None
        if tag in self.stack:
            self.stack = self.stack[:len(self.stack) - 1 - self.stack[::-1].index(tag)]

    def handle_data(self, text):
        if self.in_json:
            self.json_parts.append(text)
        if self.current is not None and not any(tag in self.stack for tag in ("script", "style", "nav", "footer", "header", "aside")):
            self.current.append(text)


class PlainText(HTMLParser):
    def __init__(self):
        super().__init__()
        self.parts = []
        self.skip = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style"):
            self.skip += 1
        if tag in ("p", "br", "div", "li"):
            self.parts.append(" ")

    def handle_endtag(self, tag):
        if tag in ("script", "style"):
            self.skip = max(0, self.skip - 1)
        if tag in ("p", "div", "li"):
            self.parts.append(" ")

    def handle_data(self, text):
        if not self.skip:
            self.parts.append(text)


def plain(value):
    parser = PlainText()
    parser.feed(value or "")
    return " ".join(unescape("".join(parser.parts)).split())


def parse_date(value):
    if not value:
        return None
    try:
        dt = datetime.fromisoformat(value.strip().replace("Z", "+00:00"))
    except ValueError:
        try:
            dt = parsedate_to_datetime(value)
        except (ValueError, TypeError, OverflowError):
            return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc).isoformat(timespec="seconds")


def local_name(tag):
    return tag.rsplit("}", 1)[-1]


def parse_feed(text, base_url):
    if re.search(r"<!\s*(DOCTYPE|ENTITY)", text, re.I):
        raise ValueError("DTD/entity declarations are not accepted in feeds")
    try:
        root = ET.fromstring(text)
    except ET.ParseError as exc:
        raise ValueError("Not a valid RSS/Atom feed; check the feed URL") from exc
    if local_name(root.tag) not in ("rss", "feed", "RDF"):
        raise ValueError("Source returned a page instead of RSS/Atom")
    result = []
    for entry in root.iter():
        if local_name(entry.tag) not in ("item", "entry"):
            continue
        fields = {}
        link = ""
        for child in entry:
            key = local_name(child.tag)
            value = "".join(child.itertext())
            fields[key] = value
            if key == "link" and child.get("rel", "alternate") == "alternate":
                link = child.get("href") or value
        link = link or fields.get("guid", "")
        title = plain(fields.get("title", ""))[:500]
        try:
            url = canonical_url(urljoin(base_url, link.strip()))
        except ValueError:
            continue
        if not title or not link:
            continue
        excerpt = plain(fields.get("encoded") or fields.get("content") or fields.get("description") or fields.get("summary", ""))[:16000]
        result.append({"headline": title, "url": url, "excerpt": excerpt,
                       "published_at": parse_date(fields.get("pubDate") or fields.get("published") or fields.get("date") or fields.get("updated"))})
    return result


def discover(fetcher, url):
    text, final_url, _ = fetcher.request(url)
    try:
        parse_feed(text, final_url)
        return final_url
    except ValueError:
        parser = PageParser()
        parser.feed(text)
        if not parser.feeds:
            raise ValueError("No RSS/Atom link found. Enter a feed URL manually; this site may need a custom adapter.")
        return canonical_url(urljoin(final_url, parser.feeds[0]))


def _json_body(value):
    if isinstance(value, dict):
        body = value.get("articleBody")
        if isinstance(body, str) and len(body) >= 300:
            return plain(body)
        for child in value.values():
            body = _json_body(child)
            if body:
                return body
    elif isinstance(value, list):
        for child in value:
            body = _json_body(child)
            if body:
                return body
    return ""


def extract_article(text):
    parser = PageParser()
    parser.feed(text)
    for block in parser.json_blocks:
        try:
            body = _json_body(json.loads(block))
            if body:
                return body
        except (ValueError, RecursionError):
            continue
    # Avoid treating an entire navigation/cookie/paywall page as an article.
    body = "\n\n".join(dict.fromkeys(parser.semantic))
    if len(body) < 300 or any(phrase in body.lower()[:800] for phrase in ("verify you are human", "enable javascript and cookies")):
        return ""
    return body
