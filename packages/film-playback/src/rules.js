import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { NativeJsonProgram } from '@meowfilm/script-host/native-json';

const require = createRequire(import.meta.url);
export const filmRuleSource = [require.resolve('@meowfilm/film-rules'),
  require.resolve('@meowfilm/film-rules/playback')].map(path => readFileSync(path, 'utf8')).join('\n');
// Public semantic identity; no dependency on private build receipts at runtime.
export const filmRuleKernelRevision = createHash('sha256')
  .update('quickjs-ng@0.18.0:d7215b0ad3fc2c562e8317fd9bca058fd4863804;')
  .update('unicode:5840a921ac11664b505ee37b1219f518fff2a04972fe848b4bdf1e85c5a0bd83;')
  .update(filmRuleSource).digest('hex');
const executors = new WeakMap();
const groups = ['episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules'];
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

export function validateRules(value, { partial = false } = {}) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw fail('INVALID_RULES');
  for (const group of groups) {
    if (partial && !Object.hasOwn(value, group)) continue;
    const rows = value[group], matching = group === 'episodeRules' || group === 'movieRules';
    if (!Array.isArray(rows) || rows.some(row => matching
      ? !row || typeof row !== 'object' || !validRuleText(row.pattern) ||
        row.replace !== undefined && !validRuleText(row.replace) ||
        row.flags !== undefined && !validRuleText(row.flags)
      : !validRuleText(row))) throw fail('INVALID_RULES');
  }
  return value;
}

// The capability stays inside Node. JSON snapshots contain only rules/revision.
export function bindRuleSnapshot(snapshot, invoke) {
  executors.set(snapshot, invoke);
  return snapshot;
}

export async function invokeFilmRules(rules, request, { signal } = {}) {
  const invoke = rules && executors.get(rules);
  if (invoke) return invoke({ ...request, rules: rules.rules }, { signal });
  const draft = rules?.rules ?? rules;
  if (draft !== undefined) validateRules(draft, { partial: true });
  const program = new NativeJsonProgram(filmRuleSource);
  try { return await program.invoke({ ...request, rules: draft }, { signal }); }
  catch (error) { throw filmRuleFailure(error); }
  finally { await program.close(); }
}

export async function invokeRecognition(rules, request, options) {
  const response = await invokeFilmRules(rules, request, options);
  if (response.error) throw fail(response.error);
  return response.value;
}

/** One call per page/metadata batch. Saved snapshots reuse their owner's program. */
export function createRecognitionRules(rules) {
  const draft = rules?.rules ?? rules;
  if (draft !== undefined) validateRules(draft, { partial: true });
  const batch = (requests, options) => invokeRecognition(rules,
    { operation: 'recognition.batch', requests }, options);
  const one = async (request, options) => (await batch([request], options))[0];
  return {
    batch,
    episode: (text, options) => one({ kind: 'episode', text }, options),
    season: async (text, options) => (await one({ kind: 'season', text }, options)) ?? undefined,
    movie: (text, options) => one({ kind: 'movie', text }, options),
    search: (text, query = text, options) => one({ kind: 'search', text, query }, options),
    metadata: (record, contentKind, options) => one({ kind: 'metadata', record, contentKind }, options),
    quality: (text, options) => one({ kind: 'quality', text }, options),
  };
}

export function providerFamily(provider) {
  switch (provider) {
    case 'quark_tv': return 'quark';
    case 'uc_tv': return 'uc';
    case 'yidong':
    case 'tianyi':
    case 'quark':
    case 'uc':
    case 'baidu': return provider;
    default: return undefined;
  }
}

// Fixed grade aliases for policy/UI constraints. Filename/path recognition and
// regexp-based quality inference execute in playback-rules.js on QuickJS.
const qualityAliases = new Map([
  ...['4k', '2160p', 'uhd'].map(value => [value, '4k']),
  ...['1080p', '1080i', 'fhd'].map(value => [value, '1080p']),
  ...['720p', 'hd'].map(value => [value, '720p']),
]);
for (const separator of ['', '·', ' ', '_', '-']) {
  qualityAliases.set(`4k${separator}hdr`, '4k_hdr');
  for (const suffix of ['fps', '60', '60fps', '60帧']) qualityAliases.set(`4k${separator}${suffix}`, '4k_fps');
}
export function qualityKey(value) {
  if (typeof value !== 'string') return '';
  // Only ASCII aliases are accepted; this does not consult a Unicode case table.
  let key = '';
  for (const character of value.trim()) {
    const unit = character.charCodeAt(0);
    key += unit >= 65 && unit <= 90 ? String.fromCharCode(unit + 32) : character;
  }
  return qualityAliases.get(key) ?? '';
}
