import test from 'node:test';
import assert from 'node:assert/strict';
import { createPlaybackCache } from '../packages/film-playback/src/cache.js';

const options = (extra = {}) => ({
  ttl: 10_000, completeness: 'complete', dependencies: [{ domain: 'source:a', revision: 1 }], ...extra,
});
const turn = () => new Promise(resolve => setImmediate(resolve));
const deferred = () => Promise.withResolvers();
const rejection = (promise, code) => assert.rejects(promise, { code, message: code });

test('E24/E25: complete identity keys isolate source, account, mode and metadata settings', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const identities = [
    { source: 'a', account: 1, mode: 'play', metadata: 1 },
    { source: 'b', account: 1, mode: 'play', metadata: 1 },
    { source: 'a', account: 2, mode: 'play', metadata: 1 },
    { source: 'a', account: 1, mode: 'download', metadata: 1 },
    { source: 'a', account: 1, mode: 'play', metadata: 2 },
  ];
  for (const [index, identity] of identities.entries()) {
    await cache.load(JSON.stringify(identity), () => ({ index }), options({ ttl: 10_000 }));
  }
  for (const [index, identity] of identities.entries()) {
    assert.deepEqual(cache.read(JSON.stringify(identity)), { index });
  }
  assert.equal(cache.read(JSON.stringify({ ...identities[0], account: 3 })), undefined);
});

test('E25: TTL starts at successful completion and expires at the exact boundary', async t => {
  let now = 10;
  const cache = createPlaybackCache({ now: () => now });
  t.after(() => cache.close());
  const pending = deferred();
  let calls = 0;
  const first = cache.load('detail', () => { calls++; return pending.promise; }, options({ ttl: 100 }));
  await turn();
  now = 80;
  pending.resolve({ episodes: [1, 2] });
  assert.deepEqual(await first, { episodes: [1, 2] });
  now = 179;
  assert.deepEqual(await cache.load('detail', () => assert.fail('unexpired hit'), options({ ttl: 100 })),
    { episodes: [1, 2] });
  now = 180;
  assert.equal(cache.read('detail'), undefined);
  assert.deepEqual(await cache.load('detail', () => { calls++; return { episodes: [1, 2, 3] }; }, options({ ttl: 100 })),
    { episodes: [1, 2, 3] });
  assert.equal(calls, 2);
});

test('E22/E24: same-key consumers share the cache-owned producer; one cancellation only stops its wait', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const result = deferred();
  const consumer = new AbortController();
  let calls = 0, producerSignal;
  const first = cache.load('shared', signal => {
    calls++;
    assert.ok(signal instanceof AbortSignal);
    producerSignal = signal;
    return result.promise;
  }, options({ signal: consumer.signal }));
  const cancelled = rejection(first, 'CANCELLED');
  const second = cache.load('shared', () => assert.fail('duplicate producer'), options());
  await turn();
  assert.notEqual(producerSignal, consumer.signal);
  consumer.abort();
  await cancelled;
  assert.equal(producerSignal.aborted, false);
  result.resolve({ files: ['a', 'b'] });
  assert.deepEqual(await second, { files: ['a', 'b'] });
  assert.equal(calls, 1);
  assert.deepEqual(cache.read('shared'), { files: ['a', 'b'] });
});

test('E22: a finite producer with no remaining consumers still completes into valid cache', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const result = deferred(), consumer = new AbortController();
  let producerSignal;
  const waiting = cache.load('background', signal => {
    producerSignal = signal;
    return result.promise;
  }, options({ signal: consumer.signal }));
  const cancelled = rejection(waiting, 'CANCELLED');
  await turn();
  consumer.abort();
  await cancelled;
  assert.equal(producerSignal.aborted, false);
  result.resolve(['complete', 'list']);
  await turn();
  assert.deepEqual(cache.read('background'), ['complete', 'list']);
});

test('E22/E48: an unobserved producer failure is settled and is never cached as empty success', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const result = deferred(), consumer = new AbortController();
  const waiting = cache.load('background-error', () => result.promise, options({ signal: consumer.signal }));
  const cancelled = rejection(waiting, 'CANCELLED');
  await turn();
  consumer.abort();
  await cancelled;
  result.reject(new Error('synthetic private diagnostic'));
  await turn();
  assert.equal(cache.read('background-error'), undefined);
  assert.deepEqual(await cache.load('background-error', () => ['retry'], options()), ['retry']);
});

test('E22/E24: already cancelled consumers start no producer, including on a cached hit', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  await cache.load('cached', () => [1], options());
  const controller = new AbortController();
  controller.abort();
  for (const key of ['missing', 'cached']) {
    await rejection(cache.load(key, () => assert.fail('cancelled dispatch'),
      options({ signal: controller.signal })), 'CANCELLED');
  }
});

test('E23: dependency invalidation revokes immediately and waits for actual producer settlement', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const oldResult = deferred(), otherResult = deferred();
  let oldSignal, otherSignal;
  const old = cache.load('detail', signal => {
    oldSignal = signal;
    return oldResult.promise;
  }, options());
  const oldRejected = rejection(old, 'CANCELLED');
  const other = cache.load('other', signal => {
    otherSignal = signal;
    return otherResult.promise;
  }, options({ dependencies: [{ domain: 'source:b', revision: 1 }] }));
  await turn();
  let drained = false;
  const invalidated = cache.invalidate(entry =>
    entry.dependencies?.some(item => item.domain === 'source:a' && item.revision === 1))
    .then(() => { drained = true; });
  assert.equal(oldSignal.aborted, true);
  assert.equal(otherSignal.aborted, false);
  await oldRejected;
  await turn();
  assert.equal(drained, false);
  await cache.load('detail', () => ({ generation: 2 }),
    options({ dependencies: [{ domain: 'source:a', revision: 2 }] }));
  oldResult.resolve({ generation: 1 });
  otherResult.resolve({ other: true });
  await invalidated;
  assert.equal(drained, true);
  assert.deepEqual(await other, { other: true });
  assert.deepEqual(cache.read('detail'), { generation: 2 });
});

test('E23/E24: changed dependencies revoke old same-key authority even without an explicit invalidate', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const oldResult = deferred();
  let oldSignal;
  const old = cache.load('reused-key', signal => {
    oldSignal = signal;
    return oldResult.promise;
  }, options());
  const oldRejected = rejection(old, 'CANCELLED');
  await turn();
  assert.deepEqual(await cache.load('reused-key', () => [2],
    options({ dependencies: [{ domain: 'source:a', revision: 2 }] })), [2]);
  await oldRejected;
  assert.equal(oldSignal.aborted, true);
  let closed = false;
  const closing = cache.close().then(() => { closed = true; });
  await turn();
  assert.equal(closed, false);
  oldResult.resolve([1]);
  await closing;
});

test('E23: cached dependency scopes are immutable and selective invalidation preserves other entries', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const dependencies = [{ domain: 'account:a', revision: 1 }];
  await cache.load('a', () => [1], options({ dependencies }));
  await cache.load('b', () => [2], options());
  dependencies[0].revision = 2;
  await cache.invalidate(entry => {
    assert.ok(Object.isFrozen(entry));
    assert.ok(Object.isFrozen(entry.dependencies));
    return entry.dependencies.some(value => value.domain === 'account:a' && value.revision === 1);
  });
  assert.equal(cache.read('a'), undefined);
  assert.deepEqual(cache.read('b'), [2]);
});

test('E23/E48: close cancels waiting consumers but does not claim drain before ignored-abort work settles', async () => {
  const cache = createPlaybackCache();
  const result = deferred();
  let signal;
  const waiting = cache.load('slow', value => { signal = value; return result.promise; }, options());
  const cancelled = rejection(waiting, 'CANCELLED');
  await turn();
  const closing = cache.close();
  assert.equal(cache.close(), closing);
  assert.equal(signal.aborted, true);
  await cancelled;
  let drained = false;
  closing.then(() => { drained = true; });
  await turn();
  assert.equal(drained, false);
  assert.throws(() => cache.read('slow'), { code: 'CLOSED' });
  assert.throws(() => cache.invalidate(), { code: 'CLOSED' });
  await rejection(cache.load('new', () => assert.fail('closed producer'), options()), 'CLOSED');
  result.reject(new Error('synthetic late error'));
  await closing;
  assert.equal(drained, true);
});

test('E23: close or invalidate before producer dispatch prevents dispatch completely', async () => {
  for (const action of ['invalidate', 'close']) {
    const cache = createPlaybackCache();
    const waiting = cache.load('undispatched', () => assert.fail('revoked dispatch'), options());
    const cancelled = rejection(waiting, 'CANCELLED');
    await cache[action]();
    await cancelled;
    await cache.close();
  }
});

test('E24/E25: partial can be read but cannot satisfy or overwrite a complete detail', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  await cache.load('detail', () => ({ title: 'partial' }), options({ completeness: 'partial' }));
  assert.deepEqual(cache.read('detail'), { title: 'partial' });
  assert.equal(cache.read('detail', { minimumCompleteness: 'complete' }), undefined);
  await cache.load('detail', () => ({ title: 'complete', seasons: [1, 2] }), options());
  assert.deepEqual(await cache.load('detail', () => assert.fail('partial downgrade'),
    options({ completeness: 'partial' })), { title: 'complete', seasons: [1, 2] });
  assert.deepEqual(cache.read('detail', { minimumCompleteness: 'complete' }),
    { title: 'complete', seasons: [1, 2] });
});

test('E24/E25: complete demand queues behind same-key partial, then produces actual complete content once', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const partialResult = deferred(), completeResult = deferred();
  let completeCalls = 0;
  const partial = cache.load('detail', () => partialResult.promise, options({ completeness: 'partial' }));
  const complete = cache.load('detail', () => {
    completeCalls++;
    return completeResult.promise;
  }, options());
  const duplicate = cache.load('detail', () => assert.fail('duplicate complete request'), options());
  await turn();
  assert.equal(completeCalls, 0);
  partialResult.resolve({ partial: true });
  assert.deepEqual(await partial, { partial: true });
  await turn();
  assert.equal(completeCalls, 1);
  assert.equal(cache.read('detail', { minimumCompleteness: 'complete' }), undefined);
  completeResult.resolve({ complete: true, episodes: [1, 2, 3] });
  assert.deepEqual(await complete, { complete: true, episodes: [1, 2, 3] });
  assert.deepEqual(await duplicate, await complete);
});

test('E24/E25: failed partial work does not turn a queued complete request into failure or empty success', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const result = deferred();
  const partial = cache.load('detail', () => result.promise, options({ completeness: 'partial' }));
  const failed = rejection(partial, 'UPSTREAM_FORMAT');
  const complete = cache.load('detail', () => ({ complete: true }), options());
  await turn();
  result.reject(Object.assign(new Error('synthetic'), { code: 'UPSTREAM_FORMAT' }));
  await failed;
  assert.deepEqual(await complete, { complete: true });
});

test('E23/E25: invalidate also revokes a queued completeness upgrade, with no late downstream dispatch', async () => {
  const cache = createPlaybackCache();
  const result = deferred();
  const partial = cache.load('detail', () => result.promise, options({ completeness: 'partial' }));
  const complete = cache.load('detail', () => assert.fail('revoked upgrade producer'), options());
  const rejected = [rejection(partial, 'CANCELLED'), rejection(complete, 'CANCELLED')];
  await turn();
  let drained = false;
  const invalidated = cache.invalidate().then(() => { drained = true; });
  await Promise.all(rejected);
  await turn();
  assert.equal(drained, false);
  result.resolve({ partial: true });
  await invalidated;
  assert.equal(cache.read('detail'), undefined);
  await cache.close();
});

test('E25/E48: failures retain safe codes, redact diagnostics, and allow retry; empty lists remain valid data', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const secret = 'https://synthetic.invalid/play?signature=private-fixture';
  for (const input of [
    new Error(secret),
    Object.assign(new Error(secret), { code: 'ARBITRARY_UPSTREAM_SECRET' }),
    Object.assign(new Error(secret), { code: 'TIMEOUT' }),
    Object.assign(new Error(secret), { name: 'AbortError' }),
  ]) {
    const expected = input.name === 'AbortError' ? 'CANCELLED' :
      input.code === 'TIMEOUT' ? 'TIMEOUT' : 'CACHE_LOAD_FAILED';
    await assert.rejects(cache.load('failure', () => { throw input; }, options()), error => {
      assert.equal(error.code, expected);
      assert.equal(error.message, expected);
      assert.equal(error.cause, undefined);
      assert.equal(error.stack.includes(secret), false);
      return true;
    });
    assert.equal(cache.read('failure'), undefined);
  }
  assert.deepEqual(await cache.load('failure', () => [], options()), []);
  assert.deepEqual(cache.read('failure'), []);
});

test('E25: absent or uncacheable producer results fail rather than becoming a cached empty response', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const cyclic = {};
  cyclic.value = cyclic;
  for (const value of [
    undefined, NaN, Infinity, () => {}, new Error('synthetic'), new Date(), new Map(),
    Buffer.from([1]), Object.create(Array.prototype), cyclic,
  ]) {
    await rejection(cache.load('bad-result', () => value, options()), 'INVALID_RESULT');
    assert.equal(cache.read('bad-result'), undefined);
  }
});

test('E48: cached provider work preserves actionable closed errors without caching a failure or private diagnostic', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  for (const code of ['WEB_INTERACTION_REQUIRED', 'MEDIA_UNAVAILABLE', 'SOURCE_FAILURE', 'CAT_FAILURE',
    'SCRIPT_FAILURE', 'SCRIPT_PROTOCOL_ERROR', 'STALE_METADATA']) {
    await assert.rejects(cache.load(code, () => {
      throw Object.assign(new Error('private upstream diagnostic'), { code });
    }, options()), error => error.code === code && error.message === code &&
      !error.stack.includes('private upstream diagnostic'));
    assert.equal(cache.read(code), undefined);
  }
});

test('E26: large business pages are delivered whole even when retention budget cannot hold them', async t => {
  const cache = createPlaybackCache({ maximumBytes: 1000 });
  t.after(() => cache.close());
  await cache.load('small', () => [1], options({ ttl: 10_000 }));
  const page = Array.from({ length: 1200 }, (_, index) => ({ id: index, name: `episode ${index}` }));
  const actual = await cache.load('large', () => page, options());
  assert.equal(actual.length, 1200);
  assert.deepEqual(actual, page);
  assert.equal(cache.read('large'), undefined);
  assert.deepEqual(cache.read('small'), [1]);
});

test('E26: LRU bounds retained cross-query growth and evicts whole values', async t => {
  const cache = createPlaybackCache({ maximumBytes: 1000 });
  t.after(() => cache.close());
  const value = 'x'.repeat(40);
  const settings = options({ dependencies: [], ttl: 10_000 });
  await cache.load('a', () => value, settings);
  await cache.load('b', () => value, settings);
  assert.equal(cache.read('a'), value);
  await cache.load('c', () => value, settings);
  assert.equal(cache.read('b'), undefined);
  assert.equal(cache.read('a'), value);
  assert.equal(cache.read('c'), value);
  for (let index = 0; index < 100; index++) {
    await cache.load(`query:${index}`, () => value, settings);
  }
  const retained = Array.from({ length: 100 }, (_, index) => cache.read(`query:${index}`))
    .filter(value => value !== undefined);
  assert.ok(retained.length > 0 && retained.length <= 2);
});

test('E24/E26: dependencies and key overhead count toward capacity, not only response bytes', async t => {
  const cache = createPlaybackCache({ maximumBytes: 1000 });
  t.after(() => cache.close());
  assert.deepEqual(await cache.load('x'.repeat(500), () => [1], options()), [1]);
  assert.equal(cache.read('x'.repeat(500)), undefined);
  assert.deepEqual(await cache.load('large-dependencies', () => [2],
    options({ dependencies: [{ domain: 'x'.repeat(500), revision: 1 }] })), [2]);
  assert.equal(cache.read('large-dependencies'), undefined);
});

test('E24/E25: consumers cannot mutate retained values or producer-owned input after completion', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const input = { files: [{ id: 'a' }] };
  const actual = await cache.load('immutable', () => input, options());
  input.files[0].id = 'changed';
  assert.throws(() => { actual.files[0].id = 'mutated'; }, TypeError);
  assert.deepEqual(cache.read('immutable'), { files: [{ id: 'a' }] });
});

test('E24/E25: zero TTL still merges active work and then retains no completed entry', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const result = deferred();
  const first = cache.load('no-retention', () => result.promise, options({ ttl: 0 }));
  const second = cache.load('no-retention', () => assert.fail('duplicate zero-TTL work'), options({ ttl: 0 }));
  result.resolve([1, 2]);
  assert.deepEqual(await first, [1, 2]);
  assert.deepEqual(await second, [1, 2]);
  assert.equal(cache.read('no-retention'), undefined);
});

test('E23/E48: invalid options or a throwing invalidation predicate cannot mutate valid cache state', async t => {
  assert.throws(() => createPlaybackCache({ maximumBytes: 0 }), { code: 'INVALID_REQUEST' });
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  await cache.load('valid', () => [1], options());
  for (const invalid of [
    options({ ttl: -1 }), options({ ttl: Infinity }), options({ completeness: 'unknown' }),
    options({ dependencies: null }), options({ signal: {} }),
  ]) {
    await rejection(cache.load('invalid', () => assert.fail('invalid dispatch'), invalid), 'INVALID_REQUEST');
  }
  assert.throws(() => cache.invalidate(() => { throw new Error('synthetic'); }), { code: 'INVALID_REQUEST' });
  assert.deepEqual(cache.read('valid'), [1]);
});

test('E25: value-based metadata TTL receives completed data and a numeric millisecond clock', async t => {
  const day = 86_400_000;
  let now = Date.UTC(2026, 10, 8);
  const cache = createPlaybackCache({ now: () => now });
  t.after(() => cache.close());
  let calls = 0;
  const metadataTTL = (value, completedAt) => {
    calls++;
    assert.equal(typeof completedAt, 'number');
    assert.equal(completedAt, now);
    if (value.kind === 'movie' || value.status === 'Ended') return 14 * day;
    return value.nextAirAt === undefined ? day : Math.min(day, Math.max(0, value.nextAirAt - completedAt));
  };
  const startedAt = now;
  await cache.load('movie', () => ({ kind: 'movie' }), options({ ttl: metadataTTL }));
  await cache.load('ended', () => ({ kind: 'tv', status: 'Ended' }), options({ ttl: metadataTTL }));
  await cache.load('airing', () => ({ kind: 'tv', nextAirAt: now + 1000 }), options({ ttl: metadataTTL }));
  await cache.load('airing-unknown', () => ({ kind: 'tv' }), options({ ttl: metadataTTL }));
  assert.equal(calls, 4);
  now = startedAt + 1000;
  assert.equal(cache.read('airing'), undefined);
  assert.deepEqual(cache.read('airing-unknown'), { kind: 'tv' });
  now = startedAt + day;
  assert.equal(cache.read('airing-unknown'), undefined);
  assert.deepEqual(cache.read('movie'), { kind: 'movie' });
  assert.deepEqual(cache.read('ended'), { kind: 'tv', status: 'Ended' });
  now = startedAt + 14 * day;
  assert.equal(cache.read('movie'), undefined);
  assert.equal(cache.read('ended'), undefined);
  assert.equal(calls, 4);
});

test('E25: TTL callback uses producer completion time and rejects invalid durations without caching', async t => {
  let now = 1000;
  const cache = createPlaybackCache({ now: () => now });
  t.after(() => cache.close());
  const result = deferred();
  const waiting = cache.load('dynamic', () => result.promise, options({
    ttl: (value, completedAt) => {
      assert.deepEqual(value, { lifetime: 50 });
      assert.equal(completedAt, 2000);
      assert.ok(Object.isFrozen(value));
      return value.lifetime;
    },
  }));
  await turn();
  now = 2000;
  result.resolve({ lifetime: 50 });
  await waiting;
  now = 2049;
  assert.deepEqual(cache.read('dynamic'), { lifetime: 50 });
  now = 2050;
  assert.equal(cache.read('dynamic'), undefined);
  for (const duration of [-1, Infinity, NaN, '100', Promise.resolve(100)]) {
    await rejection(cache.load('invalid-ttl', () => [1], options({ ttl: () => duration })), 'INVALID_REQUEST');
    assert.equal(cache.read('invalid-ttl'), undefined);
  }
  await rejection(cache.load('throwing-ttl', () => [1], options({
    ttl: () => { throw new Error('synthetic private TTL diagnostic'); },
  })), 'CACHE_LOAD_FAILED');
  assert.equal(cache.read('throwing-ttl'), undefined);
});

test('E25/E48: asynchronous TTL callbacks fail closed without unhandled rejection or retained data', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const ttl = async () => { throw new Error('synthetic private TTL diagnostic'); };
  await rejection(cache.load('async-ttl', () => [1], options({ ttl })), 'INVALID_REQUEST');
  assert.throws(() => cache.seed('async-seed-ttl', [2], options({ ttl })), { code: 'INVALID_REQUEST' });
  await turn();
  assert.equal(cache.read('async-ttl'), undefined);
  assert.equal(cache.read('async-seed-ttl'), undefined);
});

test('E24/E25/E49: seed reuses handed-off complete data without dispatch and keeps its original absolute expiry', async t => {
  let now = 1000;
  const cache = createPlaybackCache({ now: () => now });
  t.after(() => cache.close());
  const handedOff = { title: 'complete', seasons: [1, 2] };
  assert.deepEqual(cache.seed('metadata', handedOff, options({ expiresAt: 1500 })), handedOff);
  handedOff.seasons.push(3);
  assert.deepEqual(await cache.load('metadata', () => assert.fail('handed-off data was requested again'), options()),
    { title: 'complete', seasons: [1, 2] });
  now = 1499;
  assert.deepEqual(cache.read('metadata', { minimumCompleteness: 'complete' }),
    { title: 'complete', seasons: [1, 2] });
  now = 1500;
  assert.equal(cache.read('metadata'), undefined);
});

test('E24/E25/E49: seed never promotes partial content or downgrades complete/fresher entries', async t => {
  const cache = createPlaybackCache({ now: () => 1000 });
  t.after(() => cache.close());
  cache.seed('metadata', { title: 'partial' }, options({ completeness: 'partial', expiresAt: 2000 }));
  assert.equal(cache.read('metadata', { minimumCompleteness: 'complete' }), undefined);
  await cache.load('metadata', () => ({ title: 'complete' }), options({ ttl: 5000 }));
  assert.deepEqual(cache.seed('metadata', { title: 'new partial' },
    options({ completeness: 'partial', expiresAt: 10_000 })), { title: 'complete' });
  assert.deepEqual(cache.seed('metadata', { title: 'older complete' },
    options({ expiresAt: 2000 })), { title: 'complete' });
  assert.deepEqual(cache.seed('metadata', { title: 'expired complete' },
    options({ expiresAt: 999 })), { title: 'complete' });
  assert.deepEqual(cache.read('metadata', { minimumCompleteness: 'complete' }), { title: 'complete' });
});

test('E25/E49: seed supports a numeric or value-dependent TTL and does not retain expired data', async t => {
  let now = 1000;
  const cache = createPlaybackCache({ now: () => now });
  t.after(() => cache.close());
  assert.deepEqual(cache.seed('by-value', { lifetime: 50 }, options({
    ttl: (value, at) => {
      assert.equal(at, now);
      return value.lifetime;
    },
  })), { lifetime: 50 });
  cache.seed('numeric', [1], options({ ttl: 100 }));
  assert.equal(cache.seed('expired', [2], options({ expiresAt: now })), undefined);
  assert.equal(cache.read('expired'), undefined);
  now = 1050;
  assert.equal(cache.read('by-value'), undefined);
  assert.deepEqual(cache.read('numeric'), [1]);
  now = 1100;
  assert.equal(cache.read('numeric'), undefined);
});

test('E21/E22/E25/E49: a newer seed wins over an already running producer without aborting its finite work', async t => {
  const cache = createPlaybackCache({ now: () => 1000 });
  t.after(() => cache.close());
  const result = deferred();
  let signal;
  const old = cache.load('metadata', value => { signal = value; return result.promise; }, options());
  await turn();
  cache.seed('metadata', { version: 2 }, options({ expiresAt: 2000 }));
  assert.equal(signal.aborted, false);
  assert.deepEqual(await cache.load('metadata', () => assert.fail('seeded hit'), options()), { version: 2 });
  result.resolve({ version: 1 });
  assert.deepEqual(await old, { version: 2 });
  assert.deepEqual(cache.read('metadata'), { version: 2 });
});

test('E21/E25/E49: seed can satisfy not-yet-dispatched work and queued completeness upgrades without a new request', async t => {
  const cache = createPlaybackCache({ now: () => 1000 });
  t.after(() => cache.close());
  const pending = cache.load('immediate', () => assert.fail('seed available before dispatch'), options());
  cache.seed('immediate', [1], options({ expiresAt: 2000 }));
  assert.deepEqual(await pending, [1]);

  const partialResult = deferred();
  const partial = cache.load('upgrade', () => partialResult.promise, options({ completeness: 'partial' }));
  const complete = cache.load('upgrade', () => assert.fail('seed fulfilled queued complete demand'), options());
  await turn();
  cache.seed('upgrade', { complete: true }, options({ expiresAt: 2000 }));
  partialResult.resolve({ partial: true });
  assert.deepEqual(await partial, { complete: true });
  assert.deepEqual(await complete, { complete: true });
});

test('E21/E26/E49: eviction of a seed does not let an older producer repopulate it', async t => {
  const cache = createPlaybackCache({ now: () => 1000, maximumBytes: 1000 });
  t.after(() => cache.close());
  const result = deferred(), settings = options({ ttl: 10_000, dependencies: [] });
  const old = cache.load('old', () => result.promise, settings);
  await turn();
  cache.seed('old', 'n'.repeat(40), { ...settings, expiresAt: 2000 });
  await cache.load('new:a', () => 'a'.repeat(40), settings);
  await cache.load('new:b', () => 'b'.repeat(40), settings);
  assert.equal(cache.read('old'), undefined);
  result.resolve('o'.repeat(40));
  assert.equal(await old, 'o'.repeat(40));
  assert.equal(cache.read('old'), undefined);
});

test('E24/E26/E49: oversized seeds return the complete result without bypassing the retention budget', async t => {
  const cache = createPlaybackCache({ maximumBytes: 1000 });
  t.after(() => cache.close());
  const list = Array.from({ length: 1000 }, (_, index) => index);
  assert.deepEqual(cache.seed('large', list, options()), list);
  assert.equal(cache.read('large'), undefined);
  assert.equal(list.length, 1000);
});

test('E23/E49: expired handoff cannot revoke a current dependency scope, and seeds still obey invalidate/close', async () => {
  const cache = createPlaybackCache({ now: () => 1000 });
  const dependencies = [{ domain: 'source:a', revision: 2 }];
  cache.seed('detail', [2], options({ dependencies, expiresAt: 2000 }));
  assert.equal(cache.seed('detail', [1], options({ expiresAt: 999 })), undefined);
  assert.deepEqual(cache.read('detail'), [2]);
  await cache.invalidate(entry => entry.dependencies.some(item => item.revision === 2));
  assert.equal(cache.read('detail'), undefined);
  await cache.close();
  assert.throws(() => cache.seed('detail', [2], options()), { code: 'CLOSED' });
});

test('E24/E48: JSON optional undefined object fields are omitted, but undefined array slots and holes fail', async t => {
  const cache = createPlaybackCache();
  t.after(() => cache.close());
  const value = { title: 'complete', optional: undefined, nested: { absent: undefined, value: null } };
  assert.deepEqual(await cache.load('optional', () => value, options()),
    { title: 'complete', nested: { value: null } });
  assert.deepEqual(cache.seed('seed-optional', value, options()),
    { title: 'complete', nested: { value: null } });
  const disguisedHole = [, 'last'];
  disguisedHole.extra = 'not an array slot';
  for (const invalid of [[undefined], [, 'last'], Array(2), disguisedHole]) {
    await rejection(cache.load('bad-array', () => invalid, options()), 'INVALID_RESULT');
    assert.throws(() => cache.seed('bad-array', invalid, options()), { code: 'INVALID_RESULT' });
    assert.equal(cache.read('bad-array'), undefined);
  }
});

test('F4 retained stale success remains available after expiry and failed refresh without changing its data', async t => {
  let now = 1000;
  const cache = createPlaybackCache({ now: () => now });
  t.after(() => cache.close());
  await cache.load('metadata', () => ({ title: 'old', fetchedAt: now }),
    options({ retainStale: true, ttl: 100 }));
  now = 1100;
  assert.equal(cache.read('metadata'), undefined);
  assert.deepEqual(cache.read('metadata', { allowStale: true }), { title: 'old', fetchedAt: 1000 });
  await rejection(cache.load('metadata', () => { throw Object.assign(new Error('synthetic'), { code: 'TIMEOUT' }); },
    options({ retainStale: true, ttl: 100, refresh: true })), 'TIMEOUT');
  assert.deepEqual(cache.read('metadata', { allowStale: true }), { title: 'old', fetchedAt: 1000 });
});

test('F5 revoked/closed work has no publication authority even when its producer ignores cancellation', async () => {
  for (const operation of ['invalidate', 'close']) {
    const cache = createPlaybackCache(), gate = deferred();
    const published = [];
    const work = cache.load('metadata', () => gate.promise, options({ publish: value => published.push(value) }));
    const cancelled = rejection(work, 'CANCELLED');
    await turn();
    let drained = false;
    const draining = cache[operation]().then(() => { drained = true; });
    await cancelled;
    assert.equal(drained, false);
    gate.resolve({ title: 'late' });
    await draining;
    assert.deepEqual(published, []);
    await cache.close();
  }
});

test('F4/F5 forced refresh merges current work and publishes before returning even when memory cannot retain it', async t => {
  const cache = createPlaybackCache({ maximumBytes: 1000 });
  t.after(() => cache.close());
  const gate = deferred(), published = [];
  await cache.load('metadata', () => [1], options());
  const settings = options({ refresh: true, publish: value => published.push(value) });
  const first = cache.load('metadata', () => gate.promise, settings);
  const second = cache.load('metadata', () => assert.fail('duplicate refresh'), settings);
  const value = Array.from({ length: 2000 }, (_, index) => index);
  gate.resolve(value);
  assert.deepEqual(await first, value);
  assert.deepEqual(await second, value);
  assert.deepEqual(published, [value]);
});

test('publication observes the already updated memory entry for load and seed', async () => {
  const cache = createPlaybackCache();
  const first = { value: 'loaded' }, second = { value: 'seeded' };
  await cache.load('ordered-publication', () => first, options({
    publish: value => assert.deepEqual(cache.read('ordered-publication'), value),
  }));
  cache.seed('ordered-publication', second, options({ replace: true,
    publish: value => assert.deepEqual(cache.read('ordered-publication'), value),
  }));
  await cache.close();
});
