import test from 'node:test';
import assert from 'node:assert/strict';
import { createScriptHost } from '../src/index.js';
import { NativeSession } from '../src/native-session.js';
import { fault } from '../src/errors.js';
import { lxScript, target, deferred } from './fixtures.js';

const timeout = 15_000;
const responseURL = 'https://fixture.invalid/boundary-response';
const naturalURL = 'https://fixture.invalid/boundary-natural';
const responseBytes = Buffer.from([0, 255, 128, 65]);

function capturingHost(options = {}) {
  const sessions = [];
  const host = createScriptHost({ ...options, adapters: { lx: input => {
    const session = new NativeSession(input);
    sessions.push(session);
    return session;
  } } });
  return { host, sessions };
}

function assertClosed(exit) {
  assert.equal(exit.stopped, true);
  for (const key of ['threadCount', 'runtimeCount', 'contextCount', 'activeCalls',
    'hostCalls', 'timers', 'jobsPending', 'byteBuffers', 'byteBufferBytes', 'heapBytes', 'heapObjects']) {
    assert.equal(exit[key], 0, key);
  }
}

async function withoutFallbackStop(t, engine, scenario) {
  let timer, usedFallback = false;
  const expired = new Promise((_resolve, reject) => {
    timer = setTimeout(() => {
      usedFallback = true;
      t.diagnostic('external fallback stop fired; this is a failed request-control scenario');
      reject(new Error('LX callback boundary exceeded its external deadline'));
      engine.stop().catch(() => {});
    }, 5_000);
  });
  try {
    await Promise.race([Promise.resolve().then(scenario), expired]);
    assert.equal(usedFallback, false, 'external stop must not stand in for request-controlled exit');
  } finally { clearTimeout(timer); }
}

// Authored QuickJS fixture. Getter work is finite; all signals use public LX APIs.
function callbackFixture(kind, stage) {
  let callbacks = 0, entries = 0, finished = 0, elapsed = 0, delivery = null;
  let ownHasCalls = 0, foreignHasCalls = 0, hasResults;
  const ownSet = new Set(['present']), originalHas = Set.prototype.has;
  function replacementHas(value) {
    if (this !== ownSet) { foreignHasCalls++; return false; }
    ownHasCalls++;
    return originalHas.call(this, value);
  }
  const snapshot = () => ({
    callbacks, entries, finished, elapsed, delivery, ownHasCalls, foreignHasCalls, hasResults,
    mutableHas: Set.prototype.has === replacementHas,
  });
  return async ({ info }) => {
    if (info.musicInfo.probe) return snapshot();
    Set.prototype.has = replacementHas;
    hasResults = [ownSet.has('present'), ownSet.has('missing')];
    return new Promise(resolve => {
      const work = () => {
        entries++;
        const started = Date.now();
        lx.send('updateAlert', { log: JSON.stringify({
          kind, stage, started, callbacks, entries, ownHasCalls, foreignHasCalls, hasResults,
          mutableHas: Set.prototype.has === replacementHas,
        }) });
        while (Date.now() - started < 600) {}
        elapsed = Date.now() - started;
        finished++;
        lx.request('https://fixture.invalid/boundary-natural', { encoding: null }, () => {});
        resolve(snapshot());
      };
      const run = (failure, response, body) => {
        callbacks++;
        if (kind === 'network') {
          if (failure) throw failure;
          delivery = { statusCode: response.statusCode, bytes: Array.from(body), alias: body === response.raw };
        }
        if (stage === 'then') {
          const value = {};
          Object.defineProperty(value, 'then', { configurable: true, get() {
            Object.defineProperty(value, 'then', { value: undefined });
            work();
            return undefined;
          } });
          return value;
        }
        if (stage === 'promise-constructor') {
          const value = Promise.resolve(null);
          Object.defineProperty(value, 'constructor', { configurable: true, get() {
            // Subsequent species lookup is ordinary data, not another work segment.
            Object.defineProperty(value, 'constructor', { value: Promise });
            work();
            return Promise;
          } });
          return value;
        }
        const field = stage.endsWith('name') ? 'name' : 'message';
        const problem = { name: 'InternalError', message: 'synthetic callback failure' };
        Object.defineProperty(problem, field, { get() {
          work();
          return field === 'name' ? 'Error' : 'synthetic callback failure';
        } });
        if (stage.startsWith('throw')) throw problem;
        return Promise.reject(problem);
      };
      if (kind === 'timer') setTimeout(run, 1);
      else if (kind === 'microtask') queueMicrotask(run);
      else lx.request('https://fixture.invalid/boundary-response', { encoding: null }, run);
    });
  };
}

function stackFailureFixture() {
  let callbacks = 0, entries = 0, finished = 0, elapsed = 0, failure, response, body;
  const snapshot = () => ({ callbacks, entries, finished, elapsed, failure, response, body });
  return async ({ info }) => {
    if (info.musicInfo.probe) return snapshot();
    Error.prepareStackTrace = () => {
      Error.prepareStackTrace = undefined;
      entries++;
      const started = Date.now();
      lx.send('updateAlert', { log: JSON.stringify({
        kind: 'network', stage: 'prepare-stack', started, callbacks, entries,
      }) });
      while (Date.now() - started < 600) {}
      elapsed = Date.now() - started;
      finished++;
      lx.request('https://fixture.invalid/boundary-natural', { encoding: null }, () => {});
      return 'synthetic stack';
    };
    return new Promise(resolve => {
      lx.request('https://fixture.invalid/boundary-response', {}, (error, result, value) => {
        callbacks++;
        failure = error ? { code: error.code, name: error.name, message: error.message } : null;
        response = result;
        body = value;
        resolve(snapshot());
      });
    });
  };
}

async function controlledBoundary(t, kind, stage, ending) {
  const networkEntered = deferred(), releaseNetwork = deferred(), entered = deferred(), natural = deferred();
  const callbackDiagnostic = deferred(), controller = new AbortController();
  const diagnostics = [], failures = [], markers = [];
  const stack = stage === 'prepare-stack';
  const expectsDiagnostic = stage.startsWith('throw') || stage.startsWith('reject');
  const hostHas = Set.prototype.has;
  let requests = 0, naturalCompletions = 0, cancelledAt;
  const { host, sessions } = capturingHost({
    onDiagnostic: event => {
      diagnostics.push(event);
      if (event.installationId === 'a' && event.kind === 'ScriptCallbackError') callbackDiagnostic.resolve(event);
    },
    network: input => {
      if (input.url === naturalURL) {
        naturalCompletions++;
        natural.resolve();
        return { statusCode: 200, body: Buffer.alloc(0) };
      }
      assert.equal(input.url, responseURL);
      assert.equal(input.signal.aborted, false);
      requests++;
      networkEntered.resolve(input);
      return releaseNetwork.promise.then(() => {
        if (stack) throw fault('NETWORK_ERROR');
        return { statusCode: 200, body: responseBytes };
      });
    },
  });
  t.after(async () => {
    releaseNetwork.resolve();
    await host.close();
  });
  const a = await host.install({ installationId: 'a', enabled: true, source: lxScript({
    setup: `const boundary = ${stack
      ? `(${stackFailureFixture})()`
      : `(${callbackFixture})(${JSON.stringify(kind)},${JSON.stringify(stage)})`};`,
    handler: 'boundary',
  }) });
  const b = await host.install({ installationId: 'b', enabled: true,
    source: lxScript({ setup: 'let count=0;', handler: 'async()=>++count' }) });
  const session = sessions[0], engine = session.engine;
  assert.equal(session.limits.cleanupTimeoutMs, 100);
  const exited = engine.exited.then(value => ({ value, at: Date.now() }));
  const onEvent = engine.onEvent;
  engine.onEvent = (name, value, scope) => {
    onEvent(name, value, scope);
    if (name === 'send' && value.event === 'updateAlert') {
      const marker = { ...JSON.parse(value.data.log), scope };
      markers.push(marker);
      entered.resolve(marker);
      if (ending === 'abort') { cancelledAt = Date.now(); controller.abort(); }
    }
    if (name === 'failure') failures.push({ code: value.code, scope, at: Date.now() });
  };
  await withoutFallbackStop(t, engine, async () => {
    assert.equal(await host.invoke(target('b')), 1);
    const admittedAt = Date.now();
    const outcome = host.invoke(target('a'), {
      signal: controller.signal, timeoutMs: ending === 'timeout' ? 150 : ending === 'normal' ? 2000 : undefined,
    }).then(value => ({ value }), error => ({ code: error.code }));
    if (kind === 'network') {
      const outbound = await networkEntered.promise;
      assert.equal(outbound.signal.aborted, false);
      releaseNetwork.resolve();
    }
    const marker = await entered.promise;
    assert.equal(marker.kind, kind);
    assert.equal(marker.stage, stage);
    assert.equal(marker.callbacks, stack ? 0 : 1);
    assert.equal(marker.entries, 1);
    assert.ok(Number.isSafeInteger(marker.started));
    assert.ok(Number.isSafeInteger(marker.scope) && marker.scope > 0, 'the guest entry retains A identity');
    if (!stack) {
      assert.equal(marker.ownHasCalls, 2);
      assert.equal(marker.foreignHasCalls, 0, 'internal owner selection must not call guest Set.has');
      assert.deepEqual(marker.hasResults, [true, false]);
      assert.equal(marker.mutableHas, true, 'the guest must remain free to replace and use Set.has');
    }
    assert.equal(await host.invoke(target('b')), 2);
    assert.equal(naturalCompletions, 0, 'B progresses before A finishes its finite work');
    if (ending === 'normal') {
      const result = await outcome;
      assert.ok(result.value?.elapsed >= 600, 'normal callback processing may exceed the cleanup window');
      const expected = {
        callbacks: 1, entries: 1, finished: 1, elapsed: result.value.elapsed,
        ...(stack ? {
          failure: { code: 'NETWORK_ERROR', name: 'Error', message: 'NETWORK_ERROR' }, response: null, body: null,
        } : {
          delivery: kind === 'network' ? { statusCode: 200, bytes: [...responseBytes], alias: true } : null,
          ownHasCalls: 2, foreignHasCalls: 0, hasResults: [true, false], mutableHas: true,
        }),
      };
      assert.deepEqual(result.value, expected);
      await natural.promise;
      if (expectsDiagnostic) await callbackDiagnostic.promise;
      await session.whenAdaptersIdle();
      assert.equal(naturalCompletions, 1);
      assert.deepEqual(failures, []);
      const callbackErrors = diagnostics.filter(event =>
        event.installationId === 'a' && event.kind === 'ScriptCallbackError');
      assert.equal(callbackErrors.length, expectsDiagnostic ? 1 : 0);
      if (expectsDiagnostic) assert.equal(callbackErrors[0].code, 'SCRIPT_EXCEPTION');
      assert.deepEqual(await host.invoke(target('a', 'kw', { probe: true })), expected);
      assert.equal(host.get('a').state, 'ready', 'ordinary callback errors are diagnostic, not session failure');
      assert.equal(host.get('a').generation, a.generation);
      assert.equal(engine.stats().threadCount, 1);
    } else {
      const code = ending === 'timeout' ? 'TIMEOUT' : 'CANCELLED';
      assert.deepEqual(await outcome, { code });
      const retirement = await Promise.race([
        exited,
        natural.promise.then(() => { throw new Error('finite work completed before request-controlled exit'); }),
      ]);
      t.diagnostic(JSON.stringify({ kind, stage, ending, admittedAt, enteredAt: marker.started,
        cancelledAt, failures, exitedAt: retirement.at, naturalCompletions }));
      assertClosed(retirement.value);
      assert.equal(retirement.value.code, code);
      assert.equal(failures.length, 1);
      assert.equal(failures[0].code, code);
      assert.equal(failures[0].scope, marker.scope);
      assert.ok(failures[0].at - marker.started < 600, 'failure precedes finite natural completion');
      assert.ok(retirement.at - marker.started < 600, 'logical cancellation is not real owner exit');
      if (ending === 'abort') assert.ok(failures[0].at - cancelledAt >= 95,
        'the existing 100ms cleanup window must remain available');
      assert.equal(naturalCompletions, 0);
      assert.equal(session.live, false);
      assert.equal(host.get('a').state, 'failed');
      assert.equal(host.get('a').generation, a.generation);
      assert.equal(diagnostics.filter(event => event.installationId === 'a' &&
        event.operation === 'native.unresponsive' && event.code === code).length, 1);
    }
    assert.equal(markers.length, 1);
    assert.equal(requests, kind === 'network' ? 1 : 0);
    assert.equal(await host.invoke(target('b')), 3);
    assert.equal(host.get('b').state, 'ready');
    assert.equal(host.get('b').generation, b.generation);
    assert.equal(sessions.length, 2, 'neither owner is silently replaced');
    assert.equal(Set.prototype.has, hostHas, 'the fixture only changes its QuickJS Context');
    await session.whenAdaptersIdle();
    assert.equal(host.stats().pendingAdapterTasks, 0);
    await host.close();
    for (const owner of sessions) {
      assertClosed(await owner.engine.exited);
      assert.equal(owner.engine.hostTasks.size, 0);
      assert.equal(owner.engine.hostControllers.size, 0);
      assert.equal(owner.stats().pendingAdapterTasks, 0);
    }
    assert.equal(host.cleanupPending('a'), 0);
    assert.equal(host.cleanupPending('b'), 0);
  });
}

// L17: distribute observations across all public schedulers instead of repeating
// a full scheduler x getter matrix. Every case also exercises writable Set.has.
for (const [kind, stage] of [
  ['timer', 'then'],
  ['network', 'promise-constructor'],
  ['microtask', 'throw-name'],
  ['timer', 'throw-message'],
  ['network', 'reject-name'],
  ['microtask', 'reject-message'],
]) for (const ending of ['timeout', 'abort', 'normal']) {
  test(`LX ${kind} callback ${stage} observation retains its owner on ${ending}`,
    { timeout }, t => controlledBoundary(t, kind, stage, ending));
}

for (const ending of ['timeout', 'abort', 'normal']) {
  test(`LX session network failure controls prepareStackTrace on ${ending}`,
    { timeout }, t => controlledBoundary(t, 'network', 'prepare-stack', ending));
}

test('LX successful session network completion does not construct a guest error',
  { timeout }, async t => {
    const { host, sessions } = capturingHost({
      network: async input => {
        assert.equal(input.url, responseURL);
        assert.equal(input.options.encoding, null);
        return { statusCode: 200, headers: { 'x-boundary': 'unchanged' }, body: responseBytes };
      },
    });
    t.after(() => host.close());
    const a = await host.install({ installationId: 'a', enabled: true, source: lxScript({
      setup: 'let hooks=0, callbacks=0;',
      handler: `async ({info}) => {
        if (info.musicInfo.probe) return {hooks,callbacks};
        Error.prepareStackTrace=()=>{hooks++; return 'unexpected-error-construction';};
        return new Promise(resolve=>lx.request(${JSON.stringify(responseURL)},{encoding:null},
          (failure,response,body)=>{
            callbacks++;
            resolve({hooks,callbacks,failure:failure?failure.code:null,statusCode:response.statusCode,
              header:response.headers['x-boundary'],bytes:Array.from(body),
              buffer:Buffer.isBuffer(body),alias:body===response.raw&&body===response.body});
          }));
      }`,
    }) });
    await withoutFallbackStop(t, sessions[0].engine, async () => {
      assert.deepEqual(await host.invoke(target('a'), { timeoutMs: 2000 }), {
        hooks: 0, callbacks: 1, failure: null, statusCode: 200, header: 'unchanged',
        bytes: [...responseBytes], buffer: true, alias: true,
      });
      assert.deepEqual(await host.invoke(target('a', 'kw', { probe: true })), { hooks: 0, callbacks: 1 });
      assert.equal(host.get('a').generation, a.generation);
      assert.equal(host.get('a').state, 'ready');
      await sessions[0].whenAdaptersIdle();
      await host.close();
      assertClosed(await sessions[0].engine.exited);
      assert.equal(sessions[0].engine.hostTasks.size, 0);
      assert.equal(sessions[0].engine.hostControllers.size, 0);
      assert.equal(sessions[0].stats().pendingAdapterTasks, 0);
      assert.equal(host.cleanupPending('a'), 0);
    });
  });

test('LX retired network failure retains session ownership without borrowing current storage',
  { timeout }, async t => {
    const networkEntered = deferred(), releaseNetwork = deferred(), currentWritten = deferred();
    const controller = new AbortController(), storageCalls = [], failures = [];
    let stored = null;
    const { host, sessions } = capturingHost({
      network: input => {
        assert.equal(input.url, responseURL);
        networkEntered.resolve(input);
        return releaseNetwork.promise.then(() => { throw fault('NETWORK_ERROR'); });
      },
      storage: {
        set(input) {
          assert.equal(input.signal.aborted, false);
          assert.equal(input.isCurrent(), true);
          storageCalls.push(['set', input.siteId, input.value]);
          stored = input.value;
          currentWritten.resolve(input);
        },
        get(input) {
          assert.equal(input.signal.aborted, false);
          assert.equal(input.isCurrent(), true);
          storageCalls.push(['get', input.siteId]);
          return stored;
        },
      },
    });
    t.after(async () => {
      releaseNetwork.resolve();
      await host.close();
    });
    const a = await host.install({ installationId: 'a', enabled: true,
      permissions: { storage: ['read', 'write'] }, source: lxScript({
        setup: `let callbacks=0, hooks=0, oldCode, failureFields, finishLate;
          const lateDone=new Promise(resolve=>{finishLate=resolve;});`,
        handler: `async ({info},{storage}) => {
          if (info.musicInfo.arm) {
            Error.prepareStackTrace=()=>{
              Error.prepareStackTrace=undefined;
              hooks++;
              return 'retired-network-stack';
            };
            lx.request(${JSON.stringify(responseURL)},{},async(failure,response,body)=>{
              callbacks++;
              failureFields={code:failure.code,name:failure.name,message:failure.message,response,body};
              try { await storage.set('key','old'); oldCode='WROTE'; }
              catch(error) { oldCode=error.code; }
              finishLate();
            });
            return new Promise(()=>{});
          }
          await storage.set('key','current');
          await lateDone;
          return {callbacks,hooks,oldCode,failure:failureFields,value:await storage.get('key')};
        }`,
      }) });
    const b = await host.install({ installationId: 'b', enabled: true,
      source: lxScript({ setup: 'let count=0;', handler: 'async()=>++count' }) });
    const session = sessions[0], engine = session.engine;
    const onEvent = engine.onEvent;
    engine.onEvent = (name, value, scope) => {
      onEvent(name, value, scope);
      if (name === 'failure') failures.push({ value, scope });
    };
    await withoutFallbackStop(t, engine, async () => {
      assert.equal(session.limits.cleanupTimeoutMs, 100);
      assert.equal(await host.invoke(target('b')), 1);
      const old = host.invoke(target('a', 'kw', { arm: true }), { signal: controller.signal })
        .then(value => ({ value }), error => ({ code: error.code }));
      const outbound = await networkEntered.promise;
      controller.abort();
      assert.deepEqual(await old, { code: 'CANCELLED' });
      assert.equal(outbound.signal.aborted, false, 'retiring A must not abort session-owned network work');
      assert.equal(session.stats().networkRequests, 1);
      assert.equal(session.stats().pendingAdapterTasks, 1);
      const current = host.invoke(target('a', 'tx'), { timeoutMs: 2000 })
        .then(value => ({ value }), error => ({ code: error.code }));
      const context = await currentWritten.promise;
      assert.equal(context.isCurrent(), true);
      assert.equal(outbound.signal.aborted, false);
      assert.equal(await host.invoke(target('b')), 2);
      releaseNetwork.resolve();
      assert.deepEqual(await current, {
        value: {
          callbacks: 1, hooks: 1, oldCode: 'PERMISSION_DENIED',
          failure: { code: 'NETWORK_ERROR', name: 'Error', message: 'NETWORK_ERROR', response: null, body: null },
          value: 'current',
        },
      });
      await session.whenAdaptersIdle();
      assert.equal(context.isCurrent(), false);
      assert.deepEqual(storageCalls, [['set', 'tx', 'current'], ['get', 'tx']]);
      assert.deepEqual(failures, []);
      assert.equal(await host.invoke(target('b')), 3);
      for (const [id, installed] of [['a', a], ['b', b]]) {
        assert.equal(host.get(id).state, 'ready');
        assert.equal(host.get(id).generation, installed.generation);
      }
      assert.equal(sessions.length, 2);
      assert.equal(host.stats().pendingAdapterTasks, 0);
      await host.close();
      for (const owner of sessions) {
        assertClosed(await owner.engine.exited);
        assert.equal(owner.engine.hostTasks.size, 0);
        assert.equal(owner.engine.hostControllers.size, 0);
        assert.equal(owner.stats().pendingAdapterTasks, 0);
      }
      assert.equal(host.cleanupPending('a'), 0);
      assert.equal(host.cleanupPending('b'), 0);
    });
  });
