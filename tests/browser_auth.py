"""Exercise the copied setup/login screen with a real browser and HTTP server."""
from pathlib import Path
import tempfile
import threading

from playwright.sync_api import sync_playwright, expect

from newsreview.auth import Auth
from newsreview.collector import Collector
from newsreview.server import AppServer
from newsreview.store import Store


def run():
    with tempfile.TemporaryDirectory() as directory:
        store = Store(Path(directory) / 'news.db')
        collector = Collector(store)
        server = AppServer(('127.0.0.1', 0), store, collector)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        root = f'http://127.0.0.1:{server.server_port}'
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch(headless=True)
                page = browser.new_page(viewport={'width': 1280, 'height': 900})
                errors = []
                page.on('pageerror', lambda error: errors.append(str(error)))
                page.goto(root)
                expect(page).to_have_url(root + '/login.html')
                expect(page.locator('#loginWindowTitle')).to_have_text('Create Password')
                page.locator('#setupToken').fill('wrong')
                page.locator('#password').fill('browser-password')
                page.locator('#confirmPassword').fill('browser-password')
                page.locator('#loginButton').click()
                expect(page.locator('#loginError')).to_contain_text('Incorrect setup token')
                page.locator('#setupToken').fill(server.auth.setup_token)
                page.screenshot(path='/tmp/newsreview-setup.png')
                page.locator('#loginButton').click()
                expect(page.locator('#version')).to_be_visible()
                page.locator('#logout').click()
                expect(page.locator('#loginWindowTitle')).to_have_text('Log In')
                expect(page.locator('#setupTokenGroup')).to_be_hidden()
                page.locator('#password').fill('wrong-password')
                page.locator('#loginButton').click()
                expect(page.locator('#loginError')).to_contain_text('Incorrect password')
                page.locator('#password').fill('browser-password')
                page.locator('#loginButton').click()
                expect(page.locator('#version')).to_be_visible()
                page.locator('#logout').click()
                expect(page.locator('#loginWindowTitle')).to_have_text('Log In')
                page.set_viewport_size({'width': 390, 'height': 700})
                page.evaluate("localStorage.setItem('newsreview-theme', 'dark')")
                page.reload()
                expect(page.locator('#loginButton')).to_be_visible()
                box = page.locator('.login-window').bounding_box()
                assert box['width'] <= 390 and box['height'] < 700
                page.screenshot(path='/tmp/newsreview-login-mobile.png')
                assert Auth(Path(directory) / 'auth.json').verify('browser-password')
                assert not errors, errors
                browser.close()
                print('Browser setup, logout, login, mobile layout, and console checks passed.')
        finally:
            server.shutdown()
            server.server_close()
            worker.join()
            collector.stop()


if __name__ == '__main__':
    run()
