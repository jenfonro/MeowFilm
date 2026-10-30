import { ScriptHost } from './host.js';
import { LXSession } from './lx-session.js';

export { ScriptHostError } from './errors.js';
export { createNodeNetwork } from './node-network.js';

/** Protocol adapters share the same instance manager and isolated execution backend. */
export function createScriptHost({ adapters = { lx: input => new LXSession(input) }, ...options } = {}) {
  return new ScriptHost({
    ...options,
    adapters,
  });
}
