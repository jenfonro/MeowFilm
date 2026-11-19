import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, rpc, eapi, qq, md5, gate, seedClients, wySong, txSong, idOf } from './support.mjs';

test('a fresh guest keeps one durable protocol identity through home actions, detail, audio, logout and restart', async () => {
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/radio/get')) return response({ code: 200, data: [wySong('1')] }, { cookies: ['MUSIC_A=synthetic-guest; Path=/'] });
    if (path.endsWith('/song/detail')) return response({ code: 200, songs: [wySong('1')] });
    if (path.endsWith('/player/url')) return response({ code: 200, data: [{ code: 200, url: 'https://audio.invalid/full.mp3', br: 128000 }] });
    assert.fail(path);
  });
  await f.request('wy', 'fm');
  await f.request('wy', 'song', { id: '1' });
  await f.request('wy', 'audio', { id: '1' });
  const cookie = f.requests[0].headers.Cookie;
  const device = /deviceId=([^;]+)/.exec(cookie)[1];
  assert.ok(f.requests.slice(1).every(row => row.headers.Cookie.includes('MUSIC_A=synthetic-guest') &&
    row.headers.Cookie.includes('deviceId=' + device)));
  await f.session('wy');
  await f.request('wy', 'audio', { id: '1' });
  assert.ok(f.requests.at(-1).headers.Cookie.includes('MUSIC_U=synthetic-music-u'));
  await f.owner.sessionUpdate({ platform: 'wy', session: null });
  await f.request('wy', 'audio', { id: '1' });
  assert.ok(f.requests.at(-1).headers.Cookie.includes('MUSIC_A=synthetic-guest'));
  assert.ok(!f.requests.at(-1).headers.Cookie.includes('MUSIC_U='));
  assert.ok(!JSON.stringify(await f.clientState()).includes('synthetic-music-u'));
  await f.close();
  const restarted = fixture(() => response({ code: 200, data: [wySong('2')] }), { dataRoot: f.root });
  await restarted.request('wy', 'fm');
  assert.ok(restarted.requests[0].headers.Cookie.includes('deviceId=' + device));
  assert.ok(restarted.requests[0].headers.Cookie.includes('MUSIC_A=synthetic-guest'));
  assert.equal(restarted.writes.length, 0);
  await restarted.close();
});

test('anonymous state must really save before dispatch', async () => {
  const f = fixture(() => assert.fail('unconfirmed device state must not be used'));
  f.failWrites();
  await assert.rejects(f.request('wy', 'fm'), { code: 'STORAGE_ERROR' });
  assert.equal(f.requests.length, 0); assert.equal(await f.clientState(), null);
  await f.close();
});

test('only the selected anonymous identity is checked and missing or corrupt identity is never reset', async t => {
  for (const [platform, corruptSQL] of [
    ['wy', "DELETE FROM music_anonymous_wy_cookies WHERE cookie_name='deviceId'"],
    ['kg', "UPDATE music_anonymous_kg SET mid='invalid-current-mid'"],
    ['kg', 'DELETE FROM music_anonymous_kg'],
    ['tx', "UPDATE music_anonymous_tx SET installation_id='invalid-current-installation'"],
    ['tx', 'DELETE FROM music_anonymous_tx'],
  ]) await t.test(platform + ':' + corruptSQL.split(' ')[0], async () => {
    const healthy = platform === 'wy' ? 'kg' : 'wy';
    const f = fixture(request => {
      if (healthy === 'wy') return response({ code: 200, data: [] });
      return response({ status: 1, error_code: 0, data: { lists: [], total: 0 } });
    }); seedClients(f);
    await f.clientState(); await f.database.execute(corruptSQL);
    const stored = await f.clientState();
    await f.request(healthy, healthy === 'wy' ? 'fm' : 'search', { keyword: 'x' });
    await assert.rejects(f.request(platform, platform === 'wy' ? 'fm' : 'search', { keyword: 'x' }),
      { code: 'CORRUPT_STORAGE' });
    assert.equal(f.requests.length, 1); assert.equal(f.writes.length, 0);
    assert.deepEqual(await f.clientState(), stored);
    await f.close();
  });
});

test('parallel WY cookie exchanges merge into the current anonymous owner and ordinary cookies expire without rotating the device', async t => {
  const first = gate(), second = gate();
  const f = fixture(async request => {
    const keyword = eapi(request).data.s;
    await (keyword === 'first' ? first : second).wait();
    return response({ code: 200, result: { songs: [], songCount: 0 } }, { cookies: keyword === 'first' ?
      ['_ntes_nuid=exchange-a; Domain=.music.163.com', 'NMTID=first', 'NMTID=last'] :
      ['WNMCID=exchange-b; Domain=interfacepc.music.163.com', 'NMTID=gone; Max-Age=0'] });
  }); seedClients(f);
  t.after(async () => { first.release(); second.release(); await f.close(); });
  const a = f.request('wy', 'search', { keyword: 'first' }), b = f.request('wy', 'search', { keyword: 'second' });
  await Promise.all([first.entered, second.entered]);
  first.release(); await a;
  assert.equal((await f.clientState()).wy.cookies.NMTID, 'last');
  second.release(); await b;
  const cookies = (await f.clientState()).wy.cookies;
  assert.equal(cookies._ntes_nuid, 'exchange-a'); assert.equal(cookies.WNMCID, 'exchange-b');
  assert.equal(cookies.NMTID, undefined); assert.equal(cookies.deviceId, 'SYNTHETIC_DEVICE');
});

test('WY cookie scope/device/authentication failures never partially accept an exchange or silently replace the installed identity', async () => {
  for (const [code, cookies, expected] of [
    [200, ['NMTID=not-partial', 'deviceId=REPLACEMENT'], 'VERIFICATION_REQUIRED'],
    [200, ['NMTID=not-partial', 'deviceId=gone; Max-Age=0'], 'VERIFICATION_REQUIRED'],
    [200, ['NMTID=not-partial', 'WNMCID=wrong-domain; Domain=foreign.invalid'], 'REDIRECT_DENIED'],
    [301, ['NMTID=not-partial'], 'AUTH_REQUIRED'],
  ]) {
    const f = fixture(() => response({ code, data: [] }, { cookies })); seedClients(f);
    const before = await f.clientState();
    await assert.rejects(f.request('wy', 'fm'), { code: expected });
    assert.deepEqual(await f.clientState(), before); assert.equal(f.writes.length, 0);
    assert.equal(f.requests.length, 1); await f.close();
  }
  const f = fixture(() => response({ code: 200, data: [] }, { cookies: [
    'NMTID=expired; Expires=Wed, 01 Jan 2020 00:00:00 GMT',
    'MUSIC_A=anonymous-current; Domain=music.163.com', 'unrelated=ignored',
  ] })); seedClients(f);
  await f.request('wy', 'fm');
  const cookies = (await f.clientState()).wy.cookies;
  assert.equal(cookies.NMTID, undefined); assert.equal(cookies.MUSIC_A, 'anonymous-current');
  assert.equal(cookies.unrelated, undefined); await f.close();
});

test('QQ QIMEI and session preparation are single-flight and save before business use', async t => {
  const held = gate();
  const f = fixture(async request => {
    const url = new URL(request.url);
    if (url.host === 'api.tencentmusic.com') {
      const body = JSON.parse(request.body);
      assert.equal(request.headers.sign, md5('qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH' + request.headers.timestamp));
      assert.equal(body.qimeiParams.extra, '{"appKey":"0AND0HD6FE4HY80F"}');
      await held.wait();
      return response({ code: 0, data: JSON.stringify({ code: 0, data: { q36: 'fresh-q36' } }) });
    }
    const value = qq(request), call = value.req_0;
    if (call.method === 'GetSession') {
      assert.ok(JSON.stringify(await f.clientState()).includes('fresh-q36'));
      return rpc({ session: { uid: 'fresh-uid', sid: 'fresh-sid' } });
    }
    assert.equal(value.comm.QIMEI36, 'fresh-q36');
    assert.equal(value.comm.uid, 'fresh-uid'); assert.equal(value.comm.sid, 'fresh-sid');
    return rpc({ body: { item_song: [txSong()] }, meta: { sum: 1, nextpage: -1 } });
  });
  t.after(async () => { held.release(); await f.close(); });
  const a = f.request('tx', 'search', { keyword: 'a' }), b = f.request('tx', 'search', { keyword: 'b' });
  await held.entered;
  assert.equal(f.requests.length, 1);
  held.release();
  assert.equal((await a).items.length, 1); assert.equal((await b).items.length, 1);
  assert.equal(f.requests.filter(row => new URL(row.url).host === 'api.tencentmusic.com').length, 1);
  assert.equal(f.requests.filter(row => new URL(row.url).host !== 'api.tencentmusic.com' && qq(row).req_0.method === 'GetSession').length, 1);
  assert.equal(f.writes.length, 3, 'initial device, QIMEI and session are separate durable confirmations');
});

test('QQ preparation save failure cannot dispatch GetSession or business HTTP', async () => {
  const f = fixture(() => response({ code: 0, data: JSON.stringify({ code: 0, data: { q36: 'fresh-q36' } }) }));
  seedClients(f);
  const clients = (await f.clientState()); clients.tx.qimei = null; clients.tx.session = null;
  await f.setClientState(clients); f.failWrites();
  await assert.rejects(f.request('tx', 'search', { keyword: 'x' }), { code: 'STORAGE_ERROR' });
  assert.equal(f.requests.length, 1);
  await f.close();
});

test('API cache uses consumed arguments, clones results and refreshes without a verification read', async () => {
  const f = fixture(() => response({ code: 200, songs: [wySong(String(f.requests.length))] }));
  const first = await f.request('wy', 'song', { id: '123' });
  first.title = 'caller mutation';
  const cached = await f.request('wy', 'song', { id: '123', unused: { unexpected: 'anything' } });
  assert.equal(cached.title, 'Synthetic (Live)'); assert.equal(f.requests.length, 1);
  const refreshed = await f.request('wy', 'song', { id: '123', refresh: true });
  assert.equal(idOf(refreshed), '2'); assert.equal(f.requests.length, 2);
  f.owner.clear({ platform: 'wy' });
  assert.equal(idOf(await f.request('wy', 'song', { id: '123' })), '3');
  assert.ok(f.requests.every(row => row.method === 'POST'));
  await f.close();
});

test('catalog subscriber cancellation does not cancel admitted shared IO, and clear prevents old cache refill', async t => {
  const held = gate(), controller = new AbortController();
  const f = fixture(async () => { await held.wait(); return response({ code: 200, songs: [wySong('1')] }); });
  t.after(async () => { held.release(); await f.close(); });
  const cancelled = f.request('wy', 'song', { id: '1' }, { signal: controller.signal });
  const joined = f.request('wy', 'song', { id: '1' });
  await held.entered; controller.abort();
  await assert.rejects(cancelled, { code: 'CANCELLED' });
  assert.equal(f.requests[0].signal.aborted, false);
  f.owner.clear({ platform: 'wy' });
  held.release();
  assert.equal(idOf(await joined), '1');
  await f.request('wy', 'song', { id: '1' });
  assert.equal(f.requests.length, 2, 'retired flight cannot repopulate a cleared cache');
});

test('session replacement acknowledges physical settlement and never publishes a late old read', async t => {
  const held = gate();
  const f = fixture(async () => { await held.wait(); return response({ code: 200, data: [wySong('1')] }); });
  t.after(async () => { held.release(); await f.close(); });
  const old = f.request('wy', 'fm'); old.catch(() => {});
  await held.entered;
  let published = false;
  const publication = f.session('wy').then(value => { published = true; return value; });
  assert.equal(f.requests[0].signal.aborted, true);
  await Promise.resolve(); assert.equal(published, false);
  held.release();
  await assert.rejects(old, { code: 'STALE_SESSION' });
  assert.deepEqual(await publication, { published: true });
  await assert.rejects(async () => f.owner.request({ platform: 'wy', operation: 'fm', expectedSession: null }), { code: 'STALE_SESSION' });
});

test('close is shared, stops admission and waits for uncooperative IO to finish', async t => {
  const held = gate();
  const f = fixture(async () => { await held.wait(); return response({ code: 200, songs: [wySong('1')] }); });
  t.after(async () => { held.release(); await f.close(); });
  const work = f.request('wy', 'song', { id: '1' }); work.catch(() => {});
  await held.entered;
  const close = f.close(); assert.equal(close, f.close());
  let done = false; close.then(() => { done = true; });
  await assert.rejects(async () => f.request('wy', 'fm'), { code: 'CLOSED' });
  assert.equal(f.requests[0].signal.aborted, true);
  await Promise.resolve(); assert.equal(done, false);
  held.release();
  await assert.rejects(work, { code: 'CLOSED' }); await close; assert.equal(done, true);
});
