// SPDX-License-Identifier: GPL-3.0-only
// Fixed NetEase recipes from api-enhanced / iWebPlus / meowmusic; see LICENSES/NOTICE.md.
import { array, record, images, collection, encodeProtocolJSON, first, hasMoreFlag, image, input, mediaURL, number, optionalNumber, page, pageNumber, protocolInteger, required, rows, scalar, text, wySong } from '../model.mjs';
import { browseCard, browsePage, browseSection, channelDescriptor, songCard } from '../content.mjs';
import { fail } from '../errors.mjs';
import { randomInt } from 'node:crypto';
import { wyDetails } from './wy-details.mjs';
import { wyHome, wyChannels, wyChannelPage, wyCharts } from './wy-discovery.mjs';
import { wySearchDiscovery } from './wy-search-discovery.mjs';

const levelMap = { '128k': 'standard', '192k': 'higher', '320k': 'exhigh', flac: 'lossless',
  flac24bit: 'hires', master: 'jymaster', jyeffect: 'jyeffect', dolby: 'dolby', sky: 'sky', vivid: 'vivid' };
const integerArray = values => {
  if (!Array.isArray(values)) fail('INVALID_REQUEST');
  return encodeProtocolJSON(values.map(protocolInteger));
};
function idOf(song) { return song.sources[0]?.musicInfo.songmid ?? ''; }
function wyBatch(c, kind, source, context, extra = {}, algorithms = {}, feedbackSources = {}, defaultSource = '', items = source.map(wySong)) {
  return { platform: 'wy', kind, items, count: items.length, empty: items.length === 0,
    context, recommendations: items.map((song, index) => {
      const raw = source[index] ?? {}, nested = first(raw.songInfo, raw.songData, raw.song) ?? {}, id = idOf(song);
      const feedbackSource = feedbackSources?.[id] ?? raw.source ?? nested.source ?? defaultSource;
      const feedbackSourceId = first(raw.sourceId, nested.sourceId);
      return { id, alg: text(algorithms?.[id] ?? raw.alg ?? nested.alg), source: kind,
        ...(feedbackSource ? { feedbackSource: text(feedbackSource) } : {}),
        ...(feedbackSourceId == null ? {} : { feedbackSourceId: scalar(feedbackSourceId) }) };
    }),
    fetchedAt: Date.now(), expiresAt: null, ...extra };
}
async function wyPlaylist(c, { id, page: current = 1, limit = 100 }, complete = false) {
  id = input(id); current = pageNumber(current); limit = pageNumber(limit, 100);
  return c.cached(['playlist', id, current, limit, complete], async fc => {
    const data = await fc.wy('/api/v6/playlist/detail', { id, n: complete ? 100 : limit, s: 8 });
    const list = record(data?.playlist), info = collection('wy', list);
    fc.observe('playlist.tracks', list.tracks);
    const tracks = array(list.tracks), ids = array(list.trackIds ?? tracks);
    const offset = complete ? 0 : (current - 1) * limit;
    const members = (complete ? ids : ids.slice(offset, current * limit)).map(row => scalar(row?.id));
    const selected = new Set(members), requested = new Set(ids.map(row => scalar(row?.id)).filter(Boolean));
    const embedded = tracks.filter(row => {
      const s = first(row?.songInfo, row?.songData, row?.song, row);
      const id = scalar(first(s?.id, s?.songId, s?.resourceId));
      // A member locator is not an embedded song response. Its ID still needs
      // the selected-member detail recipe; optional song fields may stay empty.
      if (id && !['name', 'title', 'ar', 'artists', 'artistName', 'artist', 'al', 'album',
        'dt', 'duration', 'picUrl', 'coverUrl'].some(field => Object.hasOwn(record(s), field))) return false;
      return selected.has(id) || (complete || current === 1) && !requested.has(id);
    }).map(wySong);
    const known = new Map(embedded.filter(song => idOf(song)).map(song => [idOf(song), song]));
    const missing = members.filter(id => id && !known.has(id));
    const fetched = await wyDetails(fc, missing);
    for (const song of fetched) if (idOf(song)) known.set(idOf(song), song);
    const available = members.flatMap((id, index) => known.has(id) ?
      [{ song: known.get(id), position: offset + index }] : []);
    // Canonical records returned for aliases remain actual records. Do not rename
    // them to the requested ID, discard them, or invent a membership association.
    const aliases = new Map((complete || current === 1 ? [...embedded, ...fetched] : fetched)
      .filter(song => idOf(song) && !requested.has(idOf(song))).map(song => [idOf(song), song]));
    available.push(...[...aliases.values(), ...embedded.filter(song => !idOf(song)),
      ...fetched.filter(song => !idOf(song))].map(song => ({ song, position: null })));
    const items = available.map(row => row.song);
    return page('wy', items, { page: current, limit }, { ...info, total: optionalNumber(list.trackCount) ?? ids.length,
      more: !complete && current * limit < ids.length, consumed: members.length,
      memberIds: members, sourcePositions: available.map(row => row.position),
      description: text(list.description), complete });
  }, 3600000);
}
async function favorite(c) {
  const account = c.accountRequired();
  for (let offset = 0;;) {
    const data = await c.wy('/api/user/playlist', { uid: account.userId, limit: 100, offset, includeVideo: true });
    c.observe('playlist', data?.playlist);
    const lists = array(data?.playlist).map(record);
    const list = lists.find(row => scalar(row.creator?.userId) === account.userId && optionalNumber(row.specialType) === 5);
    if (list) return collection('wy', list);
    if (!data?.more || !lists.length) return collection('wy', null);
    offset += lists.length;
  }
}
export const wyProvider = {
  async search(c, { keyword, kind = 'songs', page: current = 1, limit = 30, pages = 1 }) {
    keyword = input(keyword); current = pageNumber(current); limit = pageNumber(limit, 30); pages = pageNumber(pages);
    const songs = kind === 'songs', offset = (current - 1) * limit, count = limit * pages;
    const data = await c.wy('/api/cloudsearch/pc', { s: keyword, type: songs ? 1 : 1000, limit: count, offset, total: true });
    const result = record(data?.result), total = optionalNumber(result[songs ? 'songCount' : 'playlistCount']);
    c.observe(songs ? 'result.songs' : 'result.playlists', result[songs ? 'songs' : 'playlists']);
    return page('wy', array(result[songs ? 'songs' : 'playlists'] ?? []).map(songs ? wySong : row => collection('wy', row)),
      { page: current, limit: count }, { total, more: result.hasMore == null ? offset + count < total : Boolean(result.hasMore),
        next: current + pages, ...(pages > 1 ? { offset } : {}) });
  },
  async song(c, { id }) {
    id = input(id);
    return c.cached(['song', id], async fc => {
      const values = await wyDetails(fc, [id]);
      return values[0] ?? wySong(null);
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
    const requested = ids.map(input), items = [];
    for (let at = 0; at < requested.length; at += 100) {
      const data = await c.wy('/api/tag/tab/playlists', { ids: requested.slice(at, at + 100).join(',') }, false,
        { dataStatus: raw => first(raw.data) !== undefined || first(raw.playlists) === undefined });
      const source = first(data?.data, data?.playlists);
      c.observe('playlists', source);
      items.push(...array(source).map(row => collection('wy', row)));
    }
    return { platform: 'wy', items };
  },
  async artistActivity(c, { before }) {
    if (!c.account) return browsePage({ platform: 'wy', id: 'wy:artistActivity', sections: [], more: false, continuation: null });
    const data = await c.wy('/api/sub/artist/new/works/song-mv/list/v2', {
      limit: 10, sourceType: 1, firstRequest: before == null,
      startTimestamp: before == null ? Date.now() : input(before),
    }, false, { dataStatus: true });
    const body = record(data?.data ?? data), works = array(body.newWorks).map(record);
    c.observe('newWorks', body.newWorks);
    const items = [], avatars = [];
    for (const [index, work] of works.entries()) {
      const info = work.info, meta = info?.blockTitle ?? {};
      if (!['song', 'album'].includes(info?.blockType)) continue;
      const song = info.songLists?.[0], albumId = scalar(first(info.albumId, song?.al?.id));
      const artist = text(meta.artistName) || array(song?.ar).map(row => text(row?.name)).join(' / ');
      if (albumId && albumId !== '0' || info.blockType === 'album' && !albumId) {
        const count = optionalNumber(info.albumSongCount);
        items.push(browseCard('wy', {
          id: albumId, key: 'artist-activity:' + index, kind: 'album',
          title: text(first(meta.resourceName, song?.al?.name)), creator: artist, subtitle: artist,
          cover: images(meta.resourcePicUrl, song?.al?.picUrl), count,
          coverLabel: count == null ? '' : count === 1 ? '新单曲' : '新专辑',
          action: albumId ? { type: 'album', id: albumId } : null,
        }));
      } else {
        const normalized = wySong(song);
        const songId = scalar(song?.id) || idOf(normalized);
        items.push(songCard('wy', normalized, {
          id: songId, key: 'artist-activity:' + index,
          title: text(meta.resourceName) || normalized.title,
          subtitle: artist || normalized.artist,
          cover: images(meta.resourcePicUrl, normalized.cover_url),
          coverLabel: '新单曲',
          action: songId ? { type: 'song', id: songId } : null,
        }));
      }
      const avatar = image(meta.imgUrl);
      if (avatar && !avatars.includes(avatar)) avatars.push(avatar);
    }
    const next = works.at(-1)?.publishTime, more = Boolean(body.hasMore);
    const sectionTitle = text(body.title) || '你关注的艺人新动向';
    return browsePage({
      platform: 'wy', id: 'wy:artistActivity',
      sections: items.length ? [browseSection({
        id: 'artist-activity:works', title: sectionTitle, layout: 'artist-updates', items, avatars,
        action: { type: 'channel', channel: channelDescriptor({
          id: 'artist-activity', name: sectionTitle, channelKind: 'artist-activity',
        }) },
      })] : [],
      consumed: works.length, more,
      continuation: more && scalar(next) ? { before: scalar(next) } : null,
    });
  },
  async 'heartbeat.seed'(c) {
    c.accountRequired();
    const list = await favorite(c);
    if (!list.id) return { platform: 'wy', song: null };
    const result = await wyPlaylist(c, { id: list.id, limit: 1 });
    return { platform: 'wy', song: result.items[0] ?? null };
  },
  async searchHotPlayback(c, { keywords }) {
    if (!Array.isArray(keywords)) fail('INVALID_REQUEST');
    const data = await c.wy('/api/hot/search/relate/song/get', { keywords: JSON.stringify(keywords.map(input)) }, false,
      { dataStatus: true });
    return { platform: 'wy', items: array(data?.data?.songs).map(row => wySong(row?.song)) };
  },
  home: wyHome,
  channels: wyChannels,
  channel: wyChannelPage,
  charts: wyCharts,
  searchDiscovery: wySearchDiscovery,
  async album(c, { id }) {
    id = input(id);
    return c.cached(['album', id], async fc => {
      const data = await fc.wy('/api/v1/album/' + encodeURIComponent(id), {});
      const album = record(data?.album), items = array(data?.songs).map(wySong);
      return { platform: 'wy', id: scalar(album.id), name: text(album.name), cover: image(album.picUrl),
        creator: text(album.artist?.name), description: text(album.description), items, total: items.length, more: false };
    }, 3600000);
  },
  async artist(c, { id }) {
    id = input(id);
    return c.cached(['artist', id], async fc => {
      const data = await fc.wy('/api/v1/artist/' + encodeURIComponent(id), {});
      const artist = record(data?.artist);
      return { platform: 'wy', id: scalar(artist.id), name: text(artist.name), cover: image(artist.picUrl),
        description: text(artist.briefDesc), items: array(data?.hotSongs).map(wySong), more: false };
    }, 3600000);
  },
  async personalPlaylists(c, { kind = 'all', page: current = 1, limit = 30, continuation }) {
    current = pageNumber(current); limit = pageNumber(limit, 30);
    const account = c.accountRequired(), offset = number(continuation?.offset, (current - 1) * limit, 'INVALID_REQUEST');
    const data = await c.wy('/api/user/playlist', { uid: account.userId, offset, limit, includeVideo: true });
    c.observe('playlist', data?.playlist);
    const source = array(data?.playlist).map(record), items = source.filter(row => kind === 'all' ||
      (scalar(row.creator?.userId) === account.userId) === (kind === 'created')).map(row => {
      const ownerId = scalar(row.creator?.userId), owned = ownerId === account.userId;
      const special = row.specialType == null ? 0 : optionalNumber(row.specialType);
      return { ...collection('wy', row), ownerId, owned, relationship: owned ? 'owned' : 'subscribed',
        subscribed: Boolean(row.subscribed),
        writable: owned && (special === 0 || special === 5),
        kind: special === 5 ? 'favorites' : special === 0 ? 'playlist' : 'special' };
    });
    const more = Boolean(data?.more);
    return { ...page('wy', items, { page: current, limit }, { total: optionalNumber(data?.total), more, consumed: source.length }),
      continuation: more && source.length ? { page: current + 1, offset: offset + source.length } : null };
  },
  async daily(c, { branch = 'default' }) {
    c.accountRequired();
    return c.cached(['daily', branch], async fc => {
      const data = await fc.xeapi(branch === 'style' ? '/api/homepage/category/daily/song/list' : '/api/v3/discovery/recommend/songs',
        branch === 'style' ? {} : { ispush: false },
        { dataStatus: raw => first(raw.data?.dailySongs, raw.data?.tags) !== undefined || first(raw.recommend) === undefined });
      const body = record(data?.data ?? data);
      const source = array(first(body.dailySongs, data?.recommend));
      fc.observe('dailySongs', first(body.dailySongs, data?.recommend));
      const now = Date.now(), local = new Date(now + 8 * 3600000), next = Date.UTC(local.getUTCFullYear(), local.getUTCMonth(), local.getUTCDate(), 6) - 8 * 3600000;
      const expiry = branch === 'style' ? now + 60000 : next > now ? next : next + 86400000;
      return wyBatch(fc, 'daily', source, { branch }, { id: 'daily:' + branch, branch,
        name: branch === 'style' ? '风格推荐' : '每日推荐', source: 'wy',
        engine: 'NeteaseCollection', more: false, cover: image(source[0]?.al?.picUrl), expiresAt: expiry,
        category: scalar(body.tags?.categoryId),
        tags: array(body.tags?.tagVOList).map(record).map(tag => ({ id: scalar(first(tag.id, tag.tagId)), name: text(first(tag.name, tag.tagName)) })) },
      {}, {}, branch === 'style' ? '' : 'dailySongRecommend');
    }, branch === 'style' ? 60000 : 600000);
  },
  async fm(c, { mode = 'default', submode = '', preview = false, continuation = false, ruleId = '', tabTagId = '', unplaySongIds = [] }) {
    const isContinuation = !preview && (Boolean(continuation) || unplaySongIds.length > 0);
    if (ruleId || tabTagId) {
      const rule = scalar(ruleId), tab = scalar(tabTagId);
      const load = async fc => {
        const params = { ruleType: 1, ruleId: rule, tabTagId: tab, firstSongId: '', firstSongAlg: '', ...(preview ? {} : { limit: 5 }) };
        const data = await fc.wy(preview ? '/api/tag/tab/fm/cover' : '/api/tag/tab/fm/recommends', params, false,
          { dataStatus: true });
        if (preview) return { platform: 'wy', kind: 'fm-preview', preview: true, cover: image(data?.data?.cover),
          context: { provider: 'rule', ruleId: rule, tabTagId: tab } };
        const rawRows = array(data?.data).map(record);
        const ids = [...new Set(rawRows.map(row => scalar(row.id)).filter(Boolean))];
        const byId = new Map((ids.length ? await wyDetails(fc, ids) : []).map(song => [idOf(song), song]));
        const matched = rawRows.filter(row => byId.has(scalar(row.id)));
        return wyBatch(fc, 'fm', matched, { provider: 'rule', ruleId: rule, tabTagId: tab },
          {}, {}, {}, '', matched.map(row => byId.get(scalar(row.id))));
      };
      return isContinuation ? load(c) : c.cached(['fm', preview ? 'preview' : 'initial', 'rule', rule, tab], load, 3600000);
    }
    const protocolMode = { default: 'DEFAULT', familiar: 'FAMILIAR', explore: 'EXPLORE', puzzle: 'PUZZLE_MODE_RCMD', scene: 'SCENE_RCMD' }[mode] ?? input(mode);
    const sub = text(submode), unplay = integerArray(unplaySongIds);
    const load = async fc => {
      const data = await fc.wy('/api/v1/radio/get', { mode: protocolMode, subMode: sub, limit: preview ? '1' : '3',
        openAidj: 'false', aidjReqTimes: '0', unplaySongIds: unplay }, false, { dataStatus: true });
      return wyBatch(fc, 'fm', array(Array.isArray(data?.data) ? data.data : data?.data?.songs),
        { mode: protocolMode, submode: sub, preview }, {}, {}, {}, 'userfm');
    };
    return isContinuation ? load(c) : c.cached(['fm', preview ? 'preview' : 'initial', 'mode', protocolMode, sub], load, 3600000);
  },
  async similar(c, { sourceIds, sourceType = 'song', unplaySongIds = [] }) {
    if (!Array.isArray(sourceIds) || !sourceIds.length) fail('INVALID_REQUEST');
    const data = await c.wy('/api/v1/radio/get', { limit: '3', mode: 'SIMILAR', sourceType,
      sourceIds: integerArray(sourceIds), unplaySonIds: integerArray(unplaySongIds) }, true, { dataStatus: true });
    return wyBatch(c, 'similar', array(Array.isArray(data?.data) ? data.data : data?.data?.songs), { sourceIds, sourceType });
  },
  async heartbeat(c, { entry = 'favorites', songId, playlistId, continuation }) {
    const account = c.accountRequired();
    let count;
    if (continuation) count = number(continuation.count, 0, 'INVALID_REQUEST');
    const listId = continuation?.playlistId ?? playlistId ?? (await favorite(c)).id;
    if (!listId) return wyBatch(c, 'heartbeat', [], { entry, accountId: account.userId },
      { entrySong: null, continuation: null });
    const list = continuation ? null : await wyPlaylist(c, { id: listId }, true);
    const known = new Map((list?.items ?? []).map(song => [idOf(song), song]));
    const choices = (list?.items ?? []).filter(song => idOf(song));
    const seed = continuation?.startMusicId ?? songId ?? (choices.length ? idOf(choices[randomInt(choices.length)]) : null);
    if (!seed) return wyBatch(c, 'heartbeat', [], { entry, playlistId: listId, accountId: account.userId },
      { entrySong: null, continuation: null });
    if (!continuation) count = list.memberIds.length;
    const currentSong = input(songId ?? seed), fromHome = entry === 'favorites';
    const data = await c.wy('/api/playmode/intelligence/list', { songId: currentSong, playlistId: input(listId),
      startMusicId: seed, count, type: fromHome ? 'fromPlayAll' : 'fromPlayOne' }, false, { dataStatus: true });
    c.observe('data', data?.data);
    const source = array(data?.data);
    const result = wyBatch(c, 'heartbeat', source,
      { entry, playlistId: listId, startMusicId: seed, songId: currentSong, fromHome, count, accountId: account.userId },
      {});
    result.recommendations = result.recommendations.map(row => ({ ...row, sourceId: listId, seedId: seed }));
    if (!continuation) {
      const index = result.items.findIndex(song => idOf(song) === currentSong);
      if (index >= 0) {
        result.items.unshift(...result.items.splice(index, 1));
        result.recommendations.unshift(...result.recommendations.splice(index, 1));
      } else {
        const selected = known.get(currentSong) ?? (await wyDetails(c, [currentSong]))[0];
        if (selected) {
          result.items.unshift(selected);
          result.recommendations.unshift({ id: idOf(selected), alg: '', source: 'heartbeat',
            sourceId: listId, seedId: seed, feedbackSource: '', feedbackSourceId: '' });
        }
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
      source: 'curlist_more_rcmd', extJson: '{}' }, false, { dataStatus: true });
    const map = value => {
      try { return typeof value === 'string' ? JSON.parse(value) : value; }
      catch { fail('INVALID_RESPONSE'); }
    };
    const body = record(data?.data), next = optionalNumber(body.offset);
    const more = hasMoreFlag(body.hasMore);
    c.observe('songList', body.songList);
    const result = wyBatch(c, 'queue-recommendations', array(body.songList), { seedSongId },
      { offset: next, hasMore: more }, map(body.algMap), map(body.sourceMap));
    for (const row of result.recommendations) row.source = 'moreRcmd';
    return result;
  },
  async chorus(c, { id, musicInfo = {} }) {
    id = input(first(id, musicInfo.songmid));
    const data = await c.wy('/api/song/chorus', { ids: JSON.stringify([id]) }, false,
      { dataStatus: raw => first(raw.chorus) === undefined });
    const list = first(data?.chorus, data?.data);
    const row = array(list)[0];
    const start = optionalNumber(row?.startTime), end = optionalNumber(row?.endTime);
    return { platform: 'wy', id, point: start !== null && end !== null && start >= 0 && end > start
      ? { start: start / 1000, end: end / 1000 } : null };
  },
  async lyrics(c, { id, musicInfo = {} }) {
    const data = await c.wy('/api/song/lyric/v1', { id: input(first(id, musicInfo.songmid)), cp: false,
      tv: 0, lv: 0, rv: 0, kv: 0, yv: 0, ytv: 0, yrv: 0 }, false);
    const yrc = text(data?.yrc?.lyric), timed = /\[\d+,\d+\]/.test(yrc);
    return { text: timed ? yrc : text(data?.lrc?.lyric),
      translation: timed && /\[\d+,\d+\]/.test(text(data?.ytlrc?.lyric)) ? data.ytlrc.lyric : text(data?.tlyric?.lyric),
      romanization: text(data?.romalrc?.lyric) };
  },
  async audio(c, { id, musicInfo = {}, quality = '128k', qualities }) {
    id = input(first(id, musicInfo.songmid));
    let requested = qualities ?? [quality];
    if (!Array.isArray(requested)) fail('INVALID_REQUEST');
    requested = requested.map(input);
    if (requested[0] !== '128k') {
      const resources = await c.wy('/api/song/music/detail/get', { songId: id }, false, { dataStatus: true });
      const fields = { '128k': 'l', '192k': 'm', '320k': 'h', flac: 'sq', flac24bit: 'hr',
        master: 'jm', jyeffect: 'je', dolby: 'db', sky: 'sk', vivid: 'vi' };
      quality = requested.find(value => resources.data?.[fields[value]] != null);
      if (!quality) fail('QUALITY_UNAVAILABLE');
    } else quality = '128k';
    const level = levelMap[quality];
    if (!level) fail('UNSUPPORTED');
    const data = quality === '128k'
      ? await c.wy('/api/song/enhance/player/url', { ids: integerArray([id]), br: 128000 }, false, { dataStatus: true })
      : await c.xeapi('/api/song/enhance/player/url/v1', { ids: integerArray([id]), level,
        encodeType: level === 'vivid' ? 'mp3' : 'flac', ...(level === 'sky' ? { immerseType: 'c51' } : {}) }, { dataStatus: true });
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
      duration, size: number(row.size), expiresAt: row.expi == null ? null : Date.now() + number(row.expi) * 1000, full: true };
  },
  async 'audio.options'(c) {
    return { qualities: c.account ? Object.keys(levelMap) : ['128k'] };
  },
  async 'dailyStyle.get'(c) {
    c.accountRequired();
    const root = await c.xeapi('/api/homepage/daily/song/config/get', {}, { dataStatus: true }), data = record(root?.data ?? root);
    return { categories: array(data.categorys).map(record).map(row => ({ id: scalar(row.categoryId), name: text(row.categoryName),
      tags: array(row.tagVOList).map(record).map(tag => ({ id: scalar(tag.tagId), name: text(tag.tagName) })) })) };
  },
  async 'dailyStyle.save'(c, { tagIds, categoryId = '' }) {
    c.accountRequired();
    if (!Array.isArray(tagIds)) fail('INVALID_REQUEST');
    const encodedTags = encodeProtocolJSON({ categoryId: protocolInteger(categoryId), tagIds: tagIds.map(protocolInteger) });
    const expectedCategory = scalar(categoryId);
    const expectedTagIds = tagIds.map(scalar);
    let status = 'notSent', error = null;
    c.invalidateCache();
    try {
      await c.xeapi('/api/homepage/daily/song/tag/save', { tags: encodedTags },
        { dataStatus: true, dispatched: () => { status = 'unknown'; }, rejected: () => { status = 'rejected'; } });
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
    return { status, readback, selectionMatches: readback ? readback.category === expectedCategory &&
      new Set(readback.tags.map(tag => tag.id)).size === new Set(expectedTagIds).size &&
      expectedTagIds.every(id => readback.tags.some(tag => tag.id === id)) : null, errorCode: error };
  },
};
