import { MusicPlatformError } from './errors.mjs';

export function waitForFlight(flight, signal) {
  return new Promise((resolve, reject) => {
    const waiter = {};
    flight.waiters.add(waiter);
    let settled = false;
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      signal?.removeEventListener('abort', onAbort);
      flight.waiters.delete(waiter);
      if (error) reject(error); else resolve(value);
    };
    const onAbort = () => {
      const reason = signal.reason instanceof MusicPlatformError
        ? signal.reason : new MusicPlatformError('CANCELLED');
      finish(reason);
      if (!flight.waiters.size && !flight.controller.signal.aborted) flight.controller.abort(reason);
    };
    signal?.addEventListener('abort', onAbort, { once: true });
    if (signal?.aborted) { onAbort(); return; }
    flight.promise.then(value => finish(null, value), error => finish(error));
  });
}
