import test from 'node:test';
import assert from 'node:assert/strict';
import { Duplex } from 'node:stream';
import { attachConnectedStream, createDescriptorPeer } from '../src/descriptor.js';

test('connected byte streams carry explicit requests and release on close', async t => {
  let first, second;
  first = new Duplex({
    read() {},
    write(bytes, _, done) { second.push(bytes); done(); },
    destroy(error, done) { second.push(null); done(error); }
  });
  second = new Duplex({
    read() {},
    write(bytes, _, done) { first.push(bytes); done(); },
    destroy(error, done) { first.push(null); done(error); }
  });
  const a = attachConnectedStream(first, { session: 'stream' });
  const b = attachConnectedStream(second, { session: 'stream', methods: { echo: value => value } });
  t.after(() => { a.close(); b.close(); });
  assert.deepEqual(await a.call('echo', { id: '安装' }), { id: '安装' });
  a.close();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(first.destroyed, true);
  assert.equal(second.destroyed, true);
  assert.equal(b.state.closed, true);
});
test('invalid descriptors do not create a socket', () => {
  for (const descriptor of [-1, NaN, '3', 0.5]) {
    assert.throws(() => createDescriptorPeer(descriptor, { session: 'stream' }), { code: 'INVALID_MESSAGE' });
  }
});
test('lifecycle callback runs once and follows terminal peer state', async () => {
  const stream = new Duplex({ read() {}, write(_bytes, _encoding, done) { done(); } });
  let calls = 0, peer;
  peer = attachConnectedStream(stream, { session: 'lifecycle', onClose() {
    assert.equal(peer.state.closed, true);
    calls++;
  } });
  peer.close();
  peer.close();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(calls, 1);
  assert.equal(stream.destroyed, true);
});
test('invalid lifecycle callback fails before taking a connected stream', () => {
  const stream = new Duplex({ read() {}, write(_bytes, _encoding, done) { done(); } });
  try {
    assert.throws(() => attachConnectedStream(stream, { session: 'lifecycle', onClose: true }),
      { code: 'INVALID_MESSAGE' });
    assert.equal(stream.destroyed, false);
  } finally { stream.destroy(); }
});
