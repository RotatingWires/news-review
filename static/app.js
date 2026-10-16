import { Feed } from './feed.js';
import { Settings } from './settings.js';
import { Transfer } from './transfer.js';

import { $, api, busy, toast, formatTime } from './shared.js';

class App {
  constructor() {
    this.overview = null;
    this.view = 'daily';
    this.authenticated = false;
    this.feed = new Feed(this);
    this.settings = new Settings(this);
    this.transfer = new Transfer(this);
    this.pollTimer = null;
    this.unconfirmedRefresh = null;
  }

  async init() {
    document.addEventListener('newsreview-connection', event => {
      $('connection-status').hidden = !event.detail.error;
      $('connection-status').textContent = event.detail.error || '';
    });
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
    $('help-nav').onclick = () => $('help-dialog').showModal();
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
    } catch (error) { toast(error.message, true); $('collector-status').textContent = error.message; this.poll(); }
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
    document.querySelector('.status-dot').classList.toggle('collecting', state.running);
    document.querySelector('.status-bar').setAttribute('aria-busy', String(state.running));
    const pending = this.overview.counts.pending || 0;
    $('collector-status').textContent = state.running ? state.message : `${state.message} · ${pending} awaiting review`;
    $('collect').disabled = state.running;
    $('review-pending').disabled = state.running || pending === 0;
    this.feed.renderTopics(this.overview.tags);
    $('clear-history').disabled = state.running;
    $('refresh-sources').disabled = state.running;
    this.transfer.sync();
    const last = this.overview.runs.find(run => run.kind === 'collect');
    let refreshStatus = state.running ? state.message : last
      ? `${state.message} · Last collection: ${last.status} · Started ${formatTime(last.started_at)} · ${last.details.errors?.length || 0} errors. See each source and run history for details.`
      : state.message;
    if (this.unconfirmedRefresh !== null) {
      if (state.running || (last && last.id > this.unconfirmedRefresh)) this.unconfirmedRefresh = null;
      else refreshStatus = 'No newer collection was recorded. Press Refresh sources to retry. ' + refreshStatus;
    }
    $('source-refresh-status').textContent = refreshStatus;
    this.settings.renderRuns();
  }

  poll() {
    clearTimeout(this.pollTimer);
    this.pollTimer = setTimeout(async () => {
      try {
        if (!this.authenticated) {
          const session = await api('session');
          if (!session.authenticated) { if (!$('login-dialog').open) $('login-dialog').showModal(); return; }
          this.authenticated = true;
        }
        const firstConnection = !this.overview;
        const lastRun = this.overview?.runs[0]?.id;
        const lastRevision = this.overview?.revision;
        const wasRunning = this.overview?.collector.running;
        await this.refreshOverview();
        if (lastRevision && lastRevision !== this.overview.revision) this.feed.clearFilters();
        if (firstConnection || lastRevision !== this.overview.revision || lastRun !== this.overview.runs[0]?.id || (wasRunning && !this.overview.collector.running)) {
          if (this.view !== 'settings') await this.feed.load();
          else if (lastRevision !== this.overview.revision) await this.settings.load();
          else await this.settings.refreshSources();
        }
        if (this.view === 'settings' && !$('panel-sources').hidden) await this.settings.refreshSources();
      } catch (error) {
        if ($('login-dialog').open) { this.authenticated = false; return; }
        $('collector-status').textContent = error.message;
      }
      this.poll();
    }, this.overview?.collector.running ? 2500 : !$('connection-status').hidden ? 5000 : 30000);
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
    const previousRun = this.overview?.runs[0]?.id || 0;
    await busy(button, async () => {
      try {
        await api('collect', 'POST', { kind });
        if (kind === 'collect') this.unconfirmedRefresh = null;
        toast(kind === 'collect' ? 'Collection started in the background.' : 'Reviewing queued stories.');
      } catch (error) {
        if (error.connection) {
          if (kind === 'collect') this.unconfirmedRefresh = previousRun;
          $('source-refresh-status').textContent = 'Refresh request was not confirmed. Reconnecting to check whether the server started it.';
        }
        throw error;
      }
    });
    try {
      await this.refreshOverview();
      if (!this.overview.collector.running) {
        if (this.view === 'settings') await this.settings.refreshSources();
        else await this.feed.load();
      }
    } catch (error) { if (!error.connection) toast(error.message, true); }
    finally { this.poll(); }
  }
}

const app = new App();
app.init();
