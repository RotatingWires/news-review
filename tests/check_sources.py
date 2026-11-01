"""Optional live website-listing preview; reads public pages but never writes application data."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from newsreview.network import Fetcher
from newsreview.sources import read_source


def check(source, details=False):
    try:
        result = read_source(Fetcher(), source)
        note = ' | ' + result['note'] if result['note'] else ''
        entries = ''.join(f"\n  {item['headline']} | {item['url']}" for item in result['entries']) if details else ''
        return f"OK | {source['name']} | {result['method']} | {len(result['entries'])} entries{note}{entries}"
    except Exception as error:
        return f"CHECK | {source['name']} | {error}"


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='Preview website listings without saving data')
    parser.add_argument('--website', action='append', required=True, help='Listing-page URL to check in website mode; may be repeated')
    parser.add_argument('--contains', default='', help='Optional literal article URL filter for website checks')
    parser.add_argument('--details', action='store_true', help='Print discovered headlines and URLs')
    args = parser.parse_args()
    sources = [{'name': url, 'url': url, 'mode': 'page', 'link_pattern': args.contains} for url in args.website]
    with ThreadPoolExecutor(max_workers=6) as executor:
        for result in executor.map(lambda source: check(source, args.details), sources):
            print(result, flush=True)
