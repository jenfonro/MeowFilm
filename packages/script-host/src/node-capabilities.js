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
  httpRequestOptionNames } from './native-roles.js';
import { dnsFacade, isLoopbackHost, publicLookup } from './network-boundary.js';

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
const listenerMethods = new Set(['on', 'addListener', 'once', 'prependListener', 'prependOnceListener']);
const destroyOnRelease = new Set(['stream', 'hash', 'cipher', 'sign', 'http2Stream']);
const networkOwner = Symbol('node-network-owner');
const ignoreError = () => {};
// Compatibility metadata only. Cat QuickJS does not apply this as a heap or
// process allocation budget, and no host memory information is exposed.
const virtualMemoryBytes = 512 * 1024 * 1024;
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
function guardDetachedNetworkResource(resource) {
  let failure, guardAttached = false, closeAttached = false;
  const guard = () => {};
  const closed = () => {
    if (closeAttached) {
      try { resource.off?.('close', closed); } catch {}
      closeAttached = false;
    }
    if (guardAttached) {
      try { resource.off?.('error', guard); } catch {}
      guardAttached = false;
    }
  };
  try {
    resource.on?.('error', guard);
    guardAttached = true;
  } catch (error) { failure ??= error; }
  try {
    resource.once?.('close', closed);
    closeAttached = true;
  } catch (error) { failure ??= error; }
  try { if (resource.closed) closed(); } catch (error) { failure ??= error; }
  return failure;
}

/** The bridge names finite capabilities. Native methods are registered with a
 * role and receiver, never selected by traversing prototypes or Function.
 * The guest can reflect on its own wrappers, not on native internals. */
export class NodeCapabilities {
  constructor(session, services, dataRoot) {
    this.session = session;
    this.services = services;
    this.volume = new PrivateVolume(dataRoot, session.installationId,
      scope => session.storageWritten(scope), session.revision.source, () => session.scope(),
      session.limits.cleanupTimeoutMs);
    this.handles = new Map();
    this.residualHandles = new Map();
    this.reverse = new WeakMap();
    this.tags = this.volume.roles;
    this.callbacks = new Map();
    this.callbackReverse = new WeakMap();
    this.callbackReturns = new Map();
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
          requireValue(!isLoopbackHost(target.hostname), 'CAPABILITY_UNAVAILABLE');
          requireValue(!options.createConnection && !options.socketPath && !options.path, 'PERMISSION_DENIED');
          const client = http2.connect(target, {
            ...this.services.tlsOptions(options, { client: true }),
            lookup: publicLookup,
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
        platform: () => 'linux', type: () => 'ScriptHost', release: () => '1',
        arch: () => 'wasm32', endianness: () => 'LE', homedir: () => '/',
        tmpdir: () => '/tmp', hostname: () => 'script', availableParallelism: () => 1,
        machine: () => 'wasm32', uptime: () => (performance.now() - this.started) / 1000,
        totalmem: () => virtualMemoryBytes,
        freemem: () => Math.max(0, virtualMemoryBytes -
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
    this.builtins.crypto.webcrypto = { subtle: Object.fromEntries([
      'encrypt', 'decrypt', 'sign', 'verify', 'digest', 'generateKey', 'deriveKey', 'deriveBits',
      'importKey', 'exportKey', 'wrapKey', 'unwrapKey',
    ].map(key => [key, crypto.webcrypto.subtle[key].bind(crypto.webcrypto.subtle)])),
    getRandomValues: value => crypto.webcrypto.getRandomValues(value), randomUUID: crypto.randomUUID };
    this.builtins.constants = { ...this.fs.constants, ...crypto.constants, ...zlib.constants };
  }

  lease(scope = this.session.scope()) {
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

  retainHandle(receipt, scope = this.session.scope()) {
    if (scope === this.session.root) receipt.persistent = true;
    return this.retain(receipt, receipt.persistent ? this.session.root : scope, 'handles');
  }

  retainCallback(receipt, scope = this.session.scope()) {
    return this.retain(receipt, scope, 'callbacks');
  }

  releaseCallbacks(ids) {
    const released = [];
    for (const id of ids) {
      const receipt = this.callbacks.get(id);
      if (!receipt) continue;
      for (const lease of receipt.leases) lease.callbacks.delete(id);
      receipt.leases.clear();
      this.callbacks.delete(id);
      this.callbackReverse.delete(receipt.value);
      released.push(id);
    }
    if (released.length) this.queueGuestRelease({ callbacks: released });
  }

  disposeHandle(receipt) {
    if (receipt.value && ['object', 'function'].includes(typeof receipt.value))
      this.reverse.delete(receipt.value);
    try {
      const handled = this.volume.release(receipt.value) || this.releaseNativeHandle(receipt);
      if (!handled && destroyOnRelease.has(receipt.role) &&
        typeof receipt.value?.destroy === 'function' && !receipt.value.destroyed) {
        receipt.value.destroy();
      }
      this.residualHandles.delete(receipt);
      return null;
    } catch (error) {
      this.residualHandles.set(receipt, error);
      return error;
    }
  }

  activeContextScope(id) {
    const receipt = this.contextLeases.get(id);
    if (!receipt) return null;
    return [...receipt.leases].map(lease => lease.scope)
      .find(scope => !scope.signal.aborted) ?? null;
  }

  retainContext(id, scope = this.session.scope()) {
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
      // execution scope, so use that scope rather than promoting the work to
      // the session root.
      const scope = this.session.scope();
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
    const scope = this.session.scope();
    scope.assertOpen();
    this.retainContext(id, scope);
    return scope;
  }

  retainPromise(id, scope = this.session.scope()) {
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
      this.callbackReverse.delete(receipt.value);
      released.callbacks.push(id);
    }
    for (const id of lease.contexts) {
      const receipt = this.contextLeases.get(id);
      if (!receipt) continue;
      receipt.leases.delete(lease);
      if (receipt.leases.size) continue;
      this.contextLeases.delete(id);
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
    resources.add(socket);
    const scope = this.session.scope();
    const state = { active: true, detach: null };
    const closed = () => {
      if (!state.active) return;
      state.active = false;
      for (const [event, listener] of [['close', closed], ['error', ignoreError]]) {
        for (let attempt = 0; attempt < 2; attempt++) {
          try { socket.off?.(event, listener); break; } catch {}
        }
      }
      const detach = state.detach;
      state.detach = null;
      try { detach?.(); } catch {}
      resources.delete(socket);
      cleanups.delete(socket);
      try { delete socket[networkOwner]; } catch {
        try { socket[networkOwner] = null; } catch {}
      }
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
      if (!closedNow) guardDetachedNetworkResource(socket);
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
      requireValue(mapped.meowLocal || this.session.network === undefined, 'CAPABILITY_UNAVAILABLE');
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

  callable(fn, receiver, name, construct = false, hint, scope = this.session.scope()) {
    const id = crypto.randomUUID();
    const argumentFields = ['request', 'get'].includes(name) &&
      [this.builtins.http, this.builtins.https].includes(receiver)
      ? { 0: httpRequestOptionNames, 1: httpRequestOptionNames }
      : name === 'format' && receiver === this.builtins.url
        ? { 0: urlFormatFields, 1: urlFormatOptions } : null;
    const receipt = { id, fn, receiver, name, construct, hint, role: 'callable',
      argumentFields, persistent: scope === this.session.root, leases: new Set() };
    this.handles.set(id, receipt);
    try { this.retainHandle(receipt, scope); }
    catch (error) {
      this.handles.delete(id);
      throw error;
    }
    return { $: 'handle', id, callable: true, name, ...(argumentFields ? { argumentFields } : {}) };
  }

  moduleValue(value, seen = new Set(), scope = this.session.root) {
    if (typeof value === 'function')
      return this.callable(value, undefined, value.name, constructors.has(value.name), undefined, scope);
    if (!value || typeof value !== 'object' || Array.isArray(value) || value instanceof crypto.KeyObject)
      return this.encode(value, new Set(), undefined, scope);
    if (classifyNative(value, this.tags)) return this.encode(value, new Set(), undefined, scope);
    requireValue(!seen.has(value), 'PERMISSION_DENIED');
    seen.add(value);
    const fields = Object.fromEntries(Object.entries(value).map(([key, item]) => [key,
      typeof item === 'function'
        ? this.callable(item, value, key, constructors.has(key), returns[key], scope)
        : this.moduleValue(item, seen, scope)]));
    seen.delete(value);
    return { $: 'object', value: fields };
  }

  encode(value, seen = new Set(), hint, scope = this.session.scope()) {
    if (value === undefined) return { $: 'undefined' };
    if (typeof value === 'bigint') return { $: 'bigint', value: String(value) };
    if (value === null || ['string', 'number', 'boolean'].includes(typeof value)) return value;
    if (value instanceof Error) return { $: 'error', code: guestErrorCodes.has(value.code) ? value.code :
      value instanceof Error && value.name === 'AbortError' ? 'CANCELLED' : 'SCRIPT_EXCEPTION' };
    if (ArrayBuffer.isView(value) || value instanceof ArrayBuffer)
      return { $: 'bytes', value: Buffer.from(value instanceof ArrayBuffer ? value :
        Buffer.from(value.buffer, value.byteOffset, value.byteLength)).toString('base64') };
    if (value instanceof Date) return { $: 'date', value: value.toISOString() };
    if (value instanceof crypto.KeyObject) {
      const data = value.export(value.type === 'secret' ? undefined
        : { format: 'der', type: value.type === 'private' ? 'pkcs8' : 'spki' });
      try {
        return { $: 'key', type: value.type, data: data.toString('base64'),
          fields: this.encode({
            type: value.type, asymmetricKeyType: value.asymmetricKeyType,
            asymmetricKeyDetails: value.asymmetricKeyDetails, symmetricKeySize: value.symmetricKeySize,
          }, new Set(), undefined, scope) };
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
          persistent: scope === this.session.root, leases: new Set() };
        this.reverse.set(value, id);
        this.handles.set(id, receipt);
        created = true;
      }
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
    const result = Array.isArray(value) ? value.map(item => this.encode(item, seen, undefined, scope))
      : { $: 'object', value: Object.fromEntries(Object.entries(value)
        .map(([key, item]) => [key, this.encode(item, seen, undefined, scope)])) };
    seen.delete(value);
    return result;
  }

  decode(value, scope = this.session.scope()) {
    if (value === null || typeof value !== 'object') return value;
    if (Array.isArray(value)) return value.map(item => this.decode(item, scope));
    switch (value.$) {
      case 'undefined': return undefined;
      case 'bigint': return BigInt(value.value);
      case 'bytes': return Buffer.from(value.value, 'base64');
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
        if (!this.callbacks.has(value.id)) {
          const ownerRef = new WeakRef(this);
          const receipt = { id: value.id, context: value.context, value: null, leases: new Set() };
          const callback = function(...args) {
            const owner = ownerRef.deref();
            if (!owner) return;
            const session = owner.session, services = owner.services;
            if (!session?.live || !services ||
              owner.callbacks.get(receipt.id) !== receipt) return;
            let active;
            try {
              const requestScope = args.map(argument => services.requestScope(argument))
                .find(Boolean);
              active = requestScope ?? [...receipt.leases]
                .map(lease => lease.scope).find(candidate => !candidate.signal.aborted);
              if (owner.session !== session || owner.services !== services || !session.live ||
                owner.callbacks.get(receipt.id) !== receipt ||
                !active || active.signal.aborted) return;
              // Only known instance-owned receivers cross. Never globalThis,
              // arbitrary library callback contexts, or a host prototype.
              const receiver = owner.reverse.has(this) || classifyNative(this, owner.tags)
                ? owner.encode(this, new Set(), undefined, active) : { $: 'undefined' };
              const result = session.nativeCallback(receipt.id,
                owner.encode(args, new Set(), undefined, active), active, receiver,
                Boolean(requestScope));
              if (result?.$ === 'callbackPromise') {
                const pending = deferred();
                pending.scope = active;
                owner.callbackReturns.set(result.id, pending);
                let detach;
                try {
                  detach = active.add(() => {
                    if (owner.callbackReturns.get(result.id) === pending)
                      owner.callbackReturns.delete(result.id);
                    pending.reject(fault('CANCELLED'));
                  });
                } catch (error) {
                  if (owner.callbackReturns.get(result.id) === pending)
                    owner.callbackReturns.delete(result.id);
                  pending.reject(fault('CANCELLED'));
                  throw error;
                }
                pending.promise.finally(detach).catch(() => {});
                return pending.promise;
              }
              return owner.decode(result, active);
            } catch (error) {
              if (session.live) session.callbackFailed(error, active ?? session.root);
              return undefined;
            }
          };
          receipt.value = callback;
          this.callbacks.set(value.id, receipt);
          this.callbackReverse.set(callback, value.id);
        }
        const receipt = this.callbacks.get(value.id);
        requireValue(receipt.context === value.context, 'INVALID_ARGUMENT');
        this.retainCallback(receipt, scope);
        this.retainContext(receipt.context, scope);
        return receipt.value;
      }
      case 'object': return Object.fromEntries(Object.entries(value.value)
        .map(([key, item]) => [key, this.decode(item, scope)]));
      default: throw fault('INVALID_ARGUMENT');
    }
  }

  settle(id, value, scope, context) {
    try {
      if (!this.session.live || scope.signal.aborted) {
        this.abandonPromise(id);
        return;
      }
      this.session.call('nativeSettled', [id, JSON.stringify(value), String(context ?? 0)], scope);
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
    this.settle(id, { ok, value: this.encode(value, new Set(), ok ? hint : undefined, scope) },
      scope, context);
  }

  member(receipt, key, scope = this.session.scope()) {
    const rules = nativeRoles[receipt.role];
    requireValue(typeof key === 'string' && rules &&
      (rules.fields.has(key) || rules.methods.has(key)), 'PERMISSION_DENIED');
    const actual = nativeSymbols.get(key) ?? key;
    const owner = receipt.persistent ? this.session.root : scope;
    if (rules.methods.has(key)) {
      if (typeof receipt.value[actual] !== 'function')
        return this.encode(undefined, new Set(), undefined, owner);
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
    return this.encode(receipt.value[actual], new Set(), undefined, owner);
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
      const scope = this.session.scope();
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
    const scope = this.session.scope();
    if (scope !== this.session.root && listenerMethods.has(key) &&
      (typeof args[0] === 'string' || typeof args[0] === 'symbol') && typeof args[1] === 'function') {
      const [event, listener] = args;
      try {
        scope.add(() => {
          try { receipt.value.removeListener?.(event, listener); } catch {}
        });
      } catch (error) {
        try { receipt.value.removeListener?.(event, listener); } catch {}
        throw error;
      }
    }
    if (['entries', 'keys', 'values', '@@iterator', '@@asyncIterator'].includes(key) && result &&
      typeof result.next === 'function') this.tags.set(result, 'iterator');
    if (key === 'copy' && receipt.role === 'hash') this.tags.set(result, 'hash');
    return result;
  }

  bridge(input) {
    const previous = this.context;
    try {
      this.context = Number.isSafeInteger(input.context) && input.context > 0 ? input.context : 0;
      if (input.operation === 'callbackDone') {
        const receipt = this.callbackReturns.get(input.id);
        if (!receipt || receipt.scope.signal.aborted) return { ok: true, value: null };
        return this.session.contextScope.run(receipt.scope, () =>
          this.bridgeScoped(input, receipt.scope));
      }
      const scope = this.scopeForContext(this.context, input.originContext);
      return this.session.contextScope.run(scope, () => this.bridgeScoped(input, scope));
    } catch (error) {
      return this.bridgeFailure(input, error);
    } finally { this.context = previous; }
  }

  bridgeScoped(input, scope) {
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
      if (!this.moduleCache.has(input.name))
        this.moduleCache.set(input.name, this.moduleValue(this.builtins[input.name],
          new Set(), this.session.root));
      return { ok: true, value: this.moduleCache.get(input.name) };
    }
    if (operation === 'source') return { ok: true, value: this.source(input.name, input.parent ?? null) };
    if (operation === 'memory') return { ok: true, value: this.session.engine.memoryUsage() };
    if (operation === 'uptime') return { ok: true, value: (performance.now() - this.started) / 1000 };
    if (operation === 'callbackDone') {
      const receipt = this.callbackReturns.get(input.id);
      if (receipt) {
        this.callbackReturns.delete(input.id);
        if (input.ok) receipt.resolve(this.decode(input.value, scope));
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
    if (operation === 'key') {
      requireValue(['export', 'equals'].includes(input.method), 'PERMISSION_DENIED');
      const key = this.decode(input.key, scope), args = this.decode(input.args, scope);
      requireValue(key instanceof crypto.KeyObject && Array.isArray(args), 'INVALID_ARGUMENT');
      const result = Reflect.apply(key[input.method], key, args);
      try {
        return { ok: true, value: Buffer.isBuffer(result)
          ? { $: 'bytes', value: result.toString('base64') }
          : this.encode(result, new Set(), undefined, scope) };
      } finally { if (Buffer.isBuffer(result)) result.fill(0); }
    }
    const receipt = this.handles.get(input.id);
    requireValue(receipt, 'PERMISSION_DENIED');
    this.retainHandle(receipt, scope);
    if (operation === 'get') return { ok: true, value: this.member(receipt, input.key, scope) };
    if (operation === 'keys') {
      const rules = nativeRoles[receipt.role];
      requireValue(rules, 'PERMISSION_DENIED');
      return { ok: true, value: [...rules.fields, ...rules.methods].filter(key => !key.startsWith('@@')) };
    }
    if (operation === 'set') {
      requireValue(nativeRoles[receipt.role]?.writable.has(input.key), 'PERMISSION_DENIED');
      receipt.value[input.key] = this.decode(input.value, scope);
      return { ok: true, value: true };
    }
    let encoded = input.args;
    if (receipt.argumentFields && Array.isArray(encoded)) {
      // The guest projects before encoding so ignored library metadata cannot
      // allocate callbacks. Recheck the same consuming API boundary here.
      encoded = encoded.map((arg, index) => receipt.argumentFields[index] && arg?.$ === 'object'
        ? { $: 'object', value: Object.fromEntries(Object.entries(arg.value)
          .filter(([key]) => receipt.argumentFields[index].includes(key) &&
            !(key === 'createConnection' && receipt.argumentFields[index].includes('agent') &&
              (arg.value.agent === false || arg.value.agent?.$ === 'handle')))) } : arg);
    }
    const args = this.decode(encoded, scope);
    requireValue(Array.isArray(args), 'INVALID_ARGUMENT');
    let value;
    if (operation === 'invoke') value = this.invoke(receipt, input.key, args);
    else {
      requireValue(['call', 'construct'].includes(operation) && receipt.role === 'callable', 'PERMISSION_DENIED');
      requireValue(operation !== 'construct' || receipt.construct, 'PERMISSION_DENIED');
      value = operation === 'construct' ? Reflect.construct(receipt.fn, args)
        : Reflect.apply(receipt.fn, receipt.receiver, args);
      if (receipt.hint && value && typeof value === 'object') this.tags.set(value, receipt.hint);
    }
    return { ok: true, value: this.encode(value, new Set(), undefined, scope),
      updates: args.flatMap((arg, index) => Buffer.isBuffer(arg)
        ? [{ index, value: this.encode(arg, new Set(), undefined, scope) }] : []) };
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
        this.activeContextScope(input.originContext) || this.session.scope();
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
      // Root-scope cleanup normally releases every receipt before close. Still
      // dispose any residual native owner deterministically if one cleanup
      // failed or close is invoked without the ordinary session stop sequence.
      const receipts = new Set([...this.handles.values(), ...this.residualHandles.keys()]);
      for (const receipt of receipts) {
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
      let resolveResources;
      const aggregate = {
        remaining: resources.length,
        promise: new Promise(resolve => { resolveResources = resolve; }),
      };
      if (!aggregate.remaining) resolveResources();
      const waits = resources.map(resource => {
        const wait = {
          resource, promise: null, finish: null, retry: null, error: null, settled: false,
        };
        wait.promise = new Promise(resolve => {
          wait.finish = () => {
            if (wait.settled) return;
            wait.settled = true;
            try { resource.off?.('close', wait.finish); } catch (error) { wait.error ??= error; }
            try { resourceCleanups.get(resource)?.(); } catch (error) { wait.error ??= error; }
            resolve();
          };
          let closed = false;
          try { closed = Boolean(resource.closed); } catch (error) { wait.error ??= error; }
          if (closed) { wait.finish(); return; }
          const observable = typeof resource.once === 'function';
          if (observable) {
            try { resource.once('close', wait.finish); }
            catch (error) { wait.error ??= error; }
          }
          wait.retry = () => {
            if (wait.settled) return;
            let nowClosed = false;
            try { nowClosed = Boolean(resource.closed); } catch (error) { wait.error ??= error; }
            if (nowClosed) { wait.finish(); return; }
            try {
              if (!resource.destroyed) resource.destroy();
              if (!observable && (resource.destroyed || resource.closed)) wait.finish();
            } catch (error) { wait.error ??= error; }
          };
          wait.retry();
        });
        wait.promise.then(() => {
          aggregate.remaining--;
          if (!aggregate.remaining) resolveResources();
        });
        return wait;
      });
      let timer;
      const complete = await Promise.race([
        aggregate.promise.then(() => true),
        new Promise(resolve => {
          timer = setTimeout(() => resolve(false), this.session.limits.cleanupTimeoutMs);
        }),
      ]);
      clearTimeout(timer);
      for (const wait of waits) {
        if (!wait.settled) wait.retry?.();
        failure ??= wait.error;
      }
      if (!complete) failure ??= fault('TIMEOUT');
      // A late close listener owns only the detached resource collections. It
      // can self-release without retaining this NodeCapabilities/session graph.
      this.networkResources = new Set();
      this.networkCleanup = new Map();
      for (let index = 0; index < resources.length; index++) {
        const resource = resources[index];
        const wait = waits[index];
        let closed = false;
        try { closed = Boolean(resource.closed); } catch (error) { failure ??= error; }
        if (!wait.settled) {
          try { resourceCleanups.get(resource)?.(); } catch (error) { failure ??= error; }
          wait.retry = null;
          if (!closed) {
            const guardFailure = guardDetachedNetworkResource(resource);
            failure ??= guardFailure;
          }
        } else if (closed) {
          try { resourceCleanups.get(resource)?.(); } catch (error) { failure ??= error; }
        }
        resourceOwners.delete(resource);
        resourceCleanups.delete(resource);
      }
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
      this.residualHandles.clear();
      this.contextLeases.clear(); this.promiseLeases.clear();
      for (const values of Object.values(this.pendingGuestRelease)) values.clear();
      this.sources.clear(); this.moduleCache?.clear();
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
