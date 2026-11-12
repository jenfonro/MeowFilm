import { closeSync } from 'node:fs';
import { isAbsolute } from 'node:path';
import { Socket } from 'node:net';
import { BridgeError, decodeJSONValue, validateSession } from '@meowfilm/runtime-bridge';
import { duplicateBootstrapDescriptor } from '../../../packages/script-host/src/native-engine.js';
import { createAppRuntime } from '../../runtime/bootstrap.mjs';

const startupStages = new Set([
  'arguments', 'native-binding', 'bootstrap-channel', 'configuration', 'assembly',
  'music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease', 'listen', 'endpoint',
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
      Object.keys(value).length !== 2 || !Object.hasOwn(value, 'sessionId') ||
      !Object.hasOwn(value, 'dataRoot') ||
      typeof value.dataRoot !== 'string' || !isAbsolute(value.dataRoot) ||
      /[\u0000-\u001f\u007f]/.test(value.dataRoot)) throw new BridgeError('INVALID_REQUEST');
  validateSession({ sessionId: value.sessionId });
  return Object.freeze(value);
}

/** An escaped script failure drains this runtime without terminating the App. */
export function installIOSFailureBoundary(runtime, { target = process, report = reportIOSRuntimeFailure } = {}) {
  let failed = false;
  const fail = () => {
    if (failed) return;
    failed = true;
    try { report(); } catch { /* Reporting must not mask shutdown. */ }
    void Promise.resolve().then(() => runtime.close()).catch(() => {});
  };
  target.on('uncaughtException', fail);
  target.on('unhandledRejection', fail);
  return fail;
}

/** One configuration, one endpoint, then EOF. Business traffic is HTTP only. */
export async function startIOSAppRuntime({
  product, assemble, argv = process.argv.slice(2), onStage = () => {},
} = {}) {
  onStage('arguments');
  const { descriptor } = parseIOSLaunchArguments(argv);
  onStage('native-binding');
  const duplicate = duplicateBootstrapDescriptor(descriptor);
  onStage('bootstrap-channel');
  let channel;
  try {
    channel = new Socket({ fd: duplicate, readable: true, writable: true, allowHalfOpen: true });
  } catch (error) {
    closeSync(duplicate);
    throw error;
  }
  let runtime, stopping, ended = false;
  const close = () => stopping ??= (async () => {
    ended = true;
    try { await runtime?.close(); }
    finally { channel.destroy(); }
  })();
  const stop = () => { void close().catch(() => {}); };
  channel.once('end', stop);
  channel.once('close', stop);
  channel.on('error', stop);
  installIOSFailureBoundary({ close });
  try {
    onStage('configuration');
    if (typeof assemble !== 'function') throw new BridgeError('INVALID_REQUEST');
    const configuration = await readConfiguration(channel);
    if (ended) throw new BridgeError('CLOSED');
    onStage('assembly');
    runtime = createAppRuntime({ product,
      sessionId: configuration.sessionId,
      assemble: () => {
        const modules = assemble({ dataRoot: configuration.dataRoot });
        onStage('assembly');
        return modules;
      },
    });
    onStage('listen');
    await runtime.start();
    if (ended) throw new BridgeError('CLOSED');
    onStage('endpoint');
    await new Promise((resolve, reject) => {
      channel.write(`${JSON.stringify({ baseUrl: runtime.baseUrl })}\n`, error => error ? reject(error) : resolve());
    });
    return Object.freeze({
      get baseUrl() { return runtime.baseUrl; },
      get state() { return runtime.state; },
      close,
    });
  } catch (error) {
    await close().catch(() => {});
    throw error;
  }
}

function readConfiguration(channel) {
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
        // Once configured, the channel only accepts the native owner's EOF.
        channel.on('data', () => channel.destroy());
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
  process.exitCode = 1;
}
