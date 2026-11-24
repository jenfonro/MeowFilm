import { executeHTTP } from './http-transport.js';
import { requireValue } from './errors.js';

/** Complete source downloads, not the guest HTTP adapter or a media proxy. */
export function createSourceDownload({
  maxRedirects = 5,
  debug,
  debugCategory = 'runtime-node',
  debugSub = 'source.download',
} = {}) {
  requireValue(Number.isSafeInteger(maxRedirects) && maxRedirects >= 0 && maxRedirects <= 10);
  return async ({ url: input, options = {}, signal, scope, validateURL }) => {
    requireValue(signal instanceof AbortSignal && typeof validateURL === 'function' &&
      options && typeof options === 'object' && !Array.isArray(options) &&
      (options.followRedirect === undefined || typeof options.followRedirect === 'boolean'));
    const follow = options.followRedirect === false ? 0 : maxRedirects;
    return executeHTTP({
      url: input,
      request: { method: 'GET', headers: {}, body: undefined },
      signal,
      validateURL,
      follow,
      scope,
      debug,
      debugCategory,
      debugSub,
    });
  };
}
