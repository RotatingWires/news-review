import { el } from './shared.js';

const controls = new WeakMap();
let serial = 0;
let opened = null;

function closeMenu(focus = false) {
  if (!opened) return;
  const { menu, trigger } = opened;
  menu.hidden = true;
  trigger.setAttribute('aria-expanded', 'false');
  opened = null;
  if (focus) trigger.focus();
}

document.addEventListener('click', event => {
  if (opened && !opened.wrapper.contains(event.target)) closeMenu();
});
document.addEventListener('focusin', event => {
  if (opened && !opened.wrapper.contains(event.target)) closeMenu();
});
document.addEventListener('close', () => closeMenu(), true);

function build(field) {
  const combo = field.tagName === 'INPUT';
  const source = combo ? field.list : field;
  const label = field.getAttribute('aria-label') || field.closest('label')?.firstChild.textContent.trim() || 'Choose option';
  if (!field.hasAttribute('aria-label')) field.setAttribute('aria-label', label);
  const wrapper = el('span', 'custom-dropdown' + (combo ? ' editable-dropdown' : ''));
  const trigger = el('button', 'dropdown-trigger');
  trigger.type = 'button';
  trigger.setAttribute('aria-label', combo ? 'Choose default category' : label);
  trigger.setAttribute('aria-haspopup', 'listbox');
  trigger.setAttribute('aria-expanded', 'false');
  const text = el('span', 'dropdown-label');
  const arrow = el('span', 'dropdown-arrow', '▼');
  arrow.setAttribute('aria-hidden', 'true');
  if (!combo) trigger.append(text);
  trigger.append(arrow);
  const menu = el('span', 'dropdown-menu');
  menu.id = 'dropdown' + ++serial;
  menu.hidden = true;
  menu.setAttribute('role', 'listbox');
  menu.setAttribute('aria-label', label);
  trigger.setAttribute('aria-controls', menu.id);
  field.before(wrapper);
  if (combo) {
    field.removeAttribute('list');
    wrapper.append(field);
  } else {
    field.classList.add('native-dropdown');
    field.tabIndex = -1;
    field.setAttribute('aria-hidden', 'true');
    wrapper.append(field);
  }
  wrapper.append(trigger, menu);
  const sync = () => {
    if (!combo) text.textContent = field.selectedOptions[0]?.textContent || '';
    for (const choice of menu.children) choice.setAttribute('aria-selected', String(choice.dataset.value === field.value));
  };
  // Options can change after the first build (the combo's datalist follows the managed topic list).
  const render = () => menu.replaceChildren(...[...source.options].map(option => {
    const choice = el('button', 'dropdown-option');
    choice.type = 'button';
    choice.setAttribute('role', 'option');
    choice.dataset.value = option.value;
    const check = el('span', 'dropdown-check', '✓');
    check.setAttribute('aria-hidden', 'true');
    choice.append(check, el('span', '', option.textContent || option.value));
    choice.addEventListener('click', event => {
      event.preventDefault();
      field.value = option.value;
      sync();
      field.dispatchEvent(new Event('input', { bubbles: true }));
      field.dispatchEvent(new Event('change', { bubbles: true }));
      closeMenu();
      (combo ? field : trigger).focus();
    });
    return choice;
  }));
  const open = () => {
    closeMenu();
    opened = { wrapper, trigger, menu };
    menu.hidden = false;
    trigger.setAttribute('aria-expanded', 'true');
    sync();
  };
  trigger.addEventListener('click', event => { event.preventDefault(); opened?.trigger === trigger ? closeMenu() : open(); });
  // Arrow keys, Home, End, and Escape come from dropdown.js, shared with the other apps. An editable field opens its
  // list with Arrow Down too.
  if (combo) field.addEventListener('keydown', event => {
    if (event.key !== 'ArrowDown' || opened?.trigger === trigger) return;
    event.preventDefault();
    open();
    menu.querySelector('.dropdown-option')?.focus();
  });
  field.addEventListener('change', sync);
  field.addEventListener('input', sync);
  controls.set(field, () => { render(); sync(); });
  render();
  sync();
}

export function syncDropdowns(root = document) {
  root.querySelectorAll('select, #sourceCategory').forEach(field => {
    if (!controls.has(field)) build(field);
    else controls.get(field)();
  });
}
