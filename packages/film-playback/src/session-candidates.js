import { isDeepStrictEqual } from 'node:util';
import { isCandidateAllowed, isItemBlocked, isLineBlocked } from './policy.js';
import { contentResults, matchesResult, mediaVariants, normalizeDetail, recognizeCandidates, recognizeVariantCandidates } from './recognition.js';
import { createRecognitionRules, providerFamily, qualityKey } from './rules.js';
import { validateBlock } from './library.js';
import { clone, codeOf, fields, identity, isIdentifier, normalizeWorkKey, object, requireValue,
  resultDetailId, text } from './identity.js';
import { extractMaxEpisodeFromRemark, manualIdentity } from './session-helpers.js';

export function createSessionCandidates(ctx) {
  const { data, library, sourceRegistry, debug } = ctx;

  function computeMatchScore(query, rawTitle) {
    const q = normalizeWorkKey(query);
    const t = normalizeWorkKey(rawTitle);
    if (!q || !t) return 0;
    if (t === q) return 1000;
    if (t.startsWith(q)) return 900;
    const idx = t.indexOf(q);
    if (idx >= 0) return 800 + Math.max(0, 60 - idx);
    return 0;
  }

  async function addResults(session, source, items, {
    kind = 'result',
    attachedDisplayOnly = false,
    admitted = () => true,
  } = {}) {
    if (!source || !Array.isArray(items) || !items.length) return false;
    const unchecked = items.filter(raw => kind === 'search' && !attachedDisplayOnly);
    let acceptedSet = null;
    if (unchecked.length) {
      const target = {
        kind: session.kind === 'movie' ? 'movie' : 'tv',
        title: session.keyword,
        ...(session.tmdbSlot ? { metadataProvider: 'tmdb', metadataId: String(session.tmdbSlot.id) } : {}),
      };
      const matching = await ctx.calculate(session, signal =>
        contentResults(unchecked, { target, rules: session.rules, signal }), 'session');
      if (ctx.isClosed() || session.released || !admitted()) return false;
      acceptedSet = new Set(unchecked.filter((_, idx) => matching[idx]));
    }

    const recognition = createRecognitionRules(session.rules);
    for (const raw of items) {
      const detailId = resultDetailId(raw);
      if (!isIdentifier(detailId)) continue;
      if (kind === 'search') session.search.get(source.key)?.seen.add(detailId);
      if (acceptedSet && !acceptedSet.has(raw)) continue;

      const id = identity(source.key, detailId);
      const rawTitle = text(raw.rawTitle ?? raw.vod_name ?? raw.title ?? raw.name) || session.fallbackTitle;
      const remark = text(raw.textBadge ?? raw.vod_remarks ?? raw.remark);
      let seasonHint = Number.isSafeInteger(raw.seasonHint) && raw.seasonHint > 0 ? raw.seasonHint : undefined;
      if (seasonHint === undefined && session.kind !== 'movie' && rawTitle) {
        const extracted = await ctx.calculate(session, signal =>
          recognition.season({ rawTitle, vod_remarks: remark }, { signal }), 'session').catch(() => null);
        if (Number.isSafeInteger(extracted) && extracted > 0) seasonHint = extracted;
      }
      if (ctx.isClosed() || session.released || !admitted()) return false;

      if (session.resultKeys.has(id)) {
        const existing = session.results.find(item => item.id === id);
        if (existing) {
          if (!attachedDisplayOnly) existing.attachedDisplayOnly = false;
          if (rawTitle && !existing.rawTitle) existing.rawTitle = rawTitle;
          if (remark && !existing.remark) {
            existing.remark = remark;
            existing.remarkMaxEpisode = extractMaxEpisodeFromRemark(remark);
          }
          if (seasonHint && !existing.seasonHint) existing.seasonHint = seasonHint;
        }
        continue;
      }

      session.resultKeys.add(id);
      session.results.push({
        id,
        source: { ...source, detailId, rawTitle, remark },
        title: rawTitle,
        rawTitle,
        remark,
        raw: clone(raw.raw ?? raw),
        seasonHint: seasonHint ?? 0,
        remarkMaxEpisode: extractMaxEpisodeFromRemark(`${remark} ${rawTitle}`),
        matchScore: Number.isFinite(raw.score) ? raw.score : computeMatchScore(session.keyword, rawTitle),
        attachedDisplayOnly,
        state: 'idle',
        error: null,
      });
    }
    return true;
  }

  /**
   * Single-pass recognition cache!
   * Recognizes `(sourceKey, detailId)` at most once per `(detail.lines, rules.revision,
   * metadataMappingRef, effectiveSeasonHint, panMappingsRevision, variants)` and shares
   * the immutable result across directory browsing, `智能列表`, smart playback, and prefetch.
   */
  async function ensureRecognized(session, result, lifetime = 'session') {
    if (!result?.detail || ctx.isClosed() || session.released || !ctx.currentSource(session, result.source)) {
      return [];
    }
    const detail = result.detail;
    const linesRef = detail.lines;
    const metadata = ctx.metadata.playbackMetadata(session);
    const baseline = ctx.metadata.baselineMetadata(session);
    const panInfo = sourceRegistry.panMappingsInfo();
    const manual = result.manualId ? ctx.currentManual(session, result) : null;
    const manualKey = manual ? manualIdentity(manual) : '';
    const effectiveManualSeason = manual?.seasonHint ?? result.source.manualSeasonHint;
    const titleSeasonHint = result.seasonHint > 0 ? result.seasonHint : undefined;

    const cacheSig = {
      linesRef,
      rulesRev: session.rules.revision,
      kind: session.kind,
      metadataRef: metadata,
      baselineRef: baseline,
      panRev: panInfo.revision,
      manualKey,
      manualSeason: effectiveManualSeason,
      titleSeasonHint,
      variantsRef: result.variants,
    };
    const prev = result.recognitionCache;
    if (prev &&
        prev.linesRef === cacheSig.linesRef &&
        prev.rulesRev === cacheSig.rulesRev &&
        prev.kind === cacheSig.kind &&
        prev.metadataRef === cacheSig.metadataRef &&
        prev.baselineRef === cacheSig.baselineRef &&
        prev.panRev === cacheSig.panRev &&
        prev.manualKey === cacheSig.manualKey &&
        prev.manualSeason === cacheSig.manualSeason &&
        prev.titleSeasonHint === cacheSig.titleSeasonHint &&
        prev.variantsRef === cacheSig.variantsRef) {
      return prev.candidates;
    }

    const source = { ...result.source };
    if (effectiveManualSeason !== undefined) source.manualSeasonHint = effectiveManualSeason;
    else delete source.manualSeasonHint;

    const baseCandidates = (await ctx.calculate(session, signal =>
      recognizeCandidates(detail, {
        kind: session.kind,
        source,
        metadata,
        baselineMetadata: baseline,
        rules: session.rules,
        panMappings: panInfo.mappings,
        titleSeasonHint,
        rawTitle: result.rawTitle || result.title,
        signal,
        completeDirectory: true,
      }), lifetime))
      .map(cand => manual ? { ...cand, manualId: manual.id, manualIdentity: manualKey } : cand);

    if (ctx.isClosed() || session.released || result.detail?.lines !== linesRef) return [];

    const expanded = [];
    for (const candidate of baseCandidates) {
      const variants = result.variants?.get(candidate.id);
      if (variants?.length) {
        const children = await ctx.calculate(session, signal =>
          recognizeVariantCandidates(candidate, variants, { rules: session.rules, signal }), lifetime);
        expanded.push(...children);
      } else {
        expanded.push(candidate);
      }
    }
    if (ctx.isClosed() || session.released || result.detail?.lines !== linesRef) return [];

    const frozen = Object.freeze(expanded.map(c => Object.freeze({ ...c })));
    // Update session.candidates in place for this result without wiping other results!
    for (const [id, cand] of session.candidates) {
      if (cand.source.key === result.source.key && cand.detailId === result.source.detailId) {
        session.candidates.delete(id);
      }
    }
    for (const cand of frozen) session.candidates.set(cand.id, cand);

    result.recognitionCache = { ...cacheSig, candidates: frozen };
    debug?.emit({
      level: 'debug', cat: 'film-playback', sub: 'film.playback', event: 'playback.candidates.recognized',
      data: { sessionId: session.id, sourceKey: result.source.key, detailId: result.source.detailId, count: frozen.length },
    });
    return frozen;
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
    await ensureRecognized(session, result, 'playback');
    return true;
  }

  async function smartResultEligible(session, result, targetSeason) {
    if (!result || result.attachedDisplayOnly) return false;
    const blocks = library.read().blocks;
    if (isItemBlocked(blocks, session.targetId, result.source.key, result.source.detailId)) return false;
    if (result.manualId) return Boolean(ctx.currentManual(session, result));
    if (result.linkedHistory) return true;
    // If the site card explicitly specifies a different season (e.g. 第2季 when playing 第1季), skip in smart match!
    if (session.kind === 'tv' && targetSeason > 0 && result.seasonHint > 0 && result.seasonHint !== targetSeason) {
      const metadata = ctx.metadata.playbackMetadata(session);
      const seasons = metadata?.doubanSeasons ?? metadata?.seasons ?? [];
      if (!metadata || seasons.filter(s => (s.season_number ?? s.season) > 0).length !== 1) {
        return false;
      }
    }
    const metadata = ctx.metadata.playbackMetadata(session);
    if (!metadata) return true;
    const target = {
      ...metadata,
      kind: session.kind,
      title: session.keyword,
      ...(targetSeason > 0 ? { season: targetSeason } : {}),
    };
    const cacheKey = identity(result.raw, session.rules.revision, session.kind, session.keyword, targetSeason);
    if (result.smartMatchCache?.key === cacheKey) return result.smartMatchCache.value;
    const matched = await ctx.calculate(session, signal =>
      matchesResult(result.raw, { target, rules: session.rules, signal }), 'playback');
    result.smartMatchCache = { key: cacheKey, value: matched };
    return matched;
  }

  function candidateAllowed(session, candidate, {
    gate = true,
    explicit = false,
    intent = session.playIntent,
    retry = false,
    discoverVariants = false,
  } = {}) {
    if (!candidate) return false;
    if (!retry && (session.excluded.has(candidate.id) ||
        candidate.fileIdentity !== undefined && session.excluded.has(candidate.fileIdentity))) {
      return false;
    }
    if (candidate.share && !candidate.file) return false;
    if (!ctx.currentSource(session, candidate.source)) return false;

    const result = session.results.find(r => r.source.key === candidate.source.key &&
      r.source.detailId === (candidate.detailId ?? candidate.source.detailId));
    if (!explicit && result?.attachedDisplayOnly) return false;
    if (candidate.manualId && !ctx.currentManual(session, candidate)) return false;

    if (!explicit) {
      if (intent.sourceFilter) {
        if (!ctx.sourceAllowed(session, candidate.source)) return false;
      } else if (!candidate.manualId && !result?.linkedHistory && !ctx.smartSourceAllowed(session, candidate.source)) {
        return false;
      }
    }

    const blocks = library.read().blocks.filter(row => row?.targetId === session.targetId);
    for (const row of blocks) {
      if (!row.errorCode) validateBlock(row, 'CORRUPT_STORAGE');
    }

    const constraint = explicit || (discoverVariants && session.constraint.action === 'quality')
      ? { action: 'default' }
      : session.constraint;

    const sourceFilter = explicit
      ? { candidateId: candidate.id }
      : (intent.mode === 'direct' && intent.candidateId ? { candidateId: intent.candidateId } : intent.sourceFilter);

    if (!isCandidateAllowed(candidate, {
      targetId: session.targetId,
      kind: intent.kind ?? session.kind,
      season: intent.season,
      episode: intent.episode,
      globalEpisode: intent.globalEpisode,
      constraint,
      excluded: retry ? new Set() : session.excluded,
      blocks,
      sourceFilter,
    })) {
      return false;
    }

    if (session.kind === 'tv' && !explicit) {
      const metadata = ctx.metadata.playbackMetadata(session);
      if (metadata) {
        const projection = ctx.metadata.episodeProjection(session, metadata);
        const key = `${candidate.season}:${candidate.episode}`;
        if (projection.keys.has(key) && !projection.visibleKeys.has(key)) return false;
      }
    }

    if (!gate || explicit || session.discoveryComplete) return true;
    if (session.constraint.action === 'pan') {
      return ['8k', '4k', '4k_hdr', '4k_fps'].includes(qualityKey(candidate.quality) || String(candidate.quality || '').toLowerCase());
    }
    if (session.constraint.action === 'quality') {
      const currentCand = session.current?.candidate ?? session.actionCurrent;
      const family = providerFamily(currentCand?.panProvider ?? currentCand?.provider);
      return !family || providerFamily(candidate.panProvider ?? candidate.provider) === family;
    }
    return true;
  }

  async function loadDetail(session, result, generation, browsing = false, refresh = false) {
    const blocks = library.read().blocks;
    if (!browsing && isItemBlocked(blocks, session.targetId, result.source.key, result.source.detailId)) {
      return null;
    }
    const admitted = () => !ctx.isClosed() && !session.released &&
      ctx.currentSource(session, result.source) &&
      (browsing ? session.browseGeneration === generation : ctx.valid(session, generation));
    if (!refresh && result.detail && result.detailEntry) {
      await ensureRecognized(session, result, browsing ? 'session' : 'playback');
      return result.detail;
    }
    result.state = 'loading'; result.error = null; ctx.publish(session);
    try {
      const read = () => data.detailEntry(result.source, result.source.detailId, { canSchedule: admitted, refresh });
      const value = await (browsing ? read() : ctx.runIO(session, generation, result.source, null, read));
      if (!admitted()) return null;
      if (!result.detail || result.detailEntry !== value &&
          !isDeepStrictEqual(result.detailEntry?.value, value.value)) {
        result.baseDetail = normalizeDetail(clone(value.value), { source: result.source });
        result.detail = { ...result.baseDetail, lines: [...result.baseDetail.lines] };
        if (result.baseDetail.rawTitle && !result.rawTitle) result.rawTitle = result.baseDetail.rawTitle;
        if (result.baseDetail.remark && !result.remark) {
          result.remark = result.baseDetail.remark;
          result.remarkMaxEpisode = extractMaxEpisodeFromRemark(`${result.remark} ${result.rawTitle}`);
        }
      }
      result.detailEntry = value;
      await ensureRecognized(session, result, browsing ? 'session' : 'playback');
      if (!admitted()) return null;
      result.state = 'ready'; result.error = null; ctx.publish(session);
      return result.detail;
    } catch (error) {
      if (admitted()) {
        result.state = 'error'; result.error = codeOf(error);
        if (!browsing) session.selectionError = result.error;
        ctx.publish(session);
      }
      return null;
    }
  }

  async function searchPage(session, source) {
    if (!source || source.enabled === false || source.searchable === false) return false;
    const state = session.search.get(source.key);
    if (!state || state.completed) return false;
    if (state.work) return state.work;
    const epoch = session.searchGeneration;
    const admitted = () => !session.released && !ctx.isClosed() && epoch === session.searchGeneration &&
      session.search.get(source.key) === state && ctx.currentSource(session, source);
    const work = (async () => {
      state.loading = true; state.error = null; ctx.publish(session);
      try {
        const page = await data.search(source, session.keyword, state.page + 1, { canSchedule: admitted });
        if (!admitted()) return false;
        const alreadyConfirmed = page.page <= state.page;
        const hasNewRaw = page.items.some(raw => {
          const id = resultDetailId(raw);
          return isIdentifier(id) && !state.seen.has(id);
        });
        const added = await addResults(session, source, page.items, { kind: 'search', admitted });
        if (!admitted()) return false;
        if (!alreadyConfirmed) {
          state.page = page.page;
          state.completed ||= !page.hasMore || !page.explicitPageCount && (!hasNewRaw || !added);
          state.error = null;
        }
        return true;
      } catch (error) {
        if (admitted()) state.error = codeOf(error);
        return false;
      } finally {
        state.loading = false; delete state.work;
        if (epoch === session.searchGeneration) ctx.publish(session);
      }
    })();
    state.work = work;
    return work;
  }

  /**
   * Pre-sorts candidate site results before fetching detail (aligned with playback.go:smartBuildCandidates).
   */
  function sortResultsForSmartSearch(session, results, targetSeason, targetEpisode) {
    const queryKey = normalizeWorkKey(session.keyword);
    const multiSeason = ctx.browsing.isMultiSeasonSession(session);
    return [...results].sort((a, b) => {
      // 1. Cached ready detail first, error cooldown last
      const aReady = a.detail && a.state !== 'error' ? 1 : 0;
      const bReady = b.detail && b.state !== 'error' ? 1 : 0;
      if (aReady !== bReady) return bReady - aReady;
      const aFailed = a.state === 'error' ? 1 : 0;
      const bFailed = b.state === 'error' ? 1 : 0;
      if (aFailed !== bFailed) return aFailed - bFailed;

      // 2. Season hint alignment when multi-season
      if (session.kind === 'tv' && targetSeason > 0 && multiSeason) {
        const sRank = item => item.seasonHint === targetSeason ? 2 : item.seasonHint === 0 ? 1 : 0;
        if (sRank(a) !== sRank(b)) return sRank(b) - sRank(a);
      }

      // 3. Update progress badge >= requested episode
      if (session.kind === 'tv' && targetEpisode > 0) {
        const epOk = item => item.remarkMaxEpisode === 0 || item.remarkMaxEpisode >= targetEpisode ? 1 : 0;
        if (epOk(a) !== epOk(b)) return epOk(b) - epOk(a);
      }

      // 4. Exact canonical key match > matchScore > configured site order
      const aExact = normalizeWorkKey(a.rawTitle || a.title) === queryKey ? 1 : 0;
      const bExact = normalizeWorkKey(b.rawTitle || b.title) === queryKey ? 1 : 0;
      if (aExact !== bExact) return bExact - aExact;
      if ((a.matchScore ?? 0) !== (b.matchScore ?? 0)) return (b.matchScore ?? 0) - (a.matchScore ?? 0);
      return (a.source.order ?? 999999) - (b.source.order ?? 999999);
    });
  }

  return {
    addResults,
    ensureRecognized,
    registerVariants,
    smartResultEligible,
    candidateAllowed,
    loadDetail,
    searchPage,
    sortResultsForSmartSearch,
    isLineBlocked: (session, sourceKey, detailId, lineId) =>
      isLineBlocked(library.read().blocks, session.targetId, sourceKey, detailId, lineId),
  };
}
