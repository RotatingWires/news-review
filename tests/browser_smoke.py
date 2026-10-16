"""Optional real-browser QA: python -m tests.browser_smoke (requires Playwright).

Uses temporary data and a local fixture model. Never writes to the user's archive.
"""

from datetime import date, timedelta
from http.server import ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading
import re

from playwright.sync_api import sync_playwright, expect

from newsreview.collector import Collector
from newsreview.network import Fetcher
from newsreview.reviewer import Reviewer, validate_review
from newsreview.server import AppServer
from newsreview.store import Store
from tests.test_core import review
from tests.test_integration import FixtureHandler


def run():
    with tempfile.TemporaryDirectory() as directory:
        store = Store(Path(directory) / 'news.db')
        source = store.sources()[0]['id']
        samples = [
            ('Experimental HTTP method reaches a browser development build', 'Protocols', 'A proposed HTTP method is now available for developer testing. Stable browser releases do not yet include it.', 'This moves the proposal into practical interoperability testing.'),
            ('Open-source SDR decoder adds new P25 analysis tools', 'Radio & SDR', 'The project introduces control-channel analysis and improved diagnostic output for compatible receivers.', 'Better diagnostics make it easier to understand reception and decoding issues.'),
            ('FreePBX update tightens module authentication', 'PBX & VoIP', 'A module update changes how administrative requests are authenticated. The release notes explain the affected configurations.', 'Administrators can check their installations and plan an update.'),
            ('Image compression project publishes experimental encoder', 'Formats', 'A research project released an encoder for a proposed image format, along with code and test material.', 'An open implementation lets developers evaluate the format themselves.'),
        ]
        today = date.today().isoformat()
        for index, (headline, category, summary, why) in enumerate(samples):
            store.insert_story(source, {'headline': headline, 'url': f'https://example.com/{index}', 'excerpt': summary,
                                       'published_at': None, 'category': category}, today)
            with store.connect() as db:
                story_id = db.execute('SELECT id FROM stories WHERE url=?', (f'https://example.com/{index}',)).fetchone()[0]
            store.review_result(story_id, validate_review(review(summary=summary, why_it_matters=why, tags=[category], score=90-index), set()),
                                {**store.settings()['ai'], 'model': 'fixture-model'}, store.settings()['interests'], summary, 'article')
        store.insert_story(source, {'headline': 'Yesterday’s protocol update', 'url': 'https://example.com/yesterday',
                                   'excerpt': 'A source excerpt from the previous day.', 'published_at': None, 'category': 'Protocols'}, (date.today() - timedelta(days=1)).isoformat())
        fixture = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        fixture.requests = []
        fixture_thread = threading.Thread(target=fixture.serve_forever, daemon=True)
        fixture_thread.start()
        collector = Collector(store, Fetcher(public=False), Reviewer(Fetcher(public=False)))
        server = AppServer(('127.0.0.1', 0), store, collector)
        server_thread = threading.Thread(target=server.serve_forever, daemon=True)
        server_thread.start()
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch(headless=True)
                page = browser.new_page(viewport={'width': 1440, 'height': 1050})
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.goto(f'http://127.0.0.1:{server.server_port}')
                page.wait_for_selector('.story')
                assert page.locator('.story').count() == 4
                page.screenshot(path='/tmp/newsreview-desktop.png', full_page=True)

                page.get_by_role('button', name='Help', exact=True).click()
                expect(page.locator('#help-dialog')).to_be_visible()
                positions = page.locator('#help-dialog').evaluate('el => [el.querySelector(".dialog-heading").getBoundingClientRect().top, el.querySelector(".dialog-actions").getBoundingClientRect().bottom]')
                page.locator('#help-dialog .dialog-body').evaluate('el => { el.scrollTop = el.scrollHeight; }')
                assert positions == page.locator('#help-dialog').evaluate('el => [el.querySelector(".dialog-heading").getBoundingClientRect().top, el.querySelector(".dialog-actions").getBoundingClientRect().bottom]')
                page.locator('#help-dialog .dialog-actions button').click()
                page.get_by_role('button', name='Import/Export...', exact=True).click()
                with page.expect_download() as download_info:
                    page.locator('#export-backup').click()
                backup_path = Path(directory) / 'browser-export.zip'
                download_info.value.save_as(backup_path)
                page.locator('#backup-file').set_input_files(str(backup_path))
                page.locator('#confirm-restore').check()
                page.locator('#restore-backup').click()
                expect(page.locator('#transfer-status')).to_contain_text('Restored', timeout=15000)
                page.locator('#transfer-dialog .dialog-actions').get_by_role('button', name='Close', exact=True).click()
                page.locator('.bookmark').first.click()
                page.get_by_role('button', name='Bookmarks', exact=True).click()
                expect(page.locator('.story')).to_have_count(1)
                assert 'HTTP' in page.locator('.story h3').inner_text()
                page.get_by_role('button', name='Daily feed', exact=True).click()
                expect(page.locator('.story')).to_have_count(4)
                page.get_by_label('Previous day', exact=True).click()
                expect(page.locator('.story')).to_have_count(1)
                assert 'Yesterday' in page.locator('.story h3').inner_text()
                page.get_by_role('button', name='Today', exact=True).click()
                page.get_by_label('Search history').fill('FreePBX')
                page.get_by_role('button', name='Search', exact=True).click()
                expect(page.locator('.story')).to_have_count(1)
                assert 'FreePBX' in page.locator('.story h3').inner_text()
                page.get_by_role('button', name='Clear search').click()
                expect(page.locator('.story')).to_have_count(4)

                page.locator('#topic-button').click()
                page.get_by_role('checkbox', name='Protocols', exact=True).check()
                expect(page.locator('.story')).to_have_count(1)
                page.get_by_role('checkbox', name='Radio & SDR', exact=True).check()
                expect(page.locator('.story')).to_have_count(2)
                page.locator('#topic-all').click()
                expect(page.locator('.story')).to_have_count(4)
                page.locator('#topic-button').click()
                yesterday = date.today() - timedelta(days=1)
                date_text = f'{yesterday.month}/{yesterday.day}/{yesterday.year} - {date.today().month}/{date.today().day}/{date.today().year}'
                page.get_by_label('Date/time range', exact=True).fill(date_text)
                page.get_by_label('Date/time range', exact=True).press('Enter')
                expect(page.locator('.story')).to_have_count(5)
                page.get_by_label('Date/time range', exact=True).fill(date_text + ', 12am - 11:59pm')
                page.get_by_label('Date/time range', exact=True).press('Enter')
                expect(page.locator('.story')).to_have_count(5)
                page.get_by_role('button', name='Clear search').click()
                expect(page.locator('.story')).to_have_count(4)

                page.get_by_role('button', name='Settings', exact=True).click()
                page.wait_for_selector('#guidance')
                page.locator('#guidance').fill('Favor RF projects and protocol changes.')
                page.get_by_role('button', name='Save interests').click()
                expect(page.locator('#toast')).to_contain_text('Interests saved')
                assert store.settings()['interests']['guidance'] == 'Favor RF projects and protocol changes.', repr(store.settings()['interests']['guidance'])
                page.get_by_role('button', name='Sources', exact=True).click()
                page.get_by_role('button', name='+ Add source', exact=True).click()
                page.get_by_label('Name', exact=True).fill('Fixture source')
                page.get_by_label('Website URL', exact=True).fill(f'http://127.0.0.1:{fixture.server_port}/')
                page.get_by_role('button', name='Find RSS/Atom feed').click()
                expect(page.locator('#source-feed')).to_have_value(re.compile('/feed$'))
                page.get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('.source-row')).to_have_count(26)
                page.get_by_role('button', name='+ Add source', exact=True).click()
                page.get_by_label('Name', exact=True).fill('Website without RSS')
                page.get_by_label('Website URL', exact=True).fill(f'http://127.0.0.1:{fixture.server_port}/listing')
                page.get_by_label('Collection method', exact=True).select_option('page')
                page.get_by_role('button', name='Preview stories', exact=True).click()
                expect(page.locator('#source-preview')).to_contain_text('Website page')
                expect(page.locator('#source-preview a')).to_have_count(2)
                page.get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('.source-row')).to_have_count(27)
                page.get_by_role('button', name='Local AI', exact=True).click()
                page.locator('#ai-url').fill(f'http://127.0.0.1:{fixture.server_port}')
                page.locator('#ai-model').fill('fixture')
                page.get_by_role('button', name='Test with a sample article').click()
                expect(page.locator('#model-test-result')).to_contain_text('validation passed')
                page.get_by_role('button', name='Save Settings').click()
                expect(page.locator('#toast')).to_contain_text('AI settings saved')
                assert store.settings()['ai']['model'] == 'fixture'

                # Keep this test entirely on the fixture server when testing Refresh.
                for configured in store.sources():
                    if not configured['url'].startswith(f'http://127.0.0.1:{fixture.server_port}'):
                        store.save_source({**configured, 'enabled': False}, configured['id'])
                page.get_by_role('button', name='Sources', exact=True).click()
                page.get_by_role('button', name='Refresh sources', exact=True).click()
                expect(page.locator('#source-refresh-status')).to_contain_text('Last collection: complete', timeout=15000)
                expect(page.locator('.source-state.healthy')).to_have_count(2)
                page.context.set_offline(True)
                page.get_by_role('button', name='Refresh sources', exact=True).click()
                expect(page.locator('#connection-status')).to_contain_text('Cannot reach the NewsReview server')
                expect(page.locator('#source-refresh-status')).to_contain_text('not confirmed')
                page.context.set_offline(False)
                expect(page.locator('#connection-status')).to_be_hidden(timeout=15000)
                expect(page.locator('#source-refresh-status')).to_contain_text('No newer collection was recorded')

                page.get_by_role('button', name='Daily feed', exact=True).click()
                page.wait_for_selector('.story')
                page.set_viewport_size({'width': 390, 'height': 844})
                chrome = page.locator('.window').evaluate('el => [el.querySelector("header").getBoundingClientRect().top, el.querySelector("footer").getBoundingClientRect().bottom]')
                page.locator('main').evaluate('el => { el.scrollTop = el.scrollHeight; }')
                assert chrome == page.locator('.window').evaluate('el => [el.querySelector("header").getBoundingClientRect().top, el.querySelector("footer").getBoundingClientRect().bottom]')
                assert page.evaluate('scrollY === 0')
                page.screenshot(path='/tmp/newsreview-mobile.png', full_page=True)
                assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'), 'Mobile page overflows horizontally'
                page.get_by_role('button', name='Switch color theme').click()
                assert page.locator('html').get_attribute('data-theme') == 'dark'
                page.screenshot(path='/tmp/newsreview-dark.png', full_page=True)
                page.get_by_role('button', name='Settings', exact=True).click()
                page.get_by_role('button', name='Collection & history', exact=True).click()
                history_count = store.stories()['total']
                page.get_by_role('button', name='Fresh start...', exact=True).click()
                page.locator('#clear-history-dialog').get_by_role('button', name='Cancel', exact=True).click()
                assert store.stories()['total'] == history_count
                page.get_by_role('button', name='Fresh start...', exact=True).click()
                page.get_by_label('Type RESET SELECTED to confirm', exact=True).fill('wrong')
                page.get_by_role('button', name='Reset selected data', exact=True).click()
                assert store.stories()['total'] == history_count
                preferences = store.settings()
                page.locator('#reset-bookmarks').check()
                page.get_by_label('Type RESET SELECTED to confirm', exact=True).fill('RESET SELECTED')
                page.get_by_role('button', name='Reset selected data', exact=True).click()
                expect(page.locator('#toast')).to_contain_text('Selected data reset')
                assert store.stories()['total'] == 0
                assert store.settings() == preferences
                assert len(store.sources()) == 27
                page.get_by_role('button', name='Daily feed', exact=True).click()
                expect(page.locator('#empty')).to_be_visible()
                assert not errors, errors
                browser.close()
                print('Browser QA passed: feed/search/bookmarks, interests, feed discovery, website preview, source health, refresh completion, offline/reconnect recovery, local AI, confirmed reset, mobile layout and Win95 themes; no browser errors.')
                print('Screenshots: /tmp/newsreview-{desktop,mobile,dark}.png')
        finally:
            collector.stop()
            server.shutdown()
            server.server_close()
            fixture.shutdown()
            fixture.server_close()
            server_thread.join()
            fixture_thread.join()


if __name__ == '__main__':
    run()
