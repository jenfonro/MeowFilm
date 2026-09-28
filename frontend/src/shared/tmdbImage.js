import { normalizeImageUrl, parseHttpUrl } from './doubanImage';

const TMDB_IMAGE_HOST = 'image.tmdb.org';
const TMDB_PROXY_PREFIXES = ['/tmdb-img/', '/pan/tmdb-img/'];
const TMDB_DEFAULT_IMAGE_PATH_PREFIX = '/t/p/original';

export function normalizeTMDBImageProxyBase(base) {
  const raw = typeof base === 'string' ? base.trim() : '';
  return raw ? raw.replace(/\/+$/, '') : '';
}

function extractTMDBProxyPath(value) {
  const raw = typeof value === 'string' ? value.trim() : '';
  if (!raw) return '';
  const matchedPrefix = TMDB_PROXY_PREFIXES.find((prefix) => raw.startsWith(prefix));
  if (!matchedPrefix) return '';
  const suffix = raw.slice(matchedPrefix.length);
  return suffix ? `/${suffix}` : '';
}

function extractTMDBImagePath(value) {
  const normalized = normalizeImageUrl(value);
  if (!normalized) return '';

  if (normalized.startsWith('/')) {
    if (normalized.startsWith('/t/p/')) return normalized;
    return `${TMDB_DEFAULT_IMAGE_PATH_PREFIX}${normalized}`;
  }

  const directPath = extractTMDBProxyPath(normalized);
  if (directPath) return directPath;

  const parsed = parseHttpUrl(normalized);
  if (!parsed) return '';
  const pathname = typeof parsed.pathname === 'string' ? parsed.pathname.trim() : '';
  const host = typeof parsed.hostname === 'string' ? parsed.hostname.trim().toLowerCase() : '';

  if (host === TMDB_IMAGE_HOST) return `${pathname}${parsed.search || ''}`;

  const proxiedPath = extractTMDBProxyPath(pathname);
  if (!proxiedPath) return '';
  return `${proxiedPath}${parsed.search || ''}`;
}

export function normalizeTMDBImageUrl(value) {
  const normalized = normalizeImageUrl(value);
  if (!normalized) return '';
  const imagePath = extractTMDBImagePath(normalized);
  if (!imagePath) return normalized;
  return `https://${TMDB_IMAGE_HOST}${imagePath}`;
}

export function isTMDBImageUrl(value) {
  const normalized = normalizeTMDBImageUrl(value);
  const parsed = parseHttpUrl(normalized);
  const host = parsed && parsed.hostname ? String(parsed.hostname).trim().toLowerCase() : '';
  return host === TMDB_IMAGE_HOST;
}

export function rewriteTMDBImageUrl(value, { proxyBase = '' } = {}) {
  const normalized = normalizeTMDBImageUrl(value);
  if (!normalized) return '';
  if (!isTMDBImageUrl(normalized)) return normalized;
  const base = normalizeTMDBImageProxyBase(proxyBase);
  if (!base) return normalized;
  try {
    const parsed = new URL(normalized);
    const suffix = `${parsed.pathname || ''}${parsed.search || ''}`.replace(/^\/+/, '');
    return suffix ? `${base}/${suffix}` : base;
  } catch (_error) {
    return normalized;
  }
}
