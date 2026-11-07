import test from 'node:test';
import assert from 'node:assert/strict';
import * as diagnostics from 'node:diagnostics_channel';
import * as nativeTimers from 'node:timers';
import http2 from 'node:http2';
import net from 'node:net';
import stream from 'node:stream';
import { promisify } from 'node:util';
import { AsyncLocalStorage } from 'node:async_hooks';
import { EventEmitter, once } from 'node:events';
import { gzipSync } from 'node:zlib';
import { existsSync, fstatSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createCatRuntime, SCRIPT_LIMITS } from '../src/index.js';
import { CatSession } from '../src/cat-session.js';
import { serverFor } from './http-fixture.js';

function rootFor(t) {
  const root = mkdtempSync(join(tmpdir(), 'meowfilm-cat-callbacks-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return root;
}

async function sessionFor(t, source) {
  const dataRoot = rootFor(t);
  const session = new CatSession({ dataRoot, installationId: 'a', generation: 1,
    revision: { source, version: '1', config: {} },
    limits: { ...SCRIPT_LIMITS, initTimeoutMs: 10000, requestTimeoutMs: 10000, cleanupTimeoutMs: 1000 } });
  t.after(() => session.stop());
  await session.start();
  session.activate();
  return session;
}

const invoke = (runtime, installationId, operation, payload = {}) =>
  runtime.invoke({ installationId, siteId: 'default', operation, payload });
const call = (session, operation, payload = {}) =>
  session.invoke({ siteId: 'default', operation, payload });
const counterSource = 'let count = 0; module.exports.home = () => ({ count: ++count });';

test('Cat native timers reactivate, cancel by identity or number, and retire direct disposal', async t => {
  const session = await sessionFor(t, `
    'use strict';
    const timers = require('timers');
    module.exports.probe = async () => {
      const result = [];
      for (const mode of ['refresh', 'object', 'number', 'close', 'dispose', 'clear-before-refresh']) {
        let calls = 0, first, again, receiver = true, argument = true;
        const fired = new Promise(resolve => { first = resolve; });
        const second = new Promise(resolve => { again = resolve; });
        const timer = timers.setTimeout(function(value) {
          receiver &&= this === timer;
          argument &&= value === 'argument';
          if (++calls === 1) first(); else again();
        }, 5, 'argument');
        await fired;
        let identity = true;
        if (mode === 'clear-before-refresh') timers.clearTimeout(timer);
        identity &&= timer.refresh() === timer;
        if (mode === 'object') timers.clearTimeout(timer);
        if (mode === 'number') timers.clearTimeout(Number(timer));
        if (mode === 'close') identity &&= timer.close() === timer;
        if (mode === 'dispose') identity &&= timer[Symbol.dispose]() === undefined;
        if (mode === 'refresh') await second;
        // The first firing was explicitly observed. This later timer is the
        // observation boundary for a cancelled reactivation, not an entry guess.
        await new Promise(resolve => timers.setTimeout(resolve, 15));
        result.push({ mode, calls, identity, receiver, argument });
      }
      let intervals = 0, immediateReceiver = false, cancelledImmediate = 0;
      await new Promise(resolve => {
        timers.setInterval(function() {
          if (++intervals === 2) { timers.clearInterval(this); resolve(); }
        }, 1);
      });
      await new Promise(resolve => {
        const cancelled = timers.setImmediate(() => cancelledImmediate++);
        timers.clearImmediate(cancelled);
        const disposed = timers.setImmediate(() => cancelledImmediate++);
        disposed[Symbol.dispose]();
        const immediate = timers.setImmediate(function() {
          immediateReceiver = this === immediate; resolve();
        });
      });
      return { result, intervals, immediateReceiver, cancelledImmediate };
    };
  `);
  assert.deepEqual(await call(session, 'probe'), {
    result: ['refresh', 'object', 'number', 'close', 'dispose', 'clear-before-refresh']
      .map(mode => ({ mode, calls: mode === 'refresh' ? 2 : 1,
        identity: true, receiver: true, argument: true })),
    intervals: 2, immediateReceiver: true, cancelledImmediate: 0,
  });
  assert.equal(session.timers.size, 0, 'completed and cancelled handles have no session owner');
  await session.stop();
  assert.equal(session.timers.size, 0);
});

test('Cat borrowed timer methods track the actual receiver and leave unknown native receivers native', async t => {
  const session = await sessionFor(t, counterSource), timers = session.node.builtins.timers;
  let borrowed;
  for (const mode of ['refresh', 'object', 'number', 'close', 'dispose']) {
    let a, b, aCalls = 0, bCalls = 0, firstA, firstB, secondB, receiver = true, argument = true;
    const aFired = new Promise(resolve => { firstA = resolve; });
    const bFired = new Promise(resolve => { firstB = resolve; });
    const bAgain = new Promise(resolve => { secondB = resolve; });
    a = timers.setTimeout(function(value) {
      receiver &&= this === a; argument &&= value === 'a';
      aCalls++; firstA();
    }, 5, 'a');
    b = timers.setTimeout(function(value) {
      receiver &&= this === b; argument &&= value === 'b';
      if (++bCalls === 1) firstB(); else secondB();
    }, 5, 'b');
    await Promise.all([aFired, bFired]);
    assert.equal(session.timers.size, 0);
    borrowed = { refresh: a.refresh, close: a.close, dispose: a[Symbol.dispose] };
    // Cancelling the donor must not make its still-valid methods use its state
    // when they operate on a different, naturally completed native handle.
    timers.clearTimeout(a);
    assert.equal(borrowed.refresh.call(b), b, mode);
    assert.deepEqual([...session.timers.keys()], [b], mode);
    if (mode === 'object') timers.clearTimeout(b);
    if (mode === 'number') timers.clearTimeout(Number(b));
    if (mode === 'close') assert.equal(borrowed.close.call(b), b);
    if (mode === 'dispose') assert.equal(borrowed.dispose.call(b), undefined);
    if (mode === 'refresh') await bAgain;
    assert.equal(session.timers.size, 0, mode);
    if (mode !== 'refresh') {
      assert.equal(borrowed.refresh.call(b), b);
      assert.equal(session.timers.size, 0, 'cancelled receiver stays cancelled after another refresh');
    }
    await new Promise(resolve => timers.setTimeout(resolve, 15));
    assert.deepEqual({ aCalls, bCalls, receiver, argument },
      { aCalls: 1, bCalls: mode === 'refresh' ? 2 : 1, receiver: true, argument: true }, mode);
  }
  for (const mode of ['refresh', 'close', 'dispose']) {
    let calls = 0, first, second, receiver = true;
    const fired = new Promise(resolve => { first = resolve; });
    const again = new Promise(resolve => { second = resolve; });
    const native = nativeTimers.setTimeout(function() {
      receiver &&= this === native;
      if (++calls === 1) first(); else second();
    }, 5);
    try {
      await fired;
      assert.equal(borrowed.refresh.call(native), native);
      assert.equal(session.timers.size, 0, 'a foreign native handle is not adopted');
      if (mode === 'close') assert.equal(borrowed.close.call(native), native);
      if (mode === 'dispose') assert.equal(borrowed.dispose.call(native), undefined);
      if (mode === 'refresh') await again;
      await new Promise(resolve => nativeTimers.setTimeout(resolve, 15));
      assert.equal(calls, mode === 'refresh' ? 2 : 1, mode);
      assert.equal(receiver, true);
      assert.equal(session.timers.size, 0);
    } finally { nativeTimers.clearTimeout(native); }
  }
  assert.deepEqual(await call(session, 'home'), { count: 1 });
  await session.stop();
  assert.equal(session.timers.size, 0);
});

test('Cat stop clears a reactivated native timer before cooperative Worker exit and keeps B', { timeout: 30000 },
  async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t),
      limits: { cleanupTimeoutMs: 100, initTimeoutMs: 10000, requestTimeoutMs: 10000 } });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'a', source: `
      let timer, calls = 0;
      module.exports.arm = async () => {
        await new Promise(resolve => {
          timer = setTimeout(() => { calls++; resolve(); }, 1000);
        });
        timer.refresh();
        return { calls };
      };
    ` });
    const b = await runtime.install({ installationId: 'b', source: counterSource });
    const owner = runtime.getSession('a'), worker = owner.worker, bOwner = runtime.getSession('b');
    assert.deepEqual(await invoke(runtime, 'a', 'arm'), { calls: 1 });
    owner.stats();
    await owner.statsRequest;
    assert.equal(owner.statsSnapshot.timers, 1);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: 1 });
    const exited = once(worker, 'exit');
    await runtime.setEnabled('a', false);
    assert.deepEqual(await exited, [0], 'a cooperative stop must not need Worker.terminate');
    assert.equal(worker.threadId, -1);
    assert.equal(owner.worker, null);
    assert.equal(owner.cleanupError, null);
    assert.equal(owner.stats().timers, 0);
    assert.equal(runtime.getSession('b'), bOwner);
    assert.equal(runtime.instance('b').generation, b.generation);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: 2 });
  });

async function http2For(t) {
  const server = http2.createServer(), sessions = new Set();
  server.on('session', session => {
    sessions.add(session);
    session.on('error', () => {});
    session.once('close', () => sessions.delete(session));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    const closed = new Promise(resolve => server.close(resolve));
    for (const session of sessions) session.destroy();
    await closed;
  });
  return { server, origin: `http://127.0.0.1:${server.address().port}`, sessions };
}

function callbackSource(http2Origin) {
  return `
    'use strict';
    const net = require('net'), http = require('http'), http2 = require('http2');
    const dc = require('diagnostics_channel');
    let release, completion, observed, setupError, count = 0;
    const turn = () => new Promise(resolve => setImmediate(resolve));
    function prepare({ kind, mode }) {
      let done, resource, unsubscribe;
      completion = new Promise(resolve => { done = resolve; });
      observed = null; setupError = null;
      function callback(...args) {
        observed = { kind, receiver: this === undefined ? 'undefined' : this === resource ? 'resource' : 'other',
          arguments: kind === 'connections' ? [args[0], args[1]]
            : kind === 'not-running' ? [args[0].code]
            : kind === 'http2' ? [args[0] === resource, typeof args[1]]
            : kind.startsWith('diagnostics') ? [args[0].marker, args[1] === 'meow-callback-owner']
            : args.length };
        if (kind === 'http2') resource.close();
        else if (kind === 'adapter') resource.destroy();
        else if (resource?.listening) resource.close();
        unsubscribe?.();
        done();
        if (mode === 'sync') throw new Error('synthetic callback failure');
        if (mode === 'async') return Promise.reject(new Error('synthetic callback failure'));
      }
      const begin = () => {
        if (kind === 'http2') {
          resource = http2.connect(${JSON.stringify(http2Origin)}, {}, callback);
          resource.on('error', () => {});
        } else if (kind === 'adapter') {
          resource = http.request('http://held.invalid/request');
          resource.on('error', () => {});
          resource.setTimeout(5, callback);
          resource.end();
        } else {
          resource = net.createServer();
          if (kind === 'event') { resource.once('listening', callback); resource.listen(0); }
          if (kind === 'listen') resource.listen(0, callback);
          if (kind === 'close') resource.listen(0, () => resource.close(callback));
          if (kind === 'connections') resource.getConnections(callback);
          if (kind === 'not-running') resource.close(callback);
        }
      };
      if (kind.startsWith('diagnostics')) {
        resource = dc.channel('meow-callback-owner');
        if (kind === 'diagnostics-module') {
          dc.subscribe(resource.name, callback);
          unsubscribe = () => dc.unsubscribe(resource.name, callback);
        } else {
          resource.subscribe(callback);
          unsubscribe = () => resource.unsubscribe(callback);
        }
        return () => resource.publish({ marker: 'value' });
      }
      return begin;
    }
    module.exports.register = input => {
      const fire = prepare(input);
      const gate = new Promise(resolve => { release = resolve; });
      gate.then(fire).catch(error => { setupError = error.code || 'SETUP_ERROR'; });
      return { registered: true };
    };
    module.exports.publish = async () => {
      release();
      await completion;
      await turn();
      return { observed, setupError };
    };
    module.exports.active = input => {
      prepare(input)();
      if (input.mode === 'normal') return completion.then(turn).then(() => ({ observed, setupError }));
      return new Promise(() => {});
    };
    module.exports.remove = async ({ kind }) => {
      let calls = 0;
      const callback = () => { calls++; };
      const resource = kind === 'http2'
        ? http2.connect(${JSON.stringify(http2Origin)}, callback)
        : http.request('http://held.invalid/removed');
      resource.on('error', () => {});
      const event = kind === 'http2' ? 'connect' : 'timeout';
      if (kind === 'adapter') resource.setTimeout(5, callback);
      const registered = resource.listeners(event).includes(callback);
      resource.removeListener(event, callback);
      await new Promise(resolve => {
        resource.once(event, resolve);
        if (kind === 'adapter') resource.end();
      });
      await new Promise(resolve => {
        resource.once('close', resolve);
        if (kind === 'http2') resource.close(); else resource.destroy();
      });
      return { registered, calls };
    };
    module.exports.home = () => ({ count: ++count });
  `;
}

test('Cat probe, HTTP2 and diagnostics callbacks keep active and retired request failures on their owner',
  { timeout: 60000 }, async t => {
    const upstream = await http2For(t), events = [];
    const runtime = createCatRuntime({ dataRoot: rootFor(t),
      limits: { initTimeoutMs: 10000, requestTimeoutMs: 3000, cleanupTimeoutMs: 1000 },
      onDiagnostic: event => events.push(event) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'a', source: callbackSource(upstream.origin) });
    await runtime.install({ installationId: 'b', source: counterSource });
    const a = runtime.getSession('a'), b = runtime.getSession('b'), worker = a.worker;
    let aCount = 0, bCount = 0;
    const expected = kind => ({ observed: {
      kind, receiver: ['connections', 'not-running', 'diagnostics-module', 'diagnostics-channel']
        .includes(kind) ? 'undefined' : 'resource',
      arguments: kind === 'connections' ? [null, 0] : kind === 'not-running' ? ['ERR_SERVER_NOT_RUNNING']
        : kind === 'http2' ? [true, 'object'] : kind.startsWith('diagnostics') ? ['value', true] : 0,
    }, setupError: null });
    for (const kind of ['event', 'listen', 'close', 'connections', 'not-running', 'http2',
      'diagnostics-module', 'diagnostics-channel']) {
      for (const mode of ['normal', 'sync', 'async']) {
        await t.test(`${kind} ${mode}`, async () => {
          const before = events.length;
          assert.deepEqual(await invoke(runtime, 'a', 'register', { kind, mode }), { registered: true });
          assert.deepEqual(await invoke(runtime, 'a', 'publish'), expected(kind));
          const retired = events.slice(before).filter(event => event.kind === 'ScriptCallbackError');
          assert.equal(retired.length, mode === 'normal' ? 0 : 1);
          if (retired.length) {
            assert.equal(retired[0].installationId, 'a');
            assert.equal(retired[0].generation, a.generation);
            assert.equal(retired[0].siteId, 'default');
            assert.ok(retired[0].requestId > 0);
            assert.equal(retired[0].code, 'SCRIPT_EXCEPTION');
          }
          if (mode === 'normal')
            assert.deepEqual(await invoke(runtime, 'a', 'active', { kind, mode }), expected(kind));
          else await assert.rejects(invoke(runtime, 'a', 'active', { kind, mode }),
            { code: 'SCRIPT_EXCEPTION' });
          assert.equal(runtime.getSession('a'), a);
          assert.equal(a.worker, worker);
          assert.equal(runtime.instance('a').state, 'ready');
          assert.equal(runtime.instance('a').generation, a.generation);
          assert.deepEqual(await invoke(runtime, 'a', 'home'), { count: ++aCount });
          assert.equal(runtime.getSession('b'), b);
          assert.equal(runtime.instance('b').generation, b.generation);
          assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: ++bCount });
        });
      }
    }
    assert.deepEqual(await invoke(runtime, 'a', 'remove', { kind: 'http2' }),
      { registered: true, calls: 0 });
    assert.deepEqual(events.filter(event => event.operation === 'worker.exit'), []);
    const exited = once(worker, 'exit');
    const disconnected = Promise.all([...upstream.sessions].map(session => once(session, 'close')));
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    await disconnected;
    assert.equal(upstream.sessions.size, 0);
    assert.equal(a.cleanupError, null);
    assert.equal(a.adapterTasks.size, 0);
    assert.equal(b.worker, null);
  });

test('Cat adapter timeout callbacks preserve their receiver, request attribution and physical drain',
  { timeout: 30000 }, async t => {
    const tasks = [], events = [];
    const runtime = createCatRuntime({ dataRoot: rootFor(t),
      limits: { initTimeoutMs: 10000, requestTimeoutMs: 3000, cleanupTimeoutMs: 1000 },
      network: input => new Promise(resolve => {
        const aborted = new Promise(done => {
          if (input.signal.aborted) done();
          else input.signal.addEventListener('abort', done, { once: true });
        });
        tasks.push({ input, resolve, aborted });
      }),
      onDiagnostic: event => events.push(event) });
    t.after(async () => {
      for (const task of tasks) task.resolve({ statusCode: 200, body: 'released' });
      await runtime.close();
    });
    await runtime.install({ installationId: 'a', source: callbackSource('http://127.0.0.1:1') });
    await runtime.install({ installationId: 'b', source: counterSource });
    const a = runtime.getSession('a'), b = runtime.getSession('b'), worker = a.worker;
    let count = 0;
    for (const mode of ['normal', 'sync', 'async']) {
      for (const retired of [false, true]) {
        const before = events.length, taskCount = tasks.length;
        const input = { kind: 'adapter', mode };
        if (retired) await invoke(runtime, 'a', 'register', input);
        const pending = invoke(runtime, 'a', retired ? 'publish' : 'active', input);
        if (!retired && mode !== 'normal') await assert.rejects(pending, { code: 'SCRIPT_EXCEPTION' });
        else assert.deepEqual(await pending, {
          observed: { kind: 'adapter', receiver: 'resource', arguments: 0 }, setupError: null,
        });
        assert.equal(tasks.length, taskCount + 1);
        const task = tasks.at(-1);
        await task.aborted;
        // An invoke result is not the physical adapter completion.
        assert.equal(a.adapterTasks.size, 1);
        task.resolve({ statusCode: 200, body: 'released' });
        await a.whenAdaptersIdle();
        assert.equal(a.adapterTasks.size, 0);
        assert.equal(task.input.signal.aborted, true);
        const failures = events.slice(before).filter(event => event.kind === 'ScriptCallbackError');
        assert.equal(failures.length, retired && mode !== 'normal' ? 1 : 0);
        for (const failure of failures) {
          assert.equal(failure.installationId, 'a');
          assert.equal(failure.generation, a.generation);
          assert.equal(failure.siteId, 'default');
          assert.ok(failure.requestId > 0);
          assert.equal(failure.code, 'SCRIPT_EXCEPTION');
        }
        assert.equal(runtime.getSession('a'), a);
        assert.equal(a.worker, worker);
        assert.deepEqual(await invoke(runtime, 'a', 'home'), { count: ++count });
        assert.equal(runtime.getSession('b'), b);
        assert.deepEqual(await invoke(runtime, 'b', 'home'), { count });
      }
    }
    const taskCount = tasks.length;
    assert.deepEqual(await invoke(runtime, 'a', 'remove', { kind: 'adapter' }),
      { registered: true, calls: 0 });
    assert.equal(tasks.length, taskCount + 1);
    const removed = tasks.at(-1);
    await removed.aborted;
    assert.equal(a.adapterTasks.size, 1);
    removed.resolve({ statusCode: 200, body: 'released' });
    await a.whenAdaptersIdle();
    assert.equal(a.adapterTasks.size, 0);
    const exited = once(worker, 'exit');
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    assert.equal(a.cleanupError, null);
    assert.deepEqual(events.filter(event => event.operation === 'worker.exit'), []);
  });

test('Cat diagnostics channels keep native identity, duplicate subscription removal and stop ownership',
  async t => {
    const name = `meow-lifecycle-${Date.now()}-${Math.random()}`;
    const session = await sessionFor(t, `
      'use strict';
      const dc = require('diagnostics_channel'), name = ${JSON.stringify(name)};
      module.exports.probe = () => {
        const channel = dc.channel(name);
        const publish = value => channel.publish(value);
        let calls = 0, receiver = true, argumentsMatch = true;
        const callback = function(value, key) {
          calls++; receiver &&= this === undefined;
          argumentsMatch &&= value.marker === 'input' && key === name;
        };
        publish({ marker: 'input' });
        const inactiveInstance = channel instanceof dc.Channel;
        dc.subscribe(name, callback);
        channel.subscribe(callback);
        publish({ marker: 'input' });
        const both = calls, activeInstance = channel instanceof dc.Channel;
        const first = dc.unsubscribe(name, callback);
        publish({ marker: 'input' });
        const one = calls, second = channel.unsubscribe(callback);
        publish({ marker: 'input' });
        const none = calls, absent = channel.unsubscribe(callback);
        channel.subscribe(callback);
        publish({ marker: 'input' });
        const reactivated = calls;
        const direct = new dc.Channel(name + '-new');
        direct.subscribe(() => {});
        const tracing = dc.tracingChannel(name + '-trace');
        tracing.subscribe({ start: () => {} });
        return { same: channel === dc.channel(name), inactiveInstance, activeInstance,
          both, one, none, first, second, absent, reactivated, receiver, argumentsMatch,
          direct: direct === dc.channel(name + '-new') && direct instanceof dc.Channel,
          trace: tracing.traceSync(() => 17) };
      };
    `);
    assert.deepEqual(await call(session, 'probe'), {
      same: true, inactiveInstance: true, activeInstance: true, both: 2, one: 3, none: 3,
      first: true, second: true, absent: false, reactivated: 4, receiver: true,
      argumentsMatch: true, direct: true, trace: 17,
    });
    const channels = [name, name + '-new', `tracing:${name}-trace:start`].map(diagnostics.channel);
    assert.ok(channels.every(channel => channel.hasSubscribers));
    let terminalCalls = 0;
    const retained = session.node.builtins.diagnostics_channel.channel(name + '-retained');
    const callback = () => { terminalCalls++; };
    retained.subscribe(callback);
    const raw = diagnostics.channel(name + '-retained');
    raw.publish({});
    assert.equal(terminalCalls, 1);
    await session.stop();
    assert.equal(session.node.subscriptions.size, 0);
    assert.ok([...channels, raw].every(channel => !channel.hasSubscribers));
    raw.publish({});
    assert.equal(terminalCalls, 1);
    assert.equal(retained.unsubscribe(callback), false);
  });

test('Cat probe listener removal and terminal callbacks preserve cooperative stop writes', { timeout: 30000 },
  async t => {
    const dataRoot = rootFor(t), runtime = createCatRuntime({ dataRoot });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'a', source: `
      const net = require('net'), fs = require('fs');
      let server, closed, closes = 0, removed = 0;
      module.exports.arm = async () => {
        server = net.createServer();
        const unused = () => { removed++; };
        server.on('listening', unused);
        server.removeListener('listening', unused);
        closed = new Promise(resolve => server.once('close', () => { closes++; resolve(); }));
        await new Promise(resolve => {
          server.listen(0, unused);
          server.removeListener('listening', unused);
          server.once('listening', resolve);
        });
        return { removed, listening: server.listening };
      };
      module.exports.stop = async () => {
        let receiver;
        await new Promise((resolve, reject) => server.close(function(error) {
          receiver = this === server;
          if (error) reject(error); else resolve();
        }));
        await closed;
        fs.writeFileSync('/close.json', JSON.stringify({ closes, removed, receiver }));
      };
    ` });
    await runtime.install({ installationId: 'b', source: counterSource });
    assert.deepEqual(await invoke(runtime, 'a', 'arm'), { removed: 0, listening: true });
    const a = runtime.getSession('a'), worker = a.worker, b = runtime.getSession('b');
    const exited = once(worker, 'exit');
    await runtime.setEnabled('a', false);
    assert.deepEqual(await exited, [0]);
    assert.equal(a.cleanupError, null);
    assert.deepEqual(JSON.parse(readFileSync(join(dataRoot, 'volumes', 'a', 'close.json'), 'utf8')),
      { closes: 1, removed: 0, receiver: true });
    assert.equal(runtime.getSession('b'), b);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: 1 });
  });

test('Cat fetch passes unknown encoding bytes and headers through and still decodes gzip', { timeout: 30000 },
  async t => {
    const bytes = Buffer.from([0, 255, 65, 128]);
    const upstream = await serverFor(t, (request, response) => {
      const encoding = request.url.slice(1);
      response.writeHead(200, { 'content-encoding': encoding, 'x-fixture': 'kept' });
      response.end(encoding === 'gzip' ? gzipSync(bytes) : bytes);
    });
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'a', source: `
      module.exports.probe = async ({ encoding }) => {
        const response = await fetch(${JSON.stringify(upstream.socketOrigin)} + '/' + encoding);
        return { status: response.status, encoding: response.headers.get('content-encoding'),
          marker: response.headers.get('x-fixture'), bytes: [...new Uint8Array(await response.arrayBuffer())] };
      };
      let count = 0;
      module.exports.home = () => ({ count: ++count });
    ` });
    const a = runtime.getSession('a'), worker = a.worker;
    for (const encoding of ['constructor', '__proto__', 'x-unknown', 'gzip'])
      assert.deepEqual(await invoke(runtime, 'a', 'probe', { encoding }),
        { status: 200, encoding, marker: 'kept', bytes: [...bytes] });
    assert.equal(runtime.getSession('a'), a);
    assert.deepEqual(await invoke(runtime, 'a', 'home'), { count: 1 });
    const exited = once(worker, 'exit');
    const disconnected = Promise.all([...upstream.sockets].map(socket => once(socket, 'close')));
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    await disconnected;
    assert.equal(worker.threadId, -1);
    assert.equal(a.cleanupError, null);
    assert.equal(a.adapterTasks.size, 0);
    assert.equal(upstream.sockets.size, 0);
  });

test('Cat diagnostics stores preserve synchronous ALS values, replacement, unbind and stop ownership',
  async t => {
    const name = `meow-store-${Date.now()}-${Math.random()}`;
    const session = await sessionFor(t, `
      'use strict';
      const dc = require('diagnostics_channel'), { AsyncLocalStorage } = require('async_hooks');
      const channel = dc.channel(${JSON.stringify(name)}), store = new AsyncLocalStorage();
      let first = 0, second = 0, receiver = true, argumentsMatch = true;
      const transform = function(value) {
        first++; receiver &&= this === undefined; argumentsMatch &&= arguments.length === 1;
        return { marker: value.marker + '-first' };
      };
      module.exports.probe = () => {
        const bound = channel.bindStore(store, transform);
        const target = {};
        const before = channel.runStores({ marker: 'input' }, function(argument) {
          return [store.getStore().marker, this === target, argument];
        }, target, 17);
        channel.bindStore(store, value => { second++; return { marker: value.marker + '-second' }; });
        const replaced = channel.runStores({ marker: 'input' }, () => store.getStore().marker);
        const removed = channel.unbindStore(store), absent = channel.unbindStore(store);
        const after = channel.runStores({}, () => store.getStore() === undefined);
        channel.bindStore(store);
        const input = { marker: 'default' };
        const defaultTransform = channel.runStores(input, () => store.getStore() === input);
        const tracing = dc.tracingChannel(${JSON.stringify(name)} + '-trace');
        tracing.start.bindStore(store, value => ({ marker: value.marker + '-trace' }));
        const traced = tracing.traceSync(() => store.getStore().marker, { marker: 'input' });
        return { bound: bound === undefined, before, replaced, removed, absent, after,
          defaultTransform, traced, first, second, receiver, argumentsMatch };
      };
    `);
    assert.deepEqual(await call(session, 'probe'), {
      bound: true, before: ['input-first', true, 17], replaced: 'input-second',
      removed: true, absent: false, after: true, defaultTransform: true, traced: 'input-trace',
      first: 1, second: 1, receiver: true, argumentsMatch: true,
    });
    const store = new AsyncLocalStorage(), retained = session.node.builtins.diagnostics_channel
      .channel(name + '-retained'), raw = diagnostics.channel(name + '-retained');
    let calls = 0;
    retained.bindStore(store, value => { calls++; return value; });
    assert.equal(raw.runStores(7, () => store.getStore()), 7);
    const channels = [diagnostics.channel(name), diagnostics.channel(`tracing:${name}-trace:start`), raw];
    assert.ok(channels.every(channel => channel.hasSubscribers));
    await session.stop();
    assert.ok(channels.every(channel => !channel.hasSubscribers));
    assert.equal(raw.runStores(9, () => store.getStore()), undefined);
    assert.equal(calls, 1, 'stop unbinds the actual native channel, not just its bookkeeping');
    assert.equal(retained.unbindStore(store), false);
  });

async function callbackRuntime(t, source) {
  const events = [], runtime = createCatRuntime({ dataRoot: rootFor(t),
    limits: { initTimeoutMs: 10000, requestTimeoutMs: 3000, cleanupTimeoutMs: 1000 },
    onDiagnostic: event => events.push(event) });
  t.after(() => runtime.close());
  await runtime.install({ installationId: 'a', source });
  await runtime.install({ installationId: 'b', source: counterSource });
  return { runtime, events };
}

async function ownerCases(t, runtime, events, kinds, check, modes = ['normal', 'sync', 'async']) {
  const a = runtime.getSession('a'), b = runtime.getSession('b'), worker = a.worker;
  let count = 0;
  for (const kind of kinds) for (const mode of modes) {
    await t.test(`${kind} ${mode}`, async () => {
      for (const retired of [false, true]) {
        const before = events.length, input = { kind, mode };
        if (retired) assert.deepEqual(await invoke(runtime, 'a', 'register', input), { registered: true });
        const pending = invoke(runtime, 'a', retired ? 'fire' : 'active', input);
        if (!retired && mode !== 'normal') await assert.rejects(pending, { code: 'SCRIPT_EXCEPTION' });
        else check(await pending, kind, mode, retired);
        check(await invoke(runtime, 'a', 'settle'), kind, mode, retired);
        const failures = events.slice(before).filter(event => event.kind === 'ScriptCallbackError');
        assert.equal(failures.length, retired && mode !== 'normal' ? 1 : 0);
        for (const failure of failures) {
          assert.equal(failure.installationId, 'a');
          assert.equal(failure.generation, a.generation);
          assert.equal(failure.siteId, 'default');
          assert.ok(failure.requestId > 0);
          assert.equal(failure.code, 'SCRIPT_EXCEPTION');
        }
        assert.equal(runtime.getSession('a'), a);
        assert.equal(a.worker, worker);
        assert.equal(runtime.instance('a').state, 'ready');
        assert.equal(runtime.instance('a').generation, a.generation);
        assert.deepEqual(await invoke(runtime, 'a', 'home'), { count: ++count });
        assert.equal(runtime.getSession('b'), b);
        assert.equal(runtime.instance('b').generation, b.generation);
        assert.deepEqual(await invoke(runtime, 'b', 'home'), { count });
      }
    });
  }
  assert.deepEqual(events.filter(event => event.operation === 'worker.exit'), []);
  return { a, b, worker, count };
}

// Gates are registered in A, released only after its public result, and then
// awaited by another action. They never guess request retirement with a timer.
const completionActions = `
  let release, completion, count = 0;
  const turn = () => new Promise(resolve => setImmediate(resolve));
  module.exports.register = input => {
    completion = new Promise(resolve => { release = resolve; }).then(() => perform(input));
    return { registered: true };
  };
  module.exports.fire = () => { release(); return completion; };
  module.exports.active = input => {
    completion = perform(input);
    return input.mode === 'normal' ? completion : completion.then(() => new Promise(() => {}));
  };
  module.exports.settle = () => completion;
  module.exports.home = () => ({ count: ++count });
`;

test('Cat diagnostics store transform errors keep active and retired request ownership without Worker exit',
  { timeout: 30000 }, async t => {
    const { runtime, events } = await callbackRuntime(t, `
      'use strict';
      const dc = require('diagnostics_channel'), { AsyncLocalStorage } = require('async_hooks');
      const channel = dc.channel('meow-store-owner'), store = new AsyncLocalStorage();
      let calls = 0;
      async function perform({ mode }) {
        calls = 0;
        channel.bindStore(store, value => {
          calls++;
          if (mode === 'sync') throw new Error('synthetic transform failure');
          return value.marker;
        });
        const value = channel.runStores({ marker: 'context' }, () => ({
          context: store.getStore() ?? null, answer: 17,
        }));
        channel.unbindStore(store);
        await turn();
        return { calls, answer: value.answer, context: value.context };
      }
      ${completionActions}
    `);
    const { a, b, worker, count } = await ownerCases(t, runtime, events, ['store'], (result, _kind, mode) => {
      assert.equal(result.calls, 1);
      assert.equal(result.answer, 17);
      assert.equal(result.context, mode === 'normal' ? 'context' : null);
    }, ['normal', 'sync']);
    // Here registration and runStores belong to different invocations, not
    // merely different turns of the same native AsyncLocalStorage continuation.
    await runtime.update({ installationId: 'a', source: `
      const channel = require('diagnostics_channel').channel('meow-store-late');
      const store = new (require('async_hooks').AsyncLocalStorage)();
      module.exports.register = () => {
        channel.bindStore(store, () => { throw new Error('retired transform'); });
        return { registered: true };
      };
      module.exports.fire = () => channel.runStores({}, () => ({ answer: 17 }));
      module.exports.home = () => ({ ready: true });
    ` });
    assert.equal(worker.threadId, -1);
    assert.equal(a.worker, null);
    const next = runtime.getSession('a'), nextWorker = next.worker, before = events.length;
    await invoke(runtime, 'a', 'register');
    assert.deepEqual(await invoke(runtime, 'a', 'fire'), { answer: 17 });
    assert.deepEqual(await invoke(runtime, 'a', 'home'), { ready: true });
    assert.equal(events.slice(before).filter(event => event.kind === 'ScriptCallbackError').length, 1);
    assert.equal(runtime.getSession('a'), next);
    assert.equal(next.worker, nextWorker);
    assert.equal(runtime.getSession('b'), b);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: count + 1 });
    const exited = once(nextWorker, 'exit');
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    assert.equal(next.cleanupError, null);
  });

async function tcpFor(t) {
  const sockets = new Set(), bodies = [];
  const server = net.createServer(socket => {
    sockets.add(socket);
    const chunks = [];
    socket.on('data', chunk => chunks.push(chunk));
    socket.on('error', () => {});
    socket.once('end', () => bodies.push(Buffer.concat(chunks)));
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    const closed = new Promise(resolve => server.close(resolve));
    for (const socket of sockets) socket.destroy();
    await closed;
  });
  return { port: server.address().port, sockets, bodies };
}

function writableSource(port, origin) {
  return `
    'use strict';
    const { Writable } = require('stream'), fs = require('fs');
    const net = require('net'), http = require('http');
    const bytes = Buffer.from([0, 255, 65, 128]);
    let file = 0;
    async function perform({ kind, mode }) {
      const [type, method] = kind.split('-');
      let resource, client, server, path, observed, calls = 0, delivered;
      const chunks = [], closes = [], errors = [];
      const watch = value => {
        value.on('error', error => errors.push(error.code || 'ERROR'));
        closes.push(new Promise(resolve => value.once('close', resolve)));
        return value;
      };
      if (type === 'writable') resource = watch(new Writable({
        write(chunk, encoding, done) { chunks.push(Buffer.from(chunk)); done(); },
      }));
      if (type === 'private') {
        path = '/completion-' + (++file);
        resource = watch(fs.createWriteStream(path));
      }
      if (type === 'socket') {
        resource = watch(net.connect({ host: '127.0.0.1', port: ${port} }));
        resource.resume();
        await new Promise(resolve => resource.once('connect', resolve));
      }
      if (type === 'request') {
        resource = watch(http.request(${JSON.stringify(origin)}, { method: 'POST', agent: false },
          response => {
            response.on('error', error => errors.push(error.code || 'ERROR')); response.resume();
          }));
      }
      if (type === 'response' || type === 'accepted') {
        const ready = new Promise(resolve => { delivered = resolve; });
        server = http.createServer((_request, response) => delivered(watch(response)));
        if (type === 'accepted') server.once('connection', socket => delivered(watch(socket)));
        await new Promise(resolve => server.listen(0, resolve));
        client = type === 'accepted'
          ? watch(net.connect({ host: '127.0.0.1', port: server.address().port }))
          : watch(http.get('http://127.0.0.1:' + server.address().port, { agent: false },
            response => {
              response.on('error', error => errors.push(error.code || 'ERROR'));
              response.on('data', chunk => chunks.push(Buffer.from(chunk)));
            }));
        if (type === 'accepted') client.on('data', chunk => chunks.push(Buffer.from(chunk)));
        resource = await ready;
      }
      let done;
      const completed = new Promise(resolve => { done = resolve; });
      const callback = function(...args) {
        const buffered = this !== null && typeof this === 'object'
          && Object.hasOwn(this, 'chunk') && Object.hasOwn(this, 'encoding') && Object.hasOwn(this, 'callback')
          && Buffer.isBuffer(this.chunk) && this.encoding === 'buffer' && typeof this.callback === 'function';
        observed = { calls: ++calls, receiver: this === undefined ? 'undefined'
          : Array.isArray(this) ? 'array' : this === resource ? 'resource'
          : buffered ? { type: 'buffered-entry', chunk: [...this.chunk],
            encoding: this.encoding, callback: typeof this.callback } : 'other',
          argc: args.length, error: args[0]?.code ?? null };
        done();
        if (mode === 'sync') throw new Error('synthetic completion failure');
        if (mode === 'async') return Promise.reject(new Error('synthetic completion failure'));
      };
      const value = resource[method](bytes, callback);
      const result = method === 'write' ? typeof value : value === resource;
      if (method === 'write') resource.end();
      await completed;
      await Promise.all(closes);
      if (server) await new Promise(resolve => server.close(resolve));
      const content = type === 'private' ? [...fs.readFileSync(path)]
        : ['writable', 'response', 'accepted'].includes(type) ? [...Buffer.concat(chunks)] : null;
      await turn();
      return { ...observed, result, closed: resource.closed ?? resource.destroyed, content, errors };
    }
    ${completionActions}
  `;
}

test('Cat Writable completions keep ownership across native streams, sockets, HTTP and private files',
  { timeout: 60000 }, async t => {
    const received = [], tcp = await tcpFor(t), upstream = await serverFor(t, (request, response) => {
      const chunks = [];
      request.on('data', chunk => chunks.push(chunk));
      request.once('end', () => { received.push(Buffer.concat(chunks)); response.end('ok'); });
    });
    const { runtime, events } = await callbackRuntime(t, writableSource(tcp.port, upstream.socketOrigin));
    const kinds = ['writable', 'private', 'socket', 'request', 'response', 'accepted']
      .flatMap(type => [`${type}-write`, `${type}-end`]);
    const { a, worker } = await ownerCases(t, runtime, events, kinds, (result, kind, mode, retired) => {
      assert.equal(result.calls, 1);
      assert.equal(result.argc, 1);
      assert.equal(result.error, null);
      if (kind === 'request-write' || kind === 'response-write')
        assert.deepEqual(result.receiver, {
          type: 'buffered-entry', chunk: [13, 10], encoding: 'buffer', callback: 'function',
        });
      else assert.equal(result.receiver, kind.endsWith('-write') ? 'undefined' : 'array');
      assert.equal(result.result, kind.endsWith('-write') ? 'boolean' : true);
      assert.equal(result.closed, true);
      if (/^(writable|private)-/.test(kind) ||
        /^(response|accepted)-/.test(kind) && (mode === 'normal' || retired))
        assert.deepEqual(result.content, [0, 255, 65, 128]);
      else if (/^(response|accepted)-/.test(kind))
        assert.ok(Buffer.from([0, 255, 65, 128]).subarray(0, result.content.length)
          .equals(Buffer.from(result.content)), 'active failure may cancel transport but cannot alter bytes');
      else assert.equal(result.content, null);
      if (mode === 'normal' || retired) assert.deepEqual(result.errors, []);
    });
    const exited = once(worker, 'exit');
    const disconnected = Promise.all([...tcp.sockets, ...upstream.sockets]
      .map(socket => once(socket, 'close')));
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    await disconnected;
    assert.equal(a.cleanupError, null);
    assert.equal(a.adapterTasks.size, 0);
    assert.equal(tcp.sockets.size + upstream.sockets.size, 0);
    assert.ok(tcp.bodies.length > 0);
    assert.ok(tcp.bodies.every(body => body.equals(Buffer.from([0, 255, 65, 128]))));
    assert.ok(received.length > 0);
    assert.ok(received.every(body => body.equals(Buffer.from([0, 255, 65, 128]))));
  });

test('Cat held Writable callbacks preserve backpressure, retired failure identity and terminal stop writes',
  { timeout: 30000 }, async t => {
    const dataRoot = rootFor(t), events = [], runtime = createCatRuntime({ dataRoot,
      limits: { cleanupTimeoutMs: 1000, requestTimeoutMs: 3000, initTimeoutMs: 10000 },
      onDiagnostic: event => events.push(event) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'a', source: `
      'use strict';
      const { Writable } = require('stream'), fs = require('fs');
      let stream, finish, closed, writeDone, endDone, record, hold = true, terminal = false;
      module.exports.arm = ({ mode }) => {
        record = { writes: 0, ends: 0, writeError: null, endError: null };
        hold = true;
        terminal = mode === 'stop';
        stream = new Writable({ highWaterMark: 1, write(chunk, encoding, done) {
          if (hold) finish = done; else done();
        } });
        stream.on('error', () => {});
        closed = new Promise(resolve => stream.once('close', resolve));
        let written, ended;
        writeDone = new Promise(resolve => { written = resolve; });
        endDone = new Promise(resolve => { ended = resolve; });
        const writable = stream.write('x', function(error) {
          record.writes++; record.writeError = error?.code ?? null;
          record.writeReceiver = this === undefined; written();
          if (mode === 'sync') throw new Error('retired write failure');
          if (mode === 'async') return Promise.reject(new Error('retired write failure'));
        });
        if (mode === 'stop') stream.end(error => {
          record.ends++; record.endError = error?.code ?? null; ended();
        });
        return { writable };
      };
      module.exports.release = async () => {
        hold = false;
        finish();
        await writeDone;
        const writable = stream.write('y'), endReceiver = stream.end() === stream;
        await closed;
        await new Promise(resolve => setImmediate(resolve));
        return { ...record, writable, endReceiver };
      };
      module.exports.stop = async () => {
        if (!stream || stream.closed) return;
        if (!terminal) { stream.destroy(); finish(); await closed; return; }
        finish(Object.assign(new Error('terminal cancellation'), { code: 'CANCELLED' }));
        await Promise.all([writeDone, endDone, closed]);
        fs.writeFileSync('/terminal.json', JSON.stringify(record));
      };
      module.exports.home = () => ({ ready: true });
    ` });
    await runtime.install({ installationId: 'b', source: counterSource });
    const a = runtime.getSession('a'), b = runtime.getSession('b'), worker = a.worker;
    for (const mode of ['normal', 'sync', 'async']) {
      const before = events.length;
      assert.deepEqual(await invoke(runtime, 'a', 'arm', { mode }), { writable: false });
      assert.deepEqual(await invoke(runtime, 'a', 'release'), {
        writes: 1, ends: 0, writeError: null, endError: null, writeReceiver: true,
        writable: true, endReceiver: true,
      });
      assert.equal(events.slice(before).filter(event => event.kind === 'ScriptCallbackError').length,
        mode === 'normal' ? 0 : 1);
      assert.equal(a.worker, worker);
      assert.deepEqual(await invoke(runtime, 'a', 'home'), { ready: true });
    }
    assert.deepEqual(await invoke(runtime, 'a', 'arm', { mode: 'stop' }), { writable: false });
    const exited = once(worker, 'exit');
    await runtime.setEnabled('a', false);
    assert.deepEqual(await exited, [0]);
    assert.equal(a.cleanupError, null);
    assert.deepEqual(JSON.parse(readFileSync(join(dataRoot, 'volumes', 'a', 'terminal.json'), 'utf8')), {
      writes: 1, ends: 1, writeError: 'CANCELLED', endError: 'CANCELLED', writeReceiver: true,
    });
    assert.equal(runtime.getSession('b'), b);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: 1 });
  });

test('Cat HTTP2 ACK and HTTP Agent connection callbacks retain scope, native results and real close',
  { timeout: 60000 }, async t => {
    const upstream = await http2For(t), tcp = await tcpFor(t);
    const { runtime, events } = await callbackRuntime(t, `
      'use strict';
      const http2 = require('http2'), http = require('http');
      async function perform({ kind, mode }) {
        let resource, agent, observed, done, calls = 0;
        const errors = [];
        const completed = new Promise(resolve => { done = resolve; });
        const callback = function(...args) {
          observed = { calls: ++calls, receiver: this === resource ? 'resource' : this === undefined ? 'undefined' : 'other',
            argc: args.length, error: args[0]?.code ?? null };
          if (kind === 'ping') observed.payload = args[2].toString();
          if (kind === 'ping' || kind === 'settings') observed.duration = typeof args[2 - (kind === 'ping' ? 1 : 0)];
          if (kind === 'settings') observed.settings = args[1].maxConcurrentStreams;
          done();
          if (mode === 'sync') throw new Error('synthetic protocol completion failure');
          if (mode === 'async') return Promise.reject(new Error('synthetic protocol completion failure'));
        };
        let value;
        if (kind === 'agent') {
          agent = new http.Agent();
          resource = agent.createConnection({ host: '127.0.0.1', port: ${tcp.port} }, callback);
          value = resource === undefined ? 'undefined' : 'socket';
          resource.resume();
        } else resource = http2.connect(${JSON.stringify(upstream.origin)});
        resource.on('error', error => errors.push(error.code || 'ERROR'));
        const closed = new Promise(resolve => resource.once('close', resolve));
        if (kind !== 'agent') {
          if (kind !== 'ping-cancel') await new Promise(resolve => resource.once('connect', resolve));
          value = kind.startsWith('ping') ? resource.ping(Buffer.from('01234567'), callback)
            : resource.settings({ maxConcurrentStreams: 17 }, callback);
        }
        await completed;
        if (kind === 'agent') { resource.end(); agent.destroy(); }
        else if (!resource.destroyed) resource.close();
        await closed;
        await turn();
        return { ...observed, result: value === undefined ? 'undefined' : value,
          closed: resource.closed || resource.destroyed, errors };
      }
      ${completionActions}
    `);
    const { a, worker } = await ownerCases(t, runtime, events,
      ['ping', 'settings', 'ping-cancel', 'agent'], (result, kind) => {
        assert.equal(result.receiver, kind === 'agent' ? 'resource' : 'undefined');
        assert.equal(result.calls, 1);
        assert.equal(result.argc, kind === 'agent' ? 0 : kind.endsWith('-cancel') ? 1 : 3);
        assert.equal(result.error, kind === 'ping-cancel' ? 'ERR_HTTP2_PING_CANCEL' : null);
        assert.equal(result.result, kind === 'agent' ? 'socket' : kind === 'ping' ? true : 'undefined');
        assert.equal(result.closed, true);
        if (kind === 'ping') assert.equal(result.payload, '01234567');
        if (kind === 'ping' || kind === 'settings') assert.equal(result.duration, 'number');
        if (kind === 'settings') assert.equal(result.settings, 17);
        assert.deepEqual(result.errors, []);
      });
    const exited = once(worker, 'exit');
    const disconnected = Promise.all([...upstream.sessions, ...tcp.sockets].map(value => once(value, 'close')));
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    await disconnected;
    assert.equal(upstream.sessions.size + tcp.sockets.size, 0);
    assert.equal(a.cleanupError, null);
  });

test('Cat HTTP2 settings destruction before ACK matches native completion absence and keeps A/B owners',
  { timeout: 30000 }, async t => {
    const upstream = await http2For(t);
    const nativeDisconnected = new Promise(resolve =>
      upstream.server.once('session', peer => peer.once('close', resolve)));
    const native = http2.connect(upstream.origin);
    t.after(() => native.destroy());
    const oracle = { calls: 0, closed: false, errors: [] };
    native.on('error', error => oracle.errors.push(error.code || 'ERROR'));
    const nativeClosed = new Promise(resolve => native.once('close', () => {
      oracle.closed = true; resolve();
    }));
    await once(native, 'connect');
    oracle.result = typeof native.settings({ maxConcurrentStreams: 17 }, () => { oracle.calls++; });
    // Do not wait for the initial SETTINGS ACK before submitting or destroying.
    native.destroy();
    await nativeClosed;
    await nativeDisconnected;
    oracle.destroyed = native.destroyed;
    assert.deepEqual(oracle, { calls: 0, closed: true, errors: [], result: 'undefined', destroyed: true });
    assert.equal(upstream.sessions.size, 0);

    const { runtime, events } = await callbackRuntime(t, `
      'use strict';
      const http2 = require('http2');
      let calls = 0;
      async function perform() {
        const client = http2.connect(${JSON.stringify(upstream.origin)});
        const errors = [];
        let closed = false;
        client.on('error', error => errors.push(error.code || 'ERROR'));
        const completed = new Promise(resolve => client.once('close', () => {
          closed = true; resolve();
        }));
        await new Promise(resolve => client.once('connect', resolve));
        const result = typeof client.settings({ maxConcurrentStreams: 17 }, () => { calls++; });
        client.destroy();
        await completed;
        return { calls, closed, errors, result, destroyed: client.destroyed };
      }
      module.exports.callbacks = () => ({ calls });
      ${completionActions}
    `);
    const a = runtime.getSession('a'), b = runtime.getSession('b'), worker = a.worker;
    let count = 0;
    for (const retired of [false, true]) {
      if (retired) assert.deepEqual(await invoke(runtime, 'a', 'register', {}), { registered: true });
      const disconnected = new Promise(resolve =>
        upstream.server.once('session', peer => peer.once('close', resolve)));
      const result = await invoke(runtime, 'a', retired ? 'fire' : 'active', { mode: 'normal' });
      await disconnected;
      assert.deepEqual(result, oracle);
      assert.deepEqual(await invoke(runtime, 'a', 'settle'), oracle);
      assert.equal(upstream.sessions.size, 0);
      assert.equal(runtime.getSession('a'), a);
      assert.equal(a.worker, worker);
      assert.equal(runtime.instance('a').state, 'ready');
      assert.equal(runtime.instance('a').generation, a.generation);
      assert.deepEqual(await invoke(runtime, 'a', 'home'), { count: ++count });
      assert.equal(runtime.getSession('b'), b);
      assert.equal(runtime.instance('b').generation, b.generation);
      assert.deepEqual(await invoke(runtime, 'b', 'home'), { count });
      assert.deepEqual(await invoke(runtime, 'a', 'callbacks'), { calls: 0 });
    }
    assert.equal(oracle.calls, 0);
    assert.deepEqual(oracle.errors, []);
    assert.deepEqual(events.filter(event => event.kind === 'ScriptCallbackError'
      || event.operation === 'worker.exit'), []);
    const exited = once(worker, 'exit');
    await runtime.setEnabled('a', false);
    assert.deepEqual(await exited, [0]);
    assert.equal(worker.threadId, -1);
    assert.equal(a.worker, null);
    assert.equal(a.cleanupError, null);
    assert.equal(a.adapterTasks.size, 0);
    assert.equal(a.stats().sockets, 0);
    assert.equal(a.stats().networkRequests, 0);
    assert.equal(upstream.sessions.size, 0);
    assert.equal(runtime.getSession('b'), b);
    assert.equal(runtime.instance('b').generation, b.generation);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: ++count });
  });

test('Cat stop preserves native HTTP2 ping cancellation and absent settings completion through real close',
  { timeout: 30000 }, async t => {
    const upstream = await http2For(t), session = await sessionFor(t, counterSource);
    const outcomes = [];
    for (const managed of [false, true]) {
      const disconnected = new Promise(resolve =>
        upstream.server.once('session', peer => peer.once('close', resolve)));
      const client = managed ? session.node.builtins.http2.connect(upstream.origin)
        : http2.connect(upstream.origin);
      t.after(() => client.destroy());
      const errors = [], callbacks = { ping: [], settings: [] };
      client.on('error', error => errors.push(error.code));
      const closed = once(client, 'close');
      await once(client, 'connect');
      let pingDone;
      const ping = new Promise(resolve => { pingDone = resolve; });
      assert.equal(client.ping(Buffer.from('01234567'), function(error) {
        callbacks.ping.push({ code: error?.code, argc: arguments.length, receiver: this });
        pingDone();
      }), true);
      assert.equal(client.settings({ maxConcurrentStreams: 17 }, function(error) {
        callbacks.settings.push({ code: error?.code, argc: arguments.length, receiver: this });
      }), undefined);
      // No turn after submission. The native oracle determines which pending
      // completions exist; real close, not a timer, is the observation boundary.
      if (managed) await session.stop();
      else client.destroy();
      await ping;
      await closed;
      await disconnected;
      assert.deepEqual(callbacks.ping, [
        { code: 'ERR_HTTP2_PING_CANCEL', argc: 1, receiver: undefined },
      ]);
      assert.deepEqual(callbacks.settings, []);
      assert.equal(client.destroyed, true);
      assert.equal(upstream.sessions.size, 0);
      assert.deepEqual(errors, []);
      outcomes.push(callbacks);
    }
    assert.deepEqual(outcomes[1], outcomes[0]);
    assert.equal(session.node.networkResources.size, 0);
    assert.equal(session.services.connections.size, 0);
    assert.equal(session.nativeRequests.size, 0);
    assert.equal(session.node.streams.size, 0);
    assert.equal(session.cleanupError, undefined);
  });

// Only generated throw-away key material is used, inside the owning Worker.
// No private key fixture, external command or certificate service is needed.
const certificateSource = `
  const crypto = require('crypto');
  function tlsOptions() {
    const der = (tag, ...parts) => {
      const body = Buffer.concat(parts.map(part => Buffer.isBuffer(part) ? part : Buffer.from(part)));
      let length = body.length.toString(16);
      if (length.length % 2) length = '0' + length;
      const bytes = Buffer.from(length, 'hex');
      return Buffer.concat([Buffer.from([tag]), body.length < 128 ? Buffer.from([body.length])
        : Buffer.concat([Buffer.from([0x80 | bytes.length]), bytes]), body]);
    };
    const seq = (...values) => der(0x30, ...values);
    const algorithm = seq(der(6, Buffer.from('2a864886f70d01010b', 'hex')), der(5));
    const name = seq(der(0x31, seq(der(6, Buffer.from('550403', 'hex')), der(12, 'Cat fixture'))));
    const { publicKey, privateKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    const tbs = seq(der(2, [1]), algorithm, name,
      seq(der(0x17, '200101000000Z'), der(0x17, '400101000000Z')), name,
      publicKey.export({ format: 'der', type: 'spki' }));
    const bytes = seq(tbs, algorithm, der(3, [0], crypto.sign('sha256', tbs, privateKey)));
    return { key: privateKey.export({ format: 'pem', type: 'pkcs8' }),
      cert: '-----BEGIN CERTIFICATE-----\\n' + bytes.toString('base64').match(/.{1,64}/g).join('\\n') +
        '\\n-----END CERTIFICATE-----\\n' };
  }
`;

test('Cat TCP and TLS connect callbacks preserve removable identity, ownership and native connection completion',
  { timeout: 60000 }, async t => {
    const tcp = await tcpFor(t);
    const { runtime, events } = await callbackRuntime(t, `
      'use strict';
      const net = require('net'), tls = require('tls'), https = require('https');
      ${certificateSource}
      const credentials = tlsOptions();
      async function perform({ kind, mode, removal }) {
        let server, resource, calls = 0, receiver = true, argc = -1;
        if (kind === 'tls') {
          server = https.createServer(credentials);
          await new Promise(resolve => server.listen(0, resolve));
        }
        const callback = function(...args) {
          calls++; receiver &&= this === resource; argc = args.length;
          if (mode === 'sync') throw new Error('synthetic connect failure');
          if (mode === 'async') return Promise.reject(new Error('synthetic connect failure'));
        };
        resource = (kind === 'tls' ? tls : net).connect({
          host: '127.0.0.1', port: server ? server.address().port : ${tcp.port},
        }, callback);
        resource.on('error', () => {});
        const event = kind === 'tls' ? 'secureConnect' : 'connect';
        const registered = resource.listeners(event).includes(callback);
        if (removal === 'remove') resource.removeListener(event, callback);
        if (removal === 'off') resource.off(event, callback);
        if (removal === 'duplicate') {
          resource.once(event, callback);
          resource.off(event, callback);
        }
        const removed = !resource.listeners(event).includes(callback);
        await new Promise(resolve => resource.once(event, resolve));
        const closed = new Promise(resolve => resource.once('close', resolve));
        resource.destroy();
        await closed;
        if (server) await new Promise(resolve => server.close(resolve));
        await turn();
        return { registered, removed, calls, receiver, argc, closed: resource.closed };
      }
      ${completionActions}
      module.exports.identity = input => perform({ ...input, mode: 'normal' });
    `);
    const { a, worker } = await ownerCases(t, runtime, events, ['net', 'tls'], result =>
      assert.deepEqual(result, {
        registered: true, removed: false, calls: 1, receiver: true, argc: 0, closed: true,
      }));
    for (const kind of ['net', 'tls']) for (const removal of ['remove', 'off'])
      assert.deepEqual(await invoke(runtime, 'a', 'identity', { kind, removal }), {
        registered: true, removed: true, calls: 0, receiver: true, argc: -1, closed: true,
      });
    for (const kind of ['net', 'tls'])
      assert.deepEqual(await invoke(runtime, 'a', 'identity', { kind, removal: 'duplicate' }), {
        registered: true, removed: false, calls: 1, receiver: true, argc: 0, closed: true,
      });
    const exited = once(worker, 'exit');
    const disconnected = Promise.all([...tcp.sockets].map(socket => once(socket, 'close')));
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    await disconnected;
    assert.equal(tcp.sockets.size, 0);
    assert.equal(a.cleanupError, null);
  });

function listenSource(primary = false) {
  return `
    'use strict';
    const http = require('http'), https = require('https'), fs = require('fs');
    ${certificateSource}
    const credentials = tlsOptions();
    let held, main;
    async function perform({ kind, mode, removal, keep = false }) {
      const server = kind === 'https' ? https.createServer(credentials) : http.createServer();
      let calls = 0, receiver = true, argc = -1;
      const callback = function(...args) {
        calls++; receiver &&= this === server; argc = args.length;
        if (mode === 'sync') throw new Error('synthetic listen failure');
        if (mode === 'async') return Promise.reject(new Error('synthetic listen failure'));
      };
      const ready = new Promise(resolve => server.once('listening', resolve));
      const value = server.listen(0, callback);
      const registered = server.listeners('listening').includes(callback);
      if (removal === 'remove') server.removeListener('listening', callback);
      if (removal === 'off') server.off('listening', callback);
      const removed = !server.listeners('listening').includes(callback);
      await ready;
      const listening = server.listening;
      if (keep) held = server;
      else await new Promise(resolve => server.close(resolve));
      await turn();
      return { registered, removed, calls, receiver, argc, listening,
        returned: value === server, closed: !server.listening };
    }
    ${completionActions}
    module.exports.identity = input => perform({ ...input, mode: 'normal' });
    module.exports.arm = input => perform({ ...input, mode: 'normal', keep: true });
    module.exports.state = () => ({ listening: held.listening });
    module.exports.stop = async () => {
      if (held) {
        const before = held.listening;
        await new Promise(resolve => held.close(resolve));
        fs.writeFileSync('/listen-stop.json', JSON.stringify({ before, after: held.listening }));
      }
      if (main) await new Promise(resolve => main.close(resolve));
    };
    ${primary ? `
      // HTTPS owns the running business endpoint, leaving the prebound HTTP
      // primary unclaimed until a ready invocation exercises its fast path.
      module.exports.start = async () => {
        main = https.createServer(credentials, async (request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config') {
            response.end(JSON.stringify({ video: { sites: [{ key: 'default', api: '/api/default',
              actions: ['register', 'fire', 'active', 'settle', 'home', 'identity', 'arm', 'state'],
            }] } })); return;
          }
          const chunks = [];
          for await (const chunk of request) chunks.push(chunk);
          const input = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : {};
          const action = new URL(request.url, 'https://127.0.0.1').pathname.split('/').pop();
          const result = await module.exports[action](input);
          response.end(JSON.stringify(result));
        });
        await new Promise(resolve => main.listen(0, resolve));
      };
    ` : ''}
  `;
}

test('Cat secondary HTTP and HTTPS listen callbacks retain request scope and session-owned listeners',
  { timeout: 60000 }, async t => {
    const { runtime, events } = await callbackRuntime(t, listenSource());
    const { a, b, worker, count } = await ownerCases(t, runtime, events, ['http', 'https'], result =>
      assert.deepEqual(result, {
        registered: true, removed: false, calls: 1, receiver: true, argc: 0,
        listening: true, returned: true, closed: true,
      }));
    for (const kind of ['http', 'https']) for (const removal of ['remove', 'off'])
      assert.deepEqual(await invoke(runtime, 'a', 'identity', { kind, removal }), {
        registered: true, removed: true, calls: 0, receiver: true, argc: -1,
        listening: true, returned: true, closed: true,
      });
    assert.deepEqual(await invoke(runtime, 'a', 'arm', { kind: 'https' }), {
      registered: true, removed: false, calls: 1, receiver: true, argc: 0,
      listening: true, returned: true, closed: false,
    });
    assert.deepEqual(await invoke(runtime, 'a', 'state'), { listening: true });
    assert.equal(runtime.getSession('b'), b);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: count + 1 });
    const exited = once(worker, 'exit');
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    assert.equal(a.cleanupError, null);
  });

test('Cat prebound primary listen keeps invocation attribution, callback identity and real stop completion',
  { timeout: 60000 }, async t => {
    for (const mode of ['normal', 'sync', 'async']) for (const retired of [false, true]) {
      await t.test(`${mode} ${retired ? 'retired' : 'active'}`, async t => {
        const { runtime, events } = await callbackRuntime(t, listenSource(true));
        const a = runtime.getSession('a'), b = runtime.getSession('b'), worker = a.worker;
        const input = { kind: 'http', mode };
        if (retired) await invoke(runtime, 'a', 'register', input);
        const pending = invoke(runtime, 'a', retired ? 'fire' : 'active', input);
        const expected = { registered: true, removed: false, calls: 1, receiver: true, argc: 0,
          listening: true, returned: true, closed: true };
        if (!retired && mode !== 'normal') await assert.rejects(pending, { code: 'SCRIPT_EXCEPTION' });
        else assert.deepEqual(await pending, expected);
        assert.deepEqual(await invoke(runtime, 'a', 'settle'), expected);
        const failures = events.filter(event => event.kind === 'ScriptCallbackError');
        assert.equal(failures.length, retired && mode !== 'normal' ? 1 : 0);
        for (const failure of failures) {
          assert.equal(failure.installationId, 'a');
          assert.equal(failure.generation, a.generation);
          assert.equal(failure.siteId, 'default');
          assert.ok(failure.requestId > 0);
        }
        assert.equal(runtime.getSession('a'), a);
        assert.equal(a.worker, worker);
        assert.deepEqual(await invoke(runtime, 'a', 'home'), { count: 1 });
        assert.equal(runtime.getSession('b'), b);
        assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: 1 });
        const exited = once(worker, 'exit');
        await runtime.close();
        assert.deepEqual(await exited, [0]);
        assert.equal(a.cleanupError, null);
        assert.deepEqual(events.filter(event => event.operation === 'worker.exit'), []);
      });
    }
    for (const removal of ['remove', 'off']) await t.test(removal, async t => {
      const dataRoot = rootFor(t), runtime = createCatRuntime({ dataRoot });
      t.after(() => runtime.close());
      await runtime.install({ installationId: 'a', source: listenSource(true) });
      const a = runtime.getSession('a'), worker = a.worker;
      assert.deepEqual(await invoke(runtime, 'a', 'arm', { kind: 'http', removal }), {
        registered: true, removed: true, calls: 0, receiver: true, argc: -1,
        listening: true, returned: true, closed: false,
      });
      assert.deepEqual(await invoke(runtime, 'a', 'state'), { listening: true });
      const exited = once(worker, 'exit');
      await runtime.close();
      assert.deepEqual(await exited, [0]);
      assert.deepEqual(JSON.parse(readFileSync(join(dataRoot, 'volumes', 'a', 'listen-stop.json'), 'utf8')),
        { before: true, after: false });
      assert.equal(a.cleanupError, null);
    });
  });

async function streamCompletionShape(Stream, promisify) {
  const closed = value => new Promise(resolve => value.once('close', resolve));
  const writable = chunks => new Stream.Writable({ write(chunk, encoding, done) {
    chunks.push(...chunk); done();
  } });
  const result = { pipelines: [], finished: [], cleanup: [], promises: [], invalid: [] };
  for (const array of [false, true]) {
    const chunks = [], source = Stream.Readable.from([Buffer.from([0, 255, 65])]);
    const target = writable(chunks), closes = [closed(source), closed(target)];
    let calls = 0, observed, complete;
    const completed = new Promise(resolve => { complete = resolve; });
    const callback = function(...args) {
      calls++;
      observed = { receiver: this === undefined, argc: args.length,
        error: args[0] === undefined, value: args[1] === undefined };
      complete();
    };
    const returned = array ? Stream.pipeline([source, target], callback)
      : Stream.pipeline(source, target, callback);
    await Promise.all([completed, ...closes]);
    result.pipelines.push({ array, returned: returned === target, calls, ...observed, chunks,
      closed: source.closed && target.closed });
  }
  for (const overload of ['callback', 'null-options', 'options', 'already-closed']) {
    const target = writable([]), close = closed(target);
    if (overload === 'already-closed') { target.end(); await close; }
    let calls = 0, observed, complete;
    const completed = new Promise(resolve => { complete = resolve; });
    const callback = function(...args) {
      calls++;
      observed = { receiver: this === target, argc: args.length };
      complete();
    };
    const cleanup = overload === 'options'
      ? Stream.finished(target, { readable: false, writable: true }, callback)
      : overload === 'null-options' ? Stream.finished(target, null, callback)
        : Stream.finished(target, callback);
    const immediate = calls;
    if (overload !== 'already-closed') target.end();
    await Promise.all([completed, close]);
    cleanup();
    result.finished.push({ overload, cleanup: typeof cleanup, immediate, calls,
      ...observed, closed: target.closed });
  }
  for (const alreadyClosed of [false, true]) {
    const target = writable([]), close = closed(target);
    if (alreadyClosed) { target.end(); await close; }
    const events = ['end', 'finish', 'error', 'close'];
    const before = events.map(event => target.listenerCount(event));
    let calls = 0;
    const cleanup = Stream.finished(target, () => { calls++; });
    const registered = events.some((event, index) => target.listenerCount(event) > before[index]);
    cleanup();
    const removed = events.every((event, index) => target.listenerCount(event) === before[index]);
    if (!alreadyClosed) target.end();
    await close;
    // Consume the already-closed nextTick notification after cleanup, not a
    // timeout used to guess whether the stream itself has closed.
    await new Promise(resolve => process.nextTick(resolve));
    result.cleanup.push({ alreadyClosed, registered, removed, calls, closed: target.closed });
  }
  for (const kind of ['pipeline', 'finished']) {
    const descriptor = Object.getOwnPropertyDescriptor(Stream[kind], promisify.custom);
    result.promises.push({ kind, custom: promisify(Stream[kind]) === Stream.promises[kind],
      getter: typeof descriptor.get, enumerable: descriptor.enumerable,
      configurable: descriptor.configurable });
  }
  const chunks = [], source = Stream.Readable.from([Buffer.from([0, 255, 65])]);
  const target = writable(chunks), sourceClose = closed(source), targetClose = closed(target);
  const pipelineResult = await promisify(Stream.pipeline)(source, target, { end: false });
  await sourceClose;
  const open = !target.writableEnded && !target.closed;
  const finished = promisify(Stream.finished)(target, { cleanup: true, readable: false });
  target.end();
  const finishedResult = await finished;
  await targetClose;
  result.promiseOptions = { pipelineResult: pipelineResult === undefined,
    finishedResult: finishedResult === undefined, open, closed: target.closed, chunks };
  for (const kind of ['pipeline', 'finished']) {
    const target = writable([]), controller = new AbortController(), closes = [closed(target)];
    let source, pending;
    if (kind === 'pipeline') {
      source = new Stream.Readable({ read() {} });
      closes.push(closed(source));
      pending = promisify(Stream.pipeline)(source, target, { signal: controller.signal });
    } else pending = promisify(Stream.finished)(target, { signal: controller.signal, cleanup: true });
    const completion = pending.then(() => ({ resolved: true }),
      error => ({ resolved: false, code: error.code, name: error.name }));
    controller.abort();
    const observed = await completion;
    const open = !target.destroyed;
    if (kind === 'finished') target.end();
    await Promise.all(closes);
    result.promises.push({ kind: kind + '-signal', ...observed, open,
      closed: target.closed && (!source || source.closed) });
  }
  for (const kind of ['pipeline', 'finished']) {
    const target = writable([]), close = closed(target);
    let code;
    try {
      if (kind === 'pipeline') Stream.pipeline([target], null);
      else Stream.finished(target, {}, null);
    } catch (error) { code = error.code; }
    target.end();
    await close;
    result.invalid.push({ kind, code, closed: target.closed });
  }
  return result;
}

test('Cat pipeline and finished preserve native overloads, cleanup, aliases and promisify options',
  { timeout: 30000 }, async t => {
    const descriptors = ['pipeline', 'finished'].map(name =>
      Object.getOwnPropertyDescriptor(stream[name], promisify.custom));
    const oracle = await streamCompletionShape(stream, promisify);
    assert.deepEqual(oracle.pipelines, [false, true].map(array => ({
      array, returned: true, calls: 1, receiver: true, argc: 2, error: true, value: true,
      chunks: [0, 255, 65], closed: true,
    })));
    assert.deepEqual(oracle.finished, ['callback', 'null-options', 'options', 'already-closed']
      .map(overload => ({ overload, cleanup: 'function', immediate: 0, calls: 1,
        receiver: true, argc: 0, closed: true })));
    assert.deepEqual(oracle.cleanup, [false, true].map(alreadyClosed => ({
      alreadyClosed, registered: true, removed: true, calls: 0, closed: true,
    })));
    assert.deepEqual(oracle.promiseOptions, { pipelineResult: true, finishedResult: true,
      open: true, closed: true, chunks: [0, 255, 65] });
    assert.deepEqual(oracle.promises, [
      ...['pipeline', 'finished'].map(kind => ({ kind, custom: true, getter: 'function',
        enumerable: true, configurable: false })),
      ...['pipeline', 'finished'].map(kind => ({ kind: kind + '-signal', resolved: false,
        code: 'ABORT_ERR', name: 'AbortError', open: kind === 'finished', closed: true })),
    ]);
    assert.deepEqual(oracle.invalid, ['pipeline', 'finished'].map(kind => ({
      kind, code: 'ERR_INVALID_ARG_TYPE', closed: true,
    })));
    const session = await sessionFor(t, `
      'use strict';
      const Stream = require('stream'), { promisify } = require('util');
      ${streamCompletionShape.toString()}
      module.exports.probe = async () => ({
        shape: await streamCompletionShape(Stream, promisify),
        aliases: ['node:stream', 'readable-stream', 'readable-stream/lib/stream.js']
          .every(name => require(name) === Stream && require(name).pipeline === Stream.pipeline &&
            require(name).finished === Stream.finished),
        constructor: Stream.Stream === Stream,
        promises: Stream.promises === require('stream/promises') &&
          Stream.promises === require('node:stream/promises'),
      });
    `);
    const standard = ['stdin', 'stdout', 'stderr'].map(name => ({
      name, value: session.node.process[name], closes: 0,
    }));
    const standardValues = new Set(standard.map(entry => entry.value));
    assert.equal(standardValues.size, standard.length);
    const assertStandardOwners = () => {
      assert.equal(session.node.streams.size, standardValues.size);
      for (const value of session.node.streams.keys()) assert.equal(standardValues.has(value), true);
      for (const { name, value } of standard) {
        assert.equal(session.node.streams.has(value), true, name);
        assert.equal(session.node.resourceCleanup.has(value), true, name);
        assert.equal(value.closed, false, name);
        assert.equal(value.destroyed, false, name);
      }
    };
    assertStandardOwners();
    const standardClosed = standard.map(entry => new Promise(resolve => {
      entry.value.on('close', () => { entry.closes++; resolve(); });
    }));
    assert.deepEqual(await call(session, 'probe'), {
      shape: oracle, aliases: true, constructor: true, promises: true,
    });
    assertStandardOwners();
    for (const [index, name] of ['pipeline', 'finished'].entries())
      assert.deepEqual(Object.getOwnPropertyDescriptor(stream[name], promisify.custom), descriptors[index]);
    await session.stop();
    for (const { name, value, closes } of standard) {
      assert.equal(closes, 1, name);
      assert.equal(value.closed, true, name);
      assert.equal(value.destroyed, true, name);
      assert.equal(session.node.streams.has(value), false, name);
      assert.equal(session.node.resourceCleanup.has(value), false, name);
    }
    await Promise.all(standardClosed);
    assert.equal(session.node.streams.size, 0);
    assert.equal(session.node.resourceCleanup.size, 0);
    assert.equal(session.cleanupError, undefined);
    assert.equal(session.adapterTasks.size, 0);
  });

test('Cat pipeline and finished completions retain active and retired failure ownership without Worker exit',
  { timeout: 30000 }, async t => {
    const { runtime, events } = await callbackRuntime(t, `
      'use strict';
      const Stream = require('stream');
      async function perform({ kind, mode }) {
        const chunks = [], target = new Stream.Writable({ write(chunk, encoding, done) {
          chunks.push(...chunk); done();
        } });
        const closed = new Promise(resolve => target.once('close', resolve));
        let source, sourceClosed, calls = 0, observed, complete;
        const completed = new Promise(resolve => { complete = resolve; });
        if (kind === 'finished-already-closed') { target.end(); await closed; }
        const callback = function(...args) {
          observed = { receiver: kind === 'pipeline' ? this === undefined : this === target,
            argc: args.length, error: args[0]?.code ?? null,
            value: args[1] === undefined };
          calls++; complete();
          if (mode === 'sync') throw new Error('synthetic stream completion failure');
          if (mode === 'async') return Promise.reject(new Error('synthetic stream completion failure'));
        };
        let returned, cleanup;
        if (kind === 'pipeline') {
          source = Stream.Readable.from([Buffer.from([0, 255, 65])]);
          sourceClosed = new Promise(resolve => source.once('close', resolve));
          returned = Stream.pipeline(source, target, callback) === target;
        } else {
          cleanup = Stream.finished(target, callback);
          returned = typeof cleanup === 'function';
          if (kind === 'finished-live') target.end(Buffer.from([0, 255, 65]));
        }
        await Promise.all([completed, closed, sourceClosed]);
        cleanup?.();
        await turn();
        return { calls, ...observed, returned, chunks, closed: target.closed && (!source || source.closed) };
      }
      ${completionActions}
    `);
    const { a, worker } = await ownerCases(t, runtime, events,
      ['pipeline', 'finished-live', 'finished-already-closed'], (result, kind) =>
        assert.deepEqual(result, { calls: 1, receiver: true, argc: kind === 'pipeline' ? 2 : 0,
          error: null, value: true, returned: true,
          chunks: kind === 'finished-already-closed' ? [] : [0, 255, 65], closed: true }));
    const exited = once(worker, 'exit');
    await runtime.close();
    assert.deepEqual(await exited, [0]);
    assert.equal(worker.threadId, -1);
    assert.equal(a.worker, null);
    assert.equal(a.cleanupError, null);
    assert.equal(a.adapterTasks.size, 0);
    assert.equal(a.stats().streams, 0);
  });

test('Cat held pipeline and finished deliver native terminal completions to cooperative stop without inheriting cleanup',
  { timeout: 30000 }, async t => {
    const dataRoot = rootFor(t), events = [], runtime = createCatRuntime({ dataRoot,
      limits: { initTimeoutMs: 10000, requestTimeoutMs: 3000, cleanupTimeoutMs: 1000 },
      onDiagnostic: event => events.push(event) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'a', source: `
      'use strict';
      const Stream = require('stream'), fs = require('fs');
      let resources, closes, completions, record, cleanup;
      module.exports.arm = () => {
        const source = new Stream.Readable({ read() {} });
        const target = new Stream.Writable({ write(chunk, encoding, done) { done(); } });
        const finished = new Stream.Writable({ write(chunk, encoding, done) { done(); } });
        resources = [source, target, finished];
        closes = resources.map(value => new Promise(resolve => value.once('close', resolve)));
        completions = [];
        record = { pipeline: { calls: 0 }, finished: { calls: 0 } };
        const callback = (kind, receiver) => {
          let complete;
          completions.push(new Promise(resolve => { complete = resolve; }));
          return function(...args) {
            record[kind] = { calls: record[kind].calls + 1, receiver: this === receiver,
              argc: args.length, code: args[0]?.code ?? null, value: args[1] === undefined };
            setImmediate(() => fs.writeFileSync('/unexpected-background.txt', kind));
            complete();
          };
        };
        const returned = Stream.pipeline(source, target, callback('pipeline', undefined)) === target;
        cleanup = Stream.finished(finished, callback('finished', finished));
        return { returned, cleanup: typeof cleanup, calls: [record.pipeline.calls, record.finished.calls],
          closed: resources.some(value => value.closed) };
      };
      module.exports.stop = async () => {
        const reason = Object.assign(new Error('terminal cancellation'), { code: 'CANCELLED' });
        for (const value of resources) value.destroy(reason);
        await Promise.all([...completions, ...closes]);
        cleanup();
        // This immediate is registered by the real cleanup hook after terminal
        // callbacks. Their earlier ordinary immediates must remain suppressed.
        await new Promise(resolve => setImmediate(resolve));
        fs.writeFileSync('/stream-stop.json', JSON.stringify({ ...record,
          closed: resources.every(value => value.closed) }));
      };
      module.exports.home = () => ({ ready: true });
    ` });
    await runtime.install({ installationId: 'b', source: counterSource });
    const a = runtime.getSession('a'), b = runtime.getSession('b'), worker = a.worker;
    assert.deepEqual(await invoke(runtime, 'a', 'arm'), {
      returned: true, cleanup: 'function', calls: [0, 0], closed: false,
    });
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: 1 });
    const exited = once(worker, 'exit');
    await runtime.setEnabled('a', false);
    assert.deepEqual(await exited, [0]);
    assert.deepEqual(JSON.parse(readFileSync(join(dataRoot, 'volumes', 'a', 'stream-stop.json'), 'utf8')), {
      pipeline: { calls: 1, receiver: true, argc: 2, code: 'CANCELLED', value: true },
      finished: { calls: 1, receiver: true, argc: 1, code: 'CANCELLED', value: true },
      closed: true,
    });
    assert.equal(existsSync(join(dataRoot, 'volumes', 'a', 'unexpected-background.txt')), false);
    assert.equal(worker.threadId, -1);
    assert.equal(a.worker, null);
    assert.equal(a.cleanupError, null);
    assert.equal(a.adapterTasks.size, 0);
    for (const name of ['streams', 'timers', 'listeners', 'sockets', 'descriptors'])
      assert.equal(a.stats()[name], 0);
    assert.equal(runtime.getSession('b'), b);
    assert.deepEqual(await invoke(runtime, 'b', 'home'), { count: 2 });
    assert.deepEqual(events.filter(event =>
      event.kind === 'ScriptCallbackError' || event.operation === 'worker.exit'), []);
    const bExited = once(b.worker, 'exit');
    await runtime.close();
    assert.deepEqual(await bExited, [0]);
    assert.equal(b.cleanupError, null);
  });

function observeServerClosure(server, services, removal) {
  const state = { managed: 0, once: 0, native: 0, late: 0, removed: 0, observations: [] };
  const observe = (kind, receiver, args) => {
    state[kind]++;
    state.observations.push({ kind, receiver: receiver === server, argc: args.length,
      owned: services.listeners.has(server) && services.serverCleanup.has(server),
      released: server.meowReleased });
  };
  if (removal) {
    server.on('close', () => { state.removed++; });
    if (removal === 'close') server.removeAllListeners('close');
    else server.removeAllListeners();
  }
  server.on('close', function(...args) { observe('managed', this, args); });
  server.once('close', function(...args) { observe('once', this, args); });
  // A raw host observer is independent of the managed on/once projection.
  const closed = new Promise(resolve => {
    EventEmitter.prototype.on.call(server, 'close', function(...args) {
      observe('native', this, args); resolve();
    });
  });
  return { state, closed, observeLate() {
    server.on('close', function(...args) { observe('late', this, args); });
  } };
}

function assertServerClosure(server, services, state, expected, late = false) {
  assert.deepEqual({ managed: state.managed, once: state.once, native: state.native,
    late: state.late, removed: state.removed }, {
    managed: expected, once: expected, native: expected, late: late ? 1 : 0, removed: 0,
  });
  const kinds = ['managed', 'once', 'native', ...(late ? ['late'] : [])];
  assert.deepEqual(state.observations, expected ? kinds.map(kind => ({
    kind, receiver: true, argc: 0, owned: true, released: false,
  })) : []);
  assert.equal(server.listening, false);
  assert.equal(server.address(), null);
  assert.equal(server.meowReleased, true);
  assert.equal(services.listeners.has(server), false);
  assert.equal(services.serverCleanup.has(server), false);
}

test('Cat server cleanup waits for real close and preserves terminal observers across lifecycle branches',
  { timeout: 30000 }, async t => {
    const b = await sessionFor(t, counterSource), realm = b.realm;
    let count = 0;
    const cases = [
      { name: 'primary scope close', kind: 'primary', phase: 'scope' },
      { name: 'HTTP scope close', kind: 'http', phase: 'scope' },
      { name: 'HTTPS scope close', kind: 'https', phase: 'scope' },
      { name: 'HTTP listen still starting', kind: 'http', phase: 'starting' },
      { name: 'explicit close before scope', kind: 'http', phase: 'manual' },
      { name: 'remove close listeners', kind: 'http', phase: 'scope', removal: 'close' },
      { name: 'remove all listeners', kind: 'http', phase: 'scope', removal: 'all' },
      { name: 'already actually closed', kind: 'http', phase: 'closed' },
      { name: 'HTTP never listened', kind: 'http', phase: 'never' },
      { name: 'HTTPS never listened', kind: 'https', phase: 'never' },
      { name: 'never listened explicit close', kind: 'http', phase: 'never-close' },
      { name: 'direct services close', kind: 'http', phase: 'direct' },
    ];
    for (const spec of cases) await t.test(spec.name, async t => {
      const session = await sessionFor(t, counterSource), services = session.services;
      assert.deepEqual(await call(b, 'home'), { count: ++count });
      const server = spec.kind === 'primary' ? services.main : services[spec.kind].createServer();
      if (spec.kind !== 'primary' && !spec.phase.startsWith('never')) {
        if (spec.phase === 'starting') server.listen(0);
        else {
          const listening = once(server, 'listening');
          server.listen(0);
          await listening;
        }
      }
      const observed = observeServerClosure(server, services, spec.removal);
      const completed = spec.phase !== 'never', pendingClose = completed && spec.phase !== 'closed';
      let callbacks = 0, callbackReceiver = true, callbackError = null;
      if (['manual', 'closed', 'never-close'].includes(spec.phase)) {
        assert.equal(server.close(function(error) {
          callbacks++;
          callbackReceiver &&= this === (spec.phase === 'never-close' ? undefined : server);
          callbackError = error?.code ?? null;
        }), server);
        if (spec.phase === 'closed') await observed.closed;
      }
      if (spec.phase !== 'direct') session.resourceScope().close();
      let settled = false;
      const closing = services.close().then(() => { settled = true; });
      const repeated = services.close();
      if (pendingClose) {
        observed.observeLate();
        assert.equal(server.listening, false, 'close has stopped accepting before its nextTick event');
        assert.equal(server.meowReleased, false, 'not listening is not terminal completion');
        assert.equal(services.listeners.has(server), true);
        assert.equal(services.serverCleanup.has(server), true);
        assert.deepEqual([observed.state.managed, observed.state.once, observed.state.native], [0, 0, 0]);
        assert.equal(settled, false);
      }
      await Promise.all([closing, repeated, session.stop()]);
      assert.equal(settled, true);
      // Stop must have observed the event itself; awaiting an unobserved close
      // here first would hide a prematurely settled cleanup.
      assertServerClosure(server, services, observed.state, completed ? 1 : 0, pendingClose);
      if (completed) await observed.closed;
      assert.equal(callbacks, ['manual', 'closed', 'never-close'].includes(spec.phase) ? 1 : 0);
      assert.equal(callbackReceiver, true);
      assert.equal(callbackError, spec.phase === 'never-close' ? 'ERR_SERVER_NOT_RUNNING' : null);
      assert.equal(session.cleanupError, undefined);
      assert.equal(services.listeners.size, 0);
      assert.equal(services.serverCleanup.size, 0);
      assert.equal(services.connections.size, 0);
      assert.equal(session.node.resourceCleanup.size, 0);
      assert.equal(session.adapterTasks.size, 0);
      if (spec.phase === 'closed') {
        assert.equal(observed.state.native, 1);
        let calls = 0, managedCloses = 0, nativeCloses = 0, result;
        const closed = new Promise(resolve => {
          server.on('close', () => { managedCloses++; });
          EventEmitter.prototype.on.call(server, 'close', () => { nativeCloses++; });
          result = server.close(function(error) {
            calls++; resolve({ code: error?.code, argc: arguments.length, receiver: this === undefined });
          });
        });
        assert.equal(result, server);
        assert.deepEqual(await closed, { code: 'ERR_SERVER_NOT_RUNNING', argc: 1, receiver: true });
        assert.equal(calls, 1);
        assert.equal(managedCloses, 1);
        assert.equal(nativeCloses, 1);
      }
      assert.equal(b.realm, realm);
      assert.equal(b.live, true);
      assert.deepEqual(await call(b, 'home'), { count: ++count });
    });
    await b.stop();
    assert.equal(b.cleanupError, undefined);
  });

test('Cat server close failure remains reportable after actual terminal delivery and independent B progress',
  { timeout: 30000 }, async t => {
    const session = new CatSession({ dataRoot: rootFor(t), installationId: 'a', generation: 1,
      revision: { source: counterSource, version: '1', config: {} },
      limits: { ...SCRIPT_LIMITS, initTimeoutMs: 10000, requestTimeoutMs: 10000, cleanupTimeoutMs: 1000 } });
    t.after(() => session.stop().catch(() => {}));
    await session.start();
    session.activate();
    const b = await sessionFor(t, counterSource), services = session.services, server = services.main;
    const observed = observeServerClosure(server, services);
    const failure = new Error('synthetic closeAllConnections failure');
    const closeConnections = server.closeAllConnections;
    server.closeAllConnections = function(...args) {
      Reflect.apply(closeConnections, this, args);
      throw failure;
    };
    assert.deepEqual(await call(b, 'home'), { count: 1 });
    session.resourceScope().close();
    const closing = services.close();
    observed.observeLate();
    assert.equal(server.meowReleased, false);
    assert.equal(observed.state.native, 0);
    const failed = assert.rejects(closing, error => error === failure);
    const stopped = assert.rejects(session.stop(), { code: 'SERVICE_ERROR' });
    await Promise.all([failed, stopped]);
    assertServerClosure(server, services, observed.state, 1, true);
    await observed.closed;
    assert.equal(session.cleanupError, 'SERVICE_ERROR');
    assert.equal(services.listeners.size, 0);
    assert.equal(services.serverCleanup.size, 0);
    assert.equal(services.connections.size, 0);
    assert.equal(session.node.resourceCleanup.size, 0);
    assert.equal(session.adapterTasks.size, 0);
    assert.equal(b.live, true);
    assert.deepEqual(await call(b, 'home'), { count: 2 });
    await b.stop();
    assert.equal(b.cleanupError, undefined);
  });

test('Cat server close waits survive another server removing pending guest listeners',
  { timeout: 30000 }, async t => {
    const session = await sessionFor(t, counterSource), services = session.services;
    const b = await sessionFor(t, counterSource), realm = b.realm;
    const x = services.http.createServer(), y = services.http.createServer();
    for (const server of [x, y]) {
      const listening = once(server, 'listening');
      server.listen(0);
      await listening;
      assert.equal(server.listening, true);
      assert.equal(await new Promise((resolve, reject) => {
        server.getConnections((error, count) => error ? reject(error) : resolve(count));
      }), 0);
    }
    assert.equal(services.connections.size, 0);
    assert.deepEqual(await call(b, 'home'), { count: 1 });
    const owned = server => services.listeners.has(server) && services.serverCleanup.has(server);
    const order = [], observations = [], raw = { x: 0, y: 0 };
    let xGuest = 0, yGuest = 0, waiting = false, settled = false, removal;
    const closed = [['x', x], ['y', y]].map(([name, server]) => new Promise(resolve => {
      EventEmitter.prototype.on.call(server, 'close', function(...args) {
        raw[name]++;
        order.push(`${name}:raw`);
        observations.push({ name, receiver: this === server, argc: args.length,
          owned: owned(server), released: server.meowReleased, xReleased: x.meowReleased });
        resolve();
      });
    }));
    const removed = () => { yGuest++; };
    y.on('close', removed);
    x.on('close', function(...args) {
      xGuest++;
      order.push('x:remove-y');
      removal = { receiver: this === x, argc: args.length, waiting, settled,
        rawY: raw.y, guestY: yGuest, xOwned: owned(x), yOwned: owned(y), yReleased: y.meowReleased,
        returned: y.removeAllListeners('close') === y,
        guestRemoved: !y.listeners('close').includes(removed) };
    });

    // Both native closes are queued by the scope. Enter the service waits in
    // this same turn, before X can remove Y's still-pending guest listeners.
    session.resourceScope().close();
    const closing = services.close().then(() => { settled = true; });
    waiting = true;
    for (const server of [x, y]) {
      assert.equal(server.listening, false);
      assert.equal(server.meowClosing, true);
      assert.equal(server.meowReleased, false);
      assert.equal(owned(server), true);
    }
    assert.deepEqual({ raw, xGuest, yGuest, settled },
      { raw: { x: 0, y: 0 }, xGuest: 0, yGuest: 0, settled: false });
    const stopped = session.stop();
    const progressing = call(b, 'home').then(value => assert.deepEqual(value, { count: 2 }));
    await Promise.all([closing, stopped, progressing]);

    // Check the actual dispatches before awaiting the raw close promises, so
    // an early cleanup result cannot hide behind a later observation barrier.
    assert.deepEqual({ raw, xGuest, yGuest, settled },
      { raw: { x: 1, y: 1 }, xGuest: 1, yGuest: 0, settled: true });
    assert.deepEqual(order, ['x:raw', 'x:remove-y', 'y:raw']);
    assert.deepEqual(removal, { receiver: true, argc: 0, waiting: true, settled: false,
      rawY: 0, guestY: 0, xOwned: true, yOwned: true, yReleased: false,
      returned: true, guestRemoved: true });
    assert.deepEqual(observations, [
      { name: 'x', receiver: true, argc: 0, owned: true, released: false, xReleased: false },
      { name: 'y', receiver: true, argc: 0, owned: true, released: false, xReleased: true },
    ]);
    for (const server of [x, y]) {
      assert.equal(server.listening, false);
      assert.equal(server.address(), null);
      assert.equal(server.meowReleased, true);
      assert.equal(services.listeners.has(server), false);
      assert.equal(services.serverCleanup.has(server), false);
    }
    await Promise.all(closed);
    assert.equal(session.cleanupError, undefined);
    assert.equal(services.listeners.size, 0);
    assert.equal(services.serverCleanup.size, 0);
    assert.equal(services.connections.size, 0);
    assert.equal(session.node.resourceCleanup.size, 0);
    assert.equal(session.adapterTasks.size, 0);
    assert.equal(session.realm, null);
    assert.equal(b.realm, realm);
    assert.equal(b.live, true);
    assert.deepEqual(await call(b, 'home'), { count: 3 });
    await b.stop();
    assert.equal(b.cleanupError, undefined);
  });

test('Cat stream close waits survive cross-resource guest removal through real destruction',
  { timeout: 30000 }, async t => {
    for (const kind of ['writable', 'private-write']) await t.test(kind, async t => {
      const session = await sessionFor(t, counterSource), node = session.node;
      const b = await sessionFor(t, counterSource), realm = b.realm;
      const resume = {}, destroys = { x: 0, y: 0 };
      let yArmed;
      const armed = new Promise(resolve => { yArmed = resolve; });
      const hold = (name, native) => function(error, done) {
        destroys[name]++;
        resume[name] = () => {
          delete resume[name];
          if (native) Reflect.apply(native, this, [error, done]);
          else done(error);
        };
        if (name === 'y') yArmed();
      };
      const x = new node.builtins.stream.Writable({
        write(_chunk, _encoding, done) { done(); }, destroy: hold('x'),
      });
      let y, fd;
      const bytes = Buffer.from([0, 255, 65, 128]);
      if (kind === 'private-write') {
        y = node.fs.createWriteStream('/l22.bin');
        await once(y, 'open');
        fd = y.fd;
        await new Promise((resolve, reject) =>
          y.write(bytes, error => error ? reject(error) : resolve()));
        y._destroy = hold('y', y._destroy);
      } else {
        y = new node.builtins.stream.Writable({
          write(_chunk, _encoding, done) { done(); }, destroy: hold('y'),
        });
      }
      const owned = value => value === y && kind === 'private-write'
        ? node.volume.streams.has(value) && node.volume.streamCleanup.has(value)
        : node.streams.has(value) && node.resourceCleanup.has(value);
      const order = [], observations = [], raw = { x: 0, y: 0 };
      let removed = 0, settled = false, removal;
      const closed = [['x', x], ['y', y]].map(([name, value]) => new Promise(resolve => {
        EventEmitter.prototype.on.call(value, 'close', function(...args) {
          raw[name]++;
          order.push(`${name}:raw`);
          observations.push({ name, receiver: this === value, argc: args.length,
            owned: owned(value), xOwned: owned(x) });
          resolve();
        });
      }));
      const guestY = () => { removed++; };
      y.on('close', guestY);
      x.on('close', () => {
        order.push('x:remove-y');
        removal = { settled, rawY: raw.y, closedY: y.closed, xOwned: owned(x), yOwned: owned(y),
          returned: y.removeAllListeners('close') === y,
          removed: !y.listeners('close').includes(guestY) };
        resume.y?.();
      });
      for (const [name, value] of [['x', x], ['y', y]])
        EventEmitter.prototype.on.call(value, 'close', () => {
          order.push(`${name}:last`);
          observations.push({ name: `${name}:last`, owned: owned(value) });
        });
      assert.deepEqual(await call(b, 'home'), { count: 1 });
      let finished;
      try {
        session.resourceScope().close();
        const closing = node.close().then(() => { settled = true; });
        finished = Promise.all([closing, session.stop()]);
        // Private fs may enter _destroy from finish on a later turn. Its
        // actual native destroy is held, not its close event or result.
        await Promise.race([armed, finished]);
        assert.deepEqual(destroys, { x: 1, y: 1 });
        assert.deepEqual(raw, { x: 0, y: 0 });
        for (const value of [x, y]) {
          assert.equal(value.destroyed, true);
          assert.equal(value.closed, false);
          assert.equal(owned(value), true);
        }
        assert.equal(settled, false);
        const progressing = call(b, 'home');
        resume.x();
        await Promise.all([finished, progressing]);
        assert.deepEqual(raw, { x: 1, y: 1 });
        assert.equal(removed, 0);
        assert.deepEqual(order, ['x:raw', 'x:remove-y', 'x:last', 'y:raw', 'y:last']);
        assert.deepEqual(removal, { settled: false, rawY: 0, closedY: false,
          xOwned: true, yOwned: true, returned: true, removed: true });
        assert.deepEqual(observations, [
          { name: 'x', receiver: true, argc: 0, owned: true, xOwned: true },
          { name: 'x:last', owned: true },
          { name: 'y', receiver: true, argc: 0, owned: true, xOwned: false },
          { name: 'y:last', owned: true },
        ]);
        await Promise.all(closed);
        for (const value of [x, y]) {
          assert.equal(value.closed, true);
          assert.equal(owned(value), false);
          assert.equal(Object.hasOwn(value, 'emit'), false, 'no retired dispatch wrapper remains');
        }
        assert.deepEqual(destroys, { x: 1, y: 1 });
        if (kind === 'private-write') {
          assert.throws(() => fstatSync(fd), { code: 'EBADF' });
          assert.deepEqual(readFileSync(join(node.volume.root, 'l22.bin')), bytes);
        }
        assert.equal(session.cleanupError, undefined);
        assert.equal(session.realm, null);
        assert.equal(node.streams.size, 0);
        assert.equal(node.resourceCleanup.size, 0);
        assert.equal(node.volume.streams.size, 0);
        assert.equal(node.volume.streamCleanup.size, 0);
        assert.equal(node.volume.descriptors.size, 0);
        assert.equal(session.adapterTasks.size, 0);
        assert.deepEqual(await progressing, { count: 2 });
        assert.equal(b.realm, realm);
        assert.equal(b.live, true);
        assert.deepEqual(await call(b, 'home'), { count: 3 });
      } finally {
        resume.x?.(); resume.y?.();
        await finished?.catch(() => {});
      }
      await b.stop();
      assert.equal(b.cleanupError, undefined);
    });
  });

test('Cat socket and request close waits survive guest removal and retire all native owners after dispatch',
  { timeout: 30000 }, async t => {
    for (const kind of ['socket', 'request']) await t.test(kind, async t => {
      const session = await sessionFor(t, counterSource), node = session.node, services = session.services;
      const b = await sessionFor(t, counterSource), realm = b.realm;
      let received;
      const incoming = new Promise(resolve => { received = resolve; });
      const server = services.http.createServer((request, _response) => { received(request); });
      const listening = once(server, 'listening');
      server.listen(0);
      await listening;
      const accepted = once(server, 'connection');
      const port = server.address().port;
      let y, socket;
      if (kind === 'socket') {
        y = node.builtins.net.connect({ host: '127.0.0.1', port });
        await once(y, 'connect');
        socket = y;
      } else {
        y = services.http.get(`http://127.0.0.1:${port}/held`, { agent: false });
        await incoming;
        socket = y.socket;
      }
      const [peer] = await accepted;
      assert.equal(services.connections.has(peer), true);
      const nativeDestroy = socket._destroy;
      let resumeSocket, socketDestroys = 0;
      socket._destroy = function(error, done) {
        socketDestroys++;
        resumeSocket = () => {
          resumeSocket = null;
          Reflect.apply(nativeDestroy, this, [error, done]);
        };
      };
      let resumeX;
      const x = new node.builtins.stream.Writable({
        write(_chunk, _encoding, done) { done(); },
        destroy(error, done) { resumeX = () => { resumeX = null; done(error); }; },
      });
      const owned = () => kind === 'socket'
        ? node.networkResources.has(y) && node.resourceCleanup.has(y) &&
          services.trustedSockets.has(y) && services.trustedSocketCleanup.has(y)
        : session.nativeRequests.has(y) && services.requestCleanup.has(y);
      let rawX = 0, rawY = 0, guestY = 0, settled = false, removal;
      const observations = [], closed = [];
      for (const [name, value] of [['x', x], ['y', y], ['peer', peer]]) {
        closed.push(new Promise(resolve => EventEmitter.prototype.on.call(value, 'close', () => {
          if (name === 'x') rawX++;
          if (name === 'y') rawY++;
          observations.push({ name, owned: name === 'x' ? node.resourceCleanup.has(x)
            : name === 'y' ? owned() : services.connections.has(peer) &&
              services.connectionCleanup.has(peer) });
          resolve();
        })));
      }
      const removed = () => { guestY++; };
      y.on('close', removed);
      x.on('close', () => {
        removal = { rawY, guestY, settled, closed: y.closed, owned: owned(),
          returned: y.removeAllListeners('close') === y,
          removed: !y.listeners('close').includes(removed) };
        resumeSocket?.();
      });
      EventEmitter.prototype.on.call(y, 'close', () =>
        observations.push({ name: 'y:last', owned: owned() }));
      assert.deepEqual(await call(b, 'home'), { count: 1 });
      let finished;
      try {
        session.resourceScope().close();
        const closing = node.close().then(() => { settled = true; });
        finished = Promise.all([closing, session.stop()]);
        assert.equal(socketDestroys, 1);
        assert.equal(y.closed, false);
        assert.equal(owned(), true);
        assert.equal(rawX + rawY, 0);
        const progressing = call(b, 'home');
        resumeX();
        await Promise.all([finished, progressing]);
        assert.deepEqual({ rawX, rawY, guestY, settled },
          { rawX: 1, rawY: 1, guestY: 0, settled: true });
        assert.deepEqual(removal, { rawY: 0, guestY: 0, settled: false,
          closed: false, owned: true, returned: true, removed: true });
        assert.deepEqual(observations.filter(value => value.name !== 'peer'), [
          { name: 'x', owned: true }, { name: 'y', owned: true }, { name: 'y:last', owned: true },
        ]);
        assert.deepEqual(observations.filter(value => value.name === 'peer'),
          [{ name: 'peer', owned: true }]);
        await Promise.all(closed);
        assert.equal(socketDestroys, 1);
        assert.equal(socket.closed, true);
        assert.equal(peer.closed, true);
        assert.equal(y.closed, true);
        assert.equal(owned(), false);
        assert.equal(Object.hasOwn(y, 'emit'), false);
        assert.equal(services.connections.size, 0);
        assert.equal(services.connectionCleanup.size, 0);
        assert.equal(services.trustedSockets.size, 0);
        assert.equal(services.trustedSocketCleanup.size, 0);
        assert.equal(services.requestCleanup.size, 0);
        assert.equal(session.nativeRequests.size, 0);
        assert.equal(node.networkResources.size, 0);
        assert.equal(node.resourceCleanup.size, 0);
        assert.equal(session.adapterTasks.size, 0);
        assert.equal(session.cleanupError, undefined);
        assert.deepEqual(await progressing, { count: 2 });
        assert.equal(b.realm, realm);
        assert.equal(b.live, true);
        assert.deepEqual(await call(b, 'home'), { count: 3 });
      } finally {
        resumeX?.(); resumeSocket?.();
        await finished?.catch(() => {});
      }
      await b.stop();
      assert.equal(b.cleanupError, undefined);
    });
  });

test('Cat resource close waits distinguish synchronous destruction from the queued native close dispatch',
  { timeout: 30000 }, async t => {
    const session = await sessionFor(t, counterSource), node = session.node;
    const output = new node.builtins.stream.Writable({ write(_chunk, _encoding, done) { done(); } });
    const input = node.fs.createReadStream('/source.js');
    const resources = [[output, node.resourceCleanup], [input, node.volume.streamCleanup]];
    const observations = [];
    for (const [value, owners] of resources)
      EventEmitter.prototype.on.call(value, 'close', () =>
        observations.push({ value, owned: owners.has(value), closed: value.closed }));
    session.resourceScope().close();
    for (const [value, owners] of resources) {
      assert.equal(value.destroyed, true);
      assert.equal(value.closed, true, 'the native destroy callback has completed synchronously');
      assert.equal(owners.has(value), true);
    }
    const closing = node.close();
    assert.deepEqual(observations, [], 'native close is still queued');
    for (const [value, owners] of resources)
      assert.equal(owners.has(value), true, 'the synchronous closed flag is not full dispatch');
    await Promise.all([closing, session.stop()]);
    assert.deepEqual(observations, resources.map(([value]) => ({ value, owned: true, closed: true })));
    for (const [value, owners] of resources) {
      assert.equal(owners.has(value), false);
      assert.equal(Object.hasOwn(value, 'emit'), false);
    }
    assert.equal(session.cleanupError, undefined);
    assert.equal(node.resourceCleanup.size, 0);
    assert.equal(node.volume.streamCleanup.size, 0);
    assert.equal(session.adapterTasks.size, 0);
  });

test('Cat synchronously destroyed silent native streams do not acquire an invented close requirement',
  { timeout: 30000 }, async t => {
    const session = await sessionFor(t, counterSource), node = session.node;
    const values = [
      new node.builtins.stream.Readable({ emitClose: false, read() {} }),
      new node.builtins.stream.Writable({
        emitClose: false, write(_chunk, _encoding, done) { done(); },
      }),
      new node.builtins.stream.PassThrough({ emitClose: false }),
    ];
    let closes = 0;
    for (const value of values) value.on('close', () => { closes++; });
    session.resourceScope().close();
    for (const value of values) assert.equal(value.closed, true);
    await Promise.all([node.close(), session.stop()]);
    assert.equal(closes, 0, 'Node does not emit close when both native stream sides disable it');
    for (const value of values) {
      assert.equal(node.resourceCleanup.has(value), false);
      assert.equal(Object.hasOwn(value, 'emit'), false);
    }
    assert.equal(node.streams.size, 0);
    assert.equal(node.resourceCleanup.size, 0);
    assert.equal(session.cleanupError, undefined);
  });

const terminalFailureSource = `
  'use strict';
  const fs = require('fs');
  const { Writable, PassThrough } = require('stream');
  let mode, failure, observe, finishDestroy, resumeFailure;
  const failureGate = new Promise(resolve => { resumeFailure = resolve; });
  const state = { calls: 0, completed: 0, asyncResumed: 0, rejected: 0, destroys: 0, hooks: 0 };
  const fixture = {
    state,
    configure(ending, value, observer) { mode = ending; failure = value; observe = observer; },
    releaseDestroy() { finishDestroy?.(); },
    releaseFailure() { resumeFailure(); },
  };
  process.emit('l26-terminal-fixture', fixture);
  module.exports.arm = () => {
    const output = new Writable({
      write(_chunk, _encoding, done) { done(); },
      destroy(error, done) {
        state.destroys++;
        finishDestroy = () => { finishDestroy = null; done(error); };
        observe('destroy');
      },
    });
    const sibling = new PassThrough();
    const descriptor = fs.openSync('/l26-retained.txt', 'w');
    function listener(...args) {
      state.calls++;
      observe('callback', { receiver: this, args });
      if (mode === 'async') {
        const result = (async () => {
          await failureGate;
          state.asyncResumed++;
          observe('async-resumed');
          state.completed++;
          throw failure;
        })();
        result.catch(error => { state.rejected++; observe('rejected', error); });
        return result;
      }
      state.completed++;
      if (mode === 'sync') {
        observe('throw', failure);
        throw failure;
      }
    }
    Object.assign(fixture, { output, sibling, descriptor, listener,
      registration: output.once('close', listener) });
    observe('armed');
    return { armed: true };
  };
  module.exports.stop = () => { state.hooks++; observe('hook'); };
`;

function terminalSignal() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
}

const terminalFailures = [
  { name: 'normal', ending: 'normal' },
  ...[
    ['null', null], ['undefined', undefined], ['false', false], ['0', 0],
    ['-0', -0], ['NaN', NaN], ['0n', 0n], ["''", ''],
  ].flatMap(([name, value]) => ['sync', 'async'].map(ending => ({ name, value, ending }))),
];

for (const { name, value: failure, ending } of terminalFailures) {
  test(`Cat stream close terminal ${ending} ${name} preserves stop failure ownership`, async t => {
    const b = await sessionFor(t, counterSource), bRealm = b.realm, bGeneration = b.generation;
    const a = new CatSession({ dataRoot: rootFor(t), installationId: 'terminal-a', generation: 1,
      revision: { source: terminalFailureSource, version: '1', config: {} }, limits: b.limits });
    let fixture;
    a.node.process.once('l26-terminal-fixture', value => { fixture = value; });
    t.after(async () => {
      fixture?.releaseDestroy();
      fixture?.releaseFailure();
      await a.stop().catch(() => {});
    });
    await a.start();
    a.activate();
    const node = a.node, services = a.services, server = services.main;
    const serverClosure = observeServerClosure(server, services);
    const destroying = terminalSignal(), closed = terminalSignal(), rejected = terminalSignal();
    const observations = [], notifications = [];
    let origin, identity, stopSettled = false, nodeClosed = false, nativeCloses = 0, siblingCloses = 0;
    const observer = (event, value) => {
      if (event === 'armed') { origin = a.scope(); identity = a.scopeIdentity(); }
      observations.push({ event, value, scope: a.scope(), identity: a.scopeIdentity(),
        cleanup: a.isCleanupScope(a.scope()), live: a.live, stopPending: !stopSettled,
        nodeClosed, pending: a.adapterTasks.size,
        owned: node.resourceCleanup.has(fixture.output) && node.streams.has(fixture.output) });
      if (event === 'destroy') destroying.resolve();
      if (event === 'rejected') rejected.resolve();
    };
    fixture.configure(ending, failure, observer);
    assert.deepEqual(await call(a, 'arm'), { armed: true });
    await a.whenAdaptersIdle();
    assert.equal(origin.signal.aborted, true, 'registration belongs to the completed real request');
    assert.equal(a.isCleanupScope(origin), false);
    assert.deepEqual(identity, { installationId: 'terminal-a', generation: a.generation,
      siteId: 'default', requestId: origin.requestId });
    assert.ok(origin.requestId > 0);
    const { output, sibling, descriptor, listener } = fixture;
    assert.equal(fixture.registration, output);
    assert.equal(output instanceof stream.Writable, true, 'the actual native stream');
    assert.equal(output.listeners('close').includes(listener), true, 'removable callback identity');
    assert.equal(output.rawListeners('close').some(value => value.listener === listener), true);
    const fd = node.volume.fd(descriptor);
    assert.equal(fstatSync(fd).isFile(), true);
    EventEmitter.prototype.on.call(output, 'close', function(...args) {
      nativeCloses++;
      observations.push({ event: 'native-close', receiver: this, args,
        owned: node.resourceCleanup.has(output) && node.streams.has(output), stopPending: !stopSettled });
      closed.resolve();
    });
    EventEmitter.prototype.on.call(sibling, 'close', () => { siblingCloses++; });
    a.onStop = session => notifications.push({
      realm: session.realm, pending: session.adapterTasks.size, cleanupError: session.cleanupError,
    });
    assert.deepEqual(await call(b, 'home'), { count: 1 });
    assert.equal(fixture.state.calls, 0);
    assert.equal(a.adapterTasks.size, 0);
    const stopped = a.stop();
    stopped.then(() => { stopSettled = true; }, () => { stopSettled = true; });
    await Promise.race([destroying.promise, stopped]);
    assert.ok(node.closing);
    assert.equal(fixture.state.destroys, 1);
    assert.equal(output.destroyed, true);
    assert.equal(output.closed, false, 'the actual native destroy callback is held');
    assert.equal(node.resourceCleanup.has(output), true);
    assert.equal(node.streams.has(output), true);
    assert.equal(fixture.state.calls, 0);
    assert.equal(nativeCloses, 0);
    assert.equal(node.failure, null);
    assert.equal(stopSettled, false);
    assert.equal(notifications.length, 0);
    assert.deepEqual(await call(b, 'home'), { count: 2 }, 'B progresses while A awaits real stream close');
    assert.equal(stopSettled, false);
    assert.equal(fixture.state.calls, 0);
    assert.equal(nativeCloses, 0);
    fixture.releaseDestroy();

    await Promise.race([closed.promise, stopped]);
    assert.equal(nativeCloses, 1);
    const native = observations.filter(value => value.event === 'native-close');
    assert.equal(native.length, 1);
    assert.equal(native[0].receiver, output);
    assert.deepEqual(native[0].args, []);
    assert.equal(native[0].owned, true, 'physical ownership lasts through all close listeners');
    assert.equal(native[0].stopPending, true);
    const callbacks = observations.filter(value => value.event === 'callback');
    assert.equal(callbacks.length, 1);
    const callback = callbacks[0];
    assert.equal(callback.value.receiver, output);
    assert.deepEqual(Array.from(callback.value.args), []);
    assert.equal(callback.scope, origin);
    assert.deepEqual(callback.identity, identity);
    assert.equal(callback.cleanup, false);
    assert.equal(callback.live, false);
    assert.equal(callback.stopPending, true);
    assert.equal(callback.nodeClosed, false);
    assert.equal(callback.pending, 0);
    assert.equal(callback.owned, true);
    assert.equal(output.listeners('close').includes(listener), false, 'the once listener was removed');
    assert.equal(output.destroyed, true);
    assert.equal(output.closed, true);
    assert.equal(node.resourceCleanup.has(output), false);
    assert.equal(node.streams.has(output), false);
    assert.equal(Object.hasOwn(output, 'emit'), false);
    assert.equal(a.adapterTasks.size, 0, 'no guest promise is registered as a physical adapter');
    assert.equal(stopSettled, false);

    if (ending === 'async') {
      await node.closing;
      nodeClosed = true;
      assert.equal(node.failure, null, 'node.close does not wait for an arbitrary guest promise');
      assert.equal(fixture.state.completed, 0);
      assert.equal(fixture.state.rejected, 0);
      assert.equal(stopSettled, false);
      // The real close dispatch has retired the stream. Reject in the same
      // microtask turn, before stop's existing adapter-dispatch fence.
      fixture.releaseFailure();
      await Promise.race([rejected.promise, stopped]);
      const resumed = observations.filter(value => value.event === 'async-resumed');
      assert.equal(resumed.length, 1);
      assert.equal(resumed[0].scope, origin);
      assert.deepEqual(resumed[0].identity, identity);
      assert.equal(resumed[0].cleanup, false);
      assert.equal(resumed[0].live, false);
      assert.equal(resumed[0].stopPending, true);
      assert.equal(resumed[0].nodeClosed, true);
      assert.equal(resumed[0].pending, 0);
      assert.equal(resumed[0].owned, false);
    }
    const expectedCode = ending === 'normal' ? undefined : 'SERVICE_ERROR';
    if (expectedCode) {
      const failures = observations.filter(value =>
        value.event === (ending === 'async' ? 'rejected' : 'throw'));
      assert.equal(failures.length, 1);
      assert.ok(Object.is(failures[0].value, failure), 'the precise falsy throw/rejection value');
      assert.equal(failures[0].scope, origin);
      assert.deepEqual(failures[0].identity, identity);
      assert.equal(failures[0].cleanup, false);
      assert.equal(failures[0].live, false);
      assert.equal(failures[0].stopPending, true);
      assert.equal(failures[0].nodeClosed, ending === 'async');
      assert.equal(failures[0].pending, 0);
      assert.equal(failures[0].owned, ending === 'sync');
      assert.equal(node.failure?.code, expectedCode, 'recorded while final stop is still pending');
      assert.equal(stopSettled, false);
      assert.equal(notifications.length, 0);
      assert.notEqual(a.realm, null);
      if (ending === 'sync') await assert.rejects(node.closing, { code: expectedCode });
      await assert.rejects(stopped, { code: expectedCode });
    } else {
      await node.closing;
      assert.equal(node.failure, null);
      await stopped;
    }
    assert.equal(a.cleanupError, expectedCode);
    assert.deepEqual(notifications, [{ realm: null, pending: 0, cleanupError: expectedCode }]);
    assert.deepEqual({ ...fixture.state }, { calls: 1, completed: 1,
      asyncResumed: ending === 'async' ? 1 : 0, rejected: ending === 'async' ? 1 : 0,
      destroys: 1, hooks: 1 });
    assert.equal(nativeCloses, 1);
    assert.equal(siblingCloses, 1);
    assert.equal(sibling.destroyed, true);
    assert.equal(sibling.closed, true);
    assert.throws(() => fstatSync(fd), { code: 'EBADF' });
    assertServerClosure(server, services, serverClosure.state, 1);
    await serverClosure.closed;
    assert.equal(node.volume.closed, true);
    assert.equal(node.volume.streamCleanup.size, 0);
    assert.equal(node.resourceCleanup.size, 0);
    assert.equal(services.serverCleanup.size, 0);
    assert.equal(a.root.signal.aborted, true);
    assert.equal(a.resourceScope().signal.aborted, true);
    assert.equal(a.realm, null);
    const stats = a.stats();
    assert.equal(stats.live, false);
    for (const key of ['timers', 'invocations', 'pendingAdapterTasks', 'networkRequests',
      'listeners', 'sockets', 'streams', 'webResources', 'descriptors']) assert.equal(stats[key], 0, key);
    assert.equal(b.realm, bRealm);
    assert.equal(b.generation, bGeneration);
    assert.equal(b.live, true);
    assert.deepEqual(await call(b, 'home'), { count: 3 });
    await b.whenAdaptersIdle();
    assert.equal(b.adapterTasks.size, 0);
    await b.stop();
    assert.equal(b.cleanupError, undefined);
  });
}
