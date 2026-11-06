import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { randomBytes, createHash } from 'node:crypto';
import { readFileSync, existsSync, mkdirSync, lstatSync, realpathSync, rmSync } from 'node:fs';
import { resolve, join, isAbsolute, relative, sep } from 'node:path';
import { parse } from 'acorn';
import { ScriptHost } from './host.js';
import { WorkerSession, workerResult } from './worker-session.js';
import { CatSettingsFile, normalizeCatConfig, catName, catSourceURL } from './cat-settings.js';
import { downloadCatSource } from './cat-source-download.js';
import { atomicWrite, removePrivateVolume } from './private-volume.js';
import { scriptAccessHeader } from './script-services.js';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';
import { ResourceScope } from './scope.js';
import { ownedExtension } from './cat-output.js';
import { allowedProxyPath, fixedOriginURL, mapLocalSnapshot } from './cat-proxy.js';
import { closeResource } from './resource-close.js';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const execution = spec => spec && [spec.sourceHash, spec.config, spec.version, spec.enabled];
const sameExecution = (a, b) => same(execution(a), execution(b));
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
function decodeSource(bytes) {
  try {
    const source = bytes.toString('utf8');
    requireValue(!source.startsWith('\uFEFF') && source.trim().length > 0 &&
      Buffer.from(source).equals(bytes));
    return source;
  } catch { throw fault('SCRIPT_PROTOCOL_ERROR'); }
}

/** Shared Cat owner. Platform supplies only the stable data directory; source,
 * settings, sandbox, Cat routing and management remain in this package. */
export class CatRuntime {
  constructor({ dataRoot, network, onDiagnostic, onScriptError, onNetwork, limits, clock = Date.now } = {}) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot));
    requireValue(typeof clock === 'function');
    this.dataRoot = resolve(dataRoot);
    this.clock = clock;
    this.network = network; this.onDiagnostic = onDiagnostic; this.onScriptError = onScriptError;
    this.onNetwork = onNetwork; this.limits = limits;
    this.sessions = new Map(); this.snapshots = new Map(); this.proxies = new Map();
    this.aggregatePort = 0;
    this.serverClosures = new WeakMap();
    this.pendingServerClosures = new Set();
    this.serverCloseFailure = null;
    this.tail = Promise.resolve(); this.closed = false;
    this.startupQueue = []; this.startupScheduled = false; this.settled = new Set();
    this.initialStartSignal = new AbortController();
    this.initialization = null;
    this.access = randomBytes(32).toString('hex');
    this.applied = new Map();
    this.failures = new Map();
    this.workers = new Set();
    this.managementTasks = new Set();
    this.replacements = new Set();
    this.managementSignals = new Map();
  }
  serverClosure(server, timeoutMs = 3000) {
    let state = this.serverClosures.get(server);
    if (!state) {
      state = closeResource(server, {
        stop: () => {
          let failure;
          try { server.closeAllConnections?.(); } catch (error) { failure = error; }
          try { server.close(() => {}); } catch (error) {
            if (error?.code !== 'ERR_SERVER_NOT_RUNNING') failure ??= error;
          }
          if (failure) throw failure;
        },
        release: () => server.removeAllListeners?.(),
        timeoutMs,
        closed: value => !value.listening,
      });
      this.serverClosures.set(server, state);
      this.pendingServerClosures.add(state);
      const ownerRef = new WeakRef(this);
      state.then(result => {
        const owner = ownerRef.deref();
        if (!owner) return;
        owner.pendingServerClosures.delete(state);
        if (result.error) owner.serverCloseFailure ??= safeError(result.error);
      }).catch(() => {});
    }
    return state;
  }
  async closeOwnedServer(server, timeoutMs = 3000) {
    if (!server) return true;
    const result = await this.serverClosure(server, timeoutMs);
    if (result.error) throw result.error;
    return result.complete;
  }
  async drainServerClosures() {
    const results = await Promise.allSettled([...this.pendingServerClosures]);
    const rejected = results.find(result => result.status === 'rejected');
    if (rejected) throw rejected.reason;
    const timeout = results.some(result => result.status === 'fulfilled' &&
      (!result.value.complete || result.value.error?.code === 'TIMEOUT'));
    if (timeout) throw fault('TIMEOUT');
    if (this.serverCloseFailure) throw this.serverCloseFailure;
  }
  async start() {
    requireValue(!this.closed, 'HOST_CLOSED');
    if (!this.initialization) {
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
          cat: input => this.workerSession(input),
        }, network: this.network, onDiagnostic: this.onDiagnostic,
        onScriptError: this.onScriptError, limits: this.limits });
        this.aggregatePort = 0;
        await this.apply(this.settings.read(), { background: true });
      });
    }
    await this.initialization;
    return this.statusSnapshot();
  }
  serial(body) {
    requireValue(!this.closed, 'HOST_CLOSED');
    const task = this.tail.catch(() => {}).then(() => {
      requireValue(!this.closed, 'HOST_CLOSED'); return body();
    });
    this.tail = task.catch(() => {});
    return task;
  }
  workerSession(input) {
    const worker = new WorkerSession({
      ...input,
      protocol: 'cat',
      startSignal: this.managementSignals.get(input.installationId) ?? this.initialStartSignal.signal,
      startParams: {
        dataRoot: this.dataRoot,
        settings: { value: clone(this.startingSettings ?? this.settings.value) },
        aggregate: { port: this.startingAggregatePort ?? this.aggregatePort },
      },
      processWorkerResult: (method, value, session) => {
        if (method !== 'invoke' && method !== 'enhanceM3U8') return undefined;
        const result = clone(value?.result);
        if (Array.isArray(value?.newProxies))
          this.registerProxyMappings(session, value.newProxies);
        // The worker's structured boundary cannot carry the ownership Symbol.
        // Restore it only as a non-enumerable internal marker on the same result.
        if (value?.owned && result && typeof result === 'object')
          Object.defineProperty(result, ownedExtension, { value: true });
        return workerResult(result);
      },
      onStart: (session, cat) => {
        if (!session.live || !cat?.endpoint || !Array.isArray(cat.endpoints)) return;
        // Keep the lifecycle owner while initialization is in flight, but do
        // not publish endpoints or proxy routes until activation completes.
        this.sessions.set(session.installationId, session);
      },
      onActivated: (session, cat) => {
        if (!session.live || !cat ||
          this.sessions.get(session.installationId) !== session) return;
        this.sessions.set(session.installationId, session);
        this.registerProxyMappings(session, cat.endpoints);
        this.snapshots.set(session.installationId, this.catSnapshot(cat));
        this.sweepProxies();
      },
      onEndpointsChanged: (session, cat) => {
        if (!session.live || !session.activated || !cat ||
          this.sessions.get(session.installationId) !== session) return;
        this.registerProxyMappings(session, cat.endpoints);
        this.snapshots.set(session.installationId, this.catSnapshot(cat));
        this.sweepProxies(session);
      },
      onNetwork: value => {
        try { this.onNetwork?.(value); } catch { /* Observation cannot alter networking. */ }
      },
      onStopped: session => {
        if (this.sessions.get(session.installationId) !== session) return;
        this.sessions.delete(session.installationId);
        this.snapshots.delete(session.installationId);
        this.sweepProxies();
      },
    });
    this.workers.add(worker);
    return worker;
  }
  registerProxyMappings(session, entries) {
    for (const entry of entries) {
      const id = entry?.id ?? entry?.proxyId;
      if (typeof id !== 'string' || typeof entry.origin !== 'string') continue;
      const current = this.proxies.get(id);
      if (current && current.session !== session) continue;
      this.proxies.set(id, { session, origin: entry.origin });
    }
  }
  catSnapshot(cat) {
    return {
      endpoint: clone(cat.endpoint),
      access: cat.access,
      endpoints: clone(cat.endpoints),
      aggregatePort: this.startingAggregatePort ?? this.aggregatePort,
    };
  }
  mapSnapshotProxy(session, snapshot, value) {
    const mapped = mapLocalSnapshot(value, snapshot.endpoints);
    if (!mapped) return value;
    requireValue(allowedProxyPath(mapped.pathname), 'PERMISSION_DENIED');
    const endpoint = snapshot.endpoints.find(item => item.origin === mapped.origin);
    requireValue(endpoint && typeof endpoint.proxyId === 'string', 'UNMAPPED_LOCAL_URL');
    const proxyId = endpoint.proxyId;
    const registered = this.proxies.get(proxyId);
    requireValue(registered?.session === session && registered.origin === mapped.origin,
      'UNMAPPED_LOCAL_URL');
    return `http://127.0.0.1:${snapshot.aggregatePort}/proxy/${session.installationId}/${session.generation}/${proxyId}${mapped.pathname}${mapped.search}${mapped.hash}`;
  }
  publishSettingsSnapshot(next) {
    const settings = clone(next);
    const aggregate = { port: this.aggregatePort };
    const sessions = new Set([...(this.host?.initializingSessions?.() ?? []),
      ...this.sessions.values()]);
    for (const session of sessions) {
      const snapshot = this.snapshots.get(session.installationId);
      if (snapshot && this.sessions.get(session.installationId) === session)
        snapshot.aggregatePort = aggregate.port;
      try { session.publishSettings({ settings, aggregate }); }
      catch { /* Settings observation cannot block publication. */ }
    }
  }
  limit(name) {
    return this.limits?.[name] ?? this.host.limits[name];
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
      requireValue(state.size <= this.limit('maxSourceBytes'), 'RESOURCE_LIMIT');
      const bytes = readFileSync(file);
      requireValue(bytes.length <= this.limit('maxSourceBytes'), 'RESOURCE_LIMIT');
      return bytes;
    } catch (error) { throw safeError(error, 'CORRUPT_STORAGE'); }
  }
  deleteSource(sourceHash) {
    try {
      requireValue(/^[a-f0-9]{64}$/.test(sourceHash), 'CORRUPT_STORAGE');
      const file = this.sourcePath(sourceHash);
      let state;
      try { state = lstatSync(file); }
      catch (error) { if (error.code === 'ENOENT') return; throw error; }
      requireValue(state.isFile() && !state.isSymbolicLink() && state.nlink === 1 &&
        inside(this.sourcesRoot, realpathSync(file)), 'CORRUPT_STORAGE');
      rmSync(file, { maxRetries: 3, retryDelay: 100 });
    } catch (error) { throw safeError(error, 'STORAGE_ERROR'); }
  }
  async apply(next, { background = false, only = null } = {}) {
    const candidate = !this.server || next.port !== this.settings.value.port
      ? await this.listen(next.port) : null;
    const gateway = candidate ?? this.server;
    const aggregatePort = gateway.address().port;
    this.startingAggregatePort = aggregatePort;
    this.startingSettings = next;
    let committed = false;
    try {
      requireValue(!this.closed, 'HOST_CLOSED');
      const current = new Map(this.host.list().map(state => [state.installationId, state]));
      const removed = new Map();
      const removing = installationId => (!only || only.has(installationId)) &&
        !next.scripts.some(spec => spec.installationId === installationId);
      for (const old of current.values()) {
        if (!removing(old.installationId)) continue;
        const sourceHash = this.applied.get(old.installationId)?.sourceHash;
        removed.set(old.installationId, {
          state: old, sourceHashes: new Set(sourceHash ? [sourceHash] : []),
        });
      }
      const oldSettings = this.settings.value.scripts;
      for (const spec of oldSettings) {
        if (!removing(spec.installationId)) continue;
        const entry = removed.get(spec.installationId) ??
          { state: null, sourceHashes: new Set() };
        entry.sourceHashes.add(spec.sourceHash);
        removed.set(spec.installationId, entry);
      }
      let removalFailure;
      for (const [installationId, entry] of removed) {
        try { await this.stopInstance(installationId); }
        catch (error) { removalFailure ??= safeError(error, 'SERVICE_ERROR'); }
        try { if (entry.state) await this.host.uninstall(installationId); }
        catch (error) { removalFailure ??= safeError(error, 'SERVICE_ERROR'); }
        const stopped = ![...this.workers].some(worker =>
          worker.installationId === installationId && worker.worker);
        current.delete(installationId);
        this.applied.delete(installationId); this.failures.delete(installationId);
        this.sessions.delete(installationId);
        this.snapshots.delete(installationId);
        if (!stopped) continue;
        try { removePrivateVolume(this.dataRoot, installationId); }
        catch (error) { this.failures.set(installationId, safeError(error, 'STORAGE_ERROR').code); }
        for (const sourceHash of entry.sourceHashes) {
          if (next.scripts.some(spec => spec.sourceHash === sourceHash)) continue;
          try { this.deleteSource(sourceHash); }
          catch (error) {
            this.failures.set(installationId, safeError(error, 'STORAGE_ERROR').code);
          }
        }
      }
      const changes = [];
      for (const spec of next.scripts) {
        if (only && !only.has(spec.installationId)) continue;
        const old = this.applied.get(spec.installationId), state = current.get(spec.installationId);
        if (old && (state ? state.enabled === spec.enabled : !spec.enabled) && sameExecution(old, spec) &&
          !this.failures.has(spec.installationId) && state?.state !== 'failed') continue;
        changes.push({ spec, existing: !!state });
      }
      if (background) {
        for (const change of changes) {
          if (change.spec.enabled) this.settled.delete(change.spec.installationId);
          else {
            await this.applyChange(change);
            requireValue(!this.closed, 'HOST_CLOSED');
          }
        }
      } else {
        for (const change of changes) {
          await this.applyChange(change);
          requireValue(!this.closed, 'HOST_CLOSED');
        }
      }
      for (const installationId of [...this.startupQueue]) {
        if (!next.scripts.some(spec => spec.installationId === installationId))
          this.startupQueue.splice(this.startupQueue.indexOf(installationId), 1);
      }
      for (const spec of next.scripts) {
        if (!spec.enabled && this.startupQueue.includes(spec.installationId))
          this.startupQueue.splice(this.startupQueue.indexOf(spec.installationId), 1);
      }
      requireValue(!this.closed, 'HOST_CLOSED');
      this.settings.publish(next);
      const previous = this.server;
      this.server = gateway;
      this.aggregatePort = aggregatePort;
      committed = true;
      this.publishSettingsSnapshot(next);
      for (const spec of oldSettings) {
        if (only && !only.has(spec.installationId)) continue;
        const replacement = next.scripts.find(script =>
          script.installationId === spec.installationId);
        if (!replacement || replacement.sourceHash === spec.sourceHash ||
          this.failures.has(spec.installationId) ||
          next.scripts.some(script => script.sourceHash === spec.sourceHash)) continue;
        try { this.deleteSource(spec.sourceHash); }
        catch (error) {
          this.failures.set(spec.installationId, safeError(error, 'STORAGE_ERROR').code);
        }
      }
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
      if (background) {
        for (const { spec } of changes) {
          if (!this.closed && spec.enabled && !this.startupQueue.includes(spec.installationId))
            this.startupQueue.push(spec.installationId);
        }
        this.scheduleStartup();
      } else this.sweepProxies();
      const result = this.applyResult();
      if (removalFailure) throw removalFailure;
      return result;
    } finally {
      this.startingAggregatePort = null;
      this.startingSettings = null;
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
  applyResult() {
    return { applied: !this.failures.size,
      failures: [...this.failures].map(([installationId, code]) => ({ installationId, code })) };
  }
  async applyChange({ spec, existing }) {
    this.settled.delete(spec.installationId);
    try {
      // A disabled installation needs no source or host revision. Stop an
      // existing generation before loading the current file for activation.
      await this.stopInstance(spec.installationId);
      if (!spec.enabled) {
        this.failures.delete(spec.installationId);
        return;
      }
      const source = decodeSource(this.readSource(spec.sourceHash));
      if (existing) {
        await this.host.update(spec.installationId, { source,
          config: spec.config, version: spec.version });
      } else await this.host.install({ ...spec, source, protocol: 'cat', enabled: false });
      await this.host.enable(spec.installationId);
      const sites = this.hostSites(spec.installationId);
      if (!same(sites, spec.sites)) {
        this.settings.save(value => ({ ...value, scripts: value.scripts.map(script => {
          if (script.installationId !== spec.installationId) return script;
          requireValue(sameExecution(script, spec), 'STORAGE_CONFLICT');
          return { ...script, sites };
        }) }));
        spec.sites = sites;
      }
      this.failures.delete(spec.installationId);
    } catch (error) { this.failures.set(spec.installationId, safeError(error).code); }
    finally {
      this.settled.add(spec.installationId);
      this.applied.set(spec.installationId, spec);
    }
  }
  scheduleStartup() {
    if (this.closed || this.startupScheduled || !this.startupQueue.length) return;
    this.startupScheduled = true;
    const task = this.serial(async () => {
      const installationId = this.startupQueue.shift();
      const spec = this.settings.value.scripts.find(script =>
        script.installationId === installationId);
      if (spec?.enabled && !this.settled.has(installationId)) {
        const state = this.hostState(spec.installationId);
        const old = this.applied.get(spec.installationId);
        if (!(old && state?.enabled && sameExecution(old, spec) &&
          !this.failures.has(spec.installationId) && state.state !== 'failed')) {
          await this.applyChange({ spec, existing: !!state });
        }
      }
    });
    task.finally(() => {
      this.startupScheduled = false;
      this.scheduleStartup();
    }).catch(() => {});
  }
  hostState(installationId) {
    try { return this.host.get(installationId); }
    catch (error) {
      if (safeError(error).code === 'NOT_FOUND') return null;
      throw error;
    }
  }
  stopInitializingSessions() {
    this.host?.stopInitializingSessions?.();
    this.initialStartSignal.abort(fault('HOST_CLOSED'));
  }
  async refresh() {
    await this.start();
    const diskSettings = this.settings.read();
    const previousSettings = new Map(this.settings.value.scripts.map(script =>
      [script.installationId, script]));
    const diskScripts = new Map(diskSettings.scripts.map(script => [script.installationId, script]));
    for (const id of new Set([...previousSettings.keys(), ...diskScripts.keys()])) {
      if (!sameExecution(previousSettings.get(id), diskScripts.get(id)))
        this.host.stopInitializingSessions(id);
    }
    return this.serial(() => {
      const next = this.settings.read();
      const previous = new Map(this.settings.value.scripts.map(script =>
        [script.installationId, script]));
      const current = new Map(next.scripts.map(script => [script.installationId, script]));
      const only = new Set([...new Set([...previous.keys(), ...current.keys()])]
        .filter(id => !same(previous.get(id), current.get(id)) ||
          this.failures.has(id) || this.hostState(id)?.state === 'failed'));
      return this.apply(next, { only });
    });
  }
  hostSites(installationId) {
    return Object.entries(this.hostState(installationId)?.capabilities ?? {})
      .map(([key, value]) => ({ key, ...value }));
  }
  async stopInstance(installationId) {
    let failure;
    if (this.hostState(installationId)) {
      try { await this.host.disable(installationId); }
      catch (error) { failure = error; }
    }
    // Worker failure can detach the public session before its thread exits.
    // Keep that owner until termination is confirmed before sharing its volume.
    for (const worker of this.workers) {
      if (worker.installationId !== installationId) continue;
      try {
        await worker.stop();
        await worker.drainAdapters();
        requireValue(!worker.worker && !worker.live, 'SERVICE_ERROR');
      } catch (error) { failure ??= error; }
      if (!worker.worker && !worker.live) this.workers.delete(worker);
    }
    if (failure) throw failure;
  }
  cancelManagement(installationId) {
    for (const task of this.managementTasks) {
      if (task.installationId !== installationId) continue;
      task.scope.close(fault('CANCELLED'));
    }
  }
  async manage(installationId, signal, body) {
    identifier(installationId);
    const scope = new ResourceScope();
    // Native AbortError has a numeric DOMException.code, not a script fault.
    const cancel = () => scope.close(fault('CANCELLED'));
    const task = { installationId, scope };
    this.managementTasks.add(task);
    try {
      if (signal?.aborted) cancel();
      else signal?.addEventListener('abort', cancel, { once: true });
      scope.assertOpen();
      await this.start();
      scope.assertOpen();
      return await this.serial(async () => {
        scope.assertOpen();
        const abort = () => {
          // Rollback relinquishes this signal before starting the old source.
          // A late abort must not stop that independent initialization.
          if (this.managementSignals.get(installationId) === scope.signal)
            this.host?.stopInitializingSessions(installationId);
        };
        scope.signal.addEventListener('abort', abort, { once: true });
        try { return await body(task); }
        finally { scope.signal.removeEventListener('abort', abort); }
      });
    } finally {
      this.managementTasks.delete(task);
      scope.close();
      signal?.removeEventListener('abort', cancel);
    }
  }
  installedSpec(installationId) {
    const spec = this.settings.read().scripts.find(script => script.installationId === installationId);
    requireValue(spec, 'NOT_FOUND');
    return spec;
  }
  saveSpec(previous, spec) {
    let next, failure;
    try {
      next = this.settings.save(value => {
        const current = value.scripts.find(script => script.installationId === spec.installationId);
        requireValue(same(current, previous), 'STORAGE_CONFLICT');
        return { ...value, scripts: current
          ? value.scripts.map(script => script.installationId === spec.installationId ? spec : script)
          : [...value.scripts, spec] };
      });
    } catch (error) {
      next = this.settings.read();
      if (!same(next.scripts.find(script => script.installationId === spec.installationId), spec)) throw error;
      failure = error;
    }
    this.settings.publish(next);
    this.applied.set(spec.installationId, spec);
    this.publishSettingsSnapshot(next);
    if (failure) throw failure;
    return next;
  }
  async install(input, { signal } = {}) {
    const proposal = clone(input);
    identifier(proposal.installationId);
    requireValue(typeof proposal.source === 'string');
    if (proposal.config !== undefined) proposal.config = normalizeCatConfig(proposal.config);
    return this.manage(proposal.installationId, signal, task => this.replaceSource(proposal, task));
  }
  async import(input, { signal } = {}) {
    const proposal = clone(input);
    catName(proposal.name); catSourceURL(proposal.sourceUrl);
    requireValue(proposal.enabled === undefined || typeof proposal.enabled === 'boolean');
    return this.manage(proposal.installationId, signal, async task => {
      const bytes = await this.downloadSource(proposal.sourceUrl, task.scope.signal);
      try {
        return await this.replaceSource({ ...proposal, sourceType: 'network', source: decodeSource(bytes) }, task);
      } finally { bytes.fill(0); }
    });
  }
  downloadSource(sourceUrl, signal) {
    return downloadCatSource(sourceUrl, { signal, maxSourceBytes: this.limit('maxSourceBytes'),
      timeoutMs: this.limit('requestTimeoutMs') });
  }
  async edit(input, { signal } = {}) {
    const { installationId, name, sourceUrl } = clone(input);
    const displayName = catName(name);
    if (sourceUrl !== undefined) catSourceURL(sourceUrl);
    return this.manage(installationId, signal, async task => {
      const previous = this.installedSpec(installationId);
      requireValue(sourceUrl === undefined || previous.sourceType === 'network');
      task.scope.assertOpen();
      this.saveSpec(previous, { ...previous, name: displayName,
        ...(sourceUrl === undefined ? {} : { sourceUrl }) });
      return this.instance(installationId);
    });
  }
  async update({ installationId, source }, { signal } = {}) {
    // A staged buffer is decoded here so encoding failures are visible in the
    // installation's durable update result as well as the RPC error envelope.
    const bytes = Buffer.isBuffer(source) ? Buffer.from(source) : null;
    try {
      return await this.manage(installationId, signal, async task => {
        const previous = this.installedSpec(installationId);
        try {
          requireValue(previous.sourceType === 'local'
            ? typeof source === 'string' || bytes : source === undefined);
          const downloaded = previous.sourceType === 'network'
            ? await this.downloadSource(previous.sourceUrl, task.scope.signal) : null;
          try {
            return await this.replaceSource({ installationId,
              source: downloaded ? decodeSource(downloaded) : bytes ? decodeSource(bytes) : source }, task,
            { update: true });
          } finally { downloaded?.fill(0); }
        } catch (error) {
          // A post-rename failure may already have committed the candidate.
          // Never turn that accepted version into a failed/rolled-back update.
          const current = this.installedSpec(installationId);
          if (same(current, previous)) this.saveSpec(current, { ...current,
            updateState: 'failed', updateError: task.scope.signal.aborted
              ? task.scope.reason.code : safeError(error, 'STORAGE_ERROR').code });
          throw task.scope.signal.aborted ? task.scope.reason : error;
        }
      });
    } finally { bytes?.fill(0); }
  }
  async replaceSource(proposal, task, { update = false } = {}) {
    const { installationId, source } = proposal;
    requireValue(proposal.enabled === undefined || typeof proposal.enabled === 'boolean');
    requireValue(proposal.version === undefined || typeof proposal.version === 'string');
    requireValue(proposal.sourceType === undefined || ['local', 'network'].includes(proposal.sourceType));
    if (proposal.name !== undefined) catName(proposal.name);
    const current = this.settings.read();
    const previous = current.scripts.find(script => script.installationId === installationId);
    const spec = { installationId,
      config: proposal.config ?? previous?.config ?? {},
      version: proposal.version ?? previous?.version ?? '0.0.0',
      enabled: proposal.enabled ?? previous?.enabled ?? true,
      name: catName(proposal.name ?? previous?.name ?? installationId),
      sourceType: proposal.sourceType ?? previous?.sourceType ?? 'local',
      sourceUrl: proposal.sourceUrl === undefined ? previous?.sourceUrl ?? null : proposal.sourceUrl,
      importedAt: previous?.importedAt ?? null, updatedAt: previous?.updatedAt ?? null,
      updateState: previous?.updateState ?? 'idle', updateError: previous?.updateError ?? null,
      sites: previous?.sites ?? [],
    };
    requireValue(typeof source === 'string' && typeof spec.enabled === 'boolean' &&
      typeof spec.version === 'string' && spec.version.length > 0 && spec.version.length <= 128 &&
      ['local', 'network'].includes(spec.sourceType));
    if (spec.sourceType === 'network') catSourceURL(spec.sourceUrl);
    else requireValue(spec.sourceUrl === null);
    const bytes = Buffer.from(source);
    requireValue(bytes.length <= this.limit('maxSourceBytes'), 'RESOURCE_LIMIT');
    decodeSource(bytes);
    // Disabled installations must not execute just to validate an import.
    // Validate CommonJS syntax here; activation validates its runtime contract.
    try { parse(source, { ecmaVersion: 'latest', allowReturnOutsideFunction: true }); }
    catch { throw fault('SCRIPT_EXCEPTION'); }
    spec.sourceHash = hash(bytes);
    task.scope.assertOpen();
    const previousBytes = previous && existsSync(this.sourcePath(previous.sourceHash))
      ? this.readSource(previous.sourceHash) : null;
    let previousSource = this.sessions.get(installationId)?.revision.source ?? null;
    if (previousSource === null && previousBytes) {
      try { previousSource = decodeSource(previousBytes); } catch { /* An invalid old file can still be replaced. */ }
    }
    const changed = previous ? previousSource === null || hash(Buffer.from(previousSource)) !== spec.sourceHash : false;
    if (update && !changed) {
      this.saveSpec(previous, { ...previous, updateState: 'unchanged', updateError: null });
      return this.instance(installationId);
    }
    const file = this.sourcePath(spec.sourceHash);
    const replacedBytes = existsSync(file) ? this.readSource(spec.sourceHash) : null;
    let touchedSource = false, committed = false, failure;
    this.replacements.add(installationId);
    this.managementSignals.set(installationId, task.scope.signal);
    this.startingSettings = { ...current, scripts: [
      ...current.scripts.filter(script => script.installationId !== installationId), spec,
    ] };
    try {
      await this.stopInstance(installationId);
      task.scope.assertOpen();
      if (!previous) removePrivateVolume(this.dataRoot, installationId);
      if (this.hostState(installationId)) await this.host.update(installationId, { source,
        config: spec.config, version: spec.version });
      else await this.host.install({ ...spec, source, protocol: 'cat', enabled: false });
      task.scope.assertOpen();
      if (spec.enabled) {
        await this.host.enable(installationId);
        spec.sites = this.hostSites(installationId);
      }
      task.scope.assertOpen();
      requireValue(!this.closed, 'HOST_CLOSED');
      const now = this.clock();
      if (!previous) spec.importedAt = now;
      if (changed) spec.updatedAt = now;
      spec.updateState = previous ? changed ? 'updated' : 'unchanged' : 'idle';
      spec.updateError = null;
      // The candidate has passed activation (or syntax validation if disabled).
      // Until this point both the old source and its description remain durable.
      touchedSource = true;
      atomicWrite(file, bytes);
      try { this.saveSpec(previous, spec); }
      catch (error) {
        const disk = this.settings.read();
        if (same(disk.scripts.find(script => script.installationId === installationId), spec)) {
          committed = true;
        }
        throw error;
      }
      committed = true;
    } catch (error) {
      if (!committed) {
        // Stop a candidate completely before restoring the old revision on the
        // same private volume. Cancellation never commits partially tested code.
        this.managementSignals.delete(installationId);
        this.startingSettings = current;
        try {
          await this.stopInstance(installationId);
          if (touchedSource) {
            if (replacedBytes && !this.readSource(spec.sourceHash).equals(replacedBytes)) atomicWrite(file, replacedBytes);
            else if (!replacedBytes && !current.scripts.some(script => script.sourceHash === spec.sourceHash))
              this.deleteSource(spec.sourceHash);
          }
          if (!this.closed) {
            if (previous && previousSource !== null) {
              if (this.hostState(installationId)) await this.host.update(installationId, {
                source: previousSource, config: previous.config, version: previous.version });
              else await this.host.install({ ...previous, source: previousSource, protocol: 'cat', enabled: false });
              // A queued disable/uninstall is not a committed change of intent.
              // Finish rollback first; that command may itself cancel or fail.
              if (previous.enabled) await this.host.enable(installationId);
              this.failures.delete(installationId);
            } else {
              if (this.hostState(installationId)) await this.host.uninstall(installationId);
              if (!previous) removePrivateVolume(this.dataRoot, installationId);
              else if (previous.enabled) this.failures.set(installationId, 'CORRUPT_STORAGE');
            }
          }
        } catch (recoveryError) { this.failures.set(installationId, safeError(recoveryError, 'STORAGE_ERROR').code); }
        if (previous) this.applied.set(installationId, previous);
      }
      failure = task.scope.signal.aborted ? task.scope.reason : error;
    } finally {
      if (committed) this.failures.delete(installationId);
      this.settled.add(installationId);
      this.replacements.delete(installationId);
      this.managementSignals.delete(installationId);
      this.startingSettings = null;
      this.sweepProxies();
      if (committed && previous && previous.sourceHash !== spec.sourceHash &&
        !this.settings.value.scripts.some(script => script.sourceHash === previous.sourceHash)) {
        try { this.deleteSource(previous.sourceHash); }
        catch (error) { failure ??= error; }
      }
    }
    if (failure) throw failure;
    return this.instance(installationId);
  }
  async restart(installationId, { signal } = {}) {
    return this.manage(installationId, signal, async task => {
      const spec = this.installedSpec(installationId);
      requireValue(spec.enabled, 'DISABLED');
      this.managementSignals.set(installationId, task.scope.signal);
      try {
        await this.applyChange({ spec, existing: !!this.hostState(installationId) });
        task.scope.assertOpen();
        this.settings.publish(this.settings.read());
        if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
        return this.instance(installationId);
      } finally { this.managementSignals.delete(installationId); }
    });
  }
  async setEnabled(installationId, enabled, { signal } = {}) {
    identifier(installationId); requireValue(typeof enabled === 'boolean');
    await this.start();
    if (!enabled) {
      this.cancelManagement(installationId);
      // Active replacement cancellation owns the candidate, not its rollback.
      if (!this.replacements.has(installationId)) this.host.stopInitializingSessions(installationId);
    }
    return this.manage(installationId, signal, async task => {
      requireValue(this.settings.read().scripts.some(script => script.installationId === installationId), 'NOT_FOUND');
      let next, failure;
      try { next = this.settings.save(value => ({ ...value, scripts: value.scripts.map(script =>
        script.installationId === installationId ? { ...script, enabled } : script) })); }
      catch (error) {
        if (error.code !== 'COMMIT_UNKNOWN') throw error;
        next = this.settings.read(); failure = error;
      }
      this.managementSignals.set(installationId, task.scope.signal);
      try {
        await this.apply(next, { only: new Set([installationId]) });
        task.scope.assertOpen();
        if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
        if (failure) throw failure;
        return this.instance(installationId);
      } finally { this.managementSignals.delete(installationId); }
    });
  }
  async uninstall(installationId, { signal } = {}) {
    identifier(installationId); await this.start();
    this.cancelManagement(installationId);
    if (!this.replacements.has(installationId)) this.host.stopInitializingSessions(installationId);
    return this.manage(installationId, signal, async () => {
      let next, failure;
      try { next = this.settings.save(value => ({ ...value,
        scripts: value.scripts.filter(script => script.installationId !== installationId) })); }
      catch (error) {
        if (error.code !== 'COMMIT_UNKNOWN') throw error;
        next = this.settings.read(); failure = error;
      }
      const result = await this.apply(next, { only: new Set([installationId]) });
      if (failure) throw failure;
      return result;
    });
  }
  starting(installationId, state = this.hostState(installationId),
    snapshot = this.snapshots.get(installationId)) {
    if (this.replacements.has(installationId)) return true;
    if (this.failures.has(installationId)) return false;
    const spec = (this.startingSettings ?? this.settings.value).scripts.find(script =>
      script.installationId === installationId);
    if (!spec?.enabled) return false;
    if (!this.settled.has(installationId) || !state) return true;
    if (!state.enabled) return false;
    return state.state === 'ready' && !snapshot;
  }
  instance(installationId) {
    const spec = this.settings.value.scripts.find(script =>
      script.installationId === installationId);
    const state = this.hostState(installationId), snapshot = this.snapshots.get(installationId);
    const failure = this.failures.get(installationId);
    const status = failure ? 'failed' : spec?.enabled === false ? 'disabled'
      : this.starting(installationId, state, snapshot) ? 'starting'
        : !state ? 'disabled'
          : !state.enabled ? 'disabled' : state.state;
    return { installationId, generation: state?.generation ?? 0, state: status,
      name: spec?.name ?? installationId, sourceType: spec?.sourceType ?? 'local',
      sourceUrl: spec?.sourceUrl ?? null, enabled: spec?.enabled ?? false,
      importedAt: spec?.importedAt ?? null, updatedAt: spec?.updatedAt ?? null,
      updateState: spec?.updateState ?? 'idle', updateError: spec?.updateError ?? null,
      version: spec?.version ?? state?.version ?? '0.0.0',
      sourceHash: this.replacements.has(installationId) ? spec?.sourceHash ?? null
        : state?.contentHash ?? spec?.sourceHash ?? null,
      error: failure ?? state?.lastFailure ?? null,
      sites: clone(status === 'ready' ? this.hostSites(installationId) : spec?.sites ?? []),
      ...(status === 'ready' && snapshot ? {
        ...(snapshot.endpoint ? { managementUrl: snapshot.endpoint.managementUrl } : {}),
        endpoints: snapshot.endpoints.map(({ proxyId, ...endpoint }) => endpoint) } : {}) };
  }
  getSession(installationId, generation) {
    requireValue(this.settings.value.scripts.find(script =>
      script.installationId === installationId)?.enabled !== false, 'DISABLED');
    requireValue(!this.starting(installationId), 'STARTING');
    const state = this.host.get(installationId), snapshot = this.snapshots.get(installationId);
    requireValue(state.state === 'ready' && snapshot, 'DISABLED');
    requireValue(generation === undefined || state.generation === generation, 'STALE_SESSION');
    const session = this.sessions.get(installationId);
    requireValue(session, 'DISABLED');
    return session;
  }
  async invoke(target, options) {
    const signal = options?.signal;
    await this.start();
    const session = this.getSession(target.installationId, target.generation);
    const assertCurrent = () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      requireValue(!this.closed, 'HOST_CLOSED');
      requireValue(session.live && this.sessions.get(session.installationId) === session, 'STALE_SESSION');
    };
    let result;
    // A failed receipt can also belong to a retired generation.
    try { result = await this.host.invoke(target, options); }
    finally { assertCurrent(); }
    if (!result || typeof result !== 'object' || typeof result.url !== 'string' ||
      !/\.m3u8(?:\?|$)/i.test(result.url) ||
      result._meow && !result[ownedExtension]) return result;
    // Optional metadata enhancement is never a playback/provider parser.
    try {
      result = await session.request('enhanceM3U8', {
        value: result, owned: Boolean(result[ownedExtension]),
      }, { signal, timeoutMs: 3000 });
    } catch (error) {
      if (error?.code === 'CANCELLED') throw error;
    }
    assertCurrent();
    return result;
  }
  async management(installationId, generation) {
    await this.start();
    return this.getSession(installationId, generation).request('management.detect', {});
  }
  async sync(installationId, generation, input) {
    await this.start();
    return this.getSession(installationId, generation).request('management.sync', { input });
  }
  async status() {
    await this.start();
    return this.statusSnapshot();
  }
  statusSnapshot() {
    return { settingsPath: this.settings.path, aggregate: {
      origin: `http://127.0.0.1:${this.server.address().port}`, access: this.access },
      instances: this.settings.value.scripts.map(spec => this.instance(spec.installationId)) };
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
  sweepProxies(session) {
    const snapshot = session ? this.snapshots.get(session.installationId) : null;
    const origins = snapshot ? new Set(snapshot.endpoints.map(endpoint => endpoint.origin)) : null;
    for (const [id, entry] of this.proxies) {
      const current = this.snapshots.get(entry.session.installationId);
      if (!current || !entry.session.live || entry.session.stopped || !entry.session.worker ||
        entry.session !== this.sessions.get(entry.session.installationId) ||
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
    const scope = new ResourceScope(undefined, this.limit('requestTimeoutMs'));
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
      scope.add(() => {
        if (!request.complete && !request.destroyed) request.destroy();
      });
      await scope.race((async () => {
        const maximum = this.limit('maxMemoryBytes');
        let length = 0, oversized = false;
        for await (const chunk of request) {
          length += chunk.length;
          if (length > maximum) {
            if (!oversized) {
              for (const retained of chunks) retained.fill(0);
              chunks.length = 0;
              oversized = true;
            }
            continue;
          }
          chunks.push(chunk);
        }
        requireValue(!oversized, 'RESOURCE_LIMIT');
      })());
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
    const snapshot = this.snapshots.get(installationId);
    requireValue(entry && snapshot && entry.session.installationId === installationId &&
      entry.session.generation === Number(generation) &&
      entry.session === this.sessions.get(installationId) && entry.session.live &&
      !entry.session.stopped && entry.session.worker, 'STALE_SESSION');
    requireValue(['GET', 'HEAD'].includes(request.method), 'PERMISSION_DENIED');
    const prefix = `/proxy/${installationId}/${generation}/${id}`;
    const path = url.pathname.slice(prefix.length) || '/';
    requireValue(allowedProxyPath(path), 'PERMISSION_DENIED');
    const target = fixedOriginURL(entry.origin, path, url.search);
    const scope = new ResourceScope(entry.session.proxyLifecycle.signal,
      this.limit('requestTimeoutMs'));
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
      headers[scriptAccessHeader] = snapshot.access;
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
        outHeaders.location = this.mapSnapshotProxy(entry.session, snapshot, next.href);
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
            return this.mapSnapshotProxy(entry.session, snapshot, candidate.href);
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
      this.startupQueue.length = 0;
      for (const task of this.managementTasks) {
        task.scope.close(fault('HOST_CLOSED'));
      }
      this.stopInitializingSessions();
      const host = this.host;
      const hostClosing = Promise.resolve().then(() => host?.close()).then(
        result => ({ result }),
        error => ({ error: safeError(error, 'SERVICE_ERROR') }),
      );
      // Stop workers before waiting for serialized runtime work that may be
      // waiting on one of those workers to initialize or answer an RPC.
      await this.tail.catch(() => {});
      try {
        const outcome = await hostClosing;
        if (outcome.error) failure ??= outcome.error;
        const result = outcome.result;
        if (result?.cleanupComplete === false) {
          const timedOut = result.pendingAdapterTasks > 0 ||
            result.cleanupIssues?.some(issue => issue.code === 'TIMEOUT');
          failure ??= fault(timedOut ? 'TIMEOUT' : 'SERVICE_ERROR');
        }
      } catch (error) { failure ??= safeError(error, 'SERVICE_ERROR'); }
      for (const worker of this.workers) {
        if (!worker.worker) continue;
        try { await worker.stop(); }
        catch (error) { failure ??= safeError(error, 'SERVICE_ERROR'); }
        if (worker.worker) failure ??= fault('SERVICE_ERROR');
      }
      this.sessions.clear();
      this.snapshots.clear();
      if (this.server) this.serverClosure(this.server);
      this.server = null;
      try { await this.drainServerClosures(); }
      catch (error) { failure ??= safeError(error, 'SERVICE_ERROR'); }
      this.serverClosures = new WeakMap();
      this.pendingServerClosures.clear();
      this.serverCloseFailure = null;
      this.proxies.clear();
      this.applied.clear(); this.failures.clear();
      this.settled.clear();
      this.workers.clear();
      this.replacements.clear(); this.managementSignals.clear();
      this.initialization = null;
      this.host = null; this.settings = null;
      this.network = null; this.onDiagnostic = null; this.onScriptError = null; this.onNetwork = null;
      if (failure) throw failure;
    })();
    return this.closing;
  }
}

export const createCatRuntime = options => new CatRuntime(options);
