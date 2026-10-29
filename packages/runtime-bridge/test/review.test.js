// Sidecar regression tests. Only production public bridge APIs are exercised.
// Held writes are deliberate backpressure in real Node Duplex streams, not
// an embedded Node engine, Swift runtime, native socketpair or device test.
import test from 'node:test';
import assert from 'node:assert/strict';
import { Duplex } from 'node:stream';
import { BridgePeer, FrameDecoder, encodeFrame } from '../src/index.js';
import { attachConnectedStream } from '../src/descriptor.js';

const turn = () => new Promise(resolve => setImmediate(resolve));
const session = 'review';
const request = (id, method, params = null) => ({
  version: 1, session, id, kind: 'request', method, params
});

function heldStreams(t, holdWrite = 1) {
  const held = Promise.withResolvers();
  let left, right, writeCount = 0, release;
  left = new Duplex({
    read() {},
    write(bytes, _encoding, done) {
      const forward = () => {
        if (!right.destroyed) right.push(bytes);
        done();
      };
      if (++writeCount === holdWrite) {
        release = forward;
        held.resolve();
      } else forward();
    },
    destroy(error, done) {
      if (!right.destroyed) right.push(null);
      done(error);
    }
  });
  right = new Duplex({
    read() {},
    write(bytes, _encoding, done) {
      if (!left.destroyed) left.push(bytes);
      done();
    },
    destroy(error, done) {
      if (!left.destroyed) left.push(null);
      done(error);
    }
  });
  const resume = () => {
    const forward = release;
    release = undefined;
    forward?.();
  };
  t.after(async () => {
    left.destroy();
    right.destroy();
    resume(); // The fixture also settles a write that was interrupted by close.
    await turn();
  });
  return { left, right, held: held.promise, resume };
}

// Convert early rejections into values immediately; no unhandled rejections or
// unfinished caller promises when a corrected peer elects to close under load.
const outcome = promise => promise.then(value => ({ value }), error => ({ error }));

test('review: an unsent cancelled request must not start its business handler after backlog drains',
  { timeout: 2000 }, async t => {
    const wire = heldStreams(t);
    let effects = 0;
    const a = attachConnectedStream(wire.left, { session, timeout: 1000 });
    const b = attachConnectedStream(wire.right, {
      session, timeout: 1000,
      methods: { echo: value => value, mutate: () => { effects++; return null; } }
    });
    t.after(() => { a.close(); b.close(); });

    const first = outcome(a.call('echo'));
    await wire.held; // No bytes, including the later mutate, have reached b.
    const controller = new AbortController();
    const cancelled = outcome(a.call('mutate', null, { signal: controller.signal }));
    controller.abort();
    assert.equal((await cancelled).error?.code, 'CANCELLED');
    assert.equal(effects, 0);

    wire.resume();
    assert.deepEqual(await first, { value: null });
    await turn();
    assert.equal(effects, 0, 'cancelled while definitely unsent, but mutate executed after CANCELLED');
  });

test('review: cancellation of running work cannot disappear when the outbound byte budget is full',
  { timeout: 2000 }, async t => {
    const wire = heldStreams(t, 2);
    const started = Promise.withResolvers();
    const finish = Promise.withResolvers();
    const maximumBytes = 256;
    const a = attachConnectedStream(wire.left, { session, maximumBytes, timeout: 1000 });
    const b = attachConnectedStream(wire.right, {
      session, maximumBytes, timeout: 1000,
      methods: {
        wait: (_, { signal }) => {
          const aborted = () => finish.resolve(null);
          signal.addEventListener('abort', aborted, { once: true });
          started.resolve(signal);
          return finish.promise.finally(() => signal.removeEventListener('abort', aborted));
        },
        fill: () => null
      }
    });
    t.after(async () => {
      finish.resolve(null);
      a.close(); b.close();
      await turn();
    });
    const controller = new AbortController();
    const waiting = outcome(a.call('wait', null, { signal: controller.signal }));
    const remoteSignal = await started.promise;
    await turn(); // Let the first transport callback release its queued-byte charge.
    assert.equal(a.state.queuedBytes, 0);

    // Four exact-size legal frames fill today's transmit budget. A corrected
    // peer may reserve control capacity and reject some fill calls instead.
    const overhead = encodeFrame(request(2, 'fill', ''), maximumBytes).length;
    const payload = 'x'.repeat(maximumBytes - overhead);
    const filling = Array.from({ length: 4 }, () => outcome(a.call('fill', payload)));
    await wire.held;
    assert.ok(a.state.queuedBytes > 0 && a.state.queuedBytes <= maximumBytes * 4);
    controller.abort();
    assert.equal((await waiting).error?.code, 'CANCELLED');
    wire.resume();
    await Promise.all(filling);
    await turn();

    assert.ok(remoteSignal.aborted || a.state.closed,
      'cancel frame was dropped on LIMIT; cooperative remote work is still active on an open peer');
  });

test('review: unknown-method responses must not be silently dropped on recoverable send backpressure',
  { timeout: 2000 }, async t => {
    const release = Promise.withResolvers();
    const replies = [];
    const decoder = new FrameDecoder();
    const peer = new BridgePeer({
      session, maximumBytes: 128,
      send: async bytes => {
        await release.promise;
        decoder.push(bytes, message => replies.push(message));
      }
    });
    t.after(() => { release.resolve(); peer.close(); });
    for (let id = 1; id <= 12; id++) peer.receive(encodeFrame(request(id, 'unknown')));
    await turn();
    assert.ok(peer.state.queuedBytes <= 128 * 4);
    release.resolve();
    await turn();
    await turn();

    assert.ok(peer.state.closed || replies.length === 12,
      `peer stayed open but only ${replies.length}/12 unknown-method requests received an outcome`);
    if (!peer.state.closed) {
      assert.deepEqual(replies.map(reply => reply.id).sort((a, b) => a - b),
        Array.from({ length: 12 }, (_, i) => i + 1));
      assert.ok(replies.every(reply => reply.kind === 'error'
        && ['METHOD_UNAVAILABLE', 'LIMIT'].includes(reply.code)));
    }
  });

test('review: encoder rejects unpaired UTF-16 values instead of emitting a frame Swift cannot decode', () => {
  const good = request(1, 'echo', { text: '中文😀', array: [null, 1.5] });
  let roundTrip;
  new FrameDecoder().push(encodeFrame(good), message => { roundTrip = message; });
  assert.deepEqual(roundTrip, good);

  // Swift BridgeValue decodes String via Foundation; the Swift 6.2 official
  // JSONScanner rejects lone surrogates. This assertion executes only JS.
  for (const text of ['\ud800', '\udfff', 'split😀'.slice(0, -1)]) {
    assert.throws(() => encodeFrame(request(2, 'echo', { text })),
      { code: 'INVALID_MESSAGE' }, 'ill-formed UTF-16 must fail locally, not close the opposite Swift peer');
  }
});

test('review: encoder validates object-key Unicode as well as value Unicode', () => {
  assert.throws(() => encodeFrame(request(1, 'echo', { ['\ud800']: 'value' })),
    { code: 'INVALID_MESSAGE' }, 'JSON object keys must also be representable as Swift String');
});
