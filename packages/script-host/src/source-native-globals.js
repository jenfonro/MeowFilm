import { Buffer, sourceUtility } from './source-native-codec.js';

globalThis.Buffer = Buffer;
globalThis.TextEncoder = class {
  encode(value = '') { return sourceUtility('encode', [String(value), 'utf8']); }
  encodeInto(value, target) {
    let read = 0, written = 0;
    for (const point of String(value)) {
      const bytes = this.encode(point);
      if (written + bytes.length > target.length) break;
      target.set(bytes, written);
      read += point.length;
      written += bytes.length;
    }
    return { read, written };
  }
};
globalThis.TextDecoder = class {
  constructor(encoding = 'utf8', { ignoreBOM = false } = {}) {
    const label = String(encoding).trim().toLowerCase();
    // HTTP TextDecoder follows the web labels, not Buffer's latin1/ascii
    // conversions. The latter remain unchanged for explicit script tools.
    this.encoding = [
      'ansi_x3.4-1968', 'ascii', 'cp1252', 'cp819', 'csisolatin1', 'ibm819',
      'iso-8859-1', 'iso-ir-100', 'iso8859-1', 'iso88591', 'iso_8859-1',
      'iso_8859-1:1987', 'l1', 'latin1', 'us-ascii', 'windows-1252', 'x-cp1252',
    ].includes(label) ? 'windows-1252' : label;
    this.ignoreBOM = ignoreBOM;
  }
  decode(value = new Uint8Array()) {
    const bytes = value instanceof ArrayBuffer ? new Uint8Array(value)
      : new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
    const text = sourceUtility('decode', [bytes, this.encoding]).toWellFormed();
    return !this.ignoreBOM && text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  }
};
globalThis.structuredClone = function clone(value, seen = new Map()) {
  const frames = [];
  const visit = input => {
    if (input === null || typeof input !== 'object') {
      if (typeof input === 'function' || typeof input === 'symbol') throw new TypeError('Invalid value');
      return input;
    }
    if (seen.has(input)) return seen.get(input);
    if (ArrayBuffer.isView(input)) {
      return new Uint8Array(new Uint8Array(input.buffer, input.byteOffset, input.byteLength));
    }
    if (input instanceof ArrayBuffer) return input.slice(0);
    const output = Array.isArray(input) ? [] : {};
    seen.set(input, output);
    frames.push({ output, entries: Object.entries(input), index: 0 });
    return output;
  };
  const output = visit(value);
  while (frames.length) {
    const frame = frames[frames.length - 1];
    if (frame.index === frame.entries.length) { frames.pop(); continue; }
    const [key, child] = frame.entries[frame.index++];
    Object.defineProperty(frame.output, key, {
      value: visit(child), enumerable: true, writable: true, configurable: true,
    });
  }
  return output;
};

class SourceAbortSignal {
  #listeners = new Map();
  aborted = false;
  reason = undefined;
  addEventListener(name, listener, options = {}) {
    if (name === 'abort' && typeof listener === 'function') {
      this.#listeners.set(listener, Boolean(options.once));
    }
  }
  removeEventListener(name, listener) {
    if (name === 'abort') this.#listeners.delete(listener);
  }
  throwIfAborted() { if (this.aborted) throw this.reason; }
  abort(reason) {
    if (this.aborted) return;
    this.aborted = true;
    this.reason = reason ?? Object.assign(new Error('Cancelled'), { code: 'CANCELLED' });
    for (const [listener, once] of [...this.#listeners]) {
      if (once) this.#listeners.delete(listener);
      listener.call(this, { type: 'abort', target: this });
    }
  }
}
globalThis.AbortSignal = SourceAbortSignal;
globalThis.AbortController = class {
  signal = new SourceAbortSignal();
  abort(reason) { this.signal.abort(reason); }
};
globalThis.queueMicrotask = callback => Promise.resolve().then(callback);
