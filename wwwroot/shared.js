// $ and $$ take CSS selectors, the same helpers as TaskList, TaskList Stats, and SpendTracker.
export const $ = selector => document.querySelector(selector);
export const $$ = selector => [...document.querySelectorAll(selector)];
export function el(tag, className = '', text = '') {
  const node = document.createElement(tag);
  if (tag === 'button') node.type = 'button'; // Every button is typed, like the other apps.
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}
let noticeTimer;
let connectionError = '';
let defaultStatus = 'Loading...';
let notice = null;
let dataRevision = null;
export function revisionHeaders() { return dataRevision ? { 'X-NewsReview-Revision': dataRevision } : {}; }
function renderStatus() {
  const message = connectionError || notice?.message || defaultStatus;
  $('#statusMessage').textContent = message;
  $('#statusMessage').title = message;
  const error = Boolean(connectionError || notice?.error);
  $('#statusMessage').classList.toggle('status-error', error);
  $('.footer-status').classList.toggle('has-error', error);
  $('#dismissStatus').hidden = !error;
}
export function setConnectionStatus(message) {
  connectionError = message || '';
  renderStatus();
}
export function dismissStatus() {
  notice = null;
  connectionError = '';
  clearTimeout(noticeTimer);
  renderStatus();
}
export function setDefaultStatus(message) { defaultStatus = message; renderStatus(); }
export function notify(message, error = false) {
  notice = { message, error };
  clearTimeout(noticeTimer);
  renderStatus();
  if (!error) noticeTimer = setTimeout(() => { notice = null; renderStatus(); }, 8000);
}
export async function api(path, method = 'GET', data, timeoutMs = 30000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  let response, result;
  try {
    response = await fetch('/api/' + path, {
      method, credentials: 'same-origin', signal: controller.signal,
      headers: { ...(method === 'GET' ? {} : revisionHeaders()), ...(data === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: data === undefined ? undefined : JSON.stringify(data),
    });
    // 204 No Content answers changes with nothing to return, like the other apps.
    result = response.status === 204 ? {} : await response.json();
  } catch (cause) {
    const message = response ? `NewsReview returned an unreadable response (HTTP ${response.status}). Check that this URL reaches the NewsReview server.`
      : `Cannot reach the NewsReview server at ${location.host}${cause.name === 'AbortError' ? ' (request timed out)' : ''}. Check that it is running and the address/port are correct. Collection may still be running; reconnect to confirm its status.`;
    const actionMessage = !response && method !== 'GET'
      ? `Could not send ${method} /api/${path}. The request may be blocked by the browser, an extension, or the network${cause.name === 'AbortError' ? ' (request timed out)' : ''}. If DevTools shows ERR_BLOCKED_BY_CLIENT, allow this app in your blocker. The action was not confirmed; check server status before retrying.`
      : message;
    const error = new Error(actionMessage);
    error.connection = true;
    document.dispatchEvent(new CustomEvent('newsreview-connection', { detail: { error: actionMessage } }));
    throw error;
  } finally { clearTimeout(timer); }
  document.dispatchEvent(new CustomEvent('newsreview-connection', { detail: { error: result.code === 'storage_unavailable' ? result.error : null } }));
  if (path === 'overview' && response.ok) dataRevision = result.revision;
  if (!response.ok) {
    if (response.status === 401) location.replace('/login.html');
    const error = new Error(response.status === 401 ? 'Your session has expired.' : result.error || `Request failed (${response.status})`);
    error.status = response.status;
    throw error;
  }
  return result;
}
export async function busy(button, work, progress = null) {
  const text = button.textContent;
  button.disabled = true;
  if (progress) notify(progress);
  else button.textContent = 'Working...';
  try { return await work(); }
  catch (error) { notify(error.message, true); }
  finally { button.disabled = false; button.textContent = text; }
}
export function externalLink(url, text, className = '') {
  const link = el('a', className, text);
  try {
    const parsed = new URL(url);
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('Invalid URL');
    link.href = parsed.href;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
  } catch { link.removeAttribute('href'); }
  return link;
}
export function formatTime(value) {
  if (!value) return 'Date unavailable';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Date unavailable' : date.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

/** Menus like the other apps' File / View / Help bar: one open at a time, closed by any outside click or Escape. */
export function closeMenus() {
  $$('.menu-dropdown').forEach(menu => { menu.hidden = true; });
  $$('[aria-haspopup="menu"]').forEach(button => button.setAttribute('aria-expanded', 'false'));
}
export function toggleMenu(button, menu) {
  const open = menu.hidden;
  closeMenus();
  menu.hidden = !open;
  button.setAttribute('aria-expanded', String(open));
}
export function menuItem(label, { role = 'menuitem', checked = false, onClick }) {
  const button = el('button');
  button.type = 'button';
  button.setAttribute('role', role);
  if (role !== 'menuitem') button.setAttribute('aria-checked', String(checked));
  button.append(el('span', 'menu-check'), el('span', 'menu-label', label));
  button.addEventListener('click', () => { closeMenus(); onClick(); });
  return button;
}
export function menuSeparator() {
  const separator = el('div', 'menu-separator');
  separator.setAttribute('role', 'separator');
  return separator;
}

/** Dates are typed as m/d/yy or m/d/yyyy (or yyyy-mm-dd), like the date ranges in every app; returns yyyy-mm-dd, or '' when it is not a real date. */
export function parseTypedDate(text) {
  const iso = /^\s*(\d{4})-(\d{1,2})-(\d{1,2})\s*$/.exec(text);
  const typed = /^\s*(\d{1,2})\/(\d{1,2})(?:\/(\d{2}|\d{4}))?\s*$/.exec(text);
  if (!iso && !typed) return '';
  const year = iso ? Number(iso[1]) : typed[3] ? Number(typed[3]) + (typed[3].length === 2 ? 2000 : 0) : new Date().getFullYear();
  const month = Number(iso ? iso[2] : typed[1]), day = Number(iso ? iso[3] : typed[2]);
  const date = new Date(year, month - 1, day);
  if (date.getFullYear() !== year || date.getMonth() !== month - 1 || date.getDate() !== day) return '';
  return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

/** yyyy-mm-dd as it is typed: m/d/yyyy. */
export function typedDate(iso) {
  const [year, month, day] = (iso || '').split('-').map(Number);
  return year ? `${month}/${day}/${year}` : '';
}
