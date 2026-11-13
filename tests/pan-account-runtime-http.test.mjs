import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPanRuntime, createRuntimeClient } from '../apps/runtime/pan-entry.mjs';
import { PanError } from '../packages/pan-runtime/src/errors.js';
import { deferred } from '../packages/runtime-bridge/test/http-fixture.js';

async function connect(t, handler) {
  const area = fileURLToPath(new URL('../tmp/test-results/pan-login/http/', import.meta.url));
  mkdirSync(area, { recursive: true });
  const dataRoot = mkdtempSync(join(area, 'case-'));
  const runtime = createPanRuntime({
    sessionId: randomUUID(), dataRoot,
    pan: { transport: { request: handler, async close() {} } },
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
const credentials = { refreshToken: 'synthetic-private-refresh', deviceId: 'synthetic-device' };
async function account(client, provider = 'quark_tv') {
  const result = await client.call('pan.settings.get');
  assert.equal(result.ok, true);
  return result.value.accounts.find(item => item.provider === provider);
}

test('HTTP account settings expose three states, distinguish authentication from network failure and never read back secrets', async t => {
  let outcome = 'success';
  const { client, dataRoot } = await connect(t, async input => {
    assert.equal(input.url, 'http://api.extscreen.com/quarkdrive/token');
    if (outcome === 'network') throw new PanError('NETWORK_ERROR');
    return response(input, outcome === 'auth'
      ? { code: 200, data: { status: -1, errno: 11000 } }
      : { code: 200, data: { access_token: 'synthetic-private-access',
        refresh_token: 'synthetic-private-rotated', expires_in: 3600 } });
  });
  const providers = (await client.call('pan.providers')).value.providers;
  assert.equal(providers.length, 7);
  assert.ok(providers.every(item => item.refresh));
  assert.equal((await account(client)).status, 'signedOut');
  const saved = await client.call('pan.settings.set', { provider: 'quark_tv', credentials });
  assert.equal(saved.ok, true);
  assert.equal(saved.value.status, 'signedIn');
  assert.equal(saved.value.revision, 1);
  const refreshed = await client.call('pan.refresh', { provider: 'quark_tv' });
  assert.equal(refreshed.ok, true);
  assert.equal(refreshed.value.account.status, 'signedIn');
  assert.equal(refreshed.value.account.revision, 1);
  for (const value of [saved, refreshed, await client.call('pan.settings.get')]) {
    assert.ok(!JSON.stringify(value).includes('synthetic-private-'));
  }
  const stored = JSON.parse(readFileSync(join(dataRoot, 'pan/accounts.json'), 'utf8'));
  assert.equal(stored.accounts.quark_tv.credentials.refreshToken, 'synthetic-private-rotated');
  outcome = 'auth';
  assert.deepEqual(await client.call('pan.refresh', { provider: 'quark_tv' }),
    { ok: false, contract: 'pan.runtime.v1', code: 'AUTH_FAILED' });
  assert.equal((await account(client)).status, 'expired');
  assert.equal((await account(client)).configured, true);
  outcome = 'network';
  assert.deepEqual(await client.call('pan.refresh', { provider: 'quark_tv' }),
    { ok: false, contract: 'pan.runtime.v1', code: 'NETWORK_ERROR' });
  assert.equal((await account(client)).status, 'expired');
  outcome = 'success';
  assert.equal((await client.call('pan.refresh', { provider: 'quark_tv' })).value.account.status, 'signedIn');
  outcome = 'network';
  assert.equal((await client.call('pan.refresh', { provider: 'quark_tv' })).code, 'NETWORK_ERROR');
  assert.equal((await account(client)).status, 'signedIn');
  const cleared = await client.call('pan.settings.clear', { provider: 'quark_tv' });
  assert.equal(cleared.ok, true);
  assert.deepEqual(cleared.value, {
    provider: 'quark_tv', revision: 2, configured: false, fields: [], status: 'signedOut',
  });
  assert.deepEqual(JSON.parse(readFileSync(join(dataRoot, 'pan/accounts.json'), 'utf8'))
    .accounts.quark_tv.credentials, {});
});

test('HTTP logout revokes an in-flight refresh and late credentials cannot sign the cleared account back in', async t => {
  const entered = deferred(), release = deferred();
  t.after(() => release.resolve());
  let signal;
  const { client, dataRoot } = await connect(t, async input => {
    signal = input.signal;
    entered.resolve();
    await release.promise;
    return response(input, { code: 200, data: {
      access_token: 'synthetic-late-access', refresh_token: 'synthetic-late-refresh', expires_in: 3600,
    } });
  });
  assert.equal((await client.call('pan.settings.set', { provider: 'quark_tv', credentials })).ok, true);
  const pending = client.call('pan.refresh', { provider: 'quark_tv' });
  await entered.promise;
  const cleared = await client.call('pan.settings.clear', { provider: 'quark_tv' });
  assert.equal(cleared.value.status, 'signedOut');
  assert.equal(signal.aborted, true);
  release.resolve();
  assert.deepEqual(await pending, { ok: false, contract: 'pan.runtime.v1', code: 'STALE_ACCOUNT' });
  assert.deepEqual(await account(client), cleared.value);
  assert.deepEqual(JSON.parse(readFileSync(join(dataRoot, 'pan/accounts.json'), 'utf8'))
    .accounts.quark_tv.credentials, {});
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
  const start = await client.call('pan.login.start', { provider: 'baidu' });
  assert.equal(start.ok, true);
  assert.equal(start.value.status, 'pending');
  assert.deepEqual(start.value.challenge, {
    type: 'image', url: `data:image/png;base64,${Buffer.from('synthetic-image').toString('base64')}`,
  });
  const input = { sessionId: start.value.sessionId };
  assert.equal((await client.call('pan.login.poll', input, { timeout: 45000 })).value.status, 'pending');
  assert.equal((await client.call('pan.login.poll', input, { timeout: 45000 })).value.status, 'scanned');
  const confirmation = await client.call('pan.login.poll', input, { timeout: 45000 });
  assert.equal(confirmation.ok, true);
  assert.equal(confirmation.value.status, 'confirmed');
  assert.equal(confirmation.value.account.status, 'signedIn');
  assert.equal(confirmation.value.account.revision, 1);
  assert.ok(!JSON.stringify(confirmation).includes('synthetic-qr-cookie'));
  assert.match(JSON.parse(readFileSync(join(dataRoot, 'pan/accounts.json'), 'utf8'))
    .accounts.baidu.credentials.cookie, /BDUSS=synthetic-qr-cookie/);
  assert.equal((await client.call('pan.login.cancel', input)).value.status, 'confirmed');
  assert.deepEqual(await account(client, 'baidu'), confirmation.value.account);
  assert.equal(polls, 3);
});
