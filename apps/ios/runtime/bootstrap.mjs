import { isAbsolute } from 'node:path';
import { BridgeError } from '@meowfilm/runtime-bridge';
import { createDescriptorPeer } from '@meowfilm/runtime-bridge/descriptor';
import { createAppRuntime } from '../../runtime/bootstrap.mjs';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const sessionPattern = new RegExp(`^${uuid}$(?![\\s\\S])`);
const descriptorPattern = /^[1-9][0-9]{0,8}$(?![\s\S])/;

/**
 * The native launcher passes exactly one connected AF_UNIX descriptor, the
 * runtime session generation and the App-owned script data directory. The
 * session UUID is a generation, NOT proof of identity or an auth secret.
 */
export function parseIOSLaunchArguments(argv) {
  if (!Array.isArray(argv) || argv.length !== 6 || argv.some(value => typeof value !== 'string') ||
      argv[0] !== '--meow-fd' || !descriptorPattern.test(argv[1]) || Number(argv[1]) < 3 ||
      argv[2] !== '--meow-session' || !sessionPattern.test(argv[3]) ||
      argv[4] !== '--meow-data' || !isAbsolute(argv[5]) || /[\u0000-\u001f\u007f]/.test(argv[5])) {
    throw new BridgeError('INVALID_MESSAGE');
  }
  return Object.freeze({ descriptor: Number(argv[1]), session: argv[3], dataRoot: argv[5] });
}

/**
 * Node runs inside the App process, so an escaped runtime failure must close
 * this runtime instead of terminating the App. The native owner observes the
 * closed descriptor and keeps scripts unavailable for the rest of this run.
 */
export function installIOSFailureBoundary(runtime, { target = process, report = reportIOSRuntimeFailure } = {}) {
  let failed = false;
  const fail = () => {
    if (failed) return;
    failed = true;
    try { report(); } catch { /* Reporting must not mask the shutdown. */ }
    void Promise.resolve().then(() => runtime.close()).catch(() => {});
  };
  target.on('uncaughtException', fail);
  return fail;
}

/** Platform launch/transport only; shared assembly owns script modules and their instances. */
export function startIOSAppRuntime({ product, assemble, argv = process.argv.slice(2) } = {}) {
  const launch = parseIOSLaunchArguments(argv);
  if (typeof assemble !== 'function') throw new BridgeError('INVALID_MESSAGE');
  // Factories are passive and run only after the descriptor is attached.
  const runtime = createAppRuntime({
    product, assemble: options => assemble({ ...options, dataRoot: launch.dataRoot }),
    connect: options => createDescriptorPeer(launch.descriptor, { ...options, session: launch.session }),
  });
  installIOSFailureBoundary(runtime);
  return runtime;
}

function reportIOSRuntimeFailure() {
  // No argv, provider error message, stack, URL, business data or credentials.
  process.stderr.write('IOS_RUNTIME_FAILED\n');
}

export function reportIOSRuntimeStartFailure() {
  process.stderr.write('IOS_RUNTIME_START_FAILED\n');
  process.exitCode = 1;
}
