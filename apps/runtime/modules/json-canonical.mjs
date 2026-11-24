import { stringifyJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';

const isPlainObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);

/** Stable canonical JSON string where object key ordering is normalized. */
export function canonicalJSON(value, depth = -Infinity) {
  function ordered(item) {
    if (isRawJSON(item)) return item;
    if (Array.isArray(item)) return item.map(ordered);
    return isPlainObject(item)
      ? Object.fromEntries(Object.keys(item).sort().map(key => [key, ordered(item[key])]))
      : item;
  }
  return stringifyJSONValue(ordered(value), depth);
}
