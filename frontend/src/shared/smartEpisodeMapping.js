import { normalizeInt } from './normalize';

const normalizeSeasonRows = (rows, { seasonKeys = [], episodeKeys = [] } = {}) => (
  (Array.isArray(rows) ? rows : [])
    .map((item) => {
      const season = seasonKeys.reduce((out, key) => (out > 0 ? out : normalizeInt(item && item[key])), 0);
      const episodeCount = episodeKeys.reduce((out, key) => (out > 0 ? out : normalizeInt(item && item[key])), 0);
      return { season, episodeCount };
    })
    .filter((item) => item.season > 0 && item.episodeCount > 0)
    .sort((left, right) => left.season - right.season)
);

const buildTMDBRenderedSeasonRows = (detail) => {
  const seasons = Array.isArray(detail && detail.seasons) ? detail.seasons : [];
  return normalizeSeasonRows(seasons, {
    seasonKeys: ['season', 'season_number'],
    episodeKeys: ['episodes', 'episodeCount', 'episode_count'],
  });
};

const buildDoubanSeasonRows = (meta) => {
  const seasons = Array.isArray(meta && meta.doubanSeasons) ? meta.doubanSeasons : [];
  return normalizeSeasonRows(seasons, {
    seasonKeys: ['season', 'season_number'],
    episodeKeys: ['episodeCount', 'episode_count', 'episodes'],
  });
};

export const clipSeasonRowsToTotalEpisodes = (rows, totalEpisodes) => {
  const list = Array.isArray(rows) ? rows : [];
  const limit = Math.max(0, normalizeInt(totalEpisodes));
  if (!list.length || limit <= 0) return [];
  const out = [];
  let left = limit;
  for (let i = 0; i < list.length; i += 1) {
    const row = list[i];
    const season = normalizeInt(row && row.season);
    const episodeCount = normalizeInt(row && row.episodeCount);
    if (season <= 0 || episodeCount <= 0 || left <= 0) continue;
    const clippedCount = Math.min(episodeCount, left);
    if (clippedCount <= 0) break;
    out.push({ season, episodeCount: clippedCount });
    left -= clippedCount;
    if (left <= 0) break;
  }
  return out;
};

export const tmdbSeasonEpisodeOfGlobal = (detail, globalNo) => {
  const g = normalizeInt(globalNo);
  if (g <= 0) return { season: 0, episode: 0 };
  const rows = buildTMDBRenderedSeasonRows(detail);
  if (!rows.length) return { season: 0, episode: g };
  let left = g;
  for (let i = 0; i < rows.length; i += 1) {
    const row = rows[i];
    if (!row || row.episodeCount <= 0) continue;
    if (left > row.episodeCount) {
      left -= row.episodeCount;
      continue;
    }
    return { season: row.season, episode: left };
  }
  return { season: 0, episode: g };
};

export const tmdbGlobalEpisodeNoOf = (detail, seasonNo, episodeNo) => {
  const season = normalizeInt(seasonNo);
  const episode = normalizeInt(episodeNo);
  if (season <= 0 || episode <= 0) return 0;
  const rows = buildTMDBRenderedSeasonRows(detail);
  if (!rows.length) return 0;
  let sum = 0;
  for (let i = 0; i < rows.length; i += 1) {
    const row = rows[i];
    if (!row) continue;
    if (row.season < season) {
      sum += row.episodeCount;
      continue;
    }
    if (row.season === season) return sum + episode;
  }
  return 0;
};

export const doubanGlobalEpisodeNoOf = (meta, seasonNo, episodeNo) => {
  const season = normalizeInt(seasonNo);
  const episode = normalizeInt(episodeNo);
  if (season <= 0 || episode <= 0) return 0;
  const rows = buildDoubanSeasonRows(meta);
  if (!rows.length) return 0;
  let sum = 0;
  for (let i = 0; i < rows.length; i += 1) {
    const row = rows[i];
    if (!row) continue;
    if (row.season < season) {
      sum += row.episodeCount;
      continue;
    }
    if (row.season === season) return sum + episode;
  }
  return 0;
};

export const doubanSeasonEpisodeOfGlobal = (meta, globalNo) => {
  const g = normalizeInt(globalNo);
  if (g <= 0) return { season: 0, episode: 0 };
  const rows = buildDoubanSeasonRows(meta);
  if (!rows.length) return { season: 0, episode: g };
  let left = g;
  for (let i = 0; i < rows.length; i += 1) {
    const row = rows[i];
    if (!row || row.episodeCount <= 0) continue;
    if (left > row.episodeCount) {
      left -= row.episodeCount;
      continue;
    }
    return { season: row.season, episode: left };
  }
  return { season: 0, episode: g };
};

export const buildSmartEpisodeMapping = ({ tmdbDetail, doubanMeta } = {}) => {
  const tmdbRows = buildTMDBRenderedSeasonRows(tmdbDetail);
  const doubanRows = buildDoubanSeasonRows(doubanMeta);
  const totalEpisodes = tmdbRows.reduce((sum, item) => sum + Math.max(0, normalizeInt(item && item.episodeCount)), 0);
  const clippedDoubanRows = clipSeasonRowsToTotalEpisodes(doubanRows, totalEpisodes);
  if (!tmdbRows.length || totalEpisodes <= 0) {
    return {
      totalEpisodes: 0,
      tmdbSeasons: tmdbRows,
      doubanSeasons: clippedDoubanRows,
      items: [],
    };
  }
  const items = [];
  for (let global = 1; global <= totalEpisodes; global += 1) {
    items.push({
      global,
      tmdb: tmdbSeasonEpisodeOfGlobal(tmdbDetail, global),
      douban: doubanSeasonEpisodeOfGlobal(doubanMeta, global),
    });
  }
  return {
    totalEpisodes,
    tmdbTotalEpisodeCount: totalEpisodes,
    doubanTotalEpisodeCount: clippedDoubanRows.reduce((sum, item) => sum + Math.max(0, normalizeInt(item && item.episodeCount)), 0),
    doubanLeads: !!(doubanMeta && doubanMeta.doubanLeads),
    tmdbSeasons: tmdbRows,
    doubanSeasons: clippedDoubanRows,
    items,
  };
};
