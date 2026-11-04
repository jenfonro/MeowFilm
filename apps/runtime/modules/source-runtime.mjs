import { createHash, randomUUID } from 'node:crypto';
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

export const sourceRuntimeContract = 'source.runtime.v1';
const sourceBytes = SCRIPT_LIMITS.maxSourceBytes;
const controlBytes = 1024 * 1024;
const chunkBytes = 64 * 1024;
const stageTTL = 300_000;
const protocols = new Set(['legado', 'yiciyuan', 'qysg', 'cat']);
const codes = new Set([
  'INVALID_REQUEST', 'CLOSED', 'CANCELLED', 'LIMIT', 'SOURCE_INTEGRITY',
  'SOURCE_ENCODING', 'STAGE_NOT_FOUND', 'STALE_SOURCE', 'SOURCE_NOT_FOUND',
  'SOURCE_DISABLED', 'UNSUPPORTED_PROTOCOL', 'UNSUPPORTED_ACTION', 'UNSUPPORTED_RULE',
  'UNSUPPORTED_DEPENDENCY', 'CAPABILITY_UNAVAILABLE', 'PERMISSION_DENIED',
  'WEB_INTERACTION_REQUIRED', 'TIMEOUT', 'SOURCE_CLEANUP', 'SOURCE_FAILURE',
  'NETWORK_ERROR', 'HTTP_ERROR', 'INVALID_RESPONSE', 'STORAGE_ERROR',
  'VIRTUAL_ROUTE_NOT_FOUND',
]);

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
  requireValue(/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(value));
}

function integer(value, minimum = 0) {
  requireValue(Number.isSafeInteger(value) && value >= minimum);
}

function bounded(value, maximum = controlBytes) {
  let json;
  try { json = JSON.stringify(value); }
  catch { throw new Failure('INVALID_REQUEST'); }
  requireValue(typeof json === 'string', 'INVALID_REQUEST');
  requireValue(Buffer.byteLength(json) <= maximum, 'LIMIT');
  return JSON.parse(json);
}

function decode(bytes) {
  let value;
  try { value = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes); }
  catch { throw new Failure('SOURCE_ENCODING'); }
  requireValue(value.trim().length > 0 && Buffer.from(value).equals(bytes), 'SOURCE_ENCODING');
  return value;
}

function canonical(value) {
  return JSON.stringify(value, (_key, item) => object(item)
    ? Object.fromEntries(Object.entries(item).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)) : item);
}

function errorCode(error) {
  if (error instanceof Failure) return error.code;
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
    VIRTUAL_ROUTE_NOT_FOUND: 'VIRTUAL_ROUTE_NOT_FOUND',
  })[code] ?? 'SOURCE_FAILURE';
}

export function createSourceRuntimeModule({
  call,
  hostFactory = createScriptHost,
  network = createNodeNetwork(),
  capabilityOwner,
  platform = 'unknown',
  clock = Date.now,
  setTimer = setTimeout,
  clearTimer = clearTimeout,
} = {}) {
  requireValue(typeof call === 'function' && typeof hostFactory === 'function' &&
    typeof network === 'function' && typeof clock === 'function' &&
    typeof setTimer === 'function' && typeof clearTimer === 'function' &&
    typeof platform === 'string' && platform.length > 0 &&
    (capabilityOwner === undefined ||
      typeof capabilityOwner?.invoke === 'function' &&
      typeof capabilityOwner?.releaseProfiles === 'function'));
  const owner = capabilityOwner ?? createNodeSourceCapabilities({ network });
  const capabilities = createCapabilityClient({
    maximumBytes: SCRIPT_LIMITS.maxMemoryBytes,
    invoke: (name, input, context) => owner.invoke(name, input, context),
  });
  const adapter = createPortableSourceAdapter({
    capabilities,
    metadata: { platform },
  });
  const host = hostFactory({
    adapters: { legado: adapter, qysg: adapter, cat: adapter },
  });
  const stages = new Map();
  const bindings = new Map();
  const profileInstallations = new Set();
  const lifetime = new AbortController();
  let closed = false;
  let closing;
  let tail = Promise.resolve();
  let stageTimer;

  const success = value => bounded({ ok: true, contract: sourceRuntimeContract, ...value });
  const failure = error => ({ ok: false, contract: sourceRuntimeContract, code: errorCode(error) });
  const serialize = body => {
    const result = tail.then(body);
    tail = result.catch(() => {});
    return result;
  };
  const guard = signal => {
    requireValue(!closed, 'CLOSED');
    requireValue(!signal?.aborted, 'CANCELLED');
  };
  const current = id => host.list().find(value => value.installationId === id);
  const release = token => {
    const stage = stages.get(token);
    if (stage) {
      stage.bytes.fill(0);
      stages.delete(token);
      scheduleSweep();
    }
  };
  const sweep = () => {
    for (const [token, stage] of stages) if (stage.expiresAt <= clock()) release(token);
    scheduleSweep();
  };
  const scheduleSweep = () => {
    if (stageTimer !== undefined) clearTimer(stageTimer);
    stageTimer = undefined;
    if (closed || !stages.size) return;
    let expiresAt = Infinity;
    for (const stage of stages.values()) expiresAt = Math.min(expiresAt, stage.expiresAt);
    stageTimer = setTimer(() => {
      stageTimer = undefined;
      sweep();
    }, Math.max(1, expiresAt - clock()));
    stageTimer?.unref?.();
  };
  const staged = token => {
    text(token);
    sweep();
    const stage = stages.get(token);
    requireValue(stage, 'STAGE_NOT_FOUND');
    return stage;
  };
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
  const refreshBinding = async (binding, live) => {
    if (!binding || !live || binding.generation === live.generation) return;
    requireValue(live.generation > binding.generation &&
      live.contentHash === binding.contentHash && live.version === binding.version &&
      live.protocol === binding.protocol, 'SOURCE_CLEANUP');
    cleanup(binding.installationId);
    await releaseProfiles(binding.installationId, live.generation);
    requireValue(current(binding.installationId)?.generation === live.generation, 'STALE_SOURCE');
    binding.generation = live.generation;
  };

  function method(allowed, required, body) {
    return async (parameters = {}, context = {}) => {
      try {
        requireValue(object(context));
        const { signal } = context;
        requireValue(signal === undefined || signal instanceof AbortSignal);
        guard(signal);
        fields(parameters, allowed, required);
        const captured = bounded(parameters);
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
    for (const token of stages.keys()) release(token);
    closing = (async () => {
      let result;
      let hostClosed = true;
      let profilesReleased = true;
      try { result = await host.close(); }
      catch { hostClosed = false; }
      finally {
        await tail;
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
    'source.runtime.stage.begin': method(['basename', 'length', 'sha256'], undefined, params => {
      sweep();
      text(params.basename, 255);
      requireValue(!/[\\/:*?"<>|]/.test(params.basename) &&
        /\.(?:json|js|html?)$/i.test(params.basename));
      integer(params.length, 1);
      requireValue(params.length <= sourceBytes, 'LIMIT');
      requireValue(typeof params.sha256 === 'string' && /^[a-f0-9]{64}$/.test(params.sha256));
      const token = randomUUID();
      stages.set(token, {
        bytes: Buffer.alloc(params.length),
        length: params.length,
        hash: params.sha256,
        offset: 0,
        expiresAt: clock() + stageTTL,
      });
      scheduleSweep();
      return { token, chunkBytes };
    }),
    'source.runtime.stage.chunk': method(['token', 'offset', 'data'], undefined, params => {
      const stage = staged(params.token);
      integer(params.offset);
      requireValue(params.offset === stage.offset, 'SOURCE_INTEGRITY');
      requireValue(typeof params.data === 'string' &&
        params.data.length <= Math.ceil(chunkBytes / 3) * 4, 'LIMIT');
      const bytes = Buffer.from(params.data, 'base64');
      requireValue(bytes.length > 0 && bytes.length <= chunkBytes &&
        bytes.toString('base64') === params.data &&
        stage.offset + bytes.length <= stage.length, 'SOURCE_INTEGRITY');
      bytes.copy(stage.bytes, stage.offset);
      stage.offset += bytes.length;
      stage.expiresAt = clock() + stageTTL;
      scheduleSweep();
      return { next: stage.offset };
    }),
    'source.runtime.stage.abort': method(['token'], undefined, params => {
      text(params.token);
      release(params.token);
      return { released: true };
    }),
    'source.runtime.load': method([
      'token', 'installationId', 'revision', 'expectedRevision', 'protocol', 'version', 'metadata',
    ], ['token', 'installationId', 'revision', 'expectedRevision'], (params, signal) =>
      serialize(async () => {
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
        requireValue(/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version));
        const metadata = bounded(params.metadata ?? {}, 32 * 1024);
        requireValue(object(metadata));
        const stage = staged(params.token);
        requireValue(stage.offset === stage.length && digest(stage.bytes) === stage.hash,
          'SOURCE_INTEGRITY');
        const uploaded = decode(stage.bytes);
        const normalized = normalizeSourceDefinition(uploaded, params.protocol);
        const normalizedSource = JSON.stringify(normalized.source);
        requireValue(Buffer.byteLength(normalizedSource) <= sourceBytes, 'LIMIT');
        const signature = digest(canonical({
          protocol: normalized.protocol,
          importedProtocol: normalized.importedProtocol,
          source: normalized.source,
          version,
          metadata,
        }));
        const id = params.installationId;
        const old = bindings.get(id);
        const live = current(id);
        await refreshBinding(old, live);
        requireValue((old?.revision ?? 0) === params.expectedRevision, 'STALE_SOURCE');
        requireValue(Boolean(old) === Boolean(live), 'SOURCE_CLEANUP');
        requireValue(!old || params.revision >= old.revision, 'STALE_SOURCE');
        if (old?.revision === params.revision) {
          requireValue(old.signature === signature, 'SOURCE_INTEGRITY');
          requireValue(live.generation === old.generation && live.state === 'ready', 'SOURCE_DISABLED');
          release(params.token);
          return { instance: instance(old, live) };
        }
        requireValue(!old || live.state === 'ready' && live.generation === old.generation,
          'SOURCE_DISABLED');
        const spec = {
          installationId: id,
          protocol: normalized.protocol,
          source: normalizedSource,
          version,
          metadata: { ...metadata, importedProtocol: normalized.importedProtocol },
          permissions: { storage: [] },
          enabled: true,
        };
        const uploadHash = stage.hash;
        release(params.token);
        profileInstallations.add(id);
        try {
          const result = old ? await host.update(id, spec) : await host.install(spec);
          guard(signal);
          const next = current(id);
          requireValue(next && result?.generation === next.generation &&
            next.state === 'ready' && next.protocol === normalized.protocol &&
            next.contentHash === digest(normalizedSource) && next.version === version,
          'SOURCE_INTEGRITY');
          cleanup(id);
          const binding = {
            installationId: id,
            revision: params.revision,
            generation: next.generation,
            protocol: normalized.protocol,
            importedProtocol: normalized.importedProtocol,
            uploadHash,
            signature,
            contentHash: next.contentHash,
            version: next.version,
          };
          const response = { instance: instance(binding, next) };
          success(response);
          bindings.set(id, binding);
          await releaseProfiles(id, next.generation);
          guard(signal);
          return response;
        } catch (error) {
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
              bindings.set(id, { ...old, generation: recovered.generation });
              cleanup(id);
              await releaseProfiles(id, recovered.generation);
            } catch {
              bindings.delete(id);
              try { if (current(id)) await host.uninstall(id); }
              catch { /* Report the cleanup failure below. */ }
              await releaseProfiles(id);
              throw new Failure('SOURCE_CLEANUP');
            }
          } else {
            bindings.delete(id);
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
      })),
    'source.runtime.unload': method(
      ['installationId', 'revision', 'generation'],
      ['installationId', 'revision'],
      (params, signal) => serialize(async () => {
        guard(signal);
        identifier(params.installationId);
        integer(params.revision, 1);
        if (params.generation !== undefined) integer(params.generation, 1);
        const binding = bindings.get(params.installationId);
        const live = current(params.installationId);
        requireValue(binding && live, 'SOURCE_NOT_FOUND');
        await refreshBinding(binding, live);
        requireValue(binding.revision === params.revision &&
          (params.generation === undefined || binding.generation === params.generation),
        'STALE_SOURCE');
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
      }),
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
      const { binding, live } = await serialize(async () => {
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
      return { value: value ?? null };
    }),
    'source.runtime.status': method([], undefined, (_params, signal) => serialize(async () => {
      guard(signal);
      for (const binding of bindings.values()) {
        await refreshBinding(binding, current(binding.installationId));
      }
      guard(signal);
      return { instances: [...bindings.values()].map(binding => instance(binding)) };
    })),
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
  return { id: 'source', version: '0.1.0', methods, close };
}
