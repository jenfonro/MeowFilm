import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { deflateSync, gunzipSync, inflateSync } from 'node:zlib';
import { fixture, response, rpc, eapi, weapi, qq, kg, seedClients, fixedEntropy, encrypt, decrypt, md5,
  gate, wySong, txSong, kgSong, kgHash, huge, idOf, now } from './support.mjs';

test('sync ordinary reads use one current membership pass, actual offsets and independent duplicate occurrence keys', async t => {
  fixedEntropy(t);
  const offsets = [];
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/user/playlist') {
      const { data } = eapi(request); offsets.push(data.offset);
      return response({ code: 200, playlist: data.offset === 0 ?
        [{ id: '10', name: 'favorites', specialType: 5, creator: { userId: 42 } }] :
        [{ id: '20', name: 'current playlist', creator: { userId: 42 } }], more: data.offset === 0 });
    }
    if (path === '/eapi/v6/playlist/detail') {
      const { data } = eapi(request), ids = data.id === '10' ? ['1', '1'] : ['2'];
      return response({ code: 200, playlist: { id: data.id, name: 'new title', trackCount: 999,
        trackIds: ids.map(id => ({ id })), tracks: [...new Set(ids)].map(id => wySong(id)) } });
    }
    assert.fail(path);
  }); await f.session('wy');
  const result = await f.request('wy', 'sync.read');
  assert.deepEqual(offsets, [0, 1]);
  assert.equal(result.collections[0].kind, 'favorites');
  assert.deepEqual(result.collections[0].members.map(row => row.id), ['1', '1']);
  assert.notEqual(result.collections[0].members[0].key, result.collections[0].members[1].key);
  assert.equal(result.collections[1].name, 'new title');
  assert.equal(f.requests.length, 4, 'no head or second-pass snapshot proof');
  await f.close();
});

test('KG private sync members keep file IDs, sort and full cloud locator for the real transaction', async () => {
  const f = fixture(request => {
    const value = kg(request);
    if (value.path === '/v7/get_all_list') return response({ status: 1, data: { total_ver: huge, has_more: '0',
      info: [{ type: 0, listid: 7, name: 'owned', list_create_userid: 42, list_create_listid: 7,
        source: 1, sort: 2, intro: 'current', tags: [], pic: '' }] } });
    assert.equal(value.path, '/v4/get_list_all_file_v3');
    return response({ status: 1, data: { has_more: '0', list_info: { name: 'current', list_ver: '9' },
      info: [kgSong(kgHash, { fileid: '22', sort: 2 }), kgSong(kgHash, { fileid: '11', sort: 1 })] } });
  }); seedClients(f); await f.session('kg');
  const result = await f.request('kg', 'sync.read');
  const list = result.collections[0];
  assert.deepEqual(list.members.map(row => row.key), ['11', '22']);
  assert.deepEqual(list.members.map(row => row.id), [kgHash, kgHash]);
  assert.equal(list.locator.totalVersion, huge); assert.equal(list.locator.version, '9');
  assert.equal(list.locator.intro, 'current'); assert.equal(f.requests.length, 2); await f.close();
});

test('WY write receipts distinguish not-sent, explicit nested rejection and an unknown dispatched response', async t => {
  fixedEntropy(t);
  const missing = fixture(() => assert.fail('unconstructible write must not dispatch'));
  await missing.session('wy');
  assert.deepEqual(await missing.request('wy', 'sync.write', { action: 'create' }),
    { status: 'notSent', errorCode: 'INVALID_REQUEST' }); await missing.close();
  for (const [raw, status, code] of [
    [{ code: 200, '/api/playlist/update/name': { code: 403 } }, 'rejected', 'FORBIDDEN'],
    [{ code: 200 }, 'unknown', 'INVALID_RESPONSE'],
    [{ code: 200, '/api/playlist/update/name': { code: 200 } }, 'confirmed', null],
  ]) {
    const f = fixture(request => {
      const { path, data } = eapi(request);
      assert.equal(path, '/api/batch');
      assert.deepEqual(JSON.parse(data['/api/playlist/update/name']), { id: '1', name: 'current' });
      return response(raw);
    }); await f.session('wy');
    assert.deepEqual(await f.request('wy', 'sync.write', { action: 'rename', id: '1', name: 'current' }),
      { status, errorCode: code });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('sync consumed arrays and member keys fail as requests before directory reads, lookups or writes', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async st => {
    const f = fixture(() => assert.fail('unusable mutation material must not dispatch dependencies'));
    await f.owner.ready;
    const storageCounts = [f.reads.length, f.writes.length];
    st.after(() => f.close()); await f.session(platform);
    const params = { id: platform === 'kg' ? '0:7' : '501',
      remote: { kind: platform === 'wy' ? 'favorites' : 'playlist', locator: { dirId: '7', listId: '7', version: '9' } } };
    const actions = platform === 'wy' ? ['add', 'remove', 'order'] : platform === 'tx' ? ['add', 'remove'] : ['add'];
    const validId = platform === 'kg' ? kgHash : '123';
    for (const kinds of [null, {}, 42, { includes: null }])
      await assert.rejects(f.request(platform, 'sync.read', { kinds }), { code: 'INVALID_REQUEST' });
    for (const action of actions) for (const ids of [null, {}, '123', [null], [validId, null], [validId, {}]])
      assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action, ids }),
        { status: 'notSent', errorCode: 'INVALID_REQUEST' });
    if (platform === 'tx') {
      assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action: 'order', ids: {} }),
        { status: 'notSent', errorCode: 'UNSUPPORTED' });
      for (const action of ['add', 'remove']) for (const value of ['not-an-integer', {}, null]) {
        assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action, ids: ['123'],
          remote: { locator: { dirId: value } } }), { status: 'notSent', errorCode: 'INVALID_REQUEST' });
        assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action, id: value, ids: ['123'],
          remote: { locator: { dirId: '7' } } }), { status: 'notSent', errorCode: 'INVALID_REQUEST' });
      }
    }
    if (platform === 'kg') {
      for (const action of ['remove', 'order'])
        for (const members of [null, {}, '11', [null], [{}], [{ key: {} }], [{ key: '11' }, null]])
          assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action, members }),
            { status: 'notSent', errorCode: 'INVALID_REQUEST' });
      for (const sources of [{}, 'not-an-array', [null]])
        assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action: 'add', ids: [kgHash],
          songs: { [kgHash]: { sources } } }), { status: 'notSent', errorCode: 'INVALID_REQUEST' });
      const secondHash = 'B' + kgHash.slice(1);
      assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action: 'add', ids: [kgHash, secondHash],
        songs: { [secondHash]: { sources: [null] } } }), { status: 'notSent', errorCode: 'INVALID_REQUEST' });
      for (const listId of ['', {}])
        assert.deepEqual(await f.request(platform, 'sync.write', { ...params, action: 'add', ids: [kgHash],
          remote: { locator: { listId } } }), { status: 'notSent', errorCode: 'INVALID_REQUEST' });
    }
    assert.equal(f.requests.length, 0); assert.equal(f.reads.length, storageCounts[0]); assert.equal(f.writes.length, storageCounts[1]);
  });
});

test('sync create ignores unrelated remote and ID material without changing the real platform recipe', async t => {
  fixedEntropy(t);
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async st => {
    const recipes = [];
    const f = fixture(request => {
      const url = new URL(request.url);
      assert.equal(request.method, 'POST');
      if (platform === 'wy') {
        assert.equal(url.pathname, '/weapi/playlist/create');
        const data = weapi(request); recipes.push(data);
        assert.equal(data.name, 'Native'); assert.equal(data.privacy, 10); assert.equal(data.type, 'NORMAL');
        return response({ code: 200, id: '71' });
      }
      if (platform === 'tx') {
        const call = qq(request).req_0; recipes.push(call);
        assert.deepEqual(call, { module: 'music.musicasset.PlaylistBaseWrite', method: 'AddPlaylist', param: { dirName: 'Native' } });
        return rpc({ retCode: 0, result: { tid: '71', dirName: 'Native' } });
      }
      const value = kg(request); recipes.push(value.body);
      assert.equal(value.path, '/cloudlist.service/v5/add_list');
      assert.equal(value.fields.last_time, String(now / 1000)); assert.equal(value.fields.last_area, 'gztx');
      assert.equal(request.headers['x-router'], undefined);
      assert.deepEqual(value.body, { userid: '42', token: 'synthetic-token', total_ver: 0, name: 'Native',
        type: 0, source: 1, is_pri: 1, list_create_gid: '', from_shupinmv: 0 });
      return response({ status: 1, data: { listid: '71' } });
    });
    seedClients(f); st.after(() => f.close()); await f.session(platform);
    const params = { action: 'create', name: 'Native' };
    const original = await f.request(platform, 'sync.write', params);
    assert.deepEqual(original, { status: 'confirmed', createdId: platform === 'kg' ? '0:71' : '71',
      createdName: 'Native', errorCode: null });
    for (const extra of [
      { remote: null },
      { id: 123 },
      { id: { unused: true }, remote: { id: null, locator: { dirId: null, listId: { unused: true } } }, ids: {}, members: null },
    ]) assert.deepEqual(await f.request(platform, 'sync.write', { ...params, ...extra }), original);
    for (const recipe of recipes.slice(1)) assert.deepEqual(recipe, recipes[0]);
    assert.equal(f.requests.length, 4);
  });
});

test('sync create, rename and delete still fail on truly unusable names and locators before dispatch', async t => {
  for (const platform of ['wy', 'tx', 'kg']) await t.test(platform, async st => {
    const f = fixture(() => assert.fail('unusable required mutation material must not dispatch'));
    await f.owner.ready;
    const storageCounts = [f.reads.length, f.writes.length];
    st.after(() => f.close()); await f.session(platform);
    const target = platform === 'wy' ? { id: '501' } :
      { remote: { locator: platform === 'tx' ? { dirId: '7' } : { listId: '7', totalVersion: '9' } } };
    for (const params of [
      { action: 'create', name: null, remote: null, id: 123 },
      { action: 'delete', id: null, remote: null },
      { action: 'rename', name: 'Native', id: null, remote: null },
      { ...target, action: 'rename', name: null },
      { action: 'delete', id: {}, remote: { locator: { dirId: {}, listId: {} } } },
    ]) assert.deepEqual(await f.request(platform, 'sync.write', params), { status: 'notSent', errorCode: 'INVALID_REQUEST' });
    assert.equal(f.requests.length, 0); assert.equal(f.reads.length, storageCounts[0]); assert.equal(f.writes.length, storageCounts[1]);
  });
});

test('sync request arrays retain convertible numeric IDs and unrelated array fields do not gate a write', async t => {
  fixedEntropy(t);
  const wy = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/user/playlist') {
      assert.equal(eapi(request).data.uid, '42');
      return response({ code: 200, playlist: [{ id: '91', name: 'Subscribed',
        creator: { userId: 'other' }, subscribed: true }], more: false });
    }
    if (path === '/weapi/playlist/create') {
      assert.equal(weapi(request).name, 'Native');
      return response({ code: 200, id: '71' });
    }
    const data = eapi(request).data;
    assert.equal(path, '/eapi/playlist/manipulate/tracks'); assert.equal(data.op, 'update');
    assert.deepEqual(JSON.parse(data.trackIds), ['123', huge]);
    return response({ code: 200 });
  });
  t.after(() => wy.close()); await wy.session('wy');
  const created = await wy.request('wy', 'sync.write', { action: 'create', name: 'Native', ids: {}, members: null });
  assert.equal(created.status, 'confirmed'); assert.equal(created.createdId, '71');
  assert.deepEqual(await wy.request('wy', 'sync.write', { action: 'order', id: '501', ids: [123, huge] }),
    { status: 'confirmed', errorCode: null });
  assert.equal(wy.requests.length, 2);
  const subscriptions = await wy.request('wy', 'sync.read', { subscriptionsOnly: true, kinds: null });
  assert.deepEqual(subscriptions.collections, []); assert.equal(subscriptions.accountId, '42');
  assert.deepEqual(subscriptions.subscriptions.map(row => row.id), ['91']); assert.equal(wy.requests.length, 3);

  const tx = fixture(request => {
    const { method, param } = qq(request).req_0;
    if (method === 'CgiGetTrackInfo') {
      assert.deepEqual(param.mids, ['77']);
      return rpc({ tracks: [txSong('77', { id: '123' })] });
    }
    assert.equal(method, 'AddSonglist'); assert.equal(param.dirId, 7); assert.equal(param.tid, 501);
    assert.deepEqual(param.v_songInfo, [{ songId: 123, songType: 0 }]);
    return rpc({ retCode: 0 });
  });
  seedClients(tx); t.after(() => tx.close()); await tx.session('tx');
  assert.deepEqual(await tx.request('tx', 'sync.write', { action: 'add', id: '501', ids: [77],
    remote: { locator: { dirId: '7' } } }), { status: 'confirmed', errorCode: null });
  assert.equal(tx.requests.length, 2);
});

test('a dispatched write settles its true receipt after caller cancellation and session revocation', async t => {
  const held = gate(), controller = new AbortController();
  const f = fixture(async () => { await held.wait(); return response({ code: 200, playlist: { id: '55', name: 'created' } }); });
  t.after(async () => { held.release(); await f.close(); });
  await f.session('wy');
  const write = f.request('wy', 'sync.write', { action: 'create', name: 'created' }, { signal: controller.signal });
  await held.entered; controller.abort();
  await f.owner.sessionUpdate({ platform: 'wy', session: null });
  assert.equal(f.requests[0].signal.aborted, false);
  held.release();
  assert.deepEqual(await write, { status: 'confirmed', createdId: '55', createdName: 'created', errorCode: null });
});

test('write connection-establishment proof remains notSent, while lost established connections remain unknown without replay', async () => {
  for (const [cause, status] of [['ENOTFOUND', 'notSent'], ['ECONNREFUSED', 'notSent'], ['ECONNRESET', 'unknown']]) {
    const f = fixture(() => { throw Object.assign(Error('synthetic transport failure'), { cause: { code: cause } }); });
    await f.session('wy');
    assert.deepEqual(await f.request('wy', 'sync.write', { action: 'create', name: 'current' }),
      { status, errorCode: 'NETWORK_ERROR' });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('only a decoded business refusal rejects a dispatched write; malformed or missing status remains unknown', async () => {
  for (const [raw, status, errorCode] of [
    [{ code: false }, 'unknown', 'INVALID_RESPONSE'], [{ code: '200' }, 'unknown', 'INVALID_RESPONSE'],
    [{ code: 200, data: { code: 403 } }, 'rejected', 'FORBIDDEN'],
    [{ code: 200, data: { code: false } }, 'unknown', 'INVALID_RESPONSE'],
  ]) {
    const f = fixture(() => response(raw)); await f.session('wy');
    assert.deepEqual(await f.request('wy', 'sync.write', { action: 'delete', id: '1' }), { status, errorCode });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('WY subscription preparation is one-use, session-bound and not consumed by ordinary reads', async t => {
  fixedEntropy(t);
  let tokens = 0;
  const f = fixture(request => {
    const { path, data } = eapi(request);
    assert.equal(path, '/api/playlist/subscribe'); assert.equal(data.header['X-antiCheatToken'], 'synthetic-anti-token');
    return response({ code: 200 });
  }, { tokenProvider: { async get() { tokens++; return 'synthetic-anti-token'; } } });
  await f.session('wy');
  const prepared = await f.request('wy', 'sync.prepare', { action: 'subscribe', id: '1' });
  assert.equal(f.requests.length, 0); assert.equal(tokens, 1);
  const params = { action: 'subscribe', id: '1', preparationId: prepared.preparationId };
  assert.equal((await f.request('wy', 'sync.write', params)).status, 'confirmed');
  assert.deepEqual(await f.request('wy', 'sync.write', params), { status: 'notSent', errorCode: 'STALE_SESSION' });
  const retired = await f.request('wy', 'sync.prepare', { action: 'subscribe', id: '1' });
  await f.session('wy', '43');
  assert.deepEqual(await f.request('wy', 'sync.write', { ...params, preparationId: retired.preparationId }),
    { status: 'notSent', errorCode: 'STALE_SESSION' });
  assert.equal(f.requests.length, 1); await f.close();
});

test('QQ subscription consumes exact IDs and only an actual acknowledgement can confirm a write', async () => {
  for (const [data, status, errorCode] of [
    [{ result: 0, v_failedPlaylistId: [] }, 'confirmed', null],
    [{ result: 0, v_failedPlaylistId: [huge] }, 'rejected', 'UPSTREAM_ERROR'],
    [{ result: 1 }, 'rejected', 'UPSTREAM_ERROR'],
    [{ result: false }, 'unknown', 'INVALID_RESPONSE'],
    [{ result: 'not-integer' }, 'unknown', 'INVALID_RESPONSE'],
    [{ result: 0.5 }, 'unknown', 'INVALID_RESPONSE'],
    [{ result: 0, retCode: false }, 'unknown', 'INVALID_RESPONSE'],
    [{ result: 0, retCode: 'not-integer' }, 'unknown', 'INVALID_RESPONSE'],
    [{ result: 0, retCode: 7 }, 'rejected', 'UPSTREAM_ERROR'],
    [{}, 'unknown', 'INVALID_RESPONSE'],
  ]) {
    const f = fixture(request => {
      const call = qq(request).req_0;
      assert.equal(call.method, 'FavPlaylist');
      assert.ok(request.body.includes('"v_playlistId":[' + huge + ']'));
      return rpc(data);
    }); seedClients(f); await f.session('tx');
    assert.deepEqual(await f.request('tx', 'sync.write', { action: 'subscribe', id: huge }), { status, errorCode });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('KG cloud requests and encrypted ACKs use real independent AES vectors; plaintext success remains unknown', async t => {
  fixedEntropy(t);
  const digest = md5('000000'), key = digest.slice(0, 16), iv = digest.slice(16);
  for (const [body, status, errorCode] of [
    [encrypt(JSON.stringify({ status: 1 }), key, 'cbc', iv), 'confirmed', null],
    [response({ status: 0, error_code: 1 }), 'rejected', 'UPSTREAM_ERROR'],
    [response({ status: 1 }), 'unknown', 'CLOUD_RESPONSE_UNCONFIRMED'],
  ]) {
    const f = fixture(request => {
      assert.equal(new URL(request.url).pathname, '/v1/modify_list');
      const plain = JSON.parse(decrypt(request.body, key, 'cbc', iv));
      assert.deepEqual(plain, { total_ver: 7, listid: 8, type: 0, name: 'new', intro: 'keep' });
      return body instanceof Response ? body.clone() : response(body);
    }); seedClients(f); await f.session('kg');
    const actual = await f.request('kg', 'sync.write', { action: 'rename', id: '0:8', name: 'new',
      remote: { locator: { listId: '8', totalVersion: '7', intro: 'keep' } } });
    assert.deepEqual(actual, { status, errorCode }); assert.equal(f.requests.length, 1); await f.close();
  }
});

test('KG remove/order addresses actual occurrence keys, not recording hashes', async t => {
  fixedEntropy(t);
  const digest = md5('000000'), key = digest.slice(0, 16), iv = digest.slice(16);
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    const body = path === '/v4/delete_songs' ? kg(request).body : JSON.parse(decrypt(request.body, key, 'cbc', iv));
    assert.deepEqual(body.data, path === '/v4/delete_songs' ? [{ fileid: 11 }, { fileid: 22 }] :
      [{ fileid: 11, sort: 0 }, { fileid: 22, sort: 1 }]);
    return path === '/v4/delete_songs' ? response({ status: 1 }) :
      response(encrypt(JSON.stringify({ status: 1 }), key, 'cbc', iv));
  }); seedClients(f); await f.session('kg');
  const params = { id: '0:8', ids: [kgHash, kgHash], members: [{ id: kgHash, key: '11' }, { id: kgHash, key: '22' }],
    remote: { locator: { listId: '8', version: '9' } } };
  assert.equal((await f.request('kg', 'sync.write', { ...params, action: 'remove' })).status, 'confirmed');
  assert.equal((await f.request('kg', 'sync.write', { ...params, action: 'order' })).status, 'confirmed');
  const { members, ...withoutOccurrences } = params;
  assert.deepEqual(await f.request('kg', 'sync.write', { ...withoutOccurrences, action: 'remove' }),
    { status: 'notSent', errorCode: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, 2); await f.close();
});

test('all KG encrypted cloud mutations retain their real envelopes and settle explicit verification refusal without retry', async t => {
  fixedEntropy(t);
  const digest = md5('000000');
  for (const [action, path] of [['delete', '/v2/delete_list'], ['rename', '/v1/modify_list'], ['order', '/v1/modify_song_sort']]) {
    const f = fixture(request => {
      const url = new URL(request.url);
      assert.equal(url.pathname, path); assert.equal(request.headers['x-router'], 'cloudlist.service.kugou.com');
      const query = url.searchParams;
      assert.match(query.get('p'), /^[\dA-F]{256}$/);
      assert.equal(query.get('key'), md5('1005OIlwieks28dk2k092lksi2UIkp204891800000000'));
      const cipher = action === 'delete' ? Buffer.from(request.body, 'base64') : request.body;
      assert.equal(cipher.length % 16, 0);
      const plain = JSON.parse(decrypt(cipher, digest.slice(0, 16), 'cbc', digest.slice(16)));
      assert.equal(plain.listid, 7);
      if (action === 'delete') {
        assert.equal(request.headers['Content-Type'], 'application/x-www-form-urlencoded');
        assert.equal(query.get('token'), 'synthetic-token'); assert.ok(query.get('signature'));
      } else for (const field of ['token', 'userid', 'uuid', 'signature']) assert.equal(query.get(field), null);
      return response({ status: 0, errcode: 20028 });
    }); seedClients(f); await f.session('kg');
    assert.deepEqual(await f.request('kg', 'sync.write', { action, id: '0:7', name: 'New',
      members: [{ id: kgHash, key: '11' }], remote: { locator: { listId: '7', totalVersion: '9', version: '8' } } }),
    { status: 'rejected', errorCode: 'VERIFICATION_REQUIRED' });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('QQ feedback prepares a real transient session, releases it, and confirms only its compressed ACK', async () => {
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/imusic_tj')) {
      const xml = gunzipSync(request.body).toString();
      assert.match(xml, /songid="9007199254740993123"/); assert.match(xml, /time="12"/);
      assert.match(xml, /play_duration_mi="12000"/);
      return response(Buffer.concat([Buffer.alloc(5), deflateSync(Buffer.from('{"code":0}'))]));
    }
    const call = qq(request).req_0;
    if (call.method === 'GetLoginUserInfo') return rpc({ code: 0, musicid: 42 });
    if (call.method === 'GetHomepageHeader') return rpc({ Info: { BaseInfo: { EncryptedUin: 'synthetic-uin' } } });
    return call.method === 'CgiGetTrackInfo' ? rpc({ tracks: [txSong('mid', { singer: [{ id: '7', name: 'A' }] })] }) :
      rpc({ midurlinfo: [{ result: 0, vkey: 'synthetic-vkey' }] });
  }); seedClients(f); await f.session('tx');
  const prepared = await f.request('tx', 'feedback.prepare', { target: { nativeId: 'mid' } });
  assert.equal(prepared.status, 'registered'); assert.ok(prepared.sessionId);
  f.advance(12000);
  assert.equal((await f.request('tx', 'feedback.send', { sessionId: prepared.sessionId, event: 'end', listened: 12 })).status, 'confirmed');
  assert.deepEqual(await f.request('tx', 'feedback.send', { sessionId: prepared.sessionId, event: 'end', listened: 12 }),
    { status: 'notSent', errorCode: 'STALE_SESSION' });
  const second = await f.request('tx', 'feedback.prepare', { target: { nativeId: 'mid' } });
  assert.deepEqual(await f.request('tx', 'feedback.release', { sessionId: second.sessionId }), { released: true });
  assert.deepEqual(await f.request('tx', 'feedback.send', { sessionId: second.sessionId, event: 'end', listened: 12 }),
    { status: 'notSent', errorCode: 'STALE_SESSION' });
  assert.equal(f.requests.length, 11);
  const methods = f.requests.filter(row => !row.url.includes('imusic_tj')).map(row => qq(row).req_0.method);
  assert.equal(methods.filter(method => method === 'GetLoginUserInfo').length, 3);
  assert.equal(methods.filter(method => method === 'GetHomepageHeader').length, 3);
  assert.equal(methods.filter(method => method === 'CgiGetTrackInfo').length, 2);
  assert.equal(methods.filter(method => method === 'UrlGetVkey').length, 2);
  await f.close();
});

test('KG feedback sends the start record, keeps the encrypted preparation and retires it on release', async t => {
  const original = crypto.publicEncrypt; let portrait;
  t.mock.method(crypto, 'publicEncrypt', (options, bytes) => { portrait = Buffer.from(bytes).toString(); return original(options, bytes); });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  let uuid, field, events = [];
  const f = fixture(request => {
    const url = new URL(request.url);
    if (url.pathname === '/kmr/v2/audio') {
      const value = kg(request);
      assert.deepEqual(value.body.data, [{ entity_id: 7 }]);
      return response({ status: 1, data: [{ __status: 1, base: { album_audio_id: 7 },
        audio_info: { hash: kgHash, timelength: 240000 } }] });
    }
    if (url.pathname === '/v3/qrydid') {
      uuid = JSON.parse(request.body).uuid; return response({ status: 1, data: { deviceid: 'synthetic-did' } });
    }
    if (url.pathname === '/v2/gen') {
      const fields = portrait.split('\t'); assert.equal(fields[0], uuid); field = fields[1];
      return response({ status: 1, data: encrypt(JSON.stringify({ cookie: 'synthetic-cscc', clienttime: '123',
        serverstr: 'synthetic-server' }), field, 'cbc', uuid.slice(0, 16)).toString('base64') });
    }
    assert.equal(url.pathname, '/v2/post');
    const key = md5(uuid + '123' + field + 'synthetic-server');
    const plain = inflateSync(decrypt(request.body, key, 'cbc', uuid.slice(0, 16)));
    const end = plain.indexOf('\r\n', plain.indexOf('\r\n') + 2) + 2;
    events.push(inflateSync(plain.subarray(end)).toString());
    return response({ status: 1 });
  }); seedClients(f); await f.session('kg');
  const start = await f.request('kg', 'feedback.send', { event: 'start', target: { nativeId: kgHash, mixsongId: '7' }, duration: 240 });
  assert.equal(start.status, 'confirmed'); assert.ok(start.sessionId);
  assert.match(events[0], /type_id=20431/); assert.match(events[0], /mixsongid=7/);
  f.advance(30000);
  const end = await f.request('kg', 'feedback.send', { event: 'end', sessionId: start.sessionId,
    duration: 240, listened: 30, mediaListened: 30, position: 240, rate: 1, reason: 'playend' });
  assert.equal(end.status, 'confirmed'); assert.match(events[1], /type_id=4/); assert.match(events[1], /duration=30000/);
  assert.equal(f.requests.length, 5); await f.close();
});

test('WY public feedback upload preserves the NCBL container and exact semantic events, without a second native codec', async t => {
  const original = crypto.randomBytes, keys = [];
  t.mock.method(crypto, 'randomBytes', size => { const bytes = original(size); if (size === 32) keys.push(Buffer.from(bytes)); return bytes; });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  let records;
  const f = fixture(request => {
    const body = request.body, start = body.indexOf('NCBL'), file = body.subarray(start);
    assert.ok(start > 0); assert.equal(file.readUInt32LE(4), 3);
    const header = file.readUInt16LE(8), uuid = file.subarray(10, 26), wrapped = file.subarray(26, 58);
    const iv = Buffer.alloc(16); iv.writeUInt32LE(uuid.readUInt32LE(12) >>> 2); uuid.copy(iv, 4, 0, 12);
    const open = (value, key) => { const cipher = crypto.createDecipheriv('chacha20', key, iv);
      return Buffer.concat([cipher.update(value), cipher.final()]); };
    assert.deepEqual(JSON.parse(open(file.subarray(74, header), wrapped)), { MUSIC_U: 'synthetic-music-u' });
    const size = file.readUInt16LE(header);
    const compressed = open(file.subarray(header + 6, header + 6 + size), keys.at(-1));
    assert.equal(compressed.readUInt32LE(), 0xfd2fb528);
    assert.equal(compressed.readUInt32LE(5), compressed.length - 12);
    records = compressed.subarray(12).toString();
    const filename = /filename="([^"]+)"/.exec(body.subarray(0, start).toString())[1];
    return response({ code: 200, data: { successfiles: [filename] } });
  }); seedClients(f); await f.session('wy');
  const events = [{ event: 'start', occurredAt: now / 1000, target: { nativeId: huge } },
    { event: 'end', occurredAt: now / 1000 + 12, target: { nativeId: huge, source: 'fm' },
      listened: 12, mediaListened: 10, reason: 'playend', rate: 1 }];
  assert.equal((await f.request('wy', 'feedback.upload', { events })).status, 'confirmed');
  const lines = records.trimEnd().split('\n').map(row => row.split('\x01'));
  assert.deepEqual(lines.map(row => row[1]), ['_plv', '_pld']);
  assert.ok(lines[0][2].includes('"id":' + huge)); assert.equal(JSON.parse(lines[1][2]).realtime, 12);
  assert.equal(JSON.parse(lines[1][2]).time, 10); assert.equal(f.requests.length, 1);
  await f.close();
});

test('sync snapshots require complete collections and real member identities while ordinary reads stay permissive', async t => {
  fixedEntropy(t);
  const wy = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/user/playlist')
      return response({ code: 200, more: false, playlist: [{ id: '10', name: 'Owned', creator: { userId: 42 } }] });
    if (path === '/eapi/v6/playlist/detail')
      return response({ code: 200, playlist: { id: '10', trackIds: [{ id: '101' }], tracks: [{ name: 'Display only' }] } });
    assert.equal(path, '/weapi/v3/song/detail');
    return response({ code: 200, songs: [{ name: 'Still missing ID' }] });
  });
  t.after(() => wy.close()); await wy.session('wy');
  const wySnapshot = await wy.request('wy', 'sync.read', { kinds: ['playlist'] });
  assert.equal(wySnapshot.collections[0].members.length, 1);
  assert.equal(wySnapshot.collections[0].members[0].id, '101');
  assert.equal(wySnapshot.collections[0].members[0].hasMetadata, false);
  assert.equal(idOf(wySnapshot.collections[0].members[0].song), '101');

  const stalledTx = fixture(request => {
    const { method } = qq(request).req_0;
    if (method === 'GetPlaylistByUin') return rpc({ v_playlist: [{ tid: '501', dirid: 8, uin: 42, name: 'Owned' }] });
    if (method === 'CgiGetPlaylistFavInfo') return rpc({ hasmore: 0, total: 0, v_list: [] });
    return rpc({ dirinfo: { id: '501' }, songlist: [], hasmore: 1, total_song_num: 5 });
  });
  seedClients(stalledTx); t.after(() => stalledTx.close()); await stalledTx.session('tx');
  const partial = await stalledTx.request('tx', 'playlist.complete', { id: '501' });
  assert.equal(partial.complete, false); assert.equal(partial.more, true);
  await assert.rejects(stalledTx.request('tx', 'sync.read', { kinds: ['playlist'] }), { code: 'INVALID_RESPONSE' });

  for (const [platform, missingDirectoryId] of [['tx', true], ['tx', false], ['kg', false]]) {
    const f = fixture(request => {
      if (platform === 'tx') {
        const { method } = qq(request).req_0;
        if (method === 'GetPlaylistByUin')
          return rpc({ v_playlist: [missingDirectoryId ? { dirid: 8, uin: 42, name: 'No ID' } : { tid: '501', dirid: 8, uin: 42, name: 'Owned' }] });
        if (method === 'CgiGetPlaylistFavInfo') return rpc({ hasmore: 0, total: 0, v_list: [] });
        return rpc({ dirinfo: { id: '501' }, songlist: [{ title: 'No MID' }], hasmore: 0, total_song_num: 1 });
      }
      const value = kg(request);
      if (value.path === '/v7/get_all_list')
        return response({ status: 1, data: { total_ver: '1', has_more: 0, info: [{ type: 0, listid: 7, list_create_userid: 42, name: 'Owned' }] } });
      return response({ status: 1, data: { has_more: 0, info: [{ fileid: '11', audio_name: 'No hash' }] } });
    });
    seedClients(f); t.after(() => f.close()); await f.session(platform);
    await assert.rejects(f.request(platform, 'sync.read', { kinds: ['playlist'] }), { code: 'INVALID_RESPONSE' });
  }

  const kgMissing = fixture(request => {
    assert.equal(kg(request).path, '/v1/audio/audio');
    return response({ status: 1, data: [{ audio_name: 'Display only' }] });
  });
  seedClients(kgMissing); t.after(() => kgMissing.close()); await kgMissing.session('kg');
  assert.deepEqual(await kgMissing.request('kg', 'sync.write', { action: 'add', id: '0:7', ids: [kgHash] }),
    { status: 'notSent', errorCode: 'NOT_FOUND' });
});
