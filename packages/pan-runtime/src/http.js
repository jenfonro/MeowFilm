import http from 'node:http';
import https from 'node:https';
import { performance } from 'node:perf_hooks';
import { createBrotliDecompress, createGunzip, createInflate } from 'node:zlib';
import { PanError, requireValue } from './errors.js';
import { decodeUTF8, parseJSON } from './values.js';

const redirectStatuses = new Set([301, 302, 303, 307, 308]);
const hopOnlyHeaders = new Set(['authorization', 'lt', 'reqid', 'referer', 'origin']);
const decompressors = { gzip: createGunzip, deflate: createInflate, br: createBrotliDecompress };
const MAX_REDIRECT_HOPS = 10;

function address(value, code = 'INVALID_REQUEST') {
  try {
    const url = new URL(value);
    requireValue(['http:', 'https:'].includes(url.protocol), code);
    url.hash = '';
    return url;
  } catch { throw new PanError(code); }
}

function headerObject(value) {
  requireValue(value !== null && typeof value === 'object' && !Array.isArray(value));
  try {
    return Object.fromEntries(Object.entries(value).map(([key, entry]) => {
      http.validateHeaderName(key);
      http.validateHeaderValue(key, entry);
      return [key.toLowerCase(), entry];
    }));
  } catch { throw new PanError('INVALID_REQUEST'); }
}

function milliseconds(value) {
  requireValue(typeof value === 'number' && Number.isFinite(value) && value >= 0);
  return value;
}

export function createTransport({ timeout = 30000, debug } = {}) {
  requireValue(typeof timeout === 'number' && Number.isFinite(timeout) && timeout > 0);
  const sockets = new Map(), requests = new Set(), operations = new Set();
  let closed = false, closing;
  // Debug keeps the raw socket/TLS/decoder failure as cause; the public code is unchanged.
  const upstream = (code, cause) => {
    const failure = new PanError(code);
    if (debug && cause !== undefined) failure.cause = cause;
    return failure;
  };

  function track(socket) {
    if (!socket || socket.closed || sockets.has(socket)) return;
    const drained = new Promise(resolve => socket.once('close', () => {
      sockets.delete(socket);
      resolve();
    }));
    sockets.set(socket, drained);
    if (closed) socket.destroy();
  }

  function agent(Type) {
    const instance = new Type({ keepAlive: true });
    const create = instance.createConnection;
    instance.createConnection = function (...args) {
      const socket = create.apply(this, args);
      if (socket) track(socket);
      return socket;
    };
    return instance;
  }
  const httpAgent = agent(http.Agent), httpsAgent = agent(https.Agent);

  function exchange(url, method, headers, body, responseMode, signal) {
    return new Promise((resolve, reject) => {
      let request, response, settled = false, requestClosed = false, responseClosed = true;
      let ended = false, stopping = false, outcome, failure, socket, socketClosed = true;
      let chunks = [], length = 0;
      const streams = new Set();

      function finish() {
        if (settled || !requestClosed || !responseClosed || streams.size ||
            (failure || stopping) && !socketClosed || !failure && !ended) return;
        settled = true;
        signal.removeEventListener('abort', abort);
        socket?.removeListener('close', socketClose);
        requests.delete(request);
        chunks = [];
        if (failure) reject(failure);
        else resolve(outcome);
      }
      function fail(error) {
        failure ??= error;
        chunks = [];
        for (const stream of streams) stream.destroy();
        response?.destroy();
        request?.destroy();
        socket?.destroy();
        finish();
      }
      function socketClose() { socketClosed = true; finish(); }
      const abort = () => fail(signal.reason instanceof PanError ?
        signal.reason : new PanError('CANCELLED'));
      if (signal.aborted) { reject(signal.reason); return; }

      try {
        request = (url.protocol === 'https:' ? https : http).request(url, {
          method, headers, agent: url.protocol === 'https:' ? httpsAgent : httpAgent,
        });
        requests.add(request);
        request.on('socket', value => {
          track(value);
          socket = value;
          socketClosed = value.closed;
          if (!socketClosed) socket.once('close', socketClose);
          if (failure) socket.destroy();
        });
        request.on('error', error => { if (!stopping) fail(upstream('NETWORK_ERROR', error)); });
        request.once('close', () => {
          requestClosed = true;
          if (!response && !failure) fail(new PanError('NETWORK_ERROR'));
          finish();
        });
        request.once('response', incoming => {
          response = incoming;
          responseClosed = false;
          incoming.once('close', () => {
            responseClosed = true;
            if (!incoming.complete && !failure && !stopping) fail(new PanError('NETWORK_ERROR'));
            finish();
          });
          incoming.on('error', error => { if (!stopping) fail(upstream('NETWORK_ERROR', error)); });
          incoming.once('aborted', () => { if (!stopping) fail(new PanError('NETWORK_ERROR')); });
          if (failure) { incoming.destroy(); return; }
          if (responseMode === 'headers') {
            stopping = true;
            ended = true;
            outcome = { status: incoming.statusCode, headers: incoming.headers,
              body: Buffer.alloc(0), url: url.href };
            incoming.destroy();
            request.destroy();
            socket?.destroy();
            finish();
            return;
          }
          let output = incoming;
          const encoding = incoming.headers['content-encoding'];
          const encodings = encoding === undefined || method === 'HEAD' ||
            [204, 205, 304].includes(incoming.statusCode) ? [] :
            String(encoding).split(',').map(value => value.trim().toLowerCase()).reverse();
          try {
            for (const name of encodings) {
              if (name === 'identity') continue;
              requireValue(Object.hasOwn(decompressors, name), 'UPSTREAM_FORMAT');
              const stream = decompressors[name]();
              streams.add(stream);
              stream.on('error', error => fail(upstream('UPSTREAM_FORMAT', error)));
              stream.once('close', () => { streams.delete(stream); finish(); });
              output = output.pipe(stream);
            }
          } catch { fail(new PanError('UPSTREAM_FORMAT')); return; }
          output.on('data', bytes => {
            if (failure) return;
            chunks.push(bytes);
            length += bytes.length;
          });
          output.once('end', () => {
            if (failure) return;
            ended = true;
            outcome = { status: incoming.statusCode, headers: incoming.headers,
              body: chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, length), url: url.href };
            finish();
          });
        });
        signal.addEventListener('abort', abort, { once: true });
        if (signal.aborted) abort();
        else request.end(body);
      } catch (error) {
        failure = upstream('NETWORK_ERROR', error);
        if (request) fail(failure);
        else { requestClosed = true; finish(); }
      }
    });
  }

  function request(options = {}) {
    if (closed) return Promise.reject(new PanError('CLOSED'));
    const controller = new AbortController();
    let complete;
    const drained = new Promise(resolve => { complete = resolve; });
    const operation = { controller, drained };
    operations.add(operation);
    return (async () => {
      let timer, signal, abort;
      try {
        requireValue(options !== null && typeof options === 'object' && !Array.isArray(options));
        const { url: input, method: inputMethod = 'GET', headers: inputHeaders = {},
          body: inputBody, signal: inputSignal, timeout: inputTimeout,
          responseMode = 'body' } = options;
        const hasExplicitTimeout = inputTimeout !== undefined;
        const duration = hasExplicitTimeout ? inputTimeout : timeout;
        requireValue(typeof duration === 'number' && Number.isFinite(duration));
        requireValue(responseMode === 'body' || responseMode === 'headers');
        requireValue(typeof inputMethod === 'string' && /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(inputMethod));
        requireValue(inputBody === undefined || typeof inputBody === 'string' ||
          inputBody instanceof Uint8Array);
        signal = inputSignal;
        abort = () => controller.abort(new PanError('CANCELLED'));
        signal?.addEventListener('abort', abort, { once: true });
        if (signal?.aborted) abort();
        if (hasExplicitTimeout && duration <= 0) {
          if (!controller.signal.aborted) controller.abort(new PanError('TIMEOUT'));
        } else {
          requireValue(duration > 0);
          timer = setTimeout(() => controller.abort(new PanError('TIMEOUT')), Math.min(duration, 2147483647));
        }
        const url = address(input), method = inputMethod.toUpperCase();
        const headers = headerObject(inputHeaders), body = inputBody;
        if (body !== undefined && headers['content-length'] === undefined &&
            headers['transfer-encoding'] === undefined) {
          headers['content-length'] = typeof body === 'string' ? Buffer.byteLength(body) : body.byteLength;
        }
        if (controller.signal.aborted) throw controller.signal.reason;
        const hopStart = debug ? performance.now() : 0;
        if (debug) {
          debug.emit({
            level: 'debug',
            cat: 'film-playback',
            sub: 'pan.http',
            event: 'http.request',
            data: { url: url.href, method, headers, body: body ?? null, responseMode },
          });
        }
        let result;
        try {
          result = await exchange(url, method, headers, body, responseMode, controller.signal);
        } catch (error) {
          if (debug) {
            debug.emit({
              level: 'warn',
              cat: 'film-playback',
              sub: 'pan.http',
              event: 'http.error',
              durationMs: performance.now() - hopStart,
              data: { url: url.href, method, code: error?.code ?? null },
              error,
            });
          }
          throw error;
        }
        if (debug) {
          debug.emit({
            level: 'debug',
            cat: 'film-playback',
            sub: 'pan.http',
            event: 'http.response',
            durationMs: performance.now() - hopStart,
            data: { url: url.href, method, status: result.status, headers: result.headers, body: result.body ?? null },
          });
        }
        return result;
      } catch (error) {
        if (controller.signal.aborted) throw controller.signal.reason;
        throw error instanceof PanError ? error : new PanError('NETWORK_ERROR');
      } finally {
        clearTimeout(timer);
        signal?.removeEventListener('abort', abort);
        operations.delete(operation);
        complete();
      }
    })();
  }

  function close() {
    if (closing) return closing;
    closed = true;
    closing = (async () => {
      for (const operation of operations) operation.controller.abort(new PanError('CLOSED'));
      for (const request of requests) request.destroy();
      httpAgent.destroy();
      httpsAgent.destroy();
      for (const socket of sockets.keys()) socket.destroy();
      await Promise.all([...operations].map(operation => operation.drained));
      while (sockets.size) await Promise.all(sockets.values());
    })();
    return closing;
  }

  return {
    request,
    diagnostics: () => ({
      closed,
      activeOperations: operations.size,
      activeRequests: requests.size,
      activeSockets: sockets.size,
    }),
    close,
  };
}

function requestOptions(ctx, options) {
  const { json, form, body, headers: inputHeaders = {}, ...rest } = options;
  requireValue([json, form, body].filter(value => value !== undefined).length <= 1);
  const headers = headerObject(inputHeaders);
  let bytes = body;
  try {
    if (json !== undefined) {
      const source = JSON.stringify(json);
      requireValue(source !== undefined);
      bytes = Buffer.from(source);
      headers['content-type'] ??= 'application/json';
    } else if (form !== undefined) {
      bytes = Buffer.from(new URLSearchParams(form).toString());
      headers['content-type'] ??= 'application/x-www-form-urlencoded';
    }
  } catch { throw new PanError('INVALID_REQUEST'); }
  return { ...rest, headers, body: bytes, signal: ctx.signal };
}

export async function requestJSON(ctx, url, options = {}) {
  const result = await ctx.http.request({ url, ...requestOptions(ctx, options) });
  const { body, ...rest } = result;
  return { ...rest, value: parseJSON(body) };
}

export async function requestText(ctx, url, options = {}) {
  const result = await ctx.http.request({ url, ...requestOptions(ctx, options) });
  const { body, ...rest } = result;
  return { ...rest, text: decodeUTF8(body) };
}

/** Bounded manual redirect follower with PRG-safe cycle check and hop header hygiene. */
export async function followRedirects(ctx, initialURL, {
  method: initialMethod = 'GET',
  headers: initialHeaders = {},
  persistentHeaders,
  body: initialBody,
  json: initialJson,
  form: initialForm,
  responseMode = 'body',
  jar,
  timeout,
  expiresAt,
  expiredCode = 'LOGIN_EXPIRED',
  isCancelled,
  validateStatus,
  maxHops = MAX_REDIRECT_HOPS,
} = {}) {
  let url = address(initialURL, 'UPSTREAM_FORMAT');
  requireValue(!url.username && !url.password, 'UPSTREAM_FORMAT');
  let method = initialMethod.toUpperCase();
  let body = initialBody, json = initialJson, form = initialForm;
  const headers = headerObject(initialHeaders);
  const baseHeaders = persistentHeaders ? headerObject(persistentHeaders) : null;
  const visited = new Set();
  for (let hop = 0; ; hop++) {
    if (ctx.signal?.aborted) throw new PanError('CANCELLED');
    isCancelled?.();
    let hopTimeout = timeout;
    if (expiresAt !== undefined) {
      const now = ctx.now();
      const remaining = expiresAt - now;
      requireValue(remaining > 0, expiredCode);
      hopTimeout = hopTimeout === undefined ? remaining : Math.min(hopTimeout, remaining);
    }
    const visitKey = `${method} ${url.href}`;
    requireValue(!visited.has(visitKey), 'UPSTREAM_FORMAT');
    visited.add(visitKey);
    const cookie = jar ? jar.header(url.href) : '';
    const reqHeaders = { ...(baseHeaders ?? {}), ...headers, ...(cookie ? { cookie } : {}) };
    const opts = requestOptions(ctx, {
      method, headers: reqHeaders, body, json, form, responseMode,
      ...(hopTimeout === undefined ? {} : { timeout: hopTimeout }),
    });
    let response;
    try {
      response = await ctx.http.request({ url: url.href, ...opts });
    } catch (error) {
      if (ctx.signal?.aborted) throw new PanError('CANCELLED');
      if (expiresAt !== undefined) requireValue(ctx.now() < expiresAt, expiredCode);
      throw error;
    }
    if (ctx.signal?.aborted) throw new PanError('CANCELLED');
    if (expiresAt !== undefined) requireValue(ctx.now() < expiresAt, expiredCode);
    jar?.set(url.href, response.headers['set-cookie'] || []);
    if (redirectStatuses.has(response.status)) {
      requireValue(hop < maxHops, 'UPSTREAM_HTTP');
      requireValue(typeof response.headers.location === 'string' && response.headers.location.trim(), 'UPSTREAM_FORMAT');
      let next;
      try { next = address(new URL(response.headers.location, url), 'UPSTREAM_FORMAT'); }
      catch { throw new PanError('UPSTREAM_FORMAT'); }
      requireValue(!next.username && !next.password, 'UPSTREAM_FORMAT');
      const crossOrigin = next.origin !== url.origin;
      if (response.status === 303 || ([301, 302].includes(response.status) && method === 'POST')) {
        method = 'GET';
        body = undefined;
        json = undefined;
        form = undefined;
        for (const name of Object.keys(headers)) {
          if (name.startsWith('content-') || name === 'transfer-encoding') delete headers[name];
        }
      }
      for (const name of Object.keys(headers)) {
        if (name === 'authorization' ? crossOrigin : hopOnlyHeaders.has(name)) delete headers[name];
      }
      url = next;
      continue;
    }
    if (validateStatus) validateStatus(response);
    else requireValue(response.status >= 200 && response.status < 300, 'UPSTREAM_HTTP');
    return { ...response, url: url.href };
  }
}

export function delay(ms, signal) {
  return new Promise((resolve, reject) => {
    milliseconds(ms);
    let timer;
    const finish = error => {
      clearTimeout(timer);
      signal?.removeEventListener('abort', abort);
      if (error) reject(error);
      else resolve();
    };
    const abort = () => finish(new PanError('CANCELLED'));
    if (signal?.aborted) { abort(); return; }
    signal?.addEventListener('abort', abort, { once: true });
    if (ms <= 0) finish();
    else timer = setTimeout(finish, Math.min(ms, 2147483647));
  });
}
