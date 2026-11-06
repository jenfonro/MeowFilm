import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { ScriptHostError } from '@meowfilm/script-host';
import { encodeJSONValue } from '@meowfilm/runtime-bridge';
import { createMusicScriptModule, musicScriptContract } from '../apps/runtime/modules/music-script.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { rawRequest, deferred, until, turn } from '../packages/runtime-bridge/test/http-fixture.js';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const success = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
const failure = (reply, code) => { assert.equal(reply.ok, false); if (code) assert.equal(reply.code, code); };
const ids = new Map();
const idOf = name => { if (!ids.has(name)) ids.set(name, randomUUID()); return ids.get(name); };
const input = (id = 'one', sourceRevision = 1) => ({ installationId: idOf(id), sourceRevision });
const request = (instance, payload) => ({ installationId: instance.installationId,
  siteId: 'kw', operation: 'musicUrl', payload });
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
  const dataRoot = mkdtempSync(join(tmpdir(), 'music-http-values-'));
  t.after(() => rmSync(dataRoot, { recursive: true, force: true }));
  const module = createMusicScriptModule({ dataRoot, hostFactory: () => host });
  const rpc = moduleHttp(t, module);
  return { state, module, rpc };
}
async function load(rpc, params = input(), source = '/* complete source */') {
  success(await rpc.upload(params.installationId, params.sourceRevision, Buffer.from(source), { basename: 'fixture.js' }));
  success(await rpc('sources.add', params));
  return success(await rpc('sources.enable', { installationId: params.installationId })).item;
}

test('B1 arbitrary values named blob and receipt remain ordinary business JSON', async t => {
  const { rpc } = setup(t); const instance = await load(rpc);
  for (const payload of [{ blob: { token: 'ordinary value' } }, { token: 'not a transfer', next: 42 }, { value: false }, {},
    ...[null, false, 0, ['a', null, false, 1.25], { blob: { token: 'business' }, ok: true }].map(value => ({ value }))]) {
    assert.deepEqual(await rpc('invoke', request(instance, payload)), { ok: true, contract: musicScriptContract, value: payload });
  }
  const envelope = payload => ({ ok: true, contract: musicScriptContract, value: payload });
  for (const length of [65536, 65537]) {
    const payload = { value: 'x'.repeat(length - encodeJSONValue(envelope({ value: '' })).length) };
    const reply = await rpc('invoke', request(instance, payload));
    assert.equal(encodeJSONValue(reply).length, length);
    assert.deepEqual(reply, envelope(payload));
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
  for (const body of [Buffer.from([0xff]), Buffer.from([0xc3, 0x28]), Buffer.from('{'),
    Buffer.from('{"x":1,"x":2}'), Buffer.from('{"x":0,"\\u0078":1}'),
    Buffer.from('{"x":"\\ud800"}'), Buffer.from('{"x":1e400}'), Buffer.from('{"x":9007199254740992}'),
    Buffer.from('['.repeat(66) + '0' + ']'.repeat(66)), Buffer.from('9007199254740992')]) {
    const result = await rawRequest(runtime.baseUrl, identity, { path: '/v1/music/invoke', body }).response;
    assert.equal(result.status, 400);
  }
  for (const value of [null, false, [], { 非ASCII: 1 }])
    failure(await rpc('invoke', value), 'INVALID_REQUEST');
  success(await rpc.upload(idOf('one'), 2, Buffer.from('/* retained */'), { basename: 'fixture.js' }));
  const partial = rawRequest(runtime.baseUrl, identity, { method: 'PUT', path: `/v1/music/sources/${idOf('one')}/3?basename=fixture.js`,
    headers: { 'content-type': 'application/octet-stream' }, body: '/* partial', end: false });
  await until(() => runtime.state.active > 0);
  partial.request.destroy(); await partial.response.catch(() => {});
  failure(await rpc('sources.update', input('one', 3)), 'SOURCE_NOT_FOUND');
  assert.equal(success(await rpc('sources.update', input('one', 2))).item.contentHash, hash('/* retained */'));
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
  success(await rpc.upload(idOf('one'), 1, Buffer.from('x'), { basename: 'x.js' }));
  success(await rpc('sources.add', input()));
  failure(await rpc('sources.enable', { installationId: idOf('one') }), 'SCRIPT_CLEANUP');
});

test('B3 enable/list return complete large declarations and fence retired generations', async t => {
  const capabilities = Object.fromEntries(Array.from({ length: 4000 }, (_, i) => [`site${i}`, { name: '猫'.repeat(50), actions: ['musicUrl'] }]));
  const { rpc } = setup(t, { capabilities }); const instance = await load(rpc);
  assert.deepEqual(instance.capabilities, capabilities);
  assert.deepEqual(success(await rpc('sources.list')).items[0].capabilities, capabilities);
  success(await rpc('sources.remove', { installationId: idOf('one') }));
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

test('B5 pre-cancelled add keeps its candidate and a cancelled update releases only its exact input', async t => {
  const entered = deferred(), released = deferred();
  let slow = false, trialSignal;
  const { rpc, state } = setup(t, { install: async (_spec, { signal }) => {
    if (!slow) return;
    trialSignal = signal;
    entered.resolve(); await released.promise;
    if (signal.aborted) throw new ScriptHostError('CANCELLED');
  } });
  const params = input();
  success(await rpc.upload(params.installationId, 1, Buffer.from('x'), { basename: 'x.js' }));
  const cancelled = new AbortController(); cancelled.abort();
  failure(await rpc('sources.add', params, { signal: cancelled.signal }), 'CANCELLED');
  assert.equal(state.installs, 0);
  success(await rpc('sources.add', params));
  const item = success(await rpc('sources.enable', { installationId: params.installationId })).item;
  success(await rpc.upload(params.installationId, 2, Buffer.from('trial'), { basename: 'x.js' }));
  slow = true;
  const controller = new AbortController();
  const trial = rpc('sources.update', input('one', 2), { signal: controller.signal });
  await entered.promise; controller.abort();
  try {
    failure(await trial, 'CANCELLED');
    await until(() => trialSignal.aborted);
  } finally { released.resolve(); }
  await until(() => rpc.transport.runtime.state.active === 0);
  failure(await rpc('sources.update', input('one', 2)), 'SOURCE_NOT_FOUND');
  assert.equal(success(await rpc('sources.list')).items[0].generation, item.generation);
  assert.equal(state.installs, 2);
});

test('B5 close cancels active invocation, clears candidates and waits for physical cleanup', async t => {
  const entered = deferred(), released = deferred(); let signal;
  const { rpc, module, state } = setup(t, { invoke: async (_input, context) => {
    signal = context.signal; entered.resolve(); await released.promise; return 'late';
  }, close: () => released.promise });
  const instance = await load(rpc);
  success(await rpc.upload(idOf('candidate'), 1, Buffer.from('x'), { basename: 'x.js' }));
  const pending = rpc('invoke', request(instance, {})); await entered.promise;
  let finished = false;
  const closing = module.close().then(result => { finished = true; return result; });
  await turn(); assert.equal(signal.aborted, true); assert.equal(finished, false);
  released.resolve(); failure(await pending, 'CLOSED'); success(await closing);
  assert.equal(state.closed, 1);
  failure(await rpc('sources.add', input('candidate')), 'CLOSED');
});

test('B5 physical HTTP timeout aborts invocation while owner retains unfinished work', async t => {
  const entered = deferred(), released = deferred(); let signal;
  const { rpc } = setup(t, { invoke: async (_input, context) => { signal = context.signal; entered.resolve(); await released.promise; return null; } });
  const instance = await load(rpc);
  const pending = rpc('invoke', request(instance, {}), { timeout: 100 });
  await entered.promise; failure(await pending, 'TIMEOUT'); await until(() => signal.aborted);
  assert.equal(rpc.transport.runtime.state.active, 1);
  released.resolve(); await until(() => rpc.transport.runtime.state.active === 0);
  assert.equal(success(await rpc('sources.list')).items.length, 1);
});

test('complete candidates select exact revisions, reject changed content or basename, and retire on disable', async t => {
  const { rpc, state } = setup(t); await load(rpc);
  const next = input('one', 5);
  const bytes = Buffer.from('/* version five */');
  const first = success(await rpc.upload(idOf('one'), 5, bytes, { basename: 'x.js' }));
  assert.deepEqual(success(await rpc.upload(idOf('one'), 5, bytes, { basename: 'x.js' })), first);
  failure(await rpc.upload(idOf('one'), 5, Buffer.from('different'), { basename: 'x.js' }), 'SCRIPT_INTEGRITY');
  failure(await rpc.upload(idOf('one'), 5, bytes, { basename: 'other.js' }), 'SCRIPT_INTEGRITY');
  failure(await rpc('sources.update', { ...next, sourceRevision: 4 }), 'SOURCE_NOT_FOUND');
  failure(await rpc('sources.update', { ...next, permissions: {} }), 'INVALID_REQUEST');
  assert.equal(state.installs, 1);
  const updated = success(await rpc('sources.update', next)).item;
  assert.equal(updated.contentHash, hash(bytes));
  assert.equal(success(await rpc('sources.update', next)).item.generation, updated.generation);
  assert.equal(state.installs, 2);
  success(await rpc.upload(idOf('one'), 8, Buffer.from('eight'), { basename: 'x.js' }));
  success(await rpc.upload(idOf('one'), 9, Buffer.from('nine'), { basename: 'x.js' }));
  failure(await rpc('sources.update', input('one', 8)), 'SOURCE_NOT_FOUND');
  success(await rpc('sources.disable', { installationId: idOf('one') }));
  failure(await rpc('sources.update', input('one', 9)), 'SOURCE_NOT_FOUND');
});

test('music retains six complete candidates and enforces only the 16 MiB source input boundary', async t => {
  const { rpc, state } = setup(t);
  const exact = Buffer.alloc(16 * 1024 * 1024, 0x61);
  const receipt = success(await rpc.upload(idOf('large'), 1, exact, { basename: 'x.js' }));
  assert.equal(receipt.sourceBytes, exact.length);
  assert.equal(receipt.contentHash, hash(exact));
  failure(await rpc.upload(idOf('large'), 2, Buffer.concat([exact, Buffer.from('a')]), { basename: 'x.js' }), 'LIMIT');
  for (let i = 0; i < 6; i++) success(await rpc.upload(idOf(`i${i}`), 1, Buffer.from(`complete-${i}`), { basename: 'x.js' }));
  for (let i = 0; i < 6; i++) {
    assert.equal(success(await rpc('sources.add', input(`i${i}`))).item.installationId, idOf(`i${i}`));
    success(await rpc('sources.enable', { installationId: idOf(`i${i}`) }));
  }
  assert.equal(success(await rpc('sources.add', input('large'))).item.contentHash, receipt.contentHash);
  success(await rpc('sources.enable', { installationId: idOf('large') }));
  assert.equal(state.installs, 7);
});

test('slow installation A does not block list or B while edits of A stay ordered', async t => {
  const entered = deferred(), release = deferred(); const order = [];
  const { rpc } = setup(t, { install: async spec => {
    if (spec.source === 'slow') { entered.resolve(); await release.promise; }
    order.push(spec.source);
  } });
  success(await rpc.upload(idOf('a'), 1, Buffer.from('slow'), { basename: 'x.js' }));
  success(await rpc('sources.add', input('a')));
  const a = rpc('sources.enable', { installationId: idOf('a') }); await entered.promise;
  const second = rpc('sources.edit', { installationId: idOf('a'), name: 'second' });
  const b = await load(rpc, input('b'), 'fast');
  assert.equal(b.installationId, idOf('b'));
  assert.equal(success(await rpc('sources.list')).items.length, 2);
  release.resolve(); success(await a); success(await second);
  assert.deepEqual(order, ['fast', 'slow']);
  assert.equal(success(await rpc('sources.detail', { installationId: idOf('a') })).item.name, 'second');
});

test('main exact-size request and result samples remain complete JSON at every former transport boundary', async t => {
  const { rpc, state } = setup(t); const instance = await load(rpc);
  for (const length of [512 * 1024, 512 * 1024 + 1, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1]) {
    const input = request(instance, { text: '' });
    input.payload.text = 'x'.repeat(length - encodeJSONValue(input).length);
    assert.equal(encodeJSONValue(input).length, length);
    assert.deepEqual(success(await rpc('invoke', input)), input.payload);
    assert.deepEqual(state.invokes.at(-1).payload, input.payload);
    const empty = { ok: true, contract: musicScriptContract, value: { text: '' } };
    const output = { text: 'r'.repeat(length - encodeJSONValue(empty).length) };
    const reply = await rpc('invoke', request(instance, output));
    assert.equal(encodeJSONValue(reply).length, length);
    assert.deepEqual(success(reply), output);
  }
  assert.equal(success(await rpc('sources.list')).items[0].state, 'ready');
});

test('main UTF-8 scalar positions survive complete request and response serialization', async t => {
  const { rpc, state } = setup(t); const instance = await load(rpc);
  const input = request(instance, { text: '' });
  const prefix = encodeJSONValue(input).indexOf(Buffer.from('"text":"')) + Buffer.byteLength('"text":"');
  input.payload.text = 'a'.repeat(65536 - prefix - 1) + '猫😀"\\' + '漢'.repeat(50000);
  input.payload.values = [null, false, true, 0, -1, 1.25, ['😀'], { text: 'quote"slash\\' }];
  const bytes = encodeJSONValue(input);
  assert.equal(bytes[65535], 0xe7); assert.equal(bytes[65536], 0x8c);
  assert.deepEqual(success(await rpc('invoke', input)), input.payload);
  assert.deepEqual(state.invokes.at(-1).payload, input.payload);
  const envelope = value => ({ ok: true, contract: musicScriptContract, value });
  const outputPrefix = encodeJSONValue(envelope({ text: '' })).indexOf(Buffer.from('"text":"')) + Buffer.byteLength('"text":"');
  const output = { text: 'a'.repeat(65536 - outputPrefix - 1) + '😀猫' };
  const expected = encodeJSONValue(envelope(output));
  assert.equal(expected[65535], 0xf0); assert.equal(expected[65536], 0x9f);
  assert.deepEqual(await rpc('invoke', request(instance, output)), envelope(output));
});

test('main large source metadata list stays complete and disable fences a previously prepared call', async t => {
  const { rpc, state } = setup(t);
  const header = `/**\n * @name ${'猫'.repeat(255)}\n * @description ${'漢'.repeat(1024)}\n` +
    ` * @author ${'作'.repeat(1024)}\n * @homepage ${'页'.repeat(1024)}\n */\n`;
  for (let index = 0; index < 8; index++) {
    const id = randomUUID();
    success(await rpc.upload(id, 1, Buffer.from(`${header}// ${index}`), { basename: 'metadata.js' }));
    success(await rpc('sources.add', { installationId: id, sourceRevision: 1 }));
  }
  const item = await load(rpc);
  const listed = await rpc('sources.list');
  assert.ok(encodeJSONValue(listed).length > 65536);
  assert.equal(success(listed).items.length, 9);
  assert.equal(success(listed).items[0].description, '漢'.repeat(1024));
  assert.deepEqual(success(listed).items.at(-1), item);
  const prepared = request(item, { text: 'x'.repeat(65536) });
  success(await rpc('sources.disable', { installationId: item.installationId }));
  failure(await rpc('invoke', prepared), 'SOURCE_DISABLED');
  assert.equal(state.invokes.length, 0);
});

test('main twelve source inputs retain their original near-16-MiB lengths without an active-candidate count quota', async t => {
  const { rpc } = setup(t), pending = [];
  for (let index = 0; index < 12; index++) {
    const installationId = randomUUID(), length = 16 * 1024 * 1024 - 12 + index;
    const bytes = Buffer.alloc(length, 0x61);
    const receipt = success(await rpc.upload(installationId, 1, bytes, { basename: 'fixture.js' }));
    assert.equal(receipt.sourceBytes, length);
    pending.push({ installationId, sourceRevision: 1, contentHash: receipt.contentHash });
  }
  for (const { contentHash, ...params } of pending)
    assert.equal(success(await rpc('sources.add', params)).item.contentHash, contentHash);
  assert.equal(success(await rpc('sources.list')).items.length, 12);
});

test('main fifty-millisecond HTTP deadline cancels one invocation without replay or leaked active work', async t => {
  const entered = deferred();
  const { rpc, state } = setup(t, { invoke: async (_params, { signal }) => {
    entered.resolve();
    await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }));
    throw new ScriptHostError('CANCELLED');
  } });
  const item = await load(rpc);
  const pending = rpc('invoke', request(item, {}), { timeout: 50 });
  await entered.promise; failure(await pending, 'TIMEOUT');
  await until(() => rpc.transport.runtime.state.active === 0);
  assert.equal(state.invokes.length, 1);
  assert.equal(success(await rpc('sources.list')).items[0].state, 'ready');
});

test('close reports host cleanup failure once while another module remains usable', async t => {
  const { module, rpc, state } = setup(t, { close: () => { throw new ScriptHostError('SCRIPT_CLEANUP'); } });
  await load(rpc);
  failure(await module.close(), 'SCRIPT_CLEANUP'); failure(await module.close(), 'SCRIPT_CLEANUP');
  assert.equal(state.closed, 1);
  failure(await rpc('sources.list'), 'CLOSED');
  const other = setup(t);
  assert.deepEqual(success(await other.rpc('sources.list')).items, []);
});
