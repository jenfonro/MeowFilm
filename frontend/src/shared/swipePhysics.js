// Ported from songloft-plugin-iwebplus static/swipe-physics.js ("track" policy).
// Apache-2.0; attribution and license: public/licenses/swipe-physics.txt.
// A short flick commits on its recent velocity; a slow or held drag commits only
// once it has travelled past a fraction of the page. Velocity is measured over
// the last 100ms only, so a finger that stops before lifting loses its fling.
const WINDOW_MS = 100;
const TURN_SLOP = 8;
const COMMIT = { distance: 0.3, velocity: 0.45, minimum: 28 };

export const createSwipeTracker = (start, startedAt) => {
  let points = [{ x: start, t: startedAt }];
  let last = points[0];
  let direction = 0;
  let turn = null;
  let movedAt = startedAt;
  const move = (x, time) => {
    if (!Number.isFinite(x) || !Number.isFinite(time) || time < last.t) return;
    const next = { x, t: time };
    const delta = x - last.x;
    if (Math.abs(delta) >= 0.5) {
      movedAt = time;
      const sign = Math.sign(delta);
      if (!direction) direction = sign;
      if (sign !== direction) {
        // Small reversals are jitter; a deliberate turn starts a new velocity leg.
        turn = turn || last;
        if (Math.abs(x - turn.x) >= TURN_SLOP) {
          direction = sign;
          points = [turn];
          turn = null;
        }
      } else {
        turn = null;
      }
    }
    points.push(next);
    last = next;
    // Keep the sample just before the window for interpolation.
    while (points.length > 2 && points[1].t <= time - WINDOW_MS) points.shift();
    while (points.length > 32) points.shift();
  };
  const release = (x, time) => {
    move(x, time);
    const end = last;
    const cutoff = end.t - WINDOW_MS;
    let first = points[0];
    if (first.t < cutoff && points.length > 1) {
      const next = points[1];
      const span = next.t - first.t;
      first = { t: cutoff, x: span ? first.x + (next.x - first.x) * (cutoff - first.t) / span : next.x };
    }
    const elapsed = end.t - first.t;
    const dx = end.x - start;
    const held = end.t - movedAt >= WINDOW_MS;
    const velocity = held || elapsed < 16 ? 0 : Math.max(-3, Math.min(3, (end.x - first.x) / elapsed));
    return { dx, velocity, reversed: !held && direction !== 0 && direction !== Math.sign(dx) };
  };
  return { move, release };
};

export const shouldCommitSwipe = (sample, size) => {
  const distance = Math.abs(sample && sample.dx);
  if (!Number.isFinite(size) || size <= 0 || !Number.isFinite(distance) || sample.reversed) return false;
  return distance > size * COMMIT.distance
    || (distance >= COMMIT.minimum && sample.velocity * Math.sign(sample.dx) > COMMIT.velocity);
};

// Velocity chooses the page, not how briefly it travels: a quick short flick
// still has most of a page left, so it (and a rebound) gets a full interval.
export const swipeSettleDuration = (from, to, size) => {
  if (![from, to, size].every(Number.isFinite) || size <= 0 || from === to) return 0;
  return 300 + 60 * Math.min(1, Math.abs(to - from) / size);
};
