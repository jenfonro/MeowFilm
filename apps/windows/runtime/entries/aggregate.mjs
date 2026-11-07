import { startWindowsAppRuntime, reportWindowsRuntimeStartFailure } from '../bootstrap.mjs';
import { createRuntime } from '../../../runtime/entry.mjs';

// Current wire identity; one App, not a selectable aggregate product.
await startWindowsAppRuntime({
  createRuntime: config => createRuntime({ ...config, source: { platform: 'windows' } }),
}).catch(reportWindowsRuntimeStartFailure);
