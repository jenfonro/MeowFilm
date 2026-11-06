import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { ScriptHostError } from '@meowfilm/script-host';
import { createMusicScriptModule, musicScriptContract } from '../apps/runtime/modules/music-script.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { rawRequest, deferred, until, turn } from '../packages/runtime-bridge/test/http-fixture.js';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const success = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
const failure = (reply, code) => { assert.equal(reply.ok, false); if (code) assert.equal(reply.code, code); };
const input = (id = 'one', revision = 1, expectedRevision = 0, sourceRevision = revision) => ({
  installationId: id, sourceRevision, revision, expectedRevision, version: 'fixture', config: {}, permissions: { storage: [] }, grant: randomUUID(),
});
const request = (instance, payload) => ({ installationId: instance.installationId, revision: instance.revision,
  generation: instance.generation, siteId: 'kw', operation: 'musicUrl', payload });
function setup(t, options = {}) {
  const state = { invokes: [], closed: 0, records: [], installs: 0 };
  let generation = 0;
  const host = {
    list: () => state.records, stats: () => ({ cleanupIssues: [] }),
    captureInitialization: () => null,
    observe(id) {
      const record = state.records.find(value => value.installationId === id);
      return () => structuredClone(record);
    },
    async install(spec, context) {
      state.installs++; await options.install?.(spec, context);
      const record = { installationId: spec.installationId, generation: ++generation, state: 'ready',
        capabilities: options.capabilities ?? { kw: { name: 'fixture', type: 'music', actions: ['musicUrl'], qualitys: [] } },
        contentHash: hash(spec.source), protocol: 'lx', version: spec.version };
      state.records = state.records.filter(value => value.installationId !== spec.installationId);
      state.records.push(record); return record;
    },
    async update(id, spec, context) { return host.install(spec, context); },
    async invoke(params, context) { state.invokes.push(params); return options.invoke ? options.invoke(params, context) : params.payload; },
    async uninstall(id) { state.records = state.records.filter(value => value.installationId !== id); },
    async close() { state.closed++; state.records = []; await options.close?.(); return { cleanupComplete: true }; },
  };
  const module = createMusicScriptModule({ call: async () => assert.fail('storage'), hostFactory: () => host });
  const rpc = moduleHttp(t, module);
  return { state, module, rpc };
}
async function load(rpc, params = input(), source = '/* complete source */') {
  success(await rpc.upload(params.installationId, params.sourceRevision, Buffer.from(source), { basename: 'fixture.js' }));
  return success(await rpc('load', params)).instance;
}

test('B1 arbitrary values named blob and receipt remain ordinary business JSON', async t => {
  const { rpc } = setup(t); const instance = await load(rpc);
  for (const payload of [{ blob: { token: 'ordinary value' } }, { token: 'not a transfer', next: 42 }, { value: false }, {}]) {
    assert.deepEqual(await rpc('invoke', request(instance, payload)), { ok: true, contract: musicScriptContract, value: payload });
  }
});

test('B2 complete 512 KiB above 512 KiB above 1 MiB and above 8 MiB retain JSON in one POST', async t => {
  const { rpc, state } = setup(t); const instance = await load(rpc);
  for (const size of [512 * 1024, 512 * 1024 + 1, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1]) {
    const payload = { text: 'x'.repeat(size), structured: [null, false, 1] };
    assert.deepEqual(success(await rpc('invoke', request(instance, payload))), payload);
  }
  assert.equal(state.invokes.length, 4);
});

test('B2 UTF-8 scalars and Unicode keys survive HTTP byte boundaries without normalization', async t => {
  const { rpc, state } = setup(t); const instance = await load(rpc);
  const payload = { text: 'a'.repeat(65535) + '猫😀"\\' + '漢'.repeat(50000),
    values: [null, false, true, 0, -1, 1.25, ['😀'], { text: 'quote"slash\\' }],
    é: 1, 'e\u0301': 2, 中文: [false, null] };
  assert.deepEqual(await rpc('invoke', request(instance, payload)),
    { ok: true, contract: musicScriptContract, value: payload });
  assert.deepEqual(state.invokes.at(-1).payload, payload);
  const output = { text: 'a'.repeat(65535) + '😀猫' };
  assert.deepEqual(await rpc('invoke', request(instance, output)),
    { ok: true, contract: musicScriptContract, value: output });
  assert.deepEqual(state.invokes.at(-1).payload, output);
});

test('B4 malformed and incomplete HTTP inputs never execute or replace a complete candidate', async t => {
  const { rpc, state } = setup(t); await load(rpc);
  const { runtime, identity } = rpc.transport;
  for (const body of [Buffer.from([0xff]), Buffer.from('{'), Buffer.from('{"x":1,"x":2}'), Buffer.from('9007199254740992')]) {
    const result = await rawRequest(runtime.baseUrl, identity, { path: '/v1/music/invoke', body }).response;
    assert.equal(result.status, 400);
  }
  success(await rpc.upload('one', 2, Buffer.from('/* retained */'), { basename: 'fixture.js' }));
  const partial = rawRequest(runtime.baseUrl, identity, { method: 'PUT', path: '/v1/music/sources/one/3?basename=fixture.js',
    headers: { 'content-type': 'application/octet-stream' }, body: '/* partial', end: false });
  await until(() => runtime.state.active > 0);
  partial.request.destroy(); await partial.response.catch(() => {});
  failure(await rpc('load', input('one', 3, 1)), 'SOURCE_NOT_FOUND');
  assert.equal(success(await rpc('load', input('one', 2, 1))).instance.revision, 2);
  assert.equal(state.invokes.length, 0);
});

test('B3 only fixed business routes execute; thrown failures retain safe code and do not retry', async t => {
  const { rpc, state } = setup(t, { invoke: () => { throw new ScriptHostError('TIMEOUT'); } });
  const instance = await load(rpc);
  for (const name of ['eval', 'read', 'arbitrary']) failure(await rpc(name), 'METHOD_NOT_FOUND');
  failure(await rpc('invoke', request(instance, {})), 'TIMEOUT');
  assert.equal(state.invokes.length, 1);
});

test('host lifecycle cleanup failures keep SCRIPT_CLEANUP rather than becoming generic script failures', async t => {
  const { rpc } = setup(t, { install: () => { throw new ScriptHostError('SCRIPT_CLEANUP'); } });
  success(await rpc.upload('one', 1, Buffer.from('x'), { basename: 'x.js' }));
  failure(await rpc('load', input()), 'SCRIPT_CLEANUP');
  failure(await rpc('load', input()), 'SOURCE_NOT_FOUND');
});

test('B3 load/status return complete large declarations and fence retired generations', async t => {
  const capabilities = Object.fromEntries(Array.from({ length: 4000 }, (_, i) => [`site${i}`, { name: '猫'.repeat(50), actions: ['musicUrl'] }]));
  const { rpc } = setup(t, { capabilities }); const instance = await load(rpc);
  assert.deepEqual(instance.capabilities, capabilities);
  assert.deepEqual(success(await rpc('status')).instances[0].capabilities, capabilities);
  success(await rpc('unload', { installationId: 'one', revision: 1 }));
  failure(await rpc('invoke', request(instance, {})), 'SOURCE_NOT_FOUND');
});

test('B4 invalid results fail strict JSON validation while Unicode dictionaries succeed', async t => {
  let output;
  const { rpc } = setup(t, { invoke: () => output }); const instance = await load(rpc);
  for (const invalid of [NaN, Infinity, Number.MAX_SAFE_INTEGER + 1, '\ud800', 1n]) {
    output = { padding: 'x'.repeat(65536), invalid }; failure(await rpc('invoke', request(instance, {})), 'INVALID_REQUEST');
  }
  output = { 猫: false, é: 1, 'e\u0301': 2 };
  assert.deepEqual(success(await rpc('invoke', request(instance, {}))), output);
  output = false;
  assert.deepEqual(await rpc('invoke', request(instance, {})),
    { ok: true, contract: musicScriptContract, value: false });
});

test('B5 pre-cancelled calls do not consume candidates and cancelled trial input is released', async t => {
  const entered = deferred(), released = deferred();
  let slow = false, trialSignal;
  const { rpc, state } = setup(t, { install: async (_spec, { signal }) => {
    if (!slow) return;
    trialSignal = signal;
    entered.resolve(); await released.promise;
    if (signal.aborted) throw new ScriptHostError('CANCELLED');
  } });
  const params = input();
  success(await rpc.upload('one', 1, Buffer.from('x'), { basename: 'x.js' }));
  const cancelled = new AbortController(); cancelled.abort();
  failure(await rpc('load', params, { signal: cancelled.signal }), 'CANCELLED');
  assert.equal(state.installs, 0);
  slow = true;
  const controller = new AbortController(); const trial = rpc('load', params, { signal: controller.signal });
  await entered.promise; controller.abort();
  try {
    failure(await trial, 'CANCELLED');
    await until(() => trialSignal.aborted);
  } finally { released.resolve(); }
  await until(() => rpc.transport.runtime.state.active === 0);
  failure(await rpc('load', params), 'SOURCE_NOT_FOUND');
  assert.deepEqual(success(await rpc('status')).instances, []);
  assert.equal(state.installs, 1);
});

test('B5 close cancels active invocation, clears candidates and waits for physical cleanup', async t => {
  const entered = deferred(), released = deferred(); let signal;
  const { rpc, module, state } = setup(t, { invoke: async (_input, context) => {
    signal = context.signal; entered.resolve(); await released.promise; return 'late';
  }, close: () => released.promise });
  const instance = await load(rpc);
  success(await rpc.upload('candidate', 1, Buffer.from('x'), { basename: 'x.js' }));
  const pending = rpc('invoke', request(instance, {})); await entered.promise;
  let finished = false;
  const closing = module.close().then(result => { finished = true; return result; });
  await turn(); assert.equal(signal.aborted, true); assert.equal(finished, false);
  released.resolve(); failure(await pending, 'CLOSED'); success(await closing);
  assert.equal(state.closed, 1);
  failure(await rpc('load', input('candidate')), 'CLOSED');
});

test('B5 physical HTTP timeout aborts invocation while owner retains unfinished work', async t => {
  const entered = deferred(), released = deferred(); let signal;
  const { rpc } = setup(t, { invoke: async (_input, context) => { signal = context.signal; entered.resolve(); await released.promise; return null; } });
  const instance = await load(rpc);
  const pending = rpc('invoke', request(instance, {}), { timeout: 100 });
  await entered.promise; failure(await pending, 'TIMEOUT'); await until(() => signal.aborted);
  assert.equal(rpc.transport.runtime.state.active, 1);
  released.resolve(); await until(() => rpc.transport.runtime.state.active === 0);
  assert.equal(success(await rpc('status')).instances.length, 1);
});

test('complete candidates enforce CAS preflight exact versions replacement idempotence and unload release', async t => {
  const { rpc, state } = setup(t); const spec = input(); await load(rpc, spec);
  const next = input('one', 5, 1);
  const bytes = Buffer.from('/* version five */');
  const first = success(await rpc.upload('one', 5, bytes, { basename: 'x.js' }));
  assert.deepEqual(success(await rpc.upload('one', 5, bytes, { basename: 'x.js' })), first);
  failure(await rpc.upload('one', 5, Buffer.from('different'), { basename: 'x.js' }), 'SCRIPT_INTEGRITY');
  failure(await rpc('load', { ...next, expectedRevision: 4 }), 'STALE_SOURCE');
  failure(await rpc('load', { ...next, grant: 'invalid' }), 'PERMISSION_DENIED');
  assert.equal(state.installs, 1);
  assert.equal(success(await rpc('load', next)).instance.revision, 5);
  success(await rpc.upload('one', 8, Buffer.from('eight'), { basename: 'x.js' }));
  success(await rpc.upload('one', 9, Buffer.from('nine'), { basename: 'x.js' }));
  failure(await rpc('load', input('one', 8, 5)), 'SOURCE_NOT_FOUND');
  success(await rpc('unload', { installationId: 'one', revision: 5 }));
  failure(await rpc('load', input('one', 9, 0)), 'SOURCE_NOT_FOUND');
});

test('music retains six complete candidates and enforces only the 16 MiB source input boundary', async t => {
  const { rpc, state } = setup(t);
  const exact = Buffer.alloc(16 * 1024 * 1024, 0x61);
  const receipt = success(await rpc.upload('large', 1, exact, { basename: 'x.js' }));
  assert.equal(receipt.sourceBytes, exact.length);
  assert.equal(receipt.contentHash, hash(exact));
  failure(await rpc.upload('large', 2, Buffer.concat([exact, Buffer.from('a')]), { basename: 'x.js' }), 'LIMIT');
  for (let i = 0; i < 6; i++) success(await rpc.upload(`i${i}`, 1, Buffer.from(`complete-${i}`), { basename: 'x.js' }));
  for (let i = 0; i < 6; i++) assert.equal(success(await rpc('load', input(`i${i}`))).instance.installationId, `i${i}`);
  assert.equal(success(await rpc('load', input('large'))).instance.contentHash, receipt.contentHash);
  assert.equal(state.installs, 7);
});

test('slow installation A does not block status B or ordered subsequent changes of A', async t => {
  const entered = deferred(), release = deferred(); const order = [];
  const { rpc } = setup(t, { install: async spec => {
    if (spec.source === 'slow') { entered.resolve(); await release.promise; }
    order.push(spec.source);
  } });
  success(await rpc.upload('a', 1, Buffer.from('slow'), { basename: 'x.js' }));
  const a = rpc('load', input('a')); await entered.promise;
  success(await rpc.upload('a', 2, Buffer.from('second'), { basename: 'x.js' }));
  const second = rpc('load', input('a', 2, 1));
  const b = await load(rpc, input('b'), 'fast');
  assert.equal(b.installationId, 'b');
  assert.equal(success(await rpc('status')).instances.length, 1);
  release.resolve(); success(await a); success(await second);
  assert.deepEqual(order, ['fast', 'slow', 'second']);
});
