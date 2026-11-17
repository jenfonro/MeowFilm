import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createPanRuntime, createRuntimeClient } from '../apps/runtime/pan-entry.mjs';
import { createRuntime } from '../apps/runtime/entry.mjs';
import { contracts } from '../apps/runtime/contracts.mjs';
import { deferred, rawRequest, turn } from '../packages/runtime-bridge/test/http-fixture.js';

function root(t) {
  const area = fileURLToPath(new URL('../tmp/test-results/pan-runtime-http/', import.meta.url));
  mkdirSync(area, { recursive: true });
  const path = mkdtempSync(join(area, 'case-'));
  t.after(() => rmSync(path, { recursive: true, force: true }));
  return path;
}
async function connect(t, handler = () => assert.fail('unexpected upstream I/O')) {
  const dataRoot = root(t), sessionId = randomUUID();
  let closes = 0;
  const runtime = createPanRuntime({ sessionId, dataRoot,
    pan: { transport: { request: handler, async close() { closes++; } } } });
  await runtime.start();
  const client = createRuntimeClient({ baseUrl: runtime.baseUrl });
  t.after(async () => { await client.close(); await runtime.close(); });
  return { client, runtime, dataRoot, identity: { sessionId }, get closes() { return closes; } };
}

test('pan-only HTTP describes exact methods, rejects upload and accepts external configuration writes without credentials', async t => {
  const { client, runtime, identity } = await connect(t);
  const descriptor = (await client.describe()).value;
  assert.equal(descriptor.product, 'pan');
  assert.deepEqual(descriptor.modules, [{ id: 'pan', contract: 'pan.runtime.v1', version: '0.1.0',
    methods: contracts.pan.operations.map(name => 'pan.' + name).sort() }]);
  const providers = await client.call('pan.providers');
  assert.equal(providers.ok, true);
  assert.equal(providers.contract, 'pan.runtime.v1');
  assert.equal(providers.value.providers.length, 5);
  const external = await rawRequest(runtime.baseUrl, {},
    { path: '/v1/pan/settings.set', headers: { host: 'external.example.invalid',
      origin: 'https://browser.example.invalid', 'x-meow-request-id': undefined },
      body: JSON.stringify({ provider: 'baidu', credentials: { cookie: 'synthetic-external-cookie' } }) }).response;
  assert.equal(external.status, 200);
  assert.deepEqual(external.value, { ok: true, contract: 'pan.runtime.v1',
    value: { provider: 'baidu', revision: 1, configured: true, fields: ['cookie'], status: 'signedIn' } });
  assert.equal(external.headers['access-control-allow-origin'], '*');
  assert.match(external.headers['x-meow-request-id'], /^[0-9a-f-]{36}$/);
  assert.equal((await client.call('pan.settings.get')).value.accounts.find(item => item.provider === 'baidu').configured, true);
  const changed = await client.call('pan.settings.set', { provider: 'baidu',
    credentials: { cookie: 'synthetic-cookie' } });
  assert.deepEqual(changed, { ok: true, contract: 'pan.runtime.v1',
    value: { provider: 'baidu', revision: 2, configured: true, fields: ['cookie'], status: 'signedIn' } });
  await assert.rejects(client.upload('pan', 'not-a-script', 1, Buffer.from('anything')),
    { code: 'METHOD_NOT_FOUND' });
  const upload = await rawRequest(runtime.baseUrl, identity,
    { method: 'PUT', path: '/v1/pan/sources/not-a-script/1', body: Buffer.from('anything'),
      headers: { 'Content-Type': 'application/octet-stream' } }).response;
  assert.equal(upload.status, 404);
  assert.equal(upload.value.code, 'METHOD_NOT_FOUND');
  assert.deepEqual(await client.call('pan.unknown'),
    { ok: false, contract: 'runtime.http.v1', code: 'METHOD_NOT_FOUND' });
  assert.deepEqual(await client.call('pan.list', { provider: 'unknown', share: { url: 'https://pan.quark.cn/s/fake' } }),
    { ok: false, contract: 'pan.runtime.v1', code: 'INVALID_REQUEST' });
});

test('pan HTTP resolves both ordinary modes through the selected provider direct-link API', async t => {
  const requests = [];
  const { client } = await connect(t, async input => {
    const url = new URL(input.url);
    requests.push(url);
    assert.equal(url.pathname, '/1/clouddrive/file/download');
    const value = { code: 0, data: [{ fid: 'stored-file', download_url: 'https://media.example/original' }] };
    return { url: input.url, status: 200, headers: {}, body: Buffer.from(JSON.stringify(value)) };
  });
  for (const provider of ['quark', 'uc']) {
    assert.equal((await client.call('pan.settings.set', {
      provider, credentials: { cookie: 'session=synthetic-cookie' },
    })).ok, true);
    const file = { provider, kind: 'stored', accountRevision: 1, fileId: 'stored-file' };
    for (const mode of ['streaming', 'download']) {
      const direct = await client.call('pan.play', { provider, file, mode });
      assert.equal(direct.ok, true);
      assert.equal(direct.value.url, 'https://media.example/original');
      assert.equal(direct.value.mode, mode);
      assert.equal(direct.value.headers.Cookie, 'session=synthetic-cookie');
    }
  }
  assert.deepEqual(requests.map(url => [url.hostname, url.pathname, url.searchParams.get('method')]), [
    ['drive.quark.cn', '/1/clouddrive/file/download', null],
    ['drive.quark.cn', '/1/clouddrive/file/download', null],
    ['pc-api.uc.cn', '/1/clouddrive/file/download', null],
    ['pc-api.uc.cn', '/1/clouddrive/file/download', null],
  ]);
});

test('HTTP shutdown cancels and physically drains upstream work without publishing late credentials', async t => {
  const entered = deferred(), release = deferred();
  let signal;
  const fixture = await connect(t, async input => {
    signal = input.signal;
    entered.resolve();
    await release.promise;
    return { url: input.url, status: 200, headers: { 'set-cookie': ['session=synthetic-late; Path=/'] },
      body: Buffer.from(JSON.stringify({ code: 'OK', success: true, data: { nickname: 'synthetic-late' } })) };
  });
  const { client } = fixture;
  assert.equal((await client.call('pan.settings.set', { provider: 'quark',
    credentials: { cookie: 'session=synthetic-account' } })).ok, true);
  const path = join(fixture.dataRoot, 'pan/accounts.json'), saved = readFileSync(path);
  const pending = assert.rejects(client.call('pan.refresh', { provider: 'quark' }), { code: 'CLOSED' });
  await entered.promise;
  let completed = false;
  const close = client.shutdown().then(result => { completed = true; return result; });
  // Await the real cancellation notification, not a guessed network scheduling delay.
  await new Promise(resolve => signal.aborted ? resolve() : signal.addEventListener('abort', resolve, { once: true }));
  await turn();
  assert.equal(completed, false);
  release.resolve();
  assert.deepEqual(await close, { ok: true, contract: 'runtime.http.v1',
    value: { closed: true, cleanupComplete: true } });
  await pending;
  assert.equal(fixture.closes, 1);
  assert.deepEqual(readFileSync(path), saved, 'shutdown must not persist the late Cookie rotation');
});

test('default aggregate stays three modules and explicit pan shares that same HTTP owner', async t => {
  for (const pan of [undefined, {}]) {
    const dataRoot = root(t), sessionId = randomUUID();
    const hostFactory = () => ({ list: () => [], async close() { return { cleanupComplete: true }; } });
    const runtime = createRuntime({ sessionId, dataRoot, pan,
      music: { hostFactory }, source: { hostFactory },
      cat: { runtimeFactory: () => ({ async status() { return {}; }, async close() {} }) } });
    await runtime.start();
    const client = createRuntimeClient({ baseUrl: runtime.baseUrl });
    try {
      assert.deepEqual((await client.describe()).value.modules.map(item => item.id),
        pan === undefined ? ['music', 'cat', 'source'] : ['music', 'cat', 'source', 'pan']);
      if (pan === undefined) assert.deepEqual(await client.call('pan.providers'),
        { ok: false, contract: 'runtime.http.v1', code: 'METHOD_NOT_FOUND' });
      else assert.equal((await client.call('pan.providers')).value.providers.length, 5);
    } finally { await client.close(); await runtime.close(); }
  }
});
