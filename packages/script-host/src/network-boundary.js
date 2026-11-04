import * as dns from 'node:dns';
import { isIP } from 'node:net';
import { fault, requireValue } from './errors.js';

const localNames = new Set(['localhost', 'localhost.localdomain', 'ip6-localhost', '0.0.0.0', '::', '::1']);

function hostName(input) {
  requireValue(typeof input === 'string' && input.length > 0 && input.length <= 253 &&
    !/[\u0000-\u0020\\/%]/.test(input), 'PERMISSION_DENIED');
  let value = input.toLowerCase();
  if (value.startsWith('[') && value.endsWith(']')) value = value.slice(1, -1);
  if (value.endsWith('.')) value = value.slice(0, -1);
  requireValue(value.length > 0 && !value.includes('%'), 'PERMISSION_DENIED');
  return value;
}

function ipv4Bytes(input) {
  if (isIP(input) !== 4) return null;
  const bytes = input.split('.').map(Number);
  return bytes.length === 4 && bytes.every(value => Number.isInteger(value) && value >= 0 && value <= 255)
    ? bytes : null;
}

function ipv6Bytes(input) {
  if (isIP(input) !== 6) return null;
  let value = input.toLowerCase(), tail = [];
  const dot = value.lastIndexOf(':');
  if (value.includes('.')) {
    const bytes = ipv4Bytes(value.slice(dot + 1));
    if (!bytes) return null;
    tail = [(bytes[0] << 8) | bytes[1], (bytes[2] << 8) | bytes[3]];
    value = value.slice(0, dot) + ':v4';
  }
  const halves = value.split('::');
  if (halves.length > 2) return null;
  const parse = part => part ? part.split(':').filter(Boolean).map(item => {
    if (item === 'v4') return tail;
    if (!/^[a-f0-9]{1,4}$/.test(item)) return null;
    return parseInt(item, 16);
  }).flat() : [];
  const left = parse(halves[0]), right = parse(halves[1] ?? '');
  if (!left || !right || left.some(item => item === null) || right.some(item => item === null)) return null;
  const missing = 8 - left.length - right.length;
  if (missing < 0 || halves.length === 1 && missing !== 0) return null;
  const words = [...left, ...Array(missing).fill(0), ...right];
  if (words.length !== 8) return null;
  return words.flatMap(word => [word >> 8, word & 255]);
}

export function isLoopbackHost(input) {
  let value;
  try { value = hostName(input); } catch { return false; }
  if (localNames.has(value) || value.endsWith('.localhost')) return true;
  const v4 = ipv4Bytes(value);
  if (v4) return v4[0] === 127 || v4.every(byte => byte === 0);
  const v6 = ipv6Bytes(value);
  return Boolean(v6 && (v6.every(byte => byte === 0) ||
    v6.slice(0, 15).every(byte => byte === 0) && v6[15] === 1));
}

function unavailable(callback) {
  const error = fault('CAPABILITY_UNAVAILABLE');
  if (typeof callback === 'function') { queueMicrotask(() => callback(error)); return; }
  throw error;
}

/** Guest DNS is unavailable when a platform network adapter owns all external
 * networking; otherwise it delegates directly to the host resolver. */
export function dnsFacade({ adapter = false } = {}) {
  const methods = ['lookup', 'resolve', 'resolve4', 'resolve6', 'resolveTxt',
    'resolveCname', 'resolveMx', 'resolveSrv', 'resolveNs', 'resolvePtr', 'reverse'];
  const facade = {};
  let defaultResultOrder = 'verbatim';
  for (const name of methods) {
    facade[name] = (...args) => {
      const callback = args.at(-1);
      if (adapter) return unavailable(typeof callback === 'function' ? callback : undefined);
      if (typeof callback !== 'function') throw Object.assign(new TypeError('Callback required'), { code: 'ERR_INVALID_ARG_TYPE' });
      if (name === 'lookup') {
        const options = typeof args[1] === 'object' && args[1] !== null ? { ...args[1] }
          : typeof args[1] === 'number' ? { family: args[1] } : {};
        if (options.order === undefined && options.verbatim === undefined) options.order = defaultResultOrder;
        if (typeof args[1] === 'function') args.splice(1, 0, options);
        else args[1] = options;
      }
      dns[name](...args);
    };
  }
  facade.promises = Object.fromEntries(methods.map(name => [name, async (...args) => {
    if (adapter) throw fault('CAPABILITY_UNAVAILABLE');
    if (name === 'lookup') {
      const options = typeof args[1] === 'object' && args[1] !== null ? { ...args[1] }
        : typeof args[1] === 'number' ? { family: args[1] } : {};
      if (options.order === undefined && options.verbatim === undefined) options.order = defaultResultOrder;
      args[1] = options;
    }
    return dns.promises[name](...args);
  }]));
  facade.setDefaultResultOrder = value => {
    requireValue(['ipv4first', 'ipv6first', 'verbatim'].includes(value), 'INVALID_ARGUMENT');
    defaultResultOrder = value;
  };
  facade.getDefaultResultOrder = () => defaultResultOrder;
  return facade;
}
