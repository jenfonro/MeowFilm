import { createMetadataStorage } from './metadata-storage.js';
import { isAbsolute } from 'node:path';
import { canonicalMetadataRequest, dueComponents, failedMetadata, mergeMetadata, metadataKind,
  metadataRecord, requestedComponents } from './metadata-policy.js';
import { fail, identity, object, requireValue } from './identity.js';

/** The data owner shares its bounded memory cache with process-only data.
 * Metadata alone has durable entity rows; there is no second response Map. */
export function createMetadataCache({ dataRoot, cache, owner, normalizeError, decodeValue, debug }) {
  requireValue(dataRoot === undefined || typeof dataRoot === 'string' && isAbsolute(dataRoot));
  const storage = dataRoot === undefined ? undefined : createMetadataStorage(dataRoot);
  const writes = new Map(), reads = new Map(), generations = new Map(), publications = new Map();
  const invalidated = new Set();
  let closed = false, closing;
  const generation = provider => generations.get(provider) ?? 0;
  const accepting = (provider, epoch) => !closed && !closing && generation(provider) === epoch;
  const publicationValid = (provider, epoch) => !closed && generation(provider) === epoch;
  function check(provider, epoch) { requireValue(accepting(provider, epoch), closed || closing ? 'CLOSED' : 'CANCELLED'); }
  const keyOf = request => identity('film-metadata', canonicalMetadataRequest(request));
  const dependencies = request => [owner, { kind: 'metadata', domain: request.provider,
    revision: request.settings.revision }];
  const retention = state => Math.max(0, ...Object.values(state.components).map(row => row.expiresAt ?? 0));
  const readMemory = request => cache.read(keyOf(request), { allowStale: true });

  const pendingPublications = new Map();
  const pendingByProvider = new Map();
  let pendingBytes = 0, inFlightWriteBytes = 0, flushWork;
  function adjustPendingProvider(provider, delta) {
    const next = (pendingByProvider.get(provider) ?? 0) + delta;
    if (next <= 0) pendingByProvider.delete(provider);
    else pendingByProvider.set(provider, next);
  }
  function cost(value) {
    if (typeof value === 'string') return Buffer.byteLength(value) + 32;
    if (value && typeof value === 'object') return 64 + Object.entries(value).reduce((sum, [key, item]) => sum + cost(key) + cost(item), 0);
    return 16;
  }
  function enqueue(provider, run) {
    const work = (writes.get(provider) ?? Promise.resolve()).then(run);
    const observed = work.catch(error => {
      debug?.emit({ level: 'warn', cat: 'film-catalog', sub: 'film.metadata.cache', event: 'cache.io.error',
        data: { provider, code: error?.code ?? null }, error });
    });
    writes.set(provider, observed);
    observed.then(() => { if (writes.get(provider) === observed) writes.delete(provider); });
    return work;
  }
  function publish(state) {
    if (!storage || closed || closing) return;
    const provider = state.request.provider, epoch = generation(provider), key = keyOf(state.request);
    const bytes = cost(state), previous = pendingPublications.get(key);
    if (bytes > 16 * 1024 * 1024 || pendingPublications.size >= 64 && !previous ||
        pendingBytes + inFlightWriteBytes - (previous?.bytes ?? 0) + bytes > 16 * 1024 * 1024) {
      debug?.emit({ level: 'warn', cat: 'film-catalog', sub: 'film.metadata.cache', event: 'cache.publish.budget_exceeded',
        data: { provider, path: state.request.path, bytes, pendingBytes, inFlightWriteBytes } });
      return;
    }
    pendingBytes += bytes - (previous?.bytes ?? 0);
    if (!previous) adjustPendingProvider(provider, 1);
    else if (previous.provider !== provider) {
      adjustPendingProvider(previous.provider, -1);
      adjustPendingProvider(provider, 1);
    }
    pendingPublications.set(key, { state, provider, epoch, bytes });
    publications.set(provider, (publications.get(provider) ?? 0) + 1);
    flushWork ??= Promise.resolve().then(async () => {
      while (pendingPublications.size) {
        const [itemKey, item] = pendingPublications.entries().next().value;
        pendingPublications.delete(itemKey);
        adjustPendingProvider(item.provider, -1);
        pendingBytes -= item.bytes;
        inFlightWriteBytes += item.bytes;
        try {
          if (publicationValid(item.provider, item.epoch)) {
            await enqueue(item.provider, () => storage.save(itemKey, item.state, item.bytes));
          }
        } catch { /* enqueue retains the observable cache I/O failure. */ }
        finally { inFlightWriteBytes -= item.bytes; }
      }
    }).finally(() => { flushWork = undefined; });
    return flushWork;
  }
  async function read(request) {
    if (closed || closing) return undefined;
    const retained = readMemory(request);
    const key = keyOf(request);
    if (retained || !storage || invalidated.has(request.provider) ||
        writes.has(request.provider) || pendingPublications.has(key)) return retained;
    const epoch = generation(request.provider), publication = publications.get(request.provider);
    const work = (async () => {
      try {
        const saved = await storage.read(key);
        if (!saved || !object(saved.components)) return undefined;
        const state = { ...saved, request: canonicalMetadataRequest(request) };
        if (!accepting(request.provider, epoch)) return undefined;
        // A producer/seed may have completed while the read was in flight.
        const memory = readMemory(request);
        if (memory) return memory;
        if (publications.get(request.provider) !== publication) return undefined;
        return cache.seed(key, state, { completeness: 'complete', expiresAt: retention(state),
          retainStale: true, dependencies: dependencies(request), supersede: false });
      } catch (error) {
        if (error?.code === 'CACHE_ENTRY_CORRUPT' || error?.code === 'STORAGE_CORRUPT') {
          debug?.emit({ level: 'warn', cat: 'film-catalog', sub: 'film.metadata.cache', event: 'cache.corrupt.miss',
            data: { provider: request.provider, path: request.path, code: error.code } });
          return undefined;
        }
        if (['STORAGE_IO', 'STORAGE_CLOSED', 'STORAGE_SCHEMA'].includes(error?.code)) {
          throw fail(closed || closing ? 'CLOSED' : 'STORAGE_ERROR');
        }
        return undefined;
      }
    })();
    reads.set(work, request.provider);
    try { return await work; } finally { reads.delete(work); }
  }

  function parentRequest(request) {
    if (metadataKind(request) !== 'season') return undefined;
    return { ...canonicalMetadataRequest(request), path: request.path.replace(/\/season\/\d+$/, '') };
  }
  function memoryParentOf(request) {
    const parent = parentRequest(request);
    return parent && readMemory(parent);
  }
  async function parentOf(request) {
    const parent = parentRequest(request);
    return parent && read(parent);
  }

  async function load(request, producer, { signal, completeness, refresh = false, requestedEpisode,
    checkSchedule, parent: checkedParent } = {}) {
    const required = requestedComponents(request, completeness);
    const epoch = generation(request.provider);
    const cacheKey = keyOf(request);
    check(request.provider, epoch);
    let state = await read(request);
    check(request.provider, epoch);
    let parent = memoryParentOf(request) ?? checkedParent ?? await parentOf(request);
    check(request.provider, epoch);
    const attempted = new Set();
    let selected = dueComponents(state, required, Date.now(), { refresh, parent, requestedEpisode });
    if (!selected.length) {
      debug?.emit({ level: 'debug', cat: 'film-catalog', sub: 'film.metadata.cache', event: 'cache.hit',
        data: { provider: request.provider, path: request.path, completeness } });
    }
    while (selected.length) {
      check(request.provider, epoch);
      checkSchedule?.();
      if (signal?.aborted) throw normalizeError(Object.assign(new Error('CANCELLED'), { code: 'CANCELLED' }));
      debug?.emit({ level: 'debug', cat: 'film-catalog', sub: 'film.metadata.cache', event: 'cache.miss',
        data: { provider: request.provider, path: request.path, refresh, components: selected } });
      let dispatched = false;
      let flightMeta;
      state = await cache.load(cacheKey, async (ownedSignal, meta) => {
        dispatched = true;
        let previous = readMemory(request) ?? state;
        parent = memoryParentOf(request) ?? parent;
        check(request.provider, epoch);
        selected = dueComponents(previous, required, Date.now(), { refresh, parent, requestedEpisode });
        if (!selected.length) {
          meta.unchanged = true;
          return previous;
        }
        meta.attempted = selected;
        const query = { ...canonicalMetadataRequest(request).query };
        const appended = selected.filter(name => !['detail', 'seasons', 'episodes', 'response', 'summary'].includes(name));
        if (appended.length) query.append_to_response = appended.join(',');
        try {
          const raw = await producer({ ...request, query }, ownedSignal);
          requireValue(!ownedSignal.aborted, 'CANCELLED');
          previous = readMemory(request) ?? previous;
          return mergeMetadata(previous, request, raw, selected, Date.now(), completeness, parent);
        } catch (error) {
          const normalized = normalizeError(error);
          if (ownedSignal.aborted || ['CANCELLED', 'CLOSED', 'STALE_METADATA'].includes(normalized.code)) throw normalized;
          previous = readMemory(request) ?? previous;
          if (!previous) throw normalized;
          debug?.emit({ level: 'warn', cat: 'film-catalog', sub: 'film.metadata.cache', event: 'cache.stale.fallback',
            data: { provider: request.provider, path: request.path, code: normalized.code, components: selected } });
          return failedMetadata(previous, selected, normalized.code);
        }
      }, { ttl: value => Math.max(0, retention(value) - Date.now()), completeness: 'complete', refresh: true,
        retainStale: true, dependencies: dependencies(request), signal, publish,
        onFlight: meta => { flightMeta = meta; } });
      // A caller for different append components or refresh:true may have joined
      // another flight. Re-evaluate due components preserving refresh while
      // excluding components actually fetched by that completed flight.
      if (dispatched) break;
      for (const name of flightMeta?.attempted ?? []) attempted.add(name);
      parent = memoryParentOf(request) ?? parent;
      selected = dueComponents(state, required, Date.now(), { refresh, parent, requestedEpisode })
        .filter(name => !attempted.has(name));
    }
    return state;
  }

  async function get(request, producer, { signal, completeness, refresh = false, requestedEpisode, checkSchedule } = {}) {
    let parent, parentError;
    const parentKey = parentRequest(request);
    if (parentKey) {
      try {
        parent = await load(parentKey, producer, { signal, completeness: 'complete', checkSchedule });
        const record = metadataRecord(parent, parentKey, ['detail', 'seasons'], Date.now());
        if (record.cache.status === 'stale') parentError = record.cache.error ?? { code: 'STALE_METADATA' };
      } catch (error) {
        const normalized = normalizeError(error);
        if (['CANCELLED', 'CLOSED'].includes(normalized.code)) throw normalized;
        parentError = { code: normalized.code };
      }
      checkSchedule?.();
    }
    const state = parentError ? await read(request) : await load(request, producer,
      { signal, completeness, refresh, requestedEpisode, checkSchedule, parent });
    if (!state) throw fail(parentError?.code ?? 'STALE_METADATA');
    return metadataRecord(state, request, requestedComponents(request, completeness), Date.now(),
      { parent: parentError ? parent : memoryParentOf(request) ?? parent, parentError, requestedEpisode });
  }

  async function entry(request, { minimumCompleteness = 'partial' } = {}) {
    requireValue(['partial', 'complete'].includes(minimumCompleteness));
    const epoch = generation(request.provider);
    check(request.provider, epoch);
    const state = await read(request);
    check(request.provider, epoch);
    if (!state) return undefined;
    const at = Date.now(), context = { parent: await parentOf(request) };
    check(request.provider, epoch);
    let record = metadataRecord(state, request, requestedComponents(request, 'complete'), at, context);
    if (record.cache.status !== 'fresh' && minimumCompleteness === 'partial') {
      record = metadataRecord(state, request, requestedComponents(request, 'partial'), at, context);
      if (record.completeness === 'complete') return undefined;
    }
    if (minimumCompleteness === 'complete' && record.completeness !== 'complete' ||
        record.cache.status !== 'fresh') return undefined;
    return record;
  }

  function seed(request, value, { completeness, fetchedAt, expiresAt }) {
    const epoch = generation(request.provider);
    check(request.provider, epoch);
    const previous = readMemory(request);
    check(request.provider, epoch);
    const required = requestedComponents(request, completeness);
    if (previous) {
      if (required.length === 1 && required[0] === 'summary') {
        const previousAt = previous.components.summary?.fetchedAt ?? previous.components.detail?.fetchedAt;
        if (previousAt >= fetchedAt) return undefined;
      } else {
        const currentRecord = metadataRecord(previous, request, requestedComponents(request, 'complete'), Date.now());
        const missingBase = previous.components.detail && previous.components.detail.fetchedAt === undefined;
        if (currentRecord.fetchedAt >= fetchedAt && !missingBase &&
            (currentRecord.completeness === 'complete' || completeness === 'partial')) return undefined;
      }
    }
    const parent = memoryParentOf(request);
    check(request.provider, epoch);
    const state = mergeMetadata(previous, request, value, required, fetchedAt, completeness, parent);
    state.components = Object.fromEntries(Object.entries(state.components).map(([name, row]) =>
      [name, required.includes(name) && row.fetchedAt === fetchedAt
        ? { ...row, expiresAt: Math.min(row.expiresAt, expiresAt) } : row]));
    const accepted = cache.seed(keyOf(request), state, { completeness, expiresAt: retention(state),
      retainStale: true, dependencies: dependencies(request), publish, replace: true, supersede: completeness === 'complete' });
    return accepted?.value;
  }

  function invalidate(matches) {
    const cleanup = [], selectedReads = [];
    for (const provider of ['tmdb', 'douban']) {
      if (!matches({ kind: 'metadata', domain: provider })) continue;
      const epoch = generation(provider) + 1;
      generations.set(provider, epoch);
      selectedReads.push(...[...reads].filter(([, domain]) => domain === provider).map(([work]) => work));
      // Even failed deletion must not make a revoked disk value eligible again
      // until cleanup succeeds. New results still use the shared bounded memory cache.
      invalidated.add(provider);
      for (const [key, item] of pendingPublications) if (item.provider === provider) {
        pendingPublications.delete(key);
        adjustPendingProvider(provider, -1);
        pendingBytes -= item.bytes;
      }
      if (storage) cleanup.push(enqueue(provider, async () => {
        await storage.invalidate(provider);
        if (accepting(provider, epoch)) invalidated.delete(provider);
      }));
    }
    return Promise.allSettled([...cleanup, ...selectedReads]).then(results => {
      if (results.slice(0, cleanup.length).some(result => result.status === 'rejected')) throw fail('STORAGE_ERROR');
    });
  }

  function close() {
    if (closing) return closing;
    // Block new admissions immediately via `closing`, drain accepted publications
    // and in-flight reads/writes with Promise.allSettled, then always close SQLite.
    closing = (async () => {
      try {
        await Promise.allSettled([flushWork, ...writes.values(), ...reads.keys()].filter(Boolean));
      } finally {
        closed = true;
        await storage?.close();
      }
    })();
    return closing;
  }
  function diagnostics() {
    return {
      hasStorage: Boolean(storage),
      closed,
      pendingPublications: pendingPublications.size,
      pendingBytes,
      inFlightWriteBytes,
      pendingByProvider: Object.fromEntries(pendingByProvider),
      activeWrites: writes.size,
      activeReads: reads.size,
      invalidatedProviders: [...invalidated],
      storage: storage?.stats() ?? null,
    };
  }
  return { get, entry, seed, parentFor: memoryParentOf, invalidate, diagnostics, close };
}
