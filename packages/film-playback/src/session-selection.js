import { randomUUID } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { compareCandidates } from './policy.js';
import { resolveEpisode } from './episodes.js';
import { providerFamily } from './rules.js';
import { validateManual } from './library.js';
import { checkTarget, clone, codeOf, fail, fields, identity, isIdentifier, requireValue, text } from './identity.js';
import { actualSourceEpisodes, canDiscoverVariants, cancelled, episodeTarget,
  manualIdentity, mergeSeason, replaceLine, sameActualEpisode, sameEpisode, sameVariantFile,
  targetForCandidate } from './session-helpers.js';

export function createSessionSelection(ctx) {
  const { data, library, media, catalog, debug } = ctx;

  async function expandDetail(session, result, detail, generation) {
    for (let index = 0; index < detail.lines.length && ctx.valid(session, generation); index++) {
      const line = result.baseDetail?.lines[index] ?? detail.lines[index];
      if (!session.reselecting && session.target.source?.lineId !== undefined &&
          line.id !== session.target.source.lineId) continue;
      if (session.constraint.action === 'pan' && providerFamily(line.provider) &&
          providerFamily(line.provider) !== providerFamily(session.constraint.provider)) continue;
      if (line.expanded || !line.episodes.some(item => item.share ||
        String(item.playId ?? item.id ?? item.url).startsWith('meow.pan.v1.'))) continue;
      try {
        const expanded = await ctx.runIO(session, generation, result.source, {
          accountProviders: line.episodes.map(item => item.file?.provider ?? item.share?.provider ?? item.provider ?? line.provider),
        }, () => data.expand(result.source, line, { canSchedule: () => ctx.valid(session, generation) }));
        if (!ctx.valid(session, generation)) return false;
        const currentIndex = detail.lines.findIndex(value => value.id === line.id);
        if (currentIndex < 0) continue;
        detail = replaceLine(result, detail, currentIndex, expanded);
        await ctx.candidates.candidatesFrom(session, result, detail);
        if (!ctx.valid(session, generation)) return false;
        ctx.publish(session);
        if (await tryCandidate(session, generation)) return true;
      } catch (error) {
        if (ctx.valid(session, generation)) {
          result.error = session.selectionError = codeOf(error); ctx.publish(session);
        }
      }
    }
    return false;
  }

  async function installPending(session, candidate, descriptor, resource, generation, preparedAt = Date.now(), {
    explicit = false, position, paused, clearLastFailed = false,
  } = {}) {
    if (!ctx.valid(session, generation)) { await media.release(resource.id); return false; }
    if (['source', 'group'].includes(session.target.kind)) session.target = targetForCandidate(session.target, candidate);
    const metadataKind = ctx.metadata.playbackMetadataSource(session)?.kind;
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
    session.pending = {
      id: randomUUID(), candidate, descriptor, resource, preparedAt, generation, explicit,
      prepareOnly: session.prepareOnly, target: clone(session.target),
      metadataSourceId: session.playbackMetadataSourceId,
      position: position !== undefined ? position
        : continuation ? session.position
        : !session.resumeCandidateId || session.resumeCandidateId === candidate.id ? session.resumePosition : 0,
      paused: paused !== undefined ? paused : continuation ? session.paused : false,
    };
    if (clearLastFailed) session.lastFailed = null;
    session.leases.set(session.pending.id, resource.id);
    session.stage = session.prepareOnly ? 'prepared' : 'pending'; session.error = null;
    debug?.emit({
      level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.attempt.installed',
      data: {
        sessionId: session.id, attemptId: session.pending.id, candidateId: candidate.id,
        sourceKey: candidate.source.key, provider: candidate.provider ?? null, quality: candidate.quality ?? null,
        prepareOnly: session.prepareOnly, explicit, continuation,
      },
    });
    ctx.publish(session);
    return true;
  }

  async function tryCandidate(session, generation) {
    const attempted = new Set();
    while (ctx.valid(session, generation)) {
      let candidate = session.explicitCandidateId ? session.candidates.get(session.explicitCandidateId) : null;
      if (candidate && !await ctx.candidates.allowed(session, candidate,
        { explicit: true, discoverVariants: canDiscoverVariants(candidate) })) candidate = null;
      if (session.explicitCandidateId && !candidate) return false;
      const explicit = !!candidate;
      if (!candidate) {
        const available = [...session.candidates.values()].filter(value => !attempted.has(value.id) &&
          !session.excluded.has(value.id) && !session.excluded.has(value.fileIdentity));
        const eligibility = await Promise.all(available.map(value =>
          ctx.candidates.allowed(session, value, { discoverVariants: canDiscoverVariants(value) })));
        const options = available.filter((_, index) => eligibility[index]);
        options.sort((a, b) => Number(b.id === session.resumeCandidateId) - Number(a.id === session.resumeCandidateId) ||
          compareCandidates(a, b, {
            preferences: ctx.preferences(), constraint: session.constraint,
            current: session.current?.candidate ?? session.actionCurrent,
          }));
        candidate = options[0];
      }
      if (!ctx.valid(session, generation)) return false;
      session.explicitCandidateId = null;
      if (!candidate) return false;
      if (!ctx.currentSource(session, candidate.source)) return false;
      session.stage = 'resolving'; session.error = null; ctx.publish(session);
      debug?.emit({
        level: 'debug', cat: 'film-playback', sub: 'film.playback', event: 'playback.candidate.selected',
        data: {
          sessionId: session.id, candidateId: candidate.id, sourceKey: candidate.source.key,
          provider: candidate.provider ?? null, quality: candidate.quality ?? null, explicit,
        },
      });
      try {
        let reconsider = false;
        const prepared = await ctx.runIO(session, generation, candidate.source, candidate, async () => {
          const descriptor = await data.resolve(candidate, {
            canSchedule: () => ctx.valid(session, generation),
            discoverVariants: canDiscoverVariants(candidate),
          });
          if (!ctx.valid(session, generation)) return false;
          if (await ctx.candidates.registerVariants(session, candidate, descriptor)) {
            if (!ctx.valid(session, generation)) return false;
            if (candidate.variant) {
              candidate = session.candidates.get(candidate.id);
              requireValue(candidate, 'MEDIA_UNAVAILABLE');
            } else {
              if (explicit) {
                const children = [...session.candidates.values()].filter(value => value.parentId === candidate.id);
                children.sort((a, b) => compareCandidates(a, b, { preferences: ctx.preferences() }));
                session.explicitCandidateId = children[0]?.id ?? null;
              }
              reconsider = true; ctx.publish(session); return false;
            }
          }
          // Discovery is permitted before quality filtering, never preparation.
          if (!await ctx.candidates.allowed(session, candidate, { explicit })) { reconsider = true; return false; }
          if (!ctx.valid(session, generation)) return false;
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          return installPending(session, candidate, descriptor, resource, generation, Date.now(), { explicit });
        });
        if (reconsider) { attempted.add(candidate.id); continue; }
        return prepared;
      } catch (error) {
        if (!ctx.valid(session, generation)) return false;
        ctx.excluded(session, candidate); session.selectionError = codeOf(error);
        debug?.emit({
          level: 'debug', cat: 'film-playback', sub: 'film.playback', event: 'playback.candidate.excluded',
          data: { sessionId: session.id, candidateId: candidate.id, code: session.selectionError },
        });
        ctx.publish(session);
      }
    }
    return false;
  }

  async function consumeResult(session, result, generation) {
    if (!ctx.valid(session, generation) || !(session.target.metadataProvider
      ? ctx.smartSourceAllowed(session, result.source) : ctx.sourceAllowed(session, result.source))) return false;
    if (session.target.kind === 'source' && result.source.detailId !== session.target.source.detailId) return false;
    if (session.target.kind === 'group' && session.target.source && !session.reselecting &&
        (result.source.key !== session.target.source.key ||
          result.source.detailId !== session.target.source.detailId)) return false;
    if (session.target.metadataProvider && !result.hint && !await ctx.candidates.smartResult(session, result)) return false;
    if (!ctx.valid(session, generation) || !ctx.currentSource(session, result.source)) return false;
    // Manual share directories are already expanded by their own stage; they
    // are not script detail endpoints and remain available to adjacent prefetch.
    if (result.source.engine === 'pan') return false;
    const detail = await ctx.candidates.loadDetail(session, result, generation);
    if (!detail || !ctx.valid(session, generation)) return false;
    if (await tryCandidate(session, generation)) return true;
    return expandDetail(session, result, detail, generation);
  }

  async function manualOutcome(row, error) {
    if (error && cancelled(error)) return;
    await library.update(document => {
      const entry = document.manual.find(item => item?.id === row.id);
      if (!entry || entry.enabled === false || manualIdentity(entry) !== manualIdentity(row)) return;
      requireValue(Number.isSafeInteger(entry.failureCount) && entry.failureCount >= 0, 'CORRUPT_STORAGE');
      entry.failureCount = error ? entry.failureCount + 1 : 0;
      if (entry.autoDisable && entry.failureCount >= 3) entry.enabled = false;
      debug?.emit({
        level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.manual.outcome',
        data: { manualId: entry.id, failureCount: entry.failureCount, enabled: entry.enabled !== false,
          errorCode: error ? codeOf(error) : null },
      });
    });
  }

  async function run(session, generation) {
    try {
      await session.initialization;
      if (!ctx.valid(session, generation)) return;
      if (session.initializationError && session.target.metadataProvider) throw fail(session.initializationError);
      if (session.target.metadataProvider) await ctx.metadata.loadPlaybackMetadata(session, generation);
      if (!ctx.valid(session, generation)) return;
      if (!ctx.groupScopeAvailable(session)) {
        session.stage = 'unavailable'; session.error = 'SOURCE_NOT_FOUND'; ctx.publish(session); return;
      }
      if (['source', 'group'].includes(session.target.kind) && !session.reselecting &&
          session.constraint.action === 'default') {
        const result = session.results.find(row => row.source.key === session.target.source?.key &&
          row.source.detailId === session.target.source.detailId);
        requireValue(result && ctx.currentSource(session, result.source), 'SOURCE_NOT_FOUND');
        if (await tryCandidate(session, generation)) return;
        if (!session.selectionError && await consumeResult(session, result, generation)) return;
        if (!ctx.valid(session, generation)) return;
        session.stage = 'unavailable';
        session.error = ctx.selectionError(session, result.error ??
          (session.target.source.candidateId ? 'NOT_FOUND' : 'MEDIA_UNAVAILABLE'));
        ctx.publish(session); return;
      }
      requireValue(session.target.kind !== 'tv' || session.target.episode !== undefined, 'NO_EPISODE_MAPPING');
      if (session.target.kind === 'tv' && session.target.metadataProvider === 'tmdb' && session.target.season !== undefined) {
        const target = session.target;
        const entry = await data.seasonMetadataEntry(target, target.season, {
          rules: session.rules,
          canSchedule: () => ctx.valid(session, generation),
        });
        if (!ctx.valid(session, generation)) return;
        const display = mergeSeason(target, ctx.metadata.playbackMetadata(session), target.season, entry.value);
        ctx.metadata.acceptMetadataCache(session, entry, target.season, undefined, session.playbackMetadataSourceId, display);
        if (session.playbackMetadataSourceId === session.metadataSourceId) session.metadata = display;
        ctx.metadata.requireFreshMetadata(session);
      }
      session.stage = session.target.metadataProvider ? 'cached' : 'loading'; ctx.publish(session);
      const neighborKey = identity(session.target);
      const neighbor = session.prefetch.get(neighborKey);
      if (neighbor && neighbor.state !== 'error' && ctx.preparedValid(neighbor) && await ctx.candidates.allowed(session, neighbor.candidate)) {
        if (!ctx.valid(session, generation)) return;
        if (session.prefetch.get(neighborKey) === neighbor) {
          session.prefetch.delete(neighborKey);
          await media.cancelPrefetch(neighbor.resource.id);
          if (await installPending(session, neighbor.candidate, neighbor.descriptor, neighbor.resource,
            generation, neighbor.preparedAt)) return;
        }
      }
      if (!ctx.valid(session, generation)) return;
      if (await tryCandidate(session, generation)) return;
      const visited = new Set();
      const consume = async result => {
        // A hint is visited without trusting its old raw. A later real search
        // promotes the same row and gets one ordinary result visit of its own.
        const key = result.hint ? result : result.id;
        if (visited.has(key) || session.target.metadataProvider && !result.hint &&
            !await ctx.candidates.smartResult(session, result)) return false;
        visited.add(key);
        return consumeResult(session, result, generation);
      };
      const savedHistory = await library.history(session.target.id);
      if (!ctx.valid(session, generation)) return;
      const document = { ...library.read(), history: savedHistory ? [savedHistory] : [] };
      for (const stage of ['history', 'manual']) {
        if (!ctx.valid(session, generation)) return;
        session.stage = session.target.metadataProvider ? stage : 'loading'; ctx.publish(session);
        const rows = document[stage].filter(row => row?.targetId === session.target.id && row.enabled !== false);
        for (const row of rows) {
          if (!ctx.valid(session, generation)) return;
          if (stage === 'manual') {
            try {
              validateManual(row, 'CORRUPT_STORAGE');
              requireValue(row.failureCount < Number.MAX_SAFE_INTEGER, 'CORRUPT_STORAGE');
            } catch (error) { session.selectionError = codeOf(error); continue; }
          }
          if (stage === 'manual' && row.share) {
            if (['source', 'group'].includes(session.target.kind)) continue;
            const source = {
              key: `manual:${row.id}`, engine: 'pan', name: row.name || '人工匹配',
              detailId: row.id, ...fields({ manualSeasonHint: row.seasonHint }, ['manualSeasonHint']),
            };
            try {
              const line = await ctx.runIO(session, generation, source, { provider: row.provider }, () =>
                data.expand(source, {
                  id: row.id, label: row.name || row.provider, provider: row.provider,
                  episodes: [{ id: row.id, name: row.name || session.target.title,
                    share: { ...row.share, provider: row.provider } }],
                }, { canSchedule: () => ctx.valid(session, generation) }));
              if (!ctx.valid(session, generation)) return;
              const added = await ctx.candidates.addResults(session, source, [{ id: row.id }], 'linked',
                () => ctx.valid(session, generation));
              if (!added || !ctx.valid(session, generation)) return;
              const result = session.results.find(value => value.id === identity(source.key, row.id));
              if (!result) return;
              const detail = { lines: [line], detailId: row.id };
              Object.assign(result, {
                title: row.title || session.target.title, linkedTargetId: session.target.id,
                manualId: row.id, manualIdentity: manualIdentity(row), state: 'ready', error: null,
                detail: isDeepStrictEqual(result.detail, detail) ? result.detail : detail,
              });
              await ctx.candidates.candidatesFrom(session, result, result.detail);
            } catch (error) {
              if (!ctx.valid(session, generation)) return;
              await ctx.tracked(manualOutcome(row, error));
              if (!ctx.valid(session, generation)) return;
              session.selectionError = codeOf(error); ctx.publish(session);
              continue;
            }
            await ctx.tracked(manualOutcome(row));
            if (!ctx.valid(session, generation)) return;
            if (await tryCandidate(session, generation)) return;
            continue;
          }
          const source = session.sources.find(item => item.key === row.sourceKey);
          if (!source || !isIdentifier(row.detailId) || !(session.target.metadataProvider
            ? ctx.smartSourceAllowed(session, source) : ctx.sourceAllowed(session, source))) continue;
          if (session.target.kind === 'source' && row.detailId !== session.target.source.detailId) continue;
          const added = await ctx.candidates.addResults(session, source, [{ id: row.detailId }], 'linked',
            () => ctx.valid(session, generation));
          if (!added || !ctx.valid(session, generation)) return;
          const result = session.results.find(item => item.source.key === source.key && item.source.detailId === row.detailId);
          if (!result) return;
          if (!result.title) result.title = row.title || session.target.title;
          result.linkedTargetId = session.target.id;
          if (session.target.kind === 'group') ctx.addMember(session, source.key, row.detailId);
          delete result.manualId; delete result.manualIdentity; delete result.source.manualSeasonHint;
          if (stage === 'manual') {
            result.manualId = row.id; result.manualIdentity = manualIdentity(row);
          }
          if (row.seasonHint !== undefined) result.source.manualSeasonHint = row.seasonHint;
          const requested = !visited.has(result.hint ? result : result.id);
          const found = await consume(result);
          if (stage === 'manual' && requested && ctx.valid(session, generation)) {
            await ctx.tracked(manualOutcome(row, result.error && !(result.hint && result.verifiedRaw && result.error === 'NO_MATCH')
              ? fail(result.error) : null));
            if (!ctx.valid(session, generation)) return;
          }
          if (found) return;
        }
      }
      if (session.target.kind !== 'source' &&
          !(session.target.kind === 'group' && session.target.source && !session.reselecting)) {
        session.stage = session.target.metadataProvider ? 'searching' : 'loading'; ctx.publish(session);
        // Already-returned search pages are shared input, not an instruction to
        // fetch every detail before history and manual matching get a chance.
        for (const result of session.results) {
          if (!ctx.valid(session, generation)) return;
          if (await consume(result)) return;
        }
        for (const source of session.sources) {
          if (!(session.target.metadataProvider ? ctx.smartSourceAllowed(session, source)
            : ctx.sourceAllowed(session, source))) continue;
          const state = session.search.get(source.key);
          while (ctx.valid(session, generation) && !state.completed && !state.error) {
            if (!await ctx.runIO(session, generation, source, null, () => ctx.candidates.searchPage(session, source)) ||
                !ctx.valid(session, generation)) break;
            for (const result of session.results) {
              if (!ctx.valid(session, generation)) return;
              if (result.source.key !== source.key) continue;
              if (await consume(result)) return;
            }
          }
        }
      } else for (const result of session.results) {
        if (!ctx.valid(session, generation)) return;
        if (await consume(result)) return;
      }
      if (!ctx.valid(session, generation)) return;
      // The action-specific preference gate is relaxed only after full discovery,
      // never merely because one lower-quality candidate happened to arrive first.
      session.discoveryComplete = session.sources.filter(source => session.target.metadataProvider
        ? ctx.smartSourceAllowed(session, source) : ctx.sourceAllowed(session, source))
        .every(source => session.search.get(source.key).completed) &&
        (await Promise.all(session.results.map(async result =>
          (session.target.metadataProvider ? ctx.smartSourceAllowed(session, result.source) &&
            await ctx.candidates.smartResult(session, result) : ctx.sourceAllowed(session, result.source)) ? result : null))).filter(Boolean)
          .every(result => result.state === 'ready' && !result.error);
      if (session.discoveryComplete && await tryCandidate(session, generation)) return;
      if (!ctx.valid(session, generation)) return;
      const donor = session.current;
      if (donor && !donor.resourceReleased && session.reselecting?.id === donor.candidate.id &&
          sameActualEpisode(donor.target, session.target)) {
        session.target = targetForCandidate(session.target, donor.candidate);
      }
      session.stage = 'unavailable';
      session.error = ctx.selectionError(session,
        [...session.search.values()].find(value => value.error)?.error ?? 'NO_MATCH');
      debug?.emit({
        level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.stage.unavailable',
        data: { sessionId: session.id, targetId: session.target.id, error: session.error },
      });
      ctx.publish(session);
    } catch (error) {
      if (ctx.valid(session, generation)) {
        session.stage = 'error'; session.error = ctx.selectionError(session, codeOf(error)); ctx.publish(session);
      }
    }
  }

  function start(session, { prepareOnly = false, constraint = { action: 'default' }, explicitCandidateId = null,
    reselecting = null, feedbackFailure = null } = {}) {
    const requestKey = identity(session.target, constraint, explicitCandidateId, reselecting?.id);
    if (session.runKey === requestKey && session.runWork && !session.stopped) {
      session.prepareOnly = prepareOnly;
      return session.runWork;
    }
    const previous = session.pending;
    void ctx.cancelCalculations(session, 'playback');
    session.generation++; session.stopped = false; session.prepareOnly = prepareOnly;
    session.pending = null; session.explicitCandidateId = explicitCandidateId;
    session.reselecting = reselecting;
    session.feedbackFailure = feedbackFailure; session.selectionError = null;
    session.constraint = clone(constraint); session.actionCurrent = session.current?.candidate ?? session.actionCurrent;
    session.error = null; session.stage = session.target.metadataProvider ? 'cached' : 'loading'; ctx.publish(session);
    if (previous) ctx.detached(session, ctx.retire(session, previous));
    const work = ctx.tracked(run(session, session.generation));
    session.runKey = requestKey; session.runWork = work; session.runGeneration = session.generation;
    void work.finally(() => { if (session.runWork === work) session.runWork = null; }).catch(() => {});
    return work;
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
    const items = ctx.metadata.episodeProjection(session, ctx.metadata.playbackMetadata(session)).visible;
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
      if (result.detail) await ctx.candidates.candidatesFrom(session, result, result.detail);
      if (ctx.isClosed() || session.released || session.generation !== generation || session.target !== target) return false;
    }
    return !ctx.isClosed() && !session.released && session.generation === generation && session.target === target;
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
          session.pending = null; ctx.detached(session, ctx.retire(session, pending));
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
      const eligible = pending && sameEpisode(pending.target, selectedTarget) && ctx.preparedValid(pending) &&
        await ctx.candidates.allowed(session, pending.candidate, { gate: false, explicit: pending.explicit });
      if (ctx.isClosed() || session.released || generation !== session.generation || pending !== session.pending ||
          selectedTarget !== session.target) return;
      if (eligible) {
        pending.prepareOnly = prepareOnly;
        session.stage = prepareOnly ? 'prepared' : 'pending'; ctx.publish(session);
      } else if (!prepareOnly && session.current && !session.current.resourceReleased &&
          sameEpisode(session.current.target, selectedTarget)) {
        session.stage = 'playing'; ctx.publish(session);
      } else void start(session, { prepareOnly, explicitCandidateId: candidateId });
    } catch (error) {
      if (!ctx.isClosed() && !session.released && generation === session.generation) {
        session.stage = 'unavailable'; session.error = codeOf(error); ctx.publish(session);
      }
    }
  }

  async function playSelection(session, prepareOnly = false) {
    const generation = session.generation, selection = ++session.selectionEpoch;
    const admitted = () => !ctx.isClosed() && !session.released && generation === session.generation &&
      selection === session.selectionEpoch;
    await session.initialization;
    if (!admitted()) return;
    try {
      let target = clone(session.target);
      if (['source', 'group'].includes(target.kind) && target.source?.candidateId === undefined) {
        const pointer = target.source ?? target.members[0];
        const result = session.results.find(row => row.source.key === (pointer?.key ?? pointer?.sourceKey) &&
          row.source.detailId === pointer?.detailId);
        requireValue(result && ctx.currentSource(session, result.source), 'SOURCE_NOT_FOUND');
        requireValue(result.detail, result.error ?? 'MEDIA_UNAVAILABLE');
        const line = target.source?.lineId === undefined ? result.detail.lines[0]
          : result.detail.lines.find(row => row.id === target.source.lineId);
        requireValue(line, 'MEDIA_UNAVAILABLE');
        const candidates = await ctx.candidates.recognizeResult(session, result, result.detail, target,
          ctx.metadata.playbackMetadata(session), 'session');
        if (!admitted()) return;
        const rows = line.episodes.map(row => candidates.find(candidate => candidate.lineId === line.id &&
          candidate.episodeId === row.id)).filter(candidate => candidate && (!candidate.share || candidate.file));
        const selected = target.episode !== undefined || target.globalEpisode !== undefined
          ? rows.find(candidate => ['season', 'episode', 'globalEpisode'].every(field =>
            target[field] === undefined || target[field] === candidate[field]))
          : rows.find(candidate => candidate.id === session.resumeCandidateId) ?? rows[0];
        requireValue(selected, 'MEDIA_UNAVAILABLE');
        target = targetForCandidate(target, selected);
      }
      await playTarget(session, target, { prepareOnly });
    } catch (error) {
      if (admitted()) { session.stage = 'unavailable'; session.error = codeOf(error); ctx.publish(session); }
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
      requireValue(ctx.currentSource(session, result.source), 'STALE_SOURCE');
      if (metadataSourceId !== null) requireValue(selected, 'NOT_FOUND');
    }
    const generation = session.generation, selection = ++session.selectionEpoch;
    const admitted = () => !ctx.isClosed() && !session.released && generation === session.generation &&
      selection === session.selectionEpoch;
    let metadata = selected ? session.metadataViews.get(selected.id)?.display ?? selected.metadata : null;
    let canonical, candidateId = null;
    if (target.metadataProvider) {
      if (target.kind === 'tv') {
        let episode = resolveEpisode(ctx.metadata.episodeMap(metadata), target);
        if (!episode) {
          const entry = await data.seasonMetadataEntry(target, target.season, { rules: session.rules, canSchedule: admitted });
          if (!admitted()) return;
          metadata = mergeSeason(target, metadata, target.season, entry.value);
          ctx.metadata.metadataDisplay(session, selected);
          ctx.metadata.acceptMetadataCache(session, entry, target.season, undefined, selected.id, metadata);
          if (selected.id === session.metadataSourceId) session.metadata = metadata;
          episode = resolveEpisode(ctx.metadata.episodeMap(metadata), target);
        }
        requireValue(episode && ctx.metadata.episodeProjection(session, metadata).visibleKeys.has(`${episode.season}:${episode.episode}`), 'NO_MATCH');
        canonical = episodeTarget({ ...target, title: selected.title }, episode);
      } else canonical = { ...target, title: selected.title };
    } else {
      const context = selected ? {
        id: target.id, title: session.entryTarget.title, kind: selected.kind,
        metadataProvider: selected.provider, metadataId: selected.metadataId,
      } : session.entryTarget;
      const candidates = await ctx.candidates.recognizeResult(session, result, result.detail, context, metadata, 'session');
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

  async function checkNextEpisode(session) {
    if (session.target.kind === 'tv') {
      const generation = session.generation, target = session.target;
      await ctx.metadata.refreshPlaybackSeason(session, session.target.season,
        session.target.episode === undefined ? undefined : session.target.episode + 1);
      if (!ctx.valid(session, generation) || session.target !== target || adjacent(session, 1)) return;
      const metadata = ctx.metadata.playbackMetadata(session);
      const nextSeason = (metadata?.doubanSeasons ?? metadata?.seasons ?? [])
        .map(row => row.season_number ?? row.season)
        .filter(season => Number.isSafeInteger(season) && season > session.target.season).sort((a, b) => a - b)[0];
      if (nextSeason !== undefined) await ctx.metadata.refreshPlaybackSeason(session, nextSeason, 1);
      return;
    }
    if (!['source', 'group'].includes(session.target.kind)) return;
    const selected = session.current ?? session.pending;
    if (!selected) return;
    const candidate = selected.candidate, generation = session.generation;
    const result = session.results.find(row => row.source.key === candidate.source.key &&
      row.source.detailId === candidate.source.detailId);
    if (!result) return;
    const admitted = () => ctx.valid(session, generation) && ctx.currentSource(session, result.source) &&
      (session.current === selected || session.pending === selected);
    try {
      const detail = result.source.engine === 'pan' ? result.detail : await ctx.candidates.loadDetail(session, result, generation);
      if (!detail || !admitted()) return;
      const index = detail.lines.findIndex(row => row.id === candidate.lineId);
      if (index < 0) return;
      const line = await data.expand(result.source,
        result.baseDetail?.lines.find(row => row.id === candidate.lineId) ?? detail.lines[index], { canSchedule: admitted });
      if (!admitted()) return;
      const expanded = replaceLine(result, detail, index, line);
      await ctx.candidates.candidatesFrom(session, result, expanded);
      if (!admitted()) return;
      await ctx.browsing.refreshBrowsing(session);
      if (!admitted()) return;
    } catch (error) {
      if (!admitted()) return;
      await ctx.browsing.refreshBrowsing(session);
      if (!admitted()) return;
      session.error = codeOf(error);
    }
    ctx.publish(session);
  }

  async function moveEpisode(session, direction) {
    let next = adjacent(session, direction);
    if (!next && direction > 0) {
      const generation = session.generation, target = session.target;
      await checkNextEpisode(session);
      if (!ctx.valid(session, generation) || session.target !== target) return;
      next = adjacent(session, direction);
    }
    requireValue(next, 'NO_MATCH');
    session.userSelected = true;
    if (!await selectTarget(session, next.target)) return;
    void start(session, { explicitCandidateId: next.candidateId });
  }

  async function retryMedia(session, attemptId) {
    const selected = attemptId === undefined ? session.pending ?? session.current ?? session.lastFailed
      : [session.pending, session.current, session.lastFailed].find(value => value?.id === attemptId);
    requireValue(selected, 'STALE_SESSION');
    requireValue(ctx.currentSource(session, selected.candidate.source), 'STALE_SOURCE');
    const preflightGeneration = session.generation, target = session.target;
    const admitted = () => !ctx.isClosed() && !session.released && session.generation === preflightGeneration &&
      ctx.currentSource(session, selected.candidate.source) &&
      [session.pending, session.current, session.lastFailed].includes(selected);
    const eligible = await ctx.candidates.allowed(session, selected.candidate, { explicit: true, target: selected.target, retry: true });
    if (!admitted() || session.target !== target) return;
    requireValue(eligible, 'NO_MATCH');
    if (!sameEpisode(session.target, selected.target) &&
        !await selectTarget(session, clone(selected.target))) return;
    if (!admitted()) return;
    void ctx.cancelCalculations(session, 'playback');
    const generation = ++session.generation;
    session.runKey = null; session.stopped = false;
    session.feedbackFailure = null; session.selectionError = null;
    session.excluded.delete(selected.candidate.id);
    if (selected.candidate.fileIdentity) session.excluded.delete(selected.candidate.fileIdentity);
    session.prepareOnly = selected.prepareOnly;
    const previous = session.pending;
    session.pending = null;
    if (previous) ctx.detached(session, ctx.retire(session, previous));
    session.stage = 'resolving'; session.error = null;
    debug?.emit({
      level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.retry',
      data: { sessionId: session.id, attemptId: selected.id, candidateId: selected.candidate.id },
    });
    ctx.publish(session);
    const work = ctx.tracked((async () => {
      try {
        await ctx.runIO(session, generation, selected.candidate.source, selected.candidate, async () => {
          let descriptor = await data.refresh(selected.candidate, { canSchedule: () => ctx.valid(session, generation) });
          if (!ctx.valid(session, generation)) return;
          const changed = await ctx.candidates.registerVariants(session, selected.candidate, descriptor);
          if (!ctx.valid(session, generation)) return;
          let candidate = selected.candidate.variant
            ? session.candidates.get(selected.candidate.id) : selected.candidate;
          if (changed && !selected.candidate.variant) {
            const children = [...session.candidates.values()].filter(value => value.parentId === candidate.id);
            const eligibility = await Promise.all(children.map(value => ctx.candidates.allowed(session, value, { retry: true })));
            const options = children.filter((_, index) => eligibility[index]);
            options.sort((a, b) => compareCandidates(a, b, {
              preferences: ctx.preferences(),
              constraint: session.constraint, current: session.current?.candidate ?? session.actionCurrent,
            }));
            candidate = options[0];
            requireValue(candidate, 'NO_MATCH');
            // A previously opaque parent has just acquired real variants.
            // Resolve the chosen child; never prepare the master as that child.
            descriptor = await data.resolve(candidate, { canSchedule: () => ctx.valid(session, generation) });
            if (!ctx.valid(session, generation)) return;
          }
          requireValue(candidate, 'MEDIA_UNAVAILABLE');
          if (candidate.variant) requireValue(await ctx.candidates.allowed(session, candidate, { retry: true }), 'NO_MATCH');
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          await installPending(session, candidate, descriptor, resource, generation, Date.now(), {
            explicit: true,
            position: selected === session.current ? session.position : selected.position,
            paused: selected === session.current ? session.paused : selected.paused,
            clearLastFailed: true,
          });
        });
      } catch (error) {
        if (!ctx.valid(session, generation)) return;
        session.lastFailed = {
          id: selected.id, candidate: selected.candidate, target: selected.target,
          prepareOnly: selected.prepareOnly, position: selected.position, paused: selected.paused,
        };
        session.error = codeOf(error);
        session.stage = session.current && !session.current.resourceReleased ? 'playing' : 'error';
        ctx.publish(session);
      }
    })());
    session.runWork = work; session.runGeneration = generation;
    void work.finally(() => { if (session.runWork === work) session.runWork = null; }).catch(() => {});
  }

  return {
    start, adjacent, selectTarget, playSelection, selectPlaybackTarget,
    reselectActualFile, moveEpisode, checkNextEpisode, retryMedia,
  };
}
