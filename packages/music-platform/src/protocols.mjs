// SPDX-License-Identifier: GPL-3.0-only
// Fixed api-enhanced / KuGouMusicApi / QQMusicApi recipes; see LICENSES/NOTICE.md.
import { createHash, createCipheriv, createDecipheriv, publicEncrypt, constants, randomBytes, randomInt } from 'node:crypto';
import { gunzipSync } from 'node:zlib';
import { fail } from './errors.mjs';
import { decodeJSON, encodeProtocolJSON, protocolInteger, required, scalar } from './model.mjs';

export const md5 = value => createHash('md5').update(value).digest('hex');
export const hex = size => randomBytes(size).toString('hex');
const formValue = value => typeof value === 'boolean' ? String(value) : scalar(value);
export const form = fields => new URLSearchParams(Object.entries(fields).map(([key, value]) => [key, formValue(value)])).toString();
export const cookieFields = header => Object.fromEntries((header ?? '').split(';').filter(part => part.includes('='))
  .map(part => { const at = part.indexOf('='); return [part.slice(0, at).trim(), part.slice(at + 1).trim()]; }));
export const cookieHeader = fields => Object.entries(fields).map(([key, value]) => `${key}=${value}`).join('; ');
export function responseCookies(rows, allowed, previous = {}, clock = Date.now, host) {
  const result = { ...previous };
  for (const row of rows) {
    const fields = row.split(';'), at = fields[0].indexOf('=');
    if (at <= 0) continue;
    const name = fields[0].slice(0, at).trim();
    if (!allowed.includes(name)) continue;
    const value = fields[0].slice(at + 1).trim();
    const attributes = Object.fromEntries(fields.slice(1).map(item => {
      const eq = item.indexOf('=');
      return eq < 0 ? [item.trim().toLowerCase(), ''] : [item.slice(0, eq).trim().toLowerCase(), item.slice(eq + 1).trim()];
    }));
    if (host && attributes.domain) {
      const domain = attributes.domain.replace(/^\./, '').toLowerCase();
      if (domain !== host && !host.endsWith('.' + domain)) fail('REDIRECT_DENIED');
    }
    const expired = attributes['max-age'] != null && Number(attributes['max-age']) <= 0 ||
      attributes.expires && Date.parse(attributes.expires) <= clock();
    if (!value || expired) delete result[name]; else result[name] = value;
  }
  return result;
}
export function aes(value, key, mode = 'ecb', iv = null) {
  const cipher = createCipheriv(`aes-${Buffer.byteLength(key) * 8}-${mode}`, Buffer.from(key), iv && Buffer.from(iv));
  return Buffer.concat([cipher.update(value), cipher.final()]);
}
export function decrypt(value, key, mode = 'ecb', iv = null) {
  const cipher = createDecipheriv(`aes-${Buffer.byteLength(key) * 8}-${mode}`, Buffer.from(key), iv && Buffer.from(iv));
  return Buffer.concat([cipher.update(value), cipher.final()]);
}
const wyPublicKey = `-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB
-----END PUBLIC KEY-----`;
export const kgPublicKey = `-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB
-----END PUBLIC KEY-----`;
export const kgSalt = 'OIlwieks28dk2k092lksi2UIkp';
export function wyRequest(path, params, client, account, clock, weapi = false, token, vip = false) {
  const cookies = { ...client.cookies, ...cookieFields(account?.cookie) };
  const data = { ...params, e_r: vip };
  let body, headers, origin;
  if (weapi) {
    const alphabet = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
    const key = Array.from({ length: 16 }, () => alphabet[randomInt(alphabet.length)]).join('');
    data.csrf_token = cookies.__csrf ?? '';
    const inner = aes(encodeProtocolJSON(data), '0CoJUm6Qyw8W8jud', 'cbc', '0102030405060708').toString('base64');
    const encrypted = aes(inner, key, 'cbc', '0102030405060708').toString('base64');
    const message = Buffer.alloc(128);
    Buffer.from([...key].reverse().join('')).copy(message, 112);
    body = form({ params: encrypted, encSecKey: publicEncrypt({ key: wyPublicKey, padding: constants.RSA_NO_PADDING }, message).toString('hex') });
    origin = 'https://music.163.com';
    headers = { Cookie: cookieHeader(cookies),
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36' };
  } else {
    const now = clock();
    const header = { os: 'pc', appver: vip ? '3.1.42.205594' : '3.1.17.204416', osver: 'Microsoft-Windows-10-Professional-build-19045-64bit',
      channel: 'netease', versioncode: '140', __csrf: cookies.__csrf ?? '', resolution: '1920x1080',
      buildver: String(Math.floor(now / 1000)), requestId: `${now}_${String(randomInt(1000)).padStart(3, '0')}` };
    for (const name of ['MUSIC_U', 'MUSIC_A', 'NMTID', 'deviceId', '_ntes_nuid', '_ntes_nnid', 'WNMCID'])
      if (cookies[name] !== undefined) header[name] = cookies[name];
    if (token !== undefined) header['X-antiCheatToken'] = token;
    data.header = header;
    const payload = encodeProtocolJSON(data);
    body = form({ params: aes(`${path}-36cd479b6b5-${payload}-36cd479b6b5-${md5(`nobody${path}use${payload}md5forencrypt`)}`,
      'e82ckenh8dichen8').toString('hex').toUpperCase() });
    origin = vip ? 'https://interface.music.163.com' : 'https://interfacepc.music.163.com';
    headers = { Cookie: cookieHeader(header),
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/3.1.29.205117' };
  }
  return { url: origin + (weapi ? '/weapi/' : '/eapi/') + path.slice(5), method: 'POST', body,
    headers: { ...headers, Referer: 'https://music.163.com/', 'Content-Type': 'application/x-www-form-urlencoded' } };
}
export function encryptedWyResponse(bytes, maximumBytes = 4 * 1024 * 1024) {
  try {
    let plain = decrypt(bytes, 'e82ckenh8dichen8');
    if (plain[0] === 0x1f && plain[1] === 0x8b) {
      const inflated = gunzipSync(plain, { maxOutputLength: maximumBytes, info: true });
      if (inflated.engine.bytesWritten !== plain.length) fail('INVALID_RESPONSE');
      plain = inflated.buffer;
    }
    return decodeJSON(plain);
  } catch (error) { fail(error.code === 'ERR_BUFFER_TOO_LARGE' ? 'LIMIT' : 'INVALID_RESPONSE'); }
}
export function wySuccess(data) {
  for (const [code, nested] of [[data.code, false], [data.data?.code, true]]) {
    if (nested && (data.data == null || !Object.hasOwn(Object(data.data), 'code'))) continue;
    if (!Number.isInteger(code)) fail('INVALID_RESPONSE');
    if (code !== 200 && !(nested && code === 0)) fail(({ 301: 'AUTH_REQUIRED', 401: 'AUTH_REQUIRED', 403: 'FORBIDDEN',
      404: 'NOT_FOUND', 405: 'RATE_LIMITED', 429: 'RATE_LIMITED', 509: 'RATE_LIMITED', 250: 'VERIFICATION_REQUIRED',
      460: 'VERIFICATION_REQUIRED', 461: 'VERIFICATION_REQUIRED' })[code] ?? 'UPSTREAM_ERROR');
  }
  return data;
}
export function txRequest(module, method, params, client, account, clock, options = {}) {
  const now = Math.floor(clock() / 1000), cookies = cookieFields(account?.cookie);
  const comm = { ct: '11', cv: '20090008', v: '20090008', chid: '10003505', tmeAppID: 'qqmusic', ...options.comm };
  if (client.qimei) Object.assign(comm, { traceid: `10002_${client.openUdid}_${now}`, QIMEI36: client.qimei.q36,
    OpenUDID: client.openUdid, udid: client.openUdid, OpenUDID2: client.openUdid2, aid: client.androidId,
    os_ver: '15', phonetype: 'V2408A' });
  if (client.session) Object.assign(comm, { uid: client.session.uid, sid: client.session.sid });
  if (account) {
    const musicid = cookies.musicid ?? cookies.str_musicid;
    if (!musicid || !cookies.musickey) fail('INVALID_CREDENTIALS');
    Object.assign(comm, { qq: musicid, authst: cookies.musickey,
      tmeLoginType: protocolInteger(cookies.login_type ?? (cookies.musickey.startsWith('W_X') ? '1' : '2')),
      traceid: `10002_${musicid}_${now}` });
  }
  return { url: 'https://u.y.qq.com/cgi-bin/musicu.fcg', method: 'POST',
    headers: { 'Content-Type': 'application/json', 'User-Agent': 'QQMusic 20090008(android 15)',
      ...(account ? { Cookie: cookieHeader({ uin: cookies.musicid ?? cookies.str_musicid,
        qqmusic_uin: cookies.musicid ?? cookies.str_musicid, qm_keyst: cookies.musickey, qqmusic_key: cookies.musickey }) } : {}) },
    body: encodeProtocolJSON({ comm, req_0: { module, method, param: params } }) };
}
export function txSuccess(data) {
  for (const value of [data.code, data.req_0?.code]) {
    const code = required(value);
    if (!Number.isInteger(Number(code))) fail('INVALID_RESPONSE');
    if (code !== '0') fail(({ 1000: 'AUTH_REQUIRED', 104400: 'AUTH_REQUIRED', 104401: 'AUTH_REQUIRED',
      2000: 'SIGNATURE_REQUIRED', 2001: 'RATE_LIMITED', 20277: 'FORBIDDEN', 20278: 'FORBIDDEN',
      20450: 'FORBIDDEN', 20279: 'DEVICE_LIMIT', 20271: 'VERIFICATION_REQUIRED', 20276: 'VERIFICATION_REQUIRED',
      104604: 'RATE_LIMITED', 100001: 'RATE_LIMITED', 20272: 'INVALID_CREDENTIALS', 20274: 'INVALID_CREDENTIALS' })[code] ?? 'UPSTREAM_ERROR');
  }
  return data.req_0.data;
}
export function kgRequest(path, params, client, account, clock, router, body, origin = 'https://gateway.kugou.com', salt = kgSalt) {
  const cookies = cookieFields(account?.cookie), time = String(Math.floor(clock() / 1000));
  const mid = cookies.KUGOU_API_MID ?? client.mid, dfid = cookies.dfid ?? client.dfid ?? '-';
  const query = { dfid, mid, uuid: '-', appid: 1005, clientver: 20489, clienttime: time, ...params };
  if (cookies.token) query.token = cookies.token;
  if (cookies.userid && cookies.userid !== '0') query.userid = cookies.userid;
  const content = body === undefined ? '' : typeof body === 'string' ? body : encodeProtocolJSON(body);
  query.signature = md5(salt + Object.keys(query).sort().map(key => `${key}=${formValue(query[key])}`).join('') + content + salt);
  return { url: origin + path + '?' + form(query), method: body === undefined ? 'GET' : 'POST',
    ...(body === undefined ? {} : { body: content }), headers: {
      'User-Agent': 'Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi', dfid, mid, clienttime: time,
      'kg-rc': '1', 'kg-thash': '5d816a0', 'kg-rec': '1', 'kg-rf': 'B9EDA08A64250DEFFBCADDEE00F8F25F',
      ...(router ? { 'x-router': router } : {}), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    } };
}
export function kgSuccess(data) {
  const integer = value => {
    const code = required(value);
    if (!Number.isInteger(Number(code))) fail('INVALID_RESPONSE');
    return code;
  };
  const codes = ['error_code', 'errcode', 'err_code'].map(key => data[key]).filter(value => value != null).map(integer);
  const status = data.status == null ? null : integer(data.status);
  if (codes.includes('404') || scalar(data.status) === '404') fail('NOT_FOUND');
  if (codes.includes('20018')) fail('AUTH_REQUIRED');
  if (codes.includes('20028') || data.ssaCode || data['ssa-code']) fail('VERIFICATION_REQUIRED');
  if (codes.some(code => !['0', '200'].includes(code)) ||
      status !== null && !['1', '200'].includes(status) ||
      data.code != null && !['0', '200'].includes(integer(data.code)) ||
      data.error != null && ![false, 0, 'false', '0', ''].includes(data.error)) fail('UPSTREAM_ERROR');
  if (data.status == null && codes.length === 0) fail('INVALID_RESPONSE');
  return data;
}
