import { startWindowsAppRuntime, reportWindowsRuntimeStartFailure, windowsScriptDataRoot } from '../bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../../../runtime/modules/cat-script.mjs';

// Current wire identity; one App, not a selectable aggregate product.
await startWindowsAppRuntime({
  product: 'aggregate', assemble: ({ call }) => [
    createMusicScriptModule({ call }),
    createCatScriptModule({ dataRoot: windowsScriptDataRoot() }),
  ],
}).catch(reportWindowsRuntimeStartFailure);
