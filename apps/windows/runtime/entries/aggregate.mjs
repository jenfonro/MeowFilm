import { startWindowsAppRuntime, reportWindowsRuntimeStartFailure } from '../bootstrap.mjs';

await startWindowsAppRuntime({ product: 'aggregate' }).catch(reportWindowsRuntimeStartFailure);
