import { createServer, request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { createHash } from 'node:crypto';
import { readFileSync, existsSync, mkdirSync, statSync, rmSync } from 'node:fs';
import { resolve, join, isAbsolute } from 'node:path';
import { parse } from 'acorn';
import { ScriptHost } from './host.js';
import { WorkerSession, workerResult } from './worker-session.js';
import { CatSettingsFile, normalizeCatConfig, catName, catSourceURL } from './cat-settings.js';
import { downloadCatSource } from './cat-source-download.js';
import { atomicWrite, removePrivateVolume } from './private-volume.js';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';
import { ResourceScope } from './scope.js';
import { ownedExtension } from './cat-output.js';
import { isLoopbackHost } from './network-boundary.js';
import { closeResource } from './resource-close.js';
import { CatProxyResources, proxyPlaylistInput, readProxyPlaylist, proxyPlaylistBody, proxyManifestLocation,
  resolveProxyManifest } from './cat-media-proxy.js';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const execution = spec => spec && [spec.sourceHash, spec.config, spec.version, spec.enabled];
const sameExecution = (a, b) => same(execution(a), execution(b));
const hopByHop = new Set(['connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
  'te', 'trailer', 'transfer-encoding', 'upgrade']);
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
    this.dataRoot = resolve(dataRoot);
    this.network = network; this.onDiagnostic = onDiagnostic; this.onScriptError = onScriptError;
    this.onNetwork = onNetwork; this.limits = limits; this.clock = clock;
    this.sessions = new Map(); this.snapshots = new Map(); this.proxies = new Map();
    this.candidates = new Map();
    this.candidateSpecs = new Map(); this.workers = new Set();
    this.replacements = new Set(); this.managementTasks = new Set();
    this.aggregatePort = 0;
    this.serverClosures = new WeakMap();
    this.pendingServerClosures = new Set();
    this.serverCloseFailure = null;
    this.proxyTasks = new Set();
    this.proxyCloseFailures = new WeakMap();
    this.proxyCloseFailure = null;
    this.transactions = new Map(); this.pending = new Set(); this.closed = false;
    this.settled = new Set();
    this.initialStartSignal = new AbortController();
    this.initialization = null;
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
        this.sourcesRoot = join(this.dataRoot, 'sources');
        mkdirSync(this.sourcesRoot, { recursive: true });
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
        settings: { value: this.workerSettings(input.installationId) },
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
    this.workers.add(candidate);
    return candidate;
  }
  publishCandidate(installationId) {
    const candidate = this.candidates.get(installationId);
    const state = this.hostState(installationId);
    // An activated Worker is still a trial until host has crossed its signal
    // fence and committed that generation. Endpoints and proxy registrations
    // become visible only after the installation transaction accepts it.
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
      if (current?.origin === entry.origin) continue;
      this.proxies.set(id, { session, origin: entry.origin });
    }
  }
  catSnapshot(cat) {
    return {
      endpoint: clone(cat.endpoint),
      endpoints: clone(cat.endpoints),
      aggregatePort: this.aggregatePort,
    };
  }
  mapSnapshotProxy(session, snapshot, value, proxyOrigin = `http://127.0.0.1:${snapshot.aggregatePort}`) {
    const url = new URL(value);
    requireValue(['http:', 'https:'].includes(url.protocol), 'UNSUPPORTED_PROTOCOL');
    const port = Number(url.port || (url.protocol === 'https:' ? 443 : 80));
    // Local listener aliases are address translation, not an access filter:
    // any other HTTP(S) address is returned unchanged.
    const endpoint = snapshot.endpoints.find(item => {
      const actual = new URL(item.origin);
      return actual.origin === url.origin || isLoopbackHost(url.hostname) &&
        actual.protocol === url.protocol &&
        (Number(actual.port || (actual.protocol === 'https:' ? 443 : 80)) === port ||
          item.requestedPorts?.includes(port));
    });
    if (!endpoint) return url.href;
    const mapped = new URL(`${proxyOrigin}/proxy/${session.installationId}/${session.generation}/${endpoint.proxyId}${url.pathname}${url.search}${url.hash}`);
    mapped.username = url.username; mapped.password = url.password;
    return mapped.href;
  }
  workerSettings(installationId, next = this.settings.value) {
    const settings = clone(next), spec = this.candidateSpecs.get(installationId);
    if (spec) settings.scripts = [...settings.scripts.filter(script =>
      script.installationId !== installationId), clone(spec)];
    return settings;
  }
  publishSettingsSnapshot(next) {
    const aggregate = { port: this.aggregatePort };
    const sessions = new Set([...(this.host?.initializingSessions?.() ?? []),
      ...this.sessions.values(), ...this.candidates.values()]);
    for (const session of sessions) {
      const snapshot = this.snapshots.get(session.installationId);
      if (snapshot && this.sessions.get(session.installationId) === session)
        snapshot.aggregatePort = aggregate.port;
      try { session.publishSettings({ settings: this.workerSettings(session.installationId, next), aggregate }); }
      catch { /* Settings observation cannot block publication. */ }
    }
  }
  limit(name) {
    return this.limits?.[name] ?? this.host.limits[name];
  }
  sourcePath(sourceHash) {
    return join(this.sourcesRoot, sourceHash + '.js');
  }
  readSource(sourceHash) {
    try {
      const file = this.sourcePath(sourceHash);
      const state = statSync(file);
      requireValue(state.isFile(), 'CORRUPT_STORAGE');
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
      rmSync(file, { maxRetries: 3, retryDelay: 100 });
    } catch (error) {
      if (error.code === 'ENOENT') return;
      throw safeError(error, 'STORAGE_ERROR');
    }
  }
  async apply(next, { background = false, only = null, signal } = {}) {
    requireValue(!signal?.aborted, 'CANCELLED');
    const previous = this.settings.value.scripts;
    await this.configureGateway();
    requireValue(!this.closed, 'HOST_CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
    const ids = only ?? new Set([...this.applied.keys(), ...previous.map(spec => spec.installationId),
      ...next.scripts.map(spec => spec.installationId)]);
    const applyOne = async installationId => {
      const spec = next.scripts.find(value => value.installationId === installationId);
      const old = this.applied.get(installationId);
      const state = this.hostState(installationId);
      if (!spec) {
        let failure;
        try { await this.stopInstance(installationId); }
        catch (error) { failure = safeError(error, 'SERVICE_ERROR'); }
        try { if (state) await this.host.uninstall(installationId); }
        catch (error) { failure ??= safeError(error, 'SERVICE_ERROR'); }
        this.applied.delete(installationId); this.failures.delete(installationId);
        this.sessions.delete(installationId); this.snapshots.delete(installationId);
        this.settled.delete(installationId);
        if (![...this.workers].some(worker => worker.installationId === installationId &&
          (worker.worker || worker.live || worker.adapterTasks.size))) {
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
      if (old && (state ? state.enabled === spec.enabled : !spec.enabled) &&
        (!spec.enabled || state?.state === 'ready') && sameExecution(old, spec) &&
        !this.failures.has(installationId) && state?.state !== 'failed') {
        this.applied.set(installationId, spec);
        return;
      }
      await this.applyChange({ spec, existing: Boolean(state), signal });
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
  async applyChange({ spec, existing, signal }) {
    requireValue(!signal?.aborted, 'CANCELLED');
    const id = spec.installationId;
    this.settled.delete(id);
    this.candidateSpecs.set(id, spec);
    try {
      // Refresh/restart use the current file after the old owner exits. They
      // do not have the source rollback transaction used by an explicit update.
      await this.stopInstance(id);
      requireValue(!signal?.aborted, 'CANCELLED');
      if (!spec.enabled) {
        this.failures.delete(id);
        return;
      }
      const source = decodeSource(this.readSource(spec.sourceHash));
      if (existing) await this.host.update(id, { source,
        config: spec.config, version: spec.version }, { signal });
      else await this.host.install({ ...spec, source, protocol: 'cat', enabled: false }, { signal });
      await this.host.enable(id, { signal });
      const sites = this.hostSites(id);
      if (!same(sites, spec.sites)) {
        const next = this.settings.save(value => ({ ...value, scripts: value.scripts.map(script => {
          if (script.installationId !== id) return script;
          requireValue(sameExecution(script, spec), 'STORAGE_CONFLICT');
          return { ...script, sites };
        }) }));
        spec.sites = sites;
        this.settings.publish(next);
        this.publishSettingsSnapshot(next);
      }
      this.failures.delete(id);
    } catch (error) { this.failures.set(id, safeError(error).code); }
    finally {
      this.publishCandidate(id);
      this.candidateSpecs.delete(id);
      this.settled.add(id);
      this.applied.set(id, spec);
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
      if (!sameExecution(previousSettings.get(id), diskScripts.get(id))) {
        this.cancelManagement(id, fault('STALE_SESSION'));
        this.captureInitialization(id)();
      }
    }
    // Publishing settings also updates gateway/worker snapshots. It does not
    // establish that a queued installation change has run.
    const ids = [...new Set([...this.applied.keys(), ...previousSettings.keys(), ...diskScripts.keys()])]
      .filter(id => {
        const spec = diskScripts.get(id);
        if (!same(this.applied.get(id), spec) || this.failures.has(id)) return true;
        const state = this.hostState(id);
        return state?.state === 'failed' || state && state.enabled !== spec?.enabled ||
          spec?.enabled && state?.state !== 'ready';
      });
    await this.configureGateway();
    requireValue(!signal?.aborted, 'CANCELLED');
    const results = await Promise.allSettled(ids.map(id => this.manage(id, signal, async task => {
      await this.apply(this.settings.read(), { only: new Set([id]), signal: task.scope.signal });
      task.scope.assertOpen();
    })));
    requireValue(!signal?.aborted, 'CANCELLED');
    const failed = results.find(result => result.status === 'rejected');
    if (failed) throw failed.reason;
    return this.applyResult();
  }

  hostSites(installationId) {
    return Object.entries(this.hostState(installationId)?.capabilities ?? {})
      .map(([key, value]) => ({ key, ...value }));
  }
  async stopWorker(worker) {
    let failure;
    const proxyClosing = this.drainProxies(worker, fault('STALE_SESSION')).catch(error => {
      failure ??= error;
    });
    try { await worker.stop(); } catch (error) { failure ??= error; }
    try {
      await worker.drainAdapters();
      if (worker.adapterTasks.size) {
        const draining = new ResourceScope(undefined, worker.limits.cleanupTimeoutMs);
        try { await draining.race(worker.whenAdaptersIdle()); }
        finally { draining.close(); }
      }
      requireValue(!worker.worker && !worker.live && !worker.adapterTasks.size, 'SERVICE_ERROR');
    } catch (error) { failure ??= error; }
    await proxyClosing;
    if (!worker.worker && !worker.live && !worker.adapterTasks.size) this.workers.delete(worker);
    if (failure) throw failure;
  }
  async drainProxies(session, reason = fault('HOST_CLOSED')) {
    const tasks = [...this.proxyTasks].filter(task => !session || task.session === session);
    for (const task of tasks) task.scope.close(reason);
    await Promise.allSettled(tasks.map(task => task.done));
    const failure = session ? this.proxyCloseFailures.get(session) : this.proxyCloseFailure;
    if (failure) throw failure;
  }
  async stopInstance(installationId) {
    let failure;
    if (!this.closed && this.hostState(installationId)) {
      try { await this.host.disable(installationId); }
      catch (error) { failure = error; }
    }
    // Worker failure can detach the public session before its thread exits.
    // Keep that owner until termination is confirmed before sharing its volume.
    for (const worker of this.workers) {
      if (worker.installationId !== installationId) continue;
      try { await this.stopWorker(worker); } catch (error) { failure ??= error; }
    }
    if (failure) throw failure;
  }
  cancelManagement(installationId, reason = fault('CANCELLED')) {
    for (const task of this.managementTasks) {
      if (task.installationId !== installationId) continue;
      task.restore = false;
      task.scope.close(reason);
      task.recovery?.close(reason);
    }
  }
  async manage(installationId, signal, body, replacement, preflight) {
    identifier(installationId);
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    await this.start();
    requireValue(!signal?.aborted, 'CANCELLED');
    const scope = new ResourceScope();
    const cancel = () => scope.close(fault('CANCELLED'));
    const previous = [...this.managementTasks].filter(task => task.installationId === installationId);
    const stop = this.captureInitialization(installationId);
    const preserveRecovery = replacement === 'CANCELLED';
    const preempt = () => {
      scope.assertOpen();
      if (!replacement) return;
      for (const task of previous) {
        if (!this.managementTasks.has(task)) continue;
        if (!preserveRecovery) task.restore = false;
        task.scope.close(fault(replacement));
        if (!preserveRecovery) task.recovery?.close(fault(replacement));
      }
      // Queued disable/uninstall has not committed a new enabled intent.
      // The candidate's own signal cancels it; its independent rollback must finish.
      if (!preserveRecovery || !this.replacements.has(installationId)) stop();
    };
    const task = { installationId, scope, restore: true };
    this.managementTasks.add(task);
    try {
      signal?.addEventListener('abort', cancel, { once: true });
      scope.assertOpen();
      // Reserve this installation's transaction immediately. Source preflight
      // may run while its predecessor initializes, but only valid replacement
      // input cancels the captured predecessor; a later owner is never selected.
      const prepared = preflight ? Promise.resolve().then(() => preflight(task)).then(value => {
        preempt(); return { value };
      }).catch(error => ({ error })) : null;
      if (prepared) {
        this.pending.add(prepared);
        prepared.finally(() => this.pending.delete(prepared)).catch(() => {});
      } else preempt();
      return await this.serial(installationId, async () => {
        if (!prepared) scope.assertOpen();
        return body(task, prepared ? await prepared : undefined);
      });
    } finally {
      this.managementTasks.delete(task);
      signal?.removeEventListener('abort', cancel);
      scope.close();
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
    // A metadata commit cannot acknowledge execution changes awaiting refresh.
    if (sameExecution(this.applied.get(spec.installationId), spec))
      this.applied.set(spec.installationId, spec);
    this.publishSettingsSnapshot(next);
    if (failure) throw failure;
    return next;
  }
  sourceSpec(proposal, previous) {
    if (proposal.name !== undefined) catName(proposal.name);
    requireValue(proposal.enabled === undefined || typeof proposal.enabled === 'boolean');
    requireValue(proposal.version === undefined || typeof proposal.version === 'string' &&
      proposal.version.length > 0 && proposal.version.length <= 128);
    requireValue(proposal.sourceType === undefined || ['local', 'network'].includes(proposal.sourceType));
    const spec = { installationId: proposal.installationId,
      config: proposal.config ?? previous?.config ?? {},
      version: proposal.version ?? previous?.version ?? '0.0.0',
      enabled: proposal.enabled ?? previous?.enabled ?? true,
      name: catName(proposal.name ?? previous?.name ?? proposal.installationId),
      sourceType: proposal.sourceType ?? previous?.sourceType ?? 'local',
      sourceUrl: proposal.sourceUrl === undefined ? previous?.sourceUrl ?? null : proposal.sourceUrl,
      importedAt: previous?.importedAt ?? null, updatedAt: previous?.updatedAt ?? null,
      updateState: previous?.updateState ?? 'idle', updateError: previous?.updateError ?? null,
      sites: previous?.sites ?? [],
    };
    if (spec.sourceType === 'network') catSourceURL(spec.sourceUrl);
    else requireValue(spec.sourceUrl === null);
    return spec;
  }
  validateSource(source, contentHash) {
    requireValue(typeof source === 'string');
    requireValue(contentHash === undefined || typeof contentHash === 'string' && /^[a-f0-9]{64}$/.test(contentHash));
    const bytes = Buffer.from(source);
    requireValue(bytes.length <= this.limit('maxSourceBytes'), 'RESOURCE_LIMIT');
    decodeSource(bytes);
    try { parse(source, { ecmaVersion: 'latest', allowReturnOutsideFunction: true }); }
    catch { throw fault('SCRIPT_EXCEPTION'); }
    return bytes;
  }
  async install(input, { signal } = {}) {
    const proposal = clone(input);
    identifier(proposal.installationId);
    requireValue(typeof proposal.source === 'string');
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    if (proposal.config !== undefined) proposal.config = normalizeCatConfig(proposal.config);
    await this.start();
    this.sourceSpec(proposal, this.settings.read().scripts.find(script =>
      script.installationId === proposal.installationId));
    const bytes = this.validateSource(proposal.source, proposal.contentHash);
    return this.manage(proposal.installationId, signal,
      task => this.replaceSource(proposal, task, { bytes }), 'STALE_SESSION');
  }
  async import(input, { signal } = {}) {
    const proposal = clone(input);
    catName(proposal.name); catSourceURL(proposal.sourceUrl);
    requireValue(proposal.enabled === undefined || typeof proposal.enabled === 'boolean');
    return this.manage(proposal.installationId, signal, (task, prepared) => {
      task.scope.assertOpen();
      if (prepared.error) throw prepared.error;
      return this.replaceSource({ ...proposal, sourceType: 'network', source: prepared.value.source },
        task, { bytes: prepared.value.bytes });
    }, 'STALE_SESSION', async task => {
      const bytes = await this.downloadSource(proposal.sourceUrl, task.scope.signal);
      try {
        const source = decodeSource(bytes);
        return { source, bytes: this.validateSource(source) };
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
  async update({ installationId, source, contentHash }, { signal } = {}) {
    // A runtime buffer is decoded here so encoding failures are visible in the
    // installation's durable update result as well as the RPC error envelope.
    identifier(installationId);
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    requireValue(contentHash === undefined || typeof contentHash === 'string' && /^[a-f0-9]{64}$/.test(contentHash));
    const bytes = Buffer.isBuffer(source) ? Buffer.from(source) : null;
    await this.start();
    const accepted = this.installedSpec(installationId);
    requireValue(accepted.sourceType === 'local'
      ? typeof source === 'string' || bytes : source === undefined && contentHash === undefined);
    try {
      return await this.manage(installationId, signal, async (task, prepared) => {
        const previous = this.installedSpec(installationId);
        try {
          task.scope.assertOpen();
          if (prepared.error) throw prepared.error;
          requireValue(previous.sourceType === 'local'
            ? typeof source === 'string' || bytes : source === undefined);
          return await this.replaceSource({ installationId, source: prepared.value.source, contentHash }, task,
            { update: true, bytes: prepared.value.bytes });
        } catch (error) {
          // A post-rename failure may already have committed the candidate.
          // Never turn that accepted version into a failed/rolled-back update.
          const current = this.installedSpec(installationId);
          if (same(current, previous)) this.saveSpec(current, { ...current,
            updateState: 'failed', updateError: task.scope.signal.aborted
              ? task.scope.reason.code : safeError(error, 'STORAGE_ERROR').code });
          throw task.scope.signal.aborted ? task.scope.reason : error;
        }
      }, 'STALE_SESSION', async task => {
        const current = this.installedSpec(installationId);
        requireValue(current.sourceType === 'local'
          ? typeof source === 'string' || bytes : source === undefined);
        const downloaded = current.sourceType === 'network'
          ? await this.downloadSource(current.sourceUrl, task.scope.signal) : null;
        try {
          const text = downloaded ? decodeSource(downloaded) : bytes ? decodeSource(bytes) : source;
          return { source: text, bytes: this.validateSource(text, contentHash) };
        } finally { downloaded?.fill(0); }
      });
    } finally { bytes?.fill(0); }
  }
  async replaceSource(proposal, task, { update = false, bytes } = {}) {
    const { installationId, source } = proposal;
    const current = this.settings.read();
    const previous = current.scripts.find(script => script.installationId === installationId);
    const spec = this.sourceSpec(proposal, previous);
    bytes ??= this.validateSource(source, proposal.contentHash);
    spec.sourceHash = proposal.contentHash ?? hash(bytes);
    task.scope.assertOpen();
    const previousBytes = previous && existsSync(this.sourcePath(previous.sourceHash))
      ? this.readSource(previous.sourceHash) : null;
    let previousSource = this.sessions.get(installationId)?.revision.source ?? null;
    if (previousSource === null && previousBytes) {
      try { previousSource = decodeSource(previousBytes); } catch { /* An invalid old file can still be replaced. */ }
    }
    const changed = Boolean(previous && previousSource !== source);
    if (update && !changed && (!spec.enabled || this.hostState(installationId)?.state === 'ready')) {
      this.saveSpec(previous, { ...previous, updateState: 'unchanged', updateError: null });
      return this.instance(installationId);
    }
    const file = this.sourcePath(spec.sourceHash);
    const replacedBytes = existsSync(file) ? this.readSource(spec.sourceHash) : null;
    let touchedSource = false, committed = false, failure;
    this.replacements.add(installationId);
    this.candidateSpecs.set(installationId, spec);
    try {
      await this.stopInstance(installationId);
      task.scope.assertOpen();
      if (!previous) removePrivateVolume(this.dataRoot, installationId);
      if (this.hostState(installationId)) await this.host.update(installationId, { source,
        contentHash: spec.sourceHash, config: spec.config, version: spec.version }, { signal: task.scope.signal });
      else await this.host.install({ ...spec, source, contentHash: spec.sourceHash, protocol: 'cat', enabled: false }, { signal: task.scope.signal });
      task.scope.assertOpen();
      if (spec.enabled) {
        await this.host.enable(installationId, { signal: task.scope.signal });
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
        if (previous) this.candidateSpecs.set(installationId, previous);
        else this.candidateSpecs.delete(installationId);
        try {
          await this.stopInstance(installationId);
          if (touchedSource) {
            if (replacedBytes && !this.readSource(spec.sourceHash).equals(replacedBytes)) atomicWrite(file, replacedBytes);
            else if (!replacedBytes && !this.settings.read().scripts.some(script => script.sourceHash === spec.sourceHash))
              this.deleteSource(spec.sourceHash);
          }
          if (!this.closed) {
            if (previous && previousSource !== null) {
              if (this.hostState(installationId)) await this.host.update(installationId, {
                source: previousSource, config: previous.config, version: previous.version });
              else await this.host.install({ ...previous, source: previousSource, protocol: 'cat', enabled: false });
              if (previous.enabled && task.restore) {
                const recovery = task.recovery = new ResourceScope();
                try { await this.host.enable(installationId, { signal: recovery.signal }); }
                finally { task.recovery = null; recovery.close(); }
              }
              this.failures.delete(installationId);
            } else {
              if (this.hostState(installationId)) await this.host.uninstall(installationId);
              if (previous?.enabled) this.failures.set(installationId, 'CORRUPT_STORAGE');
            }
          }
          if (!previous) removePrivateVolume(this.dataRoot, installationId);
        } catch (recoveryError) { this.failures.set(installationId, safeError(recoveryError, 'STORAGE_ERROR').code); }
        if (previous) this.applied.set(installationId, previous);
      }
      failure = task.scope.signal.aborted ? task.scope.reason : error;
    } finally {
      if (committed) {
        this.applied.set(installationId, spec);
        this.failures.delete(installationId);
      }
      this.settled.add(installationId);
      this.replacements.delete(installationId);
      this.candidateSpecs.delete(installationId);
      this.publishCandidate(installationId);
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
    identifier(installationId);
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    await this.start();
    requireValue(this.installedSpec(installationId).enabled, 'DISABLED');
    return this.manage(installationId, signal, async task => {
      const spec = this.installedSpec(installationId);
      requireValue(spec.enabled, 'DISABLED');
      await this.applyChange({ spec, existing: !!this.hostState(installationId), signal: task.scope.signal });
      task.scope.assertOpen();
      this.settings.publish(this.settings.read());
      if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
      return this.instance(installationId);
    }, 'STALE_SESSION');
  }
  async setEnabled(installationId, enabled, { signal } = {}) {
    identifier(installationId); requireValue(typeof enabled === 'boolean');
    return this.manage(installationId, signal, async task => {
      requireValue(this.settings.read().scripts.some(script => script.installationId === installationId), 'NOT_FOUND');
      let next, failure;
      try { next = this.settings.save(value => ({ ...value, scripts: value.scripts.map(script =>
        script.installationId === installationId ? { ...script, enabled } : script) })); }
      catch (error) {
        if (error.code !== 'COMMIT_UNKNOWN') throw error;
        next = this.settings.read(); failure = error;
      }
      await this.apply(next, { only: new Set([installationId]), signal: task.scope.signal });
      task.scope.assertOpen();
      if (this.failures.has(installationId)) throw fault(this.failures.get(installationId));
      if (failure) throw failure;
      return this.instance(installationId);
    }, enabled ? undefined : 'CANCELLED');
  }
  async uninstall(installationId, { signal } = {}) {
    return this.manage(installationId, signal, async task => {
      let next, failure;
      try { next = this.settings.save(value => ({ ...value,
        scripts: value.scripts.filter(script => script.installationId !== installationId) })); }
      catch (error) {
        if (error.code !== 'COMMIT_UNKNOWN') throw error;
        next = this.settings.read(); failure = error;
      }
      const result = await this.apply(next, { only: new Set([installationId]), signal: task.scope.signal });
      if (failure) throw failure;
      return result;
    }, 'CANCELLED');
  }
  starting(installationId, state = this.hostState(installationId),
    snapshot = this.snapshots.get(installationId)) {
    if (this.replacements.has(installationId)) return true;
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
    const status = this.replacements.has(installationId) ? 'starting'
      : failure ? 'failed' : spec?.enabled === false ? 'disabled'
      : this.starting(installationId, state, snapshot) ? 'starting'
        : !state ? 'disabled'
          : !state.enabled ? 'disabled' : state.state;
    return { installationId, generation: state?.generation ?? 0, state: status,
      name: spec?.name ?? installationId, sourceType: spec?.sourceType ?? 'local',
      sourceUrl: spec?.sourceUrl ?? null, enabled: spec?.enabled ?? false,
      importedAt: spec?.importedAt ?? null, updatedAt: spec?.updatedAt ?? null,
      updateState: spec?.updateState ?? 'idle', updateError: spec?.updateError ?? null,
      version: status === 'ready' ? state.version : spec?.version ?? state?.version ?? '0.0.0',
      sourceHash: status === 'starting' ? spec?.sourceHash ?? null
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
  proxyTarget(url, parts) {
    const [_, installationId, generation, id] = parts;
    const entry = this.proxies.get(id), snapshot = this.snapshots.get(installationId);
    if (!entry || !snapshot || entry.session.installationId !== installationId ||
      String(entry.session.generation) !== generation ||
      entry.session !== this.sessions.get(installationId) || !entry.session.live ||
      entry.session.stopped || !entry.session.worker)
      return null;
    const prefix = `/proxy/${installationId}/${generation}/${id}`;
    if (url.pathname !== prefix && !url.pathname.startsWith(prefix + '/')) return null;
    return { entry, snapshot, path: url.pathname.slice(prefix.length) || '/' };
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
    // Both successful and failed receipts must still belong to this session.
    try { result = await this.host.invoke(target, options); }
    finally { assertCurrent(); }
    const owned = Boolean(result && typeof result === 'object' && result[ownedExtension]);
    // Ownership is trusted control data, never part of the public JSON value.
    if (result && typeof result === 'object') delete result[ownedExtension];
    // Film address resolution owns only a receipt; manifest I/O begins when its
    // media consumer reads that receipt. Ordinary Cat callers retain enrichment.
    if (options?.mediaPurpose === 'receipt') return result;
    if (!result || typeof result !== 'object' || typeof result.url !== 'string' ||
      !/\.m3u8(?:\?|$)/i.test(result.url) ||
      result._meow && !owned) return result;
    // Optional metadata enhancement is never a playback/provider parser.
    try {
      result = await session.request('enhanceM3U8', {
        value: result, owned,
      }, { signal, timeoutMs: 3000 });
    } catch (error) {
      if (error?.code === 'CANCELLED') throw error;
    } finally { assertCurrent(); }
    if (result && typeof result === 'object') delete result[ownedExtension];
    return result;
  }
  async management(installationId, generation, { signal } = {}) {
    await this.start();
    return this.getSession(installationId, generation).request('management.detect', {},
      { signal, phase: 'invocation' });
  }
  async sync(installationId, generation, input, { signal } = {}) {
    await this.start();
    return this.getSession(installationId, generation).request('management.sync', { input },
      { signal, phase: 'invocation' });
  }
  async status() {
    await this.start();
    return this.statusSnapshot();
  }
  statusSnapshot() {
    return { settingsPath: this.settings.path, aggregate: {
      origin: `http://127.0.0.1:${this.server.address().port}` },
      instances: [...new Set([...this.settings.value.scripts.map(spec => spec.installationId),
        ...this.replacements])].map(id => this.instance(id)) };
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
        server.listen(port, ready);
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
    response.setHeader('Access-Control-Allow-Origin', request.headers.origin ?? '*');
    response.setHeader('Access-Control-Allow-Credentials', 'true');
    response.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, POST, OPTIONS');
    response.setHeader('Access-Control-Allow-Headers', request.headers['access-control-request-headers'] ?? '*');
    response.setHeader('Access-Control-Expose-Headers', '*');
    response.setHeader('Vary', 'Origin');
    if (request.headers['access-control-request-private-network'] === 'true') {
      response.setHeader('Access-Control-Allow-Private-Network', 'true');
    }
    if (request.method === 'OPTIONS') { response.writeHead(204); response.end(); return; }
    const origin = `http://127.0.0.1:${this.server.address().port}`;
    const url = new URL(request.url, origin), parts = url.pathname.split('/').filter(Boolean);
    if (parts[0] === 'proxy') return this.forwardProxy(request, response, url, parts);
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
    requireValue(!this.closed, 'HOST_CLOSED');
    let target = this.proxyTarget(url, parts);
    requireValue(target, 'STALE_SESSION');
    if (!['GET', 'HEAD'].includes(request.method)) {
      response.writeHead(405, { allow: 'GET, HEAD, OPTIONS', 'content-length': 0 });
      response.end();
      return;
    }
    const manifest = resolveProxyManifest(target.entry, target.path, url.search);
    if (manifest !== undefined) {
      const next = new URL(manifest.url);
      const mapped = this.mapSnapshotProxy(target.entry.session, target.snapshot, next.href);
      if (mapped === next.href) {
        target = { ...target, upstreamURL: next, hlsVariables: manifest.hlsVariables };
      } else {
        const mappedURL = new URL(mapped);
        const resolved = this.proxyTarget(mappedURL, mappedURL.pathname.split('/').filter(Boolean));
        requireValue(resolved, 'STALE_SESSION');
        target = { ...resolved, url: mappedURL, hlsVariables: manifest.hlsVariables };
      }
    }
    const { entry } = target;
    const task = { session: entry.session, scope: new ResourceScope(entry.session.proxyLifecycle.signal) };
    task.done = Promise.resolve().then(() => this.streamProxy(request, response, target, task.scope))
      .finally(() => this.proxyTasks.delete(task));
    this.proxyTasks.add(task);
    return task.done;
  }
  async streamProxy(request, response, { entry, snapshot, path, url: resolvedURL, upstreamURL, hlsVariables }, scope) {
    const url = resolvedURL ?? new URL(request.url, `http://127.0.0.1:${this.aggregatePort}`);
    const target = upstreamURL ?? new URL(entry.origin);
    if (!upstreamURL) { target.pathname = path; target.search = url.search; }
    const control = new ResourceScope(scope.signal, this.limit('requestTimeoutMs'));
    const resources = new CatProxyResources(this.limit('cleanupTimeoutMs'));
    const socketAssigned = socket => resources.track(socket);
    let client, upstream, clearHeaders, controlActive = true;
    const abort = () => scope.close(fault('CANCELLED'));
    const responseClosed = () => { if (!response.writableFinished) abort(); };
    let requestListening = false, responseListening = false;
    let failure;
    try {
      const proxyOrigin = new URL(`http://${request.headers.host ?? `127.0.0.1:${this.aggregatePort}`}`).origin;
      const map = value => this.mapSnapshotProxy(entry.session, snapshot, value, proxyOrigin);
      const headers = cleanHopByHop(request.headers);
      deleteHeader(headers, 'x-meow-request-scope');
      // Replace only the aggregate transport's default Host. An explicitly
      // supplied virtual Host belongs to this media request and stays intact.
      const caller = new URL(request.url, `http://127.0.0.1:${this.aggregatePort}`);
      if (headers.host?.toLowerCase() === caller.host.toLowerCase()) deleteHeader(headers, 'host');
      headers['accept-encoding'] = 'identity';
      response.once('close', responseClosed);
      responseListening = true;
      request.once('aborted', abort);
      requestListening = true;
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
          let failure;
          try { client.off('response', responded); } catch (error) { failure ??= error; }
          try { client.off('error', failed); } catch (error) { failure ??= error; }
          try { client.off('close', closed); } catch (error) { failure ??= error; }
          return failure;
        };
        clearHeaders = cleanup;
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
      for (const name of Object.keys(outHeaders)) if (name.startsWith('access-control-')) delete outHeaders[name];
      outHeaders.vary = [outHeaders.vary, 'Origin'].filter(Boolean).join(', ');
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
        const text = await control.race(readProxyPlaylist(input.stream, upstream.headers));
        control.close();
        controlActive = false;
        const body = proxyPlaylistBody(text, target, input.format, entry, map, hlsVariables);
        response.writeHead(upstream.statusCode, outHeaders);
        await scope.race(pipeline(Readable.from(body, { objectMode: false }),
          response, { signal: scope.signal }));
      }
    } catch (error) { failure = scope.reason ?? (controlActive ? control.reason : null) ?? error; }
    try {
      const cleanupFailure = clearHeaders?.();
      failure ??= cleanupFailure;
    } catch (error) { failure ??= error; }
    try { if (responseListening) response.off('close', responseClosed); } catch (error) { failure ??= error; }
    try { if (requestListening) request.off('aborted', abort); } catch (error) { failure ??= error; }
    control.close();
    scope.close();
    try { await resources.close(); }
    catch (error) {
      const cleanupFailure = safeError(error, 'SERVICE_ERROR');
      this.proxyCloseFailures.set(entry.session, cleanupFailure);
      this.proxyCloseFailure ??= cleanupFailure;
      failure = cleanupFailure;
    }
    try { client?.off('socket', socketAssigned); } catch (error) { failure ??= error; }
    if (failure) throw failure;
  }
  async close() {
    if (this.closing) return this.closing;
    this.closed = true;
    this.closing = (async () => {
      let failure;
      const proxyClosing = this.drainProxies().catch(error => { failure ??= error; });
      for (const task of this.managementTasks) {
        task.restore = false;
        task.scope.close(fault('HOST_CLOSED'));
        task.recovery?.close(fault('HOST_CLOSED'));
      }
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
      for (const worker of this.workers) {
        try { await this.stopWorker(worker); }
        catch (error) { failure ??= safeError(error, 'SERVICE_ERROR'); }
      }
      await proxyClosing;
      this.sessions.clear();
      this.candidates.clear();
      this.candidateSpecs.clear(); this.replacements.clear();
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
