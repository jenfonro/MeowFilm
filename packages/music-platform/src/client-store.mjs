import { openDatabase } from '@meowfilm/storage';
import { upsert } from '@meowfilm/storage/statements';
import { createHash } from 'node:crypto';

const OWNER = 'music.anonymous';
const kgColumns = ['seed', 'mid', 'dfid'];
const wyCookies = ['deviceId', '_ntes_nuid', '_ntes_nnid', 'WNMCID', '__remember_me', 'NMTID', 'MUSIC_A'];
const txColumns = ['installation_id', 'android_id', 'open_udid', 'open_udid2', 'imei',
  'qimei_q36', 'qimei_saved_at_ms', 'session_uid', 'session_sid', 'session_saved_at_ms'];
const txValues = value => [value.installationId, value.androidId, value.openUdid, value.openUdid2, value.imei,
  value.qimei?.q36 ?? null, value.qimei?.savedAt ?? null, value.session?.uid ?? null,
  value.session?.sid ?? null, value.session?.savedAt ?? null];
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const text = value => typeof value === 'string' && value.length > 0 && !/[\0\r\n]/.test(value);
const timestamp = value => Number.isSafeInteger(value) && value >= 0;

export function anonymousClient(platform, value, code) {
  const require = valid => { if (!valid) throw Object.assign(new Error(code), { code }); };
  if (platform === 'wy') {
    require(object(value) && object(value.cookies));
    const cookies = Object.fromEntries(wyCookies.filter(key => Object.hasOwn(value.cookies, key))
      .map(key => [key, value.cookies[key]]));
    require(['deviceId', '_ntes_nuid', '_ntes_nnid', 'WNMCID', '__remember_me'].every(key => text(cookies[key])) &&
      Object.values(cookies).every(text));
    return { cookies };
  } else if (platform === 'kg') {
    require(object(value) && /^[0-9a-f]{32}$/.test(value.seed) && text(value.mid) &&
      (value.dfid === null || text(value.dfid)));
    require(value.mid === BigInt('0x' + createHash('md5').update(value.seed).digest('hex')).toString());
    return Object.fromEntries(kgColumns.map(key => [key, value[key]]));
  } else {
    require(platform === 'tx' && object(value));
    require(['installationId', 'androidId', 'openUdid', 'openUdid2', 'imei'].every(key => text(value[key])) &&
      /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value.installationId));
    require(value.qimei === null || object(value.qimei) && text(value.qimei.q36) && timestamp(value.qimei.savedAt));
    require(value.session === null || object(value.session) &&
      text(value.session.uid) && text(value.session.sid) && timestamp(value.session.savedAt));
    return {
      ...Object.fromEntries(['installationId', 'androidId', 'openUdid', 'openUdid2', 'imei'].map(key => [key, value[key]])),
      qimei: value.qimei === null ? null : { q36: value.qimei.q36, savedAt: value.qimei.savedAt },
      session: value.session === null ? null : { uid: value.session.uid, sid: value.session.sid, savedAt: value.session.savedAt },
    };
  }
}

/** Only the runtime-owned anonymous protocol identity lives in these tables (CAS via owner_revisions). */
export function createAnonymousClientStore(dataRoot) {
  const database = openDatabase(dataRoot);
  let revision = 0;

  async function read() {
    const [[state], cookies, [kg], [tx]] = await database.snapshot([
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [OWNER], rows: true },
      { sql: 'SELECT cookie_name,cookie_value FROM music_anonymous_wy_cookies', rows: true },
      { sql: 'SELECT seed,mid,dfid FROM music_anonymous_kg WHERE singleton=1', rows: true },
      { sql: `SELECT ${txColumns.join(',')} FROM music_anonymous_tx WHERE singleton=1`, rows: true },
    ]);
    revision = state?.revision ?? 0;
    if (!state) {
      if (cookies.length || kg || tx) throw Object.assign(new Error('CORRUPT_STORAGE'), { code: 'CORRUPT_STORAGE' });
      return null;
    }
    return {
      wy: cookies.length ? {
        cookies: Object.fromEntries(cookies.filter(row => wyCookies.includes(row.cookie_name))
          .map(row => [row.cookie_name, row.cookie_value])),
      } : null,
      kg: kg ? Object.fromEntries(kgColumns.map(key => [key, kg[key]])) : null,
      tx: tx ? {
        installationId: tx.installation_id, androidId: tx.android_id, openUdid: tx.open_udid,
        openUdid2: tx.open_udid2, imei: tx.imei,
        qimei: tx.qimei_q36 === null ? null : { q36: tx.qimei_q36, savedAt: tx.qimei_saved_at_ms },
        session: tx.session_uid === null ? null : { uid: tx.session_uid, sid: tx.session_sid, savedAt: tx.session_saved_at_ms },
      } : null,
    };
  }

  function statements(platform, value) {
    value = anonymousClient(platform, value, 'INVALID_REQUEST');
    if (platform === 'wy') {
      const names = Object.keys(value.cookies);
      return [
        { sql: `DELETE FROM music_anonymous_wy_cookies WHERE cookie_name NOT IN (${names.map(() => '?').join(',')})`, params: names },
        ...Object.entries(value.cookies).map(([key, item]) => upsert('music_anonymous_wy_cookies',
          ['cookie_name', 'cookie_value'], [key, item], ['cookie_name'])),
      ];
    }
    if (platform === 'kg') {
      return [upsert('music_anonymous_kg', ['singleton', ...kgColumns],
        [1, ...kgColumns.map(key => value[key] ?? null)], ['singleton'])];
    }
    return [upsert('music_anonymous_tx', ['singleton', ...txColumns], [1, ...txValues(value)], ['singleton'])];
  }

  async function commit(changes) {
    const gate = revision
      ? { sql: 'UPDATE owner_revisions SET revision=revision+1 WHERE owner=? AND revision=?', params: [OWNER, revision], expectChanges: 1 }
      : { sql: 'INSERT INTO owner_revisions(owner,revision) VALUES(?,1)', params: [OWNER], expectChanges: 1 };
    await database.transaction([gate, ...changes]);
    ++revision;
  }

  return {
    read,
    initialize: value => {
      if (!object(value)) throw Object.assign(new Error('INVALID_REQUEST'), { code: 'INVALID_REQUEST' });
      return commit(['wy', 'kg', 'tx'].flatMap(platform => statements(platform, value[platform])));
    },
    writePlatform: (platform, value) => commit(statements(platform, value)),
    close: () => database.close(),
  };
}
