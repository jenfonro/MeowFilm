import { createNeteaseTokenProvider } from '@meowfilm/netease-token';
import { contracts } from '../contracts.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

export function createNeteaseTokenModule({ debug } = {}) {
  const activeProvider = createNeteaseTokenProvider({ debug });
  const spec = contracts.netease;
  return Object.freeze({
    id: 'netease', version: spec.version, contract: spec.contract,
    diagnostics: () => activeProvider.diagnostics(),
    methods: {
      'netease.token.get': async (input = {}, context) => {
        if (!object(input)) throw Object.assign(new Error('INVALID_REQUEST'), { code: 'INVALID_REQUEST' });
        const token = await activeProvider.get({ signal: context?.signal });
        return { ok: true, contract: spec.contract, value: { token } };
      },
    },
    close: () => activeProvider.close(),
  });
}
