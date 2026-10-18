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
    dispatch(type, event = {}) {
      for (const listener of listeners.get(type) || []) listener(event);
    },
  };
}

function gestures(props = {}) {
  let now = 1000;
  let nextFrame = 0;
  const frames = new Map();
  const writes = [];
  const computedTransforms = [];
  const transitions = [];
  const captures = new Set();
  const input = Object.assign(eventTarget(), {
    clientHeight: 600,
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
      shortVideoMode: true, episodeSwitching: false, url: 'episode.mp4',
      shortVideoPrev: 'Episode 1', shortVideoNext: 'Episode 3', ...props,
    },
    shortVideoRateOpen: { value: false },
    shell: { value: input }, pageTrack: { value: track },
    ref: value => ({ value }),
    computed: getter => ({ get value() { return getter(); } }),
    window, document, Date: { now: () => now }, performance: { now: () => now },
    isUiControlTarget: target => !!target.control,
    deltas: [],
    taps: 0,
    emit(name, delta) { if (name === 'episodedelta') context.deltas.push(delta); },
    togglePlay() { context.taps++; },
    track, frames, writes, computedTransforms, transitions,
    now: () => now,
    advance(ms) { now += ms; },
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
    section('const shortVideoPeerSnapshot =', 'const overlayVisible =') +
    section('const emitEpisodeDelta =', 'watch(() => props.shortVideoMode') +
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
  const context = gestures();
  const controlEvent = event(100, 300, { target: { control: true } });
  context.down(controlEvent);
  context.up(event(100, 100));
  context.click(controlEvent);
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
  assert.equal(controlEvent.prevented, false);
  assert.equal(controlEvent.stopped, false);
  assert.equal(context.shell.value.hasPointerCapture(1), false);
  assert.deepEqual(context.writes, []);
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
  const context = gestures();
  context.shortVideoRateOpen.value = true;
  swipe(context, 0, -100);
  context.click(event());
  assert.equal(context.shortVideoRateOpen.value, true);
  assert.deepEqual(context.writes, []);
  assert.equal(context.frames.size, 0);
  assert.equal(context.transitions.length, 0);
  context.down(event());
  context.up(event());
  context.click(event());
  assert.equal(context.shortVideoRateOpen.value, false);
  assert.deepEqual(context.deltas, []);
  assert.equal(context.taps, 0);
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
    props: { shortVideoMode: true, episodeSwitching: false, url: 'episode.mp4' },
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

test('only native MP4/iOS HLS reuse the retained media element; custom engines do not', () => {
  const video = { canPlayType: () => 'maybe' };
  const code = section('  const retainedVideo = retainedShortVideoVideo;', 'const playUrlForArt =');
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
      format, isIos: { value: ios }, props: { shortVideoMode: enabled, headers },
      retainedShortVideoVideo: video,
    });
    assert.equal(vm.runInContext(code + '\nreuseVideo;', context), expected);
    assert.equal(context.retainedShortVideoVideo, null);
  }
});

test('teardown retains at most one eligible video for a short video transition, not for unmount', () => {
  const video = { pause() {}, removeAttribute() {}, load() {} };
  const context = vm.createContext({
    art: { video, destroy() {}, __tvCustomToken: 'old-loader' },
    currentVideoReusable: true, retainedShortVideoVideo: null,
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
  context.destroy({ preserveShortVideoVideo: true });
  assert.equal(context.retainedShortVideoVideo, video);
  assert.equal(context.art, null);
  assert.equal(oldPlayer.__tvCustomToken, null);
  context.destroy();
  assert.equal(context.retainedShortVideoVideo, null);
  context.art = { video, destroy() {} };
  context.currentVideoReusable = false;
  context.destroy({ preserveShortVideoVideo: true });
  assert.equal(context.retainedShortVideoVideo, null);
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
