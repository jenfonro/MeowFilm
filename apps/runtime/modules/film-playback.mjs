import { contracts } from '../contracts.mjs';
import { createPlaybackCache, createPlaybackLibrary, createMediaService, createPlaybackData,
  createPlaybackService, createFilmRulesService, createFilmSearchService,
  fail, object, requireValue } from '@meowfilm/film-playback';

/** App-owned business module, borrowing already-assembled provider owners. */
export function createFilmPlaybackModule({ dataRoot, modules, now = Date.now }) {
  requireValue(Array.isArray(modules));
  const owners = new Map(modules.map(module => [module.id, module]));
  const cache = createPlaybackCache({ now });
  const library = createPlaybackLibrary({ dataRoot });
  const media = createMediaService();
  const data = createPlaybackData({ modules: owners, cache, now, dataRoot });
  const rules = createFilmRulesService({ dataRoot });
  const search = createFilmSearchService({ data, rules, now });
  const service = createPlaybackService({ data, library, media, rules, now });
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
    return reply?.ok === true ? reply.value : null;
  }
  const sourceChanged = (a, b) => ['generation', 'revision', 'state', 'enabled', 'sourceHash', 'contentHash']
    .some(key => a?.[key] !== b?.[key]);
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
  const operations = {
    'session.open': async input => {
      if (!input.searchReference) return service.open(input);
      const reference = input.searchReference;
      requireValue(object(reference));
      const handoff = search.handoff({ ...reference, sources: input.sources });
      // The Node-owned selection, not a displayed title or client cache seed,
      // supplies the initial target, provenance and fixed recognition rules.
      return service.open({ ...input, ...handoff,
        ...(input.resumeTarget ? { target: input.resumeTarget, metadata: null } : {}) },
        { ruleSnapshot: search.ruleSnapshot(reference), searchHandoff: handoff });
    },
    'session.command': input => {
      if (input.action !== 'search.update' || !input.searchReference) return service.command(input);
      const handoff = search.handoff({ ...input.searchReference, sources: input.sources });
      return service.command({ ...input, ...handoff, sessionId: input.sessionId },
        { searchHandoff: handoff });
    },
    'session.snapshot': input => service.snapshot(input),
    'session.release': input => service.release(input),
    'session.attempt': input => service.attempt(input),
    'session.media.release': input => service.releaseMedia(input),
    'settings.get': () => service.settings(),
    'settings.set': input => service.setSettings(input),
    'library.get': () => service.library(),
    'library.remove': input => service.removeLibrary(input),
    'manual.update': input => service.updateManual(input),
    'rules.get': () => rules.snapshot(),
    'rules.set': (input, signal) => rules.set(input, { signal }),
    'rules.restore': (input, signal) => rules.restore(input, { signal }),
    'rules.preview': (input, signal) => rules.preview(input, { signal }),
    'rules.normalize': (input, signal) => rules.normalize(input, { signal }),
    'rules.clear': (input, signal) => rules.clear(input, { signal }),
    'search.open': (input, signal) => search.open(input, { signal }),
    'search.snapshot': input => search.snapshot(input),
    'search.command': input => search.command(input),
    'search.release': input => search.release(input),
    'search.handoff': input => search.handoff(input),
    'data.catalog': (input, signal) => data.catalog(input.source, input.action, input.payload,
      { signal, refresh: input.refresh }),
    'data.search': (input, signal) => data.search(input.source, input.query, input.page, { signal, refresh: input.refresh }),
    'data.detail': (input, signal) => data.detail(input.source, input.detailId, { signal, refresh: input.refresh }),
    'data.metadata': (input, signal) => data.metadata(input.provider, input.path, input.query,
      { signal, imageSize: input.imageSize, refresh: input.refresh }),
    'data.metadata.entry': (input, signal) => data.metadataEntry(input.provider, input.path, input.query,
      { signal, imageSize: input.imageSize, refresh: input.refresh }),
    'data.cache.entry': (input, signal) => data.cachedEntry(input, { signal }).then(value => value ?? null),
    'data.invalidate': async input => {
      requireValue(typeof input.domain === 'string' && input.domain.length > 0);
      await invalidate(input.domain);
      return { invalidated: true };
    },
  };
  return {
    id: 'film', version: spec.version, contract: spec.contract,
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
      const keys = [...path.searchParams.keys()];
      requireValue(keys.length <= 1 && keys.every(key => key === 'after'));
      const after = path.searchParams.has('after') ? Number(path.searchParams.get('after')) : -1;
      requireValue(Number.isSafeInteger(after) && after >= -1);
      return search.has(id) ? search.events(id, after, context) : service.events(id, after, context);
    },
    async beforeMutation(moduleId) {
      return providerState(moduleId);
    },
    async afterMutation(moduleId, operation, input, value, before, succeeded) {
      if (before === undefined) return;
      if (sourceMutations[moduleId]?.has(operation)) {
        const after = operation === 'close' ? { instances: [] } : await providerState(moduleId);
        const old = before?.instances ?? [], next = after?.instances ?? [];
        const ids = input?.installationId ? [input.installationId]
          : [...new Set([...old, ...next].map(row => row.installationId))];
        for (const installationId of ids) {
          if (sourceChanged(old.find(row => row.installationId === installationId),
              next.find(row => row.installationId === installationId)) ||
              succeeded && operation === 'management.sync' && value?.status === 'saved') {
            await invalidate({ kind: 'source', engine: moduleId, installationId }, next);
          }
        }
        await service.updateSourceDisplay(moduleId, next);
      } else if (moduleId === 'pan') {
        const after = await providerState(moduleId);
        const providers = new Set([...(before?.accounts ?? []), ...(after?.accounts ?? [])].map(row => row.provider));
        for (const provider of providers) {
          if (before?.accounts?.find(row => row.provider === provider)?.revision !==
              after?.accounts?.find(row => row.provider === provider)?.revision) {
            await invalidate(provider);
          }
        }
      } else if (['tmdb', 'douban'].includes(moduleId)) {
        const after = await providerState(moduleId);
        if (after?.revision !== before?.revision || succeeded && after?.revision === undefined) await invalidate(moduleId, undefined, true);
      }
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        // Revoke in-flight producers at application shutdown, not on page release.
        const results = await Promise.allSettled([data.close(), cache.close(), service.close(), media.close(),
          search.close(), rules.close()]);
        await library.close();
        const failure = results.find(result => result.status === 'rejected');
        if (failure) throw failure.reason;
      })();
    },
  };
}
