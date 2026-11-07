import { createCatRuntime, ScriptHostError } from '@meowfilm/script-host';
import { validateJSONValue } from '@meowfilm/runtime-bridge';
import { SourceCandidates } from './source-candidates.mjs';
import { contracts } from '../contracts.mjs';

export const catScriptContract = contracts.cat.contract;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const requireValue = condition => { if (!condition) throw new ScriptHostError('INVALID_ARGUMENT'); };
const identifier = value => { requireValue(typeof value === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$(?![\s\S])/.test(value)); return value; };
const integer = value => requireValue(Number.isSafeInteger(value) && value > 0);
const name = value => requireValue(typeof value === 'string' && value.trim().length > 0);
const sourceURL = value => {
  name(value);
  let url;
  try { url = new URL(value); } catch { requireValue(false); }
  requireValue(['http:', 'https:'].includes(url.protocol));
};
const snapshot = value => { validateJSONValue(value); return structuredClone(value); };
const failure = error => ({ ok: false, contract: catScriptContract,
  code: error instanceof ScriptHostError && contracts.cat.codes.has(error.code) ? error.code : 'SCRIPT_EXCEPTION' });
const success = value => { validateJSONValue(value ?? null); return { ok: true, contract: catScriptContract, value: value ?? null }; };

function linkedSignal(external, lifetime) {
  if (!external) return { signal: lifetime, release() {} };
  const controller = new AbortController();
  const state = { controller };
  const entries = [external, lifetime].map(signal => {
    const entry = { signal, abort: null, attached: false, failure: null };
    entry.abort = () => state.controller?.abort(entry.signal.reason);
    return entry;
  });
  let released = false;
  const release = () => {
    if (released) return;
    for (const entry of entries) {
      if (!entry.attached) continue;
      try {
        entry.signal.removeEventListener('abort', entry.abort);
        entry.attached = false;
        entry.failure = null;
      } catch (error) { entry.failure = error; }
    }
    const failure = entries.find(entry => entry.attached)?.failure;
    state.controller = null;
    released = true;
    if (failure) throw failure;
  };
  try {
    for (const entry of entries) {
      if (entry.signal.aborted) { entry.abort(); break; }
      entry.signal.addEventListener('abort', entry.abort, { once: true });
      entry.attached = true;
    }
  } catch (error) {
    try { release(); } catch {}
    throw error;
  }
  return { signal: controller.signal, release };
}

/** Passive Cat protocol owner; the caller owns its data-root and session. */
export function createCatScriptModule({ dataRoot, runtimeFactory = createCatRuntime, clock } = {}) {
  requireValue(typeof dataRoot === 'string' && typeof runtimeFactory === 'function');
  const sources = new SourceCandidates({ module: 'cat', fail: code => new ScriptHostError(code) });
  const lifetime = new AbortController();
  const pending = new Set();
  let runtime, closing, closed = false;
  const get = () => runtime ??= runtimeFactory({ dataRoot, clock });
  const methods = {};
  function method(name, allowed, required, body) {
    methods['cat.script.' + name] = (input = {}, context = {}) => {
      const task = (async () => {
        try {
          if (closed) throw new ScriptHostError('HOST_CLOSED');
          requireValue(object(input) && Object.keys(input).every(key => allowed.includes(key)) &&
            required.every(key => Object.hasOwn(input, key)));
          if (context.signal?.aborted) throw new ScriptHostError('CANCELLED');
          let captured;
          try { captured = snapshot(input); } catch { throw new ScriptHostError('INVALID_ARGUMENT'); }
          const linked = linkedSignal(context.signal, lifetime.signal);
          let value, error;
          try {
            if (linked.signal.aborted) throw new ScriptHostError('CANCELLED');
            value = await body(captured, linked.signal);
            if (linked.signal.aborted) throw new ScriptHostError('CANCELLED');
          } catch (failure) { error = failure; }
          try { linked.release(); } catch (failure) { error ??= failure; }
          if (error) throw error;
          return success(value);
        } catch (error) { return failure(error); }
      })();
      pending.add(task);
      task.finally(() => pending.delete(task)).catch(() => {});
      return task;
    };
  }
  method('start', [], [], (_input, signal) => get().start({ signal }));
  method('status', [], [], () => get().status());
  method('refresh', [], [], (_input, signal) => get().refresh({ signal }));
  method('install', ['installationId', 'sourceRevision', 'config', 'version', 'enabled',
    'name', 'sourceType', 'sourceUrl'],
    ['installationId', 'sourceRevision'], async (input, signal) => {
      identifier(input.installationId);
      integer(input.sourceRevision);
      requireValue(input.config === undefined || object(input.config));
      requireValue(input.version === undefined || typeof input.version === 'string' && input.version.length > 0 && input.version.length <= 128);
      requireValue(input.enabled === undefined || typeof input.enabled === 'boolean');
      if (input.name !== undefined) name(input.name);
      requireValue(input.sourceType === undefined || ['local', 'network'].includes(input.sourceType));
      if (input.sourceUrl !== undefined && input.sourceUrl !== null) sourceURL(input.sourceUrl);
      if (input.sourceType !== undefined || input.sourceUrl !== undefined) {
        const previous = (await get().status()).instances.find(item => item.installationId === input.installationId);
        const type = input.sourceType ?? previous?.sourceType ?? 'local';
        const url = input.sourceUrl === undefined ? previous?.sourceUrl ?? null : input.sourceUrl;
        if (type === 'network') sourceURL(url);
        else requireValue(url === null);
      }
      if (signal.aborted) throw new ScriptHostError('CANCELLED');
      const uploaded = sources.take(input.installationId, input.sourceRevision);
      try {
        const { sourceRevision, ...spec } = input;
        const result = await get().install({ ...spec, source: uploaded.source,
          contentHash: uploaded.contentHash }, { signal });
        if (signal.aborted) throw new ScriptHostError('CANCELLED');
        sources.finish(uploaded, true);
        return result;
      } catch (error) { sources.finish(uploaded); throw error; }
    });
  method('import', ['installationId', 'name', 'sourceUrl', 'enabled'],
    ['installationId', 'name', 'sourceUrl'], (input, signal) => {
      identifier(input.installationId); name(input.name); sourceURL(input.sourceUrl);
      requireValue(input.enabled === undefined || typeof input.enabled === 'boolean');
      return get().import(input, { signal });
    });
  method('edit', ['installationId', 'name', 'sourceUrl'], ['installationId', 'name'], (input, signal) => {
    identifier(input.installationId); name(input.name);
    if (input.sourceUrl !== undefined) sourceURL(input.sourceUrl);
    return get().edit(input, { signal });
  });
  method('update', ['installationId', 'sourceRevision'], ['installationId'], async (input, signal) => {
    identifier(input.installationId);
    if (input.sourceRevision !== undefined) integer(input.sourceRevision);
    const previous = (await get().status()).instances.find(item => item.installationId === input.installationId);
    if (!previous || previous.sourceHash === null) throw new ScriptHostError('NOT_FOUND');
    requireValue(previous.sourceType === 'local'
      ? input.sourceRevision !== undefined : input.sourceRevision === undefined);
    if (signal.aborted) throw new ScriptHostError('CANCELLED');
    if (input.sourceRevision === undefined) return get().update(input, { signal });
    const uploaded = sources.take(input.installationId, input.sourceRevision);
    try {
      const result = await get().update({ installationId: input.installationId,
        source: uploaded.source, contentHash: uploaded.contentHash }, { signal });
      sources.finish(uploaded, true);
      return result;
    } catch (error) { sources.finish(uploaded); throw error; }
  });
  method('restart', ['installationId'], ['installationId'], (input, signal) => {
    identifier(input.installationId);
    return get().restart(input.installationId, { signal });
  });
  // CatRuntime owns the installation transaction and its pre-queue stop.
  // An extra module tail would hide that stop behind the blocked initializer.
  method('enable', ['installationId', 'enabled'], ['installationId', 'enabled'], (input, signal) => {
    identifier(input.installationId);
    requireValue(typeof input.enabled === 'boolean');
    return get().setEnabled(input.installationId, input.enabled, { signal });
  });
  method('uninstall', ['installationId'], ['installationId'], async (input, signal) => {
    identifier(input.installationId);
    try { return await get().uninstall(input.installationId, { signal }); }
    finally { sources.remove(input.installationId); }
  });
  method('invoke', ['installationId', 'generation', 'siteId', 'operation', 'payload'],
    ['installationId', 'siteId', 'operation'], (input, signal) => {
      identifier(input.installationId); identifier(input.operation);
      requireValue(typeof input.siteId === 'string' && input.siteId.length > 0 &&
        input.siteId.length <= 1024 && !/[\u0000-\u001f\u007f]/.test(input.siteId));
      if (input.generation !== undefined) integer(input.generation);
      requireValue(input.payload === undefined || object(input.payload));
      return get().invoke({ ...input, payload: input.payload ?? {} }, { signal });
    });
  method('management.describe', ['installationId', 'generation'], ['installationId'], (input, signal) => {
    identifier(input.installationId);
    if (input.generation !== undefined) integer(input.generation);
    return get().management(input.installationId, input.generation, { signal });
  });
  method('management.sync', ['installationId', 'generation', 'provider', 'credentials'],
    ['installationId', 'provider', 'credentials'], (input, signal) => {
      identifier(input.installationId); identifier(input.provider);
      if (input.generation !== undefined) integer(input.generation);
      requireValue(object(input.credentials));
      return get().sync(input.installationId, input.generation,
        { provider: input.provider, credentials: input.credentials }, { signal });
    });
  async function upload(input, bytes, { signal } = {}) {
    try {
      if (closed) throw new ScriptHostError('HOST_CLOSED');
      return success(sources.put(input, bytes, signal));
    } catch (error) { return failure(error); }
  }
  function close() {
    return closing ??= (async () => {
      closed = true;
      let error;
      try { lifetime.abort(); } catch (failure) { error ??= failure; }
      sources.close();
      try { await runtime?.close(); } catch (failure) { error ??= failure; }
      await Promise.allSettled([...pending]);
      runtime = undefined;
      if (error) throw error;
    })();
  }
  return { id: 'cat', version: contracts.cat.version, contract: catScriptContract, methods, upload, close };
}
