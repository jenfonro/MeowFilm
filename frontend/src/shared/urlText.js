import { normalizeString } from './normalize';

export const normalizeHttpBase = (value) => {
  const raw = normalizeString(value);
  if (!raw) return '';
  try {
    const url = new URL(raw);
    if (!/^https?:$/i.test(url.protocol)) return '';
    url.hash = '';
    url.search = '';
    return url.toString().replace(/\/+$/, '');
  } catch (_error) {
    return '';
  }
};
