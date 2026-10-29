import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeFrame, FrameDecoder } from '../src/index.js';

const envelope = value => ({ version: 1, session: 'keys', id: 1, kind: 'result', value });
function raw(value) {
  const body = Buffer.from(`{"version":1,"session":"keys","id":1,"kind":"result","value":${value}}`);
  const header = Buffer.alloc(4);
  header.writeUInt32BE(body.length);
  return Buffer.concat([header, body]);
}
function decode(bytes) {
  const values = [];
  new FrameDecoder().push(bytes, message => values.push(message.value));
  return values[0];
}

test('opaque Unicode identifiers survive in values and entry arrays without normalization', () => {
  const value = [{ key: '\u00e9', value: 1 }, { key: 'e\u0301', value: 2 }, { key: '中文😀', value: null }];
  assert.deepEqual(decode(encodeFrame(envelope(value))), value);
  assert.notEqual(value[0].key, value[1].key);
});

test('Unicode dictionary keys fail instead of merging across Swift and JS', () => {
  for (const key of ['\u00e9', 'e\u0301', '中文']) {
    assert.throws(() => encodeFrame(envelope({ [key]: 1 })), { code: 'INVALID_MESSAGE' });
  }
  for (const json of ['{"\\u00e9":1,"e\\u0301":2}', '{"中文":1}']) {
    assert.throws(() => decode(raw(json)), { code: 'INVALID_MESSAGE' });
  }
});

test('duplicate keys including escapes are rejected before JSON discards them', () => {
  for (const json of ['{"same":1,"same":2}', '{"same":1,"\\u0073ame":2}', '{"nested":{"x":1,"x":2}}']) {
    assert.throws(() => decode(raw(json)), { code: 'INVALID_MESSAGE' });
  }
  const bytes = raw('null').toString('utf8', 4).replace('"id":1', '"id":1,"id":2');
  const header = Buffer.alloc(4);
  header.writeUInt32BE(Buffer.byteLength(bytes));
  assert.throws(() => decode(Buffer.concat([header, Buffer.from(bytes)])), { code: 'INVALID_MESSAGE' });
});

test('key scanning preserves legal escapes, value punctuation and separate object scopes', () => {
  const value = { 'a"b\\c': '[{"x":"y"}]:', left: { same: 1 }, right: { same: 2 }, array: ['value:', '"next":'] };
  assert.deepEqual(decode(encodeFrame(envelope(value))), value);
  assert.deepEqual(decode(raw('{"\\u0061":1}')), { a: 1 });
});

test('wire depth fails before parsing an excessively nested JSON value', () => {
  assert.throws(() => decode(raw('['.repeat(500) + '0' + ']'.repeat(500))), { code: 'INVALID_MESSAGE' });
});
