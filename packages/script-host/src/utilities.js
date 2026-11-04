import {
  constants, createHash, createCipheriv, createPublicKey, publicEncrypt, randomBytes,
} from 'node:crypto';
import { deflateSync, inflateSync } from 'node:zlib';
import { fault, requireValue } from './errors.js';

export function createUtilities(maxMemoryBytes) {
  const bytes = (value, encoding = 'utf8') => {
    let result;
    if (typeof value === 'string') result = Buffer.from(value, encoding);
    else if (value?.type === 'Buffer' && Array.isArray(value.data)) result = Buffer.from(value.data);
    else if (ArrayBuffer.isView(value)) {
      result = Buffer.from(value.buffer, value.byteOffset, value.byteLength);
    } else if (Array.isArray(value)) result = Buffer.from(value);
    else throw fault('INVALID_ARGUMENT');
    return result;
  };
  return (operation, args) => {
    switch (operation) {
      case 'encode': return [...bytes(args[0], args[1])];
      case 'decode': return bytes(args[0]).toString(args[1], args[2], args[3]);
      case 'md5': return createHash('md5').update(bytes(args[0])).digest('hex');
      case 'random': {
        requireValue(Number.isSafeInteger(args[0]) && args[0] >= 0 &&
          args[0] <= maxMemoryBytes, 'RESOURCE_LIMIT');
        return [...randomBytes(args[0])];
      }
      case 'aes': {
        const [input, mode, key, iv] = args;
        requireValue(/^aes-(128|192|256)-(cbc|ecb)$/.test(mode), 'UNSUPPORTED_CRYPTO');
        try {
          const cipher = createCipheriv(mode, bytes(key), mode.endsWith('ecb') ? null : bytes(iv));
          return [...Buffer.concat([cipher.update(bytes(input)), cipher.final()])];
        } catch { throw fault('UNSUPPORTED_CRYPTO'); }
      }
      case 'rsa': {
        // Historical LX primitive: 1024-bit RSA, left zero padding, NO_PADDING.
        try {
          const key = createPublicKey(args[1]);
          const data = bytes(args[0]);
          requireValue(key.asymmetricKeyType === 'rsa' &&
            key.asymmetricKeyDetails.modulusLength === 1024 && data.length <= 128);
          const padded = Buffer.alloc(128);
          data.copy(padded, 128 - data.length);
          return [...publicEncrypt({ key, padding: constants.RSA_NO_PADDING }, padded)];
        } catch { throw fault('UNSUPPORTED_CRYPTO'); }
      }
      case 'inflate':
      case 'deflate': {
        try {
          const fn = operation === 'inflate' ? inflateSync : deflateSync;
          return [...fn(bytes(args[0]), { maxOutputLength: maxMemoryBytes })];
        } catch { throw fault('RESOURCE_LIMIT'); }
      }
      default: throw fault('UNSUPPORTED_OPTION');
    }
  };
}
