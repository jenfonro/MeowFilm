export const normalizeString = (value) => (typeof value === 'string' ? value.trim() : '');

export const normalizeInt = (value) => {
  const num = Number(value);
  return Number.isFinite(num) ? Math.trunc(num) : 0;
};
