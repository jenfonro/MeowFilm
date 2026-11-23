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
