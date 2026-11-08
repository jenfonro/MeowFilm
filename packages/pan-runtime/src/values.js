import { Buffer, isUtf8 } from 'node:buffer';
import { PanError, requireValue } from './errors.js';

export { mergeCookie } from './cookies.js';

export const object = value => value !== null && typeof value === 'object' &&
  !Array.isArray(value) && [Object.prototype, null].includes(Object.getPrototypeOf(value));

export const text = value => typeof value === 'string' ? value :
  typeof value === 'number' && Number.isFinite(value) ? String(value) : '';

function sourceText(value) {
  if (typeof value === 'string') return value;
  requireValue(Buffer.isBuffer(value) && isUtf8(value), 'UPSTREAM_FORMAT');
  return value.toString('utf8');
}

/** Quote unsafe numeric tokens before JSON.parse has a chance to round them. */
export function parseJSON(value) {
  try {
    const source = sourceText(value);
    const parts = [];
    let cursor = 0, index = 0;
    while (index < source.length) {
      const char = source[index];
      if (char === '"') {
        index++;
        while (index < source.length) {
          if (source[index] === '\\') index += 2;
          else if (source[index++] === '"') break;
          else continue;
        }
      } else if (char === '-' || char >= '0' && char <= '9') {
        const match = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(source.slice(index));
        if (!match) { index++; continue; }
        const token = match[0], number = Number(token);
        let next = index + token.length;
        while (/[ \t\r\n]/.test(source[next] ?? '') && next < source.length) next++;
        requireValue(source[next] !== ':', 'UPSTREAM_FORMAT');
        const integer = !/[.eE]/.test(token);
        requireValue(integer || Number.isFinite(number), 'UPSTREAM_FORMAT');
        if ((integer || Number.isInteger(number)) && !Number.isSafeInteger(number)) {
          parts.push(source.slice(cursor, index), JSON.stringify(token));
          cursor = index + token.length;
        }
        index += token.length;
      } else index++;
    }
    return JSON.parse(parts.length ? parts.join('') + source.slice(cursor) : source);
  } catch {
    throw new PanError('UPSTREAM_FORMAT');
  }
}

export function jsonObject(value) {
  const source = sourceText(value).trim();
  const wrapper = /^[A-Za-z_$][A-Za-z0-9_$]*[ \t\r\n]*\(([\s\S]*)\)[ \t\r\n]*;?[ \t\r\n]*$/.exec(source);
  const result = parseJSON(wrapper ? wrapper[1] : source);
  requireValue(object(result), 'UPSTREAM_FORMAT');
  return result;
}
