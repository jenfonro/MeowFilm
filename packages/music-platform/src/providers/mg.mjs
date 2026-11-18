// SPDX-License-Identifier: GPL-3.0-only
// Fixed LX / meowmusic public metadata/lyrics recipes; see LICENSES/NOTICE.md.
import { array, record, durationSeconds, first, images, input, optionalNumber, page, pageNumber, required, scalar, song, text, utf8 } from '../model.mjs';
import { form, md5 } from '../protocols.mjs';
import { lyricTracks, miguLyrics } from '../lyrics.mjs';
import { fail } from '../errors.mjs';

const device = '963B7AA0D21511ED807EE5846EC87D20';
const mobileAgent = 'Mozilla/5.0 (Linux; U; Android 11.0.0; zh-cn; MI 11 Build/OPR1.170623.032) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30';
const playlistAgent = 'Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1';
function artwork(value) {
  const address = text(value);
  return address.startsWith('/') && !address.startsWith('//') ? 'https://d.musicapp.migu.cn' + address : address;
}
export function mgSong(raw) {
  raw = record(raw);
  const names = Array.isArray(first(raw.singerList, raw.artists))
    ? first(raw.singerList, raw.artists).map(artist => text(artist?.name)).filter(Boolean) : undefined;
  let duration = durationSeconds(raw.duration);
  if (raw.duration == null) {
    const parts = text(raw.length).split(':').map(optionalNumber);
    if (parts.every(part => part !== null && part >= 0))
      duration = durationSeconds(parts.reduce((value, part) => value * 60 + part, 0));
  }
  const native = { songId: scalar(raw.songId), copyrightId: scalar(raw.copyrightId), albumId: scalar(raw.albumId) };
  for (const key of ['contentId', 'hash', 'md5'])
    if (typeof raw[key] === 'string' || typeof raw[key] === 'number') native[key] = raw[key];
  // Audio/lyric consumers need these protocol fields, not the rest of the response.
  for (const name of ['audioFormats', 'newRateFormats']) if (Array.isArray(raw[name])) native[name] = raw[name]
    .filter(row => row && typeof row === 'object' && !Array.isArray(row)).map(row =>
    Object.fromEntries(['formatType', 'resourceType', 'size', 'androidSize', 'iosSize', 'asize', 'isize', 'format',
      'androidFormat', 'iosFormat', 'aformat', 'iformat', 'bitrate', 'sampleRate', 'bitDepth', 'fileType',
      'androidFileType', 'iosFileType', 'iosAccuracyLevel', 'androidAccuracyLevel', 'hash', 'md5', 'androidMd5', 'fileId', 'contentId']
      .filter(key => typeof row[key] === 'string' || typeof row[key] === 'number').map(key => [key, row[key]])));
  const types = [], details = {};
  for (const row of native.audioFormats ?? native.newRateFormats ?? []) {
    const type = { PQ: '128k', HQ: '320k', SQ: 'flac', ZQ: 'flac24bit', ZQ24: 'flac24bit' }[row.formatType];
    if (!type || details[type]) continue;
    const bytes = optionalNumber(first(row.size, row.androidSize, row.asize, row.isize));
    const size = bytes > 0 ? (bytes / 1048576).toFixed(2) + ' MB' : '';
    types.push({ type, size }); details[type] = { size };
  }
  native.types = types; native._types = details;
  const cover = images(...[raw.img3, raw.img2, raw.img1, raw.albumImgs?.[0]?.img].map(artwork));
  return song('mg', raw.songId, first(raw.songName, raw.name), names?.join('、') ?? text(raw.singer),
    raw.album, duration, cover, native, names);
}
function mgCollection(raw) {
  raw = record(raw);
  return { id: scalar(first(raw.musicListId, raw.id)), name: text(first(raw.title, raw.name)),
    cover: images(artwork(raw.imgItem?.img), artwork(raw.musicListPicUrl)), count: optionalNumber(raw.musicNum),
    creator: text(first(raw.ownerName, raw.userName)), description: text(raw.summary), source: 'mg', engine: 'Migu' };
}
function request(url, query = {}, playlist = false, body) {
  return { url: url + (Object.keys(query).length ? '?' + form(query) : ''), method: body === undefined ? 'GET' : 'POST',
    headers: { 'User-Agent': playlist ? playlistAgent : mobileAgent, Referer: 'https://m.music.migu.cn/', channel: '0146921',
      ...(body === undefined ? {} : { 'Content-Type': 'application/x-www-form-urlencoded' }) },
    ...(body === undefined ? {} : { body: form(body) }) };
}
async function read(c, recipe) {
  const root = await c.read(recipe), code = first(root?.code, root?.returnCode);
  if (required(code) !== '000000') fail('UPSTREAM_ERROR');
  return record(root.data ?? root);
}
async function listPage(c, id, batch) {
  return read(c, request('https://app.c.nf.migu.cn/MIGUM3.0/resource/playlist/song/v2.0',
    { playlistId: id, pageNo: batch + 1, pageSize: 50 }, true));
}
export const mgProvider = {
  async search(c, { keyword, kind = 'songs', page: current = 1, limit = 30 }) {
    keyword = input(keyword); current = pageNumber(current); limit = pageNumber(limit, 30);
    const songs = kind === 'songs', timestamp = String(c.clock());
    const recipe = request('https://jadeite.migu.cn/music_search/v3/search/searchAll', {
      isCorrect: songs ? 0 : 1, isCopyright: 1,
      searchSwitch: JSON.stringify({ song: songs ? 1 : 0, album: 0, singer: 0, tagSong: songs ? 1 : 0,
        mvSong: 0, bestShow: songs ? 1 : 0, songlist: songs ? 0 : 1, lyricSong: 0 }),
      pageSize: limit, text: keyword, pageNo: current, sort: 0, sid: 'USS' });
    Object.assign(recipe.headers, { uiVersion: 'A_music_3.6.1', deviceId: device, timestamp,
      sign: md5(keyword + '6cdc72a439cef99a3418d2a78aa28c73' + 'yyapp2d16148780a1dcc7408e06336b98cfd50' + device + timestamp) });
    const raw = await read(c, recipe), result = record(raw[songs ? 'songResultData' : 'songListResultData']);
    const total = optionalNumber(result.totalCount);
    c.observe(songs ? 'songResultData.resultList' : 'songListResultData.result', songs ? result.resultList : result.result);
    if (!songs) return page('mg', array(result.result).map(mgCollection), { page: current, limit }, { total, more: current * limit < total });
    const groups = array(result.resultList), items = [];
    for (const [index, group] of groups.entries()) for (const row of array(group))
      items.push({ rank: (current - 1) * limit + index + 1, song: mgSong(row) });
    return page('mg', items, { page: current, limit }, { total, totalUnit: 'groups', groupCount: groups.length,
      rawItemCount: items.length, consumed: groups.length, more: current * limit < total });
  },
  async song(c, { id }) {
    id = input(id);
    return c.cached(['song', id], async () => {
      const data = await read(c, request('https://c.musicapp.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do',
        { resourceType: 2 }, false, { resourceId: id }));
      c.observe('resource', data.resource);
      return mgSong(array(data.resource)[0]);
    }, 3600000);
  },
  async playlist(c, { id, page: current = 1, limit = 100, continuation }) {
    id = input(id); current = pageNumber(current); limit = pageNumber(limit, 100);
    if (!Number.isSafeInteger(current) || current < 1 || !Number.isSafeInteger(limit) || limit < 1) fail('INVALID_REQUEST');
    const offset = continuation == null ? (current - 1) * limit : 0;
    if (!Number.isSafeInteger(offset)) fail('INVALID_REQUEST');
    const firstBatch = pageNumber(continuation?.batch, Math.floor(offset / 50));
    const firstIndex = pageNumber(continuation?.index, offset % 50);
    if (!Number.isSafeInteger(firstBatch) || firstBatch < 0 || !Number.isSafeInteger(firstIndex) || firstIndex < 0)
      fail('INVALID_REQUEST'); // These values address discrete upstream members.
    return c.cached(['playlist', id, current, firstBatch, firstIndex, limit], async () => {
      const meta = await read(c, request('https://c.musicapp.migu.cn/MIGUM3.0/resource/playlist/v2.0', { playlistId: id }, true));
      const info = mgCollection(meta), items = [];
      let batch = firstBatch, index = firstIndex, more = true, total = optionalNumber(meta.musicNum), stalled = false;
      while (items.length < limit && more) {
        const raw = await listPage(c, id, batch);
        total = optionalNumber(raw.totalCount) ?? total;
        c.observe('songList', raw.songList);
        const members = array(raw.songList), selected = members.slice(index, index + limit - items.length);
        items.push(...selected.map(mgSong)); index += selected.length;
        if (index < members.length) { more = true; break; }
        more = raw.hasMore == null ? (batch + 1) * 50 < total : (raw.hasMore === true || Boolean(optionalNumber(raw.hasMore)));
        if (!selected.length && more) { stalled = true; break; }
        batch++; index = 0;
      }
      return { ...page('mg', items, { page: current, limit }, { total, more, ...info }),
        consumed: items.length, continuation: more && !stalled ? { page: current + 1, batch, index } : null };
    }, 3600000);
  },
  async 'playlist.complete'(c, { id }) {
    id = input(id);
    return c.cached(['playlist.complete', id], async () => {
      const meta = await read(c, request('https://c.musicapp.migu.cn/MIGUM3.0/resource/playlist/v2.0', { playlistId: id }, true));
      const info = mgCollection(meta), items = [];
      let batch = 0, total = optionalNumber(meta.musicNum), more, members;
      do {
        const raw = await listPage(c, id, batch++);
        total = optionalNumber(raw.totalCount) ?? total;
        c.observe('songList', raw.songList);
        members = array(raw.songList);
        items.push(...members.map(mgSong));
        more = raw.hasMore == null ? batch * 50 < total : (raw.hasMore === true || Boolean(optionalNumber(raw.hasMore)));
      } while (more && members.length);
      return { platform: 'mg', ...info, items, total, count: items.length,
        more, continuation: null, complete: !more };
    }, 3600000);
  },
  async lyrics(c, { id, musicInfo = {} }) {
    id = input(first(id, musicInfo.songmid));
    const data = await read(c, request('https://c.musicapp.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do',
      { resourceType: 2 }, false, { resourceId: id }));
    const resource = record(array(data.resource)[0]), main = first(resource.mrcUrl, resource.mrcurl, resource.lrcUrl);
    if (!main) return { text: '', translation: '', romanization: '' };
    const file = async address => {
      const url = new URL(required(address));
      // Dynamic files come only from this response; no credentials follow them.
      if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) fail('INVALID_RESPONSE');
      url.protocol = 'https:';
      return c.read({ url: url.href, json: false,
        headers: { Referer: 'https://app.c.nf.migu.cn/', channel: '0146921', 'User-Agent': mobileAgent } });
    };
    const bytes = await file(main), text = resource.mrcUrl || resource.mrcurl ? await miguLyrics(bytes, c.signal) :
      utf8(bytes);
    return lyricTracks(text, { translation: async () => resource.trcUrl ? utf8(await file(resource.trcUrl)) : '' }, c.signal);
  },
};
