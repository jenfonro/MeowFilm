import { isItemBlocked } from './policy.js';
import { codeOf, fail, fields, identity, requireValue, text } from './identity.js';
import { formatQualityBadge, replaceLine } from './session-helpers.js';

const SMART_BATCH_SIZE = 4;

function lineHasUnexpandedShare(line) {
  return Boolean(line && !line.expanded && line.episodes?.some(item => item.share ||
    String(item.playId ?? item.id ?? item.url ?? '').startsWith('meow.pan.v1.')));
}

function resultHasPlayableInlineEpisodes(result) {
  return Boolean(result?.detail?.lines?.some(line =>
    line.episodes?.some(ep => !ep.share || ep.file)));
}

export function createSessionBrowsing(ctx) {
  const { data, library } = ctx;

  function eligibleSmartListResults(session) {
    const blocks = library.read().blocks;
    return session.results.filter(result => {
      if (result.attachedDisplayOnly) return false;
      if (!ctx.currentSource(session, result.source)) return false;
      if (isItemBlocked(blocks, session.targetId, result.source.key, result.source.detailId)) return false;
      if (result.raw?.type_name === '电影') return false;
      if (result.state === 'error' && !result.detail) return false;
      if (result.detail && result.recognitionCache) {
        const hasTvEpisode = result.recognitionCache.candidates.some(c =>
          (!c.share || c.file) && c.episode !== undefined && c.episode > 0);
        const hasPendingShare = result.detail.lines.some(lineHasUnexpandedShare);
        if (!hasTvEpisode && !hasPendingShare) return false;
      }
      return true;
    });
  }

  function hasSmartList(session) {
    if (session.metadataSources.length > 0 || session.kind === 'movie') return false;
    return eligibleSmartListResults(session).length >= 2;
  }

  function smartListSeasons(session) {
    const seasonEpisodes = new Map();
    const eligible = eligibleSmartListResults(session);
    const eligibleIds = new Set(eligible.map(r => r.id));

    for (const result of eligible) {
      if (result.seasonHint > 0 && !seasonEpisodes.has(result.seasonHint)) {
        seasonEpisodes.set(result.seasonHint, new Map());
      }
      const cands = result.recognitionCache?.candidates ?? [];
      for (const cand of cands) {
        if ((cand.share && !cand.file) || !cand.episode || cand.episode <= 0) continue;
        const season = cand.season && cand.season > 0 ? cand.season : 1;
        if (!seasonEpisodes.has(season)) seasonEpisodes.set(season, new Map());
        const epMap = seasonEpisodes.get(season);
        const prev = epMap.get(cand.episode);
        if (!prev || (cand.seasonRank ?? 0) > (prev.seasonRank ?? 0)) {
          epMap.set(cand.episode, cand);
        }
      }
    }

    // If none have explicit season yet, ensure Season 1 bucket exists when results exist.
    if (!seasonEpisodes.size && eligibleIds.size >= 2) {
      seasonEpisodes.set(1, new Map());
    }

    const orderedSeasons = [...seasonEpisodes.keys()].sort((a, b) => a - b);
    return { orderedSeasons, seasonEpisodes, eligible };
  }

  function isMultiSeasonSession(session) {
    const primary = ctx.metadata.playbackMetadata(session);
    if (primary) {
      const rows = primary.doubanSeasons ?? primary.seasons ?? [];
      return rows.filter(r => (r.season_number ?? r.season) > 0).length > 1;
    }
    if (hasSmartList(session)) {
      return smartListSeasons(session).orderedSeasons.length > 1;
    }
    return false;
  }

  function canLoadMoreSources(session) {
    if (session.noSources) return false;
    return session.sources
      .filter(s => s.enabled !== false && s.searchable !== false)
      .some(s => {
        const st = session.search.get(s.key);
        return st && !st.completed;
      });
  }

  function canLoadMoreSmartEpisodes(session) {
    if (!hasSmartList(session)) return false;
    const { eligible } = smartListSeasons(session);
    const hasUnexpanded = eligible.some(r => !r.detail || !resultHasPlayableInlineEpisodes(r) && r.detail.lines.some(lineHasUnexpandedShare));
    return hasUnexpanded || canLoadMoreSources(session);
  }

  async function expandResultForSmartList(session, result, admitted) {
    if (!result.detail) {
      const detail = await ctx.candidates.loadDetail(session, result, session.browseGeneration, true, false);
      if (!detail || !admitted()) return;
    }
    if (!resultHasPlayableInlineEpisodes(result) && result.detail?.lines?.length) {
      for (let i = 0; i < result.detail.lines.length && admitted(); i++) {
        const line = result.baseDetail?.lines[i] ?? result.detail.lines[i];
        if (!lineHasUnexpandedShare(line)) continue;
        if (ctx.candidates.isLineBlocked(session, result.source.key, result.source.detailId, line.id)) continue;
        try {
          const expanded = await data.expand(result.source, line, { canSchedule: admitted });
          if (!admitted()) return;
          replaceLine(result, result.detail, i, expanded);
          await ctx.candidates.ensureRecognized(session, result, 'session');
          if (resultHasPlayableInlineEpisodes(result)) break;
        } catch {
          // Try next share line if this pan provider is unconfigured or expired.
        }
      }
    } else if (result.detail) {
      await ctx.candidates.ensureRecognized(session, result, 'session');
    }
  }

  async function ensureSmartListSeasonPopulated(session, preferredResultId = null, generation = session.browseGeneration) {
    const admitted = () => !ctx.isClosed() && !session.released && session.browseGeneration === generation;
    const { orderedSeasons, seasonEpisodes, eligible } = smartListSeasons(session);
    if (!eligible.length) return;

    let targetSeason = session.browsing.season;
    if (!targetSeason || !orderedSeasons.includes(targetSeason)) {
      targetSeason = orderedSeasons[0] ?? 1;
      session.browsing.season = targetSeason;
    }

    // If a specific raw card site was clicked, load ONLY that site first!
    if (preferredResultId) {
      const targetResult = eligible.find(r => r.id === preferredResultId);
      if (targetResult) {
        await expandResultForSmartList(session, targetResult, admitted);
        return;
      }
    }

    // Otherwise ensure the selected season has at least one expanded result.
    const currentEpCount = seasonEpisodes.get(targetSeason)?.size ?? 0;
    if (currentEpCount > 0) return;

    const matchingSeasonResults = eligible.filter(r =>
      !r.detail && (r.seasonHint === targetSeason || (targetSeason === 1 && r.seasonHint === 0)));
    const candidatesToLoad = (matchingSeasonResults.length ? matchingSeasonResults : eligible.filter(r => !r.detail))
      .slice(0, 2);
    for (const item of candidatesToLoad) {
      if (!admitted()) return;
      await expandResultForSmartList(session, item, admitted);
      const updated = smartListSeasons(session);
      if ((updated.seasonEpisodes.get(targetSeason)?.size ?? 0) > 0) break;
    }

    // Re-sync selected season after actual recognition in case implicit vs explicit shifted seasons.
    const after = smartListSeasons(session);
    if (after.orderedSeasons.length && !after.orderedSeasons.includes(session.browsing.season)) {
      session.browsing.season = after.orderedSeasons[0];
    }
  }

  async function loadMoreSmartEpisodes(session) {
    if (!hasSmartList(session) || session.browsing.loadingMoreEpisodes) return;
    const generation = session.browseGeneration;
    const admitted = () => !ctx.isClosed() && !session.released && session.browseGeneration === generation;
    session.browsing.loadingMoreEpisodes = true;
    ctx.publish(session);
    try {
      let { eligible } = smartListSeasons(session);
      let unexpanded = eligible.filter(r =>
        !r.detail || (!resultHasPlayableInlineEpisodes(r) && r.detail.lines.some(lineHasUnexpandedShare)));

      if (!unexpanded.length && canLoadMoreSources(session)) {
        const searchable = session.sources.filter(s => s.enabled !== false && s.searchable !== false);
        await Promise.all(searchable.map(s => ctx.candidates.searchPage(session, s)));
        if (!admitted()) return;
        eligible = smartListSeasons(session).eligible;
        unexpanded = eligible.filter(r =>
          !r.detail || (!resultHasPlayableInlineEpisodes(r) && r.detail.lines.some(lineHasUnexpandedShare)));
      }

      const targetSeason = session.browsing.season ?? 1;
      unexpanded.sort((a, b) => {
        const aMatch = a.seasonHint === targetSeason || (targetSeason === 1 && a.seasonHint === 0) ? 1 : 0;
        const bMatch = b.seasonHint === targetSeason || (targetSeason === 1 && b.seasonHint === 0) ? 1 : 0;
        if (aMatch !== bMatch) return bMatch - aMatch;
        return (a.source.order ?? 999999) - (b.source.order ?? 999999);
      });

      const batch = unexpanded.slice(0, SMART_BATCH_SIZE);
      for (const result of batch) {
        if (!admitted()) return;
        await expandResultForSmartList(session, result, admitted);
      }
    } finally {
      if (!ctx.isClosed() && !session.released) {
        session.browsing.loadingMoreEpisodes = false;
        ctx.publish(session);
      }
    }
  }

  async function browseConcreteResult(session, result, lineId, { generation, refresh = false, season } = {}) {
    const admitted = () => !ctx.isClosed() && !session.released && session.browseGeneration === generation &&
      ctx.currentSource(session, result.source);
    session.browsing = {
      ...session.browsing,
      sourceId: result.id,
      lineId: lineId ?? session.browsing.lineId ?? null,
      ...(season !== undefined ? { season } : {}),
      loading: true,
      refreshing: Boolean(result.detail),
      error: null,
    };
    ctx.publish(session);

    try {
      let detail;
      if (result.source.engine === 'pan') {
        const row = ctx.currentManual(session, result);
        requireValue(row?.share, 'NOT_FOUND');
        if (!refresh && result.detail) detail = result.detail;
        else {
          const line = {
            id: row.id,
            label: row.name || row.provider,
            provider: row.provider,
            episodes: [{ id: row.id, name: row.name || session.fallbackTitle, share: { ...row.share, provider: row.provider } }],
          };
          detail = { lines: [line], detailId: row.id };
          result.baseDetail = detail; result.detail = detail;
        }
      } else {
        detail = await ctx.candidates.loadDetail(session, result, generation, true, refresh);
      }
      if (!admitted()) return;
      if (!detail) throw fail(result.error ?? 'UPSTREAM_ERROR');

      let line = lineId !== undefined && lineId !== null
        ? detail.lines.find(l => String(l.id) === String(lineId))
        : (detail.lines.find(l => String(l.id) === String(session.browsing.lineId)) ?? detail.lines[0]);
      if (lineId !== undefined && lineId !== null) requireValue(line, 'NOT_FOUND');

      session.browsing = {
        ...session.browsing,
        sourceId: result.id,
        lineId: line?.id ?? null,
      };
      ctx.publish(session);

      if (line && (refresh || lineHasUnexpandedShare(line))) {
        const baseLine = result.baseDetail?.lines.find(l => l.id === line.id) ?? line;
        const expanded = await data.expand(result.source, baseLine, { canSchedule: admitted, refresh });
        if (!admitted()) return;
        detail = replaceLine(result, detail, detail.lines.findIndex(l => l.id === line.id), expanded);
        line = detail.lines.find(l => l.id === line.id);
      }
      await ctx.candidates.ensureRecognized(session, result, 'session');
      if (!admitted()) return;

      result.state = 'ready';
      result.error = null;
      session.browsing = {
        ...session.browsing,
        sourceId: result.id,
        lineId: line?.id ?? null,
        loading: false,
        refreshing: false,
        error: null,
      };
    } catch (error) {
      if (!admitted()) return;
      session.browsing = {
        ...session.browsing,
        loading: false,
        refreshing: false,
        error: codeOf(error),
      };
    }
    ctx.publish(session);
  }

  async function selectSource(session, sourceId, { lineId, season, refresh = false, preferredSmartResultId = null } = {}) {
    const generation = ++session.browseGeneration;
    const admitted = () => !ctx.isClosed() && !session.released && session.browseGeneration === generation;

    if (typeof sourceId === 'string' && sourceId.startsWith('meta:')) {
      const metaRow = session.metadataSources.find(r => r.id === sourceId);
      requireValue(metaRow, 'NOT_FOUND');
      const display = session.metadataViews.get(metaRow.id)?.display ?? ctx.metadata.metadataDisplay(session, metaRow);
      const seasons = (display?.doubanSeasons ?? display?.seasons ?? [])
        .map(s => s.season_number ?? s.season)
        .filter(n => Number.isSafeInteger(n) && n > 0)
        .sort((a, b) => a - b);
      const nextSeason = season ?? (seasons.includes(session.browsing.season) ? session.browsing.season : seasons[0] ?? null);
      session.browsing = {
        ...session.browsing,
        sourceId: metaRow.id,
        lineId: null,
        season: nextSeason,
        loading: metaRow.kind === 'tv' && nextSeason !== null,
        refreshing: !refresh,
        error: null,
      };
      ctx.publish(session);
      try {
        if (refresh) {
          const fresh = await data.targetMetadataEntry(
            { kind: metaRow.kind, metadataProvider: metaRow.provider, metadataId: metaRow.metadataId },
            { rules: session.rules, canSchedule: admitted, refresh: true },
          );
          if (!admitted()) return;
          if (fresh?.value) ctx.metadata.installMetadataRow(session, metaRow.provider, metaRow.kind, metaRow.metadataId, fresh.value);
        }
        if (metaRow.kind === 'tv' && nextSeason !== null) {
          await ctx.metadata.ensureSeasonLoaded(session, metaRow, nextSeason, { canSchedule: admitted, refresh });
        }
        if (!admitted()) return;
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: null };
      } catch (error) {
        if (!admitted()) return;
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: codeOf(error) };
      }
      ctx.publish(session);
      return;
    }

    if (sourceId === 'smart') {
      session.browsing = {
        ...session.browsing,
        sourceId: 'smart',
        lineId: null,
        ...(season !== undefined ? { season } : {}),
        loading: true,
        refreshing: false,
        error: null,
      };
      ctx.publish(session);
      try {
        await ensureSmartListSeasonPopulated(session, preferredSmartResultId, generation);
        if (!admitted()) return;
        // If after loading details fewer than 2 valid TV results remain, fall back cleanly to first concrete site.
        if (!hasSmartList(session) && session.results.length > 0) {
          const fallback = preferredSmartResultId
            ? (session.results.find(r => r.id === preferredSmartResultId) ?? session.results[0])
            : session.results[0];
          await browseConcreteResult(session, fallback, undefined, { generation });
          return;
        }
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: null };
      } catch (error) {
        if (!admitted()) return;
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: codeOf(error) };
      }
      ctx.publish(session);
      return;
    }

    const result = session.results.find(r => r.id === sourceId);
    requireValue(result, 'NOT_FOUND');
    await browseConcreteResult(session, result, lineId, { generation, refresh, season });
  }

  function buildDirectorySnapshot(session) {
    const activeCand = session.pending?.candidate ?? session.current?.candidate ?? null;
    const activeIntent = session.playIntent;
    const selectedId = session.browsing.sourceId;

    // 1. Metadata Directory (TMDB / Douban)
    if (typeof selectedId === 'string' && selectedId.startsWith('meta:')) {
      const metaRow = session.metadataSources.find(r => r.id === selectedId);
      if (!metaRow) {
        return {
          mode: 'empty',
          loading: Boolean(session.browsing.loading),
          refreshing: Boolean(session.browsing.refreshing),
          error: session.browsing.error ?? null,
          seasons: [],
          selectedSeason: null,
          canLoadMore: false,
          loadingMore: false,
          previewEpisodes: session.previewEpisodes,
          canTogglePreview: false,
          items: [],
          rawItems: [],
        };
      }
      if (metaRow.kind === 'movie') {
        const movieCands = [...session.candidates.values()]
          .filter(c => (!c.share || c.file) && !c.variant && c.eligible !== false);
        const items = movieCands.map((cand, idx) => {
          const displayTitle = text(cand.fileName) || text(cand.name) || metaRow.title;
          const isActive = Boolean(activeCand && (activeCand.id === cand.id || activeCand.parentId === cand.id));
          return {
            id: `cand:${cand.id}`,
            index: idx,
            season: null,
            episode: null,
            numberLabel: String(idx + 1).padStart(2, '0'),
            title: displayTitle,
            fileName: displayTitle,
            path: text(cand.path),
            sourceLabel: text(cand.source?.name) || text(cand.source?.key),
            summary: '',
            still: '',
            duration: '',
            airDate: '',
            quality: formatQualityBadge(cand.quality),
            active: isActive,
          };
        });
        return {
          mode: 'movies',
          loading: Boolean(session.browsing.loading),
          refreshing: Boolean(session.browsing.refreshing),
          error: session.browsing.error ?? null,
          seasons: [],
          selectedSeason: null,
          canLoadMore: false,
          loadingMore: false,
          previewEpisodes: false,
          canTogglePreview: false,
          items,
          rawItems: [],
        };
      }
      const display = session.metadataViews.get(metaRow.id)?.display ?? metaRow.metadata;
      const proj = ctx.metadata.episodeProjection(session, display);
      const rawSeasons = (display?.doubanSeasons ?? display?.seasons ?? [])
        .map(s => ({
          season: s.season_number ?? s.season,
          count: s.episode_count ?? s.episodeCount ?? (Array.isArray(s.episodes) ? s.episodes.length : 0),
        }))
        .filter(s => Number.isSafeInteger(s.season) && s.season > 0)
        .sort((a, b) => a.season - b.season);
      const selectedSeason = rawSeasons.some(s => s.season === session.browsing.season)
        ? session.browsing.season
        : (rawSeasons[0]?.season ?? null);
      const seasonItems = selectedSeason === null ? [] : proj.visible.filter(ep => ep.season === selectedSeason);

      // IMPORTANT: Never copy the main show intro onto generated Douban episode cards!
      const items = seasonItems.map((ep, idx) => {
        const epNum = ep.episode;
        const rawTitle = text(ep.name);
        const defaultLabel = `第${String(epNum).padStart(2, '0')}集`;
        const title = rawTitle && rawTitle !== `第 ${epNum} 集` && rawTitle !== `Episode ${epNum}`
          ? `${defaultLabel} · ${rawTitle}`
          : defaultLabel;
        const duration = Number.isFinite(ep.runtime) && ep.runtime > 0 ? `${Math.round(ep.runtime)}分钟` : '';
        const isActive = activeIntent?.kind === 'tv' &&
          (ep.globalEpisode !== undefined && activeIntent.globalEpisode !== undefined
            ? ep.globalEpisode === activeIntent.globalEpisode
            : ep.season === activeIntent.season && ep.episode === activeIntent.episode);
        return {
          id: `ep:${ep.season}:${ep.episode}${ep.globalEpisode !== undefined ? `:${ep.globalEpisode}` : ''}`,
          index: idx,
          season: ep.season,
          episode: ep.episode,
          numberLabel: String(epNum).padStart(2, '0'),
          title,
          fileName: '',
          summary: text(ep.overview),
          still: text(ep.still_path),
          duration,
          airDate: text(ep.air_date),
          quality: '',
          active: Boolean(isActive),
        };
      });

      return {
        mode: 'episodes',
        loading: Boolean(session.browsing.loading),
        refreshing: Boolean(session.browsing.refreshing),
        error: session.browsing.error ?? null,
        seasons: rawSeasons.map(s => ({ season: s.season, label: `第${s.season}季`, episodeCount: s.count })),
        selectedSeason,
        canLoadMore: false,
        loadingMore: false,
        previewEpisodes: session.previewEpisodes,
        canTogglePreview: Boolean(proj.canTogglePreview && metaRow.provider === 'tmdb'),
        items,
        rawItems: [],
      };
    }

    // 2. Smart List Directory (`智能列表` when no TMDB & no Douban & >= 2 valid results)
    if (selectedId === 'smart' && hasSmartList(session)) {
      const { orderedSeasons, seasonEpisodes } = smartListSeasons(session);
      const selectedSeason = orderedSeasons.includes(session.browsing.season)
        ? session.browsing.season
        : (orderedSeasons[0] ?? 1);
      const epMap = seasonEpisodes.get(selectedSeason) ?? new Map();
      const sortedEpNums = [...epMap.keys()].sort((a, b) => a - b);
      const items = sortedEpNums.map((epNum, idx) => {
        const bestCand = epMap.get(epNum);
        const isActive = activeIntent?.kind === 'tv' &&
          activeIntent.season === selectedSeason &&
          activeIntent.episode === epNum;
        return {
          id: `smart:${selectedSeason}:${epNum}`,
          index: idx,
          season: selectedSeason,
          episode: epNum,
          numberLabel: String(epNum).padStart(2, '0'),
          title: `第${String(epNum).padStart(2, '0')}集`,
          fileName: text(bestCand?.fileName),
          path: text(bestCand?.path),
          sourceLabel: text(bestCand?.source?.name) || text(bestCand?.source?.key),
          summary: '',
          still: '',
          duration: '',
          airDate: '',
          quality: formatQualityBadge(bestCand?.quality),
          active: Boolean(isActive),
        };
      });

      return {
        mode: 'episodes',
        loading: Boolean(session.browsing.loading),
        refreshing: Boolean(session.browsing.refreshing),
        error: session.browsing.error ?? null,
        seasons: orderedSeasons.map(s => ({
          season: s,
          label: `第${s}季`,
          episodeCount: seasonEpisodes.get(s)?.size ?? 0,
        })),
        selectedSeason,
        canLoadMore: canLoadMoreSmartEpisodes(session),
        loadingMore: Boolean(session.browsing.loadingMoreEpisodes),
        previewEpisodes: false,
        canTogglePreview: false,
        items,
        rawItems: [],
      };
    }

    // 3. Concrete Site / Manual Pan Line Directory
    const result = session.results.find(r => r.id === selectedId);
    const line = result?.detail?.lines.find(l => String(l.id) === String(session.browsing.lineId)) ?? result?.detail?.lines[0] ?? null;
    if (!result || !line) {
      return {
        mode: 'empty',
        loading: Boolean(session.browsing.loading),
        refreshing: Boolean(session.browsing.refreshing),
        error: session.browsing.error ?? null,
        seasons: [],
        selectedSeason: null,
        canLoadMore: false,
        loadingMore: false,
        previewEpisodes: false,
        canTogglePreview: false,
        items: [],
        rawItems: [],
      };
    }

    const recognizedByEpId = new Map(
      (result.recognitionCache?.candidates ?? [])
        .filter(c => c.lineId === line.id && !c.variant)
        .map(c => [c.episodeId, c]),
    );
    const rawFiles = line.episodes.filter(ep => !ep.share || ep.file);
    const seasonSet = new Set();
    for (const ep of rawFiles) {
      const cand = recognizedByEpId.get(ep.id);
      if (cand?.season > 0 && cand?.episode > 0 && !cand.implicitSeason) seasonSet.add(cand.season);
    }
    const orderedSeasons = [...seasonSet].sort((a, b) => a - b);
    const selectedSeason = orderedSeasons.length > 1
      ? (orderedSeasons.includes(session.browsing.season) ? session.browsing.season : orderedSeasons[0])
      : null;

    const filteredFiles = selectedSeason !== null
      ? rawFiles.filter(ep => {
        const cand = recognizedByEpId.get(ep.id);
        return !cand?.season || cand.season === selectedSeason;
      })
      : rawFiles;

    const siteLabel = text(result.source.name) || result.source.key;
    const mapFileRow = (ep, idx) => {
      const cand = recognizedByEpId.get(ep.id);
      const displayTitle = text(cand?.fileName) || text(ep.name) || `条目 ${idx + 1}`;
      const isActive = activeCand
        ? Boolean(cand && (activeCand.id === cand.id || activeCand.parentId === cand.id))
        : Boolean(activeIntent?.mode === 'direct' && cand && activeIntent.candidateId === cand.id);
      return {
        id: cand ? `cand:${cand.id}` : `raw:${result.id}:${line.id}:${ep.index}`,
        index: idx,
        season: cand?.season ?? null,
        episode: cand?.episode ?? null,
        numberLabel: cand?.episode ? String(cand.episode).padStart(2, '0') : String(idx + 1).padStart(2, '0'),
        title: text(ep.name) || displayTitle,
        fileName: displayTitle,
        path: text(cand?.path ?? ep.path),
        sourceLabel: siteLabel,
        summary: '',
        still: '',
        duration: '',
        airDate: '',
        quality: formatQualityBadge(cand?.quality),
        active: isActive,
      };
    };

    const items = filteredFiles.map(mapFileRow);
    const rawItems = rawFiles.map(mapFileRow);

    return {
      mode: session.kind === 'movie' ? 'movies' : 'files',
      loading: Boolean(session.browsing.loading),
      refreshing: Boolean(session.browsing.refreshing),
      error: session.browsing.error ?? null,
      seasons: orderedSeasons.length > 1
        ? orderedSeasons.map(s => ({ season: s, label: `第${s}季`, episodeCount: 0 }))
        : [],
      selectedSeason,
      canLoadMore: false,
      loadingMore: false,
      previewEpisodes: false,
      canTogglePreview: false,
      items,
      rawItems,
    };
  }

  return {
    eligibleSmartListResults,
    hasSmartList,
    smartListSeasons,
    isMultiSeasonSession,
    canLoadMoreSources,
    canLoadMoreSmartEpisodes,
    ensureSmartListSeasonPopulated,
    loadMoreSmartEpisodes,
    selectSource,
    buildDirectorySnapshot,
  };
}
