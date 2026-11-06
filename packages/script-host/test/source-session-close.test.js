import test from 'node:test';
import assert from 'node:assert/strict';
import { PortableSourceSession } from '../src/portable-source-session.js';
import { NativeEngine } from '../src/native-engine.js';
import { deferred, fault } from '../src/errors.js';
import { SCRIPT_LIMITS } from '../src/host.js';

// Deterministic message ordering at the native boundary. Real owner exit and
// virtual.unregister behavior remain covered by source-worker/source-cleanup.
async function setup(t, { capability = async () => null } = {}) {
  const invocation = deferred(), close = deferred(), exit = deferred();
  const entered = deferred(), closing = deferred(), stopping = deferred();
  let callbacks, exited = false;
  const running = { threadCount: 1, runtimeCount: 1, contextCount: 2,
    timers: 0, activeCalls: 0, hostCalls: 0, jobsPending: 0, stopped: false };
  const stopped = { threadCount: 0, runtimeCount: 0, contextCount: 0,
    timers: 0, activeCalls: 0, hostCalls: 0, jobsPending: 0, stopped: true };
  const engine = {
    ready: Promise.resolve(), exited: exit.promise, closing: false,
    async call(method, _args, options = {}) {
      if (method === 'start') return { protocol: 'cat', importedProtocol: 'cat', actions: ['search'] };
      if (method === 'activate' || method === 'cancel') return null;
      if (method === 'invoke') {
        entered.resolve(options);
        return await invocation.promise;
      }
      if (method === 'close') {
        closing.resolve(options);
        return await close.promise;
      }
      assert.fail(`unexpected native method: ${method}`);
    },
    stop(reason) {
      this.closing = true;
      stopping.resolve(reason);
      return exit.promise;
    },
    stats: () => ({ ...(exited ? stopped : running) }),
  };
  t.mock.method(NativeEngine, 'create', options => { callbacks = options; return engine; });
  const session = new PortableSourceSession({
    installationId: 'close-order', generation: 1,
    revision: { protocol: 'cat', version: '1.0.0', source: 'export default {};' },
    capabilities: { call: capability },
    limits: { ...SCRIPT_LIMITS, initTimeoutMs: 60_000, requestTimeoutMs: 60_000, cleanupTimeoutMs: 20 },
  });
  const fail = (code, scope) => callbacks.onEvent('failure', { code }, scope);
  const finish = code => {
    exited = true;
    engine.closing = true;
    invocation.reject(fault(code));
    close.reject(fault(code));
    exit.resolve({ ...stopped, code });
  };
  t.after(async () => {
    finish('STALE_SESSION');
    await session.stop().catch(() => {});
  });
  await session.start();
  await session.activate();
  const invoke = () => session.invoke({
    siteId: 'source', operation: 'search', payload: {},
  });
  return { session, engine, invoke, entered: entered.promise, closing: closing.promise,
    stopping: stopping.promise, rejectClose: code => close.reject(fault(code)), fail, finish };
}

function observe(promise) {
  let settled = false;
  promise.then(() => { settled = true; }, () => { settled = true; });
  return () => settled;
}

for (const ordering of ['failure-first', 'close-rejection-first']) {
  test(`source stop waits for actual exit when ${ordering} interrupts an earlier action`,
    { timeout: 5000 }, async t => {
      const fixture = await setup(t);
      const rejected = assert.rejects(fixture.invoke(), { code: 'STALE_SESSION' });
      const action = await fixture.entered;
      const closed = fixture.session.stop();
      const settled = observe(closed);
      const cleanup = await fixture.closing;
      assert.notEqual(action.scope, cleanup.scope);
      assert.equal(fixture.session.stats().live, false);
      if (ordering === 'failure-first') fixture.fail('CANCELLED', action.scope);
      fixture.rejectClose('STALE_SESSION');
      await fixture.stopping;
      if (ordering === 'close-rejection-first') fixture.fail('CANCELLED', action.scope);
      assert.equal(settled(), false, 'neither rejection nor failure is a native exit');
      assert.equal(fixture.session.stats().native.threadCount, 1);
      fixture.finish('CANCELLED');
      await closed;
      await rejected;
      assert.equal(fixture.session.stats().cleanupError, null);
      for (const key of ['threadCount', 'runtimeCount', 'contextCount']) {
        assert.equal(fixture.session.stats().native[key], 0);
      }
    });
}

test('source owner failure before stop does not become a second close admission failure',
  { timeout: 5000 }, async t => {
    const fixture = await setup(t);
    const rejected = assert.rejects(fixture.invoke(), { code: 'TIMEOUT' });
    const action = await fixture.entered;
    fixture.fail('TIMEOUT', action.scope);
    const closed = fixture.session.stop();
    const settled = observe(closed);
    await fixture.closing;
    fixture.rejectClose('STALE_SESSION');
    await fixture.stopping;
    assert.equal(settled(), false);
    fixture.finish('TIMEOUT');
    await closed;
    await rejected;
    assert.equal(fixture.session.stats().cleanupError, null);
    assert.equal(fixture.session.stats().native.stopped, true);
  });

for (const code of ['CANCELLED', 'TIMEOUT']) {
  test(`queued source close rejected by actual ${code} exit is not its own cleanup failure`,
    { timeout: 5000 }, async t => {
      const fixture = await setup(t);
      const rejected = assert.rejects(fixture.invoke(), { code: 'STALE_SESSION' });
      const action = await fixture.entered;
      const closed = fixture.session.stop();
      const settled = observe(closed);
      await fixture.closing;
      fixture.fail(code, action.scope);
      assert.equal(settled(), false);
      fixture.finish(code);
      await closed;
      await rejected;
      assert.equal(fixture.session.stats().cleanupError, null);
      assert.equal(fixture.session.stats().native.threadCount, 0);
    });
}

for (const scenario of [
  { name: 'STALE without a native failure', error: 'STALE_SESSION', exit: 'STALE_SESSION' },
  { name: 'CANCELLED without a native failure', error: 'CANCELLED', exit: 'CANCELLED' },
  { name: 'close scope timeout', error: 'TIMEOUT', exit: 'TIMEOUT', failureScope: 'close' },
  { name: 'close scope cancellation', error: 'CANCELLED', exit: 'CANCELLED', failureScope: 'close' },
  { name: 'close timeout before an older action exits', error: 'TIMEOUT', exit: 'TIMEOUT',
    failureScope: 'action' },
  { name: 'virtual cleanup error despite an older action failure',
    error: 'CAPABILITY_UNAVAILABLE', exit: 'CANCELLED', failureScope: 'action' },
  { name: 'exit that does not match the action interruption',
    error: 'STALE_SESSION', exit: 'SCRIPT_EXCEPTION', failureScope: 'action', failureCode: 'CANCELLED' },
]) {
  test(`source stop retains ${scenario.name}`, { timeout: 5000 }, async t => {
    const fixture = await setup(t);
    const rejected = assert.rejects(fixture.invoke(), { code: 'STALE_SESSION' });
    const action = await fixture.entered;
    const expected = scenario.error === 'TIMEOUT' ? 'SCRIPT_CLEANUP' : scenario.error;
    const closed = fixture.session.stop();
    const failed = assert.rejects(closed, { code: expected });
    const settled = observe(closed);
    const cleanup = await fixture.closing;
    if (scenario.failureScope) {
      fixture.fail(scenario.failureCode ?? scenario.exit,
        scenario.failureScope === 'close' ? cleanup.scope : action.scope);
    }
    // wrapper.closing alone must never turn a business failure into success.
    fixture.engine.closing = true;
    fixture.rejectClose(scenario.error);
    await fixture.stopping;
    assert.equal(settled(), false, 'cleanup errors must still await actual exit');
    fixture.finish(scenario.exit);
    await failed;
    await rejected;
    assert.equal(fixture.session.stats().cleanupError, expected);
    assert.equal(fixture.session.stats().native.threadCount, 0);
  });
}

test('interrupted source close retains physical adapter debt until the adapter settles',
  { timeout: 5000 }, async t => {
    const physical = deferred(), entered = deferred();
    const fixture = await setup(t, {
      capability(_name, _input, { signal }) {
        entered.resolve(signal);
        return physical.promise;
      },
    });
    t.after(() => physical.resolve(null));
    const controller = new AbortController();
    const adapter = fixture.session.hostCall('source.capability', [
      'http.request',
      { installationId: 'close-order', generation: 1, siteId: 'source', requestId: 1 }, [],
    ], 1, { signal: controller.signal });
    const adapterSignal = await entered.promise;
    const rejected = assert.rejects(fixture.invoke(), { code: 'STALE_SESSION' });
    const action = await fixture.entered;
    const closed = fixture.session.stop();
    const settled = observe(closed);
    await fixture.closing;
    fixture.rejectClose('STALE_SESSION');
    await fixture.stopping;
    fixture.fail('CANCELLED', action.scope);
    assert.equal(adapterSignal.aborted, true);
    assert.equal(settled(), false);
    fixture.finish('CANCELLED');
    await closed;
    await rejected;
    assert.equal(fixture.session.stats().cleanupError, null);
    assert.equal(fixture.session.stats().native.threadCount, 0);
    assert.equal(fixture.session.stats().pendingAdapterTasks, 1);
    let idle = false;
    const retirement = fixture.session.adapterRetirement();
    retirement.idle.then(() => { idle = true; });
    await Promise.resolve();
    assert.equal(idle, false, 'logical cancellation cannot settle physical I/O');
    assert.equal(retirement.pendingAdapterTasks(), 1);
    physical.resolve(null);
    await adapter;
    await retirement.idle;
    assert.equal(fixture.session.stats().pendingAdapterTasks, 0);
  });
