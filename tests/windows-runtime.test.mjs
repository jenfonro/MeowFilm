import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { createServer } from 'node:net';
import { createServer as createHTTPServer } from 'node:http';
import { randomUUID, createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { packageWindowsRuntime } from '../scripts/windows/package-runtime.mjs';
import { assertOwnedPath, temporaryRoot } from '../scripts/windows/working-paths.mjs';
import { verifyPanAndDoubanAssembly, verifyTMDBAssembly } from './tmdb-runtime-assembly-support.mjs';
import { seedSourceLibrary, verifyRecoveredSourceLibrary } from './source-library-assembly-support.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const windows = { skip: process.platform !== 'win32' ? 'Requires real Windows named pipes' : false, timeout: 20000 };
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const privateMarker = 'synthetic-private-diagnostic-must-not-be-logged';
const pipeArgs = (pipeName, session) => ['--meow-pipe', pipeName, '--meow-session', session];
function temporary(t) {
  mkdirSync(temporaryRoot, { recursive: true });
  const area = mkdtempSync(join(temporaryRoot, 'windows-runtime-'));
  t.after(() => rmSync(assertOwnedPath(temporaryRoot, area), { recursive: true, force: true }));
  return area;
}
function bounded(promise, milliseconds = 5000) {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error('Test deadline exceeded')), milliseconds);
  })]).finally(() => clearTimeout(timer));
}

// Node protocol fixture only. CurrentUserOnly ACL/PID checks belong to the native launcher tests.
async function pipeServer(t, dataRoot, { config } = {}) {
  const pipeName = `meowfilm-${randomUUID()}`, sessionId = randomUUID();
  const sockets = new Set(), clients = [], replies = [];
  let accepted;
  const connection = new Promise(resolve => { accepted = resolve; });
  const server = createServer(socket => {
    sockets.add(socket);
    socket.on('error', () => {});
    socket.once('close', () => sockets.delete(socket));
    let bytes = Buffer.alloc(0);
    socket.on('data', chunk => {
      bytes = Buffer.concat([bytes, chunk]);
      const newline = bytes.indexOf(10);
      if (newline === -1) return;
      assert.equal(newline, bytes.length - 1, 'only one ready line is allowed');
      assert.equal(replies.length, 0, 'the private pipe has no business messages');
      const ready = JSON.parse(bytes.subarray(0, -1).toString('utf8'));
      assert.deepEqual(Object.keys(ready).sort(), ['baseUrl', 'protocol']);
      assert.equal(ready.protocol, 'runtime.http.v1');
      assert.match(ready.baseUrl, /^http:\/\/127\.0\.0\.1:[1-9][0-9]*$/);
      replies.push(ready);
      const client = createRuntimeClient({ baseUrl: ready.baseUrl });
      clients.push(client);
      accepted({ client, socket, ready, sessionId });
    });
    const value = { sessionId, dataRoot };
    socket.write(config ? config(value) : JSON.stringify(value) + '\n');
  });
  t.after(async () => {
    await Promise.all(clients.map(client => client.close()));
    for (const socket of sockets) socket.destroy();
    if (server.listening) await bounded(new Promise(resolve => server.close(resolve)));
  });
  server.listen({ path: `\\\\.\\pipe\\${pipeName}` });
  await bounded(once(server, 'listening'));
  return { argv: pipeArgs(pipeName, sessionId), connection, replies, sockets };
}

function spawnRuntime(t, entry, argv, payload) {
  const env = { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' };
  if (payload) {
    const hook = `
      import { registerHooks } from 'node:module';
      import { fileURLToPath } from 'node:url';
      import { isAbsolute, relative, sep } from 'node:path';
      const root = ${JSON.stringify(payload)};
      registerHooks({ resolve(specifier, context, next) {
        const value = next(specifier, context);
        if (value.url.startsWith('file:')) {
          const path = relative(root, fileURLToPath(value.url));
          if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path)) throw new Error('PAYLOAD_DEPENDENCY_ESCAPED');
        }
        return value;
      } });
    `;
    env.NODE_OPTIONS = `--import=data:text/javascript,${encodeURIComponent(hook)}`;
  }
  const child = spawn(process.execPath, [entry, ...argv], { cwd: root, env,
    stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  let stdout = '', stderr = '';
  child.stdout.setEncoding('utf8').on('data', text => { stdout += text; });
  child.stderr.setEncoding('utf8').on('data', text => { stderr += text; });
  const exit = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => resolve({ code, signal, stdout, stderr }));
  });
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) child.kill();
    await bounded(exit);
  });
  return { child, exit };
}

async function assertRealBusiness(client) {
  const reply = await client.describe();
  assert.equal(reply.ok, true);
  assert.equal(reply.contract, 'runtime.http.v1');
  const description = reply.value;
  assert.equal(description.protocol, 'runtime.http.v1');
  assert.equal(description.product, 'aggregate');
  assert.deepEqual(description.modules.map(({ id, version, contract }) => ({ id, version, contract })), [
    { id: 'music', version: '0.8.0', contract: 'music.script.v3' },
    { id: 'cat', version: '0.2.0', contract: 'cat.script.v2' },
    { id: 'source', version: '0.3.0', contract: 'source.runtime.v2' },
    { id: 'pan', version: '0.1.0', contract: 'pan.runtime.v1' },
    { id: 'tmdb', version: '0.1.0', contract: 'tmdb.data.v1' },
    { id: 'douban', version: '0.1.0', contract: 'douban.data.v1' },
    { id: 'film', version: '0.1.0', contract: 'film.playback.v1' },
    { id: 'netease', version: '0.1.0', contract: 'netease.token.v1' },
  ]);
  const operations = {
    music: ['sources.list', 'sources.detail', 'sources.add', 'sources.download', 'sources.edit',
      'sources.update', 'sources.rollback', 'sources.enable', 'sources.disable', 'sources.remove',
      'sources.check', 'resolve', 'invoke', 'close'],
    cat: ['start', 'status', 'refresh', 'install', 'import', 'edit', 'update', 'restart',
      'enable', 'uninstall', 'invoke', 'management.describe', 'management.sync'],
    source: ['load', 'unload', 'invoke', 'status', 'close',
      'library.list', 'library.get', 'library.import', 'library.save', 'library.update', 'library.remove',
      'library.export', 'library.invoke', 'library.image', 'library.releaseImage', 'web.pending', 'web.respond', 'rules.process'],
    pan: ['providers', 'settings.get', 'settings.set', 'settings.clear', 'login.start',
      'login.poll', 'login.cancel', 'list', 'prepare', 'play', 'refresh'],
    tmdb: ['settings.get', 'settings.update', 'request'],
    douban: ['settings.get', 'settings.update', 'request', 'search'],
    netease: ['get'],
    film: ['session.open', 'session.command', 'session.snapshot', 'session.release', 'session.attempt',
      'session.media.release', 'settings.get', 'settings.set', 'library.get', 'library.remove', 'manual.update',
      'data.catalog', 'data.search', 'data.detail', 'data.metadata', 'data.metadata.entry', 'data.cache.entry', 'data.invalidate',
      'rules.get', 'rules.set', 'rules.group.set', 'rules.group.defaults', 'rules.group.preview',
      'rules.restore', 'rules.preview', 'rules.normalize', 'rules.clear',
      'search.open', 'search.snapshot', 'search.command', 'search.release', 'search.handoff'],
  };
  for (const module of description.modules) {
    const prefix = { music: 'music.script.', cat: 'cat.script.', source: 'source.runtime.',
      pan: 'pan.', tmdb: 'tmdb.data.', douban: 'douban.data.', film: 'film.', netease: 'netease.token.' }[module.id];
    assert.deepEqual(module.methods, operations[module.id].map(name => prefix + name).sort());
  }
  for (const method of ['pan.settings.get', 'tmdb.data.settings.get', 'douban.data.settings.get',
    'film.settings.get', 'film.library.get']) assert.equal((await client.call(method)).ok, true, method);
  assert.deepEqual(await client.call('music.script.sources.list'), {
    ok: true, contract: 'music.script.v3', value: { revision: 0, items: [] },
  });
  assert.deepEqual(await client.call('source.runtime.status'), {
    ok: true, contract: 'source.runtime.v2', value: { instances: [] },
  });
  assert.deepEqual(await client.call('source.runtime.rules.process', {
    request: { operation: 'identity', book: { name: '《测试书》', author: '作者：甲' } },
  }), { ok: true, contract: 'source.runtime.v2',
    value: { name: '测试书', author: '甲', key: JSON.stringify(['book', '测试书', '甲']) } });
  for (const method of ['music.library.list', 'reader.content.chapter', 'eval'])
    await assert.rejects(client.call(method), { code: 'METHOD_NOT_FOUND' });
  assert.deepEqual(await client.call('film.playback.recognize'), {
    ok: false, contract: 'runtime.http.v1', code: 'METHOD_NOT_FOUND',
  });
  return description;
}

async function verifyScriptExecution(t, client, scriptDataRoot) {
  let requests = 0;
  const upstream = createHTTPServer((request, response) => {
    requests++;
    assert.equal(request.url, '/fixture');
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end('{"fixture":"local-http"}');
  });
  t.after(async () => {
    upstream.closeAllConnections();
    if (upstream.listening) await bounded(new Promise((resolve, reject) =>
      upstream.close(error => error ? reject(error) : resolve())));
  });
  await bounded(new Promise((resolve, reject) => {
    upstream.once('error', reject);
    upstream.listen(0, '127.0.0.1', resolve);
  }));
  const url = `http://127.0.0.1:${upstream.address().port}/fixture`;
  const siteId = 'fixture-site';
  // Self-authored contract fixture, not acceptance evidence for external scripts.
  const source = Buffer.from(`
    // @name Packaged LX contract fixture
    const startupStorage = typeof meow.storage;
    const background = new Promise(resolve => setTimeout(() => {
      resolve(typeof meow.storage);
    }, 1));
    let previousContext;
    lx.on(lx.EVENT_NAMES.request, async ({source, action}, context) => {
      const backgroundStorage = await background, globalStorage = typeof meow.storage;
      let retiredCode = 'NO_PREVIOUS';
      if (previousContext) {
        try { await previousContext.storage.get('retired'); retiredCode = 'UNEXPECTED'; }
        catch (error) { retiredCode = error.code; }
      }
      previousContext = context;
      await Promise.resolve();
      await context.storage.set('probe-' + action, 'module-scoped-' + action);
      const stored = await context.storage.get('probe-' + action);
      const network = await new Promise((resolve, reject) => lx.request(${JSON.stringify(url)}, {},
        (error, response, body) => error ? reject(error) : resolve(body)));
      return {source, action, startupStorage, backgroundStorage, globalStorage, retiredCode, stored, network};
    });
    lx.send(lx.EVENT_NAMES.inited, {sources:{
      '${siteId}':{name:'Fixture',type:'music',actions:['fixtureAction','musicUrl'],
        qualitys:[{type:'320k',label:'Fixture 320'}]}
    }});
  `);
  const okay = reply => {
    assert.equal(reply.ok, true, JSON.stringify(reply));
    assert.equal(reply.contract, 'music.script.v3');
    return reply.value;
  };
  const installationId = randomUUID();
  const uploaded = okay(await client.upload('music', installationId, 1, source, { basename: 'packaged-fixture.js' }));
  assert.equal(uploaded.sourceBytes, source.length);
  assert.equal(uploaded.contentHash, sha256(source));
  const imported = okay(await client.call('music.script.sources.add', { installationId, sourceRevision: 1 })).item;
  assert.equal(imported.name, 'Packaged LX contract fixture');
  const instance = okay(await client.call('music.script.sources.enable',
    { installationId: imported.installationId })).item;
  assert.equal(instance.state, 'ready');
  for (const operation of ['fixtureAction', 'musicUrl']) {
    const result = okay(await client.call('music.script.invoke', {
      installationId: instance.installationId, siteId, operation, payload: { type: '320k', musicInfo: {} },
    }));
    assert.deepEqual(result, {
      source: siteId, action: operation,
      startupStorage: 'undefined', backgroundStorage: 'undefined', globalStorage: 'undefined',
      retiredCode: operation === 'fixtureAction' ? 'NO_PREVIOUS' : 'PERMISSION_DENIED',
      stored: `module-scoped-${operation}`, network: { fixture: 'local-http' },
    });
  }
  assert.deepEqual(JSON.parse(readFileSync(join(scriptDataRoot, 'music', 'data', `${instance.installationId}.json`),
    'utf8')).sites[siteId].map(entry => entry.key), ['probe-fixtureAction', 'probe-musicUrl']);
  assert.equal(requests, 2, 'the controlled local HTTP service is reachable without an origin/private-network grant');
  assert.equal(okay(await client.call('music.script.sources.disable', {
    installationId: instance.installationId })).item.state, 'disabled');
  assert.equal(okay(await client.call('music.script.sources.list', {})).items[0].state, 'disabled');
}

async function verifySourceExecution(t, client) {
  const upstream = createHTTPServer((request, response) => {
    assert.match(request.url, /^\/search\?key=/);
    response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
    response.end(`
      <div class="book">
        <span class="name">Portable Book</span>
        <a href="/book/1">open</a>
      </div>
    `);
  });
  t.after(async () => {
    upstream.closeAllConnections();
    if (upstream.listening) await bounded(new Promise((resolve, reject) =>
      upstream.close(error => error ? reject(error) : resolve())));
  });
  await bounded(new Promise((resolve, reject) => {
    upstream.once('error', reject);
    upstream.listen(0, '127.0.0.1', resolve);
  }));
  const origin = `http://127.0.0.1:${upstream.address().port}`;
  const source = Buffer.from(`${JSON.stringify({
    bookSourceName: 'Packaged Reader fixture',
    bookSourceUrl: origin,
    searchUrl: `${origin}/search?key={{key}}`,
    ruleSearch: {
      bookList: '.book',
      name: '.name@text',
      bookUrl: 'a@href',
    },
  })}\n`);
  const okay = reply => {
    assert.equal(reply.ok, true, JSON.stringify(reply));
    assert.equal(reply.contract, 'source.runtime.v2');
    return reply.value;
  };
  const uploaded = okay(await client.upload('source', 'packaged-reader', 1, source, { basename: 'packaged-reader.json' }));
  assert.equal(uploaded.sourceBytes, source.length);
  const instance = okay(await client.call('source.runtime.load', {
    sourceRevision: 1, installationId: 'packaged-reader', revision: 1, expectedRevision: 0,
    protocol: 'legado', version: '1.0.0', metadata: {},
  })).instance;
  assert.deepEqual(instance.capabilities.source, {
    protocol: 'legado',
    importedProtocol: 'legado',
    actions: ['search', 'loginStatus', 'image'],
  });
  const result = okay(await client.call('source.runtime.invoke', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
    action: 'search',
    payload: { keyword: 'fixture', page: 1 },
  }));
  assert.equal(result.books[0].name, 'Portable Book');
  assert.equal(result.books[0].bookUrl, `${origin}/book/1`);
  assert.equal(okay(await client.call('source.runtime.unload', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
  })).cleanupComplete, true);
  assert.deepEqual(okay(await client.call('source.runtime.status', {})).instances, []);
}

async function verifyCatExecution(client) {
  const installationId = 'packaged-cat';
  const source = Buffer.from(`
    module.exports = { async init() {}, async home(filter) { return { text: filter.text }; } };
  `);
  const uploaded = await client.upload('cat', installationId, 1, source);
  assert.equal(uploaded.ok, true, JSON.stringify(uploaded));
  assert.equal(uploaded.value.sourceBytes, source.length);
  const installed = await client.call('cat.script.install', { installationId, sourceRevision: 1, enabled: true });
  assert.equal(installed.ok, true, JSON.stringify(installed));
  const text = '完整数据👨‍👩‍👦'.repeat(16000);
  const result = await client.call('cat.script.invoke', {
    installationId, generation: installed.value.generation, siteId: 'default', operation: 'home',
    payload: { filter: { text } },
  });
  assert.deepEqual(result, { ok: true, contract: 'cat.script.v2', value: { text } });
}

async function runProduct(t, entry, payload, dataRoot, manifest) {
  const server = await pipeServer(t, dataRoot);
  const child = spawnRuntime(t, entry, server.argv, payload);
  const { client, socket, ready } = await bounded(server.connection);
  const description = await assertRealBusiness(client);
  if (manifest) assert.deepEqual(description.modules.map(module => module.id), manifest.modules);
  const tmdbSettings = await verifyTMDBAssembly(t, { client, baseUrl: ready.baseUrl });
  await verifyPanAndDoubanAssembly(client);
  await verifyScriptExecution(t, client, dataRoot);
  await verifySourceExecution(t, client);
  const sourceLibrary = await seedSourceLibrary(client);
  await verifyCatExecution(client);
  assert.equal(server.replies.length, 1);
  assert.deepEqual(await client.shutdown(), {
    ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true },
  });
  socket.end();
  assert.deepEqual(await bounded(child.exit), { code: 0, signal: null, stdout: '', stderr: '' });
  // A new process/session uses the same native-owned data root; installation
  // facts and enabled Cat scripts must survive without any native reverse call.
  const recovered = await pipeServer(t, dataRoot);
  const second = spawnRuntime(t, entry, recovered.argv, payload);
  const connected = await bounded(recovered.connection);
  await verifyRecoveredSourceLibrary(connected.client, sourceLibrary);
  const restoredSettings = await connected.client.call('tmdb.data.settings.get');
  assert.equal(typeof restoredSettings.value?.revision, 'string');
  const { revision, ...restoredConfiguration } = restoredSettings.value;
  const { revision: previousRevision, ...previousConfiguration } = tmdbSettings;
  assert.equal(typeof previousRevision, 'string');
  assert.notEqual(previousRevision, '');
  assert.notEqual(revision, '');
  assert.equal(revision, previousRevision);
  assert.deepEqual({ ...restoredSettings, value: restoredConfiguration }, {
    ok: true, contract: 'tmdb.data.v1', value: previousConfiguration,
  });
  const music = await connected.client.call('music.script.sources.list');
  assert.equal(music.ok, true, JSON.stringify(music));
  assert.equal(music.value.items.length, 1);
  assert.equal(music.value.items[0].name, 'Packaged LX contract fixture');
  assert.equal(music.value.items[0].state, 'disabled');
  // status starts restoration without blocking on every enabled instance.
  const startup = AbortSignal.timeout(5000);
  let restored;
  do {
    const cat = await connected.client.call('cat.script.status', {}, { signal: startup });
    assert.equal(cat.ok, true, JSON.stringify(cat));
    restored = cat.value.instances.find(item => item.installationId === 'packaged-cat');
    assert.ok(restored, 'the persisted installation is restored');
    if (restored.state !== 'starting') break;
    await delay(20, undefined, { signal: startup });
  } while (true);
  assert.equal(restored.state, 'ready');
  assert.deepEqual(await connected.client.call('cat.script.invoke', {
    installationId: restored.installationId, generation: restored.generation,
    siteId: 'default', operation: 'home', payload: { filter: { text: 'restored' } },
  }), { ok: true, contract: 'cat.script.v2', value: { text: 'restored' } });
  assert.deepEqual(await connected.client.shutdown(), {
    ok: true, contract: 'runtime.http.v1', value: { closed: true, cleanupComplete: true },
  });
  connected.socket.end();
  assert.deepEqual(await bounded(second.exit), { code: 0, signal: null, stdout: '', stderr: '' });
}

for (const packaged of [false, true]) {
  // Disk inventory/copy of the native and jsdom payload has a separate fixture
  // budget; it must not consume the unchanged real-runtime protocol deadline.
  const options = { ...windows, timeout: packaged ? 120000 : windows.timeout };
  test(`${packaged ? 'packaged' : 'source'} Windows entry serves eight App modules over HTTP and drains on close`, options, async t => {
    const area = temporary(t);
    let entry = join(root, 'apps/windows/runtime/entries/aggregate.mjs'), payload, manifest;
    if (packaged) {
      payload = join(area, 'payload');
      manifest = await packageWindowsRuntime({ root, product: 'aggregate', output: payload });
      assert.deepEqual(manifest.modules, ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease']);
      assert.equal(manifest.transport, 'windows-http-v1');
      assert.equal(manifest.trustedNative[0].platform, 'win32');
      assert.equal(manifest.trustedNative[0].arch, process.arch);
      entry = join(payload, manifest.entry);
    }
    await bounded(runProduct(t, entry, payload, join(area, 'data'), manifest), windows.timeout);
  });
}

for (const termination of ['disconnect', 'extra-byte']) {
  test(`Windows lifetime pipe ${termination} closes the real runtime and exits the child`, windows, async t => {
    const server = await pipeServer(t, join(temporary(t), 'data'));
    const child = spawnRuntime(t, join(root, 'apps/windows/runtime/entries/aggregate.mjs'), server.argv);
    const { client, socket } = await bounded(server.connection);
    await assertRealBusiness(client);
    if (termination === 'disconnect') socket.destroy();
    else socket.write(' ');
    assert.deepEqual(await bounded(child.exit), { code: 0, signal: null, stdout: '', stderr: '' });
    await assert.rejects(client.describe(), { code: 'CLOSED' });
  });
}

test('Windows bootstrap rejects mismatched private config and exits without a ready line', windows, async t => {
  const server = await pipeServer(t, join(temporary(t), 'data'), {
    config: value => JSON.stringify({ ...value, sessionId: randomUUID() }) + '\n',
  });
  const child = spawnRuntime(t, join(root, 'apps/windows/runtime/entries/aggregate.mjs'), server.argv);
  assert.deepEqual(await bounded(child.exit), { code: 1, signal: null, stdout: '', stderr: 'WINDOWS_RUNTIME_START_FAILED\n' });
  assert.deepEqual(server.replies, []);
});

test('CLI launch failure emits only a fixed code, never argv or underlying diagnostics', async t => {
  const child = spawnRuntime(t, join(root, 'apps/windows/runtime/entries/aggregate.mjs'),
    ['--meow-pipe', privateMarker, '--meow-session', privateMarker]);
  assert.deepEqual(await bounded(child.exit), { code: 1, signal: null, stdout: '', stderr: 'WINDOWS_RUNTIME_START_FAILED\n' });
});
