export const $ = id => document.getElementById(id);
export function el(tag, className = '', text = '') {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}
let toastTimer;
export function toast(message, error = false) {
  $('toast').textContent = message;
  $('toast').className = error ? 'error-toast' : '';
  $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $('toast').hidden = true; }, error ? 9000 : 4500);
}
export async function api(path, method = 'GET', data, timeoutMs = 30000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  let response, result;
  try {
    response = await fetch('/api/' + path, {
      method, credentials: 'same-origin', signal: controller.signal,
      headers: data === undefined ? {} : { 'Content-Type': 'application/json' },
      body: data === undefined ? undefined : JSON.stringify(data),
    });
    result = await response.json();
  } catch (cause) {
    const message = response ? `NewsReview returned an unreadable response (HTTP ${response.status}). Check that this URL reaches the NewsReview server.`
      : `Cannot reach the NewsReview server at ${location.host}${cause.name === 'AbortError' ? ' (request timed out)' : ''}. Check that it is running and the address/port are correct. Collection may still be running; reconnect to confirm its status.`;
    const error = new Error(message);
    error.connection = true;
    document.dispatchEvent(new CustomEvent('newsreview-connection', { detail: { error: message } }));
    throw error;
  } finally { clearTimeout(timer); }
  document.dispatchEvent(new CustomEvent('newsreview-connection', { detail: { error: result.code === 'storage_unavailable' ? result.error : null } }));
  if (!response.ok) {
    if (response.status === 401 && path !== 'login' && !$('login-dialog').open) $('login-dialog').showModal();
    const error = new Error(result.error || `Request failed (${response.status})`);
    error.status = response.status;
    throw error;
  }
  return result;
}
export async function busy(button, work) {
  const text = button.textContent;
  button.disabled = true;
  button.textContent = 'Working…';
  try { return await work(); }
  catch (error) { toast(error.message, true); }
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
