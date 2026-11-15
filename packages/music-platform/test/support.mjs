import assert from 'node:assert/strict';
import { createCipheriv, createDecipheriv, createHash, createHmac, createPublicKey, diffieHellman, generateKeyPairSync } from 'node:crypto';
import { deflateSync } from 'node:zlib';
import crypto from 'node:crypto';
import { syncBuiltinESMExports } from 'node:module';
import http from 'node:http';
import https from 'node:https';
import { createMusicPlatform } from '../src/index.mjs';
import { createMusicTransport } from '../src/transport.mjs';

export const now = 1_800_000_000_000;
export const huge = '9007199254740993123';
export const kgHash = 'ABCDEF0123456789ABCDEF0123456789';
export const md5 = value => createHash('md5').update(value).digest('hex');
export function encrypt(value, key, mode = 'ecb', iv = null) {
  const cipher = createCipheriv(`aes-${Buffer.byteLength(key) * 8}-${mode}`, Buffer.from(key), iv && Buffer.from(iv));
  return Buffer.concat([cipher.update(value), cipher.final()]);
}
export function decrypt(value, key, mode = 'ecb', iv = null) {
  const cipher = createDecipheriv(`aes-${Buffer.byteLength(key) * 8}-${mode}`, Buffer.from(key), iv && Buffer.from(iv));
  return Buffer.concat([cipher.update(value), cipher.final()]);
}
export function fixedEntropy(t) {
  t.mock.method(crypto, 'randomInt', (min, max) => max === undefined ? 0 : min);
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
}
export function weapi(request) {
  const body = new URLSearchParams(request.body);
  assert.ok(body.get('params'));
  assert.match(body.get('encSecKey'), /^[0-9a-f]{256}$/);
  const inner = decrypt(Buffer.from(body.get('params'), 'base64'), 'a'.repeat(16), 'cbc', '0102030405060708');
  return JSON.parse(decrypt(Buffer.from(inner.toString(), 'base64'), '0CoJUm6Qyw8W8jud', 'cbc', '0102030405060708'));
}
export function response(value, { status = 200, cookies = [], headers = {} } = {}) {
  const fields = new Headers({ 'Content-Type': 'application/json', ...headers });
  for (const cookie of cookies) fields.append('Set-Cookie', cookie);
  return new Response(Buffer.isBuffer(value) || typeof value === 'string' ? value : JSON.stringify(value), { status, headers: fields });
}
export const rpc = data => response({ code: 0, req_0: { code: 0, data } });
export const wySong = (id = huge, extra = {}) => ({ id, name: 'Synthetic (Live)', ar: [{ name: 'A' }, { name: 'B' }],
  al: { id: 'album', name: 'Album', picUrl: 'https://images.invalid/song.jpg?size=480' }, dt: 240000, ...extra });
export const txSong = (id = 'SyntheticMID', extra = {}) => ({ mid: id, id: huge, type: 0, title: 'Synthetic (Live)', singer: [{ name: 'A' }, { name: 'B' }],
  album: { mid: 'SyntheticAlbum', name: 'Album' }, interval: 240, file: { size_128mp3: 1234 }, ...extra });
export const kgSong = (hash = kgHash, extra = {}) => ({ hash, name: 'A - Synthetic (Live)', singerinfo: [{ name: 'A' }, { name: 'B' }],
  albuminfo: { name: 'Album' }, timelen: 240000, ...extra });
export const kwSong = (id = huge, extra = {}) => ({ MUSICRID: 'MUSIC_' + id, SONGNAME: 'Synthetic (Live)', ARTIST: 'A / B',
  ALBUM: 'Album', DURATION: 240, FORMATS: 'MP3128|MP3H', ...extra });
export const mgSong = (id = huge, extra = {}) => ({ songId: id, copyrightId: 'SyntheticCopyright', songName: 'Synthetic (Live)',
  singerList: [{ name: 'A' }, { name: 'B' }], album: 'Album', duration: 240, ...extra });
export const idOf = song => (song.song ?? song).sources[0].musicInfo.songmid;
export function eapi(request) {
  const cipher = Buffer.from(new URLSearchParams(request.body).get('params'), 'hex');
  const [path, json, signature] = decrypt(cipher, 'e82ckenh8dichen8').toString().split('-36cd479b6b5-');
  assert.equal(signature, md5(`nobody${path}use${json}md5forencrypt`));
  return { path, data: JSON.parse(json) };
}
export function qq(request) { return JSON.parse(request.body); }
export function kg(request, { encrypted = false, json = true } = {}) {
  const url = new URL(request.url), fields = Object.fromEntries(url.searchParams);
  if (!encrypted) {
    const signature = fields.signature;
    delete fields.signature;
    assert.equal(signature, md5('OIlwieks28dk2k092lksi2UIkp' +
      Object.keys(fields).sort().map(key => `${key}=${fields[key]}`).join('') +
      (request.body ?? '') + 'OIlwieks28dk2k092lksi2UIkp'));
  }
  return { path: url.pathname, fields, body: request.body === undefined || encrypted || !json ? request.body : JSON.parse(request.body) };
}
export function gate() {
  let enter, release;
  const entered = new Promise(resolve => { enter = resolve; }), waiting = new Promise(resolve => { release = resolve; });
  return { entered, release, async wait() { enter(); await waiting; } };
}
// Test-only loopback peer: fixed production HTTPS recipes still execute the
// default Node transport, without fetching the real platforms or mocking fetch.
export async function nativeHTTP(t, handler) {
  const requests = [];
  let failure, closing;
  const server = http.createServer(async (request, response) => {
    try {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      await handler({ url: request.url, method: request.method, headers: request.headers,
        body: Buffer.concat(chunks) }, response);
    } catch (error) { failure ??= error; response.destroy(); }
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject); server.listen(0, '127.0.0.1', resolve);
  });
  const origin = `http://127.0.0.1:${server.address().port}`, send = http.request;
  t.mock.method(https, 'request', (url, options, callback) => {
    const address = new URL(url);
    const request = send(new URL(address.pathname + address.search, origin), options, callback);
    const entry = { url: address.href, ...options, request, closed: false, socket: null, socketClosed: false };
    requests.push(entry);
    request.once('close', () => { entry.closed = true; });
    request.once('socket', socket => {
      entry.socket = socket;
      socket.once('close', () => { entry.socketClosed = true; });
    });
    return request;
  });
  const close = () => closing ??= (async () => {
    server.closeAllConnections();
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  })();
  t.after(close);
  return { origin, requests, close, check() { if (failure) throw failure; } };
}
export function fixture(handler, { tokenProvider, files = new Map(), documentIO } = {}) {
  let time = now, failure = null;
  const reads = [], writes = [], requests = [];
  const documents = {
    read(key) { reads.push(key); return documentIO ? documentIO.read(key) : files.get(key); },
    write(key, data) {
      writes.push(key); if (failure) throw failure;
      if (documentIO) documentIO.write(key, Buffer.from(data)); else files.set(key, Buffer.from(data));
    },
  };
  const transport = createMusicTransport({ fetch: async (url, options) => {
    const request = { url: String(url), ...options }; requests.push(request);
    return handler(request, requests.length);
  } });
  const owner = createMusicPlatform({ documents, transport, clock: () => time, tokenProvider });
  return { owner, files, reads, writes, requests, advance(ms) { time += ms; }, failWrites(error = Error('synthetic write')) { failure = error; },
    request(platform, operation, params = {}, options) { return owner.request({ platform, operation, params }, options); },
    session(platform, userId = '42', cookie, scope = 'test:' + userId) {
      const cookies = cookie ?? ({ wy: 'MUSIC_U=synthetic-music-u; __csrf=synthetic-csrf',
        tx: `musicid=${userId}; musickey=synthetic-key; encrypt_uin=synthetic-uin`,
        kg: `userid=${userId}; token=synthetic-token` })[platform];
      return owner.sessionUpdate({ platform, session: { scope, generation: 1, userId, cookie: cookies } });
    },
    close() { return owner.close(); } };
}
export function seedClients(f) {
  f.files.set('clients.json', Buffer.from(JSON.stringify({
    wy: { cookies: { deviceId: 'SYNTHETIC_DEVICE', _ntes_nuid: 'synthetic-nuid', _ntes_nnid: 'synthetic-nuid,1800000000000',
      WNMCID: 'abcdef.1800000000000.01.0', __remember_me: 'true' } },
    kg: { seed: '0123456789abcdef0123456789abcdef', mid: '176905054734845483393488264506404844848', dfid: 'synthetic-dfid' },
    tx: { installationId: '11111111-1111-4111-8111-111111111111', androidId: 'synthetic-android', openUdid: 'synthetic-open',
      openUdid2: 'synthetic-open-2', imei: '123456789012345', qimei: { q16: 'synthetic-q16', q36: 'synthetic-q36', savedAt: now },
      session: { uid: 'synthetic-uid', sid: 'synthetic-sid', savedAt: now } },
  })));
}
export function krc(value) {
  const key = Buffer.from([64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105]), bytes = deflateSync(Buffer.from(value));
  for (let index = 0; index < bytes.length; index++) bytes[index] ^= key[index % key.length];
  return Buffer.concat([Buffer.from('krc1'), bytes]).toString('base64');
}
export function kuwo(value) {
  const key = Buffer.from('yeelion'), bytes = Buffer.from(value);
  for (let index = 0; index < bytes.length; index++) bytes[index] ^= key[index % key.length];
  return Buffer.concat([Buffer.from('tp=content\r\n\r\n'), deflateSync(Buffer.from(bytes.toString('base64')))]);
}
// An independent synthetic X25519 server opens actual XEAPI requests. No
// production helper, private platform key or deterministic product hook is used.
export function xeapi() {
  const pair = generateKeyPairSync('x25519'), staticKey = Buffer.from('ab1d5a430f6bb04a3f01e81ddd72bd916d5ce591248ac128714806d7f8fb1b84', 'hex');
  const signKey = 'mUHCwVNWJbunMqAHf5MImuirT6plvs6VSFW62MGHstFQxhBGdEoIhLItH3djc4+FB/OKty3+lL2rGeoFBpVe5g==';
  const hmac = (key, value) => createHmac('sha256', key).update(value).digest();
  return {
    key(request, { timestamp = String(now), signature } = {}) {
      const params = new URLSearchParams(request.body), nonce = params.get('nonce');
      assert.equal(params.get('signature'), hmac(signKey, params.get('timestamp') + nonce).toString('base64'));
      const data = { publicKey: pair.publicKey.export({ type: 'spki', format: 'der' }).subarray(-32).toString('base64'),
        version: 'synthetic-version', sk: 'synthetic-server-key' };
      return response({ code: 200, data: { timestamp, signature: signature ?? hmac(signKey, String(timestamp) + nonce).toString('base64'),
        encryptedData: encrypt(JSON.stringify(data), staticKey).toString('base64') } });
    },
    open(request, { sessionKey, sessionID = '' } = {}) {
      const fields = new URLSearchParams(request.body), sealed = Buffer.from(fields.get('S'), 'base64');
      const ephemeral = sealed.subarray(0, 32), peer = createPublicKey({ format: 'der', type: 'spki',
        key: Buffer.concat([Buffer.from('302a300506032b656e032100', 'hex'), ephemeral]) });
      const shared = diffieHellman({ privateKey: pair.privateKey, publicKey: peer });
      const expanded = hmac(hmac(Buffer.alloc(32), shared), Buffer.concat([ephemeral, Buffer.from([1])]));
      const decipher = createDecipheriv('aes-128-gcm', expanded.subarray(0, 16), sealed.subarray(32, 44));
      decipher.setAuthTag(sealed.subarray(-16));
      const plain = Buffer.concat([decipher.update(sealed.subarray(44, -16)), decipher.final()]).toString();
      const [dynamic, os, sk] = plain.split('|'); assert.equal(os, 'android'); assert.equal(sk, 'synthetic-server-key');
      if (sessionKey) assert.deepEqual(Buffer.from(dynamic, 'base64'), Buffer.from(sessionKey));
      assert.equal(decrypt(Buffer.from(fields.get('R'), 'base64'), staticKey).toString(), 'synthetic-version|' + sessionID);
      const encoded = decrypt(Buffer.from(fields.get('C'), 'base64url'), Buffer.from(dynamic, 'base64')), mask = encoded.subarray(0, 16);
      const rotated = encoded.subarray(16), n = (mask[0] & 15) % rotated.length;
      const inner = Buffer.concat([rotated.subarray(rotated.length - n), rotated.subarray(0, rotated.length - n)]);
      for (let index = 0; index < inner.length; index++) inner[index] ^= mask[index & 15];
      const result = JSON.parse(decrypt(inner, staticKey)); assert.equal(result.queryString, 'e_r=true');
      return new URLSearchParams(result.content ?? '');
    },
    reply(value, options) { return response(encrypt(JSON.stringify(value), 'e82ckenh8dichen8'), options); },
  };
}
