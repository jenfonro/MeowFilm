import { createHash } from 'node:crypto';
import { clone, fault, requireValue } from './errors.js';

export const extensionKey = '_meow';
export const ownedExtension = Symbol('meow.cat.extension');
const names = { baidu: '百度', quark: '夸克', uc: 'UC', tianyi: '天翼', yidong: '移动' };
const hosts = { baidu: ['pan.baidu.com'], quark: ['pan.quark.cn', 'drive.quark.cn', 'drive-pc.quark.cn'],
  uc: ['drive.uc.cn', 'pc-api.uc.cn', 'fast.uc.cn'], tianyi: ['cloud.189.cn', 'h5.cloud.189.cn'],
  yidong: ['caiyun.139.com', 'yun.139.com'] };
const defaults = { baidu: ['百度', '百度网盘', 'baidu'], quark: ['夸克', '夸父', 'quark'],
  uc: ['UC', 'UC网盘', '优夕'], tianyi: ['天翼', '天意', 'tianyi', '189'],
  yidong: ['移动', '逸动', '和彩云', '云空间', '139'] };
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
      (url.pathname === '/m/i' ? url.search.slice(1).split('&')[0] : '') : null;
  if (!id || !/^[A-Za-z0-9_-]{4,256}$/.test(id) || /^(?:root\d*|share|nopass)$/i.test(id)) return null;
  const hashQuery = new URLSearchParams(url.hash.includes('?') ? url.hash.slice(url.hash.indexOf('?') + 1) : '');
  password ||= q.get('pwd') || q.get('passcode') || q.get('accessCode') || q.get('passwd') ||
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
export function panProvider(flag, settings) {
  const label = String(flag).split('-')[0].trim().toLowerCase();
  const configured = settings.mappings.find(item => item.aliases.some(alias => alias.trim().toLowerCase() === label));
  if (configured) return configured.provider;
  return Object.keys(defaults).find(provider =>
    [...defaults[provider], ...settings.mappings.filter(item => item.provider === provider)
      .flatMap(item => item.aliases)].some(alias => alias.toLowerCase() === label));
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
export function normalizeCatOutput(input, { shares = [], settings, installationId, siteId, mapUrl }) {
  let result = clone(input);
  if (typeof result === 'string') {
    try { result = JSON.parse(result); } catch { return result; }
  }
  if (!result || typeof result !== 'object' || Array.isArray(result)) return result;
  const canExtend = !Object.hasOwn(result, extensionKey);
  const capturedShares = canExtend ? shares.map(share =>
    ({ share, marker: 'meow_' + shareIdentity(share) })) : [];
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
    if (typeof item.vod_play_from !== 'string' || typeof item.vod_play_url !== 'string') continue;
    const flags = item.vod_play_from.split('$$$'), lines = item.vod_play_url.split('$$$');
    for (let lineIndex = 0; lineIndex < Math.min(flags.length, lines.length); lineIndex++) {
      const namedProvider = panProvider(flags[lineIndex], settings);
      const parts = lines[lineIndex].split('#');
      let changed = false;
      const rewritten = parts.map((part, episodeIndex) => {
        const separator = part.indexOf('$');
        const raw = separator >= 0 ? part.slice(separator + 1) : part;
        const local = map(raw);
        if (!canExtend) return separator >= 0 ? part.slice(0, separator + 1) + local : local;
        let matching = [];
        if (capturedShares.length) {
          let expanded = raw;
          try {
            const decoded = decodeURIComponent(raw);
            expanded = decoded + Buffer.from(decoded, 'base64').toString();
          } catch {}
          matching = capturedShares.filter(item => expanded.includes(item.marker));
        }
        let share = matching.length === 1 ? matching[0].share : null;
        if (!matching.length && namedProvider && settings.providers[namedProvider]) {
          share = parseShare(raw);
          if (share?.provider !== namedProvider) share = null;
        }
        if (!share || !settings.providers[share.provider])
          return separator >= 0 ? part.slice(0, separator + 1) + local : local;
        const provider = share.provider;
        const id = encodePanResource({ ...share, installationId, siteId });
        resources.push({ kind: 'pan', provider, installationId, siteId, vodIndex, lineIndex,
          episodeIndex, id });
        changed = true;
        const label = separator >= 0 ? part.slice(0, separator) : `${names[provider]}-${share.shareId}`;
        return `${label}$${id}`;
      });
      if (changed || rewritten.some((value, index) => value !== parts[index])) lines[lineIndex] = rewritten.join('#');
    }
    item.vod_play_url = lines.join('$$$');
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
