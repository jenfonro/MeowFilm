import * as http from 'node:http';
import * as https from 'node:https';
import { EventEmitter } from 'node:events';
import { Writable, Readable } from 'node:stream';
import { randomBytes } from 'node:crypto';
import { deferred, fault, requireValue } from './errors.js';
import { isLoopbackHost } from './network-boundary.js';
import { ResourceScope } from './scope.js';
import { tlsOptionNames, projectHttpRequestOptions } from './native-roles.js';
import { afterCloseDispatch, closeResource } from './resource-close.js';

const ownerScope = Symbol('script-request-scope');
const scopeCleanup = Symbol('script-service-scope-cleanup');
const agentOwner = Symbol('script-agent-owner');
const resourceOwner = Symbol('script-resource-owner');
const ignoreError = () => {};
const requestHost = host => String(host).includes(':') && !String(host).startsWith('[') ? `[${host}]` : host;
const connectHost = host => typeof host === 'string' && host.startsWith('[') && host.endsWith(']')
  ? host.slice(1, -1) : host;
function localHeaders(input, scope) {
  const entries = [];
  if (Array.isArray(input)) {
    for (let index = 0; index + 1 < input.length; index += 2) entries.push([input[index], input[index + 1]]);
  } else entries.push(...Object.entries(input ?? {}));
  const headers = Object.fromEntries(entries.filter(([key]) => String(key).toLowerCase() !== 'x-meow-request-scope'));
  headers['x-meow-request-scope'] = scope;
  return headers;
}

const socketError = code => Object.assign(new Error(code), { code });

class PortProbeServer extends EventEmitter {
  constructor(owner, options = {}) {
    super();
    requireValue(options && typeof options === 'object' && !Array.isArray(options), 'INVALID_ARGUMENT');
    this.owner = owner;
    this.session = new WeakRef(owner.session);
    this.listening = false;
    this.timeout = 0;
    this.port = null;
  }

  listen(...args) {
    requireValue(this.owner, 'SERVICE_CLOSED');
    requireValue(!this.scope?.signal.aborted, 'SERVICE_CLOSED');
    if (this.listening) throw socketError('ERR_SERVER_ALREADY_LISTEN');
    const options = args[0] && typeof args[0] === 'object' ? args[0] : {
      port: args[0], host: typeof args[1] === 'string' ? args[1] : undefined,
    };
    requireValue(options.path === undefined && options.fd === undefined &&
      options.handle === undefined, 'PERMISSION_DENIED');
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
    if (callback) this.once('listening', callback);
    queueMicrotask(() => {
      if (!this.listening || this.port !== port) return;
      this.emit('listening');
    });
    return this;
  }

  close(callback) {
    requireValue(callback === undefined || typeof callback === 'function', 'INVALID_ARGUMENT');
    if (!this.listening) {
      if (typeof callback === 'function') {
        const session = this.session.deref(), scope = session?.scope();
        queueMicrotask(() => session?.runCallback(callback, undefined,
          [socketError('ERR_SERVER_NOT_RUNNING')], scope));
      }
      return this;
    }
    if (callback) this.once('close', callback);
    this.owner?.releaseProbe(this);
    this.listening = false;
    this.port = null;
    queueMicrotask(() => {
      this.emit('close');
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
    const session = this.session.deref(), scope = session?.scope();
    queueMicrotask(() => session?.runCallback(callback, undefined, [null, 0], scope));
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
    this.connections = new Set();
    this.primary = null;
    this.main = null;
    this.claimed = false;
    this.started = deferred();
    this.agents = new Set();
    this.requestCleanup = new Map();
    this.connectionCleanup = new Map();
    this.serverCleanup = new Map();
    this.cleanupState = { error: null };
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
        http.Server.prototype.listen.call(this.primary, 0, () => {
          finish(resolve);
        });
      } catch { failed(); }
    });
    return this.primary.address().port;
  }

  server(native, secure, options = {}) {
    const creationScope = this.session.resourceScope?.() ?? this.session.scope();
    const ownerState = { owner: this, creationScope };
    let closeDispatching = false;
    creationScope.assertOpen?.();
    const Parent = native.Server;
    class ManagedServer extends Parent {
      emit(event, ...args) {
        if (event === 'close') {
          closeDispatching = true;
          try { return super.emit(event, ...args); }
          finally { closeDispatching = false; closed(); }
        }
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
        if (event === 'request') {
          const scope = owner.requestScope(args[0]);
          owner.session.node?.observeEmitter(args[0]);
          owner.session.node?.observeEmitter(args[1]);
          return owner.session.contextScope.run(scope, () => super.emit(event, ...args));
        }
        return super.emit(event, ...args);
      }
      close(...args) {
        const closing = this.meowClosing;
        this.meowClosing = true;
        try { return super.close(...args); }
        catch (error) { this.meowClosing = closing; throw error; }
      }
      listen(...args) {
        const owner = ownerState.owner;
        const creationScope = ownerState.creationScope;
        requireValue(owner && !owner.stopping, 'SERVICE_CLOSED');
        requireValue(creationScope && !this.meowReleased &&
          !creationScope.signal?.aborted, 'SERVICE_CLOSED');
        if (args[0] && typeof args[0] === 'object')
          requireValue(args[0].path === undefined && args[0].fd === undefined &&
            args[0].handle === undefined, 'PERMISSION_DENIED');
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
          if (callback) this.once('listening', callback);
          queueMicrotask(() => {
            this.emit('listening');
            if (this === owner.main) owner.started.resolve();
            owner.session?.endpointsChanged?.();
          });
          return this;
        }
        this.meowStarted = true;
        try {
          if (callback) this.once('listening', callback);
          return Parent.prototype.listen.call(this, 0, () => {
            if (this === owner.main) owner.started.resolve();
            owner.session?.endpointsChanged?.();
          });
        } catch (error) {
          if (callback) this.removeListener('listening', callback);
          this.meowStarted = false;
          if (port !== 0 && owner.aliases.get(port) === this) owner.aliases.delete(port);
          if (owner.main === this) owner.main = null;
          throw error;
        }
      }
    }
    const server = new ManagedServer(this.serverOptions(options, secure));
    server.meowSecure = secure;
    server.meowReleased = false;
    server.meowClosing = false;
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
      if (!session?.root || session.root.signal.aborted || owner.stopping) {
        try { socket.destroy(); } catch {}
        return;
      }
      const scope = new ResourceScope(session.root.signal);
      const connections = owner.connections;
      const cleanups = owner.connectionCleanup;
      const failures = owner.cleanupState;
      connections.add(socket);
      const state = { active: true, detach: () => {}, detachClosed: null, scope };
      const close = () => {
        if (!state.active) return;
        state.active = false;
        const ownedScope = state.scope;
        state.scope = null;
        const detach = state.detach;
        state.detach = null;
        const detachClosed = state.detachClosed;
        state.detachClosed = null;
        try { detachClosed?.(); } catch (error) { failures.error ??= error; }
        try { socket.off('error', ignoreError); } catch (error) { failures.error ??= error; }
        try { detach?.(); } catch (error) { failures.error ??= error; }
        try { delete socket[ownerScope]; } catch (error) { failures.error ??= error; }
        try { delete socket[resourceOwner]; } catch (error) { failures.error ??= error; }
        connections.delete(socket);
        cleanups.delete(socket);
        if (!ownedScope.signal.aborted) {
          try { ownedScope.close(fault('SERVICE_CLOSED')); }
          catch (error) { failures.error ??= error; }
        }
      };
      cleanups.set(socket, close);
      try {
        socket.on('error', ignoreError);
        state.detachClosed = afterCloseDispatch(socket, close);
        Object.defineProperty(socket, ownerScope, {
          configurable: true, value: scope,
        });
        Object.defineProperty(socket, resourceOwner, {
          configurable: true, value: this,
        });
        state.detach = scope.add(() => {
          if (!socket.destroyed) socket.destroy();
        });
        session.node?.observeEmitter(socket);
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
    const secured = socket => ownerState.owner?.session.node?.observeEmitter(socket);
    let released = false;
    const closed = () => {
      // Reentrant cleanup cannot retire ownership during terminal dispatch.
      if (released || closeDispatching) return;
      released = true;
      const owner = ownerState.owner;
      ownerState.owner = null;
      ownerState.creationScope = null;
      server.meowReleased = true;
      for (const [event, listener] of [
        ['connection', connected], ['secureConnection', secured], ['error', failed],
      ]) try { server.off(event, listener); } catch (error) { if (owner) owner.cleanupState.error ??= error; }
      try { server[scopeCleanup]?.(); } catch (error) { if (owner) owner.cleanupState.error ??= error; }
      try { server[scopeCleanup] = null; } catch (error) { if (owner) owner.cleanupState.error ??= error; }
      owner?.listeners.delete(server);
      if (owner) {
        for (const [port, target] of owner.aliases) if (target === server) owner.aliases.delete(port);
        if (owner.main === server) owner.main = null;
        owner.serverCleanup.delete(server);
      }
      try { server.removeAllListeners?.(); } catch (error) { if (owner) owner.cleanupState.error ??= error; }
      try { delete server[resourceOwner]; } catch (error) { if (owner) owner.cleanupState.error ??= error; }
      owner?.session?.endpointsChanged?.();
    };
    this.serverCleanup.set(server, closed);
    try {
      server.on('connection', connected);
      if (secure) server.on('secureConnection', secured);
      server.on('error', failed);
      if (creationScope !== this.session.root) {
        const detach = creationScope.add(() => {
          if (server.listening || server.meowStarted || server === this.primary) {
            try { server.closeAllConnections?.(); } catch {}
            try { if (!server.meowClosing) server.close(); } catch {}
          } else if (!server.meowClosing) this.serverCleanup.get(server)?.();
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
    requireValue(!this.stopping, 'SERVICE_CLOSED');
    const listener = args.find(value => typeof value === 'function');
    const options = args.find(value => value && typeof value === 'object') ?? {};
    const server = new PortProbeServer(this, options);
    this.session.node?.observeEmitter(server);
    if (listener) server.on('connection', listener);
    const scope = this.session.resourceScope?.() ?? this.session.scope();
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
    requireValue(!this.stopping, 'SERVICE_CLOSED');
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
    requireValue(!this.stopping, 'SERVICE_CLOSED');
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
    this.session.node?.observeEmitter(server);
    if (handler) server.on('request', handler);
    return server;
  }

  facade(native, secure) {
    const create = (...args) => this.create(native, secure, ...args);
    const owner = this;
    function Agent(options = {}) {
      // Native Agent.createConnection never escapes. Its sockets are routed
      // through the exact same connection validation as net/tls.
      const scope = owner.session.resourceScope?.() ?? owner.session.root;
      scope?.assertOpen?.();
      const agent = new native.Agent({ ...(secure ? owner.tlsOptions(options, { client: true }) : {}),
        ...Object.fromEntries(['keepAlive', 'keepAliveMsecs', 'maxSockets', 'maxFreeSockets',
          'maxTotalSockets', 'scheduling', 'timeout'].filter(key => options[key] !== undefined)
          .map(key => [key, options[key]])) });
      const connect = agent.createConnection;
      agent.createConnection = function(options, callback) {
        requireValue(options.socketPath === undefined && options.fd === undefined &&
          !options.path && !options.socket && !options.lookup && !options.createConnection,
        'PERMISSION_DENIED');
        const mapped = owner.connection(options, secure);
        requireValue(mapped.meowLocal || isLoopbackHost(mapped.host) ||
          owner.session.network === undefined, 'CAPABILITY_UNAVAILABLE');
        const clean = { ...options, host: mapped.host, port: mapped.port,
          ...(secure ? owner.tlsOptions(options, { client: true, local: Boolean(mapped.meowLocal) }) : {}) };
        const socket = connect.call(this, clean, secure ? callback : undefined);
        owner.session.node?.ownNetwork(socket);
        if (!secure && typeof callback === 'function') socket.once('connect', callback);
        return socket;
      };
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
    const request = (...args) => {
      const callback = args.findLast(value => typeof value === 'function');
      let options;
      if (typeof args[0] === 'string' || args[0] instanceof URL) {
        const url = new URL(args[0]);
        options = { protocol: url.protocol, hostname: url.hostname, port: url.port,
          path: url.pathname + url.search,
          ...(url.username || url.password ? { auth: `${decodeURIComponent(url.username)}:${decodeURIComponent(url.password)}` } : {}),
          ...(typeof args[1] === 'object' ? args[1] : {}) };
      } else options = projectHttpRequestOptions(args[0],
        value => value === false || this.agents.has(value)) ?? {};
      // As in Node, an explicit Agent (including agent:false's one-shot Agent)
      // owns connection creation. Its unused per-request callback is metadata,
      // not permission to replace the controlled transport.
      if (options.agent === false || this.agents.has(options.agent))
        options = projectHttpRequestOptions(options, () => true);
      requireValue(options.socketPath === undefined && options.fd === undefined &&
        !options.createConnection && !options.lookup, 'PERMISSION_DENIED');
      options.protocol ??= secure ? 'https:' : 'http:';
      if (options.protocol !== (secure ? 'https:' : 'http:')) {
        const error = Object.assign(new TypeError('Invalid request protocol'), { code: 'ERR_INVALID_PROTOCOL' });
        this.session.emitScriptDiagnostic?.({
          kind: error.name, reason: 'host', code: error.code,
        }, this.session.scope());
        throw error;
      }
      options.hostname ??= options.host ?? 'localhost';
      options.port = Number(options.port || (options.protocol === 'https:' ? 443 : 80));
      requireValue(typeof options.hostname === 'string' && Number.isInteger(options.port) &&
        options.port > 0 && options.port <= 65535 &&
        typeof (options.path ?? '/') === 'string' && (options.path ?? '/').startsWith('/') &&
        !/[\r\n\u0000]/.test(options.path ?? '/'), 'INVALID_ARGUMENT');
      // Keep attribution and pending-transport cancellation separate from the
      // session ownership of script-created handles and connection pools.
      const context = this.session.scope();
      requireValue(!this.stopping, 'SERVICE_CLOSED');
      this.session.resourceScope().assertOpen();
      const requested = new URL(`${options.protocol}//${requestHost(options.hostname)}:${options.port}${options.path ?? '/'}`);
      const loopback = isLoopbackHost(requested.hostname);
      const local = loopback && Boolean(this.listener(options.port));
      if (local) {
        const target = this.listener(options.port);
        requireValue(target && target.meowSecure === (requested.protocol === 'https:'), 'UNMAPPED_LOCAL_URL');
        options.hostname = '127.0.0.1'; options.host = '127.0.0.1'; options.port = target.address().port;
      } else if (loopback) {
        options.hostname = connectHost(options.hostname);
        if (options.host !== undefined) options.host = connectHost(options.host);
      }
      const selectedAgent = options.agent === undefined ? agent : options.agent;
      requireValue(selectedAgent === false || this.agents.has(selectedAgent), 'PERMISSION_DENIED');
      const outbound = this.createOutboundScope(context);
      let scopeToken;
      try {
        scopeToken = local ? this.withScope(outbound) : null;
        if (local) options.headers = localHeaders(options.headers, scopeToken.id);
        const clean = { ...(secure ? this.tlsOptions(options, { client: true, local }) : {}),
          protocol: options.protocol,
          hostname: options.hostname, port: options.port, path: options.path, method: options.method,
          headers: options.headers, auth: options.auth, timeout: options.timeout, agent: selectedAgent,
        };
        const open = settings => this.openRequest(requested, settings, outbound);
        const intercepted = !loopback && this.session.interceptor?.request(requested, clean, outbound, open,
          client => this.ownForward(client, outbound));
        if (intercepted) {
          return this.trackRequest(intercepted, outbound, callback);
        }
        return this.trackRequest(open(clean), outbound, callback, scopeToken);
      }
      catch (error) { scopeToken?.release(); outbound.close(); throw error; }
    };
    const Server = function(...args) { return create(...args); };
    Server.prototype = native.Server.prototype;
    Agent.prototype = native.Agent.prototype;
    return { ...Object.fromEntries(['METHODS', 'STATUS_CODES']
      .filter(key => key in native).map(key => [key, native[key]])),
      Server, createServer: create, IncomingMessage: http.IncomingMessage,
      ServerResponse: http.ServerResponse, OutgoingMessage: http.OutgoingMessage,
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

  createOutboundScope(context) {
    const scope = new ResourceScope(this.session.root.signal);
    for (const key of ['siteId', 'requestId', 'associationId', 'settings', 'panShares', 'storageReceipt'])
      if (Object.hasOwn(context, key)) scope[key] = context[key];
    // A script request can outlive the inbound exchange, including a delayed
    // ClientRequest.end() or a request to another listener of this instance.
    // Keep its token and adapter signal alive after a normal response, without
    // retaining invocation-only storage or cancellation privileges.
    if (context !== this.session.root && context !== this.session.resourceScope?.()) {
      const ended = () => {
        if (context.completedResponse) {
          delete scope.associationId;
          delete scope.storageReceipt;
        } else scope.close(context.reason);
      };
      context.signal.addEventListener('abort', ended, { once: true });
      scope.add(() => context.signal.removeEventListener('abort', ended));
      if (context.signal.aborted) ended();
    }
    return scope;
  }

  ownForward(client, context) {
    const session = this.session;
    const requests = session.nativeRequests;
    const cleanups = this.requestCleanup;
    const failures = this.cleanupState;
    const state = { active: true, detach: () => {}, detachClosed: null };
    const release = () => {
      if (!state.active) return;
      state.active = false;
      try { state.detachClosed?.(); } catch (error) { failures.error ??= error; }
      state.detachClosed = null;
      try { client.off?.('error', ignoreError); } catch (error) { failures.error ??= error; }
      const detach = state.detach;
      state.detach = null;
      try { detach?.(); } catch (error) { failures.error ??= error; }
      requests.delete(client);
      cleanups.delete(client);
      try { delete client[resourceOwner]; } catch (error) { failures.error ??= error; }
    };
    requests.add(client);
    cleanups.set(client, release);
    try {
      client.on?.('error', ignoreError);
      state.detachClosed = afterCloseDispatch(client, release);
      Object.defineProperty(client, resourceOwner, {
        configurable: true, value: this,
      });
      state.detach = context.add(() => { if (!client.destroyed) client.destroy(); });
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
    const failures = this.cleanupState;
    const networkId = ++this.nextNetworkId;
    const state = {
      active: true, detach: () => {}, detachClosed: null, owner: this, context, callback, scopeToken,
    };
    const release = () => {
      if (!state.active) return;
      state.active = false;
      try { state.detachClosed?.(); } catch (error) { failures.error ??= error; }
      state.detachClosed = null;
      for (const [event, listener] of [
        ['response', responded], ['socket', connected],
        ['error', failed], ['error', ignoreError],
      ]) try { client.off?.(event, listener); } catch (error) { failures.error ??= error; }
      const detach = state.detach;
      const token = state.scopeToken;
      const context = state.context;
      state.detach = null;
      state.scopeToken = null;
      state.owner = null;
      state.context = null;
      state.callback = null;
      try { detach?.(); } catch (error) { failures.error ??= error; }
      try { token?.release(); } catch (error) { failures.error ??= error; }
      try { context?.close(); } catch (error) { failures.error ??= error; }
      requests.delete(client);
      cleanups.delete(client);
      try { delete client[resourceOwner]; } catch (error) { failures.error ??= error; }
    };
    const responded = response => {
      const session = state.owner?.session;
      if (!session?.live) {
        try { response.destroy?.(); } catch {}
        return;
      }
      session.node?.observeEmitter(response);
      session.networkEvent?.({
        phase: 'response', networkId, statusCode: response.statusCode,
        scope: session.scopeIdentity(state.context),
      });
      if (state.callback) session.runCallback(state.callback, client, [response], state.context);
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
    const connected = socket => state.owner?.session.node?.observeEmitter(socket);
    requests.add(client);
    cleanups.set(client, release);
    try {
      client.on('error', ignoreError);
      state.detachClosed = afterCloseDispatch(client, release);
      Object.defineProperty(client, resourceOwner, {
        configurable: true, value: this,
      });
      this.session.networkEvent?.({
        phase: 'request', networkId, scope: this.session.scopeIdentity(context),
      });
      client.once('response', responded);
      client.once('error', failed);
      client.once('socket', connected);
      if (client.socket) connected(client.socket);
      state.detach = context.add(() => { if (!client.destroyed) client.destroy(); });
      context.assertOpen?.();
      if (client.closed) release();
      this.session.node?.observeEmitter(client);
    } catch (error) {
      try { client.off?.('response', responded); } catch {}
      try { client.off?.('error', failed); } catch {}
      try { client.off?.('socket', connected); } catch {}
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
    requireValue(options.socketPath === undefined && options.fd === undefined, 'PERMISSION_DENIED');
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
        'maxHeaderSize', 'joinDuplicateHeaders', 'IncomingMessage', 'ServerResponse']
        .filter(key => options[key] !== undefined)
        .map(key => [key, options[key]])) };
  }

  outboundURL(input) {
    requireValue(!this.stopping, 'SERVICE_CLOSED');
    const target = new URL(input);
    requireValue(['http:', 'https:'].includes(target.protocol), 'INVALID_ARGUMENT');
    if (isLoopbackHost(target.hostname)) {
      const mapped = this.mapLocal(target.href);
      if (mapped) return mapped;
    }
    return target;
  }

  externalURL(input) {
    const target = new URL(input);
    requireValue(['http:', 'https:'].includes(target.protocol), 'INVALID_ARGUMENT');
    return target.href;
  }

  listener(port) {
    const target = [...this.listeners].find(server => server.meowStarted && server.listening &&
      server.address?.()?.port === Number(port)) ?? this.aliases.get(Number(port));
    return target?.meowStarted && target.listening ? target : null;
  }

  connection(options, secure = false) {
    requireValue(!this.stopping, 'SERVICE_CLOSED');
    const host = options.host ?? options.hostname ?? 'localhost';
    requireValue(typeof host === 'string' && !/[\\/\u0000]/.test(host), 'PERMISSION_DENIED');
    if (!isLoopbackHost(host)) {
      return { ...options, host };
    }
    const listener = this.listener(options.port);
    if (!listener) return { ...options, host: connectHost(host) };
    requireValue(listener.meowSecure === secure, 'UNMAPPED_LOCAL_URL');
    return { ...options, host: '127.0.0.1', port: listener.address().port, meowLocal: true };
  }
  requestScope(request) { return request?.[ownerScope]; }
  createRequestScope(parent, request, response, transferScope = parent) {
    const failures = this.cleanupState;
    // RPC cancellation terminates the transport. The request's resources live
    // until that transport actually closes, not until a sibling control call
    // completes; otherwise framework queue-release handlers are skipped.
    const scope = new ResourceScope(this.session.root.signal);
    scope.ownsTransport = true;
    scope.assertOpen();
    for (const key of ['siteId', 'requestId', 'associationId', 'settings', 'panShares', 'storageReceipt'])
      if (Object.hasOwn(transferScope, key)) scope[key] = transferScope[key];
    scope.failureScope = transferScope;
    const state = { scope };
    const close = () => {
      const ownedScope = state.scope;
      if (!ownedScope || ownedScope.signal.aborted) return;
      // Normal completion ends the inbound exchange, not the outbound work
      // the script started while serving it. An aborted exchange still cancels
      // its pending transport. ResourceScope's default reason cannot tell them
      // apart; use the actual ServerResponse completion fact.
      ownedScope.completedResponse = response.writableFinished === true;
      try { ownedScope.close(); } catch (error) { failures.error ??= error; }
    };
    // Upgraded connections have no ServerResponse; their actual transport
    // supplies the same terminal dispatch boundary.
    const completionTarget = typeof response.emit === 'function' ? response : request.socket;
    const observableCompletion = typeof completionTarget?.emit === 'function';
    const terminate = () => {
      try {
        if (!response.destroyed) {
          if (typeof response.destroy === 'function') response.destroy();
          else response.end?.();
        }
        if (!observableCompletion) close();
      } catch (error) {
        failures.error ??= error;
        scope.close(fault('SERVICE_ERROR'));
      }
    };
    const listeners = [
      { target: request, event: 'aborted', callback: terminate, attached: false },
      { target: request.socket, event: 'close', callback: terminate, attached: false },
    ];
    let active = true, parentAttached = false, detachCompleted = null;
    const cleanup = () => {
      if (!active) return;
      let failure;
      for (const listener of listeners) {
        if (!listener.attached) continue;
        try {
          listener.target?.off?.(listener.event, listener.callback);
          listener.attached = false;
        } catch (error) { failure ??= error; }
      }
      try {
        if (request[ownerScope] === scope) delete request[ownerScope];
      } catch (error) { failure ??= error; }
      try { detachCompleted?.(); } catch (error) { failure ??= error; }
      detachCompleted = null;
      if (parentAttached) {
        try { transferScope.signal.removeEventListener('abort', terminate); }
        catch (error) { failure ??= error; }
        parentAttached = false;
      }
      // The response belongs to the transport scope and may still be writing.
      // Releasing the scope must not truncate a completed script response.
      if (!response.writableEnded && !response.destroyed && !scope.completedResponse) {
        try {
          if (typeof response.destroy === 'function') response.destroy();
          else response.end?.();
        } catch (error) { failure ??= error; }
      }
      state.scope = null;
      active = false;
      if (failure) {
        failures.error ??= failure;
        throw failure;
      }
    };
    try {
      Object.defineProperty(request, ownerScope, {
        configurable: true, value: scope,
      });
      scope.add(cleanup);
      scope.assertOpen();
      if (observableCompletion) detachCompleted = afterCloseDispatch(completionTarget, close);
      for (const listener of listeners) {
        listener.target?.once?.(listener.event, listener.callback);
        listener.attached = true;
      }
      if (transferScope !== this.session.root) {
        transferScope.signal.addEventListener('abort', terminate, { once: true });
        parentAttached = true;
      }
      if (transferScope.signal.aborted) terminate();
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

  accept(request, response) {
    const session = this.session;
    if (!session || this.stopping) {
      response.writeHead(410, { 'content-length': '0' });
      response.end();
      return false;
    }
    // CORS is transport metadata, not an origin or caller admission rule.
    if (response.setHeader) {
      response.setHeader('Access-Control-Allow-Origin', request.headers.origin ?? '*');
      response.setHeader('Access-Control-Allow-Credentials', 'true');
      response.setHeader('Access-Control-Allow-Methods', 'GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS');
      response.setHeader('Access-Control-Allow-Headers', request.headers['access-control-request-headers'] ?? '*');
      response.setHeader('Access-Control-Expose-Headers', '*');
      response.setHeader('Vary', 'Origin');
      if (request.method === 'OPTIONS') {
        response.writeHead(204); response.end(); return false;
      }
    }
    const scopeToken = request.headers['x-meow-request-scope'];
    let parent = session.root;
    if (scopeToken !== undefined) {
      parent = this.scopes.get(scopeToken);
      if (!parent || parent.signal.aborted) {
        response.writeHead(410, { 'content-length': '0' }); response.end(); return false;
      }
    }
    this.createRequestScope(parent, request, response, parent);
    delete request.headers['x-meow-request-scope'];
    const raw = request.rawHeaders;
    if (raw) for (let index = raw.length - 2; index >= 0; index -= 2)
      if (raw[index].toLowerCase() === 'x-meow-request-scope') raw.splice(index, 2);
    return true;
  }

  endpoint() {
    requireValue(this.session?.live && this.main?.meowStarted && this.main.listening, 'SERVICE_CLOSED');
    const origin = `${this.main.meowSecure ? 'https' : 'http'}://127.0.0.1:${this.main.address().port}`;
    return { origin, managementUrl: `${origin}/website` };
  }

  endpoints() {
    return [...this.listeners].filter(server => server.meowStarted && server.listening).map(server => {
      const origin = `${server.meowSecure ? 'https' : 'http'}://127.0.0.1:${server.address().port}`;
      return { origin, requestedPorts: [...this.aliases].filter(([, target]) => target === server).map(([port]) => port),
        managementUrl: `${origin}/website` };
    });
  }

  mapLocal(value) {
    let url;
    try { url = new URL(value); } catch { return null; }
    if (!['http:', 'https:'].includes(url.protocol) || !isLoopbackHost(url.hostname)) return null;
    const server = this.listener(Number(url.port || (url.protocol === 'https:' ? 443 : 80)));
    if (!server) return null;
    requireValue(server.meowSecure === (url.protocol === 'https:'), 'UNMAPPED_LOCAL_URL');
    url.hostname = '127.0.0.1'; url.port = String(server.address().port);
    return url;
  }

  beginStop() {
    if (this.stopping) return;
    this.stopping = true;
    // Stop active I/O and refuse new admission, but let a cooperative script
    // close its own listeners before final owner cleanup.
    for (const target of new Set([...(this.session?.nativeRequests ?? []),
      ...this.connections, ...this.agents])) {
      try { if (!target.destroyed) target.destroy(); }
      catch (error) { this.cleanupState.error ??= error; }
    }
  }

  async close() {
    if (this.closing) return this.closing;
    this.beginStop();
    let failure;
    try { this.prepareCancel?.(); }
    catch (error) { failure = error; }
    this.prepareCancel = null;
    this.started.reject(fault('SERVICE_CLOSED'));
    this.closing = this.closeResources(failure);
    return this.closing;
  }
  async closeResources(initialFailure) {
    let failure = initialFailure ?? this.cleanupState.error;
    const failed = error => { failure ??= error; };
    const session = this.session;
    const requests = [...(session?.nativeRequests ?? [])];
    const connections = [...this.connections];
    const probes = [...this.probes];
    const listeners = [...this.listeners];
    const waits = [];
    const add = (target, stop, release, options = {}) => {
      const promise = closeResource(target, {
        stop, release, timeoutMs: session?.limits?.cleanupTimeoutMs ?? 3000, ...options,
      });
      waits.push(promise);
      return promise;
    };
    for (const request of requests) add(request, () => {
      if (!request.destroyed && typeof request.destroy === 'function') request.destroy();
    }, () => this.requestCleanup.get(request)?.(), {
      closed: value => value.closed && !this.requestCleanup.has(value),
    });
    for (const agent of [...this.agents]) {
      let state, failure;
      try { state = agent[agentOwner]; } catch (error) { failure ??= error; }
      try { agent.destroy(); } catch (error) { failure ??= error; }
      if (state?.release) {
        try { state.release(); } catch (error) { failure ??= error; }
      }
      this.agents.delete(agent);
      try { delete agent[agentOwner]; } catch (error) { failure ??= error; }
      waits.push(Promise.resolve({ error: failure }));
    }
    for (const socket of connections) {
      add(socket, () => {
        const scope = socket[ownerScope];
        if (scope && !scope.signal.aborted) scope.close(fault('SERVICE_CLOSED'));
        if (!socket.destroyed) socket.destroy();
      }, () => this.connectionCleanup.get(socket)?.(), {
        closed: value => value.closed && !this.connectionCleanup.has(value),
      });
    }
    for (const server of probes) add(server, () => server.close(ignoreError),
      () => this.releaseProbe(server));
    for (const server of listeners) {
      add(server, () => {
        let failure;
        try { server.closeAllConnections?.(); } catch (error) { failure = error; }
        try { if (!server.meowClosing) server.close(ignoreError); } catch (error) {
          if (error?.code !== 'ERR_SERVER_NOT_RUNNING') failure ??= error;
        }
        if (failure) throw failure;
      }, () => this.serverCleanup.get(server)?.(), {
        closed: () => server.meowReleased ||
          (!server.listening && !server.meowClosing && !server.meowStarted && server !== this.primary),
      });
    }
    const results = await Promise.all(waits);
    for (const result of results) {
      if (result.error && result.error.code === 'TIMEOUT') failure ??= fault('TIMEOUT');
      else if (result.error) failed(result.error);
    }
    for (const target of new Set([...requests, ...connections])) {
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
    this.requestCleanup.clear();
    this.connectionCleanup.clear(); this.serverCleanup.clear();
    this.scopes.clear(); this.aliases.clear(); this.probePorts.clear();
    this.main = null; this.primary = null;
    this.session = null;
    failure ??= this.cleanupState.error;
    this.cleanupState.error = null;
    if (failure) throw failure;
  }
}

// An injected metadata network adapter cannot accidentally fall through to
// host TCP. Production without an adapter uses native streaming HTTP above.
class AdapterRequest extends Writable {
  constructor(session, url, options, scope) {
    super();
    this.session = session; this.url = url; this.options = options; this.scope = scope;
    this.chunks = []; this.headers = Object.fromEntries(Object.entries(options.headers ?? {})
      .map(([key, value]) => [key.toLowerCase(), value]));
    if (options.auth !== undefined && this.headers.authorization === undefined)
      this.headers.authorization = 'Basic ' + Buffer.from(options.auth).toString('base64');
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
    if (callback) this.once('timeout', callback);
    this.timer = setTimeout(() => this.emit('timeout'), delay);
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
      associationId: this.scope.associationId,
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
