const { test } = require('node:test');
const assert = require('node:assert/strict');
const { buildSync } = require('esbuild');
const compiled = buildSync({ stdin: { contents: `
  export * from './src/shared/catpawrunner.js';
  export * from './src/shared/detailNavigation.js';
`, resolveDir: process.cwd() }, bundle: true, platform: 'node', format: 'cjs', write: false });
const loaded = { exports: {} };
new Function('module', 'exports', 'require', compiled.outputFiles[0].text)(loaded, loaded.exports, require);
const api = loaded.exports;
const opts = { apiBase: 'https://runner.example', spiderApi: '/aaaaaaaaaa/spider/any-author/3', siteDetail: 'movie' };
const response = data => ({ ok: true, status: 200, json: async () => data });
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
const tick = () => new Promise(resolve => setImmediate(resolve));
const nav = (id, action = 'detail', extra = {}) => ({
  vod_id: id, vod_name: id, vod_navigation: { action, payload: { id }, ...extra },
});
const share = (id, provider = 'quark') => nav(`opaque-${id}`, 'detail', {
  provider, share_flag: `${provider === 'baidu' ? '百度' : '夸克'}-${id}`,
  share_url: `https://${provider === 'baidu' ? 'pan.baidu.com/s/1' : 'pan.quark.cn/s/'}${id}`,
});
const leaf = (id, flag = '蓝光HDR') => ({ vod_id: 'leaf', vod_play_from: flag, vod_play_url: `E1$${id}` });
const mockFetch = (t, fn) => {
  api.clearCatDetailCache();
  t.mock.method(globalThis, 'fetch', (url, init) => fn(new URL(url, 'https://movie.example'), JSON.parse(init.body || '{}')));
};

test('normal detail follows each script action and emits partial leaves without changing source parsing', async t => {
  const wait = deferred(), both = deferred();
  const requested = [], updates = [];
  const root = { pan_mock: false, vod: { vod_name: 'Movie title' }, list: [
    nav('folder', 'category'), nav('special', 'resources', { payload: { cursor: 'original-cursor', limit: 2 } }),
  ] };
  mockFetch(t, async (url, body) => {
    if (url.pathname.endsWith('/detail')) return response(root);
    requested.push({ action: url.pathname.split('/').pop(), body });
    if (requested.length === 2) both.resolve();
    if (url.pathname.endsWith('/category')) {
      await wait.promise;
      return response({ pan_mock: false, list: [leaf('native-full-id***keep')] });
    }
    return response({ pan_mock: false, list: [leaf('other-private', '光鸭原画')] });
  });
  const work = api.fetchCatResolvedDetailCached({ ...opts, onUpdate: value => updates.push(value) });
  await both.promise;
  await tick();
  assert.equal(updates.some(value => value.sources.some(s => s.label === '光鸭原画') && !value.resolutionComplete), true);
  wait.resolve();
  const result = await work;
  assert.equal(result.title, 'Movie title');
  assert.deepEqual(requested.map(x => x.action).sort(), ['category', 'resources']);
  assert.deepEqual(requested.find(x => x.action === 'resources').body, { cursor: 'original-cursor', limit: 2 });
  assert.deepEqual(result.sources.map(s => s.episodeSegments[0]).sort(), ['E1$native-full-id***keep', 'E1$other-private']);
  assert.equal(result.resolutionComplete, true);
  await api.fetchCatResolvedDetailCached(opts);
  assert.equal(requested.length, 2);
});

for (const panMock of [false, true]) {
  test(`unclaimed navigation share hints keep their script action and do not deduplicate (${panMock})`, async t => {
    const calls = [], lists = [];
    mockFetch(t, (url, body) => {
      if (body.id === 'movie') return response({ pan_mock: panMock, list: [nav('custom', 'resources')] });
      if (url.pathname.endsWith('/resources')) {
        // Custom script operations pass through Runner without normalizing
        // their descriptors. Hints alone cannot establish supported ownership.
        return response({ list: [
          ...['unknown-a', 'unknown-b'].map(id => nav(id, 'detail', {
            provider: 'xunlei', share_flag: '迅雷-shareA', share_url: 'https://pan.xunlei.com/s/shareA',
          })),
          ...['lookalike-a', 'lookalike-b'].map(id => nav(id, 'detail', {
            provider: 'quark', share_flag: '夸克-shareA', share_url: 'https://pan.quark.cn.example/s/shareA',
          })),
          share('knownShare'),
        ] });
      }
      if (url.pathname === '/api/pan/quark/list') {
        lists.push(body.flag);
        return response({ ok: true, vod_play_url: 'E1$knownShare*token*fid*ftoken***Movie.mkv' });
      }
      calls.push(body.id);
      return response({ list: [leaf(`native-${body.id}`)] });
    });
    const result = await api.fetchCatResolvedDetailCached({ ...opts, sequentialNavigation: true });
    assert.deepEqual(calls.sort(), [
      'unknown-a', 'unknown-b', 'lookalike-a', 'lookalike-b',
      ...(panMock ? [] : ['opaque-knownShare']),
    ].sort());
    assert.equal(lists.length, panMock ? 1 : 0);
    assert.equal(result.sources.length, 5);
    for (const id of ['unknown-a', 'unknown-b', 'lookalike-a', 'lookalike-b']) {
      assert.ok(result.sources.some(source => source.provider === '' && source.episodeSegments.includes(`E1$native-${id}`)));
    }
  });
}

test('pan_mock local lists run concurrently even for the same provider and share leaves skip script', async t => {
  const held = deferred(), started = deferred();
  const calls = [], updates = [];
  mockFetch(t, async (url, body) => {
    if (url.pathname.endsWith('/detail')) {
      assert.equal(body.id, 'movie', 'private share ID must not be sent to the script');
      return response({ pan_mock: true, list: [share('shareA'), share('shareB'), share('shareC', 'baidu'), leaf('native')] });
    }
    calls.push(body.flag);
    if (body.flag.endsWith('shareA')) { started.resolve(); await held.promise; }
    return response({ ok: true, vod_play_url: `E1$${body.flag}*complete*file*id` });
  });
  const work = api.fetchCatResolvedDetailCached({ ...opts, onUpdate: v => updates.push(v) });
  let result;
  try {
    await started.promise;
    await tick();
    assert.equal(calls.some(x => x.endsWith('shareC')), true);
    assert.equal(calls.some(x => x.endsWith('shareB')), true);
    assert.equal(updates.some(x => x.sources.some(s => s.label === '百度-shareC' && s.episodeSegments.length) && !x.resolutionComplete), true);
  } finally {
    held.resolve();
    result = await work;
  }
  assert.equal(calls.length, 3);
  assert.equal(result.sources.length, 4);
  assert.equal(result.sources.filter(x => x.sourceKind === 'panmock').length, 3);
  assert.equal(new Set(result.sources.map(x => x.key)).size, 4);
});

test('ordinary details retain concurrent share resolution within and across sites', async t => {
  const held = deferred(), started = deferred(), calls = [];
  const updates = [];
  mockFetch(t, async (url, body) => {
    if (url.pathname.endsWith('/detail')) {
      const ids = body.id === 'movie-a' ? ['shareA', 'shareB'] : ['shareC'];
      return response({ pan_mock: true, list: [{
        vod_id: body.id, vod_play_from: ids.map(id => `夸克-${id}`).join('$$$'),
        vod_play_url: ids.map(id => `https://pan.quark.cn/s/${id}`).join('$$$'),
      }] });
    }
    calls.push(body.flag);
    if (body.flag.endsWith('shareA')) { started.resolve(); await held.promise; }
    return response({ ok: true, vod_play_url: `E1$${body.flag}*complete*file*id` });
  });
  const a = api.fetchCatResolvedDetailCached({
    ...opts, siteDetail: 'movie-a', onUpdate: value => updates.push(value),
  });
  const b = api.fetchCatResolvedDetailCached({
    ...opts, spiderApi: '/bbbbbbbbbb/spider/another-site/3', siteDetail: 'movie-b',
  });
  try {
    await started.promise;
    await tick();
    assert.ok(calls.some(flag => flag.endsWith('shareB')), 'same-detail lists were serialized');
    assert.ok(calls.some(flag => flag.endsWith('shareC')), 'another site shared a provider queue');
    assert.ok(updates.some(value => !value.resolutionComplete &&
      value.sources.some(source => source.label === '夸克-shareB' && source.episodeSegments.length)),
    'a fast ordinary share stopped producing incremental candidates');
  } finally {
    held.resolve();
    await Promise.all([a, b]);
  }
  assert.equal(calls.length, 3);
});

for (const panMock of [false, true]) {
  test(`smart navigation serializes categories, not share parsing (pan_mock=${panMock})`, async t => {
    const categoryGate = deferred(), categoryStarted = deferred();
    const shareGate = deferred(), shareStarted = deferred();
    const calls = [], updates = [];
    mockFetch(t, async (url, body) => {
      if (body.id === 'movie') return response({ pan_mock: panMock,
        list: [nav('groupA', 'category'), nav('groupB', 'category')] });
      if (url.pathname.endsWith('/category')) {
        calls.push(body.id);
        if (body.id === 'groupA') {
          categoryStarted.resolve();
          await categoryGate.promise;
          return response({ pan_mock: panMock, list: [share('shareA'), share('shareB')] });
        }
        return response({ pan_mock: panMock, list: [leaf('native')] });
      }
      const id = panMock ? body.flag.split('/').pop() : body.id.slice(7);
      calls.push(id);
      if (id === 'shareA') { shareStarted.resolve(); await shareGate.promise; }
      if (panMock) return response({ ok: true, vod_play_url: `E1$${id}*complete*file*id` });
      return response({ pan_mock: false, list: [leaf(`${id}*complete*file*id`, `夸克-${id}`)] });
    });
    const work = api.fetchCatResolvedDetailCached({
      ...opts, sequentialNavigation: true, onUpdate: value => updates.push(value),
    });
    try {
      await categoryStarted.promise;
      await tick();
      assert.equal(calls.includes('groupB'), false, 'a second category ran before the first finished');
      categoryGate.resolve();
      await shareStarted.promise;
      await tick();
      assert.equal(calls.includes('shareB'), true, 'same-provider parsing was serialized');
      assert.equal(calls.includes('groupB'), false, 'the site advanced before this group finished');
      assert.ok(updates.some(value => value.sources.some(source => source.episodeSegments.some(segment => segment.includes('shareB')))),
        'a completed share must be available without waiting for the slow share');
    } finally {
      categoryGate.resolve();
      shareGate.resolve();
      await work;
    }
    assert.deepEqual(calls, ['groupA', 'shareA', 'shareB', 'groupB']);
  });
}

test('pan_mock false requests selected share through script and preserves Runner file IDs', async t => {
  const calls = [];
  mockFetch(t, (url, body) => {
    assert.equal(url.origin, 'https://runner.example');
    assert.ok(url.pathname.endsWith('/detail'));
    calls.push(body.id);
    if (body.id === 'movie') return response({ pan_mock: false, list: [share('shareA'), share('shareB')] });
    return response({ pan_mock: false, list: [leaf(`${body.id}*token*fid*ftoken***Movie.mkv`, `夸克-${body.id.slice(7)}`)] });
  });
  const result = await api.fetchCatResolvedDetailCached(opts);
  assert.deepEqual(calls, ['movie', 'opaque-shareA', 'opaque-shareB']);
  assert.equal(result.sources.every(x => x.sourceKind === 'normal' && x.provider === ''), true);
  assert.match(result.sources[0].episodeSegments[0], /\*token\*fid\*ftoken/);
});

test('nested navigation preserves unknown items as diagnostics and reports loops, not empty success', async t => {
  mockFetch(t, (url, body) => response(body.id === 'movie'
    ? { pan_mock: false, list: [nav('group', 'category'), leaf('native')] }
    : { pan_mock: false, list: [nav('group', 'category'), null, { vod_id: 'unknown', custom: true }] }));
  const result = await api.fetchCatResolvedDetailCached(opts);
  assert.equal(result.sources.some(x => x.episodeSegments.includes('E1$native')), true);
  assert.equal(result.navigationErrors.length, 3);
  assert.match(result.navigationErrors.map(x => x.message).join(' '), /循环引用/);
  assert.equal(result.navigationErrors.some(x => x.item && x.item.custom), true);
  assert.equal(result.raw.list[0].vod_navigation.action, 'category');
});

test('category pagination preserves operation and original parameters', async () => {
  const calls = [], got = [];
  await api.walkDetailNavigation({ list: [nav('pages', 'resources', { payload: { id: 'pages', page: 1, cursor: 'keep' } })] }, {
    request: async (action, payload) => {
      calls.push({ action, payload });
      return { page: payload.page, pagecount: 3, list: [leaf(`page-${payload.page}`)] };
    },
    visit: async doc => { got.push(doc.list[0].vod_play_url); },
  });
  assert.deepEqual(calls.map(x => x.action), ['resources', 'resources', 'resources']);
  assert.deepEqual(calls.map(x => x.payload.page), [1, 2, 3]);
  assert.ok(calls.every(x => x.payload.cursor === 'keep'));
  assert.deepEqual(got, ['E1$page-1', 'E1$page-2', 'E1$page-3']);
});

test('cancelling navigation does not turn an incomplete parallel result into a complete cache entry', async t => {
  const held = deferred(), started = deferred(), controller = new AbortController(), calls = [];
  mockFetch(t, async (url, body) => {
    if (url.pathname.endsWith('/detail')) return response({ pan_mock: true, list: [share('shareA'), share('shareB')] });
    calls.push(body.flag);
    started.resolve();
    await held.promise;
    return response({ ok: true, vod_play_url: 'E1$file' });
  });
  const work = api.fetchCatResolvedDetailCached({ ...opts, signal: controller.signal });
  await started.promise;
  controller.abort();
  held.resolve();
  const result = await work;
  await tick();
  assert.equal(calls.length, 2, 'independent shares had already started concurrently');
  assert.equal(result.resolutionComplete, false);
});

test('one cancelled consumer cannot cancel another caller sharing the same in-flight list', async t => {
  const held = deferred(), started = deferred(), controller = new AbortController(), calls = [];
  mockFetch(t, async (_url, body) => {
    calls.push(body.flag);
    if (body.flag.endsWith('shareA')) { started.resolve(); await held.promise; }
    return response({ ok: true, vod_play_url: 'E1$file' });
  });
  const a = api.requestPanListByProviderFlag({ provider: 'quark', playFlag: '夸克-shareA', shareUrl: 'https://pan.quark.cn/s/shareA' });
  await started.promise;
  const b = { provider: 'quark', playFlag: '夸克-shareB', shareUrl: 'https://pan.quark.cn/s/shareB' };
  const cancelled = api.requestPanListByProviderFlag({ ...b, signal: controller.signal });
  const wanted = api.requestPanListByProviderFlag(b);
  controller.abort(); held.resolve();
  await Promise.all([a, cancelled, wanted]);
  assert.equal(calls.length, 2);
});

test('pagination accepts string pages and sentinel counts without adding an unrelated page key', async () => {
  const calls = [], errors = [];
  await api.walkDetailNavigation({ list: [nav('pages', 'resources', { payload: { id: 'pages', pg: '1', cursor: 'keep' } })] }, {
    request: async (_action, payload) => {
      assert.equal('page' in payload, false);
      calls.push(Number(payload.pg));
      return { page: String(payload.pg), pagecount: '2147483647', list: Number(payload.pg) < 3 ? [leaf('page-' + payload.pg)] : [] };
    },
    visit: async () => {}, onError: error => errors.push(error),
  });
  assert.deepEqual(calls, [1, 2, 3]);
  assert.deepEqual(errors, []);
});

test('nonadvancing page numbers or repeated page data stop with an explicit diagnostic', async () => {
  for (const kind of ['number', 'data']) {
    const calls = [], errors = [], leaves = [];
    await api.walkDetailNavigation({ list: [nav('pages', 'category', { payload: { id: 'pages', page: 1 } })] }, {
      request: async (_action, payload) => {
        calls.push(payload.page);
        return { page: kind === 'number' ? 1 : payload.page, pagecount: 2147483647, list: [leaf('repeated')] };
      },
      visit: async doc => leaves.push(doc), onError: error => errors.push(error),
    });
    assert.deepEqual(calls, [1, 2]);
    assert.equal(leaves.length, 1);
    assert.match(errors[0].message, /分页/);
  }
});

test('a changed pan_mock mode is reported rather than mixing incompatible source ownership', async () => {
  const errors = [];
  await api.walkDetailNavigation({ pan_mock: false, list: [nav('group', 'category')] }, {
    request: async () => ({ pan_mock: true, list: [share('shareA')] }),
    visit: async () => assert.fail('mode-changed leaf was accepted'), onError: error => errors.push(error),
  });
  assert.match(errors[0].message, /模式已变更.*刷新/);
});

test('a selected share can return further navigation without any provider queue', async () => {
  const calls = [];
  await api.walkDetailNavigation({ pan_mock: false, list: [share('shareA')] }, {
    request: async (_action, payload) => {
      calls.push(payload.id);
      return { pan_mock: false, list: payload.id === 'opaque-shareA' ? [share('shareB')] : [leaf('complete')] };
    },
    visit: async doc => assert.equal(doc.list[0].vod_play_url, 'E1$complete'),
  });
  assert.deepEqual(calls, ['opaque-shareA', 'opaque-shareB']);
});

for (const panMock of [true, false]) {
  test(`later navigation credentials are usable and identical credentials still deduplicate (${panMock})`, async t => {
    const requests = [];
    const card = (id, pass) => nav(id, 'detail', {
      provider: 'quark', share_flag: '夸克-protectedShare' + (pass ? '-' + pass : ''),
      share_url: 'https://pan.quark.cn/s/protectedShare' + (pass ? '?pwd=' + pass : ''),
    });
    mockFetch(t, (_url, body) => {
      if (body.id === 'movie') return response({ pan_mock: panMock,
        list: [nav('groupA', 'category'), nav('groupB', 'category'), nav('groupC', 'category')] });
      if (body.id === 'groupA') return response({ pan_mock: panMock, list: [card('without-pass', '')] });
      if (body.id === 'groupB') return response({ pan_mock: panMock, list: [card('with-pass', '1234')] });
      if (body.id === 'groupC') return response({ pan_mock: panMock, list: [card('duplicate-pass', '1234')] });
      const valid = panMock ? body.passcode === '1234' : body.id !== 'without-pass';
      requests.push(panMock ? body.passcode : body.id);
      if (!valid) return response({ ok: false, message: 'password required', pan_mock: panMock });
      return response(panMock ? { ok: true, vod_play_url: 'E1$complete*file*id*token' }
        : { pan_mock: false, list: [leaf('complete*file*id*token', '夸克-protectedShare-1234')] });
    });
    const result = await api.fetchCatResolvedDetailCached({ ...opts, sequentialNavigation: true });
    assert.ok(result.sources.some(source => source.episodeSegments.length), 'the later valid card was dropped');
    assert.deepEqual(requests, panMock ? ['', '1234'] : ['without-pass', 'with-pass']);
  });
}

test('an unchanged navigation ID does not hide enriched local share credentials', async t => {
  const passwords = [];
  mockFetch(t, (_url, body) => {
    if (body.id === 'movie') return response({ pan_mock: true, list: [nav('a', 'category'), nav('b', 'category')] });
    if (body.id) {
      const pass = body.id === 'b' ? '1234' : '';
      return response({ pan_mock: true, list: [nav('same-card-id', 'detail', {
        provider: 'quark', share_flag: '夸克-protectedShare' + (pass ? '-' + pass : ''),
        share_url: 'https://pan.quark.cn/s/protectedShare' + (pass ? '?pwd=' + pass : ''),
      })] });
    }
    passwords.push(body.passcode);
    return response(body.passcode ? { ok: true, vod_play_url: 'E1$complete' } : { ok: false, message: 'password required' });
  });
  const result = await api.fetchCatResolvedDetailCached({ ...opts, sequentialNavigation: true });
  assert.deepEqual(passwords, ['', '1234']);
  assert.ok(result.sources.some(source => source.episodeSegments.length));
});
