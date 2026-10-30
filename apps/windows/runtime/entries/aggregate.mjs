import { startWindowsAppRuntime, reportWindowsRuntimeStartFailure } from '../bootstrap.mjs';
import { createMusicScriptModule } from '../../../runtime/modules/music-script.mjs';

// Current wire identity; one App, not a selectable aggregate product.
await startWindowsAppRuntime({
  product: 'aggregate', assemble: ({ call }) => [createMusicScriptModule({ call })],
}).catch(reportWindowsRuntimeStartFailure);
