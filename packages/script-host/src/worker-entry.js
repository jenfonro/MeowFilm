import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { parentPort, workerData } from 'node:worker_threads';
import { CatSession } from './cat-session.js';
import { ownedExtension } from './cat-output.js';
import { decodeWorkerPayload, encodeWorkerPayload, event, failure, response, validateMessage } from './worker-protocol.js';
import { clone, fault, safeError } from './errors.js';

if (workerData.protocol !== 'cat') throw fault('UNSUPPORTED_PROTOCOL');
const sessions = new Map();
const cancellableRequests = new Map();
let latestSettings;
let latestAggregate;

function send(message, transfer = []) {
  parentPort.postMessage(message, transfer);
}

function emit(name, value) {
  try { send(event(name, value)); }
  catch { /* Event delivery cannot outlive a worker that is already closing. */ }
}

function makeCancellable(message, start) {
  const controller = new AbortController();
  const entry = { controller, detach: () => cancellableRequests.delete(message.id) };
  cancellableRequests.set(message.id, entry);
  return Promise.resolve().then(() => start(controller.signal)).finally(entry.detach);
}

function catResult(value, newProxies) {
  return {
    result: value && typeof value === 'object' && value[ownedExtension] ? { ...value } : value,
    newProxies: newProxies ?? [],
    ...(value && typeof value === 'object' && value[ownedExtension] ? { owned: true } : {}),
  };
}

function catEvent(session) {
  const snapshot = session.endpointSnapshot();
  return { installationId: session.installationId, generation: session.generation, ...snapshot };
}

function sessionEvent(session, value = {}) {
  return { ...value, installationId: session.installationId,
    generation: session.generation };
}

function applySettingsSnapshot(value) {
  if (!value || typeof value !== 'object') return;
  if (value.settings !== undefined) latestSettings = cloneJSONValue(value.settings);
  if (value.aggregate !== undefined) latestAggregate = clone(value.aggregate);
  const session = sessions.get(value.installationId);
  if (!session) return;
  if (latestSettings !== undefined) session.applySettings?.(cloneJSONValue(latestSettings));
  if (latestAggregate !== undefined) session.applyAggregate?.(clone(latestAggregate));
}

function createSession(spec) {
  const { dataRoot, settings, aggregate, protocol,
    observeScriptErrors, ...input } = spec;
  let session;
  session = new CatSession({
    ...input, dataRoot, settings, aggregate,
    onDiagnostic: value => emit('diagnostic', sessionEvent(session, value)),
    onScriptError: spec.observeScriptErrors
      ? value => emit('scriptError', sessionEvent(session, value)) : undefined,
    onNetwork: value => emit('network', sessionEvent(session, value)),
    onEndpointsChanged: () => {
      if (!session.live) return;
      emit('endpoints', catEvent(session));
    },
    onActivate: () => {
      if (!session.live) return;
      emit('activated', catEvent(session));
    },
    onStop: () => {
      emit('stopped', { installationId: session.installationId,
        generation: session.generation });
    },
    onFailure: (value, state) => emit('failure', sessionEvent(session, {
      code: safeError(value).code,
      phase: state?.ready ? 'running' : 'initialization',
    })),
  });
  if (latestSettings !== undefined) session.applySettings?.(cloneJSONValue(latestSettings));
  if (latestAggregate !== undefined) session.applyAggregate?.(clone(latestAggregate));
  sessions.set(spec.installationId, session);
  return session;
}

async function startSession(session, spec = {}, signal = null) {
  if (signal?.aborted) throw fault('CANCELLED');
  const abort = () => {
    const reason = signal.reason instanceof Error ? signal.reason : fault('CANCELLED');
    try { Promise.resolve(session.stop(reason)).catch(() => {}); }
    catch { /* The initial failure is authoritative. */ }
  };
  signal?.addEventListener('abort', abort, { once: true });
  try {
    await session.start(spec);
  } finally {
    try { signal?.removeEventListener('abort', abort); } catch {}
  }
  emit('capabilities', sessionEvent(session, {
    capabilities: session.capabilities,
  }));
  const value = { live: session.live, capabilities: session.capabilities,
    stats: session.stats() };
  value.cat = { ...session.endpointSnapshot(),
    config: session.config, configOrigin: session.configOrigin };
  return value;
}

function sessionFor(params) {
  const session = sessions.get(params?.installationId);
  if (!session) throw fault('NOT_FOUND');
  return session;
}

parentPort.on('message', raw => {
  let message;
  try {
    message = validateMessage(raw);
    if (message.kind === 'request') message = { ...message, params: decodeWorkerPayload(message.params) };
    else if (message.kind === 'event' && message.event === 'settings')
      message = { ...message, value: decodeWorkerPayload(message.value) };
  }
  catch (error) {
    if (raw?.kind === 'request' && Number.isSafeInteger(raw.id)) {
      try { send(failure(raw.id, error)); } catch { /* The worker may already be closing. */ }
    }
    return;
  }
  if (message.kind === 'event') {
    // Settings are an ordered, reply-free snapshot. Processing this message
    // before the next request preserves publish-then-call visibility.
    if (message.event === 'settings') applySettingsSnapshot(message.value);
    return;
  }
  if (message.kind === 'cancel') {
    cancellableRequests.get(message.id)?.controller.abort(fault(message.code));
    return;
  }
  if (message.kind !== 'request') return;
  Promise.resolve().then(async () => {
    switch (message.method) {
      case 'start': {
        const session = createSession(message.params);
        return makeCancellable(message, signal => startSession(session, message.params, signal));
      }
      case 'activate':
        return makeCancellable(message, signal => {
          if (signal.aborted) throw fault('CANCELLED');
          const session = sessionFor(message.params);
          session.activate();
          emit('stats', sessionEvent(session, session.stats()));
          return null;
        });
      case 'invoke': {
        const session = sessionFor(message.params);
        return makeCancellable(message, signal => session.invoke(message.params.request, {
          ...message.params.options, signal,
        })).then(value => catResult(value, session.takeNewProxies()))
          .finally(() => emit('stats', sessionEvent(session, session.stats())));
      }
      case 'management.detect':
        return makeCancellable(message, signal => {
          const session = sessionFor(message.params);
          return import('./cat-management.js').then(({ detectCatManagement }) =>
            detectCatManagement(session, { signal })).then(value => ({ ...value }));
        });
      case 'management.sync':
        return makeCancellable(message, signal => {
          const session = sessionFor(message.params);
          return import('./cat-management.js').then(({ syncCatManagement }) =>
            syncCatManagement(session, message.params.input, { signal }));
        });
      case 'enhanceM3U8': {
        const session = sessionFor(message.params);
        const value = message.params.value;
        if (message.params.owned && value && typeof value === 'object')
          Object.defineProperty(value, ownedExtension, { value: true });
        return makeCancellable(message, signal =>
          session.enhanceM3U8(value, {
            owned: Boolean(message.params.owned), signal,
          })).then(value =>
            catResult(session.enhanceResult(value), session.takeNewProxies()));
      }
      case 'stop': {
        const session = sessionFor(message.params);
        await session.stop(message.params.reason ? fault(message.params.reason) : undefined);
        sessions.delete(message.params.installationId);
        // A worker owns exactly one installation; close its control port after
        // the stop response has been queued so it exits naturally.
        setImmediate(() => parentPort.close());
        return null;
      }
      case 'stats':
        return sessionFor(message.params).stats();
      case 'probe':
        return { live: sessionFor(message.params).live, generation: message.params.generation };
      default:
        throw fault('UNSUPPORTED_OPTION');
    }
  }).then(value => {
    try { send(response(message.id, encodeWorkerPayload(value ?? null))); }
    catch (error) {
      try { send(failure(message.id, safeError(error, 'INVALID_ARGUMENT'))); } catch { /* The worker may already be closing. */ }
    }
  }, error => {
    try { send(failure(message.id, error)); } catch { /* The worker may already be closing. */ }
  });
});
