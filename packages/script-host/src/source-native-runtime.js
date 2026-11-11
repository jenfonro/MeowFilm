import './source-native-globals.js';
import { URL, URLSearchParams } from 'whatwg-url';
import { createCapabilityClient, SourceSession, sourceFault, sourceSafeError } from '@meowfilm/source-runtime';
import { PortableSandbox } from './portable-sandbox.js';
import { sourceUtility } from './source-native-codec.js';

globalThis.URL = URL;
globalThis.URLSearchParams = URLSearchParams;
const host = globalThis.__meowHostArgs;
const guest = globalThis.__meowGuest;
const preprocess = globalThis.__meowPreprocess;
const taskContext = globalThis.__meowTaskContext;
const emit = globalThis.__meowEmit;
for (const name of ['__meowHost', '__meowHostArgs', '__meowGuest', '__meowTaskContext', '__meowNative',
  '__meowEmit', '__meowCore', '__meowPreprocess']) delete globalThis[name];
let session, sandbox;
const operations = new Map();

function localCapability(name, input) {
  switch (name) {
    case 'encoding.encode': return sourceUtility('encode', [input.text, input.encoding]);
    case 'encoding.decode': return sourceUtility('decode', [input.body, input.encoding]);
    case 'crypto.digest': return sourceUtility('encode', [
      sourceUtility('digest', [input.algorithm, input.data]), 'hex',
    ]);
    case 'crypto.hmac': return sourceUtility('encode', [
      sourceUtility('hmac', [input.algorithm, input.key, input.data, 'hex']), 'hex',
    ]);
    case 'crypto.random': return sourceUtility('random', [input.length]);
    case 'crypto.aes': return sourceUtility(
      input.operation === 'encrypt' ? 'aes' : 'aesDecrypt',
      [input.data, input.algorithm, input.key, input.iv],
    );
    default: throw sourceFault('CAPABILITY_UNAVAILABLE');
  }
}

export const runtime = Object.freeze({
  async start(input) {
    if (session) throw sourceFault('STALE_SESSION');
    session = new SourceSession({
      ...input,
      preprocess,
      capabilities: createCapabilityClient({
        invoke(name, value, context) {
          if (name.startsWith('encoding.') || name.startsWith('crypto.')) {
            return localCapability(name, value);
          }
          const keys = Object.keys(value);
          return host('source.capability', [
            name, context.scope, keys, ...keys.map(key => value[key]),
          ]);
        },
      }),
      onDiagnostic: value => emit('source.diagnostic', value),
      createSandbox: options => {
        sandbox = new PortableSandbox({
          ...options, guest, taskContext, limits: input.limits,
          signal: session.kernel.signal,
        });
        return sandbox;
      },
    });
    return session.start();
  },
  activate() { session.activate(); return null; },
  async invoke(action, payload, timeoutMs) {
    const scope = taskContext();
    const controller = new AbortController();
    operations.set(scope, controller);
    try {
      return await session.invoke(action, payload, { signal: controller.signal, timeoutMs });
    } catch (error) {
      throw sourceSafeError(error);
    } finally {
      operations.delete(scope);
    }
  },
  cancel(scope, code) {
    operations.get(scope)?.abort(sourceFault(code));
    const invocationId = sandbox?.cancel(scope, sourceFault(code));
    if (invocationId !== undefined) session.kernel.endInvocation(invocationId);
    return null;
  },
  bridge(operation, ...args) { return sandbox.bridge(operation, ...args); },
  utility(operation, args) { return sourceUtility(operation, args); },
  async drain() { await session?.drain(); return null; },
  async close() {
    // The Node owner retains physical I/O until settlement. Do not spend the
    // same cleanup deadline a second time draining its mirrors in this Runtime.
    await session?.close({ drain: false });
    return null;
  },
  stats() { return session?.stats() ?? {}; },
});
