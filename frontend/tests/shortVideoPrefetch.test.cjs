const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const { buildSync } = require('esbuild');
const { markRaw } = require('vue');

const source = readFileSync('src/pages/play/PlayPage.vue', 'utf8');
// Include the real TTL and page helpers, not copies of the prefetch implementation.
const componentCode = source.slice(source.indexOf('const THIRD_PARTY_PLAYERS ='), source.indexOf('</script>'))
  .replace('export default', 'this.component =');
const compiled = buildSync({
  stdin: {
    contents: `
      export { normalizeInt, normalizeString } from './src/shared/normalize.js';
      export { extractRawNamesFromEpisodeUrl } from './src/shared/catpawrunner.js';
      export { scoreEpisodeDisplayName } from './src/utils/matchCore.js';
      export { buildPlayHistoryPayload } from './src/shared/playHistoryRuntime.js';
      export { tmdbSeasonEpisodeOfGlobal, doubanSeasonEpisodeOfGlobal } from './src/shared/smartEpisodeMapping.js';
      export {
        buildPanSegment as buildPanSegmentRuntime,
        getSiteResultDetail as getSiteResultDetailRuntime,
        collectRecognitionCandidatesForTarget as collectRecognitionCandidatesForTargetRuntime,
        resolveCachedPlaybackTarget as resolveCachedPlaybackTargetRuntime,
      } from './src/shared/smartPlaybackRuntime.js';
      export {
        hasNonEmptyHeaders, resolvePanFamilyLabelForUi,
        resolveCandidatePanFamilyForPlayback, resolveCandidateQualityModeKeyForPlayback,
        isPlaybackCandidateAllowedByAction, comparePlaybackCandidatesForAction,
        comparePlaybackCandidatesByDefaultRules,
      } from './src/shared/playbackRuntime.js';
    `,
    resolveDir: process.cwd(),
  },
  bundle: true, platform: 'node', format: 'cjs', write: false,
});
const loaded = { exports: {} };
new Function('module', 'exports', 'require', compiled.outputFiles[0].text)(loaded, loaded.exports, require);
const shared = loaded.exports;
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};
const flush = () => new Promise(resolve => setImmediate(resolve));

function playback(id) {
  const playerUrl = `https://media.test/${id}.m3u8`;
  const playerHeaders = { Authorization: `Bearer ${id}` };
  return {
    playerUrl, playerHeaders, goProxyBase: `https://proxy.test/${id}`,
    watchReport: { id, flag: '线路', apiBase: 'https://runner.test', spiderApi: '/aaaaaaaaaa/spider/test/3' },
    lastGoProxyCandidate: { enabled: true, url: playerUrl, headers: playerHeaders },
    lastM3U8RelayCandidate: { enabled: true, url: playerUrl },
  };
}

function fixture() {
  const clock = { now: 1000 };
  const calls = { play: [], history: [], reports: [], context: [], browse: [], cached: [], forbidden: [], proxy: [], relay: [], autoplay: 0, resume: 0 };
  const site = { id: 'site:detail', siteKey: 'site', siteName: '站点', spiderApi: '/aaaaaaaaaa/spider/test/3', siteDetail: 'detail' };
  const pan = {
    key: 'pan', label: '线路', provider: '',
    episodeSegments: Array.from({ length: 5 }, (_, index) => `E${index + 1}$episode-${index}`),
  };
  const session = {
    currentContext: { itemId: site.id, siteKey: site.siteKey, siteDetail: site.siteDetail, panKey: pan.key, panFlag: pan.label, selectionKey: 'pan::2', itemIndex: 2, globalEpisode: 2 },
    lastBrowseContext: { itemId: site.id, panKey: pan.key },
  };
  const history = { current: { selectionKey: 'pan::2' }, watchReport: { id: 'current' } };
  const forbidden = name => () => { calls.forbidden.push(name); assert.fail(`unexpected ${name}`); };
  const context = vm.createContext({
    ...shared, ArtPlayer: {}, markRaw, window: { innerWidth: 1280 },
    Date: class extends Date { static now() { return clock.now; } },
    playbackSessionState: session,
    patchCurrentPlaybackContext(payload) {
      calls.context.push(payload);
      session.currentContext = { ...session.currentContext, ...payload };
    },
    patchLastBrowsePlaybackContext(payload) {
      calls.browse.push(payload);
      session.lastBrowseContext = { ...session.lastBrowseContext, ...payload };
    },
    async preparePlayHistoryContext(payload) { calls.history.push(payload); history.current = payload; },
    bindPlayHistoryWatchReport(report) { calls.reports.push(report); history.watchReport = report; },
    executeResolvedSitePlayback(params) {
      const request = { params, ...deferred() };
      calls.play.push(request);
      return request.promise;
    },
    resolveCachedPlaybackTargetRuntime(options) {
      calls.cached.push(options);
      return shared.resolveCachedPlaybackTargetRuntime(options);
    },
    async executeProxyRetryPlayback(options) {
      calls.proxy.push(options);
      return { ok: true, playerUrl: 'https://proxy.test/retry', playerHeaders: {}, goProxyBase: 'https://proxy.test' };
    },
    executeM3U8RelayRetryPlayback(options) {
      calls.relay.push(options);
      return { ok: true, playerUrl: 'https://relay.test/retry.m3u8', playerHeaders: {} };
    },
    fetchBootstrap: forbidden('bootstrap request'),
    performSearchSessionSearch: forbidden('search'),
    runSmartPlaybackController: forbidden('smart search'),
  });
  vm.runInContext(componentCode, context);
  const component = context.component;
  const page = component.data();
  for (const [name, method] of Object.entries(component.methods)) page[name] = method.bind(page);
  Object.defineProperty(page, 'currentPlaybackContext', { get: component.computed.currentPlaybackContext.bind(page) });
  const current = playback('current');
  // Supply computed inputs directly; selection, history payloads and playback methods remain real.
  Object.assign(page, {
    shortVideoMode: true, playerFirstFrameReady: true, playRequestSeq: 7,
    playerUrl: current.playerUrl, playerHeaders: current.playerHeaders,
    lastGoProxyCandidate: current.lastGoProxyCandidate, lastM3U8RelayCandidate: current.lastM3U8RelayCandidate,
    runtimeSettings: { catpawrunnerApiBase: 'https://runner.test', relayEnabled: true, relayServers: [{ base: 'https://relay.test' }] },
    bootstrap: { user: { username: 'alice' } },
    activeSitePlaybackItem: site, currentPanSourceEntry: pan, selectedSiteResultItem: null,
    isTmdbMode: false, tmdbMovieMode: false, isTMDBEpisodeSource: true,
    selectedSiteSource: 'TMDB', smartEpisodeMappingSignature: 'fixture-mapping',
    displayTitle: 'Fixture', playContentPreferenceKey: 'Fixture', detailPoster: '', historyRemarkText: '', preOrderActive: true,
    episodeSeasonRows: [{ season: 1, episodes: 2 }, { season: 2, episodes: 3 }],
    detailTMDBData: { seasons: [{ season_number: 1, episode_count: 2 }, { season_number: 2, episode_count: 3 }] },
    siteResultDetailStore: { [site.id]: { siteItem: site, detail: { sources: [pan], panMock189AccessByShareId: { share: 'secret' } } } },
    $refs: { artPlayerRef: { async tryAutoplay() { calls.autoplay++; } } },
    $nextTick(fn) { return Promise.resolve().then(fn); },
    async ensureSiteHistoryResolvedMeta() { return { contentKey: 'Fixture' }; },
    syncPlaybackDisplayFocus() {},
    syncPlayerLayoutMetrics() {},
    applyEpisodeViewModePreference() {},
    loadSelectedSiteResultDetail() {},
    cacheRecognitionForSiteResult: forbidden('new recognition'),
    ensureSiteResultDetailCached: forbidden('detail request'),
    runSmartPlaybackWithConstraints: forbidden('smart playback'),
    resumeSmartPlaybackIfPendingFailed() { calls.resume++; return false; },
  });
  const target = index => ({
    siteItem: site, panEntry: pan, segment: page.buildCurrentPanSegment(index),
    selectionKey: `${pan.key}::${index}`, globalEpisode: index + 1,
  });
  return { page, context, clock, calls, session, history, site, pan, target };
}

function playbackState({ page, calls, history, session }) {
  return JSON.stringify({
    url: page.playerUrl, headers: page.playerHeaders, context: session.currentContext,
    browse: session.lastBrowseContext, seq: page.playRequestSeq, smartSeq: page.smartPlaybackRunSeq,
    history, historyCalls: calls.history, contextCalls: calls.context, reports: calls.reports,
    proxy: page.lastGoProxyCandidate, relay: page.lastM3U8RelayCandidate, proxyBase: page.goProxyInUseBase,
    selection: page.selectedSiteEpisodeSelectionKey, payload: page.lastResolvedPlaybackPayload,
    error: page.playError, runtimeError: page.playerRuntimeError, loading: page.playLoading,
    stage: page.playRequestStage, autoplay: calls.autoplay,
  });
}

async function warmNeighbors(f) {
  const start = f.calls.play.length;
  f.page.prefetchShortVideoNeighbors();
  await flush();
  const results = f.calls.play.slice(start).map(request => {
    const result = playback(request.params.segment.episodeUrl);
    request.resolve(result);
    return result;
  });
  await flush();
  return results;
}

function assertBound(f, result, watchReport = result.watchReport) {
  assert.equal(f.page.playerUrl, result.playerUrl);
  assert.equal(f.page.playerHeaders, result.playerHeaders);
  assert.equal(f.page.lastGoProxyCandidate, result.lastGoProxyCandidate);
  assert.equal(f.page.lastM3U8RelayCandidate, result.lastM3U8RelayCandidate);
  assert.equal(f.page.goProxyInUseBase, result.goProxyBase);
  assert.equal(f.history.watchReport, watchReport);
}

for (const mode of ['direct', 'projected', 'raw']) {
  test(`${mode} prefetch resolves only real neighbors without changing playback or history`, async () => {
    const f = fixture();
    const { page, calls, site, pan } = f;
    if (mode !== 'direct') page.selectedSiteResultItem = site;
    page.rawListMode = mode === 'raw';
    page.projectedSiteEpisodeItems = [
      { global: 1, itemIndex: 4 }, { global: 2, itemIndex: 2 }, { global: 3, itemIndex: 0 },
    ];
    const before = playbackState(f);
    page.prefetchShortVideoNeighbors();
    await flush();
    assert.deepEqual(calls.play.map(call => call.params.segment.index), mode === 'projected' ? [0, 4] : [3, 1]);
    assert.equal(playbackState(f), before, 'pending speculation must be side-effect free');
    for (const request of calls.play) {
      assert.equal(request.params.runtimeSettings, page.runtimeSettings);
      assert.equal(request.params.bootstrapUser, 'alice');
      assert.equal(request.params.apiBase, 'https://runner.test');
      assert.equal(request.params.siteItem.id, site.id);
      assert.equal(request.params.siteItem.detailData, page.siteResultDetailStore[site.id].detail);
      assert.equal(request.params.panEntry, pan);
      assert.equal(request.params.selectionKey, `pan::${request.params.segment.index}`);
      assert.equal(request.params.selectedGoProxyBase, '');
      request.resolve(playback(request.params.segment.episodeUrl));
    }
    await flush();
    assert.equal(playbackState(f), before, 'resolved speculation must not bind a player or watch report');
    assert.equal(page.shortVideoPrefetch.size, 2);
    assert.deepEqual(calls.forbidden, []);
  });
}

test('prefetch requires ready, error-free short-video playback and matching source context', async () => {
  for (const [key, value] of [
    ['shortVideoMode', false], ['playerUrl', ''], ['playerFirstFrameReady', false],
    ['playError', 'failed'], ['playerRuntimeError', 'failed'],
  ]) {
    const f = fixture();
    f.page[key] = value;
    f.page.prefetchShortVideoNeighbors();
    await flush();
    assert.equal(f.calls.play.length, 0, key);
    assert.equal(f.page.shortVideoPrefetch.size, 0, key);
  }
  for (const [key, value] of [['itemId', 'other'], ['panKey', 'other'], ['selectionKey', '']]) {
    const f = fixture();
    f.session.currentContext[key] = value;
    assert.equal(f.page.resolveShortVideoPrefetchTarget(1), null, key);
    await warmNeighbors(f);
    assert.equal(f.calls.play.length, 0, key);
  }
  const f = fixture();
  f.session.currentContext.itemIndex = 0;
  assert.equal(f.page.resolveShortVideoPrefetchTarget(-1), null);
  f.session.currentContext.itemIndex = 4;
  assert.equal(f.page.resolveShortVideoPrefetchTarget(1), null);
});

test('pending requests deduplicate beyond 60s and take consumes their original promise once', async () => {
  const f = fixture();
  f.page.prefetchShortVideoNeighbors();
  await flush();
  const [key, entry] = [...f.page.shortVideoPrefetch][0];
  assert.equal(entry.pending, true);
  f.clock.now += 120000;
  f.page.prefetchShortVideoNeighbors();
  await flush();
  assert.equal(f.calls.play.length, 2);
  assert.equal(f.page.shortVideoPrefetch.get(key), entry);
  const consumed = f.page.takeShortVideoPrefetch(key);
  assert.equal(consumed, entry.promise);
  assert.equal(f.page.takeShortVideoPrefetch(key), null);
  const result = playback('pending');
  f.calls.play[0].resolve(result);
  f.calls.play[1].resolve(playback('previous'));
  assert.equal(await consumed, result);
  assert.equal(entry.pending, false);
});

test('settled neighbors are reused before 60s, refreshed at 60s, and pruned as playback moves', async () => {
  const f = fixture();
  await warmNeighbors(f);
  const entries = [...f.page.shortVideoPrefetch.values()];
  f.clock.now += 59999;
  f.page.prefetchShortVideoNeighbors();
  await flush();
  assert.equal(f.calls.play.length, 2);
  assert.deepEqual([...f.page.shortVideoPrefetch.values()], entries);
  f.clock.now++;
  await warmNeighbors(f);
  assert.equal(f.calls.play.length, 4);
  assert.ok([...f.page.shortVideoPrefetch.values()].every(entry => entry.at === f.clock.now && !entry.pending));
  f.session.currentContext.itemIndex = 3;
  await warmNeighbors(f);
  assert.equal(f.page.shortVideoPrefetch.size, 2);
  for (const index of [2, 4]) {
    const target = f.target(index);
    assert.ok(f.page.shortVideoPrefetch.has(f.page.buildShortVideoPrefetchKey(target.siteItem, target.panEntry, target.segment)));
  }
  f.session.currentContext.itemIndex = 4;
  await warmNeighbors(f);
  assert.equal(f.page.shortVideoPrefetch.size, 1, 'the last episode has only a previous neighbor');
});

for (const age of [59999, 60000]) {
  test(`take enforces the real 60s TTL at age ${age}ms and removes the entry`, async () => {
    const f = fixture();
    await warmNeighbors(f);
    const [key, entry] = [...f.page.shortVideoPrefetch][0];
    f.clock.now += age;
    assert.equal(f.page.takeShortVideoPrefetch(key), age < 60000 ? entry.promise : null);
    assert.equal(f.page.shortVideoPrefetch.has(key), false);
  });
}

for (const action of ['exit', 'site', 'pan', 'cancel']) {
  test(`${action} clears pending neighbors and late completions cannot repopulate the cache`, async () => {
    const f = fixture();
    f.page.prefetchShortVideoNeighbors();
    await flush();
    assert.equal(f.page.shortVideoPrefetch.size, 2);
    if (action === 'exit') f.page.exitShortVideoMode();
    if (action === 'site') f.page.selectSiteSourceResult({ ...f.site, id: 'other' });
    if (action === 'pan') f.page.selectPanSource('other');
    if (action === 'cancel') f.page.cancelActivePlaybackFlow({ clearPlayerState: true });
    await flush();
    assert.equal(f.page.shortVideoPrefetch.size, 0);
    const after = playbackState(f);
    f.calls.play[0].resolve(playback('late'));
    f.calls.play[1].reject(new Error('late failure'));
    await flush();
    assert.equal(f.page.shortVideoPrefetch.size, 0);
    assert.equal(playbackState(f), after);
  });
}

for (const [name, change] of [
  ['API', f => { f.page.runtimeSettings.catpawrunnerApiBase = 'https://other.test'; }],
  ['user', f => { f.page.bootstrap.user.username = 'bob'; }],
  ['site ID', (_f, target) => { target.siteItem.id = 'other'; }],
  ['spider API', (_f, target) => { target.siteItem.spiderApi = '/bbbbbbbbbb/spider/test/3'; }],
  ['detail', (_f, target) => { target.siteItem.siteDetail = 'other-detail'; }],
  ['pan key', (_f, target) => { target.panEntry.key = 'other-pan'; }],
  ['pan flag', (_f, target) => { target.panEntry.label = 'other-flag'; }],
  ['provider', (_f, target) => { target.panEntry.provider = '189'; }],
  ['index', (_f, target) => { target.segment.index++; }],
  ['play ID', (_f, target) => { target.segment.episodeUrl = 'other-file'; }],
  ['rawName', (_f, target) => { target.segment.rawName = 'other-name.mkv'; }],
  ['proxy', (_f, target) => { target.selectedGoProxyBase = 'https://other-proxy.test'; }],
  ['relay enabled', f => { f.page.runtimeSettings.relayEnabled = false; }],
  ['relay servers', f => { f.page.runtimeSettings.relayServers = [{ base: 'https://other-relay.test' }]; }],
]) {
  test(`prefetch keys isolate ${name} without consuming another context's entry`, () => {
    const f = fixture(), target = f.target(3);
    const key = () => f.page.buildShortVideoPrefetchKey(target.siteItem, target.panEntry, target.segment, target.selectedGoProxyBase);
    const original = key(), promise = Promise.resolve(playback('isolated'));
    f.page.shortVideoPrefetch.set(original, { at: f.clock.now, pending: false, promise });
    change(f, target);
    assert.notEqual(key(), original);
    assert.equal(f.page.takeShortVideoPrefetch(key()), null);
    assert.equal(f.page.takeShortVideoPrefetch(original), promise);
  });
}

test('smart cached neighbors use primary ranking and all candidate guards without recognition or search', async () => {
  const f = fixture(), { page, site, pan, calls } = f;
  page.isTmdbMode = true;
  page.runtimeSettings.smartPanMatchTokens = ['夸克', '百度'];
  page.currentPlaybackPanFamilyLabel = '夸克';
  page.currentPlaybackQualityKey = '1080p';
  const candidates = ['1080P', '1080P', '1080P', '4K', '1080P'].map((quality, itemIndex) => ({
    itemIndex, quality, panFlag: itemIndex === 2 ? '百度-fixture' : '夸克-fixture',
    segmentIdentity: pan.episodeSegments[itemIndex], resolutionMode: itemIndex === 0 ? 'loose' : 'strict-tmdb',
    mapping: { global: 3, tmdb: { season: 2, episode: 1 } },
  }));
  page.siteResultRecognitionStore = {
    [site.id]: { recognitionBySignature: { 'fixture-mapping': { [pan.key]: { source: pan, items: candidates } } } },
  };
  const episodeKey = page.buildSwitchEpisodeKeyByGlobal(3);
  assert.equal(page.appendSwitchSkippedCandidate(episodeKey, { siteItem: site, candidate: candidates[1] }), true);
  const before = playbackState(f), recognition = JSON.stringify(page.siteResultRecognitionStore);
  const target = page.resolveShortVideoPrefetchTarget(1);
  assert.equal(target.segment.index, 4, 'prefer the current pan AND quality, excluding skipped and non-strict files');
  assert.equal(calls.cached[0].globalEpisode, 3);
  assert.equal(calls.cached[0].wantEpisodeInSeason, 1, 'cross-season neighbor uses the episode within its season');
  assert.equal(calls.cached[0].matchOptions.trigger, 'primary');
  assert.equal(calls.cached[0].matchOptions.kind, 'episode');
  await warmNeighbors(f);
  assert.deepEqual(calls.play.map(call => call.params.segment.index), [4]);
  assert.equal(playbackState(f), before);
  assert.equal(JSON.stringify(page.siteResultRecognitionStore), recognition);
  assert.equal(page.appendSwitchSkippedCandidate(episodeKey, { siteItem: site, candidate: candidates[4] }), true);
  assert.equal(page.resolveShortVideoPrefetchTarget(1).segment.index, 3, 'matching pan takes priority over matching quality alone');

  page.playBlockedSiteKeys = [site.siteKey];
  assert.equal(page.resolveShortVideoPrefetchTarget(1), null);
  page.playBlockedSiteKeys = [];
  page.playBlockedMatchIndex = { [`${site.siteKey}::${site.siteDetail}`]: { blockAll: true } };
  assert.equal(page.resolveShortVideoPrefetchTarget(1), null);
  page.playBlockedMatchIndex = { [`${site.siteKey}::${site.siteDetail}`]: { panFlags: ['夸克-fixture'] } };
  assert.equal(page.resolveShortVideoPrefetchTarget(1).segment.index, 2);
  page.playBlockedMatchIndex = {};
  page.siteResultRecognitionStore = {};
  await warmNeighbors(f);
  assert.equal(page.shortVideoPrefetch.size, 0);
  assert.equal(calls.play.length, 1, 'a cache miss must not launch speculative smart search');
  assert.ok(calls.cached.every(call => call.ensureRecognitionForSiteItem === null));
  assert.deepEqual(calls.forbidden, []);
});

test('cache hits retain normal context/history preparation and bind headers, both candidates and watch reports', async () => {
  const f = fixture();
  const [result] = await warmNeighbors(f);
  const seq = f.page.playRequestSeq;
  assert.equal(await f.page.playResolvedSiteSegment({ ...f.target(3), fromHistoryDetail: true }), true);
  assertBound(f, result);
  assert.equal(f.calls.play.length, 2, 'cache consumption must not issue another play request');
  assert.equal(f.page.shortVideoPrefetch.size, 1);
  assert.equal(f.page.playRequestSeq, seq + 1);
  assert.equal(f.calls.context.length, 1);
  assert.equal(f.page.currentPlaybackContext.itemId, f.site.id);
  assert.equal(f.page.currentPlaybackContext.selectionKey, 'pan::3');
  assert.equal(f.page.currentPlaybackContext.globalEpisode, 4);
  assert.equal(f.page.currentPlaybackContext.sourceKind, 'history-detail');
  assert.equal(f.calls.history.length, 1);
  const history = f.calls.history[0];
  assert.equal(history.selectionKey, 'pan::3');
  assert.equal(history.siteKey, f.site.siteKey);
  assert.equal(history.spiderApi, f.site.spiderApi);
  assert.equal(history.siteDetail, f.site.siteDetail);
  assert.equal(history.siteEpisodeIndex, 4);
  assert.equal(history.globalEpisode, 4);
  assert.equal(history.reportEnabled, true);
  assert.equal(history.preOrder, true);
  assert.equal(f.calls.reports.length, 1);
  assert.equal(f.calls.autoplay, 1);
  assert.equal(f.page.shortVideoPrefetchedPlayback.seq, seq + 1);
});

test('consuming a pending neighbor waits for it instead of issuing a second play request', async () => {
  const f = fixture();
  f.page.prefetchShortVideoNeighbors();
  await flush();
  const playing = f.page.playResolvedSiteSegment(f.target(3));
  await flush();
  assert.equal(f.calls.play.length, 2);
  assert.equal(f.calls.reports.length, 0);
  assert.equal(f.page.shortVideoPrefetch.size, 1);
  const result = playback('pending-hit');
  f.calls.play[0].resolve(result);
  f.calls.play[1].resolve(playback('previous'));
  assert.equal(await playing, true);
  assertBound(f, result);
  assert.equal(f.calls.play.length, 2);
});

for (const reason of ['expired', 'ordinary mode']) {
  test(`${reason} resolves normally instead of using a cached URL or enabling cache refresh`, async () => {
    const f = fixture();
    await warmNeighbors(f);
    if (reason === 'expired') f.clock.now += 60000;
    else f.page.shortVideoMode = false;
    const playing = f.page.playResolvedSiteSegment(f.target(3));
    await flush();
    assert.equal(f.calls.play.length, 3);
    const result = playback('uncached');
    f.calls.play[2].resolve(result);
    assert.equal(await playing, true);
    assertBound(f, result);
    assert.equal(f.page.shortVideoPrefetchedPlayback, null);
    f.page.onPlayerError({ message: 'ordinary error' });
    await flush();
    assert.equal(f.calls.play.length, 3, 'non-cache errors go directly to the existing fallback');
    assert.equal(f.calls.proxy.length, 1);
  });
}

for (const succeeds of [true, false]) {
  test(`failed speculation is silent; consumption retries normal resolution (${succeeds ? 'success' : 'failure'})`, async () => {
    const f = fixture(), before = playbackState(f);
    f.page.prefetchShortVideoNeighbors();
    await flush();
    for (const request of f.calls.play) request.reject(new Error('speculative failure'));
    await flush();
    assert.equal(playbackState(f), before);
    f.page.prefetchShortVideoNeighbors();
    await flush();
    assert.equal(f.calls.play.length, 2, 'failed entries deduplicate within TTL');
    const playing = f.page.playResolvedSiteSegment(f.target(3));
    await flush();
    assert.equal(f.calls.play.length, 3);
    assert.equal(f.calls.play[2].params.selectionKey, 'pan::3');
    const result = playback('fallback');
    if (succeeds) f.calls.play[2].resolve(result);
    else f.calls.play[2].reject(new Error('actual playback failed'));
    assert.equal(await playing, succeeds);
    assert.equal(f.page.shortVideoPrefetchedPlayback, null);
    assert.equal(f.calls.history.length, 1);
    if (succeeds) assertBound(f, result);
    else {
      assert.equal(f.page.playError, 'actual playback failed');
      assert.equal(f.page.playerUrl, '');
      assert.equal(f.page.lastGoProxyCandidate, null);
      assert.equal(f.page.lastM3U8RelayCandidate, null);
      assert.equal(f.calls.reports.length, 0);
    }
  });
}

for (const phase of ['metadata', 'history', 'prefetch resolve', 'prefetch reject', 'resolver resolve', 'resolver reject']) {
  test(`a newer episode invalidates an older request waiting on ${phase}`, async () => {
    const f = fixture(), gate = deferred();
    const originalMeta = f.page.ensureSiteHistoryResolvedMeta;
    const originalHistory = f.context.preparePlayHistoryContext;
    if (phase === 'metadata') f.page.ensureSiteHistoryResolvedMeta = () => gate.promise;
    if (phase === 'history') f.context.preparePlayHistoryContext = async payload => {
      await originalHistory(payload);
      await gate.promise;
    };
    if (phase.startsWith('prefetch')) {
      f.page.prefetchShortVideoNeighbors();
      await flush();
      f.calls.play[1].resolve(playback('previous'));
    }
    const seq = f.page.playRequestSeq;
    const older = f.page.playResolvedSiteSegment(f.target(3));
    assert.equal(f.page.playRequestSeq, seq + 1, 'invalidate previous work before the first await');
    await flush();
    f.page.ensureSiteHistoryResolvedMeta = originalMeta;
    f.context.preparePlayHistoryContext = originalHistory;
    const oldRequest = f.calls.play.find(call => call.params.segment.index === 3);
    f.page.beginExplicitPlaybackTransition();
    assert.equal(f.page.playRequestSeq, seq + 2);
    const newer = f.page.playResolvedSiteSegment(f.target(0));
    await flush();
    const result = playback('newer');
    f.calls.play.find(call => call.params.segment.index === 0).resolve(result);
    assert.equal(await newer, true);
    assertBound(f, result);
    const after = playbackState(f), count = f.calls.play.length;
    if (oldRequest) {
      if (phase.endsWith('reject')) oldRequest.reject(new Error('outdated failure'));
      else oldRequest.resolve(playback('outdated'));
    } else gate.resolve({ contentKey: 'old metadata' });
    assert.equal(await older, false);
    assert.equal(playbackState(f), after);
    assert.equal(f.calls.play.length, count, 'an outdated failed cache entry must not start fallback resolution');
  });
}

test('a late proxy retry cannot overwrite the next episode URL, headers or loading state', async () => {
  const f = fixture(), proxy = deferred(), autoplay = deferred();
  const candidate = f.page.lastGoProxyCandidate;
  f.context.executeProxyRetryPlayback = options => {
    f.calls.proxy.push(options);
    return proxy.promise;
  };
  f.page.onPlayerError({ message: 'old episode failed' });
  await flush();
  assert.equal(f.calls.proxy.length, 1);
  assert.equal(f.calls.proxy[0].candidate, candidate);
  assert.equal(f.page.proxyRetryInFlight, true);

  f.page.beginExplicitPlaybackTransition();
  f.page.$refs.artPlayerRef.tryAutoplay = () => { f.calls.autoplay++; return autoplay.promise; };
  const playing = f.page.playResolvedSiteSegment(f.target(3));
  await flush();
  assert.equal(f.page.proxyRetryInFlight, false, 'starting the next episode resets the previous retry flag');
  assert.equal(f.calls.play.length, 1);
  const current = playback('next-episode');
  f.calls.play[0].resolve(current);
  await flush();
  assertBound(f, current);
  assert.equal(f.page.playLoading, true, 'the next episode is still waiting for autoplay');
  const after = playbackState(f);

  proxy.resolve({ ok: true, ...playback('outdated-proxy') });
  await flush();
  assertBound(f, current);
  assert.equal(playbackState(f), after, 'neither the stale result nor its finally may change the current playback');
  assert.equal(f.page.proxyRetryInFlight, false);
  assert.equal(f.page.playLoading, true);
  autoplay.resolve();
  assert.equal(await playing, true);
  assert.equal(f.page.playLoading, false);
});

test('leaving the page invalidates pending cache consumption without launching fallback', async () => {
  const f = fixture();
  f.page.prefetchShortVideoNeighbors();
  await flush();
  const playing = f.page.playResolvedSiteSegment(f.target(3));
  await flush();
  f.page.cancelActivePlaybackFlow({ clearPlayerState: true });
  const after = playbackState(f);
  f.calls.play[0].reject(new Error('late cached failure'));
  f.calls.play[1].resolve(playback('late neighbor'));
  assert.equal(await playing, false);
  await flush();
  assert.equal(playbackState(f), after);
  assert.equal(f.calls.play.length, 2);
  assert.equal(f.page.shortVideoPrefetch.size, 0);
});

for (const phase of ['render tick', 'autoplay']) {
  test(`leaving the page during ${phase} prevents stale autoplay/completion`, async () => {
    const f = fixture(), gate = deferred();
    await warmNeighbors(f);
    if (phase === 'render tick') {
      f.page.$nextTick = fn => fn ? Promise.resolve().then(fn) : gate.promise;
    } else {
      f.page.$refs.artPlayerRef.tryAutoplay = () => { f.calls.autoplay++; return gate.promise; };
    }
    const playing = f.page.playResolvedSiteSegment(f.target(3));
    await flush();
    f.page.cancelActivePlaybackFlow({ clearPlayerState: true });
    const after = playbackState(f), autoplay = f.calls.autoplay, stage = f.page.smartPlaybackResolvedStage;
    gate.resolve();
    const completed = await playing;
    assert.deepEqual(
      { completed, autoplay: f.calls.autoplay, stage: f.page.smartPlaybackResolvedStage },
      { completed: false, autoplay, stage },
      'cancelled work must not autoplay, advance the smart stage or report success',
    );
    assert.equal(playbackState(f), after);
  });
}

for (const succeeds of [true, false]) {
  test(`expired cached playback refreshes once, suppresses duplicate errors, and preserves proxy/relay fallback (${succeeds ? 'resolved' : 'rejected'})`, async () => {
    const f = fixture(), [cached] = await warmNeighbors(f);
    assert.equal(await f.page.playResolvedSiteSegment(f.target(3)), true);
    const seq = f.page.playRequestSeq, history = f.history.current;
    const event = { message: 'signed URL expired' };
    f.page.onPlayerError(event);
    f.page.onPlayerError(event);
    await flush();
    f.page.onPlayerError(event);
    assert.equal(f.calls.play.length, 3);
    assert.equal(f.page.shortVideoRefreshInFlightSeq, seq);
    assert.equal(f.page.shortVideoPrefetchedPlayback, null);
    assert.equal(f.page.lastGoProxyCandidate, cached.lastGoProxyCandidate);
    assert.equal(f.calls.proxy.length, 0);
    assert.equal(f.calls.relay.length, 0);
    assert.equal(f.calls.play[2].params.segment.episodeUrl, 'episode-3');
    const refreshed = { ...playback('fresh'), watchReport: { ...cached.watchReport } };
    if (succeeds) f.calls.play[2].resolve(refreshed);
    else f.calls.play[2].reject(new Error('refresh failed'));
    await flush();
    assert.equal(f.page.shortVideoRefreshInFlightSeq, 0);
    assert.equal(f.page.playRequestSeq, seq);
    assert.equal(f.history.current, history);
    assert.equal(f.calls.history.length, 1);
    assert.equal(f.calls.context.length, 1);
    assert.equal(f.history.watchReport, cached.watchReport, 'refreshing the same episode must preserve its watch-report session');
    assert.equal(f.calls.reports.length, 1, 'only actual episode consumption binds a watch report');
    if (succeeds) {
      assertBound(f, refreshed, cached.watchReport);
      f.page.onPlayerError(event);
      await flush();
    } else {
      assert.equal(f.page.lastM3U8RelayCandidate, cached.lastM3U8RelayCandidate);
    }
    assert.equal(f.calls.proxy.length, 1);
    assert.equal(f.calls.proxy[0].candidate, (succeeds ? refreshed : cached).lastGoProxyCandidate);
    assert.equal(f.page.playerUrl, 'https://proxy.test/retry');
    f.page.onPlayerError(event);
    await flush();
    assert.equal(f.calls.relay.length, 1);
    assert.equal(f.calls.relay[0].candidate, (succeeds ? refreshed : cached).lastM3U8RelayCandidate);
    assert.equal(f.page.playerUrl, 'https://relay.test/retry.m3u8');
    f.page.onPlayerError(event);
    assert.equal(f.calls.resume, 1);
    assert.equal(f.calls.play.length, 3, 'later errors must never refresh this cache hit again');
  });
}

for (const action of ['beginExplicit', 'playResolved', 'cancel']) {
  for (const rejects of [false, true]) {
    test(`${action} invalidates an in-flight cache refresh before it ${rejects ? 'rejects' : 'resolves'}`, async () => {
      const f = fixture();
      await warmNeighbors(f);
      assert.equal(await f.page.playResolvedSiteSegment(f.target(3)), true);
      f.page.onPlayerError({ message: 'expired' });
      await flush();
      const request = f.calls.play[2], seq = f.page.playRequestSeq;
      const gate = deferred();
      let newer;
      if (action === 'beginExplicit') f.page.beginExplicitPlaybackTransition();
      if (action === 'cancel') f.page.cancelActivePlaybackFlow({ clearPlayerState: true });
      if (action === 'playResolved') {
        f.page.ensureSiteHistoryResolvedMeta = () => gate.promise;
        newer = f.page.playResolvedSiteSegment(f.target(0));
      }
      assert.equal(f.page.playRequestSeq, seq + 1);
      assert.equal(f.page.shortVideoRefreshInFlightSeq, 0);
      const after = playbackState(f);
      if (rejects) request.reject(new Error('outdated refresh'));
      else request.resolve(playback('outdated refresh'));
      await flush();
      assert.equal(playbackState(f), after);
      assert.equal(f.calls.proxy.length, 0);
      assert.equal(f.calls.relay.length, 0);
      assert.equal(f.calls.resume, 0);
      if (newer) {
        gate.resolve({ contentKey: 'Fixture' });
        await flush();
        f.calls.play[3].resolve(playback('current'));
        assert.equal(await newer, true);
      }
    });
  }
}
