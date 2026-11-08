import { randomBytes } from 'node:crypto';
import { posix } from 'node:path';
import { PanError, requireValue } from '../errors.js';
import { requestJSON } from '../http.js';
import { CookieJar } from '../cookies.js';
import { jsonObject, mergeCookie, parseJSON, text } from '../values.js';

const pan = 'https://pan.baidu.com';
const passport = 'https://passport.baidu.com';
const webUA = 'Mozilla/5.0 (Linux; Android 12; V2238A) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/101.0.4951.40 Safari/537.36';
const qrUA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36';
const netdiskUA = 'netdisk;12.11.9;V2238A;android-android;12;JSbridge4.4.0;jointBridge;1.1.0;';
const playUA = 'com.android.chrome/131.0.6778.200 (Linux;Android 10) AndroidXMedia3/1.5.1';
const pageSize = 10000;
const directory = '/MeowFilm';
const video = /\.(mp4|mkv|mov|iso|flv)$/i;

function active(ctx) {
  if (ctx.signal?.aborted) throw new PanError('CANCELLED');
}

function record(value) {
  requireValue(value !== null && typeof value === 'object' && !Array.isArray(value), 'UPSTREAM_FORMAT');
  return value;
}

function id(value) {
  const result = text(value).trim();
  requireValue(result, 'UPSTREAM_FORMAT');
  return result;
}

function isDirectory(value) {
  return value === true || text(value) === '1' || text(value).trim() === 'true';
}

function entryId(item) {
  return text(item.fs_id).trim() || text(item.fsid).trim();
}

function entryName(item) {
  return typeof item.server_filename === 'string' && item.server_filename.trim() ? item.server_filename : item.name;
}

function entryPath(item) {
  return typeof item.path === 'string' && item.path.trim() ? item.path : item.server_path;
}

function joinPath(parent, name) {
  return `${parent === '/' ? '' : parent}/${name}`;
}

function address(value, base, code = 'UPSTREAM_FORMAT') {
  try {
    const url = new URL(value, base);
    requireValue(['https:', 'http:'].includes(url.protocol) && !url.username && !url.password, code);
    return url;
  } catch {
    throw new PanError(code);
  }
}

function shareInput(share) {
  requireValue(share && typeof share.url === 'string');
  requireValue(share.password === undefined || typeof share.password === 'string');
  const url = address(share.url, undefined, 'INVALID_REQUEST');
  requireValue(url.hostname === 'pan.baidu.com' && (!url.port || url.port === '443'), 'INVALID_REQUEST');
  const match = /^\/s\/([A-Za-z0-9_-]+)\/?$/.exec(url.pathname);
  const surl = match ? match[1].replace(/^1/, '') : url.searchParams.get('surl') || '';
  requireValue(/^[A-Za-z0-9_-]+$/.test(surl), 'INVALID_REQUEST');
  return { id: surl, url: `${pan}/s/1${surl}`, password: share.password ?? url.searchParams.get('pwd') ?? '' };
}

function authorization(ctx) {
  const cookie = ctx.credentials.cookie;
  requireValue(typeof cookie === 'string' && cookie.trim(), 'NOT_CONFIGURED');
  return { cookie: cookie.trim() };
}

async function api(ctx, auth, path, query = {}, options = {}) {
  active(ctx);
  const url = new URL(path, pan);
  url.search = new URLSearchParams(query).toString();
  const response = await requestJSON(ctx, url.href, {
    ...options,
    headers: {
      'User-Agent': webUA, Referer: `${pan}/`, Origin: pan,
      Accept: 'application/json, text/plain, */*', Cookie: auth.cookie,
      ...options.headers,
    },
  });
  const cookies = response.headers['set-cookie'] || [];
  if (cookies.length) {
    auth.cookie = mergeCookie(auth.cookie, cookies);
    const updated = mergeCookie(ctx.credentials.cookie, cookies);
    if (updated && updated !== ctx.credentials.cookie) await ctx.updateCredentials({ cookie: updated });
  }
  requireValue(response.status >= 200 && response.status < 300, 'UPSTREAM_HTTP');
  return record(response.value);
}

function success(value, allowed = []) {
  const code = text(value.errno);
  requireValue(code !== '', 'UPSTREAM_FORMAT');
  requireValue(code === '0' || allowed.includes(code), 'UPSTREAM_ERROR');
  return value;
}

async function verify(ctx, auth, surl, password) {
  const pwd = password.trim();
  if (!pwd) return;
  success(await api(ctx, auth, '/share/verify', { surl, t: String(ctx.now()) }, {
    method: 'POST', form: { pwd }, headers: { 'User-Agent': qrUA },
  }));
}

function size(value) {
  if (value === undefined || value === null) return {};
  const raw = text(value);
  requireValue(/^\d+$/.test(raw), 'UPSTREAM_FORMAT');
  return { size: BigInt(raw) <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(raw) : raw };
}

function morePages(root, content, length, consumed, limit, previousTotal) {
  const signal = root.has_more ?? content.has_more;
  let more;
  if (signal !== undefined) {
    requireValue([true, false, 0, 1, '0', '1'].includes(signal), 'UPSTREAM_FORMAT');
    more = signal === true || signal === 1 || signal === '1';
  }
  let total = previousTotal;
  const declared = root.total ?? content.total;
  if (declared !== undefined) {
    requireValue(/^\d+$/.test(text(declared)), 'UPSTREAM_FORMAT');
    const count = BigInt(text(declared));
    if (total === undefined || count > total) total = count;
  }
  if (total !== undefined) {
    const remaining = total > BigInt(consumed);
    requireValue(more !== false || !remaining, 'UPSTREAM_FORMAT');
    more ??= remaining;
  }
  return { more: more ?? length >= limit, declared: more === true, total };
}

async function pages(ctx, auth, path, query, limit, consume, options = {}) {
  const seen = new Set();
  let expected = false;
  let total;
  for (let page = 1; ; page++) {
    const root = success(await api(ctx, auth, path, { ...query, page: String(page), num: String(limit) }, options));
    let content = root;
    let pagination = root;
    while (content.list === undefined && content.data !== undefined) {
      content = record(content.data);
      pagination = { has_more: pagination.has_more ?? content.has_more, total: pagination.total ?? content.total };
    }
    requireValue(Array.isArray(content.list), 'UPSTREAM_FORMAT');
    let added = 0;
    for (const raw of content.list) {
      const item = record(raw);
      const directoryItem = isDirectory(item.isdir);
      const identity = entryId(item) || (directoryItem ? entryPath(item) || entryName(item) : '');
      requireValue(typeof identity === 'string' && identity.length > 0, 'UPSTREAM_FORMAT');
      const key = `${directoryItem ? 'd' : 'f'}:${identity}`;
      if (!seen.has(key)) {
        seen.add(key);
        added++;
        consume(item, root);
      }
    }
    requireValue(!(content.list.length && !added) && !(expected && !content.list.length), 'UPSTREAM_FORMAT');
    const next = morePages(pagination, content, content.list.length, seen.size, limit, total);
    total = next.total;
    requireValue(!next.more || content.list.length > 0, 'UPSTREAM_FORMAT');
    if (!next.more) return root;
    expected = next.declared;
  }
}

function reference(file, kind) {
  requireValue(file && file.kind === kind);
  for (const field of kind === 'share' ? ['shareId', 'uk', 'fileId', 'surl', 'name', 'path'] : ['fileId', 'name', 'path']) {
    requireValue(typeof file[field] === 'string' && file[field].length > 0);
  }
  requireValue(file.path.startsWith('/') && !file.path.includes('\0'));
  if (kind === 'share') {
    requireValue(/^[A-Za-z0-9_-]+$/.test(file.surl));
    requireValue(typeof file.password === 'string');
  }
  return file;
}

function transferredFile(entries, source) {
  requireValue(Array.isArray(entries), 'UPSTREAM_FORMAT');
  const matches = entries.map(record).filter(item => item.from_fs_id === undefined
    ? item.from === source.path : text(item.from_fs_id) === source.fileId);
  requireValue(matches.length === 1, 'UPSTREAM_FORMAT');
  const item = matches[0];
  if (item.errno !== undefined) success(item);
  const fileId = id(item.to_fs_id);
  const path = item.to;
  requireValue(typeof path === 'string' && !path.includes('\0') &&
    posix.normalize(path) === path && posix.dirname(path) === directory, 'UPSTREAM_FORMAT');
  return { file: { kind: 'stored', fileId, path, name: posix.basename(path) } };
}

async function resolveMedia(ctx, value) {
  let url = address(value);
  const seen = new Set();
  for (;;) {
    active(ctx);
    requireValue(!seen.has(url.href), 'UPSTREAM_FORMAT');
    seen.add(url.href);
    const response = await ctx.http.request({
      url: url.href, method: 'GET', responseMode: 'headers',
      headers: { 'User-Agent': playUA, Range: 'bytes=0-0' }, signal: ctx.signal,
    });
    if (response.status >= 300 && response.status < 400) {
      requireValue(typeof response.headers.location === 'string', 'UPSTREAM_FORMAT');
      const next = address(response.headers.location, url);
      requireValue(url.protocol !== 'https:' || next.protocol === 'https:', 'UPSTREAM_HTTP');
      url = next;
      continue;
    }
    requireValue(response.status >= 200 && response.status < 300, 'UPSTREAM_HTTP');
    return url.href;
  }
}

async function qrRequest(ctx, session, value, timeout = 30000) {
  let url = address(value);
  const seen = new Set();
  for (;;) {
    active(ctx);
    const remaining = session.expiresAt - ctx.now();
    requireValue(remaining > 0, 'LOGIN_EXPIRED');
    requireValue(url.protocol === 'https:' && [pan, passport].includes(url.origin), 'UPSTREAM_FORMAT');
    requireValue(!seen.has(url.href), 'UPSTREAM_FORMAT');
    seen.add(url.href);
    let response;
    try {
      response = await ctx.http.request({
        url: url.href, signal: ctx.signal, timeout: Math.min(timeout, remaining),
        headers: { 'User-Agent': qrUA, Referer: `${pan}/`, Cookie: session.jar.header(url.href) },
      });
    } catch (error) {
      active(ctx);
      requireValue(ctx.now() < session.expiresAt, 'LOGIN_EXPIRED');
      throw error;
    }
    active(ctx);
    requireValue(ctx.now() < session.expiresAt, 'LOGIN_EXPIRED');
    session.jar.set(url.href, response.headers['set-cookie'] || []);
    if (response.status >= 300 && response.status < 400) {
      requireValue(typeof response.headers.location === 'string', 'UPSTREAM_FORMAT');
      url = address(response.headers.location, url);
      continue;
    }
    requireValue(response.status >= 200 && response.status < 300, 'UPSTREAM_HTTP');
    return response;
  }
}

function qrImage(response) {
  const body = response.body;
  requireValue(body.length > 0, 'UPSTREAM_FORMAT');
  let type = text(response.headers['content-type']).split(';')[0].trim();
  if (!type) {
    const prefix = body.subarray(0, 16).toString('hex');
    if (prefix.startsWith('89504e470d0a1a0a')) type = 'image/png';
    else if (prefix.startsWith('ffd8ff')) type = 'image/jpeg';
    else if (prefix.startsWith('474946383761') || prefix.startsWith('474946383961')) type = 'image/gif';
    else if (prefix.startsWith('52494646') && prefix.slice(16, 28) === '57454250565038') type = 'image/webp';
    else if (prefix.startsWith('424d')) type = 'image/bmp';
    else if (prefix.startsWith('00000100') || prefix.startsWith('00000200')) type = 'image/x-icon';
  }
  requireValue(/^image\/[a-z0-9.+-]+$/i.test(type), 'UPSTREAM_FORMAT');
  return { type: 'image', url: `data:${type};base64,${body.toString('base64')}` };
}

export const baidu = {
  id: 'baidu',
  credentialFields: { cookie: 'string' },
  modes: ['streaming'],

  async list({ share }, ctx) {
    const parsed = shareInput(share);
    const auth = authorization(ctx);
    await verify(ctx, auth, parsed.id, parsed.password);
    const files = [];
    const queue = [{ path: '/', root: true }];
    const directories = new Set(['/']);
    const directoryIds = new Set();
    const fileIds = new Set();
    let shareId;
    let uk;
    for (let index = 0; index < queue.length; index++) {
      const current = queue[index];
      await pages(ctx, auth, '/share/list', {
        shorturl: parsed.id, root: current.root ? '1' : '0',
        ...(current.root ? {} : { dir: current.path, t: String(ctx.now() * 1000) }),
        order: current.root ? 'time' : 'other', desc: '1', showempty: '0',
      }, pageSize, (item, root) => {
        shareId ||= text(root.shareid).trim() || text(root.share_id).trim() ||
          text(root.data?.shareid).trim() || text(root.data?.share_id).trim();
        uk ||= text(root.uk).trim() || text(root.share_uk).trim() ||
          text(root.data?.uk).trim() || text(root.data?.share_uk).trim();
        const name = entryName(item);
        const folder = isDirectory(item.isdir);
        const itemId = folder ? entryId(item) : id(entryId(item));
        let itemPath = entryPath(item);
        if (!itemPath || !folder) {
          requireValue(typeof name === 'string' && name.length > 0, 'UPSTREAM_FORMAT');
          itemPath ||= joinPath(current.path, name);
        }
        requireValue(typeof itemPath === 'string' && itemPath.startsWith('/') && !itemPath.includes('\0'), 'UPSTREAM_FORMAT');
        if (folder) {
          const key = posix.normalize(itemPath);
          if (!directories.has(key) && (!itemId || !directoryIds.has(itemId))) {
            directories.add(key);
            if (itemId) directoryIds.add(itemId);
            queue.push({ path: key, root: false });
          }
        } else if (video.test(name.trim()) && !fileIds.has(itemId)) {
          requireValue(shareId && uk, 'UPSTREAM_FORMAT');
          fileIds.add(itemId);
          files.push({
            name, path: itemPath, ...size(item.size),
            file: { kind: 'share', fileId: itemId, shareId, uk, surl: parsed.id, password: parsed.password, name, path: itemPath },
          });
        }
      }, { headers: { 'User-Agent': qrUA } });
    }
    return { share: parsed, files };
  },

  async prepare({ file }, ctx) {
    reference(file, 'share');
    const auth = authorization(ctx);
    const login = await api(ctx, auth, '/api/loginStatus', { clienttype: '1', web: '1', channel: 'web', version: '0' });
    if (login.errno !== undefined) success(login);
    const token = id(record(login.login_info).bdstoken);
    let found = false;
    await pages(ctx, auth, '/api/list', {
      clienttype: '0', app_id: '250528', web: '1', order: 'time', desc: '1',
    }, 9999, item => {
      if (isDirectory(item.isdir) && item.path === directory) found = true;
    });
    if (!found) {
      success(await api(ctx, auth, '/api/create', { a: 'commit', bdstoken: token, clienttype: '0', web: '1' }, {
        method: 'POST', form: { path: `/${directory}`, isdir: '1', block_list: '[]' },
      }), ['31066', '-8']);
    }
    await verify(ctx, auth, file.surl, file.password);
    let fsid = file.fileId;
    if (/^[+-]?\d+$/.test(fsid)) {
      const numeric = BigInt(fsid);
      if (numeric >= -9223372036854775808n && numeric <= 9223372036854775807n) fsid = numeric;
    }
    const value = success(await api(ctx, auth, '/share/transfer', {
      shareid: file.shareId, from: file.uk, ondup: 'newcopy',
    }, {
      method: 'POST', form: {
        fsidlist: typeof fsid === 'bigint' ? `[${fsid}]` : JSON.stringify([fsid]),
        path: directory,
      },
    }));
    return transferredFile(value.extra?.list, file);
  },

  async play({ file, mode }, ctx) {
    requireValue(mode === 'streaming', 'UNSUPPORTED_OPERATION');
    reference(file, file?.kind === 'share' ? 'share' : 'stored');
    const stored = file.kind === 'share' ? await ctx.prepare(file) : file;
    reference(stored, 'stored');
    const auth = authorization(ctx);
    const value = await api(ctx, auth, '/api/mediainfo', {
      type: 'M3U8_FLV_264_480', path: stored.path, origin: 'dlna', check_blue: '1', app_id: '250528',
      devuid: 'kx1cK7VGweDrdrLiQpQRZduW5KTFvBHU|YyLyiRidC', clienttype: '80',
      channel: 'android_12_V2238A_bd-netdisk_1024266g', network_type: 'wifi', version: '12.11.9',
    }, { headers: { 'User-Agent': netdiskUA, 'Content-Type': 'application/x-www-form-urlencoded' } });
    const link = text(value.info?.dlink).trim();
    if (!link && value.errno !== undefined) success(value);
    requireValue(link, 'UPSTREAM_FORMAT');
    return { url: await resolveMedia(ctx, link), headers: { 'User-Agent': playUA }, mode, file: stored };
  },

  async loginStart({}, ctx) {
    const session = {
      jar: new CookieJar(), gid: randomBytes(16).toString('hex').toUpperCase(),
      callback: `bd__cbs__${randomBytes(6).toString('hex')}`, expiresAt: ctx.now() + 180000,
    };
    try {
      await qrRequest(ctx, session, `${pan}/`);
      const url = new URL('/v2/api/getqrcode', passport);
      url.search = new URLSearchParams({ lp: 'pc', tt: String(ctx.now()), gid: session.gid, callback: session.callback }).toString();
      const response = await qrRequest(ctx, session, url);
      const value = record(jsonObject(response.body.toString('utf8')));
      success(value);
      session.sign = id(value.sign);
      const image = text(value.imgurl).trim();
      requireValue(image, 'UPSTREAM_FORMAT');
      const imageURL = address(image.startsWith('//') || image.startsWith('http') ? image : `https://${image}`, passport);
      requireValue(imageURL.protocol === 'https:' && [pan, passport].includes(imageURL.origin), 'UPSTREAM_FORMAT');
      const challenge = qrImage(await qrRequest(ctx, session, imageURL));
      return { session, challenge, expiresAt: session.expiresAt };
    } catch (error) {
      session.jar.clear();
      throw error;
    }
  },

  async loginPoll(session, ctx) {
    active(ctx);
    if (session.cancelled || ctx.now() >= session.expiresAt) {
      session.jar.clear();
      return { status: 'expired' };
    }
    if (session.credentials) return { status: 'confirmed', credentials: session.credentials };
    const url = new URL('/channel/unicast', passport);
    url.search = new URLSearchParams({
      channel_id: session.sign, tpl: 'netdisk', apiver: 'v3', tt: String(ctx.now()),
      gid: session.gid, callback: session.callback,
    }).toString();
    const response = await qrRequest(ctx, session, url, 40000);
    const value = record(jsonObject(response.body.toString('utf8')));
    if (text(value.errno) === '1') return { status: 'pending' };
    success(value);
    if (!value.channel_v) return { status: 'pending' };
    let channel = typeof value.channel_v === 'string' ? parseJSON(value.channel_v) : value.channel_v;
    channel = record(typeof channel === 'string' ? parseJSON(channel) : channel);
    const status = text(channel.status);
    requireValue(/^-?\d+$/.test(status), 'UPSTREAM_FORMAT');
    if (status !== '0') return { status: status === '1' ? 'scanned' : 'pending' };
    requireValue(typeof channel.v === 'string' && channel.v, 'UPSTREAM_FORMAT');
    const finalize = new URL('/v3/login/main/qrbdusslogin', passport);
    finalize.search = new URLSearchParams({ bduss: channel.v, u: `${pan}/`, tpl: 'netdisk', apiver: 'v3', tt: String(ctx.now()) }).toString();
    await qrRequest(ctx, session, finalize);
    await qrRequest(ctx, session, `${pan}/`);
    let cookie = session.jar.header(`${pan}/`);
    if (!/(?:^|;\s*)BDUSS=[^;]+/.test(cookie)) {
      cookie = mergeCookie(cookie, session.jar.header(`${passport}/`).split(';').map(value => value.trim()).filter(Boolean));
    }
    requireValue(/(?:^|;\s*)BDUSS=[^;]+/.test(cookie), 'AUTH_FAILED');
    session.credentials = { cookie };
    return { status: 'confirmed', credentials: session.credentials };
  },

  loginCancel(session) {
    session.cancelled = true;
    session.jar.clear();
    delete session.credentials;
    delete session.sign;
  },
};
