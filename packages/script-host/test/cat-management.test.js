import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createCatRuntime } from '../src/cat-runtime.js';
import { validateCatSettings } from '../src/cat-settings.js';
import { fault } from '../src/errors.js';

const source = value => `module.exports = { home() { return { value: ${JSON.stringify(value)} }; } };`;
const digest = value => createHash('sha256').update(value).digest('hex');
const target = installationId => ({ installationId, siteId: 'default', operation: 'home', payload: {} });
function rootFor(t) {
  const root = mkdtempSync(join(tmpdir(), 'meow-cat-management-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return root;
}
function runtimeFor(t, options = {}) {
  const runtime = createCatRuntime({ dataRoot: rootFor(t), ...options });
  t.after(() => runtime.close());
  return runtime;
}
function disk(runtime) { return JSON.parse(readFileSync(join(runtime.dataRoot, 'settings.json'), 'utf8')); }
async function ready(runtime, id) {
  for (let attempt = 0; attempt < 500; attempt++) {
    const item = (await runtime.status()).instances.find(item => item.installationId === id);
    if (item?.state !== 'starting') { assert.equal(item?.state, 'ready'); return item; }
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.fail('startup did not finish');
}
async function serverFor(t, handler) {
  const server = http.createServer(handler);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(async () => {
    server.closeAllConnections();
    if (server.listening) await new Promise(resolve => server.close(resolve));
  });
  return { server, url: `http://127.0.0.1:${server.address().port}` };
}
function gate() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
}
const waitingSource = url => `
  const http = require('http');
  module.exports.start = () => new Promise((resolve, reject) => {
    const request = http.get(${JSON.stringify(url)}, response => {
      response.resume();
      response.on('end', () => reject(new Error('candidate rejected')));
    });
    request.on('error', reject);
  });
`;
const managedSource = (url = null) => `
  const http = require('http');
  let server, options, count = 0;
  module.exports.start = async config => {
    options = config;
    if (${JSON.stringify(url)}) await new Promise((resolve, reject) => {
      const request = http.get(${JSON.stringify(url)}, response => {
        response.on('end', resolve); response.resume();
      });
      request.on('error', reject);
    });
    server = http.createServer((request, response) => {
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify(request.url === '/config' || request.url === '/full-config'
        ? { video: { sites: [{ key: 'default', name: 'Managed', api: '/api/default', actions: ['home'] }] } }
        : { owner: options.id, count: ++count }));
    });
    await new Promise(resolve => server.listen(Number(process.env.PORT), '127.0.0.1', resolve));
  };
  module.exports.stop = () => server?.close();
`;

test('Cat management persists local identity and only successful changed content advances updatedAt',
  { timeout: 30000 }, async t => {
    let now = 1000;
    const runtime = runtimeFor(t, { clock: () => now });
    const installed = await runtime.install({ installationId: 'local', name: '本地猫源', source: source('one') });
    assert.equal(installed.enabled, true);
    assert.equal(installed.state, 'ready');
    assert.equal(installed.name, '本地猫源');
    assert.equal(installed.sourceType, 'local');
    assert.equal(installed.sourceUrl, null);
    assert.equal(installed.importedAt, 1000);
    assert.equal(installed.updatedAt, null);
    assert.equal(installed.updateState, 'idle');
    assert.equal(installed.updateError, null);
    assert.equal(installed.sites[0].key, 'default');
    assert.ok(installed.sites[0].actions.includes('home'));
    const other = await runtime.install({ installationId: 'other', source: source('other') });

    now = 2000;
    const edited = await runtime.edit({ installationId: 'local', name: '已编辑' });
    assert.equal(edited.generation, installed.generation);
    assert.equal(edited.updatedAt, null);
    await assert.rejects(runtime.edit({ installationId: 'local', name: 'wrong',
      sourceUrl: 'http://127.0.0.1/source.js' }), { code: 'INVALID_ARGUMENT' });
    assert.equal(runtime.instance('local').name, '已编辑');
    const unchanged = await runtime.update({ installationId: 'local', source: source('one') });
    assert.equal(unchanged.generation, installed.generation);
    assert.equal(unchanged.updateState, 'unchanged');
    assert.equal(unchanged.updatedAt, null);

    now = 3000;
    const changed = await runtime.update({ installationId: 'local', source: source('two') });
    assert.equal(changed.installationId, 'local');
    assert.equal(changed.importedAt, 1000);
    assert.equal(changed.updatedAt, 3000);
    assert.equal(changed.updateState, 'updated');
    assert.equal(changed.name, '已编辑');
    assert.ok(changed.generation > installed.generation);
    assert.deepEqual(await runtime.invoke(target('local')), { value: 'two' });
    await assert.rejects(runtime.invoke({ ...target('local'), generation: installed.generation }),
      { code: 'STALE_SESSION' });

    now = 4000;
    const restarted = await runtime.restart('local');
    assert.ok(restarted.generation > changed.generation);
    assert.equal(restarted.updatedAt, 3000);
    assert.equal(restarted.updateState, 'updated');
    assert.equal(runtime.instance('other').generation, other.generation);
    assert.equal(disk(runtime).scripts.find(item => item.installationId === 'local').name, '已编辑');
    assert.equal(existsSync(join(runtime.dataRoot, 'sources', installed.sourceHash + '.js')), false);
  });

test('Cat failed activation restores old executable source and description and persists a separate update failure',
  { timeout: 30000 }, async t => {
    let now = 1000;
    const runtime = runtimeFor(t, { clock: () => now });
    const original = await runtime.install({ installationId: 'restore', name: '旧描述',
      source: source('working'), version: 'legacy-version', config: { fixture: 'old' } });
    const oldWorker = runtime.getSession('restore');
    now = 9000;
    await assert.rejects(runtime.update({ installationId: 'restore',
      source: 'module.exports.start = () => { throw new Error("bad candidate"); };' }),
    { code: 'SCRIPT_EXCEPTION' });
    const failed = runtime.instance('restore');
    assert.equal(failed.enabled, true);
    assert.equal(failed.state, 'ready');
    assert.equal(failed.error, null);
    assert.equal(failed.updateState, 'failed');
    assert.equal(failed.updateError, 'SCRIPT_EXCEPTION');
    assert.equal(failed.name, original.name);
    assert.equal(failed.version, original.version);
    assert.equal(failed.sourceHash, original.sourceHash);
    assert.deepEqual(failed.sites, original.sites);
    assert.equal(failed.updatedAt, null);
    assert.equal(failed.importedAt, 1000);
    assert.equal(oldWorker.worker, null);
    assert.deepEqual(await runtime.invoke(target('restore')), { value: 'working' });
    assert.equal(readFileSync(join(runtime.dataRoot, 'sources', original.sourceHash + '.js'), 'utf8'), source('working'));
    assert.deepEqual(readdirSync(join(runtime.dataRoot, 'sources')), [original.sourceHash + '.js']);
    await assert.rejects(runtime.install({ installationId: 'restore', name: '不应提交',
      source: 'module.exports.start = () => { throw new Error("bad reimport"); };' }),
    { code: 'SCRIPT_EXCEPTION' });
    assert.equal(runtime.instance('restore').name, '旧描述');
    assert.equal(runtime.instance('restore').importedAt, 1000);
    await runtime.close();
    const reopened = runtimeFor(t, { dataRoot: runtime.dataRoot });
    const recovered = await ready(reopened, 'restore');
    assert.equal(recovered.updateState, 'failed');
    assert.equal(recovered.updateError, 'SCRIPT_EXCEPTION');
    assert.deepEqual(await reopened.invoke(target('restore')), { value: 'working' });
  });

test('Cat disabled installs and updates do not execute; sites and enabled intent survive cold starts and reimport',
  { timeout: 30000 }, async t => {
    let now = 10;
    const runtime = runtimeFor(t, { clock: () => now });
    const original = await runtime.install({ installationId: 'disabled', source: source('old') });
    const disabled = await runtime.setEnabled('disabled', false);
    assert.equal(disabled.enabled, false);
    assert.equal(disabled.state, 'disabled');
    assert.deepEqual(disabled.sites, original.sites);
    const failIfRun = 'module.exports.start = () => { throw new Error("must stay disabled"); };';
    now = 20;
    const updated = await runtime.update({ installationId: 'disabled', source: failIfRun });
    assert.equal(updated.state, 'disabled');
    assert.equal(updated.enabled, false);
    assert.equal(updated.updatedAt, 20);
    await assert.rejects(runtime.restart('disabled'), { code: 'DISABLED' });
    await assert.rejects(runtime.invoke(target('disabled')), { code: 'DISABLED' });
    await assert.rejects(runtime.management('disabled'), { code: 'DISABLED' });
    await runtime.close();

    const reopened = runtimeFor(t, { dataRoot: runtime.dataRoot, clock: () => now });
    const cold = (await reopened.start()).instances[0];
    assert.equal(cold.state, 'disabled');
    assert.equal(reopened.hostState('disabled'), null);
    assert.deepEqual(cold.sites, original.sites);
    now = 30;
    const reimported = await reopened.install({ installationId: 'disabled', name: '重新导入',
      source: source('new') });
    assert.equal(reimported.enabled, false);
    assert.equal(reimported.importedAt, 10);
    assert.equal(reimported.updatedAt, 30);
    assert.equal((await reopened.setEnabled('disabled', true)).enabled, true);
    assert.deepEqual(await reopened.invoke(target('disabled')), { value: 'new' });
    const running = reopened.instance('disabled');
    const runningWorker = reopened.getSession('disabled');
    const stoppedReimport = await reopened.install({ installationId: 'disabled', name: '停用重导入',
      source: failIfRun, enabled: false });
    assert.equal(stoppedReimport.enabled, false);
    assert.equal(stoppedReimport.state, 'disabled');
    assert.equal(stoppedReimport.name, '停用重导入');
    assert.equal(stoppedReimport.importedAt, 10);
    assert.equal(stoppedReimport.generation, running.generation);
    assert.deepEqual(stoppedReimport.sites, running.sites);
    assert.equal(stoppedReimport.sourceHash, digest(failIfRun));
    assert.equal(runningWorker.worker, null);
    assert.equal(disk(reopened).scripts.find(item => item.installationId === 'disabled').enabled, false);

    const neverRun = await reopened.install({ installationId: 'never-run', source: failIfRun, enabled: false });
    assert.equal(neverRun.state, 'disabled');
    assert.equal(neverRun.enabled, false);
    assert.equal(neverRun.importedAt, 30);
    assert.equal(neverRun.updatedAt, null);
  });

test('Cat network import follows redirects, stores the supplied URL and updates only from its edited URL',
  { timeout: 30000 }, async t => {
    let body = source('remote'), now = 100;
    const paths = [];
    const { url } = await serverFor(t, (request, response) => {
      paths.push(request.url);
      if (request.url === '/redirect') { response.writeHead(302, { location: '/source' }); response.end(); }
      else if (request.url === '/error') { response.writeHead(503); response.end('unavailable'); }
      else response.end(body);
    });
    const runtime = runtimeFor(t, { clock: () => now });
    const imported = await runtime.import({ installationId: 'network', name: '网络猫源', sourceUrl: url + '/redirect' });
    assert.equal(imported.sourceType, 'network');
    assert.equal(imported.sourceUrl, url + '/redirect');
    assert.equal(imported.enabled, true);
    assert.deepEqual(paths, ['/redirect', '/source']);
    const edited = await runtime.edit({ installationId: 'network', name: '新地址', sourceUrl: url + '/changed' });
    assert.equal(edited.generation, imported.generation);
    assert.equal(edited.sourceType, 'network');
    assert.equal(edited.updatedAt, null);
    assert.equal((await runtime.update({ installationId: 'network' })).updateState, 'unchanged');
    body = source('remote updated');
    now = 200;
    const updated = await runtime.update({ installationId: 'network' });
    assert.equal(updated.updatedAt, 200);
    assert.equal(updated.importedAt, 100);
    assert.equal(paths.at(-1), '/changed');
    assert.deepEqual(await runtime.invoke(target('network')), { value: 'remote updated' });
    await runtime.edit({ installationId: 'network', name: '新地址', sourceUrl: url + '/error' });
    await assert.rejects(runtime.update({ installationId: 'network' }), { code: 'HTTP_ERROR' });
    assert.equal(runtime.instance('network').updateState, 'failed');
    assert.equal(runtime.instance('network').updatedAt, 200);
    assert.deepEqual(await runtime.invoke(target('network')), { value: 'remote updated' });
    await assert.rejects(runtime.update({ installationId: 'network', source: source('staged') }),
      { code: 'INVALID_ARGUMENT' });
    await assert.rejects(runtime.import({ installationId: 'bad-url', name: 'bad', sourceUrl: 'file:///tmp/source.js' }),
      { code: 'INVALID_ARGUMENT' });
    assert.equal((await runtime.status()).instances.length, 1);
  });

test('Cat downloads report bounded bytes, invalid encoding, disconnect and timeout without replacing the old source',
  { timeout: 30000 }, async t => {
    let mode = 'valid';
    const { url } = await serverFor(t, (request, response) => {
      if (mode === 'valid') response.end(source('valid'));
      else if (mode === 'large') response.end('x'.repeat(2048));
      else if (mode === 'invalid') response.end(Buffer.from([0xff]));
      else if (mode === 'disconnect') request.socket.destroy();
      // Timeout mode deliberately leaves the response open.
    });
    const runtime = runtimeFor(t, { limits: { maxSourceBytes: 1024, requestTimeoutMs: 150 } });
    const original = await runtime.import({ installationId: 'download', name: 'download', sourceUrl: url });
    for (const [nextMode, code] of [['large', 'RESOURCE_LIMIT'], ['invalid', 'SCRIPT_PROTOCOL_ERROR'],
      ['disconnect', 'NETWORK_ERROR'], ['timeout', 'TIMEOUT']]) {
      mode = nextMode;
      await assert.rejects(runtime.update({ installationId: 'download' }), { code });
      const result = runtime.instance('download');
      assert.equal(result.updateState, 'failed');
      assert.equal(result.updateError, code);
      assert.equal(result.sourceHash, original.sourceHash);
      assert.equal(result.updatedAt, null);
      assert.equal(result.generation, original.generation);
    }
    mode = 'valid';
    assert.equal((await runtime.update({ installationId: 'download' })).updateState, 'unchanged');
    assert.equal(runtime.instance('download').updateError, null);
    assert.deepEqual(await runtime.invoke(target('download')), { value: 'valid' });
  });

test('Cat cancellation releases a downloading connection and persists failure while the old instance keeps running',
  { timeout: 30000 }, async t => {
    const received = gate(), released = gate();
    let hanging = false;
    const { url } = await serverFor(t, (request, response) => {
      if (!hanging) { response.end(source('old')); return; }
      response.on('close', released.resolve);
      received.resolve();
    });
    const runtime = runtimeFor(t);
    const original = await runtime.import({ installationId: 'cancel', name: 'cancel', sourceUrl: url });
    const controller = new AbortController();
    hanging = true;
    const update = runtime.update({ installationId: 'cancel' }, { signal: controller.signal });
    const rejected = assert.rejects(update, { code: 'CANCELLED' });
    await received.promise;
    controller.abort();
    await rejected;
    await released.promise;
    const current = runtime.instance('cancel');
    assert.equal(current.generation, original.generation);
    assert.equal(current.updateState, 'failed');
    assert.equal(current.updateError, 'CANCELLED');
    assert.equal(current.updatedAt, null);
    assert.deepEqual(await runtime.invoke(target('cancel')), { value: 'old' });
  });

test('Cat candidate activation hides uncommitted state and cannot overlap workers on one private volume',
  { timeout: 30000 }, async t => {
    const entered = gate(), release = gate();
    const { url } = await serverFor(t, async (_request, response) => {
      entered.resolve();
      await release.promise;
      response.end('reject');
    });
    const runtime = runtimeFor(t);
    const original = await runtime.install({ installationId: 'isolated', name: 'accepted', source: source('old') });
    const oldWorker = runtime.getSession('isolated');
    const workers = [oldWorker], overlaps = [];
    const create = runtime.workerSession.bind(runtime);
    runtime.workerSession = input => {
      if (input.installationId === 'isolated') {
        overlaps.push(workers.filter(worker => worker.worker !== null).length);
        const worker = create(input);
        workers.push(worker);
        return worker;
      }
      return create(input);
    };
    const update = runtime.update({ installationId: 'isolated', source: waitingSource(url) });
    const rejected = assert.rejects(update, { code: 'SCRIPT_EXCEPTION' });
    await entered.promise;
    const pending = (await runtime.status()).instances[0];
    assert.equal(pending.state, 'starting');
    assert.equal(pending.sourceHash, original.sourceHash);
    assert.equal(pending.name, original.name);
    assert.deepEqual(pending.sites, original.sites);
    assert.equal(disk(runtime).scripts[0].sourceHash, original.sourceHash);
    assert.equal(oldWorker.worker, null);
    await assert.rejects(runtime.invoke(target('isolated')), { code: 'STARTING' });
    release.resolve();
    await rejected;
    assert.deepEqual(overlaps, [0, 0]);
    assert.equal(workers[1].worker, null);
    assert.deepEqual(await runtime.invoke(target('isolated')), { value: 'old' });
    await runtime.update({ installationId: 'isolated', source: source('accepted update') });
    assert.deepEqual(overlaps, [0, 0, 0]);
    assert.deepEqual(await runtime.invoke(target('isolated')), { value: 'accepted update' });
  });

for (const operation of ['cancel', 'disable', 'uninstall', 'close']) {
  test(`Cat ${operation} during candidate startup stops the worker and keeps only accepted source recoverable`,
    { timeout: 30000 }, async t => {
      const entered = gate(), disconnected = gate();
      const { url } = await serverFor(t, (_request, response) => {
        response.on('close', disconnected.resolve);
        entered.resolve();
      });
      const runtime = runtimeFor(t);
      const original = await runtime.install({ installationId: 'active', source: source('accepted') });
      const controller = new AbortController();
      const pending = runtime.update({ installationId: 'active', source: waitingSource(url) },
        { signal: controller.signal });
      const code = operation === 'close' ? 'HOST_CLOSED' : 'CANCELLED';
      const rejected = assert.rejects(pending, { code });
      await entered.promise;
      const candidate = runtime.host.initializingSessions()[0];
      assert.ok(candidate.worker);
      if (operation === 'cancel') controller.abort();
      if (operation === 'disable') await runtime.setEnabled('active', false);
      if (operation === 'uninstall') await runtime.uninstall('active');
      if (operation === 'close') await runtime.close();
      await rejected;
      await disconnected.promise;
      assert.equal(candidate.worker, null);
      if (operation === 'uninstall') {
        assert.deepEqual((await runtime.status()).instances, []);
        assert.deepEqual(disk(runtime).scripts, []);
        assert.equal(existsSync(join(runtime.dataRoot, 'volumes', 'active')), false);
        assert.deepEqual(readdirSync(join(runtime.dataRoot, 'sources')), []);
      } else {
        const saved = disk(runtime).scripts[0];
        assert.equal(saved.sourceHash, original.sourceHash);
        assert.equal(saved.updatedAt, null);
        assert.equal(saved.updateState, 'failed');
        assert.equal(saved.updateError, code);
        if (operation === 'disable') {
          assert.equal(saved.enabled, false);
          assert.equal(runtime.instance('active').state, 'disabled');
          assert.deepEqual(runtime.instance('active').sites, original.sites);
          await runtime.setEnabled('active', true);
        }
        if (operation === 'close') {
          assert.equal(runtime.host, null);
          const reopened = runtimeFor(t, { dataRoot: runtime.dataRoot });
          await ready(reopened, 'active');
          assert.deepEqual(await reopened.invoke(target('active')), { value: 'accepted' });
        } else assert.deepEqual(await runtime.invoke(target('active')), { value: 'accepted' });
      }
    });
}

test('Cat failed settings commit restores source and old runtime; an already committed save is reconciled',
  { timeout: 30000 }, async t => {
    let now = 10;
    const runtime = runtimeFor(t, { clock: () => now });
    const original = await runtime.install({ installationId: 'commit', source: source('old') });
    const save = runtime.settings.save.bind(runtime.settings);
    let fail = true;
    runtime.settings.save = change => {
      const proposed = change(runtime.settings.read());
      if (fail && proposed.scripts[0].sourceHash !== original.sourceHash) {
        fail = false;
        throw fault('STORAGE_ERROR');
      }
      return save(() => proposed);
    };
    now = 20;
    await assert.rejects(runtime.update({ installationId: 'commit', source: source('new') }), { code: 'STORAGE_ERROR' });
    assert.equal(runtime.instance('commit').sourceHash, original.sourceHash);
    assert.equal(runtime.instance('commit').updateState, 'failed');
    assert.equal(runtime.instance('commit').updatedAt, null);
    assert.deepEqual(await runtime.invoke(target('commit')), { value: 'old' });
    assert.deepEqual(readdirSync(join(runtime.dataRoot, 'sources')), [original.sourceHash + '.js']);
    runtime.settings.save = change => {
      save(change);
      throw fault('COMMIT_UNKNOWN');
    };
    now = 30;
    await assert.rejects(runtime.update({ installationId: 'commit', source: source('committed') }),
      { code: 'COMMIT_UNKNOWN' });
    assert.equal(disk(runtime).scripts[0].sourceHash, digest(source('committed')));
    assert.equal(runtime.instance('commit').sourceHash, digest(source('committed')));
    assert.equal(runtime.instance('commit').updateState, 'updated');
    assert.equal(runtime.instance('commit').updatedAt, 30);
    assert.deepEqual(await runtime.invoke(target('commit')), { value: 'committed' });
    assert.deepEqual(readdirSync(join(runtime.dataRoot, 'sources')), [digest(source('committed')) + '.js']);
    runtime.settings.save = save;
    await runtime.uninstall('commit');
    assert.deepEqual(readdirSync(join(runtime.dataRoot, 'sources')), []);
  });

test('Cat preserves enabled intent and timestamps when activation or restart fails',
  { timeout: 30000 }, async t => {
    const runtime = runtimeFor(t, { clock: () => 100 });
    const badSource = 'module.exports.start = () => { throw new Error("activation"); };';
    await runtime.install({ installationId: 'failed', source: badSource, enabled: false });
    await assert.rejects(runtime.setEnabled('failed', true), { code: 'SCRIPT_EXCEPTION' });
    let result = runtime.instance('failed');
    assert.equal(result.enabled, true);
    assert.equal(result.state, 'failed');
    assert.equal(result.importedAt, 100);
    assert.equal(result.updatedAt, null);
    assert.equal(result.updateState, 'idle');
    await assert.rejects(runtime.restart('failed'), { code: 'SCRIPT_EXCEPTION' });
    result = runtime.instance('failed');
    assert.equal(result.enabled, true);
    assert.equal(result.updatedAt, null);
    assert.equal((await runtime.setEnabled('failed', false)).state, 'disabled');
    await assert.rejects(runtime.restart('failed'), { code: 'DISABLED' });
  });

test('Cat legacy settings migrate only real old fields; new metadata rejects partial or builtin records',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const text = source('legacy'), sourceHash = digest(text);
    mkdirSync(join(root, 'sources'));
    writeFileSync(join(root, 'sources', sourceHash + '.js'), text);
    const legacy = { version: 1, port: 0, providers: {}, mappings: [], scripts: [
      { installationId: 'legacy', sourceHash, enabled: false, version: '1', config: {} },
    ] };
    writeFileSync(join(root, 'settings.json'), JSON.stringify(legacy));
    const runtime = runtimeFor(t, { dataRoot: root });
    const item = (await runtime.start()).instances[0];
    assert.equal(item.name, 'legacy');
    assert.equal(item.sourceType, 'local');
    assert.equal(item.sourceUrl, null);
    assert.equal(item.importedAt, null);
    assert.equal(item.updatedAt, null);
    assert.equal(item.enabled, false);
    await runtime.edit({ installationId: 'legacy', name: '历史猫源' });
    assert.equal(disk(runtime).scripts[0].name, '历史猫源');
    assert.equal(disk(runtime).scripts[0].importedAt, null);
    assert.throws(() => validateCatSettings({ ...legacy, scripts: [
      { ...legacy.scripts[0], name: 'partial' },
    ] }), { code: 'INVALID_ARGUMENT' });
    const current = disk(runtime);
    current.scripts[0].sourceType = 'builtin';
    assert.throws(() => validateCatSettings(current), { code: 'INVALID_ARGUMENT' });
  });

test('Cat accepted Buffer and syntax update failures retain the real owner and persist the update error',
  { timeout: 30000 }, async t => {
    const runtime = runtimeFor(t);
    const installed = await runtime.install({ installationId: 'preflight',
      source: 'let count = 0; module.exports.home = () => ({ count: ++count });' });
    const owner = runtime.getSession('preflight');
    assert.deepEqual(await runtime.invoke(target('preflight')), { count: 1 });
    let count = 1;
    for (const [source, code] of [
      [Buffer.from([0xff]), 'SCRIPT_PROTOCOL_ERROR'],
      [Buffer.from('\uFEFFmodule.exports = {};'), 'SCRIPT_PROTOCOL_ERROR'],
      ['module.exports = { home( };', 'SCRIPT_EXCEPTION'],
    ]) {
      await assert.rejects(runtime.update({ installationId: 'preflight', source }), { code });
      const current = runtime.instance('preflight');
      assert.equal(current.state, 'ready');
      assert.equal(current.generation, installed.generation);
      assert.equal(current.sourceHash, installed.sourceHash);
      assert.equal(current.updatedAt, null);
      assert.equal(current.updateState, 'failed');
      assert.equal(current.updateError, code);
      assert.equal(runtime.getSession('preflight'), owner);
      assert.deepEqual(await runtime.invoke(target('preflight')), { count: ++count });
    }
  });

for (const outcome of ['failure', 'cancel', 'close']) {
  test(`Cat first install ${outcome} exposes starting without publishing or retaining the candidate`,
    { timeout: 30000 }, async t => {
      const entered = gate(), disconnected = gate();
      const { url } = await serverFor(t, (_request, response) => {
        response.on('close', disconnected.resolve);
        entered.resolve(response);
      });
      const runtime = runtimeFor(t);
      const controller = new AbortController();
      const pending = runtime.install({ installationId: 'first', source: waitingSource(url) },
        { signal: controller.signal });
      const rejected = assert.rejects(pending, { code: outcome === 'cancel' ? 'CANCELLED'
        : outcome === 'close' ? 'HOST_CLOSED' : 'SCRIPT_EXCEPTION' });
      const response = await entered.promise;
      const candidate = runtime.host.initializingSessions()[0];
      const starting = (await runtime.status()).instances[0];
      assert.equal(starting.state, 'starting');
      assert.equal(starting.sourceHash, null);
      assert.deepEqual(starting.sites, []);
      assert.deepEqual(disk(runtime).scripts, []);
      assert.deepEqual(readdirSync(join(runtime.dataRoot, 'sources')), []);
      if (outcome === 'cancel') controller.abort();
      else if (outcome === 'close') await runtime.close();
      else response.end('reject');
      await rejected;
      await disconnected.promise;
      assert.equal(candidate.worker, null);
      const remaining = outcome === 'close' ? runtimeFor(t, { dataRoot: runtime.dataRoot }) : runtime;
      assert.deepEqual((await remaining.status()).instances, []);
      assert.deepEqual(disk(runtime).scripts, []);
      assert.deepEqual(readdirSync(join(runtime.dataRoot, 'sources')), []);
      assert.equal(existsSync(join(runtime.dataRoot, 'volumes', 'first')), false);
      await assert.rejects(remaining.setEnabled('first', false), { code: 'NOT_FOUND' });
      assert.deepEqual(await remaining.uninstall('first'), { applied: true, failures: [] });
      const next = await remaining.install({ installationId: 'first', source: source('accepted') });
      controller.abort();
      assert.equal(remaining.instance('first').generation, next.generation);
      assert.deepEqual(await remaining.invoke(target('first')), { value: 'accepted' });
    });
}

for (const phase of ['download', 'activation']) {
  test(`Cat ${phase} permits independent management and preserves interleaved settings commits`,
    { timeout: 30000 }, async t => {
      const entered = gate(), release = gate();
      let held = false;
      const { url } = await serverFor(t, async (_request, response) => {
        if (!held) { response.end(source('old-a')); return; }
        entered.resolve(response);
        await release.promise;
        response.end(phase === 'download' ? source('new-a') : 'reject');
      });
      t.after(() => release.resolve());
      const runtime = runtimeFor(t);
      const a = phase === 'download'
        ? await runtime.import({ installationId: 'a', name: 'A', sourceUrl: url })
        : await runtime.install({ installationId: 'a', name: 'A', source: source('old-a'), config: { id: 'A' } });
      const bSource = managedSource();
      await runtime.install({ installationId: 'b', name: 'B', source: bSource, config: { id: 'B' } });
      held = true;
      const changing = phase === 'download'
        ? runtime.update({ installationId: 'a' })
        : runtime.install({ installationId: 'a', name: 'uncommitted A',
          source: waitingSource(url), config: { id: 'candidate-A' } });
      const settled = phase === 'download' ? changing : assert.rejects(changing, { code: 'SCRIPT_EXCEPTION' });
      await entered.promise;
      let done = false;
      settled.then(() => { done = true; }, () => { done = true; });
      const candidates = runtime.host.initializingSessions().filter(worker => worker.installationId === 'a');
      if (phase === 'activation') {
        assert.equal(candidates.length, 1);
        assert.equal((await runtime.status()).instances.find(item => item.installationId === 'a').state, 'starting');
      } else assert.deepEqual(await runtime.invoke(target('a')), { value: 'old-a' });

      const edited = await runtime.edit({ installationId: 'b', name: 'B edited during A' });
      const unchanged = await runtime.update({ installationId: 'b', source: bSource });
      assert.equal(unchanged.generation, edited.generation);
      const changed = await runtime.update({ installationId: 'b', source: bSource + '\n// accepted B update' });
      const restarted = await runtime.restart('b');
      assert.ok(restarted.generation > changed.generation);
      const bOwner = runtime.getSession('b');
      assert.deepEqual(await runtime.invoke(target('b')), { count: 1, owner: 'B' });
      assert.equal(runtime.instance('b').generation, restarted.generation);
      await runtime.install({ installationId: 'c', source: source('independent-c'), config: { id: 'C' } });
      assert.equal(done, false, 'independent management must finish before A leaves its I/O barrier');
      const before = disk(runtime).scripts.find(item => item.installationId === 'b');
      assert.deepEqual(before.config, { id: 'B' });
      assert.deepEqual(before.sites, restarted.sites);
      release.resolve();
      await settled;
      assert.deepEqual(disk(runtime).scripts.find(item => item.installationId === 'b'), before);
      assert.equal(runtime.getSession('b'), bOwner);
      assert.equal(runtime.instance('b').generation, restarted.generation);
      assert.deepEqual(await runtime.invoke(target('b')), { count: 2, owner: 'B' });
      assert.deepEqual(await runtime.invoke(target('c')), { value: 'independent-c' });
      assert.deepEqual(await runtime.invoke(target('a')), { value: phase === 'download' ? 'new-a' : 'old-a' });
      if (phase === 'activation') {
        assert.equal(candidates[0].worker, null);
        assert.equal(runtime.instance('a').sourceHash, a.sourceHash);
        assert.deepEqual(disk(runtime).scripts.find(item => item.installationId === 'a').config, { id: 'A' });
      }
    });
}

test('Cat settings CAS rejects an external same-installation edit without overwriting other installations',
  { timeout: 30000 }, async t => {
    const entered = gate(), release = gate();
    const { url } = await serverFor(t, async (_request, response) => {
      entered.resolve();
      await release.promise;
      response.end('continue');
    });
    t.after(() => release.resolve());
    const runtime = runtimeFor(t);
    const original = await runtime.install({ installationId: 'a', name: 'original', source: source('old-a') });
    await runtime.install({ installationId: 'b', name: 'B', source: source('B') });
    const candidateSource = managedSource(url);
    const pending = runtime.update({ installationId: 'a', source: candidateSource });
    const rejected = assert.rejects(pending, { code: 'STORAGE_CONFLICT' });
    await entered.promise;
    const candidate = runtime.host.initializingSessions().find(worker => worker.installationId === 'a');
    const changed = disk(runtime);
    changed.scripts.find(item => item.installationId === 'a').name = 'external edit';
    writeFileSync(runtime.settings.path, JSON.stringify(changed));
    await runtime.edit({ installationId: 'b', name: 'committed B' });
    const b = disk(runtime).scripts.find(item => item.installationId === 'b');
    release.resolve();
    await rejected;
    assert.equal(candidate.worker, null);
    const after = disk(runtime);
    assert.equal(after.scripts.find(item => item.installationId === 'a').name, 'external edit');
    assert.equal(after.scripts.find(item => item.installationId === 'a').sourceHash, original.sourceHash);
    assert.deepEqual(after.scripts.find(item => item.installationId === 'b'), b);
    assert.deepEqual(await runtime.invoke(target('a')), { value: 'old-a' });
    assert.deepEqual(await runtime.invoke(target('b')), { value: 'B' });
    assert.equal(existsSync(join(runtime.dataRoot, 'sources', digest(candidateSource) + '.js')), false);
  });

test('Cat rejected replacement preflight cannot stop an initialization already owning the private volume',
  { timeout: 30000 }, async t => {
    for (const kind of ['encoding', 'syntax', 'download']) {
      await t.test(kind, async t => {
        const entered = gate(), release = gate(), downloaded = gate();
        const { url } = await serverFor(t, async (request, response) => {
          if (request.url === '/bad') {
            response.writeHead(503);
            response.end('unavailable', downloaded.resolve);
            return;
          }
          entered.resolve();
          await release.promise;
          response.end('continue');
        });
        t.after(() => release.resolve());
        const runtime = runtimeFor(t);
        const original = await runtime.install({ installationId: 'a', source: managedSource(url),
          config: { id: 'original' }, enabled: false });
        const enabling = runtime.setEnabled('a', true);
        await entered.promise;
        const owner = runtime.host.initializingSessions()[0];
        const pending = kind === 'download'
          ? runtime.import({ installationId: 'a', name: 'unaccepted', sourceUrl: url + '/bad' })
          : runtime.update({ installationId: 'a',
            source: kind === 'encoding' ? Buffer.from([0xff]) : 'module.exports = { home( };' });
        const rejected = assert.rejects(pending, { code: kind === 'download' ? 'HTTP_ERROR'
          : kind === 'encoding' ? 'SCRIPT_PROTOCOL_ERROR' : 'SCRIPT_EXCEPTION' });
        if (kind === 'download') await downloaded.promise;
        // The earlier initializer remains live until its actual HTTP gate opens.
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(owner.live, true);
        assert.ok(owner.worker);
        assert.equal(runtime.instance('a').state, 'starting');
        release.resolve();
        const enabled = await enabling;
        await rejected;
        assert.equal(runtime.getSession('a'), owner);
        assert.equal(runtime.instance('a').generation, enabled.generation);
        assert.equal(runtime.instance('a').sourceHash, original.sourceHash);
        assert.deepEqual(await runtime.invoke(target('a')), { owner: 'original', count: 1 });
      });
    }
  });

test('Cat replacement waits for real worker exit and adapter drain while another installation stays callable',
  { timeout: 30000 }, async t => {
    const entered = gate(), aborted = gate(), release = gate();
    t.after(() => release.resolve({ statusCode: 200, headers: {}, body: 'released' }));
    const runtime = runtimeFor(t, { network: input => {
      entered.resolve(input);
      input.signal.addEventListener('abort', aborted.resolve, { once: true });
      return release.promise;
    } });
    await runtime.install({ installationId: 'a', source: `
      module.exports.home = () => {
        require('http').get('http://fixture.invalid/held', response => response.resume()).on('error', () => {});
        return { started: true };
      };
    ` });
    const b = await runtime.install({ installationId: 'b',
      source: 'let count = 0; module.exports.home = () => ({ count: ++count });' });
    const bOwner = runtime.getSession('b'), owner = runtime.getSession('a');
    assert.deepEqual(await runtime.invoke(target('a')), { started: true });
    const input = await entered.promise;
    const exited = once(owner.worker, 'exit');
    const creating = [], create = runtime.workerSession.bind(runtime);
    runtime.workerSession = input => {
      if (input.installationId === 'a') creating.push({
        oldWorker: owner.worker, pending: owner.adapterTasks.size,
      });
      return create(input);
    };
    let done = false;
    const replacement = runtime.update({ installationId: 'a', source: source('replacement') });
    replacement.then(() => { done = true; }, () => { done = true; });
    await aborted.promise;
    await exited;
    await owner.stopping;
    assert.equal(input.signal.aborted, true);
    assert.equal(owner.worker, null);
    assert.equal(owner.adapterTasks.size, 1);
    assert.equal(done, false);
    assert.deepEqual(creating, []);
    assert.equal(runtime.instance('a').state, 'starting');
    assert.deepEqual(await runtime.invoke(target('b')), { count: 1 });
    release.resolve({ statusCode: 200, headers: {}, body: 'released' });
    await replacement;
    assert.deepEqual(creating, [{ oldWorker: null, pending: 0 }]);
    assert.equal(runtime.getSession('b'), bOwner);
    assert.equal(runtime.instance('b').generation, b.generation);
    assert.deepEqual(await runtime.invoke(target('b')), { count: 2 });
    assert.deepEqual(await runtime.invoke(target('a')), { value: 'replacement' });
  });
