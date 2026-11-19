import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, rpc, qq, seedClients, gate, wySong, nativeHTTP } from './support.mjs';
import { createMusicTransport } from '../src/transport.mjs';
import { MusicPlatformError } from '../src/index.mjs';
import { gzipSync, brotliCompressSync } from 'node:zlib';

// HEAD Clients/{MusicNativeClientContextTests,MusicClientStorageFailureTests}
// and QQ/MusicQQPublicPersistenceTests: faults surround actual current SQLite I/O.
test('a failed current-row read dispatches nothing and never manufactures a replacement installation', async () => {
  const f = fixture(() => assert.fail('unreadable identity cannot reach HTTP'), {
    beforeRead() { throw Error('synthetic read failure'); },
  });
  await assert.rejects(f.request('tx', 'search', { keyword: 'x' }), { code: 'STORAGE_ERROR' });
  assert.equal(f.requests.length, 0); assert.equal(f.writes.length, 0); await f.close();
});

test('an unknown initial save is reloaded, never retried with another installation or used before acknowledgement', async () => {
  let unknown = true;
  const f = fixture(() => response({ code: 200, data: [wySong('1')] }), {
    afterWrite() { if (unknown) { unknown = false; throw Error('lost save acknowledgement'); } },
  });
  await assert.rejects(f.request('wy', 'fm'), { code: 'COMMIT_UNKNOWN' });
  assert.equal(f.requests.length, 0);
  const saved = await f.clientState();
  await f.request('wy', 'fm');
  assert.ok(f.requests[0].headers.Cookie.includes('deviceId=' + saved.wy.cookies.deviceId));
  assert.equal(f.writes.length, 1, 'reload of the applied save is not a replay'); await f.close();
  const cold = fixture(() => response({ code: 200, data: [] }), { dataRoot: f.root });
  await cold.request('wy', 'fm'); assert.equal(cold.writes.length, 0);
  assert.ok(cold.requests[0].headers.Cookie.includes('deviceId=' + saved.wy.cookies.deviceId)); await cold.close();
});

test('an unknown QIMEI save reloads the applied value or actual durable winner, without replaying its HTTP', async () => {
  for (const conflict of [false, true]) {
    let unknown = true;
    const f = fixture(request => {
      if (new URL(request.url).hostname === 'api.tencentmusic.com')
        return response({ code: 0, data: JSON.stringify({ code: 0, data: { q36: 'new-q36' } }) });
      const root = qq(request);
      assert.equal(root.comm.QIMEI36, conflict ? 'winner-q36' : 'new-q36');
      if (root.req_0.method === 'GetSession') return rpc({ session: { uid: 'new-uid', sid: 'new-sid' } });
      return rpc({ body: { item_song: [] }, meta: { sum: 0, nextpage: -1 } });
    }, {
      async afterWrite(platform, value, backing) {
        if (unknown) {
          unknown = false;
          if (conflict) {
            await backing.writePlatform(platform, { ...value, qimei: { ...value.qimei, q36: 'winner-q36' } });
          }
          throw Error('save acknowledgement unavailable');
        }
      },
    }); seedClients(f);
    const saved = await f.clientState(); saved.tx.qimei = null; saved.tx.session = null;
    await f.setClientState(saved);
    await assert.rejects(f.request('tx', 'search', { keyword: 'x' }), { code: 'COMMIT_UNKNOWN' });
    assert.equal(f.requests.length, 1, 'no GetSession or business use after an unconfirmed save');
    await f.request('tx', 'search', { keyword: 'x' });
    assert.equal(f.requests.length, 3); assert.equal(f.writes.length, 2);
    assert.equal(f.requests.filter(row => new URL(row.url).hostname === 'api.tencentmusic.com').length, 1);
    await f.close();
    const cold = fixture(() => rpc({ body: { item_song: [] }, meta: { sum: 0, nextpage: -1 } }), { dataRoot: f.root });
    await cold.request('tx', 'search', { keyword: 'x' }); assert.equal(cold.requests.length, 1);
    assert.equal(cold.writes.length, 0);
    assert.equal(qq(cold.requests[0]).comm.QIMEI36, conflict ? 'winner-q36' : 'new-q36'); await cold.close();
  }
});

test('anonymous context projects consumed identity fields and cold reload keeps fixed identity and session rows', async t => {
  const f = fixture(request => qq(request).req_0.method === 'GetSession' ?
    rpc({ session: { uid: 'new-uid', sid: 'new-sid' } }) :
    rpc({ body: { item_song: [] }, meta: { sum: 0, nextpage: -1 } }));
  t.after(() => f.close());
  seedClients(f);
  const state = (await f.clientState()); state.tx.session = null;
  const before = await f.clientState();
  state.extension = { number: '9007199254740995' };
  state.wy.extension = { unrelated: false };
  state.wy.cookies.unrelated = { ignored: true };
  state.wy.cookies.MUSIC_U = 'synthetic-account-credential';
  state.wy.cookies.__csrf = 'synthetic-account-csrf';
  state.kg.extension = { unrelated: false };
  state.tx.extension = { unrelated: false };
  state.tx.qimei.extension = { unrelated: false };
  await f.setClientState(state);
  const projected = await f.clientState();
  assert.equal(projected.extension, undefined); assert.deepEqual(projected.wy, before.wy);
  assert.deepEqual(projected.kg, before.kg); assert.deepEqual(projected.tx.qimei, before.tx.qimei);
  assert.equal(projected.tx.extension, undefined);
  assert.deepEqual(await f.database.query(
    "SELECT cookie_name FROM music_anonymous_wy_cookies WHERE cookie_name IN ('MUSIC_U','__csrf','unrelated')"), []);
  await f.request('tx', 'search', { keyword: 'x' });
  const saved = await f.clientState();
  assert.deepEqual(saved.wy, before.wy); assert.deepEqual(saved.kg, before.kg);
  assert.equal(f.writes.length, 1); assert.equal(saved.tx.session.uid, 'new-uid');
  const cold = fixture(() => rpc({ body: { item_song: [] }, meta: { sum: 0, nextpage: -1 } }), { dataRoot: f.root });
  t.after(() => cold.close());
  const snapshot = await cold.clientState();
  await cold.request('tx', 'search', { keyword: 'x' });
  assert.equal(cold.writes.length, 0); assert.deepEqual(await cold.clientState(), snapshot);
  assert.equal(qq(cold.requests[0]).comm.uid, 'new-uid');
});

test('a cancelled QQ waiter does not cancel shared prepare; close aborts and drains prepare without saving late state', async t => {
  for (const closing of [false, true]) await t.test(String(closing), async t => {
    const held = gate(), controller = new AbortController();
    const f = fixture(async request => {
      if (new URL(request.url).hostname === 'api.tencentmusic.com') {
        await held.wait();
        return response({ code: 0, data: JSON.stringify({ code: 0, data: { q36: 'new-q36' } }) });
      }
      if (qq(request).req_0.method === 'GetSession') return rpc({ session: { uid: 'new-uid', sid: 'new-sid' } });
      return rpc({ body: { item_song: [] }, meta: { sum: 0, nextpage: -1 } });
    }); seedClients(f);
    const saved = (await f.clientState()); saved.tx.qimei = null; saved.tx.session = null;
    await f.setClientState(saved);
    t.after(async () => { held.release(); await f.close(); });
    const first = f.request('tx', 'search', { keyword: 'a' }, { signal: controller.signal });
    first.catch(() => {});
    const joined = f.request('tx', 'search', { keyword: 'b' }); joined.catch(() => {});
    await held.entered; controller.abort();
    await assert.rejects(first, { code: 'CANCELLED' });
    assert.equal(f.requests[0].signal.aborted, false);
    if (closing) {
      let drained = false;
      const close = f.close().then(() => { drained = true; });
      assert.equal(f.requests[0].signal.aborted, true);
      await Promise.resolve(); assert.equal(drained, false);
      held.release(); await close;
      await assert.rejects(joined, { code: 'CLOSED' });
      assert.equal(f.writes.length, 0); assert.equal(f.requests.length, 1);
      const cold = fixture(() => assert.fail('storage inspection must not dispatch'), { dataRoot: f.root });
      t.after(() => cold.close());
      assert.deepEqual(await cold.clientState(), saved);
    } else {
      held.release(); assert.equal((await joined).items.length, 0);
      assert.equal(f.requests.filter(row => new URL(row.url).hostname === 'api.tencentmusic.com').length, 1);
      assert.equal(f.writes.length, 2);
    }
  });
});

// HEAD Transport/MusicAccountHTTPRoutingTests and NeteaseProtocolTests:
// real HTTP construction rejects controls, not a second fixed-route allowlist.
test('actual account-derived HTTP headers reject CRLF and NUL before fetch and writes remain notSent', async () => {
  for (const bad of ['one\r\ntwo', 'one\rtwo', 'one\ntwo', 'one\0two']) {
    for (const platform of ['wy', 'tx', 'kg']) {
      const f = fixture(() => assert.fail('invalid header must never reach fetch')); seedClients(f);
      const cookies = { wy: 'MUSIC_U=' + bad, tx: 'musicid=42; musickey=' + bad, kg: 'userid=42; token=x; KUGOU_API_MID=' + bad };
      await f.session(platform, '42', cookies[platform]);
      await assert.rejects(f.request(platform, 'search', { keyword: 'x' }), { code: 'INVALID_REQUEST' });
      assert.deepEqual(await f.request(platform, 'sync.write', { action: 'create', name: 'x' }),
        { status: 'notSent', errorCode: 'INVALID_REQUEST' });
      assert.equal(f.requests.length, 0); await f.close();
    }
  }
});

test('default Node HTTP preserves URL, headers and cookies, retains large decoded bodies and never follows redirects', async t => {
  const paths = [], cookies = ['first=one; Expires=Wed, 01 Jan 2031 00:00:00 GMT', 'second=two; Path=/'];
  const payload = Buffer.from('{"value":"exact"}'), oversized = gzipSync(Buffer.alloc(4 * 1024 * 1024 + 1, 120));
  const peer = await nativeHTTP(t, (request, reply) => {
    paths.push(request.url);
    if (request.url.startsWith('/exact?')) {
      assert.equal(request.headers['x-actual'], 'yes');
      reply.writeHead(200, { 'Content-Encoding': 'gzip, br', 'Set-Cookie': cookies, 'X-Actual': 'kept' });
      reply.end(brotliCompressSync(gzipSync(payload)));
    } else if (request.url === '/limit') {
      reply.writeHead(200, { 'Content-Encoding': 'gzip' }); reply.end(oversized);
    } else if (request.url === '/redirect') {
      reply.writeHead(302, { Location: 'https://unchanged.invalid/path?variant=480&token=a%2Fb' }); reply.end('redirect');
    } else if (request.url === '/opaque') {
      reply.writeHead(200, { 'Content-Encoding': 'synthetic-unknown' }); reply.end('opaque');
    } else if (request.url === '/denied') {
      reply.writeHead(403); reply.end('denied');
    } else if (request.url === '/json') reply.end('not-json');
    else assert.fail('unexpected transport URL');
  });
  const transport = createMusicTransport(), url = 'https://synthetic.invalid/exact?variant=480&variant=960&token=a%2Fb';
  const result = await transport({ url, headers: { 'X-Actual': 'yes' } });
  peer.check();
  assert.deepEqual(result.value, { value: 'exact' }); assert.deepEqual(result.cookies, cookies);
  assert.equal(result.headers.get('X-ACTUAL'), 'kept'); assert.equal(result.headers.get('absent'), null);
  assert.equal(peer.requests[0].url, url);
  const local = await transport({ url: peer.origin + '/exact?variant=480&variant=960&token=a%2Fb', headers: { 'X-Actual': 'yes' } });
  assert.deepEqual(local.value, result.value); assert.deepEqual(local.cookies, cookies);
  assert.deepEqual((await transport({ url: 'https://synthetic.invalid/limit', json: false })).value, Buffer.alloc(4 * 1024 * 1024 + 1, 120));
  const redirect = await transport({ url: 'https://synthetic.invalid/redirect', json: false, allowRedirect: true });
  assert.equal(redirect.status, 302);
  assert.equal(redirect.headers.get('location'), 'https://unchanged.invalid/path?variant=480&token=a%2Fb');
  assert.equal(redirect.value.toString(), 'redirect');
  await assert.rejects(transport({ url: 'https://synthetic.invalid/redirect', json: false }), { code: 'UPSTREAM_HTTP', status: 302 });
  assert.equal((await transport({ url: 'https://synthetic.invalid/opaque', json: false })).value.toString(), 'opaque');
  await assert.rejects(transport({ url: 'https://synthetic.invalid/denied' }), { code: 'UPSTREAM_HTTP', status: 403 });
  await assert.rejects(transport({ url: 'https://synthetic.invalid/json' }), { code: 'INVALID_RESPONSE' });
  let dispatched = 0;
  const count = peer.requests.length;
  await assert.rejects(transport({ url, headers: { Cookie: 'one\r\ntwo' }, dispatched: () => { dispatched++; } }),
    { code: 'INVALID_REQUEST' });
  const controller = new AbortController(); controller.abort(new MusicPlatformError('CANCELLED'));
  await assert.rejects(transport({ url, signal: controller.signal, dispatched: () => { dispatched++; } }), { code: 'CANCELLED' });
  assert.equal(dispatched, 0); assert.equal(peer.requests.length, count);
  assert.deepEqual(paths, ['/exact?variant=480&variant=960&token=a%2Fb', '/exact?variant=480&variant=960&token=a%2Fb',
    '/limit', '/redirect', '/redirect', '/opaque', '/denied', '/json']);
  for (const request of peer.requests) {
    assert.equal(request.closed, true); assert.equal(request.socketClosed, true);
  }
  peer.check();
});
