"""Track actual headline activations without adding labels to unopened stories."""
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
        for number in range(4):
            store.insert_story(source, {'headline': f'Read marker example {number}', 'url': f'https://example.com/read/{number}',
                                       'excerpt': 'Fixture excerpt.', 'published_at': None, 'category': 'Protocols'}, '2026-10-25')
        collector = Collector(store)
        server = AppServer(('127.0.0.1', 0), store, collector, 'fixture-password')
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page(viewport={'width': 390, 'height': 844})
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.context.route('https://example.com/**', lambda route: route.fulfill(body='<p>Article fixture</p>', content_type='text/html'))
                address = f'http://127.0.0.1:{server.server_port}'
                page.context.add_cookies([{'name': 'newsreview_session', 'value': server.session(), 'url': address}])
                page.goto(address)
                expect(page.locator('.story')).to_have_count(4)
                expect(page.locator('.read-label')).to_have_count(0)
                for number, action in enumerate(('click', 'middle', 'keyboard')):
                    story = page.locator('.story').filter(has=page.get_by_role('link', name=f'Read marker example {number}', exact=True))
                    link = story.locator('h3 a')
                    with page.context.expect_page() as popup:
                        if action == 'keyboard':
                            link.focus()
                            link.press('Enter')
                        else:
                            link.click(button='middle' if action == 'middle' else 'left')
                    popup.value.close()
                    expect(story.locator('.read-label')).to_have_text('Read')
                    label = story.locator('.read-label').bounding_box()
                    publisher = story.locator('.source-name').bounding_box()
                    assert label['y'] >= publisher['y'] + publisher['height']
                page.reload()
                expect(page.locator('.read-label')).to_have_count(3)
                unopened = page.locator('.story').filter(has=page.get_by_role('link', name='Read marker example 3', exact=True))
                expect(unopened.locator('.read-label')).to_have_count(0)
                marked = page.locator('.story').filter(has=page.get_by_role('link', name='Read marker example 0', exact=True))
                marked.locator('.bookmark').click()
                expect(marked.locator('.bookmark')).to_have_attribute('aria-pressed', 'true')
                page.get_by_role('button', name='Bookmarks', exact=True).click()
                expect(page.locator('.read-label')).to_have_count(1)
                assert not errors, errors
                browser.close()
                print('Read marker browser checks passed: click/middle/keyboard, placement, reload, bookmarks, and unopened stories.')
        finally:
            collector.stop()
            server.shutdown()
            server.server_close()


if __name__ == '__main__':
    run()
