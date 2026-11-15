// SPDX-License-Identifier: GPL-3.0-only
// Fixed NetEase recipes from api-enhanced / iWebPlus / meowmusic; see LICENSES/NOTICE.md.
import { collection, encodeProtocolJSON, first, image, input, mediaURL, number, optionalNumber, page, pageNumber, protocolInteger, required, rows, scalar, text, wySong } from '../model.mjs';
import { fail } from '../errors.mjs';
import { randomInt } from 'node:crypto';
import { wyHome, wyChannels, wyChannelPage, wyCharts } from './wy-discovery.mjs';
import { wySearchDiscovery } from './wy-search-discovery.mjs';

const levelMap = { '128k': 'standard', '192k': 'higher', '320k': 'exhigh', flac: 'lossless',
  flac24bit: 'hires', master: 'jymaster', jyeffect: 'jyeffect', dolby: 'dolby', sky: 'sky', vivid: 'vivid' };
const integerArray = values => {
  if (!Array.isArray(values)) fail('INVALID_REQUEST');
  return encodeProtocolJSON(values.map(protocolInteger));
};
export async function wyDetails(c, ids) {
  const result = [];
  for (let at = 0; at < ids.length; at += 100) {
    const requested = ids.slice(at, at + 100);
    const data = await c.wy('/api/v3/song/detail', { c: encodeProtocolJSON(requested.map(id => ({ id: protocolInteger(id) }))) }, true);
    const values = rows(data.songs).map(wySong), known = new Map(values.map(song => [idOf(song), song]));
    result.push(...requested.flatMap(id => known.has(id) ? [known.get(id)] : []),
      ...values.filter(song => !requested.includes(idOf(song))));
  }
  return result;
}
function idOf(song) { return song.sources[0].musicInfo.songmid; }
async function wyBatch(c, kind, source, context, extra = {}, algorithms = {}, feedbackSources = {}, defaultSource = '', known = new Map()) {
  const items = source.map(wySong), missing = new Set(items
    .filter(song => !song.title.trim() || !song.artist.trim()).map(idOf));
  const fetched = new Map((await wyDetails(c, [...missing].filter(id => !known.has(id)))).map(song => [idOf(song), song]));
  const metadata = new Map([...known, ...fetched]);
  for (let index = 0; index < items.length; index++)
    if (missing.has(idOf(items[index])) && metadata.has(idOf(items[index])))
      items[index] = metadata.get(idOf(items[index]));
  const originalIds = new Set(items.map(idOf));
  // A canonical record is still a real result, but metadata cannot manufacture
  // the recommendation attribution of the requested alias.
  for (const [id, song] of fetched) if (!originalIds.has(id)) items.push(song);
  return { platform: 'wy', kind, items, count: items.length, empty: items.length === 0,
    context, recommendations: items.map((song, index) => {
      const raw = source[index] ?? {}, nested = first(raw.songInfo, raw.songData, raw.song) ?? {}, id = idOf(song);
      const feedbackSource = feedbackSources?.[id] ?? raw.source ?? nested.source ?? defaultSource;
      const feedbackSourceId = first(raw.sourceId, nested.sourceId);
      return { id, alg: text(algorithms?.[id] ?? raw.alg ?? nested.alg), source: kind,
        ...(feedbackSource ? { feedbackSource: text(feedbackSource) } : {}),
        ...(feedbackSourceId == null ? {} : { feedbackSourceId: scalar(feedbackSourceId) }) };
    }),
    fetchedAt: c.clock(), expiresAt: null, ...extra };
}
export async function wyPlaylist(c, { id, page: current = 1, limit = 100 }, complete = false) {
  id = input(id); current = pageNumber(current); limit = pageNumber(limit, 100);
  return c.cached(['playlist', id, current, limit, complete], async () => {
    const data = await c.wy('/api/v6/playlist/detail', { id, n: complete ? 100 : limit, s: 8 });
    const list = data.playlist, info = collection('wy', list);
    const tracks = rows(list.tracks ?? []), ids = rows(list.trackIds ?? tracks).map(row => required(row.id));
    const members = complete ? ids : ids.slice((current - 1) * limit, current * limit);
    const embedded = tracks.map(wySong);
    const known = new Map(embedded.map(song => [idOf(song), song]));
    const missing = members.filter(id => !known.has(id) || !known.get(id).title.trim() || !known.get(id).artist.trim());
    const fetched = await wyDetails(c, missing);
    for (const song of fetched) known.set(idOf(song), song);
    const offset = complete ? 0 : (current - 1) * limit;
    const available = members.flatMap((id, index) => known.has(id) ?
      [{ song: known.get(id), position: offset + index }] : []);
    // Canonical records returned for aliases remain actual records. Do not rename
    // them to the requested ID, discard them, or invent a membership association.
    const requested = new Set(ids);
    const aliases = new Map((complete || current === 1 ? [...embedded, ...fetched] : fetched)
      .filter(song => !requested.has(idOf(song))).map(song => [idOf(song), song]));
    available.push(...[...aliases.values()].map(song => ({ song, position: null })));
    const items = available.map(row => row.song);
    return page('wy', items, { page: current, limit }, { ...info, total: number(list.trackCount, ids.length),
      more: !complete && current * limit < ids.length, consumed: members.length,
      memberIds: members, sourcePositions: available.map(row => row.position),
      description: text(list.description), complete });
  }, 3600000);
}
async function favorite(c) {
  const account = c.accountRequired();
  for (let offset = 0;;) {
    const data = await c.wy('/api/user/playlist', { uid: account.userId, limit: 100, offset, includeVideo: true });
    const lists = rows(data.playlist);
    const list = lists.find(row => scalar(row.creator?.userId) === account.userId && Number(row.specialType) === 5);
    if (list) return collection('wy', list);
    if (!data.more) fail('NOT_FOUND');
    if (!lists.length) fail('INVALID_RESPONSE'); // This offset protocol has no next position.
    offset += lists.length;
  }
}
export const wyProvider = {
  async search(c, { keyword, kind = 'songs', page: current = 1, limit = 30, pages = 1 }) {
    keyword = input(keyword); current = pageNumber(current); limit = pageNumber(limit, 30); pages = pageNumber(pages);
    const songs = kind === 'songs', offset = (current - 1) * limit, count = limit * pages;
    const data = await c.wy('/api/cloudsearch/pc', { s: keyword, type: songs ? 1 : 1000, limit: count, offset, total: true });
    const result = data.result, total = number(result[songs ? 'songCount' : 'playlistCount']);
    return page('wy', rows(result[songs ? 'songs' : 'playlists'] ?? []).map(songs ? wySong : row => collection('wy', row)),
      { page: current, limit: count }, { total, more: result.hasMore == null ? offset + count < total : Boolean(result.hasMore),
        next: current + pages, ...(pages > 1 ? { offset } : {}) });
  },
  async song(c, { id }) {
    id = input(id);
    return c.cached(['song', id], async () => {
      const values = await wyDetails(c, [id]);
      if (!values.length) fail('NOT_FOUND');
      return values[0];
    }, 3600000);
  },
  playlist: (c, params) => wyPlaylist(c, params),
  'playlist.complete': (c, params) => wyPlaylist(c, params, true),
  async 'songs'(c, { ids }) {
    if (!Array.isArray(ids)) fail('INVALID_REQUEST');
    return { platform: 'wy', items: await wyDetails(c, ids.map(input)), more: false };
  },
  async 'playlists'(c, { ids }) {
    if (!Array.isArray(ids)) fail('INVALID_REQUEST');
    const data = await c.wy('/api/tag/tab/playlists', { ids: ids.map(input).join(',') });
    return { platform: 'wy', items: rows(first(data.data, data.playlists)).map(row => collection('wy', row)) };
  },
  async artistActivity(c, { before }) {
    if (!c.account) return { platform: 'wy', entries: [], sections: [], more: false, continuation: null };
    const data = await c.wy('/api/sub/artist/new/works/song-mv/list/v2', {
      limit: 10, sourceType: 1, firstRequest: before == null,
      startTimestamp: before == null ? c.clock() : input(before),
    });
    const body = data.data ?? data, works = rows(body.newWorks ?? []);
    const items = [], avatars = [];
    for (const [index, work] of works.entries()) {
      const info = work.info, meta = info?.blockTitle ?? {};
      if (!['song', 'album'].includes(info?.blockType)) continue;
      const song = info.songLists?.[0], albumId = scalar(first(info.albumId, song?.al?.id));
      const artist = text(meta.artistName) || (song?.ar ?? []).map(row => text(row.name)).join(' / ');
      if (albumId && albumId !== '0') {
        const count = optionalNumber(info.albumSongCount);
        items.push({ id: albumId, key: 'artist-activity:' + index, source: 'wy', kind: 'album',
          name: text(first(meta.resourceName, song?.al?.name)), creator: artist, subtitle: artist,
          cover: image(first(meta.resourcePicUrl, song?.al?.picUrl)), count,
          coverLabel: count == null ? '' : count === 1 ? '新单曲' : '新专辑',
          action: { type: 'album', id: albumId } });
      } else if (song) {
        items.push({ key: 'artist-activity:' + index, song: wySong(song), coverLabel: '新单曲',
          action: { type: 'song', id: scalar(song.id) } });
      }
      const avatar = image(meta.imgUrl);
      if (avatar && !avatars.includes(avatar)) avatars.push(avatar);
    }
    const next = works.at(-1)?.publishTime, more = Boolean(body.hasMore);
    if (more && (next == null || scalar(next) === scalar(before))) fail('INVALID_RESPONSE');
    return { platform: 'wy', entries: [], sections: items.length ? [{
      id: 'artist-activity:works', title: text(body.title) || '你关注的艺人新动向', type: 'artist-activity',
      layout: 'artist-updates', items, avatars, action: { type: 'channel', channel: {
        id: 'artist-activity', name: text(body.title) || '你关注的艺人新动向', kind: 'artist-activity' } },
    }] : [], consumed: works.length, more, continuation: more ? { before: scalar(next) } : null };
  },
  async 'heartbeat.seed'(c) {
    c.accountRequired();
    const list = await favorite(c);
    const result = await wyPlaylist(c, { id: list.id, limit: 1 });
    return { platform: 'wy', song: result.items[0] ?? null };
  },
  async searchHotPlayback(c, { keywords }) {
    if (!Array.isArray(keywords)) fail('INVALID_REQUEST');
    const data = await c.wy('/api/hot/search/relate/song/get', { keywords: JSON.stringify(keywords.map(input)) });
    return { platform: 'wy', items: rows(data.data?.songs).map(row => wySong(row.song)) };
  },
  home: wyHome,
  channels: wyChannels,
  channel: wyChannelPage,
  charts: wyCharts,
  searchDiscovery: wySearchDiscovery,
  async album(c, { id }) {
    id = input(id);
    return c.cached(['album', id], async () => {
      const data = await c.wy('/api/v1/album/' + encodeURIComponent(id), {});
      const album = data.album, items = rows(data.songs ?? []).map(wySong);
      return { platform: 'wy', id: required(album.id), name: text(album.name), cover: image(album.picUrl),
        creator: text(album.artist?.name), description: text(album.description), items, total: items.length, more: false };
    }, 3600000);
  },
  async artist(c, { id }) {
    id = input(id);
    return c.cached(['artist', id], async () => {
      const data = await c.wy('/api/v1/artist/' + encodeURIComponent(id), {});
      const artist = data.artist;
      return { platform: 'wy', id: required(artist.id), name: text(artist.name), cover: image(artist.picUrl),
        description: text(artist.briefDesc), items: rows(data.hotSongs ?? []).map(wySong), more: false };
    }, 3600000);
  },
  async personalPlaylists(c, { kind = 'all', page: current = 1, limit = 30, continuation }) {
    current = pageNumber(current); limit = pageNumber(limit, 30);
    const account = c.accountRequired(), offset = number(continuation?.offset, (current - 1) * limit, 'INVALID_REQUEST');
    const data = await c.wy('/api/user/playlist', { uid: account.userId, offset, limit, includeVideo: true });
    const source = rows(data.playlist), items = source.filter(row => kind === 'all' ||
      (scalar(row.creator?.userId) === account.userId) === (kind === 'created')).map(row => {
      const ownerId = scalar(row.creator?.userId), owned = ownerId === account.userId, special = Number(row.specialType ?? 0);
      return { ...collection('wy', row), ownerId, owned, subscribed: Boolean(row.subscribed),
        writable: owned && (special === 0 || special === 5),
        kind: special === 5 ? 'favorites' : special === 0 ? 'playlist' : 'special' };
    });
    const more = Boolean(data.more);
    if (more && !source.length) fail('INVALID_RESPONSE');
    return { ...page('wy', items, { page: current, limit }, { total: number(data.total, 0), more, consumed: source.length }),
      continuation: more ? { page: current + 1, offset: offset + source.length } : null };
  },
  async daily(c, { branch = 'default' }) {
    c.accountRequired();
    return c.cached(['daily', branch], async () => {
      const data = await c.xeapi(branch === 'style' ? '/api/homepage/category/daily/song/list' : '/api/v3/discovery/recommend/songs',
        branch === 'style' ? {} : { ispush: false });
      const body = data.data ?? data;
      const source = rows(first(body.dailySongs, data.recommend));
      const local = new Date(c.clock() + 8 * 3600000), next = Date.UTC(local.getUTCFullYear(), local.getUTCMonth(), local.getUTCDate(), 6) - 8 * 3600000;
      const expiry = branch === 'style' ? c.clock() + 60000 : next > c.clock() ? next : next + 86400000;
      return wyBatch(c, 'daily', source, { branch }, { id: 'daily:' + branch, branch,
        name: branch === 'style' ? '风格推荐' : '每日推荐', collectionType: 'daily', source: 'wy',
        engine: 'NeteaseCollection', more: false, cover: image(source[0]?.al?.picUrl), expiresAt: expiry,
        category: scalar(body.tags?.categoryId),
        tags: (body.tags?.tagVOList ?? []).map(tag => ({ id: scalar(first(tag.id, tag.tagId)), name: text(first(tag.name, tag.tagName)) })) },
      {}, {}, branch === 'style' ? '' : 'dailySongRecommend');
    }, branch === 'style' ? 60000 : 600000);
  },
  async fm(c, { mode = 'default', submode = '', preview = false, ruleId = '', tabTagId = '', unplaySongIds = [] }) {
    if (ruleId || tabTagId) {
      const params = { ruleType: 1, ruleId, tabTagId, firstSongId: '', firstSongAlg: '', ...(preview ? {} : { limit: 5 }) };
      const data = await c.wy(preview ? '/api/tag/tab/fm/cover' : '/api/tag/tab/fm/recommends', params);
      return preview ? { platform: 'wy', kind: 'fm-preview', preview: true, cover: image(data.data?.cover),
        context: { provider: 'rule', ruleId, tabTagId } } : wyBatch(c, 'fm', rows(data.data), { provider: 'rule', ruleId, tabTagId });
    }
    const protocolMode = { default: 'DEFAULT', familiar: 'FAMILIAR', explore: 'EXPLORE', puzzle: 'PUZZLE_MODE_RCMD', scene: 'SCENE_RCMD' }[mode] ?? input(mode);
    const data = await c.wy('/api/v1/radio/get', { mode: protocolMode, subMode: submode, limit: preview ? '1' : '3',
      openAidj: 'false', aidjReqTimes: '0', unplaySongIds: integerArray(unplaySongIds) });
    return wyBatch(c, 'fm', rows(Array.isArray(data.data) ? data.data : data.data?.songs),
      { mode: protocolMode, submode, preview }, {}, {}, {}, 'userfm');
  },
  async similar(c, { sourceIds, sourceType = 'song', unplaySongIds = [] }) {
    if (!Array.isArray(sourceIds) || !sourceIds.length) fail('INVALID_REQUEST');
    const data = await c.wy('/api/v1/radio/get', { limit: '3', mode: 'SIMILAR', sourceType,
      sourceIds: integerArray(sourceIds), unplaySonIds: integerArray(unplaySongIds) }, true);
    return wyBatch(c, 'similar', rows(Array.isArray(data.data) ? data.data : data.data?.songs), { sourceIds, sourceType });
  },
  async heartbeat(c, { entry = 'favorites', songId, playlistId, continuation }) {
    const account = c.accountRequired();
    let count;
    if (continuation) count = number(continuation.count, 0, 'INVALID_REQUEST');
    const listId = continuation?.playlistId ?? playlistId ?? (await favorite(c)).id;
    const list = continuation ? null : await wyPlaylist(c, { id: listId }, true);
    const known = new Map((list?.items ?? []).map(song => [idOf(song), song]));
    const choices = (list?.items ?? []).filter(song => song.title.trim() && song.artist.trim());
    const seed = continuation?.startMusicId ?? songId ?? (choices.length ? idOf(choices[randomInt(choices.length)]) : null);
    if (!seed) fail('NOT_FOUND');
    if (!continuation) count = list.memberIds.length;
    const currentSong = input(songId ?? seed), fromHome = entry === 'favorites';
    const data = await c.wy('/api/playmode/intelligence/list', { songId: currentSong, playlistId: input(listId),
      startMusicId: seed, count, type: fromHome ? 'fromPlayAll' : 'fromPlayOne' });
    const source = rows(data.data);
    const result = await wyBatch(c, 'heartbeat', source,
      { entry, playlistId: listId, startMusicId: seed, songId: currentSong, fromHome, count, accountId: account.userId },
      {}, {}, {}, '', known);
    result.recommendations = result.recommendations.map(row => ({ ...row, sourceId: listId, seedId: seed }));
    if (!continuation) {
      const index = result.items.findIndex(song => idOf(song) === currentSong);
      if (index >= 0) {
        result.items.unshift(...result.items.splice(index, 1));
        result.recommendations.unshift(...result.recommendations.splice(index, 1));
      } else {
        const selected = known.get(currentSong) ?? (await wyDetails(c, [currentSong]))[0];
        if (!selected) fail('NOT_FOUND');
        result.items.unshift(selected);
        result.recommendations.unshift({ id: idOf(selected), alg: '', source: 'heartbeat',
          sourceId: listId, seedId: seed, feedbackSource: '', feedbackSourceId: '' });
      }
    }
    result.count = result.items.length; result.empty = !result.count;
    result.entrySong = result.items.find(song => idOf(song) === currentSong) ?? known.get(currentSong) ?? null;
    result.continuation = { playlistId: listId, startMusicId: seed, accountId: account.userId, count };
    return result;
  },
  async queueRecommendations(c, { seedSongId, offset = 0 }) {
    c.accountRequired();
    offset = number(offset, 0, 'INVALID_REQUEST');
    const data = await c.wy('/api/homepage/scene/more/rcmd/song', { currentPlaySongId: input(seedSongId),
      songIds: integerArray([seedSongId]), songCount: '1', offset: scalar(offset),
      source: 'curlist_more_rcmd', extJson: '{}' });
    const map = value => {
      try { return typeof value === 'string' ? JSON.parse(value) : value; }
      catch { fail('INVALID_RESPONSE'); }
    };
    const next = number(required(data.data.offset)), rawMore = data.data.hasMore;
    const more = typeof rawMore === 'boolean' ? rawMore : Boolean(number(required(rawMore)));
    if (more && next <= offset) fail('INVALID_RESPONSE');
    const result = await wyBatch(c, 'queue-recommendations', rows(data.data.songList), { seedSongId },
      { offset: next, hasMore: more },
      map(data.data.algMap), map(data.data.sourceMap));
    for (const row of result.recommendations) row.source = 'moreRcmd';
    return result;
  },
  async chorus(c, { id, musicInfo = {} }) {
    id = input(first(id, musicInfo.songmid));
    const data = await c.wy('/api/song/chorus', { ids: JSON.stringify([id]) }, false, { maximumBytes: 65536 });
    const list = first(data.chorus, data.data);
    const row = list == null ? null : rows(list)[0];
    const start = optionalNumber(row?.startTime), end = optionalNumber(row?.endTime);
    return { platform: 'wy', id, point: start !== null && end !== null && start >= 0 && end > start
      ? { start: start / 1000, end: end / 1000 } : null };
  },
  async lyrics(c, { id, musicInfo = {} }) {
    const data = await c.wy('/api/song/lyric/v1', { id: input(first(id, musicInfo.songmid)), cp: false,
      tv: 0, lv: 0, rv: 0, kv: 0, yv: 0, ytv: 0, yrv: 0 }, false, { maximumBytes: 512 * 1024 });
    const yrc = text(data.yrc?.lyric), timed = /\[\d+,\d+\]/.test(yrc);
    return { text: timed ? yrc : text(data.lrc?.lyric),
      translation: timed && /\[\d+,\d+\]/.test(text(data.ytlrc?.lyric)) ? data.ytlrc.lyric : text(data.tlyric?.lyric),
      romanization: text(data.romalrc?.lyric) };
  },
  async audio(c, { id, musicInfo = {}, quality = '128k', qualities }) {
    id = input(first(id, musicInfo.songmid));
    let requested = qualities ?? [quality];
    if (!Array.isArray(requested)) fail('INVALID_REQUEST');
    requested = requested.map(input);
    if (requested[0] !== '128k') {
      const resources = await c.wy('/api/song/music/detail/get', { songId: id });
      const fields = { '128k': 'l', '192k': 'm', '320k': 'h', flac: 'sq', flac24bit: 'hr',
        master: 'jm', jyeffect: 'je', dolby: 'db', sky: 'sk', vivid: 'vi' };
      quality = requested.find(value => resources.data?.[fields[value]] != null);
      if (!quality) fail('QUALITY_UNAVAILABLE');
    } else quality = '128k';
    const level = levelMap[quality];
    if (!level) fail('UNSUPPORTED');
    const data = quality === '128k'
      ? await c.wy('/api/song/enhance/player/url', { ids: integerArray([id]), br: 128000 })
      : await c.xeapi('/api/song/enhance/player/url/v1', { ids: integerArray([id]), level,
        encodeType: level === 'vivid' ? 'mp3' : 'flac', ...(level === 'sky' ? { immerseType: 'c51' } : {}) });
    const row = rows(data.data)[0];
    if (!row) fail('MEDIA_UNAVAILABLE');
    if (row.code != null && ![0, 200].includes(number(required(row.code)))) fail('UPSTREAM_ERROR');
    if (row.freeTrialInfo) fail('TRIAL_ONLY');
    if (!row.url) fail('MEDIA_UNAVAILABLE');
    const duration = row.time == null ? null : number(row.time) / 1000;
    const actualQuality = quality === '128k' ? { 128000: '128k', 192000: '192k', 320000: '320k' }[number(row.br)] ?? ''
      : Object.keys(levelMap).find(key => levelMap[key] === row.level) ?? '';
    return { url: mediaURL(row.url), headers: {}, requestQuality: quality,
      actualQuality, qualityVerified: Boolean(actualQuality),
      duration, size: number(row.size), expiresAt: row.expi == null ? null : c.clock() + number(row.expi) * 1000, full: true };
  },
  async 'audio.options'(c) {
    return { qualities: c.account ? Object.keys(levelMap) : ['128k'] };
  },
  async 'dailyStyle.get'(c) {
    c.accountRequired();
    const root = await c.xeapi('/api/homepage/daily/song/config/get', {}), data = root.data ?? root;
    return { categories: rows(data.categorys).map(row => ({ id: required(row.categoryId), name: text(row.categoryName),
      tags: rows(row.tagVOList).map(tag => ({ id: required(tag.tagId), name: text(tag.tagName) })) })) };
  },
  async 'dailyStyle.save'(c, { tagIds, categoryId = '' }) {
    c.accountRequired();
    if (!Array.isArray(tagIds)) fail('INVALID_REQUEST');
    let status = 'notSent', error = null;
    c.invalidateCache();
    try {
      await c.xeapi('/api/homepage/daily/song/tag/save',
        { tags: encodeProtocolJSON({ categoryId: protocolInteger(categoryId), tagIds: tagIds.map(protocolInteger) }) },
        { dispatched: () => { status = 'unknown'; }, rejected: () => { status = 'rejected'; } });
      status = 'confirmed';
    } catch (failure) { if (failure.beforeSend) status = 'notSent'; error = c.errorCode(failure); }
    c.invalidateCache();
    let readback = null;
    const receipt = { status, readback: null, selectionMatches: null, errorCode: error };
    if (c.isCurrent()) {
      try {
        const value = await c.readback(receipt, () => wyProvider.daily(c, { branch: 'style' }));
        if (c.isCurrent()) readback = value;
      } catch (failure) {
        const code = c.errorCode(failure);
        if (!['STALE_SESSION', 'CANCELLED', 'CLOSED'].includes(code)) error ??= code;
      }
    }
    return { status, readback, selectionMatches: readback ? readback.category === scalar(categoryId) &&
      new Set(readback.tags.map(tag => tag.id)).size === new Set(tagIds).size &&
      tagIds.every(id => readback.tags.some(tag => tag.id === scalar(id))) : null, errorCode: error };
  },
};
