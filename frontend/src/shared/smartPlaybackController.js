import {
  getSearchSessionLaneSnapshot,
  getSearchSessionLaneStatus,
  performSearchSessionSearch,
  subscribeSearchSessionLane,
} from './searchSession';
import {
  comparePlaybackCandidatesForAction,
  comparePlaybackCandidatesByDefaultRules,
  isPlaybackCandidateAllowedByAction,
} from './playbackRuntime';
import { normalizeInt, normalizeString } from './normalize';
const normalizeMatchKind = (options) => (normalizeString(options && options.kind).toLowerCase() === 'movie' ? 'movie' : 'episode');

const compareNumbersAsc = (left, right) => {
  if (left === right) return 0;
  return left < right ? -1 : 1;
};

export const runSmartPlaybackController = async ({
  runSeq,
  query,
  bootstrap,
  searchScope = 'default',
  searchDisplayModeOverride = '',
  blockedSiteKeys = [],
  singleSiteThread = false,
  matchOptions = null,
  actionConstraint = null,
  runtimeSettings = null,
  currentContext = null,
  globalEpisode = 0,
  wantEpisodeInSeason = 0,
  isRunStopped,
  isCandidateAllowed,
  buildSiteItemsFromSnapshot,
  collectRecognitionCandidatesForTarget,
  getCachedSiteResultDetail,
  ensureSiteResultDetailCached,
  buildPanSourcesFromDetail,
  buildPanSegment,
  buildSelectionKey,
  playResolvedSiteSegment,
  setAttemptRunSeq,
  setPendingRunSeq,
  setResume,
  clearPendingAttempt,
  onLoadingStateChange,
  onErrorTextChange,
  onStreamCleanupChange,
  onConfirmedStop,
  getSearchState,
} = {}) => {
  const targetGlobal = Math.max(0, normalizeInt(globalEpisode));
  const targetLoose = Math.max(0, normalizeInt(wantEpisodeInSeason));
  const matchKind = normalizeMatchKind(matchOptions);
  const allowResolutionModes = ['strict-tmdb', 'strict-douban'];
  if (!runSeq || !query || (matchKind === 'episode' && targetGlobal <= 0)) return;

  const candidateRegistry = new Map();
  const failedCandidateKeys = new Set();
  const startedSiteKeys = new Set();
  const queuedSiteKeys = new Set();
  const pendingSiteQueue = [];
  let activeThreads = 0;
  let searchCompleted = false;
  let flowPhase = 'running'; // running | finalizing | done
  let playAttemptRunning = false;
  let queuedRetry = false;
  let currentPendingCandidateKey = '';
  let unsubscribe = () => {};
  let controllerFinished = false;
  let resolveControllerDone = null;
  const controllerDone = new Promise((resolve) => {
    resolveControllerDone = resolve;
  });
  const finishController = (reason = '') => {
    if (controllerFinished) return;
    controllerFinished = true;
    flowPhase = 'done';
    try {
      unsubscribe();
    } catch (_error) {}
    if (typeof resolveControllerDone === 'function') resolveControllerDone(reason || 'done');
  };

  const safeStopped = () => (typeof isRunStopped === 'function' ? !!isRunStopped() : false);
  const safeBuildSiteItems = (snapshot) =>
    (typeof buildSiteItemsFromSnapshot === 'function' ? buildSiteItemsFromSnapshot(snapshot) : []) || [];
  const safeGetSearchState = (targetQuery, scope) => {
    if (typeof getSearchState === 'function') {
      try {
        const payload = getSearchState(targetQuery, scope);
        if (payload && typeof payload === 'object') {
          return {
            snapshot: payload.snapshot || null,
            status: normalizeString(payload.status) || 'idle',
          };
        }
      } catch (_error) {}
    }
    return {
      snapshot: getSearchSessionLaneSnapshot(targetQuery, scope, 'site'),
      status: getSearchSessionLaneStatus(targetQuery, scope, 'site'),
    };
  };
  const safeCandidateAllowed = (candidate) => {
    if (typeof isCandidateAllowed !== 'function') return true;
    try {
      return !!isCandidateAllowed(candidate, matchOptions || {});
    } catch (_error) {
      return true;
    }
  };
  const clearPending = () => {
    if (typeof clearPendingAttempt === 'function') clearPendingAttempt();
  };

  let latestOrderMap = new Map(
    safeBuildSiteItems(safeGetSearchState(query, searchScope).snapshot)
      .map((entry, index) => [normalizeString(entry && entry.id), index]),
  );

  const rebuildOrderMapFromSnapshot = (snapshot) => {
    latestOrderMap = new Map(
      safeBuildSiteItems(snapshot).map((entry, index) => [normalizeString(entry && entry.id), index]),
    );
  };

  const compareStable = (left, right, orderMap) => {
    const leftOrder = orderMap.has(normalizeString(left && left.siteItem && left.siteItem.id))
      ? orderMap.get(normalizeString(left.siteItem.id))
      : 999999;
    const rightOrder = orderMap.has(normalizeString(right && right.siteItem && right.siteItem.id))
      ? orderMap.get(normalizeString(right.siteItem.id))
      : 999999;
    const siteOrderCmp = compareNumbersAsc(leftOrder, rightOrder);
    if (siteOrderCmp !== 0) return siteOrderCmp;
    const panCmp = normalizeString(left && left.panKey).localeCompare(normalizeString(right && right.panKey), 'zh');
    if (panCmp !== 0) return panCmp;
    const itemIndexCmp = compareNumbersAsc(normalizeInt(left && left.itemIndex), normalizeInt(right && right.itemIndex));
    if (itemIndexCmp !== 0) return itemIndexCmp;
    return normalizeString(left && left.siteItem && left.siteItem.siteDetail)
      .localeCompare(normalizeString(right && right.siteItem && right.siteItem.siteDetail), 'zh');
  };

  const compareCandidates = (left, right, orderMap) => {
    const targetConstraint = actionConstraint && typeof actionConstraint === 'object' ? actionConstraint : null;
    const primary = targetConstraint && normalizeString(targetConstraint.mode) !== 'default' && normalizeString(targetConstraint.mode) !== 'switch'
      ? comparePlaybackCandidatesForAction(left, right, targetConstraint, currentContext, runtimeSettings)
      : comparePlaybackCandidatesByDefaultRules(left, right, runtimeSettings);
    if (primary !== 0) return primary;
    return compareStable(left, right, orderMap);
  };

  const pickBestCandidate = () => {
    const orderMap = latestOrderMap;
    let best = null;
    candidateRegistry.forEach((candidate, candidateKey) => {
      if (!candidateKey || failedCandidateKeys.has(candidateKey)) return;
      if (!safeCandidateAllowed(candidate)) return;
      if (!isPlaybackCandidateAllowedByAction(candidate, actionConstraint, runtimeSettings, currentContext)) return;
      if (!best || compareCandidates(candidate, best, orderMap) < 0) {
        best = candidate;
      }
    });
    return best;
  };

  const tryResolvePlayback = async () => {
    if (safeStopped()) return false;
    while (!safeStopped()) {
      const picked = pickBestCandidate();
      if (!picked) return false;
      const candidateKey = `${normalizeString(picked && picked.siteItem && picked.siteItem.id)}::${normalizeString(picked && picked.panKey)}::${normalizeInt(picked && picked.itemIndex)}`;
      if (!candidateKey) return false;
      const detail = (typeof getCachedSiteResultDetail === 'function' ? getCachedSiteResultDetail(picked.siteItem) : null)
        || await (typeof ensureSiteResultDetailCached === 'function'
          ? ensureSiteResultDetailCached(picked.siteItem).catch(() => null)
          : null);
      if (!detail) {
        failedCandidateKeys.add(candidateKey);
        continue;
      }
      const panSources = typeof buildPanSourcesFromDetail === 'function' ? buildPanSourcesFromDetail(detail) : [];
      const panEntry = Array.isArray(panSources)
        ? panSources.find((entry) => normalizeString(entry && entry.key) === normalizeString(picked && picked.panKey)) || null
        : null;
      const segment = typeof buildPanSegment === 'function'
        ? buildPanSegment(panEntry, picked && picked.itemIndex)
        : null;
      if (!panEntry || !segment || !normalizeString(segment && segment.episodeUrl)) {
        failedCandidateKeys.add(candidateKey);
        continue;
      }
      if (typeof setAttemptRunSeq === 'function') setAttemptRunSeq(runSeq);
      const ok = await (typeof playResolvedSiteSegment === 'function'
        ? playResolvedSiteSegment({
          siteItem: picked.siteItem,
          panEntry,
          segment,
          candidate: picked && picked.candidate ? picked.candidate : null,
          selectionKey: typeof buildSelectionKey === 'function'
            ? buildSelectionKey(panEntry && panEntry.key, segment && segment.index)
            : '',
          globalEpisode: targetGlobal,
        })
        : false);
      if (!ok) {
        failedCandidateKeys.add(candidateKey);
        releasePendingPlaybackLock({ clearPending: true });
        if (typeof setAttemptRunSeq === 'function') setAttemptRunSeq(0);
        continue;
      }
      acquirePendingPlaybackLock(candidateKey);
      finishController('playback_pending');
      if (typeof setResume === 'function') {
        setResume(async () => {
          if (isClosedLoopStopped()) return;
          if (currentPendingCandidateKey) failedCandidateKeys.add(currentPendingCandidateKey);
          releasePendingPlaybackLock({ clearPending: true });
          await requestResolvePlayback();
        });
      }
      return true;
    }
    return false;
  };

  const requestResolvePlayback = async () => {
    if (isClosedLoopStopped()) return false;
    if (shouldBlockResolveRequests()) {
      queuedRetry = true;
      return false;
    }
    playAttemptRunning = true;
    try {
      return await tryResolvePlayback();
    } finally {
      playAttemptRunning = false;
      if (!isClosedLoopStopped() && queuedRetry) {
        queuedRetry = false;
        void requestResolvePlayback();
      }
    }
  };

  const finalizeIfComplete = async () => {
    if (flowPhase !== 'running' || safeStopped()) return;
    if (!searchCompleted || activeThreads > 0) return;

    // If resolve/play is currently in-flight (or blocked by pending lock),
    // do not finalize to no_match yet. Wait for the closed-loop to settle.
    if (shouldBlockResolveRequests()) {
      queuedRetry = true;
      return;
    }

    flowPhase = 'finalizing';
    const resolved = await requestResolvePlayback();
    if (resolved || flowPhase === 'done') return;
    if (safeStopped()) return;

    // requestResolvePlayback may return false when a retry gets queued by flow state
    // transitions in the same tick. In that case, do not emit no_match prematurely.
    if (queuedRetry || playAttemptRunning || flowState.pendingPlaybackLocked || currentPendingCandidateKey) {
      flowPhase = 'running';
      return;
    }

    if (typeof onLoadingStateChange === 'function') onLoadingStateChange(false);
    if (typeof onErrorTextChange === 'function') onErrorTextChange('暂无可匹配片源');
    finishController('no_match');
  };

  const flowState = {
    resolveRequestQueued: false,
    resolveRequestScheduled: false,
    pendingPlaybackLocked: false,
  };

  const isClosedLoopStopped = () => safeStopped();
  const shouldBlockNewDetailRequests = () => isClosedLoopStopped() || flowState.pendingPlaybackLocked;
  const shouldBlockResolveRequests = () => shouldBlockNewDetailRequests() || playAttemptRunning;
  const waitDispatchReady = async () => {
    while (!isClosedLoopStopped()) {
      if (flowState.pendingPlaybackLocked) return false;
      if (!playAttemptRunning) return true;
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    return false;
  };

  const releasePendingPlaybackLock = ({ clearPending = true } = {}) => {
    flowState.pendingPlaybackLocked = false;
    if (clearPending) currentPendingCandidateKey = '';
    clearPending();
  };

  const acquirePendingPlaybackLock = (candidateKey) => {
    currentPendingCandidateKey = normalizeString(candidateKey);
    flowState.pendingPlaybackLocked = !!currentPendingCandidateKey;
    if (flowState.pendingPlaybackLocked && typeof setPendingRunSeq === 'function') setPendingRunSeq(runSeq);
  };

  const scheduleResolvePlayback = () => {
    flowState.resolveRequestQueued = true;
    if (flowState.resolveRequestScheduled || isClosedLoopStopped()) return;
    flowState.resolveRequestScheduled = true;
    Promise.resolve().then(async () => {
      flowState.resolveRequestScheduled = false;
      if (!flowState.resolveRequestQueued || isClosedLoopStopped()) return;
      flowState.resolveRequestQueued = false;
      await requestResolvePlayback();
    });
  };

  const registerCandidates = (siteItem) => {
    const candidates = typeof collectRecognitionCandidatesForTarget === 'function'
      ? collectRecognitionCandidatesForTarget(siteItem, {
        matchOptions,
        globalEpisode: targetGlobal,
        wantEpisodeInSeason: targetLoose,
        allowResolutionModes,
      })
      : [];
    if (!Array.isArray(candidates) || !candidates.length) return false;
    candidates.forEach((candidate) => {
      const dedupeKey = `${normalizeString(candidate && candidate.siteItem && candidate.siteItem.id)}::${normalizeString(candidate && candidate.panKey)}::${normalizeInt(candidate && candidate.itemIndex)}`;
      if (!dedupeKey) return;
      candidateRegistry.set(dedupeKey, candidate);
    });
    scheduleResolvePlayback();
    return true;
  };

  const runNextQueuedSiteThread = () => {
    if (!singleSiteThread || shouldBlockNewDetailRequests()) return;
    if (activeThreads > 0) return;
    const next = pendingSiteQueue.shift();
    if (!next) return;
    startSiteThread(next.siteKey, next.items);
  };

  const startSiteThread = (siteKey, items) => {
    const key = normalizeString(siteKey);
    if (!key || shouldBlockNewDetailRequests() || startedSiteKeys.has(key) || !Array.isArray(items) || !items.length) return;
    startedSiteKeys.add(key);
    activeThreads += 1;
    Promise.resolve()
      .then(async () => {
        for (let i = 0; i < items.length; i += 1) {
          if (shouldBlockNewDetailRequests()) return;
          const siteItem = items[i];

          const reused = registerCandidates(siteItem);
          if (reused || shouldBlockNewDetailRequests()) continue;
          const ready = await waitDispatchReady();
          if (!ready || shouldBlockNewDetailRequests()) return;

          const detail = await Promise.resolve()
            .then(() => (typeof ensureSiteResultDetailCached === 'function'
              ? ensureSiteResultDetailCached(siteItem, {
                signal: null,
                onUpdate: (nextDetail) => {
                  const payload = nextDetail && typeof nextDetail === 'object' ? nextDetail : null;
                  if (!payload || shouldBlockNewDetailRequests()) return;
                  registerCandidates(siteItem);
                },
              }).catch(() => null)
              : null));
          if (!detail || shouldBlockNewDetailRequests()) continue;
          registerCandidates(siteItem);
        }
      })
      .finally(async () => {
        activeThreads = Math.max(0, activeThreads - 1);
        if (singleSiteThread) {
          queuedSiteKeys.delete(key);
          runNextQueuedSiteThread();
        }
        await finalizeIfComplete();
      });
  };

  const enqueueSiteThread = (siteKey, items) => {
    const key = normalizeString(siteKey);
    if (!key || shouldBlockNewDetailRequests() || startedSiteKeys.has(key) || queuedSiteKeys.has(key) || !Array.isArray(items) || !items.length) return;
    queuedSiteKeys.add(key);
    pendingSiteQueue.push({ siteKey: key, items });
    runNextQueuedSiteThread();
  };

  const scheduleThreadsFromSnapshot = (snapshot) => {
    if (shouldBlockNewDetailRequests()) return;
    rebuildOrderMapFromSnapshot(snapshot);
    const items = safeBuildSiteItems(snapshot);
    if (!items.length) return;
    const grouped = new Map();
    items.forEach((siteItem) => {
      const key = normalizeString(siteItem && siteItem.siteKey);
      if (!key) return;
      if (!grouped.has(key)) grouped.set(key, []);
      grouped.get(key).push(siteItem);
    });
    grouped.forEach((groupItems, siteKey) => {
      if (singleSiteThread) enqueueSiteThread(siteKey, groupItems);
      else startSiteThread(siteKey, groupItems);
    });
  };

  unsubscribe = subscribeSearchSessionLane(query, searchScope, 'site', (snapshot, status) => {
    if (isClosedLoopStopped()) return;
    scheduleThreadsFromSnapshot(snapshot);
    if (status === 'completed' || status === 'error') {
      searchCompleted = true;
      void finalizeIfComplete();
    }
  });

  if (typeof onStreamCleanupChange === 'function') {
    onStreamCleanupChange(() => {
      try {
        unsubscribe();
      } catch (_error) {}
      finishController('cleanup');
    });
  }

  const initialSearchState = safeGetSearchState(query, searchScope);
  if (initialSearchState.snapshot) scheduleThreadsFromSnapshot(initialSearchState.snapshot);
  if (initialSearchState.status === 'completed' || initialSearchState.status === 'error') {
    searchCompleted = true;
    await finalizeIfComplete();
  }
  if (initialSearchState.status === 'idle' && !initialSearchState.snapshot) {
    void performSearchSessionSearch(query, bootstrap, {
      saveHistoryEnabled: false,
      blockedSiteKeys,
      affectUi: false,
      scope: searchScope,
      searchDisplayModeOverride,
      contentKind: matchKind === 'movie' ? 'movie' : 'tv',
    }).then(() => {
      if (isClosedLoopStopped()) return;
      searchCompleted = true;
      void finalizeIfComplete();
    }).catch(() => {
      if (isClosedLoopStopped()) return;
      searchCompleted = true;
      void finalizeIfComplete();
    });
  }
  if (safeStopped() && typeof onConfirmedStop === 'function') onConfirmedStop();
  if (safeStopped()) {
    finishController('stopped');
  }
  await controllerDone;
};
