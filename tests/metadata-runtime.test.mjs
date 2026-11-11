import test from 'node:test';
import assert from 'node:assert/strict';
import http, { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync, rmSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { Readable } from 'node:stream';
import { createAppRuntime, createTMDBDataModule, createDoubanDataModule,
  createPanRuntimeModule } from '../apps/runtime/entry.mjs';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { createMetadataSettings } from '../apps/runtime/modules/metadata/settings.mjs';
import { checkTestSummary } from '../scripts/run-tests.mjs';

function area(t) {
  const root = fileURLToPath(new URL('../tmp/test-results/tmdb-douban-data/', import.meta.url));
  mkdirSync(root, { recursive: true });
  const path = mkdtempSync(join(root, 'data-'));
  t.after(() => rmSync(path, { recursive: true, force: true }));
  return path;
}
async function upstream(t, handler) {
  const server = createServer(handler);
  await new Promise(done => server.listen(0, '127.0.0.1', done));
  t.after(() => new Promise(done => { server.closeAllConnections(); server.close(done); }));
  return `http://127.0.0.1:${server.address().port}`;
}
async function runtime(t, dataRoot, options = {}) {
  const identity = { sessionId: randomUUID() };
  const modules = [createTMDBDataModule({ dataRoot, ...options }),
    createDoubanDataModule({ dataRoot, ...options })];
  const app = createAppRuntime({ ...identity, modules });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  async function close() { await client.close(); await app.close(); }
  t.after(close);
  return { app, client, modules, close };
}
function value(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  return reply.value;
}
function image(owner, path, { onData } = {}) {
  let request, response, headers;
  const result = new Promise((resolve, reject) => {
    const requestId = randomUUID();
    request = http.get(owner.app.baseUrl + path, { agent: false, headers: { 'x-meow-request-id': requestId } }, incoming => {
      response = incoming; headers = incoming.headers;
      const chunks = [];
      incoming.on('data', chunk => { chunks.push(chunk); onData?.(chunk); });
      incoming.on('error', reject);
      incoming.on('end', () => resolve({ status: incoming.statusCode, headers, body: Buffer.concat(chunks) }));
    });
    request.on('error', reject);
  });
  return { result, abort() { response?.destroy(); request.destroy(); } };
}
const json = (response, body) => {
  response.writeHead(200, { 'content-type': 'application/json' }); response.end(JSON.stringify(body));
};

test('TMDB App assembly serves requests and images when the unused Douban provider cannot parse', t => {
  const dataRoot = area(t), entry = join(dataRoot, 'provider-isolation.test.mjs');
  writeFileSync(entry, `
    import test from 'node:test';
    import assert from 'node:assert/strict';
    import { randomUUID } from 'node:crypto';
    import { registerHooks } from 'node:module';
    import { verifyTMDBAssembly } from ${JSON.stringify(new URL('./tmdb-runtime-assembly-support.mjs', import.meta.url).href)};
    const provider = ${JSON.stringify(new URL('../apps/runtime/modules/metadata/douban.mjs', import.meta.url).href)};
    let providerLoads = 0;
    registerHooks({ load(url, context, next) {
      if (url === provider) {
        providerLoads++;
        throw new SyntaxError('DOUBAN_PROVIDER_UNAVAILABLE');
      }
      return next(url, context);
    } });
    test('the public four-module assembly stays independent of the unused provider', async t => {
      const { createAppRuntime, createRuntimeClient, createMusicScriptModule, createCatScriptModule,
        createSourceRuntimeModule, createTMDBDataModule } =
        await import(${JSON.stringify(new URL('../apps/runtime/entry.mjs', import.meta.url).href)});
      const dataRoot = ${JSON.stringify(dataRoot)};
      const identity = { sessionId: randomUUID() };
      const app = createAppRuntime({ ...identity, modules: [
        createMusicScriptModule({ dataRoot }), createCatScriptModule({ dataRoot: dataRoot + '/cat' }),
        createSourceRuntimeModule(), createTMDBDataModule({ dataRoot }),
      ] });
      let client;
      t.after(async () => { await client?.close(); await app.close(); });
      await app.start();
      client = createRuntimeClient({ baseUrl: app.baseUrl });
      const described = await client.describe();
      assert.equal(described.ok, true);
      assert.deepEqual(described.value.modules.map(module => [module.id, module.version, module.contract]), [
        ['music', '0.8.0', 'music.script.v3'], ['cat', '0.2.0', 'cat.script.v2'],
        ['source', '0.2.0', 'source.runtime.v2'], ['tmdb', '0.1.0', 'tmdb.data.v1'],
      ]);
      await verifyTMDBAssembly(t, { client, baseUrl: app.baseUrl });
      for (const method of ['douban.data.settings.get', 'pan.providers'])
        assert.deepEqual(await client.call(method),
          { ok: false, contract: 'runtime.http.v1', code: 'METHOD_NOT_FOUND' });
      assert.equal((await client.shutdown()).ok, true);
      assert.equal(providerLoads, 0);
      await assert.rejects(import(provider), { name: 'SyntaxError', message: 'DOUBAN_PROVIDER_UNAVAILABLE' });
      assert.equal(providerLoads, 1);
    });
  `);
  const env = { ...process.env, NODE_OPTIONS: '', NODE_PATH: '', FORCE_COLOR: '0' };
  delete env.NODE_TEST_CONTEXT;
  const result = spawnSync(process.execPath, ['--test', '--test-reporter=tap', entry], {
    encoding: 'utf8', timeout: 30000, maxBuffer: 1024 * 1024, windowsHide: true, env,
  });
  assert.equal(result.error, undefined, result.error?.message);
  assert.equal(result.signal, null, result.stderr);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.equal(result.stderr, '');
  assert.equal(checkTestSummary(result.stdout, 'TMDB provider isolation'), 1);
});

test('metadata and opt-in pan modules share one HTTP lifecycle session without gaining source upload', async t => {
  const dataRoot = area(t);
  const identity = { sessionId: randomUUID() };
  let panClosed = 0;
  const app = createAppRuntime({ ...identity, modules: [
    createPanRuntimeModule({ dataRoot, transport: {
      request() { assert.fail('unexpected pan upstream'); }, close() { panClosed++; },
    } }),
    createTMDBDataModule({ dataRoot }), createDoubanDataModule({ dataRoot }),
  ] });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  t.after(async () => { await client.close(); await app.close(); });
  assert.deepEqual(value(await client.describe()).modules.map(module => module.id), ['pan', 'tmdb', 'douban']);
  assert.equal(value(await client.call('pan.providers')).providers.length, 7);
  assert.equal(value(await client.call('tmdb.data.settings.get')).apiKey, '');
  assert.equal(value(await client.call('douban.data.settings.get')).cookie, '');
  for (const id of ['pan', 'tmdb', 'douban']) {
    await assert.rejects(client.upload(id, 'unused', 1, Buffer.from('no')), { code: 'METHOD_NOT_FOUND' });
  }
  assert.equal(value(await client.shutdown()).cleanupComplete, true);
  assert.equal(panClosed, 1);
});

test('coassembled pan failures retain the pan contract without disrupting metadata reads', async t => {
  const dataRoot = area(t);
  const identity = { sessionId: randomUUID() };
  const app = createAppRuntime({ ...identity, modules: [
    { id: 'pan', version: '0.1.0', contract: 'pan.runtime.v1', close() {}, methods: {
      'pan.providers'() { throw new Error('not a public error'); },
      'pan.settings.get'() { return { ok: false, contract: 'pan.runtime.v1', code: 'NOT_A_PUBLIC_CODE' }; },
    } },
    createTMDBDataModule({ dataRoot }), createDoubanDataModule({ dataRoot }),
  ] });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  t.after(async () => { await client.close(); await app.close(); });
  for (const method of ['pan.providers', 'pan.settings.get']) {
    assert.deepEqual(await client.call(method), { ok: false, contract: 'pan.runtime.v1', code: 'PAN_FAILURE' });
  }
  assert.equal(value(await client.call('tmdb.data.settings.get')).apiKey, '');
  assert.equal(value(await client.call('douban.data.settings.get')).cookie, '');
});

test('metadata modules expose read-only operations without script upload or changing source data', async t => {
  const observed = [];
  const base = await upstream(t, (request, response) => {
    observed.push({ url: request.url, headers: request.headers });
    if (request.url.startsWith('/tmdb/3/search/multi')) json(response, {
      page: 1, total_pages: 2, total_results: 21,
      results: [{ id: 42, media_type: 'movie', title: '示例', vote_average: 8,
        poster_path: '/sample.jpg', backdrop_path: null }],
    });
    else if (request.url === '/douban/rexxar/api/v2/movie/123') json(response, {
      id: '123', title: '豆瓣示例', rating: { value: 8.4, max: 10 },
      pic: { normal: 'https://img9.doubanio.com/view/photo/s_ratio_poster/public/p123.jpg' },
    });
    else { response.writeHead(404); response.end(); }
  });
  const owner = await runtime(t, area(t));
  const description = value(await owner.client.describe());
  assert.deepEqual(description.modules.map(module => module.id), ['tmdb', 'douban']);
  assert.deepEqual(description.modules[0].methods, [
    'tmdb.data.request', 'tmdb.data.settings.get', 'tmdb.data.settings.update',
  ]);
  await assert.rejects(owner.client.upload('tmdb', 'unused', 1, Buffer.from('no')), { code: 'METHOD_NOT_FOUND' });
  value(await owner.client.call('tmdb.data.settings.update', { apiBase: base + '/tmdb/3', apiKey: 'test-api-key' }));
  const tmdb = value(await owner.client.call('tmdb.data.request', {
    path: '/3/search/multi', query: { query: '示例', page: 1 }, imageSize: 'w500',
  }));
  assert.deepEqual(tmdb, { page: 1, total_pages: 2, total_results: 21, results: [{
    id: 42, media_type: 'movie', title: '示例', vote_average: 8,
    poster_path: '/v1/tmdb/images/w500/sample.jpg', backdrop_path: null,
  }] });
  assert.equal(new URL(observed[0].url, base).searchParams.get('api_key'), 'test-api-key');
  value(await owner.client.call('douban.data.settings.update', { apiBase: base + '/douban', cookie: 'test_cookie=value' }));
  const douban = value(await owner.client.call('douban.data.request', { path: '/rexxar/api/v2/movie/123' }));
  assert.deepEqual(douban, { id: '123', title: '豆瓣示例', rating: { value: 8.4, max: 10 },
    pic: { normal: '/v1/douban/images/view/photo/s_ratio_poster/public/p123.jpg' } });
  assert.equal(observed[1].headers.cookie, 'test_cookie=value');
  assert.equal(JSON.stringify([tmdb, douban]).includes('test_cookie'), false);
});

test('runtime persists readable settings and credentials, preserves omitted fields and clears empty text', async t => {
  const dataRoot = area(t), seen = [];
  const base = await upstream(t, (request, response) => { seen.push(request.headers); json(response, { id: 42 }); });
  const first = await runtime(t, dataRoot);
  const saved = value(await first.client.call('tmdb.data.settings.update', {
    apiBase: base, accessToken: 'test-access-token',
  }));
  assert.equal(saved.accessToken, 'test-access-token');
  value(await first.client.call('tmdb.data.settings.update', { imageBase: base + '/images' }));
  value(await first.client.call('douban.data.settings.update', { apiBase: base, cookie: 'test_persistent_cookie=ok' }));
  await first.close();
  const next = await runtime(t, dataRoot);
  assert.equal(value(await next.client.call('tmdb.data.settings.get')).accessToken, 'test-access-token');
  value(await next.client.call('tmdb.data.request', { path: '/3/movie/42' }));
  assert.equal(seen[0].authorization, 'Bearer test-access-token');
  value(await next.client.call('douban.data.request', { path: '/rexxar/api/v2/movie/42' }));
  assert.equal(seen[1].cookie, 'test_persistent_cookie=ok');
  assert.equal(value(await next.client.call('douban.data.settings.get')).cookie, 'test_persistent_cookie=ok');
  const bytes = readFileSync(join(dataRoot, 'tmdb/settings.json'), 'utf8');
  assert.equal(JSON.parse(bytes).settings.accessToken, 'test-access-token');
  if (process.platform !== 'win32') assert.equal(statSync(join(dataRoot, 'tmdb/settings.json')).mode & 0o777, 0o600);
  value(await next.client.call('tmdb.data.settings.update', { accessToken: '' }));
  assert.equal(value(await next.client.call('tmdb.data.settings.get')).accessToken, '');
  assert.equal(JSON.parse(readFileSync(join(dataRoot, 'tmdb/settings.json'), 'utf8')).settings.accessToken, '');
});

test('metadata settings expose a restart-stable configuration identity only for effective changes', async t => {
  const root = area(t), settings = createMetadataSettings(root, 'tmdb');
  const initial = settings.get();
  assert.match(initial.revision, /^[0-9a-f]{64}$/);
  assert.equal(settings.get().revision, initial.revision);
  assert.equal(settings.update({}).revision, initial.revision);
  assert.equal(settings.update({ apiBase: initial.apiBase + '/' }).revision, initial.revision);
  const first = settings.update({ accessToken: 'first-token' });
  assert.notEqual(first.revision, initial.revision);
  assert.equal(settings.update({ accessToken: ' first-token ' }).revision, first.revision);
  const next = settings.update({ accessToken: 'next-token' });
  assert.notEqual(next.revision, first.revision);
  assert.equal(next.accessToken, 'next-token');
  assert.equal(first.accessToken, 'first-token');
  assert.throws(() => settings.update({ accessToken: 'bad\nvalue' }), { code: 'INVALID_REQUEST' });
  assert.equal(settings.get().revision, next.revision);
  const persisted = JSON.parse(readFileSync(join(root, 'tmdb/settings.json'), 'utf8'));
  assert.deepEqual(Object.keys(persisted).sort(), ['settings', 'version']);
  assert.equal(Object.hasOwn(persisted.settings, 'revision'), false);
  assert.equal(createMetadataSettings(root, 'tmdb').get().revision, next.revision);
  assert.equal(createMetadataSettings(area(t), 'tmdb').get().revision, initial.revision);
});

test('Douban HTML search goes through the public operation without inventing TMDB pagination', async t => {
  let observed;
  const base = await upstream(t, (request, response) => {
    observed = { url: request.url, headers: request.headers };
    response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    response.end('<html><script>window.__DATA__ = ' + JSON.stringify({
      total: 1, start: 0, count: 1, text: '示例',
      items: [{ id: 123, title: '示例 (2024)', url: 'https://movie.douban.com/subject/123/',
        cover_url: 'https://img3.doubanio.com/view/photo/s_ratio_poster/public/p123.jpg',
        abstract: '影视信息', labels: [{ text: '电影' }],
        rating: { count: 10, value: 8.4, star_count: 4 } }],
    }) + '; window.__USER__ = {};</script></html>');
  });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('douban.data.settings.update', { searchBase: base + '/search', cookie: 'test_only=ok' }));
  const result = value(await owner.client.call('douban.data.search', { query: '示例' }));
  assert.equal(result.subjects.items[0].target.id, '123');
  assert.equal(result.subjects.items[0].target.title, '示例');
  assert.equal(result.subjects.items[0].target.cover_url, '/v1/douban/images/view/photo/s_ratio_poster/public/p123.jpg');
  assert.equal(result.subjects.items[0].target.rating.max, 10);
  assert.equal(Object.hasOwn(result, 'results'), false);
  assert.equal(Object.hasOwn(result, 'total_pages'), false);
  assert.equal(new URL(observed.url, base).pathname, '/search/movie/subject_search');
  assert.equal(new URL(observed.url, base).searchParams.get('search_text'), '示例');
  assert.equal(observed.headers.cookie, 'test_only=ok');
});

test('invalid settings and damaged documents fail without rewriting previous persisted bytes', async t => {
  const root = area(t), settings = createMetadataSettings(root, 'douban');
  settings.update({ cookie: 'test-only', apiProxy: 'https://relay.invalid/?url={encodedUrl}' });
  const path = join(root, 'douban/settings.json'), original = readFileSync(path);
  for (const patch of [
    { unexpected: true }, { cookie: 'secret\r\nInjected: yes' }, { apiBase: 'file:///tmp' },
    { apiProxy: 'https://{url}/' }, { imageProxy: 'https://relay.invalid/{unknown}' },
    { apiBase: 'https://example.invalid/?api_key=secret' },
    { apiBase: 'https://example.invalid/?' }, { imageBase: 'https://example.invalid/#' },
    { apiProxy: 'https://relay.invalid/?url={encodedUrl}#' },
  ]) assert.throws(() => settings.update(patch), { code: 'INVALID_REQUEST' });
  assert.deepEqual(readFileSync(path), original);
  const corrupt = Buffer.from('{"version":1,"settings":');
  writeFileSync(path, corrupt);
  const restarted = createMetadataSettings(root, 'douban');
  assert.throws(() => restarted.get(), { code: 'CORRUPT_STORAGE' });
  assert.throws(() => restarted.update({ cookie: '' }), { code: 'CORRUPT_STORAGE' });
  assert.deepEqual(readFileSync(path), corrupt);
});

test('metadata settings restart preserves valid UTF-8 and BOM but rejects malformed bytes without rewriting', t => {
  const root = area(t);
  for (const [id, secret] of [['tmdb', 'apiKey'], ['douban', 'cookie']]) {
    const store = createMetadataSettings(root, id);
    store.update({ [secret]: '合成🎬\ufffd' });
    const path = join(root, id, 'settings.json'), original = readFileSync(path);
    for (const bytes of [original, Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), original])]) {
      writeFileSync(path, bytes);
      const restarted = createMetadataSettings(root, id);
      assert.equal(restarted.read()[secret], '合成🎬\ufffd');
      assert.equal(restarted.get()[secret], '合成🎬\ufffd');
      assert.deepEqual(readFileSync(path), bytes, 'Reading must not rewrite valid persisted bytes');
    }
    const document = JSON.parse(original);
    document.settings[secret] = 'UTF8_MARKER';
    const template = Buffer.from(JSON.stringify(document));
    const start = template.indexOf('UTF8_MARKER');
    for (const invalid of [[0x80], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82]]) {
      const bytes = Buffer.concat([template.subarray(0, start), Buffer.from(invalid),
        template.subarray(start + 'UTF8_MARKER'.length)]);
      writeFileSync(path, bytes);
      const restarted = createMetadataSettings(root, id);
      assert.throws(() => restarted.get(), { code: 'CORRUPT_STORAGE' });
      assert.throws(() => restarted.update({ [secret]: '' }), { code: 'CORRUPT_STORAGE' });
      assert.deepEqual(readFileSync(path), bytes, 'Invalid UTF-8 cannot be repaired or replaced silently');
    }
  }
});

test('both sources retain stable image identity across proxy change and runtime restart', async t => {
  const root = area(t), seen = [];
  const imageBase = await upstream(t, (request, response) => {
    seen.push({ url: request.url, headers: request.headers });
    response.writeHead(200, { 'content-type': 'image/jpeg' }); response.end(Buffer.from([0xff, 0xd8, 1, 0xff, 0xd9]));
  });
  const first = await runtime(t, root);
  value(await first.client.call('tmdb.data.settings.update', { imageBase: imageBase + '/old', accessToken: 'never-image-token' }));
  value(await first.client.call('douban.data.settings.update', { imageBase: imageBase + '/db-old', cookie: 'never-image-cookie' }));
  const tmdbPath = '/v1/tmdb/images/w500/sample.jpg';
  const doubanPath = '/v1/douban/images/view/photo/s_ratio_poster/public/p123.jpg';
  assert.equal((await image(first, tmdbPath).result).status, 200);
  assert.equal((await image(first, doubanPath).result).status, 200);
  value(await first.client.call('tmdb.data.settings.update', { imageBase: imageBase + '/new' }));
  value(await first.client.call('douban.data.settings.update', { imageBase: imageBase + '/db-new' }));
  await first.close();
  const next = await runtime(t, root);
  const binary = await image(next, tmdbPath).result;
  assert.equal(binary.headers['content-type'], 'image/jpeg');
  assert.equal(binary.headers.location, undefined);
  assert.deepEqual(binary.body, Buffer.from([0xff, 0xd8, 1, 0xff, 0xd9]));
  assert.equal((await image(next, doubanPath).result).status, 200);
  assert.deepEqual(seen.map(item => item.url), ['/old/t/p/w500/sample.jpg',
    '/db-old/view/photo/s_ratio_poster/public/p123.jpg', '/new/t/p/w500/sample.jpg',
    '/db-new/view/photo/s_ratio_poster/public/p123.jpg']);
  for (const item of seen) {
    assert.equal(item.headers.authorization, undefined);
    assert.equal(item.headers.cookie, undefined);
  }
  const count = seen.length;
  const publicImage = await image(next, tmdbPath).result;
  assert.equal(publicImage.status, 200);
  assert.deepEqual(publicImage.body, binary.body);
  assert.equal(publicImage.headers['access-control-allow-origin'], '*');
  assert.equal(seen.length, count + 1);
});

test('configuration stays local and HTTP redirects preserve the explicitly configured API headers', async t => {
  const received = [];
  const other = await upstream(t, (request, response) => {
    received.push(request.headers);
    json(response, { id: 1 });
  });
  const base = await upstream(t, (request, response) => {
    if (request.url.startsWith('/3/configuration')) json(response, {
      images: { base_url: 'http://external.invalid/', secure_base_url: 'https://external.invalid/',
        poster_sizes: ['w500', 'original'] },
    });
    else if (request.url.startsWith('/3/movie/1')) {
      response.writeHead(302, { location: other + '/captured' }); response.end();
    } else { response.writeHead(403, { 'content-type': 'text/html' }); response.end('secret error body'); }
  });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('tmdb.data.settings.update', {
    apiBase: base, imageBase: 'https://custom.invalid/tmdb-img', accessToken: 'sensitive-test-token',
  }));
  const configuration = value(await owner.client.call('tmdb.data.request', { path: '/3/configuration' }));
  assert.deepEqual(configuration.images, { base_url: '/v1/tmdb/images/', secure_base_url: '/v1/tmdb/images/',
    poster_sizes: ['w500', 'original'] });
  assert.equal(value(await owner.client.call('tmdb.data.settings.get')).imageBase, 'https://custom.invalid/tmdb-img');
  assert.deepEqual(value(await owner.client.call('tmdb.data.request', { path: '/3/movie/1' })), { id: 1 });
  assert.equal(received.length, 1);
  assert.equal(received[0].authorization, 'Bearer sensitive-test-token');
  assert.deepEqual(await owner.client.call('tmdb.data.request', { path: '/3/movie/2' }), {
    ok: false, contract: 'tmdb.data.v1', code: 'UPSTREAM_HTTP',
  });
});

test('metadata persists HTTP URL userinfo and the real transport sends its Basic credentials', async t => {
  const received = [];
  const base = await upstream(t, (request, response) => {
    received.push(request.headers.authorization);
    json(response, { id: '1' });
  });
  const endpoint = new URL(base);
  endpoint.username = 'fixture-user';
  endpoint.password = 'fixture-password';
  const root = area(t), settings = createMetadataSettings(root, 'douban');
  settings.update({ apiBase: endpoint.href, imageBase: endpoint.href });
  assert.equal(new URL(createMetadataSettings(root, 'douban').read().apiBase).username, 'fixture-user');
  const owner = await runtime(t, root);
  assert.deepEqual(value(await owner.client.call('douban.data.request', { path: '/rexxar/api/v2/movie/1' })), { id: '1' });
  assert.deepEqual(received, ['Basic ' + Buffer.from('fixture-user:fixture-password').toString('base64')]);
});

test('TMDB keeps server-compatible multi-level API and image prefixes through restart', async t => {
  const seen = [], bytes = Buffer.from([0xff, 0xd8, 1, 0xff, 0xd9]);
  const base = await upstream(t, (request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    seen.push(path);
    if (path === '/mount/pan/tmdb/3/configuration') json(response, {
      images: { base_url: 'http://image.tmdb.org/t/p/', secure_base_url: 'https://image.tmdb.org/t/p/',
        poster_sizes: ['w342', 'original'] },
    });
    else if (path === '/mount/pan/tmdb/3/movie/42') json(response, { id: 42, poster_path: '/sample.jpg' });
    else if (['/mount/pan/tmdb-img/t/p/w342/sample.jpg', '/next/proxy/tmdb-img/t/p/w342/sample.jpg']
      .includes(path)) {
      response.writeHead(200, { 'content-type': 'image/jpeg' }); response.end(bytes);
    } else { response.writeHead(404); response.end(); }
  });
  const root = area(t), first = await runtime(t, root);
  value(await first.client.call('tmdb.data.settings.update', {
    apiBase: base + '/mount/pan/tmdb/3/', imageBase: base + '/mount/pan/tmdb-img/',
  }));
  const configuration = value(await first.client.call('tmdb.data.request', { path: '/3/configuration' }));
  assert.deepEqual(configuration.images, { base_url: '/v1/tmdb/images/', secure_base_url: '/v1/tmdb/images/',
    poster_sizes: ['w342', 'original'] });
  const movie = value(await first.client.call('tmdb.data.request', { path: '/3/movie/42', imageSize: 'w342' }));
  assert.deepEqual(movie, { id: 42, poster_path: '/v1/tmdb/images/w342/sample.jpg' });
  assert.deepEqual((await image(first, movie.poster_path).result).body, bytes);
  value(await first.client.call('tmdb.data.settings.update', { imageBase: base + '/next/proxy/tmdb-img/t/p' }));
  await first.close();
  const next = await runtime(t, root);
  assert.deepEqual(value(await next.client.call('tmdb.data.request', { path: '/3/movie/42', imageSize: 'w342' })), movie);
  assert.deepEqual((await image(next, movie.poster_path).result).body, bytes);
  assert.deepEqual(seen, ['/mount/pan/tmdb/3/configuration', '/mount/pan/tmdb/3/movie/42',
    '/mount/pan/tmdb-img/t/p/w342/sample.jpg', '/mount/pan/tmdb/3/movie/42',
    '/next/proxy/tmdb-img/t/p/w342/sample.jpg']);
});

test('image streams deliver early bytes and shutdown closes the real upstream connection', async t => {
  let confirmClose;
  const drained = new Promise(done => { confirmClose = done; });
  const base = await upstream(t, (_request, response) => {
    response.on('close', confirmClose);
    response.writeHead(200, { 'content-type': 'image/png' });
    response.write(Buffer.from([137, 80, 78, 71]));
    // Deliberately never complete. Shutdown must abort the actual stream.
  });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('tmdb.data.settings.update', { imageBase: base }));
  let firstChunk;
  const started = new Promise(done => { firstChunk = done; });
  const transfer = image(owner, '/v1/tmdb/images/original/example.png', { onData: firstChunk });
  const stopped = transfer.result.catch(() => {});
  await started;
  await owner.app.close();
  await drained;
  await stopped;
  assert.equal(owner.app.state.active, 0);
  assert.equal(owner.app.state.closed, true);
});

test('image errors stay JSON failures and cannot be used as successful image bodies', async t => {
  const base = await upstream(t, (request, response) => {
    if (request.url.includes('html')) { response.writeHead(200, { 'content-type': 'text/html' }); response.end('secret challenge'); }
    else { response.writeHead(404); response.end('private error'); }
  });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('tmdb.data.settings.update', { imageBase: base }));
  for (const [name, code] of [['html.jpg', 'UPSTREAM_FORMAT'], ['missing.jpg', 'UPSTREAM_HTTP']]) {
    const reply = await image(owner, '/v1/tmdb/images/original/' + name).result;
    assert.equal(reply.status, 502);
    assert.equal(reply.headers['content-type'], 'application/json');
    assert.deepEqual(JSON.parse(reply.body), { ok: false, contract: 'tmdb.data.v1', code });
  }
});

test('image redirects are resolved by the runtime without API credentials or an external Location', async t => {
  let received;
  const bytes = Buffer.from([0x52, 0x49, 0x46, 0x46, 4, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]);
  const target = await upstream(t, (request, response) => {
    received = request.headers;
    response.writeHead(200, { 'content-type': 'image/webp' }); response.end(bytes);
  });
  const redirect = await upstream(t, (_request, response) => {
    response.writeHead(302, { location: target + '/final.webp' }); response.end();
  });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('douban.data.settings.update', { imageBase: redirect, cookie: 'should_not_leak=ok' }));
  const result = await image(owner, '/v1/douban/images/view/photo/public/p1.webp').result;
  assert.equal(result.status, 200);
  assert.equal(result.headers.location, undefined);
  assert.deepEqual(result.body, bytes);
  assert.equal(received.cookie, undefined);
  assert.equal(received.authorization, undefined);
});

test('mislabeled JSON and HTML are rejected before any image response while SVG bytes remain intact', async t => {
  const svg = Buffer.from('<?xml version="1.0"?><!-- image --><!DOCTYPE svg [<!ENTITY sample "inert>text">]>' +
    '<svg xmlns="http://www.w3.org/2000/svg"/>');
  const base = await upstream(t, (request, response) => {
    response.writeHead(200, { 'content-type': request.url.endsWith('.svg') ? 'image/svg+xml' : 'image/jpeg' });
    response.end(request.url.endsWith('.svg') ? svg : request.url.includes('json')
      ? ' \n{"error":"private-denied"}' : '<html>private-challenge</html>');
  });
  const owner = await runtime(t, area(t));
  for (const source of ['tmdb', 'douban']) {
    value(await owner.client.call(`${source}.data.settings.update`, { imageBase: base }));
    for (const name of ['json.jpg', 'html.jpg']) {
      const path = source === 'tmdb' ? `/v1/tmdb/images/original/${name}` : `/v1/douban/images/view/photo/${name}`;
      const reply = await image(owner, path).result;
      assert.equal(reply.status, 502);
      assert.deepEqual(JSON.parse(reply.body), { ok: false, contract: `${source}.data.v1`, code: 'UPSTREAM_FORMAT' });
    }
  }
  for (const path of ['/v1/tmdb/images/original/example.svg', '/v1/douban/images/view/photo/example.svg']) {
    const result = await image(owner, path).result;
    assert.equal(result.status, 200);
    assert.deepEqual(result.body, svg);
  }
});

test('incomplete image preambles are bounded and close the upstream without waiting for its body', async t => {
  let closed;
  const drained = new Promise(done => { closed = done; });
  const base = await upstream(t, (_request, response) => {
    response.on('close', closed);
    response.writeHead(200, { 'content-type': 'image/svg+xml' });
    response.write('<!--' + 'x'.repeat(4096));
    // No tail or end. The bounded probe must reject and close this connection.
  });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('tmdb.data.settings.update', { imageBase: base }));
  const result = await image(owner, '/v1/tmdb/images/original/example.svg').result;
  assert.equal(result.status, 502);
  assert.deepEqual(JSON.parse(result.body), { ok: false, contract: 'tmdb.data.v1', code: 'UPSTREAM_FORMAT' });
  await drained;
  await owner.app.close();
  assert.equal(owner.app.state.active, 0);
});

test('short incomplete image preambles reach EOF and fail without waiting for cancellation', async t => {
  const preambles = ['', ' \t\r\n', '<!-- incomplete', '<?xml version="1.0"', '<svg', '<!DOCTYPE svg ['];
  const base = await upstream(t, (request, response) => {
    const index = Number(/\/(\d+)\.svg$/.exec(request.url)[1]);
    response.writeHead(200, { 'content-type': 'image/svg+xml' });
    response.end(preambles[index]);
  });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('tmdb.data.settings.update', { imageBase: base }));
  for (let index = 0; index < preambles.length; index++) {
    const result = await image(owner, `/v1/tmdb/images/original/${index}.svg`).result;
    assert.equal(result.status, 502);
    assert.deepEqual(JSON.parse(result.body), { ok: false, contract: 'tmdb.data.v1', code: 'UPSTREAM_FORMAT' });
  }
  await owner.app.close();
  assert.equal(owner.app.state.active, 0);
});

test('image cancellation before response headers closes the pending upstream request', async t => {
  let start, finish;
  const started = new Promise(done => { start = done; }), drained = new Promise(done => { finish = done; });
  const base = await upstream(t, (_request, response) => { response.on('close', finish); start(); });
  const owner = await runtime(t, area(t));
  value(await owner.client.call('tmdb.data.settings.update', { imageBase: base }));
  const transfer = image(owner, '/v1/tmdb/images/w500/pending.jpg');
  const stopped = transfer.result.catch(() => {});
  await started;
  transfer.abort();
  await stopped;
  await drained;
  await owner.app.close();
  assert.equal(owner.app.state.active, 0);
});

for (const [id, factory, path] of [
  ['tmdb', createTMDBDataModule, '/3/movie/42'],
  ['douban', createDoubanDataModule, '/rexxar/api/v2/movie/42'],
]) test(`${id} module close waits for admitted metadata work and rejects new calls`, async t => {
  let enter, release;
  const entered = new Promise(done => { enter = done; });
  const pending = new Promise(done => { release = done; });
  const module = factory({ dataRoot: area(t), request: async (url, { signal }) => {
    enter({ url, signal }); await pending;
    return { status: 200, headers: {}, body: Buffer.from('{"id":42}') };
  } });
  value(await module.methods[id + '.data.settings.update']({ apiBase: 'http://metadata.invalid/initial' }));
  const work = module.methods[id + '.data.request']({ path });
  value(await module.methods[id + '.data.settings.update']({ apiBase: 'http://metadata.invalid/next' }));
  const { url, signal } = await entered;
  assert.equal(url, 'http://metadata.invalid/initial' + (id === 'tmdb' ? '/movie/42?language=zh-CN' : path));
  let closed = false;
  const closing = module.close().then(() => { closed = true; });
  await new Promise(done => setImmediate(done));
  assert.equal(signal.aborted, true);
  assert.equal(closed, false);
  assert.deepEqual(await module.methods[id + '.data.settings.get']({}), {
    ok: false, contract: id + '.data.v1', code: 'CLOSED',
  });
  release();
  assert.equal((await work).code, 'CANCELLED');
  await closing;
  assert.equal(closed, true);
});

test('a stream cleanup failure cannot later be reported as cleanupComplete by runtime shutdown', async t => {
  const identity = { sessionId: randomUUID() };
  let released;
  const disposed = new Promise(done => { released = done; });
  const app = createAppRuntime({ ...identity, modules: [{
    id: 'tmdb', version: '0.1.0', contract: 'tmdb.data.v1', methods: {},
    image() {
      return { status: 200, headers: { 'content-type': 'image/png' }, body: Readable.from([Buffer.from('image')]),
        async dispose() { released(); throw new Error('synthetic private cleanup failure'); } };
    },
    async close() {},
  }] });
  await app.start();
  const client = createRuntimeClient({ baseUrl: app.baseUrl });
  t.after(async () => { await client.close(); await app.close().catch(() => {}); });
  await image({ app }, '/v1/tmdb/images/original/sample.png').result.catch(() => {});
  await disposed;
  assert.deepEqual(await client.shutdown(), { ok: false, contract: 'runtime.http.v1', code: 'INVALID_RESPONSE' });
  await assert.rejects(app.close(), { code: 'INVALID_RESPONSE' });
  assert.equal(app.state.active, 0);
});
test('metadata settings persist effective TMDB options and accept plain Douban proxy prefixes', t => {
  const root = area(t), tmdb = createMetadataSettings(root, 'tmdb');
  const first = tmdb.get();
  const saved = tmdb.update({ apiBase: '', imageBase: '', language: 'en-US', region: 'US', includeAdult: true, apiKey: 'fixture.jwt.token' });
  assert.equal(saved.apiBase, 'https://api.themoviedb.org/3');
  assert.equal(saved.imageBase, 'https://image.tmdb.org');
  assert.notEqual(saved.revision, first.revision);
  assert.deepEqual(createMetadataSettings(root, 'tmdb').get(), saved);
  assert.equal(tmdb.update({ apiKey: '' }).apiKey, '');
  for (const patch of [{ includeAdult: 'false' }, { language: null }, { region: 3 }])
    assert.throws(() => tmdb.update(patch), { code: 'INVALID_REQUEST' });
  const douban = createMetadataSettings(root, 'douban');
  assert.equal(douban.get().apiProxy, ''); assert.equal(douban.get().imageProxy, '');
  for (const prefix of ['https://relay.test/mount/proxy', 'https://relay.test/mount/proxy?url=']) {
    const stored = douban.update({ apiProxy: prefix, imageProxy: prefix, cookie: 'synthetic=ok' });
    assert.equal(stored.apiProxy, prefix); assert.equal(stored.imageProxy, prefix);
    assert.deepEqual(createMetadataSettings(root, 'douban').get(), stored);
  }
  assert.equal(douban.update({ cookie: '' }).cookie, '');
});
