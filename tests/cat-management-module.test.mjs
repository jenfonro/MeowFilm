import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { deferred } from '../packages/runtime-bridge/test/http-fixture.js';
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

async function managementGates(t) {
  const detect = deferred(), commit = deferred();
  const sockets = new Set();
  const server = http.createServer((request, response) => {
    const socket = request.socket;
    const responseClosed = new Promise(resolve => response.once('close', resolve));
    const socketClosed = new Promise(resolve => socket.once('close', resolve));
    (request.url === '/detect' ? detect : commit).resolve({
      response, socket, closed: Promise.all([responseClosed, socketClosed]),
    });
  });
  server.on('connection', socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(async () => {
    const closed = new Promise(resolve => server.close(resolve));
    for (const socket of sockets) socket.destroy();
    await closed;
  });
  return { detect: detect.promise, commit: commit.promise,
    origin: `http://127.0.0.1:${server.address().port}` };
}

function credentialSource(origin) {
  return `
    const http = require('http'), fs = require('fs');
    let server, gets = 0, puts = 0, released = 0, responseCloses = 0, socketCloses = 0;
    let writeCloses = 0, putResponse;
    let probeClosed = Promise.resolve(), probeReleased = Promise.resolve(), putFinished = Promise.resolve();
    const gate = path => new Promise((resolve, reject) => {
      const request = http.get(${JSON.stringify(origin)} + path, { agent: false }, response => {
        response.once('end', resolve);
        response.once('error', reject);
        response.resume();
      });
      request.once('error', reject);
      request.once('close', () => reject(new Error('gate closed')));
    });
    const json = (response, value) => response.end(JSON.stringify(value));
    module.exports.start = async () => {
      fs.writeFileSync('/credentials.json', JSON.stringify({cookie:'old'}));
      server = http.createServer(async (request, response) => {
        if (request.url === '/config' || request.url === '/full-config') {
          json(response, {video:{sites:[{key:'default',name:'Management',api:'/api',actions:['home']}]}});
        } else if (request.url === '/website/api/credentials') {
          gets++;
          probeClosed = Promise.all([
            new Promise(resolve => response.once('close', () => { responseCloses++; resolve(); })),
            new Promise(resolve => request.socket.once('close', () => { socketCloses++; resolve(); })),
          ]);
          probeReleased = gate('/detect').catch(() => {}).then(() => {
            released++;
            json(response, {code:0,data:{quark:{cookie:'old'}}});
          });
          await probeReleased;
        } else if (request.url === '/website/api/credential/quark/cookie' && request.method === 'PUT') {
          puts++;
          const chunks = [];
          for await (const chunk of request) chunks.push(chunk);
          const body = JSON.parse(Buffer.concat(chunks).toString());
          // Opening the existing file with r+ does not acknowledge a mutation.
          const output = fs.createWriteStream('/credentials.json', {flags:'r+'});
          const closed = new Promise((resolve, reject) => {
            output.once('close', () => { writeCloses++; resolve(); });
            output.once('error', reject);
          });
          await new Promise(resolve => output.once('open', resolve));
          const allowed = gate('/commit');
          putResponse = response;
          putFinished = new Promise(resolve => response.once('finish', resolve));
          json(response, {code:0});
          await allowed;
          output.end(JSON.stringify({cookie:body.value}));
          await closed;
        } else if (request.url === '/api/home') {
          await Promise.all([probeClosed, probeReleased, putFinished]);
          json(response, {gets,puts,released,responseCloses,socketCloses,writeCloses,
            putFinished:putResponse?.writableFinished ?? false});
        } else { response.statusCode = 404; response.end(); }
      });
      await new Promise(resolve => server.listen(Number(process.env.PORT), '127.0.0.1', resolve));
    };
    module.exports.stop = () => { server.closeAllConnections(); server.close(); };
  `;
}

for (const operation of ['describe', 'sync']) {
  test(`Cat management HTTP ${operation} cancellation closes its probe before a released GET can write credentials`,
    { timeout: 30000 }, async t => {
      const gates = await managementGates(t);
      const { call, dataRoot } = moduleFor(t);
      const installed = result(call, await call('install', { installationId: 'managed',
        sourceRevision: await upload(call, 'managed', 1, credentialSource(gates.origin)) }));
      const healthy = result(call, await call('install', { installationId: 'healthy',
        sourceRevision: await upload(call, 'healthy', 1,
          'let count=0; module.exports={home(){return {count:++count}}};') }));
      const invoke = installationId => call('invoke', { installationId, siteId: 'default', operation: 'home' });
      assert.deepEqual(result(call, await invoke('healthy')), { count: 1 });
      const file = join(dataRoot, 'volumes', 'managed', 'credentials.json');
      const before = readFileSync(file);
      const controller = new AbortController();
      const pending = call('management.' + operation, {
        installationId: 'managed', generation: installed.generation,
        ...(operation === 'sync' ? { provider: 'quark', credentials: { cookie: 'new' } } : {}),
      }, { signal: controller.signal });
      const probe = await gates.detect;
      assert.equal(probe.response.destroyed, false);
      controller.abort();
      assert.deepEqual(await pending, { ok: false, contract: 'cat.script.v2', code: 'CANCELLED' });
      // The external gate can close only after the actual management transport
      // cancels its inbound scope; caller-side CANCELLED alone is not a barrier.
      await probe.closed;
      assert.equal(probe.response.destroyed, true);
      assert.equal(probe.socket.destroyed, true);
      probe.response.end('released after cancellation');
      assert.deepEqual(result(call, await invoke('managed')), {
        gets: 1, puts: 0, released: 1, responseCloses: 1, socketCloses: 1,
        writeCloses: 0, putFinished: false,
      });
      assert.deepEqual(readFileSync(file), before);
      assert.deepEqual(result(call, await invoke('healthy')), { count: 2 });
      const instances = result(call, await call('status')).instances;
      for (const item of [installed, healthy]) {
        const current = instances.find(value => value.installationId === item.installationId);
        assert.equal(current.generation, item.generation);
        assert.equal(current.state, 'ready');
      }
    });
}

test('Cat management HTTP sync returns saved only after its one PUT has a durable private-volume receipt',
  { timeout: 30000 }, async t => {
    const gates = await managementGates(t);
    const { call, dataRoot } = moduleFor(t);
    const installed = result(call, await call('install', { installationId: 'managed',
      sourceRevision: await upload(call, 'managed', 1, credentialSource(gates.origin)) }));
    const file = join(dataRoot, 'volumes', 'managed', 'credentials.json');
    let settled = false, savedBytes;
    const pending = call('management.sync', { installationId: 'managed', generation: installed.generation,
      provider: 'quark', credentials: { cookie: 'new' } }).then(reply => {
      settled = true;
      savedBytes = readFileSync(file, 'utf8');
      return reply;
    });
    const probe = await gates.detect;
    probe.response.end('release probe');
    const commit = await gates.commit;
    const inspect = async () => result(call, await call('invoke', {
      installationId: 'managed', siteId: 'default', operation: 'home',
    }));
    const waiting = await inspect();
    assert.equal(waiting.gets, 1);
    assert.equal(waiting.puts, 1);
    assert.equal(waiting.putFinished, true);
    assert.equal(waiting.writeCloses, 0);
    assert.equal(readFileSync(file, 'utf8'), JSON.stringify({ cookie: 'old' }));
    assert.equal(settled, false, 'an HTTP success without a durable write is not saved');
    commit.response.end('allow real write');
    assert.deepEqual(result(call, await pending), { status: 'saved', protocol: 'website-api-v1' });
    assert.equal(savedBytes, JSON.stringify({ cookie: 'new' }));
    assert.equal(readFileSync(file, 'utf8'), savedBytes);
    await Promise.all([probe.closed, commit.closed]);
    const saved = await inspect();
    assert.equal(saved.puts, 1);
    assert.equal(saved.writeCloses, 1);
    assert.equal(result(call, await call('status')).instances[0].generation, installed.generation);
  });
