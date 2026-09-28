import { normalizeString } from './normalize';

const buildMatchBlockedEntryKey = (siteKey, siteDetail) => {
  const safeSiteKey = normalizeString(siteKey);
  const safeSiteDetail = normalizeString(siteDetail);
  if (!safeSiteKey || !safeSiteDetail) return '';
  return `${safeSiteKey}::${safeSiteDetail}`;
};

export const isMatchBlockedBySiteDetail = (index, siteKey, siteDetail) => {
  const entryKey = buildMatchBlockedEntryKey(siteKey, siteDetail);
  if (!entryKey || !index || typeof index !== 'object') return false;
  const entry = index[entryKey];
  return !!(entry && entry.blockAll);
};

export const applyMatchBlockedIndexMutation = (index, payload, blocked) => {
  const current = index && typeof index === 'object' ? { ...index } : {};
  const entryKey = buildMatchBlockedEntryKey(payload && payload.siteKey, payload && payload.siteDetail);
  if (!entryKey) return current;
  if (blocked) {
    current[entryKey] = {
      ...(current[entryKey] && typeof current[entryKey] === 'object' ? current[entryKey] : {}),
      blockAll: true,
      siteKey: normalizeString(payload && payload.siteKey),
      siteDetail: normalizeString(payload && payload.siteDetail),
      spiderApi: normalizeString(payload && payload.spiderApi),
      poster: normalizeString(payload && payload.poster),
    };
    return current;
  }
  delete current[entryKey];
  return current;
};
