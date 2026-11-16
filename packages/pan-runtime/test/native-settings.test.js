import test from 'node:test';
import assert from 'node:assert/strict';
import { createPanService } from '../src/service.js';

const clone = value => structuredClone(value);
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
  let disk = clone(initial), observed = JSON.stringify(disk), closed = false;
  const writes = [];
  return {
    writes,
    read: () => clone(disk),
    async reload() { observed = JSON.stringify(disk); return clone(disk); },
    async write(document) {
      if (closed) throw Object.assign(new Error('CLOSED'), { code: 'CLOSED' });
      if (observed !== JSON.stringify(disk))
        throw Object.assign(new Error('STORAGE_CONFLICT'), { code: 'STORAGE_CONFLICT' });
      disk = clone(document); observed = JSON.stringify(disk); writes.push(clone(document));
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
const tv = { refreshToken: 'synthetic-refresh', deviceId: 'synthetic-device' };
const rotation = input => response(input, { code: 200, data: {
  access_token: 'synthetic-rotated-access', refresh_token: 'synthetic-rotated-refresh', expires_in: 3600,
} });
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

test('native reload uses the whole validated document and cancels old sessions, not unrelated accounts', async t => {
  const store = casStore({ version: 1, accounts: { quark_tv: record(1, tv) } });
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  const { service, requests } = setup(t, store, async input => {
    if (new URL(input.url).hostname === 'api.extscreen.com') {
      entered.resolve(); await release.promise;
      return rotation(input);
    }
    return qr(input);
  });
  const login = await service.invoke('login.start', { provider: 'baidu' });
  const refresh = assert.rejects(service.invoke('refresh', { provider: 'quark_tv' }), { code: 'STALE_ACCOUNT' });
  await entered.promise;
  store.native({ version: 1, accounts: {
    baidu: record(1, { cookie: 'synthetic-native' }),
    quark_tv: record(1, { ...tv, refreshToken: 'synthetic-native-rotated' }),
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
  const store = casStore({ version: 1, accounts: { quark_tv: record(1, tv) } });
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  let calls = 0;
  const { service } = setup(t, store, async input => {
    if (++calls === 1) { entered.resolve(); await release.promise; }
    return rotation(input);
  });
  const refresh = assert.rejects(service.invoke('refresh', { provider: 'quark_tv' }), { code: 'STALE_ACCOUNT' });
  await entered.promise;
  const latest = { version: 1, accounts: {
    quark_tv: record(1, tv), baidu: record(2, { cookie: 'synthetic-new-native' }),
  } };
  store.native(latest);
  release.resolve(); await refresh;
  assert.deepEqual(store.saved(), latest);
  assert.equal(store.writes.length, 0);
  assert.equal((await account(service, 'baidu')).revision, 2);
  await service.invoke('refresh', { provider: 'quark_tv' });
  assert.equal(store.writes.length, 1);
  assert.deepEqual(store.saved().accounts.baidu, latest.accounts.baidu);
  assert.equal(store.saved().accounts.quark_tv.credentials.refreshToken, 'synthetic-rotated-refresh');
});

test('notification reload and token write share one queue and preserve unrelated native edits', async t => {
  const store = casStore({ version: 1, accounts: { quark_tv: record(1, tv) } });
  const entered = deferred(), release = deferred(), requested = deferred();
  t.after(() => release.resolve());
  const reload = store.reload;
  store.reload = async () => { entered.resolve(); await release.promise; return reload(); };
  const { service } = setup(t, store, input => { requested.resolve(); return rotation(input); });
  store.native({ version: 1, accounts: {
    quark_tv: record(1, tv), uc: record(1, { cookie: 'synthetic-native-uc' }),
  } });
  const notification = service.settingsChanged();
  await entered.promise;
  const refresh = service.invoke('refresh', { provider: 'quark_tv' });
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
  const store = casStore({ version: 1, accounts: { quark_tv: record(1, tv) } });
  const entered = deferred(), release = deferred(), polling = deferred(), pollRelease = deferred();
  t.after(() => { release.resolve(); pollRelease.resolve(); });
  const write = store.write;
  store.write = async value => {
    if (value.accounts.quark_tv.credentials.accessToken && !entered.done) {
      entered.done = true; entered.resolve(); await release.promise;
    }
    return write(value);
  };
  const { service } = setup(t, store, async input => {
    if (new URL(input.url).hostname === 'api.extscreen.com') return rotation(input);
    if (new URL(input.url).pathname === '/channel/unicast') {
      polling.resolve(); await pollRelease.promise;
    }
    return qr(input);
  });
  const login = await service.invoke('login.start', { provider: 'baidu' });
  const refresh = service.invoke('refresh', { provider: 'quark_tv' });
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

test('reload validates every record before changing memory and can recover on a later valid notification', async t => {
  const initial = { version: 1, accounts: { baidu: record(1, { cookie: 'synthetic-old' }) } };
  const store = casStore(initial);
  const { service, requests } = setup(t, store);
  const before = await service.invoke('settings.get');
  for (const invalid of [
    { ...initial, extra: true }, { version: 2, accounts: {} }, { version: 1, accounts: [] },
    { version: 1, accounts: { baidu: record(0, {}) } },
    { version: 1, accounts: { baidu: record(9007199254740992, {}) } },
    { version: 1, accounts: { baidu: record(2, { cookie: 2 }) } },
    { version: 1, accounts: { yidong: record(2, { refreshedAt: '1' }) } },
    { version: 1, accounts: { baidu: record(2, { cookie: 'synthetic-new' }), unknown: record(1, {}) } },
  ]) {
    store.native(invalid);
    await assert.rejects(service.settingsChanged(), { code: 'CORRUPT_STORAGE' });
    assert.deepEqual(await service.invoke('settings.get'), before, 'no partial publication from a corrupt document');
    await assert.rejects(service.invoke('settings.clear', { provider: 'baidu' }), { code: 'CORRUPT_STORAGE' });
    assert.deepEqual(store.saved(), invalid, 'a changed CAS fingerprint cannot authorize overwriting a rejected document');
  }
  store.native(null);
  await service.settingsChanged();
  assert.equal((await account(service, 'baidu')).configured, false);
  assert.equal(requests.length, 0);
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
