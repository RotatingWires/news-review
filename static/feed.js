import { $, api, el, externalLink, formatTime, toast, busy } from './shared.js';

export class Feed {
  constructor(app) {
    this.app = app;
    this.page = 1;
    this.requestId = 0;
    this.selectedTopics = new Set();
    $('search').onsubmit = event => { event.preventDefault(); this.reload(); };
    ['day', 'period', 'show-hidden'].forEach(id => { $(id).onchange = () => this.reload(); });
    $('topic-button').onclick = () => this.closeTopics(!$('topic-menu').hidden);
    $('topic-all').onclick = () => { this.selectedTopics.clear(); this.renderTopics(this.app.overview.tags); this.reload(); };
    document.addEventListener('click', event => { if (!event.target.closest('.topic-filter')) this.closeTopics(true); });
    document.addEventListener('keydown', event => { if (event.key === 'Escape') this.closeTopics(true); });
    $('previous-day').onclick = () => this.moveDay(-1);
    $('next-day').onclick = () => this.moveDay(1);
    $('today').onclick = () => { this.clearFilters(); $('day').value = app.overview.today; this.reload(); };
    $('clear-search').onclick = () => { this.clearFilters(); this.reload(); };
    $('page-prev').onclick = () => { this.page--; this.loadSafe(); };
    $('page-next').onclick = () => { this.page++; this.loadSafe(); };
  }

  clearFilters() {
    ['query', 'period'].forEach(id => { $(id).value = ''; });
    this.selectedTopics.clear();
    this.renderTopics(this.app.overview.tags);
    $('show-hidden').checked = false;
  }

  closeTopics(close) {
    $('topic-menu').hidden = close;
    $('topic-button').setAttribute('aria-expanded', String(!close));
  }

  renderTopics(topics) {
    this.selectedTopics = new Set([...this.selectedTopics].filter(topic => topics.includes(topic)));
    $('topic-label').textContent = this.selectedTopics.size ? `Topics: ${this.selectedTopics.size} selected` : 'Topics: All topics';
    $('topic-options').replaceChildren(...topics.map(topic => {
      const label = el('label', 'checkbox');
      const checkbox = el('input');
      checkbox.type = 'checkbox';
      checkbox.checked = this.selectedTopics.has(topic);
      checkbox.onchange = () => {
        if (checkbox.checked) this.selectedTopics.add(topic); else this.selectedTopics.delete(topic);
        $('topic-label').textContent = this.selectedTopics.size ? `Topics: ${this.selectedTopics.size} selected` : 'Topics: All topics';
        this.reload();
      };
      label.append(checkbox, document.createTextNode(topic));
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
    try { await this.load(); } catch (error) { toast(error.message, true); }
  }

  async load() {
    const requestId = ++this.requestId;
    if (!$('day').value) $('day').value = this.app.overview.today;
    const query = $('query').value.trim(), period = $('period').value.trim();
    const history = Boolean(query || period);
    const bookmarked = this.app.view === 'bookmarks';
    const params = new URLSearchParams({ page: this.page });
    if (query) params.set('query', query);
    if (period) params.set('period', period);
    this.selectedTopics.forEach(topic => params.append('tag', topic));
    if (!history && !bookmarked) params.set('day', $('day').value);
    if (bookmarked) params.set('bookmarks', '1');
    if ($('show-hidden').checked) params.set('hidden', '1');
    $('stories').setAttribute('aria-busy', 'true');
    try {
      const result = await api('stories?' + params);
      if (requestId !== this.requestId) return;
      $('stories').replaceChildren(...result.items.map(story => this.tile(story)));
      $('day-controls').hidden = bookmarked;
      $('clear-search').hidden = !history && !this.selectedTopics.size && !$('show-hidden').checked;
      $('results-heading').textContent = history ? 'From your history' : bookmarked ? 'Saved stories' : this.editionTitle();
      $('story-count').textContent = `${result.total} ${result.total === 1 ? 'story' : 'stories'}`;
      $('empty').hidden = result.total > 0;
      $('empty-title').textContent = bookmarked ? 'No bookmarks yet.' : history ? 'Nothing matched this search.' : 'No stories in this edition.';
      $('empty-description').textContent = bookmarked ? 'Use the bookmark on any story to save it here. Saved stories stay in your history.' : history ? 'Try another keyword or a wider date range. Search includes saved headlines, summaries, excerpts, and tags.' : 'No stories in this edition yet. Check your sources to start collecting, or browse a different day. Your local model can be connected whenever you’re ready.';
      $('empty-action').hidden = bookmarked || history;
      $('empty-action').onclick = () => this.app.collect('collect', $('empty-action'));
      $('page-prev').hidden = this.page <= 1;
      $('page-next').hidden = this.page * result.page_size >= result.total;
      $('page-label').textContent = result.total > result.page_size ? `Page ${this.page} of ${Math.ceil(result.total / result.page_size)}` : '';
    } finally { if (requestId === this.requestId) $('stories').setAttribute('aria-busy', 'false'); }
  }

  editionTitle() {
    if ($('day').value === this.app.overview.today) return 'Today’s edition';
    const value = new Date($('day').value + 'T12:00:00Z');
    return Number.isNaN(value.getTime()) ? 'Daily edition' : value.toLocaleDateString(undefined, { month: 'long', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
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
      toast(next ? 'Saved to bookmarks.' : 'Bookmark removed.');
    });
    top.append(el('span', 'source-name', story.source_name || 'Source'), bookmark);
    const heading = el('h3');
    heading.append(externalLink(story.url, story.headline));
    card.append(top, heading);
    if (story.status === 'pending') {
      card.append(el('div', 'basis', story.summary ? 'Queued for reassessment · previous summary' : 'Source excerpt · awaiting AI review'));
    } else if (story.content_basis === 'excerpt') card.append(el('div', 'basis', 'AI summary · based on source excerpt'));
    const summary = story.summary || story.excerpt;
    card.append(el('p', 'story-summary', summary ? summary.slice(0, story.summary ? 2400 : 650) + (!story.summary && summary.length > 650 ? '…' : '') : 'No readable excerpt available. Open the original story to read more.'));
    if (story.why_it_matters) {
      const significance = el('div', 'significance');
      significance.append(el('strong', '', 'Why it matters'), el('span', '', story.why_it_matters));
      card.append(significance);
    }
    if (story.review_error) card.append(el('p', 'review-warning', story.review_error));
    if (['filtered', 'duplicate'].includes(story.status)) card.append(el('div', 'basis', story.status === 'duplicate' ? 'Additional coverage of an existing story' : 'Filtered by your editorial preferences'));
    const tags = el('div', 'tags');
    story.tags.forEach(tag => {
      const button = el('button', 'tag', tag);
      button.onclick = () => { this.selectedTopics.add(tag); this.renderTopics(this.app.overview.tags); this.reload(); };
      tags.append(button);
    });
    card.append(tags);
    const bottom = el('div', 'story-bottom');
    const time = el('time', '', story.published_at ? formatTime(story.published_at) : 'Publication date unavailable');
    if (story.published_at) time.dateTime = story.published_at;
    time.title = `Collected ${formatTime(story.discovered_at)} · edition ${story.day}`;
    bottom.append(time);
    if (story.milestone !== 'not applicable') bottom.append(el('span', '', story.milestone));
    if (story.model) {
      const model = el('span', '', story.model);
      model.title = `Reviewed by ${story.provider} · ${formatTime(story.reviewed_at)}`;
      bottom.append(model);
    }
    bottom.append(externalLink(story.url, 'Read story ↗', 'read-link'));
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
        } catch (error) { loaded = false; toast(error.message, true); }
      };
      card.append(related);
    }
    return card;
  }
}
