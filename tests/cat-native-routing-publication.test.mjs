import './storage-test-fixture.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createCatRuntime } from '../packages/script-host/src/cat-runtime.js';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { connectFilmPlayback } from '../apps/runtime/entry.mjs';

const area = new URL('../tmp/test-results/cat-native-routing-publication/', import.meta.url);
const gate = () => {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
};
const tick = () => new Promise(resolve => setImmediate(resolve));
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
async function until(read) {
  for (let attempt = 0; attempt < 1000; attempt++) {
    const value = await read();
    if (value) return value;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.fail('The native routing lifecycle did not settle');
}
async function fixture(t, source) {
  mkdirSync(area, { recursive: true });
  const root = mkdtempSync(new URL('case-', area)), path = join(root, 'selected.js');
  writeFileSync(path, source);
  let latest = { version: 1, port: 0, providers: { quark: true }, mappings: [], scripts: [{
    installationId: 'native', name: 'Native', sourceType: 'local', sourceUrl: null,
    localPath: path, localHash: createHash('sha256').update(source).digest('hex'),
    enabled: true, inputRevision: 1,
  }] }, cached = structuredClone(latest), runtime;
  const cat = createCatScriptModule({ dataRoot: root, configuration: {
    read: () => structuredClone(cached),
    async reload() { cached = structuredClone(latest); },
  }, runtimeFactory: options => runtime = createCatRuntime(options) });
  const modules = [cat], film = connectFilmPlayback(modules, { dataRoot: root, nativeSettings: {
    read: () => null,
  } });
  const status = async () => okay(await cat.methods['cat.script.status']({}));
  const current = await until(async () => {
    const state = (await status()).instances[0];
    return state?.state === 'ready' && state;
  });
  const sourceRef = { key: 'native-default', engine: 'cat', installationId: 'native', siteId: 'default',
    generation: current.generation, name: 'Native' };
  const catalog = input => film.methods['film.data.catalog']({ source: sourceRef, action: 'home',
    payload: {}, ...input }, {});
  t.after(async () => {
    await Promise.all([cat.close(), film.close()]);
    rmSync(root, { recursive: true, force: true });
  });
  return { cat, film, runtime, catalog, status, current, sourceRef,
    get latest() { return latest; }, set latest(value) { latest = value; },
    async notify() { await cat.settingsChanged(); await film.settingsChanged('cat.sources'); },
  };
}
async function upstream(t) {
  const entered = gate(), closed = gate();
  const server = http.createServer((_request, response) => {
    response.once('close', closed.resolve); entered.resolve(response);
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  return { entered, closed, url: `http://127.0.0.1:${server.address().port}/blocked` };
}
const routedSource = url => `
  const http = require('http'); let server, reads = 0;
  module.exports.start = async () => {
    server = http.createServer(async (request, response) => {
      const json = value => response.end(JSON.stringify(value));
      if (request.url === '/config' || request.url === '/full-config')
        return json({video:{sites:[{key:'default',name:'Native',api:'/api',actions:['home']}]}});
      if (request.url === '/api/home') {
        let body=''; for await (const part of request) body += part.toString();
        const payload = JSON.parse(body || '{}'); const count = ++reads;
        if (payload.wait) await new Promise((resolve,reject) => {
          const pending = http.get(${JSON.stringify(url)}, response => {response.resume();response.once('end',resolve);});
          pending.once('error',reject);
        });
        return json({reads:count,list:[{vod_id:'entry',vod_name:'Entry',vod_play_from:'夸克原画',
          vod_play_url:'Episode$https://pan.quark.cn/s/native-routing-share'}]});
      }
      response.statusCode=404;json({code:1});
    });
    await new Promise(resolve => server.listen(Number(process.env.PORT),'127.0.0.1',resolve));
  };
  module.exports.stop = () => {server?.closeAllConnections();server?.close();};
`;

test('native Cat routing notification retires the old execution and revokes its film caches and producers',
  { timeout: 30000 }, async t => {
    const held = await upstream(t), f = await fixture(t, routedSource(held.url));
    const first = okay(await f.catalog());
    assert.equal(first._meow.resources.length, 1);
    assert.equal(okay(await f.catalog()).reads, first.reads, 'the old routing result is really cached');
    const waiting = f.catalog({ payload: { wait: true }, refresh: true });
    const revoked = assert.rejects(waiting, { code: 'CANCELLED' });
    const producer = await held.entered.promise;
    const loaded = gate(), release = gate(), reload = f.runtime.settings.storage.reload.bind(f.runtime.settings.storage);
    t.mock.method(f.runtime.settings.storage, 'reload', async () => {
      loaded.resolve(); await release.promise; return reload();
    });
    t.after(() => release.resolve());
    f.latest.providers.quark = false;
    let notified = false;
    const notification = f.notify().then(() => { notified = true; });
    await loaded.promise; await tick();
    assert.equal(notified, false, 'notification must not race ahead of the actual routing publication');
    assert.equal((await f.status()).routing.providers.quark, true);
    release.resolve(); await notification;
    assert.equal((await f.status()).routing.providers.quark, false);
    const replacement = await until(async () => {
      const state = (await f.status()).instances[0];
      return state?.state === 'ready' && state.generation > f.current.generation && state;
    });
    await revoked;
    await held.closed.promise;
    assert.equal(producer.destroyed, true, 'revocation drains the real script request');
    await assert.rejects(f.catalog(), { code: 'STALE_SOURCE' });
    f.sourceRef.generation = replacement.generation;
    const current = okay(await f.catalog());
    assert.equal(current.reads, 1, 'the replacement starts with fresh guest state');
    assert.equal(current._meow?.resources?.length ?? 0, 0);
    assert.equal(current.list[0].vod_play_from, '夸克原画');
    assert.equal(current.list[0].vod_play_url, 'Episode$https://pan.quark.cn/s/native-routing-share');
  });

test('alias changes preserve the execution while invalidating normalized film results', { timeout: 30000 }, async t => {
  const f = await fixture(t, routedSource('http://127.0.0.1:1/unused'));
  const first = okay(await f.catalog());
  f.latest.mappings = [{ provider: 'quark', aliases: ['自定义别名'] }];
  await f.notify();
  assert.equal((await f.status()).instances[0].generation, f.current.generation);
  assert.ok(okay(await f.catalog()).reads > first.reads);
  const stable = okay(await f.catalog());
  await f.notify();
  assert.equal((await f.status()).instances[0].generation, f.current.generation);
  assert.equal(okay(await f.catalog()).reads, stable.reads);
});

test('provider switches discard cached placeholder tokens before original script networking resumes',
  { timeout: 30000 }, async t => {
    mkdirSync(area, { recursive: true });
    const root = mkdtempSync(new URL('mode-', area)), calls = [];
    const runtime = createCatRuntime({ dataRoot: root, network: async input => {
      calls.push({ path: new URL(input.url).pathname, body: JSON.parse(Buffer.from(input.options.body).toString()) });
      const token = new URL(input.url).pathname.endsWith('/token');
      return { statusCode: 200, headers: { 'content-type': 'application/json' }, body: JSON.stringify({
        code: 0, data: token ? { stoken: 'synthetic-upstream-token' }
          : { list: [{ fid: 'synthetic-real-file', file_name: 'Synthetic.mp4' }] },
      }) };
    } });
    t.after(async () => { await runtime.close(); rmSync(root, { recursive: true, force: true }); });
    await runtime.start();
    await runtime.settings.save(value => ({ ...value, providers: { quark: true } }));
    await runtime.refresh();
    await runtime.install({ installationId: 'cached', enabled: true, source: `
      const https = require('https'); let token, cached;
      const read = (path, body) => new Promise((resolve, reject) => {
        const request = https.request('https://drive.quark.cn/1/clouddrive/share/sharepage/'+path,
          {method:'POST'}, response => {let text='';response.on('data',part=>text+=part.toString());
          response.once('end',()=>resolve(JSON.parse(text)));response.once('error',reject);});
        request.once('error',reject);request.end(JSON.stringify(body));
      });
      module.exports.detail = async id => {
        if (cached) return cached;
        token ??= (await read('token',{pwd_id:'switch-synthetic-share'})).data.stoken;
        const reply = await read('detail',{pwd_id:'switch-synthetic-share',stoken:token});
        const file = reply.data.list[0];
        return cached = {list:[{vod_id:id,vod_play_from:'夸克原画',vod_play_url:file.file_name+'$'+file.fid}]};
      };
    ` });
    const detail = () => runtime.invoke({ installationId: 'cached', siteId: 'default', operation: 'detail', payload: { id: 'entry' } });
    assert.equal((await detail())._meow.resources.length, 1);
    assert.equal(calls.length, 0);
    const first = runtime.instance('cached').generation;
    await runtime.settings.save(value => ({ ...value, providers: { quark: false } }));
    await runtime.refresh();
    assert.ok(runtime.instance('cached').generation > first);
    const disabled = await detail();
    assert.equal(disabled.list[0].vod_play_url, 'Synthetic.mp4$synthetic-real-file');
    assert.deepEqual(calls.map(value => value.path.split('/').at(-1)), ['token', 'detail']);
    assert.equal(calls[1].body.stoken, 'synthetic-upstream-token');
    const second = runtime.instance('cached').generation;
    await runtime.settings.save(value => ({ ...value, providers: { quark: true } }));
    await runtime.refresh();
    assert.ok(runtime.instance('cached').generation > second);
    assert.equal((await detail())._meow.resources.length, 1);
    assert.equal(calls.length, 2);
  });

test('the native routing publication barrier confirms scheduling without waiting for a slow new source download',
  { timeout: 30000 }, async t => {
    const download = await upstream(t), f = await fixture(t, routedSource(download.url));
    f.latest.providers.quark = false;
    f.latest.scripts[0] = { ...f.latest.scripts[0], sourceType: 'network', sourceUrl: download.url,
      localPath: null, localHash: null, inputRevision: 2 };
    let notified = false;
    const notification = f.notify().then(() => { notified = true; });
    await notification;
    const response = await download.entered.promise;
    assert.equal(notified, true);
    assert.equal(response.writableEnded, false, 'notification does not need the source body');
    assert.equal((await f.status()).routing.providers.quark, false);
    response.end(routedSource(download.url) + '\n// replacement');
    await until(async () => {
      const state = (await f.status()).instances[0];
      return state?.state === 'ready' && state.inputRevision === 2;
    });
  });

for (const outcome of ['failure', 'close']) {
  test(`native routing publication ${outcome} settles its admitted notification`, { timeout: 30000 }, async t => {
    const f = await fixture(t, routedSource('http://127.0.0.1:1/unused'));
    const entered = gate(), release = gate();
    t.after(() => release.resolve());
    t.mock.method(f.runtime.settings.storage, 'reload', async () => {
      entered.resolve(); await release.promise;
      if (outcome === 'failure') throw new Error('synthetic local storage failure');
    });
    const notification = f.cat.settingsChanged();
    const rejected = assert.rejects(notification);
    await entered.promise;
    if (outcome === 'close') {
      const closing = f.cat.close();
      await rejected;
      release.resolve(); await closing;
    } else { release.resolve(); await rejected; }
  });
}
