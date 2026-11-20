import assert from 'node:assert/strict';
import { test } from 'node:test';
import { decodeValueNodes, encodeValueNodes } from '../src/value-nodes.js';

test('typed booleans require exact 0 or 1 in encoded nodes and SQL rows', () => {
  for (const value of [false, true]) {
    const [node] = encodeValueNodes(value);
    assert.equal(decodeValueNodes([node]), value);
    assert.equal(decodeValueNodes([{ node_id: 0, parent_id: null, kind: 'boolean',
      bool_value: value ? 1 : 0 }]), value);
  }
  for (const value of [null, undefined, -1, 2, '', '0', '1', false, true]) {
    assert.throws(() => decodeValueNodes([{ nodeId: 0, parentId: null, kind: 'boolean',
      boolValue: value }]), { code: 'STORAGE_CORRUPT' });
    assert.throws(() => decodeValueNodes([{ node_id: 0, parent_id: null, kind: 'boolean',
      bool_value: value }]), { code: 'STORAGE_CORRUPT' });
  }
});
