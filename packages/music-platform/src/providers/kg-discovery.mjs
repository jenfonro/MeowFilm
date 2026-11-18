// SPDX-License-Identifier: GPL-3.0-only
// Fixed KuGouMusicApi discovery recipes; see LICENSES/NOTICE.md.
import { array, record, collection, first, image, images, input, kgSong, optionalNumber, pageNumber, protocolInteger, scalar, text } from '../model.mjs';
import { kgSalt, md5 } from '../protocols.mjs';
import { fail } from '../errors.mjs';
import { kgIdentity } from './kg.mjs';

const channelAction = (id, channelKind, pageCode = '') => ({ type: 'channel', channel: { id, channelKind, pageCode } });
const section = (id, title, type, items, action = null, layout = 'rail') => ({ id, title, type, items, action, layout });
async function tags(c) {
  return c.cached(['tags'], async () => {
    const raw = await c.kg('/pubsongs/v1/get_tags_by_type', {}, null, { tag_type: 'collection', tag_id: 0, source: 3 });
    c.observe('data', raw?.data);
    return array(raw?.data).map(record).map(row => ({ id: scalar(row.tag_id), name: text(row.tag_name),
      tags: array(row.son).map(record).map(tag => ({ id: scalar(tag.tag_id), name: text(tag.tag_name), group: text(row.tag_name) })) }));
  });
}
async function ranks(c) {
  return c.cached(['ranks'], async () => {
    const raw = await c.kg('/ocean/v6/rank/list', { plat: 2, withsong: 1, parentid: 0 });
    c.observe('data.info', raw?.data?.info);
    return array(raw?.data?.info).map(record).map(row => ({ id: scalar(row.rankid) ? 'rank-' + scalar(row.rankid) : '', name: text(row.rankname),
      kind: 'channel', cover: images(row.imgurl, row.img_cover), source: 'kg',
      action: scalar(row.rankid) ? channelAction('rank-' + scalar(row.rankid), 'new-songs', scalar(row.rank_cid)) : null,
      preview: array(row.songinfo).map(record).map((song, index) => ({ title: text(song.name), artist: text(song.author),
        cover: image(song.trans_param?.union_cover), rank: index + 1,
        song: kgSong(song) })),
      updateFrequency: text(row.update_frequency) }));
  });
}
async function playlists(c, page, tag, size) {
  const { mid, uid } = kgIdentity(c), time = String(Math.floor(c.clock() / 1000));
  const raw = await c.kg('/v2/special_recommend', {}, 'specialrec.service.kugou.com', {
    appid: 1005, mid, clientver: 20489, platform: 'android', clienttime: time, userid: uid, module_id: 1, page, pagesize: size,
    key: md5('1005' + kgSalt + '20489' + time), special_recommend: { withtag: 1, withsong: 1, sort: 1, ugc: 1,
      is_selected: 0, withrecommend: 1, area_code: 1, categoryid: protocolInteger(tag) },
    req_multi: 1, retrun_min: 5, return_special_falg: 1 });
  const more = (raw?.data?.has_next === true || Boolean(optionalNumber(raw?.data?.has_next)));
  c.observe('data.special_list', raw?.data?.special_list);
  return { items: array(raw?.data?.special_list).map(record).map(row => {
    const card = collection('kg', row);
    return { ...card, action: { type: 'playlist', id: card.id }, preview: array(row.songs).map(song => {
      const normalized = kgSong(song);
      return { title: normalized.title, artist: normalized.artist, cover: normalized.cover_url, song: normalized };
    }) };
  }), more };
}
async function songs(c, current, size, rank, cid) {
  const raw = rank ? await c.kgRank({ show_portrait_mv: 1, show_type_total: 1, filter_original_remarks: 1, area_code: 1,
    pagesize: size, rank_cid: protocolInteger(cid), type: 1, page: current, rank_id: protocolInteger(rank) }) :
    await c.kg('/musicadservice/container/v1/newsong_publish', {}, null, { rank_id: 21608, userid: kgIdentity(c).uid,
      page: current, pagesize: size, tags: [] });
  const values = rank ? raw?.data?.songlist : raw?.data;
  c.observe(rank ? 'data.songlist' : 'data', values);
  const source = array(values), total = optionalNumber(rank ? raw?.data?.total : raw?.total);
  return { items: source.map(kgSong), more: current * size < total };
}
export async function kgChannels(c) {
  const groups = await tags(c);
  const descriptor = (id, name, channelKind, placement = 'primary') => ({ id, name, kind: 'channel', channelKind, placement, available: true });
  return { platform: 'kg', items: [descriptor('feature', '精选', 'featured'), descriptor('playlist', '歌单广场', 'playlists'),
    descriptor('chart', '排行榜', 'rankings'), descriptor('newsong', '新歌', 'new-songs'),
    ...groups.map(row => descriptor(row.id ? 'category-' + row.id : '', row.name, 'playlists', 'more'))] };
}
export async function kgHome(c, params, target = 'kg:home') {
  const current = pageNumber(params.continuation?.page), list = await playlists(c, current, '0', 20);
  const sections = [section(target + ':playlists', '推荐歌单', 'playlists', list.items, channelAction('playlist', 'playlists'))];
  if (current === 1) {
    sections.push(section(target + ':new', '新歌', 'songs', (await songs(c, 1, 12)).items, channelAction('newsong', 'new-songs')));
    sections.push(section(target + ':ranks', '排行榜', 'rankings', await ranks(c), channelAction('chart', 'rankings')));
  }
  return { platform: 'kg', id: target, entries: [], sections, more: list.more,
    continuation: list.more ? { page: current + 1 } : null };
}
export async function kgChannel(c, params) {
  const id = input(params.id), target = 'kg:channel:' + id, prev = params.continuation ?? {}, current = pageNumber(prev.page);
  if (id === 'feature') return { ...await kgHome(c, params, target), title: '精选' };
  if (id === 'chart') return { platform: 'kg', id: target, title: '排行榜', sections: [
    section(target, '排行榜', 'rankings', await ranks(c))], more: false, continuation: null };
  let result, title, tags = [], tag = params.tag ?? prev.tag ?? '';
  if (id === 'newsong' || id.startsWith('rank-')) {
    let cid = params.pageCode ?? prev.rankCid ?? '';
    title = '新歌';
    if (id !== 'newsong' && !cid) {
      const card = (await ranks(c)).find(row => row.id === id);
      if (!card) fail('NOT_FOUND');
      cid = card.action.channel.pageCode; title = card.name;
    }
    result = await songs(c, current, 30, id === 'newsong' ? null : id.slice(5), cid);
    return { platform: 'kg', id: target, title, sections: [section(target, title, 'songs', result.items, null, 'list')],
      more: result.more, continuation: result.more ? { page: current + 1, rankCid: cid } : null };
  }
  const groups = await tagsFor(c, id);
  tags = groups.flatMap(row => row.tags); tag ||= id === 'playlist' ? '0' : tags[0]?.id ?? '0';
  result = await playlists(c, current, tag, 30);
  title = id === 'playlist' ? '歌单广场' : groups[0]?.name ?? '';
  return { platform: 'kg', id: target, title, sections: [section(target, title, 'playlists', result.items)],
    tags: [...(id === 'playlist' ? [{ id: '0', name: '推荐', group: '' }] : []), ...tags].map(row => ({ ...row, selected: row.id === tag })),
    selectedTag: tag, more: result.more, continuation: result.more ? { page: current + 1, tag } : null };
}
async function tagsFor(c, id) {
  const all = await tags(c);
  if (id === 'playlist') return all;
  if (!id.startsWith('category-')) fail('UNSUPPORTED');
  const group = all.find(row => row.id === id.slice(9));
  if (!group) fail('NOT_FOUND');
  return [group];
}
