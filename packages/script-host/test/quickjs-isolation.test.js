import test from 'node:test';
import assert from 'node:assert/strict';
import { createScriptHost } from '../src/index.js';
import { NativeSession } from '../src/native-session.js';
import { NativeEngine } from '../src/native-engine.js';
import { lxScript, target, memoryStorage, deferred, turn } from './fixtures.js';

async function waitFor(predicate, message, timeoutMs = 3000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (predicate()) return;
    if (Date.now() >= deadline) throw new Error(message);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}

test('QuickJS unsigned BigInt widths and coercions match JavaScript before SDK initialization', async () => {
  function inspectIntegers() {
    const widths = [0, 1, 8, 31, 32, 33, 63, 64, 65, 95, 96, 127, 128, 129, 256, 512];
    const values = [0n, 1n, -1n, -2n];
    for (const width of widths) {
      const edge = 1n << BigInt(width);
      values.push(edge - 1n, edge, edge + 1n, -edge - 1n, -edge, -edge + 1n);
    }
    const matrix = widths.map(width => values.map(value => String(BigInt.asUintN(width, value))));
    const order = [], results = [];
    const check = (width, value) => {
      try { results.push(String(BigInt.asUintN(width, value))); }
      catch (error) { results.push(error.name); }
    };
    for (const width of [undefined, null, NaN, -0.5, 0.9, 64.9, '64.9', '', true,
      -1, Infinity, -Infinity, 9007199254740991, 9007199254740992, 1n, Symbol('width')])
      check(width, 1n);
    for (const value of [true, false, ' 0xff ', 'bad bigint', 1, 1.5, null, undefined, Symbol('value')])
      check(64, value);
    check({ [Symbol.toPrimitive](hint) { order.push('bits:' + hint); return 64.8; } },
      { [Symbol.toPrimitive](hint) { order.push('value:' + hint); return -1n; } });
    check({ valueOf() { order.push('invalid-bits'); return -1; } },
      { valueOf() { order.push('unreachable-value'); return 0n; } });
    check({ valueOf() { order.push('throw-bits'); throw new SyntaxError(); } },
      { valueOf() { order.push('unreachable-after-throw'); return 0n; } });
    check(0, { valueOf() { order.push('zero-width-value'); return 1; } });
    check({ valueOf() { order.push('nan-width'); return NaN; } },
      { valueOf() { order.push('nan-value'); return 12n; } });
    const descriptor = Object.getOwnPropertyDescriptor(BigInt, 'asUintN');
    let constructible = false;
    try { Reflect.construct(BigInt.asUintN, [64, 1n]); constructible = true; } catch {}
    return { matrix, order, results, metadata: {
      name: BigInt.asUintN.name, length: BigInt.asUintN.length,
      prototype: Object.hasOwn(BigInt.asUintN, 'prototype'), constructible,
      writable: descriptor.writable, configurable: descriptor.configurable, enumerable: descriptor.enumerable,
    } };
  }
  const engine = NativeEngine.create({
    // The SDK itself computes this value, not a later script invocation.
    source: `(() => {
      const initial = (${inspectIntegers})();
      const convert = BigInt.asUintN;
      return {
        inspect() { return initial; },
        captured() {
          BigInt.asIntN = () => { throw new Error('modified intrinsic'); };
          Math.trunc = () => { throw new Error('modified intrinsic'); };
          globalThis.BigInt = () => { throw new Error('modified intrinsic'); };
          return String(convert(64.8, -1n));
        },
      };
    })()`,
    maxStackBytes: 128 * 1024, timeoutMs: 10000,
  });
  try {
    await engine.ready;
    assert.deepEqual(structuredClone(await engine.call('inspect')), inspectIntegers());
    assert.equal(await engine.call('captured'), String((1n << 64n) - 1n));
  } finally { await engine.stop(); }
});

test('native QuickJS closes both contexts and their one runtime and thread', async () => {
  const engine = NativeEngine.create({ source: '({ value: () => 1 })', guestSource: '({ value: () => 2 })' });
  try {
    await engine.ready;
    const running = await engine.stats({ refresh: true });
    assert.equal(running.runtimeCount, 1);
    assert.equal(running.threadCount, 1);
    assert.equal(running.contextCount, 2);
    assert.equal(await engine.call('value'), 1);
  } finally { await engine.stop(); }
  const stopped = engine.stats();
  assert.equal(stopped.runtimeCount, 0);
  assert.equal(stopped.contextCount, 0);
  assert.equal(stopped.threadCount, 0);
  assert.equal(stopped.stopped, true);
});

test('native typed bytes survive heap growth and snapshot only the supplied view', async () => {
  const engine = NativeEngine.create({
    source: `(() => {
      const host = __meowHost;
      delete globalThis.__meowHost;
      return {
        grow(size) { globalThis.retained = new Uint8Array(size); return size; },
        exchange(value) { return host('bytes', value); },
      };
    })()`,
    onHostCall(operation, value) {
      assert.equal(operation, 'bytes');
      assert.equal(Object.getPrototypeOf(value), Object.prototype);
      assert.deepEqual({ ...value }, { label: '中文🎵\0', bytes: Buffer.from([1, 2, 3, 4]) });
      return value;
    },
  });
  try {
    await engine.ready;
    for (const size of [0, 20 * 1024 * 1024, 40 * 1024 * 1024]) {
      assert.equal(await engine.call('grow', [size]), size);
      const backing = Buffer.from([71, 1, 2, 3, 4, 83]);
      const pending = engine.call('exchange', [{ label: '中文🎵\0', bytes: backing.subarray(1, -1) }]);
      backing.fill(9, 1, 5);
      assert.deepEqual({ ...await pending }, { label: '中文🎵\0', bytes: Buffer.from([1, 2, 3, 4]) });
      assert.equal(backing[0], 71);
      assert.equal(backing[5], 83);
    }
  } finally { await engine.stop(); }
});

for (const failedContext of ['core', 'guest']) {
  test(`native ${failedContext} initialization failure releases its partially created runtime`, async () => {
    const engine = NativeEngine.create({
      source: failedContext === 'core' ? 'throw new Error("synthetic")' : '({value: () => 7})',
      ...(failedContext === 'guest' ? { guestSource: 'throw new Error("synthetic")' } : {}),
    });
    await assert.rejects(engine.ready, { code: 'SCRIPT_EXCEPTION' });
    const stopped = engine.stop();
    assert.equal(engine.stop(), stopped);
    await stopped;
    assert.equal(engine.stats().runtimeCount, 0);
    assert.equal(engine.stats().contextCount, 0);
    assert.equal(engine.stats().threadCount, 0);
  });
}

test('native typed conversion failures release partial input and preserve subsequent calls', async () => {
  const engine = NativeEngine.create({ source: '({ value: (...args) => args })' });
  try {
    await engine.ready;
    const bytes = Buffer.from([0, 255, 128]);
    await assert.rejects(engine.call('value', [bytes, { invalid: () => {} }]), { code: 'INVALID_ARGUMENT' });
    assert.deepEqual(await engine.call('value', [bytes, 'valid']), [bytes, 'valid']);
    const snapshot = await engine.stats({ refresh: true });
    assert.equal(snapshot.activeCalls, 0);
    assert.equal(snapshot.hostCalls, 0);
  } finally { await engine.stop(); }
});

test('native script failure keeps its primary code and stop remains idempotent', async () => {
  const engine = NativeEngine.create({ source: `({ fail() { throw Error('synthetic private text'); } })` });
  await engine.ready;
  await assert.rejects(engine.call('fail'), error => error.code === 'SCRIPT_EXCEPTION' &&
    !error.message.includes('synthetic private text'));
  const stopping = engine.stop();
  assert.equal(engine.stop(), stopping);
  await stopping;
  assert.equal(engine.stats().threadCount, 0);
});

test('adapter retirement waits for tasks registered by settlement continuations', async () => {
  const session = new NativeSession({
    installationId: 'adapter-retirement', generation: 1,
    revision: { source: '', permissions: { storage: [] } },
    limits: {},
  });
  const late = deferred();
  const first = Promise.resolve();
  session.trackAdapter(first);
  first.then(() => Promise.resolve().then(() => session.trackAdapter(late.promise)));
  let idle = false;
  const waiting = session.whenAdaptersIdle().then(() => { idle = true; });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(session.adapterTasks.size, 1);
  assert.equal(idle, false);
  late.resolve();
  await waiting;
  assert.equal(session.adapterTasks.size, 0);
  await session.stop();
});

test('guest constructors, eval and prototype traversal cannot reach Node or private native hooks', async t => {
  const host = createScriptHost();
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => {
      const missing = [];
      for (const run of [
        () => Function('return process')(),
        () => globalThis.constructor.constructor('return process')(),
        () => lx.request.constructor('return require')(),
        () => globalThis.__meowHost.constructor.constructor('return process')(),
      ]) { try { run(); missing.push(false); } catch { missing.push(true); } }
      return { missing, process:typeof process, require:typeof require, module:typeof module,
        taskContext:typeof __meowTaskContext,
        ownTaskContext:Object.hasOwn(globalThis, '__meowTaskContext'),
        dynamicTaskContext:Function('return typeof __meowTaskContext')(),
        native:typeof __meowNative, host:typeof __meowHost, emit:typeof __meowEmit,
        ownHost:Object.hasOwn(globalThis,'__meowHost'),
        dynamicNative:Function('return typeof __meowNative')(),
        guestEval:eval('21*2') };
    }`,
  }) });
  assert.deepEqual(await host.invoke(target()), {
    missing: [true, true, true, true], process: 'undefined', require: 'undefined',
    module: 'undefined', guestEval: 42,
    taskContext: 'undefined', ownTaskContext: false, dynamicTaskContext: 'undefined',
    native: 'undefined', host: 'undefined', emit: 'undefined',
    ownHost: false, dynamicNative: 'undefined',
  });
});

test('an infinite guest microtask chain retires only its heap and can be explicitly replaced', async t => {
  const diagnostics = [];
  const host = createScriptHost({ limits: { cleanupTimeoutMs: 30 },
    onDiagnostic: event => diagnostics.push(event) });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async ({info}) => {
      if (info.musicInfo.run) {
        lx.send('updateAlert', { log: 'microtask-entered' });
        while (true) await Promise.resolve();
      }
      return 7;
    }`,
  }) });
  await host.install({ installationId: 'b', enabled: true, source: lxScript({ handler: 'async () => 11' }) });
  const timedOut = assert.rejects(host.invoke(target('a', 'kw', { run: true }),
    { timeoutMs: 25 }), { code: 'TIMEOUT' });
  await waitFor(() => host.get('a').updateAlert?.log === 'microtask-entered',
    'the guest microtask chain did not enter its loop');
  await timedOut;
  await waitFor(() => host.get('a').state === 'failed', 'timed-out instance did not fail');
  assert.equal(host.get('a').lastFailure, 'TIMEOUT');
  await waitFor(() => diagnostics.some(event =>
    event.installationId === 'a' && event.operation === 'native.unresponsive' &&
    event.code === 'TIMEOUT' && event.phase === 'running'),
  'missing running native.unresponsive diagnostic');
  assert.equal(await host.invoke(target('b')), 11);
  assert.equal((await host.enable('a')).state, 'ready');
  assert.equal(await host.invoke(target()), 7);
});

test('request-bound storage stays scoped even when calls await the same guest promise', async t => {
  const storage = memoryStorage();
  const entered = deferred();
  const host = createScriptHost({ storage: {
    set: input => { entered.resolve(); return storage.adapter.set(input); },
    get: storage.adapter.get,
  } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read', 'write'] },
    source: lxScript({
      setup: 'const shared = new Promise(resolve => setTimeout(resolve,10));',
      handler: `async ({source}, {storage}) => {
        await shared; await storage.set('site',source); return await storage.get('site');
      }`,
    }) });
  const replies = Promise.all([host.invoke(target('a', 'kw')), host.invoke(target('a', 'tx'))]);
  await entered.promise;
  assert.deepEqual(await replies, ['kw', 'tx']);
  assert.deepEqual(storage.calls.map(call => call.siteId).sort(), ['kw', 'kw', 'tx', 'tx']);
});

test('guest JSON and prototype hooks cannot forge the request-bound storage identity', async t => {
  const sites = [];
  const host = createScriptHost({ storage: { get(input) {
    sites.push(input.siteId);
    return input.siteId;
  } } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read'] },
    source: lxScript({
      setup: 'let saved;',
      handler: `async ({info},{storage}) => {
        if(info.musicInfo.save) { saved=storage; return true; }
        const original = JSON.stringify;
        JSON.stringify = (args,replacer) => {
          if(Array.isArray(args) && args[1] === 'get') args[0]=2;
          return original(args,replacer);
        };
        Array.prototype.toJSON = function() {
          if(this[1] === 'get') this[0]=2;
          return this;
        };
        Object.prototype.toJSON = function() { return this; };
        const read = async capability => {
          try { return await capability.get('key'); } catch(error) { return error.code; }
        };
        return {global:'storage' in meow,saved:await read(saved),own:await read(storage)};
      }`,
    }) });
  assert.equal(await host.invoke(target('a', 'kw', { save: true })), true);
  assert.deepEqual(await host.invoke(target('a', 'tx')), {
    global: false, saved: 'PERMISSION_DENIED', own: 'tx',
  });
  assert.deepEqual(sites, ['tx']);
});

test('protocol factories share one manager rather than starting another runner', async t => {
  const host = createScriptHost({ adapters: {
    example: input => ({
      generation: input.generation, installationId: input.installationId, revision: input.revision,
      live: true, capabilities: {}, start: async function() { return this; }, activate() {},
      async invoke(request) { return request.payload; }, stop() { this.live = false; },
      async drainAdapters() {}, async whenAdaptersIdle() {},
      stats() { return { live: this.live, timers: 0, networkRequests: 0, invocations: 0, pendingAdapterTasks: 0 }; },
    }),
  } });
  t.after(() => host.close());
  await host.install({ installationId: 'one', protocol: 'example', source: 'data', enabled: true });
  await host.install({ installationId: 'two', protocol: 'example', source: 'data', enabled: true });
  await host.uninstall('one');
  assert.deepEqual(await host.invoke({ installationId: 'two', siteId: 'site', operation: 'request',
    payload: { value: 3 } }), { value: 3 });
  assert.equal(host.stats().liveSessions, 1);
});

test('host close drains every session even when one stop operation fails', async () => {
  const stopped = [];
  const host = createScriptHost({ adapters: {
    example: input => ({
      generation: input.generation, installationId: input.installationId, revision: input.revision,
      live: true, capabilities: {}, start: async function() { return this; }, activate() {},
      async invoke(request) { return request.payload; },
      stop() {
        this.live = false;
        stopped.push(this.installationId);
        if (this.installationId === 'one') throw new Error('synthetic stop failure');
      },
      async drainAdapters() {}, async whenAdaptersIdle() {},
      stats() {
        return { live: this.live, timers: 0, networkRequests: 0,
          invocations: 0, pendingAdapterTasks: 0 };
      },
    }),
  } });
  await host.install({ installationId: 'one', protocol: 'example', source: 'data', enabled: true });
  await host.install({ installationId: 'two', protocol: 'example', source: 'data', enabled: true });
  const closing = host.close();
  await assert.rejects(closing, { code: 'SCRIPT_EXCEPTION' });
  assert.deepEqual(stopped.sort(), ['one', 'two']);
  assert.equal(host.stats().liveSessions, 0);
  assert.equal(host.stats().installations, 0);
  assert.deepEqual(host.list(), []);
  assert.equal(host.close(), closing);
  await assert.rejects(host.close(), { code: 'SCRIPT_EXCEPTION' });
});

test('failed lifecycle cleanup detaches stopped sessions from installation records', async () => {
  const host = createScriptHost({ adapters: {
    example: input => ({
      generation: input.generation, installationId: input.installationId, revision: input.revision,
      live: true, capabilities: {}, start: async function() { return this; }, activate() {},
      async invoke(request) { return request.payload; },
      stop() { this.live = false; throw new Error('synthetic stop failure'); },
      async drainAdapters() {}, async whenAdaptersIdle() {},
      stats() {
        return { live: this.live, timers: 0, networkRequests: 0,
          invocations: 0, pendingAdapterTasks: 0 };
      },
    }),
  } });
  await host.install({ installationId: 'disabled', protocol: 'example',
    source: 'data', enabled: true });
  await host.install({ installationId: 'removed', protocol: 'example',
    source: 'data', enabled: true });
  await assert.rejects(host.disable('disabled'), { code: 'SCRIPT_EXCEPTION' });
  assert.equal(host.get('disabled').state, 'disabled');
  assert.equal(host.stats().liveSessions, 1);
  await assert.rejects(host.uninstall('removed'), { code: 'SCRIPT_EXCEPTION' });
  assert.throws(() => host.get('removed'), { code: 'NOT_FOUND' });
  assert.equal(host.stats().liveSessions, 0);
  assert.equal((await host.close()).cleanupComplete, true);
});

test('guest diagnostic console methods do not require or expose the host console', async t => {
  const host = createScriptHost();
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => {
      for(const method of ['group','groupCollapsed','groupEnd','clear','table','dir','time','timeEnd','assert','count'])
        console[method]('SYNTHETIC-PRIVATE-VALUE');
      return 1;
    }`,
  }) });
  assert.equal(await host.invoke(target()), 1);
});

test('guest module imports and prototype changes never cross the heap boundary', async t => {
  const host = createScriptHost();
  t.after(() => host.close());
  const source = lxScript({ handler: `async ({info}) => {
    const prior = Object.prototype.privateMarker || null;
    if(info.musicInfo.mutate) Object.prototype.privateMarker = 'guest-only';
    let imported = false;
    try { await import('node:fs/promises'); imported = true; } catch {}
    return { prior, imported, process:typeof process };
  }` });
  for (const installationId of ['a', 'b'])
    await host.install({ installationId, enabled: true, source });
  assert.deepEqual(await host.invoke(target('a', 'kw', { mutate: true })),
    { prior: null, imported: false, process: 'undefined' });
  assert.deepEqual(await host.invoke(target('a')),
    { prior: 'guest-only', imported: false, process: 'undefined' });
  assert.deepEqual(await host.invoke(target('b')),
    { prior: null, imported: false, process: 'undefined' });
  assert.equal(Object.prototype.privateMarker, undefined);
});

test('cancelled storage tasks remain tracked until the adapter settles without a fixed quota', async t => {
  const entered = deferred(), release = deferred(), calls = [];
  const host = createScriptHost({
    storage: { get(input) {
      calls.push(input);
      if (calls.length === 2) entered.resolve();
      return calls.length <= 2 ? release.promise : 'available';
    } },
  });
  t.after(() => { release.resolve('available'); return host.close(); });
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read'] },
    source: lxScript({ handler: `async ({info},{storage}) => {
      if(info.musicInfo.hold) return await Promise.all([storage.get('one'),storage.get('two')]);
      try { return await storage.get('probe'); } catch(error) { return error.code; }
    }` }) });
  const controller = new AbortController();
  const pending = assert.rejects(host.invoke(target('a', 'kw', { hold: true }),
    { signal: controller.signal }), { code: 'CANCELLED' });
  await entered.promise;
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
  controller.abort();
  await pending;
  await waitFor(() => calls.slice(0, 2).every(input => input.signal.aborted),
    'cancelled storage transports did not observe the abort');
  assert(calls.slice(0, 2).every(input => input.signal.aborted));
  assert.equal(host.stats().pendingAdapterTasks, 2);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
  // The new probe is admitted without a fixed in-flight/storage quota.
  assert.equal(calls.length, 4);
  release.resolve('available');
  await turn();
  assert.equal(host.stats().pendingAdapterTasks, 0);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
});

test('settled storage callbacks do not accumulate fixed callback quotas', async t => {
  const host = createScriptHost({
    storage: { get: async () => 'value' },
  });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read'] },
    source: lxScript({ handler: `async (_,{storage}) => {
      for(let i=0;i<20;i++) if(await storage.get('key') !== 'value') return false;
      return true;
    }` }) });
  assert.equal(await host.invoke(target()), true);
  assert.equal(host.stats().pendingAdapterTasks, 0);
});

test('cancelled network tasks remain tracked until the adapter settles without a fixed quota', async t => {
  const entered = deferred(), release = deferred();
  let calls = 0;
  const host = createScriptHost({
    network: () => { calls++; entered.resolve();
      return calls === 1 ? release.promise : { statusCode: 200, body: 'available' }; } });
  t.after(() => { release.resolve({ statusCode: 200, body: 'available' }); return host.close(); });
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => {
      try { return await new Promise(resolve => lx.request('https://fixture.invalid',{},
        (error,_response,body)=>resolve(error ? error.code : body))); }
      catch(error) { return error.code; }
    }`,
  }) });
  const controller = new AbortController();
  const pending = assert.rejects(host.invoke(target(), { signal: controller.signal }), { code: 'CANCELLED' });
  await entered.promise;
  controller.abort(); await pending;
  // Cancellation terminates the request wait; the session-owned adapter task
  // remains tracked until the injected network settles.
  await waitFor(() => host.stats().networkRequests === 1,
    'the live network request did not reach the owner snapshot');
  assert.equal(host.stats().networkRequests, 1);
  assert.equal(host.stats().pendingAdapterTasks, 1);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
  assert.equal(calls, 2);
  release.resolve({ statusCode: 200, body: 'available' });
  await turn();
  assert.equal(host.stats().pendingAdapterTasks, 0);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
  // The settled first task may deliver its session-owned callback, but the
  // callback result does not itself open another injected network request.
  assert.equal(calls, 3);
});

test('rapid guest aborts have no fixed queued-network quota and settle cleanly', async t => {
  let calls = 0;
  const host = createScriptHost({
    network: async () => { calls++; return { statusCode: 200, body: '' }; } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => {
      const codes=[];
      for(let i=0;i<5;i++) {
        try { lx.request('https://fixture.invalid',{},error=>codes.push(error.code))(); }
        catch(error) { codes.push(error.code); }
      }
      return codes;
    }`,
  }) });
  assert.deepEqual(await host.invoke(target()),
    ['CANCELLED', 'CANCELLED', 'CANCELLED', 'CANCELLED', 'CANCELLED']);
  assert.equal(calls, 0, 'immediate aborts must not start injected network I/O');
  await turn();
  assert.equal(host.stats().pendingAdapterTasks, 0);
});

test('bridge diagnostics contain only scoped codes and cannot change guest results', async t => {
  const events = [];
  const host = createScriptHost({ onDiagnostic(event) {
    events.push(event); throw new Error('SYNTHETIC-CONSUMER-FAILURE');
  } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => {
      try { lx.utils.crypto.aesEncrypt('SYNTHETIC-PRIVATE-VALUE','unsupported','key',null); }
      catch(error) { return error.code; }
    }`,
  }) });
  assert.equal(await host.invoke(target()), 'UNSUPPORTED_CRYPTO');
  assert.deepEqual(events, [{
    installationId: 'a', generation: 1, siteId: 'kw', requestId: 1,
    kind: 'ScriptHostError', reason: 'host', operation: 'util', code: 'UNSUPPORTED_CRYPTO',
  }]);
  assert.equal(JSON.stringify(events).includes('SYNTHETIC-PRIVATE-VALUE'), false);
});

test('raw script error observation is opt-in and cannot change the sanitized diagnostic or result', async t => {
  const diagnostics = [], observed = [];
  const host = createScriptHost({
    onDiagnostic: event => diagnostics.push(event),
    onScriptError(event) { observed.push(event); throw Error('SYNTHETIC-CONSUMER-FAILURE'); },
  });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => { throw Error('SYNTHETIC-PRIVATE-ERROR'); }`,
  }) });
  await assert.rejects(host.invoke(target()), { code: 'SCRIPT_EXCEPTION' });
  assert.equal(observed.length, 1);
  assert.equal(observed[0].message, 'SYNTHETIC-PRIVATE-ERROR');
  assert.equal(observed[0].siteId, 'kw');
  assert.equal(observed[0].requestId, 1);
  assert.equal(diagnostics.length, 1);
  assert.equal(diagnostics[0].reason, 'script');
  assert.equal(JSON.stringify(diagnostics).includes('SYNTHETIC-PRIVATE'), false);
});

test('cleared timers release callbacks and guest microtasks preserve Promise ordering', async t => {
  const host = createScriptHost();
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    setup: `
      const order=[];
      for(let i=0;i<20;i++) clearTimeout(setTimeout(()=>{},10000));
      queueMicrotask(()=>order.push('microtask'));
      Promise.resolve().then(()=>order.push('promise'));
    `,
    handler: 'async () => order',
  }) });
  assert.deepEqual(await host.invoke(target()), ['microtask', 'promise']);
  assert.equal(host.stats().timers, 0);
});

test('a guest abort closure remains usable from a later request in the same heap', async t => {
  const reached = deferred(), aborted = deferred();
  const host = createScriptHost({ network: input => new Promise((_resolve, reject) => {
    input.signal.addEventListener('abort', () => { aborted.resolve(); reject(input.signal.reason); }, { once: true });
    reached.resolve();
  }) });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    setup: 'let abort;',
    handler: `async ({info}) => {
      if(info.musicInfo.cancel) { abort(); abort(); return true; }
      return await new Promise(resolve => {
        abort = lx.request('https://fixture.invalid',{},error=>resolve(error.code));
      });
    }`,
  }) });
  const pending = host.invoke(target());
  await reached.promise;
  assert.equal(await host.invoke(target('a', 'tx', { cancel: true })), true);
  assert.equal(await pending, 'CANCELLED');
  await aborted.promise;
  assert.equal(host.stats().networkRequests, 0);
});
