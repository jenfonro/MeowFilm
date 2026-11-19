import { openDatabase } from '@meowfilm/storage';
import { createHash } from 'node:crypto';
import { stringifyJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';

export const metadataFailure = code => Object.assign(new Error(code), { code });
export const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const defaults = {
  tmdb: { apiBase: 'https://api.themoviedb.org/3', imageBase: 'https://image.tmdb.org',
    apiKey: '', accessToken: '', language: 'zh-CN', region: 'CN', includeAdult: false },
  douban: { apiBase: 'https://m.douban.com', searchBase: 'https://search.douban.com',
    imageBase: 'https://img3.doubanio.com', apiProxy: '', imageProxy: '', cookie: '' },
};
const columns = {
  tmdb: ['api_base', 'image_base', 'api_key', 'access_token', 'language', 'region', 'include_adult'],
  douban: ['api_base', 'search_base', 'image_base', 'api_proxy', 'image_proxy', 'cookie'],
};

/** Native settings are a projection, not a second writable fact source. */
export function createMetadataSettings(dataRoot, id, nativeSettings) {
  if (!Object.hasOwn(defaults, id)) throw metadataFailure('INVALID_REQUEST');
  const database = nativeSettings ? null : openDatabase(dataRoot), keys = Object.keys(defaults[id]);
  const table = `metadata_standalone_${id}`;
  let snapshot, rowRevision = 0, dirty = true, tail = Promise.resolve();
  const revision = value => createHash('sha256').update(stringifyJSONValue([id,
    keys.map(key => [key, value[key]])])).digest('hex');
  const visible = value => ({...value, revision:revision(value)});
  async function read() {
    if (!dirty && snapshot) return snapshot;
    try {
      if (nativeSettings) {
        const document = nativeSettings.read(id);
        if (document !== null && (!object(document) || document.version !== 1 ||
            !object(document.settings) || isRawJSON(document.settings))) throw metadataFailure('CORRUPT_STORAGE');
        snapshot = {...defaults[id], ...document?.settings};
      } else {
        const [row] = await database.query(`SELECT * FROM ${table} WHERE singleton=1`);
        rowRevision = row?.row_revision ?? 0;
        snapshot = row ? Object.fromEntries(keys.map((key,index) => [key,
          key === 'includeAdult' ? Boolean(row[columns[id][index]]) : row[columns[id][index]]])) : {...defaults[id]};
      }
      dirty = false;
      return snapshot;
    } catch (error) {
      throw metadataFailure(error?.code === 'STORAGE_CORRUPT' || error?.code === 'CORRUPT_STORAGE'
        ? 'CORRUPT_STORAGE' : 'STORAGE_ERROR');
    }
  }
  const ready = read(); ready.catch(() => {});
  let disposal;
  return {
    ready, read,
    invalidate() { dirty = true; },
    async get() { return visible(await read()); },
    update(patch) {
      const work = tail.then(async () => {
        if (nativeSettings) throw metadataFailure('PERMISSION_DENIED');
        if (!object(patch) || isRawJSON(patch) || Object.keys(patch).some(key => !keys.includes(key)))
          throw metadataFailure('INVALID_REQUEST');
        const current = await read(), value = {...current};
        for (const key of keys) if (Object.hasOwn(patch,key)) {
          if (key === 'includeAdult' ? typeof patch[key] !== 'boolean' : typeof patch[key] !== 'string')
            throw metadataFailure('INVALID_REQUEST');
          value[key] = id === 'tmdb' && ['apiBase','imageBase'].includes(key) && patch[key] === ''
            ? defaults[id][key] : patch[key];
        }
        if (keys.every(key => current[key] === value[key])) return visible(current);
        const values = keys.map(key => key === 'includeAdult' ? Number(value[key]) : value[key]);
        try {
          if (rowRevision) await database.execute(`UPDATE ${table} SET ${columns[id].map(key => key+'=?').join(',')},` +
            'row_revision=row_revision+1 WHERE singleton=1 AND row_revision=?', [...values,rowRevision], {expectChanges:1});
          else await database.execute(`INSERT INTO ${table}(singleton,${columns[id].join(',')},row_revision) VALUES(1,` +
            values.map(() => '?').join(',') + ',1)', values, {expectChanges:1});
        } catch (error) {
          dirty = true;
          throw metadataFailure(error?.code === 'STORAGE_CONFLICT' ? 'STORAGE_CONFLICT' : 'STORAGE_ERROR');
        }
        ++rowRevision; snapshot = value;
        return visible(value);
      });
      tail = work.catch(() => {});
      return work;
    },
    close() { return disposal ??= (async () => { await Promise.allSettled([ready,tail]); await database?.close(); })(); },
  };
}
