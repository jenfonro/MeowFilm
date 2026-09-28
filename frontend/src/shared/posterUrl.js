import { normalizeImageUrl, rewriteDoubanImageUrl } from './doubanImage';
import { normalizeTMDBImageProxyBase, rewriteTMDBImageUrl } from './tmdbImage';
import { normalizeString } from './normalize';

export function normalizePosterProxySettings(settings = {}) {
  const source = settings && typeof settings === 'object' ? settings : {};
  return {
    doubanImgProxy: normalizeString(source.doubanImgProxy) || 'server-proxy',
    doubanImgCustom: normalizeString(source.doubanImgCustom),
    tmdbImageProxyBase: normalizeTMDBImageProxyBase(source.tmdbImageProxyBase),
  };
}

export function rewriteDisplayPosterUrl(url, settings = {}) {
  const original = normalizeImageUrl(url);
  if (!original) return '';
  const resolved = normalizePosterProxySettings(settings);
  const rewrittenDouban = rewriteDoubanImageUrl(original, {
    mode: resolved.doubanImgProxy,
    custom: resolved.doubanImgCustom,
    defaultMode: 'server-proxy',
  });
  return rewriteTMDBImageUrl(rewrittenDouban, {
    proxyBase: resolved.tmdbImageProxyBase,
  });
}
