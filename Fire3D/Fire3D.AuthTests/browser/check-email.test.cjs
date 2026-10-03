const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(process.env.FET3D_OTP_PAGE_TEST_SOURCE ||
  path.resolve(__dirname, '../../Fire3D.API/wwwroot/check-email/app.js'), 'utf8');
function page(fetch) {
  const elements = new Map();
  const stored = new Map([['fet3d.registration.token', 'old-proof']]);
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      value: id === '#email' ? 'owner@example.test' : '', textContent: '', disabled: false, listeners: {},
      classList: { add() {}, remove() {} }, focus() {},
      addEventListener(event, callback) { this.listeners[event] = callback; }
    });
    return elements.get(id);
  }
  vm.runInNewContext(source, {
    document: { querySelector: element }, location: { href: 'https://example.test/check-email/' }, URL,
    fetch, TypeError, SyntaxError,
    setInterval() { return 1; }, clearInterval() {},
    sessionStorage: { setItem(k, v) { stored.set(k, v); }, removeItem(k) { stored.delete(k); } }
  });
  return { element, stored, submit: () => element('#request-form').listeners.submit({ preventDefault() {} }) };
}

test('duplicate email shows server field error instead of a generic delivery failure', async () => {
  const ui = page(async () => ({ status: 409, ok: false, json: async () => ({ code: 'EMAIL_EXISTS', errors: { email: ['Email đã được đăng ký.'] } }) }));
  await ui.submit();
  assert.equal(ui.element('#status').textContent, 'Email đã được đăng ký.');
  assert.equal(ui.stored.has('fet3d.registration.token'), false);
});

test('network failure shows friendly feedback instead of the raw fetch exception', async () => {
  const ui = page(async () => { throw new TypeError('Failed to fetch'); });
  await ui.submit();
  assert.match(ui.element('#status').textContent, /Không thể kết nối/);
  assert.doesNotMatch(ui.element('#status').textContent, /Failed to fetch/);
});

test('non-JSON gateway error does not show parser internals', async () => {
  const ui = page(async () => ({ status: 502, ok: false, json: async () => { throw new SyntaxError('Unexpected token <'); } }));
  await ui.submit();
  assert.match(ui.element('#status').textContent, /Không thể kết nối/);
  assert.doesNotMatch(ui.element('#status').textContent, /Unexpected token/);
});

test('editing the email during delivery invalidates proof and rejects the stale response', async () => {
  let release;
  const ui = page(() => new Promise(resolve => { release = resolve; }));
  const pending = ui.submit();
  ui.element('#email').value = 'changed@example.test';
  ui.element('#email').listeners.input();
  release({ status: 202, ok: true });
  await pending;
  assert.match(ui.element('#status').textContent, /Email đã thay đổi/);
  assert.equal(ui.stored.has('fet3d.registration.token'), false);
  assert.doesNotMatch(ui.element('#status').textContent, /email-changed/);
});
