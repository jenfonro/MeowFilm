import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { nodeInserts } from '@meowfilm/storage/statements';

// JS strings can contain isolated UTF-16 code units, including in map keys.
const units = text => Buffer.from(text, 'utf16le');
const text = bytes => Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString('utf16le');
const corrupt = () => Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });

export async function readReaderValues(database, id) {
  const [state, nodes] = await database.snapshot([
    { sql: 'SELECT revision,variable FROM source_reader_state WHERE installation_id=?', params: [id], rows: true },
    { sql: 'SELECT entry_key,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value FROM source_reader_value_nodes WHERE installation_id=? ORDER BY entry_key,node_id',
      params: [id], rows: true, maxRows: Number.MAX_SAFE_INTEGER },
  ]);
  const groups = new Map();
  for (const row of nodes) {
    const key = text(row.entry_key);
    let group = groups.get(key);
    if (!group) groups.set(key, group = []);
    group.push({ ...row, prop_key: row.prop_key === null ? null : text(row.prop_key),
      text_value: row.kind === 'text' ? text(row.text_value) : row.text_value });
  }
  if (!state.length && nodes.length) throw corrupt();
  return { revision: state[0]?.revision ?? 0,
    ...(state[0]?.variable != null ? { variable: text(state[0].variable) } : {}),
    entries: Array.from(groups, ([key, rows]) => [key, decodeValueNodes(rows, { rawNumbers: true })]) };
}

export async function applyReaderValues(database, id, patch, owner, ownerRevision, signal) {
  const { revision, variable, entries } = patch;
  const changes = [{
    sql: `UPDATE source_reader_state SET revision=revision+1${variable === undefined ? '' : ',variable=?'}
      WHERE installation_id=? AND revision=? AND EXISTS (SELECT 1 FROM owner_revisions WHERE owner=? AND revision=?)`,
    params: [...(variable === undefined ? [] : [units(variable)]), id, revision, owner, ownerRevision], expectChanges: 1,
  }];
  for (const [key, value] of entries) {
    const scope = [id, units(key)];
    changes.push({ sql: 'DELETE FROM source_reader_value_nodes WHERE installation_id=? AND entry_key=?', params: scope });
    const nodes = encodeValueNodes(value);
    for (const node of nodes) {
      if (node.propKey !== null) node.propKey = units(node.propKey);
      if (node.kind === 'text') node.textValue = units(node.textValue);
      if (node.kind === 'real') node.textValue = null;
    }
    changes.push(...nodeInserts('source_reader_value_nodes', ['installation_id', 'entry_key'], scope, nodes));
  }
  await database.transaction(changes, { signal });
  return revision + 1;
}
