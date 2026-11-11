import { PrivateDocuments } from '@meowfilm/script-host';
import { isUtf8 } from 'node:buffer';
import { createHash } from 'node:crypto';

export const metadataFailure = code => Object.assign(new Error(code), { code });
export const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const defaults = {
  tmdb: { apiBase: 'https://api.themoviedb.org', imageBase: 'https://image.tmdb.org/t/p',
    apiKey: '', accessToken: '' },
  douban: { apiBase: 'https://m.douban.com', searchBase: 'https://search.douban.com',
    imageBase: 'https://img3.doubanio.com', apiProxy: '', imageProxy: '', cookie: '' },
};
const secrets = { tmdb: ['apiKey', 'accessToken'], douban: ['cookie'] };

function endpoint(value, template = false) {
  if (template && value === '') return value;
  if (typeof value !== 'string' || !/^https?:\/\//i.test(value) || value !== value.trim() ||
      /[\u0000-\u0020\u007f\\#]/.test(value) || !template && value.includes('?')) {
    throw metadataFailure('INVALID_REQUEST');
  }
  const slots = value.match(/\{(?:url|encodedUrl)\}/g) ?? [];
  if (template ? slots.length !== 1 : slots.length !== 0) throw metadataFailure('INVALID_REQUEST');
  const substituted = template ? value.replace(slots[0], 'https%3A%2F%2Fexample.invalid%2F') : value;
  let url;
  try { url = new URL(substituted); } catch { throw metadataFailure('INVALID_REQUEST'); }
  if (!['http:', 'https:'].includes(url.protocol) || url.hash ||
      (!template && url.search) || /[{}]/.test(substituted) ||
      template && !value.slice(value.indexOf('://') + 3).match(/^[^/?#{}]+[/?]/)) {
    throw metadataFailure('INVALID_REQUEST');
  }
  return template ? value.replace(/^https?:\/\//i, scheme => scheme.toLowerCase()) : url.href.replace(/\/+$/, '');
}

function validate(id, value) {
  if (!object(value) || Object.keys(value).length !== Object.keys(defaults[id]).length ||
      Object.keys(value).some(key => !Object.hasOwn(defaults[id], key))) {
    throw metadataFailure('INVALID_REQUEST');
  }
  const result = {};
  for (const [key, item] of Object.entries(value)) {
    if (secrets[id].includes(key)) {
      if (typeof item !== 'string' || /[\u0000-\u001f\u007f]/.test(item)) throw metadataFailure('INVALID_REQUEST');
      result[key] = item.trim();
    } else result[key] = endpoint(item, key.endsWith('Proxy'));
  }
  return result;
}

/** The runtime owns this document. The native host supplies a private data root,
 * not a second settings database. PrivateDocuments is atomic, not encryption. */
export function createMetadataSettings(dataRoot, id) {
  const documents = new PrivateDocuments(dataRoot, id);
  let snapshot, reload = false;
  const revision = value => createHash('sha256').update(JSON.stringify([id,
    Object.keys(defaults[id]).map(key => [key, value[key]])])).digest('hex');
  const same = (left, right) => Object.keys(defaults[id]).every(key => left[key] === right[key]);
  function load() {
    try {
      const bytes = documents.read('settings.json');
      if (bytes === null) return { ...defaults[id] };
      // NodeMobile omits ICU, which fatal TextDecoder requires. Validate bytes
      // independently, retaining strict UTF-8 and the decoder's BOM handling.
      if (!isUtf8(bytes)) throw metadataFailure('CORRUPT_STORAGE');
      const document = JSON.parse(new TextDecoder('utf-8').decode(bytes));
      if (!object(document) || document.version !== 1 ||
          Object.keys(document).length !== 2 || !Object.hasOwn(document, 'settings')) {
        throw metadataFailure('CORRUPT_STORAGE');
      }
      return validate(id, document.settings);
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
    const result = { ...value, revision: revision(value) };
    for (const key of secrets[id]) {
      delete result[key];
      result[`${key}Configured`] = value[key].length > 0;
    }
    return result;
  }
  return {
    read,
    get: () => visible(read()),
    update(patch) {
      if (!object(patch) || Object.keys(patch).some(key => !Object.hasOwn(defaults[id], key))) {
        throw metadataFailure('INVALID_REQUEST');
      }
      const current = read();
      const value = validate(id, { ...current, ...patch });
      if (same(current, value)) return visible(current);
      try {
        documents.write('settings.json', Buffer.from(JSON.stringify({ version: 1, settings: value }) + '\n'));
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
