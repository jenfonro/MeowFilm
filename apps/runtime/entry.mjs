import { createAppRuntime } from './bootstrap.mjs';
import { createMusicScriptModule } from './modules/music-script.mjs';
import { createCatScriptModule } from './modules/cat-script.mjs';
import { createSourceRuntimeModule } from './modules/source-runtime.mjs';

export { createMusicScriptModule, createCatScriptModule, createSourceRuntimeModule };
export { createAppRuntime } from './bootstrap.mjs';
export { createRuntimeClient, createCallbackServer } from './client.mjs';

export function createRuntime({ dataRoot, music = {}, cat = {}, source = {}, ...config } = {}) {
  return createAppRuntime({ ...config, product: 'aggregate', assemble: ({ call }) => {
    const modules = [];
    try {
      modules.push(createMusicScriptModule({ ...music, call }));
      modules.push(createCatScriptModule({ ...cat, dataRoot }));
      modules.push(createSourceRuntimeModule({ ...source, call }));
      return modules;
    } catch (error) {
      for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
      throw error;
    }
  } });
}
export async function startRuntime(config) { return createRuntime(config).start(); }
