import { openDatabase } from '@meowfilm/storage';
import { reconcile, nodeInserts } from '@meowfilm/storage/statements';
import { encodeValueNodes, decodeValueNodes } from '@meowfilm/storage/values';
import { stringifyJSONValue } from '@meowfilm/runtime-bridge/json';

const versionKeys = ['contentHash','bytes','version','author','description','homepage'];
const versionColumns = ['hash','bytes','version','author','description','homepage'];
const catalogColumns = ['installation_id','position','name','enabled','origin_kind','origin_url',
  ...['current','previous'].flatMap(prefix => versionColumns.map(key => prefix+'_'+key)),
  'declared_hash','check_hash','checked_at_ms','check_failure'];
function catalogBatch(statements) {
  const result = [];
  for (let index = 0; index < statements.length;) {
    const current = statements[index++];
    const match = /^(INSERT INTO \w+\([\w,]+\) VALUES)(\([?,]+\))(.*)$/.exec(current.sql);
    if (!match) { result.push(current); continue; }
    const params = [...current.params], groups = [match[2]];
    while (index < statements.length && statements[index].sql === current.sql &&
        params.length + statements[index].params.length <= 500) {
      params.push(...statements[index++].params); groups.push(match[2]);
    }
    result.push({sql:match[1]+groups.join(',')+match[3],params});
  }
  return result;
}
function relations(catalog) {
  const rows = catalog.installations;
  return [
    ['music_script_installations',catalogColumns,['installation_id'],rows.map((row,index) => [row.id,index,row.name,
      Number(row.enabled),row.origin.kind,row.origin.url ?? null,...['current','previous'].flatMap(prefix =>
        versionKeys.map(key => row[prefix]?.[key] ?? null)),row.declared?.contentHash ?? null,
        row.check?.contentHash ?? null,row.check?.checkedAt ?? null,row.check?.failure ?? null])],
    ['music_script_declared_sites',['installation_id','site_id','position','has_qualities'],['installation_id','site_id'],
      rows.flatMap(row => Object.entries(row.declared?.capabilities ?? {}).map(([site,value],index) =>
        [row.id,site,index,Number(value.qualitys !== undefined)]))],
    ...[['actions','music_script_declared_actions','action'],['qualitys','music_script_declared_qualities','quality']]
      .map(([field,table,column]) => [table,['installation_id','site_id','position',column],['installation_id','site_id','position'],
        rows.flatMap(row => Object.entries(row.declared?.capabilities ?? {}).flatMap(([site,value]) =>
          (value[field] ?? []).map((item,index) => [row.id,site,index,item])))]),
    ['music_script_declared_nodes',['installation_id','site_id','node_id','parent_id','prop_key','item_index','kind',
      'bool_value','int_value','real_value','text_value'],['installation_id','site_id','node_id'],
      rows.flatMap(row => Object.entries(row.declared?.capabilities ?? {}).flatMap(([site,value]) =>
        encodeValueNodes(Object.fromEntries(Object.entries(value).filter(([key]) => !['actions','qualitys'].includes(key))))
          .map(node => [row.id,site,node.nodeId,node.parentId,node.propKey,node.itemIndex,node.kind,
            node.boolValue,node.intValue,node.realValue,node.textValue])))],
    ['music_script_checks',['installation_id','site_id','state','quality','sample','code'],['installation_id','site_id'],
      rows.flatMap(row => Object.entries(row.check?.sites ?? {}).map(([site,value]) =>
        [row.id,site,value.state,value.quality,value.sample,value.code]))],
  ];
}

export function createMusicStorage(dataRoot) {
  const database = openDatabase(dataRoot);
  let previous = {schema:1,revision:0,installations:[]};
  async function load() {
    const [state,installations,sites,actions,qualities,checks,nodes] = await database.snapshot([
      {sql:'SELECT revision FROM music_script_catalog_state WHERE singleton=1',rows:true},
      {sql:'SELECT * FROM music_script_installations ORDER BY position',rows:true},
      ...['music_script_declared_sites','music_script_declared_actions','music_script_declared_qualities']
        .map(table => ({sql:`SELECT * FROM ${table} ORDER BY installation_id,site_id,position`,rows:true})),
      {sql:'SELECT * FROM music_script_checks',rows:true},
      {sql:'SELECT * FROM music_script_declared_nodes ORDER BY installation_id,site_id,node_id',rows:true,maxRows:Number.MAX_SAFE_INTEGER},
    ]);
    return previous = {schema:1,revision:state[0]?.revision ?? 0,installations:installations.map(row => {
      const version = prefix => Object.fromEntries(versionKeys.map((key,index) => [key,row[prefix+'_'+versionColumns[index]]]));
      const capabilities = Object.fromEntries(sites.filter(item => item.installation_id === row.installation_id).map(site => [site.site_id,{
        actions:actions.filter(item => item.installation_id === row.installation_id && item.site_id === site.site_id).map(item => item.action),
        ...(site.has_qualities ? {qualitys:qualities.filter(item => item.installation_id === row.installation_id && item.site_id === site.site_id).map(item => item.quality)} : {}),
        ...decodeValueNodes(nodes.filter(item => item.installation_id === row.installation_id && item.site_id === site.site_id),{rawNumbers:true}),
      }]));
      return {id:row.installation_id,name:row.name,enabled:!!row.enabled,
        origin:row.origin_kind === 'file' ? {kind:'file'} : {kind:'network',url:row.origin_url},
        current:version('current'),previous:row.previous_hash === null ? null : version('previous'),
        declared:row.declared_hash === null ? null : {contentHash:row.declared_hash,capabilities},
        check:row.check_hash === null ? null : {contentHash:row.check_hash,checkedAt:row.checked_at_ms,failure:row.check_failure,
          sites:Object.fromEntries(checks.filter(item => item.installation_id === row.installation_id).map(item => [item.site_id,
            {state:item.state,quality:item.quality,sample:item.sample,code:item.code}]))}};
    })};
  }
  async function save(next) {
    const old = relations(previous), proposed = relations(next);
    const gate = previous.revision ? {sql:'UPDATE music_script_catalog_state SET revision=? WHERE singleton=1 AND revision=?',
      params:[next.revision,previous.revision],expectChanges:1} : {sql:'INSERT INTO music_script_catalog_state VALUES(1,?)',params:[next.revision],expectChanges:1};
    const changes = proposed.flatMap(([table,columns,keys,rows],index) => reconcile(table,columns,keys,old[index][3],rows));
    const removed = previous.installations.filter(row => !next.installations.some(item => item.id === row.id));
    await database.transaction([gate,...removed.map(row => ({sql:'DELETE FROM music_script_kv_entries WHERE installation_id=?',params:[row.id]})),...catalogBatch(changes)]);
    previous = next;
  }
  const queues = new Map();
  function kv(operation,input,encodedLength,check) {
    const id = input.installationId;
    const work = (queues.get(id) ?? Promise.resolve()).then(async () => {
      check();
      const scope = [id,input.siteId,input.key];
      const where = 'installation_id=? AND site_id=? AND entry_key=?';
      if (operation === 'get') {
        const nodes = await database.query('SELECT * FROM music_script_kv_nodes WHERE '+where+' ORDER BY node_id',scope,
          {signal:input.signal,maxRows:Number.MAX_SAFE_INTEGER});
        check();return decodeValueNodes(nodes,{rawNumbers:true}) ?? null;
      }
      decodeValueNodes(await database.query('SELECT * FROM music_script_kv_nodes WHERE '+where+' ORDER BY node_id',scope,
        {signal:input.signal,maxRows:Number.MAX_SAFE_INTEGER}),{rawNumbers:true});
      const existing = await database.query('SELECT site_id,entry_key,encoded_bytes,position FROM music_script_kv_entries WHERE installation_id=?',[id]);
      const others = existing.filter(row => row.site_id !== input.siteId || row.entry_key !== input.key);
      const siteRows = others.filter(row => row.site_id === input.siteId);
      const bytes = operation === 'set' ? encodedLength(input.value) : 0;
      const changes = [{sql:'DELETE FROM music_script_kv_entries WHERE '+where,params:scope}];
      if (operation === 'set') {
        const position = Math.max(-1,...siteRows.map(row => row.position))+1;
        changes.push({sql:'INSERT INTO music_script_kv_entries VALUES(?,?,?,?,?)',params:[...scope,position,bytes]},
          ...nodeInserts('music_script_kv_nodes',['installation_id','site_id','entry_key'],scope,encodeValueNodes(input.value)));
      }
      check();await database.transaction(changes,{signal:input.signal});return null;
    });
    const observed = work.catch(() => {});queues.set(id,observed);
    observed.finally(() => {if (queues.get(id) === observed) queues.delete(id);});return work;
  }
  return {load,save,kv,async close() {await Promise.allSettled([...queues.values()]);await database.close();}};
}
