import { randomUUID } from 'node:crypto';
import { isSmartSiteAllowed } from './policy.js';
import { resolveEpisode } from './episodes.js';
import { providerFamily } from './rules.js';
import { checkSource, checkTarget, clone, codeOf, fields, identity, isIdentifier, object,
  requireValue, sourceRevision, text } from './identity.js';
import { episodeTarget, historyPosition, manualIdentity, mergeSeason,
  metadataIdentity, sameEpisode } from './session-helpers.js';

export function createSessionLifecycle(ctx) {
  const { data, library, media, rules, catalog, sessions, debug } = ctx;

  async function initialize(session, cacheEntries, { refresh = false } = {}) {
    const epoch = session.metadataEpoch, browseGeneration = session.browseGeneration;
    const admitted = () => !session.released && !session.stopped && !ctx.isClosed() && session.metadataEpoch === epoch &&
      ctx.groupScopeAvailable(session);
    try {
      if (cacheEntries.length) await data.handoff(cacheEntries, { canSchedule: admitted });
      if (!admitted()) return;
      // Existing metadata may render immediately, but only the data owner can
      // decide that a complete, correctly scoped and still-fresh entry exists.
      let entry, displayTarget = session.target, displayError;
      if (!displayTarget.metadataProvider) displayTarget = catalog.content(displayTarget) ?? displayTarget;
      if (session.entryTarget.metadataProvider === 'douban' && session.entryResolution === 'loading') {
        const resolved = await data.tmdbTargetEntry(session.entryTarget,
          { metadata: session.entryMetadata, rules: session.rules, canSchedule: admitted, refresh });
        if (!admitted()) return;
        if (!resolved) {
          session.entryResolution = 'unmatched';
          session.metadataSources = []; session.metadataSourceId = null;
          displayTarget = session.entryTarget;
          try {
            entry = await data.targetMetadataEntry(displayTarget, { rules: session.rules, canSchedule: admitted, refresh });
          } catch (error) { displayError = codeOf(error); }
        } else {
          session.entryResolution = 'matched';
          session.seedWaitGroup = false;
          session.target = checkTarget(resolved.target);
          displayTarget = session.target;
          session.query = session.target.title;
          entry = resolved.entry;
        }
      } else {
        if (!session.target.metadataProvider && session.entryTarget.metadataProvider === 'tmdb')
          displayTarget = session.entryTarget;
        try { entry = await data.targetMetadataEntry(displayTarget, { rules: session.rules, canSchedule: admitted, refresh }); }
        catch (error) {
          if (displayTarget === session.target) throw error;
          displayError = codeOf(error);
        }
      }
      if (!admitted()) return;
      const metadata = entry?.value ?? (session.entryResolution === 'unmatched' ? session.entryMetadata
        : displayTarget !== session.target ? session.metadata : null);
      session.metadata = metadata;
      if (displayError) session.metadataError = displayError;
      if (displayTarget.metadataProvider) {
        const row = {
          provider: displayTarget.metadataProvider, metadataId: displayTarget.metadataId,
          kind: displayTarget.kind, title: displayTarget.title, metadata,
        };
        session.metadataSourceId = metadataIdentity(row);
        session.metadataSources = [{ ...row, id: session.metadataSourceId }];
        session.playbackMetadataSourceId = session.metadataSourceId;
        session.metadata = ctx.metadata.metadataDisplay(session, row);
        ctx.metadata.acceptMetadataCache(session, entry, undefined, displayError);
      }
      const previous = !session.restoreOnly && session.platform === 'windows'
        ? await library.history(session.target.id) : null;
      if (!admitted()) return;
      if (session.target.kind === 'tv') {
        ctx.metadata.initializeEpisode(session, session.generation === 0 ? previous : null);
        if (session.target.metadataProvider === 'tmdb' && session.target.season !== undefined) {
          const target = session.target;
          const season = await data.seasonMetadataEntry(target, target.season,
            { rules: session.rules, canSchedule: admitted, refresh });
          if (!admitted()) return;
          const id = session.playbackMetadataSourceId;
          const display = mergeSeason(target, ctx.metadata.playbackMetadata(session), target.season, season.value);
          ctx.metadata.acceptMetadataCache(session, season, target.season, undefined, id, display);
          if (id === session.metadataSourceId) session.metadata = display;
        }
      } else if (previous && session.generation === 0) {
        if (!['source', 'group'].includes(session.target.kind) ||
            (session.target.source?.candidateId === undefined || session.target.source.candidateId === previous.candidateId) &&
            (session.target.source?.lineId === undefined || session.target.source.lineId === previous.lineId)) {
          if (['source', 'group'].includes(session.target.kind)) {
            const result = session.results.find(row => row.source.key === previous.sourceKey &&
              row.source.detailId === previous.detailId &&
              (!session.target.source || row.source.key === session.target.source.key &&
                row.source.detailId === session.target.source.detailId));
            if (result) {
              session.resumePosition = historyPosition(previous);
              session.resumeCandidateId = previous.candidateId;
              if (session.browseGeneration === browseGeneration)
                session.browsing = { ...session.browsing, sourceId: result.id, lineId: previous.lineId };
            }
          } else session.resumePosition = historyPosition(previous);
        }
      }
      if (!admitted()) return;
      if (session.browseGeneration === browseGeneration) {
        if (session.browsing.sourceId !== null)
          await ctx.browsing.browse(session, session.browsing.sourceId, session.browsing.lineId ?? undefined);
        else session.browsing = {
          ...session.browsing, loading: false,
          ...(session.target.season === undefined ? {} : { season: session.target.season }),
        };
      }
      if (!admitted()) return;
      if (session.generation === 0 || session.stage === 'loading') {
        session.stage = 'idle'; ctx.publish(session);
      }
      // Complete the initial directory before selecting its independent view.
      // Association discovery still does not delay automatic preparation.
      if (displayTarget.metadataProvider) {
        session.metadataDiscovery = ctx.metadata.discoverMetadata(session, displayTarget);
        ctx.detached(session, session.metadataDiscovery);
      }
      if (session.generation !== 0 || session.selectionEpoch !== 0) return;
      if (session.restoreOnly) {
        await ctx.browsing.restoreReads(session);
        return;
      }
      if (session.target.kind === 'group' && !session.target.members.length) {
        session.stage = 'unavailable'; session.error = session.seedWaitGroup ? null : 'SOURCE_NOT_FOUND';
        ctx.publish(session); return;
      }
      if (session.target.kind === 'source' && !session.browsing.loading) {
        if (session.browsing.error) {
          session.stage = 'error'; session.error = session.browsing.error; ctx.publish(session); return;
        }
        if (!session.browsing.episodes.length) {
          session.stage = 'unavailable'; session.error = 'MEDIA_UNAVAILABLE'; ctx.publish(session); return;
        }
      }
      if (session.target.metadataProvider && ctx.metadata.metadataCacheState(session)?.status === 'stale') return;
      if (session.target.metadataProvider && !session.sources.some(source => source.enabled !== false) &&
          !library.read().manual.some(row => row.targetId === session.target.id && row.enabled !== false && row.share)) {
        session.stage = 'unavailable'; session.error = 'MEDIA_UNAVAILABLE'; ctx.publish(session); return;
      }
      const autoPrepare = session.platform === 'ios' && !!session.target.metadataProvider && ctx.preferences().iosAutoPrepare;
      // An explicit global coordinate is not an instruction to fall back to
      // the first episode or another episode's historical position.
      if (session.target.kind === 'tv' && session.target.episode === undefined &&
          (session.target.globalEpisode !== undefined || session.platform === 'windows' || autoPrepare)) {
        session.stage = 'unavailable'; session.error = 'NO_EPISODE_MAPPING'; ctx.publish(session); return;
      }
      if (session.platform === 'windows' || autoPrepare) {
        ctx.detached(session, ctx.selection.playSelection(session, autoPrepare));
      }
    } catch (error) {
      if (admitted()) {
        session.restoring = false;
        session.initializationError = codeOf(error);
        if (session.browseGeneration === browseGeneration)
          session.browsing = { ...session.browsing, loading: false, error: codeOf(error) };
        if (session.entryResolution === 'loading') session.entryResolution = 'error';
        if (session.generation === 0 || session.stage === 'loading') {
          session.stage = 'error'; session.error = codeOf(error); ctx.publish(session);
        }
      }
    } finally {
      if (session.metadataEpoch === epoch && session.restoring) {
        session.restoring = false;
        ctx.publish(session);
      }
    }
  }

  function checkSearchHandoff(input, searchHandoff, selectionId, sources) {
    if (searchHandoff === undefined) return;
    requireValue(object(searchHandoff) && searchHandoff.selectionId === selectionId &&
      searchHandoff.revision === input.revision);
    const target = checkTarget(searchHandoff.target);
    requireValue(target.id === selectionId);
    requireValue(Array.isArray(searchHandoff.selectionMembers));
    for (const member of searchHandoff.selectionMembers) {
      requireValue(object(member) && text(member.sourceKey) && isIdentifier(member.detailId));
      requireValue(sources.some(source => source.key === member.sourceKey), 'STALE_SOURCE');
    }
    return searchHandoff.selectionMembers;
  }

  async function open(input, { ruleSnapshot, searchHandoff } = {}) {
    requireValue(!ctx.isClosed(), 'CLOSED');
    requireValue(object(input) && ['ios', 'windows'].includes(input.platform));
    requireValue(input.restoreOnly === undefined || typeof input.restoreOnly === 'boolean');
    if (input.browsing != null) {
      requireValue(input.restoreOnly === true && object(input.browsing));
      for (const name of ['sourceId', 'metadataSourceId'])
        requireValue(input.browsing[name] == null || text(input.browsing[name]));
      requireValue(input.browsing.lineId == null || isIdentifier(input.browsing.lineId));
      requireValue(input.browsing.source === undefined || object(input.browsing.source) &&
        text(input.browsing.source.key) && isIdentifier(input.browsing.source.detailId));
      requireValue(input.browsing.season === undefined ||
        Number.isSafeInteger(input.browsing.season) && input.browsing.season >= 0);
    }
    const entryTarget = checkTarget(input.target);
    if (entryTarget.kind === 'group') requireValue(entryTarget.members.length > 0, 'SOURCE_NOT_FOUND');
    const providedResumeTarget = input.resumeTarget === undefined ? null : checkTarget(input.resumeTarget);
    const doubanSeed = entryTarget.metadataProvider === 'douban';
    if (providedResumeTarget?.kind === 'group')
      requireValue(providedResumeTarget.members.length > 0, 'SOURCE_NOT_FOUND');
    const resumesCurrentTarget = ['source', 'group'].includes(providedResumeTarget?.kind) ||
      providedResumeTarget?.metadataProvider === 'tmdb';
    const resumeTarget = doubanSeed && !resumesCurrentTarget ? null : providedResumeTarget;
    const keepSeedDisplayMetadata = doubanSeed && (!resumeTarget || resumeTarget.metadataProvider !== 'tmdb');
    const createsSeedWaitGroup = doubanSeed && !resumeTarget;
    const target = createsSeedWaitGroup
      ? checkTarget({
        id: `group:${entryTarget.id}`, kind: 'group',
        title: entryTarget.title, ...fields(entryTarget, ['year']), members: [],
      }) : resumeTarget ?? entryTarget;
    const sources = (input.sources ?? []).map(checkSource);
    requireValue(new Set(sources.map(source => source.key)).size === sources.length);
    if (target.kind === 'group') requireValue((createsSeedWaitGroup || target.members.length > 0) && target.members.every(member =>
      sources.some(source => source.key === member.sourceKey && source.enabled !== false)), 'SOURCE_NOT_FOUND');
    const selectionMembers = doubanSeed ? undefined : checkSearchHandoff(input, searchHandoff, entryTarget.id, sources);
    // The entry content and exact raw scope outlive changes of playback target.
    const capturedRules = ruleSnapshot ?? await rules.snapshot();
    requireValue(!ctx.isClosed(), 'CLOSED');
    const session = {
      id: randomUUID(), target, seedWaitGroup: createsSeedWaitGroup,
      entryTarget: clone(entryTarget), entrySurface: input.entrySurface,
      entryResolution: doubanSeed ? (resumeTarget ? (resumeTarget.metadataProvider === 'tmdb' ? 'matched' : 'unmatched')
        : 'loading') : null,
      entryMetadata: clone(input.metadata ?? null),
      platform: input.platform, restoreOnly: input.restoreOnly === true,
      restoring: input.restoreOnly === true, restoreBrowsing: clone(input.browsing),
      query: doubanSeed ? target.title : text(input.query) || target.title,
      selectionId: entryTarget.id, selectionKind: doubanSeed ? resumeTarget?.kind ?? entryTarget.kind : entryTarget.kind,
      selectionSource: entryTarget.kind === 'source' ? clone(entryTarget.source)
        : target.kind === 'source' ? clone(target.source) : null,
      searchRevision: -1, searchUpdateEpoch: 0,
      sourceScope: sources.map(source => clone(source)),
      userSelected: target.kind === 'group' && target.source?.candidateId !== undefined,
      sources,
      metadata: keepSeedDisplayMetadata || !resumeTarget ||
        !target.metadataProvider && entryTarget.metadataProvider === 'tmdb' ? clone(input.metadata ?? null) : null,
      rules: capturedRules,
      calculations: new Map(), metadataViews: new Map(), candidateEpoch: 0,
      metadataSources: [], metadataSourceId: null, playbackMetadataSourceId: null, metadataError: null,
      metadataViewInitialized: !!input.browsing?.metadataSourceId,
      lastFailed: null,
      initializationError: null,
      results: [], resultKeys: new Set(), candidates: new Map(), excluded: new Set(), listeners: new Set(),
      search: new Map(sources.map(source => [source.key, {
        sourceKey: source.key, page: 0, completed: false, loading: false, error: null, seen: new Set(),
      }])),
      sequence: 0, generation: 0, selectionEpoch: 0, metadataEpoch: 0, browseGeneration: 0, searchGeneration: 0,
      stage: 'loading', error: null, reportError: null,
      browsing: { sourceId: null, lineId: null, loading: true, error: null, lines: [], episodes: [] },
      stopped: false, released: false, pending: null, current: null, constraint: { action: 'default' },
      prepareOnly: false, resumePosition: 0, position: 0, duration: 0, paused: false,
      shortVideo: false, previewEpisodes: (library.read().previewTargets ?? []).includes(target.id),
      lastHistoryWrite: 0, discoveryComplete: false,
      prefetch: new Map(), leases: new Map(), prefetchGeneration: 0, reportQueue: Promise.resolve(),
    };
    for (const result of doubanSeed && !resumeTarget ? [] : input.results ?? []) {
      const source = sources.find(value => value.key === result.sourceKey);
      if (source) await ctx.candidates.addResults(session, source, result.items ?? [], 'result', undefined, selectionMembers);
    }
    for (const result of doubanSeed && !resumeTarget ? [] : input.resultHints ?? []) {
      const source = sources.find(value => value.key === result.sourceKey);
      if (source) await ctx.candidates.addResults(session, source, result.items ?? [], 'hint', undefined, selectionMembers);
    }
    for (const value of doubanSeed && !resumeTarget ? [] : input.search ?? []) {
      const state = session.search.get(value.sourceKey);
      if (state) {
        requireValue(Number.isSafeInteger(value.page) && value.page >= 0 && typeof value.completed === 'boolean');
        state.page = value.page; state.completed = value.completed;
      }
    }
    if (target.kind === 'source' && target.source.engine === 'pan') {
      const row = library.read().manual.find(value => value?.id === target.source.detailId &&
        value.targetId === target.id && value.share && !value.errorCode && value.enabled !== false);
      requireValue(row && target.source.key === `manual:${row.id}`, 'SOURCE_NOT_FOUND');
      const source = { key: target.source.key, engine: 'pan', name: row.name || '人工匹配', detailId: row.id };
      const added = await ctx.candidates.addResults(session, source, [{ id: row.id, title: row.title || target.title }], 'linked');
      requireValue(!ctx.isClosed(), 'CLOSED');
      const panResult = added ? session.results.find(value => value.source.key === source.key) : null;
      requireValue(panResult, 'SOURCE_NOT_FOUND');
      Object.assign(panResult, { manualId: row.id, manualIdentity: manualIdentity(row), linkedTargetId: target.id });
    } else if (target.kind === 'source') {
      const source = sources.find(value => value.key === target.source.key);
      requireValue(source, 'SOURCE_NOT_FOUND');
      await ctx.candidates.addResults(session, source, [{ id: target.source.detailId, title: target.title }], 'linked');
    } else if (target.members?.length) {
      for (const member of target.members) {
        const source = sources.find(value => value.key === member.sourceKey);
        requireValue(source, 'SOURCE_NOT_FOUND');
        await ctx.candidates.addResults(session, source, [{ detailId: member.detailId }], 'linked');
      }
    }
    if (!target.metadataProvider) {
      const selected = target.source ? session.results.find(row => row.source.key === target.source.key &&
        row.source.detailId === target.source.detailId) : session.results[0];
      if (selected) session.browsing = { ...session.browsing, sourceId: selected.id, lineId: target.source?.lineId ?? null };
    }
    if (input.browsing?.source) {
      const pointer = input.browsing.source;
      const source = pointer.engine === 'pan' ? session.results.find(row =>
        row.source.engine === 'pan' && row.source.key === pointer.key && row.source.detailId === pointer.detailId)?.source
        : sources.find(value => value.key === pointer.key && (pointer.installationId === undefined ||
          value.engine === pointer.engine && value.installationId === pointer.installationId && value.siteId === pointer.siteId));
      requireValue(source, 'SOURCE_NOT_FOUND');
      await ctx.candidates.addResults(session, source, [{ id: pointer.detailId, title: target.title }], 'linked');
      session.restoreBrowsing.sourceId = identity(source.key, pointer.detailId);
    }
    requireValue(input.cacheEntries === undefined || Array.isArray(input.cacheEntries));
    requireValue(!ctx.isClosed(), 'CLOSED');
    sessions.set(session.id, session);
    debug?.emit({
      level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.session.open',
      data: {
        sessionId: session.id, platform: session.platform, targetId: session.target.id,
        targetKind: session.target.kind, restoreOnly: session.restoreOnly, sourceCount: sources.length,
      },
    });
    ctx.publish(session);
    session.initialization = ctx.tracked(initialize(session, clone(input.cacheEntries ?? [])));
    return ctx.snapshot.build(session);
  }

  async function updateSearch(session, input, searchHandoff) {
    requireValue(input.selectionId === session.selectionId && Number.isSafeInteger(input.revision) &&
      input.revision >= 0);
    if (session.stopped || input.revision <= session.searchRevision) return;
    requireValue(Array.isArray(input.sources) && Array.isArray(input.results) && Array.isArray(input.search) &&
      (input.resultHints === undefined || Array.isArray(input.resultHints)) &&
      (input.cacheEntries === undefined || Array.isArray(input.cacheEntries)));
    const sources = input.sources.map(checkSource);
    requireValue(new Set(sources.map(source => source.key)).size === sources.length);
    for (const source of sources) {
      requireValue(session.sourceScope.some(original => original.key === source.key &&
        sourceRevision(original) === sourceRevision(source)) &&
        session.sources.some(current => current.key === source.key &&
          sourceRevision(current) === sourceRevision(source)), 'STALE_SOURCE');
    }
    requireValue(ctx.groupScopeAvailable(session), 'SOURCE_NOT_FOUND');
    const inScope = key => sources.find(source => source.key === key);
    for (const result of [...input.results, ...(input.resultHints ?? [])])
      requireValue(object(result) && inScope(result.sourceKey) && Array.isArray(result.items) && result.items.every(object));
    for (const state of input.search)
      requireValue(object(state) && inScope(state.sourceKey) && Number.isSafeInteger(state.page) &&
        state.page >= 0 && typeof state.completed === 'boolean');
    const entries = input.cacheEntries ?? [];
    for (const entry of entries) {
      requireValue(object(entry));
      if (entry.kind === 'metadata') requireValue(session.selectionKind !== 'source');
      else {
        const source = checkSource(entry.source), allowedSource = inScope(source.key);
        requireValue(allowedSource && sourceRevision(allowedSource) === sourceRevision(source), 'STALE_SOURCE');
      }
    }
    const selectionMembers = checkSearchHandoff(input, searchHandoff, session.selectionId, sources);
    let carrier = session.searchMetadataTarget;
    if (input.metadataTarget !== undefined) {
      requireValue(session.selectionKind !== 'source' && object(input.metadataTarget) &&
        ['movie', 'tv'].includes(input.metadataTarget.kind) && ['tmdb', 'douban'].includes(input.metadataTarget.metadataProvider));
      carrier = checkTarget({
        ...fields(input.metadataTarget, ['kind', 'title', 'year', 'metadataProvider', 'metadataId']),
        id: session.selectionId, title: text(input.metadataTarget.title) || session.target.title,
      });
    }
    if (session.selectionKind !== 'group' && carrier) {
      const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
      const target = selected ? { kind: selected.kind, metadataProvider: selected.provider, metadataId: selected.metadataId }
        : session.entryTarget;
      if (carrier.kind !== target.kind || carrier.metadataProvider !== target.metadataProvider ||
          String(carrier.metadataId) !== String(target.metadataId)) carrier = undefined;
    }
    session.searchRevision = input.revision;
    session.searchMetadataTarget = carrier;
    const epoch = ++session.searchUpdateEpoch;
    let expectedMetadataEpoch = session.metadataEpoch;
    const admitted = () => !ctx.isClosed() && !session.released && !session.stopped && session.searchUpdateEpoch === epoch &&
      session.metadataEpoch === expectedMetadataEpoch;
    const trustedGroup = session.target.kind === 'group' && selectionMembers !== undefined;
    // Only the Node search owner proves these exact members, even when a late
    // metadata carrier replaces the selected card's group-shaped target.
    if (trustedGroup) for (const member of selectionMembers) ctx.addMember(session, member.sourceKey, member.detailId);
    for (const result of input.results) {
      await ctx.candidates.addResults(session, inScope(result.sourceKey), result.items, 'result', admitted, selectionMembers);
      if (!admitted()) return;
    }
    for (const result of input.resultHints ?? []) {
      await ctx.candidates.addResults(session, inScope(result.sourceKey), result.items, 'hint', admitted, selectionMembers);
      if (!admitted()) return;
    }
    for (const value of input.search) {
      if (session.selectionKind === 'source' && value.sourceKey !== session.selectionSource.key) continue;
      const state = session.search.get(value.sourceKey);
      if (value.page >= state.page) {
        state.page = value.page; state.completed ||= value.completed; state.error = null;
      }
    }
    session.discoveryComplete = false;
    ctx.publish(session);
    const seeds = entries.filter(entry => session.selectionKind !== 'source' ||
      entry.kind === 'detail' && entry.source.key === session.selectionSource.key &&
        entry.detailId === session.selectionSource.detailId);
    ctx.detached(session, (async () => {
      try {
        await session.initialization;
        if (!admitted()) return;
        if (seeds.length) await data.handoff(clone(seeds), { canSchedule: admitted });
        if (!admitted() || !carrier) return;
        const entry = await data.targetMetadataEntry(carrier, { rules: session.rules, canSchedule: admitted });
        if (!admitted()) return;
        const value = entry.value;
        const metadataTarget = { ...carrier, title: text(value?.title ?? value?.name) || carrier.title };
        const row = {
          provider: carrier.metadataProvider, metadataId: carrier.metadataId, kind: carrier.kind,
          title: metadataTarget.title, metadata: value,
        };
        row.id = metadataIdentity(row);
        session.metadataSources = [...session.metadataSources.filter(item => item.id !== row.id), row];
        const display = ctx.metadata.metadataDisplay(session, row);
        if (session.restoreOnly || session.target.kind !== 'group' ||
            session.pending || session.current || session.userSelected) {
          // A proven late association supplies display data, not authority to
          // reinterpret the playing raw file as a particular metadata episode.
          if (['source', 'group'].includes(session.target.kind) &&
              (session.metadataSourceId === null || row.id === session.metadataSourceId)) {
            session.metadata = display; session.metadataSourceId = row.id;
            ctx.metadata.acceptMetadataCache(session, entry);
          } else if (session.metadataSourceId === null || row.id === session.metadataSourceId) {
            let updated = display;
            let seasonEntry;
            const season = session.browsing.season ?? session.target.season;
            if (carrier.kind === 'tv' && carrier.metadataProvider === 'tmdb' && season !== undefined) {
              const generation = session.browseGeneration;
              const details = await data.seasonMetadataEntry(carrier, season, {
                rules: session.rules,
                canSchedule: () => admitted() && generation === session.browseGeneration,
              });
              if (!admitted() || generation !== session.browseGeneration) return;
              updated = mergeSeason(carrier, display, season, details.value);
              seasonEntry = details;
            }
            session.metadata = updated; session.metadataSourceId = row.id; session.initializationError = null;
            ctx.metadata.acceptMetadataCache(session, entry);
            if (seasonEntry) ctx.metadata.acceptMetadataCache(session, seasonEntry, season);
          }
          ctx.publish(session); return;
        }
        session.generation++; session.runKey = null;
        expectedMetadataEpoch = ++session.metadataEpoch;
        session.metadata = display; session.metadataSourceId = row.id; session.initializationError = null;
        session.playbackMetadataSourceId = row.id;
        ctx.metadata.acceptMetadataCache(session, entry);
        const first = carrier.kind === 'tv' ? resolveEpisode(ctx.metadata.episodeMap(display), { globalEpisode: 1 }) : null;
        if (!await ctx.selection.selectTarget(session, episodeTarget(metadataTarget, first ?? {}))) return;
        session.browsing = { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
        session.stage = 'idle'; session.error = null; ctx.publish(session);
        ctx.detached(session, ctx.metadata.discoverMetadata(session));
        const autoPrepare = session.platform === 'ios' && ctx.preferences().iosAutoPrepare;
        if (session.platform === 'windows' || autoPrepare) {
          if (carrier.kind === 'tv' && !first) {
            session.stage = 'unavailable'; session.error = 'NO_EPISODE_MAPPING'; ctx.publish(session);
          } else void ctx.selection.start(session, { prepareOnly: autoPrepare });
        }
      } catch (error) {
        if (admitted()) { session.metadataError = codeOf(error); ctx.publish(session); }
      }
    })());
  }

  async function saveHistory(session, selected = session.current, ended = false) {
    if (!selected) return;
    const candidate = selected.candidate;
    await library.saveHistory({
      targetId: selected.target.id, title: selected.target.title,
      playbackTarget: catalog.candidate(selected.target, candidate).playbackTarget,
      sourceKey: candidate.source.key, detailId: candidate.source.detailId, lineId: candidate.lineId, candidateId: candidate.id,
      episode: fields(selected.target, ['season', 'episode', 'globalEpisode']), position: session.position,
      duration: session.duration, paused: session.paused, ended, updatedAt: Date.now(),
    });
    session.lastHistoryWrite = Date.now();
  }

  function report(session, event, selected = session.current, force = false) {
    if (!selected || selected.descriptor.watchReport !== true ||
        !force && Date.now() - (selected.lastReport ?? 0) < 12_000) return;
    selected.lastReport = Date.now();
    const payload = {
      sessionId: selected.id, positionSeconds: session.position,
      durationSeconds: session.duration, event,
    };
    session.reportQueue = session.reportQueue.then(() => data.report(selected.candidate, payload)).then(() => {
      if (!session.released && session.current?.id === selected.id) { session.reportError = null; ctx.publish(session); }
    }, error => {
      if (!session.released && session.current?.id === selected.id) { session.reportError = codeOf(error); ctx.publish(session); }
    });
    ctx.tracked(session.reportQueue);
  }

  async function stop(session) {
    session.generation++; session.browseGeneration++; session.searchGeneration++; session.metadataEpoch++;
    session.searchUpdateEpoch++;
    session.stopped = true; session.stage = 'stopped';
    const current = session.current;
    let error;
    try { await saveHistory(session); } catch (caught) { error = caught; }
    report(session, 'stopped', current, true);
    session.current = null; session.pending = null;
    const results = await Promise.allSettled([
      ctx.cancelCalculations(session), ctx.prefetch.clearPrefetch(session),
      ...[...session.leases].map(async ([attemptId, resourceId]) => {
        await media.release(resourceId); session.leases.delete(attemptId);
      }),
    ]);
    error ??= results.find(value => value.status === 'rejected')?.reason;
    if (error) session.error = codeOf(error);
    ctx.publish(session);
    if (error) throw error;
  }

  async function releaseSession(session, input) {
    if (session.releaseWork) return session.releaseWork;
    if (input.keepMedia && session.current && !session.current.resourceReleased) {
      session.generation++; session.browseGeneration++; session.searchGeneration++; session.metadataEpoch++;
      session.searchUpdateEpoch++;
      session.stopped = true;
      const calcWork = ctx.cancelCalculations(session);
      const pending = session.pending; session.pending = null;
      await Promise.all([calcWork, pending ? ctx.retire(session, pending) : null, ctx.prefetch.clearPrefetch(session)]);
      return { retained: true };
    }
    session.released = true;
    for (const listener of [...session.listeners]) listener();
    return session.releaseWork = (async () => {
      let error;
      try { await stop(session); } catch (caught) { error = caught; }
      sessions.delete(session.id);
      if (error) throw error;
      return { released: true };
    })();
  }

  async function invalidateScriptAccount({ installationId, generation }) {
    const matchesSource = source => source.engine === 'cat' && source.installationId === installationId &&
      source.generation === generation;
    const scriptCandidate = candidate => candidate && matchesSource(candidate.source) && !candidate.file && !candidate.share;
    const retainLine = line => {
      const episodes = line.episodes.filter(item => item.file || item.share);
      return episodes.length === line.episodes.length ? line : episodes.length ? { ...line, episodes } : null;
    };
    const drains = [];
    for (const session of sessions.values()) {
      const results = session.results.filter(result => matchesSource(result.source));
      if (!results.length && !session.sources.some(matchesSource)) continue;
      const activity = session.runActivity;
      const interrupted = activity && matchesSource(activity.source) &&
        !activity.candidate?.file && !activity.candidate?.share && !activity.candidate?.accountProviders;
      if (interrupted) { session.generation++; session.runKey = null; }
      for (const source of session.sources.filter(matchesSource)) session.search.set(source.key,
        { sourceKey: source.key, page: 0, completed: false, loading: false, error: null, seen: new Set() });
      for (const [id, candidate] of session.candidates) if (scriptCandidate(candidate)) session.candidates.delete(id);
      if (scriptCandidate(session.lastFailed?.candidate)) session.lastFailed = null;
      if (scriptCandidate(session.actionCurrent)) session.actionCurrent = null;
      const revoked = [session.current, session.pending].filter(value => scriptCandidate(value?.candidate));
      if (revoked.includes(session.current)) session.current = null;
      if (revoked.includes(session.pending)) session.pending = null;
      for (const value of revoked) drains.push(ctx.retire(session, value));
      for (const [key, value] of session.prefetch) if (!value.candidate || scriptCandidate(value.candidate)) {
        session.prefetch.delete(key);
        if (value.resource) drains.push(media.release(value.resource.id));
      }
      for (const result of results) {
        // The script's raw receipt is stale; accepted pan share/file identities
        // and their owner-produced directories do not consume that credential.
        delete result.detailEntry;
        delete result.recognized;
        result.variants = new Map();
        for (const detail of new Set([result.detail, result.baseDetail])) {
          if (!detail) continue;
          const lines = detail.lines.map(retainLine).filter(Boolean);
          if (lines.length !== detail.lines.length || lines.some((line, index) => line !== detail.lines[index]))
            detail.lines = lines;
        }
        for (const [id, directory] of result.directories ?? []) {
          if (!result.detail?.lines.some(line => line.id === id && line === directory.line)) result.directories.delete(id);
        }
        if (!result.detail?.lines.length) result.state = 'idle';
        if (result.browsing) delete result.browsing;
        if (session.browsing.sourceId !== result.id) continue;
        const lines = result.detail?.lines ?? [];
        if (lines.some(line => line.id === session.browsing.lineId)) {
          session.browsing = { ...session.browsing, lines: lines.map(line => fields(line, ['id', 'label', 'provider'])) };
        } else {
          session.browseGeneration++;
          session.browsing = {
            ...session.browsing, loading: false, error: 'STALE_ACCOUNT',
            lines: lines.map(line => fields(line, ['id', 'label', 'provider'])), episodes: [],
          };
        }
      }
      session.searchUpdateEpoch++;
      session.discoveryComplete = false;
      if (interrupted || revoked.length) {
        session.stage = session.current && !session.current.resourceReleased ? 'playing'
          : session.pending && !session.pending.resourceReleased ? (session.pending.prepareOnly ? 'prepared' : 'pending') : 'idle';
        session.error = 'STALE_ACCOUNT';
      }
      ctx.publish(session);
    }
    const settled = await Promise.allSettled(drains);
    const failure = settled.find(value => value.status === 'rejected');
    if (failure) throw failure.reason;
  }

  async function invalidate(domain, instances) {
    if (object(domain) && domain.kind === 'script-account') return invalidateScriptAccount(domain);
    const drains = [];
    const sourceScope = object(domain) && domain.kind === 'source' ? domain : null;
    const matchesSource = source => sourceScope
      ? source.engine === sourceScope.engine && (sourceScope.installationId === undefined ||
        source.installationId === sourceScope.installationId)
      : domain === 'source' ? source.engine !== 'pan' : source.key === domain;
    const matchesAccount = provider => !!provider && (domain === 'pan' || provider === domain);
    const matchesCandidate = candidate => matchesSource(candidate.source) ||
      matchesAccount(candidate.provider) || matchesAccount(candidate.file?.provider);
    const matchesAttempt = value => matchesCandidate(value.candidate) ||
      matchesAccount(value.descriptor?.scope?.accountProvider);

    // Phase 1: Revoke every affected session synchronously before any async drain.
    for (const session of sessions.values()) {
      if (['tmdb', 'douban'].includes(domain)) session.metadataViews.clear();
      if (domain === session.searchMetadataTarget?.metadataProvider) session.searchUpdateEpoch++;
      if (domain === 'tmdb' && session.entryTarget.metadataProvider === 'douban' &&
          !session.target.metadataProvider && ctx.groupScopeAvailable(session) &&
          (session.entryResolution === 'loading' || session.entryResolution === 'error' ||
            session.target.kind === 'group' && !session.target.members.length)) {
        session.metadataEpoch++;
        if (session.stopped) continue;
        session.initializationError = null;
        session.error = null; session.entryResolution = 'loading';
        session.stage = 'loading';
        session.initialization = ctx.tracked(initialize(session, []));
        ctx.publish(session);
        continue;
      }
      const metadata = domain === session.target.metadataProvider;
      const associatedMetadata = ['tmdb', 'douban'].includes(domain) &&
        session.metadataSources.some(row => row.provider === domain);
      const source = session.sources.some(matchesSource);
      const attempts = [session.current, session.pending].filter(Boolean);
      const activity = session.runActivity;
      const accountActivity = activity?.candidate &&
        [activity.candidate.provider, ...(activity.candidate.accountProviders ?? [])].some(provider =>
          matchesAccount(provider) || providerFamily(domain) && providerFamily(provider) === providerFamily(domain));
      const account = !!accountActivity || attempts.some(value =>
        matchesAccount(value.descriptor.scope?.accountProvider) ||
        matchesAccount(value.candidate.provider) || matchesAccount(value.candidate.file?.provider)) ||
        [...session.candidates.values()].some(value => matchesAccount(value.provider) || matchesAccount(value.file?.provider)) ||
        [...session.prefetch.values()].some(value => value.candidate && matchesAttempt(value));
      if (!metadata && !source && !account && !associatedMetadata) continue;
      drains.push(ctx.cancelCalculations(session));
      if (session.lastFailed && matchesCandidate(session.lastFailed.candidate)) session.lastFailed = null;
      session.searchUpdateEpoch++;
      if (associatedMetadata && !metadata && !source && !account) {
        session.metadataSources = session.metadataSources.filter(row => row.provider !== domain);
        if (!session.metadataSources.some(row => row.id === session.metadataSourceId)) {
          session.metadata = null; session.metadataSourceId = null;
        }
        session.metadataEpoch++;
        if (session.target.metadataProvider && !session.stopped) ctx.detached(session, ctx.metadata.discoverMetadata(session));
        ctx.publish(session); continue;
      }
      const interrupted = !!session.runWork && (metadata || accountActivity ||
        activity && matchesSource(activity.source));
      const resumeRun = interrupted && metadata && !session.stopped;
      if (interrupted) { session.generation++; session.runKey = null; }
      const generation = session.generation;
      for (const [id, candidate] of session.candidates) if (matchesCandidate(candidate)) session.candidates.delete(id);
      const browsed = session.results.find(value => value.id === session.browsing.sourceId);
      const affectedBrowse = metadata || browsed && (matchesSource(browsed.source) ||
        browsed.detail?.lines.some(line => matchesAccount(line.provider) ||
          line.episodes.some(item => matchesAccount(item.file?.provider) || matchesAccount(item.share?.provider))));
      let readdSource = null;
      if (source) {
        session.sourceScope = session.sourceScope.filter(value => !matchesSource(value));
        for (const value of session.sources.filter(matchesSource)) {
          if (instances) {
            const instance = instances.find(row => row.installationId === value.installationId);
            const site = instance?.sites?.find(row => row.key === value.siteId);
            value.enabled = instance?.state === 'ready' && (value.engine !== 'cat' || !!site);
            if (instance?.generation > 0) value.generation = instance.generation;
            if (instance?.revision !== undefined) value.revision = instance.revision;
            if (site?.name) value.name = site.name;
          }
          session.search.set(value.key, {
            sourceKey: value.key, page: 0, completed: false,
            loading: false, error: null, seen: new Set(),
          });
        }
        session.results = session.results.filter(value => !matchesSource(value.source));
        session.resultKeys = new Set(session.results.map(value => value.id));
        if (session.target.kind === 'source') {
          const value = session.sources.find(row => row.key === session.target.source.key);
          if (value && value.enabled !== false) readdSource = value;
        }
      }
      if (account) for (const result of session.results) {
        if (result.recognized?.candidates.some(matchesCandidate) || result.detail?.lines.some(line => matchesAccount(line.provider) ||
            line.episodes.some(item => matchesAccount(item.file?.provider) || matchesAccount(item.share?.provider)))) {
          for (const field of ['detail', 'baseDetail', 'detailEntry', 'recognized', 'directories', 'browsing'])
            delete result[field];
          result.state = 'idle';
        }
      }
      const revoked = attempts.filter(matchesAttempt);
      const revokedCurrent = revoked.includes(session.current);
      const revokedPending = revoked.includes(session.pending);
      const selectedRevocation = revoked.find(value => sameEpisode(value.target, session.target));
      if (selectedRevocation) session.actionCurrent = selectedRevocation.candidate;
      if (source) {
        // Retain the exact saved file selector, but never resurrect a revoked
        // member by restoring a still-playing attempt's older target.
        for (const target of [session.target, session.playbackTarget, ...attempts.map(value => value.target)]) {
          if (target?.kind === 'group') target.members = target.members.filter(member =>
            !session.sources.some(value => value.key === member.sourceKey && matchesSource(value)));
        }
      }
      if (revokedCurrent) session.current = null;
      if (revokedPending) session.pending = null;
      // Publish revocation before draining network resources, so a native
      // candidate cannot commit while its cleanup is waiting on transport.
      if (affectedBrowse) {
        session.browseGeneration++;
        session.browsing = {
          ...session.browsing, loading: false,
          error: metadata ? 'STALE_METADATA' : source ? 'STALE_SOURCE' : 'STALE_ACCOUNT', lines: [], episodes: [],
        };
      }
      session.discoveryComplete = false;
      if (metadata) {
        const epoch = ++session.metadataEpoch;
        const admitted = () => !session.released && !session.stopped && !ctx.isClosed() && session.metadataEpoch === epoch;
        session.metadata = null;
        session.metadataSources = []; session.metadataSourceId = null;
        session.initializationError = null;
        session.initialization = ctx.tracked((async () => {
          try {
            if (!admitted()) return;
            const entry = await data.targetMetadataEntry(session.target, { rules: session.rules, canSchedule: admitted });
            if (!admitted()) return;
            const value = entry.value;
            session.error = null;
            session.browsing = { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
            const row = {
              provider: session.target.metadataProvider, metadataId: session.target.metadataId,
              kind: session.target.kind, title: session.target.title, metadata: value,
            };
            session.metadataSourceId = metadataIdentity(row);
            session.metadataSources = [{ ...row, id: session.metadataSourceId }];
            session.playbackMetadataSourceId = session.metadataSourceId;
            session.metadata = ctx.metadata.metadataDisplay(session, row);
            ctx.metadata.acceptMetadataCache(session, entry);
            ctx.detached(session, ctx.metadata.discoverMetadata(session));
          } catch (error) {
            if (!admitted()) return;
            session.initializationError = session.error = codeOf(error);
          }
          ctx.publish(session);
        })());
      }
      if (interrupted || metadata || revokedCurrent || revokedPending)
        session.stage = session.current && !session.current.resourceReleased ? 'playing'
          : session.pending && !session.pending.resourceReleased ? (session.pending.prepareOnly ? 'prepared' : 'pending') : 'idle';
      if (metadata || revokedCurrent || revokedPending || interrupted)
        session.error = metadata ? 'STALE_METADATA' : source ? 'STALE_SOURCE' : 'STALE_ACCOUNT';
      if (session.target.kind === 'group' && !session.target.members.length) {
        session.stage = 'unavailable'; session.error = 'SOURCE_NOT_FOUND';
      }
      ctx.publish(session);
      const prefetch = [...session.prefetch.values()].some(value => value.candidate && matchesAttempt(value));
      const readdWork = readdSource
        ? ctx.candidates.addResults(session, readdSource,
          [{ id: session.target.source.detailId, title: session.target.title }], 'linked',
          () => !ctx.isClosed() && !session.released && !session.stopped && session.generation === generation)
          .then(added => { if (added && !ctx.isClosed() && !session.released) ctx.publish(session); })
        : Promise.resolve();
      drains.push(Promise.all([
        readdWork,
        ...revoked.map(value => ctx.retire(session, value)),
        ...(metadata || prefetch || interrupted ? [ctx.prefetch.clearPrefetch(session)] : []),
      ]).then(() => {
        if (resumeRun && ctx.valid(session, generation))
          void ctx.selection.start(session, { prepareOnly: session.prepareOnly, constraint: session.constraint });
      }));
    }

    const results = await Promise.allSettled(drains);
    const failure = results.find(value => value.status === 'rejected');
    if (failure) throw failure.reason;
  }

  async function updateSourceDisplay(engine, instances) {
    for (const session of sessions.values()) {
      let changed = false, eligibilityChanged = false;
      const policy = ctx.preferences();
      for (const source of session.sources) {
        if (source.engine !== engine) continue;
        const instance = instances.find(row => row.installationId === source.installationId);
        if (!instance) continue;
        const site = instance.sites?.find(row => row.key === source.siteId);
        const name = site?.name ?? instance.name;
        if (typeof name !== 'string' || name === source.name) continue;
        const wasAllowed = isSmartSiteAllowed(source, policy);
        source.name = name; changed = true;
        eligibilityChanged ||= session.target.metadataProvider &&
          wasAllowed !== isSmartSiteAllowed(source, policy);
        for (const result of session.results) if (result.source.key === source.key) result.source.name = name;
        for (const candidate of session.candidates.values()) if (candidate.source.key === source.key) candidate.source.name = name;
      }
      if (eligibilityChanged) await ctx.prefetch.refreshEligibility(session);
      else if (changed) ctx.publish(session);
    }
  }

  return {
    initialize, open, updateSearch, saveHistory, report,
    stop, releaseSession, invalidate, updateSourceDisplay,
  };
}
