import { createCipheriv, createDecipheriv, createHash, randomBytes } from 'node:crypto';
import { PanError, requireValue } from '../errors.js';
import { object, parseJSON, text } from '../values.js';

const outlinkBase = 'https://share-kd-njs.yun.139.com/yun-share/richlifeApp/devapp/IOutLink/';
const refreshURL = 'https://aas.caiyun.feixin.10086.cn:443/tellin/authTokenRefresh.do';
const outlinkKey = Buffer.from('PVGDwmcvfs1uV3d1');
const userAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36';
const shareDevice = '||3|12.27.0|chrome|131.0.0.0|5c7c68368f048245e1ce47f1c0f8f2d0||windows 10|1536X695|zh-CN|||';
const accountDevice = '||9|12.27.0|chrome|143.0.0.0|pda50460feabd10141fb59a3ba787afb||windows 10|1624X1305|zh-CN|||';
const pageSize = 200;
const refreshPeriod = 12 * 60 * 60 * 1000;
const shareID = /^[A-Za-z0-9_-]+$/;
const videoName = /\.(?:mp4|mkv|mov|iso|flv)$/i;

function parseShare(share) {
  requireValue(object(share) && typeof share.url === 'string');
  requireValue(share.password === undefined || typeof share.password === 'string');
  let url;
  try { url = new URL(share.url); } catch { throw new PanError('INVALID_REQUEST'); }
  requireValue(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password &&
    ['caiyun.139.com', 'yun.139.com'].includes(url.hostname));
  const query = url.searchParams;
  const hashQuery = new URLSearchParams(url.hash.includes('?') ? url.hash.slice(url.hash.indexOf('?') + 1) : '');
  const id = query.get('linkID') || query.get('linkId') ||
    /\/(?:w|m)\/i\/([A-Za-z0-9_-]+)(?:[/?#]|$)/.exec(url.pathname + url.hash)?.[1] ||
    (/^\/m\/i\/?$/.test(url.pathname) ? url.search.slice(1).split('&')[0] : '');
  requireValue(id && shareID.test(id));
  const password = share.password ?? (query.get('pwd') || query.get('passcode') || query.get('accessCode') ||
    query.get('passwd') || hashQuery.get('pwd') || hashQuery.get('passwd') || '');
  return { id, url: `https://caiyun.139.com/w/i/${id}`, password };
}

function base64Bytes(value, code) {
  const encoded = value.replace(/\s+/g, '').replace(/-/g, '+').replace(/_/g, '/');
  requireValue(/^[A-Za-z0-9+/]+={0,2}$/.test(encoded) && encoded.length % 4 !== 1, code);
  return Buffer.from(encoded, 'base64');
}

function authorizationParts(value) {
  requireValue(typeof value === 'string' && value.trim(), 'NOT_CONFIGURED');
  const raw = value.trim().replace(/^Basic\s+/i, '');
  let decoded = raw;
  if (!raw.includes(':')) decoded = base64Bytes(raw, 'AUTH_FAILED').toString('utf8');
  const match = /^([^:]+):([^:]+):([\s\S]+)$/.exec(decoded);
  requireValue(match && match[1].trim() && match[2].trim() && match[3].trim(), 'AUTH_FAILED');
  const [prefix, account, token] = match.slice(1).map(part => part.trim());
  return { prefix, account, token, authorization: Buffer.from(`${prefix}:${account}:${token}`).toString('base64') };
}

function encrypt(plain) {
  const iv = randomBytes(16);
  const cipher = createCipheriv('aes-128-cbc', outlinkKey, iv);
  return JSON.stringify(Buffer.concat([iv, cipher.update(plain, 'utf8'), cipher.final()]).toString('base64'));
}

function parseResponse(body, encrypted) {
  try {
    let raw = body.toString('utf8').trim();
    if (encrypted && !raw.startsWith('{')) {
      const encoded = raw.startsWith('"') ? parseJSON(raw) : raw;
      requireValue(typeof encoded === 'string', 'UPSTREAM_FORMAT');
      const bytes = base64Bytes(encoded, 'UPSTREAM_FORMAT');
      requireValue(bytes.length >= 32 && bytes.length % 16 === 0, 'UPSTREAM_FORMAT');
      const decipher = createDecipheriv('aes-128-cbc', outlinkKey, bytes.subarray(0, 16));
      raw = Buffer.concat([decipher.update(bytes.subarray(16)), decipher.final()]).toString('utf8');
    }
    const value = parseJSON(raw);
    requireValue(object(value), 'UPSTREAM_FORMAT');
    return value;
  } catch { throw new PanError('UPSTREAM_FORMAT'); }
}

function assertHTTP(status) {
  if (status === 401 || status === 403) throw new PanError('AUTH_REQUIRED');
  requireValue(status >= 200 && status < 300, 'UPSTREAM_HTTP');
}

function resultCode(value) {
  return text(value.code ?? value.resultCode ?? value.result_code).trim();
}

function assertResult(value, password) {
  const code = resultCode(value);
  if (!code || ['0', '200', 'success'].includes(code.toLowerCase())) return;
  if (code === '9188') throw new PanError(password ? 'AUTH_FAILED' : 'AUTH_REQUIRED');
  throw new PanError('UPSTREAM_ERROR');
}

function anonymousHeaders(id) {
  return {
    'User-Agent': userAgent, Accept: 'application/json, text/plain, */*', 'Content-Type': 'application/json',
    'hcy-cool-flag': '1', 'x-deviceinfo': shareDevice, Referer: `https://caiyun.139.com/w/i/${id}`,
  };
}

function md5(value) {
  return createHash('md5').update(value).digest('hex');
}

function signedHeaders(auth, plain, now) {
  const timestamp = new Date(now + 8 * 60 * 60 * 1000).toISOString().slice(0, 19).replace('T', ' ');
  const alphabet = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
  const random = Array.from(randomBytes(16), value => alphabet[value % alphabet.length]).join('');
  const sorted = encodeURIComponent(plain).split('').sort().join('');
  const signature = md5(md5(Buffer.from(sorted).toString('base64')) + md5(`${timestamp}:${random}`)).toUpperCase();
  return {
    Accept: 'application/json, text/plain, */*', 'Accept-Encoding': 'gzip, deflate',
    'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8', Authorization: `Basic ${auth.authorization}`,
    'Content-Type': 'application/json;charset=UTF-8', 'Hcy-Cool-Flag': '1',
    'Mcloud-Sign': `${timestamp},${random},${signature}`, Origin: 'https://yun.139.com',
    Referer: 'https://yun.139.com/', 'User-Agent': userAgent, 'X-Deviceinfo': accountDevice,
    'Accept-Charset': 'utf-8', 'X-Requested-With': 'XMLHttpRequest',
  };
}

async function post(ctx, endpoint, payload, { encrypted = false, auth, id } = {}) {
  const plain = JSON.stringify(payload);
  const headers = auth ? signedHeaders(auth, plain, ctx.now()) : id ? anonymousHeaders(id) : {
    'User-Agent': userAgent, Accept: 'application/json, text/plain, */*',
    'Content-Type': 'application/json', 'Accept-Encoding': 'identity',
  };
  const response = await ctx.http.request({
    url: outlinkBase + endpoint, method: 'POST', headers, body: encrypted ? encrypt(plain) : plain,
    signal: ctx.signal, redirect: 'manual',
  });
  assertHTTP(response.status);
  return parseResponse(response.body, encrypted);
}

function listArray(data, key) {
  const value = data[key];
  if (value == null) return [];
  if (Array.isArray(value)) return value;
  requireValue(object(value) && Object.hasOwn(value, 'item') && (value.item == null || Array.isArray(value.item)), 'UPSTREAM_FORMAT');
  return value.item ?? [];
}

function itemID(value) {
  const id = text(value);
  requireValue(id.length > 0, 'UPSTREAM_FORMAT');
  return id;
}

function fileItem(raw) {
  requireValue(object(raw) && typeof raw.coName === 'string' && raw.coName.length > 0, 'UPSTREAM_FORMAT');
  const coID = itemID(raw.coID ?? raw.coId);
  const type = text(raw.coType);
  requireValue(type.length > 0, 'UPSTREAM_FORMAT');
  const contentId = raw.contentId ?? raw.contentID;
  return { ...raw, coID, ...(contentId == null ? {} : { contentId: itemID(contentId) }) };
}

async function* directoryPages(ctx, share, parentId) {
  const directories = new Set(), files = new Set();
  for (let page = 1; ; page += 1) {
    const value = await post(ctx, 'getOutLinkInfoV6', {
      getOutLinkInfoReq: {
        account: '', linkID: share.id, passwd: share.password, caSrt: 0, coSrt: 0, srtDr: 1,
        bNum: page, pCaID: parentId, eNum: pageSize,
      },
      commonAccountInfo: { account: '', accountType: 1 },
    }, { encrypted: true, id: share.id });
    assertResult(value, share.password);
    const data = value.data;
    requireValue(object(data) && (Object.hasOwn(data, 'caLst') || Object.hasOwn(data, 'coLst')), 'UPSTREAM_FORMAT');
    const ca = listArray(data, 'caLst'), co = listArray(data, 'coLst');
    const nextDirectories = [], nextFiles = [];
    for (const raw of ca) {
      requireValue(object(raw) && typeof raw.caName === 'string' && raw.caName.length > 0, 'UPSTREAM_FORMAT');
      const id = itemID(raw.path);
      if (directories.has(id)) continue;
      directories.add(id);
      nextDirectories.push({ id, name: raw.caName });
    }
    for (const raw of co) {
      const item = fileItem(raw);
      if (files.has(item.coID)) continue;
      files.add(item.coID);
      nextFiles.push(item);
    }
    const hasNext = ca.length >= pageSize || co.length >= pageSize;
    requireValue(!hasNext || nextDirectories.length + nextFiles.length > 0, 'UPSTREAM_FORMAT');
    yield { directories: nextDirectories, files: nextFiles };
    if (!hasNext) return;
  }
}

function fileSize(value) {
  if (value == null) return undefined;
  if (typeof value === 'number') {
    requireValue(Number.isSafeInteger(value) && value >= 0, 'UPSTREAM_FORMAT');
    return value;
  }
  requireValue(typeof value === 'string' && /^\d+$/.test(value), 'UPSTREAM_FORMAT');
  return BigInt(value) <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(value) : value;
}

async function list({ share: input }, ctx) {
  const share = parseShare(input);
  const queue = [{ id: 'root', path: '' }], visited = new Set(['root']), files = [];
  for (let index = 0; index < queue.length; index += 1) {
    const directory = queue[index];
    for await (const page of directoryPages(ctx, share, directory.id)) {
      for (const child of page.directories) {
        if (visited.has(child.id)) continue;
        visited.add(child.id);
        queue.push({ id: child.id, path: `${directory.path}/${child.name}` });
      }
      for (const item of page.files) {
        if (text(item.coType) !== '3' || !videoName.test(item.coName.trim())) continue;
        const path = `${directory.path}/${item.coName}`, size = fileSize(item.coSize);
        files.push({
          name: item.coName, path, ...(size === undefined ? {} : { size }),
          file: {
            kind: 'share', shareId: share.id, password: share.password,
            coID: item.coID, ...(item.contentId === undefined ? {} : { contentId: item.contentId }),
            name: item.coName, path,
          },
        });
      }
    }
  }
  return { share, files };
}

function validateFile(file) {
  requireValue(object(file) && file.kind === 'share' && typeof file.shareId === 'string' && shareID.test(file.shareId));
  requireValue(typeof file.password === 'string' && typeof file.name === 'string' && file.name.length > 0 &&
    typeof file.path === 'string' && file.path.length > 0);
  requireValue(file.coID === undefined || (typeof file.coID === 'string' && file.coID.length > 0));
  requireValue(file.contentId === undefined || (typeof file.contentId === 'string' && file.contentId.length > 0));
  requireValue(file.coID || file.contentId);
  return file;
}

function mediaURL(value) {
  requireValue(typeof value === 'string' && value.trim(), 'UPSTREAM_FORMAT');
  let url;
  try { url = new URL(value.trim()); } catch { throw new PanError('UPSTREAM_FORMAT'); }
  requireValue(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password, 'UPSTREAM_FORMAT');
  return value.trim();
}

async function currentAuthorization(ctx) {
  let credentials = ctx.credentials;
  authorizationParts(credentials.authorization);
  const refreshedAt = credentials.refreshedAt, now = ctx.now();
  if (!Number.isFinite(refreshedAt) || refreshedAt <= 0 || refreshedAt > now || now - refreshedAt >= refreshPeriod) {
    credentials = await ctx.refresh();
  }
  return authorizationParts(credentials.authorization);
}

function downloadURL(value) {
  const data = object(value.data) ? value.data : value;
  for (const key of ['redrUrl', 'redrUrlNew', 'downloadUrl', 'url', 'dlUrl']) {
    if (typeof data[key] === 'string' && data[key].trim().startsWith('http')) return mediaURL(data[key]);
  }
  return '';
}

async function play({ file: input, mode }, ctx) {
  requireValue(['streaming', 'download'].includes(mode), 'UNSUPPORTED_OPERATION');
  const file = validateFile(input);
  if (mode === 'streaming') {
    requireValue(file.coID, 'INVALID_REQUEST');
    const value = await post(ctx, 'getContentInfoFromOutLink', {
      getContentInfoFromOutLinkReq: { contentId: file.coID, linkID: file.shareId, account: '' },
      commonAccountInfo: { account: '', accountType: 1 },
    });
    assertResult(value, file.password);
    const info = value.data?.contentInfo;
    requireValue(object(info), 'UPSTREAM_FORMAT');
    return { url: mediaURL(text(info.presentURL).trim() || text(info.presentUrl).trim()), headers: {}, mode };
  }
  const auth = await currentAuthorization(ctx);
  const request = async useCoID => post(ctx, 'dlFromOutLinkV3', {
    ...(useCoID ? {
      dlFromOutLinkReqV3: { account: auth.account, linkID: file.shareId, coIDLst: { item: [file.coID] } },
    } : {
      dlFromOutLinkReq: { contentId: file.contentId, linkID: file.shareId, account: auth.account },
    }),
    commonAccountInfo: { account: auth.account, accountType: 1 },
  }, { encrypted: true, auth });
  let value = await request(Boolean(file.coID));
  let url = downloadURL(value);
  if (!url && resultCode(value) === '9530' && file.coID && file.contentId) {
    value = await request(false);
    url = downloadURL(value);
  }
  if (!url) {
    assertResult(value, file.password);
    throw new PanError('UPSTREAM_FORMAT');
  }
  if (resultCode(value) !== '9530') assertResult(value, file.password);
  return { url, headers: {}, mode };
}

function escapeXML(value) {
  return value.replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[char]);
}

function xmlText(value) {
  let result = '', offset = 0;
  const parts = /<!\[CDATA\[([\s\S]*?)\]\]>|([^<]+)/gy;
  while (offset < value.length) {
    parts.lastIndex = offset;
    const match = parts.exec(value);
    requireValue(match, 'UPSTREAM_FORMAT');
    offset = parts.lastIndex;
    if (match[1] !== undefined) { result += match[1]; continue; }
    result += match[2].replace(/&([^;]*);|&/g, (entity, name) => {
      const named = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };
      if (Object.hasOwn(named, name)) return named[name];
      requireValue(/^#(?:\d+|x[0-9a-fA-F]+)$/.test(name ?? ''), 'UPSTREAM_FORMAT');
      const point = name.startsWith('#x') ? parseInt(name.slice(2), 16) : Number(name.slice(1));
      requireValue(point === 9 || point === 10 || point === 13 ||
        (point >= 32 && point <= 0xD7FF) || (point >= 0xE000 && point <= 0xFFFD) ||
        (point >= 0x10000 && point <= 0x10FFFF), 'UPSTREAM_FORMAT');
      return String.fromCodePoint(point);
    });
  }
  return result;
}

function refreshToken(body) {
  const root = /^(?:<\?xml\s+[^?]*\?>\s*)?<root\s*>([\s\S]*)<\/root>$/.exec(body.toString('utf8').trim());
  requireValue(root, 'UPSTREAM_FORMAT');
  const values = new Map(), source = root[1];
  const elements = /\s*(?:<([A-Za-z_][\w.-]*)\s*>([\s\S]*?)<\/\1>|<([A-Za-z_][\w.-]*)\s*\/>)\s*/gy;
  let offset = 0;
  while (offset < source.length && source.slice(offset).trim()) {
    elements.lastIndex = offset;
    const match = elements.exec(source);
    requireValue(match, 'UPSTREAM_FORMAT');
    const key = match[1] ?? match[3];
    requireValue(!values.has(key), 'UPSTREAM_FORMAT');
    values.set(key, xmlText(match[2] ?? '').trim());
    offset = elements.lastIndex;
  }
  requireValue(values.has('return'), 'UPSTREAM_FORMAT');
  const result = values.get('return');
  requireValue(/^-?\d+$/.test(result), 'UPSTREAM_FORMAT');
  requireValue(result === '0', result === '1' ? 'AUTH_FAILED' : 'UPSTREAM_ERROR');
  requireValue(values.get('token'), 'UPSTREAM_FORMAT');
  return values.get('token');
}

async function refresh(_, ctx) {
  const auth = authorizationParts(ctx.credentials.authorization);
  const response = await ctx.http.request({
    url: refreshURL, method: 'POST', signal: ctx.signal, redirect: 'manual',
    headers: { 'Content-Type': 'application/xml', Accept: 'application/xml', 'User-Agent': userAgent },
    body: `<root><token>${escapeXML(auth.token)}</token><account>${escapeXML(auth.account)}</account><clienttype>656</clienttype></root>`,
  });
  if (ctx.signal?.aborted) throw new PanError('CANCELLED');
  requireValue(response.status >= 200 && response.status < 300,
    response.status === 401 ? 'AUTH_REQUIRED' : 'UPSTREAM_HTTP');
  const token = refreshToken(response.body);
  await ctx.updateCredentials({
    authorization: Buffer.from(`${auth.prefix}:${auth.account}:${token}`).toString('base64'), refreshedAt: ctx.now(),
  });
  return { refreshed: true };
}

export const yidong = {
  id: 'yidong', credentialFields: { authorization: 'string', refreshedAt: 'number' },
  modes: ['streaming', 'download'], list, play, refresh,
};
