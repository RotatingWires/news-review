import { $, notify, revisionHeaders } from './shared.js';

const MAX_UPLOAD = 256 * 1024 * 1024;

async function errorMessage(response) {
  try { return (await response.json()).error || `Request failed (HTTP ${response.status})`; }
  catch { return `NewsReview returned an unexpected response (HTTP ${response.status}). Check the server connection.`; }
}

export class Transfer {
  constructor(app) {
    this.app = app;
    this.busy = false;
    $('transfer-nav').onclick = () => {
      if (!app.authenticated) return;
      $('backup-file').value = '';
      $('confirm-restore').checked = false;
      $('backup-file-info').textContent = 'Maximum ZIP size: 256 MiB. Maximum database size: 1 GiB.';
      this.sync();
      $('transfer-dialog').showModal();
    };
    $('choose-backup').onclick = () => $('backup-file').click();
    $('backup-file').onchange = () => {
      const file = $('backup-file').files[0];
      $('confirm-restore').checked = false;
      $('backup-file-info').textContent = file ? `${file.name} (${(file.size / 1048576).toFixed(2)} MiB)` : 'Choose a NewsReview export ZIP.';
      this.sync();
    };
    $('confirm-restore').onchange = () => this.sync();
    $('export-backup').onclick = () => this.export();
    $('restore-backup').onclick = () => this.restore();
    $('transfer-dialog').addEventListener('cancel', event => { if (this.busy) event.preventDefault(); });
  }

  sync() {
    $('restore-backup').disabled = this.busy || this.app.overview?.collector.running || !$('backup-file').files.length || !$('confirm-restore').checked;
  }

  lock(value) {
    this.busy = value;
    document.querySelector('.window').inert = value;
    $('transfer-dialog').querySelectorAll('button,input').forEach(control => { control.disabled = value; });
    this.sync();
    this.app.poll();
  }

  async export() {
    this.lock(true);
    notify('Preparing a consistent ZIP backup...');
    try {
      const response = await fetch('/api/export', { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok || !response.headers.get('Content-Type')?.startsWith('application/zip')) throw new Error(await errorMessage(response));
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement('a');
      link.href = url;
      link.download = /filename="([^"]+)"/.exec(response.headers.get('Content-Disposition') || '')?.[1] || 'newsreview-backup.zip';
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      notify('Export download started.');
    } catch (error) {
      notify(`Export error: ${error.message}`, true);
    } finally { this.lock(false); }
  }

  async reloadDataset() {
    // Never leave cached rows presented as confirmed data after an attempted restore.
    this.app.feed.requestId++;
    $('stories').replaceChildren();
    await this.app.refreshOverview();
    this.app.feed.clearFilters();
    this.app.feed.page = 1;
    await this.app.settings.load();
    await this.app.feed.load();
  }

  async restore() {
    const file = $('backup-file').files[0];
    if (this.busy || !file || !$('confirm-restore').checked) return;
    if (!file.name.toLowerCase().endsWith('.zip') || file.size > MAX_UPLOAD) {
      notify('Choose a NewsReview ZIP no larger than 256 MiB.', true);
      return;
    }
    this.lock(true);
    notify('Validating the ZIP and restoring data. A recovery backup is saved first...');
    let restored = false;
    try {
      const response = await fetch('/api/import/backup', { method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/zip', 'X-NewsReview-Confirm': 'RESTORE', ...revisionHeaders() }, body: file });
      if (!response.ok) throw new Error(await errorMessage(response));
      const result = await response.json();
      if (!result.restored) throw new Error('The server did not confirm the restore.');
      restored = true;
      await this.reloadDataset();
      notify(`Restored ${result.totals.stories} stories and ${result.totals.sources} source records. Recovery backup: data/backups/${result.recoveryBackup}`);
      $('confirm-restore').checked = false;
    } catch (error) {
      let message = restored ? `Backup restored, but the display could not be refreshed: ${error.message}` : `Restore could not be confirmed: ${error.message}`;
      try { await this.reloadDataset(); message += ' Current server data has been reloaded.'; }
      catch { $('stories').replaceChildren(); message += ' Could not reload server data; reconnect before continuing.'; }
      notify(message, true);
    } finally { this.lock(false); }
  }
}
