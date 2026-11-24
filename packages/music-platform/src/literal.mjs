import { decodeJSON, utf8 } from './model.mjs';
import { fail } from './errors.mjs';

const entityLiterals = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };
const wordPattern = /[A-Za-z_$][A-Za-z0-9_$]*/y;
const numberPattern = /-?\d+(?:\.\d+)?(?:e[+-]?\d+)?/iy;

/** Kuwo's legacy wire notation is data, not executable JavaScript. */
export function kuwoJSON(bytes) {
  const source = utf8(bytes);
  let output = '', quote = '';
  for (let at = 0; at < source.length; at++) {
    const ch = source[at];
    if (!quote) {
      if (ch === "'" || ch === '"') { quote = ch; output += '"'; }
      else output += ch;
    } else if (ch === '\\') {
      if (++at >= source.length) fail('INVALID_RESPONSE');
      const next = source[at];
      output += next === "'" && quote === "'" ? "'" : '\\' + next;
    } else if (ch === quote) {
      let next = at + 1;
      while (/\s/.test(source[next] ?? '') && next < source.length) next++;
      if (quote === "'" && next < source.length && !':,}]'.includes(source[next])) output += ch;
      else { quote = ''; output += '"'; }
    } else output += ch === '"' ? '\\"' : ch;
  }
  return decodeJSON(Buffer.from(output));
}
export function entities(value) {
  return value.replace(/&(amp|lt|gt|quot|apos|nbsp|#\d+|#x[\da-f]+);/gi, (raw, name) => {
    const lower = name.toLowerCase();
    if (Object.hasOwn(entityLiterals, lower)) return entityLiterals[lower];
    const hex = lower[1] === 'x';
    const number = Number.parseInt(name.slice(hex ? 2 : 1), hex ? 16 : 10);
    return number > 0 && number <= 0x10ffff && !(number >= 0xd800 && number <= 0xdfff) ? String.fromCodePoint(number) : raw;
  });
}
function literal(source, aliases = new Map()) {
  let at = 0;
  const white = () => { while (at < source.length && /\s/.test(source[at])) at++; };
  const take = ch => { white(); if (source[at] !== ch) return false; at++; return true; };
  const need = ch => { if (!take(ch)) fail('INVALID_RESPONSE'); };
  function quoted() {
    white();
    const start = at, quote = source[at++];
    while (at < source.length) {
      if (source[at] === '\\') {
        if (at + 1 >= source.length) fail('INVALID_RESPONSE');
        at += 2;
        continue;
      }
      if (source[at++] === quote) return decodeJSON(Buffer.from(source.slice(start, at)));
    }
    fail('INVALID_RESPONSE');
  }
  const word = () => {
    white();
    wordPattern.lastIndex = at;
    const match = wordPattern.exec(source);
    if (!match) fail('INVALID_RESPONSE');
    at = wordPattern.lastIndex;
    return match[0];
  };
  function value() {
    white();
    if (take('{')) {
      const result = {};
      if (take('}')) return result;
      do {
        white(); const key = source[at] === '"' ? quoted() : word();
        need(':');
        Object.defineProperty(result, key, { value: value(), enumerable: true, configurable: true, writable: true });
        if (take('}')) return result;
      } while (take(','));
      fail('INVALID_RESPONSE');
    }
    if (take('[')) {
      const result = [];
      if (take(']')) return result;
      do { result.push(value()); if (take(']')) return result; } while (take(','));
      fail('INVALID_RESPONSE');
    }
    if (source[at] === '"') return quoted();
    numberPattern.lastIndex = at;
    const match = numberPattern.exec(source);
    if (match) { at = numberPattern.lastIndex; return decodeJSON(Buffer.from(match[0])); }
    const name = word();
    if (name === 'true') return true;
    if (name === 'false') return false;
    if (name === 'null') return null;
    if (aliases.has(name)) return aliases.get(name);
    fail('INVALID_RESPONSE');
  }
  const result = value();
  white(); if (at !== source.length) fail('INVALID_RESPONSE');
  return result;
}
export function kuwoPage(bytes) {
  const html = utf8(bytes);
  for (const [, script] of html.matchAll(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/g)) {
    const match = /^window\.__NUXT__=\(function\(([^)]*)\)\{return ([\s\S]*)\}\(([\s\S]*)\)\);?$/.exec(script.trim());
    if (!match) continue;
    const names = match[1] ? match[1].split(',') : [], values = literal('[' + match[3] + ']');
    const result = literal(match[2], new Map(names.map((name, index) => [name, values[index]])));
    if (result.data?.[0]?.songinfo) return result.data[0].songinfo;
  }
  fail('INVALID_RESPONSE');
}
