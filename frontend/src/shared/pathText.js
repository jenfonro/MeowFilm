import { normalizeString } from './normalize';

export const splitRawPathSegments = (value) =>
  normalizeString(value)
    .replace(/\\/g, '/')
    .split('/')
    .map(normalizeString)
    .filter(Boolean);

export const getRawFileName = (value) => {
  const parts = splitRawPathSegments(value);
  return parts.length ? parts[parts.length - 1] : normalizeString(value);
};

export const getRawDirPath = (value) => {
  const parts = splitRawPathSegments(value);
  if (parts.length <= 1) return '';
  return `/${parts.slice(0, -1).join('/')}`;
};
