import { closeSync } from 'node:fs';
import { Socket } from 'node:net';
import { BridgeError } from '@meowfilm/runtime-bridge';
import { duplicateBootstrapDescriptor } from '@meowfilm/script-host/bootstrap';
import { createNativeStorageDriver } from '@meowfilm/script-host/storage';
import {
  startRuntimeOnChannel,
  reportChannelStartFailure,
} from '../../runtime/channel-bootstrap.mjs';

const startupStages = new Set([
  'arguments', 'native-binding', 'bootstrap-channel', 'configuration', 'assembly',
  'music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease', 'music-platform', 'listen', 'endpoint',
]);

/** The only launch argument is an App-owned, connected private descriptor. */
export function parseIOSLaunchArguments(argv) {
  if (!Array.isArray(argv) || argv.length !== 2 || argv[0] !== '--meow-bootstrap-fd' ||
      typeof argv[1] !== 'string' || !/^[1-9][0-9]*$(?![\s\S])/.test(argv[1]) ||
      !Number.isSafeInteger(Number(argv[1])) || Number(argv[1]) < 3 || Number(argv[1]) > 0x7fffffff) {
    throw new BridgeError('INVALID_REQUEST');
  }
  return Object.freeze({ descriptor: Number(argv[1]) });
}

/** An escaped script failure drains this runtime without terminating the iOS host App. */
function installIOSFailureBoundary({ close, getDebug }) {
  let failed = false;
  const handle = kind => error => {
    try {
      getDebug()?.emit({
        level: kind === 'uncaughtException' ? 'fatal' : 'error',
        cat: 'runtime-node',
        sub: 'ios.bootstrap',
        event: `process.${kind}`,
        error,
      });
    } catch { /* Debug logging must not mask shutdown. */ }
    if (failed) return;
    failed = true;
    try { process.stderr.write('IOS_RUNTIME_FAILED\n'); } catch { /* Ignore stderr failure. */ }
    void Promise.resolve().then(() => close()).catch(() => {});
  };
  const onUncaught = handle('uncaughtException');
  const onRejection = handle('unhandledRejection');
  process.on('uncaughtException', onUncaught);
  process.on('unhandledRejection', onRejection);
  return () => {
    process.off('uncaughtException', onUncaught);
    process.off('unhandledRejection', onRejection);
  };
}

/** Launch the embedded iOS Node runtime over its private socketpair descriptor. */
export async function startIOSAppRuntime({
  product = 'aggregate', argv = process.argv.slice(2), onStage = () => {},
} = {}) {
  const bufferedStages = [];
  const record = stage => {
    onStage(stage);
    bufferedStages.push({ stage, ts: new Date().toISOString() });
  };
  record('arguments');
  const { descriptor } = parseIOSLaunchArguments(argv);
  record('native-binding');
  const duplicate = duplicateBootstrapDescriptor(descriptor);
  record('bootstrap-channel');
  let channel;
  try {
    channel = new Socket({ fd: duplicate, readable: true, writable: true, allowHalfOpen: true });
  } catch (error) {
    closeSync(duplicate);
    throw error;
  }
  return startRuntimeOnChannel(channel, {
    platform: 'ios',
    product,
    storageDriver: createNativeStorageDriver,
    storageInitialize: config => config.nativeSettings !== true,
    sourceConfigurationOwner: 'runtime',
    onStage,
    bufferedStages,
    installProcessHooks: installIOSFailureBoundary,
  });
}

export function reportIOSRuntimeStartFailure(error, stage) {
  reportChannelStartFailure({
    platform: 'ios',
    prefix: 'IOS_RUNTIME_START_FAILED',
    error,
    stage,
    allowedStages: startupStages,
  });
}
