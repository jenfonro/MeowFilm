import { Worker } from 'node:worker_threads';
import { readFileSync } from 'node:fs';
import { clone, deferred, fault, requireValue, safeError, ScriptHostError } from './errors.js';
import { cancel, errorFromResponse, event, request, response, validateMessage } from './worker-protocol.js';

let compiledModule;
const processedWorkerResult = Symbol('processed-worker-result');

export function workerResult(value) {
  return { [processedWorkerResult]: true, value };
}

function wasmModule() {
  compiledModule ??= Promise.resolve().then(() =>
    WebAssembly.compile(readFileSync(new URL(import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')))));
  return compiledModule;
}

export class WorkerSession {
  #termination = null;

  constructor(input) {
    this.installationId = input.installationId;
    this.generation = input.generation;
    this.revision = input.revision;
    this.limits = input.limits;
    this.live = true;
    this.activated = false;
    this.capabilities = {};
    this.alert = null;
    this.catSnapshot = null;
    this.failure = null;
    this.memoryLimitExceeded = false;
    this.cleanupError = null;
    this.statsSnapshot = { live: true, timers: 0, networkRequests: 0, invocations: 0,
      pendingAdapterTasks: 0 };
    this.nextId = 1;
    this.pending = new Map();
    this.adapters = new Map();
    this.adapterTasks = new Set();
    this.leases = new Map();
    this.worker = null;
    this.exitCode = null;
    this.statsRequest = null;
    this.stopping = null;
    this.stopRequested = false;
    this.input = input;
    this.startParams = clone(input.startParams ?? {});
    this.lifecycle = new AbortController();
    this.proxyLifecycle = new AbortController();
    this.stoppedNotified = false;
    this.unresponsive = false;
    this.failureNotified = false;
  }

  async start() {
    this.#rejectStoppedStart();
    const module = await wasmModule();
    this.#rejectStoppedStart();
    const worker = new Worker(new URL('./worker-entry.js', import.meta.url), {
      workerData: {
        wasmModule: module,
      },
      stdout: false,
      stderr: false,
    });
    this.worker = worker;
    worker.on('message', message => this.#message(message));
    worker.on('error', error => {
      this.#failed(safeError(error, 'SCRIPT_EXCEPTION'), 'worker.exit');
    });
    worker.on('exit', code => this.#exited(worker, code));
    try {
      const startParams = {
        installationId: this.installationId,
        generation: this.generation,
        revision: this.revision,
        limits: this.limits,
        protocol: this.input.protocol ?? 'lx',
        network: typeof this.input.network === 'function',
        storage: Boolean(this.input.storage),
        observeScriptErrors: typeof this.input.onScriptError === 'function',
        ...this.startParams,
      };
      const value = await this.#call('start', startParams,
        this.input.startSignal, this.limits.initTimeoutMs, 'initialization',
        { awaitProbe: true });
      this.capabilities = clone(value.capabilities ?? {});
      this.alert = clone(value.alert ?? null);
      if (value.cat) this.catSnapshot = clone(value.cat);
      this.statsSnapshot = { ...clone(value.stats ?? this.statsSnapshot), live: this.live };
      const cat = clone(this.catSnapshot ?? value.cat ?? null);
      this.input.onStart?.(this, cat);
      return this;
    } catch (error) {
      const termination = this.#terminate();
      const result = await termination;
      this.#notifyFailure(safeError(error), { ready: false });
      if (result.error) throw fault('SCRIPT_CLEANUP');
      throw safeError(error);
    }
  }

  activate() {
    return this.#call('activate', { installationId: this.installationId, generation: this.generation },
      undefined, this.limits.initTimeoutMs, 'initialization', { awaitProbe: true })
      .then(() => {
        this.activated = true;
      });
  }

  async invoke(value, options = {}) {
    if (!this.live) throw fault('STALE_SESSION');
    const associationId = `invocation:${this.nextId}`;
    const linked = this.#linkedSignal(options.signal);
    const lease = { installationId: this.installationId, generation: this.generation,
      associationId, active: true };
    this.leases.set(associationId, lease);
    try {
      const response = await this.#call('invoke', {
        installationId: this.installationId,
        request: clone(value),
        options: { timeoutMs: options.timeoutMs, associationId },
      }, linked.signal, options.timeoutMs ?? this.limits.requestTimeoutMs,
        'invocation', { associationId });
      return this.#workerResult('invoke', response);
    } finally {
      linked.release();
      this.#fenceLease(associationId);
    }
  }

  async request(method, params, { signal, timeoutMs, phase = null } = {}) {
    const linked = this.#linkedSignal(signal);
    try {
      const response = await this.#call(method, {
        installationId: this.installationId, generation: this.generation,
        ...(params ?? {}),
      }, linked.signal, timeoutMs ?? this.limits.requestTimeoutMs, phase);
      return this.#workerResult(method, response);
    } finally {
      linked.release();
    }
  }

  #linkedSignal(signal) {
    if (!signal) return { signal: this.lifecycle.signal, release: () => {} };
    const controller = new AbortController();
    const abort = () => controller.abort(signal.reason ?? this.lifecycle.signal.reason ?? fault('CANCELLED'));
    const lifecycle = () => controller.abort(this.lifecycle.signal.reason ?? fault('STALE_SESSION'));
    signal.addEventListener('abort', abort, { once: true });
    this.lifecycle.signal.addEventListener('abort', lifecycle, { once: true });
    if (signal.aborted) abort();
    else if (this.lifecycle.signal.aborted) lifecycle();
    return {
      signal: controller.signal,
      release: () => {
        try { signal.removeEventListener('abort', abort); } catch {}
        try { this.lifecycle.signal.removeEventListener('abort', lifecycle); } catch {}
      },
    };
  }

  publishSettings(value) {
    const { settings, aggregate } = value ?? {};
    if (settings !== undefined) this.startParams.settings = { value: clone(settings) };
    if (aggregate !== undefined) this.startParams.aggregate = clone(aggregate);
    if (!this.worker || this.stopRequested || !this.live) return;
    // Settings use the same ordered MessagePort as requests. This event has
    // no reply, so a stuck worker cannot accumulate pending publishes, while
    // any later request observes the snapshot because it is enqueued after it.
    const message = event('settings', {
      installationId: this.installationId,
      ...(settings !== undefined ? { settings: clone(settings) } : {}),
      ...(aggregate !== undefined ? { aggregate: clone(aggregate) } : {}),
    });
    try { this.#send(message); }
    catch { /* A stopped worker has no later call to observe settings. */ }
  }

  stop(reason = fault('STALE_SESSION')) {
    if (this.stopping) return this.stopping;
    this.stopRequested = true;
    this.live = false;
    this.lifecycle.abort(reason);
    if (!this.proxyLifecycle.signal.aborted) this.proxyLifecycle.abort(reason);
    this.#fenceLeases();
    this.#abortAdapters();
    if (this.#termination) {
      this.stopping = this.#termination.then(result => {
        if (result.error) throw result.error;
      });
      this.stopping.catch(() => {});
      return this.stopping;
    }
    if (!this.worker) {
      // No thread was spawned, or a previous caller already captured its exit.
      this.stopping = Promise.resolve();
      this.stopping.catch(() => {});
      return this.stopping;
    }
    this.stopping = (async () => {
      const deadline = performance.now() + this.limits.cleanupTimeoutMs;
      let lifecycleFailure = null;
      try {
        await this.#call('stop', {
          installationId: this.installationId,
          generation: this.generation,
          reason: safeError(reason).code,
        }, undefined, this.limits.cleanupTimeoutMs);
      } catch (error) {
        // A termination already in progress is the authoritative exit path.
        if (!this.#termination) lifecycleFailure = safeError(error);
      }
      const result = await this.#terminate({ graceful: !lifecycleFailure,
        exitTimeoutMs: Math.max(0, deadline - performance.now()) });
      if (result.error) throw result.error;
      if (lifecycleFailure && lifecycleFailure.code !== 'TIMEOUT') {
        this.cleanupError = lifecycleFailure.code;
        throw lifecycleFailure;
      }
      if (lifecycleFailure) this.cleanupError ??= lifecycleFailure.code;
    })();
    this.stopping.catch(() => {});
    return this.stopping;
  }

  async drainAdapters() {
    // Physical adapters are tracked by this session until they really settle.
    // Stop must remain bounded even when an adapter ignores AbortSignal.
  }

  stats() {
    if (this.worker && this.live && !this.stopRequested && !this.statsRequest) {
      // Reads remain synchronous snapshots. A demand-driven refresh uses one
      // existing RPC, not a heartbeat or an unbounded queue of stats requests.
      this.statsRequest = this.#call('stats', { installationId: this.installationId,
        generation: this.generation }, undefined, this.limits.cleanupTimeoutMs)
        .then(value => {
          if (this.live) this.statsSnapshot = { ...clone(value), live: true };
        }).catch(() => {}).finally(() => { this.statsRequest = null; });
    }
    return { ...this.statsSnapshot, live: this.live,
      pendingAdapterTasks: this.adapterTasks.size,
      ...(this.cleanupError ? { cleanupError: this.cleanupError } : {}) };
  }

  endpoint() {
    requireValue(this.catSnapshot?.endpoint, 'DISABLED');
    return clone(this.catSnapshot.endpoint);
  }

  #workerResult(method, value) {
    if (this.input.processWorkerResult) {
      const processed = this.input.processWorkerResult(method, value, this);
      if (processed?.[processedWorkerResult]) return processed.value;
    }
    return clone(value);
  }

  async whenAdaptersIdle() {
    if (!this.adapterTasks.size) return;
    await Promise.allSettled([...this.adapterTasks]);
  }

  adapterRetirement() {
    return { installationId: this.installationId, generation: this.generation,
      pendingAdapterTasks: () => this.adapterTasks.size,
      idle: Promise.allSettled([...this.adapterTasks]).then(() => {}) };
  }

  #rejectStoppedStart() {
    if (this.stopRequested || this.stopping || !this.live) throw fault('STALE_SESSION');
    if (this.input.startSignal?.aborted)
      throw safeError(this.input.startSignal.reason, 'HOST_CLOSED');
  }

  async #call(method, params, signal, timeoutMs, phase = null,
    { awaitProbe = false, associationId = null } = {}) {
    if (!this.worker) throw fault('STALE_SESSION');
    const id = this.nextId++;
    const { entry, pending } = this.#pendingEntry(id, associationId);
    let requestSent = false;
    if (signal) {
      const abort = () => {
        const error = signal.reason instanceof ScriptHostError
          ? signal.reason : fault('CANCELLED');
        if (requestSent) this.#sendSafely(cancel(id, safeError(error, 'CANCELLED').code));
        pending.settle(error);
      };
      signal.addEventListener('abort', abort, { once: true });
      pending.cleanup = () => signal.removeEventListener('abort', abort);
      if (signal.aborted) {
        abort();
        return entry.promise;
      }
    }
    pending.timer = timeoutMs ? setTimeout(() => {
      if (pending.settled) return;
      const error = fault('TIMEOUT');
      this.#sendSafely(cancel(id, 'TIMEOUT'));
      pending.detach(error);
      if (phase) {
        const probe = this.#probeAfterTimeout(phase);
        if (awaitProbe) {
          const finish = () => pending.settle(error);
          probe.then(finish, finish);
        } else {
          pending.settle(error);
          probe.catch(() => {});
        }
      } else {
        pending.settle(error);
      }
    }, timeoutMs) : null;
    this.pending.set(id, pending);
    try {
      this.#send(request(id, method, params));
      requestSent = true;
    } catch (error) { pending.settle(safeError(error)); }
    return entry.promise;
  }

  async #probeAfterTimeout(phase) {
    if (!this.worker) return;
    const id = this.nextId++;
    const { entry, pending } = this.#pendingEntry(id, null);
    const timer = setTimeout(() => {
      if (pending.settled) return;
      Promise.resolve(this.#unresponsive(fault('TIMEOUT'), phase)).then(
        () => pending.settle(null),
        error => pending.settle(safeError(error)),
      );
    }, this.limits.cleanupTimeoutMs);
    pending.timer = timer;
    this.pending.set(id, pending);
    try {
      this.#send(request(id, 'probe', { installationId: this.installationId,
        generation: this.generation }));
    } catch (error) { pending.settle(safeError(error)); }
    return entry.promise;
  }

  #pendingEntry(id, associationId) {
    const entry = deferred();
    const pending = {
      id, settled: false, detached: false, timer: null, cleanup: () => {},
      associationId, settle: null,
    };
    pending.detach = reason => {
      if (pending.detached) return;
      pending.detached = true;
      this.pending.delete(id);
      clearTimeout(pending.timer);
      pending.cleanup();
      if (pending.associationId) this.#fenceLease(pending.associationId, reason);
      this.#scheduleFailureTermination();
    };
    pending.settle = (error, value) => {
      if (pending.settled) return;
      pending.detach(error);
      pending.settled = true;
      if (error) entry.reject(error);
      else entry.resolve(value);
    };
    return { entry, pending };
  }

  async #unresponsive(error, phase) {
    if (!this.worker) return;
    this.unresponsive = true;
    this.failure = safeError(error);
    try { this.failure.workerUnresponsive = true; } catch { /* The code is authoritative. */ }
    const ready = this.activated;
    this.#diagnostic('worker.unresponsive', this.failure.code,
      ready ? 'running' : phase);
    const result = await this.#terminate();
    if (result.error) {
      this.cleanupError = safeError(result.error).code;
      this.#diagnostic('worker.terminate', this.cleanupError, phase);
    }
    this.#notifyFailure(this.failure, { ready });
  }

  #diagnostic(operation, code, phase = this.activated ? 'running' : 'initialization') {
    try {
      this.input.onDiagnostic?.(Object.freeze({
        installationId: this.installationId, generation: this.generation,
        kind: 'ScriptHostError', reason: 'host', operation, code, phase,
      }));
    } catch { /* Diagnostics cannot alter lifecycle behavior. */ }
  }

  #send(message, transfer = []) {
    const worker = this.worker;
    if (!worker) throw fault('STALE_SESSION');
    try { worker.postMessage(message, transfer); }
    catch (error) { throw safeError(error); }
  }

  #sendSafely(message, transfer = []) {
    try { this.#send(message, transfer); }
    catch { /* Cancellation and stale responses cannot replace the settlement. */ }
  }

  #message(raw) {
    let message;
    try { message = validateMessage(raw); }
    catch (error) {
      if (this.live) this.#failed(safeError(error, 'SCRIPT_PROTOCOL_ERROR'), 'worker.exit');
      return;
    }
    if (message.kind === 'event') { this.#event(message); return; }
    if (message.kind === 'request') { this.#adapter(message); return; }
    if (message.kind === 'cancel') {
      this.adapters.get(message.id)?.controller.abort(fault(message.code));
      return;
    }
    if (message.kind !== 'response') return;
    const pending = this.pending.get(message.id);
    if (!pending || pending.settled) return;
    // A worker answer (including an HTTP error response) completes the wait;
    // it is not a caller cancellation. Worker-owned transport failures still
    // cancel their adapters explicitly, while storage always loses its lease.
    pending.detach();
    if (message.ok) pending.settle(null, message.value);
    else pending.settle(errorFromResponse(message));
  }

  #event(message) {
    const value = message.value;
    if (!value || value.installationId !== this.installationId ||
      value.generation !== this.generation) return;
    switch (message.event) {
      case 'endpoints':
        if (!this.live) break;
        this.#updateCatSnapshot(value);
        this.input.onEndpointsChanged?.(this, clone(value));
        break;
      case 'network':
        if (!this.live) break;
        try { this.input.onNetwork?.(value); } catch {}
        break;
      case 'activated':
        if (!this.live) break;
        this.#updateCatSnapshot(value);
        this.activated = true;
        this.input.onActivated?.(this, clone(value));
        break;
      case 'capabilities':
        this.capabilities = clone(value.capabilities ?? {});
        this.alert = clone(value.alert ?? null);
        break;
      case 'alert':
        this.alert = clone(value.alert ?? null);
        break;
      case 'failure':
        this.failure = fault(value.code);
        this.memoryLimitExceeded = Boolean(value.memoryLimitExceeded);
        if (this.memoryLimitExceeded) this.failure.memoryLimitExceeded = true;
        this.live = false;
        this.stopRequested = true;
        this.lifecycle.abort(this.failure);
        if (!this.proxyLifecycle.signal.aborted) this.proxyLifecycle.abort(this.failure);
        this.#fenceLeases();
        this.#abortAdapters();
        this.#completePending(this.failure);
        this.#notifyStopped();
        this.#notifyFailure(this.failure, { ready: value.phase === 'running' });
        this.#scheduleFailureTermination();
        break;
      case 'diagnostic':
        try { this.input.onDiagnostic?.(value); } catch {}
        break;
      case 'scriptError':
        try { this.input.onScriptError?.(value); } catch {}
        break;
      case 'stopped':
        this.live = false;
        this.stopped = true;
        this.#fenceLeases();
        this.#abortAdapters();
        this.#notifyStopped();
        break;
      case 'stats':
        {
          const { installationId, generation, ...stats } = value;
          this.statsSnapshot = { ...clone(stats), live: this.live };
        }
        break;
      default:
        break;
    }
  }

  #updateCatSnapshot(value) {
    if (!Array.isArray(value?.endpoints)) return;
    this.catSnapshot = clone({
      endpoint: value.endpoint,
      access: value.access,
      endpoints: value.endpoints,
      aggregatePort: value.aggregatePort,
    });
  }

  #adapter(message) {
    if (!this.worker || this.stopRequested || !this.live || this.#termination) {
      this.#sendSafely({ version: 1, kind: 'response', id: message.id, ok: false,
        code: 'STALE_SESSION' });
      return;
    }
    if (message.method === 'network') {
      this.#trackAdapter(this.#network(message));
      return;
    }
    if (message.method?.startsWith('storage.')) {
      this.#trackAdapter(this.#storage(message));
    }
  }

  #trackAdapter(task) {
    this.adapterTasks.add(task);
    const settled = () => this.adapterTasks.delete(task);
    Promise.resolve(task).then(settled, settled).catch(() => {});
    return task;
  }

  async #network(message) {
    const params = message.params ?? {};
    const controller = new AbortController();
    this.adapters.set(message.id, { controller, associationId: params.associationId, network: true });
    try {
      if (params.associationId)
        requireValue(this.leases.get(params.associationId)?.active, 'STALE_SESSION');
      if (typeof this.input.network !== 'function') throw fault('PERMISSION_DENIED');
      const result = await this.input.network({
        url: params.url,
        options: clone(params.options ?? {}),
        signal: controller.signal,
        scope: clone(params.scope ?? {}),
        validateURL: value => this.#validateURL(value, controller.signal),
      });
      const body = result?.body === undefined ? new ArrayBuffer(0)
        : result.body instanceof ArrayBuffer ? result.body.slice(0)
          : ArrayBuffer.isView(result.body)
            ? result.body.buffer.slice(result.body.byteOffset, result.body.byteOffset + result.body.byteLength)
            : new TextEncoder().encode(String(result.body)).buffer;
      this.#sendSafely(response(message.id, {
        statusCode: result?.statusCode, statusMessage: result?.statusMessage,
        headers: result?.headers, url: result?.url,
      }, [body]), [body]);
    } catch (error) {
      this.#sendSafely({ version: 1, kind: 'response', id: message.id, ok: false,
        code: safeError(error, 'NETWORK_ERROR').code });
    } finally {
      this.adapters.delete(message.id);
    }
  }

  async #storage(message) {
    const operation = message.method.slice('storage.'.length);
    const params = message.params ?? {};
    const lease = this.leases.get(params.associationId);
    const controller = new AbortController();
    this.adapters.set(message.id, { controller, associationId: params.associationId });
    try {
      if (!lease?.active || !this.live) throw fault('STALE_SESSION');
      const storage = this.input.storage;
      const adapter = storage?.[operation];
      if (typeof adapter !== 'function') throw fault('PERMISSION_DENIED');
      const input = {
        installationId: this.installationId, generation: this.generation,
        siteId: params.siteId, requestId: params.requestId, key: params.key,
        signal: controller.signal,
        isCurrent: () => this.live && lease.active && !controller.signal.aborted,
      };
      if (operation === 'set') input.value = clone(params.value);
      this.#sendSafely(response(message.id,
        clone(await Reflect.apply(adapter, storage, [input]) ?? null)));
    } catch (error) {
      this.#sendSafely({ version: 1, kind: 'response', id: message.id, ok: false,
        code: safeError(error, 'STORAGE_ERROR').code });
    } finally {
      this.adapters.delete(message.id);
    }
  }

  #validateURL(value, signal) {
    requireValue(this.live && !signal.aborted, 'STALE_SESSION');
    let url;
    try { url = new URL(value); } catch { throw fault('INVALID_ARGUMENT'); }
    requireValue((url.protocol === 'https:' || url.protocol === 'http:') &&
      url.username === '' && url.password === '');
    return url.href;
  }

  #fenceLease(associationId, reason) {
    const lease = this.leases.get(associationId);
    if (lease) {
      lease.active = false;
      this.leases.delete(associationId);
      for (const adapter of this.adapters.values()) {
        if (adapter.associationId !== associationId) continue;
        // Storage privileges always expire with the call. A normally returned
        // Cat call must not abort a still-running script network operation;
        // explicit cancellation retains the pending transport fence.
        if (adapter.network && !reason) adapter.associationId = undefined;
        else adapter.controller.abort(reason ?? fault('STALE_SESSION'));
      }
    }
  }

  #fenceLeases() {
    for (const lease of this.leases.values()) lease.active = false;
    this.leases.clear();
  }

  #abortAdapters() {
    for (const adapter of [...this.adapters.values()]) {
      try { adapter.controller.abort(fault('CANCELLED')); } catch {}
    }
    this.adapters.clear();
  }

  #completePending(error) {
    for (const pending of [...this.pending.values()]) {
      pending.settle(error);
    }
  }

  #scheduleFailureTermination() {
    if (this.live || !this.failure || this.pending.size) return;
    setImmediate(() => {
      if (!this.live && this.failure && !this.pending.size)
        this.#terminate().catch(() => {});
    });
  }

  #notifyFailure(error, state) {
    if (this.failureNotified) return;
    this.failureNotified = true;
    try { this.input.onFailure?.(error, state, this); }
    catch { /* Observation cannot alter state. */ }
  }

  #notifyStopped() {
    if (this.stoppedNotified) return;
    this.stoppedNotified = true;
    this.stopped = true;
    try {
      this.input.onStopped?.(this, {
        installationId: this.installationId, generation: this.generation,
      });
    } catch { /* Observation cannot alter lifecycle state. */ }
  }

  #failed(error, operation = 'worker.exit') {
    const wasLive = this.live;
    this.live = false;
    this.stopRequested = true;
    this.lifecycle.abort(error);
    if (!this.proxyLifecycle.signal.aborted) this.proxyLifecycle.abort(error);
    this.failure = safeError(error);
    if (wasLive) this.#diagnostic(operation, this.failure.code);
    this.#fenceLeases();
    this.#abortAdapters();
    this.#completePending(this.failure);
    if (wasLive) this.#notifyFailure(this.failure, { ready: this.activated });
    this.#notifyStopped();
    this.#terminate().catch(() => {});
  }

  #exited(worker, code) {
    if (this.worker === worker) this.worker = null;
    this.exitCode = code;
    this.#retireStats();
    this.stopRequested = true;
    if (this.live) this.#failed(fault('SCRIPT_EXCEPTION'), 'worker.exit');
  }

  #retireStats() {
    this.statsSnapshot = { ...this.statsSnapshot, timers: 0, networkRequests: 0,
      invocations: 0, pendingAdapterTasks: 0,
      ...(Object.hasOwn(this.statsSnapshot, 'listeners') ? { listeners: 0 } : {}),
      ...(Object.hasOwn(this.statsSnapshot, 'nativeHandles') ? { nativeHandles: 0 } : {}),
      live: false };
  }

  async #waitForExit(worker, timeoutMs) {
    if (worker.threadId === -1) return true;
    let listener, timer;
    const exited = new Promise(resolve => {
      listener = () => resolve(true);
      worker.once('exit', listener);
    });
    try {
      return await Promise.race([
        exited,
        new Promise(resolve => { timer = setTimeout(() => resolve(false), timeoutMs); }),
      ]);
    } finally {
      clearTimeout(timer);
      if (listener) worker.off('exit', listener);
    }
  }

  async #terminate({ graceful = false, exitTimeoutMs = this.limits.cleanupTimeoutMs } = {}) {
    const worker = this.worker;
    this.#termination ??= (async () => {
      this.live = false;
      this.stopRequested = true;
      this.lifecycle.abort(fault('STALE_SESSION'));
      if (!this.proxyLifecycle.signal.aborted)
        this.proxyLifecycle.abort(fault('STALE_SESSION'));
      this.#fenceLeases();
      this.#abortAdapters();
      this.#completePending(fault('STALE_SESSION'));
      if (!worker) {
        this.#retireStats();
        this.#notifyStopped();
        return { error: null };
      }
      let error = null;
      let exited = false;
      if (graceful) {
        try { exited = await this.#waitForExit(worker, exitTimeoutMs); }
        catch (failure) { error = safeError(failure); }
      }
      if (!exited && !error) {
        try { await worker.terminate(); }
        catch (failure) { error = safeError(failure); }
      }
      if (error) {
        this.cleanupError = error.code;
        this.#diagnostic('worker.terminate', error.code);
      } else {
        if (this.worker === worker) this.worker = null;
        this.#retireStats();
      }
      this.#notifyStopped();
      return { error };
    })();
    const result = await this.#termination;
    return result;
  }
}
