import { randomUUID } from 'node:crypto';
import { Readable } from 'node:stream';
import { compareCandidates, defaultPreferences, isCandidateAllowed, isSmartSiteAllowed, normalizePreferences } from './policy.js';
import { matchesResult, matchesResults, recognizeCandidates } from './recognition.js';
import { buildEpisodeMap, resolveEpisode } from './episodes.js';
import { providerFamily, qualityKey } from './rules.js';
import { checkSource, checkTarget, clone, codeOf, fail, identity, isIdentifier, object, requireValue,
  resultDetailId, sourceRevision, text } from './identity.js';

const publicSource = source => fields(source, ['key', 'engine', 'installationId', 'siteId', 'revision',
  'generation', 'name', 'detailId']);
const publicCandidate = candidate => ({ ...fields(candidate, ['id', 'lineId', 'lineLabel', 'provider',
  'name', 'path', 'season', 'episode', 'globalEpisode', 'quality']), source: publicSource(candidate.source) });
const publicTarget = target => ({ ...fields(target, ['id', 'kind', 'title', 'year', 'metadataProvider',
  'metadataId', 'season', 'episode', 'globalEpisode', 'members']),
  ...(target.source ? { source: fields(target.source, ['key', 'detailId', 'lineId', 'candidateId']) } : {}) });
const sameEpisode = (a, b) => {
  if (a?.id !== b?.id || a?.kind !== b?.kind) return false;
  if (a?.source?.candidateId !== undefined && b?.source?.candidateId !== undefined)
    return a.source.candidateId === b.source.candidateId;
  if (a?.globalEpisode !== undefined && b?.globalEpisode !== undefined) return a.globalEpisode === b.globalEpisode;
  if (a?.episode !== undefined || b?.episode !== undefined) return a?.season === b?.season && a?.episode === b?.episode;
  return a?.source?.episodeId === b?.source?.episodeId && a?.source?.playId === b?.source?.playId;
};
const sameActualEpisode = (a, b) => a?.id === b?.id && a?.kind === b?.kind &&
  ['source', 'group'].includes(a?.kind) && (a.kind === 'group' || a.source?.key === b.source?.key &&
    a.source?.detailId === b.source?.detailId) &&
  (a.episode !== undefined ? a.season === b.season && a.episode === b.episode &&
    (a.globalEpisode === undefined || a.globalEpisode === b.globalEpisode)
    : a.globalEpisode !== undefined && a.globalEpisode === b.globalEpisode);
const cancelled = error => ['CLOSED', 'CANCELLED', 'STALE_ACCOUNT', 'STALE_SOURCE', 'STALE_SESSION', 'STALE_METADATA'].includes(error?.code);
const addressLifetime = 3 * 60_000;
function fields(value, names) {
  return Object.fromEntries(names.filter(name => value[name] !== undefined).map(name => [name, clone(value[name])]));
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
export function createPlaybackService({ data, library, media, rules, now = Date.now }) {
  const sessions = new Map(), jobs = new Set(), calculations = new Set();
  let closed = false, closing;
  requireValue(rules && typeof rules.snapshot === 'function');
  const preferences = () => library.read().preferences ?? defaultPreferences();
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
  function calculate(session, operation) {
    const controller = new AbortController();
    calculations.add(controller);
    const work = tracked((async () => {
      requireValue(!closed && !session.released, 'CANCELLED');
      return operation(controller.signal);
    })());
    session.calculations.set(controller, work);
    void work.finally(() => {
      session.calculations.delete(controller);
      calculations.delete(controller);
    }).catch(() => {});
    return work;
  }
  function cancelCalculations(session) {
    const pending = [...session.calculations];
    for (const [controller] of pending) controller.abort();
    return Promise.allSettled(pending.map(([, work]) => work));
  }
  function detached(session, work) {
    tracked(work).catch(error => {
      if (!session.released && !closed && !cancelled(error)) {
        session.error = codeOf(error); publish(session);
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
    const current = source.engine === 'pan' ? source : session.sources.find(value => value.key === source.key);
    return currentSource(session, source) && isSmartSiteAllowed(current, preferences()) &&
      (session.target.kind !== 'source' || source.key === session.target.source.key);
  }
  function searchComplete(session) {
    return session.target.kind === 'source' || session.sources.filter(source => sourceAllowed(session, source))
      .every(source => session.search.get(source.key).completed);
  }
  function visibleEpisodes(session, allSeasons = false) {
    if (['movie', 'group'].includes(session.target.kind)) return [];
    if (!allSeasons && (session.browsing.loading && !session.browsing.refreshing || session.browsing.error)) return [];
    const today = new Date(now() + 8 * 3_600_000).toISOString().slice(0, 10);
    const map = buildEpisodeMap(session.metadata);
    const season = session.browsing.season ?? session.target.season ?? map.find(row => row.season > 0)?.season;
    const next = session.metadata?.next_episode_to_air;
    const nextSeason = next?.season_number, nextEpisode = next?.episode_number;
    const hasNext = Number.isSafeInteger(nextSeason) && Number.isSafeInteger(nextEpisode) &&
      nextSeason > 0 && nextEpisode > 0 && session.metadata?.status !== 'Ended';
    const lastEpisode = next?.air_date && next.air_date <= today ? nextEpisode : nextEpisode - 1;
    return map.filter(row => {
      if (!allSeasons && row.season !== season) return false;
      if (session.previewEpisodes) return true;
      if (row.air_date && row.air_date > today) return false;
      return !hasNext || row.season < nextSeason || row.season === nextSeason && row.episode <= lastEpisode;
    });
  }
  function snapshot(session) {
    const candidates = [...session.candidates.values()];
    if (session.target.kind === 'movie') candidates.sort((a, b) => compareCandidates(a, b,
      { preferences: { ...preferences(), ruleOrder: ['quality', 'pan', 'keyword'] } }));
    const browsing = clone(session.browsing);
    if (browsing.sourceId !== null) {
      browsing.originalEpisodes = browsing.episodes;
      const season = browsing.season ?? session.target.season;
      if (session.target.kind === 'tv' && season !== undefined && browsing.episodes.some(row => row.season !== undefined))
        browsing.episodes = browsing.episodes.filter(row => row.season === season);
    }
    return {
      sessionId: session.id, sequence: session.sequence, rulesRevision: session.rules.revision, target: publicTarget(session.target),
      stage: session.stage, error: session.error, reportError: session.reportError, metadata: clone(session.metadata),
      metadataSources: session.metadataSources.map(row => fields(row, ['id', 'provider', 'metadataId', 'kind', 'title', 'seasons'])),
      metadataSourceId: session.metadataSourceId, metadataError: session.metadataError,
      metadataCache: metadataCacheState(session),
      metadataSupplement: metadataSupplement(session),
      sources: session.results.map(item => ({ id: item.id, title: item.title,
        source: publicSource(item.source), state: item.state, error: item.error })),
      search: [...session.search.values()].map(state => fields(state, ['sourceKey', 'page', 'completed', 'loading', 'error'])),
      browsing, episodes: visibleEpisodes(session), candidates: candidates.map(publicCandidate),
      attempt: session.pending && !session.pending.resourceReleased ? { id: session.pending.id, candidate: publicCandidate(session.pending.candidate),
        prepareOnly: session.pending.prepareOnly, position: session.pending.position, paused: session.pending.paused } : null,
      current: session.current && !session.current.resourceReleased ? { attemptId: session.current.id,
        candidate: publicCandidate(session.current.candidate), position: session.position,
        duration: session.duration, paused: session.paused } : null,
      constraint: clone(session.constraint), searchComplete: searchComplete(session),
      preferences: clone(preferences()), shortVideo: session.shortVideo, previewEpisodes: session.previewEpisodes,
      prefetch: [...session.prefetch.values()].map(value => ({ target: publicTarget(value.target),
        state: value.state, bytes: value.bytes ?? 0, error: value.error ?? null })),
    };
  }
  async function addResults(session, source, items, kind = 'result', admitted = () => true, trustedGroup = false) {
    const target = session.target.kind === 'group' ? session.target : { ...session.metadata, ...session.target };
    const matching = kind === 'result' && target.kind !== 'source' && !(target.kind === 'group' && trustedGroup)
      ? await calculate(session, signal => matchesResults(items, { target, rules: session.rules, signal })) : null;
    if (closed || session.released || session.stopped || !admitted()) return 0;
    let added = 0;
    for (const [index, raw] of items.entries()) {
      const detailId = resultDetailId(raw);
      if (!isIdentifier(detailId)) continue;
      if (session.target.kind === 'source' && (source.key !== session.target.source.key ||
          detailId !== session.target.source.detailId)) continue;
      const id = identity(source.key, detailId);
      if (session.target.kind === 'group' && matching?.[index]) addMember(session, source.key, detailId);
      if (session.resultKeys.has(id)) {
        const result = session.results.find(value => value.id === id);
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
        if (matching) result.match = { key: identity(result.raw, target), value: matching[index] };
        continue;
      }
      session.resultKeys.add(id);
      added++;
      session.results.push({ id, source: { ...source, detailId }, title: text(raw.vod_name ?? raw.name ?? raw.title),
        raw: clone(raw), ...(kind === 'hint' ? { hint: true } : kind === 'linked' ? { linked: true } : {}),
        ...(matching ? { match: { key: identity(raw, target), value: matching[index] } } : {}),
        state: 'idle', error: null });
    }
    return added;
  }
  function addMember(session, sourceKey, detailId) {
    if (!session.target.members.some(row => row.sourceKey === sourceKey && row.detailId === detailId))
      session.target.members.push({ sourceKey, detailId });
  }
  const manualIdentity = row => identity(fields(row, ['targetId', 'kind', 'sourceKey', 'detailId',
    'provider', 'share', 'seasonHint']));
  function currentManual(session, value) {
    return library.read().manual.find(row => row.id === value.manualId && row.enabled !== false &&
      row.targetId === session.target.id && manualIdentity(row) === value.manualIdentity);
  }
  async function recognizeResult(session, result, detail, target = session.target) {
    if (result.hint && !await smartResult(session, result, target)) return [];
    const source = { ...result.source };
    const manual = result.manualId ? currentManual(session, result) : null;
    if (result.manualId) {
      delete source.manualSeasonHint;
      if (manual?.seasonHint !== undefined) source.manualSeasonHint = manual.seasonHint;
    }
    return (await calculate(session, signal =>
      recognizeCandidates(detail, { target, source, metadata: session.metadata, rules: session.rules, signal })))
      .map(candidate => manual ? { ...candidate, manualId: manual.id, manualIdentity: manualIdentity(manual) } : candidate);
  }
  async function candidatesFrom(session, result, detail) {
    const target = session.target, metadata = session.metadata, generation = session.generation;
    const candidates = await recognizeResult(session, result, detail);
    if (closed || session.released || target !== session.target ||
        metadata !== session.metadata || generation !== session.generation || !currentSource(session, result.source)) return;
    for (const [id, candidate] of session.candidates) if (candidate.source.key === result.source.key &&
      candidate.source.detailId === result.source.detailId) session.candidates.delete(id);
    for (const candidate of candidates) session.candidates.set(candidate.id, candidate);
  }
  function excluded(session, candidate) {
    session.excluded.add(candidate.id);
    if (candidate.fileIdentity) session.excluded.add(candidate.fileIdentity);
  }
  async function smartResult(session, result, target = session.target) {
    const context = { target: target.kind === 'group' ? target : { ...session.metadata, ...target }, rules: session.rules };
    if (!result.hint) {
      if (target.kind === 'group') return target.members.some(member =>
        member.sourceKey === result.source.key && member.detailId === result.source.detailId);
      if (result.linkedTargetId === target.id && (!result.manualId || !!currentManual(session, result))) return true;
    }
    const raw = result.hint ? result.verifiedRaw : result.raw;
    const key = identity(raw, context.target);
    if (result.match?.key === key) return result.match.value;
    const entry = { key };
    result.match = entry;
    entry.value = calculate(session, signal => matchesResult(raw, { ...context, signal }));
    try { entry.value = await entry.value; }
    catch (error) {
      if (result.match === entry) delete result.match;
      throw error;
    }
    return entry.value;
  }
  async function allowed(session, candidate, { gate = true, explicit = false, target = session.target, retry = false } = {}) {
    if (candidate.share && !candidate.file) return false;
    if (!explicit && !sourceAllowed(session, candidate.source)) return false;
    const result = session.results.find(value => value.source.key === candidate.source.key &&
      value.source.detailId === candidate.source.detailId);
    if (result && (result.hint || !explicit) && !await smartResult(session, result, target)) return false;
    if (candidate.manualId && !currentManual(session, candidate)) return false;
    if (!isCandidateAllowed(candidate, { target, constraint: explicit ? { action: 'default' } : session.constraint,
      excluded: retry ? new Set() : session.excluded, blocks: library.read().blocks,
      actualEpisode: target === session.target && !explicit ? session.reselecting : null })) return false;
    if (target.kind === 'tv' && !explicit) {
      const mapped = buildEpisodeMap(session.metadata).find(row => row.season === candidate.season &&
        row.episode === candidate.episode);
      if (mapped && !visibleEpisodes(session, true).some(row => row.season === mapped.season && row.episode === mapped.episode)) return false;
    }
    if (target.kind === 'group' && !target.source && !explicit) {
      const first = result?.detail?.lines.find(line => line.id === candidate.lineId)?.episodes[0];
      if (!first || first.id !== candidate.episodeId) return false;
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
      const read = () => data.detail(result.source, result.source.detailId, { canSchedule: admitted, refresh });
      const value = await (browsing ? read() : runIO(session, generation, result.source, null, read));
      if (!admitted()) return null;
      // Cache entries are immutable. The session's expansion/display projection
      // is owned here, and must not mutate the shared source detail.
      result.detail = clone(value);
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
        if (session.target.kind === 'group' && await smartResult(session, result))
          addMember(session, result.source.key, detailId);
      }
      await candidatesFrom(session, result, result.detail);
      if (!admitted()) return null;
      if (result.hint) {
        requireValue(result.verifiedRaw, 'UPSTREAM_FORMAT');
        requireValue(await smartResult(session, result), 'NO_MATCH');
      }
      if (!admitted()) return null;
      result.state = 'ready'; publish(session);
      return result.detail;
    } catch (error) {
      if (admitted()) { result.state = 'error'; result.error = codeOf(error); publish(session); }
      return null;
    }
  }
  async function expandDetail(session, result, detail, generation) {
    for (let index = 0; index < detail.lines.length && valid(session, generation); index++) {
      const line = detail.lines[index];
      if (line.expanded || !line.episodes.some(item => item.share ||
        String(item.playId ?? item.id ?? item.url).startsWith('meow.pan.v1.'))) continue;
      try {
        const expanded = await runIO(session, generation, result.source, {
          accountProviders: line.episodes.map(item => item.file?.provider ?? item.share?.provider ?? item.provider ?? line.provider),
        }, () => data.expand(result.source, line, { canSchedule: () => valid(session, generation) }));
        if (!valid(session, generation)) return false;
        detail.lines[index] = expanded;
        await candidatesFrom(session, result, detail);
        if (!valid(session, generation)) return false;
        publish(session);
        if (await tryCandidate(session, generation)) return true;
      } catch (error) {
        if (valid(session, generation)) { result.error = codeOf(error); publish(session); }
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
    const continuation = sameEpisode(session.playbackTarget, session.target) ||
      session.reselecting && sameActualEpisode(session.playbackTarget, session.target);
    session.pending = { id: randomUUID(), candidate, descriptor, resource, preparedAt, generation, explicit,
      prepareOnly: session.prepareOnly, target: clone(session.target),
      position: continuation ? session.position
        : !session.resumeCandidateId || session.resumeCandidateId === candidate.id ? session.resumePosition : 0,
      paused: continuation ? session.paused : false };
    session.leases.set(session.pending.id, resource.id);
    session.stage = session.prepareOnly ? 'prepared' : 'pending'; session.error = null;
    publish(session);
    return true;
  }
  async function tryCandidate(session, generation) {
    while (valid(session, generation)) {
      let candidate = session.explicitCandidateId ? session.candidates.get(session.explicitCandidateId) : null;
      if (candidate && !await allowed(session, candidate, { explicit: true })) candidate = null;
      const explicit = !!candidate;
      if (!candidate) {
        const available = [...session.candidates.values()];
        const eligibility = await Promise.all(available.map(value => allowed(session, value)));
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
        return await runIO(session, generation, candidate.source, candidate, async () => {
          const descriptor = await data.resolve(candidate, { canSchedule: () => valid(session, generation) });
          if (!valid(session, generation)) return false;
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          return installPending(session, candidate, descriptor, resource, generation, now(), explicit);
        });
      } catch (error) {
        if (!valid(session, generation)) return false;
        excluded(session, candidate); session.error = codeOf(error); publish(session);
      }
    }
    return false;
  }
  async function consumeResult(session, result, generation) {
    if (!valid(session, generation) || !sourceAllowed(session, result.source)) return false;
    if (session.target.kind === 'source' && result.source.detailId !== session.target.source.detailId) return false;
    if (session.target.kind === 'group' && session.target.source && !session.reselecting &&
        (result.source.key !== session.target.source.key ||
          result.source.detailId !== session.target.source.detailId)) return false;
    if (!result.hint && !await smartResult(session, result)) return false;
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
    if (!state || state.completed) return false;
    if (state.work) return state.work;
    const epoch = session.searchGeneration;
    const admitted = () => !session.released && !closed && epoch === session.searchGeneration &&
      session.search.get(source.key) === state && currentSource(session, source);
    const work = (async () => {
      state.loading = true; state.error = null; publish(session);
      try {
        const page = await data.search(source, session.query, state.page + 1, { canSchedule: admitted });
        if (!admitted()) return false;
        const alreadyConfirmed = page.page <= state.page;
        const added = await addResults(session, source, page.items, 'result', admitted);
        if (!admitted()) return false;
        if (!alreadyConfirmed) {
          state.page = page.page;
          state.completed ||= !page.hasMore || !page.explicitPageCount && added === 0;
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
  function manualOutcome(row, error) {
    if (error && cancelled(error)) return;
    library.update(document => {
      const entry = document.manual.find(item => item.id === row.id);
      if (!entry || entry.enabled === false || manualIdentity(entry) !== manualIdentity(row)) return;
      entry.failureCount = error ? entry.failureCount + 1 : 0;
      if (entry.autoDisable && entry.failureCount >= 3) entry.enabled = false;
    });
  }
  async function run(session, generation) {
    try {
      await session.initialization;
      if (!valid(session, generation)) return;
      if (session.initializationError) throw fail(session.initializationError);
      if (session.target.kind === 'group' && !session.target.members.length) {
        session.stage = 'unavailable'; session.error = 'SOURCE_NOT_FOUND'; publish(session); return;
      }
      requireValue(session.target.kind !== 'tv' || session.target.episode !== undefined, 'NO_EPISODE_MAPPING');
      if (session.target.kind === 'tv' && session.target.metadataProvider === 'tmdb' && session.target.season !== undefined) {
        const target = session.target, epoch = session.metadataEpoch;
        const entry = await data.seasonMetadataEntry(target, target.season, { rules: session.rules,
          canSchedule: () => valid(session, generation) && epoch === session.metadataEpoch });
        if (!valid(session, generation) || epoch !== session.metadataEpoch) return;
        session.metadata = mergeSeason(target, session.metadata, target.season, entry.value);
        acceptMetadataCache(session, entry, target.season);
      }
      session.stage = 'cached'; publish(session);
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
        if (visited.has(key) || !result.hint && !await smartResult(session, result)) return false;
        visited.add(key);
        return consumeResult(session, result, generation);
      };
      const document = library.read();
      for (const stage of ['history', 'manual']) {
        if (!valid(session, generation)) return;
        session.stage = stage; publish(session);
        const rows = document[stage].filter(row => row.targetId === session.target.id && row.enabled !== false);
        for (const row of rows) {
          if (!valid(session, generation)) return;
          if (stage === 'manual' && row.share) {
            if (['source', 'group'].includes(session.target.kind)) continue;
            const source = { key: `manual:${row.id}`, engine: 'pan', name: row.name || '人工匹配',
              detailId: row.id, manualSeasonHint: row.seasonHint };
            try {
              const line = await runIO(session, generation, source, { provider: row.provider }, () =>
                data.expand(source, { id: row.id, label: row.name || row.provider,
                provider: row.provider, episodes: [{ id: row.id, name: row.name || session.target.title,
                  share: { ...row.share, provider: row.provider } }] },
                { canSchedule: () => valid(session, generation) }));
              if (!valid(session, generation)) return;
              await addResults(session, source, [{ id: row.id }]);
              const result = session.results.find(value => value.id === identity(source.key, row.id));
              Object.assign(result, { title: row.title || session.target.title, linkedTargetId: session.target.id,
                manualId: row.id, manualIdentity: manualIdentity(row), state: 'ready', error: null,
                detail: { lines: [line], detailId: row.id } });
              await candidatesFrom(session, result, result.detail);
              manualOutcome(row);
              if (await tryCandidate(session, generation)) return;
            } catch (error) {
              if (!valid(session, generation)) return;
              manualOutcome(row, error); session.error = codeOf(error); publish(session);
            }
            continue;
          }
          const source = session.sources.find(item => item.key === row.sourceKey);
          if (!source || !isIdentifier(row.detailId) || !sourceAllowed(session, source)) continue;
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
        session.stage = 'searching'; publish(session);
        // Already-returned search pages are shared input, not an instruction to
        // fetch every detail before history and manual matching get a chance.
        for (const result of session.results) {
          if (!valid(session, generation)) return;
          if (await consume(result)) return;
        }
        for (const source of session.sources) {
          if (!sourceAllowed(session, source)) continue;
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
      session.discoveryComplete = searchComplete(session) &&
        (await Promise.all(session.results.map(async result =>
          sourceAllowed(session, result.source) && await smartResult(session, result) ? result : null))).filter(Boolean)
          .every(result => result.state === 'ready' && !result.error);
      if (session.discoveryComplete && await tryCandidate(session, generation)) return;
      if (!valid(session, generation)) return;
      const donor = session.current;
      if (donor && !donor.resourceReleased && session.reselecting?.id === donor.candidate.id &&
          sameActualEpisode(donor.target, session.target)) {
        session.target = targetForCandidate(session.target, donor.candidate);
      }
      session.stage = 'unavailable';
      session.error ??= [...session.search.values()].find(value => value.error)?.error ?? 'NO_MATCH';
      publish(session);
    } catch (error) {
      if (valid(session, generation)) { session.stage = 'error'; session.error = codeOf(error); publish(session); }
    }
  }
  function start(session, { prepareOnly = false, constraint = { action: 'default' }, explicitCandidateId = null,
    reselecting = null } = {}) {
    const requestKey = identity(session.target, prepareOnly, constraint, explicitCandidateId, reselecting?.id);
    if (session.runKey === requestKey && session.runWork && !session.stopped) return session.runWork;
    const previous = session.pending;
    void cancelCalculations(session);
    session.generation++; session.stopped = false; session.prepareOnly = prepareOnly;
    session.pending = null; session.explicitCandidateId = explicitCandidateId;
    session.reselecting = reselecting;
    session.constraint = clone(constraint); session.actionCurrent = session.current?.candidate ?? session.actionCurrent;
    session.error = null; session.stage = 'cached'; publish(session);
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
    if (index < 0) rows.push(row);
    else rows[index] = row;
    rows.sort((a, b) => (a.season_number ?? a.season) - (b.season_number ?? b.season));
    return { ...metadata, [key]: rows };
  }
  function metadataCacheState(session) {
    const base = session.metadataCache;
    const season = session.metadataSeasonCache;
    if (!season) return clone(base);
    const error = base?.error ?? season.cache.error;
    return { ...clone(base ?? season.cache),
      status: base?.status === 'stale' || season.cache.status === 'stale' ? 'stale' : 'fresh',
      ...(error ? { error: clone(error) } : {}),
      components: { ...clone(base?.components ?? {}),
        [`season:${season.season}`]: { status: season.cache.error ? 'failed' : 'ready',
          fetchedAt: season.fetchedAt, ...(season.cache.error ? { error: clone(season.cache.error) } : {}) } } };
  }
  function acceptMetadataCache(session, entry, season) {
    if (season === undefined) {
      session.metadataCache = entry?.cache ?? null;
      session.metadataSeasonCache = null;
    } else session.metadataSeasonCache = { season, cache: entry.cache, fetchedAt: entry.fetchedAt };
    session.metadataError = metadataCacheState(session)?.error?.code ?? null;
  }
  const metadataIdentity = row => identity(row.provider, row.kind, String(row.metadataId));
  const metadataDisplay = row => row.provider === 'douban' && row.seasons?.length
    ? { ...row.metadata, doubanSeasons: row.seasons } : row.metadata;
  function metadataSupplement(session) {
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    if (selected?.provider !== 'douban') return null;
    const associated = session.metadataSources.filter(row => row.provider === 'tmdb' && row.kind === selected.kind &&
      (row.association?.targetProvider === 'douban' &&
        String(row.association.targetMetadataId) === String(selected.metadataId) ||
       selected.association?.targetProvider === 'tmdb' &&
        String(selected.association.targetMetadataId) === String(row.metadataId)));
    return associated.length === 1
      ? fields(associated[0], ['provider', 'metadataId', 'kind', 'metadata']) : null;
  }
  function initializeEpisode(session, previous) {
    if (session.target.episode === undefined) {
      const episode = session.target.globalEpisode === undefined && previous ? previous.episode
        : resolveEpisode(buildEpisodeMap(session.metadata), { globalEpisode: session.target.globalEpisode ?? 1 });
      if (episode && (session.target.globalEpisode === undefined || session.target.season === undefined ||
          session.target.season === episode.season)) session.target = episodeTarget(session.target, episode);
    }
    if (previous && session.target.episode !== undefined && session.target.season === previous.episode?.season &&
        session.target.episode === previous.episode?.episode &&
        (session.target.globalEpisode === undefined || previous.episode?.globalEpisode === undefined ||
          session.target.globalEpisode === previous.episode.globalEpisode)) session.resumePosition = previous.position;
  }
  async function discoverMetadata(session) {
    const epoch = session.metadataEpoch, browsing = session.browseGeneration;
    const admitted = () => !session.released && !session.stopped && !closed &&
      epoch === session.metadataEpoch && browsing === session.browseGeneration;
    try {
      const rows = await data.metadataSources(session.target, {
        metadata: session.metadata, rules: session.rules, canSchedule: admitted });
      if (!admitted()) return;
      const sources = rows.map(row => ({ ...clone(row), id: metadataIdentity(row) }));
      // A successful discovery is the current proof set. Retaining an omitted
      // association would keep showing a supplement after it became ambiguous.
      session.metadataSources = sources;
      const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
      if (selected?.provider === 'douban' && selected.seasons) {
        session.metadata = { ...session.metadata, doubanSeasons: selected.seasons };
      }
      session.metadataError = metadataCacheState(session)?.error?.code ?? null;
      if (session.target.kind === 'tv' && session.target.episode === undefined && session.generation === 0) {
        const previous = session.platform === 'windows'
          ? library.read().history.find(row => row.targetId === session.target.id) : null;
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
  async function browseMetadata(session, id, keepSource = false) {
    const selected = session.metadataSources.find(row => row.id === id);
    requireValue(selected, 'NOT_FOUND');
    const selectedSeason = id === session.metadataSourceId ? session.browsing.season : undefined;
    session.userSelected = true;
    const generation = ++session.browseGeneration;
    const epoch = ++session.metadataEpoch;
    const admitted = () => !session.released && !closed &&
      generation === session.browseGeneration && epoch === session.metadataEpoch;
    session.generation++; session.runKey = null;
    const pending = session.pending; session.pending = null;
    if (pending) detached(session, retire(session, pending));
    detached(session, clearPrefetch(session));
    session.browsing = keepSource ? { ...session.browsing, loading: true, error: null }
      : { sourceId: null, lineId: null, loading: true, error: null, lines: [], episodes: [] };
    publish(session);
    try {
      const target = { ...session.target, kind: selected.kind,
        metadataProvider: selected.provider, metadataId: selected.metadataId };
      delete target.members; delete target.source;
      const entry = await data.targetMetadataEntry(target, { rules: session.rules, canSchedule: admitted });
      if (!admitted()) return;
      const metadata = entry.value;
      let display = metadataDisplay({ ...selected, metadata });
      const coordinates = session.target.kind === 'group' ? {}
        : session.target.globalEpisode !== undefined ? { globalEpisode: session.target.globalEpisode }
        : id === session.metadataSourceId ? fields(session.target, ['season', 'episode']) : {};
      let mapped = resolveEpisode(buildEpisodeMap(display), coordinates);
      const season = selectedSeason ?? mapped?.season ?? (id === session.metadataSourceId
        ? coordinates.season ?? session.browsing.season : undefined);
      if (target.kind === 'tv' && season !== undefined) {
        const value = await data.seasonMetadataEntry(target, season, { rules: session.rules, canSchedule: admitted });
        if (!admitted()) return;
        display = mergeSeason(target, display, season, value.value);
        acceptMetadataCache(session, entry);
        acceptMetadataCache(session, value, season);
        mapped = resolveEpisode(buildEpisodeMap(display), coordinates);
      } else acceptMetadataCache(session, entry);
      selected.metadata = metadata;
      session.metadata = display; session.metadataSourceId = id;
      if (!await selectTarget(session, episodeTarget(target, mapped ?? {})) || !admitted()) return;
      session.browsing = { ...session.browsing, season, error: null };
      await refreshBrowsing(session);
      if (!admitted()) return;
      session.browsing = { ...session.browsing, loading: false };
      session.stage = session.current && !session.current.resourceReleased ? 'playing' : 'idle';
    } catch (error) {
      if (!admitted()) return;
      session.browsing = { ...session.browsing, loading: false, error: codeOf(error), episodes: [] };
    }
    publish(session);
  }
  async function initialize(session, cacheEntries) {
    const epoch = session.metadataEpoch;
    const admitted = () => !session.released && !session.stopped && !closed && session.metadataEpoch === epoch &&
      (session.target.kind !== 'group' || session.target.members.length > 0);
    try {
      if (cacheEntries.length) await data.handoff(cacheEntries, { canSchedule: admitted });
      if (!admitted()) return;
      // Existing metadata may render immediately, but only the data owner can
      // decide that a complete, correctly scoped and still-fresh entry exists.
      const entry = await data.targetMetadataEntry(session.target, { rules: session.rules, canSchedule: admitted });
      if (!admitted()) return;
      const metadata = entry?.value ?? null;
      session.metadata = metadata;
      acceptMetadataCache(session, entry);
      if (session.target.metadataProvider) {
        const row = { provider: session.target.metadataProvider, metadataId: session.target.metadataId,
          kind: session.target.kind, title: session.target.title, metadata };
        session.metadataSourceId = metadataIdentity(row);
        session.metadataSources = [{ ...row, id: session.metadataSourceId }];
        // Association lookup must not delay the primary source's automatic
        // preparation. Failure only affects the optional metadata selector.
        detached(session, discoverMetadata(session));
      }
      const previous = session.platform === 'windows' ? library.read().history.find(row => row.targetId === session.target.id) : null;
      if (session.target.kind === 'tv') {
        initializeEpisode(session, session.generation === 0 ? previous : null);
        if (session.target.metadataProvider === 'tmdb' && session.target.season !== undefined) {
          const target = session.target;
          const season = await data.seasonMetadataEntry(target, target.season, { rules: session.rules, canSchedule: admitted });
          if (!admitted()) return;
          session.metadata = mergeSeason(target, session.metadata, target.season, season.value);
          acceptMetadataCache(session, season, target.season);
        }
      } else if (previous) {
        if (!['source', 'group'].includes(session.target.kind) || session.target.source?.playId === undefined &&
            session.target.source?.episodeId === undefined &&
            (session.target.source?.candidateId === undefined || session.target.source.candidateId === previous.candidateId) &&
            (session.target.source?.lineId === undefined || session.target.source.lineId === previous.lineId)) {
          session.resumePosition = previous.position;
          if (['source', 'group'].includes(session.target.kind)) session.resumeCandidateId = previous.candidateId;
        }
      }
      if (session.released || session.generation !== 0) return;
      session.stage = 'idle'; publish(session);
      const autoPrepare = session.platform === 'ios' && !!session.target.metadataProvider && preferences().iosAutoPrepare;
      // An explicit global coordinate is not an instruction to fall back to
      // the first episode or another episode's historical position.
      if (session.target.kind === 'tv' && session.target.episode === undefined &&
          (session.target.globalEpisode !== undefined || session.platform === 'windows' || autoPrepare)) {
        session.stage = 'unavailable'; session.error = 'NO_EPISODE_MAPPING'; publish(session); return;
      }
      if (session.platform === 'windows' || autoPrepare) {
        void start(session, { prepareOnly: autoPrepare });
      }
    } catch (error) {
      if (admitted()) {
        session.initializationError = codeOf(error);
        if (session.generation === 0) {
          session.stage = 'error'; session.error = codeOf(error); publish(session);
        }
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
    const target = checkTarget(input.target), sources = (input.sources ?? []).map(checkSource);
    requireValue(new Set(sources.map(source => source.key)).size === sources.length);
    if (target.kind === 'group') requireValue(target.members.every(member =>
      sources.some(source => source.key === member.sourceKey)), 'SOURCE_NOT_FOUND');
    const selectionMembers = checkSearchHandoff(input, searchHandoff, target.id, sources);
    // Preserve the supplied current/resume target. A handoff's full raw pages
    // supply menus, not another decision about that group's membership.
    const trustedGroup = target.kind === 'group' && selectionMembers !== undefined;
    const capturedRules = ruleSnapshot ?? await rules.snapshot();
    requireValue(!closed, 'CLOSED');
    const session = { id: randomUUID(), target, platform: input.platform, query: text(input.query) || target.title,
      selectionId: target.id, selectionKind: target.kind, searchRevision: -1, searchUpdateEpoch: 0,
      sourceScope: sources.map(source => clone(source)),
      userSelected: target.kind === 'group' && ['candidateId', 'playId', 'episodeId']
        .some(field => target.source?.[field] !== undefined),
      sources, metadata: clone(input.metadata ?? null), rules: capturedRules,
      calculations: new Map(),
      metadataSources: [], metadataSourceId: null, metadataError: null,
      metadataCache: null, metadataSeasonCache: null, lastFailed: null,
      initializationError: null,
      results: [], resultKeys: new Set(), candidates: new Map(), excluded: new Set(), listeners: new Set(),
      search: new Map(sources.map(source => [source.key, { sourceKey: source.key, page: 0, completed: false,
        loading: false, error: null }])),
      sequence: 0, generation: 0, metadataEpoch: 0, browseGeneration: 0, searchGeneration: 0,
      stage: 'loading', error: null, reportError: null,
      browsing: { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] },
      stopped: false, released: false, pending: null, current: null, constraint: { action: 'default' },
      prepareOnly: false, resumePosition: 0, position: 0, duration: 0, paused: false,
      shortVideo: false, previewEpisodes: (library.read().previewTargets ?? []).includes(target.id),
      lastHistoryWrite: 0, discoveryComplete: false,
      prefetch: new Map(), leases: new Map(), prefetchGeneration: 0, reportQueue: Promise.resolve() };
    for (const result of input.results ?? []) {
      const source = sources.find(value => value.key === result.sourceKey);
      if (source) await addResults(session, source, result.items ?? [], 'result', undefined, trustedGroup);
    }
    for (const result of input.resultHints ?? []) {
      const source = sources.find(value => value.key === result.sourceKey);
      if (source) await addResults(session, source, result.items ?? [], 'hint');
    }
    for (const value of input.search ?? []) {
      const state = session.search.get(value.sourceKey);
      if (state) {
        requireValue(Number.isSafeInteger(value.page) && value.page >= 0 && typeof value.completed === 'boolean');
        state.page = value.page; state.completed = value.completed;
      }
    }
    if (target.kind === 'source') {
      const source = sources.find(value => value.key === target.source.key);
      requireValue(source, 'SOURCE_NOT_FOUND');
      await addResults(session, source, [{ id: target.source.detailId, title: target.title }], 'linked');
    } else if (target.kind === 'group') {
      for (const member of target.members) {
        const source = sources.find(value => value.key === member.sourceKey);
        await addResults(session, source, [{ detailId: member.detailId }], 'linked');
      }
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
    if (session.selectionKind !== 'group' && carrier && (carrier.kind !== session.target.kind ||
        carrier.metadataProvider !== session.target.metadataProvider ||
        String(carrier.metadataId) !== String(session.target.metadataId))) carrier = undefined;
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
      await addResults(session, inScope(result.sourceKey), result.items, 'result', admitted, trustedGroup);
      if (!admitted()) return;
    }
    for (const result of input.resultHints ?? []) {
      await addResults(session, inScope(result.sourceKey), result.items, 'hint', admitted);
      if (!admitted()) return;
    }
    for (const value of input.search) {
      if (session.target.kind === 'source' && value.sourceKey !== session.target.source.key) continue;
      const state = session.search.get(value.sourceKey);
      if (value.page >= state.page) {
        state.page = value.page; state.completed ||= value.completed; state.error = null;
      }
    }
    session.discoveryComplete = false;
    publish(session);
    const seeds = entries.filter(entry => session.selectionKind !== 'source' ||
      entry.kind === 'detail' && entry.source.key === session.target.source.key &&
        entry.detailId === session.target.source.detailId);
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
        if (session.target.kind !== 'group' || session.pending || session.current || session.userSelected) {
          // A proven late association supplies display data, not authority to
          // reinterpret the playing raw file as a particular metadata episode.
          if (session.target.kind === 'group') {
            session.metadata = value; session.metadataSourceId = row.id;
            acceptMetadataCache(session, entry);
          } else if (session.metadataSourceId === null || row.id === session.metadataSourceId) {
            let display = value;
            let seasonEntry;
            const season = session.browsing.season ?? session.target.season;
            if (carrier.kind === 'tv' && carrier.metadataProvider === 'tmdb' && season !== undefined) {
              const generation = session.browseGeneration;
              const details = await data.seasonMetadataEntry(carrier, season, { rules: session.rules,
                canSchedule: () => admitted() && generation === session.browseGeneration });
              if (!admitted() || generation !== session.browseGeneration) return;
              display = mergeSeason(carrier, value, season, details.value);
              seasonEntry = details;
            }
            session.metadata = display; session.metadataSourceId = row.id; session.initializationError = null;
            acceptMetadataCache(session, entry);
            if (seasonEntry) acceptMetadataCache(session, seasonEntry, season);
          }
          publish(session); return;
        }
        session.generation++; session.runKey = null; session.metadataEpoch++;
        session.metadata = value; session.metadataSourceId = row.id; session.initializationError = null;
        acceptMetadataCache(session, entry);
        const first = carrier.kind === 'tv' ? resolveEpisode(buildEpisodeMap(value), { globalEpisode: 1 }) : null;
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
  function browseEpisodes(session, result, line) {
    if (!line) return [];
    const candidates = new Map([...session.candidates.values()].filter(item => item.source.key === result.source.key &&
      item.source.detailId === result.source.detailId && item.lineId === line.id).map(item => [item.episodeId, item]));
    return line.episodes.map(value => {
      const candidate = candidates.get(value.id);
      return { id: candidate?.id ?? identity(result.id, line.id, value.id), ...fields(value, ['name', 'path', 'index']),
        ...(candidate ? { candidateId: candidate.id, ...fields(candidate, ['season', 'episode', 'globalEpisode']) } : {}) };
    });
  }
  async function refreshBrowsing(session) {
    const browsing = session.browsing, generation = session.browseGeneration;
    const result = session.results.find(row => row.id === browsing.sourceId);
    if (!result?.detail) return;
    const target = session.target, metadata = session.metadata;
    await candidatesFrom(session, result, result.detail);
    if (closed || session.released || session.browsing !== browsing || generation !== session.browseGeneration ||
        target !== session.target || metadata !== session.metadata || !currentSource(session, result.source)) return;
    const line = result.detail.lines.find(row => row.id === browsing.lineId);
    browsing.episodes = browseEpisodes(session, result, line);
  }
  async function browse(session, resultId, lineId, { refresh = false, retainDisplay = false } = {}) {
    const generation = ++session.browseGeneration;
    if (!resultId) {
      session.browsing = { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
      publish(session);
      if (session.target.metadataProvider) await refreshMetadataDisplay(session);
      return;
    }
    const result = session.results.find(value => value.id === resultId);
    requireValue(result, 'NOT_FOUND');
    const admitted = () => !session.released && !closed && generation === session.browseGeneration &&
      currentSource(session, result.source);
    const previous = session.browsing;
    const retained = retainDisplay && previous.sourceId === resultId &&
      (lineId === undefined || lineId === previous.lineId);
    const priorDetail = retained ? result.detail : null;
    session.browsing = retained ? { ...previous, loading: true, refreshing: true, error: null }
      : { sourceId: resultId, lineId: lineId ?? null, loading: true,
        error: null, lines: [], episodes: [] };
    publish(session);
    try {
      let detail;
      if (result.source.engine === 'pan') {
        const row = currentManual(session, result);
        requireValue(row?.share, 'NOT_FOUND');
        const line = await data.expand(result.source, { id: row.id, label: row.name || row.provider,
          provider: row.provider, episodes: [{ id: row.id, name: row.name || session.target.title,
            share: { ...row.share, provider: row.provider } }] }, { canSchedule: admitted, refresh });
        if (!admitted()) return;
        detail = { lines: [line], detailId: row.id };
        result.detail = detail; result.state = 'ready'; result.error = null;
        await candidatesFrom(session, result, detail);
      } else detail = await loadDetail(session, result, generation, true, refresh);
      if (!admitted()) return;
      if (!detail) throw fail(result.error ?? 'UPSTREAM_ERROR');
      let line = lineId === undefined ? detail.lines[0] : detail.lines.find(value => value.id === lineId);
      if (lineId !== undefined) requireValue(line, 'NOT_FOUND');
      if (line) {
        if (result.source.engine !== 'pan') line = await data.expand(result.source, line, { canSchedule: admitted, refresh });
        if (!admitted()) return;
        detail.lines[detail.lines.findIndex(value => value.id === line.id)] = line;
        await candidatesFrom(session, result, detail);
      }
      if (!admitted()) return;
      session.browsing = { sourceId: resultId, lineId: line?.id ?? null, loading: false, error: null,
        lines: detail.lines.map(value => fields(value, ['id', 'label', 'provider'])),
        episodes: browseEpisodes(session, result, line) };
      if (retained) session.error = null;
    } catch (error) {
      if (!admitted()) return;
      if (retained) {
        if (priorDetail) {
          result.detail = priorDetail;
          await candidatesFrom(session, result, priorDetail);
          if (!admitted()) return;
        }
        session.browsing = { ...previous, loading: false, refreshing: false };
        session.error = codeOf(error);
      } else session.browsing = { ...session.browsing, loading: false, error: codeOf(error), episodes: [] };
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
    try {
      const entry = await data.targetMetadataEntry(target, { rules: session.rules, canSchedule: admitted, refresh });
      if (!admitted()) return;
      let display = entry.value;
      const requestedSeason = season ?? previous.season ?? session.target.season;
      let seasonEntry;
      const declaredSeason = (display.seasons ?? display.doubanSeasons ?? [])
        .some(row => (row.season_number ?? row.season) === requestedSeason);
      if (target.kind === 'tv' && requestedSeason !== undefined && declaredSeason) {
        seasonEntry = await data.seasonMetadataEntry(target, requestedSeason,
          { rules: session.rules, canSchedule: admitted, refresh, requestedEpisode });
        if (!admitted()) return;
        display = mergeSeason(target, display, requestedSeason, seasonEntry.value);
      }
      session.metadata = display;
      acceptMetadataCache(session, entry);
      if (seasonEntry) acceptMetadataCache(session, seasonEntry, requestedSeason);
      if (selected) {
        selected.metadata = entry.value;
        selected.seasons = display.seasons ?? display.doubanSeasons;
      }
      session.browsing = { ...previous, loading: false, refreshing: false, error: null };
      session.initializationError = null;
      if (session.target.metadataProvider) detached(session, discoverMetadata(session));
      // A data refresh updates presentation only. An already accepted file,
      // its local/global coordinates and its native lease keep their identity.
    } catch (error) {
      if (!admitted()) return;
      session.browsing = { ...previous, loading: false, refreshing: false };
      session.metadataError = codeOf(error);
    }
    publish(session);
  }
  async function refreshBrowse(session, refresh) {
    await session.initialization;
    if (session.released || session.stopped || closed) return;
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
  function saveHistory(session, selected = session.current, ended = false) {
    if (!selected) return;
    const candidate = selected.candidate;
    library.update(document => {
      const value = { targetId: selected.target.id, title: selected.target.title,
        sourceKey: candidate.source.key, detailId: candidate.source.detailId,
        lineId: candidate.lineId, candidateId: candidate.id,
        episode: fields(selected.target, ['season', 'episode', 'globalEpisode']),
        position: session.position, duration: session.duration, paused: session.paused, ended,
        updatedAt: now() };
      document.history = [value, ...document.history.filter(row => row.targetId !== value.targetId)];
    });
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
    const items = visibleEpisodes(session, true);
    const index = items.findIndex(value => value.globalEpisode !== undefined && value.globalEpisode === session.target.globalEpisode ||
      value.season === session.target.season && value.episode === session.target.episode);
    return index < 0 || !items[index + direction] ? null : { target: episodeTarget(session.target, items[index + direction]) };
  }
  async function selectTarget(session, target) {
    const generation = session.generation;
    if (session.target.kind === 'group' && ['movie', 'tv'].includes(target.kind)) {
      for (const result of session.results) {
        if (!result.hint && session.target.members.some(member => member.sourceKey === result.source.key &&
            member.detailId === result.source.detailId)) result.linkedTargetId = target.id;
      }
    }
    if (!sameEpisode(session.target, target)) {
      session.excluded.clear(); session.candidates.clear(); session.resumePosition = 0;
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
    if (target.kind === 'group') requireValue(target.members.some(member =>
      member.sourceKey === candidate.source.key && member.detailId === candidate.source.detailId), 'NO_MATCH');
    const next = episodeTarget(target, candidate);
    if (['source', 'group'].includes(next.kind)) {
      next.source = { ...next.source, key: candidate.source.key, detailId: candidate.source.detailId,
        lineId: candidate.lineId, episodeId: candidate.episodeId, candidateId: candidate.id };
      delete next.source.playId;
      if (candidate.playId !== undefined) next.source.playId = candidate.playId;
    }
    return next;
  }
  function reselectActualFile(session, selected = session.pending ?? session.current) {
    if (!['source', 'group'].includes(session.target.kind)) return false;
    const candidate = selected?.candidate ?? session.actionCurrent;
    if (!candidate || !selected && (candidate.targetId !== session.target.id ||
        candidate.kind !== session.target.kind || candidate.id !== session.target.source?.candidateId)) return false;
    // Only observed coordinates can prove another raw file is the same episode.
    // With no such evidence the exact saved selector remains, so exclusion
    // produces NO_MATCH instead of treating the next list row as an alternative.
    if (candidate.episode === undefined && candidate.globalEpisode === undefined) return null;
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
      await refreshMetadataDisplay(session, { season: session.target.season,
        requestedEpisode: session.target.episode === undefined ? undefined : session.target.episode + 1 });
      if (!valid(session, generation) || session.target !== target || adjacent(session, 1)) return;
      const nextSeason = (session.metadata?.seasons ?? session.metadata?.doubanSeasons ?? [])
        .map(row => row.season_number ?? row.season)
        .filter(season => Number.isSafeInteger(season) && season > session.target.season).sort((a, b) => a - b)[0];
      if (nextSeason !== undefined) await refreshMetadataDisplay(session, { season: nextSeason, requestedEpisode: 1 });
      return;
    }
    if (!['source', 'group'].includes(session.target.kind)) return;
    const selected = session.current ?? session.pending;
    if (!selected) return;
    const candidate = selected.candidate, generation = session.generation;
    const result = session.results.find(row => row.source.key === candidate.source.key &&
      row.source.detailId === candidate.source.detailId);
    if (!result || result.source.engine === 'pan') {
      // Manual shares use the same directory owner as an explicitly browsed list.
      if (result) await browse(session, result.id, candidate.lineId, { retainDisplay: true });
      return;
    }
    const admitted = () => valid(session, generation) && currentSource(session, result.source) &&
      (session.current === selected || session.pending === selected);
    const previous = result.detail;
    try {
      const detail = await loadDetail(session, result, generation);
      if (!detail || !admitted()) return;
      const index = detail.lines.findIndex(row => row.id === candidate.lineId);
      if (index < 0) return;
      const line = await data.expand(result.source, detail.lines[index], { canSchedule: admitted });
      if (!admitted()) return;
      detail.lines[index] = line;
      await candidatesFrom(session, result, detail);
      if (!admitted()) return;
      await refreshBrowsing(session);
      if (!admitted()) return;
    } catch (error) {
      if (!admitted()) return;
      result.detail = previous;
      if (previous) await candidatesFrom(session, result, previous);
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
          const descriptor = await data.refresh(selected.candidate, { canSchedule: () => valid(session, generation) });
          if (!valid(session, generation)) return;
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          if (await installPending(session, selected.candidate, descriptor, resource, generation, now(), true)) {
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
          await smartResult(session, result) ? recognizeResult(session, result, result.detail, neighbor.target) : []))).flat();
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
    try { saveHistory(session); } catch (caught) { error = caught; }
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
    switch (input.action) {
      case 'prepare':
      case 'play': {
        const generation = session.generation, target = session.target, pending = session.pending;
        const eligible = pending && sameEpisode(pending.target, target) && preparedValid(pending) &&
          await allowed(session, pending.candidate, { gate: false, explicit: pending.explicit });
        if (closed || session.released || generation !== session.generation || pending !== session.pending ||
            target !== session.target) break;
        if (eligible) {
          pending.prepareOnly = input.action === 'prepare';
          session.stage = input.action === 'prepare' ? 'prepared' : 'pending'; publish(session);
        } else if (input.action === 'play' && session.current && !session.current.resourceReleased &&
            sameEpisode(session.current.target, session.target)) {
          // Transport pause/resume remains with the native owner. A repeated
          // page play action must not resolve and replace the same playing item.
          session.stage = 'playing'; publish(session);
        } else void start(session, { prepareOnly: input.action === 'prepare' });
        break;
      }
      case 'select.episode': {
        requireValue(object(input.episode));
        let episode = resolveEpisode(buildEpisodeMap(session.metadata), input.episode);
        if (!episode && session.target.metadataProvider) {
          const { season, episode: number, globalEpisode } = input.episode;
          requireValue(season !== undefined || number !== undefined
            ? Number.isSafeInteger(season) && season >= 0 && Number.isSafeInteger(number) && number >= 0
            : Number.isSafeInteger(globalEpisode) && globalEpisode > 0, 'NO_MATCH');
          const generation = session.generation, target = session.target;
          await refreshMetadataDisplay(session, { season: input.episode.season,
            requestedEpisode: input.episode.episode });
          if (!valid(session, generation) || session.target !== target) break;
          episode = resolveEpisode(buildEpisodeMap(session.metadata), input.episode);
        }
        requireValue(episode && visibleEpisodes(session, true).some(value => value.season === episode.season &&
          value.episode === episode.episode), 'NO_MATCH');
        session.userSelected = true;
        if (!await selectTarget(session, episodeTarget(session.target, episode))) break;
        void start(session); break;
      }
      case 'browse.season': {
        requireValue(Number.isSafeInteger(input.season) && input.season >= 0);
        requireValue(session.target.kind === 'tv' && session.target.metadataProvider, 'NOT_FOUND');
        const hasSeason = () => (session.metadata?.seasons ?? session.metadata?.doubanSeasons ?? [])
          .some(row => (row.season_number ?? row.season) === input.season);
        if (!hasSeason()) {
          const epoch = session.metadataEpoch, browsing = session.browseGeneration;
          const entry = await data.targetMetadataEntry(session.target, { rules: session.rules,
            canSchedule: () => !closed && !session.released &&
              session.metadataEpoch === epoch && session.browseGeneration === browsing });
          if (closed || session.released || session.metadataEpoch !== epoch || session.browseGeneration !== browsing) break;
          session.metadata = entry.value;
          acceptMetadataCache(session, entry);
        }
        requireValue(hasSeason(), 'NOT_FOUND');
        const generation = ++session.browseGeneration, epoch = session.metadataEpoch;
        const admitted = () => !session.released && !closed &&
          generation === session.browseGeneration && epoch === session.metadataEpoch;
        session.browsing = { ...session.browsing, season: input.season, loading: true, error: null, episodes: [] };
        publish(session);
        detached(session, (async () => {
          try {
            const target = session.target;
            const season = await data.seasonMetadataEntry(target, input.season, { rules: session.rules, canSchedule: admitted });
            if (!admitted()) return;
            session.metadata = mergeSeason(target, session.metadata, input.season, season.value);
            acceptMetadataCache(session, season, input.season);
            await refreshBrowsing(session);
            if (!admitted()) return;
            session.browsing = { ...session.browsing, loading: false, error: null };
          } catch (error) {
            if (!admitted()) return;
            session.browsing = { ...session.browsing, loading: false, error: codeOf(error), episodes: [] };
          }
          publish(session);
        })());
        break;
      }
      case 'next': case 'previous':
        await moveEpisode(session, input.action === 'next' ? 1 : -1); break;
      case 'preview':
        requireValue(typeof input.enabled === 'boolean');
        library.update(document => {
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
      case 'select.candidate': {
        const candidate = session.candidates.get(input.candidateId);
        requireValue(candidate, 'NOT_FOUND');
        const target = targetForCandidate(session.target, candidate);
        session.userSelected = true;
        if (!await selectTarget(session, target)) break;
        void start(session, { explicitCandidateId: candidate.id }); break;
      }
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
        library.update(document => {
          const block = { targetId: selected.target.id, sourceKey: candidate.source.key,
            detailId: candidate.source.detailId, lineId: candidate.lineId };
          if (!document.blocks.some(row => identity(row) === identity(block))) document.blocks.push(block);
        });
        if (!sameEpisode(session.target, selected.target) &&
            !await selectTarget(session, clone(selected.target))) break;
        excluded(session, candidate);
        void start(session, { reselecting: reselectActualFile(session, selected) }); break;
      }
      case 'browse': detached(session, browse(session, input.sourceId, input.lineId)); break;
      case 'browse.refresh':
        requireValue(input.refresh === undefined || typeof input.refresh === 'boolean');
        detached(session, refreshBrowse(session, input.refresh !== false)); break;
      case 'media.retry':
        requireValue(input.attemptId === undefined || text(input.attemptId));
        await retryMedia(session, input.attemptId); break;
      case 'browse.metadata':
        requireValue(text(input.metadataSourceId) &&
          (input.keepSource === undefined || typeof input.keepSource === 'boolean'));
        detached(session, browseMetadata(session, input.metadataSourceId, input.keepSource)); break;
      case 'search.update': await updateSearch(session, input, searchHandoff); break;
      case 'search.more':
        detached(session, (async () => {
          const epoch = session.searchGeneration;
          for (const source of session.sources) {
            if (session.released || epoch !== session.searchGeneration) break;
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
        session.playbackTarget = clone(session.current.target);
        session.position = session.current.position; session.paused = session.current.paused;
        session.duration = 0; session.stage = 'playing'; session.error = null;
        if (old) detached(session, retire(session, old));
        report(session, 'started', session.current, true);
        try { saveHistory(session); }
        catch (error) { session.error = codeOf(error); publish(session); throw error; }
        publish(session); detached(session, prefetchNeighbors(session)); break;
      }
      case 'media.failed': {
        const attempted = session.pending?.id === input.attemptId ? session.pending
          : session.current?.id === input.attemptId ? session.current : null;
        requireValue(attempted, 'STALE_SESSION'); excluded(session, attempted.candidate);
        const generation = session.generation;
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
        let storageFailure;
        if (attempted === session.pending) session.pending = null;
        if (attempted === session.current) {
          try { saveHistory(session); } catch (error) { storageFailure = error; }
          report(session, 'stopped', attempted, true); session.current = null;
        }
        detached(session, retire(session, attempted));
        try { await invalidated; }
        catch (error) {
          if (valid(session, generation)) { session.error = codeOf(error); publish(session); }
          throw error;
        }
        if (valid(session, generation)) {
          if (!session.pending && !newerRun) void start(session, { constraint: session.constraint, reselecting });
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
        session.position = input.position; session.duration = input.duration; session.paused = input.paused;
        if (changedPause || now() - session.lastHistoryWrite >= 12_000) saveHistory(session);
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
        session.position = session.duration; saveHistory(session, session.current, true);
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
      body.push(Buffer.from(`id: ${after}\nevent: snapshot\ndata: ${JSON.stringify(snapshot(session))}\n\n`));
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
    async setSettings(input) {
      const next = normalizePreferences(input);
      library.update(document => { document.preferences = next; });
      for (const session of sessions.values()) await refreshEligibility(session);
      return clone(next);
    },
    library() { return library.read(); },
    removeLibrary(input) {
      requireValue(object(input) && ['history', 'blocks'].includes(input.kind));
      library.update(document => {
        if (input.kind === 'history') {
          requireValue(input.id == null || text(input.id));
          document.history = input.id == null ? [] : document.history.filter(row => row.targetId !== input.id);
        } else {
          requireValue(input.block == null || object(input.block));
          document.blocks = input.block == null ? [] : document.blocks.filter(row => identity(row) !== identity(input.block));
        }
      });
      return library.read();
    },
    async updateManual(input) {
      requireValue(object(input) && ['save', 'remove'].includes(input.action));
      let previous, updated;
      library.update(document => {
        if (input.action === 'remove') {
          requireValue(text(input.id));
          previous = document.manual.find(row => row.id === input.id);
          document.manual = document.manual.filter(row => row.id !== input.id);
        }
        else {
          requireValue(object(input.item) && text(input.item.targetId) &&
            (text(input.item.sourceKey) && isIdentifier(input.item.detailId) || object(input.item.share) && text(input.item.provider)));
          const item = { ...clone(input.item), id: input.item.id || randomUUID(), failureCount: 0 };
          previous = document.manual.find(row => row.id === item.id); updated = item;
          document.manual = [...document.manual.filter(row => row.id !== item.id), item];
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
        if (domain === session.searchMetadataTarget?.metadataProvider) session.searchUpdateEpoch++;
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
            session.metadataCache = null; session.metadataSeasonCache = null;
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
              loading: false, error: null });
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
          if (result.detail?.lines.some(line => matchesAccount(line.provider) ||
              line.episodes.some(item => matchesAccount(item.file?.provider) || matchesAccount(item.share?.provider)))) {
            delete result.detail; result.state = 'idle';
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
          session.metadataCache = null; session.metadataSeasonCache = null;
          session.metadataSources = []; session.metadataSourceId = null;
          session.initializationError = null;
          session.initialization = tracked((async () => {
            try {
              if (!admitted()) return;
              const entry = await data.targetMetadataEntry(session.target, { rules: session.rules, canSchedule: admitted });
              if (!admitted()) return;
              const value = entry.value;
              session.metadata = value; session.error = null;
              acceptMetadataCache(session, entry);
              session.browsing = { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
              const row = { provider: session.target.metadataProvider, metadataId: session.target.metadataId,
                kind: session.target.kind, title: session.target.title, metadata: value };
              session.metadataSourceId = metadataIdentity(row);
              session.metadataSources = [{ ...row, id: session.metadataSourceId }];
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
        let changed = false;
        for (const source of session.sources) {
          if (source.engine !== engine) continue;
          const instance = instances.find(row => row.installationId === source.installationId);
          if (!instance) continue;
          const site = instance.sites?.find(row => row.key === source.siteId);
          const name = site?.name ?? instance.name;
          if (typeof name !== 'string' || name === source.name) continue;
          source.name = name; changed = true;
          for (const result of session.results) if (result.source.key === source.key) result.source.name = name;
          for (const candidate of session.candidates.values()) if (candidate.source.key === source.key) candidate.source.name = name;
        }
        if (changed) await refreshEligibility(session);
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
