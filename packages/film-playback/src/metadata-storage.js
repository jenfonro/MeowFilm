import { openDatabase } from '@meowfilm/storage';
import { upsert, nodeInserts } from '@meowfilm/storage/statements';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { isRawJSON, rawJSON } from '@meowfilm/runtime-bridge/json';
import { metadataKind } from './metadata-policy.js';

const textFields = ['title','name','original_title','original_name','overview','intro','status','release_date',
  'first_air_date','last_air_date','air_date','year','poster_path','backdrop_path','still_path','homepage','imdb_id',
  'original_language','media_type','type','character','job','department','known_for_department','profile_path',
  'gender_label','iso_639_1','iso_3166_1','english_name','file_path','key','site','size','aspect','uri','url','subtype',
  'card_subtitle','description','subject_id','episode_type','production_code','credit_id'];
const integerFields = ['runtime','season_number','episode_number','episode_count','number_of_seasons',
  'number_of_episodes','vote_count','budget','revenue','order','gender','width','height','page','total_pages',
  'total_results','count','max','start','limit'];
const realFields = ['vote_average','popularity','aspect_ratio','value','average','rating'];
const booleanFields = ['adult','video','in_production','official'];
const fields = [...textFields,...integerFields,...realFields,...booleanFields];
const relations = new Set(['genres','seasons','episodes','last_episode_to_air','next_episode_to_air','results',
  'items','subjects','subject','target','similar','recommendations','credits','cast','crew','guest_stars','created_by',
  'images','backdrops','posters','logos','stills','videos','external_ids','production_companies','production_countries',
  'spoken_languages','origin_country','languages','episode_run_time','networks','belongs_to_collection','rating',
  'pic','cover','avatar','photos','trailers','tags','directors','actors','writers','countries','pubdate','durations',
  'aka','doubanSeasons','content_ratings','release_dates','aggregate_credits','keywords','translations','watch/providers']);
const entityColumns = ['cache_key','slot_name','entity_id','entity_kind','id_kind','external_id','presence_low','presence_high',...fields.map(key => 'field_'+key)];
const fault = code => Object.assign(new Error(code),{code});
function scalar(value) {
  if (value === null) return ['null',null,null,null];
  if (typeof value === 'string') return ['text',value,null,null];
  if (typeof value === 'boolean') return ['boolean',null,Number(value),null];
  if (isRawJSON(value)) return ['number_text',value.rawJSON,null,null];
  if (typeof value === 'number' && Number.isFinite(value)) return ['real',null,null,value];
  throw fault('STORAGE_VALUE');
}
function fromScalar(row) {
  if (row.value_kind === 'null') return null;
  if (row.value_kind === 'text') return row.text_value;
  if (row.value_kind === 'boolean') return !!row.int_value;
  if (row.value_kind === 'number_text') return rawJSON(row.text_value);
  if (row.value_kind === 'real') return row.real_value;
  throw fault('STORAGE_VALUE');
}
function graph(cacheKey,slot,root) {
  const entities = [], relationRows = [], members = [], extensions = [];
  function visit(value,depth=0) {
    if (depth > 64 || entities.length > 16384) throw fault('STORAGE_RESULT_LIMIT');
    if (value === null || typeof value !== 'object' || isRawJSON(value)) throw fault('STORAGE_VALUE');
    const id = entities.length;
    let low = 0n, high = 0n;
    const values = fields.map((key,index) => {
      if (!Object.hasOwn(value,key) || relations.has(key) && typeof value[key] === 'object' && value[key] !== null) return null;
      if (index < 63) low |= 1n << BigInt(index); else high |= 1n << BigInt(index-63);
      const item = value[key];
      if (item === null) return null;
      if (textFields.includes(key)) {if (typeof item !== 'string') throw fault('STORAGE_VALUE');return item;}
      if (booleanFields.includes(key)) {if (typeof item !== 'boolean') throw fault('STORAGE_VALUE');return Number(item);}
      const number = isRawJSON(item) ? Number(item.rawJSON) : item;
      if (typeof number !== 'number' || !Number.isFinite(number) || integerFields.includes(key) && !Number.isSafeInteger(number))
        throw fault('STORAGE_VALUE');
      return number;
    });
    const nativeId = value.id;
    let idKind = null, externalId = null;
    if (Object.hasOwn(value,'id')) {
      if (nativeId === null) idKind = 'null';
      else if (typeof nativeId === 'string') {idKind='text';externalId=nativeId;}
      else if (isRawJSON(nativeId)) {idKind='number_text';externalId=nativeId.rawJSON;}
      else if (typeof nativeId === 'number' && Number.isFinite(nativeId)) {idKind='number';externalId=String(nativeId);}
      else throw fault('STORAGE_VALUE');
    }
    entities.push([cacheKey,slot,id,Array.isArray(value) ? 'array' : 'object',idKind,externalId,low,high,...values]);
    const properties = Array.isArray(value) ? [['items',value]] : Object.entries(value).filter(([key,item]) =>
      relations.has(key) && !(fields.includes(key) && typeof item !== 'object'));
    for (const [key,item] of properties) {
      if (!(item === null || typeof item === 'object' && !isRawJSON(item))) continue;
      relationRows.push([cacheKey,slot,id,key,item === null ? 'null' : Array.isArray(item) ? 'array' : 'object']);
      const list = item === null ? [] : Array.isArray(item) ? item : [item];
      list.forEach((child,index) => {
        if (child !== null && typeof child === 'object' && !isRawJSON(child))
          members.push([cacheKey,slot,id,key,index,visit(child,depth+1),null,null,null,null]);
        else members.push([cacheKey,slot,id,key,index,null,...scalar(child)]);
      });
    }
    if (!Array.isArray(value)) {
      const dynamic = Object.fromEntries(Object.entries(value).filter(([key,item]) => key !== 'id' &&
        !fields.includes(key) && !properties.some(([name]) => name === key) || relations.has(key) &&
          !fields.includes(key) && item !== null && typeof item !== 'object'));
      if (Object.keys(dynamic).length) extensions.push(...nodeInserts('metadata_cache_extensions',
        ['cache_key','slot_name','entity_id'],[cacheKey,slot,id],encodeValueNodes(dynamic)));
    }
    return id;
  }
  visit(root);
  return [
    ...entities.map(values => upsert('metadata_cache_entities',entityColumns,values,['cache_key','slot_name','entity_id'])),
    ...relationRows.map(values => upsert('metadata_cache_relations',['cache_key','slot_name','parent_id','relation_name','relation_kind'],values,
      ['cache_key','slot_name','parent_id','relation_name'])),
    ...members.map(values => upsert('metadata_cache_members',['cache_key','slot_name','parent_id','relation_name','position',
      'child_id','value_kind','text_value','int_value','real_value'],values,['cache_key','slot_name','parent_id','relation_name','position'])),
    ...extensions,
  ];
}

/** Known provider entities and ordered relations are columns/rows; only provider extensions use nodes. */
export function createMetadataStorage(dataRoot,now) {
  const database = openDatabase(dataRoot);
  async function read(key) {
    const [[entry],components,entities,links,members,extensions] = await database.snapshot([
      {sql:'SELECT * FROM metadata_cache_entries WHERE cache_key=?',params:[key],rows:true},
      ...['metadata_cache_components','metadata_cache_entities','metadata_cache_relations','metadata_cache_members','metadata_cache_extensions']
        .map(table => ({sql:`SELECT * FROM ${table} WHERE cache_key=?`,params:[key],rows:true,maxRows:65536})),
    ]);
    if (!entry) return undefined;
    const indexed = new Map(entities.map(row => [row.slot_name+':'+row.entity_id,row]));
    const grouped = rows => {
      const map = new Map();
      for (const row of rows) {
        const id = row.slot_name+':'+(row.entity_id ?? row.parent_id);
        const list = map.get(id) ?? [];list.push(row);map.set(id,list);
      }
      return map;
    };
    const extensionIndex = grouped(extensions), linkIndex = grouped(links), memberIndex = new Map();
    for (const row of members) {
      const id = row.slot_name+':'+row.parent_id+':'+row.relation_name;
      const list = memberIndex.get(id) ?? [];list.push(row);memberIndex.set(id,list);
    }
    function build(slot,id,depth=0) {
      if (depth > 64) throw fault('STORAGE_VALUE');
      const row = indexed.get(slot+':'+id);if (!row) throw fault('STORAGE_VALUE');
      const extra = extensionIndex.get(slot+':'+id) ?? [];
      const result = extra.length ? decodeValueNodes(extra,{rawNumbers:true}) : {};
      if (row.id_kind !== null) result.id = row.id_kind === 'null' ? null : row.id_kind === 'text' ? row.external_id :
        row.id_kind === 'number' ? Number(row.external_id) : rawJSON(row.external_id);
      fields.forEach((key,index) => {
        const mask = BigInt(index < 63 ? row.presence_low : row.presence_high);
        if (mask & 1n << BigInt(index < 63 ? index : index-63)) result[key] = booleanFields.includes(key) && row['field_'+key] !== null
          ? !!row['field_'+key] : row['field_'+key];
      });
      for (const link of linkIndex.get(slot+':'+id) ?? []) {
        const children = (memberIndex.get(slot+':'+id+':'+link.relation_name) ?? []).sort((a,b) => a.position-b.position).map(item =>
          item.child_id === null ? fromScalar(item) : build(slot,item.child_id,depth+1));
        result[link.relation_name] = link.relation_kind === 'null' ? null : link.relation_kind === 'array' ? children : children[0];
      }
      return row.entity_kind === 'array' ? result.items : result;
    }
    return {value:build('base',0),...(indexed.has('summary:0') ? {summaryValue:build('summary',0)} : {}),
      components:Object.fromEntries(components.map(row => [row.component_name,{status:row.status,
        ...(row.error_code === null ? {} : {error:{code:row.error_code}}),
        ...(row.fetched_at_ms === null ? {} : {fetchedAt:row.fetched_at_ms}),
        ...(row.expires_at_ms === null ? {} : {expiresAt:row.expires_at_ms})}])),
      ...(entry.parent_signature === null ? {} : {parentSignature:entry.parent_signature}),
      ...(entry.summary_parent_signature === null ? {} : {summaryParentSignature:entry.summary_parent_signature})};
  }
  async function save(key,state,estimatedBytes) {
    const request = state.request;
    const expires = Math.max(0,...Object.values(state.components).map(row => row.expiresAt ?? 0));
    const statements = [{sql:'DELETE FROM metadata_cache_entries WHERE cache_key=?',params:[key]},
      {sql:'INSERT INTO metadata_cache_entries VALUES(?,?,?,?,?,?,?,?,?,?,?)',params:[key,request.provider,request.path,
        metadataKind(request),request.settings.revision,request.imageSize ?? null,state.parentSignature ?? null,
        state.summaryParentSignature ?? null,now(),expires,estimatedBytes]},
      ...Object.entries(state.components).map(([name,row]) => ({sql:'INSERT INTO metadata_cache_components VALUES(?,?,?,?,?,?)',
        params:[key,name,row.status,row.error?.code ?? null,row.fetchedAt ?? null,row.expiresAt ?? null]})),
      ...graph(key,'base',state.value),...(state.summaryValue === undefined ? [] : graph(key,'summary',state.summaryValue)),
      {sql:`DELETE FROM metadata_cache_entries WHERE cache_key IN (
        SELECT cache_key FROM (SELECT cache_key,row_number() OVER (ORDER BY updated_at_ms DESC,cache_key) AS ordinal,
          sum(estimated_bytes) OVER (ORDER BY updated_at_ms DESC,cache_key) AS retained FROM metadata_cache_entries)
        WHERE ordinal>4096 OR retained>134217728)`},
    ];
    await database.transaction(statements);
  }
  return {ready:database.ready,read,save,
    remove: key => database.execute('DELETE FROM metadata_cache_entries WHERE cache_key=?',[key]),
    invalidate: provider => database.execute('DELETE FROM metadata_cache_entries WHERE provider=?',[provider]),
    close: () => database.close(),
  };
}
