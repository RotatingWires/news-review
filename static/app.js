import { syncDropdowns } from './dropdowns.js';
import { Feed } from './feed.js';
import { Settings } from './settings.js';
import { Transfer } from './transfer.js';

import { $, api, busy, notify, formatTime, setConnectionStatus, setDefaultStatus, dismissStatus } from './shared.js';

class App {
  constructor() {
    this.overview = null;
    this.view = 'daily';
    this.authenticated = false;
    this.feed = new Feed(this);
    this.settings = new Settings(this);
    this.transfer = new Transfer(this);
    this.pollTimer = null;
    this.resumeTimer = null;
    this.syncInFlight = false;
    this.unconfirmedRefresh = null;
    this.connectionError = false;
    syncDropdowns();
  }

  async init() {
    document.addEventListener('newsreview-connection', event => {
      const wasDisconnected = this.connectionError;
      this.connectionError = Boolean(event.detail.error);
      setConnectionStatus(event.detail.error);
      if (this.connectionError || wasDisconnected) this.poll();
    });
    const resume = () => {
      clearTimeout(this.resumeTimer);
      if (document.hidden) { clearTimeout(this.pollTimer); return; }
      if (!this.overview && !this.connectionError) return;
      this.resumeTimer = setTimeout(() => this.syncView(true), 100);
    };
    document.addEventListener('visibilitychange', resume);
    window.addEventListener('focus', resume);
    window.addEventListener('online', () => { if (this.connectionError) resume(); });
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
    $('dismiss-status').onclick = dismissStatus;
    $('help-nav').onclick = () => $('help-dialog').showModal();
    $('collect').onclick = () => this.collect('collect', $('collect'));
    $('review-pending').onclick = () => this.collect('review', $('review-pending'));
    $('logout').onclick = async () => {
      try {
        await api('auth/logout', 'POST', {});
        this.authenticated = false;
        clearTimeout(this.pollTimer);
        clearTimeout(this.resumeTimer);
        $('stories').replaceChildren();
        $('settings').hidden = true;
        $('reader').hidden = false;
        location.replace('/login.html');
      } catch (error) { notify(error.message, true); }
    };
    try {
      const session = await api('auth/status');
      $('logout').hidden = !session.password_required;
      if (!session.authenticated) { location.replace('/login.html'); return; }
      await this.load();
    } catch (error) { notify(error.message, true); setDefaultStatus(error.message); this.poll(); }
  }

  async load() {
    this.authenticated = true;
    await this.refreshOverview();
    if (!$('day').value) $('day').value = this.overview.today;
    await this.feed.load();
    this.poll();
  }

  async reloadDataset() {
    // Reset/restore must invalidate pending feed reads before replacing cached rows.
    this.feed.requestId++;
    $('stories').replaceChildren();
    await this.refreshOverview();
    this.feed.clearFilters();
    this.feed.page = 1;
    await this.settings.load();
    await this.feed.load();
  }

  async refreshOverview() {
    this.overview = await api('overview');
    $('version').textContent = 'v' + this.overview.version;
    document.querySelector('.titlebar-name').textContent = 'NewsReview v' + this.overview.version;
    const state = this.overview.collector;
    document.querySelector('.status-dot').classList.toggle('collecting', state.running);
    document.querySelector('footer').setAttribute('aria-busy', String(state.running));
    const pending = this.overview.counts.pending || 0;
    setDefaultStatus(state.running ? state.message : `${state.message} · ${pending} awaiting review`);
    $('collect').disabled = state.running;
    $('review-pending').disabled = state.running || pending === 0;
    this.feed.renderTopics(this.overview.tags);
    $('clear-history').disabled = state.running;
    $('refresh-sources').disabled = state.running;
    this.settings.syncNewSources();
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
    this.poll();
  }

  poll() {
    clearTimeout(this.pollTimer);
    this.pollTimer = null;
    if (document.hidden || this.syncInFlight || this.transfer.busy) return;
    const delay = this.connectionError ? 5000 : this.overview?.collector.running ? 2500 : null;
    if (delay === null || (!this.authenticated && !this.connectionError)) return;
    this.pollTimer = setTimeout(() => this.syncView(), delay);
  }

  async syncView(force = false) {
    if (document.hidden || this.syncInFlight || this.transfer.busy) return;
    if (!this.authenticated && !this.connectionError) return;
    this.syncInFlight = true;
    clearTimeout(this.pollTimer);
    try {
      if (!this.authenticated) {
        const session = await api('auth/status');
        if (!session.authenticated) { location.replace('/login.html'); return; }
        this.authenticated = true;
      }
      const firstConnection = !this.overview;
      const lastRun = this.overview?.runs[0]?.id;
      const lastRevision = this.overview?.revision;
      const wasRunning = this.overview?.collector.running;
      await this.refreshOverview();
      const replaced = lastRevision && lastRevision !== this.overview.revision;
      if (replaced) this.feed.clearFilters();
      const changed = firstConnection || replaced || lastRun !== this.overview.runs[0]?.id || (wasRunning && !this.overview.collector.running);
      if (this.view !== 'settings' && (force || changed)) await this.feed.load();
      else if (this.view === 'settings') {
        // Returning to the page must not overwrite unsaved preference forms.
        if (replaced) await this.settings.load();
        else if (changed || (force && !$('panel-sources').hidden) || this.overview.collector.running)
          await this.settings.refreshSources();
      }
    } catch (error) {
      if (error.status === 401) this.authenticated = false;
      setDefaultStatus(error.message);
    } finally {
      this.syncInFlight = false;
      this.poll();
    }
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
      await this.refreshOverview();
      if (view === 'settings') await this.settings.load();
      else {
        this.feed.page = 1;
        $('feed-title').textContent = view === 'bookmarks' ? 'Bookmarks' : 'Daily Feed';
        $('feed-intro').textContent = view === 'bookmarks' ? 'Your saved stories, across every edition.' : 'Stories from your sources, reviewed for your interests.';
        await this.feed.load();
      }
    } catch (error) { notify(error.message, true); }
  }

  async collect(kind, button, uncheckedOnly = false) {
    const previousRun = this.overview?.runs[0]?.id || 0;
    await busy(button, async () => {
      try {
        await api('jobs/start', 'POST', { kind, unchecked_only: uncheckedOnly });
        if (kind === 'collect') this.unconfirmedRefresh = null;
        notify(kind === 'collect' ? uncheckedOnly ? 'Checking new sources in the background.' : 'Collection started in the background.' : 'Reviewing queued stories.');
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
    } catch (error) { if (!error.connection) notify(error.message, true); }
    finally { this.poll(); }
  }
}

const app = new App();
app.init();
