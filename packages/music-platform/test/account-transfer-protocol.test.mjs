import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, rpc, qq, kg, encrypt, decrypt, md5, seedClients, fixedEntropy, huge } from './support.mjs';

// HEAD MusicAccountTransferTests: the external file codec remains native. These
// vectors start with its canonical cookie fields and exercise the real protocol.
const seed = '0123456789abcdef0123456789abcdef';
const mid = '176905054734845483393488264506404844848';
const fields = cookie => Object.fromEntries(cookie.split('; ').map(part => {
  const at = part.indexOf('='); return [part.slice(0, at), part.slice(at + 1)];
}));
const verify = (f, platform, cookie, userId = '42') =>
  f.owner.request({ platform, operation: 'account.verify', candidate: { userId, cookie } });

test('B09 WY successful authentication returns and saves the device actually sent, preserving JSESSIONID and comma values', async () => {
  for (const succeeds of [false, true]) {
    const f = fixture(request => {
      assert.ok(request.headers.Cookie.includes('deviceId=SYNTHETIC_DEVICE'));
      assert.ok(request.headers.Cookie.includes('_ntes_nnid=synthetic-nuid,1800000000000'));
      assert.ok(request.headers.Cookie.includes('JSESSIONID=synthetic-jsession'));
      assert.ok(request.headers.Cookie.includes('MUSIC_A=synthetic-account-a'));
      return response({ code: 200, profile: { userId: succeeds ? 42 : 99 } },
        { cookies: ['NMTID=synthetic-validated-new; Path=/; Domain=music.163.com'] });
    }); seedClients(f);
    const before = await f.clientState();
    const cookie = 'MUSIC_U=synthetic-music-u; MUSIC_A=synthetic-account-a; JSESSIONID=synthetic-jsession';
    if (succeeds) {
      const result = await verify(f, 'wy', cookie), saved = fields(result.cookie);
      assert.equal(saved.NMTID, 'synthetic-validated-new');
      assert.equal(saved.deviceId, 'SYNTHETIC_DEVICE');
      assert.equal(saved._ntes_nnid, 'synthetic-nuid,1800000000000');
      assert.equal(saved.JSESSIONID, 'synthetic-jsession');
      assert.equal(saved.MUSIC_A, 'synthetic-account-a');
      assert.equal((await f.clientState()).wy.cookies.NMTID, saved.NMTID);
      assert.equal(f.writes.length, 1);
    } else {
      await assert.rejects(verify(f, 'wy', cookie), { code: 'INVALID_CREDENTIALS' });
      assert.deepEqual(await f.clientState(), before); assert.equal(f.writes.length, 0);
    }
    assert.equal(f.requests.length, 1);
    await assert.rejects(f.request('wy', 'account.profile'), { code: 'AUTH_REQUIRED' });
    await f.close();
  }
});

test('B09 a WY candidate using a different external device cannot patch the installation device', async () => {
  const f = fixture(request => {
    assert.ok(request.headers.Cookie.includes('deviceId=EXTERNAL_DEVICE'));
    return response({ code: 200, profile: { userId: 42 } }, { cookies: ['NMTID=external-nmtid'] });
  }); seedClients(f);
  const before = await f.clientState();
  const result = await verify(f, 'wy', 'MUSIC_U=synthetic-music-u; deviceId=EXTERNAL_DEVICE; _ntes_nnid=external,1800000000000');
  assert.equal(fields(result.cookie).deviceId, 'EXTERNAL_DEVICE');
  assert.equal(fields(result.cookie).NMTID, 'external-nmtid');
  assert.deepEqual(await f.clientState(), before); assert.equal(f.writes.length, 0);
  await f.close();
});

test('WY rotated authentication cookies are authenticated once using the replacement and a rotating tail is never accepted', async () => {
  for (const rotatesAgain of [false, true]) {
    const f = fixture((request, count) => {
      assert.ok(request.headers.Cookie.includes('MUSIC_U=' + (count === 1 ? 'original-u' : 'replacement-u')));
      return response({ code: 200, profile: { userId: 42 } },
        { cookies: count === 1 ? ['MUSIC_U=replacement-u'] : rotatesAgain ? ['MUSIC_U=unverified-tail'] : [] });
    }); seedClients(f);
    if (rotatesAgain) await assert.rejects(verify(f, 'wy', 'MUSIC_U=original-u'), { code: 'VERIFICATION_REQUIRED' });
    else assert.equal(fields((await verify(f, 'wy', 'MUSIC_U=original-u')).cookie).MUSIC_U, 'replacement-u');
    assert.equal(f.requests.length, 2); assert.equal(f.writes.length, 0); await f.close();
  }
});

test('B09 KG GUID must produce the exact decimal MD5 MID before registration or authentication', async () => {
  const f = fixture(() => assert.fail('a mismatched account device cannot dispatch')); seedClients(f);
  const before = await f.clientState();
  await assert.rejects(verify(f, 'kg', `userid=42; token=synthetic-token; KUGOU_API_GUID=${seed}; KUGOU_API_MID=1`),
    { code: 'DEVICE_CONTEXT_REQUIRED' });
  assert.deepEqual(await f.clientState(), before); assert.equal(f.writes.length, 0); await f.close();
});

test('B09 KG registration is anonymous and installs DFID only after successful candidate authentication', async t => {
  fixedEntropy(t);
  const key = md5('000000');
  for (const outcome of ['confirmed', 'wrong-profile', 'registration-rejected', 'plaintext-success', 'save-failed']) {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.pathname === '/risk/v2/r_register_dev') {
        const value = kg(request, { json: false });
        assert.equal(value.fields.token, undefined); assert.equal(value.fields.userid, undefined);
        assert.equal(value.fields.mid, mid); assert.equal(request.headers['x-router'], undefined);
        assert.equal(request.method, 'POST'); assert.equal(request.headers['Content-Type'], 'application/x-www-form-urlencoded');
        const payload = JSON.parse(decrypt(Buffer.from(request.body, 'base64'), key.slice(0, 16), 'cbc', key.slice(16)));
        assert.equal(payload.uuid, seed); assert.equal(payload.imei, seed);
        if (outcome === 'registration-rejected') return response({ status: 0, error_code: 1 });
        if (outcome === 'plaintext-success') return response({ status: 1, data: { dfid: 'not-encrypted' } });
        return response(encrypt(JSON.stringify({ status: 1, data: { dfid: 'registered-dfid' } }),
          key.slice(0, 16), 'cbc', key.slice(16)));
      }
      const value = kg(request);
      assert.equal(value.path, '/v3/get_my_info'); assert.equal(value.fields.dfid, 'registered-dfid');
      assert.equal(value.fields.mid, mid); assert.equal(value.fields.token, 'synthetic-token');
      assert.equal(value.body.userid, 42);
      return response({ status: 1, data: { userid: outcome === 'wrong-profile' ? 99 : 42 } });
    }); seedClients(f);
    const initial = (await f.clientState()); initial.kg.dfid = null;
    await f.setClientState(initial);
    const before = await f.clientState();
    if (outcome === 'save-failed') f.failWrites();
    const work = verify(f, 'kg', `userid=42; token=synthetic-token; KUGOU_API_GUID=${seed}; KUGOU_API_MID=${mid}; t1=SYNTHETIC_T1_OPAQUE`);
    if (outcome === 'confirmed') {
      const result = await work;
      assert.equal(fields(result.cookie).dfid, 'registered-dfid');
      assert.equal(fields(result.cookie).t1, 'SYNTHETIC_T1_OPAQUE');
      assert.equal((await f.clientState()).kg.dfid, 'registered-dfid');
      assert.equal(f.writes.length, 1);
    } else {
      await assert.rejects(work, { code: ({ 'wrong-profile': 'INVALID_CREDENTIALS', 'registration-rejected': 'UPSTREAM_ERROR',
        'plaintext-success': 'INVALID_RESPONSE', 'save-failed': 'STORAGE_ERROR' })[outcome] });
      assert.deepEqual(await f.clientState(), before);
      assert.equal(f.writes.length, outcome === 'save-failed' ? 1 : 0);
    }
    assert.equal(f.requests.length, ['registration-rejected', 'plaintext-success'].includes(outcome) ? 1 : 2);
    await assert.rejects(f.request('kg', 'account.profile'), { code: 'AUTH_REQUIRED' }); await f.close();
  }
});

test('B09 KG numeric and opaque t1 survive refresh alongside updated VIP credentials without identity substitution', async () => {
  for (const t1 of [1700000000, 'SYNTHETIC_T1_OPAQUE']) {
    const f = fixture(request => new URL(request.url).pathname === '/v5/login_by_token' ?
      response({ status: 1, data: { userid: 42, token: 'synthetic-refreshed', t1, vip_token: 'synthetic-vip', vip_type: 1 } }) :
      response({ status: 1, data: { userid: 42 } })); seedClients(f);
    const result = await f.owner.request({ platform: 'kg', operation: 'account.refresh',
      candidate: { userId: '42', cookie: `userid=42; token=synthetic-token; KUGOU_API_GUID=${seed}; KUGOU_API_MID=${mid}; dfid=synthetic-dfid` } });
    assert.equal(fields(result.cookie).t1, String(t1)); assert.equal(fields(result.cookie).vip_token, 'synthetic-vip');
    assert.equal(f.requests.length, 2); await f.close();
  }
});

test('B09 QQ canonical cookie fields preserve str_musicid and exact integer wire types during refresh', async () => {
  const f = fixture(request => {
    const data = qq(request), call = data.req_0;
    assert.equal(data.comm.tmeLoginType, 2);
    if (call.method === 'Login') {
      assert.ok(request.body.includes('"musicid":' + huge));
      assert.equal(call.param.str_musicid, huge);
      assert.equal(call.param.key_expires_in, 86400);
      assert.equal(call.param.musickey_create_time, 1700000000);
      assert.equal(call.param.expired_in, 1800000000); assert.equal(call.param.login_type, 2);
      return rpc({ str_musicid: huge, musickey: 'replacement-key', login_type: 2 });
    }
    assert.equal(data.comm.authst, 'replacement-key');
    if (call.method === 'GetLoginUserInfo') return rpc({ userInfo: { str_musicid: huge } });
    return rpc({ Info: { BaseInfo: {} } });
  }); seedClients(f);
  const cookie = `musicid=${huge}; str_musicid=${huge}; musickey=synthetic-key; refresh_token=synthetic-refresh; encrypt_uin=synthetic-uin; login_type=2; key_expires_in=86400; musickey_create_time=1700000000; expired_at=1800000000`;
  const result = await f.owner.request({ platform: 'tx', operation: 'account.refresh', candidate: { userId: huge, cookie } });
  assert.equal(fields(result.cookie).musicid, huge); assert.equal(fields(result.cookie).str_musicid, huge);
  assert.equal(fields(result.cookie).key_expires_in, '86400'); assert.equal(f.requests.length, 3); await f.close();
});

test('B09 QQ remote string identity in each real response container cannot override the candidate', async () => {
  for (const container of ['root', 'userInfo', 'data']) {
    const f = fixture(() => rpc(container === 'root' ? { musicid: 42, str_musicid: '99' } :
      { [container]: { musicid: 42, str_musicid: '99' } })); seedClients(f);
    await assert.rejects(verify(f, 'tx', 'musicid=42; musickey=synthetic-key; encrypt_uin=synthetic-uin'),
      { code: 'INVALID_CREDENTIALS' });
    assert.equal(f.requests.length, 1); assert.equal(f.writes.length, 0); await f.close();
  }
});
