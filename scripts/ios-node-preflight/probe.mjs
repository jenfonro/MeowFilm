import assert from 'node:assert/strict';
import { createHash, createCipheriv, createDecipheriv, randomBytes, webcrypto } from 'node:crypto';
import { mkdirSync, writeFileSync, readFileSync, openSync, fsyncSync, closeSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { Worker } from 'node:worker_threads';
import { once } from 'node:events';
import http from 'node:http';
import { isUtf8 } from 'node:buffer';

const directory = process.argv[2];
const report = {
  node: process.version, platform: process.platform, arch: process.arch,
  v8: process.versions.v8, execArgv: process.execArgv,
  icu: process.versions.icu ?? null,
  webAssembly: typeof globalThis.WebAssembly, cases: [],
};
async function check(name, run) {
  const started = performance.now();
  try { report.cases.push({ name, passed: true, detail: await run() ?? null, ms: performance.now() - started }); }
  catch (error) {
    report.cases.push({ name, passed: false, error: {
      name: error.name, code: error.code ?? null, message: error.message,
    } });
  }
  writeFileSync(join(directory, 'node-report.json'), JSON.stringify(report, null, 2));
}

await check('ios-asmjs-payload', async () => {
  assert.equal(process.platform, 'ios');
  assert.equal(typeof WebAssembly, 'undefined');
  const manifest = JSON.parse(readFileSync(new URL('./runtime-manifest.json', import.meta.url), 'utf8'));
  assert.equal(manifest.assemblyProfile, 'ios-embedded');
  const backend = manifest.dependencies.find(value => value.name === '@jitl/quickjs-asmjs-mjs-release-sync');
  assert.equal(backend?.version, '0.32.0');
  assert.equal(manifest.dependencies.some(value => value.name === '@jitl/quickjs-wasmfile-release-sync'), false);
  assert.equal(manifest.files.some(value => value.path.endsWith('.wasm')), false);
  return { backend: backend.name, version: backend.version };
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

await check('native-descriptor-public-bridge', async () => {
  const { createDescriptorPeer } = await import('@meowfilm/runtime-bridge/descriptor');
  const { createAppRuntime } = await import('./app/bootstrap.mjs');
  const runtime = createAppRuntime({
    product: 'ios-preflight',
    modules: [{ id: 'probe', version: '0.1.0', methods: { 'probe.echo': value => value }, close() {} }],
    connect: options => createDescriptorPeer(Number(process.argv[3]), {
      ...options, session: 'ios-preflight',
    }),
  });
  try {
    const value = { text: 'iOS→Node→iOS' };
    assert.deepEqual(await runtime.peer.call('native.echo', value), value);
  } finally { await runtime.close(); }
});

await check('public-modules-import', async () => {
  const { createMusicScriptModule } = await import('./app/modules/music-script.mjs');
  const { createCatScriptModule } = await import('./app/modules/cat-script.mjs');
  assert.equal(typeof createMusicScriptModule, 'function');
  assert.equal(typeof createCatScriptModule, 'function');
});

await check('quickjs-engine-boundaries', async () => {
  const { QuickJSEngine } = await import('./node_modules/@meowfilm/script-host/src/quickjs-engine.js');
  const { SCRIPT_LIMITS } = await import('@meowfilm/script-host');
  const source = `({
    errorCode() { return 'SCRIPT_EXCEPTION'; },
    watchStartup() {},
    inspect() { return JSON.stringify({
      integer: String(BigInt.asUintN(128, -1n)),
      host: [typeof process, typeof require],
      mark: globalThis.mark || null, job: globalThis.job || null,
      escaped: (() => { try { return !!Function('return process')(); } catch { return false; } })()
    }); },
    bytes(input, frame) {
      const reply = __meowBridge('bytes', input, frame);
      return [JSON.stringify(JSON.parse(reply[0])), reply[1]];
    }
  })`;
  const engines = [];
  try {
    for (let index = 0; index < 2; index++) engines.push(await QuickJSEngine.create({
      source, limits: SCRIPT_LIMITS,
      bridge(operation, value, frames, output) {
        assert.equal(operation, 'bytes');
        assert.deepEqual(value, { text: '字节' });
        assert.deepEqual(frames[0], Buffer.from([1, 2, 3, 4]));
        output.push(Buffer.from(frames[0]));
        return { text: value.text };
      },
    }));
    const [first, second] = engines;
    first.evaluate(`globalThis.mark='first'; globalThis.retained=new Uint8Array(20*1024*1024);
      retained[retained.length-1]=7; Promise.resolve().then(()=>{globalThis.job=42;});`);
    const inspected = first.callJSON('inspect', []);
    assert.deepEqual(inspected, {
      integer: String((1n << 128n) - 1n), host: ['undefined', 'undefined'],
      mark: 'first', job: 42, escaped: false,
    });
    assert.equal(second.callJSON('inspect', []).mark, null);
    const frame = Buffer.from([1, 2, 3, 4]);
    const reply = first.callJSONFrames('bytes', [JSON.stringify({ text: '字节' })], [frame]);
    assert.deepEqual(reply.value, { ok: true, value: { text: '字节' } });
    assert.deepEqual(reply.frames, [Buffer.from([1, 2, 3, 4])]);
    assert.deepEqual(frame, Buffer.alloc(4));
    assert.ok(first.memoryUsage().heapUsed >= 20 * 1024 * 1024);
  } finally { for (const engine of engines) engine.close(); }
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
        let server;
        module.exports = {
          start() {
            server = http.createServer((request, response) => {
              response.setHeader('content-type', 'application/json');
              const path = new URL(request.url, 'http://127.0.0.1:9978').pathname;
              if (path === '/config' || path === '/full-config') {
                response.end(JSON.stringify({ video: { sites: [{
                  key: 'ios', name: 'iOS fixture', actions: ['home'],
                  api: 'http://127.0.0.1:9978/api/ios'
                }] } }));
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
    await runtime.setEnabled('ios-cat', false);
  } finally { await runtime.close(); }
});

report.complete = true;
report.passed = report.cases.every(value => value.passed);
writeFileSync(join(directory, 'node-report.json'), JSON.stringify(report, null, 2));
console.log('IOS_NODE_PREFLIGHT', JSON.stringify(report));
process.exitCode = report.passed ? 0 : 1;
