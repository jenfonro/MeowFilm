import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { decodeJSONValue } from '@meowfilm/runtime-bridge';
import { fault, requireValue, safeError } from './errors.js';
import { ResourceScope } from './scope.js';
import {
  CatProxyResources, proxyPlaylistInput, readProxyPlaylist,
  proxyPlaylistBody, proxyManifestLocation, resolveProxyManifest,
} from './cat-media-proxy.js';

const hopByHop = new Set(['connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
  'te', 'trailer', 'transfer-encoding', 'upgrade']);

function cleanHopByHop(input) {
  const headers = { ...input };
  const connections = Object.entries(headers)
    .filter(([key]) => key.toLowerCase() === 'connection')
    .flatMap(([, value]) => Array.isArray(value) ? value : [value]);
  const denied = new Set([...hopByHop, ...connections.flatMap(value => String(value ?? '').split(','))
    .map(value => value.trim().toLowerCase()).filter(Boolean)]);
  for (const key of Object.keys(headers)) if (denied.has(key.toLowerCase())) delete headers[key];
  return headers;
}

function deleteHeader(headers, name) {
  for (const key of Object.keys(headers)) if (key.toLowerCase() === name) delete headers[key];
}

export function resolveCatProxyTarget(runtime, url, parts) {
  const [, installationId, generation, id] = parts;
  const entry = runtime.proxies.get(id), snapshot = runtime.snapshots.get(installationId);
  if (!entry || !snapshot || entry.session.installationId !== installationId ||
    String(entry.session.generation) !== generation ||
    entry.session !== runtime.sessions.get(installationId) || !entry.session.live ||
    entry.session.stopped || !entry.session.worker)
    return null;
  const prefix = `/proxy/${installationId}/${generation}/${id}`;
  if (url.pathname !== prefix && !url.pathname.startsWith(prefix + '/')) return null;
  return { entry, snapshot, path: url.pathname.slice(prefix.length) || '/' };
}

export function sweepCatProxies(runtime, session) {
  const snapshot = session ? runtime.snapshots.get(session.installationId) : null;
  const origins = snapshot ? new Set(snapshot.endpoints.map(endpoint => endpoint.origin)) : null;
  for (const [id, entry] of runtime.proxies) {
    const current = runtime.snapshots.get(entry.session.installationId);
    if (!current || !entry.session.live || entry.session.stopped || !entry.session.worker ||
      entry.session !== runtime.sessions.get(entry.session.installationId) ||
      session && entry.session === session && !origins?.has(entry.origin)) runtime.proxies.delete(id);
  }
}

export async function listenCatServer(runtime, port) {
  requireValue(Number.isInteger(port) && port >= 0 && port <= 65535, 'CORRUPT_STORAGE');
  const ownerRef = new WeakRef(runtime);
  const server = createServer((request, response) => {
    const owner = ownerRef.deref();
    if (!owner) {
      try {
        response.writeHead(503, { 'content-length': '0' });
        response.end();
      } catch {
        try { response.destroy(); } catch {}
      }
      return;
    }
    void handleCatRequest(owner, request, response).catch(error => {
      try {
        if (response.destroyed || response.writableEnded) return;
        if (!response.headersSent) {
          const status = error?.code === 'PERMISSION_DENIED' ? 403
            : error?.code === 'STARTING' ? 503
            : error?.code === 'RESOURCE_LIMIT' ? 413
              : ['INVALID_ARGUMENT', 'INVALID_JSON'].includes(error?.code) ? 400 : 502;
          response.writeHead(status, { 'content-type': 'application/json' });
          response.end(JSON.stringify({ error: { code: safeError(error).code } }));
        } else response.destroy();
      } catch {
        try { response.destroy(); } catch {}
      }
    });
  });
  try {
    await new Promise((resolve, reject) => {
      let settled = false;
      const failed = error => {
        if (settled) return;
        settled = true;
        server.off('error', failed);
        reject(error);
      };
      const ready = () => {
        if (settled) return;
        settled = true;
        server.off('error', failed);
        server.on('error', () => {
          try {
            ownerRef.deref()?.onDiagnostic?.({
              kind: 'ScriptHostError', reason: 'host',
              operation: 'cat.aggregate', code: 'SERVICE_ERROR',
            });
          } catch {}
        });
        resolve();
      };
      server.once('error', failed);
      server.listen(port, '127.0.0.1', ready);
    });
  } catch (error) {
    try { await runtime.closeOwnedServer(server); } catch { /* Preserve the listen failure. */ }
    throw error;
  }
  return server;
}

export async function handleCatRequest(runtime, request, response) {
  const origin = `http://127.0.0.1:${runtime.server.address().port}`;
  const url = new URL(request.url, origin), parts = url.pathname.split('/').filter(Boolean);
  if (parts[0] === 'proxy') return forwardCatProxy(runtime, request, response, url, parts);
  requireValue(parts[0] === 'cat' && parts.length === 4, 'INVALID_ARGUMENT');
  let decoded;
  try { decoded = parts.map(decodeURIComponent); }
  catch { throw fault('INVALID_ARGUMENT'); }
  const [, installationId, siteId, operation] = decoded;
  const scope = new ResourceScope(undefined, runtime.limit('requestTimeoutMs'));
  const abort = () => scope.close(fault('CANCELLED'));
  const responseClosed = () => { if (!response.writableFinished) abort(); };
  const chunks = [];
  request.once('aborted', abort);
  response.once('close', responseClosed);
  try {
    scope.add(() => {
      if (!request.complete && !request.destroyed) request.destroy();
    });
    await scope.race((async () => {
      for await (const chunk of request) chunks.push(chunk);
    })());
    let payload;
    try {
      payload = chunks.length ? decodeJSONValue(Buffer.concat(chunks)) : Object.fromEntries(url.searchParams);
    } catch { throw fault('INVALID_JSON'); }
    const result = await runtime.invoke({ installationId, siteId, operation, payload }, { signal: scope.signal });
    response.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
    response.end(typeof result === 'string' ? result : stringifyJSONValue(result));
  } finally {
    chunks.length = 0;
    request.off('aborted', abort);
    response.off('close', responseClosed);
    scope.close();
  }
}

async function forwardCatProxy(runtime, request, response, url, parts) {
  requireValue(!runtime.closed, 'HOST_CLOSED');
  let target = resolveCatProxyTarget(runtime, url, parts);
  requireValue(target, 'STALE_SESSION');
  if (!['GET', 'HEAD'].includes(request.method)) {
    response.writeHead(405, { allow: 'GET, HEAD', 'content-length': 0 });
    response.end();
    return;
  }
  const manifest = resolveProxyManifest(target.entry, target.path, url.search);
  if (manifest !== undefined) {
    const next = new URL(manifest.url);
    const mapped = runtime.mapSnapshotProxy(target.entry.session, target.snapshot, next.href);
    if (mapped === next.href) {
      target = { ...target, upstreamURL: next, hlsVariables: manifest.hlsVariables };
    } else {
      const mappedURL = new URL(mapped);
      const resolved = resolveCatProxyTarget(runtime, mappedURL, mappedURL.pathname.split('/').filter(Boolean));
      requireValue(resolved, 'STALE_SESSION');
      target = { ...resolved, url: mappedURL, hlsVariables: manifest.hlsVariables };
    }
  }
  const { entry } = target;
  const task = { session: entry.session, scope: new ResourceScope(entry.session.proxyLifecycle.signal) };
  task.done = Promise.resolve().then(() => streamCatProxy(runtime, request, response, target, task.scope))
    .finally(() => runtime.proxyTasks.delete(task));
  runtime.proxyTasks.add(task);
  return task.done;
}

async function streamCatProxy(runtime, request, response, { entry, snapshot, path, url: resolvedURL, upstreamURL, hlsVariables }, scope) {
  const url = resolvedURL ?? new URL(request.url, `http://127.0.0.1:${runtime.aggregatePort}`);
  const target = upstreamURL ?? new URL(entry.origin);
  if (!upstreamURL) { target.pathname = path; target.search = url.search; }
  const control = new ResourceScope(scope.signal, runtime.limit('requestTimeoutMs'));
  const resources = new CatProxyResources(runtime.limit('cleanupTimeoutMs'));
  const socketAssigned = socket => resources.track(socket);
  let client, upstream, clearHeaders, controlActive = true;
  const abort = () => scope.close(fault('CANCELLED'));
  const responseClosed = () => { if (!response.writableFinished) abort(); };
  let failure;
  response.once('close', responseClosed);
  request.once('aborted', abort);
  try {
    const proxyOrigin = new URL(`http://${request.headers.host ?? `127.0.0.1:${runtime.aggregatePort}`}`).origin;
    const map = value => runtime.mapSnapshotProxy(entry.session, snapshot, value, proxyOrigin);
    const headers = cleanHopByHop(request.headers);
    deleteHeader(headers, 'x-meow-request-scope');
    // Replace only the aggregate transport's default Host. An explicitly
    // supplied virtual Host belongs to this media request and stays intact.
    const caller = new URL(request.url, `http://127.0.0.1:${runtime.aggregatePort}`);
    if (headers.host?.toLowerCase() === caller.host.toLowerCase()) deleteHeader(headers, 'host');
    headers['accept-encoding'] = 'identity';
    if (request.aborted || response.destroyed) abort();
    scope.assertOpen();
    client = (target.protocol === 'https:' ? httpsRequest : httpRequest)(target, {
      method: request.method, headers, agent: false,
      ...(target.protocol === 'https:' && !upstreamURL ? { rejectUnauthorized: false } : {}),
    });
    resources.track(client);
    client.once('socket', socketAssigned);
    scope.assertOpen();
    upstream = await control.race(new Promise((resolve, reject) => {
      let settled = false;
      const cleanup = () => {
        client.off('response', responded);
        client.off('error', failed);
        client.off('close', closed);
      };
      clearHeaders = cleanup;
      const settle = (success, value) => {
        if (settled) {
          if (success) value?.destroy?.();
          return;
        }
        settled = true;
        cleanup();
        if (success && scope.signal.aborted) {
          value?.destroy?.();
          reject(scope.reason ?? fault('CANCELLED'));
        } else if (success) resolve(value);
        else reject(value);
      };
      const responded = value => {
        upstream = value;
        resources.track(value);
        settle(true, value);
      };
      const failed = error => settle(false, error);
      const closed = () => settle(false,
        scope.signal.aborted ? scope.reason : fault('NETWORK_ERROR'));
      try {
        client.once('response', responded);
        client.once('error', failed);
        client.once('close', closed);
        client.end();
      } catch (error) { settle(false, error); }
    }));
    control.assertOpen();
    const outHeaders = cleanHopByHop(upstream.headers);
    if (outHeaders.location) {
      const next = new URL(outHeaders.location, target);
      outHeaders.location = proxyManifestLocation(entry, next.href, map, hlsVariables);
    }
    const input = request.method === 'GET' && upstream.statusCode === 200 &&
      !request.headers.range && !outHeaders['content-range']
      ? await control.race(proxyPlaylistInput(upstream, target, resources))
      : { stream: upstream, format: null };
    if (!input.format) {
      control.close();
      controlActive = false;
      response.writeHead(upstream.statusCode, outHeaders);
      await scope.race(pipeline(input.stream, response, { signal: scope.signal }));
    } else {
      delete outHeaders['content-length']; delete outHeaders.etag;
      delete outHeaders['content-md5']; delete outHeaders.digest;
      delete outHeaders['content-encoding'];
      const text = await control.race(readProxyPlaylist(input.stream));
      control.close();
      controlActive = false;
      const body = proxyPlaylistBody(text, target, input.format, entry, map, hlsVariables);
      response.writeHead(upstream.statusCode, outHeaders);
      await scope.race(pipeline(Readable.from(body, { objectMode: false }),
        response, { signal: scope.signal }));
    }
  } catch (error) { failure = scope.reason ?? (controlActive ? control.reason : null) ?? error; }
  clearHeaders?.();
  response.off('close', responseClosed);
  request.off('aborted', abort);
  control.close();
  scope.close();
  try { await resources.close(); }
  catch (error) {
    const cleanupFailure = safeError(error, 'SERVICE_ERROR');
    runtime.proxyCloseFailures.set(entry.session, cleanupFailure);
    runtime.proxyCloseFailure ??= cleanupFailure;
    failure = cleanupFailure;
  }
  client?.off('socket', socketAssigned);
  if (failure) throw failure;
}
