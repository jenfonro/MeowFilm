import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { deflateSync, gzipSync, gunzipSync, inflateSync } from 'node:zlib';
import { fixture, response, rpc, qq, kg, seedClients, encrypt, decrypt, md5, gate, kgHash, now } from './support.mjs';

// HEAD Accounts/Feedback/{MusicAccountFeedbackProtocolTests,
// MusicAccountFeedbackKugouTests,MusicAccountFeedbackChannelTests}. Owner/lease
// reservation and outbox coverage remains native; these use only public Node I/O.
const ack = value => Buffer.concat([Buffer.alloc(5), deflateSync(Buffer.from(JSON.stringify(value)))]);
function qqFeedback(handler, authenticate = () => {}) {
  const f = fixture(async request => {
    if (new URL(request.url).pathname.endsWith('/imusic_tj')) return handler(request);
    const call = qq(request).req_0;
    if (call.method === 'GetLoginUserInfo' || call.method === 'GetHomepageHeader') {
      const override = await authenticate(request);
      if (override) return override;
      if (call.method === 'GetLoginUserInfo') {
        assert.equal(call.module, 'music.UserInfo.userInfoServer');
        return rpc({ code: 0, musicid: 42 });
      }
      assert.equal(call.module, 'music.UnifiedHomepage.UnifiedHomepageSrv');
      assert.deepEqual(call.param, { uin: 'synthetic-uin', IsQueryTabDetail: 1 });
      // Name, avatar and rights are not required by this authentication step.
      return rpc({ Info: { BaseInfo: { EncryptedUin: 'synthetic-uin' } } });
    }
    if (call.method === 'CgiGetTrackInfo') {
      assert.equal(call.module, 'music.trackInfo.UniformRuleCtrl');
      assert.deepEqual(call.param.mids, ['synthetic-mid']);
      return rpc({ tracks: [{ id: 101, interval: 180, singer: [{ id: 77 }],
        pay: { pay_play: 1, pay_down: 1 } }] });
    }
    assert.equal(call.module, 'music.vkey.GetVkey'); assert.equal(call.method, 'UrlGetVkey');
    assert.deepEqual(call.param.filename, ['M500synthetic-midsynthetic-mid.mp3']);
    assert.deepEqual(call.param.songmid, ['synthetic-mid']); assert.deepEqual(call.param.songtype, [0]);
    assert.equal(call.param.uin, '42'); assert.equal(call.param.ctx, 0);
    return rpc({ midurlinfo: [{ result: 0, vkey: 'synthetic-<&"\'>', songmid: 'synthetic-mid',
      filename: 'M500synthetic-midsynthetic-mid.mp3' }] });
  });
  seedClients(f);
  return f;
}
async function qqStart(f) {
  await f.session('tx');
  return f.request('tx', 'feedback.prepare', { target: { nativeId: 'synthetic-mid' } });
}
const qqEnd = sessionId => ({ sessionId, event: 'end', duration: 180, listened: 1.75, mediaListened: 3.5,
  position: 3.5, rate: 2, reason: 'ui' });

// HEAD Windows MusicQqFeedbackIntegrationTests.LiveFailure(auth/profile) and
// AccountQqFeedbackOwner: a warm anonymous device never authenticates Finish.
test('QQ Finish repeats live authentication and exact account association before report, without display-profile or rights admission', async t => {
  for (const [fault, expected] of [['auth', 'AUTH_EXPIRED'], ['uid', 'INVALID_CREDENTIALS'],
    ['encrypted-uin', 'INVALID_CREDENTIALS'], ['missing-association', 'INVALID_RESPONSE'],
    ['boolean-auth-code', 'INVALID_RESPONSE'], ['malformed-auth-code', 'INVALID_RESPONSE'],
    ['ordinary-profile', null]]) await t.test(fault, async () => {
    let finishing = false, reports = 0;
    const f = qqFeedback(() => { reports++; return response(ack({ code: 0 })); }, request => {
      if (!finishing) return;
      const call = qq(request).req_0;
      if (call.method === 'GetLoginUserInfo') {
        if (fault === 'auth') return response({ code: 0, req_0: { code: 1000, data: {} } });
        if (fault === 'uid') return response('{"code":0,"req_0":{"code":0,"data":{"musicid":42,"musicid":99}}}');
        if (fault === 'boolean-auth-code') return rpc({ code: false, musicid: 42 });
        if (fault === 'malformed-auth-code') return rpc({ code: 'not-integer', musicid: 42 });
      }
      if (call.method === 'GetHomepageHeader') return rpc({ Info: { BaseInfo: {
        ...(fault === 'missing-association' ? {} : { EncryptedUin: fault === 'encrypted-uin' ? 'wrong-uin' : 'synthetic-uin' }),
        Name: { unconsumed: 'synthetic-key' }, Avatar: ['not-used'], optionalRights: null,
      } } });
    });
    const prepared = await qqStart(f), before = Buffer.from(f.files.get('clients.json'));
    f.advance(2000); finishing = true;
    const result = await f.request('tx', 'feedback.send', qqEnd(prepared.sessionId));
    assert.deepEqual(result, expected ? { status: 'notSent', errorCode: expected } : { status: 'confirmed', errorCode: null });
    assert.equal(reports, expected ? 0 : 1); assert.deepEqual(f.files.get('clients.json'), before);
    assert.equal(f.writes.length, 0);
    const methods = f.requests.filter(row => !row.url.includes('imusic_tj')).map(row => qq(row).req_0.method);
    assert.equal(methods.filter(method => method === 'GetLoginUserInfo').length, 2);
    assert.equal(methods.filter(method => method === 'CgiGetTrackInfo').length, 1);
    assert.equal(methods.filter(method => method === 'UrlGetVkey').length, 1);
    assert.ok(!methods.some(method => method.includes('vip') || method.includes('LoginServer')));
    assert.deepEqual(await f.request('tx', 'feedback.send', qqEnd(prepared.sessionId)),
      { status: 'notSent', errorCode: 'STALE_SESSION' }, 'failed Finish is consumed, never automatically replayed');
    await f.close();
  });
});

test('QQ prepare and Finish distinguish ordinary auth/profile denials from actual credential failures', async t => {
  for (const phase of ['prepare', 'finish']) for (const method of ['GetLoginUserInfo', 'GetHomepageHeader'])
    for (const [code, status, expected] of [
      [20277, 200, 'FORBIDDEN'], [20278, 200, 'FORBIDDEN'], [20450, 200, 'FORBIDDEN'],
      [null, 403, 'UPSTREAM_HTTP'], [1000, 200, 'AUTH_EXPIRED'], [null, 401, 'AUTH_EXPIRED'],
      [20272, 200, 'INVALID_CREDENTIALS'],
    ]) await t.test(phase + ':' + method + ':' + (code ?? 'HTTP' + status), async t => {
      let rejecting = phase === 'prepare', reports = 0;
      const f = qqFeedback(() => { reports++; return response(ack({ code: 0 })); }, request => {
        if (rejecting && qq(request).req_0.method === method)
          return status === 200 ? response({ code: 0, req_0: { code, data: {} } }) : response('', { status });
      });
      t.after(() => f.close());
      await f.session('tx');
      const prepared = phase === 'finish' ? await f.request('tx', 'feedback.prepare',
        { target: { nativeId: 'synthetic-mid' } }) : null;
      const before = Buffer.from(f.files.get('clients.json'));
      f.advance(2000); rejecting = true;
      if (prepared)
        assert.deepEqual(await f.request('tx', 'feedback.send', qqEnd(prepared.sessionId)),
          { status: 'notSent', errorCode: expected });
      else await assert.rejects(f.request('tx', 'feedback.prepare', { target: { nativeId: 'synthetic-mid' } }),
        { code: expected });
      assert.equal(reports, 0); assert.deepEqual(f.files.get('clients.json'), before); assert.equal(f.writes.length, 0);
      const methods = f.requests.map(request => qq(request).req_0.method);
      assert.equal(methods.filter(value => value === 'CgiGetTrackInfo').length, prepared ? 1 : 0);
      assert.equal(methods.filter(value => value === 'UrlGetVkey').length, prepared ? 1 : 0);
      rejecting = false;
      const current = await f.request('tx', 'account.verify');
      assert.equal(current.userId, '42');
      assert.equal(current.cookie, 'musicid=42; musickey=synthetic-key; encrypt_uin=synthetic-uin');
    });
});

test('QQ feedback account-cookie mismatch is authentication failure, not an ordinary recording identity error', async () => {
  const f = qqFeedback(() => assert.fail('mismatched account cannot report'));
  await f.session('tx', '42', 'musicid=99; musickey=synthetic-key; encrypt_uin=synthetic-uin');
  await assert.rejects(f.request('tx', 'feedback.prepare', { id: 'synthetic-mid' }), { code: 'INVALID_CREDENTIALS' });
  assert.equal(f.requests.length, 0); await f.close();
});

test('QQ Finish live-auth cancellation and account publication retire physical work before any report', async t => {
  for (const replacement of [false, true]) await t.test(String(replacement), async () => {
    const held = gate(), controller = new AbortController(); let finishing = false, reports = 0;
    const f = qqFeedback(() => { reports++; return response(ack({ code: 0 })); }, async request => {
      if (finishing && qq(request).req_0.method === 'GetLoginUserInfo') await held.wait();
    });
    t.after(async () => { held.release(); await f.close(); });
    const prepared = await qqStart(f); f.advance(2000); finishing = true;
    const work = f.request('tx', 'feedback.send', qqEnd(prepared.sessionId), { signal: controller.signal });
    await held.entered;
    const publication = replacement ? f.session('tx', '99') : null;
    if (!replacement) controller.abort();
    assert.equal(f.requests.at(-1).signal.aborted, true);
    held.release();
    assert.deepEqual(await work, { status: 'notSent', errorCode: replacement ? 'STALE_SESSION' : 'CANCELLED' });
    await publication; assert.equal(reports, 0);
  });
});

test('QQ feedback retains exact VIP vkey, XML escaping, floored wall seconds and timekey without membership/audio requests', async () => {
  const f = qqFeedback(request => {
    assert.equal(request.url, 'https://stat6.y.qq.com/android/fcgi-bin/imusic_tj');
    assert.equal(request.method, 'POST'); assert.equal(request.headers['Content-Encoding'], 'gzip');
    assert.equal(request.headers['Content-Type'], 'application/octet-stream');
    assert.equal(request.headers.Cookie, 'uin=42; qqmusic_uin=42; qm_keyst=synthetic-key; qqmusic_key=synthetic-key');
    const xml = gunzipSync(request.body).toString(), time = String(Math.floor((now + 2000) / 1000));
    for (const field of ['<cid>228</cid>', 'QQ="42"', 'songid="101"', 'singerid="77"', 'time="1"',
      'play_duration_mi="1000"', 'audiotime="180000"', 'vkey="synthetic-&lt;&amp;&quot;&apos;&gt;"',
      `timekey="${md5(time + '142gk2$Lh-&l4#!4iow').toUpperCase()}"`]) assert.ok(xml.includes(field), field);
    assert.ok(!xml.includes('https://'));
    return response(ack({ code: 0 }));
  });
  const state = await qqStart(f); f.advance(2000);
  assert.equal((await f.request('tx', 'feedback.send', qqEnd(state.sessionId))).status, 'confirmed');
  assert.equal(f.requests.length, 7); await f.close();
});

test('QQ prepare ignores playback facts and Finish encodes only listened plus the acquired duration', async t => {
  const unused = { duration: { bad: true }, mediaListened: 'unreadable', position: -99, rate: 'unreadable' };
  const f = qqFeedback(request => {
    const xml = gunzipSync(request.body).toString();
    assert.ok(xml.includes('time="90000"')); assert.ok(xml.includes('audiotime="180000"'));
    return response(ack({ code: 0 }));
  });
  t.after(() => f.close()); await f.session('tx');
  const state = await f.request('tx', 'feedback.prepare', { target: { nativeId: 'synthetic-mid' }, ...unused, listened: {} });
  for (const listened of ['unreadable', { toString: null }, 0]) {
    assert.deepEqual(await f.request('tx', 'feedback.send', { sessionId: state.sessionId, event: 'end', listened, ...unused }),
      { status: 'notSent', errorCode: 'INVALID_REQUEST' });
    assert.equal(f.requests.length, 4);
  }
  assert.deepEqual(await f.request('tx', 'feedback.send', { sessionId: state.sessionId, event: 'end', listened: 90000, ...unused }),
    { status: 'confirmed', errorCode: null });
  assert.equal(f.requests.length, 7);
  assert.deepEqual(await f.request('tx', 'feedback.send', { sessionId: state.sessionId, event: 'end', listened: 90000 }),
    { status: 'notSent', errorCode: 'STALE_SESSION' });
});

test('QQ five-byte zlib ACK rejects corrupt, trailing, concatenated, gzip, large malformed and noninteger successes', async () => {
  const valid = deflateSync(Buffer.from('{"code":0}')), broken = Buffer.from(valid); broken[broken.length - 1] ^= 1;
  const bomb = Buffer.from('eJztwYEAAAAAgCC2/aUWqQoAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACqASKYBBE=', 'base64');
  const cases = [
    ...[Buffer.alloc(0), Buffer.from([0x78, 0x9c]), valid.subarray(0, -1), broken,
      Buffer.concat([valid, Buffer.from([0])]), Buffer.concat([valid, valid]), gzipSync(Buffer.from('{}'))]
      .map(bytes => [Buffer.concat([Buffer.alloc(5), bytes]), 'INVALID_RESPONSE']),
    [Buffer.concat([Buffer.alloc(5), bomb]), 'INVALID_RESPONSE'],
    [Buffer.alloc(65537), 'INVALID_RESPONSE'], [Buffer.concat([Buffer.from([1, 0, 0, 0, 0]), valid]), 'INVALID_RESPONSE'],
    [valid, 'INVALID_RESPONSE'], ...[false, '0', null, 0.5].map(code => [ack({ code }), 'INVALID_RESPONSE']),
    [ack({}), 'INVALID_RESPONSE'],
  ];
  for (const [bytes, errorCode] of cases) {
    const f = qqFeedback(() => response(bytes)), state = await qqStart(f); f.advance(2000);
    assert.deepEqual(await f.request('tx', 'feedback.send', qqEnd(state.sessionId)), { status: 'unknown', errorCode });
    assert.equal(f.requests.length, 7); await f.close();
  }
  const f = qqFeedback(() => response(ack({ code: 0 }), { status: 201 })), state = await qqStart(f); f.advance(2000);
  assert.deepEqual(await f.request('tx', 'feedback.send', qqEnd(state.sessionId)), { status: 'unknown', errorCode: 'INVALID_RESPONSE' });
  await f.close();
});

const csccSalt = 'LnT6xpN3khm36zse0QzvmgTZ3waWdRSA';
function capturePortrait(t) {
  let portrait;
  const original = crypto.publicEncrypt;
  t.mock.method(crypto, 'publicEncrypt', (options, bytes) => {
    if (Buffer.from(bytes).includes(9)) portrait = Buffer.from(bytes).toString();
    return original(options, bytes);
  });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  return () => portrait;
}
function kgFeedback(portrait, { gen, post = () => response({ status: 1 }), metadata, delay } = {}) {
  const events = [], f = fixture(request => {
    const url = new URL(request.url), query = Object.fromEntries(url.searchParams);
    if (url.pathname === '/kmr/v2/audio') {
      const value = kg(request); assert.equal(request.headers['x-router'], 'openapi.kugou.com');
      assert.equal(request.headers['KG-TID'], '238'); assert.deepEqual(value.body.data, [{ entity_id: 22 }]);
      delay?.(f);
      return response(metadata ?? { status: 1, data: [{ __status: 1, base: { album_audio_id: 22 },
        audio_info: { hash: kgHash, timelength: 180000 } }] });
    }
    assert.equal(url.origin, 'http://d.kugou.com');
    assert.equal(request.headers['Cache-Control'], 'no-store');
    for (const key of Object.keys(request.headers)) assert.ok(!['cookie', 'authorization', 'x-router'].includes(key.toLowerCase()));
    for (const key of ['token', 'userid', 'vip_token']) assert.equal(query[key], undefined);
    assert.ok(!request.url.includes('synthetic-token'));
    assert.ok(!Object.values(request.headers).some(value => value.includes('synthetic-token')));
    if (url.pathname === '/v3/qrydid') {
      assert.equal(request.method, 'POST'); assert.equal(query.appid, 'and02');
      assert.deepEqual(Object.keys(query).sort(), ['_t', 'appid', 'sign']);
      const body = JSON.parse(request.body);
      assert.deepEqual(body.wh, [1920, 1080]); assert.equal(body.uuid, '0123456789abcdef0123456789abcdef');
      assert.equal(body.machine, '0123456789');
      assert.equal(body.mid, '176905054734845483393488264506404844848');
      assert.equal(query.sign, md5('_t' + query._t + 'appidand02pbKC7zn{4U*ydo2M1Rir' + request.body));
      return response({ status: 1, data: { deviceid: 'synthetic-did' } });
    }
    const [uuid, field, timestamp, did] = portrait().split('\t');
    assert.equal(did, 'synthetic-did'); assert.equal(Number(timestamp), now + (delay ? 10000 : 0));
    if (url.pathname === '/v2/gen') {
      assert.equal(request.method, 'GET'); assert.equal(request.body, undefined); assert.equal(query.appid, '3116');
      assert.equal(request.headers['User-Agent'], 'Android9-1070-10597-18-0-CsccGen-wifi');
      assert.deepEqual(Object.keys(query).sort(), ['_t', 'appid', 's', 'sign']);
      assert.equal(Buffer.from(query.s, 'base64').length, 128);
      assert.equal(query.sign, md5('_t' + query._t + 'appid3116s' + query.s + csccSalt));
      return gen?.() ?? response({ status: 1, data: encrypt(JSON.stringify({ cookie: 'synthetic-cscc-cookie',
        clienttime: 1800000000, serverstr: 'synthetic-server' }), field, 'cbc', uuid.slice(0, 16)).toString('base64') });
    }
    assert.equal(url.pathname, '/v2/post'); assert.equal(request.method, 'POST');
    assert.equal(request.headers['User-Agent'], 'Android9-1070-10597-18-0-CsccPost-wifi');
    assert.equal(request.headers['Content-Type'], 'application/octet-stream');
    assert.deepEqual(Object.keys(query).sort(), ['_t', 'appid', 'cookie', 'length', 'sign']);
    assert.equal(query.cookie, 'synthetic-cscc-cookie'); assert.equal(query.appid, '3116');
    const signed = Object.keys(query).filter(key => key !== 'sign').sort().map(key => key + query[key]).join('');
    assert.equal(query.sign, md5(Buffer.concat([Buffer.from(signed + csccSalt), request.body])));
    const key = md5(uuid + '1800000000' + field + 'synthetic-server');
    const plain = inflateSync(decrypt(request.body, key, 'cbc', uuid.slice(0, 16)));
    assert.equal(query.length, String(plain.length));
    const one = plain.indexOf('\r\n'), two = plain.indexOf('\r\n', one + 2), event = inflateSync(plain.subarray(two + 2));
    assert.deepEqual(plain.subarray(0, one).toString().split('\t'), ['4', '1', uuid, '0', '3116', '10597', '18', '9',
      '0123456789', uuid, '176905054734845483393488264506404844848', '0', '000000000000000000000000000000000000']);
    assert.deepEqual(plain.subarray(one + 2, two).toString().split('\t'),
      [String(event.length), '10048', '0', query._t, '1', '42', '0', '0']);
    const fields = Object.fromEntries(event.toString().split('&').map(pair => pair.split('=')));
    for (const key of ['source', 'fo', 'token']) assert.equal(fields[key], undefined);
    assert.ok(!event.toString().includes('synthetic-token')); events.push(fields);
    return post(request, events.length);
  });
  seedClients(f); return { ...f, events };
}
const kgStart = { event: 'start', target: { nativeId: kgHash, mixsongId: '22', automatic: true },
  duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1 };
const kgEnd = sessionId => ({ event: 'end', sessionId, duration: 180, listened: 12.5,
  mediaListened: 25, position: 25, rate: 2, reason: 'interrupt', target: kgStart.target });

test('KG exact metadata and anonymous CSCC handshake bind real double-zlib AES signed start/end without credential leakage', async t => {
  const f = kgFeedback(capturePortrait(t)); await f.session('kg');
  const state = await f.request('kg', 'feedback.send', kgStart); assert.equal(state.status, 'confirmed');
  f.advance(12500);
  assert.equal((await f.request('kg', 'feedback.send', kgEnd(state.sessionId))).status, 'confirmed');
  assert.equal(f.events[0].type_id, '20431'); assert.equal(f.events[0].spt, '180000');
  assert.equal(f.events[1].type_id, '4'); assert.equal(f.events[1].st, '180000'); assert.equal(f.events[1].spt, '25000');
  assert.equal(f.events[1].duration, '12500'); assert.equal(f.events[1].state, '被终止');
  assert.equal(f.events[1].sty, '自动'); assert.equal(f.events[1].fs, '2.0'); assert.equal(f.requests.length, 5);
  await f.close();
});

test('KG unused playback facts and CSCC grade metadata cannot change actual start/end records or confirmation', async t => {
  const f = kgFeedback(capturePortrait(t), { post: () => response({ status: 1, data: { grade_synced: { unused: true } } }) });
  t.after(() => f.close()); await f.session('kg');
  const state = await f.request('kg', 'feedback.send', { ...kgStart, listened: {}, mediaListened: {}, position: {}, rate: {} });
  assert.equal(state.status, 'confirmed'); assert.ok(state.sessionId);
  f.advance(12500);
  const result = await f.request('kg', 'feedback.send', { ...kgEnd(state.sessionId), mediaListened: { toString: null } });
  assert.deepEqual(result, { status: 'confirmed', errorCode: null });
  assert.equal(f.events[0].spt, '180000'); assert.equal(f.events[1].duration, '12500');
  assert.equal(f.events[1].spt, '25000'); assert.equal(f.events[1].fs, '2.0');
  assert.equal(f.requests.length, 5);
});

test('KG bad encrypted handshake and wrong recording/duration cannot send a start or manufacture a session', async t => {
  const portrait = capturePortrait(t);
  for (const options of [
    { gen: () => response({ status: 1, data: 'not-encrypted' }) },
    { metadata: { status: 1, data: [{ __status: 1, base: { album_audio_id: 22 },
      audio_info: { hash: 'OTHER_RECORDING', timelength: 180000 } }] } },
    { metadata: { status: 1, data: [{ __status: 1, base: { album_audio_id: 22 },
      audio_info: { hash: kgHash, timelength: 190001 } }] } },
  ]) {
    const f = kgFeedback(portrait, options); await f.session('kg');
    const result = await f.request('kg', 'feedback.send', kgStart);
    assert.equal(result.status, 'notSent'); assert.equal(result.sessionId, undefined);
    assert.equal(result.errorCode, options.gen ? 'INVALID_RESPONSE' : 'REMOTE_CHANGED');
    assert.equal(f.events.length, 0); assert.equal(f.requests.length, options.gen ? 3 : 1); await f.close();
  }
});

test('KG CSCC requires actual status and exact HTTP success; dispatched failures remain settled once', async t => {
  const portrait = capturePortrait(t);
  for (const [raw, status, errorCode, httpStatus = 200] of [
    [{}, 'unknown', 'INVALID_RESPONSE'], [{ status: true }, 'unknown', 'INVALID_RESPONSE'],
    [{ status: '1' }, 'unknown', 'INVALID_RESPONSE'], [{ status: 0 }, 'rejected', 'UPSTREAM_ERROR'],
    [{ status: 1, errcode: 7 }, 'rejected', 'UPSTREAM_ERROR'],
    [{ status: 1, error_code: 1 }, 'rejected', 'UPSTREAM_ERROR'],
    [{ status: 1, errcode: false }, 'unknown', 'INVALID_RESPONSE'],
    [{ status: 1, error_code: 'not-integer' }, 'unknown', 'INVALID_RESPONSE'],
    [{ status: 1, errcode: 0.5 }, 'unknown', 'INVALID_RESPONSE'],
    [{ status: 1 }, 'unknown', 'INVALID_RESPONSE', 201],
    ['not-json', 'unknown', 'INVALID_RESPONSE'],
  ]) {
    const f = kgFeedback(portrait, { post: () => response(raw, { status: httpStatus }) }); await f.session('kg');
    const result = await f.request('kg', 'feedback.send', kgStart);
    assert.equal(result.status, status); assert.equal(result.errorCode, errorCode); assert.equal(result.sessionId, null);
    assert.equal(f.requests.length, 4); assert.equal(f.events.length, 1); await f.close();
  }
});

test('KG feedback retains recording duration association and invalid ends do not consume a session', async t => {
  const f = kgFeedback(capturePortrait(t)); await f.session('kg');
  const state = await f.request('kg', 'feedback.send', kgStart);
  f.advance(12500);
  const baseline = f.requests.length;
  for (const facts of [{ duration: 190.001 }, { duration: 'invalid' }, { position: 'invalid' },
    { listened: 'invalid' }, { listened: 15 }, { rate: 'invalid' }, { reason: 'playend', position: 178 }]) {
    assert.deepEqual(await f.request('kg', 'feedback.send', { ...kgEnd(state.sessionId), ...facts }),
      { status: 'notSent', errorCode: 'INVALID_REQUEST' });
    assert.equal(f.requests.length, baseline);
  }
  assert.equal((await f.request('kg', 'feedback.send', kgEnd(state.sessionId))).status, 'confirmed');
  assert.equal(f.requests.length, baseline + 1); await f.close();
});

test('KG elapsed listening starts before preparation I/O and uses the actual session start', async t => {
  const f = kgFeedback(capturePortrait(t), { delay(owner) { owner.advance(10000); } });
  t.after(() => f.close()); await f.session('kg');
  const state = await f.request('kg', 'feedback.send', kgStart), before = f.requests.length;
  assert.deepEqual(await f.request('kg', 'feedback.send', { ...kgEnd(state.sessionId), listened: 13 }),
    { status: 'notSent', errorCode: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, before);
  assert.equal((await f.request('kg', 'feedback.send', { ...kgEnd(state.sessionId), listened: 12 })).status, 'confirmed');
  assert.equal(f.events[1].duration, '12000');
});

test('KG late confirmed start after account retirement keeps the receipt but cannot publish a feedback session', async t => {
  const portrait = capturePortrait(t), held = gate(), controller = new AbortController();
  const f = kgFeedback(portrait, { post: async () => { await held.wait(); return response({ status: 1 }); } });
  t.after(async () => { held.release(); await f.close(); });
  await f.session('kg');
  const work = f.request('kg', 'feedback.send', kgStart, { signal: controller.signal });
  await held.entered; controller.abort(); await f.owner.sessionUpdate({ platform: 'kg', session: null });
  assert.equal(f.requests.at(-1).signal.aborted, false); held.release();
  assert.deepEqual(await work, { status: 'confirmed', errorCode: null, sessionId: null });
  assert.equal(f.events.length, 1);
});
