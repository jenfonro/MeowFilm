import { parentPort, workerData } from 'node:worker_threads';
import { LXSession } from './lx-session.js';
import { CatSession } from './cat-session.js';
import { ownedExtension } from './cat-output.js';
import { cancel, event, failure, request, response, validateMessage } from './worker-protocol.js';
import { clone, fault, safeError } from './errors.js';

const PortableSourceSession = workerData.protocol === 'source'
  ? (await import('./portable-source-session.js')).PortableSourceSession : null;
const sessions = new Map();
let nextAdapterId = 1;
const adapterPending = new Map();
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

function adapterCall(method, params, signal) {
  const id = nextAdapterId++;
  return new Promise((resolve, reject) => {
    const entry = { resolve, reject, signal, abort: null, network: method === 'network' };
    entry.abort = () => {
      if (!adapterPending.delete(id)) return;
      signal?.removeEventListener('abort', entry.abort);
      const reason = safeError(signal?.reason ?? fault('CANCELLED'), 'CANCELLED');
      try { send(cancel(id, reason.code)); }
      catch { /* The parent may already be stopping this worker. */ }
      reject(reason);
    };
    if (signal?.aborted) { reject(fault('CANCELLED')); return; }
    adapterPending.set(id, entry);
    signal?.addEventListener('abort', entry.abort, { once: true });
    try { send(request(id, method, params)); }
    catch (error) {
      adapterPending.delete(id);
      signal?.removeEventListener('abort', entry.abort);
      reject(safeError(error));
    }
  });
}

function networkAdapter(input) {
  const signal = input.signal;
  if (!signal) throw fault('PERMISSION_DENIED');
  return adapterCall('network', {
    associationId: input.associationId,
    url: input.url,
    options: clone(input.options ?? {}),
    scope: clone(input.scope ?? {}),
  }, signal);
}

function storageAdapter(operation, input) {
  if (!input.signal) throw fault('PERMISSION_DENIED');
  return adapterCall('storage.' + operation, {
    associationId: input.associationId,
    installationId: input.installationId,
    generation: input.generation,
    siteId: input.siteId,
    requestId: input.requestId,
    key: input.key,
    ...(operation === 'set' ? { value: clone(input.value) } : {}),
  }, input.signal);
}

function sourceCapability(name, input, context = {}) {
  const { signal, ...details } = context;
  if (!signal) throw fault('PERMISSION_DENIED');
  return adapterCall('capability', { name, input: clone(input), context: clone(details) }, signal);
}

function makeCancellable(message, start) {
  const controller = new AbortController();
  const entry = { controller, detach: () => cancellableRequests.delete(message.id) };
  cancellableRequests.set(message.id, entry);
  return Promise.resolve().then(() => start(controller.signal)).finally(entry.detach);
}

function catResult(value, newProxies) {
  return {
    result: value,
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
  if (value.settings !== undefined) latestSettings = clone(value.settings);
  if (value.aggregate !== undefined) latestAggregate = clone(value.aggregate);
  const session = sessions.get(value.installationId);
  if (!session) return;
  if (latestSettings !== undefined) session.applySettings?.(clone(latestSettings));
  if (latestAggregate !== undefined) session.applyAggregate?.(clone(latestAggregate));
}

function createSession(spec) {
  const Session = PortableSourceSession
    ? PortableSourceSession
    : spec.protocol === 'cat' ? CatSession : LXSession;
  const { dataRoot, settings, aggregate, protocol, network, storage,
    observeScriptErrors, sourceMetadata, ...input } = spec;
  const constructor = Session === CatSession
    ? { ...input, dataRoot, settings, aggregate }
    : input;
  let session;
  session = new Session({
    ...constructor,
    wasmModule: workerData.wasmModule,
    ...(Session === PortableSourceSession ? {
      capabilities: { version: 1, call: sourceCapability },
      metadata: sourceMetadata,
    } : {}),
    network: spec.network ? networkAdapter : undefined,
    storage: spec.storage ? {
      get: input => storageAdapter('get', input),
      set: input => storageAdapter('set', input),
      delete: input => storageAdapter('delete', input),
    } : undefined,
    onDiagnostic: value => emit('diagnostic', sessionEvent(session, value)),
    onScriptError: spec.observeScriptErrors
      ? value => emit('scriptError', sessionEvent(session, value)) : undefined,
    ...(Session === CatSession ? {
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
    } : {}),
    onFailure: (value, state) => emit('failure', sessionEvent(session, {
      code: safeError(value).code,
      memoryLimitExceeded: Boolean(value?.memoryLimitExceeded),
      phase: state?.ready ? 'running' : 'initialization',
    })),
  });
  if (Session === CatSession) {
    if (latestSettings !== undefined) session.applySettings?.(clone(latestSettings));
    if (latestAggregate !== undefined) session.applyAggregate?.(clone(latestAggregate));
  }
  sessions.set(spec.installationId, session);
  if (typeof session.send === 'function') {
    const originalSend = session.send.bind(session);
    session.send = (name, data) => {
      const value = originalSend(name, data);
      if (name === 'updateAlert') emit('alert', sessionEvent(session, { alert: clone(session.alert) }));
      return value;
    };
  }
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
    await (spec.protocol === 'cat' ? session.start(spec) : session.start());
  } finally {
    try { signal?.removeEventListener('abort', abort); } catch {}
  }
  emit('capabilities', sessionEvent(session, {
    capabilities: session.capabilities, alert: session.alert,
  }));
  const value = { live: session.live, capabilities: session.capabilities, alert: session.alert,
    stats: session.stats() };
  if (spec.protocol === 'cat') {
    value.cat = { ...session.endpointSnapshot(),
      config: session.config, configOrigin: session.configOrigin };
  }
  return value;
}

function sessionFor(params) {
  const session = sessions.get(params?.installationId);
  if (!session) throw fault('NOT_FOUND');
  return session;
}

parentPort.on('message', raw => {
  let message;
  try { message = validateMessage(raw); }
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
  if (message.kind === 'response') {
    const entry = adapterPending.get(message.id);
    if (!entry) return;
    adapterPending.delete(message.id);
    entry.signal?.removeEventListener('abort', entry.abort);
    if (message.ok) {
      const body = message.frames?.[0];
      if (entry.network) {
        entry.resolve({ ...message.value, ...(body ? { body: Buffer.from(body) } : {}) });
      } else {
        entry.resolve(message.value);
      }
    } else entry.reject(fault(message.code));
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
        const associationId = message.params.options.associationId;
        return makeCancellable(message, signal => session.invoke(message.params.request, {
          ...message.params.options, signal, associationId,
        })).then(value => session instanceof CatSession
          ? catResult(value, session.takeNewProxies()) : value)
          .finally(() => emit('stats', sessionEvent(session, session.stats())));
      }
      case 'management.detect':
        return makeCancellable(message, signal => {
          const session = sessionFor(message.params);
          return import('./cat-management.js').then(({ detectCatManagement }) =>
            detectCatManagement(session, { signal }));
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
    try { send(response(message.id, value)); } catch { /* The worker may already be closing. */ }
  }, error => {
    try { send(failure(message.id, error)); } catch { /* The worker may already be closing. */ }
  });
});
