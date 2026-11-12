import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, renameSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createPanService, PanError } from '../src/index.js';

const now = 1_800_000_000_000;
const turn = () => new Promise(resolve => setImmediate(resolve));
const providerIds = ['baidu', 'quark', 'uc', 'tianyi', 'yidong', 'quark_tv', 'uc_tv'];
const cookies = { cookie: 'session=synthetic-account' };
const tokens = { refreshToken: 'synthetic-refresh', deviceId: 'synthetic-device' };
const authorization = Buffer.from('synthetic-prefix:synthetic-account:synthetic-token').toString('base64');
const credentials = {
  baidu: cookies, quark: cookies, uc: cookies, tianyi: { username: 'synthetic-user', password: 'synthetic-password' },
  yidong: { authorization }, quark_tv: tokens, uc_tv: tokens,
};
const refreshCredentials = { ...credentials, tianyi: cookies };
const response = (value, status = 200, headers = {}) => ({
  status, headers, body: Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)),
});
function valid(provider) {
  if (provider === 'baidu') return response({ login_info: { bdstoken: 'synthetic-account-confirmation' } });
  if (provider === 'quark' || provider === 'uc') {
    return response({ code: 'OK', success: true, data: { nickname: 'synthetic-display-name' } });
  }
  if (provider === 'tianyi') return response('', 302, { location: 'https://cloud.189.cn/web/main' });
  if (provider === 'yidong') return response('<root><return>0</return><token>synthetic-next-token</token></root>');
  return response({ code: 200, data: { access_token: 'synthetic-next-access', refresh_token: 'synthetic-next-refresh' } });
}
function invalid(provider) {
  if (provider === 'baidu') return response('', 401);
  if (provider === 'quark' || provider === 'uc') return response({ code: 'OK', success: true, data: {} });
  if (provider === 'tianyi') {
    return response('', 302, { location: 'https://open.e.189.cn/api/logbox/separate/web/index.html' });
  }
  if (provider === 'yidong') return response('<root><return>1</return></root>');
  return response({ code: 200, data: { status: -1, errno: 11000 } });
}
const authCode = provider => ['yidong', 'quark_tv', 'uc_tv'].includes(provider) ? 'AUTH_FAILED' : 'AUTH_REQUIRED';
function reply(provider, input, value) {
  if (provider === 'tianyi') {
    const path = new URL(input.url).pathname;
    if (path === '/web/main') return response('synthetic account page');
    if (path === '/api/logbox/separate/web/index.html') return response('synthetic login page');
  }
  return value;
}
function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}
function setup(t, handler = () => assert.fail('account snapshots must not use the network')) {
  const root = mkdtempSync(join(tmpdir(), 'pan-account-status-')), requests = [];
  const transport = {
    async request(input) {
      requests.push(input);
      return { url: input.url, ...await handler(input) };
    },
    async close() {},
  };
  const service = createPanService({ dataRoot: root, transport, now: () => now });
  t.after(async () => { await service.close(); rmSync(root, { recursive: true, force: true }); });
  return { root, service, transport, requests, path: join(root, 'pan/accounts.json') };
}
async function account(service, provider) {
  return (await service.invoke('settings.get')).accounts.find(item => item.provider === provider);
}

test('seven account snapshots project saved facts and three states without requiring freshness timestamps', async t => {
  const { service, path, requests } = setup(t);
  assert.deepEqual((await service.invoke('settings.get')).accounts, providerIds.map(provider => ({
    provider, revision: 0, configured: false, fields: [], status: 'signedOut',
  })));
  for (const provider of providerIds) {
    const saved = await service.invoke('settings.set', { provider, credentials: credentials[provider] });
    assert.deepEqual(saved, {
      provider, revision: 1, configured: true, fields: Object.keys(credentials[provider]), status: 'signedIn',
    });
    assert.deepEqual(await account(service, provider), saved);
    const record = JSON.parse(readFileSync(path)).accounts[provider];
    assert.deepEqual(record, { revision: 1, credentials: credentials[provider] });
    for (const value of Object.values(credentials[provider])) {
      assert.equal(JSON.stringify(saved).includes(value), false);
    }
  }
  const replaced = await service.invoke('settings.set', { provider: 'quark_tv', credentials: { refreshToken: 'synthetic-replacement' } });
  assert.deepEqual(replaced.fields, ['refreshToken']);
  assert.deepEqual(JSON.parse(readFileSync(path)).accounts.quark_tv.credentials, { refreshToken: 'synthetic-replacement' });
  const empty = await service.invoke('settings.set', { provider: 'quark_tv', credentials: {} });
  assert.deepEqual(empty, { provider: 'quark_tv', revision: 3, configured: false, fields: [], status: 'signedOut' });
  for (const provider of providerIds) {
    const previous = await account(service, provider);
    assert.deepEqual(await service.invoke('settings.clear', { provider }), {
      provider, revision: previous.revision + 1, configured: false, fields: [], status: 'signedOut',
    });
  }
  assert.equal(requests.length, 0);
});

test('expiry is process-local and never erases or rewrites saved credentials', async t => {
  const { service, root, path, transport } = setup(t, () => response('', 401));
  await service.invoke('settings.set', { provider: 'baidu', credentials: cookies });
  const before = readFileSync(path);
  await assert.rejects(service.invoke('refresh', { provider: 'baidu' }), { code: 'AUTH_REQUIRED' });
  assert.deepEqual(await account(service, 'baidu'), {
    provider: 'baidu', revision: 1, configured: true, fields: ['cookie'], status: 'expired',
  });
  assert.deepEqual(readFileSync(path), before);
  await service.close();
  const restarted = createPanService({ dataRoot: root, transport });
  try {
    assert.deepEqual(await account(restarted, 'baidu'), {
      provider: 'baidu', revision: 1, configured: true, fields: ['cookie'], status: 'signedIn',
    });
  } finally { await restarted.close(); }
});

for (const provider of providerIds) {
  test(`${provider}: refresh confirms or expires the account; ordinary failures retain the last confirmed state`, async t => {
    let result = valid(provider), error;
    const { service, path } = setup(t, input => { if (error) throw error; return reply(provider, input, result); });
    await service.invoke('settings.set', { provider, credentials: refreshCredentials[provider] });
    for (const status of ['signedIn', 'expired']) {
      if (status === 'expired') {
        result = invalid(provider);
        const before = readFileSync(path);
        await assert.rejects(service.invoke('refresh', { provider }), { code: authCode(provider) });
        assert.deepEqual(readFileSync(path), before);
      }
      const snapshot = await account(service, provider);
      assert.equal(snapshot.status, status);
      const upstreamFailure = provider === 'baidu' ? { errno: 123 }
        : provider === 'yidong' ? '<root><return>429</return></root>'
          : provider === 'quark' || provider === 'uc' ? { code: 'RATE_LIMITED', success: false } : { code: 429 };
      for (const [value, code] of [
        [new PanError('NETWORK_ERROR'), 'NETWORK_ERROR'], [new PanError('TIMEOUT'), 'TIMEOUT'],
        [response('malformed response'), 'UPSTREAM_FORMAT'],
        [response('busy', 429), 'UPSTREAM_HTTP'], [response('unavailable', 503), 'UPSTREAM_HTTP'],
        [response(upstreamFailure), provider === 'tianyi' ? 'UPSTREAM_FORMAT' : 'UPSTREAM_ERROR'],
      ]) {
        const before = readFileSync(path);
        error = value instanceof Error ? value : undefined;
        if (!error) result = value;
        await assert.rejects(service.invoke('refresh', { provider }), { code });
        assert.deepEqual(await account(service, provider), snapshot);
        assert.deepEqual(readFileSync(path), before);
      }
      error = undefined;
    }
    result = valid(provider);
    const refreshed = await service.invoke('refresh', { provider });
    assert.equal(refreshed.refreshed, true);
    assert.equal(refreshed.account.status, 'signedIn');
    assert.equal(refreshed.account.revision, 1);
    result = response('', 401);
    await assert.rejects(service.invoke('refresh', { provider }), { code: 'AUTH_REQUIRED' });
    assert.equal((await account(service, provider)).status, 'expired');
    const manual = await service.invoke('settings.set', { provider, credentials: refreshCredentials[provider] });
    assert.equal(manual.status, 'signedIn');
    assert.equal(manual.revision, 2);
  });

  test(`${provider}: an unconfigured account cannot be confirmed or expired by refresh`, async t => {
    const { service, requests } = setup(t);
    await assert.rejects(service.invoke('refresh', { provider }), { code: 'NOT_CONFIGURED' });
    assert.equal((await account(service, provider)).status, 'signedOut');
    assert.equal(requests.length, 0);
  });
}

test('Baidu account confirmation uses only the reference read-only loginStatus request and persists successful Cookie rotation', async t => {
  const { service, requests, path } = setup(t, input => {
    const url = new URL(input.url);
    assert.equal(url.origin, 'https://pan.baidu.com');
    assert.equal(url.pathname, '/api/loginStatus');
    assert.deepEqual(Object.fromEntries(url.searchParams), { clienttype: '1', web: '1', channel: 'web', version: '0' });
    assert.equal(input.method, 'GET');
    assert.equal(input.headers.Cookie, cookies.cookie);
    assert.equal(input.body, undefined);
    return response({ login_info: { bdstoken: 'synthetic-account-confirmation' } }, 200,
      { 'set-cookie': ['session=synthetic-rotated; Path=/'] });
  });
  await service.invoke('settings.set', { provider: 'baidu', credentials: cookies });
  const refreshed = await service.invoke('refresh', { provider: 'baidu' });
  assert.equal(refreshed.account.status, 'signedIn');
  assert.equal(refreshed.account.revision, 1);
  assert.equal(JSON.parse(readFileSync(path)).accounts.baidu.credentials.cookie, 'session=synthetic-rotated');
  assert.equal(requests.length, 1);
  assert.equal(JSON.stringify(refreshed).includes('synthetic-rotated'), false);
});

test('Baidu loginStatus HTTP200 errno -6 expires the account without persisting response cookies', async t => {
  const { service, requests, path } = setup(t, () => response({ errno: -6 }, 200,
    { 'set-cookie': ['session=synthetic-signed-out; Path=/'] }));
  await service.invoke('settings.set', { provider: 'baidu', credentials: cookies });
  const before = readFileSync(path);
  await assert.rejects(service.invoke('refresh', { provider: 'baidu' }), { code: 'AUTH_REQUIRED' });
  assert.deepEqual(await account(service, 'baidu'), {
    provider: 'baidu', revision: 1, configured: true, fields: ['cookie'], status: 'expired',
  });
  assert.deepEqual(readFileSync(path), before);
  assert.equal(requests.length, 1);
  assert.equal(new URL(requests[0].url).pathname, '/api/loginStatus');
});

for (const provider of ['quark', 'uc']) {
  test(`${provider}: Cookie confirmation uses only the read-only account-info API`, async t => {
    const endpoint = provider === 'quark' ? 'https://pan.quark.cn/account/info' : 'https://drive.uc.cn/account/info';
    const { service, requests, path } = setup(t, input => {
      const url = new URL(input.url);
      assert.equal(url.origin + url.pathname, endpoint);
      assert.deepEqual(Object.fromEntries(url.searchParams), { fr: 'pc', platform: 'pc' });
      assert.equal(input.method, 'GET');
      assert.equal(input.headers.Cookie, cookies.cookie);
      assert.equal(input.body, undefined);
      return response({ code: 'OK', success: true, data: { nickname: 'synthetic-display-name' } }, 200,
        { 'set-cookie': ['session=synthetic-rotated; Path=/'] });
    });
    await service.invoke('settings.set', { provider, credentials: cookies });
    const refreshed = await service.invoke('refresh', { provider });
    assert.equal(refreshed.account.status, 'signedIn');
    assert.equal(refreshed.account.revision, 1);
    assert.equal(JSON.parse(readFileSync(path)).accounts[provider].credentials.cookie, 'session=synthetic-rotated');
    assert.equal(requests.length, 1);
    assert.equal(JSON.stringify(refreshed).includes('synthetic-display-name'), false);
    assert.equal(JSON.stringify(refreshed).includes('synthetic-rotated'), false);
  });
}

test('Tianyi Cookie confirmation follows the known cloud homepage and saves rotation only after it succeeds', async t => {
  const { service, requests, path } = setup(t, input => {
    const url = new URL(input.url);
    assert.equal(url.origin, 'https://cloud.189.cn');
    assert.equal(input.method, 'GET');
    assert.equal(input.redirect, 'manual');
    assert.equal(input.body, undefined);
    if (url.pathname === '/api/portal/loginUrl.action') {
      assert.deepEqual(Object.fromEntries(url.searchParams), { redirectURL: 'https://cloud.189.cn/main.action' });
      assert.equal(input.headers.Cookie, cookies.cookie);
      return response('', 302, { location: '/web/main', 'set-cookie': ['session=synthetic-rotated; Path=/'] });
    }
    assert.equal(url.pathname, '/web/main');
    assert.equal(input.headers.Cookie, 'session=synthetic-rotated');
    return response('synthetic account page');
  });
  await service.invoke('settings.set', { provider: 'tianyi', credentials: cookies });
  const refreshed = await service.invoke('refresh', { provider: 'tianyi' });
  assert.equal(refreshed.account.status, 'signedIn');
  assert.equal(refreshed.account.revision, 1);
  assert.equal(requests.length, 2);
  assert.deepEqual(JSON.parse(readFileSync(path)).accounts.tianyi.credentials, { cookie: 'session=synthetic-rotated' });
});

test('Tianyi follows intermediate redirects but unknown terminals and unsuccessful homepages retain status and saved Cookie', async t => {
  let initial, homepage;
  const { service, path, requests } = setup(t, input => {
    const target = new URL(input.url);
    if (target.origin === 'https://cloud.189.cn' && target.pathname === '/api/portal/loginUrl.action') return initial;
    if (target.origin === 'https://cloud.189.cn' && target.pathname === '/web/main') return homepage;
    return response('synthetic terminal page');
  });
  await service.invoke('settings.set', { provider: 'tianyi', credentials: cookies });
  for (const status of ['signedIn', 'expired']) {
    if (status === 'expired') {
      initial = invalid('tianyi');
      await assert.rejects(service.invoke('refresh', { provider: 'tianyi' }), { code: 'AUTH_REQUIRED' });
    }
    const snapshot = await account(service, 'tianyi'), before = readFileSync(path);
    assert.equal(snapshot.status, status);
    for (const [value, requestCount] of [
      [response('', 302), 1],
      [response('', 302, { location: 'https://unrelated.invalid/web/main' }), 2],
      [response('', 302, { location: 'https://cloud.189.cn/unknown' }), 2],
      [response('', 302, { location: 'https://open.e.189.cn/unknown' }), 2],
      [response('unknown page'), 1],
    ]) {
      initial = value;
      const count = requests.length;
      await assert.rejects(service.invoke('refresh', { provider: 'tianyi' }), { code: 'UPSTREAM_FORMAT' });
      assert.equal(requests.length, count + requestCount);
      assert.deepEqual(await account(service, 'tianyi'), snapshot);
      assert.deepEqual(readFileSync(path), before);
    }
    initial = response('', 302, { location: '/web/main', 'set-cookie': ['session=synthetic-unsaved; Path=/'] });
    for (const [value, code] of [
      [response('', 503), 'UPSTREAM_HTTP'], [response('', 429), 'UPSTREAM_HTTP'],
      [response('', 302, { location: '/unknown' }), 'UPSTREAM_FORMAT'],
      [response('', 302, { location: '/web/main' }), 'UPSTREAM_FORMAT'],
    ]) {
      homepage = value;
      await assert.rejects(service.invoke('refresh', { provider: 'tianyi' }), { code });
      assert.deepEqual(await account(service, 'tianyi'), snapshot);
      assert.deepEqual(readFileSync(path), before);
    }
  }
});

test('Tianyi accepts a complete off-domain redirect chain and scopes temporary cookies by domain and path', async t => {
  const { service, requests, path } = setup(t, input => {
    const target = new URL(input.url), cookie = input.headers.Cookie;
    assert.equal(input.method, 'GET');
    assert.equal(input.redirect, 'manual');
    if (target.origin === 'https://cloud.189.cn' && target.pathname === '/api/portal/loginUrl.action') {
      assert.equal(cookie, cookies.cookie);
      return response('', 302, {
        location: 'http://relay.invalid/start',
        'set-cookie': ['session=synthetic-cloud-rotated; Path=/', 'cloudRelay=synthetic-relay; Path=/relay'],
      });
    }
    if (target.origin === 'http://relay.invalid' && target.pathname === '/start') {
      assert.equal(cookie, '');
      return response('', 302, {
        location: '/hop',
        'set-cookie': ['relayRoot=synthetic-relay-root; Path=/', 'relayHop=synthetic-relay-hop; Path=/hop'],
      });
    }
    if (target.origin === 'http://relay.invalid' && target.pathname === '/hop') {
      assert.equal(cookie, 'relayHop=synthetic-relay-hop; relayRoot=synthetic-relay-root');
      return response('', 302, { location: 'https://open.e.189.cn/api/logbox/separate/web/index.html' });
    }
    if (target.origin === 'https://open.e.189.cn') {
      assert.equal(target.pathname, '/api/logbox/separate/web/index.html');
      assert.equal(cookie, '');
      return response('', 302, {
        location: 'https://cloud.189.cn/relay/return',
        'set-cookie': ['authOnly=synthetic-auth-cookie; Path=/'],
      });
    }
    if (target.pathname === '/relay/return') {
      assert.equal(target.origin, 'https://cloud.189.cn');
      assert.equal(cookie, 'cloudRelay=synthetic-relay; session=synthetic-cloud-rotated');
      return response('', 302, {
        location: '/web/main',
        'set-cookie': ['session=synthetic-cloud-final; Path=/', 'webOnly=synthetic-web-cookie; Path=/web'],
      });
    }
    assert.equal(target.origin, 'https://cloud.189.cn');
    assert.equal(target.pathname, '/web/main');
    assert.equal(cookie, 'webOnly=synthetic-web-cookie; session=synthetic-cloud-final');
    return response('synthetic account page');
  });
  await service.invoke('settings.set', { provider: 'tianyi', credentials: cookies });
  const result = await service.invoke('refresh', { provider: 'tianyi' });
  assert.equal(result.account.status, 'signedIn');
  assert.equal(result.account.revision, 1);
  assert.deepEqual(requests.map(input => new URL(input.url).pathname),
    ['/api/portal/loginUrl.action', '/start', '/hop', '/api/logbox/separate/web/index.html', '/relay/return', '/web/main']);
  assert.deepEqual(JSON.parse(readFileSync(path)).accounts.tianyi.credentials, { cookie: 'session=synthetic-cloud-final' });
});

test('Tianyi cancels an off-domain confirmation before a late redirect can request or save cloud credentials', async t => {
  const entered = deferred(), release = deferred();
  const { service, requests, path } = setup(t, async input => {
    const target = new URL(input.url);
    if (target.origin === 'https://cloud.189.cn') {
      assert.equal(target.pathname, '/api/portal/loginUrl.action');
      return response('', 302, { location: 'https://relay.invalid/hop', 'set-cookie': ['session=synthetic-unsaved; Path=/'] });
    }
    assert.equal(target.origin, 'https://relay.invalid');
    assert.equal(input.headers.Cookie, '');
    entered.resolve();
    await release.promise;
    return response('', 302, { location: 'https://cloud.189.cn/web/main' });
  });
  await service.invoke('settings.set', { provider: 'tianyi', credentials: cookies });
  const snapshot = await account(service, 'tianyi'), saved = readFileSync(path);
  const controller = new AbortController();
  const pending = assert.rejects(service.invoke('refresh', { provider: 'tianyi' }, { signal: controller.signal }), { code: 'CANCELLED' });
  await entered.promise;
  controller.abort();
  release.resolve();
  await pending;
  await turn();
  assert.equal(requests.length, 2);
  assert.equal(requests[1].signal.aborted, true);
  assert.deepEqual(await account(service, 'tianyi'), snapshot);
  assert.deepEqual(readFileSync(path), saved);
});

test('TV business error envelopes alone do not establish rejected authentication', async t => {
  for (const provider of ['quark_tv', 'uc_tv']) await t.test(provider, async t => {
    let value;
    const { service, path } = setup(t, () => response(value));
    await service.invoke('settings.set', { provider, credentials: tokens });
    const snapshot = await account(service, provider), before = readFileSync(path);
    for (value of [{ code: 401 }, { code: 429 }, { code: 500 }, { code: 500, data: { status: -1, errno: 11000 } }]) {
      await assert.rejects(service.invoke('refresh', { provider }), { code: 'UPSTREAM_ERROR' });
      assert.deepEqual(await account(service, provider), snapshot);
      assert.deepEqual(readFileSync(path), before);
    }
  });
});

test('missing or unknown account replies are not evidence of authentication failure', async t => {
  for (const provider of providerIds) await t.test(provider, async t => {
    let value;
    const { service, path } = setup(t, () => response(value));
    await service.invoke('settings.set', { provider, credentials: refreshCredentials[provider] });
    const snapshot = await account(service, provider), before = readFileSync(path);
    const values = provider === 'baidu'
      ? [{}, { errno: 0 }, { login_info: {} }, { login_info: { bdstoken: '' } }]
      : provider === 'quark' || provider === 'uc'
        ? [{}, { code: 'OK', success: true }, { code: 'OK', success: true, data: null },
          { code: 'OK', success: true, data: [] }, { code: 'OK', success: true, data: 'unknown' }]
      : provider === 'tianyi' ? ['unknown page']
      : provider === 'yidong' ? ['<root><return>invalid</return></root>']
        : [{}, { data: {} }, { code: 200, data: {} }];
    for (value of values) {
      await assert.rejects(service.invoke('refresh', { provider }), { code: 'UPSTREAM_FORMAT' });
      assert.deepEqual(await account(service, provider), snapshot);
      assert.deepEqual(readFileSync(path), before);
    }
  });
});

test('share password and share authorization errors never expire a saved account', async t => {
  for (const provider of ['quark', 'uc', 'tianyi', 'yidong']) await t.test(provider, async t => {
    const { service } = setup(t, () => provider === 'tianyi'
      ? response({ res_code: 1, res_message: 'accessCode required' })
      : provider === 'yidong' ? response({ code: 9188 }) : response({}, 401));
    await service.invoke('settings.set', { provider, credentials: provider === 'yidong' ? { authorization } : cookies });
    const before = await account(service, provider);
    const url = ({ quark: 'https://pan.quark.cn/s/synthetic', uc: 'https://drive.uc.cn/s/synthetic',
      tianyi: 'https://cloud.189.cn/t/SyntheticCode', yidong: 'https://caiyun.139.com/w/i/SyntheticCode' })[provider];
    await assert.rejects(service.invoke('list', { provider, share: { url } }), { code: 'AUTH_REQUIRED' });
    assert.deepEqual(await account(service, provider), before);
  });
});

for (const provider of ['baidu', 'quark_tv']) {
  for (const action of ['replace', 'clear']) {
    for (const late of ['success', 'authFailure']) {
      test(`${provider}: ${action} fences a late ${late} without changing the new status`, async t => {
        const entered = deferred(), release = deferred();
        const { service, path } = setup(t, async () => {
          entered.resolve();
          await release.promise;
          return late === 'success' ? valid(provider) : invalid(provider);
        });
        await service.invoke('settings.set', { provider, credentials: credentials[provider] });
        const rejected = assert.rejects(service.invoke('refresh', { provider }), { code: 'STALE_ACCOUNT' });
        await entered.promise;
        if (action === 'replace') await service.invoke('settings.set', { provider, credentials: credentials[provider] });
        else await service.invoke('settings.clear', { provider });
        const current = await account(service, provider), saved = readFileSync(path);
        release.resolve();
        await rejected;
        assert.deepEqual(await account(service, provider), current);
        assert.equal(current.status, action === 'clear' ? 'signedOut' : 'signedIn');
        assert.deepEqual(readFileSync(path), saved);
      });
    }
  }

  test(`${provider}: all cancelled refresh waiters fence a late authentication error while a new refresh succeeds`, async t => {
    const entered = deferred(), release = deferred();
    let calls = 0;
    const { service, requests } = setup(t, async () => {
      if (++calls === 1) {
        entered.resolve();
        await release.promise;
        return invalid(provider);
      }
      return valid(provider);
    });
    await service.invoke('settings.set', { provider, credentials: credentials[provider] });
    const controller = new AbortController();
    const rejected = assert.rejects(service.invoke('refresh', { provider }, { signal: controller.signal }), { code: 'CANCELLED' });
    await entered.promise;
    controller.abort();
    await rejected;
    assert.equal(requests[0].signal.aborted, true);
    assert.equal((await service.invoke('refresh', { provider })).account.status, 'signedIn');
    const current = await account(service, provider);
    release.resolve();
    await turn();
    assert.deepEqual(await account(service, provider), current);
    assert.equal(requests.length, 2);
  });

  test(`${provider}: cancelling one shared waiter leaves the active waiter's authentication result authoritative`, async t => {
    const entered = deferred(), release = deferred();
    const { service, requests } = setup(t, async () => {
      entered.resolve();
      await release.promise;
      return invalid(provider);
    });
    await service.invoke('settings.set', { provider, credentials: credentials[provider] });
    const controller = new AbortController();
    const cancelled = assert.rejects(service.invoke('refresh', { provider }, { signal: controller.signal }), { code: 'CANCELLED' });
    const active = assert.rejects(service.invoke('refresh', { provider }), { code: authCode(provider) });
    await entered.promise;
    controller.abort();
    await cancelled;
    assert.equal(requests[0].signal.aborted, false);
    release.resolve();
    await active;
    assert.equal((await account(service, provider)).status, 'expired');
    assert.equal(requests.length, 1);
  });
}

test('failed manual replacement and sign-out writes retain the existing expired account', async t => {
  const { service, path } = setup(t, () => response('', 401));
  await service.invoke('settings.set', { provider: 'baidu', credentials: cookies });
  await assert.rejects(service.invoke('refresh', { provider: 'baidu' }), { code: 'AUTH_REQUIRED' });
  const expired = await account(service, 'baidu'), saved = readFileSync(path), backup = path + '.held';
  renameSync(path, backup);
  mkdirSync(path);
  await assert.rejects(service.invoke('settings.set', { provider: 'baidu', credentials: { cookie: 'session=synthetic-new' } }),
    { code: 'STORAGE_ERROR' });
  await assert.rejects(service.invoke('settings.clear', { provider: 'baidu' }), { code: 'STORAGE_ERROR' });
  assert.deepEqual(await account(service, 'baidu'), expired);
  assert.deepEqual(readFileSync(backup), saved);
  rmSync(path, { recursive: true });
  renameSync(backup, path);
  assert.equal((await service.invoke('settings.clear', { provider: 'baidu' })).status, 'signedOut');
});

for (const provider of ['baidu', 'quark_tv']) {
  test(`${provider}: a failed rotated-credential write preserves expiry until the credentials are durably saved`, async t => {
    let result = invalid(provider);
    const { service, path } = setup(t, () => result);
    await service.invoke('settings.set', { provider, credentials: credentials[provider] });
    await assert.rejects(service.invoke('refresh', { provider }), { code: authCode(provider) });
    const expired = await account(service, provider), saved = readFileSync(path), backup = path + '.held';
    renameSync(path, backup);
    mkdirSync(path);
    result = provider === 'baidu' ? response({ login_info: { bdstoken: 'synthetic-confirmation' } }, 200,
      { 'set-cookie': ['session=synthetic-rotated; Path=/'] }) : valid(provider);
    await assert.rejects(service.invoke('refresh', { provider }), { code: 'STORAGE_ERROR' });
    assert.deepEqual(await account(service, provider), expired);
    assert.deepEqual(readFileSync(backup), saved);
    rmSync(path, { recursive: true });
    renameSync(backup, path);
    assert.equal((await service.invoke('refresh', { provider })).account.status, 'signedIn');
  });
}
