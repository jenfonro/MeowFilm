import test from 'node:test';
import assert from 'node:assert/strict';
import { createScriptHost } from '../src/index.js';
import { lxScript, target, memoryStorage, deferred, turn } from './fixtures.js';

test('guest constructors, eval and prototype traversal cannot reach Node or the old host invocation object', async t => {
  const host = createScriptHost();
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => {
      const missing = [];
      for (const run of [
        () => Function('return process')(),
        () => globalThis.constructor.constructor('return process')(),
        () => lx.request.constructor('return require')(),
        () => globalThis.__meowInvocation.constructor.constructor('return process')(),
      ]) { try { run(); missing.push(false); } catch { missing.push(true); } }
      return { missing, process:typeof process, require:typeof require, module:typeof module,
        invocation:typeof __meowInvocation, hostBridge:typeof __meowBridge,
        guestEval:eval('21*2') };
    }`,
  }) });
  assert.deepEqual(await host.invoke(target()), {
    missing: [true, true, true, true], process: 'undefined', require: 'undefined',
    module: 'undefined', invocation: 'undefined', hostBridge: 'undefined', guestEval: 42,
  });
});

test('an infinite guest microtask chain retires only its heap and can be explicitly replaced', async t => {
  const host = createScriptHost();
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: 'async ({info}) => { if(info.musicInfo.run) { while(true) await Promise.resolve(); } return 7; }',
  }) });
  await host.install({ installationId: 'b', enabled: true, source: lxScript({ handler: 'async () => 11' }) });
  await assert.rejects(host.invoke(target('a', 'kw', { run: true }), { timeoutMs: 25 }), { code: 'TIMEOUT' });
  assert.equal(host.get('a').state, 'failed');
  assert.equal(await host.invoke(target('b')), 11);
  await host.update('a', { source: lxScript({ handler: 'async () => 7' }) });
  assert.equal(await host.invoke(target()), 7);
});

test('guest heap allocation is bounded and failure does not become a false success', async t => {
  const host = createScriptHost({ limits: { maxMemoryBytes: 2 * 1024 * 1024 } });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: 'async () => new Uint8Array(32 * 1024 * 1024)',
  }) });
  await assert.rejects(host.invoke(target()), { code: 'RESOURCE_LIMIT' });
  assert.equal(host.stats().activeRequests, 0);
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
        return {global:await read(meow.storage),saved:await read(saved),own:await read(storage)};
      }`,
    }) });
  assert.equal(await host.invoke(target('a', 'kw', { save: true })), true);
  assert.deepEqual(await host.invoke(target('a', 'tx')), {
    global: 'PERMISSION_DENIED', saved: 'PERMISSION_DENIED', own: 'tx',
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

test('storage cancellation tracks physical tasks without refusing unrelated work', async t => {
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
  assert(calls.slice(0, 2).every(input => input.signal.aborted));
  assert.equal(host.stats().pendingAdapterTasks, 2);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
  assert.equal(calls.length, 4);
  release.resolve('available');
  await turn();
  assert.equal(host.stats().pendingAdapterTasks, 0);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
});

test('settled storage callbacks release ownership within a long request', async t => {
  const host = createScriptHost({
    limits: { maxTimers: 1 },
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

test('cancelled network tasks stay tracked without blocking fresh requests', async t => {
  const entered = deferred(), release = deferred();
  let calls = 0;
  const host = createScriptHost({ network: () => { calls++; entered.resolve(); return calls === 1 ? release.promise : { statusCode: 200, body: 'available' }; } });
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
  assert.equal(host.stats().networkRequests, 0);
  assert.equal(host.stats().pendingAdapterTasks, 1);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
  assert.equal(calls, 2);
  release.resolve({ statusCode: 200, body: 'available' });
  await turn();
  assert.equal(host.stats().pendingAdapterTasks, 0);
  assert.equal(await host.invoke(target('a', 'tx')), 'available');
  assert.equal(calls, 3);
});

test('rapid guest aborts cannot accumulate unbounded queued network tasks in one execution turn', async t => {
  let calls = 0;
  const host = createScriptHost({ network: async () => { calls++; return { statusCode: 200, body: '' }; } });
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
    Array(5).fill('CANCELLED'));
  assert.equal(calls, 0);
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

test('cleared timers release callback slots and guest microtasks preserve Promise ordering', async t => {
  const host = createScriptHost({ limits: { maxTimers: 1 } });
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
