import { createAppRuntime } from './bootstrap.mjs';
import { createMusicScriptModule } from './modules/music-script.mjs';
import { createCatScriptModule } from './modules/cat-script.mjs';
import { createSourceRuntimeModule } from './modules/source-runtime.mjs';
import { createPanRuntimeModule } from './modules/pan-runtime.mjs';

export { createMusicScriptModule, createCatScriptModule, createSourceRuntimeModule, createPanRuntimeModule };
export { createPanRuntime, startPanRuntime } from './pan-entry.mjs';
export { createTMDBDataModule, createDoubanDataModule } from './modules/metadata-data.mjs';
export { createNeteaseTokenModule } from './modules/netease-token.mjs';
export { createAppRuntime } from './bootstrap.mjs';
export { createRuntimeClient } from './client.mjs';

export function createRuntime({ dataRoot, music = {}, cat = {}, source = {}, pan, ...config } = {}) {
  return createAppRuntime({ ...config, product: 'aggregate', assemble: () => {
    const modules = [];
    try {
      modules.push(createMusicScriptModule({ ...music, dataRoot }));
      modules.push(createCatScriptModule({ ...cat, dataRoot }));
      modules.push(createSourceRuntimeModule(source));
      if (pan !== undefined) modules.push(createPanRuntimeModule({ ...pan, dataRoot }));
      return modules;
    } catch (error) {
      for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
      throw error;
    }
  } });
}
export async function startRuntime(config) { return createRuntime(config).start(); }
