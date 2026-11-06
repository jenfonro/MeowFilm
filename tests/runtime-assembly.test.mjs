import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createRuntime, startRuntime } from '../apps/runtime/entry.mjs';
import { createRuntimeClient, createCallbackServer } from '../apps/runtime/client.mjs';
import { contracts } from '../apps/runtime/contracts.mjs';
import { createMusicScriptModule } from '../apps/runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { deferred, turn, until, rawRequest } from '../packages/runtime-bridge/test/http-fixture.js';

const config = () => ({ sessionId: randomUUID(), requestKey: randomBytes(32).toString('hex'),
  callbackKey: randomBytes(32).toString('hex'), callbackUrl: 'http://127.0.0.1:1' });
function fixture(id = 'music', close = () => {}) {
  const spec = contracts[id];
  return { id, version: spec.version, contract: spec.contract, methods: {
    [spec.prefix + 'status']: async input => ({ ok: true, contract: spec.contract, value: input }),
  }, upload() {}, close };
}
async function connect(t, modules) {
  const identity = config();
  const runtime = createAppRuntime({ ...identity, modules });
  await runtime.start();
  const client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
  t.after(async () => { await client.close(); await runtime.close().catch(() => {}); });
  return { runtime, client, identity };
}

test('explicit session termination disposes each module exactly once while keepalive disconnect does not', async t => {
  let closes = 0;
  const { runtime, client } = await connect(t, [fixture('music', () => { closes++; })]);
  assert.equal((await client.call('music.script.status', { number: 2 })).value.number, 2);
  await client.close(); await turn(); assert.equal(closes, 0);
  await runtime.close(); await runtime.close(); assert.equal(closes, 1);
});

test('HTTP close accepts only an empty object and rejects every other shape before side effects', async t => {
  let closes = 0;
  const { runtime, client, identity } = await connect(t, [fixture('music', () => { closes++; })]);
  for (const value of [true, 1, [], null, { x: 1 }]) {
    const reply = await rawRequest(runtime.baseUrl,
      { sessionId: identity.sessionId, key: identity.requestKey },
      { path: '/v1/close', body: JSON.stringify(value) }).response;
    assert.equal(reply.status, 400);
    assert.deepEqual(reply.value, { ok: false, contract: 'runtime.http.v1', code: 'INVALID_REQUEST' });
    assert.equal(closes, 0);
    assert.deepEqual((await client.call('music.script.status', { still: 'open' })).value, { still: 'open' });
  }
  assert.deepEqual(await client.shutdown(), {
    ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true },
  });
  assert.equal(closes, 1);
  await runtime.close();
  assert.equal(closes, 1);
});

test('failed module cleanup is explicit without leaking provider diagnostics', async t => {
  const { runtime, client } = await connect(t, [fixture('music', () => { throw new Error('PRIVATE'); })]);
  assert.deepEqual(await client.shutdown(), { ok: false, contract: 'runtime.http.v1', code: 'INVALID_RESPONSE' });
  await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE', message: 'INVALID_RESPONSE' });
});

test('overlapping HTTP shutdown requests share disposal without waiting on each other', async t => {
  const entered = deferred(), release = deferred(); let closes = 0;
  const { client } = await connect(t, [fixture('music', async () => {
    closes++; entered.resolve(); await release.promise;
  })]);
  const first = client.shutdown(); await entered.promise;
  const second = client.shutdown();
  release.resolve();
  for (const reply of await Promise.all([first, second])) {
    assert.deepEqual(reply, { ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true } });
  }
  assert.equal(closes, 1);
});

for (const hostFailure of ['throw', 'incomplete']) {
  test(`App disposal reports a real source ${hostFailure} cleanup envelope and waits for every module`, async t => {
    const gate = deferred(), entered = deferred(); const counts = { music: 0, source: 0, cat: 0 };
    const source = createSourceRuntimeModule({ call: async () => assert.fail('reverse I/O'),
      hostFactory: () => ({ list: () => [], async close() {
        counts.source++;
        if (hostFailure === 'throw') throw new Error('PRIVATE');
        return { cleanupComplete: false };
      } }) });
    const { runtime } = await connect(t, [fixture('music', () => { counts.music++; }), source,
      fixture('cat', async () => { counts.cat++; entered.resolve(); await gate.promise; })]);
    let complete = false;
    const close = runtime.close();
    close.finally(() => { complete = true; }).catch(() => {});
    const failed = assert.rejects(close, { code: 'INVALID_RESPONSE' });
    await entered.promise; await turn();
    assert.deepEqual(counts, { music: 1, source: 1, cat: 1 }); assert.equal(complete, false);
    gate.resolve(); await failed;
    assert.deepEqual(await source.close(), { ok: false, contract: 'source.runtime.v2', code: 'SOURCE_CLEANUP' });
    await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE' });
    assert.deepEqual(counts, { music: 1, source: 1, cat: 1 });
  });
}

for (const failedModule of [undefined, 'music', 'cat']) {
  test(`App disposal consumes music Cat and source contracts (${failedModule ?? 'success'})`, async t => {
    const closes = { music: 0, cat: 0, source: 0 };
    const music = createMusicScriptModule({ call: async () => assert.fail('reverse I/O'),
      hostFactory: () => ({ async close() { closes.music++; return { cleanupComplete: failedModule !== 'music' }; } }) });
    const cat = createCatScriptModule({ dataRoot: 'unused', runtimeFactory: () => ({
      async status() { return {}; }, async close() { closes.cat++; if (failedModule === 'cat') throw new Error('PRIVATE'); },
    }) });
    const source = createSourceRuntimeModule({ call: async () => assert.fail('reverse I/O'),
      hostFactory: () => ({ list: () => [], async close() { closes.source++; return { cleanupComplete: true }; } }) });
    const { runtime, client } = await connect(t, [music, cat, source]);
    assert.equal((await client.call('cat.script.status')).ok, true);
    for (let i = 0; i < 2; i++) {
      if (failedModule) await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE' });
      else await runtime.close();
    }
    assert.deepEqual(closes, { music: 1, cat: 1, source: 1 });
    assert.equal((await music.close()).ok, failedModule !== 'music');
    assert.deepEqual(await source.close(), { ok: true, contract: 'source.runtime.v2', value: { closed: true, cleanupComplete: true } });
  });
}

test('invalid assembly cannot open a listener or override methods; failed factories dispose once', async () => {
  let closes = 0;
  const good = fixture('music', () => { closes++; });
  for (const modules of [[], [good, good], [{ ...good, id: undefined }], [{ ...good, version: '0.7.0' }],
    [{ ...good, methods: { 'other.method': () => null } }]]) {
    assert.throws(() => createAppRuntime({ ...config(), modules }), { code: 'INVALID_REQUEST' });
  }
  await turn(); assert.equal(closes, 4);
});

test('storage callback assembly rejects unsupported methods and malformed handlers before allocation', () => {
  const identity = config();
  for (const methods of [undefined, null, [], { eval() {} },
    { 'platform.musicScript.storage.clear'() {} }, { 'platform.musicScript.storage.get': null }]) {
    assert.throws(() => createCallbackServer({ ...identity, methods }), { code: 'INVALID_REQUEST' });
  }
});

test('storage callback routes preserve all three operations and reject unknown or unregistered methods', async t => {
  const identity = config(), received = [];
  const callbacks = createCallbackServer({ ...identity,
    methods: Object.fromEntries(['get', 'set', 'delete'].map(operation =>
      [`platform.musicScript.storage.${operation}`, async (input, context) => {
        assert.equal(context.sessionId, identity.sessionId);
        assert.equal(context.isCurrent(), true);
        assert.equal(context.signal.aborted, false);
        received.push({ operation, input, requestId: context.requestId });
        return { ok: true, value: { operation, input } };
      }])) });
  await callbacks.start();
  t.after(() => callbacks.close());
  const auth = { sessionId: identity.sessionId, key: identity.callbackKey };
  for (const operation of ['get', 'set', 'delete']) {
    const input = { grant: randomUUID(), installationId: 'music', siteId: 'kw', generation: 1, key: 'key',
      ...(operation === 'set' ? { value: { text: '原样😀' } } : {}) };
    const request = rawRequest(callbacks.baseUrl, auth, {
      path: `/v1/capabilities/platform.musicScript.storage.${operation}`, body: JSON.stringify(input),
    });
    const reply = await request.response;
    assert.equal(reply.status, 200);
    assert.deepEqual(reply.value, { ok: true, contract: 'runtime.http.v1', value: { operation, input } });
    assert.deepEqual(received.at(-1), { operation, input, requestId: request.requestId });
  }
  for (const [method, path] of [
    ['POST', '/v1/capabilities/eval'],
    ['POST', '/v1/capabilities/platform.musicScript.storage.clear'],
    ['POST', '/v1/capabilities/platform.musicScript.storage.get/extra'],
    ['POST', '/v1/capabilities/platform.musicScript.storage.get?extra=1'],
    ['GET', '/v1/capabilities/platform.musicScript.storage.get'],
  ]) {
    const reply = await rawRequest(callbacks.baseUrl, auth, { method, path, body: '{}' }).response;
    assert.equal(reply.status, 404);
    assert.deepEqual(reply.value, { ok: false, contract: 'runtime.http.v1', code: 'METHOD_NOT_FOUND' });
  }
  assert.equal(received.length, 3);
  const subset = createCallbackServer({ ...identity,
    methods: { 'platform.musicScript.storage.get': async () => assert.fail('unregistered handler invoked') } });
  await subset.start();
  t.after(() => subset.close());
  const missing = await rawRequest(subset.baseUrl, auth, {
    path: '/v1/capabilities/platform.musicScript.storage.delete', body: '{}',
  }).response;
  assert.equal(missing.status, 404);
  assert.deepEqual(missing.value, { ok: false, contract: 'runtime.http.v1', code: 'METHOD_NOT_FOUND' });
});

test('storage callback error projection preserves safe codes and redacts provider diagnostics', async t => {
  const identity = config();
  const cases = [
    ...['PERMISSION_DENIED', 'STALE_SOURCE', 'CANCELLED', 'CLOSED', 'TIMEOUT', 'LIMIT',
      'INVALID_REQUEST', 'STORAGE_ERROR'].map(code => ({ reply: { ok: false, code }, code })),
    { error: Object.assign(new Error('PRIVATE-provider-secret'), { code: 'STALE_SOURCE' }), code: 'STALE_SOURCE' },
    { error: Object.assign(new Error('PRIVATE-provider-secret'), { code: 'PRIVATE-code' }), code: 'STORAGE_ERROR' },
    ...[undefined, null, [], { ok: false, code: 'PRIVATE-code' }, { ok: true },
      { ok: false, value: 'PRIVATE' }, { ok: true, value: null, extra: 'PRIVATE' },
      { ok: false, code: 'TIMEOUT', message: 'PRIVATE' }].map(reply => ({ reply, code: 'STORAGE_ERROR' })),
  ];
  const callbacks = createCallbackServer({ ...identity, methods: {
    'platform.musicScript.storage.get': async ({ index }) => {
      const entry = cases[index];
      if (entry.error) throw entry.error;
      return entry.reply;
    },
  } });
  await callbacks.start();
  t.after(() => callbacks.close());
  for (let index = 0; index < cases.length; index++) {
    const reply = await rawRequest(callbacks.baseUrl, { sessionId: identity.sessionId, key: identity.callbackKey }, {
      path: '/v1/capabilities/platform.musicScript.storage.get', body: JSON.stringify({ index }),
    }).response;
    assert.equal(reply.status, 200);
    assert.deepEqual(reply.value, { ok: false, contract: 'runtime.http.v1', code: cases[index].code });
  }
});

test('factory outbound caller uses the one authenticated callback HTTP endpoint and exact envelopes', async t => {
  const identity = config(); let calls = 0, outbound, premature;
  const callbacks = createCallbackServer({ sessionId: identity.sessionId, callbackKey: identity.callbackKey,
    methods: { 'platform.musicScript.storage.get': async input => { calls++; return { ok: true, value: input.key }; } } });
  await callbacks.start();
  const runtime = createAppRuntime({ ...identity, callbackUrl: callbacks.baseUrl, assemble({ call }) {
    outbound = call; premature = call('platform.musicScript.storage.get', {}); premature.catch(() => {});
    const module = fixture();
    module.methods['music.script.status'] = async (input, context) => {
      const reply = await call('platform.musicScript.storage.get', input, { signal: context.signal });
      return { ok: true, contract: module.contract, value: reply.value };
    };
    return [module];
  } });
  await runtime.start();
  const client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
  t.after(async () => { await Promise.all([client.close(), runtime.close(), callbacks.close()]); });
  await assert.rejects(premature, { code: 'CLOSED' });
  assert.equal((await client.call('music.script.status', { key: '原始身份' })).value, '原始身份');
  assert.equal(calls, 1);
  await runtime.close();
  await assert.rejects(outbound('platform.musicScript.storage.get', {}), { code: 'CLOSED' });
});

test('real LX invokes the per-session reverse HTTP owner with exact grant site and generation', async t => {
  const identity = config(), grant = randomUUID(), received = [];
  const callbacks = createCallbackServer({ sessionId: identity.sessionId, callbackKey: identity.callbackKey,
    methods: { 'platform.musicScript.storage.get': async (input, context) => {
      assert.equal(context.sessionId, identity.sessionId);
      assert.equal(context.isCurrent(), true);
      assert.equal(context.signal.aborted, false);
      received.push(input);
      return { ok: true, value: { é: 1, 'e\u0301': 2, key: input.key } };
    } } });
  await callbacks.start();
  const runtime = createAppRuntime({ ...identity, callbackUrl: callbacks.baseUrl, assemble: ({ call }) =>
    [createMusicScriptModule({ call, network: () => assert.fail('unexpected network') })] });
  await runtime.start();
  const client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
  t.after(async () => { await Promise.all([client.close(), runtime.close(), callbacks.close()]); });
  assert.equal((await client.upload('music', 'one', 1, Buffer.from(`
    lx.on('request', async (_input, {storage}) => storage.get('key'));
    lx.send('inited', {sources:{kw:{type:'music',actions:['musicUrl'],qualitys:['128k']}}});
  `), { basename: 'source.js' })).ok, true);
  const loaded = await client.call('music.script.load', { installationId: 'one', sourceRevision: 1,
    revision: 1, expectedRevision: 0, version: '1.0.0', config: {},
    permissions: { storage: ['read'] }, grant });
  assert.equal(loaded.ok, true, JSON.stringify(loaded));
  const { generation } = loaded.value.instance;
  assert.deepEqual((await client.call('music.script.invoke', { installationId: 'one', revision: 1,
    generation, siteId: 'kw', operation: 'musicUrl', payload: { quality: '128k', musicInfo: {} } })).value,
  { é: 1, 'e\u0301': 2, key: 'key' });
  assert.deepEqual(received, [{ grant, installationId: 'one', siteId: 'kw', generation, key: 'key' }]);
});

test('real LX reverse HTTP cancellation fences a retired grant while callback close waits for physical storage',
  { timeout: 15000 }, async t => {
    const identity = config(), entered = deferred(), release = deferred();
    const grants = new Map(), received = [], stored = new Map();
    let physical = 0, rejectedOld = 0, runtime, client, callbacks;
    t.after(async () => {
      release.resolve();
      await Promise.all([client?.close(), runtime?.close(), callbacks?.close()]);
    });
    const valid = (input, context) => {
      const auth = grants.get(input.grant);
      return auth?.active && auth.installationId === input.installationId &&
        auth.siteId === input.siteId && auth.generation === input.generation &&
        context.sessionId === identity.sessionId && !context.signal.aborted && context.isCurrent();
    };
    callbacks = createCallbackServer({ sessionId: identity.sessionId, callbackKey: identity.callbackKey,
      methods: Object.fromEntries(['get', 'set'].map(operation =>
        [`platform.musicScript.storage.${operation}`, async (input, context) => {
          assert.equal(valid(input, context), true, 'admission must carry the exact live authority');
          received.push({ operation, input: structuredClone(input), context });
          physical++;
          try {
            if (operation === 'set' && input.value === 'old-pending') {
              entered.resolve({ input, context });
              await release.promise; // The storage owner deliberately ignores abort until real settlement.
            }
            if (!valid(input, context)) {
              rejectedOld++;
              return { ok: false, code: 'STALE_SOURCE' };
            }
            const key = JSON.stringify([input.installationId, input.siteId, input.key]);
            if (operation === 'set') stored.set(key, input.value);
            return { ok: true, value: stored.get(key) ?? null };
          } finally { physical--; }
        }])) });
    await callbacks.start();
    runtime = createAppRuntime({ ...identity, callbackUrl: callbacks.baseUrl, assemble: ({ call }) =>
      [createMusicScriptModule({ call, network: () => assert.fail('unexpected network') })] });
    await runtime.start();
    client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
    const source = Buffer.from(`
      lx.on('request', async ({info}, {storage}) => {
        await storage.set('key', info.musicInfo.value);
        return await storage.get('key');
      });
      lx.send('inited', {sources:{kw:{type:'music',actions:['musicUrl'],qualitys:['128k']}}});
    `);
    const load = async (revision, expectedRevision) => {
      const grant = randomUUID();
      assert.equal((await client.upload('music', 'one', revision, source, { basename: 'source.js' })).ok, true);
      const reply = await client.call('music.script.load', { installationId: 'one', sourceRevision: revision,
        revision, expectedRevision, version: '1.0.0', config: {},
        permissions: { storage: ['read', 'write'] }, grant });
      assert.equal(reply.ok, true, JSON.stringify(reply));
      const instance = reply.value.instance;
      const auth = { installationId: 'one', siteId: 'kw', generation: instance.generation, active: true };
      grants.set(grant, auth);
      return { instance, grant, auth };
    };
    const invoke = (instance, value, options) => client.call('music.script.invoke', {
      installationId: 'one', revision: instance.revision, generation: instance.generation,
      siteId: 'kw', operation: 'musicUrl', payload: { quality: '128k', musicInfo: { value } },
    }, options);
    const old = await load(1, 0), controller = new AbortController();
    const pending = invoke(old.instance, 'old-pending', { signal: controller.signal });
    const rejected = assert.rejects(pending, { code: 'CANCELLED' });
    const captured = await entered.promise;
    assert.equal(physical, 1);
    controller.abort();
    await rejected;
    await until(() => captured.context.signal.aborted && !captured.context.isCurrent());
    await until(() => runtime.state.active === 0);
    assert.equal(callbacks.state.active, 1, 'runner transport completion is not remote storage settlement');
    assert.equal(physical, 1);
    old.auth.active = false;
    const next = await load(2, 1);
    assert.ok(next.instance.generation > old.instance.generation);
    assert.notEqual(next.grant, old.grant);
    assert.deepEqual(await invoke(next.instance, 'new-committed'),
      { ok: true, contract: 'music.script.v3', value: 'new-committed' });
    await until(() => callbacks.state.active === 1);
    assert.equal(physical, 1);
    assert.deepEqual(received.map(({ operation, input }) => ({ operation, input })), [
      { operation: 'set', input: { grant: old.grant, installationId: 'one', siteId: 'kw',
        generation: old.instance.generation, key: 'key', value: 'old-pending' } },
      { operation: 'set', input: { grant: next.grant, installationId: 'one', siteId: 'kw',
        generation: next.instance.generation, key: 'key', value: 'new-committed' } },
      { operation: 'get', input: { grant: next.grant, installationId: 'one', siteId: 'kw',
        generation: next.instance.generation, key: 'key' } },
    ]);
    for (const { context } of received.slice(1)) {
      assert.equal(context.signal.aborted, false, 'old cancellation must not cancel a new exchange');
      assert.equal(context.isCurrent(), false, 'normal finish retires currentness without cancellation');
    }
    let callbackClosed = false;
    const closing = callbacks.close().then(() => { callbackClosed = true; });
    await turn();
    assert.equal(callbackClosed, false);
    assert.equal(callbacks.state.active, 1);
    assert.deepEqual(await client.shutdown(), { ok: true, contract: 'runtime.http.v1',
      value: { closed: true, cleanupComplete: true } });
    assert.equal(callbackClosed, false, 'runner shutdown cannot stand in for callback owner drain');
    assert.equal(stored.get(JSON.stringify(['one', 'kw', 'key'])), 'new-committed');
    release.resolve();
    await closing;
    assert.equal(physical, 0);
    assert.equal(callbacks.state.active, 0);
    assert.equal(rejectedOld, 1);
    assert.equal(stored.get(JSON.stringify(['one', 'kw', 'key'])), 'new-committed');
  });

test('assembly factories cannot mix modules or async results and bad identity cannot allocate owners', () => {
  for (const input of [{ modules: [fixture()], assemble() {} }, { assemble: 'invalid' },
    { assemble: async () => [fixture()] }]) {
    assert.throws(() => createAppRuntime({ ...config(), ...input }), { code: 'INVALID_REQUEST' });
  }
  assert.throws(() => createAppRuntime({ ...config(), requestKey: 'invalid', assemble: () => assert.fail('allocated') }),
    { code: 'INVALID_REQUEST' });
});

test('a synchronous factory that creates duplicate modules closes once and revokes its captured caller', async t => {
  const identity = config(), disposed = deferred();
  let closes = 0, reverseCalls = 0, call;
  const callbacks = createCallbackServer({ sessionId: identity.sessionId, callbackKey: identity.callbackKey,
    methods: { 'platform.musicScript.storage.get': async () => {
      reverseCalls++;
      return { ok: true, value: null };
    } } });
  await callbacks.start();
  t.after(() => callbacks.close());
  assert.throws(() => createAppRuntime({ ...identity, callbackUrl: callbacks.baseUrl,
    assemble(dependencies) {
      call = dependencies.call;
      const module = fixture('music', () => { closes++; disposed.resolve(); });
      return [module, module];
    },
  }), { code: 'INVALID_REQUEST' });
  await disposed.promise;
  await assert.rejects(call('platform.musicScript.storage.get', { key: 'never-dispatched' }), { code: 'CLOSED' });
  await turn();
  assert.equal(closes, 1);
  assert.equal(reverseCalls, 0);
  assert.equal(callbacks.state.active, 0);
});

test('independent public create/start runtime exposes all three frozen versions and direct close', async t => {
  for (const factory of [async input => createRuntime(input).start(), startRuntime]) {
    const identity = config();
    const hostFactory = () => ({ list: () => [], async close() { return { cleanupComplete: true }; } });
    const runtime = await factory({ ...identity, dataRoot: 'unused', music: { hostFactory }, source: { hostFactory },
      cat: { runtimeFactory: () => ({ async close() {} }) } });
    const client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
    t.after(async () => { await client.close(); await runtime.close(); });
    const description = (await client.describe()).value;
    assert.equal(description.protocol, 'runtime.http.v1'); assert.equal(description.product, 'aggregate');
    assert.deepEqual(description.modules.map(({ id, version, contract }) => ({ id, version, contract })),
      ['music', 'cat', 'source'].map(id => ({ id, version: contracts[id].version, contract: contracts[id].contract })));
    assert.deepEqual(await client.shutdown(), { ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true } });
  }
});
