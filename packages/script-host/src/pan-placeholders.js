import { createHash, createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { parseShare, shareIdentity } from './cat-output.js';
import { panReferenceKeys } from './pan-references.js';
import { fault } from './errors.js';

const bases = { baidu: 'https://pan.baidu.com/s/1', quark: 'https://pan.quark.cn/s/',
  uc: 'https://drive.uc.cn/s/', tianyi: 'https://cloud.189.cn/t/', yidong: 'https://caiyun.139.com/m/i?' };
const mobileKey = Buffer.from('PVGDwmcvfs1uV3d1');
const jsonHeaders = { 'content-type': 'application/json; charset=utf-8' };
const size = 874 * 1024 * 1024;
const md5 = value => createHash('md5').update(value).digest('hex');
const numberId = (share, kind) => Number(BigInt('0x' + md5(share.provider + ':' + share.shareId + ':' + kind)) % 900000000000000n + 100000000000000n);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

function decryptMobile(value) {
  const raw = Buffer.from(value.replace(/\s/g, '').replace(/-/g, '+').replace(/_/g, '/'), 'base64');
  if (raw.length < 32 || raw.length % 16) throw new Error('invalid ciphertext');
  const decipher = createDecipheriv('aes-128-cbc', mobileKey, raw.subarray(0, 16));
  return Buffer.concat([decipher.update(raw.subarray(16)), decipher.final()]).toString();
}
function encryptMobile(value) {
  const iv = randomBytes(16), cipher = createCipheriv('aes-128-cbc', mobileKey, iv);
  return Buffer.concat([iv, cipher.update(value), cipher.final()]).toString('base64');
}
function fields(url, body, mobile) {
  let parsed, encrypted = false;
  try { parsed = JSON.parse(body); }
  catch { parsed = Object.fromEntries(new URLSearchParams(body)); }
  if (mobile && (typeof parsed === 'string' || (body && !object(parsed)))) {
    parsed = JSON.parse(decryptMobile(typeof parsed === 'string' ? parsed : body));
    encrypted = true;
  } else if (mobile && body && !body.trimStart().startsWith('{')) {
    parsed = JSON.parse(decryptMobile(body.trim()));
    encrypted = true;
  }
  if (!object(parsed)) parsed = {};
  const data = { ...Object.fromEntries(url.searchParams), ...parsed };
  for (const key of ['data', 'body', 'params', 'getOutLinkInfoReq'])
    if (object(parsed[key])) Object.assign(data, parsed[key]);
  return { data, encrypted };
}
const first = (data, keys) => {
  const key = keys.find(key => Object.hasOwn(data, key));
  return key === undefined ? { present: false } : { present: true, value: data[key] };
};
const scalar = value => typeof value === 'string' || typeof value === 'number' ? String(value) : null;
const passwordKeys = ['passcode', 'pwd', 'accessCode', 'accesscode', 'passwd', 'passwdStr', 'password'];

function stageFor(provider, path) {
  if (['quark', 'uc'].includes(provider)) {
    if (/\/share\/sharepage\/token\/?$/.test(path)) return 'token';
    if (/\/share\/sharepage\/detail\/?$/.test(path)) return 'list';
    if (/^\/s\/[\w-]+\/?$/.test(path)) return 'page';
  } else if (provider === 'baidu') {
    if (/^\/share\/verify\/?$/.test(path)) return 'token';
    if (/^\/share\/list\/?$/.test(path)) return 'list';
    if (/^\/api\/loginStatus\/?$/.test(path)) return 'login';
    if (/^\/s\/1[\w-]+\/?$/.test(path) || /^\/share\/init\/?$/.test(path)) return 'page';
  } else if (provider === 'tianyi') {
    if (/\/getShareInfoByCode(?:V2)?\.action$/.test(path)) return 'info';
    if (/\/listShareDir\.action$/.test(path)) return 'list';
    if (/^\/t\/[\w-]+\/?$/.test(path)) return 'page';
  } else if (/\/(?:getOutLinkInfoV6|getOutLinkInfo|queryOutLinkInfo)\/?$/.test(path)) return 'info';
  else if (/\/(?:getOutLinkFileList|queryOutLinkFileList)\/?$/.test(path)) return 'list';
  else if (/^\/(?:m|w)\/i(?:\/[\w-]+)?\/?$/.test(path)) return 'page';
  return null;
}

function shareFor(provider, url, data, shares, bindings, current) {
  const identityKeys = ['quark', 'uc'].includes(provider) ? ['pwd_id', 'pwdId']
    : provider === 'baidu' ? ['surl', 'shorturl']
    : provider === 'tianyi' ? ['shareCode', 'sharecode', 'code'] : ['linkID', 'linkId'];
  const identity = first(data, identityKeys);
  const password = first(data, passwordKeys);
  if (password.present && typeof password.value !== 'string') return null;
  let share;
  if (identity.present) {
    if (typeof identity.value !== 'string') return null;
    share = parseShare(bases[provider] + identity.value);
    if (!share) return null;
    if (identityKeys.some(key => Object.hasOwn(data, key) && data[key] !== identity.value)) return null;
  } else share = parseShare(url.href);
  const candidates = new Set(share ? [provider + ':' + share.shareId] : []);
  for (const key of ['stoken', 'sToken', 's_token', 'randsk', 'shareId', 'shareid', 'share_id',
    'fileId', 'file_id', 'fid', 'fs_id', 'shareDirFileId', 'pdir_fid', 'pdirFid', 'pCaID', 'coID',
    'filename', 'fileName', 'file_name', 'server_filename', 'name', 'coName', 'path']) {
    const value = scalar(data[key]);
    if (value === null) continue;
    const known = bindings.get(provider + ':' + key + ':' + value);
    if (known) candidates.add(known);
    else if (['stoken', 'sToken', 's_token', 'randsk', 'shareId', 'shareid', 'share_id'].includes(key) && value) return null;
    for (const marker of value.match(/meow_[a-f0-9]{24}/g) ?? []) {
      const captured = bindings.get(provider + ':marker:' + marker);
      if (!captured) return null;
      candidates.add(captured);
    }
  }
  if (candidates.size !== 1) return null;
  const key = [...candidates][0], known = current.get(key) ?? shares.get(key);
  share ??= known && { ...known };
  if (!share) return null;
  share.password = password.present ? password.value : known?.password ?? share.password;
  return share;
}

/** Request-local protocol placeholders, never a provider client or account owner. */
export class PanPlaceholders {
  constructor() { this.sites = new Map(); this.failures = new WeakSet(); }

  hasFailure(shares) { return this.failures.has(shares); }

  markFailure(shares) { this.failures.add(shares); }

  close() { this.sites.clear(); }

  site(scope) {
    let site = this.sites.get(scope.siteId);
    if (!site) this.sites.set(scope.siteId, site = { shares: new Map(), bindings: new Map(), references: new Map() });
    return site;
  }

  reference(scope, value, label) {
    const site = this.site(scope);
    const { keys, unknown } = panReferenceKeys(value, token => site.references.get(token));
    const title = panReferenceKeys(label, token => site.references.get(token), { scalar: false });
    for (const key of title.keys) keys.add(key);
    if (unknown || title.unknown || keys.size > 1) throw fault('PAN_INTERCEPTION_UNSUPPORTED');
    if (!keys.size) return null;
    const key = [...keys][0], share = scope.panShares.get(key) ?? site.shares.get(key);
    scope.panShares.set(key, share);
    return share;
  }

  response(provider, url, body, scope) {
    const { shares, bindings, references } = this.site(scope);
    let encrypted = false;
    const answer = (value, headers = jsonHeaders) => ({ headers: { ...headers },
      body: JSON.stringify(encrypted ? encryptMobile(JSON.stringify(value)) : value) });
    const unsupported = () => {
      this.markFailure(scope.panShares);
      return answer({ status: 200, code: 1, errno: 1, res_code: 1,
        message: '网盘请求未能接管，请关闭对应网盘接管后重试',
        error: { code: 'PAN_INTERCEPTION_UNSUPPORTED' }, data: {} });
    };
    const stage = stageFor(provider, url.pathname);
    if (!stage) return unsupported();
    let data;
    try { ({ data, encrypted } = fields(url, body, provider === 'yidong')); }
    catch { return unsupported(); }
    if (stage === 'login') return answer({ errno: 0, err_msg: 'ok', bdstoken: 'meow_placeholder', login_status: 1, uk: 100000000000000 });
    const share = shareFor(provider, url, data, shares, bindings, scope.panShares);
    if (!share) return unsupported();
    const shareKey = provider + ':' + share.shareId;
    if (!shares.has(shareKey) || first(data, passwordKeys).present) shares.set(shareKey, share);
    scope.panShares.set(shareKey, share);
    const marker = 'meow_' + shareIdentity(share), filename = marker + '.mp4';
    const shareId = numberId(share, 'share'), fileId = numberId(share, 'file'), uk = numberId(share, 'uk');
    const bind = (keys, value) => {
      for (const key of keys) bindings.set(provider + ':' + key + ':' + value, provider + ':' + share.shareId);
      const token = String(value), identity = provider + ':' + share.shareId;
      if (!references.has(token)) references.set(token, new Set());
      references.get(token).add(identity);
    };
    bind(['stoken', 'sToken', 's_token', 'randsk'], marker);
    bind(['marker'], marker);
    bind(['filename', 'fileName', 'file_name', 'server_filename', 'name', 'coName'], filename);
    bind(['shareId', 'shareid', 'share_id'], shareId);
    bind(['fileId', 'file_id', 'fs_id', 'shareDirFileId'], fileId);
    bind(['fid', 'pdir_fid', 'pdirFid', 'pCaID', 'coID'], marker);
    const ts = Date.now(), now = Math.floor(ts / 1000);
    const file = { fid: marker, file_id: marker, fileId: String(fileId), file_name: filename,
      fileName: filename, name: filename, file_type: 1, category: 1, file: true, dir: false,
      pdir_fid: scalar(data.pdir_fid ?? data.pdirFid) ?? '0', share_fid_token: marker,
      fid_token: marker, fidToken: marker, token: marker, size, format_type: 'video/mp4',
      obj_category: 'video', status: 1, ban: false, created_at: ts, updated_at: ts };
    const baiduFile = { category: 1, fs_id: fileId, isdir: 0, server_filename: filename,
      path: '/' + filename, size, md5: md5(marker), local_ctime: now, local_mtime: now,
      server_ctime: now, server_mtime: now, thumbs: { url1: '', url2: '', url3: '', icon: '' } };
    const baiduRoot = { errno: 0, err_msg: 'ok', randsk: marker, uk, shareid: shareId,
      surl: share.shareId, list: [baiduFile], data: { uk, shareid: shareId, list: [baiduFile] } };
    if (stage === 'page') {
      const cookies = provider === 'uc' ? ['__pus=meow; Path=/; Domain=.uc.cn',
        '__puus=meow; Path=/; Domain=.uc.cn', 'Video-Auth=meow; Path=/; Domain=.uc.cn'] : [];
      const state = provider === 'baidu' ? JSON.stringify({ ...baiduRoot, file_list: baiduRoot.list,
        SHARE_ID: shareId, SHARE_UK: uk, bdstoken: marker, sign: marker, timestamp: now }) : '{}';
      return { headers: { 'content-type': 'text/html; charset=utf-8', ...(cookies.length ? { 'set-cookie': cookies } : {}) },
        body: '<!doctype html><html><head><meta charset="utf-8"></head><body><script>var yunData=' + state +
          ';window.yunData=yunData;window.locals=yunData;</script></body></html>' };
    }
    if (['quark', 'uc'].includes(provider)) {
      if (stage === 'token') return answer({ status: 200, code: 0, message: 'ok', timestamp: now,
        data: { stoken: marker, share: { title: filename } } });
      const metadata = { _size: 200, _page: 1, _count: 1, _total: 1, check_fid_token: 1, _g_group: '', _t_group: '' };
      return answer({ status: 200, code: 0, message: 'ok', timestamp: now, metadata,
        data: { is_owner: provider === 'uc' ? 1 : 0, list: [file], metadata, share: { title: filename } } });
    }
    if (provider === 'baidu') return stage === 'token'
      ? answer({ errno: 0, err_msg: 'ok', request_id: ts, surl: share.shareId, t: now, randsk: marker },
        { ...jsonHeaders, 'set-cookie': ['BDCLND=' + marker + '; Path=/; Domain=pan.baidu.com'] })
      : answer({ ...baiduRoot, has_more: 0 });
    if (provider === 'tianyi') {
      if (stage === 'info') return answer({ res_code: 0, res_message: '成功', accessCode: share.password,
        creator: { iconURL: '', nickName: '', oper: false, ownerAccount: '', superVip: 0, vip: 0 },
        expireTime: 0, expireType: 0, fileId: String(fileId), fileName: filename, fileSize: size,
        fileType: 'mp4', isFolder: false, mediaType: 3, needAccessCode: 0, reviewStatus: 1,
        shareDate: ts, shareId, shareCode: share.shareId, shareMode: 3, shareType: 1 });
      return answer({ res_code: 0, res_message: '成功', expireTime: 0, expireType: 0, lastRev: String(now),
        fileListAO: { count: 1, fileListSize: 1, folderList: [], fileList: [{ id: fileId,
          name: filename, size, md5: md5(marker), mediaType: 3, fileCata: 1,
          icon: { largeUrl: '', smallUrl: '' }, rev: String(now), starLabel: 2 }] } });
    }
    const mobileFile = { coType: 3, coName: filename, coID: marker, coSize: size };
    return answer({ code: url.pathname.endsWith('V6') ? 0 : '0000', message: 'ok',
      data: { caLst: null, coLst: [mobileFile], linkID: share.shareId, shareName: filename,
        fileList: [file], folderList: [], total: 1 } });
  }
}
