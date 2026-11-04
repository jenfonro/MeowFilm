import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { SourceTransfers, ScriptHostError } from '@meowfilm/script-host';
import { BridgePeer, encodeFrame, encodeJSONValue, decodeJSONValue, MAX_FRAME_BYTES } from '@meowfilm/runtime-bridge';
import { createMusicScriptModule, musicScriptContract } from '../apps/runtime/modules/music-script.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const chunkBytes = 65536;
const envelope = value => ({ ok: true, contract: musicScriptContract, value });
const turn = () => new Promise(resolve => setImmediate(resolve));
const success = reply => {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, musicScriptContract);
  return reply;
};
const failure = (reply, code) => {
  assert.equal(reply.ok, false, JSON.stringify(reply));
  assert.equal(reply.contract, musicScriptContract);
  if (code) assert.equal(reply.code, code);
};
function setup(t, options = {}) {
  const state = { invokes: [], closed: 0, records: [] };
  const module = createMusicScriptModule({
    call: async () => { throw new Error('unexpected storage'); },
    hostFactory: () => ({
      list: () => state.records,
      stats: () => ({ cleanupIssues: [] }),
      async install(spec) {
        const record = { installationId: spec.installationId, generation: 1, state: 'ready',
          capabilities: options.capabilities ??
            { kw: { name: 'fixture', type: 'music', actions: ['musicUrl'], qualitys: [] } },
          contentHash: hash(spec.source), protocol: 'lx', version: spec.version };
        state.records.push(record);
        return record;
      },
      async invoke(params, context) {
        state.invokes.push(params);
        return options.invoke ? options.invoke(params, context) : params.payload;
      },
      async uninstall(id) { state.records = state.records.filter(item => item.installationId !== id); },
      async close() { state.closed++; state.records = []; return { cleanupComplete: true }; },
    }),
    ...options,
  });
  const rpc = async (name, params = {}, context) => {
    // Every public request and response must remain encodable as a physical frame.
    const request = { version: 1, session: 'transfers', id: 1, kind: 'request',
      method: `music.script.${name}`, params };
    assert.ok(encodeFrame(request).length <= MAX_FRAME_BYTES + 4);
    const reply = await module.methods[`music.script.${name}`](params, context);
    assert.ok(encodeFrame({ version: 1, session: 'transfers', id: 1, kind: 'result', value: reply }).length <= MAX_FRAME_BYTES + 4);
    return reply;
  };
  t.after(async () => { await module.close(); });
  return { module, state, rpc };
}
async function upload(rpc, bytes, expectedHash = hash(bytes)) {
  const receipt = success(await rpc('control.begin', { length: bytes.length, sha256: expectedHash }));
  assert.equal(receipt.chunkBytes, chunkBytes);
  for (let offset = 0; offset < bytes.length; offset += chunkBytes) {
    const part = bytes.subarray(offset, offset + chunkBytes);
    assert.equal(success(await rpc('control.chunk', { token: receipt.token, offset,
      data: part.toString('base64') })).next, offset + part.length);
  }
  return receipt.token;
}
async function restore(rpc, reply) {
  success(reply);
  if (!Object.hasOwn(reply, 'blob')) return reply;
  assert.deepEqual(Object.keys(reply).sort(), ['blob', 'contract', 'ok']);
  const blob = reply.blob, chunks = [];
  assert.equal(blob.chunkBytes, chunkBytes);
  assert.match(blob.sha256, /^[a-f0-9]{64}$/);
  try {
    for (let offset = 0; offset < blob.length;) {
      const part = success(await rpc('blob.read', { token: blob.token, offset }));
      assert.equal(Object.hasOwn(part, 'blob'), false);
      assert.deepEqual(Object.keys(part).sort(), ['contract', 'data', 'next', 'ok']);
      if (blob.length - offset >= chunkBytes)
        assert.ok(encodeJSONValue(part).length > chunkBytes);
      const bytes = Buffer.from(part.data, 'base64');
      assert.equal(bytes.toString('base64'), part.data);
      assert.equal(bytes.length, Math.min(chunkBytes, blob.length - offset));
      assert.equal(part.next, offset + bytes.length);
      chunks.push(bytes); offset = part.next;
    }
    const bytes = Buffer.concat(chunks);
    assert.equal(bytes.length, blob.length); assert.equal(hash(bytes), blob.sha256);
    const result = decodeJSONValue(bytes);
    assert.equal(result.ok, true); assert.equal(result.contract, musicScriptContract);
    assert.equal(Object.hasOwn(result, 'blob'), false);
    return result;
  } finally { success(await rpc('blob.release', { token: blob.token })); }
}
async function load(rpc, control = false) {
  const source = '/* transfer fixture */';
  const stage = success(await rpc('stage.begin', { basename: 'fixture.js', length: Buffer.byteLength(source), sha256: hash(source) }));
  success(await rpc('stage.chunk', { token: stage.token, offset: 0, data: Buffer.from(source).toString('base64') }));
  const input = { token: stage.token, installationId: 'one', revision: 1,
    expectedRevision: 0, version: 'fixture', config: {}, permissions: { storage: [] }, grant: randomUUID() };
  const reply = control ? await rpc('control.call', {
    token: await upload(rpc, encodeJSONValue(input)), operation: 'load',
  }) : await rpc('load', input);
  return (await restore(rpc, reply)).instance;
}
const request = (instance, payload) => ({ installationId: instance.installationId,
  revision: instance.revision, generation: instance.generation, siteId: 'kw', operation: 'musicUrl', payload });
async function invoke(rpc, input) {
  const token = await upload(rpc, encodeJSONValue(input));
  return rpc('control.call', { operation: 'invoke', token });
}

// Public module protocol tests use a deterministic host seam; real QuickJS and
// native authorization regressions remain in windows-music-script.test.mjs.
test('B1 small business envelopes preserve values named blob and ordinary transfer receipts', async t => {
  const { rpc } = setup(t);
  const instance = await load(rpc);
  for (const value of [null, false, 0, ['a', null, false, 1.25], { blob: { token: 'business' }, ok: true }]) {
    // payload itself is the existing object contract; its JSON values are arbitrary.
    const payload = { value };
    assert.deepEqual(await rpc('invoke', request(instance, payload)), envelope(payload));
  }
  const small = { value: 'x'.repeat(chunkBytes - Buffer.byteLength(JSON.stringify(envelope({ value: '' })))) };
  const reply = await rpc('invoke', request(instance, small));
  assert.equal(encodeJSONValue(reply).length, chunkBytes);
  assert.deepEqual(reply, envelope(small));
  const next = success(await rpc('invoke', request(instance, { value: `${small.value}x` })));
  assert.equal(next.blob.length, chunkBytes + 1);
  assert.deepEqual(await restore(rpc, next), envelope({ value: `${small.value}x` }));
});

test('B2 complete 512 KiB, above 512 KiB, above 1 MiB and above 8 MiB requests and results retain JSON', async t => {
  const { rpc, state } = setup(t);
  const instance = await load(rpc);
  for (const length of [512 * 1024, 512 * 1024 + 1, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1]) {
    const input = request(instance, { text: '' });
    input.payload.text = 'x'.repeat(length - encodeJSONValue(input).length);
    assert.equal(encodeJSONValue(input).length, length);
    assert.deepEqual(await restore(rpc, await invoke(rpc, input)), envelope(input.payload));
    assert.deepEqual(state.invokes.at(-1).payload, input.payload);
    const outputSize = length - encodeJSONValue(envelope({ text: '' })).length;
    const output = { text: 'r'.repeat(outputSize) };
    const reply = await invoke(rpc, request(instance, output));
    assert.equal(reply.blob.length, length);
    assert.deepEqual(await restore(rpc, reply), envelope(output));
  }
  assert.equal(success(await rpc('status')).instances.length, 1);
});

test('B2 UTF-8 scalars cross both upload and result boundaries with escaped and structured values', async t => {
  const { rpc, state } = setup(t);
  const instance = await load(rpc);
  const input = request(instance, { text: '' });
  const prefix = encodeJSONValue(input).indexOf(Buffer.from('"text":"')) + Buffer.byteLength('"text":"');
  input.payload.text = 'a'.repeat(chunkBytes - prefix - 1) + '猫😀"\\' + '漢'.repeat(50000);
  input.payload.values = [null, false, true, 0, -1, 1.25, ['😀'], { text: 'quote"slash\\' }];
  const encoded = encodeJSONValue(input);
  assert.equal(encoded[chunkBytes - 1], 0xe7);
  assert.equal(encoded[chunkBytes], 0x8c);
  assert.deepEqual(await restore(rpc, await invoke(rpc, input)), envelope(input.payload));
  assert.deepEqual(state.invokes.at(-1).payload, input.payload);
  const output = { text: 'a'.repeat(chunkBytes - Buffer.byteLength('{"ok":true,"contract":"music.script.v2","value":{"text":"') - 1) + '😀猫' };
  const expected = encodeJSONValue(envelope(output));
  assert.equal(expected[chunkBytes - 1], 0xf0);
  assert.equal(expected[chunkBytes], 0x9f);
  assert.deepEqual(await restore(rpc, await invoke(rpc, request(instance, output))), envelope(output));
});

test('B4 malformed upload chunks discard identifiable tokens without executing the handler', async t => {
  const { rpc, state } = setup(t);
  const bytes = Buffer.from('{}');
  for (const invalid of [
    { offset: 1, data: 'e30=' }, { offset: -1, data: 'e30=' },
    { offset: 0, data: 'e30' }, { offset: 0, data: 'e31=' }, { offset: 0, data: 'e30=\n' },
    { offset: 0, data: 'e30===' }, { offset: 0, data: '' },
    { offset: 0, data: Buffer.from('xxx').toString('base64') },
    { offset: 0, data: Buffer.alloc(chunkBytes + 1).toString('base64') },
  ]) {
    const begin = success(await rpc('control.begin', { length: bytes.length, sha256: hash(bytes) }));
    failure(await rpc('control.chunk', { token: begin.token, ...invalid }));
    failure(await rpc('control.chunk', { token: begin.token, offset: 0, data: bytes.toString('base64') }), 'STAGE_NOT_FOUND');
  }
  const begin = success(await rpc('control.begin', { length: 2, sha256: hash(bytes) }));
  success(await rpc('control.chunk', { token: begin.token, offset: 0, data: 'ew==' }));
  failure(await rpc('control.chunk', { token: begin.token, offset: 0, data: 'fQ==' }));
  failure(await rpc('control.call', { token: begin.token, operation: 'status' }), 'STAGE_NOT_FOUND');
  assert.equal(state.invokes.length, 0);
});

test('B4 incomplete, hash, encoding and logical JSON failures consume tokens before business', async t => {
  const { module, rpc, state } = setup(t);
  const instance = await load(rpc);
  const valid = encodeJSONValue(request(instance, {}));
  const begin = success(await rpc('control.begin', { length: valid.length + 1, sha256: hash(valid) }));
  success(await rpc('control.chunk', { token: begin.token, offset: 0, data: valid.toString('base64') }));
  failure(await rpc('control.call', { token: begin.token, operation: 'invoke' }), 'SCRIPT_INTEGRITY');
  failure(await rpc('control.call', { token: begin.token, operation: 'invoke' }), 'STAGE_NOT_FOUND');
  const wrongHash = await upload(rpc, valid, '0'.repeat(64));
  failure(await rpc('control.call', { token: wrongHash, operation: 'invoke' }), 'SCRIPT_INTEGRITY');
  failure(await rpc('control.call', { token: wrongHash, operation: 'invoke' }), 'STAGE_NOT_FOUND');
  for (const bytes of [Buffer.from([0xc3, 0x28]), Buffer.from('{'),
    Buffer.from('{"installationId":"one","installationId":"two"}'),
    Buffer.from('{"x":0,"\\u0078":1}'), Buffer.from('{"非ASCII":1}'),
    Buffer.from('{"x":"\\ud800"}'), Buffer.from('{"x":1e400}'),
    Buffer.from('{"x":9007199254740992}'), Buffer.from('['.repeat(66) + '0' + ']'.repeat(66)),
    Buffer.from('null'), Buffer.from('false'), Buffer.from('[]')]) {
    const token = await upload(rpc, bytes);
    failure(await rpc('control.call', { token, operation: 'invoke' }), 'INVALID_REQUEST');
    failure(await rpc('control.call', { token, operation: 'invoke' }), 'STAGE_NOT_FOUND');
  }
  assert.equal(state.invokes.length, 0);
  for (const invalid of [0, -1, 1.5])
    failure(await rpc('control.begin', { length: invalid, sha256: hash(valid) }), 'INVALID_REQUEST');
  failure(await module.methods['music.script.control.begin']({
    length: Number.MAX_SAFE_INTEGER + 1, sha256: hash(valid),
  }), 'INVALID_REQUEST');
  failure(await rpc('control.begin', { length: 1, sha256: 'A'.repeat(64) }), 'INVALID_REQUEST');
});

test('B3 control.call permits only original business methods and consumes failures without retries', async t => {
  const { rpc, state } = setup(t, { invoke: async () => { throw new ScriptHostError('TIMEOUT'); } });
  const instance = await load(rpc, true);
  for (const operation of ['control.call', 'stage.begin', 'blob.read', 'close', 'music.script.invoke']) {
    const token = await upload(rpc, Buffer.from('{}'));
    failure(await rpc('control.call', { token, operation }), 'INVALID_REQUEST');
    failure(await rpc('control.chunk', { token, offset: 0, data: 'e30=' }), 'STAGE_NOT_FOUND');
  }
  const token = await upload(rpc, encodeJSONValue(request(instance, {})));
  failure(await rpc('control.call', { token, operation: 'invoke' }), 'TIMEOUT');
  failure(await rpc('control.call', { token, operation: 'invoke' }), 'STAGE_NOT_FOUND');
  assert.equal(state.invokes.length, 1);
  const status = await upload(rpc, Buffer.from('{}'));
  assert.equal(success(await rpc('control.call', { token: status, operation: 'status' })).instances.length, 1);
  const unload = await upload(rpc, encodeJSONValue({ installationId: 'one', revision: 1 }));
  assert.equal(success(await rpc('control.call', { token: unload, operation: 'unload' })).unloaded, true);
  assert.deepEqual(success(await rpc('status')).instances, []);
});

test('B3 load and status transfer complete large declaration envelopes and keep generation fences', async t => {
  const capabilities = Object.fromEntries(Array.from({ length: 32 }, (_, index) => [`s${index}`, {
    name: '猫'.repeat(256), type: 'music',
    actions: Array.from({ length: 32 }, (_, item) => `a${item}`.padEnd(128, 'a')),
    qualitys: Array.from({ length: 32 }, (_, item) => `q${item}`.padEnd(64, 'q')),
  }]));
  const { rpc, state } = setup(t, { capabilities });
  const instance = await load(rpc, true);
  assert.deepEqual(instance.capabilities, capabilities);
  const status = success(await rpc('status'));
  assert.ok(status.blob.length > chunkBytes);
  assert.deepEqual((await restore(rpc, status)).instances, [instance]);
  const token = await upload(rpc, encodeJSONValue(request(instance, { text: 'x'.repeat(chunkBytes) })));
  success(await rpc('unload', { installationId: 'one', revision: 1 }));
  failure(await rpc('control.call', { token, operation: 'invoke' }), 'SOURCE_NOT_FOUND');
  assert.equal(state.invokes.length, 0);
  failure(await rpc('control.call', { token, operation: 'invoke' }), 'STAGE_NOT_FOUND');
});

test('B4 logical results cannot bypass bridge JSON validation through blobs', async t => {
  let output;
  const timers = new Set();
  const { rpc } = setup(t, { invoke: async () => output,
    setTimer(callback) { const timer = { callback, unref() {} }; timers.add(timer); return timer; },
    clearTimer(timer) { timers.delete(timer); },
  });
  const instance = await load(rpc);
  for (const invalid of [NaN, Infinity, Number.MAX_SAFE_INTEGER + 1, '\ud800', { '猫': 1 }]) {
    output = { padding: 'x'.repeat(chunkBytes), invalid };
    failure(await invoke(rpc, request(instance, {})), 'INVALID_REQUEST');
    assert.equal(timers.size, 0);
  }
  output = false;
  assert.deepEqual(await invoke(rpc, request(instance, {})), envelope(false));
});

test('B5 pre-cancelled chunk/call/read release tokens and aborted business consumes upload', async t => {
  let entered, unblock;
  const started = new Promise(resolve => { entered = resolve; });
  const pending = new Promise(resolve => { unblock = resolve; });
  let delayed = false;
  const { rpc, state } = setup(t, { invoke: async params => {
    if (delayed) { entered(); await pending; }
    return params.payload;
  } });
  const instance = await load(rpc);
  const controller = new AbortController(); controller.abort();
  for (const method of ['control.chunk', 'control.call']) {
    const token = await upload(rpc, Buffer.from('{}'));
    const input = method === 'control.chunk' ? { token, offset: 0, data: 'e30=' } : { token, operation: 'status' };
    failure(await rpc(method, input, { signal: controller.signal }), 'CANCELLED');
    failure(await rpc('control.call', { token, operation: 'status' }), 'STAGE_NOT_FOUND');
  }
  const blob = success(await invoke(rpc, request(instance, { text: 'x'.repeat(chunkBytes) }))).blob;
  failure(await rpc('blob.read', { token: blob.token, offset: 0 }, { signal: controller.signal }), 'CANCELLED');
  failure(await rpc('blob.read', { token: blob.token, offset: 0 }), 'STAGE_NOT_FOUND');
  delayed = true;
  const token = await upload(rpc, encodeJSONValue(request(instance, { text: 'delayed' })));
  const active = new AbortController();
  const result = rpc('control.call', { token, operation: 'invoke' }, { signal: active.signal });
  await started; active.abort(); unblock();
  failure(await result, 'CANCELLED');
  failure(await rpc('control.call', { token, operation: 'invoke' }), 'STAGE_NOT_FOUND');
  delayed = false;
  assert.deepEqual(await restore(rpc, await invoke(rpc, request(instance, { next: true }))), envelope({ next: true }));
  assert.equal(state.invokes.length, 3);
});

test('B5 cancelled begin receipt and cancelled blob read drop owned buffers before returning', async t => {
  const timers = [];
  const { module, rpc } = setup(t, { setTimer(callback) {
    const timer = { callback, unref() {} }; timers.push(timer); return timer;
  }, clearTimer(timer) { timers.splice(timers.indexOf(timer), 1); } });
  const controller = new AbortController();
  const begin = module.methods['music.script.control.begin']({ length: 2, sha256: hash('{}') }, { signal: controller.signal });
  controller.abort();
  failure(await begin, 'CANCELLED');
  assert.equal(timers.length, 0);
  const instance = await load(rpc);
  const blob = success(await invoke(rpc, request(instance, { data: 'x'.repeat(chunkBytes) }))).blob;
  const reading = new AbortController();
  const result = module.methods['music.script.blob.read']({ token: blob.token, offset: 0 }, { signal: reading.signal });
  reading.abort(); failure(await result, 'CANCELLED');
  failure(await rpc('blob.read', { token: blob.token, offset: 0 }), 'STAGE_NOT_FOUND');
  assert.equal(timers.length, 0);
});

test('B5 module close cancels an active transferred call and retains no published result', async t => {
  let entered;
  const started = new Promise(resolve => { entered = resolve; });
  const { module, rpc, state } = setup(t, { invoke: async (_params, { signal }) => {
    entered();
    await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }));
    throw new ScriptHostError('CANCELLED');
  } });
  const instance = await load(rpc);
  const token = await upload(rpc, encodeJSONValue(request(instance, {})));
  const invoking = rpc('control.call', { token, operation: 'invoke' });
  await started;
  success(await module.close());
  failure(await invoking, 'CLOSED');
  assert.equal(state.closed, 1);
  failure(await rpc('control.call', { token, operation: 'invoke' }), 'CLOSED');
});

test('B5 active five-minute TTL renews on chunks and reads, then expires without subsequent traffic', async t => {
  let now = 0;
  const timers = new Set();
  const { rpc } = setup(t, { clock: () => now,
    setTimer(callback, delay) { const timer = { callback, delay, unref() {} }; timers.add(timer); return timer; },
    clearTimer(timer) { timers.delete(timer); },
  });
  const instance = await load(rpc);
  const token = success(await rpc('control.begin', { length: 2, sha256: hash('{}') })).token;
  now = 100;
  success(await rpc('control.chunk', { token, offset: 0, data: 'ew==' }));
  const blob = success(await invoke(rpc, request(instance, { text: 'x'.repeat(chunkBytes) }))).blob;
  now = 300000;
  for (const timer of [...timers]) timer.callback();
  success(await rpc('control.chunk', { token, offset: 1, data: 'fQ==' }));
  success(await rpc('blob.read', { token: blob.token, offset: 0 }));
  assert.equal([...timers][0].delay, 300000);
  now = 600000;
  for (const timer of [...timers]) timer.callback();
  assert.equal(timers.size, 0);
  failure(await rpc('control.call', { token, operation: 'status' }), 'STAGE_NOT_FOUND');
  failure(await rpc('blob.read', { token: blob.token, offset: 0 }), 'STAGE_NOT_FOUND');
  assert.equal(success(await rpc('status')).instances.length, 1);
});

test('B4 invalid blob offsets discard the result, release is idempotent and types cannot cross', async t => {
  const { rpc } = setup(t);
  const instance = await load(rpc);
  for (const offset of [-1, 0.5, 999999]) {
    const blob = success(await invoke(rpc, request(instance, { text: 'x'.repeat(chunkBytes) }))).blob;
    failure(await rpc('blob.read', { token: blob.token, offset }));
    failure(await rpc('blob.read', { token: blob.token, offset: 0 }), 'STAGE_NOT_FOUND');
    success(await rpc('blob.release', { token: blob.token }));
    success(await rpc('blob.release', { token: blob.token }));
  }
  const token = success(await rpc('control.begin', { length: 2, sha256: hash('{}') })).token;
  success(await rpc('control.chunk', { token, offset: 0, data: 'ew==' }));
  failure(await rpc('blob.read', { token, offset: 0 }), 'SCRIPT_INTEGRITY');
  failure(await rpc('control.call', { token, operation: 'status' }), 'STAGE_NOT_FOUND');
  const blob = success(await invoke(rpc, request(instance, { text: 'x'.repeat(chunkBytes) }))).blob;
  failure(await rpc('control.call', { token: blob.token, operation: 'status' }), 'SCRIPT_INTEGRITY');
  failure(await rpc('blob.read', { token: blob.token, offset: 0 }), 'STAGE_NOT_FOUND');
});

test('B5 close clears uploads and results and still closes host after transfer cleanup throws', async t => {
  let failClear = false;
  const timers = new Set();
  const { module, rpc, state } = setup(t, {
    setTimer(callback) { const timer = { callback, unref() {} }; timers.add(timer); return timer; },
    clearTimer(timer) { if (failClear) throw new Error('timer failure'); timers.delete(timer); },
  });
  const instance = await load(rpc);
  const token = success(await rpc('control.begin', { length: 2, sha256: hash('{}') })).token;
  success(await rpc('control.chunk', { token, offset: 0, data: 'ew==' }));
  const blob = success(await invoke(rpc, request(instance, { text: 'x'.repeat(chunkBytes) }))).blob;
  failClear = true;
  failure(await module.close(), 'SCRIPT_CLEANUP');
  assert.equal(state.closed, 1);
  for (const timer of timers) timer.callback();
  failure(await rpc('control.call', { token, operation: 'status' }), 'CLOSED');
  failure(await rpc('blob.read', { token: blob.token, offset: 0 }), 'CLOSED');
  failure(await module.close(), 'SCRIPT_CLEANUP');
  assert.equal(state.closed, 1);
  const other = setup(t);
  assert.deepEqual(success(await other.rpc('status')).instances, []);
});

test('B5 physical bridge TIMEOUT cancels a staged invocation and leaves no reusable upload', async t => {
  let entered;
  const started = new Promise(resolve => { entered = resolve; });
  const { module, rpc, state } = setup(t, { invoke: async (_params, { signal }) => {
    entered();
    await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }));
    throw new ScriptHostError('CANCELLED');
  } });
  const instance = await load(rpc);
  const token = await upload(rpc, encodeJSONValue(request(instance, {})));
  let left, right;
  left = new BridgePeer({ session: 'timeout', timeout: 1000, send: bytes => right.receive(bytes) });
  right = new BridgePeer({ session: 'timeout', timeout: 1000, send: bytes => left.receive(bytes), methods: module.methods });
  t.after(() => { left.close(); right.close(); });
  const result = left.call('music.script.control.call', { token, operation: 'invoke' }, { timeout: 50 });
  const rejected = assert.rejects(result, { code: 'TIMEOUT' });
  await started; await rejected; await turn(); await turn();
  failure(await rpc('control.call', { token, operation: 'invoke' }), 'STAGE_NOT_FOUND');
  assert.equal(state.invokes.length, 1);
  assert.equal(success(await rpc('status')).instances.length, 1);
  assert.equal(left.state.pending, 0); assert.equal(right.state.active, 0);
});

test('shared SourceTransfers default inline and blob output retain Cat serialization', () => {
  const transfers = new SourceTransfers();
  try {
    assert.deepEqual(transfers.result({ value: '猫', key: false }), { data: '{"value":"猫","key":false}' });
    assert.deepEqual(transfers.result(Buffer.from('cat')), { data: JSON.stringify(Buffer.from('cat')) });
    const value = { text: 'c'.repeat(chunkBytes) };
    const result = transfers.result(value), chunks = [];
    for (let offset = 0; offset < result.blob.length;) {
      const part = transfers.read({ token: result.blob.token, offset });
      chunks.push(Buffer.from(part.data, 'base64')); offset = part.next;
    }
    const bytes = Buffer.concat(chunks);
    assert.equal(hash(bytes), result.blob.sha256);
    assert.deepEqual(JSON.parse(bytes.toString()), value);
    transfers.release(result.blob.token);
  } finally { transfers.close(); }
});
