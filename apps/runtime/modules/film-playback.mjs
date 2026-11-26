import { contracts } from '../contracts.mjs';
import { isDeepStrictEqual } from 'node:util';
import { performance } from 'node:perf_hooks';
import {
  createPlaybackCache,
  createPlaybackLibrary,
  createMediaService,
  createPlaybackData,
  createPlaybackService,
  createFilmRulesService,
  createFilmSearchService,
  createFilmCatalog,
  createFilmSourceRegistry,
  fail,
  object,
  requireValue,
} from '@meowfilm/film-playback';

/** App-owned business module, borrowing already-assembled provider owners. */
export function createFilmPlaybackModule({ dataRoot, modules, nativeSettings, debug }) {
  requireValue(Array.isArray(modules));
  const owners = new Map(modules.map(module => [module.id, module]));
  const cache = createPlaybackCache();
  const library = createPlaybackLibrary({ dataRoot, ...(nativeSettings ? {
    nativePreferences: () => {
      const document = nativeSettings.read('film.preferences');
      if (document === null) return {};
      if (!object(document) || !object(document.preferences)) throw fail('CORRUPT_STORAGE');
      return document.preferences;
    },
  } : {}) });
  const sourceRegistry = createFilmSourceRegistry({ modules: owners, nativeSettings });
  const catalog = createFilmCatalog();
  const media = createMediaService({ debug });
  const data = createPlaybackData({ modules: owners, cache, dataRoot, debug });
  const rules = createFilmRulesService({ dataRoot, nativeSettings, debug });
  const search = createFilmSearchService({ data, rules, catalog, sourceRegistry, debug });
  const service = createPlaybackService({ data, library, media, rules, sourceRegistry, catalog, debug });
  const spec = contracts.film;
  let closed = false, closing;

  const sourceMutations = {
    cat: new Set(['refresh', 'install', 'import', 'edit', 'update', 'restart', 'enable', 'uninstall']),
    source: new Set(['load', 'unload', 'close']),
  };
  const panMutations = new Set(['settings.set', 'settings.clear', 'refresh', 'login.poll']);
  const observesMutation = (moduleId, operation) => sourceMutations[moduleId]?.has(operation) ||
    moduleId === 'pan' && panMutations.has(operation) ||
    ['tmdb', 'douban'].includes(moduleId) && operation === 'settings.update';

  async function providerState(moduleId) {
    const owner = owners.get(moduleId);
    await owner.ready;
    const method = {
      cat: 'cat.script.status',
      source: 'source.runtime.status',
      pan: 'pan.settings.get',
      tmdb: 'tmdb.data.settings.get',
      douban: 'douban.data.settings.get',
    }[moduleId];
    const reply = await owner.methods[method]({});
    if (reply?.ok !== true) return null;
    return ['tmdb', 'douban'].includes(moduleId) ? { revision: reply.value?.revision } : reply.value;
  }

  const providerStates = new Map(nativeSettings ? ['cat', 'source', 'pan', 'tmdb', 'douban']
    .filter(id => owners.has(id)).map(id => [id, providerState(id).catch(() => null)]) : []);

  const sourceChanged = (a, b) => ['generation', 'revision', 'state', 'enabled', 'sourceHash', 'contentHash']
    .some(key => a?.[key] !== b?.[key]) || Boolean(a) !== Boolean(b);

  const sourceMatches = (scope, domain) => object(domain) ?
    (domain.kind === 'source' || domain.kind === 'script-account' && !scope.accountProvider) &&
    scope.sourceEngine === (domain.kind === 'script-account' ? 'cat' : domain.engine) &&
    (domain.installationId === undefined || scope.sourceInstallationId === domain.installationId) &&
    (domain.generation === undefined || scope.sourceGeneration === domain.generation)
    : domain === 'source' ? ['cat', 'source'].includes(scope.sourceEngine)
      : domain === 'pan' ? Boolean(scope.accountProvider)
        : scope.sourceKey === domain || scope.accountProvider === domain;

  async function invalidate(domain, instances, discardCacheError = false) {
    const results = await Promise.allSettled([
      domain === 'film.video-sources' ? Promise.resolve() : data.invalidate(domain),
      service.invalidate(domain, instances),
      search.invalidate(domain),
      domain === 'film.video-sources' ? Promise.resolve() : media.invalidate(scope => sourceMatches(scope, domain)),
    ]);
    const failure = results.find((result, index) => result.status === 'rejected' &&
      !(discardCacheError && index === 0 && result.reason?.code === 'STORAGE_ERROR'));
    if (failure) throw failure.reason;
  }

  async function applyProviderChange(moduleId, before, after, { succeeded } = {}) {
    const work = [];
    let instances;
    if (moduleId === 'cat' || moduleId === 'source') {
      const old = before?.instances ?? [], next = after?.instances ?? [];
      instances = next;
      const ids = new Set([...old, ...next].map(row => row.installationId));
      const routingChanged = moduleId === 'cat' && !isDeepStrictEqual(before?.routing, after?.routing);
      for (const installationId of ids) {
        if (routingChanged || sourceChanged(old.find(row => row.installationId === installationId),
            next.find(row => row.installationId === installationId))) {
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
      const searchEntry = input.searchReference ? search.resolveEntry(input.searchReference) : null;
      return service.open(input, searchEntry ? { searchEntry } : undefined);
    },
    'session.command': input => service.command(input),
    'session.snapshot': input => service.snapshot(input),
    'session.release': input => service.release(input),
    'session.attempt': input => service.attempt(input),
    'session.media.release': input => service.releaseMedia(input),
    'settings.get': () => service.settings(),
    'settings.set': input => service.setSettings(input),
    'library.get': async input => {
      const value = await service.library(input);
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
    'data.catalog': async (input, signal) => {
      const source = await sourceRegistry.resolveSingleSource(input.source, { signal });
      const value = await data.catalog(source, input.action, input.payload, { signal, refresh: input.refresh });
      return catalog.sourceRows(source, value);
    },
    'data.search': async (input, signal) => {
      const source = await sourceRegistry.resolveSingleSource(input.source, { signal });
      const value = await data.search(source, input.query, input.page, { signal, refresh: input.refresh });
      return {
        ...value,
        raw: catalog.sourceRows(source, value.raw),
        items: catalog.sourceRows(source, { items: value.items }).items,
      };
    },
    'data.detail': async (input, signal) => {
      const source = await sourceRegistry.resolveSingleSource(input.source, { signal });
      const value = await data.detail(source, input.detailId, { signal, refresh: input.refresh });
      return catalog.directory(source, value);
    },
    'data.metadata': (input, signal) => data.metadata(input.provider, input.path, input.query,
      { signal, imageSize: input.imageSize, refresh: input.refresh })
      .then(value => catalog.metadata(input.provider, input.path, value)),
    'data.metadata.entry': (input, signal) => data.metadataEntry(input.provider, input.path, input.query,
      { signal, imageSize: input.imageSize, refresh: input.refresh })
      .then(entry => ({ ...entry, value: catalog.metadata(input.provider, input.path, entry.value) })),
    'data.invalidate': async input => {
      requireValue(typeof input.domain === 'string' && input.domain.length > 0);
      await invalidate(input.domain);
      return { invalidated: true };
    },
  };

  return {
    id: 'film',
    ready: library.ready,
    version: spec.version,
    contract: spec.contract,
    diagnostics() {
      return {
        closed,
        cache: cache.diagnostics(),
        data: data.diagnostics(),
        media: media.diagnostics(),
        search: search.diagnostics(),
        playback: service.diagnostics(),
        rules: rules.diagnostics(),
      };
    },
    clearMemoryCache: () => cache.clearMemory(),
    async settingsChanged(block) {
      debug?.emit({ level: 'info', cat: 'film-playback', sub: 'film.settings', event: 'settings.changed', data: { block } });
      if (block === 'film.rules') await rules.settingsChanged();
      else if (block === 'film.preferences') await service.settingsChanged();
      else if (block === 'film.video-sources') await invalidate('film.video-sources');
      else if (block === 'pan.accounts') await observeProviderChange('pan');
      else if (block === 'tmdb' || block === 'douban') await observeProviderChange(block);
      else if (block === 'cat.sources') await observeProviderChange('cat');
    },
    observesMutation,
    accountSaved: ({ installationId, generation, provider }) =>
      invalidate({ kind: 'script-account', installationId, generation, provider }),
    methods: Object.fromEntries(Object.entries(operations).map(([operation, handler]) =>
      [spec.prefix + operation, async (input, context) => {
        if (closed) throw fail('CLOSED');
        if (context?.signal?.aborted) throw fail('CANCELLED');
        requireValue(object(input));
        await library.ready;
        if (closed) throw fail('CLOSED');
        if (context?.signal?.aborted) throw fail('CANCELLED');
        if (!debug) {
          return { ok: true, contract: spec.contract, value: await handler(input, context?.signal) };
        }
        const startedAt = performance.now();
        const cat = operation.startsWith('search.') || operation === 'data.search'
          ? 'search'
          : operation.startsWith('data.') ? 'film-catalog' : 'film-playback';
        const sub = `film.${operation}`;
        debug.emit({ level: 'debug', cat, sub, event: 'film.call.start', data: { operation, input } });
        try {
          const value = await handler(input, context?.signal);
          debug.emit({
            level: 'debug',
            cat,
            sub,
            event: 'film.call.end',
            durationMs: performance.now() - startedAt,
            data: { operation, value },
          });
          return { ok: true, contract: spec.contract, value };
        } catch (error) {
          debug.emit({
            level: 'warn',
            cat,
            sub,
            event: 'film.call.error',
            durationMs: performance.now() - startedAt,
            data: { operation, input, code: error?.code ?? null },
            error,
          });
          throw error;
        }
      }])),
    events(path, context) {
      if (closed) throw fail('CLOSED');
      if (context?.signal?.aborted) throw fail('CANCELLED');
      const id = path.pathname.split('/')[4];
      requireValue(path.searchParams.getAll('after').length <= 1);
      const rawAfter = path.searchParams.get('after');
      const after = rawAfter === null ? -1 : (/^(?:-1|0|[1-9][0-9]*)$/.test(rawAfter) ? Number(rawAfter) : NaN);
      requireValue(Number.isSafeInteger(after) && after >= -1);
      const isSearch = search.has(id);
      debug?.emit({
        level: 'debug',
        cat: isSearch ? 'search' : 'film-playback',
        sub: isSearch ? 'film.search.events' : 'film.playback.events',
        event: 'events.subscribe',
        data: { sessionId: id, after },
      });
      return isSearch ? search.events(id, after, context?.signal) : service.events(id, after, context?.signal);
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
        const stage1 = await Promise.allSettled([
          service.close(), search.close(), media.close(), rules.close(),
        ]);
        const stage2 = await Promise.allSettled([
          data.close(), cache.close(), ...providerStates.values(),
        ]);
        const stage3 = await Promise.allSettled([library.close()]);
        const failure = [...stage1, ...stage2, ...stage3].find(result => result.status === 'rejected');
        if (failure) throw failure.reason;
      })();
    },
  };
}
