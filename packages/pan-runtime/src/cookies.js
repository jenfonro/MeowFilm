import { isIP } from 'node:net';
import { domainToASCII } from 'node:url';
import { PanError } from './errors.js';

const cookieName = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/;

function headers(value) {
  return typeof value === 'string' ? [value] : Array.isArray(value) ? value : [];
}

function pair(source) {
  const equals = source.indexOf('=');
  if (equals < 1) return null;
  const name = source.slice(0, equals).trim(), value = source.slice(equals + 1).trim();
  if (!cookieName.test(name) || /[\x00-\x20\x7f;]/.test(value)) return null;
  return { name, value };
}

function setCookie(source, now) {
  if (typeof source !== 'string') return null;
  const [first, ...attributes] = source.split(';');
  const cookie = pair(first);
  if (!cookie) return null;
  let maxAge, expires;
  for (const attribute of attributes) {
    const equals = attribute.indexOf('=');
    const name = (equals < 0 ? attribute : attribute.slice(0, equals)).trim().toLowerCase();
    const value = equals < 0 ? '' : attribute.slice(equals + 1).trim();
    if (name === 'domain') cookie.domain = value;
    else if (name === 'path') cookie.path = value;
    else if (name === 'secure') cookie.secure = true;
    else if (name === 'max-age' && /^-?\d+$/.test(value)) maxAge = Number(value);
    else if (name === 'expires' && Number.isFinite(Date.parse(value))) expires = Date.parse(value);
  }
  cookie.expires = maxAge === undefined ? expires ?? Infinity :
    maxAge <= 0 ? -Infinity : now + maxAge * 1000;
  return cookie;
}

function address(value) {
  try {
    const url = new URL(value);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error();
    return url;
  } catch { throw new PanError('INVALID_REQUEST'); }
}

const domainMatches = (host, domain) => host === domain ||
  !isIP(host) && host.endsWith(`.${domain}`);

export function mergeCookie(cookie, setCookieHeaders) {
  const values = new Map();
  for (const part of typeof cookie === 'string' ? cookie.split(';') : []) {
    const entry = pair(part);
    if (entry) values.set(entry.name, entry.value);
  }
  const now = Date.now();
  for (const header of headers(setCookieHeaders)) {
    const entry = setCookie(header, now);
    if (!entry) continue;
    if (entry.expires <= now) values.delete(entry.name);
    else values.set(entry.name, entry.value);
  }
  return [...values].map(([name, value]) => `${name}=${value}`).join('; ');
}

export class CookieJar {
  #cookies = new Map();
  #sequence = 0;

  set(value, setCookieHeaders) {
    const url = address(value), host = url.hostname.toLowerCase(), now = Date.now();
    const slash = url.pathname.lastIndexOf('/');
    const defaultPath = slash <= 0 ? '/' : url.pathname.slice(0, slash);
    for (const header of headers(setCookieHeaders)) {
      const cookie = setCookie(header, now);
      if (!cookie) continue;
      cookie.hostOnly = cookie.domain === undefined;
      if (cookie.hostOnly) cookie.domain = host;
      else {
        const domain = domainToASCII(cookie.domain.replace(/^\./, '')).toLowerCase();
        if (!domain || domain.endsWith('.') || !domainMatches(host, domain)) continue;
        cookie.domain = domain;
      }
      cookie.path = cookie.path?.startsWith('/') ? cookie.path : defaultPath;
      const key = `${cookie.domain}\0${cookie.path}\0${cookie.name}`;
      if (cookie.expires <= now) this.#cookies.delete(key);
      else {
        cookie.sequence = this.#cookies.get(key)?.sequence ?? this.#sequence++;
        this.#cookies.set(key, cookie);
      }
    }
  }

  header(value) {
    const url = address(value), host = url.hostname.toLowerCase(), now = Date.now();
    const matching = [];
    for (const [key, cookie] of this.#cookies) {
      if (cookie.expires <= now) { this.#cookies.delete(key); continue; }
      if (cookie.hostOnly ? host !== cookie.domain : !domainMatches(host, cookie.domain)) continue;
      if (cookie.secure && url.protocol !== 'https:') continue;
      if (url.pathname !== cookie.path && !(url.pathname.startsWith(cookie.path) &&
          (cookie.path.endsWith('/') || url.pathname[cookie.path.length] === '/'))) continue;
      matching.push(cookie);
    }
    matching.sort((left, right) => right.path.length - left.path.length || left.sequence - right.sequence);
    return matching.map(cookie => `${cookie.name}=${cookie.value}`).join('; ');
  }

  clear() {
    this.#cookies.clear();
    this.#sequence = 0;
  }
}
