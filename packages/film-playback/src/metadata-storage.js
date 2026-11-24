import { openDatabase } from '@meowfilm/storage';
import { nodeInserts } from '@meowfilm/storage/statements';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';

const MAX_CACHE_NODES = 65536;
const fault = code => Object.assign(new Error(code), { code });

/** External TMDB/Douban dynamic responses stored cleanly as bounded typed value trees. */
export function createMetadataStorage(dataRoot) {
  const database = openDatabase(dataRoot, { role: 'cache' });
  let reads = 0;
  let writes = 0;
  let invalidations = 0;

  async function read(key) {
    reads++;
    const [[entry], components, nodes] = await database.snapshot([
      {
        sql: 'SELECT parent_signature,summary_parent_signature FROM metadata_cache_entries WHERE cache_key=?',
        params: [key],
        rows: true,
      },
      {
        sql: 'SELECT component_name,status,error_code,fetched_at_ms,expires_at_ms FROM metadata_cache_components WHERE cache_key=?',
        params: [key],
        rows: true,
        maxRows: 256,
      },
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
      else throw fault('CACHE_ENTRY_CORRUPT');
    }
    let baseValue, summaryValue;
    try {
      baseValue = decodeValueNodes(baseNodes, { rawNumbers: true });
      if (baseValue === undefined) throw fault('CACHE_ENTRY_CORRUPT');
      if (summaryNodes.length) {
        summaryValue = decodeValueNodes(summaryNodes, { rawNumbers: true });
        if (summaryValue === undefined) throw fault('CACHE_ENTRY_CORRUPT');
      }
    } catch (error) {
      if (error?.code === 'CACHE_ENTRY_CORRUPT') throw error;
      throw fault('CACHE_ENTRY_CORRUPT');
    }
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
    const at = Date.now();
    const baseNodes = encodeValueNodes(state.value, { maxNodes: MAX_CACHE_NODES });
    let summaryNodes = [];
    if (state.summaryValue !== undefined) {
      const remainingNodes = MAX_CACHE_NODES - baseNodes.length;
      if (remainingNodes <= 0) throw fault('STORAGE_RESULT_LIMIT');
      summaryNodes = encodeValueNodes(state.summaryValue, { maxNodes: remainingNodes });
    }
    const statements = [
      { sql: 'DELETE FROM metadata_cache_entries WHERE cache_key=?', params: [key] },
      {
        sql: 'INSERT INTO metadata_cache_entries(' +
          'cache_key,provider,parent_signature,summary_parent_signature,updated_at_ms,estimated_bytes' +
          ') VALUES(?,?,?,?,?,?)',
        params: [
          key, request.provider,
          state.parentSignature ?? null, state.summaryParentSignature ?? null,
          at, estimatedBytes,
        ],
      },
      ...Object.entries(state.components).map(([name, row]) => ({
        sql: 'INSERT INTO metadata_cache_components(cache_key,component_name,status,error_code,fetched_at_ms,expires_at_ms) VALUES(?,?,?,?,?,?)',
        params: [key, name, row.status, row.error?.code ?? null, row.fetchedAt ?? null, row.expiresAt ?? null],
      })),
      ...nodeInserts('metadata_cache_value_nodes', ['cache_key', 'slot_name'], [key, 'base'], baseNodes),
      ...(summaryNodes.length
        ? nodeInserts('metadata_cache_value_nodes', ['cache_key', 'slot_name'], [key, 'summary'], summaryNodes)
        : []),
      {
        sql: `DELETE FROM metadata_cache_entries WHERE NOT EXISTS (
          SELECT 1 FROM metadata_cache_components c
          WHERE c.cache_key = metadata_cache_entries.cache_key
            AND (c.expires_at_ms IS NULL OR c.expires_at_ms > ?)
        )`,
        params: [at],
      },
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
    writes++;
  }

  return {
    read,
    save,
    invalidate: provider => { invalidations++; return database.execute('DELETE FROM metadata_cache_entries WHERE provider=?', [provider]); },
    stats: () => ({ reads, writes, invalidations }),
    close: () => database.close(),
  };
}
