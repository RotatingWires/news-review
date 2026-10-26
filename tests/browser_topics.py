"""Editable topics, preservation, filter updates, and mobile dialogs in Chromium."""
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
        source = store.sources()[0]
        category = source['category']
        for number in range(2):
            store.insert_story(source['id'], {
                'headline': f'Useful development {number}', 'url': f'https://example.com/topics-{number}',
                'excerpt': 'Source details.', 'published_at': None, 'category': category,
            }, '2026-10-25')
        first = store.stories()['items'][0]['id']
        store.review_result(first, review(tags=[category, 'Existing custom topic'], status='reviewed'),
                            store.settings()['ai'], store.settings()['interests'], 'Article text.', 'article')
        store.bookmark(first, True)
        store.mark_read(first)
        with store.connect() as db:
            db.execute("DELETE FROM settings WHERE key='topics'")
        store = Store(store.path)
        original = store.topics()
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
                page.locator('#topic-button').click()
                page.locator('#topic-options').get_by_role('checkbox', name=category, exact=True).check()
                page.locator('#topic-button').press('Escape')

                def open_topics():
                    page.get_by_role('button', name='Settings', exact=True).click()
                    page.get_by_role('button', name='Topics', exact=True).click()
                    expect(page.locator('#panel-topics')).to_be_visible()

                open_topics()
                expect(page.locator('#topic-rows .topic-row')).to_have_count(len(original))
                expect(page.locator('#topic-rows')).to_contain_text('Existing custom topic')
                page.locator('#add-topic').click()
                editor = page.locator('#topic-edit-dialog')
                editor.get_by_role('button', name='Cancel', exact=True).click()
                assert store.topics() == original
                page.locator('#add-topic').click()
                page.locator('#topic-name-input').fill('SDR Projects')
                editor.get_by_role('button', name='Save', exact=True).click()
                expect(editor).not_to_be_visible()
                expect(page.locator('#topic-rows')).to_contain_text('SDR Projects')
                assert set(original) <= set(store.topics())
                page.locator('#add-topic').click()
                page.locator('#topic-name-input').fill('sdr PROJECTS')
                editor.get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('#status-message')).to_contain_text('already exists')
                expect(editor).to_be_visible()
                editor.get_by_role('button', name='Cancel', exact=True).click()

                page.get_by_role('button', name='Rename topic ' + category, exact=True).click()
                page.locator('#topic-name-input').fill('Device Engineering')
                editor.get_by_role('button', name='Save', exact=True).click()
                expect(editor).not_to_be_visible()
                page.get_by_role('button', name='Daily Feed', exact=True).click()
                expect(page.locator('.story')).to_have_count(2)
                page.locator('#topic-button').click()
                expect(page.locator('#topic-options').get_by_role('checkbox', name='Device Engineering', exact=True)).to_be_checked()
                page.locator('#topic-button').press('Escape')
                assert store.sources()[0]['category'] == category
                assert store.stories(bookmarks=True)['total'] == 1
                expect(page.locator('.read-label')).to_have_count(1)

                open_topics()
                page.get_by_role('button', name='Delete topic Device Engineering', exact=True).click()
                deletion = page.locator('#topic-delete-dialog')
                deletion.get_by_role('button', name='Cancel', exact=True).click()
                assert 'Device Engineering' in store.topics()
                page.get_by_role('button', name='Delete topic Device Engineering', exact=True).click()
                page.set_viewport_size({'width': 390, 'height': 844})
                expect(deletion.get_by_role('button', name='Delete', exact=True)).to_be_in_viewport()
                deletion.get_by_role('button', name='Delete', exact=True).click()
                expect(deletion).not_to_be_visible()
                assert 'Device Engineering' not in store.topics()
                assert 'Existing custom topic' in store.topics()
                assert store.stories()['total'] == 2
                assert store.stories(bookmarks=True)['total'] == 1
                page.reload()
                open_topics()
                expect(page.locator('#topic-rows')).to_contain_text('Existing custom topic')
                expect(page.locator('#topic-rows')).to_contain_text('SDR Projects')
                page.get_by_role('button', name='Rename topic SDR Projects', exact=True).click()
                page.locator('#topic-name-input').fill('Radio, scanners, antennas and homebrew projects')
                editor.get_by_role('button', name='Save', exact=True).click()
                expect(editor).not_to_be_visible()
                expect(page.locator('#topic-rows')).to_contain_text('Radio, scanners, antennas and homebrew projects')
                assert page.locator('body').evaluate('el => el.scrollWidth <= innerWidth')
                assert not errors, errors
                browser.close()
                print('Topics browser checks passed: preserved existing labels, add/duplicate handling, rename/filter selection, delete/cancel, bookmarks/read markers, reload, and mobile dialogs.')
        finally:
            collector.stop()
            server.shutdown()
            server.server_close()
            worker.join()


if __name__ == '__main__':
    run()
