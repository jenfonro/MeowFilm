import { isDeepStrictEqual } from 'node:util';
import { fields, identity, requireValue, variantParentId } from './identity.js';

export const addressLifetime = 3 * 60_000;
export const IDLE_SESSION_TTL_MS = 15 * 60_000;
export const MAX_IDLE_SESSIONS = 24;

export const canDiscoverVariants = candidate => candidate?.source?.engine === 'cat' &&
  !candidate.file && !candidate.share && !candidate.variant;

export const sameVariantFile = (a, b) => !!a?.variant && !!b?.variant && a.parentId === b.parentId &&
  a.fileIdentity === b.fileIdentity && a.source.key === b.source.key && a.detailId === b.detailId &&
  a.lineId === b.lineId && a.playId === b.playId;

export const cancelled = error =>
  ['CLOSED', 'CANCELLED', 'STALE_ACCOUNT', 'STALE_SOURCE', 'STALE_SESSION', 'STALE_METADATA'].includes(error?.code);

export const historyPosition = row => (!row || row.ended) ? 0 : row.position;

export const manualIdentity = row => identity(fields(row, ['targetId', 'kind', 'sourceKey', 'detailId',
  'provider', 'share', 'seasonHint']));

export const metadataIdentity = row => `meta:${row.provider}:${row.kind}:${String(row.metadataId)}`;

export function samePlayIntent(a, b) {
  if (!a || !b || a.mode !== b.mode || a.kind !== b.kind) return false;
  if (a.mode === 'direct') return a.candidateId === b.candidateId;
  if (a.kind === 'movie') return true;
  if (a.globalEpisode !== undefined && b.globalEpisode !== undefined) return a.globalEpisode === b.globalEpisode;
  return a.season === b.season && a.episode === b.episode;
}

export function mergeSeason(provider, metadata, season, value) {
  const key = provider === 'douban' ? 'doubanSeasons' : 'seasons';
  const rows = [...(metadata?.[key] ?? [])];
  const index = rows.findIndex(row => (row.season_number ?? row.season) === season);
  const row = {
    ...(index < 0 ? {} : rows[index]),
    ...value,
    [key === 'seasons' ? 'season_number' : 'season']: season,
  };
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

export function extractMaxEpisodeFromRemark(textValue) {
  const raw = typeof textValue === 'string' ? textValue.trim() : '';
  if (!raw) return 0;
  const mUp = /(?:更新至|更至|更)\s*(\d{1,5})\s*(?:集|话|回|期|EP|E)?/i.exec(raw);
  if (mUp?.[1]) return Number.parseInt(mUp[1], 10) || 0;
  const mRange = /(\d{1,4})\s*\/\s*(\d{1,4})/.exec(raw);
  if (mRange?.[1] && mRange?.[2]) {
    const left = Number.parseInt(mRange[1], 10) || 0;
    const right = Number.parseInt(mRange[2], 10) || 0;
    if (left > 0 && right >= left) return left;
  }
  const mFull = /(?:全|共)\s*(\d{1,4})\s*(?:集|话|回|期)/i.exec(raw);
  if (mFull?.[1]) return Number.parseInt(mFull[1], 10) || 0;
  return 0;
}

export function formatQualityBadge(rawQuality) {
  if (typeof rawQuality !== 'string' || !rawQuality.trim()) return '';
  switch (rawQuality.trim().toLowerCase()) {
    case '8k': return '8K';
    case '4k_hdr': return '4K·HDR';
    case '4k_fps': return '4K·60帧';
    case '4k': return '4K';
    case '1080p': return '1080P';
    case '720p': return '720P';
    default: return rawQuality.trim();
  }
}

export function panDisplayLabel(provider) {
  switch (provider) {
    case 'yidong': return '移动云盘';
    case 'tianyi': return '天翼云盘';
    case 'quark': return '夸克网盘';
    case 'uc': return 'UC网盘';
    case 'baidu': return '百度网盘';
    default: return provider || '';
  }
}

export function variantParentOrSelf(id) {
  return variantParentId(id) ?? id;
}
