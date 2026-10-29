import test from 'node:test';
import assert from 'node:assert/strict';
import { BridgePeer, FrameDecoder, encodeFrame } from '../src/index.js';

function pair(t, methodsA = {}, methodsB = {}, options = {}) {
  let a, b;
  a = new BridgePeer({ session: 'test-session', methods: methodsA, send: bytes => b.receive(bytes), ...options });
  b = new BridgePeer({ session: 'test-session', methods: methodsB, send: bytes => a.receive(bytes), ...options });
  t.after(() => { a.close(); b.close(); });
  return [a, b];
}
const message = { version: 1, session: 'session', kind: 'request', id: 1, method: 'test.echo', params: { text: '中文' } };

test('framing handles every-byte fragmentation and coalesced frames', () => {
  const bytes = encodeFrame(message);
  const received = [];
  const decoder = new FrameDecoder();
  for (const byte of bytes) decoder.push(Uint8Array.of(byte), value => received.push(value));
  decoder.push(Buffer.concat([bytes, bytes]), value => received.push(value));
  decoder.finish();
  assert.deepEqual(received, [message, message, message]);
});
test('size, UTF-8, schema, unsafe integers and incomplete streams reject', () => {
  assert.throws(() => encodeFrame({ ...message, session: 'session\n' }), { code: 'INVALID_MESSAGE' });
  assert.throws(() => encodeFrame({ ...message, params: 2 ** 60 }), { code: 'INVALID_MESSAGE' });
  assert.throws(() => encodeFrame({ ...message, params: undefined }), { code: 'INVALID_MESSAGE' });
  assert.throws(() => encodeFrame({ ...message, extra: 'secret' }), { code: 'INVALID_MESSAGE' });
  assert.throws(() => encodeFrame({ ...message, params: '\u4e2d'.repeat(1024 * 1024) }), { code: 'LIMIT' });
  const decoder = new FrameDecoder();
  assert.throws(() => decoder.push(Buffer.from([0xff, 0xff, 0xff, 0xff]), () => {}), { code: 'LIMIT' });
  assert.throws(() => decoder.push(Buffer.from([0]), () => {}), { code: 'CLOSED' });
  const incomplete = new FrameDecoder();
  incomplete.push(Buffer.from([0]), () => {});
  assert.throws(() => incomplete.finish(), { code: 'INVALID_MESSAGE' });
  const badUTF8 = new FrameDecoder();
  assert.throws(() => badUTF8.push(Buffer.from([0, 0, 0, 1, 0xff]), () => {}), { code: 'INVALID_MESSAGE' });
});
test('duplex explicitly registered methods and null results round trip', async t => {
  const [a, b] = pair(t, { 'native.echo': value => value }, {
    'business.echo': value => value, 'business.null': () => null
  });
  assert.deepEqual(await a.call('business.echo', { text: '中文', list: [false, null, 3.5] }), { text: '中文', list: [false, null, 3.5] });
  assert.equal(await a.call('business.null'), null);
  assert.deepEqual(await b.call('native.echo', { id: 'exact-installation' }), { id: 'exact-installation' });
  await assert.rejects(a.call('unknown'), { code: 'METHOD_UNAVAILABLE' });
});
test('nested native callback does not deadlock the shared peer', async t => {
  let b;
  const connected = pair(t, { 'native.read': ({ id }) => `stored:${id}` }, {
    'business.read': params => b.call('native.read', params)
  });
  const a = connected[0]; b = connected[1];
  assert.equal(await a.call('business.read', { id: 'source' }), 'stored:source');
});
test('arguments are captured, errors redacted, no arbitrary eval', async t => {
  const [a] = pair(t, {}, {
    'business.read': value => value,
    'business.fail': () => { throw new Error('private provider credential must never cross bridge'); }
  });
  const params = { id: 'first' };
  const result = a.call('business.read', params);
  params.id = 'second';
  assert.deepEqual(await result, { id: 'first' });
  await assert.rejects(a.call('business.fail'), error => error.code === 'INTERNAL' && error.message === 'INTERNAL');
  await assert.rejects(a.call('eval', 'process.exit()'), { code: 'METHOD_UNAVAILABLE' });
});
test('abort settles client while noncooperative server work stays budgeted', async t => {
  let release, started;
  const ready = new Promise(resolve => { started = resolve; });
  const [a, b] = pair(t, {}, { 'business.wait': (_, { signal }) => {
    started(signal);
    return new Promise(resolve => { release = resolve; });
  } }, { maximumPending: 1 });
  const controller = new AbortController();
  const result = a.call('business.wait', null, { signal: controller.signal });
  const signal = await ready;
  controller.abort();
  await assert.rejects(result, { code: 'CANCELLED' });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(signal.aborted, true);
  assert.equal(b.state.active, 1);
  await assert.rejects(a.call('business.wait'), { code: 'LIMIT' });
  release('late');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(b.state.active, 0);
  assert.equal(a.state.pending, 0);
});
test('timeout and closed transport reject without late revival', async t => {
  let release;
  const [a] = pair(t, {}, { 'business.wait': () => new Promise(resolve => { release = resolve; }) });
  await assert.rejects(a.call('business.wait', null, { timeout: 5 }), { code: 'TIMEOUT' });
  release('late');
  a.close();
  await assert.rejects(a.call('business.wait'), { code: 'CLOSED' });
});
test('wrong generations and replayed IDs close before dispatch', async t => {
  let calls = 0;
  const peer = new BridgePeer({ session: 'current', send: () => {}, methods: { 'test.echo': () => { calls++; return null; } } });
  t.after(() => peer.close());
  assert.throws(() => peer.receive(encodeFrame(message)), { code: 'INVALID_MESSAGE' });
  assert.equal(calls, 0);
  assert.equal(peer.state.closed, true);
  const other = new BridgePeer({ session: 'session', send: () => {}, methods: { 'test.echo': () => { calls++; return null; } } });
  t.after(() => other.close());
  other.receive(encodeFrame(message));
  assert.throws(() => other.receive(encodeFrame(message)), { code: 'INVALID_MESSAGE' });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(calls, 0); // closed before the scheduled handler starts
});
test('pre-aborted calls never dispatch and invalid result is an explicit error', async t => {
  let calls = 0;
  const [a] = pair(t, {}, { 'test.echo': () => { calls++; return undefined; } });
  await assert.rejects(a.call('test.echo', null, { signal: AbortSignal.abort() }), { code: 'CANCELLED' });
  assert.equal(calls, 0);
  await assert.rejects(a.call('test.echo'), { code: 'INVALID_MESSAGE' });
});
