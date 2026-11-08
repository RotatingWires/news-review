/* Persistent light/dark theme controller for NewsReview, like the other apps' theme.js. Loaded before the page draws. */
(() => {
'use strict';

const STORAGE_KEY = 'newsreview-theme';
const VALID = new Set(['light', 'dark']);

function readPreference() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    return VALID.has(saved) ? saved : 'light';
  } catch {
    return 'light';
  }
}

function applyTheme(preference, persist = false) {
  const selected = VALID.has(preference) ? preference : 'light';
  if (persist) {
    try { localStorage.setItem(STORAGE_KEY, selected); } catch {}
  }
  const root = document.documentElement;
  root.dataset.theme = selected;
  root.style.colorScheme = selected;
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', selected === 'dark' ? '#111858' : '#000080');
  return selected;
}

window.addEventListener('storage', event => {
  if (event.key === STORAGE_KEY) applyTheme(readPreference());
});

applyTheme(readPreference());
window.NewsTheme = {
  getPreference: () => document.documentElement.dataset.theme || readPreference(),
  setPreference: preference => applyTheme(preference, true)
};
})();
