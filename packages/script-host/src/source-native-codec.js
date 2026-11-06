import { Buffer } from 'buffer/';
import iconv from 'iconv-lite';

const native = globalThis.__meowNative;
const basic = new Set([
  'utf8', 'utf-8', 'utf16le', 'utf-16le', 'ucs2', 'ucs-2',
  'hex', 'base64', 'base64url', 'latin1', 'binary', 'ascii',
]);

export function sourceUtility(operation, args) {
  if (operation !== 'encode' && operation !== 'decode') return native(operation, args);
  const encoding = String(args[1] ?? 'utf8').toLowerCase();
  if (basic.has(encoding)) return native(operation, args);
  if (!iconv.encodingExists(encoding)) {
    throw Object.assign(new Error('Unsupported encoding'), { code: 'UNSUPPORTED_OPTION' });
  }
  if (operation === 'encode') return iconv.encode(String(args[0]), encoding);
  const value = args[0];
  const bytes = Buffer.from(value.buffer, value.byteOffset, value.byteLength);
  return iconv.decode(bytes.subarray(args[2] ?? 0, args[3] ?? bytes.length), encoding);
}

export { Buffer };
