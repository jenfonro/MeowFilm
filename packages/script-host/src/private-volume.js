import * as fs from 'node:fs';
import { randomBytes } from 'node:crypto';
import { dirname, isAbsolute, join, relative, resolve, sep, posix } from 'node:path';
import { identifier, requireValue } from './errors.js';

const ignoreError = () => {};
const volumeOwner = Symbol('private-volume-owner');
const inside = (root, path) => {
  const delta = relative(root, path);
  return delta === '' || delta !== '..' && !delta.startsWith('..' + sep) && !isAbsolute(delta);
};
const nativeError = code => Object.assign(new Error(code), { code });
const mutatesOnOpen = (flags, existed) => typeof flags === 'string'
  ? flags.includes('w') || !existed && flags.includes('a')
  : Number.isInteger(flags) && Boolean(flags & fs.constants.O_TRUNC ||
    !existed && flags & fs.constants.O_CREAT);
function syncDirectory(path) {
  if (process.platform === 'win32') return;
  let fd;
  try { fd = fs.openSync(path, fs.constants.O_RDONLY); fs.fsyncSync(fd); }
  finally { if (fd !== undefined) fs.closeSync(fd); }
}
function syncMetadata(path) {
  if (fs.statSync(path).isFile()) {
    const fd = fs.openSync(path, fs.constants.O_RDONLY);
    try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
  }
  syncDirectory(dirname(path));
}
function closingStreams(streams) {
  let resolveAll;
  const aggregate = {
    remaining: streams.length,
    promise: new Promise(resolve => { resolveAll = resolve; }),
  };
  if (!aggregate.remaining) resolveAll();
  const waits = streams.map(stream => {
    const wait = { stream, promise: null, finish: null, error: null, settled: false };
    wait.promise = new Promise(resolve => {
      wait.finish = () => {
        if (wait.settled) return;
        wait.settled = true;
        try { stream.off('close', wait.finish); } catch (error) { wait.error ??= error; }
        resolve();
      };
      let closed = false;
      try { closed = Boolean(stream.closed); } catch (error) { wait.error ??= error; }
      if (closed) wait.finish();
      else {
        try { stream.once('close', wait.finish); }
        catch (error) { wait.error ??= error; wait.finish(); }
      }
    });
    wait.promise.then(() => {
      aggregate.remaining--;
      if (!aggregate.remaining) resolveAll();
    });
    return wait;
  });
  return { waits, promise: aggregate.promise };
}
async function waitForStreams(closing, timeoutMs) {
  if (!closing.waits.length) return true;
  let timer;
  const complete = await Promise.race([
    closing.promise.then(() => true),
    new Promise(resolve => {
      timer = setTimeout(() => resolve(false), timeoutMs);
    }),
  ]);
  clearTimeout(timer);
  return complete;
}
function guardDetachedStream(stream) {
  let failure, guardAttached = false, closeAttached = false;
  const guard = () => {};
  const closed = () => {
    if (closeAttached) {
      try { stream.off?.('close', closed); } catch {}
      closeAttached = false;
    }
    if (guardAttached) {
      try { stream.off?.('error', guard); } catch {}
      guardAttached = false;
    }
  };
  try {
    stream.on?.('error', guard);
    guardAttached = true;
  } catch (error) { failure ??= error; }
  try {
    stream.once?.('close', closed);
    closeAttached = true;
  } catch (error) { failure ??= error; }
  try { if (stream.closed) closed(); } catch (error) { failure ??= error; }
  return failure;
}

/** Script paths are virtual POSIX paths. No host path, descriptor, link or
 * loader can be selected by guest code. The installation owns this volume,
 * independently of its source revision, generation and listening port. */
export class PrivateVolume {
  constructor(dataRoot, installationId, onWrite, source, currentScope, cleanupTimeoutMs = 3000) {
    requireValue(typeof dataRoot === 'string' && isAbsolute(dataRoot));
    identifier(installationId);
    this.root = resolve(dataRoot, 'volumes', installationId);
    for (const parent of [resolve(dataRoot), resolve(dataRoot, 'volumes'), this.root]) {
      if (fs.existsSync(parent)) requireValue(!fs.lstatSync(parent).isSymbolicLink(), 'PERMISSION_DENIED');
      else fs.mkdirSync(parent);
    }
    fs.mkdirSync(this.root, { recursive: true });
    this.root = fs.realpathSync(this.root);
    this.identity = fs.statSync(this.root);
    this.descriptors = new Map();
    this.streams = new Set();
    this.streamCleanup = new Map();
    this.nextDescriptor = 100;
    this.closed = false;
    this.onWrite = onWrite;
    this.currentScope = currentScope;
    this.failure = null;
    this.source = Buffer.from(source ?? '');
    this.roles = new WeakMap();
    this.cleanupTimeoutMs = cleanupTimeoutMs;
    fs.mkdirSync(join(this.root, 'tmp'), { recursive: true });
  }

  assertOpen() {
    requireValue(!this.closed, 'STALE_SESSION');
    const now = fs.lstatSync(this.root);
    requireValue(!now.isSymbolicLink() && now.dev === this.identity.dev &&
      now.ino === this.identity.ino, 'PERMISSION_DENIED');
  }

  path(input, { missing = false } = {}) {
    this.assertOpen();
    if (input instanceof URL) {
      if (input.protocol !== 'file:' || input.host) throw nativeError('EPERM');
      input = decodeURIComponent(input.pathname);
    }
    if (typeof input !== 'string' || !input || /[\u0000\\:]/.test(input)) throw nativeError('EPERM');
    const virtual = posix.resolve('/', input);
    const path = resolve(this.root, '.' + virtual);
    if (!inside(this.root, path)) throw nativeError('EPERM');
    let current = this.root;
    for (const part of relative(this.root, path).split(sep).filter(Boolean)) {
      // Reject Windows aliases, alternate data streams and device names.
      if (/[. ]$|[<>|"*?\u0000-\u001f]/.test(part) ||
        /^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)/i.test(part)) throw nativeError('EPERM');
      current = join(current, part);
      let stat;
      try { stat = fs.lstatSync(current); }
      catch (error) { if (missing && error.code === 'ENOENT') continue; throw error; }
      if (stat.isSymbolicLink() || stat.isFile() && stat.nlink !== 1) throw nativeError('EPERM');
      if (!inside(this.root, fs.realpathSync(current))) throw nativeError('EPERM');
    }
    return path;
  }

  fd(id) {
    this.assertOpen();
    if (!Number.isSafeInteger(id) || !this.descriptors.has(id)) throw nativeError('EBADF');
    return this.descriptors.get(id).fd;
  }

  descriptor(id) {
    this.assertOpen();
    if (!Number.isSafeInteger(id) || !this.descriptors.has(id)) throw nativeError('EBADF');
    return this.descriptors.get(id);
  }

  ownerScope() {
    try { return this.currentScope?.() ?? null; }
    catch { return null; }
  }

  written(scope = this.ownerScope()) {
    this.onWrite?.(scope);
  }

  closeDescriptor(id, report = true) {
    const descriptor = this.descriptors.get(id);
    if (!descriptor) {
      if (report) throw nativeError('EBADF');
      return;
    }
    let failure;
    try { descriptor.detach?.(); } catch (error) { failure = error; }
    descriptor.detach = null;
    const validFd = Number.isInteger(descriptor.fd) && descriptor.fd >= 0;
    if (!validFd) failure ??= nativeError('EBADF');
    try {
      if (validFd && descriptor.dirty) {
        fs.fsyncSync(descriptor.fd);
        descriptor.dirty = false;
        this.written(descriptor.scope);
      }
    } catch (error) { failure ??= error; }
    descriptor.scope = null;
    let closed = false;
    if (validFd) {
      try { fs.closeSync(descriptor.fd); closed = true; }
      catch (error) { failure ??= error; }
    }
    if (closed) this.descriptors.delete(id);
    if (failure) {
      this.failure ??= failure;
      if (report) throw failure;
    }
  }

  facade() {
    const api = {};
    const isSource = input => typeof input === 'string' && posix.resolve('/', input) === '/source.js';
    const ownStream = (stream, scope, persisted) => {
      const streams = this.streams;
      const cleanups = this.streamCleanup;
      streams.add(stream);
      // Guest listeners still receive the error; this internal listener only
      // prevents an unhandled native EventEmitter error from escaping the sandbox.
      const state = {
        active: true, owner: this, detach: null, persisted,
        closeAttached: false, persistedAttached: false, errorAttached: false,
      };
      const persistedListener = () => state.persisted?.();
      const cleanup = (forced = false) => {
        if (!state.active) return;
        const owner = state.owner;
        let failure;
        for (let attempt = 0; attempt < 2; attempt++) {
          if (state.closeAttached) {
            try { stream.off('close', closed); state.closeAttached = false; }
            catch (error) { failure ??= error; }
          }
          if (state.persistedAttached) {
            try { stream.off('close', persistedListener); state.persistedAttached = false; }
            catch (error) { failure ??= error; }
          }
          if (state.errorAttached) {
            try { stream.off('error', ignoreError); state.errorAttached = false; }
            catch (error) { failure ??= error; }
          }
        }
        const detach = state.detach;
        state.detach = null;
        try { detach?.(); } catch (error) { failure ??= error; }
        streams.delete(stream);
        cleanups.delete(stream);
        try { delete stream[volumeOwner]; } catch (error) {
          failure ??= error;
          try { stream[volumeOwner] = null; } catch (next) { failure ??= next; }
        }
        let closedNow = false;
        try { closedNow = Boolean(stream.closed); } catch (error) { failure ??= error; }
        if (forced && !closedNow) {
          const guardFailure = guardDetachedStream(stream);
          failure ??= guardFailure;
        }
        state.persisted = null;
        state.owner = null;
        state.active = false;
        if (failure && owner) owner.failure ??= failure;
      };
      const closed = () => cleanup();
      cleanups.set(stream, cleanup);
      try {
        stream.once('close', closed);
        state.closeAttached = true;
        stream.on('error', ignoreError);
        state.errorAttached = true;
        if (persisted) {
          stream.prependListener('close', persistedListener);
          state.persistedAttached = true;
        }
        Object.defineProperty(stream, volumeOwner, {
          configurable: true, value: this,
        });
        if (scope?.add) {
          state.detach = scope.add(() => state.owner?.release(stream));
          scope.assertOpen?.();
        }
        if (stream.closed) cleanup();
      } catch (error) {
        this.failure ??= error;
        try { if (!stream.destroyed) stream.destroy(); }
        catch (failure) { this.failure ??= failure; }
        let closedNow = false;
        try { closedNow = Boolean(stream.closed); } catch (failure) { this.failure ??= failure; }
        try { cleanup(!closedNow); } catch (failure) { this.failure ??= failure; }
        throw error;
      }
      return stream;
    };
    const acknowledge = descriptor => {
      if (!descriptor.dirty) return;
      descriptor.dirty = false;
      this.written(descriptor.scope);
    };
    const withPath = (name, position = 0, missing = false) => (...args) => {
      if (isSource(args[position])) {
        if (name === 'accessSync') return;
        if (['statSync', 'lstatSync'].includes(name)) {
          const value = fs.statSync(this.root);
          value.size = this.source.length;
          value.isFile = () => true;
          value.isDirectory = () => false;
          return value;
        }
        throw nativeError('EPERM');
      }
      args[position] = this.path(args[position], { missing });
      return fs[name](...args);
    };
    for (const name of ['statSync', 'lstatSync', 'readdirSync', 'accessSync'])
      api[name] = withPath(name);
    api.readFileSync = (input, options) => {
      const encoding = typeof options === 'string' ? options : options?.encoding;
      if (isSource(input)) return encoding ? this.source.toString(encoding) : Buffer.from(this.source);
      return fs.readFileSync(typeof input === 'number' ? this.fd(input) : this.path(input), options);
    };
    api.existsSync = input => {
      try { return isSource(input) || fs.existsSync(this.path(input, { missing: true })); } catch { return false; }
    };
    api.realpathSync = input => {
      if (isSource(input)) return '/source.js';
      const target = fs.realpathSync(this.path(input));
      return '/' + relative(this.root, target).split(sep).join('/');
    };
    api.realpathSync.native = api.realpathSync;
    api.writeFileSync = (input, data, options) => {
      if (isSource(input)) throw nativeError('EPERM');
      const descriptor = typeof input === 'number' ? this.descriptor(input) : null;
      const target = descriptor?.fd ?? this.path(input, { missing: true });
      const existed = Boolean(descriptor) || fs.existsSync(target);
      const settings = typeof options === 'string' ? { encoding: options } : { ...options };
      fs.writeFileSync(target, data, { ...settings, flush: true });
      if (descriptor) { descriptor.dirty = true; acknowledge(descriptor); }
      else {
        if (!existed) syncDirectory(dirname(target));
        this.written();
      }
    };
    api.appendFileSync = (input, data, options) =>
      api.writeFileSync(input, data, { ...(typeof options === 'string' ? { encoding: options } : options), flag: 'a' });
    api.mkdirSync = (input, options) => {
      if (isSource(input)) throw nativeError('EPERM');
      const path = this.path(input, { missing: true });
      const existed = fs.existsSync(path);
      const created = fs.mkdirSync(path, options);
      if (!existed) {
        syncDirectory(dirname(created ?? path));
        this.written();
      }
      return options?.recursive && created !== undefined
        ? '/' + relative(this.root, created).split(sep).join('/') : undefined;
    };
    for (const name of ['unlinkSync', 'rmdirSync', 'rmSync']) api[name] = (input, options) => {
      if (isSource(input)) throw nativeError('EPERM');
      const path = this.path(input, { missing: name === 'rmSync' && options?.force });
      if (path === this.root) throw nativeError('EPERM');
      // Do not let a recursive operation follow an existing guest-created link.
      const audit = directory => {
        for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
          const target = join(directory, entry.name), stat = fs.lstatSync(target);
          if (stat.isSymbolicLink() || stat.isFile() && stat.nlink !== 1) throw nativeError('EPERM');
          if (stat.isDirectory()) audit(target);
        }
      };
      if (options?.recursive && fs.existsSync(path) && fs.lstatSync(path).isDirectory()) audit(path);
      const existed = fs.existsSync(path);
      const result = fs[name](path, options);
      if (existed) {
        syncDirectory(dirname(path));
        this.written();
      }
      return result;
    };
    for (const name of ['renameSync', 'copyFileSync']) api[name] = (from, to, ...args) => {
      if (isSource(from) || isSource(to)) throw nativeError('EPERM');
      const source = this.path(from), target = this.path(to, { missing: true });
      if (source === this.root || target === this.root) throw nativeError('EPERM');
      fs[name](source, target, ...args);
      if (fs.statSync(target).isFile()) {
        const fd = fs.openSync(target, 'r');
        try { fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
      }
      syncDirectory(dirname(source));
      if (dirname(target) !== dirname(source)) syncDirectory(dirname(target));
      this.written();
    };
    api.openSync = (input, flags = 'r', mode) => {
      if (isSource(input)) throw nativeError('EPERM');
      const scope = this.ownerScope();
      scope?.assertOpen?.();
      const target = this.path(input, { missing: true });
      const existed = fs.existsSync(target);
      let fd, id, registered = false;
      try {
        fd = fs.openSync(target, flags, mode);
        const stat = fs.fstatSync(fd);
        if (!stat.isFile() || stat.nlink !== 1) throw nativeError('EPERM');
        id = ++this.nextDescriptor;
        const descriptor = {
          fd, dirty: mutatesOnOpen(flags, existed), scope, detach: null,
        };
        this.descriptors.set(id, descriptor);
        registered = true;
        if (scope?.add)
          descriptor.detach = scope.add(() => this.closeDescriptor(id, false));
        return id;
      } catch (error) {
        if (registered) this.closeDescriptor(id, false);
        else if (fd !== undefined) {
          try { fs.closeSync(fd); } catch (failure) { this.failure ??= failure; }
        }
        throw error;
      }
    };
    api.closeSync = id => {
      this.descriptor(id);
      this.closeDescriptor(id);
    };
    api.fstatSync = (id, options) => fs.fstatSync(this.fd(id), options);
    api.fsyncSync = id => {
      const descriptor = this.descriptor(id), result = fs.fsyncSync(descriptor.fd);
      acknowledge(descriptor);
      return result;
    };
    api.fdatasyncSync = id => {
      const descriptor = this.descriptor(id), result = fs.fdatasyncSync(descriptor.fd);
      acknowledge(descriptor);
      return result;
    };
    api.ftruncateSync = (id, size) => {
      const descriptor = this.descriptor(id);
      fs.ftruncateSync(descriptor.fd, size); descriptor.dirty = true;
      fs.fsyncSync(descriptor.fd); acknowledge(descriptor);
    };
    api.readSync = (id, buffer, ...args) => fs.readSync(this.fd(id), buffer, ...args);
    api.writeSync = (id, buffer, ...args) => {
      const descriptor = this.descriptor(id), fd = descriptor.fd;
      const result = fs.writeSync(fd, buffer, ...args);
      descriptor.dirty = true;
      fs.fsyncSync(fd); acknowledge(descriptor); return result;
    };
    api.truncateSync = (input, size) => {
      const id = api.openSync(input, 'r+');
      try { api.ftruncateSync(id, size); } finally { api.closeSync(id); }
    };
    for (const name of ['chmodSync', 'utimesSync']) api[name] = (input, ...args) => {
      if (isSource(input)) throw nativeError('EPERM');
      const path = this.path(input);
      const result = fs[name](path, ...args);
      syncMetadata(path);
      this.written();
      return result;
    };
    for (const name of ['createReadStream', 'createWriteStream']) api[name] = (input, options = {}) => {
      const settings = typeof options === 'string' ? { encoding: options } : { ...options };
      if (isSource(input)) {
        if (name !== 'createReadStream') throw nativeError('EPERM');
        // A source mount has no host filename or descriptor.
        return ownStream(new SourceReadable(this.source, settings), this.ownerScope());
      }
      const path = this.path(input, { missing: name === 'createWriteStream' });
      if (settings.fd !== undefined || settings.fs !== undefined) throw nativeError('EPERM');
      const existed = fs.existsSync(path);
      const dirtyOnOpen = name === 'createWriteStream' &&
        mutatesOnOpen(settings.flags ?? 'w', existed);
      const scope = this.ownerScope();
      const stream = fs[name](path, { ...settings,
        autoClose: true, emitClose: true,
        ...(name === 'createWriteStream' ? { flush: true } : {}) });
      // The native path and fs implementation are never public capability fields.
      const persisted = () => {
        try {
          if (stream.errored || !dirtyOnOpen && stream.bytesWritten === 0) return;
          if (!existed) syncDirectory(dirname(path));
          this.written(scope);
        } catch (error) { this.failure ??= error; }
      };
      return ownStream(stream, scope, name === 'createWriteStream' ? persisted : null);
    };
    api.constants = Object.freeze({ ...fs.constants });
    const promises = {};
    for (const name of Object.keys(api).filter(name => name.endsWith('Sync'))) {
      const operation = name.slice(0, -4);
      promises[operation] = async (...args) => api[name](...args);
      api[operation] = (...args) => {
        const callback = args.pop();
        if (typeof callback !== 'function') throw nativeError('EINVAL');
        let result, error;
        try { result = api[name](...args); } catch (failure) { error = failure; }
        queueMicrotask(() => callback(error ?? null, result));
      };
    }
    api.exists = (input, callback) => {
      if (typeof callback !== 'function') throw nativeError('EINVAL');
      const result = api.existsSync(input);
      queueMicrotask(() => callback(result));
    };
    api.realpath.native = api.realpath;
    api.read = (id, buffer, ...args) => {
      const callback = args.pop();
      if (typeof callback !== 'function') throw nativeError('EINVAL');
      let count, error;
      try { count = api.readSync(id, buffer, ...args); } catch (failure) { error = failure; }
      queueMicrotask(() => callback(error ?? null, count, buffer));
    };
    api.write = (id, buffer, ...args) => {
      const callback = args.pop();
      if (typeof callback !== 'function') throw nativeError('EINVAL');
      let count, error;
      try { count = api.writeSync(id, buffer, ...args); } catch (failure) { error = failure; }
      queueMicrotask(() => callback(error ?? null, count, buffer));
    };
    promises.open = async (...args) => {
      const id = api.openSync(...args);
      const handle = {
        fd: id, close: async () => api.closeSync(id),
        readFile: async options => fs.readFileSync(this.fd(id), options),
        writeFile: async (data, options) => api.writeFileSync(id, data, options),
        stat: async options => api.fstatSync(id, options),
        sync: async () => api.fsyncSync(id), datasync: async () => api.fdatasyncSync(id),
        truncate: async size => api.ftruncateSync(id, size),
        appendFile: async (data, options) => api.appendFileSync(id, data, options),
        read: async (buffer, ...args) => ({ bytesRead: api.readSync(id, buffer, ...args), buffer }),
        write: async (buffer, ...args) => ({ bytesWritten: api.writeSync(id, buffer, ...args), buffer }),
      };
      this.roles.set(handle, 'fileHandle');
      return handle;
    };
    api.promises = promises;
    return api;
  }

  release(value) {
    if (!value || this.closed) return false;
    if (this.roles.get(value) === 'fileHandle') {
      const id = value.fd;
      if (Number.isSafeInteger(id) && this.descriptors.has(id))
        Promise.resolve(value.close()).catch(() => {});
      return true;
    }
    if (!this.streams.has(value)) return false;
    if (!value.closed && !value.destroyed) {
      try {
        if (value.writable) {
          if (!value.writableEnded) value.end();
        } else value.destroy();
      } catch (error) {
        this.failure ??= error;
        try { if (!value.destroyed) value.destroy(); }
        catch (failure) { this.failure ??= failure; }
      }
    }
    return true;
  }

  async close() {
    if (this.closing) return this.closing;
    this.closed = true;
    this.closing = (async () => {
      const pending = [];
      for (const stream of this.streams) {
        let closed = false;
        try { closed = Boolean(stream.closed); } catch (error) { this.failure ??= error; }
        if (!closed) pending.push(stream);
      }
      const closing = closingStreams(pending);
      for (const stream of pending) {
        try {
          if (stream.writable) {
            if (!stream.writableEnded && !stream.destroyed) stream.end();
          } else if (!stream.destroyed) stream.destroy();
        } catch (error) {
          this.failure ??= error;
          try { if (!stream.destroyed) stream.destroy(); }
          catch (failure) { this.failure ??= failure; }
        }
      }
      let complete = false;
      try {
        complete = await waitForStreams(closing, this.cleanupTimeoutMs);
      }
      catch (error) { this.failure ??= error; }
      for (const wait of closing.waits) this.failure ??= wait.error;
      if (!complete) {
        this.failure ??= nativeError('ETIMEDOUT');
        for (const stream of pending) {
          try {
            if (!stream.closed && !stream.destroyed) stream.destroy(this.failure);
          } catch (error) { this.failure ??= error; }
        }
        try {
          complete = await waitForStreams(closing, this.cleanupTimeoutMs);
        } catch (error) { this.failure ??= error; }
        for (const wait of closing.waits) this.failure ??= wait.error;
      }
      for (const stream of new Set([...pending, ...this.streams])) {
        let closed = false;
        try { closed = Boolean(stream.closed); } catch (error) { this.failure ??= error; }
        try { this.streamCleanup.get(stream)?.(!closed); }
        catch (error) { this.failure ??= error; }
      }
      this.streams.clear();
      this.streamCleanup.clear();
      for (const id of [...this.descriptors.keys()]) {
        try { this.closeDescriptor(id, false); } catch (error) { this.failure ??= error; }
      }
      for (const id of [...this.descriptors.keys()]) {
        try { this.closeDescriptor(id, false); } catch (error) { this.failure ??= error; }
      }
      try { this.source.fill(0); } catch (error) { this.failure ??= error; }
      this.source = Buffer.alloc(0);
      this.onWrite = null;
      this.currentScope = null;
      this.roles = new WeakMap();
      if (this.failure) throw this.failure;
    })();
    return this.closing;
  }
}

import { Readable } from 'node:stream';
class SourceReadable extends Readable {
  constructor(bytes, options) {
    super({ encoding: options.encoding });
    this.bytes = bytes.subarray(options.start ?? 0, options.end === undefined ? bytes.length : options.end + 1);
    this.offset = 0;
  }
  _read(size) {
    if (this.offset >= this.bytes.length) { this.push(null); return; }
    const next = this.bytes.subarray(this.offset, this.offset + size);
    this.offset += next.length; this.push(Buffer.from(next));
  }
}

export function atomicWrite(path, bytes) {
  const parent = dirname(path);
  fs.mkdirSync(parent, { recursive: true });
  const directory = fs.lstatSync(parent);
  requireValue(directory.isDirectory() && !directory.isSymbolicLink(), 'PERMISSION_DENIED');
  if (fs.existsSync(path)) {
    const target = fs.lstatSync(path);
    requireValue(target.isFile() && !target.isSymbolicLink() && target.nlink === 1, 'PERMISSION_DENIED');
  }
  const temporary = join(parent, `.${randomBytes(16).toString('hex')}.tmp`);
  let fd, failure;
  try {
    fd = fs.openSync(temporary, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL, 0o600);
    fs.writeFileSync(fd, bytes);
    fs.fsyncSync(fd);
    fs.closeSync(fd); fd = undefined;
    fs.renameSync(temporary, path);
    const saved = fs.lstatSync(path);
    requireValue(saved.isFile() && !saved.isSymbolicLink() && saved.nlink === 1, 'STORAGE_ERROR');
    syncDirectory(parent);
  } catch (error) {
    failure = error;
  } finally {
    if (fd !== undefined) {
      try { fs.closeSync(fd); } catch (error) { failure ??= error; }
    }
    try { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
    catch (error) { failure ??= error; }
  }
  if (failure) throw failure;
}
