import { SourceRuntimeError } from '@meowfilm/source-runtime';
import { clone, deferred, fault, requireValue, safeError } from './errors.js';
import { QuickJSEngine } from './quickjs-engine.js';
import { portableRealmSource } from './portable-realm.js';
import { ResourceScope } from './scope.js';
import { createUtilities } from './utilities.js';

export class PortableSandbox {
  static async create(options) {
    const sandbox = new PortableSandbox(options);
    await sandbox.start();
    return sandbox;
  }

  constructor({ onCapability, onSync, onDiagnostic, onFailure, limits, wasmModule }) {
    requireValue(typeof onCapability === 'function' && typeof onSync === 'function');
    requireValue(onDiagnostic === undefined || typeof onDiagnostic === 'function');
    this.onCapability = onCapability;
    this.onSync = onSync;
    this.onDiagnostic = onDiagnostic;
    this.onFailure = onFailure;
    this.wasmModule = wasmModule;
    this.limits = limits;
    this.root = new ResourceScope();
    this.startup = new ResourceScope(this.root.signal, limits.initTimeoutMs);
    this.ready = deferred();
    this.tasks = new Map();
    this.adapterTasks = new Set();
    this.capabilityTasks = new Set();
    this.callbacks = new Map();
    this.timers = new Map();
    this.nextId = 0;
    this.live = true;
    this.activated = false;
    this.capabilities = {};
    this.utils = createUtilities(limits.maxMemoryBytes);
  }

  async start() {
    try {
      this.engine = await QuickJSEngine.create({
        source: portableRealmSource,
        limits: this.limits,
        wasmModule: this.wasmModule,
        bridge: (operation, args) => this.bridge(operation, args),
      });
      return this;
    } catch (error) {
      if (error.memoryLimitExceeded) this.fail(error);
      throw error;
    }
  }

  assertLive() {
    if (!this.live) throw fault('STALE_SESSION');
  }

  call(name, args, scope = this.root) {
    this.assertLive();
    scope.assertOpen();
    try { return this.engine.call(name, args); }
    catch (error) {
      if (this.engine.memoryLimitExceeded) this.fail(error);
      throw error;
    }
  }

  fail(error) {
    if (!this.live) return;
    if (this.engine?.memoryLimitExceeded) error.memoryLimitExceeded = true;
    this.close(safeError(error));
    try { this.onFailure?.(error); } catch {}
  }

  load(kind, source, metadata) {
    requireValue(typeof kind === 'string' && typeof source === 'string' &&
      metadata && typeof metadata === 'object');
    this.call('load', [kind, source, JSON.stringify(metadata)]);
    let timer;
    return Promise.race([
      this.ready.promise,
      new Promise((_resolve, reject) => {
        timer = setTimeout(() => reject(fault('TIMEOUT')), this.limits.initTimeoutMs);
      }),
    ]).catch(error => {
      this.close(safeError(error));
      throw error;
    }).finally(() => clearTimeout(timer));
  }

  activate() {
    this.assertLive();
    this.activated = true;
  }

  taskContext(id) {
    if (id === 0) {
      this.startup.assertOpen();
      return { scope: this.startup, context: Object.freeze({ startup: true }) };
    }
    const task = this.tasks.get(id);
    if (!task) throw fault('STALE_SESSION');
    return task;
  }

  track(promise) {
    this.adapterTasks.add(promise);
    promise.finally(() => this.adapterTasks.delete(promise)).catch(() => {});
    return promise;
  }

  bridge(operation, args) {
    if (operation === 'callbackDone') {
      this.forgetCallback(args[0]);
      return;
    }
    if (operation === 'callbackFailure') {
      const callback = this.callbacks.get(args[0]);
      if (callback) callback.scope.close(fault(args[1]));
      return;
    }
    if (operation === 'clearTimer') return this.clearTimer(args[0]);
    if (operation === 'result') {
      const task = this.tasks.get(args[0]);
      if (!task || task.scope.signal.aborted) return;
      if (args[1]) task.result.resolve(clone(args[2] ?? null));
      else {
        const error = fault(args[2] === 'MEMORY_LIMIT' ? 'RESOURCE_LIMIT' : args[2]);
        task.result.reject(error);
        if (args[2] === 'MEMORY_LIMIT') {
          this.engine.memoryLimitExceeded = true;
          this.fail(error);
        }
      }
      return;
    }
    if (operation === 'ready') {
      requireValue(!this.initialized && args[0] && Array.isArray(args[0].actions),
        'SCRIPT_PROTOCOL_ERROR');
      requireValue(args[0].actions.every(action =>
        typeof action === 'string' && /^[A-Za-z][A-Za-z0-9._-]{0,127}$/.test(action)) &&
        new Set(args[0].actions).size === args[0].actions.length, 'SCRIPT_PROTOCOL_ERROR');
      this.capabilities = clone(args[0]);
      this.initialized = true;
      this.ready.resolve(this.capabilities);
      this.startup.close(fault('STALE_SESSION'));
      return;
    }
    if (operation === 'startupFailure') {
      const error = fault(args[0] === 'MEMORY_LIMIT' ? 'RESOURCE_LIMIT' : args[0]);
      if (args[0] === 'MEMORY_LIMIT') {
        this.engine.memoryLimitExceeded = true;
        error.memoryLimitExceeded = true;
      }
      this.ready.reject(error);
      if (error.memoryLimitExceeded) this.fail(error);
      else this.close(error);
      return;
    }
    this.assertLive();
    switch (operation) {
      case 'maxBytes': return this.limits.maxMemoryBytes;
      case 'util': return this.utils(...args);
      case 'sync': {
        const { scope, context } = this.taskContext(args[0]);
        scope.assertOpen();
        return clone(this.onSync(args[1], clone(args[2] ?? {}), {
          ...context, signal: scope.signal,
        }) ?? null);
      }
      case 'capability': return this.capability(...args);
      case 'timer': return this.timer(...args);
      default: throw fault('UNSUPPORTED_OPTION');
    }
  }

  capability(taskId, callbackId, name, input) {
    const { scope, context } = this.taskContext(taskId);
    scope.assertOpen();
    requireValue(Number.isSafeInteger(callbackId) && callbackId > 0 &&
      !this.callbacks.has(callbackId) && !this.timers.has(callbackId) &&
      typeof name === 'string' && input && typeof input === 'object');
    const detach = scope.add(() => this.forgetCallback(callbackId));
    this.callbacks.set(callbackId, { scope, detach, repeat: false, kind: 'capability' });
    const task = this.track(Promise.resolve().then(() => {
      scope.assertOpen();
      return this.onCapability(name, clone(input), {
        ...context, signal: scope.signal,
      });
    }));
    this.capabilityTasks.add(task);
    task.finally(() => this.capabilityTasks.delete(task)).catch(() => {});
    scope.race(task).then(
      value => this.settle(callbackId, { ok: true, value: clone(value ?? null) }, scope),
      error => this.settle(callbackId, { ok: false, code: error instanceof SourceRuntimeError
        ? error.code : safeError(error, 'CAPABILITY_UNAVAILABLE').code }, scope),
    ).catch(() => {});
  }

  settle(id, payload, scope) {
    if (!this.live || scope.signal.aborted) return;
    try { this.call('settle', [id, JSON.stringify(payload)], scope); }
    catch (error) { scope.close(safeError(error)); }
  }

  timer(taskId, id, delay = 0, repeat = false) {
    requireValue(Number.isSafeInteger(id) && id > 0 && !this.callbacks.has(id));
    const { scope } = this.taskContext(taskId);
    const detach = scope.add(() => this.clearTimer(id));
    this.callbacks.set(id, { scope, detach, repeat, kind: 'timer' });
    const run = () => {
      if (!repeat) this.clearTimer(id, true);
      if (!this.live || scope.signal.aborted) return;
      try { this.call('callback', [id], scope); }
      catch (error) { scope.close(safeError(error)); }
    };
    const milliseconds = Math.max(1, Math.min(Number(delay) || 0, 2 ** 31 - 1));
    const handle = repeat ? setInterval(run, milliseconds) : setTimeout(run, milliseconds);
    this.timers.set(id, { handle, detach });
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

  forgetCallback(id) {
    const callback = this.callbacks.get(id);
    if (!callback) return;
    callback.detach();
    this.callbacks.delete(id);
    if (this.live && !this.engine?.closing) {
      queueMicrotask(() => {
        try { this.call('forget', [id], callback.scope); }
        catch { /* The owning scope/session is already retiring. */ }
      });
    }
  }

  async execute(method, args, {
    signal, timeoutMs = this.limits.requestTimeoutMs, context = {},
  } = {}) {
    this.assertLive();
    requireValue(this.activated, 'PERMISSION_DENIED');
    requireValue(Number.isSafeInteger(timeoutMs) && timeoutMs > 0 &&
      timeoutMs <= this.limits.requestTimeoutMs);
    const scope = new ResourceScope(this.root.signal, timeoutMs);
    const id = ++this.nextId;
    const result = deferred();
    this.tasks.set(id, { scope, result, context: Object.freeze(clone(context)) });
    let detach = () => {};
    try {
      if (signal) {
        const abort = () => scope.close(fault('CANCELLED'));
        if (signal.aborted) abort();
        else {
          signal.addEventListener('abort', abort, { once: true });
          detach = () => signal.removeEventListener('abort', abort);
        }
      }
      scope.assertOpen();
      this.call(method, [id, ...args], scope);
      return clone(await scope.race(result.promise));
    } catch (error) { throw safeError(error); }
    finally {
      detach();
      if (this.live && !this.root.signal.aborted) {
        try { this.call('releaseTask', [id], this.root); }
        catch { /* Closing the owning session releases the whole guest heap. */ }
      }
      scope.close();
      this.tasks.delete(id);
    }
  }

  invoke(action, payload, options = {}) {
    return this.execute('invoke', [action, JSON.stringify(payload ?? {})], options);
  }

  evaluate(source, bindings, options = {}) {
    return this.execute('evaluate', [source, JSON.stringify(bindings ?? {})], options);
  }

  async drain() {
    const deadline = Date.now() + this.limits.cleanupTimeoutMs;
    while (this.adapterTasks.size && Date.now() < deadline) {
      let timer;
      try {
        await Promise.race([
          Promise.allSettled([...this.adapterTasks]),
          new Promise(resolve => {
            timer = setTimeout(resolve, Math.max(1, deadline - Date.now()));
          }),
        ]);
      } finally { clearTimeout(timer); }
    }
  }

  close(reason = fault('STALE_SESSION')) {
    if (!this.live) return;
    this.live = false;
    this.activated = false;
    this.ready.reject(reason);
    this.startup.close(reason);
    this.root.close(reason);
    this.callbacks.clear();
    this.timers.clear();
    this.engine?.close();
  }

  stats() {
    return {
      live: this.live,
      invocations: this.tasks.size,
      timers: this.timers.size,
      networkRequests: this.capabilityTasks.size,
      pendingAdapterTasks: this.adapterTasks.size,
    };
  }
}
