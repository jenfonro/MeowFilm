import { NativeEngine } from './native-engine.js';
import { LXAdapterOwner } from './lx-native-adapters.js';
import { lxPayload } from './lx-capabilities.js';
import { realmSource } from './realm.js';
import { ResourceScope } from './scope.js';
import { clone, fault, requireValue } from './errors.js';

const nativeError = error => fault(error?.code ?? 'SCRIPT_EXCEPTION');

/** One LX runtime and native thread, owned from creation through actual exit. */
export class NativeSession extends LXAdapterOwner {
  async start() {
    this.assertLive();
    this.starting = true;
    const initialization = new ResourceScope(this.root.signal, this.limits.initTimeoutMs);
    try {
      this.engine = NativeEngine.create({
        source: realmSource, maxStackBytes: this.limits.maxStackBytes,
        timeoutMs: this.limits.initTimeoutMs, signal: initialization.signal,
        cleanupTimeoutMs: this.limits.cleanupTimeoutMs,
        onHostCall: (operation, input, scope) => this.hostCall(operation, input, scope),
        onEvent: (name, value, scope) => this.event(name, value, scope),
      });
      this.engine.exited.then(() => {
        if (this.live && !this.starting) this.fail(fault('SCRIPT_EXCEPTION'), 'native.exit');
      });
      await initialization.race(this.engine.ready);
      await initialization.race(this.engine.call('start', [
        this.revision.config, this.scriptInfo(),
        typeof this.onScriptError === 'function',
      ], { signal: initialization.signal, timeoutMs: this.limits.initTimeoutMs }));
      this.assertLive();
      requireValue(this.initialized, 'SCRIPT_PROTOCOL_ERROR');
      return this;
    } catch (error) {
      const failure = this.failure ?? (initialization.signal.aborted ? initialization.reason : nativeError(error));
      this.fail(failure, failure.code === 'TIMEOUT' ? 'native.unresponsive' : 'session.failed');
      try { await this.stop(failure); } catch { /* Preserve the initialization failure. */ }
      throw failure;
    } finally { this.starting = false; initialization.close(); }
  }

  activate() { this.assertLive(); this.activated = true; }

  event(name, value, scope) {
    if (!this.live) return;
    if (name === 'send') {
      try { this.send(value.event, value.data); }
      catch { this.fail(fault('SCRIPT_PROTOCOL_ERROR')); }
    } else if (name === 'diagnostic') this.diagnostic(value, scope);
    else if (name === 'hostError') this.hostDiagnostic(value.operation, fault(value.code), scope);
    else if (name === 'failure') this.fail(nativeError(value), 'native.unresponsive');
    else if (name === 'startupFailure') this.fail(nativeError(value));
    else if (name === 'protocolFailure') this.fail(fault('SCRIPT_PROTOCOL_ERROR'));
    else if (name === 'callbackError') {
      try {
        this.onDiagnostic?.(Object.freeze({ ...this.scopeIdentity(),
          kind: 'ScriptCallbackError', reason: 'script', code: nativeError(value).code }));
      } catch { /* Observers do not own callbacks. */ }
    }
  }

  async invoke(request, { signal, timeoutMs, associationId } = {}) {
    this.assertLive();
    requireValue(this.activated, 'STALE_SESSION');
    const payload = lxPayload(this.capabilities, request);
    const scope = new ResourceScope(this.root.signal);
    scope.siteId = request.siteId;
    scope.requestId = ++this.nextId;
    scope.associationId = associationId ?? `invocation:${scope.requestId}`;
    this.invocations.set(scope.requestId, scope);
    let interrupted = false;
    let detach = () => {};
    if (signal) {
      const abort = () => scope.close(fault('CANCELLED'));
      if (signal.aborted) abort();
      else {
        signal.addEventListener('abort', abort, { once: true });
        detach = () => signal.removeEventListener('abort', abort);
      }
    }
    try {
      scope.assertOpen();
      const result = await scope.race(this.engine.call('dispatch', [scope.requestId, payload], {
        scope: scope.requestId, signal: scope.signal,
        timeoutMs: timeoutMs ?? this.limits.requestTimeoutMs,
      }));
      this.assertLive();
      scope.assertOpen();
      return clone(result);
    } catch (error) {
      interrupted = scope.signal.aborted || error?.code === 'CANCELLED' || error?.code === 'TIMEOUT';
      throw scope.signal.aborted ? scope.reason : nativeError(error);
    } finally {
      detach();
      scope.close();
      this.invocations.delete(scope.requestId);
      if (this.live && interrupted) this.engine.call('retire', [scope.requestId]).catch(() => {});
    }
  }

  fail(error, operation = 'session.failed') {
    if (!this.live) return;
    this.failure = nativeError(error);
    const ready = this.activated;
    this.stop(this.failure).catch(() => {});
    try {
      this.onDiagnostic?.(Object.freeze({ ...this.scopeIdentity(),
        kind: 'ScriptHostError', reason: 'host', operation,
        code: this.failure.code, phase: ready ? 'running' : 'initialization' }));
    } catch { /* Diagnostics cannot change the failure. */ }
    try { this.onFailure?.(this.failure, Object.freeze({ ready })); }
    catch { /* Failure observation cannot change owner retirement. */ }
  }

  stop(reason = fault('STALE_SESSION')) {
    if (this.stopping) return this.stopping;
    this.live = false;
    this.activated = false;
    this.stopRequested = true;
    this.root.close(reason);
    this.stopping = (async () => {
      try {
        if (this.engine) await this.engine.stop(reason.code);
      } catch {
        this.cleanupError = 'SCRIPT_CLEANUP';
        throw fault('SCRIPT_CLEANUP');
      }
    })();
    this.stopping.catch(() => {});
    return this.stopping;
  }

  stats() {
    const native = this.engine?.stats() ?? {};
    if (this.live && this.engine && !this.statsRequest) {
      this.statsRequest = this.engine.stats({ refresh: true }).catch(() => {})
        .finally(() => { this.statsRequest = null; });
    }
    return { ...native, timers: native.timers ?? 0, networkRequests: this.networks.size,
      invocations: this.invocations.size, pendingAdapterTasks: this.adapterTasks.size, live: this.live,
      ...(this.cleanupError ? { cleanupError: this.cleanupError } : {}) };
  }
}
