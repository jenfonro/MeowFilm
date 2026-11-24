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

async function invokeFilmRules(rules, request, { signal } = {}) {
  const invoke = rules && executors.get(rules);
  if (!invoke) throw fail('INVALID_RULES');
  return invoke({ ...request, rules: rules.rules }, { signal });
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
    metadata: (record, contentKind, options) => one({ kind: 'metadata', record, contentKind }, options),
  };
}

export function providerFamily(provider) {
  switch (provider) {
    case 'yidong':
    case 'tianyi':
    case 'quark':
    case 'uc':
    case 'baidu': return provider;
    default: return undefined;
  }
}
