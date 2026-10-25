"""A real model HTTP fixture verifies stopping and resuming from the reader."""
from pathlib import Path
from http.server import ThreadingHTTPServer
import tempfile
import threading

from playwright.sync_api import sync_playwright, expect

from newsreview.collector import Collector
from newsreview.network import Fetcher
from newsreview.reviewer import Reviewer
from newsreview.server import AppServer
from newsreview.store import Store
from tests.test_integration import FixtureHandler


def run():
    with tempfile.TemporaryDirectory() as directory:
        fixture = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        fixture.requests = []
        fixture.review_entered = threading.Event()
        fixture.review_release = threading.Event()
        threading.Thread(target=fixture.serve_forever, daemon=True).start()
        root = f'http://127.0.0.1:{fixture.server_port}'
        store = Store(Path(directory) / 'news.db')
        source = store.sources()[0]['id']
        ai = {**store.settings()['ai'], 'base_url': root, 'model': 'blocking-review'}
        store.save_settings({'ai': ai})
        for number in range(3):
            store.insert_story(source, {'headline': f'Experimental protocol {number}', 'url': root + f'/unreadable?{number}',
                                       'excerpt': 'Experimental support enables testing of a new protocol feature.', 'published_at': None, 'category': 'Protocols'}, '2026-10-25')
        fetcher = Fetcher(public=False)
        collector = Collector(store, fetcher, Reviewer(fetcher))
        server = AppServer(('127.0.0.1', 0), store, collector, 'fixture-password')
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page()
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                address = f'http://127.0.0.1:{server.server_port}'
                page.context.add_cookies([{'name': 'newsreview_session', 'value': server.session(), 'url': address}])
                page.goto(address)
                page.get_by_role('button', name='Review pending', exact=True).click()
                expect(page.get_by_role('button', name='Stop review', exact=True)).to_be_enabled(timeout=10000)
                page.get_by_role('button', name='Stop review', exact=True).click()
                expect(page.get_by_role('button', name='Stopping review...', exact=True)).to_be_disabled()
                fixture.review_release.set()
                expect(page.get_by_role('button', name='Review pending', exact=True)).to_be_enabled(timeout=10000)
                assert len(store.pending(10)) == 2
                expect(page.locator('#status-message')).to_contain_text('AI review stopped', timeout=12000)
                page.get_by_role('button', name='Review pending', exact=True).click()
                expect(page.get_by_role('button', name='Review pending', exact=True)).to_be_disabled(timeout=10000)
                page.wait_for_function("async () => { const value = await (await fetch('/api/overview')).json(); return !value.collector.running && !value.counts.pending; }")
                assert len(store.pending(10)) == 0
                assert not errors, errors
                browser.close()
                print('Browser Stop review checks passed: active button, stopping state, queued stories, and resume.')
        finally:
            fixture.review_release.set()
            if collector.worker:
                collector.worker.join(10)
            collector.stop()
            server.shutdown()
            server.server_close()
            fixture.shutdown()
            fixture.server_close()


if __name__ == '__main__':
    run()
