const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

const pageSource = readFileSync('src/pages/dashboard/DashboardPage.vue', 'utf8');
const stripModules = (text) => text.replace(/^import .*\r?\n/gm, '').replace(/^export /gm, '');
const logicSource = stripModules(readFileSync('src/pages/dashboard/dashboardLogic.js', 'utf8'));
const requestSource = stripModules(readFileSync('src/shared/requestJson.js', 'utf8'));
const pageCode = pageSource.slice(
  pageSource.indexOf('function normalizeVideoSourceRow'),
  pageSource.indexOf('async function persistVideoSourceToggle')
);
const firstSite = { key: 'alpha', name: '站点 A', api: '/spider/alpha/3', enabled: true };
const secondSite = { key: 'beta', name: '站点 B', api: '/spider/beta/3', enabled: false };
const plain = (value) => JSON.parse(JSON.stringify(value));

function makePage(request, confirm = true) {
  const calls = [];
  const confirmations = [];
  const messages = [];
  const context = vm.createContext({
    videoLoading: { value: false }, videoImporting: { value: false }, videoSourceDeletingKey: { value: '' },
    videoSourceSites: { value: [{ ...firstSite }, { ...secondSite }] },
    videoSourceCoverSite: { value: 'alpha' }, selectedVideoSourceKeys: { value: ['alpha', 'beta'] },
    videoSourceNameWidthPx: { value: 80 }, videoSourceApiWidthPx: { value: 90 },
    window: { confirm: (message) => { confirmations.push(message); return confirm; } },
    deleteDashboardVideoSourceSite: async (key) => { calls.push(key); return request(key); },
    notifySuccess: (message) => messages.push({ type: 'success', message }),
    notifyError: (message) => messages.push({ type: 'error', message }),
  });
  // Execute the real row normalization, list/selection/cover application and
  // delete action. Only the API call and confirmation UI are mocked.
  vm.runInContext(pageCode, context);
  return { context, calls, confirmations, messages };
}

test('source rows expose a guarded delete action after the error column', () => {
  const error = pageSource.indexOf("{{ site.error || '' }}");
  const action = pageSource.indexOf('@click="deleteVideoSourceSite(site)"');
  assert.ok(action > error);
  const button = pageSource.slice(pageSource.lastIndexOf('<button', action), action);
  assert.match(button, /videoSourceDeletingKey/);
  assert.match(button, /videoImporting/);
  assert.match(button, /aria-label=/);
  assert.match(pageSource.slice(error, action), /btn-ghost-red/);
});

test('header and site rows share a minimum width that includes the delete column', () => {
  const context = vm.createContext({
    computed: (getter) => ({ get value() { return getter(); } }),
    videoSourceNameWidthPx: { value: 80 }, videoSourceApiWidthPx: { value: 90 },
  });
  const styleCode = pageSource.slice(
    pageSource.indexOf('const videoSourceRowStyle'),
    pageSource.indexOf('const allVideoSourceSelected')
  );
  vm.runInContext(`${styleCode}\nthis.rowStyle = videoSourceRowStyle;`, context);
  assert.equal(context.rowStyle.value.minWidth, 'calc(964px + 10rem)');
  context.videoSourceNameWidthPx.value = 420;
  context.videoSourceApiWidthPx.value = 360;
  assert.equal(context.rowStyle.value.minWidth, 'calc(1574px + 10rem)');
  assert.equal((pageSource.match(/:style="videoSourceRowStyle"/g) || []).length, 2);
});

test('delete API posts a trimmed key with credentials and propagates server errors', async () => {
  const calls = [];
  let failure = false;
  const context = vm.createContext({
    URLSearchParams,
    fetch: async (url, options) => {
      calls.push({ url, options });
      return {
        ok: !failure, status: failure ? 500 : 200,
        json: async () => failure ? { success: false, message: '删除站点失败' } : { success: true, sites: [] },
      };
    },
  });
  vm.runInContext(`const requestJsonResponse = (() => { ${requestSource}; return requestJsonResponse; })();\n${logicSource}`, context);
  await assert.rejects(context.deleteDashboardVideoSourceSite(' \n '), /key/);
  assert.equal(calls.length, 0);
  await context.deleteDashboardVideoSourceSite(' alpha ');
  assert.equal(calls[0].url, '/dashboard/video/source/sites/delete');
  assert.equal(calls[0].options.method, 'POST');
  assert.equal(calls[0].options.credentials, 'include');
  assert.equal(new URLSearchParams(calls[0].options.body).get('key'), 'alpha');
  failure = true;
  await assert.rejects(context.deleteDashboardVideoSourceSite('alpha'), /删除站点失败/);
});

test('cancelling confirmation does not call the API or change the list', async () => {
  const page = makePage(() => assert.fail('request must not run'), false);
  await page.context.deleteVideoSourceSite(firstSite);
  assert.deepEqual(page.calls, []);
  assert.deepEqual(plain(page.context.videoSourceSites.value), [firstSite, secondSite]);
  assert.equal(page.context.videoSourceCoverSite.value, 'alpha');
  assert.match(page.confirmations[0], /站点 A/);
  assert.match(page.confirmations[0], /重新导入/);
});

test('successful deletion applies remaining rows, selection and cover from the server', async () => {
  const page = makePage(async () => ({ success: true, sites: [secondSite], coverSite: 'beta' }));
  await page.context.deleteVideoSourceSite(firstSite);
  assert.deepEqual(page.calls, ['alpha']);
  assert.deepEqual(plain(page.context.videoSourceSites.value.map((site) => site.key)), ['beta']);
  assert.equal(page.context.videoSourceSites.value[0].enabled, false);
  assert.deepEqual(plain(page.context.selectedVideoSourceKeys.value), ['beta']);
  assert.equal(page.context.videoSourceCoverSite.value, 'beta');
  assert.equal(page.context.videoSourceDeletingKey.value, '');
  assert.equal(page.messages[0].type, 'success');
});

test('duplicate deletions are ignored and no row disappears before the response', async () => {
  let finish;
  const page = makePage(() => new Promise((resolve) => { finish = resolve; }));
  const pending = page.context.deleteVideoSourceSite(firstSite);
  await page.context.deleteVideoSourceSite(firstSite);
  await page.context.deleteVideoSourceSite(secondSite);
  assert.deepEqual(page.calls, ['alpha']);
  assert.equal(page.confirmations.length, 1);
  assert.equal(page.context.videoSourceDeletingKey.value, 'alpha');
  assert.equal(page.context.videoSourceSites.value.length, 2);
  finish({ success: true, sites: [secondSite], coverSite: 'beta' });
  await pending;
  assert.equal(page.context.videoSourceDeletingKey.value, '');
});

test('loading/importing and missing keys cannot trigger deletion', async () => {
  const page = makePage(() => assert.fail('request must not run'));
  page.context.videoLoading.value = true;
  await page.context.deleteVideoSourceSite(firstSite);
  page.context.videoLoading.value = false;
  page.context.videoImporting.value = true;
  await page.context.deleteVideoSourceSite(firstSite);
  page.context.videoImporting.value = false;
  await page.context.deleteVideoSourceSite({ key: '' });
  await page.context.deleteVideoSourceSite(null);
  assert.deepEqual(page.calls, []);
  assert.deepEqual(page.confirmations, []);
});

test('CatPawRunner import locks deletion before resolving the server address', async () => {
  const page = makePage(() => assert.fail('delete must not run while importing'));
  let finishResolve;
  let resolutions = 0;
  page.context.resolveVideoCatApiBase = () => {
    resolutions++;
    return new Promise((resolve) => { finishResolve = resolve; });
  };
  vm.runInContext(pageSource.slice(
    pageSource.indexOf('async function importVideoSourcesFromCatpawrunner'),
    pageSource.indexOf('function exportVideoSourcesToJson')
  ), page.context);
  page.context.videoSourceDeletingKey.value = 'alpha';
  await page.context.importVideoSourcesFromCatpawrunner();
  assert.equal(resolutions, 0);
  page.context.videoSourceDeletingKey.value = '';
  const importing = page.context.importVideoSourcesFromCatpawrunner();
  assert.equal(page.context.videoImporting.value, true);
  await page.context.deleteVideoSourceSite(firstSite);
  await page.context.importVideoSourcesFromCatpawrunner();
  assert.equal(resolutions, 1);
  assert.deepEqual(page.calls, []);
  finishResolve('');
  await importing;
  assert.equal(page.context.videoImporting.value, false);
});

test('JSON import and deletion are mutually exclusive and release the busy state', async () => {
  const page = makePage(() => assert.fail('delete must not run while importing'));
  let finishRead;
  let reads = 0;
  let picks = 0;
  const input = {
    value: 'sites.json',
    click: () => { picks++; },
    files: [{ text: () => {
      reads++;
      return new Promise((resolve) => { finishRead = resolve; });
    } }],
  };
  page.context.videoSourceImportFileRef = { value: input };
  vm.runInContext(pageSource.slice(
    pageSource.indexOf('function pickVideoSourceImportFile'),
    pageSource.indexOf('async function toggleVideoSourceEnabled')
  ), page.context);
  page.context.videoSourceDeletingKey.value = 'alpha';
  page.context.pickVideoSourceImportFile();
  await page.context.importVideoSourcesFromJson({ target: input });
  assert.equal(picks, 0);
  assert.equal(reads, 0);
  assert.equal(input.value, '');
  page.context.videoSourceDeletingKey.value = '';
  const importing = page.context.importVideoSourcesFromJson({ target: input });
  assert.equal(page.context.videoImporting.value, true);
  await page.context.deleteVideoSourceSite(firstSite);
  await page.context.importVideoSourcesFromJson({ target: input });
  assert.equal(reads, 1);
  assert.deepEqual(page.calls, []);
  finishRead('[]');
  await importing;
  assert.equal(page.context.videoImporting.value, false);
  assert.equal(input.value, '');
});

test('failed deletion keeps rows and selections intact, displays the error and unlocks retry', async () => {
  const page = makePage(async () => { throw new Error('删除站点失败'); });
  await page.context.deleteVideoSourceSite(firstSite);
  assert.deepEqual(plain(page.context.videoSourceSites.value), [firstSite, secondSite]);
  assert.deepEqual(plain(page.context.selectedVideoSourceKeys.value), ['alpha', 'beta']);
  assert.equal(page.context.videoSourceCoverSite.value, 'alpha');
  assert.equal(page.context.videoSourceDeletingKey.value, '');
  assert.deepEqual(page.messages, [{ type: 'error', message: '删除站点失败' }]);
});

test('deleting the final site clears its selected state and cover', async () => {
  const page = makePage(async () => ({ success: true, sites: [], coverSite: '' }));
  page.context.videoSourceSites.value = [firstSite];
  page.context.selectedVideoSourceKeys.value = ['alpha'];
  await page.context.deleteVideoSourceSite(firstSite);
  assert.deepEqual(plain(page.context.videoSourceSites.value), []);
  assert.deepEqual(plain(page.context.selectedVideoSourceKeys.value), []);
  assert.equal(page.context.videoSourceCoverSite.value, '');
});

test('a minimal successful response removes only the target, not the whole table', async () => {
  const page = makePage(async () => ({ success: true }));
  await page.context.deleteVideoSourceSite(firstSite);
  assert.deepEqual(plain(page.context.videoSourceSites.value.map((site) => site.key)), ['beta']);
  assert.deepEqual(plain(page.context.selectedVideoSourceKeys.value), ['beta']);
  assert.equal(page.context.videoSourceCoverSite.value, '');
});
