const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

const source = readFileSync('src/pages/play/PlayPage.vue', 'utf8');
const componentCode = source.slice(source.indexOf('const THIRD_PARTY_PLAYERS ='), source.indexOf('</script>'))
  .replace('export default', 'this.component =');
const historySource = readFileSync('src/shared/playHistoryRuntime.js', 'utf8')
  .replace(/^import .*;\r?\n/gm, '').replace(/export /g, '');
const deferred = () => {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
};

function element(initialStyle, left, top) {
  let style = initialStyle;
  return {
    scrollLeft: left, scrollTop: top,
    getAttribute: () => style,
    setAttribute: (_name, value) => { style = value; },
    removeAttribute: () => { style = null; },
    style: {
      setProperty(name, value, priority) {
        style = `${style || ''};${name}:${value}${priority ? ' !' + priority : ''}`;
      },
    },
    scrollTo({ left, top, behavior }) {
      assert.equal(behavior, 'instant');
      this.scrollLeft = left;
      this.scrollTop = top;
    },
  };
}

function historyRuntime() {
  const posts = [];
  const context = vm.createContext({
    reactive: value => value,
    normalizeInt: value => Math.trunc(Number(value) || 0),
    normalizeString: value => String(value ?? '').trim(),
    apiPostJson: async (_url, body) => { posts.push(body); return { success: true }; },
    apiGetJson: async () => ({ items: [] }), apiInvalidateCache() {}, buildQuery: () => '',
  });
  vm.runInContext(historySource + `
    this.api = {
      preparePlayHistoryContext, bindPlayHistoryWatchReport, onPlayerHistoryPlaybackStart,
      confirmPlayerHistoryPlaybackReady, onPlayerHistoryTimeUpdate,
      syncHistoryProgressIfPossible, playHistorySessionState,
    };
  `, context);
  return { ...context.api, posts };
}

function fixture({ rootStyle = null, bodyStyle = null } = {}) {
  const root = element(rootStyle, 12, 150), body = element(bodyStyle, 8, 375);
  const ticks = [], timers = [], history = [];
  const window = {
    innerWidth: 1280, innerHeight: 720, scrollX: 12, scrollY: 150,
    scrollTo({ left, top, behavior }) {
      assert.equal(behavior, 'instant');
      this.scrollX = left;
      this.scrollY = top;
    },
    setTimeout(fn, delay) { timers.push({ fn, delay }); return timers.length; },
    removeEventListener() {},
  };
  const context = vm.createContext({
    ArtPlayer: {}, window,
    markRaw: value => value,
    document: { documentElement: root, body, removeEventListener() {} },
    normalizeInt: value => Math.trunc(Number(value) || 0),
    normalizeString: value => String(value ?? '').trim(),
    onPlayerHistoryTimeUpdate: info => history.push(info),
    syncHistoryProgressIfPossible: async () => {},
    flushHistoryProgressBestEffort() {},
    clearActivePlayHistoryContext() {},
    clearCurrentPlaybackContext() {},
  });
  vm.runInContext(componentCode, context);
  const component = context.component;
  const page = component.data();
  for (const [name, method] of Object.entries(component.methods)) page[name] = method.bind(page);
  for (const [name, getter] of Object.entries(component.computed)) {
    Object.defineProperty(page, name, { get: getter.bind(page) });
  }
  const player = {
    currentTime: 42,
    destroy: () => assert.fail('mode changes must not destroy the player'),
    pause: () => assert.fail('mode changes must not pause the player'),
    seekTo: () => assert.fail('mode changes must not seek the player'),
  };
  Object.assign(page, {
    $refs: { artPlayerRef: player },
    $nextTick(fn) { if (fn) ticks.push(fn); },
    syncPlayerLayoutMetrics() {},
    prefetchShortVideoNeighbors() {},
    resetSwitchSkipState() {},
    cancelActivePlaybackFlow() {},
    unbindPlaySearchQuerySubscription() {},
    unbindEpisodePanelResizer() {},
  });
  const flushTicks = () => { while (ticks.length) ticks.shift()(); };
  return { page, component, context, root, body, window, player, flushTicks, timers, history };
}

test('short video defaults off and uses the existing player and title contract', () => {
  const { page } = fixture();
  assert.equal(page.shortVideoMode, false);
  assert.equal(page.shortVideoCacheEnabled, false);
  assert.equal(page.shortVideoCacheKey, '');
  assert.equal(page.shortVideoCacheKeys.length, 0);
  assert.equal(page.shortVideoEpisodeSwitching, false);
  const playerTags = source.match(/<ArtPlayer\b[\s\S]*?\/>/g);
  assert.equal(playerTags.length, 1);
  assert.match(playerTags[0], /:short-video-mode="shortVideoMode"/);
  assert.match(playerTags[0], /:short-video-cache-enabled="shortVideoCacheEnabled"/);
  assert.match(playerTags[0], /:short-video-cache-key="shortVideoCacheKey"/);
  assert.match(playerTags[0], /:short-video-cache-keys="shortVideoCacheKeys"/);
  assert.match(playerTags[0], /:short-video-buffer-candidates="shortVideoBufferCandidates"/);
  assert.match(playerTags[0], /:episode-switching="shortVideoEpisodeSwitching"/);
  assert.match(playerTags[0], /:short-video-title="shortVideoTitleText"/);
  assert.match(playerTags[0], /:short-video-prev="shortVideoPrevTitleText"/);
  assert.match(playerTags[0], /:short-video-next="shortVideoNextTitleText"/);
  assert.match(playerTags[0], /@exit-short-video="exitShortVideoMode"/);
  assert.doesNotMatch(playerTags[0], /(?:v-if|:key)=/);
  const modeMethods = source.slice(source.indexOf('    enterShortVideoMode()'), source.indexOf('    buildShortVideoTitle('));
  assert.doesNotMatch(modeMethods, /requestFullscreen|webkitEnterFullscreen|playerUrl\s*=|\.destroy\(|\.seekTo\(/);
});

test('short-video entry is the first desktop toolbar button, shown only from 1024px', () => {
  const entry = source.match(/<div class="play-episode-toggle-wrap">\s*(<button\b[^>]*>)[\s\S]*?<\/button>\s*<button\s+id="episodePanelToggle"/)[1];
  assert.match(entry, /id="playShortVideoToggle"/);
  assert.match(entry, /@click="enterShortVideoMode"/);
  assert.doesNotMatch(entry, /\bv-(?:if|show)=/);
  const css = source.slice(source.indexOf('<style>'));
  assert.match(css, /^\.play-episode-toggle-wrap \{\s*display: none;/m);
  const toolbarRule = css.match(/@media \(min-width: 1024px\) \{\s*\.play-episode-toggle-wrap \{([^}]+)\}/)[1];
  assert.match(toolbarRule, /display: flex;/);
});

test('mobile short-video entry stays at the third-party bar first-row left edge through 1023px', () => {
  const entry = source.match(/<div class="play-thirdparty-bar">\s*<div class="play-thirdparty-bar__inner">\s*(<button\b[^>]*>)[\s\S]*?<\/button>\s*<button\s+v-for="player in thirdPartyVisiblePlayers"/)[1];
  assert.match(entry, /class="play-thirdparty-short-video"/);
  assert.match(entry, /@click="enterShortVideoMode"/);
  assert.doesNotMatch(entry, /\bv-(?:if|show|for)=/);
  const css = source.slice(source.indexOf('<style>'));
  assert.match(css, /^\.play-thirdparty-short-video \{\s*display: none;/m);
  const barRule = css.match(/^\.play-thirdparty-bar__inner \{([^}]+)\}/m)[1];
  assert.match(barRule, /flex-wrap: wrap;/);
  assert.match(barRule, /padding: 8px;/);
  const mobileRules = css.match(/@media \(max-width: 1023px\) \{\s*\.play-thirdparty-bar__inner \{([^}]+)\}\s*\.play-thirdparty-short-video \{([^}]+)\}/);
  assert.match(mobileRules[1], /position: relative;/);
  assert.match(mobileRules[1], /padding-left: 120px;/);
  assert.match(mobileRules[2], /position: absolute;/);
  assert.match(mobileRules[2], /left: 8px;/);
  assert.match(mobileRules[2], /top: 8px;/);
  assert.match(mobileRules[2], /width: 104px;/);
  assert.match(mobileRules[2], /height: 40px;/);
  assert.match(mobileRules[2], /display: inline-flex;/);
  assert.doesNotMatch(mobileRules[2], /top:\s*50%|translateY\(-50%\)/);
});

test('short-video layout fills the viewport and keeps loading overlays noninteractive', () => {
  const css = source.slice(source.indexOf('<style>'));
  const rootRule = css.match(/\.play-page\.play-page--short-video \{([^}]+)\}/)[1];
  assert.match(rootRule, /height: 100vh;[\s\S]*height: 100svh;[\s\S]*height: 100dvh;/);
  assert.match(rootRule, /position: fixed/);
  assert.match(rootRule, /background: #000/);
  assert.ok(Number(rootRule.match(/z-index: (\d+)/)[1]) > 20000, 'cover the index shell toolbar');
  assert.match(rootRule, /padding: 0;/);
  assert.doesNotMatch(rootRule, /safe-area-inset-/, 'ArtPlayer alone handles control safe areas');
  assert.match(css, /\.play-page--short-video video \{\s*object-fit: contain !important;/);
  const hiddenRule = css.match(/(\.play-page--short-video \.play-header,[^{]+)\{\s*display: none;/)[1];
  for (const part of ['play-header', 'play-detail', 'play-thirdparty-bar', 'play-episode-panel', 'play-episode-toggle-wrap']) {
    assert.ok(hiddenRule.includes(`.${part}`));
  }
  const overlayRule = css.match(/\.play-player-overlay \{([^}]+)\}/)[1];
  assert.match(overlayRule, /pointer-events: none;/);
  assert.ok(Number(overlayRule.match(/z-index: (\d+)/)[1]) < 100, 'below sv-touch-surface (100) and sv-ui (110)');
  const loadingCss = css.replace(/\.play-player-overlay--error[^{]*\{[^}]*\}/g, '');
  assert.doesNotMatch(loadingCss, /\.play-player-overlay[^{]*\{[^}]*pointer-events:\s*auto/);
});

for (const style of [null, '', 'overflow: auto !important; position: relative; color: red;']) {
  test(`exit restores exact inline styles (${JSON.stringify(style)}), both scroll containers and playback`, () => {
    const f = fixture({ rootStyle: style, bodyStyle: style });
    const { page, root, body, window, player, flushTicks } = f;
    page.playerUrl = 'https://example.test/episode.m3u8';
    const headers = page.playerHeaders = { Authorization: 'fixture' };
    page.siteSourceOpen = page.panSourceOpen = true;
    page.enterShortVideoMode();
    const snapshot = page.shortVideoScrollState;
    assert.equal(page.shortVideoMode, true);
    assert.equal(page.shortVideoCacheEnabled, true);
    assert.equal(page.siteSourceOpen || page.panSourceOpen, false);
    assert.match(root.getAttribute('style'), /overflow:hidden !important/);
    assert.match(body.getAttribute('style'), /position:fixed !important/);
    assert.match(body.getAttribute('style'), /top:-150px !important/);
    page.enterShortVideoMode();
    assert.equal(page.shortVideoScrollState, snapshot);
    root.scrollTop = body.scrollTop = window.scrollY = 0;
    root.scrollLeft = body.scrollLeft = window.scrollX = 0;
    page.exitShortVideoMode();
    flushTicks();
    assert.equal(page.shortVideoCacheEnabled, true);
    assert.equal(root.getAttribute('style'), style);
    assert.equal(body.getAttribute('style'), style);
    assert.deepEqual([root.scrollLeft, root.scrollTop, body.scrollLeft, body.scrollTop], [12, 150, 8, 375]);
    assert.deepEqual([window.scrollX, window.scrollY], [12, 150]);
    assert.equal(page.shortVideoScrollState, null);
    assert.equal(page.playerUrl, 'https://example.test/episode.m3u8');
    assert.equal(page.playerHeaders, headers);
    assert.equal(page.$refs.artPlayerRef, player);
    assert.equal(player.currentTime, 42);
    page.exitShortVideoMode();
    flushTicks();
    assert.equal(body.getAttribute('style'), style);
  });
}

test('rapid exit/reentry keeps the original scroll snapshot and the background locked', () => {
  const { page, body, window, flushTicks } = fixture();
  page.enterShortVideoMode();
  const snapshot = page.shortVideoScrollState;
  window.scrollY = 0;
  page.exitShortVideoMode();
  page.enterShortVideoMode();
  page.shortVideoCacheKeys = ['old-key'];
  page.shortVideoPrefetch.set('old-key', { result: {} });
  page.shortVideoBufferCandidates = [{ key: 'next', url: 'https://media.test/next' }];
  flushTicks();
  assert.equal(page.shortVideoScrollState, snapshot);
  assert.match(body.getAttribute('style'), /position:fixed/);
  page.exitShortVideoMode();
  flushTicks();
  assert.equal(window.scrollY, 150);
  assert.equal(body.scrollTop, 375);
  assert.equal(body.getAttribute('style'), null);
});

test('unmount restores styles immediately and scroll again after document layout is restored', () => {
  const { page, component, root, body, window, flushTicks } = fixture({ bodyStyle: 'color: red;' });
  page.enterShortVideoMode();
  flushTicks();
  component.beforeUnmount.call(page);
  assert.equal(root.getAttribute('style'), null);
  assert.equal(body.getAttribute('style'), 'color: red;');
  root.scrollTop = body.scrollTop = window.scrollY = 0;
  flushTicks();
  assert.deepEqual([root.scrollTop, body.scrollTop, window.scrollY], [150, 375, 150]);
  assert.equal(page.shortVideoScrollState, null);
  assert.equal(page.shortVideoCacheKeys.length, 0);
  assert.equal(page.shortVideoPrefetch.size, 0);
  assert.equal(page.shortVideoBufferCandidates.length, 0);
  assert.equal(fixture().page.shortVideoCacheEnabled, false, 'a new page starts a new session');
});

for (const shortVideoMode of [true, false]) {
  for (const localPosition of [0, 37.5]) {
    test(`cacheRestored consumes historical resume without seeking at ${localPosition}s (UI=${shortVideoMode})`, async () => {
      const f = fixture(), runtime = historyRuntime();
      const { page, player, context } = f;
      await runtime.preparePlayHistoryContext({
        contentKey: 'Fixture', reportEnabled: true, siteKey: 'site',
        spiderApi: '/aaaaaaaaaa/spider/test/3', siteDetail: 'detail', playFlag: 'line', selectionKey: 'episode-2',
      });
      runtime.bindPlayHistoryWatchReport({
        apiBase: 'https://runner.test', spiderApi: '/aaaaaaaaaa/spider/test/3', id: 'episode-2', flag: 'line',
      });
      const state = runtime.playHistorySessionState;
      state.resumePlan = {
        identity: state.activeContext.identity, seconds: 120, applied: false, frozen: false, tryKey: '', tryAt: 0,
      };
      Object.assign(context, runtime);
      page.shortVideoMode = shortVideoMode;
      page.shortVideoCacheEnabled = true;
      player.currentTime = localPosition;
      const frame = { currentTime: localPosition, duration: 180, playing: true, cacheRestored: true };
      await page.onPlayerFirstFrame(frame);
      assert.equal(player.currentTime, localPosition);
      assert.equal(state.resumePlan.applied, true);
      assert.equal(state.resumePlan.frozen, true);
      assert.equal(state.playbackState.ready, true, 'restored playback still confirms history/watch-report readiness');
      assert.equal(state.activeContext.watchReportReady, true);
      assert.equal(runtime.posts.length, 1);
      assert.equal(runtime.posts[0].playbackPositionTicks, localPosition * 10000000);
      assert.equal(runtime.posts[0].playbackEvent, 'started');
      assert.equal(runtime.posts[0].watchReport.id, 'episode-2');
      assert.equal(page.playerFirstFrameReady, true);
      await page.onPlayerFirstFrame({ ...frame, cacheRestored: false });
      assert.equal(player.currentTime, localPosition, 'a later firstframe cannot replay the consumed historical seek over local zero');
    });
  }
}

test('non-restored firstframe retains the ordinary historical seek and ready confirmation', async () => {
  const { page, player, context } = fixture(), calls = [];
  context.onPlayerHistoryPlaybackStart = async reason => { calls.push(['resume', reason]); return 75; };
  context.confirmPlayerHistoryPlaybackReady = async reason => { calls.push(['ready', reason]); };
  player.seekTo = seconds => { player.currentTime = seconds; calls.push(['seek', seconds]); };
  await page.onPlayerFirstFrame({ currentTime: 0, duration: 180, playing: true, cacheRestored: false });
  assert.equal(player.currentTime, 75);
  assert.deepEqual(calls, [['resume', 'firstframe'], ['seek', 75], ['ready', 'firstframe']]);
});

test('sticky cache refresh scheduling continues outside the short-video UI without affecting progress events', () => {
  const { page, history } = fixture();
  let prefetched = 0;
  page.prefetchShortVideoNeighbors = () => { prefetched++; };
  const info = { currentTime: 130, duration: 180, playing: true };
  page.onPlayerTimeUpdate(info);
  assert.equal(prefetched, 0, 'never-enabled ordinary pages must not prefetch');
  page.shortVideoCacheEnabled = true;
  page.onPlayerTimeUpdate(info);
  assert.equal(prefetched, 1);
  assert.equal(page.shortVideoMode, false);
  assert.deepEqual(history, [info, info]);
});

test('ended reports the original final position before switching and leaves local zero handling to ArtPlayer', async () => {
  const { page, player, context } = fixture(), runtime = historyRuntime(), calls = [];
  await runtime.preparePlayHistoryContext({
    reportEnabled: true, siteKey: 'site', siteDetail: 'detail', selectionKey: 'episode-2',
  });
  runtime.bindPlayHistoryWatchReport({
    apiBase: 'https://runner.test', spiderApi: '/aaaaaaaaaa/spider/test/3', id: 'episode-2', flag: 'line',
  });
  Object.assign(context, runtime);
  await page.onPlayerFirstFrame({ currentTime: 0, duration: 130.5, playing: true, cacheRestored: true });
  page.shortVideoMode = page.shortVideoCacheEnabled = true;
  player.currentTime = 130.5;
  page.switchEpisodeByDelta = async (delta, options) => {
    calls.push([delta, options.showBoundaryToast]);
    assert.equal(runtime.posts.at(-1).playbackEvent, 'stopped');
    assert.equal(runtime.posts.at(-1).playbackPositionTicks, 1305000000);
    assert.equal(player.currentTime, 130.5);
  };
  page.onPlayerEnded({ currentTime: 130.5, duration: 130.5, playing: false });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(calls, [[1, false]]);
  assert.equal(runtime.playHistorySessionState.playerTime.currentTime, 130.5);
});

test('entering and exiting short video leaves the episode panel preference unchanged', () => {
  for (const hidden of [false, true]) {
    const { page, flushTicks } = fixture();
    page.episodePanelHidden = hidden;
    page.enterShortVideoMode();
    assert.equal(page.episodePanelHidden, hidden);
    flushTicks();
    assert.equal(page.shortVideoMode, true);
    assert.equal(page.episodePanelHidden, hidden);
    page.exitShortVideoMode();
    assert.equal(page.episodePanelHidden, hidden);
    flushTicks();
    assert.equal(page.shortVideoMode, false);
    assert.equal(page.shortVideoScrollState, null);
    assert.equal(page.episodePanelHidden, hidden);
  }
});

test('short-video deltas lock before the first await and ignore overlapping gestures and ended', async () => {
  const { page, history } = fixture();
  const held = deferred(), calls = [];
  page.shortVideoMode = true;
  page.switchEpisodeByDelta = (delta, options) => { calls.push([delta, options.showBoundaryToast]); return held.promise; };
  const first = page.onPlayerEpisodeDelta(1);
  assert.equal(page.shortVideoEpisodeSwitching, true);
  await Promise.all([page.onPlayerEpisodeDelta(1), page.onPlayerEpisodeDelta(-1)]);
  page.onPlayerEnded({ currentTime: 60 });
  assert.deepEqual(calls, [[1, true]]);
  assert.equal(history.length, 0);
  held.resolve(true);
  await first;
  assert.equal(page.shortVideoEpisodeSwitching, false);
  await page.onPlayerEpisodeDelta(-1);
  assert.deepEqual(calls, [[1, true], [-1, true]]);
});

test('short video respects loading, auto-next and smart pending, even after its own await finishes', async () => {
  const { page, history } = fixture();
  page.shortVideoMode = true;
  for (const [key, value] of [['playLoading', true], ['autoNextInFlight', true], ['smartPlaybackPendingRunSeq', 7]]) {
    page.switchEpisodeByDelta = async () => { page[key] = value; };
    await page.onPlayerEpisodeDelta(1);
    assert.equal(page.shortVideoEpisodeSwitchInFlight, false);
    assert.equal(page.shortVideoEpisodeSwitching, true);
    page.switchEpisodeByDelta = () => assert.fail(`ignored ${key}`);
    await page.onPlayerEpisodeDelta(-1);
    page.onPlayerEnded({});
    page[key] = key === 'smartPlaybackPendingRunSeq' ? 0 : false;
  }
  assert.equal(history.length, 0);
});

test('failed or boundary switches release the lock without swallowing errors or boundary feedback', async () => {
  const { page } = fixture();
  page.shortVideoMode = true;
  const originalSwitch = page.switchEpisodeByDelta;
  for (const fail of [() => { throw new Error('sync'); }, async () => { throw new Error('async'); }]) {
    page.switchEpisodeByDelta = fail;
    await assert.rejects(page.onPlayerEpisodeDelta(1), /sync/);
    assert.equal(page.shortVideoEpisodeSwitching, false);
  }
  const toasts = [];
  page.switchEpisodeByDelta = originalSwitch;
  page.playNextFromCurrentContext = page.playPreviousFromCurrentContext = async () => false;
  page.showPlayerActionToast = text => toasts.push(text);
  await page.onPlayerEpisodeDelta(1);
  await page.onPlayerEpisodeDelta(-1);
  assert.deepEqual(toasts, ['已经是最后一集', '已经是第一集']);
  assert.equal(page.shortVideoEpisodeSwitching, false);
});

test('exiting and reentering during a switch cannot release its in-flight lock early', async () => {
  const { page, flushTicks } = fixture();
  const held = deferred();
  let calls = 0;
  page.switchEpisodeByDelta = () => { calls += 1; return held.promise; };
  page.enterShortVideoMode();
  const first = page.onPlayerEpisodeDelta(1);
  page.exitShortVideoMode();
  flushTicks();
  assert.equal(page.shortVideoEpisodeSwitchInFlight, true);
  page.enterShortVideoMode();
  await page.onPlayerEpisodeDelta(-1);
  assert.equal(calls, 1);
  held.resolve();
  await first;
  assert.equal(page.shortVideoEpisodeSwitching, false);
});

test('ended owns auto-next during history flush, switching and the existing cooldown', async () => {
  const { page, context, timers } = fixture();
  const history = deferred(), switching = deferred(), calls = [];
  page.shortVideoMode = true;
  context.syncHistoryProgressIfPossible = () => history.promise;
  page.switchEpisodeByDelta = (delta, options) => { calls.push([delta, options.showBoundaryToast]); return switching.promise; };
  page.onPlayerEnded({});
  assert.equal(page.shortVideoEpisodeSwitching, true);
  await page.onPlayerEpisodeDelta(-1);
  page.onPlayerEnded({});
  assert.equal(calls.length, 0);
  history.resolve();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(calls, [[1, false]]);
  await page.onPlayerEpisodeDelta(1);
  switching.resolve();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(page.shortVideoEpisodeSwitching, true);
  assert.equal(timers.length, 1);
  assert.equal(timers[0].delay, 800);
  timers[0].fn();
  assert.equal(page.shortVideoEpisodeSwitching, false);
});

test('ordinary deltas keep their original loading/concurrency behavior and guards', async () => {
  const { page, history } = fixture();
  const held = deferred();
  let calls = 0;
  page.playLoading = true;
  page.switchEpisodeByDelta = () => { calls += 1; return held.promise; };
  const first = page.onPlayerEpisodeDelta(1), second = page.onPlayerEpisodeDelta(-1);
  assert.equal(calls, 2);
  assert.equal(page.shortVideoEpisodeSwitchInFlight, false);
  assert.equal(page.shortVideoEpisodeSwitching, false);
  page.onPlayerEnded({});
  assert.equal(history.length, 1);
  assert.equal(calls, 2);
  page.autoNextInFlight = true;
  await page.onPlayerEpisodeDelta(1);
  page.autoNextInFlight = false;
  page.smartPlaybackPendingRunSeq = 3;
  await page.onPlayerEpisodeDelta(1);
  assert.equal(calls, 2);
  held.resolve();
  await Promise.all([first, second]);
});
