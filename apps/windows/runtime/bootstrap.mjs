import { Socket } from 'node:net';
import { BridgeError } from '@meowfilm/runtime-bridge';
import { createDesktopStorageDriver } from '@meowfilm/script-host/storage';
import {
  startRuntimeOnChannel,
  reportChannelStartFailure,
} from '../../runtime/channel-bootstrap.mjs';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const sessionPattern = new RegExp(`^${uuid}$(?![\\s\\S])`);
const pipePattern = new RegExp(`^meowfilm-${uuid}$(?![\\s\\S])`);

export function parseWindowsLaunchArguments(argv) {
  if (!Array.isArray(argv) || argv.length !== 4 || argv.some(value => typeof value !== 'string') ||
      argv[0] !== '--meow-pipe' || !pipePattern.test(argv[1]) ||
      argv[2] !== '--meow-session' || !sessionPattern.test(argv[3])) {
    throw new BridgeError('INVALID_REQUEST');
  }
  return Object.freeze({ pipeName: argv[1], session: argv[3] });
}

/**
 * Connect only to a local, product-named pipe. The native launcher owns its
 * single-instance CurrentUserOnly server and verifies the child PID before config.
 */
export function connectWindowsNamedPipe(pipeName, { timeout = 5000, signal } = {}) {
  if (process.platform !== 'win32' || typeof pipeName !== 'string' || !pipePattern.test(pipeName) ||
      !Number.isSafeInteger(timeout) || timeout < 1) {
    return Promise.reject(new BridgeError('INVALID_REQUEST'));
  }
  if (signal?.aborted) return Promise.reject(new BridgeError('CANCELLED'));
  return new Promise((resolve, reject) => {
    const socket = new Socket();
    let settled = false;
    const clear = () => {
      clearTimeout(timer);
      socket.off('connect', connected);
      signal?.removeEventListener('abort', cancelled);
    };
    const fail = code => {
      if (settled) return;
      settled = true;
      clear();
      socket.destroy();
      reject(new BridgeError(code));
    };
    const connected = () => {
      if (settled) return;
      if (socket.destroyed) { fail('CLOSED'); return; }
      settled = true;
      clear();
      resolve(socket);
    };
    const error = () => {
      if (settled) socket.destroy();
      else fail('CLOSED');
    };
    const closed = () => {
      fail('CLOSED');
      clear();
      socket.off('error', error);
    };
    const cancelled = () => fail('CANCELLED');
    const timer = setTimeout(() => fail('TIMEOUT'), timeout);
    signal?.addEventListener('abort', cancelled, { once: true });
    socket.once('connect', connected);
    socket.on('error', error);
    socket.once('close', closed);
    try { socket.connect({ path: `\\\\.\\pipe\\${pipeName}` }); }
    catch { fail('CLOSED'); }
  });
}

function installWindowsExceptionMonitor({ getDebug, getSessionId }) {
  const monitor = (error, origin) => {
    const debug = getDebug();
    if (!debug) return;
    const isRejection = origin === 'unhandledRejection';
    debug.emit({
      level: isRejection ? 'error' : 'fatal',
      cat: 'runtime-node',
      sub: 'windows.bootstrap',
      event: isRejection ? 'process.unhandledRejection' : 'process.uncaughtException',
      data: { sessionId: getSessionId(), origin: origin ?? 'uncaughtException' },
      error,
    });
  };
  process.on('uncaughtExceptionMonitor', monitor);
  return () => process.off('uncaughtExceptionMonitor', monitor);
}

/** Launch the Windows child Node runtime over its private Named Pipe. */
export async function startWindowsAppRuntime({
  product = 'aggregate', argv = process.argv.slice(2), connectTimeout = 5000, signal,
} = {}) {
  const launch = parseWindowsLaunchArguments(argv);
  const stream = await connectWindowsNamedPipe(launch.pipeName, { timeout: connectTimeout, signal });
  return startRuntimeOnChannel(stream, {
    platform: 'windows',
    product,
    expectedSessionId: launch.session,
    storageDriver: createDesktopStorageDriver,
    storageInitialize: false,
    sourceConfigurationOwner: 'native',
    timeout: connectTimeout,
    signal,
    installProcessHooks: installWindowsExceptionMonitor,
    onStartedCloseFailure: () => reportWindowsRuntimeStartFailure(),
    wrapError: true,
  });
}

export function reportWindowsRuntimeStartFailure(error) {
  reportChannelStartFailure({
    platform: 'windows',
    prefix: 'WINDOWS_RUNTIME_START_FAILED',
    error,
  });
}
