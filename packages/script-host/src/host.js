import { createHash } from 'node:crypto';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';

/** Source and stack contracts shared by every protocol. */
export const SCRIPT_LIMITS = Object.freeze({
  maxSourceBytes: 16 * 1024 * 1024,
  maxStackBytes: 256 * 1024,
});

/** Per-protocol wait/init/cleanup deadlines. No synchronous execution deadline. */
const protocolLimits = Object.freeze({
  lx: Object.freeze({
    initTimeoutMs: 5_000,
    requestTimeoutMs: 60_000,
    cleanupTimeoutMs: 100,
  }),
  cat: Object.freeze({
    initTimeoutMs: 60_000,
    requestTimeoutMs: 60_000,
    cleanupTimeoutMs: 3_000,
  }),
  legado: Object.freeze({
    initTimeoutMs: 60_000,
    requestTimeoutMs: 60_000,
    cleanupTimeoutMs: 3_000,
  }),
  qysg: Object.freeze({
    initTimeoutMs: 60_000,
    requestTimeoutMs: 60_000,
    cleanupTimeoutMs: 3_000,
  }),
});

const defaults = Object.freeze({
  ...SCRIPT_LIMITS,
  ...protocolLimits.lx,
});

function permissions(raw = {}) {
  requireValue(raw && typeof raw === 'object' && !Array.isArray(raw));
  requireValue(Object.keys(raw).every(k => k === 'storage'));
  const storage = raw.storage ?? [];
  requireValue(Array.isArray(storage) && storage.every(p => ['read', 'write'].includes(p)));
  return { storage: [...new Set(storage)] };
}

function revision(spec, base, maxSourceBytes = SCRIPT_LIMITS.maxSourceBytes) {
  requireValue(spec && typeof spec === 'object' && !Array.isArray(spec));
  const source = spec.source ?? base?.source;
  const version = spec.version ?? base?.version ?? '0.0.0';
  const protocol = spec.protocol ?? base?.protocol ?? 'lx';
  requireValue(typeof source === 'string' && source.trim().length > 0);
  requireValue(Buffer.byteLength(source, 'utf8') <= maxSourceBytes, 'RESOURCE_LIMIT');
  requireValue(typeof version === 'string' && version.length > 0 && version.length <= 128);
  identifier(protocol);
  const config = clone(spec.config ?? base?.config ?? {});
  const metadata = clone(spec.metadata ?? base?.metadata ?? {});
  const protocolOptions = clone(spec.protocolOptions ?? base?.protocolOptions ?? {});
  requireValue(config && typeof config === 'object' && !Array.isArray(config));
  requireValue(metadata && typeof metadata === 'object' && !Array.isArray(metadata));
  requireValue(protocolOptions && typeof protocolOptions === 'object' && !Array.isArray(protocolOptions));
  // Configuration is a JSON public contract; no functions, cycles or binary handles.
  let json;
  try { json = JSON.stringify(config); } catch { throw fault('INVALID_ARGUMENT'); }
  // Trusted composition can reuse the hash already computed for a complete
  // source upload. This field is not accepted by the public HTTP methods.
  requireValue(spec.contentHash === undefined ||
    typeof spec.contentHash === 'string' && /^[a-f0-9]{64}$/.test(spec.contentHash));
  return {
    source, version, protocol,
    hash: spec.contentHash ?? (source === base?.source ? base.hash
      : createHash('sha256').update(source).digest('hex')),
    config: JSON.parse(json), metadata, protocolOptions,
    permissions: permissions(spec.permissions ?? base?.permissions),
  };
}

/** Generic installation/session manager. Protocol vocabulary is injected. */
export class ScriptHost {
  #records = new Map();
  #generations = new Map();
  #locks = new Map();
  #sessions = new Set();
  #stops = new WeakMap();
  #initializingSessions = new Set();
  #initializingOwners = new Map();
  #retired = new Set();
  #closed = false;
  #pending = new Set();
  #cleanupIssues = new Map();
  #closing;

  constructor({ adapters, network, storage, onDiagnostic, onScriptError, limits = {},
    ...unsupported }) {
    requireValue(Object.keys(unsupported).length === 0);
    requireValue(adapters && typeof adapters === 'object');
    requireValue(network === undefined || typeof network === 'function');
    requireValue(storage === undefined || (storage && typeof storage === 'object'));
    requireValue(onDiagnostic === undefined || typeof onDiagnostic === 'function');
    requireValue(onScriptError === undefined || typeof onScriptError === 'function');
    requireValue(Object.keys(limits).every(k => Object.hasOwn(defaults, k)));
    this.limitOverrides = Object.freeze({ ...limits });
    this.limits = Object.freeze({ ...defaults, ...limits });
    for (const value of Object.values(this.limits)) requireValue(Number.isSafeInteger(value) && value > 0);
    this.adapters = { ...adapters };
    this.network = network;
    this.storage = storage;
    this.onDiagnostic = onDiagnostic;
    this.onScriptError = onScriptError;
  }

  #open() { requireValue(!this.#closed, 'HOST_CLOSED'); }
  #limitsFor(protocol) {
    return Object.freeze({ ...SCRIPT_LIMITS,
      ...protocolLimits.lx, ...(protocolLimits[protocol] ?? {}), ...this.limitOverrides });
  }
  #record(id) {
    identifier(id);
    const record = this.#records.get(id);
    if (!record) throw fault('NOT_FOUND');
    return record;
  }
  #locked(id, operation) {
    this.#open();
    identifier(id);
    const previous = this.#locks.get(id) ?? Promise.resolve();
    const promise = previous.catch(() => {}).then(() => {
      this.#open();
      return operation();
    });
    this.#locks.set(id, promise);
    promise.finally(() => {
      if (this.#locks.get(id) === promise) this.#locks.delete(id);
    }).catch(() => {});
    return promise;
  }
  async #start(id, rev, signal) {
    requireValue(!signal?.aborted, 'CANCELLED');
    const factory = this.adapters[rev.protocol];
    requireValue(typeof factory === 'function', 'UNSUPPORTED_PROTOCOL');
    const generation = (this.#generations.get(id) ?? 0) + 1;
    this.#generations.set(id, generation);
    let session;
    session = factory({
      installationId: id, generation, revision: rev,
      network: this.network, storage: this.storage, onDiagnostic: this.onDiagnostic,
      onScriptError: this.onScriptError, limits: this.#limitsFor(rev.protocol),
      onFailure: failure => this.#sessionFailed(id, session, failure),
      startSignal: signal,
    });
    this.#sessions.add(session);
    this.#initializingSessions.add(session);
    this.#initializingOwners.set(id, session);
    const abort = () => {
      try { Promise.resolve(session.stop(fault('CANCELLED'))).catch(() => {}); }
      catch { /* The owning start/stop path reports cleanup failures. */ }
    };
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) abort();
    const initializing = (async () => {
      await session.start();
      this.#open();
      requireValue(!this.#initializationStopped(session), 'STALE_SESSION');
      await session.activate();
      // A lifecycle caller may have stopped this owner while activation was
      // in flight. Such a late transaction must not publish the session.
      requireValue(!this.#initializationStopped(session), 'STALE_SESSION');
      requireValue(!signal?.aborted, 'CANCELLED');
      return session;
    })();
    initializing.finally(() => {
      this.#initializingSessions.delete(session);
      if (this.#initializingOwners.get(id) === session) this.#initializingOwners.delete(id);
    }).catch(() => {});
    try {
      await initializing;
      return session;
    } catch (e) {
      const failure = signal?.aborted ? fault('CANCELLED')
        : e instanceof Error && e.code ? e : safeError(e);
      try { await this.#stop(session); } catch { /* Preserve the initiating failure. */ }
      throw failure;
    } finally { signal?.removeEventListener('abort', abort); }
  }
  #initializationStopped(session) {
    return !session.live || Boolean(session.stopping) || session.stopRequested === true;
  }
  #stopInitializingFor(id) {
    identifier(id);
    this.#open();
    this.captureInitialization(id)?.();
  }
  // Admission captures the actual owner. Calling this stop later cannot
  // select a replacement that happens to reuse the installation ID.
  captureInitialization(id) {
    identifier(id);
    const session = this.#initializingOwners.get(id);
    if (!session) return null;
    return () => {
      if (!this.#initializingSessions.has(session)) return;
      try { Promise.resolve(session.stop()).catch(() => {}); }
      catch { /* Cleanup errors are reported by the lifecycle caller. */ }
    };
  }
  initializingSessions() {
    return [...this.#initializingSessions];
  }
  stopInitializingSessions(installationId = null) {
    const sessions = installationId === null
      ? [...this.#initializingSessions]
      : [this.#initializingOwners.get(installationId)].filter(Boolean);
    for (const session of sessions) {
      try { Promise.resolve(session.stop()).catch(() => {}); }
      catch { /* Cleanup errors are reported by the lifecycle caller. */ }
    }
    return sessions;
  }
  #sessionFailed(id, session, failure) {
    const record = this.#records.get(id);
    if (!record || record.session !== session) return;
    record.lastFailure = safeError(failure).code;
  }
  #stop(session) {
    if (!session) return Promise.resolve();
    let stopping = this.#stops.get(session);
    if (!stopping) {
      stopping = Promise.resolve().then(() => this.#stopOwned(session));
      this.#stops.set(session, stopping);
    }
    return stopping;
  }
  async #stopOwned(session) {
    let failure;
    try { await session.stop(); } catch (error) { failure ??= safeError(error); }
    try { await session.drainAdapters(); } catch (error) { failure ??= safeError(error); }
    let stats;
    try { stats = session.stats(); } catch (error) {
      failure ??= safeError(error);
      stats = { cleanupError: 'SCRIPT_EXCEPTION', pendingAdapterTasks: 0 };
    }
    const cleanupError = stats.cleanupError;
    if (cleanupError) {
      this.#cleanupIssues.set(JSON.stringify([session.installationId, session.generation]), {
        installationId: session.installationId, generation: session.generation, code: cleanupError,
      });
    }
    let retirement;
    if (stats.pendingAdapterTasks) {
      try {
        const candidate = typeof session.adapterRetirement === 'function'
          ? session.adapterRetirement() : null;
        if (candidate && typeof candidate.pendingAdapterTasks === 'function') {
          retirement = {
            installationId: session.installationId,
            generation: session.generation,
            pendingAdapterTasks: candidate.pendingAdapterTasks,
            idle: Promise.resolve(candidate.idle),
          };
        } else {
          let pending = stats.pendingAdapterTasks;
          retirement = {
            installationId: session.installationId,
            generation: session.generation,
            pendingAdapterTasks: () => pending,
            idle: Promise.resolve(session.whenAdaptersIdle()).finally(() => { pending = 0; }),
          };
        }
      } catch (error) { failure ??= safeError(error); }
    }
    this.#sessions.delete(session);
    if (retirement) {
      this.#retired.add(retirement);
      const hostRef = new WeakRef(this);
      retirement.idle.finally(() => {
        const host = hostRef.deref();
        if (host) host.#retired.delete(retirement);
      }).catch(() => {});
    }
    if (failure) throw failure;
  }
  #snapshot(record) {
    const session = record.session;
    return {
      installationId: record.id, protocol: record.revision.protocol,
      version: record.revision.version, contentHash: record.revision.hash,
      generation: session?.generation ?? this.#generations.get(record.id) ?? 0,
      enabled: record.enabled,
      state: !record.enabled ? 'disabled' : session?.live ? 'ready' : 'failed',
      capabilities: clone(session?.capabilities ?? {}),
      updateAlert: clone(session?.alert ?? null),
      lastFailure: session?.failure?.code ?? record.lastFailure ?? null,
      resources: session?.stats() ??
        { live: false, timers: 0, networkRequests: 0, invocations: 0, pendingAdapterTasks: 0 },
    };
  }

  install(spec, { signal } = {}) {
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    requireValue(spec && typeof spec === 'object');
    const id = identifier(spec.installationId);
    const rev = revision(spec, undefined, this.limits.maxSourceBytes);
    requireValue(Object.hasOwn(this.adapters, rev.protocol), 'UNSUPPORTED_PROTOCOL');
    requireValue(spec.enabled === undefined || typeof spec.enabled === 'boolean');
    return this.#locked(id, async () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      requireValue(!this.#records.has(id), 'ALREADY_EXISTS');
      const record = { id, revision: rev, enabled: false, session: null, previous: null };
      // Import does not execute code by default; enable is an explicit operation.
      this.#records.set(id, record);
      if (spec.enabled) {
        try {
          record.session = await this.#start(id, rev, signal);
          requireValue(!signal?.aborted, 'CANCELLED');
          record.enabled = true;
        }
        catch (e) {
          if (record.session && signal?.aborted) {
            try { await this.#stop(record.session); } catch { /* Preserve cancellation. */ }
            record.session = null;
          }
          record.lastFailure = e instanceof Error && e.code ? e.code : safeError(e).code;
          if (e?.code === 'TIMEOUT') record.enabled = true;
          throw e;
        }
      }
      return this.#snapshot(record);
    });
  }

  enable(id, { signal } = {}) {
    requireValue(signal === undefined || signal instanceof AbortSignal);
    return this.#locked(id, async () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      const record = this.#record(id);
      if (!record.session?.live) {
        const previous = record.session;
        record.session = null;
        record.enabled = false;
        await this.#stop(previous);
        try {
          record.session = await this.#start(id, record.revision, signal);
          requireValue(!signal?.aborted, 'CANCELLED');
          record.enabled = true;
          record.lastFailure = null;
        } catch (e) {
          if (record.session && signal?.aborted) {
            try { await this.#stop(record.session); } catch { /* Preserve cancellation. */ }
            record.session = null;
          }
          record.lastFailure = e instanceof Error && e.code ? e.code : safeError(e).code;
          if (e?.code === 'TIMEOUT') record.enabled = true;
          throw e;
        }
      }
      return this.#snapshot(record);
    });
  }

  disable(id) {
    // Reach the real initialization owner before waiting for its lock.
    this.#stopInitializingFor(id);
    return this.#locked(id, async () => {
      const record = this.#record(id);
      record.enabled = false;
      const session = record.session;
      record.session = null;
      await this.#stop(session);
      return this.#snapshot(record);
    });
  }

  async #replace(record, next, signal) {
    const old = record.session;
    let session = null;
    if (record.enabled) {
      try {
        session = await this.#start(record.id, next, signal);
        requireValue(!signal?.aborted, 'CANCELLED');
      }
      catch (e) {
        if (session) {
          try { await this.#stop(session); } catch { /* Preserve the trial failure. */ }
        }
        record.lastFailure = e instanceof Error && e.code ? e.code : safeError(e).code;
        throw e; // Old live session is never stopped on trial failure.
      }
    }
    record.previous = record.revision;
    record.revision = next;
    record.session = session;
    record.lastFailure = null;
    // Publish the new generation before invalidating old callbacks.
    await this.#stop(old);
    return this.#snapshot(record);
  }

  update(id, spec, { signal } = {}) {
    requireValue(signal === undefined || signal instanceof AbortSignal);
    requireValue(!signal?.aborted, 'CANCELLED');
    // Capture the proposal at call time, not after another lifecycle operation.
    const proposal = clone(spec);
    identifier(id);
    const current = this.#records.get(id);
    if (current) {
      try {
        const next = revision(proposal, current.revision, this.limits.maxSourceBytes);
        requireValue(Object.hasOwn(this.adapters, next.protocol), 'UNSUPPORTED_PROTOCOL');
        if (proposal.source !== undefined) proposal.contentHash = next.hash;
      }
      catch (error) { return Promise.reject(error); }
    }
    this.#stopInitializingFor(id);
    return this.#locked(id, () => {
      requireValue(!signal?.aborted, 'CANCELLED');
      const record = this.#record(id);
      const next = revision(proposal, record.revision, this.limits.maxSourceBytes);
      requireValue(Object.hasOwn(this.adapters, next.protocol), 'UNSUPPORTED_PROTOCOL');
      return this.#replace(record, next, signal);
    });
  }

  configure(id, config) { return this.update(id, { config }); }

  rollback(id) {
    return this.#locked(id, () => {
      const record = this.#record(id);
      requireValue(record.previous, 'ROLLBACK_UNAVAILABLE');
      return this.#replace(record, record.previous);
    });
  }

  uninstall(id) {
    this.#stopInitializingFor(id);
    return this.#locked(id, async () => {
      const record = this.#record(id);
      record.enabled = false;
      const session = record.session;
      record.session = null;
      try { await this.#stop(session); }
      finally { this.#records.delete(id); }
      // Retain the generation counter to prevent ABA after same-ID reinstall.
    });
  }

  get(id) { return this.#snapshot(this.#record(id)); }
  // A composition binding keeps this read-only view until its own commit.
  // Its revision/session never follow #records into a different generation;
  // live state and resource counts still come from the captured real owner.
  observe(id) {
    const record = this.#record(id);
    const { revision, enabled, session, lastFailure } = record;
    const generation = session?.generation ?? this.#generations.get(id) ?? 0;
    return () => ({ ...this.#snapshot({ id, revision, session, lastFailure,
      enabled: record.session === session ? record.enabled : enabled }), generation });
  }
  list() { return [...this.#records.values()].map(r => this.#snapshot(r)); }

  #target(target) {
    this.#open();
    const record = this.#record(target.installationId);
    requireValue(record.enabled && record.session?.live, 'DISABLED');
    if (target.generation !== undefined && target.generation !== record.session.generation) {
      throw fault('STALE_SESSION');
    }
    return record;
  }

  async invoke(target, { signal, timeoutMs } = {}) {
    requireValue(target && typeof target === 'object');
    requireValue(typeof target.siteId === 'string' && target.siteId.length > 0 &&
      target.siteId.length <= 1024 && !/[\u0000-\u001f\u007f]/.test(target.siteId));
    identifier(target.operation);
    const record = this.#target(target);
    if (timeoutMs !== undefined) {
      requireValue(Number.isSafeInteger(timeoutMs) && timeoutMs > 0 &&
        timeoutMs <= this.#limitsFor(record.revision.protocol).requestTimeoutMs);
    }
    const release = this.#reserveRequest();
    try {
      return await record.session.invoke({
        siteId: target.siteId, operation: target.operation, payload: clone(target.payload ?? {}),
      }, { signal, timeoutMs });
    } finally { release(); }
  }

  #reserveRequest() {
    this.#open();
    const token = Symbol();
    this.#pending.add(token);
    return () => this.#pending.delete(token);
  }

  stats() {
    const sessions = [...this.#sessions];
    return {
      installations: this.#records.size,
      liveSessions: sessions.filter(s => s.live).length,
      activeRequests: this.#pending.size,
      timers: sessions.reduce((sum, s) => sum + s.stats().timers, 0),
      networkRequests: sessions.reduce((sum, s) => sum + s.stats().networkRequests, 0),
      pendingAdapterTasks: sessions
        .reduce((sum, s) => sum + s.stats().pendingAdapterTasks, 0) +
        [...this.#retired].reduce((sum, record) => sum + record.pendingAdapterTasks(), 0),
      cleanupIssues: [...this.#cleanupIssues.values()].map(clone),
    };
  }

  // Only retired work is cleanup debt. Live work from another installation
  // must not make a successful install/update falsely report cleanup failure.
  cleanupPending(installationId) {
    identifier(installationId);
    return [...this.#retired].filter(record => record.installationId === installationId)
      .reduce((sum, record) => sum + record.pendingAdapterTasks(), 0);
  }

  close() {
    if (this.#closing) return this.#closing;
    this.#closing = (async () => {
      this.#closed = true;
      for (const record of this.#records.values()) record.enabled = false;
      let failure;
      const initializing = [...this.#initializingSessions];
      for (const session of initializing) {
        try { Promise.resolve(session.stop()).catch(() => {}); }
        catch { /* Reported by the lifecycle stop below. */ }
      }
      const stopAll = async () => {
        const sessions = new Set([...this.#sessions, ...this.#initializingSessions, ...initializing]);
        const results = await Promise.allSettled([...sessions].map(session => this.#stop(session)));
        for (const result of results)
          if (result.status === 'rejected') failure ??= safeError(result.reason);
      };
      await stopAll();
      await Promise.allSettled([...this.#locks.values()]);
      // A lifecycle operation that had already passed #open() can publish a
      // session after the first snapshot. Its lock is now settled, so drain it
      // in the same close operation rather than leaving an unowned session.
      await stopAll();
      this.#records.clear();
      this.#generations.clear();
      this.#initializingOwners.clear();
      this.#pending.clear();
      this.#locks.clear();
      const stats = this.stats();
      const result = { ...stats,
        cleanupComplete: stats.pendingAdapterTasks === 0 && this.#cleanupIssues.size === 0 };
      this.#sessions.clear();
      this.#retired.clear();
      this.#cleanupIssues.clear();
      if (failure) throw failure;
      return result;
    })();
    return this.#closing;
  }
}
