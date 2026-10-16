import { Feed } from './feed.js';
import { Settings } from './settings.js';

import { $, api, busy, toast } from './shared.js';

class App {
  constructor() {
    this.overview = null;
    this.view = 'daily';
    this.authenticated = false;
    this.feed = new Feed(this);
    this.settings = new Settings(this);
    this.pollTimer = null;
  }

  async init() {
    try {
      const saved = localStorage.getItem('newsreview-theme');
      document.documentElement.dataset.theme = saved || 'light';
    } catch { /* Storage may be disabled; the default theme remains usable. */ }
    $('theme').onclick = () => {
      const theme = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
      document.documentElement.dataset.theme = theme;
      $('theme').textContent = theme === 'light' ? 'Dark theme' : 'Light theme';
      try { localStorage.setItem('newsreview-theme', theme); } catch { /* Optional preference. */ }
    };
    $('theme').textContent = document.documentElement.dataset.theme === 'dark' ? 'Light theme' : 'Dark theme';
    document.querySelectorAll('[data-close]').forEach(button => { button.onclick = () => $(button.dataset.close).close(); });
    $('daily-nav').onclick = () => this.show('daily');
    $('bookmarks-nav').onclick = () => this.show('bookmarks');
    $('settings-nav').onclick = () => this.show('settings');
    $('collect').onclick = () => this.collect('collect', $('collect'));
    $('review-pending').onclick = () => this.collect('review', $('review-pending'));
    $('login-dialog').addEventListener('cancel', event => event.preventDefault());
    $('login-form').onsubmit = async event => {
      event.preventDefault();
      await busy(event.submitter, async () => {
        try {
          await api('login', 'POST', { password: $('password').value });
          $('password').value = '';
          $('login-error').textContent = '';
          $('login-dialog').close();
          await this.load();
        } catch (error) { $('login-error').textContent = error.message; }
      });
    };
    $('logout').onclick = async () => {
      try {
        await api('logout', 'POST', {});
        this.authenticated = false;
        clearTimeout(this.pollTimer);
        $('stories').replaceChildren();
        $('settings').hidden = true;
        $('reader').hidden = false;
        $('login-dialog').showModal();
      } catch (error) { toast(error.message, true); }
    };
    try {
      const session = await api('session');
      $('logout').hidden = !session.password_required;
      if (!session.authenticated) { $('login-dialog').showModal(); return; }
      await this.load();
    } catch (error) { toast(error.message, true); $('collector-status').textContent = 'Could not connect to the app server.'; }
  }

  async load() {
    this.authenticated = true;
    await this.refreshOverview();
    if (!$('day').value) $('day').value = this.overview.today;
    await this.feed.load();
    this.poll();
  }

  async refreshOverview() {
    this.overview = await api('overview');
    $('version').textContent = 'v' + this.overview.version;
    const state = this.overview.collector;
    const pending = this.overview.counts.pending || 0;
    $('collector-status').textContent = state.running ? state.message : `${state.message} · ${pending} awaiting review`;
    $('collect').disabled = state.running;
    $('review-pending').disabled = state.running || pending === 0;
    this.feed.renderTopics(this.overview.tags);
    $('clear-history').disabled = state.running;
    this.settings.renderRuns();
  }

  poll() {
    clearTimeout(this.pollTimer);
    this.pollTimer = setTimeout(async () => {
      if (!this.authenticated) return;
      try {
        const wasRunning = this.overview?.collector.running;
        await this.refreshOverview();
        if (wasRunning && !this.overview.collector.running) {
          if (this.view !== 'settings') await this.feed.load();
          else await this.settings.refreshSources();
        }
      } catch (error) {
        if ($('login-dialog').open) { this.authenticated = false; return; }
        $('collector-status').textContent = error.message;
      }
      this.poll();
    }, this.overview?.collector.running ? 2500 : 30000);
  }

  async show(view) {
    if (!this.authenticated) return;
    this.view = view;
    ['daily', 'bookmarks', 'settings'].forEach(name => {
      const button = $(name + '-nav');
      button.classList.toggle('active', name === view);
      if (name === view) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    });
    $('settings').hidden = view !== 'settings';
    $('reader').hidden = view === 'settings';
    try {
      if (view === 'settings') await this.settings.load();
      else {
        this.feed.page = 1;
        $('feed-title').textContent = view === 'bookmarks' ? 'Bookmarks' : 'Daily feed';
        $('feed-intro').textContent = view === 'bookmarks' ? 'Your saved stories, across every edition.' : 'Stories from your sources, reviewed for your interests.';
        await this.feed.load();
      }
    } catch (error) { toast(error.message, true); }
  }

  async collect(kind, button) {
    await busy(button, async () => {
      await api('collect', 'POST', { kind });
      toast(kind === 'collect' ? 'Collection started in the background.' : 'Reviewing queued stories.');
    });
    try { await this.refreshOverview(); this.poll(); } catch (error) { toast(error.message, true); }
  }
}

const app = new App();
app.init();
