import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, rpc, qq, kg, seedClients, gate, now } from './support.mjs';

const cookies = { wy: 'MUSIC_U=synthetic-music-u; __csrf=synthetic-csrf',
  tx: 'musicid=42; musickey=synthetic-key; encrypt_uin=synthetic-uin; refresh_token=synthetic-refresh',
  kg: 'userid=42; token=synthetic-token' };
const candidate = (f, platform, operation, params = {}) => f.owner.request({ platform, operation, params,
  candidate: { userId: '42', cookie: cookies[platform] } });
const profile = platform => platform === 'wy' ? response({ code: 200, account: { id: 42 }, profile: { userId: 42 } }) :
  platform === 'tx' ? rpc({ Info: { BaseInfo: { EncryptedUin: 'synthetic-uin' } } }) : response({ status: 1, data: { userid: 42 } });

test('candidate verification performs only real authentication, does not read rights or publish a session', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => {
      if (platform === 'tx' && qq(request).req_0.method === 'GetLoginUserInfo') return rpc({ musicid: '42' });
      if (platform === 'kg') {
        const value = kg(request); assert.equal(value.path, '/v3/get_my_info');
        assert.equal(value.body.userid, 42); assert.ok(value.body.p);
      }
      return profile(platform);
    }); seedClients(f);
    const verified = await candidate(f, platform, 'account.verify');
    assert.equal(verified.userId, '42'); assert.equal(verified.profile.displayName, '');
    assert.equal(verified.profile.avatar, null); assert.equal(verified.entitlements.membership.vip, null);
    assert.equal(f.requests.length, platform === 'tx' ? 2 : 1);
    await assert.rejects(f.request(platform, 'account.profile'), { code: 'AUTH_REQUIRED' });
    assert.ok(!f.files.get('clients.json').toString().includes('synthetic-key'));
    await f.close();
  });
});

test('QQ candidate authentication preserves ordinary denials and the previously published credential', async t => {
  for (const method of ['GetLoginUserInfo', 'GetHomepageHeader'])
    for (const [code, status, expected] of [
      [20277, 200, 'FORBIDDEN'], [20278, 200, 'FORBIDDEN'], [20450, 200, 'FORBIDDEN'],
      [null, 403, 'UPSTREAM_HTTP'], [1000, 200, 'AUTH_EXPIRED'], [null, 401, 'AUTH_EXPIRED'],
      [20272, 200, 'INVALID_CREDENTIALS'],
    ]) await t.test(method + ':' + (code ?? 'HTTP' + status), async t => {
      let rejecting = true;
      const f = fixture(request => {
        const call = qq(request);
        if (rejecting && call.req_0.method === method)
          return status === 200 ? response({ code: 0, req_0: { code, data: {} } }) : response('', { status });
        if (call.req_0.method === 'GetLoginUserInfo') return rpc({ musicid: call.comm.qq });
        assert.equal(call.req_0.method, 'GetHomepageHeader');
        return rpc({ Info: { BaseInfo: { EncryptedUin: call.req_0.param.uin } } });
      });
      t.after(() => f.close()); seedClients(f);
      const original = 'musicid=99; musickey=synthetic-current-key; encrypt_uin=synthetic-current-uin';
      await f.session('tx', '99', original);
      const before = Buffer.from(f.files.get('clients.json'));
      await assert.rejects(candidate(f, 'tx', 'account.verify'), { code: expected });
      assert.equal(f.requests.length, method === 'GetLoginUserInfo' ? 1 : 2);
      assert.deepEqual(f.files.get('clients.json'), before); assert.equal(f.writes.length, 0);
      rejecting = false;
      const current = await f.request('tx', 'account.verify');
      assert.equal(current.userId, '99'); assert.equal(current.cookie, original);
    });
});

test('profile observations allow unknown optional rights without becoming a new authorization', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      const rights = platform === 'wy' ? path.includes('vip/info') : platform === 'tx' ?
        qq(request).req_0.method === 'vip_login_base' : path.includes('get_union_vip');
      if (rights) return response('synthetic outage', { status: 503 });
      return profile(platform);
    }); seedClients(f); await f.session(platform, '42', cookies[platform]);
    const result = await f.request(platform, 'account.profile');
    assert.equal(result.userId, '42'); assert.equal(result.entitlements.membership.vip, null);
    assert.equal(result.entitlements.errorCode, 'UPSTREAM_HTTP');
    assert.equal(f.requests.length, 2);
    await f.close();
  });
});

test('real authentication mismatch, expiry and secret-bearing public fields remain failures', async () => {
  for (const [body, code] of [
    [{ code: 200, profile: null }, 'AUTH_EXPIRED'],
    [{ code: 200, profile: { userId: 43 } }, 'INVALID_CREDENTIALS'],
    [{ code: 200, account: { id: 43 }, profile: { userId: 42 } }, 'INVALID_CREDENTIALS'],
    [{ code: 200, profile: { userId: 42, nickname: 'synthetic-music-u' } }, 'INVALID_RESPONSE'],
    [{ code: 200, profile: { userId: 42, nickname: '%73ynthetic-music-u' } }, 'INVALID_RESPONSE'],
    [{ code: 403 }, 'FORBIDDEN'],
  ]) {
    const f = fixture(() => response(body));
    await assert.rejects(candidate(f, 'wy', 'account.verify'), { code }); await f.close();
  }
});

test('explicit refresh consumes new credentials then authenticates the same account without unrelated rights', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/token/refresh')) return response({ code: 200 }, { cookies: ['MUSIC_U=synthetic-refreshed; Path=/'] });
      if (platform === 'tx') {
        const call = qq(request).req_0;
        if (call.method === 'Login') return rpc({ musicid: '42', musickey: 'synthetic-refreshed', encrypt_uin: 'synthetic-uin' });
        assert.equal(qq(request).comm.authst, 'synthetic-refreshed');
        if (call.method === 'GetLoginUserInfo') return rpc({ musicid: '42' });
      }
      if (path === '/v5/login_by_token') return response({ status: 1, data: { userid: 42, token: 'synthetic-refreshed' } });
      if (platform === 'wy') assert.ok(request.headers.Cookie.includes('MUSIC_U=synthetic-refreshed'));
      if (platform === 'kg') assert.equal(new URL(request.url).searchParams.get('token'), 'synthetic-refreshed');
      return profile(platform);
    }); seedClients(f);
    const value = await candidate(f, platform, 'account.refresh');
    assert.equal(value.userId, '42'); assert.ok(value.cookie.includes('synthetic-refreshed'));
    assert.equal(f.requests.length, platform === 'tx' ? 3 : 2);
    await f.close();
  });
});

test('WY and KG QR flows preserve waiting/confirming/authentication and require the final real profile', async t => {
  for (const platform of ['wy', 'kg']) await t.test(platform, async () => {
    let polls = 0;
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/qrcode/unikey')) return response({ code: 200, unikey: 'synthetic-key' });
      if (path === '/v2/qrcode') return response({ status: 1, data: { qrcode: 'synthetic-key' } });
      if (path.endsWith('/qrcode/client/login')) {
        const code = [801, 802, 803][polls++];
        return response({ code }, { cookies: code === 803 ? ['MUSIC_U=synthetic-qr-user; Path=/'] : [] });
      }
      if (path === '/v2/get_userinfo_qrcode') return response({ status: 1,
        data: { status: [1, 2, 4][polls++], userid: 42, token: 'synthetic-qr-user' } });
      return profile(platform);
    }); seedClients(f);
    const created = await f.request(platform, 'qr.create', { challengeId: 'challenge' });
    assert.equal(created.challengeId, 'challenge'); assert.equal(created.expiresAt, now + 180000);
    assert.ok(created.url.includes('synthetic-key'));
    assert.equal((await f.request(platform, 'qr.poll', { challengeId: 'challenge' })).status, 'waiting');
    f.advance(3000);
    assert.equal((await f.request(platform, 'qr.poll', { challengeId: 'challenge' })).status, 'confirming');
    f.advance(3000);
    const done = await f.request(platform, 'qr.poll', { challengeId: 'challenge' });
    assert.equal(done.status, 'authenticated'); assert.equal(done.account.userId, '42');
    assert.equal(done.cookie, undefined);
    assert.equal((await f.request(platform, 'qr.poll', { challengeId: 'challenge' })).status, 'expired');
    await assert.rejects(f.request(platform, 'account.profile'), { code: 'AUTH_REQUIRED' });
    await f.close();
  });
});

test('QQ QR grants credentials only after guarded OAuth redirects and real account authentication', async () => {
  const f = fixture(request => {
    const url = new URL(request.url);
    if (url.pathname === '/ptqrshow') return response(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
      { cookies: ['qrsig=synthetic-qrsig; Domain=ptlogin2.qq.com; Path=/'] });
    if (url.pathname === '/ptqrlogin') return response("ptuiCB('0','0','https://ssl.ptlogin2.graph.qq.com/check_sig?uin=42&ptsigx=synthetic-sig','0','ok','');");
    if (url.pathname === '/check_sig') return response('', { status: 302,
      headers: { Location: 'https://graph.qq.com/oauth2.0/login_jump' },
      cookies: ['p_skey=synthetic-skey; Domain=graph.qq.com; Path=/'] });
    if (url.pathname === '/oauth2.0/authorize') {
      assert.ok(request.headers.Cookie.includes('p_skey=synthetic-skey'));
      return response('', { status: 302,
        headers: { Location: 'https://y.qq.com/portal/wx_redirect.html?state=state&code=synthetic-code' } });
    }
    const call = qq(request).req_0;
    if (call.method === 'QQLogin') {
      assert.equal(call.param.code, 'synthetic-code');
      return rpc({ musicid: '42', musickey: 'synthetic-qr-key', encrypt_uin: 'synthetic-uin', login_type: 2 });
    }
    if (call.method === 'GetLoginUserInfo') return rpc({ musicid: '42' });
    return profile('tx');
  }); seedClients(f);
  const created = await f.request('tx', 'qr.create', { challengeId: 'qq' });
  assert.ok(created.imageBase64);
  const result = await f.request('tx', 'qr.poll', { challengeId: 'qq' });
  assert.equal(result.status, 'authenticated'); assert.equal(result.account.userId, '42');
  assert.equal(f.requests.length, 7);
  await f.close();
});

test('QQ QR never follows a foreign credential-bearing redirect', async () => {
  const f = fixture(request => new URL(request.url).pathname === '/ptqrshow' ?
    response(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), { cookies: ['qrsig=synthetic-qrsig'] }) :
    response("ptuiCB('0','0','https://foreign.invalid/check_sig?uin=42&ptsigx=synthetic','0','ok','');"));
  seedClients(f);
  await f.request('tx', 'qr.create', { challengeId: 'qq' });
  await assert.rejects(f.request('tx', 'qr.poll', { challengeId: 'qq' }), { code: 'REDIRECT_DENIED' });
  assert.equal(f.requests.length, 2); await f.close();
});

test('QR cancel drains the actual in-flight poll and late authentication cannot register', async t => {
  const held = gate();
  const f = fixture(async request => {
    if (new URL(request.url).pathname.endsWith('/unikey')) return response({ code: 200, unikey: 'synthetic-key' });
    await held.wait(); return response({ code: 803 }, { cookies: ['MUSIC_U=late-synthetic'] });
  });
  t.after(async () => { held.release(); await f.close(); });
  await f.request('wy', 'qr.create', { challengeId: 'qr' });
  const poll = f.request('wy', 'qr.poll', { challengeId: 'qr' }); poll.catch(() => {});
  await held.entered;
  let done = false;
  const cancel = f.request('wy', 'qr.cancel', { challengeId: 'qr' }).then(value => { done = true; return value; });
  await Promise.resolve(); assert.equal(done, false);
  held.release();
  await assert.rejects(poll, { code: 'CANCELLED' });
  assert.deepEqual(await cancel, { cancelled: true });
  assert.equal(f.requests.length, 2);
});

test('WY and KG SMS keep separate cooldown/unknown ledgers and authenticate final login', async t => {
  for (const platform of ['wy', 'kg']) await t.test(platform, async () => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/captcha/sent')) return response({ code: 200 });
      if (path === '/v7/send_mobile_code') return response({ status: 1 });
      if (path.endsWith('/login/cellphone')) return response({ code: 200, account: { id: 42 }, profile: { userId: 42 } },
        { cookies: ['MUSIC_U=synthetic-sms-user'] });
      if (path === '/v7/login_by_verifycode') {
        const body = JSON.parse(request.body);
        assert.ok(body.pk); assert.ok(body.params); assert.equal(body.mobile, '13*****0');
        assert.ok(!request.body.includes('synthetic-captcha'));
        return response({ status: 1, data: { userid: 42, token: 'synthetic-sms-user' } });
      }
      return profile(platform);
    }); seedClients(f);
    const params = { phone: '13000000000', captcha: 'synthetic-captcha' };
    assert.equal((await f.request(platform, 'sms.send', params)).confirmed, true);
    await assert.rejects(f.request(platform, 'sms.send', params), { code: 'RATE_LIMITED', retryAfterSeconds: 60 });
    const value = await f.request(platform, 'sms.login', params);
    assert.equal(value.userId, '42');
    assert.ok(!JSON.stringify(value.profile).includes(params.phone));
    assert.equal(f.requests.length, 3); await f.close();
  });
  const failed = fixture(() => { throw Error('synthetic network'); }); seedClients(failed);
  const params = { phone: '13000000000' };
  await assert.rejects(failed.request('wy', 'sms.send', params), { code: 'SMS_SEND_UNCONFIRMED' });
  await assert.rejects(failed.request('wy', 'sms.send', params), { code: 'RATE_LIMITED' });
  assert.equal(failed.requests.length, 1);
  await failed.close();
});

test('SMS real invalid-phone/invalid-code and KG account-selection details survive without a credential or profile request', async () => {
  // HEAD MusicAccountSMSProfileTests.testTypedInvalidCodeAndMultiAccountErrorsSurviveOwnerWithoutCredentialWrite.
  for (const [platform, operation, raw, code] of [
    ['wy', 'sms.login', { code: 503 }, 'INVALID_CODE'],
    ['wy', 'sms.login', { code: 200, data: { code: 502 } }, 'INVALID_CODE'],
    ['wy', 'sms.send', { code: 400 }, 'INVALID_PHONE'],
    ['wy', 'sms.send', { code: 200, data: { code: 503 } }, 'INVALID_PHONE'],
    ['kg', 'sms.login', { status: 1, data: { info_list: [{ userid: 42 }, { userid: 99 }] } }, 'ACCOUNT_SELECTION_REQUIRED'],
    ['kg', 'sms.login', { status: 1, data: {} }, 'INVALID_RESPONSE'],
  ]) {
    const f = fixture(() => response(raw)); seedClients(f);
    const original = Buffer.from(f.files.get('clients.json'));
    await assert.rejects(f.request(platform, operation, { phone: '10000000000', captcha: '000000' }), { code });
    assert.equal(f.requests.length, 1); assert.equal(f.writes.length, 0);
    assert.deepEqual(f.files.get('clients.json'), original);
    await assert.rejects(f.request(platform, 'account.profile'), { code: 'AUTH_REQUIRED' });
    if (operation === 'sms.send') {
      await assert.rejects(f.request(platform, operation, { phone: '10000000000' }), { code: 'RATE_LIMITED' });
      assert.equal(f.requests.length, 1);
    }
    await f.close();
  }
});

test('KG SMS-authenticated identity does not require an optional profile userid echo or use login VIP as display rights', async () => {
  const f = fixture(request => new URL(request.url).pathname === '/v7/login_by_verifycode' ?
    response({ status: 1, data: { userid: 42, token: 'synthetic-login-token', t1: 'synthetic-t1', vip_type: 99 } }) :
    response({ status: 1, data: { nickname: '合成昵称', pic: 'https://imge.kugou.com/kugouicon/{size}/synthetic.jpg' } }));
  seedClients(f);
  const result = await f.request('kg', 'sms.login', { phone: '10000000000', captcha: '000000' });
  assert.equal(result.userId, '42'); assert.equal(result.profile.displayName, '合成昵称');
  assert.equal(result.profile.avatar, 'https://imge.kugou.com/kugouicon/480/synthetic.jpg');
  assert.equal(result.entitlements.membership.vip, null);
  assert.ok(result.cookie.includes('t1=synthetic-t1')); assert.equal(f.requests.length, 2);
  await f.close();
});
