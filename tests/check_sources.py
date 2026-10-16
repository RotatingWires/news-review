"""Optional live feed audit: python -m tests.check_sources. Does not write data."""

from concurrent.futures import ThreadPoolExecutor
from technews.defaults import SOURCES
from technews.feeds import discover, parse_feed
from technews.network import Fetcher


def check(source):
    name, url, feed, _ = source
    fetcher = Fetcher()
    try:
        feed = feed or discover(fetcher, url)
        text, final_url, _ = fetcher.request(feed, timeout=15)
        entries = parse_feed(text, final_url)
        return f'OK | {name} | {len(entries)} entries | {feed}'
    except Exception as error:
        try:
            alternative = discover(fetcher, url)
            text, final_url, _ = fetcher.request(alternative, timeout=15)
            entries = parse_feed(text, final_url)
            return f'ALTERNATIVE | {name} | {len(entries)} entries | {alternative} | original: {error}'
        except Exception as alternative_error:
            return f'CHECK | {name} | {error} | discovery: {alternative_error}'


if __name__ == '__main__':
    with ThreadPoolExecutor(max_workers=6) as executor:
        for result in executor.map(check, SOURCES):
            print(result, flush=True)
