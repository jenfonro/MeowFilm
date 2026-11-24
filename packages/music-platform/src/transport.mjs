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

function isZlibError(error) {
  const code = error?.code ?? '';
  return typeof code === 'string' && (code.startsWith('Z_') || code.startsWith('ERR_ZLIB_'));
}

function networkFailure(error, signal, sent) {
  if (signal?.aborted) throw signal.reason;
  if (error instanceof MusicPlatformError) throw error;
  if (isZlibError(error)) fail('INVALID_RESPONSE');
  const failure = new MusicPlatformError('NETWORK_ERROR');
  failure.cause = error;
  // Connection never completed TCP/TLS or hit an explicit pre-connect errno: zero HTTP bytes sent.
  failure.beforeSend = !sent || ['ENOTFOUND', 'EAI_AGAIN', 'ECONNREFUSED', 'ENETUNREACH', 'EHOSTUNREACH']
    .includes(error.code ?? error.cause?.code);
  throw failure;
}

function responseStatus(status, allowRedirect) {
  if (status >= 200 && status < 300 || allowRedirect && redirects.has(status)) return;
  const error = new MusicPlatformError('UPSTREAM_HTTP');
  error.status = status;
  throw error;
}

/** Fixed recipes use this transport; it never validates business objects. */
export function createMusicTransport({ debug } = {}) {
  const sockets = new Map(), activeRequests = new Set();
  let closed = false, closing;

  function trackSocket(socket) {
    if (!socket || socket.closed || sockets.has(socket)) return;
    const drained = new Promise(resolve => socket.once('close', () => {
      sockets.delete(socket);
      resolve();
    }));
    sockets.set(socket, drained);
    if (closed) socket.destroy();
  }

  function makeAgent(AgentType) {
    const instance = new AgentType({ keepAlive: true });
    const create = instance.createConnection;
    instance.createConnection = function (...args) {
      const socket = create.apply(this, args);
      if (socket) trackSocket(socket);
      return socket;
    };
    return instance;
  }

  const httpAgent = makeAgent(http.Agent), httpsAgent = makeAgent(https.Agent);

  async function nodeExchange({ url, method, headers, body, json, signal, allowRedirect, dispatched, onResponseHeaders }) {
    if (closed) fail('CLOSED');
    const resources = [], closingResources = [];
    let stopping = false, failed = false, sent = false, activeSocket = null;
    const own = resource => {
      resources.push(resource);
      if (!resource.closed) closingResources.push(new Promise(resolve => resource.once('close', resolve)));
      if (stopping) resource.destroy();
      return resource;
    };
    const markDispatched = () => {
      if (sent || signal?.aborted || closed) return;
      sent = true;
      dispatched?.();
    };
    try {
      const response = await new Promise((resolve, reject) => {
        const address = new URL(url), isHTTPS = address.protocol === 'https:';
        const module = isHTTPS ? https : http;
        const agent = isHTTPS ? httpsAgent : httpAgent;
        const request = own(module.request(address, { method, headers, signal, agent }, incoming => {
          markDispatched();
          own(incoming);
          incoming.on('error', () => {}); // Between headers and pipeline installation.
          resolve(incoming);
        }));
        activeRequests.add(request);
        request.once('close', () => activeRequests.delete(request));
        request.once('socket', socket => {
          trackSocket(socket);
          activeSocket = socket;
          if (!socket.connecting) {
            if (request.writableFinished) markDispatched();
            else request.once('finish', markDispatched);
          } else {
            socket.once(isHTTPS ? 'secureConnect' : 'connect', markDispatched);
          }
        });
        request.on('error', reject);
        request.end(body);
      });
      signal?.throwIfAborted();
      onResponseHeaders?.(response.headers);
      responseStatus(response.statusCode, allowRedirect);
      const encodings = method === 'HEAD' || [204, 205, 304].includes(response.statusCode) ? [] :
        String(response.headers['content-encoding'] ?? 'identity').split(',').map(value => value.trim().toLowerCase()).reverse();
      const streams = [response];
      // As with fetch, an unknown content encoding stays raw, not a new gate.
      if (encodings.every(encoding => encoding === 'identity' || Object.hasOwn(decompressors, encoding))) {
        for (const encoding of encodings) {
          if (encoding !== 'identity') streams.push(own(decompressors[encoding]()));
        }
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
      signal?.throwIfAborted();
      const bytes = chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, length);
      return {
        value: json ? decodeJSON(bytes) : bytes,
        cookies: response.headers['set-cookie'] ?? [],
        headers: {
          get(name) {
            const key = name.toLowerCase();
            if (!Object.hasOwn(response.headers, key)) return null;
            const value = response.headers[key];
            return Array.isArray(value) ? value.join(', ') : value;
          },
        },
        status: response.statusCode,
      };
    } catch (error) {
      failed = true;
      networkFailure(error, signal, sent);
    } finally {
      stopping = true;
      if (failed || closed || signal?.aborted) {
        for (const resource of resources) resource.destroy();
        if (activeSocket) activeSocket.destroy();
      }
      while (closingResources.length) await Promise.all(closingResources.splice(0));
    }
  }

  async function transport({ url, method = 'GET', headers = {}, body,
    json = true, signal, allowRedirect = false, dispatched }) {
    if (closed) fail('CLOSED');
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
      let rawResponseHeaders = null;
      const result = await nodeExchange({
        url, method, headers, body, json, signal, allowRedirect, dispatched,
        onResponseHeaders: debug ? value => { rawResponseHeaders = value; } : undefined,
      });
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
  }

  transport.close = function close() {
    if (closing) return closing;
    closed = true;
    closing = (async () => {
      for (const request of activeRequests) request.destroy();
      httpAgent.destroy();
      httpsAgent.destroy();
      for (const socket of sockets.keys()) socket.destroy();
      while (sockets.size) await Promise.all(sockets.values());
    })();
    return closing;
  };

  return transport;
}
