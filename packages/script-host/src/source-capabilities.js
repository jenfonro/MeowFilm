import { fault, requireValue, safeError } from './errors.js';
import { createNodeNetwork } from './node-network.js';
import {
  createCipheriv, createDecipheriv, createHash, createHmac, randomBytes,
} from 'node:crypto';
import iconv from 'iconv-lite';

const version = 1;
const obviousPublicCookieDomains = new Set([
  'com', 'net', 'org', 'edu', 'gov', 'mil', 'int',
  'co.uk', 'org.uk', 'ac.uk',
  'com.cn', 'net.cn', 'org.cn', 'gov.cn',
  'com.au', 'net.au', 'org.au',
  'co.jp', 'ne.jp', 'or.jp',
]);
const defaultStateLimits = Object.freeze({
  storageBytes: 64 * 1024 * 1024,
  storageProfileBytes: 8 * 1024 * 1024,
  cookieBytes: 16 * 1024 * 1024,
  virtualBytes: 16 * 1024 * 1024,
});

function jsonBytes(value) {
  let json;
  try { json = JSON.stringify(value); }
  catch { throw fault('INVALID_ARGUMENT'); }
  requireValue(typeof json === 'string');
  return Buffer.byteLength(json);
}

function normalizeStateLimits(value = {}) {
  requireValue(value && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).every(key => Object.hasOwn(defaultStateLimits, key)));
  const limits = { ...defaultStateLimits, ...value };
  for (const amount of Object.values(limits)) {
    requireValue(Number.isSafeInteger(amount) && amount > 0);
  }
  requireValue(limits.storageProfileBytes <= limits.storageBytes);
  return limits;
}

function base64(value) {
  requireValue(typeof value === 'string');
  const bytes = Buffer.from(value, 'base64');
  requireValue(bytes.toString('base64') === value);
  return bytes;
}

function headerObject(value) {
  requireValue(value && typeof value === 'object' && !Array.isArray(value));
  const result = {};
  for (const [key, values] of Object.entries(value)) {
    requireValue(Array.isArray(values) && values.length > 0);
    result[key] = values.length === 1 ? values[0]
      : key.toLowerCase() === 'cookie' ? values.join('; ') : values.join(', ');
  }
  return result;
}

function responseHeaders(value) {
  const result = {};
  for (const [key, raw] of Object.entries(value ?? {})) {
    const values = Array.isArray(raw) ? raw : [raw];
    result[key.toLowerCase()] = values.filter(item => item !== undefined).map(String);
  }
  return result;
}

function defaultCookiePath(pathname) {
  if (!pathname?.startsWith('/') || pathname === '/') return '/';
  const end = pathname.lastIndexOf('/');
  return end <= 0 ? '/' : pathname.slice(0, end);
}

function domainMatches(hostname, domain, hostOnly) {
  const host = hostname.toLowerCase();
  const expected = domain.toLowerCase();
  return hostOnly ? host === expected : host === expected || host.endsWith(`.${expected}`);
}

function pathMatches(pathname, path) {
  if (pathname === path) return true;
  if (!pathname.startsWith(path)) return false;
  return path.endsWith('/') || pathname[path.length] === '/';
}

class ScopedCookieJar {
  #profiles = new Map();
  #sequence = 0;
  #bytes = 0;

  constructor(maximumBytes) {
    requireValue(Number.isSafeInteger(maximumBytes) && maximumBytes > 0);
    this.maximumBytes = maximumBytes;
  }

  #delete(map, key) {
    const value = map.get(key);
    if (!value) return false;
    this.#bytes -= value.bytes;
    return map.delete(key);
  }

  #drop(name) {
    const map = this.#profiles.get(name);
    if (!map) return;
    for (const value of map.values()) this.#bytes -= value.bytes;
    this.#profiles.delete(name);
  }

  #profile(name) {
    if (!this.#profiles.has(name)) this.#profiles.set(name, new Map());
    return this.#profiles.get(name);
  }

  get(profile, input) {
    const url = new URL(input);
    const values = [];
    const map = this.#profile(profile);
    const now = Date.now();
    for (const [key, cookie] of map) {
      if (cookie.expires !== null && cookie.expires <= now) {
        this.#delete(map, key);
        continue;
      }
      if (!domainMatches(url.hostname, cookie.domain, cookie.hostOnly) ||
          !pathMatches(url.pathname || '/', cookie.path) ||
          cookie.secure && url.protocol !== 'https:') continue;
      values.push(cookie);
    }
    return values.sort((left, right) =>
      right.path.length - left.path.length || left.sequence - right.sequence)
      .map(cookie => `${cookie.name}=${cookie.value}`).join('; ');
  }

  set(profile, input, values) {
    const url = new URL(input);
    const map = this.#profile(profile);
    for (const raw of values) {
      const segments = String(raw).split(';').map(item => item.trim()).filter(Boolean);
      const separator = segments[0]?.indexOf('=') ?? -1;
      if (separator <= 0) continue;
      const name = segments[0].slice(0, separator);
      const value = segments[0].slice(separator + 1);
      if (!/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name) || /[\u0000-\u001f\u007f;]/.test(value)) {
        continue;
      }
      let domain = url.hostname.toLowerCase();
      let hostOnly = true;
      let path = defaultCookiePath(url.pathname);
      let secure = false;
      let expires = null;
      let maxAge = null;
      for (const attribute of segments.slice(1)) {
        const [attributeName, ...rest] = attribute.split('=');
        const lower = attributeName.toLowerCase();
        if (lower === 'path') path = rest.join('=') || '/';
        else if (lower === 'domain') {
          const candidate = rest.join('=').trim().replace(/^\./, '').toLowerCase();
          if (!candidate || !domainMatches(url.hostname, candidate, false) ||
              candidate !== url.hostname.toLowerCase() &&
                (!candidate.includes('.') || obviousPublicCookieDomains.has(candidate))) {
            domain = '';
            break;
          }
          domain = candidate;
          hostOnly = false;
        }
        else if (lower === 'secure') secure = true;
        else if (lower === 'max-age') {
          const seconds = Number(rest[0]);
          if (Number.isFinite(seconds)) maxAge = Math.trunc(seconds);
        } else if (lower === 'expires') {
          const date = Date.parse(rest.join('='));
          if (Number.isFinite(date)) expires = date;
        }
      }
      if (!domain) continue;
      path = path.startsWith('/') ? path : defaultCookiePath(url.pathname);
      if (/[\u0000-\u001f\u007f;]/.test(path)) continue;
      if (maxAge !== null) expires = maxAge <= 0 ? 0 : Date.now() + maxAge * 1000;
      if (name.startsWith('__Secure-') && (!secure || url.protocol !== 'https:')) continue;
      if (name.startsWith('__Host-') &&
          (!secure || url.protocol !== 'https:' || !hostOnly || path !== '/')) continue;
      const key = JSON.stringify([domain, path, name]);
      if (expires !== null && expires <= Date.now()) this.#delete(map, key);
      else {
        const bytes = jsonBytes({ name, value, domain, hostOnly, path, secure, expires });
        const previous = map.get(key);
        requireValue(this.#bytes - (previous?.bytes ?? 0) + bytes <= this.maximumBytes,
          'RESOURCE_LIMIT');
        map.set(key, {
          name, value, domain, hostOnly, path, secure, expires,
          sequence: ++this.#sequence, bytes,
        });
        this.#bytes += bytes - (previous?.bytes ?? 0);
      }
    }
  }

  clear(profile, input) {
    const url = new URL(input);
    const map = this.#profile(profile);
    let changed = false;
    for (const [key, cookie] of map) {
      if (domainMatches(url.hostname, cookie.domain, cookie.hostOnly) &&
          pathMatches(url.pathname || '/', cookie.path)) {
        this.#delete(map, key);
        changed = true;
      }
    }
    return changed;
  }

  release(prefix, except = '') {
    for (const name of this.#profiles.keys()) {
      const kept = except && (name === except || name.startsWith(`${except}:`));
      if (name.startsWith(prefix) && !kept) {
        this.#drop(name);
      }
    }
  }
}

function memoryStorage({ maximumBytes, maximumProfileBytes }) {
  const profiles = new Map();
  let bytes = 0;
  const map = profile => {
    if (!profiles.has(profile)) profiles.set(profile, { values: new Map(), bytes: 0 });
    return profiles.get(profile);
  };
  return {
    get: ({ profile, key }) => {
      const entry = map(profile).values.get(key);
      return entry ? structuredClone(entry.value) : null;
    },
    set: ({ profile, key, value }) => {
      const bucket = map(profile);
      const captured = structuredClone(value);
      const size = Buffer.byteLength(String(key)) + jsonBytes(captured);
      const previous = bucket.values.get(key);
      const nextProfileBytes = bucket.bytes - (previous?.bytes ?? 0) + size;
      const nextBytes = bytes - (previous?.bytes ?? 0) + size;
      requireValue(nextProfileBytes <= maximumProfileBytes && nextBytes <= maximumBytes,
        'RESOURCE_LIMIT');
      bucket.values.set(key, { value: captured, bytes: size });
      bucket.bytes = nextProfileBytes;
      bytes = nextBytes;
      return true;
    },
    delete: ({ profile, key }) => {
      const bucket = map(profile);
      const previous = bucket.values.get(key);
      if (!previous) return false;
      bucket.values.delete(key);
      bucket.bytes -= previous.bytes;
      bytes -= previous.bytes;
      return true;
    },
    releaseProfiles(prefix, except = '') {
      for (const name of profiles.keys()) {
        const kept = except && (name === except || name.startsWith(`${except}:`));
        if (name.startsWith(prefix) && !kept) {
          bytes -= profiles.get(name).bytes;
          profiles.delete(name);
        }
      }
    },
  };
}

function requestDeadline(parent, timeoutMs) {
  requireValue(parent instanceof AbortSignal &&
    Number.isSafeInteger(timeoutMs) && timeoutMs > 0);
  const expiresAt = Date.now() + timeoutMs;
  const controller = new AbortController();
  const abort = () => {
    if (!controller.signal.aborted) {
      controller.abort(safeError(parent.reason, 'CANCELLED'));
    }
  };
  if (parent.aborted) abort();
  else parent.addEventListener('abort', abort, { once: true });
  const timer = setTimeout(() => {
    if (!controller.signal.aborted) controller.abort(fault('TIMEOUT'));
  }, timeoutMs);
  return {
    signal: controller.signal,
    assertOpen() {
      if (!controller.signal.aborted && Date.now() >= expiresAt) {
        controller.abort(fault('TIMEOUT'));
      }
      if (controller.signal.aborted) throw controller.signal.reason ?? fault('CANCELLED');
    },
    close() {
      clearTimeout(timer);
      parent.removeEventListener('abort', abort);
    },
  };
}

/**
 * Windows' current host adapter. It is protocol-neutral and can be replaced by
 * Android/iOS native implementations of the same ABI.
 */
export function createNodeSourceCapabilities({
  network = createNodeNetwork(),
  storage,
  web,
  onDiagnostic,
  stateLimits,
} = {}) {
  const limits = normalizeStateLimits(stateLimits);
  storage ??= memoryStorage({
    maximumBytes: limits.storageBytes,
    maximumProfileBytes: limits.storageProfileBytes,
  });
  requireValue(typeof network === 'function');
  requireValue(storage && ['get', 'set', 'delete', 'releaseProfiles']
    .every(name => typeof storage[name] === 'function'));
  requireValue(web === undefined || typeof web === 'function');
  requireValue(onDiagnostic === undefined || typeof onDiagnostic === 'function');
  const cookies = new ScopedCookieJar(limits.cookieBytes);
  const virtual = new Map();
  let virtualBytes = 0;
  const redirects = new Set([301, 302, 303, 307, 308]);
  const validateURL = value => {
    const url = new URL(value);
    requireValue(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password);
    return url.href;
  };

  async function httpRequest(input, activeSignal, scope) {
    const deadline = requestDeadline(activeSignal, input.timeoutMs);
    try {
      let url = validateURL(input.url);
      let method = input.method;
      let body = input.body ? base64(input.body) : undefined;
      let headers = headerObject(input.headers);
      for (let hop = 0; ; hop++) {
        const outbound = { ...headers };
        if (input.cookieProfile && !outbound.cookie) {
          const cookie = cookies.get(input.cookieProfile, url);
          if (cookie) outbound.cookie = cookie;
        }
        deadline.assertOpen();
        const response = await network({
          url,
          options: {
            method,
            headers: outbound,
            body,
            timeout: input.timeoutMs,
            follow_max: 0,
            followRedirect: false,
            encoding: null,
          },
          signal: deadline.signal,
          scope,
          validateURL,
        });
        deadline.assertOpen();
        const responseUrl = validateURL(response.url ?? url);
        const outputHeaders = responseHeaders(response.headers);
        if (input.cookieProfile && outputHeaders['set-cookie']) {
          cookies.set(input.cookieProfile, responseUrl, outputHeaders['set-cookie']);
        }
        const location = outputHeaders.location?.[0];
        if (!location || !redirects.has(response.statusCode) || input.redirects === 0) {
          return {
            version,
            status: response.statusCode,
            statusText: response.statusMessage ?? '',
            headers: outputHeaders,
            url: responseUrl,
            body: Buffer.from(response.body ?? []).toString('base64'),
          };
        }
        requireValue(hop < input.redirects, 'RESOURCE_LIMIT');
        const next = validateURL(new URL(location, responseUrl).href);
        if (new URL(next).origin !== new URL(responseUrl).origin) {
          headers = { ...headers };
          delete headers.authorization;
          delete headers.cookie;
        }
        if ((response.statusCode === 303 && method !== 'HEAD') ||
            ([301, 302].includes(response.statusCode) && method === 'POST')) {
          method = 'GET';
          body = undefined;
          headers = { ...headers };
          delete headers['content-length'];
          delete headers['content-type'];
        }
        url = next;
      }
    } catch (error) {
      deadline.assertOpen();
      throw error;
    } finally {
      deadline.close();
    }
  }

  return Object.freeze({
    async invoke(name, input, { signal, scope } = {}) {
      requireValue(input?.version === version &&
        (signal === undefined || signal instanceof AbortSignal));
      const activeSignal = signal ?? new AbortController().signal;
      if (activeSignal.aborted) throw activeSignal.reason ?? fault('CANCELLED');
      try {
        switch (name) {
          case 'http.request': return await httpRequest(input, activeSignal, scope);
          case 'cookie.get': return cookies.get(input.profile, input.url);
          case 'cookie.set': cookies.set(input.profile, input.url, input.values); return true;
          case 'cookie.clear': return cookies.clear(input.profile, input.url);
          case 'storage.get': return await storage.get({ ...input, signal: activeSignal, scope });
          case 'storage.set': return await storage.set({ ...input, signal: activeSignal, scope });
          case 'storage.delete': return await storage.delete({ ...input, signal: activeSignal, scope });
          case 'encoding.encode':
            requireValue(iconv.encodingExists(input.encoding), 'UNSUPPORTED_OPTION');
            return iconv.encode(input.text, input.encoding).toString('base64');
          case 'encoding.decode':
            requireValue(iconv.encodingExists(input.encoding), 'UNSUPPORTED_OPTION');
            return iconv.decode(base64(input.body), input.encoding);
          case 'crypto.digest':
            return createHash(input.algorithm).update(base64(input.data)).digest('base64');
          case 'crypto.hmac':
            return createHmac(input.algorithm, base64(input.key))
              .update(base64(input.data)).digest('base64');
          case 'crypto.random':
            return randomBytes(input.length).toString('base64');
          case 'crypto.aes': {
            const operation = input.operation === 'encrypt' ? createCipheriv : createDecipheriv;
            const iv = input.algorithm.endsWith('-ecb') ? null : base64(input.iv);
            try {
              const cipher = operation(input.algorithm, base64(input.key), iv);
              return Buffer.concat([
                cipher.update(base64(input.data)), cipher.final(),
              ]).toString('base64');
            } catch { throw fault('UNSUPPORTED_CRYPTO'); }
          }
          case 'web.interact':
            if (!web) throw fault('CAPABILITY_UNAVAILABLE');
            return await web({ ...input, signal: activeSignal, scope });
          case 'virtual.register': {
            const key = JSON.stringify([input.profile, input.route]);
            const metadata = structuredClone(input.metadata);
            const bytes = Buffer.byteLength(key) + jsonBytes(metadata);
            const previous = virtual.get(key);
            requireValue(virtualBytes - (previous?.bytes ?? 0) + bytes <= limits.virtualBytes,
              'RESOURCE_LIMIT');
            virtual.set(key, { metadata, bytes });
            virtualBytes += bytes - (previous?.bytes ?? 0);
            return { url: `meow-source://${encodeURIComponent(input.profile)}/${encodeURIComponent(input.route)}` };
          }
          case 'virtual.unregister': {
            const key = JSON.stringify([input.profile, input.route]);
            const previous = virtual.get(key);
            if (!previous) return false;
            virtual.delete(key);
            virtualBytes -= previous.bytes;
            return true;
          }
          default: throw fault('CAPABILITY_UNAVAILABLE');
        }
      } catch (error) {
        if (onDiagnostic) {
          try { onDiagnostic({ name, code: safeError(error, 'CAPABILITY_UNAVAILABLE').code }); }
          catch { /* Diagnostics do not affect capability results. */ }
        }
        throw safeError(error, name === 'http.request' ? 'NETWORK_ERROR' : 'CAPABILITY_UNAVAILABLE');
      }
    },
    resolveVirtual(profile, route) {
      const value = virtual.get(JSON.stringify([profile, route]));
      return value === undefined ? null : structuredClone(value.metadata);
    },
    async releaseProfiles(prefix, except = '') {
      requireValue(typeof prefix === 'string' && prefix.length > 0 &&
        typeof except === 'string');
      cookies.release(prefix, except);
      for (const key of virtual.keys()) {
        const [profile] = JSON.parse(key);
        const kept = except && (profile === except || profile.startsWith(`${except}:`));
        if (profile.startsWith(prefix) && !kept) {
          virtualBytes -= virtual.get(key).bytes;
          virtual.delete(key);
        }
      }
      await storage.releaseProfiles(prefix, except);
    },
  });
}
