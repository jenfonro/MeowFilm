import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { createCipheriv } from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { fixture, response, rpc, kg, qq, md5, encrypt, decrypt, seedClients, fixedEntropy, now, wySong, kgSong } from './support.mjs';

// HEAD MusicNeteaseProtocolTests.testIndependentFixedWEAPIAndEAPIVectors:
// the fixed secret is chosen through the real entropy source, not a helper API.
test('the public WEAPI recipe preserves the independent fixed-secret RSA golden and both AES-CBC layers', async t => {
  fixedEntropy(t);
  const secret = '0123456789abcdef', alphabet = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
  let index = 0;
  t.mock.method(crypto, 'randomInt', maximum => {
    assert.equal(maximum, 62); return alphabet.indexOf(secret[index++ % secret.length]);
  });
  syncBuiltinESMExports();
  const f = fixture(request => {
    assert.equal(new URL(request.url).pathname, '/weapi/v1/radio/get');
    const form = new URLSearchParams(request.body);
    assert.equal(form.get('encSecKey'), '35701388baf89fed412e11269b9c76625d095ecaf17f03fa018abe19ea2d38b949debf242ee39a71ca1f6cda71b1b86a45aa909ee27f7e78e267d34e732f0de948206c3340a788d0003372183e2f753c1f78b66ac23d134ac1fc9b993156520ea826b8aa89a962d4491b4b8d7e08738e1da9b07aa39bf4a7ef0b1c210728cd52');
    const payload = JSON.stringify({ limit: '3', mode: 'SIMILAR', sourceType: 'song', sourceIds: '[101]',
      unplaySonIds: '[]', e_r: false, csrf_token: '' });
    const inner = encrypt(payload, '0CoJUm6Qyw8W8jud', 'cbc', '0102030405060708').toString('base64');
    assert.equal(form.get('params'), encrypt(inner, secret, 'cbc', '0102030405060708').toString('base64'));
    return response({ code: 200, data: [wySong('101')] });
  }); seedClients(f);
  assert.equal((await f.request('wy', 'similar', { sourceIds: ['101'] })).items.length, 1);
  assert.equal(index, 16); assert.equal(f.requests.length, 1); await f.close();
});

// HEAD MusicKugouProtocolTests.testRawRSARightPaddingGoldenAndRange.
test('KG profile sends the independent right-zero-padded raw RSA golden and rejects overlong real auth material before HTTP', async () => {
  const f = fixture(request => {
    assert.equal(new URL(request.url).pathname, '/v3/get_my_info');
    assert.equal(kg(request).body.p, '1e6dc8a3717d584895186f901a34bcd4b5725006ac5ff047b08d9387d8e8d72087635f8d6b4369ffaf7972f5d362b7834d06a041c949590dca18bf29c8e96f16699f23932901322571870444de9af2d42458338529920b8d53bcd23608c59132c560ac6bf7a19b897deb55a7b6a403b9cdfa49fcc410754c500c4fa44e5ca47a');
    return response({ status: 1, data: { userid: 42, nickname: 'Public profile' } });
  }); seedClients(f);
  assert.equal((await f.owner.request({ platform: 'kg', operation: 'account.verify',
    candidate: { userId: '42', cookie: 'userid=42; token=synthetic-token' } })).profile.displayName, 'Public profile');
  assert.equal(f.requests.length, 1); await f.close();
  const invalid = fixture(() => assert.fail('raw RSA plaintext cannot exceed its real block'));
  seedClients(invalid);
  await assert.rejects(invalid.owner.request({ platform: 'kg', operation: 'account.verify',
    candidate: { userId: '42', cookie: 'userid=42; token=' + 'x'.repeat(129) } }), { code: 'INVALID_CREDENTIALS' });
  assert.equal(invalid.requests.length, 0); await invalid.close();
});

test('KG actual metadata signs exact UTF8 bytes and QR uses the separate web salt with lossless query encoding', async () => {
  const token = 'synthetic+a b/&=', salt = 'OIlwieks28dk2k092lksi2UIkp';
  const f = fixture(request => {
    const url = new URL(request.url), fields = Object.fromEntries(url.searchParams);
    if (url.pathname === '/v1/audio/audio') {
      assert.equal(url.searchParams.get('token'), token);
      assert.ok(url.search.includes('synthetic%2Ba+b%2F%26%3D'));
      const body = `{"appid":1005,"clientver":20489,"clienttime":${now},"data":[{"hash":"汉字<&/","audio_id":0}],"dfid":"synthetic-dfid","key":"${md5('1005' + salt + '20489' + now)}","mid":"176905054734845483393488264506404844848","userid":"42","token":"${token}"}`;
      assert.equal(request.body, body);
      const query = 'appid=1005clienttime=1800000000clientver=20489dfid=synthetic-dfid' +
        'mid=176905054734845483393488264506404844848token=' + token + 'userid=42uuid=-';
      assert.equal(fields.signature, md5(salt + query + body + salt));
      assert.notEqual(fields.signature, md5(salt + query + body.replace(',"data"', ', "data"') + salt));
      return response({ status: 1, data: [kgSong()] });
    }
    assert.equal(url.pathname, '/v2/qrcode');
    assert.equal(url.hostname, 'login-user.kugou.com'); assert.equal(request.headers['x-router'], undefined);
    assert.equal(fields.qrcode_txt, 'https://h5.kugou.com/apps/loginQRCode/html/index.html?appid=1005&');
    delete fields.signature;
    const content = Object.keys(fields).sort().map(key => key + '=' + fields[key]).join('');
    const web = 'NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt';
    assert.equal(url.searchParams.get('signature'), md5(web + content + web));
    assert.notEqual(url.searchParams.get('signature'), md5(salt + content + salt));
    return response({ status: 1, data: { qrcode: 'actual+&/汉字' } });
  }); seedClients(f); await f.session('kg', '42', 'userid=42; token=' + token);
  await f.request('kg', 'song', { id: '汉字<&/' });
  const qr = await f.request('kg', 'qr.create', { challengeId: 'qr' });
  assert.ok(qr.url.endsWith(encodeURIComponent('actual+&/汉字')));
  assert.equal(f.requests.length, 2); await f.close();
});

// HEAD MusicQQProtocolTests.testQimeiEnvelopeIsFixedProtocolAndGetSessionRemainsAnonymous
// and testCBCVectorAndPrivateCallbackParsingDoNotExecuteJavaScript.
test('QIMEI carries a full independently decoded CBC device, both signatures and a randomized RSA block while GetSession stays anonymous', async t => {
  fixedEntropy(t);
  const securedKeys = [], envelopes = [], randomBytes = crypto.randomBytes;
  t.mock.method(crypto, 'randomBytes', size => {
    return size === 8 ? Buffer.from('0123456789abcdef', 'hex') : randomBytes(size);
  });
  syncBuiltinESMExports();
  for (let run = 0; run < 2; run++) {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.hostname === 'api.tencentmusic.com') {
        assert.equal(request.method, 'POST'); assert.equal(url.pathname, '/tme/trpc/proxy');
        assert.equal(request.headers.Cookie, undefined);
        assert.equal(request.headers.method, 'GetQimei');
        assert.equal(request.headers.service, 'trpc.tme_datasvr.qimeiproxy.QimeiProxy');
        const body = JSON.parse(request.body), fields = body.qimeiParams;
        assert.equal(Buffer.from(fields.key, 'base64').length, 128);
        assert.ok(fields.params.length > 1024);
        assert.equal(fields.time, String(now / 1000)); assert.equal(fields.nonce, '0123456789abcdef');
        assert.equal(fields.sign, md5(fields.key + fields.params + fields.time + '000' + fields.nonce +
          'ZdJqM15EeO2zWc08' + fields.extra));
        assert.equal(request.headers.sign, md5('qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH' + fields.time));
        const payload = JSON.parse(decrypt(Buffer.from(fields.params, 'base64'),
          '0123456789abcdef', 'cbc', '0123456789abcdef'));
        assert.equal(payload.androidId, 'synthetic-device'); assert.equal(payload.packageId, 'com.tencent.qqmusic');
        assert.equal(payload.appKey, '0AND0HD6FE4HY80F'); assert.equal(payload.userId, '{}');
        const reserved = JSON.parse(payload.reserved);
        assert.equal(reserved.oz, '+JCibeQBRLhj6aGmvwBMr1u5TILvIXRE1yp9gRJPAX0=');
        assert.equal(decrypt(Buffer.from(reserved.oo, 'base64'), 'lvcwmSYVr2Axv1gn', 'cbc', 'Zs0ntDqG2jyhKN0c').toString(), 'V2408A');
        securedKeys.push(fields.key); envelopes.push(fields.params);
        return response({ code: 0, data: JSON.stringify({ code: 0, data: { q36: 'fresh-q36' } }) });
      }
      const root = qq(request);
      if (root.req_0.method === 'GetSession') {
        assert.equal(request.headers.Cookie, undefined);
        for (const key of ['qq', 'authst', 'uid', 'sid']) assert.equal(root.comm[key], undefined);
        assert.deepEqual(root.req_0.param, { uid: '', vkey: 0, caller: 2 });
        return rpc({ session: { uid: 'fresh-uid', sid: 'fresh-sid' } });
      }
      return rpc({ body: { item_song: [] }, meta: { sum: 0, nextpage: -1 } });
    }); seedClients(f);
    const clients = JSON.parse(f.files.get('clients.json'));
    Object.assign(clients.tx, { androidId: 'synthetic-device', qimei: null, session: null });
    f.files.set('clients.json', Buffer.from(JSON.stringify(clients)));
    await f.session('tx');
    await f.request('tx', 'search', { keyword: 'first' }); await f.request('tx', 'search', { keyword: 'second' });
    assert.equal(f.requests.length, 4);
    assert.equal(f.requests.filter(request => new URL(request.url).hostname === 'api.tencentmusic.com').length, 1);
    await f.close();
  }
  assert.equal(envelopes[0], envelopes[1]); assert.notEqual(securedKeys[0], securedKeys[1], 'PKCS1 padding is random, not raw RSA');
});

test('KG registration uses independently decoded base64 CBC and randomized uppercase PKCS1, not the account raw-RSA envelope', async t => {
  fixedEntropy(t);
  const digest = md5('000000'), portraits = [], bodies = [];
  for (let run = 0; run < 2; run++) {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path === '/risk/v2/r_register_dev') {
        const value = kg(request, { json: false }); portraits.push(value.fields.p); bodies.push(request.body);
        assert.match(value.fields.p, /^[0-9A-F]{256}$/); assert.equal(value.fields.part, '1'); assert.equal(value.fields.platid, '1');
        assert.equal(value.fields.token, undefined); assert.equal(value.fields.userid, undefined);
        const payload = JSON.parse(decrypt(Buffer.from(request.body, 'base64'), digest.slice(0, 16), 'cbc', digest.slice(16)));
        assert.equal(payload.uuid, '0123456789abcdef0123456789abcdef'); assert.equal(payload.imei, payload.uuid);
        assert.equal(payload.brand, 'Redmi'); assert.equal(payload.batteryLevel, 100);
        return response(encrypt(JSON.stringify({ status: 1, data: { dfid: 'registered-dfid' } }),
          digest.slice(0, 16), 'cbc', digest.slice(16)));
      }
      if (path === '/v5/login_by_token') return response({ status: 1, data: { userid: 42, token: 'synthetic-refreshed' } });
      assert.equal(path, '/v3/get_my_info'); return response({ status: 1, data: { userid: 42 } });
    }); seedClients(f);
    const clients = JSON.parse(f.files.get('clients.json')); clients.kg.dfid = null;
    f.files.set('clients.json', Buffer.from(JSON.stringify(clients)));
    assert.equal((await f.owner.request({ platform: 'kg', operation: 'account.refresh',
      candidate: { userId: '42', cookie: 'userid=42; token=synthetic-token' } })).userId, '42');
    assert.equal(f.requests.length, 3); await f.close();
  }
  assert.equal(bodies[0], bodies[1]); assert.notEqual(portraits[0], portraits[1]);
});

test('KG encrypted login accepts real CBC padding and rejects every malformed suffix or incomplete block through the public refresh operation', async t => {
  const seed = '0123456789abcdef', key = md5(seed), iv = key.slice(16);
  const raw = block => {
    const cipher = createCipheriv('aes-256-cbc', Buffer.from(key), Buffer.from(iv)); cipher.setAutoPadding(false);
    return Buffer.concat([cipher.update(block), cipher.final()]).toString('hex');
  };
  const vectors = [
    ...['x', 'synthetic', '0123456789abcdef'].map(token =>
      [encrypt(token, key, 'cbc', iv).toString('hex'), token]),
    ...[Buffer.alloc(16), Buffer.alloc(16, 17), Buffer.from([...Array(14).fill(0), 3, 2]),
      Buffer.from([15, ...Array(15).fill(16)])].map(block => [raw(block), null]),
    ...[0, 1, 15, 17, 31].map(length => [Buffer.alloc(length).toString('hex'), null]),
  ];
  const fixtures = vectors.map(([ciphertext, token]) => {
    const f = fixture(request => new URL(request.url).pathname === '/v5/login_by_token'
      ? response({ status: 1, data: { userid: 42, secu_params: ciphertext } })
      : response({ status: 1, data: { userid: 42 } }));
    seedClients(f); return { f, token };
  });
  t.mock.method(crypto, 'randomBytes', size => {
    assert.equal(size, 8); return Buffer.from(seed, 'hex');
  });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  for (const { f, token } of fixtures) {
    const work = f.owner.request({ platform: 'kg', operation: 'account.refresh',
      candidate: { userId: '42', cookie: 'userid=42; token=synthetic-original' } });
    if (token) assert.ok((await work).cookie.includes('token=' + token));
    else await assert.rejects(work, { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, token ? 2 : 1); await f.close();
  }
});
