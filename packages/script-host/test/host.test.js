import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, createCipheriv, publicEncrypt, generateKeyPairSync, constants } from 'node:crypto';
import { createScriptHost } from '../src/index.js';
import { lxScript, target, deferred, turn, memoryStorage } from './fixtures.js';

function hostFor(t, options) {
  const host = createScriptHost(options);
  t.after(async () => {
    await host.close();
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().timers, 0);
    assert.equal(host.stats().networkRequests, 0);
  });
  return host;
}

test('host validates options and storage permissions', async t => {
  const host = hostFor(t);
  assert.throws(() => createScriptHost({ unknownOption: true }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => createScriptHost({ limits: { unknownLimit: 1 } }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => host.install({ installationId: 'denied', source: lxScript(),
    permissions: { unsupported: true } }), { code: 'INVALID_ARGUMENT' });
  assert.equal(host.list().length, 0);
  const value = await host.install({ installationId: 'a', source: lxScript(),
    permissions: {}, enabled: true });
  assert.equal(value.state, 'ready');
  assert.equal((await host.invoke(target())).action, 'musicUrl');
});

test('import is inert; enable is explicit; SDK payload and metadata stay exact', async t => {
  const host = hostFor(t);
  const imported = await host.install({ installationId: 'a', source: lxScript(), metadata: { name: 'Fixture' } });
  assert.equal(imported.state, 'disabled');
  assert.equal(imported.generation, 0);
  await assert.rejects(host.invoke(target()), { code: 'DISABLED' });
  const active = await host.enable('a');
  assert.equal(active.generation, 1);
  const result = await host.invoke(target('a', 'kw', { songmid: 'synthetic-id' }));
  assert.deepEqual(result, { source: 'kw', action: 'musicUrl',
    info: { type: '320k', musicInfo: { songmid: 'synthetic-id' } }, config: {} });
  assert.equal((await host.enable('a')).generation, 1);
});

test('same source, different configurations and same-named sites never select another installation', async t => {
  const host = hostFor(t);
  const source = lxScript({
    setup: 'let counts = {}; Object.prototype.localMark = meow.config.label; Buffer.prototype.localMark = meow.config.label;',
    handler: `async ({source}) => {
      counts[source] = (counts[source] || 0) + 1;
      return {label:meow.config.label,site:source,count:counts[source],
        objectMark:({}).localMark,bufferMark:Buffer.alloc(0).localMark};
    }`,
  });
  const a = await host.install({ installationId: 'a', source, config: { label: 'A' }, enabled: true });
  const b = await host.install({ installationId: 'b', source, config: { label: 'B' }, enabled: true });
  assert.equal(a.contentHash, b.contentHash);
  const outputs = await Promise.all([
    host.invoke(target('a', 'kw')), host.invoke(target('b', 'kw')),
    host.invoke(target('a', 'tx')), host.invoke(target('b', 'tx')),
  ]);
  assert.deepEqual(outputs.map(v => [v.label, v.site, v.count]), [
    ['A', 'kw', 1], ['B', 'kw', 1], ['A', 'tx', 1], ['B', 'tx', 1],
  ]);
  assert(outputs.every(v => v.objectMark === v.label && v.bufferMark === v.label));
  assert.equal(Object.prototype.localMark, undefined);
  assert.equal(Buffer.prototype.localMark, undefined);
  assert.equal((await host.invoke(target('b'))).count, 2);
  await assert.rejects(host.invoke(target('missing')), { code: 'NOT_FOUND' });
  await assert.rejects(host.invoke({ installationId: 'a', operation: 'musicUrl' }), { code: 'INVALID_ARGUMENT' });
});

test('declared extension actions and qualities remain exact without manufactured capabilities', async t => {
  const host = hostFor(t);
  const value = await host.install({ installationId: 'a', enabled: true,
    source: lxScript({ sources: {
      kw: { type: 'music', actions: ['search', 'musicUrl', 'musicUrl'], qualitys: ['hires', '320k', '320k'] },
      imaginary: { type: 'music', actions: ['search'], qualitys: ['master'] },
    } }) });
  assert.deepEqual(value.capabilities, {
    kw: { name: 'kw', type: 'music', actions: ['search', 'musicUrl'], qualitys: ['hires', '320k'] },
    imaginary: { name: 'imaginary', type: 'music', actions: ['search'], qualitys: ['master'] },
  });
  assert.equal((await host.invoke(target('a', 'kw', {}, { operation: 'search', payload: { keyword: 'test' } }))).info.keyword, 'test');
  await assert.rejects(host.invoke(target('a', 'kw', {}, { operation: 'missing' })), { code: 'CAPABILITY_UNAVAILABLE' });
  await assert.rejects(host.invoke(target('a', 'kw', {}, { payload: { quality: 'undeclared' } })), { code: 'CAPABILITY_UNAVAILABLE' });
  await assert.rejects(host.invoke(target('a', 'imaginary')), { code: 'CAPABILITY_UNAVAILABLE' });
  assert.equal((await host.invoke(target())).source, 'kw');
});

test('optional unknown listener rejects; replacement handler and inited Promise work', async t => {
  const host = hostFor(t);
  const source = `
let optionalRejected = false;
lx.on('optional',()=>{}).catch(() => { optionalRejected = true; });
lx.on('request',async () => { throw Error('old'); });
lx.on('request',async () => ({optionalRejected,env:lx.env,version:lx.version,
  process:typeof process,require:typeof require,fetch:typeof fetch,
  name:lx.currentScriptInfo.name,raw:lx.currentScriptInfo.rawScript.includes('optionalRejected')}))
.then(() => lx.send('inited',{sources:{kw:{type:'music',actions:['musicUrl'],qualitys:['320k']}}}));
`;
  await host.install({ installationId: 'a', source, metadata: { name: 'Fixture' }, enabled: true });
  assert.deepEqual(await host.invoke(target()), { optionalRejected: true, env: 'desktop', version: '2.0.0',
    process: 'undefined', require: 'undefined', fetch: 'undefined', name: 'Fixture', raw: true });
});

for (const [name, source, code] of [
  ['missing handler', "lx.send('inited',{sources:{kw:{type:'music',actions:['musicUrl'],qualitys:['320k']}}})", 'SCRIPT_PROTOCOL_ERROR'],
  ['negative init', "lx.on('request',async()=>1); lx.send('inited',{status:false,sources:{}})", 'SCRIPT_PROTOCOL_ERROR'],
  ['malformed arrays', lxScript({ sources: { kw: { type: 'music', actions: 'musicUrl', qualitys: [] } } }), 'SCRIPT_PROTOCOL_ERROR'],
  ['duplicate init', lxScript() + "lx.send('inited',{});", 'SCRIPT_PROTOCOL_ERROR'],
  ['throw after ready', lxScript() + "throw new Error('synthetic');", 'SCRIPT_EXCEPTION'],
  ['no inited', "setInterval(()=>{},1);", 'TIMEOUT'],
  ['synchronous runaway init', "while (true) {}", 'TIMEOUT'],
]) {
  test(`initialization rejects ${name}, leaves no resources and retains import`, async t => {
    const host = hostFor(t, { limits: { initTimeoutMs: 50, syncTimeoutMs: 20 } });
    await assert.rejects(host.install({ installationId: 'a', source, enabled: true }), { code });
    assert.equal(host.get('a').enabled, false);
    assert.equal(host.get('a').lastFailure, code);
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().timers, 0);
  });
}

test('a synchronous handler timeout does not grant a false success', async t => {
  const host = hostFor(t, { limits: { syncTimeoutMs: 20 } });
  await host.install({ installationId: 'a', enabled: true, source: lxScript({ handler: 'async () => { while (true) {} }' }) });
  await assert.rejects(host.invoke(target()), { code: 'TIMEOUT' });
  assert.equal(host.stats().activeRequests, 0);
});

test('timer ownership clears request timers and intervals on cancel, timeout, and success', async t => {
  const host = hostFor(t);
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    setup: 'let fired = 0;',
    handler: `async ({info}) => {
      if (info.musicInfo.probe) return fired;
      setInterval(() => { fired++; },10000);
      if (info.musicInfo.done) return 'done';
      return new Promise(resolve => setTimeout(() => { fired++; resolve('late'); },10000));
    }`,
  }) });
  const controller = new AbortController();
  const pending = host.invoke(target(), { signal: controller.signal });
  const rejected = assert.rejects(pending, { code: 'CANCELLED' });
  assert.equal(host.stats().timers, 2);
  controller.abort();
  await rejected;
  assert.equal(host.stats().timers, 0);
  assert.equal(await host.invoke(target('a', 'kw', { probe: true })), 0);
  await assert.rejects(host.invoke(target(), { timeoutMs: 20 }), { code: 'TIMEOUT' });
  assert.equal(host.stats().timers, 0);
  assert.equal(await host.invoke(target('a', 'kw', { done: true })), 'done');
  assert.equal(host.stats().timers, 0);
});

test('injected network accepts dynamically constructed origins without an origin grant', async t => {
  let calls = 0;
  const host = hostFor(t, { network: async () => { calls++; return { statusCode: 200, body: 'ok' }; } });
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => new Promise((resolve,reject) => lx.request('https://' + 'fixture.invalid/private',{},
      (e,r,b)=>e?reject(e):resolve(b)))`,
  }) });
  assert.equal(await host.invoke(target()), 'ok');
  assert.equal(calls, 1);
  await host.update('a', { permissions: {},
    source: lxScript({ handler: `async () => new Promise((resolve,reject)=>
      lx.request('https://fixture.invalid/public',{},(e,r,b)=>e?reject(e):resolve(b)))` }) });
  assert.equal(await host.invoke(target()), 'ok');
  assert.equal(calls, 2);
});

test('non-HTTP input is rejected before injected network access', async t => {
  let calls = 0;
  const host = hostFor(t, { network: () => { calls++; } });
  await host.install({ installationId: 'a', enabled: true,
    source: lxScript({ handler: `async () => new Promise(resolve =>
      lx.request('file:///private',{},error=>resolve(error.code)))` }) });
  assert.equal(await host.invoke(target()), 'INVALID_ARGUMENT');
  assert.equal(calls, 0);
});

test('cancel propagates AbortSignal; late network completion cannot call old callbacks', async t => {
  const started = deferred(), response = deferred();
  let outbound;
  const host = hostFor(t, { network: request => { outbound = request; started.resolve(); return response.promise; } });
  await host.install({ installationId: 'a', enabled: true,
    permissions: {},
    source: lxScript({ setup: 'let callbacks = 0;', handler: `async ({info}) => {
      if (info.musicInfo.probe) return callbacks;
      return new Promise((resolve,reject)=>lx.request('https://fixture.invalid/x',{},
        (e,r,b)=>{callbacks++;e?reject(e):resolve(b)}));
    }` }) });
  const controller = new AbortController();
  const request = host.invoke(target(), { signal: controller.signal });
  const rejected = assert.rejects(request, { code: 'CANCELLED' });
  await started.promise;
  controller.abort();
  await rejected;
  assert.equal(outbound.signal.aborted, true);
  assert.equal(host.stats().networkRequests, 0);
  response.resolve({ statusCode: 200, body: 'too late' });
  await turn();
  assert.equal(await host.invoke(target('a', 'kw', { probe: true })), 0);
});

test('LX abort closure is idempotent and invokes one AbortError callback', async t => {
  let transportStarted = 0;
  const host = hostFor(t, { network: async () => { transportStarted++; return { statusCode: 200, body: 'late' }; } });
  await host.install({ installationId: 'a', enabled: true,
    permissions: {},
    source: lxScript({ handler: `async () => new Promise(resolve => {
      let count = 0;
      const abort = lx.request('https://fixture.invalid/x',{},e => { count++; resolve({count,code:e.code,name:e.name}); });
      abort(); abort();
    })` }) });
  assert.deepEqual(await host.invoke(target()), { count: 1, code: 'CANCELLED', name: 'AbortError' });
  assert.equal(transportStarted, 0);
});

test('per-request network deadline reports TIMEOUT without killing the session', async t => {
  const host = hostFor(t, { network: () => new Promise(() => {}) });
  await host.install({ installationId: 'a', enabled: true,
    permissions: {},
    source: lxScript({ handler: `async () => new Promise(resolve =>
      lx.request('https://fixture.invalid/x',{timeout:15},e=>resolve(e.code)))` }) });
  assert.equal(await host.invoke(target()), 'TIMEOUT');
  assert.equal(host.get('a').state, 'ready');
  assert.equal(host.stats().networkRequests, 0);
});

test('storage is denied unless granted, uses instance/site scope and survives configuration revision', async t => {
  const storage = memoryStorage();
  const host = hostFor(t, { storage: storage.adapter });
  const source = lxScript({ handler: `async ({info}, {storage}) => {
    if (info.musicInfo.write) await storage.set('value',meow.config.label);
    return await storage.get('value');
  }` });
  await host.install({ installationId: 'a', enabled: true, source, config: { label: 'A' } });
  await assert.rejects(host.invoke(target()), { code: 'PERMISSION_DENIED' });
  assert.equal(storage.calls.length, 0);
  await host.update('a', { permissions: { storage: ['read', 'write'] } });
  await host.install({ installationId: 'b', enabled: true, source, config: { label: 'B' },
    permissions: { storage: ['read', 'write'] } });
  assert.equal(await host.invoke(target('a', 'kw', { write: true })), 'A');
  assert.equal(await host.invoke(target('b', 'kw', { write: true })), 'B');
  assert.equal(await host.invoke(target('a', 'tx')), null);
  assert.equal(await host.invoke(target('b', 'tx')), null);
  await host.configure('a', { label: 'changed' });
  assert.equal(await host.invoke(target('a')), 'A');
  assert.equal(await host.invoke(target('b')), 'B');
});

test('cancelled storage receives fencing signal and no completion reaches the script', async t => {
  const entered = deferred(), release = deferred();
  let input, writes = 0;
  const host = hostFor(t, { storage: { async set(value) {
    input = value; entered.resolve(); await release.promise;
    if (!value.signal.aborted && value.isCurrent()) writes++;
  } } });
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['write'] },
    source: lxScript({ handler: `async (_request, {storage}) => { await storage.set('key','synthetic'); return 'done'; }` }) });
  const controller = new AbortController();
  const rejected = assert.rejects(host.invoke(target(), { signal: controller.signal }), { code: 'CANCELLED' });
  await entered.promise;
  controller.abort();
  await rejected;
  assert.equal(input.signal.aborted, true);
  assert.equal(input.isCurrent(), false);
  release.resolve(); await turn();
  assert.equal(writes, 0);
});

test('failed update preserves live revision/config/session; explicit rollback starts a fresh generation', async t => {
  const host = hostFor(t);
  const source = lxScript({ setup: 'let count = 0;', handler: 'async () => ({n:++count,label:meow.config.label})' });
  const initial = await host.install({ installationId: 'a', version: 'one', source, config: { label: 'first' }, enabled: true });
  assert.deepEqual(await host.invoke(target()), { n: 1, label: 'first' });
  await assert.rejects(host.update('a', {
    source: "setInterval(()=>{},1); throw new Error('candidate failed');", version: 'bad', config: { label: 'bad' },
  }), { code: 'SCRIPT_EXCEPTION' });
  const unchanged = host.get('a');
  assert.equal(unchanged.generation, initial.generation);
  assert.equal(unchanged.contentHash, initial.contentHash);
  assert.equal(unchanged.version, 'one');
  assert.deepEqual(await host.invoke(target()), { n: 2, label: 'first' });
  assert.equal(host.stats().timers, 0);
  const updated = await host.update('a', { source, version: 'two', config: { label: 'second' } });
  assert(updated.generation > initial.generation + 1);
  assert.deepEqual(await host.invoke(target()), { n: 1, label: 'second' });
  await assert.rejects(host.invoke({ ...target(), generation: initial.generation }), { code: 'STALE_SESSION' });
  const restored = await host.rollback('a');
  assert(restored.generation > updated.generation);
  assert.equal(restored.version, 'one');
  assert.deepEqual(await host.invoke(target()), { n: 1, label: 'first' });
});

test('successful update invalidates pending old response, but never invokes another script', async t => {
  const entered = deferred(), late = deferred();
  const host = hostFor(t, { network: () => { entered.resolve(); return late.promise; } });
  await host.install({ installationId: 'a', enabled: true,
    permissions: {},
    source: lxScript({ handler: `async () => new Promise((resolve,reject)=>
      lx.request('https://fixture.invalid/x',{},(e,r,b)=>e?reject(e):resolve(b)))` }) });
  const rejected = assert.rejects(host.invoke(target()), { code: 'STALE_SESSION' });
  await entered.promise;
  await host.update('a', { source: lxScript({ handler: "async () => 'new'" }) });
  await rejected;
  late.resolve({ statusCode: 200, body: 'old' }); await turn();
  assert.equal(await host.invoke(target()), 'new');
});

test('disable/uninstall clean background handles, reinstall does not reuse a generation', async t => {
  const host = hostFor(t);
  const source = lxScript({ setup: 'setInterval(()=>{},10000);' });
  const first = await host.install({ installationId: 'a', source, enabled: true });
  assert.equal(host.stats().timers, 1);
  await host.disable('a');
  assert.equal(host.stats().timers, 0);
  assert.equal(host.stats().liveSessions, 0);
  const second = await host.enable('a');
  assert(second.generation > first.generation);
  await host.uninstall('a');
  assert.equal(host.list().length, 0);
  const third = await host.install({ installationId: 'a', source, enabled: true });
  assert(third.generation > second.generation);
  await assert.rejects(host.invoke({ ...target(), generation: second.generation }), { code: 'STALE_SESSION' });
});

test('global/instance/site request budgets reject only excess work and recover on cancellation', async t => {
  const host = hostFor(t, { limits: { maxSiteInFlight: 1, maxInstanceInFlight: 2, maxInFlight: 3 } });
  const source = lxScript({ handler: 'async () => new Promise(()=>{})' });
  await host.install({ installationId: 'a', source, enabled: true });
  await host.install({ installationId: 'b', source, enabled: true });
  const controller = new AbortController();
  const a = assert.rejects(host.invoke(target(), { signal: controller.signal }), { code: 'CANCELLED' });
  await assert.rejects(host.invoke(target()), { code: 'RESOURCE_LIMIT' });
  const a2 = assert.rejects(host.invoke(target('a', 'tx'), { signal: controller.signal }), { code: 'CANCELLED' });
  await assert.rejects(host.invoke(target('a', 'local')), { code: 'RESOURCE_LIMIT' });
  const b = assert.rejects(host.invoke(target('b'), { signal: controller.signal }), { code: 'CANCELLED' });
  await assert.rejects(host.invoke(target('b', 'tx')), { code: 'RESOURCE_LIMIT' });
  controller.abort(); await Promise.all([a, a2, b]);
  assert.equal(host.stats().activeRequests, 0);
});

test('byte utilities, offset, encoding, zlib, AES and LX RSA operate on real bytes', async t => {
  const host = hostFor(t);
  const key = generateKeyPairSync('rsa', { modulusLength: 1024 }).publicKey.export({ type: 'spki', format: 'pem' });
  const cipher = createCipheriv('aes-128-cbc', Buffer.from('0123456789abcdef'), Buffer.from('0123456789abcdef'));
  const aes = Buffer.concat([cipher.update('hello'), cipher.final()]).toString('hex');
  const padded = Buffer.alloc(128); padded.set([1, 2, 3], 125);
  const rsa = publicEncrypt({ key, padding: constants.RSA_NO_PADDING }, padded).toString('hex');
  await host.install({ installationId: 'a', enabled: true, source: lxScript({ handler: `async () => {
    const raw = Buffer.from([0,255,65,128]);
    const sliced = Buffer.from(raw.buffer,1,2);
    const compressed = await lx.utils.zlib.deflate(raw);
    return {
      md5:lx.utils.crypto.md5('abc'),
      slice:sliced.toString('hex'),
      latin:lx.utils.buffer.bufToString('\\xff\\x80','hex'),
      roundtrip:(await lx.utils.zlib.inflate(compressed)).equals(raw),
      b64:atob(btoa('ab')),
      utf8:new TextDecoder().decode(new TextEncoder().encode('中文🎵')),
      aes:lx.utils.crypto.aesEncrypt('hello','aes-128-cbc','0123456789abcdef','0123456789abcdef').toString('hex'),
      rsa:lx.utils.crypto.rsaEncrypt(Buffer.from([1,2,3]),${JSON.stringify(key)}).toString('hex'),
      random:lx.utils.crypto.randomBytes(17).length,
    };
  }` }) });
  assert.deepEqual(await host.invoke(target()), { md5: createHash('md5').update('abc').digest('hex'),
    slice: 'ff41', latin: 'ff80', roundtrip: true, b64: 'ab', utf8: '中文🎵', aes, rsa, random: 17 });
});

test('closing during pending initialization aborts startup and cannot publish a late session', async t => {
  const started = deferred();
  const host = hostFor(t, { network: () => { started.resolve(); return new Promise(() => {}); } });
  const installing = host.install({ installationId: 'a', enabled: true,
    permissions: {},
    source: `lx.request('https://fixture.invalid/init',{},()=>{});` });
  const rejected = assert.rejects(installing, { code: 'STALE_SESSION' });
  await started.promise;
  await host.close();
  await rejected;
  assert.equal(host.stats().liveSessions, 0);
  assert.throws(() => host.enable('a'), { code: 'HOST_CLOSED' });
});

test('raw LX header metadata is read without evaluating on import', async t => {
  const host = hostFor(t);
  const source = `/*\n * @name Synthetic raw source\n * @version 3.1.0\n * @author Fixture\n */\n` +
    lxScript({ handler: 'async () => ({name:lx.currentScriptInfo.name,version:lx.currentScriptInfo.version,author:lx.currentScriptInfo.author})' });
  const installed = await host.install({ installationId: 'a', source });
  assert.equal(installed.generation, 0);
  await host.enable('a');
  assert.deepEqual(await host.invoke(target()), { name: 'Synthetic raw source', version: '3.1.0', author: 'Fixture' });
});

test('uncooperative adapter tasks stay visible after bounded close, then retire when settled', async t => {
  const entered = deferred(), finish = deferred();
  const host = hostFor(t, { limits: { cleanupTimeoutMs: 10 },
    network: () => { entered.resolve(); return finish.promise; } });
  await host.install({ installationId: 'a', enabled: true,
    permissions: {},
    source: lxScript({ handler: `async () => new Promise((resolve,reject)=>
      lx.request('https://fixture.invalid/pending',{},(e,r,b)=>e?reject(e):resolve(b)))` }) });
  const rejected = assert.rejects(host.invoke(target()), { code: 'STALE_SESSION' });
  await entered.promise;
  const closing = host.close();
  const closed = await closing;
  await rejected;
  assert.equal(closed.liveSessions, 0);
  assert.equal(closed.networkRequests, 0);
  assert.equal(closed.pendingAdapterTasks, 1);
  assert.equal(closed.cleanupComplete, false);
  finish.resolve({ statusCode: 200, body: 'late' });
  await turn();
  assert.equal(host.stats().pendingAdapterTasks, 0);
  assert.equal(host.close(), closing);
  assert.deepEqual(await host.close(), closed);
});

test('timer/network handle budgets fail explicitly and release request-owned handles', async t => {
  const host = hostFor(t, { limits: { maxTimers: 1, maxNetworkRequests: 1 } });
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async ({info}) => {
      if(info.musicInfo.timer) {
        setInterval(()=>{},10000);setInterval(()=>{},10000);
      } else {
        lx.request('https://fixture.invalid/1',{},()=>{});
        lx.request('https://fixture.invalid/2',{},()=>{});
      }
    }`,
  }) });
  await assert.rejects(host.invoke(target('a', 'kw', { timer: true })), { code: 'RESOURCE_LIMIT' });
  assert.equal(host.stats().timers, 0);
  await assert.rejects(host.invoke(target()), { code: 'RESOURCE_LIMIT' });
  assert.equal(host.stats().networkRequests, 0);
});

test('simultaneous storage calls keep their original site after awaits', async t => {
  const release = deferred(), bothEntered = deferred();
  const seen = [];
  const host = hostFor(t, { storage: { async set(input) {
    seen.push(input); if (seen.length === 2) bothEntered.resolve();
    await release.promise;
  }, get(input) { return input.siteId; } } });
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read', 'write'] },
    source: lxScript({ handler: `async (_request, {storage}) => {
      await storage.set('key','value'); return await storage.get('key');
    }` }) });
  const a = host.invoke(target('a', 'kw'));
  const b = host.invoke(target('a', 'tx'));
  await bothEntered.promise;
  assert.deepEqual(seen.map(v => v.siteId), ['kw', 'tx']);
  assert.notEqual(seen[0].requestId, seen[1].requestId);
  release.resolve();
  assert.deepEqual(await Promise.all([a, b]), ['kw', 'tx']);
});

test('trial startup cannot write persistent storage; failed trial leaves stable stored data untouched', async t => {
  const storage = memoryStorage();
  const host = hostFor(t, { storage: storage.adapter });
  await host.install({ installationId: 'a', enabled: true, permissions: { storage: ['read', 'write'] },
    source: lxScript({ handler: "async (_request, {storage}) => { await storage.set('key','stable'); return 'ok'; }" }) });
  await host.invoke(target());
  const old = host.get('a');
  await assert.rejects(host.update('a', {
    source: `(async()=>{await meow.storage.set('key','candidate');})()`,
  }), { code: 'PERMISSION_DENIED' });
  assert.equal(storage.values.get(JSON.stringify(['a', 'kw', 'key'])), 'stable');
  assert.equal(host.get('a').generation, old.generation);
  assert.equal(host.get('a').state, 'ready');
});

test('manual script failures never silently fall back to another installation', async t => {
  const host = hostFor(t);
  await host.install({ installationId: 'a', enabled: true,
    source: lxScript({ handler: "async () => { throw Error('synthetic failure'); }" }) });
  await host.install({ installationId: 'b', enabled: true,
    source: lxScript({ setup: 'let calls = 0;', handler: 'async () => ++calls' }) });
  await assert.rejects(host.invoke(target('a')), { code: 'SCRIPT_EXCEPTION' });
  assert.equal(await host.invoke(target('b')), 1);
});

test('raw script errors cannot forge a trusted host permission error', async t => {
  const host = hostFor(t);
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: "async () => { const e = Error('synthetic'); e.code = 'PERMISSION_DENIED'; throw e; }",
  }) });
  await assert.rejects(host.invoke(target()), { code: 'SCRIPT_EXCEPTION' });
});

test('repeat upgrade/uninstall cycles do not retain owned resources', async t => {
  const host = hostFor(t);
  let lastGeneration = 0;
  const source = lxScript({ setup: 'setInterval(()=>{},10000);' });
  for (let i = 0; i < 8; i++) {
    const initial = await host.install({ installationId: 'a', source, enabled: true, config: { i } });
    assert(initial.generation > lastGeneration);
    const upgraded = await host.update('a', { version: `0.${i}.0` });
    assert(upgraded.generation > initial.generation);
    lastGeneration = upgraded.generation;
    assert.equal(host.stats().timers, 1);
    await host.uninstall('a');
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().timers, 0);
    assert.equal(host.stats().activeRequests, 0);
    assert.equal(host.stats().pendingAdapterTasks, 0);
  }
});
