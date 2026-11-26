import { isItemBlocked } from './policy.js';
import { providerFamily, qualityKey } from './rules.js';
import { clone, text } from './identity.js';
import { formatQualityBadge, panDisplayLabel } from './session-helpers.js';

const QUALITY_ORDER = ['8k', '4k_hdr', '4k_fps', '4k', '1080p', '720p'];
const PAN_ORDER = ['yidong', 'tianyi', 'quark', 'uc', 'baidu'];

function normalizeQualityKey(raw) {
  if (typeof raw === 'string' && raw.trim().toLowerCase() === '8k') return '8k';
  return qualityKey(raw);
}

function formatEpisodeLabel(intent, candidate) {
  const season = candidate?.season ?? intent?.season;
  const episode = candidate?.episode ?? intent?.episode;
  if (episode > 0) {
    return season > 1 ? `第${season}季 第${String(episode).padStart(2, '0')}集` : `第${String(episode).padStart(2, '0')}集`;
  }
  return text(candidate?.fileName ?? candidate?.name);
}

function formatSourceLabel(candidate) {
  if (!candidate) return '';
  const siteName = text(candidate.source?.name) || text(candidate.source?.key);
  const lineLabel = text(candidate.lineLabel);
  if (siteName && lineLabel && lineLabel !== siteName) return `${siteName} · ${lineLabel}`;
  return siteName || lineLabel;
}

export function createSessionSnapshot(ctx) {
  const { library } = ctx;

  function buildSourceMenu(session) {
    // Requirement 1.1: When no video sources & no manual shares exist, source dropdown
    // must display "暂无可用视频源" and be disabled — metadata items must NOT masquerade as video sources!
    if (session.noSources) {
      return {
        selectedId: null,
        placeholder: '暂无可用视频源',
        disabled: true,
        canLoadMore: false,
        loadingMore: false,
        options: [],
      };
    }

    const options = [];
    for (const row of session.metadataSources) {
      options.push({
        id: row.id,
        kind: row.provider,
        label: row.provider === 'tmdb' ? 'TMDB' : '豆瓣',
        subtitle: row.title,
        state: 'ready',
        error: null,
      });
    }

    if (ctx.browsing.hasSmartList(session)) {
      options.push({
        id: 'smart',
        kind: 'smart',
        label: '智能列表',
        subtitle: '',
        state: session.browsing.sourceId === 'smart' && session.browsing.loading ? 'loading' : 'ready',
        error: null,
      });
    }

    const blocks = library.read().blocks;
    for (const result of session.results) {
      if (!ctx.currentSource(session, result.source)) continue;
      if (isItemBlocked(blocks, session.targetId, result.source.key, result.source.detailId)) continue;
      const isManual = result.source.engine === 'pan' || Boolean(result.manualId);
      const siteName = text(result.source.name) || result.source.key;
      const itemTitle = text(result.rawTitle || result.title);
      const label = itemTitle && itemTitle !== session.keyword
        ? `${siteName} · ${itemTitle}`
        : siteName;
      options.push({
        id: result.id,
        kind: isManual ? 'manual' : 'site',
        label,
        subtitle: text(result.remark),
        state: result.state,
        error: result.error ?? null,
      });
    }

    const loadingMore = [...session.search.values()].some(st => st.loading);
    const canLoadMore = ctx.browsing.canLoadMoreSources(session);
    const selectedId = options.some(opt => opt.id === session.browsing.sourceId)
      ? session.browsing.sourceId
      : (options[0]?.id ?? null);

    return {
      selectedId,
      placeholder: options.length === 0 && loadingMore ? '正在搜索站点源…' : '选择站点源',
      disabled: options.length === 0 && !canLoadMore && !loadingMore,
      canLoadMore,
      loadingMore,
      options,
    };
  }

  function buildLineMenu(session) {
    const selectedId = session.browsing.sourceId;
    if (!selectedId || selectedId === 'smart' || selectedId.startsWith('meta:')) {
      return { visible: false, selectedId: null, options: [] };
    }
    const result = session.results.find(r => r.id === selectedId);
    const lines = result?.detail?.lines ?? [];
    if (!lines.length) {
      return { visible: false, selectedId: null, options: [] };
    }
    const activeLine = lines.find(l => String(l.id) === String(session.browsing.lineId)) ?? lines[0];
    return {
      visible: true,
      selectedId: String(activeLine.id),
      options: lines.map(line => ({
        id: String(line.id),
        label: text(line.label) || String(line.id),
        provider: providerFamily(line.provider) ?? null,
      })),
    };
  }

  function buildPlayerState(session, work) {
    const activeAttempt = session.current && !session.current.resourceReleased ? session.current
      : (session.pending && !session.pending.resourceReleased ? session.pending : null);
    const activeCand = activeAttempt?.candidate ?? null;
    const activeQuality = normalizeQualityKey(activeCand?.quality);
    const activePan = providerFamily(activeCand?.panProvider ?? activeCand?.provider) ?? '';

    const qualitySet = new Set();
    const panSet = new Set();
    for (const cand of session.candidates.values()) {
      if (cand.share && !cand.file) continue;
      if (session.kind === 'tv' && session.playIntent.episode > 0) {
        if (cand.episode !== session.playIntent.episode) continue;
        if (session.playIntent.season > 0 && cand.season !== session.playIntent.season) continue;
      }
      const qk = normalizeQualityKey(cand.quality);
      if (qk) qualitySet.add(qk);
      const pf = providerFamily(cand.panProvider ?? cand.provider);
      if (pf) panSet.add(pf);
    }

    const availableQualities = QUALITY_ORDER
      .filter(key => qualitySet.has(key))
      .map(key => ({
        key,
        label: formatQualityBadge(key),
        active: session.constraint.action === 'quality'
          ? normalizeQualityKey(session.constraint.quality) === key
          : activeQuality === key,
      }));

    const availablePans = PAN_ORDER
      .filter(provider => panSet.has(provider))
      .map(provider => ({
        provider,
        label: panDisplayLabel(provider),
        active: session.constraint.action === 'pan'
          ? providerFamily(session.constraint.provider) === provider
          : activePan === provider,
      }));

    const canPrevious = !session.noSources && Boolean(ctx.selection.adjacent(session, -1));
    const canNext = !session.noSources && Boolean(ctx.selection.adjacent(session, 1));
    const hasActiveMedia = Boolean(activeAttempt);

    return {
      shortVideo: session.shortVideo,
      canPrevious,
      canNext,
      canSwitchSource: hasActiveMedia && !session.noSources,
      canBlockSource: hasActiveMedia && !session.noSources,
      activeSourceLabel: formatSourceLabel(activeCand),
      activeEpisodeLabel: formatEpisodeLabel(session.playIntent, activeCand),
      activeQuality,
      activePan,
      constraint: clone(session.constraint),
      availableQualities,
      availablePans,
      preferences: clone(ctx.preferences()),
      attempt: session.pending && !session.pending.resourceReleased ? {
        id: session.pending.id,
        candidateId: session.pending.candidate.id,
        prepareOnly: session.pending.prepareOnly,
        position: session.pending.position,
        paused: session.pending.paused,
        title: work.title,
        season: session.pending.intent?.season ?? session.pending.candidate.season ?? null,
        episode: session.pending.intent?.episode ?? session.pending.candidate.episode ?? null,
        globalEpisode: session.pending.intent?.globalEpisode ?? session.pending.candidate.globalEpisode ?? null,
        episodeLabel: formatEpisodeLabel(session.pending.intent, session.pending.candidate),
        sourceLabel: formatSourceLabel(session.pending.candidate),
        quality: formatQualityBadge(session.pending.candidate.quality),
        provider: providerFamily(session.pending.candidate.panProvider ?? session.pending.candidate.provider) ?? null,
      } : null,
      current: session.current && !session.current.resourceReleased ? {
        attemptId: session.current.id,
        candidateId: session.current.candidate.id,
        position: session.position,
        duration: session.duration,
        paused: session.paused,
        title: work.title,
        season: session.current.intent?.season ?? session.current.candidate.season ?? null,
        episode: session.current.intent?.episode ?? session.current.candidate.episode ?? null,
        globalEpisode: session.current.intent?.globalEpisode ?? session.current.candidate.globalEpisode ?? null,
        episodeLabel: formatEpisodeLabel(session.current.intent, session.current.candidate),
        sourceLabel: formatSourceLabel(session.current.candidate),
        quality: formatQualityBadge(session.current.candidate.quality),
        provider: providerFamily(session.current.candidate.panProvider ?? session.current.candidate.provider) ?? null,
      } : null,
      prefetch: [...session.prefetch.values()].map(val => ({
        label: formatEpisodeLabel(val.intent, val.candidate),
        state: val.state,
        bytes: val.bytes ?? 0,
        error: val.error ?? null,
      })),
    };
  }

  function build(session) {
    const retry = session.pending ?? session.current ?? session.lastFailed;
    const retryMediaAttemptId = !session.stopped && !session.noSources && retry &&
      ctx.currentSource(session, retry.candidate.source) &&
      (retry !== session.lastFailed || ['error', 'unavailable'].includes(session.stage))
      ? retry.id
      : null;

    const work = ctx.metadata.buildWorkInfo(session);
    const sourceMenu = buildSourceMenu(session);
    const lineMenu = buildLineMenu(session);
    const directory = ctx.browsing.buildDirectorySnapshot(session);
    const player = buildPlayerState(session, work);

    return {
      sessionId: session.id,
      sequence: session.sequence,
      rulesRevision: session.rules.revision,
      targetId: session.targetId,
      keyword: session.keyword,
      stage: session.stage,
      error: session.error,
      errorStage: session.error && session.error === session.feedbackFailure?.code ? session.feedbackFailure.stage : null,
      reportError: session.reportError,
      noSources: session.noSources,
      retryMediaAttemptId,
      work,
      sourceMenu,
      lineMenu,
      directory,
      player,
    };
  }

  return { build };
}
