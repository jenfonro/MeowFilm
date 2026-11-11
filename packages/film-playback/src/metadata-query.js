/** The same effective query keys both cached App reads and direct provider reads. */
export function tmdbQuery(path, query, settings) {
  return {
    language: settings.language ?? 'zh-CN',
    ...(/^\/3\/(?:search|discover)\//.test(path) ? {
      region: settings.region ?? 'CN', include_adult: settings.includeAdult ?? false,
    } : {}),
    ...query,
  };
}
