import { isDeepStrictEqual } from 'node:util';
import { isCandidateAllowed } from './policy.js';
import { contentResults, matchesResult, mediaVariants, normalizeDetail, recognizeCandidates, recognizeVariantCandidates } from './recognition.js';
import { providerFamily, qualityKey } from './rules.js';
import { clone, codeOf, fields, identity, isIdentifier, object, requireValue,
  resultDetailId, text, variantParentId } from './identity.js';
import { manualIdentity, sameVariantFile } from './session-helpers.js';

export function createSessionCandidates(ctx) {
  const { data, library, debug } = ctx;

  function contentTarget(session) {
    const target = session.searchMetadataTarget ?? session.entryTarget;
    const metadata = session.entryMetadata ?? ctx.metadata.playbackMetadata(session);
    const value = { ...metadata, ...session.entryTarget, ...target };
    if (session.searchMetadataTarget)
      value.aliases = [metadata?.aliases, session.entryTarget.aliases, target.aliases]
        .filter(Array.isArray).flat().concat(session.entryTarget.title);
    return value;
  }

  async function addResults(session, source, items, kind = 'result', admitted = () => true, members = []) {
    const known = new Set([
      ...(session.entryTarget.kind === 'group' ? session.entryTarget.members : []),
      ...(session.target.kind === 'group' ? session.target.members : []),
      ...members,
    ].filter(member => member.sourceKey === source.key).map(member => member.detailId));
    for (const target of [session.entryTarget, session.target])
      if ((target.kind === 'source' || target.kind === 'group') && target.source?.key === source.key)
        known.add(target.source.detailId);
    const unchecked = items.filter(raw => kind !== 'linked' && session.selectionKind !== 'source' &&
      !known.has(resultDetailId(raw)));
    const matching = unchecked.length ? await ctx.calculate(session, signal =>
      contentResults(unchecked, { target: contentTarget(session), rules: session.rules, signal }), 'session') : [];
    const accepted = new Set(unchecked.filter((_, index) => matching[index]));
    if (ctx.isClosed() || session.released || session.stopped || !admitted()) return false;
    for (const raw of items) {
      const detailId = resultDetailId(raw);
      if (!isIdentifier(detailId)) continue;
      if (kind === 'result') session.search.get(source.key)?.seen.add(detailId);
      if (session.selectionKind === 'source' ? source.key !== session.selectionSource.key ||
          detailId !== session.selectionSource.detailId : kind !== 'linked' && !known.has(detailId) && !accepted.has(raw)) continue;
      const id = identity(source.key, detailId);
      const contentMember = kind === 'result' || known.has(detailId);
      if (session.target.kind === 'group' && kind === 'result' && accepted.has(raw))
        ctx.addMember(session, source.key, detailId);
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
      session.results.push({
        id, source: { ...source, detailId }, title: text(raw.vod_name ?? raw.name ?? raw.title),
        raw: clone(raw), contentMember,
        ...(kind === 'hint' ? { hint: true } : kind === 'linked' ? { linked: true } : {}),
        state: 'idle', error: null,
      });
      if (session.metadataSourceId === null && session.browsing.sourceId === null &&
          !session.target.metadataProvider && !session.userSelected)
        session.browsing = { ...session.browsing, sourceId: id };
    }
    return true;
  }

  async function recognizeResult(session, result, detail, target = session.target,
    metadata = ctx.metadata.playbackMetadata(session), lifetime = 'playback') {
    if (result.hint && !result.verifiedRaw) return [];
    const source = { ...result.source };
    const manual = result.manualId ? ctx.currentManual(session, result) : null;
    if (result.manualId) {
      delete source.manualSeasonHint;
      if (manual?.seasonHint !== undefined) source.manualSeasonHint = manual.seasonHint;
    }
    const lines = detail.lines;
    const candidates = (await ctx.calculate(session, signal =>
      recognizeCandidates(detail, { target, source, metadata, rules: session.rules, signal,
        completeDirectory: true }), lifetime))
      .map(candidate => manual ? { ...candidate, manualId: manual.id, manualIdentity: manualIdentity(manual) } : candidate);
    const expanded = [];
    for (const candidate of candidates) {
      const variants = result.variants?.get(candidate.id);
      if (variants) expanded.push(...await ctx.calculate(session, signal =>
        recognizeVariantCandidates(candidate, variants, { rules: session.rules, signal }), lifetime));
      else expanded.push(candidate);
    }
    return detail.lines === lines ? expanded : [];
  }

  async function candidatesFrom(session, result, detail) {
    if (ctx.isClosed() || session.released || result.detail !== detail || !session.results.includes(result) ||
        !ctx.currentSource(session, result.source)) return;
    const target = session.target, metadata = ctx.metadata.playbackMetadata(session), generation = session.generation;
    const manual = result.manualId ? ctx.currentManual(session, result) : null;
    const manualKey = manual ? manualIdentity(manual) : null;
    const context = {
      detail, lines: detail.lines, metadata: target.kind === 'group' ? null : metadata,
      targetId: target.id, kind: target.kind, variants: result.variants,
      manualKey, seasonHint: result.source.manualSeasonHint, hint: result.hint, verifiedRaw: result.verifiedRaw,
    };
    const previous = result.recognized;
    if (previous && Object.keys(context).every(key => previous[key] === context[key])) {
      if (previous.installed !== session.candidateEpoch) {
        for (const candidate of previous.candidates) session.candidates.set(candidate.id, candidate);
        previous.installed = session.candidateEpoch;
      }
      return;
    }
    const candidates = await recognizeResult(session, result, detail);
    if (ctx.isClosed() || session.released) return;
    const current = result.manualId ? ctx.currentManual(session, result) : null;
    if (target !== session.target ||
        metadata !== ctx.metadata.playbackMetadata(session) || generation !== session.generation || result.detail !== detail ||
        detail.lines !== context.lines ||
        !session.results.includes(result) || result.variants !== context.variants ||
        result.hint !== context.hint || result.verifiedRaw !== context.verifiedRaw ||
        result.source.manualSeasonHint !== context.seasonHint ||
        (current ? manualIdentity(current) : null) !== manualKey || !ctx.currentSource(session, result.source)) return;
    for (const [id, candidate] of session.candidates) if (candidate.source.key === result.source.key &&
      candidate.source.detailId === result.source.detailId) session.candidates.delete(id);
    for (const candidate of candidates) session.candidates.set(candidate.id, candidate);
    result.recognized = { ...context, candidates, installed: session.candidateEpoch };
    debug?.emit({
      level: 'debug', cat: 'film-playback', sub: 'film.playback', event: 'playback.candidates.recognized',
      data: { sessionId: session.id, sourceKey: result.source.key, detailId: result.source.detailId, count: candidates.length },
    });
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

  async function smartResult(session, result, target = session.target) {
    if (result.hint && !result.verifiedRaw) return false;
    if (!result.hint) {
      if (result.linkedTargetId === target.id && (!result.manualId || !!ctx.currentManual(session, result))) return true;
    }
    const content = session.searchMetadataTarget ?? session.entryTarget;
    const raw = result.hint ? result.verifiedRaw : result.raw;
    const contentIdentity = !result.hint && result.contentMember ? content : null;
    const previous = result.match;
    const metadata = ctx.metadata.playbackMetadata(session);
    if (previous && previous.raw === raw && previous.target === target && previous.metadata === metadata &&
        previous.contentKind === contentIdentity?.kind && previous.contentProvider === contentIdentity?.metadataProvider &&
        previous.contentId === contentIdentity?.metadataId) return previous.value;
    const entry = {
      raw, target, metadata, contentKind: contentIdentity?.kind,
      contentProvider: contentIdentity?.metadataProvider, contentId: contentIdentity?.metadataId,
    };
    result.match = entry;
    entry.value = ctx.calculate(session, signal => matchesResult(raw, {
      target: { ...metadata, ...target },
      rules: session.rules,
      ...(contentIdentity ? { contentIdentity: fields(contentIdentity, ['kind', 'metadataProvider', 'metadataId']) } : {}),
      signal,
    }));
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
    if (target.metadataProvider && ctx.metadata.metadataCacheState(session, session.playbackMetadataSourceId)?.status === 'stale') return false;
    if (target.metadataProvider && candidate.eligible === false) return false;
    if (candidate.share && !candidate.file) return false;
    if (!explicit && !(target.metadataProvider ? ctx.smartSourceAllowed(session, candidate.source)
      : ctx.sourceAllowed(session, candidate.source))) return false;
    const result = session.results.find(value => value.source.key === candidate.source.key &&
      value.source.detailId === candidate.source.detailId);
    if (result && target.metadataProvider && (result.hint || !explicit) &&
        !await smartResult(session, result, target)) return false;
    if (candidate.manualId && !ctx.currentManual(session, candidate)) return false;
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
    const blocks = library.read().blocks.filter(row => row.targetId === target.id &&
      row.sourceKey === candidate.source?.key && !row.errorCode &&
      row.detailId === (candidate.detailId ?? candidate.source?.detailId) && row.lineId === candidate.lineId);
    if (!isCandidateAllowed(candidate, {
      target: policyTarget, constraint,
      excluded: retry ? new Set() : session.excluded, blocks,
      actualEpisode,
    })) return false;
    if (target.kind === 'tv' && !explicit) {
      const projection = ctx.metadata.episodeProjection(session, ctx.metadata.playbackMetadata(session));
      const key = `${candidate.season}:${candidate.episode}`;
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
    const admitted = () => !ctx.isClosed() && !session.released &&
      ctx.currentSource(session, result.source) &&
      (browsing ? session.browseGeneration === generation : ctx.valid(session, generation));
    result.state = 'loading'; result.error = null; ctx.publish(session);
    try {
      // Re-enter the data owner: a long-lived page must not become an immortal second TTL cache.
      const read = () => data.detailEntry(result.source, result.source.detailId, { canSchedule: admitted, refresh });
      const value = await (browsing ? read() : ctx.runIO(session, generation, result.source, null, read));
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
      result.state = 'ready'; ctx.publish(session);
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
    const state = session.search.get(source.key);
    if (!state || state.completed || !ctx.groupScopeAvailable(session)) return false;
    if (state.work) return state.work;
    const epoch = session.searchGeneration;
    const admitted = () => !session.released && !ctx.isClosed() && epoch === session.searchGeneration &&
      session.search.get(source.key) === state && ctx.currentSource(session, source) && ctx.groupScopeAvailable(session);
    const work = (async () => {
      state.loading = true; state.error = null; ctx.publish(session);
      try {
        const page = await data.search(source, session.query, state.page + 1, { canSchedule: admitted });
        if (!admitted()) return false;
        const alreadyConfirmed = page.page <= state.page;
        const hasNewRaw = page.items.some(raw => {
          const id = resultDetailId(raw);
          return isIdentifier(id) && !state.seen.has(id);
        });
        const added = await addResults(session, source, page.items, 'result', admitted);
        if (!added || !admitted()) return false;
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
        if (epoch === session.searchGeneration) ctx.publish(session);
      }
    })();
    state.work = work;
    return work;
  }

  return {
    addResults, recognizeResult, candidatesFrom, registerVariants,
    smartResult, allowed, loadDetail, searchPage,
  };
}
