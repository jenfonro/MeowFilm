import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createScriptHost, ScriptHostError, SCRIPT_LIMITS } from '@meowfilm/script-host';
import { encodeJSONValue, decodeJSONValue } from '@meowfilm/runtime-bridge';
import { createMusicScriptModule, musicScriptContract, musicPlatforms } from '../apps/runtime/modules/music-script.mjs';

const limit = 512 * 1024;
const hash = value => createHash('sha256').update(value).digest('hex');
const declared = {
  kw: { name: '同名', type: 'music', actions: ['musicUrl', 'lyric'], qualitys: ['320k', 'flac'] },
  tx: { name: '同名', type: 'music', actions: ['musicUrl'], qualitys: ['320k'] },
  local: { name: '本地', type: 'music', actions: ['musicUrl', 'lyric', 'pic'], qualitys: [] },
};
const mediaHandler = `async ({source,action,info}) => {
  if (action === 'lyric') return '[00:01.00]' + source;
  counts[source] = (counts[source] || 0) + 1;
  return 'https://media.invalid/' + source + '/' + info.type + '/' + (info.musicInfo.songmid || 'x') + '?n=' + counts[source];
}`;
function fixture({ setup = '', handler = mediaHandler, sources = declared, end = '', name = 'Synthetic LX fixture' } = {}) {
  return `// @name ${name}\n// @version 2.1\nlet counts = {}; ${setup}
lx.on(lx.EVENT_NAMES.request, ${handler});
lx.send(lx.EVENT_NAMES.inited, {sources:${JSON.stringify(sources)}});
${end}`;
}
function okay(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, musicScriptContract);
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
function root(t) {
  const path = mkdtempSync(join(tmpdir(), 'music-script-'));
  t.after(() => rmSync(path, { recursive: true, force: true }));
  return path;
}
function setup(t, extra = {}) {
  let host;
  const dataRoot = extra.dataRoot ?? root(t);
  const module = createMusicScriptModule({
    dataRoot,
    network: () => { assert.fail('No real or undeclared network is allowed by this fixture'); },
    download: () => { assert.fail('No script download is declared by this fixture'); },
    hostFactory: input => { host = createScriptHost(input); return host; },
    ...extra,
  });
  t.after(async () => {
    okay(await module.close());
    if (host) {
      const stats = host.stats();
      for (const key of ['liveSessions', 'activeRequests', 'timers', 'networkRequests',
        'pendingAdapterTasks']) assert.equal(stats[key], 0, key);
    }
  });
  const direct = (name, params = {}, context) => module.methods[`music.script.${name}`](params, context);
  async function rpc(name, params = {}, context) {
    let reply;
    const bytes = encodeJSONValue(params);
    let token;
    try {
      if (!name.startsWith('stage.') && bytes.length > 65536) {
        const begin = okay(await direct('control.begin', { length: bytes.length, sha256: hash(bytes) }, context));
        token = begin.token;
        for (let offset = 0; offset < bytes.length; offset += begin.chunkBytes)
          okay(await direct('control.chunk', { token, offset,
            data: bytes.subarray(offset, offset + begin.chunkBytes).toString('base64') }, context));
        reply = await direct('control.call', { operation: name, token }, context);
      } else reply = await direct(name, params, context);
    } finally {
      bytes.fill(0);
      if (token) okay(await direct('control.abort', { token }));
    }
    if (!reply.ok || !reply.blob) return reply;
    const blob = reply.blob, chunks = [];
    try {
      for (let offset = 0; offset < blob.length;) {
        const part = okay(await direct('blob.read', { token: blob.token, offset }, context));
        const bytes = Buffer.from(part.data, 'base64');
        assert.equal(part.next, offset + bytes.length);
        chunks.push(bytes); offset = part.next;
      }
      const bytes = Buffer.concat(chunks);
      assert.equal(bytes.length, blob.length); assert.equal(hash(bytes), blob.sha256);
      const restored = decodeJSONValue(bytes);
      assert.equal(restored.contract, musicScriptContract);
      return restored;
    } finally { okay(await direct('blob.release', { token: blob.token })); }
  }
  return { module, host: () => host, rpc, dataRoot };
}
async function stage(rpc, source = fixture(), basename = 'fixture.js') {
  const bytes = Buffer.isBuffer(source) ? source : Buffer.from(source);
  const begin = okay(await rpc('stage.begin', { basename, length: bytes.length, sha256: hash(bytes) }));
  assert.equal(begin.chunkBytes, 65536);
  for (let offset = 0; offset < bytes.length; offset += begin.chunkBytes) {
    const part = bytes.subarray(offset, offset + begin.chunkBytes);
    okay(await rpc('stage.chunk', { token: begin.token, offset, data: part.toString('base64') }));
  }
  return begin.token;
}
async function imported(rpc, source = fixture(), basename) {
  return okay(await rpc('sources.add', { token: await stage(rpc, source, basename) })).item;
}
async function enabled(rpc, source = fixture()) {
  const item = await imported(rpc, source);
  return okay(await rpc('sources.enable', { installationId: item.installationId })).item;
}
const resolve = (rpc, item, platform = 'kw', musicInfo = { songmid: '1' }, quality = '320k', context) =>
  rpc('resolve', { installationId: item.installationId, platform, quality, musicInfo }, context);
async function value(rpc, item, siteId = 'kw', musicInfo = {}, operation = 'musicUrl', context) {
  return okay(await rpc('invoke', { installationId: item.installationId, siteId, operation,
    payload: { type: '320k', musicInfo } }, context)).value;
}
const storageFixture = fixture({ handler: `async ({info}, {storage}) => {
 const m=info.musicInfo;
 if(m.op==='set') return await storage.set(m.key||'key',m.value);
 if(m.op==='delete') return await storage.delete(m.key||'key');
 return await storage.get(m.key||'key');
}` });
const networkFixture = fixture({ handler: `async () => new Promise((resolve,reject) =>
 lx.request('https://fixture.invalid/value',{},(error,response,body)=>error?reject(error):resolve(body)))` });

test('descriptor exposes the v3 management, playback and transfer RPCs only', async t => {
  const { module, rpc } = setup(t);
  assert.equal(module.id, 'music'); assert.equal(module.version, '0.8.0');
  assert.deepEqual(musicPlatforms, ['wy', 'tx', 'kw', 'kg', 'mg']);
  assert.deepEqual(Object.keys(module.methods).sort(), [
    'stage.begin', 'stage.chunk', 'stage.abort', 'control.begin', 'control.chunk', 'control.abort',
    'control.call', 'blob.read', 'blob.release', 'sources.list', 'sources.detail', 'sources.add',
    'sources.download', 'sources.edit', 'sources.update', 'sources.rollback', 'sources.enable',
    'sources.disable', 'sources.remove', 'sources.check', 'resolve', 'invoke', 'close',
  ].map(name => `music.script.${name}`).sort());
  assert.deepEqual(okay(await rpc('sources.list')), { ok: true, contract: musicScriptContract, revision: 0, items: [] });
  assert.throws(() => createMusicScriptModule(), { code: 'INVALID_REQUEST' });
  assert.throws(() => createMusicScriptModule({ dataRoot: 'relative' }), { code: 'INVALID_REQUEST' });
  okay(await module.close()); okay(await rpc('close')); okay(await module.close());
  denied(await rpc('sources.list'), 'CLOSED');
});

test('import persists installation facts without executing; reopen restores the same catalog', async t => {
  const dataRoot = root(t);
  const first = setup(t, { dataRoot });
  const source = `/*\n * @name 猫音源\n * @version 3.2.1\n * @author 作者\n * @description 说明\n * @homepage https://example.invalid\n */\nthrow Error('never executed');`;
  const item = await imported(first.rpc, source);
  assert.match(item.installationId, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  assert.deepEqual({ ...item, installationId: undefined, executionHash: undefined }, {
    installationId: undefined, name: '猫音源', version: '3.2.1', author: '作者', description: '说明',
    homepage: 'https://example.invalid', origin: { kind: 'file' }, enabled: false, state: 'disabled',
    generation: 0, contentHash: hash(source), executionHash: undefined, rollbackAvailable: false,
    sites: null, check: null, lastFailure: null,
  });
  assert.equal(first.host().stats().liveSessions, 0);
  const files = readdirSync(join(dataRoot, 'music', 'sources'));
  assert.deepEqual(files, [`${hash(source)}.js`]);
  const nameless = await imported(first.rpc, 'lx.send("x")', 'file-name.js');
  assert.equal(nameless.name, 'file-name'); assert.equal(nameless.version, '1');
  okay(await first.module.close());
  const second = setup(t, { dataRoot });
  const listed = okay(await second.rpc('sources.list'));
  assert.equal(listed.revision, 2);
  assert.deepEqual(listed.items.map(entry => entry.installationId), [item.installationId, nameless.installationId]);
  assert.deepEqual(listed.items[0], item);
});

test('a corrupt catalog is reported and never reset or overwritten', async t => {
  const dataRoot = root(t);
  const first = setup(t, { dataRoot });
  await imported(first.rpc);
  okay(await first.module.close());
  const path = join(dataRoot, 'music', 'catalog.json');
  for (const bytes of ['{', JSON.stringify({ schema: 2, revision: 0, installations: [] }),
    JSON.stringify({ schema: 1, revision: 0, installations: [{ id: 'not-a-uuid' }] })]) {
    writeFileSync(path, bytes);
    const reopened = setup(t, { dataRoot });
    denied(await reopened.rpc('sources.list'), 'CORRUPT_STORAGE');
    denied(await reopened.rpc('sources.add', { token: await stage(reopened.rpc) }), 'CORRUPT_STORAGE');
    assert.equal(readFileSync(path, 'utf8'), bytes);
    okay(await reopened.module.close());
  }
});

test('import accepts a leading BOM and rejects malformed or empty UTF-8 before storing', async t => {
  const { rpc, dataRoot } = setup(t);
  assert.equal((await imported(rpc, '\uFEFF// @name 带 BOM\nlx.send("x")')).name, '带 BOM');
  for (const bytes of [[0xff, 0xfe], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xe2, 0x82], [0xf4, 0x90, 0x80, 0x80],
    [0x20, 0x0a]]) {
    denied(await rpc('sources.add', { token: await stage(rpc, Buffer.from(bytes)) }), 'SCRIPT_ENCODING');
  }
  assert.equal(readdirSync(join(dataRoot, 'music', 'sources')).length, 1);
});

test('enable loads and declares music sites; a failing script stays disabled and unloaded', async t => {
  const { rpc, host } = setup(t);
  const item = await enabled(rpc);
  assert.equal(item.state, 'ready'); assert.ok(item.generation > 0);
  assert.deepEqual(item.sites, {
    kw: { actions: ['musicUrl', 'lyric'], qualitys: ['320k', 'flac'] }, tx: { actions: ['musicUrl'], qualitys: ['320k'] },
  });
  const disabled = okay(await rpc('sources.disable', { installationId: item.installationId })).item;
  assert.equal(disabled.state, 'disabled'); assert.equal(disabled.generation, 0);
  assert.deepEqual(disabled.sites, item.sites);
  assert.equal(host().stats().liveSessions, 0);
  denied(await resolve(rpc, item), 'SOURCE_DISABLED');
  const again = okay(await rpc('sources.enable', { installationId: item.installationId })).item;
  assert.ok(again.generation > item.generation);
  const bad = await imported(rpc, `// @name bad\nthrow Error('synthetic-private-startup');`);
  denied(await rpc('sources.enable', { installationId: bad.installationId }), 'SCRIPT_FAILURE');
  const listed = okay(await rpc('sources.list')).items.find(entry => entry.installationId === bad.installationId);
  assert.equal(listed.enabled, false); assert.equal(listed.state, 'disabled');
  assert.equal(host().list().some(record => record.installationId === bad.installationId), false);
  denied(await rpc('sources.enable', { installationId: '00000000-0000-4000-8000-000000000000' }), 'SOURCE_NOT_FOUND');
});

test('resolve directs one installation and platform and returns the normalized media contract', async t => {
  const { rpc } = setup(t);
  const a = await enabled(rpc), b = await enabled(rpc);
  assert.equal(a.contentHash, b.contentHash);
  const replies = await Promise.all([resolve(rpc, a), resolve(rpc, b), resolve(rpc, a, 'tx'), resolve(rpc, a, 'kw', { songmid: '9' }, 'flac')]);
  assert.deepEqual(replies.map(reply => okay(reply).url), [
    'https://media.invalid/kw/320k/1?n=1', 'https://media.invalid/kw/320k/1?n=1',
    'https://media.invalid/tx/320k/1?n=1', 'https://media.invalid/kw/flac/9?n=2',
  ]);
  assert.deepEqual(replies[0], { ok: true, contract: musicScriptContract, installationId: a.installationId,
    generation: a.generation, contentHash: a.contentHash, platform: 'kw', quality: '320k',
    url: 'https://media.invalid/kw/320k/1?n=1', headers: {}, expiresAt: null });
  denied(await resolve(rpc, a, 'kw', {}, 'flac24bit'), 'CAPABILITY_UNAVAILABLE');
  denied(await resolve(rpc, a, 'wy'), 'CAPABILITY_UNAVAILABLE');
  denied(await resolve(rpc, a, 'local'), 'INVALID_REQUEST');
  assert.equal(await value(rpc, a, 'kw', {}, 'lyric'), '[00:01.00]kw');
  denied(await rpc('invoke', { installationId: a.installationId, siteId: 'tx', operation: 'lyric', payload: {} }),
    'CAPABILITY_UNAVAILABLE');
});

test('musicUrl results are checked once for playback: empty, malformed, restricted, expired and headers', async t => {
  const { rpc } = setup(t);
  const item = await enabled(rpc, fixture({ handler: `async ({info}) => ({
    empty: '', none: null, relative: '/a.mp3', file: 'file:///a.mp3', credentials: 'https://u:p@media.invalid/a',
    space: 'https://media.invalid/a b', trial: { url: 'https://media.invalid/a', freeTrialInfo: { start: 0 } },
    drm: { url: 'https://media.invalid/a', drm: true }, expired: { url: 'https://media.invalid/a', expiresAt: 1000 },
    future: { url: 'https://media.invalid/a', expiresAt: 4102444800000, headers: { Referer: 'https://ref.invalid' } },
    range: { url: 'https://media.invalid/a', headers: { Range: 'bytes=0-' } },
    proxy: { url: 'https://media.invalid/a', headers: { 'Proxy-Authorization': 'x' } },
    changed: { url: 'https://media.invalid/a', siteKey: 'tx' },
    interval: 'https://media.invalid/' + info.musicInfo.interval,
    timed: { url: 'https://media.invalid/timed', duration: 180 },
  })[info.musicInfo.case]` }));
  const media = musicInfo => resolve(rpc, item, 'kw', musicInfo);
  denied(await media({ case: 'empty' }), 'MEDIA_UNAVAILABLE');
  denied(await media({ case: 'none' }), 'MEDIA_UNAVAILABLE');
  for (const name of ['relative', 'file', 'credentials', 'space', 'trial', 'drm'])
    denied(await media({ case: name }), 'INVALID_MEDIA_CANDIDATE');
  denied(await media({ case: 'expired' }), 'EXPIRED_MEDIA_CANDIDATE');
  denied(await media({ case: 'range' }), 'MEDIA_HEADERS_UNSUPPORTED');
  denied(await media({ case: 'proxy' }), 'MEDIA_HEADERS_UNSUPPORTED');
  denied(await media({ case: 'changed' }), 'TARGET_CHANGED');
  denied(await media({ case: 'timed', duration: 120 }), 'RECORDING_MISMATCH');
  okay(await media({ case: 'timed', duration: 183 }));
  for (const duration of [170, 190]) okay(await media({ case: 'timed', duration }));
  for (const duration of [169.99, 190.01]) denied(await media({ case: 'timed', duration }), 'RECORDING_MISMATCH');
  okay(await media({ case: 'timed', duration: 0 }));
  const future = okay(await media({ case: 'future' }));
  assert.equal(future.expiresAt, 4102444800000);
  assert.deepEqual(future.headers, { Referer: 'https://ref.invalid' });
  assert.equal(okay(await media({ case: 'interval', duration: 245.9 })).url, 'https://media.invalid/04:05');
});

test('a failed directed invocation never falls through to another installation', async t => {
  const { rpc } = setup(t);
  const bad = await enabled(rpc, fixture({ handler: `async () => { throw Error('private'); }` }));
  const good = await enabled(rpc);
  denied(await resolve(rpc, bad), 'SCRIPT_FAILURE');
  assert.equal(okay(await resolve(rpc, good)).url, 'https://media.invalid/kw/320k/1?n=1');
});

test('file updates keep the previous version, a failing trial keeps the exact live generation, rollback swaps', async t => {
  const { rpc, dataRoot } = setup(t);
  const item = await enabled(rpc);
  const second = fixture({ name: 'Second', handler: `async () => 'https://media.invalid/second'` });
  denied(await rpc('sources.update', { installationId: item.installationId }), 'INVALID_REQUEST');
  const updated = okay(await rpc('sources.update', { installationId: item.installationId,
    token: await stage(rpc, second) })).item;
  assert.equal(updated.name, item.name);
  assert.equal(updated.contentHash, hash(second)); assert.equal(updated.rollbackAvailable, true);
  assert.ok(updated.generation > item.generation);
  assert.equal(okay(await resolve(rpc, updated)).url, 'https://media.invalid/second');
  const broken = `// @name broken\nthrow Error('trial failure');`;
  denied(await rpc('sources.update', { installationId: item.installationId, token: await stage(rpc, broken) }),
    'SCRIPT_FAILURE');
  const kept = okay(await rpc('sources.detail', { installationId: item.installationId })).item;
  assert.equal(kept.contentHash, hash(second)); assert.equal(kept.generation, updated.generation);
  assert.equal(okay(await resolve(rpc, kept)).url, 'https://media.invalid/second');
  assert.deepEqual(readdirSync(join(dataRoot, 'music', 'sources')).sort(),
    [`${hash(fixture())}.js`, `${hash(second)}.js`].sort());
  const rolled = okay(await rpc('sources.rollback', { installationId: item.installationId })).item;
  assert.equal(rolled.contentHash, item.contentHash); assert.equal(rolled.rollbackAvailable, true);
  assert.equal(okay(await resolve(rpc, rolled)).url.startsWith('https://media.invalid/kw/'), true);
  const disabled = await imported(rpc);
  denied(await rpc('sources.rollback', { installationId: disabled.installationId }), 'ROLLBACK_UNAVAILABLE');
});

test('network sources download over HTTPS without redirects; update re-downloads the saved address', async t => {
  let body = Buffer.from(fixture({ name: 'Net' })), status = 200;
  const requests = [];
  const { rpc } = setup(t, { download: async input => {
    requests.push(input);
    assert.equal(input.options.followRedirect, false);
    assert.ok(input.signal instanceof AbortSignal);
    input.validateURL(input.url);
    return { statusCode: status, body: Buffer.from(body) };
  } });
  for (const url of ['http://host.invalid/a.js', 'https://host.invalid/a.txt', 'https://u:p@host.invalid/a.js',
    'https://host.invalid/a.js#x', ' ', 'https://host.invalid/a b.js'])
    denied(await rpc('sources.download', { url }), 'INVALID_SOURCE_URL');
  assert.equal(requests.length, 0);
  const item = okay(await rpc('sources.download', { url: 'https://host.invalid/path/net.js?token=secret' })).item;
  assert.deepEqual(item.origin, { kind: 'network', host: 'host.invalid' });
  assert.equal(JSON.stringify(okay(await rpc('sources.list'))).includes('secret'), false);
  assert.deepEqual(okay(await rpc('sources.detail', { installationId: item.installationId })).item.origin,
    { kind: 'network', host: 'host.invalid', url: 'https://host.invalid/path/net.js?token=secret' });
  status = 302;
  denied(await rpc('sources.update', { installationId: item.installationId }), 'SOURCE_REDIRECT');
  status = 404;
  denied(await rpc('sources.update', { installationId: item.installationId }), 'SOURCE_DOWNLOAD_FAILED');
  status = 200; body = Buffer.alloc(0);
  denied(await rpc('sources.update', { installationId: item.installationId }), 'SOURCE_DOWNLOAD_FAILED');
  body = Buffer.from(fixture({ name: 'Net 2' }));
  denied(await rpc('sources.update', { installationId: item.installationId, token: 'x' }), 'INVALID_REQUEST');
  const updated = okay(await rpc('sources.update', { installationId: item.installationId })).item;
  assert.equal(updated.contentHash, hash(body)); assert.equal(updated.rollbackAvailable, true);
  assert.equal(requests.at(-1).url, 'https://host.invalid/path/net.js?token=secret');
  const edited = okay(await rpc('sources.edit', { installationId: item.installationId, name: ' 改名 ',
    url: 'https://other.invalid/next.js' })).item;
  assert.equal(edited.name, '改名'); assert.equal(edited.origin.url, 'https://other.invalid/next.js');
  okay(await rpc('sources.update', { installationId: item.installationId }));
  assert.equal(requests.at(-1).url, 'https://other.invalid/next.js');
  const file = await imported(rpc);
  denied(await rpc('sources.edit', { installationId: file.installationId, url: 'https://other.invalid/x.js' }),
    'INVALID_REQUEST');
  for (const name of ['', '   ', 'a\u0000b', 'x'.repeat(256)])
    denied(await rpc('sources.edit', { installationId: file.installationId, name }), 'INVALID_REQUEST');
  denied(await rpc('sources.edit', { installationId: file.installationId }), 'INVALID_REQUEST');
});

test('check runs one sample per declared platform without changing enablement and persists derived facts', async t => {
  const dataRoot = root(t);
  const { rpc, host, module } = setup(t, { dataRoot });
  const sources = {
    kw: { name: 'kw', type: 'music', actions: ['musicUrl'], qualitys: ['flac', '128k'] },
    tx: { name: 'tx', type: 'music', actions: ['musicUrl'], qualitys: ['320k'] },
    mg: { name: 'mg', type: 'music', actions: ['lyric'], qualitys: ['128k'] },
  };
  const calls = [];
  const item = await imported(rpc, fixture({ sources, handler: `async ({source,info}) => {
    if (source === 'tx') throw Error('synthetic tx failure');
    return 'https://media.invalid/' + source + '/' + info.type + '/' + info.musicInfo.songmid;
  }` }));
  const checked = okay(await rpc('sources.check', { installationId: item.installationId })).item;
  assert.equal(checked.enabled, false); assert.equal(checked.state, 'disabled');
  assert.equal(host().list().length, 0);
  assert.equal(checked.check.summary, 'partial'); assert.equal(checked.check.failure, null);
  assert.deepEqual(checked.check.sites, {
    wy: { state: 'unsupported', quality: null, sample: null, code: null },
    tx: { state: 'failed', quality: '320k', sample: 'tx:0039MnYb0qxYhV', code: 'SCRIPT_FAILURE' },
    kw: { state: 'ok', quality: '128k', sample: 'kw:838607', code: null },
    kg: { state: 'unsupported', quality: null, sample: null, code: null },
    mg: { state: 'unsupported', quality: null, sample: null, code: null },
  });
  assert.deepEqual(Object.keys(checked.sites), ['tx', 'kw', 'mg']);
  assert.equal(JSON.stringify(readFileSync(join(dataRoot, 'music', 'catalog.json'), 'utf8')).includes('media.invalid'),
    false);
  okay(await module.close());
  const reopened = setup(t, { dataRoot });
  const restored = okay(await reopened.rpc('sources.list')).items[0];
  assert.deepEqual(restored.check, checked.check);
  const updated = okay(await reopened.rpc('sources.update', { installationId: item.installationId,
    token: await stage(reopened.rpc, fixture({ name: 'changed' })) })).item;
  assert.equal(updated.check, null);
  const failing = await imported(reopened.rpc, `throw Error('startup');`);
  const abnormal = okay(await reopened.rpc('sources.check', { installationId: failing.installationId })).item;
  assert.deepEqual({ failure: abnormal.check.failure, summary: abnormal.check.summary, sites: abnormal.check.sites },
    { failure: 'SCRIPT_FAILURE', summary: 'abnormal', sites: {} });
  assert.equal(abnormal.enabled, false);
  void calls;
});

test('check of an enabled source keeps it live and never cancels a concurrent playback request', async t => {
  const entered = deferred(), release = deferred();
  const { rpc } = setup(t, { network: async () => {
    entered.resolve(); await release.promise; return { statusCode: 200, body: 'https://media.invalid/net' };
  } });
  const item = await enabled(rpc, fixture({ handler: `async ({source,info}) => info.musicInfo.block
    ? new Promise((resolve,reject) => lx.request('https://fixture.invalid/v',{},(e,r,b)=>e?reject(e):resolve(b)))
    : 'https://media.invalid/' + source` }));
  const playing = resolve(rpc, item, 'kw', { block: true });
  await entered.promise;
  const checked = okay(await rpc('sources.check', { installationId: item.installationId })).item;
  assert.equal(checked.check.summary, 'normal');
  assert.equal(checked.state, 'ready'); assert.equal(checked.generation, item.generation);
  release.resolve();
  assert.equal(okay(await playing).url, 'https://media.invalid/net');
});

test('guest storage is request bound, isolated by installation and site, and removed with the installation', async t => {
  const { rpc, dataRoot } = setup(t);
  const a = await enabled(rpc, storageFixture), b = await enabled(rpc, storageFixture);
  assert.equal(await value(rpc, a, 'kw', { op: 'set', value: { nested: ['猫'] } }), null);
  assert.deepEqual(await value(rpc, a, 'kw', { op: 'get' }), { nested: ['猫'] });
  assert.equal(await value(rpc, a, 'tx', { op: 'get' }), null);
  assert.equal(await value(rpc, b, 'kw', { op: 'get' }), null);
  denied(await rpc('invoke', { installationId: a.installationId, siteId: 'kw', operation: 'musicUrl',
    payload: { type: '320k', musicInfo: { op: 'set', key: 'big', value: 'x'.repeat(16385) } } }), 'LIMIT');
  assert.equal(await value(rpc, a, 'kw', { op: 'delete' }), null);
  assert.equal(await value(rpc, a, 'kw', { op: 'get' }), null);
  assert.equal(await value(rpc, a, 'kw', { op: 'set', value: 1 }), null);
  const data = join(dataRoot, 'music', 'data', `${a.installationId}.json`);
  assert.deepEqual(JSON.parse(readFileSync(data, 'utf8')), { schema: 1, sites: { kw: [{ key: 'key', value: 1 }] } });
  assert.equal(JSON.stringify(okay(await rpc('sources.list'))).includes('"key"'), false);
  assert.deepEqual(okay(await rpc('sources.remove', { installationId: a.installationId })), {
    ok: true, contract: musicScriptContract, removed: true });
  assert.equal(existsSync(data), false);
  assert.equal(readdirSync(join(dataRoot, 'music', 'sources')).length, 1);
  okay(await rpc('sources.remove', { installationId: b.installationId }));
  assert.deepEqual(readdirSync(join(dataRoot, 'music', 'sources')), []);
  denied(await rpc('sources.detail', { installationId: a.installationId }), 'SOURCE_NOT_FOUND');
});

test('stage lifecycle has no whole-source byte or active-token quota', async t => {
  let now = 0;
  const { rpc } = setup(t, { clock: () => now });
  const begin = length => rpc('stage.begin', { basename: 'fixture.js', length, sha256: hash('x') });
  denied(await begin(0), 'INVALID_REQUEST');
  const large = SCRIPT_LIMITS.maxSourceBytes - 12;
  const tokens = [];
  for (let index = 0; index < 12; index++)
    tokens.push(okay(await begin(large + index)).token);
  assert.equal(new Set(tokens).size, tokens.length);
  okay(await rpc('stage.abort', { token: tokens[0] }));
  okay(await rpc('stage.abort', { token: tokens[0] }));
  now = 300000;
  for (const token of tokens.slice(1))
    denied(await rpc('stage.chunk', { token, offset: 0, data: 'YQ==' }), 'STAGE_NOT_FOUND');
  okay(await begin(SCRIPT_LIMITS.maxSourceBytes));
  denied(await begin(SCRIPT_LIMITS.maxSourceBytes + 1), 'LIMIT');
});

test('stage expiry is active, renewed by chunks and ignores stale timer callbacks', async t => {
  let now = 0;
  const timers = [];
  const { rpc } = setup(t, {
    clock: () => now,
    setTimer(callback, delay) {
      const timer = { callback, delay, cleared: false, unref() {} };
      timers.push(timer);
      return timer;
    },
    clearTimer(timer) { timer.cleared = true; },
  });
  const first = okay(await rpc('stage.begin', { basename: 'first.js', length: 2, sha256: hash('ab') })).token;
  const stale = timers.at(-1);
  now = 100;
  okay(await rpc('stage.chunk', { token: first, offset: 0, data: 'YQ==' }));
  assert.equal(stale.cleared, true);
  now = 300000;
  stale.callback();
  okay(await rpc('stage.chunk', { token: first, offset: 1, data: 'Yg==' }));
  const abandoned = okay(await rpc('stage.begin', { basename: 'abandoned.js', length: 1, sha256: hash('x') })).token;
  const active = timers.at(-1);
  now = 600000;
  active.callback();
  denied(await rpc('stage.chunk', { token: first, offset: 2, data: 'Yw==' }), 'STAGE_NOT_FOUND');
  denied(await rpc('stage.chunk', { token: abandoned, offset: 0, data: 'eA==' }), 'STAGE_NOT_FOUND');
});

test('stage shutdown neutralizes a timer whose cleanup permanently fails', async t => {
  let timer, clearAttempts = 0;
  const module = createMusicScriptModule({
    dataRoot: root(t),
    setTimer(callback) { timer = { callback, unref() {} }; return timer; },
    clearTimer() { clearAttempts++; throw new Error('synthetic timer cleanup failure'); },
  });
  okay(await module.methods['music.script.stage.begin']({ basename: 'cleanup.js', length: 1, sha256: hash('x') }));
  denied(await module.close(), 'SCRIPT_CLEANUP');
  assert.equal(clearAttempts, 1);
  assert.doesNotThrow(() => timer.callback());
  denied(await module.close(), 'SCRIPT_CLEANUP');
});

test('stage validates names, sequence, canonical base64, chunk size and hash', async t => {
  const { rpc } = setup(t);
  for (const basename of ['../bad.js', 'C:\\bad.js', 'bad.txt', '/bad.js']) {
    denied(await rpc('stage.begin', { basename, length: 1, sha256: hash('x') }), 'INVALID_REQUEST');
  }
  denied(await rpc('stage.begin', { basename: 'x.js', length: 1, sha256: 'wrong' }), 'INVALID_REQUEST');
  const token = okay(await rpc('stage.begin', { basename: 'x.js', length: 3, sha256: hash('abc') })).token;
  denied(await rpc('stage.chunk', { token, offset: 1, data: 'YQ==' }), 'SCRIPT_INTEGRITY');
  denied(await rpc('stage.chunk', { token, offset: 0, data: 'YWJj=' }), 'SCRIPT_INTEGRITY');
  denied(await rpc('stage.chunk', { token, offset: 0, data: 'A'.repeat(90000) }), 'LIMIT');
  denied(await rpc('sources.add', { token }), 'SCRIPT_INTEGRITY');
  okay(await rpc('stage.chunk', { token, offset: 0, data: 'YWJk' }));
  denied(await rpc('sources.add', { token }), 'SCRIPT_INTEGRITY');
  okay(await rpc('stage.abort', { token }));
  denied(await rpc('sources.add', { token }), 'STAGE_NOT_FOUND');
});

test('source larger than the former 512KiB gate and a split UTF-8 character execute as real LX', async t => {
  const { rpc } = setup(t);
  const code = fixture();
  const prefix = '// ' + ' '.repeat(65532) + '猫\n'; // character starts at byte 65535.
  const unpadded = prefix + code;
  const sourceBytes = limit * 2 + 17;
  const source = unpadded + ' '.repeat(sourceBytes - Buffer.byteLength(unpadded));
  assert.equal(Buffer.byteLength(source), sourceBytes);
  const item = await enabled(rpc, source);
  assert.equal(item.contentHash, hash(Buffer.from(source)));
  assert.equal(okay(await resolve(rpc, item)).url, 'https://media.invalid/kw/320k/1?n=1');
});

test('network needs no domain grant and receives scoped identity, never platform credentials', async t => {
  const calls = [];
  const { rpc } = setup(t, { network: async input => {
    calls.push(input);
    assert.equal(input.validateURL('https://other.invalid/no'), 'https://other.invalid/no');
    return { statusCode: 200, body: 'synthetic-network' };
  } });
  const item = await enabled(rpc, networkFixture);
  assert.equal(await value(rpc, item, 'tx'), 'synthetic-network');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].scope.installationId, item.installationId);
  assert.equal(calls[0].scope.generation, item.generation);
  assert.equal(calls[0].scope.siteId, 'tx');
  assert.equal(calls[0].options.headers, undefined);
});

test('LX handles a 12 MiB network response and large invoke input and output', { timeout: 60000 }, async t => {
  const size = 12 * 1024 * 1024;
  const body = Buffer.alloc(size, 0x5a);
  const md5 = value => createHash('md5').update(value).digest('hex');
  const { rpc } = setup(t, { network: async () => ({ statusCode: 200, body }) });
  const item = await enabled(rpc, fixture({ setup: 'const digest = value => lx.utils.crypto.md5(value);',
    handler: `async ({info}) => info.musicInfo.data !== undefined ? info.musicInfo.data
      : info.musicInfo.size !== undefined ? 'x'.repeat(info.musicInfo.size)
      : new Promise((resolve,reject) => lx.request('https://fixture.invalid/large',{encoding:null},
        (error,response,body)=>error?reject(error):resolve({ length: body.length, hash: digest(body) })))` }));
  assert.deepEqual(await value(rpc, item, 'kw'), { length: size, hash: md5(body) });
  for (const length of [limit + 1, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1]) {
    const data = 'x'.repeat(length);
    assert.equal(await value(rpc, item, 'kw', { data }), data);
    assert.equal(await value(rpc, item, 'kw', { size: length }), data);
  }
});

test('extended declared actions keep payloads and raw values; undeclared actions are unavailable', async t => {
  const { rpc } = setup(t);
  const item = await enabled(rpc, fixture({
    sources: { kw: { name: 'extension', type: 'music', actions: ['musicSearch', 'musicUrl'],
      qualitys: [{ type: '128k', label: 'standard' }] } },
    handler: 'async ({source,action,info}) => ({source,action,info})',
  }));
  const payload = { keyword: 'song', page: 2, pagesize: 10 };
  assert.deepEqual(okay(await rpc('invoke', { installationId: item.installationId, siteId: 'kw',
    operation: 'musicSearch', payload })).value, { source: 'kw', action: 'musicSearch', info: payload });
  assert.deepEqual(item.sites.kw.qualitys, ['128k']);
  denied(await rpc('invoke', { installationId: item.installationId, siteId: 'kw', operation: 'search', payload }),
    'CAPABILITY_UNAVAILABLE');
});

test('many in-flight invocations keep management available and are not capped per site or instance', async t => {
  const entered = deferred(), release = deferred(); let count = 0;
  const { rpc } = setup(t, { network: async () => {
    if (++count === 24) entered.resolve(); await release.promise;
    return { statusCode: 200, body: 'done' };
  } });
  const items = [await enabled(rpc, networkFixture), await enabled(rpc, networkFixture)];
  const requests = items.flatMap(item => Array.from({ length: 12 },
    (_, index) => value(rpc, item, index % 2 ? 'kw' : 'tx')));
  try {
    await entered.promise; assert.equal(count, 24);
    assert.equal(okay(await rpc('sources.list')).items.length, 2);
    await imported(rpc);
  } finally { release.resolve(); }
  for (const reply of await Promise.all(requests)) assert.equal(reply, 'done');
});

test('pre-cancelled requests do not load; cancellation fences completion and later requests still work', async t => {
  const entered = deferred(), release = deferred();
  let delayed = true;
  const { rpc, host } = setup(t, { network: async () => {
    if (delayed) { entered.resolve(); await release.promise; }
    return { statusCode: 200, body: 'result' };
  } });
  const item = await imported(rpc, networkFixture);
  const cancelled = new AbortController(); cancelled.abort();
  denied(await rpc('sources.enable', { installationId: item.installationId }, { signal: cancelled.signal }),
    'CANCELLED');
  assert.equal(host().list().length, 0);
  okay(await rpc('sources.enable', { installationId: item.installationId }));
  const controller = new AbortController();
  const pending = rpc('invoke', { installationId: item.installationId, siteId: 'kw', operation: 'musicUrl',
    payload: { type: '320k' } }, { signal: controller.signal });
  await entered.promise;
  controller.abort();
  denied(await pending, 'CANCELLED');
  release.resolve(); await turn();
  delayed = false;
  assert.equal(await value(rpc, item), 'result');
});

test('disable and remove retire a live generation; close cancels pending work', async t => {
  const entered = deferred();
  const { rpc, module, host } = setup(t, { network: (_input) => new Promise((_resolve, reject) => {
    entered.resolve();
    _input.signal.addEventListener('abort', () => reject(new ScriptHostError('CANCELLED')), { once: true });
  }) });
  const source = fixture({ setup: 'setInterval(()=>{},10000);', handler: `async ({info}) => info.musicInfo.wait
    ? new Promise((resolve,reject) => lx.request('https://fixture.invalid/v',{},(e,r,b)=>e?reject(e):resolve(b)))
    : 'https://media.invalid/ok'` });
  const item = await enabled(rpc, source);
  assert.equal(host().stats().timers, 1);
  okay(await rpc('sources.disable', { installationId: item.installationId }));
  assert.equal(host().stats().timers, 0);
  const again = okay(await rpc('sources.enable', { installationId: item.installationId })).item;
  assert.ok(again.generation > item.generation);
  const pending = resolve(rpc, again, 'kw', { wait: true });
  await entered.promise;
  okay(await module.close());
  denied(await pending, 'CLOSED');
  denied(await rpc('sources.list'), 'CLOSED');
});

test('callback timeout is bounded by the real host and a timed-out startup leaves no instance', async t => {
  const { rpc, host } = setup(t, { hostFactory: input =>
    createScriptHost({ ...input, limits: { ...input.limits, initTimeoutMs: 1000, requestTimeoutMs: 30 } }) });
  const stuck = await imported(rpc, 'setInterval(()=>{},10000);');
  denied(await rpc('sources.enable', { installationId: stuck.installationId }), 'TIMEOUT');
  void host;
  const item = await enabled(rpc, fixture({ handler: 'async () => new Promise(()=>{})' }));
  denied(await resolve(rpc, item), 'TIMEOUT');
  assert.equal(okay(await rpc('sources.detail', { installationId: item.installationId })).item.state, 'ready');
});
