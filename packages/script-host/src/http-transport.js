import http from 'node:http';
import https from 'node:https';
import { performance } from 'node:perf_hooks';
import { Writable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { createGunzip, createInflate, createBrotliDecompress } from 'node:zlib';
import { requireValue, safeError } from './errors.js';

export const REDIRECT_STATUSES = new Set([301, 302, 303, 307, 308]);
export const CONTENT_ENCODING_METHODS = Object.freeze({
  __proto__: null,
  gzip: 'createGunzip',
  'x-gzip': 'createGunzip',
  deflate: 'createInflate',
  br: 'createBrotliDecompress',
});
const decompressors = {
  __proto__: null,
  gzip: createGunzip,
  'x-gzip': createGunzip,
  deflate: createInflate,
  br: createBrotliDecompress,
};

export function createDecompressor(encoding) {
  return Object.hasOwn(decompressors, encoding) ? decompressors[encoding]() : null;
}

export function redirectShouldResetToGet(statusCode, method) {
  return (statusCode === 303 && method !== 'HEAD') ||
    ([301, 302].includes(statusCode) && method === 'POST');
}

export function parseContentEncodings(headerValue, method, statusCode, { strict = true } = {}) {
  if (method === 'HEAD' || [204, 205, 304].includes(statusCode)) return [];
  const raw = Array.isArray(headerValue) ? headerValue.join(',') : String(headerValue ?? '');
  const tokens = raw.split(',').map(value => value.trim().toLowerCase()).filter(Boolean).reverse();
  const list = [];
  for (const encoding of tokens) {
    if (encoding === 'identity') continue;
    if (!Object.hasOwn(decompressors, encoding)) {
      if (strict) requireValue(false, 'UNSUPPORTED_OPTION');
      return [];
    }
    list.push(encoding);
  }
  return list;
}

function abortReason(signal) {
  return safeError(signal.reason, 'CANCELLED');
}

async function requestOnce(url, { method = 'GET', headers = {}, body } = {}, signal, follow) {
  if (signal.aborted) throw abortReason(signal);
  const resources = [], closing = [];
  const own = resource => {
    resources.push(resource);
    closing.push(new Promise(resolve => resource.once('close', resolve)));
    return resource;
  };
  try {
    const response = await new Promise((resolve, reject) => {
      const transport = url.protocol === 'https:' ? https : http;
      const request = own(transport.request(url, { method, headers, signal, agent: false }, incoming => {
        own(incoming);
        // Cover the interval between headers and installation of the pipeline.
        incoming.on('error', () => {});
        resolve(incoming);
      }));
      request.once('socket', socket => own(socket));
      request.on('error', error => reject(signal.aborted ? abortReason(signal) : error));
      request.end(body);
    });
    if (signal.aborted) throw abortReason(signal);
    if (follow && REDIRECT_STATUSES.has(response.statusCode) && response.headers.location) {
      return { statusCode: response.statusCode, statusMessage: response.statusMessage,
        headers: response.headers, body: Buffer.alloc(0) };
    }
    const responseHeaders = { ...response.headers };
    const encodings = parseContentEncodings(response.headers['content-encoding'], method, response.statusCode);
    const streams = [response];
    for (const encoding of encodings) streams.push(own(decompressors[encoding]()));
    if (streams.length > 1) {
      delete responseHeaders['content-encoding'];
      delete responseHeaders['content-length'];
    }
    const chunks = [];
    let length = 0;
    streams.push(own(new Writable({
      write(chunk, _encoding, callback) {
        length += chunk.length;
        chunks.push(chunk);
        callback();
      },
    })));
    await pipeline(streams, { signal });
    if (signal.aborted) throw abortReason(signal);
    return {
      statusCode: response.statusCode,
      statusMessage: response.statusMessage,
      headers: responseHeaders,
      body: chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, length),
    };
  } finally {
    for (const resource of resources) resource.destroy();
    await Promise.all(closing);
  }
}

export async function executeHTTP({
  url: initialUrl,
  request: initialRequest,
  signal,
  validateURL,
  follow,
  scope,
  debug,
  debugCategory = 'film-catalog',
  debugSub = 'script.http',
}) {
  let request = initialRequest;
  let url = new URL(validateURL(initialUrl));
  const category = typeof debugCategory === 'function' ? debugCategory(scope) : debugCategory;
  const scopeMeta = scope && typeof scope === 'object' ? {
    ...(scope.installationId !== undefined ? { installationId: scope.installationId } : {}),
    ...(scope.generation !== undefined ? { generation: scope.generation } : {}),
    ...(scope.protocol !== undefined ? { protocol: scope.protocol } : {}),
    ...(scope.siteId !== undefined ? { siteId: scope.siteId } : {}),
    ...(scope.requestId !== undefined ? { requestId: scope.requestId } : {}),
  } : null;
  const startedAt = debug ? performance.now() : 0;
  if (debug) {
    debug.emit({
      level: 'debug',
      cat: category,
      sub: debugSub,
      event: 'http.request',
      data: {
        ...(scopeMeta ?? {}),
        url: url.href,
        method: request.method,
        headers: request.headers,
        body: request.body ?? null,
      },
    });
  }
  try {
    for (let hop = 0; ; hop++) {
      if (signal.aborted) throw abortReason(signal);
      requireValue(['http:', 'https:'].includes(url.protocol), 'PERMISSION_DENIED');
      const response = await requestOnce(url, request, signal, follow > 0);
      if (follow > 0 && REDIRECT_STATUSES.has(response.statusCode) && response.headers.location) {
        requireValue(hop < follow, 'RESOURCE_LIMIT');
        const next = new URL(validateURL(new URL(response.headers.location, url).href));
        if (redirectShouldResetToGet(response.statusCode, request.method)) {
          const nextHeaders = { ...request.headers };
          for (const key of Object.keys(nextHeaders)) {
            const lower = key.toLowerCase();
            if (lower === 'content-length' || lower === 'content-type') delete nextHeaders[key];
          }
          request = { method: 'GET', headers: nextHeaders, body: undefined };
        }
        url = next;
        continue;
      }
      if (signal.aborted) throw abortReason(signal);
      const output = { ...response, url: url.href };
      if (debug) {
        debug.emit({
          level: 'debug',
          cat: category,
          sub: debugSub,
          event: 'http.response',
          durationMs: performance.now() - startedAt,
          data: {
            ...(scopeMeta ?? {}),
            url: output.url,
            method: request.method,
            statusCode: output.statusCode,
            headers: output.headers,
            body: output.body,
          },
        });
      }
      return output;
    }
  } catch (error) {
    if (debug) {
      debug.emit({
        level: 'warn',
        cat: category,
        sub: debugSub,
        event: 'http.error',
        durationMs: performance.now() - startedAt,
        data: { ...(scopeMeta ?? {}), url: url.href, method: request.method, code: error?.code ?? null },
        error,
      });
    }
    throw signal.aborted ? abortReason(signal) : safeError(error, 'NETWORK_ERROR');
  }
}
