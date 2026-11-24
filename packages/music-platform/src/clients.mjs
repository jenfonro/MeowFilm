// SPDX-License-Identifier: GPL-3.0-only
// Anonymous client recipes from the pinned QQMusicApi / KuGouMusicApi sources.
import { constants, publicEncrypt, randomInt, randomUUID } from 'node:crypto';
import { anonymousClient, createAnonymousClientStore } from './client-store.mjs';
import { aes, decrypt, hex, kgPublicKey, kgRequest, kgSuccess, md5, responseCookies, txRequest, txSuccess } from './protocols.mjs';
import { decodeJSON, object, required } from './model.mjs';
import { fail, MusicPlatformError } from './errors.mjs';
import { waitForFlight } from './flight.mjs';

const qimeiPublicKey = `-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDEIxgwoutfwoJxcGQeedgP7FG9qaIuS0qzfR8gWkrkTZKM2iWHn2ajQpBRZjMSoSf6+KJGvar2ORhBfpDXyVtZCKpqLQ+FLkpncClKVIrBwv6PHyUvuCb0rIarmgDnzkfQAqVufEtR64iazGDKatvJ9y6B9NMbHddGSAUmRTCrHQIDAQAB
-----END PUBLIC KEY-----`;
const requiredWyCookies = ['deviceId', '_ntes_nuid', '_ntes_nnid', 'WNMCID', '__remember_me'];

function fresh(clock) {
  const now = clock(), nuid = hex(32), seed = hex(16), digits = Array.from({ length: 14 }, () => randomInt(10));
  const sum = digits.reduce((total, digit, index) => total + (index % 2 ? digit * 2 > 9 ? digit * 2 - 9 : digit * 2 : digit), 0);
  return {
    wy: { cookies: { deviceId: hex(26).toUpperCase(), _ntes_nuid: nuid, _ntes_nnid: `${nuid},${now}`,
      WNMCID: `${Array.from({ length: 6 }, () => String.fromCharCode(97 + randomInt(26))).join('')}.${now}.01.0`,
      __remember_me: 'true' } },
    kg: { seed, mid: BigInt('0x' + md5(seed)).toString(), dfid: null },
    tx: { installationId: randomUUID(), androidId: hex(8), openUdid: hex(16), openUdid2: hex(16),
      imei: digits.join('') + (10 - sum % 10) % 10, qimei: null, session: null },
  };
}
function qimeiRequest(device, clock) {
  const now = clock(), timestamp = String(Math.floor(now / 1000)), key = hex(8), nonce = hex(8);
  const common = new Date(now).toISOString().slice(0, 7) + '-01' + randomInt(100000, 1000000) + '.' + randomInt(100000000, 1000000000);
  const commonKeys = new Set([1, 2, 13, 14, 17, 18, 21, 22, 25, 26, 29, 30, 33, 34, 37, 38]);
  const beacon = Array.from({ length: 40 }, (_, index) => {
    const n = index + 1, value = commonKeys.has(n) ? common : n === 3 ? '0000000000000000' : n === 4 ? hex(8) : String(randomInt(10000));
    return `k${n}:${value};`;
  }).join('');
  const token = value => aes(value, 'lvcwmSYVr2Axv1gn', 'cbc', 'Zs0ntDqG2jyhKN0c').toString('base64');
  const digest = Buffer.from(md5(device.androidId), 'hex');
  const reserved = { harmony: '0', clone: '0', containe: '', oz: token(device.androidId), oo: token('V2408A'),
    kelong: '0', ip: `192.168.${digest[0]}.${digest[1] % 253 + 2}`,
    uptimes: new Date(now - randomInt(14401) * 1000).toISOString().slice(0, 19).replace('T', ' '),
    multiUser: '0', bod: 'sun', brd: 'vivo', dv: 'PD2408', firstLevel: '35', manufact: 'vivo', name: 'PD2408',
    host: 'comdg01150014', kernel: 'Linux localhost 6.6.89-android15-8-g1f71897ac249-abogki467805059-4k #1 SMP PREEMPT Thu Dec 11 01:56:00 UTC 2025 aarch64',
    pre: '0', av: '20.9.0.8', ch: '' };
  const payload = { androidId: device.androidId, platformId: 1, appKey: '0AND0HD6FE4HY80F', appVersion: '20.9.0.8',
    beaconIdSrc: beacon, brand: 'vivo', channelId: '10003505', cid: '', imei: device.imei, imsi: '', mac: '',
    model: 'V2408A', networkType: 'wifi', oaid: '', osVersion: 'Android 15,level 35', qimei: '', qimei36: '',
    sdkVersion: '1.2.13.6', targetSdkVersion: '30', audit: '', userId: '{}', packageId: 'com.tencent.qqmusic',
    deviceType: 'Phone', sdkName: '', reserved: JSON.stringify(reserved) };
  const secured = publicEncrypt({ key: qimeiPublicKey, padding: constants.RSA_PKCS1_PADDING }, Buffer.from(key)).toString('base64');
  const params = aes(JSON.stringify(payload), key, 'cbc', key).toString('base64');
  const extra = '{"appKey":"0AND0HD6FE4HY80F"}';
  return { url: 'https://api.tencentmusic.com/tme/trpc/proxy', method: 'POST',
    headers: { method: 'GetQimei', service: 'trpc.tme_datasvr.qimeiproxy.QimeiProxy', appid: 'qimei_qq_android',
      sign: md5('qimei_qq_androidpzAuCmaFAaFaHrdakPjLIEqKrGnSOOvH' + timestamp),
      'User-Agent': 'QQMusic', timestamp, 'Content-Type': 'application/json' },
    body: JSON.stringify({ app: 0, os: 1, qimeiParams: { key: secured, params, time: timestamp, nonce,
      sign: md5(secured + params + Number(timestamp) * 1000 + nonce + 'ZdJqM15EeO2zWc08' + extra), extra } }) };
}
function kgRegistration(seed) {
  const data = { availableRamSize: 4983533568, availableRomSize: 48114719, availableSDSize: 48114717,
    basebandVer: '', batteryLevel: 100, batteryStatus: 3, brand: 'Redmi', buildSerial: 'unknown',
    device: 'marble', imei: seed, imsi: '', manufacturer: 'Xiaomi', uuid: seed };
  for (const sensor of ['accelerometer', 'gravity', 'gyroscope', 'light', 'magnetic', 'orientation', 'pressure', 'step_counter', 'temperature']) {
    data[sensor] = false; data[sensor + 'Value'] = '';
  }
  return data;
}
const chinaDay = time => Math.floor((time + 8 * 3600000) / 86400000);
/** Credentials are native-owned; only anonymous protocol state is durable here. */
export function createClientContexts({ dataRoot, transport, clock }) {
  const store = createAnonymousClientStore(dataRoot);
  let state, loading, uncertain = false, writes = Promise.resolve();
  const selected = new Set();
  const flights = new Map(), lifetime = new AbortController();
  function ready() {
    if (state && !uncertain) return Promise.resolve();
    if (!loading) loading = Promise.resolve().then(async () => {
      lifetime.signal.throwIfAborted();
      let current;
      try { current = await store.read(); }
      catch (error) {
        fail(['CORRUPT_STORAGE', 'STORAGE_CORRUPT', 'STORAGE_SCHEMA'].includes(error?.code)
          ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR');
      }
      if (!current) {
        if (state) fail('STORAGE_ERROR');
        current = fresh(clock);
        try { await store.initialize(current); }
        catch (error) { uncertain = true; fail(commitCode(error)); }
      }
      state = current; selected.clear(); uncertain = false;
    }).finally(() => { loading = undefined; });
    loading.catch(() => {});
    return loading;
  }
  function commitCode(error) {
    if (error?.code === 'STORAGE_CONFLICT') return 'STORAGE_CONFLICT';
    if (['CORRUPT_STORAGE', 'STORAGE_CORRUPT', 'STORAGE_SCHEMA'].includes(error?.code)) return 'CORRUPT_STORAGE';
    return ['STORAGE_BUSY', 'STORAGE_FULL', 'STORAGE_READONLY', 'STORAGE_RESULT_LIMIT', 'INVALID_REQUEST'].includes(error?.code)
      ? 'STORAGE_ERROR' : 'COMMIT_UNKNOWN';
  }
  function load() {
    lifetime.signal.throwIfAborted();
    if (!state || uncertain) fail('STORAGE_ERROR');
    return state;
  }
  function get(platform) {
    const current = load()[platform];
    if (platform === 'kw' || platform === 'mg') return current;
    if (!selected.has(platform)) {
      try { state = { ...state, [platform]: anonymousClient(platform, current, 'CORRUPT_STORAGE') }; }
      catch { fail('CORRUPT_STORAGE'); }
      selected.add(platform);
    }
    return state[platform];
  }
  function save(platform, proposed) {
    const work = writes.then(async () => {
      await ready(); lifetime.signal.throwIfAborted();
      const current = get(platform), next = typeof proposed === 'function' ? proposed(current) : proposed;
      if (next === undefined) return current;
      let value;
      try { value = anonymousClient(platform, next, 'INVALID_REQUEST'); }
      catch (error) { fail(error?.code === 'INVALID_REQUEST' ? 'INVALID_REQUEST' : 'STORAGE_ERROR'); }
      try { await store.writePlatform(platform, value); }
      catch (error) { uncertain = true; fail(commitCode(error)); }
      state = { ...state, [platform]: value };
      return value;
    });
    writes = work.catch(() => {});
    return work;
  }
  async function prepare(platform, signal) {
    let client = get(platform);
    if (platform !== 'tx') return client;
    const now = clock();
    if (!client.qimei || client.qimei.savedAt > now || now - client.qimei.savedAt >= 86400000) {
      const reply = (await transport({ ...qimeiRequest(client, clock), signal })).value;
      if (reply.code != null && required(reply.code) !== '0') fail('UPSTREAM_ERROR');
      const inner = decodeJSON(Buffer.from(required(reply.data)));
      if (inner.code != null && required(inner.code) !== '0') fail('UPSTREAM_ERROR');
      const data = object(inner.data);
      signal.throwIfAborted();
      client = await save('tx', { ...client, qimei: { q36: required(data.q36), savedAt: clock() },
        session: null });
    }
    const sessionCheckNow = clock();
    if (!client.session || client.session.savedAt > sessionCheckNow || chinaDay(client.session.savedAt) !== chinaDay(sessionCheckNow)) {
      const request = txRequest('music.getSession.session', 'GetSession', {
        uid: client.session?.uid ?? '', vkey: 0, caller: client.session ? 1 : 2,
      }, { ...client, session: null }, null, clock);
      const data = txSuccess((await transport({ ...request, signal })).value);
      const session = object(data.session);
      signal.throwIfAborted();
      client = await save('tx', { ...client, session: { uid: required(session.uid), sid: required(session.sid), savedAt: clock() } });
    }
    return client;
  }
  function shared(key, callerSignal, work) {
    if (callerSignal?.aborted) return Promise.reject(callerSignal.reason);
    let flight = flights.get(key);
    if (!flight || flight.controller.signal.aborted) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(new MusicPlatformError('TIMEOUT')), 25000);
      const flightSignal = AbortSignal.any([lifetime.signal, controller.signal]);
      const waiters = new Set();
      flight = { controller, waiters };
      flights.set(key, flight);
      flight.promise = Promise.resolve().then(() => { flightSignal.throwIfAborted(); return work(flightSignal); })
        .finally(() => { clearTimeout(timer); if (flights.get(key) === flight) flights.delete(key); });
      flight.promise.catch(() => {});
    }
    return waitForFlight(flight, callerSignal);
  }
  return {
    get ready() { return ready(); },
    get,
    prepare(platform, signal) { return shared(platform, signal, flightSignal => prepare(platform, flightSignal)); },
    registerKg(device, signal) { return shared(device ? 'kg-candidate:' + device.seed : 'kg-registration', signal, async flightSignal => {
      const client = device ?? get('kg');
      if (client.dfid) return client;
      const secret = Array.from({ length: 6 }, () => '0123456789abcdefghijklmnopqrstuvwxyz'[randomInt(36)]).join('');
      const digest = md5(secret), key = digest.slice(0, 16), iv = digest.slice(16);
      const encrypted = aes(JSON.stringify(kgRegistration(client.seed)), key, 'cbc', iv).toString('base64');
      const portrait = publicEncrypt({ key: kgPublicKey, padding: constants.RSA_PKCS1_PADDING },
        Buffer.from(JSON.stringify({ aes: secret, uid: 0, token: '' }))).toString('hex').toUpperCase();
      const request = kgRequest('/risk/v2/r_register_dev', { part: '1', platid: '1', p: portrait },
        client, null, clock, null, encrypted, 'https://userservice.kugou.com');
      request.headers['Content-Type'] = 'application/x-www-form-urlencoded';
      const response = await transport({ ...request, json: false, signal: flightSignal });
      if (response.headers.get('ssa-code')) fail('VERIFICATION_REQUIRED');
      let plain;
      try { plain = decrypt(response.value, key, 'cbc', iv); }
      catch {
        const rejection = decodeJSON(response.value);
        kgSuccess(rejection);
        fail('INVALID_RESPONSE'); // Plaintext success cannot establish the encrypted device.
      }
      const value = kgSuccess(decodeJSON(plain));
      flightSignal.throwIfAborted();
      if (Array.isArray(value.data) && value.data.length === 0) return client;
      const next = { ...client, dfid: required(value.data?.dfid) };
      return device ? next : save('kg', next);
    }); },
    acceptKgAccount(before, device) {
      return save('kg', current => current === before && current.seed === device.seed &&
        current.mid === device.mid && current.dfid !== device.dfid ? { ...current, dfid: device.dfid } : undefined);
    },
    acceptWyAccount(before, used, next) {
      return save('wy', current => {
        if (current !== before || current.cookies.deviceId !== used.deviceId || next.deviceId !== used.deviceId) return;
        const cookies = { ...current.cookies };
        for (const key of ['deviceId', '_ntes_nuid', '_ntes_nnid', 'WNMCID', 'NMTID']) {
          if (next[key] == null) {
            if (!requiredWyCookies.includes(key)) delete cookies[key];
          } else cookies[key] = next[key];
        }
        if (JSON.stringify(cookies) !== JSON.stringify(current.cookies)) return { ...current, cookies };
      });
    },
    acceptWy(headers, account, host) {
      if (account) return;
      return save('wy', client => {
        const cookies = responseCookies(headers,
          ['deviceId', '_ntes_nuid', '_ntes_nnid', 'WNMCID', 'NMTID', 'MUSIC_A'], client.cookies, clock, host);
        if (cookies.deviceId && cookies.deviceId !== client.cookies.deviceId) fail('VERIFICATION_REQUIRED');
        for (const key of requiredWyCookies) {
          if (!cookies[key]) cookies[key] = client.cookies[key];
        }
        if (JSON.stringify(cookies) !== JSON.stringify(client.cookies)) return { ...client, cookies };
      });
    },
    async close() {
      lifetime.abort(new MusicPlatformError('CLOSED'));
      await Promise.allSettled([loading, writes, ...[...flights.values()].map(flight => flight.promise)]);
      await store.close();
    },
  };
}
