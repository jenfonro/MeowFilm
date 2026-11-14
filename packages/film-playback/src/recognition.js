import { createHash } from 'node:crypto';
import { decodePanResource } from '@meowfilm/script-host/cat-output';
import { buildEpisodeMap } from './episodes.js';
import { fail, isIdentifier, resultDetailId, requireValue } from './identity.js';
import { invokeRecognition, providerFamily } from './rules.js';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const identity = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');
const scalar = value => typeof value === 'string' || typeof value === 'number';
const coordinate = value => Number.isSafeInteger(value) && value >= 0 ? value : undefined;

function invalidDetail() {
  throw Object.assign(new Error('Invalid film playback detail'), { code: 'INVALID_DETAIL' });
}

function decodedShare(id, source) {
  if (typeof id !== 'string' || !id.startsWith('meow.pan.v1.')) return undefined;
  let share;
  try { share = decodePanResource(id); } catch { invalidDetail(); }
  if (!share || source?.installationId !== share.installationId || source?.siteId !== share.siteId) invalidDetail();
  return share;
}

function normalizeEpisode(item, index, { provider, source, resource, media } = {}) {
  if (typeof item === 'string') {
    const separator = item.indexOf('$');
    item = separator >= 0 ? { name: item.slice(0, separator), id: item.slice(separator + 1) } : { name: item, id: item };
  }
  if (!object(item)) invalidDetail();
  const playId = item.playId ?? item.url ?? (item.file || item.share ? undefined : item.id);
  if (playId !== undefined && !scalar(playId)) invalidDetail();
  if (item.id !== undefined && !scalar(item.id)) invalidDetail();
  if (item.file !== undefined && !object(item.file) || item.share !== undefined && !object(item.share)) invalidDetail();
  if (resource && (resource.id !== playId || resource.installationId !== source?.installationId ||
    resource.siteId !== source?.siteId)) invalidDetail();
  const share = item.share ?? decodedShare(playId, source);
  if (share && resource && share.provider !== resource.provider) invalidDetail();
  const actualProvider = item.file?.provider ?? share?.provider ?? resource?.provider ?? item.provider ?? provider;
  const rowId = item.id ?? playId ?? fileIdentity(item.file, share);
  const result = { ...media, ...item, id: rowId, name: item.name ?? item.file?.name ?? '',
    index: item.index ?? index };
  if (media?.filename !== undefined && item.fileName === undefined) result.fileName = media.filename;
  if (playId !== undefined) result.playId = playId;
  if (item.path !== undefined || item.file?.path !== undefined) result.path = item.path ?? item.file.path;
  if (share !== undefined) result.share = share;
  if (actualProvider !== undefined) result.provider = actualProvider;
  return result;
}

export function normalizeDetail(raw, { source } = {}) {
  if (!object(raw)) invalidDetail();
  let detail = raw;
  let vodIndex = 0;
  if (Array.isArray(raw.list)) {
    const requested = source?.detailId;
    vodIndex = requested === undefined ? (raw.list.length === 1 ? 0 : -1)
      : raw.list.findIndex(item => resultDetailId(item) === requested);
    if (vodIndex < 0 || !object(raw.list[vodIndex])) invalidDetail();
    detail = raw.list[vodIndex];
  }
  const detailId = resultDetailId(detail) ?? source?.detailId;
  if (detailId !== undefined && !isIdentifier(detailId)) invalidDetail();
  const resources = new Map();
  if (raw._meow?.version === 1 && Array.isArray(raw._meow.resources)) {
    for (const resource of raw._meow.resources) {
      if (resource?.kind === 'pan') resources.set(`${resource.vodIndex}:${resource.lineIndex}:${resource.episodeIndex}`, resource);
    }
  }
  const mediaRows = new Map();
  const vodMedia = new Map();
  if (raw._meow?.version === 1 && Array.isArray(raw._meow.media)) {
    for (const media of raw._meow.media) {
      if (!object(media)) continue;
      if (media.lineIndex === undefined && media.episodeIndex === undefined) vodMedia.set(media.vodIndex, media);
      else mediaRows.set(`${media.vodIndex}:${media.lineIndex}:${media.episodeIndex}`, media);
    }
  }
  let lines;
  if (Array.isArray(detail.lines)) {
    lines = detail.lines.map(line => {
      if (!object(line) || !Array.isArray(line.episodes)) invalidDetail();
      const id = line.id ?? line.flag ?? line.label;
      if (!scalar(id)) invalidDetail();
      return { ...line, id, label: line.label ?? line.flag ?? String(id), flag: line.flag ?? id,
        episodes: line.episodes.map((item, index) => normalizeEpisode(item, index, { provider: line.provider, source })) };
    });
  } else if (Array.isArray(detail.files)) {
    const episodes = detail.files.map((item, index) => normalizeEpisode(item, index, { provider: detail.provider, source }));
    const firstProvider = episodes[0]?.provider ?? detail.provider;
    const provider = episodes.every(item => item.provider === firstProvider) ? firstProvider : undefined;
    const id = detail.lineId ?? provider ?? detail.provider;
    if (!scalar(id)) invalidDetail();
    lines = [{ id, label: detail.label ?? String(id), ...(provider === undefined ? {} : { provider }), episodes }];
  } else if (typeof detail.vod_play_from === 'string' && typeof detail.vod_play_url === 'string') {
    const flags = detail.vod_play_from.split('$$$');
    const urls = detail.vod_play_url.split('$$$');
    if (flags.length !== urls.length) invalidDetail();
    const counts = urls.map(url => url === '' ? 0 : url.split('#').length);
    const singleResource = counts.reduce((total, count) => total + count, 0) === 1;
    const occurrences = new Map();
    lines = flags.map((flag, lineIndex) => {
      const occurrence = occurrences.get(flag) ?? 0;
      occurrences.set(flag, occurrence + 1);
      const id = occurrence === 0 ? flag : `line:${identity([flag, occurrence])}`;
      const episodes = urls[lineIndex] === '' ? [] : urls[lineIndex].split('#').map((item, index) => {
        const resource = resources.get(`${vodIndex}:${lineIndex}:${index}`);
        const media = mediaRows.get(`${vodIndex}:${lineIndex}:${index}`) ??
          (singleResource ? vodMedia.get(vodIndex) : undefined);
        return normalizeEpisode(item, index, { source, resource, media });
      });
      const firstProvider = episodes[0]?.provider;
      const provider = providerFamily(firstProvider) && episodes.every(item => item.provider === firstProvider)
        ? firstProvider : undefined;
      return { id, flag, label: flag, ...(provider === undefined ? {} : { provider }), episodes };
    });
  } else invalidDetail();
  return { lines, raw, source, ...(detailId === undefined ? {} : { detailId }),
    ...(detail.kind === undefined ? {} : { kind: detail.kind }),
    ...(detail.season === undefined ? {} : { season: detail.season }) };
}

export function stableFileIdentity(file) {
  // Tokens/passwords/revisions are retained in file, but do not turn the same
  // underlying resource into a fresh candidate whenever credentials refresh.
  const fields = ['provider', 'kind', 'shareId', 'fileId', 'coID', 'contentId', 'uk',
    ...(file.kind === 'stored' ? ['accountRevision'] : [])];
  const exact = Object.fromEntries(fields.filter(field => file[field] !== undefined).map(field => [field, file[field]]));
  if (!['fileId', 'coID', 'contentId'].some(field => file[field] !== undefined)) invalidDetail();
  return identity(['file', exact]);
}

export function mediaVariants(value) {
  if (value?._meow?.version !== 1 || !Array.isArray(value._meow.media)) return [];
  const variants = new Map();
  for (const media of value._meow.media) {
    if (!Array.isArray(media?.variants)) continue;
    for (const row of media.variants) {
      requireValue(object(row) && typeof row.url === 'string' && /^https?:\/\//i.test(row.url) &&
        !/[\u0000-\u0020\u007f]/.test(row.url), 'UPSTREAM_FORMAT');
      let url;
      try { url = new URL(row.url).href; } catch { throw fail('UPSTREAM_FORMAT'); }
      const id = identity(['hls-variant', url]);
      const variant = { id, url };
      for (const field of ['quality', 'resolution', 'codec']) {
        if (typeof row[field] === 'string' && row[field].trim()) variant[field] = row[field];
      }
      if (Number.isFinite(row.fps) && row.fps > 0 && row.fps < 1000) variant.fps = row.fps;
      const previous = variants.get(id);
      requireValue(!previous || identity(previous) === identity(variant), 'UPSTREAM_FORMAT');
      variants.set(id, variant);
    }
  }
  return [...variants.values()];
}

export async function recognizeVariantCandidates(candidate, variants, { rules, signal } = {}) {
  const texts = await invokeRecognition(rules, { operation: 'recognition.candidates', target: { kind: 'source' },
    source: {}, detail: { lines: [{ episodes: variants.map(variant =>
      ({ name: '', quality: variant.quality, fps: variant.fps })) }] } }, { signal });
  return variants.map((variant, index) => {
    const result = { ...candidate, id: `${candidate.id}:${variant.id}`, parentId: candidate.id, variant };
    // A master/file label is not evidence about each of its variants.
    for (const field of ['quality', 'resolution', 'fps', 'codec']) delete result[field];
    if (texts[index].quality) result.quality = texts[index].quality;
    for (const field of ['resolution', 'fps', 'codec']) {
      if (variant[field] !== undefined) result[field] = variant[field];
    }
    return result;
  });
}

function fileIdentity(file, share, sourceIdentity, detailId, lineId, playId) {
  if (file) return stableFileIdentity(file);
  if (share) {
    const reference = share.shareId ?? share.id ?? share.url;
    if (!scalar(reference)) invalidDetail();
    return identity(['share', share.provider, reference]);
  }
  if (playId === undefined) invalidDetail();
  return identity(['script', sourceIdentity, detailId, lineId, playId]);
}

function mapIndex(map, key) {
  const index = new Map();
  for (const item of map) {
    const value = key(item);
    if (value === undefined) continue;
    index.set(value, index.has(value) ? null : item);
  }
  return index;
}

/** Smart eligibility only. Ordinary menus keep the full raw search page. */
export function matchesResults(rows, { target, rules, signal } = {}) {
  return invokeRecognition(rules, { operation: 'recognition.matches', rows, target }, { signal });
}

export async function matchesResult(raw, options) {
  return (await matchesResults([raw], options))[0];
}

export async function recognizeCandidates(detail, { target, source = detail?.source, metadata, rules, signal } = {}) {
  if (!target || !['movie', 'tv', 'source', 'group'].includes(target.kind) || !source?.key) invalidDetail();
  // normalizeDetail also accepts the expanded lines produced by data.expand.
  // Their opaque row ID must never become the script's playId.
  const normalized = normalizeDetail(detail, { source });
  const detailId = normalized.detailId ?? source.detailId;
  if (detailId === undefined) invalidDetail();
  const sourceIdentity = [source.key, source.engine, source.installationId, source.siteId];
  const candidateSource = { ...source, detailId };
  if (target.kind === 'source' && target.source) {
    for (const field of ['key', 'engine', 'installationId', 'siteId', 'revision', 'generation']) {
      if (target.source[field] !== undefined && target.source[field] !== source[field]) return [];
    }
    if (target.source.detailId !== undefined && target.source.detailId !== detailId) return [];
  }
  const texts = await invokeRecognition(rules, { operation: 'recognition.candidates', target: { kind: target.kind },
    source: { manualSeasonHint: source.manualSeasonHint },
    detail: { season: normalized.season, lines: normalized.lines.map(line =>
      ({ season: line.season, episodes: line.episodes.map(item => ({
        name: item.name, fileName: item.fileName, path: item.path, season: item.season, episode: item.episode,
        quality: item.quality, hdr: item.hdr, fps: item.fps ?? item.file?.fps,
        ...(item.file ? { file: { name: item.file.name, path: item.file.path } } : {}),
      })) })) } }, { signal });
  let textIndex = 0;
  const map = buildEpisodeMap(target.kind === 'group' ? null : metadata);
  const local = mapIndex(map, item => `${item.season}:${item.episode}`);
  const global = mapIndex(map, item => item.globalEpisode);
  const regularSeasons = new Set(map.filter(item => item.season > 0).map(item => item.season));
  const singleSeason = regularSeasons.size === 1 && regularSeasons.has(1) && !(metadata?.number_of_seasons > 1);
  const candidates = [];
  for (const line of normalized.lines) {
    for (const item of line.episodes) {
      const parsed = texts[textIndex++];
      if (!parsed.eligible) continue;
      const { fileName, path, season, episode, movie, quality } = parsed;
      const explicitGlobal = coordinate(item.globalEpisode);
      let mapped = explicitGlobal === undefined ? undefined : global.get(explicitGlobal);
      if (mapped && (season !== undefined && season !== mapped.season || episode !== undefined && episode !== mapped.episode)) {
        mapped = undefined;
      } else if (!mapped && season !== undefined && episode !== undefined) {
        const localMatch = local.get(`${season}:${episode}`);
        if (explicitGlobal === undefined || localMatch?.globalEpisode === explicitGlobal) mapped = localMatch;
      } else if (!mapped && singleSeason && season === undefined && episode !== undefined) {
        const localMatch = local.get(`1:${episode}`);
        if (explicitGlobal === undefined || localMatch?.globalEpisode === explicitGlobal) mapped = localMatch;
      }
      const resourceIdentity = fileIdentity(item.file, item.share, sourceIdentity, detailId, line.id, item.playId);
      const provider = item.file?.provider ?? item.provider ?? line.provider;
      const candidate = { id: identity([sourceIdentity, detailId, line.id, resourceIdentity]),
        fileIdentity: resourceIdentity, targetId: target.id, source: candidateSource, detailId, lineId: line.id,
        lineLabel: line.label, flag: line.flag ?? line.id, name: item.name, fileName,
        episodeId: item.id, index: item.index, kind: target.kind,
        ...(path === undefined ? {} : { path }), ...(provider === undefined ? {} : { provider }),
        ...Object.fromEntries(['resolution', 'fps', 'codec'].flatMap(field => {
          const value = item[field] ?? item.file?.[field];
          return value === undefined ? [] : [[field, value]];
        })),
        ...(quality ? { quality } : {}), ...(item.file === undefined ? {} : { file: item.file }),
        ...(item.share === undefined ? {} : { share: item.share }),
        ...(item.playId === undefined ? {} : { playId: item.playId }) };
      if (!movie) {
        if (mapped) {
          candidate.season = mapped.season;
          candidate.episode = mapped.episode;
          if (mapped.globalEpisode !== undefined) candidate.globalEpisode = mapped.globalEpisode;
        } else {
          if (season !== undefined) candidate.season = season;
          if (episode !== undefined) candidate.episode = episode;
          if (explicitGlobal !== undefined && explicitGlobal > 0) candidate.globalEpisode = explicitGlobal;
        }
      }
      candidates.push(candidate);
    }
  }
  return candidates;
}
