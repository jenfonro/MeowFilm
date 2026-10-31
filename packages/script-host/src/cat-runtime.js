import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { randomBytes, createHash } from 'node:crypto';
import { readFileSync, existsSync, mkdirSync, lstatSync, realpathSync } from 'node:fs';
import { resolve, join, isAbsolute, relative, sep } from 'node:path';
import { ScriptHost } from './host.js';
import { LXSession } from './lx-session.js';
import { CatSession } from './cat-session.js';
import { CatSettingsFile, normalizeCatConfig } from './cat-settings.js';
import { atomicWrite } from './private-volume.js';
import { scriptAccessHeader } from './script-services.js';
import { syncCatManagement, detectCatManagement } from './cat-management.js';
import { fault, identifier, requireValue, safeError } from './errors.js';
import { ResourceScope } from './scope.js';
import { addExtension, mergeExtension, ownedExtension, parseM3U8Metadata } from './cat-output.js';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const blockedProxyPath = path => /^\/(?:website|admin|__meow|config|full-config)(?:\/|$)/i.test(path);
function allowedProxyPath(path) {
  if (typeof path !== 'string' || !path.startsWith('/') || path.startsWith('//')) return false;
  let decoded = path;
  for (;;) {
    const next = decoded.replace(/%([0-9a-f]{2})/gi,
      (_, value) => String.fromCharCode(Number.parseInt(value, 16)));
    if (next === decoded) break;
    decoded = next;
  }
  if (!decoded.startsWith('/') || decoded.startsWith('//') ||
    /[\\\u0000-\u001f\u007f]/.test(decoded)) return false;
  let normalized;
  try { normalized = new URL(decoded, 'http://127.0.0.1').pathname; }
  catch { return false; }
  return normalized.startsWith('/') && !normalized.startsWith('//') &&
    !blockedProxyPath(normalized);
}
const hopByHop = new Set(['connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
  'te', 'trailer', 'transfer-encoding', 'upgrade']);
const inside = (root, path) => {
  const delta = relative(root, path);
  return delta === '' || delta !== '..' && !delta.startsWith('..' + sep) && !isAbsolute(delta);
};
export function cleanHopByHop(input) {
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
function stripPrivateCookieHeader(headers) {
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() !== 'cookie') continue;
    const cookies = (Array.isArray(headers[key]) ? headers[key].join(';') : String(headers[key] ?? ''))
      .split(';').map(value => value.trim()).filter(value => value && !/^__meow_/i.test(value));
    if (cookies.length) headers[key] = cookies.join('; ');
    else delete headers[key];
  }
}
function stripPrivateSetCookieHeader(headers) {
  for (const key of Object.keys(headers)) {
    if (key.toLowerCase() !== 'set-cookie') continue;
    const values = (Array.isArray(headers[key]) ? headers[key] : [headers[key]])
      .filter(value => !/^\s*__meow_/i.test(String(value ?? '')));
    if (!values.length) delete headers[key];
    else headers[key] = Array.isArray(headers[key]) ? values : values[0];
  }
}
function fixedOriginURL(origin, path, search = '', hash = '') {
  requireValue(typeof path === 'string' && path.startsWith('/') && !path.startsWith('//'),
    'PERMISSION_DENIED');
  const target = new URL(origin);
  target.pathname = path;
  target.search = search;
  target.hash = hash;
  return target;
}
function beginServerClose(server) {
  const state = {
    server, failure: null, settled: false, promise: null, finish: null, retry: null,
  };
  state.promise = new Promise(resolve => {
    const finish = error => {
      if (state.settled) return;
      if (error && error.code !== 'ERR_SERVER_NOT_RUNNING') state.failure ??= error;
      let listening = false;
      try { listening = Boolean(server.listening); } catch (failure) { state.failure ??= failure; }
      if (error && listening) return;
      state.settled = true;
      try { server.off?.('close', finish); } catch (failure) { state.failure ??= failure; }
      try { server.removeAllListeners?.(); } catch (failure) { state.failure ??= failure; }
      state.server = null;
      state.finish = null;
      state.retry = () => {};
      resolve();
    };
    state.finish = finish;
    try { server.once?.('close', finish); } catch (error) { state.failure ??= error; }
    state.retry = () => {
      if (state.settled) return;
      try { server.closeAllConnections?.(); } catch (error) { state.failure ??= error; }
      try {
        server.close(error => {
          if (error && error.code !== 'ERR_SERVER_NOT_RUNNING') state.failure ??= error;
          let listening = false;
          try { listening = Boolean(server.listening); }
          catch (failure) { state.failure ??= failure; }
          if (!listening) finish(error);
        });
      } catch (error) {
        if (error?.code === 'ERR_SERVER_NOT_RUNNING') finish(error);
        else {
          state.failure ??= error;
          let listening = true;
          try { listening = Boolean(server.listening); }
          catch (failure) { state.failure ??= failure; }
          if (!listening) finish(error);
        }
      }
    };
  });
  return state;
}
function guardClientRequestErrors(client) {
  const state = {
    client, active: true, failure: null, error: () => {}, closed: null,
  };
  state.closed = () => {
    if (!state.active) return;
    state.active = false;
    const target = state.client;
    state.client = null;
    try { target?.off?.('close', state.closed); } catch (error) { state.failure ??= error; }
    try { target?.off?.('error', state.error); } catch (error) { state.failure ??= error; }
    state.closed = null;
    state.error = null;
  };
  try { client.on('error', state.error); } catch (error) { state.failure ??= error; }
  try { client.once('close', state.closed); } catch (error) { state.failure ??= error; }
  try { if (client.closed) state.closed(); } catch (error) { state.failure ??= error; }
  return state;
}
async function waitServerClose(state, timeoutMs = 3000) {
  let timer;
  const complete = await Promise.race([
    state.promise.then(() => true),
    new Promise(resolve => {
      timer = setTimeout(() => resolve(false), timeoutMs);
    }),
  ]);
  clearTimeout(timer);
  if (complete && state.failure) throw state.failure;
  return complete;
}
function decodeSource(bytes) {
  try {
    const source = new TextDecoder('utf8', { fatal: true, ignoreBOM: true }).decode(bytes);
    requireValue(!source.startsWith('\uFEFF') && source.trim().length > 0 &&
      Buffer.from(source).equals(bytes));
    return source;
  } catch { throw fault('SCRIPT_PROTOCOL_ERROR'); }
}

/** Shared Cat owner. Platform supplies only the stable data directory; source,
 * settings, sandbox, Cat routing and management remain in this package. */
export class CatRuntime {
  constructor({ dataRoot, network, onDiagnostic, onNetwork } = {}) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot));
    this.dataRoot = resolve(dataRoot);
    this.network = network; this.onDiagnostic = onDiagnostic; this.onNetwork = onNetwork;
    this.sessions = new Map(); this.proxies = new Map();
    this.serverClosures = new Map();
    this.tail = Promise.resolve(); this.closed = false;
    this.access = randomBytes(32).toString('hex');
    this.applied = new Map();
    this.failures = new Map();
  }
  serverClosure(server) {
    let state = this.serverClosures.get(server);
    if (!state) {
      state = beginServerClose(server);
      this.serverClosures.set(server, state);
      const ownerRef = new WeakRef(this);
      state.promise.finally(() => {
        const owner = ownerRef.deref();
        if (owner?.serverClosures.get(server) === state) owner.serverClosures.delete(server);
      }).catch(() => {});
    }
    state.retry?.();
    return state;
  }
  async closeOwnedServer(server, timeoutMs = 3000) {
    if (!server) return true;
    const state = this.serverClosure(server);
    const complete = await waitServerClose(state, timeoutMs);
    if (!complete && state.failure) throw state.failure;
    return complete;
  }
  async drainServerClosures(timeoutMs = 3000) {
    const states = [...this.serverClosures.values()];
    for (const state of states) state.retry();
    const results = await Promise.allSettled(states.map(async state => {
      const complete = await waitServerClose(state, timeoutMs);
      if (!complete) throw fault('TIMEOUT');
    }));
    const rejected = results.find(result => result.status === 'rejected');
    if (rejected) throw rejected.reason;
  }
  async start() {
    requireValue(!this.closed, 'HOST_CLOSED');
    if (this.initialization) return this.initialization;
    this.initialization = this.serial(async () => {
      mkdirSync(this.dataRoot, { recursive: true });
      const rootState = lstatSync(this.dataRoot);
      requireValue(rootState.isDirectory() && !rootState.isSymbolicLink(), 'PERMISSION_DENIED');
      this.dataRoot = realpathSync(this.dataRoot);
      this.sourcesRoot = join(this.dataRoot, 'sources');
      mkdirSync(this.sourcesRoot, { recursive: true });
      const sources = lstatSync(this.sourcesRoot);
      requireValue(sources.isDirectory() && !sources.isSymbolicLink() &&
        inside(this.dataRoot, realpathSync(this.sourcesRoot)), 'PERMISSION_DENIED');
      this.sourcesIdentity = { dev: sources.dev, ino: sources.ino };
      this.settings = new CatSettingsFile(this.dataRoot);
      this.host = new ScriptHost({ adapters: {
        lx: input => new LXSession(input),
        cat: input => {
          const session = new CatSession({ ...input, dataRoot: this.dataRoot, settings: this.settings, onNetwork: this.onNetwork,
            mapUrl: (session, value, baseOrigin) => this.mapProxy(session, value, baseOrigin),
            onActivate: current => this.sessions.set(input.installationId, current),
            onEndpointsChanged: current => this.sweepProxies(current),
            onStop: current => {
              if (this.sessions.get(input.installationId) === current)
                this.sessions.delete(input.installationId);
              this.sweepProxies(current);
            } });
          return session;
        },
      }, network: this.network, onDiagnostic: this.onDiagnostic,
      lifecycleManagedProtocols: ['cat'], limits: { requestTimeoutMs: 30000 } });
      return await this.apply(this.settings.read());
    });
    return this.initialization;
  }
  serial(body) {
    requireValue(!this.closed, 'HOST_CLOSED');
    const task = this.tail.catch(() => {}).then(() => {
      requireValue(!this.closed, 'HOST_CLOSED'); return body();
    });
    this.tail = task.catch(() => {});
    return task;
  }
  sourcePath(sourceHash) {
    try {
      const state = lstatSync(this.sourcesRoot);
      requireValue(state.isDirectory() && !state.isSymbolicLink() &&
        state.dev === this.sourcesIdentity.dev && state.ino === this.sourcesIdentity.ino &&
        inside(this.dataRoot, realpathSync(this.sourcesRoot)), 'CORRUPT_STORAGE');
      return join(this.sourcesRoot, sourceHash + '.js');
    } catch (error) { throw safeError(error, 'CORRUPT_STORAGE'); }
  }
  readSource(sourceHash) {
    try {
      const file = this.sourcePath(sourceHash);
      requireValue(existsSync(file), 'CORRUPT_STORAGE');
      const state = lstatSync(file);
      requireValue(state.isFile() && !state.isSymbolicLink() && state.nlink === 1 &&
        inside(this.sourcesRoot, realpathSync(file)), 'CORRUPT_STORAGE');
      const bytes = readFileSync(file);
      requireValue(hash(bytes) === sourceHash, 'CORRUPT_STORAGE');
      return bytes;
    } catch (error) { throw safeError(error, 'CORRUPT_STORAGE'); }
  }
  async apply(next) {
    // Validate all referenced inputs before touching any live source/config.
    const sources = new Map();
    for (const spec of next.scripts) {
      sources.set(spec.installationId, decodeSource(this.readSource(spec.sourceHash)));
    }
    const candidate = !this.server || next.port !== this.settings.value.port
      ? await this.listen(next.port) : null;
    const gateway = candidate ?? this.server;
    let committed = false;
    try {
      const current = new Map(this.host.list().map(state => [state.installationId, state]));
      let removalFailure;
      for (const old of current.values()) {
        if (!next.scripts.some(spec => spec.installationId === old.installationId)) {
          try { await this.host.uninstall(old.installationId); }
          catch (error) { removalFailure ??= safeError(error, 'SERVICE_ERROR'); }
          current.delete(old.installationId);
          this.applied.delete(old.installationId); this.failures.delete(old.installationId);
          this.sessions.delete(old.installationId);
        }
      }
      for (const spec of next.scripts) {
        const old = this.applied.get(spec.installationId), state = current.get(spec.installationId);
        if (old && state && same(old, spec) && !this.failures.has(spec.installationId) &&
          state.state !== 'failed') continue;
        try {
          if (state) {
            // Stop only this affected instance. Two generations must never write
            // the same persistent volume concurrently during an update.
            await this.host.disable(spec.installationId);
            await this.host.update(spec.installationId, { source: sources.get(spec.installationId),
              config: spec.config, version: spec.version });
          } else await this.host.install({ ...spec, source: sources.get(spec.installationId),
            protocol: 'cat', enabled: false });
          if (spec.enabled) await this.host.enable(spec.installationId);
          this.failures.delete(spec.installationId);
        } catch (error) { this.failures.set(spec.installationId, safeError(error).code); }
        this.applied.set(spec.installationId, spec);
      }
      this.settings.publish(next);
      const previous = this.server;
      this.server = gateway;
      committed = true;
      if (previous && previous !== gateway) {
        try {
          if (!await this.closeOwnedServer(previous)) {
            try { this.onDiagnostic?.({ kind: 'ScriptHostError', reason: 'host',
              operation: 'cat.aggregate.close', code: 'TIMEOUT' }); } catch {}
          }
        } catch {
          try { this.onDiagnostic?.({ kind: 'ScriptHostError', reason: 'host',
            operation: 'cat.aggregate.close', code: 'SERVICE_ERROR' }); } catch {}
        }
      }
      this.sweepProxies();
      const result = { applied: !this.failures.size,
        failures: [...this.failures].map(([installationId, code]) => ({ installationId, code })) };
      if (removalFailure) throw removalFailure;
      return result;
    } finally {
      if (candidate && !committed) {
        try {
          if (!await this.closeOwnedServer(candidate)) {
            try { this.onDiagnostic?.({ kind: 'ScriptHostError', reason: 'host',
              operation: 'cat.aggregate.close', code: 'TIMEOUT' }); } catch {}
          }
        } catch {
          try { this.onDiagnostic?.({ kind: 'ScriptHostError', reason: 'host',
            operation: 'cat.aggregate.close', code: 'SERVICE_ERROR' }); } catch {}
        }
      }
    }
  }
  async refresh() {
    await this.start();
    return this.serial(() => this.apply(this.settings.read()));
  }
  async install({ installationId, source, config = {}, version = '0.0.0', enabled = false }) {
    identifier(installationId);
    requireValue(typeof source === 'string' && typeof enabled === 'boolean' &&
      typeof version === 'string' && version.length > 0 && version.length <= 128);
    config = normalizeCatConfig(config);
    await this.start();
    return this.serial(async () => {
      const bytes = Buffer.from(source), sourceHash = hash(bytes);
      decodeSource(bytes);
      const file = this.sourcePath(sourceHash);
      if (!existsSync(file)) atomicWrite(file, bytes);
      requireValue(this.readSource(sourceHash).equals(bytes), 'CORRUPT_STORAGE');
      const next = this.settings.save(value => ({ ...value, scripts: [
        ...value.scripts.filter(script => script.installationId !== installationId),
        { installationId, sourceHash, config, version, enabled },
      ] }));
      await this.apply(next);
      if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
      return this.instance(installationId);
    });
  }
  async setEnabled(installationId, enabled) {
    identifier(installationId); requireValue(typeof enabled === 'boolean');
    await this.start();
    return this.serial(async () => {
      requireValue(this.settings.read().scripts.some(script => script.installationId === installationId), 'NOT_FOUND');
      const next = this.settings.save(value => ({ ...value, scripts: value.scripts.map(script =>
        script.installationId === installationId ? { ...script, enabled } : script) }));
      await this.apply(next);
      if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
      return this.instance(installationId);
    });
  }
  async uninstall(installationId) {
    identifier(installationId); await this.start();
    return this.serial(async () => {
      const next = this.settings.save(value => ({ ...value,
        scripts: value.scripts.filter(script => script.installationId !== installationId) }));
      return this.apply(next);
    });
  }
  instance(installationId) {
    const state = this.host.get(installationId), session = this.sessions.get(installationId);
    return { installationId, generation: state.generation, state: this.failures.has(installationId) ? 'failed' : state.state,
      version: state.version, sourceHash: state.contentHash,
      error: this.failures.get(installationId) ?? state.lastFailure ?? null,
      sites: Object.entries(state.capabilities).map(([key, value]) => ({ key, ...value })),
      ...(state.state === 'ready' ? { managementUrl: session.endpoint().managementUrl,
        endpoints: session.services.endpoints() } : {}) };
  }
  getSession(installationId, generation) {
    const state = this.host.get(installationId), session = this.sessions.get(installationId);
    requireValue(state.state === 'ready' && session?.live, 'DISABLED');
    requireValue(generation === undefined || state.generation === generation, 'STALE_SESSION');
    return session;
  }
  async invoke(target, options) {
    await this.start();
    let result = await this.host.invoke(target, options);
    if (!result || typeof result !== 'object' || typeof result.url !== 'string' ||
      !/\.m3u8(?:\?|$)/i.test(result.url) || result._meow && !result[ownedExtension]) return result;
    // Optional metadata enrichment is never a playback/provider parser.
    try {
      const session = this.getSession(target.installationId, target.generation);
      const source = this.proxyTarget(session, result.url) ?? result.url;
      const variants = parseM3U8Metadata(
        await session.readText(source, result.header ?? result.headers, 3000), source)
        .map(variant => ({ ...variant, url: this.mapProxy(session, variant.url) }));
      if (variants.length) {
        const extension = {
          media: [...(Array.isArray(result._meow?.media) ? result._meow.media : []), { variants }],
        };
        result = result[ownedExtension] ? mergeExtension(result, extension) : addExtension(result, extension);
      }
      return result;
    } catch { return result; }
  }
  proxyTarget(session, value) {
    let url;
    try { url = new URL(value); } catch { return null; }
    if (url.hostname !== '127.0.0.1' || Number(url.port) !== this.server?.address()?.port) return null;
    const parts = url.pathname.split('/').filter(Boolean);
    if (parts[0] !== 'proxy' || parts.length < 4) return null;
    const entry = this.proxies.get(parts[3]);
    if (!entry || entry.session !== session || parts[1] !== session.installationId ||
      Number(parts[2]) !== session.generation) return null;
    const prefix = `/proxy/${parts[1]}/${parts[2]}/${parts[3]}`;
    try {
      return fixedOriginURL(entry.origin, url.pathname.slice(prefix.length) || '/',
        url.search, url.hash).href;
    } catch { return null; }
  }
  async management(installationId, generation) {
    await this.start(); return detectCatManagement(this.getSession(installationId, generation));
  }
  async sync(installationId, generation, input) {
    await this.start(); return syncCatManagement(this.getSession(installationId, generation), input);
  }
  async status() {
    await this.start();
    return { settingsPath: this.settings.path, aggregate: {
      origin: `http://127.0.0.1:${this.server.address().port}`, access: this.access },
      instances: this.host.list().map(state => this.instance(state.installationId)) };
  }
  async listen(port) {
    const ownerRef = new WeakRef(this);
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
      void owner.handle(request, response).catch(error => {
        try {
          if (response.destroyed || response.writableEnded) return;
          if (!response.headersSent) {
            const status = error?.code === 'PERMISSION_DENIED' ? 403
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
    try { await new Promise((resolve, reject) => {
      let settled = false;
      const cleanup = () => {
        try { server.off('error', failed); } catch {}
      };
      const finish = (callback, value) => {
        if (settled) return;
        settled = true;
        cleanup();
        callback(value);
      };
      const failed = error => finish(reject, error);
      const ready = () => {
        if (settled) return;
        try {
          server.off('error', failed);
          server.on('error', () => {
            const owner = ownerRef.deref();
            try { owner?.onDiagnostic?.({ kind: 'ScriptHostError', reason: 'host',
              operation: 'cat.aggregate', code: 'SERVICE_ERROR' }); } catch {}
          });
          settled = true;
          resolve();
        } catch (error) { failed(error); }
      };
      try {
        server.once('error', failed);
        server.listen(port, '127.0.0.1', ready);
      } catch (error) { failed(error); }
    }); } catch (error) {
      try { await this.closeOwnedServer(server); } catch { /* Preserve the listen failure. */ }
      throw error;
    }
    return server;
  }
  mapProxy(session, value, baseOrigin) {
    const webPrefix = 'js2p://_WEB_';
    if (value.startsWith(webPrefix)) {
      value = new URL(value.slice(webPrefix.length), baseOrigin ?? session.endpoint().origin).href;
    } else if (value.startsWith('/') && !value.startsWith('//')) {
      value = new URL(value, baseOrigin ?? session.endpoint().origin).href;
    }
    const local = session.services.mapLocal(value);
    if (!local) return value;
    requireValue(allowedProxyPath(local.pathname), 'PERMISSION_DENIED');
    const existing = [...this.proxies].find(([, entry]) => entry.session === session && entry.origin === local.origin);
    let id;
    if (existing) id = existing[0];
    else { id = randomBytes(24).toString('hex'); this.proxies.set(id, { session, origin: local.origin }); }
    return `http://127.0.0.1:${this.server.address().port}/proxy/${session.installationId}/${session.generation}/${id}${local.pathname}${local.search}${local.hash}`;
  }
  sweepProxies(session) {
    const origins = session?.live
      ? new Set(session.services.endpoints().map(endpoint => endpoint.origin)) : null;
    for (const [id, entry] of this.proxies) {
      if (!entry.session.live ||
        session && entry.session === session && !origins?.has(entry.origin)) this.proxies.delete(id);
    }
  }
  async handle(request, response) {
    const origin = `http://127.0.0.1:${this.server.address().port}`;
    requireValue(request.headers.host === this.server.address().address + ':' + this.server.address().port, 'PERMISSION_DENIED');
    const url = new URL(request.url, origin), parts = url.pathname.split('/').filter(Boolean);
    if (parts[0] === 'proxy') return this.forwardProxy(request, response, url, parts);
    requireValue(request.headers['x-meow-runtime-access'] === this.access, 'PERMISSION_DENIED');
    requireValue(parts[0] === 'cat' && parts.length === 4, 'INVALID_ARGUMENT');
    let decoded;
    try { decoded = parts.map(decodeURIComponent); }
    catch { throw fault('INVALID_ARGUMENT'); }
    const [_, installationId, siteId, operation] = decoded;
    const scope = new ResourceScope(undefined, 30000);
    const abort = () => scope.close(fault('CANCELLED'));
    const responseClosed = () => { if (!response.writableFinished) abort(); };
    let requestListening = false, responseListening = false;
    const chunks = [];
    let failure;
    try {
      request.once('aborted', abort);
      requestListening = true;
      response.once('close', responseClosed);
      responseListening = true;
      let length = 0;
      for await (const chunk of request) {
        length += chunk.length;
        requireValue(length <= this.host.limits.maxBytes * 4, 'RESOURCE_LIMIT');
        chunks.push(chunk);
      }
      let payload;
      let combined;
      try {
        if (chunks.length) {
          combined = Buffer.concat(chunks);
          payload = JSON.parse(combined);
        } else payload = Object.fromEntries(url.searchParams);
      }
      catch { throw fault('INVALID_JSON'); }
      finally {
        combined?.fill(0);
        for (const chunk of chunks) chunk.fill(0);
      }
      const result = await this.invoke({ installationId, siteId, operation, payload }, { signal: scope.signal });
      response.writeHead(200, { 'content-type': 'application/json; charset=utf-8' });
      response.end(typeof result === 'string' ? result : JSON.stringify(result));
    } catch (error) { failure = error; }
    for (const chunk of chunks) {
      try { chunk.fill(0); } catch (error) { failure ??= error; }
    }
    chunks.length = 0;
    try { if (requestListening) request.off('aborted', abort); } catch (error) { failure ??= error; }
    try { if (responseListening) response.off('close', responseClosed); } catch (error) { failure ??= error; }
    try { scope.close(); } catch (error) { failure ??= error; }
    if (failure) throw failure;
  }
  async forwardProxy(request, response, url, parts) {
    const [_, installationId, generation, id] = parts;
    const entry = this.proxies.get(id);
    requireValue(entry && entry.session.installationId === installationId &&
      entry.session.generation === Number(generation) && entry.session.live, 'STALE_SESSION');
    requireValue(['GET', 'HEAD'].includes(request.method), 'PERMISSION_DENIED');
    const prefix = `/proxy/${installationId}/${generation}/${id}`;
    const path = url.pathname.slice(prefix.length) || '/';
    requireValue(allowedProxyPath(path), 'PERMISSION_DENIED');
    const target = fixedOriginURL(entry.origin, path, url.search);
    const scope = new ResourceScope(entry.session.root.signal);
    let client, clientGuard, upstream;
    const abort = () => scope.close(fault('CANCELLED'));
    const responseClosed = () => { if (!response.writableFinished) abort(); };
    let requestListening = false, responseListening = false;
    let failure;
    try {
      const headers = cleanHopByHop(request.headers);
      for (const name of ['host', scriptAccessHeader, 'x-meow-request-scope',
        'x-meow-runtime-access']) deleteHeader(headers, name);
      stripPrivateCookieHeader(headers);
      headers.host = target.host;
      headers[scriptAccessHeader] = entry.session.services.access;
      headers['accept-encoding'] = 'identity';
      response.once('close', responseClosed);
      responseListening = true;
      request.once('aborted', abort);
      requestListening = true;
      if (request.aborted || response.destroyed) abort();
      scope.assertOpen();
      client = (target.protocol === 'https:' ? httpsRequest : httpRequest)(target, {
        method: request.method, headers,
        ...(target.protocol === 'https:' ? { rejectUnauthorized: false } : {}),
      });
      clientGuard = guardClientRequestErrors(client);
      if (clientGuard.failure) throw clientGuard.failure;
      scope.add(() => {
        upstream?.destroy();
        client?.destroy();
      });
      scope.assertOpen();
      upstream = await scope.race(new Promise((resolve, reject) => {
        let settled = false;
        const cleanup = () => {
          let failure;
          try { client.off('response', responded); } catch (error) { failure ??= error; }
          try { client.off('error', failed); } catch (error) { failure ??= error; }
          try { client.off('close', closed); } catch (error) { failure ??= error; }
          return failure;
        };
        const settle = (success, value) => {
          if (settled) {
            if (success) {
              try { value?.destroy?.(); } catch { /* The response has no remaining owner. */ }
            }
            return;
          }
          settled = true;
          const cleanupFailure = cleanup();
          if (success && (cleanupFailure || scope.signal.aborted)) {
            try { value?.destroy?.(); } catch (error) {
              if (!cleanupFailure) reject(error);
            }
            reject(cleanupFailure ?? scope.reason ?? fault('CANCELLED'));
          } else if (success) resolve(value);
          else {
            if (!value) value = cleanupFailure;
            reject(value);
          }
        };
        const responded = value => {
          upstream = value;
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
      const outHeaders = cleanHopByHop(upstream.headers);
      stripPrivateSetCookieHeader(outHeaders);
      if (outHeaders.location) {
        const next = new URL(outHeaders.location, target);
        if (entry.session.services.mapLocal(next.href)) outHeaders.location = this.mapProxy(entry.session, next.href);
      }
      const playlist = request.method === 'GET' && upstream.statusCode === 200 &&
        !request.headers.range && !outHeaders['content-range'] &&
        (/mpegurl|dash\+xml/i.test(String(outHeaders['content-type'])) ||
        /\.(?:m3u8|mpd)(?:$|\?)/i.test(target.pathname + target.search)) && !outHeaders['content-encoding'];
      if (!playlist) {
        response.writeHead(upstream.statusCode, outHeaders);
        await pipeline(upstream, response, { signal: scope.signal });
      } else {
        delete outHeaders['content-length']; delete outHeaders.etag;
        delete outHeaders['content-md5']; delete outHeaders.digest;
        const decoder = new TextDecoder(), encoder = new TextEncoder();
        let pending = '';
        const rewrite = text => {
          const trimmed = text.trim();
          const map = value => {
            let candidate;
            try { candidate = new URL(value, target); }
            catch { return value; }
            return entry.session.services.mapLocal(candidate.href)
              ? this.mapProxy(entry.session, candidate.href) : value;
          };
          if (trimmed && !trimmed.startsWith('#') && !trimmed.startsWith('<')) return map(trimmed);
          return text.replace(/(URI|media|initialization|sourceURL)="([^"]+)"/g, (_, key, value) => `${key}="${map(value)}"`)
            .replace(/(<BaseURL>)([^<]+)(<\/BaseURL>)/g, (_, a, value, b) => a + map(value) + b);
        };
        const transform = new Transform({
          transform(chunk, _, done) {
            try {
              pending += decoder.decode(chunk, { stream: true });
              const lines = pending.split('\n'); pending = lines.pop();
              for (const line of lines) this.push(encoder.encode(rewrite(line) + '\n'));
              done();
            } catch (error) { done(error); }
          },
          flush(done) {
            try {
              pending += decoder.decode();
              if (pending) this.push(encoder.encode(rewrite(pending)));
              done();
            } catch (error) { done(error); }
          },
        });
        response.writeHead(upstream.statusCode, outHeaders);
        await pipeline(upstream, transform, response, { signal: scope.signal });
      }
    } catch (error) { failure = error; }
    try { if (responseListening) response.off('close', responseClosed); } catch (error) { failure ??= error; }
    try { if (requestListening) request.off('aborted', abort); } catch (error) { failure ??= error; }
    try { scope.close(); } catch (error) { failure ??= error; }
    try { if (upstream && !upstream.destroyed) upstream.destroy(); } catch (error) { failure ??= error; }
    try { if (client && !client.destroyed) client.destroy(); } catch (error) { failure ??= error; }
    if (clientGuard) {
      try { if (client?.closed) clientGuard.closed?.(); } catch (error) { failure ??= error; }
      failure ??= clientGuard.failure;
      clientGuard = null;
    }
    if (failure) throw failure;
  }
  async close() {
    if (this.closing) return this.closing;
    this.closed = true;
    this.closing = (async () => {
      let failure;
      await this.tail.catch(() => {});
      try {
        const result = await this.host?.close();
        if (result?.cleanupComplete === false) {
          const timedOut = result.pendingAdapterTasks > 0 ||
            result.cleanupIssues?.some(issue => issue.code === 'TIMEOUT');
          failure ??= fault(timedOut ? 'TIMEOUT' : 'SERVICE_ERROR');
        }
      } catch (error) { failure ??= safeError(error, 'SERVICE_ERROR'); }
      if (this.server) this.serverClosure(this.server);
      this.server = null;
      try { await this.drainServerClosures(); }
      catch (error) { failure ??= safeError(error, 'SERVICE_ERROR'); }
      this.serverClosures = new Map();
      this.proxies.clear(); this.sessions.clear();
      this.applied.clear(); this.failures.clear();
      this.host = null; this.settings = null;
      this.network = null; this.onDiagnostic = null; this.onNetwork = null;
      this.initialization = null;
      if (failure) throw failure;
    })();
    return this.closing;
  }
}

export const createCatRuntime = options => new CatRuntime(options);
