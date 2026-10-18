const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

const source = readFileSync('src/pages/play/PlayPage.vue', 'utf8');
const componentCode = source.slice(source.indexOf('export default {'), source.indexOf('</script>'))
  .replace('export default', 'this.component =');
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
    resetSwitchSkipState() {},
    cancelActivePlaybackFlow() {},
    unbindPlaySearchQuerySubscription() {},
    unbindEpisodePanelResizer() {},
  });
  const flushTicks = () => { while (ticks.length) ticks.shift()(); };
  return { page, component, context, root, body, window, player, flushTicks, timers, history };
}

test('short drama is independent, defaults off, and uses the existing player and title contract', () => {
  const { page } = fixture();
  assert.equal(page.shortDramaMode, false);
  assert.equal(page.shortDramaEpisodeSwitching, false);
  assert.equal(page.isPortraitMode, false);
  const playerTags = source.match(/<ArtPlayer\b[\s\S]*?\/>/g);
  assert.equal(playerTags.length, 1);
  assert.match(playerTags[0], /:short-drama-mode="shortDramaMode"/);
  assert.match(playerTags[0], /:episode-switching="shortDramaEpisodeSwitching"/);
  assert.match(playerTags[0], /:portrait-top-text="portraitTopTitleEpisodeText"/);
  assert.match(playerTags[0], /@exit-short-drama="exitShortDramaMode"/);
  assert.doesNotMatch(playerTags[0], /(?:v-if|:key)=/);
  const modeMethods = source.slice(source.indexOf('    enterShortDramaMode()'), source.indexOf('    toggleEpisodePanel()'));
  assert.doesNotMatch(modeMethods, /requestFullscreen|webkitEnterFullscreen|playerUrl\s*=|\.destroy\(|\.seekTo\(/);
});

test('short-drama layout fills the viewport, keeps desktop-only controls gated and status overlays noninteractive', () => {
  const css = source.slice(source.indexOf('<style>'));
  const rootRule = css.match(/\.play-page\.play-page--short-drama \{([^}]+)\}/)[1];
  assert.match(rootRule, /height: 100vh;[\s\S]*height: 100svh;[\s\S]*height: 100dvh;/);
  assert.match(rootRule, /position: fixed/);
  assert.match(rootRule, /background: #000/);
  assert.ok(Number(rootRule.match(/z-index: (\d+)/)[1]) > 20000, 'cover the index shell toolbar');
  assert.match(rootRule, /padding: 0;/);
  assert.doesNotMatch(rootRule, /safe-area-inset-/, 'ArtPlayer alone handles control safe areas');
  assert.match(css, /\.play-page--short-drama video \{\s*object-fit: contain !important;/);
  const hiddenRule = css.match(/(\.play-page--short-drama \.play-header,[^{]+)\{\s*display: none;/)[1];
  for (const part of ['play-header', 'play-detail', 'play-thirdparty-bar', 'play-episode-panel', 'play-episode-toggle-wrap']) {
    assert.ok(hiddenRule.includes(`.${part}`));
  }
  assert.match(css, /\.play-episode-toggle-wrap \{\s*display: flex;/);
  assert.match(css, /\.play-episode-toggle-wrap \.play-episode-toggle--desktop \{\s*display: none;/);
  assert.match(css, /@media \(min-width: 1024px\) \{\s*\.play-episode-toggle-wrap \.play-episode-toggle--desktop \{\s*display: inline-flex;/);
  const overlayRule = css.match(/\.play-player-overlay \{([^}]+)\}/)[1];
  assert.match(overlayRule, /pointer-events: none;/);
  assert.ok(Number(overlayRule.match(/z-index: (\d+)/)[1]) < 100, 'below sd-touch-surface (100) and sd-ui (110)');
  assert.doesNotMatch(css, /\.play-player-overlay[^{]*\{[^}]*pointer-events:\s*auto/);
});

for (const style of [null, '', 'overflow: auto !important; position: relative; color: red;']) {
  test(`exit restores exact inline styles (${JSON.stringify(style)}), both scroll containers and playback`, () => {
    const f = fixture({ rootStyle: style, bodyStyle: style });
    const { page, root, body, window, player, flushTicks } = f;
    page.playerUrl = 'https://example.test/episode.m3u8';
    const headers = page.playerHeaders = { Authorization: 'fixture' };
    page.siteSourceOpen = page.panSourceOpen = true;
    page.enterShortDramaMode();
    const snapshot = page.shortDramaScrollState;
    assert.equal(page.shortDramaMode, true);
    assert.equal(page.isPortraitMode, false);
    assert.equal(page.portraitPlayerAreaStyle, null);
    assert.equal(page.siteSourceOpen || page.panSourceOpen, false);
    assert.match(root.getAttribute('style'), /overflow:hidden !important/);
    assert.match(body.getAttribute('style'), /position:fixed !important/);
    assert.match(body.getAttribute('style'), /top:-150px !important/);
    page.enterShortDramaMode();
    assert.equal(page.shortDramaScrollState, snapshot);
    root.scrollTop = body.scrollTop = window.scrollY = 0;
    root.scrollLeft = body.scrollLeft = window.scrollX = 0;
    page.exitShortDramaMode();
    flushTicks();
    assert.equal(root.getAttribute('style'), style);
    assert.equal(body.getAttribute('style'), style);
    assert.deepEqual([root.scrollLeft, root.scrollTop, body.scrollLeft, body.scrollTop], [12, 150, 8, 375]);
    assert.deepEqual([window.scrollX, window.scrollY], [12, 150]);
    assert.equal(page.shortDramaScrollState, null);
    assert.equal(page.playerUrl, 'https://example.test/episode.m3u8');
    assert.equal(page.playerHeaders, headers);
    assert.equal(page.$refs.artPlayerRef, player);
    assert.equal(player.currentTime, 42);
    page.exitShortDramaMode();
    flushTicks();
    assert.equal(body.getAttribute('style'), style);
  });
}

test('rapid exit/reentry keeps the original scroll snapshot and the background locked', () => {
  const { page, body, window, flushTicks } = fixture();
  page.enterShortDramaMode();
  const snapshot = page.shortDramaScrollState;
  window.scrollY = 0;
  page.exitShortDramaMode();
  page.enterShortDramaMode();
  flushTicks();
  assert.equal(page.shortDramaScrollState, snapshot);
  assert.match(body.getAttribute('style'), /position:fixed/);
  page.exitShortDramaMode();
  flushTicks();
  assert.equal(window.scrollY, 150);
  assert.equal(body.scrollTop, 375);
  assert.equal(body.getAttribute('style'), null);
});

test('unmount restores styles immediately and scroll again after document layout is restored', () => {
  const { page, component, root, body, window, flushTicks } = fixture({ bodyStyle: 'color: red;' });
  page.enterShortDramaMode();
  flushTicks();
  component.beforeUnmount.call(page);
  assert.equal(root.getAttribute('style'), null);
  assert.equal(body.getAttribute('style'), 'color: red;');
  root.scrollTop = body.scrollTop = window.scrollY = 0;
  flushTicks();
  assert.deepEqual([root.scrollTop, body.scrollTop, window.scrollY], [150, 375, 150]);
  assert.equal(page.shortDramaScrollState, null);
});

test('portrait and short drama are mutually exclusive without losing the episode panel preference', () => {
  for (const hidden of [false, true]) {
    const { page, flushTicks } = fixture();
    page.episodePanelHidden = hidden;
    page.togglePortraitMode();
    assert.equal(page.isPortraitMode, true);
    page.enterShortDramaMode();
    flushTicks();
    assert.equal(page.isPortraitMode, false);
    assert.equal(page.shortDramaMode, true);
    assert.equal(page.episodePanelHidden, hidden);
    assert.equal(page.portraitPlayerAreaStyle, null);
    page.togglePortraitMode();
    flushTicks();
    assert.equal(page.shortDramaMode, false);
    assert.equal(page.isPortraitMode, true);
    assert.equal(page.shortDramaScrollState, null);
    page.togglePortraitMode();
    assert.equal(page.episodePanelHidden, hidden);
  }
});

test('short-drama deltas lock before the first await and ignore overlapping gestures and ended', async () => {
  const { page, history } = fixture();
  const held = deferred(), calls = [];
  page.shortDramaMode = true;
  page.switchEpisodeByDelta = (delta, options) => { calls.push([delta, options.showBoundaryToast]); return held.promise; };
  const first = page.onPlayerEpisodeDelta(1);
  assert.equal(page.shortDramaEpisodeSwitching, true);
  await Promise.all([page.onPlayerEpisodeDelta(1), page.onPlayerEpisodeDelta(-1)]);
  page.onPlayerEnded({ currentTime: 60 });
  assert.deepEqual(calls, [[1, true]]);
  assert.equal(history.length, 0);
  held.resolve(true);
  await first;
  assert.equal(page.shortDramaEpisodeSwitching, false);
  await page.onPlayerEpisodeDelta(-1);
  assert.deepEqual(calls, [[1, true], [-1, true]]);
});

test('short drama respects loading, auto-next and smart pending, even after its own await finishes', async () => {
  const { page, history } = fixture();
  page.shortDramaMode = true;
  for (const [key, value] of [['playLoading', true], ['autoNextInFlight', true], ['smartPlaybackPendingRunSeq', 7]]) {
    page.switchEpisodeByDelta = async () => { page[key] = value; };
    await page.onPlayerEpisodeDelta(1);
    assert.equal(page.shortDramaEpisodeSwitchInFlight, false);
    assert.equal(page.shortDramaEpisodeSwitching, true);
    page.switchEpisodeByDelta = () => assert.fail(`ignored ${key}`);
    await page.onPlayerEpisodeDelta(-1);
    page.onPlayerEnded({});
    page[key] = key === 'smartPlaybackPendingRunSeq' ? 0 : false;
  }
  assert.equal(history.length, 0);
});

test('failed or boundary switches release the lock without swallowing errors or boundary feedback', async () => {
  const { page } = fixture();
  page.shortDramaMode = true;
  const originalSwitch = page.switchEpisodeByDelta;
  for (const fail of [() => { throw new Error('sync'); }, async () => { throw new Error('async'); }]) {
    page.switchEpisodeByDelta = fail;
    await assert.rejects(page.onPlayerEpisodeDelta(1), /sync/);
    assert.equal(page.shortDramaEpisodeSwitching, false);
  }
  const toasts = [];
  page.switchEpisodeByDelta = originalSwitch;
  page.playNextFromCurrentContext = page.playPreviousFromCurrentContext = async () => false;
  page.showPlayerActionToast = text => toasts.push(text);
  await page.onPlayerEpisodeDelta(1);
  await page.onPlayerEpisodeDelta(-1);
  assert.deepEqual(toasts, ['已经是最后一集', '已经是第一集']);
  assert.equal(page.shortDramaEpisodeSwitching, false);
});

test('exiting and reentering during a switch cannot release its in-flight lock early', async () => {
  const { page, flushTicks } = fixture();
  const held = deferred();
  let calls = 0;
  page.switchEpisodeByDelta = () => { calls += 1; return held.promise; };
  page.enterShortDramaMode();
  const first = page.onPlayerEpisodeDelta(1);
  page.exitShortDramaMode();
  flushTicks();
  assert.equal(page.shortDramaEpisodeSwitchInFlight, true);
  page.enterShortDramaMode();
  await page.onPlayerEpisodeDelta(-1);
  assert.equal(calls, 1);
  held.resolve();
  await first;
  assert.equal(page.shortDramaEpisodeSwitching, false);
});

test('ended owns auto-next during history flush, switching and the existing cooldown', async () => {
  const { page, context, timers } = fixture();
  const history = deferred(), switching = deferred(), calls = [];
  page.shortDramaMode = true;
  context.syncHistoryProgressIfPossible = () => history.promise;
  page.switchEpisodeByDelta = (delta, options) => { calls.push([delta, options.showBoundaryToast]); return switching.promise; };
  page.onPlayerEnded({});
  assert.equal(page.shortDramaEpisodeSwitching, true);
  await page.onPlayerEpisodeDelta(-1);
  page.onPlayerEnded({});
  assert.equal(calls.length, 0);
  history.resolve();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(calls, [[1, false]]);
  await page.onPlayerEpisodeDelta(1);
  switching.resolve();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(page.shortDramaEpisodeSwitching, true);
  assert.equal(timers.length, 1);
  assert.equal(timers[0].delay, 800);
  timers[0].fn();
  assert.equal(page.shortDramaEpisodeSwitching, false);
});

test('ordinary and portrait deltas keep their original loading/concurrency behavior and guards', async () => {
  for (const portraitMode of [false, true]) {
    const { page, history } = fixture();
    const held = deferred();
    let calls = 0;
    page.portraitMode = portraitMode;
    page.playLoading = true;
    page.switchEpisodeByDelta = () => { calls += 1; return held.promise; };
    const first = page.onPlayerEpisodeDelta(1), second = page.onPlayerEpisodeDelta(-1);
    assert.equal(calls, 2);
    assert.equal(page.shortDramaEpisodeSwitchInFlight, false);
    assert.equal(page.shortDramaEpisodeSwitching, false);
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
  }
});
