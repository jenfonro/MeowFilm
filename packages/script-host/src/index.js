import { ScriptHost } from './host.js';
import { LXSession } from './lx-session.js';
import { PortableSandbox } from './portable-sandbox.js';
import { createPortableSourceAdapter } from './portable-source-session.js';

export { ScriptHostError } from './errors.js';
export { createNodeNetwork } from './node-network.js';
export { createNodeSourceCapabilities } from './source-capabilities.js';
export { createPortableSourceAdapter, LXSession, PortableSandbox };

/** Protocol adapters share the same instance manager and isolated execution backend. */
export function createScriptHost({ adapters = { lx: input => new LXSession(input) }, ...options } = {}) {
  return new ScriptHost({
    ...options,
    adapters,
  });
}
