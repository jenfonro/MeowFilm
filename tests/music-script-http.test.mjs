import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createScriptHost, ScriptHostError, SCRIPT_LIMITS } from '@meowfilm/script-host';
import { encodeJSONValue } from '@meowfilm/runtime-bridge';
import { createMusicScriptModule, musicScriptContract } from '../apps/runtime/modules/music-script.mjs';
import { moduleHttp } from './runtime-http-fixture.mjs';
import { until, deferred } from '../packages/runtime-bridge/test/http-fixture.js';

const limit = 512 * 1024;
const hash = value => createHash('sha256').update(value).digest('hex');
const ids = new Map();
const idOf = name => { if (!ids.has(name)) ids.set(name, randomUUID()); return ids.get(name); };
const options = (name = 'one') => ({ installationId: idOf(name) });
const declared = {
  kw: { name: '同名', type: 'music', actions: ['musicUrl'], qualitys: ['320k', 'flac'] },
  tx: { name: '同名', type: 'music', actions: ['musicUrl'], qualitys: ['320k'] },
  local: { name: '本地', type: 'music', actions: ['musicUrl', 'lyric', 'pic'], qualitys: [] },
};
function fixture({ setup = '', handler = `async ({source,action,info}) => ({
  source,action,info,count:counts[source]=(counts[source]||0)+1
})`, sources = declared, end = '' } = {}) {
  return `\uFEFF// @name Synthetic LX fixture\nlet counts = {}; ${setup}
lx.on(lx.EVENT_NAMES.request, ${handler});
lx.send(lx.EVENT_NAMES.inited, {sources:${JSON.stringify(sources)}});
${end}`;
}
function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, musicScriptContract);
  assert.deepEqual(Object.keys(reply).sort(), ['contract', 'ok', 'value']);
  return reply.value;
}
function denied(reply, code) { assert.deepEqual(reply, { ok: false, contract: musicScriptContract, code }); }
function setup(t, extra = {}) {
  let host;
  const dataRoot = extra.dataRoot ?? mkdtempSync(join(tmpdir(), 'music-http-'));
  const module = createMusicScriptModule({ dataRoot,
    network: () => { throw new ScriptHostError('NETWORK_ERROR'); },
    hostFactory: input => { host = createScriptHost(input); return host; }, ...extra,
  });
  t.after(async () => {
    okay(await module.close());
    if (host) {
      const stats = host.stats();
      for (const key of ['liveSessions', 'activeRequests', 'timers', 'networkRequests', 'pendingAdapterTasks'])
        assert.equal(stats[key], 0, key);
      for (const key of ['localServices', 'activeWorkers']) assert.equal(key in stats, false, key);
      for (const key of ['openService', 'mapServiceURL', 'resource', 'services']) assert.equal(key in host, false, key);
    }
    if (!extra.dataRoot) rmSync(dataRoot, { recursive: true, force: true });
  });
  const rpc = moduleHttp(t, module);
  return { module, host: () => host, rpc, dataRoot };
}
async function upload(rpc, source = fixture(), installationId = idOf('one'), basename = 'fixture.js') {
  const bytes = Buffer.isBuffer(source) ? source : Buffer.from(source);
  const sourceRevision = rpc.nextRevision = (rpc.nextRevision ?? 0) + 1;
  okay(await rpc.upload(installationId, sourceRevision, bytes, { basename }));
  return sourceRevision;
}
async function add(rpc, source = fixture(), installationId = idOf('one')) {
  const sourceRevision = await upload(rpc, source, installationId);
  return okay(await rpc('sources.add', { installationId, sourceRevision })).item;
}
async function load(rpc, params = options(), source = fixture()) {
  await add(rpc, source, params.installationId);
  return okay(await rpc('sources.enable', params)).item;
}
async function update(rpc, item, source) {
  const sourceRevision = await upload(rpc, source, item.installationId);
  return okay(await rpc('sources.update', { installationId: item.installationId, sourceRevision })).item;
}
const request = (instance, siteId = 'kw', musicInfo = {}, extra = {}) => ({
  installationId: instance.installationId, siteId, operation: 'musicUrl',
  payload: { quality: '320k', musicInfo }, ...extra,
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
        length:body.length, first:body[0], last:body[body.length-1], hash:digest(body),
        allocLength:Buffer.alloc(info.musicInfo.size||0,0x5a).length,
        allocHash:digest(Buffer.alloc(info.musicInfo.size||0,0x5a)),
      })))`,
});
function wrappedHost(hooks = {}) {
  const state = {};
  return { state, factory(input) {
    const raw = createScriptHost(input);
    Object.assign(state, { raw, storage: input.storage, input });
    return {
      list: () => raw.list(), stats: () => raw.stats(), close: () => raw.close(),
      captureInitialization: id => raw.captureInitialization(id), observe: id => raw.observe(id),
      invoke: (...args) => raw.invoke(...args), uninstall: (...args) => raw.uninstall(...args),
      async install(spec, options) {
        const result = await raw.install(spec, options);
        await hooks.afterInstall?.({ ...state, spec, result, options }); return result;
      },
      async update(id, spec, options) {
        const result = await raw.update(id, spec, options);
        await hooks.afterUpdate?.({ ...state, id, spec, result, options }); return result;
      },
    };
  } };
}
function storageInput(installationId, generation, siteId = 'kw', extra = {}) {
  return { installationId, generation, siteId, key: 'key', signal: new AbortController().signal,
    isCurrent: () => true, ...extra };
}

test('raw HTTP upload preserves basename and UTF-8 contracts before execution', async t => {
  const { rpc } = setup(t);
  for (const basename of ['../bad.js', 'C:\\bad.js', 'bad.txt', '/bad.js']) {
    denied(await rpc.upload(idOf('one'), 1, Buffer.from('x'), { basename }), 'INVALID_REQUEST');
  }
  for (const bytes of [[0xff, 0xfe], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xe2, 0x82], [0xf4, 0x90, 0x80, 0x80]]) {
    denied(await rpc.upload(idOf('one'), 1, Buffer.from(bytes), { basename: 'x.js' }), 'SCRIPT_ENCODING');
  }
});

test('source larger than the former 512KiB gate and a split UTF-8 character execute as real LX', async t => {
  const { rpc } = setup(t);
  const code = fixture();
  const prefix = '// ' + ' '.repeat(65532) + '猫\n'; // character starts at byte 65535.
  const unpadded = prefix + code;
  const sourceBytes = limit * 2 + 17;
  const source = unpadded + ' '.repeat(sourceBytes - Buffer.byteLength(unpadded));
  assert.equal(Buffer.byteLength(source), sourceBytes);
  denied(await rpc.upload(idOf('large'), 1, Buffer.alloc(SCRIPT_LIMITS.maxSourceBytes + 1, 0x61), { basename: 'too-large.js' }), 'LIMIT');
  const instance = await load(rpc, options(), source);
  assert.equal(instance.contentHash, hash(Buffer.from(source)));
  assert.equal((await value(rpc, instance)).count, 1);
});

test('failed directed invocation never calls a second same-platform instance', async t => {
  const { rpc } = setup(t);
  const bad = await load(rpc, options('bad'), fixture({ handler: `async () => { throw Error('private'); }` }));
  const good = await load(rpc, options('good'));
  denied(await rpc('invoke', request(bad)), 'SCRIPT_FAILURE');
  assert.equal((await value(rpc, good)).count, 1);
});

test('private storage get/set/delete is isolated by installation and site', async t => {
  const { rpc } = setup(t);
  const aa = options('a'), bb = options('b');
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
    assert.equal(okay(await rpc('sources.list')).items[0].state, 'ready');
  });

test('real LX enable and list preserve large valid source declarations over HTTP', async t => {
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
  assert.deepEqual(okay(await rpc('sources.list')).items, [instance]);
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
    assert.equal(okay(await rpc('sources.list')).items.length,4);
  } finally { release.resolve(); }
  for (const reply of await Promise.all(requests)) okay(reply);
});

test('file identities and management reject external configuration, grants and execution selectors', async t => {
  const { rpc, host } = setup(t);
  denied(await rpc.upload('not-a-uuid', 1, Buffer.from('x'), { basename: 'x.js' }), 'INVALID_REQUEST');
  const sourceRevision = await upload(rpc, `throw Error('synthetic-private-startup');`);
  for (const extra of [{ activate: false }, { protocol: 'cat-cjs-http-v1' }, { trustedExecution: true },
    { document: 'music.sources.v1' }, { contentHash: '0'.repeat(64) }, { config: {} },
    { permissions: { storage: [] } }, { grant: randomUUID() }, { expectedRevision: 0 }])
    denied(await rpc('sources.add', { installationId: idOf('one'), sourceRevision, ...extra }), 'INVALID_REQUEST');
  const item = okay(await rpc('sources.add', { installationId: idOf('one'), sourceRevision })).item;
  assert.equal(host().stats().liveSessions, 0);
  denied(await rpc('sources.enable', { installationId: item.installationId }), 'SCRIPT_FAILURE');
  assert.equal(host().stats().installations, 0);
});

test('two identical LX sources stay directed and enabled content is selected without caller CAS', async t => {
  const { rpc } = setup(t);
  const a = await load(rpc, options('a')), b = await load(rpc, options('b'));
  assert.equal(a.contentHash, b.contentHash);
  assert.deepEqual((await Promise.all([value(rpc, a), value(rpc, b), value(rpc, a, 'tx'), value(rpc, b, 'tx')]))
    .map(item => [item.source, item.count]), [['kw', 1], ['kw', 1], ['tx', 1], ['tx', 1]]);
  const next = await update(rpc, a, fixture({ handler: 'async () => "changed"' }));
  assert.ok(next.generation > a.generation);
  assert.equal(await value(rpc, a), 'changed');
  for (const extra of [{ revision: 1 }, { generation: a.generation }])
    denied(await rpc('invoke', { ...request(a), ...extra }), 'INVALID_REQUEST');
  assert.equal((await value(rpc, b)).count, 2);
  const text = '[00:01.00]原始歌词';
  const lyric = await load(rpc, options('lyric'), fixture({ handler: `async () => ${JSON.stringify(text)}` }));
  assert.equal(await value(rpc, lyric, 'local', {}, { operation: 'lyric' }), text);
  for (const target of [request(a, 'kw', {}, { operation: 'lyric' }), request(a, 'local', {}, { operation: 'missing' }),
    request(a, 'kw', {}, { operation: 'search' }), request(a, 'default'),
    request(a, 'kw', {}, { payload: { quality: 'hires' } })])
    denied(await rpc('invoke', target), 'CAPABILITY_UNAVAILABLE');
  denied(await rpc('invoke', request(a, 'kw', {}, { installationId: randomUUID() })), 'SOURCE_NOT_FOUND');
});

test('fixed empty config and request storage expose no process, require or management data', async t => {
  const wrapped = wrappedHost({ afterInstall({ spec }) {
    assert.deepEqual(spec.config, {}); assert.deepEqual(spec.permissions, { storage: ['read', 'write'] });
    assert.equal(spec.grant, undefined); assert.equal(spec.metadata, undefined);
    assert.equal(spec.contentHash, hash(spec.source));
  } });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const item = await load(rpc, options(), fixture({ handler: `async (_,{storage}) => {
    await storage.set('private','value');
    return {config:meow.config,globalStorage:typeof meow.storage,process:typeof process,require:typeof require,
      privateValue:await storage.get('private')};
  }` }));
  assert.deepEqual(await value(rpc, item), { config: {}, globalStorage: 'undefined', process: 'undefined',
    require: 'undefined', privateValue: 'value' });
  const revision = await upload(rpc, `(async()=>{await meow.storage.set('startup','no');})()`);
  denied(await rpc('sources.update', { installationId: item.installationId, sourceRevision: revision }), 'SCRIPT_FAILURE');
  assert.equal(okay(await rpc('sources.detail', { installationId: item.installationId })).item.generation, item.generation);
});

test('private storage validates currentness, signal, generation and exact declared site before I/O', async t => {
  const wrapped = wrappedHost();
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const item = await load(rpc, options(), storageFixture);
  const controller = new AbortController(); controller.abort();
  for (const input of [
    storageInput(item.installationId, item.generation, 'kw', { isCurrent: () => false }),
    storageInput(item.installationId, item.generation, 'kw', { isCurrent: undefined }),
    storageInput(item.installationId, item.generation, 'kw', { signal: controller.signal }),
    storageInput(item.installationId, item.generation + 10), storageInput(randomUUID(), item.generation),
  ]) await assert.rejects(wrapped.state.storage.get(input), { code: 'STALE_SESSION' });
  for (const siteId of ['default', '', null, 'imaginary', 'mg'])
    await assert.rejects(wrapped.state.storage.get(storageInput(item.installationId, item.generation, siteId)),
      { code: 'PERMISSION_DENIED' });
  okay(await rpc('sources.disable', { installationId: item.installationId }));
  await assert.rejects(wrapped.state.storage.get(storageInput(item.installationId, item.generation)), { code: 'STALE_SESSION' });
});

test('actual old request storage contexts cannot borrow a later invocation authority', async t => {
  const { rpc } = setup(t);
  const item = await load(rpc, options(), fixture({ setup: 'let saved;', handler: `async ({info},{storage}) => {
    if(info.musicInfo.save) {saved=storage; await storage.set('key','current'); return null;}
    let code; try {await saved.set('key','stale'); code='unexpected';} catch(e) {code=e.code;}
    return {code,value:await storage.get('key')};
  }` }));
  assert.equal(await value(rpc, item, 'kw', { save: true }), null);
  assert.deepEqual(await value(rpc, item), { code: 'PERMISSION_DENIED', value: 'current' });
});

test('private KV accepts an independent 64-level value, rejects 65 levels and survives cold reopen', async t => {
  const first = setup(t);
  const source = fixture({ handler: `async ({info},{storage}) => {
    if(info.musicInfo.depth) {let v='leaf'; for(let i=0;i<info.musicInfo.depth;i++)v={child:v};
      await storage.set('depth',v);}
    return await storage.get('depth');
  }` });
  const item = await load(first.rpc, options(), source);
  let expected = 'leaf'; for (let i = 0; i < 64; i++) expected = { child: expected };
  assert.deepEqual(await value(first.rpc, item, 'kw', { depth: 64 }), expected);
  denied(await first.rpc('invoke', request(item, 'kw', { depth: 65 })), 'INVALID_REQUEST');
  assert.deepEqual(await value(first.rpc, item), expected);
  okay(await first.module.close());
  const reopened = setup(t, { dataRoot: first.dataRoot });
  assert.equal(reopened.host().stats().liveSessions, 0);
  assert.deepEqual(await value(reopened.rpc, item), expected);
});

test('private KV corrupt documents and invalid directories are explicit and never reset stored data', async t => {
  const { rpc, dataRoot } = setup(t);
  const item = await load(rpc, options(), storageFixture);
  await value(rpc, item, 'kw', { op: 'set', value: 'safe' });
  const path = join(dataRoot, 'music', 'data', `${item.installationId}.json`);
  writeFileSync(path, '{broken');
  denied(await rpc('invoke', request(item)), 'CORRUPT_STORAGE');
  denied(await rpc('invoke', request(item, 'kw', { op: 'set', value: 'replace' })), 'CORRUPT_STORAGE');
  assert.equal(readFileSync(path, 'utf8'), '{broken');
  writeFileSync(path, JSON.stringify({ schema: 1, sites: {} }));
  // A non-directory storage layout remains intact and cannot authorize a write.
  const directory = join(dataRoot, 'music', 'data');
  rmSync(path);
  rmSync(directory, { recursive: true });
  writeFileSync(directory, 'occupied');
  denied(await rpc('invoke', request(item, 'kw', { op: 'set', value: 'replace' })), 'PERMISSION_DENIED');
  assert.equal(readFileSync(directory, 'utf8'), 'occupied');
  rmSync(directory); mkdirSync(directory);
});

test('large JSON values retain content while private KV keeps its independent data boundary', async t => {
  const { rpc } = setup(t);
  const item = await load(rpc, options(), fixture({ handler: `async ({info}) => 'x'.repeat(info.musicInfo.length)` }));
  assert.equal(Buffer.byteLength(JSON.stringify(await value(rpc, item, 'kw', { length: limit - 2 }))), limit);
  assert.equal(Buffer.byteLength(JSON.stringify(await value(rpc, item, 'kw', { length: limit - 1 }))), limit + 1);
  const input = await load(rpc, options('input'), fixture({ handler: 'async ({info}) => info.musicInfo.data' }));
  assert.equal(await value(rpc, input, 'kw', { data: 'x'.repeat(limit) }), 'x'.repeat(limit));
  const storage = await load(rpc, options('storage'), fixture({ handler: `async (_,{storage}) => storage.set('big','x'.repeat(${limit}))` }));
  denied(await rpc('invoke', request(storage)), 'LIMIT');
});

test('private KV supports all twenty concurrent operations of one invocation', async t => {
  const { rpc } = setup(t);
  const item = await load(rpc, options(), fixture({ handler: `async (_,{storage}) => {
    await Promise.all(Array.from({length:20},(_,i)=>storage.set('k'+i,i)));
    return await Promise.all(Array.from({length:20},(_,i)=>storage.get('k'+i)));
  }` }));
  assert.deepEqual(await value(rpc, item), Array.from({ length: 20 }, (_, i) => i));
});

for (const phase of ['install', 'update']) {
  test(`HTTP abort during real LX ${phase} initialization never publishes and preserves an old live instance`, async t => {
    const entered = deferred();
    const { rpc, host } = setup(t, { network: input => new Promise((_resolve, reject) => {
      entered.resolve(input);
      const abort = () => reject(new ScriptHostError('CANCELLED'));
      if (input.signal.aborted) abort(); else input.signal.addEventListener('abort', abort, { once: true });
    }) });
    const old = phase === 'update' ? await load(rpc, options(), storageFixture) : null;
    if (old) await value(rpc, old, 'kw', { op: 'set', value: 'retained' });
    const slow = `lx.request('https://fixture.invalid/initialization',{},error=>{
      if(error)return;lx.on('request',async()=> 'should-not-publish');
      lx.send('inited',{sources:${JSON.stringify(declared)}});});`;
    const sourceRevision = await upload(rpc, slow);
    if (!old) okay(await rpc('sources.add', { installationId: idOf('one'), sourceRevision }));
    const controller = new AbortController();
    const pending = rpc(old ? 'sources.update' : 'sources.enable',
      { installationId: idOf('one'), ...(old ? { sourceRevision } : {}) }, { signal: controller.signal });
    const network = await entered.promise;
    const nextRevision = await upload(rpc, storageFixture + '\n// recovered replacement');
    controller.abort(); denied(await pending, 'CANCELLED');
    await until(() => network.signal.aborted && rpc.transport.runtime.state.active === 0);
    assert.equal(host().stats().pendingAdapterTasks, 0);
    if (old) {
      assert.equal(okay(await rpc('sources.detail', { installationId: old.installationId })).item.generation, old.generation);
      assert.equal(await value(rpc, old), 'retained');
      denied(await rpc('sources.update', { installationId: old.installationId, sourceRevision }), 'SOURCE_NOT_FOUND');
    } else assert.equal(host().stats().installations, 0);
    okay(await rpc('sources.update', { installationId: idOf('one'), sourceRevision: nextRevision }));
    const recovered = okay(await rpc('sources.enable', { installationId: idOf('one') })).item;
    assert.equal(recovered.state, 'ready');
    if (old) assert.ok(recovered.generation > old.generation);
  });
}

for (const [phase, operation] of [['install', 'sources.update'], ['update', 'sources.update'],
  ['update', 'sources.disable'], ['install', 'sources.remove']]) {
  test(`valid HTTP ${operation} stops the real LX ${phase} initializer before its installation tail`,
    { timeout: 15000 }, async t => {
      const entered = deferred();
      const { rpc } = setup(t, { network: input => new Promise((_resolve, reject) => {
        entered.resolve(input);
        input.signal.addEventListener('abort', () => reject(new ScriptHostError('CANCELLED')), { once: true });
      }) });
      const b = await load(rpc, options('b'));
      const old = phase === 'update' ? await load(rpc) : null;
      const sourceRevision = await upload(rpc, `lx.request('https://fixture.invalid/init-gate',{},()=>{});`);
      if (!old) okay(await rpc('sources.add', { installationId: idOf('one'), sourceRevision }));
      const controller = new AbortController();
      const trial = rpc(old ? 'sources.update' : 'sources.enable',
        { installationId: idOf('one'), ...(old ? { sourceRevision } : {}) }, { signal: controller.signal });
      const network = await entered.promise;
      const nextRevision = await upload(rpc);
      denied(await rpc('sources.update', { installationId: idOf('one'), sourceRevision: 'bad' }), 'INVALID_REQUEST');
      denied(await rpc('sources.update', { installationId: idOf('one'), sourceRevision: nextRevision + 1 }), 'SOURCE_NOT_FOUND');
      denied(await rpc('sources.disable', { installationId: idOf('one'), generation: 99 }), 'INVALID_REQUEST');
      assert.equal(network.signal.aborted, false);
      assert.equal((await value(rpc, b)).count, 1);
      const managing = rpc(operation, { installationId: idOf('one'),
        ...(operation === 'sources.update' ? { sourceRevision: nextRevision } : {}) });
      await until(() => network.signal.aborted);
      assert.equal(controller.signal.aborted, false);
      denied(await trial, 'STALE_SOURCE');
      okay(await managing);
      assert.equal((await value(rpc, b)).count, 2);
      if (operation === 'sources.update') {
        const next = okay(await rpc('sources.enable', { installationId: idOf('one') })).item;
        controller.abort();
        assert.equal((await value(rpc, next)).count, 1);
      }
    });
}

test('catalog remains coherent and B invokes while new A is ready and old A cleanup is gated',
  { timeout: 15000 }, async t => {
    const entered = deferred(), release = deferred(); t.after(() => release.resolve());
    let host;
    const { rpc } = setup(t, { hostFactory: input => {
      host = createScriptHost(input);
      const adapter = host.adapters.lx;
      host.adapters.lx = input => {
        const session = adapter(input), stop = session.stop.bind(session);
        if (input.installationId === idOf('a') && input.generation === 1) {
          let stopping;
          session.stop = (...args) => stopping ??= (async () => { await stop(...args); entered.resolve(); await release.promise; })();
        }
        return session;
      };
      return host;
    } });
    const a = await load(rpc, options('a')), b = await load(rpc, options('b'));
    const updating = update(rpc, a, fixture({ handler: 'async () => "new-a"' }));
    await entered.promise;
    assert.ok(host.get(a.installationId).generation > a.generation);
    assert.equal(host.get(a.installationId).state, 'ready');
    const listed = okay(await rpc('sources.list')).items;
    assert.equal(listed[0].contentHash, a.contentHash); assert.equal(listed[0].state, 'failed');
    assert.deepEqual(listed[0].capabilities, a.capabilities);
    assert.deepEqual(listed[1], b); assert.equal((await value(rpc, b)).count, 1);
    release.resolve(); const next = await updating;
    assert.deepEqual(okay(await rpc('sources.list')).items, [next, b]);
    assert.equal(await value(rpc, next), 'new-a');
  });

test('cancel after host publication retires the unconfirmed generation without silent restart', async t => {
  const entered = deferred(), release = deferred();
  const wrapped = wrappedHost({ async afterUpdate({ options }) { entered.resolve(options); await release.promise; } });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const old = await load(rpc, options(), storageFixture);
  const sourceRevision = await upload(rpc, storageFixture + '\n// changed');
  const controller = new AbortController();
  const changing = rpc('sources.update', { installationId: old.installationId, sourceRevision }, { signal: controller.signal });
  const context = await entered.promise; controller.abort(); denied(await changing, 'CANCELLED');
  await until(() => context.signal.aborted); release.resolve();
  await until(() => rpc.transport.runtime.state.active === 0);
  const failed = okay(await rpc('sources.detail', { installationId: old.installationId })).item;
  assert.equal(failed.state, 'failed'); assert.equal(failed.contentHash, old.contentHash);
  await assert.rejects(wrapped.state.storage.get(storageInput(old.installationId, old.generation)), { code: 'STALE_SESSION' });
  denied(await rpc('invoke', request(old)), 'CANCELLED');
  assert.equal(wrapped.state.raw.stats().installations, 0);
  const recovered = okay(await rpc('sources.enable', { installationId: old.installationId })).item;
  assert.ok(recovered.generation > old.generation);
});

test('same-process failed owners stay failed while cold startup lazily loads enabled sources', async t => {
  const first = setup(t);
  const item = await load(first.rpc, options(), fixture({ handler: `async ({info}) => {
    if(info.musicInfo.spin) {lx.send('updateAlert',{log:'spin-entered'});while(true){};} return 'alive';
  }` }));
  const controller = new AbortController();
  const pending = first.rpc('invoke', request(item, 'kw', { spin: true }), { signal: controller.signal });
  await until(() => first.host().get(item.installationId).updateAlert?.log === 'spin-entered');
  controller.abort(); denied(await pending, 'CANCELLED');
  await until(() => first.host().list()[0]?.state === 'failed');
  denied(await first.rpc('invoke', request(item)), 'SOURCE_DISABLED');
  const failed = okay(await first.rpc('sources.detail', { installationId: item.installationId })).item;
  assert.equal(failed.state, 'failed'); assert.equal(first.host().get(item.installationId).generation, item.generation);
  okay(await first.module.close());
  const cold = setup(t, { dataRoot: first.dataRoot });
  assert.equal(okay(await cold.rpc('sources.detail', { installationId: item.installationId })).item.state, 'idle');
  assert.equal(cold.host().stats().liveSessions, 0);
  assert.equal(await value(cold.rpc, item), 'alive');
});

test('instances have no fixed count quota, updates advance generations and disable retires timers', async t => {
  const { rpc, host } = setup(t); const items = [];
  for (let index = 0; index < 32; index++) items.push(await load(rpc, options(`i${index}`)));
  assert.equal(host().limits.requestTimeoutMs, 60000); assert.equal(host().limits.cleanupTimeoutMs, 100);
  assert.equal(host().limits.maxSourceBytes, SCRIPT_LIMITS.maxSourceBytes);
  await load(rpc, options('thirty-third'));
  const updated = await update(rpc, items[0], fixture() + '\n// new');
  assert.ok(updated.generation > items[0].generation);
  okay(await rpc('sources.disable', { installationId: items[1].installationId }));
  assert.equal(host().stats().liveSessions, 32);
  assert.equal(okay(await rpc('sources.list')).items.filter(item => item.enabled).length, 32);
});

test('pre-cancelled HTTP requests do not upload or enable and cancelled network completion cannot write KV', async t => {
  const entered = deferred(), release = deferred(); let signal;
  const { rpc, host } = setup(t, { network: async input => {
    signal = input.signal; entered.resolve(); await release.promise; return { statusCode: 200, body: 'late' };
  } });
  const cancelled = new AbortController(); cancelled.abort();
  await assert.rejects(rpc.upload(idOf('one'), 1, Buffer.from('x'), { basename: 'x.js', signal: cancelled.signal }), { code: 'CANCELLED' });
  const item = await add(rpc, fixture({ handler: `async ({info},{storage}) => {
    if(info.musicInfo.wait) {
      await new Promise((resolve,reject)=>lx.request('https://fixture.invalid/v',{},(e,r,b)=>e?reject(e):resolve(b)));
      await storage.set('late','forbidden');
    }
    return await storage.get('late');
  }` }));
  denied(await rpc('sources.enable', { installationId: item.installationId }, { signal: cancelled.signal }), 'CANCELLED');
  assert.equal(host().stats().liveSessions, 0);
  okay(await rpc('sources.enable', { installationId: item.installationId }));
  const controller = new AbortController();
  const pending = rpc('invoke', request(item, 'kw', { wait: true, padding: 'x'.repeat(65536) }), { signal: controller.signal });
  await entered.promise; controller.abort(); denied(await pending, 'CANCELLED');
  release.resolve(); await until(() => host().stats().pendingAdapterTasks === 0);
  assert.equal(signal.aborted, true);
  assert.equal(await value(rpc, item), null);
});

test('disable revokes request storage and reports adapters that ignore cancellation', async t => {
  const entered = deferred(), release = deferred(); let signal;
  const { rpc, host } = setup(t, { network: async input => {
    signal = input.signal; entered.resolve(); await release.promise; return { statusCode: 200, body: 'late' };
  } });
  const item = await load(rpc, options(), networkFixture);
  const pending = rpc('invoke', request(item)); await entered.promise;
  try {
    denied(await rpc('sources.disable', { installationId: item.installationId }), 'SCRIPT_CLEANUP');
    await until(() => signal.aborted); assert.equal((await pending).ok, false);
    assert.equal(okay(await rpc('sources.detail', { installationId: item.installationId })).item.enabled, false);
    assert.equal(host().stats().pendingAdapterTasks, 1);
  } finally { release.resolve(); await until(() => host().stats().pendingAdapterTasks === 0); }
});

test('close interrupts initialization without deadlocking its operation tail and revokes storage', async t => {
  const entered = deferred();
  const wrapped = wrappedHost();
  const { rpc, module } = setup(t, { hostFactory: wrapped.factory, network: input => new Promise((_resolve, reject) => {
    entered.resolve(input);
    input.signal.addEventListener('abort', () => reject(new ScriptHostError('CANCELLED')), { once: true });
  }) });
  const item = await add(rpc, `lx.request('https://fixture.invalid/init',{},()=>{});`);
  const pending = rpc('sources.enable', { installationId: item.installationId });
  const network = await entered.promise;
  okay(await module.close()); denied(await pending, 'CLOSED'); assert.equal(network.signal.aborted, true);
  await assert.rejects(wrapped.state.storage.get(storageInput(item.installationId, 1)), { code: 'STALE_SESSION' });
  okay(await module.close());
});

test('repeated disable and enable fences ABA generations and clears real timers including invalid source failure', async t => {
  const { rpc, host } = setup(t); let generation = 0;
  const item = await add(rpc, fixture({ setup: 'setInterval(()=>{},10000);' }));
  for (let index = 0; index < 4; index++) {
    const enabled = okay(await rpc('sources.enable', { installationId: item.installationId })).item;
    assert.ok(enabled.generation > generation); generation = enabled.generation;
    assert.equal(host().stats().timers, 1);
    okay(await rpc('sources.disable', { installationId: item.installationId }));
    assert.equal(host().stats().timers, 0); denied(await rpc('invoke', request(item)), 'SOURCE_DISABLED');
  }
  await update(rpc, item, 'not valid javascript !');
  denied(await rpc('sources.enable', { installationId: item.installationId }), 'SCRIPT_FAILURE');
  assert.equal(host().stats().installations, 0);
});

test('same-content enable and update keep generation while display edits do not choose a source revision', async t => {
  const { rpc } = setup(t);
  const item = await load(rpc), other = await load(rpc, options('other'));
  const same = okay(await rpc('sources.enable', { installationId: item.installationId })).item;
  assert.equal(same.generation, item.generation);
  okay(await rpc('sources.edit', { installationId: item.installationId, name: 'renamed' }));
  okay(await rpc.upload(item.installationId, 57, Buffer.from(fixture()), { basename: 'fixture.js' }));
  const unchanged = okay(await rpc('sources.update', { installationId: item.installationId, sourceRevision: 57 })).item;
  assert.equal(unchanged.generation, item.generation); assert.equal(unchanged.name, 'renamed');
  denied(await rpc('sources.update', { installationId: item.installationId, sourceRevision: 56 }), 'SOURCE_NOT_FOUND');
  assert.equal((await value(rpc, other)).count, 1);
});

test('failed trial preserves original generation and KV; successful publication revokes old storage inputs', async t => {
  const wrapped = wrappedHost();
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const item = await load(rpc, options(), storageFixture);
  await value(rpc, item, 'kw', { op: 'set', value: 'stable' });
  const sourceRevision = await upload(rpc, `setInterval(()=>{},10000);throw Error('synthetic-private-failure');`);
  denied(await rpc('sources.update', { installationId: item.installationId, sourceRevision }), 'SCRIPT_FAILURE');
  assert.equal(okay(await rpc('sources.detail', { installationId: item.installationId })).item.generation, item.generation);
  await assert.rejects(wrapped.state.storage.get(storageInput(item.installationId, item.generation + 1)), { code: 'STALE_SESSION' });
  assert.equal(await value(rpc, item), 'stable');
  const updated = await update(rpc, item, storageFixture + '\n// successful replacement');
  assert.ok(updated.generation > item.generation + 1);
  assert.equal(await value(rpc, updated), 'stable');
  await assert.rejects(wrapped.state.storage.get(storageInput(item.installationId, item.generation)), { code: 'STALE_SESSION' });
});

test('pre-return storage uses the real published generation while catalog queries stay available', async t => {
  const entered = deferred(), release = deferred();
  const wrapped = wrappedHost({ async afterInstall({ storage, result }) {
    entered.resolve(result); await release.promise;
    await storage.set(storageInput(result.installationId, result.generation, 'tx', { value: 'early' }));
  } });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const item = await add(rpc, storageFixture);
  const pending = rpc('sources.enable', { installationId: item.installationId });
  const record = await entered.promise;
  assert.equal(okay(await rpc('sources.detail', { installationId: item.installationId })).item.enabled, false);
  await assert.rejects(wrapped.state.storage.get(storageInput(item.installationId, record.generation + 1)), { code: 'STALE_SESSION' });
  release.resolve(); const enabled = okay(await pending).item;
  assert.equal(await value(rpc, enabled, 'tx'), 'early');
});

test('an invalid pre-return storage generation retires an unconfirmed owner instead of authorizing it', async t => {
  const wrapped = wrappedHost({ async afterInstall({ storage, result }) {
    await storage.get(storageInput(result.installationId, result.generation + 1));
  } });
  const { rpc } = setup(t, { hostFactory: wrapped.factory });
  const item = await add(rpc, storageFixture);
  denied(await rpc('sources.enable', { installationId: item.installationId }), 'STALE_SOURCE');
  assert.equal(wrapped.state.raw.stats().installations, 0);
  assert.equal(okay(await rpc('sources.detail', { installationId: item.installationId })).item.enabled, false);
  await assert.rejects(wrapped.state.storage.get(storageInput(item.installationId, 1)), { code: 'STALE_SESSION' });
});
