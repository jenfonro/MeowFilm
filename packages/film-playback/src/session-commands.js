import { providerFamily, qualityKey } from './rules.js';
import { recognizeChapters } from './chapters.js';
import { clone, codeOf, fail, fields, identity, isIdentifier, nativeMediaErrorCodes, object,
  playbackFeedbackFailure, requireValue, text } from './identity.js';
import { cancelled, samePlayIntent } from './session-helpers.js';

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

  async function command(input) {
    requireValue(object(input) && text(input.action));
    const session = ctx.get(input.sessionId);

    switch (input.action) {
      case 'media.chapters': {
        requireValue(text(input.attemptId) && text(input.resourceId));
        const value = session.pending?.id === input.attemptId ? session.pending
          : session.current?.id === input.attemptId ? session.current : null;
        requireValue(value && !value.resourceReleased && value.resource.id === input.resourceId &&
          session.leases.get(input.attemptId) === input.resourceId, 'STALE_SESSION');
        return recognizeChapters(input.chapters);
      }
      case 'prepare': {
        void ctx.selection.start(session, { prepareOnly: true });
        break;
      }
      case 'play': {
        if (session.pending && session.pending.prepareOnly && !session.pending.resourceReleased &&
            ctx.preparedValid(session.pending)) {
          session.prepareOnly = false;
          session.pending.prepareOnly = false;
          session.stage = 'pending';
          ctx.publish(session);
        } else {
          void ctx.selection.start(session, { prepareOnly: false });
        }
        break;
      }
      case 'select.item': {
        await ctx.selection.selectItem(session, input.itemId, { prepareOnly: Boolean(input.prepareOnly) });
        break;
      }
      case 'browse.source': {
        requireValue(text(input.sourceId));
        session.userBrowsedSource = true;
        if (session.playIntent.sourceFilter && input.sourceId !== session.browsing.sourceId) {
          delete session.playIntent.sourceFilter;
        }
        session.browseWork = ctx.browsing.selectSource(session, input.sourceId, {
          ...(input.season !== undefined ? { season: input.season } : {}),
        });
        detachedBrowsing(session, session.browseWork);
        break;
      }
      case 'browse.line': {
        requireValue(isIdentifier(input.lineId) && text(session.browsing.sourceId));
        session.browseWork = ctx.browsing.selectSource(session, session.browsing.sourceId, { lineId: input.lineId });
        detachedBrowsing(session, session.browseWork);
        break;
      }
      case 'browse.season': {
        requireValue(Number.isSafeInteger(input.season) && input.season >= 0);
        const sourceId = text(input.sourceId) || session.browsing.sourceId;
        requireValue(sourceId, 'NOT_FOUND');
        session.browseWork = ctx.browsing.selectSource(session, sourceId, { season: input.season });
        detachedBrowsing(session, session.browseWork);
        break;
      }
      case 'browse.refresh': {
        requireValue(input.refresh === undefined || typeof input.refresh === 'boolean');
        const sourceId = text(input.sourceId) || session.browsing.sourceId;
        if (sourceId) {
          session.browseWork = ctx.browsing.selectSource(session, sourceId, { refresh: input.refresh !== false });
          detachedBrowsing(session, session.browseWork);
        }
        break;
      }
      case 'episodes.more': {
        detachedBrowsing(session, ctx.browsing.loadMoreSmartEpisodes(session));
        break;
      }
      case 'search.more': {
        if (session.noSources) break;
        if (session.playIntent?.sourceFilter) {
          delete session.playIntent.sourceFilter;
        }
        ctx.detached(session, (async () => {
          await session.initialization;
          const epoch = session.searchGeneration;
          const searchable = session.sources.filter(s => s.enabled !== false && s.searchable !== false);
          for (const source of searchable) {
            if (session.released || ctx.isClosed() || epoch !== session.searchGeneration) break;
            await ctx.candidates.searchPage(session, source);
          }
          if (!session.released && !ctx.isClosed() && session.metadataSources.length === 0 &&
              !session.userBrowsedSource && ctx.browsing.hasSmartList(session) && session.browsing.sourceId !== 'smart') {
            await ctx.browsing.selectSource(session, 'smart');
          }
        })());
        break;
      }
      case 'next':
      case 'previous': {
        await ctx.selection.moveEpisode(session, input.action === 'next' ? 1 : -1);
        break;
      }
      case 'preview': {
        requireValue(typeof input.enabled === 'boolean');
        await library.update(document => {
          document.previewTargets = [
            ...document.previewTargets.filter(id => id !== session.targetId),
            ...(input.enabled ? [session.targetId] : []),
          ];
        });
        session.previewEpisodes = input.enabled;
        ctx.publish(session);
        break;
      }
      case 'shortvideo': {
        requireValue(typeof input.enabled === 'boolean');
        session.shortVideo = input.enabled;
        if (input.enabled) ctx.detached(session, ctx.prefetch.prefetchNeighbors(session));
        else await Promise.all([
          ctx.prefetch.clearPrefetch(session),
          ...[session.current, session.pending].filter(Boolean).map(value => media.cancelPrefetch(value.resource.id)),
        ]);
        ctx.publish(session);
        break;
      }
      case 'pan':
      case 'quality':
      case 'switch': {
        if (input.action === 'pan') requireValue(providerFamily(input.provider));
        if (input.action === 'quality') {
          requireValue(qualityKey(input.quality) || String(input.quality || '').toLowerCase() === '8k');
        }
        if (session.playIntent.mode === 'direct') {
          const activeCand = session.current?.candidate ?? session.pending?.candidate;
          if (activeCand?.episode > 0) {
            session.playIntent = {
              mode: 'smart',
              kind: 'tv',
              season: activeCand.season ?? 1,
              episode: activeCand.episode,
              ...(activeCand.globalEpisode !== undefined ? { globalEpisode: activeCand.globalEpisode } : {}),
            };
          } else if (session.kind === 'movie') {
            session.playIntent = { mode: 'smart', kind: 'movie' };
          }
        }
        delete session.playIntent.sourceFilter;
        if (input.action === 'switch') {
          const candidate = session.current?.candidate ?? session.pending?.candidate;
          if (candidate) ctx.excluded(session, candidate);
          session.affinity = null;
        }
        void ctx.selection.start(session, { constraint: fields(input, ['action', 'provider', 'quality']) });
        break;
      }
      case 'block': {
        const selected = session.current ?? session.pending;
        requireValue(selected && (!input.attemptId || input.attemptId === selected.id), 'STALE_SESSION');
        const candidate = selected.candidate;
        const generation = session.generation;
        const wholeItem = input.wholeItem === true;
        await library.update(document => {
          const block = {
            targetId: session.targetId,
            sourceKey: candidate.source.key,
            detailId: candidate.detailId ?? candidate.source.detailId,
            ...(wholeItem ? {} : { lineId: candidate.lineId }),
          };
          const blockKey = identity(fields(block, ['targetId', 'sourceKey', 'detailId', 'lineId']));
          if (!document.blocks.some(row => !row.errorCode &&
              identity(fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId'])) === blockKey)) {
            document.blocks.push(block);
          }
        });
        if (!ctx.valid(session, generation)) break;
        ctx.excluded(session, candidate);
        if (session.affinity &&
            session.affinity.sourceKey === candidate.source.key &&
            session.affinity.detailId === (candidate.detailId ?? candidate.source.detailId) &&
            (wholeItem || session.affinity.lineId === candidate.lineId)) {
          session.affinity = null;
        }
        delete session.playIntent.sourceFilter;
        if (session.playIntent.mode === 'direct' && candidate.episode > 0) {
          session.playIntent = {
            mode: 'smart',
            kind: 'tv',
            season: candidate.season ?? 1,
            episode: candidate.episode,
            ...(candidate.globalEpisode !== undefined ? { globalEpisode: candidate.globalEpisode } : {}),
          };
        }
        debug?.emit({
          level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.source.blocked',
          data: {
            sessionId: session.id, attemptId: selected.id, sourceKey: candidate.source.key,
            detailId: candidate.detailId ?? candidate.source.detailId, lineId: wholeItem ? null : candidate.lineId,
          },
        });
        void ctx.selection.start(session, { constraint: session.constraint });
        break;
      }
      case 'media.retry': {
        requireValue(input.attemptId === undefined || text(input.attemptId));
        await ctx.selection.retryMedia(session, input.attemptId);
        break;
      }
      case 'media.started': {
        requireValue(session.pending?.id === input.attemptId && !session.pending.prepareOnly &&
          !session.pending.resourceReleased, 'STALE_SESSION');
        const old = session.current;
        ctx.lifecycle.report(session, 'stopped', old, true);
        session.current = session.pending;
        session.pending = null;
        session.lastFailed = null;
        session.feedbackFailure = null;
        session.selectionError = null;
        session.activePlayIntent = clone(session.current.intent);
        session.position = session.current.position;
        session.paused = session.current.paused;
        session.duration = 0;
        session.stage = 'playing';
        session.error = null;
        if (old) ctx.detached(session, ctx.retire(session, old));
        ctx.lifecycle.report(session, 'started', session.current, true);
        try { await ctx.lifecycle.saveHistory(session); }
        catch (error) { session.error = codeOf(error); ctx.publish(session); throw error; }
        ctx.publish(session);
        ctx.detached(session, ctx.prefetch.prefetchNeighbors(session));
        break;
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
        if (session.affinity &&
            session.affinity.sourceKey === attempted.candidate.source.key &&
            session.affinity.detailId === (attempted.candidate.detailId ?? attempted.candidate.source.detailId) &&
            session.affinity.lineId === attempted.candidate.lineId) {
          session.affinity = null;
        }
        session.lastFailed = {
          id: attempted.id,
          candidate: attempted.candidate,
          intent: attempted.intent,
          prepareOnly: attempted.prepareOnly,
          position: attempted === session.current ? session.position : attempted.position,
          paused: attempted === session.current ? session.paused : attempted.paused,
        };
        const invalidated = data.invalidateAddress(attempted.candidate);
        invalidated.catch(() => {});
        const newerRun = attempted === session.current && attempted.generation < session.generation &&
          session.runWork && session.runGeneration === session.generation && !session.stopped;
        const constraint = attempted.candidate.variant && session.constraint.action === 'quality'
          ? { action: 'default' } : session.constraint;
        let storageFailure;
        if (attempted === session.pending) session.pending = null;
        if (attempted === session.current) {
          try { await ctx.lifecycle.saveHistory(session); } catch (error) { storageFailure = error; }
          ctx.lifecycle.report(session, 'stopped', attempted, true);
          session.current = null;
        }
        ctx.detached(session, ctx.retire(session, attempted));
        try { await invalidated; }
        catch (error) {
          if (ctx.valid(session, generation) && !newerRun) {
            session.feedbackFailure = feedbackFailure;
            session.error = ctx.selectionError(session, codeOf(error));
            ctx.publish(session);
          }
          throw error;
        }
        if (ctx.valid(session, generation)) {
          if (!session.pending && !newerRun) {
            session.runKey = null;
            void ctx.selection.start(session, { constraint, feedbackFailure });
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
        session.position = input.position;
        session.duration = input.duration;
        session.paused = input.paused;
        if (changedPause || changedPausedPosition || Date.now() - session.lastHistoryWrite >= 12_000) {
          await ctx.lifecycle.saveHistory(session, current);
          if (ctx.isClosed() || session.released || session.current !== current) break;
        }
        ctx.lifecycle.report(session, 'progress', current, changedPause);
        ctx.publish(session);
        break;
      }
      case 'media.ended': {
        requireValue(session.current?.id === input.attemptId, 'STALE_SESSION');
        const ended = session.current;
        const latest = ended.generation === session.generation && samePlayIntent(ended.intent, session.playIntent) && !session.stopped;
        const next = latest ? ctx.selection.adjacent(session, 1) : null;
        session.position = session.duration;
        await ctx.lifecycle.saveHistory(session, ended, true);
        if (ctx.isClosed() || session.released || session.current !== ended) break;
        ctx.lifecycle.report(session, 'stopped', ended, true);
        session.current = null;
        ctx.detached(session, ctx.retire(session, ended));
        if (!latest) {
          if (session.stage === 'playing') session.stage = 'idle';
          ctx.publish(session);
        } else if (next) {
          session.excluded.clear();
          session.resumePosition = 0;
          session.resumeCandidateId = null;
          session.playIntent = next.intent;
          if (next.intent.season) session.browsing.season = next.intent.season;
          void ctx.selection.start(session, { explicitCandidateId: next.candidateId ?? null });
        } else {
          session.stage = 'ended';
          ctx.publish(session);
        }
        break;
      }
      case 'stop': {
        await ctx.lifecycle.stop(session);
        break;
      }
      default:
        throw fail('INVALID_REQUEST');
    }
    return ctx.snapshot.build(session);
  }

  function attempt(input) {
    const session = ctx.get(input.sessionId);
    const value = session.pending?.id === input.attemptId ? session.pending
      : session.current?.id === input.attemptId ? session.current : null;
    requireValue(value && !value.resourceReleased, 'STALE_SESSION');
    const work = ctx.metadata.buildWorkInfo(session);
    return {
      attemptId: value.id,
      ...clone(value.resource),
      title: work.title,
      position: value.position,
      paused: value.paused,
      ...(value.descriptor.scope ? { scope: clone(value.descriptor.scope) } : {}),
    };
  }

  async function releaseMedia(input) {
    requireValue(!ctx.isClosed(), 'CLOSED');
    requireValue(object(input) && text(input.sessionId) && text(input.attemptId) && text(input.resourceId));
    const session = sessions.get(input.sessionId);
    const resourceId = session?.leases.get(input.attemptId);
    if (!resourceId) return { released: true };
    requireValue(resourceId === input.resourceId, 'STALE_SESSION');
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
