import { extractRawNamesFromEpisodeUrl } from './catpawrunner';
import {
  doubanGlobalEpisodeNoOf,
  doubanSeasonEpisodeOfGlobal,
  tmdbGlobalEpisodeNoOf,
  tmdbSeasonEpisodeOfGlobal,
} from './smartEpisodeMapping';
import { normalizeSeasonEpisodeMarkers, parseChineseNumeralToInt } from './episodeMarkerNormalize';
import { normalizeInt, normalizeString } from './normalize';
import { getRawFileName, splitRawPathSegments } from './pathText';

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

const normalizeRegexText = (text) => {
  const raw = typeof text === 'string' ? text : '';
  if (!raw) return '';
  return raw.replace(/\\\\(?=[dDsSwWbB.()[\]{}+*?^$|\\\-_/])/g, '\\');
};

const buildRegexFromInput = (value, { defaultFlags = '', forceGlobal = false } = {}) => {
  const parsed = normalizePatternInput(value);
  if (!parsed || !parsed.pattern) return null;
  let flags = parsed.flags || defaultFlags;
  if (forceGlobal && !flags.includes('g')) flags += 'g';
  try {
    return new RegExp(normalizeRegexText(parsed.pattern), flags);
  } catch (_error) {
    return null;
  }
};

const compileMagicCleanRules = (rawRules) =>
  (Array.isArray(rawRules) ? rawRules : [])
    .map((rule) => buildRegexFromInput(rule, { defaultFlags: 'ig', forceGlobal: true }))
    .filter(Boolean);

const compileMagicRules = (rawRules) =>
  (Array.isArray(rawRules) ? rawRules : [])
    .map((rule) => {
      if (!rule) return null;

      if (typeof rule === 'string') {
        const raw = normalizeString(rule);
        if (!raw) return null;
        if (raw.startsWith('{') && raw.endsWith('}')) {
          try {
            const obj = JSON.parse(raw);
            if (obj && typeof obj === 'object' && typeof obj.pattern === 'string') {
              const re = buildRegexFromInput(`/${obj.pattern}/${normalizeString(obj.flags) || 'i'}`);
              if (!re) return null;
              const replaceRaw = typeof obj.replace === 'string' ? obj.replace : '';
              return {
                re,
                replace: replaceRaw ? replaceRaw.replace(/\\(\d+)/g, '$$$1') : '',
              };
            }
          } catch (_error) {}
        }
        const re = buildRegexFromInput(raw, { defaultFlags: 'i' });
        return re ? { re, replace: '' } : null;
      }

      const pattern = normalizeString(rule.pattern || rule.regex || rule.re || '');
      if (!pattern) return null;
      const flags = normalizeString(rule.flags) || 'i';
      try {
        return {
          re: new RegExp(normalizeRegexText(pattern), flags),
          replace: typeof rule.replace === 'string' ? rule.replace.replace(/\\(\d+)/g, '$$$1') : '',
        };
      } catch (_error) {
        return null;
      }
    })
    .filter(Boolean);

const compileMagicEpisodeRules = (rawRules) => compileMagicRules(rawRules);
const compileMagicMovieRules = (rawRules) => compileMagicRules(rawRules);

const cleanMagicEpisodeText = (text, cleanRules) => {
  let out = normalizeString(text);
  (Array.isArray(cleanRules) ? cleanRules : []).forEach((rule) => {
    try {
      if (rule.global || rule.sticky) rule.lastIndex = 0;
      out = out.replace(rule, '');
    } catch (_error) {}
  });
  return normalizeString(out);
};

const normalizeSeasonHintText = (value) =>
  normalizeString(value)
    .replace(/[０-９]/g, (ch) => String('０１２３４５６７８９'.indexOf(ch)))
    .replace(/\s+/g, '')
    .toLowerCase();

const parseSeasonHintNumber = (value) => {
  const normalized = normalizeSeasonHintText(value);
  return /^\d+$/.test(normalized) ? normalizeInt(normalized) : parseChineseNumeralToInt(normalized);
};

const extractSeasonHintFromText = (text) => {
  const normalized = normalizeSeasonHintText(text);
  if (!normalized) return 0;
  const mSeasonWord = normalized.match(/(?:^|[^a-z0-9])season0*(\d{1,3})(?=$|[^a-z0-9])/);
  if (mSeasonWord && mSeasonWord[1]) return normalizeInt(mSeasonWord[1]);
  const mSe = normalized.match(/(?:^|[^a-z0-9])s0*(\d{1,3})(?:e\d{1,5})?(?=$|[^a-z0-9])/);
  if (mSe && mSe[1]) return normalizeInt(mSe[1]);
  const mSeason = normalized.match(/第([0-9]{1,3}|[一二三四五六七八九十百千两零〇]{1,16})(?:季|部|篇)/);
  if (!mSeason || !mSeason[1]) return 0;
  return parseSeasonHintNumber(mSeason[1]);
};

const parseManualSeasonHint = (value) => {
  const raw = normalizeString(value).toUpperCase();
  if (!raw) return 0;
  const matched = raw.match(/^S?\s*0*(\d{1,3})$/i);
  if (!matched || !matched[1]) return 0;
  return normalizeInt(matched[1]);
};

const extractNormalizedSeasonEpisode = (text, compiledRules, cleanRules) => {
  const rawText = normalizeString(text);
  const normalizedRawText = normalizeSeasonEpisodeMarkers(rawText);
  const cleaned = normalizeSeasonEpisodeMarkers(cleanMagicEpisodeText(normalizedRawText || rawText, cleanRules));
  if (!normalizedRawText && !cleaned) return { season: 0, episode: 0 };
  const parseNormalized = (value) => {
    const input = normalizeString(value);
    if (!input) return null;
    const seasonMatch = input.match(/S\s*0*(\d{1,2})\s*E\s*0*(\d{1,5})/i);
    if (seasonMatch && seasonMatch[1] && seasonMatch[2]) {
      return {
        season: normalizeInt(seasonMatch[1]),
        episode: normalizeInt(seasonMatch[2]),
      };
    }
    const epMatch = input.match(/\bE\s*0*(\d{1,5})\b/i);
    if (epMatch && epMatch[1]) {
      return { season: 0, episode: normalizeInt(epMatch[1]) };
    }
    return null;
  };
  const direct = parseNormalized(normalizedRawText) || parseNormalized(cleaned);
  if (direct && direct.episode > 0) return direct;
  for (let i = 0; i < compiledRules.length; i += 1) {
    const rule = compiledRules[i];
    const re = rule && rule.re ? rule.re : null;
    if (!re) continue;
    const matched = cleaned.match(re);
    if (!matched) continue;
    const normalized = rule && rule.replace
      ? (() => {
          try {
            return cleaned.replace(re, rule.replace);
          } catch (_error) {
            return '';
          }
        })()
      : ((matched[2] != null ? String(matched[2]) : '') || (matched[1] != null ? String(matched[1]) : '') || String(matched[0] || ''));
    const parsed = parseNormalized(normalizeSeasonEpisodeMarkers(normalized));
    if (parsed && parsed.episode > 0) return parsed;
  }
  return { season: 0, episode: 0 };
};

const extractQualityMark = (text) => {
  const raw = normalizeString(text).toLowerCase();
  if (!raw) return '';
  if (/(?:4320p|8k)/i.test(raw)) return '8K';
  if (/(?:2160p|4k|uhd)/i.test(raw)) return '4K';
  if (/(?:1080p|1080i|fhd)/i.test(raw)) return '1080P';
  if (/(?:720p|hd)/i.test(raw)) return '720P';
  return '';
};

const parseDisplayMetaQuality = (displayName) => {
  let rest = normalizeString(displayName);
  let quality = '';
  while (rest.startsWith('@')) {
    const matched = rest.match(/^@([^@/\\]+)/);
    if (!matched || !matched[1]) break;
    const token = normalizeString(matched[1]).toUpperCase();
    if (!quality) {
      if (token === '8K' || token === '4320P') quality = '8K';
      else if (token === '4K' || token === '2160P') quality = '4K';
      else if (token === '1080P') quality = '1080P';
      else if (token === '720P') quality = '720P';
    }
    rest = normalizeString(rest.slice(matched[0].length));
  }
  return quality;
};

const stripDisplayMetaPrefix = (value) => {
  let rest = normalizeString(value);
  while (rest.startsWith('@')) {
    const matched = rest.match(/^@([^@/\\]+)/);
    if (!matched || !matched[0]) break;
    rest = normalizeString(rest.slice(matched[0].length));
  }
  return rest;
};

const PAN_LIST_PROVIDER_KEYS = new Set(['baidu', 'quark', 'uc', '139', '189']);

const isPanListEntry = (entry) =>
  PAN_LIST_PROVIDER_KEYS.has(normalizeString(entry && entry.provider).toLowerCase());

const matchesAnyMagicRule = (text, rules) => {
  const normalizeForMagic = (input) => {
    const raw = typeof input === 'string' ? input : '';
    if (!raw) return '';
    return raw
      .trim()
      .replace(/[０-９]/g, (ch) => String('０１２３４５６７８９'.indexOf(ch)))
      .replace(/\s+/g, ' ');
  };

  const input = normalizeForMagic(text);
  const list = Array.isArray(rules) ? rules : [];
  if (!input || !list.length) return false;
  for (let i = 0; i < list.length; i += 1) {
    const re = list[i] && list[i].re ? list[i].re : null;
    if (!re) continue;
    try {
      if (re.global || re.sticky) re.lastIndex = 0;
    } catch (_error) {}
    if (re.test(input)) return true;
  }
  const noSpace = input.replace(/\s+/g, '');
  if (noSpace && noSpace !== input) {
    for (let i = 0; i < list.length; i += 1) {
      const re = list[i] && list[i].re ? list[i].re : null;
      if (!re) continue;
      try {
        if (re.global || re.sticky) re.lastIndex = 0;
      } catch (_error) {}
      if (re.test(noSpace)) return true;
    }
  }
  return false;
};

const isInformativeEpisodeText = (text, rules) => {
  const input = normalizeString(text);
  const compiled = Array.isArray(rules) ? rules : [];
  if (!input) return false;
  if (!compiled.length) return true;
  return matchesAnyMagicRule(input, compiled);
};

const extractEpisodeCandidateTextsFromSegment = (segment, compiledEpisodeRules) => {
  const fileName = normalizeString(segment && segment.fileName);
  const displayName = normalizeString(segment && segment.displayName);
  const rawNames = [normalizeString(segment && segment.rawName)].filter(Boolean);
  const currentDir = normalizeString(segment && segment.currentDir);
  const parentDir = normalizeString(segment && segment.parentDir);
  const out = [];
  const push = (value) => {
    const text = normalizeString(value);
    if (!text) return;
    if (!out.includes(text)) out.push(text);
  };

  // 提取优先级：文件名 -> 展示名 -> raw 路径层
  if (fileName) push(fileName);
  if (displayName && (!fileName || displayName !== fileName)) push(displayName);
  rawNames.forEach((item) => push(item));

  const fileHasSeason = !!extractSeasonHintFromText(fileName);
  if (!fileHasSeason && currentDir) {
    const currentSeasonMarker = extractSeasonHintFromText(currentDir);
    if (currentSeasonMarker > 0) push(`第${currentSeasonMarker}季`);
    push(currentDir);
    if (!currentSeasonMarker) {
      const currentQuality = extractQualityMark(currentDir);
      if (currentQuality && parentDir) {
        const parentSeasonMarker = extractSeasonHintFromText(parentDir);
        if (parentSeasonMarker > 0) {
          push(`第${parentSeasonMarker}季`);
          push(parentDir);
        }
      }
    }
  }
  return out;
};

const buildPanAliasMatchers = (tokens, mappings) => {
  const tokenList = (Array.isArray(tokens) ? tokens : []).map(normalizeString).filter(Boolean);
  const aliasRows = Array.isArray(mappings) ? mappings : [];
  const aliasMap = new Map();
  aliasRows.forEach((row) => {
    const pan = normalizeString(row && row.pan).toLowerCase();
    if (!pan) return;
    const aliases = String((row && row.aliases) || '')
      .replaceAll('，', ',')
      .split(',')
      .map((item) => normalizeString(item).toLowerCase())
      .filter(Boolean);
    aliasMap.set(pan, aliases);
  });
  return tokenList.map((token, index) => ({
    token,
    index,
    probes: [token.toLowerCase()].concat(aliasMap.get(token.toLowerCase()) || []),
  }));
};

const normalizePanMatchLabelText = (label) =>
  normalizeString(label)
    .toLowerCase()
    .replace(/[\s\-_—–·|:：/\\()[\]{}<>【】（）「」『』《》、，,。.！!？?~～]+/g, '');

const resolvePanMatch = (label, tokens, mappings) => {
  const hay = normalizePanMatchLabelText(label);
  if (!hay) return { token: '', index: -1 };
  const matchers = buildPanAliasMatchers(tokens, mappings);
  for (let i = 0; i < matchers.length; i += 1) {
    const matcher = matchers[i];
    if (matcher.probes.some((probe) => probe && hay.includes(probe))) {
      return { token: matcher.token, index: matcher.index };
    }
  }
  return { token: '', index: -1 };
};

const buildFolderStats = (items) => {
  const stats = new Map();
  const touch = (path) => {
    const key = normalizeString(path);
    if (!stats.has(key)) {
      stats.set(key, { seasons: new Set(), qualities: new Set() });
    }
    return stats.get(key);
  };
  (Array.isArray(items) ? items : []).forEach((item) => {
    const dirs = Array.isArray(item.dirs) ? item.dirs : [];
    const currentPath = normalizeString(item.currentPath);
    const currentDir = normalizeString(item.currentDir);
    const parentPath = normalizeString(item.parentPath);
    const current = touch(currentPath);
    const season = extractSeasonHintFromText(currentDir);
    const quality = extractQualityMark(currentDir);
    if (season > 0) current.seasons.add(season);
    if (quality) current.qualities.add(quality);
    if (parentPath) touch(parentPath);
    dirs.forEach((_, index) => touch(dirs.slice(0, index + 1).join('/')));
  });
  return stats;
};

const pickUniqueFolderSeason = (stats, path) => {
  const hit = stats.get(normalizeString(path));
  if (!hit || hit.seasons.size !== 1) return 0;
  return Array.from(hit.seasons)[0] || 0;
};

const pickUniqueFolderQuality = (stats, path) => {
  const hit = stats.get(normalizeString(path));
  if (!hit || hit.qualities.size !== 1) return '';
  return normalizeString(Array.from(hit.qualities)[0]);
};

const buildSegmentItems = (entry) => {
  const segments = Array.isArray(entry && entry.episodeSegments)
    ? entry.episodeSegments.map(normalizeString).filter(Boolean)
    : [];
  if (!segments.length) return [];
  return segments
    .map((segment, index) => {
      const raw = normalizeString(segment);
      if (!raw) return null;
      const dollarIdx = raw.indexOf('$');
      const displayName = dollarIdx >= 0 ? normalizeString(raw.slice(0, dollarIdx)) : raw;
      const episodeUrl = dollarIdx >= 0 ? normalizeString(raw.slice(dollarIdx + 1)) : raw;
      const rawName = extractRawNamesFromEpisodeUrl(episodeUrl)[0] || '';
      const fileName = getRawFileName(rawName);
      const rawDirs = splitRawPathSegments(rawName).slice(0, -1);
      const useListPathSemantics = isPanListEntry(entry) && !!fileName;
      const displayParts = splitRawPathSegments(useListPathSemantics ? stripDisplayMetaPrefix(displayName) : displayName);
      const displayDirs = useListPathSemantics
        ? displayParts
        : displayParts.slice(0, -1);
      const fileDirs = useListPathSemantics ? displayDirs : (rawDirs.length ? rawDirs : displayDirs);
      return {
        index,
        segmentIdentity: raw,
        displayName,
        episodeUrl,
        rawName,
        fileName,
        dirs: fileDirs,
        allowDirectoryHints: useListPathSemantics,
        currentDir: fileDirs[fileDirs.length - 1] || '',
        currentPath: fileDirs.join('/'),
        parentDir: fileDirs.length >= 2 ? fileDirs[fileDirs.length - 2] : '',
        parentPath: fileDirs.length >= 2 ? fileDirs.slice(0, -1).join('/') : '',
      };
    })
    .filter(Boolean);
};

export const buildSourceSegmentItems = (entry) => buildSegmentItems(entry);

const buildSeasonCountLookup = (rows, field) => {
  const out = new Map();
  const fields = Array.isArray(field) ? field : [field];
  (Array.isArray(rows) ? rows : []).forEach((item) => {
    const season = normalizeInt(item && (item.season != null ? item.season : item.season_number));
    const count = fields.reduce((value, key) => (value > 0 ? value : normalizeInt(item && item[key])), 0);
    if (season > 0 && count > 0) out.set(season, count);
  });
  return out;
};

const getFallbackBoundary = ({ primaryRows, primaryCounts, primaryMultiSeason, baselineSingleSeason }) => {
  const rows = Array.isArray(primaryRows) ? primaryRows : [];
  const counts = primaryCounts instanceof Map ? primaryCounts : new Map();
  if (primaryMultiSeason && baselineSingleSeason) {
    const sorted = rows
      .map((row) => ({
        season: normalizeInt(row && (row.season != null ? row.season : row.season_number)),
        count: normalizeInt(row && (row.episodes != null ? row.episodes : (row.episodeCount != null ? row.episodeCount : row.episode_count))),
      }))
      .filter((row) => row.season > 0 && row.count > 0)
      .sort((a, b) => a.season - b.season);
    if (sorted.length > 1) {
      return sorted.slice(0, -1).reduce((sum, row) => sum + row.count, 0);
    }
  }
  return normalizeInt(counts.get(1));
};

const getTMDBSeasonRows = (detail) => (
  Array.isArray(detail && detail.seasons)
    ? detail.seasons
    : []
);

const getDoubanSeasonRows = (meta) => (
  Array.isArray(meta && meta.doubanSeasons)
    ? meta.doubanSeasons
    : []
);

const buildMappingCandidate = ({
  from,
  global,
  tmdbDetail,
  doubanMeta,
  tmdb,
  douban,
  resolutionMode = '',
} = {}) => {
  const globalNo = normalizeInt(global);
  if (globalNo <= 0) return [];
  const tmdbValue = tmdb || tmdbSeasonEpisodeOfGlobal(tmdbDetail, globalNo);
  const doubanValue = douban || doubanSeasonEpisodeOfGlobal(doubanMeta, globalNo);
  const tmdbEpisode = normalizeInt(tmdbValue && tmdbValue.episode);
  const doubanEpisode = normalizeInt(doubanValue && doubanValue.episode);
  if (tmdbEpisode <= 0 && doubanEpisode <= 0) return [];
  const key = `${globalNo}:${normalizeInt(tmdbValue && tmdbValue.season)}:${tmdbEpisode}:${normalizeInt(doubanValue && doubanValue.season)}:${doubanEpisode}`;
  return [{
    key,
    from,
    global: globalNo,
    tmdb: tmdbValue,
    douban: doubanValue,
    resolutionMode: normalizeString(resolutionMode),
  }];
};

const resolveStrictTMDBGlobal = ({ seasonNo, episodeNo, tmdbDetail, tmdbCounts }) => {
  const seasonCount = normalizeInt(tmdbCounts && tmdbCounts.get(seasonNo));
  if (seasonNo <= 0 || episodeNo <= 0 || seasonCount <= 0 || episodeNo > seasonCount) return 0;
  return tmdbGlobalEpisodeNoOf(tmdbDetail, seasonNo, episodeNo);
};

const resolveAssistedGlobalFromDouban = ({ seasonNo, episodeNo, doubanMeta, doubanCounts }) => {
  const seasonCount = normalizeInt(doubanCounts && doubanCounts.get(seasonNo));
  if (seasonNo <= 0 || episodeNo <= 0 || seasonCount <= 0 || episodeNo > seasonCount) return 0;
  return doubanGlobalEpisodeNoOf(doubanMeta, seasonNo, episodeNo);
};

const resolveSeasonMarkedStrictTMDB = ({
  seasonNo,
  episodeNo,
  tmdbDetail,
  doubanMeta,
  tmdbCounts,
}) => {
  const tmdbGlobal = resolveStrictTMDBGlobal({ seasonNo, episodeNo, tmdbDetail, tmdbCounts });
  if (tmdbGlobal <= 0) return [];
  return buildMappingCandidate({
    from: 'tmdb',
    global: tmdbGlobal,
    tmdbDetail,
    doubanMeta,
    tmdb: { season: seasonNo, episode: episodeNo },
    resolutionMode: 'strict-tmdb',
  });
};

const resolveSeasonMarkedStrictDouban = ({
  seasonNo,
  episodeNo,
  tmdbDetail,
  doubanMeta,
  doubanCounts,
}) => {
  const doubanGlobal = resolveAssistedGlobalFromDouban({ seasonNo, episodeNo, doubanMeta, doubanCounts });
  if (doubanGlobal <= 0) return [];
  return buildMappingCandidate({
    from: 'assist',
    global: doubanGlobal,
    tmdbDetail,
    doubanMeta,
    douban: { season: seasonNo, episode: episodeNo },
    resolutionMode: 'strict-douban',
  });
};

const resolveEpisodeOnlyStrictTMDB = ({
  episodeNo,
  tmdbDetail,
  doubanMeta,
  tmdbMultiSeason,
}) => {
  if (tmdbMultiSeason || episodeNo <= 0) return [];
  const tmdb = tmdbSeasonEpisodeOfGlobal(tmdbDetail, episodeNo);
  if (normalizeInt(tmdb && tmdb.season) !== 1 || normalizeInt(tmdb && tmdb.episode) !== episodeNo) return [];
  return buildMappingCandidate({
    from: 'tmdb',
    global: episodeNo,
    tmdbDetail,
    doubanMeta,
    tmdb,
    resolutionMode: 'strict-tmdb',
  });
};

const resolveEpisodeOnlyStrictDouban = ({
  episodeNo,
  tmdbDetail,
  doubanMeta,
  doubanMultiSeason,
}) => {
  if (doubanMultiSeason || episodeNo <= 0) return [];
  const douban = doubanSeasonEpisodeOfGlobal(doubanMeta, episodeNo);
  if (normalizeInt(douban && douban.season) !== 1 || normalizeInt(douban && douban.episode) !== episodeNo) return [];
  return buildMappingCandidate({
    from: 'assist',
    global: episodeNo,
    tmdbDetail,
    doubanMeta,
    douban,
    resolutionMode: 'strict-douban',
  });
};

const buildSeasonMarkedMappings = ({
  seasonNo,
  episodeNo,
  tmdbDetail,
  doubanMeta,
  tmdbMultiSeason,
  doubanMultiSeason,
  sourceDirMaxEpisode,
}) => {
  const tmdbSeasonRows = getTMDBSeasonRows(tmdbDetail);
  const doubanSeasonRows = getDoubanSeasonRows(doubanMeta);
  const tmdbCounts = buildSeasonCountLookup(tmdbSeasonRows, ['episodes', 'episodeCount', 'episode_count']);
  const doubanCounts = buildSeasonCountLookup(doubanSeasonRows, ['episodeCount', 'episode_count', 'episodes']);

  const strictTMDB = resolveSeasonMarkedStrictTMDB({
    seasonNo,
    episodeNo,
    tmdbDetail,
    doubanMeta,
    tmdbCounts,
  });
  if (strictTMDB.length) return strictTMDB;

  const strictDouban = resolveSeasonMarkedStrictDouban({
    seasonNo,
    episodeNo,
    tmdbDetail,
    doubanMeta,
    doubanCounts,
  });
  if (strictDouban.length) return strictDouban;

  const singleBaselineRows = (!tmdbMultiSeason && doubanMultiSeason)
    ? tmdbSeasonRows
    : ((tmdbMultiSeason && !doubanMultiSeason) ? doubanSeasonRows : []);
  const fallbackBoundary = getFallbackBoundary({
    primaryRows: tmdbSeasonRows,
    primaryCounts: tmdbCounts,
    primaryMultiSeason: tmdbMultiSeason,
    baselineSingleSeason: singleBaselineRows.length === 1,
  });
  const sourceHasBeyondFirstSeason = fallbackBoundary > 0 && normalizeInt(sourceDirMaxEpisode) > fallbackBoundary;
  return resolveDegradedSingleBaseline({
    episodeNo,
    seasonNo,
    tmdbDetail,
    doubanMeta,
    singleBaselineRows,
    sourceHasBeyondFirstSeason,
    reason: 'season-marked-fallback',
  });
};

const resolveDegradedSingleBaseline = ({
  episodeNo,
  seasonNo,
  tmdbDetail,
  doubanMeta,
  singleBaselineRows,
  sourceHasBeyondFirstSeason,
  reason,
}) => {
  const e = normalizeInt(episodeNo);
  if (e <= 0 || !Array.isArray(singleBaselineRows) || singleBaselineRows.length !== 1) return [];
  const baselineSE = tmdbSeasonEpisodeOfGlobal({ seasons: singleBaselineRows }, e);
  if (normalizeInt(baselineSE && baselineSE.season) !== 1 || normalizeInt(baselineSE && baselineSE.episode) !== e) return [];
  if (!sourceHasBeyondFirstSeason) return [];
  return buildMappingCandidate({
    from: 'assist',
    global: e,
    tmdbDetail,
    doubanMeta,
    resolutionMode: 'degraded-single-baseline',
  }).map((item) => ({
    ...item,
    degradedReason: normalizeString(reason),
    extractedSeason: normalizeInt(seasonNo),
  }));
};

const buildEpisodeOnlyMappings = ({
  episodeNo,
  tmdbDetail,
  doubanMeta,
  tmdbMultiSeason,
  doubanMultiSeason,
  sourceDirMaxEpisode,
}) => {
  const strictTMDB = resolveEpisodeOnlyStrictTMDB({
    episodeNo,
    tmdbDetail,
    doubanMeta,
    tmdbMultiSeason,
  });
  if (strictTMDB.length) return strictTMDB;
  const strictDouban = resolveEpisodeOnlyStrictDouban({
    episodeNo,
    tmdbDetail,
    doubanMeta,
    doubanMultiSeason,
  });
  if (strictDouban.length) return strictDouban;

  const tmdbRows = getTMDBSeasonRows(tmdbDetail);
  const doubanRows = getDoubanSeasonRows(doubanMeta);
  const singleBaselineRows = (!tmdbMultiSeason && doubanMultiSeason)
    ? tmdbRows
    : ((tmdbMultiSeason && !doubanMultiSeason) ? doubanRows : []);
  const tmdbCounts = buildSeasonCountLookup(tmdbRows, ['episodes', 'episodeCount', 'episode_count']);
  const fallbackBoundary = getFallbackBoundary({
    primaryRows: tmdbRows,
    primaryCounts: tmdbCounts,
    primaryMultiSeason: tmdbMultiSeason,
    baselineSingleSeason: singleBaselineRows.length === 1,
  });
  const sourceHasBeyondFirstSeason = fallbackBoundary > 0 && normalizeInt(sourceDirMaxEpisode) > fallbackBoundary;
  return resolveDegradedSingleBaseline({
    episodeNo,
    seasonNo: 0,
    tmdbDetail,
    doubanMeta,
    singleBaselineRows,
    sourceHasBeyondFirstSeason,
    reason: 'episode-only-fallback',
  });
};

const buildCandidateMappings = ({
  season,
  episode,
  tmdbDetail,
  doubanMeta,
  tmdbMultiSeason,
  doubanMultiSeason,
  sourceDirMaxEpisode,
}) => {
  const seasonNo = normalizeInt(season);
  const episodeNo = normalizeInt(episode);
  if (episodeNo <= 0) return [];
  if (seasonNo <= 0) {
    return buildEpisodeOnlyMappings({
      episodeNo,
      tmdbDetail,
      doubanMeta,
      tmdbMultiSeason,
      doubanMultiSeason,
      sourceDirMaxEpisode,
    });
  }
  return buildSeasonMarkedMappings({
    seasonNo,
    episodeNo,
    tmdbDetail,
    doubanMeta,
    tmdbMultiSeason,
    doubanMultiSeason,
    sourceDirMaxEpisode,
  });
};

const buildRecognitionMappingContext = (smartEpisodeMapping) => {
  const tmdbDetail = smartEpisodeMapping && Array.isArray(smartEpisodeMapping.tmdbSeasons) && smartEpisodeMapping.tmdbSeasons.length
    ? {
        latestGlobal: normalizeInt(smartEpisodeMapping.totalEpisodes),
        seasons: smartEpisodeMapping.tmdbSeasons.map((item) => ({
          season: item.season,
          episodes: item.episodeCount,
        })),
      }
    : null;
  const doubanMeta = smartEpisodeMapping && Array.isArray(smartEpisodeMapping.doubanSeasons)
    ? {
        doubanSeasons: smartEpisodeMapping.doubanSeasons.map((item) => ({
          season: item.season,
          episodeCount: item.episodeCount,
        })),
      }
    : null;
  return {
    tmdbDetail,
    doubanMeta,
    tmdbMultiSeason: !!(smartEpisodeMapping && Array.isArray(smartEpisodeMapping.tmdbSeasons) && smartEpisodeMapping.tmdbSeasons.length > 1),
    doubanMultiSeason: !!(smartEpisodeMapping && Array.isArray(smartEpisodeMapping.doubanSeasons) && smartEpisodeMapping.doubanSeasons.length > 1),
  };
};

const resolveManualHintSeason = ({ recognitionOptions, tmdbDetail, doubanMeta, tmdbMultiSeason, doubanMultiSeason } = {}) => {
  if (!(tmdbMultiSeason || doubanMultiSeason)) return 0;
  const candidate = parseManualSeasonHint(recognitionOptions && recognitionOptions.manualSeasonHint);
  if (candidate <= 0) return 0;
  const tmdbRows = getTMDBSeasonRows(tmdbDetail);
  const doubanRows = getDoubanSeasonRows(doubanMeta);
  const hasTMDB = tmdbRows.some((row) => normalizeInt(row && (row.season != null ? row.season : row.season_number)) === candidate);
  const hasDouban = doubanRows.some((row) => normalizeInt(row && (row.season != null ? row.season : row.season_number)) === candidate);
  if (!hasTMDB && !hasDouban) return 0;
  return candidate;
};

const buildDirEpisodeMaxLookup = ({ segments, compiledRules, cleanRules } = {}) => {
  const dirEpisodeMax = new Map();
  (Array.isArray(segments) ? segments : []).forEach((segment) => {
    const fileParsed = extractNormalizedSeasonEpisode(segment && segment.fileName, compiledRules, cleanRules);
    const displayParsed = !fileParsed.episode
      ? extractNormalizedSeasonEpisode(segment && segment.displayName, compiledRules, cleanRules)
      : { season: 0, episode: 0 };
    const episodeNo = normalizeInt((fileParsed && fileParsed.episode) || (displayParsed && displayParsed.episode));
    if (episodeNo <= 0) return;
    const dirKey = normalizeString(segment && segment.currentPath);
    const prev = normalizeInt(dirEpisodeMax.get(dirKey));
    if (episodeNo > prev) dirEpisodeMax.set(dirKey, episodeNo);
  });
  return dirEpisodeMax;
};

const resolveSegmentEpisodeFields = ({
  segment,
  compiledRules,
  cleanRules,
  folderStats,
  dirEpisodeMax,
  manualHintSeason = 0,
} = {}) => {
  const fileParsed = extractNormalizedSeasonEpisode(segment && segment.fileName, compiledRules, cleanRules);
  const displayParsed = !fileParsed.episode
    ? extractNormalizedSeasonEpisode(segment && segment.displayName, compiledRules, cleanRules)
    : { season: 0, episode: 0 };

  let season = fileParsed.season;
  let episode = fileParsed.episode;
  let quality = extractQualityMark(segment && segment.fileName);

  if (!episode && displayParsed.episode) {
    season = displayParsed.season;
    episode = displayParsed.episode;
  }

  if (episode > 0 && segment && segment.allowDirectoryHints && (season <= 0 || !quality)) {
    const currentQuality = pickUniqueFolderQuality(folderStats, segment && segment.currentPath);
    const currentSeason = pickUniqueFolderSeason(folderStats, segment && segment.currentPath);
    const parentQuality = pickUniqueFolderQuality(folderStats, segment && segment.parentPath);
    const parentSeason = pickUniqueFolderSeason(folderStats, segment && segment.parentPath);
    if (season <= 0) {
      if (currentSeason > 0) {
        season = currentSeason;
      } else if (currentQuality && parentSeason > 0) {
        season = parentSeason;
      }
    }
    if (!quality) {
      const displayMetaQuality = parseDisplayMetaQuality(segment && segment.displayName);
      if (displayMetaQuality) quality = displayMetaQuality;
      else if (currentQuality) quality = currentQuality;
      else if (currentSeason > 0 && parentQuality) quality = parentQuality;
    }
  }

  if (episode > 0 && season <= 0 && manualHintSeason > 0) {
    season = manualHintSeason;
  }

  if (!quality) {
    quality = parseDisplayMetaQuality(segment && segment.displayName) || extractQualityMark(segment && segment.displayName);
  }

  return {
    segment,
    season: normalizeInt(season),
    episode: normalizeInt(episode),
    quality,
    sourceDirMaxEpisode: normalizeInt(dirEpisodeMax && dirEpisodeMax.get(segment && segment.currentPath)),
  };
};

const buildRecognizedEpisodeSegmentRows = ({
  entry,
  runtimeSettings,
  smartEpisodeMapping = null,
  recognitionOptions = null,
} = {}) => {
  const target = entry && typeof entry === 'object' ? entry : null;
  if (!target || !Array.isArray(target.episodeSegments) || !target.episodeSegments.length) {
    return {
      segments: [],
      rows: [],
      folderStats: new Map(),
      ...buildRecognitionMappingContext(null),
    };
  }
  const compiledRules = compileMagicEpisodeRules(runtimeSettings && runtimeSettings.magicEpisodeRules);
  const cleanRules = compileMagicCleanRules(runtimeSettings && runtimeSettings.magicEpisodeCleanRegexRules);
  const segments = buildSegmentItems(target);
  const folderStats = buildFolderStats(segments);
  const dirEpisodeMax = buildDirEpisodeMaxLookup({ segments, compiledRules, cleanRules });
  const mappingContext = buildRecognitionMappingContext(smartEpisodeMapping);
  const manualHintSeason = resolveManualHintSeason({
    recognitionOptions,
    ...mappingContext,
  });
  const rows = segments
    .map((segment) => resolveSegmentEpisodeFields({
      segment,
      compiledRules,
      cleanRules,
      folderStats,
      dirEpisodeMax,
      manualHintSeason,
    }))
    .filter((row) => normalizeInt(row && row.episode) > 0);
  return {
    segments,
    rows,
    folderStats,
    ...mappingContext,
  };
};

export const buildPlaybackRecognitionData = ({
  entry,
  siteResultItem,
  runtimeSettings,
  smartEpisodeMapping,
  recognitionOptions = null,
} = {}) => {
  const source = siteResultItem && typeof siteResultItem === 'object'
    ? {
        key: normalizeString(entry && entry.key),
        siteKey: normalizeString(siteResultItem.siteKey),
        siteName: normalizeString(siteResultItem.siteName),
        spiderApi: normalizeString(siteResultItem.spiderApi),
        siteDetail: normalizeString(siteResultItem.siteDetail),
        panFlag: normalizeString(entry && entry.label),
      }
    : null;
  if (!entry || !Array.isArray(entry.episodeSegments) || !entry.episodeSegments.length) {
    return { source, items: [] };
  }

  const compiledRules = compileMagicEpisodeRules(runtimeSettings && runtimeSettings.magicEpisodeRules);
  const cleanRules = compileMagicCleanRules(runtimeSettings && runtimeSettings.magicEpisodeCleanRegexRules);
  const movieRules = compileMagicMovieRules(runtimeSettings && runtimeSettings.magicMovieRules);
  const panTokens = Array.isArray(runtimeSettings && runtimeSettings.smartPanMatchTokens)
    ? runtimeSettings.smartPanMatchTokens
    : [];
  const panMappings = Array.isArray(runtimeSettings && runtimeSettings.smartPanAliasMappings)
    ? runtimeSettings.smartPanAliasMappings
    : [];
  const {
    segments,
    rows,
    folderStats,
    tmdbDetail,
    doubanMeta,
    tmdbMultiSeason,
    doubanMultiSeason,
  } = buildRecognizedEpisodeSegmentRows({
    entry,
    runtimeSettings,
    smartEpisodeMapping,
    recognitionOptions,
  });
  const panMatch = resolvePanMatch(entry.label, panTokens, panMappings);
  const items = [];

  rows.forEach((row) => {
    const segment = row.segment;
    const season = row.season;
    const episode = row.episode;
    const quality = row.quality;
    const sourceDirMaxEpisode = row.sourceDirMaxEpisode;
    const mappings = buildCandidateMappings({
      season,
      episode,
      tmdbDetail,
      doubanMeta,
      tmdbMultiSeason,
      doubanMultiSeason,
      sourceDirMaxEpisode,
    });
    mappings.forEach((mapping, candidateIndex) => {
      const resolutionMode = normalizeString(mapping && mapping.resolutionMode);
      const candidate = {
        key: `${normalizeString(entry.key)}:${segment.index}:${candidateIndex}`,
        matchKind: 'episode',
        source,
        panFlag: normalizeString(entry.label),
        panToken: panMatch.token,
        panTokenIndex: panMatch.index,
        provider: normalizeString(entry.provider),
        itemIndex: segment.index,
        displayName: segment.displayName,
        rawName: segment.rawName,
        fileName: segment.fileName,
        segmentIdentity: normalizeString(segment.segmentIdentity),
        pathName: segment.currentPath ? `/${segment.currentPath}` : '',
        quality,
        extracted: {
          season: normalizeInt(season),
          episode: normalizeInt(episode),
        },
        mapping,
        resolutionMode,
      };
      items.push(candidate);
    });
  });

  segments.forEach((segment) => {
    const matchedRow = rows.find((row) => row && row.segment === segment) || null;
    const quality = normalizeString(matchedRow && matchedRow.quality)
      || resolveSegmentEpisodeFields({
        segment,
        compiledRules,
        cleanRules,
        folderStats,
        dirEpisodeMax: new Map(),
      }).quality;
    const movieMatched = movieRules.length
      && extractEpisodeCandidateTextsFromSegment(segment, []).some((text) => matchesAnyMagicRule(text, movieRules));
    if (!movieMatched) return;
    const movieCandidate = {
      key: `${normalizeString(entry.key)}:${segment.index}:movie`,
      matchKind: 'movie',
      movieMatched: true,
      source,
      panFlag: normalizeString(entry.label),
      panToken: panMatch.token,
      panTokenIndex: panMatch.index,
      provider: normalizeString(entry.provider),
      itemIndex: segment.index,
      displayName: segment.displayName,
      rawName: segment.rawName,
      fileName: segment.fileName,
      segmentIdentity: normalizeString(segment.segmentIdentity),
      pathName: segment.currentPath ? `/${segment.currentPath}` : '',
      quality,
      extracted: {
        season: 0,
        episode: 0,
      },
      mapping: null,
    };
    items.push(movieCandidate);
  });

  return { source, items };
};

export const buildDirectSiteEpisodeItems = (entry, runtimeSettings) => {
  const target = entry && typeof entry === 'object' ? entry : null;
  if (!target || !Array.isArray(target.episodeSegments) || !target.episodeSegments.length) return [];
  const out = [];
  const { rows } = buildRecognizedEpisodeSegmentRows({ entry: target, runtimeSettings });

  rows.forEach((row) => {
    const segment = row.segment;
    const season = row.season;
    const episode = row.episode;
    const quality = row.quality;

    out.push({
      key: `${normalizeString(target.key)}:${segment.index}:${Math.max(1, normalizeInt(season) || 1)}:${episode}`,
      itemIndex: normalizeInt(segment.index),
      season: normalizeInt(season),
      no: normalizeInt(episode),
      quality,
      is4k: quality === '4K',
      displayName: segment.displayName,
      rawName: segment.rawName,
      fileName: segment.fileName,
      segmentIdentity: normalizeString(segment.segmentIdentity),
      pathName: segment.currentPath ? `/${segment.currentPath}` : '',
    });
  });

  return out
    .filter((item) => item.itemIndex >= 0 && item.no > 0)
    .sort((left, right) => {
      const leftSeason = normalizeInt(left && left.season);
      const rightSeason = normalizeInt(right && right.season);
      const normalizedLeftSeason = leftSeason > 0 ? leftSeason : 1;
      const normalizedRightSeason = rightSeason > 0 ? rightSeason : 1;
      if (normalizedLeftSeason !== normalizedRightSeason) return normalizedLeftSeason - normalizedRightSeason;
      const leftNo = normalizeInt(left && left.no);
      const rightNo = normalizeInt(right && right.no);
      if (leftNo !== rightNo) return leftNo - rightNo;
      return normalizeInt(left && left.itemIndex) - normalizeInt(right && right.itemIndex);
    });
};

export const detectPlaybackSiteContentKind = ({ panSources, runtimeSettings } = {}) => {
  const pans = Array.isArray(panSources) ? panSources : [];
  if (!pans.length) return 'unknown';

  const episodeRules = compileMagicEpisodeRules(runtimeSettings && runtimeSettings.magicEpisodeRules);
  const movieRules = compileMagicMovieRules(runtimeSettings && runtimeSettings.magicMovieRules);
  const maxPerPan = 20;
  const maxTotal = 80;
  let seen = 0;

  if (episodeRules.length) {
    for (let p = 0; p < pans.length && seen < maxTotal; p += 1) {
      const segments = buildSegmentItems(pans[p]);
      const take = Math.min(maxPerPan, segments.length);
      for (let i = 0; i < take && seen < maxTotal; i += 1) {
        const candidates = extractEpisodeCandidateTextsFromSegment(segments[i], episodeRules);
        if (candidates.some((text) => matchesAnyMagicRule(text, episodeRules))) return 'series';
        seen += 1;
      }
    }
  }

  if (pans.length === 1) {
    const singleSegments = buildSegmentItems(pans[0]);
    if (singleSegments.length > 10) return 'unknown';
  }

  if (movieRules.length) {
    seen = 0;
    for (let p = 0; p < pans.length && seen < maxTotal; p += 1) {
      const segments = buildSegmentItems(pans[p]);
      const take = Math.min(maxPerPan, segments.length);
      for (let i = 0; i < take && seen < maxTotal; i += 1) {
        const candidates = extractEpisodeCandidateTextsFromSegment(segments[i], episodeRules);
        if (candidates.some((text) => matchesAnyMagicRule(text, movieRules))) return 'movie';
        seen += 1;
      }
    }
  }

  return 'unknown';
};
