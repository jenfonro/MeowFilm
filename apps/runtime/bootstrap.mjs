import { BridgeError } from '@meowfilm/runtime-bridge';

const identifier = /^[a-z][a-z0-9-]{0,63}$(?![\s\S])/;
const methodName = /^[a-zA-Z][a-zA-Z0-9_.-]{0,127}$(?![\s\S])/;

/** Thin App script assembly only. No capability switch, source selection, listener,
 * filesystem access, credential lookup or implicit permission grant lives here.
 */
export function createAppRuntime({ product, modules, assemble, connect }) {
  if (typeof product !== 'string' || !identifier.test(product) || typeof connect !== 'function') {
    throw new BridgeError('INVALID_MESSAGE');
  }
  // App-owned factories receive a narrow outbound RPC dependency, never a
  // global peer/service locator. Assembly is synchronous and must be passive.
  let peer;
  let disposed = false;
  if (assemble !== undefined) {
    if (modules !== undefined || typeof assemble !== 'function') throw new BridgeError('INVALID_MESSAGE');
    modules = assemble({
      call: (method, params, options) => {
        if (!peer || disposed) return Promise.reject(new BridgeError('CLOSED'));
        return peer.call(method, params, options);
      }
    });
  }
  if (!Array.isArray(modules) || !modules.length) throw new BridgeError('INVALID_MESSAGE');
  const names = new Set();
  const methods = Object.create(null);
  const descriptors = [];
  try {
    for (const module of modules) {
      if (!module || typeof module.id !== 'string' || !identifier.test(module.id) || names.has(module.id) ||
          typeof module.version !== 'string' || !/^\d+\.\d+\.\d+$(?![\s\S])/.test(module.version) ||
          !module.methods || Array.isArray(module.methods) || typeof module.methods !== 'object' ||
          typeof module.close !== 'function') throw new BridgeError('INVALID_MESSAGE');
      names.add(module.id);
      const entries = Object.entries(module.methods);
      for (const [name, handler] of entries) {
        if (!methodName.test(name) || !name.startsWith(module.id + '.') ||
            Object.hasOwn(methods, name) || typeof handler !== 'function') throw new BridgeError('INVALID_MESSAGE');
        methods[name] = handler;
      }
      descriptors.push(Object.freeze({ id: module.id, version: module.version, methods: entries.map(([name]) => name).sort() }));
    }
  } catch (error) {
    disposed = true;
    for (const module of new Set(modules)) {
      void Promise.resolve().then(() => {
        if (typeof module?.close === 'function') return module.close();
      }).catch(() => {});
    }
    throw error;
  }
  let disposal;
  const dispose = () => disposal ??= (async () => {
    disposed = true;
    const results = await Promise.allSettled(modules.map(module => Promise.resolve().then(() => module.close())));
    if (results.some(result => result.status === 'rejected' || result.value?.ok === false)) {
      throw new BridgeError('INTERNAL');
    }
  })();
  for (const [name, handler] of Object.entries(methods)) {
    methods[name] = (params, context) => {
      if (disposed) throw new BridgeError('CLOSED');
      return handler(params, context);
    };
  }
  methods['runtime.describe'] = params => {
    if (params !== null) throw new BridgeError('INVALID_MESSAGE');
    // An inventory of the methods wired into this exact runtime instance.
    return { protocol: 1, product, modules: descriptors };
  };
  try {
    peer = connect({ methods, onClose: () => { void dispose().catch(() => {}); } });
  } catch (error) {
    void dispose().catch(() => {});
    throw error;
  }
  return Object.freeze({
    peer,
    async close() { peer.close(); await dispose(); }
  });
}
