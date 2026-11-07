import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdirSync, rmSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { join } from 'node:path';
import { createCatRuntime, ScriptHostError } from '@meowfilm/script-host';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { deferred, until, turn } from '../packages/runtime-bridge/test/http-fixture.js';
const temporaryRoot = new URL('../tmp/test-results/', import.meta.url).pathname;

const temporary = join(temporaryRoot, 'cat-script-module-tests');
mkdirSync(temporary, { recursive: true });
after(() => rmSync(temporary, { recursive: true, force: true }));
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };

test('Cat source transfer preserves Unicode and rejects malformed UTF-8 and BOM', async t => {
  const received = [];
  const module = createCatScriptModule({
    dataRoot: temporary,
    runtimeFactory: () => ({
      async install(input) { received.push(input.source); return { state: 'disabled' }; },
      async close() {},
    }),
  });
  t.after(() => module.close());
  const rpc = moduleHttp(t, module);
  async function install(bytes) {
    const installationId = randomUUID();
    const uploaded = await rpc.upload(installationId, 1, bytes);
    if (!uploaded.ok) return uploaded;
    return rpc('install', { installationId, sourceRevision: 1 });
  }
  const source = "// 中文🎵\uFFFD\nmodule.exports = {};";
  assert.equal((await install(Buffer.from(source))).ok, true);
  assert.deepEqual(received, [source]);
  for (const bytes of [[0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80],
    [0xe2, 0x82], [0xf4, 0x90, 0x80, 0x80]]) {
    assert.deepEqual(await install(Buffer.from(bytes)), {
      ok: false, contract: 'cat.script.v2', code: 'SCRIPT_PROTOCOL_ERROR',
    });
  }
  assert.deepEqual(await install(Buffer.from('\uFEFF' + source)), {
    ok: false, contract: 'cat.script.v2', code: 'SCRIPT_PROTOCOL_ERROR',
  });
  assert.deepEqual(received, [source]);
});

test('Cat close clears complete candidates and preserves a real runtime cleanup failure', async t => {
  let closes = 0;
  const module = createCatScriptModule({ dataRoot: temporary, runtimeFactory: () => ({
    async status() { return {}; }, async close() { closes++; throw new Error('private cleanup failure'); },
  }) });
  const rpc = moduleHttp(t, module);
  assert.equal((await rpc.upload('candidate', 1, Buffer.from('module.exports = {};'))).ok, true);
  assert.equal((await rpc('status')).ok, true);
  await assert.rejects(module.close(), /private cleanup failure/);
  await assert.rejects(module.close(), /private cleanup failure/);
  assert.equal(closes, 1);
  assert.equal((await rpc('install', { installationId: 'candidate', sourceRevision: 1 })).code, 'HOST_CLOSED');
});

test('Cat RPC reports one abort-listener cleanup failure and preserves the operation failure', async () => {
  const signal = {
    aborted: false,
    reason: undefined,
    listener: null,
    removals: 0,
    addEventListener(_event, listener) { this.listener = listener; },
    removeEventListener(_event, listener) {
      assert.equal(listener, this.listener);
      this.removals++;
      throw new Error('synthetic detach failure');
    },
  };
  const module = createCatScriptModule({
    dataRoot: temporary,
    runtimeFactory: () => ({
      async status() { return { state: 'ready' }; },
      async close() {},
    }),
  });
  const reply = await module.methods['cat.script.status']({}, { signal });
  assert.deepEqual(reply, { ok: false, contract: 'cat.script.v2', code: 'SCRIPT_EXCEPTION' });
  assert.equal(signal.removals, 1);
  await module.close();
  assert.doesNotThrow(() => signal.listener());
  assert.equal(signal.removals, 1);

  const permanent = {
    aborted: false,
    reason: undefined,
    removals: 0,
    addEventListener() {},
    removeEventListener() { this.removals++; throw new Error('synthetic permanent detach failure'); },
  };
  const failing = createCatScriptModule({
    dataRoot: temporary,
    runtimeFactory: () => ({
      async status() { throw new ScriptHostError('TIMEOUT'); },
      async close() {},
    }),
  });
  assert.deepEqual(await failing.methods['cat.script.status']({}, { signal: permanent }), {
    ok: false, contract: 'cat.script.v2', code: 'TIMEOUT',
  });
  assert.equal(permanent.removals, 1);
  await failing.close();
});

test('Cat RPC exposes starting status and preserves STARTING from invocation', async () => {
  const snapshot = {
    settingsPath: join(temporary, 'settings.json'),
    aggregate: { origin: 'http://127.0.0.1:1', access: 'fixture' },
    instances: [{ installationId: 'pending', state: 'starting' }],
  };
  const module = createCatScriptModule({
    dataRoot: temporary,
    runtimeFactory: () => ({
      async start() { return snapshot; },
      async status() { return snapshot; },
      async invoke() { throw new ScriptHostError('STARTING'); },
      async close() {},
    }),
  });
  const started = await module.methods['cat.script.start']({});
  assert.equal(started.ok, true);
  assert.deepEqual(started.value, snapshot);
  assert.deepEqual(await module.methods['cat.script.invoke']({
    installationId: 'pending', siteId: 'site', operation: 'home', payload: {},
  }), { ok: false, contract: 'cat.script.v2', code: 'STARTING' });
  await module.close();
});

test('Cat direct JSON preserves 12 MiB payloads Unicode keys config and management credentials', async t => {
  const captured = [];
  let invoked;
  const module = createCatScriptModule({ dataRoot: temporary, runtimeFactory: () => ({
    async install(spec) { captured.push(spec); return { state: 'ready', generation: 1 }; },
    async invoke(target) { invoked = target; return target.payload; },
    async sync(id, generation, input) { captured.push({ id, generation, input }); return input.credentials; },
    async close() {},
  }) });
  const rpc = moduleHttp(t, module);
  const config = { 中文: '配置', é: 1, 'e\u0301': 2 };
  assert.equal((await rpc.upload('cat', 1, Buffer.from('module.exports = {};'))).ok, true);
  assert.equal((await rpc('install', { installationId: 'cat', sourceRevision: 1,
    contentHash: '0'.repeat(64) })).code, 'INVALID_ARGUMENT');
  assert.equal((await rpc('install', { installationId: 'cat', sourceRevision: 1, config })).ok, true);
  assert.deepEqual(captured[0].config, config);
  const payload = { text: 'x'.repeat(12 * 1024 * 1024), ...config };
  const siteId = '站点 / ' + '猫'.repeat(200);
  assert.deepEqual((await rpc('invoke', { installationId: 'cat', siteId, operation: 'home', payload })).value, payload);
  assert.equal(invoked.siteId, siteId);
  const credentials = { account: 'fixture', nested: config };
  assert.deepEqual((await rpc('management.sync', { installationId: 'cat', provider: 'quark', credentials })).value, credentials);
  assert.deepEqual(captured[1].input.credentials, credentials);
  assert.equal((await rpc('invoke', { installationId: 'cat', siteId: 'site', operation: 'home', payload: '{}' })).code, 'INVALID_ARGUMENT');
});

test('Cat complete source accepts exactly 16 MiB and rejects the next byte without replacing it', async t => {
  const module = createCatScriptModule({ dataRoot: temporary, runtimeFactory: () => ({
    async install(spec) { return { bytes: Buffer.byteLength(spec.source) }; }, async close() {},
  }) });
  const rpc = moduleHttp(t, module);
  const bytes = Buffer.alloc(16 * 1024 * 1024, 0x61);
  assert.equal((await rpc.upload('cat', 1, bytes)).value.sourceBytes, bytes.length);
  assert.equal((await rpc.upload('cat', 2, Buffer.concat([bytes, Buffer.from('x')]))).code, 'RESOURCE_LIMIT');
  assert.equal((await rpc('install', { installationId: 'cat', sourceRevision: 1 })).value.bytes, bytes.length);
  assert.equal((await rpc('install', { installationId: 'cat', sourceRevision: 2 })).code, 'NOT_FOUND');
});

test('Cat concurrent admitted imports retain exact complete inputs until their own settlement and uninstall', async t => {
  const firstGate = deferred(), secondGate = deferred(), uninstallGate = deferred();
  const received = [];
  let uninstalls = 0;
  t.after(() => { firstGate.resolve(); secondGate.resolve(); uninstallGate.resolve(); });
  const module = createCatScriptModule({ dataRoot: temporary, runtimeFactory: () => ({
    async install(input) {
      received.push(input);
      const order = received.length;
      await (order === 1 ? firstGate : secondGate).promise;
      if (order === 1) throw new ScriptHostError('CANCELLED');
      return { state: 'ready', generation: 2 };
    },
    async uninstall() { uninstalls++; await uninstallGate.promise; return { applied: true }; },
    async close() {},
  }) });
  const rpc = moduleHttp(t, module), source = Buffer.from('module.exports.home = () => "held";');
  okay(await rpc.upload('cat', 1, source));
  const first = rpc('install', { installationId: 'cat', sourceRevision: 1 });
  await until(() => received.length === 1);
  const second = rpc('install', { installationId: 'cat', sourceRevision: 1 });
  await until(() => received.length === 2);
  okay(await rpc.upload('cat', 2, Buffer.from('module.exports.home = () => "next";')));
  assert.equal((await rpc.upload('cat', 1, Buffer.from('different'))).code, 'INVALID_ARGUMENT');
  firstGate.resolve();
  assert.equal((await first).code, 'CANCELLED');
  assert.equal((await rpc.upload('cat', 1, Buffer.from('different'))).code, 'INVALID_ARGUMENT');
  okay(await rpc.upload('cat', 1, source));
  const uninstall = rpc('uninstall', { installationId: 'cat' });
  await until(() => uninstalls === 1);
  secondGate.resolve();
  okay(await second);
  uninstallGate.resolve();
  okay(await uninstall);
  for (const sourceRevision of [1, 2]) {
    assert.equal((await rpc('install', { installationId: 'cat', sourceRevision })).code, 'NOT_FOUND');
  }
  assert.deepEqual(received.map(input => input.source), [source.toString(), source.toString()]);
});

const catFixture = marker => `let count = 0; module.exports.home = () => ({ marker: ${JSON.stringify(marker)}, count: ++count });`;
const gatedCat = url => catFixture('never-published') + `
  const http = require('http');
  module.exports.start = () => new Promise((resolve, reject) => {
    const request = http.get(${JSON.stringify(url)}, { agent: false }, response => {
      response.once('error', reject);
      response.once('end', resolve);
      response.resume();
    });
    request.once('error', reject);
  });
`;
async function catInitGate(t) {
  const entered = deferred(), disconnected = deferred();
  const server = createServer((_request, response) => {
    response.once('close', () => disconnected.resolve());
    entered.resolve(response);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => {
    server.closeAllConnections();
    return new Promise(resolve => server.close(resolve));
  });
  return { url: `http://127.0.0.1:${server.address().port}/init`, entered, disconnected };
}
function realCat(t) {
  let runtime;
  const module = createCatScriptModule({ dataRoot: join(temporary, randomUUID()),
    runtimeFactory: options => runtime = createCatRuntime(options) });
  const rpc = moduleHttp(t, module);
  const install = async (id, source, revision = 1) => {
    okay(await rpc.upload(id, revision, Buffer.from(source)));
    return okay(await rpc('install', { installationId: id, sourceRevision: revision, enabled: true }));
  };
  const invoke = (id, generation) => rpc('invoke', {
    installationId: id, generation, siteId: 'default', operation: 'home', payload: {},
  });
  return { rpc, runtime: () => runtime, install, invoke };
}

for (const operation of ['enable', 'uninstall', 'install']) {
  test(`real HTTP Cat ${operation} reaches a pending initialization before the installation transaction`,
    { timeout: 30000 }, async t => {
      const gate = await catInitGate(t), { rpc, runtime, install, invoke } = realCat(t);
      const b = await install('b', catFixture('b'));
      okay(await rpc.upload('a', 1, Buffer.from(gatedCat(gate.url))));
      const controller = new AbortController();
      const trial = rpc('install', { installationId: 'a', sourceRevision: 1, enabled: true },
        { signal: controller.signal });
      const response = await gate.entered.promise;
      const candidate = runtime().host.initializingSessions().find(session => session.installationId === 'a');
      assert.ok(candidate);
      okay(await rpc.upload('a', 2, Buffer.from(catFixture('next'))));
      assert.equal((await rpc('enable', { installationId: 'a', enabled: 'false' })).code, 'INVALID_ARGUMENT');
      assert.equal((await rpc('install', { installationId: 'a', sourceRevision: 2, version: false })).code,
        'INVALID_ARGUMENT');
      assert.equal(candidate.live, true, 'invalid fields must not stop the current owner');
      assert.equal(response.destroyed, false);
      const starting = okay(await rpc('status')).instances.find(instance => instance.installationId === 'a');
      assert.equal(starting.state, 'starting');
      assert.equal(starting.sourceHash, null);
      assert.deepEqual(starting.sites, []);
      assert.equal(runtime().settings.read().scripts.some(script => script.installationId === 'a'), false);
      assert.deepEqual(okay(await invoke('b', b.generation)), { marker: 'b', count: 1 });
      const managing = rpc(operation, { installationId: 'a',
        ...(operation === 'enable' ? { enabled: false }
          : operation === 'install' ? { sourceRevision: 2, enabled: true } : {}) });
      await gate.disconnected.promise;
      assert.equal(response.writableFinished, false, 'initialization I/O was cancelled, not released by the fixture');
      assert.equal(controller.signal.aborted, false);
      assert.equal((await trial).code, operation === 'install' ? 'STALE_SESSION' : 'CANCELLED');
      const managed = await managing;
      const result = operation === 'enable' ? null : okay(managed);
      if (operation === 'enable') assert.deepEqual(managed,
        { ok: false, contract: 'cat.script.v2', code: 'NOT_FOUND' });
      assert.equal(candidate.worker, null);
      assert.equal(runtime().host.initializingSessions().length, 0);
      assert.deepEqual(okay(await invoke('b', b.generation)), { marker: 'b', count: 2 });
      if (operation === 'install') {
        controller.abort();
        assert.deepEqual(okay(await invoke('a', result.generation)), { marker: 'next', count: 1 });
      } else assert.equal(okay(await rpc('status')).instances.some(instance => instance.installationId === 'a'), false);
    });
}

test('real HTTP refresh disconnect reports failed A after real exit and keeps B and later A usable',
  { timeout: 30000 }, async t => {
    const gate = await catInitGate(t), { rpc, runtime, install, invoke } = realCat(t);
    const a = await install('a', catFixture('old-a')), b = await install('b', catFixture('b'));
    const oldOwner = runtime().getSession('a');
    assert.deepEqual(okay(await invoke('a', a.generation)), { marker: 'old-a', count: 1 });
    const settingsPath = okay(await rpc('status')).settingsPath;
    const settings = JSON.parse(readFileSync(settingsPath, 'utf8'));
    const spec = settings.scripts.find(script => script.installationId === 'a');
    writeFileSync(join(runtime().dataRoot, 'sources', spec.sourceHash + '.js'), gatedCat(gate.url));
    spec.version = '2.0.0';
    writeFileSync(settingsPath, JSON.stringify(settings));
    const controller = new AbortController();
    const refreshing = rpc('refresh', {}, { signal: controller.signal });
    const response = await gate.entered.promise;
    const candidate = runtime().host.initializingSessions().find(session => session.installationId === 'a');
    assert.ok(candidate);
    controller.abort();
    assert.equal((await refreshing).code, 'CANCELLED');
    await gate.disconnected.promise;
    await candidate.stopping;
    await until(() => rpc.transport.runtime.state.active === 0);
    assert.equal(response.writableFinished, false);
    assert.equal(candidate.worker, null);
    assert.equal(oldOwner.worker, null);
    assert.equal(runtime().host.get('a').generation, candidate.generation);
    const states = okay(await rpc('status')).instances;
    assert.equal(states.find(instance => instance.installationId === 'a').state, 'failed');
    assert.equal(states.find(instance => instance.installationId === 'a').error, 'CANCELLED');
    assert.equal(states.find(instance => instance.installationId === 'b').generation, b.generation);
    assert.equal((await invoke('a', a.generation)).code, 'DISABLED');
    assert.deepEqual(okay(await invoke('b', b.generation)), { marker: 'b', count: 1 });
    const next = await install('a', catFixture('new-a'), 2);
    assert.ok(next.generation > a.generation);
    response.end('late original initialization');
    controller.abort();
    await turn();
    assert.deepEqual(okay(await invoke('a', next.generation)), { marker: 'new-a', count: 1 });
    assert.equal(runtime().host.get('a').generation, next.generation);
  });
