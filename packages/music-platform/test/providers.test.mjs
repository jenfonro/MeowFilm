import test from 'node:test';
import assert from 'node:assert/strict';
import { parseJSONText } from '@meowfilm/runtime-bridge/json';
import { decodeJSON } from '../src/model.mjs';
import { fixture, response, rpc, eapi, qq, kg, seedClients, huge, kgHash, wySong, txSong, kgSong, kwSong, mgSong, idOf, gate } from './support.mjs';

test('all five public search recipes consume only fields needed by the result', async t => {
  for (const platform of ['wy', 'tx', 'kg', 'kw', 'mg']) {
    await t.test(platform, async () => {
      const recipes = [];
      const f = fixture(request => {
        const url = new URL(request.url);
        if (platform === 'wy') {
          const row = eapi(request); recipes.push({ path: row.path, data: { ...row.data, header: undefined } });
          assert.equal(row.path, '/api/cloudsearch/pc'); assert.equal(row.data.s, 'A & B');
          return response({ code: 200, result: { songs: [wySong(huge, { al: {}, unused: { changed: null } })], songCount: 1 },
            unused: ['any', 'shape'] });
        }
        if (platform === 'tx') {
          const row = qq(request); recipes.push(row.req_0);
          assert.equal(row.req_0.method, 'DoSearchForQQMusicMobile'); assert.equal(row.req_0.param.query, 'A & B');
          return rpc({ body: { item_song: [txSong('canonical-mid', { album: {}, unused: 123 })] }, meta: { sum: 1, nextpage: -1 } });
        }
        if (platform === 'kg') {
          const row = kg(request); recipes.push({ path: row.path, keyword: row.fields.keyword, page: row.fields.page });
          assert.equal(row.path, '/v2/search/song'); assert.equal(row.fields.keyword, 'A & B');
          return response({ status: 1, data: { lists: [kgSong(undefined, { optional: true })], total: 1, has_next: '0' } });
        }
        recipes.push({ host: url.host, keyword: url.searchParams.get(platform === 'kw' ? 'all' : 'text') });
        assert.equal(url.searchParams.get(platform === 'kw' ? 'all' : 'text'), 'A & B');
        return platform === 'kw' ? response({ TOTAL: 1, abslist: [kwSong()] }) :
          response({ code: '000000', data: { songResultData: { totalCount: 1, resultList: [[mgSong()]] } } });
      }); seedClients(f);
      const first = await f.request(platform, 'search', { keyword: 'A & B' });
      const second = await f.request(platform, 'search', { keyword: 'A & B', unused: { headers: { Cookie: 'not-forwarded' } },
        url: 'https://attacker.invalid/', method: 'DELETE' });
      assert.deepEqual(first, second);
      assert.equal(first.items.length, 1);
      assert.ok(idOf(first.items[0]));
      assert.equal(first.items[0].song?.songId ?? first.items[0].songId, undefined, 'provider does not allocate permanent identities');
      assert.equal(f.requests.length, 2, 'search does not gain an API cache');
      assert.ok(f.requests.every(row => !JSON.stringify(row.headers).includes('not-forwarded')));
      if (platform !== 'tx') assert.deepEqual(recipes[0], recipes[1]);
      assert.equal(first.more, false);
      await f.close();
    });
  }
});

test('unsafe integer tokens and duplicate JSON fields retain final exact business values', async () => {
  const f = fixture(() => response(`{"code":500,"code":200,"result":{"songCount":1,"songs":[{"id":${huge},"name":"x","ar":[],"dt":240000}]},"unused":1e400}`));
  const result = await f.request('wy', 'search', { keyword: 'x' });
  assert.equal(idOf(result.items[0]), huge);
  assert.equal(result.items[0].cover_url, '');
  await f.close();
});

test('music consumes exact numeric request locators from the common JSON decoder', async t => {
  const f = fixture(request => {
    assert.equal(new URL(request.url).pathname, '/eapi/v6/playlist/detail');
    assert.ok(new URLSearchParams(request.body).get('params'));
    assert.equal(eapi(request).data.id, huge);
    return response({ code: 200, playlist: { id: huge, trackIds: [{ id: '1' }], tracks: [wySong('1')] } });
  });
  t.after(() => f.close());
  const result = await f.owner.request(parseJSONText(`{"platform":"wy","operation":"playlist","params":{"id":${huge},"unused":1e400}}`));
  assert.equal(result.id, huge); assert.deepEqual(result.items.map(idOf), ['1']);
  assert.equal(f.requests.length, 1);
});

test('TX and KG playlist caches distinguish effective offsets and reuse explicit, continuation and default synonyms', async t => {
  for (const platform of ['tx', 'kg']) await t.test(platform, async st => {
    const firstId = platform === 'tx' ? 'first' : kgHash, secondId = platform === 'tx' ? 'second' : 'B' + kgHash.slice(1);
    const offsets = [];
    const f = fixture(request => {
      const value = platform === 'tx' ? qq(request).req_0 : kg(request);
      if (platform === 'tx') {
        assert.equal(value.method, 'CgiGetDiss'); assert.equal(value.param.disstid, 123);
        assert.equal(value.param.song_num, 30);
      } else {
        assert.equal(value.path, '/pubsongs/v2/get_other_list_file_nofilt');
        assert.equal(value.fields.global_collection_id, '123'); assert.equal(value.fields.pagesize, '30');
      }
      const offset = platform === 'tx' ? value.param.song_begin : Number(value.fields.begin_idx);
      assert.ok(offset === 0 || offset === 30); offsets.push(offset);
      const id = offset === 0 ? firstId : secondId;
      return platform === 'tx' ? rpc({ dirinfo: { id: '123', title: 'Current' },
        songlist: [txSong(id), txSong(id)], total_song_num: 2, hasmore: 0 }) :
        response({ status: 1, data: { list_info: { global_collection_id: '123', name: 'Current', count: 2 },
          songs: [kgSong(id), kgSong(id)], has_more: 0 } });
    });
    seedClients(f); st.after(() => f.close());
    const first = await f.request(platform, 'playlist', { id: '123', page: 1, limit: 30, offset: 0 });
    const second = await f.request(platform, 'playlist', { id: '123', page: 1, limit: 30, offset: 30 });
    assert.deepEqual(first.items.map(idOf), [firstId, firstId]);
    assert.deepEqual(second.items.map(idOf), [secondId, secondId]); assert.equal(f.requests.length, 2);
    for (const params of [
      { page: '1', limit: '30', continuation: { offset: '30' } },
      { page: 1, limit: 30, offset: '30', continuation: { offset: { toString: null } }, unused: { offset: 'bad' } },
      { page: 1, limit: 30, offset: null, continuation: { offset: 30 } },
    ]) assert.deepEqual(await f.request(platform, 'playlist', { id: '123', ...params }), second);
    assert.deepEqual(await f.request(platform, 'playlist', { id: '123', limit: 30 }), first);
    assert.deepEqual(await f.request(platform, 'playlist', { id: '123', limit: 30, offset: 0, continuation: { offset: 30 } }), first);
    const next = await f.request(platform, 'playlist', { id: '123', page: 2, limit: 30 });
    assert.equal(next.page, 2); assert.deepEqual(next.items.map(idOf), [secondId, secondId]);
    assert.deepEqual(await f.request(platform, 'playlist', { id: '123', page: '2', limit: '30', offset: '30' }), next);
    for (const offset of ['not-a-number', {}, { toString: null }])
      await assert.rejects(f.request(platform, 'playlist', { id: '123', limit: 30, offset }), { code: 'INVALID_REQUEST' });
    assert.deepEqual(offsets, [0, 30, 30]); assert.equal(f.requests.length, 3);
  });
});

test('signed-in TX and KG playlist flights do not mix offsets and share synonymous in-flight requests', async t => {
  for (const platform of ['tx', 'kg']) await t.test(platform, async st => {
    const held = gate(), offsets = [];
    const firstId = platform === 'tx' ? 'first' : kgHash, secondId = platform === 'tx' ? 'second' : 'B' + kgHash.slice(1);
    const f = fixture(async request => {
      const value = platform === 'tx' ? qq(request).req_0.param : kg(request).fields;
      const offset = platform === 'tx' ? value.song_begin : Number(value.begin_idx);
      offsets.push(offset); await held.wait();
      const id = offset === 0 ? firstId : secondId;
      return platform === 'tx' ? rpc({ dirinfo: { id: '123' }, songlist: [txSong(id)], total_song_num: 1, hasmore: 0 }) :
        response({ status: 1, data: { list_info: { global_collection_id: '123', count: 1 }, songs: [kgSong(id)], has_more: 0 } });
    });
    seedClients(f); await f.session(platform);
    st.after(async () => { held.release(); await f.close(); });
    const first = f.request(platform, 'playlist', { id: '123', limit: 30, offset: 0 }); first.catch(() => {});
    await Promise.race([held.entered, first]);
    const second = f.request(platform, 'playlist', { id: '123', limit: 30, offset: 30 }); second.catch(() => {});
    await new Promise(setImmediate);
    assert.deepEqual(offsets, [0, 30]); assert.equal(f.requests.length, 2);
    const same = f.request(platform, 'playlist', { id: '123', limit: 30, continuation: { offset: '30' } }); same.catch(() => {});
    await new Promise(setImmediate);
    assert.equal(f.requests.length, 2);
    held.release();
    const [a, b, c] = await Promise.all([first, second, same]);
    assert.deepEqual(a.items.map(idOf), [firstId]); assert.deepEqual(b.items.map(idOf), [secondId]);
    assert.deepEqual(c, b);
  });
});

test('KG owned playlist flights follow pages and ignore unconsumed explicit and continuation offsets', async t => {
  const held = gate(), pages = [];
  const f = fixture(async request => {
    const { path, body } = kg(request);
    assert.equal(path, '/v4/get_list_all_file_v3'); assert.equal(body.listid, '7'); assert.equal(body.pagesize, 30);
    assert.equal(Object.hasOwn(body, 'offset'), false); assert.equal(Object.hasOwn(body, 'begin_idx'), false);
    pages.push(body.page); await held.wait();
    return response({ status: 1, data: { list_info: { name: 'Owned' }, count: 1, has_more: 0,
      info: [{ ...kgSong(body.page === 1 ? kgHash : 'B' + kgHash.slice(1)), fileid: body.page === 1 ? '11' : '22', sort: 1 }] } });
  });
  seedClients(f); await f.session('kg');
  t.after(async () => { held.release(); await f.close(); });
  const params = { id: '0:7', limit: 30, personal: { kind: 'kugou-owned', listId: '7' } };
  const first = f.request('kg', 'playlist', { ...params, page: 1, offset: 0, continuation: { offset: 0 } }); first.catch(() => {});
  await Promise.race([held.entered, first]);
  const same = f.request('kg', 'playlist', { ...params, page: '1', offset: { toString: null },
    continuation: { offset: 'not-a-number' } }); same.catch(() => {});
  const next = f.request('kg', 'playlist', { ...params, page: 2, offset: 999 }); next.catch(() => {});
  await new Promise(setImmediate);
  assert.deepEqual(pages, [1, 2]); assert.equal(f.requests.length, 2);
  held.release();
  const [a, b, c] = await Promise.all([first, same, next]);
  assert.deepEqual(b, a); assert.deepEqual(a.memberIds, ['11']); assert.deepEqual(c.memberIds, ['22']);
  assert.equal(c.page, 2);
});

test('TX and KG complete playlists keep direct current reads rather than adopting the public page cache', async t => {
  for (const platform of ['tx', 'kg']) await t.test(platform, async st => {
    const f = fixture(request => {
      const value = platform === 'tx' ? qq(request).req_0.param : kg(request).fields;
      assert.equal(platform === 'tx' ? value.song_begin : Number(value.begin_idx), 0);
      const id = 'canonical-' + f.requests.length;
      return platform === 'tx' ? rpc({ dirinfo: { id }, songlist: [txSong()], total_song_num: 1, hasmore: 0 }) :
        response({ status: 1, data: { list_info: { global_collection_id: id, count: 1 }, songs: [kgSong()], has_more: 0 } });
    });
    seedClients(f); st.after(() => f.close());
    assert.equal((await f.request(platform, 'playlist', { id: '123' })).id, 'canonical-1');
    const params = { id: '123', page: {}, limit: {}, offset: {}, continuation: { offset: {} } };
    const first = await f.request(platform, 'playlist.complete', params);
    const second = await f.request(platform, 'playlist.complete', params);
    assert.equal(first.id, 'canonical-2'); assert.equal(second.id, 'canonical-3');
    assert.equal(first.complete, true); assert.equal(second.count, 1); assert.equal(f.requests.length, 3);
  });
});

test('WY collection pages do not duplicate later embedded members or verify snapshots', async () => {
  const ids = Array.from({ length: 135 }, (_, index) => String(index + 1));
  const f = fixture(request => {
    const { path } = eapi(request);
    assert.equal(path, '/api/v6/playlist/detail');
    return response({ code: 200, playlist: { id: 'canonical-playlist', name: 'current',
      trackCount: 120, trackIds: ids.map(id => ({ id })), tracks: ids.map(id => wySong(id)) } });
  });
  const first = await f.request('wy', 'playlist', { id: 'alias-playlist', page: 1, limit: 30 });
  assert.deepEqual(first.items.map(idOf), ids.slice(0, 30));
  assert.equal(first.id, 'canonical-playlist'); assert.equal(first.total, 120); assert.equal(first.more, true);
  const next = await f.request('wy', 'playlist', { id: 'alias-playlist', page: 2, limit: 30 });
  assert.deepEqual(next.items.map(idOf), ids.slice(30, 60));
  const complete = await f.request('wy', 'playlist.complete', { id: 'alias-playlist' });
  assert.deepEqual(complete.items.map(idOf), ids);
  assert.equal(f.requests.length, 3, 'each required read executes once, no head reread');
  await f.close();
});

test('WY actual member detail batches preserve declaration order and duplicate occurrences', async () => {
  const ids = [...Array.from({ length: 120 }, (_, index) => String(index + 1)), '5'];
  const f = fixture(request => {
    const url = new URL(request.url);
    if (url.pathname === '/eapi/v6/playlist/detail') return response({ code: 200,
      playlist: { id: 'list', name: 'current', trackCount: ids.length, trackIds: ids.map(id => ({ id })), tracks: [] } });
    assert.equal(url.pathname, '/weapi/v3/song/detail');
    const batch = f.requests.length === 2 ? ids.slice(0, 100) : ids.slice(100);
    return response({ code: 200, songs: [...new Set(batch)].reverse().map(id => wySong(id)) });
  });
  const result = await f.request('wy', 'playlist.complete', { id: 'list' });
  assert.deepEqual(result.items.map(idOf), ids);
  assert.deepEqual(result.sourcePositions, ids.map((_, index) => index));
  assert.equal(f.requests.length, 3);
  await f.close();
});

test('WY canonical song aliases remain actual records without fabricated membership positions', async () => {
  const f = fixture(request => new URL(request.url).pathname.includes('playlist/detail') ?
    response({ code: 200, playlist: { id: 'list', name: 'list', trackIds: [{ id: '123' }], tracks: [] } }) :
    response({ code: 200, songs: [wySong('456')] }));
  const result = await f.request('wy', 'playlist.complete', { id: 'list' });
  assert.deepEqual(result.items.map(idOf), ['456']); assert.deepEqual(result.sourcePositions, [null]);
  await f.close();
});

test('WY pages consume selected members without parsing later IDs or metadata, while complete reads still require member identity', async t => {
  for (const bad of [{ id: {} }, null, { id: '2' }]) await t.test(JSON.stringify(bad), async t => {
    const f = fixture(request => {
      assert.equal(new URL(request.url).pathname, '/eapi/v6/playlist/detail');
      return response({ code: 200, playlist: { id: 'list', trackIds: [{ id: '1' }, bad],
        tracks: [wySong('1'), wySong('2', { ar: [null] })] } });
    });
    t.after(() => f.close());
    const first = await f.request('wy', 'playlist', { id: 'list', limit: 1 });
    assert.deepEqual(first.items.map(idOf), bad?.id === '2' ? ['1'] : ['1', '2']);
    assert.deepEqual(first.sourcePositions, bad?.id === '2' ? [0] : [0, null]);
    assert.deepEqual(first.memberIds, ['1']); assert.equal(first.consumed, 1); assert.equal(first.more, true);
    assert.equal(f.requests.length, 1);
    if (bad?.id !== '2') {
      await assert.rejects(f.request('wy', 'playlist', { id: 'list', page: 2, limit: 1 }), { code: 'INVALID_RESPONSE' });
      await assert.rejects(f.request('wy', 'playlist.complete', { id: 'list' }), { code: 'INVALID_RESPONSE' });
    }
  });
});

test('WY first-page embedded canonical records retain identity without claiming a declared member position', async t => {
  const f = fixture(request => response(new URL(request.url).pathname.includes('playlist/detail') ?
    { code: 200, playlist: { id: 'list', trackIds: [{ id: '1' }, { id: '2' }, { id: '1' }],
      tracks: [wySong('1'), wySong('2'), wySong('9')] } } : { code: 200, songs: [] }));
  t.after(() => f.close());
  const first = await f.request('wy', 'playlist', { id: 'list', limit: 1 });
  assert.deepEqual(first.items.map(idOf), ['1', '9']); assert.deepEqual(first.sourcePositions, [0, null]);
  const later = await f.request('wy', 'playlist', { id: 'list', page: 3, limit: 1 });
  assert.deepEqual(later.items.map(idOf), ['1']); assert.deepEqual(later.sourcePositions, [2]);
  const complete = await f.request('wy', 'playlist.complete', { id: 'list' });
  assert.deepEqual(complete.items.map(idOf), ['1', '2', '1', '9']);
  assert.deepEqual(complete.memberIds, ['1', '2', '1']); assert.deepEqual(complete.sourcePositions, [0, 1, 2, null]);
});

test('optional artist and Migu format members do not discard locatable songs or healthy siblings', async t => {
  for (const platform of ['wy', 'tx', 'mg']) await t.test(platform, async t => {
    const reply = items => platform === 'wy' ? response({ code: 200, result: { songs: items, songCount: items.length } }) :
      platform === 'tx' ? rpc({ body: { item_song: items }, meta: { sum: items.length, nextpage: -1 } }) :
      response({ code: '000000', data: { songResultData: { resultList: [items], totalCount: 1 } } });
    const make = { wy: wySong, tx: txSong, mg: mgSong }[platform], key = { wy: 'ar', tx: 'singer', mg: 'singerList' }[platform];
    const f = fixture(() => reply([make('1', { [key]: [null, {}, { name: 'Artist' }], audioFormats: [null, 3,
      { formatType: 'SQ', size: 1048576, hash: 'actual' }] }), make('2', { [key]: [null] }), make('3')]));
    seedClients(f); t.after(() => f.close());
    const result = await f.request(platform, 'search', { keyword: 'synthetic' });
    assert.deepEqual(result.items.map(idOf), ['1', '2', '3']);
    const songs = result.items.map(row => row.song ?? row);
    assert.deepEqual(songs[0].artistNames, ['Artist']); assert.deepEqual(songs[1].artistNames, []);
    if (platform === 'mg') {
      const info = songs[0].sources[0].musicInfo;
      assert.deepEqual(info.audioFormats, [{ formatType: 'SQ', size: 1048576, hash: 'actual' }]);
      assert.deepEqual(info.types, [{ type: 'flac', size: '1.00 MB' }]);
    }
    const invalid = fixture(() => reply([{}])); seedClients(invalid); t.after(() => invalid.close());
    await assert.rejects(invalid.request(platform, 'search', { keyword: 'synthetic' }), { code: 'INVALID_RESPONSE' });
    const broken = fixture(() => response('{"broken":')); seedClients(broken); t.after(() => broken.close());
    await assert.rejects(broken.request(platform, 'search', { keyword: 'synthetic' }), { code: 'INVALID_RESPONSE' });
  });
});

test('KW and MG optional playlist counts stay unknown without changing real search pagination', async t => {
  for (const platform of ['kw', 'mg']) for (const count of ['unknown', { toString: null }, false, null]) {
    const items = platform === 'kw' ? [{ playlistid: '1', songnum: count }, { playlistid: '2', songnum: 4 }] :
      [{ musicListId: '1', musicNum: count }, { musicListId: '2', musicNum: 4 }];
    const f = fixture(() => response(platform === 'kw' ? { abslist: items, TOTAL: 10 } :
      { code: '000000', data: { songListResultData: { result: items, totalCount: 10 } } }));
    t.after(() => f.close());
    const result = await f.request(platform, 'search', { keyword: 'synthetic', kind: 'playlists', limit: 2 });
    assert.deepEqual(result.items.map(row => row.count), [null, 4]);
    assert.equal(result.total, 10); assert.equal(result.more, true); assert.deepEqual(result.continuation, { page: 2 });
  }
});

test('KW and MG collection continuation uses consumed batch/index even across short upstream pages', async t => {
  for (const platform of ['kw', 'mg']) await t.test(platform, async () => {
    const batches = [[1, 2], [3, 4, 5], [6]], calls = [];
    const f = fixture(request => {
      const url = new URL(request.url);
      if (platform === 'mg' && url.pathname === '/MIGUM3.0/resource/playlist/v2.0')
        return response({ code: '000000', data: { musicListId: 'list', title: 'current', musicNum: 6 } });
      const batch = Number(url.searchParams.get(platform === 'kw' ? 'pn' : 'pageNo')) - (platform === 'kw' ? 0 : 1);
      calls.push(batch);
      const ids = batches[batch] ?? [], more = batch < 2;
      return platform === 'kw' ? response({ result: 'ok', playlistid: 'list', name: 'current',
        total: 6, more: +more, musiclist: ids.map(id => kwSong(String(id))) }) :
        response({ code: '000000', data: { totalCount: 6, hasMore: +more, songList: ids.map(id => mgSong(String(id))) } });
    });
    const first = await f.request(platform, 'playlist', { id: 'list', limit: 4 });
    assert.deepEqual(first.items.map(idOf), ['1', '2', '3', '4']);
    assert.deepEqual(first.continuation, { page: 2, batch: 1, index: 2 });
    const second = await f.request(platform, 'playlist', { id: 'list', limit: 4, page: 2, continuation: first.continuation });
    assert.deepEqual(second.items.map(idOf), ['5', '6']); assert.equal(second.continuation, null);
    assert.deepEqual(calls, [0, 1, 1, 2]);
    const complete = await f.request(platform, 'playlist.complete', { id: 'list' });
    assert.deepEqual(complete.items.map(idOf), ['1', '2', '3', '4', '5', '6']);
    assert.deepEqual(calls.slice(4), [0, 1, 2]);
    await f.close();
  });
});

test('MG group pagination retains every version and advances by consumed groups, not visible songs', async () => {
  const f = fixture(() => response({ code: '000000', data: { songResultData: { totalCount: 8,
    resultList: [[mgSong('one'), mgSong('two')], [], [mgSong('three')]] } } }));
  const result = await f.request('mg', 'search', { keyword: 'x', limit: 3 });
  assert.deepEqual(result.items.map(idOf), ['one', 'two', 'three']);
  assert.deepEqual(result.items.map(row => row.rank), [1, 1, 3]);
  assert.equal(result.consumed, 3); assert.equal(result.more, true); assert.equal(result.continuation.page, 2);
  await f.close();
});

test('MG collection recipes resolve their documented root-relative artwork without inventing other resources', async () => {
  for (const [address, expected] of [
    ['/cover.jpg?size=large', 'https://d.musicapp.migu.cn/cover.jpg?size=large'],
    ['https://independent-cdn.invalid/cover.jpg?size=small', 'https://independent-cdn.invalid/cover.jpg?size=small'],
    ['//cdn.invalid/cover.jpg', ''],
    ['file:///cover.jpg', ''],
    ['https://user:secret@cdn.invalid/cover.jpg', ''],
    [null, ''],
  ]) {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.pathname.endsWith('/searchAll')) return response({ code: '000000', data: {
        songListResultData: { totalCount: 1, result: [{ musicListId: 'list', title: 'Current', musicListPicUrl: address }] }
      } });
      if (url.pathname.endsWith('/playlist/v2.0')) return response({ code: '000000',
        data: { musicListId: 'list', title: 'Current', musicNum: 1, imgItem: { img: address } } });
      return response({ code: '000000', data: { totalCount: 1, hasMore: 0, songList: [mgSong('member')] } });
    });
    const search = await f.request('mg', 'search', { keyword: 'x', kind: 'playlists' });
    const partial = await f.request('mg', 'playlist', { id: 'list' });
    const complete = await f.request('mg', 'playlist.complete', { id: 'list' });
    assert.equal(search.items[0].cover, expected);
    for (const result of [partial, complete]) {
      assert.equal(result.cover, expected);
      assert.deepEqual(result.items.map(idOf), ['member'], 'optional artwork never blocks real members');
    }
    assert.equal(f.requests.length, 5, 'artwork conversion does not fetch a separate resource');
    await f.close();
  }
});

test('MG real script metadata retains content identity and mapped formats without copying signed resources', async () => {
  const raw = mgSong('actual-resource', { contentId: 'CONTENT1', hash: 'source-hash', md5: 'source-md5',
    audioFormats: [{ formatType: 'HQ', asize: 1048576, aformat: '020010', contentId: 'FORMAT1',
      url: 'https://private.invalid/audio?token=not-public', unused: { changed: true } },
    { formatType: 'SQ', size: 2097152 }, { formatType: 'HQ', size: 999999 }],
    unused: { another: 'shape' } });
  const f = fixture(request => {
    const url = new URL(request.url);
    if (url.pathname.endsWith('/resourceinfo.do')) {
      assert.equal(new URLSearchParams(request.body).get('resourceId'), 'alias');
      return response({ code: '000000', resource: [raw] });
    }
    return response({ code: '000000', songResultData: { totalCount: 1, resultList: [[raw]] } });
  });
  for (const result of [(await f.request('mg', 'search', { keyword: 'x' })).items[0].song,
    await f.request('mg', 'song', { id: 'alias' })]) {
    assert.equal(idOf(result), 'actual-resource');
    const info = result.sources[0].musicInfo;
    assert.equal(info.contentId, 'CONTENT1'); assert.equal(info.hash, 'source-hash'); assert.equal(info.md5, 'source-md5');
    assert.deepEqual(info.types, [{ type: '320k', size: '1.00 MB' }, { type: 'flac', size: '2.00 MB' }]);
    assert.deepEqual(info._types, { '320k': { size: '1.00 MB' }, flac: { size: '2.00 MB' } });
    assert.equal(info.audioFormats[0].aformat, '020010'); assert.equal(info.audioFormats[0].contentId, 'FORMAT1');
    assert.equal(info.audioFormats[0].url, undefined); assert.equal(info.audioFormats[0].unused, undefined);
    assert.ok(!JSON.stringify(result).includes('private.invalid'));
  }
  assert.equal(f.requests.length, 2); await f.close();
});

test('QQ real nextpage advances a short page and KG string false markers end the page', async () => {
  const tx = fixture(request => {
    const data = qq(request).req_0.param;
    return rpc({ body: { item_song: [txSong('page-' + data.page_num)] }, meta: { sum: 99, nextpage: data.page_num === 1 ? 4 : -1 } });
  }); seedClients(tx);
  const first = await tx.request('tx', 'search', { keyword: 'x', limit: 30 });
  assert.equal(first.continuation.page, 4);
  const next = await tx.request('tx', 'search', { keyword: 'x', page: first.continuation.page });
  assert.equal(idOf(next.items[0]), 'page-4'); assert.equal(next.more, false); await tx.close();
  const kg = fixture(() => response({ status: 1, data: { lists: [kgSong()], total: 999, has_next: '0' } }));
  const result = await kg.request('kg', 'search', { keyword: 'x' });
  assert.equal(result.more, false); assert.equal(result.continuation, null); await kg.close();
});

test('KW metadata fallback reads data-only Nuxt literals and never executes scripts', async () => {
  const f = fixture(request => new URL(request.url).host === 'm.kuwo.cn' ?
    response({ status: 301, msg: '音乐查询失败' }) :
    response('<script>window.__NUXT__=(function(a){return {data:[{songinfo:{id:a,name:"O’Connor &amp; A",artist:"A/B",duration:240}}]}}("canonical"));</script>'));
  const result = await f.request('kw', 'song', { id: 'alias' });
  assert.equal(idOf(result), 'canonical'); assert.equal(result.title, 'O’Connor & A'); assert.equal(result.artist, 'A/B');
  await f.close();
  const malicious = fixture(request => new URL(request.url).host === 'm.kuwo.cn' ?
    response({ status: 301, msg: '音乐查询失败' }) :
    response('<script>window.__NUXT__=(function(){return {data:[{songinfo:run()}]}}());</script>'));
  await assert.rejects(malicious.request('kw', 'song', { id: 'x' }), { code: 'INVALID_RESPONSE' });
  await malicious.close();
});

test('KW SSR unusable object aliases, missing aliases and trailing code are data failures without expression execution', async () => {
  // HEAD Windows MusicKwMgCatalogTests.KwUnsafePage. Duplicate-property and
  // canonical-ID admission expectations are not retained.
  const page = '<html><script>window.__NUXT__=(function(a,b){return {data:[{songinfo:{rid:a,musicrid:"MUSIC_1",name:"Synthetic",artist:"A",album:"B",duration:180,formats:b}}]}}("1","MP3128"));</script></html>';
  for (const html of [
    page.replace('"1","MP3128"', '{"id":1},"MP3128"'),
    page.replace('rid:a,', 'rid:c,'),
    page.replace('duration:180', 'duration:180;globalThis.catalogPageInjected=true'),
  ]) {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (url.host === 'm.kuwo.cn') {
        assert.equal(url.searchParams.get('musicId'), '1');
        return response({ status: 301, msg: '音乐查询失败' });
      }
      assert.equal(url.href, 'https://www.kuwo.cn/play_detail/1');
      return response(html);
    });
    await assert.rejects(f.request('kw', 'song', { id: '1' }), { code: 'INVALID_RESPONSE' });
    assert.equal(globalThis.catalogPageInjected, undefined); assert.equal(f.requests.length, 2);
    await f.close();
  }
});

test('mandatory material, JSON, transport, upstream denial and byte limits keep distinct real errors', async () => {
  const missing = fixture(() => { assert.fail('missing keyword must not dispatch'); });
  await assert.rejects(missing.request('wy', 'search'), { code: 'INVALID_REQUEST' });
  await assert.rejects(missing.request('wy', 'search', { keyword: 'x', page: { toString: null } }), { code: 'INVALID_REQUEST' });
  assert.equal(missing.requests.length, 0); await missing.close();
  for (const [body, options, code] of [
    ['broken-json', {}, 'INVALID_RESPONSE'],
    ['{"code":200,1e400:0}', {}, 'INVALID_RESPONSE'],
    ['{"code":200,"unused":"\\1e400}', {}, 'INVALID_RESPONSE'],
    [{ code: 500 }, {}, 'UPSTREAM_ERROR'],
    ['denied', { status: 503 }, 'UPSTREAM_HTTP'],
    ['x'.repeat(4 * 1024 * 1024 + 1), {}, 'LIMIT'],
    [{ code: 200, result: { songs: [{ name: 'missing actual ID' }], songCount: 1 } }, {}, 'INVALID_RESPONSE'],
  ]) {
    const f = fixture(() => response(body, options));
    await assert.rejects(f.request('wy', 'search', { keyword: 'x' }), { code }); await f.close();
  }
  const network = fixture(() => { throw Error('synthetic network'); });
  await assert.rejects(network.request('wy', 'search', { keyword: 'x' }), { code: 'NETWORK_ERROR' }); await network.close();
});

test('TX consumed directory offsets fail before dependency reads and created-only calls ignore unused continuation', async t => {
  const invalid = fixture(() => assert.fail('bad directory offset must not dispatch'));
  t.after(() => invalid.close()); await invalid.session('tx');
  for (const kind of ['all', 'subscribed']) for (const txOffset of ['not-a-number', '1e400', {}, { toString: null }]) {
    await assert.rejects(invalid.request('tx', 'personalPlaylists', { kind, continuation: { txOffset } }),
      { code: 'INVALID_REQUEST' });
  }
  assert.equal(invalid.requests.length, 0); assert.equal(invalid.reads.length, 0); assert.equal(invalid.writes.length, 0);
  const methods = [];
  const f = fixture(request => {
    const { method, param } = qq(request).req_0; methods.push(method);
    if (method === 'GetPlaylistByUin') {
      assert.equal(param.uin, '42');
      return rpc({ v_playlist: [{ id: '71', name: 'Own', uin: '42', dirid: '7' }] });
    }
    assert.equal(method, 'CgiGetPlaylistFavInfo'); assert.equal(param.offset, 40); assert.equal(param.size, 30);
    return rpc({ v_list: [{ id: '72', name: 'Subscribed', uin: 'other' }], hasmore: 0, total: 1 });
  });
  seedClients(f); t.after(() => f.close()); await f.session('tx');
  const created = await f.request('tx', 'personalPlaylists', { kind: 'created', continuation: { txOffset: { toString: null } } });
  assert.deepEqual(created.items.map(row => row.id), ['favorites', '71']);
  const subscribed = await f.request('tx', 'personalPlaylists', { kind: 'subscribed', page: '2', limit: '30',
    continuation: { txOffset: '40' } });
  assert.deepEqual(subscribed.items.map(row => row.id), ['72']); assert.equal(subscribed.more, false);
  const all = await f.request('tx', 'personalPlaylists', { continuation: { txPhase: 'subscribed', txOffset: '40' } });
  assert.deepEqual(all.items.map(row => row.id), ['72']);
  assert.deepEqual(methods, ['GetPlaylistByUin', 'CgiGetPlaylistFavInfo', 'GetPlaylistByUin', 'CgiGetPlaylistFavInfo']);
});

test('required WY playlist IDs and hot playback keywords fail at their actual array conversion before HTTP', async () => {
  const f = fixture(() => assert.fail('unusable required arrays must not dispatch'));
  for (const [operation, key] of [['playlists', 'ids'], ['searchHotPlayback', 'keywords']])
    for (const value of [undefined, null, '123', {}, [null]])
      await assert.rejects(f.request('wy', operation, value === undefined ? {} : { [key]: value }), { code: 'INVALID_REQUEST' });
  assert.equal(f.requests.length, 0); await f.close();

  const valid = fixture(request => {
    const { path, data } = eapi(request);
    if (path === '/api/tag/tab/playlists') {
      assert.equal(data.ids, '123,' + huge);
      return response({ code: 200, data: [{ id: 'canonical', name: 'actual' }] });
    }
    assert.equal(path, '/api/hot/search/relate/song/get');
    assert.deepEqual(JSON.parse(data.keywords), ['123', 'A & B']);
    return response({ code: 200, data: { songs: [{ song: wySong('actual-song') }] } });
  });
  assert.equal((await valid.request('wy', 'playlists', { ids: [123, huge] })).items[0].id, 'canonical');
  assert.equal(idOf((await valid.request('wy', 'searchHotPlayback', { keywords: [123, 'A & B'] })).items[0]), 'actual-song');
  assert.equal(valid.requests.length, 2); await valid.close();
});

test('KG complete collection consumes a convertible numeric locator and classifies missing material before HTTP', async () => {
  const missing = fixture(() => assert.fail('missing collection locator must not dispatch'));
  for (const id of [undefined, null, {}])
    await assert.rejects(missing.request('kg', 'playlist.complete', id === undefined ? {} : { id }), { code: 'INVALID_REQUEST' });
  assert.equal(missing.requests.length, 0); await missing.close();
  const f = fixture(request => {
    const { path, fields } = kg(request);
    assert.equal(path, '/pubsongs/v2/get_other_list_file_nofilt');
    assert.equal(fields.global_collection_id, '123'); assert.equal(fields.pagesize, '100');
    return response({ status: 1, data: { list_info: { global_collection_id: 'canonical', name: 'actual', count: 1 },
      songs: [kgSong()], has_more: 0 } });
  }); seedClients(f);
  const result = await f.request('kg', 'playlist.complete', { id: 123 });
  assert.equal(result.id, 'canonical'); assert.deepEqual(result.items.map(idOf), [kgHash]);
  assert.equal(result.complete, true); assert.equal(f.requests.length, 1); await f.close();
});

test('KW and MG unknown optional durations retain usable locators, while positive fractional seconds stay exact', async () => {
  for (const platform of ['kw', 'mg']) {
    for (const duration of [undefined, null, 0, -1, true, false, 'unknown', 'Infinity', [], [240], { toString: null }, 240.25]) {
      const row = platform === 'kw' ? kwSong('usable', { DURATION: duration }) : mgSong('usable', { duration });
      const f = fixture(() => platform === 'kw' ? response({ TOTAL: 1, abslist: [row] }) :
        response({ code: '000000', data: { songResultData: { totalCount: 1, resultList: [[row]] } } }));
      const result = (await f.request(platform, 'search', { keyword: 'x' })).items[0];
      const song = result.song ?? result;
      assert.equal(idOf(song), 'usable'); assert.equal(song.duration, duration === 240.25 ? 240.25 : null);
      assert.equal(song.sources[0].musicInfo.duration, song.duration);
      assert.equal(f.requests.length, 1); await f.close();
    }
    const invalid = fixture(() => platform === 'kw' ?
      response({ TOTAL: 1, abslist: [{ SONGNAME: 'missing locator', DURATION: 'unknown' }] }) :
      response({ code: '000000', data: { songResultData: { totalCount: 1, resultList: [[{ songName: 'missing locator', duration: 'unknown' }]] } } }));
    await assert.rejects(invalid.request(platform, 'search', { keyword: 'x' }), { code: 'INVALID_RESPONSE' });
    assert.equal(invalid.requests.length, 1); await invalid.close();
  }
  for (const [length, expected] of [['unknown', null], ['04:unknown', null], ['1:-2', null], ['00:00', null],
    ['04:00.25', 240.25]]) {
    const f = fixture(() => response({ code: '000000', data: { songResultData: { totalCount: 1,
      resultList: [[mgSong('usable', { duration: undefined, length })]] } } }));
    const song = (await f.request('mg', 'search', { keyword: 'x' })).items[0].song;
    assert.equal(idOf(song), 'usable'); assert.equal(song.duration, expected);
    assert.equal(song.sources[0].musicInfo.duration, expected); await f.close();
  }
});

// HEAD NativeService MusicNative{Kuwo,Migu}SearchTests, public five-platform
// browse tests and Windows MusicKwMgCatalogTests: retain real wire facts while
// removing the old page/count/ID/snapshot admission rules.
test('five playlist-search recipes encode their actual selectors beyond the old page and limit thresholds', async t => {
  for (const platform of ['wy', 'tx', 'kg', 'kw', 'mg']) await t.test(platform, async () => {
    const f = fixture(request => {
      const url = new URL(request.url);
      if (platform === 'wy') {
        const { path, data } = eapi(request);
        assert.equal(path, '/api/cloudsearch/pc'); assert.equal(data.type, 1000);
        assert.equal(data.s, 'Fixture & Test'); assert.equal(data.limit, 101); assert.equal(data.offset, 20200);
        return response({ code: 200, result: { playlistCount: 0, playlists: [{ id: huge, name: 'actual' }] } });
      }
      if (platform === 'tx') {
        const row = qq(request).req_0;
        assert.equal(row.method, 'DoSearchForQQMusicMobile'); assert.equal(row.param.search_type, 3);
        assert.equal(row.param.page_num, 201); assert.equal(row.param.num_per_page, 101);
        return rpc({ body: { item_songlist: [{ tid: huge, dissname: 'actual' }] }, meta: { sum: 0, nextpage: -1 } });
      }
      if (platform === 'kg') {
        const row = kg(request);
        assert.equal(row.path, '/v1/search/special'); assert.equal(request.headers['x-router'], 'complexsearch.kugou.com');
        assert.equal(row.fields.page, '201'); assert.equal(row.fields.pagesize, '101');
        assert.equal(row.fields.keyword, 'Fixture & Test');
        return response({ status: 1, data: { lists: [{ global_collection_id: huge, specialname: 'actual' }], total: 0 } });
      }
      assert.equal(request.headers.Cookie, undefined); assert.equal(request.headers.Authorization, undefined);
      if (platform === 'kw') {
        assert.equal(url.host + url.pathname, 'search.kuwo.cn/r.s'); assert.equal(url.searchParams.get('ft'), 'playlist');
        assert.equal(url.searchParams.get('all'), 'Fixture & Test'); assert.equal(url.searchParams.get('pn'), '200');
        assert.equal(url.searchParams.get('rn'), '101');
        return response("{'TOTAL':'0','abslist':[{'playlistid':'" + huge + "','name':'L\\'amour &amp; mix'}]}");
      }
      assert.equal(url.host + url.pathname, 'jadeite.migu.cn/music_search/v3/search/searchAll');
      assert.equal(url.searchParams.get('pageNo'), '201'); assert.equal(url.searchParams.get('pageSize'), '101');
      assert.equal(url.searchParams.get('isCorrect'), '1');
      assert.equal(JSON.parse(url.searchParams.get('searchSwitch')).songlist, 1);
      assert.equal(JSON.parse(url.searchParams.get('searchSwitch')).song, 0);
      assert.equal(request.headers.timestamp, '1700000000000');
      assert.equal(request.headers.sign, '706491f168cf2cc1a56ae7a4bf33c5b2', 'independent HEAD signature golden');
      return response({ code: '000000', songListResultData: { totalCount: 0, result: [{ musicListId: huge, title: 'actual' }] } });
    }); seedClients(f);
    if (platform === 'mg') f.advance(1700000000000 - 1800000000000);
    const result = await f.request(platform, 'search', { keyword: 'Fixture & Test', kind: 'playlists', page: 201, limit: 101,
      unused: { url: 'https://not-forwarded.invalid/', headers: { Cookie: 'not-forwarded' } } });
    assert.equal(result.items[0].id, huge); assert.equal(result.more, false);
    if (platform === 'kw') assert.equal(result.items[0].name, "L'amour & mix");
    assert.equal(f.requests.length, 1); await f.close();
  });
});

test('KW and MG complete collections cross actual native batch boundaries once, preserving repeated occurrences and live totals', async t => {
  for (const platform of ['kw', 'mg']) await t.test(platform, async () => {
    const batchSize = platform === 'kw' ? 1000 : 50, count = batchSize + 1;
    const expected = Array.from({ length: count }, (_, index) => String(index === 2 ? 1 : index + 1)), calls = [];
    const f = fixture(request => {
      const url = new URL(request.url);
      if (platform === 'mg' && url.pathname === '/MIGUM3.0/resource/playlist/v2.0') {
        calls.push('metadata');
        return response({ code: '000000', data: { musicListId: 'canonical', title: 'current', musicNum: count + 10 } });
      }
      const at = Number(url.searchParams.get(platform === 'kw' ? 'pn' : 'pageNo')) - (platform === 'kw' ? 0 : 1);
      calls.push(at);
      const ids = expected.slice(at * batchSize, (at + 1) * batchSize), total = count - at;
      if (platform === 'kw') {
        assert.equal(url.searchParams.get('rn'), '1000');
        return response({ result: 'ok', playlistid: 'canonical', name: 'current', total, more: at === 0 ? 1 : 0,
          musiclist: ids.map(id => kwSong(id)) });
      }
      assert.equal(url.searchParams.get('pageSize'), '50');
      return response({ code: '000000', data: { totalCount: total, hasMore: at === 0 ? 1 : 0,
        songList: ids.map(id => mgSong(id)) } });
    });
    const result = await f.request(platform, 'playlist.complete', { id: 'alias' });
    assert.deepEqual(result.items.map(idOf), expected); assert.equal(result.id, 'canonical');
    assert.equal(result.count, count); assert.equal(result.total, count - 1); assert.equal(result.complete, true);
    assert.deepEqual(calls, platform === 'kw' ? [0, 1] : ['metadata', 0, 1]); await f.close();
  });
});

test('KW notation is data-only and single-decodes entities; missing consumed success codes remain parse errors', async () => {
  const body = `{'TOTAL':'1','abslist':[{'MUSICRID':'MUSIC_123','SONGNAME':'Don\\'t "stop" &amp;lt; &#x4e2d;','ARTIST':'One&amp;Two'}]}`;
  const good = fixture(() => response(body));
  const row = (await good.request('kw', 'search', { keyword: 'x' })).items[0];
  assert.equal(row.title, 'Don\'t "stop" &lt; 中'); assert.equal(row.artist, 'One&Two'); await good.close();
  const script = fixture(() => response(body + ';globalThis.catalogInjected=true;'));
  await assert.rejects(script.request('kw', 'search', { keyword: 'x' }), { code: 'INVALID_RESPONSE' });
  assert.equal(globalThis.catalogInjected, undefined); await script.close();
  for (const [platform, operation, params] of [
    ['kw', 'song', { id: 'x' }], ['kw', 'playlist.complete', { id: 'x' }], ['mg', 'search', { keyword: 'x' }],
  ]) {
    const f = fixture(() => response({ unrelated: { code: 200 } }));
    await assert.rejects(f.request(platform, operation, params), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1); await f.close();
  }
});

test('HTTP JSON numbers retain equivalent numeric consumption and preserve only lossy numeric tokens', () => {
  // Exactly round-trippable numbers use the standard numeric string, not the source spelling.
  assert.deepEqual(decodeJSON(Buffer.from('[9007199254740992,9007199254740993,1e30,1e400,-1e400,1.25,7,"9007199254740993",0.123456789012345678901]')),
    ['9007199254740992', '9007199254740993', '1e+30', '1e400', '-1e400', 1.25, 7, '9007199254740993', '0.123456789012345678901']);
  for (const text of ['{1e400:0}', '[1e400,]', '{"id":01}', '{"id":NaN}'])
    assert.throws(() => decodeJSON(Buffer.from(text)), { code: 'INVALID_RESPONSE' });
});
