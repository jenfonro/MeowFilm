import { readFile } from 'node:fs/promises';
import { newQuickJSWASMModuleFromVariant, newVariant } from 'quickjs-emscripten-core';
import RELEASE_SYNC from '@jitl/quickjs-wasmfile-release-sync';
import { fault, requireValue } from './errors.js';

let compiledPromise;
const loadModule = async () => {
  compiledPromise ??= readFile(new URL(import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')))
    .then(bytes => WebAssembly.compile(bytes));
  return newQuickJSWASMModuleFromVariant(newVariant(RELEASE_SYNC, { wasmModule: await compiledPromise }));
};

/** One isolated guest heap. The only host function exchanges JSON, never host objects. */
export class QuickJSEngine {
  static async create({ source, bridge, limits }) {
    const module = await loadModule();
    const engine = new QuickJSEngine(module, limits);
    try {
      const fn = engine.context.newFunction('__meowBridge', (operation, input) => {
        let response;
        try {
          const name = engine.context.getString(operation);
          const json = engine.context.getString(input);
          if (Buffer.byteLength(json) > limits.maxBytes * 4) throw fault('RESOURCE_LIMIT');
          response = { ok: true, value: bridge(name, JSON.parse(json)) ?? null };
        } catch (error) {
          response = { ok: false, code: error?.code ?? 'SCRIPT_EXCEPTION' };
        }
        return engine.context.newString(JSON.stringify(response));
      });
      engine.context.setProp(engine.context.global, '__meowBridge', fn);
      fn.dispose();
      engine.run(() => {
        engine.realm = engine.unwrap(engine.context.evalCode(source, 'script-sdk.js'));
      }, limits.initTimeoutMs);
      return engine;
    } catch (error) { engine.close(); throw error; }
  }

  constructor(module, limits) {
    requireValue(limits.maxStackBytes <= 64 * 1024);
    this.runtime = module.newRuntime();
    this.runtime.setMemoryLimit(limits.maxMemoryBytes);
    this.runtime.setMaxStackSize(limits.maxStackBytes);
    this.context = this.runtime.newContext();
    this.methods = new Map();
    this.limits = limits;
    this.depth = 0;
    this.closing = false;
    this.closed = false;
    this.deadline = Infinity;
    this.interrupted = false;
    this.runtime.setInterruptHandler(() => {
      if (Date.now() >= this.deadline) {
        this.interrupted = true;
        return true;
      }
      return false;
    });
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
    const handles = args.map(value => typeof value === 'string'
      ? this.context.newString(value) : this.context.newNumber(value));
    try {
      const value = this.unwrap(this.context.callFunction(this.method(name), this.realm, ...handles));
      value.dispose();
    } finally { for (const handle of handles) handle.dispose(); }
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

  close() {
    if (this.closing || this.closed) return;
    this.closing = true;
    if (!this.depth) this.destroy();
  }

  destroy() {
    if (this.closed) return;
    this.closed = true;
    for (const handle of this.methods.values()) handle.dispose();
    this.methods.clear();
    this.realm?.dispose();
    this.realm = undefined;
    this.context.dispose();
    this.runtime.dispose();
    this.context = undefined;
    this.runtime = undefined;
  }
}
