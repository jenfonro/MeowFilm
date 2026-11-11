import { novelRuleSource } from '@meowfilm/novel-rules';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';

const fault = code => Object.assign(new Error(code), { code });
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const requests = {
  identity: { fields: ['book'], required: ['book'], valid: input => object(input.book) },
  merge: { fields: ['books'], required: ['books'],
    valid: input => Array.isArray(input.books) && input.books.every(object) },
  matchChapter: { fields: ['chapters', 'anchor'], required: ['chapters', 'anchor'],
    valid: input => Array.isArray(input.chapters) && input.chapters.every(object) && object(input.anchor) },
  validateRule: { fields: ['rule'], required: ['rule'], valid: () => true },
  transform: {
    fields: ['title', 'blocks', 'book', 'source', 'rules', 'conversion'],
    required: ['title', 'blocks', 'rules'],
    valid: input => typeof input.title === 'string' &&
      Array.isArray(input.blocks) && input.blocks.every(block => object(block) &&
        (block.type === 'text' && typeof block.text === 'string' ||
          block.type === 'image' && typeof block.src === 'string')) &&
      Array.isArray(input.rules) && input.rules.every(object) &&
      (input.book === undefined || object(input.book)) &&
      (input.source === undefined || object(input.source)) &&
      (input.conversion === undefined || ['none', 'simplified', 'traditional'].includes(input.conversion)),
  },
  matchUrl: { fields: ['pattern', 'url'], required: ['pattern', 'url'],
    valid: input => typeof input.pattern === 'string' && typeof input.url === 'string' },
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

function validate(request) {
  const shape = object(request) && typeof request.operation === 'string' &&
    Object.hasOwn(requests, request.operation)
    ? requests[request.operation] : null;
  if (!shape || Object.keys(request).some(key => key !== 'operation' && !shape.fields.includes(key)) ||
      !shape.required.every(key => Object.hasOwn(request, key)) || !shape.valid(request)) {
    throw fault('INVALID_REQUEST');
  }
}

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
    const ordered = request.rules.map((rule, index) => ({ rule, index })).sort((left, right) =>
      (left.rule.order ?? 0) - (right.rule.order ?? 0) || left.index - right.index);
    for (const { rule } of ordered) {
      if (rule.isEnabled === false) continue;
      const timeoutMs = rule.timeoutMillisecond ?? 3_000;
      if (!Number.isSafeInteger(timeoutMs) || timeoutMs <= 0) throw fault('INVALID_REQUEST');
      current = await invoke({ ...request, ...current, rules: [rule], conversion: 'none' },
        signal, timeoutMs);
    }
    return invoke({ ...request, ...current, rules: [], conversion: request.conversion ?? 'none' }, signal);
  }
  return {
    async process(request, { signal } = {}) {
      open(signal);
      validate(request);
      const work = process(structuredClone(request), signal);
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
