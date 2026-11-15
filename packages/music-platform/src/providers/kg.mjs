// SPDX-License-Identifier: GPL-3.0-only
// Pinned KuGouMusicApi / meowmusic recipes; see LICENSES/NOTICE.md.
import { collection, first, image, input, kgSong, mediaURL, number, optionalNumber, page, pageNumber, required, rows, scalar, text } from '../model.mjs';
import { cookieFields, form, kgSuccess, kgSalt, md5 } from '../protocols.mjs';
import { krcLyrics } from '../lyrics.mjs';
import { fail } from '../errors.mjs';
import { kgHome, kgChannels, kgChannel } from './kg-discovery.mjs';

const flagNumber = value => typeof value === 'boolean' ? Number(value) :
  value === 'false' ? 0 : value === 'true' ? 1 : number(required(value));

export function kgIdentity(c) {
  const cookies = cookieFields(c.account?.cookie), device = c.client();
  return { mid: cookies.KUGOU_API_MID ?? device.mid, dfid: cookies.dfid ?? device.dfid ?? '-',
    uid: c.account?.userId ?? 0, token: cookies.token ?? '' };
}
async function details(c, id) {
  const { mid, dfid, uid, token } = kgIdentity(c), time = c.clock();
  const raw = await c.kg('/v1/audio/audio', {}, 'kmr.service.kugou.com', {
    appid: 1005, clientver: 20489, clienttime: time, data: [{ hash: id, audio_id: 0 }], dfid,
    key: md5('1005' + kgSalt + '20489' + time), mid, ...(uid ? { userid: uid } : {}), ...(token ? { token } : {}) });
  const row = rows(raw.data)[0];
  if (!row) fail('NOT_FOUND');
  return kgSong(row);
}
async function playlist(c, p) {
  const { id, page: current, limit } = p;
  if (p.personal?.kind === 'kugou-owned' || id.startsWith('0:')) {
    c.accountRequired();
    const { uid, token } = kgIdentity(c), listid = input(p.personal?.listId ?? id.slice(2));
    const raw = await c.kg('/v4/get_list_all_file_v3', {}, 'cloudlist.service.kugou.com', {
      listid, userid: uid, type: 0, page: current, pagesize: Math.min(30, limit), area_code: 1, allplatform: 1, show_cover: 1, token });
    const data = raw.data ?? raw, info = data.list_info ?? {}, source = rows(first(data.info, data.songs, raw.info) ?? []);
    const total = number(first(data.count, data.total, info.count, raw.count, raw.total));
    return page('kg', source.map(kgSong), { page: current, limit: Math.min(30, limit) },
      { id, name: text(first(info.name, p.personal?.name)), cover: image(first(info.cover, info.imgurl)),
        total, more: data.has_more == null ? current * Math.min(30, limit) < total : Boolean(Number(data.has_more)),
        consumed: source.length, memberIds: source.map(row => required(row.fileid)),
        memberSorts: source.map(row => optionalNumber(row.sort)), version: scalar(first(info.list_ver, data.list_ver)), source: 'kg', engine: 'Kugou' });
  }
  const offset = p.offset;
  const raw = await c.kg('/pubsongs/v2/get_other_list_file_nofilt', { area_code: 1, begin_idx: offset,
    plat: 1, type: 1, mode: 1, personal_switch: 1, extend_fields: 'abtags,hot_cmt,popularization', pagesize: limit, global_collection_id: id });
  const data = raw.data, info = data.list_info ?? {}, source = rows(first(data.songs, data.info) ?? []);
  const total = number(first(info.count, data.count));
  const more = data.has_more == null ? offset + source.length < total : Boolean(Number(data.has_more));
  if (more && !source.length) fail('INVALID_RESPONSE');
  return { ...page('kg', source.map(kgSong), { page: current, limit }, { id: scalar(info.global_collection_id) || id,
    name: text(info.name), cover: image(first(info.cover, info.imgurl)), total, consumed: source.length,
    more, source: 'kg', engine: 'Kugou' }),
    continuation: more ? { page: current + 1, offset: offset + source.length } : null };
}
export const kgProvider = {
  home: kgHome, channels: kgChannels, channel: kgChannel,
  async search(c, { keyword, kind = 'songs', page: current = 1, limit = 30 }) {
    current = pageNumber(current); limit = pageNumber(limit, 30);
    const songs = kind === 'songs', args = { keyword: input(keyword), page: current, pagesize: limit,
      platform: 'AndroidFilter', iscorrection: 1, area_code: 1, ...(songs ? { privilegefilter: 0, dopicfull: 1 } : { albumhide: 0, nocollect: 0 }) };
    const raw = await c.kg(songs ? '/v2/search/song' : '/v1/search/special', args, 'complexsearch.kugou.com'), data = raw.data;
    const source = rows(first(data.lists, data.info) ?? []), total = number(data.total);
    return page('kg', source.map(songs ? kgSong : row => collection('kg', row)), { page: current, limit },
      { total, more: data.has_next == null ? current * limit < total : Boolean(Number(data.has_next)), consumed: source.length });
  },
  song(c, { id }) {
    id = input(id).toUpperCase();
    return c.cached(['song', id], () => details(c, id), 3600000);
  },
  playlist(c, p) {
    const id = input(p.id), current = pageNumber(p.page), limit = pageNumber(p.limit, 100);
    const params = { ...p, id, page: current, limit }, parts = ['playlist', id, current, limit, p.personal?.kind ?? ''];
    if (p.personal?.kind !== 'kugou-owned' && !id.startsWith('0:')) {
      params.offset = number(p.offset ?? p.continuation?.offset, (current - 1) * limit, 'INVALID_REQUEST');
      parts.push(params.offset);
    }
    return c.cached(parts, () => playlist(c, params), 3600000);
  },
  async 'playlist.complete'(c, p) {
    const id = input(p.id);
    let result, current = 1, offset = 0;
    const items = [], members = [], sorts = [];
    do {
      result = await playlist(c, { ...p, id, page: current, offset, limit: p.personal || id.startsWith('0:') ? 30 : 100 });
      items.push(...result.items); members.push(...result.memberIds ?? []); sorts.push(...result.memberSorts ?? []);
      if (result.more && !result.consumed) fail('INVALID_RESPONSE');
      offset += result.consumed; current++;
    } while (result.more);
    // Private lists carry a global sort position; sort once after all protocol pages.
    if (sorts.length === items.length && sorts.every(value => value !== null)) {
      const order = items.map((_, index) => index).sort((a, b) => sorts[a] - sorts[b]);
      return { ...result, items: order.map(index => items[index]), memberIds: order.map(index => members[index]),
        memberSorts: order.map(index => sorts[index]),
        count: items.length, more: false, continuation: null, complete: true };
    }
    return { ...result, items, memberIds: members, memberSorts: sorts,
      count: items.length, more: false, continuation: null, complete: true };
  },
  async personalPlaylists(c, { page: current = 1, limit = 30 }) {
    c.accountRequired();
    current = pageNumber(current); limit = Math.min(30, pageNumber(limit, 30));
    const { uid, token } = kgIdentity(c);
    const raw = await c.kg('/v7/get_all_list', { plat: 1, userid: uid, token }, 'cloudlist.service.kugou.com',
      { userid: uid, token, total_ver: 979, type: 2, page: current, pagesize: limit });
    const data = raw.data ?? raw, source = rows(first(data.info, data.songs, raw.info) ?? []);
    const total = optionalNumber(first(data.count, data.total, raw.count, raw.total)) ??
      (['list_count', 'collect_count', 'album_count'].some(key => data[key] != null) ?
        ['list_count', 'collect_count', 'album_count'].reduce((sum, key) => sum + number(data[key]), 0) : null);
    const items = source.map(row => {
      const listId = required(row.listid), type = number(row.type), owner = scalar(row.list_create_userid);
      const own = type === 0 && owner === uid && Number(row.source) !== 2;
      const name = text(row.name), special = [1, 2].includes(Number(row.is_def)) || Number(row.is_default) === 1;
      const global = scalar(first(row.global_collection_id, row.list_create_gid));
      const id = type + ':' + listId, locator = { kind: own ? 'kugou-owned' : 'public', listId, name,
        totalVersion: scalar(first(data.total_ver, raw.total_ver, row.total_ver)),
        creatorId: owner, creatorListId: scalar(row.list_create_listid) };
      for (const key of ['sort', 'tags', 'intro', 'pic']) if (row[key] != null) locator[key] = row[key];
      return { id, name, cover: image(first(row.cover, row.imgurl)), count: optionalNumber(row.count),
        source: 'kg', engine: 'Kugou', ownerId: owner, owned: own, relationship: own ? 'owned' : 'subscribed',
        kind: own && special && ['我喜欢', '我喜欢的音乐'].includes(name) ? 'favorites' : 'playlist',
        writable: own && !special, locator,
        action: { type: own || global ? 'playlist' : 'unsupported', id: own ? id : global, locator: own ? locator : null },
        available: own || Boolean(global) };
    });
    return page('kg', items, { page: current, limit }, { total, more: data.has_more == null ?
      total == null ? source.length > 0 : current * limit < total : Boolean(Number(data.has_more)), accountId: uid });
  },
  async recommendations(c, { kind }) {
    c.accountRequired();
    const { mid, uid, token } = kgIdentity(c), time = c.clock();
    let raw;
    if (kind === 'daily') raw = await c.kg('/everyday_song_recommend', { platform: 'ios' }, 'everydayrec.service.kugou.com', {});
    else if (kind === 'fm') raw = await c.kg('/v2/personal_recommend', {}, 'persnfm.service.kugou.com', {
      appid: 1005, clienttime: time, mid, action: 'login', recommend_source_locked: 0, song_pool_id: 0,
      callerid: 0, m_type: 1, platform: 'ios', area_code: 1, remain_songcnt: 0, clientver: 20489,
      is_overplay: 0, mode: 'normal', fakem: 'ca981cfc583a4c37f28d2d49000013c16a0a',
      key: md5('1005' + kgSalt + '20489' + time), userid: uid, kguid: uid, token });
    else fail('UNSUPPORTED');
    const source = rows(raw.data.song_list);
    return { platform: 'kg', kind, items: source.map(row => kgSong({ ...row, name: row.filename,
      Duration: row.time_length, AlbumName: row.album_name, cover: first(row.sizable_cover, row.trans_param?.union_cover),
      filesize: first(row.file_size, row.filesize_128) })), more: false, continuation: null };
  },
  async lyrics(c, { id, musicInfo = {} }) {
    const hash = input(first(id, musicInfo.hash, musicInfo.songmid)).toUpperCase();
    const args = { album_audio_id: scalar(musicInfo.albumAudioId) || '0', appid: 1005, clientver: 20489,
      duration: Math.round(number(musicInfo.duration, 0, 'INVALID_REQUEST') * 1000), hash, keyword: '', lrctxt: 1, man: 'no' };
    args.signature = md5(kgSalt + Object.keys(args).sort().map(key => `${key}=${scalar(args[key])}`).join('') + kgSalt);
    const data = await c.read({ url: 'https://lyrics.kugou.com/v1/search?' + form(args), maximumBytes: 512 * 1024 });
    if (number(data.status) === 404) return { text: '', translation: '', romanization: '' };
    kgSuccess(data);
    if (number(required(data.status)) !== 200) fail('UPSTREAM_ERROR');
    const source = rows(data.candidates);
    if (source.length === 0) return { text: '', translation: '', romanization: '' };
    const candidate = source[0];
    const download = { ver: 1, client: 'android', id: required(candidate.id),
      accesskey: required(candidate.accesskey), fmt: 'krc', charset: 'utf8' };
    download.signature = md5(kgSalt + Object.keys(download).sort().map(key => `${key}=${scalar(download[key])}`).join('') + kgSalt);
    const result = kgSuccess(await c.read({ url: 'https://lyrics.kugou.com/download?' + form(download), maximumBytes: 512 * 1024 }));
    if (number(required(result.status)) !== 200 || number(required(result.error_code)) !== 0) fail('UPSTREAM_ERROR');
    return text(result.content) ? krcLyrics(result.content) : { text: '', translation: '', romanization: '' };
  },
  async chorus(c, { id, musicInfo = {} }) {
    id = input(first(id, musicInfo.hash, musicInfo.songmid)).toUpperCase();
    const raw = await c.read({ url: 'https://expendablekmrcdn.kugou.com/v1/audio_climax/audio?' +
      form({ data: JSON.stringify([{ hash: id }]) }), maximumBytes: 65536 });
    if (number(required(raw.status)) !== 1) fail('UPSTREAM_ERROR');
    const row = rows(raw.data)[0];
    const start = optionalNumber(row?.start_time), end = optionalNumber(row?.end_time);
    return { platform: 'kg', id, point: start !== null && end !== null && start >= 0 && end > start ?
      { start: start / 1000, end: end / 1000 } : null };
  },
  async audio(c, { id, musicInfo = {}, quality = '128k', qualities }) {
    const hash = input(first(id, musicInfo.hash, musicInfo.songmid)).toUpperCase();
    if (qualities != null && !Array.isArray(qualities)) fail('INVALID_REQUEST');
    qualities = qualities?.map(input);
    const { mid, uid } = kgIdentity(c);
    const levels = { '128k': '128', '320k': '320', flac: 'flac', flac24bit: 'high' };
    quality = qualities?.[0] ?? quality;
    if (!levels[quality]) fail('UNSUPPORTED');
    let info = musicInfo;
    if (quality !== '128k' && !info._types?.[quality] || qualities?.length > 1)
      info = (await details(c, hash)).sources[0].musicInfo;
    quality = qualities?.find(value => value === '128k' || info._types?.[value]) ?? quality;
    if (!levels[quality]) fail('UNSUPPORTED');
    if (quality !== '128k' && !info._types?.[quality]) fail('QUALITY_UNAVAILABLE');
    const resourceHash = quality === '128k' ? hash : required(info._types?.[quality]?.hash);
    const raw = await c.kg('/v5/url', { album_id: scalar(info.albumId) || '0', area_code: 1, hash: resourceHash.toLowerCase(),
      ssa_flag: 'is_fromtrack', version: 11430, page_id: 151369488, quality: levels[quality],
      album_audio_id: scalar(info.albumAudioId) || '0', behavior: 'play', pid: 2, cmd: 26, pidversion: 3001,
      IsFreePart: 0, ppage_id: '463467626,350369493,788954147', cdnBackup: 1, module: '', clientver: 11430,
      key: md5(resourceHash.toLowerCase() + '57ae12eb6890223e355ccfcb74edf70d' + '1005' + mid + uid) }, 'trackercdn.kugou.com');
    const result = raw.url == null ? raw.data : raw;
    if (!result) fail('MEDIA_UNAVAILABLE');
    let full = false;
    for (const record of [raw, result, raw.trans_param, result.trans_param].filter(Boolean)) {
      for (const key of ['is_free_part', 'IsFreePart', 'free_part']) if (record[key] != null) {
        if (flagNumber(record[key]) !== 0) fail('TRIAL_ONLY');
        full = true;
      }
      for (const key of ['pay_type', 'privilege', 'fail_process'])
        if (record[key] != null && number(required(record[key])) !== 0) fail('MEDIA_UNAVAILABLE');
      for (const key of ['error_code', 'errcode', 'err_code'])
        if (record[key] != null && number(required(record[key])) !== 0) fail('UPSTREAM_ERROR');
      if (record.status != null && number(required(record.status)) !== 1 ||
          record.code != null && ![0, 200].includes(number(required(record.code))) ||
          record.error != null && record.error !== '' && flagNumber(record.error) !== 0) fail('UPSTREAM_ERROR');
    }
    const url = Array.isArray(result.url) ? result.url[0] : result.url;
    if (!url) fail('MEDIA_UNAVAILABLE');
    if (!full) fail('INVALID_RESPONSE');
    return { url: mediaURL(url), headers: {}, requestQuality: quality, actualQuality: '', qualityVerified: false,
      duration: info.duration ?? null, size: number(first(result.fileSize, result.filesize)), expiresAt: null, full };
  },
  async 'audio.options'(c, { id }) {
    const info = (await details(c, input(id))).sources[0].musicInfo;
    return { qualities: ['128k', ...['320k', 'flac', 'flac24bit'].filter(quality => info._types?.[quality])] };
  },
};
