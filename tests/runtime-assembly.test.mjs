import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:http';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createNodeNetwork, createScriptHost } from '@meowfilm/script-host';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createRuntime, startRuntime } from '../apps/runtime/entry.mjs';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { contracts } from '../apps/runtime/contracts.mjs';
import { createMusicScriptModule } from '../apps/runtime/modules/music-script.mjs';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';
import { createSourceRuntimeModule } from '../apps/runtime/modules/source-runtime.mjs';
import { deferred, turn, until, rawRequest } from '../packages/runtime-bridge/test/http-fixture.js';
import { lxScript } from '../packages/script-host/test/fixtures.js';

const config = () => ({ sessionId: randomUUID(), requestKey: randomBytes(32).toString('hex') });
function temporaryData() {
  const directory = fileURLToPath(new URL('../tmp/test-results/runtime-assembly/', import.meta.url));
  mkdirSync(directory, { recursive: true });
  return mkdtempSync(join(directory, 'case-'));
}
function fixture(id = 'music', close = () => {}) {
  const spec = contracts[id];
  return { id, version: spec.version, contract: spec.contract, methods: {
    [spec.prefix + (id === 'music' ? 'sources.list' : 'status')]:
      async input => ({ ok: true, contract: spec.contract, value: input }),
  }, upload() {}, close };
}
async function connect(t, modules) {
  const identity = config();
  const runtime = createAppRuntime({ ...identity, modules });
  await runtime.start();
  const client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
  t.after(async () => { await client.close(); await runtime.close().catch(() => {}); });
  return { runtime, client, identity };
}

function musicValue(reply) {
  assert.equal(reply.ok, true, JSON.stringify(reply));
  assert.equal(reply.contract, 'music.script.v3');
  return reply.value;
}
async function installMusic(client, source) {
  const installationId = randomUUID();
  musicValue(await client.upload('music', installationId, 1, Buffer.from(source), { basename: 'source.js' }));
  const added = musicValue(await client.call('music.script.sources.add', { installationId, sourceRevision: 1 })).item;
  assert.equal(added.installationId, installationId);
  assert.equal(added.enabled, false);
  const enabled = musicValue(await client.call('music.script.sources.enable', { installationId })).item;
  assert.equal(enabled.state, 'ready');
  return enabled;
}
const musicRequest = (installationId, musicInfo, siteId = 'kw') => ({
  installationId, siteId, operation: 'musicUrl', payload: { quality: '320k', musicInfo },
});

test('explicit session termination disposes each module exactly once while keepalive disconnect does not', async t => {
  let closes = 0;
  const { runtime, client } = await connect(t, [fixture('music', () => { closes++; })]);
  assert.equal((await client.call('music.script.sources.list', { number: 2 })).value.number, 2);
  await client.close(); await turn(); assert.equal(closes, 0);
  await runtime.close(); await runtime.close(); assert.equal(closes, 1);
});

test('HTTP close accepts only an empty object and rejects every other shape before side effects', async t => {
  let closes = 0;
  const { runtime, client, identity } = await connect(t, [fixture('music', () => { closes++; })]);
  for (const value of [true, 1, [], null, { x: 1 }]) {
    const reply = await rawRequest(runtime.baseUrl,
      { sessionId: identity.sessionId, key: identity.requestKey },
      { path: '/v1/close', body: JSON.stringify(value) }).response;
    assert.equal(reply.status, 400);
    assert.deepEqual(reply.value, { ok: false, contract: 'runtime.http.v1', code: 'INVALID_REQUEST' });
    assert.equal(closes, 0);
    assert.deepEqual((await client.call('music.script.sources.list', { still: 'open' })).value, { still: 'open' });
  }
  assert.deepEqual(await client.shutdown(), {
    ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true },
  });
  assert.equal(closes, 1);
  await runtime.close();
  assert.equal(closes, 1);
});

test('failed module cleanup is explicit without leaking provider diagnostics', async t => {
  const { runtime, client } = await connect(t, [fixture('music', () => { throw new Error('PRIVATE'); })]);
  assert.deepEqual(await client.shutdown(), { ok: false, contract: 'runtime.http.v1', code: 'INVALID_RESPONSE' });
  await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE', message: 'INVALID_RESPONSE' });
});

test('overlapping HTTP shutdown requests share disposal without waiting on each other', async t => {
  const entered = deferred(), release = deferred(); let closes = 0;
  const { client } = await connect(t, [fixture('music', async () => {
    closes++; entered.resolve(); await release.promise;
  })]);
  const first = client.shutdown(); await entered.promise;
  const second = client.shutdown();
  release.resolve();
  for (const reply of await Promise.all([first, second])) {
    assert.deepEqual(reply, { ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true } });
  }
  assert.equal(closes, 1);
});

for (const hostFailure of ['throw', 'incomplete']) {
  test(`App disposal reports a real source ${hostFailure} cleanup envelope and waits for every module`, async t => {
    const gate = deferred(), entered = deferred(); const counts = { music: 0, source: 0, cat: 0 };
    const source = createSourceRuntimeModule({
      hostFactory: () => ({ list: () => [], async close() {
        counts.source++;
        if (hostFailure === 'throw') throw new Error('PRIVATE');
        return { cleanupComplete: false };
      } }) });
    const { runtime } = await connect(t, [fixture('music', () => { counts.music++; }), source,
      fixture('cat', async () => { counts.cat++; entered.resolve(); await gate.promise; })]);
    let complete = false;
    const close = runtime.close();
    close.finally(() => { complete = true; }).catch(() => {});
    const failed = assert.rejects(close, { code: 'INVALID_RESPONSE' });
    await entered.promise; await turn();
    assert.deepEqual(counts, { music: 1, source: 1, cat: 1 }); assert.equal(complete, false);
    gate.resolve(); await failed;
    assert.deepEqual(await source.close(), { ok: false, contract: 'source.runtime.v2', code: 'SOURCE_CLEANUP' });
    await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE' });
    assert.deepEqual(counts, { music: 1, source: 1, cat: 1 });
  });
}

for (const failedModule of [undefined, 'music', 'cat']) {
  test(`App disposal consumes music Cat and source contracts (${failedModule ?? 'success'})`, async t => {
    const closes = { music: 0, cat: 0, source: 0 };
    const dataRoot = temporaryData();
    const music = createMusicScriptModule({ dataRoot,
      network: async () => assert.fail('passive modules must not use the network'),
      hostFactory: () => ({ async close() { closes.music++; return { cleanupComplete: failedModule !== 'music' }; } }) });
    const cat = createCatScriptModule({ dataRoot, runtimeFactory: () => ({
      async status() { return {}; }, async close() { closes.cat++; if (failedModule === 'cat') throw new Error('PRIVATE'); },
    }) });
    const source = createSourceRuntimeModule({
      hostFactory: () => ({ list: () => [], async close() { closes.source++; return { cleanupComplete: true }; } }) });
    const { runtime, client } = await connect(t, [music, cat, source]);
    t.after(async () => { await runtime.close().catch(() => {}); rmSync(dataRoot, { recursive: true }); });
    assert.equal((await client.call('cat.script.status')).ok, true);
    for (let i = 0; i < 2; i++) {
      if (failedModule) await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE' });
      else await runtime.close();
    }
    assert.deepEqual(closes, { music: 1, cat: 1, source: 1 });
    assert.equal((await music.close()).ok, failedModule !== 'music');
    assert.deepEqual(await source.close(), { ok: true, contract: 'source.runtime.v2', value: { closed: true, cleanupComplete: true } });
  });
}

test('invalid assembly cannot open a listener or override methods; failed factories dispose once', async () => {
  let closes = 0;
  const good = fixture('music', () => { closes++; });
  for (const modules of [[], [good, good], [{ ...good, id: undefined }], [{ ...good, version: '0.7.0' }],
    [{ ...good, methods: { 'other.method': () => null } }]]) {
    assert.throws(() => createAppRuntime({ ...config(), modules }), { code: 'INVALID_REQUEST' });
  }
  await turn(); assert.equal(closes, 4);
});

test('synchronous assembly receives no capabilities and handlers receive the authenticated HTTP context', async t => {
  const identity = config(); let assemblies = 0, received;
  const runtime = createAppRuntime({ ...identity, assemble(...args) {
    assemblies++;
    assert.deepEqual(args, []);
    const module = fixture();
    module.methods['music.script.sources.list'] = async (input, context) => {
      assert.equal(context.sessionId, identity.sessionId);
      assert.equal(context.isCurrent(), true);
      assert.equal(context.signal.aborted, false);
      received = { input, context };
      return { ok: true, contract: module.contract, value: input };
    };
    return [module];
  } });
  assert.equal(assemblies, 1);
  await runtime.start();
  const client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
  t.after(async () => { await client.close(); await runtime.close(); });
  const input = { text: '原样😀', é: 1, 'e\u0301': 2 };
  assert.deepEqual(await client.call('music.script.sources.list', input), {
    ok: true, contract: 'music.script.v3', value: input,
  });
  assert.deepEqual(received.input, input);
  assert.equal(received.context.isCurrent(), false);
  assert.equal(assemblies, 1);
  await runtime.close();
  await assert.rejects(client.call('music.script.sources.list'), { code: 'CLOSED' });
});

test('real LX private KV preserves installation site and generation authority across HTTP and runtime reopen', async t => {
  const dataRoot = temporaryData(), received = [];
  let host, reopened;
  const music = createMusicScriptModule({ dataRoot,
    network: () => assert.fail('unexpected network'),
    hostFactory: input => {
      host = createScriptHost({ ...input, storage: Object.fromEntries(['get', 'set', 'delete'].map(operation =>
        [operation, context => {
          assert.equal(context.isCurrent(), true);
          assert.equal(context.signal.aborted, false);
          received.push({ operation, context });
          return input.storage[operation](context);
        }])) });
      return host;
    },
  });
  const { runtime, client } = await connect(t, [music]);
  t.after(async () => {
    await runtime.close();
    await reopened?.client.close(); await reopened?.runtime.close();
    rmSync(dataRoot, { recursive: true });
  });
  const source = lxScript({ setup: 'let saved;', handler: `async ({info}, {storage}) => {
    const input = info.musicInfo;
    if (input.operation === 'save') {
      saved = storage;
      await storage.set('key', input.value);
      return {value: await storage.get('key'), global: typeof meow.storage};
    }
    if (input.operation === 'stale') {
      let code;
      try { await saved.set('key', 'old-write'); } catch (error) { code = error.code; }
      await storage.set('key', input.value);
      return {old: code, value: await storage.get('key')};
    }
    if (input.operation === 'delete') await storage.delete('key');
    return await storage.get('key');
  }` });
  const first = await installMusic(client, source), second = await installMusic(client, source);
  const value = { é: 1, 'e\u0301': 2, key: 'key' };
  const invoke = (item, input, siteId = 'kw') => client.call('music.script.invoke',
    musicRequest(item.installationId, input, siteId));
  assert.deepEqual(musicValue(await invoke(first, { operation: 'save', value })), { value, global: 'undefined' });
  assert.deepEqual(musicValue(await invoke(first, { operation: 'stale', value: 'tx-value' }, 'tx')),
    { old: 'PERMISSION_DENIED', value: 'tx-value' });
  assert.equal(musicValue(await invoke(second, {})), null);
  assert.deepEqual(musicValue(await invoke(first, {})), value);
  assert.equal(musicValue(await invoke(first, { operation: 'delete' }, 'tx')), null);
  assert.deepEqual(received.map(({ operation, context }) => [operation, context.installationId, context.siteId]), [
    ['set', first.installationId, 'kw'], ['get', first.installationId, 'kw'],
    ['set', first.installationId, 'tx'], ['get', first.installationId, 'tx'],
    ['get', second.installationId, 'kw'], ['get', first.installationId, 'kw'],
    ['delete', first.installationId, 'tx'], ['get', first.installationId, 'tx'],
  ]);
  for (const { context } of received) {
    assert.equal(context.generation, context.installationId === first.installationId ? first.generation : second.generation);
    assert.ok(Number.isSafeInteger(context.requestId) && context.requestId > 0);
    assert.equal(context.isCurrent(), false);
  }
  const persisted = () => JSON.parse(readFileSync(join(dataRoot, 'music/data', `${first.installationId}.json`), 'utf8'));
  assert.deepEqual(persisted(), { schema: 1, sites: { kw: [{ key: 'key', value }] } });
  const listed = musicValue(await client.call('music.script.sources.list'));
  assert.ok(Number.isSafeInteger(listed.revision) && listed.revision > 0);
  assert.deepEqual(listed.items.map(item => item.installationId), [first.installationId, second.installationId]);
  await runtime.close();
  for (const key of ['liveSessions', 'activeRequests', 'timers', 'networkRequests', 'pendingAdapterTasks'])
    assert.equal(host.stats()[key], 0, key);
  reopened = await connect(t, [createMusicScriptModule({ dataRoot,
    network: () => assert.fail('unexpected network after reopen') })]);
  const detail = musicValue(await reopened.client.call('music.script.sources.detail', {
    installationId: first.installationId,
  })).item;
  assert.equal(detail.enabled, true);
  assert.equal(detail.contentHash, first.contentHash);
  assert.deepEqual(musicValue(await reopened.client.call('music.script.invoke',
    musicRequest(first.installationId, {}))), value);
  assert.deepEqual(persisted(), { schema: 1, sites: { kw: [{ key: 'key', value }] } });
});

async function heldHttp() {
  const requests = new Map(), arrivals = new Map();
  const server = createServer((request, response) => {
    const closed = deferred(), socket = request.socket;
    socket.once('close', () => closed.resolve());
    const entry = { response, socket, closed: closed.promise };
    requests.set(request.url, entry);
    arrivals.get(request.url)?.resolve(entry);
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  return { baseUrl: `http://127.0.0.1:${server.address().port}`,
    wait(path) {
      if (requests.has(path)) return Promise.resolve(requests.get(path));
      if (!arrivals.has(path)) arrivals.set(path, deferred());
      return arrivals.get(path).promise;
    },
    async close() {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    },
  };
}

test('real LX cancellation fences old storage context while runtime close releases physical network handles',
  { timeout: 15000 }, async t => {
    const upstream = await heldHttp(), dataRoot = temporaryData();
    let host, runtime, client, physical = 0;
    const network = createNodeNetwork();
    t.after(async () => {
      await upstream.close();
      await client?.close(); await runtime?.close();
      rmSync(dataRoot, { recursive: true });
    });
    const music = createMusicScriptModule({ dataRoot,
      network: async input => {
        physical++;
        try { return await network(input); } finally { physical--; }
      },
      hostFactory: input => { host = createScriptHost(input); return host; },
    });
    ({ runtime, client } = await connect(t, [music]));
    const item = await installMusic(client, lxScript({
      setup: 'let saved, oldCode, finishOld; const oldFinished = new Promise(resolve => { finishOld = resolve; });',
      handler: `async ({info}, {storage}) => {
        const mode = info.musicInfo.mode;
        if (mode === 'pending') {
          saved = storage;
          await storage.set('key', 'initial');
          return new Promise(resolve => lx.request(${JSON.stringify(upstream.baseUrl + '/pending')}, {}, async error => {
            if (error) oldCode = error.code;
            else {
              try { await saved.set('key', 'old-pending'); oldCode = 'wrote'; }
              catch (failure) { oldCode = failure.code; }
            }
            finishOld(); resolve(oldCode);
          }));
        }
        if (mode === 'write') {
          let old;
          try { await saved.set('key', 'old-direct'); } catch (error) { old = error.code; }
          await storage.set('key', 'new-committed');
          return {old, value: await storage.get('key')};
        }
        if (mode === 'close') return new Promise(resolve =>
          lx.request(${JSON.stringify(upstream.baseUrl + '/close')}, {}, () => resolve('late')));
        await oldFinished;
        return {old: oldCode, value: await storage.get('key')};
      }`,
    }));
    const invoke = (mode, options) => client.call('music.script.invoke',
      musicRequest(item.installationId, { mode }), options);
    const controller = new AbortController();
    const pending = invoke('pending', { signal: controller.signal });
    const rejected = assert.rejects(pending, { code: 'CANCELLED' });
    const first = await upstream.wait('/pending');
    assert.equal(physical, 1);
    controller.abort(); await rejected;
    await until(() => runtime.state.active === 0);
    assert.equal(physical, 1, 'session-owned I/O remains real work after the invocation is cancelled');
    assert.equal(first.socket.destroyed, false);
    assert.deepEqual(musicValue(await invoke('write')), { old: 'PERMISSION_DENIED', value: 'new-committed' });
    const detail = musicValue(await client.call('music.script.sources.detail', { installationId: item.installationId })).item;
    assert.equal(detail.state, 'ready'); assert.equal(detail.generation, item.generation);
    first.response.end('released');
    assert.deepEqual(musicValue(await invoke('probe')), { old: 'PERMISSION_DENIED', value: 'new-committed' });
    await first.closed;
    assert.equal(physical, 0);
    const persisted = () => JSON.parse(readFileSync(join(dataRoot, 'music/data', `${item.installationId}.json`), 'utf8'));
    const expected = { schema: 1, sites: { kw: [{ key: 'key', value: 'new-committed' }] } };
    assert.deepEqual(persisted(), expected);
    const closingRequest = assert.rejects(invoke('close'), { code: 'CLOSED' });
    const closingNetwork = await upstream.wait('/close');
    assert.equal(physical, 1); assert.equal(closingNetwork.socket.destroyed, false);
    await runtime.close(); await closingRequest; await closingNetwork.closed;
    assert.equal(physical, 0); assert.equal(closingNetwork.socket.destroyed, true);
    assert.equal(runtime.state.active, 0);
    for (const key of ['liveSessions', 'activeRequests', 'timers', 'networkRequests', 'pendingAdapterTasks'])
      assert.equal(host.stats()[key], 0, key);
    assert.deepEqual(persisted(), expected);
  });

test('App close reports actual LX I/O debt when an adapter ignores cancellation instead of claiming physical cleanup',
  { timeout: 15000 }, async t => {
    const upstream = await heldHttp(), dataRoot = temporaryData(), settled = deferred();
    const network = createNodeNetwork(), physicalSignal = new AbortController();
    let runtime, client, authority, physical = 0;
    t.after(async () => {
      physicalSignal.abort();
      await upstream.close(); await client?.close(); await runtime?.close().catch(() => {});
      rmSync(dataRoot, { recursive: true });
    });
    const music = createMusicScriptModule({ dataRoot, network: async input => {
      authority = input; physical++;
      try { return await network({ ...input, signal: physicalSignal.signal }); }
      finally { physical--; settled.resolve(); }
    } });
    ({ runtime, client } = await connect(t, [music]));
    const item = await installMusic(client, lxScript({ handler: `async () => new Promise(resolve =>
      lx.request(${JSON.stringify(upstream.baseUrl + '/debt')}, {}, () => resolve('late')))` }));
    const pending = assert.rejects(client.call('music.script.invoke', musicRequest(item.installationId, {})), { code: 'CLOSED' });
    const request = await upstream.wait('/debt');
    await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE', message: 'INVALID_RESPONSE' });
    await pending;
    assert.equal(authority.signal.aborted, true);
    assert.equal(physical, 1); assert.equal(request.socket.destroyed, false);
    assert.deepEqual(await music.close(), { ok: false, contract: 'music.script.v3', code: 'SCRIPT_CLEANUP' });
    request.response.end('settled');
    await settled.promise; await request.closed;
    assert.equal(physical, 0); assert.equal(request.socket.destroyed, true);
    await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE' });
  });

test('assembly factories cannot mix modules or async results and bad identity cannot allocate owners', () => {
  for (const input of [{ modules: [fixture()], assemble() {} }, { assemble: 'invalid' },
    { assemble: async () => [fixture()] }]) {
    assert.throws(() => createAppRuntime({ ...config(), ...input }), { code: 'INVALID_REQUEST' });
  }
  assert.throws(() => createAppRuntime({ ...config(), requestKey: 'invalid', assemble: () => assert.fail('allocated') }),
    { code: 'INVALID_REQUEST' });
});

test('a synchronous factory that creates duplicate modules disposes its owner exactly once', async () => {
  const disposed = deferred(); let closes = 0;
  assert.throws(() => createAppRuntime({ ...config(), assemble() {
    const module = fixture('music', () => { closes++; disposed.resolve(); });
    return [module, module];
  } }), { code: 'INVALID_REQUEST' });
  await disposed.promise; await turn();
  assert.equal(closes, 1);
});

test('independent public create/start runtime exposes all three frozen versions and direct close', async t => {
  for (const factory of [async input => createRuntime(input).start(), startRuntime]) {
    const identity = config(), dataRoot = temporaryData();
    const hostFactory = () => ({ list: () => [], async close() { return { cleanupComplete: true }; } });
    const runtime = await factory({ ...identity, dataRoot, music: { hostFactory }, source: { hostFactory },
      cat: { runtimeFactory: input => {
        assert.equal(input.dataRoot, dataRoot);
        return { async status() { return {}; }, async close() {} };
      } } });
    const client = createRuntimeClient({ ...identity, baseUrl: runtime.baseUrl });
    t.after(async () => { await client.close(); await runtime.close(); rmSync(dataRoot, { recursive: true }); });
    const description = (await client.describe()).value;
    assert.equal(description.protocol, 'runtime.http.v1'); assert.equal(description.product, 'aggregate');
    assert.deepEqual(description.modules.map(({ id, version, contract }) => ({ id, version, contract })),
      ['music', 'cat', 'source'].map(id => ({ id, version: contracts[id].version, contract: contracts[id].contract })));
    assert.deepEqual(musicValue(await client.call('music.script.sources.list')), { revision: 0, items: [] });
    assert.equal((await client.call('cat.script.status')).ok, true);
    assert.deepEqual(await client.shutdown(), { ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true } });
  }
});
