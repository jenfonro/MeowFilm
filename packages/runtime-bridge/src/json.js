import { Buffer, isUtf8 } from 'node:buffer';
import { BridgeError } from './errors.js';
import { checkJSON, parseJSONText, stringifyJSONValue } from './json-value.js';

export { checkJSON };

export function encodeJSONValue(value) {
  return Buffer.from(stringifyJSONValue(value), 'utf8');
}

export function parseJSON(bytes) {
  if (!(bytes instanceof Uint8Array) || !isUtf8(bytes)) throw new BridgeError('INVALID_REQUEST');
  return parseJSONText(Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength)
    .toString('utf8').replace(/^\uFEFF/, ''));
}

export function decodeJSONValue(bytes) {
  const value = parseJSON(bytes);
  checkJSON(value);
  return value;
}
