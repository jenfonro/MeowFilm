import { randomUUID, createHash } from 'node:crypto';
import { Readable } from 'node:stream';
import { isUtf8 } from 'node:buffer';
import { ScriptHostError } from '@meowfilm/script-host';
import { normalizeSourceDefinition } from '@meowfilm/source-runtime';
import { parseJSONText, stringifyJSONValue, cloneJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';
import { InstallationOperations } from './source-candidates.mjs';
import { SourceStorage } from './storage/source-storage.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const emptyPrivate = () => ({ cookies: [], storage: [] });
const profile = (id, generation) => `source:${id}:${generation}:`;
const bool = (value, fallback) => value === undefined ? fallback
  : typeof value === 'string' ? !['false', '0', ''].includes(value.trim().toLowerCase()) : Boolean(value);
const hash = value => createHash('sha256').update(value).digest('hex');
const configurationKeys = ['installationId', 'revision', 'name', 'sourceUrl', 'group', 'order',
  'enabled', 'enabledExplore', 'protocol', 'definition'];
const installationId = value => typeof value === 'string' &&
  /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$(?![\s\S])/.test(value);
const sameConfiguration = (left, right, revision = true) => left && right &&
  configurationKeys.every(key => !revision && key === 'revision' || left[key] === right[key]);

const MAX_CACHED_IMAGES = 128;
const MAX_CACHED_IMAGE_BYTES = 64 * 1024 * 1024;
const PNG_MAGIC = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const isCorruptStorage = error => ['STORAGE_CORRUPT', 'STORAGE_SCHEMA', 'CORRUPT_STORAGE'].includes(error?.code);

/** Source sessions and private state; the default mode also owns permanent definitions. */
export function createSourceLibrary({ dataRoot, host, owner, web, network,
  fail, errorCode, guard, lifetime, configurationOwner, externalInstallation = () => false, debug }) {
  const native = configurationOwner === 'native';
  const disk = dataRoot === undefined ? null : new SourceStorage(dataRoot, native);
  const records = new Map(), states = new Map(), images = new Map(), streams = new Set();
  const operations = new InstallationOperations();
  const memoryReaderValues = disk ? null : new Map();
  let privateRecords = new Map(), nativeConfiguration = new Map(), nativeRevision = 0;
  let readFailure, imageBytes = 0;

  const require = (value, code = 'INVALID_REQUEST') => { if (!value) throw fail(code); };
  const live = id => host.list().find(item => item.installationId === id);
  function record(id, revision) {
    const item = records.get(id);
    require(item, 'SOURCE_NOT_FOUND');
    if (revision !== undefined) require(item.revision === revision, 'STALE_SOURCE');
    return item;
  }
  function publicRecord(item) {
    const { installationId, revision, sourceUrl } = item;
    const enabled = item.enabled === true, enabledExplore = item.enabledExplore === true;
    const protocol = typeof item.protocol === 'string' ? item.protocol : '';
    const name = typeof item.name === 'string' ? item.name : '';
    const group = typeof item.group === 'string' ? item.group : '';
    const ordering = isRawJSON(item.order) ? Number(item.order.rawJSON) : item.order;
    const order = Number.isFinite(ordering) ? ordering : 0;
    const actions = Array.isArray(item.actions) ? item.actions.filter(action => typeof action === 'string') : [];
    const state = states.get(installationId);
    let raw = null, corruptError = null;
    try {
      raw = item.definition === undefined ? disk.definition(item.installationId,
        ['weight', 'lastUpdateTime', 'respondTime', 'exploreUrl']) : definitionObject(item.definition);
    } catch (error) {
      if (isCorruptStorage(error)) corruptError = 'CORRUPT_STORAGE';
      else throw error;
    }
    const number = (key, fallback) => {
      if (!raw) return fallback;
      const value = isRawJSON(raw[key]) ? Number(raw[key].rawJSON) : Number(raw[key]);
      return Number.isFinite(value) ? value : fallback;
    };
    const errorCodeValue = state?.errorCode ?? corruptError;
    return { installationId, revision, name, sourceUrl, group, order, enabled, enabledExplore,
      protocol, weight: number('weight', 0), lastUpdateTime: number('lastUpdateTime', 0),
      respondTime: number('respondTime', 180000),
      hasExplore: Boolean(raw?.exploreUrl || actions.includes('explore')),
      actions: [...actions], ...(errorCodeValue ? { errorCode: errorCodeValue } : {}) };
  }
  async function persist(next = records, removed = []) {
    if (readFailure) throw readFailure;
    if (!disk) return;
    const nextPrivate = new Map(privateRecords);
    if (native) {
      for (const id of removed) nextPrivate.delete(id);
      for (const item of next.values()) nextPrivate.set(item.installationId, item.private);
    }
    const proposed = native ? new Map([...nextPrivate].map(([installationId, privateState]) =>
      [installationId, { installationId, private: privateState }])) : next;
    try {
      await disk.save(proposed);
    }
    catch (error) {
      if (error.code === 'STORAGE_CONFLICT') readFailure = fail('STORAGE_ERROR');
      throw fail(isCorruptStorage(error) ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR');
    }
    if (native) privateRecords = nextPrivate;
  }
  function parseDefinition(definition) {
    require(typeof definition === 'string' && definition.trim().length > 0);
    const normalized = normalizeSourceDefinition(definition.replace(/^\uFEFF/, ''));
    require(['legado', 'qysg'].includes(normalized.protocol), 'UNSUPPORTED_PROTOCOL');
    return normalized;
  }
  function definitionObject(definition, required = false) {
    let value;
    try { value = parseJSONText(definition); } catch (error) {
      if (required && !definition.trimStart().startsWith('<')) throw error;
      value = definition;
    }
    if (Array.isArray(value)) [value] = value;
    return object(value) ? value : { html: value };
  }
  function definitionText(item) {
    try { return item.definition === undefined ? disk.definition(item.installationId) : item.definition; }
    catch (error) { throw fail(isCorruptStorage(error) ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR'); }
  }
  function managedDefinition(item) {
    return { ...definitionObject(definitionText(item), true), bookSourceName: item.name,
      bookSourceUrl: item.sourceUrl, bookSourceGroup: item.group, customOrder: item.order,
      enabled: item.enabled, enabledExplore: item.enabledExplore };
  }
  function runtimeDefinition(item) {
    const normalized = parseDefinition(definitionText(item));
    normalized.source = managedDefinition(item);
    return normalized;
  }
  function build(definition, old, fallbackName = '', origin = '') {
    const normalized = parseDefinition(definition);
    definition = definition.replace(/^\uFEFF/, '');
    const source = normalized.source, raw = definitionObject(definition);
    const sourceUrl = String(source.bookSourceUrl ?? old?.sourceUrl ??
      (normalized.protocol === 'qysg' ? origin || `qysg:${fallbackName || hash(definition)}` : '')).trim();
    require(sourceUrl.length > 0);
    const inputOrder = raw.customOrder ?? old?.order ?? source.customOrder ?? records.size;
    const order = Number(isRawJSON(inputOrder) ? inputOrder.rawJSON : inputOrder);
    require(Number.isFinite(order));
    return {
      ...old,
      installationId: old?.installationId ?? randomUUID(), revision: (old?.revision ?? 0) + 1,
      name: String(source.bookSourceName ?? old?.name ?? (fallbackName || sourceUrl)),
      sourceUrl, group: String(source.bookSourceGroup ?? old?.group ?? ''),
      order, enabled: bool(raw.enabled,
        old?.enabled ?? bool(source.enabled, true)),
      enabledExplore: bool(raw.enabledExplore, old?.enabledExplore ??
        Boolean(source.enabledExplore ?? source.exploreUrl)),
      protocol: normalized.importedProtocol, actions: old?.actions ?? [],
      definition, ...(old ? {} : { private: emptyPrivate() }),
    };
  }
  function assertOpen(signal) {
    guard(signal);
    if (readFailure) throw readFailure;
  }
  function dropImage(reference) {
    const item = images.get(reference);
    if (!item) return null;
    images.delete(reference);
    imageBytes = Math.max(0, imageBytes - item.body.byteLength);
    item.controller.abort();
    return item;
  }
  function releaseImages(id) {
    for (const [reference, item] of [...images]) {
      if (item.installationId !== id) continue;
      dropImage(reference);
    }
    for (const stream of streams) if (stream.installationId === id) stream.dispose();
  }
  function retire(id) {
    const state = states.get(id);
    state?.controller.abort();
    if (state) state.accepting = false;
    web.retire(`source:${id}:`);
    releaseImages(id);
  }
  function stage(item) {
    retire(item.installationId);
    const state = { controller: new AbortController(), accepting: false, trial: true,
      generation: undefined, private: item.private === undefined ? undefined : cloneJSONValue(item.private) };
    states.set(item.installationId, state);
    return state;
  }
  function seed(id, generation, state) {
    if (state.generation === generation) return;
    require(state.generation === undefined, 'STALE_SOURCE');
    require(object(state.private), 'CORRUPT_STORAGE');
    owner.restoreProfiles(profile(id, generation), { cookies: state.private.cookies, storage: state.private.storage });
    state.generation = generation;
  }
  async function execute(item, state, signal) {
    const normalized = runtimeDefinition(item);
    require(normalized.importedProtocol === item.protocol, 'SOURCE_INTEGRITY');
    const source = stringifyJSONValue(normalized.source);
    const spec = { installationId: item.installationId, protocol: normalized.protocol,
      source, contentHash: hash(source), version: '1.0.0',
      metadata: { importedProtocol: normalized.importedProtocol, persistentSource: true },
      permissions: { storage: [] }, enabled: true };
    const prior = live(item.installationId);
    if (prior && prior.state !== 'ready') await host.uninstall(item.installationId);
    if (live(item.installationId)) {
      await host.update(item.installationId, spec, { signal });
    } else await host.install(spec, { signal });
    guard(signal);
    const current = live(item.installationId);
    require(current?.state === 'ready', 'SOURCE_FAILURE');
    if (state.generation === undefined) seed(item.installationId, current.generation, state);
    require(current.generation === state.generation, 'STALE_SOURCE');
    cleanup(item.installationId);
    item.actions = [...(current.capabilities?.source?.actions ?? [])];
    item.private = { ...item.private, ...owner.snapshotProfiles(profile(item.installationId, current.generation)) };
    if (!item.enabled) {
      state.controller.abort();
      await host.uninstall(item.installationId);
      state.generation = undefined;
    }
  }
  async function release(id, generation) {
    try {
      await owner.releaseProfiles(`source:${id}:`, generation ? `source:${id}:${generation}` : '');
      await Promise.all([...streams].filter(stream => stream.installationId === id)
        .map(stream => stream.dispose()));
    }
    catch { throw fail('SOURCE_CLEANUP'); }
  }
  function cleanup(id) {
    require(!host.stats().cleanupIssues.some(issue => issue.installationId === id) &&
      host.cleanupPending(id) === 0, 'SOURCE_CLEANUP');
  }
  async function restore(old) {
    const id = old.installationId;
    const state = stage(old);
    try {
      if (!old.enabled) {
        if (live(id)) await host.uninstall(id);
      } else {
        // A failed trial keeps the old host session; a committed trial can be
        // rolled back by the host, without importing a second repository.
        const current = live(id);
        const normalized = runtimeDefinition(old);
        if (current?.state === 'ready' && current.contentHash === hash(stringifyJSONValue(normalized.source))) {
          seed(id, current.generation, state);
        } else {
          if (current) await host.uninstall(id);
          await execute(cloneJSONValue(old), state, lifetime.signal);
        }
      }
      state.trial = false;
      state.accepting = old.enabled;
      await release(id, state.generation);
    } catch {
      state.trial = false; state.accepting = false; state.errorCode = 'SOURCE_CLEANUP';
      throw fail('SOURCE_CLEANUP');
    }
  }
  async function replace(item, old, signal) {
    const id = item.installationId, state = stage(item);
    const active = AbortSignal.any([signal, state.controller.signal]);
    try {
      await execute(item, state, active);
      guard(signal);
      await release(id, state.generation);
      const next = new Map(records).set(id, item);
      await persist(next);
      records.set(id, item);
      state.trial = false;
      state.accepting = item.enabled;
      return { source: publicRecord(item) };
    } catch (error) {
      if (lifetime.signal.aborted) throw error;
      if (old) await restore(old);
      else {
        retire(id);
        if (live(id)) await host.uninstall(id);
        states.delete(id);
        await release(id);
      }
      throw error;
    }
  }
  const ready = Promise.resolve().then(async () => {
    try {
      const saved = await disk?.load();
      for (const [id, item] of saved ?? []) {
        if (native) privateRecords.set(id, item.private);
        else records.set(id, item);
      }
    } catch (error) {
      readFailure = fail(isCorruptStorage(error) ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR');
      throw readFailure;
    }
    if (readFailure || native) return;
    for (const saved of records.values()) {
      guard(lifetime.signal);
      const item = cloneJSONValue(saved);
      const state = stage(item);
      if (item.enabled === false) { state.trial = false; continue; }
      try {
        require(item.enabled === true, 'CORRUPT_STORAGE');
        await execute(item, state, lifetime.signal);
        await persist(new Map(records).set(item.installationId, item));
        records.set(item.installationId, item);
        state.trial = false; state.accepting = true;
      } catch (error) {
        state.errorCode = errorCode(error);
        state.trial = false; state.accepting = false;
        try { if (live(item.installationId)) await host.uninstall(item.installationId); }
        catch { state.errorCode = 'SOURCE_CLEANUP'; }
        try { await release(item.installationId); }
        catch { state.errorCode = 'SOURCE_CLEANUP'; }
      }
    }
  });
  ready.catch(() => {});
  async function access(signal) { assertOpen(signal); await ready; assertOpen(signal); }
  function manage(signal, body) {
    return operations.run('library', async () => { await access(signal); return body(); });
  }
  function validateNativeSnapshot(params) {
    require(native, 'PERMISSION_DENIED');
    require(Number.isSafeInteger(params.revision) && params.revision > 0 && Array.isArray(params.sources));
    const next = new Map(), urls = new Set(), liveIds = new Set(host.list().map(item => item.installationId));
    for (const item of params.sources) {
      const order = isRawJSON(item?.order) ? Number(item.order.rawJSON) : item?.order;
      require(object(item) && installationId(item.installationId) &&
        Number.isSafeInteger(item.revision) && item.revision > 0 &&
        ['name', 'sourceUrl', 'group', 'protocol', 'definition'].every(key => typeof item[key] === 'string') &&
        item.sourceUrl.trim().length > 0 &&
        item.sourceUrl === item.sourceUrl.trim() && Number.isFinite(order) &&
        typeof item.enabled === 'boolean' && typeof item.enabledExplore === 'boolean');
      require(!next.has(item.installationId) && !urls.has(item.sourceUrl), 'SOURCE_INTEGRITY');
      require(['legado', 'qysg'].includes(item.protocol), 'UNSUPPORTED_PROTOCOL');
      require(!externalInstallation(item.installationId), 'STALE_SOURCE');
      require(!liveIds.has(item.installationId) || records.has(item.installationId) ||
        states.has(item.installationId), 'STALE_SOURCE');
      next.set(item.installationId, { ...Object.fromEntries(configurationKeys.map(key => [key, item[key]])), order });
      urls.add(item.sourceUrl);
    }
    return next;
  }
  function nativeGuard(params, signal) {
    assertOpen(signal);
    require(params.revision === nativeRevision, 'STALE_SOURCE');
  }
  function nativeSourceGuard(item, signal) {
    assertOpen(signal);
    require(sameConfiguration(item, nativeConfiguration.get(item.installationId)), 'STALE_SOURCE');
  }
  async function applyNativeSource(item, old, signal) {
    const id = item.installationId, previous = states.get(id), current = live(id);
    if (sameConfiguration(item, old) && previous && !previous.trial && !previous.errorCode &&
        (item.enabled ? previous.accepting && current?.state === 'ready' &&
          current.generation === previous.generation : !current)) return;
    const reuse = item.enabled && sameConfiguration(item, old, false) && !previous?.errorCode &&
      current?.state === 'ready' && current.generation === previous?.generation;
    const savedPrivate = item.private === undefined ? undefined : cloneJSONValue(item.private), state = stage(item);
    try {
      if (reuse) {
        seed(id, current.generation, state);
        item.actions = [...(current.capabilities?.source?.actions ?? [])];
      } else if (item.enabled) {
        await execute(item, state, AbortSignal.any([signal, state.controller.signal]));
      } else if (current) {
        await host.uninstall(id);
        require(!live(id), 'SOURCE_CLEANUP');
      }
      await release(id, state.generation);
      cleanup(id);
      nativeSourceGuard(item, signal);
      await persist();
      state.trial = false; state.accepting = item.enabled;
    } catch (error) {
      retire(id);
      state.trial = false; state.errorCode = errorCode(error);
      item.actions = []; item.private = savedPrivate;
      try {
        if (live(id)) await host.uninstall(id);
        require(!live(id), 'SOURCE_CLEANUP');
      } catch { state.errorCode = 'SOURCE_CLEANUP'; }
      try { await release(id); cleanup(id); }
      catch { state.errorCode = 'SOURCE_CLEANUP'; }
      state.generation = undefined;
      nativeSourceGuard(item, signal);
    }
  }
  async function applyNative(params, signal) {
    await access(signal);
    const next = validateNativeSnapshot(params);
    require(params.revision >= nativeRevision, 'STALE_SOURCE');
    nativeRevision = params.revision; nativeConfiguration = next;
    // Interrupt the captured initialization before waiting for library management.
    for (const id of states.keys()) {
      if (sameConfiguration(records.get(id), next.get(id))) continue;
      const stop = host.captureInitialization(id);
      retire(id);
      stop?.();
    }
    return manage(signal, async () => {
      nativeGuard(params, signal);
      const old = new Map(records);
      records.clear();
      for (const [id, input] of next) {
        const prior = old.get(id);
        const privateState = prior ? prior.private : privateRecords.has(id) ? privateRecords.get(id) : emptyPrivate();
        records.set(id, sameConfiguration(input, prior) ? prior : { ...input,
          actions: sameConfiguration(input, prior, false) ? [...prior.actions] : [],
          private: privateState === undefined ? undefined : cloneJSONValue(privateState) });
      }
      const removed = [];
      for (const [id, state] of states) {
        if (next.has(id)) continue;
        retire(id);
        try {
          if (live(id)) await host.uninstall(id);
          require(!live(id), 'SOURCE_CLEANUP');
          await release(id);
          cleanup(id);
          removed.push(id);
        } catch {
          state.errorCode = 'SOURCE_CLEANUP';
          try { await release(id); }
          catch (releaseError) {
            debug?.emit({
              level: 'warn', cat: 'novel', sub: 'source.library', event: 'source.cleanup.release_error',
              data: { installationId: id }, error: releaseError,
            });
          }
        }
        nativeGuard(params, signal);
      }
      for (const item of records.values()) {
        nativeGuard(params, signal);
        await applyNativeSource(item, old.get(item.installationId), signal);
      }
      nativeGuard(params, signal);
      await persist(records, removed);
      for (const id of removed) { states.delete(id); memoryReaderValues?.delete(id); }
      require(![...states.values()].some(state => state.errorCode === 'SOURCE_CLEANUP'), 'SOURCE_CLEANUP');
      return { revision: params.revision, sources: [...records.values()].map(publicRecord) };
    });
  }
  async function admitted(id, revision, signal) {
    return manage(signal, () => {
      const item = record(id, revision), state = states.get(id), current = live(id);
      require(item.enabled, 'SOURCE_DISABLED');
      require(state?.accepting && current?.state === 'ready' &&
        state.generation === current.generation, state?.errorCode ?? 'STALE_SOURCE');
      return { item, state, generation: current.generation,
        signal: AbortSignal.any([signal, state.controller.signal]) };
    });
  }
  async function invoke(params, signal) {
    const admittedCall = await admitted(params.installationId, params.revision, signal);
    const { item, state, generation, signal: active } = admittedCall;
    const value = await host.invoke({ installationId: item.installationId, generation,
      siteId: 'source', operation: params.action, payload: params.payload }, { signal: active });
    guard(active);
    require(records.get(item.installationId) === item && states.get(item.installationId) === state &&
      state.accepting && live(item.installationId)?.generation === generation, 'STALE_SOURCE');
    return value ?? null;
  }
  async function importEntries(params, signal) {
    let input = params.text;
    if (params.url !== undefined) {
      const validateURL = value => {
        let target;
        try { target = new URL(value); } catch { throw new ScriptHostError('INVALID_ARGUMENT'); }
        if (!['http:', 'https:'].includes(target.protocol))
          throw new ScriptHostError('INVALID_ARGUMENT');
        return target.href;
      };
      const url = validateURL(params.url);
      const deadline = new AbortController();
      const timer = setTimeout(() => deadline.abort(), 60_000);
      timer.unref?.();
      let response;
      try {
        response = await network({ url, validateURL,
          options: { method: 'GET', encoding: null },
          signal: AbortSignal.any([signal, deadline.signal]) });
        guard(signal);
        require(!deadline.signal.aborted, 'TIMEOUT');
      } catch (error) {
        guard(signal);
        if (deadline.signal.aborted) throw fail('TIMEOUT');
        throw error;
      } finally { clearTimeout(timer); }
      require(response.statusCode >= 200 && response.statusCode < 300, 'HTTP_ERROR');
      const body = Buffer.from(response.body);
      require(isUtf8(body), 'SOURCE_ENCODING');
      input = body.toString('utf8');
    }
    require(typeof input === 'string' && input.trim().length > 0);
    input = input.replace(/^\uFEFF/, '');
    const entriesOf = parsed => {
      if (Array.isArray(parsed)) return parsed;
      if (object(parsed)) {
        for (const key of ['sources', 'bookSources', 'data']) {
          if (Array.isArray(parsed[key])) return parsed[key];
        }
      }
      return [parsed];
    };
    try { return entriesOf(parseJSONText(input)); }
    catch {
      // HTML is a complete QYSG definition, not a line-delimited document.
      if (input.trimStart().startsWith('<')) return [input];
      const lines = input.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
      try { return lines.flatMap(line => entriesOf(parseJSONText(line))); }
      catch { return [input]; }
    }
  }
  function importCandidate(entry, params) {
    const definition = typeof entry === 'string' ? entry : stringifyJSONValue(entry);
    return build(definition, undefined, params.fileName, params.url);
  }
  const methods = {
    'library.apply': applyNative,
    async 'library.list'(_params, signal) {
      await access(signal);
      return { sources: [...records.values()].map(publicRecord).sort((a, b) => a.order - b.order ||
        a.name.localeCompare(b.name) || a.installationId.localeCompare(b.installationId)) };
    },
    async 'library.get'(params, signal) {
      await access(signal);
      const item = record(params.installationId);
      return { source: publicRecord(item), definition: stringifyJSONValue(managedDefinition(item)) };
    },
    'library.previewImport': (params, signal) => operations.run(Symbol('preview'), async () => {
      await access(signal);
      const entries = await importEntries(params, signal), items = [], failures = [];
      const existing = new Set([...records.values()].map(item => item.sourceUrl));
      for (const [index, entry] of entries.entries()) {
        guard(signal);
        try {
          const item = importCandidate(entry, params);
          items.push({ index, name: item.name, sourceUrl: item.sourceUrl, group: item.group,
            protocol: item.protocol, exists: existing.has(item.sourceUrl),
            // Canonical metadata makes a later text import retain HTML's URL/file identity.
            definition: stringifyJSONValue(managedDefinition(item)) });
        } catch (error) {
          failures.push({ index, name: String(object(entry) ? entry.bookSourceName ?? '' : params.fileName ?? ''),
            code: errorCode(error) });
        }
      }
      guard(signal);
      return { items, failures };
    }),
    'library.import': (params, signal) => manage(signal, async () => {
      const entries = await importEntries(params, signal);
      const imported = [], failures = [];
      for (const [index, entry] of entries.entries()) {
        guard(signal);
        const definition = typeof entry === 'string' ? entry : stringifyJSONValue(entry);
        try {
          const candidate = importCandidate(entry, params);
          const old = [...records.values()].find(value => value.sourceUrl === candidate.sourceUrl);
          const next = old ? build(definition, old, params.fileName) : candidate;
          imported.push((await replace(next, old, signal)).source);
        } catch (error) {
          guard(signal);
          failures.push({ index, name: String(object(entry) ? entry.bookSourceName ?? '' : params.fileName ?? ''),
            code: errorCode(error) });
        }
      }
      return { sources: imported, failures };
    }),
    'library.save': (params, signal) => manage(signal, () => {
      const old = record(params.installationId, params.expectedRevision);
      const item = build(params.definition, old);
      require(![...records.values()].some(other => other.installationId !== item.installationId &&
        other.sourceUrl === item.sourceUrl), 'SOURCE_INTEGRITY');
      return replace(item, old, signal);
    }),
    'library.update': (params, signal) => manage(signal, async () => {
      const old = record(params.installationId, params.expectedRevision);
      const patch = Object.fromEntries(['name', 'group', 'enabled', 'enabledExplore', 'order']
        .filter(key => Object.hasOwn(params.patch, key)).map(key => [key, params.patch[key]]));
      const item = { ...cloneJSONValue(old), ...patch, revision: old.revision + 1 };
      if (patch.enabled === true && (!old.enabled || !states.get(old.installationId)?.accepting)) {
        return replace(item, old, signal);
      }
      const previous = states.get(old.installationId);
      if (patch.enabled === false && old.enabled) retire(old.installationId);
      try {
        if (patch.enabled === false && old.enabled && live(old.installationId)) {
          await host.uninstall(old.installationId);
        }
        guard(signal);
        if (!item.enabled) await release(item.installationId);
        const next = new Map(records).set(item.installationId, item);
        await persist(next);
        records.set(item.installationId, item);
        if (!item.enabled) {
          states.set(item.installationId, { controller: new AbortController(), accepting: false,
            trial: false, generation: undefined });
        } else if (previous) {
          // Metadata changes keep the real runtime and its generation.
          states.set(item.installationId, previous);
        }
        return { source: publicRecord(item) };
      } catch (error) {
        if (patch.enabled === false && old.enabled && !lifetime.signal.aborted) await restore(old);
        throw error;
      }
    }),
    'library.remove': (params, signal) => manage(signal, async () => {
      const old = record(params.installationId, params.expectedRevision);
      retire(old.installationId);
      try {
        if (live(old.installationId)) await host.uninstall(old.installationId);
        guard(signal);
        await release(old.installationId);
        const next = new Map(records); next.delete(old.installationId);
        await persist(next);
        records.delete(old.installationId); states.delete(old.installationId);
        memoryReaderValues?.delete(old.installationId);
        return { removed: true };
      } catch (error) {
        if (!lifetime.signal.aborted) await restore(old);
        throw error;
      }
    }),
    async 'library.export'(params, signal) {
      await access(signal);
      return { text: stringifyJSONValue(params.installationIds.map(id => managedDefinition(record(id)))) };
    },
    'library.invoke': invoke,
    async 'library.image'(params, signal) {
      const call = await admitted(params.installationId, params.revision, signal);
      const value = await invoke({ ...params, action: 'image',
        payload: { url: params.url, bookUrl: params.bookUrl, kind: params.kind } }, call.signal);
      let body = value?.body;
      if (typeof body === 'string') {
        const data = /^data:(image\/[A-Za-z0-9.+-]+);base64,([\s\S]*)$/.exec(body);
        if (data) body = Buffer.from(data[2], 'base64');
        else if (/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(body)) {
          body = Buffer.from(body, 'base64');
        }
        else throw fail('INVALID_RESPONSE');
      }
      require(body instanceof Uint8Array && body.byteLength > 0, 'INVALID_RESPONSE');
      const bytes = Buffer.from(body);
      const type = imageContentType(bytes);
      require(type, 'INVALID_RESPONSE');
      require(bytes.byteLength <= MAX_CACHED_IMAGE_BYTES, 'LIMIT');
      guard(call.signal);
      while (images.size >= MAX_CACHED_IMAGES || imageBytes + bytes.byteLength > MAX_CACHED_IMAGE_BYTES) {
        const oldest = images.keys().next().value;
        if (oldest === undefined) break;
        dropImage(oldest);
      }
      const reference = `/v1/source/images/${randomUUID()}`;
      images.set(reference, { installationId: params.installationId,
        generation: call.generation, body: bytes, type, controller: new AbortController() });
      imageBytes += bytes.byteLength;
      return { reference };
    },
    async 'library.releaseImage'(params) {
      dropImage(params.reference);
      await Promise.all([...streams].filter(stream => stream.reference === params.reference)
        .map(stream => stream.dispose()));
      return { released: true };
    },
  };
  return {
    ready,
    methods,
    interrupt(name, params) {
      if (native) {
        require(!['library.import', 'library.save', 'library.update', 'library.remove'].includes(name),
          'PERMISSION_DENIED');
        return;
      }
      if (!['library.remove', 'library.save'].includes(name) &&
          !(name === 'library.update' && params.patch.enabled === false)) return;
      const item = records.get(params.installationId);
      if (!item || item.revision !== params.expectedRevision) return;
      host.captureInitialization(item.installationId)?.();
    },
    owns: id => records.has(id) || states.has(id) || nativeConfiguration.has(id),
    async readerValues(name, input, context) {
      const { installationId: id, generation, protocol, requestId } = context.scope;
      const state = states.get(id);
      require(protocol === 'legado' && state && input.profile === profile(id, generation) + 'reader', 'PERMISSION_DENIED');
      require(state.generation === generation && (state.trial || state.accepting), 'STALE_SOURCE');
      const signal = AbortSignal.any([context.signal, state.controller.signal, lifetime.signal]);
      assertOpen(signal);
      const reading = name === 'reader.values.read';
      if (!reading) require(state.accepting && !state.trial && requestId > 0, 'PERMISSION_DENIED');
      if (disk) {
        try { return reading ? await disk.readReader(id) : await disk.applyReader(id, input, signal); }
        catch (error) {
          throw new ScriptHostError(error?.code === 'CANCELLED' ? 'CANCELLED'
            : isCorruptStorage(error) ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR');
        }
      }
      // Data-root-free modules keep only this session's values.
      const current = memoryReaderValues.get(id) ?? { revision: 0, values: new Map() };
      if (reading) return { revision: current.revision, entries: [...current.values],
        ...(current.variable === undefined ? {} : { variable: current.variable }) };
      require(input.revision === current.revision && Number.isSafeInteger(current.revision + 1), 'STORAGE_ERROR');
      for (const [key, value] of input.entries) current.values.set(key, value);
      if (input.variable !== undefined) current.variable = input.variable;
      memoryReaderValues.set(id, current);
      return ++current.revision;
    },
    readStorage({ profile: name, key }) {
      const match = /^source:([^:]+):[1-9][0-9]*:([\s\S]*)$/.exec(name);
      require(match && disk, 'STORAGE_ERROR');
      try { return disk.privateValue(match[1], match[2], key); }
      catch (error) { throw new ScriptHostError(isCorruptStorage(error) ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR'); }
    },
    prepareCapability(_name, input, context) {
      const id = context.scope?.installationId, state = states.get(id);
      if (!state) return;
      const generation = context.scope.generation;
      require(!context.signal?.aborted && !lifetime.signal.aborted, 'CANCELLED');
      require(state.trial || state.accepting, 'STALE_SOURCE');
      if (state.generation === undefined && state.trial) seed(id, generation, state);
      require(state.generation === generation, 'STALE_SOURCE');
      const allowedPrefix = profile(id, generation);
      if (input.profile !== undefined) {
        require(typeof input.profile === 'string' && input.profile.startsWith(allowedPrefix), 'PERMISSION_DENIED');
      }
      if (input.cookieProfile !== undefined) {
        require(typeof input.cookieProfile === 'string' && input.cookieProfile.startsWith(allowedPrefix), 'PERMISSION_DENIED');
      }
    },
    async profileChanged(prefix) {
      const match = /^source:([^:]+):([1-9][0-9]*):$/.exec(prefix);
      if (!match || !states.has(match[1])) return;
      const state = states.get(match[1]);
      require(state.generation === Number(match[2]) && (state.trial || state.accepting) &&
        !lifetime.signal.aborted, 'STALE_SOURCE');
      if (state.trial) return;
      await operations.run('library', async () => {
        require(states.get(match[1]) === state && state.accepting && !lifetime.signal.aborted, 'STALE_SOURCE');
        const item = record(match[1]);
        const nextItem = { ...item, private: { ...item.private, ...owner.snapshotProfiles(prefix) } };
        const next = new Map(records).set(item.installationId, nextItem);
        await persist(next);
        // Keep admitted calls' record identity stable during private writes.
        item.private = nextItem.private;
      });
    },
    async image(reference, { signal } = {}) {
      assertOpen(signal);
      const item = images.get(reference);
      require(item, 'SOURCE_NOT_FOUND');
      const state = states.get(item.installationId);
      require(state?.accepting && state.generation === item.generation, 'STALE_SOURCE');
      images.delete(reference);
      images.set(reference, item);
      const body = Readable.from([item.body]);
      const active = signal ? AbortSignal.any([lifetime.signal, signal, item.controller.signal])
        : AbortSignal.any([lifetime.signal, item.controller.signal]);
      let disposal;
      const stream = { installationId: item.installationId, reference,
        dispose: () => disposal ??= (async () => {
          active.removeEventListener('abort', abort);
          const finished = body.closed ? Promise.resolve()
            : new Promise(resolve => body.once('close', resolve));
          body.destroy();
          await finished;
          streams.delete(stream);
        })() };
      const abort = () => { void stream.dispose(); };
      streams.add(stream);
      active.addEventListener('abort', abort, { once: true });
      if (active.aborted) { await stream.dispose(); throw fail('CANCELLED'); }
      return { status: 200, headers: { 'content-type': item.type, 'content-length': item.body.length,
        'cache-control': 'no-store' }, body, dispose: stream.dispose };
    },
    diagnostics() {
      return {
        records: records.size,
        activeStates: states.size,
        cachedImages: images.size,
        cachedImageBytes: imageBytes,
        activeImageStreams: streams.size,
      };
    },
    retireAll() { for (const id of states.keys()) retire(id); },
    clearMemoryCache() {
      const inUse = new Set([...streams].map(stream => stream.reference));
      let clearedImages = 0, freedBytes = 0;
      for (const [reference, item] of [...images]) {
        if (inUse.has(reference)) continue;
        freedBytes += item.body.byteLength;
        dropImage(reference);
        clearedImages++;
      }
      return { clearedImages, freedBytes, remainingImages: images.size };
    },
    async close() {
      await Promise.allSettled([ready, operations.drain()]);
      await Promise.allSettled([...streams].map(stream => stream.dispose()));
      const results = await Promise.allSettled([...states.keys()].map(id => release(id)));
      await disk?.close();
      memoryReaderValues?.clear();
      states.clear(); images.clear(); imageBytes = 0;
      if (native) { records.clear(); nativeConfiguration.clear(); sourceLeases.clear(); privateRecords.clear(); }
      require(results.every(result => result.status === 'fulfilled'), 'SOURCE_CLEANUP');
    },
  };
}

function imageContentType(bytes) {
  if (bytes.length >= 8 && bytes.subarray(0, 8).equals(PNG_MAGIC)) return 'image/png';
  if (bytes[0] === 255 && bytes[1] === 216 && bytes[2] === 255) return 'image/jpeg';
  if (/^GIF8[79]a$/.test(bytes.subarray(0, 6).toString('ascii'))) return 'image/gif';
  if (bytes.subarray(0, 4).toString('ascii') === 'RIFF' &&
      bytes.subarray(8, 12).toString('ascii') === 'WEBP') return 'image/webp';
  if (bytes.subarray(4, 8).toString('ascii') === 'ftyp' &&
      /avif|avis/.test(bytes.subarray(8, 32).toString('ascii'))) return 'image/avif';
  if (bytes.subarray(0, 2).toString('ascii') === 'BM') return 'image/bmp';
  return '';
}
