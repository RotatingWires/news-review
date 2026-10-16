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
export async function api(path, method = 'GET', data) {
  const response = await fetch('/api/' + path, {
    method, credentials: 'same-origin',
    headers: data === undefined ? {} : { 'Content-Type': 'application/json' },
    body: data === undefined ? undefined : JSON.stringify(data),
  });
  const result = await response.json();
  if (!response.ok) {
    if (response.status === 401 && path !== 'login' && !$('login-dialog').open) $('login-dialog').showModal();
    throw new Error(result.error || `Request failed (${response.status})`);
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
