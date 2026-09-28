import { reactive } from 'vue';
import { apiDeleteJson, apiGetJson, apiInvalidateCache, apiPostJson, buildQuery } from './apiClient';
import { normalizeInt, normalizeString } from './normalize';
const toTicks = (seconds) => {
  const value = Number(seconds);
  if (!Number.isFinite(value) || value <= 0) return 0;
  return Math.floor(value * 10_000_000);
};
const fromTicks = (ticks) => {
  const value = Number(ticks);
  if (!Number.isFinite(value) || value <= 0) return 0;
  return Math.floor(value / 10_000_000);
};

const buildEpisodePlaybackItemId = ({ tmdbType = '', tmdbId = 0, season = 0, episode = 0 } = {}) => {
  const type = normalizeString(tmdbType).toLowerCase();
  const id = Math.max(0, normalizeInt(tmdbId));
  if (id <= 0) return '';
  if (type === 'movie') return `tmdb_movie_${id}`;
  if (type !== 'tv') return '';
  const seasonNo = Math.max(0, normalizeInt(season));
  const episodeNo = Math.max(0, normalizeInt(episode));
  if (seasonNo <= 0 || episodeNo <= 0) return '';
  return `tmdb_tv_${id}_s${String(seasonNo).padStart(2, '0')}_e${String(episodeNo).padStart(3, '0')}`;
};

const buildHistoryIdentity = (context) => {
  const target = context && typeof context === 'object' ? context : null;
  if (!target) return '';
  const contentKey = normalizeString(target.contentKey).toLowerCase();
  const globalEpisode = Math.max(0, normalizeInt(target.globalEpisode));
  if (contentKey && globalEpisode > 0) return `${contentKey}::ep:${globalEpisode}`;
  const siteKey = normalizeString(target.siteKey);
  const siteDetail = normalizeString(target.siteDetail);
  if (!siteKey || !siteDetail) return '';
  const selectionKey = normalizeString(target.selectionKey);
  if (selectionKey) return `${siteKey}::${siteDetail}::selection:${selectionKey}`;
  const playFlag = normalizeString(target.playFlag);
  const siteEpisodeIndex = Math.max(0, normalizeInt(target.siteEpisodeIndex));
  if (playFlag) return `${siteKey}::${siteDetail}::flag:${playFlag}::${siteEpisodeIndex}`;
  return `${siteKey}::${siteDetail}::idx:${siteEpisodeIndex}`;
};

const buildHistoryCommitKey = (context) => {
  const target = context && typeof context === 'object' ? context : null;
  if (!target) return '';
  return [
    normalizeString(target.siteKey),
    normalizeString(target.spiderApi),
    normalizeString(target.siteDetail),
    normalizeString(target.playFlag),
    String(Math.max(0, normalizeInt(target.siteEpisodeIndex))),
    normalizeString(target.playbackItemId),
    normalizeString(target.identity),
  ].join('::');
};

export const playHistoryListState = reactive({
  items: [],
  at: 0,
  dirty: true,
  loading: false,
  error: '',
});

const historyListState = {
  inFlight: null,
  requestedLimit: 0,
};

const historyCommitState = {
  key: '',
  inFlight: null,
};

const historyProgressState = {
  at: 0,
  inFlight: null,
};

const cloneHistoryRows = (items) => (Array.isArray(items) ? items.map((item) => ({ ...(item || {}) })) : []);

const emitPlayHistoryUpdated = () => {
  if (typeof window !== 'undefined' && typeof window.dispatchEvent === 'function') {
    window.dispatchEvent(new CustomEvent('tv:play-history-updated'));
  }
};

const refreshPlayHistoryListFromServer = async () => {
  playHistoryListState.dirty = true;
  const requested = Math.max(0, normalizeInt(historyListState.requestedLimit));
  const limit = Math.max(50, requested || 0);
  await ensurePlayHistoryItems({ limit, force: true });
  emitPlayHistoryUpdated();
};

const removePlayHistoryRowLocally = ({ contentKey = '', siteKey = '', siteDetail = '' } = {}) => {
  const normalizedContentKey = normalizeString(contentKey);
  const normalizedSiteKey = normalizeString(siteKey);
  const normalizedSiteDetail = normalizeString(siteDetail);
  const list = Array.isArray(playHistoryListState.items) ? playHistoryListState.items : [];
  const nextItems = list.filter((item) => {
    if (!item || typeof item !== 'object') return false;
    if (normalizedContentKey) return normalizeString(item.contentKey) !== normalizedContentKey;
    return !(
      normalizeString(item.siteKey) === normalizedSiteKey
      && normalizeString(item.siteDetail) === normalizedSiteDetail
    );
  });
  updateHistoryListState({
    items: nextItems,
    at: Date.now(),
    dirty: false,
    error: '',
  });
  return nextItems;
};

const sameBaseHistoryTarget = (row, context) => {
  const item = row && typeof row === 'object' ? row : null;
  const target = context && typeof context === 'object' ? context : null;
  if (!item || !target) return false;
  const targetContentKey = normalizeString(target.contentKey).toLowerCase();
  if (!targetContentKey) return false;
  return normalizeString(item.contentKey).toLowerCase() === targetContentKey;
};

const sameEpisodeHistoryTarget = (row, context) => {
  const item = row && typeof row === 'object' ? row : null;
  const target = context && typeof context === 'object' ? context : null;
  if (!item || !target || !sameBaseHistoryTarget(item, target)) return false;

  const rowPlaybackItemId = normalizeString(item.playbackItemId);
  const targetPlaybackItemId = normalizeString(target.playbackItemId);
  if (rowPlaybackItemId && targetPlaybackItemId) return rowPlaybackItemId === targetPlaybackItemId;

  const rowGlobalEpisode = Math.max(0, normalizeInt(item.globalEpisode));
  const targetGlobalEpisode = Math.max(0, normalizeInt(target.globalEpisode));
  if (rowGlobalEpisode > 0 && targetGlobalEpisode > 0) return rowGlobalEpisode === targetGlobalEpisode;

  const rowTMDBType = normalizeString(item.tmdbType).toLowerCase();
  const targetTMDBType = normalizeString(target.tmdbType).toLowerCase();
  const rowTMDBID = Math.max(0, normalizeInt(item.tmdbId));
  const targetTMDBID = Math.max(0, normalizeInt(target.tmdbId));
  if (rowTMDBID > 0 && targetTMDBID > 0 && rowTMDBID === targetTMDBID && rowTMDBType === targetTMDBType) {
    if (rowTMDBType === 'movie') return true;
    const rowSeason = Math.max(0, normalizeInt(item.tmdbSeason));
    const rowEpisode = Math.max(0, normalizeInt(item.tmdbEpisode));
    const targetSeason = Math.max(0, normalizeInt(target.tmdbSeason));
    const targetEpisode = Math.max(0, normalizeInt(target.tmdbEpisode));
    if (rowSeason > 0 && rowEpisode > 0 && targetSeason > 0 && targetEpisode > 0) {
      return rowSeason === targetSeason && rowEpisode === targetEpisode;
    }
  }

  const rowSiteKey = normalizeString(item.siteKey);
  const targetSiteKey = normalizeString(target.siteKey);
  const rowSiteDetail = normalizeString(item.siteDetail);
  const targetSiteDetail = normalizeString(target.siteDetail);
  if (!rowSiteKey || !targetSiteKey || !rowSiteDetail || !targetSiteDetail) return false;
  if (rowSiteKey !== targetSiteKey || rowSiteDetail !== targetSiteDetail) return false;

  const rowSelectionKey = normalizeString(item.selectionKey);
  const targetSelectionKey = normalizeString(target.selectionKey);
  if (rowSelectionKey && targetSelectionKey) return rowSelectionKey === targetSelectionKey;

  const rowEpisodeIndex = Math.max(0, normalizeInt(item.siteEpisodeIndex));
  const targetEpisodeIndex = Math.max(0, normalizeInt(target.siteEpisodeIndex));
  if (rowEpisodeIndex > 0 && targetEpisodeIndex > 0) return rowEpisodeIndex === targetEpisodeIndex;

  const rowEpisodeFile = normalizeString(item.siteEpisodeFile);
  const targetEpisodeFile = normalizeString(target.siteEpisodeFile);
  if (rowEpisodeFile && targetEpisodeFile) return rowEpisodeFile === targetEpisodeFile;

  return false;
};

const updateHistoryListState = ({ items, at = Date.now(), dirty = false, error = '' } = {}) => {
  playHistoryListState.items = cloneHistoryRows(items);
  playHistoryListState.at = Math.max(0, normalizeInt(at)) || Date.now();
  playHistoryListState.dirty = !!dirty;
  playHistoryListState.error = normalizeString(error);
  return playHistoryListState.items;
};

export const seedPlayHistoryItems = (items, { at = Date.now(), dirty = false } = {}) => {
  const nextItems = cloneHistoryRows(items);
  if (!nextItems.length) return playHistoryListState.items;
  historyListState.requestedLimit = Math.max(historyListState.requestedLimit, nextItems.length);
  if (!playHistoryListState.items.length || Math.max(0, normalizeInt(at)) >= Math.max(0, normalizeInt(playHistoryListState.at))) {
    updateHistoryListState({ items: nextItems, at, dirty, error: '' });
  }
  return playHistoryListState.items;
};

export const ensurePlayHistoryItems = async ({ limit = 50, force = false } = {}) => {
  const targetLimit = Math.max(1, normalizeInt(limit) || 50);
  if (
    !force
    && !playHistoryListState.dirty
    && Array.isArray(playHistoryListState.items)
    && playHistoryListState.items.length > 0
    && historyListState.requestedLimit >= targetLimit
  ) {
    return playHistoryListState.items.slice(0, targetLimit);
  }
  if (!historyListState.inFlight) {
    historyListState.requestedLimit = Math.max(historyListState.requestedLimit, targetLimit);
    playHistoryListState.loading = true;
    playHistoryListState.error = '';
    historyListState.inFlight = (async () => {
      try {
        const list = await apiGetJson(`/api/playhistory${buildQuery({ limit: targetLimit })}`, { cacheMs: 0 });
        updateHistoryListState({
          items: Array.isArray(list) ? list : [],
          at: Date.now(),
          dirty: false,
          error: '',
        });
      } catch (error) {
        playHistoryListState.error = error && error.message ? String(error.message) : '历史记录加载失败';
      } finally {
        playHistoryListState.loading = false;
      }
    })();
    historyListState.inFlight.finally(() => {
      historyListState.inFlight = null;
    });
  }
  await historyListState.inFlight.catch(() => null);
  if (!force && historyListState.requestedLimit < targetLimit) {
    return ensurePlayHistoryItems({ limit: targetLimit, force: true });
  }
  return playHistoryListState.items.slice(0, targetLimit);
};

export const deletePlayHistoryItem = async ({ contentKey = '', siteKey = '', siteDetail = '' } = {}) => {
  const normalizedContentKey = normalizeString(contentKey);
  const normalizedSiteKey = normalizeString(siteKey);
  const normalizedSiteDetail = normalizeString(siteDetail);
  if (!normalizedContentKey && (!normalizedSiteKey || !normalizedSiteDetail)) {
    throw new Error('缺少历史记录标识');
  }
  const query = normalizedContentKey
    ? { contentKey: normalizedContentKey }
    : { siteKey: normalizedSiteKey, siteDetail: normalizedSiteDetail };
  const data = await apiDeleteJson(`/api/playhistory${buildQuery(query)}`, { dedupe: false });
  if (data && typeof data === 'object' && !Array.isArray(data) && data.success === false) {
    throw new Error(normalizeString(data.message || data.error || data.msg) || '删除失败');
  }
  apiInvalidateCache({ urlPrefix: '/api/playhistory', method: 'GET' });
  removePlayHistoryRowLocally({
    contentKey: normalizedContentKey,
    siteKey: normalizedSiteKey,
    siteDetail: normalizedSiteDetail,
  });
  emitPlayHistoryUpdated();
};

export const warmPlayHistoryForContext = async (context = {}, { limit = 50 } = {}) => {
  const target = context && typeof context === 'object' ? context : null;
  if (!target) return playHistoryListState.items;
  const existing = Array.isArray(playHistoryListState.items) ? playHistoryListState.items : [];
  if (existing.some((item) => sameBaseHistoryTarget(item, target))) return existing;
  return ensurePlayHistoryItems({ limit, force: false });
};

export const findPlayHistoryRowForContext = (context = {}, { sameEpisodeOnly = false } = {}) => {
  const target = context && typeof context === 'object' ? context : null;
  if (!target) return null;
  const list = Array.isArray(playHistoryListState.items) ? playHistoryListState.items : [];
  if (sameEpisodeOnly) {
    return list.find((item) => sameEpisodeHistoryTarget(item, target)) || null;
  }
  return list.find((item) => sameBaseHistoryTarget(item, target)) || null;
};

export const ensurePlayHistoryRowForContext = async (context = {}, { limit = 50, sameEpisodeOnly = false } = {}) => {
  const target = context && typeof context === 'object' ? context : null;
  if (!target) return null;
  const existing = findPlayHistoryRowForContext(target, { sameEpisodeOnly });
  if (existing) return existing;
  await warmPlayHistoryForContext(target, { limit });
  return findPlayHistoryRowForContext(target, { sameEpisodeOnly });
};

export const playHistorySessionState = reactive({
  activeContext: {
    identity: '',
    reportEnabled: false,
    contentKey: '',
    siteKey: '',
    siteName: '',
    spiderApi: '',
    siteDetail: '',
    Poster: '',
    Remark: '',
    tmdbId: 0,
    tmdbType: '',
    tmdbSeason: 0,
    tmdbEpisode: 0,
    globalEpisode: 0,
    playFlag: '',
    siteEpisodeIndex: 0,
    siteEpisodeFile: '',
    preOrder: false,
    playbackItemId: '',
    selectionKey: '',
  },
  playbackState: {
    started: false,
    ready: false,
  },
  playerTime: {
    currentTime: 0,
    duration: 0,
    at: 0,
    playing: false,
  },
  resumePlan: {
    identity: '',
    seconds: 0,
    applied: false,
    tryKey: '',
    tryAt: 0,
    frozen: false,
  },
  resumeOverride: {
    identity: '',
    seconds: 0,
    at: 0,
  },
});

const setResumePlan = ({ identity = '', seconds = 0 } = {}) => {
  playHistorySessionState.resumePlan = {
    identity: normalizeString(identity),
    seconds: Math.max(0, normalizeInt(seconds)),
    applied: false,
    tryKey: '',
    tryAt: 0,
    frozen: false,
  };
  return playHistorySessionState.resumePlan;
};

const rememberResumeOverride = (identity, seconds) => {
  const nextIdentity = normalizeString(identity);
  const nextSeconds = Math.max(0, normalizeInt(seconds));
  if (!nextIdentity || nextSeconds <= 0) return;
  playHistorySessionState.resumeOverride = {
    identity: nextIdentity,
    seconds: nextSeconds,
    at: Date.now(),
  };
};

const readResumeSecondsFromHistory = async (context) => {
  const target = context && typeof context === 'object' ? context : null;
  if (!target) return 0;
  const now = Date.now();
  if (
    !playHistoryListState.dirty
    && playHistoryListState.at > 0
    && now - playHistoryListState.at < 15_000
    && Array.isArray(playHistoryListState.items)
  ) {
    const hit = playHistoryListState.items.find((item) => sameEpisodeHistoryTarget(item, target)) || null;
    return fromTicks(hit && hit.playbackPositionTicks);
  }
  try {
    await warmPlayHistoryForContext(target, { limit: 50 });
  } catch (_error) {
    return 0;
  }
  const hit = (Array.isArray(playHistoryListState.items) ? playHistoryListState.items : []).find((item) => sameEpisodeHistoryTarget(item, target)) || null;
  return fromTicks(hit && hit.playbackPositionTicks);
};

export const preparePlayHistoryContext = async (payload = {}) => {
  const raw = payload && typeof payload === 'object' ? payload : {};
  const current = playHistorySessionState.activeContext && typeof playHistorySessionState.activeContext === 'object'
    ? playHistorySessionState.activeContext
    : {};
  const currentIdentity = normalizeString(current.identity);
  const currentSeconds = Math.max(0, normalizeInt(playHistorySessionState.playerTime.currentTime));
  if (currentIdentity && currentSeconds > 0) {
    rememberResumeOverride(currentIdentity, currentSeconds);
  }

  const nextContext = {
    identity: '',
    reportEnabled: !!raw.reportEnabled,
    contentKey: normalizeString(raw.contentKey),
    siteKey: normalizeString(raw.siteKey),
    siteName: normalizeString(raw.siteName),
    spiderApi: normalizeString(raw.spiderApi),
    siteDetail: normalizeString(raw.siteDetail),
    Poster: normalizeString(raw.Poster),
    Remark: normalizeString(raw.Remark),
    tmdbId: Math.max(0, normalizeInt(raw.tmdbId)),
    tmdbType: normalizeString(raw.tmdbType).toLowerCase(),
    tmdbSeason: Math.max(0, normalizeInt(raw.tmdbSeason)),
    tmdbEpisode: Math.max(0, normalizeInt(raw.tmdbEpisode)),
    globalEpisode: Math.max(0, normalizeInt(raw.globalEpisode)),
    playFlag: normalizeString(raw.playFlag),
    siteEpisodeIndex: Math.max(0, normalizeInt(raw.siteEpisodeIndex)),
    siteEpisodeFile: normalizeString(raw.siteEpisodeFile),
    preOrder: !!raw.preOrder,
    playbackItemId: normalizeString(raw.playbackItemId),
    selectionKey: normalizeString(raw.selectionKey),
  };
  nextContext.identity = buildHistoryIdentity(nextContext);
  playHistorySessionState.activeContext = nextContext;
  playHistorySessionState.playbackState = {
    started: false,
    ready: false,
  };

  if (!nextContext.identity || !nextContext.reportEnabled) {
    setResumePlan({});
    return nextContext;
  }

  const override = playHistorySessionState.resumeOverride && typeof playHistorySessionState.resumeOverride === 'object'
    ? playHistorySessionState.resumeOverride
    : null;
  const overrideIdentity = normalizeString(override && override.identity);
  const overrideSeconds = Math.max(0, normalizeInt(override && override.seconds));
  const overrideAt = Math.max(0, normalizeInt(override && override.at));
  if (
    overrideIdentity
    && overrideIdentity === nextContext.identity
    && overrideSeconds > 0
    && overrideAt > 0
    && Date.now() - overrideAt < 6 * 60 * 60 * 1000
  ) {
    setResumePlan({ identity: nextContext.identity, seconds: overrideSeconds });
    return nextContext;
  }

  const resumeSeconds = await readResumeSecondsFromHistory(nextContext);
  setResumePlan({ identity: nextContext.identity, seconds: resumeSeconds });
  return nextContext;
};

export const clearActivePlayHistoryContext = () => {
  playHistorySessionState.activeContext = {
    identity: '',
    reportEnabled: false,
    contentKey: '',
    siteKey: '',
    siteName: '',
    spiderApi: '',
    siteDetail: '',
    Poster: '',
    Remark: '',
    tmdbId: 0,
    tmdbType: '',
    tmdbSeason: 0,
    tmdbEpisode: 0,
    globalEpisode: 0,
    playFlag: '',
    siteEpisodeIndex: 0,
    siteEpisodeFile: '',
    preOrder: false,
    playbackItemId: '',
    selectionKey: '',
  };
  playHistorySessionState.playbackState = {
    started: false,
    ready: false,
  };
  setResumePlan({});
};

export const onPlayerHistoryTimeUpdate = (info = {}) => {
  const currentTime = Math.max(0, Number(info && info.currentTime) || 0);
  const duration = Math.max(0, Number(info && info.duration) || 0);
  playHistorySessionState.playerTime = {
    currentTime,
    duration,
    at: Date.now(),
    playing: !!(info && info.playing),
  };
};

const commitHistoryBaseIfNeeded = async (reason = '') => {
  const context = playHistorySessionState.activeContext && typeof playHistorySessionState.activeContext === 'object'
    ? playHistorySessionState.activeContext
    : null;
  if (!context || !context.reportEnabled) return;
  const key = buildHistoryCommitKey(context);
  if (!key) return;
  if (historyCommitState.key === key || historyCommitState.inFlight) return;
  historyCommitState.key = key;
  historyCommitState.inFlight = (async () => {
    try {
      await apiPostJson('/api/playhistory', {
        contentKey: context.contentKey,
        siteKey: context.siteKey,
        siteName: context.siteName,
        spiderApi: context.spiderApi,
        siteDetail: context.siteDetail,
        Poster: context.Poster,
        Remark: context.Remark,
        tmdbId: context.tmdbId,
        tmdbType: context.tmdbType,
        tmdbSeason: context.tmdbSeason,
        tmdbEpisode: context.tmdbEpisode,
        playFlag: context.playFlag,
        siteEpisodeIndex: context.siteEpisodeIndex,
        siteEpisodeFile: context.siteEpisodeFile,
        preOrder: !!context.preOrder,
        playbackItemId: context.playbackItemId,
      }, { dedupe: false });
      await refreshPlayHistoryListFromServer();
    } catch (_error) {
      // ignore
    }
  })();
  try {
    await historyCommitState.inFlight;
  } finally {
    if (historyCommitState.key === key) historyCommitState.inFlight = null;
  }
  void reason;
};

export const commitPlayHistoryContextNow = async (reason = '') => {
  await commitHistoryBaseIfNeeded(reason);
};

export const onPlayerHistoryPlaybackStart = async (_reason = '') => {
  playHistorySessionState.playbackState = {
    started: true,
    ready: false,
  };
  const plan = playHistorySessionState.resumePlan && typeof playHistorySessionState.resumePlan === 'object'
    ? playHistorySessionState.resumePlan
    : null;
  const context = playHistorySessionState.activeContext && typeof playHistorySessionState.activeContext === 'object'
    ? playHistorySessionState.activeContext
    : null;
  const identity = normalizeString(context && context.identity);
  const seconds = Math.max(0, normalizeInt(plan && plan.seconds));
  if (!identity || !plan || plan.frozen || seconds <= 0 || normalizeString(plan.identity) !== identity) return 0;
  const now = Date.now();
  if (plan.applied) return 0;
  if (normalizeString(plan.tryKey) === identity && now - Math.max(0, normalizeInt(plan.tryAt)) < 800) return 0;
  plan.tryKey = identity;
  plan.tryAt = now;
  plan.applied = true;
  plan.frozen = true;
  return seconds;
};

export const confirmPlayerHistoryPlaybackReady = async (reason = '') => {
  const context = playHistorySessionState.activeContext && typeof playHistorySessionState.activeContext === 'object'
    ? playHistorySessionState.activeContext
    : null;
  if (!context || !context.reportEnabled) return;
  const playbackState = playHistorySessionState.playbackState && typeof playHistorySessionState.playbackState === 'object'
    ? playHistorySessionState.playbackState
    : null;
  if (!playbackState || !playbackState.started || playbackState.ready) return;
  playHistorySessionState.playbackState = {
    started: true,
    ready: true,
  };
  historyProgressState.at = Date.now();
  await commitHistoryBaseIfNeeded(reason);
};

export const syncHistoryProgressIfPossible = async ({ force = false } = {}) => {
  const context = playHistorySessionState.activeContext && typeof playHistorySessionState.activeContext === 'object'
    ? playHistorySessionState.activeContext
    : null;
  if (!context || !context.reportEnabled) return;
  const playbackState = playHistorySessionState.playbackState && typeof playHistorySessionState.playbackState === 'object'
    ? playHistorySessionState.playbackState
    : null;
  if (!playbackState || !playbackState.ready) return;
  const playerTime = playHistorySessionState.playerTime && typeof playHistorySessionState.playerTime === 'object'
    ? playHistorySessionState.playerTime
    : null;
  if (!playerTime || !playerTime.playing) return;
  const positionTicks = toTicks(playerTime.currentTime);
  const runtimeTicks = toTicks(playerTime.duration);
  if (positionTicks <= 0) return;
  const now = Date.now();
  if (!force && now - historyProgressState.at < 12_000) return;
  if (historyProgressState.inFlight) return;
  historyProgressState.at = now;
  historyProgressState.inFlight = (async () => {
    try {
      await commitHistoryBaseIfNeeded('timeupdate');
      await apiPostJson('/api/playhistory', {
        contentKey: context.contentKey,
        siteKey: context.siteKey,
        siteName: context.siteName,
        spiderApi: context.spiderApi,
        siteDetail: context.siteDetail,
        Poster: context.Poster,
        Remark: context.Remark,
        tmdbId: context.tmdbId,
        tmdbType: context.tmdbType,
        tmdbSeason: context.tmdbSeason,
        tmdbEpisode: context.tmdbEpisode,
        playFlag: context.playFlag,
        siteEpisodeIndex: context.siteEpisodeIndex,
        siteEpisodeFile: context.siteEpisodeFile,
        preOrder: !!context.preOrder,
        playbackItemId: context.playbackItemId,
        playbackPositionTicks: positionTicks,
        playbackRuntimeTicks: runtimeTicks,
      }, { dedupe: false });
      await refreshPlayHistoryListFromServer();
    } catch (_error) {
      // ignore
    }
  })();
  try {
    await historyProgressState.inFlight;
  } finally {
    historyProgressState.inFlight = null;
  }
};

export const flushHistoryProgressBestEffort = () => {
  void syncHistoryProgressIfPossible({ force: true });
};

export const buildPlayHistoryPayload = ({
  contentKey = '',
  reportEnabled = false,
  siteKey = '',
  siteName = '',
  spiderApi = '',
  siteDetail = '',
  Poster = '',
  Remark = '',
  tmdbId = 0,
  tmdbType = '',
  tmdbSeason = 0,
  tmdbEpisode = 0,
  globalEpisode = 0,
  playFlag = '',
  siteEpisodeIndex = 0,
  siteEpisodeFile = '',
  selectionKey = '',
  preOrder = false,
} = {}) => {
  const payload = {
    contentKey: normalizeString(contentKey),
    reportEnabled: !!reportEnabled,
    siteKey: normalizeString(siteKey),
    siteName: normalizeString(siteName),
    spiderApi: normalizeString(spiderApi),
    siteDetail: normalizeString(siteDetail),
    Poster: normalizeString(Poster),
    Remark: normalizeString(Remark),
    tmdbId: Math.max(0, normalizeInt(tmdbId)),
    tmdbType: normalizeString(tmdbType).toLowerCase(),
    tmdbSeason: Math.max(0, normalizeInt(tmdbSeason)),
    tmdbEpisode: Math.max(0, normalizeInt(tmdbEpisode)),
    globalEpisode: Math.max(0, normalizeInt(globalEpisode)),
    playFlag: normalizeString(playFlag),
    siteEpisodeIndex: Math.max(0, normalizeInt(siteEpisodeIndex)),
    siteEpisodeFile: normalizeString(siteEpisodeFile),
    selectionKey: normalizeString(selectionKey),
    preOrder: !!preOrder,
    playbackItemId: '',
  };
  payload.playbackItemId = buildEpisodePlaybackItemId({
    tmdbType: payload.tmdbType,
    tmdbId: payload.tmdbId,
    season: payload.tmdbSeason,
    episode: payload.tmdbEpisode,
  });
  payload.identity = buildHistoryIdentity(payload);
  return payload;
};

export const setPlayHistoryPreOrder = async (context = {}, nextValue = false) => {
  const target = context && typeof context === 'object' ? context : null;
  if (!target) throw new Error('缺少历史上下文');
  const contentKey = normalizeString(target.contentKey);
  const tmdbType = normalizeString(target.tmdbType).toLowerCase();
  const tmdbId = Math.max(0, normalizeInt(target.tmdbId));
  if (!contentKey || tmdbType !== 'tv' || tmdbId <= 0) {
    throw new Error('缺少 TMDB 剧集上下文');
  }
  const active = playHistorySessionState.activeContext && typeof playHistorySessionState.activeContext === 'object'
    ? playHistorySessionState.activeContext
    : null;
  const sameActive =
    active
    && normalizeString(active.contentKey) === contentKey
    && normalizeString(active.tmdbType).toLowerCase() === tmdbType
    && Math.max(0, normalizeInt(active.tmdbId)) === tmdbId;
  const nextContext = {
    ...(sameActive ? active : target),
    contentKey,
    tmdbId,
    tmdbType,
    Poster: normalizeString(target.Poster),
    Remark: normalizeString(target.Remark),
    preOrder: !!nextValue,
  };
  nextContext.identity = buildHistoryIdentity(nextContext);
  playHistorySessionState.activeContext = nextContext;
  await commitHistoryBaseIfNeeded('pre_order_toggle');
};
