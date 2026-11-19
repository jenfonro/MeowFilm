import { createAppRuntime } from './bootstrap.mjs';
import { createPanRuntimeModule } from './modules/pan-runtime.mjs';
import { configureStandaloneStorage } from '@meowfilm/storage';
import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';

export { createPanRuntimeModule };
export { createRuntimeClient } from './client.mjs';

/** A trusted in-process assembly entry, not another background process or platform launcher. */
export function createPanRuntime({ dataRoot, pan = {}, ...config } = {}) {
  configureStandaloneStorage(createDesktopStorageDriver);
  return createAppRuntime({ ...config, product: 'pan',
    assemble: () => [createPanRuntimeModule({ ...pan, dataRoot })] });
}
export async function startPanRuntime(config) { return createPanRuntime(config).start(); }
