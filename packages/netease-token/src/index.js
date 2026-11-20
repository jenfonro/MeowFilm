import { Worker } from 'node:worker_threads';
import { performance } from 'node:perf_hooks';

const codes = new Set(['TIMEOUT', 'NETWORK_ERROR', 'UPSTREAM_HTTP', 'UPSTREAM_FORMAT', 'SDK_FAILURE']);
const failure = code => Object.assign(new Error(code), { code });
// RFC 6265 cookie-octet: the token also travels in the native EAPI header cookie.
export const validToken = value => typeof value === 'string' && value.length > 0 &&
  !/[^\x21\x23-\x2b\x2d-\x3a\x3c-\x5b\x5d-\x7e]/.test(value);

/** One runtime-owned SDK worker; factory/timeout are trusted deterministic-test seams. */
export function createNeteaseTokenProvider({
  workerFactory = () => new Worker(new URL('./worker.js', import.meta.url), { stdout: true, stderr: true }),
  timeout = 45_000,
  debug,
} = {}) {
  let worker, active, stopping, closing, closed = false, cleanupError, next = 0;
  const queue = [];
  function settle(job, error, token) {
    clearTimeout(job.timer);
    job.signal?.removeEventListener('abort', job.abort);
    if (debug) {
      debug.emit({
        level: error ? 'warn' : 'debug',
        cat: 'music-platform',
        sub: 'netease.token',
        event: error ? 'token.error' : 'token.ready',
        durationMs: performance.now() - job.startedAt,
        data: error ? { jobId: job.id, code: error.code ?? null } : { jobId: job.id, token },
        error: error ?? null,
      });
    }
    if (error) job.reject(error); else job.resolve(token);
  }
  function destroy() {
    if (stopping) return stopping;
    const current = worker;
    worker = undefined;
    if (!current) return Promise.resolve();
    debug?.emit({ level: 'info', cat: 'runtime-node', sub: 'netease.worker', event: 'worker.terminate' });
    stopping = Promise.resolve().then(() => current.terminate()).catch(() => {
      cleanupError = failure('SDK_CLEANUP');
      closed = true;
      for (const job of queue.splice(0)) settle(job, cleanupError);
      throw cleanupError;
    }).finally(() => { stopping = undefined; pump(); });
    return stopping;
  }
  function failActive(code) {
    const job = active;
    active = undefined;
    // Do not dispatch the next request until this worker actually exits.
    const cleanup = destroy();
    if (job) settle(job, failure(code));
    cleanup.finally(pump).catch(() => {});
  }
  function pump() {
    if (closed || stopping || active || !queue.length) return;
    active = queue.shift();
    try {
      if (!worker) {
        const current = workerFactory();
        worker = current;
        debug?.emit({ level: 'info', cat: 'runtime-node', sub: 'netease.worker', event: 'worker.spawn', data: { threadId: current.threadId ?? null } });
        // Never forward third-party console output, tokens or SDK diagnostics.
        if (debug) {
          current.stdout?.on('data', chunk => {
            debug.emit({ level: 'debug', cat: 'runtime-node', sub: 'netease.worker', event: 'worker.stdout', data: { text: chunk.toString('utf8') } });
          });
          current.stderr?.on('data', chunk => {
            debug.emit({ level: 'warn', cat: 'runtime-node', sub: 'netease.worker', event: 'worker.stderr', data: { text: chunk.toString('utf8') } });
          });
        } else {
          current.stdout?.resume(); current.stderr?.resume();
        }
        current.on('message', message => {
          if (worker !== current || !active) return;
          if (!message || message.id !== active.id) { failActive('SDK_FAILURE'); return; }
          if (!validToken(message.token)) {
            failActive(codes.has(message.code) ? message.code : 'UPSTREAM_FORMAT');
            return;
          }
          const job = active;
          active = undefined;
          settle(job, undefined, message.token);
          pump();
        });
        current.on('error', error => {
          debug?.emit({ level: 'error', cat: 'runtime-node', sub: 'netease.worker', event: 'worker.error', error });
          if (worker === current) failActive('SDK_FAILURE');
        });
        current.on('exit', code => {
          debug?.emit({ level: 'info', cat: 'runtime-node', sub: 'netease.worker', event: 'worker.exit', data: { code } });
          if (worker === current) failActive('SDK_FAILURE');
        });
      }
      active.timer = setTimeout(() => failActive('TIMEOUT'), timeout);
      worker.postMessage({ id: active.id });
    } catch { failActive('SDK_FAILURE'); }
  }
  return Object.freeze({
    get({ signal } = {}) {
      if (closed) return Promise.reject(cleanupError ?? failure('CLOSED'));
      if (signal?.aborted) return Promise.reject(failure('CANCELLED'));
      return new Promise((resolve, reject) => {
        const job = { id: ++next, startedAt: debug ? performance.now() : 0, resolve, reject, signal };
        debug?.emit({ level: 'debug', cat: 'music-platform', sub: 'netease.token', event: 'token.request', data: { jobId: job.id, queueLength: queue.length } });
        job.abort = () => {
          if (active === job) failActive('CANCELLED');
          else {
            const index = queue.indexOf(job);
            if (index >= 0) { queue.splice(index, 1); settle(job, failure('CANCELLED')); }
          }
        };
        signal?.addEventListener('abort', job.abort, { once: true });
        queue.push(job);
        pump();
      });
    },
    diagnostics() {
      return {
        workerAlive: Boolean(worker),
        activeJob: Boolean(active),
        queueLength: queue.length,
        stopping: Boolean(stopping),
        closed,
      };
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        for (const job of queue.splice(0)) settle(job, failure('CLOSED'));
        const job = active; active = undefined;
        if (job) settle(job, failure('CLOSED'));
        await destroy();
        if (cleanupError) throw cleanupError;
      })();
    },
  });
}
