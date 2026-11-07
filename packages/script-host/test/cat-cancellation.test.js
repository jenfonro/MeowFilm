import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createCatRuntime } from '../src/cat-runtime.js';
import { createCatScriptModule } from '../../../apps/runtime/modules/cat-script.mjs';
import { moduleHttp } from '../../../tests/runtime-http-fixture.mjs';

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
async function fixtureFor(t, mode, url, { source: customSource } = {}) {
  const root = mkdtempSync(join(tmpdir(), 'meow-cat-enhance-'));
  let runtime, module, call;
  const workers = [];
  const owners = [];
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
  const value = reply => {
    if (mode === 'module') {
      assert.equal(reply.contract, 'cat.script.v2');
      assert.equal(reply.ok, true, reply.code);
      reply = reply.value;
    }
    if (reply && typeof reply === 'object')
      assert.deepEqual(Object.getOwnPropertySymbols(reply), [], 'public values must not expose ownership');
    return reply;
  };
  let installed;
  if (mode === 'runtime') {
    runtime = createCatRuntime({ dataRoot: root });
    installed = await runtime.install({ installationId: 'enhance', source });
  } else {
    module = createCatScriptModule({ dataRoot: root, runtimeFactory: options => {
      runtime = createCatRuntime(options);
      return runtime;
    } });
    call = moduleHttp(t, module);
    const uploaded = value(await call.upload('enhance', 1, Buffer.from(source)));
    assert.equal(uploaded.sourceRevision, 1);
    installed = value(await call('install', { installationId: 'enhance',
      sourceRevision: uploaded.sourceRevision }));
  }
  assert.equal(installed.state, 'ready');
  assert.equal(installed.enabled, true);
  observe(runtime.getSession('enhance'));
  const originalInvoke = runtime.invoke;
  t.mock.method(runtime, 'invoke', function (target, options) {
    const owner = { operation: target.operation, signal: options?.signal };
    const pending = originalInvoke.call(this, target, options);
    // HTTP cancellation may finish the client first. Retain the real owner
    // receipt so a disconnected client cannot hide a late successful result.
    owner.outcome = pending.then(value => ({ ok: true, value }), error => ({ ok: false, error }));
    owners.push(owner);
    return pending;
  });
  const invoke = (operation, signal, identity = { generation: installed.generation }, options = {}) => {
    const target = { installationId: 'enhance', ...identity,
      siteId: 'default', operation, payload: {} };
    return mode === 'runtime'
      ? runtime.invoke(target, { ...options, signal })
      : call('invoke', target, { ...options, signal });
  };
  const enable = enabled => mode === 'runtime' ? runtime.setEnabled('enhance', enabled)
    : call('enable', { installationId: 'enhance', enabled }).then(value);
  const restart = () => mode === 'runtime' ? runtime.restart('enhance')
    : call('restart', { installationId: 'enhance' }).then(value);
  return { original, source, installed, runtime, owners, observe, enable, restart, invoke, value, close };
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
        const owner = fixture.owners.at(-1);
        const [outcome, , receipt] = await within(
          Promise.all([pending, disconnected.promise, owner.outcome]), 1000,
          'cancellation must finish the call and close HTTP before the 3s enhancement timeout');
        assert.equal(receipt.ok, false);
        assert.equal(receipt.error.code, 'CANCELLED');
        assert.equal(owner.signal.aborted, true);
        assert.ok(performance.now() - receivedAt < 1500, 'did not mistake the 3s deadline for cancellation');
        assert.equal(closedBeforeRelease, true);
        assert.equal(held.writableFinished, false);
        if (mode === 'runtime') {
          assert.equal(outcome.ok, false);
          assert.equal(outcome.error.code, 'CANCELLED');
        } else {
          assert.equal(outcome.ok, true);
          assert.deepEqual(outcome.value, { ok: false, contract: 'cat.script.v2', code: 'CANCELLED' });
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
          assert.deepEqual(outcome.value, { ok: false, contract: 'cat.script.v2', code: 'STALE_SESSION' });
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

function aborted(signal) {
  return signal.aborted ? Promise.resolve() : once(signal, 'abort');
}

for (const mode of ['runtime', 'module']) {
  for (const boundary of ['main result', 'enhancement result', 'enhancement fallback']) {
    for (const transition of ['restart', 'cancel', 'close', ...(mode === 'module' ? ['timeout'] : [])]) {
      test(`Cat ${mode} does not deliver a late ${boundary} after ${transition}`,
        { timeout: 30000 }, async t => {
          const delivery = gate(), release = gate();
          const requests = [];
          const server = await serverFor(t, (request, response) => {
            requests.push({ method: request.method, path: request.url });
            if (boundary === 'enhancement fallback') {
              response.writeHead(503);
              response.end('unavailable');
              return;
            }
            response.setHeader('content-type', 'application/vnd.apple.mpegurl');
            response.end('#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1280x720\nchild.m3u8\n');
          });
          const fixture = await fixtureFor(t, mode, server.url);
          const old = fixture.observe(fixture.runtime.getSession('enhance'));
          const controller = new AbortController();
          const host = fixture.runtime.host;
          const originalInvoke = host.invoke, originalRequest = old.worker.request;
          let mainSignal;
          // Only hold delivery after the real Worker has produced its result.
          const delayedMain = t.mock.method(host, 'invoke', async function (target, options) {
            if (target.operation === 'play') mainSignal = options.signal;
            const result = await originalInvoke.call(this, target, options);
            if (target.operation === 'play' && boundary === 'main result') {
              delivery.resolve(result);
              await release.promise;
            }
            return result;
          });
          const delayedEnhancement = t.mock.method(old.worker, 'request', async function (method, params, options) {
            if (method === 'enhanceM3U8')
              assert.equal(options.signal, mainSignal, 'enhancement must receive the main invocation signal');
            const result = await originalRequest.call(this, method, params, options);
            if (method === 'enhanceM3U8' && boundary !== 'main result') {
              delivery.resolve(result);
              await release.promise;
            }
            return result;
          });
          let settled = false;
          // Omitting generation must still bind the invocation to the original session.
          const pending = fixture.invoke('play', controller.signal, {},
            transition === 'timeout' ? { timeout: 1000 } : {}).finally(() => { settled = true; });
          pending.catch(() => {});
          let closing, next;
          try {
            const result = await within(delivery.promise, 5000, 'real Worker result did not arrive');
            const owner = fixture.owners.at(-1);
            assert.equal(mainSignal, owner.signal);
            assert.equal(mainSignal.aborted, false);
            if (mode === 'runtime') assert.equal(mainSignal, controller.signal);
            assert.equal(result.url, fixture.original.url);
            assert.deepEqual(result.extra, fixture.original.extra);
            if (boundary === 'main result') {
              assert.deepEqual(result, fixture.original);
              assert.deepEqual(requests, []);
            } else {
              if (boundary === 'enhancement result') {
                assert.equal(result._meow.media[0].variants[0].quality, '720P');
                assert.equal(result._meow.media[0].variants[0].url, server.url + '/child.m3u8');
              } else assert.deepEqual(result, fixture.original);
              assert.deepEqual(requests, [{ method: 'GET', path: '/master.m3u8' }]);
            }
            assert.equal(settled, false);
            if (transition === 'restart') {
              next = await fixture.restart();
              assert.equal(next.state, 'ready');
              assert.ok(next.generation > fixture.installed.generation);
              const current = fixture.observe(fixture.runtime.getSession('enhance', next.generation));
              assert.notEqual(current.nativeWorker, old.nativeWorker);
              assert.equal(settled, false);
              assert.deepEqual(fixture.value(await fixture.invoke('home', controller.signal,
                { generation: next.generation })), { value: 'still-usable' });
            } else if (transition === 'close') {
              closing = fixture.close();
              closing.catch(() => {});
              await within(old.exited, 5000, 'host close did not retire the original Worker');
              assert.equal(fixture.runtime.closed, true);
            } else {
              if (transition === 'cancel') controller.abort();
              await within(aborted(owner.signal), 1000, 'HTTP interruption did not reach the invocation owner');
              assert.equal(old.worker.live, true);
            }
            if (transition === 'restart' || transition === 'close') {
              assert.equal(old.exitSeen, true);
              assert.equal(old.nativeWorker.threadId, -1);
              assert.equal(old.worker.worker, null);
              assert.equal(old.worker.live, false);
            }
            release.resolve();
            const code = transition === 'restart' ? 'STALE_SESSION'
              : transition === 'close' && mode === 'runtime' ? 'HOST_CLOSED' : 'CANCELLED';
            const receipt = await within(owner.outcome, 1000, 'late result owner did not settle');
            assert.equal(receipt.ok, false);
            assert.equal(receipt.error.code, code);
            await expectFailure(mode, within(pending, 1000, 'late public result did not settle'),
              transition === 'timeout' ? 'TIMEOUT' : code);
            assert.equal(controller.signal.aborted, transition === 'cancel');
            assert.equal(requests.length, boundary === 'main result' ? 0 : 1,
              'an invalidated result must not trigger enhancement or a replay');
            if (transition === 'cancel' || transition === 'timeout') {
              assert.equal(fixture.runtime.getSession('enhance'), old.worker);
              assert.deepEqual(fixture.value(await fixture.invoke('home')), { value: 'still-usable' });
            }
            if (closing) await within(closing, 5000, 'host close did not drain the delayed invocation');
            t.diagnostic(JSON.stringify({ mode, boundary, transition, generationOmitted: true, code,
              realWorkerResult: true, requests: requests.length, oldThreadExited: old.exitSeen,
              nextGeneration: next?.generation, sameSignal: true }));
          } finally {
            release.resolve();
            delayedMain.mock.restore();
            delayedEnhancement.mock.restore();
            try { await fixture.close(); }
            finally {
              await within(Promise.allSettled([pending, closing]), 5000, 'delayed call did not drain');
              await within(server.drained(), 1000, 'HTTP sockets survived runtime close');
              assert.equal(server.sockets.size, 0);
            }
          }
        });
    }
  }
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
          key: 'default', api: '/api/default', actions: ['home', 'invalidJSON', 'httpError'],
        }] } }));
      }
      for await (const _ of request) {}
      if (url.pathname === '/api/default/invalidJSON') {
        response.setHeader('content-type', 'application/json');
        return response.end('{invalid-json');
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
    assert.deepEqual(await promise, { ok: false, contract: 'cat.script.v2', code });
  }
}

function heldInvocationSource(url) {
  return `
    const http = require('http'), fs = require('fs');
    const count = () => Number(fs.existsSync('/invocation-count')
      ? fs.readFileSync('/invocation-count', 'utf8') : '0');
    module.exports = {
      home() { return { count: count() }; },
      hold() {
        fs.writeFileSync('/invocation-count', String(count() + 1));
        return new Promise((resolve, reject) => {
          const request = http.get(${JSON.stringify(url)} + '/entered?count=' + count(),
            { agent: false }, response => {
              response.on('error', reject);
              response.on('end', () => resolve({ count: count() }));
              response.resume();
            });
          request.on('error', reject);
        });
      }
    };
  `;
}

for (const mode of ['runtime', 'module']) {
  test(`Cat ${mode} main failure from a real Worker exit is stale without caller cancellation or replay`,
    { timeout: 30000 }, async t => {
      const entered = gate(), disconnected = gate();
      let requests = 0;
      const server = await serverFor(t, (request, _response) => {
        requests++;
        request.socket.once('close', disconnected.resolve);
        entered.resolve(request.url);
      });
      const fixture = await fixtureFor(t, mode, server.url, { source: heldInvocationSource(server.url) });
      const old = fixture.observe(fixture.runtime.getSession('enhance'));
      const controller = new AbortController();
      assert.deepEqual(fixture.value(await fixture.invoke('home')), { count: 0 });
      const pending = fixture.invoke('hold', controller.signal);
      pending.catch(() => {});
      assert.equal(await within(entered.promise, 5000, 'main invocation was not accepted'),
        '/entered?count=1');
      assert.equal(old.worker.live, true);
      const owner = fixture.owners.at(-1);
      await within(old.nativeWorker.terminate(), 5000, 'real Worker termination did not finish');
      await within(Promise.all([old.exited, disconnected.promise]), 1000,
        'exited Worker left its real HTTP request open');
      await expectFailure(mode, within(pending, 1000, 'retired main invocation did not settle'), 'STALE_SESSION');
      const receipt = await owner.outcome;
      assert.equal(receipt.ok, false);
      assert.equal(receipt.error.code, 'STALE_SESSION');
      assert.equal(controller.signal.aborted, false);
      assert.equal(old.worker.failure?.code, 'SCRIPT_EXCEPTION');
      assert.equal(old.exitSeen, true);
      assert.equal(old.nativeWorker.threadId, -1);
      assert.equal(old.worker.worker, null);
      assert.equal(old.worker.live, false);
      const failed = (await fixture.runtime.status()).instances[0];
      assert.equal(failed.state, 'failed');
      assert.equal(failed.error, 'SCRIPT_EXCEPTION');
      assert.equal(failed.enabled, true);
      assert.equal(failed.generation, fixture.installed.generation);
      assert.equal(fixture.runtime.sessions.has('enhance'), false);
      // Recovery is an explicit management operation after physical exit.
      const next = await fixture.restart();
      assert.equal(next.state, 'ready');
      assert.ok(next.generation > fixture.installed.generation);
      assert.equal(next.sourceHash, fixture.installed.sourceHash);
      const current = fixture.observe(fixture.runtime.getSession('enhance', next.generation));
      assert.notEqual(current.nativeWorker, old.nativeWorker);
      assert.deepEqual(fixture.value(await fixture.invoke('home', undefined,
        { generation: next.generation })), { count: 1 }, 'the interrupted invocation is not replayed');
      assert.equal(requests, 1);
      t.diagnostic(JSON.stringify({ mode, callerSignalAborted: controller.signal.aborted,
        originalFailure: old.worker.failure.code, publicFailure: 'STALE_SESSION',
        generation: [fixture.installed.generation, next.generation], oldWorkerExited: old.exitSeen,
        sourceUnchanged: next.sourceHash === fixture.installed.sourceHash, explicitRestart: true, requests }));
    });

  test(`Cat ${mode} preserves ordinary JSON and HTTP errors from a live session`,
    { timeout: 30000 }, async t => {
      const fixture = await fixtureFor(t, mode, 'http://127.0.0.1', {
        source: failureSource,
      });
      const original = fixture.runtime.getSession('enhance');
      const controller = new AbortController();
      for (const [operation, code] of [['invalidJSON', 'INVALID_JSON'], ['httpError', 'UPSTREAM_HTTP_ERROR']]) {
        await expectFailure(mode, fixture.invoke(operation, controller.signal), code);
        assert.equal(controller.signal.aborted, false);
        assert.equal(original.live, true);
        assert.equal(original.failure, null);
        assert.equal(original.lifecycle.signal.aborted, false);
        assert.equal(fixture.runtime.getSession('enhance'), original);
        assert.deepEqual(fixture.value(await fixture.invoke('home')), { value: 'still-usable' });
      }
    });

  for (const transition of ['restart', 'cancel', 'close', ...(mode === 'module' ? ['timeout'] : [])]) {
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
        const pending = fixture.invoke('httpError', controller.signal, {},
          transition === 'timeout' ? { timeout: 1000 } : {});
        pending.catch(() => {});
        let closing;
        try {
          const error = await within(delivered.promise, 5000, 'real HTTP failure did not arrive');
          const owner = fixture.owners.at(-1);
          assert.equal(error.code, 'UPSTREAM_HTTP_ERROR');
          assert.equal(error.statusCode, 503);
          assert.equal(old.worker.live, true);
          assert.equal(owner.signal.aborted, false);
          if (transition === 'restart') {
            const next = await fixture.restart();
            assert.equal(next.state, 'ready');
            assert.ok(next.generation > fixture.installed.generation);
            const current = fixture.observe(fixture.runtime.getSession('enhance', next.generation));
            assert.notEqual(current.nativeWorker, old.nativeWorker);
            assert.equal(old.exitSeen, true);
            assert.equal(old.nativeWorker.threadId, -1);
            assert.equal(controller.signal.aborted, false);
            assert.deepEqual(fixture.value(await fixture.invoke('home', undefined,
              { generation: next.generation })), { value: 'still-usable' });
          } else if (transition === 'close') {
            closing = fixture.close();
            closing.catch(() => {});
            await within(old.exited, 5000, 'host close did not retire the original Worker');
            assert.equal(fixture.runtime.closed, true);
            assert.equal(old.nativeWorker.threadId, -1);
            assert.equal(old.worker.worker, null);
            assert.equal(old.worker.live, false);
          } else {
            if (transition === 'cancel') controller.abort();
            await within(aborted(owner.signal), 1000, 'HTTP interruption did not reach the invocation owner');
            assert.equal(old.worker.live, true);
          }
          release.resolve();
          const code = transition === 'restart' ? 'STALE_SESSION'
            : transition === 'close' && mode === 'runtime' ? 'HOST_CLOSED' : 'CANCELLED';
          const receipt = await within(owner.outcome, 1000, 'late failure owner did not settle');
          assert.equal(receipt.ok, false);
          assert.equal(receipt.error.code, code);
          await expectFailure(mode, within(pending, 1000, 'delayed failure did not settle'),
            transition === 'timeout' ? 'TIMEOUT' : code);
          assert.equal(controller.signal.aborted, transition === 'cancel');
          if (transition === 'cancel' || transition === 'timeout') {
            assert.equal(fixture.runtime.getSession('enhance'), old.worker);
            assert.deepEqual(fixture.value(await fixture.invoke('home')), { value: 'still-usable' });
          }
          if (closing) await within(closing, 5000, 'host close did not drain the delayed failure');
          assert.equal(failures, 1, 'failure must not trigger a replay');
          t.diagnostic(JSON.stringify({ mode, transition, realFailure: error.code,
            statusCode: error.statusCode, callerSignalAborted: controller.signal.aborted,
            oldWorkerExited: old.exitSeen, failures }));
        } finally {
          release.resolve();
          await within(Promise.allSettled([pending, closing]), 5000, 'delayed failure did not drain');
          delayed.mock.restore();
        }
      });
  }
}

test('Cat runtime main timeout closes accepted HTTP work without retiring a responsive Worker',
  { timeout: 30000 }, async t => {
    const entered = gate(), disconnected = gate();
    const server = await serverFor(t, (request, _response) => {
      request.socket.once('close', disconnected.resolve);
      entered.resolve(request.url);
    });
    const fixture = await fixtureFor(t, 'runtime', server.url, { source: heldInvocationSource(server.url) });
    const original = fixture.runtime.getSession('enhance');
    const controller = new AbortController();
    const pending = fixture.invoke('hold', controller.signal, {}, { timeoutMs: 1000 });
    pending.catch(() => {});
    assert.equal(await within(entered.promise, 5000, 'timed invocation was not accepted'), '/entered?count=1');
    await assert.rejects(within(pending, 1000, 'main deadline did not settle'), { code: 'TIMEOUT' });
    await within(disconnected.promise, 1000, 'main deadline did not close HTTP transport');
    assert.equal(controller.signal.aborted, false);
    assert.equal(fixture.runtime.getSession('enhance'), original);
    assert.equal(original.live, true);
    assert.equal(original.failure, null);
    assert.equal(original.lifecycle.signal.aborted, false);
    assert.deepEqual(fixture.value(await fixture.invoke('home')), { count: 1 });
  });
