import { BridgeError, HTTP_PROTOCOL, createHttpServer, validateSession } from '@meowfilm/runtime-bridge';
import { contracts } from './contracts.mjs';
import { sourceByteLimit } from './modules/source-candidates.mjs';

const invalid = () => { throw new BridgeError('INVALID_REQUEST'); };
const publicCodes = new Set(['INVALID_REQUEST', 'METHOD_NOT_FOUND', 'PERMISSION_DENIED',
  'CLOSED', 'CANCELLED', 'TIMEOUT', 'INVALID_RESPONSE']);
const success = value => ({ ok: true, contract: HTTP_PROTOCOL, value });
const failureCode = id => ({ music: 'SCRIPT_FAILURE', source: 'SOURCE_FAILURE',
  cat: 'SCRIPT_EXCEPTION', pan: 'PAN_FAILURE', tmdb: 'UPSTREAM_FORMAT', douban: 'UPSTREAM_FORMAT',
  netease: 'SDK_FAILURE' })[id];

/** Trusted session configuration; no platform bootstrap or persistent identity. */
export function createAppRuntime({ sessionId, requestKey,
  product = 'aggregate', modules, assemble } = {}) {
  validateSession({ sessionId, key: requestKey });
  if (typeof product !== 'string' || !/^[a-z][a-z0-9-]{0,63}$(?![\s\S])/.test(product) ||
      assemble !== undefined && (modules !== undefined || typeof assemble !== 'function')) invalid();
  let disposed = false, disposal, shutdown, closing;
  const dispose = () => disposal ??= (async () => {
    disposed = true;
    const results = await Promise.allSettled((Array.isArray(modules) ? [...new Set(modules)] : [])
      .map(module => Promise.resolve().then(() => module?.close?.())));
    if (results.some(result => result.status === 'rejected' || result.value?.ok === false)) {
      throw new BridgeError('INVALID_RESPONSE');
    }
  })();
  const descriptors = [], methods = new Map(), owners = new Map();
  try {
    if (assemble) modules = assemble();
    if (!Array.isArray(modules) || !modules.length) invalid();
    for (const module of modules) {
      const contract = contracts[module?.id];
      if (!contract || owners.has(module.id) || module.version !== contract.version ||
          module.contract !== contract.contract || typeof module.close !== 'function' ||
          contract.upload && typeof module.upload !== 'function' ||
          module.upload !== undefined && typeof module.upload !== 'function' ||
          module.image !== undefined && typeof module.image !== 'function' ||
          !module.methods || typeof module.methods !== 'object') invalid();
      const entries = Object.entries(module.methods);
      for (const [name, handler] of entries) {
        if (!name.startsWith(contract.prefix) || !contract.operations.includes(name.slice(contract.prefix.length)) ||
            typeof handler !== 'function' || methods.has(name)) invalid();
        methods.set(name, handler);
      }
      owners.set(module.id, module);
      descriptors.push({ id: module.id, version: module.version, contract: module.contract,
        methods: entries.map(([name]) => name).sort() });
    }
  } catch (error) { void dispose().catch(() => {}); throw error; }
  const server = createHttpServer({ sessionId, key: requestKey, resolve: ({ method, url }) => {
    if (method === 'GET' && url.pathname === '/v1/describe' && !url.search) {
      return { contract: HTTP_PROTOCOL, codes: publicCodes,
        handler: () => success({ protocol: HTTP_PROTOCOL, product, modules: descriptors }) };
    }
    if (method === 'POST' && url.pathname === '/v1/close' && !url.search) {
      return { contract: HTTP_PROTOCOL, codes: publicCodes, contentType: 'application/json',
        validate(input) {
          if (input === null || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).length) invalid();
        }, handler: async (_input, context) => {
        await (shutdown ??= (async () => {
          const drained = server.abortActive(context.requestId);
          const results = await Promise.allSettled([dispose(), drained]);
          if (results.some(result => result.status === 'rejected')) throw new BridgeError('INVALID_RESPONSE');
        })());
        return success({ closed: true, cleanupComplete: true });
      } };
    }
    const parts = url.pathname.split('/');
    const module = owners.get(parts[2]), contract = contracts[parts[2]];
    if (!module || parts[1] !== 'v1') return;
    if (method === 'GET' && parts[3] === 'images' && module.image && !url.search) {
      return { contract: module.contract, codes: contract.codes, responseType: 'stream',
        handler: async (_input, context) => {
          if (disposed) return { ok: false, contract: module.contract, code: 'CLOSED' };
          try { return await module.image(url.pathname, context); }
          catch (error) {
            return { ok: false, contract: module.contract,
              code: contract.codes.has(error?.code) ? error.code : failureCode(module.id) };
          }
        } };
    }
    if (method === 'PUT' && parts.length === 6 && parts[3] === 'sources' && module.upload) {
      let installationId, basename;
      try {
        installationId = decodeURIComponent(parts[4]);
        const query = url.search.slice(1).split('&').filter(Boolean);
        if (query.length > 1 || query.length && !query[0].startsWith('basename=')) invalid();
        if (query.length) basename = decodeURIComponent(query[0].slice('basename='.length));
      } catch { invalid(); }
      if (!/^[1-9][0-9]*$(?![\s\S])/.test(parts[5]) || !Number.isSafeInteger(Number(parts[5]))) invalid();
      return { contract: module.contract, codes: contract.codes, contentType: 'application/octet-stream',
        maximumBytes: sourceByteLimit, limitCode: module.id === 'cat' ? 'RESOURCE_LIMIT' : 'LIMIT',
        status: reply => reply.ok ? 200 : ['LIMIT', 'RESOURCE_LIMIT'].includes(reply.code) ? 413 : 400,
        handler: (bytes, context) => module.upload({ installationId, sourceRevision: Number(parts[5]),
          ...(basename === undefined ? {} : { basename }) }, bytes, context) };
    }
    if (method !== 'POST' || parts.length !== 4 || url.search) return;
    const handler = methods.get(contract.prefix + parts[3]);
    if (!handler) return;
    return { contract: module.contract, codes: contract.codes, contentType: 'application/json',
      handler: async (input, context) => {
        if (disposed) throw new BridgeError('CLOSED');
        try {
          const reply = await handler(input, context);
          if (reply?.ok === false && reply.contract === contract.contract && !contract.codes.has(reply.code)) {
            return { ok: false, contract: contract.contract, code: failureCode(module.id) };
          }
          return reply;
        } catch (error) {
          return { ok: false, contract: contract.contract, code: contract.codes.has(error?.code) ? error.code
            : failureCode(module.id) };
        }
      } };
  } });
  const runtime = Object.freeze({
    get baseUrl() { return server.baseUrl; },
    get state() { return server.state; },
    async start() {
      if (disposed) throw new BridgeError('CLOSED');
      try { await server.start(); return runtime; }
      catch (error) { await dispose().catch(() => {}); throw error; }
    },
    close() {
      return closing ??= (async () => {
        const results = await Promise.allSettled([server.close(), dispose()]);
        if (results.some(result => result.status === 'rejected')) throw new BridgeError('INVALID_RESPONSE');
      })();
    },
  });
  return runtime;
}
