import { novelRuleSource } from '@meowfilm/novel-rules';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';

const fault = code => Object.assign(new Error(code), { code });
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const requests = {
  identity: ['book'],
  merge: ['books'],
  matchChapter: ['chapters', 'anchor'],
  transform: ['title', 'blocks', 'book', 'source', 'rules', 'conversion'],
  matchUrl: ['pattern', 'url'],
};
const nativeCodes = {
  INVALID_ARGUMENT: 'INVALID_REQUEST',
  TIMEOUT: 'TIMEOUT',
  CANCELLED: 'CANCELLED',
  STALE_SESSION: 'CLOSED',
  RESOURCE_LIMIT: 'LIMIT',
  NATIVE_ARTIFACT_INVALID: 'CAPABILITY_UNAVAILABLE',
  NATIVE_ABI_MISMATCH: 'CAPABILITY_UNAVAILABLE',
};

/** Pure computation only. Rule configuration and chapter publication stay native-owned. */
export function createSourceRules() {
  const program = new NativeJsonProgram(novelRuleSource), jobs = new Set();
  let closed = false, closing;
  function open(signal) {
    if (closed) throw fault('CLOSED');
    if (signal?.aborted) throw fault('CANCELLED');
  }
  async function invoke(request, signal, timeoutMs = 3_000) {
    open(signal);
    try {
      const result = await program.invoke(request, { signal, timeoutMs });
      open(signal);
      return result;
    } catch (error) {
      if (closed) throw fault('CLOSED');
      if (signal?.aborted) throw fault('CANCELLED');
      throw fault(nativeCodes[error?.code] ?? 'SOURCE_FAILURE');
    }
  }
  async function process(request, signal) {
    if (request.operation !== 'transform') {
      return invoke(request, signal, request.operation === 'matchUrl' ? 2_000 : 3_000);
    }
    let current = { title: request.title, blocks: request.blocks };
    if (!Array.isArray(request.rules)) throw fault('INVALID_REQUEST');
    const ordered = request.rules.map((rule, index) => {
      if (!object(rule)) throw fault('INVALID_REQUEST');
      return { rule, index };
    }).filter(({ rule }) => rule.isEnabled !== false).sort((left, right) =>
      (left.rule.order ?? 0) - (right.rule.order ?? 0) || left.index - right.index);
    for (const { rule } of ordered) {
      // Only an executing rule consumes its budget; the fixed program decides
      // scope and reports invalid budgets there. The host timer needs a number.
      const budget = rule.timeoutMillisecond ?? 3_000;
      const timeoutMs = Number.isSafeInteger(budget) && budget > 0 ? budget : 3_000;
      current = await invoke({ ...request, ...current, rules: [rule], conversion: 'none' },
        signal, timeoutMs);
    }
    return invoke({ ...request, ...current, rules: [], conversion: request.conversion ?? 'none' }, signal);
  }
  return {
    async process(request, { signal } = {}) {
      open(signal);
      if (!object(request) || typeof request.operation !== 'string' ||
          !Object.hasOwn(requests, request.operation)) throw fault('INVALID_REQUEST');
      const captured = { operation: request.operation, ...Object.fromEntries(requests[request.operation]
        .filter(key => Object.hasOwn(request, key)).map(key => [key, structuredClone(request[key])])) };
      const work = process(captured, signal);
      jobs.add(work);
      try { return await work; }
      finally { jobs.delete(work); }
    },
    close() {
      if (!closing) {
        closed = true;
        closing = (async () => {
          await program.close();
          await Promise.allSettled([...jobs]);
        })();
      }
      return closing;
    },
  };
}
