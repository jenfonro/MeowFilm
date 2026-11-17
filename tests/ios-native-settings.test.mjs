import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import { once } from 'node:events';
import { join } from 'node:path';
import { createNativeSettings, nativeSettingsFrameBytes } from '../apps/runtime/native-settings.mjs';
import { parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { parseIOSConfiguration } from '../apps/ios/runtime/bootstrap.mjs';
import { createTMDBDataModule } from '../apps/runtime/modules/metadata-data.mjs';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';
import { deferred, rawRequest } from '../packages/runtime-bridge/test/http-fixture.js';
import { connect, okay, root, simulatedIOS, spawnRuntime } from './ios-runtime-support.mjs';

const blocks = ['cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const fingerprint = document => {
  const bytes = document === null ? null : Buffer.from(stringifyJSONValue(document));
  return { exists: bytes !== null, length: bytes?.length ?? 0, sha256: bytes === null ? null : hash(bytes) };
};
const result = (id, document) => ({ kind: 'settings.result', id, ok: true, document, fingerprint: fingerprint(document) });
const line = value => Buffer.from(stringifyJSONValue(value, -2) + '\n');
const rules = () => ({ episodeCleanRegexRules: [], episodeRules: [], movieRules: [], aggregateRegexRules: [] });
const tmdb = language => ({ version: 1, settings: { apiBase: 'https://api.themoviedb.org/3',
  imageBase: 'https://image.tmdb.org', apiKey: '', accessToken: '', language, region: 'CN', includeAdult: false } });
const entry = join(root, 'apps/ios/runtime/entries/aggregate.mjs');

function pipe(t) {
  const requests = [], stopped = [];
  const settings = createNativeSettings({
    write(bytes, complete) { requests.push(parseJSONText(bytes.toString())); complete(); },
  }, code => stopped.push(code));
  t.after(() => settings.close());
  return { settings, requests, stopped, respond(id, document) { settings.accept(line(result(id, document))); } };
}

test('native settings configuration is opt-in and never accepts alternative owners or extra configuration fields', () => {
  const configuration = { sessionId: randomUUID(), dataRoot: '/native/猫' };
  assert.deepEqual(parseIOSConfiguration(line({ ...configuration, nativeSettings: true })),
    { ...configuration, nativeSettings: true });
  for (const value of [false, null, 1, 'true', {}, []])
    assert.throws(() => parseIOSConfiguration(line({ ...configuration, nativeSettings: value })), { code: 'INVALID_REQUEST' });
  assert.throws(() => parseIOSConfiguration(line({ ...configuration, nativeSettings: true, settings: {} })),
    { code: 'INVALID_REQUEST' });
});

test('private settings cold-read every registered block and detach all returned snapshots', async t => {
  const f = pipe(t), initialized = f.settings.initialize();
  for (let index = 0; index < blocks.length; index++) {
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(f.requests[index], { kind: 'settings.read', id: index + 1, block: blocks[index] });
    const value = { version: 1, test: { block: blocks[index] } }, bytes = line(result(index + 1, value));
    f.settings.accept(bytes.subarray(0, 9));
    f.settings.accept(bytes.subarray(9));
  }
  await initialized;
  assert.equal(f.requests.length, blocks.length);
  for (const block of blocks) {
    const copy = f.settings.read(block);
    copy.test.block = 'detached';
    assert.equal(f.settings.read(block).test.block, block);
  }
  await assert.rejects(f.settings.reload('unknown'), { code: 'INVALID_REQUEST' });
  assert.throws(() => f.settings.document('tmdb'), { code: 'INVALID_REQUEST' });
  await assert.rejects(f.settings.write('tmdb', {}, fingerprint(null)), { code: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, blocks.length);
});

test('private pan writes use the observed fingerprint and rejected CAS never publishes a speculative snapshot', async t => {
  const f = pipe(t), initial = { version: 1, accounts: {} }, read = f.settings.reload('pan.accounts');
  f.respond(1, initial); await read;
  const store = f.settings.document('pan.accounts'), next = { version: 1, accounts: {
    baidu: { revision: 1, credentials: { cookie: 'synthetic-private-cas' } },
  } };
  const writing = store.write(next);
  assert.deepEqual(f.requests[1], { kind: 'settings.write', id: 2, block: 'pan.accounts',
    document: next, expected: fingerprint(initial) });
  const rejected = assert.rejects(writing, { code: 'STORAGE_CONFLICT' });
  f.settings.accept(line({ kind: 'settings.result', id: 2, ok: false, code: 'STORAGE_CONFLICT' }));
  await rejected;
  assert.deepEqual(store.read(), initial);
  assert.deepEqual(f.settings.read('pan.accounts'), initial);
  const accepted = store.write(next);
  f.respond(3, next); await accepted;
  assert.deepEqual(store.read(), next);
  assert.deepEqual(f.settings.read('pan.accounts'), next);
  const reload = store.reload(), reloaded = { version: 1, accounts: {} };
  f.respond(4, reloaded); await reload;
  const last = store.write(next);
  assert.deepEqual(f.requests[4].expected, fingerprint(reloaded));
  f.respond(5, next); await last;
  store.close();
  assert.throws(() => store.read(), { code: 'CLOSED' });
  await assert.rejects(store.write(next), { code: 'CLOSED' });
});

test('private settings consume the reply fields without rejecting or changing unused JSON material', async t => {
  const f = pipe(t);
  const document = parseJSONText('{"version":1,"accounts":{},"extension":9007199254740993,"é":1,"é":2}');
  const pending = f.settings.reload('pan.accounts');
  const reply = result(1, document);
  reply.extension = 'unused';
  reply.fingerprint.extension = 1;
  f.settings.accept(line(reply));
  await pending;
  const copy = f.settings.document('pan.accounts').read();
  assert.equal(stringifyJSONValue(copy), stringifyJSONValue(document));
  const failure = assert.rejects(f.settings.reload('tmdb'), { code: 'STORAGE_ERROR' });
  f.settings.accept(line({ kind: 'settings.result', id: 2, ok: false, code: 'STORAGE_ERROR', extension: true }));
  await failure;
  assert.deepEqual(f.stopped, []);
});

test('private settings control frames do not consume the document depth budget', async t => {
  const f = pipe(t);
  let nested = null;
  for (let index = 0; index < 63; index++) nested = [nested];
  const document = { nested }, pending = f.settings.reload('pan.accounts');
  f.respond(1, document);
  assert.deepEqual(await pending, document);
  const store = f.settings.document('pan.accounts');
  assert.deepEqual(store.read(), document);
  const written = store.write(document);
  assert.deepEqual(f.requests[1].document, document);
  f.respond(2, document);
  assert.deepEqual(await written, document);
  store.close();
  assert.deepEqual(f.stopped, []);
});

test('private frame parsing rejects malformed values, fingerprints, unknown codes and invalid UTF-8, draining every exchange',
  async t => {
    for (const reply of [
      { ...result(1, null), id: 99 },
      { ...result(1, null), document: [] },
      { ...result(1, null), fingerprint: { exists: false, length: 1, sha256: null } },
      { ...result(1, null), fingerprint: { exists: true, length: 1, sha256: 'not-a-digest' } },
      { kind: 'settings.result', id: 1, ok: false, code: 'private-error-message' },
      Buffer.from([0xff, 10]),
      Buffer.alloc(nativeSettingsFrameBytes + 1, 32),
    ]) await t.test(Buffer.isBuffer(reply) ? `bytes-${reply.length}` : JSON.stringify(reply), async sub => {
      const f = pipe(sub), first = assert.rejects(f.settings.reload('tmdb'), { code: 'CLOSED' }),
        second = assert.rejects(f.settings.reload('douban'), { code: 'CLOSED' });
      f.settings.accept(Buffer.isBuffer(reply) ? reply : line(reply));
      await Promise.all([first, second]);
      assert.deepEqual(f.stopped, ['INVALID_RESPONSE']);
      assert.throws(() => f.settings.read('tmdb'), { code: 'CLOSED' });
    });
  });

test('private frames accept multiple replies per chunk but bound outgoing writes and release pending work on close', async t => {
  const f = pipe(t), a = f.settings.reload('tmdb'), b = f.settings.reload('douban');
  f.settings.accept(Buffer.concat([line(result(1, null)), line(result(2, { version: 1 }))]));
  assert.equal(await a, null);
  assert.deepEqual(await b, { version: 1 });
  await assert.rejects(f.settings.write('pan.accounts', { oversized: 'x'.repeat(nativeSettingsFrameBytes) }, fingerprint(null)),
    { code: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, 2);
  const waiting = assert.rejects(f.settings.reload('cat.sources'), { code: 'CLOSED' });
  f.settings.close();
  await waiting;
  f.settings.close();
  assert.deepEqual(f.stopped, []);
});

test('private timeout and synchronous stream failures release pending requests without leaking diagnostics', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const f = pipe(t), waiting = assert.rejects(f.settings.reload('tmdb'), { code: 'CLOSED' });
  t.mock.timers.tick(5000);
  await waiting;
  assert.deepEqual(f.stopped, ['TIMEOUT']);
  const stopped = [], settings = createNativeSettings({
    write() { throw new Error('synthetic-private-stream-failure'); },
  }, code => stopped.push(code));
  await assert.rejects(settings.reload('douban'), { code: 'CLOSED' });
  assert.deepEqual(stopped, ['CLOSED']);
});

function nativeRuntime(t, { documents = {}, preload = simulatedIOS, intercept, configuration = {} } = {}) {
  const saved = new Map(blocks.map(block => [block, structuredClone(documents[block] ?? null)]));
  const requests = [], writes = [];
  const runtime = spawnRuntime(t, { entry, preload, configuration: { nativeSettings: true, ...configuration },
    async onSettings(request) {
      requests.push(request);
      const intercepted = await intercept?.(request);
      if (intercepted !== undefined) return intercepted;
      assert.ok(blocks.includes(request.block));
      assert.ok(Number.isSafeInteger(request.id) && request.id > 0);
      if (request.kind === 'settings.write') {
        assert.equal(request.block, 'pan.accounts');
        assert.deepEqual(Object.keys(request).sort(), ['block', 'document', 'expected', 'id', 'kind']);
        if (JSON.stringify(request.expected) !== JSON.stringify(fingerprint(saved.get(request.block)))) {
          return { kind: 'settings.result', id: request.id, ok: false, code: 'STORAGE_CONFLICT' };
        }
        saved.set(request.block, structuredClone(request.document));
        writes.push(request);
      } else {
        assert.equal(request.kind, 'settings.read');
        assert.deepEqual(Object.keys(request).sort(), ['block', 'id', 'kind']);
      }
      return result(request.id, structuredClone(saved.get(request.block)));
    },
  });
  return { ...runtime, saved, requests, writes };
}
async function notify(runtime, block) {
  return (await rawRequest(await runtime.endpoint, {},
    { path: '/v1/settings/changed', body: JSON.stringify({ block }) }).response).value;
}
async function stop(runtime, client) {
  okay(await client.shutdown(), 'runtime.http.v1');
  runtime.channel.end();
  await runtime.closed;
  await client.close();
  assert.deepEqual(await runtime.exited, { code: 0, signal: null }, JSON.stringify(runtime.output()));
  assert.equal(runtime.output().stderr, '');
}
async function catState(client, revision) {
  const deadline = Date.now() + 10000;
  for (;;) {
    const current = okay(await client.call('cat.script.status'), 'cat.script.v2').instances[0];
    if (current?.inputRevision === revision && current.state === 'ready') return current;
    assert.ok(Date.now() < deadline, JSON.stringify(current));
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}
const catInput = (path, bytes, revision) => ({
  version: 1, port: 0, providers: {}, mappings: [], scripts: [{
    installationId: 'native-cat', name: 'Native Cat', enabled: true, sourceType: 'local', sourceUrl: null,
    localPath: path, localHash: hash(bytes), inputRevision: revision,
  }],
});
const catSource = value => `module.exports = { home() { return { nativeValue: ${JSON.stringify(value)} }; } };`;

test('real iOS bootstrap cold-reads all native blocks, applies notifications and denies second configuration storage',
  { timeout: 30000 }, async t => {
    const first = nativeRuntime(t, { documents: {
      tmdb: tmdb('zh-CN'), douban: { version: 1, settings: { cookie: 'synthetic-native-cookie' } },
      'pan.accounts': { version: 1, accounts: { baidu: { revision: 4, credentials: { cookie: 'synthetic-pan-cookie' } } } },
      'film.rules': { version: 1, rules: rules() },
      'film.preferences': { version: 1, preferences: { ...defaultPreferences(), keywords: ['native-first'] } },
    } });
    const { client } = await connect(first);
    t.after(() => client.close());
    assert.deepEqual(first.requests.map(request => request.block), blocks);
    for (const invalid of [null, [], {}, { block: 1 }, { block: 'tmdb\n' }]) {
      const response = await rawRequest(await first.endpoint, {},
        { path: '/v1/settings/changed', body: JSON.stringify(invalid) }).response;
      assert.equal(response.status, 400);
      assert.deepEqual(response.value, { ok: false, contract: 'runtime.http.v1', code: 'INVALID_REQUEST' });
    }
    assert.equal(first.requests.length, blocks.length, 'invalid notifications cannot request private settings');
    assert.equal(okay(await client.call('tmdb.data.settings.get'), 'tmdb.data.v1').language, 'zh-CN');
    assert.equal(okay(await client.call('douban.data.settings.get'), 'douban.data.v1').cookie, 'synthetic-native-cookie');
    const pan = okay(await client.call('pan.settings.get'), 'pan.runtime.v1');
    assert.equal(pan.accounts.find(account => account.provider === 'baidu').revision, 4);
    assert.equal(JSON.stringify(pan).includes('synthetic-pan-cookie'), false);
    assert.deepEqual(okay(await client.call('film.rules.get'), 'film.playback.v1').rules, rules());
    assert.deepEqual(okay(await client.call('film.settings.get'), 'film.playback.v1').keywords, ['native-first']);
    for (const [method, input, contract] of [
      ['tmdb.data.settings.update', { language: 'en-US' }, 'tmdb.data.v1'],
      ['douban.data.settings.update', { cookie: 'not-saved' }, 'douban.data.v1'],
      ['film.rules.set', { rules: rules() }, 'film.playback.v1'],
      ['film.settings.set', defaultPreferences(), 'film.playback.v1'],
      ['cat.script.edit', { installationId: 'native-cat', name: 'not-saved' }, 'cat.script.v2'],
    ]) assert.deepEqual(await client.call(method, input), { ok: false, contract, code: 'PERMISSION_DENIED' });
    assert.deepEqual(first.writes, []);
    first.saved.set('tmdb', tmdb('en-US'));
    first.saved.set('douban', { version: 1, settings: { cookie: 'synthetic-changed-cookie' } });
    first.saved.set('film.rules', { version: 1, rules: { ...rules(), aggregateRegexRules: ['native-latest'] } });
    first.saved.set('film.preferences', { version: 1, preferences: { ...defaultPreferences(), keywords: ['native-latest'] } });
    first.saved.set('pan.accounts', { version: 1, accounts: { baidu: { revision: 5, credentials: {} } } });
    for (const block of blocks.filter(block => block !== 'cat.sources'))
      assert.deepEqual(await notify(first, block), { ok: true, contract: 'runtime.http.v1', value: { notified: true } });
    assert.equal(okay(await client.call('tmdb.data.settings.get'), 'tmdb.data.v1').language, 'en-US');
    assert.equal(okay(await client.call('douban.data.settings.get'), 'douban.data.v1').cookie, 'synthetic-changed-cookie');
    assert.deepEqual(okay(await client.call('film.rules.get'), 'film.playback.v1').rules.aggregateRegexRules, ['native-latest']);
    assert.deepEqual(okay(await client.call('film.settings.get'), 'film.playback.v1').keywords, ['native-latest']);
    assert.equal(okay(await client.call('pan.settings.get'), 'pan.runtime.v1').accounts
      .find(account => account.provider === 'baidu').configured, false);
    assert.deepEqual(await notify(first, 'unknown'), { ok: false, contract: 'runtime.http.v1', code: 'INVALID_REQUEST' });
    assert.equal(first.writes.length, 0);
    for (const path of ['pan/accounts.json', 'tmdb/settings.json', 'douban/settings.json', 'film-rules/rules.json'])
      assert.equal(existsSync(join(first.configuration.dataRoot, path)), false, path);
    await stop(first, client);
    first.saved.set('tmdb', tmdb('ja-JP')); // No notification while stopped.
    const second = nativeRuntime(t, { documents: Object.fromEntries(first.saved),
      configuration: { dataRoot: first.configuration.dataRoot } });
    const recovered = await connect(second);
    t.after(() => recovered.client.close());
    assert.deepEqual(second.requests.map(request => request.block), blocks);
    assert.equal(okay(await recovered.client.call('tmdb.data.settings.get'), 'tmdb.data.v1').language, 'ja-JP');
    await stop(second, recovered.client);
  });

test('real iOS private Cat input notifications download replacements and isolate a retired download',
  { timeout: 30000 }, async t => {
    const runtime = nativeRuntime(t), { client } = await connect(runtime);
    t.after(() => client.close());
    const bytes = catSource('local'), path = join(runtime.area, 'native-selected.js');
    writeFileSync(path, bytes);
    runtime.saved.set('cat.sources', catInput(path, bytes, 1));
    assert.equal((await notify(runtime, 'cat.sources')).ok, true);
    const home = state => client.call('cat.script.invoke', { installationId: state.installationId,
      generation: state.generation, siteId: 'default', operation: 'home', payload: {} });
    assert.deepEqual(okay(await home(await catState(client, 1)), 'cat.script.v2'), { nativeValue: 'local' });
    const entered = deferred(), release = deferred();
    let downloads = 0, hold = false;
    const server = http.createServer(async (_request, response) => {
      downloads++;
      if (hold) { entered.resolve(); await release.promise; }
      response.end(catSource(hold ? 'late-network' : 'network'));
    });
    server.listen(0, '127.0.0.1'); await once(server, 'listening');
    t.after(async () => { release.resolve(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
    const network = revision => ({ ...catInput(path, bytes, revision), scripts: [{
      ...catInput(path, bytes, revision).scripts[0], sourceType: 'network',
      sourceUrl: `http://127.0.0.1:${server.address().port}/cat.js`, localPath: null, localHash: null,
    }] });
    runtime.saved.set('cat.sources', network(2));
    assert.equal((await notify(runtime, 'cat.sources')).ok, true);
    assert.deepEqual(okay(await home(await catState(client, 2)), 'cat.script.v2'), { nativeValue: 'network' });
    assert.equal(downloads, 1);
    assert.equal(existsSync(path), true);
    hold = true; runtime.saved.set('cat.sources', network(3));
    assert.equal((await notify(runtime, 'cat.sources')).ok, true);
    await entered.promise;
    const latest = catSource('latest-local');
    writeFileSync(path, latest); runtime.saved.set('cat.sources', catInput(path, latest, 4));
    assert.equal((await notify(runtime, 'cat.sources')).ok, true);
    release.resolve();
    const current = await catState(client, 4);
    assert.deepEqual(okay(await home(current), 'cat.script.v2'), { nativeValue: 'latest-local' });
    const facts = JSON.parse(readFileSync(join(runtime.configuration.dataRoot, 'cat/settings.json'), 'utf8'));
    assert.equal(facts.version, 2);
    assert.equal(facts.scripts[0].loadedIdentity, `local:4:${hash(latest)}`);
    for (const field of ['name', 'sourceType', 'sourceUrl', 'enabled', 'localPath', 'localHash', 'inputRevision'])
      assert.equal(Object.hasOwn(facts.scripts[0], field), false, field);
    assert.equal(existsSync(join(runtime.configuration.dataRoot, 'cat/sources', hash(catSource('late-network')) + '.js')), false);
    await stop(runtime, client);
  });

test('real iOS native metadata notification revokes the film cache and cancels an older provider request',
  { timeout: 30000 }, async t => {
    const counts = { a: 0, b: 0 }, entered = deferred(), release = deferred();
    const server = http.createServer(async (request, response) => {
      const url = new URL(request.url, 'http://fixture.invalid'), label = url.pathname.split('/')[1];
      counts[label]++;
      if (url.pathname.endsWith('/43') && label === 'a') { entered.resolve(); await release.promise; }
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify({ id: url.pathname.endsWith('/43') ? 43 : 42, title: label,
        status: 'Released', genres: [], runtime: 90 }));
    });
    server.listen(0, '127.0.0.1'); await once(server, 'listening');
    t.after(async () => { release.resolve(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
    const configuration = label => ({ ...tmdb('zh-CN'), settings: {
      ...tmdb('zh-CN').settings, apiBase: `http://127.0.0.1:${server.address().port}/${label}/3`,
    } });
    const runtime = nativeRuntime(t, { documents: { tmdb: configuration('a') } });
    const { client } = await connect(runtime);
    t.after(() => client.close());
    const request = { provider: 'tmdb', path: '/3/movie/42', query: { language: 'zh-CN' } };
    const initial = okay(await client.call('film.data.metadata', request), 'film.playback.v1');
    assert.equal(initial.title, 'a');
    assert.equal(initial.businessId, 'tmdb:movie:42');
    assert.equal(initial.playbackTarget.id, initial.businessId);
    assert.deepEqual(okay(await client.call('film.data.metadata', request), 'film.playback.v1'), initial);
    assert.deepEqual(counts, { a: 1, b: 0 });
    const pending = client.call('film.data.metadata', { ...request, path: '/3/movie/43' });
    await entered.promise;
    runtime.saved.set('tmdb', configuration('b'));
    assert.equal((await notify(runtime, 'tmdb')).ok, true);
    assert.deepEqual(await pending, { ok: false, contract: 'film.playback.v1', code: 'CANCELLED' });
    release.resolve();
    const refreshed = okay(await client.call('film.data.metadata', request), 'film.playback.v1');
    assert.equal(refreshed.title, 'b');
    assert.equal(refreshed.businessId, initial.businessId);
    assert.equal(okay(await client.call('film.data.metadata', { ...request, path: '/3/movie/43' }), 'film.playback.v1').title, 'b');
    assert.deepEqual(counts, { a: 2, b: 2 });
    await stop(runtime, client);
  });

test('real iOS private pan refresh CAS commits only native storage and a stale receipt cannot overwrite a newer account',
  { timeout: 30000 }, async t => {
    const requested = [], entered = deferred(), release = deferred();
    let hold = false;
    const server = http.createServer(async (request, response) => {
      assert.equal(request.method, 'GET');
      assert.equal(request.url, '/account/info?fr=pc&platform=pc');
      requested.push(request.headers.cookie);
      if (hold) { entered.resolve(); await release.promise; }
      response.setHeader('content-type', 'application/json');
      response.setHeader('set-cookie', `session=${hold ? 'synthetic-late-refresh' : 'synthetic-new'}; Path=/`);
      response.end(JSON.stringify({ code: 'OK', success: true, data: { uid: 'synthetic-user' } }));
    });
    server.listen(0, '127.0.0.1'); await once(server, 'listening');
    t.after(async () => { release.resolve(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
    const runtime = nativeRuntime(t, { documents: { 'pan.accounts': { version: 1, accounts: {
      quark: { revision: 1, credentials: { cookie: 'session=synthetic-initial' } },
    } } }, preload: `${simulatedIOS}
      import assert from 'node:assert/strict';
      import http from 'node:http';
      import https from 'node:https';
      https.request = function(url, options) {
        assert.equal(String(url), 'https://pan.quark.cn/account/info?fr=pc&platform=pc');
        return http.request(
          new URL(${JSON.stringify(`http://127.0.0.1:${server.address().port}/account/info?fr=pc&platform=pc`)}),
          { ...options, agent: false });
      };
    ` });
    const { client } = await connect(runtime);
    t.after(() => client.close());
    assert.equal(requested.length, 0, 'cold-reading accounts must not refresh them');
    const refreshed = okay(await client.call('pan.refresh', { provider: 'quark' }), 'pan.runtime.v1');
    assert.equal(refreshed.account.revision, 1);
    assert.equal(runtime.writes.length, 1);
    assert.equal(runtime.saved.get('pan.accounts').accounts.quark.credentials.cookie, 'session=synthetic-new');
    assert.equal(requested[0], 'session=synthetic-initial');
    assert.equal(existsSync(join(runtime.configuration.dataRoot, 'pan/accounts.json')), false);
    hold = true;
    const refreshing = client.call('pan.refresh', { provider: 'quark' });
    await entered.promise;
    const latest = { version: 1, accounts: {
      quark: { revision: 2, credentials: { cookie: 'session=synthetic-native-later' } },
      baidu: { revision: 1, credentials: { cookie: 'synthetic-unrelated-native' } },
    } };
    runtime.saved.set('pan.accounts', latest);
    release.resolve();
    assert.deepEqual(await refreshing, { ok: false, contract: 'pan.runtime.v1', code: 'STALE_ACCOUNT' });
    assert.equal(requested[1], 'session=synthetic-new');
    assert.deepEqual(runtime.saved.get('pan.accounts'), latest);
    assert.equal(runtime.writes.length, 1, 'CAS failure cannot blind-retry');
    const accounts = okay(await client.call('pan.settings.get'), 'pan.runtime.v1');
    assert.equal(accounts.accounts.find(account => account.provider === 'baidu').revision, 1);
    assert.equal(accounts.accounts.find(account => account.provider === 'quark').revision, 2);
    assert.equal(JSON.stringify(accounts).includes('synthetic-'), false);
    assert.equal(runtime.requests.filter(request => request.kind === 'settings.write').length, 2);
    await stop(runtime, client);
  });

test('real iOS owner EOF while a settings read is pending drains HTTP and every private exchange',
  { timeout: 30000 }, async t => {
    let hold = false;
    const entered = deferred(), release = deferred();
    t.after(() => release.resolve());
    const runtime = nativeRuntime(t, { intercept: async request => {
      if (hold && request.block === 'tmdb') {
        entered.resolve(); await release.promise;
        return result(request.id, null);
      }
    } });
    const { client, baseUrl } = await connect(runtime);
    t.after(() => client.close());
    hold = true;
    const pending = notify(runtime, 'tmdb').catch(error => error);
    await entered.promise;
    runtime.channel.end();
    await runtime.closed;
    await pending;
    assert.deepEqual(await runtime.exited, { code: 0, signal: null }, JSON.stringify(runtime.output()));
    await assert.rejects(fetch(`${baseUrl}/v1/describe`));
    assert.equal(runtime.output().stderr, '');
    release.resolve();
  });

test('real iOS owner EOF while initializing settings cancels configuration instead of assembling module owners',
  { timeout: 30000 }, async t => {
    const entered = deferred(), release = deferred();
    t.after(() => release.resolve());
    const runtime = nativeRuntime(t, { intercept: async request => {
      entered.resolve(); await release.promise;
      return result(request.id, null);
    } });
    await entered.promise;
    runtime.channel.end();
    await runtime.closed;
    assert.deepEqual(await runtime.exited, { code: 1, signal: null }, JSON.stringify(runtime.output()));
    assert.deepEqual(runtime.requests.map(request => request.block), ['cat.sources']);
    assert.equal(runtime.output().stderr, 'IOS_RUNTIME_START_FAILED stage=configuration category=CLOSED\n');
    assert.equal(existsSync(runtime.configuration.dataRoot), false);
    await assert.rejects(runtime.endpoint, { message: 'BOOTSTRAP_CLOSED' });
    release.resolve();
  });

test('real iOS startup settings failures expose only configuration stage and safe category and release the channel',
  { timeout: 30000 }, async t => {
    for (const code of ['STORAGE_ERROR', 'CORRUPT_STORAGE']) {
      const runtime = nativeRuntime(t, { intercept: request =>
        ({ kind: 'settings.result', id: request.id, ok: false, code }) });
      await runtime.closed;
      assert.deepEqual(await runtime.exited, { code: 1, signal: null }, JSON.stringify(runtime.output()));
      assert.equal(runtime.output().stderr, `IOS_RUNTIME_START_FAILED stage=configuration category=${code}\n`);
      assert.equal(runtime.output().stdout, '');
      await assert.rejects(runtime.endpoint, { message: 'BOOTSTRAP_CLOSED' });
    }
  });

test('native metadata reload cancels old direct requests and subsequent requests read the latest configuration', async t => {
  let current = tmdb('zh-CN'), captured;
  const entered = deferred(), release = deferred();
  const module = createTMDBDataModule({ nativeSettings: { read: () => current }, request: async (_url, options) => {
    captured = options.signal; entered.resolve(); await release.promise;
    return { status: 200, headers: {}, body: Buffer.from('{"results":[]}') };
  } });
  t.after(async () => { release.resolve(); await module.close(); });
  const work = module.methods['tmdb.data.request']({ path: '/search/movie', query: { query: 'fixture' } });
  await entered.promise;
  current = tmdb('en-US');
  module.settingsChanged();
  assert.equal(captured.aborted, true);
  release.resolve();
  assert.deepEqual(await work, { ok: false, contract: 'tmdb.data.v1', code: 'CANCELLED' });
  assert.equal((await module.methods['tmdb.data.settings.get']({})).value.language, 'en-US');
  assert.equal((await module.methods['tmdb.data.request']({ path: '/search/movie', query: { query: 'fixture' } })).ok, true);
});

test('native metadata notification cancels an open image stream and uses the current image endpoint next time',
  { timeout: 10000 }, async t => {
    const closed = deferred(), observed = [];
    const server = http.createServer((request, response) => {
      observed.push(request.url);
      response.setHeader('content-type', 'image/png');
      if (request.url.startsWith('/old/')) {
        response.once('close', () => closed.resolve());
        response.write(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
      } else response.end(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
    });
    server.listen(0, '127.0.0.1'); await once(server, 'listening');
    let current = { ...tmdb('zh-CN'), settings: { ...tmdb('zh-CN').settings,
      imageBase: `http://127.0.0.1:${server.address().port}/old` } };
    const module = createTMDBDataModule({ nativeSettings: { read: () => current } });
    t.after(async () => {
      await module.close();
      server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
    });
    const old = await module.image('/v1/tmdb/images/original/fixture.png');
    assert.equal(old.status, 200);
    current = { ...current, settings: { ...current.settings, imageBase: `http://127.0.0.1:${server.address().port}/new` } };
    module.settingsChanged();
    await closed.promise;
    await old.dispose();
    const next = await module.image('/v1/tmdb/images/original/fixture.png');
    assert.equal(next.status, 200);
    const chunks = [];
    for await (const chunk of next.body) chunks.push(chunk);
    await next.dispose();
    assert.equal(Buffer.concat(chunks).length, 8);
    assert.deepEqual(observed, ['/old/t/p/original/fixture.png', '/new/t/p/original/fixture.png']);
  });
