import { syncDropdowns } from './dropdowns.js';
import { $, api, busy, el, externalLink, formatTime, notify, $$ } from './shared.js';

/** Sizes like TaskList's and SpendTracker's Fresh Start: bytes, KB, MB, or GB. */
function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} bytes`;
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

/**
 * Turns an edited one-per-line list into explicit renames and removals. Unchanged lines anchor the diff; between
 * anchors, removed and added lines pair up in order as renames (a line edited in place). A name present in both
 * lists is never removed, so moving a line cannot drop its labels.
 */
export function topicEdits(before, after) {
  const lcs = Array.from({ length: before.length + 1 }, () => new Array(after.length + 1).fill(0));
  for (let i = before.length - 1; i >= 0; i--)
    for (let j = after.length - 1; j >= 0; j--)
      lcs[i][j] = before[i] === after[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
  const kept = new Set(after), existing = new Set(before);
  const renames = {}, removed = [];
  let gone = [], added = [];
  const flush = () => {
    added = added.filter(name => !existing.has(name));
    gone.filter(name => !kept.has(name)).forEach((name, index) => {
      if (index < added.length) renames[name] = added[index];
      else removed.push(name);
    });
    gone = [];
    added = [];
  };
  let i = 0, j = 0;
  while (i < before.length || j < after.length) {
    if (i < before.length && j < after.length && before[i] === after[j]) { flush(); i++; j++; }
    else if (j < after.length && (i === before.length || lcs[i][j + 1] >= lcs[i + 1][j])) added.push(after[j++]);
    else gone.push(before[i++]);
  }
  flush();
  return { renames, removed };
}

export class Settings {
  constructor(app) {
    this.app = app;
    this.values = null;
    this.sources = [];
    this.keyStatusSequence = 0;
    $$('.settings-tabs [data-tab]').forEach(button => { button.addEventListener('click', () => this.tab(button.dataset.tab)); });
    $('#topicsForm').addEventListener('submit', event => this.submit(event, () => this.saveTopics()));
    $('#topicRemoveForm').addEventListener('submit', event => this.submit(event, async () => {
      $('#topicRemoveDialog').close();
      await this.saveTopics(true);
    }));
    $('#addInterest').addEventListener('click', () => this.interestRow({ name: '', preference: 'favor' }));
    $('#interestsForm').addEventListener('submit', event => this.submit(event, async () => {
      const topics = [...$('#interestRows').children].map(row => ({ name: row.querySelector('input').value.trim(), preference: row.querySelector('select').value }));
      const interests = { topics, guidance: $('#guidance').value, exclusions: $('#exclusions').value.split('\n').map(x => x.trim()).filter(Boolean) };
      await api('settings', 'PUT', { interests });
      this.values.interests = interests;
      notify('Interests saved. Future reviews will use these preferences.');
    }));
    $('#aiForm').addEventListener('submit', event => this.submit(event, async () => {
      const ai = this.aiValues();
      await api('settings', 'PUT', { ai });
      this.values.ai = ai;
      notify(ai.model ? 'AI settings saved.' : 'AI review paused. Collection remains available.');
    }));
    $('#aiProvider').addEventListener('change', () => {
      const defaults = { ollama: 'http://127.0.0.1:11434', compatible: 'http://127.0.0.1:1234/v1', openrouter: 'https://openrouter.ai/api/v1' };
      if (Object.values(defaults).includes($('#aiUrl').value)) $('#aiUrl').value = defaults[$('#aiProvider').value];
      this.aiHelp();
      this.refreshKeyStatus(true);
    });
    $('#aiUrl').addEventListener('change', () => this.refreshKeyStatus(true));
    $('#modelKeyForm').addEventListener('submit', async event => {
      event.preventDefault();
      await busy(event.submitter, async () => {
        await api('model-key', 'PUT', { ...this.keyConfig(), key: $('#modelApiKey').value });
        $('#modelApiKey').value = '';
        notify('API key saved. Future model requests use it immediately.');
      }, 'Saving API key...');
      await this.refreshKeyStatus();
    });
    $('#modelKeyRemove').addEventListener('click', () => {
      this.removingKeyConfig = this.keyConfig();
      $('#modelKeyRemoveTarget').textContent = `${this.removingKeyConfig.provider === 'openrouter' ? 'OpenRouter' : 'Compatible server'} · ${this.removingKeyConfig.baseUrl}`;
      $('#modelKeyEnvironmentFallback').hidden = !this.keyStatus?.environment;
      $('#modelKeyRemoveDialog').showModal();
      $('#modelKeyRemoveDialog').querySelector('[data-close]').focus();
    });
    $('#modelKeyRemoveForm').addEventListener('submit', event => this.submit(event, async () => {
      await api('model-key', 'DELETE', { ...this.removingKeyConfig, confirmation: 'REMOVE MODEL KEY' });
      $('#modelKeyRemoveDialog').close();
      await this.refreshKeyStatus();
      notify('Saved API key removed. Future requests use the updated key configuration.');
    }));
    $('#testModel').addEventListener('click', () => busy($('#testModel'), async () => {
      $('#modelTestResult').hidden = false;
      $('#modelTestResult').textContent = 'Asking your server to review a fictional sample article...';
      try {
        const result = await api('test-model', 'POST', this.aiValues(), Number($('#aiTimeout').value) * 1000 + 15000);
        $('#modelTestResult').textContent = `Connection and output validation passed. Sample summary: ${result.summary}`;
      } catch (error) { $('#modelTestResult').textContent = error.message; throw error; }
    }));
    $('#collectionForm').addEventListener('submit', event => this.submit(event, async () => {
      const collection = { enabled: $('#scheduleEnabled').checked, intervalHours: Number($('#scheduleInterval').value),
        itemsPerSource: Number($('#itemsPerSource').value), maxReviews: Number($('#maxReviews').value), lookbackDays: Number($('#lookback').value) };
      await api('settings', 'PUT', { collection });
      this.values.collection = collection;
      notify('Collection settings saved.');
    }));
    $('#feedbackProfileForm').addEventListener('submit', event => this.submit(event, async () => {
      await api('feedback/profile', 'PUT', { profile: $('#feedbackProfile').value, enabled: $('#feedbackEnabled').checked, autoUpdate: $('#feedbackAuto').checked });
      notify('Learned preferences saved.');
      await this.loadFeedback();
    }));
    $('#feedbackRebuild').addEventListener('click', () => busy($('#feedbackRebuild'), async () => {
      await api('feedback/rebuild', 'POST', {}, Number(this.values.ai.timeoutSeconds) * 1000 + 15000);
      await this.loadFeedback();
      notify('Learned preferences updated from recent feedback.');
    }, 'Updating learned preferences...'));
    $('#feedbackReset').addEventListener('click', () => $('#feedbackResetDialog').showModal());
    $('#feedbackConfirmReset').addEventListener('click', () => busy($('#feedbackConfirmReset'), async () => {
      await api('fresh-start', 'POST', { confirmation: 'RESET SELECTED', selections: { feedback: true } });
      $('#feedbackResetDialog').close();
      await this.app.reloadDataset();
      notify('Article feedback and learned preferences were reset.');
    }));
    $('#retryFailed').addEventListener('click', () => busy($('#retryFailed'), async () => {
      const result = await api('requeue', 'POST', { failedOnly: true });
      notify(`${result.count} failed reviews reset. Use Review pending to process them.`);
      await this.app.refreshOverview();
    }));
    $('#reassess').addEventListener('click', () => {
      $('#confirmDialog').showModal();
      $('#confirmDialog').querySelector('[data-close]').focus();
    });
    $('#confirmReassess').addEventListener('click', () => busy($('#confirmReassess'), async () => {
      const result = await api('requeue', 'POST', { failedOnly: false });
      $('#confirmDialog').close();
      notify(`${result.count} stories queued. Use Review pending to process them.`);
      await this.app.refreshOverview();
    }));
    $('#purgeDuplicates').addEventListener('click', () => busy($('#purgeDuplicates'), async () => {
      const result = await api('maintenance/duplicates');
      $('#purgeDuplicatesCount').textContent = `${result.count} duplicate ${result.count === 1 ? 'story can' : 'stories can'} be removed.`;
      $('#confirmPurgeDuplicates').disabled = result.count === 0;
      $('#purgeDuplicatesDialog').showModal();
      $('#purgeDuplicatesDialog').querySelector('[data-close]').focus();
    }));
    $('#purgeDuplicatesForm').addEventListener('submit', async event => {
      event.preventDefault();
      await busy(event.submitter, async () => {
        const result = await api('maintenance/duplicates', 'DELETE', { confirmation: 'PURGE DUPLICATES' }, 120000);
        $('#purgeDuplicatesDialog').close();
        await this.app.reloadDataset();
        notify(result.warning || `${result.deleted} duplicate ${result.deleted === 1 ? 'story' : 'stories'} removed.${result.compacted ? ' Database compacted.' : ''} Bookmarks and preferences were kept.`, Boolean(result.warning));
      }, 'Purging duplicate stories and compacting the database...');
    });
    $('#clearHistory').addEventListener('click', async () => {
      $('#clearHistoryForm').reset();
      $('#clearHistoryError').textContent = '';
      $('#clearHistoryDialog').querySelectorAll('[data-fresh-count]').forEach(count => { count.textContent = ''; });
      $('#freshStartSize').textContent = 'Checking stored data...';
      $('#clearHistoryDialog').showModal();
      $('#clearHistoryDialog').querySelector('[data-close]').focus();
      // Counts and the database size, like TaskList's and SpendTracker's Fresh Start.
      try {
        const summary = await api('fresh-start');
        const plural = (count, one, many = `${one}s`) => `${count} ${count === 1 ? one : many}`;
        const counts = {
          history: plural(summary.history, 'story', 'stories'), bookmarks: plural(summary.bookmarks, 'bookmark'),
          runs: plural(summary.runs, 'run'), topics: plural(summary.topics, 'topic'), feedback: plural(summary.feedback, 'rating'),
          exclusions: plural(summary.exclusions, 'exclusion'), sources: plural(summary.sources, 'source'),
          recoveryBackups: `${plural(summary.recoveryBackups, 'file')}, ${formatBytes(summary.recoveryBackupBytes)}`,
        };
        for (const [key, text] of Object.entries(counts)) $('#clearHistoryDialog').querySelector(`[data-fresh-count="${key}"]`).textContent = `(${text})`;
        $('#freshStartSize').textContent = `Database size: ${formatBytes(summary.databaseBytes)}.`;
      } catch (error) { $('#freshStartSize').textContent = `Could not read stored data: ${error.message}`; }
    });
    $('#clearHistoryForm').addEventListener('submit', event => this.submit(event, async () => {
      try {
        const selections = Object.fromEntries([...$('#clearHistoryForm').querySelectorAll('.reset-options input')].map(input => [input.dataset.freshStart, input.checked]));
        const result = await api('fresh-start', 'POST', { confirmation: $('#clearHistoryConfirmation').value, selections });
        $('#clearHistoryDialog').close();
        await this.app.reloadDataset();
        notify(`Fresh Start: ${result.deletedStories} ${result.deletedStories === 1 ? 'story' : 'stories'} and ${result.removedBackups} recovery ${result.removedBackups === 1 ? 'backup' : 'backups'} removed; freed ${formatBytes(result.freedBytes)}. Unchecked items were kept.`);
      } catch (error) { $('#clearHistoryError').textContent = error.message; throw error; }
    }));
    $('#addSource').addEventListener('click', () => this.editSource());
    $('#checkNewSources').addEventListener('click', () => this.app.collect('collect', $('#checkNewSources'), { uncheckedOnly: true }));
    $('#refreshSources').addEventListener('click', () => this.app.collect('collect', $('#refreshSources')));
    $('#discoverFeed').addEventListener('click', () => busy($('#discoverFeed'), async () => {
      $('#sourceError').textContent = '';
      try {
        const result = await api('discover', 'POST', { url: $('#sourceUrl').value }, 90000);
        $('#sourceFeed').value = result.feedUrl;
        notify('Feed discovered. Save the source to keep it.');
      } catch (error) { $('#sourceError').textContent = error.message; throw error; }
    }));
    $('#previewSource').addEventListener('click', () => busy($('#previewSource'), async () => {
      $('#sourceError').textContent = '';
      $('#sourcePreview').hidden = false;
      $('#sourcePreview').textContent = 'Checking the source...';
      try {
        const result = await api('preview-source', 'POST', this.sourceValues(), 120000);
        if (result.feedUrl && !$('#sourceFeed').value) $('#sourceFeed').value = result.feedUrl;
        $('#sourcePreview').replaceChildren(el('strong', '', `Detected: ${({ page: 'Website page', feed: 'RSS/Atom', 'feed+page': 'RSS/Atom + website' })[result.method]} · ${result.entries.length} sample stories`));
        const list = el('ul');
        result.entries.forEach(story => { const item = el('li'); item.append(externalLink(story.url, story.headline)); list.append(item); });
        $('#sourcePreview').append(list);
        if (result.note) $('#sourcePreview').append(el('p', '', result.note));
      } catch (error) { $('#sourceError').textContent = error.message; $('#sourcePreview').hidden = true; throw error; }
    }));
    $('#sourceForm').addEventListener('submit', event => this.submit(event, async () => {
      const id = $('#sourceId').value;
      const source = this.sourceValues();
      try {
        await api(id ? `sources/${id}` : 'sources', id ? 'PATCH' : 'POST', source);
        $('#sourceDialog').close();
        await this.refreshSources();
        notify('Source saved.');
      } catch (error) { $('#sourceError').textContent = error.message; throw error; }
    }));
  }

  submit(event, work) { event.preventDefault(); return busy(event.submitter, work); }

  sourceValues() {
    return { name: $('#sourceName').value, url: $('#sourceUrl').value, feedUrl: $('#sourceFeed').value,
      category: $('#sourceCategory').value, enabled: $('#sourceEnabled').checked, mode: $('#sourceMode').value,
      linkPattern: $('#sourcePattern').value, staleDays: Number($('#sourceStaleDays').value) };
  }

  tab(name) {
    ['interests', 'topics', 'sources', 'ai', 'collection'].forEach(id => {
      $(`#panel${id[0].toUpperCase()}${id.slice(1)}`).hidden = name !== id;
      $(`.settings-tabs [data-tab="${id}"]`).classList.toggle('active', name === id);
    });
    if (name === 'sources' && this.values) this.refreshSources().catch(error => notify(error.message, true));
    if (name === 'topics' && this.values) this.loadTopics(true).catch(error => notify(error.message, true));
  }

  async load() {
    // Controls must not accept edits that an in-flight initial load would replace.
    $('#settingsFields').disabled = true;
    $('#settings').setAttribute('aria-busy', 'true');
    try {
      this.values = await api('settings');
      await this.refreshSources();
      await this.loadTopics();
      const { interests, ai, collection } = this.values;
      $('#interestRows').replaceChildren();
      interests.topics.forEach(topic => this.interestRow(topic));
      $('#guidance').value = interests.guidance;
      $('#exclusions').value = interests.exclusions.join('\n');
      await this.loadFeedback();
      $('#aiProvider').value = ai.provider;
      $('#aiUrl').value = ai.baseUrl;
      $('#aiModel').value = ai.model;
      $('#aiTimeout').value = ai.timeoutSeconds;
      $('#aiInput').value = ai.maxInputChars;
      $('#aiOutput').value = ai.maxOutputTokens ?? 2400;
      $('#aiUnload').checked = ai.unloadAfterUse ?? true;
      this.aiHelp();
      await this.refreshKeyStatus(true);
      $('#scheduleEnabled').checked = collection.enabled;
      $('#scheduleInterval').value = collection.intervalHours ?? 24;
      $('#itemsPerSource').value = collection.itemsPerSource;
      $('#maxReviews').value = collection.maxReviews;
      $('#lookback').value = collection.lookbackDays;
      this.renderRuns();
      syncDropdowns();
    } finally {
      $('#settingsFields').disabled = false;
      $('#settings').setAttribute('aria-busy', 'false');
    }
  }

  async loadTopics(keepEdits = false) { this.renderTopics(await api('topics'), keepEdits); }

  /** Unsaved edits in the text box survive tab switches; a save or initial load replaces them. */
  renderTopics(topics, keepEdits = false) {
    const edited = keepEdits && this.topics && $('#topicsText').value !== this.topics.join('\n');
    this.topics = topics;
    // Default category suggestions are the managed topics; a match becomes a new story's initial label.
    $('#sourceCategories').replaceChildren(...topics.map(name => el('option', '', name)));
    if (!edited) $('#topicsText').value = topics.join('\n');
  }

  async saveTopics(removalConfirmed = false) {
    const lines = $('#topicsText').value.split('\n').map(line => line.trim()).filter(Boolean);
    const { renames, removed } = topicEdits(this.topics, lines);
    if (removed.length && !removalConfirmed) {
      $('#topicRemoveNames').replaceChildren(...removed.map(name => el('li', '', name)));
      $('#topicRemoveDialog').showModal();
      $('#topicRemoveDialog').querySelector('[data-close]').focus();
      return;
    }
    const result = await api('topics', 'PUT', { topics: lines, renames, ...(removed.length ? { confirmation: 'DELETE TOPIC' } : {}) });
    const selected = this.app.feed.selectedTopics;
    for (const [from, to] of Object.entries(result.renamed)) if (selected.delete(from)) selected.add(to);
    result.removed.forEach(name => selected.delete(name));
    this.renderTopics(result.topics);
    notify(`Topics saved. Labels updated on ${result.updatedStories} stories; articles were kept.`);
    await this.app.refreshOverview();
    await this.app.feed.load();
  }

  interestRow(topic) {
    const row = el('div', 'interest-row');
    const input = el('input');
    input.value = topic.name;
    input.placeholder = 'e.g. science, local news, or open source';
    input.required = true;
    input.maxLength = 120;
    input.setAttribute('aria-label', 'Interest topic');
    const preference = el('select');
    [['favor', 'Favor'], ['normal', 'Normal'], ['less', 'Less'], ['exclude', 'Exclude']].forEach(([value, label]) => preference.add(new Option(label, value)));
    preference.value = topic.preference;
    preference.setAttribute('aria-label', 'Topic preference');
    const remove = el('button', '', 'Remove');
    remove.type = 'button';
    remove.setAttribute('aria-label', `Remove interest ${topic.name}`);
    remove.addEventListener('click', () => row.remove());
    row.append(input, preference, remove);
    $('#interestRows').append(row);
    syncDropdowns(row);
    if (!topic.name) input.focus();
  }

  async loadFeedback() {
    const value = await api('feedback');
    $('#feedbackEnabled').checked = value.enabled;
    $('#feedbackAuto').checked = value.autoUpdate;
    $('#feedbackProfile').value = value.profile;
    $('#feedbackRebuild').disabled = value.count === 0;
    $('#feedbackProfileStatus').textContent = `${value.count} saved ${value.count === 1 ? 'rating' : 'ratings'}. ${value.revision !== value.profileRevision ? 'New feedback is awaiting a profile update.' : 'Profile is current.'}`;
  }

  keyConfig() { return { provider: $('#aiProvider').value, baseUrl: $('#aiUrl').value }; }

  async refreshKeyStatus(clearInput = false) {
    const sequence = ++this.keyStatusSequence;
    if (clearInput) $('#modelApiKey').value = '';
    const config = this.keyConfig();
    $('#modelKeyPanel').hidden = config.provider === 'ollama';
    this.keyStatus = null;
    $('#modelKeyRemove').disabled = true;
    if (config.provider === 'ollama') return;
    $('#modelKeyStatus').textContent = 'Checking key configuration...';
    $('#modelKeySave').disabled = true;
    try {
      const status = await api('model-key/status', 'POST', config);
      if (sequence !== this.keyStatusSequence) return;
      this.keyStatus = status;
      $('#modelKeyStatus').textContent = status.source === 'saved' ? 'Key configured.' : status.source === 'environment' ? 'Key configured from the server environment.' : 'No key configured.';
      $('#modelKeySave').textContent = status.saved ? 'Replace Key' : 'Save Key';
      $('#modelKeyRemove').disabled = !status.saved;
    } catch (error) {
      if (sequence !== this.keyStatusSequence) return;
      $('#modelKeyStatus').textContent = 'Key status unavailable.';
      if (!error.connection) notify(error.message, true);
    } finally {
      if (sequence === this.keyStatusSequence) $('#modelKeySave').disabled = false;
    }
  }

  aiValues() {
    return { provider: $('#aiProvider').value, baseUrl: $('#aiUrl').value,
      model: $('#aiModel').value.trim(), timeoutSeconds: Number($('#aiTimeout').value), maxInputChars: Number($('#aiInput').value), maxOutputTokens: Number($('#aiOutput').value), unloadAfterUse: $('#aiUnload').checked };
  }

  aiHelp() {
    const provider = $('#aiProvider').value;
    $('#aiUnloadRow').hidden = provider !== 'ollama';
    $('#aiModel').placeholder = provider === 'openrouter' ? 'anthropic/claude-haiku-5.5' : 'Exact model name or server model ID';
    const help = {
      ollama: 'Ollama: use the server root, such as http://127.0.0.1:11434. Enter the exact installed model name. The app uses /api/chat with a JSON schema and thinking disabled.',
      compatible: 'Compatible server: include the API prefix, such as http://127.0.0.1:1234/v1 for LM Studio. The app uses /chat/completions and JSON output. Your model must follow the requested schema.',
      openrouter: 'OpenRouter: use https://openrouter.ai/api/v1 and a model ID such as anthropic/claude-haiku-5.5. Save your API key below. Thinking is disabled for reviews, sample tests, and preference updates. Article text and review preferences are sent to OpenRouter and its selected provider.',
    };
    $('#aiHelp').textContent = help[provider];
    if (provider !== 'openrouter') $('#aiHelp').append(document.createTextNode(' If the model runs on another computer, use its LAN address. The app backend must be able to reach it.'));
  }

  syncNewSources() {
    const running = Boolean(this.app.overview?.collector.running);
    const count = this.sources.filter(source => source.enabled && source.lastChecked === null).length;
    $('#checkNewSources').textContent = `Check New Sources (${count})`;
    $('#checkNewSources').disabled = running || count === 0;
    $$('#sourceList .check-source').forEach(button => { button.disabled = running || button.dataset.enabled !== 'true'; });
    $('#checkNewSources').title = count ? 'Check only enabled sources with no recorded check' : 'No enabled sources are awaiting their first check';
  }

  async refreshSources() {
    this.sources = await api('sources');
    $('#sourceList').replaceChildren(...this.sources.map(source => {
      const row = el('div', 'source-row');
      const info = el('div', 'source-info');
      const title = el('strong');
      title.append(externalLink(source.url, source.name));
      info.append(title, el('small', '', `${source.category} · ${source.mode === 'page' ? 'Website page' : source.feedUrl || 'Automatic feed / website discovery'}`));
      const status = source.health.status;
      const statusLabel = { healthy: 'Healthy', unchecked: 'Not checked', disabled: 'Disabled', broken: 'Broken', error: 'Check failed' }[status];
      if (statusLabel) info.append(el('span', `source-state ${status}`, statusLabel));
      source.health.warnings.forEach(warning => info.append(el('small', 'source-warning', warning)));
      if (source.lastChecked) info.append(el('small', '', `Checked ${formatTime(source.lastChecked)} · Last success ${formatTime(source.lastSuccess)} · ${source.lastCount ?? 'Unknown'} entries · ${source.lastMethod || 'Method unknown'}`));
      if (source.newestPublished) info.append(el('small', '', `Newest reported publication: ${formatTime(source.newestPublished)}`));
      const check = el('button', 'check-source', 'Check');
      check.setAttribute('aria-label', `Check ${source.name}`);
      check.title = source.enabled ? 'Check only this source now' : 'Enable the source to check it';
      check.dataset.enabled = String(Boolean(source.enabled));
      check.addEventListener('click', () => this.app.collect('collect', check, { sourceId: source.id, name: source.name }));
      const edit = el('button', '', 'Edit');
      edit.setAttribute('aria-label', `Edit ${source.name}`);
      edit.addEventListener('click', () => this.editSource(source));
      const toggle = el('button', '', source.enabled ? 'Disable' : 'Enable');
      toggle.setAttribute('aria-label', `${source.enabled ? 'Disable' : 'Enable'} ${source.name}`);
      toggle.addEventListener('click', () => busy(toggle, async () => {
        await api(`sources/${source.id}`, 'PATCH', { ...source, enabled: !source.enabled });
        await this.refreshSources();
      }));
      row.append(info, check, edit, toggle);
      return row;
    }));
    this.syncNewSources();
  }

  editSource(source = null) {
    $('#sourceDialogTitle').textContent = source ? 'Edit Source' : 'Add a Source';
    $('#sourceId').value = source?.id || '';
    $('#sourceName').value = source?.name || '';
    $('#sourceUrl').value = source?.url || '';
    $('#sourceFeed').value = source?.feedUrl || '';
    $('#sourceMode').value = source?.mode || 'auto';
    $('#sourcePattern').value = source?.linkPattern || '';
    $('#sourceStaleDays').value = source?.staleDays || 14;
    $('#sourcePreview').hidden = true;
    $('#sourceCategory').value = source?.category || $('#sourceCategories').options[0]?.value || 'News';
    $('#sourceEnabled').checked = source ? Boolean(source.enabled) : true;
    $('#sourceError').textContent = '';
    syncDropdowns($('#sourceDialog'));
    $('#sourceDialog').showModal();
    $('#sourceName').focus();
  }

  renderRuns() {
    if (!this.app.overview) return;
    const overview = this.app.overview;
    $('#scheduleTimezone').textContent = `Collects every configured number of hours after the last full collection starts. The server must be running; overdue work catches up after startup. Manual full collections restart the interval.`;
    const runs = overview.runs.map(run => {
      const details = el('details', 'run');
      details.append(el('summary', '', `${formatTime(run.startedAt)} · ${run.kind}${run.details.scope === 'unchecked' ? ' (new sources only)' : run.details.scope === 'source' ? ` (${run.details.source} only)` : ''} · ${run.status}`));
      const d = run.details;
      details.append(el('p', '', `${d.added || 0} added · ${d.reviewed || 0} reviewed · ${d.duplicates || 0} duplicates · ${d.filtered || 0} filtered`));
      if (d.note) details.append(el('p', '', d.note));
      if (d.errors?.length) {
        const errors = el('ul');
        d.errors.forEach(error => errors.append(el('li', '', error)));
        details.append(errors);
      }
      return details;
    });
    $('#runHistory').replaceChildren(...(runs.length ? runs : [el('p', 'muted', 'No runs yet. Check sources to collect your first edition.')]));
  }
}
