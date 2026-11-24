import { Socket } from 'node:net';
import { isAbsolute, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { BridgeError, HTTP_PROTOCOL, decodeJSONValue, validateEndpoint, validateSession } from '@meowfilm/runtime-bridge';
import { createNodeDebug, parseDebugHandshake, serializeDebugError } from '../../runtime/debug.mjs';
import { createNativeSettings } from '../../runtime/native-settings.mjs';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const sessionPattern = new RegExp(`^${uuid}$(?![\\s\\S])`);
const pipePattern = new RegExp(`^meowfilm-${uuid}$(?![\\s\\S])`);
const DEBUG_ERROR_SYMBOL = Symbol.for('meowfilm.windowsBootstrapDebug');

/** Same fixed identity as native AppDataPaths, delivered only through the private pipe. */
export function windowsScriptDataRoot(input) {
  if (typeof input !== 'string' || !isAbsolute(input))
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
 * without changing the production Windows pipe address or process lifetime.
 */
export async function startWindowsRuntimeOnPipe(stream, {
  sessionId, createRuntime, timeout = 5000, signal,
} = {}) {
  let runtime, creating, startup, closing, stopError, settings, debug, started = false, stopped = false, configured = false;
  let length = 0, chunks = [];
  let acceptConfig, rejectConfig, rejectStopped;
  let exceptionMonitor;
  const config = new Promise((resolve, reject) => { acceptConfig = resolve; rejectConfig = reject; });
  const interrupted = new Promise((_, reject) => { rejectStopped = reject; });
  // Interruption can happen while startup or the ready write is still pending.
  interrupted.catch(() => {});
  const logNode = (level, event, data, extra = {}) => {
    if (!debug) return;
    debug.emit({ level, cat: 'runtime-node', sub: 'windows.bootstrap', event, data, ...extra });
  };
  const removeProcessHooks = () => {
    if (exceptionMonitor) { process.off('uncaughtExceptionMonitor', exceptionMonitor); exceptionMonitor = undefined; }
  };
  const installProcessHooks = () => {
    if (!debug || exceptionMonitor) return;
    exceptionMonitor = (error, origin) => {
      const isRejection = origin === 'unhandledRejection';
      logNode(
        isRejection ? 'error' : 'fatal',
        isRejection ? 'process.unhandledRejection' : 'process.uncaughtException',
        { sessionId, origin: origin ?? 'uncaughtException' },
        { error },
      );
    };
    process.on('uncaughtExceptionMonitor', exceptionMonitor);
  };
  const close = () => {
    if (closing) return closing;
    stopped = true;
    clearTimeout(timer);
    signal?.removeEventListener('abort', cancelled);
    chunks = [];
    removeProcessHooks();
    settings?.close();
    const closeStart = debug ? performance.now() : 0;
    logNode('info', 'bootstrap.close.begin', { sessionId });
    closing = (async () => {
      try {
        // Wait for any racing runtime creation or listen operation before closing.
        const created = await creating?.catch(() => undefined);
        const target = runtime ?? created;
        await startup?.catch(() => {});
        await target?.close();
      } finally {
        if (!stream.closed) await new Promise(resolve => {
          stream.once('close', resolve);
          stream.destroy();
        });
        stream.off('data', data);
        stream.off('end', ended);
        stream.off('close', ended);
        stream.off('error', failed);
        logNode('info', 'bootstrap.close.end', { sessionId },
          { durationMs: debug ? performance.now() - closeStart : undefined });
      }
    })().catch(error => {
      logNode('error', 'bootstrap.close.error', { sessionId }, { error });
      throw new BridgeError('INVALID_RESPONSE');
    });
    return closing;
  };
  const stop = code => {
    if (stopped) return;
    stopError = new BridgeError(code);
    logNode('warn', 'bootstrap.stop', { sessionId, code });
    rejectConfig(stopError);
    rejectStopped(stopError);
    void close().catch(() => { if (started) reportWindowsRuntimeStartFailure(); });
  };
  const ended = () => stop('CLOSED');
  const failed = () => stop('CLOSED');
  const cancelled = () => stop('CANCELLED');
  const data = bytes => {
    if (stopped) return;
    if (configured) {
      if (!settings) stop('INVALID_REQUEST');
      else settings.accept(bytes);
      return;
    }
    length += bytes.length;
    const newline = bytes.indexOf(10);
    if (newline !== -1 && newline !== bytes.length - 1) { stop('INVALID_REQUEST'); return; }
    chunks.push(bytes);
    if (newline === -1) return;
    configured = true;
    try {
      const value = decodeJSONValue(Buffer.concat(chunks, length).subarray(0, -1));
      chunks = [];
      if (value === null || typeof value !== 'object' || Array.isArray(value) ||
          value.sessionId !== sessionId ||
          !Object.hasOwn(value, 'dataRoot')) {
        throw new BridgeError('INVALID_REQUEST');
      }
      validateSession({ sessionId: value.sessionId });
      const parsedDebug = Object.hasOwn(value, 'debug') && value.debug !== undefined
        ? parseDebugHandshake(value.debug)
        : null;
      if (parsedDebug) {
        debug = createNodeDebug(parsedDebug);
        installProcessHooks();
        logNode('info', 'bootstrap.configured', {
          sessionId: value.sessionId,
          dataRoot: value.dataRoot,
          nativeSettings: value.nativeSettings === true,
          debug: debug.getConfig(),
        });
      }
      if (value.nativeSettings === true) settings = createNativeSettings(stream, stop, { debug });
      acceptConfig({ sessionId: value.sessionId, dataRoot: windowsScriptDataRoot(value.dataRoot),
        ...(settings ? { settings } : {}),
        ...(debug ? { debug } : {}) });
    } catch (error) {
      logNode('error', 'bootstrap.config.invalid', { sessionId }, { error });
      stop('INVALID_REQUEST');
    }
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
    const startedAt = debug ? performance.now() : 0;
    logNode('info', 'bootstrap.createRuntime', { sessionId: input.sessionId, dataRoot: input.dataRoot });
    creating = Promise.resolve().then(() => createRuntime(input));
    runtime = await Promise.race([creating, interrupted]);
    if (stopped) throw stopError;
    logNode('info', 'bootstrap.runtime.start', { sessionId: input.sessionId });
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
    logNode('info', 'bootstrap.ready', { sessionId: input.sessionId, baseUrl: runtime.baseUrl },
      { durationMs: debug ? performance.now() - startedAt : undefined });
    return Object.freeze({
      get baseUrl() { return runtime.baseUrl; },
      get state() { return runtime.state; },
      close,
    });
  } catch (error) {
    logNode('error', 'bootstrap.failed', { sessionId }, { error });
    await close();
    const wrapped = error instanceof BridgeError ? error : new BridgeError('INVALID_RESPONSE');
    if (debug) {
      if (Object.isExtensible(wrapped)) {
        Object.defineProperty(wrapped, DEBUG_ERROR_SYMBOL, {
          value: { debug, cause: error },
          enumerable: false,
          configurable: true,
        });
      }
    }
    throw wrapped;
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

export function reportWindowsRuntimeStartFailure(error) {
  // No argv, provider error message, stack, URL, business data or credentials.
  process.stderr.write('WINDOWS_RUNTIME_START_FAILED\n');
  const debugInfo = error?.[DEBUG_ERROR_SYMBOL];
  if (debugInfo?.debug) {
    const serialized = serializeDebugError(debugInfo.cause ?? error);
    debugInfo.debug.emit({
      level: 'fatal',
      cat: 'runtime-node',
      sub: 'windows.bootstrap',
      event: 'bootstrap.start_failed',
      data: { code: error?.code ?? null },
      error: debugInfo.cause ?? error,
    });
    process.stderr.write(`WINDOWS_RUNTIME_START_FAILED_DEBUG ${JSON.stringify(serialized)}\n`);
  }
  process.exitCode = 1;
}
