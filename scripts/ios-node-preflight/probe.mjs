import assert from 'node:assert/strict';
import { createHash, createCipheriv, createDecipheriv, randomBytes, webcrypto, createHmac, generateKeyPairSync, publicEncrypt, constants } from 'node:crypto';
import { mkdirSync, writeFileSync, readFileSync, openSync, readSync, fsyncSync, closeSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { Worker } from 'node:worker_threads';
import { once } from 'node:events';
import http from 'node:http';
import { isUtf8 } from 'node:buffer';
import { deflateSync } from 'node:zlib';

const directory = process.argv[2];
const selected = process.argv[3] === '--case' && process.argv.length === 5 ? process.argv[4] : undefined;
assert.ok(process.argv.length === 3 || selected, 'Use --case <exact name>');
const report = {
  node: process.version, platform: process.platform, arch: process.arch,
  v8: process.versions.v8, execArgv: process.execArgv,
  icu: process.versions.icu ?? null,
  webAssembly: typeof globalThis.WebAssembly, cases: [],
};
async function check(name, run) {
  if (selected && selected !== name) return;
  const started = performance.now();
  try { report.cases.push({ name, passed: true, detail: await run() ?? null, ms: performance.now() - started }); }
  catch (error) {
    report.cases.push({ name, passed: false, error: {
      name: error.name, code: error.code ?? null, message: error.message,
    } });
  }
  writeFileSync(join(directory, 'node-report.json'), JSON.stringify(report, null, 2));
}

await check('ios-native-framework-payload', async () => {
  assert.equal(process.platform, 'ios');
  const manifest = JSON.parse(readFileSync(new URL('./runtime-manifest.json', import.meta.url), 'utf8'));
  assert.equal(manifest.assemblyProfile, 'common-native');
  assert.equal(manifest.transport, 'runtime.http.v1');
  assert.deepEqual(manifest.modules, ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease']);
  assert.equal(manifest.trustedNative.length, 1);
  const native = manifest.trustedNative[0];
  assert.equal(native.path, 'Frameworks/MeowScriptHost.framework/MeowScriptHost');
  assert.equal(native.platform, process.platform);
  assert.equal(native.arch, process.arch);
  assert.equal(native.abi, 1);
  assert.equal(native.napi, 8);
  assert.match(native.bindingIdentity, /^[a-f0-9]{64}$/);
  return { target: native.target, bindingIdentity: native.bindingIdentity };
});
await check('strict-utf8', async () => {
  assert.equal(isUtf8(Buffer.from('中文🎵')), true);
  assert.equal(Buffer.from([0xef, 0xbb, 0xbf, 0x61]).toString('utf8'), '\uFEFFa');
  for (const bytes of [[0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xe2, 0x82]])
    assert.equal(isUtf8(Buffer.from(bytes)), false);
});
await check('filesystem-fsync-rename', async () => {
  const { renameSync } = await import('node:fs');
  const temporary = join(directory, 'bytes.tmp'), final = join(directory, 'bytes.txt');
  const fd = openSync(temporary, 'w');
  try { writeFileSync(fd, 'persistent'); fsyncSync(fd); } finally { closeSync(fd); }
  renameSync(temporary, final);
  const parent = openSync(directory, 'r');
  try { fsyncSync(parent); } finally { closeSync(parent); }
  assert.equal(readFileSync(final, 'utf8'), 'persistent');
  rmSync(final);
});
await check('native-bootstrap-descriptor-ownership', async () => {
  const { duplicateBootstrapDescriptor } = await import('./node_modules/@meowfilm/script-host/src/native-engine.js');
  const path = join(directory, 'descriptor.txt');
  writeFileSync(path, 'native');
  const original = openSync(path, 'r');
  let copy;
  try {
    copy = duplicateBootstrapDescriptor(original);
    assert.notEqual(copy, original);
    assert.ok(copy >= 3);
    const byte = Buffer.alloc(1);
    assert.equal(readSync(copy, byte, 0, 1, null), 1);
    assert.equal(byte.toString(), 'n');
    closeSync(copy); copy = undefined;
    assert.equal(readSync(original, byte, 0, 1, null), 1);
    assert.equal(byte.toString(), 'a');
    assert.throws(() => duplicateBootstrapDescriptor(-1), { code: 'INVALID_ARGUMENT' });
  } finally {
    if (copy !== undefined) closeSync(copy);
    closeSync(original);
    rmSync(path);
  }
});
await check('crypto-and-webcrypto', async () => {
  assert.equal(createHash('sha256').update('abc').digest('hex'),
    'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  const key = randomBytes(32), iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, iv);
  const encrypted = Buffer.concat([cipher.update('plaintext'), cipher.final()]);
  const decipher = createDecipheriv('aes-256-gcm', key, iv);
  decipher.setAuthTag(cipher.getAuthTag());
  assert.equal(Buffer.concat([decipher.update(encrypted), decipher.final()]).toString(), 'plaintext');
  const digest = await webcrypto.subtle.digest('SHA-256', Buffer.from('abc'));
  assert.equal(Buffer.from(digest).toString('hex'), createHash('sha256').update('abc').digest('hex'));
});
await check('http-loopback', async () => {
  const server = http.createServer((request, response) => response.end('node-mobile'));
  try {
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const body = await new Promise((resolve, reject) => {
      http.get(`http://127.0.0.1:${server.address().port}`, response => {
        const chunks = [];
        response.on('data', data => chunks.push(data));
        response.on('end', () => resolve(Buffer.concat(chunks).toString()));
        response.on('error', reject);
      }).on('error', reject);
    });
    assert.equal(body, 'node-mobile');
  } finally {
    await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  }
});
await check('worker-and-runaway-termination', async () => {
  // Authored trusted-host fixture; not a fallback engine for user scripts.
  const worker = new Worker(`
    const { parentPort } = require('node:worker_threads');
    parentPort.postMessage({ value: 6 * 7, wasm: typeof WebAssembly });
    parentPort.on('message', () => {
      parentPort.postMessage('entered');
      while (true) {}
    });
  `, { eval: true });
  try {
    const [message] = await once(worker, 'message');
    assert.equal(message.value, 42);
    const entered = once(worker, 'message');
    worker.postMessage('runaway');
    assert.equal((await entered)[0], 'entered');
    assert.equal(await worker.terminate(), 1);
    return message;
  } finally { await worker.terminate(); }
});

await check('public-explicit-film-modules-http-and-local-settings', async () => {
  const { createRuntime, createRuntimeClient } = await import('./app/entry.mjs');
  const { contracts } = await import('./app/contracts.mjs');
  const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86';
  const runtime = createRuntime({ sessionId, dataRoot: join(directory, 'app-modules'),
    source: { platform: 'ios' }, pan: {}, tmdb: {}, douban: {}, film: {} });
  const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
  let client;
  try {
    await runtime.start();
    client = createRuntimeClient({ baseUrl: runtime.baseUrl });
    const described = okay(await client.describe());
    assert.equal(described.protocol, 'runtime.http.v1');
    assert.deepEqual(described.modules.map(value => value.id), ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film']);
    for (const module of described.modules) {
      const spec = contracts[module.id];
      assert.equal(module.version, spec.version);
      assert.equal(module.contract, spec.contract);
      assert.deepEqual(module.methods, spec.operations.map(operation => spec.prefix + operation).sort());
    }
    for (const method of ['music.script.sources.list', 'pan.settings.get', 'tmdb.data.settings.get',
      'douban.data.settings.get', 'film.settings.get', 'film.library.get']) okay(await client.call(method));
    const release = await client.call('film.session.media.release', {});
    assert.equal(release.ok, false);
    assert.equal(release.contract, 'film.playback.v1');
    assert.equal(release.code, 'INVALID_REQUEST');
    assert.deepEqual(okay(await client.shutdown()), { closed: true, cleanupComplete: true });
    return { modules: described.modules.map(value => value.id) };
  } finally { await runtime.close(); await client?.close(); }
});

await check('public-three-module-http-and-source-sdk', async () => {
  const { createRuntime, createRuntimeClient } = await import('./app/entry.mjs');
  const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86';
  const runtime = createRuntime({ sessionId, dataRoot: join(directory, 'three-modules'),
    source: { platform: 'ios' } });
  const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
  let client;
  try {
    await runtime.start();
    client = createRuntimeClient({ baseUrl: runtime.baseUrl });
    const described = okay(await client.describe());
    assert.equal(described.protocol, 'runtime.http.v1');
    assert.deepEqual(described.modules.map(value => value.contract),
      ['music.script.v3', 'cat.script.v2', 'source.runtime.v2']);
    okay(await client.upload('source', 'preflight-source', 1, Buffer.from(
      'export default { searchContent(keyword) { return {list:[{vod_id:keyword,vod_name:keyword}]}; } };'),
      { basename: 'fixture.js' }));
    const source = okay(await client.call('source.runtime.load', {
      installationId: 'preflight-source', sourceRevision: 1, revision: 1, expectedRevision: 0, protocol: 'cat',
    })).instance;
    assert.deepEqual(okay(await client.call('source.runtime.invoke', {
      installationId: source.installationId, revision: source.revision, generation: source.generation,
      action: 'search', payload: { keyword: '中文' },
    })), { list: [{ vod_id: '中文', vod_name: '中文' }] });
    assert.deepEqual(okay(await client.shutdown()), { closed: true, cleanupComplete: true });
  } finally { await runtime.close(); await client?.close(); }
});

await check('ios-eight-module-http-and-tmdb', async () => {
  const { createRuntimeClient } = await import('./app/entry.mjs');
  const { createConnection, createServer } = await import('node:net');
  const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86';
  const dataRoot = join(directory, 'eight-modules');
  const seen = [];
  const upstream = http.createServer((request, response) => {
    seen.push(request.url);
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ id: 42, title: '预检', poster_path: '/preflight.jpg' }));
  });
  const bootstrap = createServer();
  const okay = reply => { assert.equal(reply.ok, true, JSON.stringify(reply)); return reply.value; };
  const originalArgv = process.argv;
  let client, owner, nativeChannel;
  try {
    upstream.listen(0, '127.0.0.1');
    await once(upstream, 'listening');
    bootstrap.listen(0, '127.0.0.1');
    await once(bootstrap, 'listening');
    const connected = once(bootstrap, 'connection');
    nativeChannel = createConnection(bootstrap.address().port, '127.0.0.1');
    await once(nativeChannel, 'connect');
    nativeChannel.pause();
    [owner] = await connected;
    const ready = new Promise((resolve, reject) => {
      let bytes = Buffer.alloc(0);
      owner.on('error', reject);
      owner.once('close', () => reject(new Error('App bootstrap closed before ready')));
      owner.setTimeout(10000, () => owner.destroy(new Error('App bootstrap did not become ready')));
      owner.on('data', chunk => {
        bytes = Buffer.concat([bytes, chunk]);
        if (bytes.includes(10)) {
          try {
            assert.equal(bytes.indexOf(10), bytes.length - 1);
            const value = JSON.parse(bytes.toString('utf8'));
            assert.deepEqual(Object.keys(value), ['baseUrl']);
            resolve(value.baseUrl);
          } catch (error) { reject(error); }
        }
      });
    });
    ready.catch(() => {});
    const manifest = JSON.parse(readFileSync(new URL('./runtime-manifest.json', import.meta.url), 'utf8'));
    // Diagnostic-only handle access supplies a real connected owner descriptor
    // to the unchanged packaged entry and its native dup; no binding is mocked.
    assert.ok(Number.isInteger(nativeChannel._handle.fd) && nativeChannel._handle.fd >= 3);
    process.argv = [originalArgv[0], originalArgv[1], '--meow-bootstrap-fd', String(nativeChannel._handle.fd)];
    owner.write(`${JSON.stringify({ sessionId, dataRoot })}\n`);
    await import('./' + manifest.entry);
    process.argv = originalArgv;
    client = createRuntimeClient({ baseUrl: await ready });
    owner.setTimeout(0);
    const described = okay(await client.describe());
    assert.deepEqual(described.modules.map(value => value.id), manifest.modules);
    assert.deepEqual(described.modules.map(value => [value.id, value.version, value.contract]), [
      ['music', '0.8.0', 'music.script.v3'], ['cat', '0.2.0', 'cat.script.v2'],
      ['source', '0.3.0', 'source.runtime.v2'], ['pan', '0.1.0', 'pan.runtime.v1'],
      ['tmdb', '0.1.0', 'tmdb.data.v1'], ['douban', '0.1.0', 'douban.data.v1'],
      ['film', '0.1.0', 'film.playback.v1'], ['netease', '0.1.0', 'netease.token.v1'],
    ]);
    const settings = okay(await client.call('tmdb.data.settings.update', {
      apiBase: `http://127.0.0.1:${upstream.address().port}/proxy/3`,
    }));
    assert.equal(settings.apiKey, '');
    assert.equal(settings.accessToken, '');
    assert.deepEqual(okay(await client.call('tmdb.data.request', {
      path: '/3/movie/42', query: { language: 'zh-CN' }, imageSize: 'w500',
    })), { id: 42, title: '预检', poster_path: '/v1/tmdb/images/w500/preflight.jpg' });
    assert.deepEqual(seen, ['/proxy/3/movie/42?language=zh-CN']);
    assert.deepEqual(okay(await client.shutdown()), { closed: true, cleanupComplete: true });
    const closed = once(owner, 'close');
    owner.end(); nativeChannel.destroy();
    await closed;
  } finally {
    process.argv = originalArgv;
    await client?.close();
    owner?.destroy(); nativeChannel?.destroy();
    await new Promise(resolve => bootstrap.close(resolve));
    upstream.closeAllConnections();
    await new Promise(resolve => upstream.close(resolve));
  }
});

await check('native-thread-isolation-typed-tsfn-and-algorithms', async () => {
  const { NativeEngine } = await import('./node_modules/@meowfilm/script-host/src/native-engine.js');
  const source = `(() => { let count = 0; return {
    next() { return ++count; },
    inspect() { return [typeof process, typeof require, String(BigInt.asUintN(128, -1n))]; },
    async echo(value) { return await __meowHost('echo', value); },
    util(op, args) { return __meowNative(op, args); }
  }; })()`;
  const engines = [];
  try {
    for (let i = 0; i < 2; i++) {
      const engine = NativeEngine.create({ source, onHostCall: async (op, input) => {
        assert.equal(op, 'echo'); await Promise.resolve(); return input;
      } });
      engines.push(engine); await engine.ready;
    }
    const [first, second] = engines;
    assert.equal(await first.call('next'), 1);
    assert.equal(await first.call('next'), 2);
    assert.equal(await second.call('next'), 1);
    assert.deepEqual(await first.call('inspect'), ['undefined', 'undefined', String((1n << 128n) - 1n)]);
    const bytes = Buffer.from([0, 255, 65]);
    assert.deepEqual(await first.call('echo', [{ text: '字节', bytes }], { scope: 42 }), { text: '字节', bytes });
    const util = (op, args) => first.call('util', [op, args]);
    assert.equal(await util('digest', ['sha256', bytes]), createHash('sha256').update(bytes).digest('hex'));
    assert.equal(await util('hmac', ['sha256', 'key', bytes]), createHmac('sha256', 'key').update(bytes).digest('hex'));
    const key = Buffer.alloc(16, 7), iv = Buffer.alloc(16, 9);
    const cipher = createCipheriv('aes-128-cbc', key, iv);
    const encrypted = Buffer.concat([cipher.update(bytes), cipher.final()]);
    assert.deepEqual(await util('aes', [bytes, 'aes-128-cbc', key, iv]), encrypted);
    assert.deepEqual(await util('aesDecrypt', [encrypted, 'aes-128-cbc', key, iv]), bytes);
    const { publicKey } = generateKeyPairSync('rsa', { modulusLength: 1024 });
    const padded = Buffer.alloc(128); bytes.copy(padded, 128 - bytes.length);
    assert.deepEqual(await util('rsa', [bytes, publicKey.export({ type: 'spki', format: 'pem' })]),
      publicEncrypt({ key: publicKey, padding: constants.RSA_NO_PADDING }, padded));
    const compressed = await util('deflate', [bytes]);
    assert.deepEqual(compressed, deflateSync(bytes));
    assert.deepEqual(await util('inflate', [compressed]), bytes);
    for (const engine of engines) {
      const closed = await engine.stop();
      for (const key of ['runtimeCount', 'contextCount', 'threadCount', 'activeCalls', 'hostCalls', 'timers'])
        assert.equal(closed[key], 0, key);
      assert.equal(closed.stopped, true);
    }
  } finally { await Promise.all(engines.map(engine => engine.stop())); }
});
await check('lx-stuck-guest-disable-and-reenable', async () => {
  const { createScriptHost } = await import('@meowfilm/script-host');
  const host = createScriptHost();
  const target = {
    installationId: 'ios-stuck', siteId: 'kw', operation: 'musicUrl',
    payload: { quality: '320k', musicInfo: { stuck: true } },
  };
  try {
    await host.install({
      installationId: target.installationId, enabled: true,
      source: `lx.on('request', async ({info}) => {
        if (info.musicInfo.stuck) {
          lx.send('updateAlert', {log:'entered'});
          while (true) {}
        }
        return 'healthy';
      });
      lx.send('inited', {status:true,sources:{
        kw:{type:'music',actions:['musicUrl'],qualitys:['320k']}
      }});`,
    });
    const pending = host.invoke(target).then(value => ({ value }), error => ({ code: error.code }));
    const deadline = performance.now() + 10000;
    while (host.get(target.installationId).updateAlert?.log !== 'entered') {
      if (performance.now() > deadline) throw new Error('Guest did not enter its loop');
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    const started = performance.now();
    assert.equal((await host.disable(target.installationId)).state, 'disabled');
    const terminationMs = performance.now() - started;
    assert.ok(terminationMs < 1100, `disable took ${terminationMs}ms`);
    assert.ok((await pending).code, 'Retired invocation must not succeed');
    assert.equal(host.stats().liveSessions, 0);
    assert.equal((await host.enable(target.installationId)).state, 'ready');
    assert.equal(await host.invoke({
      ...target, payload: { quality: '320k', musicInfo: {} },
    }), 'healthy');
    return { terminationMs };
  } finally {
    await host.close();
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().activeRequests, 0);
  }
});
await check('public-lx-install-invoke-close', async () => {
  const { createScriptHost } = await import('@meowfilm/script-host');
  const host = createScriptHost();
  try {
    const state = await host.install({
      installationId: 'ios-lx',
      source: `lx.on('request', async () => 'https://example.invalid/fixture.mp3');
        lx.send('inited', { status: true, sources: {
          kw: { type: 'music', actions: ['musicUrl'], qualitys: ['320k'] }
        }});`,
      enabled: true,
    });
    assert.equal(state.state, 'ready');
    assert.equal(await host.invoke({
      installationId: 'ios-lx', siteId: 'kw', operation: 'musicUrl',
      payload: { quality: '320k', musicInfo: {} },
    }), 'https://example.invalid/fixture.mp3');
    await host.disable('ios-lx');
  } finally {
    await host.close();
    assert.equal(host.stats().liveSessions, 0);
  }
});
await check('public-cat-install-invoke-close', async () => {
  const { createCatRuntime } = await import('@meowfilm/script-host');
  const dataRoot = join(directory, 'cat');
  mkdirSync(dataRoot, { recursive: true });
  const runtime = createCatRuntime({ dataRoot });
  try {
    await runtime.install({
      installationId: 'ios-cat', config: { id: 'ios', protocol: 'old' }, enabled: true,
      source: `const http = require('http');
        const streams = require('stream/web');
        let server;
        async function web() {
          const bytes = new Uint8Array([9, 0, 255, 65, 8]).subarray(1, 4);
          const headers = new Headers({ 'X-Preflight': 'view', 'content-type': 'application/octet-stream' });
          const request = new Request('http://127.0.0.1:9978/echo', {
            method: 'POST', headers, body: bytes
          });
          const requestCopy = request.clone();
          const requestBody = request.body instanceof streams.ReadableStream;
          const requestCloneBody = requestCopy.body instanceof streams.ReadableStream;
          const requestBytes = Array.from(new Uint8Array(await requestCopy.arrayBuffer()));
          const blob = new Blob([bytes], { type: 'application/octet-stream' });
          const form = new FormData();
          form.append('text', '表单中文');
          form.append('file', blob, 'bytes.bin');
          const readForm = await new Response(form).formData();
          const file = readForm.get('file');
          const streamed = new Response(new streams.ReadableStream({ start(controller) {
            controller.enqueue(new TextEncoder().encode('stream中文'));
            controller.close();
          } }));
          const streamCopy = streamed.clone();
          const streamBodies = [streamed, streamCopy].map(value => value.body instanceof streams.ReadableStream);
          const response = await fetch(request);
          const responseCopy = response.clone();
          const responseBodies = [response, responseCopy].map(value => value.body instanceof streams.ReadableStream);
          const detail = { value: 7 };
          const custom = new CustomEvent('web', {
            detail, bubbles: true, cancelable: true, composed: true
          });
          const target = new EventTarget();
          let received, deliveries = 0;
          target.addEventListener('web', event => {
            deliveries++;
            received = [event === custom, event.detail === detail,
              event.target === target, event.currentTarget === target];
            event.detail.value = 8;
            event.preventDefault();
          }, { once: true });
          const dispatched = target.dispatchEvent(custom);
          target.dispatchEvent(new CustomEvent('web'));
          const plain = new CustomEvent('plain', { detail: false });
          target.addEventListener('plain', event => event.preventDefault(), { once: true });
          const plainDispatched = target.dispatchEvent(plain);
          return {
            constructors: [request instanceof Request, response instanceof Response,
              headers instanceof Headers, request.headers instanceof Headers,
              response.headers instanceof Headers, form instanceof FormData, blob instanceof Blob],
            bodies: [requestBody, requestCloneBody, ...streamBodies, ...responseBodies],
            request: { method: request.method, header: request.headers.get('x-preflight'), bytes: requestBytes },
            streams: await Promise.all([streamed.text(), streamCopy.text()]),
            form: { text: readForm.get('text'), blob: file instanceof Blob, name: file.name,
              type: file.type, bytes: Array.from(new Uint8Array(await file.arrayBuffer())) },
            fetch: { status: response.status, header: response.headers.get('x-preflight-echo'),
              values: await Promise.all([response.json(), responseCopy.json()]) },
            events: { native: [custom instanceof Event, target instanceof EventTarget],
              identity: [custom.detail === detail, ...received], detail: detail.value,
              flags: [custom.bubbles, custom.cancelable, custom.composed],
              dispatched, prevented: custom.defaultPrevented, deliveries,
              noncancelable: [plainDispatched, plain.defaultPrevented, plain.detail] }
          };
        }
        module.exports = {
          start() {
            server = http.createServer(async (request, response) => {
              response.setHeader('content-type', 'application/json');
              const path = new URL(request.url, 'http://127.0.0.1:9978').pathname;
              if (path === '/config' || path === '/full-config') {
                response.end(JSON.stringify({ video: { sites: [{
                  key: 'ios', name: 'iOS fixture', actions: ['home', 'web'],
                  api: 'http://127.0.0.1:9978/api/ios'
                }] } }));
              } else if (path === '/echo') {
                const chunks = [];
                for await (const chunk of request) chunks.push(chunk);
                response.statusCode = 201;
                response.setHeader('x-preflight-echo', 'local');
                response.end(JSON.stringify({ method: request.method,
                  bytes: Array.from(Buffer.concat(chunks)), header: request.headers['x-preflight'],
                  loopback: request.socket.remoteAddress === '127.0.0.1' &&
                    request.socket.localAddress === '127.0.0.1' &&
                    request.socket.localPort === server.address().port,
                  port: server.address().port }));
              } else if (path === '/api/ios/web') {
                try { response.end(JSON.stringify(await web())); }
                catch (error) {
                  response.statusCode = 500;
                  response.end(JSON.stringify({ error: error.message }));
                }
              } else response.end(JSON.stringify({ list: [] }));
            });
            server.listen(9978, '127.0.0.1');
          },
          stop() { if (server) server.close(); }
        };`,
    });
    const status = await runtime.status();
    assert.equal(status.instances[0].state, 'ready');
    assert.deepEqual(await runtime.invoke({
      installationId: 'ios-cat', siteId: 'ios', operation: 'home', payload: {},
    }), { list: [] });
    const web = await runtime.invoke({
      installationId: 'ios-cat', siteId: 'ios', operation: 'web', payload: {},
    });
    const port = web.fetch.values[0].port;
    assert.ok(Number.isInteger(port) && port > 0 && port <= 65535);
    const echo = { method: 'POST', bytes: [0, 255, 65], header: 'view', loopback: true, port };
    assert.deepEqual(web, {
      constructors: [true, true, true, true, true, true, true],
      bodies: [true, true, true, true, true, true],
      request: { method: 'POST', header: 'view', bytes: [0, 255, 65] },
      streams: ['stream中文', 'stream中文'],
      form: { text: '表单中文', blob: true, name: 'bytes.bin',
        type: 'application/octet-stream', bytes: [0, 255, 65] },
      fetch: { status: 201, header: 'local', values: [echo, echo] },
      events: { native: [true, true], identity: [true, true, true, true, true], detail: 8,
        flags: [true, true, true], dispatched: false, prevented: true, deliveries: 1,
        noncancelable: [true, false, false] },
    });
    await runtime.setEnabled('ios-cat', false);
    assert.equal((await runtime.status()).instances[0].state, 'disabled');
    await assert.rejects(new Promise((resolve, reject) => {
      http.get(`http://127.0.0.1:${port}/echo`, response => {
        response.resume(); resolve(response.statusCode);
      }).on('error', reject);
    }), { code: 'ECONNREFUSED' });
    return { web: true, loopbackPort: port, listenerClosed: true };
  } finally { await runtime.close(); }
});

await check('netease-token-sdk-worker', async () => {
  const { createNeteaseTokenProvider } = await import('./node_modules/@meowfilm/netease-token/src/index.js');
  const sdkURL = new URL('./node_modules/@meowfilm/netease-token/src/watchman.js', import.meta.url).href;
  let downloads = 0, provider;
  const upstream = http.createServer((_request, response) => {
    downloads++;
    response.writeHead(200, { 'content-type': 'application/javascript' });
    response.end(`window.initWatchman = ({productNumber,onload}) => {
        if (productNumber !== 'YD00000558929251') throw new Error('identity');
        let next = 0, ready = false;
        onload({getInstance: () => ({I(done) { ready = true; done(); }}),
          getToken(id, done) {
            if (!ready || id !== 'bd5d2f973ef74cd2a61325a412ae54d9') throw new Error('identity');
            ready = false; done('preflight-' + (++next));
          }});
      };`);
  });
  try {
    upstream.listen(0, '127.0.0.1');
    await once(upstream, 'listening');
    provider = createNeteaseTokenProvider({ workerFactory: () => new Worker(`
      const { parentPort } = require('node:worker_threads');
      // Only redirect this diagnostic worker's fixed official download to a synthetic loopback SDK.
      require('node:https').get = (url, options, receive) => {
        require('node:assert/strict').equal(url, 'https://acstatic-dun.126.net/tool.min.js');
        return require('node:http').get('http://127.0.0.1:${upstream.address().port}/tool.js', options, receive);
      };
      const ready = import(${JSON.stringify(sdkURL)}).then(({createWatchman}) => createWatchman());
      parentPort.on('message', async ({id}) => {
        try { parentPort.postMessage({id, token: await (await ready).get()}); }
        catch { parentPort.postMessage({id, code:'SDK_FAILURE'}); }
      });
    `, { eval: true, stdout: true, stderr: true }) });
    assert.equal(await provider.get(), 'preflight-1');
    assert.equal(await provider.get(), 'preflight-2');
    assert.equal(downloads, 1);
    return { jsdom: '24.1.3', worker: true, calls: 2, downloads, network: 'loopback-only', account: false };
  } finally {
    await provider?.close();
    upstream.closeAllConnections();
    await new Promise(resolve => upstream.close(resolve));
  }
});

report.complete = true;
report.passed = report.cases.length > 0 && report.cases.every(value => value.passed) &&
  (!selected || report.cases.length === 1 && report.cases[0].name === selected);
writeFileSync(join(directory, 'node-report.json'), JSON.stringify(report, null, 2));
console.log('IOS_NODE_PREFLIGHT', JSON.stringify(report));
process.exitCode = report.passed ? 0 : 1;
