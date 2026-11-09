import { startWindowsAppRuntime, reportWindowsRuntimeStartFailure } from '../bootstrap.mjs';
import { createAppRuntime, createMusicScriptModule, createCatScriptModule, createSourceRuntimeModule, createTMDBDataModule } from '../../../runtime/entry.mjs';

// Current wire identity; one App, not a selectable aggregate product.
await startWindowsAppRuntime({
  createRuntime: ({ dataRoot, ...config }) => createAppRuntime({ ...config, product: 'aggregate', assemble: () => {
    const modules = [];
    try {
      modules.push(createMusicScriptModule({ dataRoot }));
      modules.push(createCatScriptModule({ dataRoot }));
      modules.push(createSourceRuntimeModule({ platform: 'windows' }));
      modules.push(createTMDBDataModule({ dataRoot }));
      return modules;
    } catch (error) {
      for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
      throw error;
    }
  } }),
}).catch(reportWindowsRuntimeStartFailure);
