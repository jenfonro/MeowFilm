import { openDatabase } from '@meowfilm/storage';
import { upsert } from '@meowfilm/storage/statements';
import { createHash } from 'node:crypto';

const kgColumns = ['seed','mid','dfid'];
const txColumns = ['installation_id','android_id','open_udid','open_udid2','imei',
  'qimei_q36','qimei_saved_at_ms','session_uid','session_sid','session_saved_at_ms'];
const txValues = value => [value.installationId,value.androidId,value.openUdid,value.openUdid2,value.imei,
  value.qimei?.q36 ?? null,value.qimei?.savedAt ?? null,value.session?.uid ?? null,
  value.session?.sid ?? null,value.session?.savedAt ?? null];
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const text = value => typeof value === 'string' && value.length > 0 && !/[\0\r\n]/.test(value);
const timestamp = value => Number.isSafeInteger(value) && value >= 0;
function validate(platform, value, code) {
  const require = valid => { if (!valid) throw Object.assign(new Error(code), { code }); };
  const keys = (item, names) => object(item) && Object.keys(item).every(key => names.includes(key));
  if (platform === 'wy') {
    require(keys(value, ['cookies']) && keys(value.cookies,
      ['deviceId','_ntes_nuid','_ntes_nnid','WNMCID','__remember_me','NMTID','MUSIC_A']));
    require(['deviceId','_ntes_nuid','_ntes_nnid','WNMCID','__remember_me'].every(key => text(value.cookies[key])) &&
      Object.values(value.cookies).every(text));
  } else if (platform === 'kg') {
    require(keys(value, kgColumns) && /^[0-9a-f]{32}$/.test(value.seed) && text(value.mid) &&
      (value.dfid === null || text(value.dfid)));
    require(value.mid === BigInt('0x' + createHash('md5').update(value.seed).digest('hex')).toString());
  } else {
    require(platform === 'tx' && keys(value, ['installationId','androidId','openUdid','openUdid2','imei','qimei','session']));
    require(['installationId','androidId','openUdid','openUdid2','imei'].every(key => text(value[key])) &&
      /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(value.installationId));
    require(value.qimei === null || keys(value.qimei, ['q36','savedAt']) && text(value.qimei.q36) && timestamp(value.qimei.savedAt));
    require(value.session === null || keys(value.session, ['uid','sid','savedAt']) &&
      text(value.session.uid) && text(value.session.sid) && timestamp(value.session.savedAt));
  }
}

/** Only the runtime-owned anonymous protocol identity lives in these tables. */
export function createAnonymousClientStore(dataRoot) {
  const database = openDatabase(dataRoot);
  let revision = 0;
  async function read() {
    const [[state], cookies, [kg], [tx]] = await database.snapshot([
      {sql:'SELECT row_revision FROM music_anonymous_state WHERE singleton=1',rows:true},
      {sql:'SELECT cookie_name,cookie_value FROM music_anonymous_wy_cookies',rows:true},
      {sql:'SELECT * FROM music_anonymous_kg WHERE singleton=1',rows:true},
      {sql:'SELECT * FROM music_anonymous_tx WHERE singleton=1',rows:true},
    ]);
    revision = state?.row_revision ?? 0;
    if (!state) {
      if (cookies.length || kg || tx) throw Object.assign(new Error('CORRUPT_STORAGE'), { code:'CORRUPT_STORAGE' });
      return null;
    }
    if (!kg || !tx) throw Object.assign(new Error('CORRUPT_STORAGE'),{code:'CORRUPT_STORAGE'});
    const value = {wy:{cookies:Object.fromEntries(cookies.map(row => [row.cookie_name,row.cookie_value]))},
      kg:Object.fromEntries(kgColumns.map(key => [key,kg[key]])), tx:{
        installationId:tx.installation_id,androidId:tx.android_id,openUdid:tx.open_udid,openUdid2:tx.open_udid2,imei:tx.imei,
        qimei:tx.qimei_q36 === null ? null : {q36:tx.qimei_q36,savedAt:tx.qimei_saved_at_ms},
        session:tx.session_uid === null ? null : {uid:tx.session_uid,sid:tx.session_sid,savedAt:tx.session_saved_at_ms},
      }};
    for (const [platform, item] of Object.entries(value)) validate(platform, item, 'CORRUPT_STORAGE');
    return value;
  }
  function statements(platform,value) {
    validate(platform, value, 'INVALID_REQUEST');
    if (platform === 'wy') return [
      {sql:'DELETE FROM music_anonymous_wy_cookies WHERE cookie_name NOT IN (' +
        Object.keys(value.cookies).map(() => '?').join(',') + ')',params:Object.keys(value.cookies)},
      ...Object.entries(value.cookies).map(([key,item]) => upsert('music_anonymous_wy_cookies',
        ['cookie_name','cookie_value'],[key,item],['cookie_name'])),
    ];
    if (platform === 'kg') return [upsert('music_anonymous_kg',['singleton',...kgColumns],
      [1,...kgColumns.map(key => value[key] ?? null)],['singleton'])];
    return [upsert('music_anonymous_tx',['singleton',...txColumns],[1,...txValues(value)],['singleton'])];
  }
  async function commit(changes) {
    const gate = revision ? {sql:'UPDATE music_anonymous_state SET row_revision=row_revision+1 WHERE singleton=1 AND row_revision=?',
      params:[revision],expectChanges:1} : {sql:'INSERT INTO music_anonymous_state VALUES(1,1)',expectChanges:1};
    await database.transaction([gate,...changes]); ++revision;
  }
  return {read,
    initialize: value => {
      if (!object(value) || Object.keys(value).sort().join(',') !== 'kg,tx,wy')
        throw Object.assign(new Error('INVALID_REQUEST'), { code:'INVALID_REQUEST' });
      return commit(Object.entries(value).flatMap(([platform,item]) => statements(platform,item)));
    },
    writePlatform: (platform,value) => commit(statements(platform,value)),
    close: () => database.close(),
  };
}
