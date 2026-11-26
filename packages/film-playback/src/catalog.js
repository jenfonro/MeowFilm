import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { checkSource, clone, isIdentifier, normalizeWorkKey, object, requireValue, resultDetailId, text, workTargetId } from './identity.js';

function cleanKeywordFallback(title, kind) {
  let base = text(title)
    .replace(/[（(]\s*(?:18|19|20|21)\d{2}\s*[)）]\s*$/, '')
    .trim();
  if (!base) return '';
  if (kind === 'movie') return base;
  base = base
    .replace(/第\s*(?:\d+|[０-９]+|[一二三四五六七八九十百千两零〇]+)\s*(?:季|部|篇)/gi, ' ')
    .replace(/(?:^|\s)(?:\d+|[０-９]+)\s*季(?![度节])/gi, ' ')
    .replace(/\b(?:s|season)\s*\d{1,3}\b/gi, ' ')
    .replace(/(?<!新)年\s*番\s*(?:\d+|[０-９]+|[一二三四五六七八九十百千两零〇]+)?/gi, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  const strippedDigits = base.replace(/^(.*(?:[^A-Za-z0-9\s]|\s))\s*0*[1-9]\d?\s*$/, '$1').trim();
  return strippedDigits || base || text(title);
}

function formatScore(value) {
  const num = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(num) || num <= 0) return '';
  return num.toFixed(1).replace(/\.0$/, '.0');
}

function pickDoubanPoster(record) {
  if (!object(record)) return '';
  const pic = record.pic;
  if (object(pic)) return text(pic.large) || text(pic.normal) || '';
  return text(record.cover_url) || text(record.cover?.url) || text(record.poster) || '';
}

function pickTMDBLogo(metadata) {
  const logos = metadata?.images?.logos;
  if (!Array.isArray(logos) || !logos.length) return '';
  const zh = logos.find(item => item?.iso_639_1 === 'zh' || item?.iso_639_1 === 'cn');
  const en = logos.find(item => item?.iso_639_1 === 'en');
  const chosen = zh ?? en ?? logos[0];
  return text(chosen?.file_path);
}

function formatMinutes(value) {
  if (typeof value === 'number' && Number.isFinite(value) && value > 0) return `${Math.round(value)}分钟`;
  if (typeof value === 'string' && value.trim()) {
    const raw = value.trim();
    if (/^\d+$/.test(raw)) return `${raw}分钟`;
    return raw;
  }
  return '';
}

function parseYear(record) {
  for (const raw of [record?.year, record?.release_date, record?.first_air_date,
    Array.isArray(record?.pubdate) ? record.pubdate[0] : undefined]) {
    const match = /^((?:18|19|20|21)\d{2})/.exec(String(raw ?? '').trim());
    if (match) return Number(match[1]);
  }
  return undefined;
}

/**
 * Normalizes cards, metadata, and work details into clean UI projections so
 * Windows & iOS never parse raw TMDB/Douban JSON or touch underlying script IDs.
 */
export function createFilmCatalog() {
  function buildCardFromMetadata(provider, record, defaultKind) {
    if (!object(record)) return null;
    const idValue = record.id ?? record.target_id;
    if (!/^[1-9]\d*$/.test(String(idValue ?? ''))) return null;
    const kind = ['movie', 'tv'].includes(record.media_type) ? record.media_type
      : ['movie', 'tv'].includes(record.type) ? record.type
      : ['movie', 'tv'].includes(defaultKind) ? defaultKind : 'tv';
    const title = text(record.title ?? record.name);
    if (!title) return null;
    const keyword = cleanKeywordFallback(title, kind) || title;
    const targetId = workTargetId(keyword);
    const year = parseYear(record);
    const poster = provider === 'tmdb' ? text(record.poster_path) : pickDoubanPoster(record);
    const backdrop = provider === 'tmdb' ? text(record.backdrop_path) : '';
    const score = provider === 'tmdb'
      ? formatScore(record.vote_average)
      : formatScore(record.rating?.value);
    const subtitle = provider === 'douban'
      ? (text(record.episodes_info) || text(record.card_subtitle))
      : (year ? String(year) : '');
    const textBadge = provider === 'douban'
      ? text(record.episodes_info)
      : (year ? String(year) : '');
    return {
      id: `${provider}:${kind}:${idValue}`,
      targetId,
      keyword,
      title,
      subtitle,
      poster,
      backdrop,
      score,
      textBadge,
      siteLabel: provider === 'tmdb' ? 'TMDB' : '豆瓣',
      kind,
      ...(year ? { year } : {}),
      tmdb: provider === 'tmdb' ? { kind, id: Number(idValue) } : null,
      douban: provider === 'douban' ? { kind, id: String(idValue) } : null,
      siteEntry: null,
    };
  }

  function searchCards(cards) {
    return (Array.isArray(cards) ? cards : []).map(card => {
      const title = text(card.title);
      const isTmdb = card.sourceKind === 'tmdb' && Number.isSafeInteger(card.tmdbId) && card.tmdbId > 0;
      const kind = isTmdb ? card.tmdbType : (card.raw?.type_name === '电影' ? 'movie' : undefined);
      const keyword = text(card.keyword) || cleanKeywordFallback(title, kind) || title;
      const targetId = workTargetId(keyword);
      const isRawSite = card.sourceKind === 'site' && typeof card.displayKey === 'string' && card.displayKey.startsWith('site:');
      const isSingleSite = card.sourceKind === 'site' && !card.aggregateItems?.length;
      const siteEntry = (isRawSite || isSingleSite) && text(card.siteKey) && isIdentifier(card.detailId)
        ? {
          sourceKey: card.siteKey,
          installationId: card.installationId,
          siteKey: card.siteId,
          detailId: card.detailId,
          rawTitle: text(card.rawTitle) || title,
          remark: text(card.textBadge),
          poster: text(card.poster),
        }
        : null;
      return {
        id: card.displayKey,
        displayKey: card.displayKey,
        targetId,
        keyword,
        sourceKind: card.sourceKind,
        title,
        subtitle: text(card.textBadge),
        poster: text(card.poster),
        backdrop: text(card.backdrop),
        score: '',
        textBadge: text(card.textBadge),
        siteLabel: text(card.siteLabel) || text(card.siteName),
        siteKey: text(card.siteKey),
        siteName: text(card.siteName),
        ...(Number.isSafeInteger(card.aggregateSourceCount) && card.aggregateSourceCount > 0
          ? { aggregateSourceCount: card.aggregateSourceCount } : {}),
        ...(Number.isSafeInteger(card.seasonHint) && card.seasonHint > 0
          ? { seasonHint: card.seasonHint, seasonHintLabel: text(card.seasonHintLabel) } : {}),
        ...(kind ? { kind } : {}),
        ...(Number.isSafeInteger(card.year) && card.year > 0 ? { year: card.year } : {}),
        tmdb: isTmdb ? { kind: card.tmdbType, id: card.tmdbId } : null,
        douban: null,
        siteEntry,
      };
    });
  }

  function metadata(provider, path, value) {
    if (!object(value) && !Array.isArray(value)) return value;
    const lane = /\/(?:search|discover|recent_hot|trending)\/(movie|tv)(?:\/|$)/.exec(path)?.[1]
      ?? (/movie/.test(path) ? 'movie' : /tv/.test(path) ? 'tv' : undefined);
    if (Array.isArray(value)) {
      return value.map(item => {
        const card = buildCardFromMetadata(provider, item, lane);
        return card ? { ...item, card } : item;
      });
    }
    const result = { ...value };
    if (Array.isArray(value.results)) {
      result.results = value.results.map(item => {
        const card = buildCardFromMetadata(provider, item, item?.media_type ?? lane);
        return card ? { ...item, card } : item;
      });
    }
    if (Array.isArray(value.items)) {
      result.items = value.items.map(item => {
        const inner = object(item?.subject) ? item.subject : item;
        const card = buildCardFromMetadata(provider, inner, inner?.type ?? lane);
        return card ? { ...item, card } : item;
      });
    }
    if (Array.isArray(value.subject_collection_items)) {
      result.subject_collection_items = value.subject_collection_items.map(item => {
        const card = buildCardFromMetadata(provider, item, item?.type ?? lane);
        return card ? { ...item, card } : item;
      });
    }
    return result;
  }

  function sourceRows(source, value) {
    if (!object(value)) return value;
    source = checkSource(source);
    const name = Array.isArray(value.list) ? 'list' : Array.isArray(value.items) ? 'items' : null;
    if (!name) return value;
    return {
      ...value,
      [name]: value[name].map(row => {
        const detailId = resultDetailId(row);
        const title = text(row?.vod_name ?? row?.title ?? row?.name);
        if (!object(row) || !isIdentifier(detailId) || !title) return row;
        const kind = row?.type_name === '电影' ? 'movie' : undefined;
        const keyword = cleanKeywordFallback(title, kind) || title;
        const targetId = workTargetId(keyword);
        const poster = text(row?.vod_pic ?? row?.pic);
        const remark = text(row?.vod_remarks ?? row?.remark);
        const card = {
          id: `site:${source.key}:${String(detailId)}`,
          targetId,
          keyword,
          title,
          subtitle: remark,
          poster,
          backdrop: '',
          score: '',
          textBadge: remark,
          siteLabel: text(source.name) || source.siteId,
          ...(kind ? { kind } : {}),
          tmdb: null,
          douban: null,
          siteEntry: {
            sourceKey: source.key,
            installationId: source.installationId,
            siteKey: source.siteId,
            detailId,
            rawTitle: title,
            remark,
            poster,
          },
        };
        return { ...row, card };
      }),
    };
  }

  function formatWorkInfo({ fallbackTitle, fallbackPoster = '', fallbackKind = 'tv', keyword, targetId, tmdbRow, doubanRow }) {
    const primary = tmdbRow ?? doubanRow;
    const raw = primary?.metadata ?? null;
    const doubanRaw = doubanRow?.metadata ?? null;
    const kind = primary?.kind ?? fallbackKind ?? 'tv';
    const title = text(raw?.title ?? raw?.name) || text(doubanRaw?.title ?? doubanRaw?.name) || text(fallbackTitle);
    const poster = (tmdbRow ? text(tmdbRow.metadata?.poster_path) : '') ||
      (doubanRow ? pickDoubanPoster(doubanRow.metadata) : '') ||
      text(fallbackPoster);
    const backdrop = (tmdbRow ? text(tmdbRow.metadata?.backdrop_path) : '') || poster;
    const logo = tmdbRow ? pickTMDBLogo(tmdbRow.metadata) : '';
    const yearNum = parseYear(raw) ?? parseYear(doubanRaw);
    const year = yearNum ? String(yearNum) : '';

    // Countries / Regions
    const countriesList = Array.isArray(raw?.origin_country) && raw.origin_country.length
      ? raw.origin_country
      : Array.isArray(raw?.production_countries) && raw.production_countries.length
        ? raw.production_countries.map(c => text(c?.name ?? c?.iso_3166_1)).filter(Boolean)
        : Array.isArray(raw?.countries) && raw.countries.length
          ? raw.countries.map(c => text(c)).filter(Boolean)
          : Array.isArray(doubanRaw?.countries)
            ? doubanRaw.countries.map(c => text(c)).filter(Boolean)
            : [];
    const countries = countriesList.join(' / ');

    // Duration
    const runtimeVal = raw?.runtime
      ?? (Array.isArray(raw?.episode_run_time) ? raw.episode_run_time[0] : undefined)
      ?? (Array.isArray(raw?.durations) ? raw.durations[0] : undefined)
      ?? (Array.isArray(doubanRaw?.durations) ? doubanRaw.durations[0] : undefined);
    const duration = formatMinutes(runtimeVal);

    // Season & Episode counts
    const intCount = val => {
      const n = typeof val === 'string' && /^\d+$/.test(val.trim()) ? Number(val.trim()) : val;
      return Number.isSafeInteger(n) && n > 0 ? n : null;
    };
    let seasonCount = null;
    let episodeCount = null;
    if (kind === 'tv') {
      if (tmdbRow?.metadata) {
        const ordinary = (tmdbRow.metadata.seasons ?? []).filter(s => intCount(s?.season_number) > 0 && intCount(s?.episode_count) > 0);
        seasonCount = ordinary.length || intCount(tmdbRow.metadata.number_of_seasons);
        episodeCount = intCount(tmdbRow.metadata.number_of_episodes) ??
          (ordinary.length ? ordinary.reduce((sum, s) => sum + intCount(s.episode_count), 0) : null);
      } else if (doubanRow) {
        const seasons = (doubanRow.seasons ?? doubanRow.metadata?.doubanSeasons ?? []).filter(s => intCount(s?.season) > 0);
        seasonCount = seasons.length || (intCount(doubanRow.metadata?.episodes_count) ? 1 : null);
        episodeCount = seasons.length
          ? seasons.reduce((sum, s) => sum + (intCount(s.episodeCount) ?? 0), 0) || intCount(doubanRow.metadata?.episodes_count)
          : intCount(doubanRow.metadata?.episodes_count);
      }
    }

    // Genres
    const rawGenres = Array.isArray(raw?.genres) ? raw.genres : (Array.isArray(doubanRaw?.genres) ? doubanRaw.genres : []);
    const genres = rawGenres.map(g => typeof g === 'string' ? g.trim() : text(g?.name)).filter(Boolean);

    // Score (prefer Douban score when available, otherwise TMDB vote_average)
    const doubanScore = formatScore(doubanRaw?.rating?.value);
    const tmdbScore = formatScore(tmdbRow?.metadata?.vote_average);
    const score = doubanScore || tmdbScore;
    const scoreProvider = doubanScore ? 'douban' : (tmdbScore ? 'tmdb' : '');

    // Directors
    const tmdbDirectors = (tmdbRow?.metadata?.credits?.crew ?? [])
      .filter(p => p?.job === 'Director' || p?.department === 'Directing' && p?.job === 'Series Director')
      .map(p => text(p?.name))
      .filter(Boolean);
    const tmdbCreators = (tmdbRow?.metadata?.created_by ?? []).map(p => text(p?.name)).filter(Boolean);
    const doubanDirectors = (doubanRaw?.directors ?? []).map(p => text(p?.name)).filter(Boolean);
    const directors = [...new Set(tmdbDirectors.length ? tmdbDirectors : tmdbCreators.length ? tmdbCreators : doubanDirectors)].slice(0, 4).join(' / ');

    // Cast
    const castSource = Array.isArray(tmdbRow?.metadata?.credits?.cast) && tmdbRow.metadata.credits.cast.length
      ? tmdbRow.metadata.credits.cast.slice(0, 20).map((person, idx) => ({
        id: `tmdb-cast:${person.id ?? idx}`,
        name: text(person.name),
        role: text(person.character),
        avatar: text(person.profile_path),
      }))
      : Array.isArray(doubanRaw?.actors)
        ? doubanRaw.actors.slice(0, 20).map((person, idx) => ({
          id: `douban-cast:${person.id ?? idx}`,
          name: text(person.name),
          role: text(person.character ?? (Array.isArray(person.roles) ? person.roles[0] : '')),
          avatar: text(person.avatar?.large ?? person.avatar?.normal ?? person.cover_url),
        }))
        : [];
    const cast = castSource.filter(item => item.name);

    // Summary (never site junk; only TMDB overview or Douban intro)
    const summary = text(tmdbRow?.metadata?.overview) || text(doubanRaw?.intro) || text(doubanRaw?.summary) || '';

    // Related / Similar cards
    const relatedRaw = tmdbRow?.metadata?.similar?.results ?? tmdbRow?.metadata?.recommendations?.results ?? [];
    const relatedCards = Array.isArray(relatedRaw)
      ? relatedRaw.slice(0, 18).map(item => buildCardFromMetadata('tmdb', item, item?.media_type ?? kind)).filter(Boolean)
      : [];

    const resolvedKeyword = text(keyword) || cleanKeywordFallback(title, kind) || title;
    const resolvedTargetId = text(targetId) || (resolvedKeyword ? `work:${normalizeWorkKey(resolvedKeyword)}` : '');

    return {
      targetId: resolvedTargetId,
      keyword: resolvedKeyword,
      kind,
      title,
      subtitle: [year, countries, duration].filter(Boolean).join(' · '),
      poster,
      backdrop,
      logo,
      year,
      countries,
      duration,
      seasonCount,
      episodeCount,
      genres,
      score,
      scoreProvider,
      directors,
      cast,
      summary,
      tmdb: tmdbRow ? { kind: tmdbRow.kind, id: Number(tmdbRow.metadataId) } : null,
      douban: doubanRow ? { kind: doubanRow.kind, id: String(doubanRow.metadataId) } : null,
      relatedCards,
    };
  }

  function history(value) {
    if (!object(value)) return { errorCode: 'CORRUPT_STORAGE' };
    if (value.errorCode) return cloneJSONValue(value);
    const cloned = cloneJSONValue(value);
    const episodeText = cloned.episode?.episode > 0
      ? (cloned.episode.season > 1 ? `第${cloned.episode.season}季 第${cloned.episode.episode}集` : `第${cloned.episode.episode}集`)
      : cloned.fileName || '';
    return {
      ...cloned,
      card: {
        id: cloned.targetId,
        targetId: cloned.targetId,
        keyword: cloned.keyword,
        title: cloned.title,
        subtitle: episodeText,
        poster: cloned.poster ?? '',
        backdrop: '',
        score: '',
        textBadge: episodeText,
        siteLabel: cloned.lineLabel || '',
        ...(cloned.kind ? { kind: cloned.kind } : {}),
        ...(cloned.year ? { year: cloned.year } : {}),
        tmdb: cloned.tmdb ?? null,
        douban: cloned.douban ?? null,
        siteEntry: null,
      },
    };
  }

  return {
    cleanKeywordFallback,
    buildCardFromMetadata,
    searchCards,
    metadata,
    sourceRows,
    formatWorkInfo,
    history,
    directory: (_source, value) => clone(value),
  };
}
