import { fault, requireValue } from './errors.js';
import { isLoopbackHost } from './network-boundary.js';

const blockedProxyPath = path => /^\/(?:website|admin|__meow|config|full-config)(?:\/|$)/i.test(path);

export function allowedProxyPath(path) {
  if (typeof path !== 'string' || !path.startsWith('/') || path.startsWith('//')) return false;
  let decoded = path;
  for (;;) {
    const next = decoded.replace(/%([0-9a-f]{2})/gi,
      (_, value) => String.fromCharCode(Number.parseInt(value, 16)));
    if (next === decoded) break;
    decoded = next;
  }
  if (!decoded.startsWith('/') || decoded.startsWith('//') ||
    /[\\\u0000-\u001f\u007f]/.test(decoded)) return false;
  let normalized;
  try { normalized = new URL(decoded, 'http://127.0.0.1').pathname; }
  catch { return false; }
  return normalized.startsWith('/') && !normalized.startsWith('//') &&
    !blockedProxyPath(normalized);
}

export function fixedOriginURL(origin, path, search = '', hash = '') {
  requireValue(typeof path === 'string' && path.startsWith('/') && !path.startsWith('//'),
    'PERMISSION_DENIED');
  const target = new URL(origin);
  target.pathname = path;
  target.search = search;
  target.hash = hash;
  return target;
}

/** Map a loopback URL through an endpoint snapshot; never invent an endpoint. */
export function mapLocalSnapshot(value, endpoints) {
  let url;
  try { url = new URL(value); } catch { return null; }
  if (!['http:', 'https:'].includes(url.protocol) || !isLoopbackHost(url.hostname)) return null;
  const requested = Number(url.port || (url.protocol === 'https:' ? 443 : 80));
  const matching = endpoints.filter(item => {
    let actual;
    try { actual = new URL(item.origin); } catch { return false; }
    const actualPort = Number(actual.port ||
      (actual.protocol === 'https:' ? 443 : 80));
    return actualPort === requested || item.requestedPorts?.includes(requested);
  });
  const endpoint = matching.find(item => new URL(item.origin).protocol === url.protocol);
  if (!endpoint && matching.length) throw fault('UNMAPPED_LOCAL_URL');
  if (!endpoint) return null;
  const mapped = new URL(endpoint.origin);
  mapped.pathname = url.pathname;
  mapped.search = url.search;
  mapped.hash = url.hash;
  return mapped;
}
