const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');

const source = readFileSync('src/shared/ArtPlayer.vue', 'utf8');
function section(start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.ok(from >= 0 && to > from, `source fixture boundary changed: ${start}`);
  return source.slice(from, to);
}

function gestures() {
  const context = vm.createContext({
    props: { shortDramaMode: true, episodeSwitching: false, url: 'episode.mp4' },
    shortDramaRateOpen: { value: false },
    shell: { value: { setPointerCapture() {} } },
    isUiControlTarget: target => !!target.control,
    deltas: [],
    taps: 0,
    emit(name, delta) { if (name === 'episodedelta') context.deltas.push(delta); },
    togglePlay() { context.taps++; },
  });
  vm.runInContext(
    section('const emitEpisodeDelta =', 'watch(() => props.shortDramaMode') +
    '\nObject.assign(this, { down: onShortDramaPointerDown, move: onShortDramaPointerMove, ' +
    'up: onShortDramaPointerUp, cancel: onShortDramaPointerCancel, click: onShortDramaClick });',
    context,
  );
  return context;
}

function event(x = 100, y = 300, extra = {}) {
  return {
    clientX: x, clientY: y, pointerId: 1, isPrimary: true, button: 0, target: {},
    prevented: false, stopped: false,
    preventDefault() { this.prevented = true; },
    stopImmediatePropagation() { this.stopped = true; },
    ...extra,
  };
}

function swipe(context, dx, dy) {
  context.down(event());
  context.move(event(100 + dx, 300 + dy));
  context.up(event(100 + dx, 300 + dy));
}

test('short drama: a tap toggles playback once, without treating small jitter as a swipe', () => {
  const context = gestures();
  swipe(context, 3, -5);
  context.click(event());
  assert.equal(context.taps, 1);
  assert.deepEqual(context.deltas, []);
});

test('short drama: upward/downward swipes select next/previous and suppress the trailing click', () => {
  const context = gestures();
  swipe(context, 5, -100);
  context.click(event());
  swipe(context, -4, 100);
  context.click(event());
  assert.deepEqual(context.deltas, [1, -1]);
  assert.equal(context.taps, 0);
  // A new deliberate tap is not swallowed by the previous swipe's suppression window.
  context.down(event());
  context.up(event());
  context.click(event());
  assert.equal(context.taps, 1);
});

test('short drama: short, horizontal and diagonal drags neither switch episodes nor toggle play', () => {
  const context = gestures();
  for (const [dx, dy] of [[0, 40], [130, 5], [100, 100]]) {
    swipe(context, dx, dy);
    context.click(event());
  }
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
});

test('short drama: moving away then back is a drag, not a tap', () => {
  const context = gestures();
  context.down(event());
  context.move(event(100, 200));
  context.up(event());
  context.click(event());
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
});

test('short drama: cancellation and multitouch do not trigger playback or episode changes', () => {
  for (const cancel of [
    context => context.cancel(),
    context => context.down(event(120, 300, { pointerId: 2, isPrimary: false })),
  ]) {
    const context = gestures();
    context.down(event());
    cancel(context);
    context.up(event(100, 100));
    context.click(event());
    assert.deepEqual(context.deltas, []);
    assert.equal(context.taps, 0);
  }
});

test('short drama: progress, speed and exit controls are not captured as gestures or taps', () => {
  const context = gestures();
  const controlEvent = event(100, 300, { target: { control: true } });
  context.down(controlEvent);
  context.up(event(100, 100));
  context.click(controlEvent);
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
  assert.equal(controlEvent.prevented, false);
});

test('the central paused-play hint remains swipeable, unlike the other controls', () => {
  const context = vm.createContext({ props: { shortDramaMode: true } });
  vm.runInContext(
    section('const isUiControlTarget =', 'const detectVideoFormat =') +
    '\nthis.isControl = isUiControlTarget;',
    context,
  );
  const resume = { closest: selector => selector.includes('.sd-resume') || selector.includes('.yt-btn') };
  assert.equal(context.isControl(resume), false);
  assert.equal(context.isControl({ closest: selector => selector.includes('.yt-setting') }), true);
  context.props.shortDramaMode = false;
  assert.equal(context.isControl(resume), true);
});

test('short drama: an open speed menu blocks swipes; tapping outside only closes the menu', () => {
  const context = gestures();
  context.shortDramaRateOpen.value = true;
  swipe(context, 0, -100);
  context.click(event());
  assert.equal(context.shortDramaRateOpen.value, true);
  context.down(event());
  context.up(event());
  context.click(event());
  assert.equal(context.shortDramaRateOpen.value, false);
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
});

test('short drama: switching, missing URLs and normal mode do not respond to swipes', () => {
  for (const update of [
    { episodeSwitching: true }, { url: '' }, { shortDramaMode: false },
  ]) {
    const context = gestures();
    Object.assign(context.props, update);
    swipe(context, 0, -100);
    assert.deepEqual(context.deltas, []);
  }
});

test('short drama: a switch starting mid-gesture prevents a second episode request', () => {
  const context = gestures();
  context.down(event());
  context.props.episodeSwitching = true;
  context.up(event(100, 100));
  context.click(event());
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
});

function playback() {
  const video = {
    paused: false, playbackRate: 1, defaultPlaybackRate: 1,
    play() { this.paused = false; return Promise.resolve(); },
    pause() { this.paused = true; },
  };
  const context = vm.createContext({
    art: { video, playbackRate: 1 },
    props: { shortDramaMode: true, episodeSwitching: false, url: 'episode.mp4' },
    playbackRate: { value: 1 }, shortDramaPlaybackRate: 1,
    shortDramaRateOpen: { value: true }, autoplayBlocked: { value: false },
    setBuffering(value) { context.buffering = value; },
  });
  vm.runInContext(
    section('const restoreShortDramaRate =', 'const seekBySeconds =') +
    section('const setRate =', 'const setRatio =') +
    '\nObject.assign(this, { restore: restoreShortDramaRate, setRate, togglePlay, reject: handlePlayRejection });',
    context,
  );
  return context;
}

test('short drama: selected speed is applied to video and restored after recreating the player', () => {
  const context = playback();
  context.setRate(1.5);
  assert.equal(context.art.video.playbackRate, 1.5);
  assert.equal(context.art.video.defaultPlaybackRate, 1.5);
  assert.equal(context.shortDramaRateOpen.value, false);
  context.art = { video: { playbackRate: 1, defaultPlaybackRate: 1 } };
  context.restore();
  assert.equal(context.art.video.playbackRate, 1.5);
  assert.equal(context.playbackRate.value, 1.5);
});

test('short drama: changing speed while resolving the next episode is not lost', () => {
  const context = playback();
  context.art = null;
  context.setRate(2);
  context.art = { video: { playbackRate: 1 } };
  context.restore();
  assert.equal(context.art.video.playbackRate, 2);
});

test('normal playback keeps the existing speed setter and is not forced to the short drama rate', () => {
  const context = playback();
  context.props.shortDramaMode = false;
  context.setRate(0.75);
  assert.equal(context.art.playbackRate, 0.75);
  assert.equal(context.shortDramaPlaybackRate, 1);
  context.restore();
  assert.equal(context.art.video.playbackRate, 1);
});

test('short drama: tap pauses/plays the underlying video, but not while changing episodes', async () => {
  const context = playback();
  context.togglePlay();
  assert.equal(context.art.video.paused, true);
  context.props.episodeSwitching = true;
  context.togglePlay();
  assert.equal(context.art.video.paused, true);
  context.props.episodeSwitching = false;
  context.togglePlay();
  assert.equal(context.art.video.paused, false);
  context.art.video.paused = true;
  context.art.video.play = () => Promise.reject({ name: 'NotAllowedError' });
  context.togglePlay();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(context.autoplayBlocked.value, true);
  assert.equal(context.buffering, false);
});

test('autoplay rejection feedback is limited to the current short drama player', () => {
  const context = playback();
  context.reject({ name: 'NotAllowedError' }, {});
  context.reject({ name: 'AbortError' }, context.art);
  assert.equal(context.autoplayBlocked.value, false);
  context.props.shortDramaMode = false;
  context.reject({ name: 'NotAllowedError' }, context.art);
  assert.equal(context.autoplayBlocked.value, false);
});

test('only native MP4/iOS HLS reuse the retained media element; custom engines do not', () => {
  const video = { canPlayType: () => 'maybe' };
  const code = section('  const retainedVideo = retainedShortDramaVideo;', 'const playUrlForArt =');
  for (const [format, ios, headers, enabled, expected] of [
    ['native', true, {}, true, video],
    ['hls', true, {}, true, video],
    ['hls', true, { Authorization: 'token' }, true, null],
    ['hls', false, {}, true, null],
    ['dash', true, {}, true, null],
    ['flv', true, {}, true, null],
    ['native', true, {}, false, null],
  ]) {
    const context = vm.createContext({
      format, isIos: { value: ios }, props: { shortDramaMode: enabled, headers },
      retainedShortDramaVideo: video,
    });
    assert.equal(vm.runInContext(code + '\nreuseVideo;', context), expected);
    assert.equal(context.retainedShortDramaVideo, null);
  }
});

test('teardown retains at most one eligible video for a short drama transition, not for unmount', () => {
  const video = { pause() {}, removeAttribute() {}, load() {} };
  const context = vm.createContext({
    art: { video, destroy() {}, __tvCustomToken: 'old-loader' },
    currentVideoReusable: true, retainedShortDramaVideo: null,
    cleanupMediaSession: null, setBuffering() {}, destroyCustomPlayer() {},
    window: { clearTimeout() {}, cancelAnimationFrame() {} }, document: {},
    shell: { value: null },
    ...Object.fromEntries([
      'settingsOpen', 'teleportTarget', 'isPip', 'playing', 'bufferedEnd',
      'scrubbing', 'pointerScrubbing', 'scrubTime',
    ].map(key => [key, { value: false }])),
  });
  const oldPlayer = context.art;
  vm.runInContext(
    section('const destroyNow =', 'const createCustomPlayer =') + '\nthis.destroy = destroyNow;',
    context,
  );
  context.destroy({ preserveShortDramaVideo: true });
  assert.equal(context.retainedShortDramaVideo, video);
  assert.equal(context.art, null);
  assert.equal(oldPlayer.__tvCustomToken, null);
  context.destroy();
  assert.equal(context.retainedShortDramaVideo, null);
  context.art = { video, destroy() {} };
  context.currentVideoReusable = false;
  context.destroy({ preserveShortDramaVideo: true });
  assert.equal(context.retainedShortDramaVideo, null);
});

test('media errors disable reuse so an old ArtPlayer reconnect cannot overwrite the next episode', () => {
  let onError;
  const context = vm.createContext({
    art: {
      video: { error: { code: 2 } },
      on(name, handler) { assert.equal(name, 'video:error'); onError = handler; },
    },
    currentVideoReusable: true, uiVisible: { value: false },
    setBuffering() {}, emit() {},
  });
  vm.runInContext(section("  art.on('video:error',", '  // iOS native fullscreen'), context);
  onError();
  assert.equal(context.currentVideoReusable, false);
});

test('a custom loader scheduled by a destroyed player cannot touch a reused native HLS element', () => {
  const code = section('function (videoEl, playUrl, artInstance) {', 'const headers = props.headers');
  const loader = vm.runInNewContext(`(${code} throw new Error('active loader'); })`);
  assert.doesNotThrow(() => loader({}, 'old.m3u8', { isDestroy: true }));
  assert.throws(() => loader({}, 'current.m3u8', { isDestroy: false }), /active loader/);
});
