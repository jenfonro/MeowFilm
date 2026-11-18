import { contracts } from '../contracts.mjs';
import { createPlaybackCache, createPlaybackLibrary, createMediaService, createPlaybackData,
  createPlaybackService, createFilmRulesService, createFilmSearchService, createFilmCatalog,
  fail, object, requireValue } from '@meowfilm/film-playback';

/** App-owned business module, borrowing already-assembled provider owners. */
export function createFilmPlaybackModule({ dataRoot, modules, now = Date.now, nativeSettings }) {
  requireValue(Array.isArray(modules));
  const owners = new Map(modules.map(module => [module.id, module]));
  const cache = createPlaybackCache({ now });
  const library = createPlaybackLibrary({ dataRoot, ...(nativeSettings ? {
    nativePreferences: () => nativeSettings.read('film.preferences')?.preferences ?? {}
  } : {}) });
  const catalog = createFilmCatalog({ library });
  const media = createMediaService();
  const data = createPlaybackData({ modules: owners, cache, now, dataRoot });
  const rules = createFilmRulesService({ dataRoot, nativeSettings });
  const search = createFilmSearchService({ data, rules, catalog, now });
  const service = createPlaybackService({ data, library, media, rules, catalog, now });
  const spec = contracts.film;
  let closed = false, closing;
  const sourceMutations = {
    cat: new Set(['refresh', 'install', 'import', 'edit', 'update', 'restart', 'enable', 'uninstall', 'management.sync']),
    source: new Set(['load', 'unload', 'close']),
  };
  const panMutations = new Set(['settings.set', 'settings.clear', 'refresh', 'login.poll']);
  const observesMutation = (moduleId, operation) => sourceMutations[moduleId]?.has(operation) ||
    moduleId === 'pan' && panMutations.has(operation) ||
    ['tmdb', 'douban'].includes(moduleId) && operation === 'settings.update';
  async function providerState(moduleId) {
    const owner = owners.get(moduleId);
    const method = { cat: 'cat.script.status', source: 'source.runtime.status',
      pan: 'pan.settings.get', tmdb: 'tmdb.data.settings.get', douban: 'douban.data.settings.get' }[moduleId];
    const reply = await owner.methods[method]({});
    if (reply?.ok !== true) return null;
    return ['tmdb', 'douban'].includes(moduleId) ? { revision: reply.value?.revision } : reply.value;
  }
  const providerStates = new Map(nativeSettings ? ['cat', 'source', 'pan', 'tmdb', 'douban']
    .filter(id => owners.has(id)).map(id => [id, providerState(id).catch(() => null)]) : []);
  const sourceChanged = (a, b) => ['generation', 'revision', 'state', 'enabled', 'sourceHash', 'contentHash']
    .some(key => a?.[key] !== b?.[key]) || !!a !== !!b;
  const sourceMatches = (scope, domain) => object(domain) ? domain.kind === 'source' &&
    scope.sourceEngine === domain.engine &&
    (domain.installationId === undefined || scope.sourceInstallationId === domain.installationId)
    : domain === 'source' ? ['cat', 'source'].includes(scope.sourceEngine)
      : domain === 'pan' ? !!(scope.accountProvider || scope.fileProvider)
        : scope.sourceKey === domain || scope.accountProvider === domain || scope.fileProvider === domain;
  async function invalidate(domain, instances, discardCacheError = false) {
    // Both calls revoke their generation synchronously before awaiting drain.
    // Native publication and direct/cache work cannot remain valid while a
    // different owner is already waiting for a slow transport to close.
    const results = await Promise.allSettled([
      data.invalidate(domain), service.invalidate(domain, instances), search.invalidate(domain),
      media.invalidate(scope => sourceMatches(scope, domain)),
    ]);
    const failure = results.find((result, index) => result.status === 'rejected' &&
      !(discardCacheError && index === 0 && result.reason?.code === 'STORAGE_ERROR'));
    if (failure) throw failure.reason;
  }
  async function applyProviderChange(moduleId, before, after, { operation, input, value, succeeded } = {}) {
    const work = [];
    let instances;
    if (moduleId === 'cat' || moduleId === 'source') {
      const old = before?.instances ?? [], next = after?.instances ?? [];
      instances = next;
      const ids = new Set([...old, ...next].map(row => row.installationId));
      for (const installationId of ids) {
        if (sourceChanged(old.find(row => row.installationId === installationId),
            next.find(row => row.installationId === installationId)) ||
            succeeded && operation === 'management.sync' && value?.status === 'saved' &&
              installationId === input.installationId) {
          work.push(invalidate({ kind: 'source', engine: moduleId, installationId }, next));
        }
      }
    } else if (moduleId === 'pan') {
      const providers = new Set([...(before?.accounts ?? []), ...(after?.accounts ?? [])].map(row => row.provider));
      for (const provider of providers) {
        if (before?.accounts?.find(row => row.provider === provider)?.revision !==
            after?.accounts?.find(row => row.provider === provider)?.revision) work.push(invalidate(provider));
      }
    } else if (after?.revision !== before?.revision || succeeded && after?.revision === undefined) {
      work.push(invalidate(moduleId, undefined, true));
    }
    const results = await Promise.allSettled(work);
    const failure = results.find(result => result.status === 'rejected');
    if (failure) throw failure.reason;
    if (instances) await service.updateSourceDisplay(moduleId, instances);
  }
  function observeProviderChange(moduleId, before, change) {
    const previous = providerStates.get(moduleId) ?? Promise.resolve(before);
    const work = previous.then(async state => {
      const after = change?.operation === 'close' ? { instances: [] } : await providerState(moduleId);
      await applyProviderChange(moduleId, state, after, change);
      return after;
    });
    providerStates.set(moduleId, work.catch(() => previous));
    return work;
  }
  const operations = {
    'session.open': async input => {
      const reference = input.searchReference;
      if (reference) requireValue(object(reference));
      const handoff = reference && search.findHandoff({ ...reference, sources: input.sources });
      const value = handoff ? { ...input, ...handoff } : { ...input, searchReference: undefined };
      value.target = catalog.resolve(value.target, value.sources);
      if (value.resumeTarget && !(value.resumeTarget.kind === 'group' && !value.resumeTarget.members?.length))
        value.resumeTarget = catalog.resolve(value.resumeTarget, value.sources);
      return service.open(value, handoff
        ? { ruleSnapshot: search.ruleSnapshot(reference), searchHandoff: handoff } : undefined);
    },
    'session.command': input => {
      if (input.action !== 'search.update' || !input.searchReference) return service.command(input);
      const handoff = search.findHandoff({ ...input.searchReference, sources: input.sources });
      if (!handoff) return service.snapshot(input);
      return service.command({ ...input, ...handoff, sessionId: input.sessionId },
        { searchHandoff: handoff });
    },
    'session.snapshot': input => service.snapshot(input),
    'session.release': input => service.release(input),
    'session.attempt': input => service.attempt(input),
    'session.media.release': input => service.releaseMedia(input),
    'settings.get': () => service.settings(),
    'settings.set': input => service.setSettings(input),
    'library.get': () => {
      const value = service.library();
      return { ...value, history: value.history.map(catalog.history) };
    },
    'library.remove': input => service.removeLibrary(input),
    'manual.update': input => service.updateManual(input),
    'rules.get': () => rules.snapshot(),
    'rules.set': (input, signal) => rules.set(input, { signal }),
    'rules.group.set': (input, signal) => rules.setGroup(input, { signal }),
    'rules.group.defaults': (input, signal) => rules.groupDefaults(input, { signal }),
    'rules.group.preview': (input, signal) => rules.previewGroup(input, { signal }),
    'rules.restore': (input, signal) => rules.restore(input, { signal }),
    'rules.preview': (input, signal) => rules.preview(input, { signal }),
    'rules.normalize': (input, signal) => rules.normalize(input, { signal }),
    'rules.clear': (input, signal) => rules.clear(input, { signal }),
    'search.open': (input, signal) => search.open(input, { signal }),
    'search.snapshot': input => search.snapshot(input),
    'search.command': input => search.command(input),
    'search.release': input => search.release(input),
    'search.handoff': input => search.handoff(input),
    'data.target': input => catalog.resolve(input.target, input.sources),
    'data.catalog': (input, signal) => data.catalog(input.source, input.action, input.payload,
      { signal, refresh: input.refresh }).then(value => catalog.sourceRows(input.source, value)),
    'data.search': (input, signal) => data.search(input.source, input.query, input.page, { signal, refresh: input.refresh })
      .then(value => ({ ...value, raw: catalog.sourceRows(input.source, value.raw),
        items: catalog.sourceRows(input.source, { items: value.items }).items })),
    'data.detail': (input, signal) => data.detail(input.source, input.detailId, { signal, refresh: input.refresh })
      .then(value => catalog.directory(input.source, value)),
    'data.metadata': (input, signal) => data.metadata(input.provider, input.path, input.query,
      { signal, imageSize: input.imageSize, refresh: input.refresh })
      .then(value => catalog.metadata(input.provider, input.path, value)),
    'data.metadata.entry': (input, signal) => data.metadataEntry(input.provider, input.path, input.query,
      { signal, imageSize: input.imageSize, refresh: input.refresh })
      .then(entry => ({ ...entry, value: catalog.metadata(input.provider, input.path, entry.value) })),
    'data.cache.entry': (input, signal) => data.cachedEntry(input, { signal }).then(value => value ?? null),
    'data.invalidate': async input => {
      requireValue(typeof input.domain === 'string' && input.domain.length > 0);
      await invalidate(input.domain);
      return { invalidated: true };
    },
  };
  return {
    id: 'film', version: spec.version, contract: spec.contract,
    async settingsChanged(block) {
      if (block === 'film.rules') await rules.settingsChanged();
      else if (block === 'film.preferences') await service.settingsChanged();
      else if (block === 'pan.accounts') await observeProviderChange('pan');
      else if (block === 'tmdb' || block === 'douban') await observeProviderChange(block);
      else if (block === 'cat.sources') await observeProviderChange('cat');
    },
    observesMutation,
    methods: Object.fromEntries(Object.entries(operations).map(([operation, handler]) =>
      [spec.prefix + operation, async (input, context) => {
        if (closed) throw fail('CLOSED');
        if (context?.signal?.aborted) throw fail('CANCELLED');
        requireValue(object(input));
        return { ok: true, contract: spec.contract, value: await handler(input, context?.signal) };
      }])),
    events(path, context) {
      if (closed) throw fail('CLOSED');
      const id = path.pathname.split('/')[4];
      requireValue(path.searchParams.getAll('after').length <= 1);
      const after = path.searchParams.has('after') ? Number(path.searchParams.get('after')) : -1;
      requireValue(Number.isSafeInteger(after) && after >= -1);
      return search.has(id) ? search.events(id, after, context) : service.events(id, after, context);
    },
    async beforeMutation(moduleId) {
      return providerState(moduleId);
    },
    async afterMutation(moduleId, operation, input, value, before, succeeded) {
      if (before === undefined) return;
      await observeProviderChange(moduleId, before, { operation, input, value, succeeded });
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        // Revoke in-flight producers at application shutdown, not on page release.
        const results = await Promise.allSettled([data.close(), cache.close(), service.close(), media.close(),
          search.close(), rules.close(), ...providerStates.values()]);
        await library.close();
        const failure = results.find(result => result.status === 'rejected');
        if (failure) throw failure.reason;
      })();
    },
  };
}
