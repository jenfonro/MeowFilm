const { test } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('esbuild');

const compiled = buildSync({
  stdin: {
    contents: `
      export * from './src/shared/catpawrunner.js';
      export { getPanShareInput, panMockProviderFromFlag } from './src/utils/matchCore.js';
      export { buildSourceSegmentItems } from './src/shared/smartSourceRecognition.js';
      export { executeResolvedSitePlayback } from './src/shared/playbackRuntime.js';
    `,
    resolveDir: process.cwd(),
  },
  bundle: true, platform: 'node', format: 'cjs', write: false,
});
const loaded = { exports: {} };
new Function('module', 'exports', 'require', compiled.outputFiles[0].text)(loaded, loaded.exports, require);
const api = loaded.exports;
const options = { apiBase: 'https://cat.example', spiderApi: '/aaaaaaaaaa/spider/test/3', siteDetail: 'film' };
const response = (data) => ({ ok: true, status: 200, json: async () => data });
const detail = (panMock, flags, groups) => ({
  pan_mock: panMock, list: [{ vod_name: 'Example', vod_play_from: flags.join('$$$'), vod_play_url: groups.join('$$$') }],
});
const deferred = () => {
  let resolve;
  const promise = new Promise(r => { resolve = r; });
  return { promise, resolve };
};
const installFetch = (t, fn) => {
  api.clearCatDetailCache();
  t.mock.method(globalThis, 'fetch', async (url, init = {}) =>
    fn(new URL(url, 'https://movie.example'), JSON.parse(init.body || '{}')));
};

test('canonical names and saved legacy names are recognized without inventing share IDs', () => {
  for (const [provider, labels] of Object.entries({
    baidu: ['百度', '百度-1234', '百度原画-shareA', '百度原画(无限)-shareA'],
    quark: ['夸克', '夸克-1234', '夸父-shareA'],
    uc: ['UC', 'uc-1234', '优夕-shareA'],
    '189': ['天翼', '天翼-1234', '天意-shareA'],
    '139': ['移动', '移动-1234', '逸动-shareA'],
  })) for (const label of labels) assert.equal(api.panMockProviderFromFlag(label), provider);
  for (const label of ['百度-1234', '夸克', '光鸭', '蓝光HDR']) assert.equal(api.getPanShareInput(label), null);
  assert.equal(api.normalizeSourceEntry({ label: 'saved', provider: 'quark' }).sourceKind, 'panmock');
});

test('share URLs retain identity and actual passwords, including mobile hash routes', () => {
  for (const [flag, url, provider, id, passcode] of [
    ['百度-1234', 'https://pan.baidu.com/s/1shareA?pwd=1234', 'baidu', 'shareA', '1234'],
    ['夸克', 'https://pan.quark.cn/s/shareA', 'quark', 'shareA', ''],
    ['UC', 'https://drive.uc.cn/s/shareA', 'uc', 'shareA', ''],
    ['天翼-1234', 'https://cloud.189.cn/t/shareA?accessCode=1234', '189', 'shareA', '1234'],
    ['移动-1234', 'https://yun.139.com/shareweb/#/w/i/shareA?pwd=1234', '139', 'shareA', '1234'],
  ]) {
    const share = api.getPanShareInput(flag, url);
    assert.deepEqual([share.provider, share.shareId, share.passcode], [provider, id, passcode]);
  }
  for (const url of ['https://pan.quark.cn.evil.test/s/shareA', 'https://user@pan.quark.cn/s/shareA', 'https://caiyun.139.com/m/i?pwd=1234']) {
    assert.equal(api.getPanShareInput('夸克', url), null);
  }
});

test('same-name shares resolve separately through the original list flag parameter', async (t) => {
  const a = 'https://pan.quark.cn/s/shareA', b = 'https://pan.quark.cn/s/shareB';
  const native = 'https://image.example/cover*Author*1:20****opaque-E65';
  const listCalls = [], updates = [];
  installFetch(t, (url, body) => {
    if (url.pathname.endsWith('/detail')) return response(detail(true, ['夸克', '夸克', '蓝光HDR'], [a, b, `第一集$${native}`]));
    assert.equal(url.pathname, '/api/pan/quark/list');
    listCalls.push(body);
    assert.equal(body.url, undefined, 'no new list protocol');
    const shareId = body.flag === a ? 'shareA' : body.flag === b ? 'shareB' : assert.fail('unexpected flag');
    return response({ ok: true, vod_play_url: `第2季$${shareId}*stoken*fid*fileToken***S02E01.mkv` });
  });
  const out = await api.fetchCatResolvedDetailCached({ ...options, onUpdate: item => updates.push(item) });
  assert.deepEqual(listCalls.map(item => item.flag).sort(), [a, b]);
  assert.equal(out.sources.length, 3);
  assert.notEqual(out.sources[0].key, out.sources[1].key);
  assert.match(out.sources[0].episodeSegments[0], /shareA\*/);
  assert.match(out.sources[1].episodeSegments[0], /shareB\*/);
  assert.equal(out.sources[0].sourceValue, a);
  assert.equal(out.sources[2].provider, '');
  assert.equal(out.sources[2].episodeSegments[0], `第一集$${native}`);
  assert.equal(updates.at(-1).resolutionComplete, true);
  await api.fetchCatResolvedDetailCached(options);
  assert.equal(listCalls.length, 2, 'the existing detail/list caches are reused');
});

test('Runner mode keeps full IDs and enters existing remote play without changing the player', async (t) => {
  const id = 'shareA*stoken*fid*fileToken***S02E01.mkv';
  const calls = [];
  installFetch(t, (url, body) => {
    calls.push({ url, body });
    if (url.pathname.endsWith('/detail')) return response(detail(false, ['夸克'], [`第2季$${id}`]));
    assert.equal(url.origin, 'https://cat.example');
    assert.equal(url.pathname, '/play');
    return response({ url: 'https://media.example/video.mp4' });
  });
  const out = await api.fetchCatResolvedDetailCached(options);
  assert.equal(out.sources[0].provider, '');
  assert.equal(out.sources[0].sourceKind, 'normal');
  assert.equal(out.sources[0].episodeSegments[0], `第2季$${id}`);
  const segment = api.buildSourceSegmentItems(out.sources[0])[0];
  assert.equal(segment.episodeUrl, id);
  assert.equal(segment.allowDirectoryHints, true, 'canonical name retains pan list metadata semantics');
  await api.executeResolvedSitePlayback({
    apiBase: options.apiBase, siteItem: { spiderApi: options.spiderApi, detailData: out },
    panEntry: out.sources[0], segment, runtimeSettings: {},
  });
  assert.equal(calls.length, 2);
  assert.equal(calls[1].body.id, id);
  assert.equal(calls[1].body.flag, '夸克');
  assert.equal(calls[1].body.siteApi, options.spiderApi);
});

test('a complete source with no usable share stays native even when pan_mock is on', async (t) => {
  installFetch(t, url => {
    assert.ok(url.pathname.endsWith('/detail'), 'native source must not call local list');
    return response(detail(true, ['百度', '光鸭原画'], ['File$opaque-private-id', 'File$duck-native-id']));
  });
  const out = await api.fetchCatResolvedDetailCached(options);
  assert.deepEqual(out.sources.map(s => s.provider), ['', '']);
  assert.deepEqual(out.sources.map(s => s.episodeSegments[0]), ['File$opaque-private-id', 'File$duck-native-id']);
});

test('clearing detail caches prevents an old in-flight list from overwriting the new mode', async (t) => {
  const started = deferred(), oldList = deferred();
  let mode = true, detailCalls = 0;
  installFetch(t, (url) => {
    if (url.pathname.endsWith('/detail')) {
      detailCalls += 1;
      return response(mode
        ? detail(true, ['夸克'], ['https://pan.quark.cn/s/shareA'])
        : detail(false, ['夸克'], ['File$shareA*newToken*fid*token***S01E01.mkv']));
    }
    started.resolve();
    return oldList.promise;
  });
  const old = api.fetchCatResolvedDetailCached(options);
  await started.promise;
  mode = false;
  api.clearCatDetailCache();
  const current = await api.fetchCatResolvedDetailCached(options);
  oldList.resolve(response({ ok: true, vod_play_url: 'File$shareA*oldToken*fid*token***S01E01.mkv' }));
  await old;
  const cached = await api.fetchCatResolvedDetailCached(options);
  assert.equal(cached, current);
  assert.equal(cached.panMock, false);
  assert.match(cached.sources[0].episodeSegments[0], /newToken/);
  assert.equal(detailCalls, 2);
});
