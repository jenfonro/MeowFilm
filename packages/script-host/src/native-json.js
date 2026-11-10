import { NativeEngine } from './native-engine.js';

const fault = code => Object.assign(new Error(code), { code });

function bootstrap(source) {
  return `(() => {
    const parse = JSON.parse, stringify = JSON.stringify;
    for (const name of ['__meowHost', '__meowEmit', '__meowNative', '__meowTaskContext',
      '__meowGuest', '__meowCore', '__meowHostArgs', 'setTimeout', 'setInterval',
      'clearTimeout', 'clearInterval']) delete globalThis[name];
    (0, eval)(${JSON.stringify(source)});
    const process = globalThis.meowfilmProcess;
    if (typeof process !== 'function') throw { code: 'INVALID_ARGUMENT' };
    return {
      async invoke(text) {
        const result = stringify(await process(parse(text)));
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
 */
export class NativeJsonProgram {
  #source;
  #engine;
  #queue = [];
  #running;
  #draining;
  #closed = false;
  #closing;

  constructor(source) {
    if (typeof source !== 'string') throw fault('INVALID_ARGUMENT');
    this.#source = bootstrap(source);
  }

  async invoke(value, { signal } = {}) {
    if (this.#closed) throw fault('STALE_SESSION');
    if (signal?.aborted) throw fault('CANCELLED');
    const input = JSON.stringify(value);
    if (typeof input !== 'string') throw fault('INVALID_ARGUMENT');
    if (this.#closed) throw fault('STALE_SESSION');
    if (signal?.aborted) throw fault('CANCELLED');
    return new Promise((resolve, reject) => {
      const entry = { input, signal, resolve, reject };
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
      try {
        if (entry.signal?.aborted) throw fault('CANCELLED');
        if (!this.#engine) {
          this.#engine = NativeEngine.create({
            source: this.#source, signal: entry.signal,
            maxHeapBytes: 64 * 1024 * 1024, maxStackBytes: 512 * 1024,
            timeoutMs: 2_000, cleanupTimeoutMs: 1,
          });
        }
        const text = await this.#engine.call('invoke', [entry.input], {
          signal: entry.signal, timeoutMs: 2_000,
        });
        if (this.#closed) throw fault('STALE_SESSION');
        this.#settle(entry, null, JSON.parse(text));
      } catch (error) {
        if (this.#closed) error = fault('STALE_SESSION');
        else if (entry.signal?.aborted) error = fault('CANCELLED');
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
