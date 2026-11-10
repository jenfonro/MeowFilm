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
const forbiddenHeaders = new Set([
  'content-length', 'transfer-encoding', 'upgrade', 'trailer',
  'proxy-authorization', 'proxy-connection', 'te',
]);

function text(value, maximum = 4096, code = 'INVALID_ARGUMENT') {
  sourceRequire(typeof value === 'string' && value.length <= maximum &&
    !/[\u0000-\u001f\u007f]/.test(value), code);
  return value;
}

function profile(value) {
  sourceRequire(typeof value === 'string' && value.length > 0);
  return text(value, 512);
}

function route(value) {
  sourceRequire(typeof value === 'string' &&
    /^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$/.test(value));
  return value;
}

function httpUrl(value, code = 'INVALID_ARGUMENT') {
  sourceRequire(typeof value === 'string' && value.length <= 16384, code);
  try {
    const url = new URL(value);
    sourceRequire(['http:', 'https:'].includes(url.protocol), code);
    return url.href;
  } catch (error) {
    if (error?.code) throw error;
    throw sourceFault(code);
  }
}

function headers(value = {}, code = 'INVALID_ARGUMENT') {
  sourceObject(value, code);
  const result = {};
  sourceRequire(Object.keys(value).length <= 128, code);
  for (const [key, item] of Object.entries(value)) {
    sourceRequire(/^[!#$%&'*+\-.^_`|~0-9A-Za-z]{1,128}$/.test(key), code);
    const values = Array.isArray(item) ? item : [item];
    sourceRequire(values.length > 0 && values.length <= 32, code);
    result[key.toLowerCase()] = values.map(entry => {
      sourceRequire(typeof entry === 'string' && entry.length <= 16384 &&
        !/[\r\n]/.test(entry), code);
      return entry;
    });
  }
  return result;
}

function requestHeaders(value = {}) {
  const result = headers(value);
  for (const name of Object.keys(result)) {
    sourceRequire(!forbiddenHeaders.has(name), 'PERMISSION_DENIED');
  }
  if (result.connection) {
    sourceRequire(result.connection.length === 1 &&
      ['close', 'keep-alive'].includes(result.connection[0].toLowerCase()),
    'PERMISSION_DENIED');
    delete result.connection;
  }
  return result;
}

function bytes(value, code = 'INVALID_ARGUMENT') {
  sourceRequire(value instanceof Uint8Array, code);
  return new Uint8Array(value);
}

function jsonClone(value, code = 'INVALID_ARGUMENT') {
  const ancestors = new Set(), frames = [];
  const visit = (input, depth) => {
    sourceRequire(depth <= 64, code);
    if (input === null || typeof input === 'boolean') return input;
    if (typeof input === 'string') {
      sourceRequire(input.isWellFormed(), code);
      return input;
    }
    if (typeof input === 'number') {
      sourceRequire(Number.isFinite(input) && (!Number.isInteger(input) || Number.isSafeInteger(input)), code);
      return input;
    }
    sourceRequire(input && typeof input === 'object' && !ArrayBuffer.isView(input) &&
      !(input instanceof ArrayBuffer) && !ancestors.has(input), code);
    ancestors.add(input);
    const array = Array.isArray(input);
    const output = array ? new Array(input.length) : {};
    frames.push({ input, output, array, depth, index: 0,
      entries: array ? null : Object.entries(input), length: array ? input.length : 0 });
    return output;
  };
  const output = visit(value, 0);
  while (frames.length) {
    const frame = frames[frames.length - 1];
    if (frame.index === (frame.array ? frame.length : frame.entries.length)) {
      ancestors.delete(frame.input);
      frames.pop();
      continue;
    }
    const index = frame.index++;
    if (frame.array && !(index in frame.input)) continue;
    const [key, child] = frame.array ? [index, frame.input[index]] : frame.entries[index];
    if (!frame.array) sourceRequire(key.isWellFormed(), code);
    Object.defineProperty(frame.output, key, {
      value: visit(child, frame.depth + 1), enumerable: true, configurable: true, writable: true,
    });
  }
  return output;
}

function fields(value, allowed, required = allowed, code = 'INVALID_ARGUMENT') {
  sourceObject(value, code);
  sourceRequire(Object.keys(value).every(key => allowed.includes(key)) &&
    required.every(key => Object.hasOwn(value, key)), code);
  return value;
}

function validateHttp(input) {
  fields(input, [
    'version', 'url', 'method', 'headers', 'body', 'timeoutMs', 'redirects', 'cookieProfile',
  ], ['version', 'url']);
  sourceRequire(input.version === hostApiVersion);
  const method = String(input.method ?? 'GET').toUpperCase();
  sourceRequire(['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'].includes(method));
  const timeoutMs = input.timeoutMs ?? 15000;
  const redirects = input.redirects ?? 5;
  sourceRequire(Number.isSafeInteger(timeoutMs) && timeoutMs >= 1 && timeoutMs <= 120000);
  sourceRequire(Number.isSafeInteger(redirects) && redirects >= 0 && redirects <= 10);
  sourceRequire(input.cookieProfile === undefined || typeof input.cookieProfile === 'string');
  return {
    version: hostApiVersion,
    url: httpUrl(input.url),
    method,
    headers: requestHeaders(input.headers),
    body: bytes(input.body ?? new Uint8Array()),
    timeoutMs,
    redirects,
    cookieProfile: input.cookieProfile === undefined ? '' : profile(input.cookieProfile),
  };
}

function validateHttpResult(value) {
  fields(value, [
    'version', 'status', 'statusText', 'headers', 'url', 'body',
  ], ['version', 'status', 'url', 'body'], 'INVALID_RESPONSE');
  sourceRequire(value.version === hostApiVersion && Number.isInteger(value.status) &&
    value.status >= 100 && value.status <= 999, 'INVALID_RESPONSE');
  return {
    version: hostApiVersion,
    status: value.status,
    statusText: typeof value.statusText === 'string' ? value.statusText.slice(0, 256) : '',
    headers: headers(value.headers, 'INVALID_RESPONSE'),
    url: httpUrl(value.url, 'INVALID_RESPONSE'),
    body: bytes(value.body, 'INVALID_RESPONSE'),
  };
}

function validateInput(name, input) {
  switch (name) {
    case 'http.request': return validateHttp(input);
    case 'cookie.get':
    case 'cookie.clear':
      fields(input, ['version', 'profile', 'url']);
      sourceRequire(input.version === hostApiVersion && typeof input.url === 'string');
      return { version: hostApiVersion, profile: profile(input.profile), url: httpUrl(input.url) };
    case 'cookie.set':
      fields(input, ['version', 'profile', 'url', 'values']);
      sourceRequire(input.version === hostApiVersion && typeof input.url === 'string');
      {
        const values = Array.isArray(input.values) ? input.values : [input.values];
        sourceRequire(values.length > 0 && values.length <= 64);
        return {
          version: hostApiVersion, profile: profile(input.profile), url: httpUrl(input.url),
          values: values.map(value => text(value, 16384)),
        };
      }
    case 'storage.get':
    case 'storage.delete':
      fields(input, ['version', 'profile', 'key']);
      sourceRequire(input.version === hostApiVersion);
      return {
        version: hostApiVersion, profile: profile(input.profile),
        key: text(input.key, 256),
      };
    case 'storage.set':
      fields(input, ['version', 'profile', 'key', 'value']);
      sourceRequire(input.version === hostApiVersion);
      return {
        version: hostApiVersion, profile: profile(input.profile),
        key: text(input.key, 256), value: jsonClone(input.value),
      };
    case 'encoding.encode':
      fields(input, ['version', 'encoding', 'text']);
      sourceRequire(input.version === hostApiVersion &&
        typeof input.text === 'string');
      return {
        version: hostApiVersion, encoding: text(input.encoding, 64).toLowerCase(),
        text: input.text,
      };
    case 'encoding.decode':
      fields(input, ['version', 'encoding', 'body']);
      sourceRequire(input.version === hostApiVersion);
      return {
        version: hostApiVersion, encoding: text(input.encoding, 64).toLowerCase(),
        body: bytes(input.body ?? new Uint8Array()),
      };
    case 'crypto.digest':
      fields(input, ['version', 'algorithm', 'data']);
      sourceRequire(input.version === hostApiVersion &&
        ['md5', 'sha1', 'sha256', 'sha512'].includes(input.algorithm));
      return {
        version: hostApiVersion, algorithm: input.algorithm,
        data: bytes(input.data),
      };
    case 'crypto.hmac':
      fields(input, ['version', 'algorithm', 'key', 'data']);
      sourceRequire(input.version === hostApiVersion &&
        ['md5', 'sha1', 'sha256', 'sha512'].includes(input.algorithm));
      return {
        version: hostApiVersion, algorithm: input.algorithm,
        key: bytes(input.key), data: bytes(input.data),
      };
    case 'crypto.aes':
      fields(input, ['version', 'operation', 'algorithm', 'key', 'iv', 'data'],
        ['version', 'operation', 'algorithm', 'key', 'data']);
      sourceRequire(input.version === hostApiVersion &&
        ['encrypt', 'decrypt'].includes(input.operation) &&
        /^aes-(128|192|256)-(cbc|ecb)$/.test(input.algorithm));
      {
        const key = bytes(input.key);
        const iv = bytes(input.iv ?? new Uint8Array());
        const bits = Number(/^aes-(128|192|256)-/.exec(input.algorithm)[1]);
        sourceRequire(key.length === bits / 8);
        sourceRequire(input.algorithm.endsWith('-ecb')
          ? iv.length === 0 : iv.length === 16);
        return {
          version: hostApiVersion, operation: input.operation, algorithm: input.algorithm,
          key, iv,
          data: bytes(input.data),
        };
      }
    case 'crypto.random':
      fields(input, ['version', 'length']);
      sourceRequire(input.version === hostApiVersion &&
        Number.isSafeInteger(input.length) && input.length >= 0);
      return { version: hostApiVersion, length: input.length };
    case 'web.interact':
      fields(input, ['version', 'profile', 'mode', 'request']);
      sourceRequire(input.version === hostApiVersion);
      return {
        version: hostApiVersion,
        profile: profile(input.profile),
        mode: text(input.mode, 64),
        request: jsonClone(sourceObject(input.request)),
      };
    case 'virtual.register':
      fields(input, ['version', 'profile', 'route', 'metadata'],
        ['version', 'profile', 'route']);
      sourceRequire(input.version === hostApiVersion);
      return {
        version: hostApiVersion, profile: profile(input.profile),
        route: route(input.route), metadata: jsonClone(input.metadata ?? {}),
      };
    case 'virtual.unregister':
      fields(input, ['version', 'profile', 'route']);
      sourceRequire(input.version === hostApiVersion);
      return {
        version: hostApiVersion, profile: profile(input.profile), route: route(input.route),
      };
    default: throw sourceFault('CAPABILITY_UNAVAILABLE');
  }
}

function validateResult(name, value) {
  if (name === 'http.request') return validateHttpResult(value);
  if (name === 'encoding.encode' || name.startsWith('crypto.')) {
    return bytes(value, 'INVALID_RESPONSE');
  }
  if (name === 'encoding.decode' || name === 'cookie.get') {
    sourceRequire(typeof value === 'string', 'INVALID_RESPONSE');
    return value;
  }
  return jsonClone(value, 'INVALID_RESPONSE');
}

export function createCapabilityClient({ invoke } = {}) {
  sourceRequire(typeof invoke === 'function');
  return Object.freeze({
    version: hostApiVersion,
    names: Object.freeze([...names]),
    async call(name, input, context = {}) {
      sourceRequire(names.has(name), 'CAPABILITY_UNAVAILABLE');
      sourceRequire(context.signal === undefined || context.signal instanceof AbortSignal);
      if (context.signal?.aborted) throw sourceFault('CANCELLED');
      const captured = validateInput(name, input);
      try {
        const result = await invoke(name, captured, context);
        if (context.signal?.aborted) throw sourceFault('CANCELLED');
        return validateResult(name, result);
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
    sourceRequire(a >= 0 && b >= 0 && c >= 0 && d >= 0);
    if (offset < output.length) output[offset++] = (a << 2) | (b >>> 4);
    if (offset < output.length) output[offset++] = ((b & 15) << 4) | (c >>> 2);
    if (offset < output.length) output[offset++] = ((c & 3) << 6) | d;
  }
  sourceRequire(bytesToBase64(output) === value);
  return output;
}

export async function decodeHttpBody(response, capabilities, encoding = 'utf-8', context = {}) {
  const bytes = response.body;
  const label = String(encoding || 'utf-8').trim().toLowerCase();
  try { return new TextDecoder(label, { fatal: false }).decode(bytes); }
  catch {
    return capabilities.call('encoding.decode', {
      version: hostApiVersion, encoding: label, body: response.body,
    }, context);
  }
}

export async function encodeText(value, capabilities, encoding = 'utf-8', context = {}) {
  const label = String(encoding || 'utf-8').trim().toLowerCase();
  if (['utf-8', 'utf8'].includes(label)) return new TextEncoder().encode(String(value));
  return capabilities.call('encoding.encode', {
    version: hostApiVersion, encoding: label, text: String(value),
  }, context);
}
