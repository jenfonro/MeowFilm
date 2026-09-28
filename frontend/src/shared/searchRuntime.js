import { fetchBootstrap } from './bootstrap';
import { requestCatSpider } from './catpawrunner';
import { fetchTMDBDetailCached } from './tmdbRuntime';
import {
  buildTMDBDetailTextBadge,
  getTMDBBackdropPath,
  getTMDBDetailTitle,
  getTMDBPosterPath,
  getTMDBSearchResults,
  getTMDBSearchTitle,
  getTMDBYear,
  normalizeTMDBID,
  normalizeTMDBMediaType,
} from './tmdbRaw';
import { normalizeString } from './normalize';
import { requestJson } from './requestJson';

const SEARCH_HISTORY_ENDPOINT = '/api/searchhistory';
const USER_SITES_ENDPOINT = '/api/user/sites';
const SMART_MATCHBLOCK_ITEMS_ENDPOINT = '/api/smart/matchblock/items';

const buildTMDBDataURL = (path, params = {}) => {
  const rel = normalizeString(path).replace(/^\/+/, '');
  const searchParams = new URLSearchParams();
  Object.entries(params || {}).forEach(([key, value]) => {
    if (value == null) return;
    const text = String(value).trim();
    if (!text) return;
    searchParams.set(key, text);
  });
  const url = `/api/tmdb/${rel}`;
  return searchParams.toString() ? `${url}?${searchParams.toString()}` : url;
};

export const buildTrailingDigitsFallbackQuery = (query) => {
  const raw = normalizeString(query);
  if (!raw) return '';
  const next = raw.replace(/\s*[0-9]{1,2}\s*$/g, ' ').replace(/\s+/g, ' ').trim();
  if (!next || next === raw) return '';
  return next;
};

export const requestTMDBSearchWithTrailingDigitsFallback = async (query) => {
  const safeQuery = normalizeString(query);
  const requestPayload = async (text) =>
    requestJson(buildTMDBDataURL('search', { q: text }), {
      method: 'GET',
      credentials: 'same-origin',
    });
  const rawPayload = await requestPayload(safeQuery);
  const rawResults = Array.isArray(rawPayload && rawPayload.results) ? rawPayload.results : [];
  if (rawResults.length > 0) {
    return {
      payload: rawPayload,
      effectiveQuery: safeQuery,
      usedFallback: false,
    };
  }
  const fallbackQuery = buildTrailingDigitsFallbackQuery(safeQuery);
  if (!fallbackQuery) {
    return {
      payload: rawPayload,
      effectiveQuery: safeQuery,
      usedFallback: false,
    };
  }
  const fallbackPayload = await requestPayload(fallbackQuery);
  const fallbackResults = Array.isArray(fallbackPayload && fallbackPayload.results) ? fallbackPayload.results : [];
  if (fallbackResults.length === 0) {
    return {
      payload: rawPayload,
      effectiveQuery: safeQuery,
      usedFallback: false,
    };
  }
  return {
    payload: fallbackPayload,
    effectiveQuery: fallbackQuery,
    usedFallback: true,
  };
};

const normalizeForMatch = (value) =>
  String(value || '')
    .toLowerCase()
    .replace(/[\s\u200b\u200c\u200d\ufeff]+/g, '')
    .trim();

const normalizeForGroupKey = (value) =>
  String(value || '')
    .toLowerCase()
    .replace(/[\u200b\u200c\u200d\ufeff]+/g, '')
    .replace(/[\s.\-_,，:：;；!！?？·•/\\|]+/g, '')
    .trim();

const normalizeDisplayTitle = (value) =>
  String(value || '')
    .replace(/[\u200b\u200c\u200d\ufeff]+/g, '')
    .replace(/[\s.\-_,，:：;；!！?？·•/\\|]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();

const sanitizeDisplayTitle = (value) =>
  String(value || '')
    .replace(/[\u200b\u200c\u200d\ufeff]+/g, '')
    .replace(/\s+/g, ' ')
    .trim();

const buildEmojiCleaner = () => {
  try {
    new RegExp('\\p{Extended_Pictographic}', 'u');
    return (value) =>
      String(value || '')
        .replace(/[\p{Extended_Pictographic}\p{Emoji_Presentation}]/gu, '')
        .replace(/[\uFE0E\uFE0F]/g, '');
  } catch (_error) {
    return (value) =>
      String(value || '')
        .replace(/[\uD83C-\uDBFF][\uDC00-\uDFFF]/g, '')
        .replace(/[\u2600-\u27BF]/g, '')
        .replace(/[\uFE0E\uFE0F]/g, '');
  }
};

const stripEmojiSymbols = buildEmojiCleaner();

const preCleanForRules = (value) => stripEmojiSymbols(sanitizeDisplayTitle(value));

const stripSeasonMarkers = (value) =>
  String(value || '')
    .replace(/第\s*(?:[0-9０-９]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})\s*季/gi, ' ')
    .replace(/(?:^|\s)([0-9０-９]{1,3})\s*季(?![度节])/gi, ' ')
    .replace(/\bS\s*\d{1,2}\b/gi, ' ')
    .replace(/\bSeason\s*\d{1,2}\b/gi, ' ')
    .replace(/\s+/g, ' ')
    .trim();

const stripYearbangMarkers = (value) =>
  String(value || '')
    .replace(/年番\s*(?:[0-9０-９]{1,3}|[一二三四五六七八九十百千两零〇]{1,10})?/gi, ' ')
    .replace(/\s+/g, ' ')
    .trim();

const stripSeriesVariantMarkers = (value) =>
  stripYearbangMarkers(stripSeasonMarkers(value));

export const stripTrailingSeriesDigits = (value, { contentKind = 'tv' } = {}) => {
  const kind = normalizeString(contentKind).toLowerCase();
  const base = sanitizeDisplayTitle(value);
  if (!base || kind === 'movie') return base;
  return String(base)
    .replace(/\s*[0-9０-９]{1,2}\s*$/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
};

export const stripSearchAliasMarkers = (value, { contentKind = 'tv' } = {}) => {
  const kind = normalizeString(contentKind).toLowerCase();
  const base = sanitizeDisplayTitle(value);
  if (!base) return '';
  if (kind === 'movie') return base;
  return stripTrailingSeriesDigits(stripSeriesVariantMarkers(base), { contentKind: kind || 'tv' }) || base;
};

export const resolveCanonicalSearchVariants = (value, { contentKind = 'tv' } = {}) => {
  const raw = sanitizeDisplayTitle(value);
  const kind = normalizeString(contentKind).toLowerCase() || 'tv';
  const seasonStripped = kind === 'movie' ? raw : (stripSeriesVariantMarkers(raw) || raw);
  const digitsStripped = kind === 'movie' ? seasonStripped : (stripTrailingSeriesDigits(seasonStripped, { contentKind: kind }) || seasonStripped);
  const canonical = kind === 'movie' ? raw : (digitsStripped || seasonStripped || raw);
  return {
    raw,
    seasonStripped,
    digitsStripped,
    canonical,
  };
};

const parseChineseSeasonNo = (rawValue) => {
  const raw = normalizeString(rawValue);
  if (!raw) return 0;
  const digits = raw.replace(/[０-９]/g, (char) => String('０１２３４５６７８９'.indexOf(char)));
  if (/^\d+$/.test(digits)) {
    const parsed = Number.parseInt(digits, 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
  }
  const map = {
    零: 0,
    〇: 0,
    一: 1,
    二: 2,
    两: 2,
    三: 3,
    四: 4,
    五: 5,
    六: 6,
    七: 7,
    八: 8,
    九: 9,
    十: 10,
  };
  if (raw === '十') return 10;
  if (raw.includes('十')) {
    const [tensRaw, onesRaw] = raw.split('十');
    const tens = tensRaw ? map[tensRaw] || 0 : 1;
    const ones = onesRaw ? map[onesRaw] || 0 : 0;
    const next = tens * 10 + ones;
    return next > 0 ? next : 0;
  }
  return map[raw] || 0;
};

const extractSeriesVariantMetaFromText = (rawValue) => {
  const text = normalizeString(rawValue);
  if (!text) return { kind: '', index: 0 };
  const cleaned = preCleanForRules(text) || text;
  const matchSeasonEpisode = cleaned.match(/\bS\s*(\d{1,2})\s*E\s*\d{1,5}\b/i);
  if (matchSeasonEpisode && matchSeasonEpisode[1]) {
    const parsed = Number.parseInt(String(matchSeasonEpisode[1]), 10);
    if (Number.isFinite(parsed) && parsed > 0) return { kind: 'season', index: parsed };
  }
  const matchCnSeason = cleaned.match(/(?:第\s*)?([0-9０-９]{1,3}|[一二三四五六七八九十两〇零]{1,6})\s*季/i);
  if (matchCnSeason && matchCnSeason[1]) {
    return { kind: 'season', index: parseChineseSeasonNo(String(matchCnSeason[1])) };
  }
  const matchYearbang = cleaned.match(/年番\s*([0-9０-９]{1,3}|[一二三四五六七八九十两〇零]{1,6})/i);
  if (matchYearbang && matchYearbang[1]) {
    return { kind: 'yearbang', index: parseChineseSeasonNo(String(matchYearbang[1])) };
  }
  if (/年番/i.test(cleaned)) return { kind: 'yearbang', index: 1 };
  return { kind: '', index: 0 };
};

export const resolveDisplayedSiteGroupKey = (item, tmdbByGroup, displayMode) => {
  const baseGroupKey = normalizeString(item && item.groupKey);
  if (!baseGroupKey) return '';
  if (displayMode !== 'tmdb' && displayMode !== 'both') return baseGroupKey;
  const title = normalizeString(item && item.title);
  if (!title) return baseGroupKey;
  const cleanedTitle = preCleanForRules(title) || title;
  const strippedTitle = stripSearchAliasMarkers(cleanedTitle, { contentKind: 'tv' });
  if (!strippedTitle || strippedTitle === cleanedTitle) return baseGroupKey;
  const strippedGroupKey = normalizeForGroupKey(strippedTitle);
  if (!strippedGroupKey) return baseGroupKey;
  const tmdbMatch = tmdbByGroup.get(strippedGroupKey) || null;
  if (!tmdbMatch || tmdbMatch.tmdbType !== 'tv') return baseGroupKey;
  return strippedGroupKey;
};

const CIRCLED_DIGITS = ['①', '②', '③', '④', '⑤', '⑥', '⑦', '⑧', '⑨', '⑩', '⑪', '⑫', '⑬', '⑭', '⑮', '⑯', '⑰', '⑱', '⑲', '⑳'];
const formatCircledIndex = (index) => CIRCLED_DIGITS[index] || `(${index + 1})`;

const buildTMDBGroupMap = (tmdbItems) => {
  const map = new Map();
  (Array.isArray(tmdbItems) ? tmdbItems : []).forEach((item) => {
    const groupKey = normalizeString(item && item.groupKey);
    if (!groupKey) return;
    map.set(groupKey, item);
  });
  return map;
};

const buildSiteDisplayGroups = (siteItems, { tmdbByGroup, displayMode, context } = {}) => {
  const groups = new Map();
  (Array.isArray(siteItems) ? siteItems : []).forEach((item) => {
    const key = resolveDisplayedSiteGroupKey(item, tmdbByGroup, displayMode);
    if (!key) return;
    const current = groups.get(key) || { representative: null, items: [], siteKeys: new Set(), title: '' };
    current.items.push(item);
    current.siteKeys.add(item.siteKey);
    current.title = current.title || item.title;
    if (!current.representative || compareRepresentative(item, current.representative, context) < 0) {
      current.representative = item;
    }
    groups.set(key, current);
  });
  return groups;
};

const applyTMDBAggregateIntoCards = (tmdbByGroup, groups) => {
  Array.from(groups.entries()).forEach(([groupKey, group]) => {
    const tmdbMatch = tmdbByGroup.get(groupKey) || null;
    if (!tmdbMatch) return;
    const siteCount = group && group.siteKeys ? group.siteKeys.size : 0;
    const aggregateRemark = formatAggregateTVRemark({
      tmdbMatch,
      groupTitle: (tmdbMatch && tmdbMatch.title) || (group && group.title) || '',
      sources: group && Array.isArray(group.items) ? group.items : [],
    });
    tmdbMatch.aggregateSourceCount = siteCount;
    tmdbMatch.aggregateItems = (group && Array.isArray(group.items) ? group.items : []).slice().map((item) => mergeDisplayedSiteItem(item, tmdbMatch));
    tmdbMatch.aggregateKind = 'tmdb';
    tmdbMatch.contentKey = buildDisplayedContentKey(tmdbMatch.title, tmdbMatch.contentKey);
    if (aggregateRemark) tmdbMatch.textBadge = aggregateRemark;
  });
};

const sortSiteItemsByRuntimeOrder = (items, siteOrderMap) =>
  (Array.isArray(items) ? items : [])
    .slice()
    .sort((left, right) => {
      const leftOrder = siteOrderMap.has(left.siteKey) ? siteOrderMap.get(left.siteKey) : 999999;
      const rightOrder = siteOrderMap.has(right.siteKey) ? siteOrderMap.get(right.siteKey) : 999999;
      if (leftOrder !== rightOrder) return leftOrder - rightOrder;
      const leftLen = normalizeString(left.title).length;
      const rightLen = normalizeString(right.title).length;
      if (leftLen !== rightLen) return rightLen - leftLen;
      return 0;
    });

const isSiteItemBlockedForDisplay = (item, blockedKeySet, blockedMatchMap) => {
  if (!item) return true;
  if (blockedKeySet.has(normalizeString(item.siteKey))) return true;
  const matchKey = `${normalizeString(item.siteKey)}::${normalizeString(item.siteDetail)}`;
  return !!(blockedMatchMap[matchKey] && blockedMatchMap[matchKey].blockAll);
};

export const buildSiteSourceItemsForTitle = ({
  snapshot,
  runtimeConfig,
  blockedSiteKeys = [],
  blockedMatchIndex = {},
  title = '',
  contentKind = 'tv',
} = {}) => {
  if (!snapshot || !runtimeConfig) return [];
  const siteItems = Array.isArray(snapshot.siteItems) ? snapshot.siteItems : [];
  if (!siteItems.length) return [];
  const targetGroupKey = buildSearchGroupKey(title, runtimeConfig.aggregateRules, { contentKind });
  if (!targetGroupKey) return [];
  const displayMode = normalizeString(runtimeConfig && runtimeConfig.searchDisplayMode);
  const tmdbByGroup = buildTMDBGroupMap(snapshot.tmdbItems);
  const blockedKeySet = new Set(
    (Array.isArray(blockedSiteKeys) ? blockedSiteKeys : [])
      .map((item) => normalizeString(item))
      .filter(Boolean)
  );
  const blockedMatchMap = blockedMatchIndex && typeof blockedMatchIndex === 'object' ? blockedMatchIndex : {};
  const siteOrderMap = runtimeConfig.siteOrderMap instanceof Map ? runtimeConfig.siteOrderMap : new Map();

  const groups = buildSiteDisplayGroups(siteItems, {
    tmdbByGroup,
    displayMode,
    context: runtimeConfig,
  });

  const group = groups.get(targetGroupKey);
  if (!group || !Array.isArray(group.items) || !group.items.length) return [];

  const matched = sortSiteItemsByRuntimeOrder(
    group.items.filter((item) => !isSiteItemBlockedForDisplay(item, blockedKeySet, blockedMatchMap)),
    siteOrderMap
  );

  const titleCounts = new Map();
  matched.forEach((item) => {
    const duplicateKey = `${normalizeString(item.siteKey)}::${normalizeString(item.title)}`;
    titleCounts.set(duplicateKey, (titleCounts.get(duplicateKey) || 0) + 1);
  });

  const titleIndexes = new Map();
  return matched.map((item) => {
    const duplicateKey = `${normalizeString(item.siteKey)}::${normalizeString(item.title)}`;
    const nextIndex = titleIndexes.has(duplicateKey) ? titleIndexes.get(duplicateKey) + 1 : 0;
    titleIndexes.set(duplicateKey, nextIndex);
    const duplicateCount = titleCounts.get(duplicateKey) || 0;
    return {
      ...item,
      displayLabel: `${normalizeString(item.siteName)}-${normalizeString(item.title)}${duplicateCount > 1 ? ` ${formatCircledIndex(nextIndex)}` : ''}`,
    };
  });
};

const buildCanonicalSearchTitle = (title, compiledRules, { queryTrailingDigits = '', contentKind = 'tv' } = {}) => {
  const base = sanitizeDisplayTitle(title);
  if (!base) return '';
  if (normalizeString(contentKind) === 'movie') return normalizeDisplayTitle(base) || base;
  const preCleaned = preCleanForRules(base) || base;
  const cleaned = sanitizeDisplayTitle(
    applyCleanRules(preCleaned, compiledRules, {
      skipTrailingDigitsRule: !!normalizeString(queryTrailingDigits),
      queryTrailingDigits,
    }) || preCleaned
  );
  const canonical = stripSearchAliasMarkers(cleaned, { contentKind }) || cleaned || base;
  return normalizeDisplayTitle(canonical) || canonical || base;
};

export const scoreTMDBSearchHitByQuery = (itemTitle, rawQuery, canonicalQuery, { contentKind = 'tv' } = {}) => {
  const title = sanitizeDisplayTitle(itemTitle);
  const raw = sanitizeDisplayTitle(rawQuery);
  const canonical = sanitizeDisplayTitle(canonicalQuery);
  if (!title || !raw) return 0;
  const scoreRaw = computeMatchScoreFactory(raw)(title);
  if (scoreRaw > 0) return scoreRaw;
  const kind = normalizeString(contentKind).toLowerCase();
  if (kind === 'movie') return 0;
  if (!canonical || canonical === raw) return 0;
  const canonicalTitle = stripSearchAliasMarkers(title, { contentKind: kind || 'tv' }) || title;
  const scoreCanonical = computeMatchScoreFactory(canonical)(canonicalTitle);
  if (scoreCanonical <= 0) return 0;
  if (scoreCanonical >= 1000) return 1000;
  return Math.min(699, scoreCanonical);
};

export const pickPreferredTMDBResult = (items, rawQuery, canonicalQuery, { contentKind = 'tv' } = {}) => {
  const list = Array.isArray(items) ? items : [];
  let best = null;
  list.forEach((item, index) => {
    const title = sanitizeDisplayTitle(getTMDBSearchTitle(item));
    const score = scoreTMDBSearchHitByQuery(title, rawQuery, canonicalQuery, { contentKind });
    if (!best || score > best.score || (score === best.score && index < best.index)) {
      best = { item, score, index };
    }
  });
  return best ? best.item : null;
};

const normalizePatternInput = (value) => {
  const raw = normalizeString(value);
  if (!raw) return null;
  if (raw.startsWith('/') && raw.lastIndexOf('/') > 0) {
    const last = raw.lastIndexOf('/');
    const pattern = raw.slice(1, last).trim();
    const flags = raw.slice(last + 1).trim();
    if (!pattern) return null;
    return { pattern, flags };
  }
  return { pattern: raw, flags: '' };
};

const buildRegexFromInput = (value, { defaultFlags = '', forceGlobal = false } = {}) => {
  const parsed = normalizePatternInput(value);
  if (!parsed || !parsed.pattern) return null;
  let flags = parsed.flags || defaultFlags;
  if (forceGlobal && !flags.includes('g')) flags += 'g';
  try {
    return new RegExp(parsed.pattern, flags);
  } catch (_error) {
    return null;
  }
};

const isTrailingDigitsRuleForQuery = (rule, queryTrailingDigits) => {
  const tailDigits = normalizeString(queryTrailingDigits);
  if (!rule || typeof rule.test !== 'function' || !tailDigits) return false;
  try {
    if (rule.global || rule.sticky) rule.lastIndex = 0;
  } catch (_error) {}
  try {
    const t1 = `x${tailDigits}`;
    const t2 = `x${tailDigits}y`;
    const r1 = t1.replace(rule, '');
    const r2 = t2.replace(rule, '');
    return r1 === 'x' && r2 === t2;
  } catch (_error) {
    return false;
  }
};

const compileCleanRules = (rawRules) => {
  const list = Array.isArray(rawRules) ? rawRules : [];
  return list
    .map((rule) => buildRegexFromInput(rule, { defaultFlags: 'ig', forceGlobal: true }))
    .filter(Boolean);
};

const applyCleanRules = (text, compiledRules, { skipTrailingDigitsRule = false, queryTrailingDigits = '' } = {}) => {
  let out = String(text || '');
  compiledRules.forEach((rule) => {
    if (skipTrailingDigitsRule && isTrailingDigitsRuleForQuery(rule, queryTrailingDigits)) return;
    try {
      if (rule.global || rule.sticky) rule.lastIndex = 0;
      out = out.replace(rule, '');
    } catch (_error) {}
  });
  return out;
};

const computeMatchScoreFactory = (query) => {
  const rawQuery = normalizeString(query);
  const normalizedQuery = normalizeForMatch(rawQuery);
  return (title) => {
    const name = normalizeForMatch(title);
    if (!normalizedQuery || !name) return 0;
    if (name === normalizedQuery) return 1000;
    if (name.startsWith(normalizedQuery)) return 900;
    const index = name.indexOf(normalizedQuery);
    if (index >= 0) {
      const positionBoost = 60 - Math.min(60, index);
      const lengthBoost = 40 - Math.min(40, Math.max(0, name.length - normalizedQuery.length));
      return 800 + positionBoost + lengthBoost;
    }
    const tokens = rawQuery
      .toLowerCase()
      .split(/\s+/g)
      .map((token) => token.trim())
      .filter(Boolean);
    if (tokens.length >= 2) {
      let hits = 0;
      tokens.forEach((token) => {
        if (token && name.includes(token)) hits += 1;
      });
      if (hits) return 600 + hits * 20;
    }
    return 0;
  };
};

const isConfigCenterSite = (site) => {
  const api = normalizeString(site && site.api);
  const key = normalizeString(site && site.key).toLowerCase();
  return api.includes('/spider/baseset/') || key.includes('baseset');
};

const buildSiteOrderMap = (settings = {}) => {
  const fallback = Array.isArray(settings.searchSiteOrder) ? settings.searchSiteOrder : [];
  const orderMap = new Map();
  fallback.forEach((key, index) => {
    const safeKey = normalizeString(key);
    if (safeKey && !orderMap.has(safeKey)) orderMap.set(safeKey, index);
  });
  return orderMap;
};

const compareRepresentative = (candidate, current, context) => {
  if (!current) return -1;
  if (candidate.score !== current.score) return current.score - candidate.score;
  const candidateTitleLength = candidate.title ? candidate.title.length : 0;
  const currentTitleLength = current.title ? current.title.length : 0;
  if (candidateTitleLength !== currentTitleLength) return candidateTitleLength - currentTitleLength;
  const preferredCoverSite = normalizeString(context && context.searchCoverSite);
  const siteOrderMap = context && context.siteOrderMap instanceof Map ? context.siteOrderMap : new Map();
  const candidatePreferred = preferredCoverSite && candidate.siteKey === preferredCoverSite ? 1 : 0;
  const currentPreferred = preferredCoverSite && current.siteKey === preferredCoverSite ? 1 : 0;
  if (candidatePreferred !== currentPreferred) return currentPreferred - candidatePreferred;
  const candidateOrder = siteOrderMap.has(candidate.siteKey) ? siteOrderMap.get(candidate.siteKey) : 999999;
  const currentOrder = siteOrderMap.has(current.siteKey) ? siteOrderMap.get(current.siteKey) : 999999;
  if (candidateOrder !== currentOrder) return candidateOrder - currentOrder;
  return candidate.seq - current.seq;
};

const compareDisplayedResults = (left, right, context) => {
  const siteOrderMap = context && context.siteOrderMap instanceof Map ? context.siteOrderMap : new Map();
  const pinTmdbFirst = !!(context && context.pinTmdbFirst);
  const leftKind = pinTmdbFirst ? (left.sourceKind === 'tmdb' ? 0 : 1) : 0;
  const rightKind = pinTmdbFirst ? (right.sourceKind === 'tmdb' ? 0 : 1) : 0;
  if (leftKind !== rightKind) return leftKind - rightKind;
  if (left.score !== right.score) return right.score - left.score;
  if (left.sourceKind === 'tmdb' && right.sourceKind === 'tmdb' && left.tmdbRank !== right.tmdbRank) {
    return left.tmdbRank - right.tmdbRank;
  }
  const leftOrder = siteOrderMap.has(left.siteKey) ? siteOrderMap.get(left.siteKey) : 999999;
  const rightOrder = siteOrderMap.has(right.siteKey) ? siteOrderMap.get(right.siteKey) : 999999;
  if (leftOrder !== rightOrder) return leftOrder - rightOrder;
  const leftTitleLength = left.title ? left.title.length : 0;
  const rightTitleLength = right.title ? right.title.length : 0;
  if (leftTitleLength !== rightTitleLength) return leftTitleLength - rightTitleLength;
  return left.seq - right.seq;
};

const snapshotSearchState = (state, { progressDone = 0, progressTotal = 0 } = {}) => ({
  displayMode: state.displayMode,
  tmdbItems: state.tmdbItems.slice(),
  siteItems: state.siteItems.slice(),
  displayedItems: [],
  pinTmdbFirst: state.pinTmdbFirst,
  siteTotal: state.siteTotal,
  progressDone,
  progressTotal,
});

const buildTmdbPoster = (item) => {
  return getTMDBPosterPath(item);
};

const buildTMDBSearchTextBadge = (item, tmdbType) => {
  if (tmdbType !== 'movie') return '';
  const year = getTMDBYear(item);
  return year > 0 ? String(year) : '';
};

const enrichTMDBItemWithDetail = async (item) => {
  const tmdbType = normalizeTMDBMediaType(item && item.tmdbType);
  const tmdbId = normalizeTMDBID(item && item.tmdbId);
  if (!tmdbType || tmdbId <= 0) return item;

  try {
    const detail = await fetchTMDBDetailCached({ type: tmdbType, id: tmdbId });
    if (!detail || typeof detail !== 'object') return item;
    const poster = getTMDBPosterPath(detail);
    const backdrop = getTMDBBackdropPath(detail);
    const title = sanitizeDisplayTitle(getTMDBDetailTitle(detail, tmdbType) || item.title);
    const year = getTMDBYear(detail);
    const textBadge = buildTMDBDetailTextBadge(detail, tmdbType);

    return {
      ...item,
      title: title || item.title,
      poster,
      backdrop: backdrop,
      year: year > 0 ? year : 0,
      textBadge,
    };
  } catch (_error) {
    return item;
  }
};

const enrichTMDBItemsWithDetail = async (items) => {
  const list = Array.isArray(items) ? items : [];
  if (!list.length) return list;
  const enriched = await Promise.all(list.map((item) => enrichTMDBItemWithDetail(item)));
  return enriched;
};

const normalizeInt = (value) => {
  const n = Number.isFinite(Number(value)) ? Math.floor(Number(value)) : 0;
  return n > 0 ? n : 0;
};

const buildDisplayedContentKey = (title, fallback = '') => {
  const normalizedTitle = sanitizeDisplayTitle(title);
  if (normalizedTitle) return normalizedTitle;
  return sanitizeDisplayTitle(fallback);
};

const mergeDisplayedSiteItem = (item, tmdbMatch = null, { canAggregate = false } = {}) => {
  const source = item && typeof item === 'object' ? item : {};
  const tmdbItem = tmdbMatch && typeof tmdbMatch === 'object' ? tmdbMatch : null;
  const rawOriginalTitle = normalizeString(source.rawTitle || source.title);
  const rawTitle = sanitizeDisplayTitle(rawOriginalTitle);
  const tmdbTitle = sanitizeDisplayTitle(tmdbItem && tmdbItem.title);
  const aggregateContentKey = normalizeString(source.aggregateContentKey);
  const contentKey = tmdbTitle
    || (canAggregate ? (aggregateContentKey || rawOriginalTitle || rawTitle) : '')
    || normalizeString(source.contentKey)
    || rawOriginalTitle
    || rawTitle;
  const nextTitle = tmdbTitle || rawOriginalTitle || rawTitle;
  return {
    ...source,
    title: nextTitle,
    contentKey,
    tmdbId: 0,
    tmdbType: '',
  };
};

const compareSeasonEpisode = (left, right) => {
  if (!left && !right) return 0;
  if (left && !right) return 1;
  if (!left && right) return -1;
  const leftSeason = normalizeInt(left && left.season);
  const rightSeason = normalizeInt(right && right.season);
  if (leftSeason !== rightSeason) return leftSeason > rightSeason ? 1 : -1;
  const leftEpisode = normalizeInt(left && left.episode);
  const rightEpisode = normalizeInt(right && right.episode);
  if (leftEpisode !== rightEpisode) return leftEpisode > rightEpisode ? 1 : -1;
  return 0;
};

const parseSeasonedProgress = (rawText) => {
  const raw = typeof rawText === 'string' ? rawText : '';
  const text = raw.trim();
  if (!text) return null;
  const cleaned = preCleanForRules(text) || text;

  const mCn = cleaned.match(/第\s*(\d{1,2})\s*季\s*第\s*(\d{1,4})\s*(?:集|话|回|期)/i);
  if (mCn && mCn[1] && mCn[2]) {
    const season = Number.parseInt(String(mCn[1]), 10) || 0;
    const episode = Number.parseInt(String(mCn[2]), 10) || 0;
    if (season > 0 && episode > 0) return { season, episode };
  }

  const mSe = cleaned.match(/\bS\s*(\d{1,2})\s*E\s*(\d{1,4})\b/i);
  if (mSe && mSe[1] && mSe[2]) {
    const season = Number.parseInt(String(mSe[1]), 10) || 0;
    const episode = Number.parseInt(String(mSe[2]), 10) || 0;
    if (season > 0 && episode > 0) return { season, episode };
  }

  return null;
};

const hasAnySeasonMarker = (rawText) => {
  const raw = typeof rawText === 'string' ? rawText : '';
  const text = raw.trim();
  if (!text) return false;
  const cleaned = preCleanForRules(text) || text;
  return /(?:第\s*\d{1,2}\s*季|\bS\s*\d{1,2}\b|\bSeason\s*\d{1,2}\b)/i.test(cleaned);
};

const parseUnseasonedUpdateEpisode = (rawText) => {
  const raw = typeof rawText === 'string' ? rawText : '';
  const text = raw.trim();
  if (!text) return 0;
  const cleaned = preCleanForRules(text) || text;

  const mUp = cleaned.match(/(?:更新至|更至|更)\s*(\d{1,4})\s*(?:集|话|回|期)/i);
  if (mUp && mUp[1]) {
    const episode = Number.parseInt(String(mUp[1]), 10) || 0;
    if (episode > 0 && episode <= 2000) return episode;
  }

  const mRange = cleaned.match(/(\d{1,4})\s*\/\s*(\d{1,4})/i);
  if (mRange && mRange[1] && mRange[2]) {
    const left = Number.parseInt(String(mRange[1]), 10) || 0;
    const right = Number.parseInt(String(mRange[2]), 10) || 0;
    if (left > 0 && right > 0 && right >= left && left <= 2000 && right <= 2000) return left;
  }

  return 0;
};

export const buildSearchGroupKey = (title, compiledRules, { queryTrailingDigits = '', contentKind = 'tv' } = {}) => {
  return normalizeForGroupKey(
    buildCanonicalSearchTitle(title, compiledRules, { queryTrailingDigits, contentKind }) || sanitizeDisplayTitle(title)
  );
};

const formatAggregateTVRemark = ({ tmdbMatch, groupTitle, sources } = {}) => {
  const tmdbItem = tmdbMatch && typeof tmdbMatch === 'object' ? tmdbMatch : null;
  const tmdbBaseRemark = tmdbItem ? normalizeString(tmdbItem.textBadge) : '';
  const tmdbSeasoned = parseSeasonedProgress(tmdbBaseRemark);
  const list = Array.isArray(sources) ? sources : [];
  let bestSeasoned = null;
  let bestUnseasoned = 0;

  list.forEach((source) => {
    const joined = `${source && source.textBadge ? String(source.textBadge) : ''} ${source && source.title ? String(source.title) : ''}`.trim();
    if (!joined) return;
    const seasoned = parseSeasonedProgress(joined);
    if (seasoned) {
      if (!bestSeasoned || compareSeasonEpisode(seasoned, bestSeasoned) > 0) bestSeasoned = seasoned;
      return;
    }
    if (hasAnySeasonMarker(joined)) return;
    const episode = parseUnseasonedUpdateEpisode(joined);
    if (episode > bestUnseasoned) bestUnseasoned = episode;
  });

  if (bestSeasoned) {
    if (tmdbSeasoned && compareSeasonEpisode(bestSeasoned, tmdbSeasoned) < 0) return tmdbBaseRemark;
    return `更新至第${bestSeasoned.season}季第${bestSeasoned.episode}集`;
  }
  if (tmdbSeasoned) return tmdbBaseRemark || '';
  if (bestUnseasoned > 0) return `更新至第${bestUnseasoned}集`;
  return tmdbBaseRemark || '';
};

const mapTmdbItems = (data, settings, computeMatchScore, { rawQuery = '', contentKind = 'tv' } = {}) => {
  const list = getTMDBSearchResults(data);
  const seen = new Set();
  const canonicalQuery = resolveCanonicalSearchVariants(rawQuery, { contentKind }).canonical;
  return list
    .map((item, index) => {
      const title = sanitizeDisplayTitle(getTMDBSearchTitle(item));
      const tmdbType = normalizeTMDBMediaType(item && item.media_type);
      const tmdbId = normalizeTMDBID(item && item.id);
      if (!title || !tmdbType || tmdbId <= 0) return null;
      const groupKey = buildSearchGroupKey(title, [], { contentKind: tmdbType });
      const dedupeKey = `${tmdbType}::${groupKey}`;
      if (dedupeKey && seen.has(dedupeKey)) return null;
      seen.add(dedupeKey);
      const year = getTMDBYear(item);
      const contentKey = buildDisplayedContentKey(title, groupKey);
      const textBadge = buildTMDBSearchTextBadge(item, tmdbType);
      return {
        id: `tmdb:${tmdbType}:${tmdbId}`,
        contentKey,
        sourceKind: 'tmdb',
        siteKey: 'tmdb',
        siteName: 'TMDB',
        spiderApi: '',
        siteDetail: String(tmdbId),
        title,
        poster: buildTmdbPoster(item),
        backdrop: getTMDBBackdropPath(item),
        year: year > 0 ? year : 0,
        textBadge,
        siteLabel: tmdbType === 'movie' ? '电影' : '剧集',
        score: Math.max(
          computeMatchScore(title),
          scoreTMDBSearchHitByQuery(title, rawQuery, canonicalQuery, { contentKind: tmdbType === 'movie' ? 'movie' : contentKind })
        ),
        seq: index,
        groupKey,
        tmdbId,
        tmdbType,
        tmdbRank: index + 1,
      };
    })
    .filter(Boolean);
};

const normalizeSiteSearchList = (data) => {
  const list = data && Array.isArray(data.list) ? data.list : [];
  return list
    .map((item) => {
      const rawTitle = normalizeString(item && (item.vod_name != null ? String(item.vod_name) : item.name != null ? String(item.name) : ''));
      return {
        id: normalizeString(item && (item.vod_id != null ? String(item.vod_id) : item.id != null ? String(item.id) : '')),
        rawTitle,
        title: rawTitle,
        poster: normalizeString(item && (item.vod_pic != null ? String(item.vod_pic) : item.pic != null ? String(item.pic) : '')),
        remark: normalizeString(item && (item.vod_remarks != null ? String(item.vod_remarks) : item.remark != null ? String(item.remark) : '')),
      };
    })
    .filter((item) => item.id && item.rawTitle);
};

export async function loadSearchConfig(baseBootstrap = {}) {
  const bootstrap = await fetchBootstrap('search');
  const mergedSettings = {
    ...(baseBootstrap && baseBootstrap.settings ? baseBootstrap.settings : {}),
    ...(bootstrap && bootstrap.settings ? bootstrap.settings : {}),
  };
  const searchDisplayMode = normalizeString(mergedSettings.searchDisplayMode);
  return {
    bootstrap,
    settings: mergedSettings,
    searchDisplayMode:
      searchDisplayMode === 'tmdb' || searchDisplayMode === 'both' || searchDisplayMode === 'sites'
        ? searchDisplayMode
        : 'sites',
    searchThreadCount: Number.isFinite(Number(mergedSettings.searchThreadCount))
      ? Math.max(1, Math.min(50, Math.floor(Number(mergedSettings.searchThreadCount))))
      : 5,
    searchCoverSite: normalizeString(mergedSettings.searchCoverSite),
    siteOrderMap: buildSiteOrderMap(mergedSettings),
    aggregateRules: compileCleanRules(mergedSettings.magicAggregateRegexRules),
    catpawrunnerApiBase: normalizeString(mergedSettings.catpawrunnerApiBase),
    doubanDataProxy: normalizeString(mergedSettings.doubanDataProxy),
    doubanDataCustom: normalizeString(mergedSettings.doubanDataCustom),
    doubanImgProxy: normalizeString(mergedSettings.doubanImgProxy),
    doubanImgCustom: normalizeString(mergedSettings.doubanImgCustom),
    tmdbImageProxyBase: normalizeString(mergedSettings.tmdbImageProxyBase),
  };
}

export async function fetchSearchHistory() {
  const list = await requestJson(SEARCH_HISTORY_ENDPOINT, { method: 'GET', credentials: 'same-origin' });
  return Array.isArray(list) ? list.map((item) => normalizeString(item)).filter(Boolean) : [];
}

export async function saveSearchHistory(keyword) {
  const safeKeyword = normalizeString(keyword);
  if (!safeKeyword) return [];
  const list = await requestJson(SEARCH_HISTORY_ENDPOINT, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ keyword: safeKeyword }),
  });
  return Array.isArray(list) ? list.map((item) => normalizeString(item)).filter(Boolean) : [];
}

export async function clearSearchHistory() {
  const list = await requestJson(SEARCH_HISTORY_ENDPOINT, { method: 'DELETE', credentials: 'same-origin' });
  return Array.isArray(list) ? list.map((item) => normalizeString(item)).filter(Boolean) : [];
}

export async function loadSearchableSites() {
  const payload = await requestJson(USER_SITES_ENDPOINT, { method: 'GET', credentials: 'same-origin' });
  const sites = payload && Array.isArray(payload.sites) ? payload.sites : [];
  return sites.filter((site) => {
    if (!site) return false;
    if (site.enabled === false || site.search === false) return false;
    if (!normalizeString(site.api) || !normalizeString(site.key)) return false;
    return !isConfigCenterSite(site);
  });
}

const blockedSiteKeyCache = new Map();
const blockedMatchIndexCache = new Map();

export const clearBlockedMatchCaches = () => {
  blockedSiteKeyCache.clear();
  blockedMatchIndexCache.clear();
};

export const buildSmartMatchBlockKeyword = (title, aggregateRules = []) => {
  const raw = normalizeString(title);
  if (!raw) return '';
  const compiledRules = compileCleanRules(aggregateRules);
  const preCleaned = preCleanForRules(raw) || raw;
  const cleaned = sanitizeDisplayTitle(applyCleanRules(preCleaned, compiledRules) || preCleaned);
  return normalizeString(cleaned || raw);
};

const cloneBlockedMatchIndex = (index) => {
  const source = index && typeof index === 'object' ? index : {};
  const out = {};
  Object.entries(source).forEach(([key, entry]) => {
    const item = entry && typeof entry === 'object' ? entry : null;
    if (!item) return;
    out[key] = {
      blockAll: !!item.blockAll,
      panFlags: Array.isArray(item.panFlags) ? item.panFlags.slice() : [],
    };
  });
  return out;
};

const buildBlockedMatchIndex = (items) => {
  const list = Array.isArray(items) ? items : [];
  const out = {};
  list.forEach((item) => {
    const siteKey = normalizeString(item && item.siteKey);
    const siteDetail = normalizeString(item && item.siteDetail);
    if (!siteKey || !siteDetail) return;
    const key = `${siteKey}::${siteDetail}`;
    const source = normalizeString(item && item.source);
    const panFlag = normalizeString(item && item.panFlag);
    const entry = out[key] || { blockAll: false, panFlags: [] };
    if (!source || source === 'search') {
      entry.blockAll = true;
      entry.panFlags = [];
      out[key] = entry;
      return;
    }
    if (source === 'play' && panFlag && !entry.blockAll && !entry.panFlags.includes(panFlag)) {
      entry.panFlags.push(panFlag);
    }
    out[key] = entry;
  });
  return out;
};

const fetchBlockedMatchItemsByKeyword = async (keyword) => {
  const safeKeyword = normalizeString(keyword);
  if (!safeKeyword) return [];
  const cacheKey = safeKeyword.toLowerCase();
  if (blockedMatchIndexCache.has(cacheKey)) {
    const cached = blockedMatchIndexCache.get(cacheKey);
    return Array.isArray(cached && cached.items) ? cached.items.slice() : [];
  }
  try {
    const payload = await requestJson(
      `${SMART_MATCHBLOCK_ITEMS_ENDPOINT}?keyword=${encodeURIComponent(safeKeyword)}`,
      { method: 'GET', credentials: 'same-origin' }
    );
    const items = payload && Array.isArray(payload.items) ? payload.items : [];
    blockedMatchIndexCache.set(cacheKey, { items: items.slice() });
    return items.slice();
  } catch (_error) {
    return [];
  }
};

export const fetchBlockedMatchIndex = async (keyword, aggregateRules = []) => {
  const rawKeyword = normalizeString(keyword);
  if (!rawKeyword) return {};
  const derivedKeyword = buildSmartMatchBlockKeyword(rawKeyword, aggregateRules);
  const keywordList = Array.from(new Set([rawKeyword, derivedKeyword].map(normalizeString).filter(Boolean)));
  const cacheKey = keywordList.map((item) => item.toLowerCase()).join('||');
  if (blockedMatchIndexCache.has(cacheKey)) {
    const cached = blockedMatchIndexCache.get(cacheKey);
    return cloneBlockedMatchIndex(cached && cached.index);
  }
  const merged = [];
  const seen = new Set();
  for (let i = 0; i < keywordList.length; i += 1) {
    const list = await fetchBlockedMatchItemsByKeyword(keywordList[i]).catch(() => []);
    (Array.isArray(list) ? list : []).forEach((item) => {
      const dedupeKey = [
        normalizeString(item && item.siteKey),
        normalizeString(item && item.siteDetail),
        normalizeString(item && item.panFlag),
        normalizeString(item && item.source),
      ].join('::');
      if (!dedupeKey || seen.has(dedupeKey)) return;
      seen.add(dedupeKey);
      merged.push(item);
    });
  }
  const index = buildBlockedMatchIndex(merged);
  blockedMatchIndexCache.set(cacheKey, { index: cloneBlockedMatchIndex(index) });
  return index;
};

export const addSmartMatchBlockItem = async (payload) => {
  const body = payload && typeof payload === 'object' ? payload : {};
  const response = await requestJson('/api/smart/matchblock/add', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'same-origin',
    body: JSON.stringify(body),
  });
  clearBlockedMatchCaches();
  return response;
};

export const deleteSmartMatchBlockItem = async (payload) => {
  const body = payload && typeof payload === 'object' ? payload : {};
  const response = await requestJson('/api/smart/matchblock/delete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'same-origin',
    body: JSON.stringify(body),
  });
  clearBlockedMatchCaches();
  return response;
};

export const fetchBlockedSiteKeys = async (keyword) => {
  const safeKeyword = normalizeString(keyword);
  if (!safeKeyword) return new Set();
  if (blockedSiteKeyCache.has(safeKeyword)) {
    const cached = blockedSiteKeyCache.get(safeKeyword);
    return cached instanceof Set ? new Set(cached) : new Set();
  }
  try {
    const payload = await requestJson(
      `${SMART_MATCHBLOCK_ITEMS_ENDPOINT}?keyword=${encodeURIComponent(safeKeyword)}`,
      { method: 'GET', credentials: 'same-origin' }
    );
    const items = payload && Array.isArray(payload.items) ? payload.items : [];
    const next = new Set(
      items
        .map((item) => normalizeString(item && item.siteKey))
        .filter(Boolean)
    );
    blockedSiteKeyCache.set(safeKeyword, new Set(next));
    return next;
  } catch (_error) {
    return new Set();
  }
};

export async function streamSearch(query, config, { onUpdate, blockedSiteKeys = [], contentKind = 'tv' } = {}) {
  const safeQuery = normalizeString(query);
  if (!safeQuery) {
    const emptyState = {
      displayMode: config.searchDisplayMode,
      tmdbItems: [],
      siteItems: [],
      displayedItems: [],
      pinTmdbFirst: false,
      siteTotal: 0,
      progressDone: 0,
      progressTotal: 0,
    };
    if (typeof onUpdate === 'function') onUpdate(emptyState);
    return emptyState;
  }

  const computeMatchScore = computeMatchScoreFactory(safeQuery);
  const queryTrailingDigitsMatch = safeQuery.match(/(\d+)\s*$/);
  const queryTrailingDigits = queryTrailingDigitsMatch ? String(queryTrailingDigitsMatch[1] || '') : '';
  const blockedSiteKeySet = new Set(
    (Array.isArray(blockedSiteKeys) ? blockedSiteKeys : [])
      .map((item) => normalizeString(item))
      .filter(Boolean)
  );
  const output = {
    displayMode: config.searchDisplayMode,
    tmdbItems: [],
    siteItems: [],
    displayedItems: [],
    pinTmdbFirst: false,
    siteTotal: 0,
  };
  let progressDone = 0;
  let progressTotal = config.searchDisplayMode === 'tmdb' ? 1 : 0;

  const publish = () => {
    if (typeof onUpdate !== 'function') return;
    onUpdate(snapshotSearchState(output, { progressDone, progressTotal }));
  };

  const tmdbPromise =
    config.searchDisplayMode === 'tmdb' || config.searchDisplayMode === 'both'
      ? (async () => {
          try {
            if (config.searchDisplayMode === 'both') progressTotal += 1;
            const { payload: tmdbPayload } = await requestTMDBSearchWithTrailingDigitsFallback(safeQuery);
            output.tmdbItems = mapTmdbItems(tmdbPayload, config.settings, computeMatchScore, {
              rawQuery: safeQuery,
              contentKind,
            });
            output.pinTmdbFirst = output.tmdbItems.some((item) => item.score >= 1000);
            progressDone += 1;
            publish();
            output.tmdbItems = await enrichTMDBItemsWithDetail(output.tmdbItems);
            output.pinTmdbFirst = output.tmdbItems.some((item) => item.score >= 1000);
            publish();
          } catch (error) {
            if (config.searchDisplayMode === 'tmdb') throw error;
            output.tmdbItems = [];
            output.pinTmdbFirst = false;
            progressDone += 1;
            publish();
          }
        })()
      : Promise.resolve();

  const sitePromise =
    config.searchDisplayMode === 'sites' || config.searchDisplayMode === 'both'
      ? (async () => {
          if (!config.catpawrunnerApiBase) throw new Error('catpawrunner 接口未配置');
          const sites = (await loadSearchableSites()).filter(
            (site) => !blockedSiteKeySet.has(normalizeString(site && site.key))
          );
          output.siteTotal = sites.length;
          progressTotal += sites.length;
          publish();
          const siteItems = [];
          await Promise.all(
            sites.map(async (site, index) => {
              try {
                const payload = await requestCatSpider({
                  apiBase: config.catpawrunnerApiBase,
                  action: 'search',
                  spiderApi: site.api,
                  payload: { wd: safeQuery, page: 1 },
                  timeoutMs: 10000,
                });
                const list = normalizeSiteSearchList(payload);
                list.slice(0, 12).forEach((item, innerIndex) => {
                  const rawTitle = normalizeString(item.rawTitle || item.title);
                  const aggregateContentKey = buildCanonicalSearchTitle(rawTitle, config.aggregateRules, {
                    queryTrailingDigits,
                    contentKind,
                  }) || rawTitle;
                  const variantMeta = (() => {
                    const titleMeta = extractSeriesVariantMetaFromText(rawTitle);
                    if (normalizeInt(titleMeta && titleMeta.index) > 0) return titleMeta;
                    return extractSeriesVariantMetaFromText(item.remark);
                  })();
                  const seasonHint = normalizeInt(variantMeta && variantMeta.index);
                  const seasonHintLabel = normalizeString(variantMeta && variantMeta.kind) === 'yearbang'
                    ? `年番${Math.floor(seasonHint) || 1}`
                    : (seasonHint > 0 ? `第${Math.floor(seasonHint)}季` : '');
                  siteItems.push({
                    id: `${normalizeString(site.key)}:${item.id}`,
                    sourceKind: 'site',
                    siteKey: normalizeString(site.key),
                    siteName: normalizeString(site.name) || normalizeString(site.key),
                    spiderApi: normalizeString(site.api),
                    siteDetail: item.id,
                    title: rawTitle,
                    rawTitle,
                    contentKey: rawTitle,
                    aggregateContentKey,
                    poster: item.poster,
                    textBadge: item.remark,
                    siteLabel: normalizeString(site.name) || normalizeString(site.key),
                    score: computeMatchScore(rawTitle),
                    seq: index * 100 + innerIndex,
                    groupKey: buildSearchGroupKey(rawTitle, config.aggregateRules, {
                      queryTrailingDigits,
                      contentKind,
                    }),
                    seasonHint,
                    seasonHintLabel,
                  });
                });
              } catch (_error) {
              } finally {
                progressDone += 1;
                output.siteItems = siteItems.slice();
                publish();
              }
            })
          );
          output.siteItems = siteItems;
        })()
      : Promise.resolve();

  await Promise.all([tmdbPromise, sitePromise]);

  const finalState = snapshotSearchState(output, { progressDone, progressTotal });
  if (typeof onUpdate === 'function') onUpdate(finalState);
  return finalState;
}

export const mergeSearchSnapshotsBySiteIdentity = (...snapshots) => {
  const list = snapshots.filter((item) => item && typeof item === 'object');
  const base = list[0] || null;
  if (!base) return null;
  const seenTmdb = new Set();
  const tmdbItems = [];
  const seenSite = new Set();
  const siteItems = [];
  list.forEach((snapshot) => {
    (Array.isArray(snapshot.tmdbItems) ? snapshot.tmdbItems : []).forEach((item) => {
      const key = `${normalizeString(item && item.tmdbType)}::${normalizeTMDBID(item && item.tmdbId)}`;
      if (!key || seenTmdb.has(key)) return;
      seenTmdb.add(key);
      tmdbItems.push(item);
    });
    (Array.isArray(snapshot.siteItems) ? snapshot.siteItems : []).forEach((item) => {
      const key = [
        normalizeString(item && item.siteKey),
        normalizeString(item && item.spiderApi),
        normalizeString(item && item.siteDetail),
      ].join('::');
      if (!key || seenSite.has(key)) return;
      seenSite.add(key);
      siteItems.push(item);
    });
  });
  return {
    ...base,
    tmdbItems,
    siteItems,
    displayedItems: [],
    pinTmdbFirst: list.some((item) => !!(item && item.pinTmdbFirst)),
    siteTotal: siteItems.length,
    progressDone: list.reduce((sum, item) => sum + (Number.isFinite(Number(item && item.progressDone)) ? Number(item.progressDone) : 0), 0),
    progressTotal: list.reduce((sum, item) => sum + (Number.isFinite(Number(item && item.progressTotal)) ? Number(item.progressTotal) : 0), 0),
  };
};

export function buildDisplayedResults(searchState, config, { rawListMode = false } = {}) {
  const tmdbItems = Array.isArray(searchState && searchState.tmdbItems) ? searchState.tmdbItems : [];
  const siteItems = Array.isArray(searchState && searchState.siteItems) ? searchState.siteItems : [];
  const displayMode = normalizeString(searchState && searchState.displayMode) || normalizeString(config && config.searchDisplayMode);
  const tmdbByGroup = buildTMDBGroupMap(tmdbItems);

  const normalizedTmdbItems = tmdbItems.map((item) => ({
    ...(item || {}),
    contentKey: buildDisplayedContentKey(item && item.title, item && item.contentKey),
  }));
  const siteGroupCounts = new Map();
  siteItems.forEach((item) => {
    const groupKey = normalizeString(item && item.groupKey);
    if (!groupKey) return;
    siteGroupCounts.set(groupKey, (siteGroupCounts.get(groupKey) || 0) + 1);
  });
  let siteDisplay = siteItems.map((item) => {
    const groupKey = normalizeString(item && item.groupKey);
    const tmdbMatch = groupKey ? tmdbByGroup.get(groupKey) || null : null;
    const canAggregate = !!tmdbMatch || (groupKey && (siteGroupCounts.get(groupKey) || 0) > 1);
    return mergeDisplayedSiteItem(item, tmdbMatch, { canAggregate });
  });

  if (!rawListMode) {
    const groups = buildSiteDisplayGroups(siteItems, {
      tmdbByGroup,
      displayMode,
      context: config,
    });

    applyTMDBAggregateIntoCards(tmdbByGroup, groups);

    siteDisplay = Array.from(groups.entries()).flatMap(([groupKey, group]) => {
      const representative = group.representative;
      if (!representative) return [];
      const itemCount = Array.isArray(group.items) ? group.items.length : 0;
      const siteCount = group.siteKeys.size;
      const tmdbMatch = tmdbByGroup.get(groupKey) || null;
      const aggregateRemark = formatAggregateTVRemark({
        tmdbMatch,
        groupTitle: (tmdbMatch && tmdbMatch.title) || group.title || (representative && representative.title) || '',
        sources: group.items,
      });

      if (tmdbMatch) return [];

      if (itemCount <= 1) {
        return [
          mergeDisplayedSiteItem({
            ...representative,
            groupKey,
            textBadge: aggregateRemark || representative.textBadge,
            siteLabel: normalizeString(representative && representative.siteLabel)
              || normalizeString(representative && representative.siteName),
          }, null, { canAggregate: false }),
        ];
      }

      return [
        mergeDisplayedSiteItem({
          ...representative,
          id: `aggregate:${groupKey}`,
          groupKey,
          textBadge: aggregateRemark || representative.textBadge,
          siteLabel: '聚合',
          aggregateSourceCount: siteCount,
          aggregateItems: group.items.slice(),
          aggregateKind: 'site',
        }, null, { canAggregate: true }),
      ];
    });
  }

  return [...normalizedTmdbItems, ...siteDisplay].sort((left, right) =>
    compareDisplayedResults(left, right, {
      siteOrderMap: config.siteOrderMap,
      pinTmdbFirst: !!(searchState && searchState.pinTmdbFirst),
    })
  );
}
