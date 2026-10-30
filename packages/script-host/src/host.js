import { createHash } from 'node:crypto';
import { clone, fault, identifier, requireValue, safeError } from './errors.js';

const defaults = Object.freeze({
  syncTimeoutMs: 200,
  initTimeoutMs: 5_000,
  requestTimeoutMs: 15_000,
  cleanupTimeoutMs: 100,
  maxBytes: 2 * 1024 * 1024,
  maxSourceBytes: 4 * 1024 * 1024,
  maxTotalSourceBytes: 256 * 1024 * 1024,
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

function revision(spec, base, maxSourceBytes) {
  requireValue(spec && typeof spec === 'object' && !Array.isArray(spec));
  const source = spec.source ?? base?.source;
  const version = spec.version ?? base?.version ?? '0.0.0';
  const protocol = spec.protocol ?? base?.protocol ?? 'lx';
  requireValue(typeof source === 'string' && source.trim().length > 0 &&
    Buffer.byteLength(source) <= maxSourceBytes);
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
  requireValue(Buffer.byteLength(json) <= maxSourceBytes, 'RESOURCE_LIMIT');
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

  constructor({ adapters, network, storage, onDiagnostic, limits = {}, ...unsupported }) {
    requireValue(Object.keys(unsupported).length === 0);
    requireValue(adapters && typeof adapters === 'object');
    requireValue(network === undefined || typeof network === 'function');
    requireValue(storage === undefined || (storage && typeof storage === 'object'));
    requireValue(onDiagnostic === undefined || typeof onDiagnostic === 'function');
    requireValue(Object.keys(limits).every(k => Object.hasOwn(defaults, k)));
    this.limits = Object.freeze({ ...defaults, ...limits });
    for (const value of Object.values(this.limits)) requireValue(Number.isSafeInteger(value) && value > 0);
    this.adapters = { ...adapters };
    this.network = network;
    this.storage = storage;
    this.onDiagnostic = onDiagnostic;
  }

  #open() { requireValue(!this.#closed, 'HOST_CLOSED'); }
  #sourceBudget(next) {
    const revisions = new Set([next]);
    for (const record of this.#records.values()) {
      revisions.add(record.revision);
      if (record.previous) revisions.add(record.previous);
    }
    for (const session of [...this.#sessions, ...this.#retired]) revisions.add(session.revision);
    requireValue([...revisions].reduce((total, item) => total + Buffer.byteLength(item.source), 0) <=
      this.limits.maxTotalSourceBytes, 'RESOURCE_LIMIT');
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
  async #start(id, rev) {
    requireValue(this.#sessions.size + this.#retired.size < this.limits.maxInstances * 2, 'RESOURCE_LIMIT');
    const factory = this.adapters[rev.protocol];
    requireValue(typeof factory === 'function', 'UNSUPPORTED_PROTOCOL');
    const generation = (this.#generations.get(id) ?? 0) + 1;
    this.#generations.set(id, generation);
    const session = factory({
      installationId: id, generation, revision: rev,
      network: this.network, storage: this.storage, onDiagnostic: this.onDiagnostic, limits: this.limits,
    });
    this.#sessions.add(session);
    try {
      await session.start();
      this.#open();
      session.activate();
      return session;
    } catch (e) {
      await this.#stop(session);
      throw safeError(e);
    }
  }
  async #stop(session) {
    if (!session) return;
    await session.stop();
    await session.drainAdapters();
    const cleanupError = session.stats().cleanupError;
    if (cleanupError) {
      this.#cleanupIssues.set(JSON.stringify([session.installationId, session.generation]), {
        installationId: session.installationId, generation: session.generation, code: cleanupError,
      });
      if (this.#cleanupIssues.size > 32) this.#cleanupIssues.delete(this.#cleanupIssues.keys().next().value);
    }
    this.#sessions.delete(session);
    if (session.stats().pendingAdapterTasks) {
      this.#retired.add(session);
      session.whenAdaptersIdle().then(() => this.#retired.delete(session));
    }
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
    const rev = revision(spec, null, this.limits.maxSourceBytes);
    requireValue(Object.hasOwn(this.adapters, rev.protocol), 'UNSUPPORTED_PROTOCOL');
    requireValue(spec.enabled === undefined || typeof spec.enabled === 'boolean');
    return this.#locked(id, async () => {
      requireValue(!this.#records.has(id), 'ALREADY_EXISTS');
      this.#sourceBudget(rev);
      requireValue(this.#records.size < this.limits.maxInstances, 'RESOURCE_LIMIT');
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
        await this.#stop(record.session);
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
      await this.#stop(record.session);
      record.session = null;
      return this.#snapshot(record);
    });
  }

  async #replace(record, next) {
    this.#sourceBudget(next);
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
      const next = revision(proposal, record.revision, this.limits.maxSourceBytes);
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
      await this.#stop(record.session);
      this.#records.delete(id);
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
    identifier(target.siteId);
    identifier(target.operation);
    if (timeoutMs !== undefined) {
      requireValue(Number.isSafeInteger(timeoutMs) && timeoutMs > 0 &&
        timeoutMs <= this.limits.requestTimeoutMs);
    }
    const record = this.#target(target);
    const release = this.#reserveRequest(record.id, target.siteId);
    try {
      return await record.session.invoke({
        siteId: target.siteId, operation: target.operation, payload: clone(target.payload ?? {}),
      }, { signal, timeoutMs });
    } finally { release(); }
  }

  #reserveRequest(id, siteId) {
    this.#open();
    const instanceCount = [...this.#pending.values()].filter(v => v.id === id).length;
    const siteCount = [...this.#pending.values()]
      .filter(v => v.id === id && v.siteId === siteId).length;
    requireValue(this.#pending.size < this.limits.maxInFlight &&
      instanceCount < this.limits.maxInstanceInFlight && siteCount < this.limits.maxSiteInFlight,
    'RESOURCE_LIMIT');
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
      pendingAdapterTasks: [...this.#sessions, ...this.#retired]
        .reduce((sum, s) => sum + s.stats().pendingAdapterTasks, 0),
      cleanupIssues: [...this.#cleanupIssues.values()].map(clone),
    };
  }

  // Only retired work is cleanup debt. Live work from another installation
  // must not make a successful install/update falsely report cleanup failure.
  cleanupPending(installationId) {
    identifier(installationId);
    return [...this.#retired].filter(s => s.installationId === installationId)
      .reduce((sum, s) => sum + s.stats().pendingAdapterTasks, 0);
  }

  async close() {
    this.#closed = true;
    for (const record of this.#records.values()) record.enabled = false;
    await Promise.all([...this.#sessions].map(s => this.#stop(s)));
    await Promise.allSettled([...this.#locks.values()]);
    return { ...this.stats(),
      cleanupComplete: this.stats().pendingAdapterTasks === 0 && this.#cleanupIssues.size === 0 };
  }
}
