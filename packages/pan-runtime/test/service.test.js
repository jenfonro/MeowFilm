import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, renameSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPanService } from '../src/index.js';

const area = fileURLToPath(new URL('../../../tmp/test-results/pan-runtime/', import.meta.url));
const turn = () => new Promise(resolve => setImmediate(resolve));
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function response(value, headers = {}) {
  return { status: 200, headers, body: Buffer.from(JSON.stringify(value)) };
}
function setup(t, handler = () => assert.fail('passive state must not use the network'), options = {}) {
  mkdirSync(area, { recursive: true });
  const root = mkdtempSync(join(area, 'case-')), requests = [];
  let closes = 0;
  const transport = {
    async request(input) { requests.push(input); return { url: input.url, ...await handler(input) }; },
    async close() { closes++; },
  };
  const service = createPanService({ dataRoot: root, transport, ...options });
  t.after(async () => { await service.close(); rmSync(root, { recursive: true, force: true }); });
  return { service, root, requests, transport, get closes() { return closes; } };
}
const credentials = { accessToken: 'synthetic-access', refreshToken: 'synthetic-refresh',
  deviceId: 'synthetic-device', expiresAt: Date.now() + 3600000 };
const stored = (revision = 1, provider = 'quark_tv') =>
  ({ kind: 'stored', fileId: 'exact-fid', provider, accountRevision: revision });

test('seven providers expose actual capabilities and configurations persist in plaintext without secret readback', async t => {
  const { service, root, transport } = setup(t);
  const available = (await service.invoke('providers')).providers;
  assert.deepEqual(available.map(item => item.provider),
    ['baidu', 'quark', 'uc', 'tianyi', 'yidong', 'quark_tv', 'uc_tv']);
  assert.equal(available.every(item => item.refresh), true);
  assert.equal(available.find(item => item.provider === 'quark_tv').list, false);
  assert.equal(available.find(item => item.provider === 'uc_tv').prepare, false);
  const secret = ' cookie=synthetic-private-value ';
  const saved = await service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: secret } });
  assert.equal(saved.revision, 1);
  assert.equal(saved.configured, true);
  assert.deepEqual(saved.fields, ['cookie']);
  assert.ok(!JSON.stringify(saved).includes(secret));
  const state = await service.invoke('settings.get');
  assert.ok(!JSON.stringify(state).includes(secret));
  const bytes = readFileSync(join(root, 'pan/accounts.json'), 'utf8');
  assert.equal(JSON.parse(bytes).accounts.baidu.credentials.cookie, secret);
  assert.equal(JSON.parse(bytes).accounts.baidu.revision, 1);
  await service.close();
  const recovered = createPanService({ dataRoot: root, transport });
  try {
    assert.deepEqual(await recovered.invoke('settings.get'), state);
    assert.deepEqual(await recovered.invoke('settings.clear', { provider: 'baidu' }),
      { provider: 'baidu', revision: 2, configured: false, fields: [], status: 'signedOut' });
    assert.equal(JSON.parse(readFileSync(join(root, 'pan/accounts.json'), 'utf8')).accounts.baidu.revision, 2);
  } finally { await recovered.close(); }
});

test('invalid credentials, unsupported operations and stale references reject before network effects', async t => {
  const { service, requests } = setup(t);
  for (const input of [
    { provider: 'baidu', credentials: { password: 'not-a-supported-field' } },
    { provider: 'unknown', credentials: {} },
    { provider: 'quark', credentials: { cookie: 'x\r\nInjected: yes' } },
    { provider: 'uc_tv', credentials: { expiresAt: -1 } },
  ]) await assert.rejects(service.invoke('settings.set', input), { code: 'INVALID_REQUEST' });
  await service.invoke('settings.set', { provider: 'quark_tv', credentials });
  await assert.rejects(service.invoke('list', { provider: 'quark_tv', share: { url: 'https://pan.quark.cn/s/abcd' } }),
    { code: 'UNSUPPORTED_OPERATION' });
  await assert.rejects(service.invoke('play', { provider: 'quark_tv', file: stored(0), mode: 'streaming' }),
    { code: 'STALE_ACCOUNT' });
  await assert.rejects(service.invoke('play', { provider: 'quark_tv', file: { ...stored(), kind: 'share' }, mode: 'streaming' }),
    { code: 'INVALID_REQUEST' });
  await assert.rejects(service.invoke('play', { provider: 'quark_tv', file: stored(), mode: 'automatic' }),
    { code: 'UNSUPPORTED_OPERATION' });
  assert.equal(requests.length, 0);
});

test('failed atomic configuration write does not publish new account state; damaged documents are not reset', async t => {
  const { service, root } = setup(t);
  await service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'old-synthetic' } });
  const path = join(root, 'pan/accounts.json'), backup = join(root, 'pan/test-backup');
  renameSync(path, backup);
  mkdirSync(path);
  await assert.rejects(service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'new-synthetic' } }),
    { code: 'STORAGE_ERROR' });
  assert.equal((await service.invoke('settings.get')).accounts.find(item => item.provider === 'baidu').revision, 1);
  rmSync(path, { recursive: true }); renameSync(backup, path);
  await service.close();
  writeFileSync(path, '{"version":1,"accounts":{"baidu":{"revision":0,"credentials":{}}}}');
  const before = readFileSync(path);
  assert.throws(() => createPanService({ dataRoot: root }), { code: 'CORRUPT_STORAGE' });
  assert.deepEqual(readFileSync(path), before);
});

test('parallel token refreshes share one upstream request and persist rotation before reporting completion', async t => {
  const entered = deferred(), release = deferred();
  const { service, requests, root } = setup(t, async input => {
    assert.equal(new URL(input.url).href, 'http://api.extscreen.com/quarkdrive/token');
    assert.equal(JSON.parse(input.body.toString()).refresh_token, 'synthetic-refresh');
    entered.resolve(); await release.promise;
    return response({ code: 200, data: { access_token: 'rotated-access', refresh_token: 'rotated-refresh', expires_in: 7200 } });
  });
  await service.invoke('settings.set', { provider: 'quark_tv', credentials });
  const calls = Array.from({ length: 8 }, () => service.invoke('refresh', { provider: 'quark_tv' }));
  await entered.promise; release.resolve();
  for (const value of await Promise.all(calls)) assert.equal(value.refreshed, true);
  assert.equal(requests.length, 1);
  const saved = JSON.parse(readFileSync(join(root, 'pan/accounts.json'), 'utf8')).accounts.quark_tv;
  assert.equal(saved.credentials.refreshToken, 'rotated-refresh');
  assert.equal(saved.credentials.accessToken, 'rotated-access');
  assert.equal(saved.revision, 1);
});

test('cancelling one refresh waiter preserves work required by another', async t => {
  const entered = deferred(), release = deferred();
  let upstreamSignal;
  const { service, requests } = setup(t, async input => {
    upstreamSignal = input.signal;
    entered.resolve(); await release.promise;
    return response({ code: 200, data: { access_token: 'next-access', expires_in: 7200 } });
  });
  await service.invoke('settings.set', { provider: 'uc_tv', credentials });
  const controller = new AbortController();
  const first = service.invoke('refresh', { provider: 'uc_tv' }, { signal: controller.signal });
  const rejected = assert.rejects(first, { code: 'CANCELLED' });
  const second = service.invoke('refresh', { provider: 'uc_tv' });
  await entered.promise; controller.abort(); await rejected;
  assert.equal(upstreamSignal.aborted, false);
  release.resolve();
  assert.equal((await second).refreshed, true);
  assert.equal(requests.length, 1);
});

test('account replacement fences an old token response and never overwrites the new account', async t => {
  const entered = deferred(), release = deferred();
  const { service, root } = setup(t, async () => {
    entered.resolve(); await release.promise;
    return response({ code: 200, data: { access_token: 'old-late-access', refresh_token: 'old-late-refresh', expires_in: 60 } });
  });
  await service.invoke('settings.set', { provider: 'quark_tv', credentials });
  const refresh = service.invoke('refresh', { provider: 'quark_tv' });
  const rejected = assert.rejects(refresh, error => ['STALE_ACCOUNT', 'CANCELLED'].includes(error.code));
  await entered.promise;
  await service.invoke('settings.set', { provider: 'quark_tv',
    credentials: { ...credentials, accessToken: 'new-account-access', refreshToken: 'new-account-refresh' } });
  release.resolve(); await rejected;
  const current = JSON.parse(readFileSync(join(root, 'pan/accounts.json'), 'utf8')).accounts.quark_tv;
  assert.equal(current.revision, 2);
  assert.equal(current.credentials.accessToken, 'new-account-access');
  assert.equal(current.credentials.refreshToken, 'new-account-refresh');
});

test('different providers resolve concurrently and receive only their own credential and selected mode', async t => {
  const entered = deferred(), release = deferred(), seen = [];
  const { service } = setup(t, async input => {
    const url = new URL(input.url);
    seen.push(url);
    if (seen.length === 2) entered.resolve();
    await release.promise;
    return response({ status: 200, data: { download_url: `https://media.example/${url.hostname}/download`,
      video_info: [{ url: `https://media.example/${url.hostname}/stream` }] } });
  });
  await service.invoke('settings.set', { provider: 'quark_tv', credentials });
  await service.invoke('settings.set', { provider: 'uc_tv', credentials: { ...credentials, accessToken: 'uc-only' } });
  const quarkCall = service.invoke('play', { provider: 'quark_tv', file: stored(), mode: 'streaming' });
  const ucCall = service.invoke('play', { provider: 'uc_tv', file: stored(1, 'uc_tv'), mode: 'download' });
  await entered.promise; release.resolve();
  const results = await Promise.all([quarkCall, ucCall]);
  assert.equal(results[0].mode, 'streaming');
  assert.equal(results[1].mode, 'download');
  assert.equal(seen.length, 2);
  assert.equal(seen.find(url => url.hostname.endsWith('quark.cn')).searchParams.get('access_token'), credentials.accessToken);
  assert.equal(seen.find(url => url.hostname.endsWith('uc.cn')).searchParams.get('access_token'), 'uc-only');
});

test('close cancels work, drains even a non-cooperative transport and is idempotent', async t => {
  const entered = deferred(), release = deferred();
  let upstreamSignal;
  const fixture = setup(t, async input => {
    upstreamSignal = input.signal; entered.resolve(); await release.promise;
    return response({ code: 200, data: { access_token: 'late-after-close', expires_in: 60 } });
  });
  await fixture.service.invoke('settings.set', { provider: 'quark_tv', credentials });
  const refresh = fixture.service.invoke('refresh', { provider: 'quark_tv' });
  const rejected = assert.rejects(refresh, error => ['CLOSED', 'CANCELLED'].includes(error.code));
  await entered.promise;
  let drained = false;
  const closing = fixture.service.close().then(() => { drained = true; });
  await turn();
  assert.equal(upstreamSignal.aborted, true);
  assert.equal(drained, false);
  release.resolve();
  await rejected; await closing;
  await fixture.service.close();
  assert.equal(fixture.closes, 1);
  await assert.rejects(fixture.service.invoke('providers'), { code: 'CLOSED' });
});

test('all cancelled refresh waiters abort their work; a new caller starts a fresh refresh while old work drains', async t => {
  const entered = deferred(), releaseOld = deferred();
  let firstSignal, count = 0;
  const { service, root } = setup(t, async input => {
    if (++count === 1) {
      firstSignal = input.signal;
      entered.resolve();
      await releaseOld.promise;
      return response({ code: 200, data: { access_token: 'cancelled-old', expires_in: 60 } });
    }
    return response({ code: 200, data: { access_token: 'active-new', expires_in: 60 } });
  });
  await service.invoke('settings.set', { provider: 'quark_tv', credentials });
  const controllers = [new AbortController(), new AbortController()];
  const cancelled = controllers.map(controller =>
    assert.rejects(service.invoke('refresh', { provider: 'quark_tv' }, { signal: controller.signal }),
      { code: 'CANCELLED' }));
  await entered.promise;
  controllers.forEach(controller => controller.abort());
  await Promise.all(cancelled);
  assert.equal(firstSignal.aborted, true);
  assert.equal((await service.invoke('refresh', { provider: 'quark_tv' })).refreshed, true);
  releaseOld.resolve();
  await turn();
  assert.equal(count, 2);
  assert.equal(JSON.parse(readFileSync(join(root, 'pan/accounts.json'))).accounts.quark_tv.credentials.accessToken, 'active-new');
});

test('switching the ordinary account cancels a TV resolution using its stored-file reference', async t => {
  const entered = deferred(), release = deferred();
  let upstreamSignal;
  const { service } = setup(t, async input => {
    upstreamSignal = input.signal; entered.resolve(); await release.promise;
    return response({ errno: 0, data: { download_url: 'https://media.example/synthetic' } });
  });
  await service.invoke('settings.set', { provider: 'quark', credentials: { cookie: 'synthetic-one' } });
  await service.invoke('settings.set', { provider: 'quark_tv', credentials });
  const call = service.invoke('play', { provider: 'quark_tv', file: stored(1, 'quark'), mode: 'download' });
  const rejected = assert.rejects(call, error => ['CANCELLED', 'STALE_ACCOUNT'].includes(error.code));
  await entered.promise;
  await service.invoke('settings.set', { provider: 'quark', credentials: { cookie: 'synthetic-two' } });
  assert.equal(upstreamSignal.aborted, true);
  release.resolve();
  await rejected;
});

function baiduLogin(handler) {
  return async input => {
    const url = new URL(input.url);
    if (url.pathname === '/v2/api/getqrcode') return response({
      errno: 0, sign: 'synthetic-qr-sign', imgurl: 'https://passport.baidu.com/synthetic-qr.png',
    });
    if (url.pathname === '/synthetic-qr.png') return {
      status: 200, headers: { 'content-type': 'image/png' }, body: Buffer.from('synthetic-qr-image'),
    };
    if (url.pathname === '/channel/unicast') return handler(input);
    if (url.pathname === '/v3/login/main/qrbdusslogin') return response({}, {
      'set-cookie': ['BDUSS=synthetic-confirmed-cookie; Domain=baidu.com; Path=/; Secure'],
    });
    assert.equal(url.pathname, '/');
    return response({});
  };
}
const confirmedQR = () => response({ errno: 0, channel_v: JSON.stringify({ status: 0, v: 'synthetic-qr-ticket' }) });

test('QR pending, scanned, concurrent confirmation and repeated polling expose status only after one durable commit', async t => {
  let polls = 0;
  const entered = deferred(), release = deferred();
  const { service, root } = setup(t, baiduLogin(async () => {
    polls++;
    if (polls === 1) return response({ errno: 1 });
    if (polls === 2) return response({ errno: 0, channel_v: JSON.stringify({ status: 1 }) });
    entered.resolve(); await release.promise;
    return confirmedQR();
  }));
  const login = await service.invoke('login.start', { provider: 'baidu' });
  assert.deepEqual(login.challenge, { type: 'image', url: `data:image/png;base64,${Buffer.from('synthetic-qr-image').toString('base64')}` });
  const input = { sessionId: login.sessionId };
  assert.equal((await service.invoke('login.poll', input)).status, 'pending');
  assert.equal((await service.invoke('login.poll', input)).status, 'scanned');
  const calls = Array.from({ length: 4 }, () => service.invoke('login.poll', input));
  await entered.promise; release.resolve();
  for (const reply of await Promise.all(calls)) {
    assert.equal(reply.status, 'confirmed');
    assert.equal(reply.account.revision, 1);
    assert.equal(reply.account.status, 'signedIn');
    assert.ok(!JSON.stringify(reply).includes('synthetic-confirmed-cookie'));
  }
  assert.equal(polls, 3);
  const saved = JSON.parse(readFileSync(join(root, 'pan/accounts.json')));
  assert.equal(saved.accounts.baidu.revision, 1);
  assert.match(saved.accounts.baidu.credentials.cookie, /BDUSS=synthetic-confirmed-cookie/);
  assert.equal((await service.invoke('login.poll', input)).status, 'confirmed');
  assert.equal(polls, 3);
  assert.equal((await service.invoke('login.cancel', input)).status, 'confirmed');
  assert.equal((await service.invoke('settings.get')).accounts[0].status, 'signedIn');
  await service.invoke('settings.clear', { provider: 'baidu' });
  assert.equal((await service.invoke('login.poll', input)).status, 'cancelled');
});

test('QR persistence failure is not confirmation and may be retried without repeating upstream completion', async t => {
  let polls = 0;
  const loginHTTP = baiduLogin(() => { polls++; return confirmedQR(); });
  const { service, root } = setup(t, input => new URL(input.url).pathname === '/api/loginStatus'
    ? { status: 401, headers: {}, body: Buffer.alloc(0) } : loginHTTP(input));
  await service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'synthetic-existing' } });
  await assert.rejects(service.invoke('refresh', { provider: 'baidu' }), { code: 'AUTH_REQUIRED' });
  assert.equal((await service.invoke('settings.get')).accounts[0].status, 'expired');
  const login = await service.invoke('login.start', { provider: 'baidu' }), input = { sessionId: login.sessionId };
  const path = join(root, 'pan/accounts.json'), backup = join(root, 'pan/held-accounts');
  renameSync(path, backup);
  mkdirSync(path);
  await assert.rejects(service.invoke('login.poll', input), { code: 'STORAGE_ERROR' });
  assert.equal((await service.invoke('settings.get')).accounts[0].revision, 1);
  assert.equal((await service.invoke('settings.get')).accounts[0].status, 'expired');
  rmSync(path, { recursive: true }); renameSync(backup, path);
  const confirmed = await service.invoke('login.poll', input);
  assert.equal(confirmed.status, 'confirmed');
  assert.equal(confirmed.account.status, 'signedIn');
  assert.equal(polls, 1);
  assert.equal(JSON.parse(readFileSync(path)).accounts.baidu.revision, 2);
});

for (const provider of ['baidu', 'quark', 'uc']) {
  for (const action of ['cancel', 'replace']) {
    test(`${provider}: ${action} rejects a late QR start without reviving its challenge or changing credentials`, async t => {
      const entered = deferred(), release = deferred();
      let starts = 0;
      const loginHTTP = baiduLogin(() => response({ errno: 1 }));
      const { service, root } = setup(t, async input => {
        const path = new URL(input.url).pathname;
        const isStart = path === '/v2/api/getqrcode' || path.endsWith('/getTokenForQrcodeLogin');
        if (isStart && ++starts === 1) {
          entered.resolve();
          await release.promise;
        }
        if (provider === 'baidu') return loginHTTP(input);
        if (isStart) return response({ status: 2000000, data: { token: 'synthetic-qr-token' } });
        if (path.endsWith('/getServiceTicketByQrcodeToken')) return response({ status: 'pending' });
        return response({});
      });
      const controller = new AbortController();
      const pending = assert.rejects(service.invoke('login.start', { provider }, { signal: controller.signal }),
        { code: action === 'cancel' || provider === 'baidu' ? 'CANCELLED' : 'STALE_ACCOUNT' });
      await entered.promise;
      if (action === 'cancel') controller.abort();
      else await service.invoke('settings.set', { provider, credentials: { cookie: 'session=synthetic-new' } });
      release.resolve();
      await pending;
      const current = (await service.invoke('settings.get')).accounts.find(item => item.provider === provider);
      assert.equal(current.revision, action === 'cancel' ? 0 : 1);
      assert.equal(current.status, action === 'cancel' ? 'signedOut' : 'signedIn');
      if (action === 'replace') {
        assert.deepEqual(JSON.parse(readFileSync(join(root, 'pan/accounts.json'))).accounts[provider].credentials,
          { cookie: 'session=synthetic-new' });
      }
      const next = await service.invoke('login.start', { provider });
      assert.equal(next.status, 'pending');
      assert.equal((await service.invoke('login.poll', { sessionId: next.sessionId })).status, 'pending');
      assert.equal((await service.invoke('login.cancel', { sessionId: next.sessionId })).status, 'cancelled');
      assert.deepEqual((await service.invoke('settings.get')).accounts.find(item => item.provider === provider), current);
    });
  }
}

test('QR cancellation and replacement invalidate pending completion without saving the old login', async t => {
  for (const action of ['cancel', 'replace']) {
    const entered = deferred(), release = deferred();
    let upstreamSignal;
    const { service, root } = setup(t, baiduLogin(async input => {
      upstreamSignal = input.signal; entered.resolve(); await release.promise;
      return confirmedQR();
    }));
    const login = await service.invoke('login.start', { provider: 'baidu' }), input = { sessionId: login.sessionId };
    const pending = assert.rejects(service.invoke('login.poll', input),
      error => ['CANCELLED', 'STALE_ACCOUNT'].includes(error.code));
    await entered.promise;
    if (action === 'cancel') assert.equal((await service.invoke('login.cancel', input)).status, 'cancelled');
    else await service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'synthetic-new' } });
    assert.equal(upstreamSignal.aborted, true);
    release.resolve(); await pending;
    if (action === 'cancel') assert.equal((await service.invoke('settings.get')).accounts[0].revision, 0);
    else assert.equal(JSON.parse(readFileSync(join(root, 'pan/accounts.json'))).accounts.baidu.credentials.cookie, 'synthetic-new');
    assert.equal((await service.invoke('login.poll', input)).status, 'cancelled');
  }
});

test('same-file prepare calls share one exact transfer; cancellation does not choose another saved file', async t => {
  const entered = deferred(), release = deferred();
  let transfers = 0, upstreamSignal;
  const { service } = setup(t, async input => {
    const url = new URL(input.url);
    if (url.pathname.endsWith('/file/sort')) return response({ code: 0,
      data: { list: [{ fid: 'dedicated-folder', file_name: 'MeowFilm', dir: true }], total: 1 } });
    assert.equal(url.pathname, '/1/clouddrive/share/sharepage/save');
    assert.deepEqual(JSON.parse(input.body).fid_list, ['selected-file']);
    transfers++;
    upstreamSignal = input.signal; entered.resolve(); await release.promise;
    return response({ code: 0, task_sync: true, data: { save_as_top_fids: ['exact-saved-file'] } });
  });
  await service.invoke('settings.set', { provider: 'quark', credentials: { cookie: 'synthetic-cookie' } });
  const file = { kind: 'share', provider: 'quark', accountRevision: 1,
    shareId: 'share', stoken: 'synthetic-token', fileId: 'selected-file', fileToken: 'synthetic-file-token' };
  const controller = new AbortController();
  const first = assert.rejects(service.invoke('prepare', { provider: 'quark', file }, { signal: controller.signal }),
    { code: 'CANCELLED' });
  // Field order does not change the identity of a structured reference.
  const second = service.invoke('prepare', { provider: 'quark', file: Object.fromEntries(Object.entries(file).reverse()) });
  await entered.promise; controller.abort(); await first;
  assert.equal(upstreamSignal.aborted, false);
  release.resolve();
  assert.deepEqual((await second).file, { kind: 'stored', provider: 'quark', accountRevision: 1,
    fileId: 'exact-saved-file', parentFileId: 'dedicated-folder' });
  assert.equal(transfers, 1);
});

test('upstream expired QR stays distinct from pending and cannot write credentials', async t => {
  const { service } = setup(t, async input => {
    const path = new URL(input.url).pathname;
    if (path.endsWith('/getTokenForQrcodeLogin')) return response({ status: 2000000, data: { token: 'synthetic-qr-token' } });
    if (path.endsWith('/getServiceTicketByQrcodeToken')) return response({ status: 'expired' });
    return response({});
  });
  const login = await service.invoke('login.start', { provider: 'quark' });
  const input = { sessionId: login.sessionId };
  assert.equal((await service.invoke('login.poll', input)).status, 'expired');
  assert.equal((await service.invoke('login.poll', input)).status, 'expired');
  assert.equal((await service.invoke('settings.get')).accounts.find(item => item.provider === 'quark').revision, 0);
  assert.equal((await service.invoke('settings.get')).accounts.find(item => item.provider === 'quark').status, 'signedOut');
});

test('starting another QR session and reaching the protocol expiry retire old challenges without polling upstream', async t => {
  let now = 1800000000000;
  const { service, requests } = setup(t, baiduLogin(() => assert.fail('expired sessions must not poll')),
    { now: () => now });
  const old = await service.invoke('login.start', { provider: 'baidu' });
  const current = await service.invoke('login.start', { provider: 'baidu' });
  await assert.rejects(service.invoke('login.poll', { sessionId: old.sessionId }), { code: 'LOGIN_EXPIRED' });
  const count = requests.length;
  now = current.expiresAt;
  await assert.rejects(service.invoke('login.poll', { sessionId: current.sessionId }), { code: 'LOGIN_EXPIRED' });
  assert.equal(requests.length, count);
  assert.equal((await service.invoke('settings.get')).accounts[0].revision, 0);
});

test('a new poll waits for the cancelled poll to drain before reusing its temporary QR cookie session', async t => {
  const entered = deferred(), release = deferred();
  let polls = 0;
  const { service } = setup(t, baiduLogin(async () => {
    if (++polls === 1) { entered.resolve(); await release.promise; return response({ errno: 1 }); }
    return confirmedQR();
  }));
  const login = await service.invoke('login.start', { provider: 'baidu' });
  const input = { sessionId: login.sessionId }, controller = new AbortController();
  const cancelled = assert.rejects(service.invoke('login.poll', input, { signal: controller.signal }),
    { code: 'CANCELLED' });
  await entered.promise; controller.abort(); await cancelled;
  const next = service.invoke('login.poll', input);
  await turn();
  assert.equal(polls, 1);
  release.resolve();
  assert.equal((await next).status, 'confirmed');
  assert.equal(polls, 2);
});
