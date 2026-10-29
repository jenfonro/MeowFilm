import vm from 'node:vm';
import { AsyncLocalStorage } from 'node:async_hooks';
import { ResourceScope } from './scope.js';
import { clone, deferred, fault, markForeignError, requireValue, safeError } from './errors.js';
import { lxCapabilities, lxPayload } from './lx-capabilities.js';
import { realmSource } from './realm.js';
import { createUtilities } from './utilities.js';

const bootstrap = new vm.Script(realmSource, { filename: 'meowfilm-lx-sdk.js' });
const trampoline = new vm.Script('__meowInvocation.fn(...__meowInvocation.args)', {
  filename: 'meowfilm-lx-dispatch.js',
});

export class LXSession {
  constructor({ installationId, generation, revision, network, storage, limits }) {
    Object.assign(this, { installationId, generation, revision, network, storage, limits });
    this.live = true;
    this.activated = false;
    this.ready = deferred();
    this.root = new ResourceScope();
    this.contextScope = new AsyncLocalStorage();
    this.invocations = new Set();
    this.timers = new Map();
    this.networks = new Map();
    this.adapterTasks = new Set();
    this.nextId = 0;
    this.capabilities = {};
    this.alert = null;
    this.utils = createUtilities(limits.maxBytes);
    this.context = vm.createContext({
      __meowBridge: (operation, args) => this.bridge(operation, args),
    }, { name: `meowfilm:${installationId}:${generation}` });
    this.realm = bootstrap.runInContext(this.context, { timeout: limits.syncTimeoutMs });
  }

  scope() { return this.contextScope.getStore() ?? this.root; }
  assertLive() {
    if (!this.live) throw fault('STALE_SESSION');
    this.scope().assertOpen();
  }

  async start() {
    const timeout = setTimeout(() => this.fail(fault('TIMEOUT')), this.limits.initTimeoutMs);
    try {
      const script = new vm.Script(this.revision.source, { filename: 'installed-lx-script.js' });
      const result = this.contextScope.run(this.root, () =>
        script.runInContext(this.context, { timeout: this.limits.syncTimeoutMs }));
      // Observe a returned startup Promise without requiring scripts to return one.
      if (result && typeof result.then === 'function') {
        Promise.resolve(result).catch((e) => this.fail(safeError(e)));
      }
      await this.ready.promise;
      this.assertLive();
      return this;
    } catch (e) {
      const error = e?.code === 'ERR_SCRIPT_EXECUTION_TIMEOUT' ? fault('TIMEOUT') : safeError(e);
      this.fail(error);
      throw error;
    } finally { clearTimeout(timeout); }
  }

  activate() { this.assertLive(); this.activated = true; }

  call(fn, args, scope) {
    if (!this.live) throw fault('STALE_SESSION');
    scope.assertOpen();
    // Temporary trampoline bounds synchronous callbacks, not asynchronous CPU.
    const context = this.context;
    const previous = context.__meowInvocation;
    context.__meowInvocation = { fn, args };
    try {
      return this.contextScope.run(scope, () =>
        trampoline.runInContext(context, { timeout: this.limits.syncTimeoutMs }));
    } catch (e) {
      if (e?.code === 'ERR_SCRIPT_EXECUTION_TIMEOUT') throw fault('TIMEOUT');
      throw e;
    } finally {
      if (previous) context.__meowInvocation = previous;
      else delete context.__meowInvocation;
    }
  }

  callback(fn, args, scope) {
    if (!this.live || scope.signal.aborted) return;
    try {
      const value = this.call(fn, args, scope);
      if (value && typeof value.then === 'function') {
        Promise.resolve(value).catch((e) => this.callbackFailed(e, scope));
      }
    } catch (e) { this.callbackFailed(e, scope); }
  }

  callbackFailed(error, scope) {
    if (scope.signal.aborted || !this.live) return;
    if (scope === this.root) this.fail(safeError(error));
    else scope.close(safeError(error));
  }

  bridge(operation, args) {
    if (operation === 'rememberError') return markForeignError(...args);
    // Clearing/aborting an already completed handle is always idempotent.
    if (operation === 'abortRequest') {
      const item = this.networks.get(args[0]);
      if (item?.owner === this.scope()) item.child.close(fault('CANCELLED'));
      return;
    }
    if (operation === 'clearTimer') return this.clearTimer(args[0], this.scope());
    this.assertLive();
    switch (operation) {
      case 'maxBytes': return this.limits.maxBytes;
      case 'config': return JSON.stringify(this.revision.config);
      case 'scriptInfo': return JSON.stringify(this.scriptInfo());
      case 'util': return this.utils(...args);
      case 'on':
        requireValue(args[0] === 'request' && typeof args[1] === 'function', 'SCRIPT_PROTOCOL_ERROR');
        this.handler = args[1];
        return;
      case 'send': return this.send(...args);
      case 'request': return this.request(...args);
      case 'storage': return this.storageCall(...args);
      case 'timer': return this.timer(...args);
      case 'microtask': {
        requireValue(typeof args[0] === 'function');
        const scope = this.scope();
        queueMicrotask(() => this.callback(args[0], [], scope));
        return;
      }
      default: throw fault('UNSUPPORTED_OPTION');
    }
  }

  scriptInfo() {
    const metadata = { name: 'LX script', version: this.revision.version };
    const prefix = this.revision.source.slice(0, 8192);
    const header = prefix.match(/^\s*(\/\*[\s\S]*?\*\/|(?:\/\/[^\n]*\n?)+)/)?.[0] ?? '';
    for (const field of ['name', 'description', 'author', 'homepage', 'version']) {
      const value = header.match(new RegExp(`(?:^|\\n)\\s*(?:\\/\\/|\\/\\*|\\*)?\\s*@${field}\\s+([^\\r\\n]*)`))?.[1];
      if (value) metadata[field] = value.replace(/\*\/\s*$/, '').trim().slice(0, 1024);
    }
    return { ...metadata, ...this.revision.metadata, rawScript: this.revision.source };
  }

  trackAdapter(promise) {
    this.adapterTasks.add(promise);
    promise.finally(() => this.adapterTasks.delete(promise)).catch(() => {});
    return promise;
  }

  whenAdaptersIdle() { return Promise.allSettled([...this.adapterTasks]); }

  async drainAdapters() {
    if (!this.adapterTasks.size) return;
    let timer;
    try {
      await Promise.race([
        this.whenAdaptersIdle(),
        new Promise(resolve => { timer = setTimeout(resolve, this.limits.cleanupTimeoutMs); }),
      ]);
    } finally { clearTimeout(timer); }
  }

  send(event, data) {
    try {
      if (event === 'inited') {
        requireValue(!this.initialized && this.handler, 'SCRIPT_PROTOCOL_ERROR');
        this.capabilities = lxCapabilities(data);
        this.initialized = true;
        this.ready.resolve();
      } else if (event === 'updateAlert') {
        requireValue(!this.alert && data && typeof data.log === 'string' &&
          data.log.length <= 1024 && (data.updateUrl === undefined ||
          (typeof data.updateUrl === 'string' && data.updateUrl.length <= 1024)),
        'SCRIPT_PROTOCOL_ERROR');
        if (data.updateUrl) {
          const url = new URL(data.updateUrl);
          requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password,
            'SCRIPT_PROTOCOL_ERROR');
        }
        this.alert = { log: data.log, updateUrl: data.updateUrl ?? '' };
      } else throw fault('SCRIPT_PROTOCOL_ERROR');
    } catch {
      const error = fault('SCRIPT_PROTOCOL_ERROR');
      this.fail(error);
      throw error;
    }
  }

  timer(fn, delay = 0, repeat, args) {
    requireValue(typeof fn === 'function');
    requireValue(this.timers.size < this.limits.maxTimers, 'RESOURCE_LIMIT');
    const scope = this.scope();
    const id = ++this.nextId;
    const run = () => {
      if (!repeat) this.clearTimer(id, scope);
      this.callback(fn, args, scope);
    };
    const ms = Math.max(1, Math.min(Number(delay) || 0, 2 ** 31 - 1));
    const handle = repeat ? setInterval(run, ms) : setTimeout(run, ms);
    const detach = scope.add(() => this.clearTimer(id, scope));
    this.timers.set(id, { handle, scope, detach });
    return id;
  }

  clearTimer(id, scope) {
    const timer = this.timers.get(id);
    if (!timer || timer.scope !== scope) return;
    clearTimeout(timer.handle);
    clearInterval(timer.handle);
    timer.detach();
    this.timers.delete(id);
  }

  authorizeURL(input, scope) {
    if (!this.live) throw fault('STALE_SESSION');
    scope.assertOpen();
    let url;
    try { url = new URL(input); } catch { throw fault('PERMISSION_DENIED'); }
    requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password,
      'PERMISSION_DENIED');
    requireValue(this.revision.permissions.networkOrigins.includes(url.origin), 'PERMISSION_DENIED');
    return url.href;
  }

  request(url, options, fn) {
    requireValue(typeof fn === 'function');
    requireValue(this.networks.size < this.limits.maxNetworkRequests, 'RESOURCE_LIMIT');
    const owner = this.scope();
    const id = ++this.nextId;
    const copied = clone(options);
    requireValue(copied && typeof copied === 'object' && !Array.isArray(copied));
    if (copied.timeout !== undefined) {
      requireValue(Number.isFinite(copied.timeout) && copied.timeout > 0);
    }
    const timeout = Math.min(copied.timeout ?? this.limits.requestTimeoutMs, this.limits.requestTimeoutMs);
    const child = new ResourceScope(owner.signal, timeout);
    const record = { owner, child };
    this.networks.set(id, record);
    let settled = false;
    const complete = (error, response) => {
      if (settled) return;
      settled = true;
      this.networks.delete(id);
      child.close();
      if (!this.live || owner.signal.aborted) return;
      let callbackArgs;
      try {
        let raw, meta;
        if (!error) {
          requireValue(response && Number.isInteger(response.statusCode), 'NETWORK_ERROR');
          raw = typeof response.body === 'string' ? Buffer.from(response.body) : Buffer.from(response.body ?? []);
          requireValue(raw.length <= this.limits.maxBytes, 'RESOURCE_LIMIT');
          meta = JSON.stringify({
            statusCode: response.statusCode, statusMessage: response.statusMessage ?? '',
            headers: response.headers ?? {}, url: response.url ?? url,
          });
        }
        callbackArgs = this.call(this.realm.httpArguments,
          [error?.code ?? null, meta, raw ? [...raw] : [], copied.encoding, copied.json === true], owner);
      } catch (e) {
        callbackArgs = this.call(this.realm.httpArguments,
          [safeError(e, 'NETWORK_ERROR').code, null, [], null, false], owner);
      }
      this.callback(fn, callbackArgs, owner);
    };
    child.signal.addEventListener('abort', () => complete(child.reason), { once: true });
    this.trackAdapter(Promise.resolve().then(() => {
      const authorized = this.authorizeURL(url, child);
      requireValue(typeof this.network === 'function', 'PERMISSION_DENIED');
      return this.network({
        url: authorized, options: copied, signal: child.signal,
        scope: this.scopeIdentity(owner),
        authorizeURL: (next) => this.authorizeURL(next, child),
      });
    })).then(response => complete(null, response),
      e => complete(safeError(e, 'NETWORK_ERROR')));
    return id;
  }

  scopeIdentity(scope) {
    return Object.freeze({
      installationId: this.installationId,
      generation: this.generation,
      siteId: scope.siteId ?? null,
      requestId: scope.requestId ?? null,
    });
  }

  async storageCall(operation, key, value) {
    const scope = this.scope();
    // No startup writes: failed trial revisions cannot mutate live persistent data.
    requireValue(this.activated && scope.siteId, 'PERMISSION_DENIED');
    const permission = operation === 'get' ? 'read' : 'write';
    requireValue(this.revision.permissions.storage.includes(permission), 'PERMISSION_DENIED');
    requireValue(typeof this.storage?.[operation] === 'function', 'PERMISSION_DENIED');
    requireValue(typeof key === 'string' && key.length > 0 && key.length <= 256);
    const input = {
      ...this.scopeIdentity(scope), key, signal: scope.signal,
      isCurrent: () => this.live && !scope.signal.aborted,
    };
    if (operation === 'set') input.value = clone(value);
    try {
      const result = await scope.race(this.trackAdapter(Promise.resolve().then(() => {
        scope.assertOpen();
        return this.storage[operation](input);
      })));
      this.assertLive();
      return clone(result);
    } catch (e) { throw safeError(e, 'STORAGE_ERROR'); }
  }

  async invoke(request, { signal, timeoutMs } = {}) {
    this.assertLive();
    const payload = lxPayload(this.capabilities, request);
    const scope = new ResourceScope(this.root.signal, timeoutMs ?? this.limits.requestTimeoutMs);
    scope.siteId = request.siteId;
    scope.requestId = ++this.nextId;
    this.invocations.add(scope);
    let detach = () => {};
    try {
      if (signal) {
        const abort = () => scope.close(fault('CANCELLED'));
        if (signal.aborted) abort();
        else {
          signal.addEventListener('abort', abort, { once: true });
          detach = () => signal.removeEventListener('abort', abort);
        }
      }
      scope.assertOpen();
      const inRealm = this.call(this.realm.parse, [JSON.stringify(payload)], scope);
      const result = this.call(this.handler, [inRealm], scope);
      requireValue(result && typeof result.then === 'function', 'SCRIPT_PROTOCOL_ERROR');
      const value = await scope.race(Promise.resolve(result));
      if (!this.live) throw fault('STALE_SESSION');
      scope.assertOpen();
      return clone(value);
    } catch (e) { throw safeError(e); }
    finally {
      detach();
      scope.close();
      this.invocations.delete(scope);
    }
  }

  fail(error) {
    if (!this.live) return;
    this.failure = safeError(error);
    this.ready.reject(this.failure);
    this.stop(this.failure);
  }

  stop(reason = fault('STALE_SESSION')) {
    if (!this.live) return;
    this.live = false;
    this.activated = false;
    this.ready.reject(reason);
    this.root.close(reason);
    this.handler = null;
    // ResourceScope closes all managed requests, timers, and storage waits.
    this.context = null;
    this.realm = null;
  }

  stats() {
    return { timers: this.timers.size, networkRequests: this.networks.size,
      invocations: this.invocations.size, pendingAdapterTasks: this.adapterTasks.size, live: this.live };
  }
}
