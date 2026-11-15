// SPDX-License-Identifier: GPL-3.0-only
// api-enhanced register_xeapikey and XEAPI recipe, pinned in LICENSES/NOTICE.md.
import { createHmac, createCipheriv, createPublicKey, diffieHellman, generateKeyPairSync, randomBytes, randomInt, timingSafeEqual } from 'node:crypto';
import { aes, cookieFields, cookieHeader, decrypt, encryptedWyResponse, form } from './protocols.mjs';
import { decodeJSON, object, required } from './model.mjs';
import { fail } from './errors.mjs';

const staticKey = Buffer.from('ab1d5a430f6bb04a3f01e81ddd72bd916d5ce591248ac128714806d7f8fb1b84', 'hex');
const signKey = 'mUHCwVNWJbunMqAHf5MImuirT6plvs6VSFW62MGHstFQxhBGdEoIhLItH3djc4+FB/OKty3+lL2rGeoFBpVe5g==';
const agent = 'NeteaseMusic/9.5.61.260802021928(9005061);Dalvik/2.1.0 (Linux; U; Android 12; HBN-AL00 Build/cd737a2.0)';
const hmac = (key, value) => createHmac('sha256', key).update(value).digest();
async function handshake(device, { transport, clock, signal }) {
  const nonce = Array.from({ length: 16 }, () => randomInt(10)).join('');
  const timestamp = String(clock());
  const keyReply = (await transport({ url: 'https://interface.music.163.com/api/bsr/sk/get', method: 'POST', signal,
    headers: { 'User-Agent': agent, Cookie: 'deviceId=' + device, 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form({ appVersion: '9.5.61', currentKeyVersion: '', deviceId: device, nonce, os: 'android', requestType: 'active',
      signature: hmac(signKey, timestamp + nonce).toString('base64'), t1: '', t2: '', timestamp, uid: '' }) })).value;
  if (keyReply.code != null && required(keyReply.code) !== '200') fail('UPSTREAM_ERROR');
  const data = object(keyReply.data);
  const serverTime = required(data.timestamp);
  // This is the authenticated millisecond input, not an ordinary platform ID.
  if (!/^[1-9]\d*$/.test(serverTime) || !Number.isSafeInteger(Number(serverTime))) fail('INVALID_RESPONSE');
  const signature = Buffer.from(required(data.signature), 'base64');
  const expected = hmac(signKey, serverTime + nonce);
  if (signature.length !== expected.length || !timingSafeEqual(signature, expected)) fail('INVALID_RESPONSE');
  let state;
  try { state = decodeJSON(decrypt(Buffer.from(required(data.encryptedData), 'base64'), staticKey)); }
  catch { fail('INVALID_RESPONSE'); }
  const peerBytes = Buffer.from(required(state.publicKey), 'base64');
  if (peerBytes.length !== 32) fail('INVALID_RESPONSE');
  return { publicKey: peerBytes, sk: required(state.sk), version: required(state.version) };
}
export async function xeapiRequest(path, params, context) {
  const { transport, client, account, clock, signal, dispatched, keyState } = context;
  const cookies = { ...client.cookies, ...cookieFields(account?.cookie) };
  const device = required(cookies.deviceId);
  let state = keyState.current;
  if (!state) {
    state = await handshake(device, context);
    signal.throwIfAborted();
    keyState.current = state;
  }
  const peer = createPublicKey({ key: Buffer.concat([Buffer.from('302a300506032b656e032100', 'hex'), state.publicKey]), format: 'der', type: 'spki' });
  const local = generateKeyPairSync('x25519');
  const ephemeral = local.publicKey.export({ format: 'der', type: 'spki' }).subarray(-32);
  const shared = diffieHellman({ privateKey: local.privateKey, publicKey: peer });
  const expanded = hmac(hmac(Buffer.alloc(32), shared), Buffer.concat([ephemeral, Buffer.from([1])]));
  const dynamicKey = state.sessionKey ?? randomBytes(16), iv = randomBytes(12);
  const cipher = createCipheriv('aes-128-gcm', expanded.subarray(0, 16), iv);
  const sealed = Buffer.concat([ephemeral, iv, cipher.update(`${dynamicKey.toString('base64')}|android|${required(state.sk)}`),
    cipher.final(), cipher.getAuthTag()]);
  const fields = { queryString: 'e_r=true' };
  const content = form(params);
  if (content) fields.content = content;
  const inner = aes(JSON.stringify(fields), staticKey), mask = randomBytes(16);
  for (let i = 0; i < inner.length; i++) inner[i] ^= mask[i & 15];
  const rotate = (mask[0] & 15) % inner.length;
  const encoded = aes(Buffer.concat([mask, inner.subarray(rotate), inner.subarray(0, rotate)]), dynamicKey);
  Object.assign(cookies, { os: 'android', osver: cookies.os === 'android' ? cookies.osver ?? '16' : '16',
    appver: params.level === 'vivid' ? '9.5.61' : cookies.os === 'android' ? cookies.appver ?? '9.1.65' : '9.1.65',
    buildver: cookies.buildver ?? String(Math.floor(clock() / 1000)), sDeviceId: cookies.sDeviceId ?? device });
  signal.throwIfAborted();
  const response = await transport({ url: 'https://interface3.music.163.com/xeapi/' + path.slice(5), method: 'POST', signal,
    dispatched,
    headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'User-Agent': agent, 'X-Client-Enc-State': 'ENCRYPTED',
      'x-aeapi': 'true', 'x-deviceid': device, 'x-os': 'android', 'x-osver': cookies.osver, 'x-appver': cookies.appver,
      'x-sdeviceid': cookies.sDeviceId, 'x-buildver': cookies.buildver, 'x-music-u': cookies.MUSIC_U ?? '',
      Cookie: cookieHeader(cookies) },
    body: form({ C: encoded.toString('base64url'), S: sealed.toString('base64'),
      R: aes(state.version + '|' + (state.sessionID ?? ''), staticKey).toString('base64') }),
    json: false });
  const result = encryptedWyResponse(response.value);
  const sessionID = response.headers.get('x-encr-ssid'), sessionKey = response.headers.get('x-encr-sskey');
  if (sessionID !== null && sessionKey !== null) {
    const bytes = Buffer.from(sessionKey);
    if (!sessionID || Buffer.byteLength(sessionID) > 512 || ![16, 24, 32].includes(bytes.length)) fail('INVALID_RESPONSE');
    signal.throwIfAborted();
    Object.assign(state, { sessionID, sessionKey: bytes });
  }
  return result;
}
