import { providerFamily, qualityKey } from './rules.js';

const ruleOrder = ['quality', 'pan', 'keyword'];
const panOrder = ['yidong', 'tianyi', 'quark', 'uc', 'baidu'];
const siteCleanKeywords = ['直播', '体育', '短剧', '听书', '舞曲', '哔哩'];
const qualityRanks = { '4k_hdr': 6, '4k_fps': 5, '4k': 4, '1080p': 2, '720p': 1 };

export function defaultPreferences() {
  return { ruleOrder: [...ruleOrder], panOrder: [...panOrder], keywords: [],
    siteCleanKeywords: [...siteCleanKeywords], iosAutoPrepare: true };
}

function invalidPreferences() {
  throw Object.assign(new Error('Invalid film playback preferences'), { code: 'INVALID_PREFERENCES' });
}

export function normalizePreferences(value = {}) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) invalidPreferences();
  const result = defaultPreferences();
  for (const field of ['ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords']) {
    if (!Object.hasOwn(value, field)) continue;
    if (!Array.isArray(value[field]) || value[field].some(item => typeof item !== 'string')) invalidPreferences();
    const items = value[field].map(item => item.trim());
    if (items.some(item => !item) || new Set(items.map(item => item.toLowerCase())).size !== items.length) invalidPreferences();
    if (field === 'ruleOrder' && (items.length !== ruleOrder.length || items.some(item => !ruleOrder.includes(item)))) {
      invalidPreferences();
    }
    if (field === 'panOrder') {
      if (items.some(item => !panOrder.includes(item))) invalidPreferences();
      result[field] = [...items, ...panOrder.filter(item => !items.includes(item))];
    } else result[field] = items;
  }
  if (Object.hasOwn(value, 'iosAutoPrepare')) {
    if (typeof value.iosAutoPrepare !== 'boolean') invalidPreferences();
    result.iosAutoPrepare = value.iosAutoPrepare;
  }
  return result;
}

export function isSmartSiteAllowed(source, preferences = defaultPreferences()) {
  if (!source || source.enabled === false || source.enabled === 0 || source.searchable === false || source.searchable === 0) return false;
  const name = typeof source.name === 'string' ? source.name.toLowerCase() : '';
  return !preferences.siteCleanKeywords.some(keyword => name.includes(keyword.toLowerCase()));
}

function matchesConstraint(candidate, constraint) {
  if (constraint.action === 'pan') {
    const family = providerFamily(constraint.provider);
    return family !== undefined && providerFamily(candidate.provider) === family;
  }
  if (constraint.action === 'quality') {
    const quality = qualityKey(constraint.quality);
    return quality !== '' && qualityKey(candidate.quality) === quality;
  }
  return constraint.action === 'default' || constraint.action === 'switch';
}

function keywordRank(candidate, keywords) {
  const text = [candidate.name, candidate.path, candidate.fileName, candidate.displayName]
    .filter(value => typeof value === 'string').join(' ').toLowerCase();
  const rank = keywords.findIndex(keyword => text.includes(keyword.toLowerCase()));
  return rank < 0 ? keywords.length : rank;
}

function panRank(candidate, preferences) {
  const rank = preferences.panOrder.indexOf(providerFamily(candidate.provider));
  return rank < 0 ? preferences.panOrder.length : rank;
}

export function compareCandidates(a, b, { preferences = defaultPreferences(), constraint = { action: 'default' },
  current } = {}) {
  const allowed = Number(matchesConstraint(b, constraint)) - Number(matchesConstraint(a, constraint));
  if (allowed) return allowed;
  const compare = dimension => {
    if (dimension === 'quality') return (qualityRanks[qualityKey(b.quality)] ?? 0) - (qualityRanks[qualityKey(a.quality)] ?? 0);
    if (dimension === 'pan') return panRank(a, preferences) - panRank(b, preferences);
    return keywordRank(a, preferences.keywords) - keywordRank(b, preferences.keywords);
  };
  if (constraint.action === 'pan' || constraint.action === 'quality') {
    const dimension = constraint.action === 'pan' ? 'quality' : 'pan';
    const currentValue = dimension === 'quality' ? qualityKey(current?.quality) : providerFamily(current?.provider);
    if (currentValue) {
      const value = candidate => dimension === 'quality' ? qualityKey(candidate.quality) : providerFamily(candidate.provider);
      const preferred = Number(value(b) === currentValue) - Number(value(a) === currentValue);
      if (preferred) return preferred;
    } else if (constraint.action === 'quality' && current?.source && current.lineId !== undefined) {
      // An ordinary/unknown line can retain its exact current source identity
      // without being classified as a built-in pan family.
      const sameLine = candidate => candidate.source?.key === current.source.key &&
        (candidate.detailId ?? candidate.source?.detailId) === (current.detailId ?? current.source.detailId) &&
        candidate.lineId === current.lineId;
      const preferred = Number(sameLine(b)) - Number(sameLine(a));
      if (preferred) return preferred;
    }
    const secondary = compare(dimension);
    if (secondary) return secondary;
  }
  // This compares only available candidates. Scheduling/early-stop gates while
  // searchComplete is false belong to the session, not to a sort comparator.
  for (const dimension of preferences.ruleOrder) {
    const result = compare(dimension);
    if (result) return result;
  }
  return 0;
}

function sourceMatches(actual, expected) {
  if (!actual || !expected) return false;
  return ['key', 'engine', 'installationId', 'siteId', 'revision', 'generation'].every(field =>
    expected[field] === undefined || actual[field] === expected[field]);
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

export function isCandidateAllowed(candidate, { target, constraint = { action: 'default' }, excluded, blocks = [],
  actualEpisode } = {}) {
  if (!candidate || !matchesConstraint(candidate, constraint) || isExcluded(candidate, excluded)) return false;
  if (candidate.share && !candidate.file) return false;
  if (target) {
    if (candidate.targetId !== undefined && candidate.targetId !== target.id) return false;
    if (candidate.kind !== undefined && target.kind !== 'source' && candidate.kind !== target.kind) return false;
    if (target.kind === 'group' && (!target.source || actualEpisode) &&
        !target.members?.some(member => member.sourceKey === candidate.source?.key &&
      member.detailId === (candidate.detailId ?? candidate.source?.detailId))) return false;
    if (actualEpisode && ['source', 'group'].includes(target.kind)) {
      if (target.kind === 'source' && (candidate.source?.key !== actualEpisode.source?.key ||
          candidate.detailId !== actualEpisode.detailId)) return false;
      if (actualEpisode.episode === undefined && actualEpisode.globalEpisode === undefined) return false;
      if (actualEpisode.episode !== undefined && (candidate.episode !== actualEpisode.episode ||
          candidate.season !== actualEpisode.season)) return false;
      if (actualEpisode.globalEpisode !== undefined && candidate.globalEpisode !== actualEpisode.globalEpisode) return false;
    }
    if (target.kind === 'source' || target.kind === 'group' && target.source && !actualEpisode) {
      if (!sourceMatches(candidate.source, target.source)) return false;
      if (target.source.detailId !== undefined && candidate.detailId !== target.source.detailId) return false;
      if (!actualEpisode) {
        if (target.source.lineId !== undefined && candidate.lineId !== target.source.lineId) return false;
        if (target.source.candidateId !== undefined && candidate.id !== target.source.candidateId) return false;
        if (target.source.playId !== undefined && candidate.playId !== target.source.playId) return false;
        if (target.source.episodeId !== undefined && candidate.episodeId !== target.source.episodeId) return false;
      }
    }
    if (target.kind !== 'movie') {
      const coordinates = target.kind === 'source' && target.episode !== undefined ? ['season', 'episode']
        : ['season', 'episode', 'globalEpisode'];
      for (const field of coordinates) {
        if (target[field] !== undefined && candidate[field] !== target[field]) return false;
      }
    }
  }
  return !blocks.some(block => target?.id !== undefined && block.targetId === target.id &&
    block.sourceKey === candidate.source?.key && block.detailId === (candidate.detailId ?? candidate.source?.detailId) &&
    block.lineId === candidate.lineId);
}
