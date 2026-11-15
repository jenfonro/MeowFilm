import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import { fixture, response, eapi, weapi, fixedEntropy, wySong, idOf, huge, xeapi, seedClients } from './support.mjs';

// HEAD MusicNativeFMProtocolTests / MusicNativeQueueRecommendationProtocolTests:
// actual recipes, sparse metadata and provenance; no native wire parser remains.
test('FM modes and opaque scene selectors reach their own recipe with no guessed enum admission', async () => {
  let expectedMode, expectedScene;
  const f = fixture(request => {
    const { path, data } = eapi(request);
    assert.equal(path, '/api/v1/radio/get'); assert.equal(data.mode, expectedMode);
    assert.equal(data.subMode, expectedScene); assert.equal(data.limit, '3');
    assert.equal(data.unplaySongIds, '[9007199254740993123]'); assert.equal(data.unplaySonIds, undefined);
    assert.equal(data.openAidj, 'false'); assert.equal(data.aidjReqTimes, '0');
    return response({ code: 200, data: Array.from({ length: 7 }, (_, index) => wySong(String(index + 1))) });
  });
  for (const [mode, wire] of Object.entries({ default: 'DEFAULT', familiar: 'FAMILIAR', explore: 'EXPLORE',
    puzzle: 'PUZZLE_MODE_RCMD', scene: 'SCENE_RCMD', NEW_SERVER_MODE: 'NEW_SERVER_MODE' })) {
    expectedMode = wire; expectedScene = mode === 'scene' ? 'NEW_SERVER_SCENE' : '';
    const result = await f.request('wy', 'fm', { mode, submode: expectedScene, unplaySongIds: [huge] });
    assert.equal(result.items.length, 7, 'upstream request size is not a truncation or cardinality proof');
    assert.equal(result.recommendations[0].feedbackSource, 'userfm');
  }
  assert.equal(f.requests.length, 6); await f.close();
});

test('wrapped FM hydration keeps occurrence order and original outer/nested attribution, not detail metadata attribution', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    if (new URL(request.url).pathname === '/eapi/v1/radio/get') return response({ code: 200, data: [
      { songInfo: { id: huge }, source: 'row-source', sourceId: 'row-id', alg: 'outer-alg' },
      { song: wySong('102', { source: 'song-source', sourceId: 502, alg: 'song-alg' }) },
      { songInfo: wySong('103'), source: '' },
      { songInfo: { id: huge }, alg: 'duplicate-alg' },
    ] });
    assert.equal(new URL(request.url).pathname, '/weapi/v3/song/detail');
    assert.equal(weapi(request).c, '[{"id":' + huge + '}]');
    return response({ code: 200, songs: [wySong(huge, { alg: 'metadata-ignored',
      source: 'metadata-ignored', sourceId: 'metadata-ignored' })] });
  });
  const result = await f.request('wy', 'fm');
  assert.deepEqual(result.items.map(idOf), [huge, '102', '103', huge]);
  assert.ok(result.items.every(song => song.title && song.artist));
  assert.deepEqual(result.recommendations.map(row => row.alg), ['outer-alg', 'song-alg', '', 'duplicate-alg']);
  assert.deepEqual(result.recommendations.map(row => row.feedbackSource ?? ''),
    ['row-source', 'song-source', '', 'userfm']);
  assert.deepEqual(result.recommendations.map(row => row.feedbackSourceId ?? ''), ['row-id', '502', '', '']);
  assert.equal(f.requests.length, 2); await f.close();
});

test('rule FM cover reads no songs or metadata, while actual rule rows keep only proven sources', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    const { path, data } = eapi(request);
    if (path.endsWith('/fm/cover')) {
      assert.equal(data.limit, undefined); assert.equal(data.ruleType, 1);
      assert.equal(data.ruleId, 'rule'); assert.equal(data.tabTagId, '');
      return response({ code: 200, data: { cover: 'https://images.invalid/current?size=480',
        songs: [{ malformed: 'not-consumed' }] } });
    }
    assert.equal(path, '/api/tag/tab/fm/recommends'); assert.equal(data.limit, 5);
    return response({ code: 200, data: [{ songInfo: wySong('101') },
      { songInfo: wySong('102'), source: 'rule-source', sourceId: 502, alg: 'rule-alg' }] });
  });
  const preview = await f.request('wy', 'fm', { ruleId: 'rule', preview: true });
  assert.equal(preview.preview, true); assert.equal(preview.items, undefined);
  assert.equal(preview.cover, 'https://images.invalid/current?size=480');
  const result = await f.request('wy', 'fm', { ruleId: 'rule' });
  assert.equal(result.recommendations[0].feedbackSource, undefined);
  assert.equal(result.recommendations[1].feedbackSource, 'rule-source');
  assert.equal(f.requests.length, 2); await f.close();
});

test('scene queue metadata hydrates across the real 100-song batch boundary without losing maps or long IDs', async t => {
  fixedEntropy(t);
  const ids = Array.from({ length: 103 }, (_, index) => String(1001 + index)), detailCounts = [];
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/homepage/scene/more/rcmd/song') {
      const { data } = eapi(request);
      assert.equal(data.currentPlaySongId, huge); assert.equal(data.songIds, '[' + huge + ']');
      assert.equal(data.offset, '0'); assert.equal(data.songCount, '1'); assert.equal(data.extJson, '{}');
      assert.equal(data.source, 'curlist_more_rcmd'); assert.equal(data.searchTransmissionInfo, undefined);
      return response({ code: 200, data: { songList: ids.map(songId => ({ songId, name: 'Incomplete', ar: [] })),
        hasMore: true, offset: 15, algMap: '{"1001":"first-alg","1103":"last-alg"}',
        sourceMap: { 1103: 'real-source', unused: { notConsumed: true } } } });
    }
    assert.equal(path, '/weapi/v3/song/detail');
    const batch = JSON.parse(weapi(request).c); detailCounts.push(batch.length);
    return response({ code: 200, songs: batch.map(row => wySong(String(row.id))) });
  }); await f.session('wy');
  const value = await f.request('wy', 'queueRecommendations', { seedSongId: huge });
  assert.deepEqual(value.items.map(idOf), ids); assert.deepEqual(detailCounts, [100, 3]);
  assert.ok(value.items.every(song => song.artist === 'A / B'));
  assert.equal(value.recommendations[0].alg, 'first-alg'); assert.equal(value.recommendations.at(-1).alg, 'last-alg');
  assert.ok(value.recommendations.every(row => row.source === 'moreRcmd'));
  assert.equal(value.recommendations.at(-1).feedbackSource, 'real-source');
  assert.equal(value.offset, 15); assert.equal(value.hasMore, true); assert.equal(f.requests.length, 3);
  await f.close();
});

test('scene continuation uses server offsets, allows a large dynamic batch and does not invent a legacy fallback', async () => {
  let next = 15;
  const f = fixture(request => {
    const { path, data } = eapi(request);
    assert.equal(path, '/api/homepage/scene/more/rcmd/song'); assert.equal(data.offset, String(next - 15));
    assert.equal(data.currentPlaySongId, '102'); assert.equal(data.songIds, '[102]');
    return response({ code: 200, data: { songList: Array.from({ length: 1001 }, (_, index) => wySong(String(index + 1))),
      offset: next, hasMore: next < 45, algMap: null, sourceMap: null } });
  }); await f.session('wy');
  for (let offset = 0; offset < 45;) {
    const value = await f.request('wy', 'queueRecommendations', { seedSongId: '102', offset });
    assert.equal(value.items.length, 1001); assert.equal(value.offset, next);
    offset = value.offset; next += 15;
  }
  assert.equal(f.requests.length, 3); await f.close();
});

test('scene maps consume only used values; actual JSON conversion and missing continuation remain failures', async t => {
  for (const map of [{ 101: null }, null, [], 1, { unused: ['arbitrary', { content: true }] }]) {
    const f = fixture(() => response({ code: 200, data: { songList: [wySong('101')],
      offset: '15', hasMore: 1, algMap: map, sourceMap: JSON.stringify(map) } })); await f.session('wy');
    const value = await f.request('wy', 'queueRecommendations', { seedSongId: '102' });
    assert.equal(value.recommendations[0].alg, ''); assert.equal(value.hasMore, true); await f.close();
  }
  for (const fields of [{ algMap: 'invalid-json' }, { sourceMap: '{' },
    { offset: null }, { hasMore: null }, { hasMore: true, offset: 0 },
    { hasMore: true, offset: -1 }]) await t.test(JSON.stringify(fields), async () => {
    const f = fixture(() => response({ code: 200, data: { songList: [wySong('101')],
      offset: 15, hasMore: false, ...fields } })); await f.session('wy');
    await assert.rejects(f.request('wy', 'queueRecommendations', { seedSongId: '102' }), { code: 'INVALID_RESPONSE' });
    assert.equal(f.requests.length, 1); await f.close();
  });
});

// HEAD MusicNativeHeartbeatProtocolTests: every hydrated favorite is a random
// candidate; count is the actual likes count, not the number of recommendations.
test('heartbeat finds a later directory page, hydrates every batch and inserts the actual random seed without borrowed attribution', async t => {
  fixedEntropy(t);
  t.mock.method(crypto, 'randomInt', maximum => maximum === 105 ? 104 : 0);
  syncBuiltinESMExports();
  const ids = Array.from({ length: 105 }, (_, index) => String(index + 101)), batches = [];
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/user/playlist') {
      const { data } = eapi(request);
      return response({ code: 200, more: data.offset === 0, playlist: data.offset === 0
        ? [{ id: 'other', name: 'Other', specialType: 5, creator: { userId: 99 } }]
        : [{ id: '10', name: 'Likes', specialType: 5, creator: { userId: 42 } }] });
    }
    if (path === '/eapi/v6/playlist/detail') return response({ code: 200, playlist: {
      id: '10', name: 'Likes', trackCount: 2, trackIds: ids.map(id => ({ id })), tracks: [] } });
    if (path === '/weapi/v3/song/detail') {
      const batch = JSON.parse(weapi(request).c); batches.push(batch.length);
      return response({ code: 200, songs: batch.map(row => wySong(String(row.id))) });
    }
    assert.equal(path, '/eapi/playmode/intelligence/list');
    const { data } = eapi(request);
    assert.equal(data.songId, '205'); assert.equal(data.startMusicId, '205');
    assert.equal(data.count, 105); assert.equal(data.type, 'fromPlayAll');
    return response({ code: 200, data: Array.from({ length: 1001 }, (_, index) => ({
      songInfo: wySong(String(1001 + index)), alg: 'upstream-' + index,
    })) });
  }); await f.session('wy');
  const value = await f.request('wy', 'heartbeat');
  assert.deepEqual(batches, [100, 5]); assert.equal(value.items.length, 1002);
  assert.equal(idOf(value.items[0]), '205'); assert.equal(value.continuation.count, 105);
  assert.deepEqual(value.recommendations[0], { id: '205', alg: '', source: 'heartbeat', sourceId: '10',
    seedId: '205', feedbackSource: '', feedbackSourceId: '' });
  assert.equal(value.recommendations.at(-1).alg, 'upstream-1000');
  assert.equal(value.recommendations.at(-1).sourceId, '10');
  assert.equal(f.requests.length, 6); await f.close();
});

test('heartbeat moves a supplied current seed first, hydrates sparse songInfo and continues outside likes without reinserting a seed', async t => {
  fixedEntropy(t);
  let round = 0;
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/v6/playlist/detail') return response({ code: 200, playlist: {
      id: '10', name: 'Likes', tracks: [wySong('101'), wySong('102')], trackCount: 999 } });
    if (path === '/weapi/v3/song/detail') {
      assert.equal(weapi(request).c, '[{"id":103}]');
      return response({ code: 200, songs: [wySong('103', { alg: 'ignored-detail-alg' })] });
    }
    assert.equal(path, '/eapi/playmode/intelligence/list');
    const { data } = eapi(request);
    assert.equal(data.startMusicId, '102'); assert.equal(data.count, 2); assert.equal(data.type, 'fromPlayOne');
    assert.equal(data.songId, round ? '103' : '102');
    return response({ code: 200, data: round++ ? [{ songInfo: wySong('999'), alg: 'tail' }]
      : [{ songInfo: { id: '103' }, alg: 'sparse-alg' }, { songInfo: { id: '102' }, alg: 'seed-alg' }] });
  }); await f.session('wy');
  const value = await f.request('wy', 'heartbeat', { entry: 'current', playlistId: '10', songId: '102' });
  assert.deepEqual(value.items.map(idOf), ['102', '103']);
  assert.deepEqual(value.recommendations.map(row => row.alg), ['seed-alg', 'sparse-alg']);
  const next = await f.request('wy', 'heartbeat', { entry: 'current', songId: '103', continuation: value.continuation });
  assert.deepEqual(next.items.map(idOf), ['999']); assert.equal(next.continuation.startMusicId, '102');
  assert.equal(f.requests.length, 4); await f.close();
});

test('home heartbeat seed uses only one needed member and empty likes never request metadata', async t => {
  fixedEntropy(t);
  for (const empty of [false, true]) {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path === '/eapi/user/playlist') return response({ code: 200, more: false,
        playlist: [{ id: '10', name: 'Likes', specialType: 5, creator: { userId: 42 } }] });
      if (path === '/eapi/v6/playlist/detail') return response({ code: 200, playlist: {
        id: '10', name: 'Likes', trackIds: empty ? [] : [{ id: '101' }, { id: '102' }], tracks: [] } });
      assert.equal(path, '/weapi/v3/song/detail'); assert.equal(weapi(request).c, '[{"id":101}]');
      return response({ code: 200, songs: [wySong('101')] });
    }); await f.session('wy');
    const value = await f.request('wy', 'heartbeat.seed');
    assert.equal(value.song && idOf(value.song), empty ? null : '101');
    assert.equal(f.requests.length, empty ? 2 : 3); await f.close();
  }
});

test('daily style provenance cannot reflect credentials even when encoded; unconsumed raw branches never gate a batch', async t => {
  for (const field of ['alg', 'source', 'sourceId']) await t.test(field, async () => {
    const server = xeapi(); let reflect = true;
    const f = fixture(request => {
      if (new URL(request.url).pathname === '/api/bsr/sk/get') return server.key(request);
      server.open(request);
      return server.reply({ code: 200, data: { dailySongs: [{ songInfo: wySong('101'),
        [field]: reflect ? 'synthetic%252Dmusic%252Du' : 'public-value' }],
      unused: { credential: 'synthetic-music-u' } } });
    }); seedClients(f); await f.session('wy');
    await assert.rejects(f.request('wy', 'daily', { branch: 'style' }), { code: 'INVALID_RESPONSE' });
    reflect = false;
    const value = await f.request('wy', 'daily', { branch: 'style', refresh: true });
    assert.equal(value.items.length, 1); assert.equal(value.recommendations[0][field === 'source' ?
      'feedbackSource' : field === 'sourceId' ? 'feedbackSourceId' : 'alg'], 'public-value');
    await f.close();
  });
});
