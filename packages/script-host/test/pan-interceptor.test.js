import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createCipheriv, createDecipheriv } from 'node:crypto';
import { PanInterceptor } from '../src/pan-interceptor.js';
import { shareIdentity } from '../src/cat-output.js';

const providers = ['baidu', 'quark', 'uc', 'tianyi', 'yidong'];
const scope = (siteId = 'default') => ({ siteId, settings: { providers: Object.fromEntries(providers.map(value => [value, true])) },
  panShares: new Map() });
async function request(interceptor, context, url, body = '', options = {}) {
  const client = interceptor.request(new URL(url), options, context, () => assert.fail('interception opened real network'));
  assert.ok(client);
  const closed = once(client, 'close');
  const received = new Promise((resolve, reject) => {
    client.once('error', reject);
    client.once('response', response => {
      assert.equal(response.rawHeaders.every(value => typeof value === 'string'), true);
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.once('error', reject);
      response.once('end', () => resolve({ body: Buffer.concat(chunks).toString(), headers: response.headers }));
    });
  });
  client.end(typeof body === 'string' ? body : JSON.stringify(body));
  const result = await received;
  await closed;
  assert.equal(client.chunks.length, 0);
  assert.equal(client.response, null);
  assert.equal(client.timer, null);
  return result;
}
const json = async (...args) => JSON.parse((await request(...args)).body);
const marker = (context, provider, id) => 'meow_' + shareIdentity(context.panShares.get(provider + ':' + id));
const mobileKey = Buffer.from('PVGDwmcvfs1uV3d1');
function encrypt(text) {
  const iv = Buffer.alloc(16, 7), cipher = createCipheriv('aes-128-cbc', mobileKey, iv);
  return Buffer.concat([iv, cipher.update(text), cipher.final()]).toString('base64');
}
function decrypt(text) {
  const raw = Buffer.from(text, 'base64'), decipher = createDecipheriv('aes-128-cbc', mobileKey, raw.subarray(0, 16));
  return JSON.parse(Buffer.concat([decipher.update(raw.subarray(16)), decipher.final()]).toString());
}

test('provider switches and invocation scope leave disabled, unrelated and management traffic unchanged', () => {
  const interceptor = new PanInterceptor(), context = scope();
  for (const [provider, url] of [
    ['baidu', 'https://pan.baidu.com/s/1ShareABC'], ['quark', 'https://drive.quark.cn/unknown'],
    ['uc', 'https://open-api-drive.uc.cn/unknown'], ['tianyi', 'https://content.21cn.com/unknown'],
    ['yidong', 'https://share-kd-njs.yun.139.com/unknown'],
  ]) {
    context.settings.providers[provider] = false;
    assert.equal(interceptor.request(new URL(url), {}, context), null);
    context.settings.providers[provider] = true;
    assert.equal(interceptor.request(new URL(url), {}, { settings: context.settings }), null);
  }
  for (const url of ['https://example.invalid/share/list', 'https://www.uc.cn/', 'https://example.quark.cn.invalid/'])
    assert.equal(interceptor.request(new URL(url), {}, context), null);
});

test('enabled providers reject unknown endpoints and invalid identities without opening network', async () => {
  const interceptor = new PanInterceptor(), context = scope();
  for (const host of ['pan.baidu.com', 'drive.quark.cn', 'pc-api.uc.cn', 'cloud.189.cn', 'yun.139.com']) {
    const value = await json(interceptor, context, 'https://' + host + '/unknown/provider/action');
    assert.equal(value.error.code, 'PAN_INTERCEPTION_UNSUPPORTED');
  }
  for (const body of [{}, { pwd_id: null }, { pwd_id: 'x' }, { pwd_id: ['ShareABC'] }, { pwd_id: 'root' }]) {
    const value = await json(interceptor, context, 'https://drive.quark.cn/1/clouddrive/share/sharepage/token', body);
    assert.equal(value.code, 1);
  }
  assert.equal(context.panShares.size, 0);
  assert.equal(interceptor.hasFailure(context.panShares), true);
  assert.equal(interceptor.hasFailure(scope().panShares), false);
});

for (const [provider, host] of [['quark', 'drive.quark.cn'], ['uc', 'open-api-drive.uc.cn']]) {
  test(provider + ' token/detail supports body aliases, token binding and exact multi-share association', async () => {
    const interceptor = new PanInterceptor(), context = scope();
    const base = 'https://' + host + '/1/clouddrive/share/sharepage/';
    const first = await json(interceptor, context, base + 'token', { pwdId: 'shareABCD', password: 'a1b2' });
    const second = await json(interceptor, context, base + 'token', { params: { pwd_id: 'shareEFGH', pwd: 'c3d4' } });
    assert.notEqual(first.data.stoken, second.data.stoken);
    for (const [token, id] of [[first.data.stoken, 'shareABCD'], [second.data.stoken, 'shareEFGH']]) {
      const result = await json(interceptor, context, base + 'detail?stoken=' + token);
      assert.equal(result.metadata._total, 1);
      assert.deepEqual(result.data.metadata, result.metadata);
      assert.equal(result.data.list[0].fid, marker(context, provider, id));
      assert.equal(result.data.list[0].file, true);
      assert.equal(result.data.list[0].dir, false);
      assert.match(result.data.list[0].file_name, /^meow_[a-f0-9]{24}\.mp4$/);
    }
    for (const body of [{}, { pwd_id: 'shareABCD', stoken: second.data.stoken },
      { pwd_id: 'shareABCD', pwdId: 'shareEFGH' }, { pwd_id: 'shareABCD', stoken: 'unknown' }, { stoken: 'meow_unknown' }]) {
      assert.equal((await json(interceptor, context, base + 'detail', body)).code, 1);
    }
    const fresh = await json(interceptor, context, base + 'detail', { pwd_id: 'shareNEWW', passcode: '' });
    assert.equal(fresh.code, 0);
    assert.equal(context.panShares.get(provider + ':shareNEWW').password, '');
    assert.equal((await json(interceptor, scope('other'), base + 'detail', { stoken: first.data.stoken })).code, 1);
  });
}

test('UC share page captures access password and provides cookie bootstrap without provider I/O', async () => {
  const interceptor = new PanInterceptor(), context = scope();
  const page = await request(interceptor, context, 'https://fast.uc.cn/s/shareABCD?pwd=a1b2');
  assert.match(page.headers['content-type'], /^text\/html/);
  assert.equal(page.headers['set-cookie'].length, 3);
  assert.equal(context.panShares.get('uc:shareABCD').password, 'a1b2');
});

test('Baidu share page, verify and list retain numeric IDs, cookie and filename markers for each share', async () => {
  const interceptor = new PanInterceptor(), context = scope();
  const page = await request(interceptor, context, 'https://pan.baidu.com/s/1shareABCD?pwd=a1b2');
  assert.match(page.body, /var yunData=/);
  const state = JSON.parse(/var yunData=(.*);window.yunData/.exec(page.body)[1]);
  assert.equal(Number.isSafeInteger(state.shareid), true);
  assert.equal(state.SHARE_ID, state.shareid);
  const verify = await request(interceptor, context, 'https://pan.baidu.com/share/verify?surl=shareEFGH', 'pwd=c3d4');
  assert.match(verify.headers['set-cookie'][0], /^BDCLND=meow_/);
  const root = await json(interceptor, context, 'https://pan.baidu.com/share/list?shorturl=shareEFGH&root=1');
  assert.equal(root.data.list[0].fs_id, root.list[0].fs_id);
  assert.equal(root.list[0].isdir, 0);
  assert.equal(Number.isSafeInteger(root.list[0].fs_id), true);
  const bound = await json(interceptor, context, 'https://pan.baidu.com/share/list?shareid=' + state.shareid + '&uk=' + state.uk);
  assert.equal(bound.surl, 'shareABCD');
  assert.equal(bound.list[0].server_filename, marker(context, 'baidu', 'shareABCD') + '.mp4');
  assert.equal(context.panShares.get('baidu:shareABCD').password, 'a1b2');
  const login = await json(interceptor, context, 'https://pan.baidu.com/api/loginStatus');
  assert.equal(login.login_status, 1);
  assert.equal((await json(interceptor, scope('other'), 'https://pan.baidu.com/share/list?shareid=' + state.shareid)).errno, 1);
});

test('Tianyi V2 and legacy info bind numeric share/file IDs and filename without guessing the last share', async () => {
  const interceptor = new PanInterceptor(), context = scope();
  const endpoint = 'https://api.cloud.189.cn/api/open/share/';
  const first = await json(interceptor, context, endpoint + 'getShareInfoByCodeV2.action?shareCode=shareABCD&accessCode=a1b2');
  const second = await json(interceptor, context, endpoint + 'getShareInfoByCode.action', { shareCode: 'shareEFGH', accesscode: 'c3d4' });
  assert.equal(first.isFolder, false);
  assert.equal(first.needAccessCode, 0);
  assert.equal(Number.isSafeInteger(first.shareId), true);
  assert.match(first.fileId, /^\d+$/);
  assert.notEqual(first.shareId, second.shareId);
  for (const query of ['shareId=' + first.shareId, 'fileId=' + first.fileId,
    'shareDirFileId=' + first.fileId, 'fileName=' + first.fileName]) {
    const result = await json(interceptor, context, endpoint + 'listShareDir.action?' + query);
    assert.equal(result.fileListAO.count, 1);
    assert.equal(result.fileListAO.fileList[0].mediaType, 3);
    assert.equal(result.fileListAO.fileList[0].name, first.fileName);
  }
  const mismatch = await json(interceptor, context, endpoint + 'listShareDir.action', { shareId: first.shareId, fileId: second.fileId });
  assert.equal(mismatch.res_code, 1);
  assert.equal((await json(interceptor, scope('other'), endpoint + 'listShareDir.action?shareId=' + first.shareId)).res_code, 1);
});

test('Mobile V6 handles nested plain JSON and raw/JSON-wrapped AES with matching response framing', async () => {
  const interceptor = new PanInterceptor(), context = scope();
  const endpoint = 'https://share-kd-njs.yun.139.com/orchestration/personalCloud-rebuild/outlink/v1.0/IOutLink/getOutLinkInfoV6';
  const payload = { getOutLinkInfoReq: { linkID: 'shareABCD', passwdStr: 'a1b2', pCaID: 'root' },
    commonAccountInfo: { account: '', accountType: 1 } };
  const plain = await json(interceptor, context, endpoint, payload);
  assert.equal(plain.code, 0);
  assert.equal(plain.data.caLst, null);
  assert.equal(plain.data.coLst[0].coType, 3);
  assert.equal(plain.data.coLst[0].coName, marker(context, 'yidong', 'shareABCD') + '.mp4');
  for (const body of [encrypt(JSON.stringify(payload)), JSON.stringify(encrypt(JSON.stringify(payload)))]) {
    const encoded = await json(interceptor, context, endpoint, body);
    assert.equal(typeof encoded, 'string');
    const decoded = decrypt(encoded);
    assert.equal(decoded.code, plain.code);
    assert.deepEqual(decoded.data.coLst, plain.data.coLst);
    assert.equal(decoded.data.linkID, 'shareABCD');
  }
  assert.equal(context.panShares.get('yidong:shareABCD').password, 'a1b2');
  const invalid = await json(interceptor, context, endpoint, 'bad-ciphertext');
  assert.equal(invalid.error.code, 'PAN_INTERCEPTION_UNSUPPORTED');
});

test('placeholder abort before completion and while a response is paused releases buffered data and timers', async () => {
  const interceptor = new PanInterceptor(), context = scope();
  const url = new URL('https://drive.quark.cn/1/clouddrive/share/sharepage/token');
  const first = interceptor.request(url, {}, context);
  first.setTimeout(10000);
  first.write('{"pwd_id":"shareABCD"}');
  const closed = once(first, 'close');
  first.abort();
  await closed;
  assert.equal(first.chunks.length, 0);
  assert.equal(first.timer, null);
  assert.equal(context.panShares.size, 0);
  const second = interceptor.request(url, {}, context);
  second.setTimeout(10000);
  const closing = once(second, 'close');
  second.once('response', response => { response.pause(); second.destroy(); });
  second.end('{"pwd_id":"shareEFGH"}');
  await closing;
  assert.equal(second.response, null);
  assert.equal(second.chunks.length, 0);
  assert.equal(second.timer, null);
});

test('issued tokens and numeric identities persist only within their site and generation', async () => {
  const interceptor = new PanInterceptor(), first = scope('first');
  const quark = 'https://drive.quark.cn/1/clouddrive/share/sharepage/';
  const token = await json(interceptor, first, quark + 'token', { pwd_id: 'persisted-share', passcode: 'old-code' });
  const numeric = await json(interceptor, first,
    'https://api.cloud.189.cn/api/open/share/getShareInfoByCodeV2.action?shareCode=numeric-share');
  const next = scope('first');
  assert.equal((await json(interceptor, next, quark + 'detail', { stoken: token.data.stoken })).code, 0);
  assert.equal(next.panShares.get('quark:persisted-share').password, 'old-code');
  assert.equal(next.panShares.has('tianyi:numeric-share'), false, 'unused historical shares are not captured');
  const restored = interceptor.reference(scope('first'), 'https://cloud.189.cn/play?fileId=' + numeric.fileId);
  assert.equal(restored.shareId, 'numeric-share');
  assert.equal(interceptor.reference(scope('other'), numeric.fileId), null, 'numeric bindings cannot cross sites');
  assert.throws(() => interceptor.reference(scope('other'), token.data.stoken), { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  assert.throws(() => new PanInterceptor().reference(scope('first'), token.data.stoken), { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  const updated = scope('first');
  await json(interceptor, updated, quark + 'token', { pwd_id: 'persisted-share', passcode: '' });
  assert.equal(interceptor.reference(scope('first'), token.data.stoken).password, '', 'explicit empty password replaces history');
});

test('concurrent invocations retain their own password receipt and reject conflicting output identities', async () => {
  const interceptor = new PanInterceptor(), earlier = scope(), later = scope();
  const endpoint = 'https://drive.quark.cn/1/clouddrive/share/sharepage/token';
  const one = await json(interceptor, earlier, endpoint, { pwd_id: 'shared-identity', passcode: 'first-code' });
  const two = await json(interceptor, later, endpoint, { pwd_id: 'shared-identity', passcode: 'second-code' });
  assert.equal(interceptor.reference(earlier, one.data.stoken).password, 'first-code');
  assert.equal(interceptor.reference(later, two.data.stoken).password, 'second-code');
  await json(interceptor, earlier, endpoint.replace('/token', '/detail'), { stoken: one.data.stoken });
  assert.equal(interceptor.reference(scope(), one.data.stoken).password, 'second-code',
    'an older invocation list cannot overwrite the latest explicit password');
  const other = await json(interceptor, later, endpoint, { pwd_id: 'another-share' });
  const conflict = Buffer.from(JSON.stringify({ fid: one.data.stoken, stoken: other.data.stoken })).toString('base64');
  assert.throws(() => interceptor.reference(later, conflict), { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  assert.throws(() => interceptor.reference(later,
    JSON.stringify({ fid: one.data.stoken, stoken: 'meow_aaaaaaaaaaaaaaaaaaaaaaaa' })),
  { code: 'PAN_INTERCEPTION_UNSUPPORTED' });
  const numeric = await json(interceptor, later,
    'https://api.cloud.189.cn/api/open/share/getShareInfoByCodeV2.action?shareCode=numeric-share');
  assert.equal(interceptor.reference(later,
    'https://media.invalid/trailer.mp4?duration=' + numeric.fileId), null,
  'unconsumed numeric metadata is not a file identity');
  assert.equal(interceptor.reference(later, JSON.stringify({ duration: one.data.stoken })), null,
    'unconsumed JSON metadata is not an identity either');
  assert.equal(interceptor.reference(later, JSON.stringify({ fid: one.data.stoken,
    metadata: { token: other.data.stoken, size: numeric.fileId }, duration: 'meow_aaaaaaaaaaaaaaaaaaaaaaaa' })).shareId,
  'shared-identity', 'unconsumed extension objects cannot override an exact file identity');
  assert.equal(interceptor.reference(later,
    'https://media.invalid/play?fid=' + one.data.stoken + '&duration=' + other.data.stoken +
    '&size=' + numeric.fileId + '&metadata=meow_aaaaaaaaaaaaaaaaaaaaaaaa').shareId,
  'shared-identity', 'URL metadata cannot conflict with an actual file identity');
  assert.equal(interceptor.reference(later,
    'https://media.invalid/play?fid=' + one.data.stoken + '&metadata=' +
    encodeURIComponent('&fid=' + other.data.stoken)).shareId,
  'shared-identity', 'decoding an unused query value cannot create another identity parameter');
});
