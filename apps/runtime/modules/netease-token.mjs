import { createNeteaseTokenProvider } from '@meowfilm/netease-token';
import { contracts } from '../contracts.mjs';

export function createNeteaseTokenModule({ provider, debug } = {}) {
  const activeProvider = provider ?? createNeteaseTokenProvider({ debug });
  const spec = contracts.netease;
  return Object.freeze({
    id: 'netease', version: spec.version, contract: spec.contract,
    diagnostics: () => activeProvider.diagnostics(),
    methods: {
      'netease.token.get': async (input, context) => {
        try {
          const token = await activeProvider.get({ signal: context?.signal });
          return { ok: true, contract: spec.contract, value: { token } };
        } catch (error) {
          return { ok: false, contract: spec.contract, code: spec.codes.has(error.code) ? error.code : 'SDK_FAILURE' };
        }
      },
    },
    close: () => activeProvider.close(),
  });
}
