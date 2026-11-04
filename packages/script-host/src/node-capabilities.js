import * as crypto from 'node:crypto';
import * as net from 'node:net';
import * as tls from 'node:tls';
import * as zlib from 'node:zlib';
import * as url from 'node:url';
import * as types from 'node:util/types';
import { StringDecoder } from 'node:string_decoder';
import { performance } from 'node:perf_hooks';
import * as webStreams from 'node:stream/web';
import * as http2 from 'node:http2';
import { Blob, File, INSPECT_MAX_BYTES, SlowBuffer, atob, btoa, constants as bufferConstants,
  isAscii, isUtf8, kMaxLength, kStringMaxLength, resolveObjectURL, transcode } from 'node:buffer';
import { TextEncoder, TextDecoder } from 'node:util';
import { createRequire } from 'node:module';
import { readFileSync, realpathSync, lstatSync } from 'node:fs';
import { dirname, extname, isAbsolute, relative, resolve, sep } from 'node:path';
import { deferred, fault, requireValue } from './errors.js';
import { PrivateVolume } from './private-volume.js';
import { nativeRoles, nativeSymbols, nativeConstructorRoles, classifyNative,
  httpRequestOptionNames, projectFields, projectHttpRequestOptions,
  pipeReadableRoles, pipeWritableRoles } from './native-roles.js';
import { dnsFacade, isLoopbackHost } from './network-boundary.js';
import { afterCloseDispatch, closeResource, guardDetachedEmitter } from './resource-close.js';

const require = createRequire(import.meta.url);
const guestPackages = new Set(['acorn', 'buffer', 'events', 'util', 'path-browserify',
  'readable-stream', 'supports-color', '@opentelemetry/api']);
const packageEntries = new Map([['buffer', 'buffer/'], ['events', 'events/'], ['util', 'util/']]);
const packageNameFor = specifier => {
  const parts = specifier.split('/');
  const packageSegments = specifier.startsWith('@') ? 2 : 1;
  requireValue(parts.length >= packageSegments &&
    parts.every(part => part.length > 0 && part !== '.' && part !== '..') &&
    !/[\u0000-\u001f\u007f]/.test(specifier), 'UNSUPPORTED_DEPENDENCY');
  return parts.slice(0, packageSegments).join('/');
};
const nodeCodes = new Set(['ENOENT', 'EEXIST', 'EBADF', 'EINVAL', 'EISDIR', 'ENOTDIR',
  'ENOTEMPTY', 'EPERM', 'EACCES', 'EIO', 'ENOSPC', 'ECONNRESET', 'ECONNREFUSED',
  'ENOTFOUND', 'EAI_AGAIN', 'ETIMEDOUT', 'EADDRINUSE', 'ERR_SERVER_ALREADY_LISTEN',
  'ERR_SERVER_NOT_RUNNING', 'ERR_STREAM_DESTROYED', 'ERR_STREAM_PREMATURE_CLOSE', 'ERR_INVALID_ARG_TYPE',
  'ERR_INVALID_ARG_VALUE', 'ERR_INVALID_URL', 'ERR_INVALID_PROTOCOL', 'ERR_OUT_OF_RANGE', 'ERR_CANCELED',
  'ERR_HTTP_HEADERS_SENT', 'ERR_HTTP_INVALID_HEADER_VALUE', 'ERR_INVALID_HTTP_TOKEN',
  'ERR_HTTP_INVALID_STATUS_CODE', 'ERR_INVALID_CHAR', 'ERR_UNESCAPED_CHARACTERS',
  'ERR_CRYPTO_INVALID_KEYLEN', 'ERR_CRYPTO_ECDH_INVALID_PUBLIC_KEY', 'Z_DATA_ERROR', 'Z_BUF_ERROR']);
const urlFormatFields = [...nativeRoles.legacyUrl.fields];
const urlFormatOptions = ['auth', 'fragment', 'search', 'unicode'];
const protocolCodes = new Set(['PERMISSION_DENIED', 'UNSUPPORTED_DEPENDENCY', 'STALE_SESSION',
  'RESOURCE_LIMIT', 'NETWORK_ERROR', 'UNMAPPED_LOCAL_URL', 'CAPABILITY_UNAVAILABLE',
  'SERVICE_EXISTS', 'SERVICE_CLOSED', 'TIMEOUT', 'CANCELLED']);
const guestErrorCodes = new Set([...nodeCodes, ...protocolCodes]);
const constructors = new Set(Object.keys(nativeConstructorRoles));
const returns = { createHash: 'hash', createHmac: 'hash', createCipheriv: 'cipher',
  createDecipheriv: 'cipher', createSign: 'sign', createVerify: 'sign', connectHttp2: 'http2Session' };
const destroyOnRelease = new Set(['stream', 'hash', 'cipher', 'sign', 'http2Stream']);
const resourceRoles = new Set(['server', 'request', 'response', 'incoming', 'socket',
  'tlsSocket', 'stream', 'http2Session', 'http2Stream', 'fileHandle', 'agent']);
const networkOwner = Symbol('node-network-owner');
const ignoreError = () => {};
const select = (module, names) => Object.fromEntries(names.filter(name => name in module)
  .map(name => [name, module[name]]));
const inside = (root, file) => {
  const delta = relative(root, file);
  return delta !== '..' && !delta.startsWith('..' + sep) && !isAbsolute(delta);
};
const ignoredCleanupError = error =>
  ['ERR_INVALID_STATE', 'ERR_STREAM_DESTROYED', 'ERR_STREAM_PREMATURE_CLOSE',
    'NETWORK_ERROR', 'ERR_CANCELED', 'CANCELLED']
    .includes(error?.code) || error?.name === 'AbortError';

function cleanupWebHandle(role, value) {
  try {
    if (role === 'readableWeb')
      return value.locked ? Promise.resolve() : cleanupWebHandle('reader', value.getReader());
    if (role === 'writableWeb')
      return value.locked ? Promise.resolve() : cleanupWebHandle('writer', value.getWriter());
    if (['transformWeb', 'compressionWeb', 'decompressionWeb',
      'textEncoderStream', 'textDecoderStream'].includes(role))
      return Promise.allSettled([cleanupWebHandle('readableWeb', value.readable),
        cleanupWebHandle('writableWeb', value.writable)]).then(results => {
        const failed = results.find(result => result.status === 'rejected');
        if (failed) throw failed.reason;
      });
  } catch (error) { return ignoredCleanupError(error) ? Promise.resolve() : Promise.reject(error); }
  const reason = fault('CANCELLED');
  const tasks = [];
  let failure;
  const valueRef = new WeakRef(value);
  // Cancelling an already errored stream replays its stored operation failure.
  // Observe that exact terminal error before releasing the lock; a new failure
  // from the underlying cancel/abort implementation must still fail cleanup.
  const terminal = { error: null };
  if (['reader', 'writer'].includes(role) && value.closed?.then)
    Promise.resolve(value.closed).then(() => {}, error => { terminal.error = error; });
  const failed = error => {
    if (error !== terminal.error && !ignoredCleanupError(error)) failure ??= error;
  };
  const releaseLock = () => {
    const target = valueRef.deref();
    if (!target) return true;
    try {
      target.releaseLock?.();
      return true;
    } catch (error) {
      failed(error);
      return false;
    }
  };
  const attempt = (operation, release = false) => {
    let task;
    try { task = operation(); } catch (error) {
      failed(error);
      if (release) releaseLock();
      return;
    }
    tasks.push(Promise.resolve(task).then(
      () => { if (release) releaseLock(); },
      error => { failed(error); if (release) releaseLock(); },
    ));
  };
  if (role === 'reader') {
    attempt(() => value.cancel(reason), true);
    releaseLock();
  } else if (role === 'writer') {
    attempt(() => value.abort(reason), true);
    releaseLock();
  } else if (role === 'iterator' && typeof value.return === 'function') {
    attempt(() => value.return());
  }
  return Promise.all(tasks).then(() => {
    if (failure) throw failure;
  });
}
/** The bridge names finite capabilities. Native methods are registered with a
 * role and receiver, never selected by traversing prototypes or Function.
 * The guest can reflect on its own wrappers, not on native internals. */
export class NodeCapabilities {
  constructor(session, services, dataRoot) {
    this.session = session;
    this.services = services;
    this.volume = new PrivateVolume(dataRoot, session.installationId,
      scope => session.storageWritten(scope), session.revision.source,
      () => session.callbackFailureScope ?? session.scope(),
      session.limits.cleanupTimeoutMs, () => this.ownerScope());
    this.handles = new Map();
    this.reverse = new WeakMap();
    this.tags = this.volume.roles;
    this.callbacks = new Map();
    this.callbackFinalizer = new FinalizationRegistry(receipt => {
      if (this.closing || !this.callbacks || this.callbacks.get(receipt.id) !== receipt) return;
      this.releaseCallbacks([receipt.id]);
    });
    this.callbackReverse = new WeakMap();
    this.callbackReturns = new Map();
    this.requestContexts = new WeakMap();
    // Guest contexts created by a request callback keep that request's
    // metadata for later continuation turns. Resource ownership still uses
    // the session's script scope.
    this.contextMetadata = new Map();
    this.sources = new Map();
    this.networkResources = new Set();
    this.networkCleanup = new Map();
    this.cleanupTracker = { tasks: new Set(), failure: null, active: true };
    this.cleanupTasks = this.cleanupTracker.tasks;
    this.scopeLeases = new WeakMap();
    this.contextLeases = new Map();
    this.promiseLeases = new Map();
    this.pendingGuestRelease = {
      handles: new Set(), callbacks: new Set(), contexts: new Set(), promises: new Set(),
      forgetContexts: new Set(), collectHandles: new Set(),
    };
    this.releaseScheduled = false;
    this.started = performance.now();
    this.fs = this.volume.facade();
    this.builtins = {
      fs: this.fs, 'fs/promises': this.fs.promises,
      crypto: select(crypto, ['constants', 'webcrypto', 'createHash', 'createHmac', 'createCipheriv',
        'createDecipheriv', 'createSign', 'createVerify', 'createPublicKey', 'createPrivateKey',
        'createSecretKey', 'publicEncrypt', 'publicDecrypt', 'privateEncrypt', 'privateDecrypt',
        'getRandomValues', 'randomBytes', 'randomFillSync', 'randomFill', 'randomInt', 'randomUUID',
        'timingSafeEqual', 'pbkdf2', 'pbkdf2Sync', 'scrypt', 'scryptSync', 'hkdf', 'hkdfSync',
        'getHashes', 'getCiphers', 'getCurves', 'generateKeyPair', 'generateKeyPairSync',
        'sign', 'verify', 'diffieHellman', 'createECDH', 'X509Certificate']),
      zlib: select(zlib, ['constants', 'gzip', 'gzipSync', 'gunzip', 'gunzipSync',
        'deflate', 'deflateSync', 'inflate', 'inflateSync', 'deflateRaw', 'deflateRawSync',
        'inflateRaw', 'inflateRawSync', 'unzip', 'unzipSync', 'brotliCompress',
        'brotliCompressSync', 'brotliDecompress', 'brotliDecompressSync',
        'createGzip', 'createGunzip', 'createDeflate', 'createInflate', 'createDeflateRaw',
        'createInflateRaw', 'createUnzip', 'createBrotliCompress', 'createBrotliDecompress']),
      url: { ...select(url, ['URL', 'URLSearchParams', 'parse', 'format', 'resolve',
        'domainToASCII', 'domainToUnicode', 'urlToHttpOptions']),
        fileURLToPath: value => {
          const input = new URL(value);
          requireValue(input.protocol === 'file:' && !input.host, 'PERMISSION_DENIED');
          this.volume.path(decodeURIComponent(input.pathname), { missing: true });
          return decodeURIComponent(input.pathname);
        },
        pathToFileURL: value => {
          this.volume.path(value, { missing: true });
          return new URL('file://' + (value.startsWith('/') ? value : '/' + value));
        } },
      'util/types': types, string_decoder: { StringDecoder },
      encoding: { TextEncoder, TextDecoder }, bufferExtra: {
        Blob, File, INSPECT_MAX_BYTES, SlowBuffer, atob, btoa, constants: bufferConstants,
        isAscii, isUtf8, kMaxLength, kStringMaxLength, resolveObjectURL, transcode,
      },
      'stream/web': select(webStreams, ['ReadableStream', 'WritableStream', 'TransformStream',
        'CompressionStream', 'DecompressionStream', 'TextEncoderStream', 'TextDecoderStream',
        'ByteLengthQueuingStrategy', 'CountQueuingStrategy']),
      http2: { constants: http2.constants,
        connect: (input, options = {}, callback) => {
          requireValue(this.session.network === undefined, 'CAPABILITY_UNAVAILABLE');
          const target = this.services.outboundURL(input);
          const local = this.services.mapLocal(String(input)) !== null;
          requireValue(!options.createConnection && !options.socketPath && !options.path, 'PERMISSION_DENIED');
          const client = http2.connect(target, {
            ...this.services.tlsOptions(options, { client: true, local }),
          }, callback);
          this.tags.set(client, 'http2Session');
          this.ownNetwork(client);
          return client;
        },
        getDefaultSettings: http2.getDefaultSettings, getPackedSettings: http2.getPackedSettings,
        getUnpackedSettings: http2.getUnpackedSettings },
      dns: dnsFacade({ adapter: typeof this.session.network === 'function' }),
      http: services.http, https: services.https,
      net: this.netFacade(net), tls: this.netFacade(tls),
      os: {
        // These describe the virtual POSIX volume, not the host computer.
        // Memory reports the per-script limit and current guest heap use.
        platform: () => 'linux', type: () => 'ScriptHost', release: () => '1',
        arch: () => 'wasm32', endianness: () => 'LE', homedir: () => '/',
        tmpdir: () => '/tmp', hostname: () => 'script', availableParallelism: () => 1,
        machine: () => 'wasm32', uptime: () => (performance.now() - this.started) / 1000,
        totalmem: () => this.session.limits.maxMemoryBytes,
        freemem: () => Math.max(0, this.session.limits.maxMemoryBytes -
          (this.session.engine?.memoryUsage().heapUsed ?? 0)),
        loadavg: () => [0, 0, 0],
        cpus: () => [{ model: 'ScriptHost Virtual CPU', speed: 0,
          times: { user: 0, nice: 0, sys: 0, idle: 0, irq: 0 } }],
        networkInterfaces: () => ({ lo: [
          { address: '127.0.0.1', netmask: '255.0.0.0', family: 'IPv4',
            mac: '00:00:00:00:00:00', internal: true, cidr: '127.0.0.1/8' },
          { address: '::1', netmask: 'ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff',
            family: 'IPv6', scopeid: 0, mac: '00:00:00:00:00:00', internal: true, cidr: '::1/128' },
        ] }),
        userInfo: () => ({ uid: 0, gid: 0, username: 'script', homedir: '/', shell: null }),
        devNull: '/dev/null', EOL: '\n', constants: {},
      },
      perf_hooks: { performance: { now: () => performance.now() - this.started,
        timeOrigin: Date.now() - (performance.now() - this.started) } },
      tty: { isatty: () => false },
      worker_threads: { isMainThread: true, threadId: 0, workerData: null, parentPort: null },
    };
    this.builtins['dns/promises'] = this.builtins.dns.promises;
    this.builtins.crypto.webcrypto = { subtle: {
      __proto__: null,
      ...Object.fromEntries([
      'encrypt', 'decrypt', 'sign', 'verify', 'digest', 'generateKey', 'deriveKey', 'deriveBits',
      'importKey', 'exportKey', 'wrapKey', 'unwrapKey',
      ].map(key => [key, crypto.webcrypto.subtle[key].bind(crypto.webcrypto.subtle)])),
    },
    getRandomValues: value => crypto.webcrypto.getRandomValues(value), randomUUID: crypto.randomUUID };
    this.builtins.constants = { ...this.fs.constants, ...crypto.constants, ...zlib.constants };
  }

  ownerScope() {
    // Module evaluation runs inside the session root and owns its values for
    // the session lifetime. Request and background-callback work belongs to
    // the script resources, so it keeps running after its request ends.
    const scope = this.session.scope();
    return scope === this.session.root
      ? scope : this.session.resourceScope?.() ?? scope;
  }

  lease(scope = this.ownerScope()) {
    scope.assertOpen();
    let lease = this.scopeLeases.get(scope);
    if (lease) return lease;
    lease = { scope, handles: new Set(), callbacks: new Set(),
      contexts: new Set(), promises: new Set() };
    this.scopeLeases.set(scope, lease);
    try { scope.add(() => this.releaseLease(scope, lease)); }
    catch (error) {
      this.scopeLeases.delete(scope);
      throw error;
    }
    return lease;
  }

  retain(receipt, scope, bucket) {
    const lease = this.lease(scope);
    if (!receipt.leases.has(lease)) {
      receipt.leases.add(lease);
      lease[bucket].add(receipt.id);
    }
    return receipt;
  }

  retainHandle(receipt, scope = this.ownerScope()) {
    scope = receipt.boundScope ?? scope;
    if (scope === this.session.root) receipt.persistent = true;
    const owner = receipt.persistent ? this.session.root
      : receipt.boundScope ? receipt.boundScope
        : scope === this.session.root ? scope
        : this.session.resourceScope?.() ?? scope;
    return this.retain(receipt, owner, 'handles');
  }

  retainCallback(receipt, scope = this.ownerScope()) {
    return this.retain(receipt, scope, 'callbacks');
  }

  releaseCallbacks(ids) {
    const released = [];
    for (const id of ids) {
      const receipt = this.callbacks.get(id);
      if (!receipt) continue;
      this.callbackFinalizer?.unregister(receipt);
      for (const lease of receipt.leases) lease.callbacks.delete(id);
      receipt.leases.clear();
      this.callbacks.delete(id);
      const callback = receipt.ref.deref();
      if (callback) this.callbackReverse.delete(callback);
      released.push(id);
    }
    if (released.length) this.queueGuestRelease({ callbacks: released });
  }

  disposeHandle(receipt) {
    this.releaseMethodReceipts(receipt);
    try { receipt.naturalRelease?.(); } catch (error) { this.cleanupTracker.failure ??= error; }
    receipt.naturalRelease = null;
    if (receipt.value && ['object', 'function'].includes(typeof receipt.value))
      this.reverse.delete(receipt.value);
    try {
      const handled = this.volume.release(receipt.value) || this.releaseNativeHandle(receipt);
      if (!handled && destroyOnRelease.has(receipt.role) &&
        typeof receipt.value?.destroy === 'function' && !receipt.value.destroyed) {
        receipt.value.destroy();
      }
      return null;
    } catch (error) {
      this.cleanupTracker.failure ??= error;
      return error;
    }
  }

  releaseHandle(receipt) {
    if (!receipt || this.handles.get(receipt.id) !== receipt) return false;
    this.releaseMethodReceipts(receipt);
    for (const lease of receipt.leases) lease.handles.delete(receipt.id);
    receipt.leases.clear();
    this.handles.delete(receipt.id);
    this.disposeHandle(receipt);
    this.queueGuestRelease({ handles: [receipt.id] });
    return true;
  }

  releaseMethodReceipts(receipt) {
    if (!receipt.methods) return;
    const methods = [...receipt.methods.values()];
    receipt.methods.clear();
    for (const method of methods) {
      const child = this.handles.get(method.id);
      if (child) this.releaseHandle(child);
    }
  }

  releaseGuestHandles(ids) {
    for (const id of new Set(ids)) {
      const receipt = this.handles.get(id);
      if (!receipt) continue;
      if (receipt.terminal) {
        this.releaseHandle(receipt);
        continue;
      }
      if (receipt.persistent || resourceRoles.has(receipt.role)) continue;
      this.releaseMethodReceipts(receipt);
      for (const lease of receipt.leases) lease.handles.delete(id);
      receipt.leases.clear();
      this.handles.delete(id);
      if (receipt.value && ['object', 'function'].includes(typeof receipt.value))
        this.reverse.delete(receipt.value);
    }
  }

  retireClosedHandle(receipt) {
    if (this.handles.get(receipt.id) !== receipt) return;
    // Closing the native transport does not revoke a script's object. Keep
    // terminal stream state and methods until its guest proxy becomes
    // unreachable; actual I/O ownership is released by services/volume close.
    // Inbound transports remain subject to their explicit request scope.
    if (receipt.boundScope || receipt.role === 'server' || !resourceRoles.has(receipt.role)) {
      this.releaseHandle(receipt);
      return;
    }
    receipt.terminal = true;
    try { receipt.naturalRelease?.(); } catch (error) { this.cleanupTracker.failure ??= error; }
    receipt.naturalRelease = null;
    this.queueGuestRelease({ collectHandles: [receipt.id] });
  }

  bindNaturalRelease(receipt) {
    if (receipt.naturalRelease) return;
    const value = receipt.value;
    if (!value || typeof value !== 'object') return;
    const closeRoles = new Set(['server', 'request', 'response', 'incoming', 'socket', 'tlsSocket',
      'stream', 'http2Session', 'http2Stream', 'hash', 'cipher', 'sign']);
    // A server request stays available to the guest until its response ends;
    // its close event only marks the request body as consumed. Client
    // responses (which carry `req`) are complete when they close.
    const serverRequest = receipt.role === 'incoming' && !value.req;
    let detach = null;
    if (!serverRequest && closeRoles.has(receipt.role) && typeof value.emit === 'function') {
      // Retire after the close dispatch, not while an emitter is still
      // delivering its final event; a synchronous destroy can truncate
      // pending stream delivery to the other side of a pipe.
      try { detach = afterCloseDispatch(value, () => queueMicrotask(() =>
        this.retireClosedHandle(receipt))); }
      catch (error) { this.cleanupTracker.failure ??= error; }
    }
    // A guest ServerResponse completes at finish and does not auto-destroy, so
    // no close event follows to retire its handle.
    if (receipt.role === 'response' && typeof value.once === 'function') {
      const finished = () => queueMicrotask(() => {
        const request = value.req && this.reverse.get(value.req);
        const requestReceipt = request && this.handles.get(request);
        this.releaseHandle(receipt);
        if (requestReceipt) this.releaseHandle(requestReceipt);
      });
      try {
        value.once('finish', finished);
        const closeDetach = detach;
        detach = () => {
          try { value.off('finish', finished); } catch { /* The response is gone. */ }
          closeDetach?.();
        };
      } catch (error) { this.cleanupTracker.failure ??= error; }
    }
    if (detach) receipt.naturalRelease = detach;
    if (!serverRequest && closeRoles.has(receipt.role) && value.closed === true)
      queueMicrotask(() => this.retireClosedHandle(receipt));
  }

  naturalReleaseFromInvocation(receipt, key) {
    const released = receipt.role === 'fileHandle' && key === 'close' ||
      receipt.role === 'agent' && key === 'destroy';
    if (released) queueMicrotask(() => this.releaseHandle(receipt));
  }

  observeContext(id, scope) {
    if (!Number.isSafeInteger(id) || id <= 0 || !scope) return;
    this.contextMetadata.set(id, scope);
    let ids = this.requestContexts.get(scope);
    if (!ids) { ids = new Set(); this.requestContexts.set(scope, ids); }
    ids.add(id);
  }

  forgetContext(id) {
    this.contextMetadata.delete(id);
    const receipt = this.contextLeases.get(id);
    if (!receipt) return;
    for (const lease of receipt.leases) lease.contexts.delete(id);
    receipt.leases.clear();
    this.contextLeases.delete(id);
    this.queueGuestRelease({ forgetContexts: [id] });
  }

  forgetRequestContexts(scope) {
    const ids = this.requestContexts.get(scope);
    if (!ids) return;
    this.requestContexts.delete(scope);
    for (const id of ids) this.forgetContext(id);
  }

  activeContextScope(id) {
    const receipt = this.contextLeases.get(id);
    if (!receipt) return null;
    return [...receipt.leases].map(lease => lease.scope)
      .find(scope => !scope.signal.aborted) ?? null;
  }

  retainContext(id, scope = this.ownerScope()) {
    if (!Number.isSafeInteger(id) || id <= 0) return;
    if (scope.signal.aborted) {
      if (!this.activeContextScope(id)) this.queueGuestRelease({ contexts: [id] });
      return;
    }
    let receipt = this.contextLeases.get(id);
    if (!receipt) {
      receipt = { id, leases: new Set() };
      this.contextLeases.set(id, receipt);
    }
    this.retain(receipt, scope, 'contexts');
  }

  scopeForContext(id, origin = 0) {
    if (!Number.isSafeInteger(id) || id <= 0) {
      // Native async/await continuations do not invoke the patched
      // Promise.prototype.then. They are pumped inside the originating host
      // execution scope. Module evaluation is pumped inside the session root
      // and owns its values; guest callbacks are pumped inside script
      // resources and keep running after their creating request ends.
      const scope = this.ownerScope();
      scope.assertOpen();
      return scope;
    }
    const active = this.activeContextScope(id) ||
      (Number.isSafeInteger(origin) && origin > 0 ? this.activeContextScope(origin) : null);
    if (active) {
      this.retainContext(id, active);
      return active;
    }
    const retired = value => Number.isSafeInteger(value) && value > 0 &&
      (this.contextLeases.has(value) || this.pendingGuestRelease.contexts.has(value));
    requireValue(!retired(id) && !retired(origin), 'CANCELLED');
    const scope = this.ownerScope();
    scope.assertOpen();
    return scope;
  }

  metadataScope(id, fallback) {
    const scope = this.contextMetadata.get(id);
    return scope && !scope.signal.aborted ? scope : fallback;
  }

  retainPromise(id, scope = this.ownerScope()) {
    const receipt = { id, leases: new Set() };
    this.promiseLeases.set(id, receipt);
    try { this.retain(receipt, scope, 'promises'); }
    catch (error) {
      for (const lease of receipt.leases) lease.promises.delete(id);
      receipt.leases.clear();
      this.promiseLeases.delete(id);
      throw error;
    }
  }

  completePromise(id) {
    const receipt = this.promiseLeases.get(id);
    if (!receipt) return;
    for (const lease of receipt.leases) lease.promises.delete(id);
    this.promiseLeases.delete(id);
  }

  abandonPromise(id) {
    if (!this.promiseLeases.has(id)) return;
    this.completePromise(id);
    this.queueGuestRelease({ promises: [id] });
  }

  releaseLease(scope, lease) {
    if (this.scopeLeases.get(scope) !== lease) return;
    this.scopeLeases.delete(scope);
    const released = { handles: [], callbacks: [], contexts: [], promises: [] };
    for (const id of lease.handles) {
      const receipt = this.handles.get(id);
      if (!receipt) continue;
      receipt.leases.delete(lease);
      if (receipt.leases.size) continue;
      this.handles.delete(id);
      this.disposeHandle(receipt);
      released.handles.push(id);
    }
    for (const id of lease.callbacks) {
      const receipt = this.callbacks.get(id);
      if (!receipt) continue;
      receipt.leases.delete(lease);
      if (receipt.leases.size) continue;
      this.callbacks.delete(id);
      this.callbackFinalizer?.unregister(receipt);
      const callback = receipt.ref.deref();
      if (callback) this.callbackReverse.delete(callback);
      released.callbacks.push(id);
    }
    for (const id of lease.contexts) {
      const receipt = this.contextLeases.get(id);
      if (!receipt) continue;
      receipt.leases.delete(lease);
      if (receipt.leases.size) continue;
      this.contextLeases.delete(id);
      this.contextMetadata.delete(id);
      released.contexts.push(id);
    }
    for (const id of lease.promises) {
      const receipt = this.promiseLeases.get(id);
      if (!receipt) continue;
      receipt.leases.delete(lease);
      if (receipt.leases.size) continue;
      this.promiseLeases.delete(id);
      released.promises.push(id);
    }
    this.queueGuestRelease(released);
  }

  trackCleanup(task) {
    const tracked = Promise.resolve(task);
    const state = this.cleanupTracker;
    state.tasks.add(tracked);
    const settled = error => {
      state.tasks.delete(tracked);
      if (error && state.active) state.failure ??= error;
    };
    tracked.then(() => settled(), settled);
    this.session.trackAdapter(tracked);
  }

  releaseNativeHandle(receipt) {
    if (!['readableWeb', 'writableWeb', 'transformWeb', 'compressionWeb',
      'decompressionWeb', 'textEncoderStream', 'textDecoderStream',
      'reader', 'writer', 'iterator'].includes(receipt.role)) return false;
    this.trackCleanup(cleanupWebHandle(receipt.role, receipt.value));
    return true;
  }

  queueGuestRelease(released) {
    let added = false;
    for (const key of Object.keys(this.pendingGuestRelease)) {
      for (const id of released[key] ?? []) {
        this.pendingGuestRelease[key].add(id);
        added = true;
      }
    }
    if (!added) return;
    this.scheduleGuestRelease();
  }

  scheduleGuestRelease() {
    if (this.releaseScheduled) return;
    this.releaseScheduled = true;
    queueMicrotask(() => this.flushGuestRelease());
  }

  takeReleased(values, count = 2048) {
    // This only frames lifecycle notifications across the JSON control
    // boundary. Remaining receipts are scheduled immediately; nothing is
    // rejected or retained as a resource quota.
    const result = [];
    for (const value of values) {
      values.delete(value);
      result.push(value);
      if (result.length === count) break;
    }
    return result;
  }

  flushGuestRelease() {
    this.releaseScheduled = false;
    const session = this.session;
    const engine = session?.engine;
    if (!session || !session.live || session.root.signal.aborted || !engine ||
      engine.closed || engine.closing) {
      for (const values of Object.values(this.pendingGuestRelease)) values.clear();
      return;
    }
    const packet = Object.fromEntries(Object.entries(this.pendingGuestRelease)
      .map(([key, values]) => [key, this.takeReleased(values)]));
    if (!Object.values(packet).some(values => values.length)) return;
    try {
      session.contextScope.run(session.root, () =>
        engine.callUnpumped('nativeReleased', [JSON.stringify(packet)]));
    } catch (error) {
      for (const values of Object.values(this.pendingGuestRelease)) values.clear();
      session.callbackFailed(error, session.root);
      return;
    }
    if (Object.values(this.pendingGuestRelease).some(values => values.size))
      this.scheduleGuestRelease();
  }

  ownNetwork(socket) {
    const resources = this.networkResources;
    const cleanups = this.networkCleanup;
    const cleanupState = this.cleanupTracker;
    resources.add(socket);
    const scope = this.ownerScope();
    const state = { active: true, detach: null };
    const closed = () => {
      if (!state.active) return;
      state.active = false;
      try { socket.off?.('close', closed); } catch (error) { cleanupState.failure ??= error; }
      try { socket.off?.('error', ignoreError); } catch (error) { cleanupState.failure ??= error; }
      const detach = state.detach;
      state.detach = null;
      try { detach?.(); } catch (error) { cleanupState.failure ??= error; }
      resources.delete(socket);
      cleanups.delete(socket);
      try { delete socket[networkOwner]; } catch (error) { cleanupState.failure ??= error; }
    };
    cleanups.set(socket, closed);
    try {
      socket.on?.('error', ignoreError);
      socket.once?.('close', closed);
      Object.defineProperty(socket, networkOwner, {
        configurable: true, value: { resources, cleanups },
      });
      state.detach = scope.add(() => {
        try { if (!socket.destroyed) socket.destroy(); } catch {}
      });
      scope.assertOpen?.();
    } catch (error) {
      try { if (!socket.destroyed) socket.destroy(); } catch {}
      try { closed(); } catch {}
      let closedNow = false;
      try { closedNow = Boolean(socket.closed); } catch {}
      if (!closedNow) guardDetachedEmitter(socket);
      throw error;
    }
    try { if (socket.closed) closed(); } catch {}
    return socket;
  }

  netFacade(native) {
    const connect = (...args) => {
      let options;
      if (args[0] && typeof args[0] === 'object') options = { ...args.shift() };
      else {
        options = { port: args.shift(), host: typeof args[0] === 'string' ? args.shift() : 'localhost' };
        if (native === tls && args[0] && typeof args[0] === 'object')
          Object.assign(options, args.shift());
      }
      requireValue(!options.path && options.fd === undefined && !options.socket &&
        !options.createConnection && !options.lookup &&
        Number.isInteger(Number(options.port)), 'PERMISSION_DENIED');
      const mapped = this.services.connection(options, native === tls);
      requireValue(mapped.meowLocal || isLoopbackHost(mapped.host) ||
        this.session.network === undefined, 'CAPABILITY_UNAVAILABLE');
      const clean = {
        ...(native === tls
          ? this.services.tlsOptions(options, { client: true, local: mapped.meowLocal })
          : {}),
        host: mapped.host, port: mapped.port,
        ...(mapped.lookup ? { lookup: mapped.lookup } : {}) };
      const socket = native === tls ? tls.connect(clean, ...args) : net.connect(clean, ...args);
      this.ownNetwork(socket);
      if (mapped.meowLocal) this.services.trustSocket(socket);
      return socket;
    };
    const createServer = (...args) => {
      requireValue(native === net, 'UNSUPPORTED_DEPENDENCY');
      const server = this.services.probe(...args);
      this.tags.set(server, 'server');
      return server;
    };
    const Server = function(...args) { return createServer(...args); };
    return { connect, createConnection: connect, isIP: net.isIP,
      isIPv4: net.isIPv4, isIPv6: net.isIPv6,
      ...(native === net ? { Server, createServer } : {}),
      ...(native === tls ? { rootCertificates: tls.rootCertificates, DEFAULT_MIN_VERSION: 'TLSv1.2' } : {}) };
  }

  source(specifier, parent = null) {
    requireValue(typeof specifier === 'string' && (typeof parent === 'string' || parent === null));
    const base = parent && this.sources.get(parent);
    if (parent !== null) requireValue(base, 'UNSUPPORTED_DEPENDENCY');
    const bare = !specifier.startsWith('.');
    requireValue(!specifier.startsWith('/') && !specifier.includes('\\'), 'UNSUPPORTED_DEPENDENCY');
    const packageName = bare ? packageNameFor(specifier) : null;
    if (!base) requireValue(guestPackages.has(specifier) ||
      specifier === 'readable-stream/lib/stream.js', 'UNSUPPORTED_DEPENDENCY');
    else if (bare) {
      requireValue(base.packageName === packageName || Object.hasOwn(base.dependencies, packageName) ||
        guestPackages.has(specifier), 'UNSUPPORTED_DEPENDENCY');
    }
    let file;
    try {
      file = require.resolve(packageEntries.get(specifier) ?? specifier,
        base ? { paths: [dirname(base.file)] } : undefined);
    }
    catch { throw fault('UNSUPPORTED_DEPENDENCY'); }
    requireValue(isAbsolute(file), 'UNSUPPORTED_DEPENDENCY');
    requireValue(!lstatSync(file).isSymbolicLink(), 'PERMISSION_DENIED');
    file = realpathSync(file);
    let packageRoot = dirname(file), manifest;
    for (;;) {
      try { manifest = JSON.parse(readFileSync(packageRoot + '/package.json', 'utf8')); break; }
      catch {
        const next = dirname(packageRoot);
        requireValue(next !== packageRoot, 'UNSUPPORTED_DEPENDENCY');
        packageRoot = next;
      }
    }
    if (bare) requireValue(manifest?.name === packageName, 'UNSUPPORTED_DEPENDENCY');
    // Node 24 can select a package's `module-sync` ESM condition for
    // createRequire.resolve(). The guest loader is deliberately CommonJS, so
    // use the same reviewed package's declared CommonJS main when available.
    if (extname(file) === '.mjs') {
      requireValue(typeof manifest.main === 'string' && manifest.main.length > 0,
        'UNSUPPORTED_DEPENDENCY');
      const fallback = resolve(packageRoot, manifest.main);
      requireValue(inside(packageRoot, fallback) && !lstatSync(fallback).isSymbolicLink(),
        'PERMISSION_DENIED');
      file = realpathSync(fallback);
    }
    requireValue(['.js', '.cjs', '.json'].includes(extname(file)), 'UNSUPPORTED_DEPENDENCY');
    requireValue(inside(packageRoot, file), 'PERMISSION_DENIED');
    if (!bare && base) requireValue(inside(base.packageRoot, file), 'PERMISSION_DENIED');
    // Only a module already in the reviewed dependency graph can load its JS
    // dependencies. A source cannot nominate a native loader or arbitrary package.
    const id = '/sdk/' + manifest.name + '/' + relative(packageRoot, file).split(sep).join('/');
    this.sources.set(id, { file, packageRoot, packageName: manifest.name,
      dependencies: { ...manifest.dependencies } });
    return { id, source: readFileSync(file, 'utf8'), json: extname(file) === '.json' };
  }

  callable(fn, receiver, name, construct = false, hint, scope = this.ownerScope()) {
    const id = crypto.randomUUID();
    const argumentFields = ['request', 'get'].includes(name) &&
      [this.builtins.http, this.builtins.https].includes(receiver)
      ? { kind: 'http', fields: httpRequestOptionNames }
      : name === 'format' && receiver === this.builtins.url
        ? { kind: 'urlFormat', fields: urlFormatFields,
          options: urlFormatOptions } : null;
    const receipt = { id, fn, receiver, name, construct, hint, role: 'callable',
      argumentFields, persistent: scope === this.session.root, leases: new Set(),
      boundScope: scope?.ownsTransport ? scope : null };
    this.handles.set(id, receipt);
    try { this.retainHandle(receipt, scope); }
    catch (error) {
      this.handles.delete(id);
      throw error;
    }
    return { $: 'handle', id, callable: true, name, ...(argumentFields ? { argumentFields } : {}) };
  }

  moduleValue(value, seen = new Set(), scope = this.session.root, frames = []) {
    if (typeof value === 'function')
      return this.callable(value, undefined, value.name, constructors.has(value.name), undefined, scope);
    if (!value || typeof value !== 'object' || Array.isArray(value))
      return this.encode(value, new Set(), undefined, scope, frames);
    if (classifyNative(value, this.tags))
      return this.encode(value, new Set(), undefined, scope, frames);
    requireValue(!seen.has(value), 'PERMISSION_DENIED');
    seen.add(value);
    const fields = Object.fromEntries(Object.entries(value).map(([key, item]) => [key,
      typeof item === 'function'
        ? this.callable(item, value, key, constructors.has(key), returns[key], scope)
        : this.moduleValue(item, seen, scope, frames)]));
    seen.delete(value);
    return { $: 'object', value: fields };
  }

  encode(value, seen = new Set(), hint, scope = this.ownerScope(), frames = []) {
    if (value === undefined) return { $: 'undefined' };
    if (typeof value === 'bigint') return { $: 'bigint', value: String(value) };
    if (value === null || ['string', 'number', 'boolean'].includes(typeof value)) return value;
    if (value instanceof Error) return { $: 'error', code: guestErrorCodes.has(value.code) ? value.code :
      value instanceof Error && value.name === 'AbortError' ? 'CANCELLED' : 'SCRIPT_EXCEPTION' };
    if (ArrayBuffer.isView(value) || value instanceof ArrayBuffer) {
      const frame = frames.length;
      frames.push(Buffer.from(value instanceof ArrayBuffer ? new Uint8Array(value)
        : new Uint8Array(value.buffer, value.byteOffset, value.byteLength)));
      return { $: 'bytes', frame };
    }
    if (value instanceof Date) return { $: 'date', value: value.toISOString() };
    if (value instanceof crypto.KeyObject) {
      const data = value.export(value.type === 'secret' ? undefined
        : { format: 'der', type: value.type === 'private' ? 'pkcs8' : 'spki' });
      try {
        return { $: 'key', type: value.type, data: data.toString('base64'),
          fields: this.encode({
            type: value.type, asymmetricKeyType: value.asymmetricKeyType,
            asymmetricKeyDetails: value.asymmetricKeyDetails, symmetricKeySize: value.symmetricKeySize,
          }, new Set(), undefined, scope, frames) };
      } finally { data.fill(0); }
    }
    if (this.callbackReverse.has(value)) return { $: 'guestCallback', id: this.callbackReverse.get(value) };
    if (typeof value?.then === 'function') {
      const id = crypto.randomUUID(), context = this.context;
      this.retainPromise(id, scope);
      const ownerRef = new WeakRef(this);
      const completion = {
        active: true, scope, context, hint, detach: null,
        settle(ok, result) {
          if (!this.active) return;
          this.active = false;
          const activeScope = this.scope;
          const selectedContext = this.context;
          const selectedHint = this.hint;
          const detach = this.detach;
          this.scope = null; this.context = 0; this.hint = undefined; this.detach = null;
          const owner = ownerRef.deref();
          let detachFailure;
          try { detach?.(); } catch (error) { detachFailure = error; }
          if (!owner) return;
          if (!activeScope || activeScope.signal.aborted || !owner.session?.live) {
            owner.completePromise(id);
            return;
          }
          try {
            owner.settlePromise(id, ok, result, activeScope, selectedContext,
              ok ? selectedHint : undefined);
          } catch (error) {
            owner.abandonPromise(id);
            if (owner.session.live) owner.session.callbackFailed(error, activeScope);
            return;
          }
          if (detachFailure && owner.session?.live)
            owner.session.callbackFailed(detachFailure, activeScope);
        },
      };
      try {
        completion.detach = scope.add(() => {
          completion.active = false;
          completion.scope = null; completion.context = 0;
          completion.hint = undefined; completion.detach = null;
        });
        const task = Promise.resolve(value).then(
          result => completion.settle(true, result),
          error => completion.settle(false, error),
        );
        this.session.trackAdapter(task);
      } catch (error) {
        completion.active = false;
        completion.scope = null; completion.context = 0; completion.hint = undefined;
        const detach = completion.detach;
        completion.detach = null;
        try { detach?.(); } catch {}
        this.completePromise(id);
        throw error;
      }
      return { $: 'promise', id };
    }
    let role = hint ?? classifyNative(value, this.tags);
    if (role) {
      requireValue(Object.hasOwn(nativeRoles, role), 'PERMISSION_DENIED');
      this.tags.set(value, role);
      let id = this.reverse.get(value);
      let receipt = id && this.handles.get(id);
      let created = false;
      if (!receipt) {
        id = crypto.randomUUID();
        receipt = { id, value, role, methods: new Map(),
          persistent: scope === this.session.root, leases: new Set(),
          boundScope: scope?.ownsTransport ? scope : null };
        this.reverse.set(value, id);
        this.handles.set(id, receipt);
        created = true;
      }
      if (created) this.bindNaturalRelease(receipt);
      try { this.retainHandle(receipt, receipt.persistent ? this.session.root : scope); }
      catch (error) {
        if (created) {
          this.reverse.delete(value);
          this.handles.delete(id);
        }
        throw error;
      }
      return { $: 'handle', id, role, callable: false };
    }
    requireValue(typeof value !== 'function' && !seen.has(value), 'PERMISSION_DENIED');
    const proto = Object.getPrototypeOf(value);
    requireValue(Array.isArray(value) || proto === Object.prototype || proto === null, 'PERMISSION_DENIED');
    seen.add(value);
    const result = Array.isArray(value) ? value.map(item => this.encode(item, seen, undefined, scope, frames))
      : { $: 'object', value: Object.fromEntries(Object.entries(value)
        .map(([key, item]) => [key, this.encode(item, seen, undefined, scope, frames)])) };
    seen.delete(value);
    return result;
  }

  decode(value, scope = this.ownerScope(), frames = []) {
    if (value === null || typeof value !== 'object') return value;
    if (Array.isArray(value)) return value.map(item => this.decode(item, scope, frames));
    switch (value.$) {
      case 'undefined': return undefined;
      case 'bigint': return BigInt(value.value);
      case 'bytes':
        requireValue(Number.isSafeInteger(value.frame) && value.frame >= 0 &&
          value.frame < frames.length, 'SCRIPT_EXCEPTION');
        return Buffer.from(frames[value.frame]);
      case 'date': return new Date(value.value);
      case 'error': return Object.assign(new Error('Guest callback failed'), { code: value.code });
      case 'key': {
        requireValue(['secret', 'private', 'public'].includes(value.type) &&
          typeof value.data === 'string', 'INVALID_ARGUMENT');
        const data = Buffer.from(value.data, 'base64');
        try {
          return value.type === 'secret' ? crypto.createSecretKey(data)
            : value.type === 'private' ? crypto.createPrivateKey({ key: data, format: 'der', type: 'pkcs8' })
              : crypto.createPublicKey({ key: data, format: 'der', type: 'spki' });
        } finally { data.fill(0); }
      }
      case 'handle': {
        const receipt = this.handles.get(value.id);
        requireValue(receipt && receipt.role !== 'callable', 'PERMISSION_DENIED');
        this.retainHandle(receipt, scope);
        return receipt.value;
      }
      case 'callback': {
        requireValue(Number.isSafeInteger(value.id) && value.id > 0, 'INVALID_ARGUMENT');
        requireValue(Number.isSafeInteger(value.context) && value.context >= 0, 'INVALID_ARGUMENT');
        // A release queued for the guest may not have been flushed yet. This
        // callback is live again, so revoke that stale release notification.
        this.pendingGuestRelease.callbacks.delete(value.id);
        let receipt = this.callbacks.get(value.id);
        let callback = receipt?.ref.deref();
        if (!receipt || !callback) {
          if (receipt) {
            this.callbackFinalizer?.unregister(receipt);
            // Detach the stale receipt from leases too. Otherwise a later
            // lease close would look the id up and release the new receipt.
            for (const lease of receipt.leases) lease.callbacks.delete(value.id);
            receipt.leases.clear();
            this.callbacks.delete(value.id);
          }
          const ownerRef = new WeakRef(this);
          receipt = { id: value.id, context: value.context, ref: null, leases: new Set() };
          callback = function(...args) {
            const owner = ownerRef.deref();
            if (!owner) return;
            const session = owner.session, services = owner.services;
            if (!session?.live || !services ||
              owner.callbacks.get(receipt.id) !== receipt) return;
            let active, requestScope;
            try {
              requestScope = args.map(argument => services.requestScope(argument))
                .find(Boolean);
              const contextScope = requestScope ??
                owner.metadataScope(receipt.context, null);
              // Guest callback execution keeps the request's metadata context;
              // native resources created by it remain session-owned.
              // A cancelled request must still deliver its cleanup callbacks;
              // those continue under the live session ownership scope.
              active = owner.session.resourceScope?.() ?? owner.ownerScope();
              if (owner.session !== session || owner.services !== services || !session.live ||
                owner.callbacks.get(receipt.id) !== receipt ||
                !active || active.signal.aborted) return;
              // Only known instance-owned receivers cross. Never globalThis,
              // arbitrary library callback contexts, or a host prototype.
              const frames = [];
              const receiver = owner.reverse.has(this) || classifyNative(this, owner.tags)
                ? owner.encode(this, new Set(), undefined,
                  services.requestScope(this) ?? active, frames) : { $: 'undefined' };
              // Only real inbound request arguments belong to its transport.
              // An outbound response may arrive in that request's metadata
              // context without becoming one of its revocable handles.
              const argumentScope = requestScope ?? active;
              const result = session.nativeCallback(receipt.id,
                owner.encode(args, new Set(), undefined, argumentScope, frames), active, receiver,
                Boolean(requestScope), contextScope && !contextScope.signal.aborted
                  ? contextScope : null, frames);
              const callbackResult = result && Object.hasOwn(result, 'frames')
                ? result.value : result;
              if (callbackResult?.$ === 'callbackPromise') {
                const pending = deferred();
                pending.scope = active;
                owner.callbackReturns.set(callbackResult.id, pending);
                let detach;
                try {
                  detach = pending.scope.add(() => {
                    if (owner.callbackReturns.get(callbackResult.id) === pending)
                      owner.callbackReturns.delete(callbackResult.id);
                    pending.reject(fault('CANCELLED'));
                  });
                } catch (error) {
                  if (owner.callbackReturns.get(callbackResult.id) === pending)
                    owner.callbackReturns.delete(callbackResult.id);
                  pending.reject(fault('CANCELLED'));
                  throw error;
                }
                pending.promise.finally(detach).catch(() => {});
                return pending.promise;
              }
              return owner.decode(callbackResult, owner.session.resourceScope?.() ?? active,
                result?.frames ?? []);
            } catch (error) {
              if (session.live) session.callbackFailed(error, active ?? session.root,
                requestScope ?? null);
              return undefined;
            }
          };
          receipt.ref = new WeakRef(callback);
          this.callbacks.set(value.id, receipt);
          this.callbackReverse.set(callback, value.id);
          this.callbackFinalizer?.register(callback, receipt, receipt);
        }
        requireValue(receipt.context === value.context, 'INVALID_ARGUMENT');
        this.retainCallback(receipt, scope);
        this.retainContext(receipt.context, scope);
        return callback;
      }
      case 'object': return Object.fromEntries(Object.entries(value.value)
        .map(([key, item]) => [key, this.decode(item, scope, frames)]));
      default: throw fault('INVALID_ARGUMENT');
    }
  }

  settle(id, value, scope, context, frames = []) {
    try {
      if (!this.session.live || scope.signal.aborted) {
        this.abandonPromise(id);
        return;
      }
      this.session.call('nativeSettled', [id, JSON.stringify(value), String(context ?? 0)], scope,
        frames);
      this.completePromise(id);
    } catch (error) {
      this.abandonPromise(id);
      throw error;
    }
  }

  settlePromise(id, ok, value, scope, context, hint) {
    if (!this.session.live || scope.signal.aborted) {
      this.abandonPromise(id);
      return;
    }
    const frames = [];
    this.settle(id, { ok, value: this.encode(value, new Set(), ok ? hint : undefined, scope,
      frames) }, scope, context, frames);
  }

  member(receipt, key, scope = this.ownerScope(), frames = []) {
    const rules = nativeRoles[receipt.role];
    requireValue(typeof key === 'string' && rules &&
      (rules.fields.has(key) || rules.methods.has(key)), 'PERMISSION_DENIED');
    const actual = nativeSymbols.get(key) ?? key;
    const owner = receipt.boundScope ?? (receipt.persistent ? this.session.root : scope);
    if (rules.methods.has(key)) {
      if (typeof receipt.value[actual] !== 'function')
        return this.encode(undefined, new Set(), undefined, owner, frames);
      let method = receipt.methods.get(key);
      let methodReceipt = method && this.handles.get(method.id);
      if (!methodReceipt) {
        method = this.callable((...args) => this.invoke(receipt, key, args),
          undefined, key, false, undefined, owner);
        receipt.methods.set(key, method);
        methodReceipt = this.handles.get(method.id);
      }
      this.retainHandle(methodReceipt, owner);
      return method;
    }
    return this.encode(receipt.value[actual], new Set(), undefined, owner, frames);
  }

  invoke(receipt, key, args) {
    const rules = nativeRoles[receipt.role];
    requireValue(rules?.methods.has(key), 'PERMISSION_DENIED');
    if (receipt.role === 'http2Session' && key === 'request') {
      requireValue(!args[1]?.createConnection, 'PERMISSION_DENIED');
      const result = receipt.value.request(...args);
      this.tags.set(result, 'http2Stream'); return result;
    }
    const actual = nativeSymbols.get(key) ?? key;
    if (receipt.role === 'readableWeb' && ['pipeTo', 'pipeThrough'].includes(key)) {
      const scope = this.ownerScope();
      const controller = new AbortController();
      const lifecycle = { detach: null };
      const cancel = () => {
        lifecycle.detach = null;
        controller.abort(fault('CANCELLED'));
      };
      lifecycle.detach = scope.add(cancel);
      const complete = () => {
        const detach = lifecycle.detach;
        lifecycle.detach = null;
        detach?.();
      };
      const options = args[1] && typeof args[1] === 'object' ? { ...args[1] } : {};
      try {
        if (key === 'pipeThrough') {
          const transform = args[0];
          requireValue(transform?.readable instanceof webStreams.ReadableStream &&
            transform?.writable instanceof webStreams.WritableStream, 'INVALID_ARGUMENT');
          const task = receipt.value.pipeTo(transform.writable,
            { ...options, signal: controller.signal });
          const owned = Promise.resolve(task).finally(complete);
          this.session.trackAdapter(owned.catch(() => {}));
          return transform.readable;
        }
        const result = receipt.value.pipeTo(args[0], { ...options, signal: controller.signal });
        Promise.resolve(result).finally(complete).catch(() => {});
        return result;
      } catch (error) {
        complete();
        throw error;
      }
    }
    let result;
    if (key === 'removeAllListeners') {
      // Guest removal only drops guest callbacks. Host ownership guards must
      // survive until the resource closes.
      const events = args.length ? [args[0]]
        : typeof receipt.value.eventNames === 'function' ? receipt.value.eventNames() : [];
      for (const event of events) {
        const listeners = typeof receipt.value.listeners === 'function'
          ? receipt.value.listeners(event) : [];
        for (const listener of listeners) {
          if (this.callbackReverse.has(listener))
            receipt.value.removeListener?.(event, listener);
        }
      }
      result = receipt.value;
    } else result = Reflect.apply(receipt.value[actual], receipt.value, args);
    if (receipt.role === 'tlsSocket' && ['getPeerCertificate', 'getCertificate'].includes(key)) {
      const seen = new Set();
      for (let certificate = result; certificate && typeof certificate === 'object' &&
        !seen.has(certificate); certificate = certificate.issuerCertificate) {
        seen.add(certificate);
        this.tags.set(certificate, 'certificate');
      }
    }
    this.naturalReleaseFromInvocation(receipt, key);
    if (['entries', 'keys', 'values', '@@iterator', '@@asyncIterator'].includes(key) && result &&
      typeof result.next === 'function') this.tags.set(result, 'iterator');
    if (key === 'copy' && receipt.role === 'hash') this.tags.set(result, 'hash');
    return result;
  }

  pipeStream(receipt, args) {
    // Guest Readable.pipe(Writable) can stay entirely on the host when both
    // endpoints are registered native streams. A guest data listener makes
    // that unsafe because it would stop receiving its own chunks, so leave
    // that case to the per-chunk fallback.
    requireValue(Array.isArray(args) && args.length === 1, 'INVALID_ARGUMENT');
    const target = this.reverse.get(args[0]);
    const targetReceipt = target && this.handles.get(target);
    requireValue(pipeReadableRoles.has(receipt.role) && pipeWritableRoles.has(targetReceipt?.role) &&
      typeof receipt.value?.pipe === 'function' &&
      typeof targetReceipt.value?.write === 'function' &&
      typeof targetReceipt.value?.end === 'function' &&
      receipt.value !== targetReceipt.value, 'PERMISSION_DENIED');
    if ((receipt.value.listenerCount?.('data') ?? 0) > 0) return false;
    receipt.value.pipe(targetReceipt.value);
    return true;
  }

  unpipeStream(receipt, args) {
    requireValue(Array.isArray(args) && args.length === 1, 'INVALID_ARGUMENT');
    const target = this.reverse.get(args[0]);
    const targetReceipt = target && this.handles.get(target);
    const targets = receipt.value?._readableState?.pipes;
    if (!targetReceipt || !pipeReadableRoles.has(receipt.role) ||
      !pipeWritableRoles.has(targetReceipt.role) ||
      !Array.isArray(targets) || !targets.includes(targetReceipt.value)) return true;
    try { receipt.value.unpipe(targetReceipt.value); } catch { /* Destination already closed. */ }
    return true;
  }

  bridge(input, frames = [], outFrames = []) {
    const previous = this.context;
    try {
      this.context = Number.isSafeInteger(input.context) && input.context > 0 ? input.context : 0;
      if (input.operation === 'callbackDone') {
        const receipt = this.callbackReturns.get(input.id);
        if (!receipt || receipt.scope.signal.aborted) return { ok: true, value: null };
        return this.session.contextScope.run(receipt.scope, () =>
          this.bridgeScoped(input, receipt.scope, frames, outFrames));
      }
      const scope = this.scopeForContext(this.context, input.originContext);
      // The ambient context carries request metadata for the services facade,
      // while the lease scope keeps native resources session-owned. A pump or
      // module turn runs under the session's coarse scopes; the guest context
      // still names the request whose metadata the operation must observe.
      const ambient = this.session.contextScope.getStore();
      const metadata = ambient && !ambient.signal.aborted && ambient !== this.session.root &&
        ambient !== this.session.resourceScope?.()
        ? ambient : this.metadataScope(this.context, scope);
      return this.session.contextScope.run(metadata, () =>
        this.bridgeScoped(input, scope, frames, outFrames));
    } catch (error) {
      return this.bridgeFailure(input, error);
    } finally { this.context = previous; }
  }

  bridgeScoped(input, scope, frames = [], outFrames = []) {
    this.session.assertLive();
    const { operation } = input;
    if (operation === 'context') {
      this.retainContext(this.context, scope);
      return { ok: true, value: null };
    }
    if (operation === 'requestId') return { ok: true, value: scope.requestId ?? 0 };
    if (operation === 'module') {
      requireValue(Object.hasOwn(this.builtins, input.name), 'UNSUPPORTED_DEPENDENCY');
      this.moduleCache ??= new Map();
      if (!this.moduleCache.has(input.name)) {
        const frames = [];
        this.moduleCache.set(input.name, { frames, value: this.moduleValue(
          this.builtins[input.name], new Set(), this.session.root, frames) });
      }
      const cached = this.moduleCache.get(input.name);
      outFrames.push(...cached.frames);
      return { ok: true, value: cached.value };
    }
    if (operation === 'source') return { ok: true, value: this.source(input.name, input.parent ?? null) };
    if (operation === 'memory') return { ok: true, value: this.session.engine.memoryUsage() };
    if (operation === 'uptime') return { ok: true, value: (performance.now() - this.started) / 1000 };
    if (operation === 'callbackDone') {
      const receipt = this.callbackReturns.get(input.id);
      if (receipt) {
        this.callbackReturns.delete(input.id);
        if (input.ok) receipt.resolve(this.decode(input.value, scope, frames));
        else receipt.reject(fault('SCRIPT_EXCEPTION'));
      }
      return { ok: true, value: null };
    }
    if (operation === 'releaseCallbacks') {
      requireValue(Array.isArray(input.ids) &&
        input.ids.every(id => Number.isSafeInteger(id) && id > 0), 'INVALID_ARGUMENT');
      this.releaseCallbacks(input.ids);
      return { ok: true, value: null };
    }
    if (operation === 'releaseHandles') {
      requireValue(Array.isArray(input.ids) &&
        input.ids.every(id => typeof id === 'string' && id.length > 0 && id.length <= 128),
      'INVALID_ARGUMENT');
      this.releaseGuestHandles(input.ids);
      return { ok: true, value: null };
    }
    if (operation === 'key') {
      requireValue(['export', 'equals'].includes(input.method), 'PERMISSION_DENIED');
      const key = this.decode(input.key, scope, frames),
        args = this.decode(input.args, scope, frames);
      requireValue(key instanceof crypto.KeyObject && Array.isArray(args), 'INVALID_ARGUMENT');
      const result = Reflect.apply(key[input.method], key, args);
      try {
        return { ok: true, value: this.encode(result, new Set(), undefined, scope, outFrames) };
      } finally { if (Buffer.isBuffer(result)) result.fill(0); }
    }
    const receipt = this.handles.get(input.id);
    if (!receipt && input.operation === 'invoke' && input.key === 'unpipeTo')
      return { ok: true, value: true };
    requireValue(receipt, 'PERMISSION_DENIED');
    this.retainHandle(receipt, scope);
    if (operation === 'get') return { ok: true,
      value: this.member(receipt, input.key, scope, outFrames) };
    if (operation === 'keys') {
      const rules = nativeRoles[receipt.role];
      requireValue(rules, 'PERMISSION_DENIED');
      return { ok: true, value: [...rules.fields, ...rules.methods].filter(key => !key.startsWith('@@')) };
    }
    if (operation === 'set') {
      requireValue(nativeRoles[receipt.role]?.writable.has(input.key), 'PERMISSION_DENIED');
      receipt.value[input.key] = this.decode(input.value, scope, frames);
      return { ok: true, value: true };
    }
    let encoded = input.args;
    if (receipt.argumentFields && Array.isArray(encoded)) {
      // The guest projects before encoding so ignored library metadata cannot
      // allocate callbacks. Recheck the same consuming API boundary here.
      const { kind, fields, options } = receipt.argumentFields;
      encoded = encoded.map((arg, index) => {
        if (arg?.$ !== 'object') return arg;
        const value = Object.fromEntries(Object.entries(arg.value));
        return { $: 'object', value: kind === 'http'
          ? projectHttpRequestOptions(value, agent => agent === false ||
            agent?.$ === 'handle' && this.handles.get(agent.id)?.role === 'agent')
          : index === 0 ? projectFields(value, fields) : projectFields(value, options) };
      });
    }
    if (operation === 'invoke' && input.key === 'unpipeTo') {
      // Cleanup is idempotent: the native stream may already have retired its
      // receipt and unregistered its pipe after finish/close.
      const target = input.args?.[0];
      const targetReceipt = target?.$ === 'handle' && this.handles.get(target.id);
      return { ok: true, value: pipeWritableRoles.has(targetReceipt?.role)
        ? this.unpipeStream(receipt, [targetReceipt.value]) : true };
    }
    const args = this.decode(encoded, scope, frames);
    requireValue(Array.isArray(args), 'INVALID_ARGUMENT');
    let value;
    if (operation === 'invoke' && input.key === 'pipeTo' && receipt.role !== 'readableWeb') {
      // These are internal guest stream-wiring operations, not public native
      // fields. Both endpoints must already be registered stream handles.
      value = this.pipeStream(receipt, args);
    } else if (operation === 'invoke') value = this.invoke(receipt, input.key, args);
    else {
      requireValue(['call', 'construct'].includes(operation) && receipt.role === 'callable', 'PERMISSION_DENIED');
      requireValue(operation !== 'construct' || receipt.construct, 'PERMISSION_DENIED');
      value = operation === 'construct' ? Reflect.construct(receipt.fn, args)
        : Reflect.apply(receipt.fn, receipt.receiver, args);
      if (receipt.hint && value && typeof value === 'object') this.tags.set(value, receipt.hint);
    }
    return { ok: true, value: this.encode(value, new Set(), undefined, scope, outFrames),
      updates: args.flatMap((arg, index) => Buffer.isBuffer(arg)
        ? [{ index, value: this.encode(arg, new Set(), undefined, scope, outFrames) }] : []) };
  }

  bridgeFailure(input, error) {
    const code = guestErrorCodes.has(error.code) ? error.code : 'SCRIPT_EXCEPTION';
    const callable = ['call', 'construct'].includes(input.operation)
      ? this.handles.get(input.id)?.name : null;
    const dependency = ['module', 'source'].includes(input.operation) &&
      typeof input.name === 'string' && /^@?[A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)*$/.test(input.name)
      ? input.name : null;
    const expectedProbe = code === 'PERMISSION_DENIED' && ['get', 'set'].includes(input.operation);
    if (!expectedProbe && this.session.onDiagnostic) try {
      const scope = this.activeContextScope(input.context) ||
        this.activeContextScope(input.originContext) || this.ownerScope();
      this.session.onDiagnostic?.({ ...this.session.scopeIdentity(scope),
      kind: 'ScriptHostError', reason: 'host',
      operation: 'node.' + String(input.operation), code,
      ...(dependency ? { dependency } : {}),
      ...(typeof input.key === 'string' && /^[A-Za-z_$][\w$]{0,63}$/.test(input.key)
        ? { member: input.key }
        : typeof callable === 'string' && /^[A-Za-z_$][\w$]{0,63}$/.test(callable)
          ? { member: callable } : {}) }); } catch {}
    return { ok: false, value: { $: 'error', code } };
  }

  async close() {
    if (this.closing) return this.closing;
    this.closing = (async () => {
      const cleanupState = this.cleanupTracker;
      let failure = cleanupState.failure;
      // Normal teardown has already disposed retired receipts. Direct close
      // disposes only still-owned handles, never retries a failed retirement.
      for (const receipt of this.handles.values()) {
        for (const lease of receipt.leases) lease.handles.delete(receipt.id);
        receipt.leases.clear();
        this.handles.delete(receipt.id);
        const handleFailure = this.disposeHandle(receipt);
        failure ??= handleFailure;
      }
      failure ??= cleanupState.failure;
      const resources = [...this.networkResources];
      const resourceOwners = this.networkResources;
      const resourceCleanups = this.networkCleanup;
      const results = await Promise.all(resources.map(resource => closeResource(resource, {
        stop: () => {
          if (!resource.destroyed) resource.destroy();
        },
        release: () => resourceCleanups.get(resource)?.(),
        timeoutMs: this.session.limits.cleanupTimeoutMs,
        closed: value => value.closed,
      })));
      for (const result of results) failure ??= result.error?.code === 'TIMEOUT'
        ? fault('TIMEOUT') : result.error;
      this.networkResources = new Set();
      this.networkCleanup = new Map();
      resourceOwners.clear();
      resourceCleanups.clear();
      failure ??= cleanupState.failure;
      const cleanup = [...cleanupState.tasks];
      if (cleanup.length) {
        let cleanupTimer;
        const settled = await Promise.race([
          Promise.allSettled(cleanup),
          new Promise(resolve => {
            cleanupTimer = setTimeout(() => resolve(null), this.session.limits.cleanupTimeoutMs);
          }),
        ]);
        clearTimeout(cleanupTimer);
        if (!settled) failure ??= fault('TIMEOUT');
        else {
          const rejected = settled.find(result => result.status === 'rejected');
          if (rejected) failure ??= rejected.reason;
        }
      }
      failure ??= cleanupState.failure;
      cleanupState.active = false;
      cleanupState.tasks.clear();
      cleanupState.failure = null;
      for (const receipt of this.callbackReturns.values()) receipt.reject(fault('STALE_SESSION'));
      this.callbackReturns.clear(); this.callbacks.clear(); this.handles.clear();
      this.contextLeases.clear(); this.promiseLeases.clear();
      this.requestContexts = new WeakMap();
      for (const values of Object.values(this.pendingGuestRelease)) values.clear();
      this.sources.clear(); this.moduleCache?.clear();
      this.callbackFinalizer = null;
      this.reverse = new WeakMap(); this.callbackReverse = new WeakMap();
      this.scopeLeases = new WeakMap();
      try { await this.services.close(); } catch (error) { failure ??= error; }
      try { await this.volume.close(); } catch (error) { failure ??= error; }
      this.fs = null;
      this.builtins = Object.create(null);
      this.tags = new WeakMap();
      this.session = null; this.services = null; this.volume = null;
      if (failure) throw failure;
    })();
    return this.closing;
  }
}
