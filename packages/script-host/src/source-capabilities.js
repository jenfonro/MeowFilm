import { fault, requireValue, safeError } from './errors.js';
import { createNodeNetwork } from './node-network.js';
const version = 2;
const obviousPublicCookieDomains = new Set([
  'com', 'net', 'org', 'edu', 'gov', 'mil', 'int',
  'co.uk', 'org.uk', 'ac.uk',
  'com.cn', 'net.cn', 'org.cn', 'gov.cn',
  'com.au', 'net.au', 'org.au',
  'co.jp', 'ne.jp', 'or.jp',
]);
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

function cookiePair(raw) {
  const separator = raw.indexOf('=');
  if (separator <= 0) return null;
  const name = raw.slice(0, separator).trim();
  const value = raw.slice(separator + 1).trim();
  if (!/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name) || /[\u0000-\u001f\u007f;]/.test(value)) return null;
  return { name, value };
}

function webRequestURL(mode, request) {
  if (request.url) return request.url;
  const args = request.arguments ?? [];
  if (['webview', 'webViewGetSource'].includes(mode) &&
      !(typeof args[0] === 'string' && /^https?:\/\//i.test(args[0]))) return args[1];
  return args[0];
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

  #delete(map, key) {
    const value = map.get(key);
    if (!value) return false;
    return map.delete(key);
  }

  #drop(name) {
    const map = this.#profiles.get(name);
    if (!map) return;
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

  forWeb(profile, input, headers) {
    const map = this.#profile(profile), now = Date.now();
    const values = [];
    for (const [key, cookie] of map) {
      if (cookie.expires !== null && cookie.expires <= now) this.#delete(map, key);
      else values.push(cookie);
    }
    const seeds = values.sort((left, right) =>
      right.path.length - left.path.length || left.sequence - right.sequence)
      .map(cookie => ({
        name: cookie.name, value: cookie.value, domain: cookie.domain, hostOnly: cookie.hostOnly,
        path: cookie.path, secure: cookie.secure, httpOnly: cookie.httpOnly ?? false,
        sameSite: cookie.sameSite ?? null, expires: cookie.expires,
      }));
    if (input) {
      const url = new URL(input);
      for (const [name, raw] of Object.entries(headers ?? {})) {
        if (name.toLowerCase() !== 'cookie') continue;
        const text = Array.isArray(raw) ? raw.join('; ') : String(raw);
        for (const part of text.split(';')) {
          const pair = cookiePair(part.trim());
          if (!pair) continue;
          const matching = seeds.filter(cookie => cookie.name === pair.name &&
            domainMatches(url.hostname, cookie.domain, cookie.hostOnly) &&
            pathMatches(url.pathname || '/', cookie.path) &&
            (!cookie.secure || url.protocol === 'https:'));
          const existing = matching.find(cookie => cookie.value === pair.value) ?? matching[0];
          if (existing) existing.value = pair.value;
          else seeds.push({
            ...pair, domain: url.hostname.toLowerCase(), hostOnly: true,
            path: defaultCookiePath(url.pathname), secure: url.protocol === 'https:',
            httpOnly: false, sameSite: null, expires: null,
          });
        }
      }
    }
    return seeds;
  }

  set(profile, input, values) {
    const url = new URL(input);
    const map = this.#profile(profile);
    for (const raw of values) {
      const segments = String(raw).split(';').map(item => item.trim()).filter(Boolean);
      const pair = cookiePair(segments[0] ?? '');
      if (!pair) continue;
      const { name, value } = pair;
      let domain = url.hostname.toLowerCase();
      let hostOnly = true;
      let path = defaultCookiePath(url.pathname);
      let secure = false;
      let httpOnly = false;
      let sameSite = null;
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
        else if (lower === 'httponly') httpOnly = true;
        else if (lower === 'samesite') {
          const attribute = rest.join('=').trim().toLowerCase();
          sameSite = ['Lax', 'Strict', 'None'].find(value => value.toLowerCase() === attribute) ?? null;
        }
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
        map.set(key, {
          name, value, domain, hostOnly, path, secure, httpOnly, sameSite, expires,
          sequence: ++this.#sequence,
        });
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

  snapshot(prefix) {
    return [...this.#profiles].filter(([name]) => name.startsWith(prefix))
      .map(([name, values]) => [name.slice(prefix.length), [...values]]);
  }

  restore(prefix, values) {
    this.release(prefix);
    for (const [suffix, entries] of values) {
      const map = new Map(structuredClone(entries));
      this.#profiles.set(prefix + suffix, map);
      for (const cookie of map.values()) {
        cookie.httpOnly ??= false;
        cookie.sameSite ??= null;
        this.#sequence = Math.max(this.#sequence, cookie.sequence);
      }
    }
  }
}

function memoryStorage() {
  const profiles = new Map();
  const map = profile => {
    if (!profiles.has(profile)) profiles.set(profile, new Map());
    return profiles.get(profile);
  };
  return {
    get: ({ profile, key }) => structuredClone(map(profile).get(key) ?? null),
    set: ({ profile, key, value }) => {
      map(profile).set(key, structuredClone(value));
      return true;
    },
    delete: ({ profile, key }) => map(profile).delete(key),
    snapshot: prefix => [...profiles].filter(([name]) => name.startsWith(prefix))
      .map(([name, values]) => [name.slice(prefix.length), [...values]]),
    restore(prefix, values) {
      this.releaseProfiles(prefix);
      for (const [suffix, entries] of values) profiles.set(prefix + suffix, new Map(structuredClone(entries)));
    },
    releaseProfiles(prefix, except = '') {
      for (const name of profiles.keys()) {
        const kept = except && (name === except || name.startsWith(`${except}:`));
        if (name.startsWith(prefix) && !kept) profiles.delete(name);
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
 * Shared asynchronous I/O and profile owner. Encoding and crypto execute in
 * the source's native runtime before reaching this adapter.
 */
export function createNodeSourceCapabilities({
  network = createNodeNetwork(),
  storage,
  web,
  onDiagnostic,
  onProfileChange,
} = {}) {
  storage ??= memoryStorage();
  requireValue(typeof network === 'function');
  requireValue(storage && ['get', 'set', 'delete', 'releaseProfiles']
    .every(name => typeof storage[name] === 'function'));
  requireValue(web === undefined || typeof web === 'function');
  requireValue(onDiagnostic === undefined || typeof onDiagnostic === 'function');
  requireValue(onProfileChange === undefined || typeof onProfileChange === 'function');
  const cookies = new ScopedCookieJar();
  const virtual = new Map();
  const mutations = new Map();
  const redirects = new Set([301, 302, 303, 307, 308]);
  const validateURL = value => {
    const url = new URL(value);
    requireValue(['http:', 'https:'].includes(url.protocol));
    return url.href;
  };
  const profilePrefix = profile => {
    const match = /^(source:[^:]+:[1-9][0-9]*):/.exec(profile);
    return match ? `${match[1]}:` : profile;
  };
  const capture = prefix => ({
    cookies: cookies.snapshot(prefix),
    storage: storage.snapshot?.(prefix) ?? [],
  });
  const restore = (prefix, state) => {
    cookies.restore(prefix, state.cookies);
    storage.restore?.(prefix, state.storage);
  };
  function mutate(profile, signal, body) {
    const prefix = profilePrefix(profile);
    const work = (mutations.get(prefix) ?? Promise.resolve()).catch(() => {}).then(async () => {
      if (signal.aborted) throw safeError(signal.reason, 'CANCELLED');
      const previous = onProfileChange ? capture(prefix) : null;
      try {
        const result = await body();
        if (signal.aborted) throw safeError(signal.reason, 'CANCELLED');
        await onProfileChange?.(prefix);
        return result;
      } catch (error) {
        if (previous) restore(prefix, previous);
        if (error?.code === 'STALE_SOURCE') throw fault('STALE_SESSION');
        throw error instanceof Error && typeof error.code === 'string' ? fault(error.code) : error;
      }
    });
    mutations.set(prefix, work);
    work.finally(() => { if (mutations.get(prefix) === work) mutations.delete(prefix); }).catch(() => {});
    return work;
  }

  async function httpRequest(input, activeSignal, scope) {
    const deadline = requestDeadline(activeSignal, input.timeoutMs);
    try {
      let url = validateURL(input.url);
      let method = input.method;
      requireValue(input.body === undefined || input.body instanceof Uint8Array);
      let body = input.body === undefined ? undefined : Buffer.from(input.body);
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
          await mutate(input.cookieProfile, deadline.signal, () =>
            cookies.set(input.cookieProfile, responseUrl, outputHeaders['set-cookie']));
        }
        const location = outputHeaders.location?.[0];
        if (!location || !redirects.has(response.statusCode) || input.redirects === 0) {
          return {
            version,
            status: response.statusCode,
            statusText: response.statusMessage ?? '',
            headers: outputHeaders,
            url: responseUrl,
            body: Buffer.from(response.body ?? []),
          };
        }
        requireValue(hop < input.redirects, 'RESOURCE_LIMIT');
        const next = validateURL(new URL(location, responseUrl).href);
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
      if (activeSignal.aborted) throw safeError(activeSignal.reason, 'CANCELLED');
      try {
        switch (name) {
          case 'http.request': return await httpRequest(input, activeSignal, scope);
          case 'cookie.get': return cookies.get(input.profile, input.url);
          case 'cookie.set': return await mutate(input.profile, activeSignal, () => {
            cookies.set(input.profile, input.url, input.values); return true;
          });
          case 'cookie.clear': return await mutate(input.profile, activeSignal, () =>
            cookies.clear(input.profile, input.url));
          case 'storage.get': return await storage.get({ ...input, signal: activeSignal, scope });
          case 'storage.set': return await mutate(input.profile, activeSignal, () =>
            storage.set({ ...input, signal: activeSignal, scope }));
          case 'storage.delete': return await mutate(input.profile, activeSignal, () =>
            storage.delete({ ...input, signal: activeSignal, scope }));
          case 'web.interact': {
            if (!web) throw fault('CAPABILITY_UNAVAILABLE');
            const request = structuredClone(input.request);
            const target = webRequestURL(input.mode, request);
            const url = typeof target === 'string' && /^https?:\/\//i.test(target)
              ? validateURL(target) : undefined;
            const httpProfile = input.profile.replace(/:web$/, ':http');
            request.cookies = cookies.forWeb(httpProfile, url, request.headers);
            if (url) {
              const cookie = cookies.get(httpProfile, url);
              request.headers = { ...(request.headers ?? {}) };
              if (cookie && !Object.keys(request.headers).some(key => key.toLowerCase() === 'cookie')) {
                request.headers.Cookie = cookie;
              }
            }
            const result = await web({ ...input, request, signal: activeSignal, scope });
            if (activeSignal.aborted) throw safeError(activeSignal.reason, 'CANCELLED');
            const envelope = result && typeof result === 'object' && Object.hasOwn(result, 'value') &&
              typeof result.url === 'string' && result.headers &&
              Object.keys(result).every(key => ['value', 'url', 'headers', 'cookieChanges'].includes(key));
            const changes = [];
            if (result && typeof result === 'object' && result.headers) {
              const headers = responseHeaders(result.headers);
              if (headers['set-cookie']?.length && (result.url || url)) {
                changes.push({ url: validateURL(result.url || url), values: headers['set-cookie'] });
              }
            }
            if (envelope && Object.hasOwn(result, 'cookieChanges')) {
              requireValue(Array.isArray(result.cookieChanges), 'INVALID_RESPONSE');
              for (const change of result.cookieChanges) {
                requireValue(change && typeof change === 'object' && !Array.isArray(change) &&
                  typeof change.url === 'string' && typeof change.setCookie === 'string' &&
                  Object.keys(change).every(key => ['url', 'setCookie'].includes(key)), 'INVALID_RESPONSE');
                let origin;
                try { origin = validateURL(change.url); }
                catch { throw fault('INVALID_RESPONSE'); }
                changes.push({ url: origin, values: [change.setCookie] });
              }
            }
            if (envelope) validateURL(result.url);
            if (changes.length) await mutate(httpProfile, activeSignal, () => {
              for (const change of changes) cookies.set(httpProfile, change.url, change.values);
            });
            if (envelope) return result.value;
            return result;
          }
          case 'virtual.register': {
            const key = JSON.stringify([input.profile, input.route]);
            const metadata = structuredClone(input.metadata);
            virtual.set(key, { metadata });
            return { url: `meow-source://${encodeURIComponent(input.profile)}/${encodeURIComponent(input.route)}` };
          }
          case 'virtual.unregister': {
            const key = JSON.stringify([input.profile, input.route]);
            const previous = virtual.get(key);
            if (!previous) return false;
            virtual.delete(key);
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
    snapshotProfiles(prefix) { return structuredClone(capture(prefix)); },
    restoreProfiles(prefix, state) {
      requireValue(typeof storage.restore === 'function', 'CAPABILITY_UNAVAILABLE');
      restore(prefix, structuredClone(state));
    },
    async releaseProfiles(prefix, except = '') {
      requireValue(typeof prefix === 'string' && prefix.length > 0 &&
        typeof except === 'string');
      await Promise.allSettled([...mutations].filter(([name]) =>
        name.startsWith(prefix) && !(except && name.startsWith(`${except}:`))).map(([, work]) => work));
      cookies.release(prefix, except);
      for (const key of virtual.keys()) {
        const [profile] = JSON.parse(key);
        const kept = except && (profile === except || profile.startsWith(`${except}:`));
        if (profile.startsWith(prefix) && !kept) {
          virtual.delete(key);
        }
      }
      await storage.releaseProfiles(prefix, except);
    },
  });
}
