import { createHash } from 'node:crypto';
import { cloneJSONValue, parseJSONText, stringifyJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';

export const fail = code => Object.assign(new Error(code), { code });
export const requireValue = (value, code = 'INVALID_REQUEST') => { if (!value) throw fail(code); };
export const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
export const text = value => typeof value === 'string' ? value.trim() : '';
export const isIdentifier = value => !!text(value) || Number.isSafeInteger(value);
export const clone = value => value === undefined ? undefined : cloneJSONValue(value);
// Only the shared site's explicit projection has an opaque UI id. A provider's
// unknown raw field is still part of its original row, not a projection marker.
export const isSearchProjection = value => value?.vod_id == null &&
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

// Windows and Apple public native failure codes, never arbitrary text or URLs.
export const nativeMediaErrorCodes = new Set([
  'MEDIA_INVALID_REQUEST', 'MEDIA_UNSUPPORTED_URI', 'MEDIA_UNSUPPORTED_HEADERS', 'MEDIA_INVALID_HEADERS',
  'MEDIA_UNSUPPORTED_ADAPTIVE_HEADERS', 'MEDIA_ADAPTIVE_INVALID', 'MEDIA_ADAPTIVE_VARIANT_UNSUPPORTED',
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
  'MEDIA_COMMAND_FAILED', 'MEDIA_CALLBACK_FAILED', 'MEDIA_RELEASE_FAILED', 'MEDIA_DISPATCHER_UNAVAILABLE',
  'MEDIA_WRONG_THREAD', 'MEDIA_DISPOSED', 'MEDIA_SUPERSEDED',
  'MEDIA_INPUT_INVALID', 'MEDIA_FORMAT_UNSUPPORTED', 'MEDIA_DECODING_FAILED',
  'MEDIA_OUTPUT_FAILED', 'MEDIA_SEEK_FAILED', 'MEDIA_DECRYPTION_FAILED',
  'FILM_FORMAT_UNSUPPORTED', 'FILM_ENCRYPTION_UNSUPPORTED', 'FILM_DECODE_FAILED',
  'FILM_AUDIO_FAILED', 'FILM_RENDER_FAILED', 'FILM_HDR_DISPLAY_UNSUPPORTED',
  'FILM_DOVI_UNSUPPORTED', 'FILM_DEVICE_LOST',
]);
// Existing film/bridge receipts can fail before native preparation begins.
export const playbackFeedbackErrorCodes = new Set([
  'INVALID_REQUEST', 'CLOSED', 'CANCELLED', 'RESOURCE_LIMIT', 'NOT_FOUND', 'NO_MATCH',
  'SOURCE_NOT_FOUND', 'SOURCE_DISABLED', 'STALE_SOURCE', 'STALE_SESSION', 'STALE_ACCOUNT', 'STALE_METADATA',
  'NOT_CONFIGURED', 'AUTH_REQUIRED', 'AUTH_FAILED', 'CHALLENGE_REQUIRED', 'UNSUPPORTED_OPERATION',
  'CAPABILITY_UNAVAILABLE', 'MEDIA_UNAVAILABLE', 'NETWORK_ERROR', 'TIMEOUT', 'UPSTREAM_HTTP',
  'UPSTREAM_FORMAT', 'UPSTREAM_ERROR', 'WEB_INTERACTION_REQUIRED', 'STORAGE_ERROR', 'CORRUPT_STORAGE',
  'COMMIT_UNKNOWN', 'NO_EPISODE_MAPPING', 'INVALID_PREFERENCES', 'INVALID_RULES', 'INVALID_DETAIL',
  'INVALID_RESULT', 'CACHE_LOAD_FAILED', 'STORAGE_CHANGED', 'LIBRARY_UPDATE_FAILED', 'PAN_FAILURE',
  'SOURCE_FAILURE', 'CAT_FAILURE', 'SCRIPT_FAILURE', 'SCRIPT_PROTOCOL_ERROR', 'PAN_INTERCEPTION_UNSUPPORTED', 'LOGIN_EXPIRED',
  'MEDIA_BUSY', 'MEDIA_CANCELLED', 'MEDIA_CLOSED', 'MEDIA_CLOSE_FAILED', 'MEDIA_DRM_UNSUPPORTED',
  'MEDIA_EMPTY_RESPONSE', 'MEDIA_INVALID_DESCRIPTOR', 'MEDIA_LISTEN_FAILED', 'MEDIA_MANIFEST_INVALID',
  'MEDIA_MANIFEST_LIMIT', 'MEDIA_MANIFEST_UNSUPPORTED', 'MEDIA_METHOD_NOT_ALLOWED', 'MEDIA_NOT_FOUND',
  'MEDIA_PREFETCH_DISABLED', 'MEDIA_PREFETCH_HTTP_ERROR', 'MEDIA_PREFETCH_LIMIT', 'MEDIA_PREFETCH_TIMEOUT',
  'MEDIA_PREFETCH_UNSUPPORTED', 'MEDIA_RANGE_UNSUPPORTED', 'MEDIA_REDIRECT_ERROR', 'MEDIA_RESOURCE_CHANGED',
  'MEDIA_RESOURCE_LIMIT', 'MEDIA_UPSTREAM_ERROR', 'MEDIA_UPSTREAM_TIMEOUT',
  'PERMISSION_DENIED', 'METHOD_NOT_FOUND', 'INVALID_RESPONSE', 'LIMIT', 'INVALID_MESSAGE',
  'METHOD_UNAVAILABLE', 'INTERNAL', 'SOURCE_INTEGRITY', 'SOURCE_ENCODING', 'UNSUPPORTED_PROTOCOL',
  'UNSUPPORTED_ACTION', 'UNSUPPORTED_RULE', 'UNSUPPORTED_DEPENDENCY', 'SOURCE_CLEANUP', 'HTTP_ERROR',
  'VIRTUAL_ROUTE_NOT_FOUND',
]);
export function playbackFeedbackFailure(code, stage) {
  requireValue(['prepare', 'playback'].includes(stage));
  requireValue(code === undefined || code === 'FAILED' ||
    nativeMediaErrorCodes.has(code) || playbackFeedbackErrorCodes.has(code));
  return code === undefined ? null : { code: code === 'FAILED' ? 'UPSTREAM_ERROR' : code,
    stage: nativeMediaErrorCodes.has(code) ? stage : 'receipt' };
}

export function checkSource(source) {
  requireValue(object(source) && ['cat', 'source'].includes(source.engine ?? 'cat') &&
    text(source.installationId) && text(source.siteId) &&
    Number.isSafeInteger(source.generation) && source.generation > 0);
  if (source.engine === 'source') requireValue(Number.isSafeInteger(source.revision) && source.revision > 0);
  return { ...clone(source), engine: source.engine ?? 'cat', key: source.key || sourceIdentity(source) };
}

export function checkTarget(target) {
  requireValue(object(target) && text(target.id) && text(target.title) &&
    ['movie', 'tv', 'source', 'group'].includes(target.kind));
  for (const name of ['season', 'episode', 'globalEpisode']) {
    if (target[name] !== undefined) requireValue(Number.isSafeInteger(target[name]) &&
      target[name] >= (name === 'globalEpisode' ? 1 : 0));
  }
  if (target.metadataProvider !== undefined) requireValue(['tmdb', 'douban'].includes(target.metadataProvider) &&
    /^[0-9]+$/.test(String(target.metadataId)));
  if (target.kind === 'source') requireValue(object(target.source) && text(target.source.key) &&
    isIdentifier(target.source.detailId));
  if (target.kind === 'group') {
    requireValue(Array.isArray(target.members) &&
      target.members.every(member => object(member) && text(member.sourceKey) && isIdentifier(member.detailId)) &&
      target.metadataProvider === undefined);
    if (target.source !== undefined) requireValue(object(target.source) && text(target.source.key) &&
      isIdentifier(target.source.detailId));
  }
  const result = clone(target);
  if (result.kind === 'group') result.members = result.members.map(({ sourceKey, detailId }) => ({ sourceKey, detailId }));
  return result;
}

export function parseSourceValue(value) {
  if (typeof value === 'string') {
    try { value = parseJSONText(value); } catch { throw fail('UPSTREAM_FORMAT'); }
  }
  requireValue(object(value), 'UPSTREAM_FORMAT');
  return value;
}
