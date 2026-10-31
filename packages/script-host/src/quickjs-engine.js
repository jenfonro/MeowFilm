import { readFile } from 'node:fs/promises';
import { newQuickJSWASMModuleFromVariant, newVariant } from 'quickjs-emscripten-core';
import RELEASE_SYNC from '@jitl/quickjs-wasmfile-release-sync';
import { fault, requireValue } from './errors.js';

let compiledPromise;
const disposeAll = handles => {
  let failure;
  for (const handle of handles) {
    try { handle.dispose(); } catch (error) { failure ??= error; }
  }
  return failure;
};
const loadModule = async () => {
  compiledPromise ??= readFile(new URL(import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')))
    .then(bytes => WebAssembly.compile(bytes));
  return newQuickJSWASMModuleFromVariant(newVariant(RELEASE_SYNC, { wasmModule: await compiledPromise }));
};

// The pinned QuickJS build returns signed values from asUintN at wider bit
// widths. Restore the standard operation in the guest before any SDK runs.
function installIntegerSemantics() {
  if (BigInt.asUintN(64, -1n) === (1n << 64n) - 1n) return;
  const asIntN = BigInt.asIntN, toBigInt = BigInt, trunc = Math.trunc;
  const { asUintN } = {
    asUintN(bits, value) {
      'use strict';
      const number = +bits;
      const width = number !== number ? 0 : trunc(number);
      if (width < 0 || width > 9007199254740991) throw new RangeError('Invalid index');
      const signed = asIntN(width, value);
      return signed < 0n ? signed + (1n << toBigInt(width)) : signed;
    },
  };
  Object.defineProperty(BigInt, 'asUintN', {
    ...Object.getOwnPropertyDescriptor(BigInt, 'asUintN'), value: asUintN,
  });
}

/** One isolated guest heap. The only host function exchanges JSON, never host objects. */
export class QuickJSEngine {
  static async create({ source, bridge, limits, enforceMemoryLimit = true }) {
    const module = await loadModule();
    const engine = new QuickJSEngine(module, limits, enforceMemoryLimit);
    try {
      engine.installBridge(bridge);
      engine.run(() => {
        engine.unwrap(engine.context.evalCode(`(${installIntegerSemantics})();`, 'engine-intrinsics.js')).dispose();
        engine.realm = engine.unwrap(engine.context.evalCode(source, 'script-sdk.js'));
      }, limits.initTimeoutMs);
      return engine;
    } catch (error) {
      try { engine.close(); } catch { /* Preserve the initialization failure. */ }
      throw error;
    }
  }

  installBridge(bridge) {
    const fn = this.context.newFunction('__meowBridge', (operation, input) => {
      let response;
      try {
        const name = this.context.getString(operation);
        const json = this.context.getString(input);
        if (Buffer.byteLength(json) > this.limits.maxBytes * 4) throw fault('RESOURCE_LIMIT');
        response = { ok: true, value: bridge(name, JSON.parse(json)) ?? null };
      } catch (error) {
        response = { ok: false, code: error?.code ?? 'SCRIPT_EXCEPTION' };
      }
      return this.context.newString(JSON.stringify(response));
    });
    try { this.context.setProp(this.context.global, '__meowBridge', fn); }
    finally { fn.dispose(); }
  }

  constructor(module, limits, enforceMemoryLimit = true) {
    requireValue(limits.maxStackBytes <= 1024 * 1024);
    this.methods = new Map();
    this.limits = limits;
    this.depth = 0;
    this.closing = false;
    this.closed = false;
    this.deadline = Infinity;
    this.interrupted = false;
    this.runtime = undefined;
    this.context = undefined;
    try {
      this.runtime = module.newRuntime();
      if (enforceMemoryLimit) this.runtime.setMemoryLimit(limits.maxMemoryBytes);
      this.runtime.setMaxStackSize(limits.maxStackBytes);
      this.context = this.runtime.newContext();
      this.runtime.setInterruptHandler(() => {
        if (Date.now() >= this.deadline) {
          this.interrupted = true;
          return true;
        }
        return false;
      });
    } catch (error) {
      try { this.context?.dispose(); } catch { /* Preserve the construction failure. */ }
      try { this.runtime?.dispose(); } catch { /* Preserve the construction failure. */ }
      this.context = undefined;
      this.runtime = undefined;
      this.closed = true;
      throw error;
    }
  }

  errorCode(handle) {
    if (this.interrupted) return 'TIMEOUT';
    if (this.realm) {
      const fn = this.context.getProp(this.realm, 'errorCode');
      try {
        const result = this.context.callFunction(fn, this.realm, handle);
        if (result.value) {
          try { return this.context.getString(result.value) || 'SCRIPT_EXCEPTION'; }
          finally { result.value.dispose(); }
        }
        result.error.dispose();
      } finally { fn.dispose(); }
    }
    return 'SCRIPT_EXCEPTION';
  }

  unwrap(result) {
    if (result.error) {
      let code;
      try { code = this.errorCode(result.error); }
      finally { result.error.dispose(); }
      throw fault(code);
    }
    return result.value;
  }

  run(body, timeoutMs = this.limits.syncTimeoutMs) {
    if (this.closed || this.closing) throw fault('STALE_SESSION');
    const previous = this.deadline;
    if (!this.depth) this.interrupted = false;
    this.deadline = Math.min(previous, Date.now() + timeoutMs);
    this.depth++;
    try {
      const result = body();
      if (this.interrupted || Date.now() >= this.deadline) {
        this.interrupted = true;
        throw fault('TIMEOUT');
      }
      return result;
    } finally {
      this.depth--;
      this.deadline = previous;
      if (!this.depth && this.closing) this.destroy();
    }
  }

  method(name) {
    if (!this.methods.has(name)) this.methods.set(name, this.context.getProp(this.realm, name));
    return this.methods.get(name);
  }

  callInside(name, args) {
    const handles = [];
    let failure;
    try {
      for (const value of args) handles.push(typeof value === 'string'
        ? this.context.newString(value) : this.context.newNumber(value));
      const value = this.unwrap(this.context.callFunction(this.method(name), this.realm, ...handles));
      value.dispose();
    } catch (error) { failure = error; }
    const cleanupFailure = disposeAll(handles);
    failure ??= cleanupFailure;
    if (failure) throw failure;
  }

  pumpInside() {
    while (!this.closing && this.runtime.hasPendingJob()) {
      if (Date.now() >= this.deadline) {
        this.interrupted = true;
        throw fault('TIMEOUT');
      }
      const result = this.runtime.executePendingJobs(1);
      if (result.error) {
        let code;
        try { code = this.errorCode(result.error); }
        finally { result.error.dispose(); }
        throw fault(code);
      }
    }
  }

  evaluate(source) {
    this.run(() => {
      const value = this.unwrap(this.context.evalCode(source, 'installed-script.js'));
      try {
        const watched = this.unwrap(this.context.callFunction(this.method('watchStartup'), this.realm, value));
        watched.dispose();
      } finally { value.dispose(); }
      this.pumpInside();
    });
  }

  call(name, args, timeoutMs = this.limits.syncTimeoutMs) {
    this.run(() => {
      this.callInside(name, args);
      this.pumpInside();
    }, timeoutMs);
  }

  /** Internal lifecycle notifications must not pump unrelated guest jobs under
   * the notification caller's host scope. */
  callUnpumped(name, args, timeoutMs = this.limits.syncTimeoutMs) {
    this.run(() => this.callInside(name, args), timeoutMs);
  }

  /** Synchronous callback return across the same JSON boundary. Jobs are pumped
   * after the native caller has registered any asynchronous callback receipt. */
  callJSON(name, args, timeoutMs = this.limits.syncTimeoutMs) {
    return this.run(() => {
      const handles = [];
      let result, failure;
      try {
        for (const value of args) handles.push(this.context.newString(value));
        const value = this.unwrap(this.context.callFunction(this.method(name), this.realm, ...handles));
        try {
          const json = this.context.getString(value);
          requireValue(Buffer.byteLength(json) <= this.limits.maxBytes * 4, 'RESOURCE_LIMIT');
          result = JSON.parse(json);
        }
        catch (error) { failure = error; }
        try { value.dispose(); } catch (error) { failure ??= error; }
      } catch (error) { failure ??= error; }
      const cleanupFailure = disposeAll(handles);
      failure ??= cleanupFailure;
      if (failure) throw failure;
      return result;
    }, timeoutMs);
  }

  pump() { this.run(() => this.pumpInside()); }

  memoryUsage() {
    const handle = this.runtime.computeMemoryUsage();
    try {
      const usage = this.context.dump(handle);
      const binary = usage.binary_object_size ?? 0;
      return { rss: usage.memory_used_size, heapTotal: usage.malloc_size,
        heapUsed: usage.memory_used_size, external: binary, arrayBuffers: binary };
    } finally { handle.dispose(); }
  }

  close() {
    if (this.closing || this.closed) return;
    this.closing = true;
    if (!this.depth) this.destroy();
  }

  destroy() {
    if (this.closed) return;
    this.closed = true;
    let failure;
    const attempt = operation => {
      try { return operation(); }
      catch (error) { failure ??= error; return undefined; }
    };
    const runtime = this.runtime;
    const previousDeadline = this.deadline;
    const deadline = Date.now() + Math.max(1, this.limits.cleanupTimeoutMs ?? 1000);
    this.deadline = Math.min(previousDeadline, deadline);
    this.interrupted = false;
    try {
      while (runtime && attempt(() => runtime.hasPendingJob()) && Date.now() < deadline) {
        const result = attempt(() => runtime.executePendingJobs(1));
        if (!result) break;
        if (result.error) {
          attempt(() => result.error.dispose());
          break;
        }
      }
    } finally {
      this.deadline = previousDeadline;
    }
    for (const handle of this.methods.values()) attempt(() => handle.dispose());
    this.methods.clear();
    if (this.realm) attempt(() => this.realm.dispose());
    this.realm = undefined;
    let contexts = [];
    if (runtime) {
      const values = attempt(() => runtime.contextMap.values());
      if (values) contexts = attempt(() => [...values]) ?? [];
    }
    for (const context of contexts) {
      if (attempt(() => context.alive)) attempt(() => context.dispose());
    }
    if (runtime) attempt(() => runtime.dispose());
    this.context = undefined;
    this.runtime = undefined;
    if (failure) throw failure;
  }
}
