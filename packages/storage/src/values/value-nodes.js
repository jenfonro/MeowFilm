import { isRawJSON, rawJSON } from '@meowfilm/runtime-bridge/json';
import { storageError } from '../engine/errors.js';

const MAX_DEPTH = 64;
const SAFE_MIN = -9007199254740991;
const SAFE_MAX = 9007199254740991;

function plainObject(value) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return false;
  const proto = Object.getPrototypeOf(value);
  return proto === Object.prototype || proto === null;
}

/**
 * Decomposes a dynamic protocol/KV value into typed relational tree rows
 * without serializing JSON blobs. Preserves null vs missing keys, array order,
 * -0, BigInt, and rawJSON exact decimal literals.
 */
export function encodeValueNodes(rootValue, { maxDepth = MAX_DEPTH, maxNodes = Infinity } = {}) {
  const rows = [];
  function visit(value, parentId, propKey, itemIndex, depth) {
    if (depth > maxDepth || rows.length >= maxNodes) throw storageError('STORAGE_RESULT_LIMIT');
    const nodeId = rows.length;
    if (value === null) {
      rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'null',
        boolValue: null, intValue: null, realValue: null, textValue: null });
      return;
    }
    if (typeof value === 'boolean') {
      rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'boolean',
        boolValue: value ? 1 : 0, intValue: null, realValue: null, textValue: null });
      return;
    }
    if (typeof value === 'number') {
      if (!Number.isFinite(value)) throw storageError('INVALID_REQUEST');
      if (Object.is(value, -0)) {
        rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'number_text',
          boolValue: null, intValue: null, realValue: null, textValue: '-0' });
      } else if (Number.isInteger(value) && value >= SAFE_MIN && value <= SAFE_MAX) {
        rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'integer',
          boolValue: null, intValue: value, realValue: null, textValue: null });
      } else {
        rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'real',
          boolValue: null, intValue: null, realValue: value, textValue: String(value) });
      }
      return;
    }
    if (typeof value === 'bigint') {
      rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'number_text',
        boolValue: null, intValue: null, realValue: null, textValue: value.toString(10) });
      return;
    }
    if (typeof value === 'string') {
      rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'text',
        boolValue: null, intValue: null, realValue: null, textValue: value });
      return;
    }
    if (isRawJSON(value)) {
      rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'number_text',
        boolValue: null, intValue: null, realValue: null, textValue: value.rawJSON });
      return;
    }
    if (Array.isArray(value)) {
      rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'array',
        boolValue: null, intValue: null, realValue: null, textValue: null });
      for (let index = 0; index < value.length; index++) {
        const element = value[index] === undefined ? null : value[index];
        visit(element, nodeId, null, index, depth + 1);
      }
      return;
    }
    if (plainObject(value)) {
      rows.push({ nodeId, parentId, propKey, itemIndex, kind: 'object',
        boolValue: null, intValue: null, realValue: null, textValue: null });
      for (const [key, child] of Object.entries(value)) {
        if (child === undefined) continue;
        visit(child, nodeId, key, null, depth + 1);
      }
      return;
    }
    throw storageError('INVALID_REQUEST');
  }
  visit(rootValue, null, null, null, 0);
  return rows;
}

export function decodeValueNodes(rows, { rawNumbers = false } = {}) {
  if (!Array.isArray(rows) || rows.length === 0) return undefined;
  const ordered = [...rows].sort((a, b) => (a.node_id ?? a.nodeId) - (b.node_id ?? b.nodeId));
  const children = new Map();
  let root = null;
  for (const row of ordered) {
    const nodeId = row.node_id ?? row.nodeId;
    const parentId = row.parent_id ?? row.parentId ?? null;
    if (parentId === null) {
      if (root !== null) throw storageError('STORAGE_CORRUPT');
      root = row;
    } else {
      let list = children.get(parentId);
      if (!list) { list = []; children.set(parentId, list); }
      list.push(row);
    }
    if (!Number.isSafeInteger(nodeId) || nodeId < 0) throw storageError('STORAGE_CORRUPT');
  }
  if (!root) throw storageError('STORAGE_CORRUPT');
  function build(row) {
    const nodeId = row.node_id ?? row.nodeId;
    const kind = row.kind;
    const boolValue = row.bool_value ?? row.boolValue;
    const intValue = row.int_value ?? row.intValue;
    const realValue = row.real_value ?? row.realValue;
    const textValue = row.text_value ?? row.textValue;
    switch (kind) {
      case 'null': return null;
      case 'boolean':
        if (boolValue !== 0 && boolValue !== 1) throw storageError('STORAGE_CORRUPT');
        return boolValue === 1;
      case 'integer':
        if (typeof intValue === 'bigint') return rawNumbers ? rawJSON(intValue.toString(10)) : intValue;
        if (typeof intValue !== 'number') throw storageError('STORAGE_CORRUPT');
        return intValue;
      case 'real':
        if (typeof realValue !== 'number' || !Number.isFinite(realValue)) throw storageError('STORAGE_CORRUPT');
        return realValue;
      case 'number_text':
        if (typeof textValue !== 'string') throw storageError('STORAGE_CORRUPT');
        if (textValue === '-0') return -0;
        if (rawNumbers) return rawJSON(textValue);
        {
          const parsed = Number(textValue);
          if (Number.isSafeInteger(parsed) && String(parsed) === textValue) return parsed;
          return rawJSON(textValue);
        }
      case 'text':
        if (typeof textValue !== 'string') throw storageError('STORAGE_CORRUPT');
        return textValue;
      case 'array': {
        const list = (children.get(nodeId) ?? [])
          .sort((a, b) => (a.item_index ?? a.itemIndex ?? 0) - (b.item_index ?? b.itemIndex ?? 0));
        return list.map(build);
      }
      case 'object': {
        const result = {};
        for (const child of children.get(nodeId) ?? []) {
          const key = child.prop_key ?? child.propKey;
          if (typeof key !== 'string') throw storageError('STORAGE_CORRUPT');
          Object.defineProperty(result, key, {
            value: build(child), writable: true, enumerable: true, configurable: true,
          });
        }
        return result;
      }
      default:
        throw storageError('STORAGE_CORRUPT');
    }
  }
  return build(root);
}
