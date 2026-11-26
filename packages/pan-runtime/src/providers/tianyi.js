import { constants, publicEncrypt } from 'node:crypto';
import { PanError, requireValue } from '../errors.js';
import { CookieJar } from '../cookies.js';
import { followRedirects } from '../http.js';
import { parseJSON, text } from '../values.js';

const cloud = 'https://cloud.189.cn';
const authOrigin = 'https://open.e.189.cn';
const ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/144.0.0.0 Safari/537.36';
const video = /\.(?:mp4|mkv|mov|iso|flv|ts|m2ts|avi|wmv|rmvb|webm|m4v|mpg|mpeg)$/i;
const pageSize = 200;
const authFailureCodes = new Set(['-2', '-10101', '-10102', '-10103', '-10104', '10002', '10003']);

function active(ctx) {
  if (ctx.signal?.aborted) throw new PanError('CANCELLED');
}

function record(value) {
  requireValue(value !== null && typeof value === 'object' && !Array.isArray(value), 'UPSTREAM_FORMAT');
  return value;
}

function url(value, base, code = 'UPSTREAM_FORMAT') {
  try {
    const result = new URL(value, base);
    requireValue(['https:', 'http:'].includes(result.protocol), code);
    return result;
  } catch {
    throw new PanError(code);
  }
}

function identity(value) {
  const result = text(value).trim();
  requireValue(result, 'UPSTREAM_FORMAT');
  return result;
}

function flag(value) {
  if (typeof value === 'number') return Math.trunc(value) !== 0;
  return value === true || ['1', 'true', 'yes'].includes(text(value).trim().toLowerCase());
}

function clientType(value) {
  if (typeof value === 'number') return String(Math.trunc(value));
  const raw = text(value).trim();
  return /^[+-]?\d+$/.test(raw) ? BigInt(raw).toString() : '10020';
}

function joinPath(parent, name) {
  return `${parent === '/' ? '' : parent}/${name}`;
}

function resultOK(value) {
  return ['0', '00', '0000'].includes(text(value).trim());
}

function canLogin(credentials) {
  return typeof credentials.username === 'string' && credentials.username.trim().length > 0
    && typeof credentials.password === 'string' && credentials.password.trim().length > 0;
}

async function confirmCookie(ctx) {
  const original = ctx.credentials.cookie;
  requireValue(typeof original === 'string' && original.trim(), 'NOT_CONFIGURED');
  const jar = new CookieJar();
  jar.set(`${cloud}/`, original.split(';').map(value => `${value.trim()}; Path=/`));
  try {
    const target = new URL('/api/portal/loginUrl.action', cloud);
    target.search = new URLSearchParams({ redirectURL: `${cloud}/main.action` }).toString();
    const response = await loginRequest(ctx, jar, target.href, {
      authentication: true, headers: { Referer: `${cloud}/`, Accept: 'text/html,application/xhtml+xml' },
    });
    const terminal = url(response.url);
    if (terminal.origin === authOrigin && terminal.pathname === '/api/logbox/separate/web/index.html') {
      throw new PanError('AUTH_REQUIRED');
    }
    requireValue(terminal.origin === cloud && terminal.pathname === '/web/main', 'UPSTREAM_FORMAT');
    const cookie = jar.header(`${cloud}/`);
    if (cookie && cookie !== original) await ctx.updateCredentials({ cookie });
    return { refreshed: true };
  } finally {
    jar.clear();
  }
}

function shareInput(share) {
  requireValue(share && typeof share.url === 'string');
  requireValue(share.password === undefined || typeof share.password === 'string');
  const parsed = url(share.url, undefined, 'INVALID_REQUEST');
  requireValue(['cloud.189.cn', 'h5.cloud.189.cn'].includes(parsed.hostname));
  const path = parsed.hostname === 'cloud.189.cn' && /^\/t\/([A-Za-z0-9]+)\/?$/.exec(parsed.pathname);
  const query = parsed.hash.indexOf('?');
  const fragment = new URLSearchParams(query === -1 ? '' : parsed.hash.slice(query + 1));
  const shareId = path?.[1] || parsed.searchParams.get('shareCode') || fragment.get('shareCode');
  requireValue(shareId && /^[A-Za-z0-9]+$/.test(shareId));
  return {
    id: shareId, url: `${cloud}/t/${shareId}`,
    password: share.password ?? parsed.searchParams.get('accessCode') ?? fragment.get('accessCode') ?? '',
  };
}

async function ensureCookie(ctx) {
  if (typeof ctx.credentials.cookie === 'string' && ctx.credentials.cookie.trim()) return ctx.credentials.cookie;
  requireValue(canLogin(ctx.credentials), 'NOT_CONFIGURED');
  await ctx.refresh();
  requireValue(typeof ctx.credentials.cookie === 'string' && ctx.credentials.cookie.trim(), 'AUTH_REQUIRED');
  return ctx.credentials.cookie;
}

async function api(ctx, path, query, { mode, referer = `${cloud}/` } = {}) {
  const target = new URL(path, cloud);
  target.search = new URLSearchParams(query).toString();
  for (let attempt = 0; ; attempt++) {
    active(ctx);
    const cookie = await ensureCookie(ctx);
    const response = await ctx.http.request({
      url: target.href, method: 'GET', signal: ctx.signal,
      headers: { 'User-Agent': ua, Accept: 'application/json;charset=UTF-8', 'Accept-Encoding': 'identity', Referer: referer, Cookie: cookie },
    });
    active(ctx);
    const httpOK = response.status >= 200 && response.status < 300;
    let value, parseError;
    try { value = record(parseJSON(response.body)); }
    catch (error) {
      if (httpOK && mode !== 'streaming') throw error;
      parseError = error;
    }
    const code = value?.res_code ?? (mode ? value?.resCode : undefined);
    const success = text(code).trim() === '0';
    const failed = !httpOK || parseError || !success;
    const message = failed
      ? (!httpOK || parseError || mode === 'streaming' ? response.body.toString('utf8')
        : [code, value.res_message, ...(mode ? [value.resMessage] : [])].map(text).join(' '))
      : '';
    if (failed && (response.status === 401 || /invalidsessionkey|usersessionbo is null|session expired|session失效|会话失效/i.test(message))) {
      if (attempt === 0 && canLogin(ctx.credentials)) {
        await ctx.refresh();
        continue;
      }
      throw new PanError('AUTH_REQUIRED');
    }
    if (failed && /captcha|验证码|validatecode|challenge/i.test(message)) {
      throw new PanError('CHALLENGE_REQUIRED');
    }
    if (mode && (!httpOK || parseError) && /filetoolarge|file too large/i.test(message)) {
      return { tooLarge: true };
    }
    requireValue(httpOK, 'UPSTREAM_HTTP');
    if (parseError) throw parseError;
    requireValue(code !== undefined, 'UPSTREAM_FORMAT');
    if (!success) {
      if (/accesscode|访问码|提取码|密码/i.test(message)) throw new PanError('AUTH_REQUIRED');
      throw new PanError('UPSTREAM_ERROR');
    }
    return { value };
  }
}

function loginRequest(ctx, jar, value, options = {}) {
  return followRedirects(ctx, url(value).href, {
    method: options.method ?? 'GET',
    form: options.form,
    jar,
    persistentHeaders: { 'User-Agent': ua, 'Accept-Encoding': 'identity' },
    headers: options.headers ?? {},
    validateStatus: response => requireValue(response.status >= 200 && response.status < 300,
      options.authentication && response.status === 401 ? 'AUTH_REQUIRED' : 'UPSTREAM_HTTP'),
  });
}

function loginJSON(response, { authentication = false } = {}) {
  const value = record(parseJSON(response.body));
  const code = text(value.result).trim();
  requireValue(/^-?\d+$/.test(code), 'UPSTREAM_FORMAT');
  if (!resultOK(code)) {
    const message = text(value.msg);
    if (/captcha|验证码|validatecode|challenge|短信|otp/i.test(message)) throw new PanError('CHALLENGE_REQUIRED');
    if (authentication && !/繁忙|维护|系统异常|系统错误|稍后再试|busy|maintenance|temporarily/i.test(message) &&
        (authFailureCodes.has(code) ||
          /密码|用户名|账号|帐号|不存在|冻结|禁用|锁定|登录失败|认证失败|password|username|account|credential|invalid|incorrect|locked|frozen/i.test(message))) {
      throw new PanError('AUTH_FAILED');
    }
    throw new PanError('UPSTREAM_ERROR');
  }
  return value;
}

function encrypt(publicKey, value) {
  try {
    const key = publicKey.includes('BEGIN PUBLIC KEY') ? publicKey : `-----BEGIN PUBLIC KEY-----\n${publicKey}\n-----END PUBLIC KEY-----`;
    return publicEncrypt({ key, padding: constants.RSA_PKCS1_PADDING }, Buffer.from(value, 'utf8')).toString('hex').toUpperCase();
  } catch {
    throw new PanError('UPSTREAM_FORMAT');
  }
}

function fileReference(file) {
  requireValue(file && file.kind === 'share');
  for (const field of ['fileId', 'shareId', 'name', 'path', 'shareCode']) {
    requireValue(typeof file[field] === 'string' && file[field].length > 0);
  }
  requireValue(typeof file.password === 'string' && file.path.startsWith('/') && !file.path.includes('\0'));
  return file;
}

function size(value) {
  if (value === undefined || value === null) return {};
  const raw = text(value);
  requireValue(/^\d+$/.test(raw), 'UPSTREAM_FORMAT');
  return { size: BigInt(raw) <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(raw) : raw };
}

async function mediaURL(ctx, value) {
  const response = await followRedirects(ctx, url(value).href, {
    method: 'GET',
    responseMode: 'headers',
    persistentHeaders: { 'User-Agent': ua, Range: 'bytes=0-0', 'Accept-Encoding': 'identity' },
  });
  return response.url;
}

export const tianyi = {
  id: 'tianyi',
  credentialFields: { cookie: 'string', username: 'string', password: 'string' },
  modes: ['streaming', 'download'],

  async refresh({}, ctx) {
    if (!canLogin(ctx.credentials)) return confirmCookie(ctx);
    const { username, password } = ctx.credentials;
    const jar = new CookieJar();
    try {
      const encryption = loginJSON(await loginRequest(ctx, jar,
        `${authOrigin}/api/logbox/config/encryptConf.do?${new URLSearchParams({ appId: '8025431004', timeStamp: String(ctx.now()) })}`,
        { headers: { Referer: authOrigin, Accept: 'application/json, text/plain, */*' } }));
      const config = record(encryption.data);
      const publicKey = identity(config.pubKey);
      const loginURL = `${cloud}/api/portal/loginUrl.action?${new URLSearchParams({ redirectURL: `${cloud}/main.action` })}`;
      const login = await loginRequest(ctx, jar, loginURL, { headers: { Referer: `${cloud}/` } });
      const contextURL = url(login.url);
      const lt = contextURL.searchParams.get('lt')?.trim();
      const reqId = contextURL.searchParams.get('reqId')?.trim();
      const appId = contextURL.searchParams.get('appId')?.trim() || 'cloud';
      requireValue(lt && reqId, 'UPSTREAM_FORMAT');
      const headers = { Lt: lt, Reqid: reqId, Referer: authOrigin };
      const application = loginJSON(await loginRequest(ctx, jar, `${authOrigin}/api/logbox/oauth2/appConf.do`, {
        method: 'POST', headers, form: { version: '2.0', appKey: appId },
      }));
      const app = record(application.data);
      const returnUrl = identity(app.returnUrl);
      const paramId = identity(app.paramId);
      const prefix = text(config.pre).trim() || '{RSA}';
      const submit = loginJSON(await loginRequest(ctx, jar, `${authOrigin}/api/logbox/oauth2/loginSubmit.do`, {
        method: 'POST', headers, authentication: true, form: {
          appKey: appId, version: 'v2.0', apToken: '', accountType: text(app.accountType).trim() || '01',
          userName: prefix + encrypt(publicKey, username.trim()), password: prefix + encrypt(publicKey, password),
          validateCode: '', captchaToken: '', returnUrl, mailSuffix: text(app.mailSuffix).trim() || '@189.cn',
          paramId, dynamicCheck: 'FALSE', clientType: clientType(app.clientType), cb_SaveName: '3',
          isOauth2: flag(app.isOauth2) ? 'true' : 'false', state: '',
        },
      }), { authentication: true });
      await loginRequest(ctx, jar, identity(submit.toUrl));
      const cookie = jar.header(`${cloud}/`);
      requireValue(cookie, 'UPSTREAM_FORMAT');
      await ctx.updateCredentials({ cookie });
      return { refreshed: true };
    } finally {
      jar.clear();
    }
  },

  async list({ share }, ctx) {
    const parsed = shareInput(share);
    const accessCode = parsed.password.trim();
    let shareId;
    if (accessCode) {
      const checked = await api(ctx, '/api/open/share/checkAccessCode.action', {
        key: 'noCache', shareCode: parsed.id, accessCode,
      }, { referer: `${cloud}/web/share?code=${parsed.id}` });
      shareId = identity(checked.value.shareId);
    }
    const { value: info } = await api(ctx, '/api/open/share/getShareInfoByCodeV2.action', {
      key: 'noCache', shareCode: parsed.id, ...(accessCode ? { accessCode } : {}),
    }, { referer: parsed.url });
    if (flag(info.needAccessCode) && !accessCode) throw new PanError('AUTH_REQUIRED');
    if (!shareId) {
      shareId = text(info.shareId).trim();
      requireValue(shareId, accessCode ? 'UPSTREAM_FORMAT' : 'AUTH_REQUIRED');
    }
    const rootId = text(info.fileId).trim() || '0';
    const rootName = info.fileName;
    requireValue(rootName === undefined || typeof rootName === 'string', 'UPSTREAM_FORMAT');
    const files = [];
    const emitted = new Set();
    const addFile = (item, parent) => {
      const fileId = identity(item.id);
      requireValue(typeof item.name === 'string' && item.name.length > 0, 'UPSTREAM_FORMAT');
      if (!video.test(item.name.trim()) || emitted.has(fileId)) return;
      emitted.add(fileId);
      const path = joinPath(parent, item.name);
      files.push({
        name: item.name, path, ...size(item.size),
        file: { kind: 'share', fileId, shareId, shareCode: parsed.id, password: parsed.password, name: item.name, path },
      });
    };
    if (info.isFolder === false || info.isFolder === 0 || info.isFolder === '0' || info.isFolder === 'false') {
      identity(info.fileId);
      addFile({ id: rootId, name: rootName, size: info.size }, '/');
      return { share: parsed, files };
    }
    const queue = [{ id: rootId, path: rootName ? joinPath('/', rootName) : '/' }];
    const directories = new Set([rootId]);
    let rootEmpty = false;
    for (let index = 0; index < queue.length; index++) {
      const current = queue[index];
      const seen = new Set();
      let total;
      for (let page = 1; ; page++) {
        const { value } = await api(ctx, '/api/open/share/listShareDir.action', {
          key: 'noCache', noCache: String(ctx.now()), shareId, fileId: current.id, shareDirFileId: current.id,
          isFolder: 'true', iconOption: '5', shareMode: text(info.shareMode).trim() || '3',
          pageNum: String(page), pageSize: String(pageSize), orderBy: 'lastOpTime', descending: 'true',
          ...(accessCode ? { accessCode } : {}),
        });
        const list = record(value.fileListAO);
        const plain = list.fileList;
        const folders = list.folderList;
        requireValue(Array.isArray(plain) || Array.isArray(folders), 'UPSTREAM_FORMAT');
        requireValue((plain == null || Array.isArray(plain)) && (folders == null || Array.isArray(folders)), 'UPSTREAM_FORMAT');
        const items = [
          ...(folders ?? []).map(item => ({ item: record(item), directory: true })),
          ...(plain ?? []).map(item => {
            record(item);
            const folder = item.isFolder ?? item.isfolder;
            return { item, directory: folder === true || ['1', 'true'].includes(text(folder).trim()) };
          }),
        ];
        let added = 0;
        for (const entry of items) {
          const item = entry.item;
          const fileId = identity(item.id);
          requireValue(typeof item.name === 'string' && item.name.length > 0, 'UPSTREAM_FORMAT');
          if (seen.has(fileId)) continue;
          seen.add(fileId);
          added++;
          if (entry.directory) {
            if (!directories.has(fileId)) {
              directories.add(fileId);
              queue.push({ id: fileId, path: joinPath(current.path, item.name) });
            }
          } else {
            addFile(item, current.path);
          }
        }
        const count = list.count ?? list.Count;
        if (count !== undefined) {
          requireValue(/^\d+$/.test(text(count)), 'UPSTREAM_FORMAT');
          const declared = BigInt(text(count));
          if (declared > 0n && (total === undefined || declared > total)) total = declared;
        }
        const more = total === undefined ? items.length >= pageSize : total > BigInt(seen.size);
        if (!more || !items.length || !added) break;
      }
      if (index === 0) rootEmpty = seen.size === 0;
    }
    if (rootEmpty && info.isFolder == null && rootName && info.fileId !== undefined) {
      addFile({ id: rootId, name: rootName, size: info.size }, '/');
    }
    return { share: parsed, files };
  },

  async play({ file, mode }, ctx) {
    requireValue(['streaming', 'download'].includes(mode), 'UNSUPPORTED_OPERATION');
    fileReference(file);
    const accessCode = file.password.trim();
    for (const dt of ['1', '3']) {
      const streaming = mode === 'streaming';
      const { value, tooLarge } = await api(ctx, streaming
        ? '/api/portal/getNewVlcVideoPlayUrl.action' : '/api/open/file/getFileDownloadUrl.action', {
        shareId: file.shareId, fileId: file.fileId, dt,
        ...(streaming ? { type: '4' } : accessCode ? { accessCode } : {}),
      }, { mode });
      if (tooLarge) {
        requireValue(dt === '1', 'UPSTREAM_ERROR');
        continue;
      }
      let link;
      if (streaming) {
        link = text(value.fileDownloadUrl).trim() || text(value.normal?.url).trim();
      } else {
        const nested = Object.hasOwn(value, 'data') ? value.data : value.Data;
        const data = nested !== null && typeof nested === 'object' && !Array.isArray(nested) ? nested : value;
        for (const field of ['fileDownloadUrl', 'downloadUrl', 'url', 'fileDownloadUrlHttp', 'fileDownloadUrlHttps']) {
          const candidate = typeof data[field] === 'string' ? data[field].trim() : '';
          if (candidate) { link = candidate; break; }
        }
      }
      requireValue(typeof link === 'string' && link.length > 0, 'UPSTREAM_FORMAT');
      return { url: await mediaURL(ctx, link), headers: { 'User-Agent': ua }, mode, file };
    }
  },
};
