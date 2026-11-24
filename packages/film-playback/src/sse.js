import { Readable } from 'node:stream';
import { stringifyJSONValue } from '@meowfilm/runtime-bridge/json';

/**
 * Shared SSE snapshot stream for film search and playback sessions.
 * Marks disposed and detaches listener BEFORE pushing EOF so ERR_STREAM_PUSH_AFTER_EOF cannot happen.
 */
export function createSnapshotStream({ listeners, isClosed, getSequence, getSnapshot, after = -1, signal }) {
  let demand = false;
  let disposed = false;
  let lastSequence = after;

  const detach = () => {
    if (disposed) return false;
    disposed = true;
    signal?.removeEventListener('abort', onAbort);
    listeners.delete(changed);
    return true;
  };

  const changed = () => {
    if (disposed) return;
    if (isClosed()) {
      detach();
      body.push(null);
      return;
    }
    const sequence = getSequence();
    if (!demand || sequence <= lastSequence) return;
    demand = false;
    lastSequence = sequence;
    body.push(Buffer.from(`id: ${lastSequence}\nevent: snapshot\ndata: ${stringifyJSONValue(getSnapshot())}\n\n`));
  };

  const body = new Readable({
    highWaterMark: 16 * 1024,
    read() {
      demand = true;
      changed();
    },
    destroy(error, done) {
      detach();
      done(error);
    },
  });

  const onAbort = () => {
    detach();
    body.destroy();
  };

  if (signal?.aborted) {
    disposed = true;
    body.destroy();
  } else {
    listeners.add(changed);
    signal?.addEventListener('abort', onAbort, { once: true });
  }
  return {
    status: 200,
    headers: { 'content-type': 'text/event-stream', 'cache-control': 'no-store' },
    body,
    async dispose() {
      detach();
      body.destroy();
    },
  };
}
