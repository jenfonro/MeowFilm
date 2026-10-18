const { test, before } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');

let createSwipeTracker, shouldCommitSwipe, swipeSettleDuration;
before(async () => {
  const source = readFileSync(join(__dirname, '../src/shared/swipePhysics.js'));
  ({ createSwipeTracker, shouldCommitSwipe, swipeSettleDuration } =
    await import(`data:text/javascript;base64,${source.toString('base64')}`));
});

test('a fast 28px flick commits in either direction', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(420, 1000);
    tracker.move(420 + direction * 14, 1020);
    const sample = tracker.release(420 + direction * 28, 1040);
    assert.deepEqual(sample, { dx: direction * 28, velocity: direction * 0.7, reversed: false });
    assert.equal(shouldCommitSwipe(sample, 600), true);
  }
});

test('a short swipe needs aligned velocity strictly greater than 0.45px/ms', () => {
  for (const direction of [-1, 1]) {
    for (const [speed, expected] of [[0.449, false], [0.45, false], [0.451, true], [-0.8, false]]) {
      const sample = { dx: direction * 60, velocity: direction * speed, reversed: false };
      assert.equal(shouldCommitSwipe(sample, 600), expected, `direction=${direction}, speed=${speed}`);
    }
  }
});

test('fast small drags beyond 9px but below 28px do not commit', () => {
  for (const direction of [-1, 1]) {
    for (const distance of [10, 18, 27.999]) {
      const tracker = createSwipeTracker(0, 100);
      const sample = tracker.release(direction * distance, 120);
      assert.ok(Math.abs(sample.velocity) > 0.45);
      assert.equal(shouldCommitSwipe(sample, 600), false, `distance=${direction * distance}`);
    }
  }
});

test('velocity uses an interpolated 100ms window, not an earlier fast segment', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(0, 0);
    tracker.move(direction * 150, 50);
    tracker.move(direction * 160, 150);
    const sample = tracker.release(direction * 170, 200);
    // At the 100ms cutoff the interpolated position is 155px: only 15px remain.
    assert.deepEqual(sample, { dx: direction * 170, velocity: direction * 0.15, reversed: false });
    assert.ok(Math.abs(sample.dx) / 200 > 0.45);
    assert.equal(shouldCommitSwipe(sample, 600), false);
  }
});

test('a slow drag commits only beyond 30 percent of the screen height', () => {
  for (const direction of [-1, 1]) {
    for (const [distance, expected] of [[209, false], [210, false], [211, true]]) {
      const tracker = createSwipeTracker(0, 100);
      const sample = tracker.release(direction * distance, 1100);
      assert.equal(sample.dx, direction * distance);
      assert.ok(Math.abs(sample.velocity) < 0.45);
      assert.equal(shouldCommitSwipe(sample, 700), expected, `distance=${direction * distance}`);
    }
  }
});

test('holding a short flick for 100ms or longer removes its velocity', () => {
  for (const direction of [-1, 1]) {
    for (const heldFor of [100, 250]) {
      const tracker = createSwipeTracker(0, 1000);
      tracker.move(direction * 72, 1040);
      const sample = tracker.release(direction * 72, 1040 + heldFor);
      assert.deepEqual(sample, { dx: direction * 72, velocity: 0, reversed: false });
      assert.equal(shouldCommitSwipe(sample, 700), false);
    }
  }
});

test('a held drag can still commit through distance alone', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(0, 0);
    tracker.move(direction * 211, 40);
    const sample = tracker.release(direction * 211, 140);
    assert.equal(sample.velocity, 0);
    assert.equal(shouldCommitSwipe(sample, 700), true);
  }
});

test('an exact 8px reversal cancels even a distance-qualified swipe', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(0, 0);
    tracker.move(direction * 240, 100);
    const sample = tracker.release(direction * 232, 120);
    assert.deepEqual(sample, { dx: direction * 232, velocity: -direction * 0.4, reversed: true });
    assert.ok(Math.abs(sample.dx) > 600 * 0.3);
    assert.equal(shouldCommitSwipe(sample, 600), false);
  }
});

test('several opposite moves accumulating to 8px also count as a reversal', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(0, 0);
    tracker.move(direction * 240, 100);
    tracker.move(direction * 237, 110);
    tracker.move(direction * 234, 120);
    const sample = tracker.release(direction * 232, 130);
    assert.equal(sample.dx, direction * 232);
    assert.equal(sample.reversed, true);
    assert.ok(sample.velocity * direction < 0);
    assert.equal(shouldCommitSwipe(sample, 600), false);
  }
});

test('small backwards jitter below 8px does not reverse a flick', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(0, 0);
    tracker.move(direction * 80, 80);
    tracker.move(direction * 77, 90);
    const sample = tracker.release(direction * 73, 100);
    assert.deepEqual(sample, { dx: direction * 73, velocity: direction * 0.73, reversed: false });
    assert.equal(shouldCommitSwipe(sample, 600), true);
  }
});

test('a fresh flick after a pause regains recent velocity', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(0, 0);
    tracker.move(direction * 4, 30);
    tracker.move(direction * 4, 530);
    tracker.move(direction * 34, 550);
    const sample = tracker.release(direction * 64, 580);
    assert.deepEqual(sample, { dx: direction * 64, velocity: direction * 0.6, reversed: false });
    assert.ok(Math.abs(sample.dx) / 580 < 0.45);
    assert.equal(shouldCommitSwipe(sample, 600), true);
  }
});

test('move ignores non-finite, non-numeric and backwards timestamps', () => {
  for (const time of [NaN, Infinity, -Infinity, undefined, null, '140', 129]) {
    const tracker = createSwipeTracker(100, 100);
    tracker.move(130, 130);
    tracker.move(-1000, time);
    const sample = tracker.release(160, 160);
    assert.deepEqual(sample, { dx: 60, velocity: 1, reversed: false }, `timestamp=${String(time)}`);
    assert.equal(shouldCommitSwipe(sample, 600), true);
  }
});

test('release ignores invalid timestamps without corrupting the last valid sample', () => {
  for (const time of [NaN, Infinity, -Infinity, undefined, null, '140', 129]) {
    const tracker = createSwipeTracker(100, 100);
    tracker.move(130, 130);
    assert.deepEqual(
      tracker.release(-1000, time),
      { dx: 30, velocity: 1, reversed: false },
      `timestamp=${String(time)}`,
    );
    assert.deepEqual(tracker.release(160, 160), { dx: 60, velocity: 1, reversed: false });
  }
});

test('equal timestamps retain displacement without generating infinite velocity', () => {
  const tracker = createSwipeTracker(0, 100);
  tracker.move(20, 100);
  const sample = tracker.release(40, 100);
  assert.deepEqual(sample, { dx: 40, velocity: 0, reversed: false });
  assert.equal(shouldCommitSwipe(sample, 600), false);
});

test('non-positive, non-finite and non-numeric sizes cannot commit', () => {
  const sample = createSwipeTracker(0, 0).release(300, 100);
  assert.equal(shouldCommitSwipe(sample, 600), true);
  for (const size of [0, -1, NaN, Infinity, -Infinity, undefined, null, '600']) {
    assert.equal(shouldCommitSwipe(sample, size), false, `size=${String(size)}`);
  }
});

test('release coordinates contribute to both displacement and velocity', () => {
  for (const direction of [-1, 1]) {
    const tracker = createSwipeTracker(350, 1000);
    tracker.move(350 + direction * 20, 1020);
    const sample = tracker.release(350 + direction * 28, 1040);
    assert.deepEqual(sample, { dx: direction * 28, velocity: direction * 0.7, reversed: false });
    assert.equal(shouldCommitSwipe(sample, 600), true);
  }
});

test('settle duration follows remaining distance within 300–360ms, including rebounds', () => {
  for (const [from, to, size, expected] of [
    [0, 600, 600, 360],
    [120, 600, 600, 348],
    [300, 600, 600, 330],
    [590, 600, 600, 301],
    [-120, -600, 600, 348],
    [40, 0, 600, 304],
    [240, 1200, 1200, 348],
    [0, 1200, 600, 360],
    [600, -600, 600, 360],
  ]) {
    const duration = swipeSettleDuration(from, to, size);
    assert.equal(duration, expected, `from=${from}, to=${to}, size=${size}`);
    assert.ok(duration >= 300 && duration <= 360);
  }
});

test('settle duration is zero for equal endpoints or invalid coordinates', () => {
  for (const position of [-600, 0, 150]) {
    assert.equal(swipeSettleDuration(position, position, 600), 0);
  }
  for (const coordinate of [NaN, Infinity, -Infinity, undefined, null, '40']) {
    assert.equal(swipeSettleDuration(coordinate, 600, 600), 0, `from=${String(coordinate)}`);
    assert.equal(swipeSettleDuration(40, coordinate, 600), 0, `to=${String(coordinate)}`);
  }
});

test('settle duration is zero for invalid sizes', () => {
  for (const size of [0, -1, NaN, Infinity, -Infinity, undefined, null, '600']) {
    assert.equal(swipeSettleDuration(40, 600, size), 0, `size=${String(size)}`);
  }
});
