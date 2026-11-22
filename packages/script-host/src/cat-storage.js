import { openDatabase } from '@meowfilm/storage';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { reconcile } from '@meowfilm/storage/statements';
import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { fault } from './errors.js';

const nodes = ['node_id', 'parent_id', 'prop_key', 'item_index', 'kind',
  'bool_value', 'int_value', 'real_value', 'text_value'];
const relations = {
  cat_settings: [['singleton'], ['singleton', 'port']],
  cat_providers: [['provider'], ['provider', 'position', 'enabled']],
  cat_mappings: [['position'], ['position', 'provider']],
  cat_mapping_aliases: [['mapping_position', 'position'], ['mapping_position', 'position', 'alias']],
  cat_installations: [['mode', 'installation_id'], ['mode', 'installation_id', 'position',
    'name', 'source_type', 'source_url', 'local_path', 'local_hash', 'enabled', 'input_revision',
    'source_hash', 'version', 'loaded_identity', 'imported_at_ms', 'updated_at_ms', 'update_state', 'update_error']],
  cat_installation_config_nodes: [['mode', 'installation_id', 'node_id'], ['mode', 'installation_id', ...nodes]],
  cat_installation_sites: [['mode', 'installation_id', 'site_key'],
    ['mode', 'installation_id', 'position', 'site_key', 'site_name', 'site_api', 'discovery']],
  cat_site_actions: [['mode', 'installation_id', 'site_key', 'position'],
    ['mode', 'installation_id', 'site_key', 'position', 'action']],
  cat_site_methods: [['mode', 'installation_id', 'site_key', 'method_key'],
    ['mode', 'installation_id', 'site_key', 'method_key', 'method_name']],
};
const empty = () => ({ version: 1, port: 0, providers: {}, mappings: [], scripts: [] });
const columns = ['name', 'sourceType', 'sourceUrl', 'localPath', 'localHash', 'enabled',
  'inputRevision', 'sourceHash', 'version', 'loadedIdentity', 'importedAt', 'updatedAt',
  'updateState', 'updateError'];

export function catConfig(script) {
  if (script.config !== undefined) return script.config;
  if (!script.configNodes) return {};
  try {
    const value = decodeValueNodes(script.configNodes, { rawNumbers: true });
    if (value === undefined) throw fault('CORRUPT_STORAGE');
    return value;
  } catch { throw fault('CORRUPT_STORAGE'); }
}

export function catConfigNodes(script) {
  if (script.config === undefined && script.configNodes) return script.configNodes;
  return encodeValueNodes(script.config ?? {}).map(node => ({
    node_id: node.nodeId, parent_id: node.parentId, prop_key: node.propKey, item_index: node.itemIndex,
    kind: node.kind, bool_value: node.boolValue, int_value: node.intValue,
    real_value: node.realValue, text_value: node.textValue,
  }));
}

function project(value, mode) {
  const rows = Object.fromEntries(Object.keys(relations).map(table => [table, []]));
  if (mode === 'standalone') {
    rows.cat_settings.push([1, value.port]);
    for (const [index, [provider, enabled]] of Object.entries(value.providers).entries())
      rows.cat_providers.push([provider, index, Number(enabled)]);
    for (const [index, mapping] of value.mappings.entries()) {
      rows.cat_mappings.push([index, mapping.provider]);
      for (const [position, alias] of mapping.aliases.entries())
        rows.cat_mapping_aliases.push([index, position, alias]);
    }
  }
  for (const [position, script] of value.scripts.entries()) {
    const id = script.installationId;
    rows.cat_installations.push([mode, id, position, ...columns.map((field, index) => {
      if (mode === 'native' && index < 7) return null;
      const value = script[field] ?? (field === 'version' ? '0.0.0' : field === 'updateState' ? 'idle' : null);
      return field === 'enabled' && value !== null ? Number(value) : value;
    })]);
    for (const node of catConfigNodes(script))
      rows.cat_installation_config_nodes.push([mode, id, ...nodes.map(field => node[field])]);
    for (const [index, site] of (script.sites ?? []).entries()) {
      rows.cat_installation_sites.push([mode, id, index, site.key, site.name, site.api, site.discovery ?? 'opaque-http']);
      for (const [ordinal, action] of (site.actions ?? []).entries())
        rows.cat_site_actions.push([mode, id, site.key, ordinal, action]);
      for (const [key, method] of Object.entries(site.methods ?? {}))
        rows.cat_site_methods.push([mode, id, site.key, key, method]);
    }
  }
  return rows;
}

/** Runtime installation facts, not a second copy of native source intent. */
export class CatStorage {
  constructor(root, mode) {
    this.db = openDatabase(root);
    this.mode = mode;
    this.owner = `cat.${mode}`;
    this.revision = 1;
    this.rows = Object.fromEntries(Object.keys(relations).map(table => [table, []]));
    this.value = empty();
    this.tail = Promise.resolve();
    this.pending = 0;
    this.ready = this.reload();
    this.ready.catch(() => {});
  }
  async _reload() {
    await this.db.ready;
    await this.db.execute('INSERT INTO owner_revisions(owner,revision,receipt) VALUES(?,1,?) ON CONFLICT DO NOTHING', [this.owner, 'initial']);
    const tables = Object.keys(relations).filter(table => this.mode === 'standalone' || table.includes('installation') ||
      table.startsWith('cat_site_'));
    const results = await this.db.snapshot([
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [this.owner], rows: true },
      ...tables.map(table => ({ sql: `SELECT * FROM ${table}` + (relations[table][1][0] === 'mode' ? ' WHERE mode=?' : ''),
        params: relations[table][1][0] === 'mode' ? [this.mode] : [], rows: true, maxRows: 65536 })),
    ]);
    this.revision = results[0][0].revision;
    const records = Object.fromEntries(tables.map((table, index) => [table, results[index + 1]]));
    for (const table of tables) this.rows[table] = records[table].map(row => relations[table][1].map(field => row[field]));
    const value = empty();
    if (this.mode === 'standalone') {
      value.port = records.cat_settings[0]?.port ?? 0;
      value.providers = Object.fromEntries(records.cat_providers.sort((a, b) => a.position - b.position)
        .map(row => [row.provider, Boolean(row.enabled)]));
      value.mappings = records.cat_mappings.sort((a, b) => a.position - b.position).map(row => ({
        provider: row.provider,
        aliases: records.cat_mapping_aliases.filter(alias => alias.mapping_position === row.position)
          .sort((a, b) => a.position - b.position).map(alias => alias.alias),
      }));
    }
    for (const row of records.cat_installations.sort((a, b) => a.position - b.position)) {
      const id = row.installation_id, script = { installationId: id, sites: [] };
      const fields = relations.cat_installations[1].slice(3);
      for (const [index, field] of fields.entries())
        if ((this.mode !== 'native' || index >= 7) &&
          (this.mode !== 'standalone' || !['localPath', 'localHash', 'inputRevision', 'loadedIdentity'].includes(columns[index])))
          script[columns[index]] = field === 'enabled' ? Boolean(row[field]) : row[field];
      script.configNodes = records.cat_installation_config_nodes.filter(node => node.installation_id === id)
        .map(node => Object.fromEntries(nodes.map(field => [field, node[field]])));
      for (const site of records.cat_installation_sites.filter(site => site.installation_id === id)
        .sort((a, b) => a.position - b.position)) {
        const match = member => member.installation_id === id && member.site_key === site.site_key;
        script.sites.push({ key: site.site_key, name: site.site_name, api: site.site_api, discovery: site.discovery,
          actions: records.cat_site_actions.filter(match).sort((a, b) => a.position - b.position).map(row => row.action),
          methods: Object.fromEntries(records.cat_site_methods.filter(match).map(row => [row.method_key, row.method_name])),
        });
      }
      value.scripts.push(script);
    }
    this.value = value;
    return value;
  }
  reload() {
    const work = this.tail.then(() => this._reload());
    this.tail = work.catch(() => {});
    return work;
  }
  save(change) {
    if (this.pending >= 64) return Promise.reject(fault('RESOURCE_LIMIT'));
    ++this.pending;
    const work = this.tail.then(async () => {
      await this.ready;
      for (;;) {
        const value = change(cloneJSONValue(this.value)), next = project(value, this.mode), statements = [];
        for (const [table, [keys, fields]] of Object.entries(relations)) {
          if (this.mode !== 'standalone' && !table.includes('installation') && !table.startsWith('cat_site_')) continue;
          statements.push(...reconcile(table, fields, keys, this.rows[table], next[table]));
        }
        if (!statements.length) return this.value;
        statements.unshift({ sql: 'UPDATE owner_revisions SET revision=revision+1 WHERE owner=? AND revision=?',
          params: [this.owner, this.revision], expectChanges: 1 });
        try { await this.db.transaction(statements); }
        catch (error) {
          if (error.code !== 'STORAGE_CONFLICT') throw fault('STORAGE_ERROR');
          const revision = this.revision;
          await this._reload();
          if (this.revision === revision) throw fault('STORAGE_CONFLICT');
          continue;
        }
        ++this.revision;
        this.rows = next;
        this.value = { ...value, scripts: value.scripts.map(script => {
          const { config, configNodes, ...fields } = script;
          return { ...fields, configNodes: catConfigNodes(script) };
        }) };
        return this.value;
      }
    }).finally(() => { --this.pending; });
    this.tail = work.catch(() => {});
    return work;
  }
  async close() { await this.tail; await this.db.close(); }
}
