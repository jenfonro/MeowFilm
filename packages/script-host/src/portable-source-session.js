import { createHash } from 'node:crypto';
import { lstat, readFile } from 'node:fs/promises';
import { fault, requireValue } from './errors.js';
import { NativeEngine } from './native-engine.js';
import { portableRealmSource } from './portable-realm.js';

let sdkSource;
const loadSDK = () => sdkSource ??= (async () => {
  const file = new URL('../build/Release/source-sdk.js', import.meta.url);
  const [bytes, stat, manifest] = await Promise.all([
    readFile(file),
    lstat(file),
    readFile(new URL('../build/Release/script-host-native.json', import.meta.url), 'utf8')
      .then(JSON.parse),
  ]);
  const sdkPath = process.platform === 'ios' && manifest.platform === 'ios'
    && ['iphoneos', 'iphonesimulator'].includes(manifest.target?.sdk)
    && manifest.target.arch === process.arch && manifest.target.minimumOS === '17.0'
    ? `.cache/ios-native/${manifest.target.sdk}-${manifest.target.arch === 'x64' ? 'x86_64' : manifest.target.arch}/build/Release/source-sdk.js`
    : 'packages/script-host/build/Release/source-sdk.js';
  requireValue(stat.isFile() && !stat.isSymbolicLink() &&
    manifest.sdk?.path === sdkPath &&
    manifest.sdk.bytes === bytes.length &&
    manifest.sdk.sha256 === createHash('sha256').update(bytes).digest('hex'),
  'SCRIPT_PROTOCOL_ERROR');
  return bytes.toString('utf8');
})().catch(error => { sdkSource = undefined; throw error; });
const hostError = error => fault(error?.code);

export class PortableSourceSession {
  constructor({ installationId, generation, revision, capabilities, metadata = {},
    onDiagnostic, onFailure, limits }) {
    Object.assign(this, { installationId, generation, revision, capabilitiesClient: capabilities,
      metadata, onDiagnostic, onFailure, limits });
    this.live = true;
    this.activated = false;
    this.capabilities = {};
    this.alert = null;
    this.lifecycle = new AbortController();
    this.calls = new Map();
    this.adapterTasks = new Set();
    this.adapters = new Set();
    this.nextScope = 0;
  }

  diagnostic(operation, code, phase) {
    try {
      this.onDiagnostic?.({ installationId: this.installationId, generation: this.generation,
        protocol: this.revision.protocol, operation, code, phase });
    } catch { /* Diagnostics do not own the session. */ }
  }

  async start() {
    try {
      const source = await loadSDK();
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
            // Explicit stop has already made live=false when this event arrives.
            this.nativeFailure = { scope, code: error.code };
            this.fail(error);
          } else if (name === 'source.diagnostic') {
            try { this.onDiagnostic?.({ installationId: this.installationId,
              generation: this.generation, protocol: this.revision.protocol, ...value }); } catch {}
          }
        },
      });
      this.engine.exited.then(result => {
        this.nativeExit = result;
        if (this.live) this.fail(hostError(result));
      });
      await this.engine.ready;
      const description = await this.engine.call('start', [{
        installationId: this.installationId,
        generation: this.generation,
        source: this.revision.source,
        protocol: this.revision.protocol,
        metadata: { ...this.metadata, ...this.revision.metadata, adapterVersion: this.revision.version },
        limits: {
          maximumParseBytes: 256 * 1024 * 1024,
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

  hostCall(operation, payload, _scope, { signal }) {
    requireValue(operation === 'source.capability' && Array.isArray(payload));
    const [name, scope, keys, ...values] = payload;
    requireValue(typeof name === 'string' && Array.isArray(keys) && keys.length === values.length);
    requireValue(this.live || this.cleaning && name === 'virtual.unregister', 'STALE_SESSION');
    requireValue(scope?.installationId === this.installationId &&
      scope?.generation === this.generation && scope?.siteId === 'source',
    'PERMISSION_DENIED');
    const input = Object.fromEntries(keys.map((key, index) => [key, values[index]]));
    const controller = new AbortController();
    const abort = () => controller.abort(hostError(signal.reason));
    if (signal.aborted) abort();
    else signal.addEventListener('abort', abort, { once: true });
    this.adapters.add(controller);
    const task = Promise.resolve().then(() => {
      if (controller.signal.aborted) throw controller.signal.reason;
      return this.capabilitiesClient.call(name, input, {
        signal: controller.signal, scope,
      });
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
      const result = await this.engine.call('invoke', [
        request.operation, request.payload ?? {}, timeoutMs,
      ], { scope, signal: controller.signal, timeoutMs });
      requireValue(this.live, 'STALE_SESSION');
      if (controller.signal.aborted) throw fault('CANCELLED');
      return result;
    } catch (error) {
      if (!this.live) throw this.failure ?? fault('STALE_SESSION');
      // A native pending deadline can precede the rule timer. Retire only this
      // scope; owner failures arrive through the failure event or real exit.
      if (error.code === 'TIMEOUT') {
        this.engine.call('cancel', [scope, 'TIMEOUT']).catch(() => {});
      }
      throw hostError(error);
    } finally {
      signal?.removeEventListener('abort', abort);
      this.calls.delete(scope);
    }
  }

  fail(error) {
    if (!this.live) return;
    const ready = this.activated;
    this.failure = hostError(error);
    this.diagnostic('native.unresponsive', this.failure.code, ready ? 'running' : 'initialization');
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
      // exited is the native thread/Runtime completion; external I/O remains
      // explicitly registered below when an adapter ignores cancellation.
      const exit = await this.engine.exited;
      await this.drainAdapters(Math.max(0, deadline - Date.now()));
      if (closeError) {
        // Admission can fail before Node receives the earlier action's failure.
        // Only that scoped failure and actual exit prove a collateral rejection.
        // A TIMEOUT returned by close before exit remains a cleanup failure.
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
    let timer;
    try {
      await Promise.race([
        Promise.allSettled([...this.adapterTasks]),
        new Promise(resolve => { timer = setTimeout(resolve, timeoutMs); }),
      ]);
    } finally { clearTimeout(timer); }
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
