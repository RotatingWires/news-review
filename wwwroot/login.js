/* The login page: setup token, password creation, and sign-in. */
const authForm = document.querySelector('#authForm');
const password = document.querySelector('#password');
const setupTokenGroup = document.querySelector('#setupTokenGroup');
const setupToken = document.querySelector('#setupToken');
const confirmGroup = document.querySelector('#confirmGroup');
const confirmPassword = document.querySelector('#confirmPassword');
const loginButton = document.querySelector('#loginButton');
const loginError = document.querySelector('#loginError');
const loginWindowTitle = document.querySelector('#loginWindowTitle');
const loginIntro = document.querySelector('#loginIntro');
const titleAppName = document.querySelector('.title-app-name');

let setupMode = false;

function loginReturnUrl() {
  const params = new URLSearchParams(location.search);
  const value = params.get('returnUrl') || params.get('ReturnUrl');
  if (!value || !value.startsWith('/') || value.startsWith('//')) return '/';
  try {
    const url = new URL(value, location.origin);
    return url.origin === location.origin ? `${url.pathname}${url.search}${url.hash}` : '/';
  } catch {
    return '/';
  }
}

async function request(url, options = {}) {
  const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', ...options });
  if (response.ok) return response.status === 204 ? null : response.json();

  let message = response.status === 401
    ? 'Incorrect password.'
    : response.status === 429
      ? 'Too many attempts. Try again in one minute.'
      : `${response.status} ${response.statusText}`;
  try {
    const body = await response.json();
    if (body.error) message = body.error;
  } catch {}
  throw new Error(message);
}

/** The release from /version.json, like the other apps; the server's version if that file can't be read. */
async function releaseVersion(fallback) {
  try {
    const response = await fetch('/version.json', { cache: 'no-store', credentials: 'same-origin' });
    if (response.ok) return (await response.json()).version || fallback;
  } catch {}
  return fallback;
}

async function initialize() {
  try {
    const status = await request('/api/auth/status');
    titleAppName.replaceChildren('NewsReview', Object.assign(document.createElement('span'), { className: 'title-version', textContent: ` v${await releaseVersion(status.version)}` }));
    if (status.authenticated) {
      window.location.replace(loginReturnUrl());
      return;
    }

    setupMode = !status.configured;
    if (setupMode) {
      loginWindowTitle.textContent = 'Create Password';
      loginIntro.textContent = 'First run: enter the setup token printed in the NewsReview server console, then create your password.';
      setupTokenGroup.hidden = false;
      setupToken.required = true;
      confirmGroup.hidden = false;
      confirmPassword.required = true;
      password.minLength = 8;
      confirmPassword.minLength = 8;
      password.autocomplete = 'new-password';
      loginButton.textContent = 'Create Password';
    }
    (setupMode ? setupToken : password).focus();
  } catch (error) {
    loginError.textContent = error.message;
  }
}

authForm.addEventListener('submit', async event => {
  event.preventDefault();
  loginError.textContent = '';
  loginButton.disabled = true;

  try {
    const body = setupMode
      ? { setupToken: setupToken.value, password: password.value, confirmPassword: confirmPassword.value }
      : { password: password.value };
    await request(setupMode ? '/api/auth/setup' : '/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    window.location.replace(loginReturnUrl());
  } catch (error) {
    loginError.textContent = error.message;
    const target = setupMode && error.message.toLowerCase().includes('setup token') ? setupToken : password;
    target.focus();
    target.select();
  } finally {
    loginButton.disabled = false;
  }
});

initialize();
