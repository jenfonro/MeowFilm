import { isUtf8 } from 'node:buffer';

const imageRoute = '/v1/douban/images/';
const browserUA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36';
const control = /[\u0000-\u0020\u007f\\]/;
const imageFields = new Set(['pic', 'pics', 'cover', 'cover_url', 'avatar', 'avatars', 'avatar_url',
  'image', 'images', 'image_url', 'photo', 'photos', 'poster', 'poster_url', 'icon', 'icon_url',
  'thumbnail', 'thumbnail_url', 'thumb', 'banner', 'background', 'background_image']);
const imageVariants = new Set(['normal', 'large', 'small', 'medium', 'original', 'raw', 'url', 'src',
  's', 'm', 'l', 'sizes']);
const redirects = new Set([301, 302, 303, 307, 308]);

class Failure extends Error {
  constructor(code) { super(code); this.code = code; }
}
const requireValue = (value, code = 'INVALID_REQUEST') => { if (!value) throw new Failure(code); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
function fields(value, allowed, required) {
  requireValue(object(value) && Object.keys(value).every(key => allowed.includes(key)) &&
    required.every(key => Object.hasOwn(value, key)));
}
function httpURL(value, code) {
  requireValue(typeof value === 'string' && /^https?:\/\//i.test(value) &&
    !control.test(value) && !value.includes('#'), code);
  let parsed;
  try { parsed = new URL(value); } catch { throw new Failure(code); }
  requireValue(/^https?:$/.test(parsed.protocol) && parsed.hostname && !parsed.hash, code);
  return parsed;
}
function resourcePath(value, code) {
  // Source image specifications live in path segments, never in a query or authority.
  requireValue(typeof value === 'string' && value.startsWith('/') &&
    value.slice(1).split('/').every(part => part.length > 0 && part !== '.' && part !== '..' &&
      /^[A-Za-z0-9._~-]+$/.test(part) && !control.test(part)), code);
  return value;
}
function baseURL(value) {
  const parsed = httpURL(value, 'INVALID_REQUEST');
  requireValue(!parsed.search && !/[?#]/.test(value));
  return parsed.href.replace(/\/+$/, '');
}
function proxyTemplate(value) {
  requireValue(typeof value === 'string');
  if (value === '') return null;
  const tokens = [...value.matchAll(/\{(?:encodedUrl|url)\}/g)];
  requireValue(tokens.length === 1);
  const token = tokens[0][0], offset = tokens[0].index;
  const before = value.slice(0, offset), after = value.slice(offset + token.length);
  requireValue(!/[{}]/.test(before + after));
  // The template must wrap a target in its path/query, not replace its own authority.
  requireValue(/^https?:\/\/[^/?#{}]+[/?]/.test(before));
  httpURL(value.replace(token, token === '{url}' ? 'https://m.douban.com/' : 'https%3A%2F%2Fm.douban.com%2F'),
    'INVALID_REQUEST');
  return { before, after, encoded: token === '{encodedUrl}' };
}
function applyProxy(url, template) {
  return template ? template.before + (template.encoded ? encodeURIComponent(url) : url) + template.after : url;
}
function unwrapProxy(value, template, code) {
  if (!template || !value.startsWith(template.before) || !value.endsWith(template.after)) return value;
  const inner = value.slice(template.before.length, template.after ? -template.after.length : undefined);
  if (!template.encoded) return inner;
  try { return decodeURIComponent(inner); } catch { throw new Failure(code); }
}
function apiSettings(settings, search) {
  const base = baseURL(search ? settings.searchBase : settings.apiBase);
  const proxy = proxyTemplate(settings.apiProxy);
  requireValue(typeof settings.cookie === 'string' && !/[\u0000-\u001f\u007f]/.test(settings.cookie));
  return { base, proxy, cookie: settings.cookie };
}
function imageSettings(settings) {
  const base = baseURL(settings.imageBase), parsed = new URL(base);
  return { base, origin: parsed.origin, prefix: parsed.pathname.replace(/\/$/, ''),
    proxy: proxyTemplate(settings.imageProxy) };
}
// These aliases identify the same source image path. Other HTTP(S) image URLs
// remain usable as URLs instead of being forced through the configured base.
function sourceImageHost(host) {
  return /^img[0-9]*\.(?:doubanio|douban)\.com$/.test(host) ||
    host === 'img.doubanio.cmliussss.net' || host === 'img.doubanio.cmliussss.com';
}
function imageReference(value, hint, config) {
  if (value === '') return value;
  if (value.startsWith(imageRoute)) {
    resourcePath('/' + value.slice(imageRoute.length), 'UPSTREAM_FORMAT');
    return value;
  }
  const unwrapped = unwrapProxy(value, config.proxy, 'UPSTREAM_FORMAT');
  const raw = unwrapped.startsWith('//') ? 'https:' + unwrapped : unwrapped;
  if (raw.startsWith('/') && !raw.startsWith('//')) {
    if (!hint) return value;
    try { return imageRoute + resourcePath(raw, 'UPSTREAM_FORMAT').slice(1); }
    catch { return applyProxy(httpURL(config.base + raw, 'UPSTREAM_FORMAT').href, config.proxy); }
  }
  let parsed;
  try { parsed = new URL(raw); } catch {
    requireValue(!hint, 'UPSTREAM_FORMAT');
    return value;
  }
  const { origin, prefix } = config;
  const configured = parsed.origin === origin && (prefix === '' || parsed.pathname.startsWith(prefix + '/'));
  const sourceImage = sourceImageHost(parsed.hostname);
  if (!hint && /^(?:movie|m)\.douban\.com$/.test(parsed.hostname) &&
    /^\/(?:subject|movie|tv|celebrity)\/[0-9]+\/?$/.test(parsed.pathname)) return value;
  if (!hint && !configured && !sourceImage) return value;
  requireValue(['http:', 'https:'].includes(parsed.protocol) && !control.test(raw), 'UPSTREAM_FORMAT');
  if (!configured && !sourceImage || parsed.search || parsed.hash || parsed.username || parsed.password) {
    return applyProxy(parsed.href, config.proxy);
  }
  const path = configured ? parsed.pathname.slice(prefix.length) : parsed.pathname;
  try { return imageRoute + resourcePath(path, 'UPSTREAM_FORMAT').slice(1); }
  catch { return applyProxy(parsed.href, config.proxy); }
}

/** A pure projection: source image fields keep their positions; ordinary links keep their values. */
export function rewriteDoubanImages(value, settings) {
  const config = imageSettings(settings);
  function visit(item, hint = false) {
    if (typeof item === 'string') return imageReference(item, hint, config);
    if (Array.isArray(item)) return item.map(child => visit(child, hint));
    if (!object(item)) return item;
    // Photo entities link to a page; their nested image descriptors contain the actual images.
    const photoEntity = item.type === 'photo' && object(item.image);
    return Object.fromEntries(Object.entries(item).map(([key, child]) =>
      [key, photoEntity && key === 'url' && typeof child === 'string' ? child :
        visit(child, imageFields.has(key) || (hint && imageVariants.has(key)))]));
  }
  return visit(value);
}

export function doubanImageTarget(path, settings) {
  requireValue(typeof path === 'string' && path.startsWith(imageRoute));
  const relative = resourcePath('/' + path.slice(imageRoute.length), 'INVALID_REQUEST');
  const config = imageSettings(settings);
  return { url: applyProxy(config.base + relative, config.proxy), headers: {
    referer: 'https://movie.douban.com/', 'user-agent': 'curl/8.5.0', accept: '*/*',
  } };
}

function queryString(query) {
  if (query === undefined) return '';
  requireValue(object(query));
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    requireValue(key.length > 0 && !/[\u0000-\u001f\u007f]/.test(key));
    for (const scalar of Array.isArray(value) ? value : [value]) {
      requireValue(typeof scalar === 'string' || typeof scalar === 'boolean' ||
        (typeof scalar === 'number' && Number.isFinite(scalar)));
      result.append(key, String(scalar));
    }
  }
  return result.size ? '?' + result.toString() : '';
}

// Cookies exist only for this operation. Configured raw cookies have no domain metadata,
// so they start host-only; only an actual Douban Set-Cookie may grant douban.com scope.
function operationCookies(initial, configured) {
  const jar = new Map();
  const keyOf = cookie => [cookie.domain, cookie.path, cookie.name].join('\n');
  const put = cookie => jar.set(keyOf(cookie), cookie);
  if (configured.trim()) {
    for (const part of configured.split(';').filter(part => part.trim())) {
      const match = /^([!#$%&'*+\-.^_`|~0-9A-Za-z]+)=([^;\u0000-\u001f\u007f]*)$/.exec(part.trim());
      requireValue(match);
      put({ name: match[1], value: match[2], domain: initial.hostname, path: '/', hostOnly: true,
        secure: initial.protocol === 'https:', expires: Infinity });
    }
  }
  return {
    receive(url, headers) {
      const values = headers['set-cookie'];
      for (const line of Array.isArray(values) ? values : values ? [values] : []) {
        if (typeof line !== 'string' || /[\r\n\u0000]/.test(line)) continue;
        const [pair, ...attributes] = line.split(';'), equals = pair.indexOf('=');
        if (equals < 1) continue;
        const name = pair.slice(0, equals).trim(), value = pair.slice(equals + 1).trim();
        if (!/^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/.test(name) || /[\u0000-\u001f\u007f]/.test(value)) continue;
        const cookie = { name, value, domain: url.hostname, hostOnly: true, secure: false,
          path: url.pathname.slice(0, url.pathname.lastIndexOf('/')) || '/', expires: Infinity };
        let valid = true, maxAge;
        for (const attribute of attributes) {
          const index = attribute.indexOf('=');
          const attr = (index < 0 ? attribute : attribute.slice(0, index)).trim().toLowerCase();
          const text = index < 0 ? '' : attribute.slice(index + 1).trim();
          if (attr === 'domain') {
            const domain = text.toLowerCase().replace(/^\./, '');
            // Never accept public-suffix or unrelated-domain cookies from relays/redirects.
            if (domain !== url.hostname && !(domain === 'douban.com' && url.hostname.endsWith('.douban.com'))) valid = false;
            cookie.domain = domain; cookie.hostOnly = false;
          } else if (attr === 'path' && text.startsWith('/')) cookie.path = text;
          else if (attr === 'secure') cookie.secure = true;
          else if (attr === 'max-age' && /^-?[0-9]+$/.test(text)) maxAge = Number(text);
          else if (attr === 'expires' && Number.isFinite(Date.parse(text))) cookie.expires = Date.parse(text);
        }
        if (!valid || (cookie.secure && url.protocol !== 'https:')) continue;
        if (maxAge !== undefined) cookie.expires = maxAge <= 0 ? 0 : Date.now() + maxAge * 1000;
        if (cookie.expires <= Date.now()) jar.delete(keyOf(cookie));
        else put(cookie);
      }
    },
    header(url) {
      return [...jar.values()].filter(cookie => cookie.expires > Date.now() &&
        (!cookie.secure || url.protocol === 'https:') &&
        (url.hostname === cookie.domain || (!cookie.hostOnly && url.hostname.endsWith('.' + cookie.domain))) &&
        (url.pathname === cookie.path || (url.pathname.startsWith(cookie.path) &&
          (cookie.path.endsWith('/') || url.pathname[cookie.path.length] === '/'))))
        .sort((a, b) => b.path.length - a.path.length).map(cookie => cookie.name + '=' + cookie.value).join('; ');
    },
  };
}
function interactionScript(script) {
  // Skip quoted business values and comments without evaluating the page script.
  const tokens = /"(?:\\[\s\S]|[^"\\])*"|'(?:\\[\s\S]|[^'\\])*'|`(?:\\[\s\S]|[^`\\])*`|\/\*[\s\S]*?\*\/|\/\/[^\r\n]*|(\b(?:window|top|self)\.location(?:\.href)?\s*=\s*["']https:\/\/(?:sec|accounts)\.douban\.com\/)/gi;
  for (const token of script.matchAll(tokens)) if (token[1]) return true;
  return false;
}
function interactionHTML(body) {
  let redirect = false;
  const html = body.replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<script\b[^>]*>([\s\S]*?)<\/script\s*>/gi, (_tag, script) => {
      redirect ||= interactionScript(script);
      return '';
    });
  return /<(?:form|input|img|div|iframe)\b[^>]*\s(?:id|name)\s*=\s*["']?(?:sec|captcha|captcha_image|captcha-solution|geetest)(?=["'\s>])/i.test(html) ||
    /<input\b[^>]*\stype\s*=\s*["']?password\b/i.test(html) ||
    /<title\b[^>]*>\s*(?:登录豆瓣|豆瓣登录|安全验证|验证码|访问验证|异常请求|需要登录|请先登录|检测到异常请求|请完成安全验证|请完成验证后继续|请输入验证码|captcha|security verification|access denied)(?:\s*[|·\-–—]\s*豆瓣)?\s*<\/title>/i.test(html) ||
    /<form\b[^>]*\saction\s*=\s*["'][^"']*(?:\/login|\/captcha|sec\.douban\.com)/i.test(html) ||
    redirect;
}
function cancelled(signal) { requireValue(!signal?.aborted, 'CANCELLED'); }
async function fetchMetadata(initial, config, request, signal, search) {
  requireValue(typeof request === 'function');
  const cookies = operationCookies(new URL(initial), config.cookie);
  let current = initial;
  for (let step = 0; step <= 5; step++) {
    cancelled(signal);
    const logical = httpURL(current, 'UPSTREAM_FORMAT');
    const headers = search ? {
      'user-agent': browserUA, accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
      'accept-language': 'zh-CN,zh;q=0.9', 'cache-control': 'no-cache', pragma: 'no-cache',
      'upgrade-insecure-requests': '1',
    } : { 'user-agent': 'Mozilla/5.0', accept: 'application/json, text/plain, */*',
      referer: 'https://m.douban.com/', origin: 'https://m.douban.com' };
    const cookie = cookies.header(logical);
    if (cookie) headers.cookie = cookie;
    let response;
    try {
      response = await request(applyProxy(current, config.proxy), { headers, signal, method: 'GET', followRedirect: false });
    } catch (error) {
      throw new Failure(signal?.aborted || error?.name === 'AbortError' || error?.code === 'CANCELLED'
        ? 'CANCELLED' : 'NETWORK_ERROR');
    }
    cancelled(signal);
    requireValue(object(response) && Number.isInteger(response.status) && object(response.headers) &&
      Buffer.isBuffer(response.body) && isUtf8(response.body), 'UPSTREAM_FORMAT');
    const body = response.body.toString('utf8');
    const html = /^\s*</.test(body) || /(?:text\/html|application\/xhtml)/i.test(response.headers['content-type'] ?? '');
    requireValue(!(html && interactionHTML(body)) && response.status !== 401 && response.status !== 418,
      'WEB_INTERACTION_REQUIRED');
    cookies.receive(logical, response.headers);
    if (redirects.has(response.status)) {
      requireValue(step < 5, 'UPSTREAM_HTTP');
      const location = response.headers.location;
      requireValue(typeof location === 'string' && location.length > 0 && !control.test(location), 'UPSTREAM_FORMAT');
      let next;
      try { next = new URL(unwrapProxy(location, config.proxy, 'UPSTREAM_FORMAT'), logical); }
      catch { throw new Failure('UPSTREAM_FORMAT'); }
      current = httpURL(next.href, 'UPSTREAM_FORMAT').href;
      continue;
    }
    if (response.status < 200 || response.status >= 300) {
      let payload;
      try { payload = JSON.parse(body); } catch { /* Non-JSON failures still use the HTTP category. */ }
      requireValue(!object(payload) || !requiresInteraction(payload), 'WEB_INTERACTION_REQUIRED');
      throw new Failure('UPSTREAM_HTTP');
    }
    return body;
  }
}
function businessCode(payload) {
  return typeof payload.code === 'number' ? payload.code :
    typeof payload.code === 'string' && /^-?[0-9]+$/.test(payload.code.trim()) ? Number(payload.code) : 0;
}
function requiresInteraction(payload) {
  const message = [payload.msg, payload.localized_message, payload.error].filter(value => typeof value === 'string').join(' ');
  const code = businessCode(payload);
  return code === 103 || code === 401 ||
    /(?:need[_ ]login|login[_ ]required|captcha|verify[_ ]required|verification[_ ]required|需要登录|请先登录|验证码|安全验证)/i.test(message);
}
function businessError(payload) {
  requireValue(!requiresInteraction(payload), 'WEB_INTERACTION_REQUIRED');
  const msg = typeof payload.msg === 'string' ? payload.msg.trim() : '';
  requireValue(!businessCode(payload) && !payload.error && !/^(?:permission_denied|forbidden|rate_limit(?:ed)?|invalid_request)$/i.test(msg),
    'UPSTREAM_HTTP');
}

export async function requestDouban(input, { settings, request, signal }) {
  fields(input, ['path', 'query'], ['path']);
  requireValue(typeof input.path === 'string' && input.path.startsWith('/') && !input.path.startsWith('//') &&
    !control.test(input.path) && !/[?#]/.test(input.path));
  const query = queryString(input.query), config = apiSettings(settings, false);
  const body = await fetchMetadata(config.base + input.path + query, config, request, signal, false);
  let payload;
  try { payload = JSON.parse(body); } catch { throw new Failure('UPSTREAM_FORMAT'); }
  requireValue(object(payload), 'UPSTREAM_FORMAT');
  businessError(payload);
  return rewriteDoubanImages(payload, settings);
}

function searchData(html) {
  for (const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script\s*>/gi)) {
    const script = match[1], assignment = /(?:^|[;\r\n])\s*window\.__DATA__\s*=\s*/.exec(script);
    if (!assignment) continue;
    const start = assignment.index + assignment[0].length;
    requireValue(script[start] === '{', 'UPSTREAM_FORMAT');
    let depth = 0, quoted = false, escaped = false;
    for (let index = start; index < script.length; index++) {
      const char = script[index];
      if (quoted) {
        if (escaped) escaped = false;
        else if (char === '\\') escaped = true;
        else if (char === '"') quoted = false;
      } else if (char === '"') quoted = true;
      else if (char === '{' || char === '[') depth++;
      else if (char === '}' || char === ']') {
        if (--depth !== 0) continue;
        requireValue(/^\s*;/.test(script.slice(index + 1)), 'UPSTREAM_FORMAT');
        try { return JSON.parse(script.slice(start, index + 1)); }
        catch { throw new Failure('UPSTREAM_FORMAT'); }
      }
    }
    throw new Failure('UPSTREAM_FORMAT');
  }
  throw new Failure('UPSTREAM_FORMAT');
}
function optionalText(value) {
  requireValue(value === undefined || value === null || typeof value === 'string', 'UPSTREAM_FORMAT');
  return value ?? '';
}
function numeric(value, integer = false) {
  if (value === undefined || value === null) return 0;
  requireValue(typeof value === 'number' && Number.isFinite(value) && value >= 0 &&
    (!integer || Number.isSafeInteger(value)), 'UPSTREAM_FORMAT');
  return value;
}
function searchPayload(payload, query) {
  requireValue(object(payload), 'UPSTREAM_FORMAT');
  businessError(payload);
  requireValue(Array.isArray(payload.items), 'UPSTREAM_FORMAT');
  const total = numeric(payload.total, true);
  numeric(payload.count, true); numeric(payload.start, true);
  const items = payload.items.map(item => {
    requireValue(object(item) && (Number.isSafeInteger(item.id) && item.id > 0 ||
      typeof item.id === 'string' && /^[1-9][0-9]*$/.test(item.id) && !control.test(item.id)) &&
      typeof item.title === 'string', 'UPSTREAM_FORMAT');
    const id = String(item.id), labels = item.labels ?? [];
    requireValue(Array.isArray(labels) && (item.rating === undefined || item.rating === null || object(item.rating)), 'UPSTREAM_FORMAT');
    let kind = '', canPlay = false;
    for (const label of labels) {
      requireValue(object(label), 'UPSTREAM_FORMAT');
      const text = optionalText(label.text).trim();
      if (!kind && (text === '电影' || text === '剧集')) kind = text === '电影' ? 'movie' : 'tv';
      if (text === '可播放') canPlay = true;
    }
    if (!kind && optionalText(item.url).includes('/subject/')) kind = 'movie';
    // Unicode 15.0 Cf, also supported by NodeMobile's V8 without ICU.
    const title = item.title.replace(/[\u00ad\u0600-\u0605\u061c\u06dd\u070f\u0890-\u0891\u08e2\u180e\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u206f\ufeff\ufff9-\ufffb\u{110bd}\u{110cd}\u{13430}-\u{1343f}\u{1bca0}-\u{1bca3}\u{1d173}-\u{1d17a}\u{e0001}\u{e0020}-\u{e007f}]/gu, '').trim();
    const rating = item.rating ?? {};
    const target = {
      rating: { count: numeric(rating.count, true), max: 10, star_count: numeric(rating.star_count), value: numeric(rating.value) },
      controversy_reason: '', title: title.replace(/\s*[（(](?:19|20)[0-9]{2}[)）]\s*$/, ''),
      abstract: '', has_linewatch: canPlay, uri: 'douban://douban.com/' + kind + '/' + id,
      year: /\(((?:19|20)[0-9]{2})\)/.exec(title)?.[1] ?? '0',
      card_subtitle: optionalText(item.abstract).trim(), id,
      null_rating_reason: optionalText(rating.rating_info).trim(),
    };
    if (Object.hasOwn(item, 'cover_url')) {
      requireValue(item.cover_url === null || typeof item.cover_url === 'string', 'UPSTREAM_FORMAT');
      target.cover_url = item.cover_url === null ? null : item.cover_url.trim();
    }
    return { layout: 'subject', type_name: kind === 'tv' ? '电视剧' : '电影',
      target_id: id, target_type: kind, target };
  });
  return { banned: '', fuzzy: '', promotion: [], search_egg: null, smart_box: [],
    contents: { count: 0, start: 0, total: 0, items: [] },
    subjects: { target_name: '更多热门“' + (optionalText(payload.text).trim() || query) + '”书影音',
      show_more_subjects: total > items.length, items } };
}

export async function searchDouban(input, { settings, request, signal }) {
  fields(input, ['query'], ['query']);
  requireValue(typeof input.query === 'string' && input.query.trim().length > 0 &&
    !/[\u0000-\u001f\u007f]/.test(input.query));
  const query = input.query.trim(), config = apiSettings(settings, true);
  const params = new URLSearchParams({ search_text: query, cat: '1002' });
  const body = await fetchMetadata(config.base + '/movie/subject_search?' + params, config, request, signal, true);
  return rewriteDoubanImages(searchPayload(searchData(body), query), settings);
}
