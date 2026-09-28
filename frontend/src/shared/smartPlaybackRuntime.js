import { buildSiteSourceItemsForTitle } from './searchRuntime';
import { ensurePlayHistoryRowForContext } from './playHistoryRuntime';
import { buildPlaybackRecognitionData, buildSourceSegmentItems } from './smartSourceRecognition';
import {
  extractPanListVodPlayUrl,
  fetchCatResolvedDetailCached,
  normalizeSourceEntry,
  setPanListCachedByProviderFlag,
  requestPanListByProviderFlag,
} from './catpawrunner';
import { panMockProviderFromFlag } from '../utils/matchCore';
import { normalizeInt, normalizeString } from './normalize';

export const buildHistorySitePlaybackItem = (row) => {
  const item = row && typeof row === 'object' ? row : null;
  const siteKey = normalizeString(item && item.siteKey);
  const spiderApi = normalizeString(item && item.spiderApi);
  const siteDetail = normalizeString(item && item.siteDetail);
  const siteName = normalizeString(item && item.siteName);
  const title = normalizeString(item && item.contentKey);
  if (!siteKey || !spiderApi || !siteDetail || !siteName || !title) return null;
  return {
    id: `history:${siteKey}:${siteDetail}`,
    siteKey,
    spiderApi,
    siteDetail,
    siteName,
    title,
    groupKey: '',
    _history: true,
  };
};

const normalizeSourceSegments = (entry) => {
  const source = entry && typeof entry === 'object' ? entry : null;
  if (!source) return [];
  const normalized = typeof normalizeSourceEntry === 'function' ? normalizeSourceEntry(source, 0) : null;
  if (normalized && Array.isArray(normalized.episodeSegments)) {
    return normalized.episodeSegments.map(normalizeString).filter(Boolean);
  }
  return [];
};

export const buildPanSourcesFromDetail = (detail) => {
  const target = detail && typeof detail === 'object' ? detail : null;
  if (!target) return [];
  const directSources = Array.isArray(target.sources) ? target.sources : [];
  return directSources
    .map((item, index) => {
      const normalized = typeof normalizeSourceEntry === 'function' ? normalizeSourceEntry(item, index) : null;
      if (!normalized) return null;
      return {
        ...normalized,
        episodeSegments: Array.isArray(normalized.episodeSegments)
          ? normalized.episodeSegments.map(normalizeString).filter(Boolean)
          : [],
      };
    })
    .filter(Boolean);
};

export const buildPanSegment = (entry, index) => {
  if (!entry) return null;
  const itemIndex = normalizeInt(index);
  if (itemIndex < 0) return null;
  const parsed = buildSourceSegmentItems(entry).find((item) => normalizeInt(item && item.index) === itemIndex) || null;
  if (!parsed) return null;
  return {
    index: itemIndex,
    segmentIdentity: normalizeString(parsed.segmentIdentity),
    label: normalizeString(parsed.displayName),
    displayName: normalizeString(parsed.displayName),
    episodeUrl: normalizeString(parsed.episodeUrl),
    rawName: normalizeString(parsed.rawName),
    pathName: parsed.currentPath ? `/${parsed.currentPath}` : '',
    fileName: normalizeString(parsed.fileName),
  };
};

export const buildSiteSourceResultItemsFromSnapshot = (options = {}) => {
  return buildSiteSourceItemsForTitle(options);
};

export const getSiteResultCacheEntry = (store, itemOrId) => {
  const siteStore = store && typeof store === 'object' ? store : null;
  const itemKey = normalizeString(typeof itemOrId === 'string' ? itemOrId : itemOrId && itemOrId.id);
  if (!siteStore || !itemKey) return null;
  const entry = siteStore[itemKey];
  return entry && typeof entry === 'object' ? entry : null;
};

export const getSiteResultDetail = (store, itemOrId) => {
  const entry = getSiteResultCacheEntry(store, itemOrId);
  const detail = entry && entry.detail && typeof entry.detail === 'object' ? entry.detail : null;
  return detail;
};

export const getSiteResultRecognitionBySignature = (store, itemOrId, signature, panKey = '') => {
  const entry = getSiteResultCacheEntry(store, itemOrId);
  const key = normalizeString(signature);
  if (!entry || !key) return panKey ? null : {};
  const bySignature = entry.recognitionBySignature && typeof entry.recognitionBySignature === 'object'
    ? entry.recognitionBySignature[key]
    : null;
  if (!bySignature || typeof bySignature !== 'object') return panKey ? null : {};
  const sourceKey = normalizeString(panKey);
  if (!sourceKey) return bySignature;
  const payload = bySignature[sourceKey];
  return payload && typeof payload === 'object' ? payload : null;
};

export const setSiteResultDetailCacheEntry = ({
  store,
  item,
  detail,
  cacheMeta,
} = {}) => {
  const siteStore = store && typeof store === 'object' ? store : {};
  const target = item && typeof item === 'object' ? item : null;
  const nextDetail = detail && typeof detail === 'object' ? detail : null;
  const itemKey = normalizeString(target && target.id);
  if (!target || !nextDetail || !itemKey) return siteStore;
  const current = getSiteResultCacheEntry(siteStore, itemKey) || {};
  return {
    ...siteStore,
    [itemKey]: {
      ...current,
      siteItem: target,
      detail: nextDetail,
      cacheMeta: cacheMeta && typeof cacheMeta === 'object'
        ? { ...(current.cacheMeta && typeof current.cacheMeta === 'object' ? current.cacheMeta : {}), ...cacheMeta }
        : (current.cacheMeta && typeof current.cacheMeta === 'object' ? current.cacheMeta : {}),
    },
  };
};

export const cacheRecognitionForSiteResult = ({
  store,
  item,
  detail,
  signature,
  runtimeSettings,
  smartEpisodeMapping,
  recognitionOptions = null,
} = {}) => {
  const target = item && typeof item === 'object' ? item : null;
  const rawDetail = detail && typeof detail === 'object' ? detail : null;
  const key = normalizeString(signature);
  const itemKey = normalizeString(target && target.id);
  const currentStore = store && typeof store === 'object' ? store : {};
  if (!target || !rawDetail || !key || !itemKey) {
    return { store: currentStore, byEntry: {} };
  }
  const currentGroup =
    currentStore[itemKey] && typeof currentStore[itemKey] === 'object'
      ? currentStore[itemKey]
      : {};
  const currentRecognitionBySignature = currentGroup.recognitionBySignature
    && typeof currentGroup.recognitionBySignature === 'object'
    ? currentGroup.recognitionBySignature
    : {};
  const nextByEntry = {};
  buildPanSourcesFromDetail(rawDetail).forEach((entry) => {
    if (!entry || !entry.key) return;
    nextByEntry[entry.key] = buildPlaybackRecognitionData({
      entry,
      siteResultItem: target,
      runtimeSettings,
      smartEpisodeMapping,
      recognitionOptions,
    });
  });
  return {
    store: {
      ...currentStore,
      [itemKey]: {
        recognitionBySignature: {
          ...currentRecognitionBySignature,
          [key]: nextByEntry,
        },
      },
    },
    byEntry: nextByEntry,
  };
};

export const getRecognitionCandidatesForSiteResult = ({
  store,
  item,
  signature,
} = {}) => {
  const bySignature = getSiteResultRecognitionBySignature(store, item, signature);
  if (!bySignature || typeof bySignature !== 'object') return [];
  return Object.values(bySignature).filter((entry) => entry && typeof entry === 'object');
};

const normalizeMatchKind = (options) => {
  const raw = normalizeString(options && options.kind).toLowerCase();
  return raw === 'movie' ? 'movie' : 'episode';
};

const compareCollectedCandidateOrder = (left, right) => {
  const leftLoose = !!(left && left.looseMatch);
  const rightLoose = !!(right && right.looseMatch);
  if (leftLoose !== rightLoose) return leftLoose ? 1 : -1;
  return normalizeInt(left && left.itemIndex) - normalizeInt(right && right.itemIndex);
};

export const collectRecognitionCandidatesForTarget = ({
  store,
  item,
  signature,
  matchOptions = null,
  globalEpisodeNo,
  wantEpisodeInSeason = 0,
  episodeSource = 'TMDB',
  allowResolutionModes = null,
} = {}) => {
  const target = item && typeof item === 'object' ? item : null;
  const matchKind = normalizeMatchKind(matchOptions);
  const targetGlobal = normalizeInt(globalEpisodeNo);
  const targetLoose = normalizeInt(wantEpisodeInSeason);
  const sourceMode = normalizeString(episodeSource) === '豆瓣' ? 'douban' : 'tmdb';
  const allowedResolutionModes = Array.isArray(allowResolutionModes)
    ? allowResolutionModes.map((value) => normalizeString(value)).filter(Boolean)
    : [];
  const hasResolutionModeFilter = allowedResolutionModes.length > 0;
  if (!target || (matchKind === 'episode' && targetGlobal <= 0)) return [];
  const candidates = [];
  getRecognitionCandidatesForSiteResult({ store, item: target, signature }).forEach((recognitionData) => {
    const source = recognitionData && recognitionData.source && typeof recognitionData.source === 'object'
      ? recognitionData.source
      : null;
    const panKey = normalizeString(source && source.key);
    if (!panKey) return;
    const list = Array.isArray(recognitionData && recognitionData.items) ? recognitionData.items : [];
    list.forEach((candidate) => {
        if (matchKind === 'movie') {
          if (normalizeString(candidate && candidate.matchKind) !== 'movie' || !(candidate && candidate.movieMatched)) return;
          const itemIndex = normalizeInt(candidate && candidate.itemIndex);
          if (itemIndex < 0) return;
          candidates.push({
            siteItem: target,
            panKey,
            itemIndex,
            looseMatch: false,
            candidate,
          });
          return;
        }
        const resolutionMode = normalizeString(candidate && candidate.resolutionMode);
        if (hasResolutionModeFilter && (!resolutionMode || !allowedResolutionModes.includes(resolutionMode))) return;
        const mapping = candidate && candidate.mapping && typeof candidate.mapping === 'object' ? candidate.mapping : null;
        if (!mapping) return;
        const strictMatch = normalizeInt(mapping.global) === targetGlobal;
        const looseAllowed = !strictMatch
          && targetLoose > 0
          && (normalizeString(mapping.from) === 'global' || normalizeString(mapping.from) === 'single');
        const looseMatch = !strictMatch && targetLoose > 0
          && looseAllowed
          && normalizeInt(mapping && mapping[sourceMode] && mapping[sourceMode].episode) === targetLoose;
        if (!strictMatch && !looseMatch) return;
        const itemIndex = normalizeInt(candidate && candidate.itemIndex);
        if (itemIndex < 0) return;
        candidates.push({
          siteItem: target,
          panKey,
          itemIndex,
          looseMatch,
          candidate,
        });
    });
  });
  return candidates.sort(compareCollectedCandidateOrder);
};

export const collectRecognitionCandidatesForGlobal = (payload = {}) => (
  collectRecognitionCandidatesForTarget({
    ...payload,
    matchOptions: { kind: 'episode' },
  })
);

export const buildPlaybackContextCandidates = ({
  selectedSiteResultItem,
  currentPanSourceEntry,
  includeSelectedContext = true,
  lastResolvedPlaybackContext,
  lastBrowsePlaybackContext,
  includeBrowseContext = true,
  getSiteItemById,
  detailStore,
} = {}) => {
  const out = [];
  const push = (siteItem, panEntry) => {
    const item = siteItem && typeof siteItem === 'object' ? siteItem : null;
    const pan = panEntry && typeof panEntry === 'object' ? panEntry : null;
    const itemId = normalizeString(item && item.id);
    const panKey = normalizeString(pan && pan.key);
    if (!itemId || !panKey) return;
    const dedupeKey = `${itemId}::${panKey}`;
    if (out.some((entry) => entry.key === dedupeKey)) return;
    out.push({ key: dedupeKey, siteItem: item, panEntry: pan });
  };
  if (includeSelectedContext && selectedSiteResultItem && currentPanSourceEntry) {
    push(selectedSiteResultItem, currentPanSourceEntry);
  }
  const last = lastResolvedPlaybackContext && typeof lastResolvedPlaybackContext === 'object'
    ? lastResolvedPlaybackContext
    : null;
  if (last) {
    const cachedEntry = getSiteResultCacheEntry(detailStore, last.itemId);
    const siteItem = (cachedEntry && cachedEntry.siteItem) || (typeof getSiteItemById === 'function' ? getSiteItemById(last.itemId) : null);
    const detail = (cachedEntry && cachedEntry.detail) || (siteItem ? getSiteResultDetail(detailStore, siteItem) : null);
    const panEntry = detail
      ? buildPanSourcesFromDetail(detail).find((entry) => normalizeString(entry && entry.key) === normalizeString(last.panKey)) || null
      : null;
    push(siteItem, panEntry);
  }
  if (includeBrowseContext) {
    const lastBrowse = lastBrowsePlaybackContext && typeof lastBrowsePlaybackContext === 'object'
      ? lastBrowsePlaybackContext
      : null;
    if (lastBrowse) {
      const cachedEntry = getSiteResultCacheEntry(detailStore, lastBrowse.itemId);
      const siteItem = (cachedEntry && cachedEntry.siteItem) || (typeof getSiteItemById === 'function' ? getSiteItemById(lastBrowse.itemId) : null);
      const detail = (cachedEntry && cachedEntry.detail) || (siteItem ? getSiteResultDetail(detailStore, siteItem) : null);
      const panEntry = detail
        ? buildPanSourcesFromDetail(detail).find((entry) => normalizeString(entry && entry.key) === normalizeString(lastBrowse.panKey)) || null
        : null;
      push(siteItem, panEntry);
    }
  }
  return out;
};

const pickTargetCandidate = ({ candidates, panKey = '', isCandidateAllowed, compareCandidates = null } = {}) => (
  (Array.isArray(candidates) ? candidates : [])
    .filter((candidate) => {
      if (typeof isCandidateAllowed === 'function') {
        try {
          if (!isCandidateAllowed(candidate)) return false;
        } catch (_error) {
          return false;
        }
      }
      return !normalizeString(panKey) || normalizeString(candidate && candidate.panKey) === normalizeString(panKey);
    })
    .sort((left, right) => {
      if (typeof compareCandidates === 'function') {
        try {
          const next = compareCandidates(left, right);
          if (next !== 0) return next;
        } catch (_error) {}
      }
      return compareCollectedCandidateOrder(left, right);
    })[0] || null
);

const findCachedPlaybackTargetForPrimaryEpisode = ({
  matchOptions = null,
  globalEpisode,
  wantEpisodeInSeason = 0,
  contexts = [],
  collectCandidates,
  buildSelectionKey,
  isCandidateAllowed,
  compareCandidates,
} = {}) => {
  const matchKind = normalizeMatchKind(matchOptions);
  const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
  const targetLoose = Math.max(0, normalizeInt(wantEpisodeInSeason));
  if (matchKind === 'episode' && targetGlobal <= 0) return null;
  const list = Array.isArray(contexts) ? contexts : [];
  for (let i = 0; i < list.length; i += 1) {
    const context = list[i];
    const siteItem = context && context.siteItem ? context.siteItem : null;
    const panEntry = context && context.panEntry ? context.panEntry : null;
    const panKey = normalizeString(panEntry && panEntry.key);
    if (!siteItem || !panEntry || !panKey || !normalizeSourceSegments(panEntry).length) continue;
    const candidates = typeof collectCandidates === 'function'
      ? collectCandidates(siteItem, targetGlobal, targetLoose, matchOptions)
      : [];
    const picked = pickTargetCandidate({ candidates, panKey, isCandidateAllowed, compareCandidates });
    if (!picked) continue;
    const segment = buildPanSegment(panEntry, picked.itemIndex);
    if (!segment || !normalizeString(segment.episodeUrl)) continue;
    return {
      siteItem,
      panEntry,
      segment,
      candidate: picked && picked.candidate ? picked.candidate : null,
      selectionKey: typeof buildSelectionKey === 'function'
        ? buildSelectionKey(panKey, segment.index)
        : '',
    };
  }
  return null;
};

const findCachedPlaybackTargetAcrossStore = ({
  matchOptions = null,
  globalEpisode,
  wantEpisodeInSeason = 0,
  detailStore,
  getSiteItemById,
  collectCandidates,
  buildSelectionKey,
  isCandidateAllowed,
  ensureRecognitionForSiteItem,
  compareCandidates,
} = {}) => {
  const matchKind = normalizeMatchKind(matchOptions);
  const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
  const targetLoose = Math.max(0, normalizeInt(wantEpisodeInSeason));
  if (matchKind === 'episode' && targetGlobal <= 0) return null;
  const store = detailStore && typeof detailStore === 'object' ? detailStore : {};
  const entries = Object.entries(store);
  for (let i = 0; i < entries.length; i += 1) {
    const [itemId, entry] = entries[i];
    const cachedEntry = entry && typeof entry === 'object' ? entry : null;
    const detail = cachedEntry && cachedEntry.detail && typeof cachedEntry.detail === 'object' ? cachedEntry.detail : null;
    const siteItem = (cachedEntry && cachedEntry.siteItem)
      || (typeof getSiteItemById === 'function' ? getSiteItemById(itemId) : null);
    if (!detail || !siteItem) continue;
    if (typeof ensureRecognitionForSiteItem === 'function') {
      try {
        ensureRecognitionForSiteItem(siteItem, detail);
      } catch (_error) {}
    }
    const panSources = buildPanSourcesFromDetail(detail);
    for (let j = 0; j < panSources.length; j += 1) {
      const panEntry = panSources[j];
      const panKey = normalizeString(panEntry && panEntry.key);
      if (!panKey || !normalizeSourceSegments(panEntry).length) continue;
      const candidates = typeof collectCandidates === 'function'
        ? collectCandidates(siteItem, targetGlobal, targetLoose, matchOptions)
        : [];
      const picked = pickTargetCandidate({ candidates, panKey, isCandidateAllowed, compareCandidates });
      if (!picked) continue;
      const segment = buildPanSegment(panEntry, picked.itemIndex);
      if (!segment || !normalizeString(segment.episodeUrl)) continue;
      return {
        siteItem,
        panEntry,
        segment,
        candidate: picked && picked.candidate ? picked.candidate : null,
        selectionKey: typeof buildSelectionKey === 'function'
          ? buildSelectionKey(panKey, segment.index)
          : '',
      };
    }
  }
  return null;
};

export const resolveCachedPlaybackTarget = ({
  matchOptions = null,
  globalEpisode,
  wantEpisodeInSeason = 0,
  selectedSiteResultItem,
  currentPanSourceEntry,
  includeSelectedContext = true,
  lastResolvedPlaybackContext,
  lastBrowsePlaybackContext,
  includeBrowseContext = true,
  getSiteItemById,
  detailStore,
  collectCandidates,
  buildSelectionKey,
  isCandidateAllowed,
  ensureRecognitionForSiteItem,
  compareCandidates,
  includeStoreScan = true,
} = {}) => {
  const contexts = buildPlaybackContextCandidates({
    selectedSiteResultItem,
    currentPanSourceEntry,
    includeSelectedContext,
    lastResolvedPlaybackContext,
    lastBrowsePlaybackContext,
    includeBrowseContext,
    getSiteItemById,
    detailStore,
  });
  if (typeof ensureRecognitionForSiteItem === 'function') {
    contexts.forEach((context) => {
      const siteItem = context && context.siteItem ? context.siteItem : null;
      if (!siteItem) return;
      const detail = getSiteResultDetail(detailStore, siteItem);
      if (!detail) return;
      try {
        ensureRecognitionForSiteItem(siteItem, detail);
      } catch (_error) {}
    });
  }
  const contextHit = findCachedPlaybackTargetForPrimaryEpisode({
    matchOptions,
    globalEpisode,
    wantEpisodeInSeason,
    contexts,
    collectCandidates,
    buildSelectionKey,
    isCandidateAllowed,
    compareCandidates,
  });
  if (contextHit) return contextHit;
  if (!includeStoreScan) return null;
  return findCachedPlaybackTargetAcrossStore({
    matchOptions,
    globalEpisode,
    wantEpisodeInSeason,
    detailStore,
    getSiteItemById,
    collectCandidates,
    buildSelectionKey,
    isCandidateAllowed,
    ensureRecognitionForSiteItem,
    compareCandidates,
  });
};

export const resolveHistoryPlayFlagPlaybackTarget = async ({
  matchOptions = null,
  historyRow,
  globalEpisode,
  wantEpisodeInSeason = 0,
  runtimeSettings,
  smartEpisodeMapping,
  episodeSource = 'TMDB',
  allowResolutionModes = null,
  isCandidateAllowed,
  buildSelectionKey,
  getCachedSiteResultDetail,
  cacheHistoryDetail,
  collectCandidates,
  compareCandidates,
} = {}) => {
  const row = historyRow && typeof historyRow === 'object' ? historyRow : null;
  const matchKind = normalizeMatchKind(matchOptions);
  const playFlag = normalizeString(row && row.playFlag);
  const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
  const targetLoose = Math.max(0, normalizeInt(wantEpisodeInSeason));
  const allowedResolutionModes = Array.isArray(allowResolutionModes)
    ? allowResolutionModes.map((value) => normalizeString(value)).filter(Boolean)
    : [];
  const hasResolutionModeFilter = allowedResolutionModes.length > 0;
  if (!row || !playFlag || (matchKind === 'episode' && targetGlobal <= 0) || !playFlag.includes('-')) {
    return null;
  }
  const provider = normalizeString(panMockProviderFromFlag(playFlag)).toLowerCase();
  if (!provider) {
    return null;
  }
  const siteItem = buildHistorySitePlaybackItem(row);
  if (!siteItem) return null;

  let panEntry = null;
  if (typeof getCachedSiteResultDetail === 'function') {
    const cachedDetail = getCachedSiteResultDetail(siteItem);
    const cachedSources = buildPanSourcesFromDetail(cachedDetail);
    panEntry = cachedSources.find((entry) => normalizeString(entry && entry.label) === playFlag) || null;
    if (panEntry && typeof cacheHistoryDetail === 'function') {
      try {
        cacheHistoryDetail(siteItem, {
          detail: cachedDetail,
          cacheMeta: {
            entryKind: 'history-list-cache-hit',
            resolutionComplete: !!(cachedDetail && cachedDetail.resolutionComplete === true),
          },
        });
      } catch (_error) {}
    }
  }

  if (!panEntry) {
    const listData = await requestPanListByProviderFlag({ provider, playFlag }).catch(() => null);
    const vod = extractPanListVodPlayUrl(listData);
    if (!vod) {
      return null;
    }
    if (typeof setPanListCachedByProviderFlag === 'function' && listData && typeof listData === 'object') {
      setPanListCachedByProviderFlag({ provider, playFlag, data: listData });
    }
    const listLikeDetail = {
      sources: [{
        key: `history:${playFlag}`,
        label: playFlag,
        provider,
        sourceKind: 'panmock',
        sourceValue: vod,
        error: '',
        loading: false,
      }],
    };
    panEntry = buildPanSourcesFromDetail(listLikeDetail)[0] || null;
    if (!panEntry) return null;
  }
  if (typeof cacheHistoryDetail === 'function') {
    try {
      cacheHistoryDetail(siteItem, {
        detail: {
          sources: [panEntry],
          resolutionComplete: false,
        },
        cacheMeta: {
          entryKind: 'history-list',
          resolutionComplete: false,
        },
      });
    } catch (_error) {}
  }
  const candidates = typeof collectCandidates === 'function'
    ? collectCandidates(siteItem, targetGlobal, targetLoose, matchOptions)
    : [];
  const filteredCandidates = (Array.isArray(candidates) ? candidates : []).filter((wrapper) => {
    const candidate = wrapper && wrapper.candidate && typeof wrapper.candidate === 'object' ? wrapper.candidate : null;
    const resolutionMode = normalizeString(candidate && candidate.resolutionMode);
    if (hasResolutionModeFilter && (!resolutionMode || !allowedResolutionModes.includes(resolutionMode))) return false;
    return true;
  });
  const target = buildPlaybackTargetFromPanCandidates({
    siteItem,
    panEntry,
    candidates: filteredCandidates,
    buildSelectionKey,
    isCandidateAllowed,
    compareCandidates,
    fromHistoryPlayFlag: true,
  });
  return target;
};

const buildPlaybackTargetFromPanCandidates = ({
  siteItem,
  panEntry,
  candidates,
  buildSelectionKey,
  isCandidateAllowed,
  compareCandidates,
  fromHistoryPlayFlag = false,
  fromHistoryDetail = false,
} = {}) => {
  const item = siteItem && typeof siteItem === 'object' ? siteItem : null;
  const pan = panEntry && typeof panEntry === 'object' ? panEntry : null;
  const panKey = normalizeString(pan && pan.key);
  if (!item || !pan || !panKey || !normalizeSourceSegments(pan).length) return null;
  const picked = pickTargetCandidate({
    candidates: Array.isArray(candidates) ? candidates : [],
    panKey,
    isCandidateAllowed,
    compareCandidates,
  });
  if (!picked) return null;
  const segment = buildPanSegment(pan, picked.itemIndex);
  if (!segment || !normalizeString(segment.episodeUrl)) return null;
  return {
    siteItem: item,
    panEntry: pan,
    segment,
    candidate: picked && picked.candidate ? picked.candidate : null,
    selectionKey: typeof buildSelectionKey === 'function' ? buildSelectionKey(panKey, segment.index) : '',
    fromHistoryPlayFlag: !!fromHistoryPlayFlag,
    fromHistoryDetail: !!fromHistoryDetail,
  };
};

const findHistoryDetailPlaybackTarget = ({
  matchOptions = null,
  siteItem,
  detail,
  globalEpisode,
  wantEpisodeInSeason = 0,
  collectCandidates,
  buildSelectionKey,
  isCandidateAllowed,
  compareCandidates,
} = {}) => {
  const item = siteItem && typeof siteItem === 'object' ? siteItem : null;
  const payload = detail && typeof detail === 'object' ? detail : null;
  const matchKind = normalizeMatchKind(matchOptions);
  const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
  const targetLoose = Math.max(0, normalizeInt(wantEpisodeInSeason));
  if (!item || !payload || (matchKind === 'episode' && targetGlobal <= 0)) return null;
  const candidates = typeof collectCandidates === 'function'
    ? collectCandidates(item, targetGlobal, targetLoose, matchOptions)
    : [];
  const panSources = buildPanSourcesFromDetail(payload);
  for (let i = 0; i < panSources.length; i += 1) {
    const target = buildPlaybackTargetFromPanCandidates({
      siteItem: item,
      panEntry: panSources[i],
      candidates,
      buildSelectionKey,
      isCandidateAllowed,
      compareCandidates,
      fromHistoryDetail: true,
    });
    if (target) return target;
  }
  return null;
};

export const resolveHistoryBootstrapPlaybackTarget = async ({
  matchOptions = null,
  historyContext,
  globalEpisode,
  wantEpisodeInSeason = 0,
  skipHistoryList = false,
  runtimeSettings,
  smartEpisodeMapping,
  episodeSource = 'TMDB',
  allowResolutionModes = null,
  isCandidateAllowed,
  buildSelectionKey,
  getCachedSiteResultDetail,
  cacheHistoryDetail,
  ensureRecognitionForSiteItem,
  collectCandidates,
  ensureSiteResultDetailCached,
  compareCandidates,
} = {}) => {
  const context = historyContext && typeof historyContext === 'object' ? historyContext : null;
  const matchKind = normalizeMatchKind(matchOptions);
  const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
  if (!context || (matchKind === 'episode' && targetGlobal <= 0)) return null;
  const row = await ensurePlayHistoryRowForContext(context, { limit: 50 }).catch(() => null);
  if (!row) return null;
  const siteItem = buildHistorySitePlaybackItem(row);
  if (!siteItem) return null;
  if (!skipHistoryList) {
    const listHit = await resolveHistoryPlayFlagPlaybackTarget({
      matchOptions,
      historyRow: row,
      globalEpisode: targetGlobal,
      wantEpisodeInSeason,
      runtimeSettings,
      smartEpisodeMapping,
      episodeSource,
      allowResolutionModes,
      isCandidateAllowed,
      buildSelectionKey,
      getCachedSiteResultDetail,
      cacheHistoryDetail,
      collectCandidates,
      compareCandidates,
    });
    // Strictly separate history-list and history-detail chains:
    // first pass only resolves list target; detail is handled by the caller's next stage.
    if (listHit) return listHit;
    return null;
  }
  const tryResolveFromDetail = (detail) => {
    const payload = detail && typeof detail === 'object' ? detail : null;
    if (!payload) return null;
    if (typeof ensureRecognitionForSiteItem === 'function') {
      try {
        ensureRecognitionForSiteItem(siteItem, payload);
      } catch (_error) {}
    }
    return findHistoryDetailPlaybackTarget({
      matchOptions,
      siteItem,
      detail: payload,
      globalEpisode: targetGlobal,
      wantEpisodeInSeason,
      collectCandidates,
      buildSelectionKey,
      isCandidateAllowed,
      compareCandidates,
    });
  };
  if (typeof getCachedSiteResultDetail === 'function') {
    const cached = getCachedSiteResultDetail(siteItem);
    // In history-detail stage, only reuse fully-resolved detail cache.
    // Do not consume history-list partial cache (resolutionComplete=false),
    // otherwise detail chain may be skipped and order semantics drift.
    if (cached && cached.resolutionComplete === true) {
      const cachedHit = tryResolveFromDetail(cached);
      if (cachedHit) return cachedHit;
    }
  }
  if (typeof ensureSiteResultDetailCached !== 'function') return null;
  return new Promise((resolve) => {
    let settled = false;
    const settle = (value) => {
      if (settled) return;
      settled = true;
      resolve(value || null);
    };
    void ensureSiteResultDetailCached(siteItem, {
      onUpdate: (nextDetail) => {
        const hit = tryResolveFromDetail(nextDetail);
        if (hit) settle(hit);
      },
    }).then((detail) => {
      if (settled) return;
      settle(tryResolveFromDetail(detail));
    }).catch(() => settle(null));
  });
};

export const ensureSiteResultDetailCached = async ({
  item,
  store,
  runtimeSettings,
  timeoutMs = 15000,
  onUpdate,
  signal,
} = {}) => {
  const target = item && typeof item === 'object' ? item : null;
  if (!target) return null;
  const cached = getSiteResultDetail(store, target);
  if (cached && cached.resolutionComplete === true) {
    if (typeof onUpdate === 'function') onUpdate(cached);
    return cached;
  }
  const settings = runtimeSettings && typeof runtimeSettings === 'object' ? runtimeSettings : null;
  const apiBase = normalizeString(settings && settings.catpawrunnerApiBase);
  const spiderApi = normalizeString(target && target.spiderApi);
  const siteDetail = normalizeString(target && target.siteDetail);
  if (!apiBase || !spiderApi || !siteDetail) return null;
  const detail = await fetchCatResolvedDetailCached({
    apiBase,
    spiderApi,
    siteDetail,
    timeoutMs,
    signal,
    onUpdate: (nextDetail) => {
      const payload = nextDetail && typeof nextDetail === 'object' ? nextDetail : null;
      if (!payload) return;
      if (typeof onUpdate === 'function') onUpdate(payload);
    },
  });
  return detail && typeof detail === 'object' ? detail : null;
};
