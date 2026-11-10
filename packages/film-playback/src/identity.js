import { createHash } from 'node:crypto';

export const fail = code => Object.assign(new Error(code), { code });
export const requireValue = (value, code = 'INVALID_REQUEST') => { if (!value) throw fail(code); };
export const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
export const text = value => typeof value === 'string' ? value.trim() : '';
export const isIdentifier = value => !!text(value) || Number.isSafeInteger(value);
export const clone = value => structuredClone(value);
// Only the shared site's explicit projection has an opaque UI id. A provider's
// unknown raw field is still part of its original row, not a projection marker.
export const isSearchProjection = value => value?.vod_id == null &&
  value?.sourceKind === 'site' && !!text(value.siteKey) && object(value.raw);
export const resultDetailId = value => value?.vod_id ??
  (isSearchProjection(value) ? value.detailId : value?.id ?? value?.detailId);

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (object(value)) return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  return value;
}
export const identity = (...parts) => createHash('sha256').update(JSON.stringify(canonical(parts))).digest('hex');
export const sourceIdentity = source => identity(source.engine ?? 'cat', source.installationId, source.siteId);
export const sourceRevision = source => identity(sourceIdentity(source), source.revision, source.generation);
export const codeOf = error => typeof error?.code === 'string' && /^[A-Z][A-Z_]+$/.test(error.code)
  ? error.code : 'UPSTREAM_ERROR';

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
    requireValue(Array.isArray(target.members) && target.members.length > 0 &&
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
    try { value = JSON.parse(value); } catch { throw fail('UPSTREAM_FORMAT'); }
  }
  requireValue(object(value), 'UPSTREAM_FORMAT');
  return value;
}
