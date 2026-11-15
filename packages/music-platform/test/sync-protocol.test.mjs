import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { createNeteaseTokenProvider } from '../../netease-token/src/index.js';
import { fixture, response, rpc, eapi, weapi, qq, kg, seedClients, fixedEntropy, decrypt, encrypt, md5,
  wySong, txSong, kgSong, kgHash, huge, gate, now } from './support.mjs';

// HEAD Accounts/Sync/{MusicAccountSyncProtocolTests,MusicAccountSubscriptionProtocolTests,
// MusicAccountSyncKugouTests,MusicNeteaseSubscriptionTokenTests}. Native keeps
// local preflight/revisions/authorization and the durable unknown-write journal.
test('dedicated and full reads share one-pass directory facts including owned specials, without subscribed member or token reads', async t => {
  fixedEntropy(t);
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async () => {
    let renamed = false, tokens = 0;
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (platform === 'wy') {
        if (path === '/eapi/user/playlist') return response({ code: 200, more: false, playlist: [
          { id: '501', name: 'Owned', creator: { userId: 42 }, specialType: 0, trackCount: 1 },
          { id: '503', name: renamed ? 'Changed special' : 'Owned special', creator: { userId: 42 }, specialType: 10, trackCount: 7 },
          { id: '502', name: 'Subscribed', creator: { userId: 99 }, specialType: 0, trackCount: 800 },
        ] });
        assert.equal(path, '/eapi/v6/playlist/detail'); assert.equal(eapi(request).data.id, '501');
        return response({ code: 200, playlist: { id: '501', trackIds: [{ id: '101' }], tracks: [wySong('101')] } });
      }
      if (platform === 'tx') {
        const { method, param } = qq(request).req_0;
        if (method === 'GetPlaylistByUin') return rpc({ v_playlist: [{ tid: '501', dirid: 8, uin: 42, name: 'Owned' }] });
        if (method === 'CgiGetPlaylistFavInfo') return rpc({ hasmore: 0, total: 1,
          v_list: [{ tid: '502', uin: 99, name: 'Subscribed', song_cnt: 800 }] });
        assert.equal(method, 'CgiGetDiss'); assert.equal(param.disstid, 501);
        return rpc({ dirinfo: { id: '501' }, songlist: [txSong('mid')], hasmore: 0, total_song_num: 1 });
      }
      const value = kg(request);
      if (value.path === '/v7/get_all_list') return response({ status: 1, data: { total_ver: '7', has_more: 0, info: [
        { listid: 7, type: 0, source: 1, list_create_userid: 42, name: 'Owned' },
        { listid: 8, type: 0, source: 1, is_def: 2, list_create_userid: 42, name: renamed ? 'Changed special' : 'Owned special' },
        { listid: 9, type: 1, list_create_userid: 99, name: 'Subscribed', count: 800, global_collection_id: 'public-global' },
      ] } });
      assert.equal(value.path, '/v4/get_list_all_file_v3'); assert.equal(value.body.listid, '7');
      return response({ status: 1, data: { has_more: 0, info: [kgSong(kgHash, { fileid: 11 })] } });
    }, { tokenProvider: { async get() { tokens++; assert.fail('reads cannot request write tokens'); } } });
    seedClients(f); await f.session(platform);
    const dedicated = await f.request(platform, 'sync.read', { subscriptionsOnly: true });
    const first = f.requests.length;
    assert.deepEqual(dedicated.collections, []);
    assert.equal(first, platform === 'tx' ? 2 : 1);
    const full = await f.request(platform, 'sync.read', { kinds: ['playlist'] });
    assert.deepEqual(full.directory, dedicated.directory); assert.deepEqual(full.subscriptions, dedicated.subscriptions);
    assert.equal(full.collections.length, 1); assert.equal(f.requests.length - first, first + 1);
    assert.ok(full.directory.every(row => Object.keys(row).sort().join() ===
      ['id', 'name', 'kind', 'owned', 'writable', 'ownerId', 'count', 'locator'].sort().join()));
    if (platform !== 'tx') {
      const special = full.directory.find(row => row.name === 'Owned special');
      assert.equal(special.owned, true); assert.equal(special.writable, false);
      renamed = true;
      const changed = await f.request(platform, 'sync.read', { subscriptionsOnly: true });
      assert.ok(changed.directory.some(row => row.name === 'Changed special'));
      assert.deepEqual(changed.collections, []); assert.deepEqual(changed.subscriptions, dedicated.subscriptions);
      assert.equal(f.requests.length, first * 3 + 1, 'no second-pass content proof on either read');
    }
    assert.equal(tokens, 0); await f.close();
  });
});

test('WY create/delete/rename/member-order/favorites use the exact distinct recipes and true nested acknowledgements', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    assert.equal(request.method, 'POST'); assert.ok(request.headers.Cookie.includes('MUSIC_U=synthetic-music-u'));
    if (path.startsWith('/weapi/')) {
      const value = weapi(request);
      if (path.endsWith('/create')) { assert.equal(value.privacy, 10); assert.equal(value.name, 'Native'); return response({ code: 200, id: 601 }); }
      if (path.endsWith('/remove')) assert.deepEqual(JSON.parse(value.ids), ['501']);
      else {
        assert.equal(path, '/weapi/radio/like'); assert.equal(value.trackId, '101'); assert.equal(value.like, false);
      }
    } else {
      const value = eapi(request);
      if (path === '/eapi/batch') {
        assert.deepEqual(JSON.parse(value.data['/api/playlist/update/name']), { id: '501', name: 'New' });
        return response({ code: 200, '/api/playlist/update/name': { code: 200 } });
      }
      assert.equal(path, '/eapi/playlist/manipulate/tracks'); assert.equal(value.data.pid, '501');
    }
    return response({ code: 200 });
  }); seedClients(f); await f.session('wy');
  assert.equal((await f.request('wy', 'sync.write', { action: 'create', name: 'Native' })).createdId, '601');
  for (const params of [
    { action: 'rename', name: 'New' }, { action: 'order', ids: ['102', '101'] },
    { action: 'remove', ids: ['101'] }, { action: 'add', ids: ['103'] }, { action: 'delete' },
    { action: 'remove', ids: ['101'], remote: { kind: 'favorites' } },
  ]) assert.equal((await f.request('wy', 'sync.write', { id: '501', ...params })).status, 'confirmed');
  const memberChanges = f.requests.filter(r => new URL(r.url).pathname.endsWith('/manipulate/tracks')).map(r => eapi(r).data);
  assert.deepEqual(memberChanges.map(row => row.op), ['update', 'del', 'add']);
  assert.deepEqual(memberChanges.map(row => JSON.parse(row.trackIds)), [['102', '101'], ['101'], ['103']]);
  assert.equal(f.requests.length, 7); await f.close();
});

test('QQ write lookups ignore numeric hints, use account RPCs and never emulate unsupported order', async () => {
  const methods = [];
  const f = fixture(request => {
    const root = qq(request), call = root.req_0; methods.push(call.method);
    assert.equal(root.comm.authst, 'synthetic-key');
    if (call.method === 'CgiGetTrackInfo') return rpc({ tracks: [txSong('NewMID', { id: 202, type: 3 })] });
    if (call.method === 'AddPlaylist') return rpc({ retCode: 0, result: { tid: 601, dirName: 'Native' } });
    if (['AddSonglist', 'DelSonglist'].includes(call.method)) {
      assert.equal(call.module, 'music.musicasset.PlaylistDetailWrite');
      assert.deepEqual(call.param.v_songInfo, [{ songId: 202, songType: call.method === 'AddSonglist' ? 3 : 0 }]);
      assert.equal(call.param.bFmtUtf8, call.method === 'AddSonglist' ? true : 1);
      assert.equal(call.param.dirId, 8); assert.equal(call.param.tid, 501);
    } else assert.equal(call.module, 'music.musicasset.PlaylistBaseWrite');
    return rpc({ retCode: 0 });
  }); seedClients(f); await f.session('tx');
  assert.deepEqual(await f.request('tx', 'sync.write', { action: 'order' }), { status: 'notSent', errorCode: 'UNSUPPORTED' });
  for (const action of ['create', 'rename', 'remove', 'delete', 'add']) {
    const result = await f.request('tx', 'sync.write', { action, id: '501', name: 'Native',
      remote: { locator: { dirId: '8' } }, ids: ['NewMID'], songs: { NewMID: { qqSongId: '999', songType: 1 } } });
    assert.equal(result.status, 'confirmed');
  }
  assert.deepEqual(methods, ['AddPlaylist', 'EditPlaylist', 'CgiGetTrackInfo', 'DelSonglist', 'DelPlaylist', 'CgiGetTrackInfo', 'AddSonglist']);
  await f.close();
});

test('QQ subscribe and unsubscribe address public playlist IDs with encrypted UIN, never directory deletion or member lookups', async () => {
  const methods = [];
  const f = fixture(request => {
    const call = qq(request).req_0; methods.push(call.method);
    assert.equal(call.module, 'music.musicasset.PlaylistFavWrite');
    assert.deepEqual(call.param, { uin: 'synthetic-uin', v_playlistId: [call.method === 'FavPlaylist' ? 900 : 502] });
    return rpc({ result: 0, v_failedPlaylistId: [] });
  }); seedClients(f); await f.session('tx');
  for (const [action, id] of [['subscribe', '900'], ['unsubscribe', '502']])
    assert.deepEqual(await f.request('tx', 'sync.write', { action, id, remote: { locator: { dirId: '999' } } }),
      { status: 'confirmed', errorCode: null });
  assert.deepEqual(methods, ['FavPlaylist', 'CancelFavPlaylist']); await f.close();
});

test('KG transaction header needs actual author locators and the chosen global relationship before any subscription can be constructed', async () => {
  for (const [row, code] of [
    [{ global_collection_id: 'other-global', list_create_userid: 99, list_create_listid: 87 }, 'REMOTE_CHANGED'],
    [{ global_collection_id: 'opaque-global', list_create_listid: 87 }, 'INVALID_RESPONSE'],
    [{ global_collection_id: 'opaque-global', list_create_userid: 99 }, 'INVALID_RESPONSE'],
  ]) {
    const f = fixture(request => {
      assert.equal(new URL(request.url).pathname, '/v3/get_list_info');
      return response({ status: 1, data: [row] });
    }); seedClients(f); await f.session('kg');
    await assert.rejects(f.request('kg', 'sync.target', { id: 'opaque-global' }), { code });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('KG resolves every addition before the first write and rejects changed recording or nonzero album hints without partial mutation', async () => {
  const hashes = ['E'.repeat(32), 'F'.repeat(32)];
  for (const failure of ['', 'hash', 'album']) {
    const paths = [];
    const f = fixture(request => {
      const value = kg(request); paths.push(value.path);
      if (value.path === '/v1/audio/audio') {
        const hash = value.body.data[0].hash;
        return response({ status: 1, data: [kgSong(failure === 'hash' && hash === hashes[1] ? kgHash : hash,
          { name: 'Verified', album_id: failure === 'album' && hash === hashes[1] ? 99 : 11, album_audio_id: 22 })] });
      }
      assert.equal(value.path, '/cloudlist.service/v6/add_song'); assert.equal(request.headers['x-router'], undefined);
      assert.deepEqual(value.body.data.map(row => [row.hash, row.album_id, row.mixsongid]), hashes.map(hash => [hash, 11, 22]));
      return response({ status: 1 });
    }); seedClients(f); await f.session('kg');
    const result = await f.request('kg', 'sync.write', { action: 'add', id: '0:7', ids: hashes,
      songs: Object.fromEntries(hashes.map(hash => [hash, { hash, albumId: '11', albumAudioId: '22' }])) });
    assert.equal(result.status, failure ? 'notSent' : 'confirmed');
    assert.equal(result.errorCode, failure ? 'REMOTE_CHANGED' : null);
    assert.deepEqual(paths, ['/v1/audio/audio', '/v1/audio/audio', ...(failure ? [] : ['/cloudlist.service/v6/add_song'])]);
    await f.close();
  }
});

test('KG exact hash fallback is anonymous, accepts one nested mapping and fails ambiguity before metadata/write', async () => {
  for (const ambiguous of [false, true]) {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.pathname === '/v1/audio/audio') return response({ status: 1, data: [kgSong(kgHash, { name: '未知歌曲', album_id: 0, album_audio_id: 0 })] });
      if (url.pathname === '/v2/album_audio/audio') {
        assert.equal(url.search, ''); assert.equal(request.headers['x-router'], 'kmr.service.kugou.com');
        const body = JSON.parse(request.body);
        assert.equal(body.userid, undefined); assert.equal(body.token, undefined); assert.deepEqual(body.data, [{ hash: kgHash.toLowerCase() }]);
        const row = { album_audio_id: 22, hash: kgHash };
        return response({ status: 1, data: [ambiguous ? [row, row] : [row]] });
      }
      const value = kg(request);
      if (value.path === '/kmr/v2/audio') {
        assert.equal(request.headers['KG-TID'], '238'); assert.equal(request.headers['x-router'], 'openapi.kugou.com');
        return response({ status: 1, data: [{ __status: 1, base: { album_audio_id: 22, album_id: 11, audio_name: 'Verified' },
          audio_info: { hash: kgHash, audio_id: 21, timelength: 240000 }, authors: [{ base: { author_name: 'A' } }] }] });
      }
      assert.equal(value.path, '/cloudlist.service/v6/add_song');
      assert.equal(value.body.data[0].name, 'Verified'); return response({ status: 1 });
    }); seedClients(f); await f.session('kg');
    const result = await f.request('kg', 'sync.write', { action: 'add', id: '0:7', ids: [kgHash] });
    assert.equal(result.status, ambiguous ? 'notSent' : 'confirmed'); assert.equal(f.requests.length, ambiguous ? 2 : 4);
    if (ambiguous) assert.equal(result.errorCode, 'REMOTE_CHANGED'); await f.close();
  }
});

test('KG subscribe uses the real header creator locator, unsubscribe the account relation, and encrypted cloud ACK cannot be replaced by plain success', async t => {
  fixedEntropy(t);
  const key = md5('000000'), name = 'T'.repeat(201);
  const f = fixture(request => {
    const url = new URL(request.url);
    if (url.pathname === '/v3/get_list_info') {
      assert.equal(request.headers['x-router'], 'pubsongs.kugou.com');
      assert.deepEqual(kg(request).body.data, [{ global_collection_id: 'opaque-global' }]);
      return response({ status: 1, data: [{ global_collection_id: 'opaque-global', list_create_userid: 99,
        list_create_listid: 87, name, count: 2001 }] });
    }
    if (url.pathname === '/cloudlist.service/v5/add_list') {
      const value = kg(request);
      assert.equal(value.fields.last_time, undefined); assert.equal(value.fields.last_area, undefined);
      assert.equal(request.headers['x-router'], undefined);
      assert.deepEqual(value.body, { userid: '42', token: 'synthetic-token', total_ver: 0, name, type: 1,
        source: 1, is_pri: 0, list_create_gid: 'opaque-global', from_shupinmv: 0, list_create_userid: '99', list_create_listid: '87' });
      return response({ status: 1 });
    }
    assert.equal(url.pathname, '/v2/delete_list'); assert.equal(request.headers['x-router'], 'cloudlist.service.kugou.com');
    assert.equal(request.headers['Content-Type'], 'application/x-www-form-urlencoded');
    assert.equal(url.searchParams.get('token'), 'synthetic-token'); assert.ok(url.searchParams.get('signature'));
    assert.equal(url.searchParams.get('p').length, 256);
    const body = JSON.parse(decrypt(Buffer.from(request.body, 'base64'), key.slice(0, 16), 'cbc', key.slice(16)));
    assert.equal(body.listid, 9, 'author list 87 is never the account subscription locator');
    return response(encrypt(JSON.stringify({ status: 1 }), key.slice(0, 16), 'cbc', key.slice(16)));
  }); seedClients(f); await f.session('kg');
  const target = await f.request('kg', 'sync.target', { id: 'opaque-global' });
  assert.equal((await f.request('kg', 'sync.write', { action: 'subscribe', id: target.id, remote: target })).status, 'confirmed');
  assert.equal((await f.request('kg', 'sync.write', { action: 'unsubscribe', id: 'opaque-global',
    remote: { locator: { listId: '9' } } })).status, 'confirmed');
  assert.equal(f.requests.length, 3); await f.close();
});

test('WY subscription tokens are fresh and encrypted, ordinary reads need none, failures and late cancellation cannot dispatch', async t => {
  fixedEntropy(t);
  let calls = 0;
  const f = fixture(request => {
    const url = new URL(request.url);
    if (url.pathname === '/eapi/user/playlist') return response({ code: 200, playlist: [], more: false });
    const value = eapi(request);
    assert.equal(value.data.id, huge); assert.equal(value.data.header['X-antiCheatToken'], 'synthetic-token-' + calls);
    assert.equal(request.headers['X-antiCheatToken'], undefined);
    assert.ok(request.headers.Cookie.includes('X-antiCheatToken=synthetic-token-' + calls));
    return response({ code: 200 });
  }, { tokenProvider: { async get() { return 'synthetic-token-' + ++calls; } } });
  seedClients(f); await f.session('wy');
  await f.request('wy', 'sync.read', { subscriptionsOnly: true }); assert.equal(calls, 0);
  for (const action of ['subscribe', 'unsubscribe'])
    assert.equal((await f.request('wy', 'sync.write', { action, id: huge })).status, 'confirmed');
  assert.equal(calls, 2); await f.close();
  // Reuse the actual token owner and its existing cookie-octet contract, not a
  // second validator in music-platform or a private native token helper.
  for (const token of ['', ' ', 'a"b', 'a,b', 'a;b', 'a\\b', '\r\n', '中文']) {
    const worker = new EventEmitter();
    worker.postMessage = ({ id }) => queueMicrotask(() => worker.emit('message', { id, token }));
    worker.terminate = async () => 0;
    const provider = createNeteaseTokenProvider({ workerFactory: () => worker });
    const malformed = fixture(() => assert.fail('malformed SDK token cannot dispatch'), { tokenProvider: provider });
    seedClients(malformed); await malformed.session('wy');
    for (const action of ['subscribe', 'unsubscribe'])
      assert.deepEqual(await malformed.request('wy', 'sync.write', { action, id: huge }),
        { status: 'notSent', errorCode: 'INVALID_RESPONSE' });
    assert.equal(malformed.requests.length, 0); await malformed.close(); await provider.close();
  }
  for (const code of ['NETWORK_ERROR', 'TIMEOUT', 'CANCELLED', 'SDK_FAILURE']) {
    const failed = fixture(() => assert.fail('failed token cannot dispatch'), {
      tokenProvider: { async get() { throw Object.assign(Error('private upstream'), { code }); } },
    }); seedClients(failed); await failed.session('wy');
    assert.deepEqual(await failed.request('wy', 'sync.write', { action: 'subscribe', id: huge }),
      { status: 'notSent', errorCode: code === 'SDK_FAILURE' ? 'SIGNATURE_REQUIRED' : code });
    await failed.close();
  }
  for (const event of ['cancel', 'revoke', 'close']) {
    const held = gate(), controller = new AbortController();
    const late = fixture(() => assert.fail('late token cannot authorize dispatch'), {
      tokenProvider: { async get() { await held.wait(); return 'synthetic-late'; } },
    }); seedClients(late); await late.session('wy');
    t.after(async () => { held.release(); await late.close(); });
    const write = late.request('wy', 'sync.write', { action: 'subscribe', id: huge }, { signal: controller.signal });
    await held.entered;
    const retirement = event === 'revoke' ? late.owner.sessionUpdate({ platform: 'wy', session: null }) :
      event === 'close' ? late.close() : Promise.resolve(controller.abort());
    held.release();
    assert.deepEqual(await write, { status: 'notSent', errorCode: event === 'cancel' ? 'CANCELLED' : event === 'close' ? 'CLOSED' : 'STALE_SESSION' });
    await retirement;
  }
});
