import { randomUUID, createHash } from 'node:crypto';
import { Readable } from 'node:stream';
import { isUtf8 } from 'node:buffer';
import { PrivateDocuments, SCRIPT_LIMITS } from '@meowfilm/script-host';
import { normalizeSourceDefinition } from '@meowfilm/source-runtime';
import { InstallationOperations } from './source-candidates.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const emptyPrivate = () => ({ cookies: [], storage: [] });
const profile = (id, generation) => `source:${id}:${generation}:`;
const bool = (value, fallback) => value === undefined ? fallback
  : typeof value === 'string' ? !['false', '0', ''].includes(value.trim().toLowerCase()) : Boolean(value);
const hash = value => createHash('sha256').update(value).digest('hex');

/** The single permanent source repository; native clients only hold records. */
export function createSourceLibrary({ dataRoot, documents, host, owner, web, network,
  fail, errorCode, guard, lifetime }) {
  const disk = documents ?? (dataRoot === undefined ? null : new PrivateDocuments(dataRoot, 'source'));
  const records = new Map(), states = new Map(), images = new Map(), streams = new Set();
  const operations = new InstallationOperations();
  let readFailure;

  const require = (value, code = 'INVALID_REQUEST') => { if (!value) throw fail(code); };
  const live = id => host.list().find(item => item.installationId === id);
  function record(id, revision) {
    const item = records.get(id);
    require(item, 'SOURCE_NOT_FOUND');
    if (revision !== undefined) require(item.revision === revision, 'STALE_SOURCE');
    return item;
  }
  function publicRecord(item) {
    const { installationId, revision, name, sourceUrl, group, order, enabled, enabledExplore,
      protocol, actions } = item;
    const state = states.get(installationId);
    const raw = definitionObject(item.definition);
    const number = (key, fallback) => Number.isFinite(Number(raw[key])) ? Number(raw[key]) : fallback;
    return { installationId, revision, name, sourceUrl, group, order, enabled, enabledExplore,
      protocol, weight: number('weight', 0), lastUpdateTime: number('lastUpdateTime', 0),
      respondTime: number('respondTime', 180000),
      hasExplore: Boolean(raw.exploreUrl || raw.ruleFindUrl || actions.includes('explore')),
      actions: [...actions], ...(state?.errorCode ? { errorCode: state.errorCode } : {}) };
  }
  function persist(next = records) {
    if (readFailure) throw readFailure;
    if (!disk) return;
    const bytes = Buffer.from(JSON.stringify({ schema: 1, sources: [...next.values()] }));
    let previous, readComplete = false;
    try {
      previous = disk.read('library.json'); readComplete = true;
      disk.write('library.json', bytes);
    }
    catch {
      // A writer may fail after rename. Restore the previous committed bytes,
      // and fence further operations if the storage owner cannot establish them.
      try {
        if (!readComplete) throw fail('STORAGE_ERROR');
        const current = disk.read('library.json');
        if (!Buffer.from(current ?? []).equals(Buffer.from(previous ?? []))) {
          if (previous) disk.write('library.json', previous);
          else disk.remove('library.json');
        }
      } catch { readFailure = fail('STORAGE_ERROR'); }
      throw fail('STORAGE_ERROR');
    }
  }
  function parseDefinition(definition) {
    require(typeof definition === 'string' && definition.trim().length > 0);
    require(Buffer.byteLength(definition) <= SCRIPT_LIMITS.maxSourceBytes, 'LIMIT');
    const normalized = normalizeSourceDefinition(definition.replace(/^\uFEFF/, ''));
    require(['legado', 'qysg'].includes(normalized.protocol), 'UNSUPPORTED_PROTOCOL');
    return normalized;
  }
  function definitionObject(definition) {
    let value;
    try { value = JSON.parse(definition); } catch { value = definition; }
    if (Array.isArray(value)) [value] = value;
    return object(value) ? value : { html: value };
  }
  function managedDefinition(item) {
    const value = { ...definitionObject(item.definition), bookSourceName: item.name,
      bookSourceUrl: item.sourceUrl, bookSourceGroup: item.group, customOrder: item.order,
      enabled: item.enabled, enabledExplore: item.enabledExplore };
    if (item.protocol === 'yiciyuan' || Object.hasOwn(value, 'enable')) value.enable = item.enabled;
    if (item.protocol === 'yiciyuan' || Object.hasOwn(value, 'serialNumber')) value.serialNumber = item.order;
    return value;
  }
  function runtimeDefinition(item) {
    const normalized = parseDefinition(item.definition);
    Object.assign(normalized.source, { bookSourceName: item.name, bookSourceUrl: item.sourceUrl,
      bookSourceGroup: item.group, customOrder: item.order, enabled: item.enabled,
      enabledExplore: item.enabledExplore });
    return normalized;
  }
  function build(definition, old, fallbackName = '', origin = '') {
    const normalized = parseDefinition(definition);
    definition = definition.replace(/^\uFEFF/, '');
    const source = normalized.source, raw = definitionObject(definition);
    const sourceUrl = String(source.bookSourceUrl ?? old?.sourceUrl ??
      (normalized.protocol === 'qysg' ? origin || `qysg:${fallbackName || hash(definition)}` : '')).trim();
    require(sourceUrl.length > 0);
    const order = raw.customOrder ?? raw.serialNumber ?? old?.order ?? source.customOrder ?? records.size;
    require(Number.isSafeInteger(Number(order)));
    return {
      installationId: old?.installationId ?? randomUUID(), revision: (old?.revision ?? 0) + 1,
      name: String(source.bookSourceName ?? old?.name ?? fallbackName).trim() || sourceUrl,
      sourceUrl, group: String(source.bookSourceGroup ?? old?.group ?? ''),
      order: Number(order), enabled: bool(raw.enabled ?? raw.enable,
        old?.enabled ?? bool(source.enabled, true)),
      enabledExplore: bool(raw.enabledExplore, old?.enabledExplore ??
        Boolean(source.enabledExplore ?? (source.exploreUrl || source.ruleFindUrl))),
      protocol: normalized.importedProtocol, actions: old?.actions ?? [],
      definition, private: structuredClone(old?.private ?? emptyPrivate()),
    };
  }
  function validateStored(value) {
    require(object(value) && value.schema === 1 && Array.isArray(value.sources), 'CORRUPT_STORAGE');
    for (const item of value.sources) {
      require(object(item) && typeof item.installationId === 'string' &&
        /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(item.installationId) &&
        !records.has(item.installationId) &&
        ![...records.values()].some(other => other.sourceUrl === item.sourceUrl) &&
        Number.isSafeInteger(item.revision) && item.revision > 0 &&
        ['name', 'sourceUrl', 'group', 'protocol', 'definition'].every(key => typeof item[key] === 'string') &&
        item.sourceUrl.trim().length > 0 &&
        Number.isSafeInteger(item.order) && typeof item.enabled === 'boolean' &&
        typeof item.enabledExplore === 'boolean' && Array.isArray(item.actions) &&
        item.actions.every(action => typeof action === 'string') &&
        object(item.private) && Array.isArray(item.private.cookies) && Array.isArray(item.private.storage),
      'CORRUPT_STORAGE');
      try {
        const normalized = parseDefinition(item.definition);
        require(normalized.importedProtocol === item.protocol, 'CORRUPT_STORAGE');
        for (const kind of ['cookies', 'storage']) {
          for (const entry of item.private[kind]) {
            require(Array.isArray(entry) && entry.length === 2 && typeof entry[0] === 'string' &&
              /^:[A-Za-z0-9._-]+$/.test(`:${entry[0]}`) && Array.isArray(entry[1]), 'CORRUPT_STORAGE');
            for (const pair of entry[1]) {
              require(Array.isArray(pair) && pair.length === 2 && typeof pair[0] === 'string', 'CORRUPT_STORAGE');
              if (kind === 'cookies') {
                const cookie = pair[1];
                require(object(cookie) && ['name', 'value', 'domain', 'path'].every(key =>
                  typeof cookie[key] === 'string') && typeof cookie.hostOnly === 'boolean' &&
                  typeof cookie.secure === 'boolean' && Number.isSafeInteger(cookie.sequence) &&
                  (cookie.httpOnly === undefined || typeof cookie.httpOnly === 'boolean') &&
                  (cookie.sameSite === undefined || [null, 'Lax', 'Strict', 'None'].includes(cookie.sameSite)) &&
                  (cookie.expires === null || Number.isFinite(cookie.expires)), 'CORRUPT_STORAGE');
              }
            }
          }
        }
      } catch { throw fail('CORRUPT_STORAGE'); }
      records.set(item.installationId, item);
    }
  }
  try {
    const bytes = disk?.read('library.json');
    if (bytes) {
      require(isUtf8(bytes), 'CORRUPT_STORAGE');
      validateStored(JSON.parse(bytes.toString('utf8')));
    }
  } catch (error) {
    records.clear();
    readFailure = error?.code === 'STORAGE_ERROR' || error?.code === 'PERMISSION_DENIED'
      ? fail('STORAGE_ERROR') : fail('CORRUPT_STORAGE');
  }
  function assertOpen(signal) {
    guard(signal);
    if (readFailure) throw readFailure;
  }
  function releaseImages(id) {
    for (const [reference, item] of images) {
      if (item.installationId !== id) continue;
      item.controller.abort();
      images.delete(reference);
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
      generation: undefined, private: structuredClone(item.private) };
    states.set(item.installationId, state);
    return state;
  }
  function seed(id, generation, state) {
    if (state.generation === generation) return;
    require(state.generation === undefined, 'STALE_SOURCE');
    state.generation = generation;
    owner.restoreProfiles(profile(id, generation), state.private);
  }
  async function execute(item, state, signal) {
    const normalized = runtimeDefinition(item);
    const source = JSON.stringify(normalized.source);
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
    require(!host.stats().cleanupIssues.some(issue => issue.installationId === item.installationId) &&
      host.cleanupPending(item.installationId) === 0, 'SOURCE_CLEANUP');
    item.actions = [...(current.capabilities?.source?.actions ?? [])];
    item.private = owner.snapshotProfiles(profile(item.installationId, current.generation));
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
        if (current?.state === 'ready' && current.contentHash === hash(JSON.stringify(normalized.source))) {
          seed(id, current.generation, state);
        } else {
          if (current) await host.uninstall(id);
          await execute(structuredClone(old), state, lifetime.signal);
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
      persist(next);
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
    if (readFailure) return;
    for (const saved of records.values()) {
      guard(lifetime.signal);
      const item = structuredClone(saved);
      const state = stage(item);
      if (!item.enabled) { state.trial = false; continue; }
      try {
        await execute(item, state, lifetime.signal);
        persist(new Map(records).set(item.installationId, item));
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
      let target;
      try { target = new URL(params.url); } catch { throw fail('INVALID_REQUEST'); }
      require(['http:', 'https:'].includes(target.protocol) && !target.username && !target.password);
      const response = await network({ url: target.href,
        options: { method: 'GET', encoding: null, timeout: 60_000 }, signal });
      guard(signal);
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
    try { return entriesOf(JSON.parse(input)); }
    catch {
      // HTML is a complete QYSG definition, not a line-delimited document.
      if (input.trimStart().startsWith('<')) return [input];
      const lines = input.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
      try { return lines.flatMap(line => entriesOf(JSON.parse(line))); }
      catch { return [input]; }
    }
  }
  function importCandidate(entry, params) {
    const definition = typeof entry === 'string' ? entry : JSON.stringify(entry);
    return build(definition, undefined, params.fileName, params.url);
  }
  const methods = {
    async 'library.list'(_params, signal) {
      await access(signal);
      return { sources: [...records.values()].sort((a, b) => a.order - b.order ||
        a.name.localeCompare(b.name) || a.installationId.localeCompare(b.installationId)).map(publicRecord) };
    },
    async 'library.get'(params, signal) {
      await access(signal);
      const item = record(params.installationId);
      return { source: publicRecord(item), definition: JSON.stringify(managedDefinition(item)) };
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
            definition: JSON.stringify(managedDefinition(item)) });
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
        const definition = typeof entry === 'string' ? entry : JSON.stringify(entry);
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
      const old = record(params.installationId, params.expectedRevision), patch = params.patch;
      const item = { ...structuredClone(old), ...patch, revision: old.revision + 1 };
      item.definition = JSON.stringify(managedDefinition(item));
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
        persist(next);
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
        persist(next);
        records.delete(old.installationId); states.delete(old.installationId);
        return { removed: true };
      } catch (error) {
        if (!lifetime.signal.aborted) await restore(old);
        throw error;
      }
    }),
    async 'library.export'(params, signal) {
      await access(signal);
      return { text: JSON.stringify(params.installationIds.map(id => managedDefinition(record(id))), null, 2) };
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
      guard(call.signal);
      const reference = `/v1/source/images/${randomUUID()}`;
      images.set(reference, { installationId: params.installationId,
        generation: call.generation, body: bytes, type, controller: new AbortController() });
      return { reference };
    },
    async 'library.releaseImage'(params) {
      const item = images.get(params.reference);
      item?.controller.abort();
      images.delete(params.reference);
      await Promise.all([...streams].filter(stream => stream.reference === params.reference)
        .map(stream => stream.dispose()));
      return { released: true };
    },
  };
  return {
    methods,
    interrupt(name, params) {
      if (!['library.remove', 'library.save'].includes(name) &&
          !(name === 'library.update' && params.patch.enabled === false)) return;
      const item = records.get(params.installationId);
      if (!item || item.revision !== params.expectedRevision) return;
      host.captureInitialization(item.installationId)?.();
    },
    owns: id => records.has(id) || states.has(id),
    prepareCapability(_name, input, context) {
      const id = context.scope?.installationId, state = states.get(id);
      if (!state) return;
      const generation = context.scope.generation;
      require(!context.signal?.aborted && !lifetime.signal.aborted, 'CANCELLED');
      require(state.trial || state.accepting, 'STALE_SOURCE');
      if (state.generation === undefined && state.trial) seed(id, generation, state);
      require(state.generation === generation, 'STALE_SOURCE');
      const name = input.profile ?? input.cookieProfile;
      if (name) require(name.startsWith(profile(id, generation)), 'PERMISSION_DENIED');
    },
    profileChanged(prefix) {
      const match = /^source:([^:]+):([1-9][0-9]*):$/.exec(prefix);
      if (!match || !states.has(match[1])) return;
      const state = states.get(match[1]);
      require(state.generation === Number(match[2]) && (state.trial || state.accepting) &&
        !lifetime.signal.aborted, 'STALE_SOURCE');
      if (state.trial) return;
      const item = record(match[1]);
      const nextItem = { ...item, private: owner.snapshotProfiles(prefix) };
      const next = new Map(records).set(item.installationId, nextItem);
      persist(next);
      // Keep admitted calls' record identity stable during private writes.
      item.private = nextItem.private;
    },
    async image(reference, { signal } = {}) {
      assertOpen(signal);
      const item = images.get(reference);
      require(item, 'SOURCE_NOT_FOUND');
      const state = states.get(item.installationId);
      require(state?.accepting && state.generation === item.generation, 'STALE_SOURCE');
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
    retireAll() { for (const id of states.keys()) retire(id); },
    async close() {
      await Promise.allSettled([ready, operations.drain()]);
      await Promise.allSettled([...streams].map(stream => stream.dispose()));
      const results = await Promise.allSettled([...states.keys()].map(id => release(id)));
      states.clear(); images.clear();
      require(results.every(result => result.status === 'fulfilled'), 'SOURCE_CLEANUP');
    },
  };
}

function imageContentType(bytes) {
  if (bytes.length >= 8 && bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) return 'image/png';
  if (bytes[0] === 255 && bytes[1] === 216 && bytes[2] === 255) return 'image/jpeg';
  if (/^GIF8[79]a$/.test(bytes.subarray(0, 6).toString('ascii'))) return 'image/gif';
  if (bytes.subarray(0, 4).toString('ascii') === 'RIFF' &&
      bytes.subarray(8, 12).toString('ascii') === 'WEBP') return 'image/webp';
  if (bytes.subarray(4, 8).toString('ascii') === 'ftyp' &&
      /avif|avis/.test(bytes.subarray(8, 32).toString('ascii'))) return 'image/avif';
  if (bytes.subarray(0, 2).toString('ascii') === 'BM') return 'image/bmp';
  return '';
}
