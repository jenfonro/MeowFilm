import { normalizeNFKCNative } from './native-engine.js';

const hasICU = Boolean(process.versions.icu);

/** NFKC also on NodeMobile builds whose String.normalize is an ICU-less stub. */
export function normalizeNFKC(value) {
  if (typeof value !== 'string') {
    throw Object.assign(new TypeError('Expected a string'), { code: 'INVALID_ARGUMENT' });
  }
  return hasICU ? value.normalize('NFKC') : normalizeNFKCNative(value);
}
