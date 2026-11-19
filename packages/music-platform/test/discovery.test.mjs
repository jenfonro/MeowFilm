import test from 'node:test';
import assert from 'node:assert/strict';
import { stringifyJSONValue } from '@meowfilm/runtime-bridge/json';
import { fixture, response, rpc, eapi, weapi, qq, kg, seedClients, fixedEntropy, xeapi, wySong, txSong, kgSong,
  kgHash, idOf, gate } from './support.mjs';

test('WY dynamic home preserves current sections and actions, without layout or old-content proof', async () => {
  let revision = 1;
  const f = fixture(request => {
    const { path, data } = eapi(request);
    assert.equal(path, '/api/link/page/rcmd/resource/show');
    assert.equal(data.pageCode, 'HOME_RECOMMEND_PAGE');
    const blocks = Array.from({ length: revision === 1 ? 37 : 39 }, (_, index) => ({
      positionCode: 'dynamic-' + index, uiElement: { mainTitle: { title: 'Section ' + index } },
      creatives: [{ creativeType: 'song', resources: [{ resourceType: 'song', resourceId: String(index + revision),
        resourceExtInfo: { songData: wySong(String(index + revision), { al: {} }) } }] }],
    }));
    blocks.unshift({ positionCode: 'actions', dslData: JSON.stringify({ resources: [
      { resourceType: 'fm', resourceId: 'radar', name: 'Radar' },
      { resourceType: 'similarsong', resourceId: 'similar', name: 'Similar',
        action: 'orpheus://nm/play/similarFM?sourceId=%5B%22123%22%5D&sourceType=song&sourceName=Current' },
    ] }) });
    return response({ code: 200, data: { blocks, hasMore: false }, unused: { changed: ['anything'] } });
  });
  const first = await f.request('wy', 'home');
  assert.equal(first.sections.length, 38); assert.equal(first.sections.find(row => row.layout === 'feature-rail').items.length, 2);
  assert.equal(first.sections.find(row => row.layout === 'feature-rail').items[0].available, true); assert.equal(first.sections.find(row => row.layout === 'feature-rail').items[0].requiresAccount, false);
  assert.equal(first.sections.find(row => row.layout === 'feature-rail').items[0].action.type, 'fm'); assert.equal(first.sections.find(row => row.layout === 'feature-rail').items[1].action.type, 'similar');
  assert.equal(first.sections[1].items[0].cover, '');
  revision++;
  const current = await f.request('wy', 'home', { refresh: true });
  assert.equal(current.sections.length, 40); assert.equal(idOf(current.sections[1].items[0]), '2');
  assert.equal(f.requests.length, 2); await f.close();
});

test('WY home declares account-only entrances from the published session and invalidates guest cache on login and logout', async t => {
  const f = fixture(request => {
    assert.equal(eapi(request).path, '/api/link/page/rcmd/resource/show');
    return response({ code: 200, data: { blocks: [{ positionCode: 'actions', creatives: [{ resources: [
      { resourceType: 'dailysongs', resourceId: 'daily', name: '每日推荐' },
      { resourceType: 'dailysongs', resourceId: 'style', name: '风格日推', action: 'orpheus://nm/play/dailySong?tab=style' },
      { resourceType: 'star', resourceId: 'heartbeat', name: '心动推荐' },
      { resourceType: 'fm', resourceId: 'fm', name: '私人漫游' },
      { resourceType: 'similarsong', resourceId: 'similar', name: '相似歌曲',
        action: 'orpheus://nm/play/similarFM?sourceId=%5B%22123%22%5D&sourceType=song' },
      { resourceType: 'playlist', resourceId: '456', name: '歌单' },
    ] }] }], hasMore: false } });
  });
  t.after(() => f.close());
  const check = (page, loggedIn) => {
    assert.deepEqual(page.sections.find(row => row.layout === 'feature-rail').items.map(row => row.title), ['每日推荐', '风格日推', '心动推荐', '私人漫游', '相似歌曲', '歌单']);
    assert.deepEqual(page.sections.find(row => row.layout === 'feature-rail').items.map(row => row.requiresAccount), [true, true, true, false, false, false]);
    assert.deepEqual(page.sections.find(row => row.layout === 'feature-rail').items.map(row => row.display), [loggedIn, loggedIn, loggedIn, true, true, true]);
    assert.deepEqual(page.sections.find(row => row.layout === 'feature-rail').items.map(row => row.available), [loggedIn, loggedIn, loggedIn, true, true, true]);
    assert.deepEqual(page.sections.find(row => row.layout === 'feature-rail').items.map(row => row.errorCode), [
      ...Array(3).fill(loggedIn ? '' : 'AUTH_REQUIRED'), '', '', '',
    ]);
    assert.deepEqual(page.sections.find(row => row.layout === 'feature-rail').items[1].action, { type: 'daily', branch: 'style' });
  };
  check(await f.request('wy', 'home'), false);
  check(await f.request('wy', 'home'), false);
  assert.equal(f.requests.length, 1);
  await f.session('wy');
  check(await f.request('wy', 'home'), true);
  assert.equal(f.requests.length, 2);
  await f.owner.sessionUpdate({ platform: 'wy', session: null });
  check(await f.request('wy', 'home'), false);
  check(await f.request('wy', 'home'), false);
  assert.equal(f.requests.length, 3);
  await assert.rejects(f.request('wy', 'daily'), { code: 'AUTH_REQUIRED' });
  assert.equal(f.requests.length, 3, 'a hidden daily entrance cannot dispatch an account read');
});

test('WY channel recommendation tabs use the same account visibility declaration as home', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    assert.equal(new URL(request.url).pathname, '/weapi/cube/render/page/protocol');
    return response({ code: 200, data: { pageProtocol: { pages: [{ id: 'recommendations',
      props: { recommendModule: [{ title: '推荐', modules: [
        { type: 'dailysongs', id: 'daily', name: '每日推荐' },
        { type: 'dailysongs', id: 'style', name: '风格日推', action: 'orpheus://songrcmd?type=style' },
        { type: 'fm', id: 'fm', name: '私人漫游' },
      ] }] } }] } } });
  });
  t.after(() => f.close());
  const params = { id: 'custom', pageCode: 'PAGE' };
  const guest = await f.request('wy', 'channel', params);
  assert.deepEqual(guest.sections[0].items.map(row => row.display), [false, false, true]);
  assert.deepEqual(guest.sections[0].items.map(row => row.requiresAccount), [true, true, false]);
  await f.session('wy');
  const member = await f.request('wy', 'channel', params);
  assert.deepEqual(member.sections[0].items.map(row => row.display), [true, true, true]);
});

test('WY nested home actions, DSL and play buttons retain exact IDs through the public similar action', async t => {
  fixedEntropy(t);
  const firstId = '900719925474099313', secondId = '900719925474099312';
  const sourceIds = `[${firstId},${secondId},${firstId}]`;
  const playlistAction = `{"type":"resource","params":{"resourceType":"song","resourceType":"playlist","query":${firstId},"query":${secondId}}}`;
  const resources = [
    { resourceType: 'playlist', resourceId: 'alias', name: 'Nested playlist', action: playlistAction },
    { resourceType: 'similarsong', resourceId: 'similar', name: 'Similar',
      action: 'orpheus://nm/play/similarFM?sourceId=' + encodeURIComponent(sourceIds) + '&sourceType=song' },
    { resourceType: 'song', resourceExtInfo: { songData: wySong('123') },
      playBtnData: `{"detailUrl":"broken","detailUrl":${playlistAction}}` },
    { resourceType: 'playlist', resourceId: 'short-alias', name: 'Short',
      action: '{"type":"resource","params":{"resourceType":"playlist","query":123}}' },
    { resourceType: 'similarsong', resourceId: 'bad-action', name: 'Unavailable',
      action: 'orpheus://nm/play/similarFM?sourceId=%5B123' },
    { resourceType: 'song', resourceExtInfo: { songData: wySong('456') }, playBtnData: '{' },
  ];
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/link/page/rcmd/resource/show') return response({ code: 200, data: { blocks: [
      { positionCode: 'actions', dslData: `{"resources":[],"resources":${JSON.stringify(resources)},"unused":1e400}` },
      { positionCode: 'bad-optional-dsl', dslData: '{"resources":[}' },
    ], hasMore: false } });
    assert.equal(path, '/weapi/v1/radio/get');
    const data = weapi(request);
    assert.equal(data.mode, 'SIMILAR'); assert.equal(data.sourceType, 'song');
    assert.equal(data.sourceIds, sourceIds); assert.equal(data.unplaySonIds, '[]');
    return response({ code: 200, data: [wySong(firstId), wySong(secondId), wySong(firstId)] });
  });
  t.after(() => f.close());
  const home = await f.request('wy', 'home');
  assert.equal(home.sections.find(row => row.layout === 'feature-rail').items.length, 6); assert.deepEqual(home.sections.map(row => row.layout), ['feature-rail']);
  assert.deepEqual(home.sections.find(row => row.layout === 'feature-rail').items[0].action, { type: 'playlist', id: secondId });
  assert.deepEqual(home.sections.find(row => row.layout === 'feature-rail').items[1].action.sourceIds, [firstId, secondId, firstId]);
  assert.deepEqual(home.sections.find(row => row.layout === 'feature-rail').items[2].playbackSource, { type: 'playlist', id: secondId });
  assert.equal(idOf(home.sections.find(row => row.layout === 'feature-rail').items[2]), '123');
  assert.equal(home.sections.find(row => row.layout === 'feature-rail').items[3].action.id, '123');
  assert.equal(home.sections.find(row => row.layout === 'feature-rail').items[4].action, null); assert.equal(home.sections.find(row => row.layout === 'feature-rail').items[4].available, false);
  assert.equal(idOf(home.sections.find(row => row.layout === 'feature-rail').items[5]), '456'); assert.equal(home.sections.find(row => row.layout === 'feature-rail').items[5].playbackSource, null);
  const similar = await f.request('wy', 'similar', home.sections.find(row => row.layout === 'feature-rail').items[1].action);
  assert.deepEqual(similar.items.map(idOf), [firstId, secondId, firstId]);
  assert.equal(f.requests.length, 2);
});

test('WY cube card actions preserve the final exact nested query and ignore optional bad action JSON', async t => {
  fixedEntropy(t);
  const id = '900719925474099313';
  const f = fixture(request => {
    assert.equal(new URL(request.url).pathname, '/weapi/cube/render/page/protocol');
    assert.equal(weapi(request).pageCode, 'PAGE');
    return response({ code: 200, data: { pageProtocol: { pages: [
      { componentName: 'Grid', children: [
        { componentName: 'Playlist', props: { id: 'alias', name: 'Exact',
          action: `{"type":"resource","params":{"resourceType":"playlist","query":900719925474099312,"query":${id}}}` } },
        { componentName: 'Playlist', props: { id: '123', name: 'Short', action: '{' } },
      ] },
    ] } } });
  });
  t.after(() => f.close());
  const page = await f.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE' });
  assert.deepEqual(page.sections[0].items.map(row => row.action), [
    { type: 'playlist', id }, { type: 'playlist', id: '123' },
  ]);
  assert.equal(f.requests.length, 1);
});

test('WY home continuation follows real cursor and carries only actual protocol progression fields', async () => {
  const f = fixture(request => {
    const { data } = eapi(request);
    const cursor = data.cursor;
    if (cursor !== 0) {
      assert.equal(data.blockCodeOrderList, 'current-order');
      assert.equal(data.loadedPositionCodes, '["a"]');
    }
    return response({ code: 200, data: { blocks: [{ positionCode: cursor === 0 ? 'a' : 'b', creatives: [] }],
      hasMore: cursor === 0, cursor: '7', blockCodeOrderList: 'current-order' } });
  });
  const first = await f.request('wy', 'home');
  assert.equal(first.more, true); assert.equal(first.continuation.cursor, '7');
  const second = await f.request('wy', 'home', { continuation: first.continuation });
  assert.equal(second.more, false); assert.equal(f.requests.length, 2); await f.close();
  const stalled = fixture(() => response({ code: 200, data: { blocks: [], hasMore: true, cursor: '0' } }));
  const repeated = await stalled.request('wy', 'home');
  assert.equal(repeated.more, true); assert.equal(repeated.continuation.cursor, '0');
  assert.deepEqual(repeated.sections, []); await stalled.close();
});

test('WY home and playlist channel continuations remain wire JSON without optional block order', async t => {
  fixedEntropy(t);
  for (const operation of ['home', 'channel']) {
    const f = fixture(request => {
      const data = operation === 'home' ? eapi(request).data : weapi(request);
      const next = String(data.cursor) === '7';
      assert.equal(data.blockCodeOrderList, undefined);
      return response({ code: 200, data: { blocks: [{ positionCode: 'playlists', creatives: [
        { resources: [{ resourceType: 'playlist', resourceId: '1', name: 'Current playlist' }] },
      ] }], hasMore: !next, cursor: '7' } });
    });
    t.after(() => f.close());
    const params = operation === 'channel' ? { id: 'playlist' } : {};
    const page = await f.request('wy', operation, params);
    assert.equal(page.continuation.blockCodeOrderList, '');
    assert.deepEqual(JSON.parse(stringifyJSONValue(page)), page);
    const next = await f.request('wy', operation, { ...params, continuation: page.continuation });
    assert.equal(next.more, false);
    assert.deepEqual(JSON.parse(stringifyJSONValue(next)), next);
    assert.equal(f.requests.length, 2);
  }
});

test('WY malformed optional URI actions leave actual resource locators and healthy sibling actions usable', async t => {
  const f = fixture(() => response({ code: 200, data: { blocks: [
    { positionCode: 'resources', dslData: { title: 'Current', action: 'orpheus://playlist/%ZZ', resources: [
      { resourceType: 'playlist', resourceId: '1', action: 'orpheus://playlist/%ZZ' },
      { resourceType: 'playlist', resourceId: '2', action: 'orpheus://playlist/actual%20id' },
      { resourceType: 'playlist', action: 'orpheus://playlist/%ZZ' },
      { resourceType: 'song', resourceExtInfo: { songData: wySong('3', { ar: [null, { name: 'Singer' }] }) },
        playBtnData: { detailUrl: 'orpheus://playlist/%ZZ' } },
    ] } },
  ], hasMore: false } }));
  t.after(() => f.close());
  const result = await f.request('wy', 'home');
  assert.deepEqual(result.sections[0].items.map(row => row.action), [
    { type: 'playlist', id: '1' }, { type: 'playlist', id: 'actual id' }, null,
  ]);
  assert.equal(result.sections[0].action, null);
  assert.equal(idOf(result.sections[1].items[0]), '3');
  assert.equal(result.sections[1].items[0].song.artist, 'Singer');
  for (const data of [{ blocks: 'broken' }, { blocks: [{ creatives: [{ resources: [
    { resourceType: 'song', resourceExtInfo: { songData: {} } },
  ] }] }] }, { blocks: [{ creatives: [{ resources: [
    { resourceType: 'playlist', action: 'orpheus://playlist/%ZZ' },
  ] }] }] }]) {
    const invalid = fixture(() => response({ code: 200, data })); t.after(() => invalid.close());
    const partial = await invalid.request('wy', 'home');
    assert.equal(partial.sections.length, Array.isArray(data.blocks) ? 1 : 0);
    if (partial.sections.length) assert.equal(partial.sections[0].items[0].action, null);
  }
});

test('WY cube retains cards with unavailable actions alongside healthy cards or on their own', async t => {
  for (const healthy of [true, false]) {
    const f = fixture(() => response({ code: 200, data: { pageProtocol: { pages: [
      { componentName: 'Grid', children: [
        { componentName: 'Playlist', props: { action: 'orpheus://playlist/%ZZ' } },
        ...(healthy ? [{ componentName: 'Playlist', props: { id: '1', name: 'Current', action: 'orpheus://playlist/%ZZ' } }] : []),
      ] },
    ] } } }));
    t.after(() => f.close());
    const work = f.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE' });
    assert.deepEqual((await work).sections[0].items.map(row => row.action),
      [null, ...(healthy ? [{ type: 'playlist', id: '1' }] : [])]);
    assert.equal(f.requests.length, 1);
  }
});

test('WY directory and cube channel dispatch real pageCode without a directory verification reread', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/link/position/show/resource') return response({ code: 200, data: { channels: [
      { code: 'feature', name: 'Featured' }, { code: 'custom', name: 'Current genre',
        url: 'orpheus://rnpage?component=cube-renderer-rn&page=SYNTHETIC_PAGE' },
    ] } });
    assert.equal(path, '/weapi/cube/render/page/protocol');
    assert.equal(weapi(request).pageCode, 'SYNTHETIC_PAGE');
    return response({ code: 200, data: { pageProtocol: { title: 'Genre', pages: [
      { componentName: 'Grid', children: [
        { componentName: 'Song', props: { songInfo: wySong('123') } },
        { componentName: 'Playlist', props: { id: '456', name: 'Known playlist' } },
      ] },
    ] } } });
  });
  const directory = await f.request('wy', 'channels');
  const custom = directory.items.find(row => row.id === 'custom');
  assert.equal(custom.pageCode, 'SYNTHETIC_PAGE'); assert.equal(custom.channelKind, 'cube');
  const result = await f.request('wy', 'channel', { id: custom.id, pageCode: custom.pageCode });
  assert.equal(idOf(result.sections[0].items[0]), '123');
  assert.equal(result.sections[1].items[0].action.id, '456');
  assert.equal(f.requests.length, 2); await f.close();
});

test('WY cube algorithm feed uses actual rule/tags, consumes empty visible pages and keeps continuation', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/cube/render/page/protocol')) return response({ code: 200, data: { pageProtocol: { pages: [
      { componentName: 'Page', id: 'feed', props: { songList: { ruleId: 'rule', title: 'Current', pointId: [{ id: '7' }] } } },
    ] } } });
    if (path.endsWith('/tag/tab/tags')) return response({ code: 200, data: { algTagInfos: [
      { algTagId: 'tag', algTagName: 'Tag' },
    ] } });
    const { data } = eapi(request);
    assert.equal(path, '/eapi/tag/tab/alg/recommends'); assert.equal(data.ruleId, 'rule');
    assert.deepEqual(JSON.parse(data.extMap), { pointIds: '7' });
    return response({ code: 200, data: { songs: data.offset === 0 ? [] : [wySong('123')], hasMore: data.offset === 0 } });
  });
  const first = await f.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE', tag: 'tag' });
  assert.equal(first.more, true); assert.equal(first.continuation.offset, 30);
  assert.equal(first.sections[0].errorCode, '');
  assert.deepEqual(first.sections[0].tags.map(row => row.name), ['全部歌曲', 'Tag']);
  assert.equal(first.sections[0].selectedTag, 'tag');
  const next = await f.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE', continuation: first.continuation });
  assert.equal(next.more, false); assert.equal(idOf(next.sections[0].items[0]), '123');
  assert.equal(f.requests.length, 6); await f.close();
});

// HEAD MusicNativePublicBrowseDiscoveryTests: initial feed errors belong to
// their section; optional tags do not block songs, auth and continuation fail.
const partialCube = { code: 200, data: { pageProtocol: { pages: [
  { componentName: 'Grid', id: 'cards', children: [
    { componentName: 'Playlist', props: { id: '41', name: 'Retained card' } },
  ] },
  { componentName: 'Root', id: 'live', props: { songList: { ruleId: 3, title: 'Feed', pointId: [{ id: 2 }] } } },
] } } };

test('WY initial cube feed failures remain section-local with explicit errors, filters and unrelated content intact', async t => {
  for (const [failure, code] of [
    [{ code: 500 }, 'UPSTREAM_ERROR'], ['broken-json', 'INVALID_RESPONSE'],
    [null, 'NETWORK_ERROR'],
  ]) await t.test(code, async () => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/cube/render/page/protocol')) return response(partialCube);
      if (path.endsWith('/tag/tab/tags')) return response({ code: 200, data: { algTagInfos: null } });
      const { data } = eapi(request);
      assert.equal(data.ruleId, '3'); assert.equal(data.algTagIds, '');
      assert.deepEqual(JSON.parse(data.extMap), { pointIds: '2' });
      if (failure === null) throw Error('synthetic feed outage');
      return response(failure);
    });
    const page = await f.request('wy', 'channel', { id: 'synthetic', pageCode: 'PAGE' });
    assert.equal(page.sections.length, 2); assert.equal(page.sections[0].items[0].action.id, '41');
    const feed = page.sections[1];
    assert.deepEqual(feed.items, []); assert.equal(feed.errorCode, code);
    assert.deepEqual(feed.tags, [{ id: '', name: '全部歌曲', group: '', selected: false }]);
    assert.equal(feed.selectedTag, ''); assert.equal(page.more, false); assert.equal(page.continuation, null);
    assert.equal(f.requests.length, 3); await f.close();
  });
});

test('WY optional algorithm-tag failures still read songs; authentication and failed continuation never become empty sections', async t => {
  for (const stage of ['tags-optional', 'tags-auth', 'feed-auth', 'continuation']) await t.test(stage, async () => {
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path.endsWith('/cube/render/page/protocol')) return response(partialCube);
      if (path.endsWith('/tag/tab/tags'))
        return response(stage === 'tags-auth' ? { code: 301 } : { code: 500 });
      return response(stage === 'tags-optional' ? { code: 200, songs: [wySong('6')], hasMore: false } :
        { code: stage === 'feed-auth' ? 301 : 500 });
    });
    const work = f.request('wy', 'channel', { id: 'synthetic', pageCode: 'PAGE',
      ...(stage === 'continuation' ? { continuation: { offset: 30 } } : {}) });
    if (stage === 'tags-optional') {
      const page = await work, feed = page.sections[1];
      assert.equal(idOf(feed.items[0]), '6'); assert.equal(feed.errorCode, '');
      assert.deepEqual(feed.tags.map(row => row.name), ['全部歌曲']);
    } else await assert.rejects(work, { code: stage === 'continuation' ? 'UPSTREAM_ERROR' : 'AUTH_REQUIRED' });
    assert.equal(f.requests.length, stage === 'tags-auth' ? 2 : 3); await f.close();
  });
  const held = gate();
  const f = fixture(async request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/cube/render/page/protocol')) return response(partialCube);
    if (path.endsWith('/tag/tab/tags')) return response({ code: 200, data: { algTagInfos: null } });
    await held.wait(); return response({ code: 500 });
  });
  t.after(async () => { held.release(); await f.close(); });
  const work = f.request('wy', 'channel', { id: 'synthetic', pageCode: 'PAGE' }); work.catch(() => {});
  await held.entered;
  const close = f.close(); assert.equal(f.requests.at(-1).signal.aborted, true);
  held.release(); await assert.rejects(work, { code: 'CLOSED' }); await close;
});

test('WY rankings and chart recipes preserve actual preview songs, nullable optional ranks and canonical IDs', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/toplist/detail/v2')) return response({ code: 200, data: [{ categoryCode: 'current', name: 'Rankings', list: [
      { targetType: 'PLAYLIST', id: '1', name: 'Current list',
        trackRankList: [{ trackId: '123', songName: 'Song', artistName: 'Artist' }] },
      { toplistCode: 'DIGITAL_ALBUM', name: 'Sales' },
    ] }] });
    if (path.includes('/feealbum/songsaleboard/')) {
      assert.equal(path, '/weapi/feealbum/songsaleboard/year/type/');
      assert.equal(weapi(request).year, 2026);
      return response({ code: 200, products: [{ albumId: 'album', albumName: 'Current', saleNum: 1234 }] });
    }
    if (path.endsWith('/charts/user/rcmd/song/detail')) return response({ code: 200,
      data: { charts: [{ songData: wySong('789') }] } });
    assert.fail(path);
  });
  const ranks = await f.request('wy', 'channel', { id: 'chart' });
  assert.equal(ranks.sections[0].items[0].preview[0].rank, null);
  assert.equal(idOf(ranks.sections[0].items[0].preview[0].song), '123');
  const albums = await f.request('wy', 'charts', { kind: 'albums', period: 'yearly', year: 2026 });
  assert.equal(albums.sections[0].items[0].id, 'album');
  const songs = await f.request('wy', 'charts', { kind: 'user-recommendations' });
  assert.equal(idOf(songs.sections[0].items[0]), '789');
  assert.equal(f.requests.length, 3); await f.close();
});

test('WY artist list, new songs and logged-in artist activity consume their own real fields', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path.endsWith('/artist/list')) return response({ code: 200, artists: [{ id: 'artist', name: 'Current artist' }], more: false });
    if (path.endsWith('/new/songs')) return response({ code: 200, data: [wySong('123')] });
    if (path.endsWith('/new/works/song-mv/list/v2')) return response({ code: 200, data: { title: 'New works', hasMore: true,
      newWorks: [{ publishTime: '100', info: { blockType: 'song', songLists: [wySong('456', { al: {} })],
        blockTitle: { artistName: 'Current artist' } } }] } });
    assert.fail(path);
  });
  const guest = await f.request('wy', 'artistActivity');
  assert.equal(guest.sections.length, 0); assert.equal(f.requests.length, 0);
  const artists = await f.request('wy', 'channel', { id: 'artist', tag: '7:1:65' });
  assert.equal(artists.sections[0].items[0].id, 'artist');
  assert.equal(artists.selectedTag, '7:1:65');
  const news = await f.request('wy', 'channel', { id: 'newsong' });
  assert.equal(idOf(news.sections[0].items[0]), '123');
  await f.session('wy');
  const works = await f.request('wy', 'artistActivity');
  assert.equal(idOf(works.sections[0].items[0].song), '456');
  assert.equal(works.sections[0].items[0].title, 'Synthetic (Live)');
  assert.equal(works.sections[0].items[0].subtitle, 'Current artist');
  assert.deepEqual(works.continuation, { before: '100' });
  const repeated = await f.request('wy', 'artistActivity', { before: '100' });
  assert.equal(idOf(repeated.sections[0].items[0].song), '456');
  assert.deepEqual(repeated.continuation, { before: '100' });
  await f.close();
});

test('WY response status follows the selected root or data payload and preserves nested refusals without payload', async t => {
  const protocol = { pages: [{ componentName: 'Grid', children: [
    { componentName: 'Playlist', props: { id: '123', name: 'Current' } },
  ] }] };
  const recipes = [
    { name: 'artists', operation: 'channel', params: { id: 'artist' }, suffix: '/artist/list',
      payload: { artists: [{ id: '123', name: 'Current' }] },
      check: value => assert.equal(value.sections[0].items[0].id, '123') },
    { name: 'cube', operation: 'channel', params: { id: 'current', pageCode: 'PAGE' }, suffix: '/cube/render/page/protocol',
      payload: { pageProtocol: protocol }, check: value => assert.equal(value.sections[0].items[0].id, '123') },
    { name: 'tags', operation: 'channel', params: { id: 'current', pageCode: 'PAGE' }, suffix: '/tag/tab/tags',
      payload: { algTagInfos: [{ algTagId: '123', algTagName: 'Current' }] },
      check: value => assert.equal(value.sections.at(-1).tags.at(-1).id, '123') },
    { name: 'vip', operation: 'channel', params: { id: 'vip' }, suffix: '/viptab/recommend/song/list',
      payload: { songInfoList: [wySong('123')] }, check: value => assert.equal(idOf(value.sections[0].items[0]), '123') },
    { name: 'daily', operation: 'daily', params: {}, suffix: '/discovery/recommend/songs',
      payload: { recommend: [wySong('123')] }, nested: { dailySongs: [wySong('123')] },
      check: value => assert.equal(idOf(value.items[0]), '123') },
  ];
  for (const recipe of recipes) for (const [placement, code] of [
    ['root', 'unconsumed-extension'], ['root', 403], ['nested', 301], ['missing', 301],
  ]) await t.test(recipe.name + ':' + placement + ':' + code, async t => {
    const server = recipe.name === 'daily' ? xeapi() : null;
    const raw = { code: 200,
      ...(placement === 'missing' ? {} : recipe.payload),
      data: { code, ...(placement === 'nested' ? recipe.nested ?? recipe.payload : {}) } };
    const f = fixture(request => {
      const path = new URL(request.url).pathname;
      if (path === '/api/bsr/sk/get') return server.key(request);
      if (path.endsWith(recipe.suffix)) {
        if (server) { server.open(request); return server.reply(raw); }
        return response(raw);
      }
      if (recipe.name === 'tags' && path.endsWith('/cube/render/page/protocol')) return response({ code: 200,
        data: { pageProtocol: { pages: [{ componentName: 'Root', props: { songList: { ruleId: 'rule' } } }] } } });
      if (recipe.name === 'tags' && path.endsWith('/tag/tab/alg/recommends')) return response({ code: 200, data: { songs: [] } });
      if (recipe.name === 'vip' && path.endsWith('/toplist/detail/v2')) return response({ code: 500 });
      assert.fail(path);
    }); seedClients(f); t.after(() => f.close());
    if (server) await f.session('wy');
    const work = f.request('wy', recipe.operation, recipe.params);
    if (placement === 'root') recipe.check(await work);
    else await assert.rejects(work, { code: 'AUTH_REQUIRED' });
    assert.equal(f.requests.filter(request => new URL(request.url).pathname.endsWith(recipe.suffix)).length, 1);
  });
});

test('WY search discovery uses actual hot-tab continuation and isolates optional component failures', async () => {
  const f = fixture(request => {
    const { path, data } = eapi(request);
    if (path.endsWith('/chart/list')) return response({ code: 200, data: {
      tabInfo: { list: [{ id: 'HOT_SEARCH_SONG#@#', needPlay: 1 }] }, items: {} } });
    if (path.endsWith('/chart/detail')) {
      assert.equal(data.id, 'HOT_SEARCH_SONG#@#');
      return response({ code: 200, data: { itemList: [{ searchWord: 'Current hot', score: 99 }] } });
    }
    if (path === '/api/toplist') return response({ code: 200, list: [{ id: '123', name: 'Chart' }] });
    return response('synthetic default outage', { status: 503 });
  });
  const value = await f.request('wy', 'searchDiscovery');
  assert.equal(value.hotCanPlay, true); assert.equal(value.hot[0].keyword, 'Current hot');
  assert.equal(value.charts[0].id, '123'); assert.deepEqual(value.suggestions, ['Current hot']);
  assert.deepEqual(value.failures, [{ stage: 'default', chartId: null, code: 'UPSTREAM_HTTP' }]);
  assert.equal(f.requests.length, 4); await f.close();
});

test('WY FM, rule FM and similar actions use continuous protocol identity and preserve dynamic members', async t => {
  fixedEntropy(t);
  const f = fixture(request => {
    const url = new URL(request.url), value = url.pathname.startsWith('/weapi/') ? weapi(request) : eapi(request).data;
    if (url.pathname.endsWith('/tag/tab/fm/cover')) {
      assert.equal(value.ruleId, 'rule'); return response({ code: 200, data: { cover: 'https://images.invalid/fm' } });
    }
    if (url.pathname.endsWith('/tag/tab/fm/recommends')) return response({ code: 200, data: [wySong('456')] });
    assert.equal(url.pathname.endsWith('/v1/radio/get'), true);
    assert.equal(value.mode, url.pathname.startsWith('/weapi/') ? 'SIMILAR' : 'EXPLORE');
    return response({ code: 200, data: [wySong(String(f.requests.length))] });
  });
  assert.equal((await f.request('wy', 'fm', { mode: 'explore' })).items.length, 1);
  assert.equal((await f.request('wy', 'fm', { ruleId: 'rule', tabTagId: 'tag', preview: true })).preview, true);
  assert.equal(idOf((await f.request('wy', 'fm', { ruleId: 'rule', tabTagId: 'tag' })).items[0]), '456');
  assert.equal((await f.request('wy', 'similar', { sourceIds: ['123'] })).items.length, 1);
  const device = /deviceId=([^;]+)/.exec(f.requests[0].headers.Cookie)[1];
  assert.ok(f.requests.every(row => row.headers.Cookie.includes('deviceId=' + device)));
  assert.equal(f.requests.length, 4); await f.close();
});

test('WY consumed radio arrays and heartbeat counts fail as requests before dependency dispatch', async t => {
  fixedEntropy(t);
  const invalid = fixture(() => assert.fail('unusable request material must not dispatch'));
    await invalid.owner.ready;
    const storageCounts = [invalid.reads.length, invalid.writes.length];
  t.after(() => invalid.close());
  for (const operation of ['fm', 'similar']) for (const unplaySongIds of [null, {}, '123', [null], [{}]]) {
    await assert.rejects(invalid.request('wy', operation, { sourceIds: [123], unplaySongIds }),
      { code: 'INVALID_REQUEST' });
  }
  await invalid.session('wy');
  for (const count of ['not-a-number', '1e400', {}, { toString: null }]) {
    await assert.rejects(invalid.request('wy', 'heartbeat', { songId: '123',
      continuation: { startMusicId: '123', count } }), { code: 'INVALID_REQUEST' });
  }
  assert.equal(invalid.requests.length, 0); assert.equal(invalid.reads.length, storageCounts[0]); assert.equal(invalid.writes.length, storageCounts[1]);

  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/tag/tab/fm/recommends') {
      assert.equal(eapi(request).data.ruleId, 'rule');
      return response({ code: 200, data: [wySong('123')] });
    }
    assert.equal(path, '/eapi/v1/radio/get');
    const data = eapi(request).data;
    assert.equal(data.unplaySongIds, f.requests.length === 1 ? '[]' : '[123,900719925474099313]');
    return response({ code: 200, data: [wySong('123')] });
  });
  t.after(() => f.close());
  assert.equal(idOf((await f.request('wy', 'fm')).items[0]), '123');
  assert.equal(idOf((await f.request('wy', 'fm', { unplaySongIds: [123, '900719925474099313'] })).items[0]), '123');
  assert.equal(idOf((await f.request('wy', 'fm', { ruleId: 'rule', unplaySongIds: { toString: null } })).items[0]), '123');
  assert.equal(f.requests.length, 3);

  const counts = [];
  const continued = fixture(request => {
    const { path, data } = eapi(request);
    assert.equal(path, '/api/playmode/intelligence/list');
    assert.equal(data.playlistId, '10'); assert.equal(data.startMusicId, '123');
    counts.push(data.count);
    return response({ code: 200, data: [wySong('123')] });
  });
  t.after(() => continued.close()); await continued.session('wy');
  for (const count of ['7', undefined]) {
    const result = await continued.request('wy', 'heartbeat', {
      continuation: { playlistId: '10', startMusicId: '123', count },
    });
    assert.equal(result.continuation.count, count === undefined ? 0 : 7);
  }
  assert.deepEqual(counts, [7, 0]); assert.equal(continued.requests.length, 2);
});

test('WY channel offsets fail at consumption before artist or cube feed dependencies, while unused offsets remain ignored', async t => {
  fixedEntropy(t);
  for (const offset of ['not-a-number', '1e400', {}, { toString: null }]) {
    const artists = fixture(() => assert.fail('bad artist offset must not dispatch'));
    await assert.rejects(artists.request('wy', 'channel', { id: 'artist', tag: '7:1:65',
      continuation: { offset } }), { code: 'INVALID_REQUEST' });
    assert.equal(artists.requests.length, 0); await artists.close();

    const cube = fixture(request => {
      assert.equal(new URL(request.url).pathname, '/weapi/cube/render/page/protocol');
      return response({ code: 200, data: { pageProtocol: { pages: [
        { componentName: 'Grid', children: [{ componentName: 'Playlist', props: { id: '41' } }] },
        { componentName: 'Root', props: { songList: { ruleId: 3, pointId: [{ id: 2 }] } } },
      ] } } });
    });
    await assert.rejects(cube.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE',
      continuation: { offset } }), { code: 'INVALID_REQUEST' });
    assert.equal(cube.requests.length, 1, 'only the real page needed to discover the feed recipe is read');
    await cube.close();
  }
  for (const tag of [{}, { toString: null }]) {
    const artists = fixture(() => assert.fail('bad numeric artist selectors must not dispatch'));
    await assert.rejects(artists.request('wy', 'channel', { id: 'artist', tag }), { code: 'INVALID_REQUEST' });
    assert.equal(artists.requests.length, 0); await artists.close();
  }
  const artists = fixture(request => {
    assert.equal(new URL(request.url).pathname, '/weapi/v1/artist/list');
    const data = weapi(request);
    assert.equal(data.offset, 40); assert.equal(data.area, 7); assert.equal(data.type, 1); assert.equal(data.initial, 65);
    return response({ code: 200, artists: [{ id: 'actual', name: 'Artist' }], more: false });
  });
  t.after(() => artists.close());
  const selected = await artists.request('wy', 'channel', { id: 'artist', tag: '7:1:65', continuation: { offset: '40' } });
  assert.equal(selected.sections[0].items[0].id, 'actual'); assert.equal(artists.requests.length, 1);

  const display = fixture(() => response({ code: 200, data: { pageProtocol: { pages: [
    { componentName: 'Grid', children: [{ componentName: 'Playlist', props: { id: '123', name: 'Visible' } }] },
  ] } } }));
  t.after(() => display.close());
  const page = await display.request('wy', 'channel', { id: 'custom', pageCode: 'PAGE',
    continuation: { offset: { toString: null } } });
  assert.equal(page.sections[0].items[0].action.id, '123'); assert.equal(page.feed, undefined);
  assert.equal(display.requests.length, 1);
});

test('WY heartbeat uses one membership read and continues from actual seed/count without an old snapshot check', async t => {
  fixedEntropy(t);
  const counts = [];
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/eapi/user/playlist') return response({ code: 200, more: false,
      playlist: [{ id: '10', name: 'Favorites', specialType: 5, creator: { userId: 42 } }] });
    if (path === '/eapi/v6/playlist/detail') return response({ code: 200, playlist: {
      id: '10', name: 'Current', trackIds: [{ id: '1' }, { id: '2' }], tracks: [wySong('1'), wySong('2')] } });
    if (path.endsWith('/playmode/intelligence/list')) {
      assert.equal(path, '/eapi/playmode/intelligence/list');
      const { data } = eapi(request); counts.push(data.count);
      assert.equal(data.startMusicId, '1'); assert.equal(data.playlistId, '10');
      return response({ code: 200, data: [{ songInfo: wySong('1'), alg: 'current' }, { songInfo: wySong('3') }] });
    }
    assert.fail(path);
  }); await f.session('wy');
  const first = await f.request('wy', 'heartbeat');
  assert.deepEqual(first.items.map(idOf), ['1', '3']); assert.equal(first.continuation.count, 2);
  const next = await f.request('wy', 'heartbeat', { continuation: first.continuation });
  assert.equal(next.continuation.count, 2); assert.deepEqual(counts, [2, 2]);
  assert.equal(f.requests.length, 4); await f.close();
});

test('WY daily and style settings retain real XEAPI and dispatch receipts, including unknown without automatic retry', async () => {
  const server = xeapi(); let saves = 0;
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/api/bsr/sk/get') return server.key(request);
    const params = server.open(request);
    if (path.endsWith('/daily/song/config/get')) return server.reply({ code: 200, data: {
      categorys: [{ categoryId: '1', categoryName: 'Style', tagVOList: [{ tagId: '2', tagName: 'Current' }] }] } });
    if (path.endsWith('/daily/song/tag/save')) {
      assert.deepEqual(JSON.parse(params.get('tags')), { categoryId: 1, tagIds: [2] });
      saves++; throw Error('synthetic post-dispatch network failure');
    }
    return server.reply({ code: 200, data: { dailySongs: [wySong('123')], tags: {
      categoryId: '1', tagVOList: [{ tagId: '2', tagName: 'Current' }] } } });
  }); seedClients(f); await f.session('wy');
  const settings = await f.request('wy', 'dailyStyle.get');
  assert.equal(settings.categories[0].tags[0].id, '2');
  const daily = await f.request('wy', 'daily');
  assert.equal(idOf(daily.items[0]), '123');
  const result = await f.request('wy', 'dailyStyle.save', { categoryId: '1', tagIds: ['2'] });
  assert.equal(result.status, 'unknown'); assert.equal(result.errorCode, 'NETWORK_ERROR');
  assert.equal(result.selectionMatches, true); assert.equal(saves, 1);
  await f.close();
});

test('daily style writes retain their receipt after account retirement without issuing or caching an old-account readback', async t => {
  for (const transition of ['logout', 'replacement']) for (const status of ['confirmed', 'rejected', 'unknown'])
    await t.test(transition + ':' + status, async t => {
      const held = gate(), server = xeapi();
      let saves = 0, reads = 0;
      const f = fixture(async request => {
        const path = new URL(request.url).pathname;
        if (path === '/api/bsr/sk/get') return server.key(request);
        server.open(request);
        if (path.endsWith('/daily/song/tag/save')) {
          saves++; await held.wait();
          if (status === 'unknown') throw Error('synthetic lost write receipt');
          return server.reply({ code: status === 'rejected' ? 403 : 200 });
        }
        assert.ok(path.endsWith('/category/daily/song/list'));
        reads++;
        return server.reply({ code: 200, data: { dailySongs: [wySong('456')], tags: {
          categoryId: '1', tagVOList: [{ tagId: '2', tagName: 'Current' }] } } });
      });
      t.after(async () => { held.release(); await f.close(); });
      seedClients(f); await f.session('wy');
      const pending = f.request('wy', 'dailyStyle.save', { categoryId: '1', tagIds: ['2'] });
      await held.entered;
      if (transition === 'logout') await f.owner.sessionUpdate({ platform: 'wy', session: null });
      else await f.session('wy', '84');
      held.release();
      const result = await pending;
      assert.equal(result.status, status);
      assert.equal(result.errorCode, { confirmed: null, rejected: 'FORBIDDEN', unknown: 'NETWORK_ERROR' }[status]);
      assert.equal(result.readback, null); assert.equal(result.selectionMatches, null);
      assert.equal(saves, 1); assert.equal(reads, 0);
      if (transition === 'logout') await assert.rejects(f.request('wy', 'daily', { branch: 'style' }), { code: 'AUTH_REQUIRED' });
      else {
        assert.equal(idOf((await f.request('wy', 'daily', { branch: 'style' })).items[0]), '456');
        await f.request('wy', 'daily', { branch: 'style' });
        assert.equal(reads, 1, 'only the replacement account can populate its current daily cache');
      }
    });
});

test('settled daily style receipts survive retirement during optional readback without old metadata or projection', async t => {
  const cases = ['logout', 'replacement'].flatMap(transition =>
    ['confirmed', 'rejected', 'unknown'].map(status => [transition, status]));
  cases.push(['close', 'confirmed']);
  for (const [transition, status] of cases) await t.test(transition + ':' + status, async t => {
    fixedEntropy(t);
    const held = gate(), server = xeapi();
    let saves = 0, reads = 0, details = 0;
    const f = fixture(async request => {
      const path = new URL(request.url).pathname;
      if (path === '/api/bsr/sk/get') return server.key(request);
      if (path === '/weapi/v3/song/detail') {
        details++;
        assert.deepEqual(JSON.parse(weapi(request).c), [{ id: 123 }]);
        return response({ code: 200, songs: [wySong('123')] });
      }
      server.open(request);
      if (path.endsWith('/daily/song/tag/save')) {
        saves++;
        if (status === 'unknown') throw Error('synthetic lost write receipt');
        return server.reply({ code: status === 'rejected' ? 403 : 200 });
      }
      assert.ok(path.endsWith('/category/daily/song/list'));
      reads++;
      if (reads === 1) {
        assert.equal(saves, 1);
        await held.wait();
      }
      return server.reply({ code: 200, data: { dailySongs: reads === 1 ? [{ id: 123 }] : [wySong('456')], tags: {
        categoryId: '1', tagVOList: [{ tagId: '2', tagName: 'Current' }] } } });
    });
    t.after(async () => { held.release(); await f.close(); });
    seedClients(f); await f.session('wy');
    const pending = f.request('wy', 'dailyStyle.save', { categoryId: '1', tagIds: ['2'] });
    await held.entered;
    const readback = f.requests.at(-1);
    let retirement;
    if (transition === 'logout') retirement = f.owner.sessionUpdate({ platform: 'wy', session: null });
    else if (transition === 'replacement') retirement = f.session('wy', '84');
    else retirement = f.close();
    let settled = false;
    retirement.then(() => { settled = true; });
    assert.equal(readback.signal.aborted, true);
    await Promise.resolve();
    assert.equal(settled, false, 'retirement must wait for the already entered readback transport');
    held.release();
    assert.deepEqual(await pending, { status, readback: null, selectionMatches: null,
      errorCode: { confirmed: null, rejected: 'FORBIDDEN', unknown: 'NETWORK_ERROR' }[status] });
    await retirement;
    assert.equal(saves, 1); assert.equal(reads, 1); assert.equal(details, 0);
    if (transition === 'logout')
      await assert.rejects(f.request('wy', 'daily', { branch: 'style' }), { code: 'AUTH_REQUIRED' });
    else if (transition !== 'close') {
      assert.equal(idOf((await f.request('wy', 'daily', { branch: 'style' })).items[0]), '456');
      await f.request('wy', 'daily', { branch: 'style' });
      assert.equal(reads, 2, 'only a current read may populate the daily cache');
      assert.equal(details, 0);
    }
  });
});

test('an unusable optional daily style projection cannot change a confirmed write receipt or expose the credential', async t => {
  const server = xeapi();
  let saves = 0, reads = 0;
  const f = fixture(request => {
    const path = new URL(request.url).pathname;
    if (path === '/api/bsr/sk/get') return server.key(request);
    server.open(request);
    if (path.endsWith('/daily/song/tag/save')) { saves++; return server.reply({ code: 200 }); }
    assert.ok(path.endsWith('/category/daily/song/list'));
    reads++;
    return server.reply({ code: 200, data: { dailySongs: [wySong('123', { name: 'synthetic-music-u' })], tags: {
      categoryId: '1', tagVOList: [{ tagId: '2', tagName: 'Current' }] } } });
  });
  t.after(() => f.close()); seedClients(f); await f.session('wy');
  assert.deepEqual(await f.request('wy', 'dailyStyle.save', { categoryId: '1', tagIds: ['2'] }),
    { status: 'confirmed', readback: null, selectionMatches: null, errorCode: 'INVALID_RESPONSE' });
  assert.equal(saves, 1); assert.equal(reads, 1);
});

test('WY queue recommendations retain semantic algorithm/source maps and actual continuation', async () => {
  const f = fixture(request => {
    const { data } = eapi(request);
    assert.equal(data.currentPlaySongId, '123'); assert.equal(data.offset, '7');
    return response({ code: 200, data: { songList: [wySong('456')], offset: 11, hasMore: true,
      algMap: '{"456":"algorithm"}', sourceMap: { 456: 'feedback-source' } } });
  }); await f.session('wy');
  const value = await f.request('wy', 'queueRecommendations', { seedSongId: '123', offset: 7 });
  assert.equal(value.offset, 11); assert.equal(value.hasMore, true);
  assert.equal(value.recommendations[0].alg, 'algorithm');
  assert.equal(value.recommendations[0].feedbackSource, 'feedback-source');
  await f.close();
});

test('KG home, directory and rank route preserve known previews and actual channel recipes', async () => {
  const f = fixture(request => {
    const value = kg(request);
    if (value.path === '/v2/special_recommend') return response({ status: 1, data: {
      has_next: '0', special_list: [{ global_collection_id: 'playlist', name: 'Current list', songs: [kgSong()] }] } });
    if (value.path === '/musicadservice/container/v1/newsong_publish') return response({ status: 1, total: 1, data: [kgSong()] });
    if (value.path === '/ocean/v6/rank/list') return response({ status: 1, data: { info: [
      { rankid: 7, rank_cid: 8, rankname: 'Current rank', songinfo: [kgSong()] },
    ] } });
    if (value.path === '/pubsongs/v1/get_tags_by_type') return response({ status: 1,
      data: [{ tag_id: 9, tag_name: 'Genre', son: [{ tag_id: 10, tag_name: 'Tag' }] }] });
    assert.equal(value.path, '/openapi/kmr/v2/rank/audio'); assert.equal(request.headers['kg-tid'], '369');
    assert.equal(value.body.rank_id, 7); assert.equal(value.body.rank_cid, 8);
    return response({ status: 1, data: { total: 1, songlist: [kgSong()] } });
  }); seedClients(f);
  const home = await f.request('kg', 'home');
  assert.equal(home.sections.length, 3); assert.equal(idOf(home.sections[0].items[0].preview[0].song), kgHash);
  assert.equal(idOf(home.sections[2].items[0].preview[0].song), kgHash);
  assert.ok((await f.request('kg', 'channels')).items.some(row => row.id === 'category-9'));
  const rank = await f.request('kg', 'channel', { id: 'rank-7', pageCode: '8' });
  assert.equal(idOf(rank.sections[0].items[0]), kgHash); assert.equal(rank.more, false);
  assert.equal(f.requests.length, 5); await f.close();
});

test('KG home consumes flat and nested artist records without requiring an unused base object', async t => {
  for (const [name, authors] of [
    ['flat', [{ author_name: 'A' }, { author_name: 'B' }]],
    ['nested', [{ base: { author_name: 'A' } }, { base: { author_name: 'B' } }]],
    ['unused-base', [{ author_name: 'A', base: false }, { author_name: 'B', base: { unused: [] } }]],
  ]) {
    await t.test(name, async () => {
      const f = fixture(request => {
        const value = kg(request);
        if (value.path === '/v2/special_recommend') return response({ status: 1, data: {
          has_next: 0, special_list: [{ global_collection_id: 'playlist', specialname: 'Current list' }] } });
        if (value.path === '/musicadservice/container/v1/newsong_publish') return response({ status: 1, total: 1, data: [{
          hash: kgHash, songname: 'Synthetic (Live)', filename: 'A - Synthetic (Live)', authors, timelength: 240000,
        }] });
        assert.equal(value.path, '/ocean/v6/rank/list');
        return response({ status: 1, data: { info: [] } });
      });
      seedClients(f);
      try {
        const home = await f.request('kg', 'home');
        const song = home.sections[1].items[0].song;
        assert.equal(idOf(song), kgHash);
        assert.equal(song.title, 'Synthetic (Live)');
        assert.equal(song.artist, 'A、B');
        assert.deepEqual(song.artistNames, ['A', 'B']);
        assert.deepEqual(song.sources[0].musicInfo.artistNames, ['A', 'B']);
        assert.equal(song.duration, 240);
        assert.equal(song.cover_url, '');
        assert.equal(home.sections.length, 3);
        assert.equal(f.requests.length, 3);
      } finally { await f.close(); }
    });
  }
});

test('KG home and category continuation accept a successful exhausted page without a list or next marker', async t => {
  for (const [operation, terminal] of [['home', {}], ['home', { has_next: 0, special_list: null }], ['channel', {}]]) {
    await t.test(operation + ' ' + JSON.stringify(terminal), async () => {
      const pages = [];
      let newSongs = 0, ranks = 0;
      const f = fixture(request => {
        const value = kg(request);
        if (value.path === '/pubsongs/v1/get_tags_by_type') return response({ status: 1,
          data: [{ tag_id: 9, tag_name: 'Genre', son: [{ tag_id: 10, tag_name: 'Tag' }] }] });
        if (value.path === '/v2/special_recommend') {
          pages.push(value.body.page);
          return response({ status: 1, error_code: 0, data: value.body.page === 1 ? {
            has_next: 1, special_list: [{ global_collection_id: 'playlist', name: 'Current list' }],
          } : terminal });
        }
        if (value.path === '/musicadservice/container/v1/newsong_publish') {
          newSongs++;
          return response({ status: 1, total: 1, data: [kgSong()] });
        }
        assert.equal(value.path, '/ocean/v6/rank/list');
        ranks++;
        return response({ status: 1, data: { info: [{ rankid: 7, rankname: 'Current rank' }] } });
      });
      seedClients(f);
      try {
        const params = operation === 'home' ? {} : { id: 'category-9', tag: '10' };
        const firstPage = await f.request('kg', operation, params);
        const firstItems = firstPage.sections[0].items;
        assert.deepEqual(firstItems.map(item => item.id), ['playlist']);
        assert.equal(firstPage.more, true);
        assert.equal(firstPage.continuation.page, 2);
        const lastPage = await f.request('kg', operation, { ...params, continuation: firstPage.continuation });
        assert.equal(lastPage.sections[0].id, firstPage.sections[0].id);
        assert.deepEqual(lastPage.sections[0].items, []);
        assert.equal(lastPage.more, false);
        assert.equal(lastPage.continuation, null);
        assert.deepEqual(firstItems.map(item => item.id), ['playlist'], 'the accepted first page remains usable');
        assert.deepEqual(pages, [1, 2]);
        assert.equal(newSongs, operation === 'home' ? 1 : 0);
        assert.equal(ranks, operation === 'home' ? 1 : 0);
        assert.equal(firstPage.sections.length, operation === 'home' ? 3 : 1);
        assert.equal(lastPage.sections.length, 1, 'an exhausted continuation does not reread first-page sections');
      } finally { await f.close(); }
    });
  }
});

test('KG missing or malformed recommendation lists retain the upstream continuation declaration', async t => {
  for (const data of [{ has_next: 1 }, { has_next: 1, special_list: null },
    { has_next: 0, special_list: {} }, { has_next: 0, special_list: false }]) {
    await t.test(JSON.stringify(data), async () => {
      const f = fixture(request => {
        assert.equal(kg(request).path, '/v2/special_recommend');
        return response({ status: 1, error_code: 0, data });
      });
      seedClients(f);
      try {
        const page = await f.request('kg', 'home', { continuation: { page: 2 } });
        assert.deepEqual(page.sections[0].items, []); assert.equal(page.more, Boolean(data.has_next));
        assert.deepEqual(page.continuation, data.has_next ? { page: 3 } : null);
        assert.equal(f.requests.length, 1, 'content omissions do not trigger replacement requests');
      } finally { await f.close(); }
    });
  }
});

// HEAD Windows MusicKgDiscoveryTests.TagPaging / NewSongPaging.
test('KG category continuation keeps the actual numeric tag, advances page and starts a newly selected tag at one', async () => {
  const pages = [], categories = [];
  const f = fixture(request => {
    const value = kg(request);
    assert.equal(request.method, 'POST');
    if (value.path === '/pubsongs/v1/get_tags_by_type') {
      assert.deepEqual(value.body, { tag_type: 'collection', tag_id: 0, source: 3 });
      return response({ status: 1, data: [{ tag_id: '5', tag_name: '场景', son: [
        { tag_id: '587', tag_name: '学习' }, { tag_id: '660', tag_name: '工作' },
      ] }] });
    }
    assert.equal(value.path, '/v2/special_recommend');
    assert.equal(request.headers['x-router'], 'specialrec.service.kugou.com');
    const { page, pagesize, special_recommend: { categoryid } } = value.body;
    pages.push(page); categories.push(categoryid);
    assert.ok([587, 660].includes(categoryid)); assert.equal(pagesize, 30);
    return response({ status: 1, data: { has_next: page === 1 ? 1 : 0,
      special_list: Array.from({ length: page === 1 ? pagesize : 1 }, (_, index) => ({
        global_collection_id: `collection_3_123456_${categoryid * 1000 + page * 100 + index}_0`, name: 'Current',
      })) } });
  }); seedClients(f);
  const first = await f.request('kg', 'channel', { id: 'category-5', tag: '587' });
  assert.equal(first.title, '场景'); assert.equal(first.selectedTag, '587');
  assert.deepEqual(first.tags, [
    { id: '587', name: '学习', group: '场景', selected: true },
    { id: '660', name: '工作', group: '场景', selected: false },
  ]);
  assert.equal(first.sections[0].items.length, 30); assert.equal(first.more, true);
  assert.deepEqual(first.continuation, { page: 2, tag: '587' });
  const second = await f.request('kg', 'channel', { id: 'category-5', tag: '587', continuation: first.continuation });
  assert.equal(second.sections[0].items[0].action.id, 'collection_3_123456_587200_0');
  assert.equal(second.sections[0].items.length, 1); assert.equal(second.more, false); assert.equal(second.continuation, null);
  const changed = await f.request('kg', 'channel', { id: 'category-5', tag: '660' });
  assert.equal(changed.selectedTag, '660');
  assert.equal(changed.sections[0].items[0].action.id, 'collection_3_123456_660100_0');
  assert.deepEqual(pages, [1, 2, 1]); assert.deepEqual(categories, [587, 587, 660]);
  assert.equal(f.requests.length, 4, 'one cached tag directory and exactly three selected recommendation pages');
  await f.close();
});

test('KG new-song continuation sends the actual rank POST and keeps filename metadata and 279-second durations across pages', async () => {
  const pages = [], hash = id => id.toString(16).padStart(32, '0').toUpperCase();
  const f = fixture(request => {
    const value = kg(request);
    assert.equal(value.path, '/musicadservice/container/v1/newsong_publish');
    assert.equal(request.method, 'POST'); assert.equal(request.headers['x-router'], undefined);
    assert.equal(value.body.rank_id, 21608); assert.equal(value.body.pagesize, 30);
    assert.deepEqual(value.body.tags, []); pages.push(value.body.page);
    const start = value.body.page === 1 ? 1 : 31;
    return response({ status: 1, total: 31, data: Array.from({ length: start === 1 ? 30 : 1 }, (_, index) => {
      const id = start + index;
      return { hash: hash(id).toLowerCase(), filename: 'Synthetic singer - Synthetic new song ' + id,
        timelength: 279000, album_id: 212839391, album_audio_id: 1000 + id, album_name: 'Synthetic album' };
    }) });
  }); seedClients(f);
  const first = await f.request('kg', 'channel', { id: 'newsong' });
  assert.equal(first.sections[0].items.length, 30); assert.equal(first.more, true);
  const second = await f.request('kg', 'channel', { id: 'newsong', continuation: first.continuation });
  assert.equal(second.sections[0].items.length, 1); assert.equal(second.more, false); assert.equal(second.continuation, null);
  for (const [song, id] of [[first.sections[0].items[0].song, 1], [second.sections[0].items[0].song, 31]]) {
    assert.equal(idOf(song), hash(id)); assert.equal(song.title, 'Synthetic new song ' + id);
    assert.equal(song.artist, 'Synthetic singer'); assert.equal(song.duration, 279);
    const info = song.sources[0].musicInfo;
    assert.equal(info.albumId, '212839391'); assert.equal(info.albumAudioId, String(1000 + id));
  }
  assert.deepEqual(pages, [1, 2]); assert.equal(f.requests.length, 2, 'no home, directory or unrelated member requests');
  await f.close();
});

test('QQ/KG account recommendation families retain actual dynamic results and alternate real hash fields', async t => {
  for (const [platform, kind] of [['tx', 'guess'], ['tx', 'radar'], ['kg', 'daily'], ['kg', 'fm']]) await t.test(platform + ':' + kind, async () => {
    const f = fixture(() => platform === 'tx' ? rpc(kind === 'guess' ? { tracks: [txSong()] } :
      { VecSongs: [{ Track: txSong() }], HasMore: true }) : response({ status: 1, data: { song_list: [
      { FileHash: kgHash, filename: 'A - Current', time_length: 240 },
    ] } }));
    seedClients(f); await f.session(platform);
    const value = await f.request(platform, 'recommendations', { kind });
    assert.equal(value.items.length, 1); assert.equal(idOf(value.items[0]), platform === 'kg' ? kgHash : 'SyntheticMID');
    assert.equal(f.requests.length, 1); await f.close();
  });
});
