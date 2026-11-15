// SPDX-FileCopyrightText: Copyright (c) 2024 沉默の金 <cmzj@cmzj.org>
// SPDX-License-Identifier: GPL-3.0-only
// QQMusicApi 27861e51 algorithms/tripledes.py: nonstandard S-box and byte layout.
import { setImmediate } from 'node:timers/promises';
import { fail } from './errors.mjs';

const sbox = [
  [14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7,0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8,4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0,15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13],
  [15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10,3,13,4,7,15,2,8,15,12,0,1,10,6,9,11,5,0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15,13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9],
  [10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8,13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1,13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7,1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12],
  [7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15,13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9,10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4,3,15,0,6,10,10,13,8,9,4,5,11,12,7,2,14],
  [2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9,14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6,4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14,11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3],
  [12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11,10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8,9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6,4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13],
  [4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1,13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6,1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2,6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12],
  [13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7,1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2,7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8,2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11],
];
const order = [4,5,6,7,0,1,2,3];
const cOrder = [56,48,40,32,24,16,8,0,57,49,41,33,25,17,9,1,58,50,42,34,26,18,10,2,59,51,43,35];
const dOrder = [62,54,46,38,30,22,14,6,61,53,45,37,29,21,13,5,60,52,44,36,28,20,12,4,27,19,11,3];
const compression = [13,16,10,23,0,4,2,27,14,5,20,9,22,18,11,3,25,7,15,6,26,19,12,1,40,51,30,36,46,54,29,39,50,44,32,47,43,48,38,55,33,52,45,41,49,35,28,31];
const shifts = [1,1,2,2,2,2,2,2,1,2,2,2,2,2,2,1];
const expansion = [32,1,2,3,4,5,4,5,6,7,8,9,8,9,10,11,12,13,12,13,14,15,16,17,16,17,18,19,20,21,20,21,22,23,24,25,24,25,26,27,28,29,28,29,30,31,32,1];
const permutation = [16,25,12,11,3,20,4,15,31,17,9,6,27,14,1,22,30,24,8,18,0,5,29,23,13,19,2,26,10,21,28,7];
function schedule(text, decrypt) {
  const bytes = Buffer.from(text), v0 = bytes.readUInt32LE(), v1 = bytes.readUInt32LE(4);
  const pick = bits => bits.reduce((value, bit, index) =>
    value | ((bit < 32 ? v0 >>> (31 - bit) : v1 >>> (63 - bit)) & 1) << (31 - index), 0) >>> 0;
  let c = pick(cOrder), d = pick(dOrder);
  const result = [];
  for (let round = 0; round < 16; round++) {
    c = ((c << shifts[round]) | (c >>> (28 - shifts[round]))) & 0xfffffff0;
    d = ((d << shifts[round]) | (d >>> (28 - shifts[round]))) & 0xfffffff0;
    const keys = new Uint8Array(8);
    for (let at = 0; at < 48; at++)
      keys[Math.floor(at / 6)] = keys[Math.floor(at / 6)] << 1 |
        (at < 24 ? c >>> (31 - compression[at]) : d >>> (31 - (compression[at] - 27))) & 1;
    result[decrypt ? 15 - round : round] = keys;
  }
  return result;
}
const keys = [schedule('!@#)(NHL', true), schedule('123ZXC!@', false), schedule('!@#)(*$%', true)];
function f(state, key) {
  let value = 0;
  for (let i = 0; i < 8; i++) {
    let six = 0;
    for (let j = 0; j < 6; j++) six = six << 1 | state >>> (32 - expansion[i * 6 + j]) & 1;
    six ^= key[i];
    value = value << 4 | sbox[i][(six & 32) | (six & 31) >>> 1 | (six & 1) << 4];
  }
  return permutation.reduce((result, bit) => result << 1 | value >>> bit & 1, 0) >>> 0;
}
export async function decryptQRC(data, signal) {
  if (!data.length || data.length % 8) fail('INVALID_RESPONSE');
  const result = Buffer.from(data);
  for (let offset = 0; offset < result.length; offset += 8) {
    if (offset % 16384 === 0) { signal?.throwIfAborted(); await setImmediate(); }
    const block = result.subarray(offset, offset + 8);
    for (const rounds of keys) {
      let left = 0, right = 0;
      for (let group = 0; group < 4; group++) for (const index of order) {
        left = left << 1 | block[index] >>> (6 - group * 2) & 1;
        right = right << 1 | block[index] >>> (7 - group * 2) & 1;
      }
      for (let round = 0; round < 15; round++) [left, right] = [right, f(right, rounds[round]) ^ left];
      left ^= f(right, rounds[15]);
      block.fill(0);
      for (let group = 0; group < 4; group++) for (let index = 0; index < 8; index++) {
        const bit = 31 - group * 8 - index;
        block[order[index]] |= (left >>> bit & 1) << (6 - group * 2) | (right >>> bit & 1) << (7 - group * 2);
      }
    }
  }
  signal?.throwIfAborted();
  return result;
}
