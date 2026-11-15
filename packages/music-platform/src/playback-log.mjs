import { createCipheriv, randomBytes } from 'node:crypto';
import { fail } from './errors.mjs';

const modulus = BigInt('0xfd90bd466ff9bc8a3fec2fbcf263b90d5c564879fa5d7aab89b31c1d5cb4139d');
function wrapKey(value) {
  let result = 1n, exponent = 65537n;
  while (exponent) {
    if (exponent & 1n) result = result * value % modulus;
    value = value * value % modulus; exponent >>= 1n;
  }
  return Buffer.from(result.toString(16).padStart(64, '0'), 'hex');
}
function encrypt(bytes, key, uuid) {
  const iv = Buffer.alloc(16);
  iv.writeUInt32LE(uuid.readUInt32LE(12) >>> 2);
  uuid.copy(iv, 4, 0, 12);
  const cipher = createCipheriv('chacha20', key, iv);
  return Buffer.concat([cipher.update(bytes), cipher.final()]);
}
/** Official NCBL v3 file channel. Frame sizes are protocol resources, not song counts. */
export function playbackLog(metadata, records) {
  if (metadata.length > 8192 || records.length > 65536) fail('LIMIT');
  let key, numeric;
  do { key = randomBytes(32); numeric = BigInt('0x' + key.toString('hex')); } while (numeric >= modulus);
  const uuid = randomBytes(16), wrapped = wrapKey(numeric), compressed = Buffer.alloc(records.length + 12);
  compressed.writeUInt32LE(0xfd2fb528); compressed[4] = 0xa0; compressed.writeUInt32LE(records.length, 5);
  compressed.writeUIntLE((records.length << 3) | 1, 9, 3); records.copy(compressed, 12);
  const count = Math.ceil(compressed.length / 32768), headerSize = 74 + metadata.length, header = Buffer.alloc(headerSize);
  header.write('NCBL'); header.writeUInt32LE(3, 4); header.writeUInt16LE(headerSize, 8);
  uuid.copy(header, 10); wrapped.copy(header, 26); header.writeUInt32LE(count - 1, 62);
  header.writeUInt32LE(compressed.length + count * 6, 66); header.writeUInt16LE(0x4343, 70);
  header.writeUInt16LE(metadata.length, 72); encrypt(metadata, wrapped, uuid).copy(header, 74);
  const chunks = [header];
  for (let at = 0; at < compressed.length; at += 32768) {
    const bytes = compressed.subarray(at, at + 32768), frame = Buffer.alloc(6);
    frame.writeUInt16LE(bytes.length); frame.writeUInt32LE(at / 32768, 2);
    chunks.push(frame, encrypt(bytes, key, uuid));
  }
  key.fill(0);
  return Buffer.concat(chunks);
}
