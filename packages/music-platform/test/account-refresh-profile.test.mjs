import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { fixture, response, rpc, qq, kg, eapi, weapi, decrypt, encrypt, md5, seedClients, fixedEntropy, now } from './support.mjs';

// HEAD Accounts/MusicAccountRefreshProtocolTests and MusicAccountSMSProfileTests.
const candidate = (f, platform, cookie, operation = 'account.refresh', userId = '42') =>
  f.owner.request({ platform, operation, candidate: { userId, cookie } });
const cookies = {
  wy: 'MUSIC_U=synthetic-original; __csrf=synthetic-csrf',
  tx: 'musicid=42; musickey=synthetic-original; encrypt_uin=synthetic-uin; refresh_token=synthetic-refresh',
  kg: 'userid=42; token=synthetic-original',
};
const profile = platform => platform === 'wy' ? response({ code: 200, profile: { userId: 42, nickname: '合成昵称' } }) :
  platform === 'tx' ? rpc({ Info: { BaseInfo: { Name: '合成昵称', EncryptedUin: 'synthetic-uin' } } }) :
    response({ status: 1, data: { nickname: '合成昵称' } });

test('refresh exact recipes authenticate the replacement token and retain the original binding without rights or new login', async t => {
  fixedEntropy(t);
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.pathname === '/eapi/login/token/refresh') {
        assert.equal(url.host, 'interfacepc.music.163.com'); assert.equal(request.method, 'POST');
        const value = eapi(request); assert.equal(value.path, '/api/login/token/refresh');
        assert.equal(value.data.header.MUSIC_U, 'synthetic-original');
        return response({ code: 200 }, { cookies: ['MUSIC_U=synthetic-new', '__csrf=synthetic-csrf-new', 'deviceId=ACTUAL_NEW_DEVICE'] });
      }
      if (platform === 'tx') {
        const call = qq(request), { module, method, param } = call.req_0;
        if (method === 'Login') {
          assert.equal(module, 'music.login.LoginServer'); assert.equal(param.loginMode, 2);
          assert.equal(param.refresh_token, 'synthetic-refresh'); assert.equal(call.comm.authst, 'synthetic-original');
          assert.ok(request.headers.Cookie.includes('synthetic-original'));
          return rpc({ musicid: '42', musickey: 'synthetic-new' });
        }
        assert.equal(call.comm.authst, 'synthetic-new');
        if (method === 'GetLoginUserInfo') return rpc({ musicid: 42 });
      }
      if (url.pathname === '/v5/login_by_token') {
        const value = kg(request);
        assert.equal(request.method, 'POST'); assert.equal(url.host, 'gateway.kugou.com');
        assert.equal(request.headers['x-router'], 'login.user.kugou.com'); assert.equal(value.fields.userid, '42');
        assert.equal(value.fields.token, 'synthetic-original'); assert.equal(value.body.userid, 42);
        assert.equal(value.body.dfid, 'synthetic-dfid'); assert.equal(value.body.t3, 'MCwwLDAsMCwwLDAsMCwwLDA=');
        assert.equal(value.body.pk.length, 256); assert.equal(value.body.params.length, 32);
        assert.deepEqual(JSON.parse(decrypt(Buffer.from(value.body.p3, 'hex'),
          '90b8382a1bb4ccdcf063102053fd75b8', 'cbc', 'f063102053fd75b8')), {
          clienttime: now / 1000, token: 'synthetic-original' });
        return response({ status: 1, data: { userid: 42, token: 'synthetic-new', t1: 8, vip_token: 'synthetic-vip-new' } });
      }
      if (platform === 'wy') {
        assert.equal(url.pathname, '/weapi/w/nuser/account/get');
        assert.ok(request.headers.Cookie.includes('MUSIC_U=synthetic-new'));
        assert.ok(request.headers.Cookie.includes('deviceId=ACTUAL_NEW_DEVICE'));
      }
      if (platform === 'kg') {
        assert.equal(url.pathname, '/v3/get_my_info'); assert.equal(url.searchParams.get('token'), 'synthetic-new');
      }
      return profile(platform);
    }); seedClients(f);
    const value = await candidate(f, platform, cookies[platform]);
    assert.equal(value.userId, '42'); assert.equal(value.profile.displayName, '合成昵称');
    assert.ok(value.cookie.includes('synthetic-new')); assert.equal(value.entitlements.membership.vip, null);
    assert.equal(f.requests.length, platform === 'tx' ? 3 : 2);
    await assert.rejects(f.request(platform, 'account.profile'), { code: 'AUTH_REQUIRED' });
    await f.close();
  });
});

test('rotated credentials never cross the original account fence, and missing refresh material never initiates QR', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (platform === 'wy') return path.endsWith('/token/refresh') ? response({ code: 200 }, { cookies: ['MUSIC_U=synthetic-new'] }) :
        response({ code: 200, profile: { userId: 99 } });
      return platform === 'tx' ? rpc({ musicid: 99, musickey: 'synthetic-new' }) :
        response({ status: 1, data: { userid: 99, token: 'synthetic-new' } });
    }); seedClients(f);
    const before = await f.clientState();
    await assert.rejects(candidate(f, platform, cookies[platform]), { code: 'INVALID_CREDENTIALS' });
    assert.equal(f.requests.length, platform === 'wy' ? 2 : 1);
    assert.deepEqual(await f.clientState(), before); assert.equal(f.writes.length, 0);
    await f.close();
  });
  const f = fixture(() => assert.fail('missing refresh cannot dispatch')); seedClients(f);
  await assert.rejects(candidate(f, 'tx', 'musicid=42; musickey=synthetic-key'), { code: 'VERIFICATION_REQUIRED' });
  await assert.rejects(candidate(f, 'kg', 'userid=99; token=synthetic-key'), { code: 'INVALID_CREDENTIALS' });
  await f.close();
});

test('KG refresh anonymously registers before sending the old token and accepts DFID only after same-account verification', async t => {
  fixedEntropy(t);
  const digest = md5('000000');
  for (const accepted of [false, true]) {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.pathname === '/risk/v2/r_register_dev') {
        const value = kg(request, { json: false }); assert.equal(value.fields.token, undefined); assert.equal(value.fields.userid, undefined);
        return response(encrypt(JSON.stringify({ status: 1, data: { dfid: 'registered-dfid' } }), digest.slice(0, 16), 'cbc', digest.slice(16)));
      }
      assert.equal(url.searchParams.get('dfid'), 'registered-dfid');
      if (url.pathname === '/v5/login_by_token') {
        assert.equal(kg(request).body.dfid, 'registered-dfid');
        return response({ status: 1, data: { userid: 42, token: 'synthetic-new' } });
      }
      return response({ status: 1, data: { userid: accepted ? 42 : 99 } });
    }); seedClients(f);
    const clients = (await f.clientState()); clients.kg.dfid = null;
    await f.setClientState(clients);
    const before = await f.clientState();
    if (accepted) assert.ok((await candidate(f, 'kg', cookies.kg)).cookie.includes('dfid=registered-dfid'));
    else await assert.rejects(candidate(f, 'kg', cookies.kg), { code: 'INVALID_CREDENTIALS' });
    assert.deepEqual(f.requests.map(r => new URL(r.url).pathname),
      ['/risk/v2/r_register_dev', '/v5/login_by_token', '/v3/get_my_info']);
    assert.equal(f.writes.length, accepted ? 1 : 0);
    if (!accepted) assert.deepEqual(await f.clientState(), before);
    await f.close();
  }
});

test('KG independent encrypted JSON/token vectors and invalid CBC padding go through the actual refresh recipe', async t => {
  // Install after constructing fixtures: the account operation ledger also owns entropy.
  const vectors = [
    ['81066de0314b25333e4b74434619ac00abe2190c2ab38d48644afb049cb61971893bc5e0f7e0bb9f1f3fafc4a4e9af12', true],
    ['c8f7826444ab9d0da5f9dafe1c9d3a04ccfc7543fa83494a7e486850bca49702', true],
    ...['not-hex', '0', '00', '35bbb8f3e30f868545114cf0ac17fd5f'].map(value => [value, false]),
  ];
  const fixtures = vectors.map(([cipher, valid]) => {
    const f = fixture(request => new URL(request.url).pathname === '/v5/login_by_token' ?
      response({ status: 1, data: { userid: 42, secu_params: cipher } }) : profile('kg'));
    seedClients(f); return { f, valid };
  });
  t.mock.method(crypto, 'randomBytes', size => { assert.equal(size, 8); return Buffer.from('0123456789abcdef', 'hex'); });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  for (const { f, valid } of fixtures) {
    if (valid) assert.ok((await candidate(f, 'kg', cookies.kg)).cookie.includes('token=synthetic-refreshed'));
    else await assert.rejects(candidate(f, 'kg', cookies.kg), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, valid ? 2 : 1); assert.equal(f.writes.length, 0); await f.close();
  }
});

test('profile secret and URL-encoded reflection filtering uses the rotated token, not the old credential', async t => {
  fixedEntropy(t);
  for (const field of ['nickname', 'avatarUrl']) for (const value of [
    'synthetic-new-secret', '%73ynthetic-new-secret', '%2573ynthetic-new-secret',
  ]) {
    const f = fixture(request => new URL(request.url).pathname.endsWith('/token/refresh') ?
      response({ code: 200 }, { cookies: ['MUSIC_U=synthetic-new-secret'] }) :
      response({ code: 200, profile: { userId: 42, [field]: field === 'avatarUrl' ? 'https://image.invalid/' + value : value } }));
    seedClients(f);
    await assert.rejects(candidate(f, 'wy', cookies.wy), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 2); assert.equal(f.writes.length, 0); await f.close();
  }
});

test('membership absolute expiry, upstream clock, product starts, tier priority and unknown flags are preserved publicly', async t => {
  fixedEntropy(t);
  const vectors = [
    ['wy', { now: now + 100000, associator: { expireTime: now + 90000 }, redplus: { expireTime: now + 200000 } },
      true, 'svip', now + 200000, now + 100000],
    ['wy', {}, null, 'unknown', null, 0],
    ['kg', { busi_vip: [{ busi_type: 'concept', product_type: 'svip', is_vip: 1,
      vip_begin_time: now / 1000 + 100, vip_end_time: now / 1000 + 200 }],
      is_vip: 0, vip_end_time: now / 1000 - 1000 }, false, 'none', now - 1000000, now + 100000],
    ['kg', { is_vip: 0, busi_vip: [{ product_type: 'svip' }] }, null, 'unknown', null, 0],
    ['kg', { is_vip: 1 }, true, 'vip', null, now + 60000],
    ['tx', { identity: { vip: 1 }, userinfo: { expire: '2020-01-02' } }, false, 'none', 1577894400000, now + 86400000],
    ['tx', { identity: { vip: '1' } }, null, 'unknown', null, 0],
    ['tx', { svip: 1, userinfo: { expire: now / 1000 + 1000 },
      identity: { huge_vip: 1, huge_vip_start: now / 1000 + 100, huge_vip_end: now / 1000 + 2000 } },
      true, 'svip', now + 1000000, now + 100000],
    ['tx', { identity: { HugeVip: 1, HugeVipEnd: now / 1000 + 500, vip: 1 }, userinfo: { expire: now / 1000 + 900 } },
      true, 'huge_vip', now + 500000, now + 500000],
  ];
  for (const [platform, raw, vip, tier, expiresAt, recheckAt] of vectors) {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (platform === 'wy' && url.pathname.endsWith('/vip/info')) {
        const body = weapi(request); assert.equal(body.userId, '42'); return response({ code: 200, data: raw });
      }
      if (platform === 'tx' && qq(request).req_0.method === 'vip_login_base') return rpc(raw);
      if (platform === 'kg' && url.pathname === '/v1/get_union_vip') return response({ status: 1, data: raw });
      return profile(platform);
    }); seedClients(f); await f.session(platform);
    const result = await f.request(platform, 'account.profile');
    assert.equal(result.profile.displayName, '合成昵称');
    assert.deepEqual(result.entitlements.membership, { vip, tier, expiresAt });
    assert.equal(result.entitlements.recheckAt, recheckAt);
    assert.equal(f.requests.length, 2); await f.close();
  }
});
