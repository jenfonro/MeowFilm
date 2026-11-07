import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import { once } from 'node:events';
import { createScriptHost, createCatRuntime } from '../src/index.js';
import { NativeSession } from '../src/native-session.js';
import { lxScript, target, deferred } from './fixtures.js';
import { serverFor } from './http-fixture.js';
import { existsSync, mkdtempSync, readFileSync, rmSync, watch } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';

async function waitFor(predicate, message, timeoutMs = 3000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (predicate()) return;
    if (Date.now() >= deadline) throw new Error(message);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}

function assertUnder1s(started, label) {
  const elapsed = performance.now() - started;
  assert.ok(elapsed < 1000, `${label} took ${elapsed}ms`);
  return elapsed;
}

function requestURL(url, options = {}) {
  return new Promise((resolve, reject) => {
    const request = http.request(url, { agent: false, ...options }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(Buffer.from(chunk)));
      response.once('error', reject);
      response.once('end', () => resolve({
        status: response.statusCode,
        bytes: Buffer.concat(chunks),
        text: Buffer.concat(chunks).toString(),
      }));
    });
    request.once('error', reject);
    request.end(options.body);
  });
}

function hostCapturingNativeSessions() {
  const sessions = new Map();
  return {
    sessions,
    factory(input) {
      const session = new NativeSession(input);
      sessions.set(input.installationId, session);
      return session;
    },
  };
}

function lxAlertEntered(owners, installationId) {
  return () => owners.sessions.get(installationId)?.alert?.log;
}

function lxSourceWithAlert({ alert, handler = 'async () => "ok"', setup = '' }) {
  return lxScript({
    setup: `let signalled = false;\nconst signal = () => {\n  if (!signalled) {\n    signalled = true;\n    lx.send(lx.EVENT_NAMES.updateAlert, { log: ${JSON.stringify(alert)} });\n  }\n};\n${setup}`,
    handler,
  });
}

function lxInitStuckSource(alert) {
  return lxSourceWithAlert({
    alert,
    setup: `signal();\nwhile (true) {}`,
    handler: 'async () => "ok"',
  });
}

function lxInvokeStuckSource(alert) {
  return lxSourceWithAlert({
    alert,
    handler: `async ({info}) => {
      if (info.musicInfo.stuck) {
        signal();
        while (true) {}
      }
      return 'ok';
    }`,
  });
}

function lxTimerStuckSource(alert) {
  return lxSourceWithAlert({
    alert,
    handler: `async ({info}) => {
      if (info.musicInfo.arm) {
        setTimeout(() => {
          signal();
          while (true) {}
        }, 10);
        return 'armed';
      }
      return 'ok';
    }`,
  });
}

function catSource({ route = 'return { ok: true };',
  beforeBody = '', volumeFile = null, volumeText = null,
  sites = [{ key: 'site', name: 'Cat', api: '/api', actions: ['home', 'play'] }] }) {
  const write = volumeFile ? `fs.writeFileSync(${JSON.stringify('/' + volumeFile)}, ${JSON.stringify(volumeText)});\n` : '';
  return String.raw`
    const http = require('http');
    const fs = require('fs');
    let server;
    module.exports.start = () => new Promise(resolve => {
      ${write}
      server = http.createServer(async (request, response) => {
        if (request.url === '/config' || request.url === '/full-config') {
          response.end(JSON.stringify({ video: { sites: ${JSON.stringify(sites)} } }));
          return;
        }
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        const input = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : {};
        ${beforeBody}
        const reply = (() => {
          ${route}
        })();
        if (reply !== undefined && !response.writableEnded)
          response.end(JSON.stringify(reply));
      });
      server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
    });
    module.exports.stop = () => {
      server?.closeAllConnections?.();
      server?.close();
    };
  `;
}

function healthyCatSource({ volumeFile = null, volumeText = null } = {}) {
  return catSource({
    volumeFile,
    volumeText,
    route: `if (request.url === '/api/play') return { parse: 0,
      url: 'http://127.0.0.1:' + server.address().port + '/proxy/media.bin' };
    if (request.url === '/api/home') {
      return { from: 'healthy' };
    }
    if (request.url === '/proxy/media.bin') {
      response.end('proxy-ok');
      return;
    }
    return { ok: true };`,
  });
}

function catResponses(networkEvents, installationId, from = 0) {
  return networkEvents.slice(from).filter(event => event.phase === 'response' &&
    event.scope?.installationId === installationId).length;
}

function catEntered(networkEvents, installationId, from = 0, minimum = 1) {
  // The response is observed by the host adapter immediately before the guest
  // callback resumes and synchronously enters `while (true)`.
  return () => catResponses(networkEvents, installationId, from) >= minimum;
}

function trackSettlement(promise) {
  const state = { settled: false };
  promise.then(() => { state.settled = true; }, () => { state.settled = true; });
  return state;
}

function stuckCatInitSource(signalOrigin) {
  return String.raw`
    const http = require('http');
    let server;
    module.exports.start = () => new Promise(() => {
      server = http.createServer((request, response) => {
        if (request.url === '/config' || request.url === '/full-config') {
          response.end(JSON.stringify({ video: { sites: [{
            key: 'site', name: 'Cat', api: '/api', actions: ['home'],
          }] } }));
          return;
        }
        response.end(JSON.stringify({ ok: true }));
      });
      server.listen(Number(process.env.PORT), '127.0.0.1', () => {
        http.get(${JSON.stringify(signalOrigin + '/init-entered')}, signalResponse => {
          signalResponse.resume();
          while (true) {}
        }).once('error', () => {});
      });
    });
    module.exports.stop = () => {
      server?.closeAllConnections?.();
      server?.close();
    };
  `;
}

function stuckCatInvocationSource(signalOrigin) {
  return catSource({
    route: `if (input.stuck) {
      http.get(${JSON.stringify(signalOrigin + '/invoke-entered')}, signalResponse => {
        signalResponse.resume();
        while (true) {}
      }).once('error', () => {});
      return;
    }
    return { from: 'stuck' };`,
  });
}

function stuckCatTimerSource(signalOrigin) {
  return catSource({
    beforeBody: `if (request.url === '/api/home' && input.arm) {
      setTimeout(() => {
        http.get(${JSON.stringify(signalOrigin + '/timer-entered')}, signalResponse => {
          signalResponse.resume();
          while (true) {}
        }).once('error', () => {});
      }, 100);
      response.end(JSON.stringify({ armed: true }));
      return;
    }`,
    route: 'return { from: "timer" };',
  });
}

function stuckCatLifecycleSource(signalOrigin, volumeFile, volumeText) {
  return catSource({
    volumeFile,
    volumeText,
    route: `if (input.stuck) {
      http.get(${JSON.stringify(signalOrigin + '/lifecycle-entered')}, signalResponse => {
        signalResponse.resume();
        while (true) {}
      }).once('error', () => {});
      return;
    }
    return { from: 'stuck' };`,
  });
}

function assertStuckCatVolume(root, installationId, file, expected) {
  assert.equal(readFileSync(join(root, 'volumes', installationId, file), 'utf8'), expected);
}

function catRootFor(t) {
  const root = mkdtempSync(join(tmpdir(), 'meowfilm-worker-isolation-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return root;
}

async function healthyCatGatewayFor(t) {
  const runtime = createCatRuntime({ dataRoot: catRootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({ installationId: 'healthy', enabled: true, source: healthyCatSource() });
  const play = await runtime.invoke({ installationId: 'healthy', siteId: 'site',
    operation: 'play', payload: {} });
  return async () => {
    const startedInvoke = performance.now();
    assert.deepEqual(await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'home', payload: {} }), { from: 'healthy' });
    assertUnder1s(startedInvoke, 'healthy Cat invoke');
    const startedProxy = performance.now();
    const proxy = await requestURL(play.url);
    assertUnder1s(startedProxy, 'healthy Cat gateway proxy');
    assert.equal(proxy.status, 200);
    assert.equal(proxy.text, 'proxy-ok');
  };
}

function assertPortReleased(port) {
  const server = net.createServer(() => {});
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', () => resolve(server));
  });
}

test('B22 a stuck LX initialization does not delay another instance',
  { timeout: 15000 }, async t => {
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      limits: { initTimeoutMs: 60000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 },
    });
    t.after(() => host.close().catch(() => {}));
    const assertCatAvailable = await healthyCatGatewayFor(t);
    await host.install({ installationId: 'b', enabled: true, source: lxScript() });
    const stuck = host.install({
      installationId: 'a', enabled: true, source: lxInitStuckSource('init-entered'),
    });
    stuck.catch(() => {});
    let settled = false;
    stuck.finally(() => { settled = true; }).catch(() => {});
    await waitFor(lxAlertEntered(owners, 'a'),
      'stuck LX initialization did not enter its loop');
    const started = performance.now();
    const value = await host.invoke(target('b'));
    assertUnder1s(started, 'healthy invoke');
    assert.equal(value.action, 'musicUrl');
    await assertCatAvailable();
    assert.equal(owners.sessions.get('a').alert.log, 'init-entered');
    assert.equal(owners.sessions.get('a').live, true);
    assert.equal(owners.sessions.get('a').failure, null);
    assert.equal(settled, false);
  });

test('B22 a stuck LX invocation does not delay another instance',
  { timeout: 15000 }, async t => {
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      limits: { initTimeoutMs: 10000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 },
    });
    t.after(() => host.close().catch(() => {}));
    const assertCatAvailable = await healthyCatGatewayFor(t);
    await host.install({ installationId: 'a', enabled: true, source: lxInvokeStuckSource('invoke-entered') });
    await host.install({ installationId: 'b', enabled: true, source: lxScript() });
    const pending = host.invoke(target('a', 'kw', { stuck: true }), { timeoutMs: 60000 });
    pending.catch(() => {});
    await waitFor(lxAlertEntered(owners, 'a'),
      'stuck LX invocation did not enter its loop');
    const started = performance.now();
    const value = await host.invoke(target('b'));
    assertUnder1s(started, 'healthy invoke');
    assert.equal(value.action, 'musicUrl');
    await assertCatAvailable();
    assert.equal(owners.sessions.get('a').alert.log, 'invoke-entered');
    assert.equal(owners.sessions.get('a').live, true);
    assert.equal(owners.sessions.get('a').failure, null);
    assert.equal(host.get('a').state, 'ready');
  });

test('B22 a stuck LX background timer does not delay another instance',
  { timeout: 15000 }, async t => {
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      limits: { initTimeoutMs: 10000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 },
    });
    t.after(() => host.close().catch(() => {}));
    const assertCatAvailable = await healthyCatGatewayFor(t);
    await host.install({ installationId: 'a', enabled: true, source: lxTimerStuckSource('timer-entered') });
    await host.install({ installationId: 'b', enabled: true, source: lxScript() });
    assert.equal(await host.invoke(target('a', 'kw', { arm: true })), 'armed');
    await waitFor(lxAlertEntered(owners, 'a'),
      'background LX timer did not enter its loop');
    const started = performance.now();
    const value = await host.invoke(target('b'));
    assertUnder1s(started, 'healthy invoke');
    assert.equal(value.action, 'musicUrl');
    await assertCatAvailable();
    assert.equal(owners.sessions.get('a').alert.log, 'timer-entered');
    assert.equal(owners.sessions.get('a').live, true);
    assert.equal(owners.sessions.get('a').failure, null);
    assert.equal(host.get('a').state, 'ready');
  });

test('B22 a stuck Cat initialization does not delay another instance, invoke, or gateway proxy',
  { timeout: 30000 }, async t => {
    const root = catRootFor(t);
    const networkEvents = [];
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const source = stuckCatInitSource(signalServer.socketOrigin);
    const runtime = createCatRuntime({ dataRoot: root,
      onNetwork: event => networkEvents.push(event),
      limits: { initTimeoutMs: 60000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 } });
    t.after(() => runtime.close().catch(() => {}));
    await runtime.install({ installationId: 'healthy', enabled: true, config: {}, version: '1',
      source: healthyCatSource() });
    const status = await runtime.status();
    const play = await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'play', payload: {} });
    assert.equal(new URL(play.url).origin, status.aggregate.origin);

    const stuck = runtime.install({ installationId: 'stuck', enabled: true, config: {},
      version: '1', source });
    stuck.catch(() => {});
    const settlement = trackSettlement(stuck);
    await waitFor(catEntered(networkEvents, 'stuck'),
      'stuck Cat initialization did not enter its loop');
    const startedInvoke = performance.now();
    const home = await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'home', payload: {} });
    assertUnder1s(startedInvoke, 'healthy invoke');
    assert.deepEqual(home, { from: 'healthy' });
    const startedProxy = performance.now();
    const proxy = await requestURL(play.url);
    assertUnder1s(startedProxy, 'healthy gateway proxy');
    assert.equal(proxy.status, 200);
    assert.equal(proxy.text, 'proxy-ok');
    assert.equal(settlement.settled, false);
    assert.equal(runtime.instance('stuck').state, 'starting');
  });

test('B22 a stuck Cat invocation does not delay another instance, invoke, or gateway proxy',
  { timeout: 30000 }, async t => {
    const root = catRootFor(t);
    const networkEvents = [];
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const source = stuckCatInvocationSource(signalServer.socketOrigin);
    const runtime = createCatRuntime({ dataRoot: root,
      onNetwork: event => networkEvents.push(event),
      limits: { initTimeoutMs: 10000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 } });
    t.after(() => runtime.close().catch(() => {}));
    await runtime.install({ installationId: 'healthy', enabled: true, config: {}, version: '1',
      source: healthyCatSource() });
    const status = await runtime.status();
    const play = await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'play', payload: {} });
    assert.equal(new URL(play.url).origin, status.aggregate.origin);

    await runtime.install({ installationId: 'stuck', enabled: true, config: {}, version: '1',
      source });
    const enteredFrom = networkEvents.length;
    const pending = runtime.invoke({ installationId: 'stuck', siteId: 'site',
      operation: 'home', payload: { stuck: true } }, { timeoutMs: 60000 });
    pending.catch(() => {});
    const settlement = trackSettlement(pending);
    await waitFor(catEntered(networkEvents, 'stuck', enteredFrom),
      'stuck Cat invocation did not enter its loop');
    const startedInvoke = performance.now();
    const home = await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'home', payload: {} });
    assertUnder1s(startedInvoke, 'healthy invoke');
    assert.deepEqual(home, { from: 'healthy' });
    const startedProxy = performance.now();
    const proxy = await requestURL(play.url);
    assertUnder1s(startedProxy, 'healthy gateway proxy');
    assert.equal(proxy.status, 200);
    assert.equal(proxy.text, 'proxy-ok');
    assert.equal(runtime.instance('stuck').state, 'ready');
    assert.equal(settlement.settled, false);
  });

test('B22 a stuck Cat background timer does not delay another instance, invoke, or gateway proxy',
  { timeout: 30000 }, async t => {
    const root = catRootFor(t);
    const networkEvents = [];
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const source = stuckCatTimerSource(signalServer.socketOrigin);
    const runtime = createCatRuntime({ dataRoot: root,
      onNetwork: event => networkEvents.push(event),
      limits: { initTimeoutMs: 10000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 } });
    t.after(() => runtime.close().catch(() => {}));
    await runtime.install({ installationId: 'healthy', enabled: true, config: {}, version: '1',
      source: healthyCatSource() });
    const status = await runtime.status();
    const play = await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'play', payload: {} });
    assert.equal(new URL(play.url).origin, status.aggregate.origin);

    await runtime.install({ installationId: 'stuck', enabled: true, config: {}, version: '1',
      source });
    const enteredFrom = networkEvents.length;
    assert.deepEqual(await runtime.invoke({ installationId: 'stuck', siteId: 'site',
      operation: 'home', payload: { arm: true } }), { armed: true });
    // The real timer's localhost response is observed immediately before its
    // guest callback synchronously enters the loop.
    await waitFor(catEntered(networkEvents, 'stuck', enteredFrom),
      'stuck Cat background timer did not enter its loop');
    const startedInvoke = performance.now();
    const home = await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'home', payload: {} });
    assertUnder1s(startedInvoke, 'healthy invoke');
    assert.deepEqual(home, { from: 'healthy' });
    const startedProxy = performance.now();
    const proxy = await requestURL(play.url);
    assertUnder1s(startedProxy, 'healthy gateway proxy');
    assert.equal(proxy.status, 200);
    assert.equal(proxy.text, 'proxy-ok');
    assert.equal(runtime.instance('stuck').state, 'ready');
  });

test('B23 an expired LX invocation interrupts its native thread and can be re-enabled',
  { timeout: 15000 }, async t => {
    const diagnostics = [];
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      limits: { requestTimeoutMs: 150, cleanupTimeoutMs: 50, initTimeoutMs: 1000 },
      onDiagnostic: event => diagnostics.push(event),
    });
    t.after(() => host.close().catch(() => {}));
    const installed = await host.install({
      installationId: 'a', enabled: true, config: {},
      source: lxInvokeStuckSource('invoke-entered'),
    });
    const timedOut = assert.rejects(host.invoke(target('a', 'kw', { stuck: true })),
      { code: 'TIMEOUT' });
    await waitFor(lxAlertEntered(owners, 'a'), 'expired LX invocation did not enter its loop');
    assert.equal(owners.sessions.get('a').alert.log, 'invoke-entered');
    await timedOut;
    await waitFor(() => host.get('a').state === 'failed',
      'timed out LX native owner did not become failed');
    const failed = host.get('a');
    assert.equal(failed.lastFailure, 'TIMEOUT');
    assert.equal(failed.generation, installed.generation);
    await waitFor(() => diagnostics.some(event =>
      event.operation === 'native.unresponsive' && event.code === 'TIMEOUT'),
    'missing native.unresponsive diagnostic');
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(host.get('a').state, 'failed');
    assert.equal(host.get('a').generation, installed.generation);
    const enabled = await host.enable('a');
    assert.equal(enabled.state, 'ready');
    assert.equal(await host.invoke(target('a')), 'ok');
  });

test('B23 an expired LX invocation reports the unresponsive operation once',
  { timeout: 15000 }, async t => {
    const diagnostics = [];
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      limits: { requestTimeoutMs: 100, cleanupTimeoutMs: 50, initTimeoutMs: 1000 },
      onDiagnostic: event => diagnostics.push(event),
    });
    t.after(() => host.close().catch(() => {}));
    await host.install({ installationId: 'a', enabled: true,
      source: lxInvokeStuckSource('invoke-entered') });
    const timedOut = assert.rejects(host.invoke(target('a', 'kw', { stuck: true })),
      { code: 'TIMEOUT' });
    await waitFor(lxAlertEntered(owners, 'a'), 'expired LX invocation did not enter its loop');
    assert.equal(owners.sessions.get('a').alert.log, 'invoke-entered');
    await timedOut;
    await waitFor(() => host.get('a').state === 'failed', 'stuck invocation did not fail');
    await waitFor(() => {
      const events = diagnostics.filter(event =>
        event.operation === 'native.unresponsive' && event.code === 'TIMEOUT');
      return events.length === 1 && events[0].installationId === 'a' &&
        events[0].phase === 'running';
    }, 'missing exactly one running native.unresponsive diagnostic');
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(diagnostics.filter(event =>
      event.operation === 'native.unresponsive').length, 1);
    assert.equal(host.stats().liveSessions, 0);
    assert.equal((await host.enable('a')).state, 'ready');
    assert.equal(await host.invoke(target('a')), 'ok');
  });

test('B23 a stuck LX initialization interrupts its native thread and can be retried',
  { timeout: 15000 }, async t => {
    const diagnostics = [];
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      limits: { initTimeoutMs: 1000, cleanupTimeoutMs: 50, requestTimeoutMs: 60000 },
      onDiagnostic: event => diagnostics.push(event),
    });
    t.after(() => host.close().catch(() => {}));
    const timedOut = assert.rejects(host.install({
      installationId: 'a', enabled: true, config: { stuck: true },
      source: lxSourceWithAlert({ alert: 'init-entered',
        setup: 'if (meow.config.stuck) { signal(); while (true) {} }' }),
    }), { code: 'TIMEOUT' });
    await waitFor(lxAlertEntered(owners, 'a'), 'stuck LX initialization did not enter its loop');
    assert.equal(owners.sessions.get('a').alert.log, 'init-entered');
    await timedOut;
    await waitFor(() => diagnostics.some(event =>
      event.operation === 'native.unresponsive' && event.code === 'TIMEOUT' &&
      event.phase === 'initialization'),
    'missing initialization native.unresponsive diagnostic');
    assert.equal(host.get('a').lastFailure, 'TIMEOUT');
    assert.equal(host.get('a').state, 'failed');
    await host.update('a', { source: lxScript({ handler: 'async () => "recovered"' }), config: {} });
    const enabled = await host.enable('a');
    assert.equal(enabled.state, 'ready');
    assert.equal(await host.invoke(target('a')), 'recovered');
  });

test('B23 an expired Cat invocation fails the worker without affecting another instance',
  { timeout: 30000 }, async t => {
    const diagnostics = [];
    const networkEvents = [];
    const root = catRootFor(t);
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const runtime = createCatRuntime({ dataRoot: root,
      limits: { requestTimeoutMs: 150, cleanupTimeoutMs: 50, initTimeoutMs: 10000 },
      onNetwork: event => networkEvents.push(event),
      onDiagnostic: event => diagnostics.push(event) });
    t.after(() => runtime.close().catch(() => {}));
    await runtime.install({ installationId: 'stuck', enabled: true, config: {}, version: '1',
      source: stuckCatInvocationSource(signalServer.socketOrigin) });
    await runtime.install({ installationId: 'healthy', enabled: true, config: {}, version: '1',
      source: healthyCatSource() });
    const enteredFrom = networkEvents.length;
    const timedOut = assert.rejects(runtime.invoke({ installationId: 'stuck', siteId: 'site',
      operation: 'home', payload: { stuck: true } }), { code: 'TIMEOUT' });
    await waitFor(catEntered(networkEvents, 'stuck', enteredFrom),
      'expired Cat invocation did not enter its loop');
    await timedOut;
    await waitFor(() => runtime.instance('stuck').state === 'failed',
      'timed out Cat worker did not fail');
    await waitFor(() => diagnostics.some(event =>
      event.installationId === 'stuck' && event.operation === 'worker.unresponsive' &&
      event.code === 'TIMEOUT' && event.phase === 'running'),
    'missing Cat worker.unresponsive diagnostic');
    assert.deepEqual(await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'home', payload: {} }), { from: 'healthy' });
    assert.equal((await runtime.setEnabled('stuck', true)).state, 'ready');
    assert.deepEqual(await runtime.invoke({ installationId: 'stuck', siteId: 'site',
      operation: 'home', payload: {} }), { from: 'stuck' });
  });

for (const operation of ['describe', 'sync']) for (const ending of ['timeout', 'abort', 'normal']) {
  test(`Cat management ${operation} ${ending} controls its own Worker without a later lifecycle request`,
    { timeout: 30000 }, async t => {
      const root = catRootFor(t), diagnostics = [];
      const runtime = createCatRuntime({ dataRoot: root,
        limits: { initTimeoutMs: 10000, cleanupTimeoutMs: 100,
          requestTimeoutMs: ending === 'timeout' ? 150 : ending === 'normal' ? 2000 : 60000 },
        onDiagnostic: event => diagnostics.push(event) });
      t.after(() => runtime.close().catch(() => {}));
      const installed = await runtime.install({ installationId: 'managed', enabled: true,
        source: catSource({ beforeBody: `
          if (request.url === '/website/api/credentials') {
            fs.writeFileSync('/management-entered.txt', 'entered');
            ${ending === 'normal'
              ? 'const end = Date.now() + 600; while (Date.now() < end) {}'
              : 'while (true) {}'}
            response.end(JSON.stringify({code:0,data:{quark:{cookie:''}}}));
            return;
          }
          if (request.url === '/website/api/credential/quark/cookie' && request.method === 'PUT') {
            fs.writeFileSync('/credentials.json', JSON.stringify({cookie:input.value}));
            response.end(JSON.stringify({code:0}));
            return;
          }
        ` }) });
      const healthy = await runtime.install({ installationId: 'healthy', enabled: true,
        source: 'let count=0; module.exports={home(){return {count:++count}}};' });
      const invokeHealthy = () => runtime.invoke({
        installationId: 'healthy', siteId: 'default', operation: 'home', payload: {},
      });
      assert.deepEqual(await invokeHealthy(), { count: 1 });
      const session = runtime.sessions.get('managed'), worker = session.worker, thread = worker.threadId;
      const healthySession = runtime.sessions.get('healthy'), healthyThread = healthySession.worker.threadId;
      const port = Number(new URL(session.endpoint().origin).port);
      const marker = join(root, 'volumes', 'managed', 'management-entered.txt');
      const entered = deferred(), failed = deferred();
      const watcher = watch(join(root, 'volumes', 'managed'), (_event, filename) => {
        if (String(filename) === 'management-entered.txt' &&
            existsSync(marker) && readFileSync(marker, 'utf8') === 'entered') entered.resolve();
      });
      t.after(() => watcher.close());
      const onFailure = session.input.onFailure;
      session.input.onFailure = (...args) => { onFailure(...args); failed.resolve(); };
      const exited = ending === 'normal' ? null : once(worker, 'exit', { signal: t.signal });
      exited?.catch(() => {});
      const controller = new AbortController();
      let settled = false;
      const started = performance.now();
      const pending = (operation === 'describe'
        ? runtime.management('managed', installed.generation, { signal: controller.signal })
        : runtime.sync('managed', installed.generation,
          { provider: 'quark', credentials: { cookie: 'fixture' } }, { signal: controller.signal }))
        .then(value => { settled = true; return { value }; },
          error => { settled = true; return { code: error.code }; });
      await entered.promise;
      watcher.close();
      assert.equal(settled, false, 'the management handler must enter before its caller is retired');
      assert.equal(worker.threadId, thread);
      const interrupted = performance.now();
      if (ending === 'abort') controller.abort();
      assert.deepEqual(await invokeHealthy(), { count: 2 });
      const outcome = await pending;
      if (ending === 'normal') {
        assert.deepEqual(outcome, { value: operation === 'describe'
          ? { protocol: 'website-api-v1', fields: { quark: ['cookie'] } }
          : { status: 'saved', protocol: 'website-api-v1' } });
        assert.ok(performance.now() - started >= 600);
        assert.equal(session.limits.cleanupTimeoutMs, 100);
        assert.equal(worker.threadId, thread);
        assert.equal(runtime.instance('managed').generation, installed.generation);
        assert.equal(runtime.instance('managed').state, 'ready');
        assert.deepEqual(diagnostics.filter(event => event.operation === 'worker.unresponsive'), []);
        if (operation === 'sync')
          assert.deepEqual(JSON.parse(readFileSync(join(root, 'volumes', 'managed', 'credentials.json'), 'utf8')),
            { cookie: 'fixture' });
      } else {
        const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
        assert.deepEqual(outcome, { code });
        // Only management's own interruption may retire A: no extra invoke,
        // stop, disable or close of A precedes this real exit observation.
        await exited;
        await failed.promise;
        assertUnder1s(ending === 'timeout' ? started : interrupted, 'management Worker exit');
        assert.equal(worker.threadId, -1);
        assert.equal(session.worker, null);
        assert.equal(session.live, false);
        assert.equal(runtime.instance('managed').state, 'failed');
        assert.equal(runtime.instance('managed').error, code);
        assert.equal(runtime.instance('managed').generation, installed.generation);
        assert.equal(existsSync(join(root, 'volumes', 'managed', 'credentials.json')), false);
        const rebound = await assertPortReleased(port);
        t.after(() => new Promise(resolve => rebound.close(resolve)));
        assert.deepEqual(diagnostics.filter(event => event.operation === 'worker.unresponsive')
          .map(({ installationId, code, phase }) => ({ installationId, code, phase })),
        [{ installationId: 'managed', code, phase: 'running' }]);
      }
      assert.deepEqual(await invokeHealthy(), { count: 3 });
      assert.equal(runtime.instance('healthy').generation, healthy.generation);
      assert.equal(runtime.instance('healthy').state, 'ready');
      assert.equal(runtime.sessions.get('healthy'), healthySession);
      assert.equal(healthySession.worker.threadId, healthyThread);
    });
}

for (const [kind, loop] of [
  ['synchronous loop', 'while (true) {}'],
  ['infinite microtasks', 'const spin = () => Promise.resolve().then(spin); spin();'],
]) {
  test(`a cancelled Cat ${kind} exits without another timeout or lifecycle request`,
    { timeout: 30000 }, async t => {
      const diagnostics = [], networkEvents = [];
      const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
      const runtime = createCatRuntime({ dataRoot: catRootFor(t),
        limits: { requestTimeoutMs: 60000, cleanupTimeoutMs: 100, initTimeoutMs: 10000 },
        onNetwork: event => networkEvents.push(event),
        onDiagnostic: event => diagnostics.push(event) });
      t.after(() => runtime.close().catch(() => {}));
      await runtime.install({ installationId: 'stuck', enabled: true, source: catSource({
        route: `if (input.stuck) {
          http.get(${JSON.stringify(signalServer.socketOrigin + '/cancel-entered')}, signalResponse => {
            signalResponse.resume();
            ${loop}
          }).once('error', () => {});
          return;
        }
        return { from: 'stuck' };`,
      }) });
      const healthy = await runtime.install({ installationId: 'healthy', enabled: true,
        source: healthyCatSource() });
      const session = runtime.sessions.get('stuck'), worker = session.worker;
      const healthyWorker = runtime.sessions.get('healthy').worker;
      const healthyThread = healthyWorker.threadId;
      const port = Number(new URL(session.endpoint().origin).port);
      const enteredFrom = networkEvents.length;
      const controller = new AbortController();
      const cancelled = assert.rejects(runtime.invoke({ installationId: 'stuck', siteId: 'site',
        operation: 'home', payload: { stuck: true } }, { signal: controller.signal }),
      { code: 'CANCELLED' });
      await waitFor(catEntered(networkEvents, 'stuck', enteredFrom),
        'Cat did not enter the cancellation fixture');
      const started = performance.now();
      controller.abort();
      await cancelled;
      // No extra invoke, stats RPC, stop, or disable is used to retire A.
      await waitFor(() => worker.threadId === -1, 'cancelled Cat did not physically exit');
      assertUnder1s(started, 'cancelled Cat exit');
      await waitFor(() => runtime.instance('stuck').state === 'failed',
        'cancelled unresponsive Cat did not report failure');
      assert.equal(runtime.instance('stuck').error, 'CANCELLED');
      assert.equal(session.worker, null);
      const rebound = await assertPortReleased(port);
      t.after(() => new Promise(resolve => rebound.close(resolve)));
      assert.deepEqual(diagnostics.filter(event => event.operation === 'worker.unresponsive')
        .map(({ installationId, code, phase }) => ({ installationId, code, phase })),
      [{ installationId: 'stuck', code: 'CANCELLED', phase: 'running' }]);
      assert.deepEqual(await runtime.invoke({ installationId: 'healthy', siteId: 'site',
        operation: 'home', payload: {} }), { from: 'healthy' });
      assert.equal(runtime.instance('healthy').generation, healthy.generation);
      assert.equal(healthyWorker.threadId, healthyThread);
    });
}

test('cooperative Cat cancellation keeps its Worker and a retired signal cannot stop a successor',
  { timeout: 30000 }, async t => {
    const diagnostics = [], networkEvents = [];
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const source = catSource({
      route: `if (input.wait) {
        http.get(${JSON.stringify(signalServer.socketOrigin + '/waiting')}, reply => reply.resume())
          .once('error', () => {});
        return;
      }
      return { from: 'cooperative' };`,
    });
    const runtime = createCatRuntime({ dataRoot: catRootFor(t),
      limits: { requestTimeoutMs: 60000, cleanupTimeoutMs: 100, initTimeoutMs: 10000 },
      onNetwork: event => networkEvents.push(event),
      onDiagnostic: event => diagnostics.push(event) });
    t.after(() => runtime.close().catch(() => {}));
    const installed = await runtime.install({ installationId: 'a', enabled: true, source });
    const worker = runtime.sessions.get('a').worker, thread = worker.threadId;
    const enteredFrom = networkEvents.length, controller = new AbortController();
    const cancelled = assert.rejects(runtime.invoke({ installationId: 'a', siteId: 'site',
      operation: 'home', payload: { wait: true } }, { signal: controller.signal }),
    { code: 'CANCELLED' });
    await waitFor(catEntered(networkEvents, 'a', enteredFrom), 'Cat did not enter its I/O wait');
    controller.abort();
    await cancelled;
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(worker.threadId, thread);
    assert.equal(runtime.instance('a').generation, installed.generation);
    assert.equal(runtime.instance('a').state, 'ready');
    const retired = new AbortController();
    assert.deepEqual(await runtime.invoke({ installationId: 'a', siteId: 'site',
      operation: 'home', payload: {} }, { signal: retired.signal }), { from: 'cooperative' });
    const updated = await runtime.install({ installationId: 'a', enabled: true, source, version: '2' });
    const successor = runtime.sessions.get('a').worker, successorThread = successor.threadId;
    assert.ok(updated.generation > installed.generation);
    retired.abort();
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(successor.threadId, successorThread);
    assert.equal(runtime.instance('a').generation, updated.generation);
    assert.deepEqual(await runtime.invoke({ installationId: 'a', siteId: 'site',
      operation: 'home', payload: {} }), { from: 'cooperative' });
    assert.deepEqual(diagnostics.filter(event => event.operation === 'worker.unresponsive'), []);
  });

test('B23 a stuck Cat initialization probes and terminates only its worker',
  { timeout: 30000 }, async t => {
    const diagnostics = [], networkEvents = [];
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const runtime = createCatRuntime({ dataRoot: catRootFor(t),
      onNetwork: event => networkEvents.push(event),
      onDiagnostic: event => diagnostics.push(event),
      limits: { initTimeoutMs: 1000, cleanupTimeoutMs: 50, requestTimeoutMs: 60000 } });
    t.after(() => runtime.close().catch(() => {}));
    await runtime.install({ installationId: 'healthy', enabled: true, source: healthyCatSource() });
    const timedOut = assert.rejects(runtime.install({ installationId: 'stuck', enabled: true,
      source: stuckCatInitSource(signalServer.socketOrigin) }), { code: 'TIMEOUT' });
    await waitFor(catEntered(networkEvents, 'stuck'),
      'stuck Cat initialization did not enter its loop');
    await timedOut;
    assert.equal(runtime.instance('stuck').state, 'failed');
    assert.equal(runtime.instance('stuck').error, 'TIMEOUT');
    const events = diagnostics.filter(event => event.operation === 'worker.unresponsive');
    assert.equal(events.length, 1);
    assert.equal(events[0].installationId, 'stuck');
    assert.equal(events[0].phase, 'initialization');
    assert.equal(runtime.host.stats().liveSessions, 1);
    assert.deepEqual(await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'home', payload: {} }), { from: 'healthy' });
  });

async function prepareStuckCatLifecycle(t, { operation }) {
  const root = catRootFor(t);
  const networkEvents = [];
  const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
  const source = stuckCatLifecycleSource(signalServer.socketOrigin,
    'persisted.txt', `${operation}-persisted`);
  const runtime = createCatRuntime({ dataRoot: root,
    onNetwork: event => networkEvents.push(event),
    limits: { requestTimeoutMs: 60000, cleanupTimeoutMs: 100, initTimeoutMs: 10000 } });
  t.after(() => runtime.close().catch(() => {}));
  const persisted = `${operation}-persisted`;
  const installed = await runtime.install({ installationId: 'stuck', enabled: true,
    config: {}, version: '1', source });
  const port = Number(new URL(runtime.instance('stuck').endpoints[0].origin).port);
  const enteredFrom = networkEvents.length;
  const pending = runtime.invoke({ installationId: 'stuck', siteId: 'site',
    operation: 'home', payload: { stuck: true } }, { timeoutMs: 60000 });
  pending.catch(() => {});
  await waitFor(catEntered(networkEvents, 'stuck', enteredFrom),
    'stuck Cat lifecycle operation did not enter its loop');
  assert.equal(runtime.instance('stuck').state, 'ready');
  return { runtime, root, installed, persisted, port };
}

test('B24 disabling a stuck Cat instance stays bounded, releases its port and preserves volume',
  { timeout: 30000 }, async t => {
    const { runtime, root, persisted, port } = await prepareStuckCatLifecycle(t, {
      operation: 'disable',
    });
    const started = performance.now();
    const disabled = await runtime.setEnabled('stuck', false);
    const elapsed = performance.now() - started;
    assert.equal(disabled.state, 'disabled');
    assert.ok(elapsed < 1100, `disable took ${elapsed}ms`);
    const rebound = await assertPortReleased(port);
    t.after(() => new Promise(resolve => rebound.close(resolve)));
    assertStuckCatVolume(root, 'stuck', 'persisted.txt', persisted);
  });

test('B24 updating a stuck Cat instance stays bounded, releases its old port and preserves volume',
  { timeout: 30000 }, async t => {
    const { runtime, root, persisted, port } = await prepareStuckCatLifecycle(t, {
      operation: 'update',
    });
    const started = performance.now();
    const updated = await runtime.install({
      installationId: 'stuck',
      source: healthyCatSource(), config: {}, version: '2',
      enabled: true,
    });
    const elapsed = performance.now() - started;
    assert.equal(updated.state, 'ready');
    assert.ok(elapsed < 1100, `update took ${elapsed}ms`);
    const rebound = await assertPortReleased(port);
    t.after(() => new Promise(resolve => rebound.close(resolve)));
    assertStuckCatVolume(root, 'stuck', 'persisted.txt', persisted);
  });

test('B24 uninstalling a stuck Cat instance stays bounded, releases its port and removes its volume',
  { timeout: 30000 }, async t => {
    const { runtime, root, installed, port } = await prepareStuckCatLifecycle(t, {
      operation: 'uninstall',
    });
    const sourceFile = join(root, 'sources', installed.sourceHash + '.js');
    assert.equal(existsSync(sourceFile), true);
    const started = performance.now();
    await runtime.uninstall('stuck');
    const elapsed = performance.now() - started;
    assert.ok(elapsed < 1100, `uninstall took ${elapsed}ms`);
    const rebound = await assertPortReleased(port);
    t.after(() => new Promise(resolve => rebound.close(resolve)));
    assert.equal(existsSync(join(root, 'volumes', 'stuck')), false);
    assert.equal(existsSync(sourceFile), false);
    assert.deepEqual((await runtime.status()).instances, []);
  });

test('B24 closing a runtime with a stuck Cat instance stays bounded, releases its port and preserves volume',
  { timeout: 30000 }, async t => {
    const { runtime, root, persisted, port } = await prepareStuckCatLifecycle(t, {
      operation: 'close',
    });
    const started = performance.now();
    await assert.rejects(runtime.close(), { code: 'TIMEOUT' });
    const elapsed = performance.now() - started;
    assert.ok(elapsed < 1100, `close took ${elapsed}ms`);
    const rebound = await assertPortReleased(port);
    t.after(() => new Promise(resolve => rebound.close(resolve)));
    assertStuckCatVolume(root, 'stuck', 'persisted.txt', persisted);
  });

test('B24 closing a runtime with a stuck Cat initialization does not wait for initTimeoutMs',
  { timeout: 30000 }, async t => {
    const root = catRootFor(t);
    const networkEvents = [];
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const source = stuckCatInitSource(signalServer.socketOrigin);
    const runtime = createCatRuntime({ dataRoot: root,
      onNetwork: event => networkEvents.push(event),
      limits: { requestTimeoutMs: 60000, cleanupTimeoutMs: 100, initTimeoutMs: 60000 } });
    t.after(() => runtime.close().catch(() => {}));
    const installing = runtime.install({ installationId: 'stuck', enabled: true,
      config: {}, version: '1', source });
    installing.catch(() => {});
    await waitFor(catEntered(networkEvents, 'stuck'),
      'stuck Cat initialization did not enter its loop');
    const session = runtime.host.initializingSessions().find(value =>
      value.installationId === 'stuck');
    assert.ok(session?.worker, 'initialization must own a real worker before closing');
    const port = Number(new URL(session.endpoint().origin).port);
    const rejected = assert.rejects(installing, { code: 'HOST_CLOSED' });
    const started = performance.now();
    await runtime.close();
    await rejected;
    const elapsed = performance.now() - started;
    assert.ok(elapsed < 1100, `close took ${elapsed}ms`);
    assert.equal(session.worker, null);
    const rebound = await assertPortReleased(port);
    t.after(() => new Promise(resolve => rebound.close(resolve)));
  });

async function prepareStuckLXWithOwners(t) {
  const owners = hostCapturingNativeSessions();
  const host = createScriptHost({
    adapters: { lx: owners.factory },
    limits: { initTimeoutMs: 10000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 },
  });
  t.after(() => host.close().catch(() => {}));
  await host.install({ installationId: 'a', enabled: true,
    source: lxInvokeStuckSource('entered') });
  const pending = host.invoke(target('a', 'kw', { stuck: true }), { timeoutMs: 60000 });
  pending.catch(() => {});
  await waitFor(lxAlertEntered(owners, 'a'),
    'stuck LX invocation did not enter its loop');
  assert.equal(owners.sessions.get('a').alert.log, 'entered');
  assert.equal(owners.sessions.get('a').live, true);
  assert.equal(owners.sessions.get('a').failure, null);
  assert.equal(host.get('a').state, 'ready');
  return { host, owners };
}

test('B24 disabling a stuck LX invocation completes within the cleanup budget',
  { timeout: 15000 }, async t => {
    const { host } = await prepareStuckLXWithOwners(t);
    const started = performance.now();
    const disabled = await host.disable('a');
    const elapsed = performance.now() - started;
    assert.equal(disabled.state, 'disabled');
    assert.ok(elapsed < 1100, `disable took ${elapsed}ms`);
    assert.equal(host.stats().liveSessions, 0);
  });

test('B24 updating a stuck LX invocation completes within the cleanup budget',
  { timeout: 15000 }, async t => {
    const { host } = await prepareStuckLXWithOwners(t);
    const started = performance.now();
    const updated = await host.update('a', {
      source: lxScript({ handler: 'async () => "updated"' }), config: {},
    });
    const elapsed = performance.now() - started;
    assert.equal(updated.state, 'ready');
    assert.ok(elapsed < 1100, `update took ${elapsed}ms`);
    assert.equal(await host.invoke(target('a')), 'updated');
  });

test('B24 uninstalling a stuck LX invocation completes within the cleanup budget',
  { timeout: 15000 }, async t => {
    const { host } = await prepareStuckLXWithOwners(t);
    const started = performance.now();
    await host.uninstall('a');
    const elapsed = performance.now() - started;
    assert.ok(elapsed < 1100, `uninstall took ${elapsed}ms`);
    assert.throws(() => host.get('a'), { code: 'NOT_FOUND' });
    assert.equal(host.stats().installations, 0);
  });

test('B24 closing a host with a stuck LX invocation completes within the cleanup budget',
  { timeout: 15000 }, async t => {
    const { host } = await prepareStuckLXWithOwners(t);
    const started = performance.now();
    const result = await host.close();
    const elapsed = performance.now() - started;
    assert.ok(elapsed < 1100, `close took ${elapsed}ms`);
    assert.equal(result.liveSessions, 0);
  });

test('B24 closing a host with a stuck LX initialization does not wait for the initialization deadline',
  { timeout: 15000 }, async t => {
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      limits: { initTimeoutMs: 60000, cleanupTimeoutMs: 100, requestTimeoutMs: 60000 },
    });
    const stuck = host.install({ installationId: 'a', enabled: true,
      source: lxInitStuckSource('entered') });
    stuck.catch(() => {});
    await waitFor(lxAlertEntered(owners, 'a'),
      'stuck LX initialization did not enter its loop');
    assert.equal(owners.sessions.get('a').alert.log, 'entered');
    assert.equal(owners.sessions.get('a').live, true);
    assert.equal(owners.sessions.get('a').failure, null);
    const started = performance.now();
    const result = await host.close();
    const elapsed = performance.now() - started;
    assert.ok(elapsed < 1100, `close took ${elapsed}ms`);
    assert.equal(result.liveSessions, 0);
  });

test('B25 a reported native thread exit fails only the affected LX instance',
  { timeout: 15000 }, async t => {
    const diagnostics = [];
    const owners = hostCapturingNativeSessions();
    const host = createScriptHost({
      adapters: { lx: owners.factory },
      onDiagnostic: event => diagnostics.push(event),
    });
    t.after(() => host.close().catch(() => {}));
    const installedA = await host.install({ installationId: 'a', enabled: true,
      source: lxSourceWithAlert({
        alert: 'pending-entered',
        handler: `async ({source, action, info}) => {
          if (info.musicInfo.hold) {
            signal();
            await new Promise(() => {});
          }
          return {source, action, info, config: meow.config};
        }`,
      }) });
    const installedB = await host.install({ installationId: 'b', enabled: true, source: lxScript() });
    const session = owners.sessions.get('a');
    assert.ok(session instanceof NativeSession);
    assert.equal(session.installationId, 'a');
    assert.equal(session.generation, installedA.generation);
    assert.equal((await host.invoke(target('a'))).action, 'musicUrl');
    const pending = host.invoke(target('a', 'kw', { hold: true }),
      { signal: new AbortController().signal });
    pending.catch(() => {});
    await waitFor(lxAlertEntered(owners, 'a'), 'pending LX invocation did not enter');
    assert.equal(session.alert.log, 'pending-entered');
    await session.engine.stop('SCRIPT_EXCEPTION');
    await assert.rejects(pending, { code: 'SCRIPT_EXCEPTION' });
    await waitFor(() => host.get('a').state === 'failed', 'affected instance did not fail');
    assert.equal(host.get('a').lastFailure, 'SCRIPT_EXCEPTION');
    assert.equal(host.get('a').generation, installedA.generation);
    assert.equal(host.get('b').state, 'ready');
    assert.equal((await host.invoke(target('b'))).action, 'musicUrl');
    const exits = diagnostics.filter(event => event.installationId === 'a' &&
      event.generation === installedA.generation && event.operation === 'native.exit' &&
      event.code === 'SCRIPT_EXCEPTION');
    assert.equal(exits.length, 1);
    assert.equal(diagnostics.some(event => event.installationId === 'b' &&
      event.operation === 'native.exit'), false);
    assert.equal(installedB.state, 'ready');
    assert.equal(session.live, false);
  });

test('B25 a real Cat Worker exit fails its pending call and leaves another instance available',
  { timeout: 30000 }, async t => {
    const networkEvents = [], diagnostics = [];
    const signalServer = await serverFor(t, (_request, response) => response.end('entered'));
    const runtime = createCatRuntime({ dataRoot: catRootFor(t),
      onNetwork: event => networkEvents.push(event),
      onDiagnostic: event => diagnostics.push(event) });
    t.after(() => runtime.close().catch(() => {}));
    await runtime.install({ installationId: 'a', enabled: true,
      source: stuckCatInvocationSource(signalServer.socketOrigin) });
    await runtime.install({ installationId: 'b', enabled: true, source: healthyCatSource() });
    const session = runtime.sessions.get('a');
    assert.ok(session.worker);
    const resources = ['listeners', 'sockets', 'streams', 'webResources', 'descriptors'];
    for (const key of resources) assert.equal(typeof session.stats()[key], 'number', key);
    assert.ok(session.stats().memory.heapUsed > 0);
    const enteredFrom = networkEvents.length;
    const pending = assert.rejects(runtime.invoke({ installationId: 'a', siteId: 'site',
      operation: 'home', payload: { stuck: true } }), { code: 'STALE_SESSION' });
    await waitFor(catEntered(networkEvents, 'a', enteredFrom), 'Cat invocation did not enter its worker loop');
    await session.worker.terminate();
    await pending;
    await waitFor(() => runtime.instance('a').state === 'failed', 'Cat worker exit was not published');
    assert.equal(runtime.instance('b').state, 'ready');
    assert.deepEqual(await runtime.invoke({ installationId: 'b', siteId: 'site',
      operation: 'home', payload: {} }), { from: 'healthy' });
    assert.equal(diagnostics.filter(event => event.installationId === 'a' &&
      event.operation === 'worker.exit' && event.code === 'SCRIPT_EXCEPTION').length, 1);
    assert.equal(diagnostics.some(event => event.installationId === 'b' &&
      event.operation === 'worker.exit'), false);
    assert.equal(session.live, false);
    for (const key of resources) assert.equal(session.stats()[key], 0, key);
    assert.equal(Object.hasOwn(session.stats(), 'memory'), false);
  });

test('REV-1A Cat Worker keeps CommonJS stream identity and closes the same retained resources',
  { timeout: 30000 }, async t => {
    const root = catRootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const source = `
      const Stream = require('stream'), fs = require('fs');
      const { inherits } = require('util');
      const { isMainThread } = require('worker_threads');
      class Child extends Stream {}
      function Legacy() { Stream.call(this); }
      inherits(Legacy, Stream);
      class Retained extends Stream.PassThrough {}
      const held = [], closes = [], closed = [];
      let calls = 0;
      module.exports.probe = async () => {
        if (!held.length) {
          held.push(new Retained(), Stream.Readable({ read() {} }),
            new Stream.Writable({ write(_chunk, _encoding, done) { done(); } }));
          held.forEach((value, index) => {
            closes.push(0);
            closed.push(new Promise(resolve => {
              value.once('close', () => { closes[index]++; resolve(); });
            }));
          });
        }
        const legacy = new Legacy();
        let emitted;
        legacy.once('value', value => { emitted = value; });
        legacy.emit('value', 'worker');
        const chunks = [];
        await Stream.promises.pipeline(Stream.Readable.from(['worker-', 'stream']),
          new Stream.Writable({ write(chunk, _encoding, done) {
            chunks.push(Buffer.from(chunk)); done();
          } }));
        return { calls: ++calls, worker: !isMainThread, type: typeof Stream,
          constructed: new Stream() instanceof Stream,
          inherited: new Child() instanceof Stream,
          legacy: legacy instanceof Stream && emitted === 'worker',
          aliases: Stream.Stream === Stream && require('node:stream') === Stream &&
            require('readable-stream') === Stream &&
            require('readable-stream/lib/stream.js') === Stream,
          types: held[0] instanceof Retained && held[0] instanceof Stream &&
            held[0] instanceof Stream.Readable && held[0] instanceof Stream.Writable &&
            held[0] instanceof Stream.Duplex && held[0] instanceof Stream.Transform &&
            held[0] instanceof Stream.PassThrough,
          promises: Stream.promises === require('stream/promises'),
          text: Buffer.concat(chunks).toString(),
          held: held.map((value, index) => ({
            destroyed: value.destroyed, closed: value.closed, closes: closes[index],
          })) };
      };
      module.exports.stop = async () => {
        held.forEach(value => value.destroy());
        await Promise.all(closed);
        fs.writeFileSync('/stream-close.json', JSON.stringify(held.map((value, index) => ({
          destroyed: value.destroyed, closed: value.closed, closes: closes[index],
        }))));
      };
    `;
    await runtime.install({ installationId: 'streams', enabled: true, source });
    await runtime.install({ installationId: 'healthy', enabled: true, source: healthyCatSource() });
    const session = runtime.sessions.get('streams'), worker = session.worker;
    assert.ok(worker);
    session.stats();
    await session.statsRequest;
    const baselineStreams = session.statsSnapshot.streams;
    const expected = { worker: true, type: 'function', constructed: true, inherited: true,
      legacy: true, aliases: true, types: true, promises: true, text: 'worker-stream',
      held: Array.from({ length: 3 }, () => ({ destroyed: false, closed: false, closes: 0 })) };
    for (let calls = 1; calls <= 2; calls++) {
      assert.deepEqual(await runtime.invoke({ installationId: 'streams', siteId: 'default',
        operation: 'probe', payload: {} }), { calls, ...expected });
      assert.equal(runtime.sessions.get('streams'), session);
      assert.equal(session.worker, worker);
      session.stats();
      await session.statsRequest;
      assert.equal(session.statsSnapshot.streams - baselineStreams, expected.held.length);
    }
    const exited = new Promise(resolve => worker.once('exit', resolve));
    await runtime.setEnabled('streams', false);
    assert.equal(await exited, 0);
    assert.deepEqual(JSON.parse(readFileSync(join(root, 'volumes', 'streams', 'stream-close.json'),
      'utf8')), Array.from({ length: 3 }, () => ({ destroyed: true, closed: true, closes: 1 })));
    assert.equal(session.live, false);
    assert.equal(session.cleanupError, null);
    assert.equal(session.stats().streams, 0);
    assert.deepEqual(await runtime.invoke({ installationId: 'healthy', siteId: 'site',
      operation: 'home', payload: {} }), { from: 'healthy' });
  });

test('B27 a long synchronous LX computation has no single-execution deadline',
  { timeout: 20000 }, async t => {
    const host = createScriptHost({ limits: { requestTimeoutMs: 60000 } });
    t.after(() => host.close());
    await host.install({
      installationId: 'a', enabled: true, source: lxScript({
        handler: `async () => {
          const text = 'x'.repeat(12 * 1024 * 1024);
          const started = Date.now();
          const encoded = JSON.stringify(text);
          let rounds = 0;
          while (Date.now() - started < 1000) rounds++;
          return { length: text.length, encoded: encoded.length, rounds,
            elapsed: Date.now() - started };
        }`,
      }),
    });
    const value = await host.invoke(target('a'));
    assert.equal(value.length, 12 * 1024 * 1024);
    assert.equal(value.encoded, 12 * 1024 * 1024 + 2);
    assert.ok(value.rounds > 0);
    assert.ok(value.elapsed >= 1000, `synchronous work only ran ${value.elapsed}ms`);
    assert.ok(value.elapsed < 10000, `synchronous work ran ${value.elapsed}ms`);
  });

test('B28 the public LX host contract remains available with native thread isolation',
  { timeout: 15000 }, async t => {
    const host = createScriptHost();
    t.after(() => host.close());
    for (const name of ['install', 'enable', 'disable', 'update', 'uninstall',
      'invoke', 'get', 'list', 'stats', 'close']) {
      assert.equal(typeof host[name], 'function', name);
    }
    await host.install({ installationId: 'a', enabled: true, source: lxScript() });
    assert.equal((await host.invoke(target('a'))).action, 'musicUrl');
    assert.equal((await host.disable('a')).state, 'disabled');
    assert.equal((await host.enable('a')).state, 'ready');
    assert.equal((await host.update('a', {
      source: lxScript({ handler: 'async () => 7' }),
    })).state, 'ready');
    assert.equal(await host.invoke(target('a')), 7);
    await host.uninstall('a');
    assert.throws(() => host.get('a'), { code: 'NOT_FOUND' });
  });

test('B28 LX results retain ordinary envelope-shaped fields and scalar values',
  { timeout: 15000 }, async t => {
    const host = createScriptHost();
    t.after(() => host.close());
    await host.install({ installationId: 'a', enabled: true, source: lxScript({
      handler: 'async ({info}) => info.musicInfo.value',
    }) });
    for (const value of [null, 0, 'plain', [1, 2],
      { value: 'public', result: 'public', newProxies: [], owned: true }]) {
      assert.deepEqual(await host.invoke(target('a', 'kw', { value })), value);
    }
  });
