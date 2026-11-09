import { $, notify, revisionHeaders } from './shared.js';

const MAX_UPLOAD = 256 * 1024 * 1024;

async function errorMessage(response) {
  try { return (await response.json()).error || `Request failed (HTTP ${response.status})`; }
  catch { return `NewsReview returned an unexpected response (HTTP ${response.status}). Check the server connection.`; }
}

/** File → Import/Export...: Download Backup, and Choose File to Restore... confirmed in a second dialog, like the other apps. */
export class Transfer {
  constructor(app) {
    this.app = app;
    this.busy = false;
    this.file = null;
    $('transfer-nav').onclick = () => {
      if (!app.authenticated) return;
      this.sync();
      $('transfer-dialog').showModal();
    };
    $('choose-backup').onclick = () => {
      $('backup-file').value = '';
      $('backup-file').click();
    };
    $('backup-file').onchange = () => {
      const file = $('backup-file').files[0];
      if (!file) return;
      if (!file.name.toLowerCase().endsWith('.zip') || file.size > MAX_UPLOAD) {
        notify('Choose a NewsReview backup ZIP no larger than 256 MiB.', true);
        return;
      }
      this.file = file;
      $('transfer-dialog').close();
      $('restore-file-name').textContent = file.name;
      $('restore-dialog').showModal();
    };
    $('restore-form').onsubmit = event => {
      event.preventDefault();
      $('restore-dialog').close();
      this.restore(this.file);
    };
    $('export-backup').onclick = () => this.export();
    $('transfer-dialog').addEventListener('cancel', event => { if (this.busy) event.preventDefault(); });
  }

  /** Restoring waits for a running collection to finish. */
  sync() {
    $('choose-backup').disabled = this.busy || Boolean(this.app.overview?.collector.running);
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
      const response = await fetch('/api/backup', { credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok || !response.headers.get('Content-Type')?.startsWith('application/zip')) throw new Error(await errorMessage(response));
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement('a');
      link.href = url;
      link.download = /filename="([^"]+)"/.exec(response.headers.get('Content-Disposition') || '')?.[1] || 'newsreview-backup.zip';
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
      notify('Backup download started.');
    } catch (error) {
      notify(`Backup error: ${error.message}`, true);
    } finally { this.lock(false); }
  }

  async restore(file) {
    if (this.busy || !file) return;
    this.lock(true);
    notify('Validating the ZIP and restoring data. A recovery backup is saved first...');
    let restored = false;
    try {
      const response = await fetch('/api/restore', { method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/zip', 'X-NewsReview-Confirm': 'RESTORE', ...revisionHeaders() }, body: file });
      if (!response.ok) throw new Error(await errorMessage(response));
      const result = await response.json();
      if (!result.restored) throw new Error('The server did not confirm the restore.');
      restored = true;
      await this.app.reloadDataset();
      notify(`Restored ${result.totals.stories} stories and ${result.totals.sources} source records. Recovery backup: data/backups/${result.recoveryBackup}`);
    } catch (error) {
      let message = restored ? `Backup restored, but the display could not be refreshed: ${error.message}` : `Restore could not be confirmed: ${error.message}`;
      try { await this.app.reloadDataset(); message += ' Current server data has been reloaded.'; }
      catch { $('stories').replaceChildren(); message += ' Could not reload server data; reconnect before continuing.'; }
      notify(message, true);
    } finally {
      this.file = null;
      this.lock(false);
    }
  }
}
