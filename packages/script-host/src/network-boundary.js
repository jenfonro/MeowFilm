import * as dns from 'node:dns';
import { isIP } from 'node:net';
import { fault, requireValue } from './errors.js';

const localNames = new Set(['localhost', 'localhost.localdomain', 'ip6-localhost', '0.0.0.0', '::', '::1']);
const localSuffixes = ['.localhost', '.local', '.lan', '.internal', '.home.arpa'];
const denied = () => Object.assign(new Error('EACCES'), { code: 'EACCES' });

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

function publicIPv4(bytes) {
  const [a, b, c] = bytes;
  if (a === 0 || a === 10 || a === 127 || a >= 224) return false;
  if (a === 100 && b >= 64 && b <= 127) return false;
  if (a === 169 && b === 254 || a === 172 && b >= 16 && b <= 31 || a === 192 && b === 168) return false;
  if (a === 192 && b === 0 && c === 0 || a === 192 && b === 0 && c === 2 ||
    a === 192 && b === 88 && c === 99) return false;
  if (a === 198 && (b === 18 || b === 19) || a === 198 && b === 51 && c === 100 ||
    a === 203 && b === 0 && c === 113) return false;
  return true;
}

function prefix(bytes, expected, bits) {
  if (bytes.length < Math.ceil(bits / 8) || expected.length < Math.ceil(bits / 8)) return false;
  const whole = Math.floor(bits / 8), remaining = bits % 8;
  for (let index = 0; index < whole; index++) if (bytes[index] !== expected[index]) return false;
  return remaining === 0 || (bytes[whole] >> (8 - remaining)) === (expected[whole] >> (8 - remaining));
}

function publicIPv6(bytes) {
  // IPv4-mapped IPv6 must retain the IPv4 decision.
  if (prefix(bytes, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255], 96))
    return publicIPv4(bytes.slice(12));
  // Current globally routable unicast space. Exclude documentation and
  // transition ranges that can encode a non-public IPv4 target.
  if (!(bytes[0] >= 0x20 && bytes[0] <= 0x3f)) return false;
  if (prefix(bytes, [0x20, 0x01, 0x00], 23) ||
    prefix(bytes, [0x20, 0x01, 0x0d, 0xb8], 32) ||
    prefix(bytes, [0x3f, 0xff, 0x00], 20)) return false;
  if (prefix(bytes, [0x20, 0x02], 16) && !publicIPv4(bytes.slice(2, 6))) return false;
  return true;
}

export function isPublicAddress(input) {
  const v4 = ipv4Bytes(input);
  if (v4) return publicIPv4(v4);
  const v6 = ipv6Bytes(input);
  return Boolean(v6 && publicIPv6(v6));
}

export function isLocalHost(input) {
  let value;
  try { value = hostName(input); } catch { return false; }
  if (localNames.has(value) || localSuffixes.some(suffix => value.endsWith(suffix))) return true;
  const version = isIP(value);
  return version !== 0 && !isPublicAddress(value);
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

export function assertExternalHost(input) {
  const value = hostName(input);
  requireValue(!localNames.has(value) && !localSuffixes.some(suffix => value.endsWith(suffix)),
    'PERMISSION_DENIED');
  if (isIP(value)) requireValue(isPublicAddress(value), 'PERMISSION_DENIED');
  else requireValue(value.includes('.') && value.split('.').every(label =>
    label.length > 0 && label.length <= 63 && !label.startsWith('-') && !label.endsWith('-') &&
    /^[a-z0-9-]+$/.test(label)), 'PERMISSION_DENIED');
  return value;
}

function lookupArguments(options, callback) {
  if (typeof options === 'function') return [{}, options];
  return [typeof options === 'number' ? { family: options } : { ...(options ?? {}) }, callback];
}

/** Connection-time lookup rejects the complete answer if any address is
 * non-public, so a hostname cannot alternate between public and host/private
 * targets across redirects or retries. */
export function publicLookup(hostname, options, callback) {
  const [settings, done] = lookupArguments(options, callback);
  requireValue(typeof done === 'function', 'INVALID_ARGUMENT');
  try { hostname = assertExternalHost(hostname); }
  catch { queueMicrotask(() => done(denied())); return; }
  const family = settings.family === 4 || settings.family === 6 ? settings.family : 0;
  dns.lookup(hostname, { family, hints: settings.hints ?? 0, all: true, verbatim: true }, (error, addresses) => {
    if (error) { done(error); return; }
    if (!Array.isArray(addresses) || !addresses.length || addresses.some(item => !isPublicAddress(item.address))) {
      done(denied()); return;
    }
    if (settings.all) done(null, addresses);
    else done(null, addresses[0].address, addresses[0].family);
  });
}

function checkedName(value) {
  try { return assertExternalHost(value); } catch { throw denied(); }
}

function checkedAddress(value) {
  if (!isPublicAddress(value)) throw denied();
  return value;
}

function sanitize(kind, value) {
  if (kind === 'lookup') {
    if (Array.isArray(value)) return value.map(item =>
      typeof item === 'string' ? checkedAddress(item) : { ...item, address: checkedAddress(item.address) });
    return typeof value === 'string' ? checkedAddress(value) : { ...value, address: checkedAddress(value.address) };
  }
  if (['resolve', 'resolve4', 'resolve6'].includes(kind))
    return value.map(item => typeof item === 'string' ? checkedAddress(item) : { ...item, address: checkedAddress(item.address) });
  if (['resolveCname', 'resolveNs', 'resolvePtr', 'reverse'].includes(kind)) return value.map(checkedName);
  if (kind === 'resolveMx') return value.map(item => ({ ...item, exchange: checkedName(item.exchange) }));
  if (kind === 'resolveSrv') return value.map(item => ({ ...item, name: checkedName(item.name) }));
  return value;
}

function unavailable(callback) {
  const error = fault('CAPABILITY_UNAVAILABLE');
  if (typeof callback === 'function') { queueMicrotask(() => callback(error)); return; }
  throw error;
}

/** Guest DNS is either filtered through the same public-address policy or is
 * unavailable when a platform network adapter owns all external networking. */
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
      try {
        if (name === 'reverse') checkedAddress(args[0]);
        else checkedName(args[0]);
      } catch (error) { queueMicrotask(() => callback(error)); return; }
      if (name === 'lookup') {
        const options = typeof args[1] === 'object' && args[1] !== null ? { ...args[1] }
          : typeof args[1] === 'number' ? { family: args[1] } : {};
        if (options.order === undefined && options.verbatim === undefined) options.order = defaultResultOrder;
        if (typeof args[1] === 'function') args.splice(1, 0, options);
        else args[1] = options;
      }
      args[args.length - 1] = (error, value, ...rest) => {
        if (error) callback(error);
        else {
          try { callback(null, sanitize(name, value), ...rest); }
          catch (failure) { callback(failure); }
        }
      };
      dns[name](...args);
    };
  }
  facade.promises = Object.fromEntries(methods.map(name => [name, async (...args) => {
    if (adapter) throw fault('CAPABILITY_UNAVAILABLE');
    if (name === 'reverse') checkedAddress(args[0]); else checkedName(args[0]);
    if (name === 'lookup') {
      const options = typeof args[1] === 'object' && args[1] !== null ? { ...args[1] }
        : typeof args[1] === 'number' ? { family: args[1] } : {};
      if (options.order === undefined && options.verbatim === undefined) options.order = defaultResultOrder;
      args[1] = options;
    }
    return sanitize(name, await dns.promises[name](...args));
  }]));
  facade.setDefaultResultOrder = value => {
    requireValue(['ipv4first', 'ipv6first', 'verbatim'].includes(value), 'INVALID_ARGUMENT');
    defaultResultOrder = value;
  };
  facade.getDefaultResultOrder = () => defaultResultOrder;
  return facade;
}
