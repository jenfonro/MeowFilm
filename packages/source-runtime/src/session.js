import {
  base64ToBytes, bytesToBase64, hostApiVersion,
} from './capabilities.js';
import {
  countDomNodes, DocumentRegistry, domHTML, htmlToText, isDomNode, parseMarkup,
} from './dom.js';
import {
  sourceClone, sourceFault, sourceIdentifier, sourceObject, sourceRequire, sourceSafeError,
} from './errors.js';
import { SourceHttpClient, trustedCapabilityContext } from './http.js';
import { normalizeSourceDefinition } from './importers.js';
import { CatAdapter } from './cat.js';
import { LegadoAdapter, parseLegadoRequestParts } from './legado.js';
import { QysgAdapter } from './qysg.js';
import { readStaticRuleString, readStaticRuleValues } from './rules.js';
import { transformCapabilityScript } from './script-transform.js';

const adapters = Object.freeze({
  legado: LegadoAdapter,
  qysg: QysgAdapter,
  cat: CatAdapter,
});
const defaults = Object.freeze({
  maximumBytes: 8 * 1024 * 1024,
  requestTimeoutMs: 30_000,
  maximumDocuments: 64,
  maximumNodes: 20_000,
  maximumPages: 32,
  maximumExternalScripts: 8,
  maximumSourceCandidates: 8,
  cleanupTimeoutMs: 2_000,
});
const textEncoder = new TextEncoder();

function jsonBytes(value) {
  let json;
  try { json = JSON.stringify(value); }
  catch { throw sourceFault('INVALID_ARGUMENT'); }
  sourceRequire(typeof json === 'string');
  return textEncoder.encode(json).length;
}

function decodeRequestBody(value) {
  return value && typeof value === 'object' && Object.keys(value).length === 1 &&
    Object.hasOwn(value, '__meowBodyBase64') && typeof value.__meowBodyBase64 === 'string'
    ? base64ToBytes(value.__meowBodyBase64)
    : value;
}

class BoundedState {
  #entries = new Map();
  #bytes = 0;

  constructor(maximumBytes) {
    sourceRequire(Number.isSafeInteger(maximumBytes) && maximumBytes > 0);
    this.maximumBytes = maximumBytes;
  }

  #identity(namespace, key) {
    const name = String(key);
    sourceRequire(textEncoder.encode(name).length <= 4096, 'RESOURCE_LIMIT');
    return JSON.stringify([namespace, name]);
  }

  get(namespace, key, fallback) {
    const entry = this.#entries.get(this.#identity(namespace, key));
    return sourceClone(entry ? entry.value : fallback);
  }

  set(namespace, key, value) {
    const identity = this.#identity(namespace, key);
    const captured = sourceClone(value === undefined ? null : value);
    const bytes = textEncoder.encode(identity).length + jsonBytes(captured);
    const previous = this.#entries.get(identity);
    sourceRequire(this.#bytes - (previous?.bytes ?? 0) + bytes <= this.maximumBytes,
      'RESOURCE_LIMIT');
    this.#entries.set(identity, { value: captured, bytes });
    this.#bytes += bytes - (previous?.bytes ?? 0);
    return sourceClone(captured);
  }

  clear() {
    this.#entries.clear();
    this.#bytes = 0;
  }
}

function guestValue(value) {
  if (isDomNode(value)) {
    return {
      __meowMarkup: domHTML(value, true),
      __meowNodeType: value.nodeType,
    };
  }
  if (Array.isArray(value)) return value.map(guestValue);
  if (value && typeof value === 'object') {
    const result = {};
    for (const [key, child] of Object.entries(value)) result[key] = guestValue(child);
    return result;
  }
  return value;
}

function hydrateHostValue(value, budget) {
  if (Array.isArray(value)) return value.map(child => hydrateHostValue(child, budget));
  if (value && typeof value === 'object') {
    const keys = Object.keys(value);
    const encodedNode = typeof value.__meowMarkup === 'string' &&
      keys.every(key => key === '__meowMarkup' || key === '__meowNodeType');
    if (encodedNode) {
      const bytes = textEncoder.encode(value.__meowMarkup).length;
      sourceRequire(bytes <= budget.bytes, 'RESOURCE_LIMIT');
      sourceRequire(budget.nodes > 0, 'RESOURCE_LIMIT');
      const document = parseMarkup(value.__meowMarkup, budget.nodes);
      budget.nodes -= countDomNodes(document, budget.nodes);
      budget.bytes -= bytes;
      if (Number.isInteger(value.__meowNodeType) && value.__meowNodeType !== 9) {
        return [...(document.childNodes ?? [])]
          .find(node => node.nodeType === value.__meowNodeType) ?? document;
      }
      return document;
    }
    const result = {};
    for (const [key, child] of Object.entries(value)) {
      result[key] = hydrateHostValue(child, budget);
    }
    return result;
  }
  return value;
}

function hostValue(value, maximumNodes, maximumBytes) {
  return hydrateHostValue(value, { nodes: maximumNodes, bytes: maximumBytes });
}

function aborted(signal, fallback = 'CANCELLED') {
  if (!signal?.aborted) return null;
  return sourceSafeError(signal.reason, fallback);
}

function waitFor(promise, signal) {
  const failure = aborted(signal);
  if (failure) return Promise.reject(failure);
  if (!signal) return promise;
  return new Promise((resolve, reject) => {
    const abort = () => reject(aborted(signal));
    signal.addEventListener('abort', abort, { once: true });
    promise.then(resolve, reject).finally(() => signal.removeEventListener('abort', abort)).catch(() => {});
  });
}

function operationSignal(parents, timeoutMs) {
  const controller = new AbortController();
  let timedOut = false;
  const listeners = [];
  for (const parent of parents.filter(Boolean)) {
    const abort = () => {
      if (!controller.signal.aborted) {
        controller.abort(sourceSafeError(parent.reason, 'CANCELLED'));
      }
    };
    if (parent.aborted) abort();
    else {
      parent.addEventListener('abort', abort, { once: true });
      listeners.push([parent, abort]);
    }
  }
  const timer = setTimeout(() => {
    timedOut = true;
    if (!controller.signal.aborted) controller.abort(sourceFault('TIMEOUT'));
  }, timeoutMs);
  return {
    signal: controller.signal,
    timedOut: () => timedOut,
    close() {
      clearTimeout(timer);
      for (const [parent, abort] of listeners) parent.removeEventListener('abort', abort);
    },
  };
}

class SessionKernel {
  constructor({
    source, protocol, capabilities, createSandbox, profile, capabilityScope,
    metadata, limits, onDiagnostic, signal,
  }) {
    Object.assign(this, {
      source, protocol, capabilities, createSandbox, profile, capabilityScope,
      metadata, limits, onDiagnostic, signal,
    });
    this.http = new SourceHttpClient({
      capabilities, profile, scope: capabilityScope, maximumBytes: limits.maximumBytes,
    });
    this.rootRegistry = this.createRegistry();
    this.registries = new Map();
    this.state = new BoundedState(limits.maximumBytes);
    this.virtualRoutes = new Map();
    this.virtualRouteBytes = 0;
    this.pendingVirtualRoutes = new Set();
    this.registeredVirtualRoutes = new Set();
    this.nextRoute = 0;
    this.adapterTasks = new Set();
  }

  createRegistry() {
    return new DocumentRegistry({
      maximumDocuments: this.limits.maximumDocuments,
      maximumNodes: this.limits.maximumNodes,
      maximumBytes: this.limits.maximumBytes,
    });
  }

  setInitialDocument(markup) {
    sourceRequire(this.protocol === 'qysg' && this.initialDocument === undefined);
    const value = String(markup ?? '');
    sourceRequire(textEncoder.encode(value).length <= this.limits.maximumBytes, 'RESOURCE_LIMIT');
    this.initialDocument = value;
  }

  beginInvocation(id) {
    sourceRequire(!this.registries.has(id));
    this.registries.set(id, this.createRegistry());
  }

  endInvocation(id) {
    this.registries.get(id)?.close();
    this.registries.delete(id);
  }

  registry(context = {}) {
    return this.registries.get(context.invocationId) ?? this.rootRegistry;
  }

  diagnostic(event) {
    if (!this.onDiagnostic) return;
    try { this.onDiagnostic(sourceClone(event)); }
    catch { /* Diagnostics never change protocol results. */ }
  }

  startupContext() {
    return this.capabilityContext({ signal: this.signal, startup: true });
  }

  capabilityContext(context = {}) {
    return trustedCapabilityContext(this.capabilityScope, context);
  }

  invocationContext(invocation) {
    return {
      signal: invocation.signal,
      invocationId: invocation.id,
      action: invocation.action,
    };
  }

  trackAdapterTask(promise) {
    this.adapterTasks.add(promise);
    promise.finally(() => this.adapterTasks.delete(promise)).catch(() => {});
    return promise;
  }

  ruleVariableGet(key) {
    return this.state.get('rule', key, '');
  }

  ruleVariableSet(key, value) {
    return this.state.set('rule', key, value);
  }

  async loadSandbox(kind, code, metadata) {
    sourceRequire(!this.sandbox && typeof this.createSandbox === 'function');
    this.sandbox = await this.createSandbox({
      onCapability: (name, input, context) => this.capability(name, input, context),
      onSync: (name, input, context) => this.sync(name, input, context),
      onDiagnostic: event => this.diagnostic(event),
    });
    await this.sandbox.load(kind, code, metadata);
    this.sandbox.activate();
    return this.sandbox.capabilities;
  }

  async invokeGuest(action, payload, invocation) {
    const result = await this.sandbox.invoke(action, guestValue(payload), {
      signal: invocation.signal,
      timeoutMs: invocation.timeoutMs,
      context: {
        invocationId: invocation.id,
        action,
        bookIdentity: invocation.bookIdentity ?? '',
      },
    });
    await this.flushVirtualRoutes(invocation);
    return hostValue(result, this.limits.maximumNodes, this.limits.maximumBytes);
  }

  async evaluateScript(source, bindings, invocation, {
    returnLastExpression = true,
    transform = {},
  } = {}) {
    const transformed = transformCapabilityScript(String(source), {
      ...transform,
      returnLastExpression,
    });
    const result = await this.sandbox.evaluate(transformed, guestValue(bindings), {
      signal: invocation.signal,
      timeoutMs: invocation.timeoutMs,
      context: {
        invocationId: invocation.id,
        action: invocation.action,
        bookIdentity: invocation.bookIdentity ?? '',
      },
    });
    await this.flushVirtualRoutes(invocation);
    return hostValue(result, this.limits.maximumNodes, this.limits.maximumBytes);
  }

  sync(name, input, context) {
    if (name.startsWith('dom.')) {
      return this.registry(context).operation(name.slice(4), input);
    }
    switch (name) {
      case 'legado.ruleValues':
        return guestValue(readStaticRuleValues(
          String(input.rule ?? ''),
          hostValue(input.content, this.limits.maximumNodes, this.limits.maximumBytes),
          { maximumNodes: this.limits.maximumNodes },
        ));
      case 'legado.ruleString':
        return readStaticRuleString(
          String(input.rule ?? ''),
          hostValue(input.content, this.limits.maximumNodes, this.limits.maximumBytes),
          { maximumNodes: this.limits.maximumNodes },
        );
      case 'legado.htmlToText':
        return htmlToText(String(input.value ?? ''), this.limits.maximumNodes);
      case 'legado.requestParts':
        return parseLegadoRequestParts(input.value);
      case 'legado.runtimeInfo':
        return {
          userAgent: String(this.metadata.userAgent ?? 'Mozilla/5.0 MeowfilmSourceRuntime/1'),
          deviceId: String(this.metadata.deviceId ?? ''),
          appVariant: String(this.metadata.appVariant ?? ''),
          platform: String(this.metadata.platform ?? 'unknown'),
        };
      case 'legado.variableSet':
        this.state.set('rule', input.key, input.value);
        return true;
      case 'legado.variableGet':
        return this.state.get('rule', input.key, null);
      case 'legado.sourceGet':
        return this.state.get('source', input.key, null);
      case 'legado.sourceSet':
        this.state.set('source', input.key, input.value);
        return true;
      case 'legado.sourceVariableGet':
        return String(this.source.variable ?? this.source.bookSourceVariable ?? '');
      case 'legado.sourceVariableSet': {
        const value = String(input.value ?? '');
        sourceRequire(textEncoder.encode(value).length <=
          this.limits.maximumBytes, 'RESOURCE_LIMIT');
        this.source.variable = value;
        return value;
      }
      case 'legado.bookVariableGet': {
        const key = JSON.stringify([context.bookIdentity ?? '', String(input.key ?? 'custom')]);
        return this.state.get('book', key, null);
      }
      case 'legado.bookVariableSet': {
        const key = JSON.stringify([context.bookIdentity ?? '', String(input.key ?? 'custom')]);
        return this.state.set('book', key, input.value);
      }
      case 'common.url': {
        let url;
        try { url = new URL(String(input.value ?? ''), input.base ? String(input.base) : undefined); }
        catch { throw sourceFault('INVALID_ARGUMENT'); }
        if (input.set !== undefined) {
          sourceObject(input.set);
          const name = String(input.set.name ?? '');
          sourceRequire([
            'href', 'protocol', 'username', 'password', 'hostname', 'port',
            'host', 'pathname', 'search', 'hash',
          ].includes(name));
          try { url[name] = String(input.set.value ?? ''); }
          catch { throw sourceFault('INVALID_ARGUMENT'); }
        }
        return {
          href: url.href,
          protocol: url.protocol,
          username: url.username,
          password: url.password,
          hostname: url.hostname,
          port: url.port,
          host: url.host,
          origin: url.origin,
          pathname: url.pathname,
          search: url.search,
          hash: url.hash,
        };
      }
      case 'qysg.document':
        sourceRequire(this.protocol === 'qysg');
        return this.registry(context).parse(this.initialDocument ?? '<!doctype html><html><body></body></html>');
      case 'cat.virtualUrl': {
        const route = `route-${++this.nextRoute}`;
        const metadata = {
          siteKey: String(input.siteKey ?? ''),
          url: String(input.url ?? ''),
          headers: sourceClone(input.headers ?? {}),
        };
        const bytes = textEncoder.encode(route).length + jsonBytes(metadata);
        sourceRequire(this.virtualRouteBytes + bytes <= this.limits.maximumBytes,
          'RESOURCE_LIMIT');
        this.virtualRoutes.set(route, { metadata, bytes });
        this.virtualRouteBytes += bytes;
        this.pendingVirtualRoutes.add(route);
        return `meow-source://${encodeURIComponent(this.profile)}/${route}`;
      }
      default: throw sourceFault('UNSUPPORTED_RULE');
    }
  }

  async capability(name, input, context) {
    const trustedContext = this.capabilityContext(context);
    const call = (capability, value) => this.capabilities.call(capability, {
      version: hostApiVersion, ...value,
    }, trustedContext);
    switch (name) {
      case 'legado.request': return this.http.request({
        ...input,
        body: decodeRequestBody(input.body),
        data: decodeRequestBody(input.data),
        baseUrl: this.source.bookSourceUrl,
      }, trustedContext);
      case 'legado.cookieGet':
        return call('cookie.get', {
          profile: `${this.profile}:http`, url: String(input.url),
        });
      case 'legado.cookieSet':
        return call('cookie.set', {
          profile: `${this.profile}:http`, url: String(input.url),
          values: [String(input.value)],
        });
      case 'legado.cookieClear':
        return call('cookie.clear', {
          profile: `${this.profile}:http`, url: String(input.url),
        });
      case 'legado.storageGet':
      case 'cat.storageGet':
        return call('storage.get', {
          profile: `${this.profile}:storage`, key: String(input.key),
        });
      case 'legado.storageSet':
      case 'cat.storageSet':
        return call('storage.set', {
          profile: `${this.profile}:storage`, key: String(input.key), value: input.value,
        });
      case 'legado.storageDelete':
      case 'cat.storageDelete':
        return call('storage.delete', {
          profile: `${this.profile}:storage`, key: String(input.key),
        });
      case 'legado.web':
        return call('web.interact', {
          profile: `${this.profile}:web`, mode: String(input.mode ?? 'browser'), request: input,
        });
      case 'cat.request': {
        const options = sourceObject(input.options ?? {});
        const response = await this.http.request({
          url: input.url,
          method: options.method,
          headers: options.headers,
          body: decodeRequestBody(options.body ?? options.data),
          timeout: options.timeout,
          followRedirect: options.redirect ?? options.followRedirect,
          follow_max: options.follow_max,
          charset: options.charset,
          baseUrl: this.source.baseUrl ?? this.source.url,
        }, trustedContext);
        return {
          code: response.statusCode,
          status: response.statusCode,
          headers: response.headers,
          content: response.data,
          data: response.data,
          body: response.body,
          bodyBase64: response.bodyBase64,
          url: response.url,
        };
      }
      case 'bridge.call': return this.qysgBridge(String(input.name), input.args ?? [], context);
      default: throw sourceFault('CAPABILITY_UNAVAILABLE');
    }
  }

  async qysgBridge(name, args, context) {
    const trustedContext = this.capabilityContext(context);
    const call = (capability, value) => this.capabilities.call(capability, {
      version: hostApiVersion, ...value,
    }, trustedContext);
    switch (name) {
      case 'CookieJar': return true;
      case 'http': {
        const [method, url, body, headers, followRedirects, contentType] = args;
        return this.http.request({
          method, url, body: decodeRequestBody(body), headers, followRedirects, contentType,
          baseUrl: this.source.bookSourceUrl,
        }, trustedContext);
      }
      case 'cookie.get':
        return call('cookie.get', {
          profile: `${this.profile}:http`, url: String(args[0] ?? this.source.bookSourceUrl),
        });
      case 'cookie.set':
        return call('cookie.set', {
          profile: `${this.profile}:http`, url: String(args[0] ?? this.source.bookSourceUrl),
          values: [String(args[1] ?? '')],
        });
      case 'cookie.setCookie':
      case 'cookie.setcookie':
        return call('cookie.set', {
          profile: `${this.profile}:http`, url: String(args[0] ?? this.source.bookSourceUrl),
          values: [`${String(args[1] ?? '')}=${String(args[2] ?? '')}`],
        });
      case 'cookie.getCookie': {
        const header = await call('cookie.get', {
          profile: `${this.profile}:http`, url: String(args[0] ?? this.source.bookSourceUrl),
        });
        const key = String(args[1] ?? '');
        const pair = String(header).split(';').map(value => value.trim())
          .find(value => value.startsWith(`${key}=`));
        return pair ? pair.slice(key.length + 1) : '';
      }
      case 'cookie': {
        const operation = String(args[0] ?? '').toLowerCase();
        const url = String(args[1] ?? this.source.bookSourceUrl);
        if (operation === 'get') {
          return call('cookie.get', { profile: `${this.profile}:http`, url });
        }
        if (operation === 'set') {
          return call('cookie.set', {
            profile: `${this.profile}:http`, url, values: [String(args[2] ?? '')],
          });
        }
        if (operation === 'remove' || operation === 'clear') {
          return call('cookie.clear', { profile: `${this.profile}:http`, url });
        }
        throw sourceFault('INVALID_ARGUMENT');
      }
      case 'cookie.remove':
        return call('cookie.clear', {
          profile: `${this.profile}:http`, url: String(args[0] ?? this.source.bookSourceUrl),
        });
      case 'cache.get':
      case 'cacheGet':
        return call('storage.get', {
          profile: `${this.profile}:storage`, key: String(args[0] ?? name),
        });
      case 'cache.set':
      case 'cacheSet':
        return call('storage.set', {
          profile: `${this.profile}:storage`, key: String(args[0] ?? name), value: args[1],
        });
      case 'cache.remove':
      case 'cacheRemove':
        return call('storage.delete', {
          profile: `${this.profile}:storage`, key: String(args[0] ?? name),
        });
      case 'cache.allget':
        return call('storage.get', {
          profile: `${this.profile}:storage`, key: `all:${String(args[0] ?? '')}`,
        });
      case 'cache.allset':
        return call('storage.set', {
          profile: `${this.profile}:storage`, key: `all:${String(args[0] ?? '')}`,
          value: args[1],
        });
      case 'cache.allremove':
        return call('storage.delete', {
          profile: `${this.profile}:storage`, key: `all:${String(args[0] ?? '')}`,
        });
      case 'getLoginInfo':
        return call('storage.get', {
          profile: `${this.profile}:storage`, key: 'loginInfo',
        });
      case 'putLoginInfo':
        return call('storage.set', {
          profile: `${this.profile}:storage`, key: 'loginInfo', value: args[0],
        });
      case 'getbookVariable':
        return call('storage.get', {
          profile: `${this.profile}:storage`, key: `book:${String(args[0] ?? '')}`,
        });
      case 'setbookVariable':
        return call('storage.set', {
          profile: `${this.profile}:storage`, key: `book:${String(args[0] ?? '')}`,
          value: args[1],
        });
      case 'htmlToText':
        return htmlToText(String(args[0] ?? ''), this.limits.maximumNodes);
      case 'base64encode':
        return bytesToBase64(new TextEncoder().encode(String(args[0] ?? '')));
      case 'base64decode':
        return new TextDecoder().decode(base64ToBytes(String(args[0] ?? '')));
      case 'utf8ToGbkUrlEncoded': {
        const body = await call('encoding.encode', {
          encoding: 'gbk', text: String(args[0] ?? ''),
        });
        return [...base64ToBytes(body)]
          .map(byte => `%${byte.toString(16).toUpperCase().padStart(2, '0')}`).join('');
      }
      case 'buildNumber': return Number(this.metadata.buildNumber ?? 1);
      case 'version': return String(this.metadata.version ?? '1.0.0');
      case 'getversion': return String(this.metadata.version ?? '1.0.0');
      case 'id': return String(this.metadata.deviceId ?? '');
      case 'device': return String(this.metadata.platform ?? 'unknown');
      case 'getDevice': return String(this.metadata.platform ?? 'unknown');
      case 'getDeviceid': return String(this.metadata.deviceId ?? '');
      case 'getWebViewUA': return String(this.metadata.userAgent ?? 'Mozilla/5.0 MeowfilmSourceRuntime/1');
      case 'getWidth': return Number(this.metadata.width ?? 0);
      case 'getHeight': return Number(this.metadata.height ?? 0);
      case 'getsourceurl': return String(this.source.bookSourceUrl ?? '');
      case 'getsearch': return Number(this.metadata.searchState ?? -1);
      case 'getLoginUser':
      case 'getdurChapterIndex':
      case 'getReadBook':
      case 'putLyric':
      case 'toSimplified':
      case 'toTraditional':
        return call('web.interact', {
          profile: `${this.profile}:web`, mode: name, request: { arguments: args },
        });
      case 'log':
      case 'text':
      case 'showToast':
      case 'showLongToast':
        this.diagnostic({ protocol: 'qysg', name });
        return true;
      case 'webview':
      case 'webviewajax':
      case 'webViewGetSource':
      case 'startBrowser':
      case 'startBrowserWithShouldOverrideUrlLoading':
      case 'startBrowserDp':
      case 'startBrowserDpwithHtml':
      case 'startBrowserDpWithShouldOverrideUrlLoading':
      case 'getVerificationCode':
      case 'openurl':
      case 'addbook':
      case 'searchbook':
      case 'showvideoplay':
      case 'refreshContent':
      case 'refreshExplore':
      case 'setHtml':
      case 'back':
      case 'voice':
      case 'showsvg':
      case 'getsvg':
      case 'addsvg':
        return call('web.interact', {
          profile: `${this.profile}:web`, mode: name, request: { arguments: args },
        });
      default: throw sourceFault('CAPABILITY_UNAVAILABLE');
    }
  }

  resolveVirtual(value) {
    const prefix = `meow-source://${encodeURIComponent(this.profile)}/`;
    if (typeof value !== 'string' || !value.startsWith(prefix)) return null;
    return sourceClone(this.virtualRoutes.get(value.slice(prefix.length))?.metadata ?? null);
  }

  dropVirtualRoute(route) {
    this.pendingVirtualRoutes.delete(route);
    const entry = this.virtualRoutes.get(route);
    if (!entry) return;
    this.virtualRoutes.delete(route);
    this.virtualRouteBytes -= entry.bytes;
  }

  async flushVirtualRoutes(invocation) {
    if (!this.pendingVirtualRoutes.size) return;
    const routes = [...this.pendingVirtualRoutes];
    const attempted = [];
    try {
      for (const route of routes) {
        attempted.push(route);
        await this.capabilities.call('virtual.register', {
          version: hostApiVersion,
          profile: this.profile,
          route,
          metadata: this.virtualRoutes.get(route)?.metadata,
        }, this.capabilityContext({
          signal: invocation.signal,
          invocationId: invocation.id,
          action: invocation.action,
        }));
        this.pendingVirtualRoutes.delete(route);
        this.registeredVirtualRoutes.add(route);
      }
    } catch (error) {
      for (const route of attempted) this.registeredVirtualRoutes.add(route);
      const rollback = await Promise.allSettled(attempted.map(route =>
        this.capabilities.call('virtual.unregister', {
          version: hostApiVersion,
          profile: this.profile,
          route,
        }, this.capabilityContext({
          signal: invocation.signal,
          invocationId: invocation.id,
          action: invocation.action,
        }))));
      for (let index = 0; index < attempted.length; index++) {
        const route = attempted[index];
        if (rollback[index].status === 'fulfilled' && rollback[index].value === true) {
          this.registeredVirtualRoutes.delete(route);
          this.dropVirtualRoute(route);
        } else {
          this.pendingVirtualRoutes.delete(route);
        }
      }
      for (const route of routes.slice(attempted.length)) this.dropVirtualRoute(route);
      throw error;
    }
  }

  async close() {
    this.sandbox?.close(sourceFault('STALE_SESSION'));
    const cleanup = new AbortController();
    const timer = setTimeout(() => cleanup.abort(sourceFault('TIMEOUT')), this.limits.cleanupTimeoutMs);
    const tasks = [...this.registeredVirtualRoutes].map(route => this.trackAdapterTask(
      this.capabilities.call('virtual.unregister', {
        version: hostApiVersion, profile: this.profile, route,
      }, this.capabilityContext({ signal: cleanup.signal })),
    ));
    let results;
    const settled = Promise.allSettled(tasks).then(value => {
      results = value;
      return value;
    });
    try {
      await Promise.race([
        settled,
        new Promise(resolve => cleanup.signal.addEventListener('abort', resolve, { once: true })),
      ]);
    } finally {
      clearTimeout(timer);
      cleanup.abort(sourceFault('CANCELLED'));
    }
    this.registeredVirtualRoutes.clear();
    this.pendingVirtualRoutes.clear();
    this.virtualRoutes.clear();
    this.virtualRouteBytes = 0;
    this.rootRegistry.close();
    for (const registry of this.registries.values()) registry.close();
    this.registries.clear();
    this.state.clear();
    await this.drain();
    if (!Array.isArray(results) || results.some(result =>
      result.status === 'rejected' || result.value !== true)) {
      throw sourceFault('CAPABILITY_UNAVAILABLE');
    }
  }

  async drain() {
    const deadline = Date.now() + this.limits.cleanupTimeoutMs;
    while (this.adapterTasks.size && Date.now() < deadline) {
      const remaining = Math.max(1, deadline - Date.now());
      let timer;
      try {
        await Promise.race([
          Promise.allSettled([...this.adapterTasks]),
          new Promise(resolve => { timer = setTimeout(resolve, remaining); }),
        ]);
      } finally { clearTimeout(timer); }
    }
    await this.sandbox?.drain();
  }

  stats() {
    const stats = this.sandbox?.stats() ?? {
      live: false, invocations: 0, timers: 0, networkRequests: 0, pendingAdapterTasks: 0,
    };
    return { ...stats, pendingAdapterTasks: stats.pendingAdapterTasks + this.adapterTasks.size };
  }
}

export class SourceSession {
  constructor({
    installationId,
    generation = 1,
    source,
    protocol,
    capabilities,
    createSandbox,
    metadata = {},
    limits = {},
    onDiagnostic,
  } = {}) {
    sourceIdentifier(installationId);
    sourceRequire(Number.isSafeInteger(generation) && generation > 0);
    sourceRequire(capabilities?.version === hostApiVersion && typeof capabilities.call === 'function');
    sourceRequire(typeof createSandbox === 'function');
    sourceObject(metadata);
    sourceObject(limits);
    sourceRequire(Object.keys(limits).every(key => Object.hasOwn(defaults, key)));
    this.installationId = installationId;
    this.generation = generation;
    this.input = sourceClone(source);
    this.requestedProtocol = protocol;
    this.capabilitiesClient = capabilities;
    this.createSandbox = createSandbox;
    this.metadata = sourceClone(metadata);
    this.limits = Object.freeze({ ...defaults, ...limits });
    for (const value of Object.values(this.limits)) sourceRequire(Number.isSafeInteger(value) && value > 0);
    sourceRequire(onDiagnostic === undefined || typeof onDiagnostic === 'function');
    this.onDiagnostic = onDiagnostic;
    this.lifetime = new AbortController();
    this.live = true;
    this.active = false;
    this.tail = Promise.resolve();
    this.nextInvocation = 0;
  }

  async start() {
    sourceRequire(this.live && !this.adapter, 'STALE_SESSION');
    const startup = operationSignal([this.lifetime.signal], this.limits.requestTimeoutMs);
    try {
      const normalized = normalizeSourceDefinition(this.input, this.requestedProtocol);
      const Adapter = adapters[normalized.protocol];
      sourceRequire(Adapter, 'UNSUPPORTED_PROTOCOL');
      this.protocol = normalized.protocol;
      this.importedProtocol = normalized.importedProtocol;
      this.source = normalized.source;
      this.kernel = new SessionKernel({
        source: this.source,
        protocol: this.protocol,
        capabilities: this.capabilitiesClient,
        createSandbox: this.createSandbox,
        profile: `source:${this.installationId}:${this.generation}`,
        capabilityScope: {
          installationId: this.installationId,
          generation: this.generation,
          protocol: this.protocol,
          siteId: 'source',
        },
        metadata: this.metadata,
        limits: this.limits,
        onDiagnostic: this.onDiagnostic,
        signal: startup.signal,
      });
      this.adapter = new Adapter({
        source: this.source,
        kernel: this.kernel,
        limits: this.limits,
        importedProtocol: this.importedProtocol,
      });
      const task = this.kernel.trackAdapterTask(
        Promise.resolve().then(() => this.adapter.start()),
      );
      await waitFor(task, startup.signal);
      if (startup.timedOut()) throw sourceFault('TIMEOUT');
      this.actions = Object.freeze([...this.adapter.actions]);
      return this.describe();
    } catch (error) {
      const failure = startup.timedOut() ? sourceFault('TIMEOUT') : sourceSafeError(error);
      await this.close();
      throw failure;
    } finally {
      startup.close();
    }
  }

  activate() {
    sourceRequire(this.live && this.adapter, 'STALE_SESSION');
    this.active = true;
  }

  describe() {
    sourceRequire(this.adapter);
    return {
      protocol: this.protocol,
      importedProtocol: this.importedProtocol,
      actions: [...this.actions],
    };
  }

  async invoke(action, payload = {}, {
    signal,
    timeoutMs = this.limits.requestTimeoutMs,
  } = {}) {
    sourceRequire(this.live, 'STALE_SESSION');
    sourceRequire(this.active, 'PERMISSION_DENIED');
    sourceRequire(typeof action === 'string' && this.actions.includes(action), 'UNSUPPORTED_ACTION');
    sourceObject(payload);
    sourceRequire(signal === undefined || signal instanceof AbortSignal);
    sourceRequire(Number.isSafeInteger(timeoutMs) && timeoutMs > 0 &&
      timeoutMs <= this.limits.requestTimeoutMs);
    const operation = operationSignal([signal, this.lifetime.signal], timeoutMs);
    const prior = this.tail.catch(() => {});
    let task;
    try {
      task = (async () => {
        await waitFor(prior, operation.signal);
        const failure = aborted(operation.signal, operation.timedOut() ? 'TIMEOUT' : 'CANCELLED');
        if (failure) throw failure;
        const id = ++this.nextInvocation;
        const invocation = {
          id,
          action,
          signal: operation.signal,
          timeoutMs,
          bookIdentity: String(payload.bookUrl ?? payload.tocUrl ?? payload.url ?? ''),
        };
        this.kernel.beginInvocation(id);
        try {
          const value = await this.adapter.invoke(action, sourceClone(payload), invocation);
          if (operation.timedOut()) throw sourceFault('TIMEOUT');
          const cancelled = aborted(operation.signal);
          if (cancelled) throw cancelled;
          return sourceClone(value ?? null);
        } finally { this.kernel.endInvocation(id); }
      })();
      this.tail = task.catch(() => {});
      this.kernel.trackAdapterTask(task);
      return await waitFor(task, operation.signal);
    } catch (error) {
      if (operation.timedOut()) throw sourceFault('TIMEOUT');
      throw sourceSafeError(error);
    } finally {
      operation.close();
    }
  }

  async close() {
    if (!this.live) return;
    this.live = false;
    this.active = false;
    this.lifetime.abort(sourceFault('STALE_SESSION'));
    await this.kernel?.close();
  }

  async drain() {
    await this.kernel?.drain();
  }

  stats() {
    return this.kernel?.stats() ?? {
      live: false, invocations: 0, timers: 0, networkRequests: 0, pendingAdapterTasks: 0,
    };
  }
}
