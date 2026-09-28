export async function fetchBootstrap(page) {
  const url = new URL('/api/bootstrap', window.location.origin);
  if (page) url.searchParams.set('page', page);
  const resp = await fetch(url.toString(), { credentials: 'same-origin' });
  const data = await resp.json().catch(() => null);
  if (!resp.ok) {
    throw new Error((data && data.message) || `HTTP ${resp.status}`);
  }
  return data || {};
}

export const normalizeDoubanDataProxyMode = (value, fallback = 'server-proxy') => {
  const raw = typeof value === 'string' ? value.trim().toLowerCase() : '';
  if (!raw) return fallback;
  if (raw === 'server-proxy') return 'server-proxy';
  if (raw === 'cdn-tx') return 'cdn-tx';
  if (raw === 'cdn-ali') return 'cdn-ali';
  if (raw === 'cors') return 'cors';
  if (raw === 'cors-anywhere') return 'cors-anywhere';
  if (raw === 'custom') return 'custom';
  return fallback;
};

const resolveDoubanDataProxyConfig = (settings = {}) => {
  const mode = normalizeDoubanDataProxyMode(settings.doubanDataProxy, 'server-proxy');
  const custom = typeof settings.doubanDataCustom === 'string' ? settings.doubanDataCustom.trim() : '';
  const base =
    mode === 'cdn-tx'
      ? 'https://m.douban.cmliussss.net'
      : mode === 'cdn-ali'
        ? 'https://m.douban.cmliussss.com'
        : 'https://m.douban.com';
  const proxyBase =
    mode === 'cors'
      ? 'https://ciao-cors.is-an.org/'
      : mode === 'cors-anywhere'
        ? 'https://cors-anywhere.com/'
        : mode === 'custom' && custom
          ? custom
          : '';
  return { mode, base, proxyBase };
};

const toDoubanProxiedURL = (targetURL, proxyBase) => {
  const t = typeof targetURL === 'string' ? targetURL.trim() : '';
  let p = typeof proxyBase === 'string' ? proxyBase.trim() : '';
  if (!t || !p) return t;
  if (!p.endsWith('/') && !p.endsWith('?') && !p.endsWith('&') && !p.endsWith('=')) p = `${p}/`;
  if (p.includes('cors-anywhere.com/')) return `${p}${t}`;
  return `${p}${encodeURIComponent(t)}`;
};

export const buildDoubanDataUrl = (path, settings = {}) => {
  const rel = typeof path === 'string' ? path.trim() : '';
  if (!rel) return { url: '', mode: '' };
  const { mode, base, proxyBase } = resolveDoubanDataProxyConfig(settings);
  if (mode === 'server-proxy') return { url: `/api/douban${rel.startsWith('/') ? rel : `/${rel}`}`, mode };
  const target = `${base}${rel.startsWith('/') ? rel : `/${rel}`}`;
  if (proxyBase) return { url: toDoubanProxiedURL(target, proxyBase), mode };
  return { url: target, mode };
};
