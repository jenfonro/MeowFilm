import http from 'node:http';
import { randomUUID, timingSafeEqual } from 'node:crypto';
import { Readable, Writable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { BridgeError, HTTP_PROTOCOL } from './errors.js';
import { checkJSON, encodeJSONValue, decodeJSONValue, parseJSON } from './json.js';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$(?![\s\S])/i;
const keyPattern = /^[0-9a-f]{64}$(?![\s\S])/;
const publicCodes = new Set(['INVALID_REQUEST', 'METHOD_NOT_FOUND', 'PERMISSION_DENIED',
  'CLOSED', 'CANCELLED', 'TIMEOUT', 'INVALID_RESPONSE']);
const streamHeaderNames = new Set(['content-type', 'content-length', 'cache-control', 'etag', 'last-modified']);
const invalid = () => { throw new BridgeError('INVALID_REQUEST'); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

export function validateSession({ sessionId, key }) {
  if (typeof sessionId !== 'string' || !uuid.test(sessionId) ||
      typeof key !== 'string' || !keyPattern.test(key)) invalid();
}
export function validateEndpoint(value) {
  let url;
  try { url = new URL(value); } catch { invalid(); }
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port ||
      url.username || url.password || url.pathname !== '/' || url.search || url.hash) invalid();
  return url;
}
function sameKey(left, right) {
  const bytes = Buffer.from(left ?? '');
  const expected = Buffer.from(`Bearer ${right}`);
  return bytes.length === expected.length && timingSafeEqual(bytes, expected);
}
function envelope(value, contract, codes) {
  if (!object(value) || typeof value.ok !== 'boolean' || value.contract !== contract ||
      Object.keys(value).length !== 3) throw new BridgeError('INVALID_RESPONSE');
  if (value.ok) {
    if (!Object.hasOwn(value, 'value')) throw new BridgeError('INVALID_RESPONSE');
    checkJSON(value.value);
  } else if (!Object.hasOwn(value, 'code') || !codes.has(value.code)) {
    throw new BridgeError('INVALID_RESPONSE');
  }
  return value;
}
const failed = code => ({ ok: false, contract: HTTP_PROTOCOL, code });

function streamHeaders(reply) {
  if (!object(reply) || Object.keys(reply).length !== 4 ||
      !['status', 'headers', 'body', 'dispose'].every(name => Object.hasOwn(reply, name)) ||
      reply.status !== 200 || !object(reply.headers) ||
      !(reply.body instanceof Readable) || reply.body.destroyed || reply.body.readableEnded ||
      typeof reply.dispose !== 'function') throw new BridgeError('INVALID_RESPONSE');
  const headers = {};
  for (const [name, value] of Object.entries(reply.headers)) {
    const lower = name.toLowerCase();
    if (!streamHeaderNames.has(lower) || Object.hasOwn(headers, lower) ||
        !(typeof value === 'string' || lower === 'content-length' && Number.isSafeInteger(value) && value >= 0) ||
        lower === 'content-length' && (!/^[0-9]+$(?![\s\S])/.test(String(value)) ||
          !Number.isSafeInteger(Number(value)))) {
      throw new BridgeError('INVALID_RESPONSE');
    }
    http.validateHeaderValue(lower, value);
    headers[lower] = String(value);
  }
  if (!headers['content-type']?.trim()) throw new BridgeError('INVALID_RESPONSE');
  return headers;
}

async function streamResponse(reply, response, { signal, requestId, contract, codes, onCleanupFailure }, send) {
  let body, dispose, drained, disposal;
  const release = () => disposal ??= Promise.resolve().then(async () => {
    if (body instanceof Readable) body.destroy();
    const results = await Promise.allSettled([drained,
      Promise.resolve().then(() => typeof dispose === 'function' ? dispose.call(reply) : undefined)]);
    if (results[1].status === 'rejected') {
      onCleanupFailure();
      throw new BridgeError('INVALID_RESPONSE');
    }
  });
  const releaseInBackground = () => { void release().catch(() => {}); };
  try {
    // Acquire cleanup before validation or the abort fence, including late replies.
    dispose = reply?.dispose;
    body = reply?.body;
    if (body instanceof Readable) {
      drained = new Promise(resolve => {
        let timer;
        const done = () => { clearInterval(timer); body.off('close', done); resolve(); };
        body.once('close', done);
        if (!body.destroyed) {
          const destroy = body._destroy, ownDestroy = Object.hasOwn(body, '_destroy');
          // end/finished do not prove destruction, and emitClose may be false.
          // Observe the actual implementation callback before pipeline can destroy.
          body._destroy = function (error, callback) {
            if (ownDestroy) this._destroy = destroy;
            else delete this._destroy;
            const complete = error => {
              callback(error);
              // Node queues destroy's error/close events after its callback.
              process.nextTick(done);
            };
            try { destroy.call(this, error, complete); }
            catch (error) { complete(error); }
          };
        } else if (!body.closed) {
          // Destruction started before handoff; its callback is already captured.
          // close may be disabled, so observe actual completion, never elapsed time.
          timer = setInterval(() => { if (body.closed) done(); }, 25);
        }
        if (body.closed) process.nextTick(done);
      });
      body.once('end', releaseInBackground);
      body.once('error', releaseInBackground);
    }
    signal.addEventListener('abort', releaseInBackground, { once: true });
    if (signal.aborted) return;
    if (reply?.ok === false) {
      envelope(reply, contract, codes);
      send(502, reply);
      return;
    }
    const headers = streamHeaders(reply);
    const start = () => {
      if (signal.aborted || response.destroyed) throw new BridgeError('CANCELLED');
      if (!response.headersSent) {
        response.strictContentLength = Object.hasOwn(headers, 'content-length');
        response.writeHead(200, { ...headers, 'x-meow-request-id': requestId });
      }
    };
    // The sink keeps pre-header source failures available for a JSON error.
    // ServerResponse write callbacks retain real downstream backpressure.
    const destination = new Writable({
      highWaterMark: response.writableHighWaterMark,
      write(chunk, encoding, done) {
        try { start(); response.write(chunk, encoding, done); }
        catch (error) { done(error); }
      },
      final(done) {
        let settled = false;
        const complete = error => { if (!settled) { settled = true; done(error); } };
        // end() may queue its callback before throwing a content-length error.
        try { start(); response.end(complete); }
        catch (error) { complete(error); }
      },
    });
    await pipeline(body, destination, { signal });
  } catch (error) {
    if (response.headersSent) response.destroy();
    throw error;
  } finally {
    try { await release(); }
    finally {
      signal.removeEventListener('abort', releaseInBackground);
      if (body instanceof Readable) {
        body.off('end', releaseInBackground);
        body.off('error', releaseInBackground);
      }
    }
  }
}

function readBody(stream, { maximumBytes, limitCode, signal } = {}) {
  return new Promise((resolve, reject) => {
    let chunks = [], length = 0, settled = false;
    function finish(error) {
      if (settled) return;
      settled = true;
      stream.removeListener('data', data);
      stream.removeListener('end', end);
      stream.removeListener('error', errorEvent);
      stream.removeListener('aborted', aborted);
      signal?.removeEventListener('abort', aborted);
      if (error) { chunks = []; stream.resume(); reject(error); }
      else { const bytes = Buffer.concat(chunks, length); chunks = []; resolve(bytes); }
    }
    const data = bytes => {
      length += bytes.length;
      if (maximumBytes !== undefined && length > maximumBytes) {
        finish(Object.assign(new Error(), { sourceLimit: true, code: limitCode }));
      } else chunks.push(bytes);
    };
    const end = () => finish();
    const aborted = () => finish(new BridgeError('CANCELLED'));
    const errorEvent = () => finish(new BridgeError('CLOSED'));
    stream.on('data', data);
    stream.once('end', end);
    stream.once('error', errorEvent);
    stream.once('aborted', aborted);
    signal?.addEventListener('abort', aborted, { once: true });
    if (signal?.aborted) aborted();
  });
}

/** One session listener. Handlers remain owned until they really settle. */
export function createHttpServer({ sessionId, key, resolve: resolveRoute, onClose } = {}) {
  validateSession({ sessionId, key });
  if (typeof resolveRoute !== 'function' || onClose !== undefined && typeof onClose !== 'function') invalid();
  const active = new Map(), sockets = new Set();
  let baseUrl, starting, closing, closed = false, sequence = 0, streamCleanupFailed = false;
  const server = http.createServer({ requestTimeout: 0 }, (request, response) => {
    const requestId = request.headers['x-meow-request-id'];
    const controller = new AbortController();
    const abort = () => { if (!response.writableFinished) controller.abort(); };
    const send = (status, value) => {
      if (controller.signal.aborted || response.destroyed) return;
      if (response.headersSent) { response.destroy(); return; }
      response.writeHead(status, { 'content-type': 'application/json',
        ...(uuid.test(requestId ?? '') ? { 'x-meow-request-id': requestId } : {}),
        ...(status === 413 ? { connection: 'close' } : {}) });
      response.end(Buffer.from(JSON.stringify(value)));
    };
    request.on('aborted', abort);
    response.on('close', abort);
    response.on('error', () => { abort(); response.destroy(); });
    // An error after readBody removes its temporary listener must remain handled.
    request.on('error', abort);
    const handle = async () => {
      let route;
      try {
        if (closed) { send(403, failed('CLOSED')); request.resume(); return; }
        const counts = new Map();
        for (let i = 0; i < request.rawHeaders.length; i += 2) {
          const name = request.rawHeaders[i].toLowerCase();
          counts.set(name, (counts.get(name) ?? 0) + 1);
        }
        if (request.headers.host !== new URL(baseUrl).host ||
            request.headers['x-meow-session'] !== sessionId ||
            !sameKey(request.headers.authorization, key) ||
            ['host', 'authorization', 'x-meow-session', 'x-meow-request-id'].some(name => counts.get(name) !== 1)) {
          send(401, failed('PERMISSION_DENIED')); request.resume(); return;
        }
        if (!uuid.test(requestId ?? '') || active.has(requestId)) {
          send(400, failed('INVALID_REQUEST')); request.resume(); return;
        }
        if (!request.url.startsWith('/') || request.url.startsWith('//')) invalid();
        const url = new URL(request.url, baseUrl);
        if (url.pathname + url.search !== request.url || url.hash) invalid();
        route = resolveRoute({ method: request.method, url });
        if (!route) { send(404, failed('METHOD_NOT_FOUND')); request.resume(); return; }
        if (route.contentType && request.headers['content-type'] !== route.contentType) {
          send(415, failed('INVALID_REQUEST')); request.resume(); return;
        }
        const owner = { controller, response, current: true, sequence: ++sequence, promise: undefined };
        active.set(requestId, owner);
        owner.promise = (async () => {
          let admitted = false;
          try {
            const bytes = await readBody(request, { maximumBytes: route.maximumBytes,
              limitCode: route.limitCode, signal: controller.signal });
            if (controller.signal.aborted) return;
            let input;
            if (!route.contentType) { if (bytes.length) invalid(); input = null; }
            else input = route.contentType === 'application/json' ? decodeJSONValue(bytes) : bytes;
            route.validate?.(input);
            admitted = true;
            const result = await route.handler(input, { signal: controller.signal, sessionId,
              requestId, sequence: owner.sequence,
              isCurrent: () => owner.current && !closed && !controller.signal.aborted });
            if (route.responseType === 'stream') {
              await streamResponse(result, response, { signal: controller.signal, requestId,
                contract: route.contract, codes: route.codes,
                onCleanupFailure: () => { streamCleanupFailed = true; } }, send);
              return;
            }
            if (controller.signal.aborted) return;
            envelope(result, route.contract, route.codes);
            send(route.status?.(result) ?? 200, result);
          } catch (error) {
            if (controller.signal.aborted) return;
            if (error?.sourceLimit) send(413, { ok: false, contract: route.contract, code: route.limitCode });
            else send(!admitted && error?.code === 'INVALID_REQUEST' ? 400 : route.responseType === 'stream' ? 502 : 200,
              failed(admitted && error?.code === 'INVALID_REQUEST' ? 'INVALID_RESPONSE'
                : error instanceof BridgeError ? error.code : 'INVALID_RESPONSE'));
          } finally {
            // A failed error response has no pending finish to wait for.
            if (!response.writableEnded && !response.destroyed) response.destroy();
            // Response backpressure has physical ownership after the handler returns.
            if (!response.writableFinished && !response.destroyed) await new Promise(done => {
              const finish = () => { response.off('finish', finish); response.off('close', finish); done(); };
              response.once('finish', finish); response.once('close', finish);
            });
            owner.current = false;
            active.delete(requestId);
          }
        })();
        await owner.promise;
      } catch { send(400, failed('INVALID_REQUEST')); request.resume(); }
    };
    void handle();
  });
  server.on('connection', socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  return Object.freeze({
    get baseUrl() { return baseUrl; },
    get state() { return { closed, active: active.size }; },
    async abortActive(exceptRequestId) {
      const owners = [...active.entries()].filter(([id]) => id !== exceptRequestId).map(([, owner]) => owner);
      for (const owner of owners) { owner.controller.abort(); owner.response.destroy(); }
      const results = await Promise.allSettled(owners.map(owner => owner.promise));
      if (streamCleanupFailed || results.some(result => result.status === 'rejected')) {
        throw new BridgeError('INVALID_RESPONSE');
      }
    },
    start() {
      if (closed) return Promise.reject(new BridgeError('CLOSED'));
      return starting ??= new Promise((resolve, reject) => {
        const failure = () => { server.off('listening', listening); reject(new BridgeError('CLOSED')); };
        const listening = () => {
          server.off('error', failure);
          baseUrl = `http://127.0.0.1:${server.address().port}`;
          resolve(baseUrl);
        };
        server.once('error', failure); server.once('listening', listening);
        server.listen(0, '127.0.0.1');
      });
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        if (starting) await starting.catch(() => {});
        for (const owner of active.values()) { owner.controller.abort(); owner.response.destroy(); }
        const drained = [...active.values()].map(owner => owner.promise);
        const stopped = new Promise(resolve => server.close(() => resolve()));
        for (const socket of sockets) socket.destroy();
        const results = await Promise.allSettled([stopped, ...drained]);
        try { await onClose?.(); }
        finally {
          if (streamCleanupFailed || results.some(result => result.status === 'rejected')) {
            throw new BridgeError('INVALID_RESPONSE');
          }
        }
      })();
    },
  });
}

/** Standard HTTP client; no retry, redirects, response-size branch or body processing. */
export function createHttpClient({ baseUrl, sessionId, key, timeout = 30000 } = {}) {
  const endpoint = validateEndpoint(baseUrl);
  validateSession({ sessionId, key });
  if (!Number.isSafeInteger(timeout) || timeout < 1 || timeout > 300000) invalid();
  const agent = new http.Agent({ keepAlive: true });
  const pending = new Set();
  let closed = false, closing;
  function request({ method = 'POST', path, body, contract = HTTP_PROTOCOL, codes = publicCodes,
    signal, timeout: deadline = timeout, requestId = randomUUID(), raw = false } = {}) {
    if (closed) return Promise.reject(new BridgeError('CLOSED'));
    if (signal?.aborted) return Promise.reject(new BridgeError('CANCELLED'));
    let bytes;
    try {
      if (typeof path !== 'string' || !path.startsWith('/v1/') || path.startsWith('//') ||
          !uuid.test(requestId) || !Number.isSafeInteger(deadline) || deadline < 1 || deadline > 300000) invalid();
      const url = new URL(path, endpoint);
      if (url.origin !== endpoint.origin || url.pathname + url.search !== path || url.hash ||
          !['GET', 'POST', 'PUT'].includes(method)) invalid();
      if (raw && !(body instanceof Uint8Array)) invalid();
      bytes = method === 'GET' ? undefined : raw ? Buffer.from(body) : encodeJSONValue(body);
    } catch (error) { return Promise.reject(error instanceof BridgeError ? error : new BridgeError('INVALID_REQUEST')); }
    return new Promise((resolve, reject) => {
      let failure, reply, result, settled = false, transportClosed = false, reading = false, received = false;
      const owner = { cancel: code => {
        failure ??= new BridgeError(code);
        reply?.destroy(); outgoing.destroy();
      }, done: undefined };
      let drained;
      owner.done = new Promise(done => { drained = done; });
      const outgoing = http.request(new URL(path, endpoint), {
        method, agent, headers: { authorization: `Bearer ${key}`, 'x-meow-session': sessionId,
          'x-meow-request-id': requestId,
          ...(bytes === undefined ? {} : { 'content-type': raw ? 'application/octet-stream' : 'application/json',
            'content-length': bytes.length }) },
      });
      pending.add(owner);
      const abort = () => owner.cancel('CANCELLED');
      const timer = setTimeout(() => owner.cancel('TIMEOUT'), deadline);
      function drain() {
        if (!transportClosed || reading) return;
        settle(failure ?? (received ? undefined : new BridgeError('CLOSED')), result);
        pending.delete(owner); drained();
      }
      function settle(error, value) {
        if (settled) return;
        settled = true; clearTimeout(timer); signal?.removeEventListener('abort', abort);
        if (error) reject(error); else resolve(value);
      }
      outgoing.on('response', response => {
        reply = response; reading = true;
        response.on('error', () => { failure ??= new BridgeError('CLOSED'); });
        void (async () => {
          try {
            if (response.headers['x-meow-request-id'] !== requestId ||
                response.headers['content-type'] !== 'application/json' ||
                ![200, 400, 401, 403, 404, 413, 415].includes(response.statusCode)) {
              throw new BridgeError('INVALID_RESPONSE');
            }
            const bytes = await readBody(response);
            if (!response.complete) throw new BridgeError('CLOSED');
            const value = parseJSON(bytes);
            if (value?.contract === HTTP_PROTOCOL && contract !== HTTP_PROTOCOL) {
              envelope(value, HTTP_PROTOCOL, publicCodes);
              if (value.ok) throw new BridgeError('INVALID_RESPONSE');
            } else envelope(value, contract, codes);
            if (response.statusCode !== 200 && value.ok) throw new BridgeError('INVALID_RESPONSE');
            received = true; result = value;
          } catch (error) {
            failure ??= new BridgeError(['CLOSED', 'CANCELLED'].includes(error.code) ? 'CLOSED' : 'INVALID_RESPONSE');
            response.destroy(); outgoing.destroy();
          } finally { reading = false; drain(); }
        })();
      });
      outgoing.on('error', () => { failure ??= new BridgeError('CLOSED'); });
      outgoing.once('close', () => {
        transportClosed = true;
        drain();
      });
      signal?.addEventListener('abort', abort, { once: true });
      if (signal?.aborted) abort();
      else outgoing.end(bytes);
    });
  }
  return Object.freeze({ request, get state() { return { closed, pending: pending.size }; },
    close() {
      return closing ??= (async () => {
        closed = true;
        const owners = [...pending];
        for (const owner of owners) owner.cancel('CLOSED');
        agent.destroy();
        await Promise.all(owners.map(owner => owner.done));
      })();
    },
  });
}
