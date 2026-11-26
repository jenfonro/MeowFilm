import { providerFamily, qualityKey } from './rules.js';

const ruleOrder = ['quality', 'pan', 'keyword'];
const panOrder = ['yidong', 'tianyi', 'quark', 'uc', 'baidu'];
const siteCleanKeywords = ['直播', '体育', '短剧', '听书', '舞曲', '哔哩'];
const qualityRanks = { '8k': 7, '4k_hdr': 6, '4k_fps': 5, '4k': 4, '1080p': 2, '720p': 1 };

export function defaultPreferences() {
  return { ruleOrder: [...ruleOrder], panOrder: [...panOrder], keywords: [],
    siteCleanKeywords: [...siteCleanKeywords], iosAutoPrepare: true, metadataViewProvider: 'tmdb' };
}

function corruptPreferences() {
  throw Object.assign(new Error('CORRUPT_STORAGE'), { code: 'CORRUPT_STORAGE' });
}

function invalidPreferences() {
  throw Object.assign(new Error('Invalid film playback preferences'), { code: 'INVALID_PREFERENCES' });
}

function parsePreferences(value, { onInvalid, ownOnly, unique }) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) onInvalid();
  const result = defaultPreferences();
  const has = field => ownOnly ? Object.hasOwn(value, field) : value[field] !== undefined;
  for (const field of ['ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords']) {
    if (!has(field)) continue;
    if (!Array.isArray(value[field]) || value[field].some(item => typeof item !== 'string')) onInvalid();
    const items = value[field].map(item => item.trim());
    if (items.some(item => !item) || unique && new Set(items.map(item => item.toLowerCase())).size !== items.length) onInvalid();
    result[field] = items;
  }
  if (has('iosAutoPrepare')) {
    if (typeof value.iosAutoPrepare !== 'boolean') onInvalid();
    result.iosAutoPrepare = value.iosAutoPrepare;
  }
  if (has('metadataViewProvider')) {
    if (!['tmdb', 'douban'].includes(value.metadataViewProvider)) onInvalid();
    result.metadataViewProvider = value.metadataViewProvider;
  }
  return result;
}

export function readPreferences(value = {}) {
  return parsePreferences(value, { onInvalid: corruptPreferences, ownOnly: false, unique: false });
}

export function normalizePreferences(value = {}) {
  return parsePreferences(value, { onInvalid: invalidPreferences, ownOnly: true, unique: true });
}

export function isSmartSiteAllowed(source, preferences = defaultPreferences()) {
  if (!source || source.enabled === false || source.searchable === false || source.smartSkip === true) return false;
  const name = typeof source.name === 'string' ? source.name.toLowerCase() : '';
  return !preferences.siteCleanKeywords.some(keyword => name.includes(keyword.toLowerCase()));
}

function candidatePanFamily(candidate) {
  return providerFamily(candidate?.panProvider ?? candidate?.provider);
}

function normalizedQualityKey(value) {
  if (typeof value === 'string' && value.trim().toLowerCase() === '8k') return '8k';
  return qualityKey(value);
}

function matchesConstraint(candidate, constraint) {
  if (!constraint || constraint.action === 'default' || constraint.action === 'switch') return true;
  if (constraint.action === 'pan') {
    const family = providerFamily(constraint.provider);
    return family !== undefined && candidatePanFamily(candidate) === family;
  }
  if (constraint.action === 'quality') {
    const quality = normalizedQualityKey(constraint.quality);
    return quality !== '' && normalizedQualityKey(candidate.quality) === quality;
  }
  return true;
}

function keywordRank(candidate, keywords) {
  const text = [candidate.name, candidate.path, candidate.fileName, candidate.lineLabel]
    .filter(value => typeof value === 'string').join(' ').toLowerCase();
  const rank = keywords.findIndex(keyword => text.includes(keyword.toLowerCase()));
  return rank < 0 ? keywords.length : rank;
}

function panRank(candidate, preferences) {
  const family = candidatePanFamily(candidate);
  const rank = family ? preferences.panOrder.indexOf(family) : -1;
  return rank < 0 ? preferences.panOrder.length : rank;
}

export function compareCandidates(a, b, {
  preferences = defaultPreferences(),
  constraint = { action: 'default' },
  current,
  multiSeason = false,
  preferSeason = 0,
} = {}) {
  const allowed = Number(matchesConstraint(b, constraint)) - Number(matchesConstraint(a, constraint));
  if (allowed) return allowed;

  // Aligned with match_core.go:smartCompareSeasonRank — explicit file/folder season (4)
  // > title/manual season (3) > implicit Season 1 fallback (0) when multi-season is active.
  if (multiSeason && preferSeason > 0) {
    const rankA = a.seasonRank ?? 0;
    const rankB = b.seasonRank ?? 0;
    if (rankA !== rankB) return rankB - rankA;
  }

  const compare = dimension => {
    if (dimension === 'quality') {
      return (qualityRanks[normalizedQualityKey(b.quality)] ?? 0) - (qualityRanks[normalizedQualityKey(a.quality)] ?? 0);
    }
    if (dimension === 'pan') return panRank(a, preferences) - panRank(b, preferences);
    if (dimension === 'keyword') return keywordRank(a, preferences.keywords) - keywordRank(b, preferences.keywords);
    return 0;
  };

  if (constraint.action === 'pan' || constraint.action === 'quality') {
    const dimension = constraint.action === 'pan' ? 'quality' : 'pan';
    const currentValue = dimension === 'quality'
      ? normalizedQualityKey(current?.quality)
      : candidatePanFamily(current);
    if (currentValue) {
      const value = candidate => dimension === 'quality'
        ? normalizedQualityKey(candidate.quality)
        : candidatePanFamily(candidate);
      const preferred = Number(value(b) === currentValue) - Number(value(a) === currentValue);
      if (preferred) return preferred;
    } else if (constraint.action === 'quality' && current?.source && current.lineId !== undefined) {
      const sameLine = candidate => candidate.source?.key === current.source.key &&
        (candidate.detailId ?? candidate.source?.detailId) === (current.detailId ?? current.source.detailId) &&
        candidate.lineId === current.lineId;
      const preferred = Number(sameLine(b)) - Number(sameLine(a));
      if (preferred) return preferred;
    }
    const secondary = compare(dimension);
    if (secondary) return secondary;
  }

  for (const dimension of preferences.ruleOrder) {
    const result = compare(dimension);
    if (result) return result;
  }

  // Degraded single-baseline mapping ranks after strict season/episode matches.
  if (Boolean(a.degraded) !== Boolean(b.degraded)) {
    return a.degraded ? 1 : -1;
  }

  const orderA = Number.isFinite(a.source?.order) ? a.source.order : 999999;
  const orderB = Number.isFinite(b.source?.order) ? b.source.order : 999999;
  if (orderA !== orderB) return orderA - orderB;
  return (a.index ?? 0) - (b.index ?? 0);
}

export function isItemBlocked(blocks, targetId, sourceKey, detailId) {
  if (!Array.isArray(blocks) || !targetId) return false;
  return blocks.some(block => !block?.errorCode &&
    block?.targetId === targetId &&
    block.sourceKey === sourceKey &&
    block.detailId === detailId &&
    (block.lineId === undefined || block.lineId === null));
}

export function isLineBlocked(blocks, targetId, sourceKey, detailId, lineId) {
  if (!Array.isArray(blocks) || !targetId) return false;
  return blocks.some(block => !block?.errorCode &&
    block?.targetId === targetId &&
    block.sourceKey === sourceKey &&
    block.detailId === detailId &&
    (block.lineId === undefined || block.lineId === null || block.lineId === lineId));
}

function isExcluded(candidate, excluded) {
  if (!excluded) return false;
  const identities = [candidate.id, candidate.fileIdentity].filter(value => value !== undefined);
  if (excluded instanceof Set) return identities.some(id => excluded.has(id));
  for (const item of excluded) {
    if (typeof item === 'string' && identities.includes(item)) return true;
    if (item && typeof item === 'object' &&
      (item.id !== undefined && item.id === candidate.id ||
       item.fileIdentity !== undefined && item.fileIdentity === candidate.fileIdentity)) return true;
  }
  return false;
}

export function isCandidateAllowed(candidate, {
  targetId,
  kind,
  season,
  episode,
  globalEpisode,
  constraint = { action: 'default' },
  excluded,
  blocks = [],
  sourceFilter = null,
} = {}) {
  if (!candidate || !matchesConstraint(candidate, constraint) || isExcluded(candidate, excluded)) return false;
  if (candidate.share && !candidate.file) return false;
  if (kind === 'movie' && candidate.eligible === false) return false;
  if (sourceFilter) {
    if (sourceFilter.sourceKey && candidate.source?.key !== sourceFilter.sourceKey) return false;
    if (sourceFilter.detailId !== undefined && (candidate.detailId ?? candidate.source?.detailId) !== sourceFilter.detailId) return false;
    if (sourceFilter.lineId !== undefined && candidate.lineId !== sourceFilter.lineId) return false;
    if (sourceFilter.candidateId !== undefined && candidate.id !== sourceFilter.candidateId) return false;
  }
  if (kind === 'tv') {
    if (globalEpisode !== undefined && candidate.globalEpisode !== undefined) {
      if (candidate.globalEpisode !== globalEpisode) return false;
    } else if (episode !== undefined) {
      if (candidate.episode !== episode) return false;
      if (season !== undefined && candidate.season !== season) return false;
    } else {
      return false;
    }
  }
  const detailId = candidate.detailId ?? candidate.source?.detailId;
  return !isLineBlocked(blocks, targetId, candidate.source?.key, detailId, candidate.lineId);
}
