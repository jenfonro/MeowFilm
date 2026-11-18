import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, response, rpc, eapi, weapi, kg, seedClients, fixedEntropy,
  wySong, txSong, kgSong, kwSong, mgSong, idOf } from './support.mjs';

test('five providers retain sparse songs and collections, and diagnose missing lists without rejecting siblings', async t => {
  for (const platform of ['wy', 'tx', 'kg', 'kw', 'mg']) await t.test(platform, async () => {
    let items, total = { toString: null }, songs = true;
    const f = fixture(() => {
      if (platform === 'wy') return response({ code: 200, result: songs ?
        { songs: items, songCount: total } : { playlists: items, playlistCount: total } });
      if (platform === 'tx') return rpc({ body: { [songs ? 'item_song' : 'item_songlist']: items },
        meta: { sum: total, nextpage: { toString: null } } });
      if (platform === 'kg') return response({ status: 1, data: { lists: items, total, has_next: { toString: null } } });
      if (platform === 'kw') return response({ abslist: items, TOTAL: total });
      return response({ code: '000000', [songs ? 'songResultData' : 'songListResultData']:
        { [songs ? 'resultList' : 'result']: songs && Array.isArray(items) ? [items] : items, totalCount: total } });
    });
    seedClients(f);
    for (songs of [true, false]) {
      items = [null, songs ? { name: 'Display only', title: 'Display only', SONGNAME: 'Display only', songName: 'Display only',
        l: { size: { toString: null } }, file: { size_flac: { toString: null } }, filesize: { toString: null } } :
        { name: 'Display only', title: 'Display only' }];
      if (songs) items.push({ wy: wySong, tx: txSong, kg: kgSong, kw: kwSong, mg: mgSong }[platform]('123', {
        l: { size: { toString: null } }, file: { size_flac: { toString: null } }, filesize: { toString: null },
      }));
      const value = await f.request(platform, 'search', { keyword: 'x', kind: songs ? 'songs' : 'playlists' });
      assert.equal(value.items.length, songs ? 3 : 2); assert.equal(value.total, null);
      assert.equal(value.items[1][songs ? 'title' : 'name'], 'Display only');
      if (songs) {
        assert.deepEqual(value.items[1].sources, []); assert.equal(idOf(value.items[2]), '123');
      }
      else assert.equal(value.items[1].id, '');
      for (items of [null, false, { rows: [] }]) {
        const empty = await f.request(platform, 'search', { keyword: 'x', kind: songs ? 'songs' : 'playlists' });
        assert.deepEqual(empty.items, []); assert.equal(empty.total, null);
        assert.ok(empty.diagnostics.some(row => row.stage === 'extract'));
      }
    }
    assert.equal(f.requests.length, 8); await f.close();
  });
});

test('five single-song recipes preserve display-only or missing records without inventing requested identity', async t => {
  fixedEntropy(t);
  for (const platform of ['wy', 'tx', 'kg', 'kw', 'mg']) await t.test(platform, async () => {
    const raw = { name: 'Display', title: 'Display', SONGNAME: 'Display', songName: 'Display' };
    const f = fixture(() => platform === 'wy' ? response({ code: 200, songs: [raw] }) :
      platform === 'tx' ? rpc({ tracks: [raw] }) : platform === 'kg' ? response({ status: 1, data: [raw] }) :
      platform === 'kw' ? response({ status: 200, data: { songinfo: raw } }) :
      response({ code: '000000', resource: [raw] }));
    seedClients(f);
    const value = await f.request(platform, 'song', { id: '123' });
    assert.equal(value.title, 'Display'); assert.deepEqual(value.sources, []);
    assert.equal(f.requests.length, 1); await f.close();
  });
});

test('QQ successful null payloads are consumed locally by ordinary reads', async () => {
  const f = fixture(() => rpc(null));
  seedClients(f);
  for (const [operation, params] of [['search', { keyword: 'x' }], ['playlist', { id: '1' }],
    ['playlist.complete', { id: '1' }]]) {
    const value = await f.request('tx', operation, params);
    assert.deepEqual(value.items, []);
  }
  const song = await f.request('tx', 'song', { id: '1' });
  assert.equal(song.title, ''); assert.deepEqual(song.sources, []);
  assert.deepEqual(await f.request('tx', 'lyrics', { id: '1' }), { text: '', translation: '', romanization: '' });
  await f.session('tx');
  const lists = await f.request('tx', 'personalPlaylists');
  assert.deepEqual(lists.items.map(row => row.id), ['favorites']);
  for (const kind of ['guess', 'radar'])
    assert.deepEqual((await f.request('tx', 'recommendations', { kind })).items, []);
  assert.equal(f.requests.length, 9); await f.close();
});

test('WY home and Cube preserve display materials, banner image semantics and FM modes without summary reads', async t => {
  fixedEntropy(t);
  const fm = { resourceType: 'fm', type: 'fm', id: 'fm', name: 'Mode',
    mode: 'SCENE_RCMD', subMode: 'opaque-scene' };
  const banner = { resourceType: 'playlist', id: '41', name: 'Banner', coverType: 'ALBUM_LOGO',
    imageUrl: '//images.invalid/wide.jpg', cover: 'https://images.invalid/square.jpg',
    action: 'orpheus://playlist/41?coverUrl=https%3A%2F%2Fimages.invalid%2Ftarget.jpg' };
  const display = { resourceType: 'playlist', id: '42', name: 'No image' };
  const nameless = { resourceType: 'playlist', id: '43', imageUrl: '//images.invalid/image-only.jpg' };
  const song = { resourceType: 'song', resourceExtInfo: { songData: wySong('44') } };
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/link/page/rcmd/resource/show')) return response({ code: 200, data: { blocks: [
      { positionCode: 'banner', showType: 'BANNER', creatives: [null, { resources: [banner, {
        ...song, imageUrl: null,
      }] }] },
      { positionCode: 'feature', creatives: [{ resources: [fm, song, display, nameless, {
        resourceType: 'playlist', name: 'Display only', uiElement: { images: [null, {
          imageUrl: {}, url: '//images.invalid/fallback.jpg',
        }] },
      }, { resourceType: 'playlist', id: '45', name: 'Header label', subTitle: 'Business title',
        singleLineTitle: 'Current subtitle' }] }] },
      { positionCode: 'songs', creatives: [{ resources: [{ ...song, name: 'Card title',
        cover: '//images.invalid/card.jpg' }] }] },
    ], hasMore: true } });
    assert.ok(path.endsWith('/cube/render/page/protocol'), 'missing display fields must not dispatch summaries or details');
    return response({ code: 200, data: { pageProtocol: { pages: [null,
      { id: 'banner', props: { carousel: [banner, fm] } },
      { id: 'recommend', props: { recommendModule: [{ modules: [fm, { ...display, type: 'playlist' },
        { ...nameless, type: 'playlist' }] }] } },
    ] } } });
  });
  const home = await f.request('wy', 'home');
  assert.equal(home.more, true); assert.equal(home.continuation, null);
  const cards = home.sections.find(row => row.layout === 'feature-rail').items;
  assert.deepEqual(cards[0].action, { type: 'fm', mode: 'scene', submode: 'opaque-scene' });
  assert.equal(cards[1].title, 'Synthetic (Live)'); assert.equal(cards[1].cover, cards[1].song.cover_url);
  assert.equal(cards[2].cover, ''); assert.deepEqual(cards[2].coverRef, { kind: 'playlist', id: '42' });
  assert.equal(cards[3].title, ''); assert.equal(cards[3].cover, 'https://images.invalid/image-only.jpg');
  assert.equal(cards[4].title, 'Display only'); assert.equal(cards[4].action, null);
  assert.equal(cards[4].cover, 'https://images.invalid/fallback.jpg');
  assert.equal(cards[5].label, 'Header label'); assert.equal(cards[5].title, 'Business title');
  assert.equal(cards[5].subtitle, 'Current subtitle');
  const songCard = home.sections.find(row => row.layout === 'song-columns').items[0];
  assert.equal(songCard.title, 'Card title'); assert.equal(songCard.cover, 'https://images.invalid/card.jpg');
  assert.equal(songCard.song.title, 'Synthetic (Live)');
  const homeBanners = home.sections.find(row => row.layout === 'banner').items;
  assert.equal(homeBanners.length, 2);
  const wide = homeBanners.find(row => row.id === '41');
  assert.equal(wide.cover, 'https://images.invalid/wide.jpg'); assert.equal(wide.coverRef, null);
  assert.equal(homeBanners.find(row => row.kind === 'song').cover, '');
  const cube = await f.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE' });
  assert.equal(cube.sections[0].layout, 'banner');
  assert.equal(cube.sections[0].items[0].cover, wide.cover);
  assert.deepEqual(cube.sections[0].items[1].action, cards[0].action);
  assert.deepEqual(cube.sections[1].items[0].action, cards[0].action);
  assert.equal(cube.sections[1].layout, 'feature-rail');
  assert.ok(cube.sections[1].items.every(row => row.label === ''));
  assert.equal(f.requests.length, 2); await f.close();
});

test('WY FM actions publish the same five canonical modes from carousel and recommendation containers', async t => {
  fixedEntropy(t);
  const modes = [['DEFAULT', 'default'], ['FAMILIAR', 'familiar'], ['EXPLORE', 'explore'],
    ['PUZZLE_MODE_RCMD', 'puzzle'], ['SCENE_RCMD', 'scene']];
  const rows = modes.map(([mode], index) => ({ type: 'fm', resourceType: 'fm', id: String(index),
    name: 'FM', mode, subMode: 'opaque-' + index }));
  const f = fixture(request => {
    assert.ok(new URL(request.url).pathname.endsWith('/cube/render/page/protocol'));
    return response({ code: 200, data: { pageProtocol: { pages: [
      { id: 'banner', props: { carousel: rows } },
      { id: 'recommend', props: { recommendModule: [{ modules: rows }] } },
    ] } } });
  });
  const value = await f.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE' });
  assert.deepEqual(value.sections.map(row => row.layout), ['banner', 'feature-rail']);
  const expected = modes.map(([, mode], index) => ({ type: 'fm', mode, submode: 'opaque-' + index }));
  for (const section of value.sections) assert.deepEqual(section.items.map(row => row.action), expected);
  assert.equal(f.requests.length, 1); await f.close();
});

test('WY explicit summaries use 100-ID batches and member recipes do not hydrate embedded display omissions', async t => {
  fixedEntropy(t);
  const batches = [];
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/tag/tab/playlists')) {
      const ids = eapi(request).data.ids.split(','); batches.push(ids.length);
      return response({ code: 200, data: ids.map(id => ({ id })) });
    }
    if (path.endsWith('/playlist/detail')) return response({ code: 200, playlist: { id: '1',
      trackIds: [{ id: '11' }, { id: '12' }], tracks: [{ id: '11' }, { name: 'Display only' }] } });
    assert.ok(path.endsWith('/v3/song/detail'));
    assert.equal(weapi(request).c, '[{"id":12}]');
    return response({ code: 200, songs: [{ name: 'Partial detail' }] });
  });
  const ids = Array.from({ length: 201 }, (_, index) => String(index + 1));
  assert.deepEqual((await f.request('wy', 'playlists', { ids })).items.map(row => row.id), ids);
  assert.deepEqual(batches, [100, 100, 1]);
  const list = await f.request('wy', 'playlist.complete', { id: '1' });
  assert.equal(idOf(list.items[0]), '11'); assert.equal(list.items[0].title, '');
  assert.deepEqual(list.items.slice(1).map(row => row.title), ['Display only', 'Partial detail']);
  assert.ok(list.items.slice(1).every(row => row.sources.length === 0));
  assert.deepEqual(list.sourcePositions, [0, null, null]);
  assert.equal(f.requests.length, 5); await f.close();
});

test('WY artist activity retains album and song display materials without executable IDs or a next timestamp', async () => {
  const f = fixture(() => response({ code: 200, data: { hasMore: true, newWorks: [
    null,
    { info: { blockType: 'album', blockTitle: { resourceName: 'Album display', resourcePicUrl: '//images.invalid/album.jpg' } } },
    { info: { blockType: 'song', blockTitle: { resourceName: 'Song display', artistName: 'Artist display' } } },
  ] } }));
  await f.session('wy');
  const value = await f.request('wy', 'artistActivity');
  assert.equal(value.more, true); assert.equal(value.continuation, null);
  assert.deepEqual(value.sections[0].items.map(row => row.title), ['Album display', 'Song display']);
  assert.deepEqual(value.sections[0].items.map(row => row.action), [null, null]);
  assert.equal(value.sections[0].items[0].cover, 'https://images.invalid/album.jpg');
  assert.deepEqual(value.sections[0].items[1].song.sources, []);
  assert.equal(f.requests.length, 1); await f.close();
});

test('WY heartbeat without a usable favorite collection finishes without invented member requests', async () => {
  const f = fixture(request => {
    assert.equal(eapi(request).path, '/api/user/playlist');
    return response({ code: 200, playlist: null, more: true });
  });
  await f.session('wy');
  const value = await f.request('wy', 'heartbeat');
  assert.deepEqual(value.items, []); assert.equal(value.entrySong, null); assert.equal(value.continuation, null);
  assert.equal(f.requests.length, 1); await f.close();
});

test('complete collection reads stop on unconsumable pages and retain content without claiming completeness', async t => {
  for (const platform of ['tx', 'kg', 'kw', 'mg']) await t.test(platform, async () => {
    let pages = 0;
    const f = fixture(request => {
      if (platform === 'mg' && new URL(request.url).pathname.endsWith('/playlist/v2.0'))
        return response({ code: '000000', musicListId: '1', musicNum: 1000 });
      pages++;
      assert.ok(pages <= 2, 'an empty page cannot cause an unbounded traversal');
      const items = pages === 1 ? [{ tx: txSong, kg: kgSong, kw: kwSong, mg: mgSong }[platform]()] : null;
      if (platform === 'tx') return rpc({ dirinfo: { id: '1' }, songlist: items, hasmore: true });
      if (platform === 'kg') return response({ status: 1, data: { list_info: { global_collection_id: '1' },
        songs: items, has_more: true } });
      if (platform === 'kw') return response({ result: 'ok', playlistid: '1', musiclist: items, more: true });
      return response({ code: '000000', songList: items, hasMore: true });
    }); seedClients(f);
    const value = await f.request(platform, 'playlist.complete', { id: '1' });
    assert.equal(value.items.length, 1); assert.equal(value.more, true); assert.equal(value.complete, false);
    assert.equal(value.continuation, null); assert.equal(pages, 2); await f.close();
  });
});

test('KG directory, section actions and rank card actions publish channelKind without client inference', async () => {
  const f = fixture(request => {
    const path = kg(request).path;
    if (path.endsWith('/get_tags_by_type')) return response({ status: 1,
      data: [null, { tag_id: '1', tag_name: 'Genre', son: [null] }] });
    if (path.endsWith('/special_recommend')) return response({ status: 1, data: null });
    if (path.endsWith('/newsong_publish')) return response({ status: 1, data: null });
    assert.ok(path.endsWith('/rank/list'));
    return response({ status: 1, data: { info: [null, { rankid: '7', rank_cid: '8' }] } });
  }); seedClients(f);
  const directory = await f.request('kg', 'channels');
  assert.deepEqual(directory.items.slice(0, 4).map(row => row.channelKind),
    ['featured', 'playlists', 'rankings', 'new-songs']);
  assert.equal(directory.items.at(-1).channelKind, 'playlists');
  const home = await f.request('kg', 'home');
  assert.deepEqual(home.sections.map(row => row.action.channel.channelKind), ['playlists', 'new-songs', 'rankings']);
  assert.equal(home.sections[2].items[0].action, null);
  const rank = home.sections[2].items[1].action.channel;
  assert.equal(rank.id, 'rank-7'); assert.equal(rank.pageCode, '8'); assert.equal(rank.channelKind, 'new-songs');
  assert.equal(f.requests.length, 4); await f.close();
});
