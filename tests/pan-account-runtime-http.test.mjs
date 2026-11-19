import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPanRuntime, createRuntimeClient } from '../apps/runtime/pan-entry.mjs';
import { PanError } from '../packages/pan-runtime/src/errors.js';
import { deferred } from '../packages/runtime-bridge/test/http-fixture.js';
import { withTestDatabase } from './storage-test-fixture.mjs';

async function connect(t, handler, options = {}) {
  const area = fileURLToPath(new URL('../tmp/test-results/pan-login/http/', import.meta.url));
  mkdirSync(area, { recursive: true });
  const dataRoot = mkdtempSync(join(area, 'case-'));
  const runtime = createPanRuntime({
    sessionId: randomUUID(), dataRoot,
    pan: { ...options, transport: { request: handler, async close() {} } },
  });
  await runtime.start();
  const client = createRuntimeClient({ baseUrl: runtime.baseUrl });
  t.after(async () => {
    await client.close();
    await runtime.close();
    rmSync(dataRoot, { recursive: true, force: true });
  });
  return { client, dataRoot };
}
const response = (input, value) => ({
  url: input.url, status: 200, headers: {}, body: Buffer.from(JSON.stringify(value)),
});
const credentials = { cookie: 'session=synthetic-private-cookie' };
async function account(client, provider = 'quark') {
  const result = await client.call('pan.settings.get');
  assert.equal(result.ok, true);
  return result.value.accounts.find(item => item.provider === provider);
}

test('HTTP account settings expose three states, distinguish authentication from network failure and never read back secrets', async t => {
  let outcome = 'success';
  const { client, dataRoot } = await connect(t, async input => {
    assert.equal(input.url, 'https://pan.quark.cn/account/info?fr=pc&platform=pc');
    if (outcome === 'network') throw new PanError('NETWORK_ERROR');
    return { ...response(input, { code: 'OK', success: true,
      data: outcome === 'auth' ? {} : { nickname: 'synthetic' } }),
      headers: outcome === 'auth' ? {} : { 'set-cookie': ['session=synthetic-private-rotated; Path=/'] } };
  });
  const providers = (await client.call('pan.providers')).value.providers;
  assert.equal(providers.length, 5);
  assert.ok(providers.every(item => item.refresh));
  assert.equal((await account(client)).status, 'signedOut');
  const saved = await client.call('pan.settings.set', { provider: 'quark', credentials });
  assert.equal(saved.ok, true);
  assert.equal(saved.value.status, 'signedIn');
  assert.equal(saved.value.revision, 1);
  const refreshed = await client.call('pan.refresh', { provider: 'quark' });
  assert.equal(refreshed.ok, true);
  assert.equal(refreshed.value.account.status, 'signedIn');
  assert.equal(refreshed.value.account.revision, 1);
  for (const value of [saved, refreshed, await client.call('pan.settings.get')]) {
    assert.ok(!JSON.stringify(value).includes('synthetic-private-'));
  }
  await withTestDatabase(dataRoot, async database => {
    const [stored] = await database.query('SELECT cookie FROM pan_standalone_accounts WHERE provider=?', ['quark']);
    assert.equal(stored.cookie, 'session=synthetic-private-rotated');
  });
  outcome = 'auth';
  assert.deepEqual(await client.call('pan.refresh', { provider: 'quark' }),
    { ok: false, contract: 'pan.runtime.v1', code: 'AUTH_REQUIRED' });
  assert.equal((await account(client)).status, 'expired');
  assert.equal((await account(client)).configured, true);
  outcome = 'network';
  assert.deepEqual(await client.call('pan.refresh', { provider: 'quark' }),
    { ok: false, contract: 'pan.runtime.v1', code: 'NETWORK_ERROR' });
  assert.equal((await account(client)).status, 'expired');
  outcome = 'success';
  assert.equal((await client.call('pan.refresh', { provider: 'quark' })).value.account.status, 'signedIn');
  outcome = 'network';
  assert.equal((await client.call('pan.refresh', { provider: 'quark' })).code, 'NETWORK_ERROR');
  assert.equal((await account(client)).status, 'signedIn');
  const cleared = await client.call('pan.settings.clear', { provider: 'quark' });
  assert.equal(cleared.ok, true);
  assert.deepEqual(cleared.value, {
    provider: 'quark', revision: 2, configured: false, fields: [], status: 'signedOut',
  });
  await withTestDatabase(dataRoot, async database => {
    const [stored] = await database.query('SELECT cookie FROM pan_standalone_accounts WHERE provider=?', ['quark']);
    assert.equal(stored.cookie, null);
  });
});

test('HTTP pan operations ignore extra fields and never grant them credential or transport authority', async t => {
  const requests = [];
  const { client, dataRoot } = await connect(t, async input => {
    requests.push(input);
    assert.equal(input.url, 'https://pan.quark.cn/account/info?fr=pc&platform=pc');
    assert.equal(input.method, 'GET');
    assert.equal(input.body, undefined);
    assert.equal(input.headers.Cookie, credentials.cookie);
    return response(input, { code: 'OK', success: true, data: { nickname: 'synthetic' } });
  }, { now: () => 1800000000000 });
  const providers = await client.call('pan.providers');
  const saved = await client.call('pan.settings.set', { provider: 'quark',
    credentials: { ...credentials, password: { unused: true }, method: 'DELETE', headers: ['unused'],
      url: 'https://untrusted.invalid' },
    accountRevision: 999, method: 'DELETE', url: 'https://untrusted.invalid', headers: { Cookie: 'untrusted' } });
  assert.equal(saved.ok, true);
  assert.deepEqual(saved.value.fields, ['cookie']);
  await withTestDatabase(dataRoot, async database => {
    const [stored] = await database.query('SELECT cookie FROM pan_standalone_accounts WHERE provider=?', ['quark']);
    assert.equal(stored.cookie, credentials.cookie);
  });
  for (const extra of [null, 'unused', [], { nested: { cookie: 'untrusted' } }]) {
    const ignored = { method: extra, headers: extra, url: extra, body: extra, credentials: extra, extra };
    assert.deepEqual(await client.call('pan.providers', ignored), providers);
    assert.deepEqual((await client.call('pan.settings.get', ignored)).value.accounts.find(item => item.provider === 'quark'),
      await account(client));
    const refreshed = await client.call('pan.refresh', { ...ignored, provider: 'quark' });
    assert.equal(refreshed.ok, true);
    assert.equal(refreshed.value.account.revision, saved.value.revision);
    const { signal, ...actual } = requests.at(-1);
    assert.ok(signal instanceof AbortSignal);
    if (requests.length > 1) {
      const { signal: _firstSignal, ...initial } = requests[0];
      assert.deepEqual(actual, initial);
    }
  }
  const cleared = await client.call('pan.settings.clear', { provider: 'quark',
    credentials: { cookie: 'must-not-be-saved' }, extra: { headers: 'unused' } });
  assert.equal(cleared.ok, true);
  assert.equal(cleared.value.revision, saved.value.revision + 1);
  assert.equal(cleared.value.configured, false);
});

test('HTTP logout revokes an in-flight refresh and late credentials cannot sign the cleared account back in', async t => {
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  let signal;
  const { client, dataRoot } = await connect(t, async input => {
    signal = input.signal;
    entered.resolve();
    await release.promise;
    return { ...response(input, { code: 'OK', success: true, data: { nickname: 'synthetic' } }),
      headers: { 'set-cookie': ['session=synthetic-late; Path=/'] } };
  });
  assert.equal((await client.call('pan.settings.set', { provider: 'quark', credentials })).ok, true);
  const pending = client.call('pan.refresh', { provider: 'quark' });
  await entered.promise;
  const cleared = await client.call('pan.settings.clear', { provider: 'quark' });
  assert.equal(cleared.value.status, 'signedOut');
  assert.equal(signal.aborted, true);
  release.resolve();
  assert.deepEqual(await pending, { ok: false, contract: 'pan.runtime.v1', code: 'STALE_ACCOUNT' });
  assert.deepEqual(await account(client), cleared.value);
  await withTestDatabase(dataRoot, async database => {
    const [stored] = await database.query('SELECT cookie FROM pan_standalone_accounts WHERE provider=?', ['quark']);
    assert.equal(stored.cookie, null);
  });
});

test('HTTP QR confirmation publishes the persisted account and closing the confirmed challenge does not log out', async t => {
  let polls = 0;
  const { client, dataRoot } = await connect(t, async input => {
    const path = new URL(input.url).pathname;
    if (path === '/v2/api/getqrcode') return response(input, {
      errno: 0, sign: 'synthetic-sign', imgurl: 'https://passport.baidu.com/synthetic-image.png',
    });
    if (path === '/synthetic-image.png') return {
      url: input.url, status: 200, headers: { 'content-type': 'image/png' }, body: Buffer.from('synthetic-image'),
    };
    if (path === '/channel/unicast') {
      polls++;
      return response(input, polls === 1 ? { errno: 1 }
        : { errno: 0, channel_v: JSON.stringify(polls === 2
          ? { status: 1 } : { status: 0, v: 'synthetic-ticket' }) });
    }
    if (path === '/v3/login/main/qrbdusslogin') return {
      ...response(input, {}),
      headers: { 'set-cookie': ['BDUSS=synthetic-qr-cookie; Domain=baidu.com; Path=/; Secure'] },
    };
    assert.equal(path, '/');
    return response(input, {});
  });
  const start = await client.call('pan.login.start', { provider: 'baidu', method: 'DELETE', future: { sessionId: 'unused' } });
  assert.equal(start.ok, true);
  assert.equal(start.value.status, 'pending');
  assert.deepEqual(start.value.challenge, {
    type: 'image', url: `data:image/png;base64,${Buffer.from('synthetic-image').toString('base64')}`,
  });
  const input = { sessionId: start.value.sessionId, provider: 'unused', future: { headers: ['unused'] } };
  assert.equal((await client.call('pan.login.poll', input, { timeout: 45000 })).value.status, 'pending');
  assert.equal((await client.call('pan.login.poll', input, { timeout: 45000 })).value.status, 'scanned');
  const confirmation = await client.call('pan.login.poll', input, { timeout: 45000 });
  assert.equal(confirmation.ok, true);
  assert.equal(confirmation.value.status, 'confirmed');
  assert.equal(confirmation.value.account.status, 'signedIn');
  assert.equal(confirmation.value.account.revision, 1);
  assert.ok(!JSON.stringify(confirmation).includes('synthetic-qr-cookie'));
  await withTestDatabase(dataRoot, async database => {
    const [stored] = await database.query('SELECT cookie FROM pan_standalone_accounts WHERE provider=?', ['baidu']);
    assert.match(stored.cookie, /BDUSS=synthetic-qr-cookie/);
  });
  assert.equal((await client.call('pan.login.cancel', input)).value.status, 'confirmed');
  assert.deepEqual(await account(client, 'baidu'), confirmation.value.account);
  assert.equal(polls, 3);
});
