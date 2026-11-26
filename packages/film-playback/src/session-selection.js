import { randomUUID } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { compareCandidates } from './policy.js';
import { providerFamily } from './rules.js';
import { clone, codeOf, fail, fields, identity, isIdentifier, requireValue } from './identity.js';
import { canDiscoverVariants, cancelled, manualIdentity, replaceLine, samePlayIntent, sameVariantFile } from './session-helpers.js';

export function createSessionSelection(ctx) {
  const { data, library, media, debug } = ctx;

  async function expandResultLines(session, result, detail, generation, preferredLineId = null) {
    const indices = detail.lines.map((_, idx) => idx);
    if (preferredLineId !== null && preferredLineId !== undefined) {
      indices.sort((a, b) =>
        Number(detail.lines[b]?.id === preferredLineId) - Number(detail.lines[a]?.id === preferredLineId));
    }
    for (const index of indices) {
      if (!ctx.valid(session, generation)) return false;
      const line = result.baseDetail?.lines[index] ?? detail.lines[index];
      if (!line) continue;
      if (session.playIntent.sourceFilter?.lineId !== undefined && line.id !== session.playIntent.sourceFilter.lineId) {
        continue;
      }
      // Pre-filter blocked lines BEFORE issuing pan-list expand I/O!
      if (ctx.candidates.isLineBlocked(session, result.source.key, result.source.detailId, line.id)) {
        continue;
      }
      if (session.constraint.action === 'pan' && providerFamily(line.provider) &&
          providerFamily(line.provider) !== providerFamily(session.constraint.provider)) {
        continue;
      }
      if (line.expanded || !line.episodes.some(item => item.share ||
        String(item.playId ?? item.id ?? item.url ?? '').startsWith('meow.pan.v1.'))) {
        continue;
      }
      try {
        const expanded = await ctx.runIO(session, generation, result.source, {
          accountProviders: line.episodes.map(item => item.file?.provider ?? item.share?.provider ?? item.provider ?? line.provider),
        }, () => data.expand(result.source, line, { canSchedule: () => ctx.valid(session, generation) }));
        if (!ctx.valid(session, generation)) return false;
        const currentIndex = detail.lines.findIndex(value => value.id === line.id);
        if (currentIndex < 0) continue;
        detail = replaceLine(result, detail, currentIndex, expanded);
        await ctx.candidates.ensureRecognized(session, result, 'playback');
        if (!ctx.valid(session, generation)) return false;
        ctx.publish(session);
        if (await tryCandidate(session, generation)) return true;
      } catch (error) {
        if (ctx.valid(session, generation)) {
          result.error = session.selectionError = codeOf(error);
          ctx.publish(session);
        }
      }
    }
    return false;
  }

  async function installPending(session, candidate, descriptor, resource, generation, preparedAt = Date.now(), {
    explicit = false, position, paused, clearLastFailed = false,
  } = {}) {
    if (!ctx.valid(session, generation)) { await media.release(resource.id); return false; }
    // Update session affinity so subsequent episodes prefer the same (source, line) seamlessly!
    session.affinity = {
      sourceKey: candidate.source.key,
      detailId: candidate.detailId ?? candidate.source.detailId,
      lineId: candidate.lineId,
    };
    if (candidate.episode !== undefined && candidate.episode > 0 && session.kind === 'tv') {
      session.playIntent = {
        ...session.playIntent,
        kind: 'tv',
        season: candidate.season ?? session.playIntent.season ?? 1,
        episode: candidate.episode,
        ...(candidate.globalEpisode !== undefined ? { globalEpisode: candidate.globalEpisode } : {}),
      };
    }

    const continuation = samePlayIntent(session.activePlayIntent, session.playIntent) ||
      sameVariantFile(session.actionCurrent, candidate);

    session.pending = {
      id: randomUUID(),
      candidate,
      descriptor,
      resource,
      preparedAt,
      generation,
      explicit,
      prepareOnly: session.prepareOnly,
      intent: clone(session.playIntent),
      position: position !== undefined ? position
        : continuation ? session.position
        : (!session.resumeCandidateId || session.resumeCandidateId === candidate.id ? session.resumePosition : 0),
      paused: paused !== undefined ? paused : continuation ? session.paused : false,
    };
    if (clearLastFailed) session.lastFailed = null;
    session.leases.set(session.pending.id, resource.id);
    session.stage = session.prepareOnly ? 'prepared' : 'pending';
    session.error = null;
    debug?.emit({
      level: 'info', cat: 'film-playback', sub: 'film.playback', event: 'playback.attempt.installed',
      data: {
        sessionId: session.id, attemptId: session.pending.id, candidateId: candidate.id,
        sourceKey: candidate.source.key, provider: candidate.panProvider ?? candidate.provider ?? null,
        quality: candidate.quality ?? null, prepareOnly: session.prepareOnly, explicit, continuation,
      },
    });
    ctx.publish(session);
    return true;
  }

  async function tryCandidate(session, generation, { affinityOnly = false } = {}) {
    const attempted = new Set();
    const multiSeason = ctx.browsing.isMultiSeasonSession(session);
    const preferSeason = session.playIntent.season ?? 0;

    while (ctx.valid(session, generation)) {
      let candidate = session.explicitCandidateId ? session.candidates.get(session.explicitCandidateId) : null;
      if (candidate && !ctx.candidates.candidateAllowed(session, candidate,
        { explicit: true, discoverVariants: canDiscoverVariants(candidate) })) {
        candidate = null;
      }
      if (session.explicitCandidateId && !candidate) return false;
      const explicit = Boolean(candidate);

      if (!candidate) {
        const available = [...session.candidates.values()].filter(value =>
          !attempted.has(value.id) &&
          !session.excluded.has(value.id) &&
          !(value.fileIdentity && session.excluded.has(value.fileIdentity)) &&
          ctx.candidates.candidateAllowed(session, value, { discoverVariants: canDiscoverVariants(value) }));

        const filtered = affinityOnly && session.affinity
          ? available.filter(value =>
            value.source.key === session.affinity.sourceKey &&
            (value.detailId ?? value.source.detailId) === session.affinity.detailId &&
            (session.affinity.lineId === undefined || value.lineId === session.affinity.lineId))
          : available;

        filtered.sort((a, b) => {
          if (session.resumeCandidateId) {
            const rDiff = Number(b.id === session.resumeCandidateId) - Number(a.id === session.resumeCandidateId);
            if (rDiff) return rDiff;
          }
          // Context affinity: keep continuous playback on the same (source, line) when default constraint!
          if (session.affinity && session.constraint.action === 'default') {
            const sameLine = c => c.source.key === session.affinity.sourceKey &&
              (c.detailId ?? c.source.detailId) === session.affinity.detailId &&
              (session.affinity.lineId === undefined || c.lineId === session.affinity.lineId);
            const aAff = Number(sameLine(a)), bAff = Number(sameLine(b));
            if (aAff !== bAff) return bAff - aAff;
          }
          return compareCandidates(a, b, {
            preferences: ctx.preferences(),
            constraint: session.constraint,
            current: session.current?.candidate ?? session.actionCurrent,
            multiSeason,
            preferSeason,
          });
        });
        candidate = filtered[0];
      }

      if (!ctx.valid(session, generation)) return false;
      session.explicitCandidateId = null;
      if (!candidate) return false;
      if (!ctx.currentSource(session, candidate.source)) {
        attempted.add(candidate.id);
        continue;
      }

      session.stage = 'resolving'; session.error = null; ctx.publish(session);
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
                children.sort((a, b) => compareCandidates(a, b, { preferences: ctx.preferences(), multiSeason, preferSeason }));
                session.explicitCandidateId = children[0]?.id ?? null;
              }
              reconsider = true; ctx.publish(session); return false;
            }
          }
          if (!ctx.candidates.candidateAllowed(session, candidate, { explicit })) {
            reconsider = true; return false;
          }
          if (!ctx.valid(session, generation)) return false;
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          return installPending(session, candidate, descriptor, resource, generation, Date.now(), { explicit });
        });
        if (reconsider) { attempted.add(candidate.id); continue; }
        return prepared;
      } catch (error) {
        if (!ctx.valid(session, generation)) return false;
        ctx.excluded(session, candidate);
        session.selectionError = codeOf(error);
        ctx.publish(session);
      }
    }
    return false;
  }

  async function consumeResult(session, result, generation, preferredLineId = null) {
    if (!ctx.valid(session, generation) || !result || !ctx.currentSource(session, result.source)) return false;
    if (result.source.engine === 'pan') return false;
    const detail = await ctx.candidates.loadDetail(session, result, generation, false, false);
    if (!detail || !ctx.valid(session, generation)) return false;
    if (await tryCandidate(session, generation)) return true;
    return expandResultLines(session, result, detail, generation, preferredLineId);
  }

  async function manualOutcome(row, error) {
    if (error && cancelled(error)) return;
    await library.update(document => {
      const entry = document.manual.find(item => item?.id === row.id);
      if (!entry || entry.enabled === false || manualIdentity(entry) !== manualIdentity(row)) return;
      entry.failureCount = error ? entry.failureCount + 1 : 0;
      if (entry.autoDisable && entry.failureCount >= 3) entry.enabled = false;
    });
  }

  async function tryHistoryShareBootstrap(session, savedHistory, generation) {
    if (!savedHistory?.share?.provider || !savedHistory.share.url) return false;
    if (ctx.candidates.isLineBlocked(session, savedHistory.sourceKey, savedHistory.detailId, savedHistory.lineId)) {
      return false;
    }
    const source = savedHistory.sourceKey?.startsWith('manual:')
      ? { key: savedHistory.sourceKey, engine: 'pan', name: savedHistory.lineLabel || '历史网盘', detailId: savedHistory.detailId }
      : session.sources.find(s => s.key === savedHistory.sourceKey);
    if (!source || (source.engine !== 'pan' && !ctx.smartSourceAllowed(session, source))) return false;

    try {
      const line = await ctx.runIO(session, generation, source, { provider: savedHistory.share.provider }, () =>
        data.expand({ ...source, detailId: savedHistory.detailId }, {
          id: savedHistory.lineId,
          label: savedHistory.lineLabel || savedHistory.share.provider,
          provider: savedHistory.share.provider,
          episodes: [{
            id: savedHistory.lineId,
            name: savedHistory.title || session.fallbackTitle,
            share: { ...savedHistory.share },
          }],
        }, { canSchedule: () => ctx.valid(session, generation) }));
      if (!ctx.valid(session, generation)) return false;

      await ctx.candidates.addResults(session, source, [{ id: savedHistory.detailId, title: savedHistory.title }], {
        kind: 'linked',
        admitted: () => ctx.valid(session, generation),
      });
      const result = session.results.find(r => r.source.key === source.key && r.source.detailId === savedHistory.detailId);
      if (!result) return false;
      result.linkedHistory = true;
      if (!result.detail) {
        result.baseDetail = { lines: [line], detailId: savedHistory.detailId };
        result.detail = { lines: [line], detailId: savedHistory.detailId };
      } else {
        const idx = result.detail.lines.findIndex(l => l.id === line.id);
        if (idx >= 0) replaceLine(result, result.detail, idx, line);
        else result.detail = { ...result.detail, lines: [line, ...result.detail.lines] };
      }
      await ctx.candidates.ensureRecognized(session, result, 'playback');
      if (!ctx.valid(session, generation)) return false;
      return await tryCandidate(session, generation);
    } catch {
      return false;
    }
  }

  async function run(session, generation) {
    try {
      await session.initialization;
      if (!ctx.valid(session, generation)) return;
      if (session.noSources) {
        session.stage = 'unavailable'; session.error = 'NO_VIDEO_SOURCES'; ctx.publish(session); return;
      }
      ctx.metadata.requireFreshMetadata(session);

      // Direct file click or single-site Category entry (before user clicks "加载更多"):
      if (session.playIntent.mode === 'direct' || session.playIntent.sourceFilter) {
        if (await tryCandidate(session, generation)) return;
        const filter = session.playIntent.sourceFilter;
        if (filter) {
          const result = session.results.find(r => r.source.key === filter.sourceKey && r.source.detailId === filter.detailId);
          if (result) {
            const detail = await ctx.candidates.loadDetail(session, result, generation, false, false);
            if (detail && ctx.valid(session, generation)) {
              // If episode wasn't chosen yet on a single-site category card, pick first episode/file now!
              if (session.kind === 'tv' && session.playIntent.episode === undefined && !session.explicitCandidateId) {
                const cands = result.recognitionCache?.candidates ?? [];
                const firstPlayable = cands.find(c => !c.share || c.file);
                if (firstPlayable) {
                  if (firstPlayable.episode > 0) {
                    session.playIntent = { ...session.playIntent, season: firstPlayable.season ?? 1, episode: firstPlayable.episode };
                  } else {
                    session.explicitCandidateId = firstPlayable.id;
                  }
                }
              }
              if (await tryCandidate(session, generation)) return;
              if (await expandResultLines(session, result, detail, generation, filter.lineId)) {
                return;
              }
              // After expanding share line on single-site card, pick first playable candidate if needed:
              if (session.kind === 'tv' && session.playIntent.episode === undefined && !session.explicitCandidateId) {
                const cands = result.recognitionCache?.candidates ?? [];
                const firstPlayable = cands.find(c => !c.share || c.file);
                if (firstPlayable) {
                  session.explicitCandidateId = firstPlayable.id;
                  if (await tryCandidate(session, generation)) return;
                }
              }
            }
          }
        }
        if (!ctx.valid(session, generation)) return;
        session.stage = 'unavailable';
        session.error = ctx.selectionError(session, 'MEDIA_UNAVAILABLE');
        ctx.publish(session);
        return;
      }

      // Ensure TMDB season metadata is fresh when playing a TV episode with TMDB mounted
      const primaryMeta = ctx.metadata.playbackMetadataSource(session);
      if (session.kind === 'tv' && primaryMeta?.provider === 'tmdb' && session.playIntent.season !== undefined) {
        await ctx.metadata.ensureSeasonLoaded(session, primaryMeta, session.playIntent.season, {
          canSchedule: () => ctx.valid(session, generation),
        });
        if (!ctx.valid(session, generation)) return;
      }

      // =========================================================================
      // Stage 0: Cached Context Candidates (Prefetch -> Affinity -> Smart List / Session Cache)
      // =========================================================================
      session.stage = 'cached'; ctx.publish(session);
      const neighborKey = identity(session.playIntent);
      const neighbor = session.prefetch.get(neighborKey);
      if (neighbor && neighbor.state !== 'error' && ctx.preparedValid(neighbor) &&
          ctx.candidates.candidateAllowed(session, neighbor.candidate)) {
        session.prefetch.delete(neighborKey);
        await media.cancelPrefetch(neighbor.resource.id);
        if (await installPending(session, neighbor.candidate, neighbor.descriptor, neighbor.resource, generation, neighbor.preparedAt)) {
          return;
        }
      }
      if (!ctx.valid(session, generation)) return;
      if (session.affinity && session.constraint.action === 'default' &&
          await tryCandidate(session, generation, { affinityOnly: true })) {
        return;
      }
      if (await tryCandidate(session, generation)) return;

      // If an affine source (e.g. raw split card's Site 2) hasn't loaded its detail yet, consume ONLY it first!
      const visited = new Set();
      if (session.affinity && session.constraint.action === 'default') {
        const affineResult = session.results.find(r =>
          r.source.key === session.affinity.sourceKey && r.source.detailId === session.affinity.detailId);
        if (affineResult && !affineResult.attachedDisplayOnly) {
          visited.add(affineResult.id);
          if (await consumeResult(session, affineResult, generation, session.affinity.lineId)) return;
        }
      }

      // =========================================================================
      // Stage 1: Playback History (`history_list` direct pan share -> `history_detail`)
      // =========================================================================
      const savedHistory = await library.history(session.targetId);
      if (!ctx.valid(session, generation)) return;
      if (savedHistory) {
        session.stage = 'history'; ctx.publish(session);
        // 1a. history_list: direct pan share expansion without spider detail!
        if (await tryHistoryShareBootstrap(session, savedHistory, generation)) return;
        if (!ctx.valid(session, generation)) return;
        // 1b. history_detail: load historical site detail prioritizing historical lineId!
        const histSource = session.sources.find(s => s.key === savedHistory.sourceKey);
        if (histSource && isIdentifier(savedHistory.detailId) && ctx.smartSourceAllowed(session, histSource)) {
          await ctx.candidates.addResults(session, histSource, [{ id: savedHistory.detailId, title: savedHistory.title }], {
            kind: 'linked',
            admitted: () => ctx.valid(session, generation),
          });
          const histResult = session.results.find(r => r.source.key === histSource.key && r.source.detailId === savedHistory.detailId);
          if (histResult && !visited.has(histResult.id)) {
            histResult.linkedHistory = true;
            visited.add(histResult.id);
            if (await consumeResult(session, histResult, generation, savedHistory.lineId)) return;
          }
        }
      }

      // =========================================================================
      // Stage 2: Manual Matches (`manual_list` pan share & `manual_detail` site)
      // =========================================================================
      const manualRows = library.read().manual.filter(row => row?.targetId === session.targetId && row.enabled !== false);
      if (manualRows.length) {
        session.stage = 'manual'; ctx.publish(session);
        for (const row of manualRows) {
          if (!ctx.valid(session, generation)) return;
          if (row.errorCode) {
            session.selectionError = row.errorCode;
            continue;
          }
          if (row.share) {
            const source = {
              key: `manual:${row.id}`,
              engine: 'pan',
              name: row.name || '人工匹配',
              detailId: row.id,
              ...(row.seasonHint !== undefined ? { manualSeasonHint: row.seasonHint } : {}),
            };
            try {
              const line = await ctx.runIO(session, generation, source, { provider: row.provider }, () =>
                data.expand(source, {
                  id: row.id,
                  label: row.name || row.provider,
                  provider: row.provider,
                  episodes: [{ id: row.id, name: row.name || session.fallbackTitle, share: { ...row.share, provider: row.provider } }],
                }, { canSchedule: () => ctx.valid(session, generation) }));
              if (!ctx.valid(session, generation)) return;
              await ctx.candidates.addResults(session, source, [{ id: row.id, title: row.title || session.fallbackTitle }], {
                kind: 'linked',
                admitted: () => ctx.valid(session, generation),
              });
              const result = session.results.find(v => v.id === identity(source.key, row.id));
              if (!result) continue;
              const detail = { lines: [line], detailId: row.id };
              Object.assign(result, {
                manualId: row.id,
                manualIdentity: manualIdentity(row),
                state: 'ready',
                error: null,
                baseDetail: detail,
                detail: isDeepStrictEqual(result.detail, detail) ? result.detail : detail,
              });
              await ctx.candidates.ensureRecognized(session, result, 'playback');
              await ctx.tracked(manualOutcome(row, null));
              if (!ctx.valid(session, generation)) return;
              if (await tryCandidate(session, generation)) return;
            } catch (error) {
              if (!ctx.valid(session, generation)) return;
              await ctx.tracked(manualOutcome(row, error));
              session.selectionError = codeOf(error);
              ctx.publish(session);
            }
            continue;
          }

          const source = session.sources.find(s => s.key === row.sourceKey);
          if (!source || !isIdentifier(row.detailId) || source.enabled === false) continue;
          await ctx.candidates.addResults(session, source, [{ id: row.detailId, title: row.title || session.fallbackTitle }], {
            kind: 'linked',
            admitted: () => ctx.valid(session, generation),
          });
          const result = session.results.find(r => r.source.key === source.key && r.source.detailId === row.detailId);
          if (!result || visited.has(result.id)) continue;
          result.manualId = row.id;
          result.manualIdentity = manualIdentity(row);
          if (row.seasonHint !== undefined) result.source.manualSeasonHint = row.seasonHint;
          visited.add(result.id);
          const found = await consumeResult(session, result, generation);
          if (ctx.valid(session, generation)) {
            await ctx.tracked(manualOutcome(row, result.error ? fail(result.error) : null));
          }
          if (found) return;
        }
      }

      // =========================================================================
      // Stage 3: Smart Search (Lightweight Pre-Sorted Results -> Progressive Site Search)
      // =========================================================================
      session.stage = 'searching'; ctx.publish(session);
      let targetSeason = session.playIntent.season ?? 0;
      let targetEpisode = session.playIntent.episode ?? 0;

      const trySortedResults = async candidatesList => {
        const sorted = ctx.candidates.sortResultsForSmartSearch(session, candidatesList, targetSeason, targetEpisode);
        for (const result of sorted) {
          if (!ctx.valid(session, generation)) return false;
          if (visited.has(result.id)) continue;
          if (!ctx.smartSourceAllowed(session, result.source)) continue;
          if (!await ctx.candidates.smartResultEligible(session, result, targetSeason)) continue;
          visited.add(result.id);
          if (await consumeResult(session, result, generation)) return true;
        }
        return false;
      };

      if (await trySortedResults(session.results)) return;

      if (session.kind === 'tv' && !ctx.metadata.playbackMetadata(session) && ctx.browsing.hasSmartList(session)) {
        const { orderedSeasons, seasonEpisodes } = ctx.browsing.smartListSeasons(session);
        if (orderedSeasons.length > 0 && !orderedSeasons.includes(targetSeason)) {
          const nextSeason = orderedSeasons[0];
          const eps = [...(seasonEpisodes.get(nextSeason)?.keys() ?? [])].sort((a, b) => a - b);
          const nextEpisode = eps.length && !eps.includes(targetEpisode) ? eps[0] : (targetEpisode || 1);
          session.browsing.season = nextSeason;
          session.playIntent = { ...session.playIntent, season: nextSeason, episode: nextEpisode };
          targetSeason = nextSeason;
          targetEpisode = nextEpisode;
          ctx.publish(session);
          if (await tryCandidate(session, generation)) return;
          if (await trySortedResults(session.results)) return;
        }
      }

      for (const source of session.sources) {
        if (!ctx.valid(session, generation)) return;
        if (!ctx.smartSourceAllowed(session, source)) continue;
        const state = session.search.get(source.key);
        while (ctx.valid(session, generation) && state && !state.completed && !state.error) {
          const ok = await ctx.runIO(session, generation, source, null, () => ctx.candidates.searchPage(session, source));
          if (!ok || !ctx.valid(session, generation)) break;
          // Refresh Smart List / source menu if no metadata was mounted and >=2 results arrived!
          if (session.metadataSources.length === 0 && session.browsing.sourceId === null && ctx.browsing.hasSmartList(session)) {
            session.browsing.sourceId = 'smart';
            ctx.publish(session);
          } else if (session.metadataSources.length === 0 && session.browsing.sourceId === null && session.results.length > 0) {
            session.browsing.sourceId = session.results[0].id;
            ctx.publish(session);
          }
          const siteResults = session.results.filter(r => r.source.key === source.key);
          if (await trySortedResults(siteResults)) return;
        }
      }

      if (!ctx.valid(session, generation)) return;
      session.discoveryComplete = true;
      // Relax early-stop quality/pan gate once all sources have been searched & inspected.
      if (await tryCandidate(session, generation)) return;

      session.stage = 'unavailable';
      session.error = ctx.selectionError(session,
        [...session.search.values()].find(v => v.error)?.error ?? 'NO_MATCH');
      ctx.publish(session);
    } catch (error) {
      if (ctx.valid(session, generation)) {
        session.stage = 'error';
        session.error = ctx.selectionError(session, codeOf(error));
        ctx.publish(session);
      }
    }
  }

  function start(session, {
    prepareOnly = false,
    constraint = { action: 'default' },
    explicitCandidateId = null,
    feedbackFailure = null,
  } = {}) {
    const requestKey = identity(session.playIntent, constraint, explicitCandidateId);
    if (session.runKey === requestKey && session.runWork && !session.stopped) {
      if (session.prepareOnly && !prepareOnly) {
        session.prepareOnly = false;
        if (session.pending && !session.pending.resourceReleased) {
          session.pending.prepareOnly = false;
          session.stage = 'pending';
          ctx.publish(session);
        }
      }
      return session.runWork;
    }
    const previous = session.pending;
    void ctx.cancelCalculations(session, 'playback');
    session.generation++;
    session.stopped = false;
    session.prepareOnly = prepareOnly;
    session.pending = null;
    session.explicitCandidateId = explicitCandidateId;
    session.feedbackFailure = feedbackFailure;
    session.selectionError = null;
    session.constraint = clone(constraint);
    session.actionCurrent = session.current?.candidate ?? session.actionCurrent;
    session.error = null;
    session.stage = session.noSources ? 'unavailable' : 'cached';
    if (session.noSources) session.error = 'NO_VIDEO_SOURCES';
    ctx.publish(session);
    if (previous) ctx.detached(session, ctx.retire(session, previous));
    if (session.noSources) return Promise.resolve();

    const work = ctx.tracked(run(session, session.generation));
    session.runKey = requestKey;
    session.runWork = work;
    session.runGeneration = session.generation;
    void work.finally(() => { if (session.runWork === work) session.runWork = null; }).catch(() => {});
    return work;
  }

  function adjacent(session, direction) {
    if (session.kind === 'movie') return null;
    const primaryMeta = ctx.metadata.playbackMetadata(session);
    if (primaryMeta) {
      const visible = ctx.metadata.episodeProjection(session, primaryMeta).visible;
      const idx = visible.findIndex(v =>
        (v.globalEpisode !== undefined && v.globalEpisode === session.playIntent.globalEpisode) ||
        (v.season === session.playIntent.season && v.episode === session.playIntent.episode));
      const next = idx >= 0 ? visible[idx + direction] : null;
      if (!next) return null;
      return {
        intent: {
          mode: 'smart',
          kind: 'tv',
          season: next.season,
          episode: next.episode,
          ...(next.globalEpisode !== undefined ? { globalEpisode: next.globalEpisode } : {}),
        },
      };
    }

    if (ctx.browsing.hasSmartList(session)) {
      const { orderedSeasons, seasonEpisodes } = ctx.browsing.smartListSeasons(session);
      const flat = [];
      for (const s of orderedSeasons) {
        const eps = [...(seasonEpisodes.get(s)?.keys() ?? [])].sort((a, b) => a - b);
        for (const ep of eps) flat.push({ season: s, episode: ep });
      }
      const idx = flat.findIndex(v => v.season === session.playIntent.season && v.episode === session.playIntent.episode);
      const next = idx >= 0 ? flat[idx + direction] : null;
      if (!next) return null;
      return { intent: { mode: 'smart', kind: 'tv', season: next.season, episode: next.episode } };
    }

    const activeCand = session.current?.candidate ?? session.pending?.candidate;
    if (!activeCand) return null;
    const result = session.results.find(r => r.source.key === activeCand.source.key && r.source.detailId === activeCand.detailId);
    const line = result?.detail?.lines.find(l => l.id === activeCand.lineId);
    const episodes = (line?.episodes ?? []).filter(ep => !ep.share || ep.file);
    const idx = episodes.findIndex(ep => ep.id === activeCand.episodeId);
    const nextEp = idx >= 0 ? episodes[idx + direction] : null;
    if (!nextEp) return null;
    const nextCand = (result?.recognitionCache?.candidates ?? []).find(c =>
      c.lineId === line.id && c.episodeId === nextEp.id && !c.variant);
    if (!nextCand) return null;
    return {
      intent: {
        mode: 'direct',
        kind: session.kind,
        candidateId: nextCand.id,
        ...(nextCand.season ? { season: nextCand.season } : {}),
        ...(nextCand.episode ? { episode: nextCand.episode } : {}),
      },
      candidateId: nextCand.id,
    };
  }

  async function selectItem(session, itemId, { prepareOnly = false } = {}) {
    requireValue(typeof itemId === 'string' && itemId.length > 0);
    session.userSelected = true;

    if (itemId.startsWith('ep:')) {
      const parts = itemId.slice(3).split(':').map(Number);
      const [season, episode, globalEpisode] = parts;
      requireValue(Number.isSafeInteger(season) && Number.isSafeInteger(episode) && episode > 0);
      const nextIntent = {
        mode: 'smart',
        kind: 'tv',
        season,
        episode,
        ...(Number.isSafeInteger(globalEpisode) && globalEpisode > 0 ? { globalEpisode } : {}),
      };
      if (!samePlayIntent(session.playIntent, nextIntent)) {
        session.excluded.clear();
        session.resumePosition = 0;
        session.resumeCandidateId = null;
      }
      session.playIntent = nextIntent;
      session.browsing.season = season;
      void start(session, { prepareOnly });
      return;
    }

    if (itemId.startsWith('smart:')) {
      const parts = itemId.slice(6).split(':').map(Number);
      const [season, episode] = parts;
      requireValue(Number.isSafeInteger(season) && season > 0 && Number.isSafeInteger(episode) && episode > 0);
      const nextIntent = { mode: 'smart', kind: 'tv', season, episode };
      if (!samePlayIntent(session.playIntent, nextIntent)) {
        session.excluded.clear();
        session.resumePosition = 0;
        session.resumeCandidateId = null;
      }
      session.playIntent = nextIntent;
      session.browsing.season = season;
      void start(session, { prepareOnly });
      return;
    }

    if (itemId.startsWith('cand:')) {
      const candidateId = itemId.slice(5);
      const candidate = session.candidates.get(candidateId);
      requireValue(candidate, 'NOT_FOUND');
      session.excluded.clear();
      session.resumePosition = 0;
      session.resumeCandidateId = null;
      session.playIntent = {
        mode: 'direct',
        kind: session.kind,
        candidateId: candidate.id,
        ...(candidate.season ? { season: candidate.season } : {}),
        ...(candidate.episode ? { episode: candidate.episode } : {}),
        ...(candidate.globalEpisode ? { globalEpisode: candidate.globalEpisode } : {}),
      };
      void start(session, { prepareOnly, explicitCandidateId: candidate.id });
      return;
    }

    throw fail('INVALID_REQUEST');
  }

  async function moveEpisode(session, direction) {
    const next = adjacent(session, direction);
    requireValue(next, 'NO_MATCH');
    session.userSelected = true;
    session.excluded.clear();
    session.resumePosition = 0;
    session.resumeCandidateId = null;
    session.playIntent = next.intent;
    if (next.intent.season) session.browsing.season = next.intent.season;
    void start(session, { explicitCandidateId: next.candidateId ?? null });
  }

  async function retryMedia(session, attemptId) {
    const selected = attemptId === undefined ? session.pending ?? session.current ?? session.lastFailed
      : [session.pending, session.current, session.lastFailed].find(value => value?.id === attemptId);
    requireValue(selected, 'STALE_SESSION');
    requireValue(ctx.currentSource(session, selected.candidate.source), 'STALE_SOURCE');
    const generation = ++session.generation;
    session.runKey = null;
    session.stopped = false;
    session.feedbackFailure = null;
    session.selectionError = null;
    session.excluded.delete(selected.candidate.id);
    if (selected.candidate.fileIdentity) session.excluded.delete(selected.candidate.fileIdentity);
    session.prepareOnly = selected.prepareOnly;
    const previous = session.pending;
    session.pending = null;
    if (previous) ctx.detached(session, ctx.retire(session, previous));
    session.stage = 'resolving'; session.error = null;
    ctx.publish(session);

    const work = ctx.tracked((async () => {
      try {
        await ctx.runIO(session, generation, selected.candidate.source, selected.candidate, async () => {
          const descriptor = await data.refresh(selected.candidate, { canSchedule: () => ctx.valid(session, generation) });
          if (!ctx.valid(session, generation)) return;
          await ctx.candidates.registerVariants(session, selected.candidate, descriptor);
          if (!ctx.valid(session, generation)) return;
          const candidate = selected.candidate.variant
            ? session.candidates.get(selected.candidate.id) ?? selected.candidate
            : selected.candidate;
          const resource = await media.prepare(descriptor, { scope: descriptor.scope, cache: session.shortVideo });
          await installPending(session, candidate, descriptor, resource, generation, Date.now(), {
            explicit: true, position: selected.position, paused: selected.paused, clearLastFailed: true,
          });
        });
      } catch (error) {
        if (ctx.valid(session, generation)) {
          session.stage = 'error';
          session.error = codeOf(error);
          ctx.publish(session);
        }
      }
    })());
    session.runWork = work;
    session.runGeneration = generation;
    void work.finally(() => { if (session.runWork === work) session.runWork = null; }).catch(() => {});
  }

  return {
    start,
    adjacent,
    selectItem,
    moveEpisode,
    retryMedia,
  };
}
