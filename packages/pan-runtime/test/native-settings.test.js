import test from 'node:test';
import assert from 'node:assert/strict';
import { cloneJSONValue as clone, parseJSONText, stringifyJSONValue, rawJSON } from '@meowfilm/runtime-bridge/json';
import { createPanService } from '../src/service.js';

const empty = () => ({ version: 1, accounts: {} });
const record = (revision, credentials) => ({ revision, credentials });
const response = (input, value, headers = {}) => ({
  url: input.url, status: 200, headers, body: Buffer.from(JSON.stringify(value)),
});
const turn = () => new Promise(resolve => setImmediate(resolve));
function deferred() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
}
function casStore(initial = empty()) {
  let disk = clone(initial), observed = stringifyJSONValue(disk), closed = false;
  const writes = [];
  return {
    writes,
    read: () => clone(disk),
    async reload() { observed = stringifyJSONValue(disk); return clone(disk); },
    async write(document) {
      if (closed) throw Object.assign(new Error('CLOSED'), { code: 'CLOSED' });
      if (observed !== stringifyJSONValue(disk))
        throw Object.assign(new Error('STORAGE_CONFLICT'), { code: 'STORAGE_CONFLICT' });
      disk = clone(document); observed = stringifyJSONValue(disk); writes.push(clone(document));
      return clone(disk);
    },
    native(document) { disk = clone(document); },
    saved: () => clone(disk),
    async close() { closed = true; },
  };
}
function setup(t, store, handler = () => assert.fail('passive settings must not request upstream')) {
  const requests = [];
  const service = createPanService({ store, transport: {
    async request(input) { requests.push(input); return handler(input); },
    async close() {},
  } });
  t.after(() => service.close());
  return { service, requests };
}
async function account(service, id) {
  return (await service.invoke('settings.get')).accounts.find(value => value.provider === id);
}
const cookie = { cookie: 'session=synthetic-account' };
const rotation = input => {
  assert.equal(input.url, 'https://pan.quark.cn/account/info?fr=pc&platform=pc');
  assert.equal(input.method, 'GET');
  assert.equal(input.headers.Cookie, cookie.cookie);
  assert.equal(input.headers.cookie, undefined);
  assert.equal(input.headers.Authorization, undefined);
  assert.equal(input.body, undefined);
  return response(input, { code: 'OK', success: true, data: { nickname: 'synthetic' } },
    { 'set-cookie': ['session=synthetic-rotated; Path=/'] });
};
function qr(input) {
  const path = new URL(input.url).pathname;
  if (path === '/v2/api/getqrcode') return response(input, {
    errno: 0, sign: 'synthetic-sign', imgurl: 'https://passport.baidu.com/synthetic-image.png',
  });
  if (path === '/synthetic-image.png') return {
    url: input.url, status: 200, headers: { 'content-type': 'image/png' }, body: Buffer.from('synthetic-image'),
  };
  if (path === '/channel/unicast') return response(input, {
    errno: 0, channel_v: JSON.stringify({ status: 0, v: 'synthetic-ticket' }),
  });
  if (path === '/v3/login/main/qrbdusslogin')
    return response(input, {}, { 'set-cookie': ['BDUSS=synthetic-cookie; Domain=baidu.com; Path=/; Secure'] });
  assert.equal(path, '/');
  return response(input, {});
}

test('native reload cancels changed account sessions, not unrelated accounts', async t => {
  const store = casStore({ version: 1, accounts: { quark: record(1, cookie) } });
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const { service, requests } = setup(t, store, async input => {
    if (new URL(input.url).hostname === 'pan.quark.cn') {
      entered.resolve(); await release.promise;
      return rotation(input);
    }
    return qr(input);
  });
  const login = await service.invoke('login.start', { provider: 'baidu' });
  const refresh = assert.rejects(service.invoke('refresh', { provider: 'quark' }), { code: 'STALE_ACCOUNT' });
  await entered.promise;
  store.native({ version: 1, accounts: {
    baidu: record(1, { cookie: 'synthetic-native' }),
    quark: record(1, { cookie: 'session=synthetic-native-rotated' }),
  } });
  await service.settingsChanged();
  assert.equal(requests.at(-1).signal.aborted, true);
  assert.equal((await service.invoke('login.poll', { sessionId: login.sessionId })).status, 'cancelled');
  release.resolve(); await refresh;
  assert.equal(store.writes.length, 0);
  assert.equal((await account(service, 'baidu')).revision, 1);
  assert.equal((await account(service, 'uc')).configured, false);
});

test('CAS conflict from an unrelated native edit never overwrites either account or blind-retries', async t => {
  const store = casStore({ version: 1, accounts: { quark: record(1, cookie) } });
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  let calls = 0;
  const { service } = setup(t, store, async input => {
    if (++calls === 1) { entered.resolve(); await release.promise; }
    return rotation(input);
  });
  const refresh = assert.rejects(service.invoke('refresh', { provider: 'quark' }), { code: 'STALE_ACCOUNT' });
  await entered.promise;
  const latest = { version: 1, accounts: {
    quark: record(1, cookie), baidu: record(2, { cookie: 'synthetic-new-native' }),
  } };
  store.native(latest);
  release.resolve(); await refresh;
  assert.deepEqual(store.saved(), latest);
  assert.equal(store.writes.length, 0);
  assert.equal((await account(service, 'baidu')).revision, 2);
  await service.invoke('refresh', { provider: 'quark' });
  assert.equal(store.writes.length, 1);
  assert.deepEqual(store.saved().accounts.baidu, latest.accounts.baidu);
  assert.equal(store.saved().accounts.quark.credentials.cookie, 'session=synthetic-rotated');
});

test('notification reload and Cookie write share one queue and preserve unrelated native edits', async t => {
  const store = casStore({ version: 1, accounts: { quark: record(1, cookie) } });
  const entered = deferred(), release = deferred(), requested = deferred();
  t.after(() => release.resolve());
  const reload = store.reload;
  store.reload = async () => { entered.resolve(); await release.promise; return reload(); };
  const { service } = setup(t, store, input => { requested.resolve(); return rotation(input); });
  store.native({ version: 1, accounts: {
    quark: record(1, cookie), uc: record(1, { cookie: 'synthetic-native-uc' }),
  } });
  const notification = service.settingsChanged();
  await entered.promise;
  const refresh = service.invoke('refresh', { provider: 'quark' });
  await requested.promise; await turn();
  assert.equal(store.writes.length, 0, 'write must not pass the pending native reload');
  release.resolve();
  await Promise.all([notification, refresh]);
  assert.equal(store.writes.length, 1);
  assert.deepEqual(store.saved().accounts.uc, record(1, { cookie: 'synthetic-native-uc' }));
});

test('notification cancels a late QR startup before it can register an orphan challenge', async t => {
  const store = casStore();
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const { service, requests } = setup(t, store, async input => {
    entered.resolve(); await release.promise;
    return qr(input);
  });
  const start = assert.rejects(service.invoke('login.start', { provider: 'baidu' }), { code: 'STALE_ACCOUNT' });
  await entered.promise;
  const latest = { version: 1, accounts: { baidu: record(1, { cookie: 'synthetic-native' }) } };
  store.native(latest);
  await service.settingsChanged();
  assert.equal(requests[0].signal.aborted, true);
  release.resolve(); await start;
  assert.equal(requests.length, 1);
  assert.deepEqual(store.saved(), latest);
});

test('a CAS-rejected QR confirmation reloads newer native credentials and stays cancelled', async t => {
  const store = casStore();
  const { service } = setup(t, store, qr);
  const login = await service.invoke('login.start', { provider: 'baidu' });
  const latest = { version: 1, accounts: { baidu: record(2, { cookie: 'synthetic-newer-native' }) } };
  store.native(latest); // Deliberately delay the notification until QR tries its CAS.
  await assert.rejects(service.invoke('login.poll', { sessionId: login.sessionId }), { code: 'STALE_ACCOUNT' });
  assert.deepEqual(store.saved(), latest);
  assert.equal(store.writes.length, 0);
  assert.equal((await service.invoke('login.poll', { sessionId: login.sessionId })).status, 'cancelled');
});

test('QR confirmation checks its original account again when a queued replacement is admitted', async t => {
  const store = casStore({ version: 1, accounts: { quark: record(1, cookie) } });
  const entered = deferred(), release = deferred(), polling = deferred(), pollRelease = deferred();
  t.after(() => { release.resolve(); pollRelease.resolve(); });
  const write = store.write;
  store.write = async value => {
    if (value.accounts.quark.credentials.cookie === 'session=synthetic-rotated' && !entered.done) {
      entered.done = true; entered.resolve(); await release.promise;
    }
    return write(value);
  };
  const { service } = setup(t, store, async input => {
    if (new URL(input.url).hostname === 'pan.quark.cn') return rotation(input);
    if (new URL(input.url).pathname === '/channel/unicast') {
      polling.resolve(); await pollRelease.promise;
    }
    return qr(input);
  });
  const login = await service.invoke('login.start', { provider: 'baidu' });
  const refresh = service.invoke('refresh', { provider: 'quark' });
  await entered.promise;
  const poll = assert.rejects(service.invoke('login.poll', { sessionId: login.sessionId }), { code: 'STALE_ACCOUNT' });
  await polling.promise;
  const clear = service.invoke('settings.clear', { provider: 'baidu' });
  await turn(); // Admit clear ahead of the still-upstream QR confirmation.
  pollRelease.resolve(); await turn();
  release.resolve();
  await Promise.all([refresh, clear, poll]);
  assert.deepEqual(store.saved().accounts.baidu, record(1, {}));
  assert.equal(store.writes.length, 2, 'only refresh and clear commit, never the stale QR');
});

test('reload rejects corrupt roots without publishing old state or overwriting and can recover later', async t => {
  const initial = { version: 1, accounts: { baidu: record(1, { cookie: 'synthetic-old' }) } };
  const store = casStore(initial);
  const { service, requests } = setup(t, store);
  for (const invalid of [
    [], {}, { version: 2, accounts: {} }, { version: 1, accounts: [] },
  ]) {
    store.native(invalid);
    await assert.rejects(service.settingsChanged(), { code: 'CORRUPT_STORAGE' });
    await assert.rejects(service.invoke('settings.get'), { code: 'CORRUPT_STORAGE' });
    await assert.rejects(service.invoke('settings.clear', { provider: 'baidu' }), { code: 'CORRUPT_STORAGE' });
    assert.deepEqual(store.saved(), invalid, 'a changed CAS fingerprint cannot authorize overwriting a rejected document');
  }
  store.native(null);
  await service.settingsChanged();
  assert.equal((await account(service, 'baidu')).configured, false);
  assert.equal(requests.length, 0);
});

test('bad account records stay visible and preserved while healthy accounts refresh and change', async t => {
  for (const bad of [
    null, [], 'broken', record(0, {}), record(9007199254740992, {}),
    record(2, null), record(2, { cookie: 2 }), record(2, { cookie: 'bad\r\nheader' }),
  ]) {
    const store = casStore({ version: 1, extra: true, accounts: {
      baidu: bad, quark: record(1, cookie), unknown: { future: true },
    } });
    const { service, requests } = setup(t, store, rotation);
    assert.deepEqual(await account(service, 'baidu'), { provider: 'baidu', errorCode: 'CORRUPT_STORAGE' });
    await assert.rejects(service.invoke('refresh', { provider: 'baidu' }), { code: 'CORRUPT_STORAGE' });
    assert.equal(requests.length, 0);
    assert.equal((await service.invoke('refresh', { provider: 'quark' })).refreshed, true);
    await service.invoke('settings.clear', { provider: 'uc' });
    assert.deepEqual(store.saved().accounts.baidu, bad);
    assert.deepEqual(store.saved().accounts.unknown, { future: true });
    assert.equal(store.saved().extra, true);
    assert.equal(store.writes.length, 2);
  }
});

test('reloading a bad sibling neither cancels healthy work nor loses its record on Cookie commit', async t => {
  const store = casStore({ version: 1, accounts: { quark: record(1, cookie) } });
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const { service, requests } = setup(t, store, async input => {
    entered.resolve(); await release.promise; return rotation(input);
  });
  const refresh = service.invoke('refresh', { provider: 'quark' });
  await entered.promise;
  const bad = record(2, { refreshedAt: 'not-a-timestamp' });
  store.native({ version: 1, accounts: { quark: record(1, cookie), yidong: bad } });
  await service.settingsChanged();
  assert.equal(requests[0].signal.aborted, false);
  assert.deepEqual(await account(service, 'yidong'), { provider: 'yidong', errorCode: 'CORRUPT_STORAGE' });
  release.resolve();
  assert.equal((await refresh).refreshed, true);
  assert.deepEqual(store.saved().accounts.yidong, bad);
});

test('native numeric extensions survive reload and rotation without acquiring credential authority', async t => {
  const material = parseJSONText(`{"large":9007199254740993,"decimal":1.00000000000000001,
    "overflow":1e400,"tiny":1e-400,"negativeZero":-0,"nested":{"rawJSON":"ordinary text"}}`);
  const initial = { version: 1, future: material, accounts: {
    baidu: record(3, { cookie: 'synthetic-other', extra: material }),
    quark: { ...record(1, { ...cookie, extra: material, authorization: 'must-not-be-used',
      headers: { Authorization: 'must-not-be-used' } }), future: material },
  } };
  const store = casStore(initial), entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const { service, requests } = setup(t, store, async input => {
    assert.equal(input.headers.Authorization, undefined);
    assert.equal(input.headers.cookie, undefined);
    assert.equal(input.headers.Cookie, cookie.cookie);
    assert.equal(input.body, undefined);
    entered.resolve(); await release.promise;
    return rotation(input);
  });
  const refresh = service.invoke('refresh', { provider: 'quark' });
  await entered.promise;
  await service.settingsChanged();
  assert.equal(requests[0].signal.aborted, false, 'rawJSON clones retain stable numeric identity');
  release.resolve(); await refresh;
  const saved = store.saved();
  for (const value of [saved.future, saved.accounts.quark.future, saved.accounts.quark.credentials.extra])
    assert.equal(stringifyJSONValue(value), stringifyJSONValue(material));
  assert.equal(stringifyJSONValue(saved.accounts.baidu), stringifyJSONValue(initial.accounts.baidu));
  assert.deepEqual((await account(service, 'quark')).fields, ['cookie']);
  await service.invoke('settings.set', { provider: 'quark', credentials: { cookie: 'session=synthetic-new' } });
  assert.deepEqual(store.saved().accounts.quark.credentials, { cookie: 'session=synthetic-new' });
  assert.equal(stringifyJSONValue(store.saved().accounts.quark.future), stringifyJSONValue(material));
  await service.invoke('settings.clear', { provider: 'quark' });
  assert.deepEqual(store.saved().accounts.quark.credentials, {});
  assert.equal(store.saved().accounts.quark.revision, 3);
});

test('valid revision permits explicit repair of bad credentials but never invents a bad revision', async t => {
  const store = casStore({ version: 1, accounts: { baidu: record(2, null), uc: record(0, {}) } });
  const { service } = setup(t, store);
  assert.equal((await service.invoke('settings.clear', { provider: 'baidu' })).revision, 3);
  await assert.rejects(service.invoke('settings.clear', { provider: 'uc' }), { code: 'CORRUPT_STORAGE' });
  assert.deepEqual(store.saved().accounts.uc, record(0, {}));
});

test('file consumption checks its own account revision and never borrows another provider account', async t => {
  const store = casStore({ version: 1, accounts: {
    quark: record(0, {}), uc: record(1, cookie),
  } });
  const { service, requests } = setup(t, store);
  await assert.rejects(service.invoke('play', { provider: 'quark', mode: 'download',
    file: { kind: 'stored', fileId: 'synthetic-file', provider: 'quark', accountRevision: 0 } }),
  { code: 'CORRUPT_STORAGE' });
  await assert.rejects(service.invoke('play', { provider: 'uc', mode: 'download',
    file: { kind: 'stored', fileId: 'synthetic-file', provider: 'quark', accountRevision: 1 } }),
  { code: 'INVALID_REQUEST' });
  assert.equal(requests.length, 0);
  assert.equal(store.writes.length, 0);
});

test('Tianyi consumes usable Cookie without inspecting unused password or sending unknown credentials', async t => {
  const credentials = { cookie: 'session=synthetic', password: { broken: true },
    authorization: 'must-not-be-used', headers: { Cookie: 'must-not-be-used' } };
  const store = casStore({ version: 1, accounts: { tianyi: record(1, credentials) } });
  const { service, requests } = setup(t, store, input => {
    const path = new URL(input.url).pathname;
    assert.equal(input.headers.Authorization, undefined);
    if (path === '/api/portal/loginUrl.action') return { ...response(input, ''), status: 302,
      headers: { location: '/web/main', 'set-cookie': ['session=synthetic-rotated; Path=/'] } };
    if (path === '/web/main') {
      assert.equal(input.headers.Cookie, 'session=synthetic-rotated');
      return response(input, 'synthetic account page');
    }
    assert.equal(path.endsWith('getShareInfoByCodeV2.action'), true);
    assert.equal(input.headers.Cookie ?? input.headers.cookie, credentials.cookie);
    return response(input, { res_code: 0, shareId: 'synthetic-share', fileId: 'synthetic-file',
      fileName: 'Original.mp4', isFolder: false, shareMode: '3' });
  });
  assert.deepEqual(await account(service, 'tianyi'), { provider: 'tianyi', errorCode: 'CORRUPT_STORAGE' });
  const result = await service.invoke('list', { provider: 'tianyi',
    share: { url: 'https://cloud.189.cn/t/SyntheticCode' } });
  assert.equal(result.files[0].name, 'Original.mp4');
  assert.equal(result.files[0].file.accountRevision, 1);
  assert.equal(requests.length, 1);
  assert.deepEqual(store.saved().accounts.tianyi.credentials, credentials, 'listing does not rotate Cookie');
  const refreshed = await service.invoke('refresh', { provider: 'tianyi' });
  assert.equal(refreshed.refreshed, true);
  assert.deepEqual(refreshed.account, { provider: 'tianyi', errorCode: 'CORRUPT_STORAGE' });
  assert.equal(requests.length, 3);
  assert.deepEqual(store.saved().accounts.tianyi.credentials.password, credentials.password);
  assert.deepEqual(store.saved().accounts.tianyi.credentials.headers, credentials.headers);
  assert.equal(store.saved().accounts.tianyi.credentials.cookie, 'session=synthetic-rotated');
});

test('all bad accounts produce explicit item failures, never empty successful account states', async t => {
  const ids = ['baidu', 'quark', 'uc', 'tianyi', 'yidong'];
  const initial = { version: 1, accounts: Object.fromEntries(ids.map(id => [id, null])) };
  const store = casStore(initial), { service, requests } = setup(t, store);
  assert.deepEqual((await service.invoke('settings.get')).accounts,
    ids.map(provider => ({ provider, errorCode: 'CORRUPT_STORAGE' })));
  for (const provider of ids)
    await assert.rejects(service.invoke('refresh', { provider }), { code: 'CORRUPT_STORAGE' });
  assert.equal(requests.length, 0);
  assert.deepEqual(store.saved(), initial);
  assert.equal(store.writes.length, 0);
});

test('Yidong refresh repairs its output timestamp without prechecking the stale optional timestamp', async t => {
  const authorization = 'synthetic-prefix:synthetic-account:synthetic-token';
  const store = casStore({ version: 1, accounts: {
    yidong: record(1, { authorization, refreshedAt: 'broken' }),
  } });
  const { service, requests } = setup(t, store, input => {
    assert.equal(input.url, 'https://aas.caiyun.feixin.10086.cn:443/tellin/authTokenRefresh.do');
    assert.equal(input.method, 'POST');
    assert.equal(input.headers['Content-Type'], 'application/xml');
    assert.equal(input.headers.Authorization, undefined);
    assert.equal(input.headers.Cookie, undefined);
    assert.equal(input.body, '<root><token>synthetic-token</token><account>synthetic-account</account><clienttype>656</clienttype></root>');
    return { ...response(input, ''), body: Buffer.from('<root><return>0</return><token>synthetic-rotated</token></root>') };
  });
  assert.deepEqual(await account(service, 'yidong'), { provider: 'yidong', errorCode: 'CORRUPT_STORAGE' });
  const result = await service.invoke('refresh', { provider: 'yidong' });
  assert.equal(result.account.status, 'signedIn');
  assert.equal(store.saved().accounts.yidong.credentials.authorization,
    Buffer.from('synthetic-prefix:synthetic-account:synthetic-rotated').toString('base64'));
  assert.equal(Number.isSafeInteger(store.saved().accounts.yidong.credentials.refreshedAt), true);
  assert.equal(store.saved().accounts.yidong.credentials.refreshedAt > 0, true);
  assert.equal(store.saved().accounts.yidong.revision, 1);
  assert.equal(requests.length, 1);
  assert.equal(store.writes.length, 1);
});

test('raw numeric material and a rawJSON-named ordinary object are distinct reload identities', async t => {
  const store = casStore({ version: 1, accounts: {
    quark: record(1, { ...cookie, extra: rawJSON('9007199254740993') }),
  } });
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const { service, requests } = setup(t, store, async input => {
    entered.resolve(); await release.promise; return rotation(input);
  });
  const refresh = assert.rejects(service.invoke('refresh', { provider: 'quark' }), { code: 'STALE_ACCOUNT' });
  await entered.promise;
  store.native({ version: 1, accounts: {
    quark: record(1, { ...cookie, extra: { rawJSON: '9007199254740993' } }),
  } });
  await service.settingsChanged();
  assert.equal(requests[0].signal.aborted, true);
  release.resolve(); await refresh;
  assert.equal(store.writes.length, 0);
});

test('bad document roots revoke active work and never let late rotation commit', async t => {
  const store = casStore({ version: 1, accounts: { quark: record(1, cookie) } });
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const { service, requests } = setup(t, store, async input => {
    entered.resolve(); await release.promise; return rotation(input);
  });
  const refresh = assert.rejects(service.invoke('refresh', { provider: 'quark' }), { code: 'CANCELLED' });
  await entered.promise;
  store.native({ version: 1, accounts: [] });
  await assert.rejects(service.settingsChanged(), { code: 'CORRUPT_STORAGE' });
  assert.equal(requests[0].signal.aborted, true);
  release.resolve(); await refresh;
  await service.close();
  assert.equal(store.writes.length, 0);
});

test('cancel and close fence queued writes but shutdown drains a previously admitted native commit', async t => {
  const store = casStore();
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const original = store.write;
  store.write = async document => { entered.resolve(); await release.promise; return original(document); };
  const { service } = setup(t, store);
  const admitted = service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'synthetic-admitted' } });
  await entered.promise;
  const cancel = new AbortController();
  const queued = assert.rejects(service.invoke('settings.clear', { provider: 'baidu' }, { signal: cancel.signal }),
    { code: 'CANCELLED' });
  await turn(); cancel.abort(); release.resolve();
  await admitted; await queued;
  assert.equal(store.writes.length, 1);
  // Hold a second admitted receipt across shutdown.
  const nextEntered = deferred(), nextRelease = deferred();
  t.after(() => nextRelease.resolve());
  store.write = async document => { nextEntered.resolve(); await nextRelease.promise; return original(document); };
  const second = service.invoke('settings.clear', { provider: 'baidu' });
  await nextEntered.promise;
  let finished = false;
  const close = service.close().then(() => { finished = true; });
  await turn();
  assert.equal(finished, false);
  nextRelease.resolve();
  await Promise.all([second, close]);
  assert.deepEqual(store.saved().accounts.baidu, record(2, {}));
  await assert.rejects(service.settingsChanged(), { code: 'CLOSED' });
});

test('failed native write preserves credentials and reports a fixed storage error, never raw secrets', async t => {
  const store = casStore({ version: 1, accounts: { baidu: record(1, { cookie: 'synthetic-old' }) } });
  store.write = async () => { throw new Error('synthetic-private-details'); };
  const { service } = setup(t, store);
  const before = await account(service, 'baidu');
  for (const operation of ['settings.set', 'settings.clear']) {
    await assert.rejects(service.invoke(operation, { provider: 'baidu', credentials: { cookie: 'synthetic-new' } }),
      error => error.code === 'STORAGE_ERROR' && error.message === 'STORAGE_ERROR' && error.cause === undefined);
    assert.deepEqual(await account(service, 'baidu'), before);
  }
});

test('native read write and close failures expose fixed lifecycle errors only', async t => {
  const store = casStore();
  store.read = () => { throw Object.assign(new Error('synthetic-private-path'), { code: 'STORAGE_ERROR' }); };
  assert.throws(() => createPanService({ store }), error =>
    error.code === 'STORAGE_ERROR' && error.message === 'STORAGE_ERROR');
  store.read = () => empty();
  const { service } = setup(t, store);
  store.write = async () => { throw Object.assign(new Error('synthetic-closed-pipe'), { code: 'CLOSED' }); };
  await assert.rejects(service.invoke('settings.clear', { provider: 'baidu' }), { code: 'CLOSED', message: 'CLOSED' });
  store.reload = async () => { throw Object.assign(new Error('synthetic-closed-pipe'), { code: 'CLOSED' }); };
  await assert.rejects(service.settingsChanged(), { code: 'CLOSED', message: 'CLOSED' });
  const brokenStore = casStore();
  brokenStore.close = async () => { throw new Error('synthetic-private-close'); };
  const broken = createPanService({ store: brokenStore });
  await assert.rejects(broken.close(), { code: 'STORAGE_ERROR', message: 'STORAGE_ERROR' });
});
