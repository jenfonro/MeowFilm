import { normalizeString } from './normalize';
import { requestJson } from './requestJson';

const postJson = async (url, payload) => requestJson(url, {
  method: 'POST',
  credentials: 'same-origin',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(payload && typeof payload === 'object' ? payload : {}),
});

export const saveManualRecognizeSelection = async (selected, context) => {
  const tmdbType = normalizeString(selected && selected.tmdbType).toLowerCase();
  const tmdbId = Number.isFinite(Number(selected && selected.tmdbId))
    ? Math.trunc(Number(selected.tmdbId))
    : 0;
  const title = normalizeString(selected && selected.title);
  const siteKey = normalizeString(context && context.siteKey);
  const spiderApi = normalizeString(context && context.spiderApi);
  const siteDetail = normalizeString(context && context.siteDetail);
  if ((tmdbType !== 'tv' && tmdbType !== 'movie') || tmdbId <= 0 || !title || !siteKey || !spiderApi || !siteDetail) {
    throw new Error('invalid params');
  }
  await postJson('/api/smart/manual/tmdb/add', { tmdbType, tmdbId, title });
  await postJson('/api/smart/manual/item/add', {
    tmdbType,
    tmdbId,
    siteKey,
    spiderApi,
    siteDetail,
    seasonHint: normalizeString(selected && selected.seasonHint),
    autoDisable: !!(selected && selected.autoDisable),
  });
};
