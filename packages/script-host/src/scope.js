import { deferred, fault, safeError } from './errors.js';
import { setMaxListeners } from 'node:events';

export function raceSignal(signal, promise, fallbackCode = 'CANCELLED') {
  if (!signal) return promise;
  if (signal.aborted) return Promise.reject(safeError(signal.reason, fallbackCode));
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(safeError(signal.reason, fallbackCode));
    signal.addEventListener('abort', onAbort, { once: true });
    promise.then(resolve, reject).finally(() => signal.removeEventListener('abort', onAbort));
  });
}

export async function waitWithTimeout(promise, timeoutMs) {
  let timer;
  try {
    await Promise.race([
      promise,
      new Promise(resolve => { timer = setTimeout(resolve, timeoutMs); }),
    ]);
  } finally { clearTimeout(timer); }
}

/** A cooperative resource scope, not a thread or a security boundary. */
export class ResourceScope {
  constructor(parentSignal, timeoutMs = 0) {
    this.controller = new AbortController();
    this.signal = this.controller.signal;
    // Only this scope's signal is configured, never the process-wide default.
    setMaxListeners(0, this.signal);
    this.cleanups = new Set();
    this.finished = deferred();
    this.reason = undefined;
    this.deadline = timeoutMs > 0 ? Date.now() + timeoutMs : Infinity;
    if (parentSignal) {
      const abort = () => this.close(parentSignal.reason?.code
        ? fault(parentSignal.reason.code) : fault('CANCELLED'));
      if (parentSignal.aborted) abort();
      else {
        parentSignal.addEventListener('abort', abort, { once: true });
        this.cleanups.add(() => parentSignal.removeEventListener('abort', abort));
      }
    }
    if (timeoutMs > 0 && !this.signal.aborted) {
      const timer = setTimeout(() => this.close(fault('TIMEOUT')), timeoutMs);
      this.cleanups.add(() => clearTimeout(timer));
    }
  }

  assertOpen() {
    if (!this.signal.aborted && Date.now() >= this.deadline) this.close(fault('TIMEOUT'));
    if (this.signal.aborted) throw this.reason;
  }

  add(cleanup) {
    if (this.signal.aborted) { cleanup(); return () => {}; }
    this.cleanups.add(cleanup);
    return () => this.cleanups.delete(cleanup);
  }

  close(reason = fault('CANCELLED')) {
    if (this.signal.aborted) return;
    this.reason = reason;
    this.controller.abort(reason);
    for (const cleanup of this.cleanups) {
      try { cleanup(); } catch { /* Cleanup faults must not skip other resources. */ }
    }
    this.cleanups.clear();
    this.finished.reject(reason);
  }

  race(promise) {
    this.assertOpen();
    return Promise.race([promise, this.finished.promise]);
  }
}
