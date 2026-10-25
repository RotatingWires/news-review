"""Rate articles and manage compact preferences through the real browser UI."""
from http.server import ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading

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
        fixture = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        fixture.requests = []
        threading.Thread(target=fixture.serve_forever, daemon=True).start()
        root = f'http://127.0.0.1:{fixture.server_port}'
        store = Store(Path(directory) / 'news.db')
        source = store.sources()[0]['id']
        ai = {**store.settings()['ai'], 'base_url': root, 'model': 'fixture'}
        store.save_settings({'ai': ai})
        store.insert_story(source, {'headline': 'Practical SDR project', 'url': 'https://example.com/project',
                                   'excerpt': 'Useful radio hardware details.', 'published_at': None, 'category': 'Radio'}, '2026-10-25')
        identifier = store.stories()['items'][0]['id']
        store.review_result(identifier, validate_review(review(score=25), set()), ai, store.settings()['interests'], 'Text', 'article')
        # Legacy explanations remain compatible in storage, but are never displayed.
        with store.connect() as db:
            db.execute("UPDATE stories SET why_it_matters='Legacy significance.'")
        fetcher = Fetcher(public=False)
        collector = Collector(store, fetcher, Reviewer(fetcher))
        server = AppServer(('127.0.0.1', 0), store, collector, 'fixture-password')
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page(viewport={'width': 390, 'height': 844})
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                address = f'http://127.0.0.1:{server.server_port}'
                page.context.add_cookies([{'name': 'newsreview_session', 'value': server.session(), 'url': address}])
                page.goto(address)
                page.locator('.story').wait_for()
                expect(page.locator('.significance')).to_have_count(0)
                page.get_by_role('button', name='Like: Practical SDR project', exact=True).click()
                page.locator('#feedback-reason').fill('More practical low-cost radio projects')
                page.locator('#feedback-form').get_by_role('button', name='Save', exact=True).click()
                expect(page.get_by_role('button', name='Like: Practical SDR project', exact=True)).to_have_attribute('aria-pressed', 'true')
                page.reload()
                expect(page.get_by_role('button', name='Like: Practical SDR project', exact=True)).to_have_attribute('aria-pressed', 'true')
                page.get_by_role('button', name='Dislike: Practical SDR project', exact=True).click()
                expect(page.locator('#feedback-reason')).to_have_value('More practical low-cost radio projects')
                page.locator('#feedback-reason').fill('Topic is interesting, but avoid shopping lists')
                page.locator('#feedback-form').get_by_role('button', name='Save', exact=True).click()
                expect(page.get_by_role('button', name='Dislike: Practical SDR project', exact=True)).to_have_attribute('aria-pressed', 'true')
                page.get_by_role('button', name='Settings', exact=True).click()
                expect(page.locator('#feedback-profile-status')).to_contain_text('1 saved rating')
                page.locator('#feedback-rebuild').click()
                expect(page.locator('#feedback-profile')).to_have_value('Favor practical SDR projects. Avoid shopping lists, while retaining interest in radio technology.')
                page.locator('#feedback-profile').fill('My manual preference profile')
                page.locator('#feedback-auto').uncheck()
                page.locator('#feedback-profile-form').get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('#status-message')).to_have_text('Learned preferences saved.')
                expect(page.locator('#feedback-profile')).to_have_value('My manual preference profile')
                page.locator('#feedback-reset').click()
                page.locator('#feedback-reset-dialog').get_by_role('button', name='Cancel', exact=True).click()
                assert len(store.feedback_state()['ratings']) == 1
                page.locator('#feedback-reset').click()
                page.locator('#feedback-confirm-reset').click()
                expect(page.locator('#feedback-profile-status')).to_contain_text('0 saved ratings')
                assert not store.feedback_state()['ratings']
                assert store.stories()['total'] == 1
                assert not errors, errors
                browser.close()
                print('Feedback browser checks passed: ratings/reasons, reload, profile generation/edit, reset/cancel, mobile UI, and summary-only tiles.')
        finally:
            collector.stop()
            server.shutdown()
            server.server_close()
            fixture.shutdown()
            fixture.server_close()


if __name__ == '__main__':
    run()
