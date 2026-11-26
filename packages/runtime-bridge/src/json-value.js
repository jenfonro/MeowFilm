import { BridgeError } from './errors.js';

/** Self-contained so the same codec can be injected into a native JSON program. */
export function createJSONCodec(invalid) {
  const tokens = new WeakMap();
  const isRawJSON = value => tokens.has(value);

  function rawJSON(text) {
    if (typeof text !== 'string' ||
        /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$/.exec(text)?.[0] !== text) invalid();
    const value = Object.freeze(Object.create(null, {
      rawJSON: { value: text, enumerable: true },
    }));
    tokens.set(value, text);
    return value;
  }

  function decimal(token) {
    const [mantissa, exponent = '0'] = token.toLowerCase().split('e');
    const [whole, fraction = ''] = mantissa.split('.');
    if (/^-?0+$/.test(whole + fraction)) return '0';
    const digits = (whole + fraction).replace(/^-?0+/, match => match.startsWith('-') ? '-' : '');
    const trimmed = digits.replace(/0+$/, '');
    return [trimmed, Number(exponent) - fraction.length + digits.length - trimmed.length].join('e');
  }

  function numberValue(token) {
    const value = Number(token);
    return !Number.isFinite(value) || decimal(token) !== decimal(String(value)) ? rawJSON(token) : value;
  }

  function parseJSONText(text, depth = 0) {
    if (typeof text !== 'string') invalid();
    if (!/\d(?:\.?\d){15}|-0(?![0-9.])|[eE]/.test(text)) {
      try { return JSON.parse(text); } catch { invalid(); }
    }
    let index = 0;
    const number = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y;
    function whitespace() {
      while (index < text.length && /[\x20\t\r\n]/.test(text[index])) index++;
    }
    function string() {
      const start = index++;
      while (index < text.length) {
        const char = text[index++];
        if (char === '\\') index++;
        else if (char === '"') {
          try { return JSON.parse(text.slice(start, index)); }
          catch { invalid(); }
        }
      }
      invalid();
    }
    function read(depth) {
      whitespace();
      const char = text[index];
      if (char === '{' || char === '[') {
        if (depth >= 66) invalid();
        const array = char === '[', value = array ? [] : {}, end = array ? ']' : '}';
        index++;
        whitespace();
        if (text[index] === end) { index++; return value; }
        for (;;) {
          let key;
          if (!array) {
            if (text[index] !== '"') invalid();
            key = string();
            whitespace();
            if (text[index++] !== ':') invalid();
          }
          const child = read(depth + 1);
          if (array) value.push(child);
          else Object.defineProperty(value, key, {
            value: child, enumerable: true, configurable: true, writable: true,
          });
          whitespace();
          if (text[index] === end) { index++; return value; }
          if (text[index++] !== ',') invalid();
          whitespace();
        }
      }
      if (char === '"') return string();
      for (const [token, value] of [['true', true], ['false', false], ['null', null]]) {
        if (text.startsWith(token, index)) { index += token.length; return value; }
      }
      number.lastIndex = index;
      const match = number.exec(text);
      if (!match) invalid();
      index = number.lastIndex;
      return numberValue(match[0]);
    }
    const value = read(depth);
    whitespace();
    if (index !== text.length) invalid();
    return value;
  }

  function stringifyJSONValue(value) {
    const ancestors = new Set();
    function write(current) {
      if (isRawJSON(current)) return tokens.get(current);
      if (current === null || typeof current === 'boolean' || typeof current === 'string') {
        return JSON.stringify(current);
      }
      if (typeof current === 'number') {
        if (!Number.isFinite(current)) invalid();
        return Object.is(current, -0) ? '-0' : JSON.stringify(current);
      }
      if (typeof current !== 'object' || ancestors.has(current)) invalid();
      ancestors.add(current);
      try {
        if (Array.isArray(current)) return `[${current.map(write).join(',')}]`;
        return `{${Object.keys(current).map(key => `${JSON.stringify(key)}:${write(current[key])}`).join(',')}}`;
      } finally {
        ancestors.delete(current);
      }
    }
    return write(value);
  }

  function cloneJSONValue(value) {
    if (value === null || typeof value !== 'object' || isRawJSON(value)) return value;
    if (Array.isArray(value)) return value.map(cloneJSONValue);
    const copy = {};
    for (const key of Object.keys(value)) {
      const next = cloneJSONValue(value[key]);
      if (key === '__proto__') {
        Object.defineProperty(copy, key, { value: next, enumerable: true, configurable: true, writable: true });
      } else {
        copy[key] = next;
      }
    }
    return copy;
  }

  return { parseJSONText, stringifyJSONValue, cloneJSONValue, isRawJSON, rawJSON };
}

export const {
  parseJSONText, stringifyJSONValue, cloneJSONValue, isRawJSON, rawJSON,
} = createJSONCodec(() => { throw new BridgeError('INVALID_REQUEST'); });
