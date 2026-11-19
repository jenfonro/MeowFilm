import { randomUUID } from 'node:crypto';
import { Readable } from 'node:stream';
import { isDeepStrictEqual } from 'node:util';
import { stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { compareCandidates, isCandidateAllowed, isSmartSiteAllowed, normalizePreferences, readPreferences } from './policy.js';
import { contentResults, matchesResult, mediaVariants, normalizeDetail, recognizeCandidates, recognizeVariantCandidates } from './recognition.js';
import { buildEpisodeMap, matchEpisodeMaps, resolveEpisode, sameSeasonPartition } from './episodes.js';
import { providerFamily, qualityKey } from './rules.js';
import { createFilmCatalog } from './catalog.js';
import { recognizeChapters } from './chapters.js';
import { checkSource, checkTarget, clone, codeOf, fail, identity, isIdentifier, object, requireValue,
  nativeMediaErrorCodes, playbackFeedbackFailure, resultDetailId, sourceRevision, text, variantParentId } from './identity.js';

const publicSource = source => fields(source, ['key', 'engine', 'installationId', 'siteId', 'revision',
  'generation', 'name', 'detailId']);
const publicCandidate = (candidate, target, catalog) => ({ ...fields(candidate, ['id', 'lineId', 'lineLabel', 'provider',
  'name', 'path', 'season', 'episode', 'globalEpisode', 'quality', 'resolution', 'fps', 'codec']),
  source: publicSource(candidate.source), ...catalog.candidate(target, candidate) });
const publicTarget = target => ({ ...fields(target, ['id', 'kind', 'title', 'year', 'metadataProvider',
  'metadataId', 'season', 'episode', 'globalEpisode', 'members']),
  ...(target.source ? { source: fields(target.source, ['key', 'engine', 'installationId', 'siteId',
    'detailId', 'lineId', 'candidateId']) } : {}) });
const sameEpisode = (a, b) => {
  if (a?.id !== b?.id || a?.kind !== b?.kind) return false;
  if (a?.source?.candidateId !== undefined || b?.source?.candidateId !== undefined)
    return a?.source?.candidateId === b?.source?.candidateId;
  if (a?.source?.key !== b?.source?.key || a?.source?.detailId !== b?.source?.detailId) return false;
  if (a?.globalEpisode !== undefined && b?.globalEpisode !== undefined) return a.globalEpisode === b.globalEpisode;
  if (a?.episode !== undefined || b?.episode !== undefined) return a?.season === b?.season && a?.episode === b?.episode;
  return a?.source?.episodeId === b?.source?.episodeId && a?.source?.playId === b?.source?.playId;
};
const sameActualEpisode = (a, b) => a?.id === b?.id && a?.kind === b?.kind &&
  ['source', 'group'].includes(a?.kind) && (a.kind === 'group' || a.source?.key === b.source?.key &&
    a.source?.detailId === b.source?.detailId) &&
  (a.episode !== undefined ? a.season === b.season && a.episode === b.episode &&
    (a.globalEpisode === undefined || a.globalEpisode === b.globalEpisode)
    : a.globalEpisode !== undefined && a.globalEpisode === b.globalEpisode ||
      !!variantParentId(a.source?.candidateId) &&
        variantParentId(a.source.candidateId) === variantParentId(b.source?.candidateId));
const canDiscoverVariants = candidate => candidate.source.engine === 'cat' &&
  !candidate.file && !candidate.share && !candidate.variant;
const sameVariantFile = (a, b) => !!a?.variant && !!b?.variant && a.parentId === b.parentId &&
  a.fileIdentity === b.fileIdentity && a.source.key === b.source.key && a.detailId === b.detailId &&
  a.lineId === b.lineId && a.playId === b.playId;
const cancelled = error => ['CLOSED', 'CANCELLED', 'STALE_ACCOUNT', 'STALE_SOURCE', 'STALE_SESSION', 'STALE_METADATA'].includes(error?.code);
const addressLifetime = 3 * 60_000;
function fields(value, names) {
  return Object.fromEntries(names.filter(name => value[name] !== undefined).map(name => [name, clone(value[name])]));
}
function historyPosition(row) {
  requireValue(Number.isFinite(row.position) && row.position >= 0, 'CORRUPT_STORAGE');
  return row.position;
}
function episodeTarget(target, episode) {
  const next = { ...target };
  for (const name of ['season', 'episode', 'globalEpisode']) {
    if (episode[name] === undefined) delete next[name];
    else next[name] = episode[name];
  }
  return checkTarget(next);
}

/** A page schedules finite business work. Cache producers and native media leases outlive observation. */
export function createPlaybackService({ data, library, media, rules, catalog = createFilmCatalog({ library }), now = Date.now }) {
  const sessions = new Map(), jobs = new Set(), calculations = new Set();
  const episodeMaps = new WeakMap(), browsingProjections = new WeakMap();
  let closed = false, closing;
  requireValue(rules && typeof rules.snapshot === 'function');
  const preferences = () => readPreferences(library.read().preferences);
  let observedPreferences = preferences();
  function get(id) {
    requireValue(!closed, 'CLOSED');
    const session = sessions.get(id);
    requireValue(session && !session.released, 'NOT_FOUND');
    return session;
  }
  function tracked(work) {
    jobs.add(work);
    void work.finally(() => jobs.delete(work)).catch(() => {});
    return work;
  }
  function calculate(session, operation, lifetime = 'playback') {
    const controller = new AbortController();
    calculations.add(controller);
    const work = tracked((async () => {
      requireValue(!closed && !session.released, 'CANCELLED');
      return operation(controller.signal);
    })());
    session.calculations.set(controller, { work, lifetime });
    void work.finally(() => {
      session.calculations.delete(controller);
      calculations.delete(controller);
    }).catch(() => {});
    return work;
  }
  function cancelCalculations(session, lifetime) {
    const pending = [...session.calculations].filter(([, value]) =>
      lifetime === undefined || value.lifetime === lifetime);
    for (const [controller] of pending) controller.abort();
    return Promise.allSettled(pending.map(([, value]) => value.work));
  }
  function detached(session, work) {
    tracked(work).catch(error => {
      if (!session.released && !closed && !cancelled(error)) {
        session.error = codeOf(error); publish(session);
      }
    });
  }
  function detachedBrowsing(session, work) {
    const generation = session.browseGeneration;
    tracked(work).catch(error => {
      if (!closed && !session.released && generation === session.browseGeneration && !cancelled(error)) {
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: codeOf(error) };
        publish(session);
      }
    });
  }
  function valid(session, generation) {
    return !closed && !session.released && session.generation === generation && !session.stopped;
  }
  async function runIO(session, generation, source, candidate, operation) {
    const activity = { generation, source, candidate };
    session.runActivity = activity;
    try { return await operation(); }
    finally { if (session.runActivity === activity) session.runActivity = null; }
  }
  function currentSource(session, source) {
    if (source.engine === 'pan') return true;
    if (session.selectionKind === 'group' && !session.sourceScope.some(value =>
      value.key === source.key && sourceRevision(value) === sourceRevision(source))) return false;
    const current = session.sources.find(value => value.key === source.key);
    return current && current.enabled !== false && current.generation === source.generation &&
      current.revision === source.revision;
  }
  function publish(session) {
    if (session.released) return;
    session.sequence++;
    for (const listener of session.listeners) listener();
  }
  function sourceAllowed(session, source) {
    return currentSource(session, source) &&
      (session.selectionKind !== 'source' || source.key === session.selectionSource.key);
  }
  function smartSourceAllowed(session, source) {
    const current = source.engine === 'pan' ? source : session.sources.find(value => value.key === source.key);
    return sourceAllowed(session, source) && isSmartSiteAllowed(current, preferences());
  }
  function groupScopeAvailable(session) {
    return session.target.kind !== 'group' || session.target.members.length > 0 ||
      session.seedWaitGroup;
  }
  function searchComplete(session) {
    return session.selectionKind === 'source' || session.sources.filter(source => sourceAllowed(session, source))
      .every(source => session.search.get(source.key).completed);
  }
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
    const today = new Date(now() + 8 * 3_600_000).toISOString().slice(0, 10);
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
    return session.episodeProjection = { metadata, today, preview: session.previewEpisodes,
      map, visible, keys: new Set(map.map(row => `${row.season}:${row.episode}`)),
      visibleKeys: new Set(visible.map(row => `${row.season}:${row.episode}`)), seasons: new Map() };
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
    return { provider: selected.provider, metadataId: String(selected.metadataId), kind: selected.kind,
      title: text(metadata.title) || text(metadata.name) || selected.title,
      summary, seasonCount, episodeCount,
      metadata: catalog.metadata(selected.provider, path, value, session.entryTarget) };
  }
  function snapshot(session) {
    const retry = session.pending ?? session.current ?? session.lastFailed;
    const retryMediaAttemptId = !session.stopped && !session.initializationError && retry &&
      currentSource(session, retry.candidate.source) &&
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
      { preferences: { ...preferences(), ruleOrder: ['quality', 'pan', 'keyword'] } }));
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
    const episodes = visibleEpisodes(session).map(row => {
      const entry = catalog.metadata(metadataProvider, `${metadataPath}/season/${row.season}`,
        { name: session.target.title, season_number: row.season, episodes: [row] });
      return entry.episodes?.[0] ?? row;
    });
    return {
      sessionId: session.id, sequence: session.sequence, rulesRevision: session.rules.revision, target: publicTarget(session.target),
      entryTarget: publicTarget(session.entryTarget), entryResolution: session.entryResolution,
      ...(session.entrySurface === undefined ? {} : { entrySurface: session.entrySurface }),
      stage: session.stage, error: session.error, initializationError: session.initializationError,
      retryMediaAttemptId,
      errorStage: session.error && session.error === session.feedbackFailure?.code ? session.feedbackFailure.stage : null,
      reportError: session.reportError, metadata, workInfo: workInfo(session),
      metadataSources: session.metadataSources.map(row => {
        const value = catalog.metadata(row.provider,
          `${row.provider === 'douban' ? '/rexxar/api/v2' : '/3'}/${row.kind}/${row.metadataId}`,
          { title: row.title, ...(row.seasons === undefined ? {} :
            row.provider === 'douban' ? { doubanSeasons: row.seasons } : { seasons: row.seasons }) });
        const seasons = value.doubanSeasons ?? value.seasons;
        return { ...fields(row, ['id', 'provider', 'metadataId', 'kind', 'title']),
          ...fields(value, ['businessId', 'playbackTarget']), ...(seasons === undefined ? {} : { seasons }) };
      }),
      metadataSourceId: session.metadataSourceId, metadataError: session.metadataError,
      metadataCache: metadataCacheState(session),
      metadataSupplement: metadataSupplement(session),
      sources: session.results.map(item => ({ id: item.id, title: item.title,
        source: publicSource(item.source), state: item.state, error: item.error,
        ...catalog.source(session.target, item.source, item.title || session.target.title) })),
      search: [...session.search.values()].map(state => fields(state, ['sourceKey', 'page', 'completed', 'loading', 'error'])),
      browsing, episodes, candidates: candidates.map(candidate => publicCandidate(candidate, session.target, catalog)),
      attempt: session.pending && !session.pending.resourceReleased ? { id: session.pending.id, candidate: publicCandidate(session.pending.candidate, session.pending.target, catalog),
        prepareOnly: session.pending.prepareOnly, position: session.pending.position, paused: session.pending.paused } : null,
      current: session.current && !session.current.resourceReleased ? { attemptId: session.current.id,
        candidate: publicCandidate(session.current.candidate, session.current.target, catalog), position: session.position,
        duration: session.duration, paused: session.paused } : null,
      constraint: clone(session.constraint), searchComplete: searchComplete(session),
      preferences: clone(preferences()), shortVideo: session.shortVideo, previewEpisodes: session.previewEpisodes,
      prefetch: [...session.prefetch.values()].map(value => ({ target: publicTarget(value.target),
        state: value.state, bytes: value.bytes ?? 0, error: value.error ?? null })),
    };
  }
  function contentTarget(session) {
    const target = session.searchMetadataTarget ?? session.entryTarget;
    const metadata = session.entryMetadata ?? playbackMetadata(session);
    const value = { ...metadata, ...session.entryTarget, ...target };
    if (session.searchMetadataTarget)
      value.aliases = [metadata?.aliases, session.entryTarget.aliases, target.aliases]
        .filter(Array.isArray).flat().concat(session.entryTarget.title);
    return value;
  }
  async function addResults(session, source, items, kind = 'result', admitted = () => true, members = []) {
    const known = new Set([...(session.entryTarget.kind === 'group' ? session.entryTarget.members : []),
      ...(session.target.kind === 'group' ? session.target.members : []), ...members]
      .filter(member => member.sourceKey === source.key).map(member => member.detailId));
    for (const target of [session.entryTarget, session.target])
      if ((target.kind === 'source' || target.kind === 'group') && target.source?.key === source.key)
        known.add(target.source.detailId);
    const unchecked = items.filter(raw => kind !== 'linked' && session.selectionKind !== 'source' &&
      !known.has(resultDetailId(raw)));
    const matching = unchecked.length ? await calculate(session, signal =>
      contentResults(unchecked, { target: contentTarget(session), rules: session.rules, signal }), 'session') : [];
    const accepted = new Set(unchecked.filter((_, index) => matching[index]));
    if (closed || session.released || session.stopped || !admitted()) return;
    for (const raw of items) {
      const detailId = resultDetailId(raw);
      if (!isIdentifier(detailId)) continue;
      if (kind === 'result') session.search.get(source.key)?.seen.add(detailId);
      if (session.selectionKind === 'source' ? source.key !== session.selectionSource.key ||
          detailId !== session.selectionSource.detailId : kind !== 'linked' && !known.has(detailId) && !accepted.has(raw)) continue;
      const id = identity(source.key, detailId);
      const contentMember = kind === 'result' || known.has(detailId);
      if (session.target.kind === 'group' && kind === 'result' && accepted.has(raw))
        addMember(session, source.key, detailId);
      if (session.resultKeys.has(id)) {
        const result = session.results.find(value => value.id === id);
        result.contentMember ||= contentMember;
        if (kind === 'hint' && result.linked && !result.detail) {
          result.hint = true; result.raw = clone(raw);
          result.title = text(raw.vod_name ?? raw.name ?? raw.title);
          delete result.linked;
        }
        if (kind === 'hint' || kind === 'linked') continue;
        // Only current-owner search evidence can promote a cross-owner hint;
        // history/manual linkage and synthetic raw targets cannot approve it.
        const title = text(raw.vod_name ?? raw.name ?? raw.title);
        if (title || result.hint || result.linked) {
          result.raw = clone(raw);
          delete result.linked;
          if (title) result.title = title;
          if (result.hint) {
            delete result.hint; delete result.verifiedRaw;
            result.state = 'idle'; result.error = null;
          }
        }
        delete result.match;
        continue;
      }
      session.resultKeys.add(id);
      session.results.push({ id, source: { ...source, detailId }, title: text(raw.vod_name ?? raw.name ?? raw.title),
        raw: clone(raw), contentMember,
        ...(kind === 'hint' ? { hint: true } : kind === 'linked' ? { linked: true } : {}),
        state: 'idle', error: null });
      if (session.metadataSourceId === null && session.browsing.sourceId === null &&
          !session.target.metadataProvider && !session.userSelected)
        session.browsing = { ...session.browsing, sourceId: id };
    }
  }
  function addMember(session, sourceKey, detailId) {
    requireValue(groupScopeAvailable(session), 'SOURCE_NOT_FOUND');
    if (!session.target.members.some(row => row.sourceKey === sourceKey && row.detailId === detailId))
      session.target.members.push({ sourceKey, detailId });
    session.seedWaitGroup = false;
  }
  const manualIdentity = row => identity(fields(row, ['targetId', 'kind', 'sourceKey', 'detailId',
    'provider', 'share', 'seasonHint']));
  function currentManual(session, value) {
    return library.read().manual.find(row => row?.id === value.manualId && row.enabled !== false &&
      row.targetId === session.target.id && manualIdentity(row) === value.manualIdentity);
  }
  async function recognizeResult(session, result, detail, target = session.target,
    metadata = playbackMetadata(session), lifetime = 'playback') {
    if (result.hint && !result.verifiedRaw) return [];
    const source = { ...result.source };
    const manual = result.manualId ? currentManual(session, result) : null;
    if (result.manualId) {
      delete source.manualSeasonHint;
      if (manual?.seasonHint !== undefined) source.manualSeasonHint = manual.seasonHint;
    }
    const candidates = (await calculate(session, signal =>
      recognizeCandidates(detail, { target, source, metadata, rules: session.rules, signal,
        completeDirectory: true }), lifetime))
      .map(candidate => manual ? { ...candidate, manualId: manual.id, manualIdentity: manualIdentity(manual) } : candidate);
    const expanded = [];
    for (const candidate of candidates) {
      const variants = result.variants?.get(candidate.id);
      if (variants) expanded.push(...await calculate(session, signal =>
        recognizeVariantCandidates(candidate, variants, { rules: session.rules, signal }), lifetime));
      else expanded.push(candidate);
    }
    return expanded;
  }
  async function candidatesFrom(session, result, detail) {
    if (closed || session.released || result.detail !== detail || !session.results.includes(result) ||
        !currentSource(session, result.source)) return;
    const target = session.target, metadata = playbackMetadata(session), generation = session.generation;
    const manual = result.manualId ? currentManual(session, result) : null;
    const manualKey = manual ? manualIdentity(manual) : null;
    const context = { detail, metadata: target.kind === 'group' ? null : metadata,
      targetId: target.id, kind: target.kind, variants: result.variants,
      manualKey, seasonHint: result.source.manualSeasonHint, hint: result.hint, verifiedRaw: result.verifiedRaw };
    const previous = result.recognized;
    if (previous && Object.keys(context).every(key => previous[key] === context[key])) {
      if (previous.installed !== session.candidateEpoch) {
        for (const candidate of previous.candidates) session.candidates.set(candidate.id, candidate);
        previous.installed = session.candidateEpoch;
      }
      return;
    }
    const candidates = await recognizeResult(session, result, detail);
    if (closed || session.released) return;
    const current = result.manualId ? currentManual(session, result) : null;
    if (target !== session.target ||
        metadata !== playbackMetadata(session) || generation !== session.generation || result.detail !== detail ||
        !session.results.includes(result) || result.variants !== context.variants ||
        result.hint !== context.hint || result.verifiedRaw !== context.verifiedRaw ||
        result.source.manualSeasonHint !== context.seasonHint ||
        (current ? manualIdentity(current) : null) !== manualKey || !currentSource(session, result.source)) return;
    for (const [id, candidate] of session.candidates) if (candidate.source.key === result.source.key &&
      candidate.source.detailId === result.source.detailId) session.candidates.delete(id);
    for (const candidate of candidates) session.candidates.set(candidate.id, candidate);
    result.recognized = { ...context, candidates, installed: session.candidateEpoch };
  }
  async function registerVariants(session, candidate, descriptor) {
    const variants = mediaVariants(descriptor);
    if (!variants.length) return false;
    const result = session.results.find(value => value.source.key === candidate.source.key &&
      value.source.detailId === candidate.source.detailId);
    requireValue(result?.detail, 'SOURCE_NOT_FOUND');
    const parentId = candidate.parentId ?? candidate.id;
    if (identity(result.variants?.get(parentId)) === identity(variants)) return false;
    result.variants = new Map(result.variants);
    result.variants.set(parentId, variants);
    await candidatesFrom(session, result, result.detail);
    return true;
  }
  function excluded(session, candidate) {
    session.excluded.add(candidate.id);
    if (candidate.fileIdentity && !candidate.variant) session.excluded.add(candidate.fileIdentity);
  }
  async function smartResult(session, result, target = session.target) {
    if (result.hint && !result.verifiedRaw) return false;
    if (!result.hint) {
      if (result.linkedTargetId === target.id && (!result.manualId || !!currentManual(session, result))) return true;
    }
    const content = session.searchMetadataTarget ?? session.entryTarget;
    const raw = result.hint ? result.verifiedRaw : result.raw;
    const contentIdentity = !result.hint && result.contentMember ? content : null;
    const previous = result.match;
    const metadata = playbackMetadata(session);
    if (previous && previous.raw === raw && previous.target === target && previous.metadata === metadata &&
        previous.contentKind === contentIdentity?.kind && previous.contentProvider === contentIdentity?.metadataProvider &&
        previous.contentId === contentIdentity?.metadataId) return previous.value;
    const entry = { raw, target, metadata, contentKind: contentIdentity?.kind,
      contentProvider: contentIdentity?.metadataProvider, contentId: contentIdentity?.metadataId };
    result.match = entry;
    entry.value = calculate(session, signal => matchesResult(raw, { target: { ...metadata, ...target },
      rules: session.rules, ...(contentIdentity
        ? { contentIdentity: fields(contentIdentity, ['kind', 'metadataProvider', 'metadataId']) } : {}), signal }));
    try { entry.value = await entry.value; }
    catch (error) {
      if (result.match === entry) delete result.match;
      throw error;
    }
    return entry.value;
  }
  async function allowed(session, candidate, { gate = true, explicit = false, target = session.target, retry = false,
    discoverVariants = false } = {}) {
    if (!retry && (session.excluded.has(candidate.id) ||
        candidate.fileIdentity !== undefined && session.excluded.has(candidate.fileIdentity))) return false;
    if (target.metadataProvider && metadataCacheState(session, session.playbackMetadataSourceId)?.status === 'stale') return false;
    if (target.metadataProvider && candidate.eligible === false) return false;
    if (candidate.share && !candidate.file) return false;
    if (!explicit && !(target.metadataProvider ? smartSourceAllowed(session, candidate.source)
      : sourceAllowed(session, candidate.source))) return false;
    const result = session.results.find(value => value.source.key === candidate.source.key &&
      value.source.detailId === candidate.source.detailId);
    if (result && target.metadataProvider && (result.hint || !explicit) &&
        !await smartResult(session, result, target)) return false;
    if (candidate.manualId && !currentManual(session, candidate)) return false;
    let policyTarget = target;
    const expected = target.source?.candidateId;
    const expectedParent = variantParentId(expected);
    const reselecting = target === session.target && !explicit
      ? session.reselecting || (session.constraint.action === 'quality' && expectedParent
        ? session.candidates.get(expected) : null) : null;
    let actualEpisode = reselecting;
    const parentDiscovery = discoverVariants && candidate.id === expectedParent;
    if (expectedParent && !reselecting && candidate.id !== expected && !parentDiscovery) return false;
    if (candidate.parentId === expected || parentDiscovery) {
      policyTarget = { ...target, source: { ...target.source, candidateId: candidate.id } };
    }
    if (actualEpisode && actualEpisode.episode === undefined && actualEpisode.globalEpisode === undefined) {
      if (!sameVariantFile(candidate, actualEpisode)) return false;
      const { candidateId, ...source } = target.source;
      policyTarget = { ...target, source };
      actualEpisode = null;
    }
    const constraint = explicit || discoverVariants && session.constraint.action === 'quality'
      ? { action: 'default' } : session.constraint;
    if (!isCandidateAllowed(candidate, { target: policyTarget, constraint,
      excluded: retry ? new Set() : session.excluded, blocks: library.read().blocks,
      actualEpisode })) return false;
    if (target.kind === 'tv' && !explicit) {
      const projection = episodeProjection(session, playbackMetadata(session)), key = `${candidate.season}:${candidate.episode}`;
      if (projection.keys.has(key) && !projection.visibleKeys.has(key)) return false;
    }
    if (!gate || explicit || session.discoveryComplete) return true;
    if (session.constraint.action === 'pan') return ['4k', '4k_hdr', '4k_fps'].includes(qualityKey(candidate.quality));
    if (session.constraint.action === 'quality') {
      const family = providerFamily(session.current?.candidate.provider ?? session.actionCurrent?.provider);
      return !family || providerFamily(candidate.provider) === family;
    }
    return true;
  }
  async function loadDetail(session, result, generation, browsing = false, refresh = false) {
    const admitted = () => !closed && !session.released &&
      currentSource(session, result.source) &&
      (browsing ? session.browseGeneration === generation : valid(session, generation));
    result.state = 'loading'; result.error = null; publish(session);
    try {
      // Re-enter the data owner: a long-lived page must not become an immortal second TTL cache.
      const read = () => data.detailEntry(result.source, result.source.detailId, { canSchedule: admitted, refresh });
      const value = await (browsing ? read() : runIO(session, generation, result.source, null, read));
      if (!admitted()) return null;
      // Cache entries are immutable. The session's expansion/display projection
      // is owned here, and must not mutate the shared source detail.
      if (!result.detail || result.detailEntry !== value &&
          !isDeepStrictEqual(result.detailEntry?.value, value.value)) {
        result.baseDetail = normalizeDetail(clone(value.value), { source: result.source });
        result.detail = { ...result.baseDetail, lines: [...result.baseDetail.lines] };
      }
      result.detailEntry = value;
      if (result.hint) {
        const raw = result.detail.raw, detailId = result.source.detailId;
        // Use the same strictly typed record selected by normalizeDetail, not
        // list[0] or identity/title copied from the previous runtime's search.
        const record = Array.isArray(raw?.list)
          ? raw.list.find(row => resultDetailId(row) === detailId) : raw;
        const recordId = resultDetailId(record);
        result.verifiedRaw = object(record) && result.detail.detailId === detailId &&
          (recordId === undefined || recordId === detailId)
          ? record : undefined;
      }
      if (!browsing) await candidatesFrom(session, result, result.detail);
      if (!admitted()) return null;
      if (result.hint) {
        requireValue(result.verifiedRaw, 'UPSTREAM_FORMAT');
        if (!browsing && session.target.metadataProvider)
          requireValue(await smartResult(session, result), 'NO_MATCH');
      }
      if (!admitted()) return null;
      result.state = 'ready'; publish(session);
      return result.detail;
    } catch (error) {
      if (admitted()) {
        result.state = 'error'; result.error = codeOf(error);
        if (!browsing) session.selectionError = result.error;
        publish(session);
      }
      return null;
    }
  }
  async function expandDetail(session, result, detail, generation) {
    for (let index = 0; index < detail.lines.length && valid(session, generation); index++) {
      const line = result.baseDetail?.lines[index] ?? detail.lines[index];
      if (!session.reselecting && session.target.source?.lineId !== undefined &&
          line.id !== session.target.source.lineId) continue;
      if (session.constraint.action === 'pan' && providerFamily(line.provider) &&
          providerFamily(line.provider) !== providerFamily(session.constraint.provider)) continue;
      if (line.expanded || !line.episodes.some(item => item.share ||
        String(item.playId ?? item.id ?? item.url).startsWith('meow.pan.v1.'))) continue;
      try {
        const expanded = await runIO(session, generation, result.source, {
          accountProviders: line.episodes.map(item => item.file?.provider ?? item.share?.provider ?? item.provider ?? line.provider),
        }, () => data.expand(result.source, line, { canSchedule: () => valid(session, generation) }));
        if (!valid(session, generation)) return false;
        detail = replaceLine(result, detail, index, expanded);
        await candidatesFrom(session, result, detail);
        if (!valid(session, generation)) return false;
        publish(session);
        if (await tryCandidate(session, generation)) return true;
      } catch (error) {
        if (valid(session, generation)) {
          result.error = session.selectionError = codeOf(error); publish(session);
        }
      }
    }
    return false;
  }
  function preparedValid(value) {
    return value?.descriptor && !value.resourceReleased && now() < (Number.isFinite(value.descriptor.expiresAt)
      ? Math.min(value.descriptor.expiresAt, value.preparedAt + addressLifetime) : value.preparedAt + addressLifetime);
  }
  async function retire(session, value) {
    await media.release(value.resource.id);
    session.leases.delete(value.id);
  }
  async function installPending(session, candidate, descriptor, resource, generation, preparedAt = now(), explicit = false) {
    if (!valid(session, generation)) { await media.release(resource.id); return false; }
    if (['source', 'group'].includes(session.target.kind)) session.target = targetForCandidate(session.target, candidate);
    const metadataKind = playbackMetadataSource(session)?.kind;
    const sameContent = session.playbackTarget?.id === session.target.id;
    const sameMetadataEpisode = sameContent && metadataKind === 'tv' &&
      (session.playbackTarget.globalEpisode !== undefined && session.target.globalEpisode !== undefined
        ? session.playbackTarget.globalEpisode === session.target.globalEpisode
        : session.playbackTarget.season !== undefined && session.playbackTarget.episode !== undefined &&
          session.playbackTarget.season === session.target.season &&
          session.playbackTarget.episode === session.target.episode);
    const continuation = sameEpisode(session.playbackTarget, session.target) ||
      sameContent && metadataKind === 'movie' || sameMetadataEpisode ||
      (session.reselecting || sameVariantFile(session.actionCurrent, candidate)) &&
        sameActualEpisode(session.playbackTarget, session.target);
    session.pending = { id: randomUUID(), candidate, descriptor, resource, preparedAt, generation, explicit,
      prepareOnly: session.prepareOnly, target: clone(session.target),
      metadataSourceId: session.playbackMetadataSourceId,
      position: continuation ? session.position
        : !session.resumeCandidateId || session.resumeCandidateId === candidate.id ? session.resumePosition : 0,
      paused: continuation ? session.paused : false };
    session.leases.set(session.pending.id, resource.id);
    session.stage = session.prepareOnly ? 'prepared' : 'pending'; session.error = null;
    publish(session);
    return true;
  }
  async function tryCandidate(session, generation) {
    const attempted = new Set();
    while (valid(session, generation)) {
      let candidate = session.explicitCandidateId ? session.candidates.get(session.explicitCandidateId) : null;
      if (candidate && !await allowed(session, candidate,
        { explicit: true, discoverVariants: canDiscoverVariants(candidate) })) candidate = null;
      if (session.explicitCandidateId && !candidate) return false;
      const explicit = !!candidate;
      if (!candidate) {
        const available = [...session.candidates.values()].filter(value => !attempted.has(value.id) &&
          !session.excluded.has(value.id) && !session.excluded.has(value.fileIdentity));
        const eligibility = await Promise.all(available.map(value =>
          allowed(session, value, { discoverVariants: canDiscoverVariants(value) })));
        const options = available.filter((_, index) => eligibility[index]);
        options.sort((a, b) => Number(b.id === session.resumeCandidateId) - Number(a.id === session.resumeCandidateId) ||
          compareCandidates(a, b, { preferences: preferences(), constraint: session.constraint,
            current: session.current?.candidate ?? session.actionCurrent }));
        candidate = options[0];
      }
      if (!valid(session, generation)) return false;
      session.explicitCandidateId = null;
      if (!candidate) return false;
      if (!currentSource(session, candidate.source)) return false;
      session.stage = 'resolving'; session.error = null; publish(session);
      try {
        let reconsider = false;
        const prepared = await runIO(session, generation, candidate.source, candidate, async () => {
          const descriptor = await data.resolve(candidate, { canSchedule: () => valid(session, generation),
            discoverVariants: canDiscoverVariants(candidate) });
          if (!valid(session, generation)) return false;
          if (await registerVariants(session, candidate, descriptor)) {
            if (!valid(session, generation)) return false;
            if (candidate.variant) {
              candidate = session.candidates.get(candidate.id);
              requireValue(candidate, 'MEDIA_UNAVAILABLE');
            } else {
              if (explicit) {
                const children = [...session.candidates.values()].filter(value => value.parentId === candidate.id);
                children.sort((a, b) => compareCandidates(a, b, { preferences: preferences() }));
                session.explicitCandidateId = children[0]?.id ?? null;
              }
              reconsider = true; publish(session); return false;
            }
          }
          // Discovery is permitted before quality filtering, never preparation.
          if (!await allowed(session, candidate, { explicit })) { reconsider = true; return false; }
          if (!valid(session, generation)) return false;
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          return installPending(session, candidate, descriptor, resource, generation, now(), explicit);
        });
        if (reconsider) { attempted.add(candidate.id); continue; }
        return prepared;
      } catch (error) {
        if (!valid(session, generation)) return false;
        excluded(session, candidate); session.selectionError = codeOf(error); publish(session);
      }
    }
    return false;
  }
  async function consumeResult(session, result, generation) {
    if (!valid(session, generation) || !(session.target.metadataProvider
      ? smartSourceAllowed(session, result.source) : sourceAllowed(session, result.source))) return false;
    if (session.target.kind === 'source' && result.source.detailId !== session.target.source.detailId) return false;
    if (session.target.kind === 'group' && session.target.source && !session.reselecting &&
        (result.source.key !== session.target.source.key ||
          result.source.detailId !== session.target.source.detailId)) return false;
    if (session.target.metadataProvider && !result.hint && !await smartResult(session, result)) return false;
    if (!valid(session, generation) || !currentSource(session, result.source)) return false;
    // Manual share directories are already expanded by their own stage; they
    // are not script detail endpoints and remain available to adjacent prefetch.
    if (result.source.engine === 'pan') return false;
    const detail = await loadDetail(session, result, generation);
    if (!detail || !valid(session, generation)) return false;
    if (await tryCandidate(session, generation)) return true;
    return expandDetail(session, result, detail, generation);
  }
  async function searchPage(session, source) {
    const state = session.search.get(source.key);
    if (!state || state.completed || !groupScopeAvailable(session)) return false;
    if (state.work) return state.work;
    const epoch = session.searchGeneration;
    const admitted = () => !session.released && !closed && epoch === session.searchGeneration &&
      session.search.get(source.key) === state && currentSource(session, source) && groupScopeAvailable(session);
    const work = (async () => {
      state.loading = true; state.error = null; publish(session);
      try {
        const page = await data.search(source, session.query, state.page + 1, { canSchedule: admitted });
        if (!admitted()) return false;
        const alreadyConfirmed = page.page <= state.page;
        const hasNewRaw = page.items.some(raw => {
          const id = resultDetailId(raw);
          return isIdentifier(id) && !state.seen.has(id);
        });
        await addResults(session, source, page.items, 'result', admitted);
        if (!admitted()) return false;
        if (!alreadyConfirmed) {
          state.page = page.page;
          state.completed ||= !page.hasMore || !page.explicitPageCount && !hasNewRaw;
          state.error = null;
        }
        return true;
      } catch (error) {
        if (admitted()) state.error = codeOf(error);
        return false;
      } finally {
        state.loading = false; delete state.work;
        if (epoch === session.searchGeneration) publish(session);
      }
    })();
    state.work = work;
    return work;
  }
  async function manualOutcome(row, error) {
    if (error && cancelled(error)) return;
    await library.update(document => {
      const entry = document.manual.find(item => item?.id === row.id);
      if (!entry || entry.enabled === false || manualIdentity(entry) !== manualIdentity(row)) return;
      requireValue(Number.isSafeInteger(entry.failureCount) && entry.failureCount >= 0, 'CORRUPT_STORAGE');
      entry.failureCount = error ? entry.failureCount + 1 : 0;
      if (entry.autoDisable && entry.failureCount >= 3) entry.enabled = false;
    });
  }
  async function run(session, generation) {
    try {
      await session.initialization;
      if (!valid(session, generation)) return;
      if (session.initializationError && session.target.metadataProvider) throw fail(session.initializationError);
      if (session.target.metadataProvider) await loadPlaybackMetadata(session, generation);
      if (!valid(session, generation)) return;
      if (!groupScopeAvailable(session)) {
        session.stage = 'unavailable'; session.error = 'SOURCE_NOT_FOUND'; publish(session); return;
      }
      if (['source', 'group'].includes(session.target.kind) && !session.reselecting &&
          session.constraint.action === 'default') {
        const result = session.results.find(row => row.source.key === session.target.source?.key &&
          row.source.detailId === session.target.source.detailId);
        requireValue(result && currentSource(session, result.source), 'SOURCE_NOT_FOUND');
        if (await tryCandidate(session, generation)) return;
        if (!session.selectionError && await consumeResult(session, result, generation)) return;
        if (!valid(session, generation)) return;
        session.stage = 'unavailable';
        session.error = selectionError(session, result.error ??
          (session.target.source.candidateId ? 'NOT_FOUND' : 'MEDIA_UNAVAILABLE'));
        publish(session); return;
      }
      requireValue(session.target.kind !== 'tv' || session.target.episode !== undefined, 'NO_EPISODE_MAPPING');
      if (session.target.kind === 'tv' && session.target.metadataProvider === 'tmdb' && session.target.season !== undefined) {
        const target = session.target;
        const entry = await data.seasonMetadataEntry(target, target.season, { rules: session.rules,
          canSchedule: () => valid(session, generation) });
        if (!valid(session, generation)) return;
        const display = mergeSeason(target, playbackMetadata(session), target.season, entry.value);
        acceptMetadataCache(session, entry, target.season, undefined, session.playbackMetadataSourceId, display);
        if (session.playbackMetadataSourceId === session.metadataSourceId) session.metadata = display;
        requireFreshMetadata(session);
      }
      session.stage = session.target.metadataProvider ? 'cached' : 'loading'; publish(session);
      const neighborKey = identity(session.target);
      const neighbor = session.prefetch.get(neighborKey);
      if (preparedValid(neighbor) && await allowed(session, neighbor.candidate)) {
        session.prefetch.delete(neighborKey);
        await media.cancelPrefetch(neighbor.resource.id);
        if (await installPending(session, neighbor.candidate, neighbor.descriptor, neighbor.resource,
          generation, neighbor.preparedAt)) return;
      }
      if (await tryCandidate(session, generation)) return;
      const visited = new Set();
      const consume = async result => {
        // A hint is visited without trusting its old raw. A later real search
        // promotes the same row and gets one ordinary result visit of its own.
        const key = result.hint ? result : result.id;
        if (visited.has(key) || session.target.metadataProvider && !result.hint &&
            !await smartResult(session, result)) return false;
        visited.add(key);
        return consumeResult(session, result, generation);
      };
      const savedHistory = await library.history(session.target.id);
      const document = {...library.read(),history:savedHistory ? [savedHistory] : []};
      for (const stage of ['history', 'manual']) {
        if (!valid(session, generation)) return;
        session.stage = session.target.metadataProvider ? stage : 'loading'; publish(session);
        const rows = document[stage].filter(row => row?.targetId === session.target.id && row.enabled !== false);
        for (const row of rows) {
          if (!valid(session, generation)) return;
          if (stage === 'manual' && (!Number.isSafeInteger(row.failureCount) || row.failureCount < 0 ||
              row.failureCount === Number.MAX_SAFE_INTEGER)) {
            session.selectionError = 'CORRUPT_STORAGE';
            continue;
          }
          if (stage === 'manual' && row.share) {
            if (['source', 'group'].includes(session.target.kind)) continue;
            const source = { key: `manual:${row.id}`, engine: 'pan', name: row.name || '人工匹配',
              detailId: row.id, ...fields({ manualSeasonHint: row.seasonHint }, ['manualSeasonHint']) };
            try {
              const line = await runIO(session, generation, source, { provider: row.provider }, () =>
                data.expand(source, { id: row.id, label: row.name || row.provider,
                provider: row.provider, episodes: [{ id: row.id, name: row.name || session.target.title,
                  share: { ...row.share, provider: row.provider } }] },
                { canSchedule: () => valid(session, generation) }));
              if (!valid(session, generation)) return;
              await addResults(session, source, [{ id: row.id }], 'linked');
              const result = session.results.find(value => value.id === identity(source.key, row.id));
              const detail = { lines: [line], detailId: row.id };
              Object.assign(result, { title: row.title || session.target.title, linkedTargetId: session.target.id,
                manualId: row.id, manualIdentity: manualIdentity(row), state: 'ready', error: null,
                detail: isDeepStrictEqual(result.detail, detail) ? result.detail : detail });
              await candidatesFrom(session, result, result.detail);
              await manualOutcome(row);
              if (await tryCandidate(session, generation)) return;
            } catch (error) {
              if (!valid(session, generation)) return;
              await manualOutcome(row, error); session.selectionError = codeOf(error); publish(session);
            }
            continue;
          }
          const source = session.sources.find(item => item.key === row.sourceKey);
          if (!source || !isIdentifier(row.detailId) || !(session.target.metadataProvider
            ? smartSourceAllowed(session, source) : sourceAllowed(session, source))) continue;
          if (session.target.kind === 'source' && row.detailId !== session.target.source.detailId) continue;
          await addResults(session, source, [{ id: row.detailId }], 'linked');
          const result = session.results.find(item => item.source.key === source.key && item.source.detailId === row.detailId);
          if (!result.title) result.title = row.title || session.target.title;
          result.linkedTargetId = session.target.id;
          if (session.target.kind === 'group') addMember(session, source.key, row.detailId);
          delete result.manualId; delete result.manualIdentity; delete result.source.manualSeasonHint;
          if (stage === 'manual') {
            result.manualId = row.id; result.manualIdentity = manualIdentity(row);
          }
          if (row.seasonHint !== undefined) result.source.manualSeasonHint = row.seasonHint;
          const requested = !visited.has(result.hint ? result : result.id);
          const found = await consume(result);
          if (stage === 'manual' && requested && valid(session, generation))
            manualOutcome(row, result.error && !(result.hint && result.verifiedRaw && result.error === 'NO_MATCH')
              ? fail(result.error) : null);
          if (found) return;
        }
      }
      if (session.target.kind !== 'source' &&
          !(session.target.kind === 'group' && session.target.source && !session.reselecting)) {
        session.stage = session.target.metadataProvider ? 'searching' : 'loading'; publish(session);
        // Already-returned search pages are shared input, not an instruction to
        // fetch every detail before history and manual matching get a chance.
        for (const result of session.results) {
          if (!valid(session, generation)) return;
          if (await consume(result)) return;
        }
        for (const source of session.sources) {
          if (!(session.target.metadataProvider ? smartSourceAllowed(session, source)
            : sourceAllowed(session, source))) continue;
          const state = session.search.get(source.key);
          while (valid(session, generation) && !state.completed && !state.error) {
            if (!await runIO(session, generation, source, null, () => searchPage(session, source)) ||
                !valid(session, generation)) break;
            for (const result of session.results) {
              if (!valid(session, generation)) return;
              if (result.source.key !== source.key) continue;
              if (await consume(result)) return;
            }
          }
        }
      } else for (const result of session.results) {
        if (!valid(session, generation)) return;
        if (await consume(result)) return;
      }
      if (!valid(session, generation)) return;
      // The action-specific preference gate is relaxed only after full discovery,
      // never merely because one lower-quality candidate happened to arrive first.
      session.discoveryComplete = session.sources.filter(source => session.target.metadataProvider
        ? smartSourceAllowed(session, source) : sourceAllowed(session, source))
        .every(source => session.search.get(source.key).completed) &&
        (await Promise.all(session.results.map(async result =>
          (session.target.metadataProvider ? smartSourceAllowed(session, result.source) &&
            await smartResult(session, result) : sourceAllowed(session, result.source)) ? result : null))).filter(Boolean)
          .every(result => result.state === 'ready' && !result.error);
      if (session.discoveryComplete && await tryCandidate(session, generation)) return;
      if (!valid(session, generation)) return;
      const donor = session.current;
      if (donor && !donor.resourceReleased && session.reselecting?.id === donor.candidate.id &&
          sameActualEpisode(donor.target, session.target)) {
        session.target = targetForCandidate(session.target, donor.candidate);
      }
      session.stage = 'unavailable';
      session.error = selectionError(session,
        [...session.search.values()].find(value => value.error)?.error ?? 'NO_MATCH');
      publish(session);
    } catch (error) {
      if (valid(session, generation)) {
        session.stage = 'error'; session.error = selectionError(session, codeOf(error)); publish(session);
      }
    }
  }
  function selectionError(session, fallback) {
    return session.feedbackFailure?.code ?? session.selectionError ?? fallback;
  }
  function start(session, { prepareOnly = false, constraint = { action: 'default' }, explicitCandidateId = null,
    reselecting = null, feedbackFailure = null } = {}) {
    const requestKey = identity(session.target, constraint, explicitCandidateId, reselecting?.id);
    if (session.runKey === requestKey && session.runWork && !session.stopped) {
      session.prepareOnly = prepareOnly;
      return session.runWork;
    }
    const previous = session.pending;
    void cancelCalculations(session, 'playback');
    session.generation++; session.stopped = false; session.prepareOnly = prepareOnly;
    session.pending = null; session.explicitCandidateId = explicitCandidateId;
    session.reselecting = reselecting;
    session.feedbackFailure = feedbackFailure; session.selectionError = null;
    session.constraint = clone(constraint); session.actionCurrent = session.current?.candidate ?? session.actionCurrent;
    session.error = null; session.stage = session.target.metadataProvider ? 'cached' : 'loading'; publish(session);
    if (previous) detached(session, retire(session, previous));
    const work = tracked(run(session, session.generation));
    session.runKey = requestKey; session.runWork = work; session.runGeneration = session.generation;
    void work.finally(() => { if (session.runWork === work) session.runWork = null; }).catch(() => {});
    return work;
  }
  function mergeSeason(target, metadata, season, value) {
    const key = target.metadataProvider === 'douban' ? 'doubanSeasons' : 'seasons';
    const rows = [...(metadata?.[key] ?? [])];
    const index = rows.findIndex(row => (row.season_number ?? row.season) === season);
    const row = { ...(index < 0 ? {} : rows[index]), ...value,
      [key === 'seasons' ? 'season_number' : 'season']: season };
    if (key === 'doubanSeasons' && !Object.hasOwn(value, 'episodes')) delete row.episodes;
    if (index >= 0 && isDeepStrictEqual(rows[index], row)) return metadata;
    if (index < 0) rows.push(row);
    else rows[index] = row;
    rows.sort((a, b) => (a.season_number ?? a.season) - (b.season_number ?? b.season));
    return { ...metadata, [key]: rows };
  }
  function metadataCacheState(session, id = session.metadataSourceId) {
    const view = session.metadataViews.get(id);
    const base = clone(view?.cache ?? null);
    if (base && view.expiresAt <= now()) base.status = 'stale';
    const season = view?.seasonCache;
    if (!season) return base;
    const error = base?.error ?? season.cache.error;
    return { ...clone(base ?? season.cache),
      status: base?.status === 'stale' || season.cache.status === 'stale' || season.expiresAt <= now() ? 'stale' : 'fresh',
      ...(error ? { error: clone(error) } : {}),
      components: { ...clone(base?.components ?? {}),
        [`season:${season.season}`]: { status: season.cache.error ? 'failed' : 'ready',
          fetchedAt: season.fetchedAt, ...(season.cache.error ? { error: clone(season.cache.error) } : {}) } } };
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
  const metadataIdentity = row => identity(row.provider, row.kind, String(row.metadataId));
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
    const target = { ...session.target, kind: selected.kind,
      metadataProvider: selected.provider, metadataId: selected.metadataId };
    let entry;
    try {
      entry = await data.targetMetadataEntry(target, { rules: session.rules,
        canSchedule: () => valid(session, generation) });
    } catch (error) {
      if (valid(session, generation) && selected.id === session.metadataSourceId)
        session.metadataError = codeOf(error);
      throw error;
    }
    if (!valid(session, generation)) return;
    selected.metadata = entry.value;
    const display = metadataDisplay(session, selected);
    acceptMetadataCache(session, entry, undefined, undefined, selected.id, display);
    if (selected.id === session.metadataSourceId) session.metadata = display;
    requireFreshMetadata(session);
  }
  async function refreshPlaybackSeason(session, season, requestedEpisode) {
    const generation = session.generation, target = session.target;
    const selected = playbackMetadataSource(session);
    requireValue(selected, 'NOT_FOUND');
    const admitted = () => valid(session, generation) && session.target === target;
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
      publish(session);
    } catch (error) {
      if (admitted() && selected.id === session.metadataSourceId) session.metadataError = codeOf(error);
      throw error;
    }
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
    return selected ? { ...fields(selected, ['provider', 'metadataId', 'kind']),
      metadata: catalog.metadata(selected.provider, `/3/${selected.kind}/${selected.metadataId}`, selected.metadata) } : null;
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
        const value = await data.seasonMetadataEntry(referenceTarget, number, { rules: session.rules,
          canSchedule: options.canSchedule, refresh: options.refresh });
        if (!options.canSchedule()) return { entry, display };
        supplement = mergeSeason(referenceTarget, supplement, number, value.value);
        error ??= value.cache.error?.code ?? (value.cache.status === 'stale' ? 'STALE_METADATA' : undefined);
      } catch (caught) {
        if (!options.canSchedule()) return { entry, display };
        error ??= codeOf(caught);
      }
    }
    const episodes = matchEpisodeMaps(display, supplement).filter(row => row.episode.season === season)
      .map(({ episode, match }) => ({ ...(match ? fields(match,
        ['name', 'overview', 'still_path', 'runtime', 'air_date']) : {}), ...episode }));
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
      if (primary.length === 1) target = { ...target, kind: primary[0].kind,
        metadataProvider: 'tmdb', metadataId: primary[0].metadataId };
    }
    if (!target.metadataProvider) target = session.searchMetadataTarget ?? target;
    const epoch = session.metadataEpoch;
    const admitted = () => !session.released && !session.stopped && !closed &&
      epoch === session.metadataEpoch;
    try {
      const rows = await data.metadataSources(target, {
        rules: session.rules, canSchedule: admitted });
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
        sources.find(row => row.provider === preferences().metadataViewProvider) ??
        sources.find(row => row.provider === 'tmdb') ?? sources[0];
      session.metadataViewInitialized = true;
      const changed = selected && selected.id !== session.metadataSourceId;
      if (!selected) {
        session.metadata = null; session.metadataSourceId = null;
      } else if (changed) {
        await browse(session, session.browsing.sourceId, session.browsing.lineId,
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
          const value = await seasonDisplay(session, { ...session.target,
            kind: selected.kind, metadataProvider: selected.provider, metadataId: selected.metadataId },
            display, season, { canSchedule: displaying });
          if (!displaying()) return;
          session.metadata = value.display;
          acceptMetadataCache(session, value.entry, season, value.error);
        } else if (displaying()) session.metadata = display;
      }
      if (!session.restoreOnly && session.target.kind === 'tv' &&
          session.target.episode === undefined && session.generation === 0 && session.selectionEpoch === 0) {
        const previous = session.platform === 'windows'
          ? await library.history(session.target.id) : null;
        initializeEpisode(session, previous);
        if (session.target.episode !== undefined) {
          session.stage = 'idle'; session.error = null;
          if (session.platform === 'windows' || session.platform === 'ios' && preferences().iosAutoPrepare)
            void start(session, { prepareOnly: session.platform === 'ios' });
        } else if (session.target.globalEpisode !== undefined) {
          session.stage = 'unavailable'; session.error = 'NO_EPISODE_MAPPING';
        }
      }
    } catch (error) {
      if (!admitted()) return;
      session.metadataError = codeOf(error);
    }
    publish(session);
  }
  async function browseMetadata(session, id, keepSource = false, context = {}) {
    const selected = session.metadataSources.find(row => row.id === id);
    requireValue(selected, 'NOT_FOUND');
    rememberBrowsing(session);
    const view = session.metadataViews.get(id);
    const saved = keepSource ? session.browsing : view?.browsing ??
      (view?.loadedSeasons.size ? { sourceId: null, lineId: null, loading: false, error: null,
        lines: [], episodes: [] } : undefined);
    const selectedSeason = context.season ?? saved?.season ??
      (id === session.metadataSourceId ? session.browsing.season : undefined);
    const generation = context.generation ?? ++session.browseGeneration;
    const epoch = session.metadataEpoch;
    const admitted = () => !session.released && !closed &&
      generation === session.browseGeneration && epoch === session.metadataEpoch;
    const previousId = session.metadataSourceId, previousMetadata = session.metadata;
    session.metadataSourceId = id;
    session.metadata = view?.display ?? selected.metadata ?? null;
    session.browsing = saved ? { ...saved, loading: true, refreshing: true, error: null }
      : { sourceId: null, lineId: null, loading: true, error: null, lines: [], episodes: [] };
    publish(session);
    let loadedMetadata;
    try {
      const target = { ...session.target, kind: selected.kind,
        metadataProvider: selected.provider, metadataId: selected.metadataId };
      delete target.members; delete target.source;
      const knownSeasons = (view?.display ?? selected.metadata)?.doubanSeasons ??
        (view?.display ?? selected.metadata)?.seasons ?? selected.seasons ?? [];
      const entry = !context.refresh && object(selected.metadata) && (context.season === undefined ||
          knownSeasons.some(row => (row.season_number ?? row.season) === context.season)) ? null
        : await data.targetMetadataEntry(target, { rules: session.rules, canSchedule: admitted,
          refresh: context.refresh === true });
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
      if (context.complete !== false) await refreshBrowsing(session);
      if (!admitted()) return;
      session.browsing = { ...session.browsing, loading: context.complete === false, refreshing: false };
      if (context.complete !== false) rememberBrowsing(session);
      publish(session);
      return true;
    } catch (error) {
      if (!admitted()) return;
      if (saved) {
        if (loadedMetadata && id === session.metadataSourceId) {
          session.metadata = loadedMetadata.display;
          if (loadedMetadata.entry) {
            selected.metadata = loadedMetadata.entry.value;
            acceptMetadataCache(session, loadedMetadata.entry);
          }
          session.browsing = { ...session.browsing, ...fields(loadedMetadata, ['season']) };
        }
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: codeOf(error) };
        session.metadataError = codeOf(error);
      } else session.browsing = { ...session.browsing, loading: false, error: codeOf(error), episodes: [] };
    }
    publish(session);
    return false;
  }
  async function restoreReads(session) {
    const selected = session.restoreBrowsing;
    const active = () => !session.released && !session.stopped && !closed;
    try {
      if (session.metadataDiscovery) await session.metadataDiscovery;
      if (!active() || session.generation !== 0) return;
      if (selected?.season !== undefined)
        session.browsing = { ...session.browsing, season: selected.season };
      if (selected?.metadataSourceId && session.metadataSources.some(row => row.id === selected.metadataSourceId)) {
        const generation = session.browseGeneration;
        await browseMetadata(session, selected.metadataSourceId, true);
        if (!active() || session.browseGeneration !== generation + 1) return;
      } else if (selected?.season !== undefined && session.target.metadataProvider) {
        await refreshMetadataDisplay(session, { season: selected.season });
        if (!active()) return;
      }
      const sourceId = selected && Object.hasOwn(selected, 'sourceId')
        ? selected.sourceId : session.browsing.sourceId;
      await browse(session, sourceId, selected?.lineId ?? session.browsing.lineId ?? undefined);
      if (session.browsing.error) throw fail(session.browsing.error);
      if (session.target.kind === 'group' && !session.target.members.length) {
        session.stage = 'unavailable'; session.error = session.seedWaitGroup ? null : 'SOURCE_NOT_FOUND';
      }
    } catch (error) {
      if (active()) { session.stage = 'error'; session.error = codeOf(error); }
    } finally {
      session.restoring = false;
      delete session.restoreBrowsing;
      if (active()) publish(session);
    }
  }
  async function initialize(session, cacheEntries, { refresh = false } = {}) {
    const epoch = session.metadataEpoch, browseGeneration = session.browseGeneration;
    const admitted = () => !session.released && !session.stopped && !closed && session.metadataEpoch === epoch &&
      groupScopeAvailable(session);
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
        const row = { provider: displayTarget.metadataProvider, metadataId: displayTarget.metadataId,
          kind: displayTarget.kind, title: displayTarget.title, metadata };
        session.metadataSourceId = metadataIdentity(row);
        session.metadataSources = [{ ...row, id: session.metadataSourceId }];
        session.playbackMetadataSourceId = session.metadataSourceId;
        session.metadata = metadataDisplay(session, row);
        acceptMetadataCache(session, entry, undefined, displayError);
      }
      const previous = !session.restoreOnly && session.platform === 'windows'
        ? await library.history(session.target.id) : null;
      if (session.target.kind === 'tv') {
        initializeEpisode(session, session.generation === 0 ? previous : null);
        if (session.target.metadataProvider === 'tmdb' && session.target.season !== undefined) {
          const target = session.target;
          const season = await data.seasonMetadataEntry(target, target.season, { rules: session.rules, canSchedule: admitted, refresh });
          if (!admitted()) return;
          const id = session.playbackMetadataSourceId;
          const display = mergeSeason(target, playbackMetadata(session), target.season, season.value);
          acceptMetadataCache(session, season, target.season, undefined, id, display);
          if (id === session.metadataSourceId) session.metadata = display;
        }
      } else if (previous) {
        if (!['source', 'group'].includes(session.target.kind) || session.target.source?.playId === undefined &&
            session.target.source?.episodeId === undefined &&
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
          await browse(session, session.browsing.sourceId, session.browsing.lineId ?? undefined);
        else session.browsing = { ...session.browsing, loading: false,
          ...(session.target.season === undefined ? {} : { season: session.target.season }) };
      }
      if (session.generation === 0 || session.stage === 'loading') {
        session.stage = 'idle'; publish(session);
      }
      // Complete the initial directory before selecting its independent view.
      // Association discovery still does not delay automatic preparation.
      if (displayTarget.metadataProvider) {
        session.metadataDiscovery = discoverMetadata(session, displayTarget);
        detached(session, session.metadataDiscovery);
      }
      if (session.generation !== 0 || session.selectionEpoch !== 0) return;
      if (session.restoreOnly) {
        await restoreReads(session);
        return;
      }
      if (session.target.kind === 'group' && !session.target.members.length) {
        session.stage = 'unavailable'; session.error = session.seedWaitGroup ? null : 'SOURCE_NOT_FOUND'; publish(session); return;
      }
      if (session.target.kind === 'source' && !session.browsing.loading) {
        if (session.browsing.error) {
          session.stage = 'error'; session.error = session.browsing.error; publish(session); return;
        }
        if (!session.browsing.episodes.length) {
          session.stage = 'unavailable'; session.error = 'MEDIA_UNAVAILABLE'; publish(session); return;
        }
      }
      if (session.target.metadataProvider && metadataCacheState(session)?.status === 'stale') return;
      if (session.target.metadataProvider && !session.sources.some(source => source.enabled !== false) &&
          !library.read().manual.some(row => row.targetId === session.target.id && row.enabled !== false && row.share)) {
        session.stage = 'unavailable'; session.error = 'MEDIA_UNAVAILABLE'; publish(session); return;
      }
      const autoPrepare = session.platform === 'ios' && !!session.target.metadataProvider && preferences().iosAutoPrepare;
      // An explicit global coordinate is not an instruction to fall back to
      // the first episode or another episode's historical position.
      if (session.target.kind === 'tv' && session.target.episode === undefined &&
          (session.target.globalEpisode !== undefined || session.platform === 'windows' || autoPrepare)) {
        session.stage = 'unavailable'; session.error = 'NO_EPISODE_MAPPING'; publish(session); return;
      }
      if (session.platform === 'windows' || autoPrepare) {
        detached(session, playSelection(session, autoPrepare));
      }
    } catch (error) {
      if (admitted()) {
        session.restoring = false;
        session.initializationError = codeOf(error);
        if (session.browseGeneration === browseGeneration)
          session.browsing = { ...session.browsing, loading: false, error: codeOf(error) };
        if (session.entryResolution === 'loading') session.entryResolution = 'error';
        if (session.generation === 0 || session.stage === 'loading') {
          session.stage = 'error'; session.error = codeOf(error); publish(session);
        }
      }
    } finally {
      if (session.restoring) {
        session.restoring = false;
        publish(session);
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
    requireValue(!closed, 'CLOSED');
    requireValue(object(input) && ['ios', 'windows', 'test'].includes(input.platform));
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
      ? checkTarget({ id: `group:${entryTarget.id}`, kind: 'group',
        title: entryTarget.title, ...fields(entryTarget, ['year']), members: [] }) : resumeTarget ?? entryTarget;
    const sources = (input.sources ?? []).map(checkSource);
    requireValue(new Set(sources.map(source => source.key)).size === sources.length);
    if (target.kind === 'group') requireValue((createsSeedWaitGroup || target.members.length > 0) && target.members.every(member =>
      sources.some(source => source.key === member.sourceKey && source.enabled !== false)), 'SOURCE_NOT_FOUND');
    const selectionMembers = doubanSeed ? undefined : checkSearchHandoff(input, searchHandoff, entryTarget.id, sources);
    // The entry content and exact raw scope outlive changes of playback target.
    const capturedRules = ruleSnapshot ?? await rules.snapshot();
    requireValue(!closed, 'CLOSED');
    const session = { id: randomUUID(), target, seedWaitGroup: createsSeedWaitGroup,
      entryTarget: clone(entryTarget), entrySurface: input.entrySurface,
      entryResolution: doubanSeed ? (resumeTarget ? (resumeTarget.metadataProvider === 'tmdb' ? 'matched' : 'unmatched')
        : 'loading') : null, entryMetadata: clone(input.metadata ?? null),
      platform: input.platform, restoreOnly: input.restoreOnly === true,
      restoring: input.restoreOnly === true, restoreBrowsing: clone(input.browsing),
      query: doubanSeed ? target.title : text(input.query) || target.title,
      selectionId: entryTarget.id, selectionKind: doubanSeed ? resumeTarget?.kind ?? entryTarget.kind : entryTarget.kind,
      selectionSource: entryTarget.kind === 'source' ? clone(entryTarget.source)
        : target.kind === 'source' ? clone(target.source) : null,
      searchRevision: -1, searchUpdateEpoch: 0,
      sourceScope: sources.map(source => clone(source)),
      userSelected: target.kind === 'group' && ['candidateId', 'playId', 'episodeId']
        .some(field => target.source?.[field] !== undefined),
      sources, metadata: keepSeedDisplayMetadata || !resumeTarget ||
        !target.metadataProvider && entryTarget.metadataProvider === 'tmdb' ? clone(input.metadata ?? null) : null,
      rules: capturedRules,
      calculations: new Map(), metadataViews: new Map(), candidateEpoch: 0,
      metadataSources: [], metadataSourceId: null, playbackMetadataSourceId: null, metadataError: null,
      metadataViewInitialized: !!input.browsing?.metadataSourceId,
      lastFailed: null,
      initializationError: null,
      results: [], resultKeys: new Set(), candidates: new Map(), excluded: new Set(), listeners: new Set(),
      search: new Map(sources.map(source => [source.key, { sourceKey: source.key, page: 0, completed: false,
        loading: false, error: null, seen: new Set() }])),
      sequence: 0, generation: 0, selectionEpoch: 0, metadataEpoch: 0, browseGeneration: 0, searchGeneration: 0,
      stage: 'loading', error: null, reportError: null,
      browsing: { sourceId: null, lineId: null, loading: true, error: null, lines: [], episodes: [] },
      stopped: false, released: false, pending: null, current: null, constraint: { action: 'default' },
      prepareOnly: false, resumePosition: 0, position: 0, duration: 0, paused: false,
      shortVideo: false, previewEpisodes: (library.read().previewTargets ?? []).includes(target.id),
      lastHistoryWrite: 0, discoveryComplete: false,
      prefetch: new Map(), leases: new Map(), prefetchGeneration: 0, reportQueue: Promise.resolve() };
    for (const result of doubanSeed && !resumeTarget ? [] : input.results ?? []) {
      const source = sources.find(value => value.key === result.sourceKey);
      if (source) await addResults(session, source, result.items ?? [], 'result', undefined, selectionMembers);
    }
    for (const result of doubanSeed && !resumeTarget ? [] : input.resultHints ?? []) {
      const source = sources.find(value => value.key === result.sourceKey);
      if (source) await addResults(session, source, result.items ?? [], 'hint', undefined, selectionMembers);
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
        value.targetId === target.id && value.share && value.enabled !== false);
      requireValue(row && target.source.key === `manual:${row.id}`, 'SOURCE_NOT_FOUND');
      const source = { key: target.source.key, engine: 'pan', name: row.name || '人工匹配', detailId: row.id };
      await addResults(session, source, [{ id: row.id, title: row.title || target.title }], 'linked');
      Object.assign(session.results.find(value => value.source.key === source.key),
        { manualId: row.id, manualIdentity: manualIdentity(row), linkedTargetId: target.id });
    } else if (target.kind === 'source') {
      const source = sources.find(value => value.key === target.source.key);
      requireValue(source, 'SOURCE_NOT_FOUND');
      await addResults(session, source, [{ id: target.source.detailId, title: target.title }], 'linked');
    } else if (target.members?.length) {
      for (const member of target.members) {
        const source = sources.find(value => value.key === member.sourceKey);
        requireValue(source, 'SOURCE_NOT_FOUND');
        await addResults(session, source, [{ detailId: member.detailId }], 'linked');
      }
    }
    if (!target.metadataProvider) {
      const selected = target.source ? session.results.find(row => row.source.key === target.source.key &&
        row.source.detailId === target.source.detailId) : session.results[0];
      if (selected) session.browsing = { ...session.browsing, sourceId: selected.id,
        lineId: target.source?.lineId ?? null };
    }
    if (input.browsing?.source) {
      const pointer = input.browsing.source;
      const source = pointer.engine === 'pan' ? session.results.find(row =>
        row.source.engine === 'pan' && row.source.key === pointer.key && row.source.detailId === pointer.detailId)?.source
        : sources.find(value => value.key === pointer.key && (pointer.installationId === undefined ||
          value.engine === pointer.engine && value.installationId === pointer.installationId && value.siteId === pointer.siteId));
      requireValue(source, 'SOURCE_NOT_FOUND');
      await addResults(session, source, [{ id: pointer.detailId, title: target.title }], 'linked');
      session.restoreBrowsing.sourceId = identity(source.key, pointer.detailId);
    }
    requireValue(input.cacheEntries === undefined || Array.isArray(input.cacheEntries));
    requireValue(!closed, 'CLOSED');
    sessions.set(session.id, session); publish(session);
    session.initialization = tracked(initialize(session, clone(input.cacheEntries ?? [])));
    return snapshot(session);
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
    requireValue(groupScopeAvailable(session), 'SOURCE_NOT_FOUND');
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
        const source = checkSource(entry.source), allowed = inScope(source.key);
        requireValue(allowed && sourceRevision(allowed) === sourceRevision(source), 'STALE_SOURCE');
      }
    }
    const selectionMembers = checkSearchHandoff(input, searchHandoff, session.selectionId, sources);
    let carrier = session.searchMetadataTarget;
    if (input.metadataTarget !== undefined) {
      requireValue(session.selectionKind !== 'source' && object(input.metadataTarget) &&
        ['movie', 'tv'].includes(input.metadataTarget.kind) && ['tmdb', 'douban'].includes(input.metadataTarget.metadataProvider));
      carrier = checkTarget({ ...fields(input.metadataTarget, ['kind', 'title', 'year', 'metadataProvider', 'metadataId']),
        id: session.selectionId, title: text(input.metadataTarget.title) || session.target.title });
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
    const epoch = ++session.searchUpdateEpoch, metadataEpoch = session.metadataEpoch;
    const admitted = () => !closed && !session.released && !session.stopped && session.searchUpdateEpoch === epoch &&
      session.metadataEpoch === metadataEpoch;
    const trustedGroup = session.target.kind === 'group' && selectionMembers !== undefined;
    // Only the Node search owner proves these exact members, even when a late
    // metadata carrier replaces the selected card's group-shaped target.
    if (trustedGroup) for (const member of selectionMembers) addMember(session, member.sourceKey, member.detailId);
    for (const result of input.results) {
      await addResults(session, inScope(result.sourceKey), result.items, 'result', admitted, selectionMembers);
      if (!admitted()) return;
    }
    for (const result of input.resultHints ?? []) {
      await addResults(session, inScope(result.sourceKey), result.items, 'hint', admitted, selectionMembers);
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
    publish(session);
    const seeds = entries.filter(entry => session.selectionKind !== 'source' ||
      entry.kind === 'detail' && entry.source.key === session.selectionSource.key &&
        entry.detailId === session.selectionSource.detailId);
    detached(session, (async () => {
      try {
        await session.initialization;
        if (!admitted()) return;
        if (seeds.length) await data.handoff(clone(seeds), { canSchedule: admitted });
        if (!admitted() || !carrier) return;
        const entry = await data.targetMetadataEntry(carrier, { rules: session.rules, canSchedule: admitted });
        if (!admitted()) return;
        const value = entry.value;
        const metadataTarget = { ...carrier, title: text(value?.title ?? value?.name) || carrier.title };
        const row = { provider: carrier.metadataProvider, metadataId: carrier.metadataId, kind: carrier.kind,
          title: metadataTarget.title, metadata: value };
        row.id = metadataIdentity(row);
        session.metadataSources = [...session.metadataSources.filter(item => item.id !== row.id), row];
        const display = metadataDisplay(session, row);
        if (session.restoreOnly || session.target.kind !== 'group' ||
            session.pending || session.current || session.userSelected) {
          // A proven late association supplies display data, not authority to
          // reinterpret the playing raw file as a particular metadata episode.
          if (['source', 'group'].includes(session.target.kind) &&
              (session.metadataSourceId === null || row.id === session.metadataSourceId)) {
            session.metadata = display; session.metadataSourceId = row.id;
            acceptMetadataCache(session, entry);
          } else if (session.metadataSourceId === null || row.id === session.metadataSourceId) {
            let updated = display;
            let seasonEntry;
            const season = session.browsing.season ?? session.target.season;
            if (carrier.kind === 'tv' && carrier.metadataProvider === 'tmdb' && season !== undefined) {
              const generation = session.browseGeneration;
              const details = await data.seasonMetadataEntry(carrier, season, { rules: session.rules,
                canSchedule: () => admitted() && generation === session.browseGeneration });
              if (!admitted() || generation !== session.browseGeneration) return;
              updated = mergeSeason(carrier, display, season, details.value);
              seasonEntry = details;
            }
            session.metadata = updated; session.metadataSourceId = row.id; session.initializationError = null;
            acceptMetadataCache(session, entry);
            if (seasonEntry) acceptMetadataCache(session, seasonEntry, season);
          }
          publish(session); return;
        }
        session.generation++; session.runKey = null; session.metadataEpoch++;
        session.metadata = display; session.metadataSourceId = row.id; session.initializationError = null;
        session.playbackMetadataSourceId = row.id;
        acceptMetadataCache(session, entry);
        const first = carrier.kind === 'tv' ? resolveEpisode(episodeMap(display), { globalEpisode: 1 }) : null;
        if (!await selectTarget(session, episodeTarget(metadataTarget, first ?? {}))) return;
        session.browsing = { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
        session.stage = 'idle'; session.error = null; publish(session);
        detached(session, discoverMetadata(session));
        const autoPrepare = session.platform === 'ios' && preferences().iosAutoPrepare;
        if (session.platform === 'windows' || autoPrepare) {
          if (carrier.kind === 'tv' && !first) {
            session.stage = 'unavailable'; session.error = 'NO_EPISODE_MAPPING'; publish(session);
          } else void start(session, { prepareOnly: autoPrepare });
        }
      } catch (error) {
        if (admitted()) { session.metadataError = codeOf(error); publish(session); }
      }
    })());
  }
  function replaceLine(result, detail, index, line) {
    if (result.detail !== detail) return result.detail;
    if (detail.lines[index] === line || isDeepStrictEqual(detail.lines[index], line)) return detail;
    const lines = [...detail.lines];
    lines[index] = line;
    return result.detail = { ...detail, lines };
  }
  function rememberBrowsing(session) {
    const browsing = session.browsing;
    if (browsing.error || browsing.loading && !browsing.refreshing) return;
    if (browsing.sourceId !== null) {
      const result = session.results.find(row => row.id === browsing.sourceId);
      if (result) result.browsing = browsing;
    } else {
      const view = session.metadataViews.get(session.metadataSourceId);
      if (view) view.browsing = browsing;
    }
  }
  function browseEpisodes(result, line, recognized = result.recognized?.candidates, parent) {
    if (!line) return [];
    result.directories ??= new Map();
    const previous = result.directories.get(line.id);
    if (previous?.line === line && previous.recognized === recognized) return previous.episodes;
    const candidates = new Map((recognized ?? []).filter(item => item.lineId === line.id)
      .map(item => [item.episodeId, item]));
    const episodes = line.episodes.filter(value => !value.share || value.file).map(value => {
      const candidate = candidates.get(value.id);
      return { id: candidate?.id ?? identity(result.id, line.id, value.id), ...fields(value, ['name', 'path', 'index']),
        ...(candidate ? { candidateId: candidate.id, ...fields(candidate,
          ['season', 'episode', 'globalEpisode', 'quality', 'resolution', 'fps', 'codec']),
          ...(parent ? catalog.candidate(parent, candidate) : {}) } : {}) };
    });
    result.directories.set(line.id, { line, recognized, episodes });
    return episodes;
  }
  async function directoryProjection(session, result, line, generation) {
    if (!line) return [];
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    const metadata = selected ? session.metadata : null;
    const parent = { id: session.selectionId, title: session.entryTarget.title };
    const target = selected ? { ...parent, kind: selected.kind,
      metadataProvider: selected.provider, metadataId: selected.metadataId } : session.entryTarget;
    const previous = result.directories?.get(line.id);
    if (previous?.line === line && previous.metadata === metadata && previous.variants === result.variants)
      return previous.episodes;
    const candidates = await recognizeResult(session, result, result.detail, target, metadata, 'session');
    if (closed || session.released || generation !== session.browseGeneration ||
        metadata !== (selected ? session.metadata : null) || !currentSource(session, result.source)) return null;
    const episodes = browseEpisodes(result, line, candidates, parent);
    Object.assign(result.directories.get(line.id), { metadata, variants: result.variants });
    return episodes;
  }
  async function refreshBrowsing(session) {
    const browsing = session.browsing, generation = session.browseGeneration;
    const result = session.results.find(row => row.id === browsing.sourceId);
    if (!result?.detail) return;
    const metadata = session.metadata;
    const line = result.detail.lines.find(row => row.id === browsing.lineId);
    const episodes = await directoryProjection(session, result, line, generation);
    if (closed || session.released || session.browsing !== browsing || generation !== session.browseGeneration ||
        metadata !== session.metadata || episodes === null || !currentSource(session, result.source)) return;
    if (browsing.episodes !== episodes) session.browsing = { ...browsing, episodes };
    rememberBrowsing(session);
  }
  async function browse(session, resultId, lineId, {
    refresh = false, retainDisplay = false, metadataSourceId, season,
  } = {}) {
    rememberBrowsing(session);
    const generation = ++session.browseGeneration;
    lineId ??= undefined;
    if (!resultId) {
      if (metadataSourceId === null) {
        session.metadataSourceId = null; session.metadata = null;
      } else if (metadataSourceId !== undefined || season !== undefined || refresh) {
        const id = metadataSourceId ?? session.metadataSourceId;
        requireValue(id, 'NOT_FOUND');
        await browseMetadata(session, id, false, { generation, season, refresh });
        return;
      }
      const saved = session.metadataViews.get(session.metadataSourceId)?.browsing;
      session.browsing = saved ? { ...saved, loading: false, refreshing: false, error: null }
        : { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
      publish(session);
      return;
    }
    const result = session.results.find(value => value.id === resultId);
    requireValue(result, 'NOT_FOUND');
    const admitted = () => !session.released && !closed && generation === session.browseGeneration &&
      currentSource(session, result.source);
    const previous = session.browsing;
    const previousMetadataSourceId = session.metadataSourceId;
    const saved = previous.sourceId === resultId ? previous : result.browsing;
    lineId ??= saved?.lineId ?? undefined;
    const knownLine = result.detail?.lines.find(row => row.id === lineId) ??
      (lineId === undefined ? result.detail?.lines[0] : undefined);
    const retained = !!knownLine && result.directories?.has(knownLine.id) ||
      retainDisplay && previous.sourceId === resultId &&
      (lineId === undefined || lineId === previous.lineId);
    const priorDetail = retained ? result.detail : null;
    const display = knownLine ? { sourceId: resultId, lineId: knownLine.id, loading: false, error: null,
      lines: result.detail.lines.map(value => fields(value, ['id', 'label', 'provider'])),
      episodes: retained ? result.directories?.get(knownLine.id)?.episodes ?? [] : [],
      ...((previous.season ?? saved?.season) === undefined ? {} : { season: previous.season ?? saved.season }) } : saved;
    session.browsing = retained ? { ...(display ?? previous), loading: true, refreshing: true, error: null }
      : { sourceId: resultId, lineId: lineId ?? null, loading: true,
        error: null, lines: [], episodes: [] };
    if (season !== undefined) session.browsing.season = season;
    publish(session);
    try {
      if (metadataSourceId === null) {
        session.metadataSourceId = null; session.metadata = null;
      } else if (metadataSourceId !== undefined && metadataSourceId !== previousMetadataSourceId ||
          season !== undefined && season !== previous.season) {
        const id = metadataSourceId ?? session.metadataSourceId;
        requireValue(id, 'NOT_FOUND');
        const loaded = await browseMetadata(session, id, true, { generation, season, complete: false });
        if (!admitted()) return;
        requireValue(loaded, session.browsing.error ?? session.metadataError ?? 'UPSTREAM_ERROR');
      }
      let detail;
      if (result.source.engine === 'pan') {
        const row = currentManual(session, result);
        requireValue(row?.share, 'NOT_FOUND');
        if (!refresh && result.detail) detail = result.detail;
        else {
          const line = { id: row.id, label: row.name || row.provider, provider: row.provider,
            episodes: [{ id: row.id, name: row.name || session.target.title,
              share: { ...row.share, provider: row.provider } }] };
          detail = { lines: [line], detailId: row.id };
          result.baseDetail = detail; result.detail = detail;
        }
      } else detail = !refresh && result.detail ? result.detail
        : await loadDetail(session, result, generation, true, refresh);
      if (!admitted()) return;
      if (!detail) throw fail(result.error ?? 'UPSTREAM_ERROR');
      let line = lineId === undefined ? detail.lines[0] : detail.lines.find(value => value.id === lineId);
      if (lineId !== undefined) requireValue(line, 'NOT_FOUND');
      session.browsing = { ...session.browsing, lineId: line?.id ?? null,
        lines: detail.lines.map(value => fields(value, ['id', 'label', 'provider'])) };
      publish(session);
      if (line) {
        const cached = result.directories?.get(line.id);
        if (refresh || cached?.line !== line) line = await data.expand(result.source,
          result.baseDetail?.lines.find(value => value.id === line.id) ?? line, { canSchedule: admitted, refresh });
        if (!admitted()) return;
        detail = replaceLine(result, detail, detail.lines.findIndex(value => value.id === line.id), line);
        line = detail.lines.find(value => value.id === line.id);
        requireValue(line, 'NOT_FOUND');
      }
      if (!admitted()) return;
      const episodes = await directoryProjection(session, result, line, generation);
      if (!admitted() || episodes === null) return;
      result.state = 'ready'; result.error = null;
      session.browsing = { sourceId: resultId, lineId: line?.id ?? null, loading: false, error: null,
        ...(session.browsing.season === undefined ? {} : { season: session.browsing.season }),
        lines: detail.lines.map(value => fields(value, ['id', 'label', 'provider'])),
        episodes };
      rememberBrowsing(session);
    } catch (error) {
      if (!admitted()) return;
      if (retained && result.detail === priorDetail) {
        session.browsing = { ...(display ?? previous), loading: false, refreshing: false, error: codeOf(error) };
      } else {
        session.browsing = { ...session.browsing, loading: false, refreshing: false,
          error: codeOf(error),
          lines: (result.detail?.lines ?? []).map(value => fields(value, ['id', 'label', 'provider'])),
          episodes: [] };
      }
    }
    publish(session);
  }
  async function refreshMetadataDisplay(session, { refresh = false, season, requestedEpisode } = {}) {
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    const target = selected ? { ...session.target, kind: selected.kind,
      metadataProvider: selected.provider, metadataId: selected.metadataId } : session.target;
    if (!target.metadataProvider) return;
    const generation = ++session.browseGeneration, epoch = session.metadataEpoch;
    const admitted = () => !closed && !session.released &&
      session.browseGeneration === generation && session.metadataEpoch === epoch;
    const previous = session.browsing;
    session.browsing = { ...previous, loading: true, refreshing: true, error: null };
    publish(session);
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
      session.browsing = { ...previous, ...fields({ season: requestedSeason }, ['season']),
        loading: false, refreshing: false, error: null };
      rememberBrowsing(session);
      session.initializationError = null;
      if (session.target.metadataProvider) detached(session, discoverMetadata(session));
      // A data refresh updates presentation only. An already accepted file,
      // its local/global coordinates and its native lease keep their identity.
    } catch (error) {
      if (!admitted()) return;
      session.metadataError = failure = codeOf(error);
      session.browsing = { ...previous, loading: false, refreshing: false, error: failure };
    }
    publish(session);
    return failure;
  }
  async function refreshBrowse(session, refresh, context = {}) {
    const generation = session.browseGeneration;
    await session.initialization;
    if (session.released || session.stopped || closed || generation !== session.browseGeneration ||
        !groupScopeAvailable(session)) return;
    if (session.initializationError || session.entryTarget.metadataProvider === 'douban' &&
        !session.target.metadataProvider && session.target.kind === 'group' && !session.target.members.length) {
      session.initializationError = null; session.error = null; session.stage = 'loading';
      if (session.entryResolution === 'error' || session.entryResolution === 'unmatched')
        session.entryResolution = 'loading';
      session.browsing = { ...session.browsing, loading: true, error: null };
      session.initialization = tracked(initialize(session, [], { refresh }));
      publish(session);
      await session.initialization;
      return;
    }
    if (Object.hasOwn(context, 'sourceId') || Object.hasOwn(context, 'metadataSourceId') ||
        Object.hasOwn(context, 'lineId') || Object.hasOwn(context, 'season')) {
      return browse(session, context.sourceId ?? null, context.lineId, {
        refresh, retainDisplay: true, ...fields(context, ['metadataSourceId', 'season']),
      });
    }
    const resultId = session.browsing.sourceId;
    if (resultId) return browse(session, resultId, session.browsing.lineId ?? undefined,
      { refresh, retainDisplay: true });
    if (session.metadataSourceId || session.target.metadataProvider)
      return refreshMetadataDisplay(session, { refresh });
    const candidate = session.pending?.candidate ?? session.current?.candidate;
    const source = candidate?.source ?? session.target.source;
    const result = session.results.find(row => row.source.key === source?.key &&
      row.source.detailId === source?.detailId);
    requireValue(result, 'NOT_FOUND');
    return browse(session, result.id, candidate?.lineId, { refresh, retainDisplay: true });
  }
  async function saveHistory(session, selected = session.current, ended = false) {
    if (!selected) return;
    const candidate = selected.candidate;
    await library.saveHistory({targetId:selected.target.id,title:selected.target.title,
      playbackTarget:catalog.candidate(selected.target,candidate).playbackTarget,
      sourceKey:candidate.source.key,detailId:candidate.source.detailId,lineId:candidate.lineId,candidateId:candidate.id,
      episode:fields(selected.target,['season','episode','globalEpisode']),position:session.position,
      duration:session.duration,paused:session.paused,ended,updatedAt:now()});
    session.lastHistoryWrite = now();
  }
  function report(session, event, selected = session.current, force = false) {
    if (!selected || selected.descriptor.watchReport !== true ||
        !force && now() - (selected.lastReport ?? 0) < 12_000) return;
    selected.lastReport = now();
    const payload = { sessionId: selected.id, positionSeconds: session.position,
      durationSeconds: session.duration, event };
    session.reportQueue = session.reportQueue.then(() => data.report(selected.candidate, payload)).then(() => {
      if (!session.released && session.current?.id === selected.id) { session.reportError = null; publish(session); }
    }, error => {
      if (!session.released && session.current?.id === selected.id) { session.reportError = codeOf(error); publish(session); }
    });
    tracked(session.reportQueue);
  }
  function actualSourceEpisodes(session) {
    const candidate = session.current?.candidate ?? session.pending?.candidate;
    const result = session.results.find(value => value.source.key === candidate?.source.key &&
      value.source.detailId === candidate?.source.detailId);
    const line = result?.detail?.lines.find(value => value.id === candidate.lineId);
    return line?.episodes ?? [];
  }
  function adjacent(session, direction) {
    if (session.target.kind === 'movie') return null;
    if (['source', 'group'].includes(session.target.kind)) {
      const current = session.current?.candidate ?? session.pending?.candidate;
      const items = actualSourceEpisodes(session);
      const index = items.findIndex(value => value.id === current?.episodeId);
      const item = index < 0 ? null : items[index + direction];
      if (!item) return null;
      const candidate = [...session.candidates.values()].find(value => value.source.key === current.source.key &&
        value.source.detailId === current.source.detailId && value.lineId === current.lineId && value.episodeId === item.id);
      return candidate ? { target: targetForCandidate(session.target, candidate), candidateId: candidate.id } : null;
    }
    const items = episodeProjection(session, playbackMetadata(session)).visible;
    const index = items.findIndex(value => value.globalEpisode !== undefined && value.globalEpisode === session.target.globalEpisode ||
      value.season === session.target.season && value.episode === session.target.episode);
    return index < 0 || !items[index + direction] ? null : { target: episodeTarget(session.target, items[index + direction]) };
  }
  async function selectTarget(session, target) {
    const generation = session.generation;
    const sameVariant = sameVariantFile(session.candidates.get(session.target.source?.candidateId),
      session.candidates.get(target.source?.candidateId));
    if (!sameEpisode(session.target, target) && !sameVariant) {
      session.excluded.clear(); session.candidates.clear(); session.candidateEpoch++; session.resumePosition = 0;
      session.resumeCandidateId = null;
    }
    session.target = target;
    for (const result of session.results) {
      if (result.detail) await candidatesFrom(session, result, result.detail);
      if (closed || session.released || session.generation !== generation || session.target !== target) return false;
    }
    return !closed && !session.released && session.generation === generation && session.target === target;
  }
  function targetForCandidate(target, candidate) {
    const source = { key: candidate.source.key, detailId: candidate.source.detailId,
      lineId: candidate.lineId, candidateId: candidate.id };
    return episodeTarget({ ...(['source', 'group'].includes(target.kind) ? target
      : { ...fields(target, ['id', 'title', 'year', 'members']), kind: 'source' }), source }, candidate);
  }
  function selectedMetadataTarget(session) {
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    requireValue(selected, 'NOT_FOUND');
    return checkTarget({ ...fields(session.target, ['id', 'title', 'year', 'members']),
      kind: selected.kind, metadataProvider: selected.provider, metadataId: selected.metadataId });
  }
  async function playTarget(session, target, { prepareOnly = false, candidateId = null,
    metadataSourceId = session.playbackMetadataSourceId } = {}) {
    const changed = !sameEpisode(session.target, target) || metadataSourceId !== session.playbackMetadataSourceId;
    if (changed) {
      session.generation++; session.runKey = null;
      session.playbackMetadataSourceId = metadataSourceId;
    }
    const generation = session.generation;
    try {
      if (changed) {
        const resumePosition = session.resumePosition, resumeCandidateId = session.resumeCandidateId;
        const sameFile = value => candidateId && value?.target.id === target.id &&
          value.candidate.id === candidateId && !value.resourceReleased;
        const pending = session.pending;
        if (pending && !sameFile(pending)) {
          session.pending = null; detached(session, retire(session, pending));
        }
        if (!await selectTarget(session, target)) return;
        for (const value of [session.pending, session.current]) {
          if (!sameFile(value)) continue;
          value.target = clone(target); value.generation = generation;
          if (value === session.current) session.playbackTarget = clone(target);
        }
        if (candidateId && candidateId === resumeCandidateId) {
          session.resumePosition = resumePosition; session.resumeCandidateId = resumeCandidateId;
        }
      }
      const selectedTarget = session.target, pending = session.pending;
      const eligible = pending && sameEpisode(pending.target, selectedTarget) && preparedValid(pending) &&
        await allowed(session, pending.candidate, { gate: false, explicit: pending.explicit });
      if (closed || session.released || generation !== session.generation || pending !== session.pending ||
          selectedTarget !== session.target) return;
      if (eligible) {
        pending.prepareOnly = prepareOnly;
        session.stage = prepareOnly ? 'prepared' : 'pending'; publish(session);
      } else if (!prepareOnly && session.current && !session.current.resourceReleased &&
          sameEpisode(session.current.target, selectedTarget)) {
        session.stage = 'playing'; publish(session);
      } else void start(session, { prepareOnly, explicitCandidateId: candidateId });
    } catch (error) {
      if (!closed && !session.released && generation === session.generation) {
        session.stage = 'unavailable'; session.error = codeOf(error); publish(session);
      }
    }
  }
  async function playSelection(session, prepareOnly = false) {
    const generation = session.generation, selection = ++session.selectionEpoch;
    const admitted = () => !closed && !session.released && generation === session.generation &&
      selection === session.selectionEpoch;
    await session.initialization;
    if (!admitted()) return;
    try {
      let target = clone(session.target);
      if (['source', 'group'].includes(target.kind) && target.source?.candidateId === undefined) {
        const pointer = target.source ?? target.members[0];
        const result = session.results.find(row => row.source.key === (pointer?.key ?? pointer?.sourceKey) &&
          row.source.detailId === pointer?.detailId);
        requireValue(result && currentSource(session, result.source), 'SOURCE_NOT_FOUND');
        requireValue(result.detail, result.error ?? 'MEDIA_UNAVAILABLE');
        const line = target.source?.lineId === undefined ? result.detail.lines[0]
          : result.detail.lines.find(row => row.id === target.source.lineId);
        requireValue(line, 'MEDIA_UNAVAILABLE');
        const candidates = await recognizeResult(session, result, result.detail, target, playbackMetadata(session), 'session');
        if (!admitted()) return;
        const rows = line.episodes.map(row => candidates.find(candidate => candidate.lineId === line.id &&
          candidate.episodeId === row.id)).filter(candidate => candidate && (!candidate.share || candidate.file));
        const selected = target.source && ['playId', 'episodeId'].some(field => target.source[field] !== undefined)
          ? rows.find(candidate => ['playId', 'episodeId'].every(field =>
            target.source[field] === undefined || target.source[field] === candidate[field]))
          : target.episode !== undefined || target.globalEpisode !== undefined
            ? rows.find(candidate => ['season', 'episode', 'globalEpisode'].every(field =>
              target[field] === undefined || target[field] === candidate[field]))
            : rows.find(candidate => candidate.id === session.resumeCandidateId) ?? rows[0];
        requireValue(selected, 'MEDIA_UNAVAILABLE');
        target = targetForCandidate(target, selected);
      }
      await playTarget(session, target, { prepareOnly });
    } catch (error) {
      if (admitted()) { session.stage = 'unavailable'; session.error = codeOf(error); publish(session); }
    }
  }
  async function selectPlaybackTarget(session, input) {
    const target = checkTarget(input.target);
    requireValue(input.metadataSourceId == null || text(input.metadataSourceId));
    let metadataSourceId = input.metadataSourceId === undefined
      ? session.playbackMetadataSourceId : input.metadataSourceId;
    const selected = target.metadataProvider
      ? session.metadataSources.find(row => row.provider === target.metadataProvider && row.kind === target.kind &&
        String(row.metadataId) === String(target.metadataId))
      : session.metadataSources.find(row => row.id === metadataSourceId);
    let result;
    if (target.metadataProvider) {
      requireValue(selected && target.id === `${target.metadataProvider}:${target.kind}:${target.metadataId}`, 'NO_MATCH');
      metadataSourceId = selected.id;
    } else {
      requireValue(target.kind === 'source' && [session.selectionId, session.target.id,
        ...session.metadataSources.map(row => `${row.provider}:${row.kind}:${row.metadataId}`)].includes(target.id), 'NO_MATCH');
      result = session.results.find(row => row.source.key === target.source.key &&
        row.source.detailId === target.source.detailId);
      requireValue(result?.detail && text(target.source.candidateId), 'NOT_FOUND');
      requireValue(currentSource(session, result.source), 'STALE_SOURCE');
      if (metadataSourceId !== null) requireValue(selected, 'NOT_FOUND');
    }
    const generation = session.generation, selection = ++session.selectionEpoch;
    const admitted = () => !closed && !session.released && generation === session.generation &&
      selection === session.selectionEpoch;
    let metadata = selected ? session.metadataViews.get(selected.id)?.display ?? selected.metadata : null;
    let canonical, candidateId = null;
    if (target.metadataProvider) {
      if (target.kind === 'tv') {
        let episode = resolveEpisode(episodeMap(metadata), target);
        if (!episode) {
          const entry = await data.seasonMetadataEntry(target, target.season, { rules: session.rules, canSchedule: admitted });
          if (!admitted()) return;
          metadata = mergeSeason(target, metadata, target.season, entry.value);
          metadataDisplay(session, selected);
          acceptMetadataCache(session, entry, target.season, undefined, selected.id, metadata);
          if (selected.id === session.metadataSourceId) session.metadata = metadata;
          episode = resolveEpisode(episodeMap(metadata), target);
        }
        requireValue(episode && episodeProjection(session, metadata).visibleKeys.has(`${episode.season}:${episode.episode}`), 'NO_MATCH');
        canonical = episodeTarget({ ...target, title: selected.title }, episode);
      } else canonical = { ...target, title: selected.title };
    } else {
      const context = selected ? { id: target.id, title: session.entryTarget.title, kind: selected.kind,
        metadataProvider: selected.provider, metadataId: selected.metadataId } : session.entryTarget;
      const candidates = await recognizeResult(session, result, result.detail, context, metadata, 'session');
      if (!admitted()) return;
      const candidate = candidates.find(row => row.id === target.source.candidateId);
      requireValue(candidate, 'NOT_FOUND');
      canonical = catalog.candidate({ id: target.id, title: session.entryTarget.title }, candidate).playbackTarget;
      for (const field of ['key', 'engine', 'installationId', 'siteId', 'detailId', 'lineId', 'candidateId'])
        requireValue(target.source[field] === canonical.source[field], 'NO_MATCH');
      for (const field of ['season', 'episode', 'globalEpisode'])
        requireValue(target[field] === canonical[field], 'NO_MATCH');
      candidateId = candidate.id;
    }
    if (!admitted()) return;
    session.userSelected = true;
    await playTarget(session, canonical, { candidateId, metadataSourceId });
  }
  function reselectActualFile(session, selected = session.pending ?? session.current) {
    if (!['source', 'group'].includes(session.target.kind)) return false;
    const candidate = selected?.candidate ?? session.actionCurrent;
    if (!candidate || !selected && (candidate.targetId !== session.target.id ||
        candidate.kind !== session.target.kind || candidate.id !== session.target.source?.candidateId)) return false;
    // Coordinates can prove another raw file is the same episode; real variants
    // can also prove they belong to the same file without inventing coordinates.
    if (candidate.episode === undefined && candidate.globalEpisode === undefined && !candidate.variant) return null;
    // The public/saved target stays exact even if this operation finds nothing.
    // Only this run's internal policy context may bypass the file/line selector.
    return candidate;
  }
  async function moveEpisode(session, direction) {
    let next = adjacent(session, direction);
    if (!next && direction > 0) {
      const generation = session.generation, target = session.target;
      await checkNextEpisode(session);
      if (!valid(session, generation) || session.target !== target) return;
      next = adjacent(session, direction);
    }
    requireValue(next, 'NO_MATCH');
    session.userSelected = true;
    if (!await selectTarget(session, next.target)) return;
    void start(session, { explicitCandidateId: next.candidateId });
  }
  async function checkNextEpisode(session) {
    if (session.target.kind === 'tv') {
      const generation = session.generation, target = session.target;
      await refreshPlaybackSeason(session, session.target.season,
        session.target.episode === undefined ? undefined : session.target.episode + 1);
      if (!valid(session, generation) || session.target !== target || adjacent(session, 1)) return;
      const metadata = playbackMetadata(session);
      const nextSeason = (metadata?.doubanSeasons ?? metadata?.seasons ?? [])
        .map(row => row.season_number ?? row.season)
        .filter(season => Number.isSafeInteger(season) && season > session.target.season).sort((a, b) => a - b)[0];
      if (nextSeason !== undefined) await refreshPlaybackSeason(session, nextSeason, 1);
      return;
    }
    if (!['source', 'group'].includes(session.target.kind)) return;
    const selected = session.current ?? session.pending;
    if (!selected) return;
    const candidate = selected.candidate, generation = session.generation;
    const result = session.results.find(row => row.source.key === candidate.source.key &&
      row.source.detailId === candidate.source.detailId);
    if (!result) return;
    const admitted = () => valid(session, generation) && currentSource(session, result.source) &&
      (session.current === selected || session.pending === selected);
    try {
      const detail = result.source.engine === 'pan' ? result.detail : await loadDetail(session, result, generation);
      if (!detail || !admitted()) return;
      const index = detail.lines.findIndex(row => row.id === candidate.lineId);
      if (index < 0) return;
      const line = await data.expand(result.source,
        result.baseDetail?.lines.find(row => row.id === candidate.lineId) ?? detail.lines[index], { canSchedule: admitted });
      if (!admitted()) return;
      const expanded = replaceLine(result, detail, index, line);
      await candidatesFrom(session, result, expanded);
      if (!admitted()) return;
      await refreshBrowsing(session);
      if (!admitted()) return;
    } catch (error) {
      if (!admitted()) return;
      await refreshBrowsing(session);
      if (!admitted()) return;
      session.error = codeOf(error);
    }
    publish(session);
  }
  async function retryMedia(session, attemptId) {
    const selected = attemptId === undefined ? session.pending ?? session.current ?? session.lastFailed
      : [session.pending, session.current, session.lastFailed].find(value => value?.id === attemptId);
    requireValue(selected, 'STALE_SESSION');
    requireValue(currentSource(session, selected.candidate.source), 'STALE_SOURCE');
    const preflightGeneration = session.generation, target = session.target;
    const admitted = () => !closed && !session.released && session.generation === preflightGeneration &&
      currentSource(session, selected.candidate.source) &&
      [session.pending, session.current, session.lastFailed].includes(selected);
    const eligible = await allowed(session, selected.candidate, { explicit: true, target: selected.target, retry: true });
    if (!admitted() || session.target !== target) return;
    requireValue(eligible, 'NO_MATCH');
    if (!sameEpisode(session.target, selected.target) &&
        !await selectTarget(session, clone(selected.target))) return;
    if (!admitted()) return;
    const generation = ++session.generation;
    session.runKey = null; session.stopped = false;
    session.feedbackFailure = null; session.selectionError = null;
    session.excluded.delete(selected.candidate.id);
    if (selected.candidate.fileIdentity) session.excluded.delete(selected.candidate.fileIdentity);
    session.prepareOnly = selected.prepareOnly;
    const previous = session.pending;
    session.pending = null;
    if (previous) detached(session, retire(session, previous));
    session.stage = 'resolving'; session.error = null; publish(session);
    const work = tracked((async () => {
      try {
        await runIO(session, generation, selected.candidate.source, selected.candidate, async () => {
          let descriptor = await data.refresh(selected.candidate, { canSchedule: () => valid(session, generation) });
          if (!valid(session, generation)) return;
          const changed = await registerVariants(session, selected.candidate, descriptor);
          if (!valid(session, generation)) return;
          let candidate = selected.candidate.variant
            ? session.candidates.get(selected.candidate.id) : selected.candidate;
          if (changed && !selected.candidate.variant) {
            const children = [...session.candidates.values()].filter(value => value.parentId === candidate.id);
            const eligibility = await Promise.all(children.map(value => allowed(session, value, { retry: true })));
            const options = children.filter((_, index) => eligibility[index]);
            options.sort((a, b) => compareCandidates(a, b, { preferences: preferences(),
              constraint: session.constraint, current: session.current?.candidate ?? session.actionCurrent }));
            candidate = options[0];
            requireValue(candidate, 'NO_MATCH');
            // A previously opaque parent has just acquired real variants.
            // Resolve the chosen child; never prepare the master as that child.
            descriptor = await data.resolve(candidate, { canSchedule: () => valid(session, generation) });
            if (!valid(session, generation)) return;
          }
          requireValue(candidate, 'MEDIA_UNAVAILABLE');
          if (candidate.variant) requireValue(await allowed(session, candidate, { retry: true }), 'NO_MATCH');
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          if (await installPending(session, candidate, descriptor, resource, generation, now(), true)) {
            session.pending.position = selected === session.current ? session.position : selected.position;
            session.pending.paused = selected === session.current ? session.paused : selected.paused;
            session.lastFailed = null;
            publish(session);
          }
        });
      } catch (error) {
        if (!valid(session, generation)) return;
        session.lastFailed = { id: selected.id, candidate: selected.candidate, target: selected.target,
          prepareOnly: selected.prepareOnly, position: selected.position, paused: selected.paused };
        session.error = codeOf(error);
        session.stage = session.current && !session.current.resourceReleased ? 'playing' : 'error';
        publish(session);
      }
    })());
    session.runWork = work; session.runGeneration = generation;
    void work.finally(() => { if (session.runWork === work) session.runWork = null; }).catch(() => {});
  }
  async function clearPrefetch(session) {
    session.prefetchGeneration++;
    const values = [...session.prefetch.values()]; session.prefetch.clear();
    await Promise.all(values.filter(value => value.resource).map(value => media.release(value.resource.id)));
  }
  async function refreshEligibility(session) {
    // Discovery is relative to the currently eligible source set, not a
    // permanent fact. A previously purified source may now need searching.
    session.discoveryComplete = false;
    const generation = session.generation;
    if ((await Promise.all([...session.prefetch.values()].map(async value => value.candidate &&
        !await allowed(session, value.candidate, { gate: false, target: value.target })))).some(Boolean)) {
      detached(session, clearPrefetch(session));
    }
    if (closed || session.released || generation !== session.generation) return;
    const pending = session.pending;
    const eligible = !pending || await allowed(session, pending.candidate, { gate: false, explicit: pending.explicit });
    if (closed || session.released || generation !== session.generation || pending !== session.pending) return;
    if (pending && !eligible) {
      session.generation++; session.runKey = null;
      void start(session, { prepareOnly: session.prepareOnly, constraint: session.constraint });
    } else if (session.runWork && !session.pending && !session.stopped) {
      session.generation++; session.runKey = null;
      void start(session, { prepareOnly: session.prepareOnly, constraint: session.constraint });
    }
    publish(session);
  }
  async function refreshPreferences() {
    const next = preferences(), previous = observedPreferences;
    if (isDeepStrictEqual(previous, next)) return;
    observedPreferences = next;
    const policyFields = ['ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords'];
    const policyChanged = !isDeepStrictEqual(fields(previous, policyFields), fields(next, policyFields));
    for (const session of sessions.values()) {
      if (policyChanged) await refreshEligibility(session);
      else publish(session);
    }
  }
  async function prefetchNeighbors(session) {
    if (!session.shortVideo || !session.current || session.current.resourceReleased || session.released || session.stopped) return;
    const generation = ++session.prefetchGeneration;
    const target = clone(session.target);
    const usable = () => session.prefetchGeneration === generation && !session.released && !closed &&
      session.shortVideo && sameEpisode(session.target, target) && !!session.current && !session.current.resourceReleased;
    const neighbors = [1, -1].map(direction => {
      const value = adjacent(session, direction);
      return value ? { ...value, direction } : null;
    }).filter(Boolean);
    const keep = new Set(neighbors.map(item => identity(item.target)));
    for (const [key, value] of session.prefetch) {
      if (!keep.has(key) || !preparedValid(value) || value.warming) {
        session.prefetch.delete(key);
        if (value.resource) await media.release(value.resource.id);
      } else value.generation = generation;
    }
    for (let index = 0; index < neighbors.length && usable(); index++) {
      const neighbor = neighbors[index], key = identity(neighbor.target);
      if (session.prefetch.has(key)) continue;
      const entry = { target: neighbor.target, state: 'resolving', generation };
      session.prefetch.set(key, entry); publish(session);
      try {
        const available = (await Promise.all(session.results.map(async result => result.detail &&
          (!neighbor.target.metadataProvider || await smartResult(session, result, neighbor.target))
          ? recognizeResult(session, result, result.detail, neighbor.target) : []))).flat();
        const eligibility = await Promise.all(available.map(candidate =>
          allowed(session, candidate, { gate: false, target: neighbor.target })));
        if (!usable()) return;
        const candidates = available.filter((_, index) => eligibility[index]);
        candidates.sort((a, b) => compareCandidates(a, b, { preferences: preferences(), current: session.current.candidate }));
        const candidate = candidates[0];
        requireValue(candidate, 'NO_MATCH');
        entry.candidate = candidate;
        const descriptor = await data.resolve(candidate, { canSchedule: usable });
        if (!usable()) return;
        const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: neighbor.direction === 1 });
        if (!usable()) { await media.release(resource.id); return; }
        Object.assign(entry, { candidate, descriptor, resource, preparedAt: now(), state: 'prepared' });
        if (neighbor.direction === 1) {
          entry.warming = true;
          const buffered = await media.prefetch(resource.id);
          entry.warming = false;
          if (!usable()) return;
          entry.state = 'buffered'; entry.bytes = buffered.bytes;
        }
      } catch (error) {
        if (!usable()) return;
        entry.state = 'error'; entry.error = codeOf(error);
      } finally {
        if (!usable() && entry.generation === generation && session.prefetch.get(key) === entry) {
          session.prefetch.delete(key);
          if (entry.resource) await media.release(entry.resource.id);
        }
      }
      publish(session);
    }
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
    const results = await Promise.allSettled([cancelCalculations(session), clearPrefetch(session),
      ...[...session.leases].map(async ([attemptId, resourceId]) => {
        await media.release(resourceId); session.leases.delete(attemptId);
      })]);
    error ??= results.find(value => value.status === 'rejected')?.reason;
    if (error) session.error = codeOf(error);
    publish(session);
    if (error) throw error;
  }
  async function command(input, { searchHandoff } = {}) {
    requireValue(object(input) && text(input.action));
    const session = get(input.sessionId);
    if (input.action === 'browse' || input.action.startsWith('browse.')) {
      for (const field of ['sourceId', 'metadataSourceId'])
        requireValue(input[field] == null || text(input[field]));
      requireValue(input.lineId == null || isIdentifier(input.lineId));
      requireValue(input.season === undefined || Number.isSafeInteger(input.season) && input.season >= 0);
      if (input.metadataSourceId && session.metadataSources.some(row => row.id === input.metadataSourceId))
        session.metadataViewInitialized = true;
    }
    switch (input.action) {
      case 'media.chapters': {
        requireValue(text(input.attemptId) && text(input.resourceId));
        const value = session.pending?.id === input.attemptId ? session.pending
          : session.current?.id === input.attemptId ? session.current : null;
        requireValue(value && !value.resourceReleased && value.resource.id === input.resourceId &&
          session.leases.get(input.attemptId) === input.resourceId, 'STALE_SESSION');
        return recognizeChapters(input.chapters);
      }
      case 'prepare':
      case 'play': {
        await playSelection(session, input.action === 'prepare');
        break;
      }
      case 'select.target':
        await selectPlaybackTarget(session, input); break;
      case 'browse.season': {
        requireValue(Number.isSafeInteger(input.season) && input.season >= 0);
        const metadataSourceId = input.metadataSourceId ?? session.metadataSourceId;
        requireValue(session.metadataSources.some(row => row.id === metadataSourceId && row.kind === 'tv'), 'NOT_FOUND');
        session.browseWork = browse(session,
          Object.hasOwn(input, 'sourceId') ? input.sourceId : session.browsing.sourceId,
          Object.hasOwn(input, 'lineId') ? input.lineId : session.browsing.lineId, {
            metadataSourceId, season: input.season,
          });
        detachedBrowsing(session, session.browseWork);
        break;
      }
      case 'next': case 'previous':
        await moveEpisode(session, input.action === 'next' ? 1 : -1); break;
      case 'preview':
        requireValue(typeof input.enabled === 'boolean');
        await library.update(document => {
          document.previewTargets = [...document.previewTargets.filter(id => id !== session.target.id),
            ...(input.enabled ? [session.target.id] : [])];
        });
        session.previewEpisodes = input.enabled; publish(session); break;
      case 'shortvideo':
        requireValue(typeof input.enabled === 'boolean');
        session.shortVideo = input.enabled;
        if (input.enabled) detached(session, prefetchNeighbors(session));
        else await Promise.all([clearPrefetch(session),
          ...[session.current, session.pending].filter(Boolean).map(value => media.cancelPrefetch(value.resource.id))]);
        publish(session); break;
      case 'pan': case 'quality': case 'switch': {
        if (input.action === 'pan') requireValue(providerFamily(input.provider));
        if (input.action === 'quality') requireValue(qualityKey(input.quality));
        if (session.current && !sameEpisode(session.target, session.current.target)) {
          if (!await selectTarget(session, clone(session.current.target))) break;
        }
        if (input.action === 'switch') {
          const candidate = session.current?.candidate ?? session.pending?.candidate;
          if (candidate) excluded(session, candidate);
        }
        const reselecting = reselectActualFile(session, session.current ?? session.pending);
        void start(session, { constraint: fields(input, ['action', 'provider', 'quality']), reselecting }); break;
      }
      case 'block': {
        const selected = session.current ?? session.pending;
        requireValue(selected && input.attemptId === selected.id, 'STALE_SESSION');
        const candidate = selected.candidate;
        await library.update(document => {
          const block = { targetId: selected.target.id, sourceKey: candidate.source.key,
            detailId: candidate.source.detailId, lineId: candidate.lineId };
          if (!document.blocks.some(row => identity(row) === identity(block))) document.blocks.push(block);
        });
        if (!sameEpisode(session.target, selected.target) &&
            !await selectTarget(session, clone(selected.target))) break;
        excluded(session, candidate);
        void start(session, { reselecting: reselectActualFile(session, selected) }); break;
      }
      case 'browse':
        session.browseWork = browse(session, input.sourceId, input.lineId,
          fields(input, ['metadataSourceId', 'season']));
        detachedBrowsing(session, session.browseWork); break;
      case 'browse.refresh':
        requireValue(input.refresh === undefined || typeof input.refresh === 'boolean');
        detachedBrowsing(session, refreshBrowse(session, input.refresh !== false, input)); break;
      case 'media.retry':
        requireValue(input.attemptId === undefined || text(input.attemptId));
        await retryMedia(session, input.attemptId); break;
      case 'browse.metadata': {
        requireValue(text(input.metadataSourceId) &&
          (input.keepSource === undefined || typeof input.keepSource === 'boolean'));
        const keepSource = input.keepSource !== false;
        session.browseWork = browse(session,
          Object.hasOwn(input, 'sourceId') ? input.sourceId : keepSource ? session.browsing.sourceId : null,
          Object.hasOwn(input, 'lineId') ? input.lineId : keepSource ? session.browsing.lineId : undefined, {
            metadataSourceId: input.metadataSourceId, ...fields(input, ['season']),
          });
        detachedBrowsing(session, session.browseWork); break;
      }
      case 'search.update': await updateSearch(session, input, searchHandoff); break;
      case 'search.more':
        requireValue(groupScopeAvailable(session), 'SOURCE_NOT_FOUND');
        requireValue(!['loading', 'error'].includes(session.entryResolution), 'INVALID_REQUEST');
        detached(session, (async () => {
          await session.initialization;
          const epoch = session.searchGeneration;
          for (const source of session.sources) {
            if (session.released || session.stopped || closed || session.initializationError ||
                epoch !== session.searchGeneration || !groupScopeAvailable(session)) break;
            if (sourceAllowed(session, source)) await searchPage(session, source);
          }
        })()); break;
      case 'media.started': {
        requireValue(session.pending?.id === input.attemptId && !session.pending.prepareOnly &&
          !session.pending.resourceReleased, 'STALE_SESSION');
        const old = session.current;
        report(session, 'stopped', old, true);
        session.current = session.pending; session.pending = null;
        session.lastFailed = null;
        session.feedbackFailure = null; session.selectionError = null;
        session.playbackTarget = clone(session.current.target);
        session.position = session.current.position; session.paused = session.current.paused;
        session.duration = 0; session.stage = 'playing'; session.error = null;
        if (old) detached(session, retire(session, old));
        report(session, 'started', session.current, true);
        try { await saveHistory(session); }
        catch (error) { session.error = codeOf(error); publish(session); throw error; }
        publish(session); detached(session, prefetchNeighbors(session)); break;
      }
      case 'media.failed': {
        const attempted = session.pending?.id === input.attemptId ? session.pending
          : session.current?.id === input.attemptId ? session.current : null;
        requireValue(attempted, 'STALE_SESSION');
        const failure = playbackFeedbackFailure(input.code, attempted === session.pending ? 'prepare' : 'playback');
        const generation = session.generation;
        const feedbackFailure = attempted.generation !== generation ? session.feedbackFailure ?? null
          : nativeMediaErrorCodes.has(session.feedbackFailure?.code) ? session.feedbackFailure
            : failure ?? session.feedbackFailure ?? null;
        excluded(session, attempted.candidate);
        session.lastFailed = { id: attempted.id, candidate: attempted.candidate, target: attempted.target,
          prepareOnly: attempted.prepareOnly, position: attempted === session.current ? session.position : attempted.position,
          paused: attempted === session.current ? session.paused : attempted.paused };
        const invalidated = data.invalidateAddress(attempted.candidate);
        invalidated.catch(() => {});
        const newerRun = attempted === session.current && attempted.generation < session.generation &&
          session.runWork && session.runGeneration === session.generation && !session.stopped;
        // Keep the original same-episode anchor across provisional failures.
        // An explicitly selected different file must retain its own target.
        const reselecting = attempted.generation === session.generation && sameEpisode(attempted.target, session.target) &&
          (session.reselecting || reselectActualFile(session, attempted));
        const constraint = attempted.candidate.variant && session.constraint.action === 'quality'
          ? { action: 'default' } : session.constraint;
        let storageFailure;
        if (attempted === session.pending) session.pending = null;
        if (attempted === session.current) {
          try { await saveHistory(session); } catch (error) { storageFailure = error; }
          report(session, 'stopped', attempted, true); session.current = null;
        }
        detached(session, retire(session, attempted));
        try { await invalidated; }
        catch (error) {
          if (valid(session, generation) && !newerRun) {
            session.feedbackFailure = feedbackFailure;
            session.error = selectionError(session, codeOf(error)); publish(session);
          }
          throw error;
        }
        if (valid(session, generation)) {
          if (!session.pending && !newerRun) {
            session.runKey = null;
            void start(session, { constraint, reselecting, feedbackFailure });
          }
          else publish(session);
        }
        if (storageFailure) throw storageFailure;
        break;
      }
      case 'media.progress': {
        requireValue(session.current?.id === input.attemptId, 'STALE_SESSION');
        requireValue(Number.isFinite(input.position) && input.position >= 0 &&
          Number.isFinite(input.duration) && input.duration >= 0 && typeof input.paused === 'boolean');
        const changedPause = session.paused !== input.paused;
        const changedPausedPosition = input.paused && session.position !== input.position;
        session.position = input.position; session.duration = input.duration; session.paused = input.paused;
        if (changedPause || changedPausedPosition || now() - session.lastHistoryWrite >= 12_000) await saveHistory(session);
        report(session, 'progress', session.current, changedPause); publish(session); break;
      }
      case 'media.ended': {
        requireValue(session.current?.id === input.attemptId, 'STALE_SESSION');
        const ended = session.current;
        let latest = ended.generation === session.generation && sameEpisode(ended.target, session.target) && !session.stopped;
        let next = latest ? adjacent(session, 1) : null;
        if (latest && !next && session.target.kind !== 'movie') {
          await checkNextEpisode(session);
          if (session.current !== ended || session.released || closed) break;
          latest = ended.generation === session.generation && sameEpisode(ended.target, session.target) && !session.stopped;
          next = latest ? adjacent(session, 1) : null;
        }
        session.position = session.duration; await saveHistory(session, session.current, true);
        report(session, 'stopped', session.current, true);
        session.current = null;
        detached(session, retire(session, ended));
        if (!latest) {
          // The old output may finish while a newer user intent is resolving.
          // It owns history/cleanup, not that newer intent or its episode.
          if (session.stage === 'playing') session.stage = 'idle';
          publish(session);
        } else if (next) {
          if (!await selectTarget(session, next.target)) break;
          void start(session, { explicitCandidateId: next.candidateId });
        } else {
          session.stage = 'ended'; publish(session);
        }
        break;
      }
      case 'stop': await stop(session); break;
      default: throw fail('INVALID_REQUEST');
    }
    return snapshot(session);
  }
  function attempt(input) {
    const session = get(input.sessionId);
    const value = session.pending?.id === input.attemptId ? session.pending
      : session.current?.id === input.attemptId ? session.current : null;
    requireValue(value && !value.resourceReleased, 'STALE_SESSION');
    return { attemptId: value.id, ...clone(value.resource), title: value.target.title,
      position: value.position, paused: value.paused, ...(value.descriptor.scope ? { scope: clone(value.descriptor.scope) } : {}) };
  }
  async function releaseMedia(input) {
    requireValue(!closed, 'CLOSED');
    requireValue(object(input) && text(input.sessionId) && text(input.attemptId) && text(input.resourceId));
    const session = sessions.get(input.sessionId);
    // Page shutdown may already have drained this receipt. A repeated release
    // never acquires access to an unowned resource and needs no tombstone log.
    const resourceId = session?.leases.get(input.attemptId);
    if (!resourceId) return { released: true };
    requireValue(resourceId === input.resourceId, 'STALE_SESSION');
    // A native decoder may physically retire its input before delivering the
    // terminal event. Keep only the existing pending/current attempt identity
    // for that event, never an extra tombstone/history or a reusable receipt.
    const value = session.pending?.id === input.attemptId ? session.pending
      : session.current?.id === input.attemptId ? session.current : null;
    if (value && !value.resourceReleased) {
      value.resourceReleased = true;
      session.stage = session.pending && !session.pending.resourceReleased ? (session.pending.prepareOnly ? 'prepared' : 'pending')
        : session.current && !session.current.resourceReleased ? 'playing' : 'idle';
      publish(session);
    }
    await media.release(input.resourceId);
    session.leases.delete(input.attemptId);
    return { released: true };
  }
  async function releaseSession(session, input) {
    if (session.releaseWork) return session.releaseWork;
    if (input.keepMedia && session.current && !session.current.resourceReleased) {
      session.generation++; session.browseGeneration++; session.searchGeneration++; session.metadataEpoch++;
      session.searchUpdateEpoch++;
      session.stopped = true;
      const calculations = cancelCalculations(session);
      const pending = session.pending; session.pending = null;
      await Promise.all([calculations, pending ? retire(session, pending) : null, clearPrefetch(session)]);
      return { retained: true };
    }
    session.released = true;
    for (const listener of session.listeners) listener();
    return session.releaseWork = (async () => {
      let error;
      try { await stop(session); } catch (caught) { error = caught; }
      sessions.delete(session.id);
      if (error) throw error;
      return { released: true };
    })();
  }
  function release(input) { return releaseSession(get(input.sessionId), input); }
  function events(id, after = -1) {
    const session = get(id);
    let demand = false, disposed = false, initial = true;
    const changed = () => {
      if (disposed) return;
      if (closed || session.released) { body.push(null); return; }
      if (!demand || !initial && session.sequence <= after) return;
      demand = false; initial = false; after = session.sequence;
      body.push(Buffer.from(`id: ${after}\nevent: snapshot\ndata: ${stringifyJSONValue(snapshot(session))}\n\n`));
    };
    const body = new Readable({ highWaterMark: 16 * 1024,
      read() { demand = true; changed(); },
      destroy(error, done) { disposed = true; session.listeners.delete(changed); done(error); } });
    session.listeners.add(changed);
    return { status: 200, headers: { 'content-type': 'text/event-stream', 'cache-control': 'no-store' }, body,
      async dispose() { body.destroy(); } };
  }
  return {
    open: (...args) => tracked(open(...args)),
    command, attempt, release, releaseMedia, events, snapshot: input => snapshot(get(input.sessionId)),
    settings() { return clone(preferences()); },
    async settingsChanged() {
      await refreshPreferences();
    },
    async setSettings(input) {
      requireValue(library.configurationOwner !== 'native', 'PERMISSION_DENIED');
      const next = normalizePreferences(input);
      if (!isDeepStrictEqual(preferences(), next))
        await library.update(document => { document.preferences = { ...document.preferences, ...next }; });
      await refreshPreferences();
      return clone(next);
    },
    library(input) { return library.list(input); },
    async removeLibrary(input) {
      requireValue(object(input) && ['history','blocks'].includes(input.kind));
      if (input.kind === 'history') {
        requireValue(input.id == null || text(input.id));
        await library.removeHistory(input.id);
      } else {
        requireValue(input.block == null || object(input.block));
        await library.update(document => {document.blocks = input.block == null ? [] :
          document.blocks.filter(row => identity(row) !== identity(input.block));});
      }
      return library.list();
    },
    async updateManual(input) {
      requireValue(object(input) && ['save', 'remove'].includes(input.action));
      let previous, updated;
      await library.update(document => {
        if (input.action === 'remove') {
          requireValue(text(input.id));
          previous = document.manual.find(row => row?.id === input.id);
          document.manual = document.manual.filter(row => row?.id !== input.id);
        }
        else {
          requireValue(object(input.item) && text(input.item.targetId) &&
            (text(input.item.sourceKey) && isIdentifier(input.item.detailId) || object(input.item.share) && text(input.item.provider)));
          const id = input.item.id ?? randomUUID();
          requireValue(text(id));
          const matches = document.manual.filter(row => row?.id === id);
          requireValue(matches.length <= 1, 'CORRUPT_STORAGE');
          previous = matches[0];
          const item = { ...previous, ...clone(input.item), id, failureCount: 0 };
          for (const key of ['kind', 'title', 'name', 'sourceKey', 'detailId', 'provider', 'share',
            'seasonHint', 'enabled', 'autoDisable']) {
            if (!Object.hasOwn(input.item, key)) delete item[key];
          }
          if (object(previous?.share) && object(item.share)) {
            item.share = { ...previous.share, ...item.share };
            if (!Object.hasOwn(input.item.share, 'password')) delete item.share.password;
          }
          if (item.share !== undefined) {
            requireValue(object(item.share) && typeof item.share.url === 'string' &&
              (item.share.password === undefined || typeof item.share.password === 'string'));
            let url;
            try { url = new URL(item.share.url); } catch { throw fail('INVALID_REQUEST'); }
            requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password);
          }
          for (const key of ['enabled', 'autoDisable'])
            requireValue(item[key] === undefined || typeof item[key] === 'boolean');
          requireValue(item.seasonHint === undefined || Number.isSafeInteger(item.seasonHint) && item.seasonHint >= 0);
          updated = item;
          document.manual = [...document.manual.filter(row => row?.id !== item.id), item];
        }
      });
      if (previous && updated && manualIdentity(previous) === manualIdentity(updated) &&
          (previous.enabled !== false) === (updated.enabled !== false)) return library.read().manual;
      const id = (previous ?? updated)?.id;
      for (const session of sessions.values()) {
        if (!id || ![previous?.targetId, updated?.targetId].includes(session.target.id)) continue;
        const affectedPending = session.pending?.candidate.manualId === id;
        const active = session.runWork && !session.stopped;
        // Revoke publication before an old detail/resolve can complete. Its
        // finite producer may still populate the shared cache for the old key.
        if (affectedPending || active) { session.generation++; session.runKey = null; }
        const generation = session.generation;
        session.discoveryComplete = false;
        for (const [key, candidate] of session.candidates) {
          if (candidate.manualId === id) session.candidates.delete(key);
        }
        const removed = new Set();
        for (const result of session.results) {
          if (result.manualId !== id) continue;
          if (result.source.engine === 'pan') { removed.add(result.id); continue; }
          delete result.recognized; delete result.directories; delete result.browsing;
          delete result.manualId; delete result.manualIdentity; delete result.linkedTargetId;
          delete result.source.manualSeasonHint;
          if (result.detail) await candidatesFrom(session, result, result.detail);
        }
        if (removed.size) {
          session.results = session.results.filter(value => !removed.has(value.id));
          session.resultKeys = new Set(session.results.map(value => value.id));
          if (removed.has(session.browsing.sourceId)) {
            session.browseGeneration++;
            session.browsing = { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
          }
        }
        if ([...session.prefetch.values()].some(value => value.candidate?.manualId === id)) {
          detached(session, clearPrefetch(session));
        }
        await refreshBrowsing(session);
        if (closed || session.released || generation !== session.generation) continue;
        if (affectedPending || active) {
          void start(session, { prepareOnly: session.prepareOnly, constraint: session.constraint });
        }
        publish(session);
      }
      return library.read().manual;
    },
    async invalidate(domain, instances) {
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
        matchesAccount(value.descriptor?.scope?.accountProvider) || matchesAccount(value.descriptor?.scope?.fileProvider);
      for (const session of sessions.values()) {
        if (['tmdb', 'douban'].includes(domain)) session.metadataViews.clear();
        if (domain === session.searchMetadataTarget?.metadataProvider) session.searchUpdateEpoch++;
        if (domain === 'tmdb' && session.entryTarget.metadataProvider === 'douban' &&
            !session.target.metadataProvider && groupScopeAvailable(session) &&
            (session.entryResolution === 'loading' || session.entryResolution === 'error' ||
              session.target.kind === 'group' && !session.target.members.length)) {
          session.metadataEpoch++;
          if (session.stopped) continue;
          session.initializationError = null;
          session.error = null; session.entryResolution = 'loading';
          session.stage = 'loading';
          session.initialization = tracked(initialize(session, []));
          publish(session);
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
          matchesAccount(value.descriptor.scope?.accountProvider) || matchesAccount(value.descriptor.scope?.fileProvider) ||
          matchesAccount(value.candidate.provider) || matchesAccount(value.candidate.file?.provider)) ||
          [...session.candidates.values()].some(value => matchesAccount(value.provider) || matchesAccount(value.file?.provider)) ||
          [...session.prefetch.values()].some(value => value.candidate && matchesAttempt(value));
        if (!metadata && !source && !account && !associatedMetadata) continue;
        drains.push(cancelCalculations(session));
        if (session.lastFailed && matchesCandidate(session.lastFailed.candidate)) session.lastFailed = null;
        session.searchUpdateEpoch++;
        if (associatedMetadata && !metadata && !source && !account) {
          session.metadataSources = session.metadataSources.filter(row => row.provider !== domain);
          if (!session.metadataSources.some(row => row.id === session.metadataSourceId)) {
            session.metadata = null; session.metadataSourceId = null;
          }
          session.metadataEpoch++;
          if (session.target.metadataProvider && !session.stopped) detached(session, discoverMetadata(session));
          publish(session); continue;
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
            session.search.set(value.key, { sourceKey: value.key, page: 0, completed: false,
              loading: false, error: null, seen: new Set() });
          }
          session.results = session.results.filter(value => !matchesSource(value.source));
          session.resultKeys = new Set(session.results.map(value => value.id));
          if (session.target.kind === 'source') {
            const value = session.sources.find(row => row.key === session.target.source.key);
            if (value && value.enabled !== false) await addResults(session, value,
              [{ id: session.target.source.detailId, title: session.target.title }]);
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
        if (revoked.includes(session.pending)) session.pending = null;
        // Publish revocation before draining network resources, so a native
        // candidate cannot commit while its cleanup is waiting on transport.
        if (affectedBrowse) {
          session.browseGeneration++;
          session.browsing = { ...session.browsing, loading: false,
            error: metadata ? 'STALE_METADATA' : source ? 'STALE_SOURCE' : 'STALE_ACCOUNT', lines: [], episodes: [] };
        }
        session.discoveryComplete = false;
        if (metadata) {
          const epoch = ++session.metadataEpoch;
          const admitted = () => !session.released && !session.stopped && !closed && session.metadataEpoch === epoch;
          session.metadata = null;
          session.metadataSources = []; session.metadataSourceId = null;
          session.initializationError = null;
          session.initialization = tracked((async () => {
            try {
              if (!admitted()) return;
              const entry = await data.targetMetadataEntry(session.target, { rules: session.rules, canSchedule: admitted });
              if (!admitted()) return;
              const value = entry.value;
              session.metadata = value; session.error = null;
              session.browsing = { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
              const row = { provider: session.target.metadataProvider, metadataId: session.target.metadataId,
                kind: session.target.kind, title: session.target.title, metadata: value };
              session.metadataSourceId = metadataIdentity(row);
              session.metadataSources = [{ ...row, id: session.metadataSourceId }];
              session.playbackMetadataSourceId = session.metadataSourceId;
              session.metadata = metadataDisplay(session, row);
              acceptMetadataCache(session, entry);
              detached(session, discoverMetadata(session));
            } catch (error) {
              if (!admitted()) return;
              session.initializationError = session.error = codeOf(error);
            }
            publish(session);
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
        publish(session);
        const prefetch = [...session.prefetch.values()].some(value => value.candidate && matchesAttempt(value));
        drains.push(Promise.all([...revoked.map(value => retire(session, value)),
          ...(metadata || prefetch || interrupted ? [clearPrefetch(session)] : [])]).then(() => {
          if (resumeRun && valid(session, generation))
            void start(session, { prepareOnly: session.prepareOnly, constraint: session.constraint });
        }));
      }
      const results = await Promise.allSettled(drains);
      const failure = results.find(value => value.status === 'rejected');
      if (failure) throw failure.reason;
    },
    async updateSourceDisplay(engine, instances) {
      for (const session of sessions.values()) {
        let changed = false, eligibilityChanged = false;
        const policy = preferences();
        for (const source of session.sources) {
          if (source.engine !== engine) continue;
          const instance = instances.find(row => row.installationId === source.installationId);
          if (!instance) continue;
          const site = instance.sites?.find(row => row.key === source.siteId);
          const name = site?.name ?? instance.name;
          if (typeof name !== 'string' || name === source.name) continue;
          const allowed = isSmartSiteAllowed(source, policy);
          source.name = name; changed = true;
          eligibilityChanged ||= session.target.metadataProvider &&
            allowed !== isSmartSiteAllowed(source, policy);
          for (const result of session.results) if (result.source.key === source.key) result.source.name = name;
          for (const candidate of session.candidates.values()) if (candidate.source.key === source.key) candidate.source.name = name;
        }
        if (eligibilityChanged) await refreshEligibility(session);
        else if (changed) publish(session);
      }
    },
    close() {
      return closing ??= (async () => {
        closed = true;
        for (const controller of calculations) controller.abort();
        const results = await Promise.allSettled([...sessions.values()].map(session => releaseSession(session, {})));
        await Promise.allSettled([...jobs]);
        const failure = results.find(result => result.status === 'rejected');
        if (failure) throw failure.reason;
      })();
    },
  };
}
