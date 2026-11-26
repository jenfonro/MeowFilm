import { createHash } from 'node:crypto';
import { cloneJSONValue, parseJSONText, stringifyJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';
import { normalizeNFKC } from '@meowfilm/script-host/unicode';

export const fail = code => Object.assign(new Error(code), { code });
export const requireValue = (value, code = 'INVALID_REQUEST') => { if (!value) throw fail(code); };
export const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
export const text = value => typeof value === 'string' ? value.trim() : '';
export const isIdentifier = value => !!text(value) || Number.isSafeInteger(value);
export const clone = value => value === undefined ? undefined : cloneJSONValue(value);
export function fields(value, keys) {
  requireValue(object(value));
  return Object.fromEntries(keys.filter(key => value[key] !== undefined).map(key => [key, clone(value[key])]));
}
export function freeze(value) {
  if (value && typeof value === 'object') {
    for (const item of Object.values(value)) freeze(item);
    Object.freeze(value);
  }
  return value;
}

const isSearchProjection = value => value?.vod_id == null &&
  value?.sourceKind === 'site' && !!text(value.siteKey) && object(value.raw);
export const resultDetailId = value => value?.vod_id ??
  (isSearchProjection(value) ? value.detailId : value?.id ?? value?.detailId);

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (object(value) && !isRawJSON(value) &&
      [Object.prototype, null].includes(Object.getPrototypeOf(value)))
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  return value;
}
export const identity = (...parts) => createHash('sha256')
  .update(stringifyJSONValue(canonical(parts.map(part => part === undefined ? null : part)))).digest('hex');
export const variantParentId = id => typeof id === 'string' && /^[a-f0-9]{64}:[a-f0-9]{64}$/.test(id)
  ? id.slice(0, 64) : null;
export const sourceIdentity = source => identity(source.engine ?? 'cat', source.installationId, source.siteId);
export const sourceRevision = source => identity(sourceIdentity(source), source.revision, source.generation);
export const codeOf = error => typeof error?.code === 'string' && /^[A-Z][A-Z_]+$/.test(error.code)
  ? error.code : 'UPSTREAM_ERROR';

// Unicode 15.0 Cf + punctuation/whitespace, without \p{...} for ICU-less NodeMobile.
const workKeyStrip = /[\u00ad\u0600-\u0605\u061c\u06dd\u070f\u0890-\u0891\u08e2\u180e\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u206f\ufeff\ufff9-\ufffb\u{110bd}\u{110cd}\u{13430}-\u{1343f}\u{1bca0}-\u{1bca3}\u{1d173}-\u{1d17a}\u{e0001}\u{e0020}-\u{e007f}\s.\-_,，:：;；!！?？·•/\\|《》「」『』【】()（）\[\]{}]+/gu;

export function normalizeWorkKey(value) {
  const normalized = normalizeNFKC(text(value)).toLowerCase().replace(workKeyStrip, '');
  return normalized || normalizeNFKC(text(value)).toLowerCase();
}

export function workTargetId(keyword) {
  const key = normalizeWorkKey(keyword);
  requireValue(key.length > 0);
  return `work:${key}`;
}

export const nativeMediaErrorCodes = new Set([
  'MEDIA_INVALID_REQUEST', 'MEDIA_UNSUPPORTED_URI', 'MEDIA_UNSUPPORTED_HEADERS', 'MEDIA_INVALID_HEADERS',
  'MEDIA_UNSUPPORTED_ADAPTIVE', 'MEDIA_ADAPTIVE_INVALID', 'MEDIA_ADAPTIVE_VARIANT_UNSUPPORTED',
  'MEDIA_ADAPTIVE_DRM_UNSUPPORTED', 'MEDIA_ADAPTIVE_ENCRYPTION_UNSUPPORTED', 'MEDIA_ADAPTIVE_BUDGET',
  'MEDIA_HTTP_REDIRECT_FAILED', 'MEDIA_HTTP_INVALID_RANGE', 'MEDIA_HTTP_RANGE_REJECTED',
  'MEDIA_HTTP_REPRESENTATION_CHANGED', 'MEDIA_HTTP_ENCODING_UNSUPPORTED', 'MEDIA_HTTP_FORMAT_UNSUPPORTED',
  'MEDIA_HTTP_DEADLINE', 'MEDIA_HTTP_UNAUTHORIZED', 'MEDIA_HTTP_NOT_FOUND', 'MEDIA_HTTP_EXPIRED',
  'MEDIA_HTTP_THROTTLED', 'MEDIA_HTTP_SERVER_FAILED', 'MEDIA_HTTP_STATUS_FAILED', 'MEDIA_HTTP_TLS_FAILED',
  'MEDIA_HTTP_NETWORK_FAILED', 'MEDIA_HTTP_TRUNCATED', 'MEDIA_INVALID_READ_LEASE', 'MEDIA_INVALID_RESOURCE_LEASE',
  'MEDIA_RESOURCE_REVOKED', 'MEDIA_READ_FORMAT_UNSUPPORTED', 'MEDIA_READ_REVOKED', 'MEDIA_READ_FAILED',
  'MEDIA_READ_TRUNCATED', 'MEDIA_READ_CHANGED', 'MEDIA_READ_SEEK_FAILED', 'MEDIA_INVALID_SEEK',
  'MEDIA_NOT_SEEKABLE', 'MEDIA_NOT_PAUSABLE', 'MEDIA_INVALID_TRACK', 'MEDIA_TRACK_METADATA_INVALID',
  'MEDIA_TRACK_SELECTION_FAILED', 'MEDIA_INVALID_RATE', 'MEDIA_RATE_UNSUPPORTED', 'MEDIA_INVALID_SUBTITLE_DELAY',
  'MEDIA_SUBTITLE_UNAVAILABLE', 'MEDIA_NATIVE_UNAVAILABLE', 'MEDIA_OPEN_FAILED', 'MEDIA_PLAYBACK_FAILED',
  'MEDIA_COMMAND_FAILED', 'MEDIA_RELEASE_FAILED', 'MEDIA_DISPATCHER_UNAVAILABLE',
  'MEDIA_WRONG_THREAD', 'MEDIA_DISPOSED', 'MEDIA_SUPERSEDED',
  'MEDIA_INPUT_INVALID', 'MEDIA_FORMAT_UNSUPPORTED', 'MEDIA_DECODING_FAILED',
  'MEDIA_OUTPUT_FAILED', 'MEDIA_SEEK_FAILED', 'MEDIA_DECRYPTION_FAILED',
  'FILM_FORMAT_UNSUPPORTED', 'FILM_ENCRYPTION_UNSUPPORTED', 'FILM_DECODE_FAILED',
  'FILM_AUDIO_FAILED', 'FILM_RENDER_FAILED', 'FILM_HDR_DISPLAY_UNSUPPORTED',
  'FILM_DOVI_UNSUPPORTED', 'FILM_DEVICE_LOST',
]);

export function playbackFeedbackFailure(code, stage) {
  requireValue(['prepare', 'playback'].includes(stage));
  requireValue(code === undefined || code === 'FAILED' ||
    typeof code === 'string' && /^[A-Z][A-Z0-9_]{0,63}$/.test(code));
  return code === undefined ? null : { code: code === 'FAILED' ? 'UPSTREAM_ERROR' : code,
    stage: nativeMediaErrorCodes.has(code) ? stage : 'receipt' };
}

export function checkSource(source) {
  requireValue(object(source) && ['cat', 'source'].includes(source.engine ?? 'cat') &&
    text(source.installationId) && text(source.siteId) &&
    Number.isSafeInteger(source.generation) && source.generation > 0);
  if (source.engine === 'source') requireValue(Number.isSafeInteger(source.revision) && source.revision > 0);
  requireValue(source.key === undefined || text(source.key));
  return { ...clone(source), engine: source.engine ?? 'cat',
    key: source.key === undefined ? sourceIdentity(source) : text(source.key) };
}

export function parseSourceValue(value) {
  if (typeof value === 'string') {
    try { value = parseJSONText(value); } catch { throw fail('UPSTREAM_FORMAT'); }
  }
  requireValue(object(value), 'UPSTREAM_FORMAT');
  return value;
}
