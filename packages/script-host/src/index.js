import { ScriptHost } from './host.js';
import { LXSession } from './lx-session.js';

export { ScriptHostError } from './errors.js';
export { createNodeNetwork } from './node-network.js';
export { normalizeNetworkOrigin } from './network-origin.js';

/** All I/O is denied unless explicitly granted AND supplied by the assembler. */
export function createScriptHost(options = {}) {
  return new ScriptHost({
    ...options,
    adapters: {
      lx: input => new LXSession(input),
    },
  });
}
