import test from 'node:test';
import assert from 'node:assert/strict';
import { Duplex } from 'node:stream';
import { attachConnectedStream } from '@meowfilm/runtime-bridge/descriptor';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { createMusicScriptModule } from '../apps/runtime/modules/music-script.mjs';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';

function connect(t, product, modules, options = {}) {
  let left, right;
  const side = other => new Duplex({
    read() {},
    write(bytes, _, done) { if (!other().destroyed) other().push(bytes); done(); },
    destroy(error, done) { if (!other().destroyed) other().push(null); done(error); }
  });
  left = side(() => right); right = side(() => left);
  const client = attachConnectedStream(left, { session: 'assembly', timeout: 2000, methods: options.nativeMethods });
  const runtime = createAppRuntime({
    product, modules, assemble: options.assemble,
    connect: options => attachConnectedStream(right, { ...options, session: 'assembly', timeout: 2000 })
  });
  t.after(async () => { client.close(); await runtime.close(); });
  return { client, runtime };
}

test('transport termination disposes each enabled module exactly once', async t => {
  let closes = 0;
  let resolveClosed;
  const closed = new Promise(resolve => { resolveClosed = resolve; });
  const module = { id: 'fixture', version: '0.1.0', methods: { 'fixture.echo': value => value },
    close() { closes++; resolveClosed(); } };
  const { client, runtime } = connect(t, 'fixture', [module]);
  assert.equal(await client.call('fixture.echo', 2), 2);
  client.close();
  await closed;
  await runtime.close();
  await runtime.close();
  assert.equal(closes, 1);
});

test('failed module cleanup is explicit without leaking provider diagnostics', async () => {
  let onClose;
  const runtime = createAppRuntime({
    product: 'fixture',
    modules: [{ id: 'fixture', version: '1.0.0', methods: {}, close() { throw new Error('private provider failure'); } }],
    connect: options => { onClose = options.onClose; return { close: () => onClose() }; }
  });
  await assert.rejects(runtime.close(), { code: 'INTERNAL', message: 'INTERNAL' });
});

for (const hostFailure of ['throw', 'incomplete']) {
  test(`App disposal reports a real source ${hostFailure} cleanup envelope and waits for every module`, async t => {
    let sourceCloses = 0, beforeCloses = 0, afterCloses = 0;
    let onClose, resolveAfter, afterStarted;
    let afterComplete = false, closeComplete = false;
    const afterGate = new Promise(resolve => { resolveAfter = resolve; });
    const started = new Promise(resolve => { afterStarted = resolve; });
    const source = createSourceRuntimeModule({
      call: async () => assert.fail('disposal must not make outbound RPC calls'),
      network: async () => assert.fail('disposal must not use the network'),
      hostFactory: () => ({
        list: () => [],
        async close() {
          sourceCloses++;
          if (hostFailure === 'throw') throw new Error('private host cleanup diagnostics');
          return { cleanupComplete: false };
        },
      }),
    });
    const runtime = createAppRuntime({
      product: 'aggregate',
      modules: [
        { id: 'before', version: '1.0.0', methods: {}, close() { beforeCloses++; } },
        source,
        {
          id: 'after', version: '1.0.0', methods: {},
          async close() {
            afterCloses++;
            afterStarted();
            await afterGate;
            afterComplete = true;
          },
        },
      ],
      connect: options => {
        onClose = options.onClose;
        return { close: () => onClose() };
      },
    });
    t.after(async () => {
      resolveAfter();
      await runtime.close().catch(() => {});
    });
    if (hostFailure === 'incomplete') onClose();
    const closing = runtime.close();
    void closing.then(() => { closeComplete = true; }, () => { closeComplete = true; });
    const failed = assert.rejects(closing, { code: 'INTERNAL', message: 'INTERNAL' });
    await started;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(sourceCloses, 1);
    assert.equal(beforeCloses, 1);
    assert.equal(afterCloses, 1);
    assert.equal(afterComplete, false);
    assert.equal(closeComplete, false);
    resolveAfter();
    await failed;
    assert.equal(afterComplete, true);
    await assert.rejects(runtime.close(), { code: 'INTERNAL', message: 'INTERNAL' });
    assert.deepEqual([sourceCloses, beforeCloses, afterCloses], [1, 1, 1]);
    assert.deepEqual(await source.close(), {
      ok: false, contract: 'source.runtime.v1', code: 'SOURCE_CLEANUP',
    });
    assert.equal(sourceCloses, 1);
  });
}

for (const failedModule of [undefined, 'music', 'cat']) {
  test(`App disposal consumes the existing music, Cat and source close contracts (${failedModule ?? 'success'})`, async t => {
    const closes = { music: 0, cat: 0, source: 0 };
    const noNetwork = async () => assert.fail('passive modules must not use the network');
    const noCall = async () => assert.fail('disposal must not make outbound RPC calls');
    const music = createMusicScriptModule({
      call: noCall,
      network: noNetwork,
      hostFactory: () => ({
        async close() {
          closes.music++;
          return { cleanupComplete: failedModule !== 'music' };
        },
      }),
    });
    const cat = createCatScriptModule({
      dataRoot: 'unused-assembly-fixture',
      runtimeFactory: () => ({
        async status() { return { state: 'ready' }; },
        async close() {
          closes.cat++;
          if (failedModule === 'cat') throw new Error('private Cat cleanup diagnostics');
        },
      }),
    });
    const source = createSourceRuntimeModule({
      call: noCall,
      network: noNetwork,
      hostFactory: () => ({
        list: () => [],
        async close() { closes.source++; return { cleanupComplete: true }; },
      }),
    });
    let onClose;
    const runtime = createAppRuntime({
      product: 'aggregate',
      modules: [music, cat, source],
      connect: options => {
        onClose = options.onClose;
        return { close: () => onClose() };
      },
    });
    t.after(() => runtime.close().catch(() => {}));
    assert.equal((await cat.methods['cat.script.status']({})).ok, true);
    for (let attempt = 0; attempt < 2; attempt++) {
      if (failedModule) {
        await assert.rejects(runtime.close(), { code: 'INTERNAL', message: 'INTERNAL' });
      } else {
        assert.equal(await runtime.close(), undefined);
      }
    }
    assert.deepEqual(closes, { music: 1, cat: 1, source: 1 });
    assert.equal((await music.close()).ok, failedModule !== 'music');
    if (failedModule === 'cat') await assert.rejects(cat.close(), /private Cat cleanup diagnostics/);
    else assert.equal(await cat.close(), undefined);
    assert.deepEqual(await source.close(), {
      ok: true, contract: 'source.runtime.v1', closed: true, cleanupComplete: true,
    });
    assert.deepEqual(closes, { music: 1, cat: 1, source: 1 });
  });
}

test('invalid assembly cannot connect or silently override another module', () => {
  const good = { id: 'fixture', version: '0.1.0', methods: {}, close() {} };
  const bad = [[], [good, good], [{ ...good, id: undefined }], [{ ...good, version: '1.0.0\n' }],
    [{ ...good, methods: { 'other.method': () => null } }]];
  for (const modules of bad) {
    assert.throws(() => createAppRuntime({ product: 'fixture', modules, connect: () => assert.fail('connected') }),
      { code: 'INVALID_MESSAGE' });
  }
  assert.throws(() => createAppRuntime({ modules: [good], connect() {} }), { code: 'INVALID_MESSAGE' });
});

test('App factories inject the same framed native caller without starting disabled modules', async t => {
  let calls = 0, outbound, premature;
  const { client, runtime } = connect(t, 'fixture', undefined, {
    nativeMethods: { 'platform.fixture.echo': value => { calls++; return value; } },
    assemble({ call }) {
      outbound = call;
      premature = call('platform.fixture.echo', 'not connected');
      // Observe the immediate rejection before this synchronous factory returns.
      void premature.catch(() => {});
      return [{
        id: 'fixture', version: '1.0.0', close() {},
        methods: { 'fixture.roundtrip': (value, context) => call('platform.fixture.echo', value, { signal: context.signal }) }
      }];
    }
  });
  await assert.rejects(premature, { code: 'CLOSED' });
  assert.deepEqual(await client.call('fixture.roundtrip', { value: '保留原始身份' }), { value: '保留原始身份' });
  assert.equal(calls, 1);
  await runtime.close();
  await assert.rejects(outbound('platform.fixture.echo', 'closed'), { code: 'CLOSED' });
  assert.equal(calls, 1);
});

test('assembly factory cannot be mixed with prebuilt modules or an async result', () => {
  const module = { id: 'fixture', version: '1.0.0', methods: {}, close() {} };
  for (const input of [
    { modules: [module], assemble: () => [module] },
    { assemble: 'invalid' },
    { assemble: () => Promise.resolve([module]) }
  ]) {
    assert.throws(() => createAppRuntime({
      product: 'fixture', ...input, connect: () => assert.fail('connected')
    }), { code: 'INVALID_MESSAGE' });
  }
});

test('invalid wire identity never allocates module resources and invalid factories dispose once', async () => {
  assert.throws(() => createAppRuntime({
    product: 'bad\n', assemble: () => assert.fail('assembled'), connect() {}
  }), { code: 'INVALID_MESSAGE' });
  let closed = 0, call;
  const module = { id: 'fixture', version: '1.0.0', methods: {}, close() { closed++; } };
  assert.throws(() => createAppRuntime({
    product: 'fixture',
    assemble(dependencies) { call = dependencies.call; return [module, module]; },
    connect: () => assert.fail('connected')
  }), { code: 'INVALID_MESSAGE' });
  await Promise.resolve();
  assert.equal(closed, 1);
  await assert.rejects(call('platform.fixture.echo', null), { code: 'CLOSED' });
});
