import { isUtf8 } from 'node:buffer';
import { createHash } from 'node:crypto';
import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { isDeepStrictEqual } from 'node:util';
import { readFileSync, existsSync, mkdirSync, statSync, rmSync } from 'node:fs';
import { resolve, join, isAbsolute } from 'node:path';
import { parse } from 'acorn';
import { ScriptHost } from './host.js';
import { WorkerSession, workerResult } from './worker-session.js';
import { CatStorage, catConfig, catConfigNodes } from './cat-storage.js';
import { CatNativeSettings } from './cat-native-settings.js';
import { createSourceDownload } from './source-download.js';
import { atomicWrite, removePrivateVolume } from './private-volume.js';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';
import { ResourceScope } from './scope.js';
import { ownedExtension, panProviders } from './cat-output.js';
import { isLoopbackHost } from './network-boundary.js';
import { closeResource } from './resource-close.js';
import { listenCatServer, resolveCatProxyTarget, sweepCatProxies } from './cat-server.js';

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const same = isDeepStrictEqual;
const execution = spec => spec && [spec.sourceHash, catConfigNodes(spec), spec.version, spec.enabled, spec.inputIdentity];
const sameExecution = (a, b) => same(execution(a), execution(b));
const sameProviders = (a, b) => panProviders.every(provider =>
  (a?.[provider] === true) === (b?.[provider] === true));

function catSourceURL(value) {
  requireValue(typeof value === 'string' && value.trim().length > 0);
  let url;
  try { url = new URL(value); } catch { throw fault('INVALID_ARGUMENT'); }
  requireValue(['http:', 'https:'].includes(url.protocol));
  return value;
}
function catName(value) {
  requireValue(typeof value === 'string' && value.trim().length > 0);
  return value.trim();
}
function jsonShape(value) {
  try { return cloneJSONValue(value); }
  catch { throw fault('INVALID_ARGUMENT'); }
}
function normalizeCatConfig(input) {
  const value = jsonShape(input);
  requireValue(value && typeof value === 'object' && !Array.isArray(value));
  return value;
}
function createStandaloneCatSettings(dataRoot) {
  const storage = new CatStorage(dataRoot, 'standalone');
  const store = {
    value: storage.value,
    ready: storage.ready.then(value => { store.value = value; }),
    read: () => cloneJSONValue(storage.value),
    reload: async () => { await storage.reload(); return cloneJSONValue(storage.value); },
    publish: value => { store.value = cloneJSONValue(value); },
    save: change => storage.save(value => jsonShape(change(cloneJSONValue(value)))),
    close: () => storage.close(),
  };
  return store;
}
function decodeSource(bytes) {
  try {
    requireValue(isUtf8(bytes));
    const source = bytes.toString('utf8');
    requireValue(!source.startsWith('\uFEFF') && source.trim().length > 0);
    return source;
  } catch { throw fault('SCRIPT_PROTOCOL_ERROR'); }
}

async function downloadCatSource(url, { signal, timeoutMs, debug } = {}) {
  catSourceURL(url);
  const scope = new ResourceScope(signal, timeoutMs);
  try {
    scope.assertOpen();
    const response = await createSourceDownload({
      debug, debugCategory: 'film-catalog', debugSub: 'cat.download',
    })({
      url, options: {}, signal: scope.signal, validateURL: catSourceURL,
    });
    scope.assertOpen();
    requireValue(response.statusCode >= 200 && response.statusCode < 300, 'HTTP_ERROR');
    return response.body;
  } finally { scope.close(); }
}

/** Shared execution owner. A platform may supply native source intent; actual
 * source files, installation facts, sandbox and routing remain in this package. */
export class CatRuntime {
  constructor({ dataRoot, network, onDiagnostic, onScriptError, onNetwork, limits, configuration, debug } = {}) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot));
    this.productRoot = resolve(dataRoot);
    this.dataRoot = join(this.productRoot, 'cat');
    this.network = network; this.onDiagnostic = onDiagnostic; this.onScriptError = onScriptError;
    this.configuration = configuration;
    this.onNetwork = onNetwork; this.limits = limits;
    this.debug = debug ?? null;
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
    this.accountSync = new WeakMap(); this.accountTasks = new Set();
    this.accountSavedListeners = new Set();
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
        this.settings = this.configuration ? new CatNativeSettings(this.productRoot, this.configuration)
          : createStandaloneCatSettings(this.productRoot);
        await this.settings.ready;
        this.host = new ScriptHost({ adapters: {
          cat: input => this.workerSession(input),
        }, network: this.network, onDiagnostic: this.onDiagnostic,
        onScriptError: this.onScriptError, limits: this.limits, debug: this.debug });
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
      debug: this.debug,
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
        const result = cloneJSONValue(value?.result);
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
        this.debug?.emit({
          level: 'debug',
          cat: 'film-catalog',
          sub: 'cat.network',
          event: 'worker.network',
          data: value,
        });
        try { this.onNetwork?.(value); } catch { /* Observation cannot alter networking. */ }
      },
      onStopped: session => {
        this.debug?.emit({
          level: session.failure ? 'warn' : 'info',
          cat: 'runtime-node',
          sub: 'cat.worker',
          event: 'worker.stopped',
          data: {
            installationId: session.installationId,
            generation: session.generation,
            exitCode: session.exitCode ?? null,
            unresponsive: Boolean(session.unresponsive),
          },
          error: session.failure ?? null,
        });
        if (this.candidates.get(session.installationId) === session)
          this.candidates.delete(session.installationId);
        if (!session.worker && !session.live && !session.adapterTasks.size) {
          this.workers.delete(session);
        }
        if (this.sessions.get(session.installationId) !== session) return;
        this.sessions.delete(session.installationId);
        this.snapshots.delete(session.installationId);
        this.sweepProxies();
      },
    });
    this.candidates.set(input.installationId, candidate);
    this.workers.add(candidate);
    this.debug?.emit({
      level: 'info',
      cat: 'runtime-node',
      sub: 'cat.worker',
      event: 'worker.created',
      data: { installationId: input.installationId, generation: input.generation, totalWorkers: this.workers.size },
    });
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
    this.scheduleAccountSync(candidate);
  }
  connectAccounts(accounts) {
    requireValue(!this.accounts && typeof accounts?.read === 'function' &&
      typeof accounts.subscribe === 'function' && Array.isArray(accounts.providers));
    this.accounts = accounts;
    this.releaseAccounts = accounts.subscribe(provider => {
      for (const session of this.sessions.values()) this.scheduleAccountSync(session, [provider]);
    });
    for (const session of this.sessions.values()) this.scheduleAccountSync(session);
  }
  onAccountSaved(listener) {
    this.accountSavedListeners.add(listener);
    return () => this.accountSavedListeners.delete(listener);
  }
  scheduleAccountSync(session, providers = this.accounts?.providers) {
    if (!this.accounts || !providers || this.closed) return;
    for (const provider of providers)
      this.queueAccountSync(session, { provider }, { automatic: true }).catch(() => {});
  }
  queueAccountSync(session, { provider, credentials }, { signal, automatic = false } = {}) {
    requireValue(this.accounts || credentials !== null && typeof credentials === 'object' && !Array.isArray(credentials));
    const input = this.accounts ? null : cloneJSONValue(credentials);
    provider = provider === '189' ? 'tianyi' : provider === '139' ? 'yidong' : provider;
    let state = this.accountSync.get(session);
    if (!state) {
      state = { tail: Promise.resolve(), records: new Map() };
      this.accountSync.set(session, state);
    }
    const record = { provider, status: 'pending' };
    state.records.set(provider, record);
    const scope = new ResourceScope();
    let started = false;
    const failed = error => {
      if (state.records.get(provider) !== record) return;
      record.status = 'failed'; record.errorCode = safeError(error).code;
    };
    if (signal) {
      const abort = () => scope.close(safeError(signal.reason, 'CANCELLED'));
      if (signal.aborted) abort();
      else {
        signal.addEventListener('abort', abort, { once: true });
        scope.add(() => signal.removeEventListener('abort', abort));
      }
    }
    const assertCurrent = () => {
      requireValue(!this.closed, 'HOST_CLOSED');
      requireValue(session.live && this.sessions.get(session.installationId) === session, 'STALE_SESSION');
    };
    const work = state.tail.then(async () => {
      scope.assertOpen();
      assertCurrent();
      let current = input;
      if (this.accounts) {
        if (!this.accounts.providers.includes(provider)) {
          record.status = 'unsupported';
          return { status: 'unsupported' };
        }
        const account = await scope.race(this.accounts.read(provider));
        scope.assertOpen();
        assertCurrent();
        record.revision = account.revision; record.rowRevision = account.rowRevision;
        // App-connected writes always read the sole account owner at execution
        // time. Caller credentials cannot become a second account fact.
        current = account.credentials;
        if (account.revision === 0) {
          if (automatic && state.records.get(provider) === record) state.records.delete(provider);
          else record.status = 'unsupported';
          return { status: 'unsupported' };
        }
      }
      let value;
      try {
        started = true;
        value = await session.request('management.sync', { input: {
          provider, credentials: current,
        } }, { signal: scope.signal, phase: 'invocation', awaitCancelled: true });
      } catch (error) {
        // A timed-out transport does not prove that its guest PUT handler has
        // stopped. End that execution and its volume ownership before a later
        // queued save can run; ordinary failed receipts remain usable.
        if (['TIMEOUT', 'NETWORK_ERROR', 'SERVICE_CLOSED', 'SERVICE_ERROR'].includes(error?.code)) {
          this.failures.set(session.installationId, safeError(error).code);
          try { await this.stopWorker(session); }
          finally { this.settled.add(session.installationId); }
        }
        throw error;
      }
      assertCurrent();
      if (value.status === 'saved') this.publishAccountSaved(session, state, record);
      record.status = value.status === 'saved' ? 'saved' : 'unsupported';
      delete record.errorCode;
      return value;
    }).catch(error => {
      failed(error);
      throw error;
    });
    const result = Promise.resolve().then(() => scope.race(work)).catch(error => {
      if (!started) failed(error);
      throw error;
    });
    state.tail = work.catch(() => {});
    this.accountTasks.add(work);
    work.finally(() => this.accountTasks.delete(work)).catch(() => {});
    work.finally(() => scope.close()).catch(() => {});
    return result;
  }
  publishAccountSaved(session, state, record) {
    for (const listener of this.accountSavedListeners) {
      // Revocation is synchronous; its physical drain can include an invoke
      // waiting for this tail and therefore has independent lifetime ownership.
      const drain = Promise.resolve(listener({ installationId: session.installationId,
        generation: session.generation, provider: record.provider })).catch(error => {
        if (state.records.get(record.provider) === record) {
          record.status = 'failed'; record.errorCode = safeError(error).code;
        }
      });
      this.accountTasks.add(drain);
      drain.finally(() => this.accountTasks.delete(drain)).catch(() => {});
    }
  }
  async waitAccountSync(session, signal) {
    const scope = new ResourceScope(signal);
    try {
      for (;;) {
        const tail = this.accountSync.get(session)?.tail;
        if (!tail) return;
        await scope.race(tail);
        if (tail === this.accountSync.get(session)?.tail) return;
      }
    } catch (error) {
      if (signal?.aborted) throw fault('CANCELLED');
      throw error;
    } finally { scope.close(); }
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
  workerSettings(_installationId, next = this.settings.value) {
    return cloneJSONValue({ providers: next.providers, mappings: next.mappings });
  }
  sessionRoutingMatches(session, next = this.settings.value) {
    return !!session && sameProviders(session.input.startParams.settings.value.providers, next.providers);
  }
  publishSettingsSnapshot(next) {
    const aggregate = { port: this.aggregatePort };
    const sessions = new Set([...(this.host?.initializingSessions?.() ?? []),
      ...this.sessions.values(), ...this.candidates.values()]);
    for (const session of sessions) {
      // Guest caches contain responses produced by this execution mode. Retire
      // that execution before publishing another mode, rather than changing
      // the meaning of its cached tokens and file IDs in place.
      if (!this.sessionRoutingMatches(session, next)) {
        this.settled.delete(session.installationId);
        if (!session.stopRequested) {
          const stopping = session.stop(fault('STALE_SESSION'));
          this.pending.add(stopping);
          stopping.finally(() => this.pending.delete(stopping)).catch(() => {});
        }
        continue;
      }
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
    requireValue(typeof sourceHash === 'string' && /^[a-f0-9]{64}$/.test(sourceHash), 'CORRUPT_STORAGE');
    return join(this.sourcesRoot, sourceHash + '.js');
  }
  readSource(sourceHash) {
    try {
      const file = this.sourcePath(sourceHash);
      const state = statSync(file);
      requireValue(state.isFile(), 'CORRUPT_STORAGE');
      const bytes = readFileSync(file);
      if (this.configuration) requireValue(hash(bytes) === sourceHash, 'CORRUPT_STORAGE');
      return bytes;
    } catch (error) { throw safeError(error, 'CORRUPT_STORAGE'); }
  }
  deleteSource(sourceHash) {
    try {
      requireValue(/^[a-f0-9]{64}$/.test(sourceHash), 'CORRUPT_STORAGE');
      if (this.configuration && this.settings.read().scripts.some(script => script.localHash === sourceHash)) return;
      const file = this.sourcePath(sourceHash);
      rmSync(file, { maxRetries: 3, retryDelay: 100 });
    } catch (error) {
      if (error.code === 'ENOENT') return;
      throw safeError(error, 'STORAGE_ERROR');
    }
  }
  async apply(next, { background = false, only = null, signal } = {}) {
    requireValue(!signal?.aborted, 'CANCELLED');
    const identities = new Set();
    for (const spec of next.scripts) {
      identifier(spec?.installationId);
      requireValue(!identities.has(spec.installationId), 'CORRUPT_STORAGE');
      identities.add(spec.installationId);
    }
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
          await this.settings.prune?.(installationId);
        }
        if (failure) throw failure;
        return;
      }
      if (old && (state ? state.enabled === spec.enabled : !spec.enabled) &&
        (!spec.enabled || state?.state === 'ready' && this.sessions.get(installationId)?.live &&
          this.sessionRoutingMatches(this.sessions.get(installationId))) && sameExecution(old, spec) &&
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
    if (this.configuration && spec.enabled && (spec.sourceHash === null ||
      spec.loadedIdentity !== spec.inputIdentity || !existsSync(this.sourcePath(spec.sourceHash)))) {
      return this.applyConfiguredSource(spec, signal);
    }
    const id = spec.installationId;
    this.settled.delete(id);
    this.candidateSpecs.set(id, spec);
    try {
      requireValue(typeof spec.enabled === 'boolean', 'CORRUPT_STORAGE');
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
        config: catConfig(spec), version: spec.version }, { signal });
      else await this.host.install({ ...spec, config: catConfig(spec), source, protocol: 'cat', enabled: false }, { signal });
      await this.host.enable(id, { signal });
      const sites = this.hostSites(id);
      if (!same(sites, spec.sites)) {
        const next = await this.settings.save(value => ({ ...value, scripts: value.scripts.map(script => {
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
  configuredSource(spec, signal) {
    requireValue(Number.isSafeInteger(spec.inputRevision) && spec.inputRevision > 0, 'CORRUPT_STORAGE');
    if (spec.sourceType === 'network') return this.downloadSource(catSourceURL(spec.sourceUrl), signal);
    requireValue(spec.sourceType === 'local', 'CORRUPT_STORAGE');
    requireValue(typeof spec.localHash === 'string' && /^[a-f0-9]{64}$/.test(spec.localHash), 'CORRUPT_STORAGE');
    if (spec.localPath === null) return Promise.resolve(this.readSource(spec.localHash));
    try {
      requireValue(typeof spec.localPath === 'string' && isAbsolute(spec.localPath) && !/[\u0000-\u001f]/.test(spec.localPath), 'CORRUPT_STORAGE');
      const state = statSync(spec.localPath);
      requireValue(state.isFile(), 'CORRUPT_STORAGE');
      const bytes = readFileSync(spec.localPath);
      if (hash(bytes) !== spec.localHash) throw fault('STORAGE_CONFLICT');
      return Promise.resolve(bytes);
    } catch (error) { return Promise.reject(safeError(error, 'CORRUPT_STORAGE')); }
  }
  async applyConfiguredSource(spec, signal) {
    const id = spec.installationId, scope = new ResourceScope(signal);
    const task = { installationId: id, scope, restore: false };
    this.managementTasks.add(task);
    this.settled.delete(id);
    try {
      await this.stopInstance(id);
      scope.assertOpen();
      const bytes = await this.configuredSource(spec, scope.signal);
      const source = decodeSource(bytes);
      this.validateSource(source);
      await this.settings.reload();
      scope.assertOpen();
      requireValue(sameExecution(this.settings.read().scripts.find(value => value.installationId === id), spec),
        'STALE_SESSION');
      await this.replaceSource({ installationId: id, source }, task, { bytes });
      this.settings.publish(this.settings.read());
      this.failures.delete(id);
    } catch (error) {
      await this.settings.reload();
      const current = this.settings.read().scripts.find(value => value.installationId === id);
      if (current?.inputIdentity === spec.inputIdentity && current.enabled === spec.enabled) {
        const code = safeError(error).code;
        await this.saveSpec(current, { ...current, updateState: 'failed', updateError: code });
        this.failures.set(id, code);
        this.applied.set(id, current);
        this.settled.add(id);
      }
    } finally {
      this.managementTasks.delete(task);
      scope.close();
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
  async refresh({ signal, onSettingsPublished } = {}) {
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    await this.start();
    requireValue(!signal?.aborted, 'CANCELLED');
    const diskSettings = await this.settings.reload();
    requireValue(!signal?.aborted, 'CANCELLED');
    const previousSettings = new Map(this.settings.value.scripts.map(script =>
      [script.installationId, script]));
    const diskScripts = new Map(diskSettings.scripts.map(script => [script.installationId, script]));
    for (const id of new Set([...previousSettings.keys(), ...diskScripts.keys()])) {
      if (!sameExecution(previousSettings.get(id), diskScripts.get(id))) {
        this.cancelManagement(id, fault('STALE_SESSION'));
        this.captureInitialization(id)();
        if (this.configuration) this.settled.delete(id);
      }
    }
    // Publishing settings also updates gateway/worker snapshots. It does not
    // establish that a queued installation change has run.
    await this.configureGateway();
    requireValue(!signal?.aborted, 'CANCELLED');
    const ids = [...new Set([...this.applied.keys(), ...previousSettings.keys(), ...diskScripts.keys()])]
      .filter(id => {
        const spec = diskScripts.get(id);
        if (!same(this.applied.get(id), spec) || this.failures.has(id)) return true;
        const state = this.hostState(id);
        return state?.state === 'failed' || state && state.enabled !== spec?.enabled ||
          spec?.enabled && (state?.state !== 'ready' || !this.sessions.get(id)?.live ||
            !this.sessionRoutingMatches(this.sessions.get(id)));
      });
    onSettingsPublished?.();
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
      if (worker.adapterTasks.size) {
        const draining = new ResourceScope(undefined, worker.limits.cleanupTimeoutMs);
        try { await draining.race(worker.whenAdaptersIdle()); }
        finally { draining.close(); }
      }
      requireValue(!worker.worker && !worker.live && !worker.adapterTasks.size, 'SERVICE_ERROR');
    } catch (error) { failure ??= error; }
    await proxyClosing;
    if (!worker.worker && !worker.live && !worker.adapterTasks.size) {
      this.workers.delete(worker);
      this.debug?.emit({
        level: failure ? 'warn' : 'info',
        cat: 'runtime-node',
        sub: 'cat.worker',
        event: 'worker.removed',
        data: {
          installationId: worker.installationId,
          generation: worker.generation,
          remainingWorkers: this.workers.size,
        },
        error: failure ?? null,
      });
    }
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
  async saveSpec(previous, spec) {
    let next, failure;
    try {
      next = await this.settings.save(value => {
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
      proposal.version.length > 0);
    requireValue(proposal.sourceType === undefined || ['local', 'network'].includes(proposal.sourceType));
    const spec = { ...previous, installationId: proposal.installationId,
      ...(proposal.config !== undefined ? { config: proposal.config }
        : previous?.configNodes && previous.config === undefined ? { configNodes: previous.configNodes }
        : { config: previous?.config ?? {} }),
      version: proposal.version ?? previous?.version ?? '0.0.0',
      enabled: proposal.enabled ?? previous?.enabled ?? true,
      name: catName(proposal.name ?? previous?.name ?? proposal.installationId),
      sourceType: proposal.sourceType ?? previous?.sourceType ?? 'local',
      sourceUrl: proposal.sourceUrl === undefined ? previous?.sourceUrl ?? null : proposal.sourceUrl,
      importedAt: previous?.importedAt ?? null, updatedAt: previous?.updatedAt ?? null,
      updateState: previous?.updateState ?? 'idle', updateError: previous?.updateError ?? null,
      sites: previous?.sites ?? [],
      ...(this.configuration ? { localPath: previous.localPath, localHash: previous.localHash,
        inputRevision: previous.inputRevision, inputIdentity: previous.inputIdentity,
        loadedIdentity: previous.inputIdentity } : {}),
    };
    if (spec.sourceType === 'network') catSourceURL(spec.sourceUrl);
    else requireValue(spec.sourceUrl === null);
    return spec;
  }
  validateSource(source, contentHash) {
    requireValue(typeof source === 'string');
    requireValue(contentHash === undefined || typeof contentHash === 'string' && /^[a-f0-9]{64}$/.test(contentHash));
    const bytes = Buffer.from(source);
    decodeSource(bytes);
    try { parse(source, { ecmaVersion: 'latest', allowReturnOutsideFunction: true }); }
    catch { throw fault('SCRIPT_EXCEPTION'); }
    return bytes;
  }
  async install(input, { signal } = {}) {
    const proposal = clone({ ...input, config: undefined });
    if (input.config !== undefined) proposal.config = cloneJSONValue(input.config);
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
    const proposal = clone({ ...input, config: undefined });
    if (input.config !== undefined) proposal.config = cloneJSONValue(input.config);
    catName(proposal.name); catSourceURL(proposal.sourceUrl);
    requireValue(proposal.enabled === undefined || typeof proposal.enabled === 'boolean');
    return this.manage(proposal.installationId, signal, (task, prepared) => {
      task.scope.assertOpen();
      if (prepared.error) throw prepared.error;
      return this.replaceSource({ ...proposal, sourceType: 'network', source: prepared.value.source },
        task, { bytes: prepared.value.bytes });
    }, 'STALE_SESSION', async task => {
      const bytes = await this.downloadSource(proposal.sourceUrl, task.scope.signal);
      const source = decodeSource(bytes);
      return { source, bytes: this.validateSource(source) };
    });
  }
  downloadSource(sourceUrl, signal) {
    return downloadCatSource(sourceUrl, { signal, timeoutMs: this.limit('requestTimeoutMs') });
  }
  async edit(input, { signal } = {}) {
    const { installationId, name, sourceUrl } = clone(input);
    const displayName = catName(name);
    if (sourceUrl !== undefined) catSourceURL(sourceUrl);
    return this.manage(installationId, signal, async task => {
      const previous = this.installedSpec(installationId);
      requireValue(sourceUrl === undefined || previous.sourceType === 'network');
      task.scope.assertOpen();
      await this.saveSpec(previous, { ...previous, name: displayName,
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
    return this.manage(installationId, signal, async (task, prepared) => {
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
        if (same(current, previous)) await this.saveSpec(current, { ...current,
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
      const text = downloaded ? decodeSource(downloaded) : bytes ? decodeSource(bytes) : source;
      return { source: text, bytes: this.validateSource(text, contentHash) };
    });
  }
  async replaceSource(proposal, task, { update = false, bytes } = {}) {
    const { installationId, source } = proposal;
    const current = this.settings.read();
    const previous = current.scripts.find(script => script.installationId === installationId);
    const spec = this.sourceSpec(proposal, previous);
    bytes ??= this.validateSource(source, proposal.contentHash);
    spec.sourceHash = proposal.contentHash ?? hash(bytes);
    task.scope.assertOpen();
    const previousBytes = previous?.sourceHash && existsSync(this.sourcePath(previous.sourceHash))
      ? this.readSource(previous.sourceHash) : null;
    let previousSource = this.sessions.get(installationId)?.revision.source ?? null;
    if (previousSource === null && previousBytes) {
      try { previousSource = decodeSource(previousBytes); } catch { /* An invalid old file can still be replaced. */ }
    }
    const changed = Boolean(previous?.sourceHash && previousSource !== source);
    if (update && !changed && (!spec.enabled || this.hostState(installationId)?.state === 'ready')) {
      await this.saveSpec(previous, { ...previous, updateState: 'unchanged', updateError: null });
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
        contentHash: spec.sourceHash, config: catConfig(spec), version: spec.version }, { signal: task.scope.signal });
      else await this.host.install({ ...spec, config: catConfig(spec), source, contentHash: spec.sourceHash, protocol: 'cat', enabled: false }, { signal: task.scope.signal });
      task.scope.assertOpen();
      if (spec.enabled) {
        await this.host.enable(installationId, { signal: task.scope.signal });
        spec.sites = this.hostSites(installationId);
      }
      task.scope.assertOpen();
      requireValue(!this.closed, 'HOST_CLOSED');
      const now = Date.now();
      if (!previous?.sourceHash) spec.importedAt = now;
      if (changed) spec.updatedAt = now;
      spec.updateState = previous?.sourceHash ? changed ? 'updated' : 'unchanged' : 'idle';
      spec.updateError = null;
      if (this.configuration) {
        await this.settings.reload();
        task.scope.assertOpen();
        requireValue(same(this.settings.read().scripts.find(script => script.installationId === installationId), previous),
          'STALE_SESSION');
      }
      // The candidate has passed activation (or syntax validation if disabled).
      // Until this point both the old source and its description remain durable.
      touchedSource = true;
      atomicWrite(file, bytes);
      try { await this.saveSpec(previous, spec); }
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
          if (!this.closed && (!this.configuration || task.restore)) {
            if (previous && previousSource !== null) {
              if (this.hostState(installationId)) await this.host.update(installationId, {
                source: previousSource, config: catConfig(previous), version: previous.version });
              else await this.host.install({ ...previous, config: catConfig(previous), source: previousSource, protocol: 'cat', enabled: false });
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
      if (!committed && !previous && !this.failures.has(installationId))
        this.failures.set(installationId, safeError(failure, 'STORAGE_ERROR').code);
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
      if (committed && previous?.sourceHash && previous.sourceHash !== spec.sourceHash &&
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
  async updateConfigured(installationId, { signal } = {}) {
    requireValue(this.configuration, 'PERMISSION_DENIED');
    await this.start();
    await this.settings.reload();
    const identity = this.installedSpec(installationId).inputIdentity;
    return this.manage(installationId, signal, async (task, prepared) => {
      try {
        task.scope.assertOpen();
        if (prepared.error) throw prepared.error;
        const current = this.installedSpec(installationId);
        requireValue(current.inputIdentity === identity && current.inputIdentity === prepared.value.identity, 'STALE_SESSION');
        return await this.replaceSource({ installationId, source: prepared.value.source }, task,
          { bytes: prepared.value.bytes, update: true });
      } catch (error) {
        const latest = this.settings.read().scripts.find(spec => spec.installationId === installationId);
        if (latest?.inputIdentity === identity)
          await this.saveSpec(latest, { ...latest, updateState: 'failed', updateError: safeError(error).code });
        throw error;
      }
    }, 'STALE_SESSION', async task => {
      const current = this.installedSpec(installationId);
      requireValue(current.inputIdentity === identity, 'STALE_SESSION');
      const bytes = await this.configuredSource(current, task.scope.signal);
      const source = decodeSource(bytes);
      return { identity: current.inputIdentity, source, bytes: this.validateSource(source) };
    });
  }
  async setEnabled(installationId, enabled, { signal } = {}) {
    identifier(installationId); requireValue(typeof enabled === 'boolean');
    return this.manage(installationId, signal, async task => {
      requireValue(this.settings.read().scripts.some(script => script.installationId === installationId), 'NOT_FOUND');
      let next, failure;
      try { next = await this.settings.save(value => ({ ...value, scripts: value.scripts.map(script =>
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
      try { next = await this.settings.save(value => ({ ...value,
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
    const session = this.sessions.get(installationId);
    if (session && (!session.live || !this.sessionRoutingMatches(session))) return true;
    if (this.configuration && spec.loadedIdentity !== spec.inputIdentity) return true;
    if (!this.settled.has(installationId) || !state) return true;
    if (!state.enabled) return false;
    return state.state === 'ready' && !snapshot;
  }
  instance(installationId) {
    const spec = this.settings.value.scripts.find(script =>
      script.installationId === installationId);
    const state = this.hostState(installationId), snapshot = this.snapshots.get(installationId);
    const failure = this.failures.get(installationId);
    const sync = this.accountSync.get(this.sessions.get(installationId));
    const accountSync = sync ? [...sync.records.values()].map(record => ({ ...record })) : null;
    const status = this.replacements.has(installationId) ? 'starting'
      : failure ? 'failed' : spec?.enabled === false ? 'disabled'
      : this.starting(installationId, state, snapshot) ? 'starting'
        : !state ? 'disabled'
          : !state.enabled ? 'disabled' : state.state;
    return { installationId, generation: status === 'ready' ? state.generation : state?.generation ?? 0, state: status,
      name: spec?.name ?? installationId, sourceType: spec?.sourceType ?? 'local',
      sourceUrl: spec?.sourceUrl ?? null, enabled: spec?.enabled ?? false,
      importedAt: spec?.importedAt ?? null, updatedAt: spec?.updatedAt ?? null,
      updateState: spec?.updateState ?? 'idle', updateError: spec?.updateError ?? null,
      version: status === 'ready' ? state.version : spec?.version ?? state?.version ?? '0.0.0',
      sourceHash: status === 'starting' ? spec?.sourceHash ?? null
        : state?.contentHash ?? spec?.sourceHash ?? null,
      error: failure ?? state?.lastFailure ?? accountSync?.find(record => record.status === 'failed')?.errorCode ?? null,
      ...(accountSync ? { accountSync } : {}),
      sites: cloneJSONValue(status === 'ready' ? this.hostSites(installationId) : spec?.sites ?? []),
      ...(this.configuration ? { inputRevision: spec?.inputRevision ?? 0 } : {}),
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
  proxyTarget(url, parts) { return resolveCatProxyTarget(this, url, parts); }
  async invoke(target, options) {
    const signal = options?.signal;
    await this.start();
    const session = this.getSession(target.installationId, target.generation);
    await this.waitAccountSync(session, signal);
    const assertCurrent = () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      requireValue(!this.closed, 'HOST_CLOSED');
      requireValue(session.live && this.sessions.get(session.installationId) === session, 'STALE_SESSION');
      requireValue(this.sessionRoutingMatches(session), 'STALE_SESSION');
    };
    assertCurrent();
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
    return this.queueAccountSync(this.getSession(installationId, generation), input, { signal });
  }
  async status() {
    await this.start();
    return this.statusSnapshot();
  }
  statusSnapshot() {
    return { aggregate: {
      origin: `http://127.0.0.1:${this.server.address().port}` },
      routing: this.workerSettings(null),
      instances: [...new Set([...this.settings.value.scripts.map(spec => spec.installationId),
        ...this.replacements])].map(id => this.instance(id)) };
  }
  listen(port) { return listenCatServer(this, port); }
  sweepProxies(session) { sweepCatProxies(this, session); }
  diagnostics() {
    const liveWorkers = [...this.workers].filter(worker => worker.live).length;
    return {
      closed: this.closed,
      installations: this.sessions.size,
      totalWorkers: this.workers.size,
      liveWorkers,
      readyWorkers: this.host?.list().filter(entry => entry.state === 'ready').length ?? 0,
      activeProxies: this.proxies.size,
      pendingRequests: this.pending.size,
      failures: this.failures.size,
      host: this.host?.stats?.() ?? null,
    };
  }
  async close() {
    if (this.closing) return this.closing;
    this.closed = true;
    this.releaseAccounts?.();
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
      await Promise.allSettled([this.initialization, this.gatewayPending, ...this.pending, ...this.accountTasks]);
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
      this.accountSavedListeners.clear(); this.accounts = null;
      this.transactions.clear(); this.pending.clear();
      this.initialization = null;
      try { await this.settings?.close(); } catch (error) { failure ??= error; }
      this.host = null; this.settings = null;
      this.network = null; this.onDiagnostic = null; this.onScriptError = null; this.onNetwork = null;
      if (failure) throw failure;
    })();
    return this.closing;
  }
}

export const createCatRuntime = options => new CatRuntime(options);
