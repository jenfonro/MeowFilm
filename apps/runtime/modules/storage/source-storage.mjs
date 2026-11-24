import { openDatabase } from '@meowfilm/storage';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { upsert, nodeInserts } from '@meowfilm/storage/statements';
import { parseJSONText, stringifyJSONValue, cloneJSONValue, isRawJSON } from '@meowfilm/runtime-bridge/json';
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
    const value = remaining[field];
    const accepted = typeof value === 'number' && Number.isFinite(value) &&
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

function sameDefData(previous, next) {
  if (!previous || !next || previous.kind !== next.kind || previous.html !== next.html) return false;
  if (!isDeepStrictEqual(previous.def, next.def)) return false;
  if (previous.rules.length !== next.rules.length || previous.nodes.length !== next.nodes.length) return false;
  const prevRules = new Map(previous.rules.map(row => [row.rule_group, row]));
  for (const row of next.rules) {
    if (!isDeepStrictEqual(prevRules.get(row.rule_group), row)) return false;
  }
  const groupNodes = rows => {
    const map = new Map();
    for (const row of rows) {
      let list = map.get(row.field);
      if (!list) map.set(row.field, list = []);
      list.push(row);
    }
    return map;
  };
  const prevNodes = groupNodes(previous.nodes);
  const nextNodes = groupNodes(next.nodes);
  if (prevNodes.size !== nextNodes.size) return false;
  for (const [field, rows] of nextNodes) {
    if (!isDeepStrictEqual(prevNodes.get(field), rows)) return false;
  }
  return true;
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
      { sql: 'SELECT installation_id,profile_suffix FROM source_private_profiles', rows: true, maxRows: Number.MAX_SAFE_INTEGER },
      { sql: `SELECT ${cookieColumns.join(',')} FROM source_private_cookies`, rows: true, maxRows: Number.MAX_SAFE_INTEGER },
      { sql: 'SELECT installation_id,profile_suffix,entry_key,position FROM source_private_kv_entries ORDER BY installation_id,profile_suffix,position', rows: true, maxRows: Number.MAX_SAFE_INTEGER },
      { sql: `SELECT installation_id,profile_suffix,entry_key,${nodeCols.join(',')} FROM source_private_kv_nodes ORDER BY installation_id,profile_suffix,entry_key,node_id`, rows: true, maxRows: Number.MAX_SAFE_INTEGER },
      { sql: 'SELECT installation_id FROM source_reader_state', rows: true, maxRows: Number.MAX_SAFE_INTEGER },
    ];
    if (!this.native) {
      queries.push(
        { sql: `SELECT ${installColumns.join(',')} FROM source_installations ORDER BY position`, rows: true, maxRows: Number.MAX_SAFE_INTEGER },
        { sql: 'SELECT installation_id,position,action FROM source_installation_actions ORDER BY installation_id,position', rows: true, maxRows: Number.MAX_SAFE_INTEGER },
        { sql: `SELECT ${defColumns.join(',')} FROM source_definitions`, rows: true, maxRows: Number.MAX_SAFE_INTEGER },
        { sql: `SELECT ${ruleColumns.join(',')} FROM source_definition_rules`, rows: true, maxRows: Number.MAX_SAFE_INTEGER },
        { sql: `SELECT installation_id,field,${nodeCols.join(',')} FROM source_definition_nodes ORDER BY installation_id,field,node_id`, rows: true, maxRows: Number.MAX_SAFE_INTEGER },
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
      const bucket = get(row.installation_id).private.cookies.find(([suffix]) => suffix === row.profile_suffix);
      if (!bucket) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
      bucket[1].push([JSON.stringify([row.domain, row.path, row.name]), {
        name: row.name, value: row.value, domain: row.domain, path: row.path,
        hostOnly: Boolean(row.host_only), secure: Boolean(row.secure), httpOnly: Boolean(row.http_only),
        sameSite: row.same_site, expires: row.expires_at_ms, sequence: row.sequence,
      }]);
    }
    for (const row of kvEntries) {
      const bucket = get(row.installation_id).private.storage.find(([suffix]) => suffix === row.profile_suffix);
      if (!bucket) throw Object.assign(new Error('STORAGE_CORRUPT'), { code: 'STORAGE_CORRUPT' });
      bucket[1].push([row.entry_key]);
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
        input: { ...input, actions: input.actions && [...input.actions], private: cloneJSONValue(input.private) },
        definitionText: input.definition,
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
    const metaFields = ['revision', 'name', 'sourceUrl', 'group', 'order', 'enabled', 'enabledExplore', 'protocol'];
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
      const metaSame = previous && metaFields.every(field => previous.input[field] === input[field]);
      const defTextSame = previous && (input.definition === undefined ||
        previous.definitionText !== undefined && previous.definitionText === input.definition);
      const actionsSame = previous && isDeepStrictEqual(previous.input.actions, input.actions);
      const privateSame = previous && isDeepStrictEqual(previous.input.private, input.private);
      const positionSame = this.native || previous?.defData?.position === pos;
      if (metaSame && defTextSame && actionsSame && privateSame && positionSame) {
        next.set(id, previous);
        continue;
      }

      let defData = previous?.defData ?? null;
      let definitionText = input.definition ?? previous?.definitionText;
      if (!this.native) {
        let defChanged = !previous;
        if (!defChanged && input.definition !== undefined && input.definition !== previous.definitionText) {
          const parsed = parseDefinition(id, input.definition);
          if (!sameDefData(previous.defData, parsed)) {
            defChanged = true;
            defData = { ...parsed, position: pos };
          } else {
            defData = { ...previous.defData, position: pos };
          }
        } else if (defChanged) {
          defData = { ...parseDefinition(id, input.definition), position: pos };
        } else {
          defData = { ...defData, position: pos };
        }
        if (!metaSame || !positionSame || defChanged) {
          statements.push(upsert('source_installations', installColumns, [
            id, pos, input.revision, input.name, input.sourceUrl, input.group,
            input.order, Number(input.enabled), Number(input.enabledExplore),
            input.protocol, defData.kind, defData.html,
          ], ['installation_id']));
        }
        if (!actionsSame) {
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
      if (!privateSame) {
        const prevState = previous?.input.private ?? { cookies: [], storage: [] };
        const prevProfiles = new Set([...prevState.cookies, ...prevState.storage].map(([suffix]) => suffix));
        const nextProfiles = new Set([...state.cookies, ...state.storage].map(([suffix]) => suffix));
        const prevCookies = new Map(prevState.cookies);
        const nextCookies = new Map(state.cookies);
        const prevStorage = new Map(prevState.storage);
        const nextStorage = new Map(state.storage);

        for (const suffix of prevProfiles) {
          if (!nextProfiles.has(suffix)) {
            statements.push({
              sql: 'DELETE FROM source_private_profiles WHERE installation_id=? AND profile_suffix=?',
              params: [id, suffix],
            });
          }
        }
        for (const suffix of nextProfiles) {
          const existed = prevProfiles.has(suffix);
          if (!existed) {
            statements.push({
              sql: 'INSERT INTO source_private_profiles(installation_id,profile_suffix) VALUES(?,?)',
              params: [id, suffix],
            });
          }
          const prevCookieEntries = prevCookies.get(suffix) ?? [];
          const nextCookieEntries = nextCookies.get(suffix) ?? [];
          if (!existed || !isDeepStrictEqual(prevCookieEntries, nextCookieEntries)) {
            const cookieId = cookie => JSON.stringify([cookie.domain, cookie.path, cookie.name]);
            const prevMap = new Map(prevCookieEntries.map(([, cookie]) => [cookieId(cookie), cookie]));
            const nextMap = new Map(nextCookieEntries.map(([, cookie]) => [cookieId(cookie), cookie]));
            if (existed) {
              for (const [cookieKey, cookie] of prevMap) {
                if (!nextMap.has(cookieKey)) {
                  statements.push({
                    sql: 'DELETE FROM source_private_cookies WHERE installation_id=? AND profile_suffix=? AND domain=? AND path=? AND name=?',
                    params: [id, suffix, cookie.domain, cookie.path, cookie.name],
                  });
                }
              }
            }
            for (const [cookieKey, cookie] of nextMap) {
              if (!existed || !isDeepStrictEqual(prevMap.get(cookieKey), cookie)) {
                statements.push(upsert('source_private_cookies', cookieColumns, [
                  id, suffix, cookie.domain, cookie.path, cookie.name, cookie.value,
                  Number(cookie.hostOnly), Number(cookie.secure), Number(cookie.httpOnly),
                  cookie.sameSite, cookie.expires, cookie.sequence,
                ], ['installation_id', 'profile_suffix', 'domain', 'path', 'name']));
              }
            }
          }
          const prevStorageEntries = prevStorage.get(suffix) ?? [];
          const nextStorageEntries = nextStorage.get(suffix) ?? [];
          if (existed && isDeepStrictEqual(prevStorageEntries, nextStorageEntries)) {
            for (const [key] of nextStorageEntries) {
              const k = `${suffix}\0${key}`;
              const prevRows = previous?.kvNodes.get(k);
              if (prevRows) nextKvNodes.set(k, prevRows);
            }
          } else {
            const prevKeyMap = new Map(prevStorageEntries.map((entry, idx) => [entry[0], { entry, idx }]));
            const nextKeySet = new Set(nextStorageEntries.map(entry => entry[0]));
            if (existed) {
              for (const key of prevKeyMap.keys()) {
                if (!nextKeySet.has(key)) {
                  statements.push({
                    sql: 'DELETE FROM source_private_kv_entries WHERE installation_id=? AND profile_suffix=? AND entry_key=?',
                    params: [id, suffix, key],
                  });
                }
              }
            }
            for (const [index, entry] of nextStorageEntries.entries()) {
              const [key, value] = entry;
              const k = `${suffix}\0${key}`;
              const prev = existed ? prevKeyMap.get(key) : undefined;
              if (entry.length === 1) {
                const prevRows = previous?.kvNodes.get(k) ?? [];
                nextKvNodes.set(k, prevRows);
                if (prev) {
                  if (prev.idx !== index) {
                    statements.push({
                      sql: 'UPDATE source_private_kv_entries SET position=? WHERE installation_id=? AND profile_suffix=? AND entry_key=?',
                      params: [index, id, suffix, key],
                    });
                  }
                } else {
                  statements.push({
                    sql: 'INSERT INTO source_private_kv_entries(installation_id,profile_suffix,entry_key,position) VALUES(?,?,?,?)',
                    params: [id, suffix, key, index],
                  });
                  statements.push(...nodeInserts('source_private_kv_nodes', ['installation_id', 'profile_suffix', 'entry_key'], [id, suffix, key], toNodeObjects(prevRows)));
                }
              } else {
                const encoded = encodeValueNodes(value);
                const nextRows = encoded.map(node => ({
                  installation_id: id, profile_suffix: suffix, entry_key: key,
                  node_id: node.nodeId, parent_id: node.parentId, prop_key: node.propKey, item_index: node.itemIndex,
                  kind: node.kind, bool_value: node.boolValue, int_value: node.intValue, real_value: node.realValue, text_value: node.textValue,
                }));
                nextKvNodes.set(k, nextRows);
                const prevRows = previous?.kvNodes.get(k);
                if (prev && prevRows && isDeepStrictEqual(prevRows, nextRows)) {
                  if (prev.idx !== index) {
                    statements.push({
                      sql: 'UPDATE source_private_kv_entries SET position=? WHERE installation_id=? AND profile_suffix=? AND entry_key=?',
                      params: [index, id, suffix, key],
                    });
                  }
                } else {
                  if (prev) {
                    statements.push({
                      sql: 'DELETE FROM source_private_kv_nodes WHERE installation_id=? AND profile_suffix=? AND entry_key=?',
                      params: [id, suffix, key],
                    });
                    if (prev.idx !== index) {
                      statements.push({
                        sql: 'UPDATE source_private_kv_entries SET position=? WHERE installation_id=? AND profile_suffix=? AND entry_key=?',
                        params: [index, id, suffix, key],
                      });
                    }
                  } else {
                    statements.push({
                      sql: 'INSERT INTO source_private_kv_entries(installation_id,profile_suffix,entry_key,position) VALUES(?,?,?,?)',
                      params: [id, suffix, key, index],
                    });
                  }
                  statements.push(...nodeInserts('source_private_kv_nodes', ['installation_id', 'profile_suffix', 'entry_key'], [id, suffix, key], encoded));
                }
              }
            }
          }
        }
      } else {
        for (const [k, v] of previous.kvNodes) nextKvNodes.set(k, v);
      }

      next.set(id, {
        input: {
          ...input,
          actions: input.actions && [...input.actions],
          ...(input.private !== undefined ? { private: cloneJSONValue(input.private) } : {}),
        },
        definitionText,
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
