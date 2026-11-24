// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus / api-enhanced / QQMusicApi / KuGouMusicApi library recipes.
import { constants, publicEncrypt, randomInt, randomUUID } from 'node:crypto';
import { aes, cookieFields, decrypt, form, kgPublicKey, kgSalt, kgSuccess, md5, wySuccess } from './protocols.mjs';
import { decodeJSON, encodeProtocolJSON, first, image, input, kgSong, number, protocolInteger, required, rows, scalar, song as minimalSong, text } from './model.mjs';
import { fail } from './errors.mjs';
import { wyProvider } from './providers/wy.mjs';
import { txProvider } from './providers/tx.mjs';
import { kgProvider } from './providers/kg.mjs';

const providers = { wy: wyProvider, tx: txProvider, kg: kgProvider };
/** The callback marks the actual send, not an optimistic owner admission. */
export async function settleWrite(c, action) {
  let status = 'notSent';
  const callbacks = {
    dispatched() { c.dispatched(); status = 'unknown'; },
    rejected() { status = 'rejected'; },
  };
  try { return { status: 'confirmed', ...await action(callbacks), errorCode: null }; }
  catch (error) { return { status: error.beforeSend ? 'notSent' : status, errorCode: c.errorCode(error) }; }
}
async function directory(c) {
  c.accountRequired();
  const provider = providers[c.platform];
  if (!provider) fail('UNSUPPORTED');
  const items = [];
  let continuation = null, page = 1;
  do {
    const result = await provider.personalPlaylists(c, { kind: 'all', page, limit: c.platform === 'kg' ? 30 : 100, continuation });
    for (const row of result.items) required(row.id);
    items.push(...result.items);
    if (result.more && !result.continuation) fail('INVALID_RESPONSE');
    continuation = result.continuation;
    if (continuation) {
      const next = continuation.page ?? page + 1;
      if (next <= page) fail('INVALID_RESPONSE');
      page = next;
    }
  } while (continuation);
  return items;
}
function subscription(row) {
  return { id: row.action?.id ?? row.id, remoteId: row.action?.id ?? row.id, relationId: row.id,
    name: row.name, cover: row.cover, count: row.count ?? 0, locator: row.locator ?? {} };
}
async function readEntry(c, row) {
  const result = await providers[c.platform]['playlist.complete'](c, { id: row.id, personal: row.locator, refresh: true });
  if (!result.complete || result.more || result.continuation) fail('INVALID_RESPONSE');
  const info = song => song?.sources.find(source => source.source === c.platform)?.musicInfo;
  const known = new Map();
  for (const song of result.items) {
    const songId = scalar(info(song)?.songmid);
    if (songId) known.set(songId, song);
  }
  const ids = result.memberIds ?? result.items.map(song => scalar(info(song)?.songmid));
  const occurrences = new Map();
  const members = ids.map((rawId, at) => {
    const memberId = required(rawId);
    const candidate = c.platform === 'kg' ? result.items[at] : known.get(memberId);
    const candidateId = scalar(info(candidate)?.songmid);
    const song = candidateId ? candidate : null;
    const recordingId = c.platform === 'kg' ? required(candidateId) : memberId;
    const occurrence = occurrences.get(memberId) ?? 0;
    occurrences.set(memberId, occurrence + 1);
    return { id: recordingId,
      key: c.platform === 'kg' ? memberId : JSON.stringify([memberId, occurrence]),
      // Membership is a real locator even when optional matching metadata is absent.
      // Empty metadata is not an invented title, artist, or duration.
      song: song ?? minimalSong(c.platform, recordingId, '', '', '', null, ''),
      sort: result.memberSorts?.[at] ?? null, hasMetadata: song != null };
  });
  return { id: row.id, name: result.name || row.name, kind: row.kind ?? 'playlist', writable: row.writable || row.kind === 'favorites',
    owner: row.ownerId ?? c.account.userId, members,
    locator: { ...row.locator, ...(result.version ? { version: result.version } : {}) } };
}
async function kgCloud(c, path, body, callbacks) {
  const cookies = cookieFields(c.accountRequired().cookie), client = c.client();
  const alphabet = path === '/v2/delete_list' ? '0123456789abcdefghijklmnopqrstuvwxyz' :
    '0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ';
  const secret = Array.from({ length: 6 }, () => alphabet[randomInt(alphabet.length)]).join('');
  const digest = md5(secret), key = digest.slice(0, 16), iv = digest.slice(16);
  const encrypted = aes(encodeProtocolJSON(body), key, 'cbc', iv);
  const portrait = publicEncrypt({ key: kgPublicKey, padding: constants.RSA_PKCS1_PADDING },
    Buffer.from(JSON.stringify({ aes: secret, uid: c.account.userId, token: required(cookies.token) }))).toString('hex').toUpperCase();
  const time = String(Math.floor(Date.now() / 1000)), query = { appid: 1005, clientver: 20489, mid: cookies.KUGOU_API_MID ?? client.mid,
    dfid: cookies.dfid ?? client.dfid ?? '-', clienttime: time, key: md5('1005' + kgSalt + '20489' + time), p: portrait };
  let content = encrypted, contentType = 'application/json; charset=utf-8';
  if (path === '/v2/delete_list') {
    content = encrypted.toString('base64'); contentType = 'application/x-www-form-urlencoded';
    Object.assign(query, { last_area: 'gztx', last_time: time, uuid: '-', userid: c.account.userId, token: cookies.token });
    query.signature = md5(kgSalt + Object.keys(query).sort().map(key => key + '=' + scalar(query[key])).join('') + content + kgSalt);
  }
  const response = await c.response({ url: 'https://gateway.kugou.com' + path + '?' + form(query), method: 'POST', json: false,
    body: content, headers: { 'Content-Type': contentType, 'x-router': 'cloudlist.service.kugou.com',
      mid: query.mid, dfid: query.dfid, clienttime: time, 'kg-rc': '1', 'kg-thash': '5d816a0',
      'kg-rec': '1', 'kg-rf': 'B9EDA08A64250DEFFBCADDEE00F8F25F' } }, callbacks);
  if (response.headers.get('ssa-code')) { callbacks.rejected(); fail('VERIFICATION_REQUIRED'); }
  let plain;
  try { plain = decrypt(response.value, key, 'cbc', iv); }
  catch {
    // A plaintext business rejection is meaningful; bad encrypted success is
    // not a confirmed write and must never be accepted as one.
    const rejection = decodeJSON(response.value);
    try { kgSuccess(rejection); }
    catch (error) { if (error.code !== 'INVALID_RESPONSE') callbacks.rejected(); throw error; }
    fail('CLOUD_RESPONSE_UNCONFIRMED');
  }
  const raw = decodeJSON(plain);
  try { kgSuccess(raw); }
  catch (error) { if (error.code !== 'INVALID_RESPONSE') callbacks.rejected(); throw error; }
  return raw;
}
const PREPARATION_TTL_MS = 5 * 60 * 1000;
const MAX_PREPARED = 64;

export function createSyncOperations() {
  const prepared = new Map();
  function prunePrepared(now) {
    for (const [key, entry] of prepared) if (entry.expiresAt <= now) prepared.delete(key);
    while (prepared.size >= MAX_PREPARED) prepared.delete(prepared.keys().next().value);
  }
  const operations = {
  async 'sync.prepare'(c, { action, id }) {
    c.accountRequired(); id = input(id);
    if (c.platform !== 'wy' || !['subscribe', 'unsubscribe'].includes(action)) fail('UNSUPPORTED');
    prunePrepared(Date.now());
    const token = await c.token(), preparationId = randomUUID();
    if (!c.isCurrent()) fail('STALE_SESSION');
    prepared.set(preparationId, { platform: c.platform, preparationId, account: c.account, action, id, token,
      expiresAt: Date.now() + PREPARATION_TTL_MS });
    return { preparationId };
  },
  async 'sync.read'(c, { kinds = ['favorites', 'playlist'], collectionId, subscriptionsOnly = false }) {
    let includePlaylists;
    if (!subscriptionsOnly) {
      try { includePlaylists = kinds.includes('playlist'); }
      catch { fail('INVALID_REQUEST'); }
    }
    const items = await directory(c);
    const directoryFacts = items.map(row => ({ id: row.id, name: row.name, kind: row.kind ?? 'playlist',
      owned: Boolean(row.owned), writable: Boolean(row.writable), ownerId: row.ownerId ?? '',
      count: row.count ?? null, locator: row.locator ?? {} }));
    const subscriptions = items.filter(row => !row.owned).map(subscription);
    if (subscriptionsOnly) return { accountId: c.account.userId, directory: directoryFacts, collections: [], subscriptions };
    const collections = [];
    for (const row of items) if (row.owned && (row.writable || row.kind === 'favorites') &&
      (collectionId ? row.id === collectionId : kinds.includes(row.kind ?? 'playlist')))
      collections.push(await readEntry(c, row));
    return { accountId: c.account.userId, directory: directoryFacts, collections, subscriptions: includePlaylists ? subscriptions : [] };
  },
  async 'sync.target'(c, { id, name, cover = '', count = 0 }) {
    c.accountRequired(); id = input(id);
    if (c.platform !== 'kg') return { id, remoteId: id, relationId: id, name: text(name), cover: image(cover), count, locator: {} };
    const cookies = cookieFields(c.account.cookie);
    const result = await c.kg('/v3/get_list_info', {}, 'pubsongs.kugou.com', { userid: c.account.userId,
      token: required(cookies.token), data: [{ global_collection_id: id }] });
    const row = rows(result.data)[0];
    if (!row) fail('NOT_FOUND');
    if (required(row.global_collection_id) !== id) fail('REMOTE_CHANGED');
    return { id: required(row.global_collection_id), name: text(row.name), cover: image(row.pic), count: number(row.count),
      locator: { creatorId: required(row.list_create_userid), creatorListId: required(row.list_create_listid) } };
  },
  async 'sync.write'(c, { action, remote = {}, id, name, ids = [], songs = {}, members, preparationId }) {
    c.accountRequired();
    const platform = c.platform;
    if (!providers[platform]) fail('UNSUPPORTED');
    if (platform === 'tx' && action === 'order') return { status: 'notSent', errorCode: 'UNSUPPORTED' };
    // All extra fields are ignored. The native journal supplies only the chosen mutation.
    return settleWrite(c, async callbacks => {
      let raw;
      if (platform === 'wy') {
        switch (action) {
          case 'create':
            raw = await c.wy('/api/playlist/create', { name: input(name), privacy: 10, type: 'NORMAL' }, true, callbacks);
            c.invalidateCache();
            return { createdId: required(first(raw.id, raw.playlist?.id)), createdName: text(first(raw.playlist?.name, name)) };
          case 'delete': raw = await c.wy('/api/playlist/remove', { ids: JSON.stringify([input(id ?? remote?.id)]) }, true,
            { ...callbacks, dataStatus: true }); break;
          case 'rename':
            raw = await c.wy('/api/batch', { '/api/playlist/update/name': JSON.stringify({ id: input(id ?? remote?.id), name: input(name) }) }, false, callbacks);
            try { wySuccess(raw['/api/playlist/update/name'] ?? {}); }
            catch (error) { if (error.code !== 'INVALID_RESPONSE') callbacks.rejected(); throw error; }
            break;
          case 'add': case 'remove':
            if (!Array.isArray(ids)) fail('INVALID_REQUEST');
            ids = ids.map(input);
            if (remote?.kind === 'favorites' && ids.length === 1) {
              raw = await c.wy('/api/radio/like', { trackId: ids[0], like: action === 'add', alg: 'itembased', time: '3' }, true, callbacks);
            } else raw = await c.wy('/api/playlist/manipulate/tracks', { pid: input(id ?? remote?.id), op: action === 'add' ? 'add' : 'del',
              trackIds: JSON.stringify(ids), imme: 'true' }, false, callbacks);
            break;
          case 'order':
            if (!Array.isArray(ids)) fail('INVALID_REQUEST');
            raw = await c.wy('/api/playlist/manipulate/tracks', { pid: input(id ?? remote?.id), op: 'update', trackIds: JSON.stringify(ids.map(input)) }, false, callbacks); break;
          case 'subscribe': case 'unsubscribe': {
            id = input(id ?? remote?.id);
            let token;
            if (preparationId != null) {
              prunePrepared(Date.now());
              const prepId = input(preparationId);
              const entry = prepared.get(prepId);
              if (!entry || entry.platform !== c.platform || entry.account !== c.account || entry.action !== action || entry.id !== id)
                fail('STALE_SESSION');
              prepared.delete(prepId); token = entry.token;
            } else token = await c.token();
            raw = await c.wy('/api/playlist/' + action, { id }, false, { ...callbacks, token }); break;
          }
          default: fail('UNSUPPORTED');
        }
      } else if (platform === 'tx') {
        const cookies = cookieFields(c.account.cookie);
        let method, module = 'music.musicasset.PlaylistBaseWrite', params;
        switch (action) {
          case 'create': method = 'AddPlaylist'; params = { dirName: input(name) }; break;
          case 'delete': method = 'DelPlaylist'; params = { dirId: protocolInteger(remote?.locator?.dirId) }; break;
          case 'rename': method = 'EditPlaylist'; params = { dirId: protocolInteger(remote?.locator?.dirId), dirName: input(name) }; break;
          case 'subscribe': case 'unsubscribe':
            id ??= remote?.id;
            module = 'music.musicasset.PlaylistFavWrite'; method = action === 'subscribe' ? 'FavPlaylist' : 'CancelFavPlaylist';
            params = { uin: input(first(cookies.encrypt_uin, cookies.encryptUin)), v_playlistId: [protocolInteger(id)] }; break;
          case 'add': case 'remove': {
            if (!Array.isArray(ids)) fail('INVALID_REQUEST');
            ids = ids.map(input);
            module = 'music.musicasset.PlaylistDetailWrite'; method = action === 'add' ? 'AddSonglist' : 'DelSonglist';
            const values = [];
            params = { dirId: protocolInteger(remote?.locator?.dirId), tid: remote?.kind === 'favorites' ? 0 : protocolInteger(id ?? remote?.id),
              bFmtUtf8: action === 'add' ? true : 1, v_songInfo: values };
            for (const mid of ids) {
              const raw = await c.tx('music.trackInfo.UniformRuleCtrl', 'CgiGetTrackInfo',
                { ctx: 0, client: 1, types: [0], modify_stamp: [0], mids: [mid] });
              const source = rows(raw.tracks)[0];
              if (!source) fail('NOT_FOUND');
              if (required(first(source.mid, source.songmid)) !== mid) fail('REMOTE_CHANGED');
              values.push({ songId: protocolInteger(required(source.id)),
                songType: action === 'remove' ? 0 : number(required(source.type)) });
            }
            break;
          }
          default: fail('UNSUPPORTED');
        }
        raw = await c.tx(module, method, params, true, callbacks);
        if (['subscribe', 'unsubscribe'].includes(action)) {
          const result = required(raw.result);
          if (!Number.isInteger(Number(result))) fail('INVALID_RESPONSE');
          if (Number(result) !== 0 ||
              rows(raw.v_failedPlaylistId ?? []).some(value => scalar(value) === scalar(id))) {
            callbacks.rejected(); fail('UPSTREAM_ERROR');
          }
        }
        if (raw.retCode != null) {
          const code = required(raw.retCode);
          if (!Number.isInteger(Number(code))) fail('INVALID_RESPONSE');
          if (Number(code) !== 0) { callbacks.rejected(); fail('UPSTREAM_ERROR'); }
        }
        if (action === 'create') {
          c.invalidateCache();
          return { createdId: required(raw.result?.tid), createdName: text(first(raw.result?.dirName, name)) };
        }
      } else {
        const cookies = cookieFields(c.account.cookie), credential = { userid: c.account.userId, token: required(cookies.token) };
        let path, router = 'cloudlist.service.kugou.com', query = {}, body;
        switch (action) {
          case 'create': case 'subscribe':
            path = '/cloudlist.service/v5/add_list'; router = null;
            query = action === 'create' ? { last_time: Math.floor(Date.now() / 1000), last_area: 'gztx' } : {};
            body = { ...credential, total_ver: 0, name: input(name ?? remote?.name), type: action === 'create' ? 0 : 1,
              source: 1, is_pri: action === 'create' ? 1 : 0, list_create_gid: action === 'create' ? '' : input(id ?? remote?.id), from_shupinmv: 0,
              ...(action === 'subscribe' ? { list_create_userid: input(remote?.locator?.creatorId), list_create_listid: input(remote?.locator?.creatorListId) } : {}) };
            break;
          case 'delete': case 'unsubscribe': {
            let listId = remote?.locator?.listId;
            if (listId == null) { id = input(id ?? remote?.id); listId = id.slice(id.indexOf(':') + 1); }
            raw = await kgCloud(c, '/v2/delete_list', { listid: protocolInteger(listId), total_ver: 0, type: 1 }, callbacks); break;
          }
          case 'rename': {
            const locator = remote?.locator ?? {};
            let listId = locator.listId;
            if (listId == null) { id = input(id ?? remote?.id); listId = id.slice(id.indexOf(':') + 1); }
            body = { total_ver: protocolInteger(locator.totalVersion), listid: protocolInteger(listId), type: 0, name: input(name) };
            for (const key of ['sort', 'tags', 'intro', 'pic']) if (locator[key] != null) body[key] = locator[key];
            raw = await kgCloud(c, '/v1/modify_list', body, callbacks); break;
          }
          case 'add': {
            let listId = remote?.locator?.listId;
            if (listId == null) { id = input(id ?? remote?.id); listId = id.slice(id.indexOf(':') + 1); }
            if (!Array.isArray(ids)) fail('INVALID_REQUEST');
            const requested = ids.map(hash => {
              hash = input(hash);
              try { return { hash, hint: songs[hash]?.sources?.find(row => row.source === 'kg')?.musicInfo ?? songs[hash] ?? {} }; }
              catch { fail('INVALID_REQUEST'); }
            });
            path = '/cloudlist.service/v6/add_song'; router = null;
            query = { last_time: Math.floor(Date.now() / 1000), last_area: 'gztx' };
            const values = [];
            body = { ...credential, listid: input(listId), list_ver: 0, type: 0, slow_upload: 1, scene: 'false;null', data: values };
            for (const { hash, hint } of requested) {
              const exact = hash.toUpperCase(), usable = value => value != null && value !== '' && scalar(value) !== '0';
              if (first(hint.hash, hint.songmid) && scalar(first(hint.hash, hint.songmid)).toUpperCase() !== exact) fail('REMOTE_CHANGED');
              let source = (await kgProvider.song(c, { id: exact })).sources[0]?.musicInfo;
              if (!source) fail('NOT_FOUND');
              if (source.hash !== exact) fail('REMOTE_CHANGED');
              const checkHints = value => {
                for (const key of ['albumId', 'albumAudioId'])
                  if (usable(hint[key]) && usable(value[key]) && scalar(hint[key]) !== value[key]) fail('REMOTE_CHANGED');
              };
              checkHints(source);
              if (!source.name || source.name === '未知歌曲' || !usable(source.albumId) || !usable(source.albumAudioId)) {
                const raw = await c.kgMixRow(exact, scalar(first(hint.albumAudioId, source.albumAudioId)));
                const next = kgSong({ ...raw, hash: first(raw?.hash, raw?.FileHash, raw?.audio_info?.hash, raw?.audio_info?.hash_128, exact) }).sources[0].musicInfo;
                if (source.duration > 0 && next.duration > 0 && Math.abs(source.duration - next.duration) > 10) fail('REMOTE_CHANGED');
                for (const key of ['albumId', 'albumAudioId', 'audioId'])
                  if (usable(source[key]) && usable(next[key]) && source[key] !== next[key]) fail('REMOTE_CHANGED');
                source = { ...source, name: source.name && source.name !== '未知歌曲' ? source.name : next.name,
                  albumId: usable(source.albumId) ? source.albumId : next.albumId,
                  albumAudioId: usable(source.albumAudioId) ? source.albumAudioId : next.albumAudioId };
              }
              checkHints(source);
              if (!source.name || source.name === '未知歌曲' || !usable(source.albumId) || !usable(source.albumAudioId)) fail('NOT_FOUND');
              values.push({ number: 1, name: input(source.name), hash, size: 0, sort: 0, timelen: 0, bitrate: 0,
                album_id: protocolInteger(source.albumId), mixsongid: protocolInteger(source.albumAudioId) });
            }
            break;
          }
          case 'remove': {
            let listId = remote?.locator?.listId;
            if (listId == null) { id = input(id ?? remote?.id); listId = id.slice(id.indexOf(':') + 1); }
            if (!Array.isArray(members)) fail('INVALID_REQUEST');
            path = '/v4/delete_songs'; body = { ...credential, listid: input(listId), list_ver: 0, type: 0,
              data: members.map(row => ({ fileid: protocolInteger(row?.key) })) }; break;
          }
          case 'order': {
            const locator = remote?.locator ?? {};
            let listId = locator.listId;
            if (listId == null) { id = input(id ?? remote?.id); listId = id.slice(id.indexOf(':') + 1); }
            if (!Array.isArray(members)) fail('INVALID_REQUEST');
            raw = await kgCloud(c, '/v1/modify_song_sort', { listid: protocolInteger(listId),
              list_ver: protocolInteger(locator.version), type: 0,
              data: members.map((row, sort) => ({ fileid: protocolInteger(row?.key), sort })) }, callbacks); break;
          }
          default: fail('UNSUPPORTED');
        }
        if (path) raw = await c.kg(path, query, router, body, undefined, undefined, callbacks);
        if (action === 'create') {
          c.invalidateCache();
          return { createdId: '0:' + required(first(raw.data?.listid, raw.listid)), createdName: text(name) };
        }
      }
      c.invalidateCache();
      return {};
    });
  },
  };
  return {
    operations,
    invalidate(platform) {
      for (const [key, entry] of prepared) if (entry.platform === platform) prepared.delete(key);
    },
    close() { prepared.clear(); },
  };
}
