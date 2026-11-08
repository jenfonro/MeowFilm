import test from 'node:test';
import assert from 'node:assert/strict';
import { requestDouban, searchDouban, doubanImageTarget, rewriteDoubanImages } from '../apps/runtime/modules/metadata/douban.mjs';

const defaults = Object.freeze({
  apiBase: 'https://m.douban.com', searchBase: 'https://search.douban.com',
  imageBase: 'https://img3.doubanio.com', apiProxy: '', imageProxy: '', cookie: '',
});
const settings = patch => ({ ...defaults, ...patch });
const failure = code => ({ code, message: code });
const reply = (body, status = 200, headers = {}) => ({
  status, headers, body: Buffer.from(typeof body === 'string' ? body : JSON.stringify(body)), url: 'https://m.douban.com/',
});
function upstream(responses) {
  const calls = [];
  const request = async (url, options) => {
    calls.push({ url, ...options });
    const response = responses.shift();
    assert.ok(response, 'unexpected upstream request');
    return typeof response === 'function' ? response(url, options) : response;
  };
  return { request, calls };
}
const html = data => '<!doctype html><html><script>window.__DATA__ = ' +
  JSON.stringify(data) + '; window.__USER__ = null;</script></html>';
const sampleSearch = patch => ({ count: 2, start: 0, total: 3, text: '测试',
  items: [
    { id: 1292052, title: '\u200e肖申克的救赎 (1994)\u2069', url: 'https://movie.douban.com/subject/1292052/',
      cover_url: 'https://img9.doubanio.com/view/photo/s_ratio_poster/public/p480747492.jpg',
      abstract: '1994 / 美国 / 犯罪 剧情', abstract_2: '不应代替 subtitle',
      labels: [{ text: '电影' }, { text: '可播放' }],
      rating: { count: 120, star_count: 4.5, value: 9.7, rating_info: '' } },
    { id: 3541415, title: '白夜追凶（2017）', url: 'https://movie.douban.com/subject/3541415/',
      cover_url: null, abstract: '2017 / 中国大陆 / 犯罪', labels: [{ text: '剧集' }],
      rating: { count: 0, star_count: 0, value: 0, rating_info: '尚未上映' } },
  ], ...patch });

test('Rexxar recent_hot keeps source IDs, rating, pagination and rewrites nested image fields in place', async () => {
  const body = {
    count: 1, start: 20, total: 125,
    items: [{ id: '1292052', title: '肖申克的救赎', rating: { value: 9.7, count: 120 },
      cover_url: 'https://img9.doubanio.com/view/photo/s_ratio_poster/public/p1.jpg',
      pic: { normal: 'https://img3.doubanio.com/view/photo/m/public/p1.jpg', large: null },
      url: 'https://movie.douban.com/subject/1292052/', extra: { original: 'business value' } }],
  };
  const original = structuredClone(body), fixture = upstream([reply(body)]);
  const result = await requestDouban({ path: '/rexxar/api/v2/subject/recent_hot/movie',
    query: { start: 20, limit: 20, category: '热门', type: '全部' } },
  { settings: defaults, request: fixture.request });
  const expected = structuredClone(body);
  expected.items[0].cover_url = '/v1/douban/images/view/photo/s_ratio_poster/public/p1.jpg';
  expected.items[0].pic.normal = '/v1/douban/images/view/photo/m/public/p1.jpg';
  assert.deepEqual(result, expected);
  assert.deepEqual(body, original);
  assert.equal(new URL(fixture.calls[0].url).pathname, '/rexxar/api/v2/subject/recent_hot/movie');
  assert.equal(new URL(fixture.calls[0].url).searchParams.get('category'), '热门');
  assert.equal(fixture.calls[0].headers.referer, 'https://m.douban.com/');
  assert.equal(fixture.calls[0].headers.origin, 'https://m.douban.com');
  assert.equal(fixture.calls[0].method, 'GET');
  assert.equal(fixture.calls[0].followRedirect, false);
  assert.equal(Object.hasOwn(result, 'cache_status'), false);
});

test('Rexxar movie/tv details, cast, image and related collections use explicit read-only scopes', async () => {
  const paths = [
    'subject/recent_hot/tv', 'movie/categories', 'tv/categories', 'movie/showing', 'tv/showing', 'movie/recommend', 'tv/recommend',
    'movie/1292052', 'tv/3541415', 'movie/1292052/celebrities', 'tv/3541415/credits',
    'movie/1292052/photos', 'tv/3541415/recommendations', 'movie/1292052/recommend',
    'movie/1292052/trailers', 'movie/1292052/videos', 'tv/3541415/episodes',
    'celebrity/1054521', 'celebrity/1054521/works', 'celebrity/1054521/photos',
    'subject_collection/movie_hot_gaia', 'subject_collection/movie_top250/items',
  ];
  const fixture = upstream(paths.map(() => reply({
    id: '1292052', rating: { value: 9.7 }, null_field: null,
    credits: [{ avatar: { large: 'https://img1.douban.com/icon/u1.jpg', small: '' },
      uri: 'douban://douban.com/celebrity/1054521' }],
    photos: [{ image: { large: { url: 'https://img.doubanio.com/view/photo/l/public/p2.webp', width: 100 } } }],
  })));
  for (const path of paths) {
    const result = await requestDouban({ path: '/rexxar/api/v2/' + path },
      { settings: defaults, request: fixture.request });
    assert.equal(result.id, '1292052');
    assert.deepEqual(result.rating, { value: 9.7 });
    assert.equal(result.credits[0].avatar.large, '/v1/douban/images/icon/u1.jpg');
    assert.equal(result.credits[0].avatar.small, '');
    assert.equal(result.credits[0].uri, 'douban://douban.com/celebrity/1054521');
    assert.equal(result.photos[0].image.large.url, '/v1/douban/images/view/photo/l/public/p2.webp');
    assert.equal(result.photos[0].image.large.width, 100);
    assert.equal(result.null_field, null);
    assert.equal(Object.hasOwn(result, 'cover'), false);
  }
  assert.equal(fixture.calls.length, paths.length);
});

test('Rexxar request refuses URL/path injection, unknown fields and account mutations before network access', async () => {
  const request = async () => assert.fail('invalid request must not use the network');
  const inputs = [
    null, [], {}, { path: '/rexxar/api/v2/movie/1', url: 'https://other.test' },
    { path: '/rexxar/api/v2/movie/1', method: 'POST' }, { path: '/rexxar/api/v2/movie/1', headers: {} },
    ...['https://m.douban.com/rexxar/api/v2/movie/1', '//other.test', '/3/movie/1',
      '/rexxar/api/v2/', '/rexxar/api/v2/user/1', '/rexxar/api/v2/account',
      '/rexxar/api/v2/movie/1/rating', '/rexxar/api/v2/movie/1/interests',
      '/rexxar/api/v2/search', '/rexxar/api/v2/movie/1?method=POST', '/rexxar/api/v2/movie/1#x',
      '/rexxar/api/v2/movie/../account', '/rexxar/api/v2/movie/%2e%2e/account',
      '/rexxar/api/v2/movie/%31', '/rexxar/api/v2/movie/1\\other',
      '/rexxar/api/v2/movie/1\n', '/rexxar/api/v2/subject_collection/../items',
      '/rexxar/api/v2/movie/1/photos/delete'].map(path => ({ path })),
    ...[null, [], { nested: {} }, { start: NaN }, { start: Infinity }, { x: [null] },
      { '\r\nCookie': 'x' }].map(query => ({ path: '/rexxar/api/v2/movie/1', query })),
  ];
  for (const input of inputs) await assert.rejects(requestDouban(input, { settings: defaults, request }), failure('INVALID_REQUEST'));
});

test('Rexxar query values are encoded as data, preserving array filters without turning them into routes', async () => {
  const fixture = upstream([reply({ items: [] })]);
  const query = { tags: ['剧情', '犯罪'], marker: 'a&url=https://other.test/#secret', start: 0, playable: true };
  await requestDouban({ path: '/rexxar/api/v2/subject/recent_hot/movie', query },
    { settings: defaults, request: fixture.request });
  const parsed = new URL(fixture.calls[0].url);
  assert.equal(parsed.pathname, '/rexxar/api/v2/subject/recent_hot/movie');
  assert.deepEqual(parsed.searchParams.getAll('tags'), ['剧情', '犯罪']);
  assert.equal(parsed.searchParams.get('marker'), query.marker);
  assert.equal(parsed.searchParams.get('url'), null);
  assert.equal(parsed.hash, '');
});

test('API/search bases keep full prefixes and API wrappers receive the entire logical URL', async () => {
  for (const apiProxy of ['', 'https://api-relay.test/pass/{url}', 'https://api-relay.test/?target={encodedUrl}&mode=read']) {
    const config = settings({ apiBase: 'https://api.test/go/douban/', searchBase: 'https://search.test/full/prefix/',
      apiProxy, imageBase: 'https://images.test/independent', imageProxy: 'https://image-relay.test/?u={encodedUrl}' });
    const fixture = upstream([reply({ items: [] }), reply(html(sampleSearch({ items: [] })))]);
    await requestDouban({ path: '/rexxar/api/v2/subject/recent_hot/tv', query: { category: '剧集' } },
      { settings: config, request: fixture.request });
    await searchDouban({ query: '测试 & movie' }, { settings: config, request: fixture.request });
    const logical = [
      'https://api.test/go/douban/rexxar/api/v2/subject/recent_hot/tv?category=%E5%89%A7%E9%9B%86',
      'https://search.test/full/prefix/movie/subject_search?search_text=%E6%B5%8B%E8%AF%95+%26+movie&cat=1002',
    ];
    const expected = logical.map(url => !apiProxy ? url : apiProxy.replace('{url}', url).replace('{encodedUrl}', encodeURIComponent(url)));
    assert.deepEqual(fixture.calls.map(call => call.url), expected);
    assert.equal(fixture.calls.some(call => call.url.includes('image-relay.test')), false);
  }
  const encoded = upstream([reply({ items: [] })]);
  await requestDouban({ path: '/rexxar/api/v2/movie/categories' }, {
    settings: settings({ apiBase: 'https://api.test/full%20prefix/电影' }), request: encoded.request,
  });
  assert.equal(encoded.calls[0].url, 'https://api.test/full%20prefix/%E7%94%B5%E5%BD%B1/rexxar/api/v2/movie/categories');
});

test('invalid base URLs and wrapper templates fail with fixed errors', async () => {
  const request = async () => assert.fail('invalid settings must not reach the network');
  const templates = [
    'https://relay.test/', 'https://relay.test/{url}/{encodedUrl}', 'https://relay.test/{url}/{url}',
    'https://relay.test/{other}', 'https://{url}.relay.test/', 'https://relay.test/{url}#fragment',
    'file:///relay/{url}', 'https://name:password@relay.test/{url}', 'https://relay.test/\r\n{url}',
  ];
  for (const apiProxy of templates) {
    await assert.rejects(requestDouban({ path: '/rexxar/api/v2/movie/1' },
      { settings: settings({ apiProxy }), request }), failure('INVALID_REQUEST'));
    assert.throws(() => doubanImageTarget('/v1/douban/images/view/photo/p1.jpg',
      settings({ imageProxy: apiProxy })), failure('INVALID_REQUEST'));
  }
  for (const apiBase of ['https://user:password@api.test', 'file:///tmp', 'https://api.test/base?key=x',
    'https://api.test/base#fragment', 'https://api.test/a/../b', 'https://api.test/a/%2e%2e/b']) {
    await assert.rejects(requestDouban({ path: '/rexxar/api/v2/movie/1' },
      { settings: settings({ apiBase }), request }), failure('INVALID_REQUEST'));
  }
});

test('Rexxar business errors never surface the upstream message or credentials', async () => {
  for (const body of [
    { code: 404, msg: 'Cookie=private; upstream details' }, { code: '429', localized_message: 'private url' },
    { error: 'https://private.test/?cookie=private' }, { msg: 'permission_denied' },
    { code: 500, error: 'PRIVATE' }, { msg: 'rate_limited' },
  ]) {
    const fixture = upstream([reply(body)]);
    await assert.rejects(requestDouban({ path: '/rexxar/api/v2/movie/1' },
      { settings: defaults, request: fixture.request }), failure('UPSTREAM_HTTP'));
  }
  for (const body of [
    { code: 103, msg: 'PRIVATE' }, { code: '401', msg: 'PRIVATE' }, { msg: 'need_login' },
    { error: 'captcha required' }, { localized_message: '请先登录', code: 403 },
  ]) {
    await assert.rejects(requestDouban({ path: '/rexxar/api/v2/movie/1' },
      { settings: defaults, request: upstream([reply(body)]).request }), failure('WEB_INTERACTION_REQUIRED'));
  }
  const ok = await requestDouban({ path: '/rexxar/api/v2/movie/1' },
    { settings: defaults, request: upstream([reply({ code: 0, msg: 'ok', id: '1', rating: null })]).request });
  assert.deepEqual(ok, { code: 0, msg: 'ok', id: '1', rating: null });
});

test('non-JSON, HTTP and transport failures have stable sanitized categories', async () => {
  for (const [response, code] of [
    [reply('PRIVATE upstream HTML'), 'UPSTREAM_FORMAT'], [reply(null), 'UPSTREAM_FORMAT'],
    [reply([]), 'UPSTREAM_FORMAT'], [reply({ error: 'PRIVATE' }, 502), 'UPSTREAM_HTTP'],
    [reply('<html><title>Unavailable</title></html>', 403), 'UPSTREAM_HTTP'],
    [reply('<html><title>Unavailable</title></html>', 418), 'WEB_INTERACTION_REQUIRED'],
    [reply('PRIVATE', 401), 'WEB_INTERACTION_REQUIRED'],
    [reply({ code: 103, msg: 'PRIVATE' }, 403), 'WEB_INTERACTION_REQUIRED'],
    [{ ...reply('{}'), body: Buffer.from([0xff]) }, 'UPSTREAM_FORMAT'],
    [() => { throw Object.assign(new Error('https://secret.test/?cookie=PRIVATE'), { code: 'ECONNRESET' }); }, 'NETWORK_ERROR'],
  ]) {
    await assert.rejects(requestDouban({ path: '/rexxar/api/v2/movie/1' },
      { settings: defaults, request: upstream([response]).request }), failure(code));
  }
});

test('web search maps window.__DATA__ to the server subjects.items[].target structure', async () => {
  const fixture = upstream([reply(html(sampleSearch()))]);
  const result = await searchDouban({ query: ' 测试 ' }, { settings: defaults, request: fixture.request });
  assert.deepEqual(result, {
    banned: '', fuzzy: '', promotion: [], search_egg: null, smart_box: [],
    contents: { count: 0, start: 0, total: 0, items: [] },
    subjects: { target_name: '更多热门“测试”书影音', show_more_subjects: true, items: [
      { layout: 'subject', type_name: '电影', target_id: '1292052', target_type: 'movie',
        target: { rating: { count: 120, max: 10, star_count: 4.5, value: 9.7 },
          controversy_reason: '', title: '肖申克的救赎', abstract: '', has_linewatch: true,
          uri: 'douban://douban.com/movie/1292052', cover_url: '/v1/douban/images/view/photo/s_ratio_poster/public/p480747492.jpg',
          year: '1994', card_subtitle: '1994 / 美国 / 犯罪 剧情', id: '1292052', null_rating_reason: '' } },
      { layout: 'subject', type_name: '电视剧', target_id: '3541415', target_type: 'tv',
        target: { rating: { count: 0, max: 10, star_count: 0, value: 0 },
          controversy_reason: '', title: '白夜追凶', abstract: '', has_linewatch: false,
          uri: 'douban://douban.com/tv/3541415', cover_url: null, year: '0',
          card_subtitle: '2017 / 中国大陆 / 犯罪', id: '3541415', null_rating_reason: '尚未上映' } },
    ] },
  });
  const parsed = new URL(fixture.calls[0].url);
  assert.equal(parsed.host, 'search.douban.com');
  assert.equal(parsed.pathname, '/movie/subject_search');
  assert.equal(parsed.searchParams.get('search_text'), '测试');
  assert.equal(parsed.searchParams.get('cat'), '1002');
  assert.equal(parsed.searchParams.has('start'), false);
  assert.equal(fixture.calls[0].headers['user-agent'].includes('Chrome/132.0.0.0'), true);
  assert.equal(Object.hasOwn(result, 'cache_status'), false);
  assert.equal(Object.hasOwn(result.subjects, 'start'), false);
});

test('search preserves missing/null covers and a valid empty result, using URL kind fallback', async () => {
  const data = sampleSearch({ total: 1, text: '', items: [
    { id: 1, title: '标题 "含 } ; window.__USER__" (2024)', url: 'https://movie.douban.com/subject/1/', labels: [] },
  ] });
  const result = await searchDouban({ query: '回退词' },
    { settings: defaults, request: upstream([reply(html(data))]).request });
  assert.equal(result.subjects.items[0].target_type, 'movie');
  assert.equal(result.subjects.items[0].target.title, '标题 "含 } ; window.__USER__"');
  assert.equal(result.subjects.items[0].target.year, '2024');
  assert.equal(Object.hasOwn(result.subjects.items[0].target, 'cover_url'), false);
  assert.equal(result.subjects.show_more_subjects, false);
  assert.equal(result.subjects.target_name, '更多热门“回退词”书影音');
  const empty = await searchDouban({ query: '空' },
    { settings: defaults, request: upstream([reply(html({ items: [], total: 0 }))]).request });
  assert.deepEqual(empty.subjects.items, []);
  assert.equal(empty.subjects.show_more_subjects, false);
  const title = '<html><title>安全验证 的搜索结果 | 豆瓣</title><script>window.__DATA__ = ' +
    JSON.stringify({ items: [], total: 0 }) + ';</script></html>';
  const ordinary = await searchDouban({ query: '安全验证' },
    { settings: defaults, request: upstream([reply(title)]).request });
  assert.deepEqual(ordinary.subjects.items, []);
});

test('normal search titles and result text are not interaction signals', async () => {
  for (const phrase of ['请输入验证码', '检测到异常请求', '请完成安全验证', '请完成验证后继续']) {
    const data = { total: 1, text: phrase, items: [
      { id: 1, title: phrase, abstract: phrase, labels: [{ text: '电影' }] },
    ] };
    for (const content of [
      '<title>' + phrase + ' 的搜索结果 | 豆瓣</title>',
      '<h1>' + phrase + '</h1><h2>' + phrase + '</h2><p>' + phrase + '</p>',
      '<!-- <form id="sec"><input type="password"></form> --><div data-name="captcha">' + phrase + '</div>',
    ]) {
      const result = await searchDouban({ query: phrase }, {
        settings: defaults, request: upstream([reply('<html>' + content +
          '<script>window.__DATA__ = ' + JSON.stringify(data) + ';</script></html>')]).request,
      });
      assert.equal(result.subjects.items[0].target.title, phrase);
      assert.equal(result.subjects.items[0].target.card_subtitle, phrase);
      assert.equal(result.subjects.target_name, '更多热门“' + phrase + '”书影音');
    }
  }
  for (const title of [
    'window.location.href = "https://accounts.douban.com/login";',
    "window.location.href = 'https://accounts.douban.com/login';",
  ]) {
    const quoted = sampleSearch({ items: [{ id: 1, title, labels: [{ text: '电影' }] }] });
    const result = await searchDouban({ query: '跳转代码' },
      { settings: defaults, request: upstream([reply(html(quoted))]).request });
    assert.equal(result.subjects.items[0].target.title, title);
  }
  await assert.rejects(searchDouban({ query: '请输入验证码' }, {
    settings: defaults, request: upstream([reply('<html><p>请输入验证码 的搜索结果</p></html>')]).request,
  }), failure('UPSTREAM_FORMAT'));
});

test('search rejects unsupported pagination, URL overrides, empty queries and unknown inputs', async () => {
  const request = async () => assert.fail('invalid input must not use the network');
  for (const input of [null, [], {}, { query: null }, { query: '' }, { query: '  ' }, { query: 'a\nb' },
    { query: 'x', start: 0 }, { query: 'x', url: 'https://other.test' }, { query: 'x', headers: {} }]) {
    await assert.rejects(searchDouban(input, { settings: defaults, request }), failure('INVALID_REQUEST'));
  }
});

test('missing or malformed page data is a format error; page JavaScript is never evaluated', async () => {
  const bodies = [
    '<html>正常页面但数据缺失</html>', '<script>window.__DATA__ = {"items": null}; window.__USER__=null;</script>',
    '<script>window.__DATA__ = {"total":0}; window.__USER__=null;</script>',
    '<script>window.__DATA__ = {items: []}; window.__USER__=null;</script>',
    '<script>window.__DATA__ = (() => { globalThis.__doubanPageExecuted = true; return {"items":[]}; })();</script>',
    '<script>window.__DATA__ = {"items":[]} + (globalThis.__doubanPageExecuted = true);</script>',
    html({ items: [null] }), html({ items: [{ id: 1 }] }),
    html({ items: [{ id: 0, title: '非法 ID' }] }), html({ items: [{ id: '1\n', title: '非法 ID' }] }),
    html({ items: [], total: '20' }),
    html({ items: [{ id: 1, title: 'x', labels: {} }] }),
    html({ items: [{ id: 1, title: 'x', rating: { value: '9.0' } }] }),
  ];
  for (const body of bodies) {
    await assert.rejects(searchDouban({ query: 'x' },
      { settings: defaults, request: upstream([reply(body)]).request }), failure('UPSTREAM_FORMAT'));
    assert.equal(Object.hasOwn(globalThis, '__doubanPageExecuted'), false);
  }
});

test('login, captcha and proof-of-work pages report interaction without following or solving challenges', async () => {
  const bodies = [
    '<html><title>豆瓣登录</title><input type="password"></html>',
    '<html><title>安全验证</title></html>',
    '<html><form id="sec"><input id="cha" value="challenge"><input id="sol"></form></html>',
    "<html><img id='captcha_image' src='/captcha'></html>",
    '<html><form action="https://accounts.douban.com/login"></form></html>',
    '<!-- comment --><html><title>安全验证</title></html>',
    '<img id="captcha_image" src="/captcha">',
    '<html><script>window.location.href = "https://accounts.douban.com/login";</script></html>',
    '<html><title>请输入验证码</title></html>',
    '<html><title>检测到异常请求 | 豆瓣</title></html>',
    '<html><title>请完成安全验证</title></html>',
    '<html><title>请完成验证后继续</title></html>',
    '<html><script>if (true) { window.location.href = "https://sec.douban.com/c"; }</script></html>',
  ];
  for (const body of bodies) {
    for (const withData of [false, true]) {
      for (const operation of ['search', 'request']) {
        const fixture = upstream([reply(body + (withData ? html({ items: [], total: 0 }) : ''))]);
        const run = operation === 'search' ? searchDouban({ query: 'x' },
          { settings: defaults, request: fixture.request }) : requestDouban({ path: '/rexxar/api/v2/movie/1' },
          { settings: defaults, request: fixture.request });
        await assert.rejects(run, failure('WEB_INTERACTION_REQUIRED'));
        assert.equal(fixture.calls.length, 1);
        assert.equal(fixture.calls[0].method, 'GET');
      }
    }
  }
  const fixture = upstream([reply('', 302, { location: 'https://sec.douban.com/c?red=PRIVATE' })]);
  await assert.rejects(searchDouban({ query: 'x' },
    { settings: defaults, request: fixture.request }), failure('WEB_INTERACTION_REQUIRED'));
  assert.equal(fixture.calls.length, 1);
});

test('known source image domains and aliases produce identical identities while preserving specifications', () => {
  const domains = ['img.douban.com', 'img1.douban.com', 'img2.doubanio.com', 'img3.doubanio.com',
    'img9.doubanio.com', 'img123.doubanio.com', 'img.doubanio.com',
    'img.doubanio.cmliussss.net', 'img.doubanio.cmliussss.com'];
  for (const domain of domains) {
    const value = rewriteDoubanImages({ nested: [{ unexpected_key: `https://${domain}/view/photo/l/public/p1.jpg` }],
      cover: { small: `//${domain}/view/photo/s/public/p1.jpg` } }, defaults);
    assert.equal(value.nested[0].unexpected_key, '/v1/douban/images/view/photo/l/public/p1.jpg');
    assert.equal(value.cover.small, '/v1/douban/images/view/photo/s/public/p1.jpg');
    assert.notEqual(value.cover.small, value.nested[0].unexpected_key);
    assert.equal(JSON.stringify(value).includes(domain), false);
  }
});

test('configured image prefix and image relay wrappers collapse into the same resource identity', () => {
  const target = 'https://images.test/douban/full-prefix/view/photo/l/public/p1.jpg';
  for (const imageProxy of ['', 'https://image-relay.test/read/{url}', 'https://image-relay.test/?u={encodedUrl}&v=1']) {
    const config = settings({ imageBase: 'https://images.test/douban/full-prefix/', imageProxy });
    const url = !imageProxy ? target : imageProxy.replace('{url}', target).replace('{encodedUrl}', encodeURIComponent(target));
    const source = { pic: { normal: url }, also_image: target,
      url: 'https://movie.douban.com/subject/1/', uri: 'douban://douban.com/movie/1' };
    const result = rewriteDoubanImages(source, config);
    assert.equal(result.pic.normal, '/v1/douban/images/view/photo/l/public/p1.jpg');
    assert.equal(result.also_image, result.pic.normal);
    assert.equal(result.url, source.url);
    assert.equal(result.uri, source.uri);
    assert.equal(source.pic.normal, url);
  }
  const encoded = settings({ imageBase: 'https://images.test/full%20prefix/图片' });
  const value = rewriteDoubanImages({ cover_url: 'https://images.test/full%20prefix/图片/view/photo/l/public/p1.jpg' }, encoded);
  assert.equal(value.cover_url, '/v1/douban/images/view/photo/l/public/p1.jpg');
  assert.equal(doubanImageTarget(value.cover_url, encoded).url,
    'https://images.test/full%20prefix/%E5%9B%BE%E7%89%87/view/photo/l/public/p1.jpg');
  const switched = settings({ imageBase: 'https://new-images.test/new-prefix', imageProxy: 'https://relay.test/{url}' });
  assert.equal(rewriteDoubanImages(value, switched).cover_url, value.cover_url);
  assert.equal(doubanImageTarget(value.cover_url, switched).url,
    'https://relay.test/https://new-images.test/new-prefix/view/photo/l/public/p1.jpg');
  const knownPrefix = settings({ imageBase: 'https://img3.doubanio.com/relay-prefix' });
  assert.deepEqual(rewriteDoubanImages({
    pic: 'https://img3.doubanio.com/relay-prefix/view/photo/l/public/p1.jpg',
    cover: 'https://img9.doubanio.com/view/photo/l/public/p1.jpg',
  }, knownPrefix), {
    pic: '/v1/douban/images/view/photo/l/public/p1.jpg', cover: '/v1/douban/images/view/photo/l/public/p1.jpg',
  });
  assert.deepEqual(rewriteDoubanImages({ url: 'https://movie.douban.com/subject/1/' },
    settings({ imageBase: 'https://movie.douban.com' })), { url: 'https://movie.douban.com/subject/1/' });
});

test('nested pic/cover/avatar variants, arrays and relative image fields preserve missing/null values', () => {
  const source = { pic: { normal: '/view/photo/m/public/p1.jpg', large: null, count: 3 },
    cover: null, avatar: '//img3.doubanio.com/icon/u1.jpg', images: [
      'https://img1.doubanio.com/view/photo/l/public/p2.jpg', null,
      { src: '/view/photo/l/public/p3.jpg', width: 300, description: '头像' },
    ], other: { title: '标题', path: '/subject/1' }, cover_url: '' };
  const result = rewriteDoubanImages(source, defaults);
  assert.deepEqual(result, { pic: { normal: '/v1/douban/images/view/photo/m/public/p1.jpg', large: null, count: 3 },
    cover: null, avatar: '/v1/douban/images/icon/u1.jpg', images: [
      '/v1/douban/images/view/photo/l/public/p2.jpg', null,
      { src: '/v1/douban/images/view/photo/l/public/p3.jpg', width: 300, description: '头像' },
    ], other: { title: '标题', path: '/subject/1' }, cover_url: '' });
  assert.equal(Object.hasOwn(result, 'poster'), false);
  assert.deepEqual(rewriteDoubanImages(result, defaults), result);
});

test('photo entity page URLs stay business links while nested image descriptors remain strict', async () => {
  const source = { id: '1', cover: {
    id: '2', type: 'photo', url: 'https://movie.douban.com/photos/photo/2/',
    sharing_url: 'https://www.douban.com/doubanapp/dispatch?uri=/photo/2/',
    author: { avatar: 'https://img2.doubanio.com/icon/u1.jpg', url: 'https://www.douban.com/people/1/' },
    image: { large: { url: 'https://img3.doubanio.com/view/photo/l/public/p1.jpg', width: 100 },
      small: { url: '/view/photo/s/public/p1.jpg', width: 50 }, raw: null },
  }, cover_url: 'https://img9.doubanio.com/view/photo/m_ratio_poster/public/p1.jpg' };
  const expected = structuredClone(source);
  expected.cover.author.avatar = '/v1/douban/images/icon/u1.jpg';
  expected.cover.image.large.url = '/v1/douban/images/view/photo/l/public/p1.jpg';
  expected.cover.image.small.url = '/v1/douban/images/view/photo/s/public/p1.jpg';
  expected.cover_url = '/v1/douban/images/view/photo/m_ratio_poster/public/p1.jpg';
  const result = await requestDouban({ path: '/rexxar/api/v2/movie/1' },
    { settings: defaults, request: upstream([reply(source)]).request });
  assert.deepEqual(result, expected);
  assert.deepEqual(rewriteDoubanImages({ photos: [source.cover] }, defaults), { photos: [expected.cover] });
  assert.deepEqual(rewriteDoubanImages(source, settings({ imageBase: 'https://movie.douban.com' })), expected);
  assert.equal(source.cover.image.large.url, 'https://img3.doubanio.com/view/photo/l/public/p1.jpg');
  for (const value of ['https://unknown.test/p1.jpg', 'https://movie.douban.com/subject/1/', source.cover.url]) {
    for (const image of [
      { cover_url: value }, { cover: { url: value } },
      { cover: { ...source.cover, image: { large: { url: value } } } },
      { image: { type: 'photo', url: value } },
    ]) assert.throws(() => rewriteDoubanImages(image, defaults), failure('UPSTREAM_FORMAT'));
  }
});

test('untrusted image URLs and malformed source paths fail explicitly without becoming a generic URL proxy', () => {
  const values = ['https://unknown.test/p1.jpg', 'https://img3.doubanio.com.evil.test/p1.jpg',
    'https://img3.doubanio.com@evil.test/p1.jpg', 'https://user:password@img3.doubanio.com/p1.jpg',
    'https://unknown.test/extensionless', 'data:image/png;base64,PRIVATE', 'javascript:PRIVATE',
    'https://img3.doubanio.com/view/../private/p1.jpg', 'https://img3.doubanio.com/view/%2e%2e/p1.jpg',
    'https://img3.doubanio.com/p1.jpg?key=PRIVATE', 'https://img3.doubanio.com/p1.jpg#PRIVATE',
    '/view/photo/%2F%2Fevil.test/p1.jpg', '//evil.test/p1.jpg', '/view/photo/p1.jpg?next=PRIVATE',
    '/v1/douban/images/../private', 'https://img3.doubanio.com/view/photo/%252e%252e/p1.jpg'];
  for (const value of values) assert.throws(() => rewriteDoubanImages({ cover_url: value }, defaults), failure('UPSTREAM_FORMAT'));
  assert.deepEqual(rewriteDoubanImages({ unexpected: 'https://untrusted.test/photo.jpg' }, defaults),
    { unexpected: 'https://untrusted.test/photo.jpg' });
  const source = { url: 'https://movie.douban.com/subject/1292052/', share_url: 'https://www.douban.com/doubanapp/dispatch',
    uri: 'douban://douban.com/movie/1292052', title: '普通电影链接不能改' };
  assert.deepEqual(rewriteDoubanImages(source, defaults), source);
});

test('ordinary business links with image-like suffixes remain intact within a Rexxar list', async () => {
  const source = { count: 1, start: 0, total: 1, items: [{
    id: '1292052', homepage: 'https://site.invalid/page.jpg', url: 'https://site.invalid/page.jpg',
    trailers: [{ url: 'https://site.invalid/page.png', title: '网页链接' }],
    pic: { normal: 'https://img3.doubanio.com/view/photo/l/public/p1.jpg' },
  }] };
  const result = await requestDouban({ path: '/rexxar/api/v2/subject/recent_hot/movie' },
    { settings: defaults, request: upstream([reply(source)]).request });
  const expected = structuredClone(source);
  expected.items[0].pic.normal = '/v1/douban/images/view/photo/l/public/p1.jpg';
  assert.deepEqual(result, expected);
  assert.throws(() => rewriteDoubanImages({ cover_url: source.items[0].homepage }, defaults),
    failure('UPSTREAM_FORMAT'));
});

test('image targets use the current image base/proxy only, carry source UA/Referer, and never carry Cookie', () => {
  const path = '/v1/douban/images/view/photo/s_ratio_poster/public/p1.jpg';
  const first = settings({ apiBase: 'https://api.test/api-prefix', apiProxy: 'https://api-relay.test/{url}',
    imageBase: 'https://first-images.test/first-prefix', cookie: 'synthetic=COOKIE' });
  const second = { ...first, imageBase: 'https://second-images.test/second-prefix/',
    imageProxy: 'https://image-relay.test/?u={encodedUrl}' };
  assert.deepEqual(doubanImageTarget(path, first), {
    url: 'https://first-images.test/first-prefix/view/photo/s_ratio_poster/public/p1.jpg',
    headers: { referer: 'https://movie.douban.com/', 'user-agent': 'curl/8.5.0', accept: '*/*' },
  });
  const result = doubanImageTarget(path, second);
  assert.equal(result.url, 'https://image-relay.test/?u=' +
    encodeURIComponent('https://second-images.test/second-prefix/view/photo/s_ratio_poster/public/p1.jpg'));
  assert.equal(Object.keys(result.headers).some(key => /cookie|authorization/i.test(key)), false);
  assert.equal(result.url.includes('api-relay.test'), false);
  assert.equal(result.url.includes('COOKIE'), false);
  const raw = doubanImageTarget(path, { ...first, imageProxy: 'https://relay.test/{url}' });
  assert.equal(raw.url, 'https://relay.test/https://first-images.test/first-prefix/view/photo/s_ratio_poster/public/p1.jpg');
});

test('image routes refuse authorities, query/fragment injection, traversal and encodings', () => {
  for (const path of [null, '', '/v1/douban/images/', '/view/photo/p1.jpg', 'view/photo/p1.jpg',
    'https://img3.doubanio.com/view/photo/p1.jpg', '//img3.doubanio.com/view/photo/p1.jpg',
    '/v1/douban/images/https://other.test/p1.jpg', '/v1/douban/images//other.test/p1.jpg',
    '/v1/douban/images/../secret', '/v1/douban/images/a/./p1.jpg',
    '/v1/douban/images/a/%2e%2e/p1.jpg', '/v1/douban/images/a/%252e%252e/p1.jpg',
    '/v1/douban/images/a/%2fp1.jpg', '/v1/douban/images/a\\p1.jpg',
    '/v1/douban/images/p1.jpg?x=PRIVATE', '/v1/douban/images/p1.jpg#PRIVATE',
    '/v1/douban/images/p1.jpg\r\nCookie:PRIVATE', '/v1/douban/images/user:pass@host/p1.jpg']) {
    assert.throws(() => doubanImageTarget(path, defaults), failure('INVALID_REQUEST'));
  }
});

test('same-host redirects preserve configured cookies; foreign redirects receive none', async () => {
  for (const operation of ['search', 'request']) {
    const root = operation === 'search' ? 'https://search.douban.com' : 'https://m.douban.com';
    const fixture = upstream([
      reply('', 302, { location: root + '/normal' }),
      reply('', 307, { location: 'https://elsewhere.test/final',
        'set-cookie': ['foreign=DO_NOT_LEAK; Domain=elsewhere.test; Path=/', 'session=local; Path=/'] }),
      operation === 'search' ? reply(html(sampleSearch({ items: [] }))) : reply({ id: '1' }),
    ]);
    const args = { settings: settings({ cookie: 'synthetic=CONFIGURED' }), request: fixture.request };
    if (operation === 'search') await searchDouban({ query: 'x' }, args);
    else await requestDouban({ path: '/rexxar/api/v2/movie/1' }, args);
    assert.equal(fixture.calls[0].headers.cookie, 'synthetic=CONFIGURED');
    assert.equal(fixture.calls[1].headers.cookie, 'synthetic=CONFIGURED');
    assert.equal(Object.hasOwn(fixture.calls[2].headers, 'cookie'), false);
    assert.equal(fixture.calls.every(call => call.followRedirect === false), true);
    assert.equal(fixture.calls[2].headers.referer?.includes('CONFIGURED') ?? false, false);
  }
});

test('redirect cookies obey Domain/Path/Secure/expiry boundaries and Set-Cookie arrays', async () => {
  const fixture = upstream([
    reply('', 302, { location: '/movie/next', 'set-cookie': [
      'host=HOST; Path=/', 'shared=SHARED; Domain=.douban.com; Path=/; Secure',
      'path=PATH; Path=/movie/next', 'boundary=NO; Path=/movie/nex',
      'expired=NO; Max-Age=0; Path=/', 'wrong=NO; Domain=elsewhere.test; Path=/',
      'suffix=NO; Domain=com; Path=/', 'overwrite=OLD; Path=/',
    ] }),
    reply('', 303, { location: 'https://movie.douban.com/movie/next/child', 'set-cookie': [
      'overwrite=NEW; Path=/', 'dead=NO; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/',
    ] }),
    reply('', 301, { location: 'http://movie.douban.com/movie/final' }),
    reply(html({ total: 0, items: [] })),
  ]);
  await searchDouban({ query: 'x' }, { settings: settings({ cookie: 'configured=ONLY_SEARCH' }), request: fixture.request });
  const second = fixture.calls[1].headers.cookie;
  assert.match(second, /configured=ONLY_SEARCH/);
  assert.match(second, /host=HOST/);
  assert.match(second, /shared=SHARED/);
  assert.match(second, /path=PATH/);
  assert.match(second, /overwrite=OLD/);
  for (const absent of ['boundary', 'expired', 'wrong', 'suffix', 'dead']) assert.equal(second.includes(absent + '='), false);
  assert.equal(fixture.calls[2].headers.cookie, 'shared=SHARED');
  assert.equal(Object.hasOwn(fixture.calls[3].headers, 'cookie'), false);
});

test('operation cookies stay local and API relay wrapping never copies them to a foreign redirect', async () => {
  const config = settings({ apiProxy: 'https://relay.test/proxy?url={encodedUrl}', cookie: 'synthetic=CONFIGURED' });
  const fixture = upstream([
    reply('', 302, { location: 'https://elsewhere.test/first', 'set-cookie': 'origin=SOURCE; Path=/' }),
    reply('', 302, { location: 'https://search.douban.com/movie/final',
      'set-cookie': 'attack=NO; Domain=douban.com; Path=/' }),
    reply(html({ total: 0, items: [] })), reply(html({ total: 0, items: [] })),
  ]);
  await searchDouban({ query: 'x' }, { settings: config, request: fixture.request });
  assert.equal(fixture.calls[0].headers.cookie, 'synthetic=CONFIGURED');
  assert.equal(Object.hasOwn(fixture.calls[1].headers, 'cookie'), false);
  assert.equal(fixture.calls[2].headers.cookie, 'synthetic=CONFIGURED; origin=SOURCE');
  assert.equal(new URL(fixture.calls[1].url).searchParams.get('url'), 'https://elsewhere.test/first');
  await searchDouban({ query: 'x' }, { settings: config, request: fixture.request });
  assert.equal(fixture.calls[3].headers.cookie, 'synthetic=CONFIGURED');
});

test('relative redirects preserve base prefixes and already-wrapped redirects are not double-wrapped', async () => {
  const config = settings({ searchBase: 'https://search.test/full/prefix', apiProxy: 'https://relay.test/?u={encodedUrl}' });
  const logicalFinal = 'https://search.test/full/prefix/movie/final?accepted=1';
  const fixture = upstream([
    reply('', 302, { location: 'second?step=1' }),
    reply('', 302, { location: 'https://relay.test/?u=' + encodeURIComponent(logicalFinal) }),
    reply(html({ items: [], total: 0 })),
  ]);
  await searchDouban({ query: 'x' }, { settings: config, request: fixture.request });
  assert.equal(new URL(fixture.calls[1].url).searchParams.get('u'), 'https://search.test/full/prefix/movie/second?step=1');
  assert.equal(new URL(fixture.calls[2].url).searchParams.get('u'), logicalFinal);
});

test('redirect loops are bounded; missing, unsafe and credential-bearing redirect locations fail safely', async () => {
  const fixture = upstream(Array.from({ length: 6 }, () => reply('', 302, { location: '/again' })));
  await assert.rejects(searchDouban({ query: 'x' },
    { settings: defaults, request: fixture.request }), failure('UPSTREAM_HTTP'));
  assert.equal(fixture.calls.length, 6);
  for (const headers of [{}, { location: 'file:///etc/passwd' },
    { location: 'https://name:PRIVATE@elsewhere.test/path' }, { location: 'https://elsewhere.test/\r\nCookie:PRIVATE' }]) {
    const invalid = upstream([reply('', 302, headers)]);
    await assert.rejects(searchDouban({ query: 'x' },
      { settings: defaults, request: invalid.request }), failure('UPSTREAM_FORMAT'));
    assert.equal(invalid.calls.length, 1);
  }
});

test('cancellation is propagated to the injected request and normalized before and after each request', async () => {
  for (const operation of ['search', 'request']) {
    const invoke = (request, signal) => operation === 'search' ? searchDouban({ query: 'x' },
      { settings: defaults, request, signal }) : requestDouban({ path: '/rexxar/api/v2/movie/1' },
      { settings: defaults, request, signal });
    const before = new AbortController(); before.abort(new Error('PRIVATE'));
    let beforeCalls = 0;
    await assert.rejects(invoke(async () => { beforeCalls++; assert.fail('already cancelled'); },
      before.signal), failure('CANCELLED'));
    assert.equal(beforeCalls, 0);
    const during = new AbortController();
    await assert.rejects(invoke(async (_url, { signal }) => {
      assert.equal(signal, during.signal);
      signal.throwIfAborted();
      during.abort();
      throw new Error('PRIVATE');
    }, during.signal), failure('CANCELLED'));
    const after = new AbortController();
    await assert.rejects(invoke(async () => {
      after.abort();
      return reply(operation === 'search' ? html({ items: [], total: 0 }) : {});
    }, after.signal), failure('CANCELLED'));
  }
});
