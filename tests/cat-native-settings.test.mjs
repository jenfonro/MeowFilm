import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createCatScriptModule } from '../apps/runtime/modules/cat-script.mjs';

const temporaryRoot = fileURLToPath(new URL('../tmp/test-results/', import.meta.url));
const source = value => `module.exports = { home() { return { value: ${JSON.stringify(value)} }; } };`;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const configuration = script => ({ version: 1, port: 0, providers: {}, mappings: [], scripts: [script] });

function fixture(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'cat-native-settings-'));
  const local = (value, revision = 1) => {
    const path = join(root, `selected-${revision}.js`), bytes = source(value);
    writeFileSync(path, bytes);
    return { installationId: 'cat', name: '猫源', sourceType: 'local', sourceUrl: null,
      localPath: path, localHash: hash(bytes), enabled: true, inputRevision: revision };
  };
  let latest = configuration(local('first')), cached = structuredClone(latest);
  const modules = [];
  const start = () => {
    cached = structuredClone(latest);
    const module = createCatScriptModule({ dataRoot: root, configuration: {
      read: () => structuredClone(cached),
      async reload() { cached = structuredClone(latest); return structuredClone(cached); }
    } });
    modules.push(module);
    return module;
  };
  t.after(async () => {
    await Promise.all(modules.map(module => module.close()));
    rmSync(root, { recursive: true, force: true });
  });
  return { root, local, start, get latest() { return latest; }, set latest(value) { latest = value; } };
}

async function call(module, operation, input = {}) {
  const reply = await module.methods['cat.script.' + operation](input);
  assert.equal(reply.ok, true, JSON.stringify(reply));
  return reply.value;
}
async function state(module, revision, expected = 'ready') {
  const deadline = Date.now() + 10000;
  for (;;) {
    const current = (await call(module, 'status')).instances.find(value => value.installationId === 'cat');
    if (current?.inputRevision === revision && current.state === expected) return current;
    assert.ok(Date.now() < deadline, JSON.stringify(current));
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}
async function home(module, current) {
  return call(module, 'invoke', { installationId: 'cat', generation: current.generation,
    siteId: 'default', operation: 'home', payload: {} });
}
async function server(t, handler) {
  const value = http.createServer(handler);
  value.listen(0, '127.0.0.1');
  await once(value, 'listening');
  t.after(async () => { value.closeAllConnections(); await new Promise(resolve => value.close(resolve)); });
  return `http://127.0.0.1:${value.address().port}/source.js`;
}
const network = (previous, url, inputRevision) => ({ ...previous, sourceType: 'network', sourceUrl: url,
  localPath: null, localHash: null, inputRevision });

test('failed native network update records preparation errors without replacing the accepted worker',
  { timeout: 30000 }, async t => {
    const f = fixture(t);
    let content = source('accepted');
    const url = await server(t, (_request, response) => response.end(content));
    f.latest = configuration(network(f.latest.scripts[0], url, 1));
    const module = f.start(), accepted = await state(module, 1);
    content = 'module.exports = { start( };';
    const reply = await module.methods['cat.script.update']({ installationId: 'cat' });
    assert.equal(reply.ok, false);
    assert.equal(reply.code, 'SCRIPT_EXCEPTION');
    const current = await state(module, 1);
    assert.equal(current.generation, accepted.generation);
    assert.equal(current.sourceHash, accepted.sourceHash);
    assert.equal(current.sourceUrl, url);
    assert.equal(current.updateState, 'failed');
    assert.equal(current.updateError, 'SCRIPT_EXCEPTION');
    assert.deepEqual(await home(module, current), { value: 'accepted' });
    const facts = JSON.parse(readFileSync(join(f.root, 'settings.json')));
    assert.equal(facts.scripts[0].sourceHash, accepted.sourceHash);
    assert.equal(facts.scripts[0].updateState, 'failed');
  });

test('a retired native network update cannot publish failure into a later local input',
  { timeout: 30000 }, async t => {
    const f = fixture(t);
    let hold = false, entered, release;
    const requested = new Promise(resolve => { entered = resolve; });
    const url = await server(t, (_request, response) => {
      if (!hold) response.end(source('accepted'));
      else { release = () => response.end('module.exports = { start( };'); entered(); }
    });
    f.latest = configuration(network(f.latest.scripts[0], url, 1));
    const module = f.start();
    await state(module, 1);
    hold = true;
    const updating = module.methods['cat.script.update']({ installationId: 'cat' });
    await requested;
    f.latest = configuration(f.local('latest', 2));
    await module.settingsChanged();
    release();
    assert.equal((await updating).ok, false);
    const current = await state(module, 2);
    assert.notEqual(current.updateState, 'failed');
    assert.equal(current.updateError, null);
    assert.deepEqual(await home(module, current), { value: 'latest' });
    assert.equal(f.latest.scripts[0].inputRevision, 2);
  });

test('native Cat source intent replaces immediately and cold start resolves the final input, not an old local file',
  { timeout: 30000 }, async t => {
    const f = fixture(t);
    let downloads = 0;
    const url = await server(t, (_request, response) => { downloads++; response.end(source('network')); });
    const module = f.start();
    const first = await state(module, 1);
    assert.deepEqual(await home(module, first), { value: 'first' });
    const selectedPath = f.latest.scripts[0].localPath;

    f.latest = configuration(network(f.latest.scripts[0], url, 2));
    await module.settingsChanged();
    const second = await state(module, 2);
    assert.equal(downloads, 1);
    assert.ok(existsSync(selectedPath), 'the user-selected local file still exists');
    assert.deepEqual(await home(module, second), { value: 'network' });

    f.latest.scripts[0].name = '已改名';
    await module.settingsChanged();
    const renamed = await state(module, 2);
    assert.equal(renamed.name, '已改名');
    assert.equal(renamed.generation, second.generation);
    assert.equal(downloads, 1, 'a name edit does not fetch or replace the source');

    f.latest = configuration(f.local('third', 3));
    await module.settingsChanged();
    const third = await state(module, 3);
    assert.deepEqual(await home(module, third), { value: 'third' });
    const facts = JSON.parse(readFileSync(join(f.root, 'settings.json')));
    assert.equal(facts.version, 2);
    assert.equal(facts.scripts[0].loadedIdentity, `local:3:${f.latest.scripts[0].localHash}`);
    for (const field of ['name', 'sourceType', 'sourceUrl', 'localPath', 'localHash', 'enabled', 'inputRevision'])
      assert.equal(Object.hasOwn(facts.scripts[0], field), false, field);

    await module.close();
    f.latest = configuration(network(f.latest.scripts[0], url, 4));
    const restarted = f.start();
    const final = await state(restarted, 4);
    assert.equal(downloads, 2);
    assert.deepEqual(await home(restarted, final), { value: 'network' });
  });

test('a native Cat notification acknowledges scheduling, and a later input revokes an in-flight download',
  { timeout: 30000 }, async t => {
    const f = fixture(t);
    let release, entered;
    const downloadEntered = new Promise(resolve => { entered = resolve; });
    const url = await server(t, (_request, response) => { release = () => response.end(source('late')); entered(); });
    const module = f.start();
    await state(module, 1);
    f.latest = configuration(network(f.latest.scripts[0], url, 2));
    await module.settingsChanged();
    await downloadEntered;
    f.latest = configuration(f.local('latest', 3));
    await module.settingsChanged();
    release();
    const latest = await state(module, 3);
    assert.deepEqual(await home(module, latest), { value: 'latest' });
    const facts = JSON.parse(readFileSync(join(f.root, 'settings.json')));
    assert.equal(facts.scripts[0].loadedIdentity, `local:3:${f.latest.scripts[0].localHash}`);
    assert.equal(existsSync(join(f.root, 'sources', hash(source('late')) + '.js')), false);
  });

test('native Cat loading failure does not roll back a saved input or report the former installation as ready',
  { timeout: 30000 }, async t => {
    const f = fixture(t);
    const url = await server(t, (_request, response) => response.end('module.exports = { start( };'));
    const module = f.start();
    await state(module, 1);
    f.latest = configuration(network(f.latest.scripts[0], url, 2));
    await module.settingsChanged();
    const failed = await state(module, 2, 'failed');
    assert.equal(failed.sourceType, 'network');
    assert.equal(failed.sourceUrl, url);
    assert.equal(failed.updateState, 'failed');
    assert.equal(failed.error, 'SCRIPT_EXCEPTION');
    assert.equal(f.latest.scripts[0].inputRevision, 2);
    const invocation = await module.methods['cat.script.invoke']({ installationId: 'cat',
      generation: failed.generation, siteId: 'default', operation: 'home', payload: {} });
    assert.equal(invocation.ok, false);
    f.latest = configuration(f.local('fixed', 3));
    await module.settingsChanged();
    assert.deepEqual(await home(module, await state(module, 3)), { value: 'fixed' });
  });

test('disabled native Cat inputs do not execute, and selected local bytes cannot silently change before loading',
  { timeout: 30000 }, async t => {
    const f = fixture(t);
    const input = f.latest.scripts[0];
    writeFileSync(input.localPath, source('unexpected'));
    input.enabled = false;
    const module = f.start();
    assert.equal((await state(module, 1, 'disabled')).sourceHash, null);
    input.enabled = true;
    await module.settingsChanged();
    assert.equal((await state(module, 1, 'failed')).error, 'STORAGE_CONFLICT');
    assert.equal(f.latest.scripts[0].localHash, input.localHash);
    f.latest = configuration(f.local('approved', 2));
    await module.settingsChanged();
    assert.deepEqual(await home(module, await state(module, 2)), { value: 'approved' });
  });

test('native Cat RPC cannot become a second source configuration writer', async t => {
  const f = fixture(t), module = f.start();
  await state(module, 1);
  for (const operation of ['install', 'import', 'edit', 'enable', 'uninstall']) {
    const reply = await module.methods['cat.script.' + operation]({ installationId: 'cat', name: 'other' });
    assert.equal(reply.ok, false);
    assert.equal(reply.code, 'PERMISSION_DENIED');
  }
  assert.equal(f.latest.scripts[0].name, '猫源');
});

test('native Cat reloads compare member values rather than private-channel JSON member order',
  { timeout: 30000 }, async t => {
    const f = fixture(t);
    let cached = structuredClone(f.latest), reversed = false;
    const module = createCatScriptModule({ dataRoot: f.root, configuration: {
      read: () => structuredClone(cached),
      async reload() {
        reversed = !reversed;
        cached = structuredClone(f.latest);
        cached.scripts = cached.scripts.map(script => Object.fromEntries(
          reversed ? Object.entries(script).reverse() : Object.entries(script)));
        return structuredClone(cached);
      },
    } });
    try {
      const first = await state(module, 1);
      assert.deepEqual(await home(module, first), { value: 'first' });
      await module.settingsChanged();
      const unchanged = await state(module, 1);
      assert.equal(unchanged.generation, first.generation);
      assert.equal(unchanged.sourceHash, first.sourceHash);
      assert.equal(unchanged.error, null);
      const restarted = await call(module, 'restart', { installationId: 'cat' });
      assert.equal(restarted.state, 'ready');
      assert.deepEqual(await home(module, restarted), { value: 'first' });
      assert.deepEqual(f.latest.scripts[0], cached.scripts[0]);
    } finally { await module.close(); }
  });

test('native localHash keeps an uninstalled selected input alive when another installation retires the same source bytes',
  { timeout: 30000 }, async t => {
    const f = fixture(t), module = f.start(), first = await state(module, 1);
    const url = await server(t, (_request, response) => response.end(source('network')));
    const cached = join(f.root, 'sources', first.sourceHash + '.js');
    f.latest = configuration(network(f.latest.scripts[0], url, 2));
    f.latest.scripts.push({ installationId: 'retained', name: '保留的本地输入', sourceType: 'local',
      sourceUrl: null, localPath: null, localHash: first.sourceHash, enabled: false, inputRevision: 1 });
    await module.settingsChanged();
    assert.deepEqual(await home(module, await state(module, 2)), { value: 'network' });
    assert.equal(existsSync(cached), true, 'retiring another installation must respect a saved native localHash');
    await module.close();
    f.latest.scripts[1].enabled = true;
    const restarted = f.start(), deadline = Date.now() + 10000;
    let retained;
    do {
      retained = (await call(restarted, 'status')).instances.find(item => item.installationId === 'retained');
      if (retained?.state === 'ready') break;
      assert.ok(Date.now() < deadline, JSON.stringify(retained));
      await new Promise(resolve => setTimeout(resolve, 10));
    } while (true);
    assert.deepEqual(await call(restarted, 'invoke', { installationId: retained.installationId,
      generation: retained.generation, siteId: 'default', operation: 'home', payload: {} }), { value: 'first' });
  });
