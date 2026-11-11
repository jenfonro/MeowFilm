import { PrivateDocuments } from '@meowfilm/script-host';
import { isUtf8 } from 'node:buffer';
import { canonicalMetadataRequest, dueComponents, failedMetadata, mergeMetadata, metadataKind,
  metadataRecord, requestedComponents } from './metadata-policy.js';
import { fail, identity, object, requireValue } from './identity.js';

/** The data owner shares its bounded memory cache with process-only data.
 * Metadata alone has durable documents; there is no second response Map. */
export function createMetadataCache({ dataRoot, cache, owner, now, normalizeError }) {
  const documents = dataRoot === undefined ? undefined : new PrivateDocuments(dataRoot, 'film-metadata');
  const keyOf = request => identity('film-metadata', canonicalMetadataRequest(request));
  const filename = request => `${request.provider}/${keyOf(request)}.json`;
  const dependencies = request => [owner, { kind: 'metadata', domain: request.provider,
    revision: request.settings.revision }];
  const retention = state => Math.max(0, ...Object.values(state.components).map(row => row.expiresAt ?? 0));
  const readMemory = request => cache.read(keyOf(request), { allowStale: true });

  function publish(state) {
    const { request, attempted, ...durable } = state;
    try { documents?.write(filename(request), Buffer.from(JSON.stringify({
      version: 1, key: keyOf(request), state: durable,
    }) + '\n')); }
    catch { throw fail('STORAGE_ERROR'); }
  }
  function read(request, hydrate = true) {
    const retained = readMemory(request);
    if (retained || !documents) return retained;
    let bytes;
    try { bytes = documents.read(filename(request)); } catch { throw fail('STORAGE_ERROR'); }
    if (bytes === null) return undefined;
    let state;
    try {
      requireValue(isUtf8(bytes));
      const document = JSON.parse(bytes.toString('utf8'));
      state = { ...document.state, request: canonicalMetadataRequest(request) };
      requireValue(document.version === 1 && object(state) && object(state.request) &&
        document.key === keyOf(request) && object(state.components) &&
        (object(state.value) || Array.isArray(state.value)) &&
        (state.summaryValue === undefined || object(state.summaryValue) || Array.isArray(state.summaryValue)) &&
        Object.values(state.components).every(row =>
          object(row) && ['ready', 'empty', 'missing', 'failed'].includes(row.status) &&
          (row.fetchedAt === undefined || Number.isFinite(row.fetchedAt) && Number.isFinite(row.expiresAt))));
    } catch { throw fail('CORRUPT_STORAGE'); }
    return hydrate ? cache.seed(keyOf(request), state, { completeness: 'complete', expiresAt: retention(state),
      retainStale: true, dependencies: dependencies(request), supersede: false }) : state;
  }

  function parentRequest(request) {
    if (metadataKind(request) !== 'season') return undefined;
    return { ...canonicalMetadataRequest(request), path: request.path.replace(/\/season\/\d+$/, '') };
  }
  function parentOf(request) {
    const parent = parentRequest(request);
    return parent && read(parent);
  }

  async function load(request, producer, { signal, completeness, refresh = false, requestedEpisode,
    checkSchedule, parent: checkedParent } = {}) {
    const required = requestedComponents(request, completeness);
    let state = read(request);
    let parent = parentOf(request) ?? checkedParent;
    let selected = dueComponents(state, required, now(), { refresh, parent, requestedEpisode });
    while (selected.length) {
      checkSchedule?.();
      let dispatched = false;
      state = await cache.load(keyOf(request), async ownedSignal => {
        dispatched = true;
        let previous = read(request, false);
        parent = parentOf(request) ?? checkedParent;
        selected = dueComponents(previous, required, now(), { refresh, parent, requestedEpisode });
        if (!selected.length) return previous;
        const query = { ...canonicalMetadataRequest(request).query };
        const appended = selected.filter(name => !['detail', 'seasons', 'episodes', 'response', 'summary'].includes(name));
        if (appended.length) query.append_to_response = appended.join(',');
        try {
          const raw = await producer({ ...request, query }, ownedSignal);
          requireValue(!ownedSignal.aborted, 'CANCELLED');
          previous = read(request, false) ?? previous;
          return { ...mergeMetadata(previous, request, raw, selected, now(), completeness, parent), attempted: selected };
        } catch (error) {
          const normalized = normalizeError(error);
          if (ownedSignal.aborted || ['CANCELLED', 'CLOSED', 'STALE_METADATA'].includes(normalized.code)) throw normalized;
          previous = read(request, false) ?? previous;
          if (!previous) throw normalized;
          return { ...failedMetadata(previous, selected, normalized.code), attempted: selected };
        }
      }, { ttl: value => Math.max(0, retention(value) - now()), completeness: 'complete', refresh: true,
        retainStale: true, dependencies: dependencies(request), signal, publish });
      // A caller for different append components may have joined this entity's
      // producer. It performs its own missing-component request after that work.
      if (dispatched) break;
      parent = parentOf(request) ?? checkedParent;
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
    const state = parentError ? read(request) : await load(request, producer,
      { signal, completeness, refresh, requestedEpisode, checkSchedule, parent });
    if (!state) throw fail(parentError.code);
    return metadataRecord(state, request, requestedComponents(request, completeness), now(),
      { parent: parentError ? parent : parentOf(request) ?? parent, parentError, requestedEpisode });
  }

  function entry(request, { minimumCompleteness = 'partial' } = {}) {
    requireValue(['partial', 'complete'].includes(minimumCompleteness));
    const state = read(request);
    if (!state) return undefined;
    const at = now(), context = { parent: parentOf(request) };
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
    const previous = read(request);
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
    const state = mergeMetadata(previous, request, value, required, fetchedAt, completeness, parentOf(request));
    state.components = Object.fromEntries(Object.entries(state.components).map(([name, row]) =>
      [name, required.includes(name) && row.fetchedAt === fetchedAt
        ? { ...row, expiresAt: Math.min(row.expiresAt, expiresAt) } : row]));
    const accepted = cache.seed(keyOf(request), state, { completeness, expiresAt: retention(state),
      retainStale: true, dependencies: dependencies(request), publish, replace: true, supersede: completeness === 'complete' });
    return accepted?.value;
  }

  function invalidate(matches) {
    if (!documents) return;
    try {
      for (const provider of ['tmdb', 'douban']) {
        if (!matches({ kind: 'metadata', domain: provider })) continue;
        for (const name of documents.list(provider)) documents.remove(name);
      }
    } catch { throw fail('STORAGE_ERROR'); }
  }
  return { get, entry, seed, invalidate };
}
