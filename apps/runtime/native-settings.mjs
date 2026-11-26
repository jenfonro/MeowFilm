import { isUtf8 } from 'node:buffer';
import { performance } from 'node:perf_hooks';
import { BridgeError } from '@meowfilm/runtime-bridge';
import { parseJSONText, stringifyJSONValue } from '@meowfilm/runtime-bridge/json';

export const APP_SETTINGS_BLOCKS = new Set(['cat.sources', 'pan.accounts', 'tmdb', 'douban', 'film.rules', 'film.preferences']);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const revision = value => Number.isSafeInteger(value) && value >= 0;

/** An in-memory read capability over an App-owned private stream, never a settings store. */
export function createNativeSettings(stream, stop, { debug } = {}) {
  const snapshots = new Map(), pending = new Map();
  let sequence = 0, chunks = [], length = 0, closed = false;
  const log = (level, event, data, extra = {}) => {
    if (!debug) return;
    debug.emit({ level, cat: 'runtime-bridge', sub: 'native-settings', event, data, ...extra });
  };
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
    log('error', 'native-settings.fail', { code });
    close();
    stop(code);
  }
  function request(kind, block, extra = {}) {
    if (closed) return Promise.reject(new BridgeError('CLOSED'));
    if (!APP_SETTINGS_BLOCKS.has(block)) return Promise.reject(new BridgeError('INVALID_REQUEST'));
    const id = ++sequence;
    const line = stringifyJSONValue({ kind, id, block, ...extra }, -2) + '\n';
    const startedAt = debug ? performance.now() : 0;
    log('debug', 'native-settings.request', { kind, id, block, ...extra });
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => fail('TIMEOUT'), 5000);
      pending.set(id, { kind, block, startedAt, resolve, reject, timer });
      try { stream.write(line, error => { if (error) fail('CLOSED'); }); }
      catch { fail('CLOSED'); }
    });
  }
  function update(block, command) {
    if (block !== 'pan.accounts' || !object(command))
      return Promise.reject(new BridgeError('INVALID_REQUEST'));
    return request('settings.pan.update', block, { command });
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
              !revision(reply.revision)) ||
            !reply.ok && (typeof reply.code !== 'string' || !reply.code)) {
          fail('INVALID_RESPONSE'); return;
        }
        clearTimeout(exchange.timer);
        pending.delete(reply.id);
        const durationMs = debug ? performance.now() - exchange.startedAt : undefined;
        if (!reply.ok) {
          log('warn', 'native-settings.response', {
            id: reply.id, kind: exchange.kind, block: exchange.block, ok: false, code: reply.code,
          }, { durationMs });
          exchange.reject(Object.assign(new Error(reply.code), { code: reply.code }));
        }
        else {
          const value = { document: reply.document, revision: reply.revision };
          log('debug', 'native-settings.response', {
            id: reply.id, kind: exchange.kind, block: exchange.block, ok: true,
            revision: reply.revision, document: reply.document,
          }, { durationMs });
          snapshots.set(exchange.block, value);
          exchange.resolve(value);
        }
      }
    },
    async reload(block) { return (await request('settings.read', block)).document; },
    async initialize() { for (const block of APP_SETTINGS_BLOCKS) await request('settings.read', block); },
    read(block) {
      if (closed) throw new BridgeError('CLOSED');
      if (!snapshots.has(block)) throw new BridgeError('INVALID_REQUEST');
      return snapshots.get(block).document;
    },
    update,
    accounts(block) {
      if (closed) throw new BridgeError('CLOSED');
      if (block !== 'pan.accounts' || !snapshots.has(block)) throw new BridgeError('INVALID_REQUEST');
      let current = snapshots.get(block);
      const open = () => { if (current === null || closed) throw new BridgeError('CLOSED'); };
      return {
        read() { open(); return current.document; },
        async reload() {
          open();
          const value = await request('settings.read', block);
          open(); current = value;
          return current.document;
        },
        async update(command) {
          open();
          const value = await update(block, command);
          open(); current = value;
          return current.document;
        },
        close() { current = null; },
      };
    },
    close,
  };
}
