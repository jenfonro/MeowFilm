import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { createScriptHost, ScriptHostError } from '@meowfilm/script-host';
import { createMusicScriptModule, musicScriptContract } from '../apps/windows/runtime/modules/music-script.mjs';

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
  config: { label: id }, permissions: { networkOrigins: [], storage: [], localServices: false },
  grant: randomUUID(),
});
const storageOptions = (...args) => ({
  ...options(...args), permissions: { networkOrigins: [], storage: ['read', 'write'], localServices: false },
});
function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, musicScriptContract);
  assert.ok(Buffer.byteLength(JSON.stringify(reply)) <= limit);
  return reply;
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
  return { module, native, host: () => host,
    rpc: (name, params = {}, context) => module.methods[`music.script.${name}`](params, context) };
}
async function upload(rpc, source = fixture()) {
  const bytes = Buffer.isBuffer(source) ? source : Buffer.from(source);
  const begin = okay(await rpc('stage.begin', { basename: 'fixture.js', length: bytes.length, sha256: hash(bytes) }));
  assert.equal(begin.chunkBytes, 65536);
  for (let offset = 0; offset < bytes.length; offset += begin.chunkBytes) {
    const part = bytes.subarray(offset, offset + begin.chunkBytes);
    assert.equal(okay(await rpc('stage.chunk', { token: begin.token, offset, data: part.toString('base64') })).next,
      offset + part.length);
  }
  return begin.token;
}
async function load(rpc, params = options(), source = fixture()) {
  return okay(await rpc('load', { ...params, token: await upload(rpc, source) })).instance;
}
const request = (instance, siteId = 'kw', musicInfo = {}, extra = {}) => ({
  installationId: instance.installationId, revision: instance.revision, generation: instance.generation,
  siteId, operation: 'musicUrl', payload: { quality: '320k', musicInfo }, ...extra,
});
async function value(rpc, instance, siteId, musicInfo, extra) {
  return okay(await rpc('invoke', request(instance, siteId, musicInfo, extra))).value;
}
const storageFixture = fixture({ handler: `async ({info}) => {
 const m=info.musicInfo;
 if(m.op==='set') return await meow.storage.set(m.key||'key',m.value);
 if(m.op==='delete') return await meow.storage.delete(m.key||'key');
 return await meow.storage.get(m.key||'key');
}` });
const networkFixture = fixture({ handler: `async () => new Promise((resolve,reject) =>
 lx.request('https://fixture.invalid/value',{},(error,response,body)=>error?reject(error):resolve(body)))` });

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
        invoke: (...args) => raw.invoke(...args), uninstall: (...args) => raw.uninstall(...args),
        async install(spec) {
          const result = await raw.install(spec);
          await hooks.afterInstall?.({ ...state, spec, result });
          return result;
        },
        async update(id, spec) {
          await hooks.beforeUpdate?.({ ...state, id, spec });
          const result = await raw.update(id, spec);
          await hooks.afterUpdate?.({ ...state, id, spec, result });
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

test('descriptor exposes only eight script RPCs, factory injection and enveloped close', async t => {
  const { module, rpc } = setup(t);
  assert.equal(module.id, 'music'); assert.equal(module.version, '0.4.0');
  assert.deepEqual(Object.keys(module.methods).sort(), [
    'stage.begin', 'stage.chunk', 'stage.abort', 'load', 'unload', 'invoke', 'status', 'close',
  ].map(name => `music.script.${name}`).sort());
  assert.deepEqual(okay(await rpc('status')), {
    ok: true, contract: musicScriptContract, documentAccess: false, instances: [],
  });
  for (const method of ['music.sources.list', 'music.settings.get', 'music.lyrics.parse',
    'music.library.read', 'music.catalog.search', 'platform.document.read']) {
    assert.equal(module.methods[method], undefined);
  }
  assert.throws(() => createMusicScriptModule(), { code: 'INVALID_REQUEST' });
  okay(await module.close()); okay(await rpc('close')); okay(await module.close());
  denied(await rpc('status'), 'CLOSED');
});

test('stage limits are two 512KiB sources/1MiB total, expire lazily and abort idempotently', async t => {
  let now = 0;
  const { rpc } = setup(t, { clock: () => now });
  const begin = length => rpc('stage.begin', { basename: 'fixture.js', length, sha256: hash('x') });
  denied(await begin(limit + 1), 'LIMIT');
  denied(await begin(0), 'INVALID_REQUEST');
  const a = okay(await begin(limit)).token, b = okay(await begin(limit)).token;
  denied(await begin(1), 'LIMIT');
  okay(await rpc('stage.abort', { token: a })); okay(await rpc('stage.abort', { token: a }));
  const c = okay(await begin(limit)).token;
  now = 300000;
  denied(await rpc('stage.chunk', { token: b, offset: 0, data: 'YQ==' }), 'STAGE_NOT_FOUND');
  denied(await rpc('stage.chunk', { token: c, offset: 0, data: 'YQ==' }), 'STAGE_NOT_FOUND');
  okay(await begin(limit));
});

test('stage validates names, sequence, canonical base64, chunk size, hash and strict UTF-8', async t => {
  const { rpc, native } = setup(t);
  for (const basename of ['../bad.js', 'C:\\bad.js', 'bad.txt', '/bad.js']) {
    denied(await rpc('stage.begin', { basename, length: 1, sha256: hash('x') }), 'INVALID_REQUEST');
  }
  denied(await rpc('stage.begin', { basename: 'x.js', length: 1, sha256: 'wrong' }), 'INVALID_REQUEST');
  const token = okay(await rpc('stage.begin', { basename: 'x.js', length: 3, sha256: hash('abc') })).token;
  denied(await rpc('stage.chunk', { token, offset: 1, data: 'YQ==' }), 'SCRIPT_INTEGRITY');
  denied(await rpc('stage.chunk', { token, offset: 0, data: 'YWJj=' }), 'SCRIPT_INTEGRITY');
  denied(await rpc('stage.chunk', { token, offset: 0, data: 'A'.repeat(90000) }), 'LIMIT');
  denied(await rpc('load', { ...options(), token }), 'SCRIPT_INTEGRITY');
  okay(await rpc('stage.chunk', { token, offset: 0, data: 'YWJk' }));
  denied(await rpc('load', { ...options(), token }), 'SCRIPT_INTEGRITY');
  okay(await rpc('stage.abort', { token }));
  const bad = await upload(rpc, Buffer.from([0xff, 0xfe]));
  denied(await rpc('load', { ...options(), token: bad }), 'SCRIPT_ENCODING');
  assert.equal(native.calls.length, 0);
});

test('exact maximum source, BOM and a UTF-8 character split across chunks execute as real LX', async t => {
  const { rpc } = setup(t);
  const code = fixture();
  const prefix = '// ' + ' '.repeat(65532) + '猫\n'; // character starts at byte 65535.
  const unpadded = prefix + code;
  const source = unpadded + ' '.repeat(limit - Buffer.byteLength(unpadded));
  assert.equal(Buffer.byteLength(source), limit);
  const instance = await load(rpc, options(), source);
  assert.equal(instance.contentHash, hash(Buffer.from(source)));
  assert.equal((await value(rpc, instance)).label, 'one');
});

test('load always executes LX, requires UUID and rejects disabled/Cat/document fields', async t => {
  const { rpc, native } = setup(t);
  const token = await upload(rpc, `throw Error('synthetic-private-startup');`);
  for (const extra of [{ activate: false }, { protocol: 'cat-cjs-http-v1' }, { trustedExecution: true },
    { document: 'music.sources.v1' }]) {
    denied(await rpc('load', { ...options(), token, ...extra }), 'INVALID_REQUEST');
  }
  for (const grant of ['', 'not-a-uuid', '00000000-0000-0000-0000-000000000000']) {
    denied(await rpc('load', { ...options(), token, grant }), 'PERMISSION_DENIED');
  }
  denied(await rpc('load', { ...options(), token }), 'SCRIPT_FAILURE');
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
  assert.deepEqual(a.capabilities.local.actions, ['musicUrl', 'lyric']);
  const values = await Promise.all([value(rpc, a), value(rpc, b), value(rpc, a, 'tx'), value(rpc, b, 'tx')]);
  assert.deepEqual(values.map(item => [item.label, item.source, item.count]),
    [['a', 'kw', 1], ['b', 'kw', 1], ['a', 'tx', 1], ['b', 'tx', 1]]);
  const text = '[00:01.00]原始歌词';
  const lyrics = await load(rpc, options('lyrics'), fixture({ handler: `async () => ${JSON.stringify(text)}` }));
  assert.equal(await value(rpc, lyrics, 'local', {}, { operation: 'lyric' }), text);
  for (const target of [
    request(a, 'kw', {}, { operation: 'lyric' }), request(a, 'local', {}, { operation: 'pic' }),
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
  denied(await rpc('load', { ...spec, token: await upload(rpc) }), 'STALE_SOURCE');
  const sameToken = await upload(rpc);
  const equal = okay(await rpc('load', { ...spec, expectedRevision: 1, token: sameToken,
    config: { nested: { b: 2, a: 1 }, label: 'one' },
    permissions: { ...spec.permissions, storage: ['write', 'read'] } })).instance;
  assert.equal(equal.generation, first.generation);
  for (const change of [
    { grant: randomUUID() }, { config: { label: 'changed' } }, { version: 'changed' },
    { permissions: { ...spec.permissions, storage: [] } },
  ]) {
    const token = await upload(rpc);
    denied(await rpc('load', { ...spec, expectedRevision: 1, token, ...change }), 'SCRIPT_INTEGRITY');
    okay(await rpc('stage.abort', { token }));
  }
  const changedSource = await upload(rpc, fixture({ setup: 'const changed = true;' }));
  denied(await rpc('load', { ...spec, expectedRevision: 1, token: changedSource }), 'SCRIPT_INTEGRITY');
  okay(await rpc('stage.abort', { token: changedSource }));
  const reused = await upload(rpc);
  denied(await rpc('load', { ...spec, revision: 2, expectedRevision: 1, token: reused }), 'PERMISSION_DENIED');
  okay(await rpc('stage.abort', { token: reused }));
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
  const token = await upload(rpc), spec = options('a', 9, 5);
  denied(await rpc('load', { ...spec, expectedRevision: 8, token }), 'STALE_SOURCE');
  const next = okay(await rpc('load', { ...spec, token })).instance;
  assert.equal(next.revision, 9);
  denied(await rpc('invoke', request(a)), 'STALE_SOURCE');
  assert.equal((await value(rpc, b)).label, 'b');
  assert.equal(okay(await rpc('unload', {
    installationId: next.installationId, revision: next.revision, generation: next.generation,
  })).cleanupComplete, true);
});

test('actual LX startup storage is denied without default scope; formal invocation keeps exact site/grant', async t => {
  const { rpc, native } = setup(t);
  const source = `(async () => {
 let startupCode;
 try { await meow.storage.set('startup','must-not-save'); } catch(error) { startupCode=error.code; }
 lx.on('request',async ({source}) => {
   await meow.storage.set('key',source);
   return {startupCode,value:await meow.storage.get('key')};
 });
 await lx.send('inited',{sources:${JSON.stringify(declared)}});
})()`;
  const spec = storageOptions(), instance = await load(rpc, spec, source);
  assert.equal(native.calls.length, 0);
  assert.deepEqual(await value(rpc, instance, 'tx'), { startupCode: 'PERMISSION_DENIED', value: 'tx' });
  for (const { message } of native.calls) {
    assert.equal(message.grant, spec.grant);
    assert.equal(message.installationId, instance.installationId);
    assert.equal(message.generation, instance.generation);
    assert.equal(message.siteId, 'tx');
    assert.notEqual(message.key, 'startup');
  }
  const deniedStartup = await upload(rpc, `(async()=>{await meow.storage.set('startup','no');})()`);
  denied(await rpc('load', { ...storageOptions('one', 2, 1), token: deniedStartup }), 'PERMISSION_DENIED');
  assert.equal(okay(await rpc('status')).instances[0].generation, instance.generation);
  assert.deepEqual(await value(rpc, instance), { startupCode: 'PERMISSION_DENIED', value: 'kw' });
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

test('storage/network permissions are enforced before reverse I/O, including read-only and malformed grants', async t => {
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
  denied(await rpc('invoke', request(network)), 'PERMISSION_DENIED');
  const token = await upload(rpc);
  for (const permissions of [
    { networkOrigins: ['https://user:password@fixture.invalid'], storage: [], localServices: false },
    { networkOrigins: ['https://fixture.invalid/path'], storage: [], localServices: false },
  ]) denied(await rpc('load', { ...options('invalid'), token, permissions }), 'PERMISSION_DENIED');
  denied(await rpc('load', { ...options('invalid'), token,
    permissions: { networkOrigins: [], storage: [], localServices: true } }), 'CAPABILITY_UNAVAILABLE');
});

test('grant is not exposed in script globals/config/scriptInfo, host specs or public instances', async t => {
  const wrapped = wrappedHost({
    afterInstall({ spec }) {
      assert.equal(spec.grant, undefined); assert.equal(spec.metadata, undefined);
      assert.deepEqual(Object.keys(spec).sort(),
        ['installationId', 'protocol', 'enabled', 'source', 'version', 'config', 'permissions'].sort());
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
  denied(await rpc('load', { ...failedSpec, token: await upload(rpc,
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
  denied(await rpc('load', { ...candidate, token: await upload(rpc, `throw Error('trial');`) }), 'SCRIPT_FAILURE');
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
  denied(await rpc('load', { ...storageOptions(), token: await upload(rpc) }), 'STALE_SOURCE');
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

test('status waits for mutation tail but storage bypasses it; cancellation/unknown results can be reconciled', async t => {
  const entered = deferred(), release = deferred();
  const wrapped = wrappedHost({
    async afterInstall({ storage, result }) {
      entered.resolve();
      await release.promise;
      await storage.get(callback(result.installationId, result.generation));
    },
  });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const token = await upload(rpc);
  const loading = rpc('load', { ...storageOptions(), token });
  await entered.promise;
  let observed = false;
  const status = rpc('status').then(result => { observed = true; return result; });
  try {
    await turn(); assert.equal(observed, false);
  } finally { release.resolve(); }
  const loaded = okay(await loading).instance;
  const stable = okay(await status).instances[0];
  assert.equal(stable.revision, loaded.revision); assert.equal(stable.generation, loaded.generation);
  okay(await rpc('unload', { installationId: stable.installationId,
    revision: stable.revision, generation: stable.generation }));
  assert.deepEqual(okay(await rpc('status')).instances, []);
});

test('cancel after host publication retires new generation rather than reusing the old grant through rollback', async t => {
  const entered = deferred(), release = deferred();
  const wrapped = wrappedHost({
    async afterUpdate() { entered.resolve(); await release.promise; },
  });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const old = await load(rpc, storageOptions(), storageFixture);
  const controller = new AbortController();
  const loading = rpc('load', { ...storageOptions('one', 2, 1), token: await upload(rpc) },
    { signal: controller.signal });
  await entered.promise;
  const status = rpc('status');
  controller.abort(); release.resolve();
  denied(await loading, 'CANCELLED');
  assert.deepEqual(okay(await status).instances, []);
  await assert.rejects(wrapped.state.storage.get(callback('one', old.generation)), { code: 'STALE_SESSION' });
  const recovered = await load(rpc, storageOptions(), storageFixture);
  assert.ok(recovered.generation > old.generation);
});

test('reverse errors are sanitized, result shapes are checked, and response size is bounded', async t => {
  let response = { ok: false, code: 'PERMISSION_DENIED' };
  const { rpc } = setup(t, { call: async () => {
    if (response instanceof Error) throw response;
    return response;
  } });
  const instance = await load(rpc, storageOptions(), storageFixture);
  denied(await rpc('invoke', request(instance)), 'PERMISSION_DENIED');
  for (const invalid of [new Error('private-token-do-not-log'), { ok: false, code: 'private-token' },
    null, { ok: true }, { ok: 'true', value: 'invalid' }]) {
    response = invalid;
    const reply = await rpc('invoke', request(instance));
    assert.equal(reply.ok, false);
    assert.ok(['STORAGE_ERROR', 'INVALID_REQUEST'].includes(reply.code));
    assert.ok(!JSON.stringify(reply).includes('private-token'));
  }
  response = { ok: true, value: 'x'.repeat(limit) };
  denied(await rpc('invoke', request(instance)), 'LIMIT');
  response = { ok: true, value: null };
  assert.equal(await value(rpc, instance), null);
});

test('control envelope counts toward 512KiB, oversized input/config/reverse writes do not reach consumers', async t => {
  const { rpc, native } = setup(t);
  const instance = await load(rpc, options(), fixture({
    handler: `async ({info}) => 'x'.repeat(info.musicInfo.length)`,
  }));
  const overhead = Buffer.byteLength(JSON.stringify({ ok: true, contract: musicScriptContract, value: '' }));
  const exact = okay(await rpc('invoke', request(instance, 'kw', { length: limit - overhead })));
  assert.equal(Buffer.byteLength(JSON.stringify(exact)), limit);
  denied(await rpc('invoke', request(instance, 'kw', { length: limit - overhead + 1 })), 'LIMIT');
  denied(await rpc('invoke', request(instance, 'kw', { data: 'x'.repeat(limit) })), 'LIMIT');
  const token = await upload(rpc);
  denied(await rpc('load', { ...options('config'), token, config: { data: 'x'.repeat(32768) } }), 'LIMIT');
  okay(await rpc('stage.abort', { token }));
  const storage = await load(rpc, storageOptions('storage'), fixture({
    handler: `async () => meow.storage.set('big','x'.repeat(${limit}))`,
  }));
  denied(await rpc('invoke', request(storage)), 'LIMIT');
  assert.equal(native.calls.length, 0);
});

test('thirty-two real instances are the limit, updates reuse slots, and unloading permits a new instance', async t => {
  const { rpc, host } = setup(t);
  const instances = [];
  for (let index = 0; index < 32; index++) instances.push(await load(rpc, options(`i${index}`)));
  assert.equal(host().limits.maxInstances, 32);
  assert.equal(host().limits.maxSourceBytes, limit);
  assert.equal(host().limits.syncTimeoutMs, 2000);
  const token = await upload(rpc), spec = options('thirty-third');
  denied(await rpc('load', { ...spec, token }), 'LIMIT');
  const updated = await load(rpc, options('i0', 2, 1));
  assert.ok(updated.generation > instances[0].generation);
  okay(await rpc('unload', { installationId: 'i1', revision: 1 }));
  okay(await rpc('load', { ...spec, token }));
  assert.equal(okay(await rpc('status')).instances.length, 32);
});

test('network is origin-authorized and receives installation/generation/site identity, never platform credentials', async t => {
  const calls = [];
  const { rpc } = setup(t, { network: async input => {
    calls.push(input);
    assert.equal(input.authorizeURL('https://fixture.invalid/redirect'), 'https://fixture.invalid/redirect');
    assert.throws(() => input.authorizeURL('https://other.invalid/no'), { code: 'PERMISSION_DENIED' });
    return { statusCode: 200, body: 'synthetic-network' };
  } });
  const spec = options();
  spec.permissions.networkOrigins = ['https://fixture.invalid'];
  const instance = await load(rpc, spec, networkFixture);
  assert.equal(await value(rpc, instance, 'tx'), 'synthetic-network');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].scope.installationId, instance.installationId);
  assert.equal(calls[0].scope.generation, instance.generation);
  assert.equal(calls[0].scope.siteId, 'tx');
  assert.equal(calls[0].grant, undefined);
  assert.equal(calls[0].options.headers, undefined);
});

test('reviewed origin budget accepts 128 public origins and rejects local/private access', async t => {
  const { rpc } = setup(t);
  const accepted = Array.from({ length: 128 }, (_, index) => `https://source-${index}.example`);
  const spec = options('origins');
  spec.permissions.networkOrigins = accepted;
  const instance = await load(rpc, spec);
  assert.equal(instance.state, 'ready');

  let token = await upload(rpc);
  const overflow = options('origin-overflow');
  overflow.permissions.networkOrigins = [...accepted, 'https://overflow.example'];
  denied(await rpc('load', { ...overflow, token }), 'LIMIT');
  okay(await rpc('stage.abort', { token }));

  for (const [id, origin] of [
    ['loopback', 'http://127.0.0.1:8731'],
    ['private', 'http://192.168.1.8'],
    ['localhost', 'http://localhost:3000'],
  ]) {
    token = await upload(rpc);
    const deniedSpec = options(id);
    deniedSpec.permissions.networkOrigins = [origin];
    denied(await rpc('load', { ...deniedSpec, token }), 'PERMISSION_DENIED');
    okay(await rpc('stage.abort', { token }));
  }
});

test('per-site and per-instance invoke budgets reject excess work without targeting another script', async t => {
  let entered = deferred(), needed = 4;
  const release = deferred(), calls = [];
  const { rpc } = setup(t, { network: async input => {
    calls.push(input);
    if (calls.length === needed) entered.resolve();
    await release.promise;
    return { statusCode: 200, body: 'done' };
  } });
  const spec = options(); spec.permissions.networkOrigins = ['https://fixture.invalid'];
  const instance = await load(rpc, spec, networkFixture);
  const requests = Array.from({ length: 4 }, () => rpc('invoke', request(instance)));
  try {
    await entered.promise;
    denied(await rpc('invoke', request(instance)), 'LIMIT');
    entered = deferred(); needed = 8;
    requests.push(...Array.from({ length: 4 }, () => rpc('invoke', request(instance, 'tx'))));
    await entered.promise;
    denied(await rpc('invoke', request(instance, 'local')), 'LIMIT');
    assert.equal(calls.length, 8);
  } finally { release.resolve(); }
  for (const reply of await Promise.all(requests)) okay(reply);
});

test('global control/invoke budget is bounded at sixteen requests', async t => {
  const entered = deferred(), release = deferred();
  let count = 0;
  const { rpc } = setup(t, { network: async () => {
    if (++count === 16) entered.resolve();
    await release.promise;
    return { statusCode: 200, body: 'done' };
  } });
  const instances = [];
  for (let index = 0; index < 4; index++) {
    const spec = options(`i${index}`); spec.permissions.networkOrigins = ['https://fixture.invalid'];
    instances.push(await load(rpc, spec, networkFixture));
  }
  const requests = instances.flatMap(instance =>
    Array.from({ length: 4 }, () => rpc('invoke', request(instance))));
  try {
    await entered.promise;
    denied(await rpc('invoke', request(instances[0], 'tx')), 'LIMIT');
    assert.equal(count, 16);
  } finally { release.resolve(); }
  for (const reply of await Promise.all(requests)) okay(reply);
  assert.equal(okay(await rpc('status')).instances.length, 4);
});

test('one invocation cannot fan out unbounded reverse storage calls', async t => {
  const release = deferred();
  let calls = 0;
  const { rpc } = setup(t, { call: async () => {
    calls++; await release.promise; return { ok: true, value: null };
  } });
  const instance = await load(rpc, storageOptions(), fixture({
    handler: `async () => Promise.all(Array.from({length:20},(_,i)=>meow.storage.get('k'+i)))`,
  }));
  try {
    denied(await rpc('invoke', request(instance)), 'LIMIT');
    assert.ok(calls <= 4);
  } finally { release.resolve(); await turn(); }
});

test('pre-cancelled requests do not stage/load, cancellation fences reverse completion and future requests still work', async t => {
  const entered = deferred(), release = deferred();
  let signal, completedWrites = 0, delayed = true;
  const { rpc } = setup(t, { call: async (_method, _message, context) => {
    signal = context.signal;
    if (delayed) { entered.resolve(); await release.promise; }
    if (!signal.aborted) completedWrites++;
    return { ok: true, value: 'result' };
  } });
  const cancelled = new AbortController(); cancelled.abort();
  denied(await rpc('stage.begin', { basename: 'x.js', length: 1, sha256: hash('x') },
    { signal: cancelled.signal }), 'CANCELLED');
  const token = await upload(rpc, storageFixture), spec = storageOptions();
  denied(await rpc('load', { ...spec, token }, { signal: cancelled.signal }), 'CANCELLED');
  const instance = okay(await rpc('load', { ...spec, token })).instance;
  const controller = new AbortController();
  const requestPromise = rpc('invoke', request(instance, 'kw', { op: 'set', value: 'no' }),
    { signal: controller.signal });
  await entered.promise;
  controller.abort();
  denied(await requestPromise, 'CANCELLED');
  assert.equal(signal.aborted, true);
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
    assert.equal(signal.aborted, true);
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
  const loading = rpc('load', { ...storageOptions(), token: await upload(rpc) });
  await entered.promise;
  const waitingStatus = rpc('status');
  okay(await rpc('close'));
  assert.equal((await loading).ok, false);
  denied(await waitingStatus, 'CLOSED');
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
  denied(await rpc('load', { ...options(), token: await upload(rpc, 'not valid javascript !') }), 'SCRIPT_FAILURE');
  assert.equal(host().stats().installations, 0);
});

test('callback timeout is bounded by the real host and a failed startup does not occupy an instance', async t => {
  const { rpc } = setup(t, { hostFactory: input =>
    createScriptHost({ ...input, limits: { ...input.limits, initTimeoutMs: 30, requestTimeoutMs: 30 } }) });
  denied(await rpc('load', { ...options(), token: await upload(rpc, 'setInterval(()=>{},10000);') }), 'TIMEOUT');
  assert.deepEqual(okay(await rpc('status')).instances, []);
  const instance = await load(rpc, options(), fixture({ handler: 'async () => new Promise(()=>{})' }));
  denied(await rpc('invoke', request(instance)), 'TIMEOUT');
  assert.equal(okay(await rpc('status')).instances[0].state, 'ready');
});
