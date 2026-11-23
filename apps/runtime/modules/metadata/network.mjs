import http from 'node:http';
import https from 'node:https';
import { performance } from 'node:perf_hooks';
import { createNodeNetwork } from '@meowfilm/script-host';
import { metadataFailure } from './settings.mjs';

function address(value) {
  let url;
  try { url = new URL(value); } catch { throw metadataFailure('INVALID_REQUEST'); }
  if (!['http:', 'https:'].includes(url.protocol) || url.hash) {
    throw metadataFailure('INVALID_REQUEST');
  }
  return url.href;
}

/** Reuse the buffered HTTP adapter, including explicit headers on redirects.
 * Cookie-jar consumers request individual hops and select their own cookies. */
export function createMetadataRequest({ debug, provider = 'metadata' } = {}) {
  const network = createNodeNetwork();
  return async (url, { signal, ...options } = {}) => {
    const startedAt = debug ? performance.now() : 0;
    debug?.emit({
      level: 'debug',
      cat: 'film-catalog',
      sub: `${provider}.http`,
      event: 'http.request',
      data: { url, method: options.method ?? 'GET', headers: options.headers ?? {}, body: options.body ?? null },
    });
    try {
      const result = await network({ url, signal, validateURL: address,
        options });
      const output = { status: result.statusCode, headers: result.headers, body: result.body, url: result.url };
      debug?.emit({
        level: 'debug',
        cat: 'film-catalog',
        sub: `${provider}.http`,
        event: 'http.response',
        durationMs: debug ? performance.now() - startedAt : undefined,
        data: output,
      });
      return output;
    } catch (error) {
      debug?.emit({
        level: 'warn',
        cat: 'film-catalog',
        sub: `${provider}.http`,
        event: 'http.error',
        durationMs: debug ? performance.now() - startedAt : undefined,
        data: { url, code: error?.code ?? null },
        error,
      });
      throw metadataFailure(signal?.aborted ? 'CANCELLED'
        : error?.code === 'INVALID_ARGUMENT' ? 'INVALID_REQUEST' : 'NETWORK_ERROR');
    }
  };
}

export function createMetadataImageTransport() {
  const agents = { 'http:': new http.Agent({ keepAlive: true }), 'https:': new https.Agent({ keepAlive: true }) };
  return {
    open: (target, signal) => openMetadataImage(target, signal, agents),
    async close() {
      const sockets = new Set(Object.values(agents).flatMap(agent =>
        [...Object.values(agent.sockets).flat(), ...Object.values(agent.freeSockets).flat()]));
      const drained = [...sockets].map(socket => socket.closed ? undefined
        : new Promise(done => socket.once('close', done)));
      for (const agent of Object.values(agents)) agent.destroy();
      await Promise.all(drained);
    },
  };
}

async function imageExchange(url, headers, signal, agents) {
  const resources = [], drained = [];
  let request, response, disposing;
  function own(resource) {
    resources.push(resource);
    drained.push(new Promise(done => resource.once('close', done)));
    return resource;
  }
  const dispose = () => disposing ??= (async () => {
    // A completed response has detached its socket for reuse. Only an
    // unfinished exchange still owns a socket whose close must be awaited.
    const socket = request?.socket;
    const socketClosed = !response?.readableEnded && socket && !socket.closed
      ? new Promise(done => socket.once('close', done)) : undefined;
    for (const resource of resources) resource.destroy();
    await Promise.all([...drained, socketClosed]);
  })();
  try {
    await new Promise((resolve, reject) => {
      const target = new URL(address(url));
      const transport = target.protocol === 'https:' ? https : http;
      request = own(transport.request(target, { method: 'GET', agent: agents[target.protocol], signal,
        headers: { ...headers, 'accept-encoding': 'identity' } }, reply => {
        response = own(reply);
        // The stream can fail before the bridge has installed its pipeline.
        reply.on('error', () => {});
        resolve();
      }));
      request.on('error', () => reject(metadataFailure(signal.aborted ? 'CANCELLED' : 'NETWORK_ERROR')));
      request.end();
    });
    return { response, dispose };
  } catch (error) {
    await dispose();
    throw error;
  }
}

/** Returns an owned stream, never a buffered image or an upstream redirect.
 * dispose drains request/response closure; unfinished sockets are revoked. */
async function openMetadataImage({ url, headers }, signal, agents) {
  for (let hop = 0; ; hop++) {
    if (signal.aborted) throw metadataFailure('CANCELLED');
    const exchange = await imageExchange(url, headers, signal, agents);
    const { response, dispose } = exchange;
    try {
      if ([301, 302, 303, 307, 308].includes(response.statusCode)) {
        if (hop === 5 || !response.headers.location) throw metadataFailure('UPSTREAM_HTTP');
        try { url = address(new URL(response.headers.location, url).href); }
        catch { throw metadataFailure('UPSTREAM_FORMAT'); }
        await dispose();
        continue;
      }
      if (response.statusCode !== 200) throw metadataFailure('UPSTREAM_HTTP');
      if (!/^image\/[a-z0-9.+-]+(?:\s*;[^\r\n]*)?$/i.test(response.headers['content-type'] ?? '')) {
        throw metadataFailure('UPSTREAM_FORMAT');
      }
      const output = { 'content-type': response.headers['content-type'] };
      for (const name of ['content-length', 'etag', 'last-modified']) {
        if (response.headers[name] !== undefined) output[name] = response.headers[name];
      }
      // HTTP caching must not tie a port/session or upstream validator to the
      // stable resource identity. Native image caches own their own lifetime.
      output['cache-control'] = 'no-store';
      return { status: 200, headers: output, body: response, dispose };
    } catch (error) {
      await dispose();
      throw error;
    }
  }
}
