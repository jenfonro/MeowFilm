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

test('closeResource host completion survives listener removal and follows the full dispatch', async () => {
  const emitter = new EventEmitter(), events = [];
  const original = emitter.emit;
  let removed = 0, released = false;
  emitter.on('close', () => { removed++; });
  const closing = closeResource(emitter, {
    stop() { events.push('stop'); },
    release() { released = true; events.push('release'); emitter.removeAllListeners(); },
  });
  assert.equal(emitter.listenerCount('close'), 1, 'only the public listener is registered');
  emitter.removeAllListeners();
  emitter.on('close', () => events.push(['first', released]));
  emitter.on('close', () => events.push(['last', released]));
  assert.equal(emitter.emit('close'), true);
  assert.deepEqual(await closing, { complete: true, error: null, timedOut: false, guarded: false });
  assert.equal(removed, 0, 'the deliberately removed listener is not restored');
  assert.deepEqual(events, ['stop', ['first', false], ['last', false], 'release']);
  assert.equal(emitter.emit, original);
  assert.equal(Object.hasOwn(emitter, 'emit'), false);
  assert.equal(emitter.listenerCount('error'), 0);
});

test('closeResource waits coexist with independent dispatch owners', async () => {
  const emitter = new EventEmitter(), events = [];
  const original = emitter.emit;
  const detach = afterCloseDispatch(emitter, () => events.push('owner'));
  const waits = ['first', 'second'].map(name => closeResource(emitter, {
    stop() { events.push(`${name}:stop`); },
    release() { events.push(`${name}:release`); emitter.removeAllListeners(); },
  }));
  emitter.on('close', () => events.push('guest'));
  emitter.emit('close');
  assert.deepEqual(await Promise.all(waits), Array.from({ length: 2 }, () => ({
    complete: true, error: null, timedOut: false, guarded: false,
  })));
  assert.deepEqual(events, ['first:stop', 'second:stop', 'guest', 'owner',
    'first:release', 'second:release']);
  assert.notEqual(emitter.emit, original, 'the independent owner is still attached');
  detach();
  assert.equal(emitter.emit, original);
  assert.equal(Object.hasOwn(emitter, 'emit'), false);
});

test('closeResource preserves named and symbol completion events without a public waiter', async t => {
  for (const closeEvent of ['finished', Symbol('finished')]) await t.test(String(closeEvent), async () => {
    const emitter = new EventEmitter(), events = [];
    const detach = afterCloseDispatch(emitter, () => events.push('close-owner'));
    const closing = closeResource(emitter, {
      closeEvent,
      stop() { events.push('stop'); },
      release() { events.push('release'); },
    });
    assert.equal(emitter.listenerCount(closeEvent), 0);
    emitter.removeAllListeners(closeEvent);
    emitter.emit('close');
    assert.deepEqual(events, ['stop', 'close-owner'], 'close is not the selected completion');
    detach();
    emitter.on(closeEvent, () => events.push('guest'));
    emitter.emit(closeEvent);
    assert.deepEqual(await closing, { complete: true, error: null, timedOut: false, guarded: false });
    assert.deepEqual(events, ['stop', 'close-owner', 'guest', 'release']);
    assert.equal(Object.hasOwn(emitter, 'emit'), false);
  });
});

test('closeResource still completes synchronous closure without event observation', async () => {
  for (const emitter of [{ closed: false }, new EventEmitter()]) {
    let stopped = 0, released = 0;
    const result = await closeResource(emitter, {
      closeEvent: null,
      stop() { stopped++; emitter.closed = true; },
      release() { released++; },
    });
    assert.deepEqual(result, { complete: true, error: null, timedOut: false, guarded: false });
    assert.equal(stopped, 1);
    assert.equal(released, 1);
    assert.equal(Object.hasOwn(emitter, 'emit'), false);
  }
});

test('closeResource reports dispatch attachment failure without skipping stop or release', async () => {
  const emitter = new EventEmitter(), original = emitter.emit;
  Object.defineProperty(emitter, 'emit', { configurable: false, value: original });
  let stopped = 0, released = 0;
  const result = await closeResource(emitter, {
    stop() { stopped++; emitter.closed = true; },
    release() { released++; },
  });
  assert.equal(result.complete, true);
  assert.equal(result.timedOut, false);
  assert.equal(result.error instanceof TypeError, true);
  assert.equal(stopped, 1);
  assert.equal(released, 1);
  assert.equal(emitter.emit, original);
  assert.equal(emitter.listenerCount('close'), 0);
  assert.equal(emitter.listenerCount('error'), 0);
});

test('closeResource retains release and error-detachment failures after real close', async () => {
  for (const phase of ['release', 'error-detach']) {
    const emitter = new EventEmitter(), failure = new Error(phase), original = emitter.emit;
    let released = 0;
    if (phase === 'error-detach') emitter.off = () => { throw failure; };
    const closing = closeResource(emitter, {
      stop() { emitter.emit('close'); },
      release() { released++; if (phase === 'release') throw failure; },
    });
    assert.deepEqual(await closing, { complete: true, error: failure, timedOut: false, guarded: false });
    assert.equal(released, 1);
    assert.equal(emitter.emit, original);
    assert.equal(Object.hasOwn(emitter, 'emit'), false);
    assert.equal(emitter.listenerCount('close'), 0);
    if (phase === 'error-detach')
      assert.doesNotThrow(() => emitter.emit('error', new Error('late failure')));
  }
});

test('closeResource timeout detaches only its own barrier before a late real event', async () => {
  const emitter = new EventEmitter(), original = emitter.emit;
  let observed = 0, released = 0;
  const detach = afterCloseDispatch(emitter, () => { observed++; });
  const result = await closeResource(emitter, {
    stop() { emitter.removeAllListeners(); },
    release() { released++; emitter.removeAllListeners(); },
    timeoutMs: 5,
  });
  assert.equal(result.complete, false);
  assert.equal(result.timedOut, true);
  assert.equal(result.error.code, 'TIMEOUT');
  assert.equal(result.guarded, true);
  assert.equal(released, 1);
  assert.doesNotThrow(() => emitter.emit('error', new Error('late native failure')));
  emitter.emit('close');
  assert.equal(observed, 1);
  assert.equal(released, 1, 'late completion does not release the timed-out owner twice');
  detach();
  assert.equal(emitter.emit, original);
  assert.equal(emitter.listenerCount('error'), 1);
});
