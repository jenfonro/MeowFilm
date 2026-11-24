import { loadBinding } from './native-loader.js';

function fault(code) {
  return Object.assign(new Error(code), { code });
}

/**
 * NativeEngine.create() returns immediately so initialization can be stopped.
 * Await .ready before publishing a session; call() also waits for initialization.
 *
 * source and optional guestSource evaluate to method objects in separate contexts
 * of one runtime. call(method, args, {scope, signal, timeoutMs}) spreads args.
 * onHostCall(op, input, scope, {signal}) may return a value or a Promise.
 * For positional host calls, input is the owned array of independently checked arguments.
 * onEvent(name, value, scope) receives owned typed data, including Node Buffers.
 */
export class NativeEngine {
  static create(options) {
    return new NativeEngine(options);
  }

  constructor({
    source, guestSource, guestHelpers = [], maxStackBytes = 256 * 1024, maxHeapBytes = 0,
    allowDeadlineControl = false, onHostCall, onEvent, signal, timeoutMs = 60_000, cleanupTimeoutMs = 100,
  }) {
    this.pending = new Map();
    this.hostTasks = new Set();
    this.hostControllers = new Map();
    this.onHostCall = onHostCall;
    this.onEvent = onEvent;
    this.nextId = 0;
    this.closing = false;
    this.ready = new Promise((resolve, reject) => {
      this.pending.set(0, { resolve: () => resolve(this), reject });
    });
    this.ready.catch(() => {});
    this.exited = new Promise(resolve => { this.resolveExit = resolve; });
    try {
      this.native = loadBinding().create({
        source, guestSource, guestHelpers, maxStackBytes, maxHeapBytes,
        allowDeadlineControl: Boolean(allowDeadlineControl), timeoutMs, cleanupTimeoutMs,
      }, message => this.receive(message));
    } catch (error) {
      this.pending.get(0).reject(error);
      this.pending.clear();
      throw error;
    }
    if (signal) {
      this.initSignal = signal;
      this.initAbort = () => { this.stop('CANCELLED').catch(() => {}); };
      if (signal.aborted) this.initAbort();
      else signal.addEventListener('abort', this.initAbort, { once: true });
    }
    this.ready.finally(() => {
      this.initSignal?.removeEventListener('abort', this.initAbort);
    }).catch(() => {});
  }

  receive(message) {
    if (message.type === 'host') {
      if (this.closing) return;
      const controller = new AbortController();
      this.hostControllers.set(message.id, { controller, scope: message.scope });
      const task = Promise.resolve().then(() => {
        if (controller.signal.aborted) throw fault('CANCELLED');
        if (!this.onHostCall) throw fault('CAPABILITY_UNAVAILABLE');
        return this.onHostCall(message.name, message.value, message.scope, {
          signal: controller.signal,
        });
      }).then(
        value => {
          if (!this.closing) this.native.complete(message.id, true, value ?? null);
        },
        error => {
          if (!this.closing) this.native.complete(message.id, false, error?.code ?? 'SCRIPT_EXCEPTION');
        },
      ).catch(error => {
        if (this.closing || error?.code === 'STALE_SESSION') return;
        try { this.native.complete(message.id, false, error?.code ?? 'INVALID_ARGUMENT'); }
        catch (completeError) {
          if (!this.closing && completeError?.code !== 'STALE_SESSION') {
            this.stop('SCRIPT_EXCEPTION').catch(() => {});
          }
        }
      }).finally(() => {
        this.hostControllers.delete(message.id);
        this.hostTasks.delete(task);
      });
      this.hostTasks.add(task);
      return;
    }
    if (message.type === 'cancelScope') {
      for (const { controller, scope } of this.hostControllers.values()) {
        if (scope === message.scope) controller.abort(fault(message.code));
      }
      return;
    }
    if (message.type === 'cancelHost') {
      this.hostControllers.get(message.id)?.controller.abort(fault(message.code));
      return;
    }
    if (message.type === 'event') {
      try { this.onEvent?.(message.name, message.value, message.scope); }
      catch { /* Event observers do not own the executor. */ }
      return;
    }
    if (message.type === 'stopped') {
      this.closing = true;
      for (const { controller } of this.hostControllers.values()) controller.abort(fault('STALE_SESSION'));
      for (const entry of this.pending.values()) entry.reject(fault(message.code || 'STALE_SESSION'));
      this.pending.clear();
      this.resolveExit(Object.assign(message.value, { code: message.code || 'STALE_SESSION' }));
      return;
    }
    const entry = this.pending.get(message.id);
    if (!entry) return;
    this.pending.delete(message.id);
    if (message.ok) entry.resolve(message.value);
    else entry.reject(fault(message.code || 'SCRIPT_EXCEPTION'));
  }

  async call(method, args = [], { scope = 0, signal, timeoutMs = 0 } = {}) {
    if (this.closing) throw fault('STALE_SESSION');
    if (signal?.aborted) throw fault(signal.reason?.code === 'TIMEOUT' ? 'TIMEOUT' : 'CANCELLED');
    // Native invoke snapshots args before returning, including TypedArray views.
    // Admission does not await .ready in JS: the owner queues it behind bootstrap.
    const id = ++this.nextId;
    let abort;
    const result = new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      try {
        this.native.invoke(id, method, args, scope, timeoutMs);
        if (signal) {
          abort = () => {
            const code = signal.reason?.code === 'TIMEOUT' ? 'TIMEOUT' : 'CANCELLED';
            if (this.native.cancel(id, code)) {
              const entry = this.pending.get(id);
              if (entry) {
                this.pending.delete(id);
                entry.reject(fault(code));
              }
            }
          };
          signal.addEventListener('abort', abort, { once: true });
          if (signal.aborted) abort();
        }
      } catch (error) {
        this.pending.delete(id);
        reject(error);
      }
    });
    try { return await result; }
    finally { if (abort) signal?.removeEventListener('abort', abort); }
  }

  stats() {
    return this.native.stats();
  }

  stop(reason = 'STALE_SESSION') {
    if (!this.stopping) {
      this.closing = true;
      for (const { controller } of this.hostControllers.values()) controller.abort(fault(reason));
      this.native.stop(reason);
      this.stopping = this.exited.then(async stats => {
        await Promise.allSettled([...this.hostTasks]);
        return stats;
      });
    }
    return this.stopping;
  }
}
