import { openDatabase } from '@meowfilm/storage';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { reconcile } from '@meowfilm/storage/statements';
import { parseJSONText, stringifyJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';
import { isDeepStrictEqual } from 'node:util';
import { readReaderValues, applyReaderValues } from './source-reader-values.mjs';

const texts = ['bookSourceName', 'bookSourceUrl', 'bookSourceGroup', 'bookSourceComment',
  'bookUrlPattern', 'searchUrl', 'exploreUrl', 'loginUrl', 'loginCheckJs', 'jsLib',
  'coverDecodeJs', 'concurrentRate', 'header', 'html'];
const numbers = ['bookSourceType', 'weight', 'respondTime', 'lastUpdateTime'];
const rules = ['ruleSearch', 'ruleExplore', 'ruleBookInfo', 'ruleToc', 'ruleContent'];
const ruleFields = ['bookList', 'name', 'author', 'kind', 'wordCount', 'lastChapter',
  'intro', 'coverUrl', 'bookUrl', 'tocUrl', 'init', 'chapterList', 'chapterName',
  'chapterUrl', 'nextTocUrl', 'isReverse', 'reverseToc', 'updateTime', 'preUpdateJs',
  'content', 'nextContentUrl', 'title', 'replaceRegex', 'sourceRegex', 'webJs',
  'payAction', 'imageStyle', 'imageDecode', 'imageDecrypt'];
const nodeColumns = ['node_id', 'parent_id', 'prop_key', 'item_index', 'kind',
  'bool_value', 'int_value', 'real_value', 'text_value'];
const relations = {
  source_installations: [['installation_id'], ['installation_id', 'position', 'revision',
    'name', 'source_url', 'source_group', 'sort_order', 'enabled', 'enabled_explore',
    'protocol', 'definition_kind', 'raw_html']],
  source_installation_actions: [['installation_id', 'position'], ['installation_id', 'position', 'action']],
  source_definitions: [['installation_id'], ['installation_id', ...texts, ...numbers, 'enabledCookieJar']],
  source_definition_rules: [['installation_id', 'rule_group'],
    ['installation_id', 'rule_group', 'encoded', ...ruleFields]],
  source_definition_nodes: [['installation_id', 'field', 'node_id'],
    ['installation_id', 'field', ...nodeColumns]],
  source_private_profiles: [['installation_id', 'profile_suffix'], ['installation_id', 'profile_suffix']],
  source_private_cookies: [['installation_id', 'profile_suffix', 'domain', 'path', 'name'],
    ['installation_id', 'profile_suffix', 'domain', 'path', 'name', 'value', 'host_only',
      'secure', 'http_only', 'same_site', 'expires_at_ms', 'sequence']],
  source_private_kv_entries: [['installation_id', 'profile_suffix', 'entry_key'],
    ['installation_id', 'profile_suffix', 'entry_key', 'position']],
  source_private_kv_nodes: [['installation_id', 'profile_suffix', 'entry_key', 'node_id'],
    ['installation_id', 'profile_suffix', 'entry_key', ...nodeColumns]],
};
const object = value => value && typeof value === 'object' && !Array.isArray(value) && !isRawJSON(value);
const nodeValues = row => [row.nodeId, row.parentId, row.propKey, row.itemIndex,
  row.kind, row.boolValue, row.intValue, row.realValue, row.textValue];

function definitionRows(id, text, output) {
  let definition;
  try { definition = parseJSONText(text); } catch { return ['html', text]; }
  if (Array.isArray(definition)) [definition] = definition;
  if (!object(definition)) return ['html', String(definition)];
  const remaining = { ...definition };
  const values = [id];
  for (const field of texts) {
    const value = remaining[field];
    values.push(typeof value === 'string' ? value : null);
    if (typeof value === 'string') delete remaining[field];
  }
  for (const field of numbers) {
    const raw = remaining[field], value = isRawJSON(raw) ? Number(raw.rawJSON) : raw;
    const accepted = typeof value === 'number' && Number.isFinite(value) &&
      (field !== 'bookSourceType' || Number.isSafeInteger(value));
    values.push(accepted ? value : null);
    if (accepted) delete remaining[field];
  }
  values.push(typeof remaining.enabledCookieJar === 'boolean' ? Number(remaining.enabledCookieJar) : null);
  if (typeof remaining.enabledCookieJar === 'boolean') delete remaining.enabledCookieJar;
  output.source_definitions.push(values);
  for (const group of rules) {
    let rule = remaining[group], encoded = false;
    if (typeof rule === 'string') {
      try { rule = parseJSONText(rule); encoded = true; } catch { continue; }
    }
    if (!object(rule)) continue;
    const extras = { ...rule }, fields = [id, group, Number(encoded)];
    for (const field of ruleFields) {
      fields.push(typeof extras[field] === 'string' ? extras[field] : null);
      if (typeof extras[field] === 'string') delete extras[field];
    }
    output.source_definition_rules.push(fields);
    delete remaining[group];
    if (Object.keys(extras).length) remaining[group] = extras;
  }
  for (const [field, value] of Object.entries(remaining)) {
    for (const node of encodeValueNodes(value))
      output.source_definition_nodes.push([id, field, ...nodeValues(node)]);
  }
  return ['object', null];
}

function rowsFor(records, native, offset = 0) {
  const output = Object.fromEntries(Object.keys(relations).map(table => [table, []]));
  let position = offset;
  for (const item of records.values()) {
    const id = item.installationId;
    if (!native) {
      const [kind, html] = definitionRows(id, item.definition, output);
      output.source_installations.push([id, position++, item.revision, item.name, item.sourceUrl,
        item.group, item.order, Number(item.enabled), Number(item.enabledExplore), item.protocol, kind, html]);
      for (const [index, action] of (item.actions ?? []).entries())
        output.source_installation_actions.push([id, index, action]);
    }
    const state = item.private ?? { cookies: [], storage: [] };
    const profiles = new Set([...state.cookies, ...state.storage].map(([suffix]) => suffix));
    for (const suffix of profiles) output.source_private_profiles.push([id, suffix]);
    for (const [suffix, entries] of state.cookies) for (const [, cookie] of entries)
      output.source_private_cookies.push([id, suffix, cookie.domain, cookie.path, cookie.name,
        cookie.value, Number(cookie.hostOnly), Number(cookie.secure), Number(cookie.httpOnly),
        cookie.sameSite, cookie.expires, cookie.sequence]);
    for (const [suffix, entries] of state.storage) for (const [index, [key, value]] of entries.entries()) {
      output.source_private_kv_entries.push([id, suffix, key, index]);
      for (const node of encodeValueNodes(value))
        output.source_private_kv_nodes.push([id, suffix, key, ...nodeValues(node)]);
    }
  }
  return output;
}

export class SourceStorage {
  constructor(root, native) {
    this.db = openDatabase(root);
    this.native = native;
    this.owner = native ? 'source.private' : 'source.library';
    this.revision = 1;
    this.rows = Object.fromEntries(Object.keys(relations).map(table => [table, []]));
    this.items = new Map();
  }
  async load() {
    await this.db.ready;
    await this.db.execute('INSERT INTO owner_revisions VALUES(?,1,?) ON CONFLICT DO NOTHING',
      [this.owner, 'initial']);
    const tables = Object.keys(relations).filter(table => !this.native || table.startsWith('source_private_'));
    const snapshot = await this.db.snapshot([
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [this.owner], rows: true },
      ...tables.map(table => ({ sql: `SELECT * FROM ${table}`, rows: true, maxRows: 65536 })),
      { sql: 'SELECT installation_id FROM source_reader_state', rows: true, maxRows: 65536 },
    ]);
    this.revision = snapshot[0][0].revision;
    const objects = Object.fromEntries(tables.map((table, index) => [table, snapshot[index + 1]]));
    for (const table of tables) this.rows[table] = objects[table].map(row => relations[table][1].map(field => row[field]));
    const records = new Map();
    const get = id => {
      if (!records.has(id)) records.set(id, { installationId: id, private: { cookies: [], storage: [] } });
      return records.get(id);
    };
    if (!this.native) for (const row of objects.source_installations.sort((a, b) => a.position - b.position)) {
      const item = get(row.installation_id);
      Object.assign(item, { revision: row.revision, name: row.name, sourceUrl: row.source_url,
        group: row.source_group, order: row.sort_order, enabled: Boolean(row.enabled),
        enabledExplore: Boolean(row.enabled_explore), protocol: row.protocol, actions: [] });
      if (row.definition_kind === 'html') { item.definition = row.raw_html; continue; }
      const definition = objects.source_definitions.find(value => value.installation_id === row.installation_id);
      const value = {};
      for (const field of [...texts, ...numbers]) if (definition[field] !== null) value[field] = definition[field];
      if (definition.enabledCookieJar !== null) value.enabledCookieJar = Boolean(definition.enabledCookieJar);
      const groups = objects.source_definition_rules.filter(rule => rule.installation_id === row.installation_id);
      for (const group of groups) value[group.rule_group] = Object.fromEntries(
        ruleFields.filter(field => group[field] !== null).map(field => [field, group[field]]));
      const extensions = new Map();
      for (const node of objects.source_definition_nodes.filter(node => node.installation_id === row.installation_id)) {
        if (!extensions.has(node.field)) extensions.set(node.field, []);
        extensions.get(node.field).push(node);
      }
      for (const [field, nodes] of extensions) {
        const extension = decodeValueNodes(nodes, { rawNumbers: true });
        value[field] = groups.some(group => group.rule_group === field) ? { ...value[field], ...extension } : extension;
      }
      for (const group of groups) if (group.encoded) value[group.rule_group] = stringifyJSONValue(value[group.rule_group]);
      item.definition = stringifyJSONValue(value);
    }
    for (const row of objects.source_installation_actions ?? [])
      get(row.installation_id).actions[row.position] = row.action;
    for (const row of objects.source_private_profiles) {
      const item = get(row.installation_id);
      item.private.cookies.push([row.profile_suffix, []]);
      item.private.storage.push([row.profile_suffix, []]);
    }
    for (const row of objects.source_private_cookies) {
      const entries = get(row.installation_id).private.cookies.find(([suffix]) => suffix === row.profile_suffix)[1];
      entries.push([JSON.stringify([row.domain, row.path, row.name]), {
        name: row.name, value: row.value, domain: row.domain, path: row.path,
        hostOnly: Boolean(row.host_only), secure: Boolean(row.secure), httpOnly: Boolean(row.http_only),
        sameSite: row.same_site, expires: row.expires_at_ms, sequence: row.sequence,
      }]);
    }
    for (const row of objects.source_private_kv_entries.sort((a, b) => a.position - b.position)) {
      const entries = get(row.installation_id).private.storage.find(([suffix]) => suffix === row.profile_suffix)[1];
      const nodes = objects.source_private_kv_nodes.filter(node => node.installation_id === row.installation_id &&
        node.profile_suffix === row.profile_suffix && node.entry_key === row.entry_key);
      entries.push([row.entry_key, decodeValueNodes(nodes, { rawNumbers: true })]);
    }
    for (const row of snapshot.at(-1)) get(row.installation_id);
    for (const [id, input] of records) {
      this.items.set(id, {
        input: { ...input, actions: input.actions && [...input.actions] },
        rows: Object.fromEntries(Object.entries(this.rows).map(([table, rows]) =>
          [table, rows.filter(row => row[0] === id)])),
      });
    }
    this.rows = null;
    return records;
  }
  async save(records) {
    const next = new Map(), statements = [];
    const fields = ['revision', 'name', 'sourceUrl', 'group', 'order', 'enabled', 'enabledExplore', 'protocol', 'definition'];
    let position = 0;
    for (const [id, input] of records) {
      const previous = this.items.get(id);
      if (!previous) statements.push({ sql: 'INSERT INTO source_reader_state(installation_id) VALUES(?)', params: [id] });
      const same = previous && fields.every(field => previous.input[field] === input[field]) &&
        isDeepStrictEqual(previous.input.actions, input.actions) &&
        isDeepStrictEqual(previous.input.private, input.private) &&
        (this.native || previous.rows.source_installations[0][1] === position);
      if (same) { next.set(id, previous); ++position; continue; }
      const rows = rowsFor(new Map([[id, input]]), this.native, position++);
      next.set(id, { input: { ...input, actions: input.actions && [...input.actions] }, rows });
      for (const [table, [keys, columns]] of Object.entries(relations)) {
        if (this.native && !table.startsWith('source_private_')) continue;
        statements.push(...reconcile(table, columns, keys, previous?.rows[table] ?? [], rows[table]));
      }
    }
    for (const [id] of this.items) if (!next.has(id)) {
      if (!this.native) statements.push({ sql: 'DELETE FROM source_installations WHERE installation_id=?', params: [id] });
      statements.push({ sql: 'DELETE FROM source_private_profiles WHERE installation_id=?', params: [id] });
      statements.push({ sql: 'DELETE FROM source_reader_state WHERE installation_id=?', params: [id] });
    }
    if (!statements.length) return;
    statements.unshift({ sql: 'UPDATE owner_revisions SET revision=revision+1 WHERE owner=? AND revision=?',
      params: [this.owner, this.revision], expectChanges: 1 });
    await this.db.transaction(statements);
    ++this.revision;
    this.items = next;
  }
  readReader(id) { return readReaderValues(this.db, id); }
  applyReader(id, patch, signal) { return applyReaderValues(this.db, id, patch, this.owner, this.revision, signal); }
  close() { return this.db.close(); }
}
