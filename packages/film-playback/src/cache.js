import { isDeepStrictEqual } from 'node:util';
import { cloneJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';

const maximumBytes = 32 * 1024 * 1024;
const completenessRank = { partial: 0, complete: 1 };

function failure(code) {
  const error = new Error(typeof code === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(code) ? code : 'CACHE_LOAD_FAILED');
  error.name = 'PlaybackCacheError';
  error.code = error.message;
  return error;
}

function requireValue(condition) {
  if (!condition) throw failure('INVALID_REQUEST');
}

function estimateBytes(value) {
  if (isRawJSON(value)) return 64 + value.rawJSON.length * 2;
  if (typeof value === 'string') return 24 + value.length * 2;
  if (!value || typeof value !== 'object') return 8;
  let bytes = 64;
  if (Array.isArray(value)) {
    for (const item of value) bytes += 32 + estimateBytes(item);
    return bytes;
  }
  for (const [key, item] of Object.entries(value)) {
    bytes += 32 + key.length * 2 + estimateBytes(item);
  }
  return bytes;
}

function snapshot(value) {
  try {
    const cloned = cloneJSONValue(value);
    return { value: cloned, bytes: estimateBytes(cloned) };
  } catch {
    throw failure('INVALID_RESULT');
  }
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
export function createPlaybackCache() {
  const entries = new Map();
  const flights = new Map();
  const pending = new Set();
  let retainedBytes = 0;
  let memoryEpoch = 0;
  let hits = 0;
  let misses = 0;
  let evictions = 0;
  let expiredPurges = 0;
  let closed = false;
  let closing;

  function time() {
    return Date.now();
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

  function purgeExpired(at) {
    for (const [key, entry] of entries) {
      if (entry.expiresAt <= at && !entry.retainStale) {
        remove(key);
        expiredPurges++;
      }
    }
  }

  function find(key, minimumCompleteness, at = time(), allowStale = false, recordStats = false) {
    const entry = entries.get(key);
    if (!entry) { if (recordStats) misses++; return undefined; }
    if (entry.expiresAt <= at && !allowStale) {
      if (!entry.retainStale) { remove(key); expiredPurges++; }
      if (recordStats) misses++;
      return undefined;
    }
    if (completenessRank[entry.completeness] < completenessRank[minimumCompleteness]) {
      if (recordStats) misses++;
      return undefined;
    }
    entries.delete(key);
    entries.set(key, entry);
    if (recordStats) hits++;
    return entry;
  }

  function retain(flight, result, ttl, retainStale, publish) {
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
    if (flight.memoryEpoch !== memoryEpoch) {
      publish?.(result.value);
      return result.value;
    }
    const expiresAt = expiration(ttl, result.value, at);
    const bytes = 256 + flight.key.length * 2 + flight.dependencyBytes + result.bytes;
    // A large result is delivered whole; it simply does not displace retained data.
    if (expiresAt === at && !retainStale || bytes > maximumBytes) {
      remove(flight.key);
      publish?.(result.value);
      return result.value;
    }
    remove(flight.key);
    if (retainedBytes + bytes > maximumBytes) purgeExpired(at);
    while (retainedBytes + bytes > maximumBytes && entries.size > 0) {
      remove(entries.keys().next().value);
      evictions++;
    }
    entries.set(flight.key, { ...flight.info, value: result.value, expiresAt, retainStale, bytes });
    retainedBytes += bytes;
    publish?.(result.value);
    return result.value;
  }

  function revoke(flight) {
    flight.authorized = false;
    if (flights.get(flight.key) === flight) flights.delete(flight.key);
    flight.controller.abort();
  }

  function read(key, { minimumCompleteness = 'partial', allowStale = false } = {}) {
    open();
    keyValue(key);
    completenessValue(minimumCompleteness);
    requireValue(typeof allowStale === 'boolean');
    return find(key, minimumCompleteness, time(), allowStale, true)?.value;
  }

  async function load(key, producer, {
    ttl, completeness, dependencies = [], signal, refresh = false, retainStale = false, publish, onFlight,
  } = {}) {
    open();
    keyValue(key);
    completenessValue(completeness);
    ttlValue(ttl);
    requireValue(typeof producer === 'function' &&
      (signal === undefined || signal instanceof AbortSignal) && typeof refresh === 'boolean' &&
      typeof retainStale === 'boolean' && (publish === undefined || typeof publish === 'function') &&
      (onFlight === undefined || typeof onFlight === 'function'));
    if (signal?.aborted) throw failure('CANCELLED');
    const dependency = dependenciesValue(dependencies);

    scope(key, dependency.value);
    const cached = refresh ? undefined : find(key, completeness, time(), false, true);
    if (cached) return cached.value;
    const active = flights.get(key);
    if (active && completenessRank[active.completeness] >= completenessRank[completeness]) {
      onFlight?.(active.meta, true);
      return waitFor(active, signal);
    }

    const flight = {
      key, completeness, dependencyBytes: dependency.bytes, controller: new AbortController(),
      authorized: true, memoryEpoch, meta: Object.create(null),
      info: Object.freeze({ key, completeness, dependencies: dependency.value }),
    };
    flights.set(key, flight);
    pending.add(flight);
    onFlight?.(flight.meta, false);
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
      return producer(flight.controller.signal, flight.meta);
    }).then(value => {
      if (!flight.authorized || closed) throw failure('CANCELLED');
      const result = snapshot(value);
      return retain(flight, result, ttl, retainStale, flight.meta.unchanged ? undefined : publish);
    }).catch(error => { throw normalizedError(error); }).finally(() => {
      if (flights.get(key) === flight) flights.delete(key);
      pending.delete(flight);
    });
    // Producers continue when all consumers have left. Their failure is still
    // observed, without making a rejected background promise unhandled.
    flight.done.catch(() => {});
    return waitFor(flight, signal);
  }

  function seed(key, value, { ttl, expiresAt, completeness, dependencies = [], retainStale = false, publish,
    replace = false, supersede = true } = {}) {
    open();
    keyValue(key);
    completenessValue(completeness);
    requireValue(typeof retainStale === 'boolean' && typeof replace === 'boolean' && typeof supersede === 'boolean' &&
      (publish === undefined || typeof publish === 'function'));
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
    if (until <= at && !retainStale) {
      const current = find(key, 'partial', at);
      return current && isDeepStrictEqual(current.dependencies, dependency.value) ? current.value : undefined;
    }
    scope(key, dependency.value);
    const previous = find(key, 'partial', at, retainStale);
    if (!replace && previous && (completenessRank[previous.completeness] > completenessRank[completeness] ||
        previous.completeness === completeness && previous.expiresAt >= until)) return previous.value;
    for (const flight of pending) {
      if (supersede && flight.key === key && flight.authorized &&
          completenessRank[completeness] >= completenessRank[flight.completeness]) flight.seeded = true;
    }
    const bytes = 256 + key.length * 2 + dependency.bytes + result.bytes;
    if (bytes > maximumBytes) {
      // An accepted but unretained complete result also displaces an older
      // partial entry, so read cannot later pretend that partial is the winner.
      remove(key);
      publish?.(result.value);
      return result.value;
    }
    remove(key);
    if (retainedBytes + bytes > maximumBytes) purgeExpired(at);
    while (retainedBytes + bytes > maximumBytes && entries.size > 0) {
      remove(entries.keys().next().value);
      evictions++;
    }
    entries.set(key, { key, completeness, dependencies: dependency.value,
      value: result.value, expiresAt: until, retainStale, bytes });
    retainedBytes += bytes;
    publish?.(result.value);
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

  function clearMemory() {
    memoryEpoch++;
    const clearedEntries = entries.size;
    const clearedBytes = retainedBytes;
    entries.clear();
    retainedBytes = 0;
    return { clearedEntries, clearedBytes };
  }

  function diagnostics() {
    return {
      entries: entries.size,
      retainedBytes,
      maximumBytes,
      activeFlights: flights.size,
      pendingFlights: pending.size,
      hits,
      misses,
      evictions,
      expiredPurges,
      closed,
    };
  }

  return { read, load, seed, invalidate, clearMemory, diagnostics, close };
}
