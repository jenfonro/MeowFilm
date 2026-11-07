import assert from 'node:assert/strict';
import test from 'node:test';
import { NativeEngine } from '../src/native-engine.js';
import { NativeSession } from '../src/native-session.js';
import { createScriptHost } from '../src/index.js';
import { lxScript, target } from './fixtures.js';

const timeout = 15_000;
const methods = ['transfer', 'transferToFixedLength', 'transferToImmutable'];
const bytes = Buffer.from([0, 255, 128, 65]);

// This authored fixture runs in QuickJS, including its collection barrier.
function ownershipFixture() {
  let current, old, collected, finishCollection, count = 0;
  const registry = new FinalizationRegistry(() => {
    count++;
    finishCollection();
  });
  function errorName(fn) {
    try { fn(); return null; }
    catch (error) { return error.name; }
  }
  function state(buffer) {
    return {
      length: buffer.byteLength, detached: buffer.detached,
      resizable: buffer.resizable, maximum: buffer.maxByteLength,
      immutable: buffer.immutable,
      writeError: buffer.immutable && buffer.byteLength
        ? errorName(() => new DataView(buffer).setUint8(0, 17)) : null,
      transferError: buffer.immutable ? errorName(() => buffer.transfer()) : null,
    };
  }
  function move(value, method, length) {
    const original = value.buffer, dataView = new DataView(original);
    current = length === null ? original[method]() : original[method](length);
    // Keep the detached object in a cycle until an explicit owner-thread GC.
    original.self = original;
    old = original;
    collected = new Promise(resolve => { finishCollection = resolve; });
    registry.register(original, null);
    return {
      oldDetached: original.detached, oldLength: original.byteLength,
      oldViewLength: value.byteLength,
      oldViewError: errorName(() => new Uint8Array(original)),
      oldDataViewError: errorName(() => dataView.getUint8(0)),
      oldTransferError: errorName(() => original[method]()),
      current: state(current),
    };
  }
  return {
    move,
    fail(value, method, kind) {
      const original = value.buffer, sentinel = new Error('conversion-failure');
      const length = kind === 'coercion' ? { valueOf() { throw sentinel; } }
        : kind === 'negative' ? -1 : 0x80000000;
      let failure = null, exact = false;
      try { original[method](length); }
      catch (error) { failure = error.name; exact = error === sentinel; }
      current = original;
      return { failure, exact, current: state(current),
        viewLength: value.byteLength, bytes: new Uint8Array(current) };
    },
    moveCurrent(method) { return move(new Uint8Array(current), method, null); },
    dropOld() { old = null; return null; },
    async read() {
      await collected;
      return { bytes: new Uint8Array(current), current: state(current), collected: count };
    },
    release() {
      current = old = collected = finishCollection = null;
      return null;
    },
  };
}

const nativeMethods = `(() => {
  const ownership = (${ownershipFixture})();
  async function input(origin, value) {
    if (origin === 'host') return await __meowHost('bytes', value);
    if (origin === 'guest') return new Uint8Array(value);
    if (origin === 'resizable') {
      const result = new Uint8Array(new ArrayBuffer(value.length, { maxByteLength: 8 }));
      result.set(value);
      return result;
    }
    return value;
  }
  return {
    async move(origin, value, method, length) {
      return ownership.move(await input(origin, value), method, length);
    },
    async fail(origin, value, method, kind) {
      return ownership.fail(await input(origin, value), method, kind);
    },
    moveCurrent(method) { return ownership.moveCurrent(method); },
    dropOld() { return ownership.dropOld(); },
    read() { return ownership.read(); },
    release() { return ownership.release(); },
  };
})()`;

async function create(t, options) {
  const engine = NativeEngine.create(options);
  t.after(() => engine.stop());
  await engine.ready;
  return engine;
}

async function owner(t, contexts) {
  const names = ['move', 'fail', 'moveCurrent', 'dropOld', 'read', 'release'];
  return create(t, {
    source: contexts === 'single' ? nativeMethods : `({
      ${names.map(name => `${name}(...args) { return __meowGuest('${name}', args); }`).join(',')}
    })`,
    ...(contexts === 'dual' ? { guestSource: nativeMethods } : {}),
    onHostCall(name, value) {
      assert.equal(name, 'bytes');
      assert.ok(Buffer.isBuffer(value));
      // Return an actual host subview, not a JSON-array stand-in for bytes.
      return Buffer.concat([Buffer.from([91]), value, Buffer.from([92])]).subarray(1, -1);
    },
  });
}

function expectedState(method, length, resizable = false) {
  const immutable = method === 'transferToImmutable';
  return {
    length, detached: false, resizable, maximum: resizable ? 8 : length, immutable,
    writeError: immutable && length > 0 ? 'TypeError' : null,
    transferError: immutable ? 'TypeError' : null,
  };
}

function assertMoved(actual, method, length, resizable = false) {
  assert.deepEqual(actual, {
    oldDetached: true, oldLength: 0, oldViewLength: 0,
    oldViewError: 'TypeError', oldDataViewError: 'TypeError', oldTransferError: 'TypeError',
    current: expectedState(method, length, resizable),
  });
}

function resized(input, length) {
  const result = Buffer.alloc(length);
  input.copy(result, 0, 0, Math.min(input.length, length));
  return result;
}

function assertClosed(exit) {
  assert.equal(exit.stopped, true);
  for (const key of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls', 'hostCalls',
    'timers', 'jobsPending', 'byteBuffers', 'byteBufferBytes', 'heapBytes', 'heapObjects']) {
    assert.equal(exit[key], 0, key);
  }
}

async function collectOld(engine, expected, state, count) {
  await engine.call('dropOld');
  await engine.stats({ gc: true });
  const result = await engine.call('read');
  assert.ok(Buffer.isBuffer(result.bytes));
  assert.deepEqual(result.bytes, expected);
  assert.deepEqual(result.current, state);
  assert.equal(result.collected, count, 'the old ArrayBuffer really reached finalization');
}

async function releaseBytes(engine, baseline) {
  await engine.call('release');
  const released = await engine.stats({ gc: true });
  assert.equal(released.byteBuffers, baseline.byteBuffers);
  assert.equal(released.byteBufferBytes, baseline.byteBufferBytes);
  assert.equal(released.activeCalls, 0);
  assert.equal(released.hostCalls, 0);
}

for (const contexts of ['single', 'dual']) for (const method of methods) {
  test(`native ${method} preserves bytes after old buffer GC (${contexts} Context)`,
    { timeout }, async t => {
      const engine = await owner(t, contexts);
      const other = await create(t, {
        source: '(() => { let count = 0; return { next() { return ++count; } }; })()',
      });
      assert.equal(await other.call('next'), 1);
      const baseline = await engine.stats({ gc: true });
      assert.equal(baseline.contextCount, contexts === 'single' ? 1 : 2);
      assert.equal(baseline.runtimeCount, 1);
      let collected = 0;
      for (const origin of ['argument', 'host', 'guest', 'resizable']) {
        for (const length of [null, 0, 2, 4, 7]) {
          const backing = Buffer.concat([Buffer.from([83]), bytes, Buffer.from([71])]);
          const input = backing.subarray(1, -1);
          const size = length ?? input.length;
          const resizable = origin === 'resizable' && method === 'transfer';
          assertMoved(await engine.call('move', [origin, input, method, length]),
            method, size, resizable);
          await collectOld(engine, resized(bytes, size), expectedState(method, size, resizable), ++collected);
          assert.deepEqual(backing, Buffer.from([83, 0, 255, 128, 65, 71]));
          await releaseBytes(engine, baseline);
        }
      }
      for (const origin of ['argument', 'host', 'guest']) for (const length of [null, 3]) {
        assertMoved(await engine.call('move', [origin, Buffer.alloc(0), method, length]),
          method, length ?? 0);
        await collectOld(engine, Buffer.alloc(length ?? 0),
          expectedState(method, length ?? 0), ++collected);
        await releaseBytes(engine, baseline);
      }
      assertClosed(await engine.stop());
      assert.equal(engine.hostTasks.size, 0);
      assert.equal(engine.hostControllers.size, 0);
      assert.equal(await other.call('next'), 2);
      assertClosed(await other.stop());
    });

  test(`native ${method} failures preserve the original buffer (${contexts} Context)`,
    { timeout }, async t => {
      const engine = await owner(t, contexts);
      const baseline = await engine.stats({ gc: true });
      let collected = 0;
      for (const origin of ['argument', 'host', 'guest', 'resizable']) {
        for (const kind of ['negative', 'coercion', 'unrepresentable']) {
          const failure = kind === 'coercion' ? 'Error'
            : origin === 'resizable' && method === 'transfer' && kind === 'unrepresentable'
              ? 'TypeError' : 'RangeError';
          assert.deepEqual(await engine.call('fail', [origin, bytes, method, kind]), {
            failure, exact: kind === 'coercion', bytes,
            viewLength: bytes.length,
            current: expectedState('transfer', bytes.length, origin === 'resizable'),
          });
          const resizable = origin === 'resizable' && method === 'transfer';
          assertMoved(await engine.call('moveCurrent', [method]), method, bytes.length, resizable);
          await collectOld(engine, bytes, expectedState(method, bytes.length, resizable), ++collected);
          await releaseBytes(engine, baseline);
        }
      }
      assertClosed(await engine.stop());
      assert.equal(engine.hostTasks.size, 0);
    });
}

for (const method of methods) {
  test(`LX ${method} preserves encoded, random and host response bytes after old buffer GC`,
    { timeout }, async t => {
      const sessions = [], requests = [];
      const host = createScriptHost({
        adapters: { lx: input => {
          const session = new NativeSession(input);
          sessions.push(session);
          return session;
        } },
        async network(input) {
          requests.push(input);
          assert.equal(input.options.encoding, null);
          return { statusCode: 200,
            body: Buffer.from([83, 0, 255, 128, 65, 71]).subarray(1, -1) };
        },
      });
      t.after(() => host.close());
      await host.install({ installationId: 'a', enabled: true, source: lxScript({
        setup: `const ownership = (${ownershipFixture})();`,
        handler: `async ({info}) => {
          const { operation, origin, length, method } = info.musicInfo;
          if (operation === 'dropOld') return ownership.dropOld();
          if (operation === 'read') return ownership.read();
          if (operation === 'release') return ownership.release();
          let value;
          if (origin === 'encoded') value = Buffer.from('00ff8041', 'hex');
          else if (origin === 'random') value = lx.utils.crypto.randomBytes(4);
          else value = await new Promise((resolve, reject) => lx.request(
            'https://fixture.invalid/ownership', { encoding: null },
            (error, response, body) => error ? reject(error) : resolve(body)));
          const before = Array.from(value);
          return { before, buffer: Buffer.isBuffer(value), moved: ownership.move(value, method, length) };
        }`,
      }) });
      await host.install({ installationId: 'b', enabled: true,
        source: lxScript({ setup: 'let count = 0;', handler: 'async () => ++count' }) });
      const engine = sessions[0].engine;
      const baseline = await engine.stats({ gc: true });
      assert.equal(await host.invoke(target('b')), 1);
      let collected = 0;
      for (const origin of ['encoded', 'random', 'network']) for (const length of [null, 0, 2, 4, 7]) {
        const result = await host.invoke(target('a', 'kw', { origin, length, method }));
        assert.equal(result.buffer, true);
        assert.equal(result.before.length, bytes.length);
        if (origin !== 'random') assert.deepEqual(result.before, [...bytes]);
        const size = length ?? bytes.length;
        assertMoved(result.moved, method, size);
        await host.invoke(target('a', 'kw', { operation: 'dropOld' }));
        await engine.stats({ gc: true });
        const retained = await host.invoke(target('a', 'kw', { operation: 'read' }));
        assert.ok(retained.bytes instanceof Uint8Array);
        assert.deepEqual(retained.bytes, new Uint8Array(resized(Buffer.from(result.before), size)));
        assert.deepEqual(retained.current, expectedState(method, size));
        assert.equal(retained.collected, ++collected);
        await host.invoke(target('a', 'kw', { operation: 'release' }));
        const released = await engine.stats({ gc: true });
        assert.equal(released.byteBuffers, baseline.byteBuffers);
        assert.equal(released.byteBufferBytes, baseline.byteBufferBytes);
      }
      assert.equal(requests.length, 5);
      await host.disable('a');
      assertClosed(await engine.exited);
      assert.equal(sessions[0].stats().pendingAdapterTasks, 0);
      assert.equal(host.cleanupPending('a'), 0);
      assert.equal(await host.invoke(target('b')), 2);
      await host.close();
      assertClosed(await sessions[1].engine.exited);
    });
}
