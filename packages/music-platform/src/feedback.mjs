// SPDX-License-Identifier: GPL-3.0-only
// Fixed QQMusicApi, meowmusic and NetEase official playback-log recipes.
import { constants, publicEncrypt, randomUUID } from 'node:crypto';
import { deflateSync, gzipSync, inflateSync } from 'node:zlib';
import { aes, cookieFields, cookieHeader, decrypt, form, hex, md5, txRequest } from './protocols.mjs';
import { decodeJSON, encodeProtocolJSON, first, input, number, object, protocolInteger, required, rows, scalar, text } from './model.mjs';
import { fail } from './errors.mjs';
import { playbackLog } from './playback-log.mjs';
import { settleWrite } from './sync.mjs';

const csccSalt = 'LnT6xpN3khm36zse0QzvmgTZ3waWdRSA';
const csccKey = `-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDECi0Np2UR87scwrvTr72L6oO01rBbbBPriSDFPxr3Z5syug0O24QyQO8bg27+0+4kBzTBTBOZ/WWU0WryL1JSXRTXLgFVxtzIY41Pe7lPOgsfTCn5kZcvKhYKJesKnnJDNr5/abvTGf+rHG3YRwsCHcQ08/q6ifSioBszvb3QiwIDAQAB
-----END PUBLIC KEY-----`;
const xml = value => scalar(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&apos;');
const targetID = p => input(first(p.target?.nativeId, p.target?.nativeID, p.recording?.id, p.id));
function kgFacts(c, p, state) {
  const duration = number(p.duration, 0, 'INVALID_REQUEST');
  if (duration <= 0 || state && Math.abs(duration - state.duration) > 10) fail('INVALID_REQUEST');
  if (p.event === 'start') return { duration };
  const listened = number(p.listened, 0, 'INVALID_REQUEST'), position = number(p.position, 0, 'INVALID_REQUEST'),
    rate = number(p.rate, 1, 'INVALID_REQUEST');
  if (listened < 0 || position < 0 || position > duration + 1 || rate <= 0 ||
      p.reason === 'playend' && Math.abs(position - duration) > 1 ||
      state && listened > Math.max(0, (c.clock() - state.startedAt) / 1000) + 2) fail('INVALID_REQUEST');
  return { duration, listened, position, rate };
}
function records(p) {
  const target = p.target ?? {}, start = p.event === 'start', at = Math.floor(number(p.occurredAt, 0, 'INVALID_REQUEST') * 1000);
  const fields = { id: protocolInteger(targetID(p)), type: 'song', _logTime: at,
    time: start ? 0 : Math.floor(number(first(p.mediaListened, p.media), 0, 'INVALID_REQUEST')),
    realtime: start ? 0 : Math.floor(number(p.listened, 0, 'INVALID_REQUEST')) };
  if (text(target.source)) fields.source = target.source;
  if (text(target.sourceId)) fields.sourceId = target.sourceId;
  if (text(target.algorithm)) fields.alg = target.algorithm;
  if (target.origin?.kind === 'heartbeat') fields.play_scene = 'heartbeat';
  if (!start) { fields.end = input(p.reason); fields.play_speed = number(p.rate, 1, 'INVALID_REQUEST'); }
  const json = encodeProtocolJSON(fields);
  return Buffer.from(`${Math.floor(at / 1000)}\x01${start ? '_plv' : '_pld'}\x01${json}\n`);
}
async function cscc(c, path, data, body, callbacks) {
  const response = await c.response({ url: 'http://d.kugou.com' + path + '?' + form(data),
    method: body ? 'POST' : 'GET', body, headers: {
      'Cache-Control': 'no-store', ...(body ? { 'Content-Type': 'application/octet-stream' } : {}),
      ...(path === '/v3/qrydid' ? {} : { 'KG-Rec': '1', 'User-Agent':
        'Android9-1070-10597-18-0-Cscc' + (path === '/v2/post' ? 'Post' : 'Gen') + '-wifi' }) } }, callbacks);
  const raw = response.value;
  if (response.status !== 200 || !Number.isInteger(raw.status)) fail('INVALID_RESPONSE');
  const codes = ['errcode', 'error_code'].filter(key => raw[key] != null).map(key => {
    const value = required(raw[key]);
    if (!Number.isInteger(Number(value))) fail('INVALID_RESPONSE');
    return Number(value);
  });
  if (raw.status !== 1 || codes.some(code => code !== 0)) {
    callbacks?.rejected?.(); fail('UPSTREAM_ERROR');
  }
  return raw;
}
async function prepareKG(c, p, duration) {
  const startedAt = c.clock();
  const client = c.client(), fields = cookieFields(c.accountRequired().cookie), uuid = fields.KUGOU_API_GUID ?? client.seed,
    mid = fields.KUGOU_API_MID ?? client.mid, model = fields.KUGOU_API_DEV ?? uuid.slice(0, 10).toUpperCase();
  const row = await c.kgMixRow(targetID(p).toUpperCase(), scalar(first(p.target?.mixsongId, p.target?.mixsongID, p.mixsongId)));
  if (Math.abs(number(row.audio_info?.timelength) / 1000 - duration) > 10) fail('REMOTE_CHANGED');
  const mix = required(row.base?.album_audio_id);
  const time = String(Math.floor(c.clock() / 1000)), body = Buffer.from(JSON.stringify({ machine: model, mid, uuid, wh: [1920, 1080] }));
  const firstReply = await cscc(c, '/v3/qrydid', { appid: 'and02', _t: time,
    sign: md5('_t' + time + 'appidand02pbKC7zn{4U*ydo2M1Rir' + body) }, body);
  const did = required(firstReply.data?.deviceid), field = hex(16);
  const portrait = publicEncrypt({ key: csccKey, padding: constants.RSA_PKCS1_PADDING },
    Buffer.from(`${uuid}\t${field}\t${c.clock()}\t${did}`)).toString('base64');
  const next = String(Math.floor(c.clock() / 1000));
  const second = await cscc(c, '/v2/gen', { s: portrait, appid: 3116, _t: next,
    sign: md5('_t' + next + 'appid3116s' + portrait + csccSalt) });
  const data = decodeJSON(decrypt(Buffer.from(required(second.data), 'base64'), field, 'cbc', uuid.slice(0, 16)));
  return { mix, uuid, mid, model, field, duration, startedAt, cookie: required(data.cookie),
    clienttime: required(data.clienttime), serverstr: required(data.serverstr) };
}
async function prepareTX(c, p) {
  const id = targetID(p);
  await c.verifyTxFeedback();
  await c.prepareTx();
  const data = await c.tx('music.trackInfo.UniformRuleCtrl', 'CgiGetTrackInfo', {
    ctx: 0, client: 1, types: [0], modify_stamp: [0], mids: [id] }, true);
  const song = rows(data.tracks)[0];
  if (!song) fail('NOT_FOUND');
  const filename = 'M500' + id + id + '.mp3';
  const value = await c.tx('music.vkey.GetVkey', 'UrlGetVkey', { uin: c.accountRequired().userId, filename: [filename],
    guid: randomUUID().replaceAll('-', ''), songmid: [id], songtype: [0], ctx: 0 }, true);
  const row = rows(value.midurlinfo)[0];
  if (!row || scalar(row.result) !== '0') fail('MEDIA_UNAVAILABLE');
  const duration = number(song.interval);
  if (duration <= 0 || duration > 86400) fail('INVALID_RESPONSE');
  return { id, songId: required(song.id), duration, singerId: scalar(song.singer?.[0]?.id) || '0',
    vkey: required(row.vkey) };
}
async function sendKG(c, p, state, callbacks, facts) {
  const end = p.event === 'end', now = Math.floor(c.clock() / 1000);
  const duration = Math.round(facts.duration * 1000);
  const listened = end ? Math.floor(facts.listened * 1000) : 0;
  const fields = [`type_id=${end ? '4' : '20431'}`, 'action=play', 'mixsongid=' + state.mix,
    (end ? 'st=' : 'spt=') + duration];
  if (end) fields.push('spt=' + Math.round(facts.position * 1000));
  fields.push('sty=' + (p.target?.automatic ? '自动' : '手动'));
  if (end) fields.push('duration=' + listened, 'state=' + (p.reason === 'exception' ? '播放错误' :
    p.reason === 'playend' && listened > 0 ? '完整播放' : '被终止'), 'type=1', 'fs=' + facts.rate.toFixed(1));
  fields.push('mid=' + state.mid, 'uuid=' + state.uuid, 'sys=9', 'mod=' + state.model, 'channelid=18', 'ip=0.0.0.0',
    'net=1', 'ver=10597', 'time=' + c.clock(), 'userid=' + c.account.userId, 'ss3=' + md5(hex(16)));
  const event = Buffer.from(fields.join('&'));
  const head = ['4', '1', state.uuid, '0', '3116', '10597', '18', '9', state.model, state.uuid, state.mid, '0',
    '000000000000000000000000000000000000'].join('\t') + '\r\n' +
    [String(event.length), '10048', '0', String(now), '1', c.account.userId, '0', '0'].join('\t') + '\r\n';
  const plain = Buffer.concat([Buffer.from(head), deflateSync(event)]);
  const key = md5(state.uuid + state.clienttime + state.field + state.serverstr);
  const encrypted = aes(deflateSync(plain), key, 'cbc', state.uuid.slice(0, 16));
  const query = { cookie: state.cookie, length: String(plain.length), appid: '3116', _t: String(now) };
  query.sign = md5(Buffer.concat([Buffer.from(Object.keys(query).sort().map(key => key + query[key]).join('') + csccSalt), encrypted]));
  await cscc(c, '/v2/post', query, encrypted, callbacks);
}
export function createFeedbackOperations() {
  const sessions = new Map();
  function register(c, state) {
    if (!c.isCurrent()) return null;
    const sessionId = randomUUID();
    sessions.set(sessionId, { platform: c.platform, scope: c.account.scope ?? c.account.cookie,
      generation: c.account.generation ?? 0, state });
    return sessionId;
  }
  function take(c, sessionId) {
    const entry = sessions.get(input(sessionId));
    if (!entry || entry.platform !== c.platform || entry.scope !== (c.account?.scope ?? c.account?.cookie) ||
      entry.generation !== (c.account?.generation ?? 0)) fail('STALE_SESSION');
    return entry;
  }
  async function upload(c, bytes) {
    const cookies = cookieFields(c.accountRequired().cookie), filename = 'play_' + randomUUID(), boundary = 'meowfilm-' + randomUUID();
    const file = playbackLog(Buffer.from(JSON.stringify({ MUSIC_U: required(cookies.MUSIC_U, 'INVALID_CREDENTIALS') })), bytes);
    const body = Buffer.concat([Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="${filename}"\r\nContent-Type: multipart/form-data\r\n\r\n`),
      file, Buffer.from(`\r\n--${boundary}--\r\n`)]);
    return settleWrite(c, async callbacks => {
      let response;
      try {
        response = await c.response({ url: 'https://clientlog3.music.163.com/api/clientlog/encrypt/upload?multiupload=true',
          method: 'POST', headers: { 'Content-Type': 'multipart/form-data; boundary=' + boundary,
            'Cache-Control': 'no-store', Cookie: cookieHeader(cookies), 'User-Agent': 'Meowfilm' }, body }, callbacks);
      } catch (error) {
        if (error.beforeSend) callbacks.notSent();
        throw error;
      }
      const raw = response.value;
      if (response.status !== 200 || !Number.isInteger(raw.code)) fail('INVALID_RESPONSE');
      if (raw.code !== 200) { callbacks.rejected(); fail('UPSTREAM_ERROR'); }
      if (!rows(raw.data?.successfiles).includes(filename)) fail('INVALID_RESPONSE');
      return {};
    });
  }
  const operations = {
    async 'feedback.qualify'(c) {
      if (c.platform !== 'wy') fail('UNSUPPORTED');
      const account = c.accountRequired(), raw = await c.wy('/api/w/nuser/account/get', {}, true);
      if (raw.profile === null) fail('AUTH_EXPIRED');
      if (required(raw.profile?.userId) !== account.userId) fail('INVALID_CREDENTIALS');
      return { qualified: true };
    },
    async 'feedback.upload'(c, { events }) {
      if (c.platform !== 'wy') fail('UNSUPPORTED');
      if (!Array.isArray(events)) fail('INVALID_REQUEST');
      return upload(c, Buffer.concat(events.map(event => records(object(event, 'INVALID_REQUEST')))));
    },
    async 'feedback.prepare'(c, p) {
      c.accountRequired();
      if (c.platform === 'wy') return { status: 'registered', sessionId: null };
      if (c.platform !== 'tx') fail('UNSUPPORTED');
      const state = await prepareTX(c, p);
      c.signal.throwIfAborted();
      return { status: 'registered', sessionId: register(c, state) };
    },
    async 'feedback.send'(c, p) {
      c.accountRequired();
      if (c.platform === 'wy') return upload(c, records(p));
      if (p.event === 'start') {
        if (c.platform === 'tx') return operations['feedback.prepare'](c, p);
        if (c.platform !== 'kg') fail('UNSUPPORTED');
        const facts = kgFacts(c, p), state = await prepareKG(c, p, facts.duration);
        const receipt = await settleWrite(c, async callbacks => { await sendKG(c, p, state, callbacks, facts); return {}; });
        return { ...receipt, sessionId: receipt.status === 'confirmed' ? register(c, state) : null };
      }
      if (p.event !== 'end') fail('UNSUPPORTED');
      const entry = take(c, p.sessionId);
      const facts = c.platform === 'tx' ? { listened: number(p.listened, 0, 'INVALID_REQUEST') } : kgFacts(c, p, entry.state);
      if (c.platform === 'tx' && facts.listened < 1) fail('INVALID_REQUEST');
      sessions.delete(p.sessionId);
      return settleWrite(c, async callbacks => {
        const state = entry.state, now = Math.floor(c.clock() / 1000);
        if (c.platform === 'tx') {
          const listened = Math.floor(facts.listened);
          await c.verifyTxFeedback();
          const client = await c.prepareTx(), account = c.account, cookies = cookieFields(account.cookie);
          const comm = JSON.parse(txRequest('', '', {}, client, account, c.clock).body).comm;
          const attributes = { cmd: '1', optime: String(now), QQ: account.userId, uid: client.session.uid,
            songtype: '1', playtype: '5', time: String(listened), play_duration_mi: String(listened * 1000),
            audiotime: String(state.duration * 1000), songid: state.songId, singerid: state.singerId, vkey: state.vkey,
            timekey: md5(`${now}${listened}${account.userId}gk2$Lh-&l4#!4iow`).toUpperCase() };
          const document = '<?xml version="1.0" encoding="UTF-8"?>\r\n<root>\r\n' +
            Object.keys(comm).sort().map(key => `    <${key}>${xml(comm[key])}</${key}>\r\n`).join('') +
            '    <cid>228</cid>\r\n    <item ' + Object.keys(attributes).sort().map(key => `${key}="${xml(attributes[key])}"`).join(' ') + '/>\r\n</root>';
          const bytes = Buffer.from(document);
          const response = await c.response({ url: 'https://stat6.y.qq.com/android/fcgi-bin/imusic_tj',
            method: 'POST', json: false, body: gzipSync(bytes), headers: {
              'Content-Encoding': 'gzip', 'Content-Type': 'application/octet-stream', 'User-Agent': 'QQMusic 20090008(android 15)',
              Referer: 'https://y.qq.com/', Cookie: cookieHeader({ uin: account.userId, qqmusic_uin: account.userId,
                qm_keyst: cookies.musickey, qqmusic_key: cookies.musickey }) } }, callbacks);
          if (response.status !== 200 || !response.value.subarray(0, 5).equals(Buffer.alloc(5))) fail('INVALID_RESPONSE');
          let raw;
          try {
            const stream = response.value.subarray(5);
            const inflated = inflateSync(stream, { info: true });
            if (inflated.engine.bytesWritten !== stream.length) fail('INVALID_RESPONSE');
            raw = decodeJSON(inflated.buffer);
          }
          catch (error) { fail(error.code === 'ERR_BUFFER_TOO_LARGE' ? 'LIMIT' : 'INVALID_RESPONSE'); }
          if (!Number.isInteger(raw.code)) fail('INVALID_RESPONSE');
          if (raw.code !== 0) { callbacks.rejected(); fail('UPSTREAM_ERROR'); }
        } else await sendKG(c, p, state, callbacks, facts);
        return {};
      });
    },
    'feedback.release'(c, { sessionId }) { if (sessionId) { take(c, sessionId); sessions.delete(sessionId); } return { released: true }; },
  };
  return { operations, invalidate(platform) { for (const [id, entry] of sessions) if (entry.platform === platform) sessions.delete(id); },
    close() { sessions.clear(); } };
}
