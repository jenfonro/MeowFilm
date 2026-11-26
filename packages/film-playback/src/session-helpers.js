import { isDeepStrictEqual } from 'node:util';
import { checkTarget, fields, identity, requireValue, variantParentId } from './identity.js';

export const addressLifetime = 3 * 60_000;

export const publicSource = source => fields(source, ['key', 'engine', 'installationId', 'siteId', 'revision',
  'generation', 'name', 'detailId']);

export const publicCandidate = (candidate, target, catalog) => ({
  ...fields(candidate, ['id', 'lineId', 'lineLabel', 'provider',
    'name', 'path', 'season', 'episode', 'globalEpisode', 'quality', 'resolution', 'fps', 'codec']),
  source: publicSource(candidate.source),
  ...catalog.candidate(target, candidate),
});

export const publicTarget = target => ({
  ...fields(target, ['id', 'kind', 'title', 'year', 'metadataProvider',
    'metadataId', 'season', 'episode', 'globalEpisode', 'members']),
  ...(target.source ? {
    source: fields(target.source, ['key', 'engine', 'installationId', 'siteId', 'detailId', 'lineId', 'candidateId']),
  } : {}),
});

export const sameEpisode = (a, b) => {
  if (a?.id !== b?.id || a?.kind !== b?.kind) return false;
  if (a?.source?.candidateId !== undefined || b?.source?.candidateId !== undefined)
    return a?.source?.candidateId === b?.source?.candidateId;
  if (a?.source?.key !== b?.source?.key || a?.source?.detailId !== b?.source?.detailId) return false;
  if (a?.globalEpisode !== undefined && b?.globalEpisode !== undefined) return a.globalEpisode === b.globalEpisode;
  if (a?.episode !== undefined || b?.episode !== undefined) return a?.season === b?.season && a?.episode === b?.episode;
  return a?.source?.lineId === b?.source?.lineId;
};

export const sameActualEpisode = (a, b) => a?.id === b?.id && a?.kind === b?.kind &&
  ['source', 'group'].includes(a?.kind) && (a.kind === 'group' || a.source?.key === b.source?.key &&
    a.source?.detailId === b.source?.detailId) &&
  (a.episode !== undefined ? a.season === b.season && a.episode === b.episode &&
    (a.globalEpisode === undefined || a.globalEpisode === b.globalEpisode)
    : a.globalEpisode !== undefined && a.globalEpisode === b.globalEpisode ||
      !!variantParentId(a.source?.candidateId) &&
        variantParentId(a.source.candidateId) === variantParentId(b.source?.candidateId));

export const canDiscoverVariants = candidate => candidate.source.engine === 'cat' &&
  !candidate.file && !candidate.share && !candidate.variant;

export const sameVariantFile = (a, b) => !!a?.variant && !!b?.variant && a.parentId === b.parentId &&
  a.fileIdentity === b.fileIdentity && a.source.key === b.source.key && a.detailId === b.detailId &&
  a.lineId === b.lineId && a.playId === b.playId;

export const cancelled = error =>
  ['CLOSED', 'CANCELLED', 'STALE_ACCOUNT', 'STALE_SOURCE', 'STALE_SESSION', 'STALE_METADATA'].includes(error?.code);

export const historyPosition = row => row.position;

export function episodeTarget(target, episode) {
  const next = { ...target };
  for (const name of ['season', 'episode', 'globalEpisode']) {
    if (episode[name] === undefined) delete next[name];
    else next[name] = episode[name];
  }
  return checkTarget(next);
}

export function targetForCandidate(target, candidate) {
  const source = { key: candidate.source.key, detailId: candidate.source.detailId,
    lineId: candidate.lineId, candidateId: candidate.id };
  return episodeTarget({
    ...(['source', 'group'].includes(target.kind) ? target
      : { ...fields(target, ['id', 'title', 'year', 'members']), kind: 'source' }),
    source,
  }, candidate);
}

export const manualIdentity = row => identity(fields(row, ['targetId', 'kind', 'sourceKey', 'detailId',
  'provider', 'share', 'seasonHint']));

export const metadataIdentity = row => identity(row.provider, row.kind, String(row.metadataId));

export function mergeSeason(target, metadata, season, value) {
  const key = target.metadataProvider === 'douban' ? 'doubanSeasons' : 'seasons';
  const rows = [...(metadata?.[key] ?? [])];
  const index = rows.findIndex(row => (row.season_number ?? row.season) === season);
  const row = { ...(index < 0 ? {} : rows[index]), ...value,
    [key === 'seasons' ? 'season_number' : 'season']: season };
  if (key === 'doubanSeasons' && !Object.hasOwn(value, 'episodes')) delete row.episodes;
  if (index >= 0 && isDeepStrictEqual(rows[index], row)) return metadata;
  if (index < 0) rows.push(row);
  else rows[index] = row;
  rows.sort((a, b) => (a.season_number ?? a.season) - (b.season_number ?? b.season));
  return { ...metadata, [key]: rows };
}

export function replaceLine(result, detail, index, line) {
  if (result.detail !== detail) return result.detail;
  if (detail.lines[index] === line || isDeepStrictEqual(detail.lines[index], line)) return detail;
  const lines = [...detail.lines];
  lines[index] = line;
  return result.detail = { ...detail, lines };
}

export function actualSourceEpisodes(session) {
  const candidate = session.current?.candidate ?? session.pending?.candidate;
  const result = session.results.find(value => value.source.key === candidate?.source.key &&
    value.source.detailId === candidate?.source.detailId);
  const line = result?.detail?.lines.find(value => value.id === candidate.lineId);
  return line?.episodes ?? [];
}
