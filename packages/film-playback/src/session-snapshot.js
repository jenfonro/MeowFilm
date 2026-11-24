import { compareCandidates } from './policy.js';
import { clone, fields } from './identity.js';
import { publicCandidate, publicSource, publicTarget } from './session-helpers.js';

export function createSessionSnapshot(ctx) {
  const { catalog, browsingProjections } = ctx;

  function searchComplete(session) {
    return session.selectionKind === 'source' || session.sources.filter(source => ctx.sourceAllowed(session, source))
      .every(source => session.search.get(source.key).completed);
  }

  function build(session) {
    const retry = session.pending ?? session.current ?? session.lastFailed;
    const retryMediaAttemptId = !session.stopped && !session.initializationError && retry &&
      ctx.currentSource(session, retry.candidate.source) &&
      (retry !== session.lastFailed || ['error', 'unavailable'].includes(session.stage)) ? retry.id : null;
    const unverifiedHints = new Map();
    if (session.browsing.sourceId === null && session.target.metadataProvider) {
      for (const result of session.results) {
        if (!result.hint || result.match?.value === true) continue;
        if (!unverifiedHints.has(result.source.key)) unverifiedHints.set(result.source.key, new Set());
        unverifiedHints.get(result.source.key).add(result.source.detailId);
      }
    }
    const candidates = [...session.candidates.values()].filter(candidate => {
      if (session.browsing.sourceId !== null || !session.target.metadataProvider) return true;
      return candidate.eligible !== false && !unverifiedHints.get(candidate.source.key)?.has(candidate.detailId);
    });
    if (session.target.kind === 'movie') candidates.sort((a, b) => compareCandidates(a, b,
      { preferences: { ...ctx.preferences(), ruleOrder: ['quality', 'pan', 'keyword'] } }));
    const season = session.browsing.season ?? session.target.season;
    const kind = session.metadataSources.find(row => row.id === session.metadataSourceId)?.kind;
    let projected = browsingProjections.get(session.browsing);
    if (!projected || projected.season !== season || projected.kind !== kind) {
      const value = { ...session.browsing };
      if (value.sourceId !== null) {
        value.originalEpisodes = value.episodes;
        if (kind === 'tv' && season !== undefined && value.episodes.some(row => row.season !== undefined))
          value.episodes = value.episodes.filter(row => row.season === season);
      }
      projected = { season, kind, value };
      browsingProjections.set(session.browsing, projected);
    }
    const browsing = clone(projected.value);
    if (session.restoring) browsing.loading = true;
    const result = session.results.find(row => row.id === browsing.sourceId);
    if (result) {
      const parent = catalog.source(session.target, result.source);
      browsing.lines = browsing.lines.map(row => ({ ...row, ...catalog.line(parent, row) }));
    }
    const projectFile = row => {
      if (row.playbackTarget) return row;
      const candidate = session.candidates.get(row.candidateId);
      if (candidate) return { ...row, ...catalog.candidate(session.target, candidate) };
      const file = result?.detail?.lines.find(line => line.id === browsing.lineId)?.episodes.find(item => item.index === row.index);
      return file ? { ...row, ...catalog.file(session.target, result.source, browsing.lineId, file) } : row;
    };
    browsing.episodes = browsing.episodes.map(projectFile);
    if (browsing.originalEpisodes) browsing.originalEpisodes = browsing.originalEpisodes.map(projectFile);
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    // Cold source reentry retains the TMDB entry's metadata before discovery
    // supplies a selected metadata source.
    const metadataTarget = !session.target.metadataProvider && session.entryTarget.metadataProvider === 'tmdb'
      ? session.entryTarget : session.target;
    const metadataProvider = selected?.provider ?? metadataTarget.metadataProvider;
    const metadataKind = selected?.kind ?? metadataTarget.kind;
    const metadataId = selected?.metadataId ?? metadataTarget.metadataId;
    const metadataPath = `${metadataProvider === 'douban' ? '/rexxar/api/v2' : '/3'}/${metadataKind}/${metadataId}`;
    const metadata = session.metadata && metadataProvider
      ? catalog.metadata(metadataProvider, metadataPath, session.metadata, metadataTarget) : clone(session.metadata);
    const episodes = ctx.metadata.visibleEpisodes(session).map(row => {
      const entry = catalog.metadata(metadataProvider, `${metadataPath}/season/${row.season}`,
        { name: session.target.title, season_number: row.season, episodes: [row] });
      return entry.episodes?.[0] ?? row;
    });
    return {
      sessionId: session.id, sequence: session.sequence, rulesRevision: session.rules.revision,
      target: publicTarget(session.target),
      entryTarget: publicTarget(session.entryTarget), entryResolution: session.entryResolution,
      ...(session.entrySurface === undefined ? {} : { entrySurface: session.entrySurface }),
      stage: session.stage, error: session.error, initializationError: session.initializationError,
      retryMediaAttemptId,
      errorStage: session.error && session.error === session.feedbackFailure?.code ? session.feedbackFailure.stage : null,
      reportError: session.reportError, metadata, workInfo: ctx.metadata.workInfo(session),
      metadataSources: session.metadataSources.map(row => {
        const value = catalog.metadata(row.provider,
          `${row.provider === 'douban' ? '/rexxar/api/v2' : '/3'}/${row.kind}/${row.metadataId}`,
          { title: row.title, ...(row.seasons === undefined ? {} :
            row.provider === 'douban' ? { doubanSeasons: row.seasons } : { seasons: row.seasons }) });
        const seasons = value.doubanSeasons ?? value.seasons;
        return {
          ...fields(row, ['id', 'provider', 'metadataId', 'kind', 'title']),
          ...fields(value, ['businessId', 'playbackTarget']), ...(seasons === undefined ? {} : { seasons }),
        };
      }),
      metadataSourceId: session.metadataSourceId, metadataError: session.metadataError,
      metadataCache: ctx.metadata.metadataCacheState(session),
      metadataSupplement: ctx.metadata.metadataSupplement(session),
      sources: session.results.map(item => ({
        id: item.id, title: item.title,
        source: publicSource(item.source), state: item.state, error: item.error,
        ...catalog.source(session.target, item.source, item.title || session.target.title),
      })),
      search: [...session.search.values()].map(state => fields(state, ['sourceKey', 'page', 'completed', 'loading', 'error'])),
      browsing, episodes, candidates: candidates.map(candidate => publicCandidate(candidate, session.target, catalog)),
      attempt: session.pending && !session.pending.resourceReleased ? {
        id: session.pending.id,
        candidate: publicCandidate(session.pending.candidate, session.pending.target, catalog),
        prepareOnly: session.pending.prepareOnly, position: session.pending.position, paused: session.pending.paused,
      } : null,
      current: session.current && !session.current.resourceReleased ? {
        attemptId: session.current.id,
        candidate: publicCandidate(session.current.candidate, session.current.target, catalog),
        position: session.position, duration: session.duration, paused: session.paused,
      } : null,
      constraint: clone(session.constraint), searchComplete: searchComplete(session),
      preferences: clone(ctx.preferences()), shortVideo: session.shortVideo, previewEpisodes: session.previewEpisodes,
      prefetch: [...session.prefetch.values()].map(value => ({
        target: publicTarget(value.target),
        state: value.state, bytes: value.bytes ?? 0, error: value.error ?? null,
      })),
    };
  }

  return { build };
}
