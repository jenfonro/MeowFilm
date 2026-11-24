import { randomUUID } from 'node:crypto';
import { PanError, requireValue } from '../errors.js';
import { delay, followRedirects, requestJSON, requestText } from '../http.js';
import { CookieJar, mergeCookie } from '../cookies.js';
import { decodeUTF8, parseJSON, text } from '../values.js';

const browserUA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0';
const htmlAccept = 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8';
const driveUA = (name) => `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) ${name}-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 Channel/pckk_other_ch`;
const quarkConfig = {
  id: 'quark', host: 'pan.quark.cn', api: 'https://drive.quark.cn/1/clouddrive/',
  query: { pr: 'ucpro', fr: 'pc' }, referer: 'https://pan.quark.cn/', ua: driveUA('quark'),
  sso: 'https://uop.quark.cn', clientId: '532', qrURL: 'https://su.quark.cn/4_eMHBJ',
};
const ucConfig = {
  id: 'uc', host: 'drive.uc.cn', api: 'https://pc-api.uc.cn/1/clouddrive/',
  query: { pr: 'UCBrowser', fr: 'pc' }, referer: 'https://drive.uc.cn', ua: driveUA('uc'),
  sso: 'https://api.open.uc.cn', clientId: '381', qrURL: 'https://su.uc.cn/1_n0ZCv',
};
const apiAuthCodes = new Set(['401', '31001', '40100']);
const authMessagePattern = /(?:not|need)\s*login|unauthoriz|未登录|请先?登录|登录已?(?:过期|失效)/i;

function record(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function active(ctx) {
  if (ctx.signal?.aborted) throw new PanError('CANCELLED');
}

function stringField(value) {
  return text(value).trim();
}

function pickField(containers, names, stringOnly = false) {
  const lower = names.map(name => name.toLowerCase());
  for (const container of containers) {
    if (!record(container)) continue;
    for (const target of lower) {
      for (const [key, value] of Object.entries(container)) {
        if (key.trim().toLowerCase() !== target) continue;
        if (stringOnly ? typeof value === 'string' && value.trim()
          : value !== undefined && value !== null && value !== '') return value;
      }
    }
  }
  return undefined;
}

function pickText(containers, names) {
  for (const name of names) {
    const value = pickField(containers, [name], true);
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return '';
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
    if (code !== '0' && code !== '200') {
      if (stringField(value.status) === '401' || apiAuthCodes.has(code) ||
          authMessagePattern.test(stringField(value.message ?? value.msg))) {
        throw new PanError('AUTH_REQUIRED');
      }
      throw new PanError('UPSTREAM_ERROR');
    }
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
  if (next && next !== current) await ctx.updateCredentials({ cookie: next });
}

async function webJSON(config, ctx, url, options = {}) {
  active(ctx);
  const response = await requestJSON(ctx, url, {
    ...options, headers: webHeaders(config, ctx.credentials.cookie),
  });
  httpOK(response);
  const value = apiOK(response.value);
  await persistCookie(ctx, response.headers);
  return value;
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
  if (value.code !== 'OK' || !value.success) {
    const upper = value.code.toUpperCase();
    if (stringField(value.status) === '401' || apiAuthCodes.has(upper) || upper === 'UNLOGIN' || upper === 'NOT_LOGIN' ||
        authMessagePattern.test(stringField(value.message ?? value.msg))) {
      throw new PanError('AUTH_REQUIRED');
    }
    throw new PanError('UPSTREAM_ERROR');
  }
  requireValue(record(value.data), 'UPSTREAM_FORMAT');
  requireValue(Object.keys(value.data).length > 0, 'AUTH_REQUIRED');
  await persistCookie(ctx, response.headers);
  return { refreshed: true };
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
      const token = pickText([response.data, response], ['stoken']);
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

function videoMetadata(item) {
  const source = record(item.video_meta) ? { ...item, ...item.video_meta } : item;
  const number = value => typeof value === 'number' || typeof value === 'string' && value.trim()
    ? Number(value) : NaN;
  const width = number(source.video_width ?? source.width ?? source.vwidth);
  const height = number(source.video_height ?? source.height ?? source.vheight);
  const dimensions = Number.isSafeInteger(width) && width > 0 && Number.isSafeInteger(height) && height > 0;
  const rawFPS = source.fps ?? source.frameRate ?? source.frame_rate;
  const rational = /^(\d+)\/(\d+)$/.exec(String(rawFPS));
  const fps = rational ? Number(rational[1]) / Number(rational[2]) : number(rawFPS);
  const codec = source.video_codec ?? source.codec;
  const short = dimensions ? Math.min(width, height) : 0;
  const resolution = dimensions ? `${width}x${height}`
    : typeof source.resolution === 'string' && source.resolution.trim() ? source.resolution.trim() : undefined;
  const quality = typeof source.quality === 'string' && source.quality.trim() || (short >= 4320 ? '8K'
    : short >= 2160 ? '4K' : short >= 1440 ? '2K' : short >= 1080 ? '1080P' : short >= 720 ? '720P' : '');
  return {
    ...(quality ? { quality } : {}),
    ...(resolution === undefined ? {} : { resolution }),
    ...(Number.isFinite(fps) && fps > 0 && fps < 1000 ? { fps } : {}),
    ...(typeof codec === 'string' && codec.trim() ? { codec: codec.trim() } : {}),
  };
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
      || (hasMore !== false && data.list.length > 0
        && (total !== undefined ? BigInt(seen.size) < total : data.list.length >= size));
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
        const entry = { name, path, file, ...videoMetadata(item) };
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
    httpOK(response);
    await persistCookie(ctx, response.headers);
  });
  await optionalLogin(() => webJSON(config, ctx, apiURL(config, 'transfer/upload/pdir'), { method: 'POST', json: {} }));
}

function savedId(value) {
  const names = new Set(['save_as_top_fids', 'save_as_top_fid', 'save_as_select_top_fids']);
  const containers = [value?.data?.save_as, value?.data, value?.save_as, value];
  let saved = '';
  for (const current of containers) {
    if (!record(current)) continue;
    for (const [name, entry] of Object.entries(current)) {
      if (!names.has(name.toLowerCase())) continue;
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
  const taskId = stringField(pickField([result.data, result], ['task_id', 'taskid']));
  requireValue(taskId, 'UPSTREAM_FORMAT');
  const expiresAt = ctx.now() + 30_000;
  for (;;) {
    active(ctx);
    const remaining = expiresAt - ctx.now();
    requireValue(remaining > 0, 'TIMEOUT');
    result = await webJSON(config, ctx, apiURL(config, 'task', { task_id: taskId }), {
      timeout: remaining,
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
    const url = pickText([data], [want, 'download_url', 'play_url', 'url']);
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

function loginURL(url, base) {
  let parsed;
  try { parsed = new URL(url, base); } catch { throw new PanError('UPSTREAM_FORMAT'); }
  requireValue(['http:', 'https:'].includes(parsed.protocol)
    && !parsed.username && !parsed.password, 'UPSTREAM_FORMAT');
  return parsed;
}

async function qrRequest(config, session, ctx, input, options = {}) {
  const initial = loginURL(input);
  const response = await followRedirects(ctx, initial.href, {
    method: options.method || 'GET',
    json: options.json,
    jar: session.jar,
    timeout: 30_000,
    expiresAt: session.expiresAt,
    isCancelled: () => requireValue(!session.cancelled, 'LOGIN_EXPIRED'),
    validateStatus: httpOK,
    persistentHeaders: {
      'User-Agent': browserUA,
      Accept: options.accept || 'application/json, text/plain, */*',
      'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
    },
    headers: {
      Referer: options.referer || `https://${config.host}/`,
      Origin: options.origin || initial.origin,
    },
  });
  return { ...response, text: decodeUTF8(response.body) };
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
    if (!['UPSTREAM_HTTP', 'UPSTREAM_ERROR', 'UPSTREAM_FORMAT', 'AUTH_REQUIRED'].includes(error.code)) throw error;
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
    const containers = [response.data?.members, response.data, response];
    session.token = pickText(containers, ['token']);
    requireValue(session.token, 'UPSTREAM_FORMAT');
    let challengeURL = config.id === 'quark' ? pickText(containers, ['qrcode_url', 'qrcodeurl', 'qr_url', 'qrurl']) : '';
    if (challengeURL) challengeURL = loginURL(challengeURL).href;
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
    ...(redirect ? [loginURL(redirect).href] : []),
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
        const cookie = session.jar.header(config.api);
        requireValue(cookie, 'AUTH_FAILED');
        return cookie;
      }
    }
  }
  throw new PanError('AUTH_FAILED');
}

async function ucLoginCookie(config, session, ctx, ticket) {
  await optionalLogin(() => qrRequest(config, session, ctx,
    `https://drive.uc.cn/account/info?st=${encodeURIComponent(ticket)}&fr=pc&platform=pc`));
  await optionalLogin(() => qrRequest(config, session, ctx, apiURL(config, 'transfer/upload/pdir'),
    { method: 'POST', json: {} }));
  const cookie = session.jar.header(config.api);
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
  const topFirst = [response, response.data, response.data?.members];
  const memberFirst = [response.data?.members, response.data, response];
  const status = stringField(pickField(topFirst, ['status'])).toLowerCase();
  const message = pickText(topFirst, ['message']);
  if (status === 'expired' || /过期|expired/i.test(message)) {
    loginCancel(session);
    return { status: 'expired' };
  }
  if (/验证码|captcha/i.test(message)) throw new PanError('CHALLENGE_REQUIRED');
  if (['error', 'failed', 'failure', 'denied', 'rejected'].includes(status)
    || /失败|错误|拒绝|无效|异常|不可用|繁忙|\b(?:error|fail(?:ed|ure)?|denied|invalid|rejected|unavailable)\b/i.test(message)) {
    throw new PanError('AUTH_FAILED');
  }
  const ticket = pickText(memberFirst, ['service_ticket']);
  if (status === '2000000' || ticket) {
    requireValue(ticket, 'UPSTREAM_FORMAT');
    session.cookie = config.id === 'quark'
      ? await quarkLoginCookie(config, session, ctx, ticket, pickText(memberFirst, ['redirect_url', 'redirecturl', 'redirect_uri', 'redirecturi']))
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

export const quark = ordinaryProvider(quarkConfig);
export const uc = ordinaryProvider(ucConfig);
