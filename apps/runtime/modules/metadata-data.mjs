import { contracts } from '../contracts.mjs';
import { createMetadataSettings, metadataFailure, object } from './metadata/settings.mjs';
import { createMetadataRequest, openMetadataImage } from './metadata/network.mjs';
import { requestTMDB, tmdbImageTarget } from './metadata/tmdb.mjs';
import { requestDouban, searchDouban, doubanImageTarget } from './metadata/douban.mjs';

function createMetadataModule(id, { dataRoot, request = createMetadataRequest() } = {}) {
  if (typeof request !== 'function') throw metadataFailure('INVALID_REQUEST');
  const spec = contracts[id], store = createMetadataSettings(dataRoot, id);
  const lifetime = new AbortController(), active = new Set(), streams = new Set();
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
    const signal = context?.signal ? AbortSignal.any([lifetime.signal, context.signal]) : lifetime.signal;
    if (signal.aborted) throw metadataFailure('CANCELLED');
    return signal;
  }
  const methods = {};
  function method(name, handler) {
    methods[spec.prefix + name] = (input, context) => track((async () => {
      try {
        const signal = signalFor(context);
        if (!object(input)) throw metadataFailure('INVALID_REQUEST');
        const value = await handler(input, signal);
        if (signal.aborted) throw metadataFailure('CANCELLED');
        return { ok: true, contract: spec.contract, value };
      } catch (error) { return failure(error); }
    })());
  }
  method('settings.get', input => {
    if (Object.keys(input).length) throw metadataFailure('INVALID_REQUEST');
    return store.get();
  });
  method('settings.update', input => store.update(input));
  method('request', (input, signal) => (id === 'tmdb' ? requestTMDB : requestDouban)(input,
    { settings: store.read(), request, signal }));
  if (id === 'douban') method('search', (input, signal) => searchDouban(input,
    { settings: store.read(), request, signal }));
  return Object.freeze({
    id, version: spec.version, contract: spec.contract, methods,
    image(path, context) {
      return track((async () => {
        try {
          const signal = signalFor(context);
          const target = (id === 'tmdb' ? tmdbImageTarget : doubanImageTarget)(path, store.read());
          const result = await openMetadataImage(target, signal);
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
        } catch (error) { return failure(error); }
      })());
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        lifetime.abort();
        await Promise.allSettled([...active]);
        const results = await Promise.allSettled([...streams].map(dispose => Promise.resolve().then(dispose)));
        if (cleanupFailure || results.some(result => result.status === 'rejected')) {
          throw metadataFailure('NETWORK_ERROR');
        }
      })();
    },
  });
}

export const createTMDBDataModule = options => createMetadataModule('tmdb', options);
export const createDoubanDataModule = options => createMetadataModule('douban', options);
