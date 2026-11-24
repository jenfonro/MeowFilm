// SPDX-License-Identifier: GPL-3.0-only
// Fixed api-enhanced, QQMusicApi and KuGouMusicApi account recipes.
import { constants, createHmac, publicEncrypt, randomBytes, randomUUID } from 'node:crypto';
import { aes, cookieFields, cookieHeader, credentialSecrets, decrypt, form, hex, kgPublicKey, kgSalt, md5, responseCookies as setCookies, wySuccess } from './protocols.mjs';
import { decodeJSON, encodeProtocolJSON, first, image, input, object, protocolInteger, publicText, required, scalar, text, utf8 } from './model.mjs';
import { fail, MusicPlatformError } from './errors.mjs';
import { kgRights, txRights, unknownRights, wyRights } from './entitlements.mjs';

const kgWebSalt = 'NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt';
const qqJump = 'https://graph.qq.com/oauth2.0/login_jump';
const qqRedirect = 'https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/';
const hash33 = (value, seed = 0) => [...value].reduce((hash, ch) => (Math.imul(hash, 33) + ch.charCodeAt(0)) >>> 0, seed) & 0x7fffffff;
function profile(platform, userId, name, avatar, cookie) {
  const secrets = credentialSecrets(cookie);
  return { platform, userId: required(userId), displayName: publicText(name, secrets),
    avatar: image(publicText(avatar, secrets)) || null };
}
const wyCredentialKeys = ['MUSIC_U', 'MUSIC_A', '__csrf', 'JSESSIONID', 'NMTID', 'deviceId', '_ntes_nuid', '_ntes_nnid', 'WNMCID',
  'os', 'osver', 'appver', 'buildver', 'sDeviceId'];
function credentialID(platform, cookie) {
  const fields = cookieFields(cookie);
  return platform === 'wy' ? '' : required(platform === 'tx' ? first(fields.musicid, fields.str_musicid) : fields.userid, 'INVALID_CREDENTIALS');
}
function expectedIdentity(expected, actual) {
  if (expected && scalar(expected) !== actual) fail('INVALID_CREDENTIALS');
}
async function authenticateTx(c, userId) {
  try {
    const auth = await c.tx('music.UserInfo.userInfoServer', 'GetLoginUserInfo', {}, false);
    if (auth.code != null) {
      const code = required(auth.code);
      if (!Number.isInteger(Number(code))) fail('INVALID_RESPONSE');
      if (code !== '0') fail('AUTH_EXPIRED');
    }
    for (const row of [auth, auth.userInfo, auth.data].filter(Boolean))
      for (const key of ['musicid', 'str_musicid']) if (row[key] != null) expectedIdentity(row[key], userId);
  } catch (error) {
    if (error.code === 'AUTH_REQUIRED' || error.status === 401) fail('AUTH_EXPIRED');
    throw error;
  }
}
function kgRaw(value) {
  const bytes = Buffer.from(encodeProtocolJSON(value)), block = Buffer.alloc(128);
  if (bytes.length > block.length) fail('INVALID_CREDENTIALS');
  bytes.copy(block);
  return publicEncrypt({ key: kgPublicKey, padding: constants.RSA_NO_PADDING }, block).toString('hex');
}
function qqCredential(data, previous = {}) {
  const result = {};
  for (const key of ['musicid', 'musickey', 'encrypt_uin', 'login_type', 'openid', 'refresh_token', 'access_token',
    'unionid', 'refresh_key', 'expired_at', 'musickey_create_time', 'key_expires_in']) {
    const value = first(data[key], key === 'musicid' ? data.str_musicid : key === 'encrypt_uin' ? data.encryptUin :
      key === 'login_type' ? data.loginType : undefined, previous[key], key === 'musicid' ? previous.str_musicid : undefined);
    if (value != null) result[key] = scalar(value);
  }
  result.musicid = required(result.musicid); result.musickey = required(result.musickey);
  result.login_type ||= result.musickey.startsWith('W_X') ? '1' : '2';
  result.str_musicid = result.musicid;
  if (data.str_musicid != null) expectedIdentity(data.str_musicid, result.musicid);
  return cookieHeader(result);
}
async function prepareKgAccount(c) {
  const client = c.client(), account = c.accountRequired(), fields = cookieFields(account.cookie);
  expectedIdentity(account.userId, credentialID('kg', account.cookie));
  const seed = fields.KUGOU_API_GUID ?? client.seed, mid = fields.KUGOU_API_MID ?? client.mid;
  if (BigInt('0x' + md5(seed)).toString() !== mid) fail('DEVICE_CONTEXT_REQUIRED');
  const device = await c.registerKg({ seed, mid, dfid: fields.dfid ?? (seed === client.seed ? client.dfid : null) });
  c.account = { ...account, cookie: cookieHeader({ ...fields, KUGOU_API_GUID: seed, KUGOU_API_MID: mid,
    ...(device.dfid ? { dfid: device.dfid } : {}) }) };
  return { client, device };
}
function kgLoginData(raw, key) {
  if (!text(raw.secu_params)) return {};
  // Buffer.from(hex) silently truncates invalid input; this field is an actual
  // CBC ciphertext, not an arbitrary account identifier.
  if (!/^(?:[\da-f]{2})+$/i.test(raw.secu_params)) fail('INVALID_RESPONSE');
  const plain = decrypt(Buffer.from(raw.secu_params, 'hex'), key, 'cbc', key.slice(16));
  try { return object(decodeJSON(plain)); } catch { return { token: utf8(plain) }; }
}
async function readProfile(c, expected = c.account?.userId, authenticate = false, requireIdentity = authenticate) {
  const account = c.accountRequired();
  let result, cookie = account.cookie;
  try {
    if (c.platform === 'wy') {
      if (!cookieFields(cookie).MUSIC_U) fail('INVALID_CREDENTIALS');
      const response = await c.wy('/api/w/nuser/account/get', {}, true, { response: true });
      const raw = response.value.profile;
      if (raw === null) fail('AUTH_EXPIRED');
      const userId = required(raw?.userId);
      expectedIdentity(expected, userId);
      if (authenticate && response.value.account?.id != null) expectedIdentity(response.value.account.id, userId);
      cookie = cookieHeader(setCookies(response.cookies, wyCredentialKeys,
        { ...c.client().cookies, ...cookieFields(cookie) }, c.clock, 'music.163.com'));
      result = profile('wy', userId, raw.nickname, raw.avatarUrl, cookie);
    } else if (c.platform === 'tx') {
      const fields = cookieFields(cookie), userId = credentialID('tx', cookie);
      expectedIdentity(expected, userId);
      if (authenticate) await authenticateTx(c, userId);
      const encrypted = first(fields.encrypt_uin, fields.encryptUin);
      if (encrypted) {
        const data = await c.tx('music.UnifiedHomepage.UnifiedHomepageSrv', 'GetHomepageHeader',
          { uin: encrypted, IsQueryTabDetail: 1 }, false);
        const raw = object(data.Info?.BaseInfo);
        if (authenticate && raw.EncryptedUin != null) expectedIdentity(encrypted, scalar(raw.EncryptedUin));
        result = profile('tx', userId, raw.Name, raw.Avatar, cookie);
      } else {
        const key = required(fields.musickey, 'INVALID_CREDENTIALS');
        const data = await c.read({ url: 'https://c6.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg?' + form({
          g_tk: hash33(key, 5381), format: 'json', inCharset: 'utf-8', outCharset: 'utf-8', notice: 0,
          cid: 205360838, needNewCode: 0, loginUin: userId, hostUin: 0, userid: userId, reqfrom: 1 }),
        headers: { Cookie: cookieHeader({ uin: userId, qqmusic_uin: userId, qm_keyst: key, qqmusic_key: key }) } });
        if (required(data.code) !== '0') fail('UPSTREAM_ERROR');
        const raw = object(data.data?.creator);
        if (authenticate) expectedIdentity(first(raw.uin, raw.musicid), userId);
        const encryptUin = first(raw.encrypt_uin, raw.encryptUin);
        if (encryptUin) cookie = cookieHeader({ ...fields, encrypt_uin: scalar(encryptUin) });
        result = profile('tx', userId, first(raw.nick, raw.nickname), first(raw.headpic, raw.avatar), cookie);
      }
    } else if (c.platform === 'kg') {
      const fields = cookieFields(cookie), userId = credentialID('kg', cookie), time = Math.floor(c.clock() / 1000);
      expectedIdentity(expected, userId);
      // Imported account devices are distinct from the installation identity.
      // The supplied seed must actually produce the MID used by this recipe.
      if (authenticate && fields.KUGOU_API_GUID != null &&
          BigInt('0x' + md5(fields.KUGOU_API_GUID)).toString() !== fields.KUGOU_API_MID) fail('DEVICE_CONTEXT_REQUIRED');
      const p = kgRaw({ clienttime: time, token: required(fields.token, 'INVALID_CREDENTIALS') });
      const data = await c.kg('/v3/get_my_info', { plat: 1 }, 'usercenter.kugou.com',
        { visit_time: time, usertype: 1, p, userid: protocolInteger(userId) });
      const raw = object(data.data);
      if (authenticate && requireIdentity && raw.userid == null) fail('VERIFICATION_REQUIRED');
      if (authenticate && raw.userid != null) expectedIdentity(raw.userid, userId);
      result = profile('kg', userId, raw.nickname, raw.pic, cookie);
    } else fail('UNSUPPORTED');
  } catch (error) {
    if (error.code === 'AUTH_REQUIRED' || (c.platform === 'tx' && error.status === 401)) fail('AUTH_EXPIRED');
    throw error;
  }
  c.account = { ...account, userId: result.userId, cookie };
  return result;
}
async function rights(c) {
  const now = c.clock();
  try {
    if (c.platform === 'wy') {
      const result = await c.wy('/api/music-vip-membership/client/vip/info', { userId: c.account.userId }, true, { dataStatus: true });
      return wyRights(object(result.data), now);
    }
    if (c.platform === 'tx') return txRights(await c.tx('VipLogin.VipLoginInter', 'vip_login_base', {}, false), now);
    if (c.platform === 'kg') {
      const result = await c.kg('/v1/get_union_vip', { busi_type: 'concept' }, null, undefined, 'https://kugouvip.kugou.com');
      return kgRights(object(result.data), now);
    }
    fail('UNSUPPORTED');
  } catch (error) {
    if (['NETWORK_ERROR', 'UPSTREAM_ERROR', 'UPSTREAM_HTTP', 'INVALID_RESPONSE', 'RATE_LIMITED'].includes(error.code))
      return unknownRights(now, error.code, c.platform);
    if (error.code === 'AUTH_REQUIRED') fail('AUTH_EXPIRED');
    throw error;
  }
}
async function observation(c, { expectedUserId } = {}, authenticate = true, includeRights = false,
  requireIdentity = authenticate, kgPrepared = null) {
  const client = authenticate ? c.client() : null;
  const used = authenticate && c.platform === 'wy' ? { ...client.cookies, ...cookieFields(c.accountRequired().cookie) } : null;
  if (authenticate && c.platform === 'kg' && !kgPrepared) {
    kgPrepared = await prepareKgAccount(c);
  }
  let result = await readProfile(c, expectedUserId ?? c.account?.userId, authenticate, requireIdentity);
  if (authenticate && c.platform === 'wy') {
    let next = cookieFields(c.account.cookie);
    if (['MUSIC_U', 'MUSIC_A', '__csrf', 'JSESSIONID'].some(key => used[key] !== next[key])) {
      const authorized = next;
      result = await readProfile(c, result.userId, true);
      next = cookieFields(c.account.cookie);
      if (['MUSIC_U', 'MUSIC_A', '__csrf', 'JSESSIONID'].some(key => authorized[key] !== next[key])) fail('VERIFICATION_REQUIRED');
    }
    await c.acceptWyAccount(client, used, next);
    c.account.cookie = cookieHeader(Object.fromEntries(wyCredentialKeys.filter(key => next[key] != null)
      .map(key => [key, next[key]])));
  }
  if (kgPrepared) await c.acceptKgAccount(kgPrepared.client, kgPrepared.device);
  return { profile: result, profileFetchedAt: c.clock(),
    entitlements: includeRights ? await rights(c) : unknownRights(c.clock(), null, c.platform),
    userId: result.userId, cookie: c.account.cookie };
}
function qqURL(address, host, path) {
  let url;
  try { url = new URL(address); } catch { fail('REDIRECT_DENIED'); }
  if (url.protocol !== 'https:' || url.hostname !== host || url.pathname !== path || url.port ||
    url.username || url.password || url.hash) fail('REDIRECT_DENIED');
  return url.searchParams;
}
function qqGet(host, path, data, cookie) {
  return { url: `https://${host}${path}?${form(data)}`, json: false,
    headers: { Referer: 'https://xui.ptlogin2.qq.com/', ...(cookie ? { Cookie: cookie } : {}) } };
}
function callback(bytes) {
  const source = utf8(bytes).trim();
  const match = /^ptuiCB\(([\s\S]*)\);?$/.exec(source);
  if (!match) fail('INVALID_RESPONSE');
  const args = [], pattern = /\s*'((?:[^'\\]|\\(?:[\\'/]|x[\da-f]{2}|u[\da-f]{4}))*)'\s*(,|$)/igy;
  let at = 0;
  while (at < match[1].length) {
    pattern.lastIndex = at;
    const row = pattern.exec(match[1]);
    if (!row) fail('INVALID_RESPONSE');
    args.push(row[1].replace(/\\(x[\da-f]{2}|u[\da-f]{4}|[\\'/])/gi, (_raw, escaped) =>
      escaped.length === 1 ? escaped : String.fromCharCode(Number.parseInt(escaped.slice(1), 16))));
    at = pattern.lastIndex;
  }
  return args;
}
export function createAccountOperations() {
  const qr = new Map(), lastCreate = new Map(), sms = new Map(), ledgerKey = randomBytes(32);
  function pruneSms(now) {
    for (const [key, value] of sms) if (!value.busy && now >= value.until) sms.delete(key);
  }
  function smsKey(platform, phone, countryCode) {
    return createHmac('sha256', ledgerKey).update(platform + ':' + countryCode + ':' + phone).digest('hex');
  }
  function smsResponse(raw, sending = false) {
    let code = raw.code;
    if (!Number.isInteger(code)) fail('INVALID_RESPONSE');
    if (code === 200 && raw.data?.code != null && ![0, 200].includes(raw.data.code)) code = raw.data.code;
    if (!Number.isInteger(code)) fail('INVALID_RESPONSE');
    if ([400, 502, 503].includes(code)) fail(sending ? 'INVALID_PHONE' : 'INVALID_CODE');
    if ([301, 401].includes(code)) fail('AUTH_EXPIRED');
    return wySuccess({ ...raw, code });
  }
  async function pollQQ(c, current) {
    const response = await c.response(qqGet('ssl.ptlogin2.qq.com', '/ptqrlogin', {
      u1: qqJump, ptqrtoken: hash33(current.key), ptredirect: 0, h: 1, t: 1, g: 1, from_ui: 1,
      ptlang: 2052, action: '0-0-' + c.clock(), js_ver: '20102616', js_type: 1, pt_uistyle: 40,
      aid: 716027609, daid: 383, pt_3rd_aid: 100497308, has_onekey: 1 }, 'qrsig=' + current.key));
    const args = callback(response.value), status = { 66: 'waiting', 67: 'confirming', 65: 'expired', 68: 'refused', 0: 'authenticated' }[args[0]];
    if (!status) fail('INVALID_RESPONSE');
    if (status !== 'authenticated') return { status };
    const redirect = qqURL(required(args[2]), 'ssl.ptlogin2.graph.qq.com', '/check_sig');
    const uin = required(redirect.get('uin')), sig = required(redirect.get('ptsigx'));
    const signed = await c.response({ ...qqGet('ssl.ptlogin2.graph.qq.com', '/check_sig', {
      uin, pttype: 1, service: 'ptqrlogin', nodirect: 0, ptsigx: sig, s_url: qqJump, ptlang: 2052,
      ptredirect: 100, aid: 716027609, daid: 383, j_later: 0, low_login_hour: 0, regmaster: 0,
      pt_login_type: 3, pt_aid: 0, pt_aaid: 16, pt_light: 0, pt_3rd_aid: 100497308 }), allowRedirect: true });
    const jump = signed.headers.get('location');
    if (jump) qqURL(jump, 'graph.qq.com', '/oauth2.0/login_jump');
    const cookies = setCookies(signed.cookies, ['p_skey', 'p_uin', 'pt4_token', 'pt2gguin', 'uin', 'skey'],
      {}, c.clock, 'ssl.ptlogin2.graph.qq.com');
    const authorized = await c.response({ url: 'https://graph.qq.com/oauth2.0/authorize', method: 'POST',
      headers: { Cookie: cookieHeader(cookies), 'Content-Type': 'application/x-www-form-urlencoded' },
      body: form({ response_type: 'code', client_id: 100497308, redirect_uri: qqRedirect,
        scope: 'get_user_info,get_app_friends', state: 'state', switch: '', from_ptlogin: 1,
        src: 1, update_auth: 1, openapi: '1010_1030', g_tk: hash33(required(cookies.p_skey), 5381),
        auth_time: Math.floor(c.clock() / 1000) * 1000, ui: randomUUID() }),
      allowRedirect: true, json: false });
    const location = qqURL(authorized.headers.get('location'), 'y.qq.com', '/portal/wx_redirect.html');
    if (location.get('state') !== 'state') fail('REDIRECT_DENIED');
    const code = required(location.get('code'));
    const data = await c.tx('QQConnectLogin.LoginServer', 'QQLogin', { code }, true, { comm: { tmeLoginType: 2 } });
    const cookie = qqCredential(data);
    if (cookieFields(cookie).login_type !== '2') fail('INVALID_CREDENTIALS');
    return { status, cookie };
  }
  return {
    // A live feedback transaction needs account authentication, not the display
    // profile/rights projection or an anonymous GetSession as substitute proof.
    async verifyTxFeedback(c) {
      const account = c.accountRequired(), fields = cookieFields(account.cookie), id = credentialID('tx', account.cookie);
      expectedIdentity(account.userId, id);
      if (fields.str_musicid != null) expectedIdentity(fields.str_musicid, id);
      await authenticateTx(c, id);
      const encrypted = required(fields.encrypt_uin, 'INVALID_CREDENTIALS');
      try {
        const raw = await c.tx('music.UnifiedHomepage.UnifiedHomepageSrv', 'GetHomepageHeader',
          { uin: encrypted, IsQueryTabDetail: 1 }, false);
        expectedIdentity(encrypted, required(raw.Info?.BaseInfo?.EncryptedUin));
      } catch (error) {
        if (error.code === 'AUTH_REQUIRED' || error.status === 401) fail('AUTH_EXPIRED');
        throw error;
      }
    },
    operations: {
    'account.verify': (c, p) => observation(c, p),
    'account.profile': (c, p) => observation(c, p, false, true),
    async 'account.refresh'(c, p) {
      const original = c.accountRequired();
      // Registration is anonymous and precedes the token-bearing refresh.
      // Only a successfully verified replacement may accept it into the owner.
      const prepared = c.platform === 'kg' ? await prepareKgAccount(c) : null;
      const fields = cookieFields(c.account.cookie);
      if (c.platform === 'wy') {
        required(fields.MUSIC_U, 'INVALID_CREDENTIALS');
        const response = await c.wy('/api/login/token/refresh', {}, false, { response: true });
        c.account = { ...original, cookie: cookieHeader(setCookies(response.cookies, wyCredentialKeys, fields, c.clock, 'interfacepc.music.163.com')) };
      } else if (c.platform === 'tx') {
        expectedIdentity(original.userId, credentialID('tx', original.cookie));
        if (!fields.refresh_token && !fields.refresh_key) fail('VERIFICATION_REQUIRED');
        const params = { loginMode: 2, expired_in: protocolInteger(fields.expired_at ?? 0) };
        for (const key of ['openid', 'access_token', 'refresh_token', 'str_musicid', 'musicid', 'musickey', 'unionid', 'refresh_key',
          'login_type', 'musickey_create_time', 'key_expires_in'])
          if (fields[key] != null) params[key] = ['musicid', 'login_type', 'musickey_create_time', 'key_expires_in'].includes(key) ?
            protocolInteger(fields[key]) : fields[key];
        const data = await c.tx('music.login.LoginServer', 'Login', params, true);
        c.account = { ...original, cookie: qqCredential(data, fields) };
      } else if (c.platform === 'kg') {
        const time = c.clock(), seed = hex(8), key = md5(seed);
        const p3 = aes(JSON.stringify({ clienttime: Math.floor(time / 1000), token: required(fields.token) }),
          '90b8382a1bb4ccdcf063102053fd75b8', 'cbc', 'f063102053fd75b8').toString('hex');
        const data = await c.kg('/v5/login_by_token', {}, 'login.user.kugou.com', {
          dfid: fields.dfid ?? c.client().dfid ?? '-', p3, plat: 1, t1: 0, t2: 0, t3: 'MCwwLDAsMCwwLDAsMCwwLDA=',
          pk: kgRaw({ clienttime_ms: time, key: seed }), params: aes('{}', key, 'cbc', key.slice(16)).toString('hex'),
          userid: protocolInteger(original.userId), clienttime_ms: time });
        const raw = object(data.data);
        const secured = kgLoginData(raw, key);
        const token = required(first(secured.token, raw.token));
        expectedIdentity(raw.userid, original.userId);
        expectedIdentity(secured.userid, original.userId);
        const updated = { ...fields, token };
        for (const key of ['t1', 'vip_type', 'vip_token'])
          if (first(secured[key], raw[key]) != null) updated[key] = scalar(first(secured[key], raw[key]));
        c.account = { ...original, cookie: cookieHeader(updated) };
      } else fail('UNSUPPORTED');
      return observation(c, { ...p, expectedUserId: original.userId }, true, false, false, prepared);
    },
    async 'qr.create'(c, { challengeId = randomUUID() }) {
      if (!['wy', 'tx', 'kg'].includes(c.platform)) fail('UNSUPPORTED');
      challengeId = input(challengeId);
      const previous = lastCreate.get(c.platform), cooldown = c.platform === 'kg' ? 5000 : c.platform === 'tx' ? 3000 : 0;
      if (previous != null && c.clock() - previous < cooldown) fail('RATE_LIMITED');
      lastCreate.set(c.platform, c.clock());
      const old = qr.get(c.platform);
      const current = { challengeId, key: null, expiresAt: 0, busy: true, cancel: c.cancel, drain: c.drain };
      qr.set(c.platform, current);
      let key, url = '', imageBase64 = null;
      try {
      old?.cancel?.();
      await old?.drain?.();
      c.signal.throwIfAborted();
      c.account = null;
      if (c.platform === 'wy') {
        const data = await c.wy('/api/login/qrcode/unikey', { type: 3 }, false,
          { dataStatus: raw => first(raw.unikey) === undefined });
        key = required(first(data.unikey, data.data?.unikey));
        url = 'https://music.163.com/login?codekey=' + encodeURIComponent(key);
      } else if (c.platform === 'kg') {
        await c.registerKg();
        const data = await c.kg('/v2/qrcode', { appid: 1001, type: 1, plat: 4, srcappid: 2919,
          qrcode_txt: 'https://h5.kugou.com/apps/loginQRCode/html/index.html?appid=1005&' },
        null, undefined, 'https://login-user.kugou.com', kgWebSalt);
        key = required(data.data?.qrcode);
        url = 'https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=' + encodeURIComponent(key);
      } else {
        const response = await c.response(qqGet('ssl.ptlogin2.qq.com', '/ptqrshow', {
          appid: 716027609, e: 2, l: 'M', s: 3, d: 72, v: 4, t: '0.' + Number.parseInt(hex(4), 16), daid: 383, pt_3rd_aid: 100497308 }));
        key = required(setCookies(response.cookies, ['qrsig'], {}, c.clock, 'ssl.ptlogin2.qq.com').qrsig);
        if (!response.value.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) fail('INVALID_RESPONSE');
        imageBase64 = response.value.toString('base64');
      }
      c.signal.throwIfAborted();
      if (qr.get(c.platform) !== current) fail('CANCELLED');
      const expiresAt = c.clock() + 180000;
      Object.assign(current, { key, expiresAt, busy: false, cancel: undefined, drain: undefined });
      return { platform: c.platform, challengeId, url, imageBase64, expiresAt };
      } catch (error) {
        if (qr.get(c.platform) === current) qr.delete(c.platform);
        throw error;
      }
    },
    async 'qr.poll'(c, { challengeId }) {
      const current = qr.get(c.platform), id = input(challengeId);
      if (current?.challengeId === id && current.busy) fail('BUSY');
      if (!current || current.challengeId !== id || current.expiresAt <= c.clock()) return { platform: c.platform, challengeId: id, status: 'expired' };
      if (['wy', 'kg'].includes(c.platform) && current.lastPoll != null && c.clock() - current.lastPoll < 3000) fail('RATE_LIMITED');
      current.lastPoll = c.clock();
      Object.assign(current, { busy: true, cancel: c.cancel, drain: c.drain }); c.account = null;
      try {
        let result;
        if (c.platform === 'wy') {
          const response = await c.wy('/api/login/qrcode/client/login', { key: current.key, type: 3 }, false,
            { raw: true, response: true });
          const status = { 800: 'expired', 801: 'waiting', 802: 'confirming', 803: 'authenticated' }[scalar(response.value.code)];
          if (!status) { wySuccess(response.value); fail('INVALID_RESPONSE'); }
          result = { status, ...(status === 'authenticated' ? {
            cookie: cookieHeader(setCookies(response.cookies, wyCredentialKeys, {}, c.clock, 'interfacepc.music.163.com')),
            userId: scalar(response.value.profile?.userId) } : {}) };
        } else if (c.platform === 'kg') {
          const client = c.client(), dev = client.seed.slice(0, 10).toUpperCase();
          const data = await c.kg('/v2/get_userinfo_qrcode', { plat: 4, srcappid: 2919, qrcode: current.key, dev },
            null, undefined, 'https://login-user.kugou.com', kgWebSalt);
          const raw = object(data.data), status = { 0: 'expired', 1: 'waiting', 2: 'confirming', 4: 'authenticated' }[scalar(raw.status)];
          if (!status) fail('INVALID_RESPONSE');
          result = { status };
          if (status === 'authenticated') result.cookie = cookieHeader({ userid: required(raw.userid), token: required(raw.token),
            KUGOU_API_MID: client.mid, KUGOU_API_GUID: client.seed, KUGOU_API_DEV: dev, KUGOU_API_MAC: '02:00:00:00:00:00',
            KUGOU_API_WEBGL: client.seed, ...(client.dfid ? { dfid: client.dfid } : {}),
            ...Object.fromEntries(['t1', 'vip_token', 'vip_type'].filter(key => raw[key] != null).map(key => [key, scalar(raw[key])])) });
        } else if (c.platform === 'tx') result = await pollQQ(c, current);
        else fail('UNSUPPORTED');
        c.signal.throwIfAborted();
        if (qr.get(c.platform) !== current) fail('CANCELLED');
        if (current.expiresAt <= c.clock()) fail('QR_EXPIRED');
        if (result.status === 'authenticated') {
          c.account = { cookie: result.cookie, userId: result.userId ?? credentialID(c.platform, result.cookie) };
          result.account = await observation(c, {}, true, false, false);
          delete result.cookie; delete result.userId;
        }
        c.signal.throwIfAborted();
        if (qr.get(c.platform) !== current) fail('CANCELLED');
        if (current.expiresAt <= c.clock()) fail('QR_EXPIRED');
        if (['expired', 'refused', 'authenticated'].includes(result.status)) qr.delete(c.platform);
        return { platform: c.platform, challengeId: id, ...result };
      } finally { Object.assign(current, { busy: false, cancel: undefined, drain: undefined }); }
    },
    async 'qr.cancel'(c, { challengeId }) {
      const current = qr.get(c.platform);
      if (current && (challengeId == null || current.challengeId === input(challengeId))) {
        qr.delete(c.platform); current.cancel?.(); await current.drain?.();
      }
      return { cancelled: true };
    },
    async 'sms.send'(c, { phone, countryCode = '86' }) {
      if (!['wy', 'kg'].includes(c.platform)) fail('UNSUPPORTED');
      phone = input(phone); countryCode = input(countryCode);
      if (c.platform === 'kg' && countryCode !== '86') fail('UNSUPPORTED');
      pruneSms(c.clock());
      const key = smsKey(c.platform, phone, countryCode);
      const old = sms.get(key);
      if (old?.busy) fail('BUSY');
      if (old?.until > c.clock()) throw new MusicPlatformError('RATE_LIMITED', Math.ceil((old.until - c.clock()) / 1000));
      const limit = { until: 0, busy: true }; sms.set(key, limit);
      c.account = null;
      let dispatched = false;
      const markDispatch = () => { c.dispatched(); dispatched = true; limit.until = c.clock() + 60000; };
      try {
        if (c.platform === 'wy') {
          smsResponse(await c.wy('/api/sms/captcha/sent', {
            cellphone: phone, ctcode: countryCode, secrete: 'music_middleuser_pclogin',
          }, true, { raw: true, dispatched: markDispatch }), true);
        } else {
          await c.registerKg();
          await c.kg('/v7/send_mobile_code', {}, 'login.user.kugou.com',
            { businessid: 5, mobile: phone, plat: 3 }, undefined, undefined,
            { dispatched: markDispatch });
        }
        return { platform: c.platform, confirmed: true, retryAfterSeconds: 60 };
      } catch (error) {
        if (error.beforeSend) limit.until = 0;
        if (error.status === 429) throw new MusicPlatformError('RATE_LIMITED', 60);
        if (error.status === 403) fail('FORBIDDEN');
        if (dispatched && !error.beforeSend &&
            ['NETWORK_ERROR', 'UPSTREAM_HTTP', 'TIMEOUT', 'CANCELLED', 'CLOSED', 'INVALID_RESPONSE'].includes(error.code))
          throw new MusicPlatformError('SMS_SEND_UNCONFIRMED', 60);
        throw error;
      } finally {
        limit.busy = false;
        if (c.clock() >= limit.until && sms.get(key) === limit) sms.delete(key);
      }
    },
    async 'sms.login'(c, { phone, countryCode = '86', captcha }) {
      if (!['wy', 'kg'].includes(c.platform)) fail('UNSUPPORTED');
      phone = input(phone); countryCode = input(countryCode); captcha = input(captcha);
      if (c.platform === 'kg' && countryCode !== '86') fail('UNSUPPORTED');
      pruneSms(c.clock());
      const key = smsKey(c.platform, phone, countryCode), old = sms.get(key);
      if (old?.busy) fail('BUSY');
      const limit = old ?? { until: 0, busy: false };
      limit.busy = true; sms.set(key, limit);
      c.account = null;
      try {
      if (c.platform === 'wy') {
        const response = await c.wy('/api/w/login/cellphone', { type: '1', https: 'true', phone,
          countrycode: countryCode, captcha, remember: 'true', secureCaptcha: '' }, true,
        { raw: true, response: true });
        const raw = smsResponse(response.value);
        c.account = { cookie: cookieHeader(setCookies(response.cookies, wyCredentialKeys, {}, c.clock, 'music.163.com')),
          userId: required(raw.profile?.userId) };
        expectedIdentity(raw.account?.id, c.account.userId);
      } else {
        const client = await c.registerKg(), time = c.clock(), seed = hex(8), secret = md5(seed);
        const response = await c.kg('/v7/login_by_verifycode', {}, null, {
          plat: 1, support_multi: 1, t1: 0, t2: 0, t3: 'MCwwLDAsMCwwLDAsMCwwLDA=',
          clienttime_ms: time, mobile: phone.slice(0, 2) + '*****' + phone.slice(-1),
          key: md5('1005' + kgSalt + '20489' + time),
          pk: kgRaw({ clienttime_ms: time, key: seed }).toUpperCase(),
          params: aes(JSON.stringify({ mobile: phone, code: captcha }), secret, 'cbc', secret.slice(16)).toString('hex'),
        }, 'https://loginserviceretry.kugou.com', undefined, {
          headers: { 'support-calm': '1', 'User-Agent': 'Android16-1070-11440-130-0-LOGIN-wifi' } });
        const raw = object(response.data);
        if (Array.isArray(raw.info_list)) fail('ACCOUNT_SELECTION_REQUIRED');
        const secured = kgLoginData(raw, secret);
        const userId = required(first(secured.userid, raw.userid));
        expectedIdentity(raw.userid, userId);
        c.account = { userId, cookie: cookieHeader({ userid: userId, token: required(first(secured.token, raw.token)),
          KUGOU_API_MID: client.mid, KUGOU_API_GUID: client.seed, KUGOU_API_DEV: client.seed.slice(0, 10).toUpperCase(),
          KUGOU_API_MAC: '02:00:00:00:00:00', ...(client.dfid ? { dfid: client.dfid } : {}),
          ...(first(secured.t1, raw.t1) == null ? {} : { t1: scalar(first(secured.t1, raw.t1)) }) }) };
      }
      return await observation(c, {}, true, false, false);
      } catch (error) {
        if (error.status === 429) throw new MusicPlatformError('RATE_LIMITED', 60);
        if (error.status === 403) fail('FORBIDDEN');
        throw error;
      } finally {
        limit.busy = false;
        if (c.clock() >= limit.until && sms.get(key) === limit) sms.delete(key);
      }
    },
    }, invalidate(platform) { const current = qr.get(platform); qr.delete(platform); current?.cancel?.(); },
    close() { qr.clear(); sms.clear(); lastCreate.clear(); ledgerKey.fill(0); },
  };
}
