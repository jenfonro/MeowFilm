import test from 'node:test';
import assert from 'node:assert/strict';
import http, { createServer } from 'node:http';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync, rmSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { Readable } from 'node:stream';
import { createAppRuntime, createTMDBDataModule, createDoubanDataModule } from '../apps/runtime/entry.mjs';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { createMetadataSettings } from '../apps/runtime/modules/metadata/settings.mjs';

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
  const identity = { sessionId: randomUUID(), requestKey: randomBytes(32).toString('hex') };
  const modules = [createTMDBDataModule({ dataRoot, ...options }),
    createDoubanDataModule({ dataRoot, ...options })];
  const app = createAppRuntime({ ...identity, modules });
  await app.start();
  const client = createRuntimeClient({ ...identity, baseUrl: app.baseUrl });
  async function close() { await client.close(); await app.close(); }
  t.after(close);
  return { app, client, identity, modules, close };
}
function value(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  return reply.value;
}
function image(owner, path, { authenticated = true, onData } = {}) {
  let request, response, headers;
  const result = new Promise((resolve, reject) => {
    const requestId = randomUUID();
    request = http.get(owner.app.baseUrl + path, { agent: false, headers: authenticated ? {
      authorization: `Bearer ${owner.identity.requestKey}`,
      'x-meow-session': owner.identity.sessionId, 'x-meow-request-id': requestId,
    } : {} }, incoming => {
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
  value(await owner.client.call('tmdb.data.settings.update', { apiBase: base + '/tmdb', apiKey: 'test-api-key' }));
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

test('runtime persists settings and credentials independently, redacts reads, preserves then clears secrets', async t => {
  const dataRoot = area(t), seen = [];
  const base = await upstream(t, (request, response) => { seen.push(request.headers); json(response, { id: 42 }); });
  const first = await runtime(t, dataRoot);
  const saved = value(await first.client.call('tmdb.data.settings.update', {
    apiBase: base, accessToken: 'test-access-token',
  }));
  assert.equal(saved.accessTokenConfigured, true);
  assert.equal(Object.hasOwn(saved, 'accessToken'), false);
  value(await first.client.call('tmdb.data.settings.update', { imageBase: base + '/images' }));
  value(await first.client.call('douban.data.settings.update', { apiBase: base, cookie: 'test_persistent_cookie=ok' }));
  await first.close();
  const next = await runtime(t, dataRoot);
  assert.equal(value(await next.client.call('tmdb.data.settings.get')).accessTokenConfigured, true);
  value(await next.client.call('tmdb.data.request', { path: '/3/movie/42' }));
  assert.equal(seen[0].authorization, 'Bearer test-access-token');
  value(await next.client.call('douban.data.request', { path: '/rexxar/api/v2/movie/42' }));
  assert.equal(seen[1].cookie, 'test_persistent_cookie=ok');
  assert.equal(value(await next.client.call('douban.data.settings.get')).cookieConfigured, true);
  const bytes = readFileSync(join(dataRoot, 'tmdb/settings.json'), 'utf8');
  assert.equal(JSON.parse(bytes).settings.accessToken, 'test-access-token');
  if (process.platform !== 'win32') assert.equal(statSync(join(dataRoot, 'tmdb/settings.json')).mode & 0o777, 0o600);
  value(await next.client.call('tmdb.data.settings.update', { accessToken: '' }));
  assert.equal(value(await next.client.call('tmdb.data.settings.get')).accessTokenConfigured, false);
  assert.equal(JSON.parse(readFileSync(join(dataRoot, 'tmdb/settings.json'), 'utf8')).settings.accessToken, '');
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
    { imageBase: 'https://user:password@example.invalid' },
    { apiProxy: 'https://{url}/' }, { imageProxy: 'https://relay.invalid/no-placeholder' },
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
  assert.deepEqual(seen.map(item => item.url), ['/old/w500/sample.jpg',
    '/db-old/view/photo/s_ratio_poster/public/p123.jpg', '/new/w500/sample.jpg',
    '/db-new/view/photo/s_ratio_poster/public/p123.jpg']);
  for (const item of seen) {
    assert.equal(item.headers.authorization, undefined);
    assert.equal(item.headers.cookie, undefined);
  }
  const count = seen.length;
  assert.equal((await image(next, tmdbPath, { authenticated: false }).result).status, 401);
  assert.equal(seen.length, count);
});

test('configuration is local, failures are fixed, and redirects never disclose API credentials', async t => {
  let leaked = false;
  const other = await upstream(t, (_request, response) => { leaked = true; json(response, {}); });
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
  assert.deepEqual(await owner.client.call('tmdb.data.request', { path: '/3/movie/1' }), {
    ok: false, contract: 'tmdb.data.v1', code: 'UPSTREAM_HTTP',
  });
  assert.equal(leaked, false);
  assert.deepEqual(await owner.client.call('tmdb.data.request', { path: '/3/movie/2' }), {
    ok: false, contract: 'tmdb.data.v1', code: 'UPSTREAM_HTTP',
  });
});

test('TMDB keeps independent multi-level API and GoProxy image prefixes through restart', async t => {
  const seen = [], bytes = Buffer.from([0xff, 0xd8, 1, 0xff, 0xd9]);
  const base = await upstream(t, (request, response) => {
    seen.push(request.url);
    if (request.url === '/mount/pan/tmdb/3/configuration') json(response, {
      images: { base_url: 'http://image.tmdb.org/t/p/', secure_base_url: 'https://image.tmdb.org/t/p/',
        poster_sizes: ['w342', 'original'] },
    });
    else if (request.url === '/mount/pan/tmdb/3/movie/42') json(response, { id: 42, poster_path: '/sample.jpg' });
    else if (['/mount/pan/tmdb-img/t/p/w342/sample.jpg', '/next/proxy/tmdb-img/t/p/w342/sample.jpg']
      .includes(request.url)) {
      response.writeHead(200, { 'content-type': 'image/jpeg' }); response.end(bytes);
    } else { response.writeHead(404); response.end(); }
  });
  const root = area(t), first = await runtime(t, root);
  value(await first.client.call('tmdb.data.settings.update', {
    apiBase: base + '/mount/pan/tmdb/', imageBase: base + '/mount/pan/tmdb-img/t/p/',
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

test('module close waits for admitted metadata work and rejects new calls', async t => {
  let enter, release;
  const entered = new Promise(done => { enter = done; });
  const pending = new Promise(done => { release = done; });
  const module = createTMDBDataModule({ dataRoot: area(t), request: async (_url, { signal }) => {
    enter(signal); await pending;
    return { status: 200, headers: {}, body: Buffer.from('{"id":42}') };
  } });
  const work = module.methods['tmdb.data.request']({ path: '/3/movie/42' });
  const signal = await entered;
  let closed = false;
  const closing = module.close().then(() => { closed = true; });
  await new Promise(done => setImmediate(done));
  assert.equal(signal.aborted, true);
  assert.equal(closed, false);
  assert.deepEqual(await module.methods['tmdb.data.settings.get']({}), {
    ok: false, contract: 'tmdb.data.v1', code: 'CLOSED',
  });
  release();
  assert.equal((await work).code, 'CANCELLED');
  await closing;
  assert.equal(closed, true);
});

test('a stream cleanup failure cannot later be reported as cleanupComplete by runtime shutdown', async t => {
  const identity = { sessionId: randomUUID(), requestKey: randomBytes(32).toString('hex') };
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
  const client = createRuntimeClient({ ...identity, baseUrl: app.baseUrl });
  t.after(async () => { await client.close(); await app.close().catch(() => {}); });
  await image({ app, identity }, '/v1/tmdb/images/original/sample.png').result.catch(() => {});
  await disposed;
  assert.deepEqual(await client.shutdown(), { ok: false, contract: 'runtime.http.v1', code: 'INVALID_RESPONSE' });
  await assert.rejects(app.close(), { code: 'INVALID_RESPONSE' });
  assert.equal(app.state.active, 0);
});
