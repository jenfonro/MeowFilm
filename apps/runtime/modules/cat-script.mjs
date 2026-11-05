import { createCatRuntime, SourceTransfers, ScriptHostError } from '@meowfilm/script-host';

export const catScriptContract = 'cat.script.v1';
const object = value => value && typeof value === 'object' && !Array.isArray(value);
const requireValue = condition => { if (!condition) throw new ScriptHostError('INVALID_ARGUMENT'); };
const text = value => { requireValue(typeof value === 'string' && value.length > 0); return value; };
const json = value => {
  try { return JSON.parse(text(value)); } catch { throw new ScriptHostError('INVALID_ARGUMENT'); }
};
const decode = bytes => {
  try {
    const source = bytes.toString('utf8');
    requireValue(!source.startsWith('\uFEFF') && source.trim().length > 0 &&
      Buffer.from(source).equals(bytes)); return source;
  } catch { throw new ScriptHostError('SCRIPT_PROTOCOL_ERROR'); }
};
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

/** Passive protocol owner. Data-root selection and process/pipe ownership stay
 * in the platform. Arbitrary Cat JSON is a string/blob on the scalar-key wire,
 * never an ASCII-key-normalized substitute for the original Cat result. */
export function createCatScriptModule({ dataRoot, runtimeFactory = createCatRuntime, clock = Date.now } = {}) {
  requireValue(typeof dataRoot === 'string' && typeof runtimeFactory === 'function');
  const transfers = new SourceTransfers({ clock });
  const lifetime = new AbortController();
  let runtime, closing, closed = false;
  const get = () => runtime ??= runtimeFactory({ dataRoot });
  const methods = {};
  function method(name, allowed, body) {
    methods['cat.script.' + name] = async (input = {}, context = {}) => {
      try {
        if (closed) throw new ScriptHostError('HOST_CLOSED');
        requireValue(object(input) && Object.keys(input).every(key => allowed.includes(key)));
        if (context.signal?.aborted) throw new ScriptHostError('CANCELLED');
        const captured = structuredClone(input);
        const linked = linkedSignal(context.signal, lifetime.signal);
        let value, failure;
        try {
          if (linked.signal.aborted) throw new ScriptHostError('CANCELLED');
          value = await body(captured, linked.signal);
          if (linked.signal.aborted) throw new ScriptHostError('CANCELLED');
        } catch (error) { failure = error; }
        try { linked.release(); } catch (error) { failure ??= error; }
        if (failure) throw failure;
        return { ok: true, contract: catScriptContract, ...value };
      } catch (error) {
        return { ok: false, contract: catScriptContract,
          code: error instanceof ScriptHostError ? error.code : 'SCRIPT_EXCEPTION' };
      }
    };
  }
  const result = value => transfers.result(value);
  method('stage.begin', ['length', 'sha256', 'kind'], input => transfers.begin(input));
  method('stage.chunk', ['token', 'offset', 'data'], input => transfers.chunk(input));
  method('stage.abort', ['token'], input => { transfers.release(text(input.token)); return { released: true }; });
  method('blob.read', ['token', 'offset'], input => transfers.read(input));
  method('blob.release', ['token'], input => { transfers.release(text(input.token)); return { released: true }; });
  method('start', [], async () => result(await get().start()));
  method('status', [], async () => result(await get().status()));
  method('refresh', [], async () => result(await get().refresh()));
  method('install', ['installationId', 'token', 'config', 'version', 'enabled'], async input => {
    const bytes = transfers.take(text(input.token), 'source');
    try {
      return result(await get().install({ installationId: text(input.installationId), source: decode(bytes),
        ...(input.config === undefined ? {} : { config: json(input.config) }),
        ...(input.version === undefined ? {} : { version: text(input.version) }),
        ...(input.enabled === undefined ? {} : { enabled: input.enabled }) }));
    } finally { bytes.fill(0); }
  });
  method('enable', ['installationId', 'enabled'], async input =>
    result(await get().setEnabled(text(input.installationId), input.enabled)));
  method('uninstall', ['installationId'], async input => result(await get().uninstall(text(input.installationId))));
  method('invoke', ['installationId', 'generation', 'siteId', 'operation', 'payload', 'payloadToken'], async (input, signal) => {
    requireValue(!(input.payload !== undefined && input.payloadToken !== undefined));
    let payload;
    if (input.payloadToken !== undefined) {
      const bytes = transfers.take(input.payloadToken, 'json');
      try { payload = json(decode(bytes)); } finally { bytes.fill(0); }
    } else payload = input.payload === undefined ? {} : json(input.payload);
    return result(await get().invoke({ installationId: text(input.installationId), generation: input.generation,
      siteId: text(input.siteId), operation: text(input.operation), payload }, { signal }));
  });
  method('management.describe', ['installationId', 'generation'], async input =>
    result(await get().management(text(input.installationId), input.generation)));
  method('management.sync', ['installationId', 'generation', 'provider', 'credentials'], async input =>
    result(await get().sync(text(input.installationId), input.generation,
      { provider: text(input.provider), credentials: json(input.credentials) })));
  async function close() {
    if (closing) return closing;
    const current = runtime;
    closed = true;
    closing = (async () => {
      let failure;
      try { lifetime.abort(); } catch (error) { failure ??= error; }
      try { transfers.close(); } catch (error) { failure ??= error; }
      try { await current?.close(); } catch (error) { failure ??= error; }
      if (runtime === current) runtime = undefined;
      if (failure) throw failure;
    })();
    return closing;
  }
  return { id: 'cat', version: '0.1.0', methods, close };
}
