import { Socket } from 'node:net';
import { isAbsolute, resolve } from 'node:path';
import { BridgeError, HTTP_PROTOCOL, decodeJSONValue, validateEndpoint, validateSession } from '@meowfilm/runtime-bridge';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const sessionPattern = new RegExp(`^${uuid}$(?![\\s\\S])`);
const pipePattern = new RegExp(`^meowfilm-${uuid}$(?![\\s\\S])`);

const bootstrapBytes = 64 * 1024;

/** Same fixed identity as native AppDataPaths, delivered only through the private pipe. */
export function windowsScriptDataRoot(input) {
  if (typeof input !== 'string' || !isAbsolute(input) || /^[/\\]{2}/.test(input) ||
    /[\u0000-\u001f]/.test(input) ||
    process.platform === 'win32' && (!/^[A-Za-z]:[\\/]/.test(input) || input.slice(2).includes(':')))
    throw new BridgeError('INVALID_REQUEST');
  return resolve(input);
}

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
 * The session UUID is a generation, NOT proof of identity or an auth secret.
 * socketFactory is a deterministic-test seam, never a launch argument.
 */
export function connectWindowsNamedPipe(pipeName, {
  timeout = 5000, signal, socketFactory = () => new Socket(),
} = {}) {
  if (process.platform !== 'win32' || typeof pipeName !== 'string' || !pipePattern.test(pipeName) ||
      !Number.isSafeInteger(timeout) || timeout < 1 ||
      typeof socketFactory !== 'function') {
    return Promise.reject(new BridgeError('INVALID_REQUEST'));
  }
  if (signal?.aborted) return Promise.reject(new BridgeError('CANCELLED'));
  return new Promise((resolve, reject) => {
    let socket;
    try { socket = socketFactory(); }
    catch { reject(new BridgeError('CLOSED')); return; }
    if (!socket || ['on', 'once', 'off', 'connect', 'destroy'].some(name => typeof socket[name] !== 'function')) {
      reject(new BridgeError('INVALID_REQUEST'));
      return;
    }
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
    // Keep an error listener through the promise handoff to bootstrap.
    // Never surface the OS error/path/payload.
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

/**
 * One config line, one ready line, then a lifetime handle. Business traffic uses
 * the common HTTP listener. This function also permits connected-stream tests
 * without changing the production Windows pipe address or authentication gate.
 */
export async function startWindowsRuntimeOnPipe(stream, {
  sessionId, createRuntime, timeout = 5000, signal,
} = {}) {
  let runtime, startup, closing, stopError, started = false, stopped = false, configured = false;
  let length = 0, chunks = [];
  let acceptConfig, rejectConfig, rejectStopped;
  const config = new Promise((resolve, reject) => { acceptConfig = resolve; rejectConfig = reject; });
  const interrupted = new Promise((_, reject) => { rejectStopped = reject; });
  // Interruption can happen while startup or the ready write is still pending.
  interrupted.catch(() => {});
  const close = () => {
    if (closing) return closing;
    stopped = true;
    clearTimeout(timer);
    signal?.removeEventListener('abort', cancelled);
    chunks = [];
    closing = (async () => {
      try {
        // Wait for a racing listen operation before reporting resource cleanup.
        await startup?.catch(() => {});
        await runtime?.close();
      } finally {
        if (!stream.closed) await new Promise(resolve => {
          stream.once('close', resolve);
          stream.destroy();
        });
        stream.off('data', data);
        stream.off('end', ended);
        stream.off('close', ended);
        stream.off('error', failed);
      }
    })().catch(() => { throw new BridgeError('INVALID_RESPONSE'); });
    return closing;
  };
  const stop = code => {
    if (stopped) return;
    stopError = new BridgeError(code);
    rejectConfig(stopError);
    rejectStopped(stopError);
    void close().catch(() => { if (started) reportWindowsRuntimeStartFailure(); });
  };
  const ended = () => stop('CLOSED');
  const failed = () => stop('CLOSED');
  const cancelled = () => stop('CANCELLED');
  const data = bytes => {
    if (stopped) return;
    if (configured) { stop('INVALID_REQUEST'); return; }
    length += bytes.length;
    // The resource bound includes the line terminator, never business bodies.
    if (length > bootstrapBytes) { stop('INVALID_REQUEST'); return; }
    const newline = bytes.indexOf(10);
    if (newline !== -1 && newline !== bytes.length - 1) { stop('INVALID_REQUEST'); return; }
    chunks.push(bytes);
    if (newline === -1) return;
    configured = true;
    try {
      const value = decodeJSONValue(Buffer.concat(chunks, length).subarray(0, -1));
      chunks = [];
      if (value === null || typeof value !== 'object' || Array.isArray(value) ||
          Object.keys(value).length !== 3 || value.sessionId !== sessionId ||
          !Object.hasOwn(value, 'requestKey') || !Object.hasOwn(value, 'dataRoot')) {
        throw new BridgeError('INVALID_REQUEST');
      }
      validateSession({ sessionId: value.sessionId, key: value.requestKey });
      acceptConfig({ ...value, dataRoot: windowsScriptDataRoot(value.dataRoot) });
    } catch { stop('INVALID_REQUEST'); }
  };
  const timer = setTimeout(() => stop('TIMEOUT'), timeout);
  stream.on('data', data);
  stream.once('end', ended);
  stream.once('close', ended);
  stream.on('error', failed);
  signal?.addEventListener('abort', cancelled, { once: true });
  try {
    if (signal?.aborted) stop('CANCELLED');
    if (stream.destroyed || stream.readableEnded) stop('CLOSED');
    const input = await config;
    if (stopped) throw stopError;
    runtime = createRuntime(input);
    startup = Promise.resolve(runtime.start());
    await Promise.race([startup, interrupted]);
    if (stopped) throw stopError;
    validateEndpoint(runtime.baseUrl);
    const ready = Buffer.from(JSON.stringify({ protocol: HTTP_PROTOCOL, baseUrl: runtime.baseUrl }) + '\n');
    await Promise.race([new Promise((resolve, reject) => {
      stream.write(ready, error => error ? reject(new BridgeError('CLOSED')) : resolve());
    }), interrupted]);
    if (stopped) throw stopError;
    clearTimeout(timer);
    started = true;
    return Object.freeze({
      get baseUrl() { return runtime.baseUrl; },
      get state() { return runtime.state; },
      close,
    });
  } catch (error) {
    await close();
    throw error instanceof BridgeError ? error : new BridgeError('INVALID_RESPONSE');
  }
}

/** Platform launch only; the supplied public entry owns all business modules. */
export async function startWindowsAppRuntime({
  createRuntime, argv = process.argv.slice(2), connectTimeout = 5000, signal,
} = {}) {
  const launch = parseWindowsLaunchArguments(argv);
  const stream = await connectWindowsNamedPipe(launch.pipeName, { timeout: connectTimeout, signal });
  return startWindowsRuntimeOnPipe(stream, { sessionId: launch.session, createRuntime, timeout: connectTimeout, signal });
}

export function reportWindowsRuntimeStartFailure() {
  // No argv, provider error message, stack, URL, business data or credentials.
  process.stderr.write('WINDOWS_RUNTIME_START_FAILED\n');
  process.exitCode = 1;
}
