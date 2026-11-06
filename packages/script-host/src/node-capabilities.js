import * as crypto from 'node:crypto';
import * as net from 'node:net';
import * as tls from 'node:tls';
import * as zlib from 'node:zlib';
import * as url from 'node:url';
import * as os from 'node:os';
import stream from 'node:stream';
import streamPromises from 'node:stream/promises';
import * as web from 'node:stream/web';
import * as http2 from 'node:http2';
import * as buffer from 'node:buffer';
import * as util from 'node:util';
import * as events from 'node:events';
import * as asyncHooks from 'node:async_hooks';
import * as diagnostics from 'node:diagnostics_channel';
import * as querystring from 'node:querystring';
import * as assert from 'node:assert';
import * as worker from 'node:worker_threads';
import * as timers from 'node:timers';
import { StringDecoder } from 'node:string_decoder';
import { performance } from 'node:perf_hooks';
import { createRequire } from 'node:module';
import { readFileSync, realpathSync, lstatSync } from 'node:fs';
import { dirname, extname, isAbsolute, relative, resolve, sep, posix } from 'node:path';
import { fault, requireValue } from './errors.js';
import { PrivateVolume } from './private-volume.js';
import { NativeResources } from './cat-resources.js';
import { dnsFacade, isLoopbackHost } from './network-boundary.js';
import { closeResource, guardDetachedEmitter } from './resource-close.js';

const require = createRequire(import.meta.url);
const sdkPackages = new Set(['acorn', 'supports-color', '@opentelemetry/api']);
const packageNameFor = specifier => {
  const parts = specifier.split('/');
  requireValue(parts.length >= (specifier.startsWith('@') ? 2 : 1) &&
    parts.every(part => part.length > 0 && part !== '.' && part !== '..') &&
    !/[\u0000-\u001f\u007f]/.test(specifier), 'UNSUPPORTED_DEPENDENCY');
  return parts.slice(0, specifier.startsWith('@') ? 2 : 1).join('/');
};
const inside = (root, file) => {
  const delta = relative(root, file);
  return delta !== '..' && !delta.startsWith('..' + sep) && !isAbsolute(delta);
};
const ignoreError = () => {};

const ignoredCleanupError = error =>
  ['ERR_INVALID_STATE', 'ERR_STREAM_DESTROYED', 'ERR_STREAM_PREMATURE_CLOSE',
    'NETWORK_ERROR', 'ERR_CANCELED', 'CANCELLED']
    .includes(error?.code) || error?.name === 'AbortError';
const readableLocked = Object.getOwnPropertyDescriptor(web.ReadableStream.prototype, 'locked').get;
const writableLocked = Object.getOwnPropertyDescriptor(web.WritableStream.prototype, 'locked').get;
const signalAborted = Object.getOwnPropertyDescriptor(AbortSignal.prototype, 'aborted').get;

function webPipeOptions(options) {
  if (options != null && !['object', 'function'].includes(typeof options))
    throw Object.assign(new TypeError('Invalid pipe options'), { code: 'ERR_INVALID_ARG_TYPE' });
  const preventAbort = options?.preventAbort, preventCancel = options?.preventCancel;
  const preventClose = options?.preventClose, signal = options?.signal;
  if (signal !== undefined) signalAborted.call(signal);
  return { preventAbort: Boolean(preventAbort), preventCancel: Boolean(preventCancel),
    preventClose: Boolean(preventClose), signal };
}

function closeWebResource(role, value) {
  try {
    if (role === 'readableWeb') {
      if (value.locked) throw fault('SCRIPT_CLEANUP');
      return closeWebResource('reader',
        web.ReadableStream.prototype.getReader.call(value));
    }
    if (role === 'writableWeb') {
      if (value.locked) throw fault('SCRIPT_CLEANUP');
      return closeWebResource('writer',
        web.WritableStream.prototype.getWriter.call(value));
    }
    if (['transformWeb', 'compressionWeb', 'decompressionWeb',
      'textEncoderStream', 'textDecoderStream'].includes(role))
      return Promise.allSettled([closeWebResource('readableWeb', value.readable),
        closeWebResource('writableWeb', value.writable)]).then(results => {
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
  let released = false;
  const releaseOnce = () => { if (!released) released = releaseLock(); };
  const attempt = (operation, release = false) => {
    let task;
    try { task = operation(); } catch (error) {
      failed(error);
      if (release) releaseOnce();
      return;
    }
    tasks.push(Promise.resolve(task).then(
      () => { if (release) releaseOnce(); },
      error => { failed(error); if (release) releaseOnce(); },
    ));
  };
  if (role === 'reader') {
    attempt(() => value.cancel(reason), true);
    releaseOnce();
  } else if (role === 'writer') {
    attempt(() => value.abort(reason), true);
    releaseOnce();
  } else if (role === 'iterator' && typeof value.return === 'function') {
    attempt(() => value.return());
  }
  return Promise.all(tasks).then(() => {
    if (failure) throw failure;
  });
}
/** Native Node values stay in the Cat Worker. Only resources with a real
 * lifecycle are registered; ordinary values and keys use Node ownership. */
export class NodeCapabilities {
  constructor(session, services, dataRoot) {
    this.session = session;
    this.services = services;
    this.volume = new PrivateVolume(dataRoot, session.installationId,
      scope => session.storageWritten(scope), session.revision.source,
      () => session.scope(), session.limits.cleanupTimeoutMs, () => session.resourceScope());
    this.sources = new Map();
    this.networkResources = new Set();
    this.streams = new NativeResources();
    this.webResources = new NativeResources();
    this.webPipes = new Map();
    this.webPipeAbort = new AbortController();
    this.webTees = new WeakMap();
    this.webTeeBranches = new WeakMap();
    this.webObserved = new WeakSet();
    this.bodyObserved = new WeakSet();
    this.blobObserved = new WeakSet();
    this.resourceCleanup = new NativeResources();
    this.emitters = new WeakSet();
    this.failure = null;
    this.fs = this.volume.facade();
    for (const [name, method] of Object.entries(this.fs)) {
      if (typeof method !== 'function') continue;
      this.fs[name] = (...args) => {
        const scope = session.scope();
        if (typeof args.at(-1) === 'function') {
          const callback = args.pop();
          args.push((...values) => session.runCallback(callback, undefined, values, scope));
        }
        const value = method(...args);
        if (/^create(?:Read|Write)Stream$/.test(name)) this.observeEmitter(value);
        return value;
      };
    }
    this.fs.realpath.native = this.fs.realpath;
    this.fs.realpathSync.native = this.fs.realpathSync;
    this.timers = this.timerModule();
    this.process = this.processModule();
    // Keep the CommonJS constructor and its own statics separate from Node's singleton.
    const streams = function Stream(...args) {
      return new.target ? Reflect.construct(stream, args, new.target) :
        Reflect.apply(stream, this, args);
    };
    Object.setPrototypeOf(streams, Object.getPrototypeOf(stream));
    Object.defineProperties(streams, Object.getOwnPropertyDescriptors(stream));
    streams.Stream = streams;
    const wrapStream = Native => {
      const owner = this;
      function Managed(...args) {
        if (!new.target && this instanceof Native) {
          Reflect.apply(Native, this, args);
          return owner.ownStream(this);
        }
        return owner.ownStream(Reflect.construct(Native, args,
          new.target && new.target !== Managed ? new.target : Native));
      }
      Object.setPrototypeOf(Managed, Native);
      Managed.prototype = Native.prototype;
      // Writable's native check also recognizes Duplex without making every
      // Writable subclass an instanceof match for an unrelated Duplex.
      if (Object.hasOwn(Native, Symbol.hasInstance))
        Object.defineProperty(Managed, Symbol.hasInstance, {
          value(value) {
            return Reflect.apply(Native[Symbol.hasInstance],
              this === Managed ? Native : this, [value]);
          },
        });
      return Managed;
    };
    // Stream itself is an EventEmitter base, not a destroyable I/O resource.
    for (const name of ['Readable', 'Writable', 'Duplex', 'Transform', 'PassThrough'])
      streams[name] = wrapStream(stream[name]);
    for (const name of ['from', 'fromWeb', 'fromIterable', 'wrap']) {
      if (typeof stream.Readable[name] === 'function')
        streams.Readable[name] = (...args) => this.ownStream(stream.Readable[name](...args));
    }
    streams.Readable.toWeb = (...args) => this.ownWeb(stream.Readable.toWeb(...args), 'readableWeb');
    streams.Writable.fromWeb = (...args) => this.ownStream(stream.Writable.fromWeb(...args));
    streams.Writable.toWeb = (...args) => this.ownWeb(stream.Writable.toWeb(...args), 'writableWeb');
    streams.Duplex.from = (...args) => this.ownStream(stream.Duplex.from(...args));
    streams.Duplex.fromWeb = (...args) => this.ownStream(stream.Duplex.fromWeb(...args));
    streams.Duplex.toWeb = (...args) => {
      const pair = stream.Duplex.toWeb(...args);
      this.ownWeb(pair.readable, 'readableWeb');
      this.ownWeb(pair.writable, 'writableWeb');
      return pair;
    };
    const zlibs = { ...zlib };
    for (const name of ['Gzip', 'Gunzip', 'Deflate', 'Inflate', 'DeflateRaw', 'InflateRaw',
      'Unzip', 'BrotliCompress', 'BrotliDecompress', 'ZstdCompress', 'ZstdDecompress'])
      if (typeof zlib[name] === 'function') zlibs[name] = wrapStream(zlib[name]);
    for (const key of Object.keys(zlib)) if (/^create/.test(key) && typeof zlib[key] === 'function')
      zlibs[key] = (...args) => this.ownStream(zlib[key](...args));
    for (const name of ['gzip', 'gunzip', 'deflate', 'inflate', 'deflateRaw', 'inflateRaw',
      'unzip', 'brotliCompress', 'brotliDecompress', 'zstdCompress', 'zstdDecompress'])
      if (typeof zlib[name] === 'function')
        zlibs[name] = (...args) => this.nativeCall(zlib[name], zlib, args);
    const cryptos = { ...crypto };
    for (const name of ['randomBytes', 'randomFill', 'pbkdf2', 'scrypt', 'generateKey',
      'generateKeyPair', 'generatePrime', 'checkPrime', 'sign', 'verify'])
      cryptos[name] = (...args) => this.nativeCall(crypto[name], crypto, args);
    const subtleMethods = new Map();
    const subtle = new Proxy(crypto.subtle, { get: (target, name) => {
      const value = Reflect.get(target, name, target);
      if (typeof value !== 'function') return value;
      if (!subtleMethods.has(name)) subtleMethods.set(name, (...args) => {
        this.ownerScope().assertOpen();
        return this.session.trackAdapter(Reflect.apply(value, target, args));
      });
      return subtleMethods.get(name);
    } });
    cryptos.subtle = subtle;
    cryptos.webcrypto = new Proxy(crypto.webcrypto, { get: (target, name) => {
      if (name === 'subtle') return subtle;
      const value = Reflect.get(target, name, target);
      return typeof value === 'function' ? value.bind(target) : value;
    } });
    for (const [name, terminal, type] of [['createHash', 'digest', 'Hash'], ['createHmac', 'digest', 'Hmac'],
      ['createCipheriv', 'final', 'Cipheriv'], ['createDecipheriv', 'final', 'Decipheriv'],
      ['createSign', 'sign', 'Sign'], ['createVerify', 'verify', 'Verify']]) {
      cryptos[name] = (...args) => this.ownCryptoStream(crypto[name](...args), terminal);
      const Native = crypto[type], owner = this;
      function Managed(...args) {
        return owner.ownCryptoStream(Reflect.construct(Native, args,
          new.target && new.target !== Managed ? new.target : Native), terminal);
      }
      Object.setPrototypeOf(Managed, Native); Managed.prototype = Native.prototype;
      cryptos[type] = Managed;
    }
    const webs = { ...web };
    for (const [key, role] of Object.entries({ ReadableStream: 'readableWeb',
      WritableStream: 'writableWeb', TransformStream: 'transformWeb',
      CompressionStream: 'compressionWeb', DecompressionStream: 'decompressionWeb',
      TextEncoderStream: 'textEncoderStream', TextDecoderStream: 'textDecoderStream' })) {
      const Native = web[key], owner = this;
      function Managed(...args) {
        let value, ended = false;
        const finished = () => {
          ended = true;
          if (value) owner.forgetWeb(value);
        };
        if (['ReadableStream', 'WritableStream'].includes(key) && args[0]) {
          const underlying = args[0], input = Object.create(underlying);
          for (const method of ['pull', 'write'])
            if (typeof underlying[method] === 'function')
              input[method] = (...input) => underlying[method].apply(underlying, input);
          if (key === 'ReadableStream') input.start = controller => {
            for (const method of ['close', 'error']) {
              const original = controller[method];
              controller[method] = (...input) => {
                const result = original.apply(controller, input); finished(); return result;
              };
            }
            return underlying.start?.call(underlying, controller);
          };
          for (const method of key === 'WritableStream' ? ['close', 'abort'] : ['cancel']) {
            input[method] = (...input) => {
              const result = underlying[method]?.apply(underlying, input);
              Promise.resolve(result).then(finished, () => {});
              return result;
            };
          }
          args[0] = input;
        }
        value = owner.ownWeb(Reflect.construct(Native, args,
          new.target && new.target !== Managed ? new.target : Native), role);
        if (ended) finished();
        return value;
      }
      Object.setPrototypeOf(Managed, Native);
      Managed.prototype = Native.prototype;
      webs[key] = Managed;
    }
    const channels = new Map();
    const diagnosticModule = { ...diagnostics,
      subscribe: (name, callback) => {
        diagnostics.subscribe(name, callback);
        const subscriptions = channels.get(name) ?? new Set();
        subscriptions.add(callback); channels.set(name, subscriptions);
      },
      unsubscribe: (name, callback) => {
        channels.get(name)?.delete(callback);
        return diagnostics.unsubscribe(name, callback);
      },
    };
    this.subscriptions = channels;
    const owner = this;
    const wrap = (Native, observe) => {
      function Managed(...args) {
        return observe(Reflect.construct(Native, args,
          new.target && new.target !== Managed ? new.target : Native));
      }
      Object.setPrototypeOf(Managed, Native);
      Managed.prototype = Native.prototype;
      return Managed;
    };
    this.web = {
      Request: wrap(Request, value => owner.ownBody(value)),
      Response: wrap(Response, value => owner.ownBody(value)),
      Blob: wrap(buffer.Blob, value => owner.ownBlob(value)),
      File: wrap(buffer.File, value => owner.ownBlob(value)),
    };
    for (const name of ['json', 'redirect', 'error'])
      this.web.Response[name] = (...args) => this.ownBody(Response[name](...args));
    this.builtins = {
      fs: this.fs, 'fs/promises': this.fs.promises,
      buffer: { ...buffer, Blob: this.web.Blob, File: this.web.File },
      crypto: cryptos, zlib: zlibs, events: events.default,
      util, 'util/types': util.types, string_decoder: { StringDecoder },
      stream: streams, 'stream/promises': streamPromises, 'stream/web': webs,
      'readable-stream': streams, 'readable-stream/lib/stream.js': streams,
      path: posix, 'path-browserify': posix, process: this.process,
      async_hooks: asyncHooks, diagnostics_channel: diagnosticModule, querystring,
      assert: assert.default, 'assert/strict': assert.strict,
      timers: this.timers, 'timers/promises': this.promiseTimers(),
      url: { ...url, fileURLToPath: value => {
        const input = new URL(value);
        requireValue(input.protocol === 'file:' && !input.host, 'PERMISSION_DENIED');
        this.volume.path(decodeURIComponent(input.pathname), { missing: true });
        return decodeURIComponent(input.pathname);
      }, pathToFileURL: value => {
        this.volume.path(value, { missing: true });
        return new URL('file://' + posix.resolve('/', value));
      } },
      http: services.http, https: services.https,
      net: this.netFacade(net), tls: this.netFacade(tls),
      http2: { constants: http2.constants,
        connect: (input, options = {}, callback) => {
          requireValue(!this.services.stopping, 'SERVICE_CLOSED');
          requireValue(this.session.network === undefined, 'CAPABILITY_UNAVAILABLE');
          requireValue(!options.createConnection && options.socketPath === undefined &&
            options.fd === undefined && !options.path, 'PERMISSION_DENIED');
          const local = this.services.mapLocal(String(input)) !== null;
          const client = http2.connect(this.services.outboundURL(input),
            this.services.tlsOptions(options, { client: true, local }), callback);
          const request = client.request;
          client.request = (...args) => this.ownNetwork(request.apply(client, args));
          return this.ownNetwork(client);
        },
        getDefaultSettings: http2.getDefaultSettings, getPackedSettings: http2.getPackedSettings,
        getUnpackedSettings: http2.getUnpackedSettings },
      dns: dnsFacade({ adapter: typeof session.network === 'function' }),
      os: { ...os, homedir: () => '/', tmpdir: () => '/tmp',
        userInfo: options => ({ ...os.userInfo(options), homedir: '/', shell: null }) },
      perf_hooks: { performance }, tty: { isatty: () => false },
      worker_threads: { isMainThread: worker.isMainThread, threadId: worker.threadId,
        workerData: null, parentPort: null },
      constants: { ...this.fs.constants, ...crypto.constants, ...zlib.constants },
    };
    this.builtins['dns/promises'] = this.builtins.dns.promises;
    for (const name of Object.keys(this.builtins.dns.promises)) {
      const method = this.builtins.dns[name], promise = this.builtins.dns.promises[name];
      this.builtins.dns[name] = (...args) => this.nativeCall(method, undefined, args);
      this.builtins.dns.promises[name] = (...args) => {
        this.ownerScope().assertOpen();
        return this.session.trackAdapter(promise(...args));
      };
    }
  }

  ownerScope() { return this.session.resourceScope(); }

  nativeCall(method, receiver, args) {
    this.ownerScope().assertOpen();
    if (typeof args.at(-1) !== 'function') return Reflect.apply(method, receiver, args);
    const callback = args.pop(), session = this.session, scope = session.scope();
    let complete;
    const settled = new Promise(resolve => { complete = resolve; });
    session.trackAdapter(settled);
    args.push(function(...values) {
      try { return session.runCallback(callback, this, values, scope); }
      finally { complete(); }
    });
    try { return Reflect.apply(method, receiver, args); }
    catch (error) { complete(); throw error; }
  }

  /** Keep actual EventEmitter registration/removal semantics, while attributing
   * user callbacks (including rejected async handlers) to their request. */
  observeEmitter(emitter) {
    if (!emitter || this.emitters.has(emitter) || typeof emitter.on !== 'function') return emitter;
    this.emitters.add(emitter);
    const owner = new WeakRef(this);
    const add = emitter.on, prepend = emitter.prependListener ?? add;
    const guestListeners = new WeakSet();
    const remove = emitter.removeListener;
    const register = (event, callback, once, first) => {
      requireValue(typeof callback === 'function', 'INVALID_ARGUMENT');
      const session = owner.deref()?.session;
      const scope = session?.scope();
      function listener(...args) {
        if (once) this.removeListener(event, listener);
        const node = owner.deref(), session = node?.session;
        if (!session) return;
        if (!session.live && !session.isCleanupScope?.(scope)) {
          // Native completion listeners are also attached during shutdown.
          // Terminal dispatch must finish before ownership is released.
          if (['close', 'finish', 'end', 'error'].includes(event)) {
            try {
              // A terminal listener may settle existing I/O, but must not
              // inherit a stop hook's scope and revive ordinary background work.
              const invoke = () => Reflect.apply(callback, this, args);
              const result = session.contextScope
                ? session.contextScope.run(scope ?? session.root, invoke) : invoke();
              if (result?.then) Promise.resolve(result).catch(error => { node.failure ??= error; });
              return result;
            } catch (error) { node.failure ??= error; }
          }
          return;
        }
        const inbound = node.services?.requestScope?.(args[0]);
        return session.runCallback(callback, this, args, inbound ?? scope);
      }
      listener.listener = callback;
      guestListeners.add(listener);
      (first ? prepend : add).call(emitter, event, listener);
      return emitter;
    };
    emitter.on = emitter.addListener = (event, fn) => register(event, fn, false, false);
    emitter.once = (event, fn) => register(event, fn, true, false);
    emitter.prependListener = (event, fn) => register(event, fn, false, true);
    emitter.prependOnceListener = (event, fn) => register(event, fn, true, true);
    emitter.removeAllListeners = function(event) {
      const names = arguments.length ? [event] :
        [...this.eventNames().filter(name => name !== 'removeListener'), 'removeListener'];
      for (const name of names)
        for (const listener of this.rawListeners(name).reverse())
          if (guestListeners.has(listener)) remove.call(this, name, listener);
      return this;
    };
    if (typeof emitter.pipe === 'function' && typeof emitter.unpipe === 'function') {
      const pipe = emitter.pipe, unpipe = emitter.unpipe, connections = new Map();
      emitter.pipe = function(destination, options) {
        const release = () => {
          this.off('close', closed); this.off('end', release);
          destination.off('close', release); destination.off('finish', release);
          destination.off('unpipe', unpiped);
          connections.delete(destination);
        };
        const closed = () => {
          if (!this.readableEnded && !destination.destroyed)
            destination.destroy(Object.assign(new Error('Premature close'),
              { code: 'ERR_STREAM_PREMATURE_CLOSE' }));
          release();
        };
        const unpiped = source => { if (source === this) release(); };
        connections.get(destination)?.();
        connections.set(destination, release);
        add.call(this, 'close', closed); add.call(this, 'end', release);
        events.EventEmitter.prototype.on.call(destination, 'close', release);
        events.EventEmitter.prototype.on.call(destination, 'finish', release);
        events.EventEmitter.prototype.on.call(destination, 'unpipe', unpiped);
        try { return pipe.call(this, destination, options); }
        catch (error) { release(); throw error; }
      };
      emitter.unpipe = function(destination) {
        const result = unpipe.call(this, destination);
        if (destination) connections.get(destination)?.();
        else for (const release of [...connections.values()]) release();
        return result;
      };
    }
    return emitter;
  }

  ownResource(resource, bucket) {
    if (!resource || this.resourceCleanup.has(resource)) return resource;
    this.ownerScope().assertOpen();
    bucket.add(resource);
    const cleanups = this.resourceCleanup;
    const owner = new WeakRef(this);
    const reference = new WeakRef(resource);
    let detach, active = true, stopping = false;
    const descriptor = Object.getOwnPropertyDescriptor(resource, 'emit');
    const emit = resource.emit;
    function dispatch(event, ...args) {
      try { return Reflect.apply(emit, this, [event, ...args]); }
      finally { if (event === 'close' && this === reference.deref()) release(); }
    }
    const stop = () => {
      if (stopping) return;
      stopping = true;
      const value = reference.deref();
      if (value && !value.destroyed) value.destroy();
    };
    const release = () => {
      if (!active) return;
      active = false;
      let failure;
      const resource = reference.deref();
      try {
        if (resource?.emit === dispatch) {
          if (descriptor) Object.defineProperty(resource, 'emit', descriptor);
          else delete resource.emit;
        }
      } catch (error) { failure ??= error; }
      try { resource?.off('error', ignoreError); } catch (error) { failure ??= error; }
      try { detach?.(); } catch (error) { failure ??= error; }
      detach = null;
      if (resource) { bucket.delete(resource); cleanups.delete(resource); }
      if (failure) { const node = owner.deref(); if (node) node.failure ??= failure; }
    };
    release.stop = stop;
    cleanups.set(resource, release, release);
    try {
      resource.on('error', ignoreError);
      // Run retirement after all terminal listeners, even when script code
      // removes listeners. Only the WeakRef above points at the stream.
      Object.defineProperty(resource, 'emit', {
        configurable: true, writable: true, value: dispatch,
      });
      detach = this.ownerScope().add(() => {
        try { stop(); } catch (error) { this.failure ??= error; }
      });
      this.observeEmitter(resource);
      if (resource.closed) release();
    } catch (error) {
      try { stop(); } catch {}
      release();
      if (!resource.closed) guardDetachedEmitter(resource);
      throw error;
    }
    return resource;
  }
  ownNetwork(value) { return this.ownResource(value, this.networkResources); }
  ownStream(value) { return this.ownResource(value, this.streams); }

  ownCryptoStream(value, terminal) {
    this.ownStream(value);
    const finish = value[terminal], owner = this;
    value[terminal] = function(...input) {
      const result = finish.apply(this, input);
      owner.resourceCleanup.get(this)?.();
      return result;
    };
    if (typeof value.copy === 'function') {
      const copy = value.copy;
      value.copy = function(...input) {
        return owner.ownCryptoStream(copy.apply(this, input), terminal);
      };
    }
    return value;
  }

  ownBlob(value) {
    if (!value || this.blobObserved.has(value)) return value;
    this.blobObserved.add(value);
    const owner = new WeakRef(this), slice = value.slice, stream = value.stream;
    value.slice = function(...args) {
      const result = slice.apply(this, args);
      return owner.deref()?.ownBlob(result) ?? result;
    };
    value.stream = function(...args) {
      const result = stream.apply(this, args);
      return owner.deref()?.ownWeb(result, 'readableWeb') ?? result;
    };
    return value;
  }

  ownBody(value) {
    if (!value || this.bodyObserved.has(value)) return value;
    this.bodyObserved.add(value);
    if (value.body) this.ownWeb(value.body, 'readableWeb');
    const owner = new WeakRef(this), clone = value.clone;
    value.clone = function(...args) {
      const previous = this.body;
      const result = clone.apply(this, args), node = owner.deref();
      if (previous && previous !== this.body) node?.webResources.delete(previous);
      if (this.body) node?.ownWeb(this.body, 'readableWeb');
      return node?.ownBody(result) ?? result;
    };
    for (const name of ['arrayBuffer', 'bytes', 'text', 'json', 'blob', 'formData']) {
      if (typeof value[name] !== 'function') continue;
      const method = value[name];
      value[name] = function(...args) {
        const body = this.body;
        return method.apply(this, args).then(result => {
          const node = owner.deref();
          if (body) node?.forgetWeb(body);
          if (name === 'blob') return node?.ownBlob(result) ?? result;
          if (name === 'formData')
            for (const part of result.values()) if (part instanceof buffer.Blob) node?.ownBlob(part);
          return result;
        }, error => {
          if (body) owner.deref()?.forgetWeb(body);
          throw error;
        });
      };
    }
    return value;
  }

  forgetWeb(value) {
    // Native tee keeps its internal reader locked even after termination.
    // Only the branches' real cleanup may retire that reader's ownership.
    if (!this.webTees.has(value)) this.webResources.delete(value);
  }

  ownWebTee(source, branches) {
    this.webTees.set(source, branches.map(branch => {
      const state = { reference: new WeakRef(branch), pending: new Set() };
      this.webTeeBranches.set(branch, state);
      this.ownWeb(branch, 'readableWeb');
      return state;
    }));
    this.webResources.set(source, 'readableWeb');
  }

  trackTeeTask(value, task) {
    const branch = this.webTeeBranches.get(value);
    if (!branch) return;
    // closed/releaseLock and a repeated cancel can finish before the original
    // cancel does. Observe its settlement without replacing its public result;
    // cleanup calls still propagate their own errors through closeWeb.
    const pending = Promise.resolve(task).then(ignoreError, ignoreError);
    branch.pending.add(pending);
    pending.then(() => branch.pending.delete(pending));
  }

  ownWeb(value, role) {
    if (!value || this.webResources.has(value)) return value;
    this.ownerScope().assertOpen();
    this.webResources.set(value, role);
    if (this.webObserved.has(value)) return value;
    this.webObserved.add(value);
    const owner = new WeakRef(this);
    const reference = new WeakRef(value);
    const forget = () => {
      const value = reference.deref();
      if (value) owner.deref()?.forgetWeb(value);
    };
    if (['reader', 'writer'].includes(role)) {
      const release = value.releaseLock;
      value.releaseLock = function(...args) {
        const result = release.apply(this, args); forget(); return result;
      };
      // A locked but closed stream still needs its reader/writer released on stop.
      value.closed?.catch(() => {});
    }
    for (const [key, resultRole] of [['getReader', 'reader'], ['getWriter', 'writer']]) {
      if (typeof value[key] !== 'function') continue;
      const method = value[key];
      value[key] = function(...args) {
        const node = owner.deref();
        const result = method.apply(this, args);
        const branch = node?.webTeeBranches.get(this);
        if (branch) node.webTeeBranches.set(result, branch);
        result.closed.then(forget, forget);
        return node ? node.ownWeb(result, resultRole) : result;
      };
    }
    for (const key of ['cancel', 'abort', 'close', 'return']) if (typeof value[key] === 'function') {
      const method = value[key];
      value[key] = function(...args) {
        const result = method.apply(this, args);
        owner.deref()?.trackTeeTask(this, result);
        if (!['reader', 'writer'].includes(role)) Promise.resolve(result).then(forget, () => {});
        return result;
      };
    }
    if (typeof value.pipeTo === 'function') {
      value.pipeTo = function(destination, options) {
        try {
          readableLocked.call(this); writableLocked.call(destination);
          return owner.deref().pipeWeb(this, destination, webPipeOptions(options));
        } catch (error) { return Promise.reject(error); }
      };
      value.pipeThrough = function(transform, options) {
        readableLocked.call(this);
        const readable = transform?.readable;
        readableLocked.call(readable);
        const writable = transform?.writable;
        writableLocked.call(writable);
        const settings = webPipeOptions(options);
        if (readableLocked.call(this) || writableLocked.call(writable))
          throw Object.assign(new TypeError('The stream is locked'), { code: 'ERR_INVALID_STATE' });
        const node = owner.deref();
        node.pipeWeb(this, writable, settings).catch(() => {});
        return node.ownWeb(readable, 'readableWeb');
      };
    }
    for (const key of ['tee', 'values']) {
      if (typeof value[key] !== 'function') continue;
      const method = value[key];
      value[key] = function(...args) {
        const result = method.apply(this, args), node = owner.deref();
        if (key === 'tee') {
          node?.ownWebTee(this, result);
        } else {
          const branch = node?.webTeeBranches.get(this);
          if (branch) node.webTeeBranches.set(result, branch);
          node?.ownWeb(result, 'iterator');
          const next = result.next, finish = result.return;
          result.next = async (...args) => {
            const step = await next.apply(result, args);
            if (step.done) { forget(); node?.webResources.delete(result); }
            return step;
          };
          result.return = async (...args) => {
            try { return await finish.apply(result, args); }
            finally { forget(); node?.webResources.delete(result); }
          };
        }
        return result;
      };
    }
    if (role === 'readableWeb') value[Symbol.asyncIterator] = value.values;
    if (value.readable) this.ownWeb(value.readable, 'readableWeb');
    if (value.writable) this.ownWeb(value.writable, 'writableWeb');
    if (value.readable || value.writable) this.webResources.delete(value);
    return value;
  }

  pipeWeb(source, destination, options) {
    this.ownerScope().assertOpen();
    if (this.webPipeAbort.signal.aborted) throw this.webPipeAbort.signal.reason;
    const signal = AbortSignal.any([this.ownerScope().signal, this.webPipeAbort.signal,
      ...(options.signal === undefined ? [] : [options.signal])]);
    this.ownWeb(source, 'readableWeb');
    this.ownWeb(destination, 'writableWeb');
    const task = web.ReadableStream.prototype.pipeTo.call(source, destination, { ...options, signal });
    this.trackTeeTask(source, task);
    this.webPipes.set(task, signal);
    task.then(() => {
      this.webPipes.delete(task);
      this.forgetWeb(source);
      if (!options.preventClose) this.webResources.delete(destination);
    }, error => {
      this.webPipes.delete(task);
      if (this.webPipeAbort.signal.aborted && error !== signal.reason && !ignoredCleanupError(error))
        this.failure ??= error;
    });
    return task;
  }

  async closeWeb() {
    this.webPipeAbort.abort(fault('CANCELLED'));
    const locked = [...this.webResources].filter(([, role]) =>
      ['reader', 'writer', 'iterator'].includes(role));
    const closing = new Map();
    const close = (resource, role) => {
      if (closing.has(resource)) return closing.get(resource);
      const tee = this.webTees.get(resource);
      const task = tee ? Promise.allSettled(tee.map(async branch => {
        const value = branch.reference.deref();
        // Start every unlocked leaf before waiting: native branch cancellation
        // waits for its sibling. Recheck locked leaves after their tracked
        // reader/iterator/pipe tasks settle; an unowned lock still fails cleanup.
        const cleanup = value && (!readableLocked.call(value) || this.webTees.has(value))
          ? close(value, 'readableWeb') : null;
        const results = await Promise.allSettled([...branch.pending, ...(cleanup ? [cleanup] : [])]);
        const failed = results.find(result => result.status === 'rejected');
        if (failed) throw failed.reason;
        if (value && !cleanup) await close(value, 'readableWeb');
      })).then(results => {
        const failed = results.find(result => result.status === 'rejected');
        if (failed) throw failed.reason;
      }) : closeWebResource(role, resource);
      const result = task.then(() => {
        this.webResources.delete(resource);
        if (tee) this.webTees.delete(resource);
      });
      this.trackTeeTask(resource, result);
      closing.set(resource, result);
      return result;
    };
    const tasks = [...this.webPipes].map(([task, signal]) => task.catch(error => {
      if (error !== signal.reason && !ignoredCleanupError(error)) throw error;
    }));
    tasks.push(...locked.map(([resource, role]) => close(resource, role)));
    tasks.push(...[...this.webResources].filter(([resource]) => this.webTees.has(resource))
      .map(([resource, role]) => close(resource, role)));
    const results = await Promise.allSettled(tasks);
    const remaining = await Promise.allSettled([...this.webResources]
      .filter(([, role]) => !['reader', 'writer', 'iterator'].includes(role))
      .map(([resource, role]) => close(resource, role)));
    const failed = [...results, ...remaining].find(result => result.status === 'rejected');
    if (failed) throw failed.reason;
  }

  timerModule() {
    const active = this.session.timers;
    const schedule = (name, fn, delay, args) => {
      this.ownerScope().assertOpen();
      requireValue(typeof fn === 'function', 'INVALID_ARGUMENT');
      const session = this.session, scope = session.scope();
      let timer;
      const callback = () => {
        if (name !== 'setInterval') active.delete(timer);
        session.runCallback(fn, timer, args, scope);
      };
      timer = name === 'setImmediate' ? timers.setImmediate(callback) : timers[name](callback, delay);
      active.set(timer, name === 'setImmediate' ? timers.clearImmediate : timers.clearTimeout);
      return timer;
    };
    const clear = value => {
      const entry = active.has(value) ? value : [...active.keys()].find(timer => Number(timer) === value);
      if (!entry) return;
      active.get(entry)(entry); active.delete(entry);
    };
    return { setTimeout: (fn, delay, ...args) => schedule('setTimeout', fn, delay, args),
      setInterval: (fn, delay, ...args) => schedule('setInterval', fn, delay, args),
      setImmediate: (fn, ...args) => schedule('setImmediate', fn, 0, args),
      clearTimeout: clear, clearInterval: clear, clearImmediate: clear };
  }

  promiseTimers() {
    const wait = (name, delay, value, options = {}) => new Promise((resolve, reject) => {
      let timer;
      const finish = (callback, value) => {
        this.timers.clearTimeout(timer);
        options.signal?.removeEventListener('abort', abort);
        callback(value);
      };
      const abort = () => finish(reject, options.signal.reason ?? fault('CANCELLED'));
      timer = name === 'setImmediate' ? this.timers.setImmediate(() => finish(resolve, value))
        : this.timers.setTimeout(() => finish(resolve, value), delay);
      if (options.ref === false) timer.unref();
      if (options.signal?.aborted) abort();
      else options.signal?.addEventListener('abort', abort, { once: true });
    });
    return { setTimeout: (ms, value, options) => wait('setTimeout', ms, value, options),
      setImmediate: (value, options) => wait('setImmediate', 0, value, options) };
  }

  processModule() {
    const projected = new events.EventEmitter();
    Object.defineProperty(projected, Symbol.toStringTag, { value: 'process' });
    Object.assign(projected, { env: {}, argv: ['node', '/source.js'], execArgv: [],
      version: process.version, versions: { ...process.versions }, platform: process.platform,
      arch: process.arch, pid: process.pid, ppid: process.ppid, exitCode: 0, title: 'script',
      cwd: () => '/', execPath: '/node', getuid: () => process.getuid?.() ?? 0,
      chdir: () => { throw fault('PERMISSION_DENIED'); },
      exit: () => { throw fault('PERMISSION_DENIED'); },
      binding: () => { throw fault('PERMISSION_DENIED'); },
      memoryUsage: process.memoryUsage.bind(process), cpuUsage: process.cpuUsage.bind(process),
      uptime: process.uptime.bind(process), hrtime: process.hrtime.bind(process),
      nextTick: (fn, ...args) => {
        const session = this.session, scope = session.scope();
        process.nextTick(() => session.runCallback(fn, undefined, args, scope));
      },
      emitWarning: warning => projected.emit('warning', typeof warning === 'string' ? new Error(warning) : warning),
      _rawDebug: () => {},
    });
    projected.hrtime.bigint = process.hrtime.bigint.bind(process.hrtime);
    projected.memoryUsage.rss = process.memoryUsage.rss.bind(process.memoryUsage);
    projected.stdin = new stream.Readable({ read() { this.push(null); } });
    projected.stdout = new stream.Writable({ write(_chunk, _encoding, done) { done(); } });
    projected.stderr = new stream.Writable({ write(_chunk, _encoding, done) { done(); } });
    for (const [index, value] of [projected.stdin, projected.stdout, projected.stderr].entries()) {
      Object.assign(value, { fd: index, isTTY: false, ref() { return this; }, unref() { return this; } });
      this.ownStream(value);
    }
    return this.observeEmitter(projected);
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
      requireValue(!options.path && options.socketPath === undefined && options.fd === undefined && !options.socket &&
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
      if (typeof args.at(-1) === 'function') {
        const callback = args.pop(), session = this.session, scope = session.scope();
        args.push(function(...values) { return session.runCallback(callback, this, values, scope); });
      }
      const socket = native === tls ? tls.connect(clean, ...args) : net.connect(clean, ...args);
      this.ownNetwork(socket);
      if (mapped.meowLocal) this.services.trustSocket(socket);
      return socket;
    };
    const createServer = (...args) => {
      requireValue(native === net, 'UNSUPPORTED_DEPENDENCY');
      const server = this.services.probe(...args);
      return server;
    };
    const Server = function(...args) { return createServer(...args); };
    Object.defineProperty(Server, Symbol.hasInstance, {
      value: value => this.services.probes.has(value) || value?.owner === this.services,
    });
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
    if (!base) requireValue(sdkPackages.has(specifier), 'UNSUPPORTED_DEPENDENCY');
    else if (bare) {
      requireValue(base.packageName === packageName || Object.hasOwn(base.dependencies, packageName) ||
        sdkPackages.has(specifier), 'UNSUPPORTED_DEPENDENCY');
    }
    let file;
    try {
      file = require.resolve(specifier,
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

  close() {
    if (this.closing) return this.closing;
    this.closing = this.closeOwned();
    this.closing.catch(() => {});
    return this.closing;
  }
  async closeOwned() {
    let failure = this.failure;
    for (const [timer, clear] of this.session.timers) {
      try { clear(timer); } catch (error) { failure ??= error; }
    }
    this.session.timers.clear();
    for (const [name, callbacks] of this.subscriptions)
      for (const callback of callbacks) {
        try { diagnostics.unsubscribe(name, callback); } catch (error) { failure ??= error; }
      }
    this.subscriptions.clear();
    const tasks = [...this.resourceCleanup.keys()].map(resource => closeResource(resource, {
      stop: () => this.resourceCleanup.get(resource)?.stop(),
      release: () => this.resourceCleanup.get(resource)?.(),
      timeoutMs: this.session.limits.cleanupTimeoutMs, closed: value => value.closed,
    }).then(result => { if (result.error) throw result.error; }));
    tasks.push(this.services.close(), this.volume.close());
    tasks.push(this.closeWeb());
    let timer;
    const results = await Promise.race([Promise.allSettled(tasks), new Promise(resolve => {
      timer = setTimeout(() => resolve(null), this.session.limits.cleanupTimeoutMs);
    })]);
    clearTimeout(timer);
    if (!results) failure ??= fault('TIMEOUT');
    else for (const result of results) if (result.status === 'rejected') failure ??= result.reason;
    failure ??= this.failure;
    this.sources.clear();
    this.process.removeAllListeners();
    this.builtins = Object.create(null);
    if (failure) throw failure;
  }
}
