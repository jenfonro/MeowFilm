import {
  sourceFault, sourceObject, sourceRequire, sourceSafeError,
} from './errors.js';

export const hostApiVersion = 2;

const names = new Set([
  'http.request',
  'cookie.get',
  'cookie.set',
  'cookie.clear',
  'storage.get',
  'storage.set',
  'storage.delete',
  'reader.values.read',
  'reader.values.apply',
  'encoding.encode',
  'encoding.decode',
  'crypto.digest',
  'crypto.hmac',
  'crypto.aes',
  'crypto.random',
  'web.interact',
  'virtual.register',
  'virtual.unregister',
]);
const base64Alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
const textEncoder = new TextEncoder();
const forbiddenHeaders = new Set([
  'content-length', 'transfer-encoding', 'upgrade', 'trailer',
  'proxy-authorization', 'proxy-connection', 'te',
]);

function httpUrl(value) {
  sourceRequire(typeof value === 'string');
  try {
    const url = new URL(value);
    sourceRequire(['http:', 'https:'].includes(url.protocol));
    return url.href;
  } catch (error) {
    if (error?.code) throw error;
    throw sourceFault('INVALID_ARGUMENT');
  }
}

function requestHeaders(value = {}) {
  sourceObject(value);
  const result = {};
  for (const [key, item] of Object.entries(value)) {
    const lower = key.toLowerCase();
    sourceRequire(!forbiddenHeaders.has(lower), 'PERMISSION_DENIED');
    const values = (Array.isArray(item) ? item : [item]).map(entry => {
      sourceRequire(typeof entry === 'string' && !/[\r\n]/.test(entry));
      return entry;
    });
    sourceRequire(values.length > 0);
    if (lower === 'connection') {
      sourceRequire(values.length === 1 && ['close', 'keep-alive'].includes(values[0].toLowerCase()),
        'PERMISSION_DENIED');
      continue;
    }
    result[lower] = values;
  }
  return result;
}

function normalizeInput(name, input) {
  sourceObject(input);
  sourceRequire(input.version === hostApiVersion);
  switch (name) {
    case 'http.request':
      return {
        version: hostApiVersion,
        url: httpUrl(input.url),
        method: String(input.method ?? 'GET').toUpperCase(),
        headers: requestHeaders(input.headers),
        body: input.body ?? new Uint8Array(),
        timeoutMs: input.timeoutMs ?? 15000,
        redirects: input.redirects ?? 5,
        cookieProfile: input.cookieProfile ?? '',
      };
    case 'cookie.get':
    case 'cookie.clear':
      return { version: hostApiVersion, profile: input.profile, url: httpUrl(input.url) };
    case 'cookie.set':
      return {
        version: hostApiVersion,
        profile: input.profile,
        url: httpUrl(input.url),
        values: Array.isArray(input.values) ? input.values : [input.values],
      };
    case 'encoding.encode':
      return { version: hostApiVersion, encoding: String(input.encoding).toLowerCase(), text: input.text };
    case 'encoding.decode':
      return {
        version: hostApiVersion,
        encoding: String(input.encoding).toLowerCase(),
        body: input.body ?? new Uint8Array(),
      };
    case 'crypto.aes':
      return {
        version: hostApiVersion,
        operation: input.operation,
        algorithm: input.algorithm,
        key: input.key,
        iv: input.iv ?? new Uint8Array(),
        data: input.data,
      };
    case 'virtual.register':
      return {
        version: hostApiVersion,
        profile: input.profile,
        route: input.route,
        metadata: input.metadata ?? {},
      };
    default:
      return { ...input, version: hostApiVersion };
  }
}

export function createCapabilityClient({ invoke } = {}) {
  sourceRequire(typeof invoke === 'function');
  return Object.freeze({
    version: hostApiVersion,
    names: Object.freeze([...names]),
    async call(name, input, context = {}) {
      sourceRequire(names.has(name), 'CAPABILITY_UNAVAILABLE');
      if (context.signal?.aborted) throw sourceSafeError(context.signal.reason, 'CANCELLED');
      const captured = normalizeInput(name, input);
      try {
        const result = await invoke(name, captured, context);
        if (context.signal?.aborted) throw sourceSafeError(context.signal.reason, 'CANCELLED');
        return result;
      } catch (error) {
        throw sourceSafeError(error, name === 'http.request' ? 'NETWORK_ERROR' : 'CAPABILITY_UNAVAILABLE');
      }
    },
  });
}

export function bytesToBase64(bytes) {
  sourceRequire(bytes instanceof Uint8Array);
  let output = '';
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index];
    const second = index + 1 < bytes.length ? bytes[index + 1] : 0;
    const third = index + 2 < bytes.length ? bytes[index + 2] : 0;
    output += base64Alphabet[first >>> 2];
    output += base64Alphabet[((first & 3) << 4) | (second >>> 4)];
    output += index + 1 < bytes.length
      ? base64Alphabet[((second & 15) << 2) | (third >>> 6)] : '=';
    output += index + 2 < bytes.length ? base64Alphabet[third & 63] : '=';
  }
  return output;
}

export function base64ToBytes(value) {
  sourceRequire(typeof value === 'string' && value.length % 4 === 0 &&
    /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value));
  if (!value) return new Uint8Array();
  const padding = value.endsWith('==') ? 2 : value.endsWith('=') ? 1 : 0;
  const output = new Uint8Array(value.length / 4 * 3 - padding);
  let offset = 0;
  for (let index = 0; index < value.length; index += 4) {
    const a = base64Alphabet.indexOf(value[index]);
    const b = base64Alphabet.indexOf(value[index + 1]);
    const c = value[index + 2] === '=' ? 0 : base64Alphabet.indexOf(value[index + 2]);
    const d = value[index + 3] === '=' ? 0 : base64Alphabet.indexOf(value[index + 3]);
    if (offset < output.length) output[offset++] = (a << 2) | (b >>> 4);
    if (offset < output.length) output[offset++] = ((b & 15) << 4) | (c >>> 2);
    if (offset < output.length) output[offset++] = ((c & 3) << 6) | d;
  }
  return output;
}

export async function decodeHttpBody(response, capabilities, encoding = 'utf-8', context = {}) {
  const bytes = response.body;
  const label = String(encoding || 'utf-8').trim().toLowerCase();
  try { return new TextDecoder(label, { fatal: false }).decode(bytes); }
  catch (error) {
    if (!(error instanceof RangeError)) throw error;
    return capabilities.call('encoding.decode', {
      version: hostApiVersion, encoding: label, body: response.body,
    }, context);
  }
}

export async function encodeText(value, capabilities, encoding = 'utf-8', context = {}) {
  const label = String(encoding || 'utf-8').trim().toLowerCase();
  if (['utf-8', 'utf8'].includes(label)) return textEncoder.encode(String(value));
  return capabilities.call('encoding.encode', {
    version: hostApiVersion, encoding: label, text: String(value),
  }, context);
}
