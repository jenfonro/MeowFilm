import {
  constants, createHash, createCipheriv, createDecipheriv, createHmac,
  createPublicKey, publicEncrypt, randomBytes,
} from 'node:crypto';
import { deflateSync, inflateSync } from 'node:zlib';
import { fault, requireValue } from './errors.js';

export function createUtilities(maxBytes) {
  const bytes = (value, encoding = 'utf8') => {
    let result;
    if (typeof value === 'string') result = Buffer.from(value, encoding);
    else if (value?.type === 'Buffer' && Array.isArray(value.data)) result = Buffer.from(value.data);
    else if (ArrayBuffer.isView(value)) {
      result = Buffer.from(value.buffer, value.byteOffset, value.byteLength);
    } else if (Array.isArray(value)) result = Buffer.from(value);
    else throw fault('INVALID_ARGUMENT');
    requireValue(result.length <= maxBytes, 'RESOURCE_LIMIT');
    return result;
  };
  return (operation, args) => {
    switch (operation) {
      case 'encode': return [...bytes(args[0], args[1])];
      case 'decode': return bytes(args[0]).toString(args[1], args[2], args[3]);
      case 'md5': return createHash('md5').update(bytes(args[0])).digest('hex');
      case 'digest': {
        requireValue(['md5', 'sha1', 'sha256', 'sha512'].includes(args[0]), 'UNSUPPORTED_CRYPTO');
        return createHash(args[0]).update(bytes(args[1])).digest('hex');
      }
      case 'hmac': {
        requireValue(['md5', 'sha1', 'sha256', 'sha512'].includes(args[0]), 'UNSUPPORTED_CRYPTO');
        return createHmac(args[0], bytes(args[1])).update(bytes(args[2])).digest(args[3] ?? 'hex');
      }
      case 'random': {
        requireValue(Number.isSafeInteger(args[0]) && args[0] >= 0 && args[0] <= maxBytes);
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
      case 'aesDecrypt': {
        const [input, mode, key, iv] = args;
        requireValue(/^aes-(128|192|256)-(cbc|ecb)$/.test(mode), 'UNSUPPORTED_CRYPTO');
        try {
          const decipher = createDecipheriv(mode, bytes(key), mode.endsWith('ecb') ? null : bytes(iv));
          return [...Buffer.concat([decipher.update(bytes(input)), decipher.final()])];
        } catch { throw fault('UNSUPPORTED_CRYPTO'); }
      }
      case 'des':
      case 'desDecrypt': {
        const [input, mode, key, iv] = args;
        requireValue(/^des-(cbc|ecb)$/.test(mode), 'UNSUPPORTED_CRYPTO');
        try {
          const shortKey = bytes(key);
          requireValue(shortKey.length === 8, 'UNSUPPORTED_CRYPTO');
          const expandedKey = Buffer.concat([shortKey, shortKey, shortKey]);
          const algorithm = mode.endsWith('ecb') ? 'des-ede3' : 'des-ede3-cbc';
          const transform = operation === 'des'
            ? createCipheriv(algorithm, expandedKey, mode.endsWith('ecb') ? null : bytes(iv))
            : createDecipheriv(algorithm, expandedKey, mode.endsWith('ecb') ? null : bytes(iv));
          return [...Buffer.concat([transform.update(bytes(input)), transform.final()])];
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
          return [...fn(bytes(args[0]), { maxOutputLength: maxBytes })];
        } catch { throw fault('RESOURCE_LIMIT'); }
      }
      default: throw fault('UNSUPPORTED_OPTION');
    }
  };
}
