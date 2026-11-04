import * as http from 'node:http';
import * as https from 'node:https';
import { randomBytes } from 'node:crypto';
import { LXSession } from './lx-session.js';
import { QuickJSEngine } from './quickjs-engine.js';
import { nodeRealmSource } from './node-realm.js';
import { NodeCapabilities } from './node-capabilities.js';
import { ScriptServices, scriptAccessHeader } from './script-services.js';
import { PanInterceptor } from './pan-interceptor.js';
import { ResourceScope } from './scope.js';
import { clone, deferred, fault, requireValue, safeError } from './errors.js';
import { normalizeCatOutput, decodePanResource, addExtension, mergeExtension,
  ownedExtension, parseM3U8Metadata } from './cat-output.js';
import { allowedProxyPath, fixedOriginURL } from './cat-proxy.js';
import { isLoopbackHost } from './network-boundary.js';
import { describeScriptError } from './script-diagnostic.js';

const configuredSites = config =>
  config?.video?.sites ?? config?.sites ?? config?.data?.video?.sites ?? config?.data?.sites;

function guardClientRequestErrors(client) {
  const state = {
    client, active: true, failure: null, error: () => {}, closed: null,
  };
  state.closed = () => {
    if (!state.active) return;
    state.active = false;
    const target = state.client;
    state.client = null;
    try { target?.off?.('close', state.closed); } catch (error) { state.failure ??= error; }
    try { target?.off?.('error', state.error); } catch (error) { state.failure ??= error; }
    state.closed = null;
    state.error = null;
  };
  try { client.on('error', state.error); } catch (error) { state.failure ??= error; }
  try { client.once('close', state.closed); } catch (error) { state.failure ??= error; }
  try { if (client.closed) state.closed(); } catch (error) { state.failure ??= error; }
  return state;
}

export class CatSession extends LXSession {
  constructor({ dataRoot, settings, aggregate, onNetwork, onActivate, onStop,
    onEndpointsChanged, ...input }) {
    // Timeout values come from ScriptHost's single per-protocol definition.
    super(input);
    requireValue(typeof dataRoot === 'string', 'CAPABILITY_UNAVAILABLE');
    requireValue(onNetwork === undefined || typeof onNetwork === 'function');
    requireValue(onActivate === undefined || typeof onActivate === 'function');
    requireValue(onStop === undefined || typeof onStop === 'function');
    requireValue(onEndpointsChanged === undefined || typeof onEndpointsChanged === 'function');
    this.settings = settings ?? { value: { providers: {}, mappings: [] } };
    this.aggregatePort = aggregate?.port ?? 0;
    this.onNetwork = onNetwork;
    this.onActivate = onActivate;
    this.onStop = onStop;
    this.onEndpointsChanged = onEndpointsChanged;
    this.capabilities = Object.create(null);
    this.services = new ScriptServices(this);
    this.node = new NodeCapabilities(this, this.services, dataRoot);
    this.interceptor = new PanInterceptor();
    this.nativeRequests = new Set();
    this.sites = new Map();
    this.proxyMappings = new Map();
    this.pendingProxies = new Set();
    this.stopNotified = false;
    this.guestStopped = deferred();
  }
  applySettings(value) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return;
    this.settings.value = clone(value);
  }
  applyAggregate({ port } = {}) {
    requireValue(Number.isSafeInteger(port) && port > 0, 'INVALID_ARGUMENT');
    this.aggregatePort = port;
  }
  async start(spec = {}) {
    if (spec.settings?.value) this.applySettings(spec.settings.value);
    const timeout = setTimeout(() => this.fail(fault('TIMEOUT')), this.limits.initTimeoutMs);
    try {
      const port = await this.services.prepare();
      this.engine = await QuickJSEngine.create({ source: nodeRealmSource, limits: this.limits,
        wasmModule: this.wasmModule,
        bridge: (operation, args, frames, outFrames) => {
          try { return this.bridge(operation, args, frames, outFrames); }
          catch (error) { this.bridgeDiagnostic(operation, args, error); throw error; }
        } });
      if (!this.live) { this.engine.close(); throw fault('STALE_SESSION'); }
      this.contextScope.run(this.root, () => this.engine.call('catBoot', [this.revision.source,
        JSON.stringify({ config: this.revision.config, env: {
          PORT: String(port), DEV_HTTP_PORT: String(port), HTTP_PORT: String(port),
          HOST: '127.0.0.1', CATVOD_DISABLE_AUTOSTART: '1',
          NODE_PATH: '/', CATPAW_DIST_AUTH: '0', CATPAW_CUSTOM_SPIDER_ENABLED: '0',
        }, port })]));
      await this.ready.promise;
      await this.root.race(this.services.started.promise);
      const { config, origin: configOrigin } = await this.readConfig();
      const sites = configuredSites(config);
      requireValue(Array.isArray(sites) && sites.length > 0, 'SCRIPT_PROTOCOL_ERROR');
      for (const site of sites) {
        requireValue(site && typeof site.key === 'string' && site.key.length > 0 &&
          site.key.length <= 1024 && !this.sites.has(site.key), 'SCRIPT_PROTOCOL_ERROR');
        requireValue(typeof site.api === 'string', 'SCRIPT_PROTOCOL_ERROR');
        let target;
        try { target = new URL(site.api, configOrigin); }
        catch { throw fault('SCRIPT_PROTOCOL_ERROR'); }
        requireValue(['http:', 'https:'].includes(target.protocol) && !target.username && !target.password &&
          !target.search && !target.hash, 'SCRIPT_PROTOCOL_ERROR');
        if (isLoopbackHost(target.hostname)) {
          const secure = target.protocol === 'https:';
          const requestedPort = Number(target.port || (secure ? 443 : 80));
          let listener = this.services.listener(requestedPort);
          if (!listener && this.services.main?.meowStarted && this.services.main.listening &&
            this.services.main.meowSecure === secure) {
            this.services.aliases.set(requestedPort, this.services.main);
            listener = this.services.main;
          }
          requireValue(listener?.listening && listener.meowSecure === secure, 'SCRIPT_PROTOCOL_ERROR');
        }
        const actual = this.services.mapLocal(target.href);
        requireValue(actual, 'SCRIPT_PROTOCOL_ERROR');
        this.sites.set(site.key, { ...site, api: actual.href.replace(/\/$/, '') });
        this.capabilities[site.key] = { actions: [], methods: {}, discovery: 'opaque-http',
          api: actual.pathname, name: site.name ?? site.key };
      }
      const discovered = this.engine.callJSON('catDescribe', [JSON.stringify([...this.sites])]);
      requireValue(Array.isArray(discovered) && discovered.length <= this.sites.size,
        'SCRIPT_PROTOCOL_ERROR');
      const described = new Set();
      for (const entry of discovered) {
        requireValue(Array.isArray(entry) && entry.length === 2, 'SCRIPT_PROTOCOL_ERROR');
        const [key, capability] = entry;
        requireValue(typeof key === 'string' && this.sites.has(key) && !described.has(key) &&
          capability && typeof capability === 'object' && !Array.isArray(capability),
        'SCRIPT_PROTOCOL_ERROR');
        requireValue(Array.isArray(capability.actions) &&
          capability.actions.every(action => typeof action === 'string' &&
            /^[A-Za-z][\w-]{0,63}$/.test(action)) &&
          capability.methods && typeof capability.methods === 'object' &&
          !Array.isArray(capability.methods) &&
          Object.entries(capability.methods).every(([action, method]) =>
            capability.actions.includes(action) &&
            ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].includes(method)) &&
          ['declared', 'registered-routes', 'source-routes', 'opaque-http'].includes(capability.discovery),
        'SCRIPT_PROTOCOL_ERROR');
        described.add(key);
        Object.assign(this.capabilities[key], {
          actions: [...new Set(capability.actions)],
          methods: { ...capability.methods },
          discovery: capability.discovery,
        });
      }
      this.configOrigin = configOrigin;
      this.config = config;
      this.endpointsChanged();
      return this;
    } catch (error) {
      this.fail(error);
      throw this.failure ?? safeError(error);
    } finally { clearTimeout(timeout); }
  }

  bridge(operation, input, frames = [], outFrames = []) {
    if (operation === 'node') return this.node.bridge(input, frames, outFrames);
    if (operation === 'catReady') { this.ready.resolve(); return null; }
    if (operation === 'catStopped') { this.guestStopped.resolve(); return null; }
    if (operation === 'nodeCallbackError') {
      const memory = input[0] === 'MEMORY_LIMIT';
      if (memory && this.engine) this.engine.memoryLimitExceeded = true;
      this.callbackFailed(fault(memory ? 'RESOURCE_LIMIT' : input[0]),
        this.scope(), this.callbackFailureScope);
      return null;
    }
    return super.bridge(operation, input);
  }

  storageWritten(scope = this.scope()) {
    if (scope.storageReceipt) scope.storageReceipt.resolve();
  }

  scriptInfo() {
    return { name: 'Cat script', version: this.revision.version };
  }

  async readConfig() {
    const primary = this.services.endpoint().origin;
    const origins = [...new Set([primary, ...this.services.endpoints().map(value => value.origin)])];
    let rejected, malformed = false;
    for (const origin of origins) {
      for (const path of ['/full-config', '/config']) {
        const scope = new ResourceScope(this.root.signal);
        scope.requestId = ++this.nextId;
        try {
          const config = await this.read(path, {}, scope, 'GET', origin);
          const sites = configuredSites(config);
          if (Array.isArray(sites) && sites.length > 0) {
            const target = new URL(origin);
            const listener = this.services.listener(Number(target.port ||
              (target.protocol === 'https:' ? 443 : 80)));
            requireValue(listener, 'SERVICE_CLOSED');
            this.services.main = listener;
            return { config, origin };
          }
          malformed = true;
        } catch (error) {
          const failure = safeError(error);
          if (failure.code !== 'CAPABILITY_UNAVAILABLE' && failure.statusCode !== 404) rejected ??= failure;
        } finally { scope.close(); }
      }
    }
    if (rejected) throw rejected;
    throw fault(malformed ? 'SCRIPT_PROTOCOL_ERROR' : 'CAPABILITY_UNAVAILABLE');
  }

  async management(path, payload, method = 'GET', { requireWrite = false,
    origin = this.services.endpoint().origin, signal = null } = {}) {
    const scope = new ResourceScope(this.root.signal, this.limits.requestTimeoutMs);
    scope.requestId = ++this.nextId;
    if (requireWrite) scope.storageReceipt = deferred();
    const abort = () => scope.close(signal.reason instanceof Error
      ? signal.reason : fault('CANCELLED'));
    if (signal) {
      if (signal.aborted) abort();
      else signal.addEventListener('abort', abort, { once: true });
    }
    const previousFailure = this.callbackFailureScope;
    this.callbackFailureScope = scope;
    try {
      const value = await this.read(path, payload, scope, method, origin);
      if (requireWrite && successfulManagement(value)) {
        await scope.race(scope.storageReceipt.promise);
      }
      return value;
    } finally {
      try { signal?.removeEventListener('abort', abort); } catch { /* Scope close remains authoritative. */ }
      this.callbackFailureScope = previousFailure;
      scope.close();
    }
  }

  nativeCallback(id, value, scope, receiver = { $: 'undefined' }, fresh = false,
    failureScope = null, frames = []) {
    if (!this.live || scope.signal.aborted) return { $: 'undefined' };
    const previousFailure = this.callbackFailureScope;
    // Guest code keeps the invocation's metadata context while the callback
    // runs; native resources it creates remain session-owned.
    const ambient = this.contextScope.getStore();
    const metadata = failureScope ?? (ambient && !ambient.signal.aborted && ambient !== this.root &&
      ambient !== this.resourceScope() ? ambient : scope);
    let result;
    this.callbackFailureScope = failureScope;
    try {
      result = this.contextScope.run(metadata, () =>
        this.engine.callJSONFrames('nativeCallback', [String(id), JSON.stringify(value),
          JSON.stringify(receiver), fresh ? '1' : '0'], frames));
    } finally { this.callbackFailureScope = previousFailure; }
    const answer = result?.value;
    requireValue(answer && Number.isSafeInteger(answer.context) && answer.context >= 0 &&
      Object.hasOwn(answer, 'value'), 'SCRIPT_EXCEPTION');
    this.node.retainContext(answer.context, scope);
    // Continuations created by this callback keep its attribution metadata;
    // the lease above still gives native resources the session's ownership.
    if (fresh && failureScope && answer.context > 0)
      this.node.observeContext(answer.context, failureScope);
    queueMicrotask(() => {
      if (this.live) {
        // Pending guest continuations keep this turn's attribution metadata;
        // native resources they create are still owned by the session.
        try { this.contextScope.run(metadata, () => this.engine.pump()); }
        catch (error) { this.callbackFailed(error, this.resourceScope(), failureScope); }
      }
    });
    return { value: answer.value, frames: result.frames };
  }

  callbackFailed(error, scope, failureScope = null) {
    if (!this.live) return;
    const failure = safeError(error);
    // Only heap exhaustion fails the instance. Other resource-limit callback
    // failures close their request or remain background diagnostics.
    if (failure.code === 'MEMORY_LIMIT' || this.engine?.memoryLimitExceeded) {
      this.fail(fault('RESOURCE_LIMIT'));
      return;
    }
    const diagnostic = target => {
      try {
        this.onDiagnostic?.(Object.freeze({ ...this.scopeIdentity(target),
          kind: 'ScriptCallbackError', reason: 'script', code: failure.code }));
      } catch { /* Background diagnostics cannot change script state. */ }
    };
    // A finished inbound request no longer owns a failure. Do not promote its
    // callback error to the still-running read it was originally waiting on.
    const origin = failureScope ?? scope;
    if (origin?.signal?.aborted) {
      diagnostic(origin);
      return;
    }
    // An inbound script request's failure belongs to the originating request
    // when one exists, so closing it reports the callback's actual reason.
    const target = [failureScope?.failureScope, failureScope, scope?.failureScope, scope]
      .find(candidate => candidate && candidate !== this.root &&
        candidate !== this.resourceScope() && !candidate.signal?.aborted);
    if (target) {
      try { target.close(failure); } catch { /* The invocation reports its own failure. */ }
      return;
    }
    diagnostic(origin ?? this.root);
  }

  async read(path, payload, scope, method = 'POST', origin = this.services.endpoint().origin) {
    scope.assertOpen();
    const endpoint = this.services.endpoint();
    const target = new URL(path, origin);
    const query = method === 'GET' || method === 'HEAD';
    if (query) {
      requireValue(payload === null || typeof payload === 'object' && !Array.isArray(payload));
      for (const [key, value] of Object.entries(payload ?? {}))
        if (value !== undefined) target.searchParams.set(key,
          value !== null && typeof value === 'object' ? JSON.stringify(value) : String(value));
    }
    const raw = query ? null : Buffer.from(JSON.stringify(payload));
    let token;
    let request, requestGuard, response;
    let task, value, failure;
    try {
      token = this.services.withScope(scope);
      task = new Promise((resolve, reject) => {
      let settled = false, stop = () => {};
      const cleanup = () => {
        let failure;
        for (const [target, event, listener] of [
          [request, 'error', requestFailed], [request, 'close', requestClosed],
          [response, 'data', data], [response, 'error', responseFailed],
          [response, 'end', ended], [response, 'aborted', aborted],
          [response, 'close', responseClosed],
        ]) {
          try { target?.off(event, listener); } catch (error) { failure ??= error; }
        }
        return failure;
      };
      const finish = (success, value) => {
        if (settled) return;
        settled = true;
        let cleanupFailure = cleanup();
        try { stop(); } catch (error) { cleanupFailure ??= error; }
        for (const chunk of chunks ?? []) chunk.fill(0);
        chunks = [];
        length = 0;
        if (success && cleanupFailure) reject(safeError(cleanupFailure, 'SERVICE_ERROR'));
        else (success ? resolve : reject)(value);
      };
      const requestFailed = error => finish(false,
        scope.signal.aborted ? scope.reason : safeError(error, 'NETWORK_ERROR'));
      const requestClosed = () => {
        if (!response) finish(false, scope.signal.aborted ? scope.reason : fault('NETWORK_ERROR'));
      };
      let chunks, length;
      const data = chunk => {
        if (settled) return;
        length += chunk.length;
        if (length > this.limits.maxMemoryBytes) {
          finish(false, fault('RESOURCE_LIMIT'));
          try { response.destroy(); } catch { /* Cleanup reports any remaining owned resource. */ }
          return;
        }
        chunks.push(Buffer.from(chunk));
      };
      const responseFailed = error => finish(false, safeError(error, 'NETWORK_ERROR'));
      const ended = () => {
        if (response.statusCode < 200 || response.statusCode >= 300) {
          const failure = fault('UPSTREAM_HTTP_ERROR');
          failure.statusCode = response.statusCode;
          let bytes;
          try {
            bytes = Buffer.concat(chunks);
            const body = JSON.parse(bytes.toString('utf8'));
            const detail = body?.error && typeof body.error === 'object' ? body.error : body;
            const message = detail?.message ?? detail?.msg ?? detail?.err ??
              (typeof detail?.error === 'string' ? detail.error : null);
            if (typeof message === 'string')
              this.emitScriptDiagnostic(describeScriptError({
                name: detail.name, message, code: detail.code,
              }), scope, message);
          } catch { /* Only structured error fields contribute a sanitized diagnostic. */ }
          finally { bytes?.fill(0); }
          try {
            this.onDiagnostic?.(Object.freeze({ ...this.scopeIdentity(scope),
              kind: 'ScriptHTTPError', reason: 'http', statusCode: response.statusCode }));
          } catch { /* Observation cannot change the script response. */ }
          return finish(false, failure);
        }
        let combined;
        try {
          combined = Buffer.concat(chunks);
          const text = combined.toString('utf8');
          if (!text.trim()) finish(true, null);
          else {
            try { finish(true, JSON.parse(text)); }
            catch {
              const contentType = String(response.headers['content-type'] ?? '');
              if (!contentType || /^text\//i.test(contentType) &&
                !/(?:\/|\+)json(?:\s*;|$)/i.test(contentType)) finish(true, text);
              else finish(false, fault('INVALID_JSON'));
            }
          }
        } catch (error) { finish(false, safeError(error, 'SERVICE_ERROR')); }
        finally { combined?.fill(0); }
      };
      const aborted = () => finish(false, fault('NETWORK_ERROR'));
      const responseClosed = () => {
        if (!response.complete)
          finish(false, scope.signal.aborted ? scope.reason : fault('NETWORK_ERROR'));
      };
      const responded = incoming => {
        if (settled) {
          try { incoming.destroy(); } catch { /* The request is already settled. */ }
          return;
        }
        response = incoming;
        chunks = [];
        length = 0;
        try {
          response.on('data', data);
          response.once('error', responseFailed);
          response.once('end', ended);
          response.once('aborted', aborted);
          response.once('close', responseClosed);
          if (scope.signal.aborted) {
            finish(false, scope.reason);
            try { response.destroy(); } catch { /* Request ownership cleanup still follows. */ }
          }
        } catch (error) {
          finish(false, safeError(error, 'NETWORK_ERROR'));
          try { response.destroy(); } catch { /* Request ownership cleanup still follows. */ }
        }
      };
      try {
        request = (target.protocol === 'https:' ? https : http).request(target, {
          ...(target.protocol === 'https:' ? { rejectUnauthorized: false } : {}),
          agent: false, method,
          headers: { [scriptAccessHeader]: endpoint.access, 'x-meow-request-scope': token.id,
            ...(raw ? { 'content-type': 'application/json', 'content-length': raw.length } : {}) },
        }, responded);
        requestGuard = guardClientRequestErrors(request);
        if (requestGuard.failure) throw requestGuard.failure;
        request.once('error', requestFailed);
        request.once('close', requestClosed);
        stop = scope.add(() => {
          finish(false, scope.reason ?? fault('CANCELLED'));
          try { response?.destroy(); } catch {}
          try { request?.destroy(); } catch {}
        });
        if (!settled) request.end(raw);
      } catch (error) {
        finish(false, safeError(error, 'NETWORK_ERROR'));
        try { response?.destroy(); } catch {}
        try { request?.destroy(); } catch {}
        }
      });
      value = await scope.race(this.trackAdapter(task));
    } catch (error) { failure = error; }
    const failed = error => { failure ??= safeError(error, 'SERVICE_ERROR'); };
    try { response?.destroy(); } catch (error) { failed(error); }
    try { request?.destroy(); } catch (error) { failed(error); }
    if (requestGuard) {
      try { if (request?.closed) requestGuard.closed?.(); } catch (error) { failed(error); }
      if (requestGuard.failure) failed(requestGuard.failure);
      requestGuard = null;
    }
    try { token?.release(); } catch (error) { failed(error); }
    try { raw?.fill(0); } catch (error) { failed(error); }
    // A guest heap overrun fails the session and tears down its transport;
    // report the limit rather than the resulting socket error.
    if (failure && this.engine?.memoryLimitExceeded) failure = fault('RESOURCE_LIMIT');
    if (failure) throw failure;
    return value;
  }

  async readText(url, headers = {}, timeoutMs = 3000, signal = null) {
    const scope = new ResourceScope(this.root.signal, timeoutMs);
    const abort = () => scope.close(signal.reason instanceof Error
      ? signal.reason : fault('CANCELLED'));
    if (signal) {
      if (signal.aborted) abort();
      else signal.addEventListener('abort', abort, { once: true });
    }
    let client, clientGuard, response;
    let value, failure;
    try {
      value = await this.contextScope.run(scope, () => scope.race(new Promise((resolve, reject) => {
        const safeHeaders = Object.fromEntries(Object.entries(headers ?? {}).filter(([key, value]) =>
          typeof key === 'string' && !/[\r\n\u0000]/.test(key) &&
          !/^(?:host|content-length|transfer-encoding|connection)$/i.test(key) &&
          (typeof value === 'string' || Array.isArray(value) && value.every(item => typeof item === 'string'))));
        safeHeaders['accept-encoding'] = 'identity';
        const target = new URL(url);
        const chunks = [];
        let length = 0, settled = false, release = () => {};
        const cleanup = () => {
          let failure;
          for (const [target, event, listener] of [
            [client, 'response', responded], [client, 'error', clientFailed],
            [client, 'close', clientClosed], [response, 'data', data],
            [response, 'error', responseFailed], [response, 'aborted', aborted],
            [response, 'end', ended], [response, 'close', responseClosed],
          ]) {
            try { target?.off(event, listener); } catch (error) { failure ??= error; }
          }
          return failure;
        };
        const finish = (success, value) => {
          if (settled) return;
          settled = true;
          let cleanupFailure = cleanup();
          try { release(); } catch (error) { cleanupFailure ??= error; }
          for (const chunk of chunks) chunk.fill(0);
          chunks.length = 0;
          length = 0;
          if (success && cleanupFailure) reject(safeError(cleanupFailure, 'SERVICE_ERROR'));
          else (success ? resolve : reject)(value);
        };
        const clientFailed = error => finish(false, safeError(error, 'NETWORK_ERROR'));
        const clientClosed = () => {
          if (!response) finish(false, scope.signal.aborted ? scope.reason : fault('NETWORK_ERROR'));
        };
        const data = chunk => {
          if (settled) return;
          length += chunk.length;
          if (length > this.limits.maxMemoryBytes) {
            finish(false, fault('RESOURCE_LIMIT'));
            try { response.destroy(); } catch { /* Request ownership cleanup still follows. */ }
            return;
          }
          chunks.push(Buffer.from(chunk));
        };
        const responseFailed = error => finish(false, safeError(error, 'NETWORK_ERROR'));
        const aborted = () => finish(false, fault('NETWORK_ERROR'));
        const ended = () => {
          if (response.statusCode < 200 || response.statusCode >= 300)
            finish(false, fault('UPSTREAM_HTTP_ERROR'));
          else {
            let combined;
            try {
              combined = Buffer.concat(chunks);
              finish(true, combined.toString('utf8'));
            } catch (error) { finish(false, safeError(error, 'SERVICE_ERROR')); }
            finally { combined?.fill(0); }
          }
        };
        const responseClosed = () => {
          if (!response.complete) finish(false,
            scope.signal.aborted ? scope.reason : fault('NETWORK_ERROR'));
        };
        const responded = incoming => {
          if (settled) {
            try { incoming.destroy(); } catch { /* The request is already settled. */ }
            return;
          }
          response = incoming;
          try {
            response.on('data', data);
            response.once('error', responseFailed);
            response.once('aborted', aborted);
            response.once('end', ended);
            response.once('close', responseClosed);
            if (scope.signal.aborted) {
              finish(false, scope.reason);
              try { response.destroy(); } catch { /* Request ownership cleanup still follows. */ }
            }
          } catch (error) {
            finish(false, safeError(error, 'NETWORK_ERROR'));
            try { response.destroy(); } catch { /* Request ownership cleanup still follows. */ }
          }
        };
        try {
          client = (target.protocol === 'https:' ? this.services.https : this.services.http)
            .request(url, { method: 'GET', headers: safeHeaders,
              ...(target.protocol === 'https:' && isLoopbackHost(target.hostname)
                ? { rejectUnauthorized: false } : {}) });
          clientGuard = guardClientRequestErrors(client);
          if (clientGuard.failure) throw clientGuard.failure;
          client.once('response', responded);
          client.once('error', clientFailed);
          client.once('close', clientClosed);
          release = scope.add(() => {
            finish(false, scope.reason ?? fault('CANCELLED'));
            try { response?.destroy(); } catch {}
            try { client?.destroy(); } catch {}
          });
          client.end();
        } catch (error) {
          finish(false, safeError(error, 'NETWORK_ERROR'));
          try { response?.destroy(); } catch {}
          try { client?.destroy(); } catch {}
        }
      })));
    } catch (error) { failure = error; }
    const failed = error => { failure ??= safeError(error, 'SERVICE_ERROR'); };
    try { signal?.removeEventListener('abort', abort); }
    catch { /* Scope close remains authoritative. */ }
    try { scope.close(); } catch (error) { failed(error); }
    try { response?.destroy(); } catch (error) { failed(error); }
    try { client?.destroy(); } catch (error) { failed(error); }
    if (clientGuard) {
      try { if (client?.closed) clientGuard.closed?.(); } catch (error) { failed(error); }
      if (clientGuard.failure) failed(clientGuard.failure);
      clientGuard = null;
    }
    if (failure) throw failure;
    return value;
  }

  invoke(input, options = {}) {
    return this.invokeTurn(input, options);
  }
  enhanceResult(value) {
    const owned = Boolean(value && typeof value === 'object' && value[ownedExtension]);
    if (!owned) return value;
    const result = clone(value);
    Object.defineProperty(result, ownedExtension, { value: true });
    return result;
  }
  proxyForOrigin(origin, pending = false) {
    let entry = [...this.proxyMappings.values()].find(item => item.origin === origin);
    if (!entry) {
      entry = { id: randomBytes(24).toString('hex'), origin };
      this.proxyMappings.set(entry.id, entry);
      if (pending) this.pendingProxies.add(entry.id);
    }
    return entry;
  }
  mapProxy(value, baseOrigin) {
    if (typeof value !== 'string') return value;
    const webPrefix = 'js2p://_WEB_';
    if (value.startsWith(webPrefix)) value = new URL(value.slice(webPrefix.length),
      baseOrigin ?? this.services.endpoint().origin).href;
    else if (value.startsWith('/') && !value.startsWith('//')) value = new URL(value,
      baseOrigin ?? this.services.endpoint().origin).href;
    // The worker owns the real listener table. Keep alias and HTTP(S)
    // mismatch semantics identical to direct CatSession use.
    const local = this.services.mapLocal(value);
    if (!local) return value;
    requireValue(allowedProxyPath(local.pathname), 'PERMISSION_DENIED');
    const entry = this.proxyForOrigin(local.origin, true);
    return `http://127.0.0.1:${this.aggregatePort || Number(new URL(
      this.services.endpoint().origin).port)}/proxy/${this.installationId}/${this.generation}/${entry.id}${local.pathname}${local.search}${local.hash}`;
  }
  takeNewProxies() {
    const entries = [...this.pendingProxies]
      .map(id => this.proxyMappings.get(id)).filter(Boolean);
    this.pendingProxies.clear();
    return entries.map(({ id, origin }) => ({ id, origin }));
  }
  proxyEndpoints() {
    return this.services.endpoints().map(endpoint => ({
      ...endpoint, proxyId: this.proxyForOrigin(endpoint.origin).id,
    }));
  }
  endpointSnapshot() {
    const main = this.services.main;
    return { endpoint: this.live && main?.meowStarted && main.listening ? this.endpoint() : null,
      access: this.services.access, endpoints: this.proxyEndpoints(),
      aggregatePort: this.aggregatePort };
  }

  async invokeTurn({ siteId, operation, payload }, { signal, timeoutMs, associationId } = {}) {
    this.assertLive();
    const site = this.sites.get(siteId);
    requireValue(site && this.capabilities[siteId].actions.includes(operation),
      'CAPABILITY_UNAVAILABLE');
    const siteOrigin = new URL(site.api).origin;
    const settings = clone(this.settings.value);
    const scope = new ResourceScope(this.root.signal, timeoutMs ?? this.limits.requestTimeoutMs);
    scope.siteId = siteId; scope.requestId = ++this.nextId;
    scope.associationId = associationId;
    scope.settings = settings;
    scope.panShares = new Map();
    const abort = () => scope.close(fault('CANCELLED'));
    this.invocations.set(scope.requestId, { scope });
    let listening = false;
    try {
      if (signal?.aborted) abort();
      else if (signal) {
        signal.addEventListener('abort', abort, { once: true });
        listening = true;
      }
      const resource = operation === 'play' && payload && typeof payload === 'object'
        ? decodePanResource(payload.id) : null;
      if (resource) {
        requireValue(settings.providers[resource.provider], 'CAPABILITY_UNAVAILABLE');
        requireValue(resource.installationId === this.installationId && resource.siteId === siteId,
          'PERMISSION_DENIED');
        return addExtension({ parse: 0, url: payload.id }, { resources: [{
          kind: 'pan', id: payload.id, provider: resource.provider,
          installationId: this.installationId, siteId,
        }] });
      }
      const method = this.capabilities[siteId].methods[operation] ?? 'POST';
      const value = await this.contextScope.run(scope,
        () => this.read(site.api + '/' + encodeURIComponent(operation), payload, scope, method));
      scope.assertOpen();
      const output = normalizeCatOutput(value, { settings,
        shares: [...scope.panShares.values()], installationId: this.installationId, siteId,
        mapUrl: value => this.mapProxy(value, siteOrigin) });
      return output;
    } finally {
      try { if (listening) signal.removeEventListener('abort', abort); }
      finally {
        try { scope.close(); }
        finally { this.invocations.delete(scope.requestId); }
      }
    }
  }

  activate() {
    super.activate();
    try { this.onActivate?.(this); }
    catch { /* Lifecycle observation cannot change activation. */ }
  }
  async enhanceM3U8(value, { owned = false, signal = null } = {}) {
    try {
      const url = new URL(value.url);
      let source = url.href;
      const entry = url.hostname === '127.0.0.1' &&
        Number(url.port) === this.aggregatePort &&
        url.pathname.startsWith(`/proxy/${this.installationId}/${this.generation}/`)
          ? this.proxyMappings.get(url.pathname.split('/')[4]) : null;
      if (entry) {
        const id = url.pathname.split('/')[4];
        const prefix = `/proxy/${this.installationId}/${this.generation}/${id}`;
        source = fixedOriginURL(entry.origin,
          url.pathname.slice(prefix.length) || '/', url.search, url.hash).href;
      }
      const text = await this.readText(source, value.header ?? value.headers, 3000, signal);
      const variants = parseM3U8Metadata(text, source)
        .map(variant => ({ ...variant, url: this.mapProxy(variant.url) }));
      if (!variants.length) return value;
      const extension = { media: [...(Array.isArray(value._meow?.media)
        ? value._meow.media : []), { variants }] };
      return owned || value[ownedExtension] ? mergeExtension(value, extension)
        : addExtension(value, extension);
    } catch { return value; }
  }
  endpointsChanged() {
    const endpoints = this.proxyEndpoints();
    const valid = new Set(endpoints.map(endpoint => endpoint.origin));
    for (const [id, entry] of this.proxyMappings)
      if (!valid.has(entry.origin)) {
        this.proxyMappings.delete(id);
        this.pendingProxies.delete(id);
      }
    try { this.onEndpointsChanged?.(this); }
    catch { /* Lifecycle observation cannot change listener state. */ }
  }
  networkEvent(event) {
    try { this.onNetwork?.(event); }
    catch { /* Network observation cannot change script networking. */ }
  }
  endpoint() { return clone(this.services.endpoint()); }
  stats() {
    return { ...super.stats(), networkRequests: this.nativeRequests.size + this.networks.size,
      listeners: [...this.services.listeners].filter(server => server.listening).length,
      nativeHandles: this.node.handles.size,
      cleanupError: this.cleanupError };
  }
  stop(reason = fault('STALE_SESSION')) {
    if (this.stopping) return this.stopping;
    this.stopping = this.stopOwned(reason);
    this.stopping.catch(() => {});
    return this.stopping;
  }
  async stopOwned(reason) {
    let cleanupFailure;
    const failed = error => {
      const failure = safeError(error, 'SERVICE_ERROR');
      cleanupFailure ??= failure;
      this.cleanupError ??= failure.code;
    };
    this.activated = false;
    if (this.live && this.engine && !this.engine.closed) {
      let timer;
      try {
        this.contextScope.run(this.root, () =>
          this.engine.call('catStop', []));
        await Promise.race([
          this.guestStopped.promise,
          new Promise(resolve => { timer = setTimeout(resolve, this.limits.cleanupTimeoutMs); }),
        ]);
      } catch { /* Forced owned-resource cleanup follows. */ }
      finally {
        try { clearTimeout(timer); } catch (error) { failed(error); }
      }
    }
    if (this.live) {
      this.live = false;
      this.ready.reject(reason);
      try { this.root.close(reason); }
      catch (error) { failed(error); }
      try { this.callbacks.clear(); }
      catch (error) { failed(error); }
    }
    for (const request of this.nativeRequests) {
      try { request.destroy(); }
      catch (error) { failed(error); }
    }
    try { await this.node.close(); }
    catch (error) { failed(error); }
    try { this.engine?.close(); }
    catch (error) { failed(error); }
    if (!this.stopNotified) {
      this.stopNotified = true;
      try { this.onStop?.(this); }
      catch (error) { failed(error); }
    }
    if (cleanupFailure) throw cleanupFailure;
  }
}

export function successfulManagement(value) {
  return value && typeof value === 'object' &&
    (Object.hasOwn(value, 'code') ? value.code === 0 || value.code === '0' : value.success === true);
}
