import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeJSONValue, decodeJSONValue, encodeFrame, FrameDecoder, MAX_FRAME_BYTES } from '../src/index.js';

const wire = value => ({ version: 1, session: 'json-value', id: 1, kind: 'result', value });
const decode = text => decodeJSONValue(Buffer.from(text));

test('logical JSON values preserve bridge types and Unicode without a total byte gate', () => {
  for (const value of [null, false, true, 0, -1, 1.25, Number.MAX_SAFE_INTEGER,
    '猫😀e\u0301"\\', [], {}, { array: [null, false, 42, { text: 'a' }] }]) {
    const bytes = encodeJSONValue(value);
    assert.ok(Buffer.isBuffer(bytes));
    assert.deepEqual(decodeJSONValue(bytes), value);
    let message;
    new FrameDecoder().push(encodeFrame(wire(value)), decoded => { message = decoded; });
    assert.deepEqual(message.value, decodeJSONValue(bytes));
  }
  for (const size of [512 * 1024, MAX_FRAME_BYTES + 1, 8 * MAX_FRAME_BYTES + 1]) {
    const value = 'x'.repeat(size - 2);
    const bytes = encodeJSONValue(value);
    assert.equal(bytes.length, size);
    assert.equal(decodeJSONValue(bytes), value);
  }
  assert.throws(() => encodeFrame(wire('x'.repeat(MAX_FRAME_BYTES))), { code: 'LIMIT' });
});

test('logical encoding uses the bridge strict JSON semantics', () => {
  const cycle = {}; cycle.self = cycle;
  const getter = Object.defineProperty({}, 'value', { enumerable: true, get() { throw new Error('do not execute'); } });
  const symbol = { [Symbol('private')]: 1 };
  const hidden = Object.defineProperty({}, 'value', { value: 1 });
  const arrayExtra = []; arrayExtra.extra = 1;
  for (const value of [undefined, () => {}, Symbol('value'), 1n, NaN, Infinity, -Infinity,
    Number.MAX_SAFE_INTEGER + 1, '\ud800', '\udfff', { '\u00e9': 1 },
    new Date(0), Buffer.from('x'), new Map(), cycle, getter, symbol, hidden, arrayExtra, [, 1],
    { value: undefined }]) {
    assert.throws(() => encodeJSONValue(value), { code: 'INVALID_MESSAGE' });
    assert.throws(() => encodeFrame(wire(value)), { code: 'INVALID_MESSAGE' });
  }
});

test('logical decoding rejects malformed UTF-8, grammar, duplicate keys and unsupported JSON values', () => {
  for (const bytes of [Buffer.from([0xc3, 0x28]), Buffer.from([0xed, 0xa0, 0x80]),
    Buffer.from([0xff]), Buffer.from([0xf0, 0x9f])]) {
    assert.throws(() => decodeJSONValue(bytes), { code: 'INVALID_MESSAGE' });
  }
  for (const text of ['', 'undefined', '{', '{} trailing', '01', '[1,]',
    '{"same":1,"same":2}', '{"same":1,"\\u0073ame":2}', '{"nested":{"x":1,"x":2}}',
    '{"é":1}', '{"\\u00e9":1}', '"\\ud800"', '"\\udfff"',
    '1e400', '-1e400', '9007199254740992']) {
    assert.throws(() => decode(text), { code: 'INVALID_MESSAGE' });
  }
  assert.throws(() => decodeJSONValue('{}'), { code: 'INVALID_MESSAGE' });
  assert.deepEqual(decode('{"left":{"same":1},"right":{"same":2},"array":["\\\"x\\\":","猫😀"]}'),
    { left: { same: 1 }, right: { same: 2 }, array: ['"x":', '猫😀'] });
});

test('logical JSON enforces bridge depth 64 while leaving frame validation unchanged', () => {
  const nested = depth => { let value = null; while (depth--) value = [value]; return value; };
  assert.deepEqual(decodeJSONValue(encodeJSONValue(nested(64))), nested(64));
  assert.throws(() => encodeJSONValue(nested(65)), { code: 'INVALID_MESSAGE' });
  assert.throws(() => decode('['.repeat(65) + 'null' + ']'.repeat(65)), { code: 'INVALID_MESSAGE' });
  assert.throws(() => decode('['.repeat(500) + 'null' + ']'.repeat(500)), { code: 'INVALID_MESSAGE' });
});
