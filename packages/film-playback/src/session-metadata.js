import { isDeepStrictEqual } from 'node:util';
import { buildEpisodeMap, matchEpisodeMaps, resolveEpisode, sameSeasonPartition } from './episodes.js';
import { clone, codeOf, fail, fields, object, requireValue, text } from './identity.js';
import { historyPosition, mergeSeason, metadataIdentity } from './session-helpers.js';

export function createSessionMetadata(ctx) {
  const { data, catalog, episodeMaps } = ctx;

  function episodeMap(metadata) {
    if (!metadata) return [];
    let map = episodeMaps.get(metadata);
    if (!map) { map = buildEpisodeMap(metadata); episodeMaps.set(metadata, map); }
    return map;
  }

  function playbackMetadataSource(session) {
    return session.metadataSources.find(row => row.id === session.playbackMetadataSourceId)
      ?? session.metadataSources.find(row => row.provider === 'tmdb')
      ?? session.metadataSources[0]
      ?? null;
  }

  function playbackMetadata(session) {
    const selected = playbackMetadataSource(session);
    return selected ? session.metadataViews.get(selected.id)?.display ?? selected.metadata : null;
  }

  function baselineMetadata(session) {
    const primary = playbackMetadataSource(session);
    if (!primary) return null;
    const other = session.metadataSources.find(row => row.provider !== primary.provider);
    return other ? session.metadataViews.get(other.id)?.display ?? other.metadata : null;
  }

  function selectedMetadataSource(session) {
    if (typeof session.browsing.sourceId === 'string' && session.browsing.sourceId.startsWith('meta:')) {
      return session.metadataSources.find(row => row.id === session.browsing.sourceId) ?? null;
    }
    return null;
  }

  function episodeProjection(session, metadata) {
    if (!metadata) return { map: [], visible: [], keys: new Set(), visibleKeys: new Set(), seasons: new Map() };
    const today = new Date(Date.now() + 8 * 3_600_000).toISOString().slice(0, 10);
    const previous = session.episodeProjection;
    if (previous?.metadata === metadata && previous.today === today &&
        previous.preview === session.previewEpisodes) return previous;
    const map = episodeMap(metadata);
    const next = metadata?.next_episode_to_air;
    const nextSeason = next?.season_number, nextEpisode = next?.episode_number;
    const hasNext = Number.isSafeInteger(nextSeason) && Number.isSafeInteger(nextEpisode) &&
      nextSeason > 0 && nextEpisode > 0 && metadata?.status !== 'Ended';
    const lastEpisode = next?.air_date && next.air_date <= today ? nextEpisode : nextEpisode - 1;
    const visible = map.filter(row => session.previewEpisodes || (!row.air_date || row.air_date <= today) &&
      (!hasNext || row.season < nextSeason || row.season === nextSeason && row.episode <= lastEpisode));
    const canTogglePreview = map.length > visible.length || session.previewEpisodes && map.some(row =>
      (row.air_date && row.air_date > today) ||
      (hasNext && (row.season > nextSeason || row.season === nextSeason && row.episode > lastEpisode)));
    return session.episodeProjection = {
      metadata,
      today,
      preview: session.previewEpisodes,
      canTogglePreview,
      map,
      visible,
      keys: new Set(map.map(row => `${row.season}:${row.episode}`)),
      visibleKeys: new Set(visible.map(row => `${row.season}:${row.episode}`)),
      seasons: new Map(),
    };
  }

  function metadataCacheState(session, id) {
    const view = session.metadataViews.get(id);
    if (!view?.cache) return null;
    const base = clone(view.cache);
    if (view.expiresAt <= Date.now()) base.status = 'stale';
    return base;
  }

  function acceptMetadataCache(session, id, entry, season, display) {
    const view = session.metadataViews.get(id);
    if (!view) return;
    if (season === undefined) {
      view.cache = entry?.cache ?? null;
      view.expiresAt = entry?.expiresAt ?? Infinity;
    } else {
      view.display = display;
      view.loadedSeasons.add(season);
    }
  }

  function metadataDisplay(session, row) {
    const base = row.provider === 'douban' && row.seasons?.length
      ? { ...row.metadata, doubanSeasons: row.seasons }
      : row.metadata;
    const id = row.id ?? metadataIdentity(row);
    const previous = session.metadataViews.get(id);
    if (previous && (previous.base === base || isDeepStrictEqual(previous.base, base))) {
      return previous.display;
    }
    const loadedSeasons = new Set((base?.doubanSeasons ?? base?.seasons ?? [])
      .filter(s => Array.isArray(s.episodes))
      .map(s => s.season_number ?? s.season));
    session.metadataViews.set(id, { ...previous, base, display: base, loadedSeasons });
    return base;
  }

  function installMetadataRow(session, provider, kind, metadataIdValue, metadata, seasons) {
    const title = text(metadata?.title ?? metadata?.name) || session.fallbackTitle;
    const row = {
      provider,
      kind,
      metadataId: String(metadataIdValue),
      title,
      metadata,
      ...(seasons?.length ? { seasons } : Array.isArray(metadata?.doubanSeasons) ? { seasons: metadata.doubanSeasons } : {}),
    };
    row.id = metadataIdentity(row);
    const idx = session.metadataSources.findIndex(item => item.provider === provider);
    if (idx >= 0) session.metadataSources[idx] = row;
    else session.metadataSources.push(row);
    session.metadataSources.sort((a, b) => (a.provider === 'tmdb' ? 0 : 1) - (b.provider === 'tmdb' ? 0 : 1));
    metadataDisplay(session, row);
    session.kind = kind;
    if (provider === 'tmdb') session.tmdbSlot = { kind, id: Number(metadataIdValue) };
    if (provider === 'douban') session.doubanSlot = { kind, id: String(metadataIdValue) };
    const preferred = session.metadataSources.find(item => item.provider === 'tmdb') ?? session.metadataSources[0];
    session.playbackMetadataSourceId = preferred?.id ?? null;
    return row;
  }

  function initializePlayIntentFromMetadata(session, previousHistory) {
    const primary = playbackMetadataSource(session);
    if (!primary) return;
    session.kind = primary.kind;
    if (primary.kind === 'movie') {
      session.playIntent = { mode: 'smart', kind: 'movie' };
      if (previousHistory) session.resumePosition = historyPosition(previousHistory);
      return;
    }
    const display = session.metadataViews.get(primary.id)?.display ?? primary.metadata;
    const map = episodeMap(display);
    const positiveEpisodes = map.filter(row => row.season > 0 && row.episode > 0);
    if (!positiveEpisodes.length) return;

    let chosen = null;
    if (session.requestedEpisode !== undefined) {
      chosen = resolveEpisode(positiveEpisodes, {
        ...(session.requestedSeason !== undefined ? { season: session.requestedSeason } : {}),
        episode: session.requestedEpisode,
      }) ?? positiveEpisodes.find(row => row.episode === session.requestedEpisode) ?? null;
    }
    if (!chosen && session.requestedSeason !== undefined) {
      chosen = positiveEpisodes.find(row => row.season === session.requestedSeason) ?? null;
    }
    if (!chosen && previousHistory?.episode?.episode > 0) {
      chosen = positiveEpisodes.find(row =>
        (previousHistory.episode.season === undefined || row.season === previousHistory.episode.season) &&
        row.episode === previousHistory.episode.episode) ?? null;
    }
    // Fix standalone Season 2 (e.g. 庆余年 第二季) where globalEpisode 1 does not exist:
    // fall back cleanly to the first available positive episode!
    chosen ??= resolveEpisode(positiveEpisodes, { globalEpisode: 1 }) ?? positiveEpisodes[0];

    session.playIntent = {
      mode: 'smart',
      kind: 'tv',
      season: chosen.season,
      episode: chosen.episode,
      ...(chosen.globalEpisode !== undefined ? { globalEpisode: chosen.globalEpisode } : {}),
    };
    if (session.browsing.season === null || session.browsing.season === undefined) {
      session.browsing.season = chosen.season;
    }
    if (previousHistory?.episode &&
        chosen.season === previousHistory.episode.season &&
        chosen.episode === previousHistory.episode.episode) {
      session.resumePosition = historyPosition(previousHistory);
    }
  }

  async function ensureSeasonLoaded(session, row, season, { canSchedule = () => true, refresh = false, requestedEpisode } = {}) {
    if (!row || row.kind !== 'tv' || !Number.isSafeInteger(season) || season < 0) return null;
    const view = session.metadataViews.get(row.id);
    let display = view?.display ?? metadataDisplay(session, row);
    if (!refresh && view?.loadedSeasons.has(season)) return display;

    const subjectId = row.provider === 'douban'
      ? (display?.doubanSeasons ?? row.seasons ?? []).find(s => s.season === season)?.metadataId ?? row.metadataId
      : row.metadataId;
    const target = { kind: 'tv', metadataProvider: row.provider, metadataId: subjectId };
    const entry = await data.seasonMetadataEntry(target, season, {
      rules: session.rules,
      canSchedule,
      refresh,
      requestedEpisode,
    });
    if (!entry || !canSchedule()) return display;
    display = mergeSeason(row.provider, display, season, entry.value);

    // Enrich Douban generated episodes with TMDB episode titles/stills when TMDB is also mounted.
    if (row.provider === 'douban') {
      const tmdbRow = session.metadataSources.find(item => item.provider === 'tmdb' && item.kind === 'tv');
      if (tmdbRow) {
        let tmdbDisplay = session.metadataViews.get(tmdbRow.id)?.display ?? metadataDisplay(session, tmdbRow);
        const sameSeasons = sameSeasonPartition(display, tmdbDisplay);
        const neededSeasons = new Set(sameSeasons ? [season]
          : matchEpisodeMaps(display, tmdbDisplay).filter(r => r.episode.season === season && r.match).map(r => r.match.season));
        for (const num of neededSeasons) {
          if (!session.metadataViews.get(tmdbRow.id)?.loadedSeasons.has(num)) {
            const tmdbSeason = await data.seasonMetadataEntry(
              { kind: 'tv', metadataProvider: 'tmdb', metadataId: tmdbRow.metadataId },
              num,
              { rules: session.rules, canSchedule, refresh },
            ).catch(() => null);
            if (tmdbSeason && canSchedule()) {
              tmdbDisplay = mergeSeason('tmdb', tmdbDisplay, num, tmdbSeason.value);
              acceptMetadataCache(session, tmdbRow.id, tmdbSeason, num, tmdbDisplay);
            }
          }
        }
        const mergedEpisodes = matchEpisodeMaps(display, tmdbDisplay)
          .filter(r => r.episode.season === season)
          .map(({ episode, match }) => ({
            ...(match ? fields(match, ['name', 'overview', 'still_path', 'runtime', 'air_date']) : {}),
            ...episode,
          }));
        display = mergeSeason('douban', display, season, { ...entry.value, episodes: mergedEpisodes });
      }
    }

    acceptMetadataCache(session, row.id, entry, season, display);
    return display;
  }

  async function discoverAssociations(session) {
    const primary = session.metadataSources[0];
    if (!primary) return;
    const epoch = session.metadataEpoch;
    const admitted = () => !session.released && !ctx.isClosed() && session.metadataEpoch === epoch;
    try {
      const rows = await data.metadataSources({
        kind: primary.kind,
        title: primary.title,
        metadataProvider: primary.provider,
        metadataId: primary.metadataId,
      }, { rules: session.rules, canSchedule: admitted });
      if (!admitted() || !rows.length) return;
      for (const item of rows) {
        installMetadataRow(session, item.provider, item.kind, item.metadataId, item.metadata, item.seasons);
      }
      // If TMDB was discovered after Douban seed, align browsing & playback metadata to preferred provider.
      const preferredProvider = ctx.preferences().metadataViewProvider;
      const preferred = session.metadataSources.find(item => item.provider === preferredProvider)
        ?? session.metadataSources.find(item => item.provider === 'tmdb')
        ?? session.metadataSources[0];
      if (preferred && !session.userBrowsedSource && !session.noSources &&
          typeof session.browsing.sourceId === 'string' && session.browsing.sourceId.startsWith('meta:') &&
          session.browsing.sourceId !== preferred.id) {
        session.browsing.sourceId = preferred.id;
        if (preferred.kind === 'tv' && session.browsing.season !== null) {
          await ensureSeasonLoaded(session, preferred, session.browsing.season, { canSchedule: admitted });
        }
      }
      ctx.publish(session);
    } catch {
      // Association enrichment failure is silent; primary metadata remains intact.
    }
  }

  function buildWorkInfo(session) {
    const tmdbRow = session.metadataSources.find(row => row.provider === 'tmdb') ?? null;
    const doubanRow = session.metadataSources.find(row => row.provider === 'douban') ?? null;
    return catalog.formatWorkInfo({
      fallbackTitle: session.fallbackTitle,
      fallbackPoster: session.fallbackPoster,
      fallbackKind: session.kind,
      keyword: session.keyword,
      targetId: session.targetId,
      tmdbRow: tmdbRow ? {
        ...tmdbRow,
        metadata: session.metadataViews.get(tmdbRow.id)?.display ?? tmdbRow.metadata,
      } : null,
      doubanRow: doubanRow ? {
        ...doubanRow,
        metadata: session.metadataViews.get(doubanRow.id)?.display ?? doubanRow.metadata,
      } : null,
    });
  }

  function requireFreshMetadata(session) {
    const primary = playbackMetadataSource(session);
    if (!primary) return;
    const cache = metadataCacheState(session, primary.id);
    if (cache?.status === 'stale') throw fail(cache.error?.code ?? 'STALE_METADATA');
  }

  return {
    episodeMap,
    playbackMetadataSource,
    playbackMetadata,
    baselineMetadata,
    selectedMetadataSource,
    episodeProjection,
    metadataCacheState,
    acceptMetadataCache,
    metadataDisplay,
    installMetadataRow,
    initializePlayIntentFromMetadata,
    ensureSeasonLoaded,
    discoverAssociations,
    buildWorkInfo,
    requireFreshMetadata,
  };
}
