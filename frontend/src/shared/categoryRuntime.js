import { normalizeInt, normalizeString } from './normalize';

const categoryCacheStore = Object.create(null);

export const buildCategoryCacheKey = ({
  source,
  type,
  selectedFilters,
  siteClassOptions,
} = {}) => {
  const resolvedSource = source && typeof source === 'object' ? source : {};
  const filters = selectedFilters && typeof selectedFilters === 'object' ? selectedFilters : {};
  const classOptions = Array.isArray(siteClassOptions) ? siteClassOptions : [];
  if (resolvedSource.kind === 'site') {
    const categoryId =
      normalizeString(filters.category) ||
      normalizeString(resolvedSource.categoryId) ||
      (classOptions[0] && normalizeString(classOptions[0].value)) ||
      '';
    return `site::${normalizeString(resolvedSource.siteKey)}::${categoryId}`;
  }
  return `douban::${normalizeString(type)}::${JSON.stringify(filters)}`;
};

export const getCategoryCacheEntry = (key) => {
  const cacheKey = normalizeString(key);
  if (!cacheKey) return null;
  const entry = categoryCacheStore[cacheKey];
  return entry && typeof entry === 'object' ? entry : null;
};

export const setCategoryCacheEntry = (key, patch) => {
  const cacheKey = normalizeString(key);
  if (!cacheKey) return null;
  const current = getCategoryCacheEntry(cacheKey) || {};
  const next = {
    ...current,
    ...(patch && typeof patch === 'object' ? patch : {}),
  };
  categoryCacheStore[cacheKey] = next;
  return next;
};

export const buildCategorySourceSeedEntry = ({ source } = {}) => {
  const resolvedSource = source && typeof source === 'object' ? source : {};
  const seedItems = Array.isArray(resolvedSource.initialItems) ? resolvedSource.initialItems.slice() : [];
  if (!seedItems.length) return null;
  if (resolvedSource.kind === 'site') {
    return {
      items: seedItems,
      page: Math.max(1, normalizeInt(resolvedSource.initialPage) || 1),
      hasMore: !!resolvedSource.initialHasMore,
      seeded: false,
    };
  }
  return {
    items: seedItems,
    page: 1,
    hasMore: true,
    seeded: true,
    seedCount: Math.max(0, normalizeInt(resolvedSource.initialSeedCount) || seedItems.length),
  };
};

export const primeCategoryCacheEntry = ({
  key,
  source,
} = {}) => {
  const cacheKey = normalizeString(key);
  if (!cacheKey || getCategoryCacheEntry(cacheKey)) return getCategoryCacheEntry(cacheKey);
  const seed = buildCategorySourceSeedEntry({ source });
  if (!seed) return null;
  return setCategoryCacheEntry(cacheKey, seed);
};
