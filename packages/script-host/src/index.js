import { ScriptHost } from './host.js';
import { LXSession } from './lx-session.js';

export { ScriptHostError } from './errors.js';
export { createNodeNetwork } from './node-network.js';
export { CatRuntime, createCatRuntime } from './cat-runtime.js';
export { SourceTransfers } from './source-transfers.js';

/** Protocol owners reuse this manager and backend in the same Node process. */
export function createScriptHost({ adapters = { lx: input => new LXSession(input) }, ...options } = {}) {
  return new ScriptHost({
    ...options,
    adapters,
  });
}
