import * as http from 'node:http';
import * as https from 'node:https';
import { EventEmitter } from 'node:events';
import { Writable, Readable } from 'node:stream';
import { randomBytes, createHash, timingSafeEqual } from 'node:crypto';
import { deferred, fault, requireValue } from './errors.js';
import { assertExternalHost, isLoopbackHost, publicLookup } from './network-boundary.js';
import { ResourceScope } from './scope.js';
import { tlsOptionNames } from './native-roles.js';

export const scriptAccessHeader = 'x-meow-script-access';
const ownerScope = Symbol('script-request-scope');
const scopeCleanup = Symbol('script-service-scope-cleanup');
const agentOwner = Symbol('script-agent-owner');
const resourceOwner = Symbol('script-resource-owner');
const ignoreError = () => {};
const equal = (left, right) => {
  if (typeof left !== 'string' || typeof right !== 'string') return false;
  const a = Buffer.from(left), b = Buffer.from(right);
  return a.length === b.length && timingSafeEqual(a, b);
};
const requestHost = host => String(host).includes(':') && !String(host).startsWith('[') ? `[${host}]` : host;
function localHeaders(input, port, access, scope) {
  const entries = [];
  if (Array.isArray(input)) {
    for (let index = 0; index + 1 < input.length; index += 2) entries.push([input[index], input[index + 1]]);
  } else entries.push(...Object.entries(input ?? {}));
  const denied = new Set(['host', scriptAccessHeader, 'x-meow-request-scope']);
  const headers = Object.fromEntries(entries.filter(([key]) => !denied.has(String(key).toLowerCase())));
  headers[scriptAccessHeader] = access;
  headers['x-meow-request-scope'] = scope;
  headers.host = `127.0.0.1:${port}`;
  return headers;
}

const listenHost = value => value === undefined || value === null || value === '' ||
  ['0.0.0.0', '::', '0:0:0:0:0:0:0:0'].includes(String(value).toLowerCase()) ||
  isLoopbackHost(String(value));
const socketError = code => Object.assign(new Error(code), { code });
function closing(target, begin) {
  const wait = {
    target, promise: null, finish: null, retry: null, release: null, owner: null,
    error: null, settled: false, errorGuard: null, guardErrors: null,
  };
  wait.promise = new Promise(resolve => {
    wait.finish = () => {
      if (wait.settled) return;
      wait.settled = true;
      try { target?.off?.('close', wait.finish); }
      catch (error) { wait.error ??= error; }
      if (wait.errorGuard) {
        try { target?.off?.('error', wait.errorGuard); }
        catch (error) { wait.error ??= error; }
      }
      wait.errorGuard = null;
      try { wait.release?.(); } catch (error) { wait.error ??= error; }
      wait.release = null;
      wait.owner = null;
      resolve();
    };
    let closed = false;
    try { closed = Boolean(target?.closed); }
    catch (error) { wait.error ??= error; }
    if (closed) { wait.finish(); return; }
    const observable = typeof target?.once === 'function';
    if (observable) {
      try { target.once('close', wait.finish); }
      catch (error) { wait.error ??= error; }
    }
    wait.retry = () => {
      if (wait.settled) return;
      try { begin(wait.finish); }
      catch (error) {
        wait.error ??= error;
        let nowClosed = false;
        try { nowClosed = Boolean(target?.closed); }
        catch (failure) { wait.error ??= failure; }
        if (nowClosed) wait.finish();
      }
    };
    wait.guardErrors = () => {
      if (wait.settled || wait.errorGuard || typeof target?.on !== 'function') return;
      try {
        wait.errorGuard = () => {};
        target.on('error', wait.errorGuard);
      } catch (error) {
        wait.errorGuard = null;
        wait.error ??= error;
      }
    };
    wait.retry();
  });
  return wait;
}
async function settleClosing(waits, timeoutMs) {
  const pending = waits.filter(wait => !wait.settled);
  if (!pending.length) return true;
  let resolveAll;
  const aggregate = {
    remaining: pending.length,
    promise: new Promise(resolve => { resolveAll = resolve; }),
  };
  for (const wait of pending) wait.promise.then(() => {
    aggregate.remaining--;
    if (!aggregate.remaining) resolveAll();
  });
  let timer;
  const complete = await Promise.race([
    aggregate.promise.then(() => true),
    new Promise(resolve => {
      timer = setTimeout(() => resolve(false), timeoutMs);
    }),
  ]);
  clearTimeout(timer);
  return complete;
}

class PortProbeServer extends EventEmitter {
  constructor(owner, options = {}, listener) {
    super();
    requireValue(options && typeof options === 'object' && !Array.isArray(options), 'INVALID_ARGUMENT');
    requireValue(listener === undefined || typeof listener === 'function', 'INVALID_ARGUMENT');
    this.owner = owner;
    this.listening = false;
    this.timeout = 0;
    this.port = null;
    if (listener) this.on('connection', listener);
  }

  listen(...args) {
    requireValue(this.owner, 'SERVICE_CLOSED');
    requireValue(!this.scope?.signal.aborted, 'SERVICE_CLOSED');
    if (this.listening) throw socketError('ERR_SERVER_ALREADY_LISTEN');
    const options = args[0] && typeof args[0] === 'object' ? args[0] : {
      port: args[0], host: typeof args[1] === 'string' ? args[1] : undefined,
    };
    requireValue(options.path === undefined && options.fd === undefined &&
      options.handle === undefined && listenHost(options.host), 'PERMISSION_DENIED');
    const requested = Number(options.port);
    requireValue(Number.isInteger(requested) && requested >= 0 && requested <= 65535,
      'INVALID_ARGUMENT');
    const callback = args.findLast(value => typeof value === 'function');
    const port = this.owner.claimProbe(this, requested);
    if (port === null) {
      queueMicrotask(() => this.emit('error', socketError('EADDRINUSE')));
      return this;
    }
    this.port = port;
    this.listening = true;
    queueMicrotask(() => {
      if (!this.listening || this.port !== port) return;
      this.emit('listening');
      callback?.call(this);
    });
    return this;
  }

  close(callback) {
    requireValue(callback === undefined || typeof callback === 'function', 'INVALID_ARGUMENT');
    if (!this.listening) {
      if (typeof callback === 'function')
        queueMicrotask(() => callback(socketError('ERR_SERVER_NOT_RUNNING')));
      return this;
    }
    this.owner?.releaseProbe(this);
    this.listening = false;
    this.port = null;
    queueMicrotask(() => {
      this.emit('close');
      callback?.call(this);
    });
    return this;
  }

  address() {
    return this.listening
      ? { address: '127.0.0.1', family: 'IPv4', port: this.port }
      : null;
  }

  getConnections(callback) {
    requireValue(typeof callback === 'function', 'INVALID_ARGUMENT');
    queueMicrotask(() => callback(null, 0));
    return this;
  }

  closeAllConnections() { return this; }
  closeIdleConnections() { return this; }
  ref() { return this; }
  unref() { return this; }
  setTimeout(value, callback) {
    requireValue(Number.isFinite(Number(value)) && Number(value) >= 0, 'INVALID_ARGUMENT');
    this.timeout = Number(value);
    if (typeof callback === 'function') this.once('timeout', callback);
    return this;
  }
}

/** Physical listeners are owned by a single installation/generation. Browser
 * pages use these origins directly; only Cat/media aggregation rewrites paths. */
export class ScriptServices {
  constructor(session) {
    this.session = session;
    this.listeners = new Set();
    this.aliases = new Map();
    this.probes = new Set();
    this.probePorts = new Map();
    this.nextProbePort = 49152;
    this.scopes = new Map();
    this.trustedSockets = new Set();
    this.connections = new Set();
    this.access = randomBytes(32).toString('hex');
    this.cookie = '__meow_' + createHash('sha256').update(session.installationId).digest('hex').slice(0, 16);
    this.primary = null;
    this.main = null;
    this.claimed = false;
    this.started = deferred();
    this.agents = new Set();
    this.requestCleanup = new Map();
    this.trustedSocketCleanup = new Map();
    this.connectionCleanup = new Map();
    this.serverCleanup = new Map();
    this.nextNetworkId = 0;
    this.http = this.facade(http, false);
    this.https = this.facade(https, true);
  }

  async prepare() {
    this.primary = this.server(http, false);
    await new Promise((resolve, reject) => {
      let settled = false;
      const finish = (callback, value) => {
        if (settled) return;
        settled = true;
        try { this.primary.off('error', failed); } catch {}
        try { this.primary.off('close', cancelled); } catch {}
        this.prepareCancel = null;
        callback(value);
      };
      const failed = () => finish(reject, fault('SERVICE_ERROR'));
      const cancelled = () => finish(reject, fault('SERVICE_CLOSED'));
      this.prepareCancel = cancelled;
      this.primary.once('error', failed);
      this.primary.once('close', cancelled);
      try {
        http.Server.prototype.listen.call(this.primary, 0, '127.0.0.1', () => {
          finish(resolve);
        });
      } catch { failed(); }
    });
    return this.primary.address().port;
  }

  server(native, secure, options = {}) {
    const creationScope = this.session.scope();
    const ownerState = { owner: this, creationScope };
    creationScope.assertOpen?.();
    const Parent = native.Server;
    class ManagedServer extends Parent {
      emit(event, ...args) {
        const owner = ownerState.owner;
        if (!owner) {
          if (event === 'request' || ['upgrade', 'connect'].includes(event)) {
            try { args[1]?.destroy?.(); } catch {}
            return true;
          }
          return super.emit(event, ...args);
        }
        if (event === 'request' && !owner.accept(args[0], args[1], this, secure)) return true;
        if (['upgrade', 'connect'].includes(event) && !owner.accept(args[0], {
          writeHead: status => args[1].write(`HTTP/1.1 ${status} Forbidden\r\nContent-Length: 0\r\n\r\n`),
          end: () => args[1].destroy(),
        }, this, secure)) return true;
        return super.emit(event, ...args);
      }
      listen(...args) {
        const owner = ownerState.owner;
        const creationScope = ownerState.creationScope;
        requireValue(owner, 'SERVICE_CLOSED');
        requireValue(creationScope && !this.meowReleased &&
          !creationScope.signal?.aborted, 'SERVICE_CLOSED');
        let port = typeof args[0] === 'object' ? args[0]?.port : args[0];
        requireValue(Number.isInteger(Number(port)) && Number(port) >= 0 &&
          Number(port) <= 65535, 'PERMISSION_DENIED');
        port = Number(port);
        const previous = port === 0 ? null : owner.aliases.get(port);
        requireValue((!previous || previous === this) && !owner.probePorts.has(port),
          'SERVICE_EXISTS');
        owner.main ??= this;
        if (port !== 0) owner.aliases.set(port, this);
        const callback = args.findLast(value => typeof value === 'function');
        if (this === owner.primary && this.listening) {
          requireValue(!this.meowStarted, 'SERVICE_EXISTS');
          this.meowStarted = true;
          queueMicrotask(() => {
            this.emit('listening'); callback?.call(this);
            if (this === owner.main) owner.started.resolve();
          });
          return this;
        }
        this.meowStarted = true;
        try {
          return Parent.prototype.listen.call(this, 0, '127.0.0.1', () => {
            callback?.call(this);
            if (this === owner.main) owner.started.resolve();
          });
        } catch (error) {
          this.meowStarted = false;
          if (port !== 0 && owner.aliases.get(port) === this) owner.aliases.delete(port);
          if (owner.main === this) owner.main = null;
          throw error;
        }
      }
    }
    requireValue(!options.IncomingMessage && !options.ServerResponse, 'UNSUPPORTED_OPTION');
    const server = new ManagedServer(this.serverOptions(options, secure));
    server.meowSecure = secure;
    server.meowReleased = false;
    try {
      Object.defineProperty(server, resourceOwner, {
        configurable: true, value: this,
      });
      this.listeners.add(server);
    } catch (error) {
      this.listeners.delete(server);
      try { delete server[resourceOwner]; } catch {}
      try { server.closeAllConnections?.(); } catch {}
      try { server.close?.(); } catch {}
      throw error;
    }
    const connected = socket => {
      const owner = ownerState.owner;
      const session = owner?.session;
      if (!session?.root || session.root.signal.aborted) {
        try { socket.destroy(); } catch {}
        return;
      }
      const scope = new ResourceScope(session.root.signal);
      const connections = owner.connections;
      const cleanups = owner.connectionCleanup;
      connections.add(socket);
      const state = { active: true, scheduled: false, detach: () => {}, scope };
      const close = () => {
        if (!state.active) return;
        state.active = false;
        const ownedScope = state.scope;
        state.scope = null;
        const detach = state.detach;
        state.detach = null;
        for (let attempt = 0; attempt < 2; attempt++) {
          try { socket.off('close', close); break; } catch {}
        }
        for (let attempt = 0; attempt < 2; attempt++) {
          try { socket.off('error', ignoreError); break; } catch {}
        }
        try { detach?.(); } catch {}
        try { delete socket[ownerScope]; } catch {
          try { socket[ownerScope] = null; } catch {}
        }
        try { delete socket[resourceOwner]; } catch {
          try { socket[resourceOwner] = null; } catch {}
        }
        connections.delete(socket);
        cleanups.delete(socket);
        if (state.scheduled || ownedScope.signal.aborted) return;
        state.scheduled = true;
        queueMicrotask(() => queueMicrotask(() => ownedScope.close()));
      };
      cleanups.set(socket, close);
      try {
        socket.on('error', ignoreError);
        socket.once('close', close);
        Object.defineProperty(socket, ownerScope, {
          configurable: true, value: scope,
        });
        Object.defineProperty(socket, resourceOwner, {
          configurable: true, value: this,
        });
        state.detach = scope.add(() => {
          if (!socket.destroyed) socket.destroy();
        });
        if (socket.closed) close();
      } catch {
        try { if (!socket.destroyed) socket.destroy(); } catch {}
        try { close(); } catch {}
        if (ownerState.owner?.session?.live) ownerState.owner.session.fail(fault('SERVICE_ERROR'));
      }
    };
    const failed = () => {
      if (ownerState.owner?.session?.live) ownerState.owner.session.fail(fault('SERVICE_ERROR'));
    };
    let released = false;
    const closed = () => {
      if (released) return;
      released = true;
      const owner = ownerState.owner;
      ownerState.owner = null;
      ownerState.creationScope = null;
      server.meowReleased = true;
      for (const [event, listener] of [
        ['connection', connected], ['error', failed], ['close', closed],
      ]) {
        for (let attempt = 0; attempt < 2; attempt++) {
          try { server.off(event, listener); break; } catch {}
        }
      }
      try { server[scopeCleanup]?.(); } catch {}
      server[scopeCleanup] = null;
      owner?.listeners.delete(server);
      if (owner) {
        for (const [port, target] of owner.aliases) if (target === server) owner.aliases.delete(port);
        if (owner.main === server) owner.main = null;
        owner.serverCleanup.delete(server);
      }
      try { server.removeAllListeners?.(); } catch {}
      try { delete server[resourceOwner]; } catch {
        try { server[resourceOwner] = null; } catch {}
      }
      owner?.session?.endpointsChanged?.();
    };
    this.serverCleanup.set(server, closed);
    try {
      server.on('connection', connected);
      server.on('error', failed);
      server.once('close', closed);
      if (creationScope !== this.session.root) {
        const detach = creationScope.add(() => {
          if (server.listening) {
            try { server.closeAllConnections?.(); } catch {}
            try { server.close(); } catch {}
          } else this.serverCleanup.get(server)?.();
        });
        if (!released) server[scopeCleanup] = detach;
        else detach();
        creationScope.assertOpen?.();
      }
    } catch (error) {
      closed();
      throw error;
    }
    return server;
  }

  probe(...args) {
    const listener = args.find(value => typeof value === 'function');
    const options = args.find(value => value && typeof value === 'object') ?? {};
    const server = new PortProbeServer(this, options, listener);
    const scope = this.session.scope();
    scope.assertOpen?.();
    try {
      Object.defineProperty(server, resourceOwner, {
        configurable: true, value: this,
      });
      server.scope = scope;
      if (scope !== this.session.root) {
        scope.add(() => {
          if (server.listening) server.close();
          else this.releaseProbe(server);
        });
        scope.assertOpen?.();
      }
      return server;
    } catch (error) {
      this.releaseProbe(server);
      try { server.removeAllListeners(); } catch {}
      throw error;
    }
  }

  claimProbe(server, requested) {
    const claimed = port => this.aliases.has(port) || this.probePorts.has(port) ||
      [...this.listeners].some(listener => listener.listening &&
        listener.address?.()?.port === port);
    let port = requested;
    if (port === 0) {
      const first = this.nextProbePort;
      do {
        const candidate = this.nextProbePort++;
        if (this.nextProbePort > 65535) this.nextProbePort = 49152;
        if (!claimed(candidate)) {
          port = candidate;
          break;
        }
      } while (this.nextProbePort !== first);
      requireValue(port !== 0, 'RESOURCE_LIMIT');
    }
    if (claimed(port)) return null;
    try {
      this.probePorts.set(port, server);
      this.probes.add(server);
    } catch (error) {
      this.probePorts.delete(port);
      this.probes.delete(server);
      throw error;
    }
    return port;
  }

  releaseProbe(server) {
    this.probes.delete(server);
    for (const [port, target] of this.probePorts)
      if (target === server) this.probePorts.delete(port);
    try { delete server[resourceOwner]; } catch {
      try { server[resourceOwner] = null; } catch {}
    }
    server.owner = null;
    server.scope = null;
  }

  create(native, secure, ...args) {
    const handler = args.find(value => typeof value === 'function');
    const options = args.find(value => value && typeof value === 'object') ?? {};
    const server = !secure && !this.claimed ? this.primary : this.server(native, secure, options);
    requireValue(server, 'SERVICE_CLOSED');
    if (!secure && server === this.primary) this.claimed = true;
    if (server === this.primary) {
      const selected = this.serverOptions(options, secure);
      for (const key of ['keepAliveTimeout', 'headersTimeout', 'requestTimeout'])
        if (selected[key] !== undefined) server[key] = selected[key];
    }
    if (handler) server.on('request', handler);
    return server;
  }

  facade(native, secure) {
    const create = (...args) => this.create(native, secure, ...args);
    const owner = this;
    function Agent(options = {}) {
      // Native Agent.createConnection never escapes. Its sockets are routed
      // through the exact same connection validation as net/tls.
      const scope = owner.session.scope?.() ?? owner.session.root;
      scope?.assertOpen?.();
      const agent = new native.Agent({ ...(secure ? owner.tlsOptions(options, { client: true }) : {}),
        ...Object.fromEntries(['keepAlive', 'keepAliveMsecs', 'maxSockets', 'maxFreeSockets',
          'maxTotalSockets', 'scheduling', 'timeout'].filter(key => options[key] !== undefined)
          .map(key => [key, options[key]])) });
      const destroy = agent.destroy.bind(agent);
      const state = { owner, detach: null, released: false, release: null };
      const releaseOwnership = () => {
        if (state.released) return;
        state.released = true;
        const current = state.owner, detach = state.detach;
        state.owner = null;
        state.detach = null;
        let failure;
        try { detach?.(); } catch (error) { failure = error; }
        current?.agents.delete(agent);
        try { delete agent[agentOwner]; } catch (error) { failure ??= error; }
        try {
          Object.defineProperty(agent, 'destroy', {
            configurable: true, writable: true, value: destroy,
          });
        } catch (error) { failure ??= error; }
        if (failure) throw failure;
      };
      state.release = releaseOwnership;
      let tracked = false;
      try {
        Object.defineProperty(agent, 'destroy', {
          configurable: true,
          value(...input) {
            const result = destroy(...input);
            releaseOwnership();
            return result;
          },
        });
        owner.agents.add(agent);
        tracked = true;
        Object.defineProperty(agent, agentOwner, {
          configurable: true, value: state,
        });
        if (scope && scope !== owner.session.root && typeof scope.add === 'function') {
          state.detach = scope.add(() => agent.destroy());
          scope.assertOpen?.();
        }
        return agent;
      } catch (error) {
        try { destroy(); } catch {}
        if (tracked) {
          try { releaseOwnership(); } catch {}
        } else {
          state.owner = null;
          state.detach = null;
          try {
            Object.defineProperty(agent, 'destroy', {
              configurable: true, writable: true, value: destroy,
            });
          } catch {}
        }
        throw error;
      }
    }
    const agent = new Agent({ keepAlive: true });
    const scopedAgents = new WeakMap();
    const defaultAgent = scope => {
      if (scope === owner.session.root) return agent;
      let selected = scopedAgents.get(scope);
      if (!selected) {
        selected = new Agent({ keepAlive: true });
        scopedAgents.set(scope, selected);
      }
      return selected;
    };
    const request = (...args) => {
      const callback = args.findLast(value => typeof value === 'function');
      let options;
      if (typeof args[0] === 'string' || args[0] instanceof URL) {
        const url = new URL(args[0]);
        requireValue(!url.username && !url.password, 'PERMISSION_DENIED');
        options = { protocol: url.protocol, hostname: url.hostname, port: url.port,
          path: url.pathname + url.search, ...(typeof args[1] === 'object' ? args[1] : {}) };
      } else options = { ...args[0] };
      // As in Node, an explicit Agent (including agent:false's one-shot Agent)
      // owns connection creation. Its unused per-request callback is metadata,
      // not permission to replace the controlled transport.
      if (options.agent === false || this.agents.has(options.agent)) delete options.createConnection;
      requireValue(!options.socketPath && options.fd === undefined &&
        !options.createConnection && !options.lookup, 'PERMISSION_DENIED');
      options.protocol ??= secure ? 'https:' : 'http:';
      if (options.protocol !== (secure ? 'https:' : 'http:'))
        throw Object.assign(new TypeError('Invalid request protocol'), { code: 'ERR_INVALID_PROTOCOL' });
      options.hostname ??= options.host ?? 'localhost';
      options.port = Number(options.port || (options.protocol === 'https:' ? 443 : 80));
      requireValue(typeof options.hostname === 'string' && Number.isInteger(options.port) &&
        options.port > 0 && options.port <= 65535 &&
        typeof (options.path ?? '/') === 'string' && (options.path ?? '/').startsWith('/') &&
        !/[\r\n\u0000]/.test(options.path ?? '/'), 'INVALID_ARGUMENT');
      const context = this.session.scope();
      context.assertOpen?.();
      const requested = new URL(`${options.protocol}//${requestHost(options.hostname)}:${options.port}${options.path ?? '/'}`);
      const local = isLoopbackHost(requested.hostname);
      if (local) {
        const target = this.listener(options.port);
        requireValue(target && target.meowSecure === (requested.protocol === 'https:'), 'UNMAPPED_LOCAL_URL');
        options.hostname = '127.0.0.1'; options.host = '127.0.0.1'; options.port = target.address().port;
      } else assertExternalHost(requested.hostname);
      const selectedAgent = options.agent === undefined ? defaultAgent(context) : options.agent;
      requireValue(selectedAgent === false || this.agents.has(selectedAgent), 'PERMISSION_DENIED');
      const scopeToken = local ? this.withScope(context) : null;
      try {
        if (local) options.headers = localHeaders(options.headers, options.port, this.access, scopeToken.id);
        const clean = { ...(secure ? this.tlsOptions(options, { client: true, local }) : {}),
          protocol: options.protocol,
          hostname: options.hostname, port: options.port, path: options.path, method: options.method,
          headers: options.headers, auth: options.auth, timeout: options.timeout, agent: selectedAgent,
          ...(!local && typeof this.session.network !== 'function' ? { lookup: publicLookup } : {}) };
        const open = settings => this.openRequest(requested, settings, context);
        const intercepted = !local && this.session.interceptor?.request(requested, clean, context, open,
          client => this.ownForward(client, context));
        if (intercepted) {
          this.session.node.tags.set(intercepted, 'request');
          return this.trackRequest(intercepted, context, callback);
        }
        return this.trackRequest(open(clean), context, callback, scopeToken);
      }
      catch (error) { scopeToken?.release(); throw error; }
    };
    const Server = function(...args) { return create(...args); };
    return { ...Object.fromEntries(['METHODS', 'STATUS_CODES']
      .filter(key => key in native).map(key => [key, native[key]])),
      Server, createServer: create,
      Agent, request, get: (...args) => {
        const client = request(...args);
        try { client.end(); } catch (error) { client.destroy(); throw error; }
        return client;
      },
      globalAgent: agent,
    };
  }

  openRequest(requested, options, context) {
    if (!isLoopbackHost(requested.hostname) && typeof this.session.network === 'function')
      return new AdapterRequest(this.session, requested, options, context);
    return (options.protocol === 'https:' ? https : http).request(options);
  }

  ownForward(client, context) {
    const session = this.session;
    const requests = session.nativeRequests;
    const cleanups = this.requestCleanup;
    const state = { active: true, detach: () => {} };
    const release = () => {
      if (!state.active) return;
      state.active = false;
      for (const [event, listener] of [['close', release], ['error', ignoreError]]) {
        for (let attempt = 0; attempt < 2; attempt++) {
          try { client.off?.(event, listener); break; } catch {}
        }
      }
      const detach = state.detach;
      state.detach = null;
      try { detach?.(); } catch {}
      requests.delete(client);
      cleanups.delete(client);
      try { delete client[resourceOwner]; } catch {
        try { client[resourceOwner] = null; } catch {}
      }
    };
    requests.add(client);
    cleanups.set(client, release);
    try {
      session.node.tags.set(client, 'request');
      client.on?.('error', ignoreError);
      client.once?.('close', release);
      Object.defineProperty(client, resourceOwner, {
        configurable: true, value: this,
      });
      state.detach = context.add(() => client.destroy());
      context.assertOpen?.();
      if (client.closed) release();
    } catch (error) {
      try { if (!client.destroyed) client.destroy(); } catch {}
      try { release(); } catch {}
      try {
        if (!client.closed) client.on?.('error', ignoreError);
      } catch {}
      throw error;
    }
    return client;
  }

  trackRequest(client, context, callback, scopeToken) {
    const requests = this.session.nativeRequests;
    const cleanups = this.requestCleanup;
    const networkId = ++this.nextNetworkId;
    const state = {
      active: true, detach: () => {}, owner: this, context, callback, scopeToken,
    };
    const release = () => {
      if (!state.active) return;
      state.active = false;
      for (const [event, listener] of [
        ['close', release], ['response', responded], ['error', failed], ['error', ignoreError],
      ]) {
        for (let attempt = 0; attempt < 2; attempt++) {
          try { client.off?.(event, listener); break; } catch {}
        }
      }
      const detach = state.detach;
      const token = state.scopeToken;
      state.detach = null;
      state.scopeToken = null;
      state.owner = null;
      state.context = null;
      state.callback = null;
      try { detach?.(); } catch {}
      try { token?.release(); } catch {}
      requests.delete(client);
      cleanups.delete(client);
      try { delete client[resourceOwner]; } catch {
        try { client[resourceOwner] = null; } catch {}
      }
    };
    const responded = response => {
      const session = state.owner?.session;
      if (!session?.live) {
        try { response.destroy?.(); } catch {}
        return;
      }
      session.node.tags.set(response, 'incoming');
      session.networkEvent?.({
        phase: 'response', networkId, statusCode: response.statusCode,
        scope: session.scopeIdentity(state.context),
      });
      state.callback?.(response);
    };
    const failed = error => {
      const session = state.owner?.session;
      if (!session) return;
      session.networkEvent?.({
        phase: 'error', networkId,
        code: error.code === 'ETIMEDOUT' ? 'TIMEOUT' : 'NETWORK_ERROR',
        scope: session.scopeIdentity(state.context),
      });
    };
    requests.add(client);
    cleanups.set(client, release);
    try {
      this.session.node.tags.set(client, 'request');
      client.on('error', ignoreError);
      client.once('close', release);
      Object.defineProperty(client, resourceOwner, {
        configurable: true, value: this,
      });
      this.session.networkEvent?.({
        phase: 'request', networkId, scope: this.session.scopeIdentity(context),
      });
      client.once('response', responded);
      client.once('error', failed);
      state.detach = context.add(() => client.destroy());
      context.assertOpen?.();
      if (client.closed) release();
    } catch (error) {
      try { client.off?.('response', responded); } catch {}
      try { client.off?.('error', failed); } catch {}
      try { if (!client.destroyed) client.destroy(); } catch {}
      try { release(); } catch {}
      try {
        if (!client.closed) client.on?.('error', ignoreError);
      } catch {}
      throw error;
    }
    return client;
  }

  tlsOptions(options = {}, { client = false, local = false } = {}) {
    requireValue(!options.socketPath && options.fd === undefined, 'PERMISSION_DENIED');
    const selected = Object.fromEntries(tlsOptionNames.filter(key => options[key] !== undefined)
      .map(key => [key, options[key]]));
    if (client) {
      selected.rejectUnauthorized = !local;
      delete selected.checkServerIdentity;
    }
    return selected;
  }

  serverOptions(options, secure) {
    return { ...(secure ? this.tlsOptions(options) : {}),
      ...Object.fromEntries(['keepAliveTimeout', 'headersTimeout', 'requestTimeout',
        'maxHeaderSize', 'joinDuplicateHeaders'].filter(key => options[key] !== undefined)
        .map(key => [key, options[key]])) };
  }

  outboundURL(input) {
    const target = new URL(input);
    requireValue(['http:', 'https:'].includes(target.protocol) && !target.username && !target.password,
      'PERMISSION_DENIED');
    if (isLoopbackHost(target.hostname)) {
      const mapped = this.mapLocal(target.href);
      requireValue(mapped, 'UNMAPPED_LOCAL_URL');
      return mapped;
    }
    assertExternalHost(target.hostname);
    return target;
  }

  externalURL(input) {
    const target = new URL(input);
    requireValue(['http:', 'https:'].includes(target.protocol) && !target.username && !target.password &&
      !isLoopbackHost(target.hostname), 'PERMISSION_DENIED');
    assertExternalHost(target.hostname);
    return target.href;
  }

  listener(port) {
    const target = [...this.listeners].find(server => server.meowStarted && server.listening &&
      server.address?.()?.port === Number(port)) ?? this.aliases.get(Number(port));
    return target?.meowStarted && target.listening ? target : null;
  }

  connection(options, secure = false) {
    const host = options.host ?? options.hostname ?? 'localhost';
    requireValue(typeof host === 'string' && !/[\\/\u0000]/.test(host), 'PERMISSION_DENIED');
    if (!isLoopbackHost(host)) {
      assertExternalHost(host);
      return { ...options, host, lookup: publicLookup };
    }
    const listener = this.listener(options.port);
    requireValue(listener && listener.meowSecure === secure, 'UNMAPPED_LOCAL_URL');
    return { ...options, host: '127.0.0.1', port: listener.address().port, meowLocal: true };
  }
  trustSocket(socket) {
    const sockets = this.trustedSockets;
    const cleanups = this.trustedSocketCleanup;
    sockets.add(socket);
    const state = { active: true };
    const release = () => {
      if (!state.active) return;
      state.active = false;
      for (let attempt = 0; attempt < 2; attempt++) {
        try { socket.off?.('close', release); break; } catch {}
      }
      sockets.delete(socket);
      cleanups.delete(socket);
      try { delete socket[resourceOwner]; } catch {
        try { socket[resourceOwner] = null; } catch {}
      }
    };
    cleanups.set(socket, release);
    try {
      socket.once?.('close', release);
      Object.defineProperty(socket, resourceOwner, {
        configurable: true, value: this,
      });
      if (socket.closed) release();
    } catch (error) {
      try { if (!socket.destroyed) socket.destroy(); } catch {}
      try { release(); } catch {}
      throw error;
    }
  }
  requestScope(request) { return request?.[ownerScope]; }
  createRequestScope(parent, request, response) {
    const scope = new ResourceScope(parent.signal);
    scope.assertOpen();
    for (const key of ['siteId', 'requestId', 'settings', 'panShares', 'storageReceipt'])
      if (Object.hasOwn(parent, key)) scope[key] = parent[key];
    scope.failureScope = parent;
    const state = { scope, scheduled: false };
    const close = () => {
      const ownedScope = state.scope;
      if (!ownedScope || state.scheduled || ownedScope.signal.aborted) return;
      state.scheduled = true;
      queueMicrotask(() => queueMicrotask(() => ownedScope.close()));
    };
    const listeners = [
      { target: request, event: 'aborted', attached: false },
      { target: response, event: 'finish', attached: false },
      { target: response, event: 'close', attached: false },
      { target: request.socket, event: 'close', attached: false },
    ];
    let active = true;
    const cleanup = () => {
      if (!active) return;
      let failure;
      for (let attempt = 0; attempt < 2; attempt++) {
        for (const listener of listeners) {
          if (!listener.attached) continue;
          try {
            listener.target?.off?.(listener.event, close);
            listener.attached = false;
          } catch (error) { failure ??= error; }
        }
      }
      try {
        if (request[ownerScope] === scope) delete request[ownerScope];
      } catch (error) {
        failure ??= error;
        try { request[ownerScope] = null; } catch (next) { failure ??= next; }
      }
      if (!response.writableEnded && !response.destroyed) {
        try {
          if (typeof response.destroy === 'function') response.destroy();
          else response.end?.();
        } catch (error) { failure ??= error; }
      }
      state.scope = null;
      active = false;
      if (failure) throw failure;
    };
    try {
      Object.defineProperty(request, ownerScope, {
        configurable: true, value: scope,
      });
      scope.add(cleanup);
      scope.assertOpen();
      for (const listener of listeners) {
        listener.target?.once?.(listener.event, close);
        listener.attached = true;
      }
    } catch (error) {
      try { scope.close(fault('SERVICE_ERROR')); } catch {}
      if (active) try { cleanup(); } catch {}
      throw error;
    }
    return scope;
  }
  withScope(scope) {
    const id = randomBytes(24).toString('hex');
    scope.assertOpen?.();
    this.scopes.set(id, scope);
    let active = true;
    let detach = () => {};
    try {
      detach = scope.add(() => {
        if (!active) return;
        active = false;
        this.scopes.delete(id);
      });
      scope.assertOpen?.();
      return { id, release: () => {
        if (!active) return;
        active = false;
        let failure;
        try { detach(); } catch (error) { failure = error; }
        detach = () => {};
        try { this.scopes.delete(id); } catch (error) { failure ??= error; }
        if (failure) throw failure;
      } };
    } catch (error) {
      active = false;
      try { detach(); } catch {}
      this.scopes.delete(id);
      throw error;
    }
  }

  accept(request, response, server, secure) {
    const session = this.session;
    if (!session) {
      response.writeHead(410, { 'content-length': '0' });
      response.end();
      return false;
    }
    const address = server.address();
    const origin = `${secure ? 'https' : 'http'}://127.0.0.1:${address.port}`;
    const deny = status => { response.writeHead(status, { 'content-length': '0' }); response.end(); return false; };
    if (request.headers.host !== `127.0.0.1:${address.port}`) return deny(403);
    const url = new URL(request.url, origin);
    if (url.pathname === '/__meow/open') {
      if (!session.activated || !equal(url.searchParams.get('access'), this.access)) return deny(403);
      const target = url.searchParams.get('target') || '/website';
      if (!target.startsWith('/') || target.startsWith('//') || /[\r\n\\]/.test(target)) return deny(400);
      response.writeHead(302, { location: target, 'cache-control': 'no-store',
        'referrer-policy': 'no-referrer',
        'set-cookie': `${this.cookie}=${this.access}; HttpOnly; SameSite=Strict; Path=/${secure ? '; Secure' : ''}` });
      response.end(); return false;
    }
    const cookie = String(request.headers.cookie ?? '').split(';').map(value => value.trim())
      .find(value => value.startsWith(this.cookie + '='))?.slice(this.cookie.length + 1);
    const socketTrusted = [...this.trustedSockets].some(socket => !socket.destroyed &&
      socket.localPort === request.socket.remotePort && socket.remotePort === address.port);
    const token = request.headers[scriptAccessHeader];
    const requestOrigin = request.headers.origin;
    const fetchSite = request.headers['sec-fetch-site'];
    const cookieAuthorized = equal(cookie, this.access) &&
      (requestOrigin === origin || requestOrigin === undefined &&
        (fetchSite === 'none' || fetchSite === 'same-origin'));
    const authorized = equal(token, this.access) || socketTrusted ||
      cookieAuthorized;
    if (!authorized || requestOrigin !== undefined && requestOrigin !== origin) return deny(403);
    const scopeToken = request.headers['x-meow-request-scope'];
    let parent = session.root;
    if (scopeToken !== undefined) {
      if (typeof scopeToken !== 'string') return deny(403);
      parent = this.scopes.get(scopeToken);
      if (!parent || parent.signal.aborted) return deny(410);
    }
    this.createRequestScope(parent, request, response);
    delete request.headers[scriptAccessHeader];
    delete request.headers['x-meow-request-scope'];
    request.headers.cookie = String(request.headers.cookie ?? '').split(';')
      .filter(value => !value.trim().startsWith('__meow_')).join(';');
    request.rawHeaders = Object.entries(request.headers).flatMap(([key, value]) => [key, String(value)]);
    return true;
  }

  endpoint() {
    requireValue(this.session?.live && this.main?.meowStarted && this.main.listening, 'SERVICE_CLOSED');
    const origin = `${this.main.meowSecure ? 'https' : 'http'}://127.0.0.1:${this.main.address().port}`;
    return { origin, access: this.access,
      managementUrl: `${origin}/__meow/open?access=${this.access}&target=%2Fwebsite` };
  }

  endpoints() {
    return [...this.listeners].filter(server => server.meowStarted && server.listening).map(server => {
      const origin = `${server.meowSecure ? 'https' : 'http'}://127.0.0.1:${server.address().port}`;
      return { origin, requestedPorts: [...this.aliases].filter(([, target]) => target === server).map(([port]) => port),
        managementUrl: `${origin}/__meow/open?access=${this.access}&target=%2Fwebsite` };
    });
  }

  mapLocal(value) {
    let url;
    try { url = new URL(value); } catch { return null; }
    if (!['http:', 'https:'].includes(url.protocol) || !isLoopbackHost(url.hostname)) return null;
    const server = this.listener(Number(url.port || (url.protocol === 'https:' ? 443 : 80)));
    requireValue(server && server.meowSecure === (url.protocol === 'https:'), 'UNMAPPED_LOCAL_URL');
    url.hostname = '127.0.0.1'; url.port = String(server.address().port);
    return url;
  }

  async close() {
    if (this.closing) return this.closing;
    let failure;
    try { this.prepareCancel?.(); }
    catch (error) { failure = error; }
    this.prepareCancel = null;
    this.started.reject(fault('SERVICE_CLOSED'));
    this.closing = this.closeResources(failure);
    return this.closing;
  }
  async closeResources(initialFailure) {
    let failure = initialFailure;
    const failed = error => { failure ??= error; };
    const session = this.session;
    const requests = [...(session?.nativeRequests ?? [])];
    const connections = [...this.connections];
    const trustedSockets = [...this.trustedSockets];
    const probes = [...this.probes];
    const listeners = [...this.listeners];
    const waits = [];
    const add = (target, begin, release) => {
      const wait = closing(target, begin);
      wait.owner = this;
      wait.release = release;
      if (wait.settled) {
        try { release?.(); } catch (error) { wait.error ??= error; }
        wait.release = null;
        wait.owner = null;
      }
      waits.push(wait);
      if (wait.error) failed(wait.error);
      return wait;
    };
    for (const request of requests) add(request, finish => {
      if (!request.destroyed && typeof request.destroy === 'function') request.destroy();
      else if (typeof request.once !== 'function') finish();
    }, () => this.requestCleanup.get(request)?.());
    for (const agent of [...this.agents]) {
      let state;
      try { state = agent[agentOwner]; } catch (error) { failed(error); }
      let released = false;
      for (let attempt = 0; attempt < 2 && !released; attempt++) {
        try { agent.destroy(); released = true; }
        catch (error) { failed(error); }
      }
      if (state?.release) {
        try { state.release(); } catch (error) { failed(error); }
      }
      if (released) {
        this.agents.delete(agent);
        try { delete agent[agentOwner]; } catch (error) { failed(error); }
      }
    }
    for (const socket of connections) {
      add(socket, finish => {
        const scope = socket[ownerScope];
        if (scope && !scope.signal.aborted) scope.close(fault('SERVICE_CLOSED'));
        if (!socket.destroyed) socket.destroy();
        if (typeof socket.once !== 'function') finish();
      }, () => this.connectionCleanup.get(socket)?.());
    }
    for (const socket of trustedSockets) add(socket, finish => {
      if (!socket.destroyed) socket.destroy();
      else if (typeof socket.once !== 'function') finish();
    }, () => this.trustedSocketCleanup.get(socket)?.());
    for (const server of probes) add(server, finish => server.close(finish),
      () => this.releaseProbe(server));
    for (const server of listeners) add(server, finish => {
      try { server.closeAllConnections?.(); } catch (error) { failed(error); }
      try { server.close(finish); }
      catch (error) {
        failed(error);
        let listening = true;
        try { listening = Boolean(server.listening); } catch (failure) { failed(failure); }
        if (!listening) finish();
        else throw error;
      }
    }, () => this.serverCleanup.get(server)?.());
    let complete = false;
    try { complete = await settleClosing(waits, session?.limits?.cleanupTimeoutMs ?? 3000); }
    catch (error) { failed(error); }
    for (const wait of waits) {
      if (!wait.settled) wait.retry?.();
      if (wait.error) failed(wait.error);
      if (!wait.settled) {
        try { wait.release?.(); } catch (error) { failed(error); }
        wait.release = null;
        wait.owner = null;
        wait.retry = null;
        wait.errorGuard = null;
        let closed = false;
        try { closed = Boolean(wait.target?.closed); } catch (error) { failed(error); }
        if (!closed && typeof wait.target?.once === 'function') {
          for (let attempt = 0; attempt < 2; attempt++) {
            try { wait.target.off?.('close', wait.finish); break; }
            catch (error) { failed(error); }
          }
          try { wait.target.once('close', wait.finish); } catch (error) { failed(error); }
        } else if (closed) wait.finish();
        if (!wait.settled) wait.guardErrors?.();
        wait.guardErrors = null;
      }
      if (wait.error) failed(wait.error);
    }
    for (const target of new Set([...requests, ...connections, ...trustedSockets])) {
      if (target[ownerScope]) {
        try { target[ownerScope].close?.(fault('SERVICE_CLOSED')); }
        catch (error) { failed(error); }
      }
    }
    for (const scope of new Set(this.scopes.values())) {
      try { scope.close?.(fault('SERVICE_CLOSED')); } catch (error) { failed(error); }
    }
    session?.nativeRequests?.clear();
    this.listeners.clear(); this.probes.clear(); this.connections.clear();
    this.trustedSockets.clear();
    this.requestCleanup.clear(); this.trustedSocketCleanup.clear();
    this.connectionCleanup.clear(); this.serverCleanup.clear();
    this.scopes.clear(); this.aliases.clear(); this.probePorts.clear();
    this.main = null; this.primary = null;
    this.session = null;
    if (!complete) failed(fault('TIMEOUT'));
    if (failure) throw failure;
  }
}

// An injected metadata network adapter cannot accidentally fall through to
// host TCP. Production without an adapter uses native streaming HTTP above.
class AdapterRequest extends Writable {
  constructor(session, url, options, scope) {
    super();
    this.session = session; this.url = url; this.options = options; this.scope = scope;
    this.chunks = []; this.headers = { ...options.headers };
    this.method = options.method ?? 'GET'; this.path = url.pathname + url.search;
    this.controller = new AbortController();
    this.scopeAbort = () => this.controller.abort(scope.reason);
    if (scope.signal.aborted) this.scopeAbort();
    else scope.signal.addEventListener('abort', this.scopeAbort, { once: true });
  }
  setHeader(key, value) { this.headers[key.toLowerCase()] = value; }
  getHeader(key) { return this.headers[key.toLowerCase()]; }
  getHeaders() { return { ...this.headers }; }
  getHeaderNames() { return Object.keys(this.headers); }
  hasHeader(key) { return Object.hasOwn(this.headers, key.toLowerCase()); }
  removeHeader(key) { delete this.headers[key.toLowerCase()]; }
  flushHeaders() {}
  setTimeout(delay, callback) {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => { this.emit('timeout'); callback?.(); }, delay);
    return this;
  }
  setNoDelay() { return this; }
  setSocketKeepAlive() { return this; }
  abort() {
    try { this.emit('abort'); }
    finally { this.destroy(); }
    return this;
  }
  _write(chunk, _, done) { this.chunks.push(Buffer.from(chunk)); done(); }
  _final(done) {
    const session = this.session;
    if (!session || !this.controller || this.controller.signal.aborted) {
      done(fault('SERVICE_CLOSED'));
      return;
    }
    const sessionRef = new WeakRef(session);
    const network = session.network;
    const input = {
      url: this.url.href, options: { method: this.method, headers: this.headers,
        ...(this.chunks.length ? { body: Buffer.concat(this.chunks) } : {}) },
      signal: this.controller.signal, scope: session.scopeIdentity(this.scope),
      validateURL: value => {
        const owner = sessionRef.deref();
        if (!owner) throw fault('STALE_SESSION');
        return owner.services.externalURL(value);
      },
    };
    const completion = {
      active: true, request: new WeakRef(this), done,
      settle(error, reply) {
        if (!this.active) return;
        this.active = false;
        const request = this.request.deref();
        const finish = this.done;
        this.request = null; this.done = null;
        if (!request || request.destroyed || request.controller.signal.aborted) return;
        if (error) { finish(error); return; }
        try {
          const response = Readable.from([Buffer.from(reply.body ?? '')]);
          Object.assign(response, {
            statusCode: reply.statusCode, statusMessage: reply.statusMessage ?? '',
            headers: reply.headers ?? {}, rawHeaders: Object.entries(reply.headers ?? {})
              .flatMap(([key, value]) => [key, String(value)]),
            trailers: {}, rawTrailers: [], httpVersion: '1.1', httpVersionMajor: 1,
            httpVersionMinor: 1, complete: true, req: request,
          });
          request.session.node.tags.set(response, 'incoming');
          request.emit('response', response);
          finish();
        } catch (failure) { finish(failure); }
      },
      clear() {
        this.active = false;
        this.request = null; this.done = null;
      },
    };
    this.adapterCompletion = completion;
    const task = Promise.resolve().then(() => network(input));
    try { session.trackAdapter(task); }
    catch (error) {
      completion.clear();
      task.catch(() => {});
      done(error);
      return;
    }
    task.then(
      reply => completion.settle(null, reply),
      error => completion.settle(error),
    ).catch(() => {});
  }
  _destroy(error, done) {
    let failure = error;
    const failed = next => { failure ??= next; };
    try { this.adapterCompletion?.clear(); } catch (next) { failed(next); }
    this.adapterCompletion = null;
    clearTimeout(this.timer);
    this.timer = null;
    try { this.scope?.signal.removeEventListener('abort', this.scopeAbort); }
    catch (next) { failed(next); }
    try { this.controller?.abort(error ?? fault('CANCELLED')); }
    catch (next) { failed(next); }
    for (const chunk of this.chunks) chunk.fill(0);
    this.chunks = [];
    this.headers = {};
    this.options = null;
    this.url = null;
    this.path = null;
    this.method = null;
    this.controller = null;
    this.scopeAbort = null;
    this.session = null;
    this.scope = null;
    done(failure);
  }
}
