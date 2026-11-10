import { requireValue } from './errors.js';

export function fixedOriginURL(origin, path, search = '', hash = '') {
  requireValue(typeof path === 'string' && path.startsWith('/'), 'INVALID_ARGUMENT');
  const target = new URL(origin);
  target.pathname = path;
  target.search = search;
  target.hash = hash;
  return target;
}
