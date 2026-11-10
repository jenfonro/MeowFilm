import { contracts } from '../contracts.mjs';
import { createPlaybackCache, createPlaybackLibrary, createMediaService, createPlaybackData,
  createPlaybackService, fail, object, requireValue } from '@meowfilm/film-playback';

/** App-owned business module, borrowing already-assembled provider owners. */
export function createFilmPlaybackModule({ dataRoot, modules, now = Date.now }) {
  requireValue(Array.isArray(modules));
  const owners = new Map(modules.map(module => [module.id, module]));
  const cache = createPlaybackCache({ now });
  const library = createPlaybackLibrary({ dataRoot });
  const media = createMediaService();
  const data = createPlaybackData({ modules: owners, cache, now });
  const service = createPlaybackService({ data, library, media, now });
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
  async function invalidate(domain, instances) {
    // Both calls revoke their generation synchronously before awaiting drain.
    // Native publication and direct/cache work cannot remain valid while a
    // different owner is already waiting for a slow transport to close.
    const results = await Promise.allSettled([
      data.invalidate(domain), service.invalidate(domain, instances),
      media.invalidate(scope => sourceMatches(scope, domain)),
    ]);
    const failure = results.find(result => result.status === 'rejected');
    if (failure) throw failure.reason;
  }
  const operations = {
    'session.open': input => service.open(input),
    'session.command': input => service.command(input),
    'session.snapshot': input => service.snapshot(input),
    'session.release': input => service.release(input),
    'session.attempt': input => service.attempt(input),
    'session.media.release': input => service.releaseMedia(input),
    'settings.get': () => service.settings(),
    'settings.set': input => service.setSettings(input),
    'library.get': () => service.library(),
    'library.remove': input => service.removeLibrary(input),
    'manual.update': input => service.updateManual(input),
    'data.search': (input, signal) => data.search(input.source, input.query, input.page, { signal }),
    'data.detail': (input, signal) => data.detail(input.source, input.detailId, { signal }),
    'data.metadata': (input, signal) => data.metadata(input.provider, input.path, input.query,
      { signal, imageSize: input.imageSize }),
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
      return service.events(id, after, context);
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
        service.updateSourceDisplay(moduleId, next);
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
        if (after?.revision !== before?.revision || succeeded && after?.revision === undefined) await invalidate(moduleId);
      }
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        // Revoke in-flight producers at application shutdown, not on page release.
        const results = await Promise.allSettled([data.close(), cache.close(), service.close(), media.close()]);
        await library.close();
        const failure = results.find(result => result.status === 'rejected');
        if (failure) throw failure.reason;
      })();
    },
  };
}
