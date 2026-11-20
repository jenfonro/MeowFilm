import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import { resolve } from 'node:path';
import { openDatabase } from '@meowfilm/storage';
import { createPlaybackLibrary } from '../packages/film-playback/src/library.js';
import { defaultPreferences } from '../packages/film-playback/src/policy.js';
import './storage-test-fixture.mjs';

async function fixture(t, options = {}) {
  const base = resolve('tmp/test-results/storage-audit');
  await mkdir(base, {recursive:true});
  const root = await mkdtemp(base + '/library-'), owners = [];
  const database = openDatabase(root);
  async function open() {
    const owner = createPlaybackLibrary({dataRoot:root,...options}); owners.push(owner);
    await owner.ready; return owner;
  }
  const owner = await open();
  t.after(async () => {
    try { for (const item of owners) await item.close(); await database.close(); }
    finally { await rm(root, {recursive:true,force:true}); }
  });
  return {owner,database,open};
}
const history = (index = 0) => ({
  targetId:'tmdb:tv:'+index,title:'合成剧集',sourceKey:'cat:installation:site',detailId:0,lineId:0,
  candidateId:'candidate:exact-file',episode:{season:1,episode:2,globalEpisode:2},position:12.5,duration:600,
  paused:false,ended:false,playedAt:1000,updatedAt:1200+index,
  playbackTarget:{id:'tmdb:tv:'+index,kind:'source',title:'合成剧集',year:2024,season:1,episode:2,globalEpisode:2,
    source:{key:'cat:installation:site',detailId:0,lineId:0,siteId:0}},
});
const manual = () => ({id:'manual:one',targetId:'tmdb:tv:0',kind:'site',sourceKey:'cat:installation:site',
  detailId:0,seasonHint:1,enabled:true,autoDisable:true,failureCount:0});
const block = () => ({targetId:'tmdb:tv:0',sourceKey:'cat:installation:site',detailId:0,lineId:0});

test('current defaults are immutable and do not create a saved control revision', async t => {
  const {owner,database} = await fixture(t);
  assert.deepEqual(owner.read(), {version:2,preferences:defaultPreferences(),history:[],manual:[],blocks:[],previewTargets:[],groups:[]});
  assert.ok(Object.isFrozen(owner.read().preferences.keywords));
  assert.throws(() => owner.read().manual.push(manual()), TypeError);
  assert.equal((await database.query("SELECT count(*) AS n FROM owner_revisions WHERE owner='film.library'"))[0].n,0);
});

test('native preferences have one owner and business writes never persist a second copy', async t => {
  let preferences = {...defaultPreferences(),keywords:['native-first']};
  const {owner,database,open} = await fixture(t,{nativePreferences:() => preferences});
  await owner.saveHistory(history());
  await owner.update(draft => {draft.manual.push(manual());});
  assert.equal((await database.query('SELECT count(*) AS n FROM film_preferences_state'))[0].n,0);
  assert.deepEqual(owner.read().preferences.keywords,['native-first']);
  preferences = {...preferences,keywords:['native-latest']};
  assert.deepEqual(owner.read().preferences.keywords,['native-latest']);
  await owner.close(); const restarted = await open();
  assert.deepEqual(await restarted.history(history().targetId),history());
  assert.deepEqual(restarted.read().manual,[manual()]);
  assert.deepEqual(restarted.read().preferences.keywords,['native-latest']);
  await assert.rejects(restarted.update(draft => {draft.preferences = defaultPreferences();}),{code:'INVALID_REQUEST'});
});

test('publication follows a real commit and never retains a caller-owned draft', async t => {
  const {owner} = await fixture(t); const before = owner.read(); let held;
  const pending = owner.update(draft => {assert.equal(owner.read(),before); held=draft; draft.blocks.push(block());});
  const after = await pending;
  assert.equal(owner.read(),after); assert.notEqual(after,held);
  held.blocks[0].lineId='not persisted';
  assert.deepEqual(after.blocks,[block()]);
});

test('control edits and point history writes reopen with exact identifier types and progress', async t => {
  const {owner,open} = await fixture(t);
  await owner.update(draft => {
    draft.preferences.keywords=['DDP','H.265']; draft.preferences.siteCleanKeywords=[]; draft.preferences.iosAutoPrepare=false;
    draft.manual.push(manual(),{id:'manual:share',targetId:'tmdb:tv:0',kind:'share',provider:'quark',
      share:{url:'https://fixture.invalid/s/one',password:'synthetic-only'},seasonHint:0,enabled:false,autoDisable:false,failureCount:3});
    draft.blocks.push(block()); draft.previewTargets.push('tmdb:tv:0');
  });
  const entry=history(); await owner.saveHistory(entry);
  await owner.saveHistory({...entry,position:123.75,paused:true,updatedAt:1400});
  await owner.close(); const restarted = await open();
  assert.deepEqual(await restarted.history(entry.targetId),{...entry,position:123.75,paused:true,updatedAt:1400});
  assert.deepEqual(restarted.read().preferences.keywords,['DDP','H.265']);
  assert.deepEqual(restarted.read().preferences.siteCleanKeywords,[]);
  assert.equal(restarted.read().manual[1].share.password,'synthetic-only');
  assert.deepEqual(restarted.read().blocks,[block()]);
  assert.deepEqual(restarted.read().history,[], 'progress is not materialized into the control snapshot');
});

test('movie metadata IDs and omitted episode material survive without coercion', async t => {
  const {owner} = await fixture(t); const entry=history(); delete entry.episode;
  entry.playbackTarget={id:entry.targetId,kind:'movie',title:entry.title,metadataProvider:'tmdb',metadataId:42};
  await owner.saveHistory(entry); assert.deepEqual(await owner.history(entry.targetId),entry);
  entry.playbackTarget.metadataId='42'; await owner.saveHistory(entry);
  assert.equal((await owner.history(entry.targetId)).playbackTarget.metadataId,'42');
});

test('history uses stable bounded keyset windows and removal does not rewrite controls', async t => {
  const {owner,database} = await fixture(t);
  await owner.update(draft => {draft.manual.push(manual());});
  const revision=(await database.query("SELECT revision FROM owner_revisions WHERE owner='film.library'"))[0].revision;
  for (let index=0;index<650;index++) await owner.saveHistory(history(index));
  const first=await owner.list({limit:500}),second=await owner.list({limit:500,historyAfter:first.historyAfter});
  assert.equal(first.history.length,500); assert.equal(second.history.length,150); assert.equal(second.historyAfter,null);
  assert.equal(new Set([...first.history,...second.history].map(row=>row.targetId)).size,650);
  await owner.removeHistory(first.history[0].targetId); assert.equal(await owner.history(first.history[0].targetId),undefined);
  assert.equal((await database.query("SELECT revision FROM owner_revisions WHERE owner='film.library'"))[0].revision,revision);
  await owner.removeHistory(); assert.deepEqual((await owner.list()).history,[]); assert.deepEqual(owner.read().manual,[manual()]);
});

test('read-only storage failure cannot publish an edit and a retry uses the latest healthy state', async t => {
  const {owner,database} = await fixture(t); await owner.update(draft=>{draft.blocks.push(block());}); const before=owner.read();
  await database.query('PRAGMA query_only=ON');
  await assert.rejects(owner.update(draft=>{draft.blocks=[];}),{code:'STORAGE_READONLY'});
  assert.equal(owner.read(),before); await database.query('PRAGMA query_only=OFF');
  await owner.update(draft=>{draft.blocks=[];}); assert.deepEqual(owner.read().blocks,[]);
});

test('independent control owners detect stale revisions without overwriting another commit', async t => {
  const {owner,open} = await fixture(t); const second=await open();
  await owner.update(draft=>{draft.manual.push(manual());});
  await assert.rejects(second.update(draft=>{draft.blocks.push(block());}),{code:'STORAGE_CHANGED'});
  assert.deepEqual(second.read().blocks,[]); await owner.close(); await second.close();
  const reopened=await open(); assert.deepEqual(reopened.read().manual,[manual()]); assert.deepEqual(reopened.read().blocks,[]);
});

test('invalid, asynchronous and reentrant edits never commit and diagnostics contain no input', async t => {
  const {owner} = await fixture(t); const before=owner.read();
  assert.throws(()=>owner.update(async ()=>{}),{code:'INVALID_REQUEST'});
  await assert.rejects(owner.update(()=>Promise.resolve()),{code:'INVALID_REQUEST'});
  await assert.rejects(owner.update(()=>owner.update(()=>{})),{code:'INVALID_REQUEST'});
  await assert.rejects(owner.update(draft=>{draft.manual.push({...manual(),enabled:'not a boolean'});}),{code:'INVALID_REQUEST'});
  await assert.rejects(owner.update(()=>{throw new Error('SYNTHETIC_PRIVATE_DIAGNOSTIC');}),error=>{
    assert.equal(error.code,'LIBRARY_UPDATE_FAILED'); assert.equal(error.cause,undefined);
    assert.equal(error.stack.includes('SYNTHETIC_PRIVATE_DIAGNOSTIC'),false); return true;
  });
  assert.equal(owner.read(),before);
});

test('close drains an already admitted control write and rejects new work', async t => {
  const {owner,open} = await fixture(t); const pending=owner.update(draft=>{draft.previewTargets.push('tmdb:tv:0');});
  const closing=owner.close(); await pending; await closing;
  assert.throws(()=>owner.read(),{code:'CLOSED'}); assert.throws(()=>owner.update(()=>{}),{code:'CLOSED'});
  assert.deepEqual((await open()).read().previewTargets,['tmdb:tv:0']);
});


test('ordinary extensions are projected before typed validation and never become saved fields', async t => {
  const {owner,open} = await fixture(t);
  const group = {id:'group:'+'a'.repeat(64),keys:['key'],members:['member']};
  await owner.update(draft => {
    draft.extension = {unused:true};
    draft.manual.push({...manual(),extension:{unused:true}});
    draft.blocks.push({...block(),extension:[]});
    draft.groups.push({...group,extension:false});
    draft.preferences.extension = 'unused';
  });
  const entry = history();
  await owner.saveHistory({...entry,extension:{unused:true},episode:{...entry.episode,extension:'unused'},
    playbackTarget:{...entry.playbackTarget,extension:false,source:{...entry.playbackTarget.source,extension:[]}}});
  await owner.close(); const restarted = await open();
  assert.deepEqual(await restarted.history(entry.targetId),entry);
  assert.deepEqual(restarted.read().manual,[manual()]);
  assert.deepEqual(restarted.read().blocks,[block()]);
  assert.deepEqual(restarted.read().groups,[group]);
  assert.deepEqual(restarted.read().preferences,defaultPreferences());
  assert.equal('extension' in restarted.read(),false);
  await assert.rejects(restarted.saveHistory({...entry,episode:{season:'bad'}}),{code:'INVALID_REQUEST'});
  await assert.rejects(restarted.saveHistory({...entry,playbackTarget:{...entry.playbackTarget,id:'wrong'}}),{code:'INVALID_REQUEST'});
});

test('unselected unusable SQL rows survive ready, unrelated writes and restart without re-encoding', async t => {
  const {owner,database,open} = await fixture(t);
  await owner.close();
  await database.execute(`INSERT INTO film_manual_matches(match_id,position_order,target_id,detail_id_kind,detail_id_text,
    has_enabled,has_auto_disable,failure_count,row_revision) VALUES('bad',7,'unselected','integer','not-an-integer',0,0,0,7)`);
  await database.execute(`INSERT INTO film_blocked_sources VALUES('bad-block',7,'unselected','a','integer','not-an-integer','text','line')`);
  await database.execute("INSERT INTO film_groups VALUES('unusable-group',7)");
  await database.execute("INSERT INTO film_group_keys VALUES('unusable-group',0,'')");
  const tables = ['film_manual_matches','film_blocked_sources','film_groups','film_group_keys'];
  const before = await Promise.all(tables.map(table => database.query(`SELECT * FROM ${table}`)));
  const restarted = await open();
  assert.equal(restarted.read().manual[0].errorCode,'CORRUPT_STORAGE');
  assert.equal(restarted.read().blocks[0].errorCode,'CORRUPT_STORAGE');
  await restarted.update(draft => {
    draft.preferences.keywords=['healthy']; draft.manual[0].extension='unused'; draft.blocks[0].extension=false;
    draft.groups[0].extension=[]; draft.manual.push(manual());
  });
  await restarted.update(draft => {draft.manual[1].name='edited';draft.previewTargets.push('healthy');});
  for (const [index,table] of tables.entries()) {
    const rows = await database.query(`SELECT * FROM ${table}`);
    assert.deepEqual(rows[0],before[index][0]);
  }
  const committed = restarted.read();
  await assert.rejects(restarted.update(draft => {draft.manual[0].name='actually edited';}),{code:'INVALID_REQUEST'});
  assert.equal(restarted.read(),committed);
  await restarted.saveHistory(history());
  await restarted.close(); const reopened = await open();
  assert.deepEqual(reopened.read().manual,committed.manual);
  assert.deepEqual(await reopened.history(history().targetId),history());
  await reopened.update(draft => {draft.manual = draft.manual.filter(row => row.id !== 'bad');});
  assert.deepEqual(await database.query("SELECT * FROM film_manual_matches WHERE match_id='bad'"),[]);
  assert.deepEqual((await database.query('SELECT * FROM film_blocked_sources'))[0],before[1][0]);
});
