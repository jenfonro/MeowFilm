import { randomUUID } from 'node:crypto';
import { ScriptHostError } from '@meowfilm/script-host';
import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';

/** Pending native presentation belongs to the exact request signal and profile. */
export function createSourceWebBroker() {
  const pending = new Map();
  let closed = false;
  return {
    interact({ profile, mode, request, signal, scope }) {
      if (closed || signal.aborted) return Promise.reject(new ScriptHostError('CANCELLED'));
      return new Promise((resolve, reject) => {
        const id = randomUUID();
        const finish = (error, value) => {
          if (!pending.delete(id)) return;
          signal.removeEventListener('abort', abort);
          if (error) reject(error);
          else resolve(value);
        };
        const abort = () => finish(new ScriptHostError('CANCELLED'));
        pending.set(id, { profile, signal,
          value: { id, installationId: scope.installationId, mode, request: cloneJSONValue(request) },
          finish });
        signal.addEventListener('abort', abort, { once: true });
        if (signal.aborted) abort();
      });
    },
    pending() {
      return { requests: [...pending.values()].map(item => cloneJSONValue(item.value)) };
    },
    pendingCount() {
      return pending.size;
    },
    respond({ id, result, cancelled }) {
      const item = pending.get(id);
      if (!item || item.signal.aborted) throw new ScriptHostError('STALE_SESSION');
      item.finish(cancelled ? new ScriptHostError('CANCELLED') : null, result ?? null);
      return { accepted: true };
    },
    retire(prefix, except = '') {
      for (const item of pending.values()) {
        const kept = except && (item.profile === except || item.profile.startsWith(`${except}:`));
        if (item.profile.startsWith(prefix) && !kept) {
          item.finish(new ScriptHostError('CANCELLED'));
        }
      }
    },
    close() {
      closed = true;
      for (const item of pending.values()) item.finish(new ScriptHostError('CANCELLED'));
    },
  };
}
