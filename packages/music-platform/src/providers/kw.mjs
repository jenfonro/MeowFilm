// SPDX-License-Identifier: GPL-3.0-only
// Fixed LX public metadata/lyrics recipes; see LICENSES/NOTICE.md.
import { array, record, durationSeconds, first, hasMoreFlag, image, images, input, optionalNumber, page, pageNumber, required, scalar, song, text } from '../model.mjs';
import { form } from '../protocols.mjs';
import { entities, kuwoJSON, kuwoPage } from '../literal.mjs';
import { kuwoLyrics } from '../lyrics.mjs';
import { fail } from '../errors.mjs';
import { batchedPlaylistComplete, batchedPlaylistPage } from './batched-playlist.mjs';

const agent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36';
function kwSong(raw) {
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
  const total = optionalNumber(raw.total);
  return { raw, info: kwCollection(raw), members: array(raw.musiclist), total,
    more: hasMoreFlag(raw.more, (batch + 1) * 1000 < total) };
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
    return c.cached(['song', id], async fc => {
      const root = await fc.read({ url: 'https://m.kuwo.cn/newh5/singles/songinfoandlrc?' + form({ musicId: id }), headers: headers() });
      if (scalar(root?.status) === '301' && root.msg === '音乐查询失败')
        return kwSong(kuwoPage(await read(fc, 'https://www.kuwo.cn/play_detail/' + encodeURIComponent(id))));
      if (required(root?.status) !== '200') fail('UPSTREAM_ERROR');
      return kwSong(root.data?.songinfo);
    }, 3600000);
  },
  playlist(c, params) {
    // The protocol serves 1000 members per hop. That is not a whole-collection limit.
    return batchedPlaylistPage(c, params, {
      platform: 'kw', batchSize: 1000, loadBatch: (fc, id, batch) => listPage(fc, id, batch), mapSong: kwSong,
    });
  },
  'playlist.complete'(c, params) {
    return batchedPlaylistComplete(c, params, {
      platform: 'kw', loadBatch: (fc, id, batch) => listPage(fc, id, batch), mapSong: kwSong,
    });
  },
  async lyrics(c, { id, musicInfo = {} }) {
    const bytes = await read(c, 'https://mlyric.kuwo.cn/mobi.s',
      { f: 'web', type: 'lyric', lrcx: '1', rid: input(first(id, musicInfo.songmid)), encode: 'utf8' });
    return kuwoLyrics(bytes);
  },
};
const headers = () => ({ 'User-Agent': agent, Referer: 'https://www.kuwo.cn/' });
async function read(c, url, query = {}) {
  return c.read({ url: url + (Object.keys(query).length ? '?' + form(query) : ''), headers: headers(), json: false });
}
