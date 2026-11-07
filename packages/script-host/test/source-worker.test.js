import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createServer } from 'node:http';
import {
  createNodeNetwork,
  createNodeSourceCapabilities,
  createPortableSourceAdapter,
  createScriptHost,
} from '../src/index.js';
import { createCapabilityClient } from '@meowfilm/source-runtime';
import { createSourceRuntimeModule, sourceRuntimeContract } from '../../../apps/runtime/modules/source-runtime.mjs';
import { moduleHttp } from '../../../tests/runtime-http-fixture.mjs';

function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}

async function within(promise, message, timeoutMs = 10000) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_resolve, reject) => {
        timer = setTimeout(() => reject(new Error(message)), timeoutMs);
      }),
    ]);
  } finally { clearTimeout(timer); }
}

async function waitFor(predicate, message) {
  const deadline = performance.now() + 10000;
  while (!predicate()) {
    assert.ok(performance.now() < deadline, message);
    await new Promise(resolve => setImmediate(resolve));
  }
}

function response(input, body = 'entered') {
  return {
    statusCode: 200,
    statusMessage: 'OK',
    headers: { 'content-type': 'text/plain' },
    body,
    url: input.url,
  };
}

function sourceHost(t, { network = async input => response(input), limits, diagnostics = [] } = {}) {
  const owner = createNodeSourceCapabilities({ network });
  const capabilities = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  const adapter = createPortableSourceAdapter({
    capabilities,
    metadata: { platform: 'test', deviceId: 'fixture-device' },
  });
  const sessions = [];
  const factory = input => {
    const session = adapter(input);
    sessions.push(session);
    return session;
  };
  const host = createScriptHost({
    adapters: { legado: factory, qysg: factory, cat: factory },
    limits,
    onDiagnostic: event => diagnostics.push(event),
  });
  t.after(async () => {
    await host.close();
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().timers, 0);
    assert.equal(host.stats().networkRequests, 0);
    await owner.releaseProfiles('source:');
  });
  return { host, sessions };
}

const source = `
  export default {
    async searchContent(keyword) {
      if (keyword === 'block') {
        await req('https://fixture.invalid/entered');
        while (true) {}
      }
      return { list: [{ vod_id: keyword, vod_name: keyword }] };
    }
  };
`;

// Cat's exported init action is lazy; this request belongs to host initialization.
const startupSource = `
  req('https://fixture.invalid/entered').then(result => {
    if (result.content === 'block') while (true) {}
  });
  ${source}
`;

const spec = (installationId, script = source) => ({
  installationId, protocol: 'cat', source: script, version: '1.0.0', enabled: true,
});
const target = (installationId, keyword = 'normal') => ({
  installationId, siteId: 'source', operation: 'search', payload: { keyword },
});
const result = keyword => ({ list: [{ vod_id: keyword, vod_name: keyword }] });

const awaitingSource = url => `
  let current = 'initial';
  export default {
    async searchContent(keyword) {
      if (keyword === 'A') {
        const reply = await req(${JSON.stringify(url)});
        current = reply.content;
        await local.set('latest', current);
      } else if (keyword === 'B') {
        current = 'B';
        await local.set('latest', current);
      }
      return { current, stored: await local.get('latest') };
    }
  };
`;

test('R4 a pending source HTTP timeout keeps the same owner ready and B usable',
  { timeout: 30000 }, async t => {
    const entered = deferred(), closed = deferred(), requested = deferred();
    const server = createServer((request, response) => {
      response.once('close', () => closed.resolve());
      entered.resolve(request.url);
      // Keep the real response pending until the client's cancellation closes it.
    });
    t.after(() => new Promise(resolve => {
      server.close(resolve);
      server.closeAllConnections();
    }));
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const url = `http://127.0.0.1:${server.address().port}/slow`;
    const network = createNodeNetwork();
    const diagnostics = [];
    const { host, sessions } = sourceHost(t, {
      diagnostics,
      network: input => {
        requested.resolve(input);
        return network(input);
      },
      limits: { initTimeoutMs: 10000, requestTimeoutMs: 60000, cleanupTimeoutMs: 1000 },
    });
    const instance = await host.install(spec('a', awaitingSource(url)));
    const session = sessions[0], engine = session.engine;
    let exited = false;
    engine.exited.then(() => { exited = true; });
    const timedOut = assert.rejects(host.invoke(target('a', 'A'), { timeoutMs: 1000 }),
      { code: 'TIMEOUT' });
    const input = await within(requested.promise, 'A did not enter the network adapter');
    assert.equal(await within(entered.promise, 'A did not reach the HTTP server'), '/slow');
    assert.equal(input.scope.generation, instance.generation);
    await within(timedOut, 'pending HTTP did not time out');
    assert.equal(input.signal.aborted, true);
    assert.equal(input.signal.reason.code, 'TIMEOUT');
    await within(closed.promise, 'timed-out HTTP did not physically close');
    await within(session.whenAdaptersIdle(), 'cancelled HTTP adapter did not settle');
    assert.equal(host.get('a').generation, instance.generation);
    assert.equal(host.get('a').state, 'ready');
    assert.equal(host.get('a').lastFailure, null);
    assert.deepEqual(await host.invoke(target('a', 'B')), { current: 'B', stored: 'B' });
    await session.drain();
    const rules = await engine.call('stats');
    assert.equal(rules.invocations, 0, 'the expired rule invocation must be retired');
    assert.equal(rules.pendingAdapterTasks, 0);
    const native = await engine.stats({ refresh: true });
    assert.equal(native.threadCount, 1);
    assert.equal(native.runtimeCount, 1);
    assert.equal(native.contextCount, 2);
    assert.equal(exited, false);
    assert.equal(sessions.length, 1, 'a pending timeout must not replace the owner');
    assert.equal(session.engine, engine);
    assert.equal(host.stats().activeRequests, 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
    assert.deepEqual(diagnostics.filter(event => event.operation === 'native.unresponsive'), []);
  });

test('R4 a pending source timeout retains physical I/O debt and rejects late results after B',
  { timeout: 30000 }, async t => {
    const entered = deferred(), physical = deferred();
    const diagnostics = [];
    const { host, sessions } = sourceHost(t, {
      diagnostics,
      network: async input => {
        entered.resolve(input);
        await physical.promise;
        return response(input, 'late-A');
      },
      limits: { initTimeoutMs: 10000, requestTimeoutMs: 60000, cleanupTimeoutMs: 1000 },
    });
    const instance = await host.install(spec('a', awaitingSource('https://fixture.invalid/late')));
    const session = sessions[0];
    const timedOut = assert.rejects(host.invoke(target('a', 'A'), { timeoutMs: 1000 }),
      { code: 'TIMEOUT' });
    try {
      const input = await within(entered.promise, 'A did not enter the delayed physical adapter');
      await within(timedOut, 'A timeout waited for physical I/O');
      assert.equal(input.signal.aborted, true);
      assert.equal(input.signal.reason.code, 'TIMEOUT');
      assert.equal(host.get('a').generation, instance.generation);
      assert.equal(host.get('a').state, 'ready');
      assert.equal(host.get('a').lastFailure, null);
      assert.equal(host.stats().activeRequests, 0);
      assert.equal(host.stats().pendingAdapterTasks, 1);
      assert.equal(session.stats().pendingAdapterTasks, 1);
      const retirement = session.adapterRetirement();
      let idle = false;
      retirement.idle.then(() => { idle = true; });
      assert.deepEqual(await host.invoke(target('a', 'B')), { current: 'B', stored: 'B' });
      assert.equal((await session.engine.call('stats')).invocations, 0);
      assert.equal(idle, false, 'logical timeout must not report physical completion');
      assert.equal(retirement.pendingAdapterTasks(), 1);
      physical.resolve();
      await within(retirement.idle, 'physical adapter did not retire after its actual settlement');
      await session.drain();
      assert.equal(retirement.pendingAdapterTasks(), 0);
      assert.equal(session.stats().pendingAdapterTasks, 0);
      assert.equal(host.stats().pendingAdapterTasks, 0);
      assert.deepEqual(await host.invoke(target('a', 'after-late-result')),
        { current: 'B', stored: 'B' }, 'late A must not overwrite B in guest state or storage');
      assert.equal(host.get('a').generation, instance.generation);
      assert.equal(host.get('a').state, 'ready');
      assert.equal(host.get('a').lastFailure, null);
      assert.equal(sessions.length, 1);
      assert.deepEqual(diagnostics.filter(event => event.operation === 'native.unresponsive'), []);
    } finally {
      physical.resolve();
      await within(session.whenAdaptersIdle(), 'released physical adapter did not settle');
    }
  });

test('R4 a stuck source invocation leaves the main thread and B responsive, fails only A and can be enabled',
  { timeout: 30000 }, async t => {
    const entered = deferred();
    const diagnostics = [];
    const { host, sessions } = sourceHost(t, {
      diagnostics,
      network: async input => {
        entered.resolve(input);
        return response(input);
      },
      limits: { initTimeoutMs: 10000, requestTimeoutMs: 60000, cleanupTimeoutMs: 100 },
    });
    const a = await host.install(spec('a'));
    const b = await host.install(spec('b'));
    const engine = sessions.find(value => value.installationId === 'a').engine;
    let settled = false;
    const invocation = host.invoke(target('a', 'block'), { timeoutMs: 2000 });
    invocation.then(() => { settled = true; }, () => { settled = true; });
    const timedOut = assert.rejects(invocation, { code: 'TIMEOUT' });
    const request = await within(entered.promise, 'source invocation did not reach the capability owner');
    assert.equal(request.url, 'https://fixture.invalid/entered');
    assert.equal(request.scope.installationId, 'a');
    assert.equal(request.scope.generation, a.generation);
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(await host.invoke(target('b')), result('normal'));
    assert.equal(settled, false, 'B must answer while A is still stuck');
    await timedOut;
    await waitFor(() => host.get('a').state === 'failed' && engine.stats().threadCount === 0,
      'expired source native thread did not fail and physically exit');
    const exit = await within(engine.exited, 'interrupted source did not report its real exit');
    assert.equal(exit.code, 'TIMEOUT');
    assert.equal(exit.threadCount, 0);
    assert.equal(exit.runtimeCount, 0);
    assert.equal(exit.contextCount, 0);
    assert.equal(host.get('a').lastFailure, 'TIMEOUT');
    assert.equal(host.get('a').generation, a.generation);
    assert.deepEqual(diagnostics.filter(event => event.operation === 'native.unresponsive').map(event => ({
      installationId: event.installationId, code: event.code, phase: event.phase,
    })), [{ installationId: 'a', code: 'TIMEOUT', phase: 'running' }]);
    assert.deepEqual(await host.invoke(target('b', 'after-timeout')), result('after-timeout'));
    assert.equal(host.get('b').generation, b.generation);
    assert.equal(host.get('b').state, 'ready');
    assert.equal(host.get('a').state, 'failed');
    assert.equal(sessions.filter(value => value.installationId === 'a').length, 1);
    const recovered = await host.enable('a');
    assert.equal(recovered.state, 'ready');
    assert.ok(recovered.generation > a.generation);
    assert.equal(recovered.contentHash, a.contentHash);
    assert.deepEqual(await host.invoke(target('a')), result('normal'));
  });

test('R4 a stuck source initialization leaves B responsive and is interrupted and stopped without automatic restart',
  { timeout: 30000 }, async t => {
    const entered = deferred();
    const diagnostics = [];
    let block = true;
    const { host, sessions } = sourceHost(t, {
      diagnostics,
      network: async input => {
        entered.resolve(input);
        return response(input, block ? 'block' : 'normal');
      },
      limits: { initTimeoutMs: 2000, requestTimeoutMs: 60000, cleanupTimeoutMs: 100 },
    });
    const b = await host.install(spec('b'));
    let settled = false;
    const installing = host.install(spec('a', startupSource));
    installing.then(() => { settled = true; }, () => { settled = true; });
    const timedOut = assert.rejects(installing, { code: 'TIMEOUT' });
    await within(entered.promise, 'source initialization did not reach the capability owner');
    const engine = sessions.find(value => value.installationId === 'a').engine;
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(await host.invoke(target('b')), result('normal'));
    assert.equal(settled, false, 'B must answer before A initialization times out');
    await timedOut;
    const failed = host.get('a');
    assert.equal(failed.state, 'failed');
    assert.equal(failed.lastFailure, 'TIMEOUT');
    assert.equal(engine.stats().threadCount, 0);
    assert.deepEqual(diagnostics.filter(event => event.operation === 'native.unresponsive').map(event => ({
      installationId: event.installationId, code: event.code, phase: event.phase,
    })), [{ installationId: 'a', code: 'TIMEOUT', phase: 'initialization' }]);
    assert.deepEqual(await host.invoke(target('b', 'still-live')), result('still-live'));
    assert.equal(host.get('b').generation, b.generation);
    assert.equal(host.get('a').generation, failed.generation);
    assert.equal(host.get('a').state, 'failed');
    assert.equal(sessions.filter(value => value.installationId === 'a').length, 1);
    block = false;
    const recovered = await host.enable('a');
    assert.equal(recovered.state, 'ready');
    assert.ok(recovered.generation > failed.generation);
    assert.deepEqual(await host.invoke(target('a')), result('normal'));
  });

for (const phase of ['initialization', 'invocation']) {
  test(`R4 close cancels stuck source ${phase} and waits for the real native thread exit`,
    { timeout: 30000 }, async t => {
      const entered = deferred();
      const { host, sessions } = sourceHost(t, {
        network: async input => {
          entered.resolve(input);
          return response(input, 'block');
        },
        limits: { initTimeoutMs: 60000, requestTimeoutMs: 60000, cleanupTimeoutMs: 100 },
      });
      let pending;
      if (phase === 'initialization') pending = host.install(spec('a', startupSource));
      else {
        await host.install(spec('a'));
        pending = host.invoke(target('a', 'block'));
      }
      const cancelled = assert.rejects(pending, { code: 'STALE_SESSION' });
      await within(entered.promise, `source ${phase} did not enter its capability`);
      const engine = sessions[0].engine;
      assert.ok(engine && engine.stats().threadCount === 1);
      let exited = false;
      engine.exited.then(() => { exited = true; });
      await new Promise(resolve => setImmediate(resolve));
      const closed = await within(host.close(), 'close waited for the 60s guest deadline', 3000);
      assert.equal(exited, true, 'close must not resolve before the real native thread exit event');
      assert.equal(engine.stats().threadCount, 0);
      await cancelled;
      assert.equal(closed.installations, 0);
      assert.equal(closed.liveSessions, 0);
      assert.equal(closed.activeRequests, 0);
      assert.equal(closed.timers, 0);
      assert.equal(closed.networkRequests, 0);
      assert.equal(closed.pendingAdapterTasks, 0);
    });
}

test('R4/R6 closing a source does not count non-cooperative capability I/O as physically completed',
  { timeout: 30000 }, async t => {
    const entered = deferred();
    const physical = deferred();
    const { host, sessions } = sourceHost(t, {
      network: async input => {
        entered.resolve(input);
        await physical.promise;
        return response(input);
      },
      limits: { initTimeoutMs: 10000, requestTimeoutMs: 60000, cleanupTimeoutMs: 100 },
    });
    await host.install(spec('a'));
    const cancelled = assert.rejects(host.invoke(target('a', 'block')), { code: 'STALE_SESSION' });
    const input = await within(entered.promise, 'physical source network operation did not enter');
    const engine = sessions[0].engine;
    try {
      const closed = await within(host.close(), 'close waited for non-cooperative capability I/O', 3000);
      await cancelled;
      assert.equal(engine.stats().threadCount, 0);
      assert.equal(input.signal.aborted, true);
      assert.equal(closed.cleanupComplete, false);
      assert.ok(closed.pendingAdapterTasks > 0, 'unsettled platform I/O must remain cleanup debt');
      assert.ok(sessions[0].stats().pendingAdapterTasks > 0);
    } finally {
      physical.resolve();
      await within(sessions[0].whenAdaptersIdle(), 'released physical capability did not settle');
    }
    assert.equal(sessions[0].stats().pendingAdapterTasks, 0);
  });

for (const failure of ['false', 'throw']) {
  test(`R4/R6 virtual.unregister returning ${failure} remains observable through native stop, host cleanup and module unload`,
    { timeout: 30000 }, async t => {
      const owner = createNodeSourceCapabilities({ network: async input => response(input) });
      const unregisters = [];
      const sessions = [];
      let host;
      const module = createSourceRuntimeModule({
        call: async () => ({ ok: false, code: 'PERMISSION_DENIED' }),
        platform: 'test',
        network: async input => response(input),
        capabilityOwner: {
          async invoke(name, input, context) {
            if (name === 'virtual.unregister') {
              unregisters.push({ input: structuredClone(input), scope: structuredClone(context.scope) });
              if (failure === 'throw') throw new Error('synthetic virtual.unregister failure');
              return false;
            }
            return owner.invoke(name, input, context);
          },
          releaseProfiles: (prefix, except) => owner.releaseProfiles(prefix, except),
        },
        hostFactory: ({ adapters, ...options }) => {
          host = createScriptHost({
            ...options,
            adapters: {
              ...adapters,
              cat: input => {
                const session = adapters.cat(input);
                sessions.push(session);
                return session;
              },
            },
            limits: { initTimeoutMs: 10000, requestTimeoutMs: 10000, cleanupTimeoutMs: 500 },
          });
          return host;
        },
      });
      t.after(() => module.close());
      const rpc = moduleHttp(t, module);
      const bytes = Buffer.from(`
        export default {
          async playerContent() {
            return {
              parse: 0,
              url: js2Proxy(false, 0, 'fixture', 'https://media.invalid/video', {})
            };
          }
        };
      `);
      const uploaded = await rpc.upload('route-cleanup', 1, bytes, { basename: 'source.js' });
      assert.equal(uploaded.ok, true, JSON.stringify(uploaded));
      assert.equal(uploaded.contract, sourceRuntimeContract);
      const loaded = await rpc('load', {
        sourceRevision: 1,
        installationId: 'route-cleanup',
        revision: 1,
        expectedRevision: 0,
        protocol: 'cat',
        version: '1.0.0',
        metadata: {},
      });
      assert.equal(loaded.ok, true, JSON.stringify(loaded));
      assert.equal(loaded.contract, sourceRuntimeContract);
      const { installationId, revision, generation } = loaded.value.instance;
      const identity = { installationId, revision, generation };
      const played = await rpc('invoke', { ...identity, action: 'play', payload: {} });
      assert.equal(played.ok, true, JSON.stringify(played));
      const url = new URL(played.value.url);
      assert.equal(url.protocol, 'meow-source:');
      const profile = decodeURIComponent(url.hostname);
      const route = decodeURIComponent(url.pathname.slice(1));
      assert.equal(profile, `source:${installationId}:${generation}`);
      assert.equal(owner.resolveVirtual(profile, route).url, 'https://media.invalid/video');
      const engine = sessions[0].engine;
      assert.ok(engine && engine.stats().threadCount === 1);
      const unloaded = await rpc('unload', identity);
      assert.equal(unloaded.ok, false, JSON.stringify(unloaded));
      assert.equal(unloaded.contract, sourceRuntimeContract);
      assert.equal(unloaded.code, 'SOURCE_CLEANUP');
      assert.equal(engine.stats().threadCount, 0, 'a cleanup error must not leave the native thread running');
      assert.equal(unregisters.length, 1);
      assert.equal(unregisters[0].input.profile, profile);
      assert.equal(unregisters[0].input.route, route);
      assert.equal(unregisters[0].scope.installationId, installationId);
      assert.equal(unregisters[0].scope.generation, generation);
      assert.deepEqual(host.stats().cleanupIssues, [{
        installationId, generation, code: 'CAPABILITY_UNAVAILABLE',
      }]);
      const closed = await module.close();
      assert.equal(closed.ok, false);
      assert.equal(closed.code, 'SOURCE_CLEANUP');
    });
}
