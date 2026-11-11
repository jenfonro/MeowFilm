import { createHash } from 'node:crypto';
import { readFileSync, lstatSync } from 'node:fs';
import { createRequire } from 'node:module';
import { constants } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const artifact = new URL('../build/Release/script_host_native.node', import.meta.url);
const manifestFile = new URL('../build/Release/script-host-native.json', import.meta.url);
let binding;

function fault(code) {
  return Object.assign(new Error(code), { code });
}

function loadBinding() {
  if (binding) return binding;
  for (const path of ['../build/', '../build/Release/']) {
    const entry = lstatSync(new URL(path, import.meta.url));
    if (!entry.isDirectory() || entry.isSymbolicLink()) throw fault('NATIVE_ARTIFACT_INVALID');
  }
  const receiptStat = lstatSync(manifestFile);
  if (!receiptStat.isFile() || receiptStat.isSymbolicLink()) throw fault('NATIVE_ARTIFACT_INVALID');
  const manifest = JSON.parse(readFileSync(manifestFile, 'utf8'));
  if (process.platform === 'ios') {
    const target = manifest.target;
    if (manifest.schemaVersion !== 1 || manifest.abi !== 1 || manifest.napi !== 8
        || Number(process.versions.napi) < 8
        || manifest.platform !== 'ios' || manifest.arch !== process.arch
        || !['iphoneos', 'iphonesimulator'].includes(target?.sdk)
        || target.arch !== process.arch || target.minimumOS !== '17.0'
        || (target.sdk === 'iphoneos' && target.arch !== 'arm64')
        || !['arm64', 'x64'].includes(target.arch)
        || !/^[a-f0-9]{64}$/.test(manifest.bindingIdentity ?? '')
        || manifest.artifact?.path !== `.cache/ios-native/${target.sdk}-${target.arch === 'x64' ? 'x86_64' : target.arch}/MeowScriptHost.framework/MeowScriptHost`) {
      throw fault('NATIVE_ARTIFACT_INVALID');
    }
    const self = fileURLToPath(import.meta.url);
    const suffix = '/Runtime/node_modules/@meowfilm/script-host/src/native-engine.js';
    if (!self.endsWith(suffix)) throw fault('NATIVE_ARTIFACT_INVALID');
    const app = self.slice(0, -suffix.length);
    if (!app.endsWith('.app')) throw fault('NATIVE_ARTIFACT_INVALID');
    const appStat = lstatSync(app);
    if (!appStat.isDirectory() || appStat.isSymbolicLink()) throw fault('NATIVE_ARTIFACT_INVALID');
    // Derive the only executable location from this signed bundle resource.
    // No business argument, environment variable or receipt path chooses code.
    for (const relative of ['Runtime', 'Runtime/node_modules', 'Runtime/node_modules/@meowfilm',
      'Runtime/node_modules/@meowfilm/script-host', 'Runtime/node_modules/@meowfilm/script-host/src',
      'Frameworks', 'Frameworks/MeowScriptHost.framework']) {
      const entry = lstatSync(join(app, relative));
      if (!entry.isDirectory() || entry.isSymbolicLink()) throw fault('NATIVE_ARTIFACT_INVALID');
    }
    const executable = join(app, 'Frameworks/MeowScriptHost.framework/MeowScriptHost');
    const stat = lstatSync(executable);
    if (!stat.isFile() || stat.isSymbolicLink()) throw fault('NATIVE_ARTIFACT_INVALID');
    const module = { exports: {} };
    process.dlopen(module, executable, constants.dlopen.RTLD_NOW | constants.dlopen.RTLD_LOCAL);
    const loaded = module.exports;
    if (loaded.abi !== 1 || loaded.napi !== 8 || loaded.bindingIdentity !== manifest.bindingIdentity
        || loaded.sdk !== target.sdk || loaded.arch !== target.arch || loaded.minimumOS !== target.minimumOS) {
      throw fault('NATIVE_ABI_MISMATCH');
    }
    // Code integrity after embedding/re-signing belongs to the App signature.
    // The unsigned Mach-O hash remains a build/package check in the receipt.
    binding = loaded;
    return binding;
  }
  const stat = lstatSync(artifact);
  const bytes = readFileSync(artifact);
  if (!stat.isFile() || stat.isSymbolicLink() || manifest.schemaVersion !== 1 ||
      manifest.abi !== 1 || manifest.napi !== 8 ||
      manifest.platform !== process.platform || manifest.arch !== process.arch ||
      manifest.artifact.path !== 'packages/script-host/build/Release/script_host_native.node' ||
      manifest.artifact.bytes !== bytes.length ||
      manifest.artifact.sha256 !== createHash('sha256').update(bytes).digest('hex')) {
    throw fault('NATIVE_ARTIFACT_INVALID');
  }
  const loaded = require(fileURLToPath(artifact));
  if (loaded.abi !== 1 || loaded.napi !== 8) throw fault('NATIVE_ABI_MISMATCH');
  binding = loaded;
  return binding;
}

/** Internal launch handoff: the caller owns the copy, never the input descriptor. */
export function duplicateBootstrapDescriptor(descriptor) {
  const loaded = loadBinding();
  if (typeof loaded.duplicateBootstrapDescriptor !== 'function') throw fault('NATIVE_ABI_MISMATCH');
  return loaded.duplicateBootstrapDescriptor(descriptor);
}

/** Synchronous Unicode utility without a script owner or guest context. */
export function normalizeNFKCNative(value) {
  const loaded = loadBinding();
  if (typeof loaded.normalizeNFKC !== 'function') throw fault('NATIVE_ABI_MISMATCH');
  return loaded.normalizeNFKC(value);
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
 *
 * Trusted bootstrap globals:
 * __meowHost(op, input, scope?) -> Promise; __meowEmit(name, value);
 * __meowHostArgs(op, args, scope?) -> Promise; args itself is not a data depth layer.
 * __meowNative(op, args) -> synchronous native utility result;
 * __meowTaskContext(scope?) -> current opaque task scope;
 * __meowGuest(method, args) -> cloned guest result / Promise;
 * __meowCore(method, args) -> cloned synchronous allowlisted core helper.
 * guestHelpers is the explicit list of methods callable through __meowCore.
 * setTimeout/setInterval/clearTimeout/clearInterval execute on the owner thread.
 * Bootstrap must capture and hide privileged globals before evaluating user code.
 */
export class NativeEngine {
  static create(options) {
    return new NativeEngine(options);
  }

  constructor({
    source, guestSource, guestHelpers = [], maxStackBytes = 256 * 1024, maxHeapBytes = 0,
    onHostCall, onEvent, signal, timeoutMs = 60_000, cleanupTimeoutMs = 100,
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
        source, guestSource, guestHelpers, maxStackBytes, maxHeapBytes, timeoutMs, cleanupTimeoutMs,
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
        if (!this.closing) {
          try { this.native.complete(message.id, false, error?.code ?? 'INVALID_ARGUMENT'); }
          catch { this.stop('SCRIPT_EXCEPTION').catch(() => {}); }
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
    if (signal?.aborted) throw fault('CANCELLED');
    // Native invoke snapshots args before returning, including TypedArray views.
    // Admission does not await .ready in JS: the owner queues it behind bootstrap.
    const id = ++this.nextId;
    let abort;
    const result = new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      try {
        this.native.invoke(id, method, args, scope, timeoutMs);
        if (signal) {
          abort = () => this.native.cancel(id, 'CANCELLED');
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

  stats({ refresh = false, gc = false } = {}) {
    if (!refresh && !gc) return this.native.stats();
    if (this.closing) return Promise.resolve(this.native.stats());
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      try { this.native.refreshStats(id, gc); }
      catch (error) { this.pending.delete(id); reject(error); }
    });
  }

  stop(reason = 'STALE_SESSION') {
    if (!this.stopping) {
      this.closing = true;
      for (const { controller } of this.hostControllers.values()) controller.abort(fault(reason));
      this.native.stop(reason);
      this.stopping = this.exited.then(async stats => {
        // The adapter defines whether this is a logical operation or physical I/O;
        // physical work raced by an adapter remains that adapter's tracked debt.
        await Promise.allSettled([...this.hostTasks]);
        return stats;
      });
    }
    return this.stopping;
  }
}
