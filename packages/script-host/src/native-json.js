import { performance } from 'node:perf_hooks';
import { createJSONCodec, parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { NativeEngine } from './native-engine.js';
import { nativeKernelIdentity } from './native-loader.js';

export { nativeKernelIdentity };

const fault = code => Object.assign(new Error(code), { code });

function bootstrap(source) {
  return `(() => {
    const { parseJSONText: parse, stringifyJSONValue: stringify } =
      (${createJSONCodec.toString()})(() => { throw { code: 'INVALID_ARGUMENT' }; });
    for (const name of ['__meowHost', '__meowEmit', '__meowNative', '__meowTaskContext',
      '__meowGuest', '__meowCore', '__meowHostArgs', 'setTimeout', 'setInterval',
      'clearTimeout', 'clearInterval']) delete globalThis[name];
    (0, eval)(${JSON.stringify(source)});
    delete globalThis.__meowRunWithDeadline;
    const process = globalThis.meowfilmProcess;
    if (typeof process !== 'function') throw { code: 'INVALID_ARGUMENT' };
    return {
      async invoke(text) {
        const result = stringify(await process(parse(text, -Infinity)), -Infinity);
        if (typeof result !== 'string') throw { code: 'INVALID_ARGUMENT' };
        return result;
      }
    };
  })()`;
}

/**
 * A pure JSON program on the shared native core, with its own thread and Runtime.
 * Calls share program state and execute serially. A failed turn is fully released
 * before the next admitted call initializes the same source again.
 * timeoutMs is a positive safe integer covering both cold-start bootstrap and invoke.
 */
export class NativeJsonProgram {
  #source;
  #engine;
  #queue = [];
  #running;
  #draining;
  #closed = false;
  #closing;
  #allowDeadlineControl;
  #debug;
  #name;

  constructor(source, { allowDeadlineControl = false, debug = null, name = 'native-json' } = {}) {
    if (typeof source !== 'string') throw fault('INVALID_ARGUMENT');
    this.#source = bootstrap(source);
    this.#allowDeadlineControl = Boolean(allowDeadlineControl);
    this.#debug = debug;
    this.#name = name;
  }

  stats() {
    const native = this.#engine?.stats() ?? null;
    return {
      name: this.#name,
      live: Boolean(this.#engine && !this.#closed),
      queued: this.#queue.length,
      running: Boolean(this.#running),
      ...(native ? { native } : {}),
    };
  }

  async invoke(value, { signal, timeoutMs = 2_000 } = {}) {
    if (this.#closed) throw fault('STALE_SESSION');
    if (signal?.aborted) throw fault('CANCELLED');
    if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0) throw fault('INVALID_ARGUMENT');
    let input;
    // Text programs retain their heap/stack budgets, not HTTP's depth limit.
    try { input = stringifyJSONValue(value, -Infinity); } catch { throw fault('INVALID_ARGUMENT'); }
    if (typeof input !== 'string') throw fault('INVALID_ARGUMENT');
    if (this.#closed) throw fault('STALE_SESSION');
    if (signal?.aborted) throw fault('CANCELLED');
    return new Promise((resolve, reject) => {
      const entry = { input, signal, timeoutMs, resolve, reject };
      entry.abort = () => {
        if (this.#running === entry) return; // The active NativeEngine call owns interruption.
        const index = this.#queue.indexOf(entry);
        if (index < 0) return;
        this.#queue.splice(index, 1);
        this.#settle(entry, fault('CANCELLED'));
      };
      this.#queue.push(entry);
      signal?.addEventListener('abort', entry.abort, { once: true });
      if (signal?.aborted) entry.abort();
      this.#start();
    });
  }

  #settle(entry, error, value) {
    entry.signal?.removeEventListener('abort', entry.abort);
    entry.input = undefined;
    if (error) entry.reject(error);
    else entry.resolve(value);
  }

  #start() {
    if (this.#draining || this.#closed || !this.#queue.length) return;
    this.#draining = this.#drain().finally(() => {
      this.#draining = undefined;
      this.#start();
    });
  }

  async #drain() {
    while (!this.#closed && this.#queue.length) {
      const entry = this.#queue.shift();
      this.#running = entry;
      const startedAt = performance.now();
      const deadline = Date.now() + entry.timeoutMs;
      try {
        if (entry.signal?.aborted) throw fault('CANCELLED');
        if (!this.#engine) {
          this.#engine = NativeEngine.create({
            source: this.#source, signal: entry.signal,
            maxHeapBytes: 64 * 1024 * 1024, maxStackBytes: 512 * 1024,
            allowDeadlineControl: this.#allowDeadlineControl,
            timeoutMs: entry.timeoutMs, cleanupTimeoutMs: 1,
            onEvent: (eventName, eventValue) => {
              if (eventName === 'failure' && this.#debug) {
                this.#debug.emit({
                  level: 'error',
                  cat: 'runtime-node',
                  sub: 'native-json',
                  event: 'program.failure',
                  data: { name: this.#name, code: eventValue?.code ?? 'SCRIPT_EXCEPTION', stats: eventValue?.stats ?? null },
                });
              }
            },
          });
          await this.#engine.ready;
        }
        const remaining = deadline - Date.now();
        if (remaining <= 0) throw fault('TIMEOUT');
        const text = await this.#engine.call('invoke', [entry.input], {
          signal: entry.signal, timeoutMs: remaining,
        });
        if (this.#closed) throw fault('STALE_SESSION');
        if (entry.signal?.aborted) throw fault('CANCELLED');
        this.#settle(entry, null, parseJSONText(text, -Infinity));
      } catch (error) {
        if (this.#closed) error = fault('STALE_SESSION');
        else if (entry.signal?.aborted) error = fault('CANCELLED');
        this.#debug?.emit({
          level: 'warn',
          cat: 'runtime-node',
          sub: 'native-json',
          event: 'program.error',
          durationMs: performance.now() - startedAt,
          data: { name: this.#name, code: error?.code ?? 'SCRIPT_EXCEPTION', stats: this.#engine?.stats() ?? null },
          error,
        });
        // A rejection never leaves a poisoned Runtime, jobs or native thread
        // alive behind the caller's completed cancellation/timeout.
        if (this.#engine) {
          await this.#engine.stop(error.code ?? 'SCRIPT_EXCEPTION');
          this.#engine = undefined;
        }
        this.#settle(entry, error);
      } finally {
        this.#running = undefined;
      }
    }
  }

  close() {
    if (!this.#closing) {
      this.#closed = true;
      for (const entry of this.#queue.splice(0)) this.#settle(entry, fault('STALE_SESSION'));
      const stopped = this.#engine?.stop();
      this.#closing = (async () => {
        await stopped;
        await this.#draining;
        this.#engine = undefined;
      })();
    }
    return this.#closing;
  }
}
