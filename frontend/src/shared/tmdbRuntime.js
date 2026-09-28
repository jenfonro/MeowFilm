import { normalizeString } from './normalize';
import { requestJson } from './requestJson';
import { normalizeTMDBID } from './tmdbRaw';

const buildTMDBDetailURL = ({ type, id } = {}) => {
  const tmdbType = normalizeString(type).toLowerCase();
  const tmdbId = normalizeTMDBID(id);
  if (tmdbId <= 0 || (tmdbType !== 'movie' && tmdbType !== 'tv')) return '';
  const searchParams = new URLSearchParams({ type: tmdbType, id: String(tmdbId) });
  return `/api/tmdb/detail?${searchParams.toString()}`;
};

const tmdbDetailCache = new Map();

export const fetchTMDBDetailCached = async ({ type, id } = {}) => {
  const tmdbType = normalizeString(type).toLowerCase();
  const tmdbId = normalizeTMDBID(id);
  if (tmdbId <= 0 || (tmdbType !== 'movie' && tmdbType !== 'tv')) {
    throw new Error('TMDB 参数无效');
  }

  const cacheKey = `${tmdbType}:${String(tmdbId)}`;
  const cached = tmdbDetailCache.get(cacheKey);
  if (cached && cached.status === 'resolved') return cached.data;
  if (cached && cached.status === 'pending') return cached.promise;

  const promise = requestJson(buildTMDBDetailURL({ type: tmdbType, id: tmdbId }), {
    method: 'GET',
    credentials: 'same-origin',
  })
    .then((data) => {
      tmdbDetailCache.set(cacheKey, { status: 'resolved', data });
      return data;
    })
    .catch((error) => {
      tmdbDetailCache.delete(cacheKey);
      throw error;
    });

  tmdbDetailCache.set(cacheKey, { status: 'pending', promise });
  return promise;
};

export const clearTMDBDetailCache = () => {
  tmdbDetailCache.clear();
};
