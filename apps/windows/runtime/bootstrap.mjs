import { Socket } from 'node:net';
import { isAbsolute, resolve } from 'node:path';
import { BridgeError } from '@meowfilm/runtime-bridge';
import { attachConnectedStream } from '@meowfilm/runtime-bridge/descriptor';
import { createAppRuntime } from '../../runtime/bootstrap.mjs';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const sessionPattern = new RegExp(`^${uuid}$(?![\\s\\S])`);
const pipePattern = new RegExp(`^meowfilm-${uuid}$(?![\\s\\S])`);

/** Same fixed identity as native AppDataPaths. Never a script/argv-selected path. */
export function windowsScriptDataRoot(input = process.env.MEOWFILM_SCRIPT_DATA_ROOT) {
  if (typeof input !== 'string' || !isAbsolute(input) || /^[/\\]{2}/.test(input) ||
    /[\u0000-\u001f]/.test(input))
    throw new BridgeError('INVALID_MESSAGE');
  return resolve(input);
}

export function parseWindowsLaunchArguments(argv) {
  if (!Array.isArray(argv) || argv.length !== 4 || argv.some(value => typeof value !== 'string') ||
      argv[0] !== '--meow-pipe' || !pipePattern.test(argv[1]) ||
      argv[2] !== '--meow-session' || !sessionPattern.test(argv[3])) {
    throw new BridgeError('INVALID_MESSAGE');
  }
  return Object.freeze({ pipeName: argv[1], session: argv[3] });
}

/**
 * Connect only to a local, product-named pipe. The native launcher owns its
 * single-instance CurrentUserOnly server and verifies the child PID before RPC.
 * The session UUID is a generation, NOT proof of identity or an auth secret.
 * socketFactory is a deterministic-test seam, never a launch argument.
 */
export function connectWindowsNamedPipe(pipeName, {
  timeout = 5000, socketFactory = () => new Socket(),
} = {}) {
  if (process.platform !== 'win32' || typeof pipeName !== 'string' || !pipePattern.test(pipeName) ||
      !Number.isSafeInteger(timeout) || timeout < 1 || timeout > 30000 ||
      typeof socketFactory !== 'function') {
    return Promise.reject(new BridgeError('INVALID_MESSAGE'));
  }
  return new Promise((resolve, reject) => {
    let socket;
    try { socket = socketFactory(); }
    catch { reject(new BridgeError('CLOSED')); return; }
    if (!socket || ['on', 'once', 'off', 'connect', 'destroy'].some(name => typeof socket[name] !== 'function')) {
      reject(new BridgeError('INVALID_MESSAGE'));
      return;
    }
    let settled = false;
    const clear = () => {
      clearTimeout(timer);
      socket.off('connect', connected);
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
    // Keep an error listener until close, including the promise handoff between
    // connect and attachConnectedStream. Never surface the OS error/path/payload.
    const error = () => {
      if (settled) socket.destroy();
      else fail('CLOSED');
    };
    const closed = () => {
      fail('CLOSED');
      clear();
      socket.off('error', error);
    };
    const timer = setTimeout(() => fail('TIMEOUT'), timeout);
    socket.once('connect', connected);
    socket.on('error', error);
    socket.once('close', closed);
    try { socket.connect({ path: `\\\\.\\pipe\\${pipeName}` }); }
    catch { fail('CLOSED'); }
  });
}

/** Platform launch/transport only; shared assembly owns script modules and their instances. */
export async function startWindowsAppRuntime({
  product, assemble, argv = process.argv.slice(2), connectTimeout = 5000,
} = {}) {
  const launch = parseWindowsLaunchArguments(argv);
  const stream = await connectWindowsNamedPipe(launch.pipeName, { timeout: connectTimeout });
  try {
    if (stream.destroyed) throw new BridgeError('CLOSED');
    // Factories are passive and run only after connecting. Shared assembly owns
    // module disposal; end/error/close and repeated close all converge on it.
    return createAppRuntime({
      product, assemble,
      connect: options => attachConnectedStream(stream, { ...options, session: launch.session }),
    });
  } catch (error) {
    stream.destroy();
    throw error;
  }
}

export function reportWindowsRuntimeStartFailure() {
  // No argv, provider error message, stack, URL, business data or credentials.
  process.stderr.write('WINDOWS_RUNTIME_START_FAILED\n');
  process.exitCode = 1;
}
