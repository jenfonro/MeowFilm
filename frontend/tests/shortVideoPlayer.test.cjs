const { test, before } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');

const source = readFileSync(join(__dirname, '../src/shared/ArtPlayer.vue'), 'utf8');
let physics;
before(async () => {
  const code = readFileSync(join(__dirname, '../src/shared/swipePhysics.js'));
  physics = await import(`data:text/javascript;base64,${code.toString('base64')}`);
});

function section(start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.ok(from >= 0 && to > from, `source fixture boundary changed: ${start}`);
  return source.slice(from, to);
}

function eventTarget() {
  const listeners = new Map();
  return {
    addEventListener(type, listener) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push(listener);
    },
    removeEventListener(type, listener) {
      listeners.set(type, (listeners.get(type) || []).filter(item => item !== listener));
    },
    dispatch(type, event = {}) {
      for (const listener of listeners.get(type) || []) listener(event);
    },
  };
}

function gestures(props = {}, { time = 30, duration = 240, paused = false, width = 400 } = {}) {
  let now = 1000;
  let nextFrame = 0;
  let nextTimer = 0;
  const frames = new Map();
  const timers = new Map();
  const writes = [];
  const seeks = [];
  const playbackCalls = [];
  const video = {
    duration, paused,
    get currentTime() { return time; },
    set currentTime(value) { seeks.push(value); time = value; },
    play() { playbackCalls.push('play'); this.paused = false; return Promise.resolve(); },
    pause() { playbackCalls.push('pause'); this.paused = true; },
  };
  const computedTransforms = [];
  const transitions = [];
  const captures = new Set();
  const input = Object.assign(eventTarget(), {
    clientHeight: 600,
    clientWidth: width,
    style: {},
    setPointerCapture(id) { captures.add(id); },
    hasPointerCapture(id) { return captures.has(id); },
    releasePointerCapture(id) {
      captures.delete(id);
      input.dispatch('lostpointercapture', { pointerId: id });
    },
  });
  const track = {
    clientHeight: 600,
    style: new Proxy({ transform: '', transition: '' }, {
      set(style, property, value) {
        writes.push([property, value]);
        style[property] = value;
        return true;
      },
    }),
    getAnimations() {
      let finish, reject;
      const finished = new Promise((resolve, fail) => { finish = resolve; reject = fail; });
      const transition = {
        transitionProperty: 'transform', finished, finish, cancelled: false,
        cancel() {
          this.cancelled = true;
          reject(new Error('transition cancelled'));
        },
      };
      transitions.push(transition);
      // An unrelated animation finishing must not land the page.
      return [{ transitionProperty: 'opacity', finished: Promise.resolve() }, transition];
    },
  };
  const window = Object.assign(eventTarget(), {
    requestAnimationFrame(callback) { frames.set(++nextFrame, callback); return nextFrame; },
    cancelAnimationFrame(id) { frames.delete(id); },
    setTimeout(callback, delay) {
      timers.set(++nextTimer, { callback, at: now + delay });
      return nextTimer;
    },
    clearTimeout(id) { timers.delete(id); },
    getComputedStyle(element) {
      assert.equal(element, track);
      computedTransforms.push(element.style.transform);
      return { transform: element.style.transform };
    },
    matchMedia: () => ({ matches: false }),
  });
  const document = Object.assign(eventTarget(), { hidden: false });
  const context = vm.createContext({
    ...physics,
    props: {
      shortVideoMode: true, shortVideoCacheEnabled: true, episodeSwitching: false, url: 'episode.mp4',
      shortVideoPrev: 'Episode 1', shortVideoNext: 'Episode 3', ...props,
    },
    shortVideoRateOpen: { value: false },
    shortVideoSeekPreview: { value: null }, shortVideoSeekDragging: { value: false },
    art: { video }, currentTime: { value: time },
    shortVideoBuffer: { activate() { context.activations.push(context.transitions.length); } },
    activations: [],
    seeks, playbackCalls, timers, bufferedSyncs: 0,
    scheduleBufferedSync() { context.bufferedSyncs++; },
    shell: { value: input }, pageTrack: { value: track },
    ref: value => ({ value }),
    computed: getter => ({ get value() { return getter(); } }),
    watch(_getter, callback) { context.sourceChanged = callback; },
    window, document, Date: { now: () => now }, performance: { now: () => now },
    deltas: [],
    taps: 0,
    emit(name, delta) { if (name === 'episodedelta') context.deltas.push(delta); },
    togglePlay() { context.taps++; },
    track, frames, writes, computedTransforms, transitions,
    now: () => now,
    advance(ms) {
      now += ms;
      for (const [id, timer] of [...timers]) {
        if (timer.at <= now && timers.delete(id)) timer.callback();
      }
    },
    advanceVideo(seconds) { time += seconds; },
    changeUrl(url) {
      context.props.url = url;
      context.sourceChanged();
    },
    frame() {
      // Frames scheduled by a callback belong to the next presentation frame.
      for (const [id, callback] of [...frames]) {
        if (frames.delete(id)) callback(now);
      }
    },
    async finishTransition() {
      const transition = transitions.at(-1);
      assert.ok(transition, 'expected a CSS transform transition');
      transition.finish();
      await transition.finished;
    },
    down(event) {
      document.dispatch('pointerdown', event);
      input.dispatch('pointerdown', event);
    },
    move(event) { input.dispatch('pointermove', event); },
    up(event) {
      input.dispatch('pointerup', event);
      // The browser implicitly releases pointer capture after pointerup.
      if (input.hasPointerCapture(event.pointerId)) input.releasePointerCapture(event.pointerId);
    },
  });
  vm.runInContext(
    section('const isUiControlTarget =', 'const detectVideoFormat =') +
    section('const shortVideoPeerSnapshot =', 'const overlayVisible =') +
    section('const emitEpisodeDelta =', 'watch(() => props.shortVideoMode') +
    section('watch(() => [props.url,', 'const toggleMute =') +
    '\nObject.assign(this, { cancel: cancelShortVideoGesture, click: onShortVideoClick, ' +
    'peers: shortVideoPeers, snapshot: shortVideoPeerSnapshot });' +
    '\n{ const el = shell.value;\n' +
    section("el.addEventListener('pointerdown',", '// iOS Safari') + '\n}',
    context,
  );
  return context;
}

function event(x = 100, y = 300, extra = {}) {
  return {
    clientX: x, clientY: y, pointerId: 1, isPrimary: true, button: 0, target: {},
    timeStamp: 0, cancelable: true, detail: 1,
    prevented: false, stopped: false,
    preventDefault() { this.prevented = true; },
    stopImmediatePropagation() { this.stopped = true; },
    ...extra,
  };
}

function swipe(context, dx, dy) {
  context.down(event(100, 300, { timeStamp: context.now() }));
  context.advance(40);
  context.move(event(100 + dx, 300 + dy, { timeStamp: context.now() }));
  context.frame();
  const up = event(100 + dx, 300 + dy, { timeStamp: context.now() });
  context.up(up);
  return up;
}

test('short video: a tap toggles playback once, without treating small jitter as a swipe', () => {
  const context = gestures();
  swipe(context, 3, -5);
  assert.equal(context.taps, 0);
  const click = event();
  context.click(click);
  assert.equal(context.taps, 1);
  assert.deepEqual(context.deltas, []);
  assert.deepEqual(context.writes, []);
  assert.equal(context.snapshot.value, null);
  assert.equal(click.prevented, true);
  assert.equal(click.stopped, true);
});

test('short video: keyboard clicks remain usable without clearing suppressed pointer clicks', async () => {
  const context = gestures();
  swipe(context, 0, -100);
  context.click(event(100, 300, { detail: 0 }));
  assert.equal(context.taps, 0, 'keyboard input must still wait for settling');
  await context.finishTransition();
  context.click(event(100, 300, { detail: 0 }));
  assert.equal(context.taps, 0, 'keyboard input must still wait for rebasing');
  context.frame();
  context.advance(900);
  context.click(event());
  assert.equal(context.taps, 0, 'a delayed pointer click must remain suppressed');
  context.click(event(100, 300, { detail: 0 }));
  assert.equal(context.taps, 1);
  context.click(event());
  assert.equal(context.taps, 1, 'keyboard input must not clear pointer suppression');
  context.down(event());
  context.up(event());
  context.click(event());
  assert.equal(context.taps, 2, 'a new legitimate tap clears pointer suppression');
  assert.deepEqual(context.deltas, [1]);
});

test('short video: upward/downward swipes select next/previous and suppress the trailing click', async () => {
  const context = gestures();
  for (const [dx, dy] of [[5, -100], [-4, 100]]) {
    const up = swipe(context, dx, dy);
    assert.equal(up.prevented, true);
    assert.equal(up.stopped, true);
    context.click(event());
    await context.finishTransition();
    context.frame();
    context.click(event());
  }
  assert.deepEqual(context.deltas, [1, -1]);
  assert.equal(context.taps, 0);
  // A new deliberate tap is not swallowed by the previous swipe's suppression window.
  context.down(event());
  context.up(event());
  context.click(event());
  assert.equal(context.taps, 1);
});

test('short video: short, horizontal and diagonal drags neither switch episodes nor toggle play', async () => {
  for (const [dx, dy] of [[0, 20], [130, 5], [100, 100]]) {
    const context = gestures();
    swipe(context, dx, dy);
    context.click(event());
    if (context.transitions.length) await context.finishTransition();
    context.frame();
    context.click(event());
    assert.deepEqual(context.deltas, []);
    assert.equal(context.taps, 0);
    assert.equal(context.track.style.transform, '');
  }
});

test('short video: horizontal drags only preview, then seek once on release without changing playback', async t => {
  for (const paused of [false, true]) {
    await t.test(paused ? 'paused, starting on the central play hint' : 'playing', () => {
      const context = gestures({}, { time: 30.25, paused });
      const target = paused
        ? { closest: selector => selector.includes('.sv-resume') || selector.includes('.yt-btn') }
        : {};
      context.currentTime.value = 5; // A stale UI time must not be the seek origin.
      context.down(event(100, 300, { target }));
      assert.equal(context.shell.value.hasPointerCapture(1), true);
      if (!paused) context.advanceVideo(1);
      for (const dx of [40, 80, -20]) {
        const move = event(100 + dx, 305, { target });
        context.move(move);
        context.frame();
        assert.equal(move.prevented, true);
        assert.equal(context.shortVideoSeekPreview.value, 30.25 + dx / 400 * 120);
        assert.equal(context.shortVideoSeekDragging.value, true);
        assert.deepEqual(context.seeks, []);
        assert.equal(context.art.video.currentTime, paused ? 30.25 : 31.25);
        assert.equal(context.currentTime.value, 5);
        assert.equal(context.bufferedSyncs, 0);
        assert.equal(context.timers.size, 0);
      }
      context.advance(1000);
      assert.equal(context.shortVideoSeekPreview.value, 24.25, 'a held preview must not expire');
      assert.deepEqual(context.seeks, []);
      // Release at a new position: commit its exact fractional target, not the last move.
      const up = event(237.5, 305, { target });
      context.up(up);
      assert.equal(up.prevented, true);
      assert.equal(up.stopped, true);
      assert.deepEqual(context.seeks, [71.5]);
      assert.equal(context.art.video.currentTime, 71.5);
      assert.equal(context.currentTime.value, 71.5);
      assert.equal(context.bufferedSyncs, 1);
      assert.equal(context.shortVideoSeekPreview.value, 71.5);
      assert.equal(context.shortVideoSeekDragging.value, false);
      assert.equal(context.shell.value.hasPointerCapture(1), false);
      assert.equal(context.snapshot.value, null);
      assert.equal(context.timers.size, 1);
      context.up(up);
      context.click(event(237.5, 305, { target }));
      context.advance(799);
      assert.equal(context.shortVideoSeekPreview.value, 71.5);
      context.advance(1);
      assert.equal(context.shortVideoSeekPreview.value, null);
      assert.equal(context.shortVideoSeekDragging.value, false);
      assert.equal(context.timers.size, 0);
      context.click(event(237.5, 305, { target }));
      assert.deepEqual(context.seeks, [71.5]);
      assert.equal(context.bufferedSyncs, 1);
      assert.equal(context.art.video.paused, paused);
      assert.deepEqual(context.playbackCalls, []);
      assert.deepEqual(context.deltas, []);
      assert.equal(context.taps, 0);
      assert.deepEqual(context.writes, []);
      assert.equal(context.frames.size, 0);
      assert.equal(context.transitions.length, 0);
    });
  }
});

test('short video: horizontal seek scales by width and duration, with exact clamped targets', async t => {
  for (const [name, time, duration, width, dx, expected] of [
    ['forward', 60, 240, 400, 100, 90],
    ['backward', 60, 240, 400, -100, 30],
    ['clamp at zero', 10, 240, 400, -400, 0],
    ['clamp at duration', 230, 240, 400, 400, 240],
    ['short clip forward', 5, 20, 400, 200, 15],
    ['short clip backward', 15, 20, 400, -200, 5],
    ['short clip clamp', 5, 20, 400, 400, 20],
    ['fractional duration', 0.25, 1.5, 400, 100, 0.625],
    ['120 second duration', 0, 120, 400, 200, 60],
    ['120 second screen cap', 100, 600, 400, 400, 220],
    ['wider input surface', 60, 240, 800, 100, 75],
  ]) {
    await t.test(name, () => {
      const context = gestures({}, { time, duration, width });
      context.down(event());
      context.move(event(100 + dx, 300));
      assert.equal(context.shortVideoSeekPreview.value, expected);
      assert.equal(context.shortVideoSeekDragging.value, true);
      assert.deepEqual(context.seeks, []);
      context.up(event(100 + dx, 300));
      context.click(event());
      assert.deepEqual(context.seeks, [expected]);
      assert.equal(context.art.video.currentTime, expected);
      assert.equal(context.currentTime.value, expected);
      assert.equal(context.bufferedSyncs, 1);
      assert.deepEqual(context.deltas, []);
      assert.equal(context.taps, 0);
    });
  }
});

test('short video: horizontal seeking requires 9px of movement and a strict 1.15 axis ratio', () => {
  for (const [dx, dy, seeking] of [
    [8.5, 0, false], [9, 0, true], [-9, 0, true],
    [22.5, 20, false], [23, 20, false], [23.5, -20, true], [-23.5, 20, true],
  ]) {
    const context = gestures();
    context.down(event());
    context.move(event(100 + dx, 300 + dy));
    assert.equal(context.shortVideoSeekDragging.value, seeking, `movement ${dx}, ${dy}`);
    assert.equal(context.shortVideoSeekPreview.value !== null, seeking);
    assert.deepEqual(context.seeks, []);
    context.up(event(100 + dx, 300 + dy));
    assert.equal(context.seeks.length, seeking ? 1 : 0);
    assert.deepEqual(context.deltas, []);
    assert.deepEqual(context.writes, []);
  }
});

test('short video: horizontal seeking needs a finite positive duration and a positive width', () => {
  for (const [duration, width] of [
    [NaN, 400], [Infinity, 400], [-Infinity, 400], [0, 400], [-1, 400],
    [240, 0], [240, -1],
  ]) {
    const context = gestures({}, { duration, width });
    context.down(event());
    context.move(event(200, 300));
    assert.equal(context.shortVideoSeekPreview.value, null);
    assert.equal(context.shortVideoSeekDragging.value, false);
    context.up(event(200, 300));
    context.click(event());
    assert.deepEqual(context.seeks, []);
    assert.equal(context.bufferedSyncs, 0);
    assert.equal(context.timers.size, 0);
    assert.deepEqual(context.deltas, []);
    assert.equal(context.taps, 0);
    assert.deepEqual(context.writes, []);
  }
});

test('short video: a horizontal axis lock survives large vertical movements in either direction', () => {
  const context = gestures();
  context.down(event());
  context.move(event(108, 300));
  assert.equal(context.shortVideoSeekPreview.value, null);
  context.move(event(110, 300));
  assert.equal(context.shortVideoSeekPreview.value, 33);
  for (const y of [0, 900]) {
    context.move(event(180, y));
    context.frame();
    assert.equal(context.shortVideoSeekPreview.value, 54);
    assert.equal(context.shortVideoSeekDragging.value, true);
    assert.deepEqual(context.seeks, []);
    assert.deepEqual(context.writes, []);
  }
  context.up(event(180, 900));
  context.click(event());
  assert.deepEqual(context.seeks, [54]);
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
  assert.equal(context.frames.size, 0);
  assert.equal(context.transitions.length, 0);
});

test('short video: cancelling a horizontal gesture clears its HUD and never seeks, even on a later release', async t => {
  for (const [name, cancel] of [
    ['pointercancel', context => context.shell.value.dispatch('pointercancel', event())],
    ['lost capture', context => context.shell.value.releasePointerCapture(1)],
    ['second pointer', context => context.down(event(120, 300, { pointerId: 2, isPrimary: false }))],
    ['document second pointer', context => context.document.dispatch('pointerdown', event(120, 300, { pointerId: 2, isPrimary: false }))],
    ['multitouch', context => context.document.dispatch('touchstart', { touches: [{}, {}] })],
    ['blur', context => context.window.dispatch('blur')],
    ['resize', context => context.window.dispatch('resize')],
    ['hidden document', context => { context.document.hidden = true; context.document.dispatch('visibilitychange'); }],
    ['URL switch', context => context.changeUrl('next.mp4')],
  ]) {
    await t.test(name, () => {
      for (const moved of [false, true]) {
        const context = gestures();
        context.down(event());
        if (moved) context.move(event(200, 300));
        assert.equal(context.shortVideoSeekPreview.value, moved ? 60 : null);
        assert.equal(context.shortVideoSeekDragging.value, moved);
        cancel(context);
        assert.equal(context.shortVideoSeekPreview.value, null);
        assert.equal(context.shortVideoSeekDragging.value, false);
        assert.equal(context.shell.value.hasPointerCapture(1), false);
        context.move(event(250, 300));
        context.up(event(250, 300));
        context.click(event());
        context.advance(1000);
        context.frame();
        assert.deepEqual(context.seeks, []);
        assert.equal(context.art.video.currentTime, 30);
        assert.equal(context.currentTime.value, 30);
        assert.equal(context.bufferedSyncs, 0);
        assert.equal(context.timers.size, 0);
        assert.deepEqual(context.playbackCalls, []);
        assert.deepEqual(context.deltas, []);
        assert.equal(context.taps, 0);
        assert.equal(context.shortVideoSeekPreview.value, null);
        assert.equal(context.shortVideoSeekDragging.value, false);
        assert.equal(context.snapshot.value, null);
        assert.equal(context.track.style.transform, '');
        assert.equal(context.frames.size, 0);
        assert.equal(context.transitions.length, 0);
      }
    });
  }
});

test('short video: blur, resize and URL changes also clear a committed seek HUD and its timer', () => {
  for (const interrupt of [
    context => context.window.dispatch('blur'),
    context => context.window.dispatch('resize'),
    context => context.changeUrl('next.mp4'),
  ]) {
    const context = gestures();
    swipe(context, 100, 0);
    assert.equal(context.shortVideoSeekPreview.value, 60);
    assert.equal(context.timers.size, 1);
    interrupt(context);
    assert.equal(context.shortVideoSeekPreview.value, null);
    assert.equal(context.shortVideoSeekDragging.value, false);
    assert.equal(context.timers.size, 0);
    context.advance(1000);
    assert.deepEqual(context.seeks, [60]);
    assert.equal(context.bufferedSyncs, 1);
  }
});

test('short video: a new gesture cancels the previous HUD timer without clearing its own preview', () => {
  const context = gestures();
  swipe(context, 100, 0);
  context.advance(400);
  context.down(event());
  assert.equal(context.shortVideoSeekPreview.value, null);
  assert.equal(context.shortVideoSeekDragging.value, false);
  assert.equal(context.timers.size, 0);
  context.move(event(200, 300));
  context.advance(400);
  assert.equal(context.shortVideoSeekPreview.value, 90);
  assert.equal(context.shortVideoSeekDragging.value, true);
  assert.deepEqual(context.seeks, [60]);
  context.up(event(200, 300));
  context.advance(799);
  assert.equal(context.shortVideoSeekPreview.value, 90);
  context.advance(1);
  assert.equal(context.shortVideoSeekPreview.value, null);
  assert.equal(context.shortVideoSeekDragging.value, false);
  assert.deepEqual(context.seeks, [60, 90]);
  assert.equal(context.bufferedSyncs, 2);
  assert.equal(context.timers.size, 0);
});

test('short video: moving away then back is a drag, not a tap', async () => {
  const context = gestures();
  context.down(event());
  context.move(event(100, 200, { timeStamp: 40 }));
  context.frame();
  context.up(event(100, 300, { timeStamp: 80 }));
  context.click(event());
  await context.finishTransition();
  context.click(event());
  assert.equal(context.track.style.transform, '');
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
});

test('short video: cancellation and multitouch do not trigger playback or episode changes', async () => {
  for (const cancel of [
    context => context.cancel(),
    context => context.down(event(120, 300, { pointerId: 2, isPrimary: false })),
    context => context.document.dispatch('pointerdown', event(120, 300, { pointerId: 2, isPrimary: false })),
    context => context.document.dispatch('touchstart', { touches: [{}, {}] }),
  ]) {
    for (const moved of [false, true]) {
      const context = gestures();
      context.down(event());
      if (moved) {
        context.move(event(100, 200, { timeStamp: 40 }));
        context.frame();
      }
      cancel(context);
      assert.equal(context.shell.value.hasPointerCapture(1), false);
      assert.equal(context.track.style.transform, moved ? 'translate3d(0, 0px, 0)' : '');
      context.up(event(100, 100, { timeStamp: 60 }));
      context.click(event());
      if (moved) await context.finishTransition();
      context.click(event());
      assert.equal(context.track.style.transform, '');
      assert.equal(context.snapshot.value, null);
      assert.deepEqual(context.deltas, []);
      assert.equal(context.taps, 0);
    }
  }
});

test('short video: progress, speed and exit controls are not captured as gestures or taps', () => {
  for (const control of [
    '.yt-progress', '.yt-progress__range', '.yt-btn', '.yt-setting',
    '.yt-setting__menu', '.yt-setting__item', '.yt-top__back', '.art-contextmenu',
  ]) {
    for (const [dx, dy] of [[0, -200], [160, 0]]) {
      const context = gestures();
      const target = { closest: selector => selector.split(',').includes(control) };
      const controlEvent = event(100, 300, { target });
      context.down(controlEvent);
      assert.equal(context.shell.value.hasPointerCapture(1), false, control);
      const move = event(100 + dx, 300 + dy, { target });
      context.move(move);
      context.up(event(100 + dx, 300 + dy, { target }));
      context.click(controlEvent);
      assert.deepEqual(context.deltas, []);
      assert.equal(context.taps, 0);
      assert.equal(controlEvent.prevented, false);
      assert.equal(controlEvent.stopped, false);
      assert.equal(move.prevented, false);
      assert.equal(context.shell.value.hasPointerCapture(1), false);
      assert.deepEqual(context.writes, []);
      assert.deepEqual(context.seeks, []);
      assert.equal(context.shortVideoSeekPreview.value, null);
      assert.equal(context.shortVideoSeekDragging.value, false);
      assert.equal(context.bufferedSyncs, 0);
      assert.equal(context.timers.size, 0);
    }
  }
});

test('the short-video resume button keeps its icon and accessible action without visible text', () => {
  const button = source.match(/<button\b[^>]*class="yt-btn sv-resume"[^>]*>([\s\S]*?)<\/button>/);
  assert.ok(button);
  assert.match(button[0], /aria-label="继续播放"/);
  assert.match(button[0], /@click\.stop="togglePlay"/);
  assert.match(button[1], /<svg\b[^>]*aria-hidden="true"[^>]*>/);
  assert.equal(button[1].replace(/<svg\b[\s\S]*?<\/svg>/, '').trim(), '');
});

test('the central paused-play hint remains swipeable, unlike the other controls', () => {
  const context = vm.createContext({ props: { shortVideoMode: true } });
  vm.runInContext(
    section('const isUiControlTarget =', 'const detectVideoFormat =') +
    '\nthis.isControl = isUiControlTarget;',
    context,
  );
  const resume = { closest: selector => selector.includes('.sv-resume') || selector.includes('.yt-btn') };
  assert.equal(context.isControl(resume), false);
  assert.equal(context.isControl({ closest: selector => selector.includes('.yt-setting') }), true);
  context.props.shortVideoMode = false;
  assert.equal(context.isControl(resume), true);
});

test('short video: an open speed menu blocks swipes; tapping outside only closes the menu', () => {
  for (const [dx, dy] of [[0, -100], [100, 0]]) {
    const context = gestures();
    context.shortVideoRateOpen.value = true;
    swipe(context, dx, dy);
    context.click(event());
    assert.equal(context.shortVideoRateOpen.value, true);
    assert.deepEqual(context.writes, []);
    assert.equal(context.frames.size, 0);
    assert.equal(context.transitions.length, 0);
    assert.equal(context.shortVideoSeekPreview.value, null);
    assert.equal(context.shortVideoSeekDragging.value, false);
    assert.deepEqual(context.seeks, []);
    assert.equal(context.bufferedSyncs, 0);
    assert.equal(context.timers.size, 0);
    context.down(event());
    context.up(event());
    context.click(event());
    assert.equal(context.shortVideoRateOpen.value, false);
    assert.deepEqual(context.deltas, []);
    assert.equal(context.taps, 0);
  }
});

test('short video: switching and normal mode do not respond to swipes', () => {
  for (const update of [
    { episodeSwitching: true }, { shortVideoMode: false },
  ]) {
    const context = gestures(update);
    swipe(context, 0, -100);
    assert.deepEqual(context.deltas, []);
    assert.deepEqual(context.writes, []);
    assert.equal(context.shell.value.hasPointerCapture(1), false);
  }
});

test('short video: a missing or failed media URL still allows swiping to a neighbor', async () => {
  const context = gestures({ url: '' });
  swipe(context, 0, -100);
  assert.deepEqual(context.deltas, []);
  await context.finishTransition();
  assert.deepEqual(context.deltas, [1]);
  assert.equal(context.taps, 0);
});

test('short video: a switch starting mid-gesture prevents a second episode request', async () => {
  const context = gestures();
  context.down(event());
  context.move(event(100, 100, { timeStamp: 40 }));
  context.frame();
  context.props.episodeSwitching = true;
  context.up(event(100, 100, { timeStamp: 50 }));
  assert.equal(context.track.style.transform, 'translate3d(0, 0px, 0)');
  context.click(event());
  await context.finishTransition();
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
});

test('short video: pointer bursts write only the latest transform once per frame on the track', () => {
  const context = gestures();
  context.down(event());
  for (const [dy, timeStamp] of [[-30, 20], [-55, 40], [-90, 60]]) {
    const move = event(100, 300 + dy, { timeStamp });
    context.move(move);
    assert.equal(move.prevented, true);
  }
  assert.equal(context.frames.size, 1);
  assert.deepEqual(context.writes, []);
  context.frame();
  assert.deepEqual(context.writes, [['transform', 'translate3d(0, -90px, 0)']]);
  context.move(event(100, 210, { timeStamp: 70 }));
  context.frame();
  assert.equal(context.writes.length, 1, 'an unchanged offset must not be written again');
  context.move(event(100, 180, { timeStamp: 80 }));
  context.move(event(100, 150, { timeStamp: 90 }));
  assert.equal(context.frames.size, 1);
  assert.equal(context.writes.length, 1);
  context.frame();
  assert.deepEqual(context.writes, [
    ['transform', 'translate3d(0, -90px, 0)'],
    ['transform', 'translate3d(0, -150px, 0)'],
  ]);
  assert.deepEqual(context.shell.value.style, {}, 'the input surface must stay fixed');
});

test('short video: commit waits for the transform transition finished promise, then rebases next frame', async () => {
  const context = gestures();
  swipe(context, 0, -100);
  assert.equal(context.transitions.length, 1);
  assert.deepEqual(context.computedTransforms, ['translate3d(0, -100px, 0)']);
  assert.equal(context.track.style.transition, 'transform 350ms cubic-bezier(.22,.68,.25,1)');
  assert.equal(context.track.style.transform, 'translate3d(0, -600px, 0)');
  await Promise.resolve();
  context.advance(2000);
  context.frame();
  context.click(event());
  assert.deepEqual(context.deltas, [], 'neither elapsed time nor an unrelated animation may commit');
  assert.equal(context.taps, 0);
  await context.finishTransition();
  assert.deepEqual(context.deltas, [1]);
  assert.equal(context.track.style.transform, 'translate3d(0, -600px, 0)');
  assert.equal(context.frames.size, 1);
  context.click(event());
  assert.equal(context.taps, 0, 'the commit-to-rebase gap is not tappable');
  context.frame();
  assert.equal(context.track.style.transform, '');
  assert.equal(context.track.style.transition, '');
  assert.equal(context.snapshot.value, null);
  assert.equal(context.frames.size, 0);
  assert.deepEqual(context.shell.value.style, {});
});

test('short video: neighbor metadata is snapshotted until the committed page rebases', async () => {
  const context = gestures();
  const peers = context.peers.value;
  swipe(context, 0, -100);
  assert.notEqual(context.snapshot.value, peers);
  assert.notEqual(context.snapshot.value[0], peers[0]);
  await context.finishTransition();
  context.props.shortVideoPrev = 'Episode 2';
  context.props.shortVideoNext = 'Episode 4';
  assert.deepEqual(Array.from(context.peers.value, peer => peer.title), ['Episode 1', 'Episode 3']);
  context.frame();
  assert.equal(context.snapshot.value, null);
  assert.deepEqual(Array.from(context.peers.value, peer => peer.title), ['Episode 2', 'Episode 4']);
});

test('short video: real physics distinguishes flicks, slow drags, reversals and pauses', async t => {
  for (const [name, points, expected] of [
    ['28px fast flick', [[-28, 40]], [1]],
    ['fast but below the minimum distance', [[-20, 20]], []],
    ['slow short drag', [[-100, 400]], []],
    ['slow drag at exactly 30 percent', [[-180, 600]], []],
    ['slow drag beyond 30 percent', [[-181, 600]], [1]],
    ['8px reversal after a long drag', [[-240, 100], [-232, 120]], []],
    ['7px jitter after a flick', [[-80, 80], [-73, 100]], [1]],
    ['100ms hold after a flick', [[-80, 40], [-80, 140]], []],
    ['fresh flick after a pause', [[-4, 40], [-4, 540], [-64, 590]], [1]],
  ]) {
    await t.test(name, async () => {
      const context = gestures();
      context.down(event());
      for (const [dy, timeStamp] of points) {
        context.move(event(100, 300 + dy, { timeStamp }));
        context.frame();
      }
      const [dy, timeStamp] = points.at(-1);
      context.up(event(100, 300 + dy, { timeStamp }));
      assert.deepEqual(context.deltas, []);
      assert.equal(context.transitions.length, 1);
      assert.equal(context.track.style.transform, `translate3d(0, ${expected.length ? -600 : 0}px, 0)`);
      context.click(event());
      await context.finishTransition();
      assert.deepEqual(context.deltas, expected);
      context.frame();
      context.click(event());
      assert.equal(context.taps, 0);
      assert.equal(context.track.style.transform, '');
    });
  }
});

test('short video: empty previous/next titles damp the first/last page and always rebound', async () => {
  for (const [missing, dy] of [['shortVideoPrev', 240], ['shortVideoNext', -240]]) {
    const context = gestures({ [missing]: '' });
    context.down(event());
    context.move(event(100, 300 + dy, { timeStamp: 40 }));
    context.frame();
    assert.equal(context.track.style.transform, `translate3d(0, ${dy / 4}px, 0)`);
    assert.equal(context.peers.value.length, 1);
    context.up(event(100, 300 + dy, { timeStamp: 40 }));
    assert.equal(context.track.style.transform, 'translate3d(0, 0px, 0)');
    context.click(event());
    await context.finishTransition();
    assert.deepEqual(context.deltas, []);
    assert.equal(context.taps, 0);
    assert.equal(context.track.style.transform, '');
    assert.deepEqual(context.shell.value.style, {});
  }
});

test('short video: pointercancel and lost capture rebound without leaving a gesture or click', async () => {
  for (const cancel of [
    context => context.shell.value.dispatch('pointercancel', event()),
    context => context.shell.value.releasePointerCapture(1),
  ]) {
    const context = gestures();
    context.down(event());
    context.move(event(100, 200, { timeStamp: 40 }));
    context.frame();
    cancel(context);
    assert.equal(context.shell.value.hasPointerCapture(1), false);
    assert.equal(context.frames.size, 0);
    assert.equal(context.track.style.transform, 'translate3d(0, 0px, 0)');
    context.up(event(100, 100, { timeStamp: 60 }));
    context.click(event());
    await context.finishTransition();
    context.click(event());
    assert.deepEqual(context.deltas, []);
    assert.equal(context.taps, 0);
    assert.equal(context.track.style.transform, '');
    assert.equal(context.snapshot.value, null);
  }
});

test('short video: blur cancels queued or painted motion and prevents stale frames from repainting', () => {
  for (const painted of [false, true]) {
    const context = gestures();
    context.down(event());
    context.move(event(100, 200, { timeStamp: 40 }));
    if (painted) context.frame();
    context.window.dispatch('blur');
    assert.equal(context.frames.size, 0);
    assert.equal(context.shell.value.hasPointerCapture(1), false);
    context.frame();
    context.up(event(100, 100, { timeStamp: 60 }));
    context.click(event());
    assert.equal(context.track.style.transform, '');
    assert.equal(context.track.style.transition, '');
    assert.equal(context.snapshot.value, null);
    assert.deepEqual(context.deltas, []);
    assert.equal(context.taps, 0);
  }
});

test('short video: blur or a hidden document interrupts settling, including an already queued finish', async () => {
  for (const queuedFinish of [false, true]) {
    for (const interrupt of [
      context => context.window.dispatch('blur'),
      context => { context.document.hidden = true; context.document.dispatch('visibilitychange'); },
    ]) {
      const context = gestures();
      swipe(context, 0, -100);
      const transition = context.transitions[0];
      if (queuedFinish) transition.finish();
      interrupt(context);
      assert.equal(transition.cancelled, true);
      await Promise.resolve();
      transition.finish();
      await Promise.resolve();
      context.frame();
      context.click(event());
      assert.deepEqual(context.deltas, []);
      assert.equal(context.taps, 0);
      assert.equal(context.frames.size, 0);
      assert.equal(context.track.style.transform, '');
      assert.equal(context.snapshot.value, null);
    }
  }
});

test('short video: consecutive gestures cannot add clicks or commits while settling or rebasing', async () => {
  const context = gestures();
  swipe(context, 0, -100);
  const firstTransition = context.transitions[0];
  swipe(context, 0, 100);
  context.click(event());
  assert.equal(context.transitions.length, 1);
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
  await context.finishTransition();
  assert.deepEqual(context.deltas, [1]);
  // A new down before the rebase frame is ignored, as is its up/click.
  context.down(event());
  context.up(event(100, 200, { timeStamp: 40 }));
  context.click(event());
  assert.equal(context.transitions.length, 1);
  assert.equal(context.taps, 0);
  context.frame();
  context.click(event());
  assert.equal(context.taps, 0);
  // After rebasing, a genuinely new swipe is accepted exactly once.
  swipe(context, 0, 100);
  context.click(event());
  assert.equal(context.transitions.length, 2);
  firstTransition.finish();
  await Promise.resolve();
  assert.deepEqual(context.deltas, [1]);
  await context.finishTransition();
  context.frame();
  context.click(event());
  assert.deepEqual(context.deltas, [1, -1]);
  assert.equal(context.taps, 0);
  context.down(event());
  context.up(event());
  context.click(event());
  assert.equal(context.taps, 1);
});

test('short video: a touch begun during settling cannot become a tap or swipe after landing', async t => {
  for (const [name, dy, expected] of [['rebound', -20, []], ['commit', -100, [1]]]) {
    for (const heldFor of [300, 900]) {
      await t.test(`${name}, ignored pointer held for ${heldFor}ms`, async () => {
        const context = gestures();
        swipe(context, 0, dy);
        context.advance(60);
        context.down(event(100, 300, { pointerId: 2, timeStamp: context.now() }));
        assert.equal(context.shell.value.hasPointerCapture(2), false);
        context.advance(300);
        await context.finishTransition();
        context.frame();
        context.advance(heldFor - 300);
        context.move(event(100, 100, { pointerId: 2, timeStamp: context.now() }));
        context.up(event(100, 100, { pointerId: 2, timeStamp: context.now() }));
        context.click(event(100, 100, { pointerId: 2, timeStamp: context.now() }));
        assert.deepEqual(context.deltas, expected);
        assert.equal(context.taps, 0, 'an ignored drag must not become a playback click when suppression expires');
        assert.equal(context.transitions.length, 1);
        assert.equal(context.track.style.transform, '');
      });
    }
  }
});

function playback() {
  const video = {
    paused: false, playbackRate: 1, defaultPlaybackRate: 1,
    play() { this.paused = false; return Promise.resolve(); },
    pause() { this.paused = true; },
  };
  const context = vm.createContext({
    art: { video, playbackRate: 1 },
    props: { shortVideoMode: true, shortVideoCacheEnabled: false, episodeSwitching: false, url: 'episode.mp4' },
    shortVideoBuffer: { activate() {} },
    playbackRate: { value: 1 }, shortVideoPlaybackRate: 1,
    shortVideoRateOpen: { value: true }, autoplayBlocked: { value: false },
    setBuffering(value) { context.buffering = value; },
  });
  vm.runInContext(
    section('const restoreShortVideoRate =', 'const seekBySeconds =') +
    section('const setRate =', 'const setRatio =') +
    '\nObject.assign(this, { restore: restoreShortVideoRate, setRate, togglePlay, reject: handlePlayRejection });',
    context,
  );
  return context;
}

test('short video: selected speed is applied to video and restored after recreating the player', () => {
  const context = playback();
  context.setRate(1.5);
  assert.equal(context.art.video.playbackRate, 1.5);
  assert.equal(context.art.video.defaultPlaybackRate, 1.5);
  assert.equal(context.shortVideoRateOpen.value, false);
  context.art = { video: { playbackRate: 1, defaultPlaybackRate: 1 } };
  context.restore();
  assert.equal(context.art.video.playbackRate, 1.5);
  assert.equal(context.playbackRate.value, 1.5);
});

test('short video: changing speed while resolving the next episode is not lost', () => {
  const context = playback();
  context.art = null;
  context.setRate(2);
  context.art = { video: { playbackRate: 1 } };
  context.restore();
  assert.equal(context.art.video.playbackRate, 2);
});

test('normal playback keeps the existing speed setter and is not forced to the short video rate', () => {
  const context = playback();
  context.props.shortVideoMode = false;
  context.setRate(0.75);
  assert.equal(context.art.playbackRate, 0.75);
  assert.equal(context.shortVideoPlaybackRate, 1);
  context.restore();
  assert.equal(context.art.video.playbackRate, 1);
});

test('sticky short video cache keeps the selected speed after leaving the short video UI', () => {
  const context = playback();
  context.props.shortVideoCacheEnabled = true;
  context.setRate(1.5);
  context.props.shortVideoMode = false;
  context.art = null;
  context.setRate(2);
  context.art = { video: { playbackRate: 1, defaultPlaybackRate: 1 } };
  context.restore();
  assert.equal(context.art.video.playbackRate, 2);
  assert.equal(context.art.video.defaultPlaybackRate, 2);
  assert.equal(context.playbackRate.value, 2);
});

test('short video: tap pauses/plays the underlying video, but not while changing episodes', async () => {
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

test('autoplay rejection feedback is limited to the current short video player', () => {
  const context = playback();
  context.reject({ name: 'NotAllowedError' }, {});
  context.reject({ name: 'AbortError' }, context.art);
  assert.equal(context.autoplayBlocked.value, false);
  context.props.shortVideoMode = false;
  context.reject({ name: 'NotAllowedError' }, context.art);
  assert.equal(context.autoplayBlocked.value, false);
});

test('committed swipes activate buffered elements synchronously before the settling animation', async () => {
  for (const dy of [-100, 100]) {
    const context = gestures();
    swipe(context, 0, dy);
    assert.deepEqual(context.activations, [0]);
    assert.deepEqual(context.deltas, [], 'activation precedes the asynchronous episode change');
    await context.finishTransition();
    assert.deepEqual(context.activations, [0]);
    assert.deepEqual(context.deltas, [dy < 0 ? 1 : -1]);
  }
});

test('taps, seeking, cancelled swipes and episode boundaries do not activate neighbors', () => {
  for (const [props, dx, dy] of [
    [{}, 2, 2],
    [{}, 100, 0],
    [{ shortVideoNext: '' }, 0, -100],
    [{ shortVideoPrev: '' }, 0, 100],
    [{ episodeSwitching: true }, 0, -100],
    [{ shortVideoMode: false }, 0, -100],
  ]) {
    const context = gestures(props);
    swipe(context, dx, dy);
    assert.deepEqual(context.activations, []);
  }
  const context = gestures();
  context.down(event());
  context.move(event(100, 100));
  context.cancel();
  context.up(event(100, 100));
  assert.deepEqual(context.activations, []);
});

function mediaVideo({ time = 12, duration = 90, readyState = 0 } = {}) {
  let src = 'episode.mp4';
  const video = Object.assign(eventTarget(), {
    duration, readyState, paused: true, muted: false,
    playbackRate: 1, defaultPlaybackRate: 1, videoWidth: 720, videoHeight: 1280,
    sourceChanges: [], seeks: [], firstChild: null,
    canPlayType: () => 'maybe',
    setAttribute(name, value) { if (name === 'src') this.src = value; },
    removeAttribute(name) {
      if (name === 'src') { this.sourceChanges.push(['removeAttribute', name]); src = ''; }
    },
    removeChild(child) { this.sourceChanges.push(['removeChild', child]); this.firstChild = null; },
    load() { this.sourceChanges.push(['load']); },
    play() { this.paused = false; return Promise.resolve(); },
    pause() { this.paused = true; },
  });
  return Object.defineProperties(video, {
    src: {
      get: () => src,
      set(value) { this.sourceChanges.push(['src', value]); src = value; },
    },
    currentTime: {
      get: () => time,
      set(value) { this.seeks.push(value); time = value; },
    },
  });
}

function playerContext({ props = {}, video = mediaVideo(), cached = null, keptMedia = false } = {}) {
  const cacheCalls = [];
  const destroys = [];
  const frames = [];
  class Artplayer {
    static REMOVE_SRC_WHEN_DESTROY = true;
    constructor(option) {
      this.video = option.proxy ? option.proxy() : video;
      this.events = new Map();
      this.isDestroy = false;
      this.destroyFailures = 0;
      this.template = { $player: null };
      const type = option.type || option.url.split('?')[0].split('.').pop();
      const loader = option.customType[type];
      // ArtPlayer defers custom loaders but assigns native src immediately.
      this.loadSource = () => loader && loader(this.video, option.url, this);
      if (!loader) this.video.src = option.url;
    }
    get currentTime() { return this.video.currentTime; }
    set currentTime(value) { this.video.currentTime = value; }
    get duration() { return this.video.duration; }
    get playing() { return !this.video.paused; }
    get playbackRate() { return this.video.playbackRate; }
    on(name, callback) {
      if (!this.events.has(name)) this.events.set(name, []);
      this.events.get(name).push(callback);
    }
    emit(name) { for (const callback of this.events.get(name) || []) callback(); }
    destroy(remove) {
      destroys.push([remove, Artplayer.REMOVE_SRC_WHEN_DESTROY]);
      if (Artplayer.REMOVE_SRC_WHEN_DESTROY) this.video.removeAttribute('src');
      if (this.destroyFailures-- > 0) throw new Error('destroy failed');
      this.isDestroy = true;
      this.events.clear();
    }
  }
  const context = vm.createContext({
    props: {
      url: 'episode.mp4', headers: {}, autoplay: false, shortVideoMode: true,
      shortVideoCacheEnabled: true, shortVideoCacheKey: 'episode-2',
      shortVideoCacheKeys: ['episode-1', 'episode-2', 'episode-3'],
      shortVideoBufferCandidates: [{ key: 'episode-3', url: 'next.mp4', headers: {} }],
      ...props,
    },
    art: null, Artplayer, cached, keptMedia, cacheCalls, destroys, emitted: [], watchers: [],
    shortVideoBuffer: {
      sync(candidates, keys) { cacheCalls.push(['sync', candidates, keys]); },
      adopt(entry) { cacheCalls.push(['adopt', entry]); },
      park(key, options) { cacheCalls.push(['park', key, options]); return context.keptMedia; },
      take(url, headers, key) { cacheCalls.push(['take', url, headers, key]); return context.cached; },
      detach(video) { cacheCalls.push(['detach', video]); },
      setVisible(visible) { cacheCalls.push(['setVisible', visible]); },
      activate() { cacheCalls.push(['activate']); },
      clear() { cacheCalls.push(['clear']); },
    },
    currentVideoReusable: false, retainedShortVideoVideo: null,
    currentCacheKey: '', currentPlaybackUrl: '', currentPlaybackHeaders: {},
    currentMediaInvalid: false, currentMediaEnded: false, shortVideoPlaybackRate: 1,
    shortVideoSuppressClick: false, container: { value: {} }, shell: { value: null },
    ...Object.fromEntries([
      'isPip', 'isFullscreen', 'isMobile', 'isIos', 'autoplayBlocked', 'playing', 'buffering',
      'currentTime', 'duration', 'volume', 'muted', 'playbackRate', 'aspectRatio', 'uiVisible',
      'desktopControlsVisible', 'settingsOpen', 'teleportTarget', 'bufferedEnd',
      'scrubbing', 'pointerScrubbing', 'scrubTime', 'shortVideoRateOpen',
    ].map(key => [key, { value: 0 }])),
    ...Object.fromEntries([
      'cleanupMediaSession', 'cleanupPipListeners', 'cleanupFsListeners', 'cleanupNoCorsEnforcer',
      'cleanupNativeVideoListeners', 'cleanupPlayerElListeners', 'cleanupInfoExtraObserver',
      'cleanupDesktopLayoutObserver', 'mediaQuery',
    ].map(key => [key, null])),
    ...Object.fromEntries([
      'timeUpdateRaf', 'timeUpdatePending', 'timeUpdateEmitAt', 'bufferUpdateRaf',
      'bufferUpdatePending', 'hideTimer', 'desktopHideTimer', 'rightFitRaf',
    ].map(key => [key, 0])),
    window: Object.assign(eventTarget(), {
      location: { href: 'https://example.com/play', origin: 'https://example.com' },
      clearTimeout() {}, cancelAnimationFrame() {},
      requestAnimationFrame(callback) { frames.push(callback); return frames.length; },
    }),
    document: Object.assign(eventTarget(), { hidden: false }), URL,
    frame() { for (const callback of frames.splice(0)) callback(); },
    emit(name, data) { context.emitted.push([name, data]); },
    setBuffering(value) { context.buffering.value = value; },
    showUiTemporarily() {}, scheduleBufferedSync() {}, syncInfoExtraIntoArtInfoPanel() {},
    bindMediaSession() {}, interruptShortVideoPage() {}, onDocDown() {}, updateIsMobile() {},
    watch(getter, callback) { context.watchers.push({ getter, callback }); },
    onBeforeUnmount(callback) { context.unmount = callback; },
    createCustomPlayer: { hls: async () => null },
  });
  vm.runInContext(
    section('const detectVideoFormat =', 'let hlsModulePromise =') +
    section('const syncShortVideoBuffer =', 'const createCustomPlayer =') +
    section('const init =', 'const seekBySeconds =') +
    section('watch(() => props.shortVideoMode,', 'watch(() => [props.url,') +
    section('watch(() => props.shortVideoCacheEnabled,', '\n\twatch(') +
    section('onBeforeUnmount(() =>', 'const pause =') +
    section('const play = async () =>', '\n\tconst seekTo =') +
    '\nObject.assign(this, { init, destroy: destroyNow, sync: syncShortVideoBuffer, ' +
    'register: registerShortVideoPlayback, togglePlay, tryAutoplay });',
    context,
  );
  return context;
}

test('autoplay does not await a second native play promise when cached frames are already playing', async () => {
  for (const [enabled, paused, readyState, expectedCalls] of [
    [true, false, 2, 0], [true, true, 4, 1], [true, false, 0, 1], [false, false, 2, 1],
  ]) {
    const context = playerContext({ props: { shortVideoCacheEnabled: enabled } });
    context.init();
    Object.assign(context.art.video, { paused, readyState });
    let calls = 0;
    context.art.play = () => {
      calls++;
      return expectedCalls ? Promise.resolve() : new Promise(() => {});
    };
    const result = await Promise.race([
      context.tryAutoplay(),
      new Promise(resolve => setImmediate(() => resolve('pending'))),
    ]);
    assert.equal(result, undefined, 'episode switching must not remain locked behind redundant play()');
    assert.equal(calls, expectedCalls);
  }
});

test('cache sync, adoption and parking follow the sticky flag, not the short video UI mode', () => {
  for (const mode of [false, true]) {
    for (const enabled of [false, true]) {
      const context = playerContext({
        props: { shortVideoMode: mode, shortVideoCacheEnabled: enabled }, keptMedia: true,
      });
      context.init();
      const { props, cacheCalls } = context;
      if (enabled) {
        assert.deepEqual(cacheCalls.find(call => call[0] === 'take'),
          ['take', props.url, props.headers, props.shortVideoCacheKey]);
        const adopted = cacheCalls.find(call => call[0] === 'adopt')[1];
        assert.equal(adopted.key, props.shortVideoCacheKey);
        assert.equal(adopted.url, props.url);
        assert.equal(adopted.video, context.art.video);
        assert.deepEqual({ ...adopted.headers }, props.headers);
        assert.deepEqual(cacheCalls.find(call => call[0] === 'sync'),
          ['sync', props.shortVideoBufferCandidates, props.shortVideoCacheKeys]);
        assert.deepEqual(cacheCalls.find(call => call[0] === 'setVisible'), ['setVisible', true]);
      } else {
        assert.deepEqual(cacheCalls, [['clear']]);
      }
      cacheCalls.length = 0;
      props.shortVideoMode = !mode;
      assert.equal(context.watchers[0].getter(), !mode);
      context.watchers[0].callback(!mode);
      assert.equal(props.shortVideoCacheEnabled, enabled);
      assert.deepEqual(cacheCalls, [], 'switching UI mode must not clear or replace the cache');
      context.document.hidden = true;
      context.sync();
      assert.deepEqual(cacheCalls, enabled ? [
        ['setVisible', false], ['sync', props.shortVideoBufferCandidates, props.shortVideoCacheKeys],
      ] : [['clear']]);
      assert.deepEqual(Array.from(context.watchers[2].getter()),
        [enabled, props.shortVideoBufferCandidates, props.shortVideoCacheKeys]);
      context.togglePlay();
      assert.equal(cacheCalls.some(call => call[0] === 'activate'), enabled);
      context.destroy({ preserveShortVideoVideo: true });
      assert.equal(cacheCalls.some(call => call[0] === 'park'), enabled);
    }
  }
});

test('PWA pageshow restores preload visibility without recreating media or changing pause intent', () => {
  const context = playerContext();
  context.document.hidden = true;
  context.init();
  const player = context.art;
  assert.deepEqual(context.cacheCalls.filter(call => ['sync', 'setVisible'].includes(call[0])), [
    ['setVisible', false], ['sync', context.props.shortVideoBufferCandidates, context.props.shortVideoCacheKeys],
  ]);
  vm.runInContext(section("  document.addEventListener('visibilitychange', syncShortVideoBuffer);", '\n\n  try {'), context);
  context.cacheCalls.length = 0;
  context.document.hidden = false;
  context.window.dispatch('pageshow', { persisted: true });
  assert.deepEqual(context.cacheCalls, [
    ['setVisible', true], ['sync', context.props.shortVideoBufferCandidates, context.props.shortVideoCacheKeys],
  ]);
  assert.equal(context.art, player);
  assert.equal(player.video.currentTime, 12);
  assert.equal(player.video.paused, true);
  const removed = [];
  const remove = context.window.removeEventListener;
  context.window.removeEventListener = (name, listener) => { removed.push(name); remove(name, listener); };
  context.unmount();
  assert.ok(removed.includes('pageshow'));
});

test('enabling caching adopts the current playback without recreating it or using pending props', () => {
  const context = playerContext({ props: { shortVideoMode: false, shortVideoCacheEnabled: false } });
  context.init();
  const player = context.art;
  context.cacheCalls.length = 0;
  assert.equal(context.watchers[1].getter(), false);
  context.props.shortVideoCacheEnabled = true;
  assert.equal(context.watchers[1].getter(), true);
  context.watchers[1].callback(true);
  assert.equal(context.art, player);
  assert.deepEqual(context.cacheCalls.map(call => call[0]), ['adopt']);
  context.props.url = 'next.mp4';
  context.props.headers = { Authorization: 'next-token' };
  context.props.shortVideoCacheKey = 'episode-3';
  context.watchers[1].callback(true);
  assert.equal(context.cacheCalls.length, 1, 'a pending URL must not be adopted as the playing episode');
  context.register();
  const adopted = context.cacheCalls.at(-1)[1];
  assert.equal(adopted.key, 'episode-2');
  assert.equal(adopted.url, 'episode.mp4');
  assert.deepEqual({ ...adopted.headers }, {});
});

test('cached native and HLS media keep their source and engine when ArtPlayer is recreated', async t => {
  for (const [name, url, ios, customPlayer] of [
    ['MP4', 'episode.mp4', false, null],
    ['iOS HLS', 'episode.m3u8', true, { direct: true }],
    ['hls.js', 'episode.m3u8', false, { hls: {} }],
  ]) {
    for (const position of [null, 0, 24]) {
      const restored = position !== null;
      await t.test(`${name}, ${restored ? `returning cache at ${position}s` : 'prefetched neighbor'}`, async () => {
        const video = mediaVideo({ readyState: 3, time: restored ? position : 12 });
        let resumed = 0;
        let created = 0;
        const cached = {
          video, customPlayer, activated: true, restored, position,
          resume() { resumed++; }, destroy() { assert.fail('cached media must not be discarded'); },
        };
        const headers = name === 'hls.js' ? { Authorization: 'token' } : {};
        const context = playerContext({ props: { url, headers, shortVideoMode: false }, cached });
        context.isIos.value = ios;
        context.createCustomPlayer.hls = async () => { created++; return null; };
        context.init();
        await context.art.loadSource();
        assert.equal(context.art.video, video);
        assert.equal(context.art.customPlayer, customPlayer);
        assert.deepEqual(context.cacheCalls.find(call => call[0] === 'take'),
          ['take', url, headers, 'episode-2']);
        assert.equal(resumed, 1);
        assert.equal(created, 0);
        assert.deepEqual(video.sourceChanges, [], 'neither ArtPlayer nor the component may reset the cached source');
        assert.deepEqual(video.seeks, [], 'retained media resumes without a redundant seek');
        assert.equal(context.cacheCalls.some(call => call[0] === 'detach'), false);
        const firstFrames = context.emitted.filter(([name]) => name === 'firstframe');
        assert.equal(firstFrames.length, 1, 'already-loaded cached media must rehydrate the new UI');
        assert.equal(firstFrames[0][1].cacheRestored, restored);
        assert.equal(firstFrames[0][1].currentTime, restored ? position : 12);
        video.dispatch('loadedmetadata');
        context.art.emit('video:loadedmetadata');
        context.art.emit('video:canplay');
        assert.equal(context.emitted.filter(([name]) => name === 'loadedmetadata').length, 1);
        assert.equal(context.emitted.filter(([name]) => name === 'firstframe').length, 1);
      });
    }
  }
});

test('position-only cache returns restore even zero on loadedmetadata, once, and mark firstframe', async t => {
  for (const position of [0, 37.5]) {
    for (const nativeEvent of [false, true]) {
      await t.test(`${position}s via ${nativeEvent ? 'native' : 'ArtPlayer'} metadata`, async () => {
        const context = playerContext({
          props: { shortVideoMode: false, url: nativeEvent ? 'episode.mp4' : 'episode.m3u8' },
          cached: { video: null, restored: true, position },
        });
        const customPlayer = { hls: {} };
        context.createCustomPlayer.hls = async () => customPlayer;
        context.init();
        await context.art.loadSource();
        if (!nativeEvent) {
          assert.equal(context.art.customPlayer, customPlayer);
          assert.equal(context.cacheCalls.filter(call => call[0] === 'adopt').at(-1)[1].customPlayer, customPlayer);
        }
        const video = context.art.video;
        assert.deepEqual(video.seeks, [], 'a position-only return must wait for metadata on the new video');
        video.readyState = 1;
        if (nativeEvent) video.dispatch('loadedmetadata');
        else context.art.emit('video:loadedmetadata');
        assert.deepEqual(video.seeks, [position]);
        assert.equal(context.currentTime.value, position);
        context.art.emit('video:canplay');
        const firstFrame = context.emitted.find(([name]) => name === 'firstframe')[1];
        assert.equal(firstFrame.cacheRestored, true);
        assert.equal(firstFrame.currentTime, position);
        video.currentTime = 55;
        video.dispatch('loadedmetadata');
        context.art.emit('video:loadedmetadata');
        context.art.emit('video:canplay');
        assert.deepEqual(video.seeks, [position, 55], 'duplicate metadata must not rewind playback again');
        assert.equal(context.emitted.filter(([name]) => name === 'firstframe').length, 1);
      });
    }
  }
  const cold = playerContext();
  cold.init();
  cold.art.emit('video:loadedmetadata');
  cold.art.emit('video:canplay');
  assert.deepEqual(cold.art.video.seeks, []);
  assert.equal(cold.emitted.find(([name]) => name === 'firstframe')[1].cacheRestored, false);
});

test('a retained native video is detached from the helper before reuse for another source', () => {
  const retained = mediaVideo();
  const context = playerContext({ props: { shortVideoMode: false, url: 'next.mp4' } });
  context.retainedShortVideoVideo = retained;
  context.init();
  assert.equal(context.art.video, retained);
  assert.deepEqual(context.cacheCalls.find(call => call[0] === 'detach'), ['detach', retained]);
});

test('parking media destroys only ArtPlayer UI and restores REMOVE_SRC_WHEN_DESTROY in finally', async t => {
  for (const removeSrc of [false, true]) {
    for (const failures of [0, 1, 2]) {
      await t.test(`original flag ${removeSrc}, ${failures} destroy failures`, () => {
        const context = playerContext({ props: { shortVideoMode: false }, keptMedia: true });
        context.init();
        const player = context.art;
        const video = player.video;
        let engineDestroyed = 0;
        player.customPlayer = { hls: { destroy() { engineDestroyed++; } } };
        player.__tvCustomToken = 'old-loader';
        player.destroyFailures = failures;
        video.sourceChanges.length = 0;
        const child = video.firstChild = {};
        context.Artplayer.REMOVE_SRC_WHEN_DESTROY = removeSrc;
        context.destroy({ preserveShortVideoVideo: true });
        assert.deepEqual(context.destroys, failures ? [[true, false], [false, false]] : [[true, false]]);
        assert.equal(context.Artplayer.REMOVE_SRC_WHEN_DESTROY, removeSrc);
        assert.deepEqual(video.sourceChanges, []);
        assert.equal(video.firstChild, child);
        assert.equal(engineDestroyed, 0);
        assert.equal(player.__tvCustomToken, null);
        assert.equal(context.art, null);
        const [, key, options] = context.cacheCalls.find(call => call[0] === 'park');
        assert.equal(key, 'episode-2');
        assert.deepEqual({ ...options }, { ended: false, invalid: false });
      });
    }
  }
});

test('a long-video park(false), disabled cache and unmount keep the original destructive teardown', async t => {
  for (const name of ['long video', 'cache disabled', 'unmount']) {
    await t.test(name, () => {
      const context = playerContext({
        props: { shortVideoCacheEnabled: name !== 'cache disabled' },
        keptMedia: name !== 'long video',
      });
      context.init();
      const video = context.art.video;
      let engineDestroyed = 0;
      context.art.customPlayer = { hls: { destroy() { engineDestroyed++; } } };
      video.sourceChanges.length = 0;
      video.firstChild = {};
      context.cacheCalls.length = 0;
      if (name === 'unmount') context.unmount();
      else context.destroy({ preserveShortVideoVideo: true });
      assert.equal(context.cacheCalls.some(call => call[0] === 'park'), name === 'long video');
      if (name === 'unmount') {
        assert.deepEqual(context.cacheCalls, [['clear']]);
        assert.equal(context.shortVideoBuffer, null);
        assert.equal(context.retainedShortVideoVideo, null);
      }
      assert.equal(video.src, '');
      assert.ok(video.sourceChanges.some(([name]) => name === 'load'));
      assert.equal(video.firstChild, null);
      assert.equal(engineDestroyed, 1);
      assert.deepEqual(context.destroys, [[true, true]]);
      assert.equal(context.Artplayer.REMOVE_SRC_WHEN_DESTROY, true);
    });
  }
});

test('native and ArtPlayer ended events mark the cache without resetting reported history time', () => {
  for (const nativeEvent of [false, true]) {
    for (const replay of [false, true]) {
      const video = mediaVideo({ time: 90, duration: 90 });
      const context = playerContext({ video, keptMedia: true });
      context.init();
      if (nativeEvent) video.dispatch('ended');
      else context.art.emit('video:ended');
      assert.equal(context.currentMediaEnded, true);
      assert.equal(video.currentTime, 90);
      assert.equal(context.currentTime.value, 90);
      assert.deepEqual(video.seeks, []);
      assert.equal(context.emitted.find(([name]) => name === 'ended')[1].currentTime, 90);
      context.art.emit('video:pause');
      assert.equal(context.emitted.find(([name]) => name === 'timeupdate')[1].currentTime, 90);
      context.art.emit('video:timeupdate');
      context.frame();
      assert.ok(context.emitted.filter(([name]) => name === 'timeupdate').every(([, data]) => data.currentTime === 90));
      if (replay) context.art.emit('video:play');
      context.destroy({ preserveShortVideoVideo: true });
      assert.equal(context.cacheCalls.find(call => call[0] === 'park')[2].ended, !replay);
    }
  }
});

test('only native MP4/iOS HLS reuse the retained media element; custom engines do not', () => {
  const video = { canPlayType: () => 'maybe' };
  const code = section('  const retainedVideo = retainedShortVideoVideo;', 'const playUrlForArt =');
  for (const [format, ios, headers, mode, cache, expected] of [
    ['native', true, {}, true, false, video],
    ['hls', true, {}, true, false, video],
    ['hls', true, { Authorization: 'token' }, true, false, null],
    ['hls', false, {}, true, false, null],
    ['dash', true, {}, true, false, null],
    ['flv', true, {}, true, false, null],
    ['native', true, {}, false, false, null],
    ['native', true, {}, false, true, video],
    ['hls', true, {}, false, true, video],
  ]) {
    const context = vm.createContext({
      format, isIos: { value: ios }, props: { shortVideoMode: mode, shortVideoCacheEnabled: cache, headers },
      retainedShortVideoVideo: video, warmed: null, shortVideoBuffer: null,
      url: 'episode.mp4',
    });
    assert.equal(vm.runInContext(code + '\nreuseVideo;', context), expected);
    assert.equal(context.retainedShortVideoVideo, null);
  }
});

test('iOS retains authorized native playback unless the warmed element was gesture-activated', () => {
  const video = { canPlayType: () => 'maybe' };
  const code = section('  const retainedVideo = retainedShortVideoVideo;', 'const playUrlForArt =');
  for (const [format, ios, headers, activated, useWarm] of [
    ['native', true, {}, false, false],
    ['hls', true, {}, false, false],
    ['native', true, {}, true, true],
    ['hls', true, {}, true, true],
    ['native', false, {}, false, true],
    ['hls', true, { Authorization: 'token' }, true, true],
  ]) {
    let destroyed = false;
    const warmVideo = {};
    const context = vm.createContext({
      format, isIos: { value: ios }, props: { shortVideoMode: true, headers },
      retainedShortVideoVideo: video, shortVideoBuffer: null, url: 'episode.mp4',
      warmed: { video: warmVideo, activated, destroy() { destroyed = true; } },
    });
    assert.equal(vm.runInContext(code + '\nreuseVideo;', context), useWarm ? warmVideo : video);
    assert.equal(destroyed, !useWarm);
  }
});

test('teardown retains at most one eligible video for a short video transition, not for unmount', () => {
  const context = playerContext({ props: { shortVideoCacheEnabled: false } });
  context.init();
  const video = context.art.video;
  const oldPlayer = context.art;
  oldPlayer.__tvCustomToken = 'old-loader';
  context.destroy({ preserveShortVideoVideo: true });
  assert.equal(context.retainedShortVideoVideo, video);
  assert.equal(context.art, null);
  assert.equal(oldPlayer.__tvCustomToken, null);
  context.destroy();
  assert.equal(context.retainedShortVideoVideo, null);
  context.init();
  context.currentVideoReusable = false;
  context.destroy({ preserveShortVideoVideo: true });
  assert.equal(context.retainedShortVideoVideo, null);
});

test('media errors invalidate cache parking and disable reuse by delayed ArtPlayer reconnects', async t => {
  for (const route of ['native', 'ArtPlayer', 'adopt', 'resume']) {
    await t.test(route, async () => {
      const video = mediaVideo();
      video.error = { code: 2 };
      let resumeError;
      let engineDestroyed = 0;
      let oldErrorCleanup = 0;
      const cached = {
        video, activated: true, restored: true, position: 12,
        customPlayer: {
          hls: { destroy() { engineDestroyed++; } },
          __tvCleanupError() { oldErrorCleanup++; },
        },
        resume(onError) { resumeError = onError; },
      };
      // The helper rejects invalid media; verify the component passes the invalid flag.
      const context = playerContext({ cached, keptMedia: false });
      context.init();
      await context.art.loadSource();
      const adopted = context.cacheCalls.find(call => call[0] === 'adopt')[1];
      assert.equal(oldErrorCleanup, 1);
      if (route === 'native') video.dispatch('error');
      if (route === 'ArtPlayer') context.art.emit('video:error');
      if (route === 'adopt') adopted.onError('fatal HLS error');
      if (route === 'resume') resumeError('fatal HLS error');
      assert.equal(context.currentMediaInvalid, true);
      assert.equal(context.currentVideoReusable, false);
      assert.equal(context.emitted.filter(([name]) => name === 'error').length, 1);
      context.destroy({ preserveShortVideoVideo: true });
      assert.equal(context.cacheCalls.find(call => call[0] === 'park')[2].invalid, true);
      assert.equal(context.retainedShortVideoVideo, null);
      assert.equal(video.src, '');
      assert.equal(engineDestroyed, 1);
      assert.deepEqual(context.destroys, [[true, true]]);
      const emitted = context.emitted.length;
      adopted.onError('stale error');
      resumeError('stale error');
      video.dispatch('error');
      assert.equal(context.emitted.length, emitted);
      assert.equal(context.currentMediaInvalid, false);
    });
  }
});

test('a custom loader scheduled by a destroyed player cannot touch a reused native HLS element', () => {
  const code = section('function (videoEl, playUrl, artInstance) {', 'const headers = props.headers');
  const loader = vm.runInNewContext(`(${code} throw new Error('active loader'); })`, { warmed: null, format: 'hls' });
  assert.doesNotThrow(() => loader({}, 'old.m3u8', { isDestroy: true }));
  assert.throws(() => loader({}, 'current.m3u8', { isDestroy: false }), /active loader/);
});
