const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

const stripModuleSyntax = (source) => source
  .replace(/^import .*\r?\n/gm, '')
  .replace(/^export /gm, '');
const source = stripModuleSyntax(readFileSync('src/pages/dashboard/dashboardLogic.js', 'utf8'));
const requestSource = stripModuleSyntax(readFileSync('src/shared/requestJson.js', 'utf8'));
const normalizeSource = stripModuleSyntax(readFileSync('src/shared/normalize.js', 'utf8'));
const urlSource = stripModuleSyntax(readFileSync('src/shared/urlText.js', 'utf8'));
const site = { key: 'probe', name: '检测测试源', api: '/aaaaaaaaaa/spider/test/3' };
const response = (data, status = 200) => ({ status, ok: status >= 200 && status < 300, json: async () => data });
const listing = (id) => response({ ok: true, vod_play_url: `第一集$${id}` });
const playable = () => response({ ok: true, url: 'https://media.example/video.mp4' });

function makeProbe({ details, items, lists = {}, plays = {}, nativePlays = {}, panMock = true }) {
  const calls = [];
  let saved;
  const context = vm.createContext({
    URL, URLSearchParams, AbortController, setTimeout, clearTimeout,
    fetch: async (url, options = {}) => {
      const pathname = new URL(String(url), 'https://movie.example').pathname;
      const isSave = pathname === '/dashboard/video/source/sites/check';
      const body = isSave
        ? Object.fromEntries(new URLSearchParams(options.body))
        : (options.body ? JSON.parse(options.body) : {});
      calls.push({ pathname, body, method: options.method });
      if (isSave) {
        saved = { results: JSON.parse(body.results), errors: JSON.parse(body.errors) };
        return response({ success: true, sites: [site], results: saved.results });
      }
      if (pathname.endsWith('/home')) return response({ class: [{ type_id: 'movies' }] });
      if (pathname.endsWith('/category')) return response({ list: items || [{ vod_id: 'first' }] });
      if (pathname.endsWith('/search')) return response({ list: [] });
      if (pathname.endsWith('/detail')) {
        assert.ok(details[body.id], `unexpected detail: ${body.id}`);
        return response({ pan_mock: panMock, list: [details[body.id]] });
      }
      if (pathname.startsWith('/api/pan/') && pathname.endsWith('/list')) {
        assert.ok(lists[body.flag], `unexpected list: ${body.flag}`);
        return lists[body.flag];
      }
      if (pathname.startsWith('/api/pan/') && pathname.endsWith('/play')) {
        assert.ok(plays[body.id], `unexpected pan play: ${body.id}`);
        return plays[body.id];
      }
      if (pathname === '/play') {
        assert.ok(nativePlays[body.id], `unexpected native play: ${body.id}`);
        return nativePlays[body.id];
      }
      throw new Error(`unexpected request: ${pathname}`);
    },
  });
  // Run the actual shared JSON parser too, so an undeclared readJson cannot be
  // hidden by a test stub. No production requests or persisted settings.
  vm.runInContext(`
    const requestJsonResponse = (() => { ${requestSource}; return requestJsonResponse; })();
    const sharedNormalizeHttpBase = (() => { ${normalizeSource}; ${urlSource}; return normalizeHttpBase; })();
    ${source}
  `, context);
  return {
    calls,
    async run() {
      await context.probeDashboardVideoSourceSites({
        apiBase: 'https://cat.example',
        keys: [site.key],
        sites: [site],
        tvUser: 'probe-user',
      });
      return saved;
    },
  };
}

const detail = (from, url = '') => ({ vod_play_from: from, vod_play_url: url });
const resolutionCalls = (calls) => calls
  .filter(({ pathname }) => pathname.endsWith('/detail') || pathname.startsWith('/api/pan/') || pathname === '/play')
  .map(({ pathname, body }) => `${pathname.split('/').pop()}:${body.flag || body.id}`);

test('source probing parses list/play JSON through the shared request helper', async () => {
  const probe = makeProbe({
    details: { first: detail('夸父-share-one', 'nopass.mp4$placeholder') },
    lists: { '夸父-share-one': listing('quark-file') },
    plays: { 'quark-file': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(saved.errors, {});
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'list:夸父-share-one', 'play:夸父-share-one']);
});

test('pan_mock detail flags can resolve a list even when vod_play_url is empty', async () => {
  const probe = makeProbe({
    details: { first: detail('夸父-share-one') },
    lists: { '夸父-share-one': listing('quark-file') },
    plays: { 'quark-file': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(saved.errors, {});
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'list:夸父-share-one', 'play:夸父-share-one']);
});

test('empty aligned URL groups retain every pan flag and try another share before another detail', async () => {
  for (const separator of ['$$$', '|||']) {
    const probe = makeProbe({
      details: { first: detail(`百度原画-expired${separator}百度原画-working`, separator) },
      lists: {
        '百度原画-expired': response({ message: 'baidu api errno=-9', ok: false }, 404),
        '百度原画-working': listing('baidu-file'),
      },
      plays: { 'baidu-file': playable() },
    });
    const saved = await probe.run();
    assert.equal(saved.results.probe, 'valid', separator);
    assert.deepEqual(saved.errors, {});
    assert.deepEqual(resolutionCalls(probe.calls), [
      'detail:first', 'list:百度原画-expired', 'list:百度原画-working', 'play:百度原画-working',
    ]);
  }
});

test('Baidu errno -9 means an expired share, not a JSON parser or playback error', async () => {
  for (const data of [
    { ok: false, message: 'baidu api errno=-9' },
    { ok: false, errno: -9 },
    { ok: false, errno: '-9' },
  ]) {
    const probe = makeProbe({
      details: { first: detail('百度-expired', 'nopass.mp4$placeholder') },
      lists: { '百度-expired': response(data) },
    });
    const saved = await probe.run();
    assert.equal(saved.results.probe, 'invalid');
    assert.match(saved.errors.probe, /分享链接已失效/);
    assert.doesNotMatch(saved.errors.probe, /readJson|缺少播放信息/);
    assert.equal(probe.calls.filter((c) => c.pathname.endsWith('/play')).length, 0);
  }
});

test('Baidu errors other than -9 are not mislabeled as expired shares', async () => {
  const probe = makeProbe({
    details: { first: detail('百度-other-error', 'nopass.mp4$placeholder') },
    lists: { '百度-other-error': response({ ok: false, message: 'baidu api errno=-90' }) },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'invalid');
  assert.match(saved.errors.probe, /errno=-90/);
  assert.doesNotMatch(saved.errors.probe, /分享链接已失效/);
});

test('a provider-specific 403 does not skip the remaining pans in the same detail', async () => {
  const probe = makeProbe({
    details: { first: detail('百度-denied$$$优夕-working', 'nopass.mp4$one$$$nopass.mp4$two') },
    lists: {
      '百度-denied': response({ ok: false, message: 'share denied' }, 403),
      '优夕-working': listing('uc-file'),
    },
    plays: { 'uc-file': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), [
    'detail:first', 'list:百度-denied', 'list:优夕-working', 'play:优夕-working',
  ]);
});

test('all pan lists in one detail are attempted before advancing to the next detail', async () => {
  const probe = makeProbe({
    items: [{ vod_id: 'first' }, { vod_id: 'second' }],
    details: {
      first: detail('百度-expired$$$夸父-empty'),
      second: detail('优夕-working'),
    },
    lists: {
      '百度-expired': response({ ok: false, message: 'baidu api errno=-9' }, 404),
      '夸父-empty': response({ ok: true, vod_play_url: '' }),
      '优夕-working': listing('uc-file'),
    },
    plays: { 'uc-file': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), [
    'detail:first', 'list:百度-expired', 'list:夸父-empty', 'detail:second', 'list:优夕-working', 'play:优夕-working',
  ]);
});

test('a failed pan play still permits the other pan in the current detail', async () => {
  const probe = makeProbe({
    details: { first: detail('夸父-first$$$优夕-second') },
    lists: { '夸父-first': listing('quark-file'), '优夕-second': listing('uc-file') },
    plays: {
      'quark-file': response({ ok: false, message: 'file unavailable' }, 404),
      'uc-file': playable(),
    },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), [
    'detail:first', 'list:夸父-first', 'play:夸父-first', 'list:优夕-second', 'play:优夕-second',
  ]);
});

test('a flag without a share identifier is not a usable flag-only candidate', async () => {
  const probe = makeProbe({ details: { first: detail('夸父$$$未知网盘') } });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'invalid');
  assert.match(saved.errors.probe, /缺少播放信息/);
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first']);
});

test('ordinary non-mock detail still needs a playback ID', async () => {
  const probe = makeProbe({ panMock: false, details: { first: detail('夸父-share-one') } });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'invalid');
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first']);
});

test('native playback also tries all routes in one detail after a route fails', async () => {
  const probe = makeProbe({
    panMock: false,
    details: { first: detail('线路一$$$线路二', '第一集$bad-id$$$第一集$good-id') },
    nativePlays: {
      'bad-id': response({ message: 'first route failed' }, 404),
      'good-id': playable(),
    },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'play:线路一', 'play:线路二']);
});
