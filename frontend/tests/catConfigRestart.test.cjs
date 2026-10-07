const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const pageSource = readFileSync('src/pages/dashboard/DashboardPage.vue', 'utf8');
const logicSource = readFileSync('src/pages/dashboard/dashboardLogic.js', 'utf8');
const code = pageSource.slice(pageSource.indexOf('async function triggerCatConfigRestart'), pageSource.indexOf('function buildCatRemoteSettingsSnapshot'));
function makeContext(request) {
  const context = vm.createContext({
    catRemoteLoading: { value: false }, catSaving: { value: false }, catConfigRestartBusyKey: { value: '' },
    normalizedCatApiBase: { value: 'https://cat.example' },
    catOnlineConfigs: { value: [{ id: 'abc', name: 'unsaved name', status: 'error' }] },
    buildCatConfigActionKey: (item) => `id:${item.id}`,
    restartCatpawrunnerOnlineConfig: request,
    messages: [], notifySuccess(message) { context.messages.push(message); }, notifyError(message) { context.messages.push(message); },
  });
  vm.runInContext(`${code}\nthis.restart = triggerCatConfigRestart;`, context);
  return context;
}

test('restart button is between update and edit, with no configuration-status gate', () => {
  const update = pageSource.indexOf('@click="triggerCatConfigUpdate');
  const restart = pageSource.indexOf('@click="triggerCatConfigRestart');
  const edit = pageSource.indexOf('@click="openCatConfigEditorForEdit');
  assert.ok(update < restart && restart < edit);
  const button = pageSource.slice(pageSource.lastIndexOf('<button', restart), restart);
  assert.doesNotMatch(button, /item\.status|updateResult/);
});

test('restart uses its own endpoint and requires a saved id', async () => {
  const start = logicSource.indexOf('export async function restartCatpawrunnerOnlineConfig');
  const end = logicSource.indexOf('\nexport ', start + 1);
  let request;
  const context = vm.createContext({ requestCatpawrunnerAdminJson: async (data) => { request = data; return {}; } });
  vm.runInContext(logicSource.slice(start, end).replace('export ', ''), context);
  await assert.rejects(context.restartCatpawrunnerOnlineConfig('https://cat.example', ''), /ID/);
  assert.equal(request, undefined);
  await context.restartCatpawrunnerOnlineConfig('https://cat.example', ' abc ', 'admin');
  assert.equal(request.path, 'admin/online-configs/restart');
  assert.equal(request.method, 'POST');
  assert.equal(request.body.id, 'abc');
  assert.equal(request.tvUser, 'admin');
});

test('all statuses submit restart without overwriting unsaved config', async () => {
  for (const status of ['pass', 'error', 'checking', '']) {
    let called = false;
    const context = makeContext(async (base, id) => {
      assert.equal(base, 'https://cat.example'); assert.equal(id, 'abc'); called = true;
      return { pending: true, onlineConfigs: [{ id: 'abc', name: 'server name', status: 'checking' }] };
    });
    await context.restart({ id: 'abc', status }, 0);
    assert.equal(called, true);
    assert.equal(context.catOnlineConfigs.value[0].name, 'unsaved name');
    assert.equal(context.catOnlineConfigs.value[0].status, 'checking');
    assert.equal(context.catConfigRestartBusyKey.value, '');
    assert.equal(context.messages[0], '重启任务已提交');
  }
});

test('duplicate clicks are ignored; changing server discards stale response', async () => {
  let resolve;
  let calls = 0;
  const context = makeContext(() => { calls += 1; return new Promise((done) => { resolve = done; }); });
  const pending = context.restart({ id: 'abc' }, 0);
  await context.restart({ id: 'abc' }, 0);
  assert.equal(calls, 1);
  context.normalizedCatApiBase.value = 'https://another.example';
  resolve({ onlineConfigs: [{ id: 'abc', status: 'checking' }] });
  await pending;
  assert.equal(context.catOnlineConfigs.value[0].status, 'error');
  assert.equal(context.messages.length, 0);
  assert.equal(context.catConfigRestartBusyKey.value, '');
});

test('request failures are shown and busy state is released', async () => {
  const context = makeContext(async () => { throw new Error('HTTP 404'); });
  await context.restart({ id: 'abc' }, 0);
  assert.equal(context.messages[0], 'HTTP 404');
  assert.equal(context.catConfigRestartBusyKey.value, '');
});
