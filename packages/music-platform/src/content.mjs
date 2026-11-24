import { array, optionalNumber, record, scalar, text } from './model.mjs';

export function channelDescriptor({
  id = '', name = '', channelKind = 'unsupported', pageCode = '',
  cover = '', placement = '', available = true, display = true,
} = {}) {
  return {
    id: scalar(id),
    name: text(name),
    channelKind: text(channelKind) || 'unsupported',
    pageCode: text(pageCode),
    cover: text(cover),
    placement: text(placement),
    available: available !== false,
    display: display !== false,
  };
}

export function browseTags(items) {
  const seen = new Set(), result = [];
  for (const item of array(items)) {
    const row = record(item), id = scalar(row.id), name = text(row.name), group = text(row.group);
    if (!name) continue;
    const key = group + '\0' + id;
    if (seen.has(key)) continue;
    seen.add(key);
    result.push({ id, name, group, selected: row.selected === true });
  }
  return result;
}

function browsePreview(items) {
  return array(items).map(value => {
    const row = record(value);
    return {
      id: scalar(row.id),
      title: text(row.title),
      artist: text(row.artist),
      cover: text(row.cover),
      rank: optionalNumber(row.rank),
      lastRank: optionalNumber(row.lastRank),
      song: row.song ?? null,
    };
  });
}

export function browseCard(platform, {
  key = '', id = '', kind = 'unsupported', label = '', title = '', subtitle = '',
  contextText = '', cover = '', coverRef = null, action = null, song = null,
  presentation = 'static', display = true, available = true, canPlay = true,
  requiresAccount = false, errorCode = '', engine = '', creator = '',
  count = null, playCount = null, dayOfMonth = null, decoration = '', badge = '',
  coverLabel = '', preview = [],
  songCover = '', updateFrequency = '', playbackSource = null, locator = null,
  rankText = '', salesText = '',
} = {}) {
  return {
    key: text(key),
    id: scalar(id),
    kind,
    label: text(label),
    title: text(title),
    subtitle: text(subtitle),
    contextText: text(contextText),
    cover: text(cover),
    coverRef: coverRef ?? null,
    action: action ?? null,
    song: song ?? null,
    presentation: text(presentation) || 'static',
    display: display !== false,
    available: available !== false,
    canPlay: canPlay !== false,
    requiresAccount: requiresAccount === true,
    errorCode: text(errorCode),
    source: platform,
    engine: text(engine),
    creator: text(creator),
    count: optionalNumber(count),
    playCount: optionalNumber(playCount),
    dayOfMonth: optionalNumber(dayOfMonth),
    decoration: text(decoration),
    badge: text(badge),
    coverLabel: text(coverLabel),
    preview: browsePreview(preview),
    songCover: text(songCover),
    updateFrequency: text(updateFrequency),
    playbackSource: playbackSource ?? null,
    locator: locator ?? null,
    rankText: text(rankText),
    salesText: text(salesText),
  };
}

export function songCard(platform, item, overrides = {}) {
  const id = scalar(overrides.id) || scalar(item?.sources?.[0]?.musicInfo?.songmid);
  return browseCard(platform, {
    id,
    kind: 'song',
    title: text(item?.title),
    subtitle: text(item?.artist),
    cover: text(item?.cover_url),
    song: item ?? null,
    action: id ? { type: 'song', id } : null,
    ...overrides,
  });
}

export function browseSection({
  id, title = '', layout, items = [], tabs = [], selectedTab = '',
  action = null, tags = [], selectedTag = '', errorCode = '',
  avatars = [], requiresAccount = false,
}) {
  const sectionId = scalar(id);
  const withKeys = (cards, prefix) => array(cards).map((card, index) =>
    card.key ? card : { ...card, key: `${prefix}:${index}` });
  return {
    id: sectionId,
    title: text(title),
    layout,
    items: withKeys(items, sectionId),
    tabs: array(tabs).map((tab, index) => {
      const tabId = scalar(tab.id) || `${sectionId}:tab:${index}`;
      return { id: tabId, name: text(tab.name), items: withKeys(tab.items, tabId) };
    }),
    selectedTab: text(selectedTab),
    action: action ?? null,
    tags: browseTags(tags),
    selectedTag: text(selectedTag),
    errorCode: text(errorCode),
    avatars: array(avatars).map(text),
    requiresAccount: requiresAccount === true,
  };
}

export function browsePage({
  platform, id, title = '', sections = [], tags = [], selectedTag = '',
  continuation = null, ...extra
}) {
  return {
    ...extra,
    platform,
    id: text(id),
    title: text(title),
    sections: array(sections),
    tags: browseTags(tags),
    selectedTag: text(selectedTag),
    continuation: continuation ?? null,
  };
}
