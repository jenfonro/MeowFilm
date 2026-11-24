import { providerFamily, qualityKey } from './rules.js';
import { recognizeChapters } from './chapters.js';
import { clone, codeOf, fail, fields, isIdentifier, nativeMediaErrorCodes, object,
  playbackFeedbackFailure, requireValue, text } from './identity.js';
import { cancelled, sameEpisode } from './session-helpers.js';

export function createSessionCommands(ctx) {
  const { data, library, media, sessions, debug } = ctx;

  function detachedBrowsing(session, work) {
    const generation = session.browseGeneration;
    ctx.tracked(work).catch(error => {
      if (!ctx.isClosed() && !session.released && generation === session.browseGeneration && !cancelled(error)) {
        session.browsing = { ...session.browsing, loading: false, refreshing: false, error: codeOf(error) };
        ctx.publish(session);
      }
    });
  }

  async function command(input, { searchHandoff } = {}) {
    requireValue(object(input) && text(input.action));
    const session = ctx.get(input.sessionId);
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
        await ctx.selection.playSelection(session, input.action === 'prepare');
        break;
      }
      case 'select.target':
        await ctx.selection.selectPlaybackTarget(session, input); break;
      case 'browse.season': {
        requireValue(Number.isSafeInteger(input.season) && input.season >= 0);
        const metadataSourceId = input.metadataSourceId ?? session.metadataSourceId;
        requireValue(session.metadataSources.some(row => row.id === metadataSourceId && row.kind === 'tv'), 'NOT_FOUND');
        session.browseWork = ctx.browsing.browse(session,
          Object.hasOwn(input, 'sourceId') ? input.sourceId : session.browsing.sourceId,
          Object.hasOwn(input, 'lineId') ? input.lineId : session.browsing.lineId, {
            metadataSourceId, season: input.season,
          });
        detachedBrowsing(session, session.browseWork);
        break;
      }
      case 'next': case 'previous':
        await ctx.selection.moveEpisode(session, input.action === 'next' ? 1 : -1); break;
      case 'preview':
        requireValue(typeof input.enabled === 'boolean');
        await library.update(document => {
          document.previewTargets = [
            ...document.previewTargets.filter(id => id !== session.target.id),
            ...(input.enabled ? [session.target.id] : []),
          ];
        });
        session.previewEpisodes = input.enabled; ctx.publish(session); break;
      case 'shortvideo':
        requireValue(typeof input.enabled === 'boolean');
        session.shortVideo = input.enabled;
        if (input.enabled) ctx.detached(session, ctx.prefetch.prefetchNeighbors(session));
        else await Promise.all([
          ctx.prefetch.clearPrefetch(session),
          ...[session.current, session.pending].filter(Boolean).map(value => media.cancelPrefetch(value.resource.id)),
        ]);
        ctx.publish(session); break;
      case 'pan': case 'quality': case 'switch': {
        if (input.action === 'pan') requireValue(providerFamily(input.provider));
        if (input.action === 'quality') requireValue(qualityKey(input.quality));
        if (session.current && !sameEpisode(session.target, session.current.target)) {
          if (!await ctx.selection.selectTarget(session, clone(session.current.target))) break;
        }
        if (input.action === 'switch') {
          const candidate = session.current?.candidate ?? session.pending?.candidate;
          if (candidate) ctx.excluded(session, candidate);
        }
        const reselecting = ctx.selection.reselectActualFile(session, session.current ?? session.pending);
        void ctx.selection.start(session, { constraint: fields(input, ['action', 'provider', 'quality']), reselecting }); break;
      }
      case 'block': {
        const selected = session.current ?? session.pending;
        requireValue(selected && input.attemptId === selected.id, 'STALE_SESSION');
        const candidate = selected.candidate;
        const generation = session.generation;
        await library.update(document => {
          const block = {
            targetId: selected.target.id, sourceKey: candidate.source.key,
            detailId: candidate.source.detailId, lineId: candidate.lineId,
          };
          if (!document.blocks.some(row => !row.errorCode &&
              row.targetId === block.targetId && row.sourceKey === block.sourceKey &&
              row.detailId === block.detailId && row.lineId === block.lineId)) {
            document.blocks.push(block);
          }
        });
        if (!ctx.valid(session, generation) || (session.current !== selected && session.pending !== selected)) break;
        if (!sameEpisode(session.target, selected.target) &&
            !await ctx.selection.selectTarget(session, clone(selected.target))) break;
        if (!ctx.valid(session, generation)) break;
        ctx.excluded(session, candidate);
        debug?.emit({
          level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.source.blocked',
          data: { sessionId: session.id, attemptId: selected.id, sourceKey: candidate.source.key,
            detailId: candidate.source.detailId, lineId: candidate.lineId },
        });
        void ctx.selection.start(session, { reselecting: ctx.selection.reselectActualFile(session, selected) }); break;
      }
      case 'browse':
        session.browseWork = ctx.browsing.browse(session, input.sourceId, input.lineId,
          fields(input, ['metadataSourceId', 'season']));
        detachedBrowsing(session, session.browseWork); break;
      case 'browse.refresh':
        requireValue(input.refresh === undefined || typeof input.refresh === 'boolean');
        detachedBrowsing(session, ctx.browsing.refreshBrowse(session, input.refresh !== false, input)); break;
      case 'media.retry':
        requireValue(input.attemptId === undefined || text(input.attemptId));
        await ctx.selection.retryMedia(session, input.attemptId); break;
      case 'browse.metadata': {
        requireValue(text(input.metadataSourceId) &&
          (input.keepSource === undefined || typeof input.keepSource === 'boolean'));
        requireValue(session.metadataSources.some(row => row.id === input.metadataSourceId), 'NOT_FOUND');
        const keepSource = input.keepSource !== false;
        session.browseWork = ctx.browsing.browse(session,
          Object.hasOwn(input, 'sourceId') ? input.sourceId : keepSource ? session.browsing.sourceId : null,
          Object.hasOwn(input, 'lineId') ? input.lineId : keepSource ? session.browsing.lineId : undefined, {
            metadataSourceId: input.metadataSourceId, ...fields(input, ['season']),
          });
        detachedBrowsing(session, session.browseWork); break;
      }
      case 'search.update': await ctx.lifecycle.updateSearch(session, input, searchHandoff); break;
      case 'search.more':
        requireValue(ctx.groupScopeAvailable(session), 'SOURCE_NOT_FOUND');
        requireValue(!['loading', 'error'].includes(session.entryResolution), 'INVALID_REQUEST');
        ctx.detached(session, (async () => {
          await session.initialization;
          const epoch = session.searchGeneration;
          for (const source of session.sources) {
            if (session.released || session.stopped || ctx.isClosed() || session.initializationError ||
                epoch !== session.searchGeneration || !ctx.groupScopeAvailable(session)) break;
            if (ctx.sourceAllowed(session, source)) await ctx.candidates.searchPage(session, source);
          }
        })()); break;
      case 'media.started': {
        requireValue(session.pending?.id === input.attemptId && !session.pending.prepareOnly &&
          !session.pending.resourceReleased, 'STALE_SESSION');
        const old = session.current;
        ctx.lifecycle.report(session, 'stopped', old, true);
        session.current = session.pending; session.pending = null;
        session.lastFailed = null;
        session.feedbackFailure = null; session.selectionError = null;
        session.playbackTarget = clone(session.current.target);
        session.position = session.current.position; session.paused = session.current.paused;
        session.duration = 0; session.stage = 'playing'; session.error = null;
        if (old) ctx.detached(session, ctx.retire(session, old));
        ctx.lifecycle.report(session, 'started', session.current, true);
        try { await ctx.lifecycle.saveHistory(session); }
        catch (error) { session.error = codeOf(error); ctx.publish(session); throw error; }
        ctx.publish(session); ctx.detached(session, ctx.prefetch.prefetchNeighbors(session)); break;
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
        ctx.excluded(session, attempted.candidate);
        debug?.emit({
          level: 'warn', cat: 'film-playback', sub: 'film.playback', event: 'playback.media.failed',
          data: { sessionId: session.id, attemptId: attempted.id, candidateId: attempted.candidate.id,
            code: failure?.code ?? null, stage: failure?.stage ?? null },
        });
        session.lastFailed = {
          id: attempted.id, candidate: attempted.candidate, target: attempted.target,
          prepareOnly: attempted.prepareOnly, position: attempted === session.current ? session.position : attempted.position,
          paused: attempted === session.current ? session.paused : attempted.paused,
        };
        const invalidated = data.invalidateAddress(attempted.candidate);
        invalidated.catch(() => {});
        const newerRun = attempted === session.current && attempted.generation < session.generation &&
          session.runWork && session.runGeneration === session.generation && !session.stopped;
        // Keep the original same-episode anchor across provisional failures.
        // An explicitly selected different file must retain its own target.
        const reselecting = attempted.generation === session.generation && sameEpisode(attempted.target, session.target) &&
          (session.reselecting || ctx.selection.reselectActualFile(session, attempted));
        const constraint = attempted.candidate.variant && session.constraint.action === 'quality'
          ? { action: 'default' } : session.constraint;
        let storageFailure;
        if (attempted === session.pending) session.pending = null;
        if (attempted === session.current) {
          try { await ctx.lifecycle.saveHistory(session); } catch (error) { storageFailure = error; }
          ctx.lifecycle.report(session, 'stopped', attempted, true); session.current = null;
        }
        ctx.detached(session, ctx.retire(session, attempted));
        try { await invalidated; }
        catch (error) {
          if (ctx.valid(session, generation) && !newerRun) {
            session.feedbackFailure = feedbackFailure;
            session.error = ctx.selectionError(session, codeOf(error)); ctx.publish(session);
          }
          throw error;
        }
        if (ctx.valid(session, generation)) {
          if (!session.pending && !newerRun) {
            session.runKey = null;
            void ctx.selection.start(session, { constraint, reselecting, feedbackFailure });
          } else ctx.publish(session);
        }
        if (storageFailure) throw storageFailure;
        break;
      }
      case 'media.progress': {
        requireValue(session.current?.id === input.attemptId, 'STALE_SESSION');
        requireValue(Number.isFinite(input.position) && input.position >= 0 &&
          Number.isFinite(input.duration) && input.duration >= 0 && typeof input.paused === 'boolean');
        const current = session.current;
        const changedPause = session.paused !== input.paused;
        const changedPausedPosition = input.paused && session.position !== input.position;
        session.position = input.position; session.duration = input.duration; session.paused = input.paused;
        if (changedPause || changedPausedPosition || Date.now() - session.lastHistoryWrite >= 12_000) {
          await ctx.lifecycle.saveHistory(session, current);
          if (ctx.isClosed() || session.released || session.current !== current) break;
        }
        ctx.lifecycle.report(session, 'progress', current, changedPause); ctx.publish(session); break;
      }
      case 'media.ended': {
        requireValue(session.current?.id === input.attemptId, 'STALE_SESSION');
        const ended = session.current;
        let latest = ended.generation === session.generation && sameEpisode(ended.target, session.target) && !session.stopped;
        let next = latest ? ctx.selection.adjacent(session, 1) : null;
        if (latest && !next && session.target.kind !== 'movie') {
          await ctx.selection.checkNextEpisode(session);
          if (session.current !== ended || session.released || ctx.isClosed()) break;
          latest = ended.generation === session.generation && sameEpisode(ended.target, session.target) && !session.stopped;
          next = latest ? ctx.selection.adjacent(session, 1) : null;
        }
        session.position = session.duration;
        await ctx.lifecycle.saveHistory(session, ended, true);
        if (ctx.isClosed() || session.released || session.current !== ended) break;
        latest = ended.generation === session.generation && sameEpisode(ended.target, session.target) && !session.stopped;
        next = latest ? ctx.selection.adjacent(session, 1) : null;
        ctx.lifecycle.report(session, 'stopped', ended, true);
        session.current = null;
        ctx.detached(session, ctx.retire(session, ended));
        if (!latest) {
          // The old output may finish while a newer user intent is resolving.
          // It owns history/cleanup, not that newer intent or its episode.
          if (session.stage === 'playing') session.stage = 'idle';
          ctx.publish(session);
        } else if (next) {
          if (!await ctx.selection.selectTarget(session, next.target)) break;
          if (!ctx.valid(session, ended.generation)) break;
          void ctx.selection.start(session, { explicitCandidateId: next.candidateId });
        } else {
          session.stage = 'ended'; ctx.publish(session);
        }
        break;
      }
      case 'stop': await ctx.lifecycle.stop(session); break;
      default: throw fail('INVALID_REQUEST');
    }
    return ctx.snapshot.build(session);
  }

  function attempt(input) {
    const session = ctx.get(input.sessionId);
    const value = session.pending?.id === input.attemptId ? session.pending
      : session.current?.id === input.attemptId ? session.current : null;
    requireValue(value && !value.resourceReleased, 'STALE_SESSION');
    return {
      attemptId: value.id, ...clone(value.resource), title: value.target.title,
      position: value.position, paused: value.paused,
      ...(value.descriptor.scope ? { scope: clone(value.descriptor.scope) } : {}),
    };
  }

  async function releaseMedia(input) {
    requireValue(!ctx.isClosed(), 'CLOSED');
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
      ctx.publish(session);
    }
    await media.release(input.resourceId);
    session.leases.delete(input.attemptId);
    return { released: true };
  }

  return { command, attempt, releaseMedia };
}
