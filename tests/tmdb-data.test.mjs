import test from 'node:test';
import assert from 'node:assert/strict';
import { requestTMDB, tmdbImageTarget } from '../apps/runtime/modules/metadata/tmdb.mjs';

const defaults = Object.freeze({
  apiBase: 'https://api.themoviedb.org',
  imageBase: 'https://image.tmdb.org/t/p',
  apiKey: '',
  accessToken: '',
});

function fixture(value = { id: 42 }, settings = defaults, response = {}) {
  const calls = [];
  return {
    calls,
    context: {
      settings,
      request: async (url, options) => {
        calls.push({ url, ...options });
        return {
          status: 200,
          headers: { 'content-type': 'application/json' },
          body: Buffer.from(JSON.stringify(value)),
          url,
          ...response,
        };
      },
    },
  };
}

function failure(code) {
  return error => {
    assert.ok(error instanceof Error);
    assert.equal(error.code, code);
    assert.equal(error.message, code);
    assert.equal(error.cause, undefined);
    assert.deepEqual(Object.keys(error), ['code']);
    return true;
  };
}

test('TMDB preserves API prefixes, query scalars and the direct business response', async () => {
  const value = { page: 4, total_pages: 90, total_results: 1800, results: [{ id: 123, vote_average: 7.3 }] };
  for (const [apiBase, expected] of [
    [defaults.apiBase, 'https://api.themoviedb.org/3/search/multi'],
    ['https://api-proxy.invalid/', 'https://api-proxy.invalid/3/search/multi'],
    ['https://api-proxy.invalid/tmdb', 'https://api-proxy.invalid/tmdb/3/search/multi'],
    ['https://api-proxy.invalid/prefix/tmdb///', 'https://api-proxy.invalid/prefix/tmdb/3/search/multi'],
    ['http://127.0.0.1:3456/tmdb', 'http://127.0.0.1:3456/tmdb/3/search/multi'],
  ]) {
    const { context, calls } = fixture(value, { ...defaults, apiBase });
    assert.deepEqual(await requestTMDB({
      path: '/3/search/multi',
      query: { query: '电影 & api_key=untrusted/#?', language: 'zh-CN', page: 4, include_adult: false },
    }, context), value);
    const url = new URL(calls[0].url);
    assert.equal(`${url.origin}${url.pathname}`, expected);
    assert.deepEqual(Object.fromEntries(url.searchParams), {
      query: '电影 & api_key=untrusted/#?', language: 'zh-CN', page: '4', include_adult: 'false',
    });
    assert.equal(calls[0].method, 'GET');
    assert.equal(calls[0].body, undefined);
    assert.equal(calls[0].followRedirect, false);
    assert.deepEqual(calls[0].headers, { Accept: 'application/json' });
    assert.equal(calls.length, 1);
  }
});

test('TMDB uses a bearer token in preference to an API key and never sends both', async () => {
  for (const [credentials, expectedHeaders, expectedKey] of [
    [{ apiKey: 'fixture-key', accessToken: '' }, { Accept: 'application/json' }, 'fixture-key'],
    [{ apiKey: '', accessToken: 'fixture-token' },
      { Accept: 'application/json', Authorization: 'Bearer fixture-token' }, null],
    [{ apiKey: 'fixture-unused-key', accessToken: 'fixture-token' },
      { Accept: 'application/json', Authorization: 'Bearer fixture-token' }, null],
    [{ apiKey: 'fixture-key&/#?', accessToken: '' }, { Accept: 'application/json' }, 'fixture-key&/#?'],
  ]) {
    const { context, calls } = fixture({ id: 42 }, { ...defaults, ...credentials });
    assert.deepEqual(await requestTMDB({ path: '/3/movie/42', query: { language: 'en-US' } }, context), { id: 42 });
    assert.equal(new URL(calls[0].url).searchParams.get('api_key'), expectedKey);
    assert.deepEqual(calls[0].headers, expectedHeaders);
    assert.equal(new URL(calls[0].url).searchParams.get('access_token'), null);
  }
});

test('TMDB query parameters cannot supply credentials, headers or nested values', async () => {
  const { context, calls } = fixture();
  for (const name of [
    'api_key', 'API_KEY', 'apiKey', 'access_token', 'AccessToken', 'request_token',
    'session_id', 'guest_session_id', 'Authorization', 'cookie', 'api_key[]', 'api.key\n',
  ]) {
    await assert.rejects(requestTMDB({ path: '/3/movie/42', query: { [name]: 'fixture-injection' } }, context),
      failure('INVALID_REQUEST'));
  }
  for (const query of [null, [], new URLSearchParams(), { language: {} }, { page: [] },
    { page: null }, { page: undefined }, { page: Infinity }, { page: NaN }]) {
    await assert.rejects(requestTMDB({ path: '/3/movie/42', query }, context), failure('INVALID_REQUEST'));
  }
  assert.equal(calls.length, 0);
});

test('TMDB discover parameters and whitelisted append responses preserve their source syntax', async () => {
  const { context, calls } = fixture();
  await requestTMDB({ path: '/3/discover/movie',
    query: { 'vote_average.gte': 7.5, with_genres: '18,35', sort_by: 'popularity.desc', include_video: true },
  }, context);
  assert.deepEqual(Object.fromEntries(new URL(calls[0].url).searchParams), {
    'vote_average.gte': '7.5', with_genres: '18,35', sort_by: 'popularity.desc', include_video: 'true',
  });
  await requestTMDB({ path: '/3/movie/42',
    query: { append_to_response: 'credits,images,external_ids,watch/providers' },
  }, context);
  await requestTMDB({ path: '/3/tv/42',
    query: { append_to_response: 'aggregate_credits,season/0,season/1/episode/2' },
  }, context);
  assert.equal(new URL(calls[1].url).searchParams.get('append_to_response'),
    'credits,images,external_ids,watch/providers');
  assert.equal(new URL(calls[2].url).searchParams.get('append_to_response'),
    'aggregate_credits,season/0,season/1/episode/2');
  for (const append of ['account_states', 'credits,account_states', '../account', 'images?session_id=x',
    '/3/account', 'images,', 'images/../account_states', '%61ccount_states']) {
    await assert.rejects(requestTMDB({ path: '/3/movie/42', query: { append_to_response: append } }, context),
      failure('INVALID_REQUEST'));
  }
  assert.equal(calls.length, 3);
});

test('TMDB exposes the explicit configuration, catalog and related metadata routes', async () => {
  const paths = [
    '/3/configuration',
    ...['countries', 'languages', 'primary_translations', 'timezones'].map(part => `/3/configuration/${part}`),
    ...['movie', 'tv'].flatMap(type => [`/3/genre/${type}/list`, `/3/discover/${type}`]),
    ...['movie', 'tv', 'multi', 'person'].map(type => `/3/search/${type}`),
    ...['all', 'movie', 'tv', 'person'].flatMap(type =>
      ['day', 'week'].map(window => `/3/trending/${type}/${window}`)),
    ...['latest', 'now_playing', 'popular', 'top_rated', 'upcoming'].map(part => `/3/movie/${part}`),
    '/3/movie/42',
    ...['alternative_titles', 'credits', 'external_ids', 'images', 'keywords', 'lists', 'recommendations',
      'release_dates', 'similar', 'translations', 'videos', 'watch/providers']
      .map(part => `/3/movie/42/${part}`),
    ...['latest', 'airing_today', 'on_the_air', 'popular', 'top_rated'].map(part => `/3/tv/${part}`),
    '/3/tv/42',
    ...['aggregate_credits', 'alternative_titles', 'content_ratings', 'credits', 'episode_groups',
      'external_ids', 'images', 'keywords', 'recommendations', 'screened_theatrically',
      'similar', 'translations', 'videos', 'watch/providers'].map(part => `/3/tv/42/${part}`),
    '/3/tv/42/season/0', '/3/tv/42/season/1',
    ...['aggregate_credits', 'credits', 'external_ids', 'images', 'translations', 'videos', 'watch/providers']
      .map(part => `/3/tv/42/season/1/${part}`),
    '/3/tv/42/season/0/episode/0', '/3/tv/42/season/1/episode/2',
    ...['credits', 'external_ids', 'images', 'translations', 'videos']
      .map(part => `/3/tv/42/season/1/episode/2/${part}`),
    '/3/tv/episode_group/fixture-group_1',
    '/3/person/latest', '/3/person/popular', '/3/person/42',
    ...['combined_credits', 'external_ids', 'images', 'movie_credits', 'tagged_images', 'translations',
      'tv_credits'].map(part => `/3/person/42/${part}`),
    '/3/collection/42', '/3/collection/42/images', '/3/collection/42/translations',
    '/3/credit/fixture-credit_1',
    ...['regions', 'movie', 'tv'].map(part => `/3/watch/providers/${part}`),
  ];
  const { context, calls } = fixture();
  for (const path of paths) {
    assert.deepEqual(await requestTMDB({ path }, context), { id: 42 }, path);
    assert.equal(new URL(calls.at(-1).url).pathname, path);
  }
  assert.equal(calls.length, paths.length);
});

test('TMDB rejects movie and TV reviews, including appended reviews, before networking', async () => {
  const { context, calls } = fixture();
  for (const type of ['movie', 'tv']) {
    const path = `/3/${type}/42`;
    await assert.rejects(requestTMDB({ path: `${path}/reviews` }, context), failure('INVALID_REQUEST'));
    for (const append of ['reviews', 'credits,reviews', 'reviews,images']) {
      await assert.rejects(requestTMDB({ path, query: { append_to_response: append } }, context),
        failure('INVALID_REQUEST'));
    }
  }
  assert.equal(calls.length, 0);
});

test('TMDB rejects account, authentication, writes, unknown routes and encoded paths before networking', async () => {
  const { context, calls } = fixture();
  for (const path of [
    '/3/account', '/3/account/42', '/3/account/42/favorite/movies', '/3/authentication/token/new',
    '/3/authentication/guest_session/new', '/3/guest_session/42/rated/movies', '/3/movie/42/account_states',
    '/3/movie/42/rating', '/3/tv/42/rating', '/3/tv/42/season/1/account_states',
    '/3/tv/42/season/1/episode/2/rating', '/3/movie/42/unknown', '/3/list/42', '/4/list/42',
    '/3/search/company', '/3/configuration/unknown', '/3/trending/movie/month', '/3/movie/0',
    '/3/movie/01', '/3/tv/-1', '/3/tv/42/season/-1', '/3/person/42/changes',
    'https://untrusted.invalid/3/movie/42', '//untrusted.invalid/3/movie/42',
    '/3/movie/42?api_key=fixture-injection', '/3/movie/42#fragment', '/3/movie/42/',
    '/3/movie/42/../43', '/3/movie/%34%32', '/3/movie/42%2fimages', '/3/movie/42\\images',
    '/3//movie/42', '/3/movie/42\n', '/3/movie/42\u0000', '/3/movie/42;images',
    '/3/movie/42\u2028', '/3/movie/42\u2029',
  ]) {
    await assert.rejects(requestTMDB({ path }, context), failure('INVALID_REQUEST'), path);
  }
  for (const input of [
    null, [], {}, { path: '/3/movie/42', method: 'GET' }, { path: '/3/movie/42', method: 'POST' },
    { path: '/3/movie/42', headers: {} }, { path: '/3/movie/42', url: 'https://untrusted.invalid' },
    { path: '/3/movie/42', body: {} }, { path: '/3/movie/42', followRedirect: true },
  ]) {
    await assert.rejects(requestTMDB(input, context), failure('INVALID_REQUEST'));
  }
  assert.equal(calls.length, 0);
});

test('TMDB rewrites only the image fields in place across nested results and preserves business values', async () => {
  const value = {
    page: 2, total_pages: 5, total_results: 91,
    results: [{
      id: 42, media_type: 'movie', vote_average: 8.2, vote_count: 123,
      poster_path: '/poster.jpg', backdrop_path: '/backdrop.webp', profile_path: null,
      original_title: 'Fixture', ordinary_path: '/untouched.jpg', text: '/poster.jpg',
      homepage: 'https://business.invalid/poster.jpg',
      credits: { cast: [{ id: 13, profile_path: '/person.png', roles: [{ character: '主角' }] }],
        crew: [{ profile_path: '', department: 'Directing' }] },
      aggregate_credits: { cast: [{ id: 14, profile_path: '/aggregate.jpg',
        roles: [{ credit_id: 'fixture-credit', episode_count: 12 }] }] },
      images: { posters: [{ file_path: '/poster-2.jpg', width: 1000, height: 1500 }],
        logos: [{ file_path: '/logo.svg' }] },
      production_companies: [{ id: 5, logo_path: '/company.png' }],
      seasons: [{ season_number: 0, episodes: [{ episode_number: 2, still_path: '/episode.jpg' }] }],
      empty: { poster_path: '', backdrop_path: null, ordinary_path: null },
      ordinary: { image_url: 'https://business.invalid/image.jpg', base_url: 'https://business.invalid/',
        secure_base_url: 'https://business.invalid/' },
    }],
  };
  const expected = structuredClone(value);
  const item = expected.results[0];
  item.poster_path = '/v1/tmdb/images/original/poster.jpg';
  item.backdrop_path = '/v1/tmdb/images/original/backdrop.webp';
  item.credits.cast[0].profile_path = '/v1/tmdb/images/original/person.png';
  item.aggregate_credits.cast[0].profile_path = '/v1/tmdb/images/original/aggregate.jpg';
  item.images.posters[0].file_path = '/v1/tmdb/images/original/poster-2.jpg';
  item.images.logos[0].file_path = '/v1/tmdb/images/original/logo.svg';
  item.production_companies[0].logo_path = '/v1/tmdb/images/original/company.png';
  item.seasons[0].episodes[0].still_path = '/v1/tmdb/images/original/episode.jpg';
  const { context } = fixture(value);
  assert.deepEqual(await requestTMDB({ path: '/3/search/multi' }, context), expected);
  assert.equal(value.results[0].poster_path, '/poster.jpg');
});

test('TMDB image sizes use original by default and positive w/h specifications without a synthetic ceiling', async () => {
  for (const size of ['original', 'w1', 'h1', 'w500', 'h632',
    'w184467440737095516161234567890', 'h184467440737095516161234567890']) {
    const { context } = fixture({ poster_path: '/fixture.jpg' });
    const value = await requestTMDB({ path: '/3/movie/42', imageSize: size }, context);
    assert.deepEqual(value, { poster_path: `/v1/tmdb/images/${size}/fixture.jpg` });
    assert.equal(tmdbImageTarget(value.poster_path, defaults).url,
      `${defaults.imageBase}/${size}/fixture.jpg`);
  }
  const { context, calls } = fixture();
  for (const imageSize of ['', null, 500, 'w0', 'h-1', 'w01', 'w1.5', 'W500', 'w500\n', 'w500\u2028',
    'original?api_key=x', '../w500', 'w500/extra']) {
    await assert.rejects(requestTMDB({ path: '/3/movie/42', imageSize }, context), failure('INVALID_REQUEST'));
  }
  assert.equal(calls.length, 0);
});

test('TMDB configuration rewrites both image bases, retains sizes and leaves explicit settings intact', async () => {
  const settings = Object.freeze({ ...defaults, imageBase: 'https://images-proxy.invalid/images' });
  const value = {
    images: {
      base_url: 'http://images-upstream.invalid/t/p/', secure_base_url: 'https://images-upstream.invalid/t/p/',
      backdrop_sizes: ['w300', 'w780', 'w1280', 'original'], logo_sizes: ['w45', 'w92', 'original'],
      poster_sizes: ['w92', 'w500', 'original'], profile_sizes: ['w45', 'h632', 'original'],
      still_sizes: ['w92', 'w185', 'w300', 'original'],
    },
    change_keys: ['adult', 'air_date', 'poster'],
  };
  const { context } = fixture(value, settings);
  const result = await requestTMDB({ path: '/3/configuration', imageSize: 'w500' }, context);
  assert.deepEqual(result, {
    ...value,
    images: { ...value.images, base_url: '/v1/tmdb/images/', secure_base_url: '/v1/tmdb/images/' },
  });
  assert.equal(tmdbImageTarget('/v1/tmdb/images/w500/fixture.jpg', settings).url,
    'https://images-proxy.invalid/images/w500/fixture.jpg');
  assert.equal(settings.imageBase, 'https://images-proxy.invalid/images');
});

test('TMDB configuration collections retain array response shapes', async () => {
  for (const [path, value] of [
    ['/3/configuration/countries', [{ iso_3166_1: 'CN', english_name: 'China', native_name: '中国' }]],
    ['/3/configuration/languages', [{ iso_639_1: 'zh', english_name: 'Chinese', name: '普通话' }]],
    ['/3/configuration/primary_translations', ['en-US', 'zh-CN']],
    ['/3/configuration/timezones', [{ iso_3166_1: 'CN', zones: ['Asia/Shanghai'] }]],
  ]) {
    assert.deepEqual(await requestTMDB({ path }, fixture(value).context), value);
  }
});

test('TMDB image identity survives independent API and image proxy changes without credentials', async () => {
  const first = { ...defaults, apiBase: 'https://api-a.invalid/tmdb', imageBase: 'https://images-a.invalid/t/p',
    apiKey: 'fixture-key', accessToken: 'fixture-token' };
  const second = { ...first, apiBase: 'http://127.0.0.1:6789/proxy', imageBase: 'https://images-b.invalid/assets/' };
  const one = await requestTMDB({ path: '/3/movie/42' }, fixture({ poster_path: '/fixture.jpg' }, first).context);
  const two = await requestTMDB({ path: '/3/movie/42' }, fixture({ poster_path: '/fixture.jpg' }, second).context);
  assert.deepEqual(one, two);
  assert.equal(one.poster_path, '/v1/tmdb/images/original/fixture.jpg');
  assert.deepEqual(tmdbImageTarget(one.poster_path, first), {
    url: 'https://images-a.invalid/t/p/original/fixture.jpg', headers: { Accept: 'image/*' },
  });
  assert.deepEqual(tmdbImageTarget(one.poster_path, second), {
    url: 'https://images-b.invalid/assets/original/fixture.jpg', headers: { Accept: 'image/*' },
  });
  assert.notEqual(tmdbImageTarget('/v1/tmdb/images/w500/fixture.jpg', first).url,
    tmdbImageTarget(one.poster_path, first).url);
  assert.deepEqual(tmdbImageTarget(one.poster_path, {
    ...second, imageBase: 'http://127.0.0.1:3456/t/p',
  }), { url: 'http://127.0.0.1:3456/t/p/original/fixture.jpg', headers: { Accept: 'image/*' } });
});

test('TMDB image target rejects URLs, credentials, encoded paths, query injection and traversal', () => {
  for (const path of [
    null, {}, '', '/fixture.jpg', '/v1/tmdb/images/', '/v1/tmdb/images/w500',
    'https://untrusted.invalid/v1/tmdb/images/w500/fixture.jpg',
    '//untrusted.invalid/v1/tmdb/images/w500/fixture.jpg', '/v1/douban/images/w500/fixture.jpg',
    '/v1/tmdb/images/w500/../fixture.jpg', '/v1/tmdb/images/w500/..',
    '/v1/tmdb/images/w500/nested/fixture.jpg', '/v1/tmdb/images/w500/%2e%2e/fixture.jpg',
    '/v1/tmdb/images/%77%35%30%30/fixture.jpg', '/v1/tmdb/images/w500/fixture%2ejpg',
    '/v1/tmdb/images/w500/fixture%252ejpg', '/v1/tmdb/images/w500/fixture.jpg?api_key=fixture-injection',
    '/v1/tmdb/images/w500/fixture.jpg?', '/v1/tmdb/images/w500/fixture.jpg#fragment',
    '/v1/tmdb/images/w500/fixture.jpg;api_key=fixture-injection',
    '/v1/tmdb/images/w500/fixture.jpg\n', '/v1/tmdb/images/w500/fixture.jpg\u0000',
    '/v1/tmdb/images/w500/fixture.jpg\u2028', '/v1/tmdb/images/w500/fixture.jpg\u2029',
    '/v1/tmdb/images/w500\\fixture.jpg', '/v1/tmdb/images/w0/fixture.jpg',
    '/v1/tmdb/images/h-1/fixture.jpg', '/v1/tmdb/images/w500/.jpg', '/v1/tmdb/images/w500/fixture',
  ]) {
    assert.throws(() => tmdbImageTarget(path, defaults), failure('INVALID_REQUEST'));
  }
});

test('TMDB rejects invalid configured bases and header-injected credentials without exposing them', async () => {
  for (const base of ['not-a-url', 'file:///tmp/fixture', 'https://fixture-user:fixture-password@proxy.invalid',
    'https://proxy.invalid/tmdb?api_key=fixture-injection', 'https://proxy.invalid/tmdb?',
    'https://proxy.invalid/tmdb#fragment', 'https://proxy.invalid\\tmdb', 'https://proxy.invalid/\n']) {
    const { context, calls } = fixture({}, { ...defaults, apiBase: base });
    await assert.rejects(requestTMDB({ path: '/3/movie/42' }, context), failure('INVALID_REQUEST'));
    assert.equal(calls.length, 0);
    assert.throws(() => tmdbImageTarget('/v1/tmdb/images/original/fixture.jpg',
      { ...defaults, imageBase: base }), failure('INVALID_REQUEST'));
  }
  for (const credentials of [{ apiKey: 'fixture-key\n', accessToken: '' },
    { apiKey: '', accessToken: 'fixture-token\r\nX-Injection: fixture-value' }]) {
    const { context, calls } = fixture({}, { ...defaults, ...credentials });
    await assert.rejects(requestTMDB({ path: '/3/movie/42' }, context), failure('INVALID_REQUEST'));
    assert.equal(calls.length, 0);
  }
});

test('TMDB HTTP failures and redirects yield fixed errors without following or leaking upstream details', async () => {
  for (const status of [301, 302, 303, 304, 307, 308, 400, 401, 403, 404, 429, 500, 503]) {
    const { context, calls } = fixture({
      success: false, status_message: 'fixture-secret https://fixture-user:fixture-password@proxy.invalid',
    }, { ...defaults, apiKey: 'fixture-secret' }, {
      status, headers: { 'content-type': 'application/json',
        location: 'https://untrusted.invalid/error?api_key=fixture-secret' },
    });
    await assert.rejects(requestTMDB({ path: '/3/movie/42' }, context), failure('UPSTREAM_HTTP'));
    assert.equal(calls.length, 1);
    assert.equal(calls[0].followRedirect, false);
  }
  const { context } = fixture({ success: false, status_code: 7, status_message: 'fixture-secret' });
  await assert.rejects(requestTMDB({ path: '/3/movie/42' }, context), failure('UPSTREAM_HTTP'));
  const redirect = fixture({}, defaults, {
    status: 302, headers: { Location: 'https://login.invalid/?api_key=fixture-secret',
      'Content-Type': 'text/html' }, body: Buffer.from('<html>Login fixture-secret</html>'),
  });
  await assert.rejects(requestTMDB({ path: '/3/movie/42' }, redirect.context), failure('UPSTREAM_HTTP'));
  assert.equal(redirect.calls.length, 1);
});

test('TMDB challenge and login pages require web interaction', async () => {
  for (const response of [
    { status: 403, headers: { 'Content-Type': 'text/html' }, body: Buffer.from('<html>CAPTCHA fixture-secret</html>') },
    { status: 200, headers: { 'content-type': 'text/html' }, body: Buffer.from('<html>Login fixture-secret</html>') },
    { status: 200, headers: {}, body: Buffer.from('<!DOCTYPE html><p>cf-chl-platform fixture-secret</p>') },
    { status: 403, headers: new Headers({ 'cf-mitigated': 'challenge' }), body: Buffer.from('{}') },
  ]) {
    const { context, calls } = fixture({}, defaults, response);
    await assert.rejects(requestTMDB({ path: '/3/movie/42' }, context), failure('WEB_INTERACTION_REQUIRED'));
    assert.equal(calls.length, 1);
  }
});

test('TMDB malformed JSON, invalid image fields and invalid response shapes are fixed format errors', async () => {
  for (const response of [
    { body: Buffer.from('fixture-secret not json') },
    { body: Buffer.from('<html>unexpected upstream page</html>') },
    { body: Buffer.from('{"incomplete":"fixture-secret"') },
    { body: Buffer.from('null') }, { body: Buffer.from('42') }, { body: Buffer.from('"fixture-secret"') },
    { body: Buffer.concat([Buffer.from('{"title":"'), Buffer.from([0xff]), Buffer.from('"}')]) },
    { body: Buffer.from('{"poster_path":"https://untrusted.invalid/fixture-secret.jpg"}') },
    { body: Buffer.from('{"poster_path":"/../fixture-secret.jpg"}') },
    { body: Buffer.from('{"poster_path":"/fixture.jpg?api_key=fixture-secret"}') },
    { body: Buffer.from('{"file_path":42}') },
    { body: 'fixture-secret' }, { status: '200' }, { status: 999 },
  ]) {
    await assert.rejects(requestTMDB({ path: '/3/movie/42' }, fixture({}, defaults, response).context),
      failure('UPSTREAM_FORMAT'));
  }
});

test('TMDB sanitizes network errors and timeouts without retrying or returning empty data', async () => {
  for (const [upstream, code] of [
    [new Error('fixture-secret https://proxy.invalid?api_key=fixture-key'), 'NETWORK_ERROR'],
    [Object.assign(new Error('fixture-secret'), { code: 'ECONNRESET' }), 'NETWORK_ERROR'],
    [Object.assign(new Error('fixture-secret'), { code: 'TIMEOUT' }), 'NETWORK_ERROR'],
    [Object.assign(new Error('fixture-secret'), { name: 'TimeoutError' }), 'NETWORK_ERROR'],
    [Object.assign(new Error('fixture-secret'), { name: 'AbortError' }), 'CANCELLED'],
    [Object.assign(new Error('fixture-secret'), { code: 'CANCELLED' }), 'CANCELLED'],
  ]) {
    let calls = 0;
    await assert.rejects(requestTMDB({ path: '/3/movie/42' }, {
      settings: defaults,
      request: async () => { calls += 1; throw upstream; },
    }), failure(code));
    assert.equal(calls, 1);
  }
});

test('TMDB passes cancellation to the request owner and waits for settlement before discarding a late result', async () => {
  const early = new AbortController();
  early.abort(new Error('fixture-secret'));
  const earlyFixture = fixture();
  await assert.rejects(requestTMDB({ path: '/3/movie/42' },
    { ...earlyFixture.context, signal: early.signal }), failure('CANCELLED'));
  assert.equal(earlyFixture.calls.length, 0);

  const controller = new AbortController();
  let finish;
  let admitted;
  const started = new Promise(resolve => { admitted = resolve; });
  const pending = requestTMDB({ path: '/3/movie/42' }, {
    settings: defaults,
    signal: controller.signal,
    request: async (_url, options) => {
      assert.equal(options.signal, controller.signal);
      admitted();
      return new Promise(resolve => { finish = resolve; });
    },
  });
  await started;
  let settled = false;
  const checked = assert.rejects(pending, failure('CANCELLED')).then(() => { settled = true; });
  controller.abort(new Error('fixture-secret'));
  await Promise.resolve();
  assert.equal(settled, false);
  finish({ status: 200, headers: {}, body: Buffer.from('{"id":42}'), url: 'https://proxy.invalid' });
  await checked;
});
