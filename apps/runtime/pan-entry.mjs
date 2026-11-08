import { createAppRuntime } from './bootstrap.mjs';
import { createPanRuntimeModule } from './modules/pan-runtime.mjs';

export { createPanRuntimeModule };
export { createRuntimeClient } from './client.mjs';

/** A trusted in-process assembly entry, not another background process or platform launcher. */
export function createPanRuntime({ dataRoot, pan = {}, ...config } = {}) {
  return createAppRuntime({ ...config, product: 'pan',
    assemble: () => [createPanRuntimeModule({ ...pan, dataRoot })] });
}
export async function startPanRuntime(config) { return createPanRuntime(config).start(); }
