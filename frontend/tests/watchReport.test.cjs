const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync('src/shared/playHistoryRuntime.js', 'utf8').replace(/^import .*;\n/gm, '').replace(/export /g, '');
const binding = { apiBase: 'http://runner/prefix/', spiderApi: '/0123456789/spider/site/3', id: 'pic*author*duration****watch?v=episode', flag: 'line' };
function runtime(respond = body => ({ success: true, ...(body.watchReport ? { watchReport: { ok: true } } : {}) })) {
  const posts = [];
  const context = vm.createContext({
    reactive: x => x, normalizeString: x => String(x ?? '').trim(), normalizeInt: x => Math.trunc(Number(x) || 0),
    apiPostJson: async (url, body) => { posts.push(body); return respond(body); },
    apiGetJson: async () => ({ items: [] }), apiInvalidateCache() {}, buildQuery: () => '',
  });
  vm.runInContext(source + '\nthis.api = {preparePlayHistoryContext, bindPlayHistoryWatchReport, confirmPlayerHistoryPlaybackReady, onPlayerHistoryPlaybackStart, onPlayerHistoryTimeUpdate, syncHistoryProgressIfPossible, commitPlayHistoryContextNow, playHistorySessionState};', context);
  return { ...context.api, posts };
}
async function prepare(r, b = binding) {
  await r.preparePlayHistoryContext({ reportEnabled: true, siteKey: 'source', spiderApi: b?.spiderApi || '/spider/plain/3', siteDetail: 'detail', playFlag: 'line', selectionKey: b?.id || 'plain' });
  r.bindPlayHistoryWatchReport(b);
}
async function frame(r) { await r.onPlayerHistoryPlaybackStart(); await r.confirmPlayerHistoryPlaybackReady('first-frame'); }
async function progress(r) { r.onPlayerHistoryTimeUpdate({ currentTime: 13, duration: 60, playing: true }); await r.syncHistoryProgressIfPossible({ force: true }); }

test('first frame and every existing progress event carry the same binding and actual position', async () => {
  const r = runtime(); await prepare(r); assert.equal(r.posts.length, 0);
  await r.onPlayerHistoryPlaybackStart(); assert.equal(r.posts.length, 0);
  r.onPlayerHistoryTimeUpdate({currentTime:0.25,duration:60,playing:true});
  await r.confirmPlayerHistoryPlaybackReady('first-frame');
  assert.equal(r.posts[0].playbackPositionTicks, 2500000);
  assert.equal(r.posts[0].playbackRuntimeTicks, 600000000);
  for (const key of Object.keys(binding)) assert.equal(r.posts[0].watchReport[key], binding[key]);
  assert.equal(r.posts[0].playbackEvent, 'started');
  for (const pos of [12.5,24,8]) {
    r.onPlayerHistoryTimeUpdate({currentTime:pos,duration:60,playing:true});
    await r.syncHistoryProgressIfPossible({force:true});
    const sent=r.posts.at(-1);
    assert.equal(sent.playbackPositionTicks,pos*10000000);
    assert.equal(sent.playbackEvent,'progress');
    assert.equal(sent.watchReport.sessionId,r.posts[0].watchReport.sessionId);
  }
  assert.equal(r.posts.filter(x => x.watchReport).length, 4);
});
test('history commits before first frame cannot report', async () => {
  const r = runtime(); await prepare(r); await r.commitPlayHistoryContextNow('pre_order_toggle');
  assert.equal(r.posts[0].watchReport, undefined); await frame(r); await progress(r);
  assert.equal(r.posts.filter(x => x.watchReport).length, 2);
});
test('failure retries on progress and success never suppresses subsequent progress', async () => {
  let attempt = 0;
  const r = runtime(() => ({ success: true, watchReport: { ok: ++attempt > 1 } }));
  await prepare(r); await frame(r); await progress(r); await progress(r);
  const reports = r.posts.filter(x => x.watchReport);
  assert.equal(reports.length, 3);
  assert.equal(new Set(reports.map(x => x.watchReport.sessionId)).size, 1);
});
test('same target retains session; changing runtime/runner/episode isolates callbacks; ordinary source opts out', async () => {
  const r = runtime(); await prepare(r); await frame(r);
  const first=r.posts[0].watchReport.sessionId;
  await prepare(r); await frame(r); await progress(r);
  assert.equal(r.posts.at(-1).watchReport.sessionId,first);
  for (const b of [{...binding, spiderApi: '/abcdef0123/spider/site/3'}, {...binding, id: 'another-episode'}, {...binding, apiBase: 'http://another-runner/'}]) {
    await prepare(r, b); await frame(r);
    const sent = r.posts.at(-1).watchReport;
    for (const key of Object.keys(b)) assert.equal(sent[key], b[key]);
  }
  assert.equal(new Set(r.posts.filter(x => x.watchReport).map(x => x.watchReport.sessionId)).size, 4);
  await prepare(r, null); await frame(r); await progress(r); assert.equal(r.posts.at(-1).watchReport, undefined);
});
test('a late old request does not lose or rebind the new video first-frame report', async () => {
  let resolve;let calls=0;
  const r = runtime(() => ++calls===1 ? new Promise(r => { resolve = r; }) : {success:true,watchReport:{ok:true}});
  await prepare(r); const first = frame(r); while (!resolve) await Promise.resolve();
  const oldID=r.posts[0].watchReport.sessionId;
  await prepare(r, {...binding, id: 'new'});
  const next=frame(r);
  resolve({success: true, watchReport: {ok: true}}); await first;await next;
  assert.equal(r.posts.at(-1).watchReport.id,'new');
  assert.notEqual(r.posts.at(-1).watchReport.sessionId,oldID);
});
test('pause and stop forward the final position even when the player is not playing', async () => {
  const r=runtime();await prepare(r);await frame(r);
  for(const [pos,event] of [[27.5,'paused'],[60,'stopped']]) {
    r.onPlayerHistoryTimeUpdate({currentTime:pos,duration:60,playing:false});
    await r.syncHistoryProgressIfPossible({force:true,event});
    assert.equal(r.posts.at(-1).playbackPositionTicks,pos*10000000);
    assert.equal(r.posts.at(-1).playbackEvent,event);
    assert.ok(r.posts.at(-1).watchReport);
  }
});
test('final progress waits for an in-flight progress request instead of being dropped', async () => {
  let resolve;let calls=0;
  const r=runtime(()=>++calls===2 ? new Promise(r=>{resolve=r;}) : {success:true,watchReport:{ok:true}});
  await prepare(r);await frame(r);
  const pending=progress(r);while(!resolve)await Promise.resolve();
  r.onPlayerHistoryTimeUpdate({currentTime:31,duration:60,playing:false});
  const stop=r.syncHistoryProgressIfPossible({force:true,event:'stopped'});
  resolve({success:true,watchReport:{ok:true}});await pending;await stop;
  assert.equal(r.posts.at(-1).playbackPositionTicks,310000000);
  assert.equal(r.posts.at(-1).playbackEvent,'stopped');
});
test('switching videos clears the previous player time before the new first frame', async()=>{
 const r=runtime();await prepare(r);await frame(r);await progress(r);
 await prepare(r,{...binding,id:'new-video'});await frame(r);
 assert.equal(r.posts.at(-1).playbackPositionTicks,0);
});
test('play result opts in only with boolean true', async () => {
  const playback = fs.readFileSync('src/shared/playbackRuntime.js', 'utf8');
  const code = playback.slice(playback.indexOf('export const executeResolvedSitePlayback ='), playback.indexOf('export const executeProxyRetryPlayback =')).replace('export ', '');
  const ctx = vm.createContext({ normalizeString: x => String(x ?? '').trim(), requestCatPlay: async () => ctx.response, rewritePlayPayloadUrls: x => x, normalizePlayPayload: x => x, resolvePlayTargetForPlayback: () => ({url:'http://media/video',headers:{}}), hasNonEmptyHeaders: () => false });
  vm.runInContext(code+'\nthis.execute=executeResolvedSitePlayback;',ctx);
  for (const value of [undefined,false,'true',true]) {
    ctx.response={watchReport:value};
    const result=await ctx.execute({apiBase:binding.apiBase, siteItem:{spiderApi:binding.spiderApi}, panEntry:{label:binding.flag}, segment:{episodeUrl:binding.id}, runtimeSettings:{}});
    assert.equal(!!result.watchReport, value===true);
    if(result.watchReport) for(const key of Object.keys(binding)) assert.equal(result.watchReport[key],binding[key]);
  }
});
