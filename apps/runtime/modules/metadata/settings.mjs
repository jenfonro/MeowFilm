import { PrivateDocuments } from '@meowfilm/script-host';
import { isUtf8 } from 'node:buffer';
import { createHash } from 'node:crypto';
import { parseJSONText, stringifyJSONValue, cloneJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';

export const metadataFailure = code => Object.assign(new Error(code), { code });
export const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const defaults = {
  tmdb: { apiBase: 'https://api.themoviedb.org/3', imageBase: 'https://image.tmdb.org',
    apiKey: '', accessToken: '', language: 'zh-CN', region: 'CN', includeAdult: false },
  douban: { apiBase: 'https://m.douban.com', searchBase: 'https://search.douban.com',
    imageBase: 'https://img3.doubanio.com', apiProxy: '', imageProxy: '', cookie: '' },
};
/** Native assembly supplies a read capability; standalone assembly owns its
 * document. PrivateDocuments is atomic storage, not encryption. */
export function createMetadataSettings(dataRoot, id, nativeSettings) {
  const documents = nativeSettings ? null : new PrivateDocuments(dataRoot, id);
  let snapshot, document, reload = false;
  const revision = value => createHash('sha256').update(stringifyJSONValue([id,
    Object.keys(defaults[id]).map(key => [key, value[key]])])).digest('hex');
  const same = (left, right) => stringifyJSONValue(left) === stringifyJSONValue(right);
  function load() {
    try {
      if (nativeSettings) document = nativeSettings.read(id);
      else {
        const bytes = documents.read('settings.json');
        if (bytes === null) document = null;
        else {
          if (!isUtf8(bytes)) throw metadataFailure('CORRUPT_STORAGE');
          document = parseJSONText(new TextDecoder('utf-8').decode(bytes));
        }
      }
      if (document === null) return { ...defaults[id] };
      if (!object(document) || document.version !== 1 || !object(document.settings) || isRawJSON(document.settings))
        throw metadataFailure('CORRUPT_STORAGE');
      return { ...defaults[id], ...document.settings };
    } catch (error) {
      throw metadataFailure(error?.code === 'STORAGE_ERROR' || error?.code === 'PERMISSION_DENIED'
        ? 'STORAGE_ERROR' : 'CORRUPT_STORAGE');
    }
  }
  function read() {
    if (snapshot !== undefined && !reload) return snapshot;
    const value = load();
    snapshot = value;
    reload = false;
    return snapshot;
  }
  function visible(value) {
    return { ...value, revision: revision(value) };
  }
  return {
    read,
    invalidate() { reload = true; },
    get: () => visible(read()),
    update(patch) {
      if (nativeSettings) throw metadataFailure('PERMISSION_DENIED');
      if (!object(patch)) {
        throw metadataFailure('INVALID_REQUEST');
      }
      const current = read();
      const value = cloneJSONValue({ ...current, ...Object.fromEntries(Object.keys(defaults[id])
        .filter(key => Object.hasOwn(patch, key)).map(key => [key,
          id === 'tmdb' && ['apiBase', 'imageBase'].includes(key) && patch[key] === ''
            ? defaults[id][key] : patch[key]])) });
      if (same(current, value)) return visible(current);
      try {
        documents.write('settings.json', Buffer.from(stringifyJSONValue({ ...document, version: 1, settings: value }) + '\n'));
      } catch {
        // rename may have committed before directory synchronization failed.
        // Reload the authoritative file next time instead of claiming rollback.
        reload = true;
        throw metadataFailure('STORAGE_ERROR');
      }
      snapshot = value;
      return visible(value);
    },
  };
}
