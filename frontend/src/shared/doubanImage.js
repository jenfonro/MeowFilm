export function normalizeProxyBase(base) {
  const raw = typeof base === 'string' ? base.trim() : '';
  if (!raw) return '';
  if (/[?&=]$/.test(raw)) return raw;
  return raw.endsWith('/') ? raw : `${raw}/`;
}

export function normalizeImageUrl(url) {
  const raw = typeof url === 'string' ? url.trim() : '';
  if (!raw) return '';
  if (raw.startsWith('//')) return `https:${raw}`;
  if (raw.startsWith('http://')) return `https://${raw.slice('http://'.length)}`;
  return raw;
}

export function parseHttpUrl(urlStr) {
  const normalized = normalizeImageUrl(urlStr);
  if (!normalized) return null;
  try {
    const u = new URL(normalized);
    const protocol = String(u.protocol || '').toLowerCase();
    if (protocol !== 'http:' && protocol !== 'https:') return null;
    return u;
  } catch (_e) {
    return null;
  }
}

export function isAllowedDoubanImageHost(hostname) {
  const host = typeof hostname === 'string' ? hostname.trim().toLowerCase() : '';
  if (!host) return false;
  if (/^img\d+\.doubanio\.com$/.test(host)) return true;
  if (/^img\d+\.douban\.com$/.test(host)) return true;
  if (host === 'img3.doubanio.com') return true;
  if (host === 'img.doubanio.com') return true;
  if (host === 'img.douban.com') return true;
  if (host === 'img.doubanio.cmliussss.net') return true;
  if (host === 'img.doubanio.cmliussss.com') return true;
  return false;
}

export function isDoubanImageUrl(urlStr) {
  const parsed = parseHttpUrl(urlStr);
  const host = parsed && parsed.hostname ? parsed.hostname : '';
  return isAllowedDoubanImageHost(host);
}

export function normalizeDoubanImageProxyMode(mode, fallback = 'server-proxy') {
  const raw = typeof mode === 'string' ? mode.trim() : '';
  const first = raw.split(/[\\s,]+/g)[0] || '';
  const normalized = first.toLowerCase();
  if (!normalized) return fallback;
  if (normalized === 'server-proxy') return 'server-proxy';
  if (normalized === 'douban-cdn-ali') return 'douban-cdn-ali';
  if (normalized === 'cdn-tx') return 'cdn-tx';
  if (normalized === 'cdn-ali') return 'cdn-ali';
  if (normalized === 'custom') return 'custom';
  return fallback;
}

function serverProxyUrl(original) {
  return `/api/douban/image?url=${encodeURIComponent(original)}`;
}

export function swapDoubanImageHost(urlStr, nextHost) {
  const original = normalizeImageUrl(urlStr);
  const target = typeof nextHost === 'string' ? nextHost.trim() : '';
  if (!original || !target) return original;
  try {
    const u = new URL(original);
    if (!isAllowedDoubanImageHost(u.hostname || '')) return original;
    u.protocol = 'https:';
    u.hostname = target;
    return u.toString();
  } catch (_e) {
    return original.replace(
      /(img\d+\.doubanio\.com|img\d+\.douban\.com|img\.doubanio\.com|img\.douban\.com|img3\.doubanio\.com|img\.doubanio\.cmliussss\.(net|com))/gi,
      target
    );
  }
}

export function rewriteDoubanImageUrl(urlStr, { mode, custom, defaultMode = 'server-proxy' } = {}) {
  const original = normalizeImageUrl(urlStr);
  if (!original) return '';

  const p = normalizeDoubanImageProxyMode(mode, defaultMode);
  if (p === 'custom') {
    const base = normalizeProxyBase(typeof custom === 'string' ? custom : '');
    return base ? `${base}${encodeURIComponent(original)}` : original;
  }

  if (!isDoubanImageUrl(original)) return original;

  if (p === 'server-proxy') return serverProxyUrl(original);

  switch (p) {
    case 'douban-cdn-ali':
      return swapDoubanImageHost(original, 'img3.doubanio.com');
    case 'cdn-tx':
      return swapDoubanImageHost(original, 'img.doubanio.cmliussss.net');
    case 'cdn-ali':
      return swapDoubanImageHost(original, 'img.doubanio.cmliussss.com');
    default:
      return original;
  }
}

export function buildDoubanImageCandidates(
  originalUrl,
  { mode, custom, defaultMode = 'server-proxy', includeServerProxyFallback = true } = {}
) {
  const original = normalizeImageUrl(originalUrl);
  if (!original) return [];

  const rewritten = rewriteDoubanImageUrl(original, { mode, custom, defaultMode });
  const candidates = [];
  if (rewritten) candidates.push(rewritten);
  if (!isDoubanImageUrl(original)) return dedupeNonEmpty(candidates);

  const p = normalizeDoubanImageProxyMode(mode, defaultMode);
  if (p === 'server-proxy') return [serverProxyUrl(original)];

  if (p === 'cdn-tx') {
    candidates.push(swapDoubanImageHost(original, 'img.doubanio.cmliussss.net'));
    candidates.push(swapDoubanImageHost(original, 'img.doubanio.cmliussss.com'));
  } else if (p === 'cdn-ali') {
    candidates.push(swapDoubanImageHost(original, 'img.doubanio.cmliussss.com'));
    candidates.push(swapDoubanImageHost(original, 'img.doubanio.cmliussss.net'));
  } else if (p === 'douban-cdn-ali') {
    candidates.push(swapDoubanImageHost(original, 'img3.doubanio.com'));
  }

  if (includeServerProxyFallback) candidates.push(serverProxyUrl(original));
  return dedupeNonEmpty(candidates);
}

function dedupeNonEmpty(values) {
  const uniq = [];
  const seen = new Set();
  values.forEach((v) => {
    const s = typeof v === 'string' ? v.trim() : '';
    if (!s || seen.has(s)) return;
    seen.add(s);
    uniq.push(s);
  });
  return uniq;
}
