import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createHash, createHmac, createCipheriv, generateKeyPairSync, publicEncrypt, constants } from 'node:crypto';
import { deflateSync } from 'node:zlib';
import { Worker } from 'node:worker_threads';
import { NativeEngine } from '../src/native-engine.js';

const timeout = 15_000;
async function create(t, options) {
  const engine = NativeEngine.create(options);
  t.after(() => engine.stop());
  await engine.ready;
  return engine;
}

async function withoutFallbackStop(engine, scenario) {
  let timer, usedFallback = false;
  const expired = new Promise((_resolve, reject) => {
    timer = setTimeout(() => {
      usedFallback = true;
      reject(new Error('Native scenario exceeded its external deadline; fallback stop is not cancellation evidence'));
      engine.stop().catch(() => {});
    }, 5_000);
  });
  try {
    const result = await Promise.race([Promise.resolve().then(scenario), expired]);
    assert.equal(usedFallback, false, 'fallback stop must not count as request interruption');
    return result;
  } finally { clearTimeout(timer); }
}

test('native owners retain state, run concurrent calls and exit exactly once', { timeout }, async t => {
  const source = `(() => {
    let count = 0;
    return {
      add() { return ++count; },
      async delay(value, ms) { await new Promise(r => setTimeout(r, ms)); return value; },
    };
  })()`;
  const a = await create(t, { source }), b = await create(t, { source });
  assert.equal(await a.call('add'), 1);
  assert.equal(await a.call('add'), 2);
  assert.equal(await b.call('add'), 1);
  const order = [];
  await Promise.all([
    a.call('delay', ['slow', 40], { scope: 1 }).then(v => order.push(v)),
    a.call('delay', ['fast', 1], { scope: 2 }).then(v => order.push(v)),
  ]);
  assert.deepEqual(order, ['fast', 'slow']);
  const stats = await a.stats({ refresh: true });
  assert.equal(stats.runtimeCount, 1); assert.equal(stats.contextCount, 1); assert.equal(stats.threadCount, 1);
  assert.equal(stats.activeCalls, 0);
  assert.ok(stats.heapBytes > 0); assert.ok(stats.heapObjects > 0);
  const stop = a.stop(); assert.equal(a.stop(), stop);
  const closed = await stop;
  for (const key of ['runtimeCount', 'contextCount', 'threadCount', 'activeCalls', 'hostCalls',
    'timers', 'heapBytes', 'heapObjects', 'byteBuffers', 'byteBufferBytes', 'jobsPending']) assert.equal(closed[key], 0, key);
  assert.equal(closed.stopped, true);
  await assert.rejects(a.call('add'), { code: 'STALE_SESSION' });
  assert.equal(await b.call('add'), 2);
});

test('typed bridge snapshots subviews, Unicode keys and byte leaves without sharing identity', { timeout }, async t => {
  let observed;
  const engine = await create(t, {
    source: `({
      async echo(value) { await new Promise(r => setTimeout(r, 10)); return value; },
      async host(value) { const result = await __meowHost('echo', value); result.bytes[0] = 17; return result; },
    })`,
    onHostCall(op, value, scope) {
      assert.equal(op, 'echo'); assert.equal(scope, 42);
      assert.ok(Buffer.isBuffer(value.bytes));
      observed = value; return value;
    },
  });
  const backing = Buffer.from([99, 0, 255, 128, 42, 88]);
  const view = new Uint8Array(backing.buffer, backing.byteOffset + 1, 4);
  const value = Object.create(null);
  value.bytes = view; value['nul\0key'] = 'a\0b😀'; value['é'] = 1; value['e\u0301'] = 2;
  value.__proto__ = { token: 'business', blob: 'value' };
  const promise = engine.call('echo', [value]);
  backing.fill(3);
  const result = await promise;
  assert.deepEqual(result.bytes, Buffer.from([0, 255, 128, 42]));
  assert.equal(Object.getPrototypeOf(result), Object.prototype);
  assert.equal(result['nul\0key'], 'a\0b😀'); assert.equal(result['é'], 1); assert.equal(result['e\u0301'], 2);
  assert.equal(result.__proto__.token, 'business');
  assert.deepEqual(await engine.call('echo', [Buffer.alloc(0)]), Buffer.alloc(0));
  const hostResult = await engine.call('host', [result], { scope: 42 });
  assert.equal(hostResult.bytes[0], 17); assert.equal(observed.bytes[0], 0);
  await assert.rejects(engine.call('echo', ['\ud800']), { code: 'INVALID_ARGUMENT' });
  await assert.rejects(engine.call('echo', [Infinity]), { code: 'INVALID_ARGUMENT' });
  const cycle = {}; cycle.cycle = cycle;
  await assert.rejects(engine.call('echo', [cycle]), { code: 'INVALID_ARGUMENT' });
});

function nestedData(depth, leaf = null, objects = false) {
  let value = leaf;
  for (let index = 0; index < depth; index++) value = objects ? { child: value } : [value];
  return value;
}

// REV-1B: a positional list is not a layer of either argument's business data.
for (const contexts of ['single', 'dual']) {
  test(`64-level data survives arguments, results, events and host completion (${contexts} Context)`, { timeout }, async t => {
    const events = [], hostValues = [];
    let completion;
    const methods = `({
      echo(value, marker, other) {
        if (marker !== 'positional' || other !== 73) throw new Error('argument order');
        return value;
      },
      async later(value) { await Promise.resolve(); return value; },
      result(depth, objects) {
        let value = null;
        for (let i = 0; i < depth; i++) value = objects ? {child: value} : [value];
        return value;
      },
      async host(value) { return await __meowHost('echo', value); },
      async completion() { return await __meowHost('completion', null); },
      event(value) { __meowEmit('depth', value); return null; },
      core(value) { return __meowCore('echoCore', [value, 'positional', 73]); },
    })`;
    const engine = await create(t, {
      source: contexts === 'single' ? methods : `({
        echo(...args) { return __meowGuest('echo', args); },
        later(value) { return __meowGuest('later', [value]); },
        result(...args) { return __meowGuest('result', args); },
        host(value) { return __meowGuest('host', [value]); },
        completion() { return __meowGuest('completion', []); },
        event(value) { return __meowGuest('event', [value]); },
        core(value) { return __meowGuest('core', [value]); },
        echoCore(value, marker, other) {
          if (marker !== 'positional' || other !== 73) throw new Error('argument order');
          return value;
        },
      })`,
      ...(contexts === 'dual' ? { guestSource: methods, guestHelpers: ['echoCore'] } : {}),
      onHostCall(name, value, scope) {
        assert.equal(scope, 64);
        if (name === 'completion') { assert.equal(value, null); return completion; }
        assert.equal(name, 'echo');
        hostValues.push(value);
        return value;
      },
      onEvent(name, value, scope) { events.push({ name, value, scope }); },
    });
    for (const objects of [false, true]) {
      const value = nestedData(64, null, objects);
      assert.deepEqual(await engine.call('echo', [value, 'positional', 73]), value);
      assert.deepEqual(await engine.call('later', [value]), value);
      assert.deepEqual(await engine.call('result', [64, objects]), value);
      if (contexts === 'dual') assert.deepEqual(await engine.call('core', [value]), value);
      assert.deepEqual(await engine.call('host', [value], { scope: 64 }), value);
      assert.deepEqual(hostValues.at(-1), value);
      completion = value;
      assert.deepEqual(await engine.call('completion', [], { scope: 64 }), value);
      assert.equal(await engine.call('event', [value], { scope: 64 }), null);
      assert.deepEqual(events.at(-1), { name: 'depth', value, scope: 64 });
    }
    const backing = Buffer.from([99, 0, 255, 128, 42, 88]);
    const view = new Uint8Array(backing.buffer, backing.byteOffset + 1, 4);
    const bytes = nestedData(64, view);
    const snapshotted = engine.call('later', [bytes]);
    backing.fill(3);
    const expectedBytes = nestedData(64, Buffer.from([0, 255, 128, 42]));
    assert.deepEqual(await snapshotted, expectedBytes);
    assert.deepEqual(await engine.call('host', [expectedBytes], { scope: 64 }), expectedBytes);
    const stats = await engine.stats({ refresh: true });
    assert.equal(stats.contextCount, contexts === 'dual' ? 2 : 1);
    assert.equal(stats.runtimeCount, 1);
    assert.equal(stats.activeCalls, 0);
    assert.equal(stats.hostCalls, 0);
  });

  test(`business depth, cycles and invalid scalars remain rejected (${contexts} Context)`, { timeout }, async t => {
    const cycle = []; cycle.push(cycle);
    const invalid = new Map([
      ['depth', nestedData(65)], ['objectDepth', nestedData(65, null, true)],
      ['envelope', { ok: true, value: nestedData(64) }], ['cycle', cycle],
      ['unicode', '\ud800'], ['number', Infinity], ['unsafe', Number.MAX_SAFE_INTEGER + 1],
      ['bigint', 1n],
    ]);
    const methods = `(() => {
      function invalid(kind) {
        if (kind === 'cycle') { const value = []; value.push(value); return value; }
        if (kind === 'unicode') return '\\ud800';
        if (kind === 'number') return Infinity;
        if (kind === 'unsafe') return 9007199254740992;
        if (kind === 'bigint') return 1n;
        let value = null;
        for (let i = 0; i < (kind === 'envelope' ? 64 : 65); i++) {
          value = kind === 'objectDepth' ? {child: value} : [value];
        }
        return kind === 'envelope' ? {ok: true, value} : value;
      }
      return {
        echo(value) { return value; },
        invalid(kind, route) {
          const value = invalid(kind);
          if (route === 'host') return __meowHost('invalid-input', value);
          if (route === 'core') return __meowCore('echoCore', [value]);
          return value;
        },
        async invalidLater(kind) { await Promise.resolve(); return invalid(kind); },
        completion(kind) { return __meowHost('invalid-completion', kind); },
        badArgs() { return __meowNative('random', {0: 0}); },
      };
    })()`;
    const engine = await create(t, {
      source: contexts === 'single' ? methods : `({
        echo(value) { return __meowGuest('echo', [value]); },
        invalid(...args) { return __meowGuest('invalid', args); },
        invalidLater(kind) { return __meowGuest('invalidLater', [kind]); },
        completion(kind) { return __meowGuest('completion', [kind]); },
        badArgs() { return __meowGuest('badArgs', []); },
        badCrossArgs() { return __meowGuest('echo', {0: null}); },
        echoCore(value) { return value; },
      })`,
      ...(contexts === 'dual' ? { guestSource: methods, guestHelpers: ['echoCore'] } : {}),
      onHostCall(name, kind) {
        assert.equal(name, 'invalid-completion', 'invalid host input must be rejected before dispatch');
        assert.ok(invalid.has(kind));
        return invalid.get(kind);
      },
    });
    for (const [kind, value] of invalid) {
      await assert.rejects(engine.call('echo', [value]), { code: 'INVALID_ARGUMENT' }, kind);
      await assert.rejects(engine.call('invalid', [kind]), { code: 'INVALID_ARGUMENT' }, kind);
      await assert.rejects(engine.call('invalidLater', [kind]), { code: 'INVALID_ARGUMENT' }, kind);
      await assert.rejects(engine.call('invalid', [kind, 'host']), { code: 'INVALID_ARGUMENT' }, kind);
      await assert.rejects(engine.call('completion', [kind]), { code: 'INVALID_ARGUMENT' }, kind);
      if (contexts === 'dual') {
        await assert.rejects(engine.call('invalid', [kind, 'core']), { code: 'INVALID_ARGUMENT' }, kind);
      }
    }
    await assert.rejects(engine.call('echo', { 0: null }), { code: 'INVALID_ARGUMENT' });
    await assert.rejects(engine.call('badArgs'), { code: 'INVALID_ARGUMENT' });
    if (contexts === 'dual') {
      await assert.rejects(engine.call('badCrossArgs'), { code: 'INVALID_ARGUMENT' });
    }
    assert.equal(await engine.call('echo', ['still-live']), 'still-live');
    const stats = await engine.stats({ refresh: true });
    assert.equal(stats.activeCalls, 0);
    assert.equal(stats.hostCalls, 0);
    assert.equal(stats.stopped, false);
  });
}

for (const contexts of ['single', 'dual']) {
  test(`positional host input retains 64 levels without weakening business limits (${contexts} Context)`,
    { timeout }, async t => {
      const received = [];
      let completion = null;
      const methods = `({
        roundTrip(value, bytes) { return __meowHostArgs('echo', ['marker', value, bytes], 641); },
        invalid(kind) {
          if (kind === 'args') return __meowHostArgs('invalid', {0:null});
          let value = null;
          for (let i = 0; i < (kind === 'envelope' ? 64 : 65); i++) value = [value];
          if (kind === 'envelope') value = {ok:true, value};
          if (kind === 'cycle') { value = []; value.push(value); }
          return __meowHostArgs('invalid', [value]);
        },
        wrongScope() { return __meowHostArgs('invalid', [null], 642); },
        completion() { return __meowHostArgs('completion', []); },
      })`;
      const engine = await create(t, {
        source: contexts === 'single' ? methods : `({
          roundTrip(...args) { return __meowGuest('roundTrip', args); },
          invalid(kind) { return __meowGuest('invalid', [kind]); },
          wrongScope() { return __meowGuest('wrongScope', []); },
          completion() { return __meowGuest('completion', []); },
        })`,
        ...(contexts === 'dual' ? { guestSource: methods } : {}),
        onHostCall(name, args, scope) {
          assert.equal(scope, 641);
          assert.notEqual(name, 'invalid', 'invalid data and identity must fail before host dispatch');
          if (name === 'completion') { assert.deepEqual(args, []); return completion; }
          assert.equal(name, 'echo');
          received.push(args);
          return args[1];
        },
      });
      const options = { scope: 641 };
      for (const objects of [false, true]) {
        const value = nestedData(64, null, objects);
        const bytes = nestedData(64, Buffer.from([0, 255, 65]), objects);
        assert.deepEqual(await engine.call('roundTrip', [value, bytes], options), value);
        assert.deepEqual(received.at(-1), ['marker', value, bytes]);
        completion = value;
        assert.deepEqual(await engine.call('completion', [], options), value);
      }
      for (const kind of ['depth', 'envelope', 'cycle', 'args']) {
        await assert.rejects(engine.call('invalid', [kind], options), { code: 'INVALID_ARGUMENT' });
      }
      completion = nestedData(65);
      await assert.rejects(engine.call('completion', [], options), { code: 'INVALID_ARGUMENT' });
      await assert.rejects(engine.call('wrongScope', [], options), { code: 'STALE_SESSION' });
      assert.deepEqual(await engine.call('roundTrip', [nestedData(64), null], options), nestedData(64));
      const stats = await engine.stats({ refresh: true });
      for (const key of ['activeCalls', 'hostCalls', 'timers', 'jobsPending']) assert.equal(stats[key], 0, key);
      assert.equal(stats.contextCount, contexts === 'dual' ? 2 : 1);
      assert.equal(stats.stopped, false);
    });

  test(`cancelled positional host calls cannot borrow a later task's authority (${contexts} Context)`,
    { timeout }, async t => {
      let enter, release, heldSignal;
      const entered = new Promise(resolve => { enter = resolve; });
      const held = new Promise(resolve => { release = resolve; });
      const hosts = [], events = [];
      const methods = `(() => {
        let resume, late;
        return {
          first(value) {
            late = new Promise(resolve => { resume = resolve; })
              .then(() => __meowHostArgs('forbidden', [value]))
              .then(() => 'unsafe', error => error.code);
            return __meowHostArgs('hold', [value], 651);
          },
          async second(value) {
            resume();
            __meowEmit('fenced', {code:await late, scope:__meowTaskContext()});
            return __meowHostArgs('echo', [value], 652);
          },
        };
      })()`;
      const engine = await create(t, {
        source: contexts === 'single' ? methods : `({
          first(value) { return __meowGuest('first', [value]); },
          second(value) { return __meowGuest('second', [value]); },
        })`,
        ...(contexts === 'dual' ? { guestSource: methods } : {}),
        onHostCall(name, args, scope, { signal }) {
          hosts.push([name, scope]);
          assert.deepEqual(args, [nestedData(64)]);
          if (name === 'hold') {
            heldSignal = signal;
            enter();
            return held;
          }
          assert.equal(name, 'echo');
          return args[0];
        },
        onEvent(name, value, scope) { events.push({ name, value, scope }); },
      });
      await withoutFallbackStop(engine, async () => {
        const controller = new AbortController();
        const cancelled = assert.rejects(engine.call('first', [nestedData(64)], {
          scope: 651, signal: controller.signal,
        }), { code: 'CANCELLED' });
        try {
          await entered;
          controller.abort();
          await cancelled;
          assert.equal(heldSignal.aborted, true);
          assert.deepEqual(await engine.call('second', [nestedData(64)], { scope: 652 }), nestedData(64));
          assert.deepEqual(hosts, [['hold', 651], ['echo', 652]]);
          assert.deepEqual(events, [{
            name: 'fenced', value: { code: 'STALE_SESSION', scope: 652 }, scope: 652,
          }]);
        } finally { release(nestedData(64)); }
        await Promise.all([...engine.hostTasks]);
        const stats = await engine.stats({ refresh: true });
        for (const key of ['activeCalls', 'hostCalls', 'timers', 'jobsPending']) assert.equal(stats[key], 0, key);
        assert.equal(stats.stopped, false);
      });
    });
}

// QYSG SIGSEGV / S02: CString ownership must not depend on substring storage.
for (const contexts of ['single', 'dual']) {
  test(`wide strings and long slices retain exact values across bridges and encode (${contexts} Context)`, { timeout }, async t => {
    const input = '普通宽串é😀\0'.repeat(160);
    const expected = {
      plain: input, slice: input, latin: 'é'.repeat(1400), literalLatin: 'café',
      pairs: '😀é中文\0'.repeat(180), empty: '',
    };
    const events = [];
    const source = `({
      async inspect(value) {
        const samples = {
          plain: value,
          slice: ('开始' + value + '结束').slice(2, -2),
          latin: ('xx' + 'é'.repeat(1400) + 'yy').slice(2, -2),
          literalLatin: 'café',
          pairs: ('prefix' + '😀é中文\\0'.repeat(180) + 'suffix').slice(6, -6),
          empty: '',
        };
        const encoded = {};
        for (const key of Object.keys(samples)) encoded[key] = __meowNative('encode', [samples[key], 'utf8']);
        __meowEmit('strings', samples);
        return { samples, encoded, echoed: await __meowHost('strings', samples) };
      },
      invalid(which) {
        if (which === 'slice') return '😀'.repeat(600).slice(1, -1);
        return which === 'high' ? '\\ud800' : '\\udc00';
      },
      invalidEncode() { return __meowNative('encode', ['\\ud800', 'utf8']); },
    })`;
    const engine = await create(t, {
      source: contexts === 'single' ? source : `({
        inspect(value) { return __meowGuest('inspect', [value]); },
        invalid(which) { return __meowGuest('invalid', [which]); },
        invalidEncode() { return __meowGuest('invalidEncode', []); },
      })`,
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      onHostCall(name, value, scope) {
        assert.equal(name, 'strings'); assert.equal(scope, 221);
        assert.deepEqual(value, expected);
        return value;
      },
      onEvent(name, value, scope) { events.push({ name, value, scope }); },
    });
    const result = await engine.call('inspect', [input], { scope: 221 });
    assert.deepEqual(result.samples, expected);
    assert.deepEqual(result.echoed, expected);
    for (const [key, text] of Object.entries(expected)) {
      assert.deepEqual(result.encoded[key], Buffer.from(text, 'utf8'), key);
    }
    assert.deepEqual(events, [{ name: 'strings', value: expected, scope: 221 }]);
    for (const which of ['high', 'low', 'slice']) {
      await assert.rejects(engine.call('invalid', [which]), { code: 'INVALID_ARGUMENT' });
    }
    await assert.rejects(engine.call('invalidEncode'), { code: 'INVALID_ARGUMENT' });
    assert.equal(engine.stats().stopped, false);
    assert.deepEqual((await engine.call('inspect', [input], { scope: 221 })).samples, expected);
  });
}

test('native object outputs retain ordinary nested prototypes and safe own special keys', { timeout }, async t => {
  const prototypes = [Object.prototype, Array.prototype];
  const originalDescriptors = prototypes.map(value => Object.getOwnPropertyDescriptors(value));
  const data = label => ({
    label,
    ['__proto__']: { nativeValuePollution: label },
    constructor: { prototype: { nativeValuePollution: label } },
  });
  const expected = { nested: data('nested'), array: [data('array'), { leaf: { value: 7 } }] };
  const input = { nested: Object.create(null), array: [Object.create(null), { leaf: { value: 7 } }] };
  for (const [object, label] of [[input.nested, 'nested'], [input.array[0], 'array']]) {
    object.label = label;
    object.__proto__ = { nativeValuePollution: label };
    object.constructor = { prototype: { nativeValuePollution: label } };
  }
  function verify(value) {
    assert.deepEqual(value, expected);
    function prototypesOf(item) {
      if (item === null || typeof item !== 'object') return;
      assert.equal(Object.getPrototypeOf(item), Array.isArray(item) ? Array.prototype : Object.prototype);
      for (const child of Object.values(item)) prototypesOf(child);
    }
    prototypesOf(value);
    for (const [actual, wanted] of [[value.nested, expected.nested], [value.array[0], expected.array[0]]]) {
      for (const key of ['__proto__', 'constructor']) {
        assert.deepEqual(Object.getOwnPropertyDescriptor(actual, key), {
          value: wanted[key], writable: true, enumerable: true, configurable: true,
        });
      }
    }
    prototypes.forEach((prototype, index) => {
      assert.deepEqual(Object.getOwnPropertyDescriptors(prototype), originalDescriptors[index]);
    });
    assert.equal(({}).nativeValuePollution, undefined);
  }
  let hostCalls = 0;
  const events = [];
  const engine = await create(t, {
    source: `({
      echo(value) { return value; },
      async exchange(value) {
        const result = await __meowHost('objects', value);
        __meowEmit('objects', result);
        return result;
      },
    })`,
    onHostCall(operation, value, scope) {
      assert.equal(operation, 'objects'); assert.equal(scope, 61);
      verify(value); hostCalls++;
      return value;
    },
    onEvent(name, value, scope) { events.push({ name, value, scope }); },
  });
  verify(await engine.call('echo', [input]));
  verify(await engine.call('exchange', [input], { scope: 61 }));
  assert.equal(hostCalls, 1);
  assert.equal(events.length, 1);
  assert.equal(events[0].name, 'objects'); assert.equal(events[0].scope, 61);
  verify(events[0].value);
});

test('core and guest share one Runtime with cloned async values and allowlisted synchronous reentry', { timeout }, async t => {
  const engine = await create(t, {
    source: `(() => {
      let state = 0;
      return {
        helper(value) { value.changed = true; return { value: value.n + ++state, scope: __meowTaskContext() }; },
        async run(value) { const result = await __meowGuest('run', [value]); return { result, input: value }; },
        state() { return state; },
        denied() { return __meowGuest('denied', []); },
      };
    })()`,
    guestSource: `({
      async run(value) {
        value.n++;
        await Promise.resolve();
        const result = __meowCore('helper', [value]);
        return { result, changed: value.changed ?? false, scope: __meowTaskContext() };
      },
      denied() { return __meowCore('state', []); },
    })`,
    guestHelpers: ['helper'],
  });
  const result = await engine.call('run', [{ n: 5 }], { scope: 73 });
  assert.equal(result.input.n, 5); assert.equal(result.result.result.value, 7);
  assert.equal(result.result.changed, false);
  assert.equal(result.result.result.scope, 73); assert.equal(result.result.scope, 73);
  await assert.rejects(engine.call('denied'), { code: 'UNSUPPORTED_OPTION' });
  const stats = await engine.stats({ refresh: true });
  assert.equal(stats.runtimeCount, 1); assert.equal(stats.contextCount, 2); assert.equal(stats.threadCount, 1);
});

test('native Promise/await, generators, finalizers and timers preserve registration task identity', { timeout }, async t => {
  const events = [];
  let didFinalize;
  const finalized = new Promise(resolve => { didFinalize = resolve; });
  const engine = await create(t, {
    source: `(() => {
      let resolve, generator, registry;
      const pending = new Promise(r => { resolve = r; });
      return {
        register() { return pending.then(() => __meowTaskContext()); },
        async awaitPending() { await pending; return __meowTaskContext(); },
        resolve() { resolve(); return __meowTaskContext(); },
        async timer() { await new Promise(r => setTimeout(r, 1)); return __meowTaskContext(); },
        generator() {
          generator = (async function* () {
            await new Promise(r => setTimeout(r, 10)); yield __meowTaskContext();
            await Promise.resolve(); yield __meowTaskContext();
          })();
        },
        next() { return generator.next(); },
        finalizer() {
          registry = new FinalizationRegistry(value => __meowEmit('final', { value, scope: __meowTaskContext() }));
          let obj = {}; obj.self = obj; registry.register(obj, 'held'); obj = null;
        },
        noop() { return null; },
      };
    })()`,
    onEvent(name, value, scope) { events.push({ name, value, scope }); didFinalize(); },
  });
  const reaction = engine.call('register', [], { scope: 101 });
  const awaiting = engine.call('awaitPending', [], { scope: 102 });
  assert.equal(await engine.call('resolve', [], { scope: 103 }), 103);
  assert.equal(await reaction, 101); assert.equal(await awaiting, 102);
  assert.equal(await engine.call('timer', [], { scope: 104 }), 104);
  await engine.call('generator');
  const one = engine.call('next', [], { scope: 201 }), two = engine.call('next', [], { scope: 202 });
  assert.equal((await one).value, 201); assert.equal((await two).value, 202);
  await engine.call('finalizer', [], { scope: 301 });
  await engine.stats({ gc: true }); await finalized;
  assert.equal(events.length, 1); assert.equal(events[0].name, 'final');
  assert.equal(events[0].scope, 301); assert.equal(events[0].value.scope, 301);
});

test('interrupts cancel synchronous JS, infinite jobs and initialization while other owners progress', { timeout }, async t => {
  const events = [];
  const source = `({
    spin() {
      const scope = __meowTaskContext();
      __meowTaskContext(0);
      __meowTaskContext(scope);
      for (;;) {}
    },
    async jobs() { for (;;) await Promise.resolve(); },
    value() { return 7; },
  })`;
  const engine = await create(t, { source,
    onEvent(name, value, scope) { events.push({ name, code: value.code, scope }); },
  });
  const other = await create(t, { source: '({ value() { return 9; } })' });
  let ticks = 0;
  const timer = setInterval(() => ticks++, 5); t.after(() => clearInterval(timer));
  await assert.rejects(engine.call('spin', [], { scope: 1, timeoutMs: 40 }), { code: 'TIMEOUT' });
  assert.deepEqual(events, [{ name: 'failure', code: 'TIMEOUT', scope: 1 }]);
  await engine.exited;
  assert.equal(engine.stats().threadCount, 0);
  assert.equal(await other.call('value'), 9);
  assert.ok(ticks > 0);
  const controller = new AbortController();
  const jobsEngine = await create(t, { source,
    onEvent(name, value, scope) { events.push({ name, code: value.code, scope }); },
  });
  const jobs = jobsEngine.call('jobs', [], { scope: 2, signal: controller.signal });
  setTimeout(() => controller.abort(), 20);
  await assert.rejects(jobs, { code: 'CANCELLED' });
  assert.deepEqual(events[1], { name: 'failure', code: 'CANCELLED', scope: 2 });
  await jobsEngine.exited;
  const startup = NativeEngine.create({ source: 'for (;;) {}', timeoutMs: 30 });
  t.after(() => startup.stop());
  await assert.rejects(startup.ready, { code: 'TIMEOUT' });
  await startup.stop();
  assert.equal(startup.stats().threadCount, 0);
  const invalid = NativeEngine.create({ source: '(() => {' });
  t.after(() => invalid.stop());
  await assert.rejects(invalid.ready, { code: 'SCRIPT_EXCEPTION' });
  await invalid.stop(); assert.equal(invalid.stats().runtimeCount, 0);
  const partial = NativeEngine.create({ source: '({})', guestSource: '(() => {' });
  t.after(() => partial.stop());
  await assert.rejects(partial.ready, { code: 'SCRIPT_EXCEPTION' });
  await partial.stop(); assert.equal(partial.stats().contextCount, 0);
});

// R1 / S01, S02.10, S03, V09/V10: registration A, not resolver B, owns a live continuation.
for (const contexts of ['single', 'dual']) for (const ending of ['timeout', 'abort']) {
  test(`active await keeps its own ${ending} after another call resolves it (${contexts} Context)`, { timeout }, async t => {
    const controller = new AbortController(), events = [];
    let didRegister, didEnter;
    const registered = new Promise(resolve => { didRegister = resolve; });
    const entered = new Promise(resolve => { didEnter = resolve; });
    const source = `(() => {
      let resume;
      return {
        async A() {
          await new Promise(resolve => { resume = resolve; __meowEmit('registered', null); });
          __meowEmit('entered-spin', null);
          for (;;) {}
        },
        B() { resume(); return 'B-done'; },
      };
    })()`;
    const other = await create(t, { source: '({ ping() { return 91; } })' });
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ A() { return __meowGuest("A", []); }, B() { return __meowGuest("B", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      onEvent(name, value, scope) {
        events.push({ name, code: value?.code, scope });
        if (name === 'registered') didRegister();
        if (name === 'entered-spin') {
          didEnter();
          if (ending === 'abort') controller.abort();
        }
      },
    });
    await withoutFallbackStop(engine, async () => {
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      const rejected = assert.rejects(engine.call('A', [], {
        scope: 1, signal: controller.signal, timeoutMs: ending === 'timeout' ? 250 : 0,
      }), { code });
      await registered;
      assert.equal(await engine.call('B', [], { scope: 2 }), 'B-done');
      await entered;
      assert.equal(await other.call('ping'), 91);
      await rejected;
      assert.deepEqual(events, [
        { name: 'registered', code: undefined, scope: 1 },
        { name: 'entered-spin', code: undefined, scope: 1 },
        { name: 'failure', code, scope: 1 },
      ]);
      const exit = await engine.exited;
      assert.equal(exit.code, code); assert.equal(exit.threadCount, 0);
      assert.equal(exit.runtimeCount, 0); assert.equal(exit.contextCount, 0); assert.equal(exit.stopped, true);
      assert.equal(await other.call('ping'), 91);
    });
  });
}

// R1 increment / S03, V09: successful A retains authority, not an active request lifetime.
for (const contexts of ['single', 'dual']) for (const ending of ['timeout', 'abort']) {
  test(`completed registration uses the awaiting resolver's ${ending} (${contexts} Context)`, { timeout }, async t => {
    const controller = new AbortController(), events = [];
    const source = `(() => {
      let resume, late;
      return {
        A() {
          late = new Promise(resolve => { resume = resolve; }).then(() => {
            __meowEmit('entered-spin', null);
            for (;;) {}
          });
          return 'A-done';
        },
        B() { resume(); return late; },
      };
    })()`;
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ A() { return __meowGuest("A", []); }, B() { return __meowGuest("B", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      onEvent(name, value, scope) {
        events.push({ name, code: value?.code, scope });
        if (name === 'entered-spin' && ending === 'abort') controller.abort();
      },
    });
    await withoutFallbackStop(engine, async () => {
      const completed = engine.call('A', [], { scope: 1 });
      assert.equal(await completed, 'A-done');
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      await assert.rejects(engine.call('B', [], {
        scope: 2, signal: controller.signal, timeoutMs: ending === 'timeout' ? 250 : 0,
      }), { code });
      assert.deepEqual(events, [
        { name: 'entered-spin', code: undefined, scope: 1 },
        { name: 'failure', code, scope: 2 },
      ]);
      const exit = await engine.exited;
      assert.equal(exit.code, code); assert.equal(exit.threadCount, 0);
      assert.equal(exit.runtimeCount, 0); assert.equal(exit.contextCount, 0); assert.equal(exit.stopped, true);
      assert.equal(await completed, 'A-done');
    });
  });
}

for (const contexts of ['single', 'dual']) {
  test(`completed registration retains finite reaction and background capabilities (${contexts} Context)`, { timeout }, async t => {
    const events = [], hosts = [];
    let releaseBackground, didStartBackground, didBackground, didTimer;
    const started = new Promise(resolve => { didStartBackground = resolve; });
    const background = new Promise(resolve => { didBackground = resolve; });
    const timer = new Promise(resolve => { didTimer = resolve; });
    const source = `(() => {
      let resume, late;
      return {
        A() {
          late = new Promise(resolve => { resume = resolve; }).then(async () => {
            const scope = __meowTaskContext();
            const value = await __meowHost('finite', null);
            return { scope, after: __meowTaskContext(), value };
          });
          __meowHost('background', null).then(value => {
            __meowEmit('background-done', { value, scope: __meowTaskContext() });
          });
          setTimeout(() => {
            __meowHost('timer', null).then(value => {
              __meowEmit('timer-done', { value, scope: __meowTaskContext() });
            });
          }, 0);
          return 'A-done';
        },
        async B() {
          resume();
          const old = await late;
          return { old, scope: __meowTaskContext(), current: await __meowHost('current', null) };
        },
      };
    })()`;
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ A() { return __meowGuest("A", []); }, B() { return __meowGuest("B", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      onHostCall(op, _input, scope, { signal }) {
        hosts.push([op, scope]);
        if (op !== 'background') return `${op}-ok`;
        return new Promise(resolve => {
          releaseBackground = resolve;
          signal.addEventListener('abort', () => resolve('aborted'), { once: true });
          didStartBackground();
        });
      },
      onEvent(name, value, scope) {
        events.push({ name, value, scope });
        if (name === 'background-done') didBackground();
        if (name === 'timer-done') didTimer();
      },
    });
    await withoutFallbackStop(engine, async () => {
      assert.equal(await engine.call('A', [], { scope: 1 }), 'A-done');
      await started;
      releaseBackground('background-ok');
      const result = await engine.call('B', [], { scope: 2 });
      assert.equal(result.old.scope, 1); assert.equal(result.old.after, 1);
      assert.equal(result.old.value, 'finite-ok');
      assert.equal(result.scope, 2); assert.equal(result.current, 'current-ok');
      await Promise.all([background, timer]);
      assert.deepEqual(hosts.sort((a, b) => a[0].localeCompare(b[0])), [
        ['background', 1], ['current', 2], ['finite', 1], ['timer', 1],
      ]);
      assert.deepEqual(events.map(event => event.name).sort(), ['background-done', 'timer-done']);
      for (const event of events) {
        assert.equal(event.scope, 1); assert.equal(event.value.scope, 1);
        assert.equal(event.value.value, event.name === 'background-done' ? 'background-ok' : 'timer-ok');
      }
      assert.equal(engine.stats().threadCount, 1); assert.equal(engine.stats().stopped, false);
    });
  });
}

// R2 / S01, S03, V09: the built-in resolver's inherited then lookup can execute user code.
for (const ending of ['timeout', 'abort']) {
  test(`host array completion interrupts its inherited then getter on ${ending}`, { timeout }, async t => {
    const controller = new AbortController(), events = [];
    let release, hostStarted;
    const started = new Promise(resolve => { hostStarted = resolve; });
    const engine = await create(t, {
      source: `({
        wait() {
          Object.defineProperty(Array.prototype, 'then', {
            configurable: true,
            get() {
              __meowEmit('getter-entered', null);
              for (;;) {}
            },
          });
          return __meowHost('array', null);
        },
      })`,
      onHostCall(_op, _input, _scope, { signal }) {
        return new Promise(resolve => {
          release = resolve;
          signal.addEventListener('abort', () => resolve([]), { once: true });
          hostStarted();
        });
      },
      onEvent(name, value, scope) {
        events.push({ name, code: value?.code, scope });
        if (name === 'getter-entered' && ending === 'abort') controller.abort();
      },
    });
    await withoutFallbackStop(engine, async () => {
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      const rejected = assert.rejects(engine.call('wait', [], {
        scope: 3, signal: controller.signal, timeoutMs: ending === 'timeout' ? 250 : 0,
      }), { code });
      await started;
      await engine.stats({ refresh: true }); // Original method has returned before completing I/O.
      release([]);
      await rejected;
      assert.deepEqual(events, [
        { name: 'getter-entered', code: undefined, scope: 3 },
        { name: 'failure', code, scope: 3 },
      ]);
      const exit = await engine.exited;
      assert.equal(exit.code, code); assert.equal(exit.threadCount, 0);
      assert.equal(exit.runtimeCount, 0); assert.equal(exit.stopped, true);
    });
  });
}

test('ordinary array completion cancellation does not report an interrupted owner', { timeout }, async t => {
  const events = [];
  let release, hostStarted;
  const engine = await create(t, {
    source: '({ wait() { return __meowHost("array", null); }, ping() { return 43; } })',
    onHostCall(_op, _input, _scope, { signal }) {
      return new Promise(resolve => {
        release = resolve;
        signal.addEventListener('abort', () => resolve([]), { once: true });
        hostStarted();
      });
    },
    onEvent(name) { events.push(name); },
  });
  await withoutFallbackStop(engine, async () => {
    for (const ordering of ['cancel-first', 'race', 'complete-first']) {
      const controller = new AbortController();
      const started = new Promise(resolve => { hostStarted = resolve; });
      const result = engine.call('wait', [], { scope: 4, signal: controller.signal }).then(
        value => ({ value }), error => ({ code: error.code }),
      );
      await started;
      await engine.stats({ refresh: true });
      if (ordering === 'cancel-first') controller.abort();
      release([]);
      // No native command between the competing completion/abort and observation.
      if (ordering === 'race') await new Promise(resolve => setImmediate(() => { controller.abort(); resolve(); }));
      const outcome = await result;
      if (ordering === 'cancel-first') assert.deepEqual(outcome, { code: 'CANCELLED' });
      else if (ordering === 'race' && outcome.code) assert.deepEqual(outcome, { code: 'CANCELLED' });
      else assert.deepEqual(outcome, { value: [] });
      if (ordering === 'complete-first') controller.abort();
      assert.deepEqual(events, []);
      assert.equal(engine.stats().threadCount, 1); assert.equal(engine.stats().stopped, false);
      assert.equal(await engine.call('ping'), 43);
    }
  });
});

// R3 / S01, S03, V09/V10: cancellation is the only wakeup after the pre-cancel barrier.
test('idle timeout-zero Promise cancellation wakes without later commands and keeps the owner usable', { timeout }, async t => {
  const events = [];
  const engine = await create(t, {
    source: '({ pending() { return new Promise(() => {}); }, ping() { return 57; } })',
    onEvent(name) { events.push(name); },
  });
  await withoutFallbackStop(engine, async () => {
    for (let i = 0; i < 12; i++) {
      const controller = new AbortController();
      const rejected = assert.rejects(engine.call('pending', [], {
        scope: i + 10, signal: controller.signal, timeoutMs: 0,
      }), { code: 'CANCELLED' });
      const before = await engine.stats({ refresh: true }); // Before cancel; never a rescue wakeup.
      assert.equal(before.activeCalls, 1); assert.equal(before.hostCalls, 0);
      assert.equal(before.timers, 0); assert.equal(before.jobsPending, 0);
      if (i % 3 === 1) await Promise.resolve();
      if (i % 3 === 2) await new Promise(resolve => setImmediate(resolve));
      controller.abort();
      await rejected; // No stats/invoke/stop until cancellation has actually been delivered.
      assert.deepEqual(events, []);
      assert.equal(engine.stats().stopped, false);
      assert.equal(await engine.call('ping'), 57);
    }
  });
});

test('retired finite reactions settle across contexts with old identity and fenced capabilities', { timeout }, async t => {
  const events = [];
  const engine = await create(t, {
    source: '({ run(name) { return __meowGuest(name, []); } })',
    guestSource: `(() => {
      let resolve, late;
      return {
        async begin() {
          late = new Promise(r => { resolve = r; }).then(() => {
            try { __meowHost('forbidden', null); return 'unsafe'; }
            catch (error) { return { code: error.code, scope: __meowTaskContext() }; }
          });
          __meowEmit('entered', null);
          return new Promise(() => {});
        },
        async resume() {
          resolve();
          const old = await late;
          return { old, current: await __meowHost('allowed', null), scope: __meowTaskContext() };
        },
      };
    })()`,
    onHostCall(op, _input, scope) { assert.equal(op, 'allowed'); assert.equal(scope, 2); return 'current'; },
    onEvent(name) { events.push(name); },
  });
  const controller = new AbortController();
  const pending = engine.call('run', ['begin'], { scope: 1, signal: controller.signal });
  // A native round trip proves begin has run and its unresolved reaction is retained.
  await engine.stats({ refresh: true });
  controller.abort(); await assert.rejects(pending, { code: 'CANCELLED' });
  const result = await engine.call('run', ['resume'], { scope: 2 });
  assert.equal(result.old.code, 'STALE_SESSION'); assert.equal(result.old.scope, 1);
  assert.equal(result.current, 'current'); assert.equal(result.scope, 2);
  assert.deepEqual(events, ['entered']);
});

test('scope zero background work survives an ordinary asynchronous request cancellation', { timeout }, async t => {
  const events = [];
  const request = new AbortController();
  let didBackground;
  const background = new Promise(resolve => { didBackground = resolve; });
  let finishBackground, hostScope, backgroundAborted = false;
  const engine = await create(t, {
    source: `({
      run() {
        const previous = __meowTaskContext();
        __meowTaskContext(0);
        __meowHost('background', null).then(v => __meowEmit('background', v));
        __meowTaskContext(previous);
        return new Promise(() => {});
      },
      ping() { return __meowTaskContext(); },
      wait() { return __meowHost('request', null); },
    })`,
    onHostCall(op, _input, scope, { signal }) {
      if (op === 'request') { request.abort(); return 'late'; }
      hostScope = scope;
      signal.addEventListener('abort', () => { backgroundAborted = true; }, { once: true });
      return new Promise(resolve => { finishBackground = resolve; });
    },
    onEvent(name) { events.push(name); if (name === 'background') didBackground(); },
  });
  await assert.rejects(engine.call('run', [], { scope: 77, timeoutMs: 30 }), { code: 'TIMEOUT' });
  assert.equal(hostScope, 0); assert.equal(backgroundAborted, false);
  assert.equal(await engine.call('ping', [], { scope: 78 }), 78);
  finishBackground('done');
  await background;
  assert.deepEqual(events, ['background']);
  await assert.rejects(engine.call('wait', [], { scope: 79, signal: request.signal }), { code: 'CANCELLED' });
  assert.equal(await engine.call('ping', [], { scope: 80 }), 80);
  assert.deepEqual(events, ['background']);
});

test('stop awaits actual native exit and physical host settlement', { timeout }, async t => {
  let release, started, aborted = false;
  const began = new Promise(r => { started = r; });
  const engine = await create(t, {
    source: '({ run() { return __meowHost("wait", null); } })',
    onHostCall(_op, _value, _scope, { signal }) {
      signal.addEventListener('abort', () => { aborted = true; }, { once: true });
      started(); return new Promise(r => { release = r; });
    },
  });
  const result = engine.call('run', [], { scope: 1 });
  const rejected = assert.rejects(result, { code: 'STALE_SESSION' });
  await began;
  let stopped = false;
  const closing = engine.stop().then(() => { stopped = true; });
  await rejected;
  assert.equal(engine.stats().threadCount, 0); assert.equal(aborted, true); assert.equal(stopped, false);
  release('late'); await closing; assert.equal(stopped, true);
});

test('native algorithms retain byte/crypto/zlib contracts on the owner thread', { timeout }, async t => {
  const engine = await create(t, { source: '({ util(op, args) { return __meowNative(op, args); } })' });
  const util = (op, args) => engine.call('util', [op, args]);
  const data = Buffer.from('native\0payload😀');
  assert.deepEqual(await util('encode', [data.toString('base64'), 'base64']), data);
  assert.equal(await util('decode', [data, 'utf8']), data.toString());
  assert.equal(await util('decode', [Buffer.from([0xff, 0xc0, 0x61, 0xe2, 0x82]), 'utf8']), '\ufffd\ufffda\ufffd');
  assert.equal(await util('digest', ['sha256', data]), createHash('sha256').update(data).digest('hex'));
  assert.equal(await util('hmac', ['sha256', 'key', data]), createHmac('sha256', 'key').update(data).digest('hex'));
  for (const [op, mode, key, iv, nodeMode] of [
    ['aes', 'aes-128-cbc', Buffer.alloc(16, 7), Buffer.alloc(16, 9), 'aes-128-cbc'],
    ['aes', 'aes-192-ecb', Buffer.alloc(24, 7), null, 'aes-192-ecb'],
    ['aes', 'aes-256-cbc', Buffer.alloc(32, 7), Buffer.alloc(16, 9), 'aes-256-cbc'],
    ['des', 'des-cbc', Buffer.alloc(8, 7), Buffer.alloc(8, 9), 'des-ede3-cbc'],
    ['des', 'des-ecb', Buffer.alloc(8, 7), null, 'des-ede3'],
  ]) {
    const nodeKey = op === 'des' ? Buffer.concat([key, key, key]) : key;
    const cipher = createCipheriv(nodeMode, nodeKey, iv);
    const expected = Buffer.concat([cipher.update(data), cipher.final()]);
    const encrypted = await util(op, [data, mode, key, iv]);
    assert.deepEqual(encrypted, expected);
    assert.deepEqual(await util(`${op}Decrypt`, [encrypted, mode, key, iv]), data);
  }
  const { publicKey } = generateKeyPairSync('rsa', { modulusLength: 1024 });
  const pem = publicKey.export({ type: 'spki', format: 'pem' });
  const padded = Buffer.alloc(128); data.copy(padded, 128 - data.length);
  assert.deepEqual(await util('rsa', [data, pem]),
    publicEncrypt({ key: publicKey, padding: constants.RSA_NO_PADDING }, padded));
  const compressed = await util('deflate', [data]);
  assert.deepEqual(compressed, deflateSync(data));
  assert.deepEqual(await util('inflate', [compressed]), data);
  assert.equal((await util('random', [32])).length, 32);
  await assert.rejects(util('digest', ['invalid', data]), { code: 'UNSUPPORTED_CRYPTO' });
});

test('garbage collection accounts for native buffers without accumulating cycles', { timeout }, async t => {
  const engine = await create(t, { source: `(() => {
    let held;
    return {
      allocate() { held = new Uint8Array(1024 * 1024); const o = { held }; o.self = o; held = o; },
      release() { held = null; },
    };
  })()` });
  const baseline = await engine.stats({ gc: true });
  for (let i = 0; i < 3; i++) {
    await engine.call('allocate');
    assert.ok((await engine.stats({ refresh: true })).byteBufferBytes >= 1024 * 1024);
    await engine.call('release');
    const current = await engine.stats({ gc: true });
    assert.equal(current.byteBufferBytes, baseline.byteBufferBytes);
    assert.equal(current.byteBuffers, baseline.byteBuffers);
  }
});

test('Node environment teardown interrupts and joins its native owners', { timeout }, async () => {
  const module = new URL('../src/native-engine.js', import.meta.url).href;
  const worker = new Worker(`
    const { parentPort } = require('node:worker_threads');
    import(${JSON.stringify(module)}).then(async ({ NativeEngine }) => {
      const engine = NativeEngine.create({ source: '({ run() { for (;;) {} } })' });
      await engine.ready; engine.call('run').catch(() => {});
      parentPort.postMessage('running');
    });
  `, { eval: true });
  await new Promise((resolve, reject) => {
    worker.once('message', resolve); worker.once('error', reject);
  });
  assert.equal(await worker.terminate(), 1);
});

// TSFN teardown / S01, S03: every participant releases once, including closing channels.
test('Node environment teardown joins multiple entered owners without affecting another environment', { timeout }, async t => {
  const other = await create(t, { source: '(() => { let value = 0; return { next() { return ++value; } }; })()' });
  assert.equal(await other.call('next'), 1);
  const module = new URL('../src/native-engine.js', import.meta.url).href;
  const worker = new Worker(`
    const { parentPort } = require('node:worker_threads');
    import(${JSON.stringify(module)}).then(async ({ NativeEngine }) => {
      const engines = Array.from({ length: 3 }, (_, index) => NativeEngine.create({
        source: '({ run() { __meowEmit("entered", null); for (;;) {} } })',
        onEvent(name) { if (name === 'entered') parentPort.postMessage(index); },
      }));
      await Promise.all(engines.map(engine => engine.ready));
      for (const engine of engines) engine.call('run').catch(() => {});
    });
  `, { eval: true });
  t.after(() => worker.terminate());
  await new Promise((resolve, reject) => {
    const entered = new Set();
    worker.on('message', index => {
      entered.add(index);
      if (entered.size === 3) resolve();
    });
    worker.once('error', reject);
    worker.once('exit', code => reject(new Error(`Worker exited before all owners entered: ${code}`)));
  });
  assert.equal(await worker.terminate(), 1);
  assert.equal(await other.call('next'), 2);
  assert.equal(other.stats().stopped, false);
});

// BG01 / S03, M10, V09: an already-running background turn cannot hide a new admission.
for (const contexts of ['single', 'dual']) for (const ending of ['timeout', 'abort']) {
  test(`admitted ${ending} interrupts an entered background timer (${contexts} Context)`, { timeout }, async t => {
    const controller = new AbortController(), events = [];
    let didEnter;
    const entered = new Promise(resolve => { didEnter = resolve; });
    const source = `(() => {
      setTimeout(() => {
        __meowEmit('timer-entered', null);
        for (;;) {}
      }, 10);
      return { ping() { return 'pong'; } };
    })()`;
    const other = await create(t, { source: '(() => { let count = 0; return { ping() { return ++count; } }; })()' });
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ ping() { return __meowGuest("ping", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      onEvent(name, value, scope) {
        events.push({ name, code: value?.code, scope });
        if (name === 'timer-entered') didEnter();
      },
    });
    await withoutFallbackStop(engine, async () => {
      await entered;
      assert.equal(await other.call('ping'), 1);
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      const pending = engine.call('ping', [], {
        scope: 401, signal: controller.signal, timeoutMs: ending === 'timeout' ? 250 : 60_000,
      });
      const rejected = assert.rejects(pending, { code });
      if (ending === 'abort') controller.abort(); // call() has synchronously admitted the request.
      assert.equal(await other.call('ping'), 2);
      await rejected; // No A refresh, later invoke or stop is allowed to rescue cancellation.
      assert.deepEqual(events.filter(event => event.name === 'timer-entered' || event.name === 'failure'), [
        { name: 'timer-entered', code: undefined, scope: 0 },
        { name: 'failure', code, scope: 401 },
      ]);
      const exit = await engine.exited;
      assert.equal(exit.code, code); assert.equal(exit.stopped, true);
      for (const key of ['threadCount', 'runtimeCount', 'contextCount']) assert.equal(exit[key], 0, key);
      assert.equal(await other.call('ping'), 3);
      assert.equal(other.stats().stopped, false);
    });
  });
}

for (const contexts of ['single', 'dual']) {
  test(`finite background timer survives unsent calls and yields before the admitted deadline (${contexts} Context)`, { timeout }, async t => {
    const events = [];
    let didEnter;
    const entered = new Promise(resolve => { didEnter = resolve; });
    const source = `(() => {
      let calls = 0, finishedAt = 0, rounds = 0;
      setTimeout(() => {
        __meowEmit('timer-entered', null);
        const started = Date.now();
        while (Date.now() - started < 500) rounds++;
        finishedAt = Date.now();
        __meowEmit('timer-finished', __meowTaskContext());
      }, 10);
      return { ping() { return { calls: ++calls, finishedAt, rounds, scope: __meowTaskContext() }; } };
    })()`;
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ ping() { return __meowGuest("ping", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      onEvent(name, value, scope) {
        events.push({ name, value, scope });
        if (name === 'timer-entered') didEnter();
      },
    });
    await withoutFallbackStop(engine, async () => {
      await entered;
      const cancelled = new AbortController();
      cancelled.abort();
      await assert.rejects(engine.call('ping', [], {
        scope: 402, signal: cancelled.signal, timeoutMs: 1,
      }), { code: 'CANCELLED' });
      await assert.rejects(engine.call('ping', [], { scope: 403, timeoutMs: -1 }), { code: 'INVALID_ARGUMENT' });
      const admittedAt = Date.now();
      const result = await engine.call('ping', [], { scope: 404, timeoutMs: 2_000 });
      assert.equal(result.calls, 1); assert.equal(result.scope, 404); assert.ok(result.rounds > 0);
      assert.ok(admittedAt < result.finishedAt, 'the accepted call must precede the finite timer finishing');
      assert.deepEqual(events, [
        { name: 'timer-entered', value: null, scope: 0 },
        { name: 'timer-finished', value: 0, scope: 0 },
      ]);
      assert.equal(engine.stats().threadCount, 1); assert.equal(engine.stats().stopped, false);
      assert.equal((await engine.call('ping')).calls, 2);
    });
  });
}

// SL01 / S03: authority expires immediately; runnable cleanup uses the existing owner budget.
for (const contexts of ['single', 'dual']) for (const ending of ['timeout', 'abort']) {
  for (const origin of ['host', 'call', 'interval', 'sync-return']) {
    test(`finite ${origin} cleanup survives ${ending} with immediate capability fences (${contexts} Context)`, { timeout }, async t => {
      const controller = new AbortController(), events = [], hosts = [];
      let didEnter;
      const entered = new Promise(resolve => { didEnter = resolve; });
      const source = `(() => {
        let finished = 0, calls = 0, interval;
        function waitForFence() {
          __meowEmit('entered', null);
          for (;;) {
            try { __meowNative('random', [0]); }
            catch (error) { return error.code; }
          }
        }
        async function tail(first) {
          const fences = [];
          for (const attempt of [
            () => __meowHost('forbidden', null),
            () => __meowNative('random', [0]),
            () => setTimeout(() => {}, 1),
            () => setInterval(() => {}, 1),
          ]) {
            try { attempt(); fences.push('unsafe'); }
            catch (error) { fences.push(error.code); }
          }
          __meowEmit('fenced', { first, fences, scope: __meowTaskContext() });
          for (let i = 0; i < 128; i++) await Promise.resolve();
          clearInterval(interval);
          finished++;
          __meowEmit('tail-finished', { finished, scope: __meowTaskContext() });
          return 'obsolete-result';
        }
        return {
          run(origin) {
            if (origin === 'host') return __meowHost('hold', null).then(
              () => tail('unsafe-completion'), error => tail(error.code));
            if (origin === 'interval') return new Promise(resolve => {
              interval = setInterval(() => { tail(waitForFence()).then(resolve); }, 1);
            });
            if (origin === 'sync-return') {
              const scope = __meowTaskContext();
              __meowTaskContext(0);
              Promise.resolve().then(() => null);
              __meowTaskContext(scope);
            }
            const first = waitForFence();
            if (origin === 'sync-return') {
              Promise.resolve().then(() => tail(first));
              return 'obsolete-sync-result';
            }
            return tail(first);
          },
          async ping() {
            return { finished, calls: ++calls, scope: __meowTaskContext(),
              current: await __meowHost('allowed', null) };
          },
        };
      })()`;
      const other = await create(t, { source: '(() => { let n = 0; return { next() { return ++n; } }; })()' });
      const engine = await create(t, {
        source: contexts === 'single' ? source : `({
          run(origin) { return __meowGuest('run', [origin]); },
          ping() { return __meowGuest('ping', []); },
        })`,
        ...(contexts === 'dual' ? { guestSource: source } : {}),
        cleanupTimeoutMs: 250,
        onHostCall(op, _value, scope, { signal }) {
          hosts.push([op, scope]);
          if (op !== 'hold') return 'current';
          return new Promise(resolve => {
            signal.addEventListener('abort', () => resolve('late-physical-result'), { once: true });
            didEnter();
            if (ending === 'abort') controller.abort();
          });
        },
        onEvent(name, value, scope) {
          events.push({ name, value, scope });
          if (name === 'entered') {
            didEnter();
            if (ending === 'abort') controller.abort();
          }
        },
      });
      await withoutFallbackStop(engine, async () => {
        assert.equal(await other.call('next'), 1);
        const rejected = assert.rejects(engine.call('run', [origin], {
          scope: 501, signal: controller.signal, timeoutMs: ending === 'timeout' ? 100 : 0,
        }), { code: ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED' });
        await entered;
        assert.equal(await other.call('next'), 2);
        await rejected;
        assert.deepEqual(events.filter(event => event.name !== 'entered'), [
          { name: 'fenced', value: { first: 'STALE_SESSION',
            fences: Array(4).fill('STALE_SESSION'), scope: 501 }, scope: 501 },
          { name: 'tail-finished', value: { finished: 1, scope: 501 }, scope: 501 },
        ]);
        assert.deepEqual(await engine.call('ping', [], { scope: 502 }), {
          finished: 1, calls: 1, scope: 502, current: 'current',
        });
        assert.deepEqual(hosts, origin === 'host' ? [['hold', 501], ['allowed', 502]] : [['allowed', 502]]);
        const stats = await engine.stats({ refresh: true });
        for (const key of ['activeCalls', 'hostCalls', 'timers', 'jobsPending']) assert.equal(stats[key], 0, key);
        assert.equal(stats.threadCount, 1); assert.equal(stats.runtimeCount, 1);
        assert.equal(stats.contextCount, contexts === 'single' ? 1 : 2);
        assert.equal(stats.stopped, false);
        assert.equal(await other.call('next'), 3);
        assert.equal(other.stats().stopped, false);
      });
    });
  }
}

for (const contexts of ['single', 'dual']) for (const ending of ['timeout', 'abort']) {
  test(`infinite cancelled tail exits after cleanup and preserves another owner on ${ending} (${contexts} Context)`, { timeout }, async t => {
    const controller = new AbortController(), events = [];
    const cleanupTimeoutMs = 80;
    let didEnter, cancelledAt, failedAt;
    const entered = new Promise(resolve => { didEnter = resolve; });
    const source = `({
      async run() {
        __meowEmit('entered', null);
        let fenced = false;
        for (;;) {
          await Promise.resolve();
          if (!fenced) {
            try { __meowNative('random', [0]); }
            catch (error) { fenced = true; __meowEmit('fenced', error.code); }
          }
        }
      },
    })`;
    const other = await create(t, { source: '(() => { let n = 0; return { next() { return ++n; } }; })()' });
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ run() { return __meowGuest("run", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      cleanupTimeoutMs,
      onEvent(name, value, scope) {
        events.push({ name, value, scope });
        if (name === 'entered') {
          didEnter();
          if (ending === 'abort') { cancelledAt = Date.now(); controller.abort(); }
        }
        if (name === 'failure') failedAt = Date.now();
      },
    });
    await withoutFallbackStop(engine, async () => {
      assert.equal(await other.call('next'), 1);
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      const rejected = assert.rejects(engine.call('run', [], {
        scope: 511, signal: controller.signal, timeoutMs: ending === 'timeout' ? 100 : 0,
      }), { code });
      await entered;
      assert.equal(await other.call('next'), 2);
      await rejected;
      assert.deepEqual(events, [
        { name: 'entered', value: null, scope: 511 },
        { name: 'fenced', value: 'STALE_SESSION', scope: 511 },
        { name: 'failure', value: { code }, scope: 511 },
      ]);
      if (ending === 'abort') assert.ok(failedAt - cancelledAt >= cleanupTimeoutMs - 5,
        'a runnable tail receives the existing cleanup window before owner failure');
      const exit = await engine.exited;
      assert.equal(exit.code, code); assert.equal(exit.stopped, true);
      for (const key of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls',
        'hostCalls', 'timers', 'jobsPending']) assert.equal(exit[key], 0, key);
      assert.equal(await other.call('next'), 3);
      assert.equal(other.stats().stopped, false);
    });
  });
}

for (const contexts of ['single', 'dual']) for (const ending of ['timeout', 'abort']) {
  test(`idle ${ending} returns before cleanup and leaves an old reaction for a later caller (${contexts} Context)`, { timeout }, async t => {
    const controller = new AbortController(), events = [], hosts = [];
    const cleanupTimeoutMs = 500;
    const source = `(() => {
      let resume, late, count = 0;
      return {
        A() {
          late = new Promise(resolve => { resume = resolve; }).then(() => {
            let code;
            try { __meowHost('forbidden', null); code = 'unsafe'; }
            catch (error) { code = error.code; }
            return { code, scope: __meowTaskContext() };
          });
          return new Promise(() => {});
        },
        async B() {
          resume();
          return { old: await late, count: ++count, scope: __meowTaskContext(),
            current: await __meowHost('allowed', null) };
        },
      };
    })()`;
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ A() { return __meowGuest("A", []); }, B() { return __meowGuest("B", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      cleanupTimeoutMs,
      onHostCall(op, _value, scope) { hosts.push([op, scope]); return 'current'; },
      onEvent(name) { events.push(name); },
    });
    await withoutFallbackStop(engine, async () => {
      const rejected = assert.rejects(engine.call('A', [], {
        scope: 521, signal: controller.signal, timeoutMs: ending === 'timeout' ? 100 : 0,
      }), { code: ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED' });
      const before = await engine.stats({ refresh: true });
      assert.equal(before.activeCalls, 1);
      for (const key of ['hostCalls', 'timers', 'jobsPending']) assert.equal(before[key], 0, key);
      const started = Date.now();
      if (ending === 'abort') controller.abort();
      await rejected; // No new command is allowed to wake or rescue the cancelled call.
      assert.ok(Date.now() - started < cleanupTimeoutMs / 2, 'idle cancellation must not wait out cleanup');
      await new Promise(resolve => setTimeout(resolve, cleanupTimeoutMs + 25));
      assert.deepEqual(events, []);
      assert.equal(engine.stats().stopped, false);
      const result = await engine.call('B', [], { scope: 522, timeoutMs: 1_000 });
      assert.deepEqual(result, {
        old: { code: 'STALE_SESSION', scope: 521 }, count: 1, scope: 522, current: 'current',
      });
      assert.deepEqual(hosts, [['allowed', 522]]);
      assert.deepEqual(events, []);
      assert.equal(engine.stats().threadCount, 1);
      assert.equal(engine.stats().contextCount, contexts === 'single' ? 1 : 2);
    });
  });
}

for (const contexts of ['single', 'dual']) for (const ending of ['timeout', 'abort']) {
  test(`later caller controls a retired reaction's infinite execution on ${ending} (${contexts} Context)`, { timeout }, async t => {
    const aController = new AbortController(), bController = new AbortController(), events = [];
    const source = `(() => {
      let resume, late;
      return {
        A() {
          late = new Promise(resolve => { resume = resolve; }).then(() => {
            let code;
            try { __meowHost('forbidden', null); code = 'unsafe'; }
            catch (error) { code = error.code; }
            __meowEmit('old-entered', { code, scope: __meowTaskContext() });
            for (;;) {}
          });
          return new Promise(() => {});
        },
        B() { resume(); return late; },
      };
    })()`;
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ A() { return __meowGuest("A", []); }, B() { return __meowGuest("B", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      cleanupTimeoutMs: 60,
      onEvent(name, value, scope) {
        events.push({ name, value, scope });
        if (name === 'old-entered' && ending === 'abort') bController.abort();
      },
    });
    await withoutFallbackStop(engine, async () => {
      const cancelled = assert.rejects(engine.call('A', [], {
        scope: 531, signal: aController.signal,
      }), { code: 'CANCELLED' });
      await engine.stats({ refresh: true });
      aController.abort();
      await cancelled;
      await new Promise(resolve => setTimeout(resolve, 100));
      assert.deepEqual(events, []);
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      await assert.rejects(engine.call('B', [], {
        scope: 532, signal: bController.signal, timeoutMs: ending === 'timeout' ? 100 : 0,
      }), { code });
      assert.deepEqual(events, [
        { name: 'old-entered', value: { code: 'STALE_SESSION', scope: 531 }, scope: 531 },
        { name: 'failure', value: { code }, scope: 532 },
      ]);
      const exit = await engine.exited;
      assert.equal(exit.code, code); assert.equal(exit.stopped, true);
      for (const key of ['threadCount', 'runtimeCount', 'contextCount']) assert.equal(exit[key], 0, key);
    });
  });
}

for (const contexts of ['single', 'dual']) {
  test(`normal computation can exceed cleanup without acquiring a new deadline (${contexts} Context)`, { timeout }, async t => {
    const events = [];
    const source = `(() => {
      let count = 0;
      return {
        run() {
          const start = Date.now();
          while (Date.now() - start < 120) {}
          return { count: ++count, elapsed: Date.now() - start };
        },
      };
    })()`;
    const engine = await create(t, {
      source: contexts === 'single' ? source : '({ run() { return __meowGuest("run", []); } })',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      cleanupTimeoutMs: 30,
      onEvent(name) { events.push(name); },
    });
    await withoutFallbackStop(engine, async () => {
      const result = await engine.call('run', [], { scope: 541, timeoutMs: 0 });
      assert.equal(result.count, 1); assert.ok(result.elapsed >= 120);
      assert.equal((await engine.call('run', [], { scope: 542, timeoutMs: 2_000 })).count, 2);
      assert.deepEqual(events, []);
      assert.equal(engine.stats().stopped, false);
      assert.equal(engine.stats().threadCount, 1);
    });
  });
}

for (const contexts of ['single', 'dual']) {
  test(`bootstrap finishing inside cleanup cannot publish ready after its deadline (${contexts} Context)`, { timeout }, async t => {
    const source = `(() => {
      const start = Date.now();
      while (Date.now() - start < 120) {}
      return {};
    })()`;
    const engine = NativeEngine.create({
      source: contexts === 'single' ? source : '({})',
      ...(contexts === 'dual' ? { guestSource: source } : {}),
      timeoutMs: 30, cleanupTimeoutMs: 250,
    });
    t.after(() => engine.stop());
    await withoutFallbackStop(engine, async () => {
      await assert.rejects(engine.ready, { code: 'TIMEOUT' });
      const exit = await engine.exited;
      assert.equal(exit.code, 'TIMEOUT'); assert.equal(exit.stopped, true);
      for (const key of ['threadCount', 'runtimeCount', 'contextCount']) assert.equal(exit[key], 0, key);
    });
  });
}
