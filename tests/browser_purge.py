"""Real-browser confirmation and cancellation checks for duplicate maintenance."""
from pathlib import Path
import tempfile
import threading

from playwright.sync_api import sync_playwright, expect

from newsreview.collector import Collector
from newsreview.server import AppServer
from newsreview.store import Store
from tests.fixtures import add_source


def run():
    with tempfile.TemporaryDirectory() as directory:
        store = Store(Path(directory) / 'news.db')
        source = add_source(store)['id']
        for name in ('Main', 'Duplicate', 'Saved duplicate'):
            store.insert_story(source, {'headline': name, 'url': 'https://example.com/' + name.replace(' ', '-'),
                                       'excerpt': 'Fixture article.', 'published_at': None, 'category': 'Security'}, '2026-10-21')
        with store.connect() as db:
            main = db.execute("SELECT id FROM stories WHERE headline='Main'").fetchone()[0]
            db.execute("UPDATE stories SET status='duplicate',duplicate_of=? WHERE headline!='Main'", (main,))
            db.execute("UPDATE stories SET bookmarked=1 WHERE headline='Saved duplicate'")
        collector = Collector(store)
        server = AppServer(('127.0.0.1', 0), store, collector, 'test-password')
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page(viewport={'width': 390, 'height': 844})
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                root = f'http://127.0.0.1:{server.server_port}'
                page.context.add_cookies([{'name': 'newsreview_session', 'value': server.session(), 'url': root}])
                page.goto(root)
                page.get_by_role('button', name='Settings', exact=True).click()
                page.get_by_role('button', name='Collection & history', exact=True).click()
                page.locator('#purge-duplicates').click()
                expect(page.locator('#purge-duplicates-count')).to_have_text('1 duplicate story can be removed.')
                page.locator('#purge-duplicates-dialog').get_by_role('button', name='Cancel', exact=True).click()
                assert store.stories(show_hidden=True)['total'] == 3
                page.locator('#purge-duplicates').click()
                page.locator('#confirm-purge-duplicates').click()
                expect(page.locator('#purge-duplicates-dialog')).not_to_be_visible()
                expect(page.locator('#status-message')).to_contain_text('1 duplicate story removed.')
                assert store.stories(show_hidden=True)['total'] == 2
                assert store.stories(bookmarks=True)['total'] == 1
                page.locator('#purge-duplicates').click()
                expect(page.locator('#purge-duplicates-count')).to_have_text('0 duplicate stories can be removed.')
                expect(page.locator('#confirm-purge-duplicates')).to_be_disabled()
                assert not errors, errors
                browser.close()
                print('Duplicate purge browser checks passed: cancel, confirm, bookmark retention, empty state, and mobile dialog.')
        finally:
            server.shutdown()
            server.server_close()
            worker.join()
            collector.stop()


if __name__ == '__main__':
    run()
