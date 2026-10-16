"""Extract article candidates from downloaded listing HTML without executing scripts."""

from html.parser import HTMLParser
import json
import re
from urllib.parse import urljoin, urlsplit

from .feeds import PageParser, parse_date, plain
from .network import canonical_url

VOID = {'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'param', 'source', 'track', 'wbr'}
SKIP = {'nav', 'footer', 'aside', 'script', 'style', 'form'}


class ListingParser(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.stack = []
        self.cards = []
        self.anchor = None
        self.links = []
        self.paragraph = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'article':
            self.cards.append((len(self.stack), {'date': None, 'paragraphs': [], 'has_headline': False}))
        if tag == 'time' and self.cards and attrs.get('datetime'):
            self.cards[-1][1]['date'] = parse_date(attrs['datetime'])
        if tag == 'a' and attrs.get('href') and not any(t in SKIP for t in self.stack):
            heading = any(t in ('h1', 'h2', 'h3', 'h4') for t in self.stack) or 'bookmark' in attrs.get('rel', '').split() or attrs.get('itemprop') == 'headline'
            if heading and self.cards:
                self.cards[-1][1]['has_headline'] = True
            self.anchor = {'href': attrs['href'], 'text': [], 'title': attrs.get('title', ''),
                           'main': 'main' in self.stack,
                           'heading': heading,
                           'semantic': bool(self.cards or any(t in ('h1', 'h2', 'h3', 'h4') for t in self.stack)),
                           'card': self.cards[-1][1] if self.cards else None}
        if tag in ('h1', 'h2', 'h3', 'h4') and self.anchor:
            self.anchor['semantic'] = True
            self.anchor['heading'] = True
            if self.anchor['card'] is not None:
                self.anchor['card']['has_headline'] = True
        if tag == 'p' and self.cards:
            self.paragraph = []
        if tag not in VOID:
            self.stack.append(tag)

    def handle_endtag(self, tag):
        if tag == 'a' and self.anchor:
            self.links.append(self.anchor)
            self.anchor = None
        if tag == 'p' and self.paragraph is not None:
            if self.cards:
                self.cards[-1][1]['paragraphs'].append(''.join(self.paragraph))
            self.paragraph = None
        if tag in self.stack:
            depth = len(self.stack) - 1 - self.stack[::-1].index(tag)
            self.stack = self.stack[:depth]
            self.cards = [card for card in self.cards if card[0] < depth]

    def handle_data(self, value):
        if not any(t in SKIP for t in self.stack):
            if self.anchor:
                self.anchor['text'].append(value)
            if self.paragraph is not None:
                self.paragraph.append(value)


def _structured(value):
    if isinstance(value, dict):
        kind = value.get('@type', [])
        kinds = [kind] if isinstance(kind, str) else kind
        if any(k in ('NewsArticle', 'Article', 'BlogPosting', 'DiscussionForumPosting') for k in kinds):
            url = value.get('url') or value.get('mainEntityOfPage')
            if isinstance(url, dict):
                url = url.get('@id') or url.get('url')
            yield {'url': url, 'headline': value.get('headline') or value.get('name'),
                   'excerpt': value.get('description', ''), 'published_at': parse_date(value.get('datePublished')) if isinstance(value.get('datePublished'), str) else None}
        if value.get('@type') == 'ListItem':
            item = value.get('item', value)
            if isinstance(item, dict):
                yield {'url': item.get('url'), 'headline': item.get('headline') or item.get('name'),
                       'excerpt': item.get('description', ''), 'published_at': None}
        for child in value.values():
            yield from _structured(child)
    elif isinstance(value, list):
        for child in value:
            yield from _structured(child)


def parse_listing(text, base_url, link_pattern=''):
    parser = ListingParser()
    parser.feed(text)
    metadata = PageParser()
    metadata.feed(text)
    candidates = []
    for block in metadata.json_blocks:
        try:
            candidates.extend(_structured(json.loads(block)))
        except (ValueError, TypeError, RecursionError):
            continue
    links = [link for link in parser.links if link['main']] if any(link['main'] for link in parser.links) else parser.links
    has_cards = any(link['card'] is not None for link in links)
    for link in links:
        if link['card'] and link['card']['has_headline'] and not link['heading']:
            continue
        # When article cards are available, do not turn unrelated sidebar headings
        # into stories. An explicit URL filter can opt into another page layout.
        if has_cards and link['card'] is None and not link_pattern:
            continue
        if not link['semantic'] and not link_pattern:
            continue
        card = link['card'] or {}
        candidates.append({'url': link['href'], 'headline': ''.join(link['text']).strip() or link['title'],
                           'excerpt': ' '.join(card.get('paragraphs', [])), 'published_at': card.get('date')})
    host = urlsplit(base_url).hostname
    results = {}
    for item in candidates[:3000]:
        if not isinstance(item.get('url'), str) or not isinstance(item.get('headline'), str):
            continue
        try:
            url = canonical_url(urljoin(base_url, item['url']))
        except ValueError:
            continue
        parts = urlsplit(url)
        headline = plain(item['headline'])[:500]
        if parts.hostname != host or url == canonical_url(base_url) or len(headline) < 12:
            continue
        if link_pattern and link_pattern not in url:
            continue
        if re.search(r'/(?:tags?|categor(?:y|ies)|author|login|sign-?in|subscribe|privacy|contact|about|search)(?:/|$)', parts.path, re.I):
            continue
        item = {**item, 'url': url, 'headline': headline, 'excerpt': plain(item['excerpt'])[:16000] if isinstance(item['excerpt'], str) else ''}
        if url not in results:
            results[url] = item
        else:
            if not results[url]['published_at'] and item['published_at']:
                results[url]['published_at'] = item['published_at']
            if not results[url]['excerpt'] and item['excerpt']:
                results[url]['excerpt'] = item['excerpt']
    if not results:
        raise ValueError('No article links found in the downloaded page. Try a news/category listing and an Article URL filter. JavaScript-only pages need a custom adapter.')
    return list(results.values())
