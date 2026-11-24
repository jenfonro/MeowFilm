import { ScriptHost } from './host.js';
import { NativeSession } from './native-session.js';

export { ScriptHostError } from './errors.js';
export { createNodeNetwork } from './node-network.js';
export { createSourceDownload } from './source-download.js';
export { CatRuntime, createCatRuntime } from './cat-runtime.js';
export { PrivateSources } from './private-sources.js';
export { createNodeSourceCapabilities } from './source-capabilities.js';
export { createPortableSourceAdapter } from './portable-source-session.js';
export { parseLXHeader } from './lx-capabilities.js';

/** Protocol owners reuse this manager and backend in the same Node process. */
export function createScriptHost({ adapters = { lx: input => new NativeSession(input) }, ...options } = {}) {
  return new ScriptHost({
    ...options,
    adapters,
  });
}
