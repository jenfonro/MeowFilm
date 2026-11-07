import { join } from 'node:path';
import { startIOSAppRuntime, reportIOSRuntimeStartFailure } from '../bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../../../runtime/modules/cat-script.mjs';

// Same wire identity as the other platform runtimes. The iOS assembly has no
// source module: the release-sync asm.js engine has no task-context hook.
try {
  startIOSAppRuntime({
    product: 'aggregate', assemble: ({ dataRoot }) => [
      createMusicScriptModule({ dataRoot }),
      createCatScriptModule({ dataRoot: join(dataRoot, 'cat') }),
    ],
  });
} catch {
  reportIOSRuntimeStartFailure();
}
