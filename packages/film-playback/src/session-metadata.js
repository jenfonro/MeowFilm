import { isDeepStrictEqual } from 'node:util';
import { buildEpisodeMap, matchEpisodeMaps, resolveEpisode, sameSeasonPartition } from './episodes.js';
import { clone, codeOf, fail, fields, object, requireValue, text } from './identity.js';
import { episodeTarget, historyPosition, mergeSeason, metadataIdentity } from './session-helpers.js';

export function createSessionMetadata(ctx) {
  const { data, library, catalog, episodeMaps } = ctx;

  function episodeMap(metadata) {
    if (!metadata) return [];
    let map = episodeMaps.get(metadata);
    if (!map) { map = buildEpisodeMap(metadata); episodeMaps.set(metadata, map); }
    return map;
  }

  function playbackMetadataSource(session) {
    return session.metadataSources.find(row => row.id === session.playbackMetadataSourceId);
  }

  function playbackMetadata(session) {
    const selected = playbackMetadataSource(session);
    return selected ? session.metadataViews.get(selected.id)?.display ?? selected.metadata : null;
  }

  function episodeProjection(session, metadata = session.metadata) {
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
    return session.episodeProjection = {
      metadata, today, preview: session.previewEpisodes,
      map, visible, keys: new Set(map.map(row => `${row.season}:${row.episode}`)),
      visibleKeys: new Set(visible.map(row => `${row.season}:${row.episode}`)), seasons: new Map(),
    };
  }

  function visibleEpisodes(session, allSeasons = false) {
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    if ((selected?.kind ?? session.target.kind) !== 'tv') return [];
    if (!allSeasons && (session.browsing.loading && !session.browsing.refreshing || session.browsing.error)) return [];
    const { map, visible, seasons } = episodeProjection(session);
    if (allSeasons) return visible;
    const season = session.browsing.season ?? session.target.season ?? map.find(row => row.season > 0)?.season;
    if (!seasons.has(season)) seasons.set(season, visible.filter(row => row.season === season));
    return seasons.get(season);
  }

  function workInfo(session) {
    const known = session.metadataSources.filter(row => object(row.metadata));
    const primary = known.find(row => row.provider === 'tmdb' &&
      session.entryTarget.metadataProvider === 'tmdb' &&
      String(row.metadataId) === String(session.entryTarget.metadataId)) ??
      (known.filter(row => row.provider === 'tmdb').length === 1
        ? known.find(row => row.provider === 'tmdb') : null);
    const related = known.filter(row => row.provider === 'douban' && (!primary ||
      row.association?.targetProvider === 'tmdb' &&
        String(row.association.targetMetadataId) === String(primary.metadataId) ||
      primary.association?.targetProvider === 'douban' &&
        String(primary.association.targetMetadataId) === String(row.metadataId)));
    const douban = related.find(row => session.entryTarget.metadataProvider === 'douban' &&
      String(row.metadataId) === String(session.entryTarget.metadataId)) ??
      (related.length === 1 ? related[0] : null);
    const selected = primary ?? douban;
    if (!selected) return null;
    const count = value => {
      const number = typeof value === 'string' && /^\d+$/.test(value) ? Number(value) : value;
      return Number.isSafeInteger(number) && number >= 0 ? number : null;
    };
    const metadata = selected.provider === 'douban' && selected.seasons?.length
      ? { ...selected.metadata, doubanSeasons: selected.seasons } : selected.metadata;
    let seasonCount = selected.kind === 'tv' ? count(metadata.number_of_seasons) : null;
    let episodeCount = selected.kind === 'tv'
      ? count(metadata.number_of_episodes ?? metadata.episodes_count) : null;
    if (selected.provider === 'douban' && selected.kind === 'tv') {
      const seasons = (selected.seasons ?? []).filter(row => Number.isSafeInteger(row.season) && row.season > 0);
      seasonCount = seasons.length || null;
      const counts = seasons.map(row => count(row.episodeCount));
      episodeCount = counts.length && counts.every(value => value !== null)
        ? counts.reduce((sum, value) => sum + value, 0) : null;
    }
    const summary = text(metadata.overview) || text(metadata.intro) ||
      (primary ? text(douban?.metadata.intro) || text(douban?.metadata.overview) : '');
    const value = clone(metadata);
    if (primary && !text(metadata.overview) && !text(metadata.intro) && summary) value.overview = summary;
    const path = `${selected.provider === 'douban' ? '/rexxar/api/v2' : '/3'}/${selected.kind}/${selected.metadataId}`;
    return {
      provider: selected.provider, metadataId: String(selected.metadataId), kind: selected.kind,
      title: text(metadata.title) || text(metadata.name) || selected.title,
      summary, seasonCount, episodeCount,
      metadata: catalog.metadata(selected.provider, path, value, session.entryTarget),
    };
  }

  function metadataCacheState(session, id = session.metadataSourceId) {
    const view = session.metadataViews.get(id);
    const base = clone(view?.cache ?? null);
    if (base && view.expiresAt <= Date.now()) base.status = 'stale';
    const season = view?.seasonCache;
    if (!season) return base;
    const error = base?.error ?? season.cache.error;
    return {
      ...clone(base ?? season.cache),
      status: base?.status === 'stale' || season.cache.status === 'stale' || season.expiresAt <= Date.now() ? 'stale' : 'fresh',
      ...(error ? { error: clone(error) } : {}),
      components: {
        ...clone(base?.components ?? {}),
        [`season:${season.season}`]: {
          status: season.cache.error ? 'failed' : 'ready',
          fetchedAt: season.fetchedAt, ...(season.cache.error ? { error: clone(season.cache.error) } : {}),
        },
      },
    };
  }

  function acceptMetadataCache(session, entry, season, supplementError,
    id = session.metadataSourceId, display = session.metadata) {
    const view = session.metadataViews.get(id);
    if (!view) return;
    if (season === undefined) {
      view.cache = entry?.cache ?? null;
      view.expiresAt = entry?.expiresAt;
      view.seasonCache = null;
    } else {
      view.seasonCache = { season, cache: entry.cache, fetchedAt: entry.fetchedAt, expiresAt: entry.expiresAt };
      view.display = display; view.loadedSeasons.add(season);
    }
    if (id === session.metadataSourceId)
      session.metadataError = metadataCacheState(session)?.error?.code ?? supplementError ?? null;
  }

  function requireFreshMetadata(session) {
    const cache = metadataCacheState(session, session.playbackMetadataSourceId);
    if (cache?.status === 'stale') throw fail(cache.error?.code ?? 'STALE_METADATA');
  }

  function associatedTMDB(session, selected) {
    if (selected?.provider !== 'douban') return null;
    const associated = session.metadataSources.filter(row => row.provider === 'tmdb' && row.kind === selected.kind &&
      (row.association?.targetProvider === 'douban' &&
        String(row.association.targetMetadataId) === String(selected.metadataId) ||
       selected.association?.targetProvider === 'tmdb' &&
        String(selected.association.targetMetadataId) === String(row.metadataId)));
    return associated.length === 1 ? associated[0] : null;
  }

  function metadataSupplement(session) {
    const selected = associatedTMDB(session, session.metadataSources.find(row => row.id === session.metadataSourceId));
    return selected ? {
      ...fields(selected, ['provider', 'metadataId', 'kind']),
      metadata: catalog.metadata(selected.provider, `/3/${selected.kind}/${selected.metadataId}`, selected.metadata),
    } : null;
  }

  function metadataDisplay(session, row) {
    const base = row.provider === 'douban' && row.seasons?.length
      ? { ...row.metadata, doubanSeasons: row.seasons } : row.metadata;
    const id = row.id ?? metadataIdentity(row), previous = session.metadataViews.get(id);
    const associated = associatedTMDB(session, row);
    const reference = associated ? fields(associated, ['provider', 'metadataId', 'kind', 'metadata', 'association']) : null;
    if (previous && (previous.base === base || isDeepStrictEqual(previous.base, base)) &&
        isDeepStrictEqual(previous.reference, reference)) return previous.display;
    const loadedSeasons = new Set((base?.doubanSeasons ?? base?.seasons ?? [])
      .filter(season => Array.isArray(season.episodes)).map(season => season.season_number ?? season.season));
    session.metadataViews.set(id, { ...previous, base, display: base, reference, loadedSeasons });
    return base;
  }

  async function loadPlaybackMetadata(session, generation) {
    const selected = playbackMetadataSource(session);
    requireValue(selected, 'NOT_FOUND');
    const cache = metadataCacheState(session, selected.id);
    if (cache && cache.status !== 'stale') return;
    const target = {
      ...session.target, kind: selected.kind,
      metadataProvider: selected.provider, metadataId: selected.metadataId,
    };
    let entry;
    try {
      entry = await data.targetMetadataEntry(target, {
        rules: session.rules,
        canSchedule: () => ctx.valid(session, generation),
      });
    } catch (error) {
      if (ctx.valid(session, generation) && selected.id === session.metadataSourceId)
        session.metadataError = codeOf(error);
      throw error;
    }
    if (!ctx.valid(session, generation)) return;
    selected.metadata = entry.value;
    const display = metadataDisplay(session, selected);
    acceptMetadataCache(session, entry, undefined, undefined, selected.id, display);
    if (selected.id === session.metadataSourceId) session.metadata = display;
    requireFreshMetadata(session);
  }

  async function seasonDisplay(session, target, metadata, season, options) {
    const subject = target.metadataProvider === 'douban'
      ? metadata.doubanSeasons?.find(row => row.season === season)?.metadataId : undefined;
    const entry = await data.seasonMetadataEntry(subject === undefined ? target : { ...target, metadataId: subject },
      season, { rules: session.rules, ...options });
    if (!options.canSchedule()) return { entry, display: metadata };
    let display = mergeSeason(target, metadata, season, entry.value);
    const selected = session.metadataSources.find(row => row.provider === target.metadataProvider &&
      String(row.metadataId) === String(target.metadataId));
    const reference = associatedTMDB(session, selected);
    if (!reference) return { entry, display };
    let supplement = metadataDisplay(session, reference);
    const declared = (supplement.seasons ?? []).some(row => (row.season_number ?? row.season) === season);
    const needed = new Set(sameSeasonPartition(display, supplement) && declared ? [season]
      : matchEpisodeMaps(display, supplement).filter(row => row.episode.season === season && row.match)
        .map(row => row.match.season));
    const referenceTarget = { ...target, metadataProvider: 'tmdb', metadataId: reference.metadataId };
    let error;
    for (const number of needed) {
      try {
        const value = await data.seasonMetadataEntry(referenceTarget, number, {
          rules: session.rules,
          canSchedule: options.canSchedule, refresh: options.refresh,
        });
        if (!options.canSchedule()) return { entry, display };
        supplement = mergeSeason(referenceTarget, supplement, number, value.value);
        error ??= value.cache.error?.code ?? (value.cache.status === 'stale' ? 'STALE_METADATA' : undefined);
      } catch (caught) {
        if (!options.canSchedule()) return { entry, display };
        error ??= codeOf(caught);
      }
    }
    const episodes = matchEpisodeMaps(display, supplement).filter(row => row.episode.season === season)
      .map(({ episode, match }) => ({
        ...(match ? fields(match, ['name', 'overview', 'still_path', 'runtime', 'air_date']) : {}), ...episode,
      }));
    display = mergeSeason(target, display, season, { ...entry.value, episodes });
    if (isDeepStrictEqual(display, metadata)) display = metadata;
    const view = session.metadataViews.get(reference.id);
    if (view) {
      view.display = supplement;
      for (const row of supplement.seasons ?? [])
        if (Array.isArray(row.episodes)) view.loadedSeasons.add(row.season_number ?? row.season);
    }
    return { entry, display, error };
  }

  async function refreshPlaybackSeason(session, season, requestedEpisode) {
    const generation = session.generation, target = session.target;
    const selected = playbackMetadataSource(session);
    requireValue(selected, 'NOT_FOUND');
    const admitted = () => ctx.valid(session, generation) && session.target === target;
    try {
      const entry = await data.targetMetadataEntry(target, { rules: session.rules, canSchedule: admitted });
      if (!admitted()) return;
      const display = metadataDisplay(session, { ...selected, metadata: entry.value });
      const value = await seasonDisplay(session, target, display, season, { canSchedule: admitted, requestedEpisode });
      if (!admitted()) return;
      selected.metadata = entry.value;
      acceptMetadataCache(session, entry, undefined, undefined, selected.id, value.display);
      acceptMetadataCache(session, value.entry, season, value.error, selected.id, value.display);
      if (selected.id === session.metadataSourceId) session.metadata = value.display;
      requireFreshMetadata(session);
      ctx.publish(session);
    } catch (error) {
      if (admitted() && selected.id === session.metadataSourceId) session.metadataError = codeOf(error);
      throw error;
    }
  }

  function initializeEpisode(session, previous) {
    if (session.target.episode === undefined) {
      const episode = session.target.globalEpisode === undefined && previous ? previous.episode
        : resolveEpisode(episodeMap(playbackMetadata(session)), { globalEpisode: session.target.globalEpisode ?? 1 });
      if (episode && (session.target.globalEpisode === undefined || session.target.season === undefined ||
          session.target.season === episode.season)) session.target = episodeTarget(session.target, episode);
    }
    if (previous && session.target.episode !== undefined && session.target.season === previous.episode?.season &&
        session.target.episode === previous.episode?.episode &&
        (session.target.globalEpisode === undefined || previous.episode?.globalEpisode === undefined ||
          session.target.globalEpisode === previous.episode.globalEpisode)) session.resumePosition = historyPosition(previous);
  }

  async function discoverMetadata(session, target = catalog.content(session.entryTarget) ?? session.entryTarget) {
    if (target.metadataProvider === 'douban' && session.entryResolution === 'matched') {
      const primary = session.metadataSources.filter(row => row.provider === 'tmdb');
      if (primary.length === 1) target = {
        ...target, kind: primary[0].kind,
        metadataProvider: 'tmdb', metadataId: primary[0].metadataId,
      };
    }
    if (!target.metadataProvider) target = session.searchMetadataTarget ?? target;
    const epoch = session.metadataEpoch;
    const admitted = () => !session.released && !session.stopped && !ctx.isClosed() &&
      epoch === session.metadataEpoch;
    try {
      const rows = await data.metadataSources(target, { rules: session.rules, canSchedule: admitted });
      if (!admitted()) return;
      const sources = rows.map(row => ({ ...clone(row), id: metadataIdentity(row) }));
      // A successful discovery is the current proof set. Retaining an omitted
      // association would keep showing a supplement after it became ambiguous.
      session.metadataSources = sources;
      for (const id of session.metadataViews.keys())
        if (!sources.some(row => row.id === id)) session.metadataViews.delete(id);
      const cache = metadataCacheState(session);
      session.metadataError = cache ? cache.error?.code ?? null : session.metadataError;
      const selected = (session.metadataViewInitialized
        ? sources.find(row => row.id === session.metadataSourceId) : null) ??
        sources.find(row => row.provider === ctx.preferences().metadataViewProvider) ??
        sources.find(row => row.provider === 'tmdb') ?? sources[0];
      session.metadataViewInitialized = true;
      const changed = selected && selected.id !== session.metadataSourceId;
      if (!selected) {
        session.metadata = null; session.metadataSourceId = null;
      } else if (changed) {
        await ctx.browsing.browse(session, session.browsing.sourceId, session.browsing.lineId,
          { metadataSourceId: selected.id });
        if (!admitted()) return;
      }
      if (!changed && selected?.provider === 'douban' && selected.seasons) {
        const generation = session.browseGeneration;
        const displaying = () => admitted() && generation === session.browseGeneration &&
          selected.id === session.metadataSourceId;
        const display = metadataDisplay(session, selected);
        const season = session.browsing.season ?? session.target.season ??
          buildEpisodeMap(display).find(row => row.season > 0)?.season;
        if (season !== undefined) {
          const value = await seasonDisplay(session, {
            ...session.target, kind: selected.kind, metadataProvider: selected.provider, metadataId: selected.metadataId,
          }, display, season, { canSchedule: displaying });
          if (!displaying()) return;
          session.metadata = value.display;
          acceptMetadataCache(session, value.entry, season, value.error);
        } else if (displaying()) session.metadata = display;
      }
      if (!session.restoreOnly && session.target.kind === 'tv' &&
          session.target.episode === undefined && session.generation === 0 && session.selectionEpoch === 0) {
        const previous = session.platform === 'windows'
          ? await library.history(session.target.id) : null;
        if (!admitted() || session.generation !== 0 || session.selectionEpoch !== 0) return;
        initializeEpisode(session, previous);
        if (session.target.episode !== undefined) {
          session.stage = 'idle'; session.error = null;
          if (session.platform === 'windows' || session.platform === 'ios' && ctx.preferences().iosAutoPrepare)
            void ctx.selection.start(session, { prepareOnly: session.platform === 'ios' });
        } else if (session.target.globalEpisode !== undefined) {
          session.stage = 'unavailable'; session.error = 'NO_EPISODE_MAPPING';
        }
      }
    } catch (error) {
      if (!admitted()) return;
      session.metadataError = codeOf(error);
    }
    ctx.publish(session);
  }

  async function browseMetadata(session, id, keepSource = false, context = {}) {
    const selected = session.metadataSources.find(row => row.id === id);
    requireValue(selected, 'NOT_FOUND');
    ctx.browsing.rememberBrowsing(session);
    const view = session.metadataViews.get(id);
    const saved = keepSource ? session.browsing : view?.browsing ??
      (view?.loadedSeasons.size ? { sourceId: null, lineId: null, loading: false, error: null,
        lines: [], episodes: [] } : undefined);
    const selectedSeason = context.season ?? saved?.season ??
      (id === session.metadataSourceId ? session.browsing.season : undefined);
    const generation = context.generation ?? ++session.browseGeneration;
    const epoch = session.metadataEpoch;
    const admitted = () => !session.released && !ctx.isClosed() &&
      generation === session.browseGeneration && epoch === session.metadataEpoch;
    const previousId = session.metadataSourceId, previousMetadata = session.metadata;
    session.metadataSourceId = id;
    session.metadata = view?.display ?? selected.metadata ?? null;
    session.browsing = saved ? { ...saved, loading: true, refreshing: true, error: null }
      : { sourceId: null, lineId: null, loading: true, error: null, lines: [], episodes: [] };
    ctx.publish(session);
    let loadedMetadata;
    try {
      const target = {
        ...session.target, kind: selected.kind,
        metadataProvider: selected.provider, metadataId: selected.metadataId,
      };
      delete target.members; delete target.source;
      const knownSeasons = (view?.display ?? selected.metadata)?.doubanSeasons ??
        (view?.display ?? selected.metadata)?.seasons ?? selected.seasons ?? [];
      const entry = !context.refresh && object(selected.metadata) && (context.season === undefined ||
          knownSeasons.some(row => (row.season_number ?? row.season) === context.season)) ? null
        : await data.targetMetadataEntry(target, {
          rules: session.rules, canSchedule: admitted, refresh: context.refresh === true,
        });
      if (!admitted()) return;
      const metadata = entry?.value ?? selected.metadata;
      let display = metadataDisplay(session, { ...selected, metadata });
      const coordinates = session.target.kind === 'group' ? {}
        : session.target.globalEpisode !== undefined ? { globalEpisode: session.target.globalEpisode }
        : id === previousId ? fields(session.target, ['season', 'episode']) : {};
      const mappedEpisode = value => id === previousId
        ? resolveEpisode(buildEpisodeMap(value), coordinates)
        : matchEpisodeMaps(value, previousMetadata).find(({ match }) => match &&
          (session.target.season !== undefined && session.target.episode !== undefined
            ? match.season === session.target.season && match.episode === session.target.episode
            : match.globalEpisode !== undefined && match.globalEpisode === session.target.globalEpisode))?.episode;
      let mapped = mappedEpisode(display);
      const seasons = display.doubanSeasons ?? display.seasons ?? [];
      if (context.season !== undefined)
        requireValue(target.kind === 'tv' && seasons.some(row =>
          (row.season_number ?? row.season) === context.season), 'NOT_FOUND');
      const firstSeason = seasons.find(row => (row.season_number ?? row.season) > 0) ?? seasons[0];
      const preferredSeason = selectedSeason ?? mapped?.season ?? (id === previousId
        ? coordinates.season ?? session.browsing.season
        : sameSeasonPartition(display, previousMetadata) ? session.target.season : undefined);
      const season = seasons.some(row => (row.season_number ?? row.season) === preferredSeason)
        ? preferredSeason : firstSeason?.season_number ?? firstSeason?.season;
      loadedMetadata = { entry, display, season };
      let seasonResult;
      if (target.kind === 'tv' && season !== undefined && (context.refresh ||
          !session.metadataViews.get(id)?.loadedSeasons.has(season))) {
        const value = await seasonDisplay(session, target, display, season,
          { canSchedule: admitted, refresh: context.refresh === true });
        if (!admitted()) return;
        display = value.display;
        seasonResult = value;
        mapped = mappedEpisode(display);
      }
      selected.metadata = metadata;
      session.metadata = display; session.metadataSourceId = id;
      if (entry) acceptMetadataCache(session, entry);
      if (seasonResult) acceptMetadataCache(session, seasonResult.entry, season, seasonResult.error);
      session.metadataError = metadataCacheState(session)?.error?.code ?? seasonResult?.error ?? null;
      loadedMetadata = null;
      session.browsing = { ...(saved ?? session.browsing), ...fields({ season }, ['season']), error: null };
      if (context.complete !== false) await ctx.browsing.refreshBrowsing(session);
      if (!admitted()) return;
      session.browsing = { ...session.browsing, loading: context.complete === false, refreshing: false };
      if (context.complete !== false) ctx.browsing.rememberBrowsing(session);
      ctx.publish(session);
      return true;
    } catch (error) {
      if (!admitted()) return;
      const code = codeOf(error);
      session.metadataError = code;
      if (saved) {
        if (loadedMetadata && id === session.metadataSourceId) {
          session.metadata = loadedMetadata.display;
          if (loadedMetadata.entry) {
            selected.metadata = loadedMetadata.entry.value;
            acceptMetadataCache(session, loadedMetadata.entry);
          }
          session.browsing = { ...session.browsing, ...fields(loadedMetadata, ['season']) };
        }
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: code };
      } else {
        session.browsing = { ...session.browsing, loading: false, error: code, episodes: [] };
      }
    }
    ctx.publish(session);
    return false;
  }

  async function refreshMetadataDisplay(session, { refresh = false, season, requestedEpisode } = {}) {
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    const target = selected ? {
      ...session.target, kind: selected.kind,
      metadataProvider: selected.provider, metadataId: selected.metadataId,
    } : session.target;
    if (!target.metadataProvider) return;
    const generation = ++session.browseGeneration, epoch = session.metadataEpoch;
    const admitted = () => !ctx.isClosed() && !session.released &&
      session.browseGeneration === generation && session.metadataEpoch === epoch;
    const previous = session.browsing;
    session.browsing = { ...previous, loading: true, refreshing: true, error: null };
    ctx.publish(session);
    let failure;
    try {
      const entry = await data.targetMetadataEntry(target, { rules: session.rules, canSchedule: admitted, refresh });
      if (!admitted()) return;
      let display = selected ? metadataDisplay(session, { ...selected, metadata: entry.value }) : entry.value;
      const requestedSeason = season ?? previous.season ?? session.target.season;
      let seasonEntry, supplementError;
      const declaredSeason = (display.doubanSeasons ?? display.seasons ?? [])
        .some(row => (row.season_number ?? row.season) === requestedSeason);
      if (target.kind === 'tv' && requestedSeason !== undefined && declaredSeason) {
        const value = await seasonDisplay(session, target, display, requestedSeason,
          { canSchedule: admitted, refresh, requestedEpisode });
        if (!admitted()) return;
        display = value.display;
        seasonEntry = value.entry;
        supplementError = value.error;
      }
      session.metadata = display;
      acceptMetadataCache(session, entry);
      if (seasonEntry) acceptMetadataCache(session, seasonEntry, requestedSeason, supplementError);
      if (selected) {
        selected.metadata = entry.value;
        if (selected.provider === 'tmdb') selected.seasons = display.seasons;
      }
      session.browsing = {
        ...previous, ...fields({ season: requestedSeason }, ['season']),
        loading: false, refreshing: false, error: null,
      };
      ctx.browsing.rememberBrowsing(session);
      session.initializationError = null;
      if (session.target.metadataProvider) ctx.detached(session, discoverMetadata(session));
      // A data refresh updates presentation only. An already accepted file,
      // its local/global coordinates and its native lease keep their identity.
    } catch (error) {
      if (!admitted()) return;
      session.metadataError = failure = codeOf(error);
      session.browsing = { ...previous, loading: false, refreshing: false, error: failure };
    }
    ctx.publish(session);
    return failure;
  }

  return {
    episodeMap, playbackMetadataSource, playbackMetadata, episodeProjection, visibleEpisodes,
    workInfo, metadataCacheState, acceptMetadataCache, requireFreshMetadata,
    metadataSupplement, metadataDisplay, loadPlaybackMetadata, refreshPlaybackSeason,
    initializeEpisode, discoverMetadata, browseMetadata, refreshMetadataDisplay,
  };
}
