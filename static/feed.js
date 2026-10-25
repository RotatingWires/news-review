import { $, api, el, externalLink, formatTime, notify, busy } from './shared.js';

const RELEVANCE = [
  { name: 'high', minimum: 75, label: 'Highly relevant', range: '75–100' },
  { name: 'medium', minimum: 40, label: 'Somewhat relevant', range: '40–74' },
  { name: 'low', minimum: 0, label: 'Low relevance', range: '0–39' },
];
const UNREVIEWED = { name: 'pending', label: 'Awaiting review' };

function checkboxRow(row, checkbox) {
  row.onmousedown = event => { if (event.target !== checkbox) event.preventDefault(); };
  row.onclick = event => {
    if (event.target === checkbox) return;
    event.preventDefault();
    checkbox.focus({ preventScroll: true });
    checkbox.click();
  };
}

export class Feed {
  constructor(app) {
    this.app = app;
    this.page = 1;
    this.requestId = 0;
    this.selectedTopics = new Set();
    const key = $('relevance-key');
    key.append(el('span', 'relevance-key-heading', 'Relevance:'));
    [...RELEVANCE, UNREVIEWED].forEach(tier => {
      const item = el('span', 'relevance-key-item');
      const swatch = el('span', 'relevance-swatch relevance-' + tier.name);
      swatch.setAttribute('aria-hidden', 'true');
      item.append(swatch, document.createTextNode(tier.label + (tier.range ? ` (${tier.range})` : '')));
      key.append(item);
    });
    $('feedback-form').onsubmit = async event => {
      event.preventDefault();
      await this.saveFeedback(this.feedbackVote, event.submitter);
    };
    $('feedback-remove').onclick = () => this.saveFeedback(0, $('feedback-remove'));
    $('search').onsubmit = event => { event.preventDefault(); this.reload(); };
    ['query', 'day', 'period', 'show-hidden'].forEach(id => { $(id).onchange = () => this.reload(); });
    document.querySelectorAll('input[name="relevance"]').forEach(input => { input.onchange = () => this.reload(); });
    document.querySelectorAll('#topic-menu .filter-option').forEach(row => checkboxRow(row, row.querySelector('input')));
    $('topic-button').onclick = () => this.closeTopics(!$('topic-menu').hidden);
    $('topic-all').onclick = () => { this.selectedTopics.clear(); this.renderTopics(this.app.overview.tags); this.reload(); };
    document.addEventListener('click', event => { if (!event.target.closest('.topic-filter')) this.closeTopics(true); });
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && !$('topic-menu').hidden) {
        event.preventDefault(); this.closeTopics(true); $('topic-button').focus();
      }
    });
    document.querySelector('.topic-filter').addEventListener('keydown', event => {
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      this.closeTopics(false);
      const items = [...$('topic-menu').querySelectorAll('button, input')];
      const current = items.indexOf(document.activeElement);
      const index = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
        : current < 0 ? 0 : (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
      items[index].focus();
    });
    document.addEventListener('focusin', event => { if (!event.target.closest('.topic-filter')) this.closeTopics(true); });
    $('previous-day').onclick = () => this.moveDay(-1);
    $('next-day').onclick = () => this.moveDay(1);
    $('today').onclick = () => { this.clearFilters(); $('day').value = app.overview.today; this.reload(); };
    $('clear-search').onclick = () => { this.clearFilters(); this.reload(); };
    $('page-prev').onclick = () => { this.page--; this.loadSafe(); };
    $('page-next').onclick = () => { this.page++; this.loadSafe(); };
  }

  editFeedback(story, vote) {
    this.feedbackStory = story;
    this.feedbackVote = vote;
    $('feedback-title').textContent = vote > 0 ? 'Thumbs up' : 'Thumbs down';
    $('feedback-headline').textContent = story.headline;
    $('feedback-reason').value = story.feedback_reason || '';
    $('feedback-remove').hidden = !story.feedback_vote;
    $('feedback-dialog').showModal();
    $('feedback-reason').focus();
  }

  async saveFeedback(vote, button) {
    await busy(button, async () => {
      await api('feedback/story/' + this.feedbackStory.id, 'PUT', { vote, reason: vote ? $('feedback-reason').value : '' });
      $('feedback-dialog').close();
      await this.load();
      notify(vote ? 'Feedback saved. Learned preferences update before the next review batch.' : 'Article feedback removed.');
    }, 'Saving article feedback...');
  }

  clearFilters() {
    ['query', 'period'].forEach(id => { $(id).value = ''; });
    this.selectedTopics.clear();
    this.renderTopics(this.app.overview.tags);
    $('show-hidden').checked = false;
    document.querySelectorAll('input[name="relevance"]').forEach(input => { input.checked = true; });
  }

  closeTopics(close) {
    $('topic-menu').hidden = close;
    $('topic-button').setAttribute('aria-expanded', String(!close));
  }

  renderTopics(topics) {
    this.selectedTopics = new Set([...this.selectedTopics].filter(topic => topics.includes(topic)));
    $('topic-label').textContent = 'Filter...';
    $('topic-options').replaceChildren(...topics.map(topic => {
      const label = el('div', 'checkbox topic-row');
      const checkbox = el('input');
      checkbox.type = 'checkbox';
      checkbox.setAttribute('aria-label', topic);
      checkbox.checked = this.selectedTopics.has(topic);
      checkbox.onchange = () => {
        if (checkbox.checked) this.selectedTopics.add(topic); else this.selectedTopics.delete(topic);
        $('topic-label').textContent = 'Filter...';
        this.reload();
      };
      checkboxRow(label, checkbox);
      label.append(checkbox, el('span', 'topic-name', topic));
      return label;
    }));
    if (!topics.length) $('topic-options').append(el('p', 'muted', 'Topics will appear after stories are collected.'));
  }

  moveDay(offset) {
    const value = new Date(($('day').value || this.app.overview.today) + 'T12:00:00Z');
    value.setUTCDate(value.getUTCDate() + offset);
    $('day').value = value.toISOString().slice(0, 10);
    this.clearFilters();
    this.reload();
  }

  reload() { this.page = 1; this.loadSafe(); }
  async loadSafe() {
    try { await this.load(); } catch (error) { notify(error.message, true); }
  }

  async load() {
    const requestId = ++this.requestId;
    if (!$('day').value) $('day').value = this.app.overview.today;
    const query = $('query').value.trim(), period = $('period').value.trim();
    const history = period.includes('/');
    const searching = Boolean(query || period);
    ['day', 'previous-day', 'next-day'].forEach(id => { $(id).disabled = history; });
    $('day').title = history ? 'The date range controls which days are searched. Clear it to use this picker.' : 'Publication day';
    const bookmarked = this.app.view === 'bookmarks';
    const params = new URLSearchParams({ page: this.page, time_zone: Intl.DateTimeFormat().resolvedOptions().timeZone });
    if (query) params.set('query', query);
    if (period) {
      params.set('period', period);
      params.set('time_zone', Intl.DateTimeFormat().resolvedOptions().timeZone);
    }
    this.selectedTopics.forEach(topic => params.append('tag', topic));
    if (!bookmarked) params.set('day', $('day').value);
    const excludedRelevance = [...document.querySelectorAll('input[name="relevance"]')].filter(input => !input.checked).map(input => input.value);
    excludedRelevance.forEach(value => params.append('exclude_relevance', value));
    if (bookmarked) params.set('bookmarks', '1');
    if ($('show-hidden').checked) params.set('hidden', '1');
    $('stories').setAttribute('aria-busy', 'true');
    try {
      const result = await api('stories?' + params);
      if (requestId !== this.requestId) return;
      $('stories').replaceChildren(...result.items.map(story => this.tile(story)));
      $('day-controls').hidden = bookmarked;
      $('clear-search').hidden = !searching && !this.selectedTopics.size && !$('show-hidden').checked && !excludedRelevance.length;
      $('results-heading').textContent = history ? 'From your history' : bookmarked ? 'Saved stories' : this.editionTitle();
      $('story-count').textContent = `${result.total} ${result.total === 1 ? 'story' : 'stories'}`;
      $('empty').hidden = result.total > 0;
      $('empty-title').textContent = bookmarked ? 'No bookmarks yet.' : searching ? 'Nothing matched this search.' : 'No stories in this edition.';
      $('empty-description').textContent = bookmarked ? 'Use the bookmark on any story to save it here. Saved stories stay in your history.' : searching ? 'Try another keyword, select a different day, or enter a date range to search across days. Search includes source names, headlines, summaries, excerpts, and tags.' : 'No stories in this edition yet. Check your sources to start collecting, or browse a different day. Your local model can be connected whenever you’re ready.';
      $('empty-action').hidden = bookmarked || searching;
      $('empty-action').onclick = () => this.app.collect('collect', $('empty-action'));
      $('page-prev').hidden = this.page <= 1;
      $('page-next').hidden = this.page * result.page_size >= result.total;
      $('page-label').textContent = result.total > result.page_size ? `Page ${this.page} of ${Math.ceil(result.total / result.page_size)}` : '';
    } finally { if (requestId === this.requestId) $('stories').setAttribute('aria-busy', 'false'); }
  }

  editionTitle() {
    if ($('day').value === this.app.overview.today) return 'Today’s Edition';
    const value = new Date($('day').value + 'T12:00:00Z');
    return Number.isNaN(value.getTime()) ? 'Daily Edition' : value.toLocaleDateString(undefined, { month: 'long', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
  }

  tile(story) {
    const card = el('article', 'story');
    const top = el('div', 'story-top');
    const bookmark = el('button', 'bookmark', story.bookmarked ? '★' : '☆');
    bookmark.setAttribute('aria-pressed', String(story.bookmarked));
    bookmark.setAttribute('aria-label', `${story.bookmarked ? 'Remove bookmark:' : 'Bookmark:'} ${story.headline}`);
    bookmark.onclick = () => busy(bookmark, async () => {
      const next = !story.bookmarked;
      await api('bookmarks/' + story.id, 'PUT', { bookmarked: next });
      story.bookmarked = next;
      if (this.app.view === 'bookmarks' && !next) await this.load();
      else card.replaceWith(this.tile(story));
      notify(next ? 'Saved to bookmarks.' : 'Bookmark removed.');
    }, story.bookmarked ? 'Removing bookmark…' : 'Saving bookmark…');
    const tier = story.status !== 'pending' && Number.isInteger(story.score)
      ? RELEVANCE.find(value => story.score >= value.minimum) || UNREVIEWED : UNREVIEWED;
    const relevance = el('span', 'relevance-indicator relevance-' + tier.name);
    const description = tier === UNREVIEWED ? 'Awaiting AI review; no current relevance score'
      : `${tier.label}: AI interest score ${story.score}/100`;
    relevance.title = description;
    relevance.setAttribute('role', 'img');
    relevance.setAttribute('aria-label', description);
    const controls = el('span', 'story-controls');
    [1, -1].forEach(vote => {
      const button = el('button', 'feedback-button');
      const icon = el('img');
      icon.src = vote > 0 ? '/thumb-up.png' : '/thumb-down.png';
      icon.alt = '';
      icon.draggable = false;
      button.append(icon);
      button.type = 'button';
      button.setAttribute('aria-label', (vote > 0 ? 'Like: ' : 'Dislike: ') + story.headline);
      button.setAttribute('aria-pressed', String(story.feedback_vote === vote));
      button.title = vote > 0 ? 'Thumbs up; add or edit an optional reason' : 'Thumbs down; add or edit an optional reason';
      button.onclick = () => this.editFeedback(story, vote);
      controls.append(button);
    });
    controls.append(bookmark, relevance);
    top.append(el('span', 'source-name', story.source_name || 'Source'), controls);
    const heading = el('h3');
    heading.append(externalLink(story.url, story.headline));
    card.append(top, heading);
    if (story.status === 'pending') {
      card.append(el('div', 'basis', story.summary ? 'Queued for reassessment · previous summary' : 'Source excerpt · awaiting AI review'));
    } else if (story.content_basis === 'excerpt') card.append(el('div', 'basis', 'AI summary · based on source excerpt'));
    const summary = story.summary || story.excerpt;
    card.append(el('p', 'story-summary', summary ? summary.slice(0, story.summary ? 2400 : 650) + (!story.summary && summary.length > 650 ? '…' : '') : 'No readable excerpt available. Open the original story to read more.'));
    const tags = el('div', 'tags');
    story.tags.forEach(tag => {
      const button = el('button', 'tag', tag);
      button.onclick = () => { this.selectedTopics.add(tag); this.renderTopics(this.app.overview.tags); this.reload(); };
      tags.append(button);
    });
    card.append(tags);
    const bottom = el('div', 'story-bottom');
    const timestamp = story.published_at || story.discovered_at;
    const time = el('time', '', (story.published_at ? '' : 'Collected ') + formatTime(timestamp));
    time.dateTime = timestamp;
    time.title = `Collected ${formatTime(story.discovered_at)}`;
    bottom.append(time);
    if (story.milestone !== 'not applicable') bottom.append(el('span', '', story.milestone));
    if (story.model) {
      const model = el('span', '', story.model);
      model.title = `Reviewed by ${story.provider} · ${formatTime(story.reviewed_at)}`;
      bottom.append(model);
    }
    card.append(bottom);
    if (story.related_count) {
      const related = el('details', 'related');
      related.append(el('summary', '', `${story.related_count} additional source${story.related_count > 1 ? 's' : ''}`));
      let loaded = false;
      related.ontoggle = async () => {
        if (!related.open || loaded) return;
        loaded = true;
        try {
          const links = await api('related/' + story.id);
          links.forEach(item => related.append(externalLink(item.url, `${item.source_name}: ${item.headline}`)));
        } catch (error) { loaded = false; notify(error.message, true); }
      };
      card.append(related);
    }
    return card;
  }
}
