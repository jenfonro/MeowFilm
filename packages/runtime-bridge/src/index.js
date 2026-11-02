import { Buffer } from 'node:buffer';

export const BRIDGE_VERSION = 1;
export const MAX_FRAME_BYTES = 1024 * 1024;
const codes = new Set(['CANCELLED', 'TIMEOUT', 'CLOSED', 'LIMIT', 'INVALID_MESSAGE', 'METHOD_UNAVAILABLE', 'INTERNAL']);
const identity = /^[a-zA-Z0-9_.:-]{1,80}$(?![\s\S])/;
const methodName = /^[a-zA-Z][a-zA-Z0-9_.-]{0,127}$(?![\s\S])/;

export class BridgeError extends Error {
  constructor(code) {
    super(codes.has(code) ? code : 'INTERNAL');
    this.name = 'BridgeError';
    this.code = this.message;
  }
}

function invalid() { throw new BridgeError('INVALID_MESSAGE'); }
function checkString(value) {
  // Swift String represents Unicode scalars, not isolated UTF-16 surrogates.
  // Reject locally rather than emitting a frame that closes the native peer.
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) invalid();
    } else if (unit >= 0xdc00 && unit <= 0xdfff) invalid();
  }
}
function checkKey(value) {
  // Object keys are schema fields. Opaque/user keys belong in string values
  // (or arrays of { key, value }), never a Swift-normalizing String dictionary.
  if (/[^\x00-\x7f]/.test(value)) invalid();
}
function checkWire(text) {
  // Check keys before JSON.parse can discard duplicates; the parser remains
  // responsible for the complete JSON grammar. Limit nesting before parsing.
  const stack = [];
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (char === '{' || char === '[') {
      stack.push(char === '{' ? new Set() : null);
      if (stack.length > 66) invalid();
    } else if (char === '}' || char === ']') {
      if (!stack.length) invalid();
      stack.pop();
    } else if (char === '"') {
      const start = i++;
      for (; i < text.length; i++) {
        if (text[i] === '\\') i++;
        else if (text[i] === '"') break;
      }
      if (i >= text.length) invalid();
      let next = i + 1;
      while (next < text.length && /[\x20\t\r\n]/.test(text[next])) next++;
      if (text[next] === ':') {
        const key = JSON.parse(text.slice(start, i + 1));
        checkKey(key);
        const keys = stack.at(-1);
        if (!keys || keys.has(key)) invalid();
        keys.add(key);
      }
    }
  }
}
function checkJSON(value, depth = 0, ancestors = new Set()) {
  if (depth > 64) invalid();
  if (typeof value === 'string') { checkString(value); return; }
  if (value === null || typeof value === 'boolean') return;
  if (typeof value === 'number') {
    if (!Number.isFinite(value) || (Number.isInteger(value) && !Number.isSafeInteger(value))) invalid();
    return;
  }
  if (typeof value !== 'object' || ancestors.has(value)) invalid();
  const prototype = Object.getPrototypeOf(value);
  if (!Array.isArray(value) && prototype !== Object.prototype && prototype !== null) invalid();
  if (Object.getOwnPropertySymbols(value).length) invalid();
  ancestors.add(value);
  const descriptors = Object.getOwnPropertyDescriptors(value);
  for (const [key, descriptor] of Object.entries(descriptors)) {
    checkKey(key);
    if (Array.isArray(value) && key === 'length') continue;
    if (Array.isArray(value) && (!/^(0|[1-9][0-9]*)$/.test(key) || Number(key) >= value.length)) invalid();
    if (!descriptor.enumerable || descriptor.get || descriptor.set) invalid();
    checkJSON(descriptor.value, depth + 1, ancestors);
  }
  if (Array.isArray(value) && Object.keys(descriptors).length !== value.length + 1) invalid();
  ancestors.delete(value);
}

function validate(message) {
  checkJSON(message);
  if (!message || typeof message !== 'object' || Array.isArray(message) ||
      message.version !== BRIDGE_VERSION || typeof message.session !== 'string' ||
      !identity.test(message.session) || !Number.isSafeInteger(message.id) || message.id <= 0) invalid();
  let fields = ['version', 'session', 'id', 'kind'];
  if (message.kind === 'request') {
    if (typeof message.method !== 'string' || !methodName.test(message.method) || !Object.hasOwn(message, 'params')) invalid();
    fields = fields.concat('method', 'params');
  } else if (message.kind === 'result') {
    if (!Object.hasOwn(message, 'value')) invalid();
    fields.push('value');
  } else if (message.kind === 'error') {
    if (!codes.has(message.code)) invalid();
    fields.push('code');
  } else if (message.kind !== 'cancel') invalid();
  if (Object.keys(message).some(key => !fields.includes(key)) || Object.keys(message).length !== fields.length) invalid();
  return message;
}

export function encodeFrame(message, maximumBytes = MAX_FRAME_BYTES) {
  validate(message);
  const body = Buffer.from(JSON.stringify(message), 'utf8');
  if (body.length > maximumBytes) throw new BridgeError('LIMIT');
  const header = Buffer.alloc(4);
  header.writeUInt32BE(body.length);
  return Buffer.concat([header, body]);
}

/** Bounded decoder; chunks can contain fragmented or multiple frames. */
export class FrameDecoder {
  #header = Buffer.alloc(4);
  #headerCount = 0;
  #body;
  #bodyCount = 0;
  #failed = false;
  constructor({ maximumBytes = MAX_FRAME_BYTES } = {}) {
    if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 128 || maximumBytes > MAX_FRAME_BYTES) invalid();
    this.maximumBytes = maximumBytes;
  }
  push(chunk, onMessage) {
    if (this.#failed) throw new BridgeError('CLOSED');
    if (!(chunk instanceof Uint8Array)) invalid();
    let offset = 0;
    try {
      while (offset < chunk.length) {
        if (!this.#body) {
          const count = Math.min(4 - this.#headerCount, chunk.length - offset);
          this.#header.set(chunk.subarray(offset, offset + count), this.#headerCount);
          offset += count;
          this.#headerCount += count;
          if (this.#headerCount < 4) continue;
          const length = this.#header.readUInt32BE();
          if (!length || length > this.maximumBytes) throw new BridgeError('LIMIT');
          this.#body = Buffer.alloc(length);
        }
        const count = Math.min(this.#body.length - this.#bodyCount, chunk.length - offset);
        this.#body.set(chunk.subarray(offset, offset + count), this.#bodyCount);
        offset += count;
        this.#bodyCount += count;
        if (this.#bodyCount !== this.#body.length) continue;
        // Fatal UTF-8 decoding prevents replacement characters changing identity.
        const text = new TextDecoder('utf-8', { fatal: true }).decode(this.#body);
        checkWire(text);
        const message = validate(JSON.parse(text));
        this.#body = undefined;
        this.#headerCount = 0;
        this.#bodyCount = 0;
        onMessage(message);
      }
    } catch (error) {
      this.#failed = true;
      this.#body = undefined;
      throw error instanceof BridgeError ? error : new BridgeError('INVALID_MESSAGE');
    }
  }
  finish() {
    if (this.#headerCount || this.#bodyCount) invalid();
  }
}

/** Duplex RPC over an injected ordered binary transport, not an HTTP server.
 * The product registers a finite public method map; there is no eval, module
 * loader, filesystem access or implicit domain/installation selection.
 */
export class BridgePeer {
  #decoder;
  #pending = new Map();
  #active = new Map();
  #nextID = 0;
  #lastIncomingID = 0;
  #closed = false;
  #writing = false;
  #queued = new Set();
  #queuedBytes = 0;
  #methods;
  constructor({ session, send, close = () => {}, methods = {},
    maximumBytes = MAX_FRAME_BYTES, timeout = 30000 } = {}) {
    if (typeof session !== 'string' || !identity.test(session) || typeof send !== 'function' ||
        typeof close !== 'function' || !Number.isSafeInteger(timeout) ||
        timeout < 1 || timeout > 300000) invalid();
    for (const [name, method] of Object.entries(methods)) {
      if (!methodName.test(name) || typeof method !== 'function') invalid();
    }
    this.session = session;
    this.send = send;
    this.closeTransport = close;
    this.maximumBytes = maximumBytes;
    this.timeout = timeout;
    this.#methods = new Map(Object.entries(methods));
    this.#decoder = new FrameDecoder({ maximumBytes });
  }
  get state() {
    return Object.freeze({ closed: this.#closed, pending: this.#pending.size,
      active: this.#active.size, queuedBytes: this.#queuedBytes });
  }
  #envelope(kind, id, fields = {}) {
    return { version: BRIDGE_VERSION, session: this.session, kind, id, ...fields };
  }
  #write(message, { shouldSend = () => true, onSend = () => {}, onQueued = () => {} } = {}) {
    if (this.#closed) return Promise.reject(new BridgeError('CLOSED'));
    let bytes;
    try { bytes = encodeFrame(message, this.maximumBytes); }
    catch (error) { return Promise.reject(error); }
    const control = message.kind === 'cancel' || message.kind === 'error';
    // Bounded reserve for cancellation/errors prevents a full data queue from
    // silently retaining cancelled remote work. Exhausting even the reserve
    // closes the transport, making failure visible to both peers.
    const reserve = this.maximumBytes;
    const budget = this.maximumBytes * 4 - (control ? 0 : reserve);
    if (this.#queuedBytes + bytes.length > budget) {
      if (control) this.close();
      return Promise.reject(new BridgeError('LIMIT'));
    }
    this.#queuedBytes += bytes.length;
    const operation = new Promise((resolve, reject) => {
      const entry = { bytes, shouldSend, onSend, resolve, reject, writing: false };
      entry.discard = error => {
        if (entry.writing || !this.#queued.delete(entry)) return;
        this.#queuedBytes -= entry.bytes.length;
        entry.bytes = undefined;
        if (error) reject(error); else resolve();
      };
      this.#queued.add(entry);
      onQueued(entry.discard);
    });
    if (!this.#writing) {
      this.#writing = true;
      queueMicrotask(() => { void this.#drainWrites(); });
    }
    return operation;
  }
  async #drainWrites() {
    try {
      while (this.#queued.size) {
        const entry = this.#queued.values().next().value;
        this.#queued.delete(entry);
        entry.writing = true;
        try {
          if (this.#closed) throw new BridgeError('CLOSED');
          if (entry.shouldSend()) {
            entry.onSend();
            await this.send(entry.bytes);
          }
          entry.resolve();
        } catch (error) {
          entry.reject(error);
          this.close();
        } finally {
          this.#queuedBytes -= entry.bytes.length;
          entry.bytes = undefined;
        }
      }
    } finally { this.#writing = false; }
  }
  call(method, params = null, { signal, timeout = this.timeout } = {}) {
    if (this.#closed) return Promise.reject(new BridgeError('CLOSED'));
    if (signal?.aborted) return Promise.reject(new BridgeError('CANCELLED'));
    if (this.#nextID >= Number.MAX_SAFE_INTEGER) {
      return Promise.reject(new BridgeError('LIMIT'));
    }
    if (!Number.isSafeInteger(timeout) || timeout < 1 || timeout > 300000) {
      return Promise.reject(new BridgeError('INVALID_MESSAGE'));
    }
    const id = ++this.#nextID;
    let message;
    try {
      // Snapshot before scheduling, so caller mutation cannot retarget a call.
      message = JSON.parse(JSON.stringify(validate(this.#envelope('request', id, { method, params }))));
      encodeFrame(message, this.maximumBytes);
    } catch (error) { return Promise.reject(error); }
    return new Promise((resolve, reject) => {
      const settle = (error, value) => {
        const entry = this.#pending.get(id);
        if (!entry) return;
        this.#pending.delete(id);
        entry.discard?.();
        clearTimeout(entry.timer);
        signal?.removeEventListener('abort', entry.onAbort);
        if (error) reject(error); else resolve(value);
      };
      const cancel = code => {
        const sent = this.#pending.get(id)?.sent;
        settle(new BridgeError(code));
        if (sent) this.#write(this.#envelope('cancel', id)).catch(() => this.close());
      };
      const onAbort = () => cancel('CANCELLED');
      const timer = setTimeout(() => cancel('TIMEOUT'), timeout);
      this.#pending.set(id, { settle, timer, onAbort, sent: false });
      signal?.addEventListener('abort', onAbort, { once: true });
      this.#write(message, {
        shouldSend: () => this.#pending.has(id),
        onSend: () => { const entry = this.#pending.get(id); if (entry) entry.sent = true; },
        onQueued: discard => { const entry = this.#pending.get(id); if (entry) entry.discard = discard; else discard(); }
      }).catch(error => settle(error instanceof BridgeError ? error : new BridgeError('CLOSED')));
    });
  }
  receive(chunk) {
    if (this.#closed) return;
    try { this.#decoder.push(chunk, message => this.#accept(message)); }
    catch (error) { this.close(); throw error; }
  }
  #accept(message) {
    if (message.session !== this.session) throw new BridgeError('INVALID_MESSAGE');
    if (message.kind === 'result' || message.kind === 'error') {
      this.#pending.get(message.id)?.settle(message.kind === 'error' ? new BridgeError(message.code) : null, message.value);
      return;
    }
    if (message.kind === 'cancel') {
      this.#active.get(message.id)?.controller.abort();
      return;
    }
    if (message.id <= this.#lastIncomingID) throw new BridgeError('INVALID_MESSAGE');
    this.#lastIncomingID = message.id;
    const method = this.#methods.get(message.method);
    if (!method) {
      this.#write(this.#envelope('error', message.id, { code: 'METHOD_UNAVAILABLE' })).catch(() => this.close());
      return;
    }
    const controller = new AbortController();
    const entry = { controller, responded: false };
    this.#active.set(message.id, entry);
    const respond = (kind, fields) => {
      if (entry.responded || this.#closed) return;
      entry.responded = true;
      this.#write(this.#envelope(kind, message.id, fields)).catch(error => {
        // Invalid/oversized results fail explicitly; no original error message,
        // signed URL, stack, Cookie or arbitrary provider payload is serialized.
        if (!this.#closed) this.#write(this.#envelope('error', message.id, {
          code: error instanceof BridgeError ? error.code : 'INTERNAL'
        })).catch(() => this.close());
      });
    };
    const onAbort = () => respond('error', { code: 'CANCELLED' });
    controller.signal.addEventListener('abort', onAbort, { once: true });
    const timer = setTimeout(() => { respond('error', { code: 'TIMEOUT' }); controller.abort(); }, this.timeout);
    entry.timer = timer;
    Promise.resolve().then(() => {
      if (controller.signal.aborted) throw new BridgeError('CANCELLED');
      return method(message.params, { signal: controller.signal });
    }).then(value => respond('result', { value }), error => {
      respond('error', { code: error instanceof BridgeError ? error.code : 'INTERNAL' });
    }).finally(() => {
      clearTimeout(timer);
      controller.signal.removeEventListener('abort', onAbort);
      this.#active.delete(message.id);
    });
  }
  close() {
    if (this.#closed) return;
    this.#closed = true;
    for (const pending of this.#pending.values()) pending.settle(new BridgeError('CLOSED'));
    for (const entry of this.#queued) entry.discard(new BridgeError('CLOSED'));
    for (const entry of this.#active.values()) {
      clearTimeout(entry.timer);
      entry.controller.abort();
    }
    // Non-cooperative handlers remain counted until they really settle.
    try { this.closeTransport(); } catch {}
  }
}
