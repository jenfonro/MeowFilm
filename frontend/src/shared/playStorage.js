import { normalizeString } from './normalize';

export const readPlayLocalStorage = (key) => {
  try {
    if (typeof window === 'undefined' || !window.localStorage || !key) return '';
    return normalizeString(window.localStorage.getItem(key));
  } catch (_error) {
    return '';
  }
};

export const writePlayLocalStorage = (key, value) => {
  try {
    if (typeof window === 'undefined' || !window.localStorage || !key) return;
    const nextValue = normalizeString(value);
    if (!nextValue) {
      window.localStorage.removeItem(key);
      return;
    }
    window.localStorage.setItem(key, nextValue);
  } catch (_error) {
    // ignore storage errors
  }
};
