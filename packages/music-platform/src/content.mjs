import { array, record, text, scalar, optionalNumber } from './model.mjs';

const browseOperations = new Set(['home', 'channel', 'artistActivity', 'charts']);
const layouts = new Set(['feature-rail', 'banner', 'playlist-rail', 'playlist-grid',
  'song-columns', 'song-list', 'artist-grid', 'channel-grid', 'ranking', 'ranking-small',
  'ranked-songs', 'artist-updates', 'vip-songs', 'vip-ranked-songs', 'album-ranking']);
const kinds = new Set(['song', 'playlist', 'album', 'artist', 'channel', 'chart',
  'daily', 'fm', 'similar', 'heartbeat', 'song-collection']);

function layout(section) {
  const value = text(section.layout), type = text(section.type);
  if (value === 'ranking' && type === 'albums') return 'album-ranking';
  if (layouts.has(value)) return value;
  if (type === 'banner') return 'banner';
  if (type === 'entries') return 'feature-rail';
  if (type === 'artists') return 'artist-grid';
  if (type === 'songs') return value === 'list' ? 'song-list' : 'song-columns';
  if (type === 'rankings') return value === 'TOP_3' ? 'ranking' : 'ranking-small';
  return value === 'grid' ? 'playlist-grid' : 'playlist-rail';
}

function channel(value) {
  const row = record(value);
  return { id: scalar(row.id), name: text(row.name), kind: 'channel',
    channelKind: text(row.channelKind) || (row.kind === 'channel' ? 'unsupported' : text(row.kind)),
    pageCode: text(row.pageCode), cover: text(row.cover), placement: text(row.placement),
    available: row.available !== false, display: row.display !== false };
}

function action(value, kind, id) {
  const row = record(value), type = text(row.type);
  if (['song', 'playlist', 'album', 'artist', 'chart'].includes(type))
    return scalar(row.id) ? { type, id: scalar(row.id) } : null;
  if (type === 'channel') return scalar(row.channel?.id) ?
    { type, channel: channel(row.channel) } : null;
  if (['daily', 'fm', 'similar', 'heartbeat', 'song-collection'].includes(type)) return row;
  if (['song', 'playlist', 'album', 'artist', 'chart'].includes(kind) && id) return { type: kind, id };
  return null;
}

function card(value, platform, key, sectionLayout, home) {
  const row = record(value);
  const standaloneSong = row.song == null && Array.isArray(row.sources);
  const song = standaloneSong ? row : row.song == null ? null : record(row.song);
  const declaredKind = song ? 'song' : text(row.action?.type) || text(row.kind);
  const kind = kinds.has(declaredKind) ? declaredKind : 'unsupported';
  const id = scalar(row.id) || scalar(row.action?.id) || scalar(song?.sources?.[0]?.musicInfo?.songmid);
  const title = standaloneSong ? text(row.title) : text(row.name) || text(row.title);
  const cover = standaloneSong ? text(row.cover_url) : text(row.cover);
  return {
    key: text(row.key) || key, id, kind,
    label: home && sectionLayout === 'feature-rail' ? text(row.label) || title : '',
    title, subtitle: standaloneSong ? text(row.artist) : text(row.subtitle), cover,
    coverRef: platform === 'wy' && kind === 'playlist' && id && sectionLayout !== 'banner'
      ? { kind: 'playlist', id } : null,
    action: action(row.action, kind, id), song,
    presentation: text(row.presentation) || 'static',
    display: row.display !== false, available: row.available !== false,
    canPlay: row.canPlay !== false,
    requiresAccount: row.requiresAccount === true, errorCode: text(row.errorCode),
    source: platform, engine: text(row.engine), creator: text(row.creator),
    count: optionalNumber(row.count), dayOfMonth: optionalNumber(row.dayOfMonth),
    decoration: text(row.decoration), badge: text(row.badge), coverLabel: text(row.coverLabel),
    recommendationReason: text(row.recommendationReason),
    preview: array(row.preview).map(value => {
      const preview = record(value);
      return { id: scalar(preview.id), title: text(preview.title), artist: text(preview.artist),
        cover: text(preview.cover), rank: optionalNumber(preview.rank),
        lastRank: optionalNumber(preview.lastRank), song: preview.song ?? null };
    }),
    previewSongs: array(row.previewSongs), songCover: text(row.songCover),
    updateFrequency: text(row.updateFrequency), playbackSource: row.playbackSource ?? null,
    locator: row.locator ?? null, rankText: text(row.rankText), salesText: text(row.salesText),
  };
}

function tags(value) {
  return array(value).map(value => {
    const row = record(value);
    return { id: scalar(row.id), name: text(row.name), group: text(row.group), selected: row.selected === true };
  });
}

function section(value, platform, key, home) {
  const row = record(value), id = scalar(row.id) || key, displayLayout = layout(row);
  return { id, title: text(row.title), layout: displayLayout,
    items: array(row.items).map((value, index) => card(value, platform, id + ':' + index, displayLayout, home)),
    tabs: array(row.tabs).map((value, index) => {
      const tab = record(value), tabID = scalar(tab.id) || id + ':tab:' + index;
      return { id: tabID, name: text(tab.name),
        items: array(tab.items).map((item, at) => card(item, platform, tabID + ':' + at, displayLayout, home)) };
    }),
    selectedTab: text(row.selectedTab), action: action(row.action),
    tags: tags(row.tags), selectedTag: text(row.selectedTag), errorCode: text(row.errorCode),
    avatars: array(row.avatars).map(text), requiresAccount: row.requiresAccount === true };
}

// Providers own upstream extraction. This projection publishes one native
// presentation contract; it never proves that a page contains enough content.
export function projectContent(platform, operation, value) {
  if (operation === 'channels') {
    const result = record(value);
    return { ...result, platform, items: array(result.items).map(channel) };
  }
  if (browseOperations.has(operation)) {
    const result = record(value), home = operation === 'home';
    const { entries, feed, sections, ...fields } = result;
    const groups = array(sections);
    const displaySections = array(entries).length ? [
      { id: text(result.id) + ':entries', title: '', layout: 'feature-rail', items: entries }, ...groups,
    ] : groups;
    return { ...fields, platform, id: text(result.id) || platform + ':' + operation,
      title: text(result.title) || text(result.name),
      sections: displaySections.map((value, index) => section(value, platform, 'section:' + index, home)),
      tags: tags([...array(result.tags), ...array(feed?.tags)]),
      selectedTag: text(result.selectedTag), continuation: result.continuation ?? null };
  }
  if (value && Array.isArray(value.items)) {
    return { ...value, items: value.items.map(item => {
      if (!item?.song || !Array.isArray(item.song.sources)) return item;
      const { song, ...position } = item;
      return { ...song, ...position };
    }) };
  }
  return value;
}
