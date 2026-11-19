import { randomUUID } from 'node:crypto';
import { openDatabase } from '@meowfilm/storage';
import { reconcile } from '@meowfilm/storage/statements';
import { defaultPreferences, normalizePreferences } from './policy.js';
import { cloneJSONValue } from '@meowfilm/runtime-bridge/json';
import { identity, checkTarget, isIdentifier, object, requireValue, text, fail } from './identity.js';

const optional = value => value ?? null;
const detailKind = value => typeof value === 'number' ? 'integer' : value == null ? null : 'text';
const detailText = value => value == null ? null : String(value);
const detailValue = (kind,value) => {
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
const preferenceLists = ['ruleOrder','panOrder','keywords','siteCleanKeywords'];
const historyColumns = ['target_id','title','source_key','detail_id_kind','detail_id_text',
  'line_id_kind','line_id_text','candidate_id','season','episode','global_episode','has_episode',
  'position_sec','duration_sec','paused','ended','played_at_ms','updated_at_ms','target_kind','target_title',
  'target_year_kind','target_year_text','target_metadata_provider','target_metadata_id_kind','target_metadata_id_text',
  'target_season','target_episode','target_global_episode','target_has_members','target_source_key','target_source_engine',
  'target_source_installation_id','target_source_site_kind','target_source_site_text','target_source_detail_kind',
  'target_source_detail_text','target_source_line_kind','target_source_line_text','target_source_candidate_id','row_revision'];
function fields(value, names) {
  requireValue(object(value) && Object.keys(value).every(key => names.includes(key)));
}
function validateHistory(value) {
  fields(value, ['targetId','title','sourceKey','detailId','lineId','candidateId','episode','position','duration',
    'paused','ended','playedAt','updatedAt','playbackTarget']);
  fields(value.playbackTarget, ['id','kind','title','year','metadataProvider','metadataId','season','episode','globalEpisode','source','members']);
  checkTarget(value.playbackTarget);
  requireValue(value.targetId === value.playbackTarget.id && text(value.title) && text(value.sourceKey) &&
    isIdentifier(value.detailId) && isIdentifier(value.lineId) && text(value.candidateId) &&
    Number.isFinite(value.position) && value.position >= 0 && Number.isFinite(value.duration) && value.duration >= 0 &&
    typeof value.paused === 'boolean' && typeof value.ended === 'boolean' && Number.isSafeInteger(value.updatedAt) &&
    (value.playedAt === undefined || Number.isSafeInteger(value.playedAt)));
  if (value.episode !== undefined) {
    fields(value.episode, ['season','episode','globalEpisode']);
    requireValue(Object.values(value.episode).every(item => Number.isSafeInteger(item) && item >= 0));
  }
  const target = value.playbackTarget;
  for (const key of ['metadataId','year']) if (target[key] !== undefined) requireValue(isIdentifier(target[key]));
  if (target.source !== undefined) {
    fields(target.source, ['key','engine','installationId','siteId','detailId','lineId','candidateId']);
    requireValue(text(target.source.key) && isIdentifier(target.source.detailId));
    for (const key of ['siteId','lineId']) if (target.source[key] !== undefined) requireValue(isIdentifier(target.source[key]));
  }
}
function historyValues(value,revision) {
  const target = value.playbackTarget, source = target.source ?? {}, episode = value.episode ?? {};
  return [value.targetId,value.title,value.sourceKey,detailKind(value.detailId),detailText(value.detailId),
    detailKind(value.lineId),detailText(value.lineId),value.candidateId,optional(episode.season),optional(episode.episode),
    optional(episode.globalEpisode),Number(value.episode !== undefined),
    value.position,value.duration,Number(value.paused),Number(value.ended),optional(value.playedAt),value.updatedAt,
    target.kind,target.title,detailKind(target.year),detailText(target.year),optional(target.metadataProvider),
    detailKind(target.metadataId),detailText(target.metadataId),optional(target.season),
    optional(target.episode),optional(target.globalEpisode),Number(target.members !== undefined),optional(source.key),optional(source.engine),
    optional(source.installationId),detailKind(source.siteId),detailText(source.siteId),detailKind(source.detailId),
    detailText(source.detailId),detailKind(source.lineId),detailText(source.lineId),optional(source.candidateId),revision];
}
function historyProjection(row,members) {
  const target = {id:row.target_id,kind:row.target_kind,title:row.target_title};
  for (const [field,column] of [['metadataProvider','target_metadata_provider'],
    ['season','target_season'],['episode','target_episode'],['globalEpisode','target_global_episode']])
    if (row[column] !== null) target[field] = row[column];
  for (const [field,prefix] of [['metadataId','target_metadata_id'],['year','target_year']])
    if (row[prefix+'_kind'] !== null) target[field] = detailValue(row[prefix+'_kind'],row[prefix+'_text']);
  if (row.target_source_key !== null) {
    target.source = {key:row.target_source_key};
    for (const [field,column] of [['engine','target_source_engine'],['installationId','target_source_installation_id'],
      ['candidateId','target_source_candidate_id']])
      if (row[column] !== null) target.source[field] = row[column];
    for (const [field,prefix] of [['siteId','target_source_site'],['lineId','target_source_line'],['detailId','target_source_detail']])
      if (row[prefix+'_kind'] !== null) target.source[field] = detailValue(row[prefix+'_kind'],row[prefix+'_text']);
  }
  if (row.target_has_members) target.members = members.map(item => ({sourceKey:item.source_key,
    detailId:detailValue(item.detail_id_kind,item.detail_id_text)}));
  const episode = {};
  for (const [field,column] of [['season','season'],['episode','episode'],['globalEpisode','global_episode']])
    if (row[column] !== null) episode[field] = row[column];
  const result = {targetId:row.target_id,title:row.title,playbackTarget:target,sourceKey:row.source_key,
    detailId:detailValue(row.detail_id_kind,row.detail_id_text),lineId:detailValue(row.line_id_kind,row.line_id_text),candidateId:row.candidate_id,
    ...(row.has_episode ? {episode} : {}),position:row.position_sec,duration:row.duration_sec,paused:!!row.paused,ended:!!row.ended,
    ...(row.played_at_ms === null ? {} : {playedAt:row.played_at_ms}),updatedAt:row.updated_at_ms};
  try { validateHistory(result); } catch { throw fail('CORRUPT_STORAGE'); }
  return result;
}
const manualColumns = ['match_id','position_order','target_id','match_kind','title','name','source_key',
  'detail_id_kind','detail_id_text','provider','share_url','share_password','season_hint','has_enabled','enabled',
  'has_auto_disable','auto_disable','failure_count','row_revision'];
const manualRows = rows => rows.map((row,index) => [row.id,index,row.targetId,optional(row.kind),optional(row.title),
  optional(row.name),optional(row.sourceKey),detailKind(row.detailId),detailText(row.detailId),optional(row.provider),
  optional(row.share?.url),optional(row.share?.password),optional(row.seasonHint),Number(row.enabled !== undefined),
  row.enabled === undefined ? null : Number(row.enabled),Number(row.autoDisable !== undefined),
  row.autoDisable === undefined ? null : Number(row.autoDisable),row.failureCount,1]);
const blockColumns = ['block_id','position_order','target_id','source_key','detail_id_kind','detail_id_text','line_id_kind','line_id_text'];
const blockRows = rows => rows.map((row,index) => [identity(row),index,row.targetId,row.sourceKey,
  detailKind(row.detailId),detailText(row.detailId),detailKind(row.lineId),detailText(row.lineId)]);
function validateControl(value, native) {
  fields(value, ['version','history','manual','blocks','previewTargets','groups', ...(native ? [] : ['preferences'])]);
  requireValue(value.version === 2 && ['history','manual','blocks','previewTargets','groups'].every(key =>
    Array.isArray(value[key])) && !value.history.length);
  const unique = (values, key) => requireValue(new Set(values.map(key)).size === values.length);
  for (const row of value.manual) {
    fields(row, ['id','targetId','kind','title','name','sourceKey','detailId','provider','share',
      'seasonHint','enabled','autoDisable','failureCount']);
    requireValue(text(row.id) && text(row.targetId) && Number.isSafeInteger(row.failureCount) && row.failureCount >= 0 &&
      (text(row.sourceKey) && isIdentifier(row.detailId) || text(row.provider) && object(row.share)));
    if (row.share !== undefined) {
      fields(row.share, ['url','password']);
      requireValue(typeof row.share.url === 'string' && (row.share.password === undefined || typeof row.share.password === 'string'));
    }
    for (const key of ['enabled','autoDisable']) requireValue(row[key] === undefined || typeof row[key] === 'boolean');
  }
  unique(value.manual, row => row.id);
  for (const row of value.blocks) {
    fields(row, ['targetId','sourceKey','detailId','lineId']);
    requireValue(text(row.targetId) && text(row.sourceKey) && isIdentifier(row.detailId) && isIdentifier(row.lineId));
  }
  unique(value.blocks, row => identity(row));
  requireValue(value.previewTargets.every(text)); unique(value.previewTargets, row => row);
  for (const row of value.groups) {
    fields(row, ['id','keys','members']);
    requireValue(/^group:[a-f0-9]{64}$/.test(row.id) && Array.isArray(row.keys) && Array.isArray(row.members) &&
      [...row.keys,...row.members].every(text));
    unique(row.keys, key => key); unique(row.members, key => key);
  }
  unique(value.groups, row => row.id);
  if (!native) {
    fields(value.preferences, [...preferenceLists,'iosAutoPrepare']);
    requireValue(typeof value.preferences.iosAutoPrepare === 'boolean' &&
      preferenceLists.every(key => Array.isArray(value.preferences[key]) && value.preferences[key].every(item => typeof item === 'string')));
  }
}
function relations(value) {
  return [
    ['film_manual_matches',manualColumns,['match_id'],manualRows(value.manual)],
    ['film_blocked_sources',blockColumns,['block_id'],blockRows(value.blocks)],
    ['film_preview_targets',['position_order','target_id'],['target_id'],value.previewTargets.map((id,index) => [index,id])],
    ['film_groups',['group_id','position_order'],['group_id'],value.groups.map((row,index) => [row.id,index])],
    ['film_group_keys',['group_id','position','aggregation_key'],['group_id','position'],value.groups.flatMap(row => row.keys.map((key,index) => [row.id,index,key]))],
    ['film_group_members',['group_id','position','member_identity'],['group_id','position'],value.groups.flatMap(row => row.members.map((key,index) => [row.id,index,key]))],
    ...(value.preferences ? [
      ['film_preferences_state',['singleton','ios_auto_prepare','row_revision'],['singleton'],[[1,Number(value.preferences.iosAutoPrepare),1]]],
      ['film_preference_items',['list_kind','position','item_value'],['list_kind','position'],preferenceLists.flatMap(key => value.preferences[key].map((item,index) => [key,index,item]))],
    ] : []),
  ];
}

/** Control state is small; history is queried by target or bounded window, never cloned on progress. */
export function createPlaybackLibrary({dataRoot,nativePreferences} = {}) {
  const database = openDatabase(dataRoot);
  let closed = false, editing = false, disposal, tail = Promise.resolve(), revision = 0;
  let committed = freeze({version:2,history:[],manual:[],blocks:[],previewTargets:[],groups:[],
    ...(nativePreferences ? {} : {preferences:defaultPreferences()})});
  const ready = (async () => {
    const [state,manual,blocks,preview,groups,keys,members,prefs,items] = await database.snapshot([
      {sql:"SELECT revision FROM owner_revisions WHERE owner='film.library'",rows:true},
      ...['film_manual_matches','film_blocked_sources'].map(table => ({sql:`SELECT * FROM ${table} ORDER BY position_order`,rows:true})),
      {sql:'SELECT * FROM film_preview_targets ORDER BY position_order',rows:true},
      {sql:'SELECT * FROM film_groups ORDER BY position_order',rows:true},
      ...['film_group_keys','film_group_members'].map(table => ({sql:`SELECT * FROM ${table} ORDER BY group_id,position`,rows:true})),
      {sql:'SELECT * FROM film_preferences_state WHERE singleton=1',rows:true},
      {sql:'SELECT * FROM film_preference_items ORDER BY list_kind,position',rows:true},
    ]);
    revision = state[0]?.revision ?? 0;
    const value = {version:2,history:[],manual:manual.map(row => {
      const item = {id:row.match_id,targetId:row.target_id,failureCount:row.failure_count};
      for (const [key,column] of [['kind','match_kind'],['title','title'],['name','name'],['sourceKey','source_key'],['provider','provider'],['seasonHint','season_hint']])
        if (row[column] !== null) item[key] = row[column];
      if (row.detail_id_kind !== null) item.detailId = detailValue(row.detail_id_kind,row.detail_id_text);
      if (row.share_url !== null) item.share = {url:row.share_url,...(row.share_password === null ? {} : {password:row.share_password})};
      if (row.has_enabled) item.enabled = !!row.enabled;
      if (row.has_auto_disable) item.autoDisable = !!row.auto_disable;
      return item;
    }),blocks:blocks.map(row => ({targetId:row.target_id,sourceKey:row.source_key,
      detailId:detailValue(row.detail_id_kind,row.detail_id_text),lineId:detailValue(row.line_id_kind,row.line_id_text)})),
    previewTargets:preview.map(row => row.target_id),groups:groups.map(row => ({id:row.group_id,
      keys:keys.filter(item => item.group_id === row.group_id).map(item => item.aggregation_key),
      members:members.filter(item => item.group_id === row.group_id).map(item => item.member_identity)}))};
    if (!nativePreferences) {
      value.preferences = defaultPreferences();
      if (prefs[0]) value.preferences.iosAutoPrepare = !!prefs[0].ios_auto_prepare;
      for (const key of preferenceLists) {
        const list = items.filter(row => row.list_kind === key);
        if (prefs[0]) value.preferences[key] = list.map(row => row.item_value);
      }
    }
    try { validateControl(value, !!nativePreferences); } catch { throw fail('CORRUPT_STORAGE'); }
    committed = freeze(value);
  })(); ready.catch(() => {});
  function open() { if (closed) throw Object.assign(new Error('CLOSED'),{code:'CLOSED'}); }
  function serialize(action) {
    open(); const work = tail.then(async () => {await ready;return action();}).catch(error => {throw failure(error);});
    tail = work.catch(() => {}); return work;
  }
  function snapshot() {return nativePreferences ? freeze({...committed,preferences:normalizePreferences(nativePreferences())}) : committed;}
  function read() {open();return snapshot();}
  function history(targetId) { return serialize(async () => {
    const [[row],members] = await database.snapshot([
      {sql:'SELECT * FROM film_history WHERE target_id=?',params:[targetId],rows:true},
      {sql:'SELECT * FROM film_history_target_members WHERE target_id=? ORDER BY position',params:[targetId],rows:true},
    ]);
    return row ? historyProjection(row,members) : undefined;
  }); }
  return {ready,read,history,configurationOwner:nativePreferences ? 'native' : 'runtime',
    update(mutator) {
      if (editing || typeof mutator !== 'function' || mutator.constructor?.name === 'AsyncFunction')
        throw Object.assign(new Error('INVALID_REQUEST'),{code:'INVALID_REQUEST'});
      return serialize(async () => {
        const draft = cloneJSONValue(committed);
        let result;
        editing = true;
        try { result = mutator(draft); } finally { editing = false; }
        if (result?.then) throw fail('INVALID_REQUEST');
        validateControl(draft, !!nativePreferences);
        const next = cloneJSONValue(draft), previous = relations(committed), proposed = relations(next);
        const changes = proposed.flatMap(([table,columns,keys,rows],index) => reconcile(table,columns,keys,previous[index][3],rows));
        if (!changes.length) return snapshot();
        const gate = revision ? {sql:"UPDATE owner_revisions SET revision=revision+1,receipt=? WHERE owner='film.library' AND revision=?",params:[randomUUID(),revision],expectChanges:1}
          : {sql:'INSERT INTO owner_revisions VALUES(?,?,?)',params:['film.library',1,randomUUID()],expectChanges:1};
        try { await database.transaction([gate,...changes]); }
        catch (error) {
          let current;
          try { [current] = await database.query("SELECT revision FROM owner_revisions WHERE owner='film.library'"); }
          catch { throw error; }
          if ((current?.revision ?? 0) !== revision) throw fail('STORAGE_CHANGED');
          throw error;
        }
        ++revision;committed = freeze(next);return snapshot();
      });
    },
    saveHistory(value) {return serialize(async () => {
      validateHistory(value);
      const [old] = await database.query('SELECT row_revision FROM film_history WHERE target_id=?',[value.targetId]);
      const values = historyValues(value,(old?.row_revision ?? 0)+1);
      const statement = old ? {sql:`UPDATE film_history SET ${historyColumns.slice(1).map(key => key+'=?').join(',')} WHERE target_id=? AND row_revision=?`,
        params:[...values.slice(1),value.targetId,old.row_revision],expectChanges:1} : {
        sql:`INSERT INTO film_history(${historyColumns.join(',')}) VALUES(${values.map(() => '?').join(',')})`,params:values,expectChanges:1};
      const members = value.playbackTarget.members ?? [];
      await database.transaction([statement,{sql:'DELETE FROM film_history_target_members WHERE target_id=?',params:[value.targetId]},
        ...members.map((row,index) => ({sql:'INSERT INTO film_history_target_members VALUES(?,?,?,?,?)',
          params:[value.targetId,index,row.sourceKey,detailKind(row.detailId),detailText(row.detailId)]}))]);
    });},
    removeHistory(targetId) {return serialize(() => database.execute('DELETE FROM film_history' +
      (targetId == null ? '' : ' WHERE target_id=?'),targetId == null ? [] : [targetId]));},
    list({limit=100,historyAfter} = {}) { return serialize(async () => {
      if (!Number.isSafeInteger(limit) || limit < 1 || limit > 500 || historyAfter &&
          (!Number.isSafeInteger(historyAfter.updatedAt) || typeof historyAfter.targetId !== 'string'))
        throw Object.assign(new Error('INVALID_REQUEST'),{code:'INVALID_REQUEST'});
      const window = (historyAfter ? ' WHERE (updated_at_ms,target_id)<(?,?)' : '') +
        ' ORDER BY updated_at_ms DESC,target_id DESC LIMIT ?';
      const params = historyAfter ? [historyAfter.updatedAt,historyAfter.targetId] : [];
      const [rows,members] = await database.snapshot([
        {sql:'SELECT * FROM film_history'+window, params:[...params,limit+1], rows:true},
        {sql:'SELECT m.* FROM film_history_target_members m JOIN (SELECT target_id FROM film_history'+window+
          ') f USING(target_id) ORDER BY m.target_id,m.position',params:[...params,limit],rows:true},
      ]);
      const selected = rows.slice(0,limit);
      const last = selected.at(-1);
      return {...snapshot(),history:selected.map(row => historyProjection(row,members.filter(item => item.target_id === row.target_id))),
        historyAfter:rows.length > limit ? {updatedAt:last.updated_at_ms,targetId:last.target_id} : null};
    }); },
    close() {return disposal ??= (async () => {
      closed=true;await Promise.allSettled([ready,tail]);await database.close();
    })();},
  };
}
