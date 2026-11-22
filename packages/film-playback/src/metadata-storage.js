import { openDatabase } from '@meowfilm/storage';
import { nodeInserts } from '@meowfilm/storage/statements';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { metadataKind } from './metadata-policy.js';

const MAX_CACHE_NODES = 65536;
const fault = code => Object.assign(new Error(code), { code });

/** External TMDB/Douban dynamic responses stored cleanly as bounded typed value trees. */
export function createMetadataStorage(dataRoot, now) {
  const database = openDatabase(dataRoot);

  async function read(key) {
    const [[entry], components, nodes] = await database.snapshot([
      { sql: 'SELECT * FROM metadata_cache_entries WHERE cache_key=?', params: [key], rows: true },
      { sql: 'SELECT * FROM metadata_cache_components WHERE cache_key=?', params: [key], rows: true, maxRows: 256 },
      {
        sql: 'SELECT slot_name,node_id,parent_id,prop_key,item_index,kind,bool_value,int_value,real_value,text_value ' +
          'FROM metadata_cache_value_nodes WHERE cache_key=? ORDER BY slot_name,node_id',
        params: [key],
        rows: true,
        maxRows: MAX_CACHE_NODES,
      },
    ]);
    if (!entry) return undefined;
    const baseNodes = [];
    const summaryNodes = [];
    for (const row of nodes) {
      if (row.slot_name === 'base') baseNodes.push(row);
      else if (row.slot_name === 'summary') summaryNodes.push(row);
      else throw fault('STORAGE_CORRUPT');
    }
    const baseValue = decodeValueNodes(baseNodes, { rawNumbers: true });
    if (baseValue === undefined) throw fault('STORAGE_CORRUPT');
    const summaryValue = summaryNodes.length ? decodeValueNodes(summaryNodes, { rawNumbers: true }) : undefined;
    return {
      value: baseValue,
      ...(summaryValue === undefined ? {} : { summaryValue }),
      components: Object.fromEntries(components.map(row => [row.component_name, {
        status: row.status,
        ...(row.error_code === null ? {} : { error: { code: row.error_code } }),
        ...(row.fetched_at_ms === null ? {} : { fetchedAt: row.fetched_at_ms }),
        ...(row.expires_at_ms === null ? {} : { expiresAt: row.expires_at_ms }),
      }])),
      ...(entry.parent_signature === null ? {} : { parentSignature: entry.parent_signature }),
      ...(entry.summary_parent_signature === null ? {} : { summaryParentSignature: entry.summary_parent_signature }),
    };
  }

  async function save(key, state, estimatedBytes) {
    const request = state.request;
    const expires = Math.max(0, ...Object.values(state.components).map(row => row.expiresAt ?? 0));
    const baseNodes = encodeValueNodes(state.value, { maxNodes: MAX_CACHE_NODES });
    const summaryNodes = state.summaryValue === undefined
      ? []
      : encodeValueNodes(state.summaryValue, { maxNodes: MAX_CACHE_NODES - baseNodes.length });
    const statements = [
      { sql: 'DELETE FROM metadata_cache_entries WHERE cache_key=?', params: [key] },
      {
        sql: 'INSERT INTO metadata_cache_entries VALUES(?,?,?,?,?,?,?,?,?,?,?)',
        params: [
          key, request.provider, request.path, metadataKind(request),
          request.settings.revision, request.imageSize ?? null,
          state.parentSignature ?? null, state.summaryParentSignature ?? null,
          now(), expires, estimatedBytes,
        ],
      },
      ...Object.entries(state.components).map(([name, row]) => ({
        sql: 'INSERT INTO metadata_cache_components VALUES(?,?,?,?,?,?)',
        params: [key, name, row.status, row.error?.code ?? null, row.fetchedAt ?? null, row.expiresAt ?? null],
      })),
      ...nodeInserts('metadata_cache_value_nodes', ['cache_key', 'slot_name'], [key, 'base'], baseNodes),
      ...(summaryNodes.length
        ? nodeInserts('metadata_cache_value_nodes', ['cache_key', 'slot_name'], [key, 'summary'], summaryNodes)
        : []),
      {
        sql: `DELETE FROM metadata_cache_entries WHERE cache_key IN (
          SELECT cache_key FROM (
            SELECT cache_key, row_number() OVER (ORDER BY updated_at_ms DESC, cache_key) AS rank_pos,
              sum(estimated_bytes) OVER (ORDER BY updated_at_ms DESC, cache_key) AS retained
            FROM metadata_cache_entries
          ) WHERE rank_pos > 4096 OR retained > 134217728
        )`,
      },
    ];
    await database.transaction(statements);
  }

  return {
    ready: database.ready,
    read,
    save,
    remove: key => database.execute('DELETE FROM metadata_cache_entries WHERE cache_key=?', [key]),
    invalidate: provider => database.execute('DELETE FROM metadata_cache_entries WHERE provider=?', [provider]),
    close: () => database.close(),
  };
}
