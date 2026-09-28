import { computed, ref } from 'vue';
import {
  buildDisplayedResults,
  clearSearchHistory,
  fetchSearchHistory,
  loadSearchConfig,
  saveSearchHistory,
  streamSearch,
} from './searchRuntime';

const STATUS_IDLE = 'idle';
const STATUS_LOADING = 'loading';
const STATUS_COMPLETED = 'completed';
const STATUS_ERROR = 'error';

const LANE_SITE = 'site';
const LANE_TMDB = 'tmdb';
const ALL_LANES = [LANE_SITE, LANE_TMDB];

const inputValue = ref('');
const activeQuery = ref('');
const rawListMode = ref(false);
const historyItems = ref([]);
const loading = ref(false);
const errorText = ref('');
const runtimeConfig = ref(null);
const searchState = ref({
  displayMode: 'sites',
  tmdbItems: [],
  siteItems: [],
  displayedItems: [],
  pinTmdbFirst: false,
  siteTotal: 0,
  progressDone: 0,
  progressTotal: 0,
});
const progressDone = ref(0);
const progressTotal = ref(0);

const queryEntryStore = new Map();
const queryListeners = new Map();
const laneListeners = new Map();
let queryRunTokenSeq = 0;
let uiOwnerScopedKey = '';
let configInFlight = null;
let historyLoaded = false;

const normalizeQueryKey = (value) => (typeof value === 'string' ? value.trim() : '');
const normalizeQueryScope = (value) => {
  const raw = normalizeQueryKey(value);
  return raw || 'default';
};
const normalizeLane = (value) => {
  const raw = normalizeQueryKey(value).toLowerCase();
  return raw === LANE_TMDB ? LANE_TMDB : LANE_SITE;
};
const buildScopedQueryKey = (scope, query) => `${normalizeQueryScope(scope)}::${normalizeQueryKey(query)}`;
const buildLaneListenerKey = (scopedKey, lane) => `${normalizeQueryKey(scopedKey)}::${normalizeLane(lane)}`;

const normalizeStatus = (value) => {
  const raw = normalizeQueryKey(value).toLowerCase();
  if (raw === STATUS_LOADING || raw === STATUS_COMPLETED || raw === STATUS_ERROR) return raw;
  return STATUS_IDLE;
};

const normalizeNumber = (value) => {
  const num = Number(value);
  return Number.isFinite(num) ? num : 0;
};

const createEmptySearchState = () => ({
  displayMode: runtimeConfig.value ? runtimeConfig.value.searchDisplayMode : 'sites',
  tmdbItems: [],
  siteItems: [],
  displayedItems: [],
  pinTmdbFirst: false,
  siteTotal: 0,
  progressDone: 0,
  progressTotal: 0,
});

const cloneSearchSnapshot = (state) => ({
  displayMode: state && state.displayMode ? state.displayMode : 'sites',
  tmdbItems: Array.isArray(state && state.tmdbItems) ? state.tmdbItems.slice() : [],
  siteItems: Array.isArray(state && state.siteItems) ? state.siteItems.slice() : [],
  displayedItems: Array.isArray(state && state.displayedItems) ? state.displayedItems.slice() : [],
  pinTmdbFirst: !!(state && state.pinTmdbFirst),
  siteTotal: normalizeNumber(state && state.siteTotal),
  progressDone: normalizeNumber(state && state.progressDone),
  progressTotal: normalizeNumber(state && state.progressTotal),
});

const createLaneState = () => ({
  status: STATUS_IDLE,
  snapshot: null,
  errorText: '',
  inflightPromise: null,
  runToken: 0,
});

const createEntry = (scopedKey) => ({
  scopedKey,
  lanes: {
    [LANE_SITE]: createLaneState(),
    [LANE_TMDB]: createLaneState(),
  },
});

const getOrCreateEntry = (scopedKey) => {
  const safeKey = normalizeQueryKey(scopedKey);
  if (!safeKey) return createEntry('');
  const cached = queryEntryStore.get(safeKey);
  if (cached && typeof cached === 'object') return cached;
  const next = createEntry(safeKey);
  queryEntryStore.set(safeKey, next);
  return next;
};

const readEntry = (scopedKey) => {
  const safeKey = normalizeQueryKey(scopedKey);
  if (!safeKey) return null;
  const cached = queryEntryStore.get(safeKey);
  return cached && typeof cached === 'object' ? cached : null;
};

const readEntryByQuery = (query, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return null;
  return readEntry(buildScopedQueryKey(scope, safeQuery));
};

const getLaneState = (entry, lane = LANE_SITE) => {
  const target = entry && entry.lanes && typeof entry.lanes === 'object' ? entry.lanes : null;
  if (!target) return null;
  const laneKey = normalizeLane(lane);
  const laneState = target[laneKey];
  return laneState && typeof laneState === 'object' ? laneState : null;
};

const getLaneStatus = (entry, lane = LANE_SITE) => {
  const laneState = getLaneState(entry, lane);
  return normalizeStatus(laneState && laneState.status);
};

const getLaneSnapshot = (entry, lane = LANE_SITE) => {
  const laneState = getLaneState(entry, lane);
  if (!laneState || !laneState.snapshot || typeof laneState.snapshot !== 'object') return null;
  return cloneSearchSnapshot(laneState.snapshot);
};

const getLaneErrorText = (entry, lane = LANE_SITE) => {
  const laneState = getLaneState(entry, lane);
  return normalizeQueryKey(laneState && laneState.errorText);
};

const resolveRequestedLanes = (displayMode) => {
  const mode = normalizeQueryKey(displayMode).toLowerCase();
  if (mode === 'tmdb') return [LANE_TMDB];
  if (mode === 'both') return [LANE_SITE, LANE_TMDB];
  return [LANE_SITE];
};

const buildLaneConfig = (config, lane = LANE_SITE) => ({
  ...config,
  searchDisplayMode: normalizeLane(lane) === LANE_TMDB ? 'tmdb' : 'sites',
});

const buildLaneSnapshot = (lane, state) => {
  const source = state && typeof state === 'object' ? state : {};
  const base = cloneSearchSnapshot(createEmptySearchState());
  if (normalizeLane(lane) === LANE_TMDB) {
    base.displayMode = 'tmdb';
    base.tmdbItems = Array.isArray(source.tmdbItems) ? source.tmdbItems.slice() : [];
    base.pinTmdbFirst = !!source.pinTmdbFirst;
    base.siteItems = [];
    base.siteTotal = 0;
  } else {
    base.displayMode = 'sites';
    base.siteItems = Array.isArray(source.siteItems) ? source.siteItems.slice() : [];
    base.siteTotal = normalizeNumber(source.siteTotal);
    if (base.siteTotal <= 0) base.siteTotal = base.siteItems.length;
    base.tmdbItems = [];
    base.pinTmdbFirst = false;
  }
  base.progressDone = normalizeNumber(source.progressDone);
  base.progressTotal = normalizeNumber(source.progressTotal);
  base.displayedItems = [];
  return base;
};

const buildMergedSnapshot = (entry) => {
  const siteSnapshot = getLaneSnapshot(entry, LANE_SITE);
  const tmdbSnapshot = getLaneSnapshot(entry, LANE_TMDB);
  if (!siteSnapshot && !tmdbSnapshot) return null;
  const base = cloneSearchSnapshot(createEmptySearchState());
  base.displayMode = siteSnapshot && tmdbSnapshot ? 'both' : (tmdbSnapshot ? 'tmdb' : 'sites');
  base.siteItems = siteSnapshot ? siteSnapshot.siteItems.slice() : [];
  base.siteTotal = siteSnapshot ? normalizeNumber(siteSnapshot.siteTotal) : 0;
  if (base.siteTotal <= 0) base.siteTotal = base.siteItems.length;
  base.tmdbItems = tmdbSnapshot ? tmdbSnapshot.tmdbItems.slice() : [];
  base.pinTmdbFirst = !!(tmdbSnapshot && tmdbSnapshot.pinTmdbFirst);
  base.progressDone = normalizeNumber(siteSnapshot && siteSnapshot.progressDone)
    + normalizeNumber(tmdbSnapshot && tmdbSnapshot.progressDone);
  base.progressTotal = normalizeNumber(siteSnapshot && siteSnapshot.progressTotal)
    + normalizeNumber(tmdbSnapshot && tmdbSnapshot.progressTotal);
  base.displayedItems = [];
  return base;
};

const getStatusForLanes = (entry, lanes = ALL_LANES) => {
  const targetLanes = Array.isArray(lanes) ? lanes.map((lane) => normalizeLane(lane)) : ALL_LANES;
  const statuses = targetLanes.map((lane) => getLaneStatus(entry, lane));
  if (statuses.includes(STATUS_LOADING)) return STATUS_LOADING;
  if (statuses.includes(STATUS_COMPLETED)) return STATUS_COMPLETED;
  if (statuses.includes(STATUS_ERROR)) return STATUS_ERROR;
  return STATUS_IDLE;
};

const getErrorTextForLanes = (entry, lanes = ALL_LANES) => {
  const targetLanes = Array.isArray(lanes) ? lanes.map((lane) => normalizeLane(lane)) : ALL_LANES;
  for (let i = 0; i < targetLanes.length; i += 1) {
    const message = getLaneErrorText(entry, targetLanes[i]);
    if (message) return message;
  }
  return '';
};

const isAnyLaneLoading = (entry, lanes = ALL_LANES) => getStatusForLanes(entry, lanes) === STATUS_LOADING;

const notifyQueryListeners = (query, snapshot, status, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return;
  const listeners = queryListeners.get(buildScopedQueryKey(scope, safeQuery));
  if (!listeners || !listeners.size) return;
  const nextSnapshot = snapshot ? cloneSearchSnapshot(snapshot) : null;
  const nextStatus = normalizeStatus(status);
  listeners.forEach((listener) => {
    try {
      listener(nextSnapshot, nextStatus);
    } catch (_error) {}
  });
};

const notifyLaneListeners = (query, lane, snapshot, status, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return;
  const scopedKey = buildScopedQueryKey(scope, safeQuery);
  const listeners = laneListeners.get(buildLaneListenerKey(scopedKey, lane));
  if (!listeners || !listeners.size) return;
  const nextSnapshot = snapshot ? cloneSearchSnapshot(snapshot) : null;
  const nextStatus = normalizeStatus(status);
  listeners.forEach((listener) => {
    try {
      listener(nextSnapshot, nextStatus);
    } catch (_error) {}
  });
};

const notifyByEntry = (query, scope, entry, lanes = ALL_LANES) => {
  const mergedSnapshot = buildMergedSnapshot(entry) || cloneSearchSnapshot(createEmptySearchState());
  const mergedStatus = getStatusForLanes(entry, ALL_LANES);
  notifyQueryListeners(query, mergedSnapshot, mergedStatus, scope);
  const targetLanes = Array.isArray(lanes) ? lanes : ALL_LANES;
  targetLanes.forEach((lane) => {
    const laneSnapshot = getLaneSnapshot(entry, lane) || buildLaneSnapshot(lane, null);
    const laneStatus = getLaneStatus(entry, lane);
    notifyLaneListeners(query, lane, laneSnapshot, laneStatus, scope);
  });
};

const resetSearchResultState = () => {
  errorText.value = '';
  searchState.value = createEmptySearchState();
  progressDone.value = 0;
  progressTotal.value = 0;
};

const applyUiFromMergedEntry = (scopedKey, { requestedLanes = ALL_LANES, forceLoading = false, fallbackEmpty = false } = {}) => {
  const entry = readEntry(scopedKey);
  const merged = buildMergedSnapshot(entry);
  if (!merged && !fallbackEmpty) return false;
  const nextSnapshot = merged || cloneSearchSnapshot(createEmptySearchState());
  searchState.value = nextSnapshot;
  progressDone.value = normalizeNumber(nextSnapshot.progressDone);
  progressTotal.value = normalizeNumber(nextSnapshot.progressTotal);
  const status = getStatusForLanes(entry, requestedLanes);
  if (status === STATUS_ERROR) {
    errorText.value = getErrorTextForLanes(entry, requestedLanes) || '搜索失败：未知错误';
  } else {
    errorText.value = '';
  }
  loading.value = forceLoading ? true : status === STATUS_LOADING;
  return true;
};

const hasLaneReusableSnapshot = (entry, lane) => {
  const status = getLaneStatus(entry, lane);
  const snapshot = getLaneSnapshot(entry, lane);
  return !!snapshot && (status === STATUS_COMPLETED || status === STATUS_ERROR);
};

const uniquePromises = (list) => Array.from(new Set((Array.isArray(list) ? list : []).filter(Boolean)));

const isUiOwner = (scopedKey) => normalizeQueryKey(uiOwnerScopedKey) === normalizeQueryKey(scopedKey);

const hasError = computed(() => !!errorText.value);

const displayedItems = computed(() => {
  if (!runtimeConfig.value) return [];
  return buildDisplayedResults(searchState.value, runtimeConfig.value, { rawListMode: rawListMode.value });
});

const showResultsSection = computed(() => !!activeQuery.value || loading.value || !!errorText.value);
const showHistorySection = computed(() => !activeQuery.value && historyItems.value.length > 0);

const showRawListToggle = computed(() => {
  if (!runtimeConfig.value) return false;
  if (runtimeConfig.value.searchDisplayMode === 'tmdb') return false;
  return searchState.value.siteItems.length > 0;
});

const progressText = computed(() => {
  if (!loading.value) return '';
  if (progressTotal.value <= 0) return '';
  return `(${progressDone.value}/${progressTotal.value})`;
});

const summaryText = computed(() => {
  if (!activeQuery.value || errorText.value) return '';
  return displayedItems.value.length ? `共 ${displayedItems.value.length} 条` : '';
});

const statusText = computed(() => {
  if (errorText.value) return errorText.value;
  if (!loading.value && activeQuery.value && !displayedItems.value.length) return '暂无搜索结果';
  return '';
});

export const ensureSearchSessionConfig = async (baseBootstrap = {}) => {
  if (runtimeConfig.value) return runtimeConfig.value;
  if (configInFlight) return configInFlight;
  configInFlight = loadSearchConfig(baseBootstrap)
    .then((config) => {
      runtimeConfig.value = config;
      return config;
    })
    .finally(() => {
      configInFlight = null;
    });
  return configInFlight;
};

export const refreshSearchSessionHistory = async () => {
  historyItems.value = await fetchSearchHistory().catch(() => []);
  historyLoaded = true;
  return historyItems.value;
};

export const ensureSearchSessionHistory = async () => {
  if (historyLoaded) return historyItems.value;
  return refreshSearchSessionHistory();
};

export const performSearchSessionSearch = async (
  query,
  baseBootstrap = {},
  {
    saveHistoryEnabled = true,
    blockedSiteKeys = [],
    affectUi = true,
    scope = 'default',
    searchDisplayModeOverride = '',
    contentKind = 'tv',
  } = {}
) => {
  const safeQuery = normalizeQueryKey(query);
  const safeScope = normalizeQueryScope(scope);
  const scopedKey = buildScopedQueryKey(safeScope, safeQuery);

  if (affectUi) {
    inputValue.value = safeQuery;
    activeQuery.value = safeQuery;
    uiOwnerScopedKey = safeQuery ? scopedKey : '';
  }
  const canUpdateUi = () => affectUi && isUiOwner(scopedKey);

  if (!safeQuery) {
    if (affectUi) {
      resetSearchResultState();
      loading.value = false;
    }
    return;
  }

  const config = await ensureSearchSessionConfig(baseBootstrap);
  const effectiveConfig = normalizeQueryKey(searchDisplayModeOverride)
    ? { ...config, searchDisplayMode: normalizeQueryKey(searchDisplayModeOverride) }
    : config;
  const requestedLanes = resolveRequestedLanes(effectiveConfig && effectiveConfig.searchDisplayMode);

  if (saveHistoryEnabled) {
    const nextHistory = await saveSearchHistory(safeQuery).catch(() => historyItems.value);
    historyItems.value = nextHistory;
    historyLoaded = true;
  }

  const entry = getOrCreateEntry(scopedKey);

  const lanePromises = [];
  const startLaneSearch = (lane) => {
    const laneKey = normalizeLane(lane);
    const laneState = getLaneState(entry, laneKey);
    if (!laneState) return;
    if (laneState.inflightPromise) {
      lanePromises.push(laneState.inflightPromise);
      return;
    }
    if (hasLaneReusableSnapshot(entry, laneKey)) return;

    const runToken = ++queryRunTokenSeq;
    laneState.runToken = runToken;
    laneState.status = STATUS_LOADING;
    laneState.errorText = '';

    notifyByEntry(safeQuery, safeScope, entry, [laneKey]);
    if (canUpdateUi()) {
      applyUiFromMergedEntry(scopedKey, {
        requestedLanes,
        forceLoading: true,
        fallbackEmpty: true,
      });
    }

    const laneConfig = buildLaneConfig(effectiveConfig, laneKey);
    const lanePromise = (async () => {
      try {
        await streamSearch(safeQuery, laneConfig, {
          blockedSiteKeys,
          contentKind,
          onUpdate(nextState) {
            const currentEntry = readEntry(scopedKey);
            const currentLaneState = getLaneState(currentEntry, laneKey);
            if (!currentLaneState || currentLaneState.runToken !== runToken) return;
            currentLaneState.snapshot = buildLaneSnapshot(laneKey, nextState);
            currentLaneState.status = STATUS_LOADING;
            currentLaneState.errorText = '';
            notifyByEntry(safeQuery, safeScope, currentEntry, [laneKey]);
            if (canUpdateUi()) {
              applyUiFromMergedEntry(scopedKey, {
                requestedLanes,
                forceLoading: isAnyLaneLoading(currentEntry, requestedLanes),
                fallbackEmpty: true,
              });
            }
          },
        });

        const currentEntry = readEntry(scopedKey);
        const currentLaneState = getLaneState(currentEntry, laneKey);
        if (!currentLaneState || currentLaneState.runToken !== runToken) return;
        currentLaneState.status = STATUS_COMPLETED;
        currentLaneState.errorText = '';
        if (!currentLaneState.snapshot) currentLaneState.snapshot = buildLaneSnapshot(laneKey, null);
        notifyByEntry(safeQuery, safeScope, currentEntry, [laneKey]);
        if (canUpdateUi()) {
          applyUiFromMergedEntry(scopedKey, {
            requestedLanes,
            forceLoading: isAnyLaneLoading(currentEntry, requestedLanes),
            fallbackEmpty: true,
          });
        }
      } catch (error) {
        const currentEntry = readEntry(scopedKey);
        const currentLaneState = getLaneState(currentEntry, laneKey);
        if (!currentLaneState || currentLaneState.runToken !== runToken) return;
        currentLaneState.status = STATUS_ERROR;
        currentLaneState.errorText = `搜索失败：${error && error.message ? error.message : '未知错误'}`;
        notifyByEntry(safeQuery, safeScope, currentEntry, [laneKey]);
        if (canUpdateUi()) {
          applyUiFromMergedEntry(scopedKey, {
            requestedLanes,
            forceLoading: isAnyLaneLoading(currentEntry, requestedLanes),
            fallbackEmpty: true,
          });
        }
      } finally {
        const currentEntry = readEntry(scopedKey);
        const currentLaneState = getLaneState(currentEntry, laneKey);
        if (currentLaneState && currentLaneState.runToken === runToken) {
          currentLaneState.inflightPromise = null;
        }
        if (canUpdateUi()) {
          const latestEntry = readEntry(scopedKey);
          loading.value = isAnyLaneLoading(latestEntry, requestedLanes);
        }
      }
    })();

    laneState.inflightPromise = lanePromise;
    lanePromises.push(lanePromise);
  };

  requestedLanes.forEach((lane) => startLaneSearch(lane));

  if (canUpdateUi()) {
    const forceLoading = requestedLanes.some((lane) => getLaneStatus(entry, lane) === STATUS_LOADING);
    applyUiFromMergedEntry(scopedKey, {
      requestedLanes,
      forceLoading,
      fallbackEmpty: true,
    });
  }

  const waitPromises = uniquePromises(lanePromises);
  if (!waitPromises.length) {
    return buildMergedSnapshot(entry) || cloneSearchSnapshot(createEmptySearchState());
  }
  return Promise.all(waitPromises).then(() => {
    const latestEntry = readEntry(scopedKey);
    return buildMergedSnapshot(latestEntry) || cloneSearchSnapshot(createEmptySearchState());
  });
};

export const clearSearchSessionQuery = async () => {
  inputValue.value = '';
  activeQuery.value = '';
  uiOwnerScopedKey = '';
  resetSearchResultState();
  await refreshSearchSessionHistory();
};

export const clearSearchSessionHistory = async () => {
  historyItems.value = await clearSearchHistory().catch(() => historyItems.value);
  historyLoaded = true;
  return historyItems.value;
};

export const getSearchSessionQueryStatus = (query, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return STATUS_IDLE;
  const entry = readEntryByQuery(safeQuery, scope);
  const status = getStatusForLanes(entry, ALL_LANES);
  if (status !== STATUS_IDLE) return status;
  if (normalizeQueryScope(scope) === 'default' && normalizeQueryKey(activeQuery.value) === safeQuery && loading.value) {
    return STATUS_LOADING;
  }
  return STATUS_IDLE;
};

export const getSearchSessionQuerySnapshot = (query, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return null;
  if (normalizeQueryScope(scope) === 'default' && normalizeQueryKey(activeQuery.value) === safeQuery) {
    return cloneSearchSnapshot({
      ...searchState.value,
      progressDone: progressDone.value,
      progressTotal: progressTotal.value,
    });
  }
  const entry = readEntryByQuery(safeQuery, scope);
  return buildMergedSnapshot(entry);
};

export const getSearchSessionRuntimeQueryStatus = (query, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return STATUS_IDLE;
  const entry = readEntryByQuery(safeQuery, scope);
  return getStatusForLanes(entry, ALL_LANES);
};

export const getSearchSessionRuntimeQuerySnapshot = (query, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return null;
  const entry = readEntryByQuery(safeQuery, scope);
  return buildMergedSnapshot(entry);
};

export const getSearchSessionAnyQueryStatus = (query, scope = 'default') => {
  const safeScope = normalizeQueryScope(scope);
  return safeScope === 'default'
    ? getSearchSessionQueryStatus(query, safeScope)
    : getSearchSessionRuntimeQueryStatus(query, safeScope);
};

export const getSearchSessionAnyQuerySnapshot = (query, scope = 'default') => {
  const safeScope = normalizeQueryScope(scope);
  return safeScope === 'default'
    ? getSearchSessionQuerySnapshot(query, safeScope)
    : getSearchSessionRuntimeQuerySnapshot(query, safeScope);
};

export const getSearchSessionLaneStatus = (query, scope = 'default', lane = LANE_SITE) => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return STATUS_IDLE;
  const entry = readEntryByQuery(safeQuery, scope);
  return getLaneStatus(entry, lane);
};

export const getSearchSessionLaneSnapshot = (query, scope = 'default', lane = LANE_SITE) => {
  const safeQuery = normalizeQueryKey(query);
  if (!safeQuery) return null;
  const entry = readEntryByQuery(safeQuery, scope);
  return getLaneSnapshot(entry, lane);
};

export const subscribeSearchSessionQuery = (query, listener, scope = 'default') => {
  const safeQuery = normalizeQueryKey(query);
  const safeScope = normalizeQueryScope(scope);
  const fn = typeof listener === 'function' ? listener : null;
  if (!safeQuery || !fn) return () => {};
  const scopedKey = buildScopedQueryKey(safeScope, safeQuery);
  if (!queryListeners.has(scopedKey)) queryListeners.set(scopedKey, new Set());
  const listeners = queryListeners.get(scopedKey);
  listeners.add(fn);
  const status = getSearchSessionAnyQueryStatus(safeQuery, safeScope);
  const snapshot = getSearchSessionAnyQuerySnapshot(safeQuery, safeScope);
  try {
    fn(snapshot, status);
  } catch (_error) {}
  return () => {
    const current = queryListeners.get(scopedKey);
    if (!current) return;
    current.delete(fn);
    if (!current.size) queryListeners.delete(scopedKey);
  };
};

export const subscribeSearchSessionRuntimeQuery = (query, listener, scope = 'default') => {
  return subscribeSearchSessionQuery(query, listener, scope);
};

export const subscribeSearchSessionLane = (query, scope = 'default', lane = LANE_SITE, listener) => {
  const safeQuery = normalizeQueryKey(query);
  const safeScope = normalizeQueryScope(scope);
  const safeLane = normalizeLane(lane);
  const fn = typeof listener === 'function' ? listener : null;
  if (!safeQuery || !fn) return () => {};
  const scopedKey = buildScopedQueryKey(safeScope, safeQuery);
  const listenerKey = buildLaneListenerKey(scopedKey, safeLane);
  if (!laneListeners.has(listenerKey)) laneListeners.set(listenerKey, new Set());
  const listeners = laneListeners.get(listenerKey);
  listeners.add(fn);
  const status = getSearchSessionLaneStatus(safeQuery, safeScope, safeLane);
  const snapshot = getSearchSessionLaneSnapshot(safeQuery, safeScope, safeLane);
  try {
    fn(snapshot, status);
  } catch (_error) {}
  return () => {
    const current = laneListeners.get(listenerKey);
    if (!current) return;
    current.delete(fn);
    if (!current.size) laneListeners.delete(listenerKey);
  };
};

export const useSearchSession = () => ({
  inputValue,
  activeQuery,
  rawListMode,
  historyItems,
  loading,
  errorText,
  runtimeConfig,
  searchState,
  progressDone,
  progressTotal,
  hasError,
  displayedItems,
  showResultsSection,
  showHistorySection,
  showRawListToggle,
  progressText,
  summaryText,
  statusText,
  ensureConfig: ensureSearchSessionConfig,
  ensureHistory: ensureSearchSessionHistory,
  refreshHistory: refreshSearchSessionHistory,
  performSearch: performSearchSessionSearch,
  clearQuery: clearSearchSessionQuery,
  clearHistory: clearSearchSessionHistory,
});
