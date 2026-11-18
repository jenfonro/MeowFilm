// SPDX-License-Identifier: GPL-3.0-only
// Fixed LX public metadata/lyrics recipes; see LICENSES/NOTICE.md.
import { array, record, durationSeconds, first, image, images, input, optionalNumber, page, pageNumber, required, scalar, song, text } from '../model.mjs';
import { form } from '../protocols.mjs';
import { entities, kuwoJSON, kuwoPage } from '../literal.mjs';
import { kuwoLyrics } from '../lyrics.mjs';
import { fail } from '../errors.mjs';

const agent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36';
export function kwSong(raw) {
  raw = record(raw);
  const id = scalar(first(raw.MUSICRID, raw.id, raw.rid, raw.musicrid)).replace(/^MUSIC_/, '');
  const minfo = text(raw.N_MINFO), formats = text(first(raw.FORMATS, raw.formats));
  const types = [], nativeTypes = {};
  for (const match of minfo.matchAll(/level:(\w+),bitrate:(\d+),format:(\w+),size:([\w.]+)/g)) {
    const type = { 4000: 'flac24bit', 2000: 'flac', 320: '320k', 128: '128k' }[match[2]];
    if (!type || nativeTypes[type]) continue;
    nativeTypes[type] = { size: match[4].toUpperCase(), bitrate: match[2], format: match[3], level: match[1] };
    types.unshift({ type, size: match[4].toUpperCase() });
  }
  if (!minfo) for (const format of formats.split('|')) {
    const type = { MP3128: '128k', MP3H: '320k', ALFLAC: 'flac', HIRFLAC: 'flac24bit' }[format];
    if (type && !nativeTypes[type]) { nativeTypes[type] = { format }; types.push({ type }); }
  }
  const relative = text(raw.web_albumpic_short);
  let cover = images(raw.pic, raw.img);
  if (!cover && relative) {
    try { cover = image(new URL(relative, 'http://img1.kwcdn.kuwo.cn/star/albumcover/').href); }
    catch { /* An unusable optional image is not a failed song locator. */ }
  }
  return song('kw', id, entities(text(first(raw.SONGNAME, raw.name, raw.songName))),
    entities(text(first(raw.ARTIST, raw.artist))), entities(text(first(raw.ALBUM, raw.album))),
    durationSeconds(first(raw.DURATION, raw.duration)), cover,
    { id, albumId: scalar(first(raw.ALBUMID, raw.albumid, raw.albumId)), types, _types: nativeTypes });
}
function kwCollection(raw) {
  raw = record(raw);
  return { id: scalar(first(raw.playlistid, raw.id)), name: entities(text(first(raw.name, raw.title))),
    cover: image(raw.pic), count: optionalNumber(first(raw.songnum, raw.total)),
    creator: entities(text(first(raw.nickname, raw.uname))), description: entities(text(first(raw.intro, raw.info))),
    source: 'kw', engine: 'Kuwo' };
}
async function listPage(c, id, batch) {
  const raw = kuwoJSON(await read(c, 'http://nplserver.kuwo.cn/pl.svc', { op: 'getlistinfo', pid: id, pn: batch,
    rn: 1000, encode: 'utf8', keyset: 'pl2012', identity: 'kuwo', pcmp4: '1', vipver: 'MUSIC_9.0.5.0_W1', newver: '1' }));
  if (required(raw?.result) !== 'ok') fail('UPSTREAM_ERROR');
  c.observe('musiclist', raw.musiclist);
  return { raw, members: array(raw.musiclist), total: optionalNumber(raw.total),
    more: raw.more == null ? (batch + 1) * 1000 < optionalNumber(raw.total) : (raw.more === true || Boolean(optionalNumber(raw.more))) };
}
export const kwProvider = {
  async search(c, { keyword, kind = 'songs', page: current = 1, limit = 30 }) {
    keyword = input(keyword); current = pageNumber(current); limit = pageNumber(limit, 30);
    const recipe = kind === 'songs' ? { client: 'kt', uid: '794762570', ver: 'kwplayer_ar_9.2.2.1',
      vipver: '1', show_copyright_off: '1', newver: '1', ft: 'music', cluster: '0', strategy: '2012', vermerge: '1', mobi: '1', issubtitle: '1' }
      : { ver: 'mbox', vipver: 'MUSIC_8.7.7.0_BCS37', plat: 'pc', devid: '28156413', ft: 'playlist', pay: '0', needliveshow: '0' };
    const data = kuwoJSON(await read(c, 'http://search.kuwo.cn/r.s',
      { all: keyword, pn: current - 1, rn: limit, rformat: 'json', encoding: 'utf8', ...recipe }));
    const total = optionalNumber(data?.TOTAL);
    c.observe('abslist', data?.abslist);
    return page('kw', array(data?.abslist).map(kind === 'songs' ? kwSong : kwCollection),
      { page: current, limit }, { total, more: current * limit < total });
  },
  async song(c, { id }) {
    id = input(id);
    return c.cached(['song', id], async () => {
      const root = await c.read({ url: 'https://m.kuwo.cn/newh5/singles/songinfoandlrc?' + form({ musicId: id }), headers: headers() });
      if (scalar(root?.status) === '301' && root.msg === '音乐查询失败')
        return kwSong(kuwoPage(await read(c, 'https://www.kuwo.cn/play_detail/' + encodeURIComponent(id))));
      if (required(root?.status) !== '200') fail('UPSTREAM_ERROR');
      return kwSong(root.data?.songinfo);
    }, 3600000);
  },
  async playlist(c, { id, page: current = 1, limit = 100, continuation }) {
    id = input(id); current = pageNumber(current); limit = pageNumber(limit, 100);
    if (!Number.isSafeInteger(current) || current < 1 || !Number.isSafeInteger(limit) || limit < 1) fail('INVALID_REQUEST');
    const offset = continuation == null ? (current - 1) * limit : 0;
    if (!Number.isSafeInteger(offset)) fail('INVALID_REQUEST');
    const firstBatch = pageNumber(continuation?.batch, Math.floor(offset / 1000));
    const firstIndex = pageNumber(continuation?.index, offset % 1000);
    if (!Number.isSafeInteger(firstBatch) || firstBatch < 0 || !Number.isSafeInteger(firstIndex) || firstIndex < 0)
      fail('INVALID_REQUEST'); // These values address discrete upstream members.
    return c.cached(['playlist', id, current, firstBatch, firstIndex, limit], async () => {
      // The protocol serves 1000 members per hop. That is not a whole-collection limit.
      const items = [];
      let batch = firstBatch, index = firstIndex, info, total = null, more = false, stalled = false;
      do {
        const result = await listPage(c, id, batch), { raw, members } = result;
        info ??= kwCollection(raw); total = result.total;
        const selected = members.slice(index, index + limit - items.length);
        items.push(...selected.map(kwSong)); index += selected.length;
        if (index < members.length) { more = true; break; }
        more = result.more;
        if (!selected.length && more) { stalled = true; break; }
        batch++; index = 0;
      } while (items.length < limit && more);
      return { ...page('kw', items, { page: current, limit }, { total, more, ...info }),
        consumed: items.length, continuation: more && !stalled ? { page: current + 1, batch, index } : null };
    }, 3600000);
  },
  async 'playlist.complete'(c, { id }) {
    id = input(id);
    return c.cached(['playlist.complete', id], async () => {
      const items = [];
      let result, info, batch = 0;
      do {
        result = await listPage(c, id, batch++);
        info ??= kwCollection(result.raw);
        items.push(...result.members.map(kwSong));
      } while (result.more && result.members.length);
      return { platform: 'kw', ...info, items, total: result.total, count: items.length,
        more: result.more, continuation: null, complete: !result.more };
    }, 3600000);
  },
  async lyrics(c, { id, musicInfo = {} }) {
    const bytes = await read(c, 'https://mlyric.kuwo.cn/mobi.s',
      { f: 'web', type: 'lyric', lrcx: '1', rid: input(first(id, musicInfo.songmid)), encode: 'utf8' }, 512 * 1024);
    return kuwoLyrics(bytes);
  },
};
const headers = () => ({ 'User-Agent': agent, Referer: 'https://www.kuwo.cn/' });
async function read(c, url, query = {}, maximumBytes) {
  return c.read({ url: url + (Object.keys(query).length ? '?' + form(query) : ''), headers: headers(), json: false,
    ...(maximumBytes === undefined ? {} : { maximumBytes }) });
}
