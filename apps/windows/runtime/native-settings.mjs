import { BridgeError } from '@meowfilm/runtime-bridge';
import { parseJSONText, stringifyJSONValue, cloneJSONValue } from '@meowfilm/runtime-bridge/json';

const blocks = new Set(['cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences']);
const maximumBytes = 8 * 1024 * 1024;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

/** A process-private capability, never exported to scripts or the HTTP clients. */
export function createWindowsNativeSettings(stream, stop) {
  const snapshots = new Map(), pending = new Map();
  let sequence = 0, chunks = [], length = 0, closed = false;
  function request(kind, block, extra = {}) {
    if (closed) return Promise.reject(new BridgeError('CLOSED'));
    if (!blocks.has(block)) return Promise.reject(new BridgeError('INVALID_REQUEST'));
    const id = ++sequence;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => stop('TIMEOUT'), 5000);
      pending.set(id, { block, resolve, reject, timer });
      stream.write(stringifyJSONValue({ kind, id, block, ...extra }, -2) + '\n', error => {
        if (error) stop('CLOSED');
      });
    });
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
        if (length > maximumBytes) { stop('INVALID_RESPONSE'); return; }
        chunks.push(part);
        offset = end + 1;
        if (newline < 0) return;
        let reply;
        try { reply = parseJSONText(Buffer.concat(chunks, length).toString('utf8')); }
        catch { stop('INVALID_RESPONSE'); return; }
        chunks = []; length = 0;
        const exchange = pending.get(reply?.id);
        if (!object(reply) || reply.kind !== 'settings.result' || !exchange || typeof reply.ok !== 'boolean') {
          stop('INVALID_RESPONSE'); return;
        }
        if (reply.ok && (!(reply.document === null || object(reply.document)) || !object(reply.fingerprint))) {
          stop('INVALID_RESPONSE'); return;
        }
        clearTimeout(exchange.timer);
        pending.delete(reply.id);
        if (!reply.ok) exchange.reject(Object.assign(new Error(reply.code), { code: reply.code }));
        else {
          snapshots.set(exchange.block, { document: cloneJSONValue(reply.document), fingerprint: reply.fingerprint });
          exchange.resolve({ document: cloneJSONValue(reply.document), fingerprint: cloneJSONValue(reply.fingerprint) });
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
    async write(block, document, expected) {
      if (block !== 'pan.accounts' || !object(document)) throw new BridgeError('INVALID_REQUEST');
      return request('settings.write', block, { document, expected });
    },
    document(block) {
      let current = cloneJSONValue(snapshots.get(block), -2);
      return {
        read: () => cloneJSONValue(current.document),
        async reload() {
          current = await request('settings.read', block);
          return cloneJSONValue(current.document);
        },
        async write(document) {
          current = await request('settings.write', block, { document, expected: current.fingerprint });
          return cloneJSONValue(current.document);
        },
        close() { current = null; },
      };
    },
    close() {
      closed = true; chunks = []; length = 0; snapshots.clear();
      for (const exchange of pending.values()) { clearTimeout(exchange.timer); exchange.reject(new BridgeError('CLOSED')); }
      pending.clear();
    },
  };
}
