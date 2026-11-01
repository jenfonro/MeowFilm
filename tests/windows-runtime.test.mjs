import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter, once } from 'node:events';
import { createServer } from 'node:net';
import { createServer as createHTTPServer } from 'node:http';
import { randomUUID, createHash } from 'node:crypto';
import {
  existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync,
  renameSync, symlinkSync, writeFileSync,
} from 'node:fs';
import { spawn, spawnSync } from 'node:child_process';
import { dirname, isAbsolute, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { attachConnectedStream } from '@meowfilm/runtime-bridge/descriptor';
import { encodeFrame } from '@meowfilm/runtime-bridge';
import {
  connectWindowsNamedPipe, parseWindowsLaunchArguments, startWindowsAppRuntime,
} from '../apps/windows/runtime/bootstrap.mjs';
import { packageWindowsRuntime } from '../scripts/windows/package-runtime.mjs';
import { packageRuntime } from '../scripts/package-runtime.mjs';
import { temporaryRoot } from '../scripts/windows/working-paths.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const temporary = join(temporaryRoot, 'windows-runtime-tests');
mkdirSync(temporary, { recursive: true });
const windows = { skip: process.platform !== 'win32' ? 'Requires real Windows named pipes' : false, timeout: 20000 };
const products = { aggregate: ['music', 'source'] };
const runtimeDependencies = [
  'node_modules/@acemir/cssom',
  'node_modules/@jitl/quickjs-ffi-types',
  'node_modules/@jitl/quickjs-wasmfile-release-sync',
  'node_modules/@meowfilm/runtime-bridge',
  'node_modules/@meowfilm/script-host',
  'node_modules/@meowfilm/source-runtime',
  'node_modules/acorn',
  'node_modules/boolbase',
  'node_modules/css-select',
  'node_modules/css-what',
  'node_modules/dom-serializer',
  'node_modules/domelementtype',
  'node_modules/domhandler',
  'node_modules/domutils',
  'node_modules/entities',
  'node_modules/fontoxpath',
  'node_modules/html-escaper',
  'node_modules/htmlparser2',
  'node_modules/htmlparser2/node_modules/dom-serializer',
  'node_modules/htmlparser2/node_modules/dom-serializer/node_modules/entities',
  'node_modules/htmlparser2/node_modules/domelementtype',
  'node_modules/htmlparser2/node_modules/domhandler',
  'node_modules/htmlparser2/node_modules/domutils',
  'node_modules/htmlparser2/node_modules/entities',
  'node_modules/iconv-lite',
  'node_modules/linkedom',
  'node_modules/nth-check',
  'node_modules/prsc',
  'node_modules/quickjs-emscripten-core',
  'node_modules/safer-buffer',
  'node_modules/uhyphen',
  'node_modules/whynot',
  'node_modules/xspattern',
].sort();
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const readJSON = path => JSON.parse(readFileSync(path, 'utf8'));
const privateMarker = 'synthetic-private-diagnostic-must-not-be-logged';
const pipeArgs = (pipeName, session) => ['--meow-pipe', pipeName, '--meow-session', session];

function within(base, path) {
  const delta = relative(base, path);
  return delta !== '..' && !delta.startsWith(`..${sep}`) && !isAbsolute(delta);
}
function write(base, path, value) {
  const target = join(base, path);
  assert.ok(within(temporary, target), 'test writes stay in the owned Windows temporary directory');
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, typeof value === 'string' || Buffer.isBuffer(value) ?
    value : `${JSON.stringify(value, null, 2)}\n`);
}
function bounded(promise, milliseconds = 5000) {
  let timer;
  return Promise.race([
    promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Test deadline exceeded')), milliseconds); }),
  ]).finally(() => clearTimeout(timer));
}
function files(directory, prefix = '') {
  return readdirSync(join(directory, prefix), { withFileTypes: true }).flatMap(item => {
    assert.equal(item.isSymbolicLink(), false);
    const path = prefix ? `${prefix}/${item.name}` : item.name;
    return item.isDirectory() ? files(directory, path) : [path];
  }).sort();
}
function verifyManifest(output, manifest) {
  assert.deepEqual(readJSON(join(output, 'runtime-manifest.json')), manifest);
  assert.equal(manifest.status, 'complete');
  assert.equal(manifest.transport, 'windows-named-pipe-v1');
  assert.equal(manifest.platform, 'win32');
  assert.equal(manifest.entry, `app/windows/entries/${manifest.product}.mjs`);
  assert.deepEqual(manifest.modules, products[manifest.product]);
  const paths = manifest.files.map(item => item.path);
  assert.deepEqual(paths, [...paths].sort());
  assert.equal(new Set(paths.map(path => path.normalize('NFC').toLowerCase())).size, paths.length);
  assert.deepEqual(files(output), [...paths, 'runtime-manifest.json'].sort());
  for (const file of manifest.files) {
    const bytes = readFileSync(join(output, file.path));
    assert.equal(file.bytes, bytes.length, file.path);
    assert.equal(file.sha256, sha256(bytes), file.path);
  }
  assert.deepEqual(paths.filter(path => path.startsWith('app/windows/')), [
    'app/windows/bootstrap.mjs', manifest.entry,
  ]);
  assert.deepEqual(manifest.fileHashExcludes, ['runtime-manifest.json']);
  assert.ok(!paths.some(path => /\.(?:exe|dll|node)$/i.test(path)));
  const engine = readJSON(join(root, 'scripts/quickjs-runtime.json')).packages
    .find(pkg => pkg.name === '@jitl/quickjs-wasmfile-release-sync');
  assert.deepEqual(manifest.trustedWasm, [{
    package: engine.name, version: engine.version,
    ...engine.wasm, path: `node_modules/${engine.name}/${engine.wasm.path}`,
    licensePath: `node_modules/${engine.name}/LICENSE`, licenseSha256: engine.licenseSha256,
  }]);
  assert.deepEqual(paths.filter(path => /\.wasm$/i.test(path)), manifest.trustedWasm.map(file => file.path));
  assert.equal(manifest.sharedEntry, undefined);
  assert.equal(manifest.assemblyProfile, 'windows-native');
  for (const module of ['music-script.mjs', 'source-runtime.mjs']) {
    assert.deepEqual(readFileSync(join(output, `app/modules/${module}`)),
      readFileSync(join(root, `apps/runtime/modules/${module}`)));
  }
  for (const workspace of ['runtime-bridge', 'script-host', 'source-runtime']) {
    assert.equal(existsSync(join(output, `node_modules/@meowfilm/${workspace}`)), true);
  }
  assert.deepEqual(manifest.dependencies.map(item => item.path).sort(), runtimeDependencies);
  for (const dependency of manifest.dependencies) {
    const sourceManifest = readJSON(join(root, dependency.lockPath, 'package.json'));
    assert.deepEqual(readJSON(join(output, dependency.path, 'package.json')), sourceManifest);
    // Check root legal notices from the actual dependency, not only filenames.
    for (const name of readdirSync(join(root, dependency.lockPath))) {
      if (/^(?:\.?licen[cs]e[sd]?|notice|copying|copyright|authors|third[-_]?party|sources)(?:[._-]|$)/i.test(name) &&
          lstatSync(join(root, dependency.lockPath, name)).isFile()) {
        assert.deepEqual(readFileSync(join(output, dependency.path, name)),
          readFileSync(join(root, dependency.lockPath, name)));
      }
    }
  }
}

test('Windows launch accepts only the exact ordered pipe-name/session UUID protocol', () => {
  const session = randomUUID(), pipeName = `meowfilm-${randomUUID()}`, valid = pipeArgs(pipeName, session);
  assert.deepEqual(parseWindowsLaunchArguments(valid), { pipeName, session });
  for (const argv of [
    null, {}, 'not-an-array', [], valid.slice(0, 3), [...valid, '--extra'], [...valid, ...valid],
    ['--meow-session', session, '--meow-pipe', pipeName],
    ['--meow-fd', '3', '--meow-session', session],
    ['--meow-pipe', pipeName, '--session', session],
    [`--meow-pipe=${pipeName}`, '--meow-session', session],
    ...['', 'meowfilm', pipeName.toUpperCase(), `${pipeName}\n`, `${pipeName}\0`, `${pipeName} `,
      ` ${pipeName}`, `\\\\.\\pipe\\${pipeName}`, `\\\\server\\pipe\\${pipeName}`,
      `//./pipe/${pipeName}`, `../${pipeName}`, `C:\\${pipeName}`, '127.0.0.1:8080',
      'tcp://localhost:8080', `meowfilm-${randomUUID().replaceAll('-', '')}`,
      pipeName.replace('meowfilm-', 'other-'), null, 10, { toString: () => pipeName },
    ].map(pipe => pipeArgs(pipe, session)),
    ...['', 'session-1', session.toUpperCase(), `${session}\r\n`, ` ${session}`, `{${session}}`,
      session.replaceAll('-', ''), 'auth-token', null, 10, { toString: () => session },
    ].map(value => pipeArgs(pipeName, value)),
  ]) {
    assert.throws(() => parseWindowsLaunchArguments(argv), { code: 'INVALID_MESSAGE', message: 'INVALID_MESSAGE' });
  }
});

test('invalid transport paths and timeout settings cannot invoke a socket factory', async () => {
  const forbidden = () => assert.fail('must not create a socket');
  for (const path of ['tcp://127.0.0.1:1', '\\\\.\\pipe\\arbitrary', '../arbitrary', 'anything', null]) {
    await assert.rejects(connectWindowsNamedPipe(path, { socketFactory: forbidden }), { code: 'INVALID_MESSAGE' });
  }
  for (const timeout of [0, -1, 30001, NaN, Infinity, 1.5, '5000']) {
    await assert.rejects(connectWindowsNamedPipe(`meowfilm-${randomUUID()}`, { timeout, socketFactory: forbidden }),
      { code: 'INVALID_MESSAGE' });
  }
  if (process.platform !== 'win32') {
    await assert.rejects(connectWindowsNamedPipe(`meowfilm-${randomUUID()}`, { socketFactory: forbidden }),
      { code: 'INVALID_MESSAGE' });
  }
});

class ControlledSocket extends EventEmitter {
  destroyed = false;
  destructions = 0;
  connect(options) { this.options = options; return this; }
  destroy() {
    if (this.destroyed) return this;
    this.destroyed = true;
    this.destructions++;
    this.emit('close');
    return this;
  }
}

test('connection timeout is bounded and removes transient listeners without logging errors', windows, async () => {
  const socket = new ControlledSocket(), name = `meowfilm-${randomUUID()}`;
  const beginning = performance.now();
  await assert.rejects(connectWindowsNamedPipe(name, { timeout: 30, socketFactory: () => socket }),
    { code: 'TIMEOUT', message: 'TIMEOUT' });
  assert.ok(performance.now() - beginning < 2000);
  assert.deepEqual(socket.options, { path: `\\\\.\\pipe\\${name}` });
  assert.equal(socket.destructions, 1);
  assert.deepEqual(socket.eventNames(), []);
  socket.emit('connect');
  assert.equal(socket.destructions, 1);
});

test('connect errors, synchronous throws, early close and handoff errors reclaim the socket', windows, async () => {
  for (const kind of ['error', 'throw', 'close', 'connected-error']) {
    const socket = new ControlledSocket();
    socket.connect = () => {
      if (kind === 'throw') throw new Error(privateMarker);
      queueMicrotask(() => {
        if (kind === 'error') socket.emit('error', new Error(privateMarker));
        else if (kind === 'close') socket.destroy();
        else { socket.emit('connect'); socket.emit('error', new Error(privateMarker)); }
      });
      return socket;
    };
    const result = connectWindowsNamedPipe(`meowfilm-${randomUUID()}`, { socketFactory: () => socket });
    if (kind === 'connected-error') assert.equal(await result, socket);
    else await assert.rejects(result, { code: 'CLOSED', message: 'CLOSED' });
    assert.equal(socket.destructions, 1);
    assert.deepEqual(socket.eventNames(), []);
  }
  await assert.rejects(connectWindowsNamedPipe(`meowfilm-${randomUUID()}`, {
    socketFactory() { throw new Error(privateMarker); },
  }), { code: 'CLOSED', message: 'CLOSED' });
  await assert.rejects(connectWindowsNamedPipe(`meowfilm-${randomUUID()}`, {
    socketFactory: () => ({}),
  }), { code: 'INVALID_MESSAGE' });
});

// This is a Node pipe server protocol fixture, NOT the native C# ACL/PID gate.
async function pipeServer(t, { methods = {}, session = randomUUID() } = {}) {
  const pipeName = `meowfilm-${randomUUID()}`, sockets = new Set(), peers = [];
  let accepted;
  const connection = new Promise(resolve => { accepted = resolve; });
  const server = createServer(socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
    const peer = attachConnectedStream(socket, { session, methods, timeout: 3000 });
    peers.push(peer);
    accepted({ peer, socket });
  });
  t.after(async () => {
    for (const peer of peers) peer.close();
    for (const socket of sockets) socket.destroy();
    if (server.listening) await bounded(new Promise((resolve, reject) =>
      server.close(error => error ? reject(error) : resolve())));
  });
  const listening = once(server, 'listening');
  server.listen({ path: `\\\\.\\pipe\\${pipeName}` });
  await bounded(listening);
  return { session, pipeName, argv: pipeArgs(pipeName, session), connection, peers };
}

test('real named pipe carries duplex RPC and abnormal disconnect disposes modules once', windows, async t => {
  const server = await pipeServer(t, { methods: { 'platform.fixture.echo': params => params } });
  let closes = 0, aborted = 0, disposed;
  const closed = new Promise(resolve => { disposed = resolve; });
  const runtime = await startWindowsAppRuntime({
    product: 'fixture', argv: server.argv,
    assemble: ({ call }) => [{
      id: 'fixture', version: '1.0.0',
      methods: {
        'fixture.echo': params => params,
        'fixture.roundtrip': (params, { signal }) => call('platform.fixture.echo', params, { signal }),
        'fixture.wait': (_, { signal }) => new Promise(resolve => {
          signal.addEventListener('abort', () => { aborted++; resolve(null); }, { once: true });
        }),
      },
      close() { closes++; disposed(); },
    }],
  });
  t.after(() => runtime.close());
  const { peer, socket } = await bounded(server.connection);
  assert.deepEqual(await peer.call('fixture.echo', { text: '管道👨‍👩‍👦' }), { text: '管道👨‍👩‍👦' });
  assert.equal(await peer.call('fixture.roundtrip', 7), 7);
  const pending = peer.call('fixture.wait');
  const rejected = assert.rejects(pending, { code: 'CLOSED' });
  // Ordered request/response guarantees the preceding handler was dispatched.
  await peer.call('runtime.describe');
  socket.destroy();
  await rejected;
  await bounded(closed);
  await runtime.close();
  await runtime.close();
  assert.equal(closes, 1);
  assert.equal(aborted, 1);
  assert.equal(runtime.peer.state.closed, true);
  assert.equal(server.peers.length, 1);
});

for (const termination of ['eof', 'malformed-frame', 'stale-session']) {
  test(`real named pipe ${termination} closes and disposes exactly once`, windows, async t => {
    const server = await pipeServer(t);
    let closes = 0, disposed;
    const closed = new Promise(resolve => { disposed = resolve; });
    const runtime = await startWindowsAppRuntime({
      product: 'fixture', argv: server.argv,
      assemble: () => [{ id: 'fixture', version: '1.0.0', methods: {}, close() { closes++; disposed(); } }],
    });
    t.after(() => runtime.close());
    const { socket, peer } = await bounded(server.connection);
    await peer.call('runtime.describe');
    if (termination === 'eof') socket.end();
    else if (termination === 'malformed-frame') socket.write(Buffer.alloc(4));
    else socket.write(encodeFrame({
      version: 1, session: randomUUID(), id: 2, kind: 'request', method: 'runtime.describe', params: null,
    }));
    await bounded(closed);
    await runtime.close();
    assert.equal(runtime.peer.state.closed, true);
    assert.equal(closes, 1);
  });
}

test('missing pipe and invalid CLI never assemble modules', windows, async () => {
  const assemble = () => assert.fail('a failed connection cannot start a domain');
  await assert.rejects(startWindowsAppRuntime({
    product: 'fixture', assemble, argv: ['--meow-pipe', privateMarker],
  }), { code: 'INVALID_MESSAGE' });
  await assert.rejects(bounded(startWindowsAppRuntime({
    product: 'fixture', assemble, argv: pipeArgs(`meowfilm-${randomUUID()}`, randomUUID()), connectTimeout: 200,
  })), { code: 'CLOSED', message: 'CLOSED' });
});

test('failed assembly releases its connected Windows pipe and disposes the module once', windows, async t => {
  const server = await pipeServer(t);
  let closes = 0;
  await assert.rejects(startWindowsAppRuntime({
    product: 'fixture', argv: server.argv,
    assemble: () => [{ id: 'fixture', version: 'invalid', methods: {}, close() { closes++; } }],
  }), { code: 'INVALID_MESSAGE' });
  const { peer, socket } = await bounded(server.connection);
  if (!socket.destroyed) await bounded(once(socket, 'close'));
  assert.equal(peer.state.closed, true);
  assert.equal(closes, 1);
});

function spawnRuntime(t, entry, argv, payload) {
  const env = { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' };
  if (payload) {
    // Test-only resolver guard: neither ESM nor CJS may be rescued by the
    // development worktree's ancestor node_modules. No guard ships in payload.
    const hook = `
      import { registerHooks } from 'node:module';
      import { fileURLToPath } from 'node:url';
      import { isAbsolute, relative, sep } from 'node:path';
      const root = ${JSON.stringify(payload)};
      registerHooks({ resolve(specifier, context, next) {
        const value = next(specifier, context);
        if (value.url.startsWith('file:')) {
          const path = relative(root, fileURLToPath(value.url));
          if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path)) {
            throw new Error('PAYLOAD_DEPENDENCY_ESCAPED');
          }
        }
        return value;
      } });
    `;
    env.NODE_OPTIONS = `--import=data:text/javascript,${encodeURIComponent(hook)}`;
  }
  const child = spawn(process.execPath, [entry, ...argv], {
    cwd: root, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true,
  });
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

async function assertRealBusiness(peer, product, domains) {
  const description = await peer.call('runtime.describe');
  assert.equal(description.protocol, 1);
  assert.equal(description.product, product);
  assert.deepEqual(description.modules.map(module => module.id), domains);
  assert.ok(description.modules.every(module => module.methods.every(method => method.startsWith(`${module.id}.`))));
  for (const [domain, method] of [
    ['film', 'film.search.variants'], ['music', 'music.lyrics.parse'], ['reader', 'reader.sources.inspect'],
  ]) {
    if (!domains.includes(domain)) await assert.rejects(peer.call(method), { code: 'METHOD_UNAVAILABLE' });
  }
  await assert.rejects(peer.call('eval', privateMarker), { code: 'METHOD_UNAVAILABLE' });
  await assert.rejects(peer.call('runtime.describe', {}), { code: 'INVALID_MESSAGE' });
  const music = description.modules.find(module => module.id === 'music');
  assert.equal(music.version, '0.5.0');
  assert.ok(music.methods.every(method => method.startsWith('music.script.')));
  const source = description.modules.find(module => module.id === 'source');
  assert.equal(source.version, '0.1.0');
  assert.ok(source.methods.every(method => method.startsWith('source.runtime.')));
  assert.deepEqual(await peer.call('music.script.status', {}), {
    ok: true, contract: 'music.script.v2', documentAccess: false, instances: [],
  });
  assert.deepEqual(await peer.call('source.runtime.status', {}), {
    ok: true, contract: 'source.runtime.v1', instances: [],
  });
  for (const method of ['music.library.list', 'music.queue.get', 'music.history.list',
    'music.storage.flush', 'music.lyrics.parse', 'music.accounts.list', 'music.catalog.search',
    'film.script.status', 'film.playback.recognize', 'film.sources.list', 'film.library.read',
    'reader.rules.reset', 'reader.sources.setAccess', 'reader.library.list', 'reader.content.chapter'])
    await assert.rejects(peer.call(method, {}), { code: 'METHOD_UNAVAILABLE' });
}

async function verifyScriptExecution(t, peer, storageCalls) {
  let requests = 0;
  const upstream = createHTTPServer((request, response) => {
    requests++;
    assert.equal(request.url, '/fixture');
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end('{"fixture":"local-http"}');
  });
  t.after(async () => {
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
    const startup = meow.storage.get('startup').then(() => 'UNEXPECTED', error => error.code);
    const background = new Promise(resolve => setTimeout(() => {
      meow.storage.get('background').then(() => resolve('UNEXPECTED'), error => resolve(error.code));
    }, 1));
    let previousContext;
    lx.on(lx.EVENT_NAMES.request, async ({source, action}, context) => {
      const startupCode = await startup, backgroundCode = await background;
      let globalCode;
      try { await meow.storage.get('global'); globalCode = 'UNEXPECTED'; }
      catch (error) { globalCode = error.code; }
      let retiredCode = 'NO_PREVIOUS';
      if (previousContext) {
        try { await previousContext.storage.get('retired'); retiredCode = 'UNEXPECTED'; }
        catch (error) { retiredCode = error.code; }
      }
      previousContext = context;
      await Promise.resolve();
      const stored = await context.storage.get('probe-' + action);
      const network = await new Promise((resolve, reject) => lx.request(${JSON.stringify(url)}, {},
        (error, response, body) => error ? reject(error) : resolve(body)));
      return {source, action, startupCode, backgroundCode, globalCode, retiredCode, stored, network};
    });
    lx.send(lx.EVENT_NAMES.inited, {sources:{
      '${siteId}':{name:'Fixture',type:'music',actions:['fixtureAction','musicUrl'],
        qualitys:[{type:'320k',label:'Fixture 320'}]}
    }});
  `);
  const okay = reply => {
    assert.equal(reply.ok, true, JSON.stringify(reply));
    assert.equal(reply.contract, 'music.script.v2');
    return reply;
  };
  const staged = okay(await peer.call('music.script.stage.begin', {
    basename: 'packaged-fixture.js', length: source.length, sha256: sha256(source),
  }));
  assert.equal(okay(await peer.call('music.script.stage.chunk', {
    token: staged.token, offset: 0, data: source.toString('base64'),
  })).next, source.length);
  const grant = randomUUID();
  const instance = okay(await peer.call('music.script.load', {
    token: staged.token, installationId: 'packaged-fixture', revision: 1, expectedRevision: 0,
    version: 'fixture-1', config: {}, permissions: { storage: ['read'] }, grant,
  })).instance;
  assert.deepEqual(instance.capabilities[siteId].actions, ['fixtureAction', 'musicUrl']);
  assert.deepEqual(instance.capabilities[siteId].qualitys, ['320k']);
  assert.equal(storageCalls.length, 0, 'startup/background storage never reaches native');
  for (const operation of ['fixtureAction', 'musicUrl']) {
    const result = okay(await peer.call('music.script.invoke', {
      installationId: instance.installationId, revision: instance.revision, generation: instance.generation,
      siteId, operation, payload: { quality: '320k', musicInfo: {} },
    }));
    assert.deepEqual(result.value, {
      source: siteId, action: operation,
      startupCode: 'PERMISSION_DENIED', backgroundCode: 'PERMISSION_DENIED', globalCode: 'PERMISSION_DENIED',
      retiredCode: operation === 'fixtureAction' ? 'NO_PREVIOUS' : 'PERMISSION_DENIED',
      stored: 'native-scoped-value', network: { fixture: 'local-http' },
    });
  }
  assert.deepEqual(storageCalls, ['fixtureAction', 'musicUrl'].map(operation => ({
    grant, installationId: instance.installationId, siteId, generation: instance.generation, key: `probe-${operation}`,
  })));
  assert.equal(requests, 2, 'the controlled local HTTP service is reachable without an origin/private-network grant');
  assert.equal(okay(await peer.call('music.script.unload', {
    installationId: instance.installationId, revision: instance.revision, generation: instance.generation,
  })).cleanupComplete, true);
  assert.deepEqual(okay(await peer.call('music.script.status', {})).instances, []);
}

async function verifySourceExecution(t, peer) {
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
    assert.equal(reply.contract, 'source.runtime.v1');
    return reply;
  };
  const staged = okay(await peer.call('source.runtime.stage.begin', {
    basename: 'packaged-reader.json',
    length: source.length,
    sha256: sha256(source),
  }));
  assert.equal(okay(await peer.call('source.runtime.stage.chunk', {
    token: staged.token,
    offset: 0,
    data: source.toString('base64'),
  })).next, source.length);
  const instance = okay(await peer.call('source.runtime.load', {
    token: staged.token,
    installationId: 'packaged-reader',
    revision: 1,
    expectedRevision: 0,
    protocol: 'legado',
    version: '1.0.0',
    metadata: {},
  })).instance;
  assert.deepEqual(instance.capabilities.source, {
    protocol: 'legado',
    importedProtocol: 'legado',
    actions: ['search', 'loginStatus', 'image'],
  });
  const result = okay(await peer.call('source.runtime.invoke', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
    action: 'search',
    payload: { keyword: 'fixture', page: 1 },
  })).value;
  assert.equal(result.books[0].name, 'Portable Book');
  assert.equal(result.books[0].bookUrl, `${origin}/book/1`);
  assert.equal(okay(await peer.call('source.runtime.unload', {
    installationId: instance.installationId,
    revision: instance.revision,
    generation: instance.generation,
  })).cleanupComplete, true);
  assert.deepEqual(okay(await peer.call('source.runtime.status', {})).instances, []);
}

async function runProduct(t, product, entry, payload) {
  const counts = new Map(['music.library.v1', 'reader.sources.v1', 'reader.library.v1',
    'film.sources.v1', 'film.library.v1'].map(namespace => [namespace, 0]));
  let reads = 0, releases = 0;
  const storageCalls = [];
  // Tripwires: no script-only product may touch ordinary native documents.
  const methods = {
    'platform.documents.read.begin': params => {
      reads++;
      counts.set(params.namespace, (counts.get(params.namespace) ?? 0) + 1);
      assert.fail('Script-only product attempted native document access');
    },
    'platform.documents.release': () => {
      releases++;
      assert.fail('Script-only product attempted native document transfer release');
    },
    'platform.musicScript.storage.get': params => {
      storageCalls.push(structuredClone(params));
      return { ok: true, value: 'native-scoped-value' };
    },
  };
  const server = await pipeServer(t, { methods });
  const child = spawnRuntime(t, entry, server.argv, payload);
  const { peer } = await bounded(server.connection);
  await assertRealBusiness(peer, product, products[product]);
  await verifyScriptExecution(t, peer, storageCalls);
  await verifySourceExecution(t, peer);
  assert.equal(server.peers.length, 1, 'one runtime connection serves the whole product');
  assert.equal(reads, 0);
  assert.equal(counts.get('reader.sources.v1'), 0);
  assert.equal(counts.get('reader.library.v1'), 0);
  assert.equal(counts.get('music.library.v1'), 0);
  assert.equal(counts.get('film.sources.v1'), 0);
  assert.equal(counts.get('film.library.v1'), 0);
  assert.equal(reads, releases);
  peer.close();
  assert.deepEqual(await bounded(child.exit), { code: 0, signal: null, stdout: '', stderr: '' });
}

for (const product of Object.keys(products)) {
  test(`${product}: thin platform entry explicitly imports its shared factories`, () => {
    const source = readFileSync(join(root, 'apps/windows/runtime/entries', `${product}.mjs`), 'utf8');
    assert.deepEqual([...source.matchAll(/^import\s+\{[^{}]+\}\s+from\s+['"]([^'"]+)['"];/gm)].map(match => match[1]),
      ['../bootstrap.mjs', '../../../runtime/modules/music-script.mjs',
        '../../../runtime/modules/source-runtime.mjs']);
    assert.equal(source.match(/await startWindowsAppRuntime\(/g)?.length, 1);
    if (products[product].includes('music')) assert.ok(source.includes('createMusicScriptModule({ call })'));
    if (products[product].includes('source')) {
      assert.ok(source.includes("createSourceRuntimeModule({ call, platform: 'windows' })"));
    }
  });
  test(`${product}: real source entry, named pipe and native-only business boundary`, windows, async t => {
    await runProduct(t, product, join(root, 'apps/windows/runtime/entries', `${product}.mjs`));
  });
  test(`${product}: package hashes, license/closure preservation and executable Windows entry`,
    { timeout: 60000 }, async t => {
      const area = mkdtempSync(join(temporary, `${product}-`)), output = join(area, 'payload');
      const manifest = await packageWindowsRuntime({ root, product, output });
      verifyManifest(output, manifest);
      const base = await packageRuntime({ root, product, output: join(area, 'shared-payload'), profile: 'windows-native' });
      assert.equal(base.status, 'platform-pending', 'an assembly without the Windows entry is not executable');
      assert.deepEqual(manifest.files.filter(file => !file.path.startsWith('app/windows/')), base.files,
        'the wrapper preserves the explicitly selected Windows dependency closure');
      assert.deepEqual(manifest.dependencies, base.dependencies);
      assert.deepEqual(manifest.omittedOptionalDependencies, base.omittedOptionalDependencies);
      if (process.platform === 'win32') await runProduct(t, product, join(output, manifest.entry), output);
      else await t.test('packaged Windows named-pipe execution', { skip: 'Requires Windows' }, () => {});
    });
}

test('CLI launch failure emits only a fixed code, never argv or underlying diagnostics', async t => {
  const child = spawnRuntime(t, join(root, 'apps/windows/runtime/entries/aggregate.mjs'),
    ['--meow-pipe', privateMarker, '--meow-session', privateMarker]);
  const result = await bounded(child.exit);
  assert.deepEqual(result, { code: 1, signal: null, stdout: '', stderr: 'WINDOWS_RUNTIME_START_FAILED\n' });
});

// Synthetic packaging inputs. Fixture code must never be imported or run;
// dependency traversal and failure-manifest assertions need no network/npm.
function packagingFixture({
  missingBootstrap = false, extraSource = false, extraImport = false,
  missingShared = false, missingCommonModule = false, platformModule = false,
} = {}) {
  const area = mkdtempSync(join(temporary, 'failure-')), source = join(area, 'source');
  const manifest = { name: 'windows-package-fixture', version: '1.0.0', private: true, workspaces: ['packages/*'] };
  const lock = { name: manifest.name, version: manifest.version, lockfileVersion: 3, packages: { '': { ...manifest } } };
  const inert = 'throw new Error("Synthetic fixture must not execute");\n';
  write(source, 'package.json', manifest);
  for (const name of ['runtime-bridge', 'source-runtime', 'script-host', 'inactive-core']) {
    const location = `packages/${name}`, installed = `node_modules/@meowfilm/${name}`;
    const pkg = {
      name: `@meowfilm/${name}`,
      version: '1.0.0',
      type: 'module',
      license: 'MIT',
      exports: './src/index.js',
      ...(name === 'script-host'
        ? { dependencies: { '@meowfilm/source-runtime': '1.0.0' } }
        : {}),
    };
    write(source, `${location}/package.json`, pkg);
    if (!(missingShared && name === 'script-host')) write(source, `${location}/src/index.js`, inert);
    write(source, `${location}/LICENSE`, 'Synthetic fixture license\n');
    lock.packages[location] = {
      version: pkg.version,
      ...(pkg.dependencies ? { dependencies: pkg.dependencies } : {}),
    };
    lock.packages[installed] = { resolved: location, link: true };
    mkdirSync(dirname(join(source, installed)), { recursive: true });
    symlinkSync(join(source, location), join(source, installed), process.platform === 'win32' ? 'junction' : 'dir');
  }
  write(source, 'package-lock.json', lock);
  write(source, 'apps/runtime/bootstrap.mjs', inert);
  write(source, 'apps/runtime/modules/music-script.mjs', inert);
  if (!missingCommonModule) write(source, 'apps/runtime/modules/source-runtime.mjs', inert);
  for (const path of ['bootstrap.mjs', 'entries/aggregate.mjs']) {
    if (missingBootstrap && path === 'bootstrap.mjs') continue;
    let text = readFileSync(join(root, 'apps/windows/runtime', path), 'utf8');
    if (extraImport && path === 'bootstrap.mjs') text += "\nimport { unexpected } from './unexpected.mjs';\n";
    write(source, `apps/windows/runtime/${path}`, text);
  }
  if (extraSource) write(source, 'apps/windows/runtime/unlisted-helper.mjs', inert);
  if (platformModule) write(source, 'apps/windows/runtime/modules/music-script.mjs', inert);
  return { root: source, area, output: join(area, 'payload') };
}

test('packaging rejects existing output without changing a single prior byte', async () => {
  const f = packagingFixture();
  mkdirSync(f.output);
  write(f.output, 'runtime-manifest.json', { status: 'existing-do-not-touch' });
  write(f.output, 'sentinel.txt', 'preserve');
  const before = files(f.output).map(path => [path, sha256(readFileSync(join(f.output, path)))]);
  await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code: 'OUTPUT_EXISTS' });
  assert.deepEqual(files(f.output).map(path => [path, sha256(readFileSync(join(f.output, path)))]), before);
});

for (const [option, code] of [
  ['missingBootstrap', 'ENOENT'], ['extraSource', 'WINDOWS_CLOSURE'],
  ['extraImport', 'WINDOWS_IMPORT'], ['missingShared', 'MISSING_PUBLIC_FILE'],
  ['missingCommonModule', 'MISSING_SOURCE'], ['platformModule', 'WINDOWS_CLOSURE'],
]) {
  test(`packaging ${option} is explicit and retains a failed file inventory`, async () => {
    const f = packagingFixture({ [option]: true });
    await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code });
    const manifest = readJSON(join(f.output, 'runtime-manifest.json'));
    assert.equal(manifest.status, 'failed');
    assert.equal(manifest.error.code, code);
    for (const file of manifest.files) {
      assert.equal(sha256(readFileSync(join(f.output, file.path))), file.sha256);
    }
    if (!['missingShared', 'missingCommonModule'].includes(option)) {
      assert.equal(manifest.transport, 'windows-named-pipe-v1');
      assert.equal(manifest.entry, 'app/windows/entries/aggregate.mjs');
    }
  });
}

test('Windows source junctions are rejected, not dereferenced or silently omitted', async () => {
  const f = packagingFixture();
  const entries = join(f.root, 'apps/windows/runtime/entries'), relocated = join(f.root, 'saved-entries');
  assert.ok(within(temporary, entries) && within(temporary, relocated));
  renameSync(entries, relocated);
  symlinkSync(relocated, entries, process.platform === 'win32' ? 'junction' : 'dir');
  await assert.rejects(packageWindowsRuntime({ ...f, product: 'aggregate' }), { code: 'UNSAFE_SYMLINK' });
  assert.equal(readJSON(join(f.output, 'runtime-manifest.json')).status, 'failed');
  assert.ok(existsSync(join(relocated, 'aggregate.mjs')), 'source is never removed');
});

test('App payload contains the common script assemblies and only the Windows platform entry', async () => {
  const output = join(mkdtempSync(join(temporary, 'closure-')), 'payload');
  const manifest = await packageWindowsRuntime({ root, product: 'aggregate', output });
  assert.deepEqual(manifest.modules, ['music', 'source']);
  assert.deepEqual(manifest.files.filter(file => file.path.startsWith('app/')).map(file => file.path), [
    'app/bootstrap.mjs', 'app/modules/music-script.mjs', 'app/modules/source-runtime.mjs',
    'app/windows/bootstrap.mjs', 'app/windows/entries/aggregate.mjs',
  ]);
});

test('Windows packaging is reproducible and does not execute fixture code', async () => {
  const f = packagingFixture();
  const first = await packageWindowsRuntime({ ...f, product: 'aggregate' });
  const second = await packageWindowsRuntime({ root: f.root, product: 'aggregate', output: join(f.area, 'second') });
  assert.deepEqual(first, second);
  assert.deepEqual(readFileSync(join(f.output, 'runtime-manifest.json')),
    readFileSync(join(f.area, 'second/runtime-manifest.json')));
});

test('packaging CLI accepts only product/output and produces a real Windows manifest', { timeout: 30000 }, () => {
  const script = join(root, 'scripts/windows/package-runtime.mjs');
  const area = mkdtempSync(join(temporary, 'cli-')), output = join(area, 'payload');
  const invoke = args => spawnSync(process.execPath, [script, ...args], {
    cwd: root, encoding: 'utf8', timeout: 20000, windowsHide: true,
  });
  for (const args of [
    [], ['--product', 'aggregate'], ['--output', output],
    ['--product', 'aggregate', '--product', 'music', '--output', output],
    ['--product', 'aggregate', '--output', output, '--unknown', 'x'],
    ['--product', 'aggregate', '--output', output, '--output', output],
  ]) {
    const result = invoke(args);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /^CLI_USAGE:/);
    assert.equal(existsSync(output), false);
  }
  const result = invoke(['--product', 'aggregate', '--output', output]);
  assert.equal(result.status, 0, result.stderr);
  const summary = JSON.parse(result.stdout);
  assert.equal(summary.transport, 'windows-named-pipe-v1');
  assert.equal(summary.entry, 'app/windows/entries/aggregate.mjs');
  verifyManifest(output, readJSON(join(output, 'runtime-manifest.json')));
});
