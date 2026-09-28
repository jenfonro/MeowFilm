import { apiGetJson, apiPostJson, buildQuery } from './apiClient';
import { extractPanListVodPlayUrl, requestPanListByProviderFlag } from './catpawrunner';
import { normalizeInt, normalizeString } from './normalize';
import { panMockProviderFromFlag } from '../utils/matchCore';

const normalizeMatchKind = (options) => (normalizeString(options && options.kind).toLowerCase() === 'movie' ? 'movie' : 'episode');

const fetchEnabledManualItems = async (tmdbType, tmdbId) => {
  const type = normalizeString(tmdbType).toLowerCase();
  const id = Math.max(0, normalizeInt(tmdbId));
  if ((type !== 'tv' && type !== 'movie') || id <= 0) return [];
  const data = await apiGetJson(`/api/smart/manual/item/get-data${buildQuery({
    tmdbType: type,
    tmdbId: id,
    enabled: 1,
  })}`, {
    cacheMs: 0,
    dedupe: false,
  }).catch(() => null);
  const rows = Array.isArray(data && data.items) ? data.items : [];
  return rows.map((row) => {
    const current = row && typeof row === 'object' ? row : {};
    return {
      id: Math.max(0, normalizeInt(current.id)),
      siteKey: normalizeString(current.siteKey),
      spiderApi: normalizeString(current.spiderApi),
      siteDetail: normalizeString(current.siteDetail),
      panFlag: normalizeString(current.panFlag),
      seasonHint: normalizeString(current.seasonHint).toUpperCase(),
      enabled: current.enabled !== false,
    };
  }).filter((row) => (
    row.id > 0
    && row.enabled
    && (
      !!row.panFlag
      || (!!row.siteKey && !!row.spiderApi && !!row.siteDetail)
    )
  ));
};

const reportManualItemRequestResult = async (id, success) => {
  const itemId = Math.max(0, normalizeInt(id));
  if (itemId <= 0) return;
  await apiPostJson('/api/smart/manual/item/report-result', {
    id: itemId,
    success: !!success,
  }, {
    cacheMs: 0,
    dedupe: false,
  }).catch(() => null);
};

const buildManualBootstrapSiteItem = (row, mode = '', { query = '', title = '' } = {}) => {
  const current = row && typeof row === 'object' ? row : {};
  const itemId = Math.max(0, normalizeInt(current.id));
  const manualMode = normalizeString(mode);
  if (manualMode === 'panflag') {
    return {
      id: `manual:panflag:${itemId}`,
      siteKey: 'manual',
      spiderApi: '',
      siteDetail: `manual:panflag:${itemId}`,
      siteName: 'Manual',
      title: normalizeString(query) || normalizeString(title),
      groupKey: '',
      _manual: true,
      _manualItemId: itemId,
    };
  }
  return {
    id: `manual:site:${itemId}:${normalizeString(current.siteKey)}:${normalizeString(current.siteDetail)}`,
    siteKey: normalizeString(current.siteKey),
    spiderApi: normalizeString(current.spiderApi),
    siteDetail: normalizeString(current.siteDetail),
    siteName: normalizeString(current.siteKey) || 'Manual',
    title: normalizeString(query) || normalizeString(title),
    groupKey: '',
    _manual: true,
    _manualItemId: itemId,
  };
};

const buildManualListLikeDetail = (playFlag, provider, vodPlayUrl) => {
  const label = normalizeString(playFlag);
  const providerKey = normalizeString(provider).toLowerCase();
  const vod = normalizeString(vodPlayUrl);
  if (!label || !providerKey || !vod) return null;
  return {
    sources: [{
      key: `manual:${providerKey}:${label}`,
      label,
      provider: providerKey,
      sourceKind: 'panmock',
      sourceValue: vod,
      error: '',
      loading: false,
    }],
    resolutionComplete: false,
  };
};

export const tryManualSmartBootstrapRuntime = async ({
  isTmdbMode = false,
  tmdbType = '',
  searchType = '',
  tmdbId = 0,
  globalEpisode = 0,
  wantEpisodeInSeason = 0,
  matchOptions = null,
  selectedSiteSource = '',
  episodeSource = '',
  allowResolutionModes = null,
  isRunStopped = null,
  playSearchQuery = '',
  displayTitle = '',
  ensureSiteResultDetailCached = null,
  buildSiteDetailDedupeKey = null,
  setSiteResultDetailCacheEntry = null,
  cacheRecognitionForSiteResult = null,
  collectRecognitionCandidatesForTarget = null,
  onCandidatesChanged = null,
  shouldPauseDispatch = null,
  waitDispatchResume = null,
} = {}) => {
  if (!isTmdbMode) return true;
  const type = normalizeString(tmdbType || searchType).toLowerCase();
  const id = Math.max(0, normalizeInt(tmdbId));
  if ((type !== 'tv' && type !== 'movie') || id <= 0) return true;
  const matchKind = normalizeMatchKind(matchOptions);
  const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
  const targetLoose = Math.max(0, normalizeInt(wantEpisodeInSeason));
  if (matchKind === 'episode' && targetGlobal <= 0) return true;

  const stopped = () => (typeof isRunStopped === 'function' ? !!isRunStopped() : false);
  const waitDispatchReady = async () => {
    while (!stopped()) {
      const paused = typeof shouldPauseDispatch === 'function' ? !!shouldPauseDispatch() : false;
      if (!paused) return true;
      if (typeof waitDispatchResume === 'function') {
        await Promise.resolve(waitDispatchResume());
      } else {
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
    }
    return false;
  };
  const rows = await fetchEnabledManualItems(type, id);
  if (stopped()) return false;
  if (!rows.length) return true;

  const targetEpisodeSource = normalizeString(episodeSource) || normalizeString(selectedSiteSource);
  const resolutionModes = Array.isArray(allowResolutionModes)
    ? allowResolutionModes.map((value) => normalizeString(value)).filter(Boolean)
    : [];
  const detailCacheByKey = new Map();
  const registerCandidatesFromDetail = (siteItem, detail, seasonHint = '') => {
    if (stopped()) return;
    if (typeof cacheRecognitionForSiteResult === 'function') {
      cacheRecognitionForSiteResult(siteItem, detail, {
        manualSeasonHint: normalizeString(seasonHint).toUpperCase(),
      });
    }
    const wrappers = typeof collectRecognitionCandidatesForTarget === 'function'
      ? collectRecognitionCandidatesForTarget(siteItem, targetGlobal, targetLoose, {
        matchOptions,
        episodeSource: targetEpisodeSource,
        allowResolutionModes: resolutionModes,
      })
      : [];
    if (Array.isArray(wrappers) && wrappers.length > 0) {
      if (typeof onCandidatesChanged === 'function') {
        try {
          onCandidatesChanged();
        } catch (_error) {}
      }
    }
  };

  const runPanFlagRow = async (row) => {
    const playFlag = normalizeString(row && row.panFlag);
    const provider = normalizeString(panMockProviderFromFlag(playFlag)).toLowerCase();
    let requestSuccess = false;
    let requestStarted = false;
    try {
      if (stopped()) return;
      if (!playFlag || !provider) return;
      requestStarted = true;
      const listData = await requestPanListByProviderFlag({ provider, playFlag }).catch(() => null);
      requestSuccess = !!(listData && typeof listData === 'object');
      if (stopped()) return;
      const vod = extractPanListVodPlayUrl(listData);
      if (!vod) return;
      const detail = buildManualListLikeDetail(playFlag, provider, vod);
      if (!detail) return;
      const siteItem = buildManualBootstrapSiteItem(row, 'panflag', {
        query: playSearchQuery,
        title: displayTitle,
      });
      if (typeof setSiteResultDetailCacheEntry === 'function') {
        setSiteResultDetailCacheEntry(siteItem, detail, {
          entryKind: 'manual-list',
          resolutionComplete: false,
        });
      }
      registerCandidatesFromDetail(siteItem, detail, row && row.seasonHint);
    } finally {
      if (!requestStarted) return;
      await reportManualItemRequestResult(row && row.id, requestSuccess);
    }
  };

  const runSiteDetailRow = async (row) => {
    const siteItem = buildManualBootstrapSiteItem(row, 'site', {
      query: playSearchQuery,
      title: displayTitle,
    });
    const dedupeKey = typeof buildSiteDetailDedupeKey === 'function'
      ? buildSiteDetailDedupeKey(siteItem)
      : '';
    let requestSuccess = false;
    let requestStarted = false;
    try {
      if (stopped()) return;
      let detail = dedupeKey ? detailCacheByKey.get(dedupeKey) : null;
      if (!detail) {
        requestStarted = true;
        detail = typeof ensureSiteResultDetailCached === 'function'
          ? await ensureSiteResultDetailCached(siteItem).catch(() => null)
          : null;
        if (!detail || typeof detail !== 'object') return;
        requestSuccess = true;
        if (dedupeKey) detailCacheByKey.set(dedupeKey, detail);
      }
      if (stopped()) return;
      if (typeof setSiteResultDetailCacheEntry === 'function') {
        setSiteResultDetailCacheEntry(siteItem, detail, {
          entryKind: 'manual-detail',
          resolutionComplete: !!(detail && detail.resolutionComplete === true),
        });
      }
      registerCandidatesFromDetail(siteItem, detail, row && row.seasonHint);
    } finally {
      if (!requestStarted) return;
      await reportManualItemRequestResult(row && row.id, requestSuccess);
    }
  };

  const panFlagRows = [];
  const siteRowsByKey = new Map();
  rows.forEach((row) => {
    const current = row && typeof row === 'object' ? row : null;
    if (!current) return;
    if (normalizeString(current.panFlag)) {
      panFlagRows.push(current);
      return;
    }
    const siteKey = normalizeString(current.siteKey);
    if (!siteKey) return;
    if (!siteRowsByKey.has(siteKey)) siteRowsByKey.set(siteKey, []);
    siteRowsByKey.get(siteKey).push(current);
  });

  const tasks = [];
  panFlagRows.forEach((row) => {
    tasks.push(runPanFlagRow(row));
  });
  siteRowsByKey.forEach((groupRows) => {
    tasks.push((async () => {
      const list = Array.isArray(groupRows) ? groupRows : [];
      for (let i = 0; i < list.length; i += 1) {
        if (stopped()) break;
        const ready = await waitDispatchReady();
        if (!ready) break;
        await runSiteDetailRow(list[i]);
      }
    })());
  });
  if (!tasks.length) return true;
  await Promise.allSettled(tasks);
  if (stopped()) return false;
  return true;
};
