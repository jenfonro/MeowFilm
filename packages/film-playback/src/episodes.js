const integer = (value, minimum = 0) => {
  if (typeof value !== 'number' && !(typeof value === 'string' && /^\d+$/.test(value))) return undefined;
  const number = Number(value);
  return Number.isSafeInteger(number) && number >= minimum ? number : undefined;
};

// Provider fields remain primary: TMDB seasons/episodes use season_number,
// episode_number and episode_count. Douban season rows may use season and
// episodeCount (including the reference's doubanSeasons). A count-only season
// declares numeric episodes, but never supplies invented names/images/air dates.
export function buildEpisodeMap(metadata) {
  if (!metadata || typeof metadata !== 'object') return [];
  const rows = Array.isArray(metadata) ? metadata
    : Array.isArray(metadata.doubanSeasons) ? metadata.doubanSeasons
      : Array.isArray(metadata.seasons) ? metadata.seasons
        : metadata.season_number !== undefined || metadata.season !== undefined ? [metadata] : [];
  const seasons = new Map();
  for (const row of rows) {
    if (!row || typeof row !== 'object') continue;
    const season = integer(row.season_number ?? row.season);
    if (season === undefined) continue;
    const listed = Array.isArray(row.episodes);
    const episodes = listed ? row.episodes.flatMap(item => {
      if (!item || typeof item !== 'object') return [];
      const episode = integer(item.episode_number ?? item.episode ?? item.number);
      if (episode === undefined) return [];
      const explicitSeason = integer(item.season_number ?? item.season);
      if (explicitSeason !== undefined && explicitSeason !== season) return [];
      return [{ ...item, season, episode }];
    }) : [];
    let count = integer(row.episode_count ?? row.episodeCount ?? (listed ? undefined : row.episodes));
    if (count === undefined && listed && episodes.length === row.episodes.length) {
      const numbers = episodes.map(item => item.episode).sort((a, b) => a - b);
      if (numbers.every((episode, index) => episode === index + 1)) count = numbers.length;
    }
    if (!listed && count !== undefined) {
      for (let episode = 1; episode <= count; episode += 1) episodes.push({ season, episode });
    }
    const prior = seasons.get(season);
    if (prior) {
      // Conflicting duplicate season records cannot establish a global offset.
      prior.count = prior.count === count ? count : undefined;
      prior.episodes.push(...episodes);
    } else seasons.set(season, { count, episodes });
  }
  const result = [];
  const ordered = [...seasons.keys()].sort((a, b) => a - b);
  let previous = 0;
  let offset = 0;
  let reliableOffset = true;
  for (const season of ordered) {
    const row = seasons.get(season);
    if (season > 0 && season !== previous + 1) reliableOffset = false;
    for (const item of row.episodes.sort((a, b) => a.episode - b.episode)) {
      // Global numbering excludes specials. Missing earlier seasons/counts
      // leave local coordinates intact without claiming a global association.
      const globalEpisode = season > 0 && reliableOffset && item.episode > 0 &&
        (row.count === undefined || item.episode <= row.count) ? offset + item.episode : undefined;
      const entry = { ...item };
      delete entry.globalEpisode;
      if (globalEpisode !== undefined) entry.globalEpisode = globalEpisode;
      result.push(entry);
    }
    if (season > 0) {
      previous = season;
      if (row.count === undefined) reliableOffset = false;
      else offset += row.count;
    }
  }
  return result;
}

export function sameSeasonPartition(metadata, reference) {
  const rows = value => value?.doubanSeasons ?? value?.seasons ?? [];
  const numbers = value => [...new Set(rows(value).map(row => integer(row.season_number ?? row.season))
    .filter(number => number > 0))].sort((a, b) => a - b);
  const ownSeasons = numbers(metadata), referenceSeasons = numbers(reference);
  return ownSeasons.length > 0 && ownSeasons.length === referenceSeasons.length &&
    ownSeasons.every((season, index) => season === referenceSeasons[index]);
}

export function matchEpisodeMaps(metadata, reference) {
  const sameSeasons = sameSeasonPartition(metadata, reference);
  const index = new Map();
  for (const episode of buildEpisodeMap(reference)) {
    const key = sameSeasons ? `${episode.season}:${episode.episode}` : episode.globalEpisode;
    if (key !== undefined) index.set(key, index.has(key) ? null : episode);
  }
  return buildEpisodeMap(metadata).map(episode => ({ episode,
    match: index.get(sameSeasons ? `${episode.season}:${episode.episode}` : episode.globalEpisode) ?? null }));
}

export function resolveEpisode(map, { season, episode, globalEpisode } = {}) {
  if (!Array.isArray(map)) return null;
  const local = season !== undefined || episode !== undefined;
  if (local && (integer(season) === undefined || integer(episode) === undefined)) return null;
  if (globalEpisode !== undefined && integer(globalEpisode, 1) === undefined) return null;
  if (!local && globalEpisode === undefined) return null;
  let result = null;
  for (const item of map) {
    if (local && (item.season !== Number(season) || item.episode !== Number(episode))) continue;
    if (globalEpisode !== undefined && item.globalEpisode !== Number(globalEpisode)) continue;
    if (result !== null) return null;
    result = item;
  }
  return result === null ? null : { ...result };
}
