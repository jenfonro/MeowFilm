import './storage-test-fixture.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { createCatRuntime } from '../packages/script-host/src/cat-runtime.js';
import { createCatScriptModule, createPanRuntimeModule, connectPanAccounts,
  connectFilmPlayback } from '../apps/runtime/entry.mjs';

const area = new URL('../tmp/test-results/film-script-account-invalidation/', import.meta.url);
const gate = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
async function until(read) {
  for (let attempt = 0; attempt < 1000; attempt++) {
    const value = await read();
    if (value) return value;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.fail('script account lifecycle did not settle');
}

async function fixture(t, { rotate = false } = {}) {
  mkdirSync(area, { recursive: true });
  const root = mkdtempSync(new URL('case-', area)), listEntered = gate(), releaseList = gate();
  const catalogEntered = gate(), catalogClosed = gate();
  const server = http.createServer((request, response) => {
    if (request.url === '/held') {
      response.once('close', catalogClosed.resolve); catalogEntered.resolve(); return;
    }
    response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': '0' }); response.end();
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const origin = `http://127.0.0.1:${server.address().port}`;
  const source = `
    const http=require('http'),fs=require('fs');let server,saved='',details=0;
    const json=(response,value)=>response.end(JSON.stringify(value));
    module.exports.start=async()=>{server=http.createServer(async(request,response)=>{
      let body='';for await(const part of request)body+=part.toString();
      if(request.url==='/config'||request.url==='/full-config')return json(response,{video:{sites:[{
        key:'default',name:'Mixed',api:'/api',actions:['home','detail','play']} ]}});
      if(request.url==='/website/api/credentials')return json(response,{code:0,data:{quark:{cookie:''}}});
      if(request.url==='/website/api/credential/quark/cookie'&&request.method==='PUT'){
        saved=JSON.parse(body).value;fs.writeFileSync('/credential.json',JSON.stringify({cookie:saved}));return json(response,{code:0});}
      if(request.url.startsWith('/api')){
        const payload=JSON.parse(body||'{}');
        if(request.url==='/api/play')return json(response,{url:${JSON.stringify(origin)}+'/script.mp4',headers:{'x-synthetic':'script'}});
        if(request.url!=='/api/detail')await new Promise((resolve,reject)=>{
          const pending=http.get(${JSON.stringify(origin)}+'/held',reply=>{reply.resume();reply.once('end',resolve);});pending.once('error',reject);});
        const scriptOnly=payload.id==='script-only';
        return json(response,{details:++details,list:[{vod_id:payload.id||'entry',vod_name:'Entry',
          vod_play_from:scriptOnly?'脚本':'夸克$$$脚本',vod_play_url:(scriptOnly?'':'Episode$https://pan.quark.cn/s/account-share$$$')+'Episode$'+saved}]});
      }response.statusCode=404;json(response,{code:1});
    });await new Promise(resolve=>server.listen(Number(process.env.PORT),'127.0.0.1',resolve));};
    module.exports.stop=()=>{server?.closeAllConnections();server?.close();};`;
  let runtime, listSignal, refreshCookie, rotations = 0;
  const cat = createCatScriptModule({ dataRoot: root, runtimeFactory: options => runtime = createCatRuntime(options) });
  const pan = createPanRuntimeModule({ dataRoot: root, transport: {
    async request(input) {
      const url = new URL(input.url), response = (value, headers = {}) => ({ url: input.url, status: 200,
        headers, body: Buffer.from(JSON.stringify(value)) });
      if (url.pathname.endsWith('/account/info')) {
        const headers = refreshCookie ? { 'set-cookie': [`${refreshCookie}; Path=/`] } : {};
        refreshCookie = undefined;
        return response({ code: 'OK', success: true, data: { nickname: 'Synthetic' } }, headers);
      }
      if (url.pathname.endsWith('/share/sharepage/token')) return response({ code: 0, data: { stoken: 'synthetic-token' } },
        rotate && rotations++ === 0 ? { 'set-cookie': ['session=rotated; Path=/'] } : {});
      if (url.pathname.endsWith('/share/sharepage/detail')) {
        listSignal = input.signal; listEntered.resolve();
        if (rotate) await Promise.race([releaseList.promise, new Promise((_, reject) => {
          const cancel = () => reject(Object.assign(new Error('CANCELLED'), { code: 'CANCELLED' }));
          input.signal.addEventListener('abort', cancel, { once: true });
          releaseList.promise.finally(() => input.signal.removeEventListener('abort', cancel));
          if (input.signal.aborted) cancel();
        })]);
        return response({ code: 0, data: { list: [{ fid: 'file', file_name: 'Episode.mp4', file_type: 1,
          share_fid_token: 'synthetic-file' }], has_more: false } });
      }
      if (url.pathname.endsWith('/file/sort')) return response({ code: 0, data: {
        list: [{ fid: 'destination', file_name: 'MeowFilm', dir: true }], has_more: false } });
      if (url.pathname.endsWith('/share/sharepage/save')) return response({ code: 0, task_sync: true,
        data: { save_as: { save_as_top_fids: ['stored'] } } });
      if (url.pathname.endsWith('/file/download')) return response({ code: 0,
        data: [{ fid: 'stored', play_url: origin + '/pan.mp4' }] });
      assert.fail('unexpected synthetic provider request: ' + url.pathname);
    }, async close() { releaseList.resolve(); },
  } });
  const modules = [cat, pan]; connectPanAccounts(modules);
  const film = connectFilmPlayback(modules, { dataRoot: root });
  const call = async (method, input = {}) => okay(await modules.find(module => Object.hasOwn(module.methods, method)).methods[method](input, {}));
  t.after(async () => {
    releaseList.resolve(); server.closeAllConnections();
    await Promise.allSettled(modules.map(module => module.close()));
    await new Promise(resolve => server.close(resolve)); rmSync(root, { recursive: true, force: true });
  });
  await cat.ready; await pan.ready;
  await runtime.settings.save(value => ({ ...value, providers: { quark: true } })); await call('cat.script.refresh');
  await call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'session=initial' } });
  okay(await cat.upload({ installationId: 'managed', sourceRevision: 1 }, Buffer.from(source)));
  const installed = await call('cat.script.install', { installationId: 'managed', sourceRevision: 1 });
  const sync = async rowRevision => until(async () => {
    const account = (await call('cat.script.status')).instances[0].accountSync?.find(row => row.provider === 'quark');
    return account?.status === 'saved' && account.rowRevision === rowRevision && account;
  });
  await sync(1);
  const sourceRef = { key: 'managed:default', engine: 'cat', installationId: 'managed', siteId: 'default',
    generation: installed.generation, name: 'Mixed' };
  const snapshot = sessionId => call('film.session.snapshot', { sessionId });
  return { call, film, cat, source: sourceRef, listEntered, releaseList, catalogEntered, catalogClosed, sync,
    rotateCookie(value) { refreshCookie = value; },
    get listSignal() { return listSignal; },
    async open(detailId = 'entry') {
      const target = await call('film.data.target', { target: { kind: 'source', title: 'Entry',
        source: { key: sourceRef.key, detailId } }, sources: [sourceRef] });
      return call('film.session.open', { platform: 'test', entrySurface: 'search', target, sources: [sourceRef] });
    }, snapshot,
    command: (sessionId, action, input = {}) => call('film.session.command', { sessionId, action, ...input }),
    async settle(sessionId) { return until(async () => { const state = await snapshot(sessionId); return !state.browsing.loading && state.stage !== 'loading' && state; }); },
    async play(state, lineId) {
      await call('film.session.command', { sessionId: state.sessionId, action: 'browse', sourceId: state.sources[0].id, lineId });
      state = await this.settle(state.sessionId);
      await this.command(state.sessionId, 'select.target', { target: state.browsing.episodes[0].playbackTarget });
      state = await until(async () => { const next = await snapshot(state.sessionId); return next.attempt && next; });
      const receipt = await call('film.session.attempt', { sessionId: state.sessionId, attemptId: state.attempt.id });
      await this.command(state.sessionId, 'media.started', { attemptId: state.attempt.id });
      return { state: await snapshot(state.sessionId), receipt };
    },
  };
}

test('same-account Cookie rotation preserves the in-flight built-in directory and accepted pan media', { timeout: 30000 }, async t => {
  const f = await fixture(t, { rotate: true }), opened = await f.open();
  await f.listEntered.promise; await f.sync(2);
  assert.equal(f.listSignal.aborted, false, 'the script save cannot revoke the pan owner request that rotated its Cookie');
  f.releaseList.resolve();
  const ready = await f.settle(opened.sessionId);
  assert.equal(ready.browsing.error, null); assert.equal(ready.browsing.episodes.length, 1);
  const { state, receipt } = await f.play(ready, '夸克-account-share');
  assert.equal(receipt.scope.accountProvider, 'quark');
  assert.equal((await fetch(receipt.url, { method: 'HEAD' })).status, 200);
  await f.call('pan.refresh', { provider: 'quark' }); await f.sync(2);
  await f.call('cat.script.management.sync', { installationId: 'managed', provider: 'quark', credentials: { cookie: 'ignored' } });
  const current = await f.snapshot(state.sessionId);
  assert.equal(current.current.attemptId, state.current.attemptId);
  assert.equal(current.error, null); assert.equal(current.browsing.error, null);
  assert.equal((await fetch(receipt.url, { method: 'HEAD' })).status, 200, 'the exact accepted pan media lease stays readable');
  assert.equal((await f.call('pan.settings.get')).accounts.find(row => row.provider === 'quark').revision, 1);
  await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'session=replaced' } }); await f.sync(3);
  assert.equal((await f.snapshot(state.sessionId)).current, null, 'a real account replacement still revokes the old pan lease');
  assert.notEqual((await fetch(receipt.url, { method: 'HEAD' })).status, 200);
});

test('script saves revoke opaque results and media in mixed sources while preserving the pan branch and rereading script details', { timeout: 30000 }, async t => {
  const f = await fixture(t), pan = await f.play(await f.settle((await f.open()).sessionId), '夸克-account-share');
  const script = await f.play(await f.settle((await f.open('script-only')).sessionId), '脚本');
  const previousDetail = await f.call('film.data.detail', { source: f.source, detailId: 'entry' });
  const catalog = f.film.methods['film.data.catalog']({ source: f.source, action: 'home', payload: {} }, {});
  const cancelled = assert.rejects(catalog, { code: 'CANCELLED' }); await f.catalogEntered.promise;
  f.rotateCookie('session=new'); await f.call('pan.refresh', { provider: 'quark' }); await f.sync(2);
  await cancelled; await f.catalogClosed.promise;
  assert.equal((await f.snapshot(script.state.sessionId)).current, null);
  assert.notEqual((await fetch(script.receipt.url, { method: 'HEAD' })).status, 200);
  assert.equal((await f.snapshot(pan.state.sessionId)).current.attemptId, pan.state.current.attemptId);
  assert.equal((await fetch(pan.receipt.url, { method: 'HEAD' })).status, 200);
  for (const [sessionId, sourceId] of [[pan.state.sessionId, pan.state.sources[0].id],
    [script.state.sessionId, script.state.sources[0].id]]) {
    await f.command(sessionId, 'browse', { sourceId, lineId: '脚本' });
    const state = await f.settle(sessionId);
    assert.equal(state.browsing.error, null); assert.equal(state.browsing.episodes.length, 1);
  }
  const refreshed = await f.call('film.data.detail', { source: f.source, detailId: 'entry' });
  assert.ok(refreshed.raw.details > previousDetail.raw.details);
  assert.equal(refreshed.lines.find(line => line.id === '脚本').episodes[0].playId, 'session=new');
  const replay = await f.play(await f.snapshot(script.state.sessionId), '脚本');
  assert.equal((await fetch(replay.receipt.url, { method: 'HEAD' })).status, 200, 'script candidates are rebuilt from the new receipt');
  const before = await f.snapshot(replay.state.sessionId);
  await f.film.accountSaved({ installationId: f.source.installationId, generation: f.source.generation - 1, provider: 'quark' });
  assert.deepEqual(await f.snapshot(replay.state.sessionId), before, 'a receipt from an old script generation cannot revoke this one');
});
