import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BridgeError } from '@meowfilm/runtime-bridge';
import { rawRequest } from '../packages/runtime-bridge/test/http-fixture.js';
import { createWindowsNativeSettings } from '../apps/windows/runtime/native-settings.mjs';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createTMDBDataModule } from '../apps/runtime/modules/metadata-data.mjs';

function pipe(t) {
  const requests = [], stopped = [];
  const settings = createWindowsNativeSettings({
    write(line, complete) { requests.push(JSON.parse(line)); complete(); }
  }, code => { stopped.push(code); settings.close(); });
  t.after(() => settings.close());
  function respond(request, document, overrides = {}) {
    const reply = { kind: 'settings.result', id: request.id, ok: true, document,
      fingerprint: { exists: document !== null, length: 0, sha256: null }, ...overrides };
    return Buffer.from(JSON.stringify(reply) + '\n');
  }
  return { settings, requests, stopped, respond };
}

test('Windows private settings handle fragmented replies, detached reads and bounded block access', async t => {
  const f = pipe(t);
  const read = f.settings.reload('tmdb');
  assert.deepEqual(f.requests[0], { kind: 'settings.read', id: 1, block: 'tmdb' });
  const bytes = f.respond(f.requests[0], { version: 1, settings: { language: 'zh-CN' } });
  f.settings.accept(bytes.subarray(0, 11));
  f.settings.accept(bytes.subarray(11));
  assert.equal((await read).settings.language, 'zh-CN');
  f.settings.read('tmdb').settings.language = 'changed-copy';
  assert.equal(f.settings.read('tmdb').settings.language, 'zh-CN');
  await assert.rejects(f.settings.reload('unregistered'), { code: 'INVALID_REQUEST' });
  await assert.rejects(f.settings.write('tmdb', {}, {}), { code: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, 1);
});

test('malformed private settings success retires and rejects the actual pending exchange', async t => {
  const f = pipe(t), read = f.settings.reload('pan.accounts');
  const rejected = assert.rejects(read, { code: 'CLOSED' });
  f.settings.accept(f.respond(f.requests[0], []));
  await rejected;
  assert.deepEqual(f.stopped, ['INVALID_RESPONSE']);
});

test('private settings CAS failure keeps the prior snapshot and close drains pending exchanges', async t => {
  const f = pipe(t), read = f.settings.reload('pan.accounts');
  const before = { version: 1, accounts: {} };
  f.settings.accept(f.respond(f.requests[0], before));
  await read;
  const write = f.settings.write('pan.accounts', before, { exists: true, length: 1, sha256: 'test' });
  const conflict = assert.rejects(write, { code: 'STORAGE_CONFLICT' });
  f.settings.accept(Buffer.from(JSON.stringify({ kind: 'settings.result',
    id: f.requests[1].id, ok: false, code: 'STORAGE_CONFLICT' }) + '\n'));
  await conflict;
  assert.deepEqual(f.settings.read('pan.accounts'), before);
  const pending = f.settings.reload('pan.accounts');
  const closed = assert.rejects(pending, { code: 'CLOSED' });
  f.settings.close();
  await closed;
});

test('private settings reject unknown exchanges and oversize frames without waiting for timeout', async t => {
  for (const oversized of [false, true]) {
    const f = pipe(t), read = f.settings.reload('douban');
    const closed = assert.rejects(read, { code: 'CLOSED' });
    f.settings.accept(oversized ? Buffer.alloc(8 * 1024 * 1024 + 1, 32) :
      f.respond({ id: 99 }, null));
    await closed;
    assert.deepEqual(f.stopped, ['INVALID_RESPONSE']);
  }
});

const tmdb = language => ({ version: 1, settings: { apiBase: 'https://api.themoviedb.org/3',
  imageBase: 'https://image.tmdb.org', apiKey: '', accessToken: '', language, region: 'CN', includeAdult: false } });

test('native settings HTTP notification carries only a block and metadata rereads instead of writing a second file', async t => {
  const root = fileURLToPath(new URL('../tmp/test-results/', import.meta.url));
  mkdirSync(root, { recursive: true });
  const dataRoot = mkdtempSync(join(root, 'windows-native-settings-'));
  let latest = tmdb('zh-CN'), cached = structuredClone(latest);
  const settings = { read: () => structuredClone(cached), async reload() { cached = structuredClone(latest); } };
  const module = createTMDBDataModule({ dataRoot, nativeSettings: settings });
  const notified = [];
  const runtime = createAppRuntime({ sessionId: randomUUID(), modules: [module], async settingsChanged(block) {
    if (block !== 'tmdb') throw new BridgeError('INVALID_REQUEST');
    notified.push(block); await settings.reload(block); module.settingsChanged();
  } });
  t.after(async () => { await runtime.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  await runtime.start();
  const post = input => rawRequest(runtime.baseUrl, {},
    { path: '/v1/settings/changed', body: JSON.stringify(input) }).response;
  assert.equal((await module.methods['tmdb.data.settings.get']({})).value.language, 'zh-CN');
  latest = tmdb('en-US');
  for (const [name, input] of [
    ['settings document', { block: 'tmdb', settings: latest.settings }],
    ['null', null], ['array', ['tmdb']], ['string', 'tmdb'], ['boolean', true], ['number', 1],
    ['missing block', {}], ['untyped block', { block: 1 }], ['empty block', { block: '' }],
    ['uppercase block', { block: 'TMDB' }], ['trailing newline', { block: 'tmdb\n' }],
    ['oversize block', { block: 'a'.repeat(65) }],
  ]) await t.test(name, async () => {
    const reply = await post(input);
    assert.equal(reply.status, 400);
    assert.deepEqual(reply.value, { ok: false, contract: 'runtime.http.v1', code: 'INVALID_REQUEST' });
    assert.deepEqual(notified, []);
    assert.equal(cached.settings.language, 'zh-CN');
  });
  const unknown = await post({ block: 'unregistered' });
  assert.equal(unknown.status, 200);
  assert.deepEqual(unknown.value, { ok: false, contract: 'runtime.http.v1', code: 'INVALID_REQUEST' });
  assert.deepEqual(notified, []);
  const accepted = await post({ block: 'tmdb' });
  assert.equal(accepted.status, 200);
  assert.deepEqual(accepted.value, { ok: true, contract: 'runtime.http.v1', value: { notified: true } });
  assert.deepEqual(notified, ['tmdb']);
  assert.equal((await module.methods['tmdb.data.settings.get']({})).value.language, 'en-US');
  const denied = await rawRequest(runtime.baseUrl, {},
    { path: '/v1/tmdb/settings.update', body: JSON.stringify({ language: 'other' }) }).response;
  assert.equal(denied.status, 200);
  assert.deepEqual(denied.value, { ok: false, contract: 'tmdb.data.v1', code: 'PERMISSION_DENIED' });
  assert.equal((await module.methods['tmdb.data.settings.update']({ language: 'other' })).code, 'PERMISSION_DENIED');
  assert.equal(cached.settings.language, 'en-US');
  assert.deepEqual(readdirSync(dataRoot), []);
});

test('settings notification preserves public errors, redacts unexpected failures and remains usable', async t => {
  let error, calls = 0;
  const runtime = createAppRuntime({ sessionId: randomUUID(), modules: [{
    id: 'tmdb', version: '0.1.0', contract: 'tmdb.data.v1', methods: {}, close() {},
  }], async settingsChanged() {
    calls++;
    if (error) throw error;
  } });
  t.after(() => runtime.close());
  await runtime.start();
  for (const code of ['INVALID_REQUEST', 'PERMISSION_DENIED', 'CLOSED', 'CANCELLED', 'TIMEOUT', 'INVALID_RESPONSE']) {
    await t.test(code, async () => {
      error = new BridgeError(code);
      const reply = await rawRequest(runtime.baseUrl, {},
        { path: '/v1/settings/changed', body: '{"block":"tmdb"}' }).response;
      assert.equal(reply.status, 200);
      assert.deepEqual(reply.value, { ok: false, contract: 'runtime.http.v1', code });
    });
  }
  for (const failure of [new Error('synthetic-private-diagnostic'),
    Object.assign(new Error('synthetic-storage-diagnostic'), { code: 'STORAGE_ERROR' })]) {
    error = failure;
    const reply = await rawRequest(runtime.baseUrl, {},
      { path: '/v1/settings/changed', body: '{"block":"tmdb"}' }).response;
    assert.equal(reply.status, 200);
    assert.deepEqual(reply.value, { ok: false, contract: 'runtime.http.v1', code: 'INVALID_RESPONSE' });
  }
  error = undefined;
  const recovered = await rawRequest(runtime.baseUrl, {},
    { path: '/v1/settings/changed', body: '{"block":"tmdb"}' }).response;
  assert.equal(recovered.status, 200);
  assert.deepEqual(recovered.value, { ok: true, contract: 'runtime.http.v1', value: { notified: true } });
  assert.equal(calls, 9);
});

test('settings notification requires a callable owner at runtime construction', () => {
  for (const settingsChanged of [null, true, 'tmdb', {}]) {
    assert.throws(() => createAppRuntime({ sessionId: randomUUID(), modules: [{
      id: 'tmdb', version: '0.1.0', contract: 'tmdb.data.v1', methods: {}, close() {},
    }], settingsChanged }),
      { code: 'INVALID_REQUEST' });
  }
});
