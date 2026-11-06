import { createHash } from 'node:crypto';
import {
  createNodeNetwork,
  createNodeSourceCapabilities,
  createPortableSourceAdapter,
  createScriptHost,
  SCRIPT_LIMITS,
  ScriptHostError,
} from '@meowfilm/script-host';
import {
  createCapabilityClient,
  normalizeSourceDefinition,
  SourceRuntimeError,
} from '@meowfilm/source-runtime';

import { BridgeError, encodeJSONValue, validateJSONValue } from '@meowfilm/runtime-bridge';
import { SourceCandidates, InstallationOperations } from './source-candidates.mjs';
import { contracts } from '../contracts.mjs';

export const sourceRuntimeContract = contracts.source.contract;
const sourceBytes = SCRIPT_LIMITS.maxSourceBytes;
const protocols = new Set(['legado', 'yiciyuan', 'qysg', 'cat']);
const codes = contracts.source.codes;

class Failure extends Error {
  constructor(code) {
    super(codes.has(code) ? code : 'SOURCE_FAILURE');
    this.code = this.message;
  }
}

const requireValue = (condition, code = 'INVALID_REQUEST') => {
  if (!condition) throw new Failure(code);
};
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const digest = value => createHash('sha256').update(value).digest('hex');

function fields(value, allowed, required = allowed) {
  requireValue(object(value) && Object.keys(value).every(key => allowed.includes(key)) &&
    required.every(key => Object.hasOwn(value, key)));
}

function text(value, maximum = 128) {
  requireValue(typeof value === 'string' && value.trim().length > 0 &&
    value.length <= maximum && !/[\u0000-\u001f\u007f]/.test(value));
}

function identifier(value) {
  text(value);
  requireValue(/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$(?![\s\S])/.test(value));
}

function integer(value, minimum = 0) {
  requireValue(Number.isSafeInteger(value) && value >= minimum);
}

function snapshot(value) { validateJSONValue(value); return structuredClone(value); }
function metadataValue(value) {
  requireValue(object(value));
  requireValue(encodeJSONValue(value).length <= 32 * 1024, 'LIMIT');
  return value;
}

function canonical(value) {
  return JSON.stringify(value, (_key, item) => object(item)
    ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item);
}

function errorCode(error) {
  if (error instanceof Failure) return error.code;
  if (error instanceof BridgeError) return 'INVALID_REQUEST';
  const code = error instanceof ScriptHostError || error instanceof SourceRuntimeError
    ? error.code : error?.name === 'AbortError' ? 'CANCELLED' : '';
  return ({
    INVALID_ARGUMENT: 'INVALID_REQUEST',
    HOST_CLOSED: 'CLOSED',
    CANCELLED: 'CANCELLED',
    TIMEOUT: 'TIMEOUT',
    STALE_SESSION: 'STALE_SOURCE',
    DISABLED: 'SOURCE_DISABLED',
    NOT_FOUND: 'SOURCE_NOT_FOUND',
    ALREADY_EXISTS: 'STALE_SOURCE',
    RESOURCE_LIMIT: 'LIMIT',
    UNSUPPORTED_PROTOCOL: 'UNSUPPORTED_PROTOCOL',
    UNSUPPORTED_ACTION: 'UNSUPPORTED_ACTION',
    UNSUPPORTED_RULE: 'UNSUPPORTED_RULE',
    UNSUPPORTED_SCRIPT_ASYNC: 'UNSUPPORTED_RULE',
    UNSUPPORTED_OPTION: 'UNSUPPORTED_RULE',
    UNSUPPORTED_CRYPTO: 'CAPABILITY_UNAVAILABLE',
    UNSUPPORTED_DEPENDENCY: 'UNSUPPORTED_DEPENDENCY',
    CAPABILITY_UNAVAILABLE: 'CAPABILITY_UNAVAILABLE',
    PERMISSION_DENIED: 'PERMISSION_DENIED',
    WEB_INTERACTION_REQUIRED: 'WEB_INTERACTION_REQUIRED',
    NETWORK_ERROR: 'NETWORK_ERROR',
    HTTP_ERROR: 'HTTP_ERROR',
    INVALID_RESPONSE: 'INVALID_RESPONSE',
    STORAGE_ERROR: 'STORAGE_ERROR',
    SCRIPT_CLEANUP: 'SOURCE_CLEANUP',
    VIRTUAL_ROUTE_NOT_FOUND: 'VIRTUAL_ROUTE_NOT_FOUND',
  })[code] ?? 'SOURCE_FAILURE';
}

export function createSourceRuntimeModule({
  hostFactory = createScriptHost,
  network = createNodeNetwork(),
  capabilityOwner,
  platform = 'unknown',
} = {}) {
  requireValue(typeof hostFactory === 'function' &&
    typeof network === 'function' &&
    typeof platform === 'string' && platform.length > 0 &&
    (capabilityOwner === undefined ||
      typeof capabilityOwner?.invoke === 'function' &&
      typeof capabilityOwner?.releaseProfiles === 'function'));
  const owner = capabilityOwner ?? createNodeSourceCapabilities({ network });
  const capabilities = createCapabilityClient({
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  const adapter = createPortableSourceAdapter({
    capabilities,
    metadata: { platform },
  });
  const host = hostFactory({
    adapters: { legado: adapter, qysg: adapter, cat: adapter },
  });
  const sources = new SourceCandidates({ module: 'source', fail: code => new Failure(code) });
  const operations = new InstallationOperations();
  const bindings = new Map();
  const profileInstallations = new Set();
  const lifetime = new AbortController();
  let closed = false;
  let closing;

  const success = value => { validateJSONValue(value); return { ok: true, contract: sourceRuntimeContract, value }; };
  const failure = error => ({ ok: false, contract: sourceRuntimeContract, code: errorCode(error) });
  const guard = signal => {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
  };
  const current = id => host.list().find(value => value.installationId === id);
  const instance = (binding, live = current(binding.installationId)) => {
    requireValue(live && live.generation === binding.generation, 'STALE_SOURCE');
    return {
      installationId: binding.installationId,
      revision: binding.revision,
      generation: binding.generation,
      state: live.state,
      protocol: live.protocol,
      importedProtocol: binding.importedProtocol,
      version: live.version,
      contentHash: live.contentHash,
      uploadHash: binding.uploadHash,
      capabilities: live.capabilities,
    };
  };
  const cleanup = installationId => {
    requireValue(!host.stats().cleanupIssues.some(issue =>
      issue.installationId === installationId) &&
      host.cleanupPending(installationId) === 0, 'SOURCE_CLEANUP');
  };
  const releaseProfiles = async (installationId, keepGeneration) => {
    const keep = keepGeneration === undefined
      ? '' : `source:${installationId}:${keepGeneration}`;
    try { await owner.releaseProfiles(`source:${installationId}:`, keep); }
    catch { throw new Failure('SOURCE_CLEANUP'); }
  };
  const checkGeneration = (binding, live) => {
    if (!binding || !live || binding.generation === live.generation) return;
    requireValue(live.generation > binding.generation &&
      live.contentHash === binding.contentHash && live.version === binding.version &&
      live.protocol === binding.protocol, 'SOURCE_CLEANUP');
  };
  const refreshBinding = async (binding, live) => {
    if (!binding || !live || binding.generation === live.generation) return;
    checkGeneration(binding, live);
    cleanup(binding.installationId);
    await releaseProfiles(binding.installationId, live.generation);
    requireValue(current(binding.installationId)?.generation === live.generation, 'STALE_SOURCE');
    binding.generation = live.generation;
    binding.observe = host.observe(binding.installationId);
  };
  const interruptLoad = params => {
    if (!operations.has(params.installationId)) return;
    const stop = host.captureInitialization(params.installationId);
    if (!stop) return;
    const old = bindings.get(params.installationId), live = current(params.installationId);
    if ((old?.revision ?? 0) !== params.expectedRevision) return;
    requireValue(!old || params.revision >= old.revision, 'STALE_SOURCE');
    sources.peek(params.installationId, params.sourceRevision);
    if (old?.revision === params.revision) return;
    requireValue(!old || live?.generation === old.generation && live.state === 'ready', 'SOURCE_DISABLED');
    stop();
  };
  const interruptUnload = params => {
    if (!operations.has(params.installationId)) return;
    const binding = bindings.get(params.installationId), live = current(params.installationId);
    if (binding?.revision !== params.revision || live?.generation !== binding?.generation ||
        params.generation !== undefined && params.generation !== binding?.generation) return;
    host.captureInitialization(params.installationId)?.();
  };

  function method(allowed, required, body) {
    return async (parameters = {}, context = {}) => {
      try {
        requireValue(object(context));
        const { signal } = context;
        requireValue(signal === undefined || signal instanceof AbortSignal);
        guard(signal);
        fields(parameters, allowed, required);
        const captured = snapshot(parameters);
        const active = signal ? AbortSignal.any([lifetime.signal, signal]) : lifetime.signal;
        const result = await body(captured, active);
        guard(active);
        return success(result);
      } catch (error) { return failure(error); }
    };
  }

  async function shutdown() {
    if (closing) return closing;
    closed = true;
    lifetime.abort();
    sources.close();
    closing = (async () => {
      let result;
      let hostClosed = true;
      let profilesReleased = true;
      try { result = await host.close(); }
      catch { hostClosed = false; }
      finally {
        await operations.drain();
        for (const id of bindings.keys()) profileInstallations.add(id);
        const released = await Promise.allSettled(
          [...profileInstallations].map(id => releaseProfiles(id)),
        );
        profilesReleased = released.every(value => value.status === 'fulfilled');
        bindings.clear();
        profileInstallations.clear();
      }
      requireValue(hostClosed && result?.cleanupComplete && profilesReleased, 'SOURCE_CLEANUP');
      return { closed: true, cleanupComplete: true };
    })();
    return closing;
  }

  const methods = {
    'source.runtime.load': method([
      'sourceRevision', 'installationId', 'revision', 'expectedRevision', 'protocol', 'version', 'metadata',
    ], ['sourceRevision', 'installationId', 'revision', 'expectedRevision'], (params, signal) => {
      guard(signal);
      identifier(params.installationId);
      integer(params.revision, 1);
      integer(params.expectedRevision);
      if (params.protocol !== undefined) {
        text(params.protocol);
        requireValue(protocols.has(params.protocol), 'UNSUPPORTED_PROTOCOL');
      }
      const version = params.version ?? '1.0.0';
      text(version);
      requireValue(/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$(?![\s\S])/.test(version));
      const metadata = metadataValue(params.metadata ?? {});
      integer(params.sourceRevision, 1);
      interruptLoad(params);
      return operations.run(params.installationId, async () => {
        guard(signal);
        const id = params.installationId;
        const old = bindings.get(id);
        const live = current(id);
        requireValue((old?.revision ?? 0) === params.expectedRevision, 'STALE_SOURCE');
        await refreshBinding(old, live);
        guard(signal);
        requireValue(Boolean(old) === Boolean(live), 'SOURCE_CLEANUP');
        requireValue(!old || params.revision >= old.revision, 'STALE_SOURCE');
        const uploaded = sources.peek(id, params.sourceRevision);
        let execution = uploaded.execution;
        if (!execution || execution.inputProtocol !== params.protocol) {
          try {
            const normalized = normalizeSourceDefinition(uploaded.source, params.protocol);
            const source = JSON.stringify(normalized.source);
            requireValue(Buffer.byteLength(source) <= sourceBytes, 'LIMIT');
            const hash = source === uploaded.source ? uploaded.contentHash
              : source === execution?.source ? execution.hash : digest(source);
            // Revision equality retains the existing object-order-independent
            // definition identity, separate from actual executed byte identity.
            const identity = canonical(normalized.source);
            execution = { inputProtocol: params.protocol, protocol: normalized.protocol,
              importedProtocol: normalized.importedProtocol, source, hash,
              identityHash: identity === source ? hash : digest(identity) };
            uploaded.execution = execution;
          } catch (error) {
            sources.finish(sources.take(id, params.sourceRevision));
            throw error;
          }
        }
        const { protocol, importedProtocol, source: normalizedSource, hash: executionHash, identityHash } = execution;
        const signature = digest(canonical({
          protocol,
          importedProtocol,
          contentHash: identityHash,
          version,
          metadata,
        }));
        if (old?.revision === params.revision) {
          requireValue(old.signature === signature, 'SOURCE_INTEGRITY');
          requireValue(live.generation === old.generation && live.state === 'ready', 'SOURCE_DISABLED');
          sources.finish(sources.take(id, params.sourceRevision), true);
          return { instance: instance(old, live) };
        }
        requireValue(!old || live.state === 'ready' && live.generation === old.generation,
          'SOURCE_DISABLED');
        const spec = {
          installationId: id,
          protocol,
          source: normalizedSource,
          contentHash: executionHash,
          version,
          metadata: { ...metadata, importedProtocol },
          permissions: { storage: [] },
          enabled: true,
        };
        const uploadHash = uploaded.contentHash;
        sources.take(id, params.sourceRevision);
        profileInstallations.add(id);
        try {
          const result = old ? await host.update(id, spec, { signal }) : await host.install(spec, { signal });
          guard(signal);
          const next = current(id);
          requireValue(next && result?.generation === next.generation &&
            next.state === 'ready' && next.protocol === protocol &&
            next.contentHash === executionHash && next.version === version,
          'SOURCE_INTEGRITY');
          cleanup(id);
          const binding = {
            installationId: id,
            revision: params.revision,
            generation: next.generation,
            protocol,
            importedProtocol,
            uploadHash,
            signature,
            contentHash: next.contentHash,
            version: next.version,
            observe: host.observe(id),
          };
          const response = { instance: instance(binding, next) };
          success(response);
          bindings.set(id, binding);
          await releaseProfiles(id, next.generation);
          guard(signal);
          sources.finish(uploaded, true);
          return response;
        } catch (error) {
          sources.finish(uploaded);
          if (closed) throw error;
          const remaining = current(id);
          if (old && remaining?.generation === old.generation &&
              remaining.state === 'ready' && remaining.contentHash === live.contentHash) {
            cleanup(id);
            await releaseProfiles(id, old.generation);
          } else if (old && remaining) {
            try {
              const restored = await host.rollback(id);
              const recovered = current(id);
              requireValue(recovered && restored.generation === recovered.generation &&
                recovered.state === 'ready' && recovered.protocol === live.protocol &&
                recovered.contentHash === live.contentHash && recovered.version === live.version,
              'SOURCE_CLEANUP');
              bindings.set(id, { ...old, generation: recovered.generation, observe: host.observe(id) });
              cleanup(id);
              await releaseProfiles(id, recovered.generation);
            } catch {
              bindings.delete(id); sources.retire(id);
              try { if (current(id)) await host.uninstall(id); }
              catch { /* Report the cleanup failure below. */ }
              await releaseProfiles(id);
              throw new Failure('SOURCE_CLEANUP');
            }
          } else {
            bindings.delete(id); sources.retire(id);
            if (remaining) {
              try { await host.uninstall(id); }
              catch { throw new Failure('SOURCE_CLEANUP'); }
            }
            cleanup(id);
            await releaseProfiles(id);
            profileInstallations.delete(id);
          }
          throw error;
        }
      });
    }),
    'source.runtime.unload': method(
      ['installationId', 'revision', 'generation'],
      ['installationId', 'revision'],
      (params, signal) => {
        guard(signal);
        identifier(params.installationId);
        integer(params.revision, 1);
        if (params.generation !== undefined) integer(params.generation, 1);
        interruptUnload(params);
        return operations.run(params.installationId, async () => {
          guard(signal);
          const binding = bindings.get(params.installationId);
          const live = current(params.installationId);
          requireValue(binding && live, 'SOURCE_NOT_FOUND');
          await refreshBinding(binding, live);
          requireValue(binding.revision === params.revision &&
            (params.generation === undefined || binding.generation === params.generation),
          'STALE_SOURCE');
          sources.remove(params.installationId);
          try { await host.uninstall(params.installationId); }
          catch { throw new Failure('SOURCE_CLEANUP'); }
          finally {
            if (!current(params.installationId)) bindings.delete(params.installationId);
          }
          requireValue(!current(params.installationId), 'SOURCE_CLEANUP');
          cleanup(params.installationId);
          await releaseProfiles(params.installationId);
          profileInstallations.delete(params.installationId);
          return { unloaded: true, cleanupComplete: true };
        });
      },
    ),
    'source.runtime.invoke': method([
      'installationId', 'revision', 'generation', 'action', 'payload', 'timeoutMs',
    ], ['installationId', 'revision', 'generation', 'action', 'payload'], async (params, signal) => {
      identifier(params.installationId);
      identifier(params.action);
      integer(params.revision, 1);
      integer(params.generation, 1);
      requireValue(object(params.payload));
      if (params.timeoutMs !== undefined) {
        integer(params.timeoutMs, 1);
        requireValue(params.timeoutMs <= 60_000);
      }
      const { binding, live } = await operations.run(params.installationId, async () => {
        guard(signal);
        const binding = bindings.get(params.installationId);
        const live = current(params.installationId);
        requireValue(binding && live, 'SOURCE_NOT_FOUND');
        await refreshBinding(binding, live);
        return { binding, live };
      });
      requireValue(binding.revision === params.revision &&
        binding.generation === params.generation &&
        live.generation === params.generation, 'STALE_SOURCE');
      requireValue(live.state === 'ready', 'SOURCE_DISABLED');
      const value = await host.invoke({
        installationId: params.installationId,
        generation: params.generation,
        siteId: 'source',
        operation: params.action,
        payload: params.payload,
      }, { signal, timeoutMs: params.timeoutMs });
      guard(signal);
      requireValue(bindings.get(params.installationId) === binding &&
        current(params.installationId)?.generation === params.generation, 'STALE_SOURCE');
      return value ?? null;
    }),
    'source.runtime.status': method([], undefined, (_params, signal) => {
      guard(signal);
      return { instances: [...bindings.values()].map(binding => {
        const live = operations.has(binding.installationId)
          ? binding.observe() : current(binding.installationId);
        checkGeneration(binding, live);
        return instance({ ...binding, generation: live?.generation }, live);
      }) };
    }),
    'source.runtime.close': async (params = {}) => {
      try {
        fields(params, []);
        return success(await shutdown());
      } catch (error) { return failure(error); }
    },
  };

  const close = async () => {
    try { return success(await shutdown()); }
    catch (error) { return failure(error); }
  };
  async function upload(input, bytes, { signal } = {}) {
    try { guard(signal); return success(sources.put(input, bytes, signal)); }
    catch (error) { return failure(error); }
  }
  return { id: 'source', version: contracts.source.version, contract: sourceRuntimeContract, methods, upload, close };
}
