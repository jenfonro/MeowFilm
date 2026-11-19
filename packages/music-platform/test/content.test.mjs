import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { projectContent } from '../src/content.mjs';
import { collection, images, kgSong, song, txSong, wySong } from '../src/model.mjs';

const fixture = name => JSON.parse(readFileSync(
  new URL('../../../tests/fixtures/music-content/' + name + '.json', import.meta.url), 'utf8'));
const inputs = fixture('inputs');

test('browse projection publishes the same fixed fixture consumed by both native clients', () => {
  const page = projectContent('wy', 'channel', inputs.browse);
  assert.deepEqual(page, fixture('browse-page'));
  assert.deepEqual(page.sections.map(section => section.layout),
    ['banner', 'feature-rail', 'song-list', 'playlist-grid', 'ranking', 'song-list']);
  const banners = page.sections[0].items;
  assert.equal(banners[0].cover, 'https://images.invalid/banner.jpg');
  assert.equal(banners[0].song.cover_url, 'https://images.invalid/song.jpg');
  assert.equal(banners[1].cover, '');
  assert.ok(banners.every(item => item.coverRef === null));
  assert.equal(page.sections[1].items[0].label, '');
  assert.deepEqual(page.sections[1].items[0].coverRef, { kind: 'playlist', id: 'synthetic-list' });
  assert.equal(page.sections[3].items[0].title, '缺少定位的歌单');
  assert.equal(page.sections[3].items[0].action, null);
  assert.equal(page.sections[4].items[0].action.type, 'chart');
  assert.equal(page.sections[4].items[0].canPlay, false);
  assert.equal(page.sections[4].action.channel.channelKind, 'rankings');
  assert.equal(page.sections[5].items.length, 0);
});

test('home labels are presentation roles, not a second kind of card parsing', () => {
  const home = projectContent('wy', 'home', inputs.home);
  assert.deepEqual(home, fixture('home-page'));
  assert.equal(home.sections[0].layout, 'feature-rail');
  assert.equal(home.sections[0].items[0].label, '私人雷达');
  assert.equal(home.sections[0].items[0].subtitle, '今天的歌单内容');
  assert.equal(home.sections[0].items[0].contextText, '今日动态歌单');
  const channel = projectContent('wy', 'channel', inputs.home);
  assert.equal(channel.sections[0].items[0].label, '');
  assert.equal(channel.sections[0].items[0].title, '私人雷达');
});

test('directory descriptors and ranked list songs have one native shape', () => {
  assert.deepEqual(projectContent('wy', 'channels', inputs.channels), fixture('channels'));
  const list = projectContent('mg', 'playlist', inputs.list);
  assert.deepEqual(list, fixture('list-page'));
  assert.equal(list.items[0].title, '合成歌曲');
  assert.equal(list.items[0].rank, 1);
  assert.deepEqual(list.items[0].sources, []);
});

test('missing content fields stay local and do not manufacture executable song identities', () => {
  for (const extract of [wySong, txSong, kgSong]) {
    for (const input of [null, [], {}, { name: '展示资料' }]) {
      const value = extract(input);
      assert.deepEqual(value.sources, []);
      assert.equal(typeof value.title, 'string');
    }
  }
  for (const platform of ['wy', 'tx', 'kg', 'kw', 'mg']) {
    assert.equal(collection(platform, { name: '无定位歌单' }).name, '无定位歌单');
    assert.deepEqual(song(platform, '', '无定位歌曲', '', '', null, '').sources, []);
  }
  const page = projectContent('wy', 'channel', { sections: [
    { id: 'missing-items', title: '保留栏目', type: 'songs', items: null },
    { id: 'partial-items', type: 'playlists', items: [null, { name: '保留卡片', count: {} }] }
  ] });
  assert.equal(page.sections.length, 2);
  assert.equal(page.sections[0].title, '保留栏目');
  assert.equal(page.sections[1].items.length, 2);
  assert.equal(page.sections[1].items[1].title, '保留卡片');
  assert.equal(page.sections[1].items[1].count, null);
});

test('image extraction tries usable candidates and preserves rendition identity', () => {
  assert.equal(images({}, '', '//p1.music.126.net/synthetic.jpg?param=480y480'),
    'https://p1.music.126.net/synthetic.jpg?param=480y480');
  assert.equal(images('data:image/png;base64,AA', 'https://images.invalid/cover/{size}.jpg?crop=1'),
    'https://images.invalid/cover/480.jpg?crop=1');
  assert.equal(images('https://user:secret@images.invalid/a.jpg'), '');
});

test('collection summaries and browse cards preserve play count separately from song count', () => {
  const summary = collection('wy', { id: 1, name: '歌单', songCount: 200, playCount: 3031000 });
  assert.equal(summary.count, 200); assert.equal(summary.playCount, 3031000);
  const cards = projectContent('wy', 'channel', { sections: [{ type: 'entries', items: [
    { ...summary, kind: 'playlist' }, { kind: 'playlist', playCount: 0 }, { kind: 'playlist', playCount: {} },
  ] }] }).sections[0].items;
  assert.deepEqual(cards.map(row => row.playCount), [3031000, 0, null]);
  assert.equal(cards[0].count, 200);
});
