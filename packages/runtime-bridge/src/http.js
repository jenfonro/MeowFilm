import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { Readable, Writable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { BridgeError, HTTP_PROTOCOL } from './errors.js';
import { decodeJSONValue } from './json.js';
import { stringifyJSONValue } from './json-value.js';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$(?![\s\S])/i;
const streamHeaderNames = new Set(['content-type', 'content-length', 'cache-control', 'etag', 'last-modified']);
const invalid = () => { throw new BridgeError('INVALID_REQUEST'); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

export function validateSession({ sessionId }) {
  if (typeof sessionId !== 'string' || !uuid.test(sessionId)) invalid();
}
function envelope(value, contract) {
  if (!object(value) || typeof value.ok !== 'boolean' || value.contract !== contract) throw new BridgeError('INVALID_RESPONSE');
  if (value.ok) {
    if (!Object.hasOwn(value, 'value')) throw new BridgeError('INVALID_RESPONSE');
  } else if (typeof value.code !== 'string' || !value.code) {
    throw new BridgeError('INVALID_RESPONSE');
  }
  return value;
}
const failed = code => ({ ok: false, contract: HTTP_PROTOCOL, code });

function streamHeaders(reply) {
  if (!object(reply) ||
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

async function streamResponse(reply, response, { signal, requestId, contract, onCleanupFailure }, send) {
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
      envelope(reply, contract);
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

function readBody(stream, { signal } = {}) {
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
      chunks.push(bytes);
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
export function createHttpServer({ sessionId = randomUUID(), resolve: resolveRoute, onClose } = {}) {
  validateSession({ sessionId });
  if (typeof resolveRoute !== 'function' || onClose !== undefined && typeof onClose !== 'function') invalid();
  const active = new Map(), sockets = new Set();
  let baseUrl, starting, closing, closed = false, sequence = 0, streamCleanupFailed = false;
  const server = http.createServer({ requestTimeout: 0, keepAliveTimeout: 0 }, (request, response) => {
    const rawRequestId = request.headers['x-meow-request-id'];
    const requestId = typeof rawRequestId === 'string' && rawRequestId && !active.has(rawRequestId)
      ? rawRequestId : randomUUID();
    const controller = new AbortController();
    const abort = () => { if (!response.writableFinished) controller.abort(); };
    const send = (status, value) => {
      if (controller.signal.aborted || response.destroyed) return;
      if (response.headersSent) { response.destroy(); return; }
      response.writeHead(status, { 'content-type': 'application/json',
        'x-meow-request-id': requestId });
      response.end(Buffer.from(stringifyJSONValue(value, -1)));
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
        if (!request.url.startsWith('/') || request.url.startsWith('//')) invalid();
        const url = new URL(request.url, baseUrl);
        if (url.pathname + url.search !== request.url || url.hash) invalid();
        route = resolveRoute({ method: request.method, url, headers: request.headers });
        if (!route) { send(404, failed('METHOD_NOT_FOUND')); request.resume(); return; }
        if (route.contentType && request.headers['content-type'] !== route.contentType) {
          send(415, failed('INVALID_REQUEST')); request.resume(); return;
        }
        const owner = { controller, response, current: true, sequence: ++sequence, promise: undefined };
        active.set(requestId, owner);
        owner.promise = (async () => {
          let admitted = false;
          try {
            const bytes = await readBody(request, { signal: controller.signal });
            if (controller.signal.aborted) return;
            let input;
            if (!route.contentType) { if (bytes.length) invalid(); input = null; }
            else input = route.contentType === 'application/json' ? decodeJSONValue(bytes) : bytes;
            route.validate?.(input);
            admitted = true;
            const debugOpId = typeof request.headers['x-meow-debug-op'] === 'string' && request.headers['x-meow-debug-op']
              ? request.headers['x-meow-debug-op'] : undefined;
            const debugTraceId = typeof request.headers['x-meow-debug-trace'] === 'string' && request.headers['x-meow-debug-trace']
              ? request.headers['x-meow-debug-trace'] : undefined;
            const result = await route.handler(input, { signal: controller.signal, sessionId,
              requestId, sequence: owner.sequence,
              ...(debugOpId ? { debugOpId } : {}),
              ...(debugTraceId ? { debugTraceId } : {}),
              isCurrent: () => owner.current && !closed && !controller.signal.aborted });
            if (route.responseType === 'stream') {
              await streamResponse(result, response, { signal: controller.signal, requestId,
                contract: route.contract,
                onCleanupFailure: () => { streamCleanupFailed = true; } }, send);
              return;
            }
            if (controller.signal.aborted) return;
            envelope(result, route.contract);
            send(route.status?.(result) ?? 200, result);
          } catch (error) {
            if (controller.signal.aborted) return;
            send(!admitted && error?.code === 'INVALID_REQUEST' ? 400 : route.responseType === 'stream' ? 502 : 200,
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
