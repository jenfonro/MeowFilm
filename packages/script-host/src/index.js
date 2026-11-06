import { ScriptHost } from './host.js';
import { NativeSession } from './native-session.js';

export { SCRIPT_LIMITS } from './host.js';
export { ScriptHostError } from './errors.js';
export { createNodeNetwork } from './node-network.js';
export { CatRuntime, createCatRuntime } from './cat-runtime.js';
export { createNodeSourceCapabilities } from './source-capabilities.js';
export { createPortableSourceAdapter } from './portable-source-session.js';
export { LXSession } from './lx-session.js';

/** Protocol owners reuse this manager and backend in the same Node process. */
export function createScriptHost({ adapters = { lx: input => new NativeSession(input) }, ...options } = {}) {
  return new ScriptHost({
    ...options,
    adapters,
  });
}
