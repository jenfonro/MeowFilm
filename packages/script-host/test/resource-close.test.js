import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { afterCloseDispatch, closeResource } from '../src/resource-close.js';

test('close dispatch retirement follows all listeners and removes the native adapter', () => {
  const emitter = new EventEmitter(), events = [];
  const original = emitter.emit;
  let detach;
  detach = afterCloseDispatch(emitter, () => { events.push('retired'); detach(); });
  emitter.once('close', () => events.push('first'));
  emitter.once('close', () => events.push('last'));
  assert.equal(emitter.emit('close'), true);
  assert.deepEqual(events, ['first', 'last', 'retired']);
  assert.equal(emitter.emit, original);
  assert.equal(Object.hasOwn(emitter, 'emit'), false);
  detach();
});

test('close dispatch keeps independent owners and preserves an own emitter implementation', () => {
  const emitter = new EventEmitter(), events = [];
  emitter.emit = function(...args) { return EventEmitter.prototype.emit.apply(this, args); };
  const descriptor = Object.getOwnPropertyDescriptor(emitter, 'emit');
  const first = afterCloseDispatch(emitter, () => events.push('first'));
  const last = afterCloseDispatch(emitter, () => events.push('last'));
  first();
  emitter.emit('finish');
  assert.deepEqual(events, []);
  emitter.emit('close');
  assert.deepEqual(events, ['last']);
  last();
  assert.deepEqual(Object.getOwnPropertyDescriptor(emitter, 'emit'), descriptor);
});

test('close dispatch completes retirement even if a native listener fails', () => {
  const emitter = new EventEmitter(), failure = new Error('synthetic listener failure');
  let retired = false;
  const detach = afterCloseDispatch(emitter, () => { retired = true; });
  emitter.once('close', () => { throw failure; });
  assert.throws(() => emitter.emit('close'), error => error === failure);
  assert.equal(retired, true);
  detach();
});

test('closeResource waits for the real close event and releases once', async () => {
  const emitter = new EventEmitter();
  let stopped = 0, released = 0;
  const closing = closeResource(emitter, {
    stop() { stopped++; },
    release() { released++; },
    timeoutMs: 100,
  });
  assert.equal(stopped, 1);
  assert.equal(released, 0);
  emitter.emit('close');
  assert.deepEqual(await closing, { complete: true, error: null, timedOut: false, guarded: false });
  assert.equal(stopped, 1);
  assert.equal(released, 1);
  assert.equal(emitter.listenerCount('close'), 0);
  assert.equal(emitter.listenerCount('error'), 0);
});

test('closeResource does not invent a second stop attempt or hide an error', async () => {
  const emitter = new EventEmitter();
  const failure = new Error('synthetic stop failure');
  let stopped = 0, released = 0;
  const result = await closeResource(emitter, {
    stop() { stopped++; throw failure; },
    release() { released++; },
    timeoutMs: 100,
  });
  assert.equal(stopped, 1);
  assert.equal(released, 1);
  assert.equal(result.complete, false);
  assert.equal(result.timedOut, true);
  assert.equal(result.error, failure);
  assert.equal(emitter.listenerCount('close'), 0);
});

test('closeResource detaches a timed-out emitter and retains only an owner-free error guard', async () => {
  const emitter = new EventEmitter();
  let released = 0;
  const result = await closeResource(emitter, {
    stop() {},
    release() { released++; },
    timeoutMs: 5,
  });
  assert.equal(result.complete, false);
  assert.equal(result.timedOut, true);
  assert.equal(result.guarded, true);
  assert.equal(released, 1);
  assert.equal(emitter.listenerCount('close'), 0);
  assert.equal(emitter.listenerCount('error'), 1);
  assert.doesNotThrow(() => emitter.emit('error', new Error('late failure')));
  assert.equal(emitter.listenerCount('close'), 0);
  assert.equal(emitter.listenerCount('error'), 1);
  assert.equal(released, 1);
});

test('closeResource preserves a stop error even when close is emitted synchronously', async () => {
  const emitter = new EventEmitter();
  const failure = new Error('synthetic stop failure');
  let released = 0;
  const result = await closeResource(emitter, {
    stop() { emitter.emit('close'); throw failure; },
    release() { released++; },
  });
  assert.equal(result.complete, true);
  assert.equal(result.timedOut, false);
  assert.equal(result.error, failure);
  assert.equal(released, 1);
  assert.equal(emitter.listenerCount('close'), 0);
  assert.equal(emitter.listenerCount('error'), 0);
});

test('closeResource installs the detached guard after releasing owner listeners', async () => {
  const emitter = new EventEmitter();
  const result = await closeResource(emitter, {
    stop() { emitter.emit('error', new Error('synthetic native error')); },
    release() { emitter.removeAllListeners(); },
    timeoutMs: 5,
  });
  assert.equal(result.complete, false);
  assert.equal(result.error.code, 'TIMEOUT');
  assert.equal(emitter.listenerCount('close'), 0);
  assert.equal(emitter.listenerCount('error'), 1);
  assert.doesNotThrow(() => emitter.emit('error', new Error('synthetic late error')));
});

test('closeResource releases an already-closed resource without repeating termination', async () => {
  const emitter = new EventEmitter();
  emitter.closed = true;
  let stopped = 0, released = 0;
  assert.deepEqual(await closeResource(emitter, {
    stop() { stopped++; },
    release() { released++; },
  }), { complete: true, error: null, timedOut: false, guarded: false });
  assert.equal(stopped, 0);
  assert.equal(released, 1);
});
