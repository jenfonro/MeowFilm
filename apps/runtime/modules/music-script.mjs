import { createHash } from 'node:crypto';
import {
  createScriptHost, createNodeNetwork, ScriptHostError,
} from '@meowfilm/script-host';
import { BridgeError, encodeJSONValue, validateJSONValue } from '@meowfilm/runtime-bridge';

import { SourceCandidates, InstallationOperations } from './source-candidates.mjs';
import { contracts } from '../contracts.mjs';

export const musicScriptContract = contracts.music.contract;
const codes = contracts.music.codes;
class Failure extends Error {
  constructor(code) { super(codes.has(code) ? code : 'SCRIPT_FAILURE'); this.code = this.message; }
}
const requireValue = (condition, code = 'INVALID_REQUEST') => { if (!condition) throw new Failure(code); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const digest = value => createHash('sha256').update(value).digest('hex');
function fields(value, allowed, required = allowed) {
  requireValue(object(value) && Object.keys(value).every(key => allowed.includes(key)) &&
    required.every(key => Object.hasOwn(value, key)));
}
function integer(value, min = 0) {
  requireValue(Number.isSafeInteger(value) && value >= min);
}
function text(value, max = 128) {
  requireValue(typeof value === 'string' && value.trim().length > 0 && value.length <= max &&
    !/[\u0000-\u001f\u007f]/.test(value));
}
function identifier(value) {
  text(value); requireValue(/^[A-Za-z0-9][A-Za-z0-9._-]*$(?![\s\S])/.test(value));
}
function snapshot(value) { validateJSONValue(value); return structuredClone(value); }
// Object ordering is not a revision change; array ordering remains meaningful.
const canonical = value => JSON.stringify(value, (_key, item) => object(item)
  ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item);
function settings(params) {
  text(params.version);
  requireValue(object(params.config));
  const bytes = encodeJSONValue(params.config);
  try { requireValue(bytes.length <= 32 * 1024, 'LIMIT'); }
  finally { bytes.fill(0); }
  fields(params.permissions, ['storage']);
  const permissions = params.permissions;
  requireValue(Array.isArray(permissions.storage) && permissions.storage.length <= 2 &&
    new Set(permissions.storage).size === permissions.storage.length &&
    permissions.storage.every(value => ['read', 'write'].includes(value)));
  requireValue(typeof params.grant === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$(?![\s\S])/i.test(params.grant) &&
    params.grant !== '00000000-0000-0000-0000-000000000000', 'PERMISSION_DENIED');
  params.grant = params.grant.toLowerCase();
  permissions.storage.sort();
}
function errorCode(error) {
  if (error instanceof Failure) return error.code;
  if (error instanceof BridgeError) return 'INVALID_REQUEST';
  if (error instanceof ScriptHostError) return ({
    INVALID_ARGUMENT: 'INVALID_REQUEST', HOST_CLOSED: 'CLOSED', CANCELLED: 'CANCELLED',
    TIMEOUT: 'TIMEOUT', STALE_SESSION: 'STALE_SOURCE', DISABLED: 'SOURCE_DISABLED',
    NOT_FOUND: 'SOURCE_NOT_FOUND', ALREADY_EXISTS: 'STALE_SOURCE',
    CAPABILITY_UNAVAILABLE: 'CAPABILITY_UNAVAILABLE', PERMISSION_DENIED: 'PERMISSION_DENIED',
    RESOURCE_LIMIT: 'LIMIT', NETWORK_ERROR: 'NETWORK_ERROR', STORAGE_ERROR: 'STORAGE_ERROR',
    SCRIPT_CLEANUP: 'SCRIPT_CLEANUP',
    UPSTREAM_HTTP_ERROR: 'UPSTREAM_HTTP', INVALID_JSON: 'UPSTREAM_FORMAT',
  })[error.code] ?? 'SCRIPT_FAILURE';
  return error?.name === 'AbortError' ? 'CANCELLED' : 'SCRIPT_FAILURE';
}
function storageError(code) {
  return new ScriptHostError(({
    PERMISSION_DENIED: 'PERMISSION_DENIED', STALE_SOURCE: 'STALE_SESSION',
    CANCELLED: 'CANCELLED', CLOSED: 'HOST_CLOSED', TIMEOUT: 'TIMEOUT',
    LIMIT: 'RESOURCE_LIMIT', INVALID_REQUEST: 'INVALID_ARGUMENT',
  })[code] ?? 'STORAGE_ERROR');
}
function abortable(promise, signal) {
  if (signal.aborted) return Promise.reject(new ScriptHostError('CANCELLED'));
  return new Promise((resolve, reject) => {
    const abort = () => reject(new ScriptHostError('CANCELLED'));
    signal.addEventListener('abort', abort, { once: true });
    promise.then(resolve, reject).finally(() => signal.removeEventListener('abort', abort)).catch(() => {});
  });
}

/**
 * LX execution only. Native owns metadata and transactional storage overlays.
 * No document bridge, platform API, library, settings or lyrics parser.
 * ScriptHost executes guest code in isolated QuickJS heaps.
 */
export function createMusicScriptModule({ call, hostFactory = createScriptHost,
  network = createNodeNetwork() } = {}) {
  requireValue(typeof call === 'function' && typeof hostFactory === 'function' &&
    typeof network === 'function');
  const bindings = new Map(), candidates = new Map(), sessions = new Map();
  const generations = new Map(), usedGrants = new Set(), adapterTasks = new Set();
  const sources = new SourceCandidates({ module: 'music', fail: code => new Failure(code) });
  const operations = new InstallationOperations();
  const lifetime = new AbortController();
  let closed = false, closing;
  const sessionKey = (id, generation) => JSON.stringify([id, generation]);
  function track(input, promise, kind = 'network') {
    const task = { installationId: input.installationId, generation: input.generation,
      siteId: input.siteId, kind, promise };
    adapterTasks.add(task);
    promise.finally(() => adapterTasks.delete(task)).catch(() => {});
    return promise;
  }
  // Track actual adapter completion, not merely a cancelled Promise.race.
  const host = hostFactory({
    network: input => track(input.scope, Promise.resolve().then(() => network(input))),
    storage: Object.fromEntries(['get', 'set', 'delete'].map(operation =>
      [operation, input => storage(operation, input)])),
  });
  function guard(signal) {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
  }
  function current(id) { return host.list().find(value => value.installationId === id); }
  function pin(candidate, generation) {
    integer(generation, 1);
    requireValue(generation > candidate.floor &&
      (candidate.generation === null || candidate.generation === generation), 'STALE_SOURCE');
    candidate.generation = generation;
    generations.set(candidate.installationId, Math.max(generations.get(candidate.installationId) ?? 0, generation));
  }
  function revoke(auth) {
    if (!auth) return;
    auth.active = false; auth.controller.abort();
    if (candidates.get(auth.installationId) === auth) candidates.delete(auth.installationId);
    const key = sessionKey(auth.installationId, auth.generation);
    if (sessions.get(key) === auth) sessions.delete(key);
  }
  function authorized(auth, input) {
    requireValue(!closed && auth?.active && input.signal instanceof AbortSignal &&
      !input.signal.aborted && typeof input.isCurrent === 'function' && input.isCurrent() === true &&
      !auth.controller.signal.aborted &&
      (auth.phase !== 'candidate' || !auth.loadSignal.aborted), 'STALE_SOURCE');
    requireValue(auth.installationId === input.installationId && auth.generation === input.generation &&
      (auth.phase === 'candidate' ? candidates.get(input.installationId) === auth
        : sessions.get(sessionKey(input.installationId, input.generation)) === auth), 'STALE_SOURCE');
    requireValue(auth.phase === 'candidate' || Object.hasOwn(auth.capabilities, input.siteId), 'PERMISSION_DENIED');
  }
  async function storage(operation, input) {
    try {
      requireValue(object(input), 'PERMISSION_DENIED');
      identifier(input.installationId); integer(input.generation, 1);
      requireValue(typeof input.siteId === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$(?![\s\S])/.test(input.siteId),
        'PERMISSION_DENIED');
      requireValue(typeof input.key === 'string' &&
        input.key.length > 0 && input.key.length <= 256, 'PERMISSION_DENIED');
      requireValue(!closed && input.signal instanceof AbortSignal && !input.signal.aborted &&
        typeof input.isCurrent === 'function' && input.isCurrent() === true, 'STALE_SOURCE');
      // A live old generation is resolved BEFORE a trial candidate. Never use
      // bindings.get(id).grant: callbacks can overlap update's publication.
      let auth = sessions.get(sessionKey(input.installationId, input.generation));
      if (!auth) {
        auth = candidates.get(input.installationId);
        requireValue(auth?.active && !auth.loadSignal.aborted, 'STALE_SOURCE');
        pin(auth, input.generation);
      }
      authorized(auth, input);
      requireValue(auth.permissions.storage.includes(operation === 'get' ? 'read' : 'write'),
        'PERMISSION_DENIED');
      const message = { grant: auth.grant, installationId: input.installationId,
        siteId: input.siteId, generation: input.generation, key: input.key };
      if (operation === 'set') message.value = input.value;
      const captured = snapshot(message);
      if (operation === 'set') requireValue(Object.hasOwn(captured, 'value'));
      const signal = AbortSignal.any([lifetime.signal, auth.controller.signal, input.signal,
        ...(auth.phase === 'candidate' ? [auth.loadSignal] : [])]);
      const task = track(input, Promise.resolve().then(() => {
        authorized(auth, input);
        return call(`platform.musicScript.storage.${operation}`, captured, { signal });
      }), 'storage');
      const response = snapshot(await abortable(task, signal));
      authorized(auth, input);
      requireValue(object(response) && typeof response.ok === 'boolean', 'STORAGE_ERROR');
      if (!response.ok) {
        fields(response, ['ok', 'code']);
        throw storageError(response.code);
      }
      fields(response, ['ok', 'value']);
      return response.value;
    } catch (error) {
      if (error instanceof ScriptHostError) throw error;
      throw storageError(error instanceof Failure ? error.code : 'STORAGE_ERROR');
    }
  }
  function cleanup(id, keepGeneration) {
    requireValue(!host.stats().cleanupIssues.some(issue => issue.installationId === id) &&
      ![...adapterTasks].some(task => task.installationId === id && task.generation !== keepGeneration),
    'SCRIPT_CLEANUP');
  }
  function instance(binding, live = current(binding.installationId)) {
    requireValue(live && live.generation === binding.generation, 'STALE_SOURCE');
    const capabilities = live.capabilities;
    return { installationId: binding.installationId, revision: binding.revision,
      generation: binding.generation, state: live.state, capabilities, contentHash: live.contentHash,
      protocol: live.protocol, version: live.version };
  }
  function interruptLoad(params) {
    if (!operations.has(params.installationId)) return;
    const stop = host.captureInitialization(params.installationId);
    if (!stop) return;
    const old = bindings.get(params.installationId), live = current(params.installationId);
    // A request depending on an earlier queued commit is checked again in
    // order; it must not cancel that earlier commit to manufacture its CAS.
    if ((old?.revision ?? 0) !== params.expectedRevision) return;
    requireValue(!old || params.revision >= old.revision, 'STALE_SOURCE');
    sources.peek(params.installationId, params.sourceRevision);
    if (old?.revision === params.revision) return;
    requireValue(!old || live?.generation === old.generation && live.state === 'ready', 'SOURCE_DISABLED');
    requireValue(!usedGrants.has(digest(params.grant)), 'PERMISSION_DENIED');
    stop();
  }
  function interruptUnload(params) {
    if (!operations.has(params.installationId)) return;
    const binding = bindings.get(params.installationId), live = current(params.installationId);
    if (binding?.revision !== params.revision || live?.generation !== binding?.generation ||
        params.generation !== undefined && params.generation !== binding?.generation) return;
    host.captureInitialization(params.installationId)?.();
  }
  function success(value) {
    validateJSONValue(value);
    return { ok: true, contract: musicScriptContract, value };
  }
  const failure = error => ({ ok: false, contract: musicScriptContract, code: errorCode(error) });
  function business(allowed, required, body) {
    return async (parameters = {}, context = {}) => {
      try {
        requireValue(object(context));
        const { signal } = context;
        requireValue(signal === undefined || signal instanceof AbortSignal);
        guard(signal); fields(parameters, allowed, required);
        const captured = snapshot(parameters);
        const active = signal ? AbortSignal.any([lifetime.signal, signal]) : lifetime.signal;
        const result = await body(captured, active);
        guard(active);
        return success(result);
      } catch (error) { return failure(error); }
    };
  }
  async function upload(input, bytes, { signal } = {}) {
    try { guard(signal); return success(sources.put(input, bytes, signal)); }
    catch (error) { return failure(error); }
  }
  async function shutdown() {
    if (closing) return closing;
    closed = true; lifetime.abort();
    sources.close();
    for (const auth of [...candidates.values(), ...sessions.values()]) revoke(auth);
    // Stop sessions immediately, including one awaiting a reverse callback.
    closing = (async () => {
      let result, cleanupFailure;
      try { result = await host.close(); }
      catch (error) { cleanupFailure ??= error; }
      finally {
        await operations.drain();
        bindings.clear(); candidates.clear(); sessions.clear(); generations.clear(); usedGrants.clear();
      }
      if (cleanupFailure) throw new Failure('SCRIPT_CLEANUP');
      requireValue(result?.cleanupComplete && adapterTasks.size === 0, 'SCRIPT_CLEANUP');
      return { closed: true, cleanupComplete: true };
    })();
    return closing;
  }
  async function close() {
    try { return success(await shutdown()); } catch (error) { return failure(error); }
  }
  const methods = {
    'music.script.load': business(['sourceRevision', 'installationId', 'revision', 'expectedRevision', 'version',
      'config', 'permissions', 'grant'], undefined, (params, signal) => {
      guard(signal); identifier(params.installationId);
      integer(params.revision, 1); integer(params.expectedRevision); settings(params);
      integer(params.sourceRevision, 1);
      interruptLoad(params);
      return operations.run(params.installationId, async () => {
        guard(signal);
        const id = params.installationId, old = bindings.get(id), live = current(id);
        requireValue((old?.revision ?? 0) === params.expectedRevision, 'STALE_SOURCE');
        requireValue(Boolean(old) === Boolean(live), 'SCRIPT_CLEANUP');
        requireValue(!old || params.revision >= old.revision, 'STALE_SOURCE');
        const uploaded = sources.peek(id, params.sourceRevision);
        const { source, contentHash } = uploaded;
        const signature = digest(canonical({ contentHash, version: params.version, config: params.config,
          permissions: params.permissions, grant: params.grant }));
        if (old?.revision === params.revision) {
          requireValue(old.signature === signature, 'SCRIPT_INTEGRITY');
          requireValue(live.generation === old.generation && live.state === 'ready', 'SOURCE_DISABLED');
          sources.finish(sources.take(id, params.sourceRevision), true);
          return { instance: instance(old, live) };
        }
        requireValue(!old || (live.state === 'ready' && live.generation === old.generation), 'SOURCE_DISABLED');
        const grantHash = digest(params.grant);
        requireValue(!usedGrants.has(grantHash), 'PERMISSION_DENIED');
        const candidate = { installationId: id, revision: params.revision, signature, contentHash,
          grant: params.grant, permissions: params.permissions, phase: 'candidate', active: true,
          generation: null, floor: Math.max(generations.get(id) ?? 0, old?.generation ?? 0),
          controller: new AbortController(), loadSignal: signal };
        // Register BEFORE install/update; storage never waits for this queue/load.
        sources.take(id, params.sourceRevision);
        candidates.set(id, candidate); usedGrants.add(grantHash);
        const spec = { installationId: id, protocol: 'lx', enabled: true, source, contentHash,
          version: params.version, config: params.config, permissions: params.permissions };
        try {
          const result = old ? await host.update(id, spec, { signal }) : await host.install(spec, { signal });
          guard(signal);
          const next = current(id);
          requireValue(next && result?.generation === next.generation && next.state === 'ready' &&
            next.contentHash === contentHash && next.protocol === 'lx' && next.version === params.version,
          'SCRIPT_INTEGRITY');
          pin(candidate, next.generation);
          cleanup(id, next.generation);
          // Preflight the whole response before publishing the authorization.
          const response = { instance: instance(candidate, next) };
          validateJSONValue(response);
          revoke(old);
          candidates.delete(id);
          candidate.phase = 'session'; candidate.loadSignal = null; candidate.capabilities = next.capabilities;
          candidate.observe = host.observe(id);
          sessions.set(sessionKey(id, candidate.generation), candidate);
          bindings.set(id, candidate);
          sources.finish(uploaded, true);
          return response;
        } catch (error) {
          sources.finish(uploaded);
          revoke(candidate);
          if (closed) throw error;
          const remaining = current(id);
          if (old && remaining?.generation === old.generation && remaining.state === 'ready' &&
              remaining.contentHash === old.contentHash) {
            // Public host.update failed its trial: preserve EXACT old generation/grant.
            cleanup(id, old.generation);
          } else {
            // Never call rollback: it creates another generation and cannot reuse
            // Native's old grant/metadata. A post-publication failure retires the
            // changed session; Native retires it and reconciles before the next
            // explicitly requested source command.
            revoke(old); bindings.delete(id); sources.retire(id);
            if (remaining) {
              generations.set(id, Math.max(generations.get(id) ?? 0, remaining.generation));
              try { await host.uninstall(id); } catch { throw new Failure('SCRIPT_CLEANUP'); }
            }
            cleanup(id);
          }
          throw error;
        }
      });
    }),
    'music.script.unload': business(['installationId', 'revision', 'generation'],
      ['installationId', 'revision'], (params, signal) => {
        guard(signal); identifier(params.installationId); integer(params.revision, 1);
        if (params.generation !== undefined) integer(params.generation, 1);
        interruptUnload(params);
        return operations.run(params.installationId, async () => {
          guard(signal);
          const binding = bindings.get(params.installationId), live = current(params.installationId);
          requireValue(binding && live, 'SOURCE_NOT_FOUND');
          requireValue(binding.revision === params.revision && live.generation === binding.generation &&
            (params.generation === undefined || params.generation === binding.generation), 'STALE_SOURCE');
          sources.remove(params.installationId);
          revoke(binding);
          try { await host.uninstall(params.installationId); }
          finally { bindings.delete(params.installationId); }
          requireValue(!current(params.installationId), 'SCRIPT_CLEANUP');
          cleanup(params.installationId);
          return { unloaded: true, cleanupComplete: true };
        });
      }),
    'music.script.invoke': business(['installationId', 'revision', 'generation', 'siteId', 'operation', 'payload'],
      undefined, async (params, signal) => {
        identifier(params.installationId); identifier(params.siteId); identifier(params.operation);
        integer(params.revision, 1); integer(params.generation, 1);
        requireValue(object(params.payload));
        const binding = bindings.get(params.installationId), live = current(params.installationId);
        requireValue(binding && live, 'SOURCE_NOT_FOUND');
        requireValue(binding.revision === params.revision && binding.generation === params.generation &&
          live.generation === params.generation, 'STALE_SOURCE');
        requireValue(live.state === 'ready', 'SOURCE_DISABLED');
        const value = await host.invoke(params, { signal });
        guard(signal);
        requireValue(bindings.get(params.installationId) === binding &&
          current(params.installationId)?.generation === params.generation, 'STALE_SOURCE');
        return value ?? null;
      }),
    'music.script.status': business([], undefined, (_params, signal) => {
      guard(signal);
      return { documentAccess: false,
        instances: [...bindings.values()].map(binding => instance(binding, binding.observe())) };
    }),
    'music.script.close': async (params = {}) => {
      try { fields(params, []); return await close(); } catch (error) { return failure(error); }
    },
  };
  return { id: 'music', version: contracts.music.version, contract: musicScriptContract, methods, upload, close };
}
