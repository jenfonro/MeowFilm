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

test('resolution/start do not report; first frame reports original binding once', async () => {
  const r = runtime(); await prepare(r); assert.equal(r.posts.length, 0);
  await r.onPlayerHistoryPlaybackStart(); assert.equal(r.posts.length, 0);
  await r.confirmPlayerHistoryPlaybackReady('first-frame'); assert.equal(r.posts.length, 1);
  for (const key of Object.keys(binding)) assert.equal(r.posts[0].watchReport[key], binding[key]);
  assert.ok(r.posts[0].watchReport.sessionId); assert.equal(r.posts[0].playbackEvent, 'started');
  await progress(r); assert.equal(r.posts.filter(x => x.watchReport).length, 1);
});
test('history commits before first frame cannot report', async () => {
  const r = runtime(); await prepare(r); await r.commitPlayHistoryContextNow('pre_order_toggle');
  assert.equal(r.posts[0].watchReport, undefined); await frame(r); await progress(r);
  assert.equal(r.posts.filter(x => x.watchReport).length, 1);
});
test('failure retries with the same session on progress', async () => {
  let attempt = 0;
  const r = runtime(() => ({ success: true, watchReport: { ok: ++attempt > 1 } }));
  await prepare(r); await frame(r); await progress(r); await progress(r);
  const reports = r.posts.filter(x => x.watchReport);
  assert.equal(reports.length, 2); assert.equal(reports[0].watchReport.sessionId, reports[1].watchReport.sessionId);
});
test('resume does not repeat; runtime/runner/episode changes isolate callbacks; ordinary site opts out', async () => {
  const r = runtime(); await prepare(r); await frame(r); await prepare(r); await frame(r); await progress(r);
  assert.equal(r.posts.filter(x => x.watchReport).length, 1);
  for (const b of [{...binding, spiderApi: '/abcdef0123/spider/site/3'}, {...binding, id: 'another-episode'}, {...binding, apiBase: 'http://another-runner/'}]) {
    await prepare(r, b); await frame(r);
    const sent = r.posts.at(-1).watchReport;
    for (const key of Object.keys(b)) assert.equal(sent[key], b[key]);
  }
  assert.equal(new Set(r.posts.filter(x => x.watchReport).map(x => x.watchReport.sessionId)).size, 4);
  await prepare(r, null); await frame(r); await progress(r); assert.equal(r.posts.at(-1).watchReport, undefined);
});
test('late acknowledgement cannot acknowledge a new video', async () => {
  let resolve; const r = runtime(() => new Promise(r => { resolve = r; }));
  await prepare(r); const first = frame(r); while (!resolve) await Promise.resolve();
  const oldState = r.playHistorySessionState.activeContext.watchReportState;
  await prepare(r, {...binding, id: 'new'}); resolve({success: true, watchReport: {ok: true}}); await first;
  assert.equal(oldState.done, true); assert.equal(r.playHistorySessionState.activeContext.watchReportState.done, false);
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
