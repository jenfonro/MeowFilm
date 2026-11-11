import fs from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { isAbsolute, join } from 'node:path';
import { isUtf8 } from 'node:buffer';
import { canonicalMetadataRequest, dueComponents, failedMetadata, mergeMetadata, metadataKind,
  metadataRecord, requestedComponents } from './metadata-policy.js';
import { fail, identity, object, requireValue } from './identity.js';

/** The data owner shares its bounded memory cache with process-only data.
 * Metadata alone has durable documents; there is no second response Map. */
export function createMetadataCache({ dataRoot, cache, owner, now, normalizeError, decodeValue }) {
  requireValue(dataRoot === undefined || typeof dataRoot === 'string' && isAbsolute(dataRoot));
  const root = dataRoot === undefined ? undefined : join(dataRoot, 'film-metadata');
  const writes = new Map(), reads = new Map(), generations = new Map(), publications = new Map();
  const invalidated = new Set();
  let closed = false, closing;
  const generation = provider => generations.get(provider) ?? 0;
  const current = (provider, epoch) => !closed && generation(provider) === epoch;
  function check(provider, epoch) { requireValue(current(provider, epoch), closed ? 'CLOSED' : 'CANCELLED'); }
  const keyOf = request => identity('film-metadata', canonicalMetadataRequest(request));
  const filename = request => `${request.provider}/${keyOf(request)}.json`;
  const dependencies = request => [owner, { kind: 'metadata', domain: request.provider,
    revision: request.settings.revision }];
  const retention = state => Math.max(0, ...Object.values(state.components).map(row => row.expiresAt ?? 0));
  const readMemory = request => cache.read(keyOf(request), { allowStale: true });

  function enqueue(provider, run) {
    // Serial publication/cleanup belongs to this owner, not to the business
    // document queue. Observe every failure without changing a request result.
    const work = (writes.get(provider) ?? Promise.resolve()).then(run);
    const observed = work.catch(() => { console.warn('film metadata cache I/O failed'); });
    writes.set(provider, observed);
    observed.then(() => { if (writes.get(provider) === observed) writes.delete(provider); });
    return work;
  }
  function publish(state) {
    if (!root || closed) return;
    const { request, attempted, ...durable } = state;
    const provider = request.provider, epoch = generation(provider);
    publications.set(provider, (publications.get(provider) ?? 0) + 1);
    return enqueue(provider, async () => {
      if (!current(provider, epoch)) return;
      const parent = join(root, provider), path = join(root, filename(request));
      await fs.mkdir(parent, { recursive: true });
      const temporary = join(parent, `.${randomUUID()}.tmp`);
      try {
        await fs.writeFile(temporary, JSON.stringify({ key: keyOf(request), state: durable }) + '\n',
          { flag: 'wx', mode: 0o600 });
        if (current(provider, epoch)) await fs.rename(temporary, path);
      } finally {
        await fs.rm(temporary, { force: true });
      }
    });
  }
  async function read(request) {
    const retained = readMemory(request);
    if (retained || !root || invalidated.has(request.provider) || writes.has(request.provider)) return retained;
    const epoch = generation(request.provider), publication = publications.get(request.provider);
    const work = (async () => {
      try {
        const path = join(root, filename(request));
        const bytes = await fs.readFile(path);
        requireValue(isUtf8(bytes));
        const document = JSON.parse(bytes.toString('utf8'));
        const state = { ...document.state, request: canonicalMetadataRequest(request) };
        // These are the fields consumed by metadata policy, not a separate
        // format/version probe. An unusable decoded object is simply a miss.
        requireValue(document.key === keyOf(request) && object(state.components) &&
          Object.values(state.components).every(row =>
            object(row) && ['ready', 'empty', 'missing', 'failed'].includes(row.status) &&
            (row.fetchedAt === undefined ? ['missing', 'failed'].includes(row.status)
              : Number.isFinite(row.fetchedAt) && Number.isFinite(row.expiresAt))));
        decodeValue(request.provider, request.path, state.value, 'partial');
        if (state.summaryValue !== undefined) decodeValue(request.provider, request.path, state.summaryValue, 'partial');
        const available = Object.entries(state.components)
          .filter(([, row]) => ['ready', 'empty'].includes(row.status)).map(([name]) => name);
        // Decode claimed components through the same receipt rules as an
        // upstream response; persisted readiness cannot replace their bodies.
        const decoded = mergeMetadata(undefined, request, state.value, available, now(), 'complete');
        requireValue(available.every(name => ['ready', 'empty'].includes(decoded.components[name]?.status)));
        if (!current(request.provider, epoch)) return undefined;
        // A producer/seed may have completed while the read was in flight.
        const memory = readMemory(request);
        if (memory) return memory;
        if (publications.get(request.provider) !== publication) return undefined;
        return cache.seed(keyOf(request), state, { completeness: 'complete', expiresAt: retention(state),
          retainStale: true, dependencies: dependencies(request), supersede: false });
      } catch { return undefined; }
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
    let state = await read(request);
    check(request.provider, epoch);
    let parent = memoryParentOf(request) ?? checkedParent ?? await parentOf(request);
    check(request.provider, epoch);
    let selected = dueComponents(state, required, now(), { refresh, parent, requestedEpisode });
    while (selected.length) {
      check(request.provider, epoch);
      checkSchedule?.();
      let dispatched = false;
      state = await cache.load(keyOf(request), async ownedSignal => {
        dispatched = true;
        let previous = readMemory(request) ?? state;
        parent = memoryParentOf(request) ?? parent;
        check(request.provider, epoch);
        selected = dueComponents(previous, required, now(), { refresh, parent, requestedEpisode });
        if (!selected.length) return previous;
        const query = { ...canonicalMetadataRequest(request).query };
        const appended = selected.filter(name => !['detail', 'seasons', 'episodes', 'response', 'summary'].includes(name));
        if (appended.length) query.append_to_response = appended.join(',');
        try {
          const raw = await producer({ ...request, query }, ownedSignal);
          requireValue(!ownedSignal.aborted, 'CANCELLED');
          previous = readMemory(request) ?? previous;
          return { ...mergeMetadata(previous, request, raw, selected, now(), completeness, parent), attempted: selected };
        } catch (error) {
          const normalized = normalizeError(error);
          if (ownedSignal.aborted || ['CANCELLED', 'CLOSED', 'STALE_METADATA'].includes(normalized.code)) throw normalized;
          previous = readMemory(request) ?? previous;
          if (!previous) throw normalized;
          return { ...failedMetadata(previous, selected, normalized.code), attempted: selected };
        }
      }, { ttl: value => Math.max(0, retention(value) - now()), completeness: 'complete', refresh: true,
        retainStale: true, dependencies: dependencies(request), signal, publish });
      // A caller for different append components may have joined this entity's
      // producer. It performs its own missing-component request after that work.
      if (dispatched) break;
      parent = memoryParentOf(request) ?? parent;
      selected = dueComponents(state, required, now(), { parent, requestedEpisode })
        .filter(name => !state.attempted?.includes(name));
    }
    return state;
  }

  async function get(request, producer, { signal, completeness, refresh = false, requestedEpisode, checkSchedule } = {}) {
    let parent, parentError;
    const parentKey = parentRequest(request);
    if (parentKey) {
      try {
        parent = await load(parentKey, producer, { signal, completeness: 'complete', checkSchedule });
        const record = metadataRecord(parent, parentKey, ['detail', 'seasons'], now());
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
    if (!state) throw fail(parentError.code);
    return metadataRecord(state, request, requestedComponents(request, completeness), now(),
      { parent: parentError ? parent : memoryParentOf(request) ?? parent, parentError, requestedEpisode });
  }

  async function entry(request, { minimumCompleteness = 'partial' } = {}) {
    requireValue(['partial', 'complete'].includes(minimumCompleteness));
    const epoch = generation(request.provider);
    const state = await read(request);
    check(request.provider, epoch);
    if (!state) return undefined;
    const at = now(), context = { parent: await parentOf(request) };
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
    const previous = readMemory(request);
    check(request.provider, epoch);
    const required = requestedComponents(request, completeness);
    if (previous) {
      if (required.length === 1 && required[0] === 'summary') {
        const previousAt = previous.components.summary?.fetchedAt ?? previous.components.detail?.fetchedAt;
        if (previousAt >= fetchedAt) return undefined;
      } else {
        const current = metadataRecord(previous, request, requestedComponents(request, 'complete'), now());
        const missingBase = previous.components.detail && previous.components.detail.fetchedAt === undefined;
        if (current.fetchedAt >= fetchedAt && !missingBase &&
            (current.completeness === 'complete' || completeness === 'partial')) return undefined;
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
      if (root) cleanup.push(enqueue(provider, async () => {
        let names;
        try { names = await fs.readdir(join(root, provider)); }
        catch (error) {
          if (error.code !== 'ENOENT') throw error;
          if (current(provider, epoch)) invalidated.delete(provider);
          return;
        }
        for (const name of names) {
          if (!/^[a-f0-9]+\.json$/.test(name)) continue;
          try { await fs.unlink(join(root, provider, name)); }
          catch (error) { if (error.code !== 'ENOENT') throw error; }
        }
        if (current(provider, epoch)) invalidated.delete(provider);
      }));
    }
    return Promise.allSettled([...cleanup, ...selectedReads]).then(results => {
      if (results.some(result => result.status === 'rejected')) throw fail('STORAGE_ERROR');
    });
  }
  function close() {
    if (closing) return closing;
    // Accepted publications drain on close; no producer can enqueue new work
    // after the data owner has revoked it. Closing is not cache invalidation.
    closing = Promise.all([...writes.values(), ...reads.keys()]).then(() => { closed = true; });
    return closing;
  }
  return { get, entry, seed, invalidate, close };
}
