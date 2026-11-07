import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createCatRuntime } from '../src/cat-runtime.js';
import { createCatScriptModule } from '../../../apps/runtime/modules/cat-script.mjs';

function gate() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
}
async function within(promise, ms, label) {
  let timer;
  try {
    return await Promise.race([promise, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(label)), ms);
    })]);
  } finally { clearTimeout(timer); }
}
async function serverFor(t, handler) {
  const sockets = new Set();
  const server = http.createServer(handler);
  server.on('connection', socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const drained = () => Promise.all([...sockets].map(socket => once(socket, 'close')));
  t.after(async () => {
    const closed = drained();
    server.closeAllConnections();
    if (server.listening) await new Promise(resolve => server.close(resolve));
    await closed;
  });
  return { url: `http://127.0.0.1:${server.address().port}`, sockets, drained };
}
async function fixtureFor(t, mode, url, { source: customSource, limits } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'meow-cat-enhance-'));
  let runtime, module;
  const workers = [];
  const observe = worker => {
    let entry = workers.find(value => value.worker === worker);
    if (entry) return entry;
    const nativeWorker = worker.worker;
    assert.ok(nativeWorker && nativeWorker.threadId > 0);
    entry = { worker, nativeWorker, exitSeen: false, exited: once(nativeWorker, 'exit') };
    nativeWorker.once('exit', () => { entry.exitSeen = true; });
    workers.push(entry);
    return entry;
  };
  const close = async () => {
    for (const worker of runtime?.workers ?? []) if (worker.worker) observe(worker);
    let failure;
    try { await (module ?? runtime)?.close(); }
    catch (error) { failure = error; }
    for (const { worker, nativeWorker, exited } of workers) {
      try {
        if (nativeWorker.threadId !== -1) {
          failure ??= new Error('owner close did not retire its Worker');
          await nativeWorker.terminate();
        }
        await within(exited, 1000, 'Worker did not physically exit');
        assert.equal(nativeWorker.threadId, -1);
        assert.equal(worker.worker, null);
        assert.equal(worker.live, false);
      } catch (error) { failure ??= error; }
    }
    if (failure) throw failure;
  };
  t.after(async () => {
    try { await close(); }
    finally {
      if (workers.every(({ nativeWorker }) => nativeWorker.threadId === -1) &&
        ![...(runtime?.workers ?? [])].some(worker => worker.worker)) {
        rmSync(root, { recursive: true, force: true });
      }
    }
    t.diagnostic(JSON.stringify({ mode, cleanup: 'complete',
      workers: workers.map(({ worker, nativeWorker, exitSeen }) => ({
        generation: worker.generation, threadId: nativeWorker.threadId,
        exitSeen, attached: Boolean(worker.worker), live: worker.live,
      })) }));
  });
  const original = { parse: 0, url: url + '/master.m3u8', extra: { label: 'original' } };
  const source = customSource ?? `module.exports = {
    play() { return ${JSON.stringify(original)}; },
    home() { return { value: 'still-usable' }; }
  };`;
  const call = (method, input, signal) =>
    module.methods['cat.script.' + method](input, signal ? { signal } : {});
  const value = reply => {
    if (mode === 'runtime') return reply;
    assert.equal(reply.contract, 'cat.script.v1');
    assert.equal(reply.ok, true, reply.code);
    assert.equal(typeof reply.data, 'string');
    return JSON.parse(reply.data);
  };
  let installed;
  if (mode === 'runtime') {
    runtime = createCatRuntime({ dataRoot: root, limits });
    installed = await runtime.install({ installationId: 'enhance', source });
  } else {
    module = createCatScriptModule({ dataRoot: root, runtimeFactory: options => {
      runtime = createCatRuntime({ ...options, limits });
      return runtime;
    } });
    const bytes = Buffer.from(source);
    const stage = await call('stage.begin', { kind: 'source', length: bytes.length,
      sha256: createHash('sha256').update(bytes).digest('hex') });
    assert.equal(stage.ok, true);
    for (let offset = 0; offset < bytes.length; offset += stage.chunkBytes) {
      const chunk = await call('stage.chunk', { token: stage.token, offset,
        data: bytes.subarray(offset, offset + stage.chunkBytes).toString('base64') });
      assert.equal(chunk.ok, true);
    }
    installed = value(await call('install', { installationId: 'enhance', token: stage.token }));
  }
  assert.equal(installed.state, 'ready');
  assert.equal(installed.enabled, true);
  observe(runtime.getSession('enhance'));
  const invoke = (operation, signal, identity = { generation: installed.generation }) => {
    const target = { installationId: 'enhance', ...identity,
      siteId: 'default', operation };
    return mode === 'runtime'
      ? runtime.invoke({ ...target, payload: {} }, { signal })
      : call('invoke', { ...target, payload: '{}' }, signal);
  };
  const enable = enabled => mode === 'runtime' ? runtime.setEnabled('enhance', enabled)
    : call('enable', { installationId: 'enhance', enabled }).then(value);
  return { original, source, installed, runtime, observe, enable, invoke, value, close };
}

for (const mode of ['runtime', 'module']) {
  test(`Cat ${mode} enhancement cancellation closes the real HTTP request without fallback`,
    { timeout: 30000 }, async t => {
      const entered = gate(), disconnected = gate();
      let held, closedAt, receivedAt, released = false, closedBeforeRelease = false;
      const server = await serverFor(t, (request, response) => {
        held = response;
        receivedAt = performance.now();
        request.socket.once('close', () => {
          closedAt = performance.now();
          closedBeforeRelease = !released;
          disconnected.resolve();
        });
        entered.resolve({ method: request.method, path: request.url });
      });
      const fixture = await fixtureFor(t, mode, server.url);
      const controller = new AbortController();
      let settledAt;
      const pending = fixture.invoke('play', controller.signal).then(
        value => { settledAt = performance.now(); return { ok: true, value }; },
        error => { settledAt = performance.now(); return { ok: false, error }; },
      );
      try {
        assert.deepEqual(await within(entered.promise, 5000, 'enhancement GET did not arrive'),
          { method: 'GET', path: '/master.m3u8' });
        assert.equal(settledAt, undefined);
        const abortedAt = performance.now();
        controller.abort();
        const [outcome] = await within(Promise.all([pending, disconnected.promise]), 1000,
          'cancellation must finish the call and close HTTP before the 3s enhancement timeout');
        assert.ok(performance.now() - receivedAt < 1500, 'did not mistake the 3s deadline for cancellation');
        assert.equal(closedBeforeRelease, true);
        assert.equal(held.writableFinished, false);
        if (mode === 'runtime') {
          assert.equal(outcome.ok, false);
          assert.equal(outcome.error.code, 'CANCELLED');
        } else {
          assert.equal(outcome.ok, true);
          assert.deepEqual(outcome.value, { ok: false, contract: 'cat.script.v1', code: 'CANCELLED' });
        }
        t.diagnostic(JSON.stringify({ mode, cancelledAfterGet: true,
          settledAfterAbortMs: settledAt - abortedAt, socketClosedAfterAbortMs: closedAt - abortedAt,
          closedBeforeRelease, responseFinished: held.writableFinished }));
      } finally {
        // A completed response may keep a connection alive; only the
        // pre-release check above attributes transport closure to cancellation.
        released = true;
        if (held && !held.destroyed && !held.writableEnded) held.end('#EXTM3U\n');
      }
      assert.deepEqual(fixture.value(await fixture.invoke('home')), { value: 'still-usable' });
      await fixture.close();
      await within(server.drained(), 1000, 'HTTP sockets survived runtime close');
      assert.equal(server.sockets.size, 0);
    });

  test(`Cat ${mode} ordinary enhancement failures fall back and subsequent enhancement still succeeds`,
    { timeout: 30000 }, async t => {
      let behavior, entered, disconnected;
      const server = await serverFor(t, (request, response) => {
        entered.resolve({ method: request.method, path: request.url, at: performance.now() });
        request.socket.once('close', disconnected.resolve);
        if (behavior === 'http') { response.writeHead(503); response.end('unavailable'); }
        else if (behavior === 'disconnect') request.socket.destroy();
        else if (behavior === 'success') {
          response.setHeader('content-type', 'application/vnd.apple.mpegurl');
          response.end('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1280x720\nchild.m3u8\n');
        }
        // Timeout mode deliberately never responds; no cancellation is sent.
      });
      const fixture = await fixtureFor(t, mode, server.url);
      const controller = new AbortController();
      for (const failure of ['http', 'disconnect', 'timeout']) {
        behavior = failure; entered = gate(); disconnected = gate();
        const pending = fixture.invoke('play', controller.signal);
        const request = await within(entered.promise, 5000, 'enhancement GET did not arrive');
        assert.equal(request.method, 'GET');
        assert.equal(request.path, '/master.m3u8');
        const result = fixture.value(await within(pending, 6000, 'ordinary enhancement failure did not settle'));
        assert.deepEqual(result, fixture.original);
        assert.equal(controller.signal.aborted, false);
        if (failure === 'timeout') {
          assert.ok(performance.now() - request.at >= 2500, 'did not exercise the enhancement deadline');
          await within(disconnected.promise, 1000, 'timeout did not close HTTP transport');
        }
        t.diagnostic(JSON.stringify({ mode, failure, fallback: true,
          elapsedAfterGetMs: performance.now() - request.at, signalAborted: controller.signal.aborted }));
        assert.deepEqual(fixture.value(await fixture.invoke('home')), { value: 'still-usable' });
      }
      behavior = 'success'; entered = gate(); disconnected = gate();
      const result = fixture.value(await fixture.invoke('play'));
      assert.equal(result.url, fixture.original.url);
      assert.deepEqual(result.extra, fixture.original.extra);
      assert.equal(result._meow.version, 1);
      assert.equal(result._meow.media[0].variants[0].quality, '720P');
      assert.equal(result._meow.media[0].variants[0].url, server.url + '/child.m3u8');
      assert.deepEqual(fixture.value(await fixture.invoke('home')), { value: 'still-usable' });
      await fixture.close();
      await within(server.drained(), 1000, 'HTTP sockets survived runtime close');
      assert.equal(server.sockets.size, 0);
    });
}

for (const [mode, operation] of [
  ['runtime', 'disable'], ['module', 'disable'],
  ['runtime', 'update'], ['runtime', 'restart'], ['runtime', 'uninstall'],
]) {
  test(`Cat ${mode} ${operation} retires an invocation waiting for real enhancement HTTP`,
    { timeout: 30000 }, async t => {
      const entered = gate(), disconnected = gate();
      let held, receivedAt, closedAt, settledAt, released = false, closedBeforeRelease = false;
      let requests = 0;
      const server = await serverFor(t, (request, response) => {
        requests++;
        held = response;
        receivedAt = performance.now();
        request.socket.once('close', () => {
          closedAt = performance.now();
          closedBeforeRelease = !released;
          disconnected.resolve();
        });
        entered.resolve({ method: request.method, path: request.url });
      });
      const fixture = await fixtureFor(t, mode, server.url);
      const old = fixture.observe(fixture.runtime.getSession('enhance'));
      const controller = new AbortController();
      const pending = fixture.invoke('play', controller.signal).then(
        value => { settledAt = performance.now(); return { ok: true, value }; },
        error => { settledAt = performance.now(); return { ok: false, error }; },
      );
      let management;
      try {
        assert.deepEqual(await within(entered.promise, 5000, 'enhancement GET did not arrive'),
          { method: 'GET', path: '/master.m3u8' });
        assert.equal(settledAt, undefined);
        assert.equal(controller.signal.aborted, false);
        assert.equal(held.writableFinished, false);
        const stoppedAt = performance.now();
        assert.ok(stoppedAt - receivedAt < 1000, 'must stop before the enhancement deadline');
        if (operation === 'disable') management = fixture.enable(false);
        else if (operation === 'update') management = fixture.runtime.update({
          installationId: 'enhance',
          source: fixture.source.replace("value: 'still-usable'", "value: 'updated'"),
        });
        else management = fixture.runtime[operation]('enhance');
        // Observe failures immediately, while the transport and invocation settle.
        management.catch(() => {});
        const [outcome] = await within(Promise.all([pending, disconnected.promise]), 1000,
          'retirement must reject the call and close HTTP before the enhancement timeout');
        assert.equal(controller.signal.aborted, false);
        assert.equal(old.worker.lifecycle.signal.reason?.code, 'STALE_SESSION');
        if (mode === 'runtime') {
          assert.equal(outcome.ok, false);
          assert.equal(outcome.error.code, 'STALE_SESSION');
        } else {
          assert.equal(outcome.ok, true);
          assert.deepEqual(outcome.value, { ok: false, contract: 'cat.script.v1', code: 'STALE_SESSION' });
        }
        assert.ok(closedAt - receivedAt < 1500, 'did not mistake the 3s deadline for retirement');
        assert.equal(closedBeforeRelease, true);
        assert.equal(held.writableFinished, false);
        let next = await within(management, 15000, 'public management did not finish');
        assert.equal(old.exitSeen, true);
        assert.equal(old.nativeWorker.threadId, -1);
        assert.equal(old.worker.worker, null);
        assert.equal(old.worker.live, false);
        if (operation === 'disable') {
          assert.equal(next.state, 'disabled');
          assert.equal(next.enabled, false);
          next = await fixture.enable(true);
        } else if (operation === 'uninstall') {
          assert.equal((await fixture.runtime.status()).instances.length, 0);
          next = await fixture.runtime.install({ installationId: 'enhance', source: fixture.source });
        }
        assert.equal(next.state, 'ready');
        assert.equal(next.enabled, true);
        assert.ok(next.generation > fixture.installed.generation);
        const current = fixture.observe(fixture.runtime.getSession('enhance', next.generation));
        assert.notEqual(current.nativeWorker, old.nativeWorker);
        assert.deepEqual(fixture.value(await fixture.invoke('home', controller.signal,
          { generation: next.generation })), { value: operation === 'update' ? 'updated' : 'still-usable' });
        assert.equal(controller.signal.aborted, false);
        assert.equal(requests, 1, 'no enhancement probe or replay');
        t.diagnostic(JSON.stringify({ mode, operation, code: 'STALE_SESSION',
          settledAfterStopMs: settledAt - stoppedAt, socketClosedAfterStopMs: closedAt - stoppedAt,
          closedBeforeRelease, responseFinished: held.writableFinished,
          oldThreadExited: old.exitSeen, nextGeneration: next.generation, requests }));
      } finally {
        released = true;
        if (held && !held.destroyed && !held.writableEnded) held.end('#EXTM3U\n');
        try { await fixture.close(); }
        finally {
          await within(Promise.allSettled([pending, management]), 5000, 'public calls did not drain');
          await within(server.drained(), 1000, 'HTTP sockets survived runtime close');
          assert.equal(server.sockets.size, 0);
        }
      }
    });
}

for (const boundary of ['main result', 'enhancement result']) {
  test(`Cat does not deliver a retired ${boundary} or enhance it in a replacement generation`,
    { timeout: 30000 }, async t => {
      const delivery = gate(), release = gate();
      const requests = [];
      const server = await serverFor(t, (request, response) => {
        requests.push({ method: request.method, path: request.url });
        response.setHeader('content-type', 'application/vnd.apple.mpegurl');
        response.end('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1280x720\nchild.m3u8\n');
      });
      const fixture = await fixtureFor(t, 'runtime', server.url);
      const old = fixture.observe(fixture.runtime.getSession('enhance'));
      const controller = new AbortController();
      const owner = boundary === 'main result' ? fixture.runtime.host : old.worker;
      const method = boundary === 'main result' ? 'invoke' : 'request';
      const original = owner[method];
      // Delay delivery of the actual Worker result, not its execution or value.
      // This isolates the successful-result race after transport cancellation can no longer help.
      const delayed = t.mock.method(owner, method, async function (...args) {
        const result = await original.apply(this, args);
        if (boundary === 'main result' ? args[0].operation === 'play' : args[0] === 'enhanceM3U8') {
          delivery.resolve(result);
          await release.promise;
        }
        return result;
      });
      let settled = false;
      // Omitting generation must still bind the invocation to the original session.
      const pending = fixture.invoke('play', controller.signal, {}).then(
        value => { settled = true; return { ok: true, value }; },
        error => { settled = true; return { ok: false, error }; },
      );
      try {
        const result = await within(delivery.promise, 5000, 'real Worker result did not arrive');
        assert.equal(result.url, fixture.original.url);
        assert.deepEqual(result.extra, fixture.original.extra);
        if (boundary === 'main result') {
          assert.deepEqual(result, fixture.original);
          assert.deepEqual(requests, []);
        } else {
          assert.equal(result._meow.media[0].variants[0].quality, '720P');
          assert.equal(result._meow.media[0].variants[0].url, server.url + '/child.m3u8');
          assert.deepEqual(requests, [{ method: 'GET', path: '/master.m3u8' }]);
        }
        assert.equal(settled, false);
        const next = await fixture.runtime.restart('enhance');
        assert.equal(next.state, 'ready');
        assert.ok(next.generation > fixture.installed.generation);
        const current = fixture.observe(fixture.runtime.getSession('enhance', next.generation));
        assert.notEqual(current.nativeWorker, old.nativeWorker);
        assert.equal(old.exitSeen, true);
        assert.equal(old.nativeWorker.threadId, -1);
        assert.equal(old.worker.worker, null);
        assert.equal(old.worker.live, false);
        assert.equal(settled, false);
        assert.deepEqual(await fixture.invoke('home', controller.signal, { generation: next.generation }),
          { value: 'still-usable' });
        release.resolve();
        const outcome = await within(pending, 1000, 'retired result did not settle');
        assert.equal(outcome.ok, false);
        assert.equal(outcome.error.code, 'STALE_SESSION');
        assert.equal(controller.signal.aborted, false);
        assert.equal(requests.length, boundary === 'main result' ? 0 : 1,
          'retired result must not trigger enhancement in the new generation');
        t.diagnostic(JSON.stringify({ boundary, generationOmitted: true, code: outcome.error.code,
          realWorkerResult: true, requests: requests.length, oldThreadExited: old.exitSeen,
          nextGeneration: next.generation }));
      } finally {
        release.resolve();
        delayed.mock.restore();
        try { await fixture.close(); }
        finally {
          await within(pending, 5000, 'delayed call did not drain');
          await within(server.drained(), 1000, 'HTTP sockets survived runtime close');
          assert.equal(server.sockets.size, 0);
        }
      }
    });
}

const failureSource = String.raw`
  const http = require('http');
  let server;
  module.exports.start = async () => {
    server = http.createServer(async (request, response) => {
      const url = new URL(request.url, 'http://127.0.0.1');
      if (url.pathname === '/full-config' || url.pathname === '/config') {
        response.setHeader('content-type', 'application/json');
        return response.end(JSON.stringify({ video: { sites: [{
          key: 'default', api: '/api/default', actions: ['home', 'resource', 'httpError'],
        }] } }));
      }
      for await (const _ of request) {}
      if (url.pathname === '/api/default/resource') {
        const chunk = Buffer.alloc(1024 * 1024, 97);
        for (let index = 0; index < 17; index++) response.write(chunk);
        return response.end();
      }
      if (url.pathname === '/api/default/httpError') {
        response.statusCode = 503;
        return response.end('unavailable');
      }
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify({ value: 'still-usable' }));
    });
    await new Promise(resolve => server.listen(Number(process.env.PORT), '127.0.0.1', resolve));
  };
  module.exports.stop = () => {
    if (!server?.listening) return;
    server.closeAllConnections();
    return new Promise(resolve => server.close(resolve));
  };
`;

async function expectFailure(mode, promise, code) {
  if (mode === 'runtime') {
    await assert.rejects(promise, { code });
  } else {
    assert.deepEqual(await promise, { ok: false, contract: 'cat.script.v1', code });
  }
}

for (const mode of ['runtime', 'module']) {
  test(`Cat ${mode} main failure from a memory-retired session is stale without caller cancellation`,
    { timeout: 30000 }, async t => {
      const source = String.raw`
        const http = require('http'), fs = require('fs');
        let server;
        const count = () => Number(fs.existsSync('/allocation-count')
          ? fs.readFileSync('/allocation-count', 'utf8') : '0');
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            const path = request.url.split('?')[0];
            const send = value => {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify(value));
            };
            if (path === '/full-config' || path === '/config')
              return send({ video: { sites: [{
                key: 'default', api: '/api/default', actions: ['home', 'allocate'],
              }] } });
            if (path === '/api/default/allocate') {
              const previous = count();
              fs.writeFileSync('/allocation-count', String(previous + 1));
              if (previous === 0) new Uint8Array(512 * 1024 * 1024);
            }
            send({ count: count() });
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
      `;
      // Keep the production default 256 MiB heap; the single allocation must fail in QuickJS.
      const fixture = await fixtureFor(t, mode, 'http://127.0.0.1', { source });
      const old = fixture.observe(fixture.runtime.getSession('enhance'));
      const controller = new AbortController();
      assert.deepEqual(fixture.value(await fixture.invoke('home')), { count: 0 });
      await expectFailure(mode, fixture.invoke('allocate', controller.signal), 'STALE_SESSION');
      assert.equal(controller.signal.aborted, false);
      assert.equal(old.worker.failure?.code, 'RESOURCE_LIMIT');
      assert.equal(old.worker.memoryLimitExceeded, true);
      await within(old.exited, 5000, 'memory-retired Worker did not exit');
      assert.equal(old.exitSeen, true);
      assert.equal(old.nativeWorker.threadId, -1);
      let next;
      const deadline = performance.now() + 10000;
      do {
        next = (await fixture.runtime.status()).instances[0];
        if (next.state === 'ready' && next.generation > fixture.installed.generation) break;
        await new Promise(resolve => setTimeout(resolve, 20));
      } while (performance.now() < deadline);
      assert.equal(next.state, 'ready');
      assert.ok(next.generation > fixture.installed.generation);
      assert.equal(next.sourceHash, fixture.installed.sourceHash);
      const current = fixture.observe(fixture.runtime.getSession('enhance', next.generation));
      assert.notEqual(current.nativeWorker, old.nativeWorker);
      assert.deepEqual(fixture.value(await fixture.invoke('home', undefined,
        { generation: next.generation })), { count: 1 }, 'failed allocation is not replayed');
      t.diagnostic(JSON.stringify({ mode, originalTokenCancelled: controller.signal.aborted,
        originalFailure: old.worker.failure.code, publicFailure: 'STALE_SESSION',
        generation: [fixture.installed.generation, next.generation], oldWorkerExited: old.exitSeen,
        sourceUnchanged: next.sourceHash === fixture.installed.sourceHash, allocationAttempts: 1 }));
    });

  test(`Cat ${mode} preserves ordinary RESOURCE_LIMIT and HTTP errors from a live session`,
    { timeout: 30000 }, async t => {
      const fixture = await fixtureFor(t, mode, 'http://127.0.0.1', {
        source: failureSource, limits: { maxMemoryBytes: 16 * 1024 * 1024 },
      });
      const original = fixture.runtime.getSession('enhance');
      const controller = new AbortController();
      for (const [operation, code] of [['resource', 'RESOURCE_LIMIT'], ['httpError', 'UPSTREAM_HTTP_ERROR']]) {
        await expectFailure(mode, fixture.invoke(operation, controller.signal), code);
        assert.equal(controller.signal.aborted, false);
        assert.equal(original.live, true);
        assert.equal(Boolean(original.memoryLimitExceeded), false);
        assert.equal(fixture.runtime.getSession('enhance'), original);
        assert.deepEqual(fixture.value(await fixture.invoke('home')), { value: 'still-usable' });
      }
    });

  for (const transition of ['restart', 'cancel']) {
    test(`Cat ${mode} checks original ownership when a real main error is delivered after ${transition}`,
      { timeout: 30000 }, async t => {
        const fixture = await fixtureFor(t, mode, 'http://127.0.0.1', { source: failureSource });
        const old = fixture.observe(fixture.runtime.getSession('enhance'));
        const delivered = gate(), release = gate();
        const original = fixture.runtime.host.invoke;
        let failures = 0;
        const delayed = t.mock.method(fixture.runtime.host, 'invoke', async function (...args) {
          try { return await original.apply(this, args); }
          catch (error) {
            if (args[0].operation === 'httpError') {
              failures++;
              delivered.resolve(error);
              await release.promise;
            }
            throw error;
          }
        });
        const controller = new AbortController();
        // No generation is supplied: the public owner must still retain the original session.
        const pending = fixture.invoke('httpError', controller.signal, {});
        pending.catch(() => {});
        try {
          const error = await within(delivered.promise, 5000, 'real HTTP failure did not arrive');
          assert.equal(error.code, 'UPSTREAM_HTTP_ERROR');
          assert.equal(error.statusCode, 503);
          assert.equal(old.worker.live, true);
          if (transition === 'restart') {
            const next = await fixture.runtime.restart('enhance');
            assert.equal(next.state, 'ready');
            assert.ok(next.generation > fixture.installed.generation);
            const current = fixture.observe(fixture.runtime.getSession('enhance', next.generation));
            assert.notEqual(current.nativeWorker, old.nativeWorker);
            assert.equal(old.exitSeen, true);
            assert.equal(old.nativeWorker.threadId, -1);
            assert.equal(controller.signal.aborted, false);
            assert.deepEqual(fixture.value(await fixture.invoke('home', undefined,
              { generation: next.generation })), { value: 'still-usable' });
          } else {
            controller.abort();
            assert.equal(old.worker.live, true);
          }
          release.resolve();
          await expectFailure(mode, within(pending, 1000, 'delayed failure did not settle'),
            transition === 'restart' ? 'STALE_SESSION' : 'CANCELLED');
          assert.equal(failures, 1, 'failure must not trigger a replay');
          t.diagnostic(JSON.stringify({ mode, transition, realFailure: error.code,
            statusCode: error.statusCode, originalTokenCancelled: controller.signal.aborted,
            oldWorkerExited: old.exitSeen, failures }));
        } finally {
          release.resolve();
          await pending.catch(() => {});
          delayed.mock.restore();
        }
      });
  }
}
