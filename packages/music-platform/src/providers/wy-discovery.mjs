// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 jenfonro and iWebPlus contributors; derived from iWebPlayer.
// Data-only translation of the pinned discovery recipes; no URL/DSL execution.
import { array, decodeJSON, first, image, images, input, number, optionalNumber, protocolInteger, record, scalar, text, wySong } from '../model.mjs';
import { browseCard, browsePage, browseSection, browseTags, channelDescriptor, songCard } from '../content.mjs';
import { fail } from '../errors.mjs';
import { wyDetails } from './wy-details.mjs';

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
const fmMode = value => ({ DEFAULT: 'default', FAMILIAR: 'familiar', EXPLORE: 'explore',
  PUZZLE: 'puzzle', PUZZLE_MODE_RCMD: 'puzzle', SCENE: 'scene', SCENE_RCMD: 'scene' })[text(value).toUpperCase()] ?? 'default';
const supportedChannel = kind => ['featured', 'cube', 'playlists', 'rankings', 'new-songs', 'artists', 'artist-activity', 'vip'].includes(kind);
const descriptor = row => channelDescriptor({
  id: row.id, name: row.name, pageCode: row.pageCode ?? '',
  channelKind: row.kind, available: supportedChannel(row.kind), placement: row.placement ?? 'more',
});

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
    try {
      const id = decodeURIComponent(match[2]);
      return id ? { type: match[1], id } : null;
    } catch { return null; }
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
      return { type: 'channel', channel: channelDescriptor({ id: q('page'), channelKind: 'cube', pageCode: q('page') }) };
    if (kinds[component]) return { type: 'channel', channel: channelDescriptor({ id: kinds[component], channelKind: kinds[component] }) };
  }
  if (path === 'nm/mainTab/select' && q('tab') === 'music') {
    const id = scalar(first(q('musicTabCode'), q('subTab')));
    if (!id) return null;
    const channelKind = { feature: 'featured', playlist: 'playlists', chart: 'rankings',
      artist: 'artists', vip: 'vip', newsong: 'new-songs' }[id] ?? 'unsupported';
    return { type: 'channel', channel: channelDescriptor({ id, channelKind }) };
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

function entryCard(card, c, { key, home = false } = {}) {
  const type = card.action?.type;
  const requiresAccount = type === 'daily' || type === 'heartbeat';
  const display = !requiresAccount || Boolean(c.account);
  const presentation = ['playlist', 'daily', 'album'].includes(type) ? 'preview' :
    type === 'similar' ? 'overlay' : ['fm', 'heartbeat'].includes(type) ? 'animated' : 'static';
  return {
    ...card,
    key: key || card.key,
    label: home ? (card.label || card.title) : '',
    presentation,
    requiresAccount,
    display,
    available: Boolean(type) && type !== 'unsupported' && display,
    errorCode: display ? '' : 'AUTH_REQUIRED',
  };
}

function resource(r, { banner = false } = {}) {
  if (!r || typeof r !== 'object') return null;
  const ext = r.resourceExtInfo ?? {}, play = nested(r.playBtnData);
  const kind = text(first(r.resourceType, r.targetType, r.type, r.creativeType)).toLowerCase();
  if (/video|podcast|voice|djradio|djprogram|mv/.test(kind)) return null;
  const declared = action(first(r.action, r.url, r.orpheus, r.clickAction)) ?? action(play?.detailUrl);
  const id = scalar(first(r.rankId, r.resourceId, r.id, r.resource));
  let cardTitle = title(r), cardSubtitle = text(first(r.subTitle, r.subtitle)), contextText = '';
  if (text(r.singleLineTitle)) {
    const context = cardTitle;
    cardTitle = kind === 'dailysongs' && r.dayOfMonth != null ? cardTitle.split(/\s*\|\s*/)[0] :
      cardSubtitle || cardTitle;
    contextText = context === cardTitle ? '' : context;
    cardSubtitle = r.singleLineTitle;
  }
  const base = {
    id, title: cardTitle, subtitle: cardSubtitle, contextText, cover: cover(r, banner),
    badge: text(array(r.tags)[0]), engine: 'Netease',
  };
  const wrap = (card, toplist = false) => ({ card, toplist });

  if (kind === 'similarsong' || kind === 'similarartist' || declared?.type === 'similar') {
    return declared?.type === 'similar'
      ? wrap(browseCard('wy', { ...base, kind: 'similar', action: declared }))
      : wrap(browseCard('wy', { ...base, kind: 'unsupported', action: null }));
  }
  if (kind === 'star' || r.lunaItemType === 'heartRcmd') {
    const rawSong = first(ext.songData, ext.song, r.songData, r.songInfo);
    const songId = rawSong ? nativeSong(rawSong, cover(r)).sources[0]?.musicInfo?.songmid : '';
    return wrap(browseCard('wy', {
      ...base, kind: 'heartbeat',
      action: { type: 'heartbeat', entry: 'favorites', ...(songId ? { songId } : {}) },
    }));
  }
  if (ext.songData || ext.song || r.songData || r.songInfo || kind === 'song' || declared?.type === 'song') {
    const rawSong = first(ext.songData, ext.song, r.songData, r.songInfo) ?? { ...r, id: declared?.id ?? id };
    const normalized = nativeSong(rawSong, cover(r));
    const songId = normalized.sources[0]?.musicInfo.songmid ?? '';
    const source = action(play?.detailUrl);
    return wrap(browseCard('wy', {
      ...base,
      id: songId,
      kind: 'song',
      title: !banner && !base.title ? normalized.title : base.title,
      subtitle: !banner && !base.subtitle ? normalized.artist : base.subtitle,
      cover: !banner && !base.cover ? normalized.cover_url : base.cover,
      song: normalized,
      playbackSource: source?.type === 'playlist' ? source : null,
      action: songId ? { type: 'song', id: songId } : null,
    }));
  }
  if (kind === 'dailysongs' || declared?.type === 'daily') {
    return wrap(browseCard('wy', {
      ...base, kind: 'daily', dayOfMonth: optionalNumber(r.dayOfMonth),
      action: { type: 'daily', branch: declared?.branch ?? 'default' },
    }));
  }
  if (kind === 'fm' || declared?.type === 'fm') {
    const coverLabel = text(r.fmLogoTextContent);
    const explicitMode = text(r.mode) || declared?.mode, explicitSubmode = text(first(r.subMode, r.submode)) || declared?.submode || '';
    const fmAction = !explicitMode && !explicitSubmode && (scalar(r.ruleId) || scalar(r.tabTagId))
      ? { type: 'fm', provider: 'rule', ruleId: scalar(r.ruleId), tabTagId: scalar(r.tabTagId), sourceName: coverLabel || base.title }
      : { type: 'fm', mode: fmMode(explicitMode), submode: explicitSubmode };
    return wrap(browseCard('wy', { ...base, kind: 'fm', coverLabel, action: fmAction }));
  }
  if (['playlist', 'play_list', 'song_list', 'list', 'scroll_playlist', 'toplist'].includes(kind) || declared?.type === 'playlist') {
    const isToplist = kind === 'toplist';
    const playCount = optionalNumber(r.playCount);
    const pid = declared?.id ?? id;
    if (!pid) {
      const ids = first(play?.playAction?.songIds, r.songIds);
      if (!Array.isArray(ids) || !ids.length) {
        return wrap(browseCard('wy', {
          ...base, playCount, kind: isToplist ? 'unsupported' : 'playlist', action: null,
        }), isToplist);
      }
      return wrap(browseCard('wy', {
        ...base, id: 'songs:' + scalar(r.resourceId), engine: 'NeteaseCollection',
        kind: 'song-collection', count: ids.length, playCount,
        action: { type: 'song-collection', ids: ids.map(scalar) },
      }), isToplist);
    }
    let cardCover = base.cover, decoration = '';
    if (!banner && r.coverType === 'ALBUM_LOGO') {
      for (const value of [r.action, r.url, play?.detailUrl]) {
        try {
          const url = new URL(value), supplied = image(url.searchParams.get('coverUrl'));
          if (url.protocol === 'orpheus:' && url.hostname === 'playlist' && supplied && supplied !== cardCover) {
            decoration = cardCover; cardCover = supplied; break;
          }
        } catch { /* No optional cover decoration. */ }
      }
    }
    const preview = array(first(r.rankItems, r.trackRankList)).map(record).map(row => ({
      id: scalar(first(row.trackId, row.songId)),
      title: text(first(row.songName, row.title)),
      artist: text(first(row.artistName, row.subTitle)),
      cover: cover(row),
      rank: optionalNumber(row.rank),
      lastRank: optionalNumber(row.lastRank),
      song: first(row.trackId, row.songId) ? wySong({ ...row, id: first(row.trackId, row.songId),
        name: first(row.songName, row.title), artistName: first(row.artistName, row.subTitle), picUrl: cover(row) }) : null,
    }));
    return wrap(browseCard('wy', {
      ...base, id: pid, kind: 'playlist', cover: cardCover, decoration,
      coverRef: !banner ? { kind: 'playlist', id: pid } : null,
      count: optionalNumber(first(r.trackCount, r.songCount, ext.songCount)),
      playCount, creator: text(r.creator?.nickname), preview,
      songCover: image(r.songCoverImgUrl), updateFrequency: text(r.updateFrequency),
      action: { type: 'playlist', id: pid },
    }), isToplist);
  }
  if (['album', 'digitalbum', 'digitalalbum'].includes(kind) || declared?.type === 'album') {
    const albumId = declared?.id ?? id;
    return wrap(browseCard('wy', {
      ...base, id: albumId, kind: 'album', engine: 'NeteaseAlbum',
      count: optionalNumber(r.songCount),
      action: albumId ? { type: 'album', id: albumId } : null,
    }));
  }
  if (declared?.type === 'channel') {
    return wrap(browseCard('wy', { ...base, id: declared.channel.id, kind: 'channel', action: declared }));
  }
  if (kind === 'dragon_ball') {
    const selected = action(first(r.uiElement?.mainTitle?.action, r.action));
    if (selected) {
      const selectedId = selected.id || selected.channel?.id || id;
      return wrap(browseCard('wy', {
        ...base, id: selectedId, kind: selected.type,
        coverRef: !banner && selected.type === 'playlist' && selectedId ? { kind: 'playlist', id: selectedId } : null,
        action: selected,
      }));
    }
  }
  return wrap(browseCard('wy', { ...base, kind: 'unsupported', action: null }));
}

// These traversals read the displayed DSL's resource/component tree only.
function* leaves(node) {
  if (Array.isArray(node)) { for (const child of node) yield* leaves(child); return; }
  if (!node || typeof node !== 'object') return;
  if (node.resourceType != null || node.songInfo || node.songData || node.resourceExtInfo?.songData ||
      node.targetType != null || node.action && node.resourceId != null) { yield node; return; }
  for (const [key, value] of Object.entries(node))
    if (!['playBtn', 'playBtnData', 'playAll', 'playAction', 'clickAction', 'log', 'track', 'crossPlatformConfig',
      'dataGroupResourceList', 'commonResourceList'].includes(key)) yield* leaves(value);
}
function roots(value) {
  const data = nested(value);
  if (!data || typeof data !== 'object') return [];
  if (data.blockResource) return [data.blockResource];
  if (data.content?.items || data.resources || data.items) return [data];
  const candidates = Object.values(data).filter(value => value && typeof value === 'object' && !Array.isArray(value));
  const selected = candidates.filter(value => value.blockResource || value.header || value.block || value.resources || value.items || value.content?.items);
  return selected.map(value => value.blockResource ?? value);
}

function contentPage(raw, target, c, cursor = '') {
  const data = record(raw?.data ?? raw), blocks = array(data.blocks).map(record);
  const home = target === 'wy:home';
  const isHomeOp = home || target === 'wy:featured';
  const featureItems = [], sections = [], occurrences = new Map();
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
      const entrance = items.some(item => ['daily', 'heartbeat', 'fm', 'similar'].includes(item.card.action?.type)) ||
        native.some(value => value.resourceType === 'dragon_ball') ||
        nested(block.dslData)?.dslShowTitle === false && !text(block.showType).startsWith('PLAYLIST_SQUARE_');
      if (entrance && !banner) {
        featureItems.push(...items.map((item, index) => entryCard(item.card, c, { key: `${base}:entry:${ri}:${index}`, home: isHomeOp })));
        return;
      }
      const gridLayout = /MULTI.*GRID|FLAT|GRID/i.test(text(block.showType)) ? 'playlist-grid' : 'playlist-rail';
      let run, runLayout;
      for (const item of items) {
        const nextLayout = banner ? 'banner' : item.card.kind === 'song' ? 'song-columns' : item.toplist ? 'ranking-small' : gridLayout;
        if (!run || runLayout !== nextLayout) {
          const heading = block.hideTitle ? '' : text(first(group.title, group.header?.title, group.block?.title,
            block.uiElement?.subTitle?.title, block.uiElement?.mainTitle?.title));
          const sectionTitle = home && code === 'PAGE_RECOMMEND_MY_SHEET' && (nextLayout === 'playlist-rail' || nextLayout === 'playlist-grid')
            ? '最近常听' : heading;
          runLayout = nextLayout;
          run = {
            id: `${base}:${ri}:${sections.length}`,
            title: sectionTitle,
            layout: nextLayout,
            items: [],
            action: action(first(group.action, group.header?.action)),
          };
          sections.push(run);
        }
        run.items.push(item.card);
      }
    });
  });
  const finalSections = [
    ...(featureItems.length ? [browseSection({ id: `${target}:entries`, title: '', layout: 'feature-rail', items: featureItems })] : []),
    ...sections.map(row => browseSection(row)),
  ];
  const more = Boolean(data.hasMore);
  const continuation = more && scalar(data.cursor) ? { cursor: scalar(data.cursor),
    blockCodeOrderList: scalar(data.blockCodeOrderList),
    loadedPositionCodes: JSON.stringify(blocks.map(row => text(row.positionCode)).filter(Boolean)) } : null;
  return browsePage({ platform: 'wy', id: target, sections: finalSections, more, continuation });
}

async function content(c, params, kind, target) {
  const prev = params.continuation ?? {}, cursor = scalar(prev.cursor) || '0';
  const body = { pageCode: kind === 'home' ? 'HOME_RECOMMEND_PAGE' : 'HOME_DISCOVERY_PAGE',
    cursor: protocolInteger(cursor), isFirstScreen: cursor === '0', pageStyleType: 'noCutBlock', refresh: false };
  for (const key of ['blockCodeOrderList', 'loadedPositionCodes']) if (text(prev[key])) body[key] = prev[key];
  const result = contentPage(await c.wy('/api/link/page/rcmd/resource/show', body, false, { dataStatus: true }), target, c, cursor);
  if (result.continuation) {
    const before = array(nested(prev.loadedPositionCodes)), after = array(nested(result.continuation.loadedPositionCodes));
    result.continuation.loadedPositionCodes = JSON.stringify([...new Set([...before, ...after])]);
  }
  return result;
}

async function directory(c) {
  return c.cached(['channel-directory'], async fc => {
    const raw = await fc.wy('/api/link/position/show/resource', { positionCode: 'music_top_tab_full' }, false, { dataStatus: true });
    const data = record(raw?.data ?? raw), scene = data.generalizedSceneShow ?? data;
    const source = first(scene.generalizedMap?.nowChannelItems, data.nowChannelItems, data.channels);
    fc.observe('channels', source);
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
    fc => content(fc, { continuation }, params.featured ? 'featured' : 'home',
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
  const sections = [];
  for (const [index, group] of array(first(raw?.data, raw?.list)).map(record).entries()) {
    const items = array(group.list).map(record).map(row => {
      if (row.targetType === 'PLAYLIST') {
        const extracted = resource({ ...row, resourceType: 'playlist' });
        if (!extracted) return null;
        return { ...extracted.card, canPlay: row.canPlay == null || Boolean(row.canPlay) };
      }
      let chartKind = { DIGITAL_ALBUM: 'albums', USER_RCMD_SONG: 'user-recommendations' }[row.toplistCode] ?? '';
      try {
        const url = new URL(row.frontTargetUrl);
        if (url.protocol === 'https:' && url.hostname === 'music.163.com' && url.pathname === '/st/album/m/album/rank')
          chartKind = { 1: 'singles', 2: 'song-sales' }[url.searchParams.get('type')] ?? 'albums';
      } catch { /* Not a native destination. */ }
      return browseCard('wy', {
        id: scalar(row.toplistCode), title: title(row), cover: cover(row),
        kind: chartKind ? 'chart' : 'unsupported',
        action: chartKind ? { type: 'chart', id: chartKind } : null,
        preview: array(row.trackRankList).map(record).map(track => ({
          title: text(first(track.songName, track.itemName, track.title)),
          artist: text(track.artistName), cover: cover(track),
          rank: optionalNumber(track.rank), lastRank: optionalNumber(track.lastRank),
          song: first(track.trackId, track.songId) ? wySong({ ...track, id: first(track.trackId, track.songId),
            name: first(track.songName, track.itemName, track.title), picUrl: cover(track) }) : null,
        })),
        updateFrequency: text(row.updateFrequency), canPlay: false,
      });
    }).filter(Boolean);
    if (items.length) {
      const displayType = text(first(group.frontDisplayType, group.displayType)) || 'ONLY_COVER';
      sections.push(browseSection({
        id: `${target}:${text(group.categoryCode) || index}`,
        title: text(group.name),
        layout: displayType === 'TOP_3' ? 'ranking' : 'ranking-small',
        items,
      }));
    }
  }
  return browsePage({ platform: 'wy', id: target, sections, more: false, continuation: null });
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
  const pageSelectedTag = chosen ? groupId(chosen) : '';
  const pageTags = groups.map(row => ({ id: groupId(row), name: text(row.props?.title), selected: row === chosen })).filter(row => row.name);
  const sections = [];
  let feedRequest = null;

  function add(node, suffix, values, layout, heading = '', extra = {}) {
    const items = values.filter(Boolean);
    if (items.length) {
      sections.push(browseSection({
        id: `${target}:${text(node.id) || sections.length}:${suffix}`,
        title: heading, layout, items, ...extra,
      }));
    }
  }
  const componentCard = node => resource({ ...node.props,
    resourceType: node.componentName === 'Entrance' ? '' : text(node.componentName).toLowerCase() })?.card ?? null;

  function walk(node, heading = '') {
    node = record(node);
    if (groups.includes(node) && node !== chosen) return;
    const p = node.props ?? {}, children = array(node.children).map(record);
    if (p.songList?.ruleId != null) {
      feedRequest = {
        id: `${target}:feed:${text(node.id)}`,
        title: text(p.songList.title),
        ruleId: scalar(p.songList.ruleId),
        pointIds: array(p.songList.pointId).map(row => scalar(row?.id)).join(','),
        tabTagId: '',
        algTagIds: '',
      };
    }
    if (Array.isArray(p.carousel)) {
      add(node, 'banner', p.carousel.map(row => resource(row, { banner: true })?.card), 'banner');
      return;
    }
    if (Array.isArray(p.recommendModule)) {
      const tabs = p.recommendModule.map(record).map((group, i) => ({
        id: text(node.id) + ':group:' + i,
        name: text(group.title),
        selected: Boolean(group.isDefaultTab),
        items: array(group.modules).map((row, index) => {
          if (row == null) return null;
          row = record(row);
          const extracted = resource({ ...row, resourceType: row.type,
            ...(row.type === 'fm' && !row.mode && !row.ruleId && !row.tabTagId && !action(first(row.action, row.url, row.orpheus)) ?
              { tabTagId: p.tab } : {}) });
          return extracted ? entryCard(extracted.card, c, { key: `${target}:${text(node.id)}:entry:${i}:${index}` }) : null;
        }).filter(Boolean),
      })).filter(row => row.items.length);
      const tab = tabs.find(row => row.selected) ?? tabs[0];
      if (tab) {
        add(node, 'entries', tab.items, 'feature-rail', text(p.title),
          tabs.length > 1 ? { tabs: tabs.map(({ id, name, items }) => ({ id, name, items })), selectedTab: tab.id } : {});
      }
      return;
    }
    if (text(p.playlistIds)) {
      const names = text(p.titles).split(',');
      add(node, 'rankings', p.playlistIds.split(',').map((id, index) =>
        resource({ id, name: names[index] ?? '', resourceType: 'playlist' })?.card), 'ranked-songs', text(p.rankModuleTitle));
      return;
    }
    if (node.componentName === 'BillboardSingleSongList' && scalar(p.id)) {
      add(node, 'playlists', [resource({ id: p.id, name: p.cardTitle, subtitle: p.cardSubTitle, resourceType: 'playlist' })?.card],
        'playlist-rail', text(p.cardTitle));
      return;
    }
    if (p.albumData) {
      add(node, 'playlists', [resource({ ...p.albumData, resourceType: 'album' })?.card], 'playlist-rail', heading);
      return;
    }
    if (node.componentName === 'InfiniteScrollList') {
      const songs = [];
      function collectSongs(value) {
        value = record(value);
        if (value.componentName === 'Song') {
          const selected = componentCard(value);
          if (selected?.song) songs.push(selected);
        } else for (const child of array(value.children)) collectSongs(child);
      }
      for (const child of children.filter(row => ['Grid', 'Song'].includes(row.componentName))) collectSongs(child);
      feedRequest = null;
      add(node, 'songs', songs, 'song-list', text(p.listTitleStr));
      for (const child of children.filter(row => !['Grid', 'Song'].includes(row.componentName))) walk(child, heading);
      return;
    }
    if (['Grid', 'Scroll'].includes(node.componentName)) {
      const direct = ['Playlist', 'Song', 'Album', 'Entrance'];
      const cards = children.filter(row => direct.includes(row.componentName)).map(componentCard).filter(Boolean);
      add(node, 'songs', cards.filter(row => row.kind === 'song'), 'song-columns', heading);
      add(node, 'playlists', cards.filter(row => row.kind !== 'song'),
        node.componentName === 'Grid' ? 'playlist-grid' : 'playlist-rail', heading);
      for (const child of children.filter(row => !direct.includes(row.componentName))) walk(child);
      return;
    }
    for (const child of children) {
      if (child.componentName === 'Title') heading = text(first(child.props?.mainTitle, child.props?.title));
      else walk(child, heading);
    }
  }
  for (const node of pages) walk(node);
  return {
    page: browsePage({
      platform: 'wy', id: target, title: text(protocol.title), sections,
      tags: pageTags, selectedTag: pageSelectedTag, more: false, continuation: null,
    }),
    feedRequest,
  };
}

export function wyChannelPage(c, params) {
  const id = input(params.id), prev = record(params.continuation), selectedTag = text(params.tag ?? prev.tag ?? '');
  return c.cached(['channel', id, text(params.pageCode), selectedTag, prev], fc => readChannelPage(fc, params));
}

async function readChannelPage(c, params) {
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
    case 'rankings': result = rankings(await c.wy('/api/toplist/detail/v2', {}, true,
      { dataStatus: raw => first(raw.data) !== undefined || first(raw.list) === undefined }), target); break;
    case 'new-songs': {
      const raw = await c.wy('/api/v1/discovery/new/songs', { areaId: 0, total: true }, true, { dataStatus: true });
      result = browsePage({
        platform: 'wy', id: target, title: channel.name,
        sections: [browseSection({ id: `${target}:songs`, title: channel.name, layout: 'song-list',
          items: array(raw?.data).map(row => songCard('wy', wySong(row))) })],
        more: false, continuation: null,
      });
      break;
    }
    case 'playlists': {
      const tag = selectedTag || '推荐';
      result = contentPage(await c.wy('/api/playlist/square/block/page', { categoryName: tag, offset: 0, limit: 20,
        ...(prev.cursor == null ? {} : { cursor: scalar(prev.cursor) }) }, true, { dataStatus: true }), target, c, scalar(prev.cursor));
      result.tags = browseTags(['推荐', '官方', '华语', '摇滚', '民谣', '电子', '轻音乐'].map(name =>
        ({ id: name, name, group: 'playlist', selected: name === tag })));
      result.selectedTag = tag;
      if (result.continuation) result.continuation.tag = tag;
      break;
    }
    case 'artists': {
      const parts = input(selectedTag || '-1:-1:-1').split(':');
      if (parts.length !== 3) fail('INVALID_REQUEST');
      const values = parts.map(value => number(value, undefined, 'INVALID_REQUEST'));
      const [area, type, initial] = values, offset = number(prev.offset, 0, 'INVALID_REQUEST');
      const raw = await c.wy('/api/v1/artist/list', { area, type, initial, offset, limit: 40, total: true }, true,
        { dataStatus: raw => first(raw.data?.artists, raw.data?.more) !== undefined || first(raw.artists) === undefined });
      const data = record(raw?.data ?? raw), source = array(first(data.artists, raw?.artists)), more = Boolean(first(data.more, raw?.more));
      const items = source.map(record).map(row => {
        const artistId = scalar(first(row.id, row.artistId));
        return browseCard('wy', {
          id: artistId, kind: 'artist', title: title(row), cover: cover(row),
          subtitle: optionalNumber(first(row.musicSize, row.songCount)) == null ? '' : '单曲：' + first(row.musicSize, row.songCount),
          action: artistId ? { type: 'artist', id: artistId } : null,
        });
      });
      result = browsePage({
        platform: 'wy', id: target, title: channel.name,
        sections: [browseSection({ id: `${target}:artists`, title: '', layout: 'artist-grid', items })],
        tags: artistTags(values), selectedTag: values.join(':'), more,
        continuation: more ? { offset: offset + 40, tag: values.join(':') } : null,
      });
      break;
    }
    case 'cube': {
      const offset = number(prev.offset, 0, 'INVALID_REQUEST');
      const pageCode = input(channel.pageCode);
      const protocolRaw = await c.cached(['cube-protocol', pageCode], fc => fc.wy('/api/cube/render/page/protocol',
        { pageCode, scene: 'music' }, true,
        { dataStatus: raw => first(raw.data?.pageProtocol) !== undefined || first(raw.pageProtocol) === undefined }));
      const parsed = cube(protocolRaw, target, selectedTag, c);
      result = parsed.page;
      const feedRequest = parsed.feedRequest;
      if (feedRequest) {
        const ruleId = (offset > 0 && scalar(prev.ruleId)) || feedRequest.ruleId;
        const pointIds = offset > 0 && prev.pointIds != null ? text(prev.pointIds) : feedRequest.pointIds;
        const layoutTag = result.tags.some(row => row.id === selectedTag);
        const algTagIds = offset > 0 && prev.algTagIds != null ? text(prev.algTagIds) : (layoutTag ? '' : selectedTag);
        let feedTags = [];
        try {
          feedTags = await c.cached(['cube-feed-tags', ruleId], async fc => {
            const tagData = await fc.wy('/api/tag/tab/tags', { ruleId, ruleType: 1, tabTagId: '' }, false,
              { dataStatus: raw => first(raw.data?.algTagInfos) !== undefined || first(raw.algTagInfos) === undefined });
            return array(first(tagData?.data?.algTagInfos, tagData?.algTagInfos)).map(record).flatMap(row => array(row.children).length ?
              row.children.map(record).map(child => ({ id: scalar(child.algTagId), name: text(child.algTagName), group: text(row.algTagName) })) :
              [{ id: scalar(row.algTagId), name: text(row.algTagName), group: '' }]);
          });
        } catch (error) {
          c.signal.throwIfAborted();
          if (['AUTH_REQUIRED', 'AUTH_EXPIRED', 'INVALID_CREDENTIALS', 'CANCELLED', 'CLOSED', 'STALE_SESSION', 'TIMEOUT']
            .includes(c.errorCode(error))) throw error;
          // Filter metadata is optional; it is not authentication or song data.
        }
        if (feedTags.length) feedTags.unshift({ id: result.selectedTag, name: '全部歌曲', group: '' });
        const feedSelectedTag = feedTags.length ? (selectedTag || result.selectedTag) : '';
        if (offset > 0) result.sections = [];
        result.tags = browseTags([...result.tags, ...feedTags]);
        let feedItems = [], feedErrorCode = '';
        try {
          const feed = await c.wy('/api/tag/tab/alg/recommends', { ruleType: 1, ruleId, tabTagId: '',
            algTagIds, extMap: JSON.stringify({ pointIds }),
            limit: 30, offset, trialMode: 103, source: offset > 0 ? 'nextPage' : 'reset' }, false, { dataStatus: true });
          const data = record(feed?.data ?? feed);
          c.observe('feed.songs', data.songs);
          feedItems = array(data.songs).map(row => songCard('wy', wySong(row)));
          const more = Boolean(data.hasMore);
          result.more = more;
          result.continuation = more ? { offset: offset + 30, tag: selectedTag,
            ruleId, pointIds, algTagIds } : null;
        } catch (error) {
          c.signal.throwIfAborted();
          if (offset > 0 || ['AUTH_REQUIRED', 'AUTH_EXPIRED', 'INVALID_CREDENTIALS', 'CANCELLED', 'CLOSED', 'STALE_SESSION', 'TIMEOUT']
            .includes(c.errorCode(error))) throw error;
          feedErrorCode = c.errorCode(error);
          result.more = false; result.continuation = null;
        }
        result.sections.push(browseSection({
          id: feedRequest.id, title: feedRequest.title, layout: 'song-list',
          items: feedItems, tags: feedTags, selectedTag: feedSelectedTag, errorCode: feedErrorCode,
        }));
      }
      break;
    }
    case 'vip': {
      const sections = [];
      const reads = await Promise.allSettled([
        c.wy('/api/vipnewcenter/app/viptab/recommend/song/list', {}, false,
          { dataStatus: raw => first(raw.data?.songInfoList) !== undefined || text(raw.data?.title) !== '' || first(raw.songInfoList) === undefined }),
        c.wy('/api/toplist/detail/v2', {}, true,
          { dataStatus: raw => first(raw.data) !== undefined || first(raw.list) === undefined }),
      ]);
      c.signal.throwIfAborted();
      const failures = reads.filter(row => row.status === 'rejected');
      const fatal = failures.find(row => !['INVALID_RESPONSE', 'UPSTREAM_HTTP', 'UPSTREAM_ERROR', 'NETWORK_ERROR', 'NOT_FOUND']
        .includes(c.errorCode(row.reason)));
      if (fatal) throw fatal.reason;
      if (failures.length === reads.length) throw failures[0].reason;
      if (reads[0].status === 'fulfilled') {
        const raw = reads[0].value, data = record(raw?.data ?? raw);
        const items = array(first(data.songInfoList, raw?.songInfoList)).map(row => songCard('wy', wySong(row)));
        if (items.length) sections.push(browseSection({
          id: `${target}:recommend`, title: text(data.title) || '黑胶VIP 每日推荐', layout: 'vip-songs', items,
        }));
      } else {
        sections.push(browseSection({
          id: `${target}:recommend`, title: '黑胶VIP 每日推荐', layout: 'vip-songs', items: [],
          errorCode: c.errorCode(reads[0].reason),
        }));
      }
      if (reads[1].status === 'fulfilled') {
        const items = rankings(reads[1].value, target).sections.flatMap(row => row.items).filter(row => /^黑胶(?:SVIP|VIP)/i.test(row.title));
        if (items.length) sections.push(browseSection({
          id: `${target}:rankings`, title: '黑胶VIP 排行榜', layout: 'vip-ranked-songs', items,
        }));
      } else {
        sections.push(browseSection({
          id: `${target}:rankings`, title: '黑胶VIP 排行榜', layout: 'vip-ranked-songs', items: [],
          errorCode: c.errorCode(reads[1].reason),
        }));
      }
      result = browsePage({ platform: 'wy', id: target, sections, more: false, continuation: null });
      break;
    }
    default: fail('UNSUPPORTED');
  }
  result.channel = descriptor(channel); result.title ||= channel.name;
  return result;
}

export function wyCharts(c, { kind, period = 'daily', year }) {
  const chartKind = input(kind);
  return c.cached(['charts', chartKind, text(period), year ?? null],
    fc => readCharts(fc, { kind: chartKind, period, year }));
}

async function readCharts(c, { kind, period = 'daily', year }) {
  const chartId = 'chart:' + input(kind);
  let items, name, layout = 'song-list';
  if (kind === 'albums' || kind === 'singles') {
    const route = { daily: 'daily', weekly: 'week', yearly: 'year', overall: 'total' }[period];
    if (!route) fail('UNSUPPORTED');
    let resolvedYear;
    if (period === 'yearly') {
      resolvedYear = year == null ? new Date(Date.now()).getUTCFullYear() : number(year, undefined, 'INVALID_REQUEST');
      if (!Number.isInteger(resolvedYear) || resolvedYear < 1900 || resolvedYear > 2200) fail('INVALID_REQUEST');
    }
    const raw = await c.wy('/api/feealbum/songsaleboard/' + route + '/type' + (period === 'yearly' ? '/' : ''),
      { albumType: kind === 'albums' ? 0 : 1, limit: 50, ...(period === 'yearly' ? { year: resolvedYear } : {}) }, true);
    items = array(raw?.products).map(record).map((row, index) => {
      const albumId = scalar(row.albumId);
      return browseCard('wy', {
        id: albumId, kind: 'album', title: text(row.albumName), cover: image(row.coverUrl),
        subtitle: text(row.artistName), rankText: String(index + 1),
        salesText: optionalNumber(row.saleNum) == null ? '' : Number(row.saleNum).toLocaleString('en-US') + ' 张',
        action: albumId ? { type: 'album', id: albumId } : null,
      });
    });
    name = kind === 'albums' ? '数字专辑畅销榜' : '数字单曲榜';
    layout = 'album-ranking';
  } else if (kind === 'song-sales') {
    const raw = await c.wy('/api/single/sales-ranking/get', {}, true, { dataStatus: true });
    items = (await wyDetails(c, array(raw?.data?.itemList).map(row => scalar(row?.songId)).filter(Boolean)))
      .map(song => songCard('wy', song));
    name = '歌曲畅销指数榜';
  } else if (kind === 'user-recommendations') {
    const raw = await c.wy('/api/charts/user/rcmd/song/detail', { chartCode: 'USER_RCMD_SONG', period: '' }, true,
      { dataStatus: true });
    items = array(raw?.data?.charts).map(row => songCard('wy', wySong(row?.songData)));
    name = '全民热推榜';
  } else fail('UNSUPPORTED');
  return browsePage({
    platform: 'wy', id: chartId, title: name, chartKind: kind,
    count: items.length, total: items.length, more: false, continuation: null,
    sections: [browseSection({ id: chartId, title: name, layout, items })],
  });
}
