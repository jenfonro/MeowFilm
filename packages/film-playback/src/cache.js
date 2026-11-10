import { isDeepStrictEqual } from 'node:util';

const defaultMaximumBytes = 32 * 1024 * 1024;
const completenessRank = { partial: 0, complete: 1 };
const codes = new Set([
  'INVALID_REQUEST', 'INVALID_RESULT', 'CACHE_LOAD_FAILED', 'CANCELLED', 'CLOSED',
  'UPSTREAM_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_FORMAT', 'NETWORK_ERROR', 'TIMEOUT',
  'AUTH_REQUIRED', 'AUTH_FAILED', 'LOGIN_EXPIRED', 'NOT_FOUND', 'STALE_ACCOUNT',
  'STALE_SOURCE', 'CAPABILITY_UNAVAILABLE', 'NOT_CONFIGURED', 'CHALLENGE_REQUIRED',
  'UPSTREAM_REDIRECT', 'PAN_FAILURE', 'UNSUPPORTED_OPERATION', 'STORAGE_ERROR', 'CORRUPT_STORAGE',
  'WEB_INTERACTION_REQUIRED', 'MEDIA_UNAVAILABLE', 'SOURCE_FAILURE', 'CAT_FAILURE',
  'SCRIPT_FAILURE', 'SCRIPT_PROTOCOL_ERROR', 'STALE_METADATA',
]);

function failure(code) {
  const error = new Error(codes.has(code) ? code : 'CACHE_LOAD_FAILED');
  error.name = 'PlaybackCacheError';
  error.code = error.message;
  return error;
}

function requireValue(condition) {
  if (!condition) throw failure('INVALID_REQUEST');
}

// Owned, immutable JSON data. The accounting includes UTF-16 strings, property
// names, slots and conservative object/entry overhead, not just wire JSON bytes.
function snapshot(value, ancestors = new Set()) {
  if (value === null) return { value, bytes: 8 };
  if (typeof value === 'string') return { value, bytes: 24 + value.length * 2 };
  if (typeof value === 'boolean') return { value, bytes: 8 };
  if (typeof value === 'number' && Number.isFinite(value)) return { value, bytes: 8 };
  if (!value || typeof value !== 'object' || ancestors.has(value)) {
    throw failure('INVALID_RESULT');
  }
  const array = Array.isArray(value);
  const prototype = Object.getPrototypeOf(value);
  if (array ? prototype !== Array.prototype : prototype !== Object.prototype && prototype !== null) {
    throw failure('INVALID_RESULT');
  }
  const keys = Object.keys(value);
  if (array && (keys.length !== value.length || keys.some((key, index) => key !== String(index)))) {
    throw failure('INVALID_RESULT');
  }
  ancestors.add(value);
  const result = array ? [] : {};
  let bytes = 64;
  for (const key of keys) {
    if (!array && value[key] === undefined) continue;
    const child = snapshot(value[key], ancestors);
    Object.defineProperty(result, key, {
      value: child.value, enumerable: true, configurable: false, writable: false,
    });
    bytes += 32 + key.length * 2 + child.bytes;
  }
  ancestors.delete(value);
  return { value: Object.freeze(result), bytes };
}

function normalizedError(error) {
  if (error?.name === 'AbortError') return failure('CANCELLED');
  return failure(error?.code);
}

function waitFor(flight, signal) {
  if (signal?.aborted || flight.controller.signal.aborted) {
    return Promise.reject(failure('CANCELLED'));
  }
  return new Promise((resolve, reject) => {
    const cancelled = () => finish(reject, failure('CANCELLED'));
    const finish = (settle, value) => {
      signal?.removeEventListener('abort', cancelled);
      flight.controller.signal.removeEventListener('abort', cancelled);
      settle(value);
    };
    signal?.addEventListener('abort', cancelled, { once: true });
    flight.controller.signal.addEventListener('abort', cancelled, { once: true });
    flight.done.then(value => finish(resolve, value), error => finish(reject, error));
  });
}

/**
 * Keys are caller-built strings containing the complete identity and mode.
 * Values and dependencies are owned JSON snapshots; results are immutable.
 * A producer is called as producer(signal), with a cache-owned AbortSignal.
 */
export function createPlaybackCache({ now = Date.now, maximumBytes = defaultMaximumBytes } = {}) {
  requireValue(typeof now === 'function' && Number.isSafeInteger(maximumBytes) && maximumBytes > 0);
  const entries = new Map();
  const flights = new Map();
  const pending = new Set();
  let retainedBytes = 0;
  let closed = false;
  let closing;

  function time() {
    const value = now();
    requireValue(Number.isFinite(value));
    return value;
  }

  function open() {
    if (closed) throw failure('CLOSED');
  }

  function keyValue(key) {
    requireValue(typeof key === 'string' && key.length > 0);
  }

  function completenessValue(value) {
    requireValue(Object.hasOwn(completenessRank, value));
  }

  function dependenciesValue(value) {
    try {
      requireValue(value !== null && typeof value === 'object');
      return snapshot(value);
    } catch { throw failure('INVALID_REQUEST'); }
  }

  function ttlValue(ttl) {
    requireValue(typeof ttl === 'function' || Number.isFinite(ttl) && ttl >= 0);
  }

  function expiration(ttl, value, at) {
    const duration = typeof ttl === 'function' ? ttl(value, at) : ttl;
    if (!Number.isFinite(duration)) {
      if (duration && typeof duration.then === 'function') Promise.resolve(duration).catch(() => {});
      throw failure('INVALID_REQUEST');
    }
    requireValue(duration >= 0 && Number.isFinite(at + duration));
    return at + duration;
  }

  function scope(key, dependencies) {
    const existing = entries.get(key);
    if (existing && !isDeepStrictEqual(existing.dependencies, dependencies)) remove(key);
    const current = flights.get(key);
    if (current && !isDeepStrictEqual(current.info.dependencies, dependencies)) {
      for (const flight of pending) {
        if (flight.key === key) revoke(flight);
      }
    }
  }

  function remove(key) {
    const entry = entries.get(key);
    if (!entry) return;
    retainedBytes -= entry.bytes;
    entries.delete(key);
  }

  function find(key, minimumCompleteness, at = time()) {
    const entry = entries.get(key);
    if (!entry) return undefined;
    if (entry.expiresAt <= at) {
      remove(key);
      return undefined;
    }
    if (completenessRank[entry.completeness] < completenessRank[minimumCompleteness]) return undefined;
    entries.delete(key);
    entries.set(key, entry);
    return entry;
  }

  function retain(flight, result, ttl) {
    if (!flight.authorized || closed) {
      throw failure('CANCELLED');
    }
    const at = time();
    const previous = entries.get(flight.key);
    if (previous && previous.expiresAt > at &&
        (flight.seeded && completenessRank[previous.completeness] >= completenessRank[flight.completeness] ||
         completenessRank[previous.completeness] > completenessRank[flight.completeness])) {
      return previous.value;
    }
    // A seed can be evicted for capacity while its earlier producer is still
    // draining. That producer must not repopulate the cache with the old value.
    if (flight.seeded) return result.value;
    const expiresAt = expiration(ttl, result.value, at);
    const bytes = 256 + flight.key.length * 2 + flight.dependencyBytes + result.bytes;
    // A large result is delivered whole; it simply does not displace retained data.
    if (expiresAt === at || bytes > maximumBytes) return result.value;
    remove(flight.key);
    while (retainedBytes + bytes > maximumBytes) remove(entries.keys().next().value);
    entries.set(flight.key, { ...flight.info, value: result.value, expiresAt, bytes });
    retainedBytes += bytes;
    return result.value;
  }

  function revoke(flight) {
    flight.authorized = false;
    if (flights.get(flight.key) === flight) flights.delete(flight.key);
    flight.controller.abort();
  }

  function read(key, { minimumCompleteness = 'partial' } = {}) {
    open();
    keyValue(key);
    completenessValue(minimumCompleteness);
    return find(key, minimumCompleteness)?.value;
  }

  async function load(key, producer, {
    ttl, completeness, dependencies = [], signal,
  } = {}) {
    open();
    keyValue(key);
    completenessValue(completeness);
    ttlValue(ttl);
    requireValue(typeof producer === 'function' &&
      (signal === undefined || signal instanceof AbortSignal));
    if (signal?.aborted) throw failure('CANCELLED');
    const dependency = dependenciesValue(dependencies);

    scope(key, dependency.value);
    const cached = find(key, completeness);
    if (cached) return cached.value;
    const active = flights.get(key);
    if (active && completenessRank[active.completeness] >= completenessRank[completeness]) {
      return waitFor(active, signal);
    }

    const flight = {
      key, completeness, dependencyBytes: dependency.bytes, controller: new AbortController(),
      authorized: true, info: Object.freeze({ key, completeness, dependencies: dependency.value }),
    };
    flights.set(key, flight);
    pending.add(flight);
    flight.done = Promise.resolve().then(async () => {
      // A complete request waits for partial work, then runs its own producer.
      // The queued producer is already tracked, so invalidate/close can revoke
      // it before it starts; partial work can never be promoted to complete.
      if (active) {
        try { await active.done; } catch {}
      }
      if (!flight.authorized) throw failure('CANCELLED');
      if (flight.seeded) {
        const handedOff = find(key, completeness);
        if (handedOff && isDeepStrictEqual(handedOff.dependencies, flight.info.dependencies)) return handedOff.value;
      }
      return producer(flight.controller.signal);
    }).then(value => {
      if (!flight.authorized || closed) throw failure('CANCELLED');
      const result = snapshot(value);
      return retain(flight, result, ttl);
    }).catch(error => { throw normalizedError(error); }).finally(() => {
      if (flights.get(key) === flight) flights.delete(key);
      pending.delete(flight);
    });
    // Producers continue when all consumers have left. Their failure is still
    // observed, without making a rejected background promise unhandled.
    flight.done.catch(() => {});
    return waitFor(flight, signal);
  }

  function seed(key, value, { ttl, expiresAt, completeness, dependencies = [] } = {}) {
    open();
    keyValue(key);
    completenessValue(completeness);
    if (expiresAt === undefined) ttlValue(ttl);
    else requireValue(Number.isFinite(expiresAt));
    const dependency = dependenciesValue(dependencies);
    let result, until;
    const at = time();
    try {
      result = snapshot(value);
      until = expiresAt ?? expiration(ttl, result.value, at);
    } catch (error) { throw normalizedError(error); }
    // An expired handoff has no publication or revocation authority.
    if (until <= at) {
      const current = find(key, 'partial', at);
      return current && isDeepStrictEqual(current.dependencies, dependency.value) ? current.value : undefined;
    }
    scope(key, dependency.value);
    const previous = find(key, 'partial', at);
    if (previous && (completenessRank[previous.completeness] > completenessRank[completeness] ||
        previous.completeness === completeness && previous.expiresAt >= until)) return previous.value;
    for (const flight of pending) {
      if (flight.key === key && flight.authorized &&
          completenessRank[completeness] >= completenessRank[flight.completeness]) flight.seeded = true;
    }
    const bytes = 256 + key.length * 2 + dependency.bytes + result.bytes;
    if (bytes > maximumBytes) {
      // An accepted but unretained complete result also displaces an older
      // partial entry, so read cannot later pretend that partial is the winner.
      remove(key);
      return result.value;
    }
    remove(key);
    while (retainedBytes + bytes > maximumBytes) remove(entries.keys().next().value);
    entries.set(key, { key, completeness, dependencies: dependency.value,
      value: result.value, expiresAt: until, bytes });
    retainedBytes += bytes;
    return result.value;
  }

  function invalidate(predicate = () => true) {
    open();
    requireValue(typeof predicate === 'function');
    // Select everything before mutating, so a throwing predicate cannot
    // partially invalidate an otherwise valid authorization scope.
    let selectedEntries, selectedFlights;
    try {
      selectedEntries = [...entries.values()].filter(entry => predicate(Object.freeze({
        key: entry.key, completeness: entry.completeness, dependencies: entry.dependencies,
      })));
      selectedFlights = [...pending].filter(flight => predicate(flight.info));
    } catch { throw failure('INVALID_REQUEST'); }
    for (const entry of selectedEntries) remove(entry.key);
    for (const flight of selectedFlights) revoke(flight);
    return Promise.allSettled(selectedFlights.map(flight => flight.done)).then(() => undefined);
  }

  function close() {
    if (closing) return closing;
    closed = true;
    entries.clear();
    retainedBytes = 0;
    const remaining = [...pending];
    for (const flight of remaining) revoke(flight);
    closing = Promise.allSettled(remaining.map(flight => flight.done)).then(() => undefined);
    return closing;
  }

  return { read, load, seed, invalidate, close };
}
