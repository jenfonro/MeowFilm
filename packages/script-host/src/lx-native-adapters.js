import { ResourceScope } from './scope.js';
import { deferred, fault, requireValue, safeError } from './errors.js';
import { lxCapabilities } from './lx-capabilities.js';
import { describeScriptError } from './script-diagnostic.js';

class AdapterTaskTracker {
  constructor() {
    this.tasks = new Set();
    this.waiter = null;
    this.version = 0;
    this.idleCheckScheduled = false;
    this.idleVersion = null;
  }

  track(input) {
    const task = Promise.resolve(input);
    this.tasks.add(task);
    this.version++;
    this.idleVersion = null;
    const settled = () => {
      if (!this.tasks.delete(task)) return;
      this.version++;
      this.idleVersion = null;
      this.scheduleIdleCheck();
    };
    task.then(settled, settled);
    return task;
  }

  whenIdle() {
    this.waiter ??= deferred();
    this.scheduleIdleCheck();
    return this.waiter.promise;
  }

  scheduleIdleCheck() {
    if (this.idleCheckScheduled) return;
    this.idleCheckScheduled = true;
    setImmediate(() => {
      this.idleCheckScheduled = false;
      if (!this.waiter) return;
      if (this.tasks.size) {
        this.idleVersion = null;
        return;
      }
      if (this.idleVersion !== this.version) {
        this.idleVersion = this.version;
        this.scheduleIdleCheck();
        return;
      }
      const waiter = this.waiter;
      this.waiter = null;
      this.idleVersion = null;
      waiter.resolve();
    });
  }
}

/** LX capabilities and physical adapter ownership; no script executes here. */
export class LXAdapterOwner {
  constructor({ installationId, generation, revision, network, storage, onDiagnostic, onScriptError,
    onFailure, limits }) {
    Object.assign(this, { installationId, generation, revision, network, storage, onDiagnostic, onScriptError,
      onFailure, limits });
    this.live = true;
    this.activated = false;
    this.failure = null;
    this.root = new ResourceScope();
    this.invocations = new Map();
    this.networks = new Map();
    this.adapterTracker = new AdapterTaskTracker();
    this.adapterTasks = this.adapterTracker.tasks;
    this.nextId = 0;
    this.capabilities = {};
    this.alert = null;
  }

  assertLive() {
    if (!this.live) throw this.failure ?? fault('STALE_SESSION');
    this.root.assertOpen();
  }

  scriptInfo() {
    const metadata = { name: 'LX script', version: this.revision.version };
    const prefix = this.revision.source.slice(0, 8192);
    const header = prefix.match(/^\s*(\/\*[\s\S]*?\*\/|(?:\/\/[^\n]*\n?)+)/)?.[0] ?? '';
    for (const field of ['name', 'description', 'author', 'homepage', 'version']) {
      const value = header.match(new RegExp(`(?:^|\\n)\\s*(?:\\/\\/|\\/\\*|\\*)?\\s*@${field}\\s+([^\\r\\n]*)`))?.[1];
      if (value) metadata[field] = value.replace(/\*\/\s*$/, '').trim().slice(0, 1024);
    }
    return { ...metadata, ...this.revision.metadata, rawScript: this.revision.source };
  }

  trackAdapter(promise) { return this.adapterTracker.track(promise); }
  whenAdaptersIdle() { return this.adapterTracker.whenIdle(); }
  adapterRetirement() {
    const tracker = this.adapterTracker;
    return {
      installationId: this.installationId, generation: this.generation,
      idle: tracker.whenIdle(), pendingAdapterTasks: () => tracker.tasks.size,
    };
  }

  async drainAdapters() {
    if (!this.adapterTasks.size) return;
    let timer;
    try {
      await Promise.race([
        this.whenAdaptersIdle(),
        new Promise(resolve => { timer = setTimeout(resolve, this.limits.cleanupTimeoutMs); }),
      ]);
    } finally { clearTimeout(timer); }
  }

  scopeIdentity(scope = this.root) {
    return Object.freeze({ installationId: this.installationId, generation: this.generation,
      siteId: scope.siteId ?? null, requestId: scope.requestId ?? null });
  }

  diagnostic(input, scopeId = 0) {
    const scope = scopeId === 0 ? this.root : this.invocations.get(scopeId);
    if (!this.live || !scope || scope.signal.aborted || !input) return;
    const kinds = ['Error', 'TypeError', 'ReferenceError', 'SyntaxError', 'RangeError', 'InternalError'];
    const reasons = ['reference', 'input', 'upstream', 'script'];
    const diagnostic = input.diagnostic;
    if (!diagnostic || !kinds.includes(diagnostic.kind) || !reasons.includes(diagnostic.reason)) return;
    const safe = { kind: diagnostic.kind, reason: diagnostic.reason };
    if (typeof diagnostic.code === 'string' &&
      describeScriptError({ code: diagnostic.code }).code === diagnostic.code) safe.code = diagnostic.code;
    for (const key of ['reference', 'member']) {
      if (typeof diagnostic[key] === 'string' && /^[A-Za-z_$][\w$]{0,63}$/.test(diagnostic[key]))
        safe[key] = diagnostic[key];
    }
    if (Number.isSafeInteger(diagnostic.line) && diagnostic.line > 0 && diagnostic.line <= 1_000_000)
      safe.line = diagnostic.line;
    if (Number.isSafeInteger(diagnostic.column) && diagnostic.column >= 0 && diagnostic.column <= 1_000_000)
      safe.column = diagnostic.column;
    const event = Object.freeze({ ...this.scopeIdentity(scope), ...safe });
    try { this.onDiagnostic?.(event); } catch { /* Observers do not own script execution. */ }
    if (typeof this.onScriptError === 'function' && typeof input.message === 'string') {
      try { this.onScriptError(Object.freeze({ ...event, message: input.message })); }
      catch { /* Raw errors are explicit, non-authoritative observation. */ }
    }
  }

  hostDiagnostic(operation, error, scopeId = 0) {
    if (!['on', 'send', 'request', 'util', 'storage', 'timer', 'microtask'].includes(operation)) return;
    const scope = scopeId === 0 ? this.root : this.invocations.get(scopeId);
    if (!this.live || !scope || scope.signal.aborted) return;
    try {
      this.onDiagnostic?.(Object.freeze({ ...this.scopeIdentity(scope),
        kind: 'ScriptHostError', reason: 'host', operation, code: safeError(error).code }));
    } catch { /* Diagnostics cannot alter capability results. */ }
  }

  send(event, data) {
    this.assertLive();
    if (event === 'inited') {
      requireValue(!this.initialized, 'SCRIPT_PROTOCOL_ERROR');
      this.capabilities = lxCapabilities(data);
      this.initialized = true;
    } else if (event === 'updateAlert') {
      requireValue(!this.alert && data && typeof data.log === 'string' &&
        data.log.length <= 1024 && (data.updateUrl === undefined ||
        (typeof data.updateUrl === 'string' && data.updateUrl.length <= 1024)), 'SCRIPT_PROTOCOL_ERROR');
      if (data.updateUrl) this.validateURL(data.updateUrl, this.root);
      this.alert = { log: data.log, updateUrl: data.updateUrl ?? '' };
    } else throw fault('SCRIPT_PROTOCOL_ERROR');
  }

  validateURL(input, scope) {
    this.assertLive();
    scope.assertOpen();
    let url;
    try { url = new URL(input); } catch { throw fault('INVALID_ARGUMENT'); }
    requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password);
    return url.href;
  }

  async hostCall(operation, input, scopeId) {
    this.assertLive();
    try {
      if (operation === 'request') return await this.request(input);
      if (operation === 'storage') {
        const [storageScope, action, key, value] = input;
        input = { scopeId: storageScope, operation: action, key, value };
        return await this.storageCall(input);
      }
      if (operation === 'abortRequest') {
        this.networks.get(input)?.close(fault('CANCELLED'));
        return null;
      }
      throw fault('UNSUPPORTED_OPTION');
    } catch (error) {
      const safe = safeError(error, operation === 'request' ? 'NETWORK_ERROR' : 'SCRIPT_EXCEPTION');
      this.hostDiagnostic(operation, safe,
        operation === 'storage' || operation === 'request' ? input.scopeId : scopeId);
      throw safe;
    }
  }

  async request({ id, url, options, scopeId }) {
    requireValue(Number.isSafeInteger(id) && id > 0 && !this.networks.has(id));
    requireValue(options && typeof options === 'object' && !Array.isArray(options));
    if (options.timeout !== undefined) requireValue(Number.isFinite(options.timeout) && options.timeout > 0);
    const timeout = Math.min(options.timeout ?? this.limits.requestTimeoutMs, this.limits.requestTimeoutMs);
    const child = new ResourceScope(this.root.signal, timeout);
    this.networks.set(id, child);
    const metadata = this.invocations.get(scopeId) ?? this.root;
    try {
      const request = {
        url: this.validateURL(url, child), options, signal: child.signal,
        scope: this.scopeIdentity(metadata), validateURL: next => this.validateURL(next, child),
      };
      requireValue(typeof this.network === 'function', 'PERMISSION_DENIED');
      const physical = this.trackAdapter(Promise.resolve().then(() => {
        child.assertOpen();
        return this.network(request);
      }));
      const response = await child.race(physical);
      requireValue(response && Number.isInteger(response.statusCode), 'NETWORK_ERROR');
      let body = response.body ?? Buffer.alloc(0);
      // The public adapter returns bytes. Strings support injected adapters;
      // neither form is decoded or JSON-parsed on the Node thread.
      if (typeof body === 'string') body = Buffer.from(body);
      requireValue(ArrayBuffer.isView(body) || body instanceof ArrayBuffer, 'NETWORK_ERROR');
      return { statusCode: response.statusCode, statusMessage: response.statusMessage ?? '',
        headers: response.headers ?? {}, url: response.url ?? url, body };
    } finally {
      this.networks.delete(id);
      child.close();
    }
  }

  async storageCall({ scopeId, operation, key, value }) {
    const scope = this.invocations.get(scopeId);
    // Authority comes only from the explicit context captured by dispatch.
    requireValue(this.activated && scope && !scope.signal.aborted, 'PERMISSION_DENIED');
    requireValue(['get', 'set', 'delete'].includes(operation));
    requireValue(this.revision.permissions.storage.includes(operation === 'get' ? 'read' : 'write'),
      'PERMISSION_DENIED');
    requireValue(typeof this.storage?.[operation] === 'function', 'PERMISSION_DENIED');
    requireValue(typeof key === 'string' && key.length > 0 && key.length <= 256);
    const signal = scope.signal, session = new WeakRef(this);
    const input = { ...this.scopeIdentity(scope), associationId: scope.associationId, key, signal,
      isCurrent: () => Boolean(session.deref()?.live && !signal.aborted) };
    if (operation === 'set') input.value = value;
    const task = this.trackAdapter(Promise.resolve().then(() => {
      scope.assertOpen();
      return this.storage[operation](input);
    }));
    try { return (await scope.race(task)) ?? null; }
    catch (error) { throw safeError(error, 'STORAGE_ERROR'); }
  }
}
