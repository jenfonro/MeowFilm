import { codeOf, fail, fields, identity, requireValue } from './identity.js';
import { replaceLine } from './session-helpers.js';

export function createSessionBrowsing(ctx) {
  const { data, catalog } = ctx;

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
      return {
        id: candidate?.id ?? identity(result.id, line.id, value.id),
        ...fields(value, ['name', 'path', 'index']),
        ...(candidate ? {
          candidateId: candidate.id,
          ...fields(candidate, ['season', 'episode', 'globalEpisode', 'quality', 'resolution', 'fps', 'codec']),
          ...(parent ? catalog.candidate(parent, candidate) : {}),
        } : {}),
      };
    });
    result.directories.set(line.id, { line, recognized, episodes });
    return episodes;
  }

  async function directoryProjection(session, result, line, generation) {
    if (!line) return [];
    const selected = session.metadataSources.find(row => row.id === session.metadataSourceId);
    const metadata = selected ? session.metadata : null;
    const parent = { id: session.selectionId, title: session.entryTarget.title };
    const target = selected ? {
      ...parent, kind: selected.kind,
      metadataProvider: selected.provider, metadataId: selected.metadataId,
    } : session.entryTarget;
    const previous = result.directories?.get(line.id);
    if (previous?.line === line && previous.metadata === metadata && previous.variants === result.variants)
      return previous.episodes;
    const candidates = await ctx.candidates.recognizeResult(session, result, result.detail, target, metadata, 'session');
    if (ctx.isClosed() || session.released || generation !== session.browseGeneration ||
        metadata !== (selected ? session.metadata : null) || !result.detail.lines.includes(line) ||
        !ctx.currentSource(session, result.source)) return null;
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
    if (ctx.isClosed() || session.released || session.browsing !== browsing || generation !== session.browseGeneration ||
        metadata !== session.metadata || episodes === null || !ctx.currentSource(session, result.source)) return;
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
        await ctx.metadata.browseMetadata(session, id, false, { generation, season, refresh });
        return;
      }
      const saved = session.metadataViews.get(session.metadataSourceId)?.browsing;
      session.browsing = saved ? { ...saved, loading: false, refreshing: false, error: null }
        : { sourceId: null, lineId: null, loading: false, error: null, lines: [], episodes: [] };
      ctx.publish(session);
      return;
    }
    const result = session.results.find(value => value.id === resultId);
    requireValue(result, 'NOT_FOUND');
    const admitted = () => !session.released && !ctx.isClosed() && generation === session.browseGeneration &&
      ctx.currentSource(session, result.source);
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
    const display = knownLine ? {
      sourceId: resultId, lineId: knownLine.id, loading: false, error: null,
      lines: result.detail.lines.map(value => fields(value, ['id', 'label', 'provider'])),
      episodes: retained ? result.directories?.get(knownLine.id)?.episodes ?? [] : [],
      ...((previous.season ?? saved?.season) === undefined ? {} : { season: previous.season ?? saved.season }),
    } : saved;
    session.browsing = retained ? { ...(display ?? previous), loading: true, refreshing: true, error: null }
      : { sourceId: resultId, lineId: lineId ?? null, loading: true, error: null, lines: [], episodes: [] };
    if (season !== undefined) session.browsing.season = season;
    ctx.publish(session);
    try {
      if (metadataSourceId === null) {
        session.metadataSourceId = null; session.metadata = null;
      } else if (metadataSourceId !== undefined && metadataSourceId !== previousMetadataSourceId ||
          season !== undefined && season !== previous.season) {
        const id = metadataSourceId ?? session.metadataSourceId;
        requireValue(id, 'NOT_FOUND');
        const loaded = await ctx.metadata.browseMetadata(session, id, true, { generation, season, complete: false });
        if (!admitted()) return;
        requireValue(loaded, session.browsing.error ?? session.metadataError ?? 'UPSTREAM_ERROR');
      }
      let detail;
      if (result.source.engine === 'pan') {
        const row = ctx.currentManual(session, result);
        requireValue(row?.share, 'NOT_FOUND');
        if (!refresh && result.detail) detail = result.detail;
        else {
          const line = {
            id: row.id, label: row.name || row.provider, provider: row.provider,
            episodes: [{ id: row.id, name: row.name || session.target.title,
              share: { ...row.share, provider: row.provider } }],
          };
          detail = { lines: [line], detailId: row.id };
          result.baseDetail = detail; result.detail = detail;
        }
      } else {
        detail = !refresh && result.detail && result.detailEntry ? result.detail
          : await ctx.candidates.loadDetail(session, result, generation, true, refresh);
      }
      if (!admitted()) return;
      if (!detail) throw fail(result.error ?? 'UPSTREAM_ERROR');
      let line = lineId === undefined ? detail.lines[0] : detail.lines.find(value => value.id === lineId);
      if (lineId !== undefined) requireValue(line, 'NOT_FOUND');
      session.browsing = {
        ...session.browsing, lineId: line?.id ?? null,
        lines: detail.lines.map(value => fields(value, ['id', 'label', 'provider'])),
      };
      ctx.publish(session);
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
      session.browsing = {
        sourceId: resultId, lineId: line?.id ?? null, loading: false, error: null,
        ...(session.browsing.season === undefined ? {} : { season: session.browsing.season }),
        lines: detail.lines.map(value => fields(value, ['id', 'label', 'provider'])),
        episodes,
      };
      rememberBrowsing(session);
    } catch (error) {
      if (!admitted()) return;
      if (retained && result.detail === priorDetail) {
        session.browsing = { ...(display ?? previous), loading: false, refreshing: false, error: codeOf(error) };
      } else {
        session.browsing = {
          ...session.browsing, loading: false, refreshing: false,
          error: codeOf(error),
          lines: (result.detail?.lines ?? []).map(value => fields(value, ['id', 'label', 'provider'])),
          episodes: [],
        };
      }
    }
    ctx.publish(session);
  }

  async function restoreReads(session) {
    const selected = session.restoreBrowsing;
    const active = () => !session.released && !session.stopped && !ctx.isClosed();
    try {
      if (session.metadataDiscovery) await session.metadataDiscovery;
      if (!active() || session.generation !== 0) return;
      if (selected?.season !== undefined)
        session.browsing = { ...session.browsing, season: selected.season };
      if (selected?.metadataSourceId && session.metadataSources.some(row => row.id === selected.metadataSourceId)) {
        const generation = session.browseGeneration;
        await ctx.metadata.browseMetadata(session, selected.metadataSourceId, true);
        if (!active() || session.browseGeneration !== generation + 1) return;
      } else if (selected?.season !== undefined && session.target.metadataProvider) {
        await ctx.metadata.refreshMetadataDisplay(session, { season: selected.season });
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
      if (active()) ctx.publish(session);
    }
  }

  async function refreshBrowse(session, refresh, context = {}) {
    const generation = session.browseGeneration;
    await session.initialization;
    if (session.released || session.stopped || ctx.isClosed() || generation !== session.browseGeneration ||
        !ctx.groupScopeAvailable(session)) return;
    if (session.initializationError || session.entryTarget.metadataProvider === 'douban' &&
        !session.target.metadataProvider && session.target.kind === 'group' && !session.target.members.length) {
      session.initializationError = null; session.error = null; session.stage = 'loading';
      if (session.entryResolution === 'error' || session.entryResolution === 'unmatched')
        session.entryResolution = 'loading';
      session.browsing = { ...session.browsing, loading: true, error: null };
      session.initialization = ctx.tracked(ctx.lifecycle.initialize(session, [], { refresh }));
      ctx.publish(session);
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
      return ctx.metadata.refreshMetadataDisplay(session, { refresh });
    const candidate = session.pending?.candidate ?? session.current?.candidate;
    const source = candidate?.source ?? session.target.source;
    const result = session.results.find(row => row.source.key === source?.key &&
      row.source.detailId === source?.detailId);
    requireValue(result, 'NOT_FOUND');
    return browse(session, result.id, candidate?.lineId, { refresh, retainDisplay: true });
  }

  return {
    rememberBrowsing, refreshBrowsing,
    browse, restoreReads, refreshBrowse,
  };
}
