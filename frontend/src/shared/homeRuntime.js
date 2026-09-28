import { normalizeString } from './normalize';

const homeCacheStore = Object.create(null);

const cloneSectionItems = (items) => (
  Array.isArray(items)
    ? items.map((item) => (item && typeof item === 'object' ? { ...item } : item))
    : []
);

const cloneSections = (sections) => (
  Array.isArray(sections)
    ? sections.map((section) => (
      section && typeof section === 'object'
        ? { ...section, items: cloneSectionItems(section.items) }
        : section
    ))
    : []
);

export const buildHomeCacheKey = ({ source } = {}) => {
  const resolved = source && typeof source === 'object' ? source : {};
  if (normalizeString(resolved.kind) === 'site') {
    const siteKey = normalizeString(resolved.siteKey);
    return siteKey ? `site::home::${siteKey}` : '';
  }
  return 'douban::home';
};

export const getHomeCacheEntry = (key) => {
  const cacheKey = normalizeString(key);
  if (!cacheKey) return null;
  const entry = homeCacheStore[cacheKey];
  if (!entry || typeof entry !== 'object') return null;
  return {
    ...entry,
    sections: cloneSections(entry.sections),
  };
};

export const setHomeCacheEntry = (key, patch) => {
  const cacheKey = normalizeString(key);
  if (!cacheKey) return null;
  const current = homeCacheStore[cacheKey] && typeof homeCacheStore[cacheKey] === 'object'
    ? homeCacheStore[cacheKey]
    : {};
  const nextPatch = patch && typeof patch === 'object' ? patch : {};
  const next = {
    ...current,
    ...nextPatch,
    sections: cloneSections(nextPatch.sections != null ? nextPatch.sections : current.sections),
  };
  homeCacheStore[cacheKey] = next;
  return getHomeCacheEntry(cacheKey);
};

export const ensureHomeCacheEntry = (key) => {
  const cacheKey = normalizeString(key);
  if (!cacheKey) return null;
  const current = getHomeCacheEntry(cacheKey);
  if (current) return current;
  return setHomeCacheEntry(cacheKey, {
    status: 'idle',
    sourceKey: cacheKey,
    sections: [],
    errorText: '',
    promise: null,
    updatedAt: 0,
  });
};

export const clearHomeCacheEntry = (key) => {
  const cacheKey = normalizeString(key);
  if (!cacheKey) return;
  delete homeCacheStore[cacheKey];
};

export const resolveCachedHomeSections = async ({ key, loader } = {}) => {
  const cacheKey = normalizeString(key);
  const runLoader = typeof loader === 'function' ? loader : null;
  if (!cacheKey || !runLoader) return null;
  const cached = ensureHomeCacheEntry(cacheKey);
  if (cached && cached.status === 'resolved' && Array.isArray(cached.sections) && cached.sections.length) {
    return cached;
  }
  if (cached && cached.status === 'loading' && cached.promise) {
    return cached.promise;
  }
  const promise = Promise.resolve()
    .then(() => runLoader())
    .then((result) => {
      const sections = cloneSections(result && Array.isArray(result.sections) ? result.sections : []);
      const errorText = normalizeString(result && result.errorText);
      return setHomeCacheEntry(cacheKey, {
        status: errorText ? 'error' : 'resolved',
        sourceKey: cacheKey,
        sections,
        errorText,
        promise: null,
        updatedAt: Date.now(),
      });
    })
    .catch((error) => {
      const errorText = normalizeString(error && error.message) || '加载失败';
      return setHomeCacheEntry(cacheKey, {
        status: 'error',
        sourceKey: cacheKey,
        sections: [],
        errorText,
        promise: null,
        updatedAt: Date.now(),
      });
    });
  setHomeCacheEntry(cacheKey, {
    status: 'loading',
    sourceKey: cacheKey,
    promise,
  });
  return promise;
};
