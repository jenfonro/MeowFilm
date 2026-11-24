import { startIOSAppRuntime, reportIOSRuntimeStartFailure } from '../bootstrap.mjs';

let stage = 'arguments';
try {
  await startIOSAppRuntime({
    product: 'aggregate',
    onStage: value => { stage = value; },
  });
} catch (error) {
  reportIOSRuntimeStartFailure(error, stage);
}
