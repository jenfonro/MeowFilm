import { createPanService } from '@meowfilm/pan-runtime';
import { contracts } from '../contracts.mjs';

export function createPanRuntimeModule(options) {
  const service = createPanService(options);
  const spec = contracts.pan;
  return {
    id: 'pan', version: spec.version, contract: spec.contract,
    methods: Object.fromEntries(spec.operations.map(operation => [spec.prefix + operation,
      async (input, context) => ({ ok: true, contract: spec.contract,
        value: await service.invoke(operation, input, context) })])),
    close: () => service.close(),
  };
}
