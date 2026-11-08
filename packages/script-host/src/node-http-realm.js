import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fault, requireValue } from './errors.js';
import { Headers } from './node-web-values.js';

/** Native Web request/response values over the instance's managed HTTP
 * transport, so fetch shares listener mapping, adapters and cancellation. */
export function createFetch(node) {
  const send = async (request, hops = 0) => {
    node.ownerScope().assertOpen();
    const target = new URL(request.url);
    requireValue(['http:', 'https:'].includes(target.protocol), 'PERMISSION_DENIED');
    const transport = node.services[target.protocol === 'https:' ? 'https' : 'http'];
    const headers = Object.fromEntries(request.headers);
    if (!request.headers.has('accept-encoding')) headers['accept-encoding'] = 'gzip, deflate, br';
    // Keep a native tee branch only while a redirect may need to replay the
    // upload. The active branch still streams with transport backpressure.
    let replay = request.body && request.redirect === 'follow' ? request.clone() : null;
    let client, incoming, body, detached = false;
    const discardReplay = () => {
      if (replay?.body) replay.body.cancel().catch(() => {});
      replay = null;
    };
    const detach = () => {
      if (detached) return;
      detached = true;
      request.signal.removeEventListener('abort', abort);
    };
    const abort = () => {
      const error = request.signal.reason ?? fault('CANCELLED');
      body?.destroy(error); incoming?.destroy(error); client?.destroy(error);
      discardReplay();
    };
    try {
      incoming = await new Promise((resolve, reject) => {
        let responded = false;
        client = transport.request(target, { method: request.method, headers }, response => {
          responded = true; resolve(response);
        });
        client.once('error', reject);
        client.once('close', () => { if (!responded) reject(fault('NETWORK_ERROR')); });
        request.signal.addEventListener('abort', abort, { once: true });
        if (request.signal.aborted) { abort(); reject(request.signal.reason); return; }
        if (request.body) {
          pipeline(Readable.fromWeb(request.body), client).catch(reject);
        } else client.end();
      });
      const location = incoming.headers.location;
      if ([301, 302, 303, 307, 308].includes(incoming.statusCode) && location && request.redirect !== 'manual') {
        requireValue(request.redirect !== 'error' && hops < 20, 'NETWORK_ERROR');
        const next = new URL(location, target);
        const method = incoming.statusCode === 303 && request.method !== 'HEAD' ||
          [301, 302].includes(incoming.statusCode) && request.method === 'POST' ? 'GET' : request.method;
        const redirectedHeaders = new Headers(request.headers);
        if (target.origin !== next.origin) for (const key of ['authorization', 'cookie', 'proxy-authorization'])
          redirectedHeaders.delete(key);
        if (method === 'GET') for (const key of ['content-length', 'content-type']) redirectedHeaders.delete(key);
        const redirected = new node.web.Request(next, { method, headers: redirectedHeaders,
          signal: request.signal, redirect: request.redirect,
          ...(method !== 'GET' && method !== 'HEAD' && replay?.body
            ? { body: replay.body, duplex: 'half' } : {}) });
        if (!redirected.body) discardReplay();
        else replay = null;
        incoming.destroy(); client.destroy(); detach();
        return send(redirected, hops + 1);
      }
      discardReplay();
      body = incoming;
      const encoding = String(incoming.headers['content-encoding'] ?? '').toLowerCase();
      const decoder = { __proto__: null, gzip: 'createGunzip', 'x-gzip': 'createGunzip',
        deflate: 'createInflate', br: 'createBrotliDecompress' }[encoding];
      if (decoder && request.method !== 'HEAD' && ![204, 304].includes(incoming.statusCode)) {
        body = node.builtins.zlib[decoder]();
        pipeline(incoming, body).catch(error => body.destroy(error));
      }
      const closed = () => {
        detach();
        if (!incoming.complete) {
          const error = fault('NETWORK_ERROR');
          body.destroy(error); incoming.destroy(error); client.destroy(error);
        }
      };
      incoming.once('aborted', () => body.destroy(fault('NETWORK_ERROR')));
      body.once('end', detach);
      body.once('close', closed);
      const empty = request.method === 'HEAD' || [204, 205, 304].includes(incoming.statusCode);
      const responseHeaders = new Headers();
      for (const [key, value] of Object.entries(incoming.headers))
        for (const part of Array.isArray(value) ? value : [value])
          if (part !== undefined) responseHeaders.append(key, part);
      const response = new node.web.Response(empty ? null : node.ownWeb(Readable.toWeb(body), 'readableWeb'), {
        status: incoming.statusCode, statusText: incoming.statusMessage,
        headers: responseHeaders,
      });
      Object.defineProperties(response, { url: { value: target.href }, redirected: { value: hops > 0 } });
      if (empty) { incoming.resume(); detach(); }
      return response;
    } catch (error) {
      detach(); discardReplay(); body?.destroy(); incoming?.destroy(); client?.destroy(); throw error;
    }
  };
  return async (input, options) => {
    const request = new node.web.Request(input, options);
    return send(request);
  };
}
