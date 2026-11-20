import './storage-test-fixture.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { createAppRuntime, createCatScriptModule, createPanRuntimeModule, createRuntimeClient,
  connectPanAccounts, connectFilmPlayback } from '../apps/runtime/entry.mjs';
import { syncCatManagement } from '../packages/script-host/src/cat-management.js';
import { createCatRuntime } from '../packages/script-host/src/cat-runtime.js';

const area = new URL('../tmp/test-results/pan-cat-account-sync/', import.meta.url).pathname;
const tick = () => new Promise(resolve => setImmediate(resolve));
async function until(run) {
  for (let attempt = 0; attempt < 1000; attempt++) {
    const value = await run();
    if (value) return value;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.fail('account synchronization did not complete');
}
const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
const managedSource = ({ gate = null, fail = false } = {}) => `
  const http = require('http'), fs = require('fs');
  let server, saved = {cookie:'script-original'}, puts = 0;
  const json = (response, value) => response.end(JSON.stringify(value));
  module.exports.start = async () => {
    if (fs.existsSync('/credential.json')) saved = JSON.parse(fs.readFileSync('/credential.json','utf8'));
    server = http.createServer(async (request, response) => {
      if (request.url === '/config' || request.url === '/full-config')
        return json(response,{video:{sites:[{key:'default',name:'Account',api:'/api',actions:['home','detail','play']}]}});
      if (request.url === '/website/api/credentials') return json(response,{code:0,data:{quark:{cookie:''}}});
      if (request.url === '/website/api/credential/quark/cookie' && request.method === 'PUT') {
        let body = ''; for await (const part of request) body += part.toString();
        puts++;
        if (${JSON.stringify(gate)}) await new Promise((resolve,reject) => {
          const pending = http.get(${JSON.stringify(gate)}, response => {response.resume();response.once('end',resolve);});
          pending.once('error',reject);
        });
        if (${fail}) return json(response,{code:1,message:'untrusted echoed response'});
        saved = {cookie:JSON.parse(body).value};
        fs.writeFileSync('/credential.json',JSON.stringify(saved));
        return json(response,{code:0});
      }
      if (request.url.startsWith('/api')) {
        const query = new URL(request.url,'http://fixture').searchParams;
        if (query.get('ac') === 'detail') return json(response,{list:[{vod_id:'entry',vod_name:'Entry',
          vod_play_from:'line',vod_play_url:'Episode$opaque'}]});
        if (query.get('play')) return json(response,{url:'https://media.invalid/movie.mp4'});
        return json(response,{cookie:saved.cookie,puts});
      }
      response.statusCode = 404; json(response,{code:1});
    });
    await new Promise(resolve => server.listen(Number(process.env.PORT),'127.0.0.1',resolve));
  };
  module.exports.stop = () => {server?.closeAllConnections();server?.close();};
`;
async function fixture(t, { pan: options = {}, film = false, accounts = true, limits } = {}) {
  mkdirSync(area, { recursive: true });
  const dataRoot = mkdtempSync(join(area, 'case-'));
  const cat = createCatScriptModule({ dataRoot, ...(limits ? {
    runtimeFactory: input => createCatRuntime({ ...input, limits }),
  } : {}) });
  const pan = createPanRuntimeModule({ dataRoot, transport: {
    async request(input) {
      return { url: input.url, status: 200, headers: { 'set-cookie': ['session=rotated; Path=/'] },
        body: Buffer.from(JSON.stringify({ code: 'OK', success: true, data: { nickname: 'synthetic' } })) };
    }, async close() {},
  }, ...options });
  const modules = [cat, pan];
  if (accounts) connectPanAccounts(modules);
  if (film) connectFilmPlayback(modules, { dataRoot });
  const runtime = createAppRuntime({ sessionId: randomUUID(), modules });
  await runtime.start();
  const client = createRuntimeClient({ baseUrl: runtime.baseUrl });
  t.after(async () => { await client.close(); await runtime.close(); rmSync(dataRoot, { recursive: true, force: true }); });
  const call = async (method, input) => okay(await client.call(method, input));
  return { cat, pan, client, call, runtime, dataRoot,
    async install(id, source = managedSource()) {
      okay(await client.upload('cat', id, 1, Buffer.from(source)));
      return call('cat.script.install', { installationId: id, sourceRevision: 1 });
    },
    async probe(id) {
      return call('cat.script.invoke', { installationId: id, siteId: 'default', operation: 'home' });
    },
    async settled(id, status = 'saved') {
      return until(async () => {
        const instance = (await call('cat.script.status')).instances.find(item => item.installationId === id);
        return instance?.accountSync?.find(item => item.provider === 'quark')?.status === status && instance;
      });
    },
  };
}
async function gated(t) {
  const entered = [], responses = [];
  const server = http.createServer((_request, response) => {
    const waiter = entered.shift();
    if (waiter) waiter(response); else responses.push(response);
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  return {
    url: `http://127.0.0.1:${server.address().port}/gate`,
    pending: () => responses.length,
    next: () => responses.length ? Promise.resolve(responses.shift()) : new Promise(resolve => entered.push(resolve)),
  };
}

test('account saves, refreshes and explicit clears proactively persist into each ready generation without changing local commit semantics',
  { timeout: 30000 }, async t => {
    const f = await fixture(t);
    await f.install('managed');
    assert.deepEqual(await f.probe('managed'), { cookie: 'script-original', puts: 0 });
    assert.deepEqual((await f.call('cat.script.status')).instances[0].accountSync, []);
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'session=local' } });
    await f.settled('managed');
    assert.deepEqual(await f.probe('managed'), { cookie: 'session=local', puts: 1 });
    const before = (await f.call('cat.script.status')).instances[0].generation;
    await f.call('pan.refresh', { provider: 'quark' });
    await f.settled('managed');
    assert.equal((await f.probe('managed')).cookie, 'session=rotated');
    await f.call('cat.script.restart', { installationId: 'managed' });
    const restarted = await f.settled('managed');
    assert.ok(restarted.generation > before);
    assert.equal((await f.probe('managed')).cookie, 'session=rotated');
    await f.call('cat.script.enable', { installationId: 'managed', enabled: false });
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'while-disabled' } });
    await f.call('cat.script.enable', { installationId: 'managed', enabled: true });
    await f.settled('managed');
    assert.equal((await f.probe('managed')).cookie, 'while-disabled');
    okay(await f.client.upload('cat', 'managed', 2, Buffer.from(managedSource() + '\n// updated generation')));
    await f.call('cat.script.update', { installationId: 'managed', sourceRevision: 2 });
    await f.settled('managed');
    assert.equal((await f.probe('managed')).cookie, 'while-disabled');
    await f.call('pan.settings.clear', { provider: 'quark' });
    await f.settled('managed');
    assert.equal((await f.probe('managed')).cookie, '');
    assert.equal(readFileSync(join(f.dataRoot, 'cat/volumes/managed/credential.json'), 'utf8'), '{"cookie":""}');
    const status = await f.call('cat.script.status');
    assert.equal(JSON.stringify(status).includes('session=rotated'), false);
    assert.equal((await f.call('pan.settings.get')).accounts.find(item => item.provider === 'quark').status, 'signedOut');
  });

test('QR confirmation commits to the local owner and proactively synchronizes the accepted Cookie',
  { timeout: 30000 }, async t => {
    const f = await fixture(t, { pan: { transport: {
      async request(input) {
        const url = new URL(input.url), path = url.pathname;
        const value = path.endsWith('/getTokenForQrcodeLogin') ? { data: { token: 'synthetic-qr-token' } }
          : path.endsWith('/getServiceTicketByQrcodeToken') ? { status: '2000000', service_ticket: 'synthetic-ticket' }
          : path.endsWith('/file/sort') ? { code: 0, data: { list: [] } } : {};
        return { url: input.url, status: 200,
          headers: { 'set-cookie': ['__puus=synthetic-qr-confirmed; Domain=.quark.cn; Path=/; Secure'] },
          body: Buffer.from(JSON.stringify(value)) };
      }, async close() {},
    } } });
    await f.install('qr');
    const start = await f.call('pan.login.start', { provider: 'quark' });
    const confirmed = await f.call('pan.login.poll', { sessionId: start.sessionId });
    assert.equal(confirmed.status, 'confirmed');
    await f.settled('qr');
    assert.match(readFileSync(join(f.dataRoot, 'cat/volumes/qr/credential.json'), 'utf8'), /synthetic-qr-confirmed/);
    assert.equal(JSON.stringify(confirmed).includes('synthetic-qr-confirmed'), false);
  });

test('a film producer waiting for proactive synchronization is revoked without blocking the sync barrier or its real drain',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t, { film: true });
    const installed = await f.install('film', managedSource({ gate: gate.url }));
    const source = { key: 'film:default', engine: 'cat', installationId: 'film',
      siteId: 'default', generation: installed.generation, name: 'Account' };
    const catalog = () => f.client.call('film.data.catalog', { source, action: 'home', payload: {} });
    assert.equal(okay(await catalog()).cookie, 'script-original');
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'film-current' } });
    const blocked = await gate.next();
    const waiting = f.client.call('film.data.catalog', { source, action: 'home', payload: {}, refresh: true });
    await tick();
    blocked.end('commit account');
    const receipt = await waiting;
    assert.equal(receipt.ok, false, 'the waiting old film producer must be revoked');
    await f.settled('film');
    assert.equal(okay(await catalog()).cookie, 'film-current', 'the saved account revokes the old structured cache');
  });

test('an account saved before enable and a native store reload synchronize without waiting for an invoke',
  { timeout: 30000 }, async t => {
    let document = { version: 1, accounts: { quark: { revision: 1, rowRevision: 1,
      credentials: { cookie: 'native-before-start' } } } };
    const f = await fixture(t, { pan: { store: {
      read: () => structuredClone(document), reload: () => structuredClone(document),
      update() { assert.fail('native store remains the sole write owner'); }, async close() {},
    } } });
    await f.install('native'); await f.settled('native');
    assert.equal(readFileSync(join(f.dataRoot, 'cat/volumes/native/credential.json'), 'utf8'), '{"cookie":"native-before-start"}');
    document.accounts.quark = { revision: 2, rowRevision: 2, credentials: {} };
    await f.pan.settingsChanged(); await f.settled('native');
    assert.equal(readFileSync(join(f.dataRoot, 'cat/volumes/native/credential.json'), 'utf8'), '{"cookie":""}');
  });

test('queued saves cannot let a late older receipt overwrite current status or the final script account',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t);
    await f.install('managed', managedSource({ gate: gate.url }));
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'older' } });
    const older = await gate.next();
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'newer' } });
    let completed = false;
    const invoke = f.probe('managed').then(value => { completed = true; return value; });
    older.end('release old');
    const newer = await gate.next();
    const status = (await f.call('cat.script.status')).instances[0];
    assert.equal(status.accountSync.find(item => item.provider === 'quark').status, 'pending');
    assert.equal(completed, false);
    newer.end('release new');
    assert.deepEqual(await invoke, { cookie: 'newer', puts: 2 });
    const current = await f.settled('managed');
    assert.equal(current.accountSync.find(item => item.provider === 'quark').revision, 2);
  });

test('explicit and automatic saves share one ordered owner and App requests always use its latest account',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t);
    const installed = await f.install('managed', managedSource({ gate: gate.url }));
    const saved = [];
    f.cat.onAccountSaved(value => saved.push(value));
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'owner-before-explicit' } });
    const first = await gate.next();
    const explicit = f.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'caller-must-not-become-owner' } });
    await until(async () => !(await f.call('cat.script.status')).instances[0].accountSync
      .find(item => item.provider === 'quark')?.revision);
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'owner-newest' } });
    assert.equal(gate.pending(), 0, 'no second write can reach its management gate while the first owns the queue');
    first.end('release first owner write');
    const second = await gate.next();
    second.end('release explicit request');
    assert.deepEqual(await explicit, { status: 'saved', protocol: 'website-api-v1' });
    const third = await gate.next();
    third.end('release automatic request');
    const current = await f.settled('managed');
    assert.deepEqual(await f.probe('managed'), { cookie: 'owner-newest', puts: 3 });
    assert.deepEqual(current.accountSync.find(item => item.provider === 'quark'),
      { provider: 'quark', status: 'saved', revision: 2, rowRevision: 2 });
    assert.deepEqual(saved, Array.from({ length: 3 }, () => ({ installationId: 'managed',
      generation: installed.generation, provider: 'quark' })));
    assert.equal((await f.call('pan.settings.get')).accounts.find(item => item.provider === 'quark').revision, 2);
  });

test('standalone explicit saves are serialized and publish the same saved event without a pan owner',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t, { accounts: false });
    const installed = await f.install('managed', managedSource({ gate: gate.url }));
    const saved = [];
    f.cat.onAccountSaved(value => saved.push(value));
    const sync = cookie => f.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie } });
    const older = sync('standalone-older');
    const first = await gate.next();
    const newer = sync('standalone-newer');
    await tick();
    assert.equal(gate.pending(), 0);
    first.end('release older');
    assert.equal((await older).status, 'saved');
    const second = await gate.next();
    assert.equal(readFileSync(join(f.dataRoot, 'cat/volumes/managed/credential.json'), 'utf8'),
      '{"cookie":"standalone-older"}');
    second.end('release newer');
    assert.equal((await newer).status, 'saved');
    assert.deepEqual(await f.probe('managed'), { cookie: 'standalone-newer', puts: 2 });
    assert.deepEqual(saved, Array.from({ length: 2 }, () => ({ installationId: 'managed',
      generation: installed.generation, provider: 'quark' })));
  });

test('cancelling a queued explicit save settles its caller and never writes after the previous owner drains',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t, { accounts: false });
    await f.install('managed', managedSource({ gate: gate.url }));
    const first = f.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'kept' } });
    const blocked = await gate.next();
    const controller = new AbortController();
    const cancelled = f.cat.methods['cat.script.management.sync']({ installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'cancelled' } }, { signal: controller.signal });
    await until(async () => (await f.call('cat.script.status')).instances[0].accountSync
      ?.find(item => item.provider === 'quark')?.status === 'pending');
    controller.abort();
    assert.equal((await cancelled).code, 'CANCELLED');
    blocked.end('release previous write');
    assert.equal((await first).status, 'saved');
    assert.deepEqual(await f.probe('managed'), { cookie: 'kept', puts: 1 });
    assert.equal(gate.pending(), 0);
    assert.equal((await f.call('cat.script.status')).instances[0].accountSync
      .find(item => item.provider === 'quark').status, 'failed');
  });

test('an App explicit sync cannot create or clear a script account when the pan owner is unconfigured',
  { timeout: 30000 }, async t => {
    const f = await fixture(t);
    await f.install('managed');
    assert.deepEqual(await f.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'caller-account' } }), { status: 'unsupported' });
    assert.deepEqual(await f.probe('managed'), { cookie: 'script-original', puts: 0 });
  });

test('cancelling an invoke waiting for account synchronization does not cancel its persistent write',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t);
    await f.install('managed', managedSource({ gate: gate.url }));
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'active-owner' } });
    const write = await gate.next(), controller = new AbortController();
    const waiting = f.cat.methods['cat.script.invoke']({ installationId: 'managed', siteId: 'default',
      operation: 'home', payload: {} }, { signal: controller.signal });
    await tick();
    controller.abort();
    assert.equal((await waiting).code, 'CANCELLED');
    assert.equal(write.destroyed, false);
    write.end('complete the owner write');
    await f.settled('managed');
    assert.deepEqual(await f.probe('managed'), { cookie: 'active-owner', puts: 1 });
  });

test('cancelling an explicit request does not discard an already queued automatic owner save',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t);
    await f.install('managed', managedSource({ gate: gate.url }));
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'first-owner' } });
    const first = await gate.next();
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'newest-owner' } });
    const controller = new AbortController();
    const cancelled = f.cat.methods['cat.script.management.sync']({ installationId: 'managed',
      provider: 'quark' }, { signal: controller.signal });
    await until(async () => !(await f.call('cat.script.status')).instances[0].accountSync
      .find(item => item.provider === 'quark')?.revision);
    controller.abort();
    assert.equal((await cancelled).code, 'CANCELLED');
    first.end('release first');
    const second = await gate.next();
    second.end('release previously queued automatic save');
    assert.deepEqual(await f.probe('managed'), { cookie: 'newest-owner', puts: 2 });
    assert.equal(gate.pending(), 0);
  });

test('cancelling an accepted PUT settles only its caller and the next save waits for its durable receipt',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t, { accounts: false });
    await f.install('managed', managedSource({ gate: gate.url }));
    const saved = [];
    f.cat.onAccountSaved(value => saved.push(value));
    const controller = new AbortController();
    const older = f.cat.methods['cat.script.management.sync']({ installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'accepted-older' } }, { signal: controller.signal });
    const first = await gate.next();
    controller.abort();
    assert.equal((await older).code, 'CANCELLED');
    assert.equal((await f.call('cat.script.status')).instances[0].accountSync[0].status, 'pending');
    const newer = f.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'accepted-newer' } });
    await tick();
    assert.equal(first.destroyed, false, 'caller cancellation does not abort an accepted persistent write');
    assert.equal(gate.pending(), 0, 'the next PUT stays behind the real receipt');
    first.end('release accepted write');
    const second = await gate.next();
    assert.equal(readFileSync(join(f.dataRoot, 'cat/volumes/managed/credential.json'), 'utf8'),
      '{"cookie":"accepted-older"}');
    second.end('release next write');
    assert.equal((await newer).status, 'saved');
    assert.deepEqual(await f.probe('managed'), { cookie: 'accepted-newer', puts: 2 });
    assert.equal(saved.length, 2, 'both actual durable writes publish their same owner event');
  });

test('a cancelled caller still leaves the accepted write recorded as saved when its actual receipt arrives',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t, { accounts: false });
    const installed = await f.install('managed', managedSource({ gate: gate.url }));
    const saved = [];
    f.cat.onAccountSaved(value => saved.push(value));
    const controller = new AbortController();
    const pending = f.cat.methods['cat.script.management.sync']({ installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'actually-saved' } }, { signal: controller.signal });
    const write = await gate.next();
    controller.abort();
    assert.equal((await pending).code, 'CANCELLED');
    write.end('durable completion after caller cancellation');
    const current = await f.settled('managed');
    assert.deepEqual(current.accountSync, [{ provider: 'quark', status: 'saved' }]);
    assert.deepEqual(saved, [{ installationId: 'managed', generation: installed.generation, provider: 'quark' }]);
    assert.deepEqual(await f.probe('managed'), { cookie: 'actually-saved', puts: 1 });
  });

test('an unknown timed-out PUT retires its Worker before another queued save can reach the same volume',
  { timeout: 30000 }, async t => {
    const gate = await gated(t), f = await fixture(t, { accounts: false, limits: { requestTimeoutMs: 1000 } });
    await f.install('managed', managedSource({ gate: gate.url }));
    const older = f.client.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'timed-out-older' } });
    const first = await gate.next();
    const physicallyClosed = new Promise(resolve => first.once('close', resolve));
    const newer = f.client.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'must-not-overlap' } });
    assert.equal((await older).code, 'TIMEOUT');
    assert.equal((await newer).code, 'STALE_SESSION');
    await physicallyClosed;
    assert.equal(first.destroyed, true);
    assert.equal(gate.pending(), 0);
    assert.throws(() => readFileSync(join(f.dataRoot, 'cat/volumes/managed/credential.json'), 'utf8'), { code: 'ENOENT' });
    const failed = (await f.call('cat.script.status')).instances[0];
    assert.equal(failed.state, 'failed'); assert.equal(failed.error, 'TIMEOUT');
    const recovered = await f.call('cat.script.restart', { installationId: 'managed' });
    assert.ok(recovered.generation > failed.generation);
    assert.deepEqual(await f.probe('managed'), { cookie: 'script-original', puts: 0 });
  });

test('a failed explicit save reports the real failure through its receipt and current instance status',
  { timeout: 30000 }, async t => {
    const f = await fixture(t, { accounts: false });
    await f.install('managed', managedSource({ fail: true }));
    const reply = await f.client.call('cat.script.management.sync', { installationId: 'managed',
      provider: 'quark', credentials: { cookie: 'never-persisted' } });
    assert.equal(reply.ok, false); assert.equal(reply.code, 'STORAGE_ERROR');
    const status = (await f.call('cat.script.status')).instances[0];
    assert.equal(status.error, 'STORAGE_ERROR');
    assert.deepEqual(status.accountSync, [{ provider: 'quark', status: 'failed', errorCode: 'STORAGE_ERROR' }]);
    assert.deepEqual(await f.probe('managed'), { cookie: 'script-original', puts: 1 });
  });

test('App sync ignores unused caller credential shapes while standalone requires its actual credential input',
  { timeout: 30000 }, async t => {
    const f = await fixture(t);
    await f.install('managed');
    await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'sole-owner' } });
    await f.settled('managed');
    for (const extra of [{}, { credentials: 'unused' }]) {
      assert.equal((await f.call('cat.script.management.sync', { installationId: 'managed',
        provider: 'quark', ...extra })).status, 'saved');
    }
    assert.deepEqual(await f.probe('managed'), { cookie: 'sole-owner', puts: 3 });
    const standalone = await fixture(t, { accounts: false });
    await standalone.install('managed');
    for (const extra of [{}, { credentials: 'consumed' }]) {
      assert.equal((await standalone.client.call('cat.script.management.sync', { installationId: 'managed',
        provider: 'quark', ...extra })).code, 'INVALID_ARGUMENT');
    }
    assert.deepEqual(await standalone.probe('managed'), { cookie: 'script-original', puts: 0 });
  });

test('a failed script save leaves local account committed, exposes a bounded error, and does not block unrelated invokes',
  { timeout: 30000 }, async t => {
    const f = await fixture(t);
    await f.install('managed', managedSource({ fail: true }));
    assert.equal((await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'local-private' } })).configured, true);
    const failed = await f.settled('managed', 'failed');
    assert.equal(failed.state, 'ready'); assert.equal(failed.error, 'STORAGE_ERROR');
    assert.equal(JSON.stringify(failed).includes('local-private'), false);
    assert.equal(JSON.stringify(failed).includes('untrusted echoed response'), false);
    assert.deepEqual(await f.probe('managed'), { cookie: 'script-original', puts: 1 });
    assert.equal((await f.call('pan.settings.get')).accounts.find(item => item.provider === 'quark').configured, true);
  });

test('shutdown cancels and drains an active automatic management request', { timeout: 30000 }, async t => {
  const gate = await gated(t), f = await fixture(t);
  await f.install('managed', managedSource({ gate: gate.url }));
  await f.call('pan.settings.set', { provider: 'quark', credentials: { cookie: 'pending' } });
  const response = await gate.next();
  const closed = new Promise(resolve => response.once('close', resolve));
  await f.runtime.close(); await closed;
  assert.equal(response.destroyed, true);
});

function managementFixture(descriptor, routes = {}) {
  const calls = [];
  return { calls, services: { endpoint: () => ({ origin: 'http://127.0.0.1:1234' }), endpoints: () => [] },
    async management(path, body, method, options) {
      calls.push({ path, body, method, options });
      if (path === '/website/api/credentials') return { code: 0, data: descriptor };
      return routes[path]?.(method, body) ?? { code: 1 };
    } };
}
test('management clears use real durable PUTs, cookie-only Tianyi never clears an account-password route, and unknown providers are unsupported', async () => {
  const session = managementFixture({ pan189: { account: '', password: '' } }, {
    '/website/api/pan189/account': () => ({ code: 0 }),
  });
  assert.deepEqual(await syncCatManagement(session, { provider: 'tianyi', credentials: { cookie: 'cookie-only' } }), { status: 'unsupported' });
  assert.equal(session.calls.some(call => call.method === 'PUT'), false);
  assert.deepEqual(await syncCatManagement(session, { provider: 'tianyi', credentials: {} }), { status: 'saved', protocol: 'website-api-v1' });
  assert.deepEqual(session.calls.at(-1).body, { account: '', password: '' });
  assert.equal(session.calls.at(-1).options.requireWrite, true);
  assert.deepEqual(await syncCatManagement(session, { provider: 'unrecognized', credentials: { cookie: 'unused' } }), { status: 'unsupported' });
});
test('a pure Authorization descriptor is detected and a mobile save is sent only to a probed compatible route', async () => {
  const session = managementFixture({ pan139: { authorization: '' } }, {
    '/website/api/pan139/authorization': (method) => ({ code: 0, ...(method === 'GET' ? { data: { authorization: '' } } : {}) }),
  });
  assert.deepEqual(await syncCatManagement(session, { provider: 'yidong', credentials: { authorization: 'synthetic-auth' } }), { status: 'saved', protocol: 'website-api-v1' });
  assert.deepEqual(session.calls.at(-1).body, { authorization: 'synthetic-auth' });
  const unsupported = managementFixture({ pan139: { authorization: '' } });
  assert.deepEqual(await syncCatManagement(unsupported, { provider: 'yidong', credentials: { authorization: 'private' } }), { status: 'unsupported' });
  assert.equal(unsupported.calls.some(call => call.method === 'PUT'), false);
});
