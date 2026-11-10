// Search algorithms adapted from meowfilm-server 934a03e5aeda7edbcef0cfb037734fed93bfe881.
// Native JSON hosts own I/O, sessions, pagination and cancellation. See README.md.
(() => {
'use strict';

const normalizeString = (value) => (typeof value === 'string' ? value.trim() : '');

const normalizeTMDBInt = (value) => {
  const num = Number(value);
  return Number.isFinite(num) ? Math.trunc(num) : 0;
};

const normalizePositiveInteger = (value) => (
  typeof value === 'number' && Number.isFinite(value) ? Math.trunc(value) : 0
);

const normalizeTMDBMediaType = (value) => {
  const raw = normalizeString(value).toLowerCase();
  if (raw === 'movie' || raw === 'tv') return raw;
  return '';
};

const normalizeTMDBID = (value) => {
  const id = normalizePositiveInteger(value);
  return id > 0 ? id : 0;
};

const getTMDBSearchResults = (payload) => (
  payload && Array.isArray(payload.results) ? payload.results : []
);

const getTMDBSearchTitle = (item) => {
  const target = item && typeof item === 'object' ? item : null;
  const tmdbType = normalizeTMDBMediaType(target && target.media_type);
  if (tmdbType === 'movie') {
    return normalizeString(target && target.title) || normalizeString(target && target.original_title);
  }
  if (tmdbType === 'tv') {
    return normalizeString(target && target.name) || normalizeString(target && target.original_name);
  }
  return '';
};

const getTMDBDetailTitle = (detail, tmdbType = '') => {
  const target = detail && typeof detail === 'object' ? detail : null;
  const normalizedType = normalizeTMDBMediaType(tmdbType);
  if (normalizedType === 'movie') {
    return normalizeString(target && target.title) || normalizeString(target && target.original_title);
  }
  if (normalizedType === 'tv') {
    return normalizeString(target && target.name) || normalizeString(target && target.original_name);
  }
  return '';
};

const getTMDBReleaseDateText = (payload) => {
  const target = payload && typeof payload === 'object' ? payload : null;
  return normalizeString(target && target.release_date) || normalizeString(target && target.first_air_date);
};

const getTMDBYear = (payload) => {
  const raw = getTMDBReleaseDateText(payload);
  const match = raw.match(/^(\d{4})/);
  return match && match[1] ? normalizeTMDBInt(match[1]) : 0;
};

const getTMDBPosterPath = (payload) => {
  const target = payload && typeof payload === 'object' ? payload : null;
  return normalizeString(target && target.poster_path);
};

const getTMDBBackdropPath = (payload) => {
  const target = payload && typeof payload === 'object' ? payload : null;
  return normalizeString(target && target.backdrop_path);
};

const getTMDBOrdinarySeasons = (detail) => {
  const target = detail && typeof detail === 'object' ? detail : null;
  const seasons = Array.isArray(target && target.seasons) ? target.seasons : [];
  return seasons
    .map((item) => ({
      season: normalizeTMDBInt(item && item.season_number),
      episodes: normalizeTMDBInt(item && item.episode_count),
    }))
    .filter((item) => item.season > 0 && item.episodes > 0)
    .sort((left, right) => left.season - right.season);
};

const getTMDBEpisodeToAir = (detail, key) => {
  const target = detail && typeof detail === 'object' ? detail : null;
  const episode = target && typeof target[key] === 'object' ? target[key] : null;
  if (!episode) return null;
  return {
    seasonNumber: normalizeTMDBInt(episode.season_number),
    episodeNumber: normalizeTMDBInt(episode.episode_number),
    airDate: normalizeString(episode.air_date),
  };
};

const getTMDBNextEpisodeToAir = (detail) => getTMDBEpisodeToAir(detail, 'next_episode_to_air');

const getTMDBStatus = (detail) => normalizeString(detail && detail.status);

const getTodayDateText = () => {
  const now = new Date();
  const year = now.getFullYear();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${year}-${month}-${day}`;
};

const buildTMDBDetailTextBadge = (detail, tmdbType = '', today = '') => {
  const type = normalizeTMDBMediaType(tmdbType);
  if (type === 'movie') {
    const year = getTMDBYear(detail);
    return year > 0 ? String(year) : '';
  }
  if (type !== 'tv') return '';

  const ordinarySeasons = getTMDBOrdinarySeasons(detail);
  const seasonCount = ordinarySeasons.length;
  const totalEpisodes = ordinarySeasons.reduce((sum, item) => sum + normalizeTMDBInt(item && item.episodes), 0);
  const normalizedStatus = getTMDBStatus(detail);
  const status = normalizedStatus.toLowerCase();
  const isContinuing = status === 'continuing' || status === 'returning series' || status === 'in production';
  const nextEpisode = getTMDBNextEpisodeToAir(detail);
  const nextSeasonNumber = normalizeTMDBInt(nextEpisode && nextEpisode.seasonNumber);
  const nextEpisodeNumber = normalizeTMDBInt(nextEpisode && nextEpisode.episodeNumber);
  const nextAirDate = normalizeString(nextEpisode && nextEpisode.airDate);

  if (status === 'ended') {
    if (totalEpisodes <= 0) return '';
    return seasonCount > 1 ? `共${seasonCount}季${totalEpisodes}集` : `共${totalEpisodes}集`;
  }

  const currentEpisodeNumber = nextAirDate && nextAirDate <= (today || getTodayDateText())
    ? nextEpisodeNumber
    : Math.max(0, nextEpisodeNumber - 1);
  if (currentEpisodeNumber > 0) {
    if (nextSeasonNumber > 1 && seasonCount > 1) {
      return `更新至第${nextSeasonNumber}季第${currentEpisodeNumber}集`;
    }
    return `更新至第${currentEpisodeNumber}集`;
  }

  if (isContinuing) {
    const lastSeason = seasonCount > 0 ? ordinarySeasons[seasonCount - 1] : null;
    const lastSeasonNumber = normalizeTMDBInt(lastSeason && lastSeason.season);
    const lastSeasonEpisodeCount = normalizeTMDBInt(lastSeason && lastSeason.episodes);
    if (lastSeasonEpisodeCount > 0) {
      if (seasonCount > 1 && lastSeasonNumber > 1) {
        return `更新至第${lastSeasonNumber}季第${lastSeasonEpisodeCount}集`;
      }
      return `更新至第${Math.max(totalEpisodes, lastSeasonEpisodeCount)}集`;
    }
  }

  return normalizedStatus ? '持续更新' : '';
};

const buildTrailingDigitsFallbackQuery = (query) => {
  const raw = normalizeString(query);
  if (!raw) return '';
  const next = raw.replace(/\s*[0-9]{1,2}\s*$/g, ' ').replace(/\s+/g, ' ').trim();
  if (!next || next === raw) return '';
  return next;
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

// Unicode 17.0 (Node 24.21.0): Extended_Pictographic | Emoji_Presentation | FE0E–FE0F, without ICU.
const stripEmojiSymbols = (value) =>
  String(value || '').replace(/[\u00A9\u00AE\u203C\u2049\u2122\u2139\u2194-\u2199\u21A9-\u21AA\u231A-\u231B\u2328\u23CF\u23E9-\u23F3\u23F8-\u23FA\u24C2\u25AA-\u25AB\u25B6\u25C0\u25FB-\u25FE\u2600-\u2604\u260E\u2611\u2614-\u2615\u2618\u261D\u2620\u2622-\u2623\u2626\u262A\u262E-\u262F\u2638-\u263A\u2640\u2642\u2648-\u2653\u265F-\u2660\u2663\u2665-\u2666\u2668\u267B\u267E-\u267F\u2692-\u2697\u2699\u269B-\u269C\u26A0-\u26A1\u26A7\u26AA-\u26AB\u26B0-\u26B1\u26BD-\u26BE\u26C4-\u26C5\u26C8\u26CE-\u26CF\u26D1\u26D3-\u26D4\u26E9-\u26EA\u26F0-\u26F5\u26F7-\u26FA\u26FD\u2702\u2705\u2708-\u270D\u270F\u2712\u2714\u2716\u271D\u2721\u2728\u2733-\u2734\u2744\u2747\u274C\u274E\u2753-\u2755\u2757\u2763-\u2764\u2795-\u2797\u27A1\u27B0\u27BF\u2934-\u2935\u2B05-\u2B07\u2B1B-\u2B1C\u2B50\u2B55\u3030\u303D\u3297\u3299\uFE0E-\uFE0F\u{1F004}\u{1F02C}-\u{1F02F}\u{1F094}-\u{1F09F}\u{1F0AF}-\u{1F0B0}\u{1F0C0}\u{1F0CF}-\u{1F0D0}\u{1F0F6}-\u{1F0FF}\u{1F170}-\u{1F171}\u{1F17E}-\u{1F17F}\u{1F18E}\u{1F191}-\u{1F19A}\u{1F1AE}-\u{1F1FF}\u{1F201}-\u{1F20F}\u{1F21A}\u{1F22F}\u{1F232}-\u{1F23A}\u{1F23C}-\u{1F23F}\u{1F249}-\u{1F25F}\u{1F266}-\u{1F321}\u{1F324}-\u{1F393}\u{1F396}-\u{1F397}\u{1F399}-\u{1F39B}\u{1F39E}-\u{1F3F0}\u{1F3F3}-\u{1F3F5}\u{1F3F7}-\u{1F4FD}\u{1F4FF}-\u{1F53D}\u{1F549}-\u{1F54E}\u{1F550}-\u{1F567}\u{1F56F}-\u{1F570}\u{1F573}-\u{1F57A}\u{1F587}\u{1F58A}-\u{1F58D}\u{1F590}\u{1F595}-\u{1F596}\u{1F5A4}-\u{1F5A5}\u{1F5A8}\u{1F5B1}-\u{1F5B2}\u{1F5BC}\u{1F5C2}-\u{1F5C4}\u{1F5D1}-\u{1F5D3}\u{1F5DC}-\u{1F5DE}\u{1F5E1}\u{1F5E3}\u{1F5E8}\u{1F5EF}\u{1F5F3}\u{1F5FA}-\u{1F64F}\u{1F680}-\u{1F6C5}\u{1F6CB}-\u{1F6D2}\u{1F6D5}-\u{1F6E5}\u{1F6E9}\u{1F6EB}-\u{1F6F0}\u{1F6F3}-\u{1F6FF}\u{1F7DA}-\u{1F7FF}\u{1F80C}-\u{1F80F}\u{1F848}-\u{1F84F}\u{1F85A}-\u{1F85F}\u{1F888}-\u{1F88F}\u{1F8AE}-\u{1F8AF}\u{1F8BC}-\u{1F8BF}\u{1F8C2}-\u{1F8CF}\u{1F8D9}-\u{1F8FF}\u{1F90C}-\u{1F93A}\u{1F93C}-\u{1F945}\u{1F947}-\u{1F9FF}\u{1FA58}-\u{1FA5F}\u{1FA6E}-\u{1FAFF}\u{1FC00}-\u{1FFFD}]/gu, '');

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

const stripTrailingSeriesDigits = (value, { contentKind = 'tv' } = {}) => {
  const kind = normalizeString(contentKind).toLowerCase();
  const base = sanitizeDisplayTitle(value);
  if (!base || kind === 'movie') return base;
  return String(base)
    .replace(/\s*[0-9０-９]{1,2}\s*$/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
};

const stripSearchAliasMarkers = (value, { contentKind = 'tv' } = {}) => {
  const kind = normalizeString(contentKind).toLowerCase();
  const base = sanitizeDisplayTitle(value);
  if (!base) return '';
  if (kind === 'movie') return base;
  return stripTrailingSeriesDigits(stripSeriesVariantMarkers(base), { contentKind: kind || 'tv' }) || base;
};

const resolveCanonicalSearchVariants = (value, { contentKind = 'tv' } = {}) => {
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

const resolveDisplayedSiteGroupKey = (item, tmdbByGroup, displayMode) => {
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

const scoreTMDBSearchHitByQuery = (itemTitle, rawQuery, canonicalQuery, { contentKind = 'tv' } = {}) => {
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
  return compareSiteSequence(candidate, current);
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
  return compareSiteSequence(left, right);
};

const buildTmdbPoster = (item) => {
  return getTMDBPosterPath(item);
};

const buildTMDBSearchTextBadge = (item, tmdbType) => {
  if (tmdbType !== 'movie') return '';
  const year = getTMDBYear(item);
  return year > 0 ? String(year) : '';
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

const buildSearchGroupKey = (title, compiledRules, { queryTrailingDigits = '', contentKind = 'tv' } = {}) => {
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

const mapTmdbItems = (data, computeMatchScore, { rawQuery = '', contentKind = 'tv' } = {}) => {
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
      const detailId = item && (item.vod_id != null ? item.vod_id : item.id);
      const rawTitle = normalizeString(item && (item.vod_name != null ? String(item.vod_name) : item.name != null ? String(item.name) : ''));
      return {
        detailId,
        raw: item,
        rawTitle,
        title: rawTitle,
        poster: normalizeString(item && (item.vod_pic != null ? String(item.vod_pic) : item.pic != null ? String(item.pic) : '')),
        remark: normalizeString(item && (item.vod_remarks != null ? String(item.vod_remarks) : item.remark != null ? String(item.remark) : '')),
      };
    })
    .filter((item) => (typeof item.detailId === 'string'
      ? normalizeString(item.detailId).length > 0
      : Number.isSafeInteger(item.detailId)) && item.rawTitle);
};

// Sequence is a tuple, so a complete page never overlaps another site's range.
const compareSiteSequence = (left, right) => {
  if (left.sourceKind === 'site' && right.sourceKind === 'site') {
    if (left.siteOrder !== right.siteOrder) return left.siteOrder - right.siteOrder;
    if (left.sitePage !== right.sitePage) return left.sitePage - right.sitePage;
  }
  return left.seq - right.seq;
};

const siteItemIdentity = (item) => `site:${JSON.stringify([item.siteKey, item.detailId])}`;

const mapSitePage = ({ query, aggregateRules, site, page, payload }) => {
  const safeQuery = normalizeString(query);
  const computeMatchScore = computeMatchScoreFactory(safeQuery);
  const queryTrailingDigitsMatch = safeQuery.match(/(\d+)\s*$/);
  const queryTrailingDigits = queryTrailingDigitsMatch ? String(queryTrailingDigitsMatch[1] || '') : '';
  const compiledRules = compileCleanRules(aggregateRules);
  const siteKey = site.siteKey;
  const siteName = normalizeString(site.siteName) || siteKey;
  const items = normalizeSiteSearchList(payload).map((item, innerIndex) => {
    const rawTitle = normalizeString(item.rawTitle || item.title);
    const aggregateContentKey = buildCanonicalSearchTitle(rawTitle, compiledRules, {
      queryTrailingDigits,
    }) || rawTitle;
    const titleMeta = extractSeriesVariantMetaFromText(rawTitle);
    const variantMeta = normalizeInt(titleMeta && titleMeta.index) > 0
      ? titleMeta
      : extractSeriesVariantMetaFromText(item.remark);
    const seasonHint = normalizeInt(variantMeta && variantMeta.index);
    const seasonHintLabel = normalizeString(variantMeta && variantMeta.kind) === 'yearbang'
      ? `年番${Math.floor(seasonHint) || 1}`
      : (seasonHint > 0 ? `第${Math.floor(seasonHint)}季` : '');
    return {
      id: siteItemIdentity({ siteKey, detailId: item.detailId }),
      sourceKind: 'site',
      siteKey,
      siteId: site.siteId,
      installationId: site.installationId,
      generation: site.generation,
      siteName,
      siteDetail: normalizeString(String(item.detailId)),
      detailId: item.detailId,
      raw: item.raw,
      title: rawTitle,
      rawTitle,
      contentKey: rawTitle,
      aggregateContentKey,
      poster: item.poster,
      textBadge: item.remark,
      siteLabel: siteName,
      score: computeMatchScore(rawTitle),
      siteOrder: site.order,
      sitePage: page,
      seq: innerIndex,
      groupKey: buildSearchGroupKey(rawTitle, compiledRules, { queryTrailingDigits }),
      seasonHint,
      seasonHintLabel,
    };
  });
  const pageCountValues = [payload && payload.pagecount, payload && payload.pageCount]
    .filter((value) => typeof value === 'number' || typeof value === 'string')
    .map(Number);
  const pageCount = pageCountValues.find((value) => Number.isInteger(value) && value > 0) ?? null;
  const explicitPageCount = pageCount !== null;
  return { items, page, pageCount, hasMore: explicitPageCount ? page < pageCount : items.length > 0, explicitPageCount };
};

const mapTMDBSearch = ({ query, payload }) => {
  const safeQuery = normalizeString(query);
  const items = mapTmdbItems(payload, computeMatchScoreFactory(safeQuery), { rawQuery: safeQuery });
  return { items, pinTmdbFirst: items.some((item) => item.score >= 1000) };
};

const enrichTMDBDetail = ({ item, payload: detail, today }) => {
  const tmdbType = normalizeTMDBMediaType(item && item.tmdbType);
  const tmdbId = normalizeTMDBID(item && item.tmdbId);
  if (!tmdbType || tmdbId <= 0 || !detail || typeof detail !== 'object') return item;
  const title = sanitizeDisplayTitle(getTMDBDetailTitle(detail, tmdbType) || item.title);
  const year = getTMDBYear(detail);
  return {
    ...item,
    title: title || item.title,
    poster: getTMDBPosterPath(detail),
    backdrop: getTMDBBackdropPath(detail),
    year: year > 0 ? year : 0,
    textBadge: buildTMDBDetailTextBadge(detail, tmdbType, normalizeString(today)),
  };
};

const buildDisplay = ({ siteItems, tmdbItems, siteOrder, searchCoverSite, rawListMode, previousOrder }) => {
  const seenSite = new Set();
  const sites = (Array.isArray(siteItems) ? siteItems : []).filter((item) => {
    const identity = siteItemIdentity(item);
    if (seenSite.has(identity)) return false;
    seenSite.add(identity);
    return true;
  });
  // Aggregate the copies that are actually returned, without modifying cached TMDB data.
  const tmdb = (Array.isArray(tmdbItems) ? tmdbItems : []).map((item) => ({
    ...item,
    contentKey: buildDisplayedContentKey(item.title, item.contentKey),
  }));
  const tmdbByGroup = buildTMDBGroupMap(tmdb);
  const siteOrderMap = new Map();
  (Array.isArray(siteOrder) ? siteOrder : []).forEach((key, index) => {
    if (!siteOrderMap.has(key)) siteOrderMap.set(key, index);
  });
  const context = { siteOrderMap, searchCoverSite, pinTmdbFirst: tmdb.some((item) => item.score >= 1000) };
  const groups = buildSiteDisplayGroups(sites, { tmdbByGroup, displayMode: 'both', context });
  applyTMDBAggregateIntoCards(tmdbByGroup, groups);
  const siteGroupCounts = new Map();
  sites.forEach((item) => {
    const groupKey = normalizeString(item.groupKey);
    if (!groupKey) return;
    siteGroupCounts.set(groupKey, (siteGroupCounts.get(groupKey) || 0) + 1);
  });
  let siteDisplay = sites.map((item) => {
    const groupKey = normalizeString(item.groupKey);
    const tmdbMatch = groupKey ? tmdbByGroup.get(groupKey) || null : null;
    const canAggregate = !!tmdbMatch || (groupKey && (siteGroupCounts.get(groupKey) || 0) > 1);
    return { ...mergeDisplayedSiteItem(item, tmdbMatch, { canAggregate }), displayKey: siteItemIdentity(item) };
  });

  if (!rawListMode) {
    siteDisplay = Array.from(groups.entries()).flatMap(([groupKey, group]) => {
      const representative = group.representative;
      if (!representative) return [];
      const itemCount = group.items.length;
      const tmdbMatch = tmdbByGroup.get(groupKey) || null;
      const aggregateRemark = formatAggregateTVRemark({
        tmdbMatch,
        groupTitle: (tmdbMatch && tmdbMatch.title) || group.title || representative.title || '',
        sources: group.items,
      });
      if (tmdbMatch) return [];
      const displayKey = `group:${groupKey}`;
      if (itemCount <= 1) {
        return [{
          ...mergeDisplayedSiteItem({
            ...representative,
            groupKey,
            textBadge: aggregateRemark || representative.textBadge,
            siteLabel: normalizeString(representative.siteLabel) || normalizeString(representative.siteName),
          }, null, { canAggregate: false }),
          displayKey,
        }];
      }
      return [{
        ...mergeDisplayedSiteItem({
          ...representative,
          id: `aggregate:${groupKey}`,
          groupKey,
          textBadge: aggregateRemark || representative.textBadge,
          siteLabel: '聚合',
          aggregateSourceCount: group.siteKeys.size,
          aggregateItems: group.items.slice(),
          aggregateKind: 'site',
        }, null, { canAggregate: true }),
        displayKey,
      }];
    });
  }

  const byKey = new Map([
    ...tmdb.map((item) => ({ ...item, displayKey: item.id })),
    ...siteDisplay,
  ].map((item) => [item.displayKey, item]));
  const items = [];
  (Array.isArray(previousOrder) ? previousOrder : []).forEach((key) => {
    if (!byKey.has(key)) return;
    items.push(byKey.get(key));
    byKey.delete(key);
  });
  // Only new cards participate in this round's sorting.
  const newItems = Array.from(byKey.values()).sort((left, right) => compareDisplayedResults(left, right, context));
  for (const item of newItems) items.push(item);
  return { items, order: items.map((item) => item.displayKey) };
};

globalThis.meowfilmProcess = (input) => {
  switch (input.operation) {
    case 'site': return mapSitePage(input);
    case 'tmdb': return mapTMDBSearch(input);
    case 'fallback': return { query: buildTrailingDigitsFallbackQuery(input.query) };
    case 'detail': return enrichTMDBDetail(input);
    case 'display': return buildDisplay(input);
    default: throw new Error(`Unknown film-search operation: ${input.operation}`);
  }
};
})();
