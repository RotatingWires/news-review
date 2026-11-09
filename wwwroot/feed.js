import { $, api, el, externalLink, formatTime, notify, busy, parseTypedDate, typedDate, $$ } from './shared.js';

/** The day box holds m/d/yyyy; the code and the server use yyyy-mm-dd. */
const dayValue = () => parseTypedDate($('#day').value);
const setDay = iso => { $('#day').value = typedDate(iso); };

const RELEVANCE = [
  { name: 'high', minimum: 75, label: 'Highly relevant', range: '75–100' },
  { name: 'medium', minimum: 40, label: 'Somewhat relevant', range: '40–74' },
  { name: 'low', minimum: 0, label: 'Low relevance', range: '0–39' },
];
const UNREVIEWED = { name: 'pending', label: 'Awaiting review' };

function checkboxRow(row, checkbox) {
  row.addEventListener('mousedown', event => { if (event.target !== checkbox) event.preventDefault(); });
  row.addEventListener('click', event => {
    if (event.target === checkbox) return;
    event.preventDefault();
    checkbox.focus({ preventScroll: true });
    checkbox.click();
  });
}

export class Feed {
  constructor(app) {
    this.app = app;
    this.page = 1;
    this.requestId = 0;
    this.selectedTopics = new Set();
    try {
      const saved = localStorage.getItem('newsreview-sort');
      if ([...$('#storySort').options].some(option => option.value === saved)) $('#storySort').value = saved;
    } catch { /* Sorting still works without browser storage. */ }
    $('#storySort').addEventListener('change', () => {
      try { localStorage.setItem('newsreview-sort', $('#storySort').value); } catch { /* Optional preference. */ }
      this.reload();
    });
    const key = $('#relevanceKey');
    key.append(el('span', 'relevance-key-heading', 'Relevance:'));
    [...RELEVANCE, UNREVIEWED].forEach(tier => {
      const item = el('span', 'relevance-key-item');
      const swatch = el('span', `relevance-swatch relevance-${tier.name}`);
      swatch.setAttribute('aria-hidden', 'true');
      item.append(swatch, document.createTextNode(tier.label + (tier.range ? ` (${tier.range})` : '')));
      key.append(item);
    });
    $('#feedbackForm').addEventListener('submit', async event => {
      event.preventDefault();
      await this.saveFeedback(this.feedbackVote, event.submitter);
    });
    $('#feedbackRemove').addEventListener('click', () => this.saveFeedback(0, $('#feedbackRemove')));
    $('#search').addEventListener('submit', event => { event.preventDefault(); this.reload(); });
    ['query', 'period', 'showHidden'].forEach(id => { $(`#${id}`).addEventListener('change', () => this.reload()); });
    // The day is typed like every app's dates; a day that isn't real says so and goes back to the last one shown.
    $('#day').addEventListener('change', () => {
      const typed = parseTypedDate($('#day').value);
      if (!typed) { notify('Type a day as m/d/yy, for example 11/8/26.', true); setDay(this.shownDay || this.app.overview.today); return; }
      setDay(typed);
      this.reload();
    });
    $$('input[name="relevance"]').forEach(input => { input.addEventListener('change', () => this.reload()); });
    $$('#topicMenu .filter-option').forEach(row => checkboxRow(row, row.querySelector('input')));
    $('#topicButton').addEventListener('click', () => this.closeTopics(!$('#topicMenu').hidden));
    $('#topicAll').addEventListener('click', () => { this.selectedTopics.clear(); this.renderTopics(this.app.overview.tags); this.reload(); });
    document.addEventListener('click', event => { if (!event.target.closest('.topic-filter')) this.closeTopics(true); });
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && !$('#topicMenu').hidden) {
        event.preventDefault(); this.closeTopics(true); $('#topicButton').focus();
      }
    });
    $('.topic-filter').addEventListener('keydown', event => {
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      this.closeTopics(false);
      const items = [...$('#topicMenu').querySelectorAll('button, input')];
      const current = items.indexOf(document.activeElement);
      const index = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
        : current < 0 ? 0 : (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
      items[index].focus();
    });
    document.addEventListener('focusin', event => { if (!event.target.closest('.topic-filter')) this.closeTopics(true); });
    $('#previousDay').addEventListener('click', () => this.moveDay(-1));
    $('#nextDay').addEventListener('click', () => this.moveDay(1));
    $('#today').addEventListener('click', () => { this.clearFilters(); setDay(app.overview.today); this.reload(); });
    $('#clearSearch').addEventListener('click', () => { this.clearFilters(); this.reload(); });
    $('#emptyAction').addEventListener('click', () => this.app.collect('collect', $('#emptyAction')));
    $('#pagePrev').addEventListener('click', () => { this.page--; this.loadSafe(); });
    $('#pageNext').addEventListener('click', () => { this.page++; this.loadSafe(); });
  }

  articleLink(story, onRead = () => {}) {
    const link = externalLink(story.url, story.headline);
    let saving = false;
    const markRead = async event => {
      if (event.type === 'auxclick' && event.button !== 1) return;
      if (story.readAt || saving) return;
      saving = true;
      try {
        const result = await api(`read/${story.id}`, 'POST', {});
        story.readAt = result.readAt;
        onRead();
      } catch (error) { notify(error.message, true); }
      finally { saving = false; }
    };
    link.addEventListener('click', markRead);
    link.addEventListener('auxclick', markRead);
    return link;
  }

  editFeedback(story, vote) {
    this.feedbackStory = story;
    this.feedbackVote = vote;
    $('#feedbackTitle').textContent = vote > 0 ? 'Thumbs up' : 'Thumbs down';
    $('#feedbackHeadline').textContent = story.headline;
    $('#feedbackReason').value = story.feedbackReason || '';
    $('#feedbackRemove').hidden = !story.feedbackVote;
    $('#feedbackDialog').showModal();
    $('#feedbackReason').focus();
  }

  async saveFeedback(vote, button) {
    await busy(button, async () => {
      await api(`feedback/story/${this.feedbackStory.id}`, 'PUT', { vote, reason: vote ? $('#feedbackReason').value : '' });
      $('#feedbackDialog').close();
      await this.load();
      notify(vote ? 'Feedback saved. Learned preferences update before the next review batch.' : 'Article feedback removed.');
    }, 'Saving article feedback...');
  }

  clearFilters() {
    ['query', 'period'].forEach(id => { $(`#${id}`).value = ''; });
    this.selectedTopics.clear();
    this.renderTopics(this.app.overview.tags);
    $('#showHidden').checked = false;
    $$('input[name="relevance"]').forEach(input => { input.checked = true; });
  }

  closeTopics(close) {
    $('#topicMenu').hidden = close;
    $('#topicButton').setAttribute('aria-expanded', String(!close));
  }

  renderTopics(topics) {
    this.selectedTopics = new Set([...this.selectedTopics].filter(topic => topics.includes(topic)));
    $('#topicLabel').textContent = 'Filter...';
    $('#topicOptions').replaceChildren(...topics.map(topic => {
      const label = el('div', 'checkbox topic-row');
      const checkbox = el('input');
      checkbox.type = 'checkbox';
      checkbox.className = 'retro-check';
      checkbox.setAttribute('aria-label', topic);
      checkbox.checked = this.selectedTopics.has(topic);
      checkbox.addEventListener('change', () => {
        if (checkbox.checked) this.selectedTopics.add(topic); else this.selectedTopics.delete(topic);
        $('#topicLabel').textContent = 'Filter...';
        this.reload();
      });
      checkboxRow(label, checkbox);
      label.append(checkbox, el('span', 'topic-name', topic));
      return label;
    }));
    if (!topics.length) $('#topicOptions').append(el('p', 'muted', 'Add topics in Settings > Topics.'));
  }

  moveDay(offset) {
    const value = new Date(`${dayValue() || this.app.overview.today}T12:00:00Z`);
    value.setUTCDate(value.getUTCDate() + offset);
    setDay(value.toISOString().slice(0, 10));
    this.clearFilters();
    this.reload();
  }

  reload() { this.page = 1; this.loadSafe(); }
  async loadSafe() {
    try { await this.load(); } catch (error) { notify(error.message, true); }
  }

  async load() {
    const requestId = ++this.requestId;
    if (!dayValue()) setDay(this.app.overview.today);
    this.shownDay = dayValue();
    const query = $('#query').value.trim(), period = $('#period').value.trim();
    const history = period.includes('/');
    const searching = Boolean(query || period);
    ['day', 'previousDay', 'nextDay'].forEach(id => { $(`#${id}`).disabled = history; });
    $('#day').title = history ? 'The date range controls which days are searched. Clear it to type a day here.' : 'Publication day, typed as m/d/yy';
    const bookmarked = this.app.view === 'bookmarks';
    const params = new URLSearchParams({ page: this.page, timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone, sort: $('#storySort').value });
    if (query) params.set('query', query);
    if (period) params.set('period', period);
    this.selectedTopics.forEach(topic => params.append('tag', topic));
    if (!bookmarked) params.set('day', dayValue());
    const excludedRelevance = [...$$('input[name="relevance"]')].filter(input => !input.checked).map(input => input.value);
    excludedRelevance.forEach(value => params.append('excludeRelevance', value));
    if (bookmarked) params.set('bookmarks', '1');
    if ($('#showHidden').checked) params.set('hidden', '1');
    $('#stories').setAttribute('aria-busy', 'true');
    try {
      const result = await api(`stories?${params}`);
      if (requestId !== this.requestId) return;
      $('#stories').replaceChildren(...result.items.map(story => this.tile(story)));
      $('#dayControls').hidden = bookmarked;
      $('#clearSearch').hidden = !searching && !this.selectedTopics.size && !$('#showHidden').checked && !excludedRelevance.length;
      $('#resultsHeading').textContent = history ? 'From Your History' : bookmarked ? 'Saved Stories' : this.editionTitle();
      $('#storyCount').textContent = `${result.total} ${result.total === 1 ? 'story' : 'stories'}`;
      $('#empty').hidden = result.total > 0;
      $('#emptyTitle').textContent = bookmarked ? 'No bookmarks yet.' : searching ? 'Nothing matched this search.' : 'No stories in this edition.';
      $('#emptyDescription').textContent = bookmarked ? 'Use the bookmark on any story to save it here. Saved stories stay in your history.' : searching ? 'Try another keyword, select a different day, or enter a date range to search across days. Search includes source names, headlines, summaries, excerpts, and tags.' : 'No stories in this edition yet. Check your sources to start collecting, or browse a different day. Configure a review model whenever you\'re ready.';
      $('#emptyAction').hidden = bookmarked || searching;
      $('#pagePrev').hidden = this.page <= 1;
      $('#pageNext').hidden = this.page * result.pageSize >= result.total;
      $('#pageLabel').textContent = result.total > result.pageSize ? `Page ${this.page} of ${Math.ceil(result.total / result.pageSize)}` : '';
    } finally { if (requestId === this.requestId) $('#stories').setAttribute('aria-busy', 'false'); }
  }

  editionTitle() {
    if (dayValue() === this.app.overview.today) return "Today's Edition";
    const value = new Date(`${dayValue()}T12:00:00Z`);
    return Number.isNaN(value.getTime()) ? 'Daily Edition' : value.toLocaleDateString(undefined, { month: 'long', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
  }

  tile(story) {
    const card = el('article', 'story');
    const top = el('div', 'story-top');
    const bookmark = el('button', 'bookmark', story.bookmarked ? '★' : '☆');
    bookmark.setAttribute('aria-pressed', String(story.bookmarked));
    bookmark.setAttribute('aria-label', `${story.bookmarked ? 'Remove bookmark:' : 'Bookmark:'} ${story.headline}`);
    bookmark.addEventListener('click', () => busy(bookmark, async () => {
      const next = !story.bookmarked;
      await api(`bookmarks/${story.id}`, 'PATCH', { bookmarked: next });
      story.bookmarked = next;
      if (this.app.view === 'bookmarks' && !next) await this.load();
      else card.replaceWith(this.tile(story));
      notify(next ? 'Saved to bookmarks.' : 'Bookmark removed.');
    }, story.bookmarked ? 'Removing bookmark...' : 'Saving bookmark...'));
    const tier = story.status !== 'pending' && Number.isInteger(story.score)
      ? RELEVANCE.find(value => story.score >= value.minimum) || UNREVIEWED : UNREVIEWED;
    const relevance = el('span', `relevance-indicator relevance-${tier.name}`);
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
      button.setAttribute('aria-pressed', String(story.feedbackVote === vote));
      button.title = vote > 0 ? 'Thumbs up; add or edit an optional reason' : 'Thumbs down; add or edit an optional reason';
      button.addEventListener('click', () => this.editFeedback(story, vote));
      controls.append(button);
    });
    controls.append(bookmark, relevance);
    const publisher = el('div', 'story-publisher');
    publisher.append(el('span', 'source-name', story.sourceName || 'Source'));
    const showRead = () => { if (story.readAt && !publisher.querySelector('.read-label')) publisher.append(el('span', 'read-label', 'Read')); };
    showRead();
    top.append(publisher, controls);
    const heading = el('h3');
    heading.append(this.articleLink(story, showRead));
    card.append(top, heading);
    if (story.status === 'pending') {
      card.append(el('div', 'basis', story.summary ? 'Queued for reassessment · previous summary' : 'Source excerpt · awaiting AI review'));
    } else if (story.contentBasis === 'excerpt') card.append(el('div', 'basis', 'AI summary · based on source excerpt'));
    const summary = story.summary || story.excerpt;
    card.append(el('p', 'story-summary', summary ? summary.slice(0, story.summary ? 2400 : 650) + (!story.summary && summary.length > 650 ? '...' : '') : 'No readable excerpt available. Open the original story to read more.'));
    const tags = el('div', 'tags');
    story.tags.forEach(tag => {
      const button = el('button', 'tag', tag);
      button.addEventListener('click', () => { this.selectedTopics.add(tag); this.renderTopics(this.app.overview.tags); this.reload(); });
      tags.append(button);
    });
    card.append(tags);
    const bottom = el('div', 'story-bottom');
    const timestamp = story.publishedAt || story.discoveredAt;
    const time = el('time', '', (story.publishedAt ? '' : 'Collected ') + formatTime(timestamp));
    time.dateTime = timestamp;
    time.title = `Collected ${formatTime(story.discoveredAt)}`;
    bottom.append(time);
    if (story.milestone !== 'not applicable') bottom.append(el('span', '', story.milestone));
    if (story.model) {
      const model = el('span', '', story.model);
      model.title = `Reviewed by ${story.provider} · ${formatTime(story.reviewedAt)}`;
      bottom.append(model);
    }
    card.append(bottom);
    if (story.relatedCount) {
      const related = el('details', 'related');
      related.append(el('summary', '', `${story.relatedCount} additional source${story.relatedCount > 1 ? 's' : ''}`));
      let loaded = false;
      related.addEventListener('toggle', async () => {
        if (!related.open || loaded) return;
        loaded = true;
        try {
          const links = await api(`related/${story.id}`);
          links.forEach(item => related.append(this.articleLink({ ...item, headline: `${item.sourceName}: ${item.headline}` })));
        } catch (error) { loaded = false; notify(error.message, true); }
      });
      card.append(related);
    }
    return card;
  }
}
