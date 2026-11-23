import { isDeepStrictEqual } from 'node:util';
import { openDatabase } from '@meowfilm/storage';
import { upsert } from '@meowfilm/storage/statements';
import { defaultPreferences } from './policy.js';
import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { identity, checkTarget, isIdentifier, object, requireValue, text, fail } from './identity.js';

const OWNER = 'film.library';
const optional = value => value ?? null;
const detailKind = value => typeof value === 'number' ? 'integer' : value == null ? null : 'text';
const detailText = value => value == null ? null : String(value);
const detailValue = (kind, value) => {
  requireValue(kind === null && value === null || kind === 'text' && typeof value === 'string' ||
    kind === 'integer' && /^-?(?:0|[1-9][0-9]*)$/.test(value) && Number.isSafeInteger(Number(value)), 'CORRUPT_STORAGE');
  return kind === 'integer' ? Number(value) : value;
};
const freeze = value => {
  if (value && typeof value === 'object') { for (const item of Object.values(value)) freeze(item); Object.freeze(value); }
  return value;
};
const failure = error => fail(error?.code === 'STORAGE_CONFLICT' ? 'STORAGE_CHANGED' :
  error?.code === 'STORAGE_CLOSED' ? 'CLOSED' :
    ['INVALID_REQUEST', 'CORRUPT_STORAGE', 'STORAGE_BUSY', 'STORAGE_FULL', 'STORAGE_READONLY',
      'STORAGE_CORRUPT', 'STORAGE_IO', 'STORAGE_ERROR', 'STORAGE_RESULT_LIMIT', 'STORAGE_CHANGED'].includes(error?.code)
      ? error.code : 'LIBRARY_UPDATE_FAILED');
const preferenceLists = ['ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords'];
const historyColumns = ['target_id', 'title', 'source_key', 'detail_id_kind', 'detail_id_text',
  'line_id_kind', 'line_id_text', 'candidate_id', 'season', 'episode', 'global_episode', 'has_episode',
  'position_sec', 'duration_sec', 'paused', 'ended', 'played_at_ms', 'updated_at_ms', 'target_kind', 'target_title',
  'target_year_kind', 'target_year_text', 'target_metadata_provider', 'target_metadata_id_kind', 'target_metadata_id_text',
  'target_season', 'target_episode', 'target_global_episode', 'target_has_members', 'target_source_key', 'target_source_engine',
  'target_source_installation_id', 'target_source_site_kind', 'target_source_site_text', 'target_source_detail_kind',
  'target_source_detail_text', 'target_source_line_kind', 'target_source_line_text', 'target_source_candidate_id', 'row_revision'];

function fields(value, names) {
  requireValue(object(value));
  return Object.fromEntries(names.filter(key => value[key] !== undefined).map(key => [key, value[key]]));
}
function historyInput(value) {
  const result = fields(value, ['targetId', 'title', 'sourceKey', 'detailId', 'lineId', 'candidateId', 'episode', 'position', 'duration',
    'paused', 'ended', 'playedAt', 'updatedAt', 'playbackTarget']);
  result.playbackTarget = fields(result.playbackTarget,
    ['id', 'kind', 'title', 'year', 'metadataProvider', 'metadataId', 'season', 'episode', 'globalEpisode', 'source', 'members']);
  if (result.episode !== undefined) result.episode = fields(result.episode, ['season', 'episode', 'globalEpisode']);
  if (result.playbackTarget.source !== undefined) result.playbackTarget.source = fields(result.playbackTarget.source,
    ['key', 'engine', 'installationId', 'siteId', 'detailId', 'lineId', 'candidateId']);
  if (result.playbackTarget.members !== undefined) {
    requireValue(Array.isArray(result.playbackTarget.members));
    result.playbackTarget.members = result.playbackTarget.members.map(row => fields(row, ['sourceKey', 'detailId']));
  }
  return result;
}
function validateHistory(value) {
  checkTarget(value.playbackTarget);
  requireValue(value.targetId === value.playbackTarget.id && text(value.title) && text(value.sourceKey) &&
    isIdentifier(value.detailId) && isIdentifier(value.lineId) && text(value.candidateId) &&
    Number.isFinite(value.position) && value.position >= 0 && Number.isFinite(value.duration) && value.duration >= 0 &&
    typeof value.paused === 'boolean' && typeof value.ended === 'boolean' && Number.isSafeInteger(value.updatedAt) &&
    (value.playedAt === undefined || Number.isSafeInteger(value.playedAt)));
  if (value.episode !== undefined) requireValue(object(value.episode) && ['season', 'episode', 'globalEpisode'].every(key =>
    value.episode[key] === undefined || Number.isSafeInteger(value.episode[key]) && value.episode[key] >= 0));
  const target = value.playbackTarget;
  for (const key of ['metadataId', 'year']) if (target[key] !== undefined) requireValue(isIdentifier(target[key]));
  if (target.source !== undefined) {
    requireValue(text(target.source.key) && isIdentifier(target.source.detailId));
    for (const key of ['siteId', 'lineId']) if (target.source[key] !== undefined) requireValue(isIdentifier(target.source[key]));
  }
}
function historyValues(value, revision) {
  const target = value.playbackTarget, source = target.source ?? {}, episode = value.episode ?? {};
  return [value.targetId, value.title, value.sourceKey, detailKind(value.detailId), detailText(value.detailId),
    detailKind(value.lineId), detailText(value.lineId), value.candidateId, optional(episode.season), optional(episode.episode),
    optional(episode.globalEpisode), Number(value.episode !== undefined),
    value.position, value.duration, Number(value.paused), Number(value.ended), optional(value.playedAt), value.updatedAt,
    target.kind, target.title, detailKind(target.year), detailText(target.year), optional(target.metadataProvider),
    detailKind(target.metadataId), detailText(target.metadataId), optional(target.season),
    optional(target.episode), optional(target.globalEpisode), Number(target.members !== undefined), optional(source.key), optional(source.engine),
    optional(source.installationId), detailKind(source.siteId), detailText(source.siteId), detailKind(source.detailId),
    detailText(source.detailId), detailKind(source.lineId), detailText(source.lineId), optional(source.candidateId), revision];
}
function historyProjection(row, members) {
  const target = { id: row.target_id, kind: row.target_kind, title: row.target_title };
  for (const [field, column] of [['metadataProvider', 'target_metadata_provider'],
    ['season', 'target_season'], ['episode', 'target_episode'], ['globalEpisode', 'target_global_episode']])
    if (row[column] !== null) target[field] = row[column];
  for (const [field, prefix] of [['metadataId', 'target_metadata_id'], ['year', 'target_year']])
    if (row[prefix + '_kind'] !== null) target[field] = detailValue(row[prefix + '_kind'], row[prefix + '_text']);
  if (row.target_source_key !== null) {
    target.source = { key: row.target_source_key };
    for (const [field, column] of [['engine', 'target_source_engine'], ['installationId', 'target_source_installation_id'],
      ['candidateId', 'target_source_candidate_id']])
      if (row[column] !== null) target.source[field] = row[column];
    for (const [field, prefix] of [['siteId', 'target_source_site'], ['lineId', 'target_source_line'], ['detailId', 'target_source_detail']])
      if (row[prefix + '_kind'] !== null) target.source[field] = detailValue(row[prefix + '_kind'], row[prefix + '_text']);
  }
  if (row.target_has_members) target.members = members.map(item => ({
    sourceKey: item.source_key,
    detailId: detailValue(item.detail_id_kind, item.detail_id_text),
  }));
  const episode = {};
  for (const [field, column] of [['season', 'season'], ['episode', 'episode'], ['globalEpisode', 'global_episode']])
    if (row[column] !== null) episode[field] = row[column];
  const result = {
    targetId: row.target_id, title: row.title, playbackTarget: target, sourceKey: row.source_key,
    detailId: detailValue(row.detail_id_kind, row.detail_id_text), lineId: detailValue(row.line_id_kind, row.line_id_text), candidateId: row.candidate_id,
    ...(row.has_episode ? { episode } : {}), position: row.position_sec, duration: row.duration_sec, paused: !!row.paused, ended: !!row.ended,
    ...(row.played_at_ms === null ? {} : { playedAt: row.played_at_ms }), updatedAt: row.updated_at_ms,
  };
  try { validateHistory(result); } catch { throw fail('CORRUPT_STORAGE'); }
  return result;
}
const manualColumns = ['match_id', 'position_order', 'target_id', 'match_kind', 'title', 'name', 'source_key',
  'detail_id_kind', 'detail_id_text', 'provider', 'share_url', 'share_password', 'season_hint', 'enabled',
  'auto_disable', 'failure_count', 'row_revision'];
const manualRowValues = (row, position) => [
  row.id, position, row.targetId, optional(row.kind), optional(row.title),
  optional(row.name), optional(row.sourceKey), detailKind(row.detailId), detailText(row.detailId), optional(row.provider),
  optional(row.share?.url), optional(row.share?.password), optional(row.seasonHint),
  row.enabled === undefined ? null : Number(row.enabled),
  row.autoDisable === undefined ? null : Number(row.autoDisable), row.failureCount, 1,
];
const blockColumns = ['block_id', 'position_order', 'target_id', 'source_key', 'detail_id_kind', 'detail_id_text', 'line_id_kind', 'line_id_text'];
const blockRowValues = (row, position) => [
  identity(row), position, row.targetId, row.sourceKey,
  detailKind(row.detailId), detailText(row.detailId), detailKind(row.lineId), detailText(row.lineId),
];
const manualFields = ['id', 'targetId', 'kind', 'title', 'name', 'sourceKey', 'detailId', 'provider', 'share',
  'seasonHint', 'enabled', 'autoDisable', 'failureCount'];

export function validateManual(row, code = 'INVALID_REQUEST') {
  requireValue(object(row) && text(row.id) && text(row.targetId) && !row.errorCode &&
    Number.isSafeInteger(row.failureCount) && row.failureCount >= 0 &&
    (text(row.sourceKey) && isIdentifier(row.detailId) || text(row.provider) && object(row.share)), code);
  if (row.share !== undefined) {
    requireValue(object(row.share) && typeof row.share.url === 'string' &&
      (row.share.password === undefined || typeof row.share.password === 'string'), code);
    let url;
    try { url = new URL(row.share.url); } catch { throw fail(code); }
    requireValue(['http:', 'https:'].includes(url.protocol) && !url.username && !url.password, code);
  }
  for (const key of ['enabled', 'autoDisable']) requireValue(row[key] === undefined || typeof row[key] === 'boolean', code);
  requireValue(row.seasonHint === undefined || Number.isSafeInteger(row.seasonHint) && row.seasonHint >= 0, code);
}
export function validateBlock(row, code = 'INVALID_REQUEST') {
  requireValue(object(row) && !row.errorCode && text(row.targetId) && text(row.sourceKey) &&
    isIdentifier(row.detailId) && isIdentifier(row.lineId), code);
}
function controlInput(value, previous, native) {
  requireValue(object(value) && value.version === 1 && ['history', 'manual', 'blocks', 'previewTargets', 'groups'].every(key =>
    Array.isArray(value[key])) && !value.history.length && (!native || value.preferences === undefined));
  const projectRows = (name, project, validate) => value[name].map(row => {
    const item = project(row);
    const old = previous[name].find(before => isDeepStrictEqual(project(before), item));
    if (old) return old;
    validate(item); return item;
  });
  const unique = (values, key) => requireValue(new Set(values.map(key)).size === values.length);
  const result = {
    version: 1, history: [],
    manual: projectRows('manual', row => {
      const item = fields(row, manualFields);
      if (item.share !== undefined) item.share = fields(item.share, ['url', 'password']);
      return item;
    }, validateManual),
    blocks: projectRows('blocks', row => fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId']), validateBlock),
    previewTargets: value.previewTargets,
    groups: projectRows('groups', row => fields(row, ['id', 'keys', 'members']), row => {
      requireValue(/^group:[a-f0-9]{64}$/.test(row.id) && Array.isArray(row.keys) && Array.isArray(row.members) &&
        [...row.keys, ...row.members].every(text));
      unique(row.keys, key => key); unique(row.members, key => key);
    }),
  };
  unique(result.manual, row => row.id); unique(result.blocks, row => row.id ?? identity(row)); unique(result.groups, row => row.id);
  for (const id of result.previewTargets) if (!previous.previewTargets.includes(id)) requireValue(text(id));
  unique(result.previewTargets, row => row);
  if (!native) {
    result.preferences = fields(value.preferences, [...preferenceLists, 'iosAutoPrepare', 'metadataViewProvider']);
    if (!isDeepStrictEqual(result.preferences, previous.preferences)) requireValue(
      ['tmdb', 'douban'].includes(result.preferences.metadataViewProvider) && typeof result.preferences.iosAutoPrepare === 'boolean' &&
      preferenceLists.every(key => Array.isArray(result.preferences[key]) && result.preferences[key].every(item => typeof item === 'string')));
  }
  return result;
}

function buildMutations(previous, next, prefRowRevision) {
  const changes = [];
  let nextPrefRevision = prefRowRevision;

  if (!isDeepStrictEqual(previous.manual, next.manual)) {
    const oldMap = new Map(previous.manual.map((row, idx) => [row.id, { row, idx }]));
    const nextIds = new Set(next.manual.map(row => row.id));
    for (const id of oldMap.keys()) {
      if (!nextIds.has(id)) changes.push({ sql: 'DELETE FROM film_manual_matches WHERE match_id=?', params: [id] });
    }
    for (let i = 0; i < next.manual.length; i++) {
      const row = next.manual[i];
      const old = oldMap.get(row.id);
      if (old && old.idx === i && isDeepStrictEqual(old.row, row)) continue;
      if (old && isDeepStrictEqual(old.row, row)) {
        changes.push({ sql: 'UPDATE film_manual_matches SET position_order=? WHERE match_id=?', params: [i, row.id] });
      } else {
        changes.push(upsert('film_manual_matches', manualColumns, manualRowValues(row, i), ['match_id']));
      }
    }
  }

  if (!isDeepStrictEqual(previous.blocks, next.blocks)) {
    const keyOf = row => row.id ?? identity(row);
    const oldMap = new Map(previous.blocks.map((row, idx) => [keyOf(row), { row, idx }]));
    const nextIds = new Set(next.blocks.map(keyOf));
    for (const id of oldMap.keys()) {
      if (!nextIds.has(id)) changes.push({ sql: 'DELETE FROM film_blocked_sources WHERE block_id=?', params: [id] });
    }
    for (let i = 0; i < next.blocks.length; i++) {
      const row = next.blocks[i];
      const id = keyOf(row);
      const old = oldMap.get(id);
      if (old && old.idx === i && isDeepStrictEqual(old.row, row)) continue;
      if (old && isDeepStrictEqual(old.row, row)) {
        changes.push({ sql: 'UPDATE film_blocked_sources SET position_order=? WHERE block_id=?', params: [i, id] });
      } else {
        changes.push(upsert('film_blocked_sources', blockColumns, blockRowValues(row, i), ['block_id']));
      }
    }
  }

  if (!isDeepStrictEqual(previous.previewTargets, next.previewTargets)) {
    const oldPos = new Map(previous.previewTargets.map((id, idx) => [id, idx]));
    const nextIds = new Set(next.previewTargets);
    for (const id of oldPos.keys()) {
      if (!nextIds.has(id)) changes.push({ sql: 'DELETE FROM film_preview_targets WHERE target_id=?', params: [id] });
    }
    for (let i = 0; i < next.previewTargets.length; i++) {
      const id = next.previewTargets[i];
      if (oldPos.get(id) === i) continue;
      changes.push(upsert('film_preview_targets', ['position_order', 'target_id'], [i, id], ['target_id']));
    }
  }

  if (!isDeepStrictEqual(previous.groups, next.groups)) {
    const oldMap = new Map(previous.groups.map((row, idx) => [row.id, { row, idx }]));
    const nextIds = new Set(next.groups.map(row => row.id));
    for (const id of oldMap.keys()) {
      if (!nextIds.has(id)) changes.push({ sql: 'DELETE FROM film_groups WHERE group_id=?', params: [id] });
    }
    for (let i = 0; i < next.groups.length; i++) {
      const group = next.groups[i];
      const old = oldMap.get(group.id);
      if (!old || old.idx !== i) {
        changes.push(upsert('film_groups', ['group_id', 'position_order'], [group.id, i], ['group_id']));
      }
      if (!old || !isDeepStrictEqual(old.row.keys, group.keys)) {
        if (old) changes.push({ sql: 'DELETE FROM film_group_keys WHERE group_id=?', params: [group.id] });
        for (let k = 0; k < group.keys.length; k++) {
          changes.push({
            sql: 'INSERT INTO film_group_keys(group_id,position,aggregation_key) VALUES(?,?,?)',
            params: [group.id, k, group.keys[k]],
          });
        }
      }
      if (!old || !isDeepStrictEqual(old.row.members, group.members)) {
        if (old) changes.push({ sql: 'DELETE FROM film_group_members WHERE group_id=?', params: [group.id] });
        for (let m = 0; m < group.members.length; m++) {
          changes.push({
            sql: 'INSERT INTO film_group_members(group_id,position,member_identity) VALUES(?,?,?)',
            params: [group.id, m, group.members[m]],
          });
        }
      }
    }
  }

  if (next.preferences && !isDeepStrictEqual(previous.preferences, next.preferences)) {
    nextPrefRevision = prefRowRevision + 1;
    changes.push(upsert(
      'film_preferences',
      ['singleton', 'ios_auto_prepare', 'metadata_view_provider', 'row_revision'],
      [1, Number(next.preferences.iosAutoPrepare), next.preferences.metadataViewProvider, nextPrefRevision],
      ['singleton'],
    ));
    for (const kind of preferenceLists) {
      const oldList = previous.preferences?.[kind] ?? [];
      const nextList = next.preferences[kind];
      if (isDeepStrictEqual(oldList, nextList)) continue;
      if (nextList.length < oldList.length) {
        changes.push({ sql: 'DELETE FROM film_preference_items WHERE list_kind=? AND position>=?', params: [kind, nextList.length] });
      }
      for (let i = 0; i < nextList.length; i++) {
        if (i < oldList.length && oldList[i] === nextList[i]) continue;
        changes.push(upsert('film_preference_items', ['list_kind', 'position', 'item_value'], [kind, i, nextList[i]], ['list_kind', 'position']));
      }
    }
  }

  return { changes, nextPrefRevision };
}

/** Control state is small; history is queried by target or bounded window, never cloned on progress. */
export function createPlaybackLibrary({ dataRoot, nativePreferences } = {}) {
  const database = openDatabase(dataRoot);
  let closed = false, editing = false, disposal, tail = Promise.resolve(), revision = 0, prefRowRevision = 0;
  let committed = freeze({
    version: 1, history: [], manual: [], blocks: [], previewTargets: [], groups: [],
    ...(nativePreferences ? {} : { preferences: defaultPreferences() }),
  });

  const ready = (async () => {
    const [state, manual, blocks, preview, groups, keys, members, prefs, items] = await database.snapshot([
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [OWNER], rows: true },
      { sql: `SELECT ${manualColumns.join(',')} FROM film_manual_matches ORDER BY position_order`, rows: true },
      { sql: `SELECT ${blockColumns.join(',')} FROM film_blocked_sources ORDER BY position_order`, rows: true },
      { sql: 'SELECT position_order,target_id FROM film_preview_targets ORDER BY position_order', rows: true },
      { sql: 'SELECT group_id,position_order FROM film_groups ORDER BY position_order', rows: true },
      { sql: 'SELECT group_id,position,aggregation_key FROM film_group_keys ORDER BY group_id,position', rows: true },
      { sql: 'SELECT group_id,position,member_identity FROM film_group_members ORDER BY group_id,position', rows: true },
      { sql: 'SELECT ios_auto_prepare,metadata_view_provider,row_revision FROM film_preferences WHERE singleton=1', rows: true },
      { sql: 'SELECT list_kind,position,item_value FROM film_preference_items ORDER BY list_kind,position', rows: true },
    ]);
    revision = state[0]?.revision ?? 0;
    prefRowRevision = prefs[0]?.row_revision ?? 0;
    const keysByGroup = new Map();
    for (const row of keys) {
      let list = keysByGroup.get(row.group_id);
      if (!list) keysByGroup.set(row.group_id, list = []);
      list.push(row.aggregation_key);
    }
    const membersByGroup = new Map();
    for (const row of members) {
      let list = membersByGroup.get(row.group_id);
      if (!list) membersByGroup.set(row.group_id, list = []);
      list.push(row.member_identity);
    }
    const value = {
      version: 1,
      history: [],
      manual: manual.map(row => {
        const item = { id: row.match_id, targetId: row.target_id, failureCount: row.failure_count };
        for (const [key, column] of [['kind', 'match_kind'], ['title', 'title'], ['name', 'name'], ['sourceKey', 'source_key'], ['provider', 'provider'], ['seasonHint', 'season_hint']])
          if (row[column] !== null) item[key] = row[column];
        if (row.detail_id_kind !== null) {
          try { item.detailId = detailValue(row.detail_id_kind, row.detail_id_text); }
          catch { item.errorCode = 'CORRUPT_STORAGE'; }
        }
        if (row.share_url !== null) item.share = { url: row.share_url, ...(row.share_password === null ? {} : { password: row.share_password }) };
        if (row.enabled !== null) item.enabled = !!row.enabled;
        if (row.auto_disable !== null) item.autoDisable = !!row.auto_disable;
        return item;
      }),
      blocks: blocks.map(row => {
        const item = { targetId: row.target_id, sourceKey: row.source_key };
        try { item.detailId = detailValue(row.detail_id_kind, row.detail_id_text); item.lineId = detailValue(row.line_id_kind, row.line_id_text); }
        catch { item.id = row.block_id; item.errorCode = 'CORRUPT_STORAGE'; }
        return item;
      }),
      previewTargets: preview.map(row => row.target_id),
      groups: groups.map(row => ({
        id: row.group_id,
        keys: keysByGroup.get(row.group_id) ?? [],
        members: membersByGroup.get(row.group_id) ?? [],
      })),
    };
    if (!nativePreferences) {
      value.preferences = defaultPreferences();
      if (prefs[0]) {
        value.preferences.iosAutoPrepare = !!prefs[0].ios_auto_prepare;
        value.preferences.metadataViewProvider = prefs[0].metadata_view_provider;
        for (const key of preferenceLists) {
          value.preferences[key] = items.filter(row => row.list_kind === key).map(row => row.item_value);
        }
      }
    }
    committed = freeze(value);
  })();
  ready.catch(() => {});

  function open() { if (closed) throw Object.assign(new Error('CLOSED'), { code: 'CLOSED' }); }
  function serialize(action) {
    open();
    const work = tail.then(async () => { await ready; return action(); }).catch(error => { throw failure(error); });
    tail = work.catch(() => {});
    return work;
  }
  function snapshot() {
    if (!nativePreferences) return committed;
    const preferences = nativePreferences();
    requireValue(object(preferences), 'CORRUPT_STORAGE');
    return freeze({ ...committed, preferences: cloneJSONValue(preferences) });
  }
  function read() { open(); return snapshot(); }
  function history(targetId) {
    return serialize(async () => {
      const [[row], members] = await database.snapshot([
        { sql: `SELECT ${historyColumns.join(',')} FROM film_history WHERE target_id=?`, params: [targetId], rows: true },
        { sql: 'SELECT source_key,detail_id_kind,detail_id_text FROM film_history_target_members WHERE target_id=? ORDER BY position', params: [targetId], rows: true },
      ]);
      return row ? historyProjection(row, members) : undefined;
    });
  }

  return {
    ready, read, history, configurationOwner: nativePreferences ? 'native' : 'runtime',
    update(mutator) {
      if (editing || typeof mutator !== 'function' || mutator.constructor?.name === 'AsyncFunction')
        throw Object.assign(new Error('INVALID_REQUEST'), { code: 'INVALID_REQUEST' });
      return serialize(async () => {
        const draft = cloneJSONValue(committed);
        let result;
        editing = true;
        try { result = mutator(draft); } finally { editing = false; }
        if (result?.then) throw fail('INVALID_REQUEST');
        const next = cloneJSONValue(controlInput(draft, committed, !!nativePreferences));
        const { changes, nextPrefRevision } = buildMutations(committed, next, prefRowRevision);
        if (!changes.length) return snapshot();
        const gate = revision
          ? { sql: 'UPDATE owner_revisions SET revision=revision+1 WHERE owner=? AND revision=?', params: [OWNER, revision], expectChanges: 1 }
          : { sql: 'INSERT INTO owner_revisions(owner,revision) VALUES(?,1)', params: [OWNER], expectChanges: 1 };
        try { await database.transaction([gate, ...changes]); }
        catch (error) {
          let current;
          try { [current] = await database.query('SELECT revision FROM owner_revisions WHERE owner=?', [OWNER]); }
          catch { throw error; }
          if ((current?.revision ?? 0) !== revision) throw fail('STORAGE_CHANGED');
          throw error;
        }
        ++revision;
        prefRowRevision = nextPrefRevision;
        committed = freeze(next);
        return snapshot();
      });
    },
    saveHistory(value) {
      return serialize(async () => {
        value = historyInput(value);
        validateHistory(value);
        const [old] = await database.query('SELECT row_revision FROM film_history WHERE target_id=?', [value.targetId]);
        const values = historyValues(value, (old?.row_revision ?? 0) + 1);
        const statement = old
          ? {
            sql: `UPDATE film_history SET ${historyColumns.slice(1).map(key => key + '=?').join(',')} WHERE target_id=? AND row_revision=?`,
            params: [...values.slice(1), value.targetId, old.row_revision],
            expectChanges: 1,
          }
          : {
            sql: `INSERT INTO film_history(${historyColumns.join(',')}) VALUES(${values.map(() => '?').join(',')})`,
            params: values,
            expectChanges: 1,
          };
        const members = value.playbackTarget.members ?? [];
        await database.transaction([
          statement,
          { sql: 'DELETE FROM film_history_target_members WHERE target_id=?', params: [value.targetId] },
          ...members.map((row, index) => ({
            sql: 'INSERT INTO film_history_target_members(target_id,position,source_key,detail_id_kind,detail_id_text) VALUES(?,?,?,?,?)',
            params: [value.targetId, index, row.sourceKey, detailKind(row.detailId), detailText(row.detailId)],
          })),
        ]);
      });
    },
    removeHistory(targetId) {
      return serialize(() => database.execute('DELETE FROM film_history' +
        (targetId == null ? '' : ' WHERE target_id=?'), targetId == null ? [] : [targetId]));
    },
    list({ limit = 100, historyAfter } = {}) {
      return serialize(async () => {
        if (!Number.isSafeInteger(limit) || limit < 1 || limit > 500 || historyAfter &&
            (!Number.isSafeInteger(historyAfter.updatedAt) || typeof historyAfter.targetId !== 'string'))
          throw Object.assign(new Error('INVALID_REQUEST'), { code: 'INVALID_REQUEST' });
        const window = (historyAfter ? ' WHERE (updated_at_ms,target_id)<(?,?)' : '') +
          ' ORDER BY updated_at_ms DESC,target_id DESC LIMIT ?';
        const params = historyAfter ? [historyAfter.updatedAt, historyAfter.targetId] : [];
        const [rows, members] = await database.snapshot([
          { sql: `SELECT ${historyColumns.join(',')} FROM film_history` + window, params: [...params, limit + 1], rows: true },
          {
            sql: 'SELECT m.target_id,m.source_key,m.detail_id_kind,m.detail_id_text FROM film_history_target_members m JOIN (SELECT target_id FROM film_history' +
              window + ') f USING(target_id) ORDER BY m.target_id,m.position',
            params: [...params, limit],
            rows: true,
          },
        ]);
        const selected = rows.slice(0, limit);
        const last = selected.at(-1);
        return {
          ...snapshot(),
          history: selected.map(row => historyProjection(row, members.filter(item => item.target_id === row.target_id))),
          historyAfter: rows.length > limit ? { updatedAt: last.updated_at_ms, targetId: last.target_id } : null,
        };
      });
    },
    close() {
      return disposal ??= (async () => {
        closed = true;
        await Promise.allSettled([ready, tail]);
        await database.close();
      })();
    },
  };
}
