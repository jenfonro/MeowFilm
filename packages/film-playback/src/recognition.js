import { createHash } from 'node:crypto';
import { decodePanResource } from '@meowfilm/script-host/cat-output';
import { normalizeNFKC } from '@meowfilm/script-host/unicode';
import { buildEpisodeMap } from './episodes.js';
import { isIdentifier, isSearchProjection, resultDetailId } from './identity.js';
import { createRecognitionRules, providerFamily, recognizeQuality } from './rules.js';

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

const titleText = value => typeof value === 'string' ? normalizeNFKC(value).trim() : '';
// Unicode 15.0 Cf ranges: NodeMobile's V8 without ICU cannot parse property escapes.
const titleKey = value => titleText(value).toLowerCase().replace(/[\u00ad\u0600-\u0605\u061c\u06dd\u070f\u0890-\u0891\u08e2\u180e\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u206f\ufeff\ufff9-\ufffb\u{110bd}\u{110cd}\u{13430}-\u{1343f}\u{1bca0}-\u{1bca3}\u{1d173}-\u{1d17a}\u{e0001}\u{e0020}-\u{e007f}\s.\-_,，:：;；!！?？·•/\\|]+/gu, '');
const yearValue = value => /^(?:18|19|20|21)\d{2}$/.test(String(value)) ? Number(value) : undefined;

function resultYear(record) {
  for (const value of [record.year, record.vod_year, record.release_date?.slice?.(0, 4),
    record.first_air_date?.slice?.(0, 4), ...(Array.isArray(record.pubdate) ? record.pubdate.map(value => value?.slice?.(0, 4)) : [])]) {
    const year = yearValue(value);
    if (year !== undefined) return year;
  }
  const name = titleText(record.rawTitle ?? record.vod_name ?? record.title ?? record.name);
  return yearValue(/[（(]\s*((?:18|19|20|21)\d{2})\s*[)）]\s*$/.exec(name)?.[1]);
}

function resultTitles(record) {
  // rawTitle is the site title before an aggregate card replaced its display
  // title with metadata. A group/display key alone is not identity evidence.
  const first = record.rawTitle ?? record.vod_name ?? record.title ?? record.name;
  return [first, record.originalTitle, record.original_title, record.original_name,
    ...(Array.isArray(record.aka) ? record.aka : []),
    ...(Array.isArray(record.localized_name) ? record.localized_name : [record.localized_name]),
    ...(Array.isArray(record.aliases) ? record.aliases : [])].map(titleText).filter(Boolean);
}

function resultKind(record) {
  const value = record.kind ?? record.contentKind ?? record.media_type ?? record.tmdbType ?? record.target_type;
  if (value === 'movie' || value === 'tv') return value;
  return ({ 电影: 'movie', 电视剧: 'tv', 剧集: 'tv' })[record.type_name];
}

function resultSeason(record, recognition) {
  const explicit = coordinate(record.season) ?? coordinate(record.season_number) ?? coordinate(record.seasonHint);
  if (explicit !== undefined) return explicit;
  return recognition.season(titleText(record.rawTitle ?? record.vod_name ?? record.title ?? record.name)) ??
    recognition.season(titleText(record.vod_remarks ?? record.remark));
}

function canonicalTitle(value, kind, recognition, query) {
  let title = titleText(value).replace(/[（(]\s*(?:18|19|20|21)\d{2}\s*[)）]\s*$/, '').trim();
  if (kind === 'movie') {
    // Feed observed digits to the shared query-tail protection, even when
    // source annotations follow a sequel number. Do not strip a movie's number.
    return titleKey(recognition.search(title, title.match(/\d+/g)?.at(-1) ?? title).text);
  }
  title = title.replace(/第\s*(?:\d+|[一二三四五六七八九十百千两零〇]+)\s*季/gi, ' ')
    .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ');
  query = titleText(query).replace(/第\s*(?:\d+|[一二三四五六七八九十百千两零〇]+)\s*季/gi, ' ')
    .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ').trim();
  return titleKey(recognition.search(title, query).text);
}

/** Smart eligibility only. Ordinary menus keep the full raw search page. */
export function matchesResult(raw, { target, rules } = {}) {
  if (!object(raw) || !target || !['movie', 'tv', 'source', 'group'].includes(target.kind)) return false;
  // Source scope is checked by the caller's exact installation/detail identity,
  // not by pretending an arbitrary script title has a metadata counterpart.
  if (target.kind === 'source') return true;
  if (isSearchProjection(raw)) raw = { ...raw, ...raw.raw,
    rawTitle: raw.raw.rawTitle ?? raw.raw.vod_name ?? raw.raw.title ?? raw.raw.name ?? raw.rawTitle };
  const kind = resultKind(raw);
  if (target.kind !== 'group' && kind !== undefined && kind !== target.kind) return false;
  const recognition = createRecognitionRules(rules);
  const rawSeason = resultSeason(raw, recognition), targetSeason = resultSeason(target, recognition);
  if (rawSeason !== undefined && targetSeason !== undefined && rawSeason !== targetSeason) return false;
  const rawYear = resultYear(raw), targetYear = resultYear(target);
  const laterSeason = target.kind === 'tv' && rawSeason > 1 && rawSeason === targetSeason;
  if (rawYear !== undefined && targetYear !== undefined && rawYear !== targetYear && !laterSeason) return false;
  const provider = raw.metadataProvider ?? (raw.tmdbId > 0 ? 'tmdb' : undefined);
  const metadataId = raw.metadataId ?? (raw.tmdbId > 0 ? raw.tmdbId
    : raw.metadataProvider !== undefined ? raw.id : undefined);
  if (provider === target.metadataProvider && metadataId !== undefined && target.metadataId !== undefined) {
    return String(metadataId) === String(target.metadataId);
  }
  const targets = resultTitles(target);
  const titles = resultTitles(raw);
  return targets.some(title => {
    const expected = canonicalTitle(title, target.kind, recognition, title);
    return expected && titles.some(value => canonicalTitle(value, target.kind, recognition, title) === expected);
  });
}

export function recognizeCandidates(detail, { target, source = detail?.source, metadata, rules } = {}) {
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
  const recognition = createRecognitionRules(rules);
  const map = buildEpisodeMap(target.kind === 'group' ? null : metadata);
  const local = mapIndex(map, item => `${item.season}:${item.episode}`);
  const global = mapIndex(map, item => item.globalEpisode);
  const regularSeasons = new Set(map.filter(item => item.season > 0).map(item => item.season));
  const singleSeason = regularSeasons.size === 1 && regularSeasons.has(1) && !(metadata?.number_of_seasons > 1);
  const candidates = [];
  for (const line of normalized.lines) {
    for (const item of line.episodes) {
      const path = item.path ?? item.file?.path;
      const parts = typeof path === 'string' ? path.split(/[/\\]/).filter(Boolean) : [];
      const fileName = item.file?.name ?? item.fileName ?? (parts.length ? parts[parts.length - 1] : item.name);
      const parsedFile = recognition.episode(fileName);
      const parsed = parsedFile.episode === undefined ? recognition.episode(item.name) : parsedFile;
      const season = coordinate(item.season) ?? parsed.season ??
        (parts.length > 1 ? recognition.season(parts.slice(0, -1).reverse().join('/')) : undefined) ??
        coordinate(line.season) ?? coordinate(normalized.season) ?? coordinate(source.manualSeasonHint);
      const episode = coordinate(item.episode) ?? parsed.episode;
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
      const movie = target.kind === 'movie' && (recognition.movie(fileName) || episode === undefined);
      if (target.kind === 'movie' && !movie) continue;
      const quality = recognizeQuality(item, [fileName, item.name, path]);
      const resourceIdentity = fileIdentity(item.file, item.share, sourceIdentity, detailId, line.id, item.playId);
      const provider = item.file?.provider ?? item.provider ?? line.provider;
      const candidate = { id: identity([sourceIdentity, detailId, line.id, resourceIdentity]),
        fileIdentity: resourceIdentity, targetId: target.id, source: candidateSource, detailId, lineId: line.id,
        lineLabel: line.label, flag: line.flag ?? line.id, name: item.name, fileName,
        episodeId: item.id, index: item.index, kind: target.kind,
        ...(path === undefined ? {} : { path }), ...(provider === undefined ? {} : { provider }),
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
