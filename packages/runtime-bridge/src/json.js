import { Buffer, isUtf8 } from 'node:buffer';
import { BridgeError } from './errors.js';

function invalid() { throw new BridgeError('INVALID_REQUEST'); }
function checkString(value) {
  // JSON strings and keys contain Unicode scalars, never isolated surrogates.
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) invalid();
    } else if (unit >= 0xdc00 && unit <= 0xdfff) invalid();
  }
}
function checkWire(text) {
  // Check keys before JSON.parse can discard duplicates; the parser remains
  // responsible for the complete JSON grammar. Limit nesting before parsing.
  const stack = [];
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (char === '{' || char === '[') {
      stack.push(char === '{' ? new Set() : null);
      if (stack.length > 66) invalid();
    } else if (char === '}' || char === ']') {
      if (!stack.length) invalid();
      stack.pop();
    } else if (char === '"') {
      const start = i++;
      for (; i < text.length; i++) {
        if (text[i] === '\\') i++;
        else if (text[i] === '"') break;
      }
      if (i >= text.length) invalid();
      let next = i + 1;
      while (next < text.length && /[\x20\t\r\n]/.test(text[next])) next++;
      if (text[next] === ':') {
        const key = JSON.parse(text.slice(start, i + 1));
        checkString(key);
        const keys = stack.at(-1);
        if (!keys || keys.has(key)) invalid();
        keys.add(key);
      }
    }
  }
}
export function checkJSON(value, depth = 0, ancestors = new Set()) {
  if (depth > 64) invalid();
  if (typeof value === 'string') { checkString(value); return; }
  if (value === null || typeof value === 'boolean') return;
  if (typeof value === 'number') {
    if (!Number.isFinite(value) || (Number.isInteger(value) && !Number.isSafeInteger(value))) invalid();
    return;
  }
  if (typeof value !== 'object' || ancestors.has(value)) invalid();
  const prototype = Object.getPrototypeOf(value);
  if (!Array.isArray(value) && prototype !== Object.prototype && prototype !== null) invalid();
  if (Object.getOwnPropertySymbols(value).length) invalid();
  ancestors.add(value);
  const descriptors = Object.getOwnPropertyDescriptors(value);
  for (const [key, descriptor] of Object.entries(descriptors)) {
    checkString(key);
    if (Array.isArray(value) && key === 'length') continue;
    if (Array.isArray(value) && (!/^(0|[1-9][0-9]*)$/.test(key) || Number(key) >= value.length)) invalid();
    if (!descriptor.enumerable || descriptor.get || descriptor.set) invalid();
    checkJSON(descriptor.value, depth + 1, ancestors);
  }
  if (Array.isArray(value) && Object.keys(descriptors).length !== value.length + 1) invalid();
  ancestors.delete(value);
}

/** Strict business JSON, independent of HTTP envelope depth or byte size. */
export function encodeJSONValue(value) {
  checkJSON(value);
  return Buffer.from(JSON.stringify(value), 'utf8');
}

export function parseJSON(bytes) {
  try {
    if (!(bytes instanceof Uint8Array) || !isUtf8(bytes)) invalid();
    const text = Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString('utf8').replace(/^\uFEFF/, '');
    checkWire(text);
    return JSON.parse(text);
  } catch (error) {
    throw error instanceof BridgeError ? error : new BridgeError('INVALID_REQUEST');
  }
}

export function decodeJSONValue(bytes) {
  const value = parseJSON(bytes);
  checkJSON(value);
  return value;
}
