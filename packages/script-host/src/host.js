import { createHash } from 'node:crypto';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';

const defaults = Object.freeze({
  syncTimeoutMs: 200,
  initTimeoutMs: 5_000,
  requestTimeoutMs: 15_000,
  cleanupTimeoutMs: 100,
  maxBytes: 2 * 1024 * 1024,
  maxTimers: 128,
  maxNetworkRequests: 16,
  maxInstances: 32,
  maxInFlight: 32,
  maxInstanceInFlight: 8,
  maxSiteInFlight: 4,
  maxMemoryBytes: 64 * 1024 * 1024,
  maxStackBytes: 64 * 1024,
});

function permissions(raw = {}) {
  requireValue(raw && typeof raw === 'object' && !Array.isArray(raw));
  requireValue(Object.keys(raw).every(k => k === 'storage'));
  const storage = raw.storage ?? [];
  requireValue(Array.isArray(storage) && storage.every(p => ['read', 'write'].includes(p)));
  return { storage: [...new Set(storage)] };
}

function revision(spec, base) {
  requireValue(spec && typeof spec === 'object' && !Array.isArray(spec));
  const source = spec.source ?? base?.source;
  const version = spec.version ?? base?.version ?? '0.0.0';
  const protocol = spec.protocol ?? base?.protocol ?? 'lx';
  requireValue(typeof source === 'string' && source.trim().length > 0);
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
  return {
    source, version, protocol,
    hash: createHash('sha256').update(source).digest('hex'),
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
  #retired = new Set();
  #closed = false;
  #pending = new Map();
  #cleanupIssues = new Map();
  #closing;

  constructor({ adapters, network, storage, onDiagnostic, limits = {},
    lifecycleManagedProtocols = [], ...unsupported }) {
    requireValue(Object.keys(unsupported).length === 0);
    requireValue(adapters && typeof adapters === 'object');
    requireValue(network === undefined || typeof network === 'function');
    requireValue(storage === undefined || (storage && typeof storage === 'object'));
    requireValue(onDiagnostic === undefined || typeof onDiagnostic === 'function');
    requireValue(Array.isArray(lifecycleManagedProtocols) &&
      lifecycleManagedProtocols.every(protocol => typeof protocol === 'string' &&
        Object.hasOwn(adapters, protocol)));
    requireValue(Object.keys(limits).every(k => Object.hasOwn(defaults, k)));
    this.limits = Object.freeze({ ...defaults, ...limits });
    for (const value of Object.values(this.limits)) requireValue(Number.isSafeInteger(value) && value > 0);
    this.adapters = { ...adapters };
    this.network = network;
    this.storage = storage;
    this.onDiagnostic = onDiagnostic;
    this.lifecycleManagedProtocols = new Set(lifecycleManagedProtocols);
  }

  #open() { requireValue(!this.#closed, 'HOST_CLOSED'); }
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
  async #start(id, rev) {
    const lifecycleManaged = this.lifecycleManagedProtocols.has(rev.protocol);
    if (!lifecycleManaged) {
      const active = [...this.#sessions].filter(session => !session.lifecycleManaged).length +
        [...this.#retired].filter(record => !record.lifecycleManaged).length;
      requireValue(active < this.limits.maxInstances * 2, 'RESOURCE_LIMIT');
    }
    const factory = this.adapters[rev.protocol];
    requireValue(typeof factory === 'function', 'UNSUPPORTED_PROTOCOL');
    const generation = (this.#generations.get(id) ?? 0) + 1;
    this.#generations.set(id, generation);
    const session = factory({
      installationId: id, generation, revision: rev,
      network: this.network, storage: this.storage, onDiagnostic: this.onDiagnostic, limits: this.limits,
      lifecycleManaged,
    });
    this.#sessions.add(session);
    try {
      await session.start();
      this.#open();
      session.activate();
      return session;
    } catch (e) {
      const failure = safeError(e);
      try { await this.#stop(session); } catch { /* Preserve the initiating failure. */ }
      throw failure;
    }
  }
  async #stop(session) {
    if (!session) return;
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
            lifecycleManaged: session.lifecycleManaged,
            pendingAdapterTasks: candidate.pendingAdapterTasks,
            idle: Promise.resolve(candidate.idle),
          };
        } else {
          let pending = stats.pendingAdapterTasks;
          retirement = {
            installationId: session.installationId,
            generation: session.generation,
            lifecycleManaged: session.lifecycleManaged,
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

  install(spec) {
    requireValue(spec && typeof spec === 'object');
    const id = identifier(spec.installationId);
    const rev = revision(spec);
    requireValue(Object.hasOwn(this.adapters, rev.protocol), 'UNSUPPORTED_PROTOCOL');
    requireValue(spec.enabled === undefined || typeof spec.enabled === 'boolean');
    return this.#locked(id, async () => {
      requireValue(!this.#records.has(id), 'ALREADY_EXISTS');
      if (!this.lifecycleManagedProtocols.has(rev.protocol)) {
        const installations = [...this.#records.values()]
          .filter(item => !this.lifecycleManagedProtocols.has(item.revision.protocol)).length;
        requireValue(installations < this.limits.maxInstances, 'RESOURCE_LIMIT');
      }
      const record = { id, revision: rev, enabled: false, session: null, previous: null };
      // Import does not execute code by default; enable is an explicit operation.
      this.#records.set(id, record);
      if (spec.enabled) {
        try { record.session = await this.#start(id, rev); record.enabled = true; }
        catch (e) { record.lastFailure = safeError(e).code; throw e; }
      }
      return this.#snapshot(record);
    });
  }

  enable(id) {
    return this.#locked(id, async () => {
      const record = this.#record(id);
      if (!record.session?.live) {
        const previous = record.session;
        record.session = null;
        record.enabled = false;
        await this.#stop(previous);
        try {
          record.session = await this.#start(id, record.revision);
          record.enabled = true;
          record.lastFailure = null;
        } catch (e) { record.lastFailure = safeError(e).code; throw e; }
      }
      return this.#snapshot(record);
    });
  }

  disable(id) {
    return this.#locked(id, async () => {
      const record = this.#record(id);
      record.enabled = false;
      const session = record.session;
      record.session = null;
      await this.#stop(session);
      return this.#snapshot(record);
    });
  }

  async #replace(record, next) {
    if (!this.lifecycleManagedProtocols.has(next.protocol) &&
      this.lifecycleManagedProtocols.has(record.revision.protocol)) {
      const installations = [...this.#records.values()]
        .filter(item => item !== record &&
          !this.lifecycleManagedProtocols.has(item.revision.protocol)).length;
      requireValue(installations < this.limits.maxInstances, 'RESOURCE_LIMIT');
    }
    const old = record.session;
    let session = null;
    if (record.enabled) {
      try { session = await this.#start(record.id, next); }
      catch (e) {
        record.lastFailure = safeError(e).code;
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

  update(id, spec) {
    // Capture the proposal at call time, not after another lifecycle operation.
    const proposal = clone(spec);
    return this.#locked(id, () => {
      const record = this.#record(id);
      const next = revision(proposal, record.revision);
      requireValue(Object.hasOwn(this.adapters, next.protocol), 'UNSUPPORTED_PROTOCOL');
      return this.#replace(record, next);
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
    if (timeoutMs !== undefined) {
      requireValue(Number.isSafeInteger(timeoutMs) && timeoutMs > 0 &&
        timeoutMs <= this.limits.requestTimeoutMs);
    }
    const record = this.#target(target);
    const release = this.#reserveRequest(record.id, target.siteId, !record.session.lifecycleManaged);
    try {
      return await record.session.invoke({
        siteId: target.siteId, operation: target.operation, payload: clone(target.payload ?? {}),
      }, { signal, timeoutMs });
    } finally { release(); }
  }

  #reserveRequest(id, siteId, enforceLimit = true) {
    this.#open();
    if (enforceLimit) {
      const instanceCount = [...this.#pending.values()].filter(v => v.id === id).length;
      const siteCount = [...this.#pending.values()]
        .filter(v => v.id === id && v.siteId === siteId).length;
      requireValue(this.#pending.size < this.limits.maxInFlight &&
        instanceCount < this.limits.maxInstanceInFlight && siteCount < this.limits.maxSiteInFlight,
      'RESOURCE_LIMIT');
    }
    const token = Symbol();
    this.#pending.set(token, { id, siteId });
    return () => this.#pending.delete(token);
  }

  stats() {
    return {
      installations: this.#records.size,
      liveSessions: [...this.#sessions].filter(s => s.live).length,
      activeRequests: this.#pending.size,
      timers: [...this.#sessions].reduce((sum, s) => sum + s.stats().timers, 0),
      networkRequests: [...this.#sessions].reduce((sum, s) => sum + s.stats().networkRequests, 0),
      pendingAdapterTasks: [...this.#sessions]
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
      const stopAll = async () => {
        const results = await Promise.allSettled([...this.#sessions].map(session => this.#stop(session)));
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
