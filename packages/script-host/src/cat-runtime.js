import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { randomBytes, createHash } from 'node:crypto';
import { readFileSync, existsSync, mkdirSync, lstatSync, realpathSync, rmSync } from 'node:fs';
import { resolve, join, isAbsolute, relative, sep } from 'node:path';
import { ScriptHost } from './host.js';
import { WorkerSession, workerResult } from './worker-session.js';
import { CatSettingsFile, normalizeCatConfig } from './cat-settings.js';
import { atomicWrite, removePrivateVolume } from './private-volume.js';
import { scriptAccessHeader } from './script-services.js';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';
import { ResourceScope } from './scope.js';
import { ownedExtension } from './cat-output.js';
import { allowedProxyPath, fixedOriginURL, mapLocalSnapshot } from './cat-proxy.js';
import { closeResource } from './resource-close.js';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
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
  constructor({ dataRoot, network, onDiagnostic, onScriptError, onNetwork, limits } = {}) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot));
    this.dataRoot = resolve(dataRoot);
    this.network = network; this.onDiagnostic = onDiagnostic; this.onScriptError = onScriptError;
    this.onNetwork = onNetwork; this.limits = limits;
    this.sessions = new Map(); this.snapshots = new Map(); this.proxies = new Map();
    this.candidates = new Map();
    this.aggregatePort = 0;
    this.serverClosures = new WeakMap();
    this.pendingServerClosures = new Set();
    this.serverCloseFailure = null;
    this.transactions = new Map(); this.pending = new Set(); this.closed = false;
    this.settled = new Set();
    this.initialStartSignal = new AbortController();
    this.initialization = null;
    this.access = randomBytes(32).toString('hex');
    this.applied = new Map();
    this.failures = new Map();
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
      this.initialization = (async () => {
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
      })();
    }
    await this.initialization;
    return this.statusSnapshot();
  }
  serial(installationId, body) {
    requireValue(!this.closed, 'HOST_CLOSED');
    const previous = this.transactions.get(installationId) ?? Promise.resolve();
    const task = previous.catch(() => {}).then(() => {
      requireValue(!this.closed, 'HOST_CLOSED'); return body();
    });
    this.transactions.set(installationId, task);
    this.pending.add(task);
    task.finally(() => {
      this.pending.delete(task);
      if (this.transactions.get(installationId) === task) this.transactions.delete(installationId);
    }).catch(() => {});
    return task;
  }
  async configureGateway() {
    if (this.gatewayPending) return this.gatewayPending;
    // Only physical gateway creation is shared. Script initialization never
    // runs under this owner and cannot hold up another installation.
    this.gatewayPending = (async () => {
      for (;;) {
        const next = this.settings.read();
        const changed = !this.server || next.port !== this.settings.value.port;
        const candidate = changed ? await this.listen(next.port) : this.server;
        if (this.closed) {
          if (changed) await this.closeOwnedServer(candidate);
          throw fault('HOST_CLOSED');
        }
        const previous = this.server;
        this.server = candidate;
        this.aggregatePort = candidate.address().port;
        this.settings.publish(next);
        this.publishSettingsSnapshot(next);
        if (previous && previous !== candidate) this.serverClosure(previous);
        if (same(next, this.settings.read())) return;
      }
    })();
    try { await this.gatewayPending; }
    finally { this.gatewayPending = null; }
  }
  workerSession(input) {
    const candidate = new WorkerSession({
      ...input,
      protocol: 'cat',
      startSignal: input.startSignal
        ? AbortSignal.any([input.startSignal, this.initialStartSignal.signal])
        : this.initialStartSignal.signal,
      startParams: {
        dataRoot: this.dataRoot,
        settings: { value: clone(this.settings.value) },
        aggregate: { port: this.aggregatePort },
      },
      processWorkerResult: (method, value, session) => {
        if (method !== 'invoke' && method !== 'enhanceM3U8') return undefined;
        const result = clone(value?.result);
        if (Array.isArray(value?.newProxies))
          this.registerProxyMappings(session, value.newProxies);
        // The worker's structured boundary cannot carry the ownership Symbol.
        // Restore it only as a non-enumerable internal marker on the same result.
        if (value?.owned && result && typeof result === 'object')
          Object.defineProperty(result, ownedExtension, { value: true, configurable: true });
        return workerResult(result);
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
        if (this.candidates.get(session.installationId) === session)
          this.candidates.delete(session.installationId);
        if (this.sessions.get(session.installationId) !== session) return;
        this.sessions.delete(session.installationId);
        this.snapshots.delete(session.installationId);
        this.sweepProxies();
      },
    });
    this.candidates.set(input.installationId, candidate);
    return candidate;
  }
  publishCandidate(installationId) {
    const candidate = this.candidates.get(installationId);
    const state = this.hostState(installationId);
    // An activated Worker is still a trial until host has crossed its signal
    // fence and committed that generation. A cancelled trial never displaces
    // the old endpoint or its proxy registrations.
    if (!candidate?.live || !candidate.activated || state?.state !== 'ready' ||
      candidate.generation !== state.generation || !candidate.catSnapshot?.endpoint) return;
    this.sessions.set(installationId, candidate);
    this.snapshots.set(installationId, this.catSnapshot(candidate.catSnapshot));
    this.registerProxyMappings(candidate, candidate.catSnapshot.endpoints);
    this.candidates.delete(installationId);
    this.sweepProxies();
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
      aggregatePort: this.aggregatePort,
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
      ...this.sessions.values(), ...this.candidates.values()]);
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
  async apply(next, { background = false, only = null, force = false, uploaded, signal } = {}) {
    requireValue(!signal?.aborted, 'CANCELLED');
    const previous = this.settings.value.scripts;
    await this.configureGateway();
    requireValue(!this.closed, 'HOST_CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
    const ids = only ?? new Set([...previous.map(spec => spec.installationId),
      ...next.scripts.map(spec => spec.installationId)]);
    const applyOne = async installationId => {
      const spec = next.scripts.find(value => value.installationId === installationId);
      const old = this.applied.get(installationId) ?? previous.find(value => value.installationId === installationId);
      const state = this.hostState(installationId);
      if (!spec) {
        const owner = this.sessions.get(installationId) ?? this.host.initializingSessions()
          .find(session => session.installationId === installationId);
        let failure;
        try { if (state) await this.host.uninstall(installationId); }
        catch (error) { failure = safeError(error, 'SERVICE_ERROR'); }
        this.applied.delete(installationId); this.failures.delete(installationId);
        this.sessions.delete(installationId); this.snapshots.delete(installationId);
        this.settled.delete(installationId);
        if (!failure || owner?.stopped && !owner.worker) {
          try { removePrivateVolume(this.dataRoot, installationId); }
          catch (error) { this.failures.set(installationId, safeError(error, 'STORAGE_ERROR').code); }
          const hashes = new Set([old?.sourceHash,
            previous.find(value => value.installationId === installationId)?.sourceHash].filter(Boolean));
          for (const sourceHash of hashes) {
            if (this.settings.read().scripts.some(value => value.sourceHash === sourceHash)) continue;
            try { this.deleteSource(sourceHash); }
            catch (error) { this.failures.set(installationId, safeError(error, 'STORAGE_ERROR').code); }
          }
        }
        if (failure) throw failure;
        return;
      }
      if (!force && old && (!spec.enabled || state) && same(old, spec) &&
        !this.failures.has(installationId) && state?.state !== 'failed') return;
      await this.applyChange({ spec, existing: Boolean(state), uploaded, signal });
      if (old && old.sourceHash !== spec.sourceHash && !this.failures.has(installationId) &&
        !this.settings.read().scripts.some(value => value.sourceHash === old.sourceHash)) {
        try { this.deleteSource(old.sourceHash); }
        catch (error) { this.failures.set(installationId, safeError(error, 'STORAGE_ERROR').code); }
      }
    };
    if (background) {
      for (const id of ids) {
        const spec = next.scripts.find(value => value.installationId === id);
        if (!spec?.enabled) { await applyOne(id); continue; }
        this.settled.delete(id);
        this.serial(id, () => applyOne(id)).finally(() => this.sweepProxies()).catch(() => {});
      }
    } else {
      const results = await Promise.allSettled([...ids].map(applyOne));
      const failed = results.find(result => result.status === 'rejected');
      if (failed) throw failed.reason;
    }
    this.sweepProxies();
    return this.applyResult();
  }
  applyResult() {
    return { applied: !this.failures.size,
      failures: [...this.failures].map(([installationId, code]) => ({ installationId, code })) };
  }
  async applyChange({ spec, existing, uploaded, signal }) {
    requireValue(!signal?.aborted, 'CANCELLED');
    this.settled.delete(spec.installationId);
    try {
      // A disabled installation needs no source or host revision. Enabled
      // updates keep the old generation until the candidate is committed.
      if (!spec.enabled) {
        if (existing) await this.host.disable(spec.installationId);
        this.failures.delete(spec.installationId);
        return;
      }
      // Only the current import carries an immutable source/hash pair. On
      // every other entry, the persisted hash names the import file, whose
      // actual bytes may have been edited since the import.
      const source = uploaded?.source ?? decodeSource(this.readSource(spec.sourceHash));
      const contentHash = uploaded?.contentHash;
      if (existing) {
        await this.host.update(spec.installationId, { source,
          contentHash, config: spec.config, version: spec.version }, { signal });
      } else await this.host.install({ ...spec, source, contentHash, protocol: 'cat', enabled: false }, { signal });
      if (!this.hostState(spec.installationId).enabled) await this.host.enable(spec.installationId, { signal });
      this.failures.delete(spec.installationId);
    } catch (error) { this.failures.set(spec.installationId, safeError(error).code); }
    finally {
      this.publishCandidate(spec.installationId);
      this.settled.add(spec.installationId);
      if (!this.failures.has(spec.installationId) || !this.applied.has(spec.installationId))
        this.applied.set(spec.installationId, spec);
    }
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
  captureInitialization(installationId) {
    identifier(installationId);
    requireValue(!this.closed, 'HOST_CLOSED');
    return this.host?.captureInitialization(installationId) ?? (() => {});
  }
  async refresh({ signal } = {}) {
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    await this.start();
    requireValue(!signal?.aborted, 'CANCELLED');
    const diskSettings = this.settings.read();
    const previousSettings = new Map(this.settings.value.scripts.map(script =>
      [script.installationId, script]));
    const diskScripts = new Map(diskSettings.scripts.map(script => [script.installationId, script]));
    for (const id of new Set([...previousSettings.keys(), ...diskScripts.keys()])) {
      if (!same(previousSettings.get(id), diskScripts.get(id)))
        this.host.stopInitializingSessions(id);
    }
    const ids = [...new Set([...previousSettings.keys(), ...diskScripts.keys()])]
      .filter(id => !same(previousSettings.get(id), diskScripts.get(id)) ||
        this.failures.has(id) || this.hostState(id)?.state === 'failed');
    await this.configureGateway();
    requireValue(!signal?.aborted, 'CANCELLED');
    const results = await Promise.allSettled(ids.map(id => this.serial(id, () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      return this.apply(this.settings.read(), { only: new Set([id]), signal });
    })));
    requireValue(!signal?.aborted, 'CANCELLED');
    const failed = results.find(result => result.status === 'rejected');
    if (failed) throw failed.reason;
    return this.applyResult();
  }

  async install({ installationId, source, contentHash, config = {}, version = '0.0.0', enabled = false },
    { signal } = {}) {
    identifier(installationId);
    requireValue(typeof source === 'string' && typeof enabled === 'boolean' &&
      typeof version === 'string' && version.length > 0 && version.length <= 128);
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(contentHash === undefined || typeof contentHash === 'string' && /^[a-f0-9]{64}$/.test(contentHash));
    requireValue(!signal?.aborted, 'CANCELLED');
    config = normalizeCatConfig(config);
    await this.start();
    requireValue(!signal?.aborted, 'CANCELLED');
    const bytes = Buffer.from(source), sourceHash = contentHash ?? hash(bytes);
    decodeSource(bytes);
    requireValue(bytes.length <= this.limit('maxSourceBytes'), 'RESOURCE_LIMIT');
    this.captureInitialization(installationId)();
    return this.serial(installationId, async () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      const current = this.settings.read();
      const previous = current.scripts.find(script =>
        script.installationId === installationId);
      if (!previous) {
        if (this.hostState(installationId)) {
          try { await this.host.uninstall(installationId); }
          catch (error) {
            const failure = safeError(error, 'SERVICE_ERROR');
            this.failures.set(installationId, failure.code);
            throw failure;
          }
          this.applied.delete(installationId);
          this.sessions.delete(installationId);
          this.snapshots.delete(installationId);
        }
        try { removePrivateVolume(this.dataRoot, installationId); }
        catch (error) {
          const failure = safeError(error, 'STORAGE_ERROR');
          this.failures.set(installationId, failure.code);
          throw failure;
        }
      }
      requireValue(!signal?.aborted, 'CANCELLED');
      const file = this.sourcePath(sourceHash);
      atomicWrite(file, bytes);
      const next = this.settings.save(value => ({ ...value, scripts: [
        ...value.scripts.filter(script => script.installationId !== installationId),
        { installationId, sourceHash, config, version, enabled },
      ] }));
      await this.apply(next, { only: new Set([installationId]), force: true,
        uploaded: { source, contentHash: sourceHash }, signal });
      if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
      return this.instance(installationId);
    });
  }
  async setEnabled(installationId, enabled, { signal } = {}) {
    identifier(installationId); requireValue(typeof enabled === 'boolean');
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    await this.start();
    requireValue(!signal?.aborted, 'CANCELLED');
    if (!enabled) this.captureInitialization(installationId)();
    return this.serial(installationId, async () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      requireValue(this.settings.read().scripts.some(script => script.installationId === installationId), 'NOT_FOUND');
      const next = this.settings.save(value => ({ ...value, scripts: value.scripts.map(script =>
        script.installationId === installationId ? { ...script, enabled } : script) }));
      await this.apply(next, { only: new Set([installationId]), signal });
      if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
      return this.instance(installationId);
    });
  }
  async uninstall(installationId) {
    identifier(installationId); await this.start();
    this.captureInitialization(installationId)();
    return this.serial(installationId, async () => {
      const next = this.settings.save(value => ({ ...value,
        scripts: value.scripts.filter(script => script.installationId !== installationId) }));
      return this.apply(next, { only: new Set([installationId]) });
    });
  }
  starting(installationId, state = this.hostState(installationId),
    snapshot = this.snapshots.get(installationId)) {
    if (state?.state === 'ready' && snapshot &&
      this.sessions.get(installationId)?.generation === state.generation) return false;
    if (this.failures.has(installationId)) return false;
    const spec = this.settings.value.scripts.find(script =>
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
    const ready = state?.state === 'ready' && this.sessions.get(installationId)?.live &&
      this.sessions.get(installationId)?.generation === state.generation && snapshot;
    const status = ready ? 'ready' : failure ? 'failed'
      : this.starting(installationId, state, snapshot) ? 'starting'
        : !state ? 'disabled'
          : !state.enabled ? 'disabled' : state.state;
    return { installationId, generation: state?.generation ?? 0, state: status,
      version: ready ? state.version : spec?.version ?? state?.version ?? '0.0.0',
      sourceHash: state?.contentHash ?? spec?.sourceHash ?? null,
      error: failure ?? state?.lastFailure ?? null,
      sites: state ? Object.entries(state.capabilities).map(([key, value]) => ({ key, ...value })) : [],
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
    await this.start();
    requireValue(this.settings.value.scripts.find(script =>
      script.installationId === target.installationId)?.enabled !== false, 'DISABLED');
    requireValue(!this.starting(target.installationId), 'STARTING');
    const result = await this.host.invoke(target, options);
    const owned = Boolean(result && typeof result === 'object' && result[ownedExtension]);
    // Ownership is trusted control data, never part of the public JSON value.
    if (result && typeof result === 'object') delete result[ownedExtension];
    if (!result || typeof result !== 'object' || typeof result.url !== 'string' ||
      !/\.m3u8(?:\?|$)/i.test(result.url) ||
      result._meow && !owned) return result;
    // Optional metadata enhancement is never a playback/provider parser.
    try {
      const enhanced = await this.getSession(target.installationId, target.generation)
        .request('enhanceM3U8', {
          value: result, owned,
        }, { timeoutMs: 3000 });
      if (enhanced && typeof enhanced === 'object') delete enhanced[ownedExtension];
      return enhanced;
    } catch { return result; }
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
        for await (const chunk of request) chunks.push(chunk);
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
      this.stopInitializingSessions();
      const host = this.host;
      const hostClosing = Promise.resolve().then(() => host?.close()).then(
        result => ({ result }),
        error => ({ error: safeError(error, 'SERVICE_ERROR') }),
      );
      // Stop workers before waiting for serialized runtime work that may be
      // waiting on one of those workers to initialize or answer an RPC.
      await Promise.allSettled([this.initialization, this.gatewayPending, ...this.pending]);
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
      this.sessions.clear();
      this.candidates.clear();
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
      this.transactions.clear(); this.pending.clear();
      this.initialization = null;
      this.host = null; this.settings = null;
      this.network = null; this.onDiagnostic = null; this.onScriptError = null; this.onNetwork = null;
      if (failure) throw failure;
    })();
    return this.closing;
  }
}

export const createCatRuntime = options => new CatRuntime(options);
