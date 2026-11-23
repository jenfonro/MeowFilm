import { isDeepStrictEqual } from 'node:util';
import { openDatabase } from '@meowfilm/storage';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { upsert, nodeInserts } from '@meowfilm/storage/statements';
import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { fault } from './errors.js';

const nodeCols = ['node_id', 'parent_id', 'prop_key', 'item_index', 'kind',
  'bool_value', 'int_value', 'real_value', 'text_value'];
const installColumns = ['mode', 'installation_id', 'position',
  'name', 'source_type', 'source_url', 'local_path', 'local_hash', 'enabled', 'input_revision',
  'source_hash', 'version', 'loaded_identity', 'imported_at_ms', 'updated_at_ms', 'update_state', 'update_error'];
const scriptFields = ['name', 'sourceType', 'sourceUrl', 'localPath', 'localHash', 'enabled',
  'inputRevision', 'sourceHash', 'version', 'loadedIdentity', 'importedAt', 'updatedAt',
  'updateState', 'updateError'];
const empty = () => ({ version: 1, port: 0, providers: {}, mappings: [], scripts: [] });

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

function installationRow(mode, script, position) {
  return [mode, script.installationId, position, ...scriptFields.map((field, index) => {
    if (mode === 'native' && index < 7) return null;
    const value = script[field] ?? (field === 'version' ? '0.0.0' : field === 'updateState' ? 'idle' : null);
    return field === 'enabled' && value !== null ? Number(value) : value;
  })];
}

function scriptHeaderEqual(mode, a, b) {
  return scriptFields.every((field, index) => {
    if (mode === 'native' && index < 7) return true;
    return (a[field] ?? null) === (b[field] ?? null);
  });
}

function buildStatements(mode, previous, next) {
  const statements = [];
  if (mode === 'standalone') {
    if (previous.port !== next.port) {
      statements.push(upsert('cat_settings', ['singleton', 'port'], [1, next.port], ['singleton']));
    }
    if (!isDeepStrictEqual(previous.providers, next.providers)) {
      const oldProviders = Object.entries(previous.providers);
      const nextEntries = Object.entries(next.providers);
      const nextKeys = new Set(nextEntries.map(([k]) => k));
      for (const [provider] of oldProviders) {
        if (!nextKeys.has(provider)) {
          statements.push({ sql: 'DELETE FROM cat_providers WHERE provider=?', params: [provider] });
        }
      }
      for (let i = 0; i < nextEntries.length; i++) {
        const [provider, enabled] = nextEntries[i];
        statements.push(upsert('cat_providers', ['provider', 'position', 'enabled'], [provider, i, Number(enabled)], ['provider']));
      }
    }
    if (!isDeepStrictEqual(previous.mappings, next.mappings)) {
      if (next.mappings.length < previous.mappings.length) {
        statements.push({ sql: 'DELETE FROM cat_mappings WHERE position>=?', params: [next.mappings.length] });
      }
      for (let i = 0; i < next.mappings.length; i++) {
        const mapping = next.mappings[i];
        const old = previous.mappings[i];
        if (old && isDeepStrictEqual(old, mapping)) continue;
        statements.push(upsert('cat_mappings', ['position', 'provider'], [i, mapping.provider], ['position']));
        if (old) statements.push({ sql: 'DELETE FROM cat_mapping_aliases WHERE mapping_position=?', params: [i] });
        for (let a = 0; a < mapping.aliases.length; a++) {
          statements.push({
            sql: 'INSERT INTO cat_mapping_aliases(mapping_position,position,alias) VALUES(?,?,?)',
            params: [i, a, mapping.aliases[a]],
          });
        }
      }
    }
  }

  const oldById = new Map(previous.scripts.map((s, idx) => [s.installationId, { script: s, idx }]));
  const nextIds = new Set(next.scripts.map(s => s.installationId));
  for (const id of oldById.keys()) {
    if (!nextIds.has(id)) {
      statements.push({ sql: 'DELETE FROM cat_installations WHERE mode=? AND installation_id=?', params: [mode, id] });
    }
  }

  for (let i = 0; i < next.scripts.length; i++) {
    const script = next.scripts[i];
    const id = script.installationId;
    const old = oldById.get(id);
    if (!old || old.idx !== i || !scriptHeaderEqual(mode, old.script, script)) {
      statements.push(upsert('cat_installations', installColumns, installationRow(mode, script, i), ['mode', 'installation_id']));
    }
    const nextNodes = catConfigNodes(script);
    if (!old || !isDeepStrictEqual(old.script.configNodes, nextNodes)) {
      statements.push({ sql: 'DELETE FROM cat_installation_config_nodes WHERE mode=? AND installation_id=?', params: [mode, id] });
      const encoded = nextNodes.map(n => ({
        nodeId: n.node_id, parentId: n.parent_id, propKey: n.prop_key, itemIndex: n.item_index,
        kind: n.kind, boolValue: n.bool_value, intValue: n.int_value, realValue: n.real_value, textValue: n.text_value,
      }));
      statements.push(...nodeInserts('cat_installation_config_nodes', ['mode', 'installation_id'], [mode, id], encoded));
    }
    if (!old || !isDeepStrictEqual(old.script.sites ?? [], script.sites ?? [])) {
      statements.push({ sql: 'DELETE FROM cat_installation_sites WHERE mode=? AND installation_id=?', params: [mode, id] });
      for (const [sIdx, site] of (script.sites ?? []).entries()) {
        statements.push({
          sql: 'INSERT INTO cat_installation_sites(mode,installation_id,position,site_key,site_name,site_api,discovery) VALUES(?,?,?,?,?,?,?)',
          params: [mode, id, sIdx, site.key, site.name, site.api, site.discovery ?? 'opaque-http'],
        });
        for (const [aIdx, action] of (site.actions ?? []).entries()) {
          statements.push({
            sql: 'INSERT INTO cat_site_actions(mode,installation_id,site_key,position,action) VALUES(?,?,?,?,?)',
            params: [mode, id, site.key, aIdx, action],
          });
        }
        for (const [mKey, mName] of Object.entries(site.methods ?? {})) {
          statements.push({
            sql: 'INSERT INTO cat_site_methods(mode,installation_id,site_key,method_key,method_name) VALUES(?,?,?,?,?)',
            params: [mode, id, site.key, mKey, mName],
          });
        }
      }
    }
  }
  return statements;
}

/** Runtime installation facts, not a second copy of native source intent. */
export class CatStorage {
  constructor(root, mode) {
    this.db = openDatabase(root);
    this.mode = mode;
    this.owner = `cat.${mode}`;
    this.revision = 1;
    this.value = empty();
    this.tail = Promise.resolve();
    this.pending = 0;
    this.ready = this.reload();
    this.ready.catch(() => {});
  }
  async _reload() {
    await this.db.ready;
    await this.db.execute('INSERT INTO owner_revisions(owner,revision) VALUES(?,1) ON CONFLICT DO NOTHING', [this.owner]);
    const queries = [
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [this.owner], rows: true },
      { sql: `SELECT ${installColumns.join(',')} FROM cat_installations WHERE mode=? ORDER BY position`, params: [this.mode], rows: true, maxRows: 65536 },
      { sql: `SELECT installation_id,${nodeCols.join(',')} FROM cat_installation_config_nodes WHERE mode=? ORDER BY installation_id,node_id`, params: [this.mode], rows: true, maxRows: 65536 },
      { sql: 'SELECT installation_id,position,site_key,site_name,site_api,discovery FROM cat_installation_sites WHERE mode=? ORDER BY installation_id,position', params: [this.mode], rows: true, maxRows: 65536 },
      { sql: 'SELECT installation_id,site_key,position,action FROM cat_site_actions WHERE mode=? ORDER BY installation_id,site_key,position', params: [this.mode], rows: true, maxRows: 65536 },
      { sql: 'SELECT installation_id,site_key,method_key,method_name FROM cat_site_methods WHERE mode=?', params: [this.mode], rows: true, maxRows: 65536 },
    ];
    if (this.mode === 'standalone') {
      queries.push(
        { sql: 'SELECT port FROM cat_settings WHERE singleton=1', rows: true },
        { sql: 'SELECT provider,enabled FROM cat_providers ORDER BY position', rows: true },
        { sql: 'SELECT position,provider FROM cat_mappings ORDER BY position', rows: true },
        { sql: 'SELECT mapping_position,alias FROM cat_mapping_aliases ORDER BY mapping_position,position', rows: true },
      );
    }
    const results = await this.db.snapshot(queries);
    this.revision = results[0][0].revision;
    const [, installations, configNodes, sites, actions, methods] = results;
    const value = empty();
    if (this.mode === 'standalone') {
      const [settings, providers, mappings, aliases] = results.slice(6);
      value.port = settings[0]?.port ?? 0;
      value.providers = Object.fromEntries(providers.map(row => [row.provider, Boolean(row.enabled)]));
      value.mappings = mappings.map(row => ({
        provider: row.provider,
        aliases: aliases.filter(alias => alias.mapping_position === row.position).map(alias => alias.alias),
      }));
    }
    for (const row of installations) {
      const id = row.installation_id, script = { installationId: id, sites: [] };
      const fields = installColumns.slice(3);
      for (const [index, field] of fields.entries())
        if ((this.mode !== 'native' || index >= 7) &&
          (this.mode !== 'standalone' || !['localPath', 'localHash', 'inputRevision', 'loadedIdentity'].includes(scriptFields[index])))
          script[scriptFields[index]] = field === 'enabled' ? Boolean(row[field]) : row[field];
      script.configNodes = configNodes.filter(node => node.installation_id === id)
        .map(node => Object.fromEntries(nodeCols.map(field => [field, node[field]])));
      for (const site of sites.filter(site => site.installation_id === id)) {
        const match = member => member.installation_id === id && member.site_key === site.site_key;
        script.sites.push({
          key: site.site_key, name: site.site_name, api: site.site_api, discovery: site.discovery,
          actions: actions.filter(match).map(row => row.action),
          methods: Object.fromEntries(methods.filter(match).map(row => [row.method_key, row.method_name])),
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
        const value = change(cloneJSONValue(this.value));
        const statements = buildStatements(this.mode, this.value, value);
        if (!statements.length) return this.value;
        statements.unshift({
          sql: 'UPDATE owner_revisions SET revision=revision+1 WHERE owner=? AND revision=?',
          params: [this.owner, this.revision],
          expectChanges: 1,
        });
        try { await this.db.transaction(statements); }
        catch (error) {
          if (error.code !== 'STORAGE_CONFLICT') throw fault('STORAGE_ERROR');
          const revision = this.revision;
          await this._reload();
          if (this.revision === revision) throw fault('STORAGE_CONFLICT');
          continue;
        }
        ++this.revision;
        this.value = {
          ...value,
          scripts: value.scripts.map(script => {
            const { config, configNodes, ...fields } = script;
            return { ...fields, configNodes: catConfigNodes(script) };
          }),
        };
        return this.value;
      }
    }).finally(() => { --this.pending; });
    this.tail = work.catch(() => {});
    return work;
  }
  async close() { await this.tail; await this.db.close(); }
}
