import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  createNodeSourceCapabilities,
  createPortableSourceAdapter,
  createScriptHost,
} from '../src/index.js';
import { createCapabilityClient } from '@meowfilm/source-runtime';
import { createSourceRuntimeModule, sourceRuntimeContract } from '../../../apps/runtime/modules/source-runtime.mjs';

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
  let retained;
  export default {
    async searchContent(keyword) {
      if (keyword === 'block') {
        await req('https://fixture.invalid/entered');
        while (true) {}
      }
      if (keyword === 'oom') retained = new Uint8Array(64 * 1024 * 1024);
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
    const worker = sessions.find(value => value.installationId === 'a').worker;
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
    await waitFor(() => host.get('a').state === 'failed' && worker.threadId === -1,
      'expired source worker did not fail and physically exit');
    assert.equal(host.get('a').lastFailure, 'TIMEOUT');
    assert.equal(host.get('a').generation, a.generation);
    assert.deepEqual(diagnostics.filter(event => event.operation === 'worker.unresponsive').map(event => ({
      installationId: event.installationId, code: event.code, phase: event.phase,
    })), [{ installationId: 'a', code: 'TIMEOUT', phase: 'running' }]);
    assert.deepEqual(await host.invoke(target('b', 'after-timeout')), result('after-timeout'));
    assert.equal(host.get('b').generation, b.generation);
    assert.equal(host.get('b').state, 'ready');
    assert.equal(host.get('a').state, 'failed');
    assert.equal(sessions.filter(value => value.installationId === 'a').length, 1);
    assert.equal(diagnostics.filter(event => event.operation === 'memory.restart').length, 0);
    const recovered = await host.enable('a');
    assert.equal(recovered.state, 'ready');
    assert.ok(recovered.generation > a.generation);
    assert.equal(recovered.contentHash, a.contentHash);
    assert.deepEqual(await host.invoke(target('a')), result('normal'));
  });

test('R4 a stuck source initialization leaves B responsive and is probed and terminated without automatic restart',
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
    const worker = sessions.find(value => value.installationId === 'a').worker;
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(await host.invoke(target('b')), result('normal'));
    assert.equal(settled, false, 'B must answer before A initialization times out');
    await timedOut;
    const failed = host.get('a');
    assert.equal(failed.state, 'failed');
    assert.equal(failed.lastFailure, 'TIMEOUT');
    assert.equal(worker.threadId, -1);
    assert.deepEqual(diagnostics.filter(event => event.operation === 'worker.unresponsive').map(event => ({
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
  test(`R4 close cancels stuck source ${phase} and waits for the real worker exit`,
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
      const worker = sessions[0].worker;
      assert.ok(worker && worker.threadId !== -1);
      let exited = false;
      worker.once('exit', () => { exited = true; });
      await new Promise(resolve => setImmediate(resolve));
      const closed = await within(host.close(), 'close waited for the 60s guest deadline', 3000);
      assert.equal(exited, true, 'close must not resolve before the real worker exit event');
      assert.equal(worker.threadId, -1);
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
    const worker = sessions[0].worker;
    try {
      const closed = await within(host.close(), 'close waited for non-cooperative capability I/O', 3000);
      await cancelled;
      assert.equal(worker.threadId, -1);
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

test('R4 a ready source OOM fails only A and restarts the same revision exactly once',
  { timeout: 30000 }, async t => {
    const diagnostics = [];
    const { host, sessions } = sourceHost(t, {
      diagnostics,
      limits: {
        maxMemoryBytes: 16 * 1024 * 1024,
        initTimeoutMs: 10000, requestTimeoutMs: 10000, cleanupTimeoutMs: 100,
      },
    });
    const a = await host.install(spec('a'));
    const b = await host.install(spec('b'));
    assert.deepEqual(await host.invoke(target('a')), result('normal'));
    await assert.rejects(host.invoke(target('a', 'oom')), { code: 'RESOURCE_LIMIT' });
    await waitFor(() => host.get('a').state === 'ready' &&
      host.get('a').generation > a.generation, 'ready source OOM did not restart');
    const restarted = host.get('a');
    assert.equal(restarted.enabled, true);
    assert.equal(restarted.contentHash, a.contentHash);
    assert.equal(restarted.version, a.version);
    assert.equal(restarted.protocol, a.protocol);
    assert.equal(diagnostics.filter(event => event.operation === 'memory.restart' &&
      event.installationId === 'a' && event.code === 'RESOURCE_LIMIT').length, 1);
    assert.deepEqual(await host.invoke(target('a', 'after-restart')), result('after-restart'));
    assert.deepEqual(await host.invoke(target('b')), result('normal'));
    const worker = sessions.find(value => value.installationId === 'a' &&
      value.generation === restarted.generation).worker;
    await assert.rejects(host.invoke(target('a', 'oom')), { code: 'RESOURCE_LIMIT' });
    await waitFor(() => host.get('a').state === 'failed' && worker.threadId === -1,
      'second source OOM did not leave its worker failed and exited');
    assert.deepEqual(await host.invoke(target('b', 'after-second-oom')), result('after-second-oom'));
    assert.equal(host.get('b').state, 'ready');
    assert.equal(host.get('b').generation, b.generation);
    assert.equal(host.get('a').lastFailure, 'RESOURCE_LIMIT');
    assert.equal(host.get('a').generation, restarted.generation);
    assert.equal(host.get('a').state, 'failed');
    assert.equal(diagnostics.filter(event => event.operation === 'memory.restart').length, 1);
    assert.equal(sessions.filter(value => value.installationId === 'a').length, 2);
    await assert.rejects(host.invoke(target('a')), { code: 'DISABLED' });
  });

test('R4 source initialization OOM stays enabled and failed without restarting, exits its worker and leaves B usable',
  { timeout: 30000 }, async t => {
    const diagnostics = [];
    const { host, sessions } = sourceHost(t, {
      diagnostics,
      limits: {
        maxMemoryBytes: 16 * 1024 * 1024,
        initTimeoutMs: 10000, requestTimeoutMs: 10000, cleanupTimeoutMs: 100,
      },
    });
    const b = await host.install(spec('b'));
    const installing = host.install(spec('a', `
      const allocation = new Uint8Array(64 * 1024 * 1024);
      ${source}
    `));
    const failed = assert.rejects(installing, { code: 'RESOURCE_LIMIT' });
    await waitFor(() => sessions.some(value => value.installationId === 'a'),
      'initializing source did not create a session');
    const session = sessions.find(value => value.installationId === 'a');
    await waitFor(() => session.worker !== null || session.exitCode !== null,
      'initializing source did not create a real worker');
    const worker = session.worker;
    await failed;
    assert.equal(host.get('a').enabled, true);
    assert.equal(host.get('a').state, 'failed');
    assert.equal(host.get('a').lastFailure, 'RESOURCE_LIMIT');
    const generation = host.get('a').generation;
    if (worker) assert.equal(worker.threadId, -1);
    else assert.notEqual(session.exitCode, null, 'the real worker exit must have been observed');
    assert.deepEqual(await host.invoke(target('b', 'after-init-oom')), result('after-init-oom'));
    assert.equal(host.get('b').state, 'ready');
    assert.equal(host.get('b').generation, b.generation);
    assert.equal(host.get('a').generation, generation);
    assert.equal(host.get('a').state, 'failed');
    assert.equal(sessions.filter(value => value.installationId === 'a').length, 1);
    assert.equal(diagnostics.filter(event => event.operation === 'memory.restart').length, 0);
    await assert.rejects(host.invoke(target('a')), { code: 'DISABLED' });
  });

for (const failure of ['false', 'throw']) {
  test(`R4/R6 virtual.unregister returning ${failure} remains observable through worker stop, host cleanup and module unload`,
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
      const rpc = (name, params = {}) => module.methods[`source.runtime.${name}`](params);
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
      const begun = await rpc('stage.begin', {
        basename: 'source.js',
        length: bytes.length,
        sha256: createHash('sha256').update(bytes).digest('hex'),
      });
      assert.equal(begun.ok, true, JSON.stringify(begun));
      const chunk = await rpc('stage.chunk', {
        token: begun.token, offset: 0, data: bytes.toString('base64'),
      });
      assert.equal(chunk.ok, true, JSON.stringify(chunk));
      assert.equal(chunk.next, bytes.length);
      const loaded = await rpc('load', {
        token: begun.token,
        installationId: 'route-cleanup',
        revision: 1,
        expectedRevision: 0,
        protocol: 'cat',
        version: '1.0.0',
        metadata: {},
      });
      assert.equal(loaded.ok, true, JSON.stringify(loaded));
      const { installationId, revision, generation } = loaded.instance;
      const identity = { installationId, revision, generation };
      const played = await rpc('invoke', { ...identity, action: 'play', payload: {} });
      assert.equal(played.ok, true, JSON.stringify(played));
      const url = new URL(played.value.url);
      assert.equal(url.protocol, 'meow-source:');
      const profile = decodeURIComponent(url.hostname);
      const route = decodeURIComponent(url.pathname.slice(1));
      assert.equal(profile, `source:${installationId}:${generation}`);
      assert.equal(owner.resolveVirtual(profile, route).url, 'https://media.invalid/video');
      const worker = sessions[0].worker;
      assert.ok(worker && worker.threadId !== -1);
      const unloaded = await rpc('unload', identity);
      assert.equal(unloaded.ok, false, JSON.stringify(unloaded));
      assert.equal(unloaded.contract, sourceRuntimeContract);
      assert.equal(unloaded.code, 'SOURCE_CLEANUP');
      assert.equal(worker.threadId, -1, 'a cleanup error must not leave the worker running');
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
