// SPDX-License-Identifier: GPL-3.0-only
// Pinned QQMusicApi / meowmusic recipes; see LICENSES/NOTICE.md.
import { randomInt } from 'node:crypto';
import { array, record, collection, first, images, input, mediaURL, number, optionalNumber, page, pageNumber, protocolInteger, required, rows, scalar, text, txSong } from '../model.mjs';
import { cookieFields, hex } from '../protocols.mjs';
import { lyricTracks, qrcLyrics } from '../lyrics.mjs';
import { fail } from '../errors.mjs';

async function details(c, id) {
  const data = await c.tx('music.trackInfo.UniformRuleCtrl', 'CgiGetTrackInfo',
    { ctx: 0, client: 1, types: [0], modify_stamp: [0], mids: [id] });
  c.observe('tracks', data?.tracks);
  return record(array(data?.tracks)[0]);
}
async function playlist(c, p) {
  const { id, page: current, limit, offset } = p;
  const favorites = id === 'favorites' || p.personal?.kind === 'qq-favorites';
  const args = { disstid: favorites ? 0 : protocolInteger(id), dirid: favorites ? 201 : 0, tag: 1,
    song_begin: offset, song_num: limit, userinfo: 1, orderlist: 1 };
  if (favorites) {
    const cookies = cookieFields(c.accountRequired().cookie);
    args.enc_host_uin = input(first(cookies.encrypt_uin, cookies.encryptUin));
  } else args.onlysonglist = 0;
  const data = record(await c.tx('music.srfDissInfo.DissInfo', 'CgiGetDiss', args));
  c.observe('songlist', data.songlist);
  const info = record(data.dirinfo), items = array(data.songlist).map(txSong);
  const more = (data.hasmore === true || Boolean(optionalNumber(data.hasmore)));
  return { ...page('tx', items, { page: current, limit }, { id: favorites ? 'favorites' : scalar(first(info.id, info.tid, info.dissid)),
    name: text(first(info.title, info.dissname, info.name, favorites ? '我喜欢' : '')),
    cover: images(info.picurl, info.cover, info.logo), total: optionalNumber(data.total_song_num),
    more, consumed: items.length,
    memberIds: items.map(song => song.sources[0]?.musicInfo.songmid ?? ''), source: 'tx', engine: 'QQ' }),
  continuation: more && items.length ? { page: current + 1, offset: offset + items.length } : null };
}
function personalItem(raw, uid, owned) {
  raw = record(raw);
  const card = collection('tx', raw);
  return { ...card, ownerId: scalar(raw.uin), relationship: owned ? 'owned' : 'subscribed',
    owned, writable: owned && scalar(raw.uin) === uid, kind: 'playlist', available: !raw.invalid,
    locator: { playlistId: card.id, dirId: scalar(first(raw.dirid, raw.dirId)) },
    action: { type: 'playlist', id: card.id } };
}
export const txProvider = {
  async search(c, { keyword, kind = 'songs', page: current = 1, limit = 30 }) {
    current = pageNumber(current); limit = pageNumber(limit, 30);
    const searchid = (BigInt(randomInt(1, 21)) * 18014398509481984n +
      BigInt(randomInt(4194305)) * 4294967296n + BigInt(c.clock() % 86400000)).toString();
    const data = await c.tx('music.search.SearchCgiService', 'DoSearchForQQMusicMobile', {
      searchid, query: input(keyword), search_type: kind === 'songs' ? 0 : 3, num_per_page: limit,
      page_num: current, highlight: 0, grp: 1, selectors: {}, vec_selectors: [] }, true);
    const next = optionalNumber(data?.meta?.nextpage) ?? -1;
    const source = data?.body?.[kind === 'songs' ? 'item_song' : 'item_songlist'];
    c.observe('body.' + (kind === 'songs' ? 'item_song' : 'item_songlist'), source);
    return page('tx', array(source)
      .map(kind === 'songs' ? txSong : row => collection('tx', row)), { page: current, limit },
    { total: optionalNumber(data?.meta?.sum), more: next !== -1, next });
  },
  async song(c, { id }) {
    id = input(id);
    return c.cached(['song', id], async () => txSong(await details(c, id)), 3600000);
  },
  playlist(c, p) {
    const id = input(p.id), current = pageNumber(p.page), limit = pageNumber(p.limit, 100);
    const offset = number(p.offset ?? p.continuation?.offset, (current - 1) * limit, 'INVALID_REQUEST');
    return c.cached(['playlist', id, current, limit, p.personal?.kind ?? '', offset],
      () => playlist(c, { ...p, id, page: current, limit, offset }), 3600000);
  },
  async 'playlist.complete'(c, p) {
    const id = input(p.id), items = [], members = [];
    let result, current = 1, offset = 0;
    do {
      result = await playlist(c, { ...p, id, page: current, offset, limit: 100 });
      items.push(...result.items); members.push(...result.memberIds);
      if (!result.consumed) break;
      offset += result.consumed; current++;
    } while (result.more);
    return { ...result, page: 1, items, memberIds: members, count: items.length, complete: !result.more };
  },
  async personalPlaylists(c, { kind = 'all', page: current = 1, limit = 30, continuation }) {
    const account = c.accountRequired(), uid = account.userId, cookies = cookieFields(account.cookie);
    current = pageNumber(current); limit = pageNumber(limit, 30);
    let offset;
    if (kind !== 'created')
      offset = number(continuation?.txOffset, kind === 'subscribed' ? (current - 1) * limit : 0, 'INVALID_REQUEST');
    let created = [];
    if (kind !== 'subscribed') {
      const data = await c.tx('music.musicasset.PlaylistBaseRead', 'GetPlaylistByUin', { uin: uid });
      created = [{ id: 'favorites', name: '我喜欢', cover: '', count: null, source: 'tx', engine: 'QQ',
        ownerId: uid, owned: true, relationship: 'owned', kind: 'favorites', writable: true, available: true,
        locator: { kind: 'qq-favorites', listId: '201', playlistId: 'favorites', dirId: '201' },
        action: { type: 'playlist', id: 'favorites' } },
      ...array(data?.v_playlist).filter(row => scalar(first(row?.dirid, row?.dirId)) !== '201').map(row => personalItem(row, uid, true))];
      c.observe('v_playlist', data?.v_playlist);
    }
    if (kind === 'created') return page('tx', created.slice((current - 1) * limit, current * limit),
      { page: current, limit }, { total: created.length, more: current * limit < created.length, accountId: uid });
    let phase = continuation?.txPhase ?? (kind === 'subscribed' ? 'subscribed' : 'created'), items = [];
    if (phase === 'created') {
      items = created.slice(offset, offset + limit);
      offset += items.length;
      if (offset >= created.length) { phase = 'subscribed'; offset = 0; }
    }
    let more = true, total = null;
    if (phase === 'subscribed' && items.length < limit) {
      const data = await c.tx('music.musicasset.PlaylistFavRead', 'CgiGetPlaylistFavInfo',
        { uin: input(first(cookies.encrypt_uin, cookies.encryptUin)), offset, size: limit - items.length });
      if (data?.hide) fail('FORBIDDEN');
      c.observe('v_list', data?.v_list);
      const source = array(data?.v_list);
      items.push(...source.map(row => personalItem(row, uid, false)).filter(row => !created.some(own => own.id === row.id)));
      offset += source.length; more = (data?.hasmore === true || Boolean(optionalNumber(data?.hasmore))); total = kind === 'subscribed' ? optionalNumber(data?.total) : null;
      if (more && !source.length)
        return { platform: 'tx', accountId: uid, collection: kind, items, page: current, limit, total, more, continuation: null };
    }
    return { platform: 'tx', accountId: uid, collection: kind, items, page: current, limit, total, more,
      continuation: more ? { page: current + 1, txPhase: phase, txOffset: offset } : null };
  },
  async recommendations(c, { kind, page: current = 1 }) {
    c.accountRequired();
    current = pageNumber(current);
    let data, source, more;
    if (kind === 'guess') {
      data = await c.tx('music.radioProxy.MbTrackRadioSvr', 'get_radio_track',
        { id: 99, num: 5, from: 0, scene: 0, song_ids: [] });
      c.observe('tracks', data?.tracks);
      source = array(data?.tracks); more = false;
    } else if (kind === 'radar') {
      data = await c.tx('music.recommend.TrackRelationServer', 'GetRadarSong',
        { Page: current, ReqType: 0, FavSongs: [], EntranceSongs: [] });
      c.observe('VecSongs', data?.VecSongs);
      source = array(data?.VecSongs).map(row => row?.Track); more = Boolean(data?.HasMore);
    } else fail('UNSUPPORTED');
    return page('tx', source.map(txSong), { page: current, limit: 30 }, { more });
  },
  async lyrics(c, { id, musicInfo = {} }) {
    const data = await c.tx('music.musichallSong.PlayLyricInfo', 'GetPlayLyricInfo', {
      songMid: input(first(id, musicInfo.songmid)), type: 1, crypt: 1, lrc_t: 0, qrc: 1, qrc_t: 0,
      trans: 1, trans_t: 0, roma: 1, roma_t: 0, needSingingAnnotations: false }, false);
    return lyricTracks(await qrcLyrics(text(data?.lyric), c.signal), {
      translation: () => qrcLyrics(text(data?.trans), c.signal),
      romanization: () => qrcLyrics(text(data?.roma), c.signal),
    }, c.signal);
  },
  async chorus(c, { id, musicInfo = {} }) {
    return { platform: 'tx', id: input(first(id, musicInfo.songmid)), point: null };
  },
  async audio(c, { id, musicInfo = {}, quality = '128k', qualities }) {
    const mid = input(first(id, musicInfo.songmid)), formats = { '128k': ['M500', 'mp3'],
      '320k': ['M800', 'mp3'], flac: ['F000', 'flac'], master: ['AI00', 'flac'] };
    if (qualities != null && !Array.isArray(qualities)) fail('INVALID_REQUEST');
    qualities = qualities?.map(input);
    let track;
    if (qualities && qualities.length > 1) {
      track = await details(c, mid);
      const file = track.file ?? {}, fields = { '128k': 'size_128mp3', '320k': 'size_320mp3', flac: 'size_flac', master: 'size_new' };
      quality = qualities.find(value => Number(value === 'master' ? file.size_new?.[0] : file[fields[value]]) > 0);
      if (!quality) fail('QUALITY_UNAVAILABLE');
    } else quality = qualities?.[0] ?? quality;
    const format = formats[quality];
    if (!format) fail('UNSUPPORTED');
    const filename = format[0] + mid + mid + '.' + format[1];
    const data = await c.tx('music.vkey.GetVkey', 'UrlGetVkey', { uin: c.account?.userId ?? '0',
      filename: [filename], guid: hex(8), songmid: [mid], songtype: [0], ctx: 0 });
    const row = rows(data.midurlinfo)[0];
    if (!row || !text(row.purl)) fail('MEDIA_UNAVAILABLE');
    if (row.freeTrialInfo) fail('TRIAL_ONLY');
    for (const flag of [row.trial, row.preview])
      if (flag != null && flag !== '' && number(typeof flag === 'boolean' ? Number(flag) : required(flag)) > 0)
        fail('TRIAL_ONLY');
    if (row.result != null && number(required(row.result)) !== 0 || text(row.ekey)) fail('MEDIA_UNAVAILABLE');
    return { url: mediaURL(row.purl, 'https://isure.stream.qqmusic.qq.com/'), headers: {},
      requestQuality: quality, actualQuality: '', qualityVerified: false, full: true,
      duration: track?.interval ?? musicInfo.duration ?? null, size: 0,
      expiresAt: data.expiration == null ? null : c.clock() + Math.min(120, number(data.expiration)) * 1000 };
  },
  async 'audio.options'(c, { id }) {
    const track = await details(c, input(id)), file = track.file ?? {};
    return { qualities: ['128k', ...[['320k', file.size_320mp3], ['flac', file.size_flac], ['master', file.size_new?.[0]]]
      .filter(([, size]) => optionalNumber(size) > 0).map(([quality]) => quality)] };
  },
};
