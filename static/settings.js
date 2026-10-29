import { syncDropdowns } from './dropdowns.js';
import { $, api, busy, el, externalLink, formatTime, notify } from './shared.js';

export class Settings {
  constructor(app) {
    this.app = app;
    this.values = null;
    this.sources = [];
    document.querySelectorAll('[data-tab]').forEach(button => { button.onclick = () => this.tab(button.dataset.tab); });
    $('add-topic').onclick = () => this.editTopic();
    $('topic-edit-form').onsubmit = event => this.submit(event, async () => {
      const name = $('topic-name-input').value.trim();
      const previous = this.editingTopic;
      const result = await api('topics', 'POST', previous === null ? { action: 'add', name } : { action: 'rename', name: previous, new_name: name });
      $('topic-edit-dialog').close();
      await this.topicsChanged(result, previous, name);
      notify(previous === null ? 'Topic added. Future reviews can use it.' : `Topic renamed on ${result.updated_stories} stories.`);
    });
    $('topic-delete-form').onsubmit = event => this.submit(event, async () => {
      const name = $('topic-delete-name').textContent;
      const result = await api('topics', 'POST', { action: 'delete', name, confirmation: 'DELETE TOPIC' });
      $('topic-delete-dialog').close();
      await this.topicsChanged(result, name, null);
      notify(`Topic deleted. Label removed from ${result.updated_stories} stories. Articles were kept.`);
    });
    $('add-interest').onclick = () => this.interestRow({ name: '', preference: 'favor' });
    $('interests-form').onsubmit = event => this.submit(event, async () => {
      const topics = [...$('interest-rows').children].map(row => ({ name: row.querySelector('input').value.trim(), preference: row.querySelector('select').value }));
      const interests = { topics, guidance: $('guidance').value, exclusions: $('exclusions').value.split('\n').map(x => x.trim()).filter(Boolean) };
      await api('settings', 'PUT', { interests });
      this.values.interests = interests;
      notify('Interests saved. Future reviews will use these preferences.');
    });
    $('ai-form').onsubmit = event => this.submit(event, async () => {
      const ai = this.aiValues();
      await api('settings', 'PUT', { ai });
      this.values.ai = ai;
      notify(ai.model ? 'AI settings saved.' : 'AI review paused. Collection remains available.');
    });
    $('ai-provider').onchange = () => {
      const defaults = { ollama: 'http://127.0.0.1:11434', compatible: 'http://127.0.0.1:1234/v1', openrouter: 'https://openrouter.ai/api/v1' };
      if (Object.values(defaults).includes($('ai-url').value)) $('ai-url').value = defaults[$('ai-provider').value];
      this.aiHelp();
    };
    $('test-model').onclick = () => busy($('test-model'), async () => {
      $('model-test-result').hidden = false;
      $('model-test-result').textContent = 'Asking your server to review a fictional sample article…';
      try {
        const result = await api('test-model', 'POST', this.aiValues(), Number($('ai-timeout').value) * 1000 + 15000);
        $('model-test-result').textContent = `Connection and output validation passed. Sample summary: ${result.summary}`;
      } catch (error) { $('model-test-result').textContent = error.message; throw error; }
    });
    $('collection-form').onsubmit = event => this.submit(event, async () => {
      const collection = { enabled: $('schedule-enabled').checked, interval_hours: Number($('schedule-interval').value),
        items_per_source: Number($('items-per-source').value), max_reviews: Number($('max-reviews').value), lookback_days: Number($('lookback').value) };
      await api('settings', 'PUT', { collection });
      this.values.collection = collection;
      notify('Collection settings saved.');
    });
    $('feedback-profile-form').onsubmit = event => this.submit(event, async () => {
      await api('feedback/profile', 'PUT', { profile: $('feedback-profile').value, enabled: $('feedback-enabled').checked, auto_update: $('feedback-auto').checked });
      notify('Learned preferences saved.');
      await this.loadFeedback();
    });
    $('feedback-rebuild').onclick = () => busy($('feedback-rebuild'), async () => {
      await api('feedback/rebuild', 'POST', {}, Number(this.values.ai.timeout_seconds) * 1000 + 15000);
      await this.loadFeedback();
      notify('Learned preferences updated from recent feedback.');
    }, 'Updating learned preferences...');
    $('feedback-reset').onclick = () => $('feedback-reset-dialog').showModal();
    $('feedback-confirm-reset').onclick = () => busy($('feedback-confirm-reset'), async () => {
      await api('reset', 'POST', { confirmation: 'RESET SELECTED', selections: { feedback: true } });
      $('feedback-reset-dialog').close();
      await this.app.reloadDataset();
      notify('Article feedback and learned preferences were reset.');
    });
    $('retry-failed').onclick = () => busy($('retry-failed'), async () => {
      const result = await api('requeue', 'POST', { failed_only: true });
      notify(`${result.count} failed reviews reset. Use Review pending to process them.`);
      await this.app.refreshOverview();
    });
    $('reassess').onclick = () => {
      $('confirm-dialog').showModal();
      $('confirm-dialog').querySelector('[data-close]').focus();
    };
    $('confirm-reassess').onclick = () => busy($('confirm-reassess'), async () => {
      const result = await api('requeue', 'POST', { failed_only: false });
      $('confirm-dialog').close();
      notify(`${result.count} stories queued. Use Review pending to process them.`);
      await this.app.refreshOverview();
    });
    $('purge-duplicates').onclick = () => busy($('purge-duplicates'), async () => {
      const result = await api('maintenance/duplicates');
      $('purge-duplicates-count').textContent = `${result.count} duplicate ${result.count === 1 ? 'story can' : 'stories can'} be removed.`;
      $('confirm-purge-duplicates').disabled = result.count === 0;
      $('purge-duplicates-dialog').showModal();
      $('purge-duplicates-dialog').querySelector('[data-close]').focus();
    });
    $('purge-duplicates-form').onsubmit = async event => {
      event.preventDefault();
      await busy(event.submitter, async () => {
        const result = await api('maintenance/duplicates/purge', 'POST', { confirmation: 'PURGE DUPLICATES' }, 120000);
        $('purge-duplicates-dialog').close();
        await this.app.reloadDataset();
        notify(result.warning || `${result.deleted} duplicate ${result.deleted === 1 ? 'story' : 'stories'} removed.${result.compacted ? ' Database compacted.' : ''} Bookmarks and preferences were kept.`, Boolean(result.warning));
      }, 'Purging duplicate stories and compacting the database...');
    };
    $('clear-history').onclick = () => {
      $('clear-history-form').reset();
      $('clear-history-error').textContent = '';
      $('clear-history-dialog').showModal();
      $('clear-history-dialog').querySelector('[data-close]').focus();
    };
    $('clear-history-form').onsubmit = event => this.submit(event, async () => {
      try {
        const selections = Object.fromEntries([...$('clear-history-form').querySelectorAll('.reset-options input')].map(input => [input.id.slice('reset-'.length), input.checked]));
        const result = await api('reset', 'POST', { confirmation: $('clear-history-confirmation').value, selections });
        $('clear-history-dialog').close();
        await this.app.reloadDataset();
        notify(`Selected data reset (${result.deleted_stories} stories removed). Unchecked categories were kept.`);
      } catch (error) { $('clear-history-error').textContent = error.message; throw error; }
    });
    $('add-source').onclick = () => this.editSource();
    $('check-new-sources').onclick = () => this.app.collect('collect', $('check-new-sources'), true);
    $('refresh-sources').onclick = () => this.app.collect('collect', $('refresh-sources'));
    $('discover-feed').onclick = () => busy($('discover-feed'), async () => {
      $('source-error').textContent = '';
      try {
        const result = await api('discover', 'POST', { url: $('source-url').value }, 90000);
        $('source-feed').value = result.feed_url;
        notify('Feed discovered. Save the source to keep it.');
      } catch (error) { $('source-error').textContent = error.message; throw error; }
    });
    $('preview-source').onclick = () => busy($('preview-source'), async () => {
      $('source-error').textContent = '';
      $('source-preview').hidden = false;
      $('source-preview').textContent = 'Checking the source…';
      try {
        const result = await api('preview-source', 'POST', this.sourceValues(), 120000);
        if (result.feed_url && !$('source-feed').value) $('source-feed').value = result.feed_url;
        $('source-preview').replaceChildren(el('strong', '', `Detected: ${({ page: 'Website page', feed: 'RSS/Atom', 'feed+page': 'RSS/Atom + website' })[result.method]} · ${result.entries.length} sample stories`));
        const list = el('ul');
        result.entries.forEach(story => { const item = el('li'); item.append(externalLink(story.url, story.headline)); list.append(item); });
        $('source-preview').append(list);
        if (result.note) $('source-preview').append(el('p', '', result.note));
      } catch (error) { $('source-error').textContent = error.message; $('source-preview').hidden = true; throw error; }
    });
    $('source-form').onsubmit = event => this.submit(event, async () => {
      const id = $('source-id').value;
      const source = this.sourceValues();
      try {
        await api(id ? 'sources/' + id : 'sources', id ? 'PUT' : 'POST', source);
        $('source-dialog').close();
        await this.refreshSources();
        notify('Source saved.');
      } catch (error) { $('source-error').textContent = error.message; throw error; }
    });
  }

  submit(event, work) { event.preventDefault(); return busy(event.submitter, work); }

  sourceValues() {
    return { name: $('source-name').value, url: $('source-url').value, feed_url: $('source-feed').value,
      category: $('source-category').value, enabled: $('source-enabled').checked, mode: $('source-mode').value,
      link_pattern: $('source-pattern').value, stale_days: Number($('source-stale-days').value) };
  }

  tab(name) {
    ['interests', 'topics', 'sources', 'ai', 'collection'].forEach(id => {
      $('panel-' + id).hidden = name !== id;
      document.querySelector(`[data-tab="${id}"]`).classList.toggle('active', name === id);
    });
    if (name === 'sources' && this.values) this.refreshSources().catch(error => notify(error.message, true));
    if (name === 'topics' && this.values) this.loadTopics().catch(error => notify(error.message, true));
  }

  async load() {
    // Controls must not accept edits that an in-flight initial load would replace.
    $('settings-fields').disabled = true;
    $('settings').setAttribute('aria-busy', 'true');
    try {
      this.values = await api('settings');
      await this.refreshSources();
      await this.loadTopics();
      const { interests, ai, collection } = this.values;
      $('interest-rows').replaceChildren();
      interests.topics.forEach(topic => this.interestRow(topic));
      $('guidance').value = interests.guidance;
      $('exclusions').value = interests.exclusions.join('\n');
      await this.loadFeedback();
      $('ai-provider').value = ai.provider;
      $('ai-url').value = ai.base_url;
      $('ai-model').value = ai.model;
      $('ai-timeout').value = ai.timeout_seconds;
      $('ai-input').value = ai.max_input_chars;
      $('ai-output').value = ai.max_output_tokens ?? 2400;
      $('ai-unload').checked = ai.unload_after_use ?? true;
      this.aiHelp();
      $('schedule-enabled').checked = collection.enabled;
      $('schedule-interval').value = collection.interval_hours ?? 24;
      $('items-per-source').value = collection.items_per_source;
      $('max-reviews').value = collection.max_reviews;
      $('lookback').value = collection.lookback_days;
      this.renderRuns();
      syncDropdowns();
    } finally {
      $('settings-fields').disabled = false;
      $('settings').setAttribute('aria-busy', 'false');
    }
  }

  async loadTopics() { this.renderTopics(await api('topics')); }

  renderTopics(topics) {
    $('topic-rows').replaceChildren(...topics.map(name => {
      const row = el('div', 'topic-row');
      const title = el('span', 'managed-topic-name', name);
      const controls = el('div', 'button-row');
      const rename = el('button', '', 'Rename');
      rename.type = 'button';
      rename.setAttribute('aria-label', 'Rename topic ' + name);
      rename.onclick = () => this.editTopic(name);
      const remove = el('button', 'danger', 'Delete');
      remove.type = 'button';
      remove.setAttribute('aria-label', 'Delete topic ' + name);
      remove.onclick = () => {
        $('topic-delete-name').textContent = name;
        $('topic-delete-dialog').showModal();
        $('topic-delete-dialog').querySelector('[data-close]').focus();
      };
      controls.append(rename, remove);
      row.append(title, controls);
      return row;
    }));
    if (!topics.length) $('topic-rows').append(el('p', 'muted', 'No topics configured. Add one to label future AI reviews.'));
  }

  editTopic(name = null) {
    this.editingTopic = name;
    $('topic-edit-title').textContent = name === null ? 'Add topic' : 'Rename topic';
    $('topic-name-input').value = name || '';
    $('topic-edit-dialog').showModal();
    $('topic-name-input').focus();
  }

  async topicsChanged(result, previous, name) {
    if (previous !== null && this.app.feed.selectedTopics.delete(previous) && name !== null) this.app.feed.selectedTopics.add(name);
    this.renderTopics(result.topics);
    await this.app.refreshOverview();
    await this.app.feed.load();
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
    syncDropdowns(row);
    if (!topic.name) input.focus();
  }

  async loadFeedback() {
    const value = await api('feedback');
    $('feedback-enabled').checked = value.enabled;
    $('feedback-auto').checked = value.auto_update;
    $('feedback-profile').value = value.profile;
    $('feedback-rebuild').disabled = value.count === 0;
    $('feedback-profile-status').textContent = `${value.count} saved ${value.count === 1 ? 'rating' : 'ratings'}. ${value.revision !== value.profile_revision ? 'New feedback is awaiting a profile update.' : 'Profile is current.'}`;
  }

  aiValues() {
    return { provider: $('ai-provider').value, base_url: $('ai-url').value,
      model: $('ai-model').value.trim(), timeout_seconds: Number($('ai-timeout').value), max_input_chars: Number($('ai-input').value), max_output_tokens: Number($('ai-output').value), unload_after_use: $('ai-unload').checked };
  }

  aiHelp() {
    const provider = $('ai-provider').value;
    $('ai-unload-row').hidden = provider !== 'ollama';
    $('ai-model').placeholder = provider === 'openrouter' ? 'anthropic/claude-haiku-5.5' : 'Exact model name or server model ID';
    const help = {
      ollama: 'Ollama: use the server root, such as http://127.0.0.1:11434. Enter the exact installed model name. The app uses /api/chat with a JSON schema and thinking disabled.',
      compatible: 'Compatible server: include the API prefix, such as http://127.0.0.1:1234/v1 for LM Studio. The app uses /chat/completions and JSON output. Your model must follow the requested schema.',
      openrouter: 'OpenRouter: use https://openrouter.ai/api/v1 and a model ID such as anthropic/claude-haiku-5.5. Set NEWSREVIEW_MODEL_API_KEY in the app server environment. Thinking is disabled for reviews, sample tests, and preference updates. Article text and review preferences are sent to OpenRouter and its selected provider.',
    };
    $('ai-help').textContent = help[provider];
    if (provider !== 'openrouter') $('ai-help').append(document.createTextNode(' If the model runs on another computer, use its LAN address. The app backend must be able to reach it.'));
  }

  syncNewSources() {
    const count = this.sources.filter(source => source.enabled && source.last_checked === null).length;
    $('check-new-sources').textContent = `Check new sources (${count})`;
    $('check-new-sources').disabled = Boolean(this.app.overview?.collector.running) || count === 0;
    $('check-new-sources').title = count ? 'Check only enabled sources with no recorded check' : 'No enabled sources are awaiting their first check';
  }

  async refreshSources() {
    this.sources = await api('sources');
    this.syncNewSources();
    $('source-list').replaceChildren(...this.sources.map(source => {
      const row = el('div', 'source-row');
      const info = el('div', 'source-info');
      const title = el('strong');
      title.append(externalLink(source.url, source.name));
      info.append(title, el('small', '', `${source.category} · ${source.mode === 'page' ? 'Website page' : source.feed_url || 'Automatic feed / website discovery'}`));
      const status = source.health.status;
      const statusLabel = { healthy: 'Healthy', unchecked: 'Not checked', disabled: 'Disabled', broken: 'Broken', error: 'Check failed' }[status];
      if (statusLabel) info.append(el('span', 'source-state ' + status, statusLabel));
      source.health.warnings.forEach(warning => info.append(el('small', 'source-warning', warning)));
      if (source.last_checked) info.append(el('small', '', `Checked ${formatTime(source.last_checked)} · Last success ${formatTime(source.last_success)} · ${source.last_count ?? 'Unknown'} entries · ${source.last_method || 'Method unknown'}`));
      if (source.newest_published) info.append(el('small', '', 'Newest reported publication: ' + formatTime(source.newest_published)));
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
    $('source-mode').value = source?.mode || 'auto';
    $('source-pattern').value = source?.link_pattern || '';
    $('source-stale-days').value = source?.stale_days || 14;
    $('source-preview').hidden = true;
    $('source-category').value = source?.category || 'News';
    $('source-enabled').checked = source ? Boolean(source.enabled) : true;
    $('source-error').textContent = '';
    syncDropdowns($('source-dialog'));
    $('source-dialog').showModal();
    $('source-name').focus();
  }

  renderRuns() {
    if (!this.app.overview) return;
    const overview = this.app.overview;
    $('schedule-timezone').textContent = `Collects every configured number of hours after the last full collection starts. The server must be running; overdue work catches up after startup. Manual full collections restart the interval.`;
    const runs = overview.runs.map(run => {
      const details = el('details', 'run');
      details.append(el('summary', '', `${formatTime(run.started_at)} · ${run.kind}${run.details.scope === 'unchecked' ? ' (new sources only)' : ''} · ${run.status}`));
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
