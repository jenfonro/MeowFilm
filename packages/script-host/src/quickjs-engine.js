import { createQuickJSModule } from './quickjs-module.js';
import { fault, requireValue } from './errors.js';

const disposeAll = handles => {
  let failure;
  for (const handle of handles) {
    try { handle.dispose(); } catch (error) { failure ??= error; }
  }
  return failure;
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

/** One isolated guest heap. The host bridge exchanges JSON and byte frames, never host objects. */
export class QuickJSEngine {
  static async create({ source, bridge, limits, wasmModule }) {
    const module = await createQuickJSModule(wasmModule);
    const engine = new QuickJSEngine(module, limits);
    try {
      engine.installBridge(bridge);
      engine.run(() => {
        engine.unwrap(engine.context.evalCode(`(${installIntegerSemantics})();`, 'engine-intrinsics.js')).dispose();
        engine.realm = engine.unwrap(engine.context.evalCode(source, 'script-sdk.js'));
        engine.unwrap(engine.context.evalCode('delete globalThis.__meowTaskContext;', 'engine-private.js')).dispose();
      });
      return engine;
    } catch (error) {
      try { engine.close(); } catch { /* Preserve the initialization failure. */ }
      if (engine.memoryLimitExceeded) {
        try { error.memoryLimitExceeded = true; } catch { /* Keep the original failure. */ }
      }
      throw error;
    }
  }

  installBridge(bridge) {
    const fn = this.context.newFunction('__meowBridge', (operation, input, ...values) => {
      const frames = [], lifetimes = [];
      let response, outFrames = [];
      try {
        const name = this.context.getString(operation);
        const json = this.context.getString(input);
        for (const value of values) {
          const view = this.context.getArrayBuffer(value);
          lifetimes.push(view);
          frames.push(Buffer.from(view.value));
        }
        const answer = bridge(name, JSON.parse(json), frames, outFrames);
        response = { ok: true, value: answer ?? null };
      } catch (error) {
        outFrames = [];
        response = { ok: false, code: error?.code ?? 'SCRIPT_EXCEPTION' };
      } finally {
        for (const frame of frames) {
          try { frame.fill(0); } catch { /* Preserve the bridge result. */ }
        }
        for (const lifetime of lifetimes) {
          try { lifetime.dispose(); } catch { /* Preserve the bridge result. */ }
        }
      }
      const json = JSON.stringify(response);
      if (!outFrames.length) return this.context.newString(json);
      const result = this.context.newArray();
      const jsonHandle = this.context.newString(json);
      try { this.context.setProp(result, 0, jsonHandle); }
      finally { jsonHandle.dispose(); }
      outFrames.forEach((frame, index) => {
        const handle = this.context.newArrayBuffer(frame);
        try { this.context.setProp(result, index + 1, handle); }
        finally { handle.dispose(); }
      });
      for (const frame of outFrames) {
        try { frame.fill(0); } catch { /* The guest already owns its copy. */ }
      }
      return result;
    });
    try { this.context.setProp(this.context.global, '__meowBridge', fn); }
    finally { fn.dispose(); }
  }

  constructor(module, limits) {
    requireValue(limits.maxStackBytes <= 1024 * 1024);
    this.methods = new Map();
    this.limits = limits;
    this.depth = 0;
    this.closing = false;
    this.closed = false;
    this.memoryLimitExceeded = false;
    this.runtime = undefined;
    this.context = undefined;
    try {
      this.runtime = module.newRuntime();
      this.runtime.setMemoryLimit(limits.maxMemoryBytes);
      this.runtime.setMaxStackSize(limits.maxStackBytes);
      this.context = this.runtime.newContext();
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
    // QuickJS reports heap exhaustion directly. Check this before guest
    // errorCode() implementations, which may collapse every error to a
    // generic script exception.
    let name, message;
    try {
      if (this.context.typeof(handle) === 'object') {
        name = this.context.getProp(handle, 'name');
        message = this.context.getProp(handle, 'message');
        if (this.context.getString(name) === 'InternalError' &&
          /out of memory|allocation failed/i.test(this.context.getString(message))) {
          this.memoryLimitExceeded = true;
        }
      }
    } catch { /* Guest errors may not expose readable properties. */ }
    finally {
      try { name?.dispose(); } catch { /* Preserve error decoding. */ }
      try { message?.dispose(); } catch { /* Preserve error decoding. */ }
    }
    if (this.realm) {
      const fn = this.context.getProp(this.realm, 'errorCode');
      try {
        const result = this.context.callFunction(fn, this.realm, handle);
        if (result.value) {
          try {
            const code = this.context.getString(result.value) || 'SCRIPT_EXCEPTION';
            if (code === 'MEMORY_LIMIT') {
              this.memoryLimitExceeded = true;
              return 'RESOURCE_LIMIT';
            }
            return code;
          }
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
      const error = fault(code);
      if (this.memoryLimitExceeded) error.memoryLimitExceeded = true;
      throw error;
    }
    return result.value;
  }

  run(body) {
    if (this.closed || this.closing) throw fault('STALE_SESSION');
    this.depth++;
    try {
      return body();
    } catch (error) {
      if (error?.code === 'MEMORY_LIMIT') {
        this.memoryLimitExceeded = true;
        throw fault('RESOURCE_LIMIT');
      }
      throw error;
    } finally {
      this.depth--;
      if (!this.depth && this.closing) this.destroy();
    }
  }

  method(name) {
    if (!this.methods.has(name)) this.methods.set(name, this.context.getProp(this.realm, name));
    return this.methods.get(name);
  }

  callInside(name, args, frames = []) {
    const handles = [];
    let failure;
    try {
      for (const value of args) handles.push(typeof value === 'string'
        ? this.context.newString(value) : this.context.newNumber(value));
      for (const frame of frames) handles.push(this.context.newArrayBuffer(frame));
      const value = this.unwrap(this.context.callFunction(this.method(name), this.realm, ...handles));
      value.dispose();
    } catch (error) { failure = error; }
    const cleanupFailure = disposeAll(handles);
    failure ??= cleanupFailure;
    if (failure) throw failure;
  }

  pumpInside() {
    while (!this.closing && this.runtime.hasPendingJob()) {
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

  call(name, args, frames = []) {
    this.run(() => {
      this.callInside(name, args, frames);
      this.pumpInside();
    });
  }

  /** Internal lifecycle notifications must not pump unrelated guest jobs under
   * the notification caller's host scope. */
  callUnpumped(name, args) {
    this.run(() => this.callInside(name, args));
  }

  /** Synchronous callback return across the same JSON boundary. Jobs are pumped
   * after the native caller has registered any asynchronous callback receipt. */
  callJSON(name, args) {
    return this.callJSONFrames(name, args, []).value;
  }

  /** Like callJSON, but binary payloads travel as ArrayBuffers. The guest
   * returns either the JSON string or [JSON string, ...frames]. */
  callJSONFrames(name, args, frames) {
    return this.run(() => {
      const handles = [];
      let result, failure;
      try {
        for (const value of args) handles.push(this.context.newString(value));
        for (const frame of frames) handles.push(this.context.newArrayBuffer(frame));
        for (const frame of frames) frame.fill(0);
        const value = this.unwrap(this.context.callFunction(this.method(name), this.realm, ...handles));
        try {
          const outputFrames = [];
          let json, jsonHandle;
          if (this.context.typeof(value) === 'string') {
            jsonHandle = value.dup();
            json = this.context.getString(value);
          } else {
            const first = this.context.getProp(value, 0);
            try {
              jsonHandle = first.dup();
              json = this.context.getString(first);
            } finally { first.dispose(); }
            // core.getLength caches an output view that becomes stale after heap growth.
            const lengthHandle = this.context.getProp(value, 'length');
            let length;
            try { length = this.context.getNumber(lengthHandle); }
            finally { lengthHandle.dispose(); }
            for (let index = 1; index < length; index++) {
              const item = this.context.getProp(value, index);
              const view = this.context.getArrayBuffer(item);
              try { outputFrames.push(Buffer.from(view.value)); }
              finally {
                try { view.dispose(); } catch (error) { failure ??= error; }
                try { item.dispose(); } catch (error) { failure ??= error; }
              }
            }
          }
          jsonHandle.dispose();
          result = { value: JSON.parse(json), frames: outputFrames };
        }
        catch (error) { failure = error; }
        try { value.dispose(); } catch (error) { failure ??= error; }
      } catch (error) { failure ??= error; }
      const cleanupFailure = disposeAll(handles);
      failure ??= cleanupFailure;
      if (failure) throw failure;
      return result;
    });
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
    while (runtime && attempt(() => runtime.hasPendingJob())) {
      const result = attempt(() => runtime.executePendingJobs(1));
      if (!result) break;
      if (result.error) {
        attempt(() => result.error.dispose());
        break;
      }
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
