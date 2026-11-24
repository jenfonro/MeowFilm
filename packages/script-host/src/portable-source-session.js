import { DEFAULT_SOURCE_LIMITS } from '@meowfilm/source-runtime';
import { fault, requireValue } from './errors.js';
import { NativeEngine } from './native-engine.js';
import { loadSourceSDK } from './native-loader.js';
import { portableRealmSource } from './portable-realm.js';
import { raceSignal, waitWithTimeout } from './scope.js';
import { parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';

const hostError = error => fault(error?.code);

class PortableSourceSession {
  constructor({ installationId, generation, revision, capabilities, metadata = {},
    onDiagnostic, onScriptError, onFailure, limits }) {
    Object.assign(this, {
      installationId, generation, revision, capabilitiesClient: capabilities,
      metadata, onDiagnostic, onScriptError, onFailure, limits,
    });
    this.live = true;
    this.activated = false;
    this.capabilities = {};
    this.lifecycle = new AbortController();
    this.calls = new Map();
    this.adapterTasks = new Set();
    this.adapters = new Set();
    this.nextScope = 0;
  }

  diagnostic(operation, code, phase, extra = null) {
    try {
      this.onDiagnostic?.({
        installationId: this.installationId, generation: this.generation,
        protocol: this.revision.protocol, operation, code, phase,
        ...(extra ?? {}),
      });
    } catch { /* Diagnostics do not own the session. */ }
  }

  reportScriptError(detail, scopeId = 0) {
    if (typeof this.onScriptError !== 'function' || !detail || typeof detail !== 'object') return;
    const message = typeof detail.message === 'string' ? detail.message : String(detail.code ?? 'SCRIPT_EXCEPTION');
    const raw = { message };
    for (const key of ['name', 'stack', 'code']) {
      if (typeof detail[key] === 'string' && detail[key]) raw[key] = detail[key];
    }
    try {
      this.onScriptError(Object.freeze({
        installationId: this.installationId,
        generation: this.generation,
        protocol: this.revision.protocol,
        requestId: scopeId || null,
        kind: detail.kind || detail.name || 'Error',
        reason: detail.reason || 'script',
        code: hostError(detail).code,
        ...(detail.operation ? { operation: detail.operation } : {}),
        message,
        raw,
      }));
    } catch { /* Script error observation does not own the session. */ }
  }

  async start() {
    try {
      const source = await loadSourceSDK();
      requireValue(this.live, 'STALE_SESSION');
      this.engine = NativeEngine.create({
        source,
        guestSource: portableRealmSource,
        guestHelpers: ['bridge', 'utility'],
        maxStackBytes: this.limits.maxStackBytes,
        signal: this.lifecycle.signal,
        timeoutMs: this.limits.initTimeoutMs,
        cleanupTimeoutMs: this.limits.cleanupTimeoutMs,
        onHostCall: (...args) => this.hostCall(...args),
        onEvent: (name, value, scope) => {
          if (name === 'failure') {
            const error = hostError(value);
            this.nativeFailure = { scope, code: error.code };
            this.fail(error, value?.stats ?? null);
          } else if (name === 'diagnostic' && value && typeof value === 'object' && value.kind === 'timer') {
            this.diagnostic(value.operation || 'setTimeout', hostError(value).code,
              this.activated ? 'running' : 'initialization', { kind: 'timer', reason: 'script', requestId: scope || null });
            this.reportScriptError(value, scope);
          } else if (name === 'source.diagnostic') {
            try {
              this.onDiagnostic?.({
                installationId: this.installationId,
                generation: this.generation,
                protocol: this.revision.protocol,
                ...value,
              });
            } catch {}
          } else if (name === 'source.scriptError') {
            this.reportScriptError(value, value?.scopeId ?? scope);
          }
        },
      });
      this.engine.exited.then(result => {
        this.nativeExit = result;
        if (this.live) this.fail(hostError(result), result);
      });
      await this.engine.ready;
      const description = await this.engine.call('start', [{
        installationId: this.installationId,
        generation: this.generation,
        source: this.revision.source,
        protocol: this.revision.protocol,
        metadata: { ...this.metadata, ...this.revision.metadata, adapterVersion: this.revision.version },
        observeScriptErrors: typeof this.onScriptError === 'function',
        limits: {
          ...DEFAULT_SOURCE_LIMITS,
          requestTimeoutMs: this.limits.requestTimeoutMs,
          cleanupTimeoutMs: this.limits.cleanupTimeoutMs,
        },
      }], { scope: 0, signal: this.lifecycle.signal, timeoutMs: this.limits.initTimeoutMs });
      requireValue(this.live, 'STALE_SESSION');
      this.capabilities = { source: description };
      return this;
    } catch (error) {
      const failure = this.failure ?? (!this.live ? fault('STALE_SESSION') : hostError(error));
      if (failure.code === 'TIMEOUT' && !this.failure) {
        this.diagnostic('native.unresponsive', 'TIMEOUT', 'initialization');
      }
      await this.stop().catch(() => {});
      throw failure;
    }
  }

  async activate() {
    requireValue(this.live && this.engine, 'STALE_SESSION');
    await this.engine.call('activate');
    requireValue(this.live, 'STALE_SESSION');
    this.activated = true;
  }

  hostCall(operation, payload, scopeId, { signal }) {
    requireValue(operation === 'source.capability' && Array.isArray(payload));
    const [name, scope, keys, ...values] = payload;
    requireValue(typeof name === 'string' && Array.isArray(keys) && keys.length === values.length);
    requireValue(this.live || this.cleaning && name === 'virtual.unregister', 'STALE_SESSION');
    requireValue(scope?.installationId === this.installationId &&
      scope?.generation === this.generation && scope?.siteId === 'source',
    'PERMISSION_DENIED');
    const input = Object.fromEntries(keys.map((key, index) => [key, values[index]]));
    if (name === 'storage.set') input.value = parseJSONText(input.value);
    if (name === 'reader.values.apply') input.entries = parseJSONText(input.entries, -2);
    const controller = new AbortController();
    const abort = () => controller.abort(hostError(signal.reason));
    if (signal.aborted) abort();
    else signal.addEventListener('abort', abort, { once: true });
    this.adapters.add(controller);
    const enrichedScope = { ...scope, protocol: this.revision.protocol, requestId: scopeId || null };
    const task = Promise.resolve().then(async () => {
      if (controller.signal.aborted) throw controller.signal.reason;
      const result = await this.capabilitiesClient.call(name, input, {
        signal: controller.signal, scope: enrichedScope,
      });
      if (name === 'reader.values.read') return stringifyJSONValue(result, -3);
      return name === 'storage.get' ? stringifyJSONValue(result) : result;
    }).finally(() => {
      signal.removeEventListener('abort', abort);
      this.adapters.delete(controller);
      this.adapterTasks.delete(task);
    });
    this.adapterTasks.add(task);
    return task;
  }

  async invoke(request, { signal, timeoutMs = this.limits.requestTimeoutMs } = {}) {
    requireValue(this.live && this.activated, 'STALE_SESSION');
    requireValue(request?.siteId === 'source');
    requireValue(Number.isSafeInteger(timeoutMs) && timeoutMs > 0 &&
      timeoutMs <= this.limits.requestTimeoutMs);
    const scope = ++this.nextScope;
    const controller = new AbortController();
    const abort = () => {
      controller.abort(fault('CANCELLED'));
      this.engine.call('cancel', [scope, 'CANCELLED']).catch(() => {});
    };
    if (signal?.aborted) abort();
    else signal?.addEventListener('abort', abort, { once: true });
    this.calls.set(scope, controller);
    try {
      if (controller.signal.aborted) throw controller.signal.reason ?? fault('CANCELLED');
      const pending = this.engine.call('invoke', [
        request.operation, request.payload ?? {}, timeoutMs,
      ], { scope, signal: controller.signal, timeoutMs });
      pending.catch(() => {});
      const result = await raceSignal(controller.signal, pending, 'CANCELLED');
      requireValue(this.live, 'STALE_SESSION');
      if (controller.signal.aborted) throw fault('CANCELLED');
      return result;
    } catch (error) {
      if (!this.live) throw this.failure ?? fault('STALE_SESSION');
      if (error.code === 'TIMEOUT') {
        this.engine.call('cancel', [scope, 'TIMEOUT']).catch(() => {});
      }
      throw hostError(error);
    } finally {
      signal?.removeEventListener('abort', abort);
      this.calls.delete(scope);
    }
  }

  fail(error, nativeStats = null) {
    if (!this.live) return;
    const ready = this.activated;
    this.failure = hostError(error);
    this.diagnostic('native.unresponsive', this.failure.code, ready ? 'running' : 'initialization',
      nativeStats ? { stats: nativeStats } : null);
    this.stop(this.failure).catch(() => {});
    try { this.onFailure?.(this.failure, { ready }); } catch {}
  }

  stop(reason = fault('STALE_SESSION')) {
    if (this.stopping) return this.stopping;
    this.live = false;
    this.activated = false;
    this.stopRequested = true;
    this.lifecycle.abort(reason);
    for (const [scope, controller] of this.calls) {
      controller.abort(reason);
      this.engine?.call('cancel', [scope, reason.code]).catch(() => {});
    }
    for (const controller of this.adapters) controller.abort(reason);
    const deadline = Date.now() + this.limits.cleanupTimeoutMs;
    this.stopping = (async () => {
      if (!this.engine) return;
      const closeScope = ++this.nextScope;
      let closeError, rejectedAfterExit = false;
      this.cleaning = true;
      if (!this.engine.closing) {
        try {
          await this.engine.call('close', [], {
            scope: closeScope, timeoutMs: this.limits.cleanupTimeoutMs,
          });
        } catch (error) {
          closeError = error;
          rejectedAfterExit = this.nativeExit !== undefined;
        }
      }
      this.cleaning = false;
      this.engine.stop(reason.code).catch(() => {});
      const exit = await this.engine.exited;
      await this.drainAdapters(Math.max(0, deadline - Date.now()));
      if (closeError) {
        const interruption = this.nativeFailure;
        const interrupted = interruption &&
          Number.isSafeInteger(interruption.scope) &&
          interruption.scope >= 0 && interruption.scope < closeScope &&
          ['CANCELLED', 'TIMEOUT'].includes(interruption.code) &&
          exit.code === interruption.code &&
          (closeError.code === 'STALE_SESSION' ||
            rejectedAfterExit && closeError.code === interruption.code);
        if (!interrupted) {
          const failure = closeError.code === 'TIMEOUT' ? fault('SCRIPT_CLEANUP') : hostError(closeError);
          this.cleanupError = failure.code;
          throw failure;
        }
      }
    })();
    this.stopping.catch(() => {});
    return this.stopping;
  }

  async drainAdapters(timeoutMs = this.stopping ? 0 : this.limits.cleanupTimeoutMs) {
    if (!this.adapterTasks.size || timeoutMs <= 0) return;
    await waitWithTimeout(Promise.allSettled([...this.adapterTasks]), timeoutMs);
    if (this.adapterTasks.size) this.cleanupError ??= 'SCRIPT_CLEANUP';
  }

  async whenAdaptersIdle() {
    while (this.adapterTasks.size) await Promise.allSettled([...this.adapterTasks]);
  }

  adapterRetirement() {
    return { pendingAdapterTasks: () => this.adapterTasks.size, idle: this.whenAdaptersIdle() };
  }

  async drain() {
    if (this.live && this.engine && !this.engine.closing) {
      await this.engine.call('drain', [], { timeoutMs: this.limits.cleanupTimeoutMs });
    }
    await this.drainAdapters();
  }

  stats() {
    const native = this.engine?.stats();
    return {
      live: this.live,
      invocations: this.calls.size,
      timers: native?.timers ?? 0,
      networkRequests: this.adapterTasks.size,
      pendingAdapterTasks: this.adapterTasks.size,
      cleanupError: this.cleanupError ?? null,
      ...(native ? { native } : {}),
    };
  }
}

export function createPortableSourceAdapter({ capabilities, metadata = {} } = {}) {
  requireValue(capabilities && typeof capabilities.call === 'function');
  requireValue(metadata && typeof metadata === 'object' && !Array.isArray(metadata));
  return input => new PortableSourceSession({ ...input, capabilities, metadata });
}
