import test from 'node:test';
import assert from 'node:assert/strict';
import { gzipSync } from 'node:zlib';
import { fixture, response, rpc, qq, seedClients, xeapi, encrypt, gate, now } from './support.mjs';

// HEAD MusicNeteaseProtocolTests official session-header and encrypted-response
// vectors; MusicQQPublicPersistenceTests TTL/day/bootstrap/failure vectors.
const audio = { code: 200, data: [{ code: 200, url: 'https://audio.invalid/full', level: 'lossless', time: 240000 }] };
const resolve = f => f.request('wy', 'audio', { id: '123', quality: 'flac' });

test('XEAPI authenticates canonical positive safe millisecond values before any media request', async () => {
  const invalid = [true, 0, -1, 1.5, 9007199254740992, '001700000000000', '1e12', 'not-a-time'];
  for (const timestamp of [now, String(now), ...invalid]) {
    const server = xeapi();
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/music/detail/get')) return response({ code: 200, data: { sq: {} } });
      if (path === '/api/bsr/sk/get') return server.key(request, { timestamp });
      server.open(request); return server.reply(audio);
    }); seedClients(f);
    if (invalid.includes(timestamp)) {
      await assert.rejects(resolve(f), { code: 'INVALID_RESPONSE' });
      assert.equal(f.requests.length, 2, 'even an authenticated malformed timestamp cannot reach media');
    } else {
      assert.equal((await resolve(f)).full, true);
      assert.equal(f.requests.length, 3);
    }
    await f.close();
  }
  const server = xeapi();
  const f = fixture(request => new URL(request.url).pathname.endsWith('/music/detail/get') ?
    response({ code: 200, data: { sq: {} } }) : server.key(request, { signature: 'not-a-signature' }));
  seedClients(f);
  await assert.rejects(resolve(f), { code: 'INVALID_RESPONSE' });
  assert.equal(f.requests.length, 2); await f.close();
});

test('XEAPI distinguishes malformed handshake status from explicit refusal before consuming success data', async t => {
  const values = [
    ['object', { code: {}, data: {} }, 'INVALID_RESPONSE'],
    ['array', { code: [], data: {} }, 'INVALID_RESPONSE'],
    ['boolean', { code: false, data: {} }, 'INVALID_RESPONSE'],
    ['empty', { code: '', data: {} }, 'INVALID_RESPONSE'],
    ['rejected', { code: 503 }, 'UPSTREAM_ERROR'],
    ['rejected-string', { code: '503' }, 'UPSTREAM_ERROR'],
    ['success-without-data', { code: 200 }, 'INVALID_RESPONSE'],
  ];
  for (const [name, value, code] of values) await t.test(name, async t => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/music/detail/get')) return response({ code: 200, data: { sq: {} } });
      assert.equal(path, '/api/bsr/sk/get');
      return response(value);
    }); seedClients(f);
    t.after(() => f.close());
    const before = Buffer.from(f.files.get('clients.json'));
    await assert.rejects(resolve(f), { code });
    assert.deepEqual(f.requests.map(r => new URL(r.url).pathname), ['/eapi/song/music/detail/get', '/api/bsr/sk/get']);
    assert.equal(f.writes.length, 0);
    assert.deepEqual(f.files.get('clients.json'), before);
  });
});

test('XEAPI optional handshake status preserves missing, null and convertible success on the authenticated media path', async t => {
  for (const [name, code] of [['missing', undefined], ['null', null], ['success-string', '200']]) await t.test(name, async t => {
    const server = xeapi();
    const f = fixture(async request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/music/detail/get')) return response({ code: 200, data: { sq: {} } });
      if (path === '/api/bsr/sk/get') {
        const value = await server.key(request).json(); value.code = code;
        return response(value);
      }
      assert.equal(path, '/xeapi/song/enhance/player/url/v1');
      server.open(request); return server.reply(audio);
    }); seedClients(f);
    t.after(() => f.close());
    assert.equal((await resolve(f)).full, true);
    assert.deepEqual(f.requests.map(r => new URL(r.url).pathname),
      ['/eapi/song/music/detail/get', '/api/bsr/sk/get', '/xeapi/song/enhance/player/url/v1']);
    assert.equal(f.writes.length, 0);
  });
});

test('official XEAPI session headers reuse 16/24/32-byte keys and revocation performs a fresh anonymous handshake', async t => {
  for (const count of [16, 24, 32]) await t.test(String(count), async () => {
    const server = xeapi(), key = 'A'.repeat(count);
    let calls = 0;
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      assert.ok(!request.headers.Cookie.includes('MUSIC_U'));
      if (path.endsWith('/music/detail/get')) return response({ code: 200, data: { sq: {} } });
      if (path === '/api/bsr/sk/get') return server.key(request);
      assert.equal(path, '/xeapi/song/enhance/player/url/v1');
      const reused = calls++ === 1;
      const params = server.open(request, reused ? { sessionKey: key, sessionID: 'synthetic-reuse' } : {});
      assert.equal(params.get('level'), 'lossless'); assert.equal(params.get('e_r'), null);
      return server.reply(audio, { headers: { 'x-encr-ssid': 'synthetic-reuse', 'x-encr-sskey': key } });
    }); seedClients(f);
    await resolve(f); await resolve(f);
    assert.equal(f.requests.filter(r => new URL(r.url).pathname === '/api/bsr/sk/get').length, 1);
    await f.owner.sessionUpdate({ platform: 'wy', session: null });
    await resolve(f);
    assert.equal(f.requests.filter(r => new URL(r.url).pathname === '/api/bsr/sk/get').length, 2);
    assert.equal(f.writes.length, 0, 'ephemeral encryption state is not an account or durable client document');
    await f.close();
  });
});

test('malformed official session fields fail at the encrypted response without automatic replay', async () => {
  const invalid = [0, 15, 17, 23, 25, 31, 33].map(length => ['synthetic-session', 'A'.repeat(length)]);
  invalid.push(['', 'A'.repeat(32)], ['S'.repeat(513), 'A'.repeat(32)]);
  for (const [session, key] of invalid) {
    const server = xeapi();
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/music/detail/get')) return response({ code: 200, data: { sq: {} } });
      if (path === '/api/bsr/sk/get') return server.key(request);
      server.open(request);
      return server.reply(audio, { headers: { 'x-encr-ssid': session, 'x-encr-sskey': key } });
    }); seedClients(f);
    await assert.rejects(resolve(f), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 3); await f.close();
  }
});

test('encrypted audio consumes one gzip JSON document and rejects corruption, trailing bytes, bad padding and byte bombs', async () => {
  const gzip = gzipSync(Buffer.from(JSON.stringify(audio)));
  const values = [
    [encrypt(gzip, 'e82ckenh8dichen8'), null],
    [encrypt(gzip.subarray(0, -1), 'e82ckenh8dichen8'), 'INVALID_RESPONSE'],
    [encrypt(Buffer.concat([gzip, Buffer.from([1])]), 'e82ckenh8dichen8'), 'INVALID_RESPONSE'],
    [Buffer.alloc(15), 'INVALID_RESPONSE'],
    [Buffer.from('CkxDe2y6GLX6q3tUVB3MWg==', 'base64'), 'INVALID_RESPONSE'],
    [Buffer.from('BK+u/eo6ubcSKJsnT6mW5A==', 'base64'), 'INVALID_RESPONSE'],
    [Buffer.from('ZW1y8lrlpR85IKm1Tsz3kA==', 'base64'), 'INVALID_RESPONSE'],
    [Buffer.from('qkIOF2CwpVUkeAIFRlB9cQ==', 'base64'), 'INVALID_RESPONSE'],
    [encrypt(gzipSync(Buffer.alloc(4 * 1024 * 1024 + 1, 32)), 'e82ckenh8dichen8'), 'LIMIT'],
  ];
  for (const [bytes, code] of values) {
    const server = xeapi();
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/music/detail/get')) return response({ code: 200, data: { sq: {} } });
      if (path === '/api/bsr/sk/get') return server.key(request);
      return response(bytes);
    }); seedClients(f);
    if (code) await assert.rejects(resolve(f), { code });
    else assert.equal((await resolve(f)).full, true);
    assert.equal(f.requests.length, 3); await f.close();
  }
});

const qimei = () => response({ code: 0, data: JSON.stringify({ code: 0, data: { q16: 'new-q16', q36: 'new-q36' } }) });
const method = request => new URL(request.url).hostname === 'api.tencentmusic.com' ? 'GetQimei' : qq(request).req_0.method;
const search = f => f.request('tx', 'search', { keyword: 'synthetic' });
function qqReply(request) {
  if (method(request) === 'GetQimei') return qimei();
  if (method(request) === 'GetSession') {
    const root = qq(request);
    assert.equal(request.headers.Cookie, undefined); assert.equal(root.comm.authst, undefined);
    assert.equal(root.comm.qq, undefined); assert.equal(root.comm.uid, undefined); assert.equal(root.comm.sid, undefined);
    assert.equal(root.req_0.param.vkey, 0);
    return rpc({ session: { uid: 'new-uid', sid: 'new-sid' } });
  }
  return rpc({ body: { item_song: [] }, meta: { sum: 0, nextpage: -1 } });
}

test('QIMEI optional status separates conversion failures and explicit refusals without dependent requests or successful state saves', async t => {
  const values = [
    ['missing', undefined, null], ['null', null, null], ['success-string', '0', null],
    ['object', {}, 'INVALID_RESPONSE'], ['array', [], 'INVALID_RESPONSE'],
    ['boolean', false, 'INVALID_RESPONSE'], ['empty', '', 'INVALID_RESPONSE'],
    ['rejected', 503, 'UPSTREAM_ERROR'], ['rejected-string', '503', 'UPSTREAM_ERROR'],
  ];
  for (const stage of ['outer', 'inner']) for (const [name, value, code] of values) await t.test(stage + ':' + name, async t => {
    const reply = { code: stage === 'outer' ? value : 0, data: JSON.stringify({
      code: stage === 'inner' ? value : 0, data: { q16: 'new-q16', q36: 'new-q36' },
    }) };
    const f = fixture(request => method(request) === 'GetQimei' ? response(reply) : qqReply(request));
    seedClients(f);
    t.after(() => f.close());
    const state = JSON.parse(f.files.get('clients.json')); state.tx.qimei = null; state.tx.session = null;
    const before = Buffer.from(JSON.stringify(state)); f.files.set('clients.json', before);
    if (code) {
      await assert.rejects(search(f), { code });
      assert.deepEqual(f.requests.map(method), ['GetQimei']);
      assert.equal(f.writes.length, 0);
      assert.deepEqual(f.files.get('clients.json'), before);
    } else {
      assert.deepEqual((await search(f)).items, []);
      assert.deepEqual(f.requests.map(method), ['GetQimei', 'GetSession', 'DoSearchForQQMusicMobile']);
      const saved = JSON.parse(f.files.get('clients.json')).tx;
      assert.deepEqual(saved.qimei, { q16: 'new-q16', q36: 'new-q36', savedAt: now });
      assert.deepEqual(saved.session, { uid: 'new-uid', sid: 'new-sid', savedAt: now });
    }
  });
});

test('QIMEI expires exactly at 24h, backward clocks refresh real state and stable anonymous identity never rotates', async () => {
  const f = fixture(qqReply); seedClients(f);
  const identity = JSON.parse(f.files.get('clients.json')).tx;
  f.advance(86399999); await search(f);
  assert.equal(f.requests.filter(r => method(r) === 'GetQimei').length, 0);
  f.advance(1); await search(f);
  assert.equal(f.requests.filter(r => method(r) === 'GetQimei').length, 1);
  f.advance(-60000); await search(f);
  assert.equal(f.requests.filter(r => method(r) === 'GetQimei').length, 2);
  const current = JSON.parse(f.files.get('clients.json')).tx;
  for (const key of ['installationId', 'androidId', 'openUdid', 'openUdid2', 'imei']) assert.equal(current[key], identity[key]);
  assert.equal(f.requests.filter(r => method(r) === 'GetSession').length, 3);
  await f.close();
});

test('Shanghai midnight refreshes only the anonymous session using the previous UID', async () => {
  const f = fixture(request => {
    if (method(request) === 'GetSession') {
      const param = qq(request).req_0.param;
      assert.equal(param.caller, 1); assert.equal(param.uid, 'synthetic-uid');
    }
    return qqReply(request);
  }); seedClients(f);
  const day = 86400000, midnight = (Math.floor((now + 8 * 3600000) / day) + 1) * day - 8 * 3600000;
  f.advance(midnight - now - 1000); await search(f);
  f.advance(2000); await search(f);
  assert.equal(f.requests.filter(r => method(r) === 'GetSession').length, 1);
  assert.equal(f.requests.filter(r => method(r) === 'GetQimei').length, 0); await f.close();
});

test('delayed QIMEI or GetSession cannot extend the TTL or relabel the session as the next Shanghai day', async t => {
  for (const stage of ['GetQimei', 'GetSession']) await t.test(stage, async t => {
    const held = gate();
    let delayed = false;
    const f = fixture(async request => {
      if (!delayed && method(request) === stage) { delayed = true; await held.wait(); }
      return qqReply(request);
    }); seedClients(f);
    t.after(async () => { held.release(); await f.close(); });
    const state = JSON.parse(f.files.get('clients.json')); state.tx.qimei = null; state.tx.session = null;
    f.files.set('clients.json', Buffer.from(JSON.stringify(state)));
    const day = 86400000, start = (Math.floor((now + 8 * 3600000) / day) + 1) * day - 8 * 3600000 - 1000;
    f.advance(start - now);
    const work = search(f); await held.entered; f.advance(2000); held.release(); await work;
    let saved = JSON.parse(f.files.get('clients.json')).tx;
    assert.equal(saved.qimei.savedAt, start); assert.equal(saved.session.savedAt, start);
    await search(f);
    assert.equal(f.requests.filter(r => method(r) === 'GetSession').length, 2);
    assert.equal(f.requests.filter(r => method(r) === 'GetQimei').length, 1);
    f.advance(day - 2000); await search(f);
    saved = JSON.parse(f.files.get('clients.json')).tx;
    assert.equal(saved.qimei.savedAt, start + day);
    assert.equal(f.requests.filter(r => method(r) === 'GetQimei').length, 2);
  });
});

test('partial preparation failures cold-reload only committed state and invalid GetSession never reaches business HTTP', async () => {
  for (const stage of ['GetQimei', 'GetSession', 'invalid-session']) {
    const failed = fixture(request => {
      if (method(request) === (stage === 'invalid-session' ? 'GetSession' : stage)) return stage === 'invalid-session' ?
        rpc({ session: { uid: 'new-uid', sid: '' } }) : response('', { status: 503 });
      return qqReply(request);
    });
    await assert.rejects(search(failed), { code: stage === 'invalid-session' ? 'INVALID_RESPONSE' : 'UPSTREAM_HTTP' });
    assert.ok(!failed.requests.some(r => method(r) === 'DoSearchForQQMusicMobile'));
    const saved = JSON.parse(failed.files.get('clients.json')).tx;
    assert.equal(saved.session, null);
    assert.equal(saved.qimei === null, stage === 'GetQimei');
    await failed.close();
    const cold = fixture(qqReply, { files: failed.files });
    await search(cold);
    const current = JSON.parse(cold.files.get('clients.json')).tx;
    assert.equal(current.openUdid, saved.openUdid);
    assert.equal(cold.requests.filter(r => method(r) === 'GetQimei').length, stage === 'GetQimei' ? 1 : 0);
    assert.equal(cold.requests.filter(r => method(r) === 'GetSession').length, 1); await cold.close();
  }
});
