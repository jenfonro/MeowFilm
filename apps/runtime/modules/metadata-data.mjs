import { performance } from 'node:perf_hooks';
import { contracts } from '../contracts.mjs';
import { createMetadataSettings, metadataFailure, object } from './metadata/settings.mjs';
import { createMetadataRequest, createMetadataImageTransport } from './metadata/network.mjs';
import { requestTMDB, tmdbImageTarget } from './metadata/tmdb.mjs';

function createMetadataModule(id, { dataRoot, request, nativeSettings, debug } = {}) {
  const activeRequest = request ?? createMetadataRequest({ debug, provider: id });
  if (typeof activeRequest !== 'function') throw metadataFailure('INVALID_REQUEST');
  const spec = contracts[id], store = createMetadataSettings(dataRoot, id, nativeSettings);
  const lifetime = new AbortController(), active = new Set(), streams = new Set();
  let configuration = new AbortController();
  const images = createMetadataImageTransport();
  let closed = false, closing, cleanupFailure;
  const failure = error => ({ ok: false, contract: spec.contract,
    code: spec.codes.has(error?.code) ? error.code : 'UPSTREAM_FORMAT' });
  function track(work) {
    active.add(work);
    work.finally(() => active.delete(work)).catch(() => {});
    return work;
  }
  function signalFor(context) {
    if (closed) throw metadataFailure('CLOSED');
    const signal = AbortSignal.any([lifetime.signal, configuration.signal, ...(context?.signal ? [context.signal] : [])]);
    if (signal.aborted) throw metadataFailure('CANCELLED');
    return signal;
  }
  const methods = {};
  function method(name, handler) {
    methods[spec.prefix + name] = (input, context) => track((async () => {
      const startedAt = debug ? performance.now() : 0;
      const cat = id === 'douban' && name === 'search' ? 'search' : 'film-catalog';
      const sub = `${id}.${name}`;
      debug?.emit({ level: 'debug', cat, sub, event: 'request.start', data: { input } });
      try {
        const signal = signalFor(context);
        if (!object(input)) throw metadataFailure('INVALID_REQUEST');
        const value = await handler(input, signal);
        if (signal.aborted) throw metadataFailure('CANCELLED');
        debug?.emit({
          level: 'debug',
          cat,
          sub,
          event: 'request.end',
          durationMs: debug ? performance.now() - startedAt : undefined,
          data: { value },
        });
        return { ok: true, contract: spec.contract, value };
      } catch (error) {
        const reply = failure(error);
        debug?.emit({
          level: 'warn',
          cat,
          sub,
          event: 'request.error',
          durationMs: debug ? performance.now() - startedAt : undefined,
          data: { input, code: reply.code },
          error,
        });
        return reply;
      }
    })());
  }
  method('settings.get', () => store.get());
  method('settings.update', input => store.update(input));
  method('request', async (input, signal) => {
    const settings = await store.read();
    return (id === 'douban' ? (await import('./metadata/douban.mjs')).requestDouban : requestTMDB)(input,
      { settings, request: activeRequest, signal });
  });
  if (id === 'douban') method('search', async (input, signal) => {
    const settings = await store.read();
    return (await import('./metadata/douban.mjs')).searchDouban(input, { settings, request: activeRequest, signal });
  });
  return Object.freeze({
    id, version: spec.version, contract: spec.contract, methods, ready: store.ready,
    diagnostics() {
      return {
        closed,
        activeRequests: active.size,
        activeImageStreams: streams.size,
      };
    },
    settingsChanged() {
      debug?.emit({ level: 'info', cat: 'film-catalog', sub: id, event: 'settings.changed' });
      store.invalidate();
      configuration.abort();
      configuration = new AbortController();
    },
    image(path, context) {
      return track((async () => {
        const startedAt = debug ? performance.now() : 0;
        try {
          const signal = signalFor(context);
          const settings = await store.read();
          const target = (id === 'douban' ? (await import('./metadata/douban.mjs')).doubanImageTarget
            : tmdbImageTarget)(path, settings);
          const result = await images.open(target, signal);
          debug?.emit({
            level: 'debug',
            cat: 'film-catalog',
            sub: `${id}.image`,
            event: 'image.open',
            durationMs: debug ? performance.now() - startedAt : undefined,
            data: { path, targetUrl: target?.url ?? null, headers: result.headers },
          });
          const dispose = () => {
            const done = result.dispose();
            done.catch(() => { cleanupFailure = metadataFailure('NETWORK_ERROR'); })
              .finally(() => streams.delete(dispose));
            return done;
          };
          streams.add(dispose);
          if (signal.aborted) {
            await dispose();
            throw metadataFailure('CANCELLED');
          }
          return { ...result, dispose };
        } catch (error) {
          const reply = failure(error);
          debug?.emit({
            level: 'warn',
            cat: 'film-catalog',
            sub: `${id}.image`,
            event: 'image.error',
            durationMs: debug ? performance.now() - startedAt : undefined,
            data: { path, code: reply.code },
            error,
          });
          return reply;
        }
      })());
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        lifetime.abort();
        await Promise.allSettled([...active]);
        const results = await Promise.allSettled([
          ...[...streams].map(dispose => Promise.resolve().then(dispose)), images.close(), store.close(),
        ]);
        if (cleanupFailure || results.some(result => result.status === 'rejected')) {
          throw metadataFailure('NETWORK_ERROR');
        }
      })();
    },
  });
}

export const createTMDBDataModule = options => createMetadataModule('tmdb', options);
export const createDoubanDataModule = options => createMetadataModule('douban', options);
