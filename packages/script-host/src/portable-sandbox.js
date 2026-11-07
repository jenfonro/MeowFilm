import { sourceFault, sourceSafeError } from '@meowfilm/source-runtime';

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  promise.catch(() => {});
  return { promise, resolve, reject };
}

// Runs inside the trusted source Context. Guest calls cross only to the other
// Context of this Runtime; no Node objects enter either Context.
export class PortableSandbox {
  constructor({ onCapability, onSync, guest, taskContext, limits, signal }) {
    Object.assign(this, { onCapability, onSync, guest, taskContext, limits });
    this.startupSignal = signal;
    this.ready = deferred();
    this.tasks = new Map();
    this.adapterTasks = new Set();
    this.capabilityTasks = new Set();
    this.live = true;
    this.activated = false;
    this.capabilities = {};
  }

  assertLive() {
    if (!this.live) throw sourceFault('STALE_SESSION');
  }

  async load(kind, source, metadata) {
    this.assertLive();
    await this.guest('load', [kind, source, metadata]);
    this.capabilities = await this.ready.promise;
    this.guest('releaseTask', [0]);
    return this.capabilities;
  }

  activate() { this.assertLive(); this.activated = true; }

  cancel(id, reason) {
    const task = this.tasks.get(id);
    if (!task) return;
    this.tasks.delete(id);
    task.result.reject(reason);
    this.guest('releaseTask', [id]);
    return task.context.invocationId;
  }

  context(id) {
    this.assertLive();
    if (id !== this.taskContext()) throw sourceFault('STALE_SESSION');
    if (id === 0 && !this.initialized) {
      if (this.startupSignal?.aborted) throw sourceSafeError(this.startupSignal.reason, 'CANCELLED');
      return { context: { startup: true }, signal: this.startupSignal };
    }
    const task = this.tasks.get(id);
    if (!task || task.signal?.aborted) throw sourceFault('STALE_SESSION');
    return task;
  }

  track(promise, capability = false) {
    this.adapterTasks.add(promise);
    if (capability) this.capabilityTasks.add(promise);
    promise.finally(() => {
      this.adapterTasks.delete(promise);
      this.capabilityTasks.delete(promise);
    }).catch(() => {});
    return promise;
  }

  bridge(operation, ...args) {
    switch (operation) {
      case 'ready':
        this.initialized = true;
        this.ready.resolve(args[0]);
        return null;
      case 'startupFailure':
        this.ready.reject(sourceFault(args[0]));
        return null;
      case 'result': {
        const task = this.tasks.get(args[0]);
        if (!task || task.signal?.aborted) return null;
        if (args[1]) task.result.resolve(args[2] ?? null);
        else task.result.reject(sourceFault(args[2]));
        return null;
      }
      case 'sync': {
        const [id, name, keys, ...values] = args;
        const task = this.context(id);
        const input = Object.fromEntries(keys.map((key, index) => [key, values[index]]));
        return this.onSync(name, input, {
          ...task.context, signal: task.signal,
        }) ?? null;
      }
      case 'capability': {
        const [id, callbackId, name, ...parameters] = args;
        const task = this.context(id);
        // Only this private call record is unpacked; each business value was
        // independently checked at the native argument boundary.
        const input = name === 'bridge.call'
          ? { name: parameters[0], args: parameters.slice(1) }
          : Object.fromEntries(parameters[0].map((key, index) => [key, parameters[index + 1]]));
        const promise = this.track(Promise.resolve().then(() => {
          this.context(id);
          return this.onCapability(name, input, { ...task.context, signal: task.signal });
        }), true);
        promise.then(
          value => this.settle(id, callbackId, true, value ?? null),
          error => this.settle(id, callbackId, false, sourceSafeError(error, 'CAPABILITY_UNAVAILABLE').code),
        ).catch(() => {});
        return null;
      }
      case 'callbackDone': return null;
      case 'callbackFailure': {
        const task = this.tasks.get(this.taskContext());
        task?.result.reject(sourceFault(args[1]));
        return null;
      }
      default: throw sourceFault('UNSUPPORTED_OPTION');
    }
  }

  settle(id, callbackId, ok, value) {
    if (!this.live || (id === 0 ? this.initialized : !this.tasks.has(id))) return;
    try {
      this.context(id);
      this.guest('settle', [callbackId, ok, value]);
    } catch { /* Retired task results cannot recreate guest callbacks. */ }
  }

  async execute(method, args, { signal, context = {} } = {}) {
    this.assertLive();
    if (!this.activated) throw sourceFault('PERMISSION_DENIED');
    const id = this.taskContext();
    if (this.tasks.has(id)) throw sourceFault('SCRIPT_PROTOCOL_ERROR');
    const result = deferred();
    this.tasks.set(id, { context, signal, result });
    const abort = () => result.reject(sourceSafeError(signal.reason, 'CANCELLED'));
    if (signal?.aborted) abort();
    else signal?.addEventListener('abort', abort, { once: true });
    try {
      this.context(id);
      await this.guest(method, [id, ...args]);
      return await result.promise;
    } finally {
      signal?.removeEventListener('abort', abort);
      this.tasks.delete(id);
      this.guest('releaseTask', [id]);
    }
  }

  invoke(action, payload, options) {
    return this.execute('invoke', [action, payload ?? {}], options);
  }

  evaluate(source, bindings, options) {
    return this.execute('evaluate', [source, bindings ?? {}], options);
  }

  async drain() {
    if (!this.adapterTasks.size) return;
    let timer;
    try {
      await Promise.race([
        Promise.allSettled([...this.adapterTasks]),
        new Promise(resolve => { timer = setTimeout(resolve, this.limits.cleanupTimeoutMs); }),
      ]);
    } finally { clearTimeout(timer); }
  }

  close(reason = sourceFault('STALE_SESSION')) {
    if (!this.live) return;
    this.live = false;
    this.activated = false;
    this.ready.reject(reason);
    for (const [id, task] of this.tasks) {
      task.result.reject(reason);
      this.guest('releaseTask', [id]);
    }
    this.guest('releaseTask', [0]);
  }

  stats() {
    return {
      live: this.live,
      invocations: this.tasks.size,
      timers: this.live ? this.guest('timerCount', []) : 0,
      networkRequests: this.capabilityTasks.size,
      pendingAdapterTasks: this.adapterTasks.size,
    };
  }
}
