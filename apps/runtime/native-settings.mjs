import { isUtf8 } from 'node:buffer';
import { BridgeError } from '@meowfilm/runtime-bridge';
import { parseJSONText, stringifyJSONValue, cloneJSONValue } from '@meowfilm/runtime-bridge/json';

const blocks = new Set(['cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences']);
export const nativeSettingsFrameBytes = 8 * 1024 * 1024;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const codes = new Set(['INVALID_REQUEST', 'PERMISSION_DENIED', 'CLOSED', 'CANCELLED', 'TIMEOUT',
  'STORAGE_ERROR', 'CORRUPT_STORAGE', 'STORAGE_CONFLICT', 'COMMIT_UNKNOWN']);
const fingerprint = value => object(value) &&
  typeof value.exists === 'boolean' && Number.isSafeInteger(value.length) && value.length >= 0 &&
  (value.exists ? typeof value.sha256 === 'string' && /^[a-f0-9]{64}$(?![\s\S])/.test(value.sha256)
    : value.length === 0 && value.sha256 === null);

/** An in-memory read capability over an App-owned private stream, never a settings store. */
export function createNativeSettings(stream, stop) {
  const snapshots = new Map(), pending = new Map();
  let sequence = 0, chunks = [], length = 0, closed = false;
  function close() {
    if (closed) return;
    closed = true; chunks = []; length = 0; snapshots.clear();
    for (const exchange of pending.values()) {
      clearTimeout(exchange.timer);
      exchange.reject(new BridgeError('CLOSED'));
    }
    pending.clear();
  }
  function fail(code) {
    if (closed) return;
    close();
    stop(code);
  }
  function request(kind, block, extra = {}) {
    if (closed) return Promise.reject(new BridgeError('CLOSED'));
    if (!blocks.has(block) || !Number.isSafeInteger(sequence + 1))
      return Promise.reject(new BridgeError('INVALID_REQUEST'));
    const id = ++sequence;
    const line = stringifyJSONValue({ kind, id, block, ...extra }, -2) + '\n';
    if (Buffer.byteLength(line) - 1 > nativeSettingsFrameBytes)
      return Promise.reject(new BridgeError('INVALID_REQUEST'));
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => fail('TIMEOUT'), 5000);
      pending.set(id, { block, resolve, reject, timer });
      try { stream.write(line, error => { if (error) fail('CLOSED'); }); }
      catch { fail('CLOSED'); }
    });
  }
  function write(block, document, expected) {
    if (block !== 'pan.accounts' || !object(document) || !fingerprint(expected))
      return Promise.reject(new BridgeError('INVALID_REQUEST'));
    return request('settings.write', block, { document, expected });
  }
  return {
    accept(bytes) {
      if (closed) return;
      let offset = 0;
      while (offset < bytes.length) {
        const newline = bytes.indexOf(10, offset);
        const end = newline < 0 ? bytes.length : newline;
        const part = bytes.subarray(offset, end);
        length += part.length;
        if (length > nativeSettingsFrameBytes) { fail('INVALID_RESPONSE'); return; }
        chunks.push(part);
        offset = end + 1;
        if (newline < 0) return;
        let reply;
        try {
          const frame = Buffer.concat(chunks, length);
          if (!isUtf8(frame)) throw new BridgeError('INVALID_RESPONSE');
          reply = parseJSONText(frame.toString('utf8'));
        } catch { fail('INVALID_RESPONSE'); return; }
        chunks = []; length = 0;
        const exchange = pending.get(reply?.id);
        if (!object(reply) || reply.kind !== 'settings.result' || !exchange || typeof reply.ok !== 'boolean' ||
            reply.ok && (!(reply.document === null || object(reply.document)) ||
              !fingerprint(reply.fingerprint)) ||
            !reply.ok && !codes.has(reply.code)) {
          fail('INVALID_RESPONSE'); return;
        }
        clearTimeout(exchange.timer);
        pending.delete(reply.id);
        if (!reply.ok) exchange.reject(Object.assign(new Error(reply.code), { code: reply.code }));
        else {
          const value = { document: reply.document, fingerprint: reply.fingerprint };
          snapshots.set(exchange.block, cloneJSONValue(value, -2));
          exchange.resolve(cloneJSONValue(value, -2));
        }
      }
    },
    async reload(block) { return (await request('settings.read', block)).document; },
    async initialize() { for (const block of blocks) await request('settings.read', block); },
    read(block) {
      if (closed) throw new BridgeError('CLOSED');
      if (!snapshots.has(block)) throw new BridgeError('INVALID_REQUEST');
      return cloneJSONValue(snapshots.get(block).document);
    },
    write,
    document(block) {
      if (closed) throw new BridgeError('CLOSED');
      if (block !== 'pan.accounts' || !snapshots.has(block)) throw new BridgeError('INVALID_REQUEST');
      let current = cloneJSONValue(snapshots.get(block), -2);
      const open = () => { if (current === null || closed) throw new BridgeError('CLOSED'); };
      return {
        read() { open(); return cloneJSONValue(current.document); },
        async reload() {
          open();
          const value = await request('settings.read', block);
          open(); current = value;
          return cloneJSONValue(current.document);
        },
        async write(document) {
          open();
          const value = await write(block, document, current.fingerprint);
          open(); current = value;
          return cloneJSONValue(current.document);
        },
        close() { current = null; },
      };
    },
    close,
  };
}
