import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
const temporaryRoot = new URL('../tmp/test-results/', import.meta.url).pathname;

const source = value => `module.exports = { home() { return { value: ${JSON.stringify(value)} }; } };`;
function moduleFor(t, options = {}) {
  mkdirSync(temporaryRoot, { recursive: true });
  const dataRoot = mkdtempSync(join(temporaryRoot, 'cat-management-module-'));
  const module = createCatScriptModule({ dataRoot, ...options });
  t.after(async () => {
    await module.close();
    rmSync(dataRoot, { recursive: true, force: true });
  });
  return { module, dataRoot, call: moduleHttp(t, module) };
}
async function upload(call, installationId, sourceRevision, value) {
  const bytes = Buffer.isBuffer(value) ? value : Buffer.from(value);
  const reply = await call.upload(installationId, sourceRevision, bytes);
  assert.equal(reply.ok, true, JSON.stringify(reply));
  return reply.value.sourceRevision;
}
function result(_call, reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, 'cat.script.v2');
  return reply.value;
}

test('Cat management HTTP exposes metadata edit update restart and complete authoritative snapshots',
  { timeout: 30000 }, async t => {
    let now = 100;
    const { call } = moduleFor(t, { clock: () => now });
    const legacy = await result(call, await call('install', { installationId: 'legacy',
      sourceRevision: await upload(call, 'legacy', 1, source('legacy')), config: {}, version: '1', enabled: false }));
    assert.equal(legacy.enabled, false);
    assert.equal(legacy.sourceType, 'local');
    assert.equal(legacy.name, 'legacy');
    assert.equal(legacy.version, '1');
    const installed = await result(call, await call('install', { installationId: 'wire',
      sourceRevision: await upload(call, 'wire', 1, source('first')), name: '本地猫源', sourceType: 'local', sourceUrl: null, enabled: true }));
    assert.equal(installed.enabled, true);
    assert.equal(installed.importedAt, 100);
    assert.equal(installed.updatedAt, null);
    for (const method of ['start', 'status']) {
      const snapshot = await result(call, await call(method));
      assert.deepEqual(snapshot.instances.map(item => item.installationId), ['legacy', 'wire']);
      const item = snapshot.instances[1];
      for (const field of ['installationId', 'generation', 'state', 'sourceHash', 'error', 'sites',
        'name', 'sourceType', 'sourceUrl', 'enabled', 'importedAt', 'updatedAt', 'updateState', 'updateError'])
        assert.ok(Object.hasOwn(item, field), field);
      for (const field of ['key', 'name', 'api', 'actions', 'methods'])
        assert.ok(Object.hasOwn(item.sites[0], field), field);
    }
    const edited = await result(call, await call('edit', { installationId: 'wire', name: '已改名' }));
    assert.equal(edited.generation, installed.generation);
    now = 200;
    const updated = await result(call, await call('update', { installationId: 'wire',
      sourceRevision: await upload(call, 'wire', 2, source('second')) }));
    assert.equal(updated.installationId, 'wire');
    assert.equal(updated.updatedAt, 200);
    assert.equal(updated.updateState, 'updated');
    const restarted = await result(call, await call('restart', { installationId: 'wire' }));
    assert.ok(restarted.generation > updated.generation);
    assert.equal(restarted.updatedAt, 200);
    assert.deepEqual(await result(call, await call('invoke', { installationId: 'wire', siteId: 'default',
      generation: restarted.generation, operation: 'home', payload: {} })), { value: 'second' });
    const disabled = await result(call, await call('enable', { installationId: 'wire', enabled: false }));
    assert.deepEqual(disabled.sites, updated.sites);
    assert.deepEqual(await call('restart', { installationId: 'wire' }),
      { ok: false, contract: 'cat.script.v2', code: 'DISABLED' });
    assert.equal((await result(call, await call('uninstall', { installationId: 'wire' }))).applied, true);
    assert.deepEqual((await result(call, await call('status'))).instances.map(item => item.installationId), ['legacy']);
  });

test('Cat HTTP rejects malformed raw uploads without side effects and persists accepted network encoding failures',
  { timeout: 30000 }, async t => {
    let body = source('network');
    const server = http.createServer((_request, response) => response.end(body));
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    t.after(async () => {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    });
    const { call } = moduleFor(t);
    const sourceUrl = `http://127.0.0.1:${server.address().port}/source.js?fixture=1`;
    const imported = await result(call, await call('import', {
      installationId: 'remote', name: '网络', sourceUrl, enabled: true,
    }));
    assert.equal(imported.sourceUrl, sourceUrl);
    assert.equal(imported.sourceType, 'network');
    body = source('changed');
    const updated = await result(call, await call('update', { installationId: 'remote' }));
    assert.equal(updated.updateState, 'updated');
    const local = await result(call, await call('install', { installationId: 'encoding',
      sourceRevision: await upload(call, 'encoding', 1, source('valid')) }));
    await upload(call, 'encoding', 2, source('still pending'));
    assert.deepEqual(await call.upload('encoding', 3, Buffer.from([0xff])),
      { ok: false, contract: 'cat.script.v2', code: 'SCRIPT_PROTOCOL_ERROR' });
    const beforeUpdate = await result(call, await call('status'));
    assert.deepEqual(beforeUpdate.instances.find(item => item.installationId === 'encoding'), local);
    assert.deepEqual(await call('update', { installationId: 'encoding', sourceRevision: 3 }),
      { ok: false, contract: 'cat.script.v2', code: 'NOT_FOUND' });
    assert.equal((await result(call, await call('update', { installationId: 'encoding', sourceRevision: 2 }))).updateState,
      'updated');
    body = Buffer.from([0xff]);
    assert.deepEqual(await call('update', { installationId: 'remote' }),
      { ok: false, contract: 'cat.script.v2', code: 'SCRIPT_PROTOCOL_ERROR' });
    const snapshot = await result(call, await call('status'));
    const invalid = snapshot.instances.find(item => item.installationId === 'remote');
    assert.equal(invalid.updateState, 'failed');
    assert.equal(invalid.updateError, 'SCRIPT_PROTOCOL_ERROR');
    assert.equal(invalid.state, 'ready');
    assert.equal(invalid.updatedAt, updated.updatedAt);
    assert.equal(invalid.sourceHash, updated.sourceHash);
    assert.equal(invalid.generation, updated.generation);
    assert.deepEqual(await result(call, await call('invoke', { installationId: 'remote',
      siteId: 'default', operation: 'home' })), { value: 'changed' });
    assert.deepEqual(await call('install', { installationId: 'no-source', name: 'bad', sourceUrl }),
      { ok: false, contract: 'cat.script.v2', code: 'INVALID_ARGUMENT' });
    assert.deepEqual(await call('edit', { installationId: 'remote', name: 'bad', sourceType: 'local' }),
      { ok: false, contract: 'cat.script.v2', code: 'INVALID_ARGUMENT' });
    assert.deepEqual(await call('update', { installationId: 'remote', sourceUrl }),
      { ok: false, contract: 'cat.script.v2', code: 'INVALID_ARGUMENT' });
  });

test('Cat management HTTP preserves large authoritative snapshots as complete JSON values', async t => {
  const instance = { installationId: 'large', name: '猫'.repeat(30000), sourceType: 'local', sourceUrl: null,
    enabled: false, importedAt: null, updatedAt: null, updateState: 'idle', updateError: null,
    generation: 0, state: 'disabled', sourceHash: 'a'.repeat(64), error: null, sites: [] };
  const { call } = moduleFor(t, { runtimeFactory: () => ({
    async start() { return { instances: [instance] }; },
    async status() { return { instances: [instance] }; },
    async edit() { return instance; },
    async close() {},
  }) });
  for (const method of ['start', 'status']) {
    const reply = await call(method);
    assert.deepEqual(reply.value, { instances: [instance] });
    assert.deepEqual(await result(call, reply), { instances: [instance] });
  }
  const edited = await call('edit', { installationId: 'large', name: instance.name });
  assert.deepEqual(edited.value, instance);
  assert.deepEqual(await result(call, edited), instance);
});

test('Cat module close cancels a pending network update and leaves the accepted source durable',
  { timeout: 30000 }, async t => {
    let resolveEntered, resolveClosed, hang = false;
    const entered = new Promise(resolve => { resolveEntered = resolve; });
    const disconnected = new Promise(resolve => { resolveClosed = resolve; });
    const server = http.createServer((_request, response) => {
      if (!hang) response.end(source('accepted'));
      else { response.on('close', resolveClosed); resolveEntered(); }
    });
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    t.after(async () => {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    });
    const { module, call, dataRoot } = moduleFor(t);
    const original = await result(call, await call('import', { installationId: 'closing', name: 'closing',
      sourceUrl: `http://127.0.0.1:${server.address().port}` }));
    hang = true;
    const update = call('update', { installationId: 'closing' });
    await entered;
    await module.close();
    assert.deepEqual(await update, { ok: false, contract: 'cat.script.v2', code: 'CANCELLED' });
    await disconnected;
    const saved = JSON.parse(readFileSync(join(dataRoot, 'settings.json'), 'utf8')).scripts[0];
    assert.equal(saved.sourceHash, original.sourceHash);
    assert.equal(saved.updatedAt, null);
    assert.equal(saved.updateState, 'failed');
    assert.equal(saved.updateError, 'CANCELLED');
    assert.deepEqual(await call('status'), { ok: false, contract: 'cat.script.v2', code: 'HOST_CLOSED' });
  });
