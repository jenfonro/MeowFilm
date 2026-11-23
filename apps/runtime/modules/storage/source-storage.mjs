import { openDatabase } from '@meowfilm/storage';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { upsert, nodeInserts } from '@meowfilm/storage/statements';
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
  ['bookSourceType', 'book_source_type', true],
  ['weight', 'weight', false],
  ['respondTime', 'respond_time_ms', false],
  ['lastUpdateTime', 'last_update_at_ms', true],
];
const rules = ['ruleSearch', 'ruleExplore', 'ruleBookInfo', 'ruleToc', 'ruleContent'];
const ruleFieldMap = [
  ['bookList', 'book_list'], ['name', 'name'], ['author', 'author'], ['kind', 'kind'],
  ['wordCount', 'word_count'], ['lastChapter', 'last_chapter'], ['intro', 'intro'],
  ['coverUrl', 'cover_url'], ['bookUrl', 'book_url'], ['tocUrl', 'toc_url'], ['init', 'init'],
  ['chapterList', 'chapter_list'], ['chapterName', 'chapter_name'], ['chapterUrl', 'chapter_url'],
  ['nextTocUrl', 'next_toc_url'], ['isReverse', 'is_reverse'], ['reverseToc', 'reverse_toc'],
  ['updateTime', 'update_time_rule'], ['preUpdateJs', 'pre_update_js'], ['content', 'content'],
  ['nextContentUrl', 'next_content_url'], ['title', 'title'], ['replaceRegex', 'replace_regex'],
  ['sourceRegex', 'source_regex'], ['webJs', 'web_js'], ['payAction', 'pay_action'],
  ['imageStyle', 'image_style'], ['imageDecode', 'image_decode'], ['imageDecrypt', 'image_decrypt'],
];
const nodeCols = ['node_id', 'parent_id', 'prop_key', 'item_index', 'kind',
  'bool_value', 'int_value', 'real_value', 'text_value'];
const installColumns = ['installation_id', 'position', 'revision', 'name', 'source_url',
  'source_group', 'sort_order', 'enabled', 'enabled_explore', 'protocol', 'definition_kind', 'raw_html'];
const defColumns = ['installation_id', ...textFields.map(f => f[1]), ...numberFields.map(f => f[1]), 'enabled_cookie_jar'];
const ruleColumns = ['installation_id', 'rule_group', 'encoded', ...ruleFieldMap.map(f => f[1])];
const cookieColumns = ['installation_id', 'profile_suffix', 'domain', 'path', 'name', 'value',
  'host_only', 'secure', 'http_only', 'same_site', 'expires_at_ms', 'sequence'];
const object = value => value && typeof value === 'object' && !Array.isArray(value) && !isRawJSON(value);

function parseDefinition(id, text) {
  let definition;
  try { definition = parseJSONText(text); } catch { return { kind: 'html', html: text, def: null, rules: [], nodes: [] }; }
  if (Array.isArray(definition)) [definition] = definition;
  if (!object(definition)) return { kind: 'html', html: String(definition), def: null, rules: [], nodes: [] };
  const remaining = { ...definition };
  const defObj = { installation_id: id };
  for (const [field, col] of textFields) {
    const value = remaining[field];
    defObj[col] = typeof value === 'string' ? value : null;
    if (typeof value === 'string') delete remaining[field];
  }
  for (const [field, col, requireInt] of numberFields) {
    const raw = remaining[field], value = isRawJSON(raw) ? Number(raw.rawJSON) : raw;
    const accepted = !isRawJSON(raw) && typeof value === 'number' && Number.isFinite(value) &&
      (!requireInt || Number.isSafeInteger(value));
    defObj[col] = accepted ? value : null;
    if (accepted) delete remaining[field];
  }
  defObj.enabled_cookie_jar = typeof remaining.enabledCookieJar === 'boolean' ? Number(remaining.enabledCookieJar) : null;
  if (typeof remaining.enabledCookieJar === 'boolean') delete remaining.enabledCookieJar;

  const ruleRows = [];
  for (const group of rules) {
    let rule = remaining[group], encoded = false;
    if (typeof rule === 'string') {
      try { rule = parseJSONText(rule); encoded = true; } catch { continue; }
    }
    if (!object(rule)) continue;
    const extras = { ...rule };
    const row = { installation_id: id, rule_group: group, encoded: Number(encoded) };
    for (const [field, col] of ruleFieldMap) {
      row[col] = typeof extras[field] === 'string' ? extras[field] : null;
      if (typeof extras[field] === 'string') delete extras[field];
    }
    ruleRows.push(row);
    delete remaining[group];
    if (Object.keys(extras).length) remaining[group] = extras;
  }
  const nodeRows = [];
  for (const [field, value] of Object.entries(remaining)) {
    for (const node of encodeValueNodes(value)) {
      nodeRows.push({
        installation_id: id, field,
        node_id: node.nodeId, parent_id: node.parentId, prop_key: node.propKey, item_index: node.itemIndex,
        kind: node.kind, bool_value: node.boolValue, int_value: node.intValue, real_value: node.realValue, text_value: node.textValue,
      });
    }
  }
  return { kind: 'object', html: null, def: defObj, rules: ruleRows, nodes: nodeRows };
}

function toNodeObjects(rows) {
  return rows.map(r => ({
    nodeId: r.node_id, parentId: r.parent_id, propKey: r.prop_key, itemIndex: r.item_index,
    kind: r.kind, boolValue: r.bool_value, intValue: r.int_value, realValue: r.real_value, textValue: r.text_value,
  }));
}

export class SourceStorage {
  constructor(root, native) {
    this.db = openDatabase(root);
    this.native = native;
    this.owner = native ? 'source.private' : 'source.library';
    this.revision = 0;
    this.items = new Map();
  }
  async load() {
    await this.db.ready;
    const queries = [
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [this.owner], rows: true },
      { sql: 'SELECT installation_id,profile_suffix FROM source_private_profiles', rows: true, maxRows: 65536 },
      { sql: `SELECT ${cookieColumns.join(',')} FROM source_private_cookies`, rows: true, maxRows: 65536 },
      { sql: 'SELECT installation_id,profile_suffix,entry_key,position FROM source_private_kv_entries ORDER BY installation_id,profile_suffix,position', rows: true, maxRows: 65536 },
      { sql: `SELECT installation_id,profile_suffix,entry_key,${nodeCols.join(',')} FROM source_private_kv_nodes ORDER BY installation_id,profile_suffix,entry_key,node_id`, rows: true, maxRows: 65536 },
      { sql: 'SELECT installation_id FROM source_reader_state', rows: true, maxRows: 65536 },
    ];
    if (!this.native) {
      queries.push(
        { sql: `SELECT ${installColumns.join(',')} FROM source_installations ORDER BY position`, rows: true, maxRows: 65536 },
        { sql: 'SELECT installation_id,position,action FROM source_installation_actions ORDER BY installation_id,position', rows: true, maxRows: 65536 },
        { sql: `SELECT ${defColumns.join(',')} FROM source_definitions`, rows: true, maxRows: 65536 },
        { sql: `SELECT ${ruleColumns.join(',')} FROM source_definition_rules`, rows: true, maxRows: 65536 },
        { sql: `SELECT installation_id,field,${nodeCols.join(',')} FROM source_definition_nodes ORDER BY installation_id,field,node_id`, rows: true, maxRows: 65536 },
      );
    }
    const snapshot = await this.db.snapshot(queries);
    this.revision = snapshot[0][0]?.revision ?? 0;
    const [, profiles, cookies, kvEntries, kvNodes, readerStates] = snapshot;
    const records = new Map();
    const get = id => {
      if (!records.has(id)) records.set(id, { installationId: id, private: { cookies: [], storage: [] } });
      return records.get(id);
    };
    const defCache = new Map();
    if (!this.native) {
      const [installations, actions, defs, ruleRows, defNodes] = snapshot.slice(6);
      const defsById = new Map(defs.map(r => [r.installation_id, r]));
      const rulesById = new Map();
      for (const r of ruleRows) {
        let list = rulesById.get(r.installation_id);
        if (!list) rulesById.set(r.installation_id, list = []);
        list.push(r);
      }
      const nodesById = new Map();
      for (const r of defNodes) {
        let list = nodesById.get(r.installation_id);
        if (!list) nodesById.set(r.installation_id, list = []);
        list.push(r);
      }
      for (const row of installations) {
        const item = get(row.installation_id);
        Object.assign(item, {
          revision: row.revision, name: row.name, sourceUrl: row.source_url,
          group: row.source_group, order: row.sort_order, enabled: Boolean(row.enabled),
          enabledExplore: Boolean(row.enabled_explore), protocol: row.protocol, actions: [],
        });
        if (row.definition_kind === 'html') item.definition = row.raw_html;
        defCache.set(row.installation_id, {
          position: row.position,
          kind: row.definition_kind,
          html: row.raw_html,
          def: defsById.get(row.installation_id) ?? null,
          rules: rulesById.get(row.installation_id) ?? [],
          nodes: nodesById.get(row.installation_id) ?? [],
        });
      }
      for (const row of actions) get(row.installation_id).actions[row.position] = row.action;
    }
    for (const row of profiles) {
      const item = get(row.installation_id);
      item.private.cookies.push([row.profile_suffix, []]);
      item.private.storage.push([row.profile_suffix, []]);
    }
    for (const row of cookies) {
      const entries = get(row.installation_id).private.cookies.find(([suffix]) => suffix === row.profile_suffix)[1];
      entries.push([JSON.stringify([row.domain, row.path, row.name]), {
        name: row.name, value: row.value, domain: row.domain, path: row.path,
        hostOnly: Boolean(row.host_only), secure: Boolean(row.secure), httpOnly: Boolean(row.http_only),
        sameSite: row.same_site, expires: row.expires_at_ms, sequence: row.sequence,
      }]);
    }
    for (const row of kvEntries) {
      const entries = get(row.installation_id).private.storage.find(([suffix]) => suffix === row.profile_suffix)[1];
      entries.push([row.entry_key]);
    }
    for (const row of readerStates) get(row.installation_id);

    const kvByKey = new Map();
    for (const row of kvNodes) {
      let byProfile = kvByKey.get(row.installation_id);
      if (!byProfile) kvByKey.set(row.installation_id, byProfile = new Map());
      const k = `${row.profile_suffix}\0${row.entry_key}`;
      let list = byProfile.get(k);
      if (!list) byProfile.set(k, list = []);
      list.push(row);
    }

    this.items.clear();
    for (const [id, input] of records) {
      this.items.set(id, {
        input: { ...input, actions: input.actions && [...input.actions] },
        defData: defCache.get(id) ?? null,
        kvNodes: kvByKey.get(id) ?? new Map(),
      });
    }
    return records;
  }

  definition(id, selected) {
    const defData = this.items.get(id)?.defData;
    if (!defData) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
    if (defData.kind === 'html') return selected ? {} : defData.html;
    const definition = defData.def;
    if (!definition) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
    const requested = field => !selected || selected.includes(field);
    const value = {};
    for (const [field, column] of [...textFields, ...numberFields])
      if (requested(field) && definition[column] !== null) value[field] = definition[column];
    if (requested('enabledCookieJar') && definition.enabled_cookie_jar !== null)
      value.enabledCookieJar = Boolean(definition.enabled_cookie_jar);
    const groups = defData.rules.filter(group => requested(group.rule_group));
    for (const group of groups) {
      value[group.rule_group] = Object.fromEntries(
        ruleFieldMap.filter(([, column]) => group[column] !== null).map(([field, column]) => [field, group[column]]),
      );
    }
    const extensions = new Map();
    for (const node of defData.nodes) {
      if (!requested(node.field)) continue;
      if (!extensions.has(node.field)) extensions.set(node.field, []);
      extensions.get(node.field).push(node);
    }
    for (const [field, nodes] of extensions) {
      const extension = decodeValueNodes(nodes, { rawNumbers: true });
      if (extension === undefined) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
      Object.defineProperty(value, field, {
        enumerable: true, configurable: true, writable: true,
        value: groups.some(group => group.rule_group === field) ? { ...value[field], ...extension } : extension,
      });
    }
    for (const group of groups) if (group.encoded) value[group.rule_group] = stringifyJSONValue(value[group.rule_group]);
    return selected ? value : stringifyJSONValue(value);
  }

  privateValue(id, suffix, key) {
    const rows = this.items.get(id)?.kvNodes.get(`${suffix}\0${key}`) ?? [];
    const value = decodeValueNodes(rows, { rawNumbers: true });
    if (value === undefined) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
    return value;
  }

  async save(records) {
    const next = new Map(), statements = [];
    const scalarFields = ['revision', 'name', 'sourceUrl', 'group', 'order', 'enabled', 'enabledExplore', 'protocol', 'definition'];
    let position = 0;

    // Delete removed installations before upserts so reusing a unique source_url in the same save never collides.
    for (const [id] of this.items) {
      if (!records.has(id)) {
        if (!this.native) statements.push({ sql: 'DELETE FROM source_installations WHERE installation_id=?', params: [id] });
        statements.push({ sql: 'DELETE FROM source_private_profiles WHERE installation_id=?', params: [id] });
        statements.push({ sql: 'DELETE FROM source_reader_state WHERE installation_id=?', params: [id] });
      }
    }

    for (const [id, input] of records) {
      const previous = this.items.get(id);
      const pos = position++;
      if (!previous) statements.push({ sql: 'INSERT INTO source_reader_state(installation_id) VALUES(?)', params: [id] });
      const same = previous && scalarFields.every(field => previous.input[field] === input[field]) &&
        isDeepStrictEqual(previous.input.actions, input.actions) &&
        isDeepStrictEqual(previous.input.private, input.private) &&
        (this.native || previous.defData?.position === pos);
      if (same) { next.set(id, previous); continue; }

      let defData = previous?.defData ?? null;
      if (!this.native) {
        const defChanged = !previous || input.definition !== undefined;
        if (defChanged) {
          defData = { ...parseDefinition(id, input.definition), position: pos };
        } else {
          defData = { ...defData, position: pos };
        }
        statements.push(upsert('source_installations', installColumns, [
          id, pos, input.revision, input.name, input.sourceUrl, input.group,
          input.order, Number(input.enabled), Number(input.enabledExplore),
          input.protocol, defData.kind, defData.html,
        ], ['installation_id']));
        if (!previous || !isDeepStrictEqual(previous.input.actions, input.actions)) {
          if (previous) statements.push({ sql: 'DELETE FROM source_installation_actions WHERE installation_id=?', params: [id] });
          for (const [index, action] of (input.actions ?? []).entries()) {
            statements.push({
              sql: 'INSERT INTO source_installation_actions(installation_id,position,action) VALUES(?,?,?)',
              params: [id, index, action],
            });
          }
        }
        if (defChanged) {
          if (previous) {
            statements.push(
              { sql: 'DELETE FROM source_definitions WHERE installation_id=?', params: [id] },
              { sql: 'DELETE FROM source_definition_rules WHERE installation_id=?', params: [id] },
              { sql: 'DELETE FROM source_definition_nodes WHERE installation_id=?', params: [id] },
            );
          }
          if (defData.def) {
            statements.push({
              sql: `INSERT INTO source_definitions(${defColumns.join(',')}) VALUES(${defColumns.map(() => '?').join(',')})`,
              params: defColumns.map(c => defData.def[c]),
            });
          }
          for (const rule of defData.rules) {
            statements.push({
              sql: `INSERT INTO source_definition_rules(${ruleColumns.join(',')}) VALUES(${ruleColumns.map(() => '?').join(',')})`,
              params: ruleColumns.map(c => rule[c]),
            });
          }
          const byField = new Map();
          for (const n of defData.nodes) {
            let list = byField.get(n.field);
            if (!list) byField.set(n.field, list = []);
            list.push(n);
          }
          for (const [field, rows] of byField) {
            statements.push(...nodeInserts('source_definition_nodes', ['installation_id', 'field'], [id, field], toNodeObjects(rows)));
          }
        }
      }

      const nextKvNodes = new Map();
      const state = input.private ?? { cookies: [], storage: [] };
      if (!previous || !isDeepStrictEqual(previous.input.private, input.private)) {
        if (previous) statements.push({ sql: 'DELETE FROM source_private_profiles WHERE installation_id=?', params: [id] });
        const profiles = new Set([...state.cookies, ...state.storage].map(([suffix]) => suffix));
        for (const suffix of profiles) {
          statements.push({ sql: 'INSERT INTO source_private_profiles(installation_id,profile_suffix) VALUES(?,?)', params: [id, suffix] });
        }
        for (const [suffix, entries] of state.cookies) {
          for (const [, cookie] of entries) {
            statements.push({
              sql: `INSERT INTO source_private_cookies(${cookieColumns.join(',')}) VALUES(${cookieColumns.map(() => '?').join(',')})`,
              params: [id, suffix, cookie.domain, cookie.path, cookie.name, cookie.value,
                Number(cookie.hostOnly), Number(cookie.secure), Number(cookie.httpOnly),
                cookie.sameSite, cookie.expires, cookie.sequence],
            });
          }
        }
        for (const [suffix, entries] of state.storage) {
          for (const [index, entry] of entries.entries()) {
            const [key, value] = entry;
            statements.push({
              sql: 'INSERT INTO source_private_kv_entries(installation_id,profile_suffix,entry_key,position) VALUES(?,?,?,?)',
              params: [id, suffix, key, index],
            });
            let encoded;
            if (entry.length === 1) {
              const prevRows = previous?.kvNodes.get(`${suffix}\0${key}`) ?? [];
              nextKvNodes.set(`${suffix}\0${key}`, prevRows);
              encoded = toNodeObjects(prevRows);
            } else {
              encoded = encodeValueNodes(value);
              nextKvNodes.set(`${suffix}\0${key}`, encoded.map(node => ({
                installation_id: id, profile_suffix: suffix, entry_key: key,
                node_id: node.nodeId, parent_id: node.parentId, prop_key: node.propKey, item_index: node.itemIndex,
                kind: node.kind, bool_value: node.boolValue, int_value: node.intValue, real_value: node.realValue, text_value: node.textValue,
              })));
            }
            statements.push(...nodeInserts('source_private_kv_nodes', ['installation_id', 'profile_suffix', 'entry_key'], [id, suffix, key], encoded));
          }
        }
      } else {
        for (const [k, v] of previous.kvNodes) nextKvNodes.set(k, v);
      }

      next.set(id, {
        input: { ...input, actions: input.actions && [...input.actions] },
        defData,
        kvNodes: nextKvNodes,
      });
    }

    if (!statements.length) return;
    statements.unshift(this.revision
      ? {
        sql: 'UPDATE owner_revisions SET revision=revision+1 WHERE owner=? AND revision=?',
        params: [this.owner, this.revision],
        expectChanges: 1,
      }
      : {
        sql: 'INSERT INTO owner_revisions(owner,revision) VALUES(?,1) ON CONFLICT(owner) DO NOTHING',
        params: [this.owner],
        expectChanges: 1,
      });
    await this.db.transaction(statements);
    ++this.revision;
    this.items = next;
  }

  readReader(id) { return readReaderValues(this.db, id); }
  applyReader(id, patch, signal) { return applyReaderValues(this.db, id, patch, this.owner, this.revision, signal); }
  close() { return this.db.close(); }
}
