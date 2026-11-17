import test from 'node:test';
import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { encodeJSONValue, decodeJSONValue } from '../src/index.js';
import {
  parseJSONText, stringifyJSONValue, cloneJSONValue, checkJSON, isRawJSON, rawJSON, createJSONCodec,
} from '../src/json-value.js';

const decode = text => decodeJSONValue(Buffer.from(text));

test('logical JSON values preserve bridge types and Unicode without a total byte gate', () => {
  for (const value of [null, false, true, 0, -1, 1.25, Number.MAX_SAFE_INTEGER,
    '猫😀e\u0301"\\', [], {}, { array: [null, false, 42, { text: 'a' }] }]) {
    const bytes = encodeJSONValue(value);
    assert.ok(Buffer.isBuffer(bytes));
    assert.deepEqual(decodeJSONValue(bytes), value);

  }
  for (const size of [512 * 1024, (1024 * 1024) + 1, 8 * (1024 * 1024) + 1]) {
    const value = 'x'.repeat(size - 2);
    const bytes = encodeJSONValue(value);
    assert.equal(bytes.length, size);
    assert.equal(decodeJSONValue(bytes), value);
  }
});

test('logical encoding rejects values that cannot serialize without loss or executing code', () => {
  const cycle = {}; cycle.self = cycle;
  const getter = Object.defineProperty({}, 'value', { enumerable: true, get() { throw new Error('do not execute'); } });
  const symbol = { [Symbol('private')]: 1 };
  const hidden = Object.defineProperty({}, 'value', { value: 1 });
  const arrayExtra = []; arrayExtra.extra = 1;
  for (const value of [undefined, () => {}, Symbol('value'), 1n, NaN, Infinity, -Infinity,
    new Date(0), Buffer.from('x'), new Map(), cycle, getter, symbol, hidden, arrayExtra, [, 1],
    { value: undefined }]) {
    assert.throws(() => encodeJSONValue(value), { code: 'INVALID_REQUEST' });
  }
});

test('logical decoding rejects malformed UTF-8 and grammar', () => {
  for (const bytes of [Buffer.from([0xc3, 0x28]), Buffer.from([0xed, 0xa0, 0x80]),
    Buffer.from([0xff]), Buffer.from([0xf0, 0x9f])]) {
    assert.throws(() => decodeJSONValue(bytes), { code: 'INVALID_REQUEST' });
  }
  for (const text of ['', 'undefined', '{', '{} trailing', '01', '[1,]']) {
    assert.throws(() => decode(text), { code: 'INVALID_REQUEST' });
  }
  assert.throws(() => decodeJSONValue('{}'), { code: 'INVALID_REQUEST' });
  assert.deepEqual(decode('{"left":{"same":1},"right":{"same":2},"array":["\\\"x\\\":","猫😀"]}'),
    { left: { same: 1 }, right: { same: 2 }, array: ['"x":', '猫😀'] });
});

test('unused JSON material survives edits, copies and UTF-8 transport without numeric rounding', () => {
  const text = '{"é":1,"é":2,"__proto__":3,"extension":{"integer":9007199254740993,' +
    '"fraction":1.0000000000000001,"large":1e400,"small":-1e-400},"text":"\\ud800","name":"before"}';
  const value = cloneJSONValue(parseJSONText(text));
  value.name = 'after';
  const encoded = stringifyJSONValue(value);
  assert.equal(encoded, text.replace('"before"', '"after"'));
  assert.equal(encodeJSONValue(decodeJSONValue(Buffer.from(encoded))).toString(), encoded);
  assert.equal(Object.getPrototypeOf(value), Object.prototype);
  assert.equal(Object.hasOwn(value, '__proto__'), true);
  for (const token of ['0.0', '0e100000', '1e0', '1.000', '100.00', '-0.0']) {
    assert.equal(typeof parseJSONText(token), 'number');
  }
  assert.equal(stringifyJSONValue(parseJSONText('-0')), '-0');
  assert.throws(() => stringifyJSONValue({ rawJSON: undefined }), { code: 'INVALID_REQUEST' });
});

test('logical JSON enforces bridge depth 64 independently of envelope depth', () => {
  const nested = depth => { let value = null; while (depth--) value = [value]; return value; };
  assert.deepEqual(decodeJSONValue(encodeJSONValue(nested(64))), nested(64));
  assert.throws(() => encodeJSONValue(nested(65)), { code: 'INVALID_REQUEST' });
  assert.throws(() => decode('['.repeat(65) + 'null' + ']'.repeat(65)), { code: 'INVALID_REQUEST' });
  assert.throws(() => decode('['.repeat(500) + 'null' + ']'.repeat(500)), { code: 'INVALID_REQUEST' });
});

test('control envelope depth allowances do not widen business values', () => {
  const nested = depth => { let value = null; while (depth--) value = [value]; return value; };
  const body = nested(64);
  for (const [envelope, depth] of [[{ value: body }, -1], [{ params: { config: body } }, -2]]) {
    assert.throws(() => stringifyJSONValue(envelope), { code: 'INVALID_REQUEST' });
    assert.throws(() => cloneJSONValue(envelope), { code: 'INVALID_REQUEST' });
    assert.doesNotThrow(() => checkJSON(envelope, depth));
    assert.deepEqual(parseJSONText(stringifyJSONValue(envelope, depth)), envelope);
    const cloned = cloneJSONValue(envelope, depth);
    assert.deepEqual(cloned, envelope);
    assert.notEqual(cloned, envelope);
  }
  const tooDeep = { params: { config: nested(65) } };
  assert.throws(() => stringifyJSONValue(tooDeep, -2), { code: 'INVALID_REQUEST' });
  assert.throws(() => cloneJSONValue(tooDeep, -2), { code: 'INVALID_REQUEST' });
  assert.throws(() => parseJSONText(JSON.stringify(tooDeep)), { code: 'INVALID_REQUEST' });
  assert.throws(() => encodeJSONValue([body]), { code: 'INVALID_REQUEST' });
});

test('plain JSON text programs retain 96 levels through explicit depth offsets using the same carriers', () => {
  const token = '9007199254740993';
  let value = rawJSON(token);
  for (let depth = 0; depth < 96; depth++) value = [value];
  const text = '['.repeat(96) + token + ']'.repeat(96);
  assert.throws(() => stringifyJSONValue(value), { code: 'INVALID_REQUEST' });
  assert.throws(() => parseJSONText(text), { code: 'INVALID_REQUEST' });
  assert.throws(() => cloneJSONValue(value), { code: 'INVALID_REQUEST' });
  assert.equal(stringifyJSONValue(value, -Infinity), text);
  assert.equal(stringifyJSONValue(cloneJSONValue(value, -Infinity), -Infinity), text);
  const parsed = parseJSONText(text, -Infinity);
  assert.equal(stringifyJSONValue(parsed, -Infinity), text);
  let leaf = parsed;
  for (let depth = 0; depth < 96; depth++) leaf = leaf[0];
  assert.equal(isRawJSON(leaf), true);
  assert.equal(stringifyJSONValue(leaf), token);
  assert.throws(() => stringifyJSONValue([Infinity], -Infinity), { code: 'INVALID_REQUEST' });
});

test('raw numbers are opaque immutable carriers, not objects with a reserved property name', () => {
  for (const token of ['9007199254740993', '1.0000000000000001', '1e400', '-1e-400', '-0']) {
    const value = rawJSON(token);
    assert.equal(isRawJSON(value), true);
    assert.equal(value.rawJSON, token);
    assert.equal(Object.getPrototypeOf(value), null);
    assert.equal(Object.isFrozen(value), true);
    assert.equal(stringifyJSONValue(value), token);
    assert.throws(() => { value.rawJSON = '0'; }, TypeError);
  }
  for (const value of [null, undefined, 1, '1', [], {}, { rawJSON: '9007199254740993' }]) {
    assert.equal(isRawJSON(value), false);
  }
  assert.equal(stringifyJSONValue({ rawJSON: '1e400' }), '{"rawJSON":"1e400"}');
  for (const value of [1, null, undefined, '', ' 1', '1 ', '1\n', '+1', '01', '-01', '1.',
    '.1', '1e', '1e+', 'NaN', 'Infinity', 'null', 'true', '"1"', '[1]', '{}', '1,2']) {
    assert.throws(() => rawJSON(value), { code: 'INVALID_REQUEST' });
  }
});

test('numbers stay ordinary when decimal value and signed zero survive, otherwise tokens stay exact', () => {
  for (const token of ['0', '-0', '-0.0', '0e999999', '1', '-1', '1.25', '1e0', '1.000',
    '100.00', '9007199254740992', '1e20', '-1.25E+2', '1.7976931348623157e308', '5e-324']) {
    const value = parseJSONText(token);
    assert.equal(typeof value, 'number', token);
    assert.ok(Object.is(value, JSON.parse(token)), token);
  }
  for (const token of ['9007199254740993', '-9007199254740993', '1.0000000000000001',
    '1.234567890123456789', '1e400', '-1e400', '1e-400', '-1e-400', '1e999999999999999999999']) {
    const value = parseJSONText(token);
    assert.equal(isRawJSON(value), true, token);
    assert.equal(stringifyJSONValue(cloneJSONValue(value)), token);
  }
  assert.equal(stringifyJSONValue(-0), '-0');
  assert.equal(Object.is(cloneJSONValue(-0), -0), true);
});

test('JSON grammar retains last duplicate keys, own __proto__, UTF-16 and escaped strings', () => {
  const text = '{"value":1,"\\u0076alue":9007199254740993,"__proto__":{"before":true},' +
    '"__proto__":{"after":true},"é":1,"é":2,"\\ud800":"\\udc00","escaped":"\\\\\\\"\\b\\f\\n\\r\\t"}';
  const value = parseJSONText(text);
  assert.equal(value.value.rawJSON, '9007199254740993');
  assert.equal(Object.getPrototypeOf(value), Object.prototype);
  assert.deepEqual(Object.getOwnPropertyDescriptor(value, '__proto__'), {
    value: { after: true }, enumerable: true, configurable: true, writable: true,
  });
  assert.equal(value['é'], 1);
  assert.equal(value['é'], 2);
  assert.equal(value['\ud800'], '\udc00');
  assert.equal(value.escaped, '\\"\b\f\n\r\t');
  assert.equal(stringifyJSONValue(parseJSONText(stringifyJSONValue(value))), stringifyJSONValue(value));
  for (const text of [
    '\t\r\n [ 1, true, false, null, {}, [] ] \n', '"raw 猫😀"', '"\\u0000\\u007f"',
    '{"0":true,"a":null,"0":false}', '"\\uD83D\\uDE00"', '"\\"\\\\\\/"', '"\ud800"',
  ]) assert.deepEqual(parseJSONText(text), JSON.parse(text));
  for (const text of ['', ' ', '\uFEFF{}', '\u00a0null', '+1', '-01', '1.', '.1', '1e', '1e+',
    '1 2', 'truefalse', 'Null', 'NaN', 'Infinity', 'undefined', '"\\x00"', '"\\u123"',
    '"\\uZZZZ"', '"\\v"', '"line\nbreak"', '"\u0000"', '"unterminated', '"end\\',
    '[,]', '[1,]', '[1 2]', '[}', '{]', '{a:1}', '{"a"}', '{"a":}', '{"a":1,}',
    '{"a":1 "b":2}', '{"a":1} null', '{}/*comment*/']) {
    assert.throws(() => parseJSONText(text), { code: 'INVALID_REQUEST' }, text);
  }
});

test('codec factory injects unchanged without raw JSON builtins or parse reviver context', () => {
  const codec = runInNewContext(`(() => {
    const parse = JSON.parse;
    JSON.parse = (text, reviver) => {
      if (reviver !== undefined) throw new Error('reviver unavailable');
      return parse(text);
    };
    for (const name of ['rawJSON', 'isRawJSON']) Object.defineProperty(JSON, name, {
      get() { throw new Error('raw JSON builtin unavailable'); }
    });
    return (${createJSONCodec.toString()})(() => { throw { code: 'LOCAL_JSON_ERROR' }; });
  })()`);
  const text = '{"integer":9007199254740993,"decimal":1.234567890123456789,"large":1e400,' +
    '"zero":-0,"\\ud800":"\\udc00","__proto__":{"data":true},"nested":[false,null,1]}';
  const value = codec.cloneJSONValue(codec.parseJSONText(text));
  assert.equal(codec.isRawJSON(value.integer), true);
  assert.equal(value.integer.rawJSON, '9007199254740993');
  assert.equal(codec.stringifyJSONValue(value), text);
  assert.equal(codec.stringifyJSONValue(codec.rawJSON('1e-400')), '1e-400');
  const deep = '['.repeat(96) + '1e400' + ']'.repeat(96);
  assert.equal(codec.stringifyJSONValue(codec.cloneJSONValue(codec.parseJSONText(deep, -Infinity), -Infinity),
    -Infinity), deep);
  assert.throws(() => codec.parseJSONText('{bad'), { code: 'LOCAL_JSON_ERROR' });
  assert.throws(() => codec.stringifyJSONValue(Infinity), { code: 'LOCAL_JSON_ERROR' });
});

test('strict JSON UTF-8 decoding works without an ICU-backed fatal TextDecoder', () => {
  // Node builds without ICU (Node Mobile on iOS) reject the fatal option.
  const original = globalThis.TextDecoder;
  globalThis.TextDecoder = class extends original {
    constructor(label, options) {
      if (options?.fatal) throw Object.assign(new TypeError('fatal decoding needs ICU'), { code: 'ERR_NO_ICU' });
      super(label, options);
    }
  };
  try {
    const value = { text: '猫😀\uFEFF\uFFFD' };
    const body = Buffer.from(JSON.stringify(value));
    assert.deepEqual(decodeJSONValue(Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), body])), value);
    const backing = new Uint8Array(body.length + 2);
    backing.set(body, 1);
    assert.deepEqual(decodeJSONValue(backing.subarray(1, body.length + 1)), value);
    const template = JSON.stringify({ text: '@' }).split('@');
    for (const bytes of [[0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xe2, 0x82], [0xf4, 0x90, 0x80, 0x80]]) {
      const malformed = Buffer.concat([Buffer.from(template[0]), Buffer.from(bytes), Buffer.from(template[1])]);
      assert.throws(() => decodeJSONValue(malformed), { code: 'INVALID_REQUEST' });
    }

  } finally { globalThis.TextDecoder = original; }
});
