import { createNeteaseTokenProvider } from '@meowfilm/netease-token';
import { contracts } from '../contracts.mjs';

export function createNeteaseTokenModule({ provider = createNeteaseTokenProvider() } = {}) {
  const spec = contracts.netease;
  return Object.freeze({
    id: 'netease', version: spec.version, contract: spec.contract,
    methods: {
      'netease.token.get': async (input, context) => {
        try {
          if (!input || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).length) {
            return { ok: false, contract: spec.contract, code: 'INVALID_REQUEST' };
          }
          const token = await provider.get({ signal: context?.signal });
          return { ok: true, contract: spec.contract, value: { token } };
        } catch (error) {
          return { ok: false, contract: spec.contract, code: spec.codes.has(error.code) ? error.code : 'SDK_FAILURE' };
        }
      },
    },
    close: () => provider.close(),
  });
}
