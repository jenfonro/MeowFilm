import { createHash } from 'node:crypto';
import { fault, requireValue } from './errors.js';
import { parseJSONText, cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { panReferenceKeys } from './pan-references.js';

export const extensionKey = '_meow';
export const ownedExtension = Symbol('meow.cat.extension');
const names = { baidu: '百度', quark: '夸克', uc: 'UC', tianyi: '天翼', yidong: '移动' };
export const panProviders = Object.freeze(Object.keys(names));
const hosts = { baidu: ['pan.baidu.com'], quark: ['pan.quark.cn', 'drive.quark.cn', 'drive-pc.quark.cn'],
  uc: ['drive.uc.cn', 'pc-api.uc.cn', 'fast.uc.cn', 'open-api-drive.uc.cn'],
  tianyi: ['cloud.189.cn', 'h5.cloud.189.cn', 'content.21cn.com', 'api.cloud.21cn.com'],
  yidong: ['caiyun.139.com', 'yun.139.com'] };
const defaults = { baidu: ['百度', '百度网盘', 'baidu'], quark: ['夸克', '夸父', 'quark'],
  uc: ['UC', 'UC网盘', '优夕'], tianyi: ['天翼', '天意', 'tianyi', '189'],
  yidong: ['移动', '逸动', '和彩云', '云空间', 'caiyun', 'yun139', '139'] };
export function providerForHost(host) {
  return Object.keys(hosts).find(provider => hosts[provider].some(value =>
    host === value || host.endsWith('.' + value))) ?? null;
}
export function parseShare(value, password = '') {
  let url;
  const annotated = /^(https?:\/\/[^\s（）()]+)\s*[（(]\s*(?:提取码|访问码|密码)\s*[:：]\s*([A-Za-z0-9]{1,16})\s*[）)]$/i.exec(String(value));
  try { url = new URL(annotated ? annotated[1] : value); } catch { return null; }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) return null;
  const provider = providerForHost(url.hostname);
  const q = url.searchParams;
  let id = provider === 'baidu' ? /^\/s\/1([\w-]+)\/?$/.exec(url.pathname)?.[1] || q.get('surl')
    : ['quark', 'uc'].includes(provider) ? /^\/s\/([\w-]+)\/?$/.exec(url.pathname)?.[1]
    : provider === 'tianyi' ? /^\/t\/([\w-]+)\/?$/.exec(url.pathname)?.[1] || q.get('shareCode') || q.get('code')
    : provider === 'yidong' ? q.get('linkID') || q.get('linkId') ||
      (/\/(?:m|w)\/i\/([\w-]+)/.exec(url.pathname + url.hash)?.[1]) ||
      (/^\/m\/i\/?$/.test(url.pathname) ? url.search.slice(1).split('&')[0] : '') : null;
  if (!id || !/^[A-Za-z0-9_-]{4,256}$/.test(id) || /^(?:root\d*|share|nopass)$/i.test(id)) return null;
  const hashQuery = new URLSearchParams(url.hash.includes('?') ? url.hash.slice(url.hash.indexOf('?') + 1) : '');
  password ||= q.get('pwd') || q.get('passcode') || q.get('accessCode') || q.get('password') || q.get('passwd') ||
    hashQuery.get('pwd') || hashQuery.get('passwd') || annotated?.[2] || '';
  const bases = { baidu: 'https://pan.baidu.com/s/1', quark: 'https://pan.quark.cn/s/',
    uc: 'https://drive.uc.cn/s/', tianyi: 'https://cloud.189.cn/t/', yidong: 'https://caiyun.139.com/m/i?' };
  return { provider, shareId: id, url: bases[provider] + id, password };
}
export function encodePanResource(share) {
  return 'meow.pan.v1.' + Buffer.from(JSON.stringify(share)).toString('base64url');
}
export function decodePanResource(value) {
  if (typeof value !== 'string' || !value.startsWith('meow.pan.v1.')) return null;
  try {
    const share = JSON.parse(Buffer.from(value.slice(12), 'base64url').toString());
    const verified = parseShare(share.url, share.password);
    requireValue(verified && verified.provider === share.provider && verified.shareId === share.shareId);
    requireValue(typeof share.installationId === 'string' && typeof share.siteId === 'string');
    return { ...verified, installationId: share.installationId, siteId: share.siteId };
  } catch { throw fault('INVALID_ARGUMENT'); }
}
export function providerEnabled(settings, provider) {
  const value = settings.providers?.[provider];
  requireValue(value === undefined || typeof value === 'boolean', 'INVALID_ARGUMENT');
  return value === true;
}
export function panProvider(flag, settings) {
  const label = String(flag).trim().toLowerCase();
  requireValue(Array.isArray(settings.mappings), 'INVALID_ARGUMENT');
  const matches = alias => alias.trim().length > 0 && label.includes(alias.trim().toLowerCase());
  const configured = settings.mappings.find(item => item.aliases.some(matches));
  if (configured) {
    requireValue(Object.hasOwn(defaults, configured.provider), 'INVALID_ARGUMENT');
    return configured.provider;
  }
  return Object.keys(defaults).find(provider =>
    [...defaults[provider], ...settings.mappings.filter(item => item.provider === provider)
      .flatMap(item => item.aliases)].some(matches));
}
function metadata(record) {
  const source = record.video_meta && typeof record.video_meta === 'object' ? { ...record, ...record.video_meta } : record;
  const number = value => typeof value === 'number' || typeof value === 'string' ? Number(value) : NaN;
  const width = number(source.video_width ?? source.width ?? source.vwidth), height = number(source.video_height ?? source.height ?? source.vheight);
  const dimensions = Number.isSafeInteger(width) && width > 0 && Number.isSafeInteger(height) && height > 0;
  let resolution = typeof record.resolution === 'string' ? record.resolution : '';
  if (dimensions) resolution = `${width}x${height}`;
  const rawFPS = source.fps ?? source.frameRate ?? source.frame_rate;
  const rational = /^(\d+)\/(\d+)$/.exec(String(rawFPS));
  const fps = rational ? Number(rational[1]) / Number(rational[2]) : number(rawFPS);
  const short = dimensions ? Math.min(width, height) : 0;
  const quality = short >= 2160 ? '4K' : short >= 1440 ? '2K' : short >= 1080 ? '1080P'
    : short >= 720 ? '720P' : short > 0 ? `${short}P` : '';
  return { ...(resolution ? { resolution } : {}), ...(quality ? { quality } : {}),
    ...(fps > 0 && fps < 1000 ? { fps } : {}),
    ...(typeof record.file_name === 'string' ? { filename: record.file_name } : {}) };
}
export function addExtension(output, value) {
  requireValue(output && typeof output === 'object' && !Array.isArray(output), 'SCRIPT_PROTOCOL_ERROR');
  // Enhancement must never destroy a valid Cat result or overwrite a guest
  // extension. A reserved-field collision leaves that result unchanged.
  if (Object.hasOwn(output, extensionKey)) return output;
  const result = { ...output, [extensionKey]: { version: 1, ...value } };
  Object.defineProperty(result, ownedExtension, { value: true });
  return result;
}
export function mergeExtension(output, value) {
  if (!output?.[ownedExtension]) return output;
  const result = { ...output, [extensionKey]: { ...output[extensionKey], ...value } };
  Object.defineProperty(result, ownedExtension, { value: true });
  return result;
}
export function normalizeCatOutput(input, { shares = [], settings, installationId, siteId, mapUrl, operation,
  resolveReference }) {
  let result = cloneJSONValue(input);
  if (typeof result === 'string') {
    try { result = parseJSONText(result); } catch { return result; }
  }
  if (!result || typeof result !== 'object' || Array.isArray(result)) return result;
  const canExtend = !Object.hasOwn(result, extensionKey);
  const capturedShares = shares.filter(share => providerEnabled(settings, share.provider));
  if (!canExtend && capturedShares.length) throw fault('PAN_INTERCEPTION_UNSUPPORTED');
  const references = new Map(shares.map(share => ['meow_' + shareIdentity(share), share]));
  const reference = resolveReference ?? ((value, label) => {
    const lookup = token => references.has(token) ? new Set([token]) : undefined;
    const raw = panReferenceKeys(value, lookup), title = panReferenceKeys(label, lookup, { scalar: false });
    const keys = new Set([...raw.keys, ...title.keys]);
    if (raw.unknown || title.unknown || keys.size > 1) throw fault('PAN_INTERCEPTION_UNSUPPORTED');
    return references.get([...keys][0]) ?? null;
  });
  const resources = [], media = [];
  let routed = false;
  const map = value => {
    if (typeof value !== 'string') return value;
    let next;
    try { next = mapUrl?.(value) ?? value; }
    catch { return value; }
    if (next !== value) routed = true;
    return next;
  };
  const rewriteShape = (value, nested = false) => {
    if (typeof value === 'string') return map(value);
    if (Array.isArray(value)) return value.map(item => rewriteShape(item, nested));
    if (!value || typeof value !== 'object') return value;
    return Object.fromEntries(Object.entries(value).map(([key, item]) => {
      if (['header', 'headers', 'cookie', 'cookies'].includes(key.toLowerCase())) return [key, item];
      const urlField = /(?:^|_)(?:url|urls|src|source|proxy|subtitle|subtitles|danmu|danmaku|m3u8|mpd)$/i.test(key) ||
        /(?:Url|URL|Urls|URLs)$/.test(key);
      return [key, nested || urlField ? rewriteShape(item, true) : item];
    }));
  };
  for (const [vodIndex, item] of (Array.isArray(result.list) ? result.list : []).entries()) {
    if (!item || typeof item !== 'object') continue;
    const info = metadata(item);
    if (Object.keys(info).length) media.push({ vodIndex, ...info });
    for (const field of ['vod_pic', 'pic', 'poster', 'logo']) {
      if (typeof item[field] === 'string') item[field] = map(item[field]);
    }
    const captureOnly = operation === 'detail' && result.list.length === 1 && capturedShares.length &&
      item.vod_play_from === undefined && item.vod_play_url === undefined;
    if (!captureOnly && (typeof item.vod_play_from !== 'string' || typeof item.vod_play_url !== 'string')) continue;
    const flags = (item.vod_play_from ?? '').split('$$$'), lines = (item.vod_play_url ?? '').split('$$$');
    if (flags.length !== lines.length) continue;
    const normalized = [], seen = new Map(), opaqueProviders = new Set();
    let opaque = false;
    const appendShare = share => {
      const key = shareIdentity(share);
      const previous = seen.get(key);
      if (previous && (previous.share.password || !share.password)) return;
      const id = encodePanResource({ ...share, installationId, siteId });
      const label = `${names[share.provider]}-${share.shareId}`;
      if (previous) {
        previous.share = share;
        previous.resource.id = id;
        normalized[previous.resource.lineIndex].line = `${label}$${id}`;
      } else {
        const resource = { kind: 'pan', provider: share.provider, installationId, siteId, vodIndex,
          lineIndex: normalized.length, episodeIndex: 0, id };
        seen.set(key, { share, resource });
        resources.push(resource);
        normalized.push({ flag: label, line: `${label}$${id}` });
      }
    };
    for (let lineIndex = 0; lineIndex < Math.min(flags.length, lines.length); lineIndex++) {
      const namedProvider = panProvider(flags[lineIndex], settings);
      const parts = lines[lineIndex] === '' ? [] : lines[lineIndex].split(/#(?!\/(?:m|w)\/i\/)/);
      const remaining = [];
      for (const part of parts) {
        const separator = part.indexOf('$');
        const raw = separator >= 0 ? part.slice(separator + 1) : part;
        const local = map(raw);
        const original = separator >= 0 ? part.slice(0, separator + 1) + local : local;
        const issued = reference(raw, separator >= 0 ? part.slice(0, separator) : undefined);
        if (issued && (!canExtend || !providerEnabled(settings, issued.provider)))
          throw fault('PAN_INTERCEPTION_UNSUPPORTED');
        if (!canExtend) { remaining.push(original); continue; }
        let share = issued ?? parseShare(raw);
        if (share) share = capturedShares.find(item => shareIdentity(item) === shareIdentity(share)) ?? share;
        if (share && providerEnabled(settings, share.provider)) {
          appendShare(share);
        } else {
          let targetProvider;
          try { targetProvider = providerForHost(new URL(raw).hostname.toLowerCase().replace(/\.$/, '')); } catch {}
          if (targetProvider && providerEnabled(settings, targetProvider)) opaqueProviders.add(targetProvider);
          if (!/^https?:\/\//i.test(raw)) {
            opaque = true;
            if (namedProvider && providerEnabled(settings, namedProvider)) opaqueProviders.add(namedProvider);
          }
          remaining.push(original);
        }
      }
      if (remaining.length || !parts.length && (flags[lineIndex] || !capturedShares.length))
        normalized.push({ flag: flags[lineIndex], line: remaining.join('#') });
    }
    if ([...opaqueProviders].some(provider => capturedShares.some(share => share.provider === provider) ||
      [...seen.values()].some(item => item.share.provider === provider)))
      throw fault('PAN_INTERCEPTION_UNSUPPORTED');
    // Invocation captures belong to a detail, never to every item of a batch.
    if (result.list.length === 1 && (operation === 'detail' || operation === undefined)) {
      if (opaque && capturedShares.some(share => !seen.has(shareIdentity(share))))
        throw fault('PAN_INTERCEPTION_UNSUPPORTED');
      for (const share of capturedShares) appendShare(share);
    }
    item.vod_play_from = normalized.map(row => row.flag).join('$$$');
    item.vod_play_url = normalized.map(row => row.line).join('$$$');
  }
  for (const field of ['url', 'urls', 'playUrl', 'play_url', 'proxy', 'danmuSearchUrl',
    'danmu', 'danmaku', 'subtitle', 'subtitles']) {
    if (Object.hasOwn(result, field)) result[field] = rewriteShape(result[field], true);
  }
  const playMeta = metadata(result);
  if (Object.keys(playMeta).length) media.push(playMeta);
  if (!canExtend) return result;
  if (!resources.length && !media.length && !shares.length && !routed) return result;
  return addExtension(result, { resources, media,
    ...(routed ? { routes: [{ kind: 'script-proxy', installationId, siteId }] } : {}),
    ...(shares.length ? { captures: shares.map(share => ({
    kind: 'pan', provider: share.provider, installationId, siteId,
    id: encodePanResource({ ...share, installationId, siteId }),
  })) } : {}) });
}
export function parseM3U8Metadata(text, base) {
  const variants = [], lines = text.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i].startsWith('#EXT-X-STREAM-INF:')) continue;
    const resolution = /(?:^|,)RESOLUTION=(\d+)x(\d+)/.exec(lines[i].slice(18));
    const rate = /(?:^|,)FRAME-RATE=([\d.]+)/.exec(lines[i].slice(18));
    const uri = lines.slice(i + 1).find(line => line.trim() && !line.trimStart().startsWith('#'));
    if (!uri) continue;
    variants.push({ ...metadata({ width: resolution?.[1], height: resolution?.[2], fps: rate?.[1] }),
      url: new URL(uri.trim(), base).href });
  }
  return variants;
}
export const shareIdentity = share => createHash('sha256')
  .update(share.provider + ':' + share.shareId).digest('hex').slice(0, 24);
