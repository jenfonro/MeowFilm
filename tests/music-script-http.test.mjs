import test from 'node:test';
import { until } from '../packages/runtime-bridge/test/http-fixture.js';
import { moduleHttp } from './runtime-http-fixture.mjs';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { createScriptHost, ScriptHostError, SCRIPT_LIMITS } from '@meowfilm/script-host';
import { encodeJSONValue } from '@meowfilm/runtime-bridge';
import { createMusicScriptModule, musicScriptContract } from '../apps/runtime/modules/music-script.mjs';

const limit = 512 * 1024;
const hash = value => createHash('sha256').update(value).digest('hex');
const declared = {
  kw: { name: '同名', type: 'music', actions: ['musicUrl'], qualitys: ['320k', 'flac'] },
  tx: { name: '同名', type: 'music', actions: ['musicUrl'], qualitys: ['320k'] },
  local: { name: '本地', type: 'music', actions: ['musicUrl', 'lyric', 'pic'], qualitys: [] },
};
function fixture({ setup = '', handler = `async ({source,action,info}) => ({
  label:meow.config.label,source,action,info,count:counts[source]=(counts[source]||0)+1
})`, sources = declared, end = '' } = {}) {
  return `\uFEFF// @name Synthetic LX fixture\nlet counts = {}; ${setup}
lx.on(lx.EVENT_NAMES.request, ${handler});
lx.send(lx.EVENT_NAMES.inited, {sources:${JSON.stringify(sources)}});
${end}`;
}
const options = (id = 'one', revision = 1, expectedRevision = 0) => ({
  installationId: id, revision, expectedRevision, version: 'fixture-1',
  config: { label: id }, permissions: { storage: [] },
  grant: randomUUID(),
});
const storageOptions = (...args) => ({
  ...options(...args), permissions: { storage: ['read', 'write'] },
});
function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, musicScriptContract);
  assert.deepEqual(Object.keys(reply).sort(), ['contract', 'ok', 'value']);
  return reply.value;
}
function denied(reply, code) {
  assert.deepEqual(reply, { ok: false, contract: musicScriptContract, code });
}
function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  promise.catch(() => {});
  return { promise, resolve, reject };
}
const turn = () => new Promise(resolve => setImmediate(resolve));
function memoryNative() {
  const calls = [], values = new Map();
  return {
    calls, values,
    async call(method, message, context) {
      assert.match(method, /^platform\.musicScript\.storage\.(get|set|delete)$/);
      const operation = method.split('.').at(-1);
      assert.deepEqual(Object.keys(message).sort(),
        ['grant', 'installationId', 'siteId', 'generation', 'key', ...(operation === 'set' ? ['value'] : [])].sort());
      assert.ok(context.signal instanceof AbortSignal);
      assert.equal(context.signal.aborted, false);
      calls.push({ method, message: structuredClone(message), signal: context.signal });
      const key = JSON.stringify([message.installationId, message.siteId, message.key]);
      if (operation === 'set') values.set(key, message.value);
      if (operation === 'delete') values.delete(key);
      return { ok: true, value: values.get(key) ?? null };
    },
  };
}
function setup(t, extra = {}) {
  let host;
  const native = memoryNative();
  const module = createMusicScriptModule({
    call: native.call,
    network: () => { assert.fail('No real or undeclared network is allowed by this fixture'); },
    hostFactory: input => { host = createScriptHost(input); return host; },
    ...extra,
  });
  t.after(async () => {
    okay(await module.close());
    if (host) {
      const stats = host.stats();
      for (const key of ['liveSessions', 'activeRequests', 'timers', 'networkRequests',
        'pendingAdapterTasks']) assert.equal(stats[key], 0, key);
      for (const key of ['localServices', 'activeWorkers']) assert.equal(key in stats, false, key);
      for (const key of ['openService', 'mapServiceURL', 'resource', 'services'])
        assert.equal(key in host, false, key);
    }
  });
  const rpc = moduleHttp(t, module);
  return { module, native, host: () => host, rpc };
}
async function upload(rpc, source = fixture(), installationId = 'one') {
  const bytes = Buffer.isBuffer(source) ? source : Buffer.from(source);
  rpc.nextRevision = (rpc.nextRevision ?? 0) + 1;
  okay(await rpc.upload(installationId, rpc.nextRevision, bytes, { basename: 'fixture.js' }));
  return rpc.nextRevision;
}
async function load(rpc, params = options(), source = fixture()) {
  return okay(await rpc('load', { ...params, sourceRevision: await upload(rpc, source, params.installationId) })).instance;
}
const request = (instance, siteId = 'kw', musicInfo = {}, extra = {}) => ({
  installationId: instance.installationId, revision: instance.revision, generation: instance.generation,
  siteId, operation: 'musicUrl', payload: { quality: '320k', musicInfo }, ...extra,
});
async function value(rpc, instance, siteId, musicInfo, extra) {
  return okay(await rpc('invoke', request(instance, siteId, musicInfo, extra)));
}
const storageFixture = fixture({ handler: `async ({info}, {storage}) => {
 const m=info.musicInfo;
 if(m.op==='set') return await storage.set(m.key||'key',m.value);
 if(m.op==='delete') return await storage.delete(m.key||'key');
 return await storage.get(m.key||'key');
}` });
const networkFixture = fixture({ handler: `async () => new Promise((resolve,reject) =>
 lx.request('https://fixture.invalid/value',{},(error,response,body)=>error?reject(error):resolve(body)))` });
const largeNetworkFixture = fixture({
  setup: 'const digest = value => lx.utils.crypto.md5(value);',
  handler: `async ({info}) => new Promise((resolve,reject) =>
    lx.request('https://fixture.invalid/large',{encoding:null},
      (error,response,body)=>error?reject(error):resolve({
        length:body.length, first:body[0], last:body[body.length-1],
        hash:digest(body),
        allocLength:Buffer.alloc(info.musicInfo.size||0,0x5a).length,
        allocHash:digest(Buffer.alloc(info.musicInfo.size||0,0x5a)),
      })))`,
});

// Timing seams wrap the real host, but the injected callback itself is not
// evidence that real LX startup storage is available.
function wrappedHost(hooks = {}) {
  const state = {};
  return {
    state,
    factory(input) {
      const raw = createScriptHost(input);
      Object.assign(state, { raw, storage: input.storage, input });
      return {
        list: () => raw.list(), stats: () => raw.stats(), close: () => raw.close(),
        captureInitialization: id => raw.captureInitialization(id), observe: id => raw.observe(id),
        invoke: (...args) => raw.invoke(...args), uninstall: (...args) => raw.uninstall(...args),
        async install(spec, options) {
          const result = await raw.install(spec, options);
          await hooks.afterInstall?.({ ...state, spec, result });
          return result;
        },
        async update(id, spec, options) {
          await hooks.beforeUpdate?.({ ...state, id, spec });
          const result = await raw.update(id, spec, options);
          await hooks.afterUpdate?.({ ...state, id, spec, result, options });
          return result;
        },
      };
    },
  };
}
function callback(installationId, generation, siteId = 'kw', extra = {}) {
  return { installationId, generation, siteId, key: 'key', signal: new AbortController().signal,
    isCurrent: () => true, ...extra };
}

test('public descriptor exposes only LX business methods and enveloped close', async t => {
  const { module, rpc } = setup(t);
  assert.equal(module.id, 'music'); assert.equal(module.version, '0.8.0');
  assert.deepEqual(Object.keys(module.methods).sort(), ['load', 'unload', 'invoke', 'status', 'close']
    .map(name => `music.script.${name}`).sort());
  assert.deepEqual(okay(await rpc('status')), { documentAccess: false, instances: [] });
  assert.throws(() => createMusicScriptModule(), { code: 'INVALID_REQUEST' });
  okay(await module.close()); okay(await rpc('close')); okay(await module.close());
  denied(await rpc('status'), 'CLOSED');
});

test('raw HTTP upload preserves basename and UTF-8 contracts before execution', async t => {
  const { rpc, native } = setup(t);
  for (const basename of ['../bad.js', 'C:\\bad.js', 'bad.txt', '/bad.js']) {
    denied(await rpc.upload('one', 1, Buffer.from('x'), { basename }), 'INVALID_REQUEST');
  }
  for (const bytes of [[0xff, 0xfe], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xe2, 0x82], [0xf4, 0x90, 0x80, 0x80]]) {
    denied(await rpc.upload('one', 1, Buffer.from(bytes), { basename: 'x.js' }), 'SCRIPT_ENCODING');
  }
  assert.equal(native.calls.length, 0);
});

test('source larger than the former 512KiB gate and a split UTF-8 character execute as real LX', async t => {
  const { rpc } = setup(t);
  const code = fixture();
  const prefix = '// ' + ' '.repeat(65532) + '猫\n'; // character starts at byte 65535.
  const unpadded = prefix + code;
  const sourceBytes = limit * 2 + 17;
  const source = unpadded + ' '.repeat(sourceBytes - Buffer.byteLength(unpadded));
  assert.equal(Buffer.byteLength(source), sourceBytes);
  denied(await rpc.upload('large', 1, Buffer.alloc(SCRIPT_LIMITS.maxSourceBytes + 1, 0x61), { basename: 'too-large.js' }), 'LIMIT');
  const instance = await load(rpc, options(), source);
  assert.equal(instance.contentHash, hash(Buffer.from(source)));
  assert.equal((await value(rpc, instance)).label, 'one');
});

test('load always executes LX, requires UUID and rejects disabled/Cat/document fields', async t => {
  const { rpc, native } = setup(t);
  const sourceRevision = await upload(rpc, `throw Error('synthetic-private-startup');`);
  for (const extra of [{ activate: false }, { protocol: 'cat-cjs-http-v1' }, { trustedExecution: true },
    { document: 'music.sources.v1' }, { contentHash: '0'.repeat(64) }]) {
    denied(await rpc('load', { ...options(), sourceRevision, ...extra }), 'INVALID_REQUEST');
  }
  for (const grant of ['', 'not-a-uuid', '00000000-0000-0000-0000-000000000000']) {
    denied(await rpc('load', { ...options(), sourceRevision, grant }), 'PERMISSION_DENIED');
  }
  denied(await rpc('load', { ...options(), sourceRevision }), 'SCRIPT_FAILURE');
  assert.deepEqual(okay(await rpc('status')).instances, []);
  assert.equal(native.calls.length, 0);
});

test('two real LX instances with identical platforms/configured names remain directed; lyric stays raw', async t => {
  const { rpc } = setup(t);
  const a = await load(rpc, options('a')), b = await load(rpc, options('b'));
  assert.equal(a.contentHash, b.contentHash);
  assert.equal(a.state, 'ready'); assert.equal(a.protocol, 'lx');
  assert.deepEqual(a.capabilities.kw.actions, ['musicUrl']);
  assert.deepEqual(a.capabilities.kw.qualitys, ['320k', 'flac']);
  assert.deepEqual(a.capabilities.local.actions, ['musicUrl', 'lyric', 'pic']);
  const values = await Promise.all([value(rpc, a), value(rpc, b), value(rpc, a, 'tx'), value(rpc, b, 'tx')]);
  assert.deepEqual(values.map(item => [item.label, item.source, item.count]),
    [['a', 'kw', 1], ['b', 'kw', 1], ['a', 'tx', 1], ['b', 'tx', 1]]);
  const text = '[00:01.00]原始歌词';
  const lyrics = await load(rpc, options('lyrics'), fixture({ handler: `async () => ${JSON.stringify(text)}` }));
  assert.equal(await value(rpc, lyrics, 'local', {}, { operation: 'lyric' }), text);
  for (const target of [
    request(a, 'kw', {}, { operation: 'lyric' }), request(a, 'local', {}, { operation: 'missing' }),
    request(a, 'kw', {}, { operation: 'search' }), request(a, 'default'),
    request(a, 'kw', {}, { payload: { quality: 'hires' } }),
  ]) denied(await rpc('invoke', target), 'CAPABILITY_UNAVAILABLE');
  denied(await rpc('invoke', request(a, 'kw', {}, { installationId: 'missing' })), 'SOURCE_NOT_FOUND');
});

test('failed directed invocation never calls a second same-platform instance', async t => {
  const { rpc } = setup(t);
  const bad = await load(rpc, options('bad'), fixture({ handler: `async () => { throw Error('private'); }` }));
  const good = await load(rpc, options('good'));
  denied(await rpc('invoke', request(bad)), 'SCRIPT_FAILURE');
  assert.equal((await value(rpc, good)).count, 1);
});

test('revision CAS, canonical idempotency and consumed grant uniqueness never restart an equal session', async t => {
  const { rpc } = setup(t);
  const spec = storageOptions();
  spec.config = { label: 'one', nested: { a: 1, b: 2 } };
  const first = await load(rpc, spec);
  denied(await rpc('load', { ...spec, sourceRevision: await upload(rpc) }), 'STALE_SOURCE');
  const sameRevision = await upload(rpc);
  const equal = okay(await rpc('load', { ...spec, expectedRevision: 1, sourceRevision: sameRevision,
    config: { nested: { b: 2, a: 1 }, label: 'one' },
    permissions: { ...spec.permissions, storage: ['write', 'read'] } })).instance;
  assert.equal(equal.generation, first.generation);
  for (const change of [
    { grant: randomUUID() }, { config: { label: 'changed' } }, { version: 'changed' },
    { permissions: { ...spec.permissions, storage: [] } },
  ]) {
    const sourceRevision = await upload(rpc);
    denied(await rpc('load', { ...spec, expectedRevision: 1, sourceRevision, ...change }), 'SCRIPT_INTEGRITY');
  }
  const changedSource = await upload(rpc, fixture({ setup: 'const changed = true;' }));
  denied(await rpc('load', { ...spec, expectedRevision: 1, sourceRevision: changedSource }), 'SCRIPT_INTEGRITY');
  const reused = await upload(rpc);
  denied(await rpc('load', { ...spec, revision: 2, expectedRevision: 1, sourceRevision: reused }), 'PERMISSION_DENIED');
  denied(await rpc('unload', { installationId: 'one', revision: 2 }), 'STALE_SOURCE');
  denied(await rpc('unload', { installationId: 'one', revision: 1, generation: first.generation + 1 }), 'STALE_SOURCE');
  assert.equal((await value(rpc, first)).count, 1);
});

test('source revisions can skip metadata revisions and never follow another source implicitly', async t => {
  const { rpc } = setup(t);
  const a = await load(rpc, options('a', 5, 0));
  const b = await load(rpc, options('b', 6, 0));
  assert.deepEqual(okay(await rpc('status')).instances.map(item => [item.installationId, item.revision]),
    [['a', 5], ['b', 6]]);
  const sourceRevision = await upload(rpc, fixture(), 'a'), spec = options('a', 9, 5);
  denied(await rpc('load', { ...spec, expectedRevision: 8, sourceRevision }), 'STALE_SOURCE');
  const next = okay(await rpc('load', { ...spec, sourceRevision })).instance;
  assert.equal(next.revision, 9);
  denied(await rpc('invoke', request(a)), 'STALE_SOURCE');
  assert.equal((await value(rpc, b)).label, 'b');
  assert.equal(okay(await rpc('unload', {
    installationId: next.installationId, revision: next.revision, generation: next.generation,
  })).cleanupComplete, true);
});

test('actual LX has no global storage; formal invocation keeps exact site/grant', async t => {
  const { rpc, native } = setup(t);
  const source = `(async () => {
 const startupStorage = typeof meow.storage;
 lx.on('request',async ({source}, {storage}) => {
   await storage.set('key',source);
   return {startupStorage,value:await storage.get('key')};
 });
 await lx.send('inited',{sources:${JSON.stringify(declared)}});
})()`;
  const spec = storageOptions(), instance = await load(rpc, spec, source);
  assert.equal(native.calls.length, 0);
  assert.deepEqual(await value(rpc, instance, 'tx'), { startupStorage: 'undefined', value: 'tx' });
  for (const { message } of native.calls) {
    assert.equal(message.grant, spec.grant);
    assert.equal(message.installationId, instance.installationId);
    assert.equal(message.generation, instance.generation);
    assert.equal(message.siteId, 'tx');
    assert.notEqual(message.key, 'startup');
  }
  const deniedStartup = await upload(rpc, `(async()=>{await meow.storage.set('startup','no');})()`);
  denied(await rpc('load', { ...storageOptions('one', 2, 1), sourceRevision: deniedStartup }), 'SCRIPT_FAILURE');
  assert.equal(okay(await rpc('status')).instances[0].generation, instance.generation);
  assert.deepEqual(await value(rpc, instance), { startupStorage: 'undefined', value: 'kw' });
});

test('storage get/set/delete is isolated by installation and site; no document reverse traffic', async t => {
  const { rpc, native } = setup(t);
  const aa = storageOptions('a'), bb = storageOptions('b');
  const a = await load(rpc, aa, storageFixture), b = await load(rpc, bb, storageFixture);
  await Promise.all([
    value(rpc, a, 'kw', { op: 'set', value: 'A-kw' }),
    value(rpc, a, 'tx', { op: 'set', value: 'A-tx' }),
    value(rpc, b, 'kw', { op: 'set', value: 'B-kw' }),
  ]);
  assert.deepEqual(await Promise.all([value(rpc, a), value(rpc, a, 'tx'), value(rpc, b), value(rpc, b, 'tx')]),
    ['A-kw', 'A-tx', 'B-kw', null]);
  assert.equal(await value(rpc, a, 'kw', { op: 'delete' }), null);
  assert.equal(await value(rpc, b), 'B-kw');
  for (const call of native.calls) assert.equal(call.message.grant, call.message.installationId === 'a' ? aa.grant : bb.grant);
});

test('storage permissions are enforced before reverse I/O and malformed grant parameters fail', async t => {
  const { rpc, native } = setup(t);
  const none = await load(rpc, options('none'), storageFixture);
  denied(await rpc('invoke', request(none)), 'PERMISSION_DENIED');
  denied(await rpc('invoke', request(none, 'kw', { op: 'set', value: 'x' })), 'PERMISSION_DENIED');
  const readSpec = storageOptions('read'); readSpec.permissions.storage = ['read'];
  const read = await load(rpc, readSpec, storageFixture);
  assert.equal(await value(rpc, read), null);
  denied(await rpc('invoke', request(read, 'kw', { op: 'delete' })), 'PERMISSION_DENIED');
  assert.equal(native.calls.length, 1);
  const network = await load(rpc, options('network'), networkFixture);
  denied(await rpc('invoke', request(network)), 'NETWORK_ERROR');
  const sourceRevision = await upload(rpc);
  for (const permissions of [
    { storage: ['execute'] },
    { storage: ['read', 'read'] },
  ]) denied(await rpc('load', { ...options('invalid'), sourceRevision, permissions }), 'INVALID_REQUEST');
  denied(await rpc('load', { ...options('invalid'), sourceRevision,
    permissions: { storage: [], unsupported: true } }), 'INVALID_REQUEST');
  denied(await rpc('load', { ...options('invalid'), sourceRevision, grant: 'not-a-uuid' }), 'PERMISSION_DENIED');
});

test('grant is not exposed in script globals/config/scriptInfo, host specs or public instances', async t => {
  const wrapped = wrappedHost({
    afterInstall({ spec }) {
      assert.equal(spec.grant, undefined); assert.equal(spec.metadata, undefined);
      assert.deepEqual(Object.keys(spec).sort(),
        ['installationId', 'protocol', 'enabled', 'source', 'contentHash', 'version', 'config', 'permissions'].sort());
      assert.equal(spec.contentHash, hash(spec.source));
    },
  });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const spec = storageOptions();
  const instance = await load(rpc, spec, fixture({ handler: `async () => ({
    config:meow.config,info:lx.currentScriptInfo,globalKeys:Object.keys(globalThis),
    grant:typeof grant,process:typeof process,require:typeof require
  })` }));
  const result = await value(rpc, instance);
  assert.equal(result.grant, 'undefined'); assert.equal(result.process, 'undefined');
  assert.equal(result.require, 'undefined');
  assert.ok(!JSON.stringify([instance, result, await rpc('status')]).includes(spec.grant));
});

test('host update trials preserve exact previous generation/grant on failure, success fences stale revision', async t => {
  const { rpc, native } = setup(t);
  const firstSpec = storageOptions(), first = await load(rpc, firstSpec, storageFixture);
  await value(rpc, first, 'kw', { op: 'set', value: 'stable' });
  const failedSpec = storageOptions('one', 2, 1);
  denied(await rpc('load', { ...failedSpec, sourceRevision: await upload(rpc,
    `setInterval(()=>{},10000);throw Error('synthetic-private-failure');`) }), 'SCRIPT_FAILURE');
  const retained = okay(await rpc('status')).instances[0];
  assert.equal(retained.revision, 1); assert.equal(retained.generation, first.generation);
  assert.equal(retained.contentHash, first.contentHash);
  assert.equal(await value(rpc, first), 'stable');
  assert.equal(native.calls.at(-1).message.grant, firstSpec.grant);
  const nextSpec = storageOptions('one', 2, 1);
  const next = await load(rpc, nextSpec, storageFixture);
  assert.ok(next.generation > first.generation + 1);
  assert.equal(await value(rpc, next), 'stable');
  assert.equal(native.calls.at(-1).message.grant, nextSpec.grant);
  denied(await rpc('invoke', request(first)), 'STALE_SOURCE');
  denied(await rpc('invoke', request(next, 'kw', {}, { revision: 1 })), 'STALE_SOURCE');
});

for (const phase of ['install', 'update']) {
  test(`HTTP abort during real LX ${phase} initialization never publishes and preserves an old live instance`, async t => {
    const entered = deferred();
    const { rpc, host, native } = setup(t, { network: input => new Promise((resolve, reject) => {
      entered.resolve(input);
      const abort = () => reject(new ScriptHostError('CANCELLED'));
      if (input.signal.aborted) abort();
      else input.signal.addEventListener('abort', abort, { once: true });
    }) });
    const oldSpec = storageOptions();
    const old = phase === 'update' ? await load(rpc, oldSpec, storageFixture) : null;
    if (old) await value(rpc, old, 'kw', { op: 'set', value: 'retained' });
    const spec = storageOptions('one', old ? 2 : 1, old ? 1 : 0);
    const sourceRevision = await upload(rpc, `
      lx.request('https://fixture.invalid/initialization', {}, error => {
        if (error) return;
        lx.on('request', async () => 'should-not-publish');
        lx.send('inited', { sources: ${JSON.stringify(declared)} });
      });
    `);
    const controller = new AbortController();
    const pending = rpc('load', { ...spec, sourceRevision }, { signal: controller.signal });
    const network = await entered.promise;
    assert.equal(network.signal.aborted, false);
    // A separately completed next candidate must survive failure of the held one.
    const nextRevision = await upload(rpc, storageFixture);
    controller.abort();
    denied(await pending, 'CANCELLED');
    await until(() => network.signal.aborted && rpc.transport.runtime.state.active === 0);
    assert.deepEqual(okay(await rpc('status')).instances, old ? [old] : []);
    assert.equal(host().stats().pendingAdapterTasks, 0);
    denied(await rpc('load', { ...spec, sourceRevision }), 'SOURCE_NOT_FOUND');
    if (old) {
      assert.equal(await value(rpc, old), 'retained');
      assert.equal(native.calls.at(-1).message.grant, oldSpec.grant);
    } else assert.equal(host().stats().installations, 0);
    const recovered = okay(await rpc('load', {
      ...storageOptions('one', old ? 2 : 1, old ? 1 : 0), sourceRevision: nextRevision,
    })).instance;
    assert.equal(recovered.state, 'ready');
    if (old) assert.ok(recovered.generation > old.generation);
  });
}

for (const [phase, operation] of [['install', 'load'], ['update', 'load'], ['update', 'unload']]) {
  test(`valid HTTP ${operation} stops the real LX ${phase} initializer before its installation tail`,
    { timeout: 15000 }, async t => {
      const entered = deferred();
      let captures = 0;
      const { rpc } = setup(t, {
        hostFactory: input => {
          const host = createScriptHost(input), capture = host.captureInitialization.bind(host);
          host.captureInitialization = id => { captures++; return capture(id); };
          return host;
        },
        network: input => new Promise((_resolve, reject) => {
          entered.resolve(input);
          input.signal.addEventListener('abort', () => reject(new ScriptHostError('CANCELLED')), { once: true });
        }),
      });
      const b = await load(rpc, options('b'));
      const old = phase === 'update' ? await load(rpc) : null;
      const sourceRevision = await upload(rpc, `
        lx.request('https://fixture.invalid/init-gate', {}, () => {});
      `);
      const controller = new AbortController();
      const trial = rpc('load', { ...options('one', old ? 2 : 1, old ? 1 : 0), sourceRevision },
        { signal: controller.signal });
      const network = await entered.promise;
      const next = { ...options('one', old ? 3 : 2, old ? 1 : 0),
        sourceRevision: await upload(rpc) };
      denied(await rpc('load', { ...next, version: 1 }), 'INVALID_REQUEST');
      const before = captures;
      const stale = rpc('load', { ...next, expectedRevision: 99 });
      await until(() => captures > before);
      const staleUnload = old ? rpc('unload', { installationId: old.installationId,
        revision: old.revision, generation: old.generation + 100 }) : null;
      if (staleUnload) await until(() => rpc.transport.runtime.state.active === 3);
      assert.equal(network.signal.aborted, false, 'a stale CAS must not stop the initializer');
      assert.deepEqual(okay(await rpc('status')).instances, old ? [b, old] : [b]);
      assert.equal((await value(rpc, b)).count, 1);
      const managing = operation === 'load' ? rpc('load', next)
        : rpc('unload', { installationId: 'one', revision: old.revision, generation: old.generation });
      await until(() => network.signal.aborted);
      assert.equal(controller.signal.aborted, false, 'the lifecycle request, not the original HTTP abort, stopped A');
      denied(await trial, 'STALE_SOURCE');
      denied(await stale, 'STALE_SOURCE');
      if (staleUnload) denied(await staleUnload, 'STALE_SOURCE');
      const result = okay(await managing);
      assert.equal((await value(rpc, b)).count, 2);
      if (operation === 'load') {
        assert.equal(result.instance.revision, next.revision, 'stale admission must not consume the next candidate');
        controller.abort();
        assert.equal((await value(rpc, result.instance)).count, 1, 'late cancellation must not stop the successor');
      } else assert.equal(result.cleanupComplete, true);
    });
}

test('music status remains coherent and B invokes while new A is ready and old A cleanup is gated',
  { timeout: 15000 }, async t => {
    const entered = deferred(), release = deferred();
    t.after(() => release.resolve());
    let host;
    const { rpc } = setup(t, { hostFactory: input => {
      host = createScriptHost(input);
      const adapter = host.adapters.lx;
      host.adapters.lx = input => {
        const session = adapter(input), stop = session.stop.bind(session);
        if (input.installationId === 'a' && input.generation === 1) {
          let stopping;
          session.stop = (...args) => stopping ??= (async () => {
            await stop(...args); entered.resolve(); await release.promise;
          })();
        }
        return session;
      };
      return host;
    } });
    const a = await load(rpc, options('a')), b = await load(rpc, options('b'));
    const updating = load(rpc, options('a', 2, 1), fixture({ handler: 'async () => "new-a"' }));
    await entered.promise;
    assert.ok(host.get('a').generation > a.generation);
    assert.equal(host.get('a').state, 'ready');
    assert.deepEqual(okay(await rpc('status')).instances, [{ ...a, state: 'failed' }, b]);
    assert.equal((await value(rpc, b)).count, 1);
    release.resolve();
    const next = await updating;
    assert.deepEqual(okay(await rpc('status')).instances, [next, b]);
    assert.equal(await value(rpc, next), 'new-a');
  });

test('candidate grant exists before load resolves; first callback pins generation, then becomes session grant', async t => {
  const wrapped = wrappedHost({
    async afterInstall({ storage, result }) {
      await storage.set(callback(result.installationId, result.generation, 'tx', { value: 'early' }));
      await storage.get(callback(result.installationId, result.generation, 'kw'));
    },
  });
  const { rpc, native } = setup(t, { hostFactory: wrapped.factory });
  const spec = storageOptions(), instance = await load(rpc, spec, storageFixture);
  assert.equal(native.calls.length, 2);
  assert.ok(native.calls.every(item => item.message.grant === spec.grant &&
    item.message.generation === instance.generation));
  assert.equal(await value(rpc, instance, 'tx'), 'early');
  assert.equal(native.calls.at(-1).message.grant, spec.grant);
});

test('old and trial callbacks never exchange grants; failed candidate loses authorization, old session retains it', async t => {
  let proposedGeneration = 1, suspendedGrant = null, trialGrant = null;
  const native = memoryNative(), rejectedOld = [];
  const overlay = new Map();
  const wrapped = wrappedHost({
    async beforeUpdate({ storage, raw, id }) {
      const old = raw.get(id);
      await assert.rejects(storage.get(callback(id, old.generation)), error => {
        rejectedOld.push(error.code); return error.code === 'STALE_SESSION';
      });
      await storage.set(callback(id, ++proposedGeneration, 'tx', { value: 'overlay-only' }));
    },
  });
  const { rpc } = setup(t, {
    hostFactory: wrapped.factory,
    async call(method, message, context) {
      if (message.grant === suspendedGrant) return { ok: false, code: 'STALE_SOURCE' };
      if (message.grant === trialGrant) {
        // A Native stand-in, not runtime persistence. A failed candidate is
        // discarded instead of being merged into stable instance/site data.
        if (method.endsWith('.set')) overlay.set(message.key, message.value);
        native.calls.push({ method, message: structuredClone(message), signal: context.signal });
        return { ok: true, value: overlay.get(message.key) ?? null };
      }
      return native.call(method, message, context);
    },
  });
  const oldSpec = storageOptions(), old = await load(rpc, oldSpec, storageFixture);
  suspendedGrant = oldSpec.grant;
  const candidate = storageOptions('one', 2, 1);
  trialGrant = candidate.grant;
  denied(await rpc('load', { ...candidate, sourceRevision: await upload(rpc, `throw Error('trial');`) }), 'SCRIPT_FAILURE');
  suspendedGrant = null; trialGrant = null; overlay.clear();
  assert.deepEqual(rejectedOld, ['STALE_SESSION']);
  assert.equal(native.calls.at(-1).message.grant, candidate.grant);
  await assert.rejects(wrapped.state.storage.get(callback('one', proposedGeneration)), { code: 'STALE_SESSION' });
  await wrapped.state.storage.get(callback('one', old.generation));
  assert.equal(native.calls.at(-1).message.grant, oldSpec.grant);
  assert.equal(native.values.size, 0);
  assert.equal(okay(await rpc('status')).instances[0].generation, old.generation);
});

test('generation mismatch after a pre-return callback retires the unconfirmed session and candidate grant', async t => {
  let wrongGeneration;
  const wrapped = wrappedHost({
    async afterInstall({ storage, result }) {
      wrongGeneration = result.generation + 1;
      await storage.get(callback(result.installationId, wrongGeneration));
    },
  });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  denied(await rpc('load', { ...storageOptions(), sourceRevision: await upload(rpc) }), 'STALE_SOURCE');
  assert.deepEqual(okay(await rpc('status')).instances, []);
  assert.equal(wrapped.state.raw.stats().installations, 0);
  await assert.rejects(wrapped.state.storage.get(callback('one', wrongGeneration)), { code: 'STALE_SESSION' });
});

test('callback checks currentness, signal, generation, exact scope and permissions before reverse calls', async t => {
  const wrapped = wrappedHost();
  const { rpc, native } = setup(t, { hostFactory: wrapped.factory });
  const instance = await load(rpc, storageOptions(), storageFixture);
  const controller = new AbortController(); controller.abort();
  for (const input of [
    callback('one', instance.generation, 'kw', { isCurrent: () => false }),
    callback('one', instance.generation, 'kw', { isCurrent: undefined }),
    callback('one', instance.generation, 'kw', { signal: controller.signal }),
    callback('one', instance.generation + 10), callback('other', instance.generation),
  ]) await assert.rejects(wrapped.state.storage.get(input), { code: 'STALE_SESSION' });
  for (const siteId of ['default', '', null, 'imaginary', 'mg']) {
    await assert.rejects(wrapped.state.storage.get(callback('one', instance.generation, siteId)),
      { code: 'PERMISSION_DENIED' });
  }
  assert.equal(native.calls.length, 0);
  okay(await rpc('unload', { installationId: 'one', revision: 1 }));
  await assert.rejects(wrapped.state.storage.get(callback('one', instance.generation)), { code: 'STALE_SESSION' });
  assert.equal(native.calls.length, 0);
});

test('reverse completion rechecks isCurrent instead of trusting the authorization captured before await', async t => {
  const entered = deferred(), release = deferred();
  const wrapped = wrappedHost();
  const { rpc } = setup(t, { hostFactory: wrapped.factory,
    call: async () => { entered.resolve(); await release.promise; return { ok: true, value: 'late' }; } });
  const instance = await load(rpc, storageOptions(), storageFixture);
  let current = true;
  const result = wrapped.state.storage.get(callback('one', instance.generation, 'kw',
    { isCurrent: () => current }));
  const rejected = assert.rejects(result, { code: 'STALE_SESSION' });
  await entered.promise; current = false; release.resolve();
  await rejected;
});

test('status completes during a mutation and storage remains independently available', async t => {
  const entered = deferred(), release = deferred();
  const wrapped = wrappedHost({ async afterInstall({ storage, result }) {
    entered.resolve(); await release.promise;
    await storage.get(callback(result.installationId, result.generation));
  } });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const sourceRevision = await upload(rpc);
  const loading = rpc('load', { ...storageOptions(), sourceRevision });
  await entered.promise;
  assert.deepEqual(okay(await rpc('status')).instances, []);
  release.resolve();
  const loaded = okay(await loading).instance;
  const stable = okay(await rpc('status')).instances[0];
  assert.equal(stable.revision, loaded.revision); assert.equal(stable.generation, loaded.generation);
  okay(await rpc('unload', { installationId: stable.installationId, revision: stable.revision, generation: stable.generation }));
  assert.deepEqual(okay(await rpc('status')).instances, []);
});

test('cancel after host publication retires new generation rather than reusing the old grant through rollback', async t => {
  const entered = deferred(), release = deferred();
  const wrapped = wrappedHost({
    async afterUpdate({ options }) { entered.resolve(options); await release.promise; },
  });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const old = await load(rpc, storageOptions(), storageFixture);
  const controller = new AbortController();
  const loading = rpc('load', { ...storageOptions('one', 2, 1), sourceRevision: await upload(rpc) },
    { signal: controller.signal });
  const hostContext = await entered.promise;
  controller.abort();
  denied(await loading, 'CANCELLED');
  await until(() => hostContext.signal.aborted);
  release.resolve();
  await until(() => rpc.transport.runtime.state.active === 0);
  assert.deepEqual(okay(await rpc('status')).instances, []);
  await assert.rejects(wrapped.state.storage.get(callback('one', old.generation)), { code: 'STALE_SESSION' });
  const recovered = await load(rpc, storageOptions(), storageFixture);
  assert.ok(recovered.generation > old.generation);
});

test('reverse errors are sanitized, result shapes are checked, and large responses are snapshotted', async t => {
  let response = { ok: false, code: 'PERMISSION_DENIED' };
  const { rpc } = setup(t, { call: async () => {
    if (response instanceof Error) throw response;
    return response;
  } });
  const instance = await load(rpc, storageOptions(), storageFixture);
  denied(await rpc('invoke', request(instance)), 'PERMISSION_DENIED');
  for (const invalid of [new Error('private-sourceRevision-do-not-log'), { ok: false, code: 'private-sourceRevision' },
    null, { ok: true }, { ok: 'true', value: 'invalid' }]) {
    response = invalid;
    const reply = await rpc('invoke', request(instance));
    assert.equal(reply.ok, false);
    assert.ok(['STORAGE_ERROR', 'INVALID_REQUEST'].includes(reply.code));
    assert.ok(!JSON.stringify(reply).includes('private-sourceRevision'));
  }
  response = { ok: true, value: 'x'.repeat(limit) };
  assert.equal(await value(rpc, instance), response.value);
  response = { ok: true, value: null };
  assert.equal(await value(rpc, instance), null);
});

test('large control envelopes retain content, config and native storage keep independent limits', async t => {
  const writes = [];
  const { rpc } = setup(t, { call: async (_method, message) => {
    writes.push(message);
    return { ok: false, code: 'LIMIT' };
  } });
  const instance = await load(rpc, options(), fixture({
    handler: `async ({info}) => 'x'.repeat(info.musicInfo.length)`,
  }));
  const overhead = 2;
  const exact = okay(await rpc('invoke', request(instance, 'kw', { length: limit - overhead })));
  assert.equal(Buffer.byteLength(JSON.stringify(exact)), limit);
  const above = okay(await rpc('invoke', request(instance, 'kw', { length: limit - overhead + 1 })));
  assert.equal(Buffer.byteLength(JSON.stringify(above)), limit + 1);
  const input = await load(rpc, options('input'), fixture({ handler: 'async ({info}) => info.musicInfo.data' }));
  assert.equal(await value(rpc, input, 'kw', { data: 'x'.repeat(limit) }), 'x'.repeat(limit));
  const sourceRevision = await upload(rpc, fixture(), 'config');
  denied(await rpc('load', { ...options('config'), sourceRevision, config: { data: 'x'.repeat(32768) } }), 'LIMIT');
  const storage = await load(rpc, storageOptions('storage'), fixture({
    handler: `async (_request, {storage}) => storage.set('big','x'.repeat(${limit}))`,
  }));
  denied(await rpc('invoke', request(storage)), 'LIMIT');
  assert.equal(writes.length, 1);
  assert.equal(writes[0].value, 'x'.repeat(limit));
});

test('instances have no fixed count quota, updates advance generations, and unload releases a binding', async t => {
  const { rpc, host } = setup(t);
  const instances = [];
  for (let index = 0; index < 32; index++) instances.push(await load(rpc, options(`i${index}`)));
  assert.equal(host().limits.requestTimeoutMs, 60000);
  assert.equal(host().limits.cleanupTimeoutMs, 100);
  assert.equal(host().limits.maxSourceBytes, SCRIPT_LIMITS.maxSourceBytes);
  const sourceRevision = await upload(rpc, fixture(), 'thirty-third'), spec = options('thirty-third');
  okay(await rpc('load', { ...spec, sourceRevision }));
  const updated = await load(rpc, options('i0', 2, 1));
  assert.ok(updated.generation > instances[0].generation);
  okay(await rpc('unload', { installationId: 'i1', revision: 1 }));
  assert.equal(okay(await rpc('status')).instances.length, 32);
});

test('network needs no domain grant and receives scoped identity, never platform credentials', async t => {
  const calls = [];
  const { rpc } = setup(t, { network: async input => {
    calls.push(input);
    assert.equal(input.validateURL('https://fixture.invalid/redirect'), 'https://fixture.invalid/redirect');
    assert.equal(input.validateURL('https://other.invalid/no'), 'https://other.invalid/no');
    return { statusCode: 200, body: 'synthetic-network' };
  } });
  const spec = options();
  const instance = await load(rpc, spec, networkFixture);
  assert.equal(await value(rpc, instance, 'tx'), 'synthetic-network');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].scope.installationId, instance.installationId);
  assert.equal(calls[0].scope.generation, instance.generation);
  assert.equal(calls[0].scope.siteId, 'tx');
  assert.equal(calls[0].grant, undefined);
  assert.equal(calls[0].options.headers, undefined);
});

test('LX handles a 12 MiB script buffer and a network response above the external message limit',
  { timeout: 60000 }, async t => {
    const size = 12 * 1024 * 1024;
    const body = Buffer.alloc(size, 0x5a);
    const md5 = value => createHash('md5').update(value).digest('hex');
    const { rpc } = setup(t, { network: async () => ({ statusCode: 200, body }) });
    const instance = await load(rpc, options(), largeNetworkFixture);
    const result = await value(rpc, instance, 'kw', { size });
    assert.deepEqual(result, {
      length: size, first: 0x5a, last: 0x5a, hash: md5(body),
      allocLength: size, allocHash: md5(Buffer.alloc(size, 0x5a)),
    });
    const next = await value(rpc, instance, 'kw', { size: 0 });
    assert.deepEqual(next, {
      length: size, first: 0x5a, last: 0x5a, hash: md5(body),
      allocLength: 0, allocHash: md5(Buffer.alloc(0, 0x5a)),
    });
  });

test('real LX input and invokeResult above 512 KiB, 1 MiB and 8 MiB are restored and serve the session',
  { timeout: 60000 }, async t => {
    const { rpc } = setup(t);
    const instance = await load(rpc, options(), fixture({
      handler: `async ({info}) => info.musicInfo.data ?? 'x'.repeat(info.musicInfo.size)`,
    }));
    for (const size of [limit + 1, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1]) {
      const data = 'x'.repeat(size);
      assert.equal(await value(rpc, instance, 'kw', { data }), data);
      assert.equal(await value(rpc, instance, 'kw', { size }), data);
    }
    assert.equal(await value(rpc, instance, 'kw', { size: 8 }), 'x'.repeat(8));
    assert.equal(okay(await rpc('status')).instances[0].state, 'ready');
  });

test('real LX load and status preserve large valid source declarations over HTTP', async t => {
  const { rpc } = setup(t);
  const sources = Object.fromEntries(Array.from({ length: 32 }, (_, index) => [`s${index}`, {
    name: '猫'.repeat(256), type: 'music',
    actions: Array.from({ length: 32 }, (_, item) => `a${item}`.padEnd(128, 'a')),
    qualitys: Array.from({ length: 32 }, (_, item) => `q${item}`.padEnd(64, 'q')),
  }]));
  const instance = await load(rpc, options('declarations'), fixture({
    sources, handler: 'async ({info}) => info',
  }));
  assert.ok(encodeJSONValue(instance).length > 65536);
  assert.deepEqual(instance.capabilities, sources);
  assert.deepEqual(okay(await rpc('status')).instances, [instance]);
  assert.deepEqual(await value(rpc, instance, 's0', {}, {
    operation: sources.s0.actions[0], payload: { expected: 'raw' },
  }), { expected: 'raw' });
});

test('extended source actions preserve payload and returned values without a platform whitelist', async t => {
  const { rpc } = setup(t);
  const instance = await load(rpc, options('extension'), fixture({
    sources: { qsvip: { name: 'extension', type: 'music', actions: ['musicSearch', 'musicUrl', 'lyric'],
      qualitys: [{ type: '128k', label: 'standard' }] } },
    handler: 'async ({source,action,info}) => ({source,action,info})',
  }));
  assert.equal(instance.state, 'ready');
  const info = { keyword: 'song', page: 2, pagesize: 10 };
  assert.deepEqual(await value(rpc, instance, 'qsvip', {}, { operation: 'musicSearch', payload: info }),
    { source: 'qsvip', action: 'musicSearch', info });
  assert.deepEqual(instance.capabilities.qsvip.qualitys, ['128k']);
  denied(await rpc('invoke', request(instance, 'qsvip', {}, { operation: 'search', payload: info })), 'CAPABILITY_UNAVAILABLE');
});

test('native script calls retain scope without per-site or per-instance count quotas', async t => {
  const entered = deferred(), release = deferred(), calls = [];
  const {rpc} = setup(t, {network: async input => {
    calls.push(input); if (calls.length === 24) entered.resolve();
    await release.promise; return {statusCode:200, body:'done'};
  }});
  const instance = await load(rpc, options(), networkFixture);
  const requests = Array.from({length:24}, (_,i) => rpc('invoke', request(instance, i % 2 ? 'kw' : 'tx')));
  try { await entered.promise; assert.equal(calls.length, 24); }
  finally { release.resolve(); }
  for (const reply of await Promise.all(requests)) okay(reply);
});

test('script control remains available while many invocations are in flight', async t => {
  const entered = deferred(), release = deferred(); let count = 0;
  const {rpc} = setup(t, {network: async () => {
    if (++count === 32) entered.resolve(); await release.promise;
    return {statusCode:200, body:'done'};
  }});
  const instances = [];
  for (let i=0;i<4;i++) instances.push(await load(rpc, options(`i${i}`), networkFixture));
  const requests = instances.flatMap(instance => Array.from({length:8}, () => rpc('invoke', request(instance))));
  try {
    await entered.promise; assert.equal(count,32);
    assert.equal(okay(await rpc('status')).instances.length,4);
  } finally { release.resolve(); }
  for (const reply of await Promise.all(requests)) okay(reply);
});

test('one invocation owns all reverse storage calls until their real completion', async t => {
  const entered = deferred(), release = deferred(); let calls = 0;
  const {rpc} = setup(t, {call: async () => {
    if (++calls === 20) entered.resolve(); await release.promise; return {ok:true, value:null};
  }});
  const instance = await load(rpc, storageOptions(), fixture({
    handler: `async (_request, {storage}) => Promise.all(Array.from({length:20},(_,i)=>storage.get('k'+i)))`,
  }));
  const pending = rpc('invoke', request(instance));
  try { await entered.promise; assert.equal(calls,20); }
  finally { release.resolve(); }
  okay(await pending);
});

test('pre-cancelled HTTP requests do not upload/load, cancellation fences reverse completion and future requests still work', async t => {
  const entered = deferred(), release = deferred();
  let signal, completedWrites = 0, delayed = true;
  const { rpc } = setup(t, { call: async (_method, _message, context) => {
    signal = context.signal;
    if (delayed) { entered.resolve(); await release.promise; }
    if (!signal.aborted) completedWrites++;
    return { ok: true, value: 'result' };
  } });
  const cancelled = new AbortController(); cancelled.abort();
  await assert.rejects(rpc.upload('one', 1, Buffer.from('x'), { basename: 'x.js', signal: cancelled.signal }), { code: 'CANCELLED' });
  const sourceRevision = await upload(rpc, storageFixture), spec = storageOptions();
  denied(await rpc('load', { ...spec, sourceRevision }, { signal: cancelled.signal }), 'CANCELLED');
  const instance = okay(await rpc('load', { ...spec, sourceRevision })).instance;
  const controller = new AbortController();
  const requestPromise = rpc('invoke', request(instance, 'kw', {
    op: 'set', value: 'no', padding: 'x'.repeat(65536),
  }),
    { signal: controller.signal });
  await entered.promise;
  controller.abort();
  denied(await requestPromise, 'CANCELLED');
  await until(() => signal.aborted);
  release.resolve(); await turn();
  assert.equal(completedWrites, 0);
  delayed = false;
  assert.equal(await value(rpc, instance), 'result');
});

test('unload clears callback authorization before waiting and reports adapters that ignore cancellation', async t => {
  const entered = deferred(), release = deferred();
  let signal;
  const { rpc } = setup(t, { call: async (_method, _message, context) => {
    signal = context.signal; entered.resolve(); await release.promise;
    return { ok: true, value: 'late' };
  } });
  const instance = await load(rpc, storageOptions(), storageFixture);
  const pending = rpc('invoke', request(instance));
  await entered.promise;
  try {
    denied(await rpc('unload', { installationId: 'one', revision: 1, generation: instance.generation }),
      'SCRIPT_CLEANUP');
    await until(() => signal.aborted);
    assert.equal((await pending).ok, false);
    assert.deepEqual(okay(await rpc('status')).instances, []);
  } finally { release.resolve(); await turn(); }
});

test('close cancels a pre-return reverse callback without tail/storage deadlock; callbacks remain revoked', async t => {
  const entered = deferred();
  let callbackInput;
  const wrapped = wrappedHost({
    async afterInstall({ storage, result }) {
      callbackInput = callback(result.installationId, result.generation);
      await storage.get(callbackInput);
    },
  });
  const { rpc } = setup(t, {
    hostFactory: wrapped.factory,
    call: (_method, _message, { signal }) => new Promise((_resolve, reject) => {
      entered.resolve();
      signal.addEventListener('abort', () => reject(new Error('synthetic abort')), { once: true });
    }),
  });
  const loading = rpc('load', { ...storageOptions(), sourceRevision: await upload(rpc) });
  await entered.promise;
  assert.deepEqual(okay(await rpc('status')), { documentAccess: false, instances: [] });
  okay(await rpc('close'));
  assert.equal((await loading).ok, false);
  denied(await rpc('status'), 'CLOSED');
  await assert.rejects(wrapped.state.storage.get(callbackInput), { code: 'STALE_SESSION' });
  okay(await rpc('close'));
});

test('repeated unload/reinstall fences ABA generations and cleans real timers, including invalid source failure', async t => {
  const { rpc, host } = setup(t);
  let generation = 0;
  const source = fixture({ setup: 'setInterval(()=>{},10000);' });
  for (let index = 0; index < 4; index++) {
    const instance = await load(rpc, options(), source);
    assert.ok(instance.generation > generation); generation = instance.generation;
    assert.equal(host().stats().timers, 1);
    okay(await rpc('unload', { installationId: 'one', revision: 1, generation }));
    assert.equal(host().stats().timers, 0);
    denied(await rpc('invoke', request(instance)), 'SOURCE_NOT_FOUND');
  }
  denied(await rpc('load', { ...options(), sourceRevision: await upload(rpc, 'not valid javascript !') }), 'SCRIPT_FAILURE');
  assert.equal(host().stats().installations, 0);
});

test('callback timeout is bounded by the real host and a failed startup does not occupy an instance', async t => {
  const { rpc } = setup(t, { hostFactory: input =>
    createScriptHost({ ...input, limits: { ...input.limits, initTimeoutMs: 1000, requestTimeoutMs: 30 } }) });
  denied(await rpc('load', { ...options(), sourceRevision: await upload(rpc, 'setInterval(()=>{},10000);') }), 'TIMEOUT');
  assert.deepEqual(okay(await rpc('status')).instances, []);
  const instance = await load(rpc, options(), fixture({ handler: 'async () => new Promise(()=>{})' }));
  denied(await rpc('invoke', request(instance)), 'TIMEOUT');
  assert.equal(okay(await rpc('status')).instances[0].state, 'ready');
});
