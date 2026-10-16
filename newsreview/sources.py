"""Feed-first automatic collection plus explicit feed/page collection modes."""

from urllib.parse import urljoin

from .feeds import PageParser, parse_feed
from .listings import parse_listing
from .network import canonical_url


def read_source(fetcher, source):
    mode = source.get('mode', 'auto')
    feed_url = source.get('feed_url', '')
    page = None
    feed_error = None
    if mode != 'page':
        try:
            if not feed_url:
                text, url, _ = fetcher.request(source['url'])
                page = (text, url)
                try:
                    entries = parse_feed(text, url)
                    return {'entries': entries, 'method': 'feed', 'feed_url': url, 'note': ''}
                except ValueError:
                    parser = PageParser()
                    parser.feed(text)
                    if parser.feeds:
                        feed_url = canonical_url(urljoin(url, parser.feeds[0]))
                    else:
                        raise ValueError('No RSS/Atom link advertised by the website')
            text, final, _ = fetcher.request(feed_url)
            return {'entries': parse_feed(text, final), 'method': 'feed', 'feed_url': feed_url, 'note': ''}
        except ValueError as exc:
            if mode == 'feed':
                raise
            feed_error = str(exc)
    if page is None:
        text, final, _ = fetcher.request(source['url'])
    else:
        text, final = page
    try:
        entries = parse_listing(text, final, source.get('link_pattern', ''))
    except ValueError as exc:
        prefix = f'Feed: {feed_error}. ' if feed_error else ''
        raise ValueError(prefix + str(exc)) from exc
    note = f'Using website-page collection. Feed unavailable: {feed_error}' if feed_error and source.get('feed_url') else ''
    return {'entries': entries, 'method': 'page', 'feed_url': None, 'note': note}
