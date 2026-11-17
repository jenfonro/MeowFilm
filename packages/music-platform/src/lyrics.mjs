// SPDX-License-Identifier: GPL-3.0-only
// Shared text conversion of the pinned QRC/KRC/Kuwo/MRC formats.
import { inflateSync } from 'node:zlib';
import { setImmediate } from 'node:timers/promises';
import { errorCode, fail } from './errors.mjs';
import { decodeJSON, utf8 } from './model.mjs';
import { entities } from './literal.mjs';
import { decryptQRC } from './qrc.mjs';

const maximumBytes = 512 * 1024;
const timed = value => /\[(?:\d+:\d+(?:\.\d+)?|\d+,\d+)\]/.test(value);
function inflate(bytes, padding = false) {
  try {
    const result = inflateSync(bytes, { maxOutputLength: maximumBytes, info: true });
    const tail = bytes.subarray(result.engine.bytesWritten);
    // QRC encrypts complete eight-byte blocks with up to seven zero bytes.
    // KRC/Kuwo have no block padding and consume exactly one zlib document.
    if (tail.length && (!padding || tail.length > 7 || tail.some(value => value !== 0))) fail('INVALID_RESPONSE');
    return result.buffer;
  }
  catch (error) { fail(error.code === 'ERR_BUFFER_TOO_LARGE' ? 'LIMIT' : 'INVALID_RESPONSE'); }
}
function output(value) {
  if (Buffer.byteLength(value) > maximumBytes) fail('LIMIT');
  return value;
}
export async function lyricTracks(main, optional, signal) {
  const result = { text: main, translation: '', romanization: '' }, errors = {};
  for (const [track, read] of Object.entries(optional)) {
    signal?.throwIfAborted();
    try { result[track] = await read(); }
    catch (error) {
      signal?.throwIfAborted();
      const code = errorCode(error);
      if (!['INVALID_RESPONSE', 'UPSTREAM_HTTP', 'UPSTREAM_ERROR', 'NETWORK_ERROR', 'NOT_FOUND'].includes(code)) throw error;
      errors[track] = code;
    }
  }
  signal?.throwIfAborted();
  if (Object.keys(errors).length && !result.text && !result.translation && !result.romanization)
    fail(Object.values(errors)[0]);
  return Object.keys(errors).length ? { ...result, errors } : result;
}
function base64(value) {
  const encoded = value.replace(/\s/g, ''), content = encoded.replace(/=+$/, '');
  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(encoded) || content.length % 4 === 1 ||
      encoded.length - content.length > (4 - content.length % 4) % 4) fail('INVALID_RESPONSE');
  return Buffer.from(encoded, 'base64');
}
export function lyricXML(value) {
  if (!value.trimStart().startsWith('<')) return value;
  if (/<!\s*(?:DOCTYPE|ENTITY)\b/i.test(value)) fail('INVALID_RESPONSE');
  const contents = [...value.matchAll(/\bLyricContent\s*=\s*(["'])([\s\S]*?)\1/g)];
  if (contents.length !== 1) fail('INVALID_RESPONSE');
  return entities(contents[0][2]);
}
export async function qrcLyrics(encoded, signal) {
  if (!encoded) return '';
  if (timed(encoded) || encoded.trimStart().startsWith('<')) return output(lyricXML(encoded));
  encoded = encoded.replace(/\s/g, '');
  if (encoded.length % 2 || !/^[\da-f]+$/i.test(encoded)) fail('INVALID_RESPONSE');
  if (encoded.length / 2 > maximumBytes) fail('LIMIT');
  return output(lyricXML(utf8(inflate(await decryptQRC(Buffer.from(encoded, 'hex'), signal), true))));
}
export function krcLyrics(encoded) {
  const bytes = base64(encoded);
  if (bytes.subarray(0, 4).toString('ascii') !== 'krc1') fail('INVALID_RESPONSE');
  const key = [64,71,97,119,94,50,116,71,81,54,49,45,206,210,110,105];
  const encrypted = bytes.subarray(4), plain = Buffer.from(encrypted);
  for (let i = 0; i < plain.length; i++) plain[i] ^= key[i % 16];
  const value = utf8(inflate(plain)), lines = [], offsets = [], languages = [];
  for (const raw of value.split(/\r?\n/)) {
    const row = raw.trim(), offset = /^\[offset:([+-]?\d+)\]$/i.exec(row);
    if (offset) { offsets.push(integer(offset[1])); continue; }
    const meta = /^\[language:([^\]]+)\]$/.exec(row);
    if (meta) { languages.push(meta[1]); continue; }
    const match = /^\[(\d+),(\d+)\](.*)$/.exec(row);
    if (!match) { if (/^\[\d+,/.test(row)) fail('INVALID_RESPONSE'); continue; }
    const start = integer(match[1]), duration = integer(match[2]), body = match[3];
    integer(start + duration);
    const words = [...body.matchAll(/<(\d+),(\d+),(\d+)>(.*?)(?=<\d+,\d+,\d+>|$)/g)];
    if (words.length ? words[0].index !== 0 : /<\d+,/.test(body)) fail('INVALID_RESPONSE');
    const converted = words.map(word => {
      const at = integer(start + integer(word[1])), length = integer(word[2]);
      integer(at + length);
      if (/\(\d+,\d+,\d+\)|<\d+,/.test(word[4])) fail('INVALID_RESPONSE');
      return `(${at},${length},${word[3]})${word[4]}`;
    }).join('');
    lines.push({ start, plain: words.length ? words.map(word => word[4]).join('') : body,
      value: `[${start},${duration}]` + (words.length ? converted : body) });
  }
  const prefix = offsets.length ? `[offset:${offsets[0]}]\n` : '';
  let translation = '';
  if (languages.length === 1 && offsets.length <= 1) {
    // Language is optional alignment metadata, never a condition on the main KRC.
    let language;
    try { language = decodeJSON(Buffer.from(languages[0], 'base64')); } catch {}
    const tracks = Array.isArray(language?.content) ? language.content.filter(row => row?.type === 1) : [];
    const content = tracks[0]?.lyricContent;
    if (language?.version === 1 && tracks.length === 1 && Array.isArray(content) && content.length === lines.length &&
        content.every(row => Array.isArray(row) && row.length === 1 && typeof row[0] === 'string' &&
          !/[\r\n]|\[\d{1,3}:\d{2}|<\d{1,3}:\d{2}/.test(row[0]))) {
      const clock = line => Math.max(0, line.start + (offsets[0] ?? 0)), counts = new Map();
      for (const line of lines) counts.set(clock(line), (counts.get(clock(line)) ?? 0) + 1);
      const translated = lines.flatMap((line, index) => line.plain.trim() && content[index][0].trim() && counts.get(clock(line)) === 1
        ? [stamp(line.start) + content[index][0]] : []);
      if (translated.length) translation = output(prefix + translated.join('\n'));
    }
  }
  return { text: output(prefix + lines.map(line => line.value).join('\n')), translation, romanization: '' };
}
function stamp(ms) {
  const minutes = Math.floor(ms / 60000), seconds = (ms % 60000 / 1000).toFixed(3).padStart(6, '0');
  return `[${String(minutes).padStart(2, '0')}:${seconds}]`;
}
function integer(value) {
  const result = Number(value);
  if (!Number.isSafeInteger(result)) fail('INVALID_RESPONSE');
  return result;
}
function literal(value) {
  if (/\0|\(\d+,\d+(?:,\d+)?\)|<-?\d+,|\[\d+:\d+|\[offset:/i.test(value)) fail('INVALID_RESPONSE');
  return value;
}
export function kuwoLyrics(bytes) {
  const split = bytes.indexOf(Buffer.from('\r\n\r\n'));
  if (bytes.subarray(0, 10).toString('ascii').toLowerCase() !== 'tp=content' || split < 0) fail('INVALID_RESPONSE');
  const encrypted = base64(utf8(inflate(bytes.subarray(split + 4))));
  const key = Buffer.from('yeelion');
  for (let index = 0; index < encrypted.length; index++) encrypted[index] ^= key[index % key.length];
  const value = utf8(encrypted), lines = [], translations = [], seen = new Set();
  let factor = 1, factor2 = 1, offset = '', hasFactor = false;
  for (const raw of value.replace(/^\uFEFF/, '').split(/\r\n?|\n/)) {
    const row = raw.trim();
    const codec = /^\[kuwo:([0-7]+)\]$/.exec(row);
    if (codec) {
      const number = integer(Number.parseInt(codec[1], 8)); factor = Math.floor(number / 10); factor2 = number % 10;
      if (!factor || !factor2 || hasFactor) fail('INVALID_RESPONSE');
      hasFactor = true;
    } else if (/^\[kuwo:/i.test(row)) fail('INVALID_RESPONSE');
    else if (/^\[offset:/i.test(row)) {
      const match = /^\[offset:([+-]?\d+)\]$/i.exec(row);
      if (offset || !match) fail('INVALID_RESPONSE');
      offset = `[offset:${integer(match[1])}]\n`;
    }
    const match = /^\[(\d+):(\d+(?:\.\d+)?)\](.*)$/.exec(row);
    if (!match) continue;
    if (Number(match[2]) >= 60) fail('INVALID_RESPONSE');
    const at = integer(integer(match[1]) * 60000 + Math.round(Number(match[2]) * 1000));
    if (seen.has(at)) {
      if (lines.length < 2) continue;
      const translated = lines.pop();
      translations.push({ ...translated, at: lines.at(-1).at });
    }
    seen.add(at); lines.push({ at, text: match[3].trim() });
  }
  const converted = lines.map(line => {
    const markers = [...line.text.matchAll(/<(-?\d+),(-?\d+)(?:,-?\d+)?>/g)];
    if (!markers.length) return stamp(line.at) + literal(entities(line.text));
    if (markers[0].index !== 0) fail('INVALID_RESPONSE');
    const words = markers.map((match, index) => {
      const a = integer(match[1]), b = integer(match[2]);
      return { start: integer(Math.floor(Math.abs(integer(a + b)) / (factor * 2))),
        duration: integer(Math.floor(Math.abs(integer(a - b)) / (factor2 * 2))),
        text: literal(entities(line.text.slice(match.index + match[0].length, markers[index + 1]?.index ?? line.text.length))) };
    });
    for (let index = 1; index < words.length; index++) {
      const previous = words[index - 1], current = words[index];
      if (current.start < previous.start + previous.duration) {
        previous.start = Math.min(previous.start, current.start); previous.duration = current.start - previous.start;
      }
    }
    const duration = words.reduce((end, word) => Math.max(end, integer(word.start + word.duration)), 0);
    integer(line.at + duration);
    return `[${line.at},${duration}]` + words.map(word => `(${line.at + word.start},${word.duration},0)${word.text}`).join('');
  });
  if (!lines.length) fail('INVALID_RESPONSE');
  return { text: output(offset + converted.join('\n')),
    translation: output(translations.length ? offset + translations.map(line =>
      stamp(line.at) + literal(entities(line.text.replace(/<(-?\d+),(-?\d+)(?:,-?\d+)?>/g, '')))).join('\n') : ''), romanization: '' };
}
export async function miguLyrics(bytes, signal) {
  const encoded = utf8(bytes).trim();
  if (encoded.length < 32 || encoded.length % 16 || !/^[\da-f]+$/i.test(encoded)) fail('INVALID_RESPONSE');
  const values = encoded.match(/.{16}/g).map(value => BigInt.asIntN(64, BigInt('0x' + value)));
  const key = [27303562373562475n,18014862372307051n,22799692160172081n,34058940340699235n], delta = 2654435769n;
  let sum = BigInt(6 + Math.floor(52 / values.length)) * delta, y = values[0];
  while (sum !== 0n) {
    const e = Number(sum >> 2n & 3n);
    for (let at = values.length - 1; at >= 0; at--) {
      if (at % 1024 === 0) { signal?.throwIfAborted(); await setImmediate(); }
      const z = values[(at + values.length - 1) % values.length];
      const a = BigInt.asIntN(64, (y ^ sum) + (z ^ key[(at & 3) ^ e]));
      const b = BigInt.asIntN(64, ((z >> 5n) ^ (y << 2n)) + ((y >> 3n) ^ (z << 4n)));
      values[at] = BigInt.asIntN(64, values[at] - (a ^ b)); y = values[at];
    }
    sum -= delta;
  }
  const buffer = Buffer.alloc(values.length * 8);
  values.forEach((value, index) => buffer.writeBigInt64LE(value, index * 8));
  let end = buffer.length;
  while (end && buffer.readUInt16LE(end - 2) === 0) end -= 2;
  let value;
  try { value = new TextDecoder('utf-16le', { fatal: true }).decode(buffer.subarray(0, end)); }
  catch { fail('INVALID_RESPONSE'); }
  if (!value || value.includes('\0')) fail('INVALID_RESPONSE');
  const lines = value.replace(/^\uFEFF/, '').split(/\r\n?|\n/).map(raw => {
    const row = raw.trim();
    const head = /^\[(\d+),(\d+)\](.*)$/.exec(row);
    if (!head) { if (/^\[\d+,/.test(row)) fail('INVALID_RESPONSE'); return ''; }
    const start = integer(head[1]), duration = integer(head[2]), limit = integer(start + duration), body = head[3];
    const words = [...body.matchAll(/\((\d+),(\d+)\)/g)];
    if (!words.length) return `[${start},${duration}]` + literal(body);
    if (words.at(-1).index + words.at(-1)[0].length !== body.length) fail('INVALID_RESPONSE');
    let from = 0, previous = start;
    return `[${start},${duration}]` + words.map(word => {
      const at = integer(word[1]), length = integer(word[2]);
      if (at < previous || integer(at + length) > limit) fail('INVALID_RESPONSE');
      const text = literal(body.slice(from, word.index));
      from = word.index + word[0].length; previous = at;
      return `(${at},${length},0)` + text;
    }).join('');
  }).filter(Boolean);
  if (!lines.length) fail('INVALID_RESPONSE');
  return output(lines.join('\n'));
}
