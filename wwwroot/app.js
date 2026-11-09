import { syncDropdowns } from './dropdowns.js';
import { Feed } from './feed.js';
import { Settings } from './settings.js';
import { Transfer } from './transfer.js';

import { $, el, api, busy, notify, formatTime, setConnectionStatus, setDefaultStatus, dismissStatus, closeMenus, toggleMenu, menuItem, menuSeparator, parseTypedDate, typedDate, $$ } from './shared.js';

/** The release from /version.json, like the other apps; the server's version if that file can't be read. */
async function releaseVersion(fallback) {
  try {
    const response = await fetch('/version.json', { cache: 'no-store', credentials: 'same-origin' });
    if (response.ok) return (await response.json()).version || fallback;
  } catch {}
  return fallback;
}

// Buttons marked data-close="dialogId" close that dialog; the same code in every app.
document.addEventListener('click', event => {
  const button = event.target.closest('[data-close]');
  if (button && !button.disabled) document.getElementById(button.dataset.close)?.close(button.value || 'cancel');
});

// Touch screens have no hover, so tapping an underlined label shows its definition, like the other apps.
const helpTooltip = Object.assign(document.createElement('div'), { className: 'help-tooltip', hidden: true });
document.body.append(helpTooltip);
let helpShownFor = null;
document.addEventListener('click', event => {
  const target = event.target.closest?.('.has-tooltip[title]');
  if (!target) {
    // Tapping label text also clicks the label's own field; that second click keeps the definition showing.
    if (!helpTooltip.hidden && helpShownFor && event.target.closest?.('label')?.contains(helpShownFor)) return;
    helpTooltip.hidden = true;
    return;
  }
  helpShownFor = target;
  helpTooltip.textContent = target.getAttribute('title');
  helpTooltip.hidden = false;
  const rect = target.getBoundingClientRect();
  helpTooltip.style.left = `${Math.max(8, Math.min(window.innerWidth - helpTooltip.offsetWidth - 8, rect.left))}px`;
  helpTooltip.style.top = `${rect.bottom + 8 + helpTooltip.offsetHeight > window.innerHeight ? rect.top - helpTooltip.offsetHeight - 8 : rect.bottom + 8}px`;
}, true);

class App {
  constructor() {
    this.overview = null;
    this.view = 'daily';
    this.authenticated = false;
    this.feed = new Feed(this);
    this.settings = new Settings(this);
    this.transfer = new Transfer(this);
    this.pollTimer = null;
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
    // Like the other apps, coming back to the tab does not reload; File > Refresh or a page reload does.
    // Hidden tabs pause job-progress and reconnect checks; showing the tab again only resumes those.
    document.addEventListener('visibilitychange', () => this.poll());
    // File / View / Help like the other apps; Daily Feed, Bookmarks, and Settings are tabs below the toolbar.
    $('#fileMenuButton').addEventListener('click', event => { event.stopPropagation(); toggleMenu($('#fileMenuButton'), $('#fileMenu')); });
    $('#viewMenuButton').addEventListener('click', event => { event.stopPropagation(); this.populateViewMenu(); toggleMenu($('#viewMenuButton'), $('#viewMenu')); });
    document.addEventListener('click', closeMenus);
    // Escape closes an open menu first, without also closing a dialog.
    document.addEventListener('keydown', event => {
      if (event.key !== 'Escape' || !$('.menu-dropdown:not([hidden])')) return;
      event.preventDefault();
      closeMenus();
    }, true);
    $('#refreshMenuItem').addEventListener('click', () => { closeMenus(); this.syncView(true); });
    $('#firstRunImport').addEventListener('click', () => this.dismissImportReminder(true));
    $('#firstRunContinue').addEventListener('click', () => this.dismissImportReminder(false));
    $('#firstRunDialog').addEventListener('cancel', event => { event.preventDefault(); this.dismissImportReminder(false); });
    $$('.tabs [role="tab"]').forEach(tab => { tab.addEventListener('click', () => this.show(tab.dataset.tab)); });
    $('#dismissStatus').addEventListener('click', dismissStatus);
    $('#helpNav').addEventListener('click', () => { closeMenus(); $('#helpDialog').showModal(); });
    $('#collect').addEventListener('click', () => this.collect('collect', $('#collect')));
    $('#reviewPending').addEventListener('click', () => this.overview?.collector.reviewing ? this.stopReview() : this.collect('review', $('#reviewPending')));
    $('#logout').addEventListener('click', async () => {
      closeMenus();
      try {
        await api('auth/logout', 'POST', {});
        this.authenticated = false;
        clearTimeout(this.pollTimer);
        $('#stories').replaceChildren();
        $('#settings').hidden = true;
        $('#reader').hidden = false;
        location.replace('/login.html');
      } catch (error) { notify(error.message, true); }
    });
    try {
      const session = await api('auth/status');
      if (!session.authenticated) { location.replace('/login.html'); return; }
      await this.load();
    } catch (error) { notify(error.message, true); setDefaultStatus(error.message); this.poll(); }
  }

  async load() {
    this.authenticated = true;
    await this.refreshOverview();
    if (!parseTypedDate($('#day').value)) $('#day').value = typedDate(this.overview.today);
    await this.feed.load();
    this.poll();
    await this.showImportReminder();
  }

  /** Answers the first-start box: Import/Export... (openImport) or Start Fresh. */
  async dismissImportReminder(openImport) {
    const buttons = [$('#firstRunImport'), $('#firstRunContinue')];
    buttons.forEach(button => { button.disabled = true; });
    try {
      await api('onboarding/acknowledge', 'POST', {});
      $('#firstRunDialog').close();
      if (openImport) $('#transferNav').click();
    } catch (error) {
      $('#firstRunError').textContent = error.message;
    } finally {
      buttons.forEach(button => { button.disabled = false; });
    }
  }

  /** "Restore a Backup Before Starting" on a brand-new database, the same box as TaskList's. */
  async showImportReminder() {
    try {
      if (!(await api('onboarding')).importReminderPending) return;
    } catch { return; }
    $('#firstRunError').textContent = '';
    $('#firstRunDialog').showModal();
    $('#firstRunImport').focus();
  }

  async reloadDataset() {
    // Reset/restore must invalidate pending feed reads before replacing cached rows.
    this.feed.requestId++;
    $('#stories').replaceChildren();
    await this.refreshOverview();
    this.feed.clearFilters();
    this.feed.page = 1;
    await this.settings.load();
    await this.feed.load();
  }

  async refreshOverview() {
    this.overview = await api('overview');
    this.release ??= await releaseVersion(this.overview.version);
    $('#version').textContent = `v${this.release}`;
    // The version is its own span so phones can hide it from the title bar.
    $('.titlebar-name').replaceChildren('NewsReview', Object.assign(document.createElement('span'), { className: 'title-version', textContent: ` v${this.release}` }));
    $('#helpVersion').textContent = `NewsReview v${this.release}`;
    const state = this.overview.collector;
    $('.status-dot').classList.toggle('collecting', state.running);
    $('footer').setAttribute('aria-busy', String(state.running));
    const pending = this.overview.counts.pending || 0;
    setDefaultStatus(state.running ? state.message : `${state.message} · ${pending} awaiting review`);
    $('#collect').disabled = state.running;
    $('#reviewPending').textContent = state.reviewing ? state.reviewStopRequested ? 'Stopping Review...' : 'Stop Review' : 'Review Pending';
    $('#reviewPending').classList.toggle('danger', Boolean(state.reviewing));
    $('#reviewPending').title = state.reviewing ? 'Finish the current article, then stop. Remaining stories stay queued.' : 'Review queued stories with your configured model';
    $('#reviewPending').disabled = state.reviewing ? Boolean(state.reviewStopRequested) : state.running || pending === 0;
    this.feed.renderTopics(this.overview.tags);
    $('#clearHistory').disabled = state.running;
    $('#refreshSources').disabled = state.running;
    this.settings.syncNewSources();
    this.transfer.sync();
    const last = this.overview.runs.find(run => run.kind === 'collect');
    let refreshStatus = state.running ? state.message : last
      ? `${state.message} · Last collection: ${last.status} · Started ${formatTime(last.startedAt)} · ${last.details.errors?.length || 0} errors. See each source and run history for details.`
      : state.message;
    if (this.unconfirmedRefresh !== null) {
      if (state.running || (last && last.id > this.unconfirmedRefresh)) this.unconfirmedRefresh = null;
      else refreshStatus = `No newer collection was recorded. Press Refresh Sources to retry. ${refreshStatus}`;
    }
    $('#sourceRefreshStatus').textContent = refreshStatus;
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
        else if (changed || (force && !$('#panelSources').hidden) || this.overview.collector.running)
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
    $$('.tabs [role="tab"]').forEach(tab => tab.setAttribute('aria-selected', String(tab.dataset.tab === view)));
    $('#settings').hidden = view !== 'settings';
    $('#reader').hidden = view === 'settings';
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
    // A heading over each group, like TaskList's View menu.
    const tabsHeading = el('div', 'menu-heading', 'Tabs');
    tabsHeading.setAttribute('role', 'presentation');
    $('#viewMenu').replaceChildren(tabsHeading,
      ...[...$$('.tabs [role="tab"]')].map(tab =>
        menuItem(tab.textContent, { role: 'menuitemradio', checked: tab.dataset.tab === this.view, onClick: () => this.show(tab.dataset.tab) })),
      menuSeparator(), heading,
      ...[['light', 'Light'], ['dark', 'Dark']].map(([value, label]) =>
        menuItem(label, { role: 'menuitemradio', checked: window.AppTheme.getPreference() === value, onClick: () => window.AppTheme.setPreference(value) })),
      menuSeparator(),
      menuItem('Use Retro Font', { role: 'menuitemcheckbox', checked: window.AppTheme.getRetroFont(), onClick: () => window.AppTheme.setRetroFont(!window.AppTheme.getRetroFont()) }));
  }

  async stopReview() {
    await busy($('#reviewPending'), async () => {
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
          $('#sourceRefreshStatus').textContent = 'Refresh request was not confirmed. Reconnecting to check whether the server started it.';
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
