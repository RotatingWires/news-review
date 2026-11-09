import { syncDropdowns } from './dropdowns.js';
import { Feed } from './feed.js';
import { Settings } from './settings.js';
import { Transfer } from './transfer.js';

import { $, el, api, busy, notify, formatTime, setConnectionStatus, setDefaultStatus, dismissStatus, closeMenus, toggleMenu, menuItem, menuSeparator } from './shared.js';

/** The release from /version.json, like the other apps; the server's version if that file can't be read. */
async function releaseVersion(fallback) {
  try {
    const response = await fetch('/version.json', { cache: 'no-store', credentials: 'same-origin' });
    if (response.ok) return (await response.json()).version || fallback;
  } catch {}
  return fallback;
}

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
    document.querySelectorAll('[data-close]').forEach(button => { button.onclick = () => $(button.dataset.close).close(); });
    // File / View / Help like the other apps; Daily Feed, Bookmarks, and Settings are tabs below the toolbar.
    $('file-menu-button').onclick = event => { event.stopPropagation(); toggleMenu($('file-menu-button'), $('file-menu')); };
    $('view-menu-button').onclick = event => { event.stopPropagation(); this.populateViewMenu(); toggleMenu($('view-menu-button'), $('view-menu')); };
    document.addEventListener('click', closeMenus);
    // Escape closes an open menu first, without also closing a dialog.
    document.addEventListener('keydown', event => {
      if (event.key !== 'Escape' || !document.querySelector('.menu-dropdown:not([hidden])')) return;
      event.preventDefault();
      closeMenus();
    }, true);
    $('refresh-menu-item').onclick = () => { closeMenus(); this.syncView(true); };
    document.querySelectorAll('.tabs [role="tab"]').forEach(tab => { tab.onclick = () => this.show(tab.dataset.tab); });
    $('dismiss-status').onclick = dismissStatus;
    $('help-nav').onclick = () => { closeMenus(); $('help-dialog').showModal(); };
    $('collect').onclick = () => this.collect('collect', $('collect'));
    $('review-pending').onclick = () => this.overview?.collector.reviewing ? this.stopReview() : this.collect('review', $('review-pending'));
    $('logout').onclick = async () => {
      closeMenus();
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
    await this.showImportReminder();
  }

  /** "Restore a Backup Before Starting" on a brand-new database, the same box as TaskList's. */
  async showImportReminder() {
    try {
      if (!(await api('onboarding')).importReminderPending) return;
    } catch { return; }
    $('first-run-error').textContent = '';
    const dismiss = async openImport => {
      const buttons = [$('first-run-import'), $('first-run-continue')];
      buttons.forEach(button => { button.disabled = true; });
      try {
        await api('onboarding/acknowledge', 'POST', {});
        $('first-run-dialog').close();
        if (openImport) $('transfer-nav').click();
      } catch (error) {
        $('first-run-error').textContent = error.message;
      } finally {
        buttons.forEach(button => { button.disabled = false; });
      }
    };
    $('first-run-import').onclick = () => dismiss(true);
    $('first-run-continue').onclick = () => dismiss(false);
    $('first-run-dialog').oncancel = event => { event.preventDefault(); dismiss(false); };
    $('first-run-dialog').showModal();
    $('first-run-import').focus();
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
    this.release ??= await releaseVersion(this.overview.version);
    $('version').textContent = 'v' + this.release;
    // The version is its own span so phones can hide it from the title bar.
    document.querySelector('.titlebar-name').replaceChildren('NewsReview', Object.assign(document.createElement('span'), { className: 'title-version', textContent: ` v${this.release}` }));
    $('help-version').textContent = 'NewsReview v' + this.release;
    const state = this.overview.collector;
    document.querySelector('.status-dot').classList.toggle('collecting', state.running);
    document.querySelector('footer').setAttribute('aria-busy', String(state.running));
    const pending = this.overview.counts.pending || 0;
    setDefaultStatus(state.running ? state.message : `${state.message} · ${pending} awaiting review`);
    $('collect').disabled = state.running;
    $('review-pending').textContent = state.reviewing ? state.reviewStopRequested ? 'Stopping Review...' : 'Stop Review' : 'Review Pending';
    $('review-pending').classList.toggle('danger', Boolean(state.reviewing));
    $('review-pending').title = state.reviewing ? 'Finish the current article, then stop. Remaining stories stay queued.' : 'Review queued stories with your configured model';
    $('review-pending').disabled = state.reviewing ? Boolean(state.reviewStopRequested) : state.running || pending === 0;
    this.feed.renderTopics(this.overview.tags);
    $('clear-history').disabled = state.running;
    $('refresh-sources').disabled = state.running;
    this.settings.syncNewSources();
    this.transfer.sync();
    const last = this.overview.runs.find(run => run.kind === 'collect');
    let refreshStatus = state.running ? state.message : last
      ? `${state.message} · Last collection: ${last.status} · Started ${formatTime(last.startedAt)} · ${last.details.errors?.length || 0} errors. See each source and run history for details.`
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
    document.querySelectorAll('.tabs [role="tab"]').forEach(tab => tab.setAttribute('aria-selected', String(tab.dataset.tab === view)));
    $('settings').hidden = view !== 'settings';
    $('reader').hidden = view === 'settings';
    try {
      await this.refreshOverview();
      if (view === 'settings') await this.settings.load();
      else {
        this.feed.page = 1;
        await this.feed.load();
      }
    } catch (error) { notify(error.message, true); }
  }

  /** View lists the tabs, then the theme, like SpendTracker's View menu. */
  populateViewMenu() {
    const heading = el('div', 'menu-heading', 'Theme');
    heading.setAttribute('role', 'presentation');
    $('view-menu').replaceChildren(
      ...[...document.querySelectorAll('.tabs [role="tab"]')].map(tab =>
        menuItem(tab.textContent, { role: 'menuitemradio', checked: tab.dataset.tab === this.view, onClick: () => this.show(tab.dataset.tab) })),
      menuSeparator(), heading,
      ...[['light', 'Light'], ['dark', 'Dark']].map(([value, label]) =>
        menuItem(label, { role: 'menuitemradio', checked: window.AppTheme.getPreference() === value, onClick: () => window.AppTheme.setPreference(value) })),
      menuSeparator(),
      menuItem('Use Retro Font', { role: 'menuitemcheckbox', checked: window.AppTheme.getRetroFont(), onClick: () => window.AppTheme.setRetroFont(!window.AppTheme.getRetroFont()) }));
  }

  async stopReview() {
    await busy($('review-pending'), async () => {
      await api('jobs/stop-review', 'POST', {});
    }, 'Stopping AI review after the current article; remaining stories stay queued.');
    try { await this.refreshOverview(); }
    catch (error) { notify(error.message, true); }
  }

  /** scope: { uncheckedOnly } for never-checked sources, or { sourceId, name } for one source. */
  async collect(kind, button, { uncheckedOnly = false, sourceId = null, name = '' } = {}) {
    const previousRun = this.overview?.runs[0]?.id || 0;
    await busy(button, async () => {
      try {
        await api('jobs/start', 'POST', { kind, uncheckedOnly, ...(sourceId ? { sourceId } : {}) });
        if (kind === 'collect') this.unconfirmedRefresh = null;
        notify(kind !== 'collect' ? 'Reviewing queued stories.' : sourceId ? `Checking ${name} in the background.`
          : uncheckedOnly ? 'Checking new sources in the background.' : 'Collection started in the background.');
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
