import { isUtf8 } from 'node:buffer';
import { parseJSONText, isRawJSON } from '@meowfilm/runtime-bridge/json';
import { fail } from './errors.mjs';

export const platforms = Object.freeze(['wy', 'tx', 'kg', 'kw', 'mg']);
export const first = (...values) => values.find(value => value !== undefined && value !== null && value !== '');
export const text = value => typeof value === 'string' ? value : '';
export const scalar = value => typeof value === 'string' ? value :
  typeof value === 'number' || typeof value === 'bigint' ? String(value) : isRawJSON(value) ? value.rawJSON : '';
export const array = value => Array.isArray(value) ? value : [];
export const record = value => value !== null && typeof value === 'object' && !Array.isArray(value) ? value : {};
export function required(value, code = 'INVALID_RESPONSE') {
  const result = scalar(value);
  if (!result) fail(code);
  return result;
}
export function number(value, fallback = 0, code = 'INVALID_RESPONSE') {
  if (value === undefined || value === null || value === '') return fallback;
  let result;
  try { result = Number(isRawJSON(value) ? value.rawJSON : value); }
  catch { fail(code); }
  if (!Number.isFinite(result)) fail(code);
  return result;
}
export function optionalNumber(value) {
  if (isRawJSON(value)) value = value.rawJSON;
  if (!['string', 'number', 'bigint'].includes(typeof value) || value === '') return null;
  const result = Number(value);
  return Number.isFinite(result) ? result : null;
}
export function durationSeconds(value, scale = 1) {
  if (isRawJSON(value)) value = value.rawJSON;
  if (typeof value !== 'string' && typeof value !== 'number') return null;
  const result = optionalNumber(value);
  return result !== null && result > 0 ? result / scale : null;
}
export function rows(value) {
  if (!Array.isArray(value)) fail('INVALID_RESPONSE');
  return value;
}
export function object(value, code = 'INVALID_RESPONSE') {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) fail(code);
  return value;
}
export function utf8(bytes) {
  if (!isUtf8(bytes)) fail('INVALID_RESPONSE');
  return new TextDecoder('utf-8').decode(bytes);
}
/** JSON itself is decoded once. Unsafe integer tokens remain exact strings. */
export function decodeJSON(bytes) {
  try {
    const project = value => {
      if (isRawJSON(value)) return value.rawJSON;
      if (typeof value === 'number' && Number.isInteger(value) && !Number.isSafeInteger(value))
        return String(value);
      if (Array.isArray(value)) return value.map(project);
      if (value !== null && typeof value === 'object')
        return Object.fromEntries(Object.entries(value).map(([key, child]) => [key, project(child)]));
      return value;
    };
    return project(parseJSONText(utf8(bytes)));
  } catch { fail('INVALID_RESPONSE'); }
}
/** Protocol payloads contain JSON values and exact BigInt integer tokens. */
export function encodeProtocolJSON(value) {
  if (isRawJSON(value)) return value.rawJSON;
  if (typeof value === 'bigint') return value.toString();
  if (Array.isArray(value)) return '[' + Array.from(value, item => encodeProtocolJSON(item) ?? 'null').join(',') + ']';
  if (value !== null && typeof value === 'object') {
    const fields = [];
    for (const [key, item] of Object.entries(value)) {
      const encoded = encodeProtocolJSON(item);
      if (encoded !== undefined) fields.push(JSON.stringify(key) + ':' + encoded);
    }
    return '{' + fields.join(',') + '}';
  }
  return JSON.stringify(value);
}
/** Only protocols that actually require a JSON integer use this conversion. */
export function protocolInteger(value) {
  try { return BigInt(required(value, 'INVALID_REQUEST')); }
  catch { fail('INVALID_REQUEST'); }
}
export const input = value => required(value, 'INVALID_REQUEST');
export const pageNumber = (value, fallback = 1) => number(value, fallback, 'INVALID_REQUEST');
export function page(platform, items, p, { total = 0, more = false, next, ...extra } = {}) {
  return { platform, items, page: p.page ?? 1, limit: p.limit ?? 30, total, more,
    continuation: more ? { page: next ?? (p.page ?? 1) + 1 } : null, ...extra };
}
export function image(value) {
  const address = text(value).replaceAll('{size}', '480');
  if (!address) return '';
  try {
    const url = new URL(address.startsWith('//') ? 'https:' + address : address);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) return '';
    if (url.protocol === 'http:' && /(?:^|\.)music\.(?:126\.net|163\.com)$/i.test(url.hostname))
      url.protocol = 'https:';
    // A resource query is part of its identity. Do not discard rendition fields.
    return url.href;
  } catch { return ''; }
}
export function images(...values) {
  for (const value of values) {
    const address = image(value);
    if (address) return address;
  }
  return '';
}
export function mediaURL(value, base) {
  try {
    const url = new URL(required(value), base);
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) fail('INVALID_RESPONSE');
    if (url.protocol === 'http:' && /(?:^|\.)music\.(?:126\.net|163\.com)$/i.test(url.hostname))
      url.protocol = 'https:';
    return url.href;
  } catch { fail('INVALID_RESPONSE'); }
}
export function publicText(value, secrets) {
  const result = text(value);
  let decoded = result;
  for (let at = 0; at < 4; at++) {
    if (secrets.some(secret => secret && decoded.includes(secret))) fail('INVALID_RESPONSE');
    try { const next = decodeURIComponent(decoded); if (next === decoded) break; decoded = next; }
    catch { break; }
  }
  return result;
}
export function sourceKey(platform, info) {
  return JSON.stringify([platform, required(info.songmid),
    platform === 'kg' ? scalar(first(info.hash, info.albumAudioId)).toLowerCase() : '']);
}
export function song(platform, id, title, artist, album, duration, cover, native = {}, artistNames) {
  const info = { ...native, source: platform, songmid: scalar(id), name: text(title),
    singer: text(artist), albumName: text(album), duration, img: image(cover),
    ...(artistNames === undefined ? {} : { artistNames }) };
  // Permanent identity belongs to the native repository, never to a provider.
  return { title: info.name, artist: info.singer, album: info.albumName, duration,
    cover_url: info.img, ...(artistNames === undefined ? {} : { artistNames }),
    sources: info.songmid ? [{ sourceKey: sourceKey(platform, info), source: platform, musicInfo: info }] : [] };
}
export function wySong(raw) {
  raw = record(raw);
  const s = record(first(raw.songInfo, raw.songData, raw.song, raw));
  const album = first(s.al, s.album) ?? {};
  const artists = first(s.ar, s.artists);
  const names = Array.isArray(artists) ? artists.map(a => text(a?.name)).filter(Boolean) : undefined;
  const types = [['128k', 'l'], ['192k', 'm'], ['320k', 'h'], ['flac', 'sq'], ['flac24bit', 'hr']]
    .filter(([, key]) => optionalNumber(s[key]?.size) > 0).map(([type]) => ({ type }));
  return song('wy', first(s.id, s.songId, s.resourceId), first(s.name, s.title),
    names?.join(' / ') ?? text(first(artists, s.artistName, s.artist)),
    typeof album === 'string' ? album : album.name,
    durationSeconds(first(s.dt, s.duration), 1000),
    images(album.picUrl, s.picUrl, s.coverUrl), { albumId: scalar(album.id), types }, names);
}
export function txSong(s) {
  s = record(s);
  const album = s.album ?? {}, names = Array.isArray(s.singer) ? s.singer.map(a => text(a?.name)).filter(Boolean) : undefined;
  const types = [['128k', 'size_128mp3'], ['320k', 'size_320mp3'], ['flac', 'size_flac'], ['flac24bit', 'size_hires']]
    .filter(([, field]) => optionalNumber(s.file?.[field]) > 0).map(([type]) => ({ type }));
  const albumMid = scalar(album.mid);
  return song('tx', s.mid, first(s.title, s.name), names?.join('、') ?? text(s.singer),
    album.name, durationSeconds(s.interval), images(s.cover,
      albumMid ? `https://y.gtimg.cn/music/photo_new/T002R500x500M000${albumMid}.jpg` : ''),
    { qqSongId: scalar(s.id), songId: scalar(s.id), songType: optionalNumber(s.type) ?? 0,
      albumId: albumMid, albumMid, strMediaMid: scalar(s.file?.media_mid), types }, names);
}
export function kgSong(s) {
  s = record(s);
  const audio = s.audio_info ?? {}, album = first(s.albuminfo, s.album_info) ?? {};
  const hash = scalar(first(s.hash, s.FileHash, audio.hash, audio.hash_128)).toUpperCase();
  const full = text(first(s.name, s.FileName, s.filename)).replace(/\.(mp3|flac|m4a|wav|ape|ogg|aac|wma)$/i, '');
  const separator = full.indexOf(' - '), artists = first(s.singerinfo, s.Singers, s.authors);
  const names = Array.isArray(artists) ? artists.map(a =>
    text(first(a?.name, a?.author_name, a?.base?.name, a?.base?.author_name))) : undefined;
  const artist = names?.join('、') ?? text(first(s.SingerName, s.author_name, s.base?.author_name,
    separator < 0 ? '' : full.slice(0, separator)));
  const title = first(s.OriSongName ? s.OriSongName + (s.Suffix ? ' ' + s.Suffix : '') : '',
    s.songname, s.audio_name, s.base?.audio_name, separator < 0 ? full : full.slice(separator + 3));
  const types = [], nativeTypes = {};
  for (const [type, h, size] of [
    ['128k', hash, first(s.filesize, s.FileSize, s.size, audio.filesize, audio.filesize_128)],
    ['320k', first(s.hash_320, s.HQFileHash, audio.hash_320), first(s.filesize_320, s.HQFileSize, audio.filesize_320)],
    ['flac', first(s.hash_flac, s.SQFileHash, audio.hash_flac), first(s.filesize_flac, s.SQFileSize, audio.filesize_flac)],
    ['flac24bit', first(s.hash_high, s.ResFileHash, audio.hash_high), first(s.filesize_high, s.ResFileSize, audio.filesize_high)],
  ]) {
    if (!h || !(optionalNumber(size) > 0)) continue;
    const value = { hash: scalar(h).toUpperCase(), size: scalar(size) };
    types.push({ type, ...value }); nativeTypes[type] = value;
  }
  const milliseconds = first(s.timelen, s.timelength, audio.timelength, audio.duration_128);
  return song('kg', hash, title, artist, first(s.AlbumName, album.name, album.album_name, s.album_name),
    s.Duration == null ? durationSeconds(milliseconds, 1000) : durationSeconds(s.Duration),
    images(s.cover, s.Image, s.trans_param?.union_cover, album.sizable_cover),
    { hash, albumId: scalar(first(s.album_id, s.AlbumID, s.base?.album_id, album.id, album.album_id)),
      albumAudioId: scalar(first(s.mixsongid, s.add_mixsongid, s.album_audio_id, s.MixSongID, s.base?.album_audio_id)),
      audioId: scalar(first(s.audio_id, audio.audio_id)),
      types, _types: nativeTypes }, names);
}
export function collection(platform, raw) {
  raw = record(raw);
  const id = platform === 'wy' ? first(raw.id, raw.playlistId) :
    platform === 'tx' ? first(raw.id, raw.tid, raw.dissid) : first(raw.global_collection_id, raw.gid);
  return { id: scalar(id), name: text(first(raw.name, raw.title, raw.dissname, raw.dirName, raw.specialname)),
    cover: images(raw.picUrl, raw.coverImgUrl, raw.picurl, raw.imgurl, raw.img, raw.cover, raw.logo, raw.pic),
    count: optionalNumber(first(raw.trackCount, raw.songCount, raw.songnum, raw.songNum, raw.song_cnt, raw.songcount, raw.song_count, raw.count)),
    playCount: optionalNumber(raw.playCount),
    creator: text(first(raw.creator?.nickname, raw.nickname, raw.creator_nick, raw.list_create_username)),
    source: platform, engine: { wy: 'Netease', tx: 'QQ', kg: 'Kugou', kw: 'Kuwo', mg: 'Migu' }[platform] };
}
