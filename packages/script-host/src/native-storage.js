import { Worker } from 'node:worker_threads';
import { storageError } from '@meowfilm/storage';
import { loadNativeStorageBinding } from './native-engine.js';

function spawnSqliteWorker(workerData) {
  const worker = new Worker(new URL('./sqlite-worker.js', import.meta.url), {
    workerData,
    execArgv: [],
  });
  let sequence = 0, stopped = false;
  const pending = new Map();
  function rejectAll(error) {
    stopped = true;
    for (const { reject } of pending.values()) reject(error);
    pending.clear();
  }
  const ready = new Promise((resolveReady, rejectReady) => {
    pending.set(0, { resolve: resolveReady, reject: rejectReady });
  });
  worker.on('message', ({ id, value, code }) => {
    const item = pending.get(id);
    if (!item) return;
    pending.delete(id);
    if (code) item.reject(storageError(code));
    else item.resolve(value);
  });
  worker.on('error', () => rejectAll(storageError('STORAGE_ERROR')));
  const exited = new Promise(resolve => worker.once('exit', code => {
    rejectAll(storageError('STORAGE_CLOSED'));
    resolve(code);
  }));
  ready.catch(() => {});
  return {
    ready,
    request(input) {
      if (stopped) return Promise.reject(storageError('STORAGE_CLOSED'));
      const id = ++sequence;
      return new Promise((resolveRequest, reject) => {
        pending.set(id, { resolve: resolveRequest, reject });
        try { worker.postMessage({ id, input }); }
        catch { pending.delete(id); reject(storageError('STORAGE_ERROR')); }
      });
    },
    async close() {
      try {
        try { await ready; } catch { return; }
        if (!stopped) await this.request({ kind: 'close' });
      }
      finally { if (await exited !== 0) throw storageError('STORAGE_ERROR'); }
    },
  };
}

/**
 * WAL read/write dual-worker driver for Desktop Node runtime:
 * - writer initializes/verifies schema and serializes writes, checkpoints, and backups.
 * - reader opens a dedicated readOnly + query_only=ON connection after writer ready so reads
 *   execute concurrently under SQLite WAL without queueing behind write transactions.
 */
export function createDesktopStorageDriver(path, identity, initialize) {
  const writer = spawnSqliteWorker({ path, identity, initialize, role: 'writer' });
  let reader = null;
  const ready = (async () => {
    await writer.ready;
    reader = spawnSqliteWorker({ path, identity, initialize: false, role: 'reader' });
    try {
      await reader.ready;
    } catch (error) {
      const failedReader = reader;
      reader = null;
      await failedReader.close().catch(() => {});
      await writer.close().catch(() => {});
      throw error;
    }
    return true;
  })();
  ready.catch(() => {});
  return {
    ready,
    request(input) {
      const target = (input?.kind === 'query' || input?.kind === 'snapshot') && reader ? reader : writer;
      return target.request(input);
    },
    async close() {
      let closeError = null;
      try { await ready; } catch {}
      if (reader) {
        try { await reader.close(); }
        catch (error) { closeError ??= error; }
        reader = null;
      }
      try { await writer.close(); }
      catch (error) { closeError ??= error; }
      if (closeError) throw closeError;
    },
  };
}

/** Platform bootstrap injects this driver into the trusted storage owner on iOS. */
export function createNativeStorageDriver(path, identity, initialize) {
  let sequence = 0, closed = false, disposal, handle;
  const pending = new Map();
  const failure = code => Object.assign(new Error(code), { code });
  const ready = new Promise((resolve, reject) => { pending.set(0, { resolve, reject }); });
  const joined = new Promise((resolve, reject) => { pending.set(-1, { resolve, reject }); });
  ready.catch(() => {});
  joined.catch(() => {});
  function receive({ id, value, code }) {
    const work = pending.get(id);
    if (!work) return;
    pending.delete(id);
    if (code) work.reject(failure(code));
    else work.resolve(value);
  }
  try { handle = loadNativeStorageBinding().openStorage({ path, identity, initialize }, receive); }
  catch (error) {
    pending.get(0).reject(error);
    pending.get(-1).resolve();
    pending.clear();
  }
  function request(input) {
    if (closed || !handle) return Promise.reject(failure('STORAGE_CLOSED'));
    if (pending.size >= 130 || !Number.isSafeInteger(sequence + 1))
      return Promise.reject(failure('STORAGE_BUSY'));
    const id = ++sequence;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      try { handle.request(id, input); }
      catch (error) { pending.delete(id); reject(error); }
    });
  }
  return {
    ready, request,
    close() {
      return disposal ??= (async () => {
        try {
          try { await ready; } catch { return; }
          if (handle) await request({ kind: 'close' });
        }
        finally { closed = true; await joined; handle = null; }
      })();
    },
  };
}
