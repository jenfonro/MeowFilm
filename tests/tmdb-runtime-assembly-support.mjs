import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { createServer, get } from 'node:http';

// Controlled local upstream only; shared by the actual platform entry tests.
export async function verifyTMDBAssembly(t, { client, baseUrl }) {
  const observed = [];
  const png = Buffer.from(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII=',
    'base64');
  const upstream = createServer((request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    observed.push({ method: request.method, path: url.pathname, query: [...url.searchParams], headers: request.headers });
    if (url.pathname === '/metadata/3/movie/99') {
      response.writeHead(503); response.end('synthetic upstream failure'); return;
    }
    if (url.pathname === '/posters/t/p/w500/fixture.png') {
      response.writeHead(200, { 'content-type': 'image/png', 'content-length': png.length });
      response.end(png); return;
    }
    const value = url.pathname === '/metadata/3/search/multi'
      ? { page: 1, total_pages: 2, total_results: 21, results: [
        { id: 42, media_type: 'movie', title: '示例', poster_path: '/fixture.png' },
      ] } : url.pathname === '/metadata/3/movie/42'
        ? { id: 42, title: '示例', poster_path: '/fixture.png' }
        : url.pathname === '/metadata/3/tv/73'
          ? { id: 73, name: '剧集', poster_path: '/fixture.png' } : undefined;
    response.writeHead(value ? 200 : 404, { 'content-type': 'application/json' });
    response.end(JSON.stringify(value ?? { error: 'unexpected test request' }));
  });
  t.after(() => new Promise(resolve => { upstream.closeAllConnections(); upstream.close(resolve); }));
  await new Promise((resolve, reject) => {
    upstream.once('error', reject); upstream.listen(0, '127.0.0.1', resolve);
  });
  const origin = `http://127.0.0.1:${upstream.address().port}`;
  const okay = reply => {
    assert.equal(reply.ok, true, JSON.stringify(reply));
    assert.equal(reply.contract, 'tmdb.data.v1');
    return reply.value;
  };
  const initial = okay(await client.call('tmdb.data.settings.get'));
  assert.equal(initial.apiKeyConfigured, false);
  assert.equal(initial.accessTokenConfigured, false);
  const settings = okay(await client.call('tmdb.data.settings.update', {
    apiBase: origin + '/metadata', imageBase: origin + '/posters/t/p',
  }));
  assert.deepEqual(settings, {
    apiBase: origin + '/metadata', imageBase: origin + '/posters/t/p',
    apiKeyConfigured: false, accessTokenConfigured: false, revision: settings.revision,
  });
  assert.equal(typeof settings.revision, 'string');
  assert.notEqual(settings.revision, '');
  assert.notEqual(settings.revision, initial.revision);
  assert.deepEqual(okay(await client.call('tmdb.data.settings.update', {
    apiBase: settings.apiBase, imageBase: settings.imageBase,
  })), settings);
  const search = okay(await client.call('tmdb.data.request', {
    path: '/3/search/multi', query: { query: '示例', page: 1, language: 'zh-CN' }, imageSize: 'w500',
  }));
  const imagePath = '/v1/tmdb/images/w500/fixture.png';
  assert.deepEqual(search, { page: 1, total_pages: 2, total_results: 21, results: [
    { id: 42, media_type: 'movie', title: '示例', poster_path: imagePath },
  ] });
  for (const [type, id, title] of [['movie', 42, { title: '示例' }], ['tv', 73, { name: '剧集' }]]) {
    assert.deepEqual(okay(await client.call('tmdb.data.request', {
      path: `/3/${type}/${id}`, query: { language: 'zh-CN' }, imageSize: 'w500',
    })), { id, ...title, poster_path: imagePath });
  }
  assert.deepEqual(await client.call('tmdb.data.request', { path: '/3/movie/99' }),
    { ok: false, contract: 'tmdb.data.v1', code: 'UPSTREAM_HTTP' });
  const requestId = randomUUID();
  const image = await new Promise((resolve, reject) => {
    get(baseUrl + imagePath, { agent: false, headers: {
      'x-meow-request-id': requestId,
    } }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('error', reject);
      response.on('end', () => resolve({
        status: response.statusCode, headers: response.headers, body: Buffer.concat(chunks),
      }));
    }).on('error', reject);
  });
  assert.equal(image.status, 200);
  assert.equal(image.headers['content-type'], 'image/png');
  assert.equal(image.headers['x-meow-request-id'], requestId);
  assert.deepEqual(image.body, png);
  assert.deepEqual(observed.map(item => item.path), [
    '/metadata/3/search/multi', '/metadata/3/movie/42', '/metadata/3/tv/73',
    '/metadata/3/movie/99', '/posters/t/p/w500/fixture.png',
  ]);
  assert.deepEqual(Object.fromEntries(observed[0].query), { query: '示例', page: '1', language: 'zh-CN' });
  assert.deepEqual(observed.slice(1, 3).map(item => Object.fromEntries(item.query)),
    [{ language: 'zh-CN' }, { language: 'zh-CN' }]);
  for (const item of observed) {
    assert.equal(item.method, 'GET');
    assert.equal(item.headers.authorization, undefined);
    assert.equal(item.headers['x-meow-session'], undefined);
    assert.equal(item.headers['x-meow-request-id'], undefined);
  }
  return settings;
}

export async function verifyPanAndDoubanAssembly(client) {
  const douban = await client.call('douban.data.settings.get');
  assert.equal(douban.ok, true);
  assert.equal(douban.contract, 'douban.data.v1');
  assert.equal(typeof douban.value.revision, 'string');
  assert.notEqual(douban.value.revision, '');
  assert.equal(douban.value.cookieConfigured, false);
  const pan = await client.call('pan.providers');
  assert.equal(pan.ok, true);
  assert.equal(pan.contract, 'pan.runtime.v1');
  assert.deepEqual(pan.value.providers.map(row => row.provider).sort(),
    ['baidu', 'quark', 'quark_tv', 'tianyi', 'uc', 'uc_tv', 'yidong']);
}
