import { performance } from 'node:perf_hooks';
import { createCatRuntime, ScriptHostError } from '@meowfilm/script-host';
import { SourceCandidates } from './source-candidates.mjs';
import { runCatSourceProbe } from './cat-probe.mjs';
import { contracts } from '../contracts.mjs';

const catScriptContract = contracts.cat.contract;
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
const failure = error => ({ ok: false, contract: catScriptContract,
  code: error instanceof ScriptHostError && typeof error.code === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(error.code)
    ? error.code : 'SCRIPT_EXCEPTION' });
const success = value => ({ ok: true, contract: catScriptContract, value: value ?? null });

/** Passive Cat protocol owner; the caller owns its data-root and session. */
export function createCatScriptModule({ dataRoot, runtimeFactory = createCatRuntime, configuration, debug } = {}) {
  requireValue(typeof dataRoot === 'string' && typeof runtimeFactory === 'function');
  const sources = new SourceCandidates({ module: 'cat', fail: code => new ScriptHostError(code) });
  const lifetime = new AbortController();
  const pending = new Set();
  let runtime, closing, closed = false;
  const get = () => runtime ??= runtimeFactory({ dataRoot, configuration, debug });
  const nativeWrites = new Set(['install', 'import', 'edit', 'enable', 'uninstall']);
  const methods = {};
  function method(name, parameters, body) {
    methods['cat.script.' + name] = (input = {}, context = {}) => {
      const task = (async () => {
        const startedAt = debug ? performance.now() : 0;
        const cat = ['install', 'import', 'update', 'restart', 'enable', 'uninstall', 'status'].includes(name)
          ? 'runtime-node' : 'film-catalog';
        const sub = `cat.${name}`;
        debug?.emit({ level: 'debug', cat, sub, event: 'cat.call.start', data: { name, input } });
        try {
          if (closed) throw new ScriptHostError('HOST_CLOSED');
          if (configuration && nativeWrites.has(name)) throw new ScriptHostError('PERMISSION_DENIED');
          requireValue(object(input));
          requireValue(context.signal === undefined || context.signal instanceof AbortSignal);
          if (context.signal?.aborted) throw new ScriptHostError('CANCELLED');
          const captured = Object.fromEntries(parameters.filter(key => Object.hasOwn(input, key))
            .map(key => [key, input[key]]));
          const signal = context.signal ? AbortSignal.any([context.signal, lifetime.signal]) : lifetime.signal;
          if (signal.aborted) throw new ScriptHostError('CANCELLED');
          const value = await body(captured, signal, context);
          if (signal.aborted) throw new ScriptHostError('CANCELLED');
          debug?.emit({
            level: 'debug',
            cat,
            sub,
            event: 'cat.call.end',
            durationMs: debug ? performance.now() - startedAt : undefined,
            data: { name, value },
          });
          return success(value);
        } catch (error) {
          const reply = failure(error);
          debug?.emit({
            level: 'warn',
            cat,
            sub,
            event: 'cat.call.error',
            durationMs: debug ? performance.now() - startedAt : undefined,
            data: { name, input, code: reply.code },
            error,
          });
          return reply;
        }
      })();
      pending.add(task);
      task.finally(() => pending.delete(task)).catch(() => {});
      return task;
    };
  }
  method('start', [], (_input, signal) => get().start({ signal }));
  method('status', [], () => get().status());
  method('refresh', [], (_input, signal) => get().refresh({ signal }));
  method('install', ['installationId', 'sourceRevision', 'config', 'version', 'enabled',
    'name', 'sourceType', 'sourceUrl'], async (input, signal) => {
      identifier(input.installationId);
      integer(input.sourceRevision);
      requireValue(input.config === undefined || object(input.config));
      requireValue(input.version === undefined || typeof input.version === 'string' && input.version.length > 0);
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
  method('import', ['installationId', 'name', 'sourceUrl', 'enabled'], (input, signal) => {
      identifier(input.installationId); name(input.name); sourceURL(input.sourceUrl);
      requireValue(input.enabled === undefined || typeof input.enabled === 'boolean');
      return get().import(input, { signal });
    });
  method('edit', ['installationId', 'name', 'sourceUrl'], (input, signal) => {
    identifier(input.installationId); name(input.name);
    if (input.sourceUrl !== undefined) sourceURL(input.sourceUrl);
    return get().edit(input, { signal });
  });
  method('update', configuration ? ['installationId'] : ['installationId', 'sourceRevision'], async (input, signal) => {
    identifier(input.installationId);
    if (configuration) {
      return get().updateConfigured(input.installationId, { signal });
    }
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
      if (signal.aborted) throw new ScriptHostError('CANCELLED');
      sources.finish(uploaded, true);
      return result;
    } catch (error) { sources.finish(uploaded); throw error; }
  });
  method('restart', ['installationId'], (input, signal) => {
    identifier(input.installationId);
    return get().restart(input.installationId, { signal });
  });
  // CatRuntime owns the installation transaction and its pre-queue stop.
  // An extra module tail would hide that stop behind the blocked initializer.
  method('enable', ['installationId', 'enabled'], (input, signal) => {
    identifier(input.installationId);
    requireValue(typeof input.enabled === 'boolean');
    return get().setEnabled(input.installationId, input.enabled, { signal });
  });
  method('uninstall', ['installationId'], async (input, signal) => {
    identifier(input.installationId);
    try { return await get().uninstall(input.installationId, { signal }); }
    finally { sources.remove(input.installationId); }
  });
  method('invoke', ['installationId', 'generation', 'siteId', 'operation', 'payload'], (input, signal, context) => {
      identifier(input.installationId); identifier(input.operation);
      requireValue(typeof input.siteId === 'string' && input.siteId.length > 0 &&
        input.siteId.length <= 1024 && !/[\u0000-\u001f\u007f]/.test(input.siteId));
      if (input.generation !== undefined) integer(input.generation);
      requireValue(input.payload === undefined || object(input.payload));
      return get().invoke({ ...input, payload: input.payload ?? {} }, { signal, ...(context.mediaPurpose === undefined ? {} : { mediaPurpose: context.mediaPurpose }) });
    });
  method('probe', ['installationId', 'generation', 'siteId', 'siteApi', 'actions'], (input, signal) => {
    identifier(input.installationId);
    if (input.generation !== undefined) integer(input.generation);
    requireValue(typeof input.siteId === 'string' && input.siteId.length > 0 &&
      input.siteId.length <= 1024 && !/[\u0000-\u001f\u007f]/.test(input.siteId));
    requireValue(input.siteApi === undefined ||
      typeof input.siteApi === 'string' && !/[\u0000-\u001f\u007f]/.test(input.siteApi));
    requireValue(Array.isArray(input.actions) &&
      input.actions.every(action => typeof action === 'string' && action.length > 0 && !/[\u0000-\u001f\u007f]/.test(action)));
    return runCatSourceProbe({
      invoke: (operation, payload, activeSignal) => get().invoke({
        installationId: input.installationId,
        ...(input.generation === undefined ? {} : { generation: input.generation }),
        siteId: input.siteId,
        operation,
        payload,
      }, { signal: activeSignal }),
      actions: input.actions,
      siteApi: input.siteApi ?? '',
      signal,
      debug,
    });
  });
  method('management.describe', ['installationId', 'generation'], (input, signal) => {
    identifier(input.installationId);
    if (input.generation !== undefined) integer(input.generation);
    return get().management(input.installationId, input.generation, { signal });
  });
  method('management.sync', ['installationId', 'generation', 'provider', 'credentials'], (input, signal) => {
      identifier(input.installationId); identifier(input.provider);
      if (input.generation !== undefined) integer(input.generation);
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
  return { id: 'cat', version: contracts.cat.version, contract: catScriptContract,
    diagnostics() {
      const runtimeDiag = runtime?.diagnostics?.() ?? null;
      return {
        closed,
        pendingModuleCalls: pending.size,
        ...(runtimeDiag ?? { installations: 0, liveWorkers: 0, readyWorkers: 0 }),
      };
    },
    async settingsChanged() {
      if (!configuration || closed) throw new ScriptHostError('HOST_CLOSED');
      // Applying a saved input is owned by the module lifetime, not the short notification HTTP request.
      let published, failed, settled = false;
      const resolvePublication = () => { settled = true; published(); };
      const rejectPublication = error => { settled = true; failed(error); };
      const publication = new Promise((resolve, reject) => { published = resolve; failed = reject; });
      const abort = () => rejectPublication(new ScriptHostError('CANCELLED'));
      lifetime.signal.addEventListener('abort', abort, { once: true });
      const work = get().refresh({ signal: lifetime.signal, onSettingsPublished: resolvePublication });
      pending.add(work);
      work.finally(() => pending.delete(work)).catch(() => {});
      work.then(resolvePublication, error => {
        if (settled) {
          debug?.emit({
            level: 'warn',
            cat: 'runtime-node',
            sub: 'cat.settingsChanged',
            event: 'cat.refresh.background_error',
            data: { code: error?.code ?? null },
            error,
          });
        }
        rejectPublication(error);
      });
      // The next owner must observe the actual routing snapshot. Source
      // downloads and generation activation continue after this local barrier.
      try { await publication; }
      finally { lifetime.signal.removeEventListener('abort', abort); }
    },
    connectAccounts: accounts => {
      get().connectAccounts(accounts);
      // Start schedules generation-owned synchronization as each background
      // initializer publishes, rather than waiting for a later invoke.
      const work = get().start({ signal: lifetime.signal });
      pending.add(work);
      work.finally(() => pending.delete(work)).catch(() => {});
      return work;
    },
    onAccountSaved: listener => get().onAccountSaved(listener),
    onStateChanged: listener => get().onStateChanged(listener),
    methods, upload, close };
}
