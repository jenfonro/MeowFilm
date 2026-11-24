import { executeHTTP } from './http-transport.js';
import { fault, requireValue } from './errors.js';

const forbiddenHeaders = new Set([
  'content-length', 'transfer-encoding', 'upgrade', 'trailer',
  'proxy-authorization', 'proxy-connection', 'te',
]);

function buffer(value) {
  if (typeof value === 'string') return Buffer.from(value);
  if (ArrayBuffer.isView(value)) return Buffer.from(value.buffer, value.byteOffset, value.byteLength);
  if (value instanceof ArrayBuffer) return Buffer.from(value);
  throw fault('INVALID_ARGUMENT');
}

function prepare(options) {
  requireValue(options && typeof options === 'object' && !Array.isArray(options));
  const method = String(options.method ?? 'GET').toUpperCase();
  requireValue(['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'].includes(method));
  const headers = {};
  requireValue(options.headers === undefined ||
    (options.headers && typeof options.headers === 'object' && !Array.isArray(options.headers)));
  for (const [key, value] of Object.entries(options.headers ?? {})) {
    const lower = key.toLowerCase();
    requireValue(!forbiddenHeaders.has(lower), 'PERMISSION_DENIED');
    if (lower === 'connection') {
      requireValue(['close', 'keep-alive'].includes(String(value).toLowerCase()), 'PERMISSION_DENIED');
      continue; // No shared agent/session configuration from script options.
    }
    requireValue(typeof value === 'string' && !/[\r\n]/.test(value));
    headers[lower] = value;
  }
  let body;
  if (options.body !== undefined) {
    if (options.body && typeof options.body === 'object' && !ArrayBuffer.isView(options.body) &&
        !(options.body instanceof ArrayBuffer)) {
      body = Buffer.from(JSON.stringify(options.body));
      headers['content-type'] ??= 'application/json';
    } else body = buffer(options.body);
  }
  if (body) headers['content-length'] = String(body.length);
  return { method, headers, body };
}

/**
 * Optional, explicitly injected adapter for metadata HTTP, not a media relay.
 * Validate HTTP(S) on every redirect and preserve explicit caller headers.
 */
export function createNodeNetwork({
  maxRedirects = 5,
  debug,
  debugCategory = 'film-catalog',
  debugSub = 'script.http',
} = {}) {
  requireValue(Number.isSafeInteger(maxRedirects) && maxRedirects >= 0 && maxRedirects <= 10);
  return async ({ url: input, options = {}, signal, scope, validateURL }) => {
    requireValue(signal && typeof validateURL === 'function');
    requireValue(options.followRedirect === undefined || typeof options.followRedirect === 'boolean');
    const requestedFollow = options.followRedirect === false ? 0 : options.follow_max ?? maxRedirects;
    requireValue(Number.isSafeInteger(requestedFollow) && requestedFollow >= 0);
    const follow = Math.min(requestedFollow, maxRedirects);
    return executeHTTP({
      url: input,
      request: prepare(options),
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
