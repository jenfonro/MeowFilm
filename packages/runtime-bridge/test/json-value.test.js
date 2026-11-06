import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeJSONValue, decodeJSONValue } from '../src/index.js';

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

test('logical encoding uses the bridge strict JSON semantics', () => {
  const cycle = {}; cycle.self = cycle;
  const getter = Object.defineProperty({}, 'value', { enumerable: true, get() { throw new Error('do not execute'); } });
  const symbol = { [Symbol('private')]: 1 };
  const hidden = Object.defineProperty({}, 'value', { value: 1 });
  const arrayExtra = []; arrayExtra.extra = 1;
  for (const value of [undefined, () => {}, Symbol('value'), 1n, NaN, Infinity, -Infinity,
    Number.MAX_SAFE_INTEGER + 1, '\ud800', '\udfff',
    new Date(0), Buffer.from('x'), new Map(), cycle, getter, symbol, hidden, arrayExtra, [, 1],
    { value: undefined }]) {
    assert.throws(() => encodeJSONValue(value), { code: 'INVALID_REQUEST' });
  }
});

test('logical decoding rejects malformed UTF-8, grammar, duplicate keys and unsupported JSON values', () => {
  for (const bytes of [Buffer.from([0xc3, 0x28]), Buffer.from([0xed, 0xa0, 0x80]),
    Buffer.from([0xff]), Buffer.from([0xf0, 0x9f])]) {
    assert.throws(() => decodeJSONValue(bytes), { code: 'INVALID_REQUEST' });
  }
  for (const text of ['', 'undefined', '{', '{} trailing', '01', '[1,]',
    '{"same":1,"same":2}', '{"same":1,"\\u0073ame":2}', '{"nested":{"x":1,"x":2}}',
     '"\\ud800"', '"\\udfff"',
    '1e400', '-1e400', '9007199254740992']) {
    assert.throws(() => decode(text), { code: 'INVALID_REQUEST' });
  }
  assert.throws(() => decodeJSONValue('{}'), { code: 'INVALID_REQUEST' });
  assert.deepEqual(decode('{"left":{"same":1},"right":{"same":2},"array":["\\\"x\\\":","猫😀"]}'),
    { left: { same: 1 }, right: { same: 2 }, array: ['"x":', '猫😀'] });
});

test('logical JSON enforces bridge depth 64 independently of envelope depth', () => {
  const nested = depth => { let value = null; while (depth--) value = [value]; return value; };
  assert.deepEqual(decodeJSONValue(encodeJSONValue(nested(64))), nested(64));
  assert.throws(() => encodeJSONValue(nested(65)), { code: 'INVALID_REQUEST' });
  assert.throws(() => decode('['.repeat(65) + 'null' + ']'.repeat(65)), { code: 'INVALID_REQUEST' });
  assert.throws(() => decode('['.repeat(500) + 'null' + ']'.repeat(500)), { code: 'INVALID_REQUEST' });
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
