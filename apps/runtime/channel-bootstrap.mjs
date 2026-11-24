import { isAbsolute, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { BridgeError, HTTP_PROTOCOL, decodeJSONValue, validateEndpoint, validateSession } from '@meowfilm/runtime-bridge';
import { configureStorage, setStorageObserver } from '@meowfilm/storage';
import { createAppRuntime } from './bootstrap.mjs';
import { createNodeDebug, parseDebugHandshake, serializeDebugError } from './debug.mjs';
import { assembleAppModules } from './entry.mjs';
import { createNativeSettings } from './native-settings.mjs';

const DEBUG_ERROR_SYMBOL = Symbol.for('meowfilm.platformBootstrapDebug');

export function parseChannelConfiguration(bytes, { expectedSessionId } = {}) {
  const value = decodeJSONValue(bytes);
  if (value === null || typeof value !== 'object' || Array.isArray(value) ||
      !Object.hasOwn(value, 'sessionId') ||
      !Object.hasOwn(value, 'dataRoot') ||
      typeof value.dataRoot !== 'string' || !isAbsolute(value.dataRoot)) {
    throw new BridgeError('INVALID_REQUEST');
  }
  if (expectedSessionId !== undefined && value.sessionId !== expectedSessionId) {
    throw new BridgeError('INVALID_REQUEST');
  }
  validateSession({ sessionId: value.sessionId });
  const debug = Object.hasOwn(value, 'debug') && value.debug !== undefined
    ? parseDebugHandshake(value.debug)
    : null;
  return Object.freeze({
    ...value,
    dataRoot: resolve(value.dataRoot),
    debug,
  });
}

/**
 * Shared private-channel bootstrap used by both iOS (socketpair) and Windows (Named Pipe).
 * Protocol: one JSON configuration line in, optional coalesced native-settings frames,
 * one JSON ready line (`{ protocol, baseUrl }`) out, then lifetime EOF/error drain.
 */
export async function startRuntimeOnChannel(stream, {
  platform,
  product = 'aggregate',
  expectedSessionId,
  storageDriver,
  storageInitialize,
  sourceConfigurationOwner = 'runtime',
  timeout = 0,
  signal,
  onStage = () => {},
  bufferedStages = [],
  installProcessHooks,
  onStartedCloseFailure,
  wrapError = false,
} = {}) {
  const sub = `${platform}.bootstrap`;
  let debug = null;
  let activeSessionId = expectedSessionId;
  let runtime, assembling, startingRuntime, settings, closing, stopError;
  let started = false, stopped = false, configured = false;
  let length = 0, chunks = [];
  let acceptConfig, rejectConfig, rejectStopped;
  let removeProcessHooks;

  const config = new Promise((yes, no) => { acceptConfig = yes; rejectConfig = no; });
  const interrupted = new Promise((_, no) => { rejectStopped = no; });
  interrupted.catch(() => {});

  const log = (level, event, data, extra = {}) => {
    if (!debug) return;
    debug.emit({ level, cat: 'runtime-node', sub, event, data, ...extra });
  };

  const recordStage = stage => {
    onStage(stage);
    if (debug) {
      log('info', 'bootstrap.stage', { stage });
    } else if (bufferedStages.length < 16) {
      bufferedStages.push({ stage, ts: new Date().toISOString() });
    }
  };

  let timer;
  const close = () => {
    if (closing) return closing;
    stopped = true;
    clearTimeout(timer);
    signal?.removeEventListener('abort', cancelled);
    chunks = [];
    removeProcessHooks?.();
    removeProcessHooks = undefined;
    settings?.close();
    const closeStart = debug ? performance.now() : 0;
    log('info', 'bootstrap.close.begin', activeSessionId ? { sessionId: activeSessionId } : undefined);
    closing = (async () => {
      try {
        const assembled = await assembling?.catch(() => null);
        await startingRuntime?.catch(() => {});
        if (runtime) await runtime.close();
        else if (assembled?.modules) {
          await Promise.allSettled(assembled.modules.map(module => Promise.resolve().then(() => module.close())));
        }
      } finally {
        if (debug) setStorageObserver(null);
        stream.off('data', data);
        stream.off('end', ended);
        stream.off('close', ended);
        stream.off('error', ended);
        if (!stream.closed) {
          await new Promise(done => {
            stream.once('close', done);
            stream.destroy();
          });
        }
        log('info', 'bootstrap.close.end', activeSessionId ? { sessionId: activeSessionId } : undefined,
          { durationMs: debug ? performance.now() - closeStart : undefined });
      }
    })();
    return closing;
  };

  const stop = code => {
    if (stopped) return;
    stopError = new BridgeError(code);
    log('warn', 'bootstrap.stop', { ...(activeSessionId ? { sessionId: activeSessionId } : {}), code });
    rejectConfig(stopError);
    rejectStopped(stopError);
    void close().catch(() => { if (started) onStartedCloseFailure?.(); });
  };

  const ended = () => stop('CLOSED');
  const cancelled = () => stop('CANCELLED');

  if (installProcessHooks) {
    removeProcessHooks = installProcessHooks({
      close,
      getDebug: () => debug,
      getSessionId: () => activeSessionId,
    });
  }

  const data = bytes => {
    if (stopped) return;
    if (configured) {
      if (!settings) stop('INVALID_REQUEST');
      else settings.accept(bytes);
      return;
    }
    const newline = bytes.indexOf(10);
    if (newline === -1) {
      chunks.push(bytes); length += bytes.length;
      return;
    }
    const head = bytes.subarray(0, newline);
    const remainder = bytes.subarray(newline + 1);
    chunks.push(head); length += head.length;
    configured = true;
    try {
      const value = parseChannelConfiguration(Buffer.concat(chunks, length), { expectedSessionId });
      chunks = []; length = 0;
      activeSessionId = value.sessionId;
      if (value.debug) {
        debug = createNodeDebug(value.debug);
        for (const item of bufferedStages.splice(0)) log('info', 'bootstrap.stage', item);
        log('info', 'bootstrap.configured', {
          sessionId: value.sessionId,
          dataRoot: value.dataRoot,
          nativeSettings: value.nativeSettings === true,
          debug: debug.getConfig(),
        });
      }
      if (value.nativeSettings === true) settings = createNativeSettings(stream, stop, { debug });
      if (remainder.length > 0) {
        if (!settings) throw new BridgeError('INVALID_REQUEST');
        settings.accept(remainder);
      }
      acceptConfig(value);
    } catch (error) {
      log('error', 'bootstrap.config.invalid', activeSessionId ? { sessionId: activeSessionId } : undefined, { error });
      stop('INVALID_REQUEST');
    }
  };

  if (timeout > 0) timer = setTimeout(() => stop('TIMEOUT'), timeout);
  stream.on('data', data);
  stream.once('end', ended);
  stream.once('close', ended);
  stream.on('error', ended);
  signal?.addEventListener('abort', cancelled, { once: true });

  try {
    if (signal?.aborted) stop('CANCELLED');
    if (stream.destroyed || stream.readableEnded) stop('CLOSED');
    recordStage('configuration');
    const configuration = await config;
    if (stopped) throw stopError;
    const startedAt = debug ? performance.now() : 0;
    if (debug) setStorageObserver(debug);
    configureStorage({
      driver: storageDriver,
      initialize: typeof storageInitialize === 'function' ? storageInitialize(configuration) : Boolean(storageInitialize),
      target: platform,
    });
    if (settings) {
      await Promise.race([settings.initialize(), interrupted]);
      if (stopped) throw stopError;
    }
    recordStage('assembly');
    assembling = assembleAppModules({
      dataRoot: configuration.dataRoot,
      settings,
      debug,
      platform,
      sourceConfigurationOwner,
      onStage: recordStage,
    });
    const { modules, settingsChanged } = await Promise.race([assembling, interrupted]);
    if (stopped) throw stopError;
    try {
      runtime = createAppRuntime({
        product,
        sessionId: configuration.sessionId,
        debug,
        modules,
        ...(settings && settingsChanged ? { settingsChanged } : {}),
      });
    } catch (error) {
      await Promise.allSettled(modules.map(module => Promise.resolve().then(() => module.close())));
      throw error;
    }
    recordStage('listen');
    startingRuntime = Promise.resolve(runtime.start());
    await Promise.race([startingRuntime, interrupted]);
    if (stopped) throw stopError;
    validateEndpoint(runtime.baseUrl);
    recordStage('endpoint');
    const readyLine = Buffer.from(`${JSON.stringify({ protocol: HTTP_PROTOCOL, baseUrl: runtime.baseUrl })}\n`);
    await Promise.race([
      new Promise((yes, no) => {
        stream.write(readyLine, error => error ? no(new BridgeError('CLOSED')) : yes());
      }),
      interrupted,
    ]);
    if (stopped) throw stopError;
    clearTimeout(timer);
    started = true;
    log('info', 'bootstrap.ready', { sessionId: configuration.sessionId, baseUrl: runtime.baseUrl },
      { durationMs: debug ? performance.now() - startedAt : undefined });
    return Object.freeze({
      get baseUrl() { return runtime.baseUrl; },
      get state() { return runtime.state; },
      close,
    });
  } catch (error) {
    const failure = stopped && stopError ? stopError : error;
    log('error', 'bootstrap.failed', activeSessionId ? { sessionId: activeSessionId } : undefined, { error: failure });
    await close().catch(() => {});
    const output = wrapError && !(failure instanceof BridgeError) ? new BridgeError('INVALID_RESPONSE') : failure;
    if (debug && output && typeof output === 'object' && Object.isExtensible(output)) {
      Object.defineProperty(output, DEBUG_ERROR_SYMBOL, {
        value: { debug, cause: failure },
        enumerable: false,
        configurable: true,
      });
    }
    throw output;
  }
}

export function reportChannelStartFailure({ platform, prefix, error, stage, allowedStages }) {
  if (stage !== undefined) {
    let category = 'UNKNOWN';
    try {
      const code = error?.code;
      if (typeof code === 'string' && /^[A-Z0-9_]{1,64}$/.test(code)) category = code;
    } catch { /* Unknown thrown values cannot replace the safe diagnostic. */ }
    const initialization = allowedStages?.has(stage) ? stage : 'unknown';
    process.stderr.write(`${prefix} stage=${initialization} category=${category}\n`);
  } else {
    process.stderr.write(`${prefix}\n`);
  }
  const debugInfo = error?.[DEBUG_ERROR_SYMBOL];
  if (debugInfo?.debug) {
    const cause = debugInfo.cause ?? error;
    const serialized = serializeDebugError(cause);
    debugInfo.debug.emit({
      level: 'fatal',
      cat: 'runtime-node',
      sub: `${platform}.bootstrap`,
      event: 'bootstrap.start_failed',
      data: { code: cause?.code ?? null, ...(stage !== undefined ? { stage } : {}) },
      error: cause,
    });
    process.stderr.write(`${prefix}_DEBUG ${JSON.stringify(serialized)}\n`);
  }
  process.exitCode = 1;
}
