import { Buffer, isUtf8 } from 'node:buffer';
import { isRawJSON, parseJSONText } from '@meowfilm/runtime-bridge/json';
import { PanError, requireValue } from './errors.js';

export const object = value => value !== null && typeof value === 'object' &&
  !Array.isArray(value) && [Object.prototype, null].includes(Object.getPrototypeOf(value));

export const text = value => typeof value === 'string' ? value :
  typeof value === 'number' && Number.isFinite(value) ? String(value) : '';

export function decodeUTF8(value) {
  if (typeof value === 'string') return value;
  requireValue(Buffer.isBuffer(value) && isUtf8(value), 'UPSTREAM_FORMAT');
  return value.toString('utf8');
}

function projectJSON(value) {
  if (isRawJSON(value)) {
    const token = value.rawJSON;
    const numeric = Number(token);
    const integer = !/[.eE]/.test(token);
    requireValue(integer || Number.isFinite(numeric), 'UPSTREAM_FORMAT');
    return integer || (Number.isInteger(numeric) && !Number.isSafeInteger(numeric)) ? token : numeric;
  }
  if (typeof value === 'number' && Number.isInteger(value) && !Number.isSafeInteger(value)) {
    return String(value);
  }
  if (Array.isArray(value)) return value.map(projectJSON);
  if (value !== null && typeof value === 'object') {
    const result = {};
    for (const [key, child] of Object.entries(value)) {
      Object.defineProperty(result, key, {
        value: projectJSON(child), enumerable: true, configurable: true, writable: true,
      });
    }
    return result;
  }
  return value;
}

/** Decode JSON via runtime-bridge while preserving unsafe integer tokens as exact strings. */
export function parseJSON(value) {
  try {
    return projectJSON(parseJSONText(decodeUTF8(value)));
  } catch {
    throw new PanError('UPSTREAM_FORMAT');
  }
}

export function jsonObject(value) {
  const source = decodeUTF8(value).trim();
  const wrapper = /^[A-Za-z_$][A-Za-z0-9_$]*[ \t\r\n]*\(([\s\S]*)\)[ \t\r\n]*;?[ \t\r\n]*$/.exec(source);
  const result = parseJSON(wrapper ? wrapper[1] : source);
  requireValue(object(result), 'UPSTREAM_FORMAT');
  return result;
}
