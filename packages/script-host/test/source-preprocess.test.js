import test from 'node:test';
import assert from 'node:assert/strict';
import { createCapabilityClient } from '@meowfilm/source-runtime';
import { NativeEngine } from '../src/native-engine.js';
import { NativeJsonProgram } from '../src/native-json.js';
import { PortableSourceSession } from '../src/portable-source-session.js';
import { SCRIPT_LIMITS } from '../src/host.js';

const timeout = 15_000;
const measure = `
  function measure() {
    let depth = 0;
    function visit() { depth++; return 1 + visit(); }
    try { visit(); }
    catch (error) {
      if (error.name !== 'RangeError' || error.message !== 'Maximum call stack size exceeded') throw error;
    }
    return depth;
  }
`;
const source = `(() => {
  const preprocess = __meowPreprocess, guest = __meowGuest;
  delete globalThis.__meowPreprocess;
  ${measure}
  return {
    depth() { return measure(); },
    preparedDepth() { return Number(preprocess(() => String(measure()))); },
    fail(code) { return preprocess(() => { throw {code}; }); },
    overflow() {
      return preprocess(() => {
        function visit() { return 1 + visit(); }
        return visit();
      });
    },
    forbidden(kind) {
      return preprocess(() => {
        if (kind === 'guest') return guest('hit', []);
        if (kind === 'host') return __meowHost('unexpected', {});
        if (kind === 'timer') return setTimeout(() => {}, 1);
        return preprocess(() => 'nested');
      });
    },
    invalidResult() {
      let reads = 0;
      try { preprocess(() => ({get then() { reads++; throw Error('must not run'); }})); }
      catch (error) { return {code:error.code, reads}; }
      throw Error('thenable accepted');
    },
    asyncCallback() {
      let entered = false;
      try { preprocess(async () => { entered = true; return 'invalid'; }); }
      catch (error) { return {code:error.code, entered}; }
      throw Error('async callback accepted');
    },
    later(prepared) {
      let done;
      const result = new Promise(resolve => { done = resolve; });
      const schedule = () => {
        Promise.resolve().then(() => done(measure()));
        return 'queued';
      };
      if (prepared) preprocess(schedule); else schedule();
      return result;
    },
    spin() {
      __meowEmit('entered', null);
      return preprocess(() => { for (;;) {} });
    },
    borrow() { return preprocess(() => 'borrowed'); },
    guest(method) { return guest(method, []); },
  };
})()`;
const guestSource = `(() => {
  ${measure}
  let hits = 0;
  return {
    depth() { return measure(); },
    hidden() { return typeof __meowPreprocess; },
    hit() { return ++hits; },
    hits() { return hits; },
    borrow() { return __meowCore('borrow', []); },
    async laterBorrow() { await Promise.resolve(); return __meowCore('borrow', []); },
  };
})()`;

function released(exit) {
  assert.equal(exit.stopped, true);
  for (const key of ['runtimeCount', 'contextCount', 'threadCount', 'activeCalls',
    'hostCalls', 'timers', 'heapBytes', 'heapObjects', 'byteBuffers', 'byteBufferBytes',
    'jobsPending']) assert.equal(exit[key], 0, key);
}

async function engineFor(t, options = {}) {
  const engine = NativeEngine.create({
    source, guestSource, guestHelpers: ['borrow'], maxStackBytes: 256 * 1024, ...options,
  });
  t.after(async () => released(await engine.stop()));
  await engine.ready;
  return engine;
}

test('trusted preprocessing has a finite larger stack and restores core and guest on success and failure',
  { timeout }, async t => {
    const engine = await engineFor(t);
    const coreDepth = await engine.call('depth');
    const guestDepth = await engine.call('guest', ['depth']);
    assert.ok(await engine.call('preparedDepth') > coreDepth);
    const restored = async () => {
      assert.equal(await engine.call('depth'), coreDepth);
      assert.equal(await engine.call('guest', ['depth']), guestDepth);
    };
    await restored();
    await assert.rejects(engine.call('overflow'), { code: 'RESOURCE_LIMIT' });
    await restored();
    for (const code of ['SCRIPT_PROTOCOL_ERROR', 'CANCELLED', 'TIMEOUT']) {
      await assert.rejects(engine.call('fail', [code]), { code });
      await restored();
    }
    assert.deepEqual(await engine.call('invalidResult'), { code: 'INVALID_ARGUMENT', reads: 0 });
    await restored();
    assert.deepEqual(await engine.call('asyncCallback'), { code: 'INVALID_ARGUMENT', entered: false });
    await restored();
    // The identical Promise callback must see the ordinary budget even if it
    // was registered inside the trusted scope; no pending job runs there.
    assert.equal(await engine.call('later', [true]), await engine.call('later', [false]));
    await restored();
  });

test('preprocessing is private to core and cannot execute or lend its budget to guest or host calls',
  { timeout }, async t => {
    let hostCalls = 0;
    const engine = await engineFor(t, { onHostCall() { hostCalls++; } });
    assert.equal(await engine.call('guest', ['hidden']), 'undefined');
    for (const method of ['borrow', 'laterBorrow']) {
      await assert.rejects(engine.call('guest', [method]), { code: 'UNSUPPORTED_OPTION' });
    }
    for (const kind of ['guest', 'host', 'timer', 'nested']) {
      await assert.rejects(engine.call('forbidden', [kind]), { code: 'UNSUPPORTED_OPTION' });
    }
    assert.equal(await engine.call('guest', ['hits']), 0);
    assert.equal(hostCalls, 0);
    assert.equal((await engine.stats({ refresh: true })).timers, 0);
    assert.ok(await engine.call('preparedDepth') > await engine.call('depth'));
    const json = new NativeJsonProgram(
      'globalThis.meowfilmProcess = () => typeof globalThis.__meowPreprocess;',
    );
    t.after(() => json.close());
    assert.equal(await json.invoke(null), 'undefined');
  });

for (const code of ['CANCELLED', 'TIMEOUT']) {
  test(`real preprocessing interruption retains ${code} and joins the native owner`,
    { timeout }, async t => {
      let entered;
      const started = new Promise(resolve => { entered = resolve; });
      const controller = new AbortController();
      const failures = [];
      const engine = await engineFor(t, {
        onEvent(name, value) {
          if (name === 'entered') entered();
          if (name === 'failure') failures.push(value.code);
        },
      });
      const running = engine.call('spin', [], {
        scope: 1, signal: controller.signal, timeoutMs: code === 'TIMEOUT' ? 100 : 5000,
      });
      const rejected = assert.rejects(running, { code });
      await started;
      if (code === 'CANCELLED') {
        const timer = setTimeout(() => controller.abort(), 10);
        t.after(() => clearTimeout(timer));
      }
      await rejected;
      released(await engine.exited);
      assert.deepEqual(failures, [code]);
    });
}

async function portable(t, source, protocol = 'cat') {
  const session = new PortableSourceSession({
    installationId: 'preprocess', generation: 1,
    revision: { source, protocol, version: '1.0.0' },
    capabilities: createCapabilityClient({
      invoke() { assert.fail('pure preprocessing must not request a host capability'); },
    }),
    limits: { ...SCRIPT_LIMITS, initTimeoutMs: 5000, requestTimeoutMs: 5000, cleanupTimeoutMs: 1000 },
  });
  t.after(async () => {
    await session.stop();
    if (session.engine) released(await session.engine.exited);
  });
  assert.equal(session.limits.maxStackBytes, 256 * 1024);
  return session;
}

test('real Cat preprocessing distinguishes ordinary syntax errors from Acorn stack exhaustion',
  { timeout }, async t => {
    for (const [script, code] of [
      ['export default {', 'SCRIPT_PROTOCOL_ERROR'],
      [`export default ${'('.repeat(4096)}{}${')'.repeat(4096)};`, 'RESOURCE_LIMIT'],
    ]) {
      const session = await portable(t, script);
      await assert.rejects(session.start(), { code });
      released(await session.engine.exited);
    }
    const session = await portable(t, 'export default { searchContent() { return "ready"; } };');
    await session.start();
    await session.activate();
    assert.equal(await session.invoke({ siteId: 'source', operation: 'search' }), 'ready');
  });

test('real Legado preprocessing classifies exhaustion and syntax errors without poisoning its guest',
  { timeout }, async t => {
    for (const [script, code] of [
      ['function (', 'SCRIPT_PROTOCOL_ERROR'],
      [`${'('.repeat(4096)}1${')'.repeat(4096)}`, 'RESOURCE_LIMIT'],
    ]) {
      const session = await portable(t, {
        bookSourceName: 'Preprocess', bookSourceUrl: 'https://preprocess.invalid/',
        coverDecodeJs: script,
        exploreUrl: '@js: "Ready::https://preprocess.invalid/ready"',
      }, 'legado');
      await session.start();
      await session.activate();
      const engine = session.engine;
      await assert.rejects(session.invoke({ siteId: 'source', operation: 'image' }), { code });
      assert.equal(session.engine, engine);
      // A valid transform and guest evaluation on the same owner still work.
      assert.deepEqual(await session.invoke({ siteId: 'source', operation: 'exploreKinds' }), {
        kinds: [{ title: 'Ready', url: 'https://preprocess.invalid/ready', style: {} }],
      });
    }
  });
