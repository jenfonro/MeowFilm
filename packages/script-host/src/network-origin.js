import { isIP } from 'node:net';
import { fault, requireValue } from './errors.js';

function publicIPv4(host) {
  const parts = host.split('.').map(Number);
  return parts.length === 4 && parts.every(value => Number.isInteger(value) && value >= 0 && value <= 255) &&
    parts[0] !== 0 && parts[0] !== 10 && parts[0] !== 127 && parts[0] < 224 &&
    !(parts[0] === 100 && parts[1] >= 64 && parts[1] <= 127) &&
    !(parts[0] === 169 && parts[1] === 254) &&
    !(parts[0] === 172 && parts[1] >= 16 && parts[1] <= 31) &&
    !(parts[0] === 192 && parts[1] === 168);
}

function publicHost(input) {
  const host = input.toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '');
  if (!host || host === 'localhost' || host.endsWith('.localhost') || host.endsWith('.local')) return false;
  const family = isIP(host);
  if (family === 4) return publicIPv4(host);
  if (family !== 6) return true;
  if (host.startsWith('::ffff:')) return false;
  return host !== '::' && host !== '::1' && !host.startsWith('fc') && !host.startsWith('fd') &&
    !/^fe[89ab]/.test(host) && !/^fe[cdef]/.test(host) && !host.startsWith('ff');
}

export function normalizeNetworkOrigin(input, { originOnly = true } = {}) {
  let url;
  try { url = new URL(input); } catch { throw fault('PERMISSION_DENIED'); }
  requireValue(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password &&
    publicHost(url.hostname), 'PERMISSION_DENIED');
  if (originOnly) {
    requireValue(url.pathname === '/' && !url.search && !url.hash && url.origin === input,
      'PERMISSION_DENIED');
  }
  return url.origin;
}
