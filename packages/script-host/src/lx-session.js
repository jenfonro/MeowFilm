import { AsyncLocalStorage } from 'node:async_hooks';
import { ResourceScope } from './scope.js';
import { clone, deferred, fault, requireValue, safeError } from './errors.js';
import { lxCapabilities, lxPayload } from './lx-capabilities.js';
import { realmSource } from './realm.js';
import { createUtilities } from './utilities.js';
import { QuickJSEngine } from './quickjs-engine.js';
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
      waiter?.resolve();
    });
  }

  retirement(installationId, generation) {
    const tracker = this;
    return {
      installationId, generation,
      idle: tracker.whenIdle(),
      pendingAdapterTasks: () => tracker.tasks.size,
    };
  }
}

export class LXSession {
  constructor({ installationId, generation, revision, network, storage, onDiagnostic, onScriptError,
    onFailure, limits, wasmModule }) {
    Object.assign(this, { installationId, generation, revision, network, storage, onDiagnostic, onScriptError,
      onFailure, limits, wasmModule });
    this.live = true;
    this.activated = false;
    this.ready = deferred();
    this.root = new ResourceScope();
    this.scriptResources = new ResourceScope(this.root.signal);
    // Host execution-turn ownership, not guest Promise privilege propagation.
    this.contextScope = new AsyncLocalStorage();
    this.invocations = new Map();
    this.timers = new Map();
    this.networks = new Map();
    this.callbacks = new Map();
    this.adapterTracker = new AdapterTaskTracker();
    this.adapterTasks = this.adapterTracker.tasks;
    this.nextId = 0;
    this.capabilities = {};
    this.alert = null;
    this.utils = createUtilities(limits.maxMemoryBytes);
  }

  scope() { return this.contextScope.getStore() ?? this.root; }
  resourceScope() { return this.scriptResources; }
  assertLive() {
    if (!this.live) throw fault('STALE_SESSION');
    this.scope().assertOpen();
  }

  async start() {
    const timeout = setTimeout(() => this.fail(fault('TIMEOUT')), this.limits.initTimeoutMs);
    try {
      this.engine = await QuickJSEngine.create({
        source: realmSource, limits: this.limits, wasmModule: this.wasmModule,
        bridge: (operation, args) => {
          try { return this.bridge(operation, args); }
          catch (error) { this.bridgeDiagnostic(operation, args, error); throw error; }
        },
      });
      if (!this.live) { this.engine.close(); throw fault('STALE_SESSION'); }
      this.contextScope.run(this.root, () => this.engine.evaluate(this.revision.source));
      await this.ready.promise;
      this.assertLive();
      return this;
    } catch (error) {
      const failure = this.failure ?? safeError(error);
      this.fail(error);
      throw this.failure ?? failure;
    } finally { clearTimeout(timeout); }
  }

  activate() { this.assertLive(); this.activated = true; }

  call(name, args, scope, frames = []) {
    if (!this.live) throw fault('STALE_SESSION');
    scope.assertOpen();
    try {
      return this.contextScope.run(scope, () => this.engine.call(name, args, frames));
    }
    catch (error) {
      if (this.engine.memoryLimitExceeded) this.fail(safeError(error));
      throw error;
    }
  }

  registerCallback(id, scope, repeat = false) {
    requireValue(Number.isSafeInteger(id) && id > 0 && !this.callbacks.has(id));
    const detach = scope.add(() => this.forgetCallback(id));
    this.callbacks.set(id, { scope, repeat, detach });
  }

  forgetCallback(id) {
    const item = this.callbacks.get(id);
    if (!item) return;
    item.detach();
    this.callbacks.delete(id);
    queueMicrotask(() => {
      if (this.live && !this.engine?.closing) {
        try { this.engine.call('forgetCallback', [id]); }
        catch { /* Forgetting a retired callback cannot alter session state. */ }
      }
    });
  }

  callback(id, value, scope) {
    if (!this.live || scope.signal.aborted) return;
    try { this.call('callback', [id, JSON.stringify(value)], scope); }
    catch (error) { this.callbackFailed(error, scope); }
  }

  callbackFailed(error, scope) {
    if (!this.live || scope.signal.aborted) return;
    if (error?.code === 'MEMORY_LIMIT') {
      if (this.engine) this.engine.memoryLimitExceeded = true;
      error = fault('RESOURCE_LIMIT');
    }
    if (this.engine?.memoryLimitExceeded) this.fail(safeError(error));
    else if (scope === this.scriptResources) {
      const failure = safeError(error);
      try {
        this.onDiagnostic?.(Object.freeze({ ...this.scopeIdentity(scope),
          kind: 'ScriptCallbackError', reason: 'script', code: failure.code }));
      } catch { /* Diagnostics cannot change callback execution. */ }
    }
    else scope.close(safeError(error));
  }

  bridgeDiagnostic(operation, args, error) {
    if (!this.live || !this.onDiagnostic ||
      !['on', 'send', 'request', 'util', 'storage', 'timer', 'microtask', 'node'].includes(operation)) return;
    const code = safeError(error).code;
    const scope = operation === 'storage' ? this.invocations.get(args[0])?.scope : this.scope();
    if (!scope || scope.signal.aborted) return;
    try {
      this.onDiagnostic(Object.freeze({ ...this.scopeIdentity(scope),
        kind: 'ScriptHostError', reason: 'host', operation, code }));
    } catch { /* Diagnostics cannot alter guest results or capability checks. */ }
  }

  emitScriptDiagnostic(diagnostic, scope, message) {
    if (!this.live || scope.signal.aborted) return;
    const event = Object.freeze({ ...this.scopeIdentity(scope), ...diagnostic });
    try { this.onDiagnostic?.(event); }
    catch { /* Observation cannot alter script results or authority. */ }
    if (this.onScriptError && typeof message === 'string') {
      try { this.onScriptError(Object.freeze({ ...event, message })); }
      catch { /* Raw error observation is trusted, opt-in and non-authoritative. */ }
    }
  }

  bridge(operation, args) {
    if (operation === 'observeScriptErrors') return typeof this.onScriptError === 'function';
    if (operation === 'diagnostic') {
      const scope = args[0] === 0 ? this.root : this.invocations.get(args[0])?.scope;
      if (!this.live || !scope || scope.signal.aborted || !this.onDiagnostic && !this.onScriptError) return;
      const input = args[1];
      const kinds = ['Error', 'TypeError', 'ReferenceError', 'SyntaxError', 'RangeError', 'InternalError'];
      const reasons = ['reference', 'input', 'upstream', 'script'];
      if (!input || !kinds.includes(input.kind) || !reasons.includes(input.reason)) return;
      const diagnostic = { kind: input.kind, reason: input.reason };
      if (typeof input.code === 'string' && describeScriptError({ code: input.code }).code === input.code)
        diagnostic.code = input.code;
      if (typeof input.reference === 'string' && /^[A-Za-z_$][\w$]{0,63}$/.test(input.reference))
        diagnostic.reference = input.reference;
      if (typeof input.member === 'string' && /^[A-Za-z_$][\w$]{0,63}$/.test(input.member))
        diagnostic.member = input.member;
      if (Number.isSafeInteger(input.line) && input.line > 0 && input.line <= 1_000_000)
        diagnostic.line = input.line;
      if (Number.isSafeInteger(input.column) && input.column >= 0 && input.column <= 1_000_000)
        diagnostic.column = input.column;
      this.emitScriptDiagnostic(diagnostic, scope, args[2]);
      return;
    }
    if (operation === 'abortRequest') {
      const item = this.networks.get(args[0]);
      if (item) item.child.close(fault('CANCELLED'));
      return;
    }
    if (operation === 'clearTimer') return this.clearTimer(args[0]);
    if (operation === 'callbackDone') {
      const item = this.callbacks.get(args[0]);
      if (item && !item.repeat) this.forgetCallback(args[0]);
      return;
    }
    if (operation === 'callbackFailure') {
      const item = this.callbacks.get(args[0]);
      const memory = args[1] === 'MEMORY_LIMIT';
      if (memory && this.engine) this.engine.memoryLimitExceeded = true;
      const code = memory ? 'RESOURCE_LIMIT' : args[1];
      if (item) this.callbackFailed(fault(code), item.scope);
      return;
    }
    if (operation === 'invokeResult') {
      const item = this.invocations.get(args[0]);
      if (!this.live || !item || item.scope.signal.aborted) return;
      if (args[1]) {
        const value = clone(args[2]);
        item.result.resolve(value);
      } else {
        const memory = args[2] === 'MEMORY_LIMIT';
        if (memory && this.engine) this.engine.memoryLimitExceeded = true;
        const code = memory ? 'RESOURCE_LIMIT' : args[2];
        item.result.reject(fault(code));
      }
      return;
    }
    this.assertLive();
    switch (operation) {
      case 'config': return JSON.stringify(this.revision.config);
      case 'scriptInfo': return JSON.stringify(this.scriptInfo());
      case 'util': return this.utils(...args);
      case 'on':
        requireValue(args[0] === 'request', 'SCRIPT_PROTOCOL_ERROR');
        this.handlerRegistered = true;
        return;
      case 'send': return this.send(...args);
      case 'request': return this.request(...args);
      case 'storage': return this.storageCall(...args);
      case 'timer': return this.timer(...args);
      case 'microtask': return this.registerCallback(args[0], this.resourceScope());
      case 'startupFailure': {
        const memory = args[0] === 'MEMORY_LIMIT';
        if (memory && this.engine) this.engine.memoryLimitExceeded = true;
        return this.fail(fault(memory ? 'RESOURCE_LIMIT' : args[0]));
      }
      default: throw fault('UNSUPPORTED_OPTION');
    }
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

  trackAdapter(promise) {
    return this.adapterTracker.track(promise);
  }

  whenAdaptersIdle() { return this.adapterTracker.whenIdle(); }

  adapterRetirement() {
    return this.adapterTracker.retirement(this.installationId, this.generation);
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

  send(event, data) {
    try {
      if (event === 'inited') {
        requireValue(!this.initialized && this.handlerRegistered, 'SCRIPT_PROTOCOL_ERROR');
        this.capabilities = lxCapabilities(data);
        this.initialized = true;
        this.ready.resolve();
      } else if (event === 'updateAlert') {
        requireValue(!this.alert && data && typeof data.log === 'string' &&
          data.log.length <= 1024 && (data.updateUrl === undefined ||
          (typeof data.updateUrl === 'string' && data.updateUrl.length <= 1024)), 'SCRIPT_PROTOCOL_ERROR');
        if (data.updateUrl) this.validateURL(data.updateUrl, this.root);
        this.alert = { log: data.log, updateUrl: data.updateUrl ?? '' };
      } else throw fault('SCRIPT_PROTOCOL_ERROR');
    } catch {
      const error = fault('SCRIPT_PROTOCOL_ERROR');
      this.fail(error);
      throw error;
    }
  }

  timer(id, delay = 0, repeat) {
    const scope = this.resourceScope();
    this.registerCallback(id, scope, repeat);
    const run = () => {
      if (!repeat) this.clearTimer(id, true);
      this.callback(id, {}, scope);
    };
    const ms = Math.max(1, Math.min(Number(delay) || 0, 2 ** 31 - 1));
    const handle = repeat ? setInterval(run, ms) : setTimeout(run, ms);
    const detach = scope.add(() => this.clearTimer(id));
    this.timers.set(id, { handle, scope, detach });
  }

  clearTimer(id, firing = false) {
    const timer = this.timers.get(id);
    if (!timer) return;
    clearTimeout(timer.handle);
    clearInterval(timer.handle);
    timer.detach();
    this.timers.delete(id);
    if (!firing) this.forgetCallback(id);
  }

  validateURL(input, scope) {
    if (!this.live) throw fault('STALE_SESSION');
    scope.assertOpen();
    let url;
    try { url = new URL(input); } catch { throw fault('INVALID_ARGUMENT'); }
    requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password);
    return url.href;
  }

  request(url, options, id) {
    const metadata = this.scope();
    const owner = this.resourceScope();
    const copied = clone(options);
    requireValue(copied && typeof copied === 'object' && !Array.isArray(copied));
    if (copied.timeout !== undefined) requireValue(Number.isFinite(copied.timeout) && copied.timeout > 0);
    const timeout = Math.min(copied.timeout ?? this.limits.requestTimeoutMs, this.limits.requestTimeoutMs);
    const child = new ResourceScope(owner.signal, timeout);
    this.registerCallback(id, owner);
    this.networks.set(id, { owner, child });
    const completion = {
      state: { session: this, owner, child, copied, url, id },
      settle(error, response) {
        const state = this.state;
        if (!state) return;
        this.state = null;
        const { session, owner: requestOwner, child: requestChild,
          copied: requestOptions, url: requestedUrl, id: callbackId } = state;
        session.networks.delete(callbackId);
        requestChild.close();
        if (!session.live || requestOwner.signal.aborted) return;
        let input;
        try {
          if (error) input = { code: error.code };
          else {
            requireValue(response && Number.isInteger(response.statusCode), 'NETWORK_ERROR');
            const raw = typeof response.body === 'string'
              ? Buffer.from(response.body) : Buffer.from(response.body ?? []);
            input = {
              response: { statusCode: response.statusCode, statusMessage: response.statusMessage ?? '',
                headers: response.headers ?? {}, url: response.url ?? requestedUrl },
              body: raw.toString('base64'), encoding: requestOptions.encoding ?? 'utf8',
              json: requestOptions.json === true,
            };
            if (requestOptions.encoding === null) input.encoding = null;
          }
        } catch (failure) { input = { code: safeError(failure, 'NETWORK_ERROR').code }; }
        session.callback(callbackId, input, requestOwner);
      },
    };
    child.signal.addEventListener('abort', () => completion.settle(child.reason), { once: true });
    const sessionRef = new WeakRef(this);
    const requestInput = {
      options: copied, signal: child.signal, scope: this.scopeIdentity(metadata),
      validateURL: next => {
        const session = sessionRef.deref();
        if (!session) throw fault('STALE_SESSION');
        return session.validateURL(next, child);
      },
    };
    const task = this.trackAdapter(Promise.resolve().then(() => {
      const session = sessionRef.deref();
      if (!session) throw fault('STALE_SESSION');
      requestInput.url = session.validateURL(url, child);
      requireValue(typeof session.network === 'function', 'PERMISSION_DENIED');
      return session.network(requestInput);
    }));
    task.then(
      response => completion.settle(null, response),
      error => completion.settle(safeError(error, 'NETWORK_ERROR')),
    ).catch(() => {});
  }

  scopeIdentity(scope) {
    return Object.freeze({ installationId: this.installationId, generation: this.generation,
      siteId: scope.siteId ?? null, requestId: scope.requestId ?? null });
  }

  storageCall(scopeId, operation, key, value, callbackId) {
    const item = this.invocations.get(scopeId);
    // The explicit request context survives guest awaits without ambient privileges.
    requireValue(this.activated && item && !item.scope.signal.aborted, 'PERMISSION_DENIED');
    const scope = item.scope;
    requireValue(['get', 'set', 'delete'].includes(operation));
    requireValue(this.revision.permissions.storage.includes(operation === 'get' ? 'read' : 'write'), 'PERMISSION_DENIED');
    requireValue(typeof this.storage?.[operation] === 'function', 'PERMISSION_DENIED');
    requireValue(typeof key === 'string' && key.length > 0 && key.length <= 256);
    const sessionRef = new WeakRef(this);
    const signal = scope.signal;
    const input = { ...this.scopeIdentity(scope), associationId: scope.associationId, key, signal,
      isCurrent: () => Boolean(sessionRef.deref()?.live && !signal.aborted) };
    if (operation === 'set') input.value = clone(value);
    this.registerCallback(callbackId, scope);
    const storage = this.storage;
    const adapter = storage[operation];
    const task = this.trackAdapter(Promise.resolve().then(() => {
      scope.assertOpen();
      return Reflect.apply(adapter, storage, [input]);
    }));
    scope.race(task).then(
      result => this.storageResult(callbackId, { value: clone(result ?? null) }, scope),
      error => this.storageResult(callbackId, { code: safeError(error, 'STORAGE_ERROR').code }, scope),
    ).catch(() => {});
  }

  storageResult(id, result, scope) {
    if (!this.live || scope.signal.aborted) return;
    try { this.call('storageResult', [id, JSON.stringify(result)], scope); }
    catch (error) { this.callbackFailed(error, scope); }
  }

  async invoke(request, { signal, timeoutMs, associationId } = {}) {
    this.assertLive();
    const payload = lxPayload(this.capabilities, request);
    const scope = new ResourceScope(this.root.signal, timeoutMs ?? this.limits.requestTimeoutMs);
    scope.siteId = request.siteId;
    scope.requestId = ++this.nextId;
    scope.associationId = associationId;
    const result = deferred();
    this.invocations.set(scope.requestId, { scope, result });
    let detach = () => {};
    try {
      if (signal) {
        const abort = () => scope.close(fault('CANCELLED'));
        if (signal.aborted) abort();
        else { signal.addEventListener('abort', abort, { once: true }); detach = () => signal.removeEventListener('abort', abort); }
      }
      scope.assertOpen();
      this.call('dispatch', [scope.requestId, JSON.stringify(payload)], scope);
      const value = await scope.race(result.promise);
      if (!this.live) throw fault('STALE_SESSION');
      scope.assertOpen();
      return clone(value);
    } catch (error) {
      if (this.engine?.memoryLimitExceeded || error?.code === 'MEMORY_LIMIT') {
        if (this.engine) this.engine.memoryLimitExceeded = true;
        try { error.memoryLimitExceeded = true; } catch { /* The session flag is authoritative. */ }
        this.fail(error);
      }
      throw this.memoryLimitExceeded ? fault('RESOURCE_LIMIT') : safeError(error);
    }
    finally {
      detach();
      scope.close();
      this.invocations.delete(scope.requestId);
    }
  }

  fail(error) {
    if (!this.live) return;
    const ready = this.activated;
    this.memoryLimitExceeded = Boolean(this.memoryLimitExceeded ||
      this.engine?.memoryLimitExceeded || error?.memoryLimitExceeded);
    this.failure = this.memoryLimitExceeded ? fault('RESOURCE_LIMIT') : safeError(error);
    if (this.memoryLimitExceeded) {
      try { this.failure.memoryLimitExceeded = true; } catch { /* Session state is authoritative. */ }
    }
    this.ready.reject(this.failure);
    this.stop(this.failure);
    this.reportFailure(ready);
  }

  reportFailure(ready) {
    if (this.failureReported || !this.failure) return;
    this.failureReported = true;
    try {
      this.onDiagnostic?.(Object.freeze({ ...this.scopeIdentity(this.root),
        kind: 'ScriptHostError', reason: 'host',
        operation: this.memoryLimitExceeded ? 'memory.limit' : 'session.failed',
        code: this.failure.code, phase: ready ? 'running' : 'initialization' }));
    } catch { /* Diagnostics cannot alter failure handling. */ }
    try {
      this.onFailure?.(this.failure, Object.freeze({ ready }));
    }
    catch { /* Failure observation cannot alter failure handling. */ }
  }

  stop(reason = fault('STALE_SESSION')) {
    if (!this.live) return;
    this.live = false;
    this.activated = false;
    this.ready.reject(reason);
    this.root.close(reason);
    this.callbacks.clear();
    this.engine?.close();
  }

  stats() {
    return { timers: this.timers.size, networkRequests: this.networks.size,
      invocations: this.invocations.size, pendingAdapterTasks: this.adapterTasks.size, live: this.live };
  }
}
