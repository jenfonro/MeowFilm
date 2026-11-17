import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeJSONValue, decodeJSONValue } from '../src/index.js';
const raw = text => Buffer.from(text);
const decode = bytes => decodeJSONValue(bytes);

test('opaque Unicode identifiers survive in values and entry arrays without normalization', () => {
  const value = [{ key: '\u00e9', value: 1 }, { key: 'e\u0301', value: 2 }, { key: '中文😀', value: null }];
  assert.deepEqual(decode(encodeJSONValue(value)), value);
  assert.notEqual(value[0].key, value[1].key);
});

test('Unicode dictionary keys preserve canonically equivalent code point sequences', () => {
  const value = { '\u00e9': 1, 'e\u0301': 2, '中文': 3 };
  assert.deepEqual(decode(encodeJSONValue(value)), value);
  assert.deepEqual(Object.keys(decode(raw('{"\\u00e9":1,"e\\u0301":2}'))), ['é', 'e\u0301']);
});

test('duplicate keys use the JSON parser semantics without an independent schema gate', () => {
  assert.deepEqual(decode(raw('{"same":1,"\\u0073ame":2}')), { same: 2 });
  assert.deepEqual(decode(raw('{"nested":{"x":1,"x":2}}')), { nested: { x: 2 } });
});

test('key scanning preserves legal escapes, value punctuation and separate object scopes', () => {
  const value = { 'a"b\\c': '[{"x":"y"}]:', left: { same: 1 }, right: { same: 2 }, array: ['value:', '"next":'] };
  assert.deepEqual(decode(encodeJSONValue(value)), value);
  assert.deepEqual(decode(raw('{"\\u0061":1}')), { a: 1 });
});

test('wire depth fails before parsing an excessively nested JSON value', () => {
  assert.throws(() => decode(raw('['.repeat(500) + '0' + ']'.repeat(500))), { code: 'INVALID_REQUEST' });
});
