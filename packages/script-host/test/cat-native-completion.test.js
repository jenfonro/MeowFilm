import test from 'node:test';
import assert from 'node:assert/strict';
import * as crypto from 'node:crypto';
import * as zlib from 'node:zlib';
import * as dns from 'node:dns';
import { once } from 'node:events';
import { fstatSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CatSession } from '../src/cat-session.js';
import { SCRIPT_LIMITS } from '../src/host.js';
import { ResourceScope } from '../src/scope.js';

const nativeModules = { crypto, zlib, dns };
const bytes = [0, 255, 65, 0, 128, 10];
const cases = [
  { name: 'crypto.pbkdf2', module: 'crypto', method: 'pbkdf2',
    args: () => ['password', 'salt', 1, 20, 'sha1'] },
  { name: 'zlib.gzip', module: 'zlib', method: 'gzip',
    args: () => [Buffer.from(bytes)] },
  { name: 'zlib.gunzip error', module: 'zlib', method: 'gunzip',
    args: () => [Buffer.from('not a gzip body')] },
  { name: 'dns.lookup', module: 'dns', method: 'lookup',
    args: () => ['localhost', { family: 4 }] },
];

function deferred() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
}

const invoke = session => session.invoke({ siteId: 'default', operation: 'home', payload: {} });
const counterSource = 'let count = 0; module.exports.home = () => ({ count: ++count });';

// Expose only this fixture's arming/observation functions, not a fake realm or
// native completion. Calling arm and stop in one turn proves the real callback
// has not been delivered; it does not rely on how long the native job takes.
const source = `
  'use strict';
  const fs = require('fs');
  let observe, nativeDone, finishNative, finishHook, resumeFailure, failureGate, mode;
  let count = 0;
  const state = { calls: 0, completed: 0, asyncResumed: 0, nonterminal: 0,
    background: 0, hookEntered: 0, hookResumed: 0, cleanup: 0, written: 0 };
  const hookGate = new Promise(resolve => { finishHook = resolve; });
  function forbiddenBackground() {
    process.emit('l20-background');
    setImmediate(() => { state.background++; });
  }
  function complete() { state.completed++; finishNative(); }
  const fixture = {
    state,
    arm(spec, ending, observer) {
      observe = observer; mode = ending;
      nativeDone = new Promise(resolve => { finishNative = resolve; });
      failureGate = new Promise(resolve => { resumeFailure = resolve; });
      process.on('l20-background', () => { state.nonterminal++; });
      return require(spec.module)[spec.method](...spec.args, function(...args) {
        state.calls++;
        observe('callback', { receiver: this, args });
        forbiddenBackground();
        if (mode === 'async') {
          return (async () => {
            await failureGate;
            state.asyncResumed++;
            observe('async-resumed');
            forbiddenBackground();
            try { throw new Error('L20 delayed native callback failure'); }
            finally { complete(); }
          })();
        }
        complete();
        if (mode === 'sync') throw new Error('L20 synchronous native callback failure');
      });
    },
    releaseFailure() { resumeFailure?.(); },
    releaseHook() { finishHook(); },
  };
  process.emit('l20-fixture', fixture);
  module.exports.home = () => ({ count: ++count });
  module.exports.stop = async () => {
    if (!nativeDone) return;
    state.hookEntered++;
    observe('hook-entered');
    await nativeDone;
    state.hookResumed++;
    observe('hook-resumed');
    await hookGate;
    process.emit('l20-background');
    // A real cleanup-owned immediate is also a FIFO observation boundary for
    // background immediates queued by the old native callback.
    await new Promise(resolve => setImmediate(() => { state.cleanup++; resolve(); }));
    fs.writeFileSync('/native-completion.json', JSON.stringify(state));
    state.written++;
    observe('hook-written');
  };
`;

async function sessionFor(t, installationId, script) {
  const dataRoot = mkdtempSync(join(tmpdir(), 'meowfilm-cat-native-completion-'));
  const session = new CatSession({ dataRoot, installationId, generation: 1,
    revision: { source: script, version: '1', config: {} },
    limits: { ...SCRIPT_LIMITS, initTimeoutMs: 60_000,
      requestTimeoutMs: 60_000, cleanupTimeoutMs: 3_000 } });
  let fixture;
  session.node.process.once('l20-fixture', value => { fixture = value; });
  t.after(async () => {
    fixture?.releaseFailure();
    fixture?.releaseHook();
    try { await session.stop(); }
    catch { /* The test body asserts the expected cleanup result. */ }
    finally { rmSync(dataRoot, { recursive: true, force: true }); }
  });
  await session.start();
  session.activate();
  await session.whenAdaptersIdle();
  return { session, fixture, dataRoot };
}

async function nativeOracle(spec) {
  const delivered = deferred();
  let calls = 0;
  const returned = nativeModules[spec.module][spec.method](...spec.args(), function(...args) {
    calls++;
    delivered.resolve({ receiver: this, args });
  });
  assert.equal(calls, 0, 'the selected native callback is genuinely asynchronous');
  const result = await delivered.promise;
  return { ...result, returned, calls };
}

function assertNativeValues(spec, args) {
  args = Array.from(args);
  if (spec.method === 'pbkdf2') {
    assert.deepEqual(args, [null, Buffer.from('0c60c80f961f0e71f3a9b524af6012062fe037a6', 'hex')]);
  } else if (spec.method === 'gzip') {
    assert.equal(args.length, 2);
    assert.equal(args[0], null);
    assert.equal(Buffer.isBuffer(args[1]), true);
    assert.deepEqual(zlib.gunzipSync(args[1]), Buffer.from(bytes));
  } else if (spec.method === 'gunzip') {
    assert.equal(args.length, 1);
    assert.equal(args[0].name, 'Error');
    assert.equal(args[0].code, 'Z_DATA_ERROR');
  } else {
    assert.deepEqual(args, [null, '127.0.0.1', 4]);
  }
}

function assertNativeCallback(spec, observed, returned, oracle) {
  assertNativeValues(spec, observed.args);
  assertNativeValues(spec, oracle.args);
  const values = args => Array.from(args, value => value instanceof Error
    ? { name: value.name, code: value.code, message: value.message } : value);
  assert.deepEqual(values(observed.args), values(oracle.args), 'native callback arguments');
  if (spec.module === 'dns' && spec.method === 'lookup') {
    assert.equal(returned, undefined, 'DNS callback facade return value');
  } else {
    assert.equal(typeof returned, typeof oracle.returned, 'native return type');
    if (oracle.returned === null || typeof oracle.returned !== 'object') {
      assert.equal(returned, oracle.returned, 'native return value');
    } else {
      assert.equal(Object.getPrototypeOf(returned), Object.getPrototypeOf(oracle.returned));
    }
    assert.equal(observed.receiver === returned, oracle.receiver === oracle.returned,
      'native callback receiver/return identity');
  }
  if (oracle.receiver === undefined || oracle.receiver === null) {
    assert.equal(observed.receiver, oracle.receiver, 'native callback receiver');
  } else {
    assert.equal(Object.getPrototypeOf(observed.receiver), Object.getPrototypeOf(oracle.receiver),
      'native callback receiver prototype, not a wrapper or session');
    assert.equal(observed.receiver.constructor, oracle.receiver.constructor);
  }
}

function retainResources(session) {
  const server = session.services.main;
  assert.equal(server.listening, true);
  let serverCloses = 0, streamCloses = 0;
  server.on('close', () => { serverCloses++; });
  const serverClosed = once(server, 'close');
  const stream = new session.node.builtins.stream.PassThrough();
  stream.on('close', () => { streamCloses++; });
  const streamClosed = once(stream, 'close');
  const descriptor = session.node.fs.openSync('/retained.txt', 'w');
  const fd = session.node.volume.fd(descriptor);
  assert.equal(fstatSync(fd).isFile(), true);
  assert.equal(stream.closed, false);
  return {
    async assertClosed() {
      // Check facts before awaiting the barriers: stop itself must have waited.
      assert.equal(serverCloses, 1);
      assert.equal(streamCloses, 1);
      assert.equal(server.listening, false);
      assert.equal(server.address(), null);
      assert.equal(stream.destroyed, true);
      assert.equal(stream.closed, true);
      assert.throws(() => fstatSync(fd), { code: 'EBADF' });
      await Promise.all([serverClosed, streamClosed]);
      assert.equal(session.node.volume.closed, true);
      assert.equal(session.root.signal.aborted, true);
      assert.equal(session.resourceScope().signal.aborted, true);
      assert.equal(session.realm, null);
      assert.equal(session.node.resourceCleanup.size, 0);
      assert.equal(session.adapterTasks.size, 0);
      const stats = session.stats();
      assert.equal(stats.live, false);
      for (const key of ['timers', 'invocations', 'pendingAdapterTasks', 'networkRequests',
        'listeners', 'sockets', 'streams', 'webResources', 'descriptors']) {
        assert.equal(stats[key], 0, key);
      }
    },
  };
}

for (const spec of cases) {
  for (const ending of ['before-stop', 'normal', 'sync', 'async']) {
    const scenario = ending === 'before-stop' ? 'completion before stop is the native control'
      : `in-flight ${ending} completion survives same-turn stop`;
    test(`Cat ${spec.name} ${scenario}`, { timeout: 30_000 }, async t => {
      const oracle = await nativeOracle(spec);
      assert.equal(oracle.calls, 1);
      const { session: a, fixture, dataRoot } = await sessionFor(t, 'a', source);
      const { session: b } = await sessionFor(t, 'b', counterSource);
      const bGeneration = b.generation, bRealm = b.realm;
      assert.deepEqual(await invoke(b), { count: 1 });
      assert.deepEqual(await invoke(a), { count: 1 });
      await a.whenAdaptersIdle();
      assert.equal(a.adapterTasks.size, 0);
      const resources = retainResources(a);
      const origin = new ResourceScope(a.root.signal);
      origin.requestId = 71;
      origin.siteId = 'default';
      const callbackEntered = deferred(), hookResumed = deferred(), observations = [];
      const observer = (event, value) => {
        observations.push({ event, value, scope: a.scope(),
          cleanup: a.isCleanupScope(a.scope()), live: a.live,
          pending: a.adapterTasks.size, completed: fixture.state.completed });
        if (event === 'callback') callbackEntered.resolve();
        if (event === 'hook-resumed') hookResumed.resolve();
      };
      const returned = a.contextScope.run(origin, () => fixture.arm({
        module: spec.module, method: spec.method, args: spec.args(),
      }, ending, observer));
      assert.equal(fixture.state.calls, 0, 'native callback has not been delivered in the arming turn');
      assert.equal(fixture.state.completed, 0);
      assert.equal(a.adapterTasks.size, 1, 'real native work is registered before stop');
      if (ending === 'before-stop') {
        await callbackEntered.promise;
        await a.whenAdaptersIdle();
        assert.equal(fixture.state.completed, 1);
        assert.equal(fixture.state.nonterminal, 1, 'normal background event is not disabled');
        assert.equal(fixture.state.background, 1, 'normal background immediate is not disabled');
        assert.deepEqual(await invoke(a), { count: 2 });
        await a.whenAdaptersIdle();
      }
      // The native operation is session-owned even after its registering
      // request retires. The stop hook must not relabel that request as cleanup.
      origin.close();
      const stopped = a.stop();
      let stopSettled = false;
      stopped.then(() => { stopSettled = true; }, () => { stopSettled = true; });
      assert.equal(a.live, false);
      assert.equal(a.root.signal.aborted, false, 'the hook still owns an open volume');
      assert.equal(fixture.state.hookEntered, 1);
      if (ending !== 'before-stop') {
        assert.equal(fixture.state.calls, 0, 'stop begins in the same turn, before native delivery');
        assert.equal(fixture.state.completed, 0);
        assert.equal(a.adapterTasks.size, 1, 'stop cannot clear the in-flight tracker');
      }
      await Promise.race([callbackEntered.promise, stopped]);
      await Promise.race([a.whenAdaptersIdle(), stopped]);
      const callback = observations.filter(value => value.event === 'callback');
      assert.equal(callback.length, 1);
      assert.equal(callback[0].scope, origin);
      assert.equal(callback[0].cleanup, false);
      assert.equal(callback[0].live, ending === 'before-stop');
      assert.equal(callback[0].pending, 1, 'physical work is retained until callback dispatch finishes');
      assertNativeCallback(spec, callback[0].value, returned, oracle);
      assert.equal(a.adapterTasks.size, 0, 'the real native callback has now physically completed');
      assert.equal(stopSettled, false);
      assert.equal(fixture.state.written, 0);
      if (ending === 'async') {
        assert.equal(fixture.state.completed, 0, 'physical completion is not guest Promise completion');
        assert.equal(fixture.state.hookResumed, 0, 'the hook is still waiting for its guest completion');
        assert.equal(fixture.state.asyncResumed, 0);
      } else {
        await Promise.race([hookResumed.promise, stopped]);
        assert.equal(fixture.state.completed, 1);
      }
      assert.deepEqual(await invoke(b), { count: 2 }, 'B progresses while A is waiting in its hook');
      assert.equal(stopSettled, false);
      if (ending === 'async') {
        // This is a delayed rejection after real independent work, not an
        // immediate Promise.reject and not an unrelated never-settling promise.
        fixture.releaseFailure();
        await Promise.race([hookResumed.promise, stopped]);
        const resumed = observations.filter(value => value.event === 'async-resumed');
        assert.equal(resumed.length, 1);
        assert.equal(resumed[0].scope, origin);
        assert.equal(resumed[0].cleanup, false);
        assert.equal(resumed[0].live, false);
        assert.equal(fixture.state.completed, 1);
        assert.equal(fixture.state.asyncResumed, 1);
      }
      fixture.releaseHook();
      if (ending === 'sync' || ending === 'async') {
        await assert.rejects(stopped, { code: 'SERVICE_ERROR' });
      } else {
        await stopped;
      }
      const hook = observations.filter(value => value.event.startsWith('hook-'));
      assert.deepEqual(hook.map(value => value.event), ['hook-entered', 'hook-resumed', 'hook-written']);
      const cleanupScope = hook[0].scope;
      assert.notEqual(cleanupScope, origin);
      for (const event of hook) {
        assert.equal(event.scope, cleanupScope);
        assert.equal(event.cleanup, true);
        assert.equal(event.live, false);
      }
      const expected = { calls: 1, completed: 1, asyncResumed: ending === 'async' ? 1 : 0,
        nonterminal: ending === 'before-stop' ? 1 : 0,
        background: ending === 'before-stop' ? 1 : 0,
        hookEntered: 1, hookResumed: 1, cleanup: 1, written: 0 };
      assert.deepEqual(JSON.parse(readFileSync(
        join(dataRoot, 'volumes', 'a', 'native-completion.json'), 'utf8')), expected);
      assert.deepEqual({ ...fixture.state }, { ...expected, written: 1 });
      assert.equal(a.cleanupError, ending === 'sync' || ending === 'async' ? 'SERVICE_ERROR' : undefined);
      await resources.assertClosed();
      assert.equal(b.realm, bRealm);
      assert.equal(b.generation, bGeneration);
      assert.equal(b.live, true);
      assert.deepEqual(await invoke(b), { count: 3 });
      await b.whenAdaptersIdle();
      assert.equal(b.adapterTasks.size, 0);
      await b.stop();
      assert.equal(b.cleanupError, undefined);
      assert.equal(b.stats().pendingAdapterTasks, 0);
    });
  }
}

// Reuse the fixture observations and cleanup, but do not start background work
// from the closed resource scope or make the stop hook wait for native completion.
const lateCompletionSource = source + `
  fixture.arm = (spec, ending, observer) => {
    observe = observer; mode = ending;
    failureGate = new Promise(resolve => { resumeFailure = resolve; });
    return require(spec.module)[spec.method](...spec.args, function(...args) {
      state.calls++;
      observe('callback', { receiver: this, args });
      if (mode === 'async') {
        return (async () => {
          await failureGate;
          state.asyncResumed++;
          observe('async-resumed');
          state.completed++;
          throw new Error('L23 delayed native callback failure');
        })();
      }
      state.completed++;
      if (mode === 'sync') throw new Error('L23 synchronous native callback failure');
    });
  };
  module.exports.stop = () => {
    state.hookEntered++;
    observe('hook-entered');
    fs.writeFileSync('/native-completion.json', JSON.stringify(state));
    state.written++;
    observe('hook-written');
  };
`;

for (const ending of ['normal', 'sync', 'async']) {
  test(`Cat crypto.pbkdf2 late ${ending} completion settles after node.close`,
    { timeout: 30_000 }, async t => {
      const spec = cases[0];
      const oracle = await nativeOracle(spec);
      assert.equal(oracle.calls, 1);
      const { session: a, fixture, dataRoot } = await sessionFor(t, 'a', lateCompletionSource);
      const { session: b } = await sessionFor(t, 'b', counterSource);
      const bGeneration = b.generation, bRealm = b.realm;
      assert.deepEqual(await invoke(b), { count: 1 });
      // With no outstanding sockets, the real listener/PassThrough close
      // notifications drain before a native worker completion can be delivered.
      assert.equal(a.services.connections.size, 0);
      assert.equal(a.nativeRequests.size, 0);
      const resources = retainResources(a);
      const origin = new ResourceScope(a.root.signal);
      origin.requestId = 72;
      origin.siteId = 'default';
      const rootClosed = deferred(), callbackEntered = deferred(), asyncResumed = deferred();
      const observations = [], notifications = [];
      let nodeClosed = false, stopSettled = false;
      a.root.signal.addEventListener('abort', () => rootClosed.resolve(), { once: true });
      a.onStop = session => notifications.push({
        realm: session.realm, pending: session.adapterTasks.size, cleanupError: session.cleanupError,
      });
      const observer = (event, value) => {
        observations.push({ event, value, scope: a.scope(), cleanup: a.isCleanupScope(a.scope()),
          live: a.live, pending: a.adapterTasks.size, nodeClosed, stopPending: !stopSettled });
        if (event === 'callback') callbackEntered.resolve();
        if (event === 'async-resumed') asyncResumed.resolve();
      };
      const returned = a.contextScope.run(origin, () => fixture.arm({
        module: spec.module, method: spec.method, args: spec.args(),
      }, ending, observer));
      assert.equal(fixture.state.calls, 0);
      assert.equal(a.adapterTasks.size, 1);
      const nativeTasks = [...a.adapterTasks];
      origin.close();
      const stopped = a.stop();
      stopped.then(() => { stopSettled = true; }, () => { stopSettled = true; });

      await Promise.race([rootClosed.promise, stopped]);
      assert.ok(a.node.closing, 'root closure reaches the real node.close call in the same turn');
      await a.node.closing;
      nodeClosed = true;
      assert.equal(a.node.failure, null, 'node.close succeeded before any callback error');
      assert.equal(fixture.state.calls, 0, 'the real native callback is still undelivered');
      assert.equal(fixture.state.completed, 0);
      assert.equal(a.adapterTasks.size, 1, 'node.close must not clear the native tracker');
      assert.equal(stopSettled, false, 'stop is still waiting for physical adapter completion');
      assert.notEqual(a.realm, null);
      assert.equal(notifications.length, 0);

      await Promise.race([callbackEntered.promise, stopped]);
      await Promise.allSettled(nativeTasks);
      const callback = observations.filter(value => value.event === 'callback');
      assert.equal(callback.length, 1);
      assert.equal(callback[0].scope, origin);
      assert.equal(callback[0].cleanup, false);
      assert.equal(callback[0].live, false);
      assert.equal(callback[0].nodeClosed, true);
      assert.equal(callback[0].stopPending, true);
      assert.equal(callback[0].pending, 1, 'the callback still owns its physical tracker');
      assertNativeCallback(spec, callback[0].value, returned, oracle);
      assert.equal(a.adapterTasks.size, 0, 'native finally has settled, independently of guest promises');
      assert.equal(stopSettled, false);
      if (ending === 'async') {
        assert.equal(fixture.state.completed, 0);
        assert.equal(fixture.state.asyncResumed, 0);
        // Release only after the actual native tracker settles. The continuation
        // rejects in this microtask turn, before waitForAdapters' dispatch fence.
        fixture.releaseFailure();
        await Promise.race([asyncResumed.promise, stopped]);
        const resumed = observations.filter(value => value.event === 'async-resumed');
        assert.equal(resumed.length, 1);
        assert.equal(resumed[0].scope, origin);
        assert.equal(resumed[0].cleanup, false);
        assert.equal(resumed[0].live, false);
        assert.equal(resumed[0].nodeClosed, true);
        assert.equal(resumed[0].stopPending, true);
        assert.equal(resumed[0].pending, 0);
      }
      const cleanupError = ending === 'normal' ? undefined : 'SERVICE_ERROR';
      if (cleanupError) await assert.rejects(stopped, { code: cleanupError });
      else await stopped;
      assert.equal(a.cleanupError, cleanupError);
      assert.equal(a.node.failure?.message, ending === 'normal' ? undefined
        : ending === 'sync' ? 'L23 synchronous native callback failure' : 'L23 delayed native callback failure');
      assert.deepEqual(notifications, [{ realm: null, pending: 0, cleanupError }]);
      const saved = { calls: 0, completed: 0, asyncResumed: 0, nonterminal: 0, background: 0,
        hookEntered: 1, hookResumed: 0, cleanup: 0, written: 0 };
      assert.deepEqual(JSON.parse(readFileSync(
        join(dataRoot, 'volumes', 'a', 'native-completion.json'), 'utf8')), saved);
      assert.deepEqual({ ...fixture.state }, { ...saved, calls: 1, completed: 1,
        asyncResumed: ending === 'async' ? 1 : 0, written: 1 });
      await resources.assertClosed();
      assert.equal(b.realm, bRealm);
      assert.equal(b.generation, bGeneration);
      assert.equal(b.live, true);
      assert.deepEqual(await invoke(b), { count: 2 });
      await b.whenAdaptersIdle();
      assert.equal(b.adapterTasks.size, 0);
      await b.stop();
      assert.equal(b.cleanupError, undefined);
      assert.equal(b.stats().pendingAdapterTasks, 0);
    });
}

const falsyCompletionSource = lateCompletionSource + `
  state.rejected = 0;
  fixture.arm = (spec, ending, failure, observer) => {
    observe = observer; mode = ending;
    failureGate = new Promise(resolve => { resumeFailure = resolve; });
    return require(spec.module)[spec.method](...spec.args, function(...args) {
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
        // Observe the actual rejection, not merely entry into the continuation.
        // Return the original promise so the production catch still owns it.
        result.catch(error => { state.rejected++; observe('rejected', error); });
        return result;
      }
      state.completed++;
      observe('throw', failure);
      throw failure;
    });
  };
`;

const falsyNativeFailures = [
  ['null', null], ['undefined', undefined], ['false', false], ['0', 0],
  ['-0', -0], ['NaN', NaN], ['0n', 0n], ["''", ''],
];

for (const [name, failure] of falsyNativeFailures) for (const ending of ['sync', 'async']) {
  test(`Cat crypto.pbkdf2 late ${ending} ${name} failure remains reportable after node.close`, async t => {
    const spec = cases[0], oracle = await nativeOracle(spec);
    assert.equal(oracle.calls, 1);
    const { session: a, fixture, dataRoot } = await sessionFor(t, 'a', falsyCompletionSource);
    const { session: b } = await sessionFor(t, 'b', counterSource);
    const bGeneration = b.generation, bRealm = b.realm;
    assert.deepEqual(await invoke(b), { count: 1 });
    assert.equal(a.services.connections.size, 0);
    assert.equal(a.nativeRequests.size, 0);
    const resources = retainResources(a);
    const origin = new ResourceScope(a.root.signal);
    origin.requestId = 73;
    origin.siteId = 'default';
    const identity = { installationId: 'a', generation: a.generation,
      siteId: 'default', requestId: 73 };
    const rootClosed = deferred(), callbackEntered = deferred(), rejected = deferred();
    const observations = [], notifications = [];
    let nodeClosed = false, stopSettled = false;
    a.root.signal.addEventListener('abort', () => rootClosed.resolve(), { once: true });
    a.onStop = session => notifications.push({
      realm: session.realm, pending: session.adapterTasks.size, cleanupError: session.cleanupError,
    });
    const observer = (event, value) => {
      observations.push({ event, value, scope: a.scope(), identity: a.scopeIdentity(),
        cleanup: a.isCleanupScope(a.scope()), live: a.live, pending: a.adapterTasks.size,
        nodeClosed, stopPending: !stopSettled });
      if (event === 'callback') callbackEntered.resolve();
      if (event === 'rejected') rejected.resolve();
    };
    const returned = a.contextScope.run(origin, () => fixture.arm({
      module: spec.module, method: spec.method, args: spec.args(),
    }, ending, failure, observer));
    assert.equal(fixture.state.calls, 0);
    assert.equal(a.adapterTasks.size, 1);
    const nativeTasks = [...a.adapterTasks];
    origin.close();
    const stopped = a.stop();
    stopped.then(() => { stopSettled = true; }, () => { stopSettled = true; });

    await Promise.race([rootClosed.promise, stopped]);
    assert.ok(a.node.closing);
    await a.node.closing;
    nodeClosed = true;
    assert.equal(a.node.failure, null, 'node.close succeeds before the native callback');
    assert.equal(fixture.state.calls, 0, 'the actual native callback is still undelivered');
    assert.equal(fixture.state.completed, 0);
    assert.equal(fixture.state.rejected, 0);
    assert.equal(a.adapterTasks.size, 1, 'node.close retains the physical native tracker');
    assert.equal(stopSettled, false);
    assert.notEqual(a.realm, null);
    assert.equal(notifications.length, 0);

    await Promise.race([callbackEntered.promise, stopped]);
    await Promise.allSettled(nativeTasks);
    const callbacks = observations.filter(value => value.event === 'callback');
    assert.equal(callbacks.length, 1);
    const callback = callbacks[0];
    assert.equal(callback.scope, origin);
    assert.deepEqual(callback.identity, identity);
    assert.equal(callback.cleanup, false);
    assert.equal(callback.live, false);
    assert.equal(callback.nodeClosed, true);
    assert.equal(callback.stopPending, true);
    assert.equal(callback.pending, 1);
    assertNativeCallback(spec, callback.value, returned, oracle);
    assert.equal(a.adapterTasks.size, 0, 'the real native finally has released its tracker');
    assert.equal(stopSettled, false);
    if (ending === 'async') {
      assert.equal(fixture.state.completed, 0);
      assert.equal(fixture.state.rejected, 0);
      assert.equal(a.node.failure, null);
      // Release in the native-finally microtask turn, before the existing
      // waitForAdapters dispatch fence, without making stop await this promise.
      fixture.releaseFailure();
      await Promise.race([rejected.promise, stopped]);
      const resumed = observations.filter(value => value.event === 'async-resumed');
      assert.equal(resumed.length, 1);
      assert.equal(resumed[0].scope, origin);
      assert.deepEqual(resumed[0].identity, identity);
      assert.equal(resumed[0].cleanup, false);
      assert.equal(resumed[0].live, false);
      assert.equal(resumed[0].nodeClosed, true);
      assert.equal(resumed[0].stopPending, true);
      assert.equal(resumed[0].pending, 0);
    }
    const failures = observations.filter(value =>
      value.event === (ending === 'async' ? 'rejected' : 'throw'));
    assert.equal(failures.length, 1);
    assert.ok(Object.is(failures[0].value, failure), 'the exact falsy value, including -0/NaN/0n');
    assert.equal(failures[0].scope, origin);
    assert.deepEqual(failures[0].identity, identity);
    assert.equal(failures[0].cleanup, false);
    assert.equal(failures[0].live, false);
    assert.equal(failures[0].nodeClosed, true);
    assert.equal(failures[0].stopPending, true);
    assert.equal(failures[0].pending, ending === 'async' ? 0 : 1);
    assert.equal(a.node.failure?.code, 'SERVICE_ERROR', 'the failure is recorded before final stop');
    assert.equal(stopSettled, false, 'actual rejection and recording precede the dispatch fence');
    assert.equal(notifications.length, 0);
    assert.notEqual(a.realm, null);
    await assert.rejects(stopped, { code: 'SERVICE_ERROR' });
    assert.equal(a.cleanupError, 'SERVICE_ERROR');
    assert.deepEqual(notifications, [{ realm: null, pending: 0, cleanupError: 'SERVICE_ERROR' }]);
    const saved = { calls: 0, completed: 0, asyncResumed: 0, nonterminal: 0, background: 0,
      hookEntered: 1, hookResumed: 0, cleanup: 0, written: 0, rejected: 0 };
    assert.deepEqual(JSON.parse(readFileSync(
      join(dataRoot, 'volumes', 'a', 'native-completion.json'), 'utf8')), saved);
    assert.deepEqual({ ...fixture.state }, { ...saved, calls: 1, completed: 1,
      asyncResumed: ending === 'async' ? 1 : 0, rejected: ending === 'async' ? 1 : 0, written: 1 });
    await resources.assertClosed();
    assert.equal(b.realm, bRealm);
    assert.equal(b.generation, bGeneration);
    assert.equal(b.live, true);
    assert.deepEqual(await invoke(b), { count: 2 });
    assert.deepEqual(await invoke(b), { count: 3 });
    await b.whenAdaptersIdle();
    assert.equal(b.adapterTasks.size, 0);
    await b.stop();
    assert.equal(b.cleanupError, undefined);
    assert.equal(b.stats().pendingAdapterTasks, 0);
  });
}
