import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { qualityKey } from '@meowfilm/film-rules/quality';
import { nativeKernelIdentity } from '@meowfilm/script-host/native-json';

export { qualityKey };

const require = createRequire(import.meta.url);
export const filmRuleSource = [
  `globalThis.__meowfilmQualityKey = ${qualityKey.toString()};`,
  readFileSync(require.resolve('@meowfilm/film-rules'), 'utf8'),
  readFileSync(require.resolve('@meowfilm/film-rules/playback'), 'utf8'),
].join('\n');
let cachedKernelRevision;
export function filmRuleKernelRevision() {
  return cachedKernelRevision ??= createHash('sha256')
    .update(nativeKernelIdentity()).update(';')
    .update(filmRuleSource).digest('hex');
}
const executors = new WeakMap();
const fail = code => Object.assign(new Error(code), { code });
const nativeErrors = {
  TIMEOUT: 'TIMEOUT', CANCELLED: 'CANCELLED', RESOURCE_LIMIT: 'RESOURCE_LIMIT',
  INVALID_ARGUMENT: 'INVALID_REQUEST', SCRIPT_EXCEPTION: 'SCRIPT_FAILURE',
  STALE_SESSION: 'CLOSED', NATIVE_ARTIFACT_INVALID: 'CAPABILITY_UNAVAILABLE',
  NATIVE_ABI_MISMATCH: 'CAPABILITY_UNAVAILABLE',
};
export function filmRuleFailure(error) {
  return fail(nativeErrors[error?.code] ?? 'SCRIPT_FAILURE');
}

export function validRuleText(value) {
  if (typeof value !== 'string') return false;
  for (let index = 0; index < value.length; index++) {
    const unit = value.charCodeAt(index);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
  }
  return true;
}

// The capability stays inside Node. JSON snapshots contain only rules/revision.
export function bindRuleSnapshot(snapshot, invoke) {
  executors.set(snapshot, invoke);
  return snapshot;
}

function sanitizeJsonInput(value) {
  if (Array.isArray(value)) return value.map(item => item === undefined ? null : sanitizeJsonInput(item));
  if (value !== null && typeof value === 'object') {
    const proto = Object.getPrototypeOf(value);
    if (proto === Object.prototype || proto === null) {
      return Object.fromEntries(
        Object.entries(value)
          .filter(([, item]) => item !== undefined)
          .map(([key, item]) => [key, sanitizeJsonInput(item)]),
      );
    }
  }
  return value;
}

async function invokeFilmRules(rules, request, { signal } = {}) {
  const invoke = rules && executors.get(rules);
  if (!invoke) throw fail('INVALID_RULES');
  return invoke({ ...sanitizeJsonInput(request), rules: rules.rules }, { signal });
}

export async function invokeRecognition(rules, request, options) {
  const response = await invokeFilmRules(rules, request, options);
  if (response.error) throw fail(response.error);
  return response.value;
}

/** One call per page/metadata batch. Saved snapshots reuse their owner's program. */
export function createRecognitionRules(rules) {
  const batch = (requests, options) => invokeRecognition(rules,
    { operation: 'recognition.batch', requests }, options);
  const one = async (request, options) => (await batch([request], options))[0];
  return {
    batch,
    metadata: (record, contentKind, options) => one({
      kind: 'metadata',
      record,
      ...(contentKind === undefined ? {} : { contentKind }),
    }, options),
    canonical: (title, contentKind, query, options) =>
      invokeRecognition(rules, {
        operation: 'recognition.canonical',
        title,
        ...(contentKind === undefined ? {} : { contentKind }),
        ...(query === undefined ? {} : { query }),
      }, options),
    season: (record, options) => one({ kind: 'season', record }, options),
  };
}

export function providerFamily(provider) {
  if (typeof provider !== 'string') return undefined;
  switch (provider.trim().toLowerCase()) {
    case '139':
    case 'yidong': return 'yidong';
    case '189':
    case 'tianyi': return 'tianyi';
    case 'quark': return 'quark';
    case 'uc': return 'uc';
    case 'baidu': return 'baidu';
    default: return undefined;
  }
}
