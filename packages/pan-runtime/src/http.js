import http from 'node:http';
import https from 'node:https';
import { performance } from 'node:perf_hooks';
import { createBrotliDecompress, createGunzip, createInflate } from 'node:zlib';
import { PanError, requireValue } from './errors.js';
import { parseJSON } from './values.js';

const redirects = new Set([301, 302, 303, 307, 308]);
const decompressors = { gzip: createGunzip, deflate: createInflate, br: createBrotliDecompress };

function address(value, code = 'INVALID_REQUEST') {
  try {
    const url = new URL(value);
    requireValue(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password, code);
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
  requireValue(Number.isFinite(value) && value >= 0);
  return value;
}

export function createTransport({ timeout = 30000 } = {}) {
  milliseconds(timeout);
  const sockets = new Map(), requests = new Set(), operations = new Set();
  let closed = false, closing;

  function track(socket) {
    if (sockets.has(socket)) return;
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
        request.on('error', () => { if (!stopping) fail(new PanError('NETWORK_ERROR')); });
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
          incoming.on('error', () => { if (!stopping) fail(new PanError('NETWORK_ERROR')); });
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
              stream.on('error', () => fail(new PanError('UPSTREAM_FORMAT')));
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
      } catch {
        failure = new PanError('NETWORK_ERROR');
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
          body: inputBody, signal: inputSignal, redirect = 'manual', timeout: duration = timeout,
          responseMode = 'body' } = options;
        milliseconds(duration);
        requireValue(redirect === 'manual' || redirect === 'follow');
        requireValue(responseMode === 'body' || responseMode === 'headers');
        requireValue(typeof inputMethod === 'string' && /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(inputMethod));
        requireValue(inputBody === undefined || typeof inputBody === 'string' ||
          inputBody instanceof Uint8Array);
        requireValue(inputSignal === undefined || typeof inputSignal?.addEventListener === 'function' &&
          typeof inputSignal?.removeEventListener === 'function' && typeof inputSignal.aborted === 'boolean');
        signal = inputSignal;
        abort = () => controller.abort(new PanError('CANCELLED'));
        signal?.addEventListener('abort', abort, { once: true });
        if (signal?.aborted) abort();
        const deadline = performance.now() + duration;
        const tick = () => {
          const remaining = deadline - performance.now();
          if (remaining <= 0) controller.abort(new PanError('TIMEOUT'));
          else timer = setTimeout(tick, Math.min(remaining, 2147483647));
        };
        if (duration > 0) tick();
        let url = address(input), method = inputMethod.toUpperCase();
        let headers = headerObject(inputHeaders), body = inputBody;
        if (body !== undefined && headers['content-length'] === undefined &&
            headers['transfer-encoding'] === undefined) {
          headers['content-length'] = typeof body === 'string' ? Buffer.byteLength(body) : body.byteLength;
        }
        const visited = new Set([`${method} ${url.href}`]);
        while (true) {
          if (controller.signal.aborted) throw controller.signal.reason;
          const result = await exchange(url, method, headers, body, responseMode, controller.signal);
          if (redirect === 'manual' || !redirects.has(result.status) || !result.headers.location) return result;
          let next;
          try { next = address(new URL(result.headers.location, url), 'UPSTREAM_FORMAT'); }
          catch { throw new PanError('UPSTREAM_FORMAT'); }
          requireValue(!(url.protocol === 'https:' && next.protocol === 'http:'), 'UPSTREAM_HTTP');
          const discardBody = result.status === 303 && method !== 'HEAD' ||
            [301, 302].includes(result.status) && method === 'POST';
          if (next.origin !== url.origin) {
            requireValue(body === undefined || discardBody, 'UPSTREAM_HTTP');
            // Only non-secret representation headers survive a change of origin.
            const publicHeaders = new Set(['accept', 'accept-encoding', 'accept-language',
              'user-agent', 'content-type', 'content-length']);
            headers = Object.fromEntries(Object.entries(headers).filter(([name]) => publicHeaders.has(name)));
          }
          if (discardBody) {
            method = 'GET';
            body = undefined;
            for (const name of Object.keys(headers)) if (name.startsWith('content-')) delete headers[name];
            delete headers['transfer-encoding'];
          }
          const key = `${method} ${next.href}`;
          requireValue(!visited.has(key), 'UPSTREAM_HTTP');
          visited.add(key);
          url = next;
        }
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

  return { request, close };
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
  return { ...rest, text: body.toString('utf8') };
}

export function delay(ms, signal) {
  return new Promise((resolve, reject) => {
    milliseconds(ms);
    requireValue(signal === undefined || typeof signal?.addEventListener === 'function' &&
      typeof signal?.removeEventListener === 'function' && typeof signal.aborted === 'boolean');
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
    const deadline = performance.now() + ms;
    const tick = () => {
      const remaining = deadline - performance.now();
      if (remaining <= 0) finish();
      else timer = setTimeout(tick, Math.min(remaining, 2147483647));
    };
    tick();
  });
}
