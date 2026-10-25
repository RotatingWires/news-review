"""Check generated-topic cleanup in the real UI with a temporary archive."""
from pathlib import Path
import tempfile
import threading

from playwright.sync_api import sync_playwright, expect

from newsreview.collector import Collector
from newsreview.server import AppServer
from newsreview.store import Store
from tests.test_core import review


def run():
    with tempfile.TemporaryDirectory() as directory:
        store = Store(Path(directory) / 'news.db')
        source = next(item for item in store.sources() if item['category'] == 'Security')
        for number in range(2):
            store.insert_story(source['id'], {
                'headline': f'Security development {number}', 'url': f'https://example.com/topics-{number}',
                'excerpt': 'Source details.', 'published_at': None, 'category': 'Security',
            }, '2026-10-25')
        for story in store.stories()['items']:
            store.review_result(story['id'], review(tags=[f'One-off tag {story["id"]}'], status='reviewed'),
                                store.settings()['ai'], store.settings()['interests'], 'Article text.', 'article')
        identifier = store.stories()['items'][0]['id']
        store.bookmark(identifier, True)
        store.mark_read(identifier)
        collector = Collector(store)
        server = AppServer(('127.0.0.1', 0), store, collector, 'fixture-password')
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page(viewport={'width': 1440, 'height': 950})
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                root = f'http://127.0.0.1:{server.server_port}'
                page.context.add_cookies([{'name': 'newsreview_session', 'value': server.session(), 'url': root}])
                page.goto(root)
                page.get_by_role('button', name='Settings', exact=True).click()
                page.get_by_role('button', name='Collection & history', exact=True).click()
                page.locator('#clear-topics').click()
                dialog = page.locator('#clear-topics-dialog')
                expect(dialog).to_be_visible()
                dialog.get_by_role('button', name='Cancel', exact=True).click()
                assert len(store.overview()['tags']) == 2
                page.locator('#clear-topics').click()
                page.set_viewport_size({'width': 390, 'height': 844})
                expect(page.locator('#confirm-clear-topics')).to_be_in_viewport()
                assert page.locator('body').evaluate('el => el.scrollWidth <= innerWidth')
                page.locator('#confirm-clear-topics').click()
                expect(dialog).not_to_be_visible()
                expect(page.locator('#status-message')).to_contain_text('Generated topics cleared on 2 stories.')
                page.get_by_role('button', name='Daily Feed', exact=True).click()
                expect(page.locator('.story')).to_have_count(2)
                expect(page.locator('.read-label')).to_have_count(1)
                expect(page.locator('.story .tag')).to_have_text(['Security', 'Security'])
                page.locator('#topic-button').click()
                expect(page.locator('#topic-options input')).to_have_count(1)
                expect(page.locator('#topic-options')).to_have_text('Security')
                page.locator('#topic-button').click()
                page.get_by_role('button', name='Bookmarks', exact=True).click()
                expect(page.locator('.story')).to_have_count(1)
                assert not errors, errors
                browser.close()
                print('Topic cleanup browser checks passed: cancel, confirm, compact filter list, retained stories/bookmarks/read markers, and mobile dialog.')
        finally:
            collector.stop()
            server.shutdown()
            server.server_close()
            worker.join()


if __name__ == '__main__':
    run()
