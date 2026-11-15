import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { fixture, response, seedClients, weapi, kg, md5, decrypt, encrypt, fixedEntropy, gate, now } from './support.mjs';

// HEAD MusicAccountSMSProfileTests and platform QR tests. Native retains the
// user attempt, secure commit, publication and late-result owner assertions.
const params = { phone: '10000000000', countryCode: '86', captcha: '000000' };
test('SMS send and login use distinct exact anonymous recipes and encrypted KG material on the same device', async t => {
  fixedEntropy(t);
  const fixtures = ['wy', 'kg'].map(platform => {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.pathname !== '/weapi/w/nuser/account/get' && url.pathname !== '/v3/get_my_info') {
        assert.ok(!request.headers.Cookie?.includes('MUSIC_U'));
        assert.equal(url.searchParams.get('token'), null);
      }
      if (platform === 'wy') {
        const data = weapi(request);
        if (url.pathname === '/weapi/sms/captcha/sent') {
          assert.equal(data.cellphone, params.phone); assert.equal(data.ctcode, '86');
          assert.equal(data.secrete, 'music_middleuser_pclogin'); assert.equal(data.captcha, undefined);
          return response({ code: 200 });
        }
        if (url.pathname === '/weapi/w/login/cellphone') {
          assert.equal(data.phone, params.phone); assert.equal(data.captcha, params.captcha);
          assert.equal(data.countrycode, '86'); assert.equal(data.secureCaptcha, '');
          return response({ code: 200, account: { id: 42 }, profile: { userId: 42 } }, { cookies: ['MUSIC_U=synthetic-sms'] });
        }
        assert.equal(url.pathname, '/weapi/w/nuser/account/get');
        return response({ code: 200, profile: { userId: 42, nickname: 'Profile' } });
      }
      const data = kg(request);
      if (url.pathname === '/v7/send_mobile_code') {
        assert.equal(request.headers['x-router'], 'login.user.kugou.com');
        assert.deepEqual(data.body, { businessid: 5, mobile: params.phone, plat: 3 });
        return response({ status: 1 });
      }
      if (url.pathname === '/v7/login_by_verifycode') {
        assert.equal(url.host, 'loginserviceretry.kugou.com'); assert.equal(request.headers['support-calm'], '1');
        assert.equal(data.body.mobile, '10*****0'); assert.equal(data.body.support_multi, 1);
        assert.equal(data.body.pk.length, 256); assert.equal(data.body.pk, data.body.pk.toUpperCase());
        assert.equal(data.body.key, md5('1005OIlwieks28dk2k092lksi2UIkp20489' + now));
        const key = md5('0123456789abcdef');
        assert.deepEqual(JSON.parse(decrypt(Buffer.from(data.body.params, 'hex'), key, 'cbc', key.slice(16))), {
          mobile: params.phone, code: params.captcha,
        });
        assert.equal(data.body.code, undefined); assert.equal(data.body.token, undefined);
        assert.ok(!request.body.includes(params.phone));
        return response({ status: 1, data: { userid: 42,
          secu_params: encrypt(JSON.stringify({ userid: 42, token: 'synthetic-sms', t1: 0 }), key, 'cbc', key.slice(16)).toString('hex') } });
      }
      assert.equal(url.pathname, '/v3/get_my_info'); assert.equal(data.fields.token, 'synthetic-sms');
      return response({ status: 1, data: { nickname: 'Profile' } });
    }); seedClients(f); return { platform, f };
  });
  t.mock.method(crypto, 'randomBytes', size => { assert.equal(size, 8); return Buffer.from('0123456789abcdef', 'hex'); });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  for (const { platform, f } of fixtures) {
    await f.session(platform);
    assert.equal((await f.request(platform, 'sms.send', params)).confirmed, true);
    const value = await f.request(platform, 'sms.login', params);
    assert.equal(value.userId, '42'); assert.equal(value.profile.displayName, 'Profile');
    assert.equal(value.entitlements.membership.vip, null); assert.equal(f.requests.length, 3);
    if (platform === 'kg') {
      assert.ok(value.cookie.includes('t1=0'));
      const mids = f.requests.map(r => new URL(r.url).searchParams.get('mid'));
      assert.equal(new Set(mids).size, 1);
    }
    await f.close();
  }
});

test('failed SMS dispatch keeps cooldown after caller cancellation, login does not spend send quota and platforms remain independent', async t => {
  const held = gate(), controller = new AbortController();
  const f = fixture(async request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/captcha/sent')) { await held.wait(); throw Error('lost dispatch'); }
    if (path === '/v7/send_mobile_code') return response({ status: 1 });
    return response({ code: 503 });
  }); seedClients(f);
  t.after(async () => { held.release(); await f.close(); });
  for (let index = 0; index < 20; index++)
    await assert.rejects(f.request('wy', 'sms.login', params), { code: 'INVALID_CODE' });
  const send = f.request('wy', 'sms.send', params, { signal: controller.signal }); send.catch(() => {});
  await held.entered; controller.abort();
  await assert.rejects(f.request('wy', 'sms.login', params), { code: 'BUSY' });
  await assert.rejects(f.request('wy', 'sms.send', params), { code: 'BUSY' });
  assert.equal((await f.request('kg', 'sms.send', params)).confirmed, true);
  held.release(); await assert.rejects(send, { code: 'SMS_SEND_UNCONFIRMED' });
  await assert.rejects(f.request('wy', 'sms.send', params), { code: 'RATE_LIMITED', retryAfterSeconds: 60 });
  assert.equal(f.requests.length, 22);
  f.advance(60000);
  await assert.rejects(f.request('wy', 'sms.send', params), { code: 'SMS_SEND_UNCONFIRMED' });
  assert.equal(f.requests.length, 23, 'one dispatch only after the actual cooldown');
});

test('QR poll throttle, busy reservations and expiry include the final profile phase', async t => {
  for (const platform of ['wy', 'kg']) await t.test(platform, async t => {
    const held = gate();
    let polls = 0;
    const f = fixture(async request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/unikey')) return response({ code: 200, unikey: 'synthetic-qr' });
      if (path === '/v2/qrcode') return response({ status: 1, data: { qrcode: 'synthetic-qr' } });
      if (path.endsWith('/qrcode/client/login')) return response({ code: ++polls === 1 ? 801 : 803 }, { cookies: ['MUSIC_U=synthetic-qr-auth'] });
      if (path === '/v2/get_userinfo_qrcode') return response({ status: 1, data: { status: ++polls === 1 ? 1 : 4,
        userid: 42, token: 'synthetic-qr-auth' } });
      await held.wait();
      return response(platform === 'wy' ? { code: 200, profile: { userId: 42 } } : { status: 1, data: { nickname: 'Profile' } });
    }); seedClients(f);
    t.after(async () => { held.release(); await f.close(); });
    const created = await f.request(platform, 'qr.create', { challengeId: 'qr' });
    assert.equal((await f.request(platform, 'qr.poll', { challengeId: 'qr' })).status, 'waiting');
    f.advance(2999);
    await assert.rejects(f.request(platform, 'qr.poll', { challengeId: 'qr' }), { code: 'RATE_LIMITED' });
    assert.equal(f.requests.length, 2); f.advance(1);
    const pending = f.request(platform, 'qr.poll', { challengeId: 'qr' }); pending.catch(() => {});
    await held.entered;
    await assert.rejects(f.request(platform, 'qr.poll', { challengeId: 'qr' }), { code: 'BUSY' });
    f.advance(created.expiresAt - now - 3000); held.release();
    await assert.rejects(pending, { code: 'QR_EXPIRED' });
    assert.equal(f.requests.length, 4);
    await assert.rejects(f.request(platform, 'account.profile'), { code: 'AUTH_REQUIRED' });
  });
});

test('QQ QR callback is data-only and never executes extra JavaScript', async () => {
  for (const callback of ["ptuiCB('66','0','','0','','');globalThis.qrInjected=true;",
    "ptuiCB('0','0',run(),'0','','');", "ptuiCB('66', 'unterminated);"]) {
    const f = fixture(request => new URL(request.url).pathname === '/ptqrshow' ?
      response(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), { cookies: ['qrsig=synthetic-qrsig'] }) : response(callback));
    seedClients(f); await f.request('tx', 'qr.create', { challengeId: 'qq' });
    await assert.rejects(f.request('tx', 'qr.poll', { challengeId: 'qq' }), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 2); assert.equal(globalThis.qrInjected, undefined); await f.close();
  }
});
