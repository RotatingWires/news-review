"""Browser checks for idle silence, active polling, and return-to-tab refresh.

Uses Playwright's clock and explicit fixture job state; no publisher/model access.
"""
from pathlib import Path
import tempfile
import threading

from playwright.sync_api import sync_playwright, expect

from newsreview import __version__
from newsreview.collector import Collector
from newsreview.server import AppServer
from newsreview.store import Store


def run():
    with tempfile.TemporaryDirectory() as directory:
        store = Store(Path(directory) / 'newsreview.db')
        collector = Collector(store)
        server = AppServer(('127.0.0.1', 0), store, collector)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch(headless=True)
                page = browser.new_page()
                page.clock.install()
                requests = []
                errors = []
                page.on('request', lambda request: requests.append(request.url) if request.url.endswith('/api/overview') else None)
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.goto(f'http://127.0.0.1:{server.server_port}')
                expect(page.locator('#version')).to_have_text('v' + __version__)
                expect(page.locator('#empty')).to_be_visible()

                def advance(milliseconds):
                    page.clock.fast_forward(milliseconds)
                    page.wait_for_timeout(100)

                count = len(requests)
                advance(35000)
                assert len(requests) == count, 'Idle tab polled the server'

                # A server-side/scheduled job can start without browser requests.
                collector.update(running=True, message='Fixture job started')
                advance(35000)
                assert len(requests) == count, 'Idle tab kept polling for external jobs'
                page.evaluate('window.dispatchEvent(new Event("focus"))')
                advance(150)
                expect(page.locator('footer')).to_have_attribute('aria-busy', 'true')
                expect(page.locator('#status-message')).to_contain_text('Fixture job started')

                collector.update(message='Fixture job progressed')
                advance(2600)
                expect(page.locator('#status-message')).to_contain_text('Fixture job progressed')
                assert len(requests) > count
                collector.update(running=False, message='Fixture job finished')
                advance(2600)
                expect(page.locator('footer')).to_have_attribute('aria-busy', 'false')
                expect(page.locator('#status-message')).to_contain_text('Fixture job finished')
                count = len(requests)
                advance(35000)
                assert len(requests) == count, 'Polling continued after the job finished'

                page.get_by_role('button', name='Settings', exact=True).click()
                page.locator('#guidance').fill('Unsaved editorial guidance')
                collector.update(message='Changed elsewhere')
                page.evaluate('window.dispatchEvent(new Event("focus")); document.dispatchEvent(new Event("visibilitychange"))')
                advance(150)
                expect(page.locator('#status-message')).to_contain_text('Changed elsewhere')
                expect(page.locator('#guidance')).to_have_value('Unsaved editorial guidance')

                page.context.set_offline(True)
                page.evaluate('window.dispatchEvent(new Event("focus"))')
                advance(150)
                expect(page.locator('#status-message')).to_contain_text('Cannot reach')
                count = len(requests)
                advance(5100)
                assert len(requests) > count, 'Connection failure was not retried'
                page.context.set_offline(False)
                advance(5100)
                expect(page.locator('#status-message')).not_to_contain_text('Cannot reach')
                count = len(requests)
                advance(35000)
                assert len(requests) == count, 'Recovery polling continued after reconnecting'
                assert not errors, errors
                browser.close()
                print('Polling QA passed: idle silence, focus refresh, active progress/completion, unsaved settings, connection retries and recovery.')
        finally:
            collector.stop()
            server.shutdown()
            server.server_close()
            worker.join()


if __name__ == '__main__':
    run()
