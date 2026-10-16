"""Feed-first collection with website checks for failed or stale Automatic feeds."""

from datetime import datetime, timezone
from urllib.parse import urljoin

from .feeds import PageParser, parse_feed
from .listings import parse_listing
from .network import canonical_url


def _stale_reason(entries, source, now):
    days = source.get('stale_days', 14)
    dates = [datetime.fromisoformat(item['published_at']) for item in entries if item['published_at']]
    if not entries:
        return 'Feed returned no story entries'
    if dates and (now - max(dates)).total_seconds() >= days * 86400:
        return f"Feed's newest publication is at least {days} days old"
    if not dates:
        observed = source.get('last_new_story') or source.get('first_checked')
        if observed and (now - datetime.fromisoformat(observed)).total_seconds() >= days * 86400:
            return f'Undated feed has no newly observed stories for at least {days} days'
    return ''


def _feed_result(fetcher, source, entries, feed_url, page, now):
    result = {'entries': entries, 'method': 'feed', 'feed_url': feed_url, 'note': ''}
    if source.get('mode', 'auto') != 'auto':
        return result
    reason = _stale_reason(entries, source, now)
    if not reason:
        return result
    try:
        text, final = page if page is not None else fetcher.request(source['url'])[:2]
        website = parse_listing(text, final, source.get('link_pattern', ''))
    except ValueError as exc:
        error = str(exc)[:500].rstrip('.')
        result['note'] = f'{reason}. Website fallback could not be read: {error}. Retaining available feed entries.'
        return result
    # Website order leads previews; preserve feed dates/excerpts when the page
    # omits them and keep feed-only stories. Existing URL history still deduplicates.
    merged = {item['url']: dict(item) for item in website}
    for item in entries:
        if item['url'] not in merged:
            merged[item['url']] = dict(item)
        else:
            for field in ('published_at', 'excerpt'):
                merged[item['url']][field] = merged[item['url']][field] or item[field]
    result.update(entries=list(merged.values()), method='feed+page',
                  note=f'{reason}. Website listing also checked; combined website and feed entries.')
    return result


def read_source(fetcher, source, *, now=None):
    now = now or datetime.now(timezone.utc)
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
                except ValueError:
                    parser = PageParser()
                    parser.feed(text)
                    if parser.feeds:
                        feed_url = canonical_url(urljoin(url, parser.feeds[0]))
                    else:
                        raise ValueError('No RSS/Atom link advertised by the website')
                else:
                    return _feed_result(fetcher, source, entries, url, None, now)
            text, final, _ = fetcher.request(feed_url)
            return _feed_result(fetcher, source, parse_feed(text, final), feed_url, page, now)
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
