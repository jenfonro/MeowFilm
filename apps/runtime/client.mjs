import { BridgeError, HTTP_PROTOCOL, createHttpClient } from '@meowfilm/runtime-bridge';
import { contracts } from './contracts.mjs';

export function createRuntimeClient({ baseUrl, timeout } = {}) {
  const client = createHttpClient({ baseUrl, timeout });
  return Object.freeze({
    call(method, params = {}, options = {}) {
      const selected = Object.entries(contracts).find(([, value]) => typeof method === 'string' && method.startsWith(value.prefix));
      if (!selected) return Promise.reject(new BridgeError('METHOD_NOT_FOUND'));
      const [id, { prefix, contract, codes }] = selected;
      const operation = method.slice(prefix.length);
      if (!/^[a-z]+(?:\.[a-z]+)*$(?![\s\S])/.test(operation)) return Promise.reject(new BridgeError('METHOD_NOT_FOUND'));
      return client.request({ ...options, path: `/v1/${id}/${operation}`, body: params, contract, codes });
    },
    upload(module, installationId, sourceRevision, bytes, { basename, ...options } = {}) {
      if (!Object.hasOwn(contracts, module) || !contracts[module].upload) {
        return Promise.reject(new BridgeError('METHOD_NOT_FOUND'));
      }
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
