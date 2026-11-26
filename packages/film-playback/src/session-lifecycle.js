import { randomUUID } from 'node:crypto';
import { createRecognitionRules, providerFamily } from './rules.js';
import { clone, codeOf, fields, identity, isIdentifier, normalizeWorkKey, object,
  requireValue, text, workTargetId } from './identity.js';
import { IDLE_SESSION_TTL_MS, MAX_IDLE_SESSIONS, historyPosition } from './session-helpers.js';

export function createSessionLifecycle(ctx) {
  const { data, library, media, rules, sourceRegistry, sessions, sessionsByKey, debug } = ctx;

  function computeNoSources(session) {
    const hasEnabledSite = session.sources.some(src => src.enabled !== false);
    const hasManualShare = library.read().manual.some(row =>
      row?.targetId === session.targetId && row.enabled !== false && row.share);
    return !hasEnabledSite && !hasManualShare;
  }

  function clearIdleTimer(session) {
    if (session.idleTimer) {
      clearTimeout(session.idleTimer);
      session.idleTimer = null;
    }
  }

  function scheduleIdleEviction(session) {
    clearIdleTimer(session);
    session.detachedAt = Date.now();
    session.idleTimer = setTimeout(() => {
      void destroySession(session);
    }, IDLE_SESSION_TTL_MS);
    session.idleTimer.unref?.();

    // Enforce bounded LRU of detached idle sessions
    const detachedList = [...sessions.values()]
      .filter(s => !s.attached && !s.released && s !== session)
      .sort((a, b) => (a.detachedAt ?? 0) - (b.detachedAt ?? 0));
    while (detachedList.length >= MAX_IDLE_SESSIONS) {
      const oldest = detachedList.shift();
      if (oldest) void destroySession(oldest);
    }
  }

  async function ingestEntryContext(session, entry, searchEntry, { isReentry = false } = {}) {
    if (entry.title && (!isReentry || !session.fallbackTitle)) session.fallbackTitle = text(entry.title);
    if (entry.poster && (!isReentry || !session.fallbackPoster)) session.fallbackPoster = text(entry.poster);
    if (entry.kind === 'movie' || entry.kind === 'tv') session.kind = entry.kind;
    if (Number.isSafeInteger(entry.year) && entry.year > 0) session.year = entry.year;

    // 1. Mount TMDB / Douban slots if provided by entry or search handoff
    if (object(entry.tmdb) && ['movie', 'tv'].includes(entry.tmdb.kind) && Number.isSafeInteger(entry.tmdb.id) && entry.tmdb.id > 0) {
      session.tmdbSlot = { kind: entry.tmdb.kind, id: entry.tmdb.id };
      session.kind = entry.tmdb.kind;
    } else if (searchEntry?.tmdb) {
      session.tmdbSlot = { kind: searchEntry.tmdb.kind, id: searchEntry.tmdb.id };
      session.kind = searchEntry.tmdb.kind;
      if (searchEntry.tmdb.detail) {
        ctx.metadata.installMetadataRow(session, 'tmdb', searchEntry.tmdb.kind, searchEntry.tmdb.id, searchEntry.tmdb.detail);
      }
    }
    if (object(entry.douban) && ['movie', 'tv'].includes(entry.douban.kind) && text(entry.douban.id)) {
      session.doubanSlot = { kind: entry.douban.kind, id: text(entry.douban.id) };
      session.kind = entry.douban.kind;
    }

    // 2. Sync search cursors if entering from a search session for the exact same canonical keyword
    if (searchEntry?.searchCursors?.length) {
      for (const cur of searchEntry.searchCursors) {
        const state = session.search.get(cur.sourceKey);
        if (state && cur.page >= state.page) {
          state.page = cur.page;
          state.completed ||= Boolean(cur.completed);
        }
      }
    }

    // 3. Ingest matched group items from search session
    if (searchEntry?.groupItems?.length) {
      const bySite = new Map();
      for (const item of searchEntry.groupItems) {
        if (!bySite.has(item.siteKey)) bySite.set(item.siteKey, []);
        bySite.get(item.siteKey).push(item);
      }
      for (const [siteKey, rows] of bySite) {
        const source = session.sources.find(s => s.key === siteKey);
        if (source) await ctx.candidates.addResults(session, source, rows, { kind: 'result' });
      }
    }

    // 4. Ingest cross-query display attachments (e.g. searched "仙逆", clicked "仙逆高清")
    if (searchEntry?.crossQueryItems?.length) {
      const bySite = new Map();
      for (const item of searchEntry.crossQueryItems) {
        if (!bySite.has(item.siteKey)) bySite.set(item.siteKey, []);
        bySite.get(item.siteKey).push(item);
      }
      for (const [siteKey, rows] of bySite) {
        const source = session.sources.find(s => s.key === siteKey);
        if (source) await ctx.candidates.addResults(session, source, rows, { kind: 'result', attachedDisplayOnly: true });
      }
    }

    // 5. Ingest explicit siteEntry (from Site Category card or Search Raw split card)
    let clickedResultId = null;
    const rawSite = searchEntry?.selectedSite ?? entry.siteEntry ?? null;
    if (rawSite && isIdentifier(rawSite.detailId)) {
      const matchedSource = session.sources.find(s =>
        (rawSite.sourceKey && s.key === rawSite.sourceKey) ||
        (rawSite.installationId && rawSite.siteKey &&
          s.installationId === rawSite.installationId && s.siteId === rawSite.siteKey));
      if (matchedSource) {
        await ctx.candidates.addResults(session, matchedSource, [{
          id: rawSite.detailId,
          detailId: rawSite.detailId,
          rawTitle: text(rawSite.rawTitle) || text(entry.title),
          vod_name: text(rawSite.rawTitle) || text(entry.title),
          vod_remarks: text(rawSite.remark),
          seasonHint: rawSite.seasonHint ?? entry.seasonHint,
        }], { kind: 'linked' });
        clickedResultId = identity(matchedSource.key, rawSite.detailId);
      }
    }

    return {
      clickedResultId,
      isRawSearchCard: Boolean(searchEntry?.isRaw),
      isSingleSiteCategoryCard: Boolean(!searchEntry && entry.siteEntry && clickedResultId),
    };
  }

  async function initialize(session, entryIntent) {
    const epoch = session.metadataEpoch;
    const browseGen = ++session.browseGeneration;
    const admitted = () => !session.released && !ctx.isClosed() && session.metadataEpoch === epoch;

    try {
      // Step 1: Load / enrich metadata slots according to strict configuration & guest Douban boundaries!
      if (session.doubanSlot) {
        const doubanEntry = await data.targetMetadataEntry({
          kind: session.doubanSlot.kind,
          title: session.fallbackTitle,
          metadataProvider: 'douban',
          metadataId: session.doubanSlot.id,
        }, { rules: session.rules, canSchedule: admitted });
        if (!admitted()) return;
        if (doubanEntry?.value) {
          const row = ctx.metadata.installMetadataRow(
            session, 'douban', session.doubanSlot.kind, session.doubanSlot.id,
            doubanEntry.value, doubanEntry.value.doubanSeasons,
          );
          ctx.metadata.acceptMetadataCache(session, row.id, doubanEntry);
        }
        // Only resolve TMDB cross-match if TMDB is actually configured!
        if (!session.tmdbSlot && await data.isMetadataConfigured('tmdb', { checkSchedule: admitted })) {
          const resolvedTmdb = await data.tmdbTargetEntry({
            kind: session.doubanSlot.kind,
            title: doubanEntry?.value?.title ?? session.fallbackTitle,
            ...(session.year ? { year: session.year } : {}),
          }, { metadata: doubanEntry?.value, rules: session.rules, canSchedule: admitted }).catch(() => null);
          if (!admitted()) return;
          if (resolvedTmdb?.entry?.value) {
            const row = ctx.metadata.installMetadataRow(
              session, 'tmdb', resolvedTmdb.target.kind, resolvedTmdb.target.metadataId,
              resolvedTmdb.entry.value, resolvedTmdb.entry.value.seasons,
            );
            ctx.metadata.acceptMetadataCache(session, row.id, resolvedTmdb.entry);
          }
        }
      } else if (session.tmdbSlot) {
        if (await data.isMetadataConfigured('tmdb', { checkSchedule: admitted })) {
          const tmdbEntry = await data.targetMetadataEntry({
            kind: session.tmdbSlot.kind,
            title: session.fallbackTitle,
            metadataProvider: 'tmdb',
            metadataId: session.tmdbSlot.id,
          }, { rules: session.rules, canSchedule: admitted });
          if (!admitted()) return;
          if (tmdbEntry?.value) {
            const row = ctx.metadata.installMetadataRow(
              session, 'tmdb', session.tmdbSlot.kind, session.tmdbSlot.id,
              tmdbEntry.value, tmdbEntry.value.seasons,
            );
            ctx.metadata.acceptMetadataCache(session, row.id, tmdbEntry);
          }
        }
      } else {
        // Pure site card (Search or Site Category): only look up TMDB if configured, or Douban if Cookie configured!
        const resolvedTmdb = await data.tmdbTargetEntry({
          kind: session.kind,
          title: session.keyword || session.fallbackTitle,
          ...(session.year ? { year: session.year } : {}),
        }, { rules: session.rules, canSchedule: admitted }).catch(() => null);
        if (!admitted()) return;
        if (resolvedTmdb?.entry?.value) {
          const row = ctx.metadata.installMetadataRow(
            session, 'tmdb', resolvedTmdb.target.kind, resolvedTmdb.target.metadataId,
            resolvedTmdb.entry.value, resolvedTmdb.entry.value.seasons,
          );
          ctx.metadata.acceptMetadataCache(session, row.id, resolvedTmdb.entry);
        } else {
          const resolvedDouban = await data.doubanTargetEntry({
            kind: session.kind,
            title: session.keyword || session.fallbackTitle,
          }, { rules: session.rules, canSchedule: admitted }).catch(() => null);
          if (!admitted()) return;
          if (resolvedDouban?.entry?.value) {
            const row = ctx.metadata.installMetadataRow(
              session, 'douban', resolvedDouban.target.kind, resolvedDouban.target.metadataId,
              resolvedDouban.entry.value, resolvedDouban.entry.value.doubanSeasons,
            );
            ctx.metadata.acceptMetadataCache(session, row.id, resolvedDouban.entry);
          }
        }
      }

      if (!admitted()) return;
      session.noSources = computeNoSources(session);

      // Step 2: Determine initial playIntent & resumePosition
      const previousHistory = await library.history(session.targetId);
      if (!admitted()) return;

      if (session.metadataSources.length > 0) {
        ctx.metadata.initializePlayIntentFromMetadata(session, previousHistory);
        const primaryMeta = ctx.metadata.playbackMetadataSource(session);
        if (primaryMeta?.kind === 'tv' && session.browsing.season !== null) {
          await ctx.metadata.ensureSeasonLoaded(session, primaryMeta, session.browsing.season, { canSchedule: admitted });
          if (!admitted()) return;
        }
      } else {
        // No official metadata on session
        const initialSeason = session.requestedSeason ?? previousHistory?.episode?.season ?? 1;
        const initialEpisode = session.requestedEpisode ?? previousHistory?.episode?.episode ?? 1;
        if (session.kind === 'movie') {
          session.playIntent = { mode: 'smart', kind: 'movie' };
          if (previousHistory) session.resumePosition = historyPosition(previousHistory);
        } else {
          session.playIntent = { mode: 'smart', kind: 'tv', season: initialSeason, episode: initialEpisode };
          session.browsing.season = initialSeason;
          if (previousHistory?.episode?.season === initialSeason && previousHistory?.episode?.episode === initialEpisode) {
            session.resumePosition = historyPosition(previousHistory);
            session.resumeCandidateId = previousHistory.candidateId;
          }
        }
      }

      // Apply single-site Category card constraint or Raw split card affinity!
      const { clickedResultId, isRawSearchCard, isSingleSiteCategoryCard } = entryIntent;
      const clickedResult = clickedResultId ? session.results.find(r => r.id === clickedResultId) : null;

      if (isSingleSiteCategoryCard && clickedResult) {
        // Decision 2: Site Category card selects that site directly and does NOT auto-search all sites!
        session.affinity = { sourceKey: clickedResult.source.key, detailId: clickedResult.source.detailId };
        session.playIntent = {
          ...session.playIntent,
          sourceFilter: { sourceKey: clickedResult.source.key, detailId: clickedResult.source.detailId },
        };
      } else if (isRawSearchCard && clickedResult) {
        // Requirement 2.2 & 3.4: Raw split card sets affinity to that exact site (never fetches Site #1!)
        session.affinity = { sourceKey: clickedResult.source.key, detailId: clickedResult.source.detailId };
      }

      // Step 3: Choose & load initial directory source!
      if (session.browseGeneration === browseGen) {
        if (session.noSources) {
          // Still display Douban/TMDB episodes in directory even when no video sources are configured!
          const metaRow = session.metadataSources[0] ?? null;
          session.browsing = {
            ...session.browsing,
            sourceId: metaRow?.id ?? null,
            loading: false,
            refreshing: false,
            error: null,
          };
        } else if (isSingleSiteCategoryCard && clickedResult) {
          await ctx.browsing.selectSource(session, clickedResult.id, { season: session.browsing.season ?? undefined });
        } else if (isRawSearchCard && clickedResult) {
          if (session.metadataSources.length > 0) {
            // Has TMDB/Douban: raw card selects the clicked site directly without touching Site #1!
            await ctx.browsing.selectSource(session, clickedResult.id, { season: session.browsing.season ?? undefined });
          } else if (ctx.browsing.hasSmartList(session)) {
            // No TMDB/Douban & >=2 results: raw card selects 智能列表 + jumps to card's season + fetches ONLY clickedResult!
            const rawSeason = session.requestedSeason ?? (clickedResult.seasonHint > 0 ? clickedResult.seasonHint : 1);
            session.browsing.season = rawSeason;
            session.playIntent = { ...session.playIntent, kind: 'tv', season: rawSeason, episode: session.requestedEpisode ?? 1 };
            await ctx.browsing.selectSource(session, 'smart', {
              season: rawSeason,
              preferredSmartResultId: clickedResult.id,
            });
            // Sync playIntent season & episode to actual selected season and available episodes
            const { seasonEpisodes } = ctx.browsing.smartListSeasons(session);
            const activeSeason = session.browsing.season ?? rawSeason;
            const eps = [...(seasonEpisodes.get(activeSeason)?.keys() ?? [])].sort((a, b) => a - b);
            if (session.playIntent.season !== activeSeason || (eps.length && !eps.includes(session.playIntent.episode))) {
              session.playIntent = {
                ...session.playIntent,
                season: activeSeason,
                episode: eps.length && !eps.includes(session.playIntent.episode) ? eps[0] : session.playIntent.episode,
              };
            }
          } else {
            await ctx.browsing.selectSource(session, clickedResult.id);
          }
        } else if (session.metadataSources.length > 0) {
          const prefProvider = ctx.preferences().metadataViewProvider;
          const chosenMeta = session.metadataSources.find(r => r.provider === prefProvider)
            ?? session.metadataSources.find(r => r.provider === 'tmdb')
            ?? session.metadataSources[0];
          await ctx.browsing.selectSource(session, chosenMeta.id, { season: session.browsing.season ?? undefined });
        } else if (ctx.browsing.hasSmartList(session)) {
          await ctx.browsing.selectSource(session, 'smart', { season: session.browsing.season ?? undefined });
          const { seasonEpisodes } = ctx.browsing.smartListSeasons(session);
          const activeSeason = session.browsing.season ?? 1;
          const eps = [...(seasonEpisodes.get(activeSeason)?.keys() ?? [])].sort((a, b) => a - b);
          if (session.playIntent.season !== activeSeason || (eps.length && !eps.includes(session.playIntent.episode))) {
            session.playIntent = {
              ...session.playIntent,
              season: activeSeason,
              episode: eps.length && !eps.includes(session.playIntent.episode) ? eps[0] : session.playIntent.episode,
            };
          }
        } else if (session.results.length > 0) {
          await ctx.browsing.selectSource(session, session.results[0].id);
        } else {
          session.browsing = { ...session.browsing, loading: false, refreshing: false, error: null };
        }
      }

      if (!admitted()) return;

      // Kick off background association discovery (only runs if Cookie Douban / configured TMDB applies)
      if (session.metadataSources.length > 0) {
        ctx.detached(session, ctx.metadata.discoverAssociations(session));
      }

      // Step 4: Handle terminal No-Video-Sources or Auto-Start / iOS Auto-Prepare (Decision 5)!
      if (session.noSources) {
        session.stage = 'unavailable';
        session.error = 'NO_VIDEO_SOURCES';
        ctx.publish(session);
        return;
      }

      session.stage = 'idle';
      ctx.publish(session);

      if (session.restoreOnly) return;

      const autoPrepare = session.platform === 'ios' && ctx.preferences().iosAutoPrepare;
      if (session.platform === 'windows' || autoPrepare) {
        void ctx.selection.start(session, { prepareOnly: autoPrepare });
      }
    } catch (error) {
      if (admitted()) {
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: codeOf(error) };
        session.stage = 'error';
        session.error = codeOf(error);
        ctx.publish(session);
      }
    }
  }

  async function open(input, { searchEntry } = {}) {
    requireValue(!ctx.isClosed(), 'CLOSED');
    requireValue(object(input) && ['ios', 'windows'].includes(input.platform));

    const rawEntry = input.entry;
    requireValue(object(rawEntry) && text(rawEntry.title));

    const capturedRules = searchEntry?.ruleSnapshot ?? await rules.snapshot();
    const liveSources = await sourceRegistry.liveSources({ explicitSources: input.sources });
    requireValue(!ctx.isClosed(), 'CLOSED');

    const recognition = createRecognitionRules(capturedRules);
    const entryKindHint = rawEntry.kind === 'movie' ? 'movie' : undefined;
    const canonical = await recognition.canonical(
      text(searchEntry?.keyword) || text(rawEntry.keyword) || text(rawEntry.title),
      entryKindHint,
      undefined,
    );
    const titleSeason = rawEntry.kind === 'movie' ? null
      : (await recognition.canonical(text(rawEntry.title), entryKindHint, undefined)).season;

    const keyword = text(canonical.keyword) || text(rawEntry.title);
    const sessionKey = canonical.sessionKey || normalizeWorkKey(keyword);
    requireValue(sessionKey.length > 0);
    const targetId = workTargetId(keyword);
    const registryKey = `${input.platform}:${sessionKey}`;

    const requestedSeason = Number.isSafeInteger(rawEntry.season) && rawEntry.season > 0 ? rawEntry.season
      : (Number.isSafeInteger(searchEntry?.seasonHint) && searchEntry.seasonHint > 0 ? searchEntry.seasonHint
        : (Number.isSafeInteger(titleSeason) && titleSeason > 0 ? titleSeason : undefined));
    const requestedEpisode = Number.isSafeInteger(rawEntry.episode) && rawEntry.episode > 0 ? rawEntry.episode : undefined;

    // Reuse existing live keyword session if present!
    const existing = sessionsByKey.get(registryKey);
    if (existing && !existing.released) {
      clearIdleTimer(existing);
      existing.attached = true;
      existing.stopped = false;
      existing.restoreOnly = input.restoreOnly === true;
      existing.rules = capturedRules;
      existing.sources = liveSources;
      for (const src of liveSources) {
        if (!existing.search.has(src.key)) {
          existing.search.set(src.key, { sourceKey: src.key, page: 0, completed: false, loading: false, error: null, seen: new Set() });
        }
      }
      existing.requestedSeason = requestedSeason;
      existing.requestedEpisode = requestedEpisode;
      existing.userBrowsedSource = false;
      existing.metadataEpoch++;
      existing.generation++;
      existing.runKey = null;

      const entryIntent = await ingestEntryContext(existing, rawEntry, searchEntry, { isReentry: true });
      existing.noSources = computeNoSources(existing);
      existing.stage = existing.noSources ? 'unavailable' : 'loading';
      existing.error = existing.noSources ? 'NO_VIDEO_SOURCES' : null;
      ctx.publish(existing);
      existing.initialization = ctx.tracked(initialize(existing, entryIntent));
      return ctx.snapshot.build(existing);
    }

    // Build a brand-new keyword-anchored session
    const session = {
      id: randomUUID(),
      registryKey,
      platform: input.platform,
      sessionKey,
      targetId,
      keyword,
      fallbackTitle: text(rawEntry.title),
      fallbackPoster: text(rawEntry.poster),
      kind: rawEntry.kind === 'movie' ? 'movie' : 'tv',
      year: Number.isSafeInteger(rawEntry.year) && rawEntry.year > 0 ? rawEntry.year : undefined,
      requestedSeason,
      requestedEpisode,
      restoreOnly: input.restoreOnly === true,
      attached: true,
      idleTimer: null,
      detachedAt: 0,
      rules: capturedRules,
      sources: liveSources,
      noSources: false,
      tmdbSlot: null,
      doubanSlot: null,
      metadataSources: [],
      metadataViews: new Map(),
      playbackMetadataSourceId: null,
      userBrowsedSource: false,
      userSelected: false,
      calculations: new Map(),
      results: [],
      resultKeys: new Set(),
      candidates: new Map(),
      excluded: new Set(),
      listeners: new Set(),
      search: new Map(liveSources.map(source => [source.key, {
        sourceKey: source.key, page: 0, completed: false, loading: false, error: null, seen: new Set(),
      }])),
      sequence: 0,
      generation: 0,
      metadataEpoch: 0,
      browseGeneration: 0,
      searchGeneration: 0,
      stage: 'loading',
      error: null,
      reportError: null,
      feedbackFailure: null,
      selectionError: null,
      browsing: {
        sourceId: null,
        lineId: null,
        season: requestedSeason ?? null,
        loading: true,
        refreshing: false,
        loadingMoreEpisodes: false,
        error: null,
      },
      playIntent: {
        mode: 'smart',
        kind: rawEntry.kind === 'movie' ? 'movie' : 'tv',
        ...(requestedSeason ? { season: requestedSeason } : {}),
        ...(requestedEpisode ? { episode: requestedEpisode } : {}),
      },
      activePlayIntent: null,
      affinity: null,
      stopped: false,
      released: false,
      pending: null,
      current: null,
      lastFailed: null,
      constraint: { action: 'default' },
      prepareOnly: false,
      resumePosition: 0,
      resumeCandidateId: null,
      position: 0,
      duration: 0,
      paused: false,
      shortVideo: false,
      previewEpisodes: (library.read().previewTargets ?? []).includes(targetId),
      lastHistoryWrite: 0,
      discoveryComplete: false,
      prefetch: new Map(),
      leases: new Map(),
      prefetchGeneration: 0,
      reportQueue: Promise.resolve(),
    };

    const entryIntent = await ingestEntryContext(session, rawEntry, searchEntry, { isReentry: false });
    session.noSources = computeNoSources(session);
    sessions.set(session.id, session);
    sessionsByKey.set(registryKey, session);

    debug?.emit({
      level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.session.open',
      data: {
        sessionId: session.id, platform: session.platform, targetId: session.targetId,
        keyword: session.keyword, sourceCount: liveSources.length, noSources: session.noSources,
      },
    });

    ctx.publish(session);
    session.initialization = ctx.tracked(initialize(session, entryIntent));
    return ctx.snapshot.build(session);
  }

  async function saveHistory(session, selected = session.current, ended = false) {
    if (!selected) return;
    const candidate = selected.candidate;
    const work = ctx.metadata.buildWorkInfo(session);
    const share = candidate.shareOrigin
      ? {
        provider: candidate.shareOrigin.provider,
        url: candidate.shareOrigin.url,
        ...(candidate.shareOrigin.password ? { password: candidate.shareOrigin.password } : {}),
      }
      : undefined;
    await library.saveHistory({
      targetId: session.targetId,
      keyword: session.keyword,
      title: work.title || session.fallbackTitle,
      poster: work.poster || session.fallbackPoster || '',
      kind: session.kind,
      ...(session.year ? { year: session.year } : {}),
      ...(work.tmdb ? { tmdb: work.tmdb } : {}),
      ...(work.douban ? { douban: work.douban } : {}),
      sourceKey: candidate.source.key,
      detailId: candidate.detailId ?? candidate.source.detailId,
      lineId: candidate.lineId,
      lineLabel: text(candidate.lineLabel) || text(candidate.source.name),
      ...(share ? { share } : {}),
      candidateId: candidate.id,
      fileName: text(candidate.fileName ?? candidate.name),
      episode: session.kind === 'tv' ? fields(selected.intent ?? session.playIntent, ['season', 'episode', 'globalEpisode']) : undefined,
      position: session.position,
      duration: session.duration,
      paused: session.paused,
      ended,
      updatedAt: Date.now(),
    });
    session.lastHistoryWrite = Date.now();
  }

  function report(session, event, selected = session.current, force = false) {
    if (!selected || selected.descriptor?.watchReport !== true ||
        !force && Date.now() - (selected.lastReport ?? 0) < 12_000) return;
    selected.lastReport = Date.now();
    const payload = {
      sessionId: selected.id,
      positionSeconds: session.position,
      durationSeconds: session.duration,
      event,
    };
    session.reportQueue = session.reportQueue.then(() => data.report(selected.candidate, payload)).then(() => {
      if (!session.released && session.current?.id === selected.id) { session.reportError = null; ctx.publish(session); }
    }, error => {
      if (!session.released && session.current?.id === selected.id) { session.reportError = codeOf(error); ctx.publish(session); }
    });
    ctx.tracked(session.reportQueue);
  }

  async function stop(session) {
    session.generation++;
    session.browseGeneration++;
    session.searchGeneration++;
    session.metadataEpoch++;
    session.stopped = true;
    session.stage = 'stopped';
    const current = session.current;
    let error;
    try { await saveHistory(session); } catch (caught) { error = caught; }
    report(session, 'stopped', current, true);
    session.current = null;
    session.pending = null;
    const results = await Promise.allSettled([
      ctx.cancelCalculations(session),
      ctx.prefetch.clearPrefetch(session),
      ...[...session.leases].map(async ([attemptId, resourceId]) => {
        await media.release(resourceId);
        session.leases.delete(attemptId);
      }),
    ]);
    error ??= results.find(value => value.status === 'rejected')?.reason;
    if (error) session.error = codeOf(error);
    ctx.publish(session);
    if (error) throw error;
  }

  async function destroySession(session) {
    if (session.releaseWork) return session.releaseWork;
    clearIdleTimer(session);
    session.released = true;
    if (sessionsByKey.get(session.registryKey) === session) {
      sessionsByKey.delete(session.registryKey);
    }
    for (const listener of [...session.listeners]) listener();
    return session.releaseWork = (async () => {
      let error;
      try { await stop(session); } catch (caught) { error = caught; }
      sessions.delete(session.id);
      if (error) throw error;
      return { released: true };
    })();
  }

  async function releaseSession(session, input = {}) {
    if (session.released) return { released: true };
    if (input.forceDestroy || ctx.isClosed()) {
      return destroySession(session);
    }
    if (input.keepMedia && session.current && !session.current.resourceReleased) {
      session.generation++;
      session.browseGeneration++;
      session.stopped = true;
      const calcWork = ctx.cancelCalculations(session, 'playback');
      const pending = session.pending;
      session.pending = null;
      await Promise.all([calcWork, pending ? ctx.retire(session, pending) : null, ctx.prefetch.clearPrefetch(session)]);
      return { retained: true };
    }
    // Detach media & active playback work, keeping the keyword session warm in memory until idle eviction!
    session.attached = false;
    await stop(session);
    scheduleIdleEviction(session);
    return { released: true, cachedInMemory: true };
  }

  async function refreshSources(explicitSources) {
    const freshSources = await sourceRegistry.liveSources({ explicitSources });
    for (const session of sessions.values()) {
      if (session.released) continue;
      const wasNoSources = session.noSources;
      session.sources = freshSources;
      for (const src of freshSources) {
        if (!session.search.has(src.key)) {
          session.search.set(src.key, { sourceKey: src.key, page: 0, completed: false, loading: false, error: null, seen: new Set() });
        }
      }
      session.noSources = computeNoSources(session);
      if (session.noSources && session.stage !== 'playing') {
        session.stage = 'unavailable';
        session.error = 'NO_VIDEO_SOURCES';
        ctx.publish(session);
      } else if (!session.noSources && (wasNoSources || session.error === 'NO_VIDEO_SOURCES')) {
        session.stage = 'idle';
        session.error = null;
        ctx.publish(session);
        if (session.attached && !session.restoreOnly && !session.stopped && !session.current && !session.pending) {
          const autoPrepare = session.platform === 'ios' && ctx.preferences().iosAutoPrepare;
          if (session.platform === 'windows' || autoPrepare) {
            void ctx.selection.start(session, { prepareOnly: autoPrepare });
          }
        }
      } else {
        ctx.publish(session);
      }
    }
  }

  async function invalidate(domain) {
    if (domain === 'film.video-sources') {
      await refreshSources();
      return;
    }
    const drains = [];
    const sourceScope = object(domain) && domain.kind === 'source' ? domain : null;
    const matchesSource = source => sourceScope
      ? source.engine === sourceScope.engine && (sourceScope.installationId === undefined ||
        source.installationId === sourceScope.installationId)
      : domain === 'source' ? source.engine !== 'pan' : source.key === domain;
    const matchesAccount = provider => Boolean(provider) && (domain === 'pan' || providerFamily(provider) === providerFamily(domain));

    for (const session of sessions.values()) {
      if (session.released) continue;
      if (['tmdb', 'douban'].includes(domain)) {
        session.metadataViews.clear();
        session.metadataSources = session.metadataSources.filter(r => r.provider !== domain);
      }
      const affectedResults = session.results.filter(r => matchesSource(r.source));
      for (const res of affectedResults) {
        for (const [id, cand] of session.candidates) {
          if (cand.source.key === res.source.key && cand.detailId === res.source.detailId) session.candidates.delete(id);
        }
      }
      if (affectedResults.length) {
        session.results = session.results.filter(r => !matchesSource(r.source));
        session.resultKeys = new Set(session.results.map(r => r.id));
      }
      for (const [id, cand] of session.candidates) {
        if (matchesAccount(cand.provider) || matchesAccount(cand.file?.provider)) session.candidates.delete(id);
      }
      for (const Attempt of [session.current, session.pending].filter(Boolean)) {
        if (matchesSource(Attempt.candidate.source) || matchesAccount(Attempt.candidate.provider)) {
          if (Attempt === session.current) session.current = null;
          if (Attempt === session.pending) session.pending = null;
          drains.push(ctx.retire(session, Attempt));
        }
      }
      ctx.publish(session);
    }
    await refreshSources().catch(() => {});
    await Promise.allSettled(drains);
  }

  async function updateSourceDisplay(_engine, _instances) {
    await refreshSources();
  }

  return {
    open,
    initialize,
    saveHistory,
    report,
    stop,
    releaseSession,
    destroySession,
    invalidate,
    updateSourceDisplay,
    refreshSources,
  };
}
