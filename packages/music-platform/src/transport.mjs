import { fail, MusicPlatformError } from './errors.mjs';
import { decodeJSON } from './model.mjs';
import http, { validateHeaderName, validateHeaderValue } from 'node:http';
import https from 'node:https';
import { performance } from 'node:perf_hooks';
import { Writable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { createGunzip, createInflate, createBrotliDecompress } from 'node:zlib';

const redirects = new Set([301, 302, 303, 307, 308]);
const decompressors = { gzip: createGunzip, deflate: createInflate, br: createBrotliDecompress };
function networkFailure(error, signal) {
  if (signal?.aborted) throw signal.reason;
  if (error instanceof MusicPlatformError) throw error;
  const failure = new MusicPlatformError('NETWORK_ERROR');
  failure.cause = error;
  // Only actual connection-establishment failures prove zero HTTP bytes sent.
  failure.beforeSend = ['ENOTFOUND', 'EAI_AGAIN', 'ECONNREFUSED', 'ENETUNREACH', 'EHOSTUNREACH']
    .includes(error.code ?? error.cause?.code);
  throw failure;
}
function responseStatus(status, allowRedirect) {
  if (status >= 200 && status < 300 || allowRedirect && redirects.has(status)) return;
  const error = new MusicPlatformError('UPSTREAM_HTTP');
  error.status = status;
  throw error;
}
async function nodeExchange({ url, method, headers, body, json, signal, allowRedirect, dispatched, onResponseHeaders }) {
  // Use the existing managed Node network's per-exchange ownership pattern.
  // No shared agent survives the operation; completion includes physical close.
  const resources = [], closing = [];
  let stopping = false;
  const own = resource => {
    resources.push(resource);
    closing.push(new Promise(resolve => resource.once('close', resolve)));
    if (stopping) resource.destroy();
    return resource;
  };
  try {
    const response = await new Promise((resolve, reject) => {
      const address = new URL(url), transport = address.protocol === 'https:' ? https : http;
      const request = own(transport.request(address, { method, headers, signal, agent: false }, incoming => {
        own(incoming);
        incoming.on('error', () => {}); // Between headers and pipeline installation.
        resolve(incoming);
      }));
      request.once('socket', socket => own(socket));
      request.on('error', reject);
      dispatched?.();
      request.end(body);
    });
    signal?.throwIfAborted();
    onResponseHeaders?.(response.headers);
    responseStatus(response.statusCode, allowRedirect);
    const encodings = method === 'HEAD' || [204, 205, 304].includes(response.statusCode) ? [] :
      String(response.headers['content-encoding'] ?? 'identity').split(',').map(value => value.trim().toLowerCase()).reverse();
    const streams = [response];
    // As with fetch, an unknown content encoding stays raw, not a new gate.
    if (encodings.every(encoding => encoding === 'identity' || Object.hasOwn(decompressors, encoding)))
      for (const encoding of encodings) if (encoding !== 'identity') streams.push(own(decompressors[encoding]()));
    const chunks = [];
    let length = 0;
    streams.push(own(new Writable({
      write(chunk, _encoding, callback) {
        length += chunk.length;
        chunks.push(chunk);
        callback();
      },
    })));
    // Compressed input stays streaming until the complete decoded response is available.
    await pipeline(streams, { signal });
    signal?.throwIfAborted();
    const bytes = chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, length);
    return { value: json ? decodeJSON(bytes) : bytes, cookies: response.headers['set-cookie'] ?? [],
      headers: { get(name) {
        const key = name.toLowerCase();
        if (!Object.hasOwn(response.headers, key)) return null;
        const value = response.headers[key];
        return Array.isArray(value) ? value.join(', ') : value;
      } }, status: response.statusCode };
  } catch (error) { networkFailure(error, signal); }
  finally {
    stopping = true;
    for (const resource of resources) resource.destroy();
    while (closing.length) await Promise.all(closing.splice(0));
  }
}

/** Fixed recipes use this transport; it never validates business objects. */
export function createMusicTransport({ fetch: fetcher, debug } = {}) {
  return async ({ url, method = 'GET', headers = {}, body,
    json = true, signal, allowRedirect = false, dispatched }) => {
    signal?.throwIfAborted();
    try {
      for (const [name, value] of Object.entries(headers)) {
        validateHeaderName(name); validateHeaderValue(name, value);
      }
    } catch { fail('INVALID_REQUEST'); }
    const startedAt = debug ? performance.now() : 0;
    if (debug) {
      debug.emit({
        level: 'debug',
        cat: 'music-platform',
        sub: 'http',
        event: 'http.request',
        data: { url, method, headers, body: body ?? null, json, allowRedirect },
      });
    }
    try {
      let result;
      let rawResponseHeaders = null;
      if (!fetcher) {
        result = await nodeExchange({
          url, method, headers, body, json, signal, allowRedirect, dispatched,
          onResponseHeaders: debug ? value => { rawResponseHeaders = value; } : undefined,
        });
      } else {
        let response;
        dispatched?.();
        try { response = await fetcher(url, { method, headers, body, signal, redirect: 'manual' }); }
        catch (error) { networkFailure(error, signal); }
        if (debug && typeof response.headers?.entries === 'function') {
          rawResponseHeaders = Object.fromEntries(response.headers.entries());
        }
        if (!response.ok && !(allowRedirect && redirects.has(response.status))) {
          await response.body?.cancel().catch(() => {});
          responseStatus(response.status, allowRedirect);
        }
        const chunks = [];
        let length = 0;
        const reader = response.body?.getReader();
        try {
          while (reader) {
            const { done, value: chunk } = await reader.read();
            if (done) break;
            signal?.throwIfAborted();
            length += chunk.byteLength;
            chunks.push(chunk);
          }
        } catch (error) {
          networkFailure(error, signal);
        } finally { reader?.releaseLock(); }
        const bytes = Buffer.concat(chunks, length);
        result = { value: json ? decodeJSON(bytes) : bytes,
          cookies: response.headers.getSetCookie(), headers: response.headers, status: response.status };
      }
      if (debug) {
        debug.emit({
          level: 'debug',
          cat: 'music-platform',
          sub: 'http',
          event: 'http.response',
          durationMs: performance.now() - startedAt,
          data: {
            url,
            method,
            status: result.status,
            headers: rawResponseHeaders,
            cookies: result.cookies,
            location: result.headers?.get?.('location') ?? null,
            contentType: result.headers?.get?.('content-type') ?? null,
            value: result.value,
          },
        });
      }
      return result;
    } catch (error) {
      if (debug) {
        debug.emit({
          level: 'warn',
          cat: 'music-platform',
          sub: 'http',
          event: 'http.error',
          durationMs: performance.now() - startedAt,
          data: { url, method, status: error?.status ?? null, code: error?.code ?? null },
          error,
        });
      }
      throw error;
    }
  };
}
