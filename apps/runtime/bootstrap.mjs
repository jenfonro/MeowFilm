import { performance } from 'node:perf_hooks';
import { BridgeError, HTTP_PROTOCOL, createHttpServer, validateSession } from '@meowfilm/runtime-bridge';
import { getStorageDiagnostics, setStorageObserver } from '@meowfilm/storage';
import { contracts } from './contracts.mjs';
import { createNodeDebug } from './debug.mjs';

const invalid = () => { throw new BridgeError('INVALID_REQUEST'); };
const publicCodes = new Set(['INVALID_REQUEST', 'METHOD_NOT_FOUND', 'PERMISSION_DENIED',
  'CLOSED', 'CANCELLED', 'TIMEOUT', 'INVALID_RESPONSE']);
const validCode = code => typeof code === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(code);
const success = value => ({ ok: true, contract: HTTP_PROTOCOL, value });
const failureCode = id => ({ music: 'SCRIPT_FAILURE', source: 'SOURCE_FAILURE',
  cat: 'SCRIPT_EXCEPTION', pan: 'PAN_FAILURE', tmdb: 'UPSTREAM_FORMAT', douban: 'UPSTREAM_FORMAT',
  film: 'UPSTREAM_ERROR', netease: 'SDK_FAILURE', 'music-platform': 'INVALID_RESPONSE' })[id];
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const storageObserverStack = [];
function claimStorageObserver(observer) {
  storageObserverStack.push(observer);
  setStorageObserver(observer);
}
function releaseStorageObserver(observer) {
  const index = storageObserverStack.lastIndexOf(observer);
  if (index < 0) return;
  storageObserverStack.splice(index, 1);
  setStorageObserver(storageObserverStack.at(-1) ?? null);
}

/** Lifecycle session configuration; no platform bootstrap or persistent identity. */
export function createAppRuntime({ sessionId,
  product = 'aggregate', modules, assemble, settingsChanged, debug: rawDebug } = {}) {
  validateSession({ sessionId });
  if (typeof product !== 'string' || !/^[a-z][a-z0-9-]{0,63}$(?![\s\S])/.test(product) ||
      assemble !== undefined && (modules !== undefined || typeof assemble !== 'function') ||
      settingsChanged !== undefined && typeof settingsChanged !== 'function') invalid();
  const debug = rawDebug !== undefined ? createNodeDebug(rawDebug) : null;
  if (debug) claimStorageObserver(debug);
  let disposed = false, disposal, shutdown, closing;
  const dispose = () => disposal ??= (async () => {
    disposed = true;
    const startedAt = debug ? performance.now() : 0;
    const list = Array.isArray(modules) ? [...new Set(modules)] : [];
    // Close consumer modules before their dependencies so final watch reports
    // and in-flight requests drain cleanly while provider modules stay live.
    const stage1 = list.filter(module => module?.id === 'film' || module?.id === 'music-platform');
    const stage3 = list.filter(module => ['pan', 'tmdb', 'douban', 'netease'].includes(module?.id));
    const staged = new Set([...stage1, ...stage3]);
    const stage2 = list.filter(module => !staged.has(module));
    const results = [];
    for (const group of [stage1, stage2, stage3]) {
      if (!group.length) continue;
      results.push(...await Promise.allSettled(group.map(module => Promise.resolve().then(() => module?.close?.()))));
    }
    if (debug) {
      const failed = results.filter(result => result.status === 'rejected' || result.value?.ok === false);
      debug.emit({
        level: failed.length ? 'error' : 'info',
        cat: 'runtime-node',
        sub: 'runtime.dispose',
        event: failed.length ? 'runtime.dispose.error' : 'runtime.dispose.end',
        durationMs: performance.now() - startedAt,
        data: { moduleCount: results.length, failedCount: failed.length },
        error: failed[0]?.status === 'rejected' ? failed[0].reason : undefined,
      });
      releaseStorageObserver(debug);
    }
    if (results.some(result => result.status === 'rejected' || result.value?.ok === false)) {
      throw new BridgeError('INVALID_RESPONSE');
    }
  })();
  const descriptors = [], methods = new Map(), owners = new Map();
  try {
    if (assemble) modules = assemble();
    if (!Array.isArray(modules) || !modules.length) invalid();
    for (const module of modules) {
      const contract = contracts[module?.id];
      if (!contract || owners.has(module.id) || typeof module.close !== 'function' ||
          !module.methods || typeof module.methods !== 'object') invalid();
      const entries = Object.entries(module.methods);
      for (const [name, handler] of entries) {
        if (!name.startsWith(contract.prefix) || typeof handler !== 'function' || methods.has(name)) invalid();
        methods.set(name, handler);
      }
      owners.set(module.id, module);
      descriptors.push({ id: module.id, version: module.version, contract: module.contract,
        methods: entries.map(([name]) => name).sort() });
    }
    if (debug) {
      debug.emit({
        level: 'info',
        cat: 'runtime-node',
        sub: 'runtime.assembly',
        event: 'runtime.assembled',
        data: { product, sessionId, modules: descriptors.map(item => ({ id: item.id, contract: item.contract, version: item.version, methodCount: item.methods.length })) },
      });
    }
  } catch (error) {
    if (debug) {
      debug.emit({ level: 'error', cat: 'runtime-node', sub: 'runtime.assembly', event: 'runtime.assembly.error', data: { product, sessionId }, error });
    }
    void dispose().catch(() => {});
    throw error;
  }
  function collectModuleDiagnostics() {
    const out = {};
    for (const [id, module] of owners.entries()) {
      if (typeof module.diagnostics !== 'function') {
        out[id] = { contract: module.contract, version: module.version };
        continue;
      }
      try {
        out[id] = module.diagnostics();
      } catch (error) {
        out[id] = { error: error?.message ?? String(error) };
      }
    }
    try {
      out.storage = getStorageDiagnostics();
    } catch (error) {
      out.storage = { error: error?.message ?? String(error) };
    }
    return out;
  }
  function wrapRoute(route, meta) {
    if (!debug || !route) return route;
    const originalHandler = route.handler;
    return {
      ...route,
      handler: async (input, context) => {
        const opId = context?.debugOpId || context?.requestId;
        const traceId = context?.debugTraceId;
        const startedAt = performance.now();
        return debug.runWithContext({ opId, traceId }, async () => {
          const startData = {
            method: meta.httpMethod,
            path: meta.path,
            ...(meta.search ? { query: meta.search } : {}),
            requestId: context?.requestId,
            ...(meta.moduleId ? { moduleId: meta.moduleId } : {}),
            ...(meta.rpcMethod ? { rpcMethod: meta.rpcMethod } : {}),
            ...(meta.inputSummary ? meta.inputSummary(input) : input !== null && input !== undefined ? { input } : {}),
          };
          debug.emit({ level: 'debug', cat: 'runtime-bridge', sub: meta.sub, event: 'http.request', data: startData });
          try {
            const reply = await originalHandler(input, context);
            const durationMs = performance.now() - startedAt;
            if (route.responseType === 'stream') {
              if (reply?.ok === false) {
                debug.emit({
                  level: 'warn',
                  cat: 'runtime-bridge',
                  sub: meta.sub,
                  event: 'http.stream.rejected',
                  durationMs,
                  data: { method: meta.httpMethod, path: meta.path, code: reply.code, contract: reply.contract },
                });
              } else {
                debug.emit({
                  level: 'debug',
                  cat: 'runtime-bridge',
                  sub: meta.sub,
                  event: 'http.stream.open',
                  durationMs,
                  data: { method: meta.httpMethod, path: meta.path, status: reply?.status, headers: reply?.headers },
                });
                const body = reply?.body;
                if (body && typeof body.once === 'function') {
                  const contentType = String(reply?.headers?.['content-type'] ?? reply?.headers?.['Content-Type'] ?? '');
                  const isSse = contentType.toLowerCase().startsWith('text/event-stream');
                  let sseEvents = 0;
                  if (isSse && typeof body.push === 'function') {
                    const originalPush = body.push.bind(body);
                    body.push = (chunk, encoding) => {
                      if (chunk !== null && chunk !== undefined) {
                        const text = Buffer.isBuffer(chunk)
                          ? chunk.toString('utf8')
                          : typeof chunk === 'string'
                            ? chunk
                            : Buffer.from(chunk).toString('utf8');
                        debug.emit({
                          level: 'debug',
                          cat: 'runtime-bridge',
                          sub: meta.sub,
                          event: 'http.sse.event',
                          opId,
                          traceId,
                          data: { path: meta.path, index: ++sseEvents, chunk: text },
                        });
                      }
                      return originalPush(chunk, encoding);
                    };
                  }
                  let streamDone = false;
                  const finishStream = (reason, streamError) => {
                    if (streamDone) return;
                    streamDone = true;
                    debug.emit({
                      level: streamError ? 'warn' : 'debug',
                      cat: 'runtime-bridge',
                      sub: meta.sub,
                      event: streamError ? 'http.stream.error' : 'http.stream.close',
                      opId,
                      traceId,
                      durationMs: performance.now() - startedAt,
                      data: {
                        method: meta.httpMethod,
                        path: meta.path,
                        reason,
                        ...(isSse ? { eventCount: sseEvents } : {}),
                      },
                      error: streamError ?? null,
                    });
                  };
                  body.once('end', () => finishStream('end'));
                  body.once('error', err => finishStream('error', err));
                  body.once('close', () => finishStream('close'));
                }
              }
              return reply;
            }
            debug.emit({
              level: reply?.ok === false ? 'warn' : 'debug',
              cat: 'runtime-bridge',
              sub: meta.sub,
              event: 'http.response',
              durationMs,
              data: {
                method: meta.httpMethod,
                path: meta.path,
                ...(meta.rpcMethod ? { rpcMethod: meta.rpcMethod } : {}),
                ok: reply?.ok,
                ...(reply?.ok === false ? { code: reply.code } : { value: reply?.value }),
              },
            });
            return reply;
          } catch (error) {
            debug.emit({
              level: 'error',
              cat: 'runtime-bridge',
              sub: meta.sub,
              event: 'http.error',
              durationMs: performance.now() - startedAt,
              data: { method: meta.httpMethod, path: meta.path, ...(meta.rpcMethod ? { rpcMethod: meta.rpcMethod } : {}) },
              error,
            });
            throw error;
          }
        });
      },
    };
  }
  function resolveDebugRoute(method, url, headers) {
    if (!debug || url.search) return;
    const hasJsonContent = headers?.['content-type'] === 'application/json';
    const optionalJsonContent = hasJsonContent ? {
      contentType: 'application/json',
      validate(input) { if (!object(input)) invalid(); },
    } : {};
    const withCtx = fn => (input, context) => debug.runWithContext(
      { opId: context?.debugOpId, traceId: context?.debugTraceId },
      () => fn(input, context),
    );
    if (method === 'GET' && url.pathname === '/v1/debug/diagnostics') {
      return {
        contract: HTTP_PROTOCOL,
        handler: withCtx(() => success(debug.getDiagnostics({
          activeRequests: server.state.active,
          moduleDiagnostics: collectModuleDiagnostics(),
        }))),
      };
    }
    if (method === 'POST' && url.pathname === '/v1/debug/config') {
      return {
        contract: HTTP_PROTOCOL,
        contentType: 'application/json',
        validate(input) { debug.validateConfigPatch(input); },
        handler: withCtx(input => success(debug.updateConfig(input))),
      };
    }
    if (method === 'POST' && url.pathname === '/v1/debug/gc') {
      return {
        contract: HTTP_PROTOCOL,
        ...optionalJsonContent,
        handler: withCtx(() => success(debug.runGc({
          clearCaches() {
            const cleared = {};
            for (const [id, module] of owners.entries()) {
              if (typeof module.clearMemoryCache !== 'function') continue;
              try { cleared[id] = module.clearMemoryCache(); }
              catch (error) { cleared[id] = { error: error?.message ?? String(error) }; }
            }
            return cleared;
          },
        }))),
      };
    }
    if (method === 'POST' && url.pathname === '/v1/debug/heapsnapshot') {
      return {
        contract: HTTP_PROTOCOL,
        ...optionalJsonContent,
        handler: withCtx(() => success(debug.writeHeapSnapshot())),
      };
    }
    if (method === 'POST' && url.pathname === '/v1/debug/cpu-profile') {
      return {
        contract: HTTP_PROTOCOL,
        ...(hasJsonContent ? {
          contentType: 'application/json',
          validate(input) {
            if (!object(input)) invalid();
            if (input.durationMs !== undefined &&
                (!Number.isSafeInteger(input.durationMs) || input.durationMs < 500 || input.durationMs > 60000)) invalid();
          },
        } : {}),
        handler: withCtx(async (input, context) => {
          const durationMs = input?.durationMs ?? 5000;
          return success(await debug.captureCpuProfile({ durationMs, signal: context?.signal }));
        }),
      };
    }
  }
  let server;
  try {
    server = createHttpServer({ sessionId, resolve: ({ method, url, headers }) => {
    if (debug && url.pathname.startsWith('/v1/debug/')) {
      return resolveDebugRoute(method, url, headers);
    }
    if (method === 'GET' && url.pathname === '/v1/describe' && !url.search) {
      return wrapRoute({ contract: HTTP_PROTOCOL,
        handler: () => success({ protocol: HTTP_PROTOCOL, product, modules: descriptors }) },
      { httpMethod: method, path: url.pathname, sub: 'runtime.describe' });
    }
    if (method === 'POST' && url.pathname === '/v1/settings/changed' && !url.search && settingsChanged) {
      return wrapRoute({ contract: HTTP_PROTOCOL, contentType: 'application/json',
        validate(input) {
          if (input === null || typeof input !== 'object' || Array.isArray(input) ||
              typeof input.block !== 'string' ||
              !/^[a-z][a-z0-9.-]{0,63}$(?![\s\S])/.test(input.block)) invalid();
        },
        handler: async input => {
          try {
            if (disposed) throw new BridgeError('CLOSED');
            await settingsChanged(input.block);
            return success({ notified: true });
          } catch (error) {
            if (debug) {
              debug.emit({ level: 'error', cat: 'runtime-node', sub: 'settings.changed',
                event: 'settings.changed.error', data: { block: input?.block }, error });
            }
            return { ok: false, contract: HTTP_PROTOCOL,
              code: publicCodes.has(error?.code) ? error.code : 'INVALID_RESPONSE' };
          }
        } }, { httpMethod: method, path: url.pathname, sub: 'runtime.settings' });
    }
    if (method === 'POST' && url.pathname === '/v1/close' && !url.search) {
      return wrapRoute({ contract: HTTP_PROTOCOL, contentType: 'application/json',
        validate(input) {
          if (input === null || typeof input !== 'object' || Array.isArray(input)) invalid();
        }, handler: async (_input, context) => {
        await (shutdown ??= (async () => {
          const drained = server.abortActive(context.requestId);
          const results = await Promise.allSettled([dispose(), drained]);
          if (results.some(result => result.status === 'rejected')) throw new BridgeError('INVALID_RESPONSE');
        })());
        return success({ closed: true, cleanupComplete: true });
      } }, { httpMethod: method, path: url.pathname, sub: 'runtime.close' });
    }
    const parts = url.pathname.split('/');
    const module = owners.get(parts[2]), contract = contracts[parts[2]];
    if (!module || parts[1] !== 'v1') return;
    if (method === 'GET' && parts.length === 5 && parts[3] === 'events' && module.events) {
      return wrapRoute({ contract: module.contract, responseType: 'stream',
        handler: async (_input, context) => {
          if (disposed) return { ok: false, contract: module.contract, code: 'CLOSED' };
          try { return await module.events(url, context); }
          catch (error) {
            if (debug) {
              debug.emit({ level: 'error', cat: 'runtime-node', sub: `${module.id}.events`,
                event: 'module.events.error', data: { path: url.pathname, query: url.search }, error });
            }
            return { ok: false, contract: module.contract,
              code: validCode(error?.code) ? error.code : failureCode(module.id) };
          }
        } }, { httpMethod: method, path: url.pathname, search: url.search, moduleId: module.id, sub: `${module.id}.events` });
    }
    if (method === 'GET' && parts[3] === 'images' && module.image && !url.search) {
      return wrapRoute({ contract: module.contract, responseType: 'stream',
        handler: async (_input, context) => {
          if (disposed) return { ok: false, contract: module.contract, code: 'CLOSED' };
          try { return await module.image(url.pathname, context); }
          catch (error) {
            if (debug) {
              debug.emit({ level: 'error', cat: 'runtime-node', sub: `${module.id}.image`,
                event: 'module.image.error', data: { path: url.pathname }, error });
            }
            return { ok: false, contract: module.contract,
              code: validCode(error?.code) ? error.code : failureCode(module.id) };
          }
        } }, { httpMethod: method, path: url.pathname, moduleId: module.id, sub: `${module.id}.image` });
    }
    if (method === 'PUT' && parts.length === 6 && parts[3] === 'sources' && module.upload) {
      let installationId, basename;
      try {
        installationId = decodeURIComponent(parts[4]);
        const query = url.search.slice(1).split('&').filter(Boolean);
        if (query.length > 1 || query.length && !query[0].startsWith('basename=')) invalid();
        if (query.length) basename = decodeURIComponent(query[0].slice('basename='.length));
      } catch { invalid(); }
      if (!/^[1-9][0-9]*$(?![\s\S])/.test(parts[5]) || !Number.isSafeInteger(Number(parts[5]))) invalid();
      const sourceRevision = Number(parts[5]);
      return wrapRoute({ contract: module.contract, contentType: 'application/octet-stream',
        status: reply => reply.ok ? 200 : 400,
        handler: (bytes, context) => module.upload({ installationId, sourceRevision,
          ...(basename === undefined ? {} : { basename }) }, bytes, context) },
      {
        httpMethod: method,
        path: url.pathname,
        search: url.search,
        moduleId: module.id,
        sub: `${module.id}.upload`,
        inputSummary: bytes => ({ installationId, sourceRevision, ...(basename !== undefined ? { basename } : {}),
          bytes: bytes?.byteLength ?? 0 }),
      });
    }
    if (method !== 'POST' || parts.length !== 4 || url.search) return;
    const rpcMethod = contract.prefix + parts[3];
    const handler = methods.get(rpcMethod);
    if (!handler) return;
    return wrapRoute({ contract: module.contract, contentType: 'application/json',
      handler: async (input, context) => {
        if (disposed) throw new BridgeError('CLOSED');
        try {
          const reply = await handler(input, context);
          if (reply?.ok === false && reply.contract === contract.contract && !validCode(reply.code)) {
            if (debug) {
              debug.emit({ level: 'error', cat: 'runtime-node', sub: rpcMethod,
                event: 'module.reply.unmapped_code', data: { moduleId: module.id, rpcMethod, rawCode: reply.code } });
            }
            return { ok: false, contract: contract.contract, code: failureCode(module.id) };
          }
          return reply;
        } catch (error) {
          if (debug) {
            debug.emit({ level: 'error', cat: 'runtime-node', sub: rpcMethod,
              event: 'module.method.exception', data: { moduleId: module.id, rpcMethod, input }, error });
          }
          return {
            ok: false,
            contract: contract.contract,
            code: validCode(error?.code) ? error.code : failureCode(module.id),
            ...(Number.isSafeInteger(error?.retryAfterSeconds) && error.retryAfterSeconds > 0
              ? { retryAfterSeconds: error.retryAfterSeconds } : {}),
          };
        }
      } }, { httpMethod: method, path: url.pathname, moduleId: module.id, rpcMethod, sub: rpcMethod });
  } });
  } catch (error) {
    if (debug) {
      debug.emit({ level: 'error', cat: 'runtime-node', sub: 'runtime.assembly', event: 'runtime.server.error', data: { product, sessionId }, error });
    }
    void dispose().catch(() => {});
    throw error;
  }
  const runtime = Object.freeze({
    get baseUrl() { return server.baseUrl; },
    get state() { return server.state; },
    async start() {
      if (disposed) throw new BridgeError('CLOSED');
      const startedAt = debug ? performance.now() : 0;
      if (debug) {
        debug.emit({ level: 'info', cat: 'runtime-node', sub: 'runtime.start', event: 'runtime.start.begin', data: { product } });
      }
      try {
        await Promise.all(modules.map(module => module.ready));
        if (disposed) throw new BridgeError('CLOSED');
        await server.start();
        if (debug) {
          debug.emit({
            level: 'info',
            cat: 'runtime-node',
            sub: 'runtime.start',
            event: 'runtime.start.ready',
            durationMs: performance.now() - startedAt,
            data: { product, baseUrl: server.baseUrl },
          });
        }
        return runtime;
      }
      catch (error) {
        if (debug) {
          debug.emit({
            level: 'error',
            cat: 'runtime-node',
            sub: 'runtime.start',
            event: 'runtime.start.error',
            durationMs: performance.now() - startedAt,
            data: { product },
            error,
          });
        }
        await dispose().catch(() => {});
        throw error;
      }
    },
    close() {
      return closing ??= (async () => {
        const startedAt = debug ? performance.now() : 0;
        if (debug) {
          debug.emit({ level: 'info', cat: 'runtime-node', sub: 'runtime.close', event: 'runtime.close.begin', data: { product } });
        }
        const results = await Promise.allSettled([server.close(), dispose()]);
        if (debug) {
          const failed = results.filter(result => result.status === 'rejected');
          debug.emit({
            level: failed.length ? 'error' : 'info',
            cat: 'runtime-node',
            sub: 'runtime.close',
            event: failed.length ? 'runtime.close.error' : 'runtime.close.end',
            durationMs: performance.now() - startedAt,
            data: { product, failedCount: failed.length },
            error: failed[0]?.reason,
          });
        }
        if (results.some(result => result.status === 'rejected')) throw new BridgeError('INVALID_RESPONSE');
      })();
    },
  });
  return runtime;
}
