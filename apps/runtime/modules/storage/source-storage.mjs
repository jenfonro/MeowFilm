import { openDatabase } from '@meowfilm/storage';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { reconcile } from '@meowfilm/storage/statements';
import { parseJSONText, stringifyJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';
import { isDeepStrictEqual } from 'node:util';
import { readReaderValues, applyReaderValues } from './source-reader-values.mjs';

const textFields = [
  ['bookSourceName', 'book_source_name'], ['bookSourceUrl', 'book_source_url'],
  ['bookSourceGroup', 'book_source_group'], ['bookSourceComment', 'book_source_comment'],
  ['bookUrlPattern', 'book_url_pattern'], ['searchUrl', 'search_url'], ['exploreUrl', 'explore_url'],
  ['loginUrl', 'login_url'], ['loginCheckJs', 'login_check_js'], ['jsLib', 'js_lib'],
  ['coverDecodeJs', 'cover_decode_js'], ['concurrentRate', 'concurrent_rate'],
  ['header', 'header'], ['html', 'html'],
];
const numberFields = [
  ['bookSourceType', 'book_source_type'], ['weight', 'weight'],
  ['respondTime', 'respond_time'], ['lastUpdateTime', 'last_update_time'],
];
const rules = ['ruleSearch', 'ruleExplore', 'ruleBookInfo', 'ruleToc', 'ruleContent'];
const ruleFieldMap = [
  ['bookList', 'book_list'], ['name', 'name'], ['author', 'author'], ['kind', 'kind'],
  ['wordCount', 'word_count'], ['lastChapter', 'last_chapter'], ['intro', 'intro'],
  ['coverUrl', 'cover_url'], ['bookUrl', 'book_url'], ['tocUrl', 'toc_url'], ['init', 'init'],
  ['chapterList', 'chapter_list'], ['chapterName', 'chapter_name'], ['chapterUrl', 'chapter_url'],
  ['nextTocUrl', 'next_toc_url'], ['isReverse', 'is_reverse'], ['reverseToc', 'reverse_toc'],
  ['updateTime', 'update_time'], ['preUpdateJs', 'pre_update_js'], ['content', 'content'],
  ['nextContentUrl', 'next_content_url'], ['title', 'title'], ['replaceRegex', 'replace_regex'],
  ['sourceRegex', 'source_regex'], ['webJs', 'web_js'], ['payAction', 'pay_action'],
  ['imageStyle', 'image_style'], ['imageDecode', 'image_decode'], ['imageDecrypt', 'image_decrypt'],
];
const nodeColumns = ['node_id', 'parent_id', 'prop_key', 'item_index', 'kind',
  'bool_value', 'int_value', 'real_value', 'text_value'];
const relations = {
  source_installations: [['installation_id'], ['installation_id', 'position', 'revision',
    'name', 'source_url', 'source_group', 'sort_order', 'enabled', 'enabled_explore',
    'protocol', 'definition_kind', 'raw_html']],
  source_installation_actions: [['installation_id', 'position'], ['installation_id', 'position', 'action']],
  source_definitions: [['installation_id'], ['installation_id', ...textFields.map(f => f[1]), ...numberFields.map(f => f[1]), 'enabled_cookie_jar']],
  source_definition_rules: [['installation_id', 'rule_group'],
    ['installation_id', 'rule_group', 'encoded', ...ruleFieldMap.map(f => f[1])]],
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
  for (const [field] of textFields) {
    const value = remaining[field];
    values.push(typeof value === 'string' ? value : null);
    if (typeof value === 'string') delete remaining[field];
  }
  for (const [field] of numberFields) {
    const raw = remaining[field], value = isRawJSON(raw) ? Number(raw.rawJSON) : raw;
    const accepted = !isRawJSON(raw) && typeof value === 'number' && Number.isFinite(value) &&
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
    for (const [field] of ruleFieldMap) {
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

function rowsFor(records, native, offset = 0, previous) {
  const output = Object.fromEntries(Object.keys(relations).map(table => [table, []]));
  let position = offset;
  for (const item of records.values()) {
    const id = item.installationId;
    if (!native) {
      let kind, html;
      if (item.definition === undefined && previous) {
        [kind, html] = previous.source_installations[0].slice(-2);
        for (const table of ['source_definitions', 'source_definition_rules', 'source_definition_nodes'])
          output[table] = previous[table];
      } else [kind, html] = definitionRows(id, item.definition, output);
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
    for (const [suffix, entries] of state.storage) for (const [index, entry] of entries.entries()) {
      const [key, value] = entry;
      output.source_private_kv_entries.push([id, suffix, key, index]);
      if (entry.length === 1) {
        output.source_private_kv_nodes.push(...previous.source_private_kv_nodes
          .filter(row => row[1] === suffix && row[2] === key));
      } else for (const node of encodeValueNodes(value))
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
      if (row.definition_kind === 'html') item.definition = row.raw_html;
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
      entries.push([row.entry_key]);
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
  definition(id, selected) {
    const rows = this.items.get(id).rows;
    const installation = rows.source_installations[0];
    if (installation.at(-2) === 'html') return selected ? {} : installation.at(-1);
    const objects = table => rows[table].map(row => Object.fromEntries(
      relations[table][1].map((field, index) => [field, row[index]])));
    const definition = objects('source_definitions')[0];
    if (!definition) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
    const requested = field => !selected || selected.includes(field);
    const value = {};
    for (const [field, column] of [...textFields, ...numberFields])
      if (requested(field) && definition[column] !== null) value[field] = definition[column];
    if (requested('enabledCookieJar') && definition.enabled_cookie_jar !== null)
      value.enabledCookieJar = Boolean(definition.enabled_cookie_jar);
    const groups = objects('source_definition_rules').filter(group => requested(group.rule_group));
    for (const group of groups) value[group.rule_group] = Object.fromEntries(
      ruleFieldMap.filter(([, column]) => group[column] !== null).map(([field, column]) => [field, group[column]]));
    const extensions = new Map();
    for (const node of objects('source_definition_nodes')) {
      if (!requested(node.field)) continue;
      if (!extensions.has(node.field)) extensions.set(node.field, []);
      extensions.get(node.field).push(node);
    }
    for (const [field, nodes] of extensions) {
      const extension = decodeValueNodes(nodes, { rawNumbers: true });
      if (extension === undefined) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
      Object.defineProperty(value, field, { enumerable: true, configurable: true, writable: true,
        value: groups.some(group => group.rule_group === field) ? { ...value[field], ...extension } : extension });
    }
    for (const group of groups) if (group.encoded) value[group.rule_group] = stringifyJSONValue(value[group.rule_group]);
    return selected ? value : stringifyJSONValue(value);
  }
  privateValue(id, suffix, key) {
    const rows = this.items.get(id)?.rows.source_private_kv_nodes
      .filter(row => row[1] === suffix && row[2] === key) ?? [];
    const value = decodeValueNodes(rows.map(row => Object.fromEntries(
      relations.source_private_kv_nodes[1].map((field, index) => [field, row[index]]))), { rawNumbers: true });
    if (value === undefined) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
    return value;
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
      const rows = rowsFor(new Map([[id, input]]), this.native, position++, previous?.rows);
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
