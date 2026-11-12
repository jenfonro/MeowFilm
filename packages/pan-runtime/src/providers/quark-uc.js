import { createHash, randomUUID } from 'node:crypto';
import { PanError, requireValue } from '../errors.js';
import { delay, requestJSON, requestText } from '../http.js';
import { CookieJar } from '../cookies.js';
import { mergeCookie, parseJSON, text } from '../values.js';

const browserUA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0';
const htmlAccept = 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8';
const tvUA = 'Mozilla/5.0 (Linux; U; Android 13; zh-cn; M2004J7AC Build/UKQ1.231108.001) AppleWebKit/533.1 (KHTML, like Gecko) Mobile Safari/533.1';
const driveUA = (name) => `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ${name}-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 Channel/pckk_other_ch`;
const quarkConfig = {
  id: 'quark', host: 'pan.quark.cn', api: 'https://drive.quark.cn/1/clouddrive/',
  query: { pr: 'ucpro', fr: 'pc' }, referer: 'https://pan.quark.cn/', ua: driveUA('quark'),
  sso: 'https://uop.quark.cn', clientId: '532', qrURL: 'https://su.quark.cn/4_eMHBJ',
  loginHosts: new Set(['pan.quark.cn', 'drive.quark.cn', 'drive-h.quark.cn', 'uop.quark.cn', 'su.quark.cn']),
  tv: {
    id: 'quark_tv', api: 'https://open-api-drive.quark.cn/file',
    token: 'http://api.extscreen.com/quarkdrive/token',
    clientId: 'd3194e61504e493eb6222857bccfed94', signKey: 'kw2dvtd7p4t3pjl2d9ed9yc8yej8kw2d',
    appVersion: '1.8.2.2', channel: 'GENERAL',
  },
};
const ucConfig = {
  id: 'uc', host: 'drive.uc.cn', api: 'https://pc-api.uc.cn/1/clouddrive/',
  query: { pr: 'UCBrowser', fr: 'pc' }, referer: 'https://drive.uc.cn', ua: driveUA('uc'),
  sso: 'https://api.open.uc.cn', clientId: '381', qrURL: 'https://su.uc.cn/1_n0ZCv',
  loginHosts: new Set(['drive.uc.cn', 'api.open.uc.cn', 'pc-api.uc.cn', 'su.uc.cn']),
  tv: {
    id: 'uc_tv', api: 'https://open-api-drive.uc.cn/file',
    token: 'http://api.extscreen.com/ucdrive/token',
    clientId: '5acf882d27b74502b7040b0c65519aa7', signKey: 'l3srvtd7p42l0d0x1u8d7yc8ye9kki4d',
    appVersion: '1.7.2.2', channel: 'UCTVOFFICIALWEB',
  },
};

function record(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function active(ctx) {
  if (ctx.signal?.aborted) throw new PanError('CANCELLED');
}

function stringField(value) {
  return text(value).trim();
}

function field(root, names, stringOnly = false) {
  const queue = [root];
  for (let index = 0; index < queue.length; index += 1) {
    const value = queue[index];
    if (Array.isArray(value)) {
      for (const entry of value) queue.push(entry);
    } else if (record(value)) {
      for (const key of names) {
        const entry = Object.entries(value).find(([name]) => name.trim().toLowerCase() === key.toLowerCase());
        if (entry && (stringOnly ? typeof entry[1] === 'string' && entry[1].trim()
          : entry[1] !== undefined && entry[1] !== null && entry[1] !== '')) return entry[1];
      }
      for (const entry of Object.values(value)) queue.push(entry);
    }
  }
  return undefined;
}

function httpOK(response) {
  requireValue(response.status >= 200 && response.status < 300,
    response.status === 401 ? 'AUTH_REQUIRED' : 'UPSTREAM_HTTP');
}

function apiOK(value) {
  requireValue(record(value), 'UPSTREAM_FORMAT');
  if (value.code !== undefined) {
    const code = stringField(value.code);
    requireValue(code !== '', 'UPSTREAM_FORMAT');
    requireValue(code === '0' || code === '200', 'UPSTREAM_ERROR');
  }
  return value;
}

function apiURL(config, pathname, query = {}) {
  const url = new URL(pathname, config.api);
  url.search = new URLSearchParams({ ...config.query, ...query }).toString();
  return url.href;
}

function webHeaders(config, cookie) {
  return {
    'User-Agent': config.ua, Referer: config.referer,
    Accept: 'application/json, text/plain, */*',
    ...(cookie ? { Cookie: cookie } : {}),
  };
}

async function persistCookie(ctx, headers) {
  const current = ctx.credentials.cookie || '';
  const next = mergeCookie(current, headers['set-cookie'] || []);
  if (next !== current) await ctx.updateCredentials({ cookie: next });
}

async function webJSON(config, ctx, url, options = {}) {
  active(ctx);
  const response = await requestJSON(ctx, url, {
    ...options, headers: webHeaders(config, ctx.credentials.cookie),
  });
  await persistCookie(ctx, response.headers);
  httpOK(response);
  return apiOK(response.value);
}

function shareInput(config, share) {
  requireValue(record(share) && typeof share.url === 'string');
  requireValue(share.password === undefined || typeof share.password === 'string');
  let url;
  try { url = new URL(share.url); } catch { throw new PanError('INVALID_REQUEST'); }
  requireValue(['https:', 'http:'].includes(url.protocol) && url.hostname === config.host
    && !url.username && !url.password && !url.port);
  const match = /^\/s\/([^/]+)\/?$/.exec(url.pathname);
  requireValue(match);
  let id;
  try { id = decodeURIComponent(match[1]); } catch { throw new PanError('INVALID_REQUEST'); }
  requireValue(id && !/[\/\\\s?#]/.test(id));
  return {
    id, url: `https://${config.host}/s/${encodeURIComponent(id)}`,
    password: share.password ?? url.searchParams.get('pwd') ?? url.searchParams.get('passcode') ?? '',
  };
}

function cookieRequired(ctx) {
  requireValue(typeof ctx.credentials.cookie === 'string' && ctx.credentials.cookie.trim(), 'NOT_CONFIGURED');
}

async function refresh(config, ctx) {
  cookieRequired(ctx);
  active(ctx);
  const url = new URL('/account/info', `https://${config.host}`);
  url.search = new URLSearchParams({ fr: 'pc', platform: 'pc' }).toString();
  const response = await ctx.http.request({
    url: url.href, method: 'GET', signal: ctx.signal, headers: webHeaders(config, ctx.credentials.cookie),
  });
  active(ctx);
  httpOK(response);
  const value = parseJSON(response.body);
  requireValue(record(value) && typeof value.code === 'string' && typeof value.success === 'boolean', 'UPSTREAM_FORMAT');
  requireValue(value.code === 'OK' && value.success, 'UPSTREAM_ERROR');
  requireValue(record(value.data), 'UPSTREAM_FORMAT');
  requireValue(Object.keys(value.data).length > 0, 'AUTH_REQUIRED');
  await persistCookie(ctx, response.headers);
  return { refreshed: true };
}

function firstText(root, names) {
  for (const name of names) {
    const value = field(root, [name], true);
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return '';
}

async function shareToken(config, share, ctx) {
  const body = { pwd_id: share.id, ...(share.password ? { passcode: share.password } : {}) };
  const attempts = [{ url: apiURL(config, 'share/sharepage/token'), method: 'POST', json: body }];
  if (config.id === 'uc') {
    attempts.push(
      { url: apiURL(config, 'share/sharepage/detail', { pwd_id: share.id }) },
      { url: apiURL(config, 'share/sharepage/detail'), method: 'POST', json: { ...body, pdir_fid: '0' } },
    );
  }
  let failure;
  for (const { url, ...options } of attempts) {
    try {
      const response = await webJSON(config, ctx, url, options);
      const token = firstText(response, ['stoken']);
      if (token) return token;
      failure ||= new PanError('UPSTREAM_FORMAT');
    } catch (error) {
      if (!['UPSTREAM_ERROR', 'UPSTREAM_FORMAT', 'UPSTREAM_HTTP'].includes(error.code)) throw error;
      failure ||= error;
    }
  }
  throw failure;
}

function itemId(item) {
  return stringField(item.fid) || stringField(item.file_id) || stringField(item.id);
}

function itemName(item) {
  return typeof item.file_name === 'string' && item.file_name
    ? item.file_name : (typeof item.name === 'string' ? item.name : '');
}

function isDirectory(item) {
  return item.dir === true || item.file === false || stringField(item.file_type) === '0'
    || ['folder', 'dir', 'directory'].includes(stringField(item.type).toLowerCase());
}

function itemToken(item) {
  return stringField(item.share_fid_token) || stringField(item.fid_token)
    || stringField(item.fidToken) || stringField(item.token);
}

function totalCount(value) {
  if (value === undefined || value === null) return undefined;
  const raw = stringField(value);
  requireValue(/^\d+$/.test(raw), 'UPSTREAM_FORMAT');
  return BigInt(raw);
}

async function directory(config, ctx, { share, token, parent = '0' } = {}) {
  const size = share ? 200 : 500;
  const items = [];
  const seen = new Set();
  let page = 1;
  let total;
  for (;;) {
    const query = { pdir_fid: parent, _page: String(page), _size: String(size), _sort: 'file_type:asc,file_name:asc' };
    if (share) Object.assign(query, { pwd_id: share.id, stoken: token, force: '0' });
    else query._fetch_total = '1';
    const result = await webJSON(config, ctx, apiURL(config, share ? 'share/sharepage/detail' : 'file/sort', query));
    const data = result.data;
    requireValue(record(data) && Array.isArray(data.list), 'UPSTREAM_FORMAT');
    let added = 0;
    for (const item of data.list) {
      requireValue(record(item), 'UPSTREAM_FORMAT');
      const id = itemId(item);
      requireValue(id, 'UPSTREAM_FORMAT');
      if (seen.has(id)) continue;
      seen.add(id);
      items.push(item);
      added += 1;
    }
    const declaredTotal = totalCount(data.total ?? data._total ?? result.metadata?._total);
    if (declaredTotal !== undefined && (total === undefined || declaredTotal > total)) total = declaredTotal;
    const hasMore = data.has_more ?? data.hasMore;
    requireValue(hasMore === undefined || typeof hasMore === 'boolean', 'UPSTREAM_FORMAT');
    const nextRaw = data.next_page ?? data.nextPage;
    requireValue(nextRaw === undefined || /^\d+$/.test(stringField(nextRaw)), 'UPSTREAM_FORMAT');
    const next = nextRaw === undefined ? 0 : Number(nextRaw);
    requireValue(Number.isSafeInteger(next) && (!next || next > page), 'UPSTREAM_FORMAT');
    const more = next > page || hasMore === true
      || (total !== undefined ? BigInt(seen.size) < total : hasMore !== false && data.list.length >= size);
    if (!more) return items;
    requireValue(added > 0, 'UPSTREAM_FORMAT');
    page = next > page ? next : page + 1;
  }
}

async function list(config, { share: input }, ctx) {
  const share = shareInput(config, input);
  cookieRequired(ctx);
  const token = await shareToken(config, share, ctx);
  const queue = [{ id: '0', path: '' }];
  const seenDirectories = new Set(['0']);
  const seenFiles = new Set();
  const files = [];
  for (let index = 0; index < queue.length; index += 1) {
    const current = queue[index];
    for (const item of await directory(config, ctx, { share, token, parent: current.id })) {
      const id = itemId(item);
      const name = itemName(item);
      if (isDirectory(item)) {
        requireValue(name, 'UPSTREAM_FORMAT');
        if (!seenDirectories.has(id)) {
          seenDirectories.add(id);
          queue.push({ id, path: `${current.path}/${name}` });
        }
      } else if (/\.(mp4|mkv|mov|iso|flv)$/i.test(name.trim())) {
        const fileToken = itemToken(item);
        requireValue(fileToken, 'UPSTREAM_FORMAT');
        if (seenFiles.has(id)) continue;
        seenFiles.add(id);
        const path = `${current.path}/${name}`;
        const file = {
          kind: 'share', shareId: share.id, shareUrl: share.url, password: share.password,
          stoken: token, fileId: id, fileToken, name, path,
        };
        const entry = { name, path, file };
        if (item.size !== undefined && item.size !== null) {
          const rawSize = stringField(item.size);
          requireValue(/^\d+$/.test(rawSize), 'UPSTREAM_FORMAT');
          entry.size = BigInt(rawSize) <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(rawSize) : rawSize;
        }
        files.push(entry);
      }
    }
  }
  return { share, files };
}

function storedFile(file) {
  requireValue(record(file) && file.kind === 'stored' && typeof file.fileId === 'string'
    && file.fileId.trim() && file.fileId.trim() !== '0');
  return file;
}

function shareFile(file) {
  requireValue(record(file) && file.kind === 'share');
  for (const name of ['shareId', 'stoken', 'fileId', 'fileToken']) {
    requireValue(typeof file[name] === 'string' && file[name].trim());
  }
  return file;
}

async function destination(config, ctx) {
  const matches = (await directory(config, ctx)).filter((item) => isDirectory(item) && itemName(item) === 'MeowFilm');
  requireValue(matches.length <= 1, 'UPSTREAM_FORMAT');
  if (matches.length === 1) return itemId(matches[0]);
  const result = await webJSON(config, ctx, apiURL(config, 'file'), {
    method: 'POST', json: { pdir_fid: '0', file_name: 'MeowFilm', dir_path: '', dir_init_lock: false },
  });
  const id = record(result.data) ? itemId(result.data) : '';
  requireValue(id && id !== '0', 'UPSTREAM_FORMAT');
  return id;
}

async function transferCookie(config, file, ctx) {
  if (config.id !== 'uc') return;
  if (/(?:^|;\s*)__puus=/.test(ctx.credentials.cookie) && /(?:^|;\s*)Video-Auth=/.test(ctx.credentials.cookie)) return;
  await optionalLogin(async () => {
    const response = await requestText(ctx, `https://drive.uc.cn/s/${encodeURIComponent(file.shareId)}?platform=pc`, {
      headers: { ...webHeaders(config, ctx.credentials.cookie), Accept: htmlAccept },
    });
    await persistCookie(ctx, response.headers);
  });
  await optionalLogin(() => webJSON(config, ctx, apiURL(config, 'transfer/upload/pdir'), { method: 'POST', json: {} }));
}

function savedId(value) {
  const queue = [value];
  const names = new Set(['save_as_top_fids', 'save_as_top_fid', 'save_as_select_top_fids']);
  let saved = '';
  for (let index = 0; index < queue.length; index += 1) {
    const current = queue[index];
    if (Array.isArray(current)) {
      for (const entry of current) queue.push(entry);
    } else if (record(current)) for (const [name, entry] of Object.entries(current)) {
      if (!names.has(name.toLowerCase())) {
        if (entry && typeof entry === 'object') queue.push(entry);
        continue;
      }
      if (Array.isArray(entry)) requireValue(entry.length === 1, 'UPSTREAM_FORMAT');
      const id = stringField(Array.isArray(entry) ? entry[0] : entry);
      requireValue(id && id !== '0' && (!saved || saved === id), 'UPSTREAM_FORMAT');
      saved = id;
    }
  }
  return saved;
}

async function prepare(config, { file }, ctx) {
  shareFile(file);
  cookieRequired(ctx);
  const parent = await destination(config, ctx);
  requireValue(parent && parent !== '0', 'UPSTREAM_FORMAT');
  await transferCookie(config, file, ctx);
  let result = await webJSON(config, ctx, apiURL(config, 'share/sharepage/save'), {
    method: 'POST', json: {
      fid_list: [file.fileId], fid_token_list: [file.fileToken], to_pdir_fid: parent,
      pwd_id: file.shareId, stoken: file.stoken, pdir_fid: '0', scene: 'link', share_id: file.shareId,
    },
  });
  if (result.task_sync === true) {
    const id = savedId(result);
    if (id) return { file: { kind: 'stored', fileId: id, parentFileId: parent } };
  }
  const taskId = stringField(field(result, ['task_id', 'taskid']));
  requireValue(taskId, 'UPSTREAM_FORMAT');
  const expiresAt = ctx.now() + 30_000;
  for (;;) {
    active(ctx);
    requireValue(ctx.now() < expiresAt, 'TIMEOUT');
    result = await webJSON(config, ctx, apiURL(config, 'task', { task_id: taskId }), {
      timeout: Math.max(1, expiresAt - ctx.now()),
    });
    const data = result.data;
    requireValue(record(data), 'UPSTREAM_FORMAT');
    const state = stringField(data.state ?? data.status);
    const finished = ['2', '3', '100'].includes(state) || data.finished === true || data.finish === true
      || [data.finished, data.finish].some((flag) => stringField(flag).toLowerCase() === 'true')
      || stringField(data.finish) === '1';
    if (finished) {
      const id = savedId(result);
      requireValue(id && id !== '0', 'UPSTREAM_FORMAT');
      return { file: { kind: 'stored', fileId: id, parentFileId: parent } };
    }
    await delay(Math.min(300, Math.max(0, expiresAt - ctx.now())), ctx.signal);
  }
}

function modeRequired(mode) {
  requireValue(mode === 'streaming' || mode === 'download', 'UNSUPPORTED_OPERATION');
}

async function ordinaryPlay(config, { file, mode }, ctx) {
  modeRequired(mode);
  if (file?.kind === 'share') shareFile(file);
  else storedFile(file);
  cookieRequired(ctx);
  const stored = file.kind === 'share' ? storedFile(await ctx.prepare(file)) : file;
  const want = mode === 'streaming' ? 'play_url' : 'download_url';
  const resolve = async (token) => {
    const result = await webJSON(config, ctx, apiURL(config, 'file/download'), {
      method: 'POST', json: {
        fid: stored.fileId, fids: [stored.fileId],
        ...(token ? { fid_token: token, fid_token_list: [token] } : {}),
      },
    });
    let data = result.data;
    if (Array.isArray(data)) {
      const exact = data.filter((item) => record(item) && itemId(item) === stored.fileId);
      requireValue(exact.length <= 1, 'UPSTREAM_FORMAT');
      data = exact[0] ?? (data.length === 1 && record(data[0]) && !itemId(data[0]) ? data[0] : undefined);
    }
    requireValue(record(data) && (!itemId(data) || itemId(data) === stored.fileId), 'UPSTREAM_FORMAT');
    const url = firstText(data, [want, 'download_url', 'play_url', 'url']);
    requireValue(url && /^https?:\/\//i.test(url), 'UPSTREAM_FORMAT');
    return url;
  };
  let url;
  try { url = await resolve(stored.fileToken); } catch (error) {
    if (stored.fileToken || !stored.parentFileId || !['UPSTREAM_ERROR', 'UPSTREAM_FORMAT'].includes(error.code)) throw error;
    const items = await directory(config, ctx, { parent: stored.parentFileId });
    const exact = items.find((item) => itemId(item) === stored.fileId);
    requireValue(exact && !isDirectory(exact), 'NOT_FOUND');
    const token = itemToken(exact);
    requireValue(token, 'UPSTREAM_FORMAT');
    url = await resolve(token);
  }
  return { url, headers: webHeaders(config, ctx.credentials.cookie), mode, file: stored };
}

function tvDevice(config, credentials, now) {
  const deviceId = stringField(credentials.deviceId);
  return {
    req_id: createHash('md5').update(deviceId + String(now)).digest('hex'),
    app_ver: config.appVersion, device_id: deviceId, device_brand: 'Xiaomi', platform: 'tv',
    device_name: 'M2004J7AC', device_model: 'M2004J7AC', build_device: 'M2004J7AC', build_product: 'M2004J7AC',
    device_gpu: 'Adreno (TM) 550', activity_rect: '{}', channel: config.channel,
  };
}

async function tvRefresh(config, ctx) {
  const credentials = ctx.credentials;
  requireValue(stringField(credentials.refreshToken) && stringField(credentials.deviceId), 'NOT_CONFIGURED');
  active(ctx);
  const response = await ctx.http.request({
    url: config.token, method: 'POST', signal: ctx.signal,
    headers: { 'User-Agent': tvUA, Accept: 'application/json, text/plain, */*', 'Content-Type': 'application/json' },
    body: Buffer.from(JSON.stringify({ ...tvDevice(config, credentials, ctx.now()), refresh_token: credentials.refreshToken.trim() })),
  });
  active(ctx);
  httpOK(response);
  const value = parseJSON(response.body);
  requireValue(record(value) && stringField(value.code), 'UPSTREAM_FORMAT');
  requireValue(stringField(value.code) === '200', 'UPSTREAM_ERROR');
  const data = value.data;
  if (record(data) && stringField(data.status) === '-1' && stringField(data.errno) === '11000') {
    throw new PanError('AUTH_FAILED');
  }
  requireValue(record(data) && typeof data.access_token === 'string' && data.access_token.trim(), 'UPSTREAM_FORMAT');
  requireValue(data.refresh_token === undefined || typeof data.refresh_token === 'string', 'UPSTREAM_FORMAT');
  requireValue(!/[\0\r\n]/.test(data.access_token) && !/[\0\r\n]/.test(data.refresh_token || ''), 'UPSTREAM_FORMAT');
  requireValue(data.expires_in === undefined || /^-?\d+$/.test(stringField(data.expires_in)), 'UPSTREAM_FORMAT');
  const expiresIn = data.expires_in === undefined ? 0 : Number(data.expires_in);
  const expiresAt = expiresIn > 0 ? ctx.now() + expiresIn * 1000 : 0;
  requireValue(Number.isSafeInteger(expiresIn) && Number.isSafeInteger(expiresAt) && expiresAt >= 0, 'UPSTREAM_FORMAT');
  await ctx.updateCredentials({
    accessToken: data.access_token.trim(),
    refreshToken: stringField(data.refresh_token) || credentials.refreshToken,
    expiresAt,
  });
  return { refreshed: true };
}

function invalidToken(config, value) {
  if (!record(value)) return false;
  const status = stringField(value.status);
  const errno = stringField(value.errno);
  if (status === '-1' && (errno === '10001' || (config.id === 'uc_tv' && errno === '11001'))) return true;
  return config.id === 'quark_tv' && /access[ _]token|token\s*无效/i.test(stringField(value.error_info) || stringField(value.message));
}

async function tvPlay(config, { file, mode }, ctx) {
  modeRequired(mode);
  storedFile(file);
  requireValue(stringField(ctx.credentials.deviceId), 'NOT_CONFIGURED');
  let credentials = ctx.credentials;
  if (!stringField(credentials.accessToken) || (credentials.expiresAt > 0 && credentials.expiresAt <= ctx.now() + 60_000)) {
    credentials = await ctx.refresh();
  }
  const resolve = async (current) => {
    requireValue(stringField(current.accessToken) && stringField(current.deviceId), 'NOT_CONFIGURED');
    const now = ctx.now();
    const timestamp = String(now);
    const url = new URL(config.api);
    url.search = new URLSearchParams({
      ...tvDevice(config, current, now), access_token: current.accessToken.trim(), method: mode,
      group_by: 'source', fid: file.fileId, resolution: 'low,normal,high,super,2k,4k', support: 'dolby_vision',
    }).toString();
    const response = await requestJSON(ctx, url.href, {
      headers: {
        'User-Agent': tvUA, Accept: 'application/json, text/plain, */*', 'x-pan-tm': timestamp,
        'x-pan-token': createHash('sha256').update(`GET&/file&${timestamp}&${config.signKey}`).digest('hex'),
        'x-pan-client-id': config.clientId,
      },
    });
    if (invalidToken(config, response.value)) return { invalid: true };
    httpOK(response);
    const value = response.value;
    requireValue(record(value), 'UPSTREAM_FORMAT');
    requireValue(value.errno === undefined || stringField(value.errno) === '0', 'UPSTREAM_ERROR');
    const data = value.data;
    requireValue(record(data), 'UPSTREAM_FORMAT');
    const raw = mode === 'download' ? data.download_url
      : (Array.isArray(data.video_info)
        ? data.video_info.find((item) => record(item) && typeof item.url === 'string' && item.url.trim())?.url : undefined);
    const result = typeof raw === 'string' ? raw.trim() : '';
    requireValue(/^https?:\/\//i.test(result), 'UPSTREAM_FORMAT');
    return { url: result };
  };
  let result = await resolve(credentials);
  if (result.invalid) {
    const latest = ctx.credentials;
    result = await resolve(latest.accessToken !== credentials.accessToken ? latest : await ctx.refresh());
  }
  requireValue(!result.invalid, 'AUTH_FAILED');
  return { url: result.url, headers: {}, mode, file };
}

function loginURL(config, url, base) {
  let parsed;
  try { parsed = new URL(url, base); } catch { throw new PanError('UPSTREAM_FORMAT'); }
  requireValue(parsed.protocol === 'https:' && config.loginHosts.has(parsed.hostname)
    && !parsed.username && !parsed.password && !parsed.port, 'UPSTREAM_FORMAT');
  return parsed;
}

function combineCookies(...cookies) {
  return cookies.reduce((result, cookie) => mergeCookie(result, cookie ? cookie.split(/;\s*/) : []), '');
}

async function qrRequest(config, session, ctx, input, options = {}) {
  let url = loginURL(config, input);
  let method = options.method || 'GET';
  let json = options.json;
  const visited = new Set();
  for (;;) {
    active(ctx);
    requireValue(!session.cancelled && ctx.now() < session.expiresAt, 'LOGIN_EXPIRED');
    const key = `${method} ${url.href}`;
    requireValue(!visited.has(key), 'UPSTREAM_FORMAT');
    visited.add(key);
    const cookie = combineCookies(session.jar.header(url.href), url.href === input ? options.cookie : '');
    const response = await requestText(ctx, url.href, {
      method, json, redirect: 'manual', timeout: Math.min(30_000, session.expiresAt - ctx.now()),
      headers: {
        'User-Agent': browserUA, Accept: options.accept || 'application/json, text/plain, */*',
        'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
        Referer: options.referer || `https://${config.host}/`, Origin: options.origin || url.origin,
        ...(cookie ? { Cookie: cookie } : {}),
      },
    });
    session.jar.set(url.href, response.headers['set-cookie'] || []);
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      requireValue(typeof response.headers.location === 'string' && response.headers.location, 'UPSTREAM_FORMAT');
      const next = loginURL(config, response.headers.location, url);
      if (response.status === 303 || ([301, 302].includes(response.status) && method === 'POST')) {
        method = 'GET';
        json = undefined;
      }
      url = next;
      continue;
    }
    httpOK(response);
    return response;
  }
}

function qrValue(response) {
  const value = parseJSON(response.text);
  requireValue(record(value), 'UPSTREAM_FORMAT');
  return value;
}

function loginQuery(config, ctx, pathname, token) {
  const now = ctx.now();
  const url = new URL(pathname, config.sso);
  url.search = new URLSearchParams({
    client_id: config.clientId, v: '1.2', __dt: String(1000 + Math.abs(now % 9000)),
    __t: String(now), request_id: randomUUID(), ...(token ? { token } : {}),
  }).toString();
  return url.href;
}

async function optionalLogin(action) {
  try { return await action(); } catch (error) {
    if (!['UPSTREAM_HTTP', 'UPSTREAM_ERROR', 'UPSTREAM_FORMAT', 'NETWORK_ERROR', 'TIMEOUT', 'AUTH_REQUIRED'].includes(error.code)) throw error;
    return undefined;
  }
}

async function loginStart(config, ctx) {
  active(ctx);
  const session = { provider: config.id, jar: new CookieJar(), expiresAt: ctx.now() + 180_000, cancelled: false };
  try {
    await optionalLogin(() => qrRequest(config, session, ctx, `https://${config.host}/`,
      config.id === 'uc' ? { accept: htmlAccept } : {}));
    await optionalLogin(() => qrRequest(config, session, ctx,
      `${config.sso}/cas/custom/login?custom_login_type=mobile&client_id=${config.clientId}&display=pc&v=1.2`,
      { referer: `${config.sso}/cas/custom/login`, ...(config.id === 'uc' ? { accept: htmlAccept } : {}) }));
    const response = qrValue(await qrRequest(config, session, ctx,
      loginQuery(config, ctx, '/cas/ajax/getTokenForQrcodeLogin'), { referer: `${config.sso}/cas/custom/login` }));
    session.token = firstText(response, ['token']);
    requireValue(session.token, 'UPSTREAM_FORMAT');
    let challengeURL = config.id === 'quark' ? firstText(response, ['qrcode_url', 'qrcodeurl', 'qr_url', 'qrurl']) : '';
    if (challengeURL) challengeURL = loginURL(config, challengeURL).href;
    else {
      const url = new URL(config.qrURL);
      url.searchParams.set('token', session.token);
      url.searchParams.set('client_id', config.clientId);
      if (config.id === 'quark') {
        url.searchParams.set('ssb', 'weblogin');
        url.searchParams.set('uc_param_str', '');
        url.searchParams.set('uc_biz_str', 'S:custom|OPT:SAREA@0|OPT:IMMERSIVE@1|OPT:BACK_BTN_STYLE@0');
      } else {
        url.searchParams.set('uc_param_str', 'dsdnfrpfbivesscpgimibtbmnijblauputogpintnwktprchmt');
        url.searchParams.set('uc_biz_str', 'S:custom|C:titlebar_fix');
      }
      challengeURL = url.href;
    }
    return { session, challenge: { type: 'url', url: challengeURL }, expiresAt: session.expiresAt };
  } catch (error) {
    loginCancel(session);
    throw error;
  }
}

async function quarkLoginCookie(config, session, ctx, ticket, redirect) {
  const query = `st=${encodeURIComponent(ticket)}&fr=pc&platform=pc`;
  const candidates = [
    ...(redirect ? [loginURL(config, redirect).href] : []),
    `https://drive.quark.cn/account/info?${query}`, `https://pan.quark.cn/account/info?${query}`,
    `https://drive-h.quark.cn/account/info?${query}`,
    `https://drive.quark.cn/?st=${encodeURIComponent(ticket)}`, `https://pan.quark.cn/?st=${encodeURIComponent(ticket)}`,
  ];
  for (const candidate of candidates) {
    await optionalLogin(() => qrRequest(config, session, ctx, candidate,
      { referer: `${new URL(candidate).origin}/`, accept: htmlAccept }));
    await optionalLogin(() => qrRequest(config, session, ctx, 'https://pan.quark.cn/'));
    await optionalLogin(() => qrRequest(config, session, ctx, 'https://drive.quark.cn/',
      { referer: 'https://drive.quark.cn/', accept: htmlAccept }));
    const validation = await optionalLogin(async () => qrValue(await qrRequest(config, session, ctx,
      apiURL(config, 'file/sort', { pdir_fid: '0', _fetch_total: '1', _size: '1', _sort: 'file_type:asc,file_name:asc' }),
      { origin: 'https://pan.quark.cn' })));
    if (validation) {
      if (stringField(validation.code) === '0') {
        const cookie = combineCookies(session.jar.header('https://pan.quark.cn/'), session.jar.header('https://drive.quark.cn/'));
        requireValue(cookie, 'AUTH_FAILED');
        return cookie;
      }
    }
  }
  throw new PanError('AUTH_FAILED');
}

async function ucLoginCookie(config, session, ctx, ticket) {
  const openCookie = session.jar.header(`${config.sso}/`);
  await optionalLogin(() => qrRequest(config, session, ctx,
    `https://drive.uc.cn/account/info?st=${encodeURIComponent(ticket)}&fr=pc&platform=pc`, { cookie: openCookie }));
  const combined = combineCookies(openCookie, session.jar.header('https://drive.uc.cn/'));
  await optionalLogin(() => qrRequest(config, session, ctx, apiURL(config, 'transfer/upload/pdir'),
    { method: 'POST', json: {}, cookie: combined }));
  const cookie = combineCookies(openCookie, session.jar.header('https://drive.uc.cn/'), session.jar.header('https://pc-api.uc.cn/'));
  requireValue(cookie && /(?:^|;\s*)(?:__)?pu{1,2}s=/i.test(cookie), 'AUTH_FAILED');
  return cookie;
}

async function loginPoll(config, session, ctx) {
  active(ctx);
  requireValue(record(session) && session.provider === config.id, 'INVALID_REQUEST');
  if (session.cancelled || ctx.now() >= session.expiresAt) {
    loginCancel(session);
    return { status: 'expired' };
  }
  if (session.cookie) return { status: 'confirmed', credentials: { cookie: session.cookie } };
  const response = qrValue(await qrRequest(config, session, ctx,
    loginQuery(config, ctx, '/cas/ajax/getServiceTicketByQrcodeToken', session.token),
    { referer: `${config.sso}/cas/custom/login` }));
  const status = stringField(field(response, ['status'])).toLowerCase();
  const message = firstText(response, ['message']);
  if (status === 'expired' || /过期|expired/i.test(message)) {
    loginCancel(session);
    return { status: 'expired' };
  }
  if (/验证码|captcha/i.test(message)) throw new PanError('CHALLENGE_REQUIRED');
  if (['error', 'failed', 'failure', 'denied', 'rejected'].includes(status)
    || /失败|错误|拒绝|无效|异常|不可用|繁忙|\b(?:error|fail(?:ed|ure)?|denied|invalid|rejected|unavailable)\b/i.test(message)) {
    throw new PanError('AUTH_FAILED');
  }
  const ticket = firstText(response, ['service_ticket']);
  if (status === '2000000' || ticket) {
    requireValue(ticket, 'UPSTREAM_FORMAT');
    session.cookie = config.id === 'quark'
      ? await quarkLoginCookie(config, session, ctx, ticket, firstText(response, ['redirect_url', 'redirecturl', 'redirect_uri', 'redirecturi']))
      : await ucLoginCookie(config, session, ctx, ticket);
    active(ctx);
    requireValue(!session.cancelled && ctx.now() < session.expiresAt, 'LOGIN_EXPIRED');
    return { status: 'confirmed', credentials: { cookie: session.cookie } };
  }
  if (/未(?:扫码|扫描)|(?:等待|待|请)(?:扫码|扫描)|\bnot[\s_-]+scanned\b|\bwaiting\b.*\bscan\b|\bpending\b/i.test(message)) {
    return { status: 'pending' };
  }
  if (status === 'scanned' || /已(?:扫码|扫描)|\bscanned\b/i.test(message)) return { status: 'scanned' };
  if (status === 'pending' || status === '50004001') return { status: 'pending' };
  throw new PanError('UPSTREAM_FORMAT');
}

function loginCancel(session) {
  session.cancelled = true;
  session.token = '';
  session.cookie = '';
  session.jar.clear();
}

function ordinaryProvider(config) {
  return {
    id: config.id, credentialFields: { cookie: 'string' }, modes: ['streaming', 'download'],
    list: (params, ctx) => list(config, params, ctx),
    prepare: (params, ctx) => prepare(config, params, ctx),
    play: (params, ctx) => ordinaryPlay(config, params, ctx),
    refresh: (_params, ctx) => refresh(config, ctx),
    loginStart: (_params, ctx) => loginStart(config, ctx),
    loginPoll: (session, ctx) => loginPoll(config, session, ctx),
    loginCancel,
  };
}

function tvProvider(config) {
  return {
    id: config.id,
    credentialFields: { refreshToken: 'string', deviceId: 'string', accessToken: 'string', expiresAt: 'number' },
    modes: ['streaming', 'download'],
    play: (params, ctx) => tvPlay(config, params, ctx),
    refresh: (_params, ctx) => tvRefresh(config, ctx),
  };
}

export const quark = ordinaryProvider(quarkConfig);
export const uc = ordinaryProvider(ucConfig);
export const quarkTV = tvProvider(quarkConfig.tv);
export const ucTV = tvProvider(ucConfig.tv);
