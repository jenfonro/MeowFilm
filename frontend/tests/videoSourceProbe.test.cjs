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
const matchSource = stripModuleSyntax(readFileSync('src/utils/matchCore.js', 'utf8'));
const navigationSource = stripModuleSyntax(readFileSync('src/shared/detailNavigation.js', 'utf8'));
const site = { key: 'probe', name: '检测测试源', api: '/aaaaaaaaaa/spider/test/3' };
const response = (data, status = 200) => ({
  status, ok: status >= 200 && status < 300,
  json: async () => data, text: async () => JSON.stringify(data),
});
const listing = (id) => response({ ok: true, vod_play_url: `第一集$${id}` });
const playable = () => response({ ok: true, url: 'https://media.example/video.mp4' });

function makeProbe({ details = {}, items, lists = {}, plays = {}, nativePlays = {}, panMock = true, navigation = {}, responses = {} }) {
  const calls = [];
  let saved;
  const context = vm.createContext({
    URL, URLSearchParams, AbortController, setTimeout, clearTimeout, atob,
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
      const action = pathname.split('/').pop();
      if (responses[action]) return responses[action];
      const child = navigation[`${pathname.split('/').pop()}:${body.id}`];
      if (child) return response({ pan_mock: panMock, ...child });
      if (pathname.endsWith('/home')) return response({ class: [{ type_id: 'movies' }] });
      if (pathname.endsWith('/category')) return response({ list: items || [{ vod_id: 'first' }] });
      if (pathname.endsWith('/search')) return response({ list: [] });
      if (pathname.endsWith('/detail')) {
        assert.ok(details[body.id], `unexpected detail: ${body.id}`);
        return response({ pan_mock: panMock, ...(Array.isArray(details[body.id].list)
          ? details[body.id] : { list: [details[body.id]] }) });
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
    const { getPanShareInput, isBuiltinPanPlayId, panMockProviderFromFlag } = (() => { ${matchSource}; return { getPanShareInput, isBuiltinPanPlayId, panMockProviderFromFlag }; })();
    const { hasDetailNavigation, walkDetailNavigation } = (() => { ${navigationSource}; return { hasDetailNavigation, walkDetailNavigation }; })();
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
    details: { first: detail('夸父-share-one', '') },
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
      details: { first: detail('百度-expired', 'https://pan.baidu.com/s/1expired?pwd=expired') },
      lists: { 'https://pan.baidu.com/s/1expired?pwd=expired': response(data) },
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
    details: { first: detail('百度-other-error', 'https://pan.baidu.com/s/1other-error?pwd=other-error') },
    lists: { 'https://pan.baidu.com/s/1other-error?pwd=other-error': response({ ok: false, message: 'baidu api errno=-90' }) },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'invalid');
  assert.match(saved.errors.probe, /errno=-90/);
  assert.doesNotMatch(saved.errors.probe, /分享链接已失效/);
});

test('a provider-specific 403 does not skip the remaining pans in the same detail', async () => {
  const probe = makeProbe({
    details: { first: detail('百度-denied$$$优夕-working', 'https://pan.baidu.com/s/1denied?pwd=denied$$$') },
    lists: {
      'https://pan.baidu.com/s/1denied?pwd=denied': response({ ok: false, message: 'share denied' }, 403),
      '优夕-working': listing('uc-file'),
    },
    plays: { 'uc-file': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), [
    'detail:first', 'list:https://pan.baidu.com/s/1denied?pwd=denied', 'list:优夕-working', 'play:优夕-working',
  ]);
});

test('all pan lists in one detail are attempted before advancing to the next detail', async () => {
  const probe = makeProbe({
    items: [{ vod_id: 'first' }, { vod_id: 'second' }],
    details: {
      first: detail('百度-expired$$$夸父-empty', 'https://pan.baidu.com/s/1expired?pwd=expired$$$'),
      second: detail('优夕-working'),
    },
    lists: {
      'https://pan.baidu.com/s/1expired?pwd=expired': response({ ok: false, message: 'baidu api errno=-9' }, 404),
      '夸父-empty': response({ ok: true, vod_play_url: '' }),
      '优夕-working': listing('uc-file'),
    },
    plays: { 'uc-file': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), [
    'detail:first', 'list:https://pan.baidu.com/s/1expired?pwd=expired', 'list:夸父-empty', 'detail:second', 'list:优夕-working', 'play:优夕-working',
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

test('new name recognition does not force an extra detail for already-playable native category items', async () => {
  for (const flag of ['夸克', '夸克-shareA', 'UC-shareA', '天翼-shareA', '移动-shareA']) {
    const probe = makeProbe({
      panMock: false,
      items: [{ vod_id: 'first', ...detail(flag, '第一集$opaque-native-id') }],
      details: {},
      nativePlays: { 'opaque-native-id': playable() },
    });
    const saved = await probe.run();
    assert.equal(saved.results.probe, 'valid', flag);
    assert.deepEqual(resolutionCalls(probe.calls), [`play:${flag}`]);
  }
});

test('the original probe legacy-name detail refresh is preserved, including names without a hyphen', async () => {
  for (const flag of ['百度', '夸父', '优夕极速', '天意', '逸动-shareA']) {
    const probe = makeProbe({
      panMock: false,
      items: [{ vod_id: 'first', ...detail(flag, '第一集$old-id') }],
      details: { first: detail(flag, '第一集$fresh-native-id') },
      nativePlays: { 'fresh-native-id': playable() },
    });
    const saved = await probe.run();
    assert.equal(saved.results.probe, 'valid', flag);
    assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', `play:${flag}`]);
  }
});

const navigationCard = (id, action, extra = {}) => ({
  vod_id: id, vod_name: id, vod_navigation: { action, payload: { id }, ...extra },
});
const shareCard = id => navigationCard('opaque-' + id, 'detail', {
  provider: 'quark', share_flag: '夸克-' + id, share_url: 'https://pan.quark.cn/s/' + id,
});

test('source probing follows navigation action then locally lists shares without script-private detail', async () => {
  const probe = makeProbe({
    details: { first: { list: [navigationCard('pan-group', 'category')] } },
    navigation: { 'category:pan-group': { list: [shareCard('expiredShare'), shareCard('workingShare')] } },
    lists: {
      'https://pan.quark.cn/s/expiredShare': response({ ok: false, message: 'share expired' }),
      'https://pan.quark.cn/s/workingShare': listing('navigation-file'),
    },
    plays: { 'navigation-file': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.ok(probe.calls.some(c => c.pathname.endsWith('/category') && c.body.id === 'pan-group'));
  assert.deepEqual(resolutionCalls(probe.calls), [
    'detail:first', 'list:https://pan.quark.cn/s/expiredShare',
    'list:https://pan.quark.cn/s/workingShare', 'play:夸克-workingShare',
  ]);
});

test('source probing with pan_mock off follows custom operation and uses Runner complete file ID', async () => {
  const probe = makeProbe({
    panMock: false,
    details: { first: { list: [navigationCard('pan-group', 'resources', { payload: { id: 'pan-group', cursor: 'original' } })] } },
    navigation: {
      'resources:pan-group': { list: [shareCard('workingShare')] },
      'detail:opaque-workingShare': { list: [detail('夸克-workingShare', 'File$original*token*fid*ftoken***Movie.mkv')] },
    },
    nativePlays: { 'original*token*fid*ftoken***Movie.mkv': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.equal(probe.calls.find(c => c.pathname.endsWith('/resources')).body.cursor, 'original');
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'detail:opaque-workingShare', 'play:夸克-workingShare']);
  assert.equal(probe.calls.some(c => c.pathname.startsWith('/api/pan/')), false);
});

test('source probing reports unknown navigation data instead of inventing a play request', async () => {
  const probe = makeProbe({ panMock: false,
    details: { first: { list: [navigationCard('group', 'category')] } },
    navigation: { 'category:group': { list: [null, { vod_id: 'unknown', custom: 'upstream' }] } },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'invalid');
  assert.match(saved.errors.probe, /未支持的详情/);
  assert.equal(probe.calls.some(c => c.pathname.endsWith('/play')), false);
});

test('source probing preserves explicitly returned script errors at home/category/search/detail', async () => {
  for (const action of ['home', 'category', 'search', 'detail']) {
    for (const doc of [
      { list: [], error: '解密失败: 加密字符串不能为空' },
      { list: [], ok: false, msg: '原脚本明确返回的错误' },
      { list: [], success: false, message: '原脚本失败信息' },
      { data: { list: [], error: '内层脚本明确返回的错误' } },
    ]) {
      const probe = makeProbe({
        items: action === 'search' ? [] : undefined,
        responses: { [action]: response(doc) },
      });
      const saved = await probe.run();
      assert.equal(saved.results.probe, 'invalid', action);
      assert.ok(saved.errors.probe.includes(doc.error || doc.msg || doc.message || doc.data.error), action);
      assert.doesNotMatch(saved.errors.probe, /Runner.*丢失|未知上游|缺少播放信息/);
    }
  }
});

test('script HTTP errors keep message/msg/error or non-JSON text instead of just the HTTP code', async () => {
  for (const result of [
    response({ message: "Cannot read properties of undefined (reading 'children')" }, 500),
    response({ msg: '原脚本 msg 错误' }, 500),
    response({ error: '原脚本 error 错误' }, 500),
    { ok: false, status: 500, json: async () => { throw new Error('not JSON'); }, text: async () => '1040 Too many connections' },
  ]) {
    const probe = makeProbe({ responses: { category: result } });
    const saved = await probe.run();
    assert.match(saved.errors.probe, /children|原脚本 (?:msg|error) 错误|1040 Too many connections/);
  }
});

test('a script-swallowed error stays an empty result; arbitrary business codes and informational messages are not guessed', async () => {
  for (const doc of [
    { list: [] },
    { list: [], code: 101801001105, msg: '未知业务含义' },
    { list: [], message: 'ok' },
  ]) {
    const probe = makeProbe({ responses: { category: response(doc), search: response(doc) } });
    const saved = await probe.run();
    assert.equal(saved.results.probe, 'invalid');
    assert.deepEqual(saved.errors, {}, 'no error may be reconstructed from an empty list');
    assert.equal(probe.calls.some(c => c.pathname.endsWith('/detail') || c.pathname.endsWith('/play')), false);
  }
});

test('native play errors retain their text without skipping subsequent candidates', async () => {
  const failed = { ok: false, msg: '解析器原始错误', url: '' };
  const probe = makeProbe({
    panMock: false,
    details: { first: detail('线路一$$$线路二', '第一集$bad-id$$$第一集$good-id') },
    nativePlays: { 'bad-id': response(failed), 'good-id': playable() },
  });
  assert.equal((await probe.run()).results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'play:线路一', 'play:线路二']);
  const onlyFailure = makeProbe({
    panMock: false,
    details: { first: detail('线路一', '第一集$bad-id') },
    nativePlays: { 'bad-id': response(failed) },
  });
  assert.match((await onlyFailure.run()).errors.probe, /解析器原始错误/);
});

test('a navigation child exposes its returned error without inventing another request', async () => {
  const probe = makeProbe({
    details: { first: { list: [navigationCard('pan-group', 'category')] } },
    navigation: { 'category:pan-group': { list: [], error: '导航接口原始错误' } },
  });
  const saved = await probe.run();
  assert.match(saved.errors.probe, /导航接口原始错误/);
  assert.equal(probe.calls.some(c => c.pathname.endsWith('/play')), false);
});

test('informational messages and unknown codes keep usable data; category never selects advertised filters', async () => {
  const probe = makeProbe({
    panMock: false,
    responses: {
      home: response({
        class: [{ type_id: 'movies' }], code: 90001, message: '首页提示',
        filters: { movies: [{ key: 'year', value: [{ n: '2026', v: '2026' }] }] },
      }),
      category: response({ list: [{ vod_id: 'first' }], code: 90002, msg: '分类提示' }),
      detail: response({
        list: [detail('蓝光HDR', '第一集$native-id')], code: 90003,
        message: '部分线路提示，其他线路可用', error: '', pan_mock: false,
      }),
    },
    nativePlays: { 'native-id': response({ url: 'https://media.example/video.mp4', code: 90004, msg: '播放提示' }) },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(saved.errors, {});
  assert.deepEqual(probe.calls.find(c => c.pathname.endsWith('/category')).body, {
    id: 'movies', page: 1, filter: true, filters: {},
  });
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'play:蓝光HDR']);
});

test('an explicitly failed detail still permits the next original detail candidate', async () => {
  const probe = makeProbe({
    panMock: false,
    items: [{ vod_id: 'first' }, { vod_id: 'second' }],
    details: {
      first: { list: [], error: '第一详情原始错误' },
      second: detail('原生线路', '第一集$working-id'),
    },
    nativePlays: { 'working-id': playable() },
  });
  assert.equal((await probe.run()).results.probe, 'valid');
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'detail:second', 'play:原生线路']);
});

test('a returned error in an empty detail preserves the existing search fallback', async () => {
  const probe = makeProbe({
    panMock: false,
    details: {
      first: { list: [], error: '分类详情原始错误' },
      searched: detail('搜索线路', '第一集$working-id'),
    },
    responses: { search: response({ list: [{ vod_id: 'searched' }] }) },
    nativePlays: { 'working-id': playable() },
  });
  const saved = await probe.run();
  assert.equal(saved.results.probe, 'valid');
  assert.deepEqual(saved.errors, {});
  assert.deepEqual(probe.calls.filter(c => !c.pathname.startsWith('/dashboard/')).map(c => c.pathname.split('/').pop()), [
    'home', 'category', 'detail', 'search', 'detail', 'play',
  ]);
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'detail:searched', 'play:搜索线路']);
});

test('an explicitly failed navigation branch does not suppress its working sibling', async () => {
  const probe = makeProbe({
    panMock: false,
    details: { first: { list: [navigationCard('bad', 'category'), navigationCard('good', 'category')] } },
    navigation: {
      'category:bad': { list: [], error: '第一导航原始错误' },
      'category:good': { list: [detail('原生线路', '第一集$working-id')] },
    },
    nativePlays: { 'working-id': playable() },
  });
  assert.equal((await probe.run()).results.probe, 'valid');
  assert.deepEqual(probe.calls.filter(c => c.pathname.endsWith('/category')).map(c => c.body.id), ['movies', 'bad', 'good']);
  assert.deepEqual(resolutionCalls(probe.calls), ['detail:first', 'play:原生线路']);
});
