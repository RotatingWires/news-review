import { $, api, busy, el, externalLink, formatTime, toast } from './shared.js';

export class Settings {
  constructor(app) {
    this.app = app;
    this.values = null;
    this.sources = [];
    document.querySelectorAll('[data-tab]').forEach(button => { button.onclick = () => this.tab(button.dataset.tab); });
    $('add-interest').onclick = () => this.interestRow({ name: '', preference: 'favor' });
    $('interests-form').onsubmit = event => this.submit(event, async () => {
      const topics = [...$('interest-rows').children].map(row => ({ name: row.querySelector('input').value.trim(), preference: row.querySelector('select').value }));
      const interests = { topics, guidance: $('guidance').value, exclusions: $('exclusions').value.split('\n').map(x => x.trim()).filter(Boolean) };
      await api('settings', 'PUT', { interests });
      this.values.interests = interests;
      toast('Interests saved. Future reviews will use these preferences.');
    });
    $('ai-form').onsubmit = event => this.submit(event, async () => {
      const ai = this.aiValues();
      await api('settings', 'PUT', { ai });
      this.values.ai = ai;
      toast(ai.model ? 'Local AI settings saved.' : 'AI review paused. Collection remains available.');
    });
    $('ai-provider').onchange = () => {
      const compatible = $('ai-provider').value === 'compatible';
      if (['http://127.0.0.1:11434', 'http://127.0.0.1:1234/v1'].includes($('ai-url').value)) $('ai-url').value = compatible ? 'http://127.0.0.1:1234/v1' : 'http://127.0.0.1:11434';
      this.aiHelp();
    };
    $('test-model').onclick = () => busy($('test-model'), async () => {
      $('model-test-result').hidden = false;
      $('model-test-result').textContent = 'Asking your server to review a fictional sample article…';
      try {
        const result = await api('test-model', 'POST', this.aiValues());
        $('model-test-result').textContent = `Connection and output validation passed. Sample summary: ${result.summary}`;
      } catch (error) { $('model-test-result').textContent = error.message; throw error; }
    });
    $('collection-form').onsubmit = event => this.submit(event, async () => {
      const [hour, minute] = $('schedule-time').value.split(':').map(Number);
      const collection = { enabled: $('schedule-enabled').checked, hour, minute,
        items_per_source: Number($('items-per-source').value), max_reviews: Number($('max-reviews').value), lookback_days: Number($('lookback').value) };
      await api('settings', 'PUT', { collection });
      this.values.collection = collection;
      toast('Collection settings saved.');
    });
    $('retry-failed').onclick = () => busy($('retry-failed'), async () => {
      const result = await api('requeue', 'POST', { failed_only: true });
      toast(`${result.count} failed reviews reset. Use Review pending to process them.`);
      await this.app.refreshOverview();
    });
    $('reassess').onclick = () => {
      $('confirm-dialog').showModal();
      $('confirm-dialog').querySelector('[data-close]').focus();
    };
    $('confirm-reassess').onclick = () => busy($('confirm-reassess'), async () => {
      const result = await api('requeue', 'POST', { failed_only: false });
      $('confirm-dialog').close();
      toast(`${result.count} stories queued. Use Review pending to process them.`);
      await this.app.refreshOverview();
    });
    $('clear-history').onclick = () => {
      $('clear-history-confirmation').value = '';
      $('clear-history-error').textContent = '';
      $('clear-history-dialog').showModal();
      $('clear-history-dialog').querySelector('[data-close]').focus();
    };
    $('clear-history-form').onsubmit = event => this.submit(event, async () => {
      try {
        const result = await api('history/clear', 'POST', { confirmation: $('clear-history-confirmation').value });
        $('clear-history-dialog').close();
        this.app.feed.clearFilters();
        this.app.feed.page = 1;
        $('stories').replaceChildren();
        await this.app.refreshOverview();
        await this.refreshSources();
        await this.app.feed.load();
        toast(`History cleared (${result.deleted} stories). Your preferences and sources were kept.`);
      } catch (error) { $('clear-history-error').textContent = error.message; throw error; }
    });
    $('add-source').onclick = () => this.editSource();
    $('discover-feed').onclick = () => busy($('discover-feed'), async () => {
      $('source-error').textContent = '';
      try {
        const result = await api('discover', 'POST', { url: $('source-url').value });
        $('source-feed').value = result.feed_url;
        toast('Feed discovered. Save the source to keep it.');
      } catch (error) { $('source-error').textContent = error.message; throw error; }
    });
    $('source-form').onsubmit = event => this.submit(event, async () => {
      const id = $('source-id').value;
      const source = { name: $('source-name').value, url: $('source-url').value,
        feed_url: $('source-feed').value, category: $('source-category').value, enabled: $('source-enabled').checked };
      try {
        await api(id ? 'sources/' + id : 'sources', id ? 'PUT' : 'POST', source);
        $('source-dialog').close();
        await this.refreshSources();
        toast('Source saved.');
      } catch (error) { $('source-error').textContent = error.message; throw error; }
    });
  }

  submit(event, work) { event.preventDefault(); return busy(event.submitter, work); }

  tab(name) {
    ['interests', 'sources', 'ai', 'collection'].forEach(id => {
      $('panel-' + id).hidden = name !== id;
      document.querySelector(`[data-tab="${id}"]`).classList.toggle('active', name === id);
    });
  }

  async load() {
    // Controls must not accept edits that an in-flight initial load would replace.
    $('settings-fields').disabled = true;
    $('settings').setAttribute('aria-busy', 'true');
    try {
      this.values = await api('settings');
      await this.refreshSources();
      const { interests, ai, collection } = this.values;
      $('interest-rows').replaceChildren();
      interests.topics.forEach(topic => this.interestRow(topic));
      $('guidance').value = interests.guidance;
      $('exclusions').value = interests.exclusions.join('\n');
      $('ai-provider').value = ai.provider;
      $('ai-url').value = ai.base_url;
      $('ai-model').value = ai.model;
      $('ai-timeout').value = ai.timeout_seconds;
      $('ai-input').value = ai.max_input_chars;
      this.aiHelp();
      $('schedule-enabled').checked = collection.enabled;
      $('schedule-time').value = `${String(collection.hour).padStart(2, '0')}:${String(collection.minute).padStart(2, '0')}`;
      $('items-per-source').value = collection.items_per_source;
      $('max-reviews').value = collection.max_reviews;
      $('lookback').value = collection.lookback_days;
      this.renderRuns();
    } finally {
      $('settings-fields').disabled = false;
      $('settings').setAttribute('aria-busy', 'false');
    }
  }

  interestRow(topic) {
    const row = el('div', 'interest-row');
    const input = el('input');
    input.value = topic.name;
    input.placeholder = 'e.g. science, local news, or radio projects';
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
    remove.onclick = () => row.remove();
    row.append(input, preference, remove);
    $('interest-rows').append(row);
    if (!topic.name) input.focus();
  }

  aiValues() {
    return { provider: $('ai-provider').value, base_url: $('ai-url').value,
      model: $('ai-model').value.trim(), timeout_seconds: Number($('ai-timeout').value), max_input_chars: Number($('ai-input').value) };
  }

  aiHelp() {
    $('ai-help').textContent = $('ai-provider').value === 'ollama'
      ? 'Ollama: use the server root, such as http://127.0.0.1:11434. Enter the exact installed model name. The app uses /api/chat with a JSON schema.'
      : 'Compatible server: include the API prefix, such as http://127.0.0.1:1234/v1 for LM Studio. The app uses /chat/completions and JSON output. Your model must follow the requested schema.';
    $('ai-help').append(document.createTextNode(' If the model runs on another computer, use its LAN address. The app backend—not your browser—must be able to reach it.'));
  }

  async refreshSources() {
    this.sources = await api('sources');
    $('source-list').replaceChildren(...this.sources.map(source => {
      const row = el('div', 'source-row');
      const info = el('div', 'source-info');
      const title = el('strong');
      title.append(externalLink(source.url, source.name));
      info.append(title, el('small', '', `${source.category} · ${source.feed_url || 'Automatic feed discovery'}`));
      const state = el('span', 'source-state' + (source.last_error ? ' failed' : ''), !source.enabled ? 'Disabled' : source.last_error ? source.last_error : source.last_checked ? 'Checked ' + formatTime(source.last_checked) : 'Not checked yet');
      info.append(state);
      const edit = el('button', '', 'Edit');
      edit.setAttribute('aria-label', `Edit ${source.name}`);
      edit.onclick = () => this.editSource(source);
      const toggle = el('button', '', source.enabled ? 'Disable' : 'Enable');
      toggle.setAttribute('aria-label', `${source.enabled ? 'Disable' : 'Enable'} ${source.name}`);
      toggle.onclick = () => busy(toggle, async () => {
        await api('sources/' + source.id, 'PUT', { ...source, enabled: !source.enabled });
        await this.refreshSources();
      });
      row.append(info, edit, toggle);
      return row;
    }));
  }

  editSource(source = null) {
    $('source-dialog-title').textContent = source ? 'Edit source' : 'Add a source';
    $('source-id').value = source?.id || '';
    $('source-name').value = source?.name || '';
    $('source-url').value = source?.url || '';
    $('source-feed').value = source?.feed_url || '';
    $('source-category').value = source?.category || 'News';
    $('source-enabled').checked = source ? Boolean(source.enabled) : true;
    $('source-error').textContent = '';
    $('source-dialog').showModal();
    $('source-name').focus();
  }

  renderRuns() {
    if (!this.app.overview) return;
    const overview = this.app.overview;
    $('schedule-timezone').textContent = `Uses the app server’s local timezone (${overview.timezone}). The server must be running. If today’s scheduled time was missed, it catches up after startup.`;
    const runs = overview.runs.map(run => {
      const details = el('details', 'run');
      details.append(el('summary', '', `${formatTime(run.started_at)} · ${run.kind} · ${run.status}`));
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
    $('run-history').replaceChildren(...(runs.length ? runs : [el('p', 'muted', 'No runs yet. Check sources to collect your first edition.')]));
  }
}
