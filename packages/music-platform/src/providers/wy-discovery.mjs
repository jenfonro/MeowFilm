// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 jenfonro and iWebPlus contributors; derived from iWebPlayer.
// Data-only translation of the pinned discovery recipes; no URL/DSL execution.
import { array, decodeJSON, first, image, images, input, number, optionalNumber, protocolInteger, record, scalar, text, wySong } from '../model.mjs';
import { fail } from '../errors.mjs';
import { wyDetails } from './wy.mjs';

const title = value => text(first(value?.name, value?.title, value?.uiElement?.mainTitle?.title));
const cover = (value, banner = false) => images(
  ...(banner ? [value?.imageUrl, value?.uiElement?.image?.imageUrl,
    ...array(value?.uiElement?.images).flatMap(row => [row?.imageUrl, row?.url])] : []),
  value?.cover, value?.coverImg, value?.coverUrl, value?.picUrl, value?.coverImgUrl, value?.img1v1Url,
  value?.imageUrl, value?.background, value?.uiElement?.image?.imageUrl,
  ...array(value?.uiElement?.images).flatMap(row => [row?.imageUrl, row?.url]));
const nested = value => {
  if (typeof value !== 'string') return value;
  try { return decodeJSON(Buffer.from(value)); } catch { return null; }
};
const section = (id, title, type, items, layout = 'rail', action = null) => ({ id, title, type, layout, items, action });
const emptyPage = id => ({ platform: 'wy', id, entries: [], sections: [], more: false, continuation: null });
const fmMode = value => ({ DEFAULT: 'default', FAMILIAR: 'familiar', EXPLORE: 'explore',
  PUZZLE: 'puzzle', PUZZLE_MODE_RCMD: 'puzzle', SCENE: 'scene', SCENE_RCMD: 'scene' })[text(value).toUpperCase()] ?? 'default';
const supportedChannel = kind => ['featured', 'cube', 'playlists', 'rankings', 'new-songs', 'artists', 'artist-activity', 'vip'].includes(kind);
const descriptor = row => ({ id: row.id, name: row.name, pageCode: row.pageCode ?? '', kind: 'channel',
  channelKind: row.kind, available: supportedChannel(row.kind), placement: row.placement ?? 'more' });

function action(value) {
  const data = nested(value);
  if (data && typeof data === 'object') {
    if (data.type === 'resource' || data.type === 'play') {
      const kind = { song_list: 'playlist', list: 'playlist' }[data.params?.resourceType] ?? data.params?.resourceType;
      if (['song', 'playlist', 'album'].includes(kind) && scalar(data.params?.query))
        return { type: kind, id: scalar(data.params.query) };
    }
    const next = first(data.clickAction, data.url, data.targetUrl, data.urlAction?.url);
    return next ? action(next) : null;
  }
  if (!text(value)) return null;
  let url;
  try { url = new URL(value); } catch { return null; }
  if (url.protocol !== 'orpheus:') return null;
  const path = url.hostname + url.pathname, q = key => url.searchParams.get(key) ?? '';
  const match = /^(playlist|song|album)\/(.+)$/.exec(path);
  if (match) {
    try { return { type: match[1], id: decodeURIComponent(match[2]) }; }
    catch { return null; }
  }
  if (['songrcmd', 'songrcmd/', 'nm/play/dailySong'].includes(path))
    return { type: 'daily', branch: first(q('tab'), q('type')) === 'style' ? 'style' : 'default' };
  if (['nm/luna/privatefm', 'privatefm', 'privatefm/'].includes(path))
    return { type: 'fm', mode: 'default', submode: '' };
  if (path === 'nm/play/similarFM') {
    const ids = nested(q('sourceId'));
    return Array.isArray(ids) ? { type: 'similar', sourceType: q('sourceType'),
      sourceIds: ids.map(scalar), sourceName: q('sourceName') } : null;
  }
  if (['rnpage', 'rnpage/'].includes(path)) {
    const component = q('component'), kinds = { 'rn-ranklist-homepage': 'rankings',
      'rn-playlist-square': 'playlists', 'rn-playlistsquare': 'playlists',
      'rn-vip-portal': 'vip', 'rn-music-homepage': 'featured' };
    if (component === 'cube-renderer-rn' && q('page'))
      return { type: 'channel', channel: { id: q('page'), channelKind: 'cube', pageCode: q('page') } };
    if (kinds[component]) return { type: 'channel', channel: { id: kinds[component], channelKind: kinds[component] } };
  }
  if (path === 'nm/mainTab/select' && q('tab') === 'music') {
    const id = scalar(first(q('musicTabCode'), q('subTab')));
    const channelKind = { feature: 'featured', playlist: 'playlists', chart: 'rankings',
      artist: 'artists', vip: 'vip', newsong: 'new-songs' }[id] ?? 'unsupported';
    return { type: 'channel', channel: { id, channelKind } };
  }
  return null;
}
function nativeSong(raw, resourceCover = '') {
  raw = record(raw);
  const s = record(first(raw.songInfo, raw.songData, raw.song, raw));
  return wySong({ ...s, id: first(s.id, s.songId, s.resourceId), name: first(s.name, s.title),
    artistName: first(s.artistName, s.subTitle),
    coverUrl: images(s.coverUrl, cover(s), resourceCover) });
}
function entry(value, c) {
  const type = value.action?.type;
  const requiresAccount = type === 'daily' || type === 'heartbeat';
  const display = !requiresAccount || Boolean(c.account);
  return { ...value, presentation: ['playlist', 'daily', 'album'].includes(type) ? 'preview' :
    type === 'similar' ? 'overlay' : ['fm', 'heartbeat'].includes(type) ? 'animated' : 'static',
  requiresAccount, display, available: Boolean(type) && type !== 'unsupported' && display,
  ...(display ? {} : { errorCode: 'AUTH_REQUIRED' }) };
}
function resource(r, { banner = false } = {}) {
  if (!r || typeof r !== 'object') return null;
  const ext = r.resourceExtInfo ?? {}, play = nested(r.playBtnData);
  const kind = text(first(r.resourceType, r.targetType, r.type, r.creativeType)).toLowerCase();
  if (/video|podcast|voice|djradio|djprogram|mv/.test(kind)) return null;
  const declared = action(first(r.action, r.url, r.orpheus, r.clickAction)) ?? action(play?.detailUrl);
  const id = scalar(first(r.rankId, r.resourceId, r.id, r.resource));
  const result = { id, label: title(r), name: title(r), cover: cover(r, banner), subtitle: text(first(r.subTitle, r.subtitle)),
    badge: text(array(r.tags)[0]), source: 'wy', engine: 'Netease' };
  const finish = (type, action) => ({ ...result, kind: type, action });
  if (text(r.singleLineTitle)) {
    result.name = kind === 'dailysongs' && r.dayOfMonth != null ? result.name.split(/\s*\|\s*/)[0] :
      result.subtitle || result.name;
    result.subtitle = r.singleLineTitle;
  }
  if (kind === 'similarsong' || kind === 'similarartist' || declared?.type === 'similar')
    return declared?.type === 'similar' ? finish('similar', declared) :
      finish('unsupported', { type: 'unsupported' });
  if (kind === 'star' || r.lunaItemType === 'heartRcmd') {
    const raw = first(ext.songData, ext.song, r.songData, r.songInfo);
    if (raw) {
      result.entrySong = nativeSong(raw, cover(r));
      return finish('collection', { type: 'heartbeat', entry: 'favorites',
        ...(result.entrySong.sources[0] ? { songId: result.entrySong.sources[0].musicInfo.songmid } : {}) });
    }
    return finish('collection', { type: 'heartbeat', entry: 'favorites' });
  }
  if (ext.songData || ext.song || r.songData || r.songInfo || kind === 'song' || declared?.type === 'song') {
    const raw = first(ext.songData, ext.song, r.songData, r.songInfo) ?? { ...r, id: declared?.id ?? id };
    result.song = nativeSong(raw, cover(r));
    result.id = result.song.sources[0]?.musicInfo.songmid ?? '';
    if (!banner) {
      result.name ||= result.song.title;
      result.subtitle ||= result.song.artist;
      result.cover ||= result.song.cover_url;
    }
    const source = action(play?.detailUrl);
    if (source?.type === 'playlist') result.playbackSource = source;
    return finish('song', result.id ? { type: 'song', id: result.id } : null);
  }
  if (kind === 'dailysongs' || declared?.type === 'daily') {
    result.dayOfMonth = optionalNumber(r.dayOfMonth);
    return finish('collection', { type: 'daily', branch: declared?.branch ?? 'default' });
  }
  if (kind === 'fm' || declared?.type === 'fm') {
    result.coverLabel = text(r.fmLogoTextContent);
    if (scalar(r.ruleId) || scalar(r.tabTagId))
      return finish('fm', { type: 'fm', provider: 'rule', ruleId: scalar(r.ruleId), tabTagId: scalar(r.tabTagId),
        sourceName: result.coverLabel || result.name });
    return finish('fm', { type: 'fm', mode: fmMode(text(r.mode) || declared?.mode),
      submode: text(first(r.subMode, r.submode)) || declared?.submode || '' });
  }
  if (['playlist', 'play_list', 'song_list', 'list', 'scroll_playlist', 'toplist'].includes(kind) || declared?.type === 'playlist') {
    const pid = declared?.id ?? id;
    if (!pid) {
      const ids = first(play?.playAction?.songIds, r.songIds);
      if (!Array.isArray(ids) || !ids.length) return finish(kind === 'toplist' ? 'ranking' : 'playlist', null);
      result.id = 'songs:' + scalar(r.resourceId);
      result.engine = 'NeteaseCollection'; result.collectionType = 'songs'; result.count = ids.length;
      return finish(kind === 'toplist' ? 'ranking' : 'collection', { type: 'song-collection', ids: ids.map(scalar) });
    }
    result.id = pid;
    if (!banner && r.coverType === 'ALBUM_LOGO') {
      for (const value of [r.action, r.url, play?.detailUrl]) {
        try {
          const url = new URL(value), supplied = image(url.searchParams.get('coverUrl'));
          if (url.protocol === 'orpheus:' && url.hostname === 'playlist' && supplied && supplied !== result.cover) {
            result.decoration = result.cover; result.cover = supplied; break;
          }
        } catch { /* No optional cover decoration. */ }
      }
    }
    result.count = optionalNumber(first(r.trackCount, r.songCount, ext.songCount));
    result.creator = text(r.creator?.nickname);
    result.preview = array(first(r.rankItems, r.trackRankList)).map(record).map(row => ({
      id: scalar(first(row.trackId, row.songId)), title: text(first(row.songName, row.title)),
      artist: text(first(row.artistName, row.subTitle)), cover: cover(row),
      rank: optionalNumber(row.rank), lastRank: optionalNumber(row.lastRank),
      ...(first(row.trackId, row.songId) ? { song: wySong({ ...row, id: first(row.trackId, row.songId),
        name: first(row.songName, row.title), artistName: first(row.artistName, row.subTitle), picUrl: cover(row) }) } : {}) }));
    result.songCover = image(r.songCoverImgUrl); result.updateFrequency = text(r.updateFrequency);
    return finish(kind === 'toplist' ? 'ranking' : 'playlist', { type: 'playlist', id: pid });
  }
  if (['album', 'digitalbum', 'digitalalbum'].includes(kind) || declared?.type === 'album') {
    result.id = declared?.id ?? id; result.engine = 'NeteaseAlbum';
    result.artist = text(first(r.artistName, r.subtitle)); result.count = optionalNumber(r.songCount);
    return finish('album', result.id ? { type: 'album', id: result.id } : null);
  }
  if (declared?.type === 'channel') {
    result.id = declared.channel.id;
    return finish('channel', declared);
  }
  if (kind === 'dragon_ball') {
    const selected = action(first(r.uiElement?.mainTitle?.action, r.action));
    if (selected) return finish('entry', selected);
  }
  return finish('unsupported', null);
}
// These traversals read the displayed DSL's resource/component tree only.
function* leaves(node) {
  if (Array.isArray(node)) { for (const child of node) yield* leaves(child); return; }
  if (!node || typeof node !== 'object') return;
  if (node.resourceType != null || node.songInfo || node.songData || node.resourceExtInfo?.songData ||
      node.targetType != null || node.action && node.resourceId != null) { yield node; return; }
  for (const [key, value] of Object.entries(node))
    if (!['playBtn', 'playBtnData', 'playAll', 'playAction', 'clickAction', 'log', 'track', 'crossPlatformConfig'].includes(key)) yield* leaves(value);
}
function roots(value) {
  const data = nested(value);
  if (!data || typeof data !== 'object') return [];
  if (data.blockResource) return [data.blockResource];
  if (data.content?.items || data.resources || data.items) return [data];
  const candidates = Object.values(data).filter(value => value && typeof value === 'object' && !Array.isArray(value));
  const selected = candidates.filter(value => value.blockResource || value.header || value.block || value.resources || value.items || value.content?.items);
  return (selected.length ? selected : candidates).map(value => value.blockResource ?? value);
}
function contentPage(raw, target, c, cursor = '') {
  const data = record(raw?.data ?? raw), blocks = array(data.blocks).map(record), result = emptyPage(target), occurrences = new Map();
  c.observe('blocks', data.blocks);
  blocks.forEach((block, bi) => {
    const code = text(first(block.positionCode, block.blockCode, block.code, block.id)) || 'block-' + bi;
    const nth = occurrences.get(code) ?? 0; occurrences.set(code, nth + 1);
    const base = `${target}:${code}:${scalar(first(block.id, block.channelCode)) || cursor || 'root'}:${nth}`;
    const groups = block.dslData ? roots(block.dslData) : [block];
    groups.forEach((group, ri) => {
      const native = block.dslData ? [...leaves(group)] : array(block.creatives).flatMap(creative =>
        creative == null ? [] : array(creative.resources).length ?
          creative.resources.flatMap(value => value == null ? [] : [{ ...creative, ...value, creativeType: creative.creativeType }]) :
          [{ ...creative, resourceId: creative.creativeId }]);
      const banner = /BANNER|CAROUSEL/i.test(text(block.showType));
      const items = native.map(value => resource(value, { banner })).filter(Boolean);
      const entrance = items.some(value => ['daily', 'heartbeat', 'fm', 'similar'].includes(value.action?.type)) ||
        native.some(value => value.resourceType === 'dragon_ball') ||
        nested(block.dslData)?.dslShowTitle === false && !text(block.showType).startsWith('PLAYLIST_SQUARE_');
      if (entrance && !banner) {
        result.entries.push(...items.map((value, index) => entry({ ...value, key: `${base}:entry:${ri}:${index}` }, c)));
        return;
      }
      let run;
      for (const value of items) {
        const type = banner ? 'banner' : value.kind === 'song' ? 'songs' : value.kind === 'ranking' ? 'rankings' : 'playlists';
        if (!run || run.type !== type) {
          const heading = block.hideTitle ? '' : text(first(group.title, group.header?.title, group.block?.title,
            block.uiElement?.subTitle?.title, block.uiElement?.mainTitle?.title));
          run = section(`${base}:${ri}:${result.sections.length}`, target === 'wy:home' && code === 'PAGE_RECOMMEND_MY_SHEET' && type === 'playlists' ?
            '最近常听' : heading, type, [], /MULTI.*GRID|FLAT|GRID/i.test(text(block.showType)) ? 'grid' : 'rail',
            action(first(group.action, group.header?.action)));
          result.sections.push(run);
        }
        run.items.push(value);
      }
    });
  });
  result.more = Boolean(data.hasMore);
  result.continuation = result.more && scalar(data.cursor) ? { cursor: scalar(data.cursor),
    blockCodeOrderList: scalar(data.blockCodeOrderList),
    loadedPositionCodes: JSON.stringify(blocks.map(row => text(row.positionCode)).filter(Boolean)) } : null;
  return result;
}
async function content(c, params, kind, target) {
  const prev = params.continuation ?? {}, cursor = scalar(prev.cursor) || '0';
  const body = { pageCode: kind === 'home' ? 'HOME_RECOMMEND_PAGE' : 'HOME_DISCOVERY_PAGE',
    cursor: protocolInteger(cursor), isFirstScreen: cursor === '0', pageStyleType: 'noCutBlock', refresh: false };
  for (const key of ['blockCodeOrderList', 'loadedPositionCodes']) if (text(prev[key])) body[key] = prev[key];
  const result = contentPage(await c.wy('/api/link/page/rcmd/resource/show', body), target, c, cursor);
  if (result.continuation) {
    const before = array(nested(prev.loadedPositionCodes)), after = array(nested(result.continuation.loadedPositionCodes));
    result.continuation.loadedPositionCodes = JSON.stringify([...new Set([...before, ...after])]);
  }
  return result;
}
async function directory(c) {
  return c.cached(['channel-directory'], async () => {
    const raw = await c.wy('/api/link/position/show/resource', { positionCode: 'music_top_tab_full' });
    const data = record(raw?.data ?? raw), scene = data.generalizedSceneShow ?? data;
    const source = first(scene.generalizedMap?.nowChannelItems, data.nowChannelItems, data.channels);
    c.observe('channels', source);
    const channels = array(source).map(record);
    const items = channels.map((row, index) => {
      const selected = action(first(row.url, row.action));
      const code = text(row.code);
      const kind = { chart: 'rankings', playlist: 'playlists', feature: 'featured',
        artist: 'artists', vip: 'vip', newsong: 'new-songs' }[code] ?? selected?.channel?.channelKind ?? 'unsupported';
      return { id: code || selected?.channel?.id || 'channel-' + index,
        name: code === 'playlist' ? '歌单广场' : title(row), kind, pageCode: selected?.channel?.pageCode ?? '' };
    });
    if (!items.some(row => row.id === 'artist')) items.push({ id: 'artist', name: '歌手', kind: 'artists' });
    return items;
  });
}
export const wyHome = (c, params) => {
  const previous = params.continuation ?? {};
  const continuation = { cursor: previous.cursor ?? '0',
    blockCodeOrderList: previous.blockCodeOrderList ?? '',
    loadedPositionCodes: previous.loadedPositionCodes ?? '' };
  return c.cached(['home', Boolean(params.featured), continuation],
    () => content(c, { continuation }, params.featured ? 'featured' : 'home',
      params.featured ? 'wy:featured' : 'wy:home'));
};
export async function wyChannels(c) {
  const primary = ['feature', 'playlist', 'chart', 'artist', 'vip', 'euro', 'djR', 'huayu', 'qingge', 'acg'];
  const items = await directory(c);
  return { platform: 'wy', items: [
    ...primary.flatMap(id => items.filter(row => row.id === id).map(row => descriptor({ ...row, placement: 'primary' }))),
    ...items.filter(row => !primary.includes(row.id)).map(descriptor),
  ] };
}
function rankings(raw, target) {
  const result = emptyPage(target);
  for (const [index, group] of array(first(raw?.data, raw?.list)).map(record).entries()) {
    const items = array(group.list).map(record).map(row => {
      if (row.targetType === 'PLAYLIST') {
        const card = resource({ ...row, resourceType: 'playlist' });
        if (card) card.canPlay = row.canPlay == null || Boolean(row.canPlay);
        return card;
      }
      let kind = { DIGITAL_ALBUM: 'albums', USER_RCMD_SONG: 'user-recommendations' }[row.toplistCode] ?? '';
      try {
        const url = new URL(row.frontTargetUrl);
        if (url.protocol === 'https:' && url.hostname === 'music.163.com' && url.pathname === '/st/album/m/album/rank')
          kind = { 1: 'singles', 2: 'song-sales' }[url.searchParams.get('type')] ?? 'albums';
      } catch { /* Not a native destination. */ }
      return { id: scalar(row.toplistCode), name: title(row), cover: cover(row), kind: kind ? 'chart' : 'unsupported',
        action: { type: kind ? 'chart' : 'unsupported', id: kind || scalar(row.toplistCode) },
        preview: array(row.trackRankList).map(record).map(track => ({ title: text(first(track.songName, track.itemName, track.title)),
          artist: text(track.artistName), cover: cover(track), rank: optionalNumber(track.rank), lastRank: optionalNumber(track.lastRank),
          ...(first(track.trackId, track.songId) ? { song: wySong({ ...track, id: first(track.trackId, track.songId),
            name: first(track.songName, track.itemName, track.title), picUrl: cover(track) }) } : {}) })),
        updateFrequency: text(row.updateFrequency), canPlay: false };
    }).filter(Boolean);
    if (items.length) result.sections.push(section(target + ':' + (text(group.categoryCode) || index), text(group.name),
      'rankings', items, text(first(group.frontDisplayType, group.displayType)) || 'ONLY_COVER'));
  }
  return result;
}
function artistTags(selection) {
  const groups = { area: [[-1, '全部'], [7, '华语'], [96, '欧美'], [8, '日本'], [16, '韩国'], [0, '其他']],
    type: [[-1, '全部'], [1, '男歌手'], [2, '女歌手'], [3, '乐队组合']],
    initial: [[-1, '热门'], ...Array.from({ length: 26 }, (_, index) => [65 + index, String.fromCharCode(65 + index)]), [0, '#']] };
  return Object.entries(groups).flatMap(([group, values], index) => values.map(([value, name]) => {
    const next = [...selection]; next[index] = value;
    return { id: next.join(':'), name, group, selected: value === selection[index] };
  }));
}
function cube(raw, target, selectedTag, c) {
  const protocol = record(first(raw?.data?.pageProtocol, raw?.pageProtocol)), pages = array(protocol.pages);
  c.observe('pageProtocol.pages', protocol.pages);
  const groups = [], parents = new Map();
  function collect(node, parent) {
    node = record(node);
    if (['MusicGenreSongListItem', 'TabItem'].includes(node.componentName)) {
      groups.push(node); parents.set(node, parent); return;
    }
    for (const child of array(node.children)) collect(child, node);
  }
  for (const node of pages) collect(node, null);
  const groupId = row => text(row.id) || 'tab-' + groups.indexOf(row);
  const parent = parents.get(groups[0]), siblings = groups.filter(row => parents.get(row) === parent);
  const fallback = parent?.componentName === 'MusicGenreSongList' && !parent.props?.useNewTabBarStyle ?
    siblings[Math.floor(siblings.length / 2)] : siblings[0];
  const chosen = groups.find(row => groupId(row) === selectedTag) ?? groups.find(row => row.props?.isDefaultTab) ?? fallback;
  const result = { ...emptyPage(target), title: text(protocol.title), selectedTag: chosen ? groupId(chosen) : '',
    tags: groups.map(row => ({ id: groupId(row), name: text(row.props?.title), selected: row === chosen })) };
  function add(node, type, values, layout = 'rail', heading = '') {
    const items = values.filter(Boolean);
    if (items.length) result.sections.push(section(`${target}:${text(node.id) || result.sections.length}:${type}`, heading, type, items, layout));
  }
  const componentResource = node => resource({ ...node.props,
    resourceType: node.componentName === 'Entrance' ? '' : text(node.componentName).toLowerCase() });
  function walk(node, heading = '') {
    node = record(node);
    if (groups.includes(node) && node !== chosen) return;
    const p = node.props ?? {}, children = array(node.children).map(record);
    if (p.songList?.ruleId != null) result.feed = { id: target + ':feed:' + text(node.id), title: text(p.songList.title),
      songs: [], more: true, cursor: 0, request: { ruleId: scalar(p.songList.ruleId),
        pointIds: array(p.songList.pointId).map(row => scalar(row?.id)).join(','), tabTagId: '', algTagIds: '' } };
    if (Array.isArray(p.carousel)) {
      add(node, 'banner', p.carousel.map(row => resource(row, { banner: true }))); return;
    }
    if (Array.isArray(p.recommendModule)) {
      const tabs = p.recommendModule.map(record).map((group, i) => ({ id: text(node.id) + ':group:' + i,
        name: text(group.title), selected: Boolean(group.isDefaultTab),
        items: array(group.modules).map((row, index) => {
          if (row == null) return null;
          row = record(row);
          const value = resource({ ...row, resourceType: row.type,
            ...(row.type === 'fm' && !row.mode && !row.ruleId && !row.tabTagId && !action(first(row.action, row.url, row.orpheus)) ?
              { tabTagId: p.tab } : {}) });
          return value ? entry({ ...value, key: `${target}:${text(node.id)}:entry:${i}:${index}` }, c) : null;
        }).filter(Boolean) })).filter(row => row.items.length);
      const tab = tabs.find(row => row.selected) ?? tabs[0];
      if (tab) {
        add(node, 'entries', tab.items, 'rail', text(p.title));
        if (tabs.length > 1) Object.assign(result.sections.at(-1), { tabs, selectedTab: tab.id });
      }
      return;
    }
    if (text(p.playlistIds)) {
      const names = text(p.titles).split(',');
      add(node, 'rankings', p.playlistIds.split(',').map((id, index) =>
        resource({ id, name: names[index] ?? '', resourceType: 'playlist' })), 'ranked-songs', text(p.rankModuleTitle)); return;
    }
    if (node.componentName === 'BillboardSingleSongList' && scalar(p.id)) {
      add(node, 'playlists', [resource({ id: p.id, name: p.cardTitle, subtitle: p.cardSubTitle, resourceType: 'playlist' })],
        'rail', text(p.cardTitle)); return;
    }
    if (p.albumData) { add(node, 'playlists', [resource({ ...p.albumData, resourceType: 'album' })], 'rail', heading); return; }
    if (node.componentName === 'InfiniteScrollList') {
      const songs = [];
      function collectSongs(value) {
        value = record(value);
        if (value.componentName === 'Song') {
          const selected = componentResource(value); if (selected?.song) songs.push(selected);
        } else for (const child of array(value.children)) collectSongs(child);
      }
      for (const child of children.filter(row => ['Grid', 'Song'].includes(row.componentName))) collectSongs(child);
      result.feed = { id: target + ':feed:' + text(node.id), title: text(p.listTitleStr), songs, more: false, cursor: null, request: null };
      add(node, 'songs', songs, 'list', text(p.listTitleStr));
      for (const child of children.filter(row => !['Grid', 'Song'].includes(row.componentName))) walk(child, heading);
      return;
    }
    if (['Grid', 'Scroll'].includes(node.componentName)) {
      const direct = ['Playlist', 'Song', 'Album', 'Entrance'];
      const resources = children.filter(row => direct.includes(row.componentName)).map(componentResource).filter(Boolean);
      add(node, 'songs', resources.filter(row => row.kind === 'song'), 'rail', heading);
      add(node, 'playlists', resources.filter(row => row.kind !== 'song'), node.componentName === 'Grid' ? 'grid' : 'rail', heading);
      for (const child of children.filter(row => !direct.includes(row.componentName))) walk(child);
      return;
    }
    for (const child of children) {
      if (child.componentName === 'Title') heading = text(first(child.props?.mainTitle, child.props?.title));
      else walk(child, heading);
    }
  }
  for (const node of pages) walk(node);
  return result;
}
export async function wyChannelPage(c, params) {
  const id = input(params.id), prev = params.continuation ?? {}, selectedTag = params.tag ?? prev.tag ?? '';
  // A supplied descriptor already contains the real recipe selector; do not re-read its directory to prove it.
  const known = { feature: 'featured', featured: 'featured', chart: 'rankings', rankings: 'rankings',
    playlist: 'playlists', playlists: 'playlists', artist: 'artists', artists: 'artists', newsong: 'new-songs', 'new-songs': 'new-songs', vip: 'vip' };
  const channel = params.pageCode ? { id, name: text(params.name), kind: 'cube', pageCode: params.pageCode } :
    known[id] ? { id, name: text(params.name), kind: known[id] } : (await directory(c)).find(row => row.id === id);
  if (!channel) fail('NOT_FOUND');
  const target = 'wy:channel:' + id;
  let result;
  switch (channel.kind) {
    case 'featured': result = await content(c, params, 'featured', target); break;
    case 'rankings': result = rankings(await c.wy('/api/toplist/detail/v2', {}, true), target); break;
    case 'new-songs': {
      const raw = await c.wy('/api/v1/discovery/new/songs', { areaId: 0, total: true }, true);
      result = emptyPage(target); result.sections.push(section(target + ':songs', channel.name, 'songs', array(raw?.data).map(wySong), 'list')); break;
    }
    case 'playlists': {
      const tag = selectedTag || '推荐';
      result = contentPage(await c.wy('/api/playlist/square/block/page', { categoryName: tag, offset: 0, limit: 20,
        ...(prev.cursor == null ? {} : { cursor: scalar(prev.cursor) }) }, true), target, c, scalar(prev.cursor));
      result.tags = ['推荐', '官方', '华语', '摇滚', '民谣', '电子', '轻音乐'].map(name => ({ id: name, name, group: 'playlist', selected: name === tag }));
      result.selectedTag = tag;
      if (result.continuation) result.continuation.tag = tag;
      break;
    }
    case 'artists': {
      const values = input(selectedTag || '-1:-1:-1').split(':').map(value => number(value, undefined, 'INVALID_REQUEST'));
      const [area, type, initial] = values, offset = number(prev.offset, 0, 'INVALID_REQUEST');
      const raw = await c.wy('/api/v1/artist/list', { area, type, initial, offset, limit: 40, total: true }, true);
      const data = record(raw?.data ?? raw), source = array(first(data.artists, raw?.artists)), more = Boolean(first(data.more, raw?.more));
      result = { ...emptyPage(target), tags: artistTags(values), selectedTag: values.join(':'), more,
        continuation: more ? { offset: offset + 40, tag: values.join(':') } : null };
      result.sections.push(section(target + ':artists', '', 'artists', source.map(record).map(row => ({ id: scalar(first(row.id, row.artistId)),
        name: title(row), cover: cover(row), kind: 'artist', source: 'wy',
        subtitle: optionalNumber(first(row.musicSize, row.songCount)) == null ? '' : '单曲：' + first(row.musicSize, row.songCount) })), 'artist-grid'));
      break;
    }
    case 'cube': {
      result = cube(await c.wy('/api/cube/render/page/protocol', { pageCode: input(channel.pageCode), scene: 'music' }, true), target, selectedTag, c);
      const offset = result.feed?.request ? number(prev.offset, 0, 'INVALID_REQUEST') : undefined;
      if (result.feed?.request) {
        const request = result.feed.request, layoutTag = result.tags.some(row => row.id === selectedTag);
        request.algTagIds = layoutTag ? '' : selectedTag;
        let tags = [];
        try {
          const tagData = await c.wy('/api/tag/tab/tags', { ruleId: request.ruleId, ruleType: 1, tabTagId: '' });
          tags = array(first(tagData?.data?.algTagInfos, tagData?.algTagInfos)).map(record).flatMap(row => array(row.children).length ?
            row.children.map(record).map(child => ({ id: scalar(child.algTagId), name: text(child.algTagName), group: text(row.algTagName) })) :
            [{ id: scalar(row.algTagId), name: text(row.algTagName), group: '' }]);
        } catch (error) {
          c.signal.throwIfAborted();
          if (['AUTH_REQUIRED', 'AUTH_EXPIRED', 'INVALID_CREDENTIALS', 'CANCELLED', 'CLOSED', 'STALE_SESSION', 'TIMEOUT']
            .includes(c.errorCode(error))) throw error;
          // Filter metadata is optional; it is not authentication or song data.
        }
        tags.unshift({ id: result.selectedTag, name: '全部歌曲', group: '' });
        const feedSection = { ...section(result.feed.id, result.feed.title, 'songs', [], 'list'),
          tags, selectedTag: selectedTag || result.selectedTag, errorCode: null };
        if (offset > 0) result.sections = [];
        result.sections.push(feedSection);
        Object.assign(result.feed, { songs: [], tags, selectedTag: feedSection.selectedTag, more: false, cursor: null });
        try {
          const feed = await c.wy('/api/tag/tab/alg/recommends', { ruleType: 1, ruleId: request.ruleId, tabTagId: '',
            algTagIds: request.algTagIds, extMap: JSON.stringify({ pointIds: request.pointIds }),
            limit: 30, offset, trialMode: 103, source: offset > 0 ? 'nextPage' : 'reset' });
          const data = record(feed?.data ?? feed);
          c.observe('feed.songs', data.songs);
          const items = array(data.songs).map(wySong), more = Boolean(data.hasMore);
          Object.assign(result.feed, { songs: items, more, cursor: more ? offset + 30 : null });
          feedSection.items = items;
          result.more = more; result.continuation = more ? { offset: offset + 30, tag: selectedTag,
            ruleId: request.ruleId, pointIds: request.pointIds, tabTagId: '', algTagIds: request.algTagIds } : null;
        } catch (error) {
          c.signal.throwIfAborted();
          if (offset > 0 || ['AUTH_REQUIRED', 'AUTH_EXPIRED', 'INVALID_CREDENTIALS', 'CANCELLED', 'CLOSED', 'STALE_SESSION', 'TIMEOUT']
            .includes(c.errorCode(error))) throw error;
          feedSection.errorCode = c.errorCode(error);
          result.more = false; result.continuation = null;
        }
      }
      break;
    }
    case 'vip': {
      result = emptyPage(target);
      const reads = await Promise.allSettled([
        c.wy('/api/vipnewcenter/app/viptab/recommend/song/list', {}),
        c.wy('/api/toplist/detail/v2', {}, true),
      ]);
      c.signal.throwIfAborted();
      if (reads.every(row => row.status === 'rejected')) throw reads[0].reason;
      if (reads[0].status === 'fulfilled') {
        const raw = reads[0].value, data = record(raw?.data ?? raw);
        const items = array(first(data.songInfoList, raw?.songInfoList)).map(wySong);
        if (items.length) result.sections.push(section(target + ':recommend', text(data.title) || '黑胶VIP 每日推荐', 'songs', items, 'vip-songs'));
      }
      if (reads[1].status === 'fulfilled') {
        const items = rankings(reads[1].value, target).sections.flatMap(row => row.items).filter(row => /^黑胶(?:SVIP|VIP)/i.test(row.name));
        if (items.length) result.sections.push(section(target + ':rankings', '黑胶VIP 排行榜', 'rankings', items, 'vip-ranked-songs'));
      }
      break;
    }
    default: fail('UNSUPPORTED');
  }
  result.channel = descriptor(channel); result.title ||= channel.name;
  return result;
}
export async function wyCharts(c, { kind, period = 'daily', year }) {
  const result = emptyPage('chart:' + input(kind));
  let items, name, type = 'songs', layout = 'list';
  if (kind === 'albums' || kind === 'singles') {
    const route = { daily: 'daily', weekly: 'week', yearly: 'year', overall: 'total' }[period];
    if (!route) fail('UNSUPPORTED');
    const raw = await c.wy('/api/feealbum/songsaleboard/' + route + '/type' + (period === 'yearly' ? '/' : ''),
      { albumType: kind === 'albums' ? 0 : 1, limit: 50, ...(period === 'yearly' ? { year: year ?? new Date(c.clock()).getUTCFullYear() } : {}) }, true);
    items = array(raw?.products).map(record).map((row, index) => ({ id: scalar(row.albumId), name: text(row.albumName),
      cover: image(row.coverUrl), subtitle: text(row.artistName), rankText: String(index + 1),
      salesText: optionalNumber(row.saleNum) == null ? '' : Number(row.saleNum).toLocaleString('en-US') + ' 张',
      kind: 'album', action: scalar(row.albumId) ? { type: 'album', id: scalar(row.albumId) } : null }));
    name = kind === 'albums' ? '数字专辑畅销榜' : '数字单曲榜'; type = 'albums'; layout = 'ranking';
  } else if (kind === 'song-sales') {
    const raw = await c.wy('/api/single/sales-ranking/get', {}, true);
    items = await wyDetails(c, array(raw?.data?.itemList).map(row => scalar(row?.songId)).filter(Boolean)); name = '歌曲畅销指数榜';
  } else if (kind === 'user-recommendations') {
    const raw = await c.wy('/api/charts/user/rcmd/song/detail', { chartCode: 'USER_RCMD_SONG', period: '' }, true);
    items = array(raw?.data?.charts).map(row => wySong(row?.songData)); name = '全民热推榜';
  } else fail('UNSUPPORTED');
  result.name = name; result.chartKind = kind; result.count = items.length; result.total = items.length;
  result.sections.push(section(result.id, name, type, items, layout));
  return result;
}
