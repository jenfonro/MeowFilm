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
        // Wire envelopes may add two containers; business values are checked at 64.
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

  function checkJSON(value, depth = 0, ancestors = new Set()) {
    if (depth > 64) invalid();
    if (value === null || typeof value === 'string' || typeof value === 'boolean' || isRawJSON(value)) return;
    if (typeof value === 'number') {
      if (!Number.isFinite(value)) invalid();
      return;
    }
    if (typeof value !== 'object' || ancestors.has(value)) invalid();
    const array = Array.isArray(value);
    const prototype = Object.getPrototypeOf(value);
    if (!array && prototype !== Object.prototype && prototype !== null) invalid();
    if (Object.getOwnPropertySymbols(value).length) invalid();
    ancestors.add(value);
    const descriptors = Object.getOwnPropertyDescriptors(value);
    for (const [key, descriptor] of Object.entries(descriptors)) {
      if (array && key === 'length') continue;
      if (array && (!/^(0|[1-9][0-9]*)$/.test(key) || Number(key) >= value.length)) invalid();
      if (!descriptor.enumerable || descriptor.get || descriptor.set) invalid();
      checkJSON(descriptor.value, depth + 1, ancestors);
    }
    if (array && Object.keys(descriptors).length !== value.length + 1) invalid();
    ancestors.delete(value);
  }

  function stringifyJSONValue(value, depth = 0) {
    checkJSON(value, depth);
    function write(value) {
      if (isRawJSON(value)) return tokens.get(value);
      if (value === null || typeof value !== 'object') return Object.is(value, -0) ? '-0' : JSON.stringify(value);
      if (Array.isArray(value)) return `[${value.map(write).join(',')}]`;
      return `{${Object.keys(value).map(key => `${JSON.stringify(key)}:${write(value[key])}`).join(',')}}`;
    }
    return write(value);
  }

  function cloneJSONValue(value, depth = 0) {
    return parseJSONText(stringifyJSONValue(value, depth), depth);
  }

  return { parseJSONText, stringifyJSONValue, cloneJSONValue, checkJSON, isRawJSON, rawJSON };
}

export const {
  parseJSONText, stringifyJSONValue, cloneJSONValue, checkJSON, isRawJSON, rawJSON,
} = createJSONCodec(() => { throw new BridgeError('INVALID_REQUEST'); });
