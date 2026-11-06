import { BridgeError, HTTP_PROTOCOL, createHttpClient, createHttpServer } from '@meowfilm/runtime-bridge';
import { contracts, storageCodes, storageMethods } from './contracts.mjs';

export function createRuntimeClient({ baseUrl, sessionId, requestKey, timeout } = {}) {
  const client = createHttpClient({ baseUrl, sessionId, key: requestKey, timeout });
  return Object.freeze({
    call(method, params = {}, options = {}) {
      const selected = Object.entries(contracts).find(([, value]) => typeof method === 'string' && method.startsWith(value.prefix));
      if (!selected) return Promise.reject(new BridgeError('METHOD_NOT_FOUND'));
      const [id, { prefix, contract, codes }] = selected;
      const operation = method.slice(prefix.length);
      if (!/^[a-z]+(?:\.[a-z]+)?$(?![\s\S])/.test(operation)) return Promise.reject(new BridgeError('METHOD_NOT_FOUND'));
      return client.request({ ...options, path: `/v1/${id}/${operation}`, body: params, contract, codes });
    },
    upload(module, installationId, sourceRevision, bytes, { basename, ...options } = {}) {
      if (!Object.hasOwn(contracts, module)) return Promise.reject(new BridgeError('METHOD_NOT_FOUND'));
      const { contract, codes } = contracts[module];
      return client.request({ ...options, method: 'PUT', raw: true,
        path: `/v1/${module}/sources/${encodeURIComponent(installationId)}/${sourceRevision}` +
          (basename === undefined ? '' : `?basename=${encodeURIComponent(basename)}`), body: bytes, contract, codes });
    },
    describe: options => client.request({ ...options, method: 'GET', path: '/v1/describe', contract: HTTP_PROTOCOL }),
    shutdown: options => client.request({ ...options, path: '/v1/close', body: {} }),
    close: () => client.close(),
  });
}

/** Adapt client-owned storage handlers; grant/scope/transaction policy stays with their owner. */
export function createCallbackServer({ sessionId, callbackKey, methods, onClose } = {}) {
  const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
  if (!object(methods) || Object.entries(methods).some(([name, handler]) =>
    !storageMethods.has(name) || typeof handler !== 'function')) throw new BridgeError('INVALID_REQUEST');
  return createHttpServer({ sessionId, key: callbackKey, onClose, resolve: ({ method, url }) => {
    if (method !== 'POST' || url.search) return;
    const name = url.pathname.slice('/v1/capabilities/'.length);
    if (url.pathname !== `/v1/capabilities/${name}` || !storageMethods.has(name) || !Object.hasOwn(methods, name)) return;
    return { contract: HTTP_PROTOCOL, codes: storageCodes, contentType: 'application/json',
      handler: async (input, context) => {
        let reply;
        try { reply = await methods[name](input, context); }
        catch (error) { reply = { ok: false, code: error?.code }; }
        if (!object(reply) || Object.keys(reply).length !== 2 || typeof reply.ok !== 'boolean' ||
            !(reply.ok ? Object.hasOwn(reply, 'value') : Object.hasOwn(reply, 'code'))) {
          reply = { ok: false, code: 'STORAGE_ERROR' };
        }
        return reply?.ok === true ? { ok: true, contract: HTTP_PROTOCOL, value: reply.value }
          : { ok: false, contract: HTTP_PROTOCOL, code: storageCodes.has(reply?.code) ? reply.code : 'STORAGE_ERROR' };
      } };
  } });
}
