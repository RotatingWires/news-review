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
            store.review_result(story_id, validate_review(review(summary=summary, why_it_matters=why, tags=[category] + [f'Topic {index}-{n}' for n in range(5)], score=[90, 60, 25, 75][index]), set()),
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
                page = browser.new_page(viewport={'width': 1440, 'height': 1050}, timezone_id='America/New_York')
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.goto(f'http://127.0.0.1:{server.server_port}')
                page.wait_for_selector('.story')
                assert page.locator('.story').count() == 4
                expect(page.locator('.relevance-indicator.relevance-high')).to_have_count(2)
                expect(page.locator('.relevance-indicator.relevance-medium')).to_have_count(1)
                expect(page.locator('.relevance-indicator.relevance-low')).to_have_count(1)
                expect(page.locator('#relevance-key .relevance-key-item')).to_have_count(4)
                indicator = page.locator('.relevance-indicator').first.bounding_box()
                bookmark = page.locator('.bookmark').first.bounding_box()
                assert indicator['width'] == indicator['height'] == bookmark['width'] == bookmark['height']
                assert indicator['x'] + indicator['width'] < bookmark['x']
                count_box = page.locator('#story-count').bounding_box()
                assert page.locator('#relevance-key').bounding_box()['y'] >= count_box['y'] + count_box['height']
                expect(page.locator('.relevance-indicator').first).to_have_attribute('title', 'Highly relevant: AI interest score 90/100')
                expect(page.locator('#feed-title')).to_have_text('Daily Feed')
                assert page.locator('.titlebar-caption').evaluate('el => getComputedStyle(el).fontSize') == '14px'
                assert page.locator('#story-count').evaluate('el => getComputedStyle(el).fontSize') == '14px'
                expect(page.locator('#results-heading')).to_have_text('Today’s Edition')
                heading = page.locator('#results-heading').bounding_box()
                count = page.locator('#story-count').bounding_box()
                assert count['y'] >= heading['y'] + heading['height']
                assert count['x'] == heading['x']
                assert page.locator('.title-buttons').inner_text() == '_ □ ×'
                assert page.locator('body').evaluate('el => getComputedStyle(el).userSelect') == 'none'
                assert page.locator('.story').first.evaluate('el => getComputedStyle(el).userSelect') == 'text'
                assert page.locator('.bookmark').first.evaluate('el => getComputedStyle(el).userSelect') == 'none'
                assert page.locator('#guidance').evaluate('el => getComputedStyle(el).userSelect') == 'text'
                assert page.locator('#topic-button .dropdown-arrow').count() == 1
                page.keyboard.press('Control+a')
                selected = page.evaluate('getSelection().toString()')
                assert 'Experimental HTTP method' in selected
                assert 'Daily Feed' not in selected
                assert '☆' not in selected
                page.evaluate('getSelection().removeAllRanges()')
                assert page.locator('#collector-status').count() == 0
                assert page.locator('footer .status-dot').count() == 1
                page.screenshot(path='/tmp/newsreview-desktop.png', full_page=True)

                page.get_by_role('button', name='Help', exact=True).click()
                expect(page.locator('#help-dialog')).to_be_visible()
                positions = page.locator('#help-dialog').evaluate('el => [el.querySelector(".dialog-heading").getBoundingClientRect().top, el.querySelector(".dialog-actions").getBoundingClientRect().bottom]')
                page.locator('#help-dialog .dialog-body').evaluate('el => { el.scrollTop = el.scrollHeight; }')
                assert positions == page.locator('#help-dialog').evaluate('el => [el.querySelector(".dialog-heading").getBoundingClientRect().top, el.querySelector(".dialog-actions").getBoundingClientRect().bottom]')
                page.locator('#help-dialog .dialog-actions button').click()
                page.get_by_role('button', name='Settings', exact=True).click()
                page.get_by_role('button', name='Collection & history', exact=True).click()
                page.get_by_role('button', name='Import/Export...', exact=True).click()
                with page.expect_download() as download_info:
                    page.locator('#export-backup').click()
                backup_path = Path(directory) / 'browser-export.zip'
                download_info.value.save_as(backup_path)
                with page.expect_file_chooser() as chooser:
                    page.locator('#choose-backup').click()
                chooser.value.set_files(str(backup_path))
                page.locator('#confirm-restore').check()
                page.locator('#restore-backup').click()
                expect(page.locator('#status-message')).to_contain_text('Restored', timeout=15000)
                page.locator('#transfer-dialog .dialog-actions').get_by_role('button', name='OK', exact=True).click()
                page.get_by_role('button', name='Daily Feed', exact=True).click()
                def inspect_bookmark_request(route):
                    assert page.locator('.bookmark').first.inner_text() == '☆'
                    assert 'Saving bookmark' in page.locator('#status-message').inner_text()
                    route.continue_()
                page.route('**/api/bookmarks/*', inspect_bookmark_request)
                page.locator('.bookmark').first.click()
                expect(page.locator('#status-message')).to_contain_text('Saved to bookmarks')
                page.unroute('**/api/bookmarks/*', inspect_bookmark_request)
                assert page.locator('.read-link').count() == 0
                page.get_by_role('button', name='Bookmarks', exact=True).click()
                expect(page.locator('.story')).to_have_count(1)
                assert 'HTTP' in page.locator('.story h3').inner_text()
                page.get_by_role('button', name='Daily Feed', exact=True).click()
                expect(page.locator('.story')).to_have_count(4)
                page.get_by_label('Previous day', exact=True).click()
                expect(page.locator('.story')).to_have_count(1)
                assert 'Yesterday' in page.locator('.story h3').inner_text()
                expect(page.locator('.relevance-indicator.relevance-pending')).to_have_count(1)
                page.get_by_role('button', name='Today', exact=True).click()
                page.get_by_label('Search history').fill('Android Authority')
                page.get_by_role('button', name='Search', exact=True).click()
                expect(page.locator('.story')).to_have_count(5)
                page.get_by_role('button', name='Clear search').click()
                expect(page.locator('.story')).to_have_count(4)
                page.get_by_label('Search history').fill('FreePBX')
                page.get_by_role('button', name='Search', exact=True).click()
                expect(page.locator('.story')).to_have_count(1)
                assert 'FreePBX' in page.locator('.story h3').inner_text()
                page.get_by_role('button', name='Clear search').click()
                expect(page.locator('.story')).to_have_count(4)

                assert page.locator('#topic-button').bounding_box()['width'] <= 170
                page.locator('#topic-button').press('ArrowDown')
                expect(page.locator('#topic-all')).to_be_focused()
                page.locator('#topic-all').press('Escape')
                expect(page.locator('#topic-menu')).to_be_hidden()
                page.locator('#topic-button').click()
                assert page.locator('#topic-menu').bounding_box()['width'] <= 230
                assert page.locator('#topic-menu').evaluate('el => el.scrollHeight <= el.clientHeight')
                assert page.locator('#topic-options').evaluate('el => el.scrollHeight > el.clientHeight')
                assert page.locator('#topic-options').evaluate('el => el.scrollWidth <= el.clientWidth')
                page.locator('#topic-options').evaluate('el => { el.scrollTop = el.scrollHeight; }')
                expect(page.locator('#topic-all')).to_be_visible()
                page.locator('#topic-options').evaluate('el => { el.scrollTop = 0; }')
                page.screenshot(path='/tmp/newsreview-topics.png')
                page.locator('#topic-options .topic-name').get_by_text('Protocols', exact=True).click()
                expect(page.get_by_role('checkbox', name='Protocols', exact=True)).to_be_checked()
                expect(page.locator('.story')).to_have_count(1)
                protocol_row = page.locator('#topic-options .topic-row').filter(has_text='Protocols')
                protocol_row.click(position={'x': protocol_row.bounding_box()['width'] - 4, 'y': 10})
                expect(page.get_by_role('checkbox', name='Protocols', exact=True)).not_to_be_checked()
                expect(page.locator('.story')).to_have_count(4)
                page.get_by_role('checkbox', name='Protocols', exact=True).check()
                expect(page.locator('.story')).to_have_count(1)
                page.locator('#topic-options .topic-name').get_by_text('Radio & SDR', exact=True).click()
                expect(page.get_by_role('checkbox', name='Radio & SDR', exact=True)).to_be_checked()
                expect(page.locator('.story')).to_have_count(2)
                page.locator('#topic-all').click()
                expect(page.locator('.story')).to_have_count(4)
                page.locator('#topic-button').click()
                yesterday = date.today() - timedelta(days=1)
                date_text = f'{yesterday.month}/{yesterday.day}/{yesterday.year} - {date.today().month}/{date.today().day}/{date.today().year}'
                with store.connect() as db:
                    db.execute("UPDATE stories SET discovered_at='2026-10-16T02:00:00+00:00'")
                    db.execute("UPDATE stories SET published_at='2026-10-15T15:00:00+00:00', discovered_at='2026-10-16T02:00:00+00:00' WHERE url='https://example.com/0'")
                page.emulate_media(reduced_motion='reduce')
                page.get_by_label('Date/time range', exact=True).fill('10am - 12pm')
                page.get_by_label('Date/time range', exact=True).press('Enter')
                expect(page.locator('.story')).to_have_count(1)
                expect(page.locator('.story h3')).to_contain_text('Experimental HTTP method')
                page.get_by_label('Date/time range', exact=True).fill(date_text)
                page.get_by_label('Date/time range', exact=True).press('Enter')
                expect(page.locator('.story')).to_have_count(5)
                page.get_by_label('Date/time range', exact=True).fill(date_text + ', 12am - 11:59pm')
                page.get_by_label('Date/time range', exact=True).press('Enter')
                expect(page.locator('.story')).to_have_count(5)
                page.get_by_role('button', name='Clear search').click()
                expect(page.locator('.story')).to_have_count(4)

                page.get_by_role('button', name='Settings', exact=True).click()
                page.get_by_role('button', name='Interests', exact=True).click()
                page.wait_for_selector('#guidance')
                page.locator('#guidance').fill('Favor RF projects and protocol changes.')
                page.locator('#interests-form').get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('#status-message')).to_contain_text('Interests saved')
                assert store.settings()['interests']['guidance'] == 'Favor RF projects and protocol changes.', repr(store.settings()['interests']['guidance'])
                page.get_by_role('button', name='Sources', exact=True).click()
                page.get_by_role('button', name='+ Add source', exact=True).click()
                assert page.locator('#source-dialog .dialog-actions button').all_text_contents() == ['Cancel', 'Save']
                assert page.locator('#source-category').bounding_box()['width'] <= 225
                category_box = page.locator('#source-category').bounding_box()
                arrow_box = page.locator('.editable-dropdown .dropdown-arrow').bounding_box()
                assert abs((category_box['y'] + category_box['height']/2) - (arrow_box['y'] + arrow_box['height']/2)) < 1
                page.get_by_role('button', name='Choose default category', exact=True).click()
                page.get_by_role('option', name='Hardware', exact=True).click()
                expect(page.locator('#source-category')).to_have_value('Hardware')
                page.screenshot(path='/tmp/newsreview-source-dialog.png')
                page.locator('#source-category').fill('Custom radio category')
                page.get_by_role('button', name='Choose default category', exact=True).click()
                page.get_by_role('button', name='Choose default category', exact=True).press('Escape')
                expect(page.locator('#source-dialog')).to_be_visible()
                page.get_by_label('Name', exact=True).fill('Fixture source')
                page.get_by_label('Website URL', exact=True).fill(f'http://127.0.0.1:{fixture.server_port}/')
                page.get_by_role('button', name='Find RSS/Atom feed').click()
                expect(page.locator('#source-feed')).to_have_value(re.compile('/feed$'))
                page.locator('#source-feed').fill(f'http://127.0.0.1:{fixture.server_port}/stale-feed')
                page.get_by_role('button', name='Preview stories', exact=True).click()
                expect(page.locator('#source-preview')).to_contain_text('Website fallback could not be read')
                page.locator('#source-url').fill(f'http://127.0.0.1:{fixture.server_port}/listing')
                page.get_by_role('button', name='Preview stories', exact=True).click()
                expect(page.locator('#source-preview')).to_contain_text('RSS/Atom + website')
                page.locator('#source-feed').fill(f'http://127.0.0.1:{fixture.server_port}/feed')
                page.locator('#source-url').fill(f'http://127.0.0.1:{fixture.server_port}')
                page.get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('.source-row')).to_have_count(34)
                page.get_by_role('button', name='+ Add source', exact=True).click()
                page.get_by_label('Name', exact=True).fill('Website without RSS')
                page.get_by_label('Website URL', exact=True).fill(f'http://127.0.0.1:{fixture.server_port}/listing')
                page.get_by_role('button', name='Collection method', exact=True).click()
                page.get_by_role('option', name='Website page — no RSS needed', exact=True).click()
                page.get_by_role('button', name='Preview stories', exact=True).click()
                expect(page.locator('#source-preview')).to_contain_text('Website page')
                expect(page.locator('#source-preview a')).to_have_count(2)
                page.get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('.source-row')).to_have_count(35)
                page.get_by_role('button', name='Local AI', exact=True).click()
                page.get_by_role('button', name='Server type', exact=True).click()
                page.get_by_role('option', name='Compatible local chat server (e.g. LM Studio)', exact=True).click()
                expect(page.locator('#ai-provider')).to_have_value('compatible')
                page.get_by_role('button', name='Server type', exact=True).press('ArrowDown')
                page.keyboard.press('Home')
                page.keyboard.press('Enter')
                expect(page.locator('#ai-provider')).to_have_value('ollama')
                page.locator('#ai-url').fill(f'http://127.0.0.1:{fixture.server_port}')
                page.locator('#ai-model').fill('fixture')
                page.get_by_role('button', name='Test with a sample article').click()
                expect(page.locator('#model-test-result')).to_contain_text('validation passed')
                page.screenshot(path='/tmp/newsreview-ai-settings.png')
                page.locator('#ai-form').get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('#status-message')).to_contain_text('AI settings saved')
                assert store.settings()['ai']['model'] == 'fixture'

                # Keep this test entirely on the fixture server when testing Refresh.
                for configured in store.sources():
                    if not configured['url'].startswith(f'http://127.0.0.1:{fixture.server_port}'):
                        store.save_source({**configured, 'enabled': False}, configured['id'])
                page.get_by_role('button', name='Sources', exact=True).click()
                expect(page.locator('#check-new-sources')).to_be_enabled()
                expect(page.locator('#check-new-sources')).to_have_text('Check new sources (2)')
                page.route('**/api/collect', lambda route: route.abort('blockedbyclient'))
                page.locator('#check-new-sources').click()
                expect(page.locator('#source-refresh-status')).to_contain_text('Last collection: complete', timeout=15000)
                expect(page.locator('#check-new-sources')).to_have_text('Check new sources (0)')
                expect(page.locator('#check-new-sources')).to_be_disabled()
                expect(page.locator('.source-state.healthy')).to_have_count(2)
                page.route('**/api/jobs/start', lambda route: route.abort('blockedbyclient'))
                page.get_by_role('button', name='Refresh sources', exact=True).click()
                expect(page.locator('#status-message')).to_contain_text('Could not send POST /api/jobs/start')
                expect(page.locator('#status-message')).to_contain_text('ERR_BLOCKED_BY_CLIENT')
                expect(page.locator('#dismiss-status')).to_be_visible()
                assert page.locator('#status-message').evaluate('el => getComputedStyle(el).whiteSpace') == 'normal'
                page.set_viewport_size({'width': 390, 'height': 844})
                assert page.locator('#status-message').evaluate('el => el.scrollWidth <= el.clientWidth')
                expect(page.locator('#dismiss-status')).to_be_in_viewport()
                page.screenshot(path='/tmp/newsreview-blocked-request.png')
                page.set_viewport_size({'width': 1440, 'height': 1050})
                page.get_by_role('button', name='Interests', exact=True).click()
                page.get_by_role('button', name='Sources', exact=True).click()
                expect(page.locator('#status-message')).to_contain_text('Could not send POST /api/jobs/start')
                page.locator('#dismiss-status').click()
                expect(page.locator('#status-message')).not_to_contain_text('Could not send POST')
                page.unroute('**/api/jobs/start')
                page.context.set_offline(True)
                page.get_by_role('button', name='Refresh sources', exact=True).click()
                expect(page.locator('#status-message')).to_contain_text('Cannot reach the NewsReview server')
                expect(page.locator('#source-refresh-status')).to_contain_text('not confirmed')
                page.context.set_offline(False)
                expect(page.locator('#source-refresh-status')).to_contain_text('No newer collection was recorded', timeout=15000)
                expect(page.locator('#status-message')).to_contain_text('Could not send POST /api/jobs/start')
                page.locator('#dismiss-status').click()
                expect(page.locator('#status-message')).not_to_contain_text('Could not send POST')
                expect(page.locator('#source-refresh-status')).to_contain_text('No newer collection was recorded')

                page.get_by_role('button', name='Daily Feed', exact=True).click()
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
                expect(page.locator('#status-message')).to_contain_text('Selected data reset')
                assert store.stories()['total'] == 0
                assert store.settings() == preferences
                assert len(store.sources()) == 35
                page.get_by_role('button', name='Daily Feed', exact=True).click()
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
