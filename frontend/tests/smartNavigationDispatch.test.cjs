const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { build } = require('esbuild');

const ready = build({
  stdin: {
    contents: `
      export * from './src/shared/catpawrunner.js';
      export * from './src/shared/detailNavigation.js';
      export { runSmartPlaybackController } from './src/shared/smartPlaybackController.js';
    `,
    resolveDir: process.cwd(),
  },
  bundle: true, platform: 'node', format: 'cjs', write: false,
  plugins: [{
    name: 'fixture-search',
    setup(build) {
      build.onResolve({ filter: /^\.\/searchSession$/ }, args => args.importer.endsWith('smartPlaybackController.js')
        ? { path: 'search', namespace: 'fixture' } : null);
      build.onLoad({ filter: /.*/, namespace: 'fixture' }, () => ({ contents: `
        export const getSearchSessionLaneSnapshot = () => null;
        export const getSearchSessionLaneStatus = () => 'completed';
        export const performSearchSessionSearch = async () => null;
        export const subscribeSearchSessionLane = () => () => {};
      ` }));
    },
  }],
}).then(result => {
  const loaded = { exports: {} };
  new Function('module', 'exports', 'require', result.outputFiles[0].text)(loaded, loaded.exports, require);
  return loaded.exports;
});

const opts = { apiBase: 'https://runner.example', spiderApi: '/aaaaaaaaaa/spider/test/3', siteDetail: 'movie1' };
const response = data => ({ ok: true, status: 200, json: async () => data });
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
const folder = id => ({ vod_id: id, vod_navigation: { action: 'category', payload: { id } } });
const share = id => ({
  vod_id: id, vod_navigation: {
    action: 'detail', payload: { id }, provider: 'quark',
    share_flag: '夸克-' + id, share_url: 'https://pan.quark.cn/s/' + id,
  },
});
const file = id => ({ vod_play_from: '夸克-' + id, vod_play_url: 'S01E01$' + id + '*token*fid*ftoken' });
const section = (text, start, end) => {
  const from = text.indexOf(start), to = text.indexOf(end, from + start.length);
  assert.ok(from >= 0 && to > from, 'source fixture boundary changed: ' + start);
  return text.slice(from, to);
};

// Exercise the actual page -> runtime -> shared detail option forwarding too.
function pageFixture(api, cache) {
  const source = readFileSync('src/pages/play/PlayPage.vue', 'utf8');
  const runtimeSource = readFileSync('src/shared/smartPlaybackRuntime.js', 'utf8');
  const runtimeCode = runtimeSource.slice(runtimeSource.indexOf('export const ensureSiteResultDetailCached ='))
    .replace('export const', 'const');
  const normalize = value => typeof value === 'string' ? value.trim() : '';
  const runtime = new Function('getSiteResultDetail', 'normalizeString', 'fetchCatResolvedDetailCached',
    runtimeCode + '\nreturn ensureSiteResultDetailCached;')(() => null, normalize, api.fetchCatResolvedDetailCached);
  const ensure = new Function('ensureSiteResultDetailCachedRuntime', 'createNavigationDispatchGuard',
    'return ({' + section(source, '    async ensureSiteResultDetailCached(', '    cacheRecognitionForSiteResult(') +
    '}).ensureSiteResultDetailCached;')(runtime, api.createNavigationDispatchGuard);
  const callbacks = new Function('return ({' +
    section(source, '    buildSmartPlaybackControllerCallbacks(', '    async runSmartPlaybackWithConstraints(') +
    '}).buildSmartPlaybackControllerCallbacks;')();
  const page = {
    siteResultDetailStore: {}, siteResultDetailInFlightMap: {},
    getCachedSiteResultDetail: item => cache.get(item.id),
    buildSiteDetailDedupeKey: item => item.id,
    ensurePlayRuntimeSettings: async () => ({ catpawrunnerApiBase: opts.apiBase }),
    awaitSiteDetailInFlight: entry => entry.promise,
    applySiteResultDetailUpdate: (item, detail) => cache.set(item.id, detail),
    emitSiteDetailInFlightUpdate: (entry, detail) => entry.listeners.forEach(fn => fn(detail)),
    cacheRecognitionForSiteResult() {},
  };
  page.ensureSiteResultDetailCached = ensure.bind(page);
  page.smartEnsure = callbacks.call(page, { runSeq: 1 }).ensureSiteResultDetailCached;
  return page;
}

for (const panMock of [true, false]) {
  for (const reason of ['pending', 'confirmed', 'browsing']) {
    const browsing = reason === 'browsing';
    test(`smart category stop preserves shared browsing (pan_mock=${panMock}, ${reason})`, async t => {
      const api = await ready;
      api.clearCatDetailCache();
      const held = deferred(), pending = deferred(), calls = [];
      const cache = new Map(), details = [];
      const page = pageFixture(api, cache);
      const first = { id: 'item1', siteKey: 'site', siteDetail: 'movie1', spiderApi: opts.spiderApi };
      const second = { id: 'item2', siteKey: 'site', siteDetail: 'movie2', spiderApi: opts.spiderApi };
      let confirmed = false;
      t.mock.method(globalThis, 'fetch', async (rawUrl, init) => {
        const body = JSON.parse(init.body);
        const id = body.id || new URL(body.flag).pathname.split('/').pop();
        calls.push(id);
        if (id === 'movie1') return response({ pan_mock: panMock, list: [folder('groupA'), folder('groupB')] });
        if (id === 'movie2') return response({ pan_mock: panMock, list: [file(id)] });
        if (id === 'groupA') return response({ pan_mock: panMock, list: [share('shareFast'), share('shareSlow')] });
        if (id === 'groupB') return response({ pan_mock: panMock, list: [file(id)] });
        if (id === 'shareSlow') await held.promise;
        return response(panMock ? { ok: true, vod_play_url: file(id).vod_play_url }
          : { pan_mock: false, list: [file(id)] });
      });
      const running = api.runSmartPlaybackController({
        runSeq: 1, query: 'movie', globalEpisode: 1,
        isRunStopped: () => confirmed,
        getSearchState: () => ({ snapshot: { items: [first, second] }, status: 'completed' }),
        buildSiteItemsFromSnapshot: snapshot => snapshot.items,
        getCachedSiteResultDetail: item => cache.get(item.id),
        ensureSiteResultDetailCached: (item, options) => {
          const work = page.smartEnsure(item, options);
          details.push(work);
          return work;
        },
        collectRecognitionCandidatesForTarget: item => (cache.get(item.id)?.sources || [])
          .filter(source => source.episodeSegments.length)
          .map(source => ({ siteItem: item, panKey: source.key, itemIndex: 0 })),
        buildPanSourcesFromDetail: detail => detail.sources,
        buildPanSegment: entry => ({ episodeUrl: entry.episodeSegments[0].split('$').slice(1).join('$'), index: 0 }),
        playResolvedSiteSegment: async () => true,
        setPendingRunSeq: () => pending.resolve(),
      });
      let timeout, browsingWork;
      try {
        await Promise.race([pending.promise, new Promise((_, reject) => {
          timeout = setTimeout(() => reject(new Error('smart did not reach playback pending')), 2000);
        })]);
        await running;
        confirmed = reason !== 'pending';
        assert.ok(calls.includes('shareSlow'), 'the slow share must already have started in parallel');
        if (browsing) browsingWork = page.ensureSiteResultDetailCached(first);
      } finally {
        clearTimeout(timeout);
        held.resolve();
        await Promise.all(details);
      }
      const result = browsing ? await browsingWork : await details[0];
      assert.equal(calls.includes('movie2'), false, 'the original site worker stop must remain effective');
      assert.equal(calls.includes('groupB'), browsing, 'only an independent shared caller should keep navigation running');
      assert.equal(result.resolutionComplete, browsing);
      assert.equal(result.sources.some(source => source.episodeSegments.some(x => x.includes('shareFast'))), true);
      if (!browsing) {
        const reopened = await page.ensureSiteResultDetailCached(first);
        assert.equal(reopened.resolutionComplete, true, 'a stopped navigation was cached as fully resolved');
        assert.equal(calls.filter(id => id === 'groupB').length, 1);
        assert.equal(calls.filter(id => id === 'shareFast').length, 1, 'reopening must reuse the existing list/detail caches');
      }
    });
  }
}

test('another wait by the same smart consumer does not disable its navigation stop', async () => {
  const api = await ready;
  const wait = () => false;
  const guard = api.createNavigationDispatchGuard(wait);
  guard.share(wait);
  assert.equal(await guard.wait(), false);
  guard.share();
  assert.equal(await guard.wait(), true);
});

test('a shared detail caller releases a navigation dispatch wait without a provider queue', async t => {
  const api = await ready;
  api.clearCatDetailCache();
  const waiting = deferred(), release = deferred(), calls = [];
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    const { id } = JSON.parse(init.body);
    calls.push(id);
    return response(id === 'movie1' ? { list: [folder('group')] } : { list: [file('complete')] });
  });
  const smart = api.fetchCatResolvedDetailCached({
    ...opts, sequentialNavigation: true,
    waitForNavigationDispatch: () => { waiting.resolve(); return release.promise; },
  });
  await waiting.promise;
  const browsing = api.fetchCatResolvedDetailCached(opts);
  let timeout;
  try {
    const detail = await Promise.race([browsing, new Promise((_, reject) => {
      timeout = setTimeout(() => reject(new Error('shared browsing remained blocked behind smart')), 1500);
    })]);
    assert.equal(detail.resolutionComplete, true);
    assert.deepEqual(calls, ['movie1', 'group']);
  } finally {
    clearTimeout(timeout);
    release.resolve(false);
    await smart;
  }
});

test('dispatch waiting applies to categories, not the concurrent share resolutions', async t => {
  const api = await ready;
  const next = deferred(), both = deferred(), calls = [];
  let guards = 0, shares = 0;
  const work = api.walkDetailNavigation({ pan_mock: false, list: [folder('groupA'), folder('groupB')] }, {
    sequentialNavigation: true,
    waitForNavigationDispatch: () => ++guards === 1 ? true : next.promise,
    request: async (_action, payload) => {
      calls.push(payload.id);
      if (payload.id === 'groupA') return { list: [share('shareFast'), share('shareSlow')] };
      if (payload.id === 'groupB') return { list: [file('groupB')] };
      if (++shares === 2) both.resolve();
      await both.promise;
      return { list: [file(payload.id)] };
    },
    visit: async () => {},
  });
  await both.promise;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(calls.includes('groupB'), false);
  assert.equal(guards, 2, 'share detail requests must bypass the navigation dispatch wait');
  next.resolve(true);
  assert.equal(await work, true);
  assert.equal(calls.at(-1), 'groupB');
});

test('ordinary complete details never invoke the navigation dispatch guard', async t => {
  const api = await ready;
  api.clearCatDetailCache();
  t.mock.method(globalThis, 'fetch', async () => response({ pan_mock: false, list: [file('normal')] }));
  const result = await api.fetchCatResolvedDetailCached({
    ...opts, waitForNavigationDispatch: () => assert.fail('ordinary detail entered navigation dispatch'),
  });
  assert.equal(result.resolutionComplete, true);
  assert.equal(result.sources.length, 1);
});
