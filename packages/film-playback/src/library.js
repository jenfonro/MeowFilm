import { isDeepStrictEqual } from 'node:util';
import { openDatabase } from '@meowfilm/storage';
import { upsert } from '@meowfilm/storage/statements';
import { defaultPreferences } from './policy.js';
import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { identity, fields, freeze, isIdentifier, normalizeWorkKey, object, requireValue, text, fail, workTargetId } from './identity.js';

const OWNER = 'film.library';
const optional = value => value ?? null;
const detailKind = value => typeof value === 'number' ? 'integer' : value == null ? null : 'text';
const detailText = value => value == null ? null : String(value);
const detailValue = (kind, value) => {
  requireValue(kind === null && value === null || kind === 'text' && typeof value === 'string' ||
    kind === 'integer' && /^-?(?:0|[1-9][0-9]*)$/.test(value) && Number.isSafeInteger(Number(value)), 'CORRUPT_STORAGE');
  return kind === 'integer' ? Number(value) : value;
};
const failure = error => fail(error?.code === 'STORAGE_CONFLICT' ? 'STORAGE_CHANGED' :
  error?.code === 'STORAGE_CLOSED' ? 'CLOSED' :
    ['CORRUPT_STORAGE', 'STORAGE_SCHEMA', 'STORAGE_CORRUPT'].includes(error?.code) ? 'CORRUPT_STORAGE' :
      ['STORAGE_BUSY', 'STORAGE_FULL', 'STORAGE_READONLY', 'STORAGE_IO',
        'STORAGE_ERROR', 'STORAGE_RESULT_LIMIT'].includes(error?.code) ? 'STORAGE_ERROR' :
        ['INVALID_REQUEST', 'STORAGE_CHANGED'].includes(error?.code)
          ? error.code : 'LIBRARY_UPDATE_FAILED');

const preferenceLists = ['ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords'];
const historyColumns = [
  'target_id', 'keyword', 'title', 'poster', 'content_kind', 'year',
  'tmdb_kind', 'tmdb_id', 'douban_kind', 'douban_id',
  'source_key', 'detail_id_kind', 'detail_id_text', 'line_id_kind', 'line_id_text', 'line_label',
  'share_provider', 'share_url', 'share_password',
  'candidate_id', 'file_name',
  'season', 'episode', 'global_episode', 'has_episode',
  'position_sec', 'duration_sec', 'paused', 'ended', 'played_at_ms', 'updated_at_ms', 'row_revision',
];

function historyInput(value) {
  requireValue(object(value));
  const keyword = text(value.keyword) || text(value.title);
  const targetId = text(value.targetId) || workTargetId(keyword);
  const result = {
    targetId,
    keyword,
    title: text(value.title) || keyword,
    poster: typeof value.poster === 'string' ? value.poster.trim() : '',
    ...(value.kind === 'movie' || value.kind === 'tv' ? { kind: value.kind } : {}),
    ...(Number.isSafeInteger(value.year) && value.year > 0 ? { year: value.year } : {}),
    ...(object(value.tmdb) && ['movie', 'tv'].includes(value.tmdb.kind) && Number.isSafeInteger(value.tmdb.id) && value.tmdb.id > 0
      ? { tmdb: { kind: value.tmdb.kind, id: value.tmdb.id } } : {}),
    ...(object(value.douban) && ['movie', 'tv'].includes(value.douban.kind) && text(value.douban.id)
      ? { douban: { kind: value.douban.kind, id: text(value.douban.id) } } : {}),
    sourceKey: text(value.sourceKey),
    detailId: value.detailId,
    lineId: value.lineId,
    lineLabel: typeof value.lineLabel === 'string' ? value.lineLabel.trim() : '',
    ...(object(value.share) && text(value.share.provider) && text(value.share.url)
      ? { share: { provider: text(value.share.provider), url: text(value.share.url),
        ...(typeof value.share.password === 'string' && value.share.password ? { password: value.share.password } : {}) } } : {}),
    candidateId: text(value.candidateId),
    fileName: typeof value.fileName === 'string' ? value.fileName.trim() : '',
    ...(object(value.episode) ? { episode: fields(value.episode, ['season', 'episode', 'globalEpisode']) } : {}),
    position: value.position,
    duration: value.duration,
    paused: value.paused,
    ended: value.ended,
    ...(value.playedAt === undefined ? {} : { playedAt: value.playedAt }),
    updatedAt: value.updatedAt,
  };
  return result;
}

function validateHistory(value) {
  requireValue(text(value.targetId) && text(value.keyword) && text(value.title) &&
    text(value.sourceKey) && isIdentifier(value.detailId) && isIdentifier(value.lineId) &&
    text(value.candidateId) && Number.isFinite(value.position) && value.position >= 0 &&
    Number.isFinite(value.duration) && value.duration >= 0 &&
    typeof value.paused === 'boolean' && typeof value.ended === 'boolean' &&
    Number.isSafeInteger(value.updatedAt) &&
    (value.playedAt === undefined || Number.isSafeInteger(value.playedAt)));
  if (value.episode !== undefined) {
    requireValue(object(value.episode) && ['season', 'episode', 'globalEpisode'].every(key =>
      value.episode[key] === undefined || Number.isSafeInteger(value.episode[key]) && value.episode[key] >= 0));
  }
}

function historyValues(value, revision) {
  const episode = value.episode ?? {};
  return [
    value.targetId, value.keyword, value.title, value.poster ?? '',
    optional(value.kind), optional(value.year),
    optional(value.tmdb?.kind), optional(value.tmdb?.id),
    optional(value.douban?.kind), optional(value.douban?.id),
    value.sourceKey, detailKind(value.detailId), detailText(value.detailId),
    detailKind(value.lineId), detailText(value.lineId), value.lineLabel ?? '',
    optional(value.share?.provider), optional(value.share?.url), optional(value.share?.password),
    value.candidateId, value.fileName ?? '',
    optional(episode.season), optional(episode.episode), optional(episode.globalEpisode),
    Number(value.episode !== undefined && Object.keys(episode).length > 0),
    value.position, value.duration, Number(value.paused), Number(value.ended),
    optional(value.playedAt), value.updatedAt, revision,
  ];
}

function historyProjection(row) {
  const episode = {};
  for (const [field, column] of [['season', 'season'], ['episode', 'episode'], ['globalEpisode', 'global_episode']]) {
    if (row[column] !== null) episode[field] = row[column];
  }
  const result = {
    targetId: row.target_id,
    keyword: row.keyword,
    title: row.title,
    poster: row.poster ?? '',
    ...(row.content_kind === null ? {} : { kind: row.content_kind }),
    ...(row.year === null ? {} : { year: row.year }),
    ...(row.tmdb_kind !== null && row.tmdb_id !== null ? { tmdb: { kind: row.tmdb_kind, id: row.tmdb_id } } : {}),
    ...(row.douban_kind !== null && row.douban_id !== null ? { douban: { kind: row.douban_kind, id: row.douban_id } } : {}),
    sourceKey: row.source_key,
    detailId: detailValue(row.detail_id_kind, row.detail_id_text),
    lineId: detailValue(row.line_id_kind, row.line_id_text),
    lineLabel: row.line_label ?? '',
    ...(row.share_provider !== null && row.share_url !== null ? {
      share: {
        provider: row.share_provider,
        url: row.share_url,
        ...(row.share_password === null ? {} : { password: row.share_password }),
      },
    } : {}),
    candidateId: row.candidate_id,
    fileName: row.file_name ?? '',
    ...(row.has_episode ? { episode } : {}),
    position: row.position_sec,
    duration: row.duration_sec,
    paused: !!row.paused,
    ended: !!row.ended,
    ...(row.played_at_ms === null ? {} : { playedAt: row.played_at_ms }),
    updatedAt: row.updated_at_ms,
  };
  try { validateHistory(result); } catch { throw fail('CORRUPT_STORAGE'); }
  return result;
}

const manualColumns = ['match_id', 'position_order', 'target_id', 'match_kind', 'title', 'name', 'source_key',
  'detail_id_kind', 'detail_id_text', 'provider', 'share_url', 'share_password', 'season_hint', 'enabled',
  'auto_disable', 'failure_count'];
const manualRowValues = (row, position) => [
  row.id, position, row.targetId, optional(row.kind), optional(row.title),
  optional(row.name), optional(row.sourceKey), detailKind(row.detailId), detailText(row.detailId), optional(row.provider),
  optional(row.share?.url), optional(row.share?.password), optional(row.seasonHint),
  row.enabled === undefined ? null : Number(row.enabled),
  row.autoDisable === undefined ? null : Number(row.autoDisable), row.failureCount,
];
const blockColumns = ['block_id', 'position_order', 'target_id', 'source_key', 'detail_id_kind', 'detail_id_text', 'line_id_kind', 'line_id_text'];
const blockRowValues = (row, position) => [
  identity(fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId'])), position, row.targetId, row.sourceKey,
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
    isIdentifier(row.detailId) && (row.lineId === undefined || row.lineId === null || isIdentifier(row.lineId)), code);
}

function controlInput(value, previous, native) {
  requireValue(object(value) && value.version === 1 && ['history', 'manual', 'blocks', 'previewTargets'].every(key =>
    Array.isArray(value[key])) && !value.history.length && (!native || value.preferences === undefined));
  const unique = (values, key) => requireValue(new Set(values.map(key)).size === values.length);
  const result = {
    version: 1,
    history: [],
    manual: value.manual.map(row => {
      requireValue(object(row));
      if (row.errorCode && row.id !== undefined) {
        const old = previous.manual.find(before => before.id === row.id && before.errorCode);
        if (old) return old;
      }
      const item = fields(row, manualFields);
      if (item.share !== undefined) item.share = fields(item.share, ['url', 'password']);
      const old = previous.manual.find(before => !before.errorCode &&
        isDeepStrictEqual((() => {
          const projected = fields(before, manualFields);
          if (projected.share !== undefined) projected.share = fields(projected.share, ['url', 'password']);
          return projected;
        })(), item));
      if (old) return old;
      validateManual(item);
      return item;
    }),
    blocks: value.blocks.map(row => {
      requireValue(object(row));
      if (row.id !== undefined) {
        const old = previous.blocks.find(before => before.id === row.id);
        if (old) return old;
      }
      const item = fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId']);
      if (item.lineId === null) delete item.lineId;
      const old = previous.blocks.find(before => !before.errorCode &&
        isDeepStrictEqual(fields(before, ['targetId', 'sourceKey', 'detailId', 'lineId']), item));
      if (old) return old;
      validateBlock(item);
      return item;
    }),
    previewTargets: value.previewTargets,
  };
  unique(result.manual, row => row.id);
  unique(result.blocks, row => row.id ?? identity(fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId'])));
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

function buildMutations(previous, next) {
  const changes = [];

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
    const keyOf = row => row.id ?? identity(fields(row, ['targetId', 'sourceKey', 'detailId', 'lineId']));
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

  if (next.preferences && !isDeepStrictEqual(previous.preferences, next.preferences)) {
    changes.push(upsert(
      'film_preferences',
      ['singleton', 'ios_auto_prepare', 'metadata_view_provider'],
      [1, Number(next.preferences.iosAutoPrepare), next.preferences.metadataViewProvider],
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

  return changes;
}

/** Control state is small; history is queried by unified keyword targetId or bounded window. */
export function createPlaybackLibrary({ dataRoot, nativePreferences } = {}) {
  const database = openDatabase(dataRoot);
  let closed = false, editing = false, disposal, tail = Promise.resolve(), revision = 0;
  let committed = freeze({
    version: 1, history: [], manual: [], blocks: [], previewTargets: [],
    ...(nativePreferences ? {} : { preferences: defaultPreferences() }),
  });

  const ready = (async () => {
    const [state, manual, blocks, preview, prefs, items] = await database.snapshot([
      { sql: 'SELECT revision FROM owner_revisions WHERE owner=?', params: [OWNER], rows: true },
      { sql: `SELECT ${manualColumns.join(',')} FROM film_manual_matches ORDER BY position_order`, rows: true },
      { sql: `SELECT ${blockColumns.join(',')} FROM film_blocked_sources ORDER BY position_order`, rows: true },
      { sql: 'SELECT position_order,target_id FROM film_preview_targets ORDER BY position_order', rows: true },
      { sql: 'SELECT ios_auto_prepare,metadata_view_provider FROM film_preferences WHERE singleton=1', rows: true },
      { sql: 'SELECT list_kind,position,item_value FROM film_preference_items ORDER BY list_kind,position', rows: true },
    ]);
    revision = state[0]?.revision ?? 0;
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
        const item = { id: row.block_id, targetId: row.target_id, sourceKey: row.source_key };
        try {
          item.detailId = detailValue(row.detail_id_kind, row.detail_id_text);
          const lineId = detailValue(row.line_id_kind, row.line_id_text);
          if (lineId !== null) item.lineId = lineId;
        } catch {
          item.errorCode = 'CORRUPT_STORAGE';
        }
        return item;
      }),
      previewTargets: preview.map(row => row.target_id),
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
  function history(targetIdOrKeyword) {
    const id = text(targetIdOrKeyword).startsWith('work:')
      ? text(targetIdOrKeyword)
      : workTargetId(targetIdOrKeyword);
    return serialize(async () => {
      const [row] = await database.query(`SELECT ${historyColumns.join(',')} FROM film_history WHERE target_id=?`, [id]);
      return row ? historyProjection(row) : undefined;
    });
  }

  return {
    ready, read, history, configurationOwner: nativePreferences ? 'native' : 'runtime',
    normalizeTargetId(targetIdOrKeyword) {
      const raw = text(targetIdOrKeyword);
      requireValue(raw.length > 0);
      return raw.startsWith('work:') ? `work:${normalizeWorkKey(raw.slice(5))}` : workTargetId(raw);
    },
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
        const changes = buildMutations(committed, next);
        if (!changes.length) return snapshot();
        const gate = revision
          ? { sql: 'UPDATE owner_revisions SET revision=revision+1 WHERE owner=? AND revision=?', params: [OWNER, revision], expectChanges: 1 }
          : { sql: 'INSERT INTO owner_revisions(owner,revision) VALUES(?,1) ON CONFLICT(owner) DO NOTHING', params: [OWNER], expectChanges: 1 };
        try { await database.transaction([gate, ...changes]); }
        catch (error) {
          let current;
          try { [current] = await database.query('SELECT revision FROM owner_revisions WHERE owner=?', [OWNER]); }
          catch { throw error; }
          if ((current?.revision ?? 0) !== revision) throw fail('STORAGE_CHANGED');
          throw error;
        }
        ++revision;
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
            sql: `INSERT INTO film_history(${historyColumns.join(',')}) VALUES(${values.map(() => '?').join(',')}) ON CONFLICT(target_id) DO NOTHING`,
            params: values,
            expectChanges: 1,
          };
        await database.transaction([statement]);
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
        const rows = await database.query(`SELECT ${historyColumns.join(',')} FROM film_history` + window, [...params, limit + 1]);
        const selected = rows.slice(0, limit);
        const last = selected.at(-1);
        return {
          ...snapshot(),
          history: selected.map(row => {
            try {
              return historyProjection(row);
            } catch (error) {
              if (error?.code !== 'CORRUPT_STORAGE') throw error;
              return { id: row.target_id, targetId: row.target_id, errorCode: 'CORRUPT_STORAGE' };
            }
          }),
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
