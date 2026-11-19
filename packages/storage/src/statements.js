/** Repositories supply fixed identifiers; public requests never select tables or columns. */
export function upsert(table, columns, values, keys) {
  const changed = columns.filter(column => !keys.includes(column));
  return {
    sql: `INSERT INTO ${table}(${columns.join(',')}) VALUES(${columns.map(() => '?').join(',')}) ` +
      `ON CONFLICT(${keys.join(',')}) ${changed.length ? 'DO UPDATE SET ' +
        changed.map(column => `${column}=excluded.${column}`).join(',') + ' WHERE ' +
        changed.map(column => `${table}.${column} IS NOT excluded.${column}`).join(' OR ') : 'DO NOTHING'}`,
    params: values,
  };
}

export function nodeInserts(table, scopeColumns, scopeValues, nodes) {
  const columns = [...scopeColumns, 'node_id', 'parent_id', 'prop_key', 'item_index', 'kind',
    'bool_value', 'int_value', 'real_value', 'text_value'];
  return nodes.map(row => ({
    sql: `INSERT INTO ${table}(${columns.join(',')}) VALUES(${columns.map(() => '?').join(',')})`,
    params: [...scopeValues, row.nodeId, row.parentId, row.propKey, row.itemIndex, row.kind,
      row.boolValue, row.intValue, row.realValue, row.textValue],
  }));
}

/** Reconcile a typed relation without rewriting unchanged members. */
export function reconcile(table, columns, keys, previous, proposed) {
  const offsets = keys.map(key => columns.indexOf(key));
  const identity = row => JSON.stringify(offsets.map(offset => row[offset]));
  const remaining = new Map(previous.map(row => [identity(row), row]));
  const seen = new Set();
  const writes = [], deletes = [];
  for (const row of proposed) {
    const id = identity(row), old = remaining.get(id);
    if (seen.has(id)) throw Object.assign(new Error('INVALID_REQUEST'), {code:'INVALID_REQUEST'});
    seen.add(id);
    remaining.delete(id);
    if (!old || row.some((value, index) => !Object.is(value, old[index])))
      writes.push(upsert(table, columns, row, keys));
  }
  for (const row of remaining.values()) deletes.push({
    sql: `DELETE FROM ${table} WHERE ${keys.map(key => key + '=?').join(' AND ')}`,
    params: offsets.map(offset => row[offset]),
  });
  return [...deletes, ...writes];
}
