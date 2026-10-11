const { test } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('esbuild');

const compiled = buildSync({
  stdin: {
    contents: `
      export * from './src/shared/catpawrunner.js';
      export { getPanShareInput, getPanListInput, panMockProviderFromFlag, extractTianyiShareCodeAndAccessCode } from './src/utils/matchCore.js';
      export { buildSourceSegmentItems } from './src/shared/smartSourceRecognition.js';
      export { resolveHistoryPlayFlagPlaybackTarget } from './src/shared/smartPlaybackRuntime.js';
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
    baidu: ['百度-shareA', '百度-1share-A_b-1234', '百度', '百度-1234', '百度原画-shareA', '百度原画(无限)-shareA'],
    quark: ['夸克-shareA', '夸克-shareA-1234', '夸克', '夸克-1234', '夸父-shareA'],
    uc: ['UC-shareA', 'UC-shareA-1234', 'UC', 'uc-1234', '优夕-shareA'],
    '189': ['天翼-shareA', '天翼-shareA-1234', '天翼', '天翼-1234', '天意-shareA'],
    '139': ['移动-shareA', '移动-shareA-1234', '移动', '移动-1234', '逸动-shareA'],
  })) for (const label of labels) assert.equal(api.panMockProviderFromFlag(label), provider);
  for (const label of ['百度-1234', '夸克', '光鸭', '蓝光HDR']) assert.equal(api.getPanShareInput(label), null);
  assert.equal(api.normalizeSourceEntry({ label: 'saved', provider: 'quark' }).sourceKind, 'panmock');
});

test('share URLs retain identity and actual passwords, including mobile hash routes', () => {
  for (const [flag, url, provider, id, passcode] of [
    ['百度-shareA-1234', 'https://pan.baidu.com/s/1shareA?pwd=1234', 'baidu', 'shareA', '1234'],
    ['夸克-shareA', 'https://pan.quark.cn/s/shareA', 'quark', 'shareA', ''],
    ['UC-shareA', 'https://drive.uc.cn/s/shareA', 'uc', 'shareA', ''],
    ['天翼-shareA-1234', 'https://cloud.189.cn/t/shareA?accessCode=1234', '189', 'shareA', '1234'],
    ['移动-shareA-1234', 'https://yun.139.com/shareweb/#/w/i/shareA?pwd=1234', '139', 'shareA', '1234'],
    ['百度-1share-A_b-abcd', 'https://pan.baidu.com/s/11share-A_b-abcd', 'baidu', '1share-A_b-abcd', ''],
    ['百度-1share-A_b-abcd-1234', 'https://pan.baidu.com/s/11share-A_b-abcd', 'baidu', '1share-A_b-abcd', '1234'],
    ['百度-otherShare-1234', 'https://pan.baidu.com/s/11share-A_b-abcd', 'baidu', '1share-A_b-abcd', ''],
    ['百度-shareA-1234', 'https://pan.baidu.com/s/1shareA?pwd=abcd', 'baidu', 'shareA', 'abcd'],
  ]) {
    const share = api.getPanShareInput(flag, url);
    assert.deepEqual([share.provider, share.shareId, share.passcode], [provider, id, passcode]);
  }
  for (const url of ['https://pan.quark.cn.evil.test/s/shareA', 'https://user@pan.quark.cn/s/shareA', 'https://caiyun.139.com/m/i?pwd=1234']) {
    assert.equal(api.getPanShareInput('夸克', url), null);
  }
});

test('explicit history list calls accept canonical flags and reuse the normal share cache', async (t) => {
  const calls = [];
  installFetch(t, (url, body) => {
    calls.push({ path: url.pathname, body });
    return response({ ok: true, vod_play_url: 'E1$complete-file-id' });
  });
  const baiduId = '1Abcdefghij-klmn_opqrs';
  assert.equal(baiduId.length, 22);
  for (const [provider, label, shareId, password, shareUrl] of [
    ['baidu', `百度-${baiduId}-a1b2`, baiduId, 'a1b2', `https://pan.baidu.com/s/1${baiduId}?pwd=a1b2`],
    ['baidu', '百度-shareA', 'shareA', '', 'https://pan.baidu.com/s/1shareA'],
    ['quark', '夸克-shareA-abcd', 'shareA', 'abcd', 'https://pan.quark.cn/s/shareA?pwd=abcd'],
    ['uc', 'UC-shareA-abcd', 'shareA', 'abcd', 'https://drive.uc.cn/s/shareA?pwd=abcd'],
    ['189', '天翼-shareA-abcd', 'shareA', 'abcd', 'https://cloud.189.cn/t/shareA?accessCode=abcd'],
    ['139', '移动-shareA-abcd', 'shareA', 'abcd', 'https://caiyun.139.com/m/i?shareA&pwd=abcd'],
  ]) {
    api.clearCatDetailCache();
    const before = calls.length;
    const data = await api.requestPanListByProviderFlag({ provider, playFlag: label });
    assert.ok(data, label + ' was rejected before the existing list call');
    assert.equal(calls.length, before + 1);
    assert.equal(calls.at(-1).path, `/api/pan/${provider}/list`);
    assert.equal(calls.at(-1).body.flag, shareUrl);
    const field = provider === 'baidu' ? 'pwd' : provider === '189' ? 'accessCode' : 'passcode';
    assert.equal(calls.at(-1).body[field], password);
    if (provider === '189') assert.equal(calls.at(-1).body.shareCode, shareId);
    assert.equal(await api.requestPanListByProviderFlag({ provider, playFlag: label, shareUrl }), data);
    assert.equal(calls.length, before + 1, 'history and detail must reuse the existing share cache');
    assert.equal(api.getPanShareInput(label, 'File$opaque-private-id'), null, 'normal detail ownership must stay strict');
  }
  for (const label of ['百度-1234', '夸克', '光鸭-shareA']) {
    assert.equal(await api.requestPanListByProviderFlag({ provider: api.panMockProviderFromFlag(label), playFlag: label }), null);
  }
});

test('the existing history replay entry still requests a canonical saved flag without fetching detail', async (t) => {
  const calls = [], cached = [];
  installFetch(t, (url, body) => {
    calls.push({ path: url.pathname, body });
    assert.equal(url.pathname, '/api/pan/quark/list');
    return response({ ok: true, vod_play_url: '第1季$shareA*token*fid*ftoken***S01E01.mkv' });
  });
  await api.resolveHistoryPlayFlagPlaybackTarget({
    historyRow: { playFlag: '夸克-shareA-abcd', siteKey: 'site', siteName: 'Site',
      spiderApi: options.spiderApi, siteDetail: 'film', contentKey: 'Movie' },
    globalEpisode: 1,
    cacheHistoryDetail: (_item, payload) => cached.push(payload.detail),
    collectCandidates: () => [],
  });
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0].body, { flag: 'https://pan.quark.cn/s/shareA?pwd=abcd', passcode: 'abcd' });
  assert.equal(cached.at(-1).sources[0].label, '夸克-shareA-abcd');
  assert.equal(cached.at(-1).sources[0].provider, 'quark');
});

test('decorated legacy Baidu flags still reach the existing history/list entry', async (t) => {
  const calls = [];
  installFetch(t, (url, body) => {
    calls.push({ path: url.pathname, body });
    assert.equal(url.pathname, '/api/pan/baidu/list', 'history acceleration must not refetch detail');
    return response({ ok: true, vod_play_url: '第1季$opaque|||S01E01.mkv' });
  });
  for (const flag of ['百度原画(无限)-1historyShareA', '百度原画HDR-1historyShareA', '百度原画(极速)-1historyShareA#旧后缀']) {
    api.clearCatDetailCache();
    const before = calls.length;
    await api.resolveHistoryPlayFlagPlaybackTarget({
      historyRow: { playFlag: flag, siteKey: 'site', siteName: 'Site',
        spiderApi: options.spiderApi, siteDetail: 'film', contentKey: 'Movie' },
      globalEpisode: 1, collectCandidates: () => [],
    });
    assert.equal(calls.length, before + 1, flag);
    assert.deepEqual(calls.at(-1).body, { flag, pwd: '' }, 'keep the old list request body');
    assert.equal(api.getPanShareInput(flag, 'File$opaque-private-id'), null);
  }
});

test('standalone flag adaptation does not guess an unknown Baidu hyphen boundary or override a real URL', () => {
  const flag = '百度-1share-A_b-abcd';
  const share = api.getPanListInput(flag);
  assert.equal(share.shareId, '1share-A_b-abcd');
  assert.equal(share.passcode, '');
  assert.equal(api.getPanListInput(flag, 'https://pan.baidu.com/s/1otherShare?pwd=4321').shareId, 'otherShare');
  assert.equal(api.getPanListInput('夸克-shareA-abcd', 'private-id'), null);
});

test('same-provider shares resolve separately through the original list flag parameter', async (t) => {
  const a = 'https://pan.quark.cn/s/shareA', b = 'https://pan.quark.cn/s/shareB';
  const native = 'https://image.example/cover*Author*1:20****opaque-E65';
  const listCalls = [], updates = [];
  installFetch(t, (url, body) => {
    if (url.pathname.endsWith('/detail')) return response(detail(true, ['夸克-shareA', '夸克-shareB', '蓝光HDR'], [a, b, `第一集$${native}`]));
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
    if (url.pathname.endsWith('/detail')) return response(detail(false, ['夸克-shareA'], [`第2季$${id}`]));
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
  assert.equal(calls[1].body.flag, '夸克-shareA');
  assert.equal(calls[1].body.siteApi, options.spiderApi);
});

test('unclaimed legacy/native labels keep the original raw-file directory matching', async (t) => {
  const labels = ['百度原画HDR-native', '夸父极速-native', '优夕极速-native', '天意原画-native', '逸动高清-native', '蓝光HDR',
    '百度', '百度-shareA', '夸克', '夸克-shareA', 'UC-shareA', '天翼-shareA', '移动-shareA'];
  const segment = '第2季/第1集$x***Show/第1季/E01.mkv';
  let panMock = false;
  installFetch(t, url => {
    assert.ok(url.pathname.endsWith('/detail'), 'unclaimed native data must not request a local list');
    return response(detail(panMock, labels, labels.map(() => segment)));
  });
  for (panMock of [false, true]) {
    api.clearCatDetailCache();
    const out = await api.fetchCatResolvedDetailCached(options);
    assert.equal(out.sources.length, labels.length);
    for (const source of out.sources) {
      assert.equal(source.provider, '');
      assert.equal(source.sourceKind, 'normal');
      const item = api.buildSourceSegmentItems(source)[0];
      assert.equal(item.allowDirectoryHints, false, source.label);
      assert.deepEqual(item.dirs, ['Show', '第1季'], source.label);
      assert.equal(item.episodeUrl, 'x***Show/第1季/E01.mkv');
    }
  }
  assert.equal(api.panMockProviderFromFlag(labels[0]), 'baidu', 'saved history still recognizes legacy names');
});

test('canonical Runner lists use their existing full file IDs, not names alone, for directory semantics', () => {
  const baiduId = Buffer.from(JSON.stringify({ surl: 'shareA', shareid: '123', uk: '456', fs_id: '789' })).toString('base64');
  for (const [label, id] of [
    ['百度-shareA', `${baiduId}|||E01.mkv`],
    ['夸克-shareA', 'shareA*token*fid*fileToken***E01.mkv'],
    ['UC-shareA', 'shareA*token*fid*fileToken***E01.mkv'],
    ['天翼-shareA', '123*456*E01.mkv'],
    ['移动-shareA', 'fileId*shareA***E01.mkv'],
  ]) {
    const item = api.buildSourceSegmentItems({ label, provider: '', episodeSegments: [`第2季$${id}`] })[0];
    assert.equal(item.allowDirectoryHints, true, label);
    assert.deepEqual(item.dirs, ['第2季'], label);
  }
  const saved = api.buildSourceSegmentItems({
    label: '夸父-shareA', provider: 'quark', episodeSegments: ['第2季$x***Show/第1季/E01.mkv'],
  })[0];
  assert.equal(saved.allowDirectoryHints, true);
  assert.deepEqual(saved.dirs, ['第2季']);
});

test('a complete source with no usable share stays native even when pan_mock is on', async (t) => {
  installFetch(t, url => {
    assert.ok(url.pathname.endsWith('/detail'), 'native source must not call local list');
    return response(detail(true, ['百度-shareA-abcd', '光鸭原画'], ['File$opaque-private-id', 'File$duck-native-id']));
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
        ? detail(true, ['夸克-shareA'], ['https://pan.quark.cn/s/shareA'])
        : detail(false, ['夸克-shareA'], ['File$shareA*newToken*fid*token***S01E01.mkv']));
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

test('Tianyi playback reads the final password suffix and keeps saved old flag compatibility', () => {
  for (const [flag, value, shareCode, accessCode] of [
    ['天翼-shareA-abcd', 'S01E01.mkv', 'shareA', 'abcd'],
    ['天翼-shareA', 'S01E01.mkv', 'shareA', ''],
    ['天翼-shareA-abcd', 'https://cloud.189.cn/t/shareA', 'shareA', 'abcd'],
    ['天翼-shareA-abcd', 'https://cloud.189.cn/t/shareA?accessCode=1234', 'shareA', '1234'],
    ['天翼-abcd', 'S01E01.mkv', '', 'abcd'],
    ['天意-shareA', 'abcd', 'shareA', 'abcd'],
  ]) assert.deepEqual(api.extractTianyiShareCodeAndAccessCode(flag, value), { shareCode, accessCode });
});
