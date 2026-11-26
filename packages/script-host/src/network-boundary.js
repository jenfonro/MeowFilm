import * as dns from 'node:dns';
import { isIP } from 'node:net';
import { requireValue } from './errors.js';

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

export function isLoopbackHost(input) {
  let value;
  try { value = hostName(input); } catch { return false; }
  if (localNames.has(value) || value.endsWith('.localhost')) return true;
  const kind = isIP(value);
  if (kind === 4) return value.startsWith('127.') || value === '0.0.0.0';
  if (kind === 6) {
    try {
      const canonical = new URL(`http://[${value}]`).hostname;
      return canonical === '[::1]' || canonical === '[::]' ||
        /^\[::ffff:(?:7f[0-9a-f]{2}:[0-9a-f]{1,4}|0:0)\]$/i.test(canonical);
    } catch { return false; }
  }
  return false;
}

export function dnsFacade() {
  const methods = ['lookup', 'resolve', 'resolve4', 'resolve6', 'resolveTxt',
    'resolveCname', 'resolveMx', 'resolveSrv', 'resolveNs', 'resolvePtr', 'reverse'];
  const facade = {};
  let defaultResultOrder = 'verbatim';
  for (const name of methods) {
    facade[name] = (...args) => {
      const callback = args.at(-1);
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
