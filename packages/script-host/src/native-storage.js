import { Worker } from 'node:worker_threads';
import { storageError } from '@meowfilm/storage';
import { loadNativeStorageBinding } from './native-engine.js';

export function createDesktopStorageDriver(path, identity, initialize) {
  const worker = new Worker(new URL('./sqlite-worker.js', import.meta.url), {
    workerData: { path, identity, initialize },
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
      try { await ready; await this.request({ kind: 'close' }); }
      finally { if (await exited !== 0) throw storageError('STORAGE_ERROR'); }
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
    if (pending.size >= 66 || !Number.isSafeInteger(sequence + 1))
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
        try { await ready; if (handle) await request({ kind: 'close' }); }
        finally { closed = true; await joined; handle = null; }
      })();
    },
  };
}
