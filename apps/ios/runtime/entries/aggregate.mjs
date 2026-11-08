import { join } from 'node:path';
import { startIOSAppRuntime, reportIOSRuntimeStartFailure } from '../bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../../../runtime/modules/cat-script.mjs';
import { createSourceRuntimeModule } from '../../../runtime/modules/source-runtime.mjs';

try {
  await startIOSAppRuntime({
    product: 'aggregate', assemble: ({ dataRoot }) => {
      const modules = [];
      try {
        modules.push(createMusicScriptModule({ dataRoot }));
        modules.push(createCatScriptModule({ dataRoot: join(dataRoot, 'cat') }));
        modules.push(createSourceRuntimeModule({ platform: 'ios' }));
        return modules;
      } catch (error) {
        for (const module of modules) void Promise.resolve(module.close()).catch(() => {});
        throw error;
      }
    },
  });
} catch {
  reportIOSRuntimeStartFailure();
}
