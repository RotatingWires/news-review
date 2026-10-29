"""Save, replace, scope, and remove model credentials through the real UI."""
from http.server import ThreadingHTTPServer
from pathlib import Path
import tempfile
import threading

from playwright.sync_api import sync_playwright, expect

from newsreview.collector import Collector
from newsreview.model_keys import ModelKeys
from newsreview.network import Fetcher
from newsreview.reviewer import Reviewer
from newsreview.server import AppServer
from newsreview.store import Store
from tests.test_integration import FixtureHandler


def run():
    with tempfile.TemporaryDirectory() as directory:
        fixture = ThreadingHTTPServer(('127.0.0.1', 0), FixtureHandler)
        fixture.requests, fixture.authorizations = [], []
        fixture_worker = threading.Thread(target=fixture.serve_forever, daemon=True)
        fixture_worker.start()
        model_root = f'http://127.0.0.1:{fixture.server_port}'
        store = Store(Path(directory) / 'news.db')
        keys = ModelKeys(Path(directory) / 'model-credentials.json', environment={})
        collector = Collector(store, Fetcher(public=False), Reviewer(Fetcher(public=False)), model_keys=keys)
        server = AppServer(('127.0.0.1', 0), store, collector, 'fixture-password')
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        secret1, secret2 = 'fake-ui-first-private-key', 'fake-ui-replacement-private-key'
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page(viewport={'width': 1440, 'height': 1000})
                errors, response_bodies = [], []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.on('response', lambda response: response_bodies.append(response.text()) if '/api/' in response.url and response.headers.get('content-type', '').startswith('application/json') else None)
                root = f'http://127.0.0.1:{server.server_port}'
                page.context.add_cookies([{'name': 'newsreview_session', 'value': server.session(), 'url': root}])
                page.goto(root)

                def open_ai():
                    page.get_by_role('button', name='Settings', exact=True).click()
                    page.get_by_role('button', name='AI', exact=True).click()

                open_ai()
                expect(page.locator('#model-key-panel')).not_to_be_visible()
                page.get_by_role('button', name='Server type', exact=True).click()
                page.get_by_role('option', name='OpenRouter', exact=True).click()
                page.locator('#ai-url').fill(model_root + '/v1')
                page.locator('#ai-model').fill('openrouter-fixture')
                expect(page.locator('#model-key-status')).to_have_text('No key configured.')
                password = page.locator('#model-api-key')
                expect(password).to_have_attribute('type', 'password')
                password.fill(secret1)
                page.locator('#model-key-save').click()
                expect(password).to_have_value('')
                expect(page.locator('#model-key-status')).to_have_text('Key configured.')
                expect(page.locator('#model-key-save')).to_have_text('Replace key')
                expect(page.locator('#model-key-remove')).to_be_enabled()
                page.get_by_role('button', name='Test with a sample article', exact=True).click()
                expect(page.locator('#model-test-result')).to_contain_text('validation passed')
                assert fixture.authorizations[-1] == 'Bearer ' + secret1
                password.fill(secret2)
                page.locator('#model-key-save').click()
                expect(password).to_have_value('')
                page.get_by_role('button', name='Test with a sample article', exact=True).click()
                expect(page.locator('#model-test-result')).to_contain_text('validation passed')
                assert fixture.authorizations[-1] == 'Bearer ' + secret2
                page.locator('#ai-form').get_by_role('button', name='Save', exact=True).click()
                expect(page.locator('#status-message')).to_have_text('AI settings saved.')
                page.reload()
                open_ai()
                expect(page.locator('#model-key-status')).to_have_text('Key configured.')
                expect(password).to_have_value('')
                page.locator('#ai-url').fill(model_root + '/different/v1')
                password.focus()
                expect(page.locator('#model-key-status')).to_have_text('No key configured.')
                expect(page.locator('#model-key-remove')).to_be_disabled()
                page.locator('#ai-url').fill(model_root + '/v1')
                password.focus()
                expect(page.locator('#model-key-status')).to_have_text('Key configured.')
                page.get_by_role('button', name='Server type', exact=True).click()
                page.get_by_role('option', name='Compatible chat server (e.g. LM Studio)', exact=True).click()
                expect(page.locator('#model-key-status')).to_have_text('No key configured.')
                page.get_by_role('button', name='Server type', exact=True).click()
                page.get_by_role('option', name='OpenRouter', exact=True).click()
                expect(page.locator('#model-key-status')).to_have_text('Key configured.')
                page.locator('#model-key-remove').click()
                dialog = page.locator('#model-key-remove-dialog')
                dialog.get_by_role('button', name='Cancel', exact=True).click()
                config = {'provider': 'openrouter', 'base_url': model_root + '/v1'}
                assert keys.get(config) == secret2
                page.set_viewport_size({'width': 390, 'height': 844})
                page.locator('#model-key-remove').click()
                expect(dialog.get_by_role('button', name='Remove', exact=True)).to_be_in_viewport()
                dialog.get_by_role('button', name='Remove', exact=True).click()
                expect(dialog).not_to_be_visible()
                expect(page.locator('#model-key-status')).to_have_text('No key configured.')
                assert keys.get(config) == ''
                keys.environment['NEWSREVIEW_MODEL_API_KEY'] = 'fake-environment-key'
                page.reload()
                open_ai()
                expect(page.locator('#model-key-status')).to_have_text('Key configured from the server environment.')
                expect(page.locator('#model-key-remove')).to_be_disabled()
                password.fill(secret1)
                page.locator('#model-key-save').click()
                expect(password).to_have_value('')
                page.locator('#model-key-remove').click()
                expect(page.locator('#model-key-environment-fallback')).to_be_visible()
                dialog.get_by_role('button', name='Remove', exact=True).click()
                expect(dialog).not_to_be_visible()
                expect(page.locator('#model-key-status')).to_have_text('Key configured from the server environment.')
                assert keys.get(config) == 'fake-environment-key'
                assert all(secret1 not in body and secret2 not in body for body in response_bodies)
                assert page.locator('body').evaluate('el => el.scrollWidth <= innerWidth')
                assert not errors, errors
                browser.close()
                print('Model-key browser checks passed: save/replace, hidden stored values, immediate use, scope isolation, reload, remove/cancel, environment fallback, and mobile dialogs.')
        finally:
            collector.stop()
            server.shutdown()
            server.server_close()
            worker.join()
            fixture.shutdown()
            fixture.server_close()
            fixture_worker.join()


if __name__ == '__main__':
    run()
