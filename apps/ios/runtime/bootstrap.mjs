import { closeSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { Socket } from 'node:net';
import { performance } from 'node:perf_hooks';
import { BridgeError, decodeJSONValue, validateSession } from '@meowfilm/runtime-bridge';
import { duplicateBootstrapDescriptor } from '../../../packages/script-host/src/native-engine.js';
import { createNativeStorageDriver } from '@meowfilm/script-host/storage';
import { configureStorage, setStorageObserver } from '@meowfilm/storage';
import { createAppRuntime } from '../../runtime/bootstrap.mjs';
import { createNodeDebug, parseDebugHandshake, serializeDebugError } from '../../runtime/debug.mjs';
import { createNativeSettings } from '../../runtime/native-settings.mjs';

const DEBUG_ERROR_SYMBOL = Symbol.for('meowfilm.iosBootstrapDebug');
const startupStages = new Set([
  'arguments', 'native-binding', 'bootstrap-channel', 'configuration', 'assembly',
  'music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease', 'music-platform', 'listen', 'endpoint',
]);
const startupErrorCategories = new Set([
  'INVALID_REQUEST', 'INVALID_RESPONSE', 'CLOSED', 'CANCELLED', 'TIMEOUT',
  'STORAGE_ERROR', 'CORRUPT_STORAGE', 'PERMISSION_DENIED',
  'NATIVE_ARTIFACT_INVALID', 'NATIVE_ABI_MISMATCH', 'DESCRIPTOR_UNAVAILABLE',
  'ERR_DLOPEN_FAILED', 'ERR_INVALID_FD_TYPE',
  'EACCES', 'EPERM', 'ENOENT', 'ENOTDIR', 'EISDIR', 'EIO', 'ENOSPC', 'EROFS', 'EMFILE', 'ENFILE', 'EBADF',
  'EADDRINUSE', 'EADDRNOTAVAIL', 'EPIPE', 'ECONNRESET',
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

export function parseIOSConfiguration(bytes) {
  const value = decodeJSONValue(bytes);
  if (value === null || typeof value !== 'object' || Array.isArray(value) ||
      !Object.hasOwn(value, 'sessionId') ||
      !Object.hasOwn(value, 'dataRoot') ||
      typeof value.dataRoot !== 'string' || !isAbsolute(value.dataRoot) ||
      /[\u0000-\u001f\u007f]/.test(value.dataRoot)) throw new BridgeError('INVALID_REQUEST');
  validateSession({ sessionId: value.sessionId });
  if (Object.hasOwn(value, 'debug') && value.debug !== undefined) {
    const debug = parseDebugHandshake(value.debug);
    return Object.freeze({ ...value, debug });
  }
  return Object.freeze(value);
}

/** An escaped script failure drains this runtime without terminating the App. */
export function installIOSFailureBoundary(runtime, { target = process, report = reportIOSRuntimeFailure, onError } = {}) {
  let failed = false;
  const handle = kind => error => {
    try { onError?.(kind, error); } catch { /* Debug logging must not mask shutdown. */ }
    if (failed) return;
    failed = true;
    try { report(error, kind); } catch { /* Reporting must not mask shutdown. */ }
    void Promise.resolve().then(() => runtime.close()).catch(() => {});
  };
  const onUncaught = handle('uncaughtException');
  const onRejection = handle('unhandledRejection');
  target.on('uncaughtException', onUncaught);
  target.on('unhandledRejection', onRejection);
  return onUncaught;
}

/** One configuration and endpoint; optional settings frames stay on the private channel. */
export async function startIOSAppRuntime({
  product, assemble, settingsChanged, argv = process.argv.slice(2), onStage = () => {},
} = {}) {
  let debug = null;
  const bufferedStages = [];
  const recordStage = value => {
    onStage(value);
    if (debug) {
      debug.emit({ level: 'info', cat: 'runtime-node', sub: 'ios.bootstrap', event: 'bootstrap.stage', data: { stage: value } });
    } else if (bufferedStages.length < 16) {
      bufferedStages.push({ stage: value, ts: new Date().toISOString() });
    }
  };
  recordStage('arguments');
  const { descriptor } = parseIOSLaunchArguments(argv);
  recordStage('native-binding');
  const duplicate = duplicateBootstrapDescriptor(descriptor);
  recordStage('bootstrap-channel');
  let channel;
  try {
    channel = new Socket({ fd: duplicate, readable: true, writable: true, allowHalfOpen: true });
  } catch (error) {
    closeSync(duplicate);
    throw error;
  }
  let runtime, settings, stopping, stopError, ended = false;
  const close = () => stopping ??= (async () => {
    ended = true;
    const closeStart = debug ? performance.now() : 0;
    debug?.emit({ level: 'info', cat: 'runtime-node', sub: 'ios.bootstrap', event: 'bootstrap.close.begin' });
    settings?.close();
    try { await runtime?.close(); }
    finally {
      if (debug) setStorageObserver(null);
      channel.destroy();
      debug?.emit({ level: 'info', cat: 'runtime-node', sub: 'ios.bootstrap', event: 'bootstrap.close.end',
        durationMs: debug ? performance.now() - closeStart : undefined });
    }
  })();
  const stop = code => {
    stopError ??= new BridgeError(code);
    debug?.emit({ level: 'warn', cat: 'runtime-node', sub: 'ios.bootstrap', event: 'bootstrap.stop', data: { code } });
    void close().catch(() => {});
  };
  const disconnected = () => stop('CLOSED');
  channel.once('end', disconnected);
  channel.once('close', disconnected);
  channel.on('error', disconnected);
  installIOSFailureBoundary({ close }, {
    onError: (kind, error) => {
      debug?.emit({
        level: kind === 'uncaughtException' ? 'fatal' : 'error',
        cat: 'runtime-node',
        sub: 'ios.bootstrap',
        event: `process.${kind}`,
        error,
      });
    },
  });
  try {
    recordStage('configuration');
    if (typeof assemble !== 'function' || settingsChanged !== undefined && typeof settingsChanged !== 'function')
      throw new BridgeError('INVALID_REQUEST');
    const configuration = await readConfiguration(channel, value => {
      if (value.debug) {
        debug = createNodeDebug(value.debug);
        for (const item of bufferedStages.splice(0)) {
          debug.emit({ level: 'info', cat: 'runtime-node', sub: 'ios.bootstrap', event: 'bootstrap.stage', data: item });
        }
        debug.emit({
          level: 'info',
          cat: 'runtime-node',
          sub: 'ios.bootstrap',
          event: 'bootstrap.configured',
          data: {
            sessionId: value.sessionId,
            dataRoot: value.dataRoot,
            nativeSettings: value.nativeSettings === true,
            debug: debug.getConfig(),
          },
        });
      }
      if (value.nativeSettings === true) settings = createNativeSettings(channel, stop, { debug });
      channel.on('data', bytes => settings ? settings.accept(bytes) : stop('INVALID_REQUEST'));
    });
    if (ended) throw stopError;
    if (debug) setStorageObserver(debug);
    configureStorage({ driver: createNativeStorageDriver, initialize: configuration.nativeSettings !== true });
    await settings?.initialize();
    if (ended) throw stopError;
    recordStage('assembly');
    runtime = createAppRuntime({ product,
      sessionId: configuration.sessionId,
      debug,
      assemble: () => {
        const modules = assemble({ dataRoot: configuration.dataRoot, settings, debug, onStage: recordStage });
        recordStage('assembly');
        return modules;
      },
      ...(settings && settingsChanged ? { settingsChanged: block => settingsChanged(block, { settings, debug }) } : {}),
    });
    recordStage('listen');
    await runtime.start();
    if (ended) throw new BridgeError('CLOSED');
    recordStage('endpoint');
    await new Promise((resolve, reject) => {
      channel.write(`${JSON.stringify({ baseUrl: runtime.baseUrl })}\n`, error => error ? reject(error) : resolve());
    });
    debug?.emit({ level: 'info', cat: 'runtime-node', sub: 'ios.bootstrap', event: 'bootstrap.ready', data: { baseUrl: runtime.baseUrl } });
    return Object.freeze({
      get baseUrl() { return runtime.baseUrl; },
      get state() { return runtime.state; },
      close,
    });
  } catch (error) {
    const failure = ended && stopError ? stopError : error;
    if (debug) {
      debug.emit({ level: 'error', cat: 'runtime-node', sub: 'ios.bootstrap', event: 'bootstrap.failed', error: failure });
      if (failure && typeof failure === 'object' && Object.isExtensible(failure)) {
        Object.defineProperty(failure, DEBUG_ERROR_SYMBOL, {
          value: debug,
          enumerable: false,
          configurable: true,
        });
      }
    }
    await close().catch(() => {});
    throw failure;
  }
}

function readConfiguration(channel, configured) {
  return new Promise((resolve, reject) => {
    let chunks = [], length = 0, settled = false;
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      channel.off('data', data);
      channel.off('end', failed);
      channel.off('close', failed);
      channel.off('error', failed);
      chunks = [];
      if (error) reject(error); else resolve(value);
    };
    const failed = () => finish(new BridgeError('CLOSED'));
    const data = bytes => {
      chunks.push(bytes); length += bytes.length;
      const newline = bytes.indexOf(10);
      if (newline === -1) return;
      try {
        if (newline !== bytes.length - 1) throw new BridgeError('INVALID_REQUEST');
        const value = parseIOSConfiguration(Buffer.concat(chunks, length).subarray(0, -1));
        configured(value);
        finish(undefined, value);
      } catch (error) { finish(error); }
    };
    channel.on('data', data);
    channel.once('end', failed);
    channel.once('close', failed);
    channel.once('error', failed);
  });
}

function reportIOSRuntimeFailure() { process.stderr.write('IOS_RUNTIME_FAILED\n'); }

export function reportIOSRuntimeStartFailure(error, stage) {
  let category = 'UNKNOWN';
  try {
    const code = error?.code;
    if (startupErrorCategories.has(code)) category = code;
  } catch { /* An unknown thrown value cannot replace the safe diagnostic. */ }
  const initialization = startupStages.has(stage) ? stage : 'unknown';
  process.stderr.write(`IOS_RUNTIME_START_FAILED stage=${initialization} category=${category}\n`);
  const debug = error?.[DEBUG_ERROR_SYMBOL];
  if (debug) {
    const serialized = serializeDebugError(error);
    debug.emit({
      level: 'fatal',
      cat: 'runtime-node',
      sub: 'ios.bootstrap',
      event: 'bootstrap.start_failed',
      data: { stage: initialization, category },
      error,
    });
    process.stderr.write(`IOS_RUNTIME_START_FAILED_DEBUG ${JSON.stringify(serialized)}\n`);
  }
  process.exitCode = 1;
}
