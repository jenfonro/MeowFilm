import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createHash, createSecretKey, webcrypto, X509Certificate } from 'node:crypto';
import { inflateSync } from 'node:zlib';
import { rootCertificates } from 'node:tls';
import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import { existsSync, linkSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync,
  writeFileSync } from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { networkInterfaces, tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import v8 from 'node:v8';
import { runInNewContext } from 'node:vm';
import { createCatRuntime, createNodeNetwork, createScriptHost, SourceTransfers, SCRIPT_LIMITS } from '../src/index.js';
import { ScriptHost } from '../src/host.js';
import { CatSession } from '../src/cat-session.js';
import { syncCatManagement } from '../src/cat-management.js';
import { cleanHopByHop } from '../src/cat-runtime.js';
import { normalizeCatOutput } from '../src/cat-output.js';
import { fault } from '../src/errors.js';
import { NodeCapabilities } from '../src/node-capabilities.js';
import { PrivateVolume } from '../src/private-volume.js';
import { ResourceScope } from '../src/scope.js';
import { ScriptServices, scriptAccessHeader } from '../src/script-services.js';
import { projectHttpRequestOptions, projectFields } from '../src/native-roles.js';
import { parse, tokenizer, parseExpressionAt } from 'acorn';
import { createLocalStartupLookup, startupWrapperTarget } from '../src/cat-source-routes.js';
import { readFile } from 'node:fs/promises';
import { serverFor } from './http-fixture.js';
import { lxScript } from './fixtures.js';

v8.setFlagsFromString('--expose_gc');
const forceGc = runInNewContext('gc');
async function collectGarbage(rounds = 4) {
  for (let round = 0; round < rounds; round++) {
    forceGc();
    await new Promise(resolve => setImmediate(resolve));
    forceGc();
    await new Promise(resolve => setImmediate(resolve));
  }
}

const source = String.raw`
const http = require('http');
const https = require('https');
const fs = require('fs');
const os = require('os');
const zlib = require('zlib');
const { PassThrough } = require('stream');
const streams = require('stream/web');
const { Blob, File } = require('buffer');
const { TextEncoder, TextDecoder } = require('util');
const { StringDecoder } = require('string_decoder');
let server, options;
const cachedNativeURL = new URL('https://example.invalid/cached');
let cachedNativeString;
let parallelActive = 0, parallelPeak = 0;
const requestedPort = 9988;
const json = (response, value, status = 200) => {
  response.statusCode = status;
  response.setHeader('content-type', 'application/json; charset=utf-8');
  response.end(JSON.stringify(value));
};
const readBody = request => new Promise((resolve, reject) => {
  const chunks = [];
  request.on('data', chunk => chunks.push(Buffer.from(chunk)));
  request.once('end', () => {
    if (!chunks.length) { resolve({}); return; }
    try { resolve(JSON.parse(Buffer.concat(chunks).toString())); }
    catch (error) { reject(error); }
  });
  request.once('error', reject);
});
const readRaw = request => new Promise((resolve, reject) => {
  const chunks = [];
  request.on('data', chunk => chunks.push(Buffer.from(chunk)));
  request.once('end', () => resolve(Buffer.concat(chunks).toString()));
  request.once('error', reject);
});
const requestOutcome = value => new Promise(resolve => {
  let request;
  try {
    request = http.get(value, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(Buffer.from(chunk)));
      response.once('end', () => resolve({
        status: response.statusCode, body: Buffer.concat(chunks).toString(),
      }));
      response.once('error', error => resolve({ code: error.code }));
    });
  } catch (error) { resolve({ code: error.code }); return; }
  request.once('error', error => resolve({ code: error.code }));
});
const selfHeaders = () => new Promise((resolve, reject) => {
  const request = http.get('http://127.0.0.1:' + requestedPort + '/headers', {
    headers: {
      HOST: 'attacker.invalid',
      'X-Meow-Script-Access': 'attacker',
      'X-Meow-Request-Scope': 'attacker',
    },
  }, response => {
    const chunks = [];
    response.on('data', chunk => chunks.push(Buffer.from(chunk)));
    response.once('end', () => {
      try { resolve(JSON.parse(Buffer.concat(chunks).toString())); }
      catch (error) { reject(error); }
    });
    response.once('error', reject);
  });
  request.once('error', reject);
});
const external = (value, payload) => new Promise((resolve, reject) => {
  const body = Buffer.from(JSON.stringify(payload));
  let request;
  try {
    request = https.request(value, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'content-length': String(body.length) },
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(Buffer.from(chunk)));
      response.once('end', () => resolve({
        status: response.statusCode,
        body: Buffer.concat(chunks).toString(),
        shape: {
          rawHeaders: Array.isArray(response.rawHeaders),
          trailers: response.trailers && typeof response.trailers === 'object',
          rawTrailers: Array.isArray(response.rawTrailers),
          httpVersion: response.httpVersion,
          major: response.httpVersionMajor,
          minor: response.httpVersionMinor,
          complete: response.complete,
        },
      }));
      response.once('error', reject);
    });
  } catch (error) { reject(error); return; }
  request.once('error', reject);
  request.end(body);
});
function credentialPath() { return '/credential-' + options.id + '.json'; }
function credential() {
  try { return JSON.parse(fs.readFileSync(credentialPath(), 'utf8')); }
  catch { return null; }
}
async function route(request, response) {
  const url = new URL(request.url, 'http://127.0.0.1:' + requestedPort);
  if (url.pathname === '/full-config' || url.pathname === '/config') {
    json(response, { video: { sites: [{
      key: options.id,
      name: 'Fixture ' + options.id,
      api: 'http://127.0.0.1:' + requestedPort + '/api/' + options.id,
      actions: ['home', 'search', 'detail', 'play', 'probe', 'scopedFile', 'cachedNative',
        'timerBurst', 'parallel', 'pipeLifecycle', 'slow', 'pan', 'panOpaque', 'guestMeow',
        'externalPlay'],
    }] } });
    return;
  }
  if (url.pathname === '/website') {
    response.setHeader('content-type', 'text/html; charset=utf-8');
    response.end('<!doctype html><title>' + options.id + '</title>');
    return;
  }
  if (url.pathname === '/headers') {
    json(response, {
      hostCorrect: request.headers.host === '127.0.0.1:' + server.address().port,
      access: request.headers['x-meow-script-access'] || null,
      scope: request.headers['x-meow-request-scope'] || null,
    });
    return;
  }
  if (url.pathname === '/multipart') {
    json(response, {
      contentType: String(request.headers['content-type'] || ''),
      body: await readRaw(request),
    });
    return;
  }
  if (url.pathname === '/website/api/credentials') {
    if (options.protocol !== 'new') { json(response, { success: false }, 404); return; }
    json(response, { code: 0, data: { quark: { cookie: credential()?.cookie || '' } } });
    return;
  }
  if (url.pathname === '/website/pans/list') {
    if (options.protocol !== 'old') { json(response, { success: false }, 404); return; }
    json(response, { success: true, data: [{ key: 'quark' }] });
    return;
  }
  const oldCookie = url.pathname === '/website/quark/cookie' && options.protocol === 'old';
  const newCookie = url.pathname === '/website/api/credential/quark/cookie' && options.protocol === 'new';
  if (oldCookie || newCookie) {
    if (request.method === 'GET') {
      json(response, oldCookie
        ? { success: true, data: credential()?.cookie || '' }
        : { code: 0, data: { value: credential()?.cookie || '' } });
      return;
    }
    const body = await readBody(request);
    fs.writeFileSync(credentialPath(), JSON.stringify({
      cookie: oldCookie ? body.cookie : body.value,
    }));
    json(response, oldCookie ? { success: true } : { code: 0 });
    return;
  }
  if (url.pathname === '/proxy/media.m3u8') {
    if (request.headers['x-remove']) { json(response, { error: 'hop header leaked' }, 400); return; }
    const bytes = Buffer.from('#EXTM3U\n  #EXT-X-VERSION:3\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1920x1080,FRAME-RATE=25\nvideo.bin\n');
    const match = /^bytes=(\d+)-(\d*)$/.exec(String(request.headers.range || ''));
    if (match) {
      const start = Number(match[1]), end = match[2] ? Number(match[2]) : bytes.length - 1;
      const part = bytes.subarray(start, Math.min(end + 1, bytes.length));
      response.writeHead(206, {
        'content-type': 'application/vnd.apple.mpegurl',
        'content-length': String(part.length),
        'content-range': 'bytes ' + start + '-' + (start + part.length - 1) + '/' + bytes.length,
        connection: 'x-hop', 'x-hop': 'remove-me',
      });
      response.end(part);
      return;
    }
    response.writeHead(200, {
      'content-type': 'application/vnd.apple.mpegurl',
      'content-length': String(bytes.length),
      connection: 'x-hop', 'x-hop': 'remove-me',
    });
    response.end(bytes);
    return;
  }
  if (url.pathname === '/proxy/manifest.mpd') {
    response.setHeader('content-type', 'application/dash+xml');
    response.end('<MPD><Period><BaseURL>video.bin</BaseURL></Period></MPD>');
    return;
  }
  if (url.pathname === '/proxy/unmapped.m3u8') {
    response.setHeader('content-type', 'application/vnd.apple.mpegurl');
    response.end('#EXTM3U\nhttp://127.0.0.1:65534/unmapped.ts\n');
    return;
  }
  if (url.pathname === '/proxy/redir') {
    response.writeHead(302, { location: 'http://127.0.0.1:' + requestedPort + '/proxy/video.bin' });
    response.end();
    return;
  }
  if (url.pathname === '/proxy/video.bin') {
    if (request.headers['x-meow-request-scope']) {
      json(response, { error: 'scope header leaked' }, 400);
      return;
    }
    const bytes = Buffer.from('0123456789');
    response.setHeader('x-upstream-cookie', String(request.headers.cookie || ''));
    response.setHeader('set-cookie', [
      'media_session=kept; Path=/',
      '__meow_private=blocked; HttpOnly; Path=/',
    ]);
    const match = /^bytes=(\d+)-(\d*)$/.exec(String(request.headers.range || ''));
    if (match) {
      const start = Number(match[1]), end = match[2] ? Number(match[2]) : bytes.length - 1;
      const part = bytes.subarray(start, Math.min(end + 1, bytes.length));
      response.writeHead(206, {
        'content-type': 'application/octet-stream',
        'content-length': String(part.length),
        'content-range': 'bytes ' + start + '-' + (start + part.length - 1) + '/' + bytes.length,
        'accept-ranges': 'bytes',
      });
      response.end(part);
      return;
    }
    response.writeHead(200, {
      'content-type': 'application/octet-stream',
      'content-length': String(bytes.length),
      'accept-ranges': 'bytes',
    });
    response.end(bytes);
    return;
  }
  const prefix = '/api/' + options.id + '/';
  if (!url.pathname.startsWith(prefix)) { json(response, { error: 'missing' }, 404); return; }
  const action = url.pathname.slice(prefix.length);
  const input = await readBody(request);
  if (action === 'cachedNative') {
    cachedNativeString ??= cachedNativeURL.toString;
    json(response, { value: cachedNativeString.call(cachedNativeURL) });
    return;
  }
  if (action === 'scopedFile') {
    const fd = fs.openSync('/scoped-descriptor.txt', 'w');
    fs.closeSync(fd);
    const handle = await fs.promises.open('/scoped-handle.txt', 'w');
    await handle.writeFile('scoped');
    await handle.close();
    json(response, { written: true });
    return;
  }
  if (action === 'timerBurst') {
    const timers = Array.from({ length: 160 }, () => setTimeout(() => {}, 60000));
    for (const timer of timers) clearTimeout(timer);
    json(response, { count: timers.length });
    return;
  }
  if (action === 'parallel') {
    parallelActive++;
    parallelPeak = Math.max(parallelPeak, parallelActive);
    try {
      await new Promise(resolve => setTimeout(resolve, 100));
      json(response, { peak: parallelPeak });
    } finally { parallelActive--; }
    return;
  }
  if (action === 'pipeLifecycle') {
    const unpipeSource = zlib.createGzip();
    const unpipeDestination = new PassThrough({ autoDestroy: false });
    unpipeDestination.resume();
    unpipeSource.pipe(unpipeDestination);
    unpipeSource.unpipe(unpipeDestination);
    const unpipe = {
      data: unpipeSource.listenerCount('data'),
      end: unpipeSource.listenerCount('end'),
      error: unpipeSource.listenerCount('error'),
      drain: unpipeDestination.listenerCount('drain'),
      finish: unpipeDestination.listenerCount('finish'),
      close: unpipeDestination.listenerCount('close'),
    };
    unpipeSource.destroy();
    unpipeDestination.destroy();

    const finishedSource = zlib.createGzip();
    const finishedDestination = new PassThrough({ autoDestroy: false });
    finishedDestination.resume();
    await new Promise((resolve, reject) => {
      const failed = error => {
        finishedSource.off('error', failed);
        finishedDestination.off('error', failed);
        reject(error);
      };
      finishedDestination.once('finish', () => {
        finishedSource.off('error', failed);
        finishedDestination.off('error', failed);
        resolve();
      });
      finishedSource.once('error', failed);
      finishedDestination.once('error', failed);
      finishedSource.pipe(finishedDestination);
      finishedSource.end('pipe-lifecycle');
    });
    const finish = {
      data: finishedSource.listenerCount('data'),
      end: finishedSource.listenerCount('end'),
      error: finishedSource.listenerCount('error'),
      drain: finishedDestination.listenerCount('drain'),
      finish: finishedDestination.listenerCount('finish'),
      close: finishedDestination.listenerCount('close'),
    };
    finishedSource.destroy();
    finishedDestination.destroy();

    const closedSource = zlib.createGzip();
    const closedDestination = new PassThrough({ autoDestroy: false });
    closedDestination.resume();
    const premature = await new Promise((resolve, reject) => {
      closedDestination.once('error', error => {
        if (error.code !== 'ERR_STREAM_PREMATURE_CLOSE') reject(error);
      });
      closedDestination.once('close', () => resolve({
        destroyed: closedDestination.destroyed,
        data: closedSource.listenerCount('data'),
        end: closedSource.listenerCount('end'),
        error: closedSource.listenerCount('error'),
        sourceClose: closedSource.listenerCount('close'),
        drain: closedDestination.listenerCount('drain'),
        finish: closedDestination.listenerCount('finish'),
        close: closedDestination.listenerCount('close'),
      }));
      closedSource.pipe(closedDestination);
      closedSource.destroy();
    });
    json(response, { unpipe, finish, premature });
    return;
  }
  if (action === 'probe') {
    const privateUrl = input?.privateUrl ?? 'http://127.0.0.1:65534/private';
    const loopbackUrl = input?.loopbackUrl ?? 'http://127.0.0.1:65534/private';
    let count = 0;
    try { count = Number(fs.readFileSync('/state.json', 'utf8')); } catch {}
    fs.writeFileSync('/state.json', String(++count));
    const exists = await new Promise(resolve => fs.exists('/state.json', resolve));
    const nativeRealpath = await new Promise((resolve, reject) =>
      fs.realpath.native('/state.json', (error, value) => error ? reject(error) : resolve(value)));
    let hostFile, dependency;
    try { fs.readFileSync('C:\\Windows\\win.ini'); hostFile = 'UNEXPECTED'; }
    catch (error) { hostFile = error.code; }
    try { require('child_process'); dependency = 'UNEXPECTED'; }
    catch (error) { dependency = error.code; }
    const usage = process.memoryUsage();
    const interfaces = os.networkInterfaces();
    const byteStrategy = new streams.ByteLengthQueuingStrategy({ highWaterMark: 8 });
    const countStrategy = new streams.CountQueuingStrategy({ highWaterMark: 4 });
    const httpsServer = new https.Server();
    const agent = new https.Agent();
    const form = new FormData();
    form.append('text', 'value');
    form.append('file', new File(['hello'], 'sample.txt', { type: 'text/plain' }));
    const multipart = await (await fetch('http://127.0.0.1:' + requestedPort + '/multipart', {
      method: 'POST', body: form,
    })).json();
    httpsServer.close();
    agent.destroy();
    json(response, {
      count,
      hostFile,
      dependency,
      privateHost: await requestOutcome(privateUrl),
      unmappedLoopback: await requestOutcome(loopbackUrl),
      hostEnvironment: process.env.USERPROFILE || process.env.HOME || null,
      guestGlobals: { lx: typeof lx, meow: typeof meow, bridge: typeof __meowBridge },
      fsCompatibility: {
        exists,
        nativeRealpath,
        nativeAlias: fs.realpath.native === fs.realpath,
      },
      selfHeaders: await selfHeaders(),
      configValue: options.jsonValue || null,
      cookie: credential()?.cookie || null,
      multipart: {
        contentType: multipart.contentType.startsWith('multipart/form-data; boundary=----meowfilm-'),
        text: /name="text"\r\n\r\nvalue\r\n/.test(multipart.body),
        file: /name="file"; filename="sample.txt"\r\nContent-Type: text\/plain\r\n\r\nhello\r\n/.test(multipart.body),
      },
      responseStream: await new Response(new Blob(['stream-body']).stream()).text(),
      deletedSetCookie: (() => {
        const headers = new Headers({ 'set-cookie': 'one=1' });
        headers.delete('set-cookie');
        return !headers.has('set-cookie') && headers.getSetCookie().length === 0;
      })(),
      virtual: {
        dartPort: catDartServerPort(),
        os: {
          platform: os.platform(), arch: os.arch(), hostname: os.hostname(),
          homedir: os.homedir(), tmpdir: os.tmpdir(), cpus: os.cpus().length,
          loopback: interfaces.lo?.[0]?.address, totalmem: os.totalmem(),
          freemem: os.freemem(),
        },
        process: {
          stdin: process.stdin.fd, stdout: process.stdout.fd, stderr: process.stderr.fd,
          title: process.title, rawDebug: typeof process._rawDebug, usage,
        },
        constructors: {
          url: new URL('https://example.invalid/') instanceof URL,
          search: new URLSearchParams('a=1') instanceof URLSearchParams,
          encoder: new TextEncoder() instanceof TextEncoder,
          decoder: new TextDecoder() instanceof TextDecoder,
          stringDecoder: new StringDecoder() instanceof StringDecoder,
          blob: new Blob(['x']) instanceof Blob,
          file: new File(['x'], 'x.txt') instanceof File,
          readable: new streams.ReadableStream() instanceof streams.ReadableStream,
          writable: new streams.WritableStream() instanceof streams.WritableStream,
          transform: new streams.TransformStream() instanceof streams.TransformStream,
          compression: new streams.CompressionStream('gzip') instanceof streams.CompressionStream,
          decompression: new streams.DecompressionStream('gzip') instanceof streams.DecompressionStream,
          encoderStream: new streams.TextEncoderStream() instanceof streams.TextEncoderStream,
          decoderStream: new streams.TextDecoderStream() instanceof streams.TextDecoderStream,
          globalReadable: new ReadableStream() instanceof ReadableStream,
          globalWritable: new WritableStream() instanceof WritableStream,
          globalTransform: new TransformStream() instanceof TransformStream,
          globalCompression: new CompressionStream('gzip') instanceof CompressionStream,
          globalDecompression: new DecompressionStream('gzip') instanceof DecompressionStream,
          form: (() => {
            const value = new FormData();
            value.append('one', '1');
            value.set('one', '2');
            return value instanceof FormData && value.get('one') === '2' &&
              [...value].length === 1;
          })(),
          fileIsBlob: new File(['x'], 'x.txt') instanceof Blob,
          byteStrategy: byteStrategy instanceof streams.ByteLengthQueuingStrategy,
          countStrategy: countStrategy instanceof streams.CountQueuingStrategy,
          byteSize: byteStrategy.size(Buffer.from('123')),
          countSize: countStrategy.size('x'),
          httpServer: (() => {
            const server = new http.Server();
            server.close();
            return server instanceof http.Server;
          })(),
          httpsServer: httpsServer instanceof https.Server,
          agent: agent instanceof https.Agent,
        },
      },
    });
    return;
  }
  if (action === 'slow') {
    await new Promise(resolve => setTimeout(resolve, 10000));
    json(response, { done: true });
    return;
  }
  if (action === 'home') {
    if (input?.resultField) {
      json(response, { result: { value: 'nested-result' }, marker: 'outer-result' });
      return;
    }
    json(response, { class: [{ type_id: 'fixture', type_name: 'Fixture' }], filters: {} });
    return;
  }
  if (action === 'search') {
    json(response, {
      page: 1, pagecount: 1, limit: 20, total: 1,
      list: [{ vod_id: options.id + '-1', vod_name: String(input.wd || 'fixture'),
        vod_pic: 'http://127.0.0.1:' + requestedPort + '/proxy/video.bin',
        video_width: 1920, video_height: 1080, fps: '30000/1001' }],
    });
    return;
  }
  if (action === 'detail') {
    json(response, { list: [{ vod_id: options.id + '-1', vod_name: 'Fixture',
      vod_play_from: 'Local', vod_play_url: 'Episode$http://127.0.0.1:' +
        requestedPort + '/proxy/media.m3u8' }] });
    return;
  }
  if (action === 'play') {
    const path = input?.id === 'redirect' ? '/proxy/redir'
      : input?.id === 'dash' ? '/proxy/manifest.mpd'
        : input?.id === 'unmapped' ? '/proxy/unmapped.m3u8' : '/proxy/media.m3u8';
    json(response, { parse: 0, url: 'http://127.0.0.1:' + requestedPort + path,
      header: { Referer: 'https://fixture.invalid/' } });
    return;
  }
  if (action === 'pan') {
    const upstream = await external('https://pan.quark.cn/1/clouddrive/share/sharepage/token',
      { pwd_id: 'abcd1234', passcode: '' });
    json(response, {
      upstream: JSON.parse(upstream.body),
      upstreamShape: upstream.shape,
      list: [{ vod_id: 'pan-1', vod_name: 'Pan',
        vod_play_from: 'QuarkAlias',
        vod_play_url: 'Episode$https://pan.quark.cn/s/abcd1234' }],
    });
    return;
  }
  if (action === 'panOpaque') {
    const upstream = await external('https://pan.quark.cn/1/clouddrive/share/sharepage/token',
      { pwd_id: 'x', passcode: '' });
    json(response, { upstream: JSON.parse(upstream.body) });
    return;
  }
  if (action === 'guestMeow') {
    json(response, {
      _meow: { guest: true },
      url: 'http://127.0.0.1:' + requestedPort + '/proxy/video.bin',
      list: [{ vod_id: 'guest-1', vod_name: 'Guest',
        vod_play_from: 'QuarkAlias',
        vod_play_url: 'Episode$https://pan.quark.cn/s/abcd1234' }],
    });
    return;
  }
  if (action === 'externalPlay') {
    json(response, { parse: 0, url: 'https://media.example.invalid/master.m3u8' });
    return;
  }
  json(response, { error: 'unsupported' }, 404);
}
function start(config) {
  options = config;
  server = http.createServer((request, response) => {
    Promise.resolve(route(request, response)).catch(error =>
      json(response, { error: { code: error.code || 'SCRIPT_EXCEPTION' } }, 500));
  });
  server.listen(requestedPort, '0.0.0.0');
}
function stop() {
  if (!server) return;
  server.closeAllConnections();
  server.close();
}
module.exports = { start, stop };
`;

function rootFor(t) {
  const root = mkdtempSync(join(tmpdir(), 'meowfilm-cat-runtime-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return root;
}

let sharedQuickJSModule;
/**
 * These tests exercise ownership of live native objects and guest execution
 * state. They own a real CatSession in this thread; the public CatRuntime
 * facade is reserved for public gateway and persistence tests.
 */
async function createCatSessionForInternalTest(t, {
  dataRoot = rootFor(t), installationId, source, config = {}, version = '1',
  settings, onNetwork, onActivate, onStop, network, onDiagnostic,
} = {}) {
  sharedQuickJSModule ??= readFile(
    new URL(import.meta.resolve('@jitl/quickjs-wasmfile-release-sync/wasm')))
    .then(bytes => WebAssembly.compile(bytes));
  const session = new CatSession({
    dataRoot,
    installationId,
    generation: 1,
    revision: {
      source, version, config,
      metadata: {}, protocolOptions: {}, permissions: { storage: [] },
    },
    settings: settings ?? { value: { providers: {}, mappings: [] } },
    onNetwork,
    onActivate,
    onStop,
    network,
    onDiagnostic,
    limits: {
      ...SCRIPT_LIMITS,
      initTimeoutMs: 60_000,
      requestTimeoutMs: 60_000,
      cleanupTimeoutMs: 3_000,
    },
    wasmModule: await sharedQuickJSModule,
  });
  t.after(async () => {
    await session.stop().catch(() => {});
  });
  try {
    await session.start();
    await session.activate();
  } catch (error) {
    await session.stop().catch(() => {});
    throw error;
  }
  return session;
}

function delayedSource(name, delay) {
  return String.raw`
    const http = require('http');
    let server;
    module.exports.start = () => new Promise(resolve => {
      setTimeout(() => {
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config') {
            response.end(JSON.stringify({ video: { sites: [{
              key: 'site-${name}', name: 'Delayed ${name}', api: '/api/site-${name}',
              actions: ['home'],
            }] } }));
            return;
          }
          response.end(JSON.stringify({ instance: '${name}' }));
        });
        server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
      }, ${delay});
    });
    module.exports.stop = () => server?.close();
  `;
}

function request(url, { method = 'GET', headers = {}, body } = {}) {
  return new Promise((resolve, reject) => {
    const raw = body === undefined ? null : Buffer.isBuffer(body) ? body : Buffer.from(body);
    const client = http.request(url, {
      method,
      headers: { ...headers, ...(raw ? { 'content-length': String(raw.length) } : {}) },
      agent: false,
    }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(Buffer.from(chunk)));
      response.once('error', reject);
      response.once('aborted', () => reject(Object.assign(new Error('Response aborted'), {
        code: 'ECONNRESET',
      })));
      response.once('end', () => resolve({
        status: response.statusCode,
        headers: response.headers,
        bytes: Buffer.concat(chunks),
        text: Buffer.concat(chunks).toString(),
      }));
    });
    client.once('error', reject);
    client.end(raw);
  });
}

function writeSettings(path, change) {
  const current = JSON.parse(readFileSync(path, 'utf8'));
  writeFileSync(path, JSON.stringify(change(current), null, 2) + '\n');
}

async function waitForState(runtime, installationId, expected, timeoutMs = 10000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const state = (await runtime.status()).instances.find(instance =>
      instance.installationId === installationId)?.state;
    if (state === expected) return;
    if (Date.now() >= deadline) throw new Error(
      `Timed out waiting for ${installationId}: ${state} instead of ${expected}`);
    await new Promise(resolve => setTimeout(resolve, 20));
  }
}

async function installed(t, root, runtimeOptions = {}) {
  const runtime = createCatRuntime({ dataRoot: root, ...runtimeOptions });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'fixture-a',
    source,
    config: { id: 'site-a', protocol: 'old' },
    version: '1',
    enabled: true,
  });
  return runtime;
}

function deterministicBytes(size, seed = 0x6d2b79f5) {
  const value = Buffer.allocUnsafe(size);
  let state = seed >>> 0;
  for (let index = 0; index < size; index++) {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    value[index] = state >>> 24;
  }
  return value;
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function networkForLocalServers(servers) {
  const aliases = new Map(servers.map(server => [server.origin, server.socketOrigin]));
  const sockets = new Map(servers.map(server => [server.socketOrigin, server.origin]));
  const replaceOrigin = (input, replacements) => {
    const url = new URL(input), replacement = replacements.get(url.origin);
    return replacement === undefined
      ? url.href
      : replacement + url.pathname + url.search + url.hash;
  };
  const network = createNodeNetwork();
  return input => network({
    ...input,
    validateURL(next) {
      const validated = input.validateURL(replaceOrigin(next, sockets));
      return replaceOrigin(validated, aliases);
    },
  });
}

function streamBridgeSource(upstreamOrigin, valuesDigest = '') {
  return String.raw`
    const http = require('http');
    const fs = require('fs');
    const zlib = require('zlib');
    const crypto = require('crypto');
    const upstream = ${JSON.stringify(upstreamOrigin)};
    const valuesDigest = ${JSON.stringify(valuesDigest)};
    let server;
    let retainedShared = null, retainedDigest = null;
    const json = (response, value, status = 200) => {
      if (response.writableEnded || response.destroyed) return;
      response.statusCode = status;
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify(value));
    };
    const readBody = request => new Promise((resolve, reject) => {
      const chunks = [];
      request.on('data', chunk => chunks.push(Buffer.from(chunk)));
      request.once('end', () => resolve(Buffer.concat(chunks)));
      request.once('error', reject);
    });
    const failed = (response, error) =>
      json(response, { error: error.code || 'SCRIPT_EXCEPTION' }, 500);
    async function route(request, response) {
      const url = new URL(request.url, 'http://127.0.0.1');
      if (url.pathname === '/config' || url.pathname === '/full-config') {
        json(response, { video: { sites: [{
          key: 'bridge', name: 'Byte bridge', api: '/bridge',
          actions: ['download', 'proxy', 'dataProxy', 'dataFile', 'observed',
            'slowProxy', 'endStream', 'largeResult', 'chunkedResponse', 'body', 'ok',
            'bytesCallback', 'bytesCallArguments',
            'bytesReturns', 'sharedCapture', 'sharedCheck', 'sharedMutate',
            'arrayCapture', 'arrayMutate', 'largeRead'],
        }] } });
        return;
      }
      const prefix = '/bridge/';
      if (!url.pathname.startsWith(prefix)) {
        response.statusCode = 404;
        response.end();
        return;
      }
      const action = url.pathname.slice(prefix.length);
      let size = Number(url.searchParams.get('size'));
      const values = Buffer.from(Array.from({ length: 256 }, (_, index) => index));
      if (action === 'ok') {
        json(response, { ok: true });
        return;
      }
      if (action === 'endStream') {
        const bytes = fs.readFileSync('/end-' + size + '.bin');
        response.writeHead(200, {
          'content-type': 'application/octet-stream',
          'content-length': String(bytes.length),
        });
        response.end(bytes);
        return;
      }
      if (action === 'largeResult') {
        json(response, { data: 'x'.repeat(12 * 1024 * 1024) });
        return;
      }
      if (action === 'chunkedResponse') {
        const input = JSON.parse((await readBody(request)).toString() || '{}');
        const total = Number(input.size);
        response.writeHead(200, {
          'content-type': 'application/octet-stream',
          'content-length': String(total),
        });
        const chunk = Buffer.alloc(64 * 1024, 0x5a);
        for (let offset = 0; offset < total; offset += chunk.length)
          response.write(chunk.subarray(0, Math.min(chunk.length, total - offset)));
        response.end();
        return;
      }
      if (action === 'body') {
        const raw = await readBody(request);
        let data = null;
        try { data = JSON.parse(raw.toString()).data; } catch {}
        json(response, { length: raw.length, dataLength: data?.length ?? null });
        return;
      }
      if (action === 'download') {
        const path = '/download-' + size + '.bin';
        const output = fs.createWriteStream(path);
        const finish = error => {
          if (error) { failed(response, error); return; }
          json(response, { size, length: fs.statSync(path).size });
        };
        output.once('finish', () => finish());
        output.once('error', finish);
        http.get(upstream + '/blob?size=' + size, incoming => {
          incoming.once('error', error => output.destroy(error));
          incoming.pipe(output);
        }).once('error', finish);
        return;
      }
      if (action === 'proxy') {
        if (!Number.isSafeInteger(size) || size <= 0) {
          const input = JSON.parse((await readBody(request)).toString() || '{}');
          size = Number(input.size);
        }
        http.get(upstream + '/blob?size=' + size, incoming => {
          response.writeHead(200, {
            'content-type': 'application/octet-stream',
            'content-length': String(size),
          });
          incoming.once('error', error => response.destroy(error));
          incoming.pipe(response);
        }).once('error', error => {
          if (response.headersSent) response.destroy(error); else failed(response, error);
        });
        return;
      }
      if (action === 'dataProxy') {
        http.get(upstream + '/blob?size=' + size, incoming => {
          response.writeHead(200, {
            'content-type': 'application/octet-stream',
            'content-length': String(size),
          });
          incoming.on('data', chunk => {
            if (!response.write(chunk)) incoming.pause();
          });
          response.on('drain', () => incoming.resume());
          incoming.once('end', () => response.end());
          incoming.once('error', error => response.destroy(error));
        }).once('error', error => {
          if (response.headersSent) response.destroy(error); else failed(response, error);
        });
        return;
      }
      if (action === 'dataFile') {
        const path = '/data-' + size + '.bin';
        const output = fs.createWriteStream(path);
        let bytes = 0;
        http.get(upstream + '/blob?size=' + size, incoming => {
          incoming.on('data', chunk => {
            bytes += chunk.length;
            if (!output.write(chunk)) incoming.pause();
          });
          output.on('drain', () => incoming.resume());
          incoming.once('end', () => output.end());
          incoming.once('error', error => output.destroy(error));
        }).once('error', error => output.destroy(error));
        output.once('finish', () =>
          json(response, { size, length: fs.statSync(path).size, bytes }));
        output.once('error', error => failed(response, error));
        return;
      }
      if (action === 'observed') {
        const path = '/observed.bin';
        const output = fs.createWriteStream(path);
        let observed = 0;
        http.get(upstream + '/blob?size=' + size, incoming => {
          incoming.on('data', chunk => { observed += chunk.length; });
          incoming.once('error', error => output.destroy(error));
          incoming.pipe(output);
        }).once('error', error => output.destroy(error));
        output.once('finish', () =>
          json(response, { size, length: fs.statSync(path).size, observed }));
        output.once('error', error => failed(response, error));
        return;
      }
      if (action === 'slowProxy') {
        response.writeHead(200, { 'content-type': 'application/octet-stream' });
        response.flushHeaders();
        let input;
        const download = http.get(upstream + '/slow?size=' + size, incoming => {
          input = incoming;
          incoming.once('error', error => response.destroy(error));
          incoming.pipe(response);
        });
        download.once('error', error => {
          if (response.headersSent) response.destroy(error); else failed(response, error);
        });
        response.once('close', () => {
          if (!response.writableFinished) {
            input?.destroy();
            download.destroy();
          }
        });
        return;
      }
      if (action === 'bytesCallback') {
        let chunks = [], empty = null, subview = null, buffers = true;
        const received = await new Promise((resolve, reject) => {
          http.get(upstream + '/values', incoming => {
            incoming.on('data', chunk => {
              if (!Buffer.isBuffer(chunk)) buffers = false;
              chunks.push(Buffer.from(chunk));
            });
            incoming.once('end', () => resolve(Buffer.concat(chunks)));
            incoming.once('error', reject);
          }).once('error', reject);
        });
        await new Promise(resolve => {
          fs.readFile('/empty.bin', (error, data) => {
            empty = error ? { code: error.code } : {
              length: data.length, buffer: Buffer.isBuffer(data),
            };
            resolve();
          });
        });
        await new Promise(resolve => {
          const fd = fs.openSync('/values.bin', 'r');
          const backing = new Uint8Array(values.length + 19);
          const view = backing.subarray(11, 11 + values.length);
          fs.read(fd, view, 0, values.length, 0, (error, length, data) => {
            try { fs.closeSync(fd); } catch {}
            subview = error ? { code: error.code } : {
              length, same: Buffer.from(data).equals(values),
            };
            resolve();
          });
        });
        json(response, {
          length: received.length, same: received.equals(values), buffers,
          empty, subview,
        });
        return;
      }
      if (action === 'bytesCallArguments') {
        const expected = values;
        const backing = new Uint8Array(expected.length + 32);
        backing.set(expected, 16);
        const view = backing.subarray(16, 16 + expected.length);
        const arrayBuffer = view.buffer.slice(view.byteOffset, view.byteOffset + view.byteLength);
        fs.writeFileSync('/call-values.bin', expected);
        fs.writeFileSync('/call-subview.bin', view);
        fs.writeFileSync('/call-empty.bin', Buffer.alloc(0));
        const compressed = zlib.deflateSync(arrayBuffer);
        fs.writeFileSync('/call-arraybuffer.bin', compressed);
        json(response, {
          viewOffset: view.byteOffset, viewLength: view.byteLength,
          arrayBuffer: arrayBuffer instanceof ArrayBuffer,
        });
        return;
      }
      if (action === 'bytesReturns') {
        const buffer = fs.readFileSync('/values.bin');
        const empty = fs.readFileSync('/empty.bin');
        const compressed = zlib.deflateSync(values);
        const inflated = zlib.inflateSync(compressed);
        const digest = await crypto.subtle.digest('SHA-256',
          buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength));
        json(response, {
          bufferHash: crypto.createHash('sha256').update(buffer).digest('hex'),
          buffer: Buffer.isBuffer(buffer), emptyLength: empty.length,
          emptyBuffer: Buffer.isBuffer(empty), inflated: inflated.equals(values),
          digestHash: Buffer.from(digest).toString('hex'),
          digestType: digest instanceof ArrayBuffer,
          digestView: ArrayBuffer.isView(digest), digestLength: digest.byteLength,
          expectedDigest: valuesDigest,
        });
        return;
      }
      if (action === 'largeRead') {
        const promised = await fs.promises.readFile('/large.bin');
        const synchronous = fs.readFileSync('/large.bin');
        const digest = value => crypto.createHash('sha256').update(value).digest('hex');
        json(response, {
          promiseLength: promised.length, promiseHash: digest(promised),
          promiseBuffer: Buffer.isBuffer(promised),
          syncLength: synchronous.length, syncHash: digest(synchronous),
          syncBuffer: Buffer.isBuffer(synchronous),
          same: promised.equals(synchronous),
        });
        return;
      }
      if (action === 'sharedCapture') {
        const chunks = [];
        await new Promise((resolve, reject) => {
          http.get(upstream + '/shared', incoming => {
            incoming.on('data', chunk => {
              chunks.push(Buffer.from(chunk));
            });
            incoming.once('end', () => {
              retainedShared = Buffer.concat(chunks);
              resolve();
            });
            incoming.once('error', reject);
          }).once('error', reject);
        });
        json(response, {
          length: retainedShared?.length ?? 0,
          hash: crypto.createHash('sha256').update(retainedShared).digest('hex'),
          buffer: Buffer.isBuffer(retainedShared),
          guestPrototype: Object.getPrototypeOf(retainedShared) === Buffer.prototype,
        });
        return;
      }
      if (action === 'sharedCheck') {
        json(response, {
          hash: retainedShared && crypto.createHash('sha256').update(retainedShared).digest('hex'),
          first: retainedShared?.[0] ?? null,
          buffer: Buffer.isBuffer(retainedShared),
        });
        return;
      }
      if (action === 'sharedMutate') {
        const first = retainedShared[0];
        retainedShared[0] = first ^ 0xff;
        json(response, { first, changed: retainedShared[0] });
        return;
      }
      if (action === 'arrayCapture') {
        retainedDigest = await crypto.subtle.digest('SHA-256', retainedShared);
        json(response, {
          hash: Buffer.from(retainedDigest).toString('hex'),
          type: retainedDigest instanceof ArrayBuffer,
          view: ArrayBuffer.isView(retainedDigest),
          guestPrototype: Object.getPrototypeOf(retainedDigest) === ArrayBuffer.prototype,
          length: retainedDigest.byteLength,
        });
        return;
      }
      if (action === 'arrayMutate') {
        const bytes = new Uint8Array(retainedDigest);
        const first = bytes[0];
        bytes[0] = first ^ 0xff;
        json(response, { first, changed: bytes[0], length: retainedDigest.byteLength });
        return;
      }
      response.statusCode = 404;
      response.end();
    }
    module.exports.start = () => new Promise(resolve => {
      server = http.createServer((request, response) => {
        route(request, response).catch(error => failed(response, error));
      });
      server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
    });
    module.exports.stop = () => {
      server?.closeAllConnections();
      server?.close();
    };
  `;
}

async function startStreamBridge(t, upstream, { files = {} } = {}) {
  const root = rootFor(t);
  const volumeRoot = join(root, 'volumes', 'bridge');
  const runtime = createCatRuntime({
    dataRoot: root, network: networkForLocalServers([upstream]),
  });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'bridge',
    source: streamBridgeSource(upstream.origin, sha256(upstream.values)),
    config: {},
    version: '1', enabled: true,
  });
  for (const [name, bytes] of Object.entries(files))
    writeFileSync(join(volumeRoot, name), bytes);
  return { runtime, root, volumeRoot, endpoint: runtime.getSession('bridge').endpoint() };
}

async function streamUpstream(t) {
  const registry = new Map();
  const reads = [];
  const values = Buffer.from(Array.from({ length: 256 }, (_, index) => index));
  const shared = Buffer.from(Array.from({ length: 256 }, (_, index) => 255 - index));
  const server = await serverFor(t, (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1');
    if (url.pathname === '/values') {
      response.end(values);
      return;
    }
    if (url.pathname === '/shared') {
      response.end(shared);
      return;
    }
    const filename = url.pathname.slice(1);
    if (filename === 'blob' || filename === 'slow') {
      const size = Number(url.searchParams.get('size'));
      const bytes = registry.get(filename + '-' + size);
      if (filename === 'slow') reads.push({ response, url: url.href });
      response.writeHead(200, {
        'content-type': 'application/octet-stream',
        'content-length': String(size),
      });
      if (filename === 'blob') { response.end(bytes); return; }
      response.write(bytes.subarray(0, 64 * 1024));
      const timer = setInterval(() => {
        if (response.destroyed || response.writableEnded) { clearInterval(timer); return; }
        response.write(Buffer.alloc(64 * 1024, 0x5a));
      }, 10);
      response.once('close', () => clearInterval(timer));
      return;
    }
    response.statusCode = 404;
    response.end();
  });
  return {
    ...server,
    values,
    shared,
    blob(size) {
      const bytes = deterministicBytes(size, 0x6d2b79f5 ^ size);
      registry.set('blob-' + size, bytes);
      return bytes;
    },
    slow(size) {
      const bytes = deterministicBytes(size, 0x5a5a5a5a ^ size);
      registry.set('slow-' + size, bytes);
      return bytes;
    },
    reads,
  };
}

function scriptRequest(endpoint, path) {
  return request(endpoint.origin + path, {
    headers: { 'x-meow-script-access': endpoint.access },
  });
}

async function waitFor(predicate, message, timeoutMs = 5000) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (predicate()) return;
    if (Date.now() >= deadline) throw new Error(message);
    await new Promise(resolve => setTimeout(resolve, 20));
  }
}

function sourceHash(value) {
  return createHash('sha256').update(value).digest('hex');
}

function volumeSource(marker, extra = '') {
  return String.raw`
    const http = require('http');
    const fs = require('fs');
    let server;
    module.exports.start = () => new Promise(resolve => {
      server = http.createServer((request, response) => {
        response.setHeader('content-type', 'application/json');
        if (request.url === '/config' || request.url === '/full-config') {
          response.end(JSON.stringify({ video: { sites: [{
            key: '${marker}', name: '${marker}', api: '/api/${marker}',
            actions: ['read', 'write'],
          }] } }));
          return;
        }
        if (request.url === '/api/${marker}/write') {
          fs.writeFileSync('/${marker}-state.txt', '${marker}');
          response.end(JSON.stringify({ written: true }));
          return;
        }
        let value = null;
        try { value = fs.readFileSync('/${marker}-state.txt', 'utf8'); } catch {}
        response.end(JSON.stringify({ value }));
      });
      server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
    });
    module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    ${extra}
  `;
}

function settingsScript(runtime, installationId) {
  return JSON.parse(readFileSync(runtime.settings.path, 'utf8')).scripts
    .find(script => script.installationId === installationId);
}

function sourceFile(dataRoot, hash) {
  return join(dataRoot, 'sources', hash + '.js');
}

test('Cat transfer staging is reclaimed by lifecycle without a concurrent token quota', () => {
  const transfers = new SourceTransfers();
  for (let index = 0; index < 12; index++)
    transfers.begin({ length: 1, sha256: '0'.repeat(64), kind: 'source' });
  assert.equal(transfers.entries.size, 12);
  transfers.close();
  assert.equal(transfers.entries.size, 0);
});

test('Cat abandoned transfer staging expires without requiring another RPC', () => {
  let now = 0, scheduled;
  const transfers = new SourceTransfers({
    clock: () => now,
    ttl: 100,
    setTimer(callback) {
      scheduled = { callback, unref() {} };
      return { callback, unref() {} };
    },
    clearTimer() {},
  });
  transfers.begin({ length: 1, sha256: '0'.repeat(64), kind: 'source' });
  assert.equal(transfers.entries.size, 1);
  now = 100;
  scheduled.callback();
  assert.equal(transfers.entries.size, 0);
  transfers.close();
});

test('Cat transfer expiry ignores a stale timer callback after rescheduling', () => {
  let now = 0;
  let scheduled;
  const transfers = new SourceTransfers({
    clock: () => now,
    ttl: 100,
    setTimer(callback) {
      scheduled = { callback, unref() {} };
      return { callback, unref() {} };
    },
    clearTimer() {},
  });
  const begun = transfers.begin({ length: 1, sha256: createHash('sha256').update('x').digest('hex'),
    kind: 'source' });
  const stale = scheduled;
  transfers.chunk({ token: begun.token, offset: 0, data: Buffer.from('x').toString('base64') });
  stale.callback();
  assert.equal(transfers.entries.size, 1);
  now = 100;
  scheduled.callback();
  assert.equal(transfers.entries.size, 0);
  transfers.close();
});

test('Cat transfer setup failure does not retain an unreachable staging entry', () => {
  let scheduled = 0;
  const transfers = new SourceTransfers({
    setTimer() { scheduled++; throw new Error('synthetic timer setup failure'); },
  });
  assert.throws(() => transfers.begin({
    length: 1, sha256: createHash('sha256').update('x').digest('hex'), kind: 'source',
  }), /synthetic timer setup failure/);
  assert.equal(transfers.entries.size, 0);
  assert.equal(scheduled, 1);
  transfers.close();
});

test('Cat source staging rejects the 16 MiB boundary before retaining chunks', () => {
  const transfers = new SourceTransfers();
  const sha256 = createHash('sha256').update('x').digest('hex');
  assert.throws(() => transfers.begin({
    length: SCRIPT_LIMITS.maxSourceBytes + 1, sha256, kind: 'source',
  }), { code: 'RESOURCE_LIMIT' });
  const exact = transfers.begin({ length: SCRIPT_LIMITS.maxSourceBytes, sha256, kind: 'source' });
  assert.equal(transfers.entries.get(exact.token).length, SCRIPT_LIMITS.maxSourceBytes);
  transfers.close();
});

test('Cat script result blobs preserve a 12 MiB payload across chunk reads', () => {
  const transfers = new SourceTransfers();
  const size = 12 * 1024 * 1024;
  const exact = { data: 'x'.repeat(size) };
  const result = transfers.result(exact);
  assert.equal(result.blob.length, Buffer.byteLength(JSON.stringify(exact)));
  assert.equal(result.blob.chunkBytes, 64 * 1024);
  const chunks = [];
  for (let offset = 0; offset < result.blob.length;) {
    const part = transfers.read({ token: result.blob.token, offset });
    chunks.push(Buffer.from(part.data, 'base64'));
    offset = part.next;
  }
  const bytes = Buffer.concat(chunks);
  assert.equal(bytes.length, result.blob.length);
  assert.equal(sha256(bytes), result.blob.sha256);
  assert.equal(bytes.toString(), JSON.stringify(exact));
  transfers.release(result.blob.token);
  transfers.close();
});

test('Cat transfer chunk rolls back when expiry rescheduling fails', () => {
  let schedules = 0;
  let now = 0;
  const transfers = new SourceTransfers({
    clock: () => now,
    ttl: 100,
    setTimer(callback) {
      schedules++;
      if (schedules === 3) throw new Error('synthetic reschedule failure');
      return { callback, unref() {} };
    },
    clearTimer() {},
  });
  const begun = transfers.begin({
    length: 1, sha256: createHash('sha256').update('x').digest('hex'), kind: 'source',
  });
  assert.throws(() => transfers.chunk({
    token: begun.token, offset: 0, data: Buffer.from('x').toString('base64'),
  }), /synthetic reschedule failure/);
  assert.equal(transfers.entries.get(begun.token).offset, 0);
  assert.equal(transfers.entries.get(begun.token).chunks.length, 0);
  assert.equal(transfers.entries.get(begun.token).expires, 100);
  transfers.close();
});

test('Cat transfer close releases every entry even when timer cleanup fails', () => {
  const cleared = [];
  let handle;
  const transfers = new SourceTransfers({
    setTimer(callback) { handle = { callback, unref() {} }; return handle; },
    clearTimer(timer) {
      cleared.push(timer);
      throw new Error('synthetic timer cleanup failure');
    },
  });
  transfers.begin({
    length: 1, sha256: createHash('sha256').update('x').digest('hex'), kind: 'source',
  });
  assert.throws(() => transfers.close(), /synthetic timer cleanup failure/);
  assert.equal(cleared.length, 1);
  assert.equal(transfers.entries.size, 0);
  handle.callback();
  assert.equal(transfers.entries.size, 0);
  assert.doesNotThrow(() => transfers.close());
  assert.equal(cleared.length, 1);
});

test('CatNode cannot access the private native task-context hook at startup or in dynamic code', async t => {
  const inspect = `({
    type: typeof __meowTaskContext,
    own: Object.hasOwn(globalThis, '__meowTaskContext'),
    dynamic: Function('return typeof __meowTaskContext')(),
    nested: require('vm').runInNewContext('typeof __meowTaskContext'),
  })`;
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'private-task-context', enabled: true, version: '1',
    source: `
      const startup = ${inspect};
      module.exports = {
        home() { return { startup, action: ${inspect} }; },
      };
    `,
    config: {},
  });
  const expected = { type: 'undefined', own: false, dynamic: 'undefined', nested: 'undefined' };
  const result = await runtime.invoke({
    installationId: 'private-task-context', siteId: 'default',
    operation: 'home', payload: {},
  });
  assert.deepEqual(result, { startup: expected, action: expected });
});

test('Cat os memory metadata reports the per-script limit and current guest heap', async t => {
  const root = new ResourceScope();
  let heapUsed = 234;
  const session = {
    installationId: 'os-memory-fixture',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100, maxMemoryBytes: 1234 },
    live: true,
    scope: () => root,
    storageWritten() {},
    engine: { memoryUsage: () => ({ heapUsed }) },
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  assert.equal(node.builtins.os.totalmem(), 1234);
  assert.equal(node.builtins.os.freemem(), 1000);
  heapUsed = 2000;
  assert.equal(node.builtins.os.freemem(), 0);
  root.close();
  session.live = false;
  await node.close();
});

test('Cat URL enhancement failure preserves the original valid result', () => {
  const input = { parse: 0, url: 'http://127.0.0.1:9988/proxy?id=fixture' };
  assert.deepEqual(normalizeCatOutput(input, {
    settings: {
      providers: { baidu: false, quark: false, uc: false, tianyi: false, yidong: false },
      mappings: [],
    },
    installationId: 'output-fixture',
    siteId: 'site',
    mapUrl() { throw new Error('synthetic route enhancement failure'); },
  }), input);
});

test('Cat imports are not rejected by any installation-count quota',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    for (let index = 0; index < 40; index++) {
      await runtime.install({
        installationId: `unlimited-${index}`, source, config: {},
        version: '1', enabled: false,
      });
    }
    assert.equal((await runtime.status()).instances.length, 40);
  });

test('Cat ready memory overrun restarts once with the same revision and keeps its private volume',
  { timeout: 30000 }, async t => {
    const dataRoot = rootFor(t);
    const diagnostics = [];
    const host = new ScriptHost({
      adapters: { cat: input => new CatSession({ ...input, dataRoot }) },
      limits: { maxMemoryBytes: 128 * 1024 * 1024 },
      onDiagnostic: event => diagnostics.push(event),
    });
    t.after(() => host.close());
    const memorySource = String.raw`
      const http = require('http');
      const fs = require('fs');
      let server;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'memory', name: 'Memory', api: '/api/memory', actions: ['home', 'allocate'],
            }] } }));
            return;
          }
          if (request.url === '/api/memory/allocate') {
            fs.writeFileSync('/generation-count', String(Number(
              fs.existsSync('/generation-count') ? fs.readFileSync('/generation-count', 'utf8') : '0') + 1));
            const chunks = [new Uint8Array(256 * 1024 * 1024)];
            response.end(JSON.stringify({ allocated: true }));
            return;
          }
          if (request.url === '/api/memory/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ count: Number(fs.existsSync('/generation-count')
              ? fs.readFileSync('/generation-count', 'utf8') : '0') }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server.closeAllConnections(); server.close(); };
    `;
    const installed = await host.install({
      installationId: 'memory-cat', protocol: 'cat', source: memorySource, config: {},
      version: '1', enabled: true,
    });
    await assert.rejects(host.invoke({
      installationId: 'memory-cat', siteId: 'memory', operation: 'allocate', payload: {},
    }), { code: 'RESOURCE_LIMIT' });
    let restarted;
    for (let attempt = 0; attempt < 500; attempt++) {
      restarted = host.get('memory-cat');
      if (restarted.generation > installed.generation && restarted.state === 'ready') break;
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    assert.equal(restarted.state, 'ready');
    assert(restarted.generation > installed.generation);
    assert.equal(restarted.contentHash, installed.contentHash);
    assert.equal(diagnostics.filter(event => event.operation === 'memory.restart').length, 1);
    assert.deepEqual(await host.invoke({
      installationId: 'memory-cat', siteId: 'memory', operation: 'home', payload: {},
    }), { count: 1 });
  });

test('Cat start returns after aggregate readiness while instances load in order',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'first', source: delayedSource('first', 50), config: {},
      version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'second', source: delayedSource('second', 500), config: {},
      version: '1', enabled: true,
    });
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: runtime.dataRoot });
    t.after(() => reopened.close());
    const started = await reopened.start();
    assert.equal(started.settingsPath, join(reopened.dataRoot, 'settings.json'));
    assert.equal(started.aggregate.origin, `http://127.0.0.1:${reopened.server.address().port}`);
    assert.ok(reopened.server.listening);
    assert.deepEqual((await reopened.status()).instances.map(instance => instance.state).sort(),
      ['starting', 'starting']);
    await waitForState(reopened, 'first', 'ready');
    const first = await reopened.invoke({
      installationId: 'first', siteId: 'site-first', operation: 'home', payload: {},
    });
    assert.deepEqual(first, { instance: 'first' });
    assert.equal((await reopened.status()).instances.find(instance =>
      instance.installationId === 'second').state, 'starting');
    await assert.rejects(reopened.invoke({
      installationId: 'second', siteId: 'site-second', operation: 'home', payload: {},
    }), { code: 'STARTING' });
    const early = await request(`${started.aggregate.origin}/cat/second/site-second/home`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-meow-runtime-access': started.aggregate.access,
      },
      body: '{}',
    });
    assert.equal(early.status, 503);
    assert.deepEqual(JSON.parse(early.text), { error: { code: 'STARTING' } });
    await waitForState(reopened, 'second', 'ready');
    assert.deepEqual(await reopened.refresh(), { applied: true, failures: [] });
    assert.equal((await reopened.status()).instances.find(instance =>
      instance.installationId === 'second').state, 'ready');
    assert.deepEqual(await reopened.invoke({
      installationId: 'second', siteId: 'site-second', operation: 'home', payload: {},
    }), { instance: 'second' });
  });

test('Cat management operations queue between background instance starts',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'first', source: delayedSource('first', 50), config: {},
      version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'second', source: delayedSource('second', 150), config: {},
      version: '1', enabled: true,
    });
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: runtime.dataRoot });
    t.after(() => reopened.close());
    await reopened.start();
    const before = (await reopened.status()).instances.find(instance =>
      instance.installationId === 'second').state;
    assert.ok(['starting', 'ready'].includes(before), before);
    const disabled = await reopened.setEnabled('second', false);
    assert.equal(disabled.state, 'disabled');
    assert.equal((await reopened.status()).instances.find(instance =>
      instance.installationId === 'second').state, 'disabled');
    await waitForState(reopened, 'first', 'ready');
    assert.equal((await reopened.status()).instances.find(instance =>
      instance.installationId === 'second').state, 'disabled');
    assert.equal((await reopened.setEnabled('second', true)).state, 'ready');
    assert.deepEqual(await reopened.invoke({
      installationId: 'second', siteId: 'site-second', operation: 'home', payload: {},
    }), { instance: 'second' });
  });

test('Cat starting instances reject management and session access without hidden waiting',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'pending', source: delayedSource('pending', 500), config: {},
      version: '1', enabled: true,
    });
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: runtime.dataRoot });
    t.after(() => reopened.close());
    await reopened.start();
    assert.throws(() => reopened.getSession('pending'), { code: 'STARTING' });
    await assert.rejects(reopened.management('pending'), { code: 'STARTING' });
    await assert.rejects(reopened.sync('pending', undefined, {
      provider: 'quark', credentials: { cookie: 'fixture' },
    }), { code: 'STARTING' });
    await assert.rejects(reopened.invoke({
      installationId: 'pending', siteId: 'site-pending', operation: 'home', payload: {},
    }), { code: 'STARTING' });
  });

test('Cat close stops queued startup and waits for the in-flight instance',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    await runtime.install({
      installationId: 'first', source: delayedSource('first', 20), config: {},
      version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'second', source: delayedSource('second', 500), config: {},
      version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'third', source: delayedSource('third', 20), config: {},
      version: '1', enabled: true,
    });
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: runtime.dataRoot });
    await reopened.start();
    await waitForState(reopened, 'first', 'ready');
    assert.equal((await reopened.status()).instances.find(instance =>
      instance.installationId === 'second').state, 'starting');
    assert.deepEqual(reopened.startupQueue, ['third']);
    await reopened.close();
    assert.equal(reopened.closed, true);
    assert.deepEqual(reopened.startupQueue, []);
    assert.equal(reopened.startupScheduled, false);
    assert.equal(reopened.sessions.size, 0);
    assert.equal(reopened.host, null);
    await assert.rejects(reopened.start(), { code: 'HOST_CLOSED' });
  });

test('Cat startup failure is recorded without blocking later instances',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'broken', source: 'module.exports.start = () => { throw new Error("broken"); };',
      config: {}, version: '1', enabled: false,
    });
    await runtime.install({
      installationId: 'healthy', source: delayedSource('healthy', 20), config: {},
      version: '1', enabled: true,
    });
    writeSettings((await runtime.status()).settingsPath, value => ({
      ...value,
      scripts: value.scripts.map(script => ({ ...script, enabled: true })),
    }));
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: runtime.dataRoot });
    t.after(() => reopened.close());
    await reopened.start();
    await waitForState(reopened, 'broken', 'failed');
    await waitForState(reopened, 'healthy', 'ready');
    assert.deepEqual(await reopened.invoke({
      installationId: 'healthy', siteId: 'site-healthy', operation: 'home', payload: {},
    }), { instance: 'healthy' });
  });

test('Cat cold-start background apply records disabled instances for consistent invocation',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'disabled-cold', source: delayedSource('disabled-cold', 0), config: {},
      version: '1', enabled: false,
    });
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: runtime.dataRoot });
    t.after(() => reopened.close());
    await reopened.start();
    const state = (await reopened.status()).instances.find(instance =>
      instance.installationId === 'disabled-cold');
    assert.equal(state.state, 'disabled');
    assert.equal(reopened.hostState('disabled-cold')?.enabled, false);
    await assert.rejects(reopened.invoke({
      installationId: 'disabled-cold', siteId: 'site-disabled-cold', operation: 'home', payload: {},
    }), { code: 'DISABLED' });
  });

test('Cat compressed fetch handles an aborted upstream response without a host error leak',
  { timeout: 30000 }, async t => {
    const brokenSource = String.raw`
      const http = require('http');
      const requestedPort = 9988;
      let server;
      const json = (response, value) => {
        response.setHeader('content-type', 'application/json');
        response.end(JSON.stringify(value));
      };
      module.exports.start = () => {
        server = http.createServer(async (request, response) => {
          if (request.url === '/config' || request.url === '/full-config') {
            json(response, { video: { sites: [{
              key: 'broken-gzip', name: 'Broken gzip',
              api: '/api/broken-gzip', actions: ['probe'],
            }] } });
            return;
          }
          if (request.url === '/broken-gzip') {
            response.writeHead(200, {
              'content-type': 'text/plain',
              'content-encoding': 'gzip',
              'content-length': '100',
            });
            response.flushHeaders();
            response.write(Buffer.from([0x1f, 0x8b, 0x08, 0x00]));
            setTimeout(() => response.socket.destroy(), 10);
            return;
          }
          if (request.url === '/api/broken-gzip/probe') {
            let code = 'UNEXPECTED';
            try {
              const reply = await fetch('http://127.0.0.1:' + requestedPort + '/broken-gzip');
              await reply.text();
            } catch (error) { code = error.code || error.name; }
            json(response, { code });
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(requestedPort, '127.0.0.1');
      };
      module.exports.stop = () => {
        server?.closeAllConnections();
        server?.close();
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'broken-gzip', source: brokenSource, config: {},
      version: '1', enabled: true,
    });
    for (let attempt = 0; attempt < 2; attempt++) {
      assert.deepEqual(await runtime.invoke({
        installationId: 'broken-gzip', siteId: 'broken-gzip',
        operation: 'probe', payload: {},
      }), { code: 'NETWORK_ERROR' });
    }
  });

test('Cat does not promote a retired guest context into the session root', async t => {
  const root = new ResourceScope();
  const session = {
    installationId: 'context-fixture',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100 },
    live: true,
    scope: () => root,
    storageWritten() {},
  };
  const services = { close: async () => {} };
  const node = new NodeCapabilities(session, services, rootFor(t));
  const request = new ResourceScope(root.signal);
  node.retainContext(1, request);
  request.close();
  assert.throws(() => node.scopeForContext(1), { code: 'CANCELLED' });
  root.close();
  await node.close();
});

test('Cat SDK loader rejects non-canonical package paths and cross-package resolution', async t => {
  const root = new ResourceScope();
  const session = {
    installationId: 'sdk-path-fixture',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100 },
    live: true,
    scope: () => root,
    storageWritten() {},
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  const util = node.source('util');
  const inherits = node.source('inherits', util.id);
  assert.equal(node.source('util', inherits.id).id, util.id);
  const parent = node.source('readable-stream/lib/stream.js');
  assert.throws(() => node.source('readable-stream/../supports-color', parent.id),
    { code: 'UNSUPPORTED_DEPENDENCY' });
  assert.throws(() => node.source('readable-stream/node_modules/events', parent.id),
    { code: 'UNSUPPORTED_DEPENDENCY' });
  root.close();
  session.live = false;
  await node.close();
});

test('Cat native Promise completion survives detach failure without retaining its receipt', async t => {
  const root = new ResourceScope();
  const failure = new Error('synthetic Promise detach failure');
  let registrations = 0;
  const scope = {
    signal: new AbortController().signal,
    assertOpen() {},
    add() {
      registrations++;
      return registrations === 1 ? () => {} : () => { throw failure; };
    },
  };
  const calls = [], failures = [], tasks = [];
  const session = {
    installationId: 'promise-detach-fixture',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100 },
    live: true,
    scope: () => scope,
    storageWritten() {},
    call(name, args) { calls.push([name, args]); },
    callbackFailed(error) { failures.push(error); },
    trackAdapter(task) { tasks.push(task); },
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  assert.equal(node.encode(Promise.resolve('settled'), new Set(), undefined, scope).$, 'promise');
  await Promise.allSettled(tasks);
  assert.equal(node.promiseLeases.size, 0);
  assert.equal(calls.length, 1);
  assert.deepEqual(failures, [failure]);
  root.close();
  session.live = false;
  await node.close();
});

test('Cat callback and listener registration failures roll back native ownership', async t => {
  const root = new ResourceScope();
  const failure = new Error('synthetic scope registration failure');
  const failedScope = {
    signal: new AbortController().signal,
    assertOpen() {},
    add() { throw failure; },
  };
  const callbackFailures = [];
  const session = {
    installationId: 'registration-rollback-fixture',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100 },
    live: true,
    scope: () => failedScope,
    storageWritten() {},
    nativeCallback: () => ({ $: 'callbackPromise', id: 'pending-result' }),
    callbackFailed(error) { callbackFailures.push(error); },
  };
  const services = {
    requestScope: () => failedScope,
    close: async () => {},
  };
  const node = new NodeCapabilities(session, services, rootFor(t));
  const callback = node.decode({ $: 'callback', id: 1, context: 0 }, root);
  assert.equal(callback({ value: true }), undefined);
  assert.equal(node.callbackReturns.size, 0);
  assert.deepEqual(callbackFailures, [failure]);

  const emitter = new EventEmitter();
  const listener = () => {};
  // Listener registration no longer adds a per-listener scope closure, so it
  // cannot fail through the scope and must not retain the emitter.
  assert.doesNotThrow(() => node.invoke({ value: emitter, role: 'stream' },
    'on', ['event', listener]));
  assert.equal(emitter.listenerCount('event'), 1);
  emitter.removeListener('event', listener);
  assert.equal(emitter.listenerCount('event'), 0);

  assert.throws(() => node.encode(Promise.resolve('unused'), new Set(), undefined, failedScope),
    error => error === failure);
  assert.equal(node.promiseLeases.size, 0);
  root.close();
  session.live = false;
  await node.close();
});

test('Cat ignores a native callback retained past bridge shutdown', async t => {
  const root = new ResourceScope();
  let callbacks = 0, failures = 0;
  const session = {
    installationId: 'late-native-callback',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100 },
    live: true,
    scope: () => root,
    storageWritten() {},
    nativeCallback() { callbacks++; },
    callbackFailed() { failures++; },
  };
  const services = { requestScope: () => null, close: async () => {} };
  const node = new NodeCapabilities(session, services, rootFor(t));
  const callback = node.decode({ $: 'callback', id: 1, context: 0 }, root);
  session.live = false;
  root.close();
  await node.close();
  assert.doesNotThrow(() => callback({ retained: true }));
  assert.equal(callbacks, 0);
  assert.equal(failures, 0);
});

test('Cat releases native callbacks with their last host reference and revokes a pending release',
  async t => {
    const root = new ResourceScope();
    const resources = new ResourceScope(root.signal);
    const session = {
      installationId: 'callback-reachability',
      revision: { source: '' },
      root,
      limits: { cleanupTimeoutMs: 100 },
      live: true,
      scope: () => resources,
      resourceScope: () => resources,
      storageWritten() {},
      trackAdapter() {},
      nativeCallback() {},
    };
    const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
    try {
      let callback = node.decode({ $: 'callback', id: 1, context: 0 }, resources);
      const original = node.callbacks.get(1);
      assert.equal(original.ref.deref(), callback);
      callback = null;
      await collectGarbage();
      assert.equal(node.callbacks.size, 0);

      node.pendingGuestRelease.callbacks.add(1);
      callback = node.decode({ $: 'callback', id: 1, context: 0 }, resources);
      assert.equal(node.pendingGuestRelease.callbacks.has(1), false);
      assert.equal(node.callbacks.get(1).ref.deref(), callback);
      assert.notEqual(node.callbacks.get(1), original);
    } finally {
      root.close();
      session.live = false;
      await node.close();
    }
  });

test('Cat listener registration does not retain the emitter through a session cleanup closure',
  async t => {
    const root = new ResourceScope();
    const resources = new ResourceScope(root.signal);
    const session = {
      installationId: 'listener-reachability',
      revision: { source: '' },
      root,
      limits: { cleanupTimeoutMs: 100 },
      live: true,
      scope: () => resources,
      resourceScope: () => resources,
      storageWritten() {},
      trackAdapter() {},
      nativeCallback() {},
    };
    const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
    try {
      let emitter = new EventEmitter();
      const reference = new WeakRef(emitter);
      const register = value => node.invoke({ value, role: 'stream' }, 'on', ['event', () => {}]);
      register(emitter);
      assert.equal(emitter.listenerCount('event'), 1);
      emitter = null;
      await collectGarbage();
      assert.equal(reference.deref(), undefined);
    } finally {
      root.close();
      session.live = false;
      await node.close();
    }
  });

test('Cat guest handle release skips persistent and naturally closing resource roles', async t => {
  const root = new ResourceScope();
  const resources = new ResourceScope(root.signal);
  const session = {
    installationId: 'guest-handle-release',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100 },
    live: true,
    scope: () => resources,
    resourceScope: () => resources,
    storageWritten() {},
    trackAdapter() {},
    nativeCallback() {},
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  try {
    const url = node.encode(new URL('https://example.invalid/'), new Set(), undefined, resources);
    const stats = node.encode({ size: 1 }, new Set(), 'stats', resources);
    const stream = node.encode(new EventEmitter(), new Set(), 'stream', resources);
    assert.equal(node.handles.size, 3);
    node.releaseGuestHandles([url.id, stats.id, stream.id]);
    assert.equal(node.handles.has(url.id), false);
    assert.equal(node.handles.has(stats.id), false);
    assert.equal(node.handles.has(stream.id), true);
  } finally {
    root.close();
    session.live = false;
    await node.close();
  }
});

test('Cat reports native handle cleanup failure at scope teardown and shutdown without a hidden retry loop',
  async t => {
    const root = new ResourceScope();
    const session = {
      installationId: 'residual-handle-fixture',
      revision: { source: '' },
      root,
      limits: { cleanupTimeoutMs: 100 },
      live: true,
      scope: () => root,
      storageWritten() {},
    };
    const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
    const scope = new ResourceScope(root.signal);
    let attempts = 0;
    const stream = {
      destroyed: false,
      destroy() {
        attempts++;
        throw new Error('synthetic disposal failure');
      },
    };
    const receipt = {
      id: 'residual-stream', value: stream, role: 'stream',
      methods: new Map(), persistent: false, leases: new Set(),
    };
    node.handles.set(receipt.id, receipt);
    node.retainHandle(receipt, scope);
    scope.close();
    assert.equal(node.handles.size, 0);
    await assert.rejects(node.close(), /synthetic disposal failure/);
    assert.equal(stream.destroyed, false);
    assert.equal(attempts, 1);
    assert.equal(node.handles.size, 0);
    root.close();
  });

test('Cat cleanup timeout releases Web stream locks and detaches owner-side cleanup tracking',
  async t => {
    const root = new ResourceScope();
    const session = {
      installationId: 'web-cleanup-timeout',
      revision: { source: '' },
      root,
      limits: { cleanupTimeoutMs: 10 },
      live: false,
      scope: () => root,
      storageWritten() {},
      trackAdapter() {},
    };
    const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
    let releases = 0;
    const reader = {
      cancel() { return new Promise(() => {}); },
      releaseLock() { releases++; },
    };
    node.releaseNativeHandle({
      id: 'pending-reader', value: reader, role: 'reader',
      methods: new Map(), persistent: false, leases: new Set(),
    });
    await assert.rejects(node.close(), { code: 'TIMEOUT' });
    assert.equal(releases, 1);
    assert.equal(node.cleanupTasks.size, 0);
    assert.equal(node.cleanupTracker.active, false);
    root.close();
  });

test('Cat Web stream cleanup releases its lock after cancellation settles',
  async t => {
    const root = new ResourceScope();
    const session = {
      installationId: 'web-cleanup-release',
      revision: { source: '' },
      root,
      limits: { cleanupTimeoutMs: 100 },
      live: false,
      scope: () => root,
      storageWritten() {},
      trackAdapter() {},
    };
    const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
    let pending = true, attempts = 0, releases = 0;
    const reader = {
      cancel() {
        return Promise.resolve().then(() => { pending = false; });
      },
      releaseLock() {
        attempts++;
        if (pending) throw Object.assign(new TypeError('pending read'), { code: 'ERR_INVALID_STATE' });
        releases++;
      },
    };
    node.releaseNativeHandle({
      id: 'settling-reader', value: reader, role: 'reader',
      methods: new Map(), persistent: false, leases: new Set(),
    });
    await node.close();
    assert.equal(attempts, 2);
    assert.equal(releases, 1);
    root.close();
  });

test('Cat Web stream cleanup accepts a replayed Axios cancellation and releases its reader', async t => {
  const scope = new ResourceScope();
  const session = {
    installationId: 'cancelled-reader', revision: { source: '' }, root: scope,
    limits: { cleanupTimeoutMs: 100 }, live: false, scope: () => scope,
    storageWritten() {}, trackAdapter() {},
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  const stream = new ReadableStream({
    start(controller) {
      controller.error(Object.assign(new Error('synthetic cancellation'), { code: 'ERR_CANCELED' }));
    },
  });
  const reader = stream.getReader();
  reader.closed.catch(() => {});
  node.releaseNativeHandle({
    id: 'cancelled-reader', value: reader, role: 'reader',
    methods: new Map(), persistent: false, leases: new Set(),
  });
  await node.close();
  assert.equal(stream.locked, false);
  assert.equal(node.cleanupTasks.size, 0);
  scope.close();
});

test('Cat Web stream cleanup distinguishes a stored operation error from a new cancel failure', async t => {
  const scope = new ResourceScope();
  const session = {
    installationId: 'errored-reader', revision: { source: '' }, root: scope,
    limits: { cleanupTimeoutMs: 100 }, live: false, scope: () => scope,
    storageWritten() {}, trackAdapter() {},
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  const stored = new Error('synthetic operation error');
  const stream = new ReadableStream({ start(controller) { controller.error(stored); } });
  node.releaseNativeHandle({
    id: 'errored-stream', value: stream, role: 'readableWeb',
    methods: new Map(), persistent: false, leases: new Set(),
  });
  await node.close();
  assert.equal(stream.locked, false);
  const failing = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  const failure = new Error('synthetic cancel failure');
  const active = new ReadableStream({ cancel() { throw failure; } });
  failing.releaseNativeHandle({
    id: 'active-stream', value: active, role: 'readableWeb',
    methods: new Map(), persistent: false, leases: new Set(),
  });
  await assert.rejects(failing.close(), error => error === failure);
  assert.equal(active.locked, false);
  scope.close();
});

test('Cat proxy header cleanup treats Connection declarations case-insensitively', () => {
  assert.deepEqual(cleanHopByHop({
    Connection: 'X-Private, X-Trace',
    'X-Private': 'secret',
    'x-trace': 'secret',
    'Content-Type': 'application/octet-stream',
  }), { 'Content-Type': 'application/octet-stream' });
});

test('Cat HTTPS management bootstrap cookie is Secure', () => {
  const session = { installationId: 'cookie-fixture', activated: true, root: {} };
  const services = new ScriptServices(session);
  let status, headers, ended = false;
  const accepted = services.accept({
    headers: { host: '127.0.0.1:4443' },
    url: `/__meow/open?access=${services.access}&target=%2Fwebsite`,
    socket: {},
  }, {
    writeHead(value, input) { status = value; headers = input; },
    end() { ended = true; },
  }, { address: () => ({ port: 4443 }) }, true);
  assert.equal(accepted, false);
  assert.equal(status, 302);
  assert.equal(ended, true);
  assert.match(headers['set-cookie'], /; Secure$/);
  for (const agent of services.agents) agent.destroy();
});

test('Cat access comparison rejects equal-character Unicode tokens without throwing', () => {
  const session = { installationId: 'access-fixture', activated: true, root: {} };
  const services = new ScriptServices(session);
  let status;
  const accepted = services.accept({
    headers: {
      host: '127.0.0.1:4555',
      'x-meow-script-access': 'é'.repeat(services.access.length),
    },
    url: '/',
    socket: {},
  }, {
    writeHead(value) { status = value; },
    end() {},
  }, { address: () => ({ port: 4555 }) }, false);
  assert.equal(accepted, false);
  assert.equal(status, 403);
  for (const agent of services.agents) agent.destroy();
});

test('Cat service cleanup continues after request and agent cleanup failures', async () => {
  const root = new ResourceScope();
  const session = {
    installationId: 'service-cleanup-fixture',
    live: false,
    root,
    nativeRequests: new Set(),
    limits: { cleanupTimeoutMs: 100 },
  };
  const services = new ScriptServices(session);
  const broken = new EventEmitter();
  broken.closed = false;
  broken.destroyed = false;
  broken.destroy = () => { throw new Error('synthetic request cleanup failure'); };
  const released = new EventEmitter();
  released.closed = false;
  released.destroyed = false;
  released.destroy = () => {
    released.destroyed = true;
    released.closed = true;
    released.emit('close');
  };
  session.nativeRequests.add(broken);
  session.nativeRequests.add(released);
  let agentReleased = false;
  const brokenAgent = { destroy() { throw new Error('synthetic agent cleanup failure'); } };
  const brokenAgent2 = { destroy() { throw new Error('synthetic agent cleanup failure'); } };
  services.agents.add(brokenAgent);
  services.agents.add(brokenAgent2);
  services.agents.add({ destroy() { agentReleased = true; } });
  await assert.rejects(services.close(), /synthetic request cleanup failure/);
  assert.equal(released.destroyed, true);
  assert.equal(agentReleased, true);
  assert.equal(session.nativeRequests.size, 0);
  assert.equal(services.agents.size, 0);
  assert.equal(broken.listenerCount('error'), 1);
  broken.closed = true;
  broken.destroyed = true;
  broken.emit('close');
  assert.equal(broken.listenerCount('error'), 1);
  assert.equal(services.session, null);
  root.close();
});

test('Cat service timeout detaches request ownership and leaves only a late-close guard', async () => {
  const root = new ResourceScope();
  const context = new ResourceScope(root.signal);
  const session = {
    installationId: 'service-timeout-detach',
    live: false,
    root,
    scope: () => root,
    nativeRequests: new Set(),
    limits: { cleanupTimeoutMs: 10 },
    node: { tags: new WeakMap() },
  };
  const services = new ScriptServices(session);
  const request = new EventEmitter();
  request.closed = false;
  request.destroyed = false;
  request.destroy = () => { request.destroyed = true; };
  services.ownForward(request, context);
  assert.equal(context.cleanups.size, 2);
  await assert.rejects(services.close(), { code: 'TIMEOUT' });
  assert.equal(session.nativeRequests.size, 0);
  assert.equal(services.requestCleanup.size, 0);
  assert.equal(context.cleanups.size, 1);
  assert.equal(request.listenerCount('close'), 0);
  assert.equal(request.listenerCount('error'), 1);
  assert.doesNotThrow(() => request.emit('error', new Error('late request error')));
  request.closed = true;
  request.emit('close');
  assert.equal(request.listenerCount('close'), 0);
  assert.equal(request.listenerCount('error'), 1);
  assert.equal(services.session, null);
  root.close();
});

test('Cat request-scope cleanup reports a listener that cannot be removed', () => {
  const root = new ResourceScope();
  const services = new ScriptServices({
    installationId: 'request-scope-cleanup',
    root,
    scope: () => root,
  });
  const request = new EventEmitter();
  request.socket = new EventEmitter();
  const response = new EventEmitter();
  const responseEmit = response.emit;
  response.writableEnded = false;
  response.destroyed = false;
  response.destroy = () => { response.destroyed = true; };
  const removed = [];
  const requestOff = request.off.bind(request);
  request.off = (event, listener) => {
    removed.push(`request:${event}`);
    if (event === 'aborted')
      throw new Error('synthetic listener removal failure');
    return requestOff(event, listener);
  };
  for (const [name, target] of [['response', response], ['socket', request.socket]]) {
    const off = target.off.bind(target);
    target.off = (event, listener) => {
      removed.push(`${name}:${event}`);
      return off(event, listener);
    };
  }
  const scope = services.createRequestScope(root, request, response);
  scope.close();
  assert.deepEqual(removed, [
    'request:aborted', 'socket:close',
  ]);
  assert.equal(response.emit, responseEmit);
  assert.equal(request.listenerCount('aborted'), 1);
  assert.equal(response.listenerCount('finish'), 0);
  assert.equal(response.listenerCount('close'), 0);
  assert.equal(request.socket.listenerCount('close'), 0);
  assert.equal(services.cleanupState.error.message, 'synthetic listener removal failure');
  assert.equal(response.destroyed, true);
  for (const agent of services.agents) agent.destroy();
  root.close();
});

test('Cat response completion runs guest finish and close handlers before retiring request leases',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'response-completion', enabled: true, source: `
      const http = require('http');
      let server, active = 0, completed = 0, closed = 0;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config')
            return response.end(JSON.stringify({ video: { sites: [{
              key: 'completion', api: '/api/completion', actions: ['home'],
            }] } }));
          active++;
          response.once('finish', () => { completed++; });
          response.once('close', () => { active--; closed++; });
          response.end(JSON.stringify({ active, completed, closed }));
        });
        server.listen(9988);
      };
      module.exports.stop = () => server.close();
    ` });
    for (let index = 0; index < 8; index++) {
      assert.deepEqual(await runtime.invoke({
        installationId: 'response-completion', siteId: 'completion', operation: 'home', payload: {},
      }), { active: 1, completed: index, closed: index });
    }
  });

test('Cat control cancellation closes the transport before retiring guest cleanup handlers',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'cancel-completion',
      source: `
      const http = require('http');
      let server, active = 0, completed = 0;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config')
            return response.end(JSON.stringify({ video: { sites: [{
              key: 'completion', api: '/api/completion', actions: ['home', 'wait'],
            }] } }));
          if (request.url === '/api/completion/home')
            return response.end(JSON.stringify({ active, completed }));
          active++;
          response.once('close', () => { active--; completed++; });
        });
        server.listen(9988);
      };
      module.exports.stop = () => server.close();
    `,
    });
    let arrived, closed;
    const requestArrived = new Promise(resolve => { arrived = resolve; });
    const responseClosed = new Promise(resolve => { closed = resolve; });
    session.services.main.once('request', (_request, response) => {
      response.once('close', closed);
      arrived();
    });
    const controller = new AbortController();
    const pending = session.invoke({
      siteId: 'completion', operation: 'wait', payload: {},
    }, { signal: controller.signal });
    const cancelled = assert.rejects(pending, { code: 'CANCELLED' });
    await requestArrived;
    controller.abort();
    await cancelled;
    await responseClosed;
    assert.deepEqual(await session.invoke({
      siteId: 'completion', operation: 'home', payload: {},
    }), { active: 0, completed: 1 });
  });

test('Cat connection close handlers run before native socket leases retire',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'socket-completion',
      source: `
      const http = require('http');
      let server, active = 0;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config')
            return response.end(JSON.stringify({ video: { sites: [{
              key: 'completion', api: '/api/completion', actions: ['home'],
            }] } }));
          response.end(JSON.stringify({ active }));
        });
        server.on('connection', socket => {
          active++;
          socket.once('close', () => { active--; });
        });
        server.listen(9988);
      };
      module.exports.stop = () => server.close();
    `,
    });
    await Promise.all([...session.services.connections].map(socket => socket.closed ? undefined
      : new Promise(resolve => socket.once('close', resolve))));
    for (let index = 0; index < 8; index++) {
      const closed = new Promise(resolve => {
        session.services.main.once('connection', socket => socket.once('close', resolve));
      });
      assert.deepEqual(await session.invoke({
        siteId: 'completion', operation: 'home', payload: {},
      }), { active: 1 });
      await closed;
    }
  });

test('Cat upgraded transports run guest close handlers before retiring request leases',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'upgrade-completion',
      source: `
      const http = require('http');
      let server, closed = 0;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config')
            return response.end(JSON.stringify({ video: { sites: [{
              key: 'completion', api: '/api/completion', actions: ['home'],
            }] } }));
          response.end(JSON.stringify({ closed }));
        });
        server.on('upgrade', (request, socket) => {
          socket.once('close', () => { closed++; });
          socket.once('end', () => socket.end());
          socket.resume();
          socket.write('HTTP/1.1 101 Switching Protocols\\r\\nConnection: Upgrade\\r\\nUpgrade: test\\r\\n\\r\\n');
        });
        server.listen(9988);
      };
      module.exports.stop = () => server.close();
    `,
    });
    const endpoint = session.endpoint();
    const closed = new Promise(resolve => {
      session.services.main.once('upgrade', (_request, socket) => socket.once('close', resolve));
    });
    await new Promise((resolve, reject) => {
      const client = http.get(endpoint.origin, {
        agent: false, headers: { [scriptAccessHeader]: endpoint.access,
          connection: 'Upgrade', upgrade: 'test' },
      });
      client.once('error', reject);
      client.once('upgrade', (_response, socket) => { socket.destroy(); resolve(); });
    });
    await closed;
    assert.deepEqual(await session.invoke({
      siteId: 'completion', operation: 'home', payload: {},
    }), { closed: 1 });
  });

test('Cat native network guard survives guest listener removal and leaves on close', async t => {
  const root = new ResourceScope();
  const session = {
    installationId: 'network-guard',
    revision: { source: '' },
    root,
    limits: { cleanupTimeoutMs: 100 },
    live: true,
    scope: () => root,
    storageWritten() {},
    trackAdapter() {},
  };
  const node = new NodeCapabilities(session, {
    http: {}, https: {}, close: async () => {},
  }, rootFor(t));
  const socket = new EventEmitter();
  socket.closed = false;
  socket.destroyed = false;
  socket.destroy = () => {
    if (socket.closed) return;
    socket.destroyed = true;
    socket.closed = true;
    socket.emit('close');
  };
  node.ownNetwork(socket);
  const guest = () => {};
  node.callbackReverse.set(guest, 1);
  socket.on('error', guest);
  node.invoke({ value: socket, role: 'socket' }, 'removeAllListeners', ['error']);
  assert.equal(socket.listenerCount('error'), 1);
  assert.doesNotThrow(() => socket.emit('error', new Error('synthetic socket failure')));
  socket.destroy();
  assert.equal(socket.listenerCount('error'), 0);
  await node.close();
  root.close();
});

test('Cat network cleanup timeout detaches its request scope and keeps a lightweight guard',
  async t => {
    const root = new ResourceScope();
    const scope = new ResourceScope(root.signal);
    const session = {
      installationId: 'network-timeout-detach',
      revision: { source: '' },
      root,
      limits: { cleanupTimeoutMs: 10 },
      live: false,
      scope: () => scope,
      resourceScope: () => scope,
      storageWritten() {},
      trackAdapter() {},
    };
    const node = new NodeCapabilities(session, {
      http: {}, https: {}, close: async () => {},
    }, rootFor(t));
    const socket = new EventEmitter();
    socket.closed = false;
    socket.destroyed = false;
    socket.destroy = () => { socket.destroyed = true; };
    node.ownNetwork(socket);
    assert.equal(scope.cleanups.size, 2);
    await assert.rejects(node.close(), { code: 'TIMEOUT' });
    assert.equal(node.networkResources.size, 0);
    assert.equal(node.networkCleanup.size, 0);
    assert.equal(scope.cleanups.size, 1);
    assert.equal(socket.listenerCount('error'), 1);
    assert.doesNotThrow(() => socket.emit('error', new Error('late socket error')));
    socket.closed = true;
    socket.emit('close');
    assert.equal(socket.listenerCount('close'), 0);
    assert.equal(socket.listenerCount('error'), 1);
    assert.equal(node.session, null);
    assert.equal(node.fs, null);
    assert.deepEqual(Object.keys(node.builtins), []);
    root.close();
  });

test('Cat rejects an expired internal request scope instead of promoting it to the session root', () => {
  const root = new ResourceScope();
  const session = { installationId: 'scope-fixture', activated: true, live: true, root };
  const services = new ScriptServices(session);
  const child = new ResourceScope(root.signal);
  const token = services.withScope(child);
  child.close();
  let status;
  const accepted = services.accept({
    headers: {
      host: '127.0.0.1:4666',
      [scriptAccessHeader]: services.access,
      'x-meow-request-scope': token.id,
    },
    url: '/',
    socket: {},
  }, {
    writeHead(value) { status = value; },
    end() {},
  }, { address: () => ({ port: 4666 }) }, false);
  root.close();
  for (const agent of services.agents) agent.destroy();
  assert.equal(accepted, false);
  assert.equal(status, 410);
});

test('Cat unregistered loopback ports connect to the real host port', async t => {
  const echo = net.createServer(socket => socket.pipe(socket));
  await new Promise((resolve, reject) => {
    echo.once('error', reject);
    echo.listen(0, '127.0.0.1', resolve);
  });
  t.after(() => new Promise(resolve => {
    echo.closeAllConnections?.();
    echo.close(resolve);
  }));
  const direct = await serverFor(t, (_request, response) => response.end('direct'));
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'direct-loopback', enabled: true, config: {}, version: '1', source: String.raw`
      const http = require('http'), net = require('net');
      const get = value => new Promise((resolve, reject) => {
        http.get(value, incoming => {
          const chunks = [];
          incoming.on('data', chunk => chunks.push(Buffer.from(chunk)));
          incoming.once('end', () => resolve(Buffer.concat(chunks).toString()));
          incoming.once('error', reject);
        }).once('error', reject);
      });
      const socket = (port, value) => new Promise((resolve, reject) => {
        const client = net.connect(port, '127.0.0.1', () => client.end(value));
        const chunks = [];
        client.on('data', chunk => chunks.push(Buffer.from(chunk)));
        client.once('end', () => resolve(Buffer.concat(chunks).toString()));
        client.once('error', reject);
      });
      module.exports = {
        home: async () => ({
          http: await get('http://127.0.0.1:${direct.port}/direct'),
          socket: await socket(${echo.address().port}, 'echo'),
        }),
      };
    `,
  });
  assert.deepEqual(await runtime.invoke({
    installationId: 'direct-loopback', siteId: 'default', operation: 'home', payload: {},
  }), { http: 'direct', socket: 'echo' });
});

test('Cat can select an HTTPS listener as the script primary endpoint', async t => {
  const scope = { signal: new AbortController().signal, add: () => () => {} };
  const services = new ScriptServices({
    installationId: 'https-primary',
    live: true,
    scope: () => scope,
  });
  t.after(() => services.close());
  services.create(https, true);
  const server = services.create(https, true);
  server.listen(9443, '0.0.0.0');
  await services.started.promise;
  const endpoint = services.endpoint();
  assert.match(endpoint.origin, /^https:\/\/127\.0\.0\.1:\d+$/);
  assert.match(endpoint.managementUrl, /^https:\/\/127\.0\.0\.1:\d+\/__meow\/open\?/);
  assert.deepEqual(services.endpoints()[0].requestedPorts, [9443]);
});

test('Cat client TLS keeps external verification and only relaxes registered local TLS', () => {
  const services = new ScriptServices({ installationId: 'tls-policy' });
  const external = services.tlsOptions({
    rejectUnauthorized: false,
    checkServerIdentity: () => undefined,
    ca: 'custom-ca',
  }, { client: true });
  assert.equal(external.rejectUnauthorized, true);
  assert.equal(Object.hasOwn(external, 'checkServerIdentity'), false);
  assert.equal(external.ca, 'custom-ca');
  const local = services.tlsOptions({
    rejectUnauthorized: true,
    checkServerIdentity: () => new Error('reject'),
  }, { client: true, local: true });
  assert.equal(local.rejectUnauthorized, false);
  assert.equal(Object.hasOwn(local, 'checkServerIdentity'), false);
  for (const agent of services.agents) agent.destroy();
});

test('Cat private volume reports each durable mutation once', async t => {
  const root = rootFor(t);
  let writes = 0;
  const volume = new PrivateVolume(root, 'receipt-fixture', () => writes++, 'source-data');
  t.after(() => volume.close());
  const fs = volume.facade();
  fs.mkdirSync('/data', { recursive: true });
  assert.equal(writes, 1);
  fs.mkdirSync('/data', { recursive: true });
  assert.equal(writes, 1);
  const fd = fs.openSync('/data/state.json', 'w');
  fs.fsyncSync(fd);
  assert.equal(writes, 2);
  fs.fsyncSync(fd);
  assert.equal(writes, 2);
  fs.writeSync(fd, Buffer.from('1'));
  assert.equal(writes, 3);
  fs.closeSync(fd);
  assert.equal(writes, 3);
  fs.openSync('/data/unclosed.json', 'w');
  const stream = fs.createWriteStream('/data/unclosed-stream.json');
  const sourceStream = fs.createReadStream('/source.js');
  stream.write('stream-data');
  await volume.close();
  assert.equal(writes, 5);
  assert.equal(readFileSync(join(volume.root, 'data', 'unclosed-stream.json'), 'utf8'), 'stream-data');
  assert.equal(sourceStream.destroyed, true);
});

test('Cat private metadata and copies preserve native read-only file behavior', async t => {
  const volume = new PrivateVolume(rootFor(t), 'readonly-metadata', () => {}, '');
  const api = volume.facade();
  try {
    api.writeFileSync('/original.txt', 'fixture');
    api.chmodSync('/original.txt', 0o444);
    api.utimesSync('/original.txt', new Date(1234), new Date(1234));
    assert.equal(api.statSync('/original.txt').mtimeMs, 1234);
    api.copyFileSync('/original.txt', '/copy.txt');
    api.renameSync('/original.txt', '/renamed.txt');
    assert.equal(api.readFileSync('/copy.txt', 'utf8'), 'fixture');
    assert.equal(api.readFileSync('/renamed.txt', 'utf8'), 'fixture');
    assert.equal(api.existsSync('/original.txt'), false);
  } finally {
    for (const path of ['/original.txt', '/copy.txt', '/renamed.txt'])
      if (api.existsSync(path)) api.chmodSync(path, 0o666);
    await volume.close();
  }
});

test('Cat private volume reports a failed descriptor and closes it at shutdown', async t => {
  const volume = new PrivateVolume(rootFor(t), 'descriptor-retry', () => {}, 'source-data');
  const facade = volume.facade();
  facade.writeFileSync('/retry.txt', 'retry');
  const id = facade.openSync('/retry.txt', 'r');
  const descriptor = volume.descriptors.get(id);
  const actual = descriptor.fd;
  descriptor.fd = -1;
  assert.throws(() => facade.closeSync(id), { code: 'EBADF' });
  assert.equal(volume.descriptors.has(id), true);
  descriptor.fd = actual;
  await assert.rejects(volume.close(), { code: 'EBADF' });
  assert.equal(volume.descriptors.size, 0);
});

test('Cat private stream setup failure keeps ownership until native close', async t => {
  const failure = new Error('synthetic stream scope failure');
  const controller = new AbortController();
  const scope = {
    signal: controller.signal,
    assertOpen() {},
    add() { throw failure; },
  };
  const volume = new PrivateVolume(rootFor(t), 'stream-setup-failure', () => {},
    'source-data', () => scope);
  const facade = volume.facade();
  assert.throws(() => facade.createReadStream('/source.js'), error => error === failure);
  await new Promise(resolve => setImmediate(resolve));
  await assert.rejects(volume.close(), error => error === failure);
  assert.equal(volume.streams.size, 0);
  assert.equal(volume.streamCleanup.size, 0);
});

test('Cat private stream cleanup reports listener removal failure', async t => {
  const failure = new Error('synthetic transient stream listener failure');
  const volume = new PrivateVolume(rootFor(t), 'stream-listener-retry', () => {},
    'source-data', undefined, 100);
  const stream = volume.facade().createReadStream('/source.js');
  const off = stream.off.bind(stream);
  let attempts = 0;
  stream.off = (event, listener) => {
    if (event === 'close' && attempts++ === 0) throw failure;
    return off(event, listener);
  };
  await assert.rejects(volume.close(), error => error === failure);
  assert.equal(attempts, 2);
  assert.equal(volume.streams.size, 0);
  assert.equal(volume.streamCleanup.size, 0);
  assert.equal(stream.listenerCount('close'), 0);
});

test('Cat private stream timeout detaches the volume and keeps only late-close guards', async t => {
  const volume = new PrivateVolume(rootFor(t), 'stream-timeout-detach', () => {},
    'source-data', undefined, 10);
  const stream = volume.facade().createReadStream('/source.js');
  stream.destroy = () => stream;
  await assert.rejects(volume.close(), { code: 'ETIMEDOUT' });
  assert.equal(volume.streams.size, 0);
  assert.equal(volume.streamCleanup.size, 0);
  assert.equal(stream.listenerCount('error'), 1);
  assert.doesNotThrow(() => stream.emit('error', new Error('late stream error')));
  stream.emit('close');
  assert.equal(stream.listenerCount('close'), 0);
  assert.equal(stream.listenerCount('error'), 1);
});

test('Cat creates internal scope tokens only after request payload serialization succeeds',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'fixture-a',
      source,
      config: { id: 'site-a', protocol: 'old' },
    });
    const before = session.services.scopes.size;
    const cyclic = {};
    cyclic.self = cyclic;
    await assert.rejects(session.read('/api/site-a/home', cyclic, session.root), TypeError);
    assert.equal(session.services.scopes.size, before);
  });

test('Cat isolation, private persistence, same requested ports and aggregate/direct routing stay instance-scoped',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = await installed(t, root);
    await runtime.install({
      installationId: 'fixture-b',
      source,
      config: { id: 'site-b', protocol: 'new' },
      version: '1',
      enabled: true,
    });
    const status = await runtime.status();
    assert.equal(status.instances.length, 2);
    const [a, b] = ['fixture-a', 'fixture-b'].map(id =>
      status.instances.find(instance => instance.installationId === id));
    assert.notEqual(new URL(a.managementUrl).port, new URL(b.managementUrl).port);
    assert.ok(a.endpoints.some(endpoint => endpoint.requestedPorts.includes(9988)));
    assert.ok(b.endpoints.some(endpoint => endpoint.requestedPorts.includes(9988)));

    const local = await serverFor(t, (_request, response) => response.end('reachable'));
    const probe = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'probe', payload: {
        privateUrl: local.socketOrigin + '/private',
        loopbackUrl: local.socketOrigin + '/loopback',
      },
    });
    const { virtual, ...plainProbe } = probe;
    assert.deepEqual(plainProbe, {
      count: 1,
      hostFile: 'EPERM',
      dependency: 'UNSUPPORTED_DEPENDENCY',
      privateHost: { status: 200, body: 'reachable' },
      unmappedLoopback: { status: 200, body: 'reachable' },
      hostEnvironment: null,
      guestGlobals: { lx: 'undefined', meow: 'undefined', bridge: 'undefined' },
      fsCompatibility: { exists: true, nativeRealpath: '/state.json', nativeAlias: true },
      selfHeaders: { hostCorrect: true, access: null, scope: null },
      configValue: null,
      cookie: null,
      multipart: { contentType: true, text: true, file: true },
      responseStream: 'stream-body',
      deletedSetCookie: true,
    });
    assert.equal(virtual.dartPort, 0);
    assert.deepEqual(virtual.os, {
      platform: 'linux', arch: 'wasm32', hostname: 'script', homedir: '/', tmpdir: '/tmp',
      cpus: 1, loopback: '127.0.0.1', totalmem: SCRIPT_LIMITS.maxMemoryBytes,
      freemem: virtual.os.freemem,
    });
    assert.ok(virtual.os.freemem >= 0 && virtual.os.freemem <= virtual.os.totalmem);
    assert.equal(virtual.process.stdin, 0);
    assert.equal(virtual.process.stdout, 1);
    assert.equal(virtual.process.stderr, 2);
    assert.equal(virtual.process.title, 'script');
    assert.equal(virtual.process.rawDebug, 'function');
    assert.equal(virtual.process.usage.rss, virtual.process.usage.heapUsed);
    assert.ok(virtual.process.usage.heapUsed > 0 &&
      virtual.process.usage.heapUsed <= virtual.os.totalmem);
    assert.deepEqual(virtual.constructors, {
      url: true, search: true, encoder: true, decoder: true, stringDecoder: true,
      blob: true, file: true, readable: true, writable: true, transform: true,
      compression: true, decompression: true, encoderStream: true, decoderStream: true,
      globalReadable: true, globalWritable: true, globalTransform: true,
      globalCompression: true, globalDecompression: true, form: true, fileIsBlob: true,
      byteStrategy: true, countStrategy: true, byteSize: 3, countSize: 1,
      httpServer: true, httpsServer: true, agent: true,
    });

    const search = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'search', payload: { wd: '猫源' },
    });
    assert.equal(search.list[0].vod_name, '猫源');
    assert.equal(search._meow.version, 1);
    assert.equal(search._meow.media[0].quality, '1080P');
    assert.match(search.list[0].vod_pic, new RegExp('^' + status.aggregate.origin.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '/proxy/'));
    assert.deepEqual(await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'home',
      payload: { resultField: true },
    }), { result: { value: 'nested-result' }, marker: 'outer-result' });

    const aggregate = await request(`${status.aggregate.origin}/cat/fixture-a/site-a/search`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-meow-runtime-access': status.aggregate.access,
      },
      body: JSON.stringify({ wd: 'aggregate' }),
    });
    assert.equal(aggregate.status, 200);
    assert.equal(JSON.parse(aggregate.text).list[0].vod_name, 'aggregate');

    const opened = await request(a.managementUrl);
    assert.equal(opened.status, 302);
    const cookie = opened.headers['set-cookie'][0].split(';')[0];
    const pageUrl = new URL(opened.headers.location, a.managementUrl);
    const missingFetchContext = await request(pageUrl, {
      headers: { cookie },
    });
    assert.equal(missingFetchContext.status, 403);
    const page = await request(pageUrl, {
      headers: { cookie, 'sec-fetch-site': 'same-origin' },
    });
    assert.equal(page.status, 200);
    assert.match(page.text, /site-a/);

    const play = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: { id: 'hls' },
    });
    assert.equal(play._meow.routes[0].kind, 'script-proxy');
    const variant = play._meow.media.at(-1).variants[0];
    assert.equal(variant.quality, '1080P');
    assert.match(variant.url, /\/proxy\/fixture-a\//);
    const playlist = await request(play.url);
    assert.equal(playlist.status, 200);
    assert.equal(playlist.headers['x-hop'], undefined);
    assert.match(playlist.text, /^  #EXT-X-VERSION:3$/m);
    const segment = playlist.text.split(/\r?\n/).find(line => /^http:/.test(line));
    assert.match(segment, /\/proxy\/fixture-a\//);
    const rangedPlaylist = await request(play.url, {
      headers: { range: 'bytes=0-6', connection: 'x-remove', 'x-remove': 'secret' },
    });
    assert.equal(rangedPlaylist.status, 206);
    assert.equal(rangedPlaylist.text, '#EXTM3U');
    assert.match(rangedPlaylist.headers['content-range'], /^bytes 0-6\/\d+$/);
    assert.equal(rangedPlaylist.headers['x-hop'], undefined);
    const ranged = await request(segment, { headers: { range: 'bytes=2-5' } });
    assert.equal(ranged.status, 206);
    assert.equal(ranged.text, '2345');
    assert.equal(ranged.headers['content-range'], 'bytes 2-5/10');
    const cookieProxy = await request(segment, {
      headers: { cookie: 'media_auth=client; __meow_private=blocked' },
    });
    assert.equal(cookieProxy.headers['x-upstream-cookie'], 'media_auth=client');
    assert.deepEqual(cookieProxy.headers['set-cookie'], ['media_session=kept; Path=/']);
    const unscoped = await request(segment, {
      headers: { 'x-meow-request-scope': 'attacker-controlled' },
    });
    assert.equal(unscoped.status, 200);
    assert.equal(unscoped.text, '0123456789');

    const redirected = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: { id: 'redirect' },
    });
    const redirect = await request(redirected.url);
    assert.equal(redirect.status, 302);
    assert.match(redirect.headers.location, /\/proxy\/fixture-a\//);
    assert.equal((await request(redirect.headers.location)).text, '0123456789');

    const unmapped = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: { id: 'unmapped' },
    });
    assert.equal((await request(unmapped.url)).status, 200);

    const primitivePlay = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: 'primitive',
    });
    assert.match(primitivePlay.url, /\/proxy\/fixture-a\//);

    const controller = new AbortController();
    const slow = runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'slow', payload: {},
    }, { signal: controller.signal });
    controller.abort();
    await assert.rejects(slow, { code: 'CANCELLED' });

    await runtime.close();
    const reopened = createCatRuntime({ dataRoot: root });
    t.after(() => reopened.close());
    await reopened.start();
    await waitForState(reopened, 'fixture-a', 'ready');
    await waitForState(reopened, 'fixture-b', 'ready');
    const persisted = await reopened.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'probe', payload: {},
    });
    assert.equal(persisted.count, 2);
    assert.equal((await reopened.status()).instances.length, 2);
  });

test('Cat aggregate proxy cannot escape its registered origin through a scheme-relative path',
  { timeout: 30000 }, async t => {
    let captured = 0;
    const attacker = http.createServer((_request, response) => {
      captured++;
      response.end('escaped');
    });
    await new Promise((resolve, reject) => {
      attacker.once('error', reject);
      attacker.listen(0, '127.0.0.1', resolve);
    });
    t.after(() => new Promise(resolve => {
      attacker.closeAllConnections();
      attacker.close(resolve);
    }));
    const runtime = await installed(t, rootFor(t));
    const play = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: { id: 'hls' },
    });
    const escaped = new URL(play.url);
    const prefix = escaped.pathname.split('/').slice(0, 5).join('/');
    escaped.pathname = `${prefix}//127.0.0.1:${attacker.address().port}/capture`;
    const denied = await request(escaped);
    assert.equal(denied.status, 403);
    assert.equal(captured, 0);
  });

test('Cat aggregate proxy rejects repeatedly encoded and normalized management paths',
  { timeout: 30000 }, async t => {
    const runtime = await installed(t, rootFor(t));
    const play = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: { id: 'hls' },
    });
    const route = new URL(play.url);
    const prefix = route.pathname.split('/').slice(0, 5).join('/');
    for (const path of [
      '/%2577ebsite',
      '/%2561dmin',
      '/%255f%255fmeow/open',
      '/%252e%252e/full-config',
      '/%255cwebsite',
      '/%252f%252fexample.invalid/website',
    ]) {
      const denied = await request(`${route.origin}${prefix}${path}`);
      assert.equal(denied.status, 403, path);
    }
  });

test('Cat provider interception is settings-controlled, emits one Cat root and falls back to script networking',
  { timeout: 30000 }, async t => {
    const calls = [];
    const runtime = await installed(t, rootFor(t), {
      network: async input => {
        calls.push(input.url);
        return {
          statusCode: 200,
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ status: 200, code: 0, data: { stoken: 'upstream' } }),
        };
      },
    });
    const status = await runtime.status();
    writeSettings(status.settingsPath, value => ({
      ...value,
      providers: { ...value.providers, quark: true },
      mappings: [{ provider: 'quark', aliases: ['QuarkAlias'] }],
    }));
    assert.deepEqual(await runtime.refresh(), { applied: true, failures: [] });
    const intercepted = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'pan', payload: {},
    });
    assert.equal(calls.length, 0);
    assert.equal(Object.hasOwn(intercepted, '_meow'), true);
    assert.equal(Object.hasOwn(intercepted, 'list'), true);
    assert.deepEqual(intercepted.upstreamShape, {
      rawHeaders: true, trailers: true, rawTrailers: true,
      httpVersion: '1.1', major: 1, minor: 1, complete: true,
    });
    assert.equal(intercepted._meow.resources[0].provider, 'quark');
    const resource = intercepted.list[0].vod_play_url.split('$').at(-1);
    assert.match(resource, /^meow\.pan\.v1\./);
    const delegated = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: { id: resource },
    });
    assert.equal(delegated.parse, 0);
    assert.equal(delegated.url, resource);
    assert.equal(delegated._meow.resources[0].kind, 'pan');

    await waitFor(() => runtime.host.get('fixture-a').resources.networkRequests === 0,
      'completed provider interception retained a native request');
    const resourcesBefore = runtime.host.get('fixture-a').resources.networkRequests;
    const opaque = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'panOpaque', payload: {},
    });
    assert.equal(opaque.upstream.data.stoken, 'upstream');
    await waitFor(() =>
      runtime.host.get('fixture-a').resources.networkRequests === resourcesBefore,
    'opaque provider request did not release its native resources');
    const resourcesAfter = runtime.host.get('fixture-a').resources.networkRequests;
    assert.equal(resourcesAfter, resourcesBefore);
    assert.equal(calls.length, 1);

    const guest = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'guestMeow', payload: {},
    });
    assert.deepEqual(guest._meow, { guest: true });
    assert.match(guest.list[0].vod_play_url, /https:\/\/pan\.quark\.cn\/s\/abcd1234/);
    assert.match(guest.url, new RegExp('^' +
      status.aggregate.origin.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '/proxy/'));

    writeSettings(status.settingsPath, value => ({
      ...value,
      providers: { ...value.providers, quark: false },
    }));
    await runtime.refresh();
    const fallback = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'pan', payload: {},
    });
    assert.equal(calls.length, 2);
    assert.match(fallback.list[0].vod_play_url, /https:\/\/pan\.quark\.cn\/s\/abcd1234/);
    assert.equal(fallback._meow, undefined);

    writeSettings(status.settingsPath, value => ({ ...value, port: -1 }));
    await assert.rejects(runtime.refresh(), { code: 'CORRUPT_STORAGE' });
    const stillApplied = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'pan', payload: {},
    });
    assert.equal(calls.length, 3);
    assert.equal(stillApplied._meow, undefined);
    writeSettings(status.settingsPath, value => ({ ...value, port: 0 }));
    await runtime.refresh();
  });

test('Cat old and new management protocols persist synchronized credentials without repeat synchronization',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = await installed(t, root);
    await runtime.install({
      installationId: 'fixture-new',
      source,
      config: { id: 'site-new', protocol: 'new' },
      version: '1',
      enabled: true,
    });
    assert.deepEqual(await runtime.management('fixture-a'), { protocol: 'website-v1', fields: {} });
    assert.deepEqual(await runtime.management('fixture-new'), {
      protocol: 'website-api-v1',
      fields: { quark: ['cookie'] },
    });
    assert.deepEqual(await runtime.sync('fixture-a', undefined, {
      provider: 'quark', credentials: { cookie: 'old-cookie' },
    }), { status: 'saved', protocol: 'website-v1' });
    assert.deepEqual(await runtime.sync('fixture-new', undefined, {
      provider: 'quark', credentials: { cookie: 'new-cookie' },
    }), { status: 'saved', protocol: 'website-api-v1' });
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: root });
    t.after(() => reopened.close());
    await reopened.start();
    await waitForState(reopened, 'fixture-a', 'ready');
    await waitForState(reopened, 'fixture-new', 'ready');
    const old = await reopened.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'probe', payload: {},
    });
    const modern = await reopened.invoke({
      installationId: 'fixture-new', siteId: 'site-new', operation: 'probe', payload: {},
    });
    assert.equal(old.cookie, 'old-cookie');
    assert.equal(modern.cookie, 'new-cookie');
  });

test('Cat keeps request transport state bounded while session resources are explicitly closed',
  { timeout: 120000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'fixture-a',
      source,
      config: { id: 'site-a', protocol: 'old' },
    });
    const counts = () => ({
      handles: session.node.handles.size,
      callbacks: session.node.callbacks.size,
      callbackReturns: session.node.callbackReturns.size,
      contexts: session.node.contextLeases.size,
      promises: session.node.promiseLeases.size,
      descriptors: session.node.volume.descriptors.size,
      streams: session.node.volume.streams.size,
      networkResources: session.node.networkResources.size,
      nativeRequests: session.nativeRequests.size,
      listeners: session.services.listeners.size,
      scopeTokens: session.services.scopes.size,
      connections: session.services.connections.size,
      agents: session.services.agents.size,
      probes: session.services.probes.size,
    });
    const invoke = operation => session.invoke({
      siteId: 'site-a', operation, payload: {},
    });
    assert.deepEqual(await invoke('cachedNative'), { value: 'https://example.invalid/cached' });
    assert.deepEqual(await invoke('cachedNative'), { value: 'https://example.invalid/cached' });
    assert.equal((await invoke('probe')).responseStream, 'stream-body');
    assert.deepEqual(await invoke('scopedFile'), { written: true });
    assert.deepEqual(await invoke('timerBurst'), { count: 160 });
    assert.deepEqual(await invoke('pipeLifecycle'), {
      unpipe: { data: 0, end: 0, error: 0, drain: 0, finish: 0, close: 0 },
      finish: { data: 0, end: 0, error: 0, drain: 0, finish: 0, close: 0 },
      premature: {
        destroyed: true, data: 0, end: 0, error: 0, sourceClose: 0,
        drain: 0, finish: 0, close: 0,
      },
    });
    assert.match((await invoke('play')).url, /\/proxy\/fixture-a\//);
    const endpoint = session.endpoint();
    assert.equal((await request(endpoint.origin + '/api/site-a/home', {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        [scriptAccessHeader]: endpoint.access,
      },
      body: '{}',
    })).status, 200);
    await new Promise(resolve => setImmediate(resolve));
    await collectGarbage();
    const baseline = counts();
    for (let index = 0; index < 64; index++) {
      assert.deepEqual(await invoke('home'),
        { class: [{ type_id: 'fixture', type_name: 'Fixture' }], filters: {} });
    }
    const parallel = await Promise.all(Array.from({ length: 12 }, () => invoke('parallel')));
    assert.equal(parallel.length, 12);
    assert.ok(Math.max(...parallel.map(value => value.peak)) > 1);
    for (let index = 0; index < 3; index++) {
      assert.equal((await invoke('probe')).responseStream, 'stream-body');
      assert.deepEqual(await invoke('scopedFile'), { written: true });
    }
    for (let index = 0; index < 64; index++)
      assert.match((await invoke('play')).url, /\/proxy\/fixture-a\//);
    for (let index = 0; index < 64; index++) {
      const reply = await request(endpoint.origin + '/api/site-a/home', {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          [scriptAccessHeader]: endpoint.access,
        },
        body: '{}',
      });
      assert.equal(reply.status, 200);
    }
    await new Promise(resolve => setImmediate(resolve));
    await collectGarbage();
    const bounded = counts();
    for (const key of ['handles', 'callbacks', 'callbackReturns', 'contexts', 'promises']) {
      assert.ok(bounded[key] <= baseline[key] * 2 + 16,
        `${key} grew from ${baseline[key]} to ${bounded[key]}`);
    }
    assert.deepEqual({
      descriptors: bounded.descriptors,
      streams: bounded.streams,
      networkResources: bounded.networkResources,
      nativeRequests: bounded.nativeRequests,
      listeners: bounded.listeners,
      scopeTokens: bounded.scopeTokens,
      connections: bounded.connections,
      agents: bounded.agents,
      probes: bounded.probes,
    }, {
      descriptors: baseline.descriptors,
      streams: baseline.streams,
      networkResources: baseline.networkResources,
      nativeRequests: baseline.nativeRequests,
      listeners: baseline.listeners,
      scopeTokens: baseline.scopeTokens,
      connections: baseline.connections,
      agents: baseline.agents,
      probes: baseline.probes,
    });
  });

test('Cat releases request-created value handles and one-shot callbacks under sustained load',
  { timeout: 120000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'value-handles',
      source: `
        const http = require('http'), fs = require('fs'), crypto = require('crypto');
        let server;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/config') {
              response.end(JSON.stringify({ video: { sites: [{
                key: 'value-handles', api: '/api', actions: ['home'],
              }] } }));
              return;
            }
            if (request.url === '/api/home') {
              module.exports.home().then(value => response.end(JSON.stringify(value)),
                error => { response.statusCode = 500; response.end(JSON.stringify({
                  error: error.code || error.message })); });
              return;
            }
            response.end(JSON.stringify({ ok: true }));
          });
          return new Promise(resolve => server.listen(9988, '127.0.0.1', resolve));
        };
        module.exports.stop = () => server?.close();
        const stat = () => new Promise((resolve, reject) =>
          fs.stat('/source.js', (error, value) => error ? reject(error) : resolve(value.size)));
        const fetchSelf = () => new Promise((resolve, reject) => {
          http.get('http://127.0.0.1:9988/echo', response => {
            const parts = [];
            response.on('data', part => parts.push(part));
            response.once('end', () => resolve(Buffer.concat(parts).toString()));
            response.once('error', reject);
          }).once('error', reject);
        });
        module.exports.home = async () => {
          const parsed = new URL('https://example.invalid/path?q=1');
          const digest = crypto.createHash('sha256').update('value').digest('hex').slice(0, 8);
          const size = await stat();
          const body = await fetchSelf();
          return { host: parsed.hostname, digest, size, body };
        };
      `,
    });
    const counts = () => ({
      handles: session.node.handles.size,
      callbacks: session.node.callbacks.size,
      returns: session.node.callbackReturns.size,
      contexts: session.node.contextLeases.size,
      promises: session.node.promiseLeases.size,
    });
    const invoke = () => session.invoke({
      siteId: 'value-handles', operation: 'home', payload: {},
    });
    await collectGarbage();
    const baseline = counts();
    let midpoint;
    for (let index = 0; index < 300; index++) {
      const value = await invoke();
      assert.equal(value.host, 'example.invalid');
      assert.match(value.digest, /^[0-9a-f]{8}$/);
      assert.ok(value.size > 0);
      assert.equal(value.body, '{"ok":true}');
      if (index === 149) {
        await collectGarbage();
        midpoint = counts();
      }
    }
    await collectGarbage();
    const bounded = counts();
    for (const key of Object.keys(baseline)) {
      assert.ok(bounded[key] <= baseline[key] * 2 + 64,
        `${key} grew from ${baseline[key]} to ${bounded[key]} after 300 requests`);
      assert.ok(midpoint[key] <= baseline[key] * 2 + 64,
        `${key} grew from ${baseline[key]} to ${midpoint[key]} after 150 requests`);
    }
  });

test('Cat destroys session-owned native streams when the installation stops',
  { timeout: 30000 }, async t => {
    const sourceWithStream = String.raw`
      const zlib = require('zlib');
      const retained = zlib.createGzip();
      module.exports = {
        home() { return { class: [] }; },
        retained,
      };
    `;
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'session-stream', source: sourceWithStream, config: {},
    });
    const receipt = [...session.node.handles.values()].find(item =>
      item.persistent && item.role === 'stream' && typeof item.value?.flush === 'function');
    assert(receipt);
    const stream = receipt.value;
    assert.equal(stream.destroyed, false);
    await session.stop();
    assert.equal(stream.destroyed, true);
    assert.equal(session.live, false);
  });

test('Cat root-scope pipe and unpipe release their bridge callback receipts immediately',
  { timeout: 30000 }, async t => {
    const makeSource = withPipe => `
      const http = require('http');
      ${withPipe ? `
      const zlib = require('zlib');
      const { PassThrough } = require('stream');
      ` : ''}
      let server;
      module.exports.start = () => {
        ${withPipe ? `
        const source = zlib.createGzip();
        const destination = new PassThrough({ autoDestroy: false });
        destination.resume();
        source.pipe(destination);
        source.unpipe(destination);
        source.destroy();
        destination.destroy();
        ` : ''}
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/full-config' || request.url === '/config') {
            response.end(JSON.stringify({ video: { sites: [{
              key: 'default', name: 'Default', api: '/api/default', actions: ['home'],
            }] } }));
            return;
          }
          response.end(JSON.stringify({ class: [] }));
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server.closeAllConnections(); server.close(); };
    `;
    const pipeControl = await createCatSessionForInternalTest(t, {
      installationId: 'pipe-control', source: makeSource(false), config: {},
    });
    const pipeRoot = await createCatSessionForInternalTest(t, {
      installationId: 'pipe-root', source: makeSource(true), config: {},
    });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(pipeRoot.node.callbacks.size, pipeControl.node.callbacks.size);
  });

test('Cat guest streams pipe through the host without per-chunk QuickJS delivery',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    const bytes = 1024 * 1024;
    await runtime.install({
      installationId: 'stream-pipe-fast', enabled: true, config: {}, version: '1', source: String.raw`
        const http = require('http'), fs = require('fs');
        const size = 1024 * 1024;
        const payload = Buffer.alloc(size, 7);
        let server;
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            if (request.url === '/config' || request.url === '/full-config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'stream-pipe-fast', api: '/stream-pipe-fast',
                actions: ['download'],
              }] } }));
              return;
            }
            if (request.url === '/stream-pipe-fast/download') {
              return new Promise(resolve => {
                const output = fs.createWriteStream('/downloaded.bin');
                let settled = false;
                const finish = value => {
                  if (settled) return;
                  settled = true;
                  resolve(value);
                };
                output.once('finish', () => {
                  const length = fs.statSync('/downloaded.bin').size;
                  setTimeout(() => finish({ length }), 0);
                });
                output.once('error', error => finish({ code: error.code }));
                http.get('http://127.0.0.1:' + process.env.PORT + '/blob', incoming => {
                  incoming.once('error', error => {
                    output.destroy(error);
                    finish({ code: error.code });
                  });
                  incoming.pipe(output);
                }).once('error', error => finish({ code: error.code }));
              }).then(value => {
                if (response.writableEnded) return;
                response.setHeader('content-type', 'application/json');
                response.end(JSON.stringify(value));
              });
            }
            if (request.url === '/blob') {
              response.writeHead(200, {
                'content-type': 'application/octet-stream',
                'content-length': String(size),
              });
              response.end(payload);
              return;
            }
            response.statusCode = 404;
            response.end();
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => server?.close();
      `,
    });
    const result = await runtime.invoke({
      installationId: 'stream-pipe-fast', siteId: 'stream-pipe-fast',
      operation: 'download', payload: {},
    });
    assert.deepEqual(result, { length: bytes });
    await runtime.close();
  });

test('Cat guest stream pipe keeps delivering data to guest listeners while writing the file',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'stream-pipe-observer', enabled: true, config: {}, version: '1', source: String.raw`
        const http = require('http'), fs = require('fs');
        const size = 1024 * 1024;
        const payload = Buffer.alloc(size, 3);
        let server;
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            if (request.url === '/config' || request.url === '/full-config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'stream-pipe-observer', api: '/stream-pipe-observer',
                actions: ['download'],
              }] } }));
              return;
            }
            if (request.url === '/stream-pipe-observer/download') {
              return new Promise(resolve => {
                const output = fs.createWriteStream('/observed.bin');
                let observed = 0;
                output.once('finish', () => resolve({
                  length: fs.statSync('/observed.bin').size, observed,
                }));
                output.once('error', error => resolve({ code: error.code }));
                http.get('http://127.0.0.1:' + process.env.PORT + '/blob', incoming => {
                  incoming.on('data', chunk => { observed += chunk.length; });
                  incoming.once('error', error => output.destroy(error));
                  incoming.pipe(output);
                }).once('error', error => resolve({ code: error.code }));
              }).then(value => {
                if (response.writableEnded) return;
                response.setHeader('content-type', 'application/json');
                response.end(JSON.stringify(value));
              });
            }
            if (request.url === '/blob') {
              const body = Buffer.alloc(size, 3);
              response.writeHead(200, {
                'content-type': 'application/octet-stream',
                'content-length': String(size),
              });
              response.end(body);
              return;
            }
            response.statusCode = 404;
            response.end();
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => server?.close();
      `,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'stream-pipe-observer', siteId: 'stream-pipe-observer',
      operation: 'download', payload: {},
    }), { length: 1024 * 1024, observed: 1024 * 1024 });
    await runtime.close();
  });

test('Cat guest SSE streams support data listeners, async iteration and fetch readers',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'guest-sse', enabled: true, config: {}, version: '1', source: String.raw`
        const http = require('http');
        let server;
        const endpoint = '/guest-sse/sse';
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            if (request.url === '/config' || request.url === '/full-config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'guest-sse', api: '/guest-sse',
                actions: ['events', 'iterate', 'reader'],
              }] } }));
              return;
            }
            if (request.url === endpoint) {
              response.writeHead(200, { 'content-type': 'text/event-stream' });
              const chunks = [
                'data: {"n":1}\n\n', 'data: {"n":2}\n\n',
                'data: {"n":3}\n\n', 'data: [DONE]\n\n',
              ];
              let index = 0;
              const timer = setInterval(() => {
                response.write(chunks[index++]);
                if (index === chunks.length) {
                  clearInterval(timer);
                  response.end();
                }
              }, 10);
              request.once('close', () => clearInterval(timer));
              return;
            }
            const action = request.url.split('/').pop();
            if (action === 'events') return getEvents(response);
            if (action === 'iterate') return iterateEvents(response);
            if (action === 'reader') return readEvents(response);
            response.statusCode = 404;
            response.end();
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        const url = () => 'http://127.0.0.1:' + process.env.PORT + endpoint;
        const send = (response, value) => {
          if (response.writableEnded) return;
          response.setHeader('content-type', 'application/json');
          response.end(JSON.stringify(value));
        };
        const getEvents = response => {
          const chunks = [];
          http.get(url(), incoming => {
            incoming.setEncoding('utf8');
            incoming.on('data', chunk => chunks.push(chunk));
            incoming.once('end', () => send(response, { text: chunks.join('') }));
            incoming.once('error', error => send(response, { error: error.code }));
          }).once('error', error => send(response, { error: error.code }));
        };
        const iterateEvents = async response => {
          const chunks = [];
          const incoming = await new Promise((resolve, reject) =>
            http.get(url(), resolve).once('error', reject));
          incoming.setEncoding('utf8');
          for await (const chunk of incoming) chunks.push(chunk);
          send(response, { text: chunks.join('') });
        };
        const readEvents = async response => {
          const fetched = await fetch(url());
          const reader = fetched.body.getReader();
          const decoder = new TextDecoder();
          let text = '';
          for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            text += decoder.decode(value, { stream: true });
          }
          text += decoder.decode();
          send(response, { text });
        };
        module.exports.stop = () => server?.close();
      `,
    });
    const expected = 'data: {"n":1}\n\ndata: {"n":2}\n\ndata: {"n":3}\n\ndata: [DONE]\n\n';
    for (const operation of ['events', 'iterate', 'reader']) {
      assert.deepEqual(await runtime.invoke({
        installationId: 'guest-sse', siteId: 'guest-sse', operation, payload: {},
      }), { text: expected });
    }
    await runtime.close();
  });

test('Cat closed outbound streams remain usable while their guest object or method is retained',
  { timeout: 30000 }, async t => {
    const upstream = await serverFor(t, (_request, response) => {
      response.setHeader('content-type', 'text/event-stream');
      response.write('data: first\n\n');
      const timer = setTimeout(() => response.end('data: last\n\n'), 20);
      response.once('close', () => clearTimeout(timer));
    });
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'closed-stream',
      source: `
        const http = require('http');
        let server, retained, destroy;
        const read = async mode => {
          const incoming = await new Promise((resolve, reject) =>
            http.get(${JSON.stringify(upstream.socketOrigin)}, resolve).once('error', reject));
          incoming.setEncoding('utf8');
          let text = '';
          try {
            for await (const chunk of incoming) {
              text += chunk;
              if (mode === 'break') break;
            }
          } finally { incoming.destroy(); }
          retained = incoming;
          destroy = incoming.destroy;
          return { text, destroyed: incoming.destroyed, status: incoming.statusCode };
        };
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer((request, response) => {
            const send = value => response.end(JSON.stringify(value));
            if (request.url === '/config' || request.url === '/full-config') {
              send({video:{sites:[{key:'closed-stream',api:'/stream',
                actions:['end','break','inspect','method','drop']}]}});
              return;
            }
            const action = request.url.split('/').pop();
            if (action === 'inspect') {
              retained.destroy();
              send({destroyed:retained.destroyed, status:retained.statusCode,
                ended:retained.readableEnded, closed:retained.closed});
              return;
            }
            if (action === 'method') {
              retained = null;
              setImmediate(() => { destroy(); destroy = null; send({ok:true}); });
              return;
            }
            if (action === 'drop') { retained = destroy = null; send({ok:true}); return; }
            read(action).then(send, error => {response.statusCode=500;send({code:error.code});});
          });
          server.listen(9988, '127.0.0.1', resolve);
        });
        module.exports.stop = () => server.close();
      `,
    });
    const invoke = operation => session.invoke({
      siteId: 'closed-stream', operation, payload: {},
    });
    assert.deepEqual(await invoke('end'), {
      text: 'data: first\n\ndata: last\n\n', destroyed: true, status: 200,
    });
    await collectGarbage();
    assert.deepEqual(await invoke('inspect'), {
      destroyed: true, status: 200, ended: true, closed: true,
    });
    assert.deepEqual(await invoke('method'), { ok: true });
    const broken = await invoke('break');
    assert.ok(broken.text.startsWith('data: first\n\n'));
    assert.equal(broken.destroyed, true);
    assert.equal(broken.status, 200);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal((await invoke('inspect')).destroyed, true);
    assert.deepEqual(await invoke('drop'), { ok: true });
    await session.stop();
    assert.equal(session.node.handles.size, 0);
    assert.equal(session.nativeRequests.size, 0);
  });

test('Cat guest self requests reach the current generation listeners on both endpoints',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'self-endpoint', enabled: true, config: {}, version: '1', source: String.raw`
        const http = require('http');
        let main, second;
        module.exports.start = () => new Promise(resolve => {
          main = http.createServer((request, response) => {
            if (request.url === '/config' || request.url === '/full-config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'self-endpoint', api: '/self-endpoint', actions: ['second', 'main'],
              }] } }));
              return;
            }
            response.end(JSON.stringify({ path: request.url, main: true }));
          });
          second = http.createServer((_request, response) => response.end(JSON.stringify({
            second: true,
          })));
          let listening = 0;
          const ready = () => { if (++listening === 2) resolve(); };
          main.listen(Number(process.env.PORT), '127.0.0.1', ready);
          second.listen(Number(process.env.PORT) + 1, '127.0.0.1', ready);
        });
        const request = port => new Promise((resolve, reject) => {
          http.get('http://127.0.0.1:' + port + '/', incoming => {
            const chunks = [];
            incoming.on('data', chunk => chunks.push(chunk));
            incoming.once('end', () => resolve(JSON.parse(Buffer.concat(chunks).toString())));
            incoming.once('error', reject);
          }).once('error', reject);
        });
        module.exports.main = () => request(Number(process.env.PORT));
        module.exports.second = () => request(Number(process.env.PORT) + 1);
        module.exports.stop = () => { main?.close(); second?.close(); };
      `,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'self-endpoint', siteId: 'self-endpoint',
      operation: 'main', payload: {},
    }), { path: '/self-endpoint/main', main: true });
    const second = await runtime.invoke({
      installationId: 'self-endpoint', siteId: 'self-endpoint',
      operation: 'second', payload: {},
    });
    assert.ok(second.main || second.second);
    await runtime.close();
  });

test('Cat cancels and unlocks session-owned native Web streams when the installation stops',
  { timeout: 30000 }, async t => {
    const sourceWithWebStreams = String.raw`
      const http = require('http');
      const streams = require('stream/web');
      const readable = new streams.ReadableStream({ start() {} });
      const reader = readable.getReader();
      const writable = new streams.WritableStream({ write() {} });
      const writer = writable.getWriter();
      let server;
      module.exports = {
        readable, reader, writable, writer,
        start() {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/full-config' || request.url === '/config') {
              response.end(JSON.stringify({ video: { sites: [{
                key: 'web-streams', name: 'Web streams',
                api: '/api/web-streams', actions: ['home'],
              }] } }));
              return;
            }
            response.end(JSON.stringify({ class: [] }));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
          return server;
        },
        stop() { server.closeAllConnections(); server.close(); },
      };
    `;
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'session-web-streams', source: sourceWithWebStreams, config: {},
    });
    const values = Object.fromEntries([...session.node.handles.values()]
      .filter(receipt => ['readableWeb', 'reader', 'writableWeb', 'writer'].includes(receipt.role))
      .map(receipt => [receipt.role, receipt.value]));
    assert.deepEqual(Object.keys(values).sort(), ['readableWeb', 'reader', 'writableWeb', 'writer'].sort());
    assert.equal(values.readableWeb.locked, true);
    assert.equal(values.writableWeb.locked, true);
    await session.stop();
    assert.equal(values.readableWeb.locked, false);
    assert.equal(values.writableWeb.locked, false);
    assert.equal(session.node.handles.size, 0);
    assert.equal(session.node.cleanupTasks.size, 0);
  });

test('Cat invocation uses one settings snapshot while a refresh publishes the next turn',
  { timeout: 30000 }, async t => {
    let enteredResolve, releaseResolve;
    const entered = new Promise(resolve => { enteredResolve = resolve; });
    const release = new Promise(resolve => { releaseResolve = resolve; });
    let calls = 0;
    const runtime = await installed(t, rootFor(t), {
      network: async () => {
        calls++;
        enteredResolve();
        await release;
        return {
          statusCode: 200,
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ status: 200, code: 0, data: { stoken: 'upstream' } }),
        };
      },
    });
    const status = await runtime.status();
    const pending = runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'pan', payload: {},
    });
    await entered;
    writeSettings(status.settingsPath, value => ({
      ...value,
      providers: { ...value.providers, quark: true },
      mappings: [{ provider: 'quark', aliases: ['QuarkAlias'] }],
    }));
    await runtime.refresh();
    releaseResolve();
    const first = await pending;
    assert.equal(calls, 1);
    assert.equal(first._meow, undefined);
    assert.match(first.list[0].vod_play_url, /https:\/\/pan\.quark\.cn\/s\/abcd1234/);

    const second = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'pan', payload: {},
    });
    assert.equal(calls, 1);
    assert.match(second.list[0].vod_play_url, /meow\.pan\.v1\./);
  });

test('Cat destroys an injected adapter request before a late network completion can re-enter the guest',
  { timeout: 30000 }, async t => {
    let enteredResolve, releaseResolve;
    const entered = new Promise(resolve => { enteredResolve = resolve; });
    const release = new Promise(resolve => { releaseResolve = resolve; });
    const runtime = await installed(t, rootFor(t), {
      network: input => {
        enteredResolve(input);
        return release;
      },
    });
    const controller = new AbortController();
    const pending = runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'panOpaque', payload: {},
    }, { signal: controller.signal });
    const outbound = await entered;
    controller.abort();
    await assert.rejects(pending, { code: 'CANCELLED' });
    assert.equal(outbound.signal.aborted, true);
    releaseResolve({
      statusCode: 200,
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ status: 200, code: 0, data: { stoken: 'late' } }),
    });
    await waitFor(() => runtime.host.get('fixture-a').resources.pendingAdapterTasks === 0,
      'released adapter did not settle its physical task');
    const resources = runtime.host.get('fixture-a').resources;
    assert.equal(resources.pendingAdapterTasks, 0);
    assert.deepEqual(await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'home', payload: {},
    }), { class: [{ type_id: 'fixture', type_name: 'Fixture' }], filters: {} });
  });

test('Cat keeps outbound responses and a request-created Agent alive after a normal return',
  { timeout: 30000 }, async t => {
    const responses = [];
    const upstream = await serverFor(t, (_request, response) => {
      responses.push(response);
      response.writeHead(200);
      response.write('first');
    });
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'outbound', enabled: true,
      source: `
        const http = require('http');
        let server, agent, incoming, complete = false, body = '';
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            const send = value => response.end(JSON.stringify(value));
            if (request.url === '/config' || request.url === '/full-config')
              return send({video:{sites:[{key:'outbound',api:'/api',
                actions:['home','state','again']}]}});
            if (request.url.endsWith('/state'))
              return send({code:complete ? null : incoming.statusCode, complete, body});
            if (!agent) agent = new http.Agent({keepAlive:true});
            http.get(${JSON.stringify(upstream.socketOrigin)}, {agent}, async input => {
              incoming = input;
              input.on('data', chunk => {body += chunk.toString();});
              send({code:input.statusCode});
              await new Promise(resolve => input.once('end', resolve));
              complete = true;
            }).on('error', error => send({code:error.code}));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => {server.closeAllConnections();server.close();};
      `,
    });
    const invoke = operation => runtime.invoke({
      installationId: 'outbound', siteId: 'outbound', operation, payload: {},
    });
    assert.deepEqual(await invoke('home'), { code: 200 });
    assert.equal((await invoke('state')).code, 200);
    assert.equal(responses[0].destroyed, false);
    responses[0].end('last');
    let state;
    const deadline = Date.now() + 5000;
    do {
      state = await invoke('state');
      assert.ok(Date.now() < deadline, 'outbound body did not finish after the invocation returned');
      if (!state.complete) await new Promise(resolve => setTimeout(resolve, 10));
    } while (!state.complete);
    assert.equal(state.body, 'firstlast');
    assert.deepEqual(await invoke('again'), { code: 200 });
    assert.equal(responses.length, 2);
    assert.equal(responses[1].destroyed, false);
    await runtime.close();
    await waitFor(() => responses[1].destroyed, 'session stop did not close its outbound transport');
  });

test('Cat retains an in-flight adapter after a normal return and aborts it when the session stops',
  { timeout: 30000 }, async t => {
    const requests = [];
    const runtime = createCatRuntime({
      dataRoot: rootFor(t),
      network: input => new Promise(resolve => {
        requests.push({ input, resolve });
        input.signal.addEventListener('abort', () =>
          resolve({ statusCode: 200, headers: {}, body: 'after-stop' }), { once: true });
      }),
    });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'outbound-adapter', enabled: true,
      source: `
        const http = require('http');
        let server, delayed, result = null;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            const send = value => response.end(JSON.stringify(value));
            if (request.url === '/config' || request.url === '/full-config')
              return send({video:{sites:[{key:'outbound',api:'/api',
                actions:['home','state','prepare','send','error']}]}});
            if (request.url.endsWith('/state')) return send({result});
            if (request.url.endsWith('/send')) {delayed.end(); return send({sent:true});}
            const download = http.request('http://fixture.invalid/background', input => {
              let body = '';
              input.on('data', chunk => {body += chunk.toString();});
              input.on('end', () => {result = body;});
            }).on('error', error => {result = error.code;});
            if (request.url.endsWith('/prepare')) delayed = download;
            else download.end();
            if (request.url.endsWith('/error')) response.statusCode = 503;
            send({started:true});
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => {server.closeAllConnections();server.close();};
      `,
    });
    const invoke = operation => runtime.invoke({
      installationId: 'outbound-adapter', siteId: 'outbound', operation, payload: {},
    });
    assert.deepEqual(await invoke('home'), { started: true });
    await waitFor(() => requests.length === 1, 'background adapter was not called');
    assert.equal(requests[0].input.signal.aborted, false);
    requests[0].resolve({ statusCode: 200, headers: {}, body: 'late-body' });
    let state;
    const deadline = Date.now() + 5000;
    do {
      state = await invoke('state');
      assert.ok(Date.now() < deadline, 'background adapter did not complete');
      if (state.result === null) await new Promise(resolve => setTimeout(resolve, 10));
    } while (state.result === null);
    assert.deepEqual(state, { result: 'late-body' });
    await assert.rejects(invoke('error'), { code: 'UPSTREAM_HTTP_ERROR' });
    await waitFor(() => requests.length === 2, 'error response did not start its adapter');
    assert.equal(requests[1].input.signal.aborted, false);
    assert.deepEqual(await invoke('prepare'), { started: true });
    assert.equal(requests.length, 2);
    assert.deepEqual(await invoke('send'), { sent: true });
    await waitFor(() => requests.length === 3, 'delayed background adapter was not called');
    assert.equal(requests[2].input.signal.aborted, false);
    await runtime.close();
    assert.equal(requests.slice(1).every(entry => entry.input.signal.aborted), true);
  });

test('Cat self-requests retain their scoped listener token after the caller returns',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'self-background', enabled: true,
      source: `
        const http = require('http');
        let server, pending, result = null;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            const send = value => response.end(JSON.stringify(value));
            if (request.url === '/config' || request.url === '/full-config')
              return send({video:{sites:[{key:'self',api:'/api',actions:['home','state','flush']}]}});
            if (request.url === '/pending') {pending = response; return;}
            if (request.url.endsWith('/state')) return send({pending:!!pending,result});
            if (request.url.endsWith('/flush')) {pending.end('self-body'); return send({sent:true});}
            http.get('http://127.0.0.1:9988/pending', input => {
              let body = '';
              input.on('data', chunk => {body += chunk.toString();});
              input.on('end', () => {result = body;});
            }).on('error', error => {result = error.code;});
            send({started:true});
          });
          server.listen(9988, '127.0.0.1');
        };
        module.exports.stop = () => {server.closeAllConnections();server.close();};
      `,
    });
    const invoke = operation => runtime.invoke({
      installationId: 'self-background', siteId: 'self', operation, payload: {},
    });
    assert.deepEqual(await invoke('home'), { started: true });
    let state;
    const deadline = Date.now() + 5000;
    do {
      state = await invoke('state');
      assert.ok(Date.now() < deadline, 'self request did not reach the listener after return');
      if (!state.pending) await new Promise(resolve => setTimeout(resolve, 10));
    } while (!state.pending);
    assert.deepEqual(await invoke('flush'), { sent: true });
    do {
      state = await invoke('state');
      assert.ok(Date.now() < deadline, 'self request did not finish');
      if (state.result === null) await new Promise(resolve => setTimeout(resolve, 10));
    } while (state.result === null);
    assert.equal(state.result, 'self-body');
  });

test('Cat guest pipes 1, 4 and 8 MiB upstream responses into private-volume files',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const { runtime, root } = await startStreamBridge(t, upstream);
    const sizes = [1, 4, 8].map(mib => mib * 1024 * 1024);
    for (const size of sizes) {
      const bytes = upstream.blob(size);
      const result = await scriptRequest(runtime.getSession('bridge').endpoint(),
        '/bridge/download?size=' + size);
      assert.equal(result.status, 200);
      assert.deepEqual(JSON.parse(result.text), { size, length: size });
      const target = join(root, 'volumes', 'bridge', 'download-' + size + '.bin');
      assert.equal(readFileSync(target).length, size);
      assert.equal(sha256(readFileSync(target)), sha256(bytes));
    }
  });

test('Cat guest proxies 1, 4 and 8 MiB upstream responses without resetting the client',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const { runtime, endpoint } = await startStreamBridge(t, upstream);
    const sizes = [1, 4, 8].map(mib => mib * 1024 * 1024);
    for (const size of sizes) {
      const bytes = upstream.blob(size);
      const result = await scriptRequest(endpoint, '/bridge/proxy?size=' + size);
      assert.equal(result.status, 200);
      assert.equal(result.bytes.length, size, size + ' byte proxy length');
      assert.equal(sha256(result.bytes), sha256(bytes), size + ' byte proxy digest');
    }
    assert.equal(upstream.reads.length, 0);
  });

test('Cat guest data listeners preserve 1, 4 and 8 MiB while writing to responses and files',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const { runtime, root } = await startStreamBridge(t, upstream);
    const sizes = [1, 4, 8].map(mib => mib * 1024 * 1024);
    const endpoint = runtime.getSession('bridge').endpoint();
    for (const size of sizes) {
      const bytes = upstream.blob(size);
      const proxied = await scriptRequest(endpoint, '/bridge/dataProxy?size=' + size);
      assert.equal(proxied.status, 200);
      assert.equal(sha256(proxied.bytes), sha256(bytes), size + ' byte data proxy digest');
      const written = await scriptRequest(endpoint, '/bridge/dataFile?size=' + size);
      assert.equal(written.status, 200);
      assert.deepEqual(JSON.parse(written.text), { size, length: size, bytes: size });
      const target = join(root, 'volumes', 'bridge', 'data-' + size + '.bin');
      assert.equal(sha256(readFileSync(target)), sha256(bytes),
        size + ' byte data file digest');
    }
  });

test('Cat guest pipe keeps an existing data listener complete while writing the target',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const { runtime, root, endpoint } = await startStreamBridge(t, upstream);
    const size = 4 * 1024 * 1024;
    const bytes = upstream.blob(size);
    const result = await scriptRequest(endpoint, '/bridge/observed?size=' + size);
    assert.equal(result.status, 200);
    assert.deepEqual(JSON.parse(result.text), { size, length: size, observed: size });
    const target = join(root, 'volumes', 'bridge', 'observed.bin');
    assert.equal(sha256(readFileSync(target)), sha256(bytes));
  });

test('Cat guest proxy releases upstream work when the client disconnects and stays usable',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const root = rootFor(t);
    const size = 16 * 1024 * 1024;
    upstream.slow(size);
    const session = await createCatSessionForInternalTest(t, {
      dataRoot: root,
      installationId: 'bridge',
      source: streamBridgeSource(upstream.origin),
      network: networkForLocalServers([upstream]),
    });
    const endpoint = session.endpoint();
    const client = http.get(endpoint.origin + '/bridge/slowProxy?size=' + size, {
      agent: false,
      headers: { host: new URL(endpoint.origin).host,
        'x-meow-script-access': endpoint.access },
    });
    const closed = new Promise((resolve, reject) => {
      let dataSeen = false;
      client.once('error', error => {
        if (error.code === 'ECONNRESET' && dataSeen) resolve();
        else reject(error);
      });
      client.once('close', () => {
        if (dataSeen) resolve();
        else reject(new Error('client closed before receiving proxy data'));
      });
      client.once('response', response => {
        response.once('error', () => {});
        response.once('data', () => { dataSeen = true; client.destroy(); });
        response.once('end', () => reject(new Error('slow proxy completed before disconnect')));
      });
    });
    await closed;
    await waitFor(() => upstream.reads.length === 1,
      'slow upstream request was not established');
    await waitFor(() => upstream.reads[0].response.destroyed,
      'disconnected upstream response was not released');
    await waitFor(() => session.node.volume.streams.size === 0 &&
      session.node.callbackReturns.size === 0 && session.nativeRequests.size === 0,
    'guest request resources were not released after disconnect');
    const next = await scriptRequest(endpoint, '/bridge/ok');
    assert.equal(next.status, 200);
    assert.deepEqual(JSON.parse(next.text), { ok: true });
    assert.equal(session.live, true);
  });

test('Cat guest streams 4 and 12 MiB response buffers and returns a 12 MiB call result',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const { runtime, endpoint } = await startStreamBridge(t, upstream, {
      files: {
        'end-4194304.bin': upstream.blob(4 * 1024 * 1024),
        'end-12582912.bin': upstream.blob(12 * 1024 * 1024),
      },
    });
    for (const size of [4, 12].map(mib => mib * 1024 * 1024)) {
      const bytes = upstream.blob(size);
      const result = await scriptRequest(endpoint, '/bridge/endStream?size=' + size);
      assert.equal(result.status, 200);
      assert.equal(result.headers['content-length'], String(size));
      assert.equal(result.bytes.length, size);
      assert.equal(sha256(result.bytes), sha256(bytes));
    }
    const returned = await runtime.invoke({
      installationId: 'bridge', siteId: 'bridge', operation: 'largeResult', payload: {},
    });
    assert.equal(returned.data.length, 12 * 1024 * 1024);
    assert.equal(sha256(returned.data), sha256('x'.repeat(12 * 1024 * 1024)));
    assert.deepEqual(await runtime.invoke({
      installationId: 'bridge', siteId: 'bridge', operation: 'ok', payload: {},
    }), { ok: true });
    const next = await scriptRequest(endpoint, '/bridge/ok');
    assert.equal(next.status, 200);
    assert.deepEqual(JSON.parse(next.text), { ok: true });
  });

test('Cat listener response above the injected memory bound fails only that request',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const root = rootFor(t);
    const runtime = createCatRuntime({
      dataRoot: root, network: networkForLocalServers([upstream]),
      limits: { maxMemoryBytes: 16 * 1024 * 1024 },
    });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'bridge',
      source: streamBridgeSource(upstream.origin, sha256(upstream.values)),
      config: {}, version: '1', enabled: true,
    });
    const size = 17 * 1024 * 1024;
    upstream.blob(size);
    await assert.rejects(runtime.invoke({
      installationId: 'bridge', siteId: 'bridge', operation: 'proxy',
      payload: { size },
    }), { code: 'RESOURCE_LIMIT' });
    assert.equal((await runtime.status()).instances[0].state, 'ready');
    assert.deepEqual(await runtime.invoke({
      installationId: 'bridge', siteId: 'bridge', operation: 'ok', payload: {},
    }), { ok: true });
  });

test('Cat aggregate entry accepts a 12 MiB request body and bounds it by maxMemoryBytes',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root, network: networkForLocalServers([upstream]) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'bridge',
      source: streamBridgeSource(upstream.origin, sha256(upstream.values)),
      config: {}, version: '1', enabled: true,
    });
    const status = await runtime.status();
    const path = '/cat/bridge/bridge/body';
    const dataLength = 12 * 1024 * 1024 -
      Buffer.byteLength(JSON.stringify({ data: '' }));
    const data = 'y'.repeat(dataLength);
    const accepted = await request(status.aggregate.origin + path, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-meow-runtime-access': status.aggregate.access,
      },
      body: JSON.stringify({ data }),
    });
    assert.equal(accepted.status, 200);
    assert.deepEqual(JSON.parse(accepted.text), {
      length: 12 * 1024 * 1024, dataLength,
    });

    const boundedRoot = rootFor(t);
    const bounded = createCatRuntime({
      dataRoot: boundedRoot, network: networkForLocalServers([upstream]),
      limits: { maxMemoryBytes: 16 * 1024 * 1024 },
    });
    t.after(() => bounded.close());
    await bounded.install({
      installationId: 'bridge',
      source: streamBridgeSource(upstream.origin, sha256(upstream.values)),
      config: {}, version: '1', enabled: true,
    });
    const boundedStatus = await bounded.status();
    const rejected = await request(boundedStatus.aggregate.origin + path, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-meow-runtime-access': boundedStatus.aggregate.access,
      },
      body: JSON.stringify({ data: 'z'.repeat(17 * 1024 * 1024) }),
    }).catch(error => ({ status: null, error: error.code }));
    assert.equal(rejected.status, 413);
    assert.equal(rejected.error, undefined);
    assert.deepEqual(JSON.parse(rejected.text), { error: { code: 'RESOURCE_LIMIT' } });
    const next = await request(boundedStatus.aggregate.origin + path, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-meow-runtime-access': boundedStatus.aggregate.access,
      },
      body: JSON.stringify({ data: 'ok' }),
    });
    assert.equal(next.status, 200);
    assert.deepEqual(JSON.parse(next.text), {
      length: Buffer.byteLength(JSON.stringify({ data: 'ok' })), dataLength: 2,
    });
  });

test('Cat aggregate invocation is admitted under the 60 second request deadline',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = await installed(t, root);
    const aggregate = (await runtime.status()).aggregate;
    const pending = request(aggregate.origin + '/cat/fixture-a/site-a/home', {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        'x-meow-runtime-access': aggregate.access,
      },
      body: JSON.stringify({ wd: 'wait' }),
    });
    assert.equal((await pending).status, 200);
    assert.equal((await runtime.status()).instances[0].error, null);
  });

test('Cat guest byte frames never expose host-owned buffers or mutate host data',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const { endpoint } = await startStreamBridge(t, upstream);
    const capture = await scriptRequest(endpoint, '/bridge/sharedCapture');
    assert.equal(capture.status, 200);
    assert.deepEqual(JSON.parse(capture.text), {
      length: upstream.shared.length, hash: sha256(upstream.shared), buffer: true,
      guestPrototype: true,
    });
    const before = await scriptRequest(endpoint, '/bridge/sharedCheck');
    assert.deepEqual(JSON.parse(before.text), {
      hash: sha256(upstream.shared), first: upstream.shared[0], buffer: true,
    });
    const mutate = await scriptRequest(endpoint, '/bridge/sharedMutate');
    assert.deepEqual(JSON.parse(mutate.text), {
      first: upstream.shared[0], changed: upstream.shared[0] ^ 0xff,
    });
    assert.equal(upstream.shared[0], 255);
    const guestValue = Buffer.from(upstream.shared);
    guestValue[0] ^= 0xff;
    upstream.shared[0] ^= 0x0f;
    const after = await scriptRequest(endpoint, '/bridge/sharedCheck');
    assert.deepEqual(JSON.parse(after.text), {
      hash: sha256(guestValue), first: guestValue[0], buffer: true,
    });
    const digest = await scriptRequest(endpoint, '/bridge/arrayCapture');
    assert.equal(digest.status, 200);
    const digestValue = JSON.parse(digest.text);
    assert.equal(digestValue.hash, sha256(guestValue));
    assert.equal(digestValue.type, true);
    assert.equal(digestValue.view, false);
    assert.equal(digestValue.guestPrototype, true);
    assert.equal(digestValue.length, 32);
    const mutatedDigest = await scriptRequest(endpoint, '/bridge/arrayMutate');
    assert.equal(mutatedDigest.status, 200);
    const mutated = JSON.parse(mutatedDigest.text);
    assert.equal(mutated.length, 32);
    assert.equal(mutated.changed, mutated.first ^ 0xff);
    const digestAgain = await scriptRequest(endpoint, '/bridge/arrayCapture');
    assert.equal(JSON.parse(digestAgain.text).hash, sha256(guestValue));
  });

test('Cat guest callbacks, native call arguments and native results preserve the full byte range',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const { runtime, root } = await startStreamBridge(t, upstream, {
      files: { 'values.bin': upstream.values, 'empty.bin': Buffer.alloc(0) },
    });
    const endpoint = runtime.getSession('bridge').endpoint();
    const callback = await scriptRequest(endpoint, '/bridge/bytesCallback');
    assert.equal(callback.status, 200);
    assert.deepEqual(JSON.parse(callback.text), {
      length: 256, same: true, buffers: true,
      empty: { length: 0, buffer: true },
      subview: { length: 256, same: true },
    });

    const args = await scriptRequest(endpoint, '/bridge/bytesCallArguments');
    assert.equal(args.status, 200);
    assert.deepEqual(JSON.parse(args.text), {
      viewOffset: 16, viewLength: 256, arrayBuffer: true,
    });
    const volume = join(root, 'volumes', 'bridge');
    for (const name of ['call-values.bin', 'call-subview.bin'])
      assert.equal(sha256(readFileSync(join(volume, name))), sha256(upstream.values), name);
    assert.equal(readFileSync(join(volume, 'call-empty.bin')).length, 0);
    const compressed = readFileSync(join(volume, 'call-arraybuffer.bin'));
    assert.equal(inflateSync(compressed).equals(upstream.values), true);

    const returns = await scriptRequest(endpoint, '/bridge/bytesReturns');
    assert.equal(returns.status, 200);
    const value = JSON.parse(returns.text);
    assert.deepEqual(value, {
      bufferHash: sha256(upstream.values), buffer: true,
      emptyLength: 0, emptyBuffer: true, inflated: true,
      digestHash: sha256(upstream.values), digestType: true, digestView: false,
      digestLength: 32, expectedDigest: sha256(upstream.values),
    });
  });

test('Cat guest reads a 40 MiB private-volume file and processes 12 MiB buffers in memory',
  { timeout: 60000 }, async t => {
    const size = 40 * 1024 * 1024;
    const work = 12 * 1024 * 1024;
    const bytes = deterministicBytes(size, 0x13579bdf);
    const root = rootFor(t);
    const volumeRoot = join(root, 'volumes', 'large-read');
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'large-read', enabled: true, config: {}, version: '1', source: String.raw`
        const http = require('http'), fs = require('fs'), crypto = require('crypto'), zlib = require('zlib');
        let server;
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer(async (request, response) => {
            if (request.url === '/config' || request.url === '/full-config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'large-read', name: 'Large read', api: '/large-read',
                actions: ['read'],
              }] } }));
              return;
            }
            if (request.url === '/large-read/read') {
              const promised = await fs.promises.readFile('/large.bin');
              const synchronous = fs.readFileSync('/large.bin');
              const digest = value => crypto.createHash('sha256').update(value).digest('hex');
              const buffer = Buffer.alloc(12 * 1024 * 1024, 0x5a);
              const compressed = zlib.deflateSync(buffer);
              const inflated = zlib.inflateSync(compressed);
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({
                promiseLength: promised.length, promiseHash: digest(promised),
                promiseBuffer: Buffer.isBuffer(promised),
                syncLength: synchronous.length, syncHash: digest(synchronous),
                syncBuffer: Buffer.isBuffer(synchronous),
                same: promised.equals(synchronous),
                workLength: buffer.length, workHash: digest(buffer),
                workCompressed: compressed.length, workInflated: inflated.equals(buffer),
              }));
              return;
            }
            response.statusCode = 404;
            response.end();
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => server?.close();
      `,
    });
    writeFileSync(join(volumeRoot, 'large.bin'), bytes);
    const result = await runtime.invoke({
      installationId: 'large-read', siteId: 'large-read', operation: 'read', payload: {},
    });
    assert.equal(result.promiseLength, size);
    assert.equal(result.promiseHash, sha256(bytes));
    assert.equal(result.promiseBuffer, true);
    assert.equal(result.syncLength, size);
    assert.equal(result.syncHash, sha256(bytes));
    assert.equal(result.syncBuffer, true);
    assert.equal(result.same, true);
    const buffer = Buffer.alloc(work, 0x5a);
    assert.equal(result.workLength, work);
    assert.equal(result.workHash, sha256(buffer));
    assert.ok(result.workCompressed > 0 && result.workCompressed < work);
    assert.equal(result.workInflated, true);
    assert.equal(sha256(readFileSync(join(volumeRoot, 'large.bin'))), sha256(bytes));
  });

test('Cat keeps an async listener continuation after its request transport is released',
  { timeout: 30000 }, async t => {
    const lateSource = String.raw`
      const http = require('http');
      let server, interval, release;
      module.exports.start = () => {
        interval = setInterval(() => {
          const next = release;
          release = null;
          next?.();
        }, 100);
        server = http.createServer(async (request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/full-config' || request.url === '/config') {
            response.end(JSON.stringify({ video: { sites: [{
              key: 'late-context', name: 'Late context',
              api: '/api/late-context', actions: ['home'],
            }] } }));
            return;
          }
          if (request.url === '/api/late-context/home') {
            response.end(JSON.stringify({ class: [] }));
            await new Promise(resolve => { release = resolve; });
            return 'late';
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => {
        clearInterval(interval);
        server.closeAllConnections();
        server.close();
      };
    `;
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'late-context', source: lateSource, config: {},
    });
    assert.deepEqual(await session.invoke({
      siteId: 'late-context', operation: 'home', payload: {},
    }), { class: [] });
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(session.live, true);
    assert.equal(session.node.callbackReturns.size, 0);
    assert.equal(session.node.contextLeases.size, 0);
    assert.deepEqual(await session.invoke({
      siteId: 'late-context', operation: 'home', payload: {},
    }), { class: [] });
  });

test('Cat keeps a background preload Promise across requests', { timeout: 30000 }, async t => {
  const session = await createCatSessionForInternalTest(t, {
    installationId: 'background-preload',
    source: String.raw`
      const http = require('http');
      let server, pending = null, finished = false;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config')
            return response.end(JSON.stringify({ video: { sites: [{
              key: 'preload', api: '/api/preload', actions: ['startPreload', 'wait'],
            }] } }));
          if (request.url === '/api/preload/startPreload') {
            pending ??= new Promise(resolve => setTimeout(() => {
              finished = true;
              resolve('loaded');
            }, 60));
            return response.end(JSON.stringify({ started: true, finished }));
          }
          pending.then(value => response.end(JSON.stringify({ finished, value })));
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server.closeAllConnections(); server.close(); };
    `,
  });
  const invoke = operation => session.invoke({
    siteId: 'preload', operation, payload: {},
  });
  assert.deepEqual(await invoke('startPreload'), { started: true, finished: false });
  assert.deepEqual(await invoke('wait'), { finished: true, value: 'loaded' });
  assert.equal(session.live, true);
  assert.equal(session.node.promiseLeases.size, 0);
});

test('Cat keeps a timer active after its creating request ends', { timeout: 30000 }, async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'background-timer', enabled: true, source: String.raw`
      const http = require('http'), fs = require('fs');
      let server;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          response.setHeader('content-type', 'application/json');
          if (request.url === '/config' || request.url === '/full-config')
            return response.end(JSON.stringify({ video: { sites: [{
              key: 'timer', api: '/api/timer', actions: ['schedule', 'state'],
            }] } }));
          if (request.url === '/api/timer/schedule') {
            setTimeout(() => fs.writeFileSync('/timer-fired.txt', 'fired'), 60);
            return response.end(JSON.stringify({ scheduled: true }));
          }
          let text = null;
          try { text = fs.readFileSync('/timer-fired.txt', 'utf8'); } catch {}
          response.end(JSON.stringify({ text }));
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server.closeAllConnections(); server.close(); };
    `,
  });
  const invoke = operation => runtime.invoke({
    installationId: 'background-timer', siteId: 'timer', operation, payload: {},
  });
  assert.deepEqual(await invoke('schedule'), { scheduled: true });
  await new Promise(resolve => setTimeout(resolve, 120));
  assert.deepEqual(await invoke('state'), { text: 'fired' });
});

test('Cat lets a private-volume write stream finish after its request ends',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'background-write',
      source: String.raw`
        const http = require('http'), fs = require('fs');
        let server;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/config' || request.url === '/full-config')
              return response.end(JSON.stringify({ video: { sites: [{
                key: 'write', api: '/api/write', actions: ['download', 'state'],
              }] } }));
            if (request.url === '/api/write/download') {
              const stream = fs.createWriteStream('/background-download.bin');
              setTimeout(() => stream.end('persisted-after-request'), 60);
              return response.end(JSON.stringify({ started: true }));
            }
            let text = null;
            try { text = fs.readFileSync('/background-download.bin', 'utf8'); } catch {}
            response.end(JSON.stringify({ text }));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
      `,
    });
    const invoke = operation => session.invoke({
      siteId: 'write', operation, payload: {},
    });
    assert.deepEqual(await invoke('download'), { started: true });
    await new Promise(resolve => setTimeout(resolve, 140));
    assert.deepEqual(await invoke('state'), { text: 'persisted-after-request' });
    assert.equal(session.node.volume.streams.size, 0);
  });

test('Cat control cancellation closes only the transport and leaves background work running',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'cancel-background',
      source: String.raw`
        const http = require('http'), fs = require('fs');
        let server, continued = false;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/config' || request.url === '/full-config')
              return response.end(JSON.stringify({ video: { sites: [{
                key: 'cancel', api: '/api/cancel', actions: ['wait', 'state'],
              }] } }));
            if (request.url === '/api/cancel/wait') {
              setTimeout(() => {
                continued = true;
                fs.writeFileSync('/after-cancel.txt', 'continued');
              }, 60);
              return;
            }
            let text = null;
            try { text = fs.readFileSync('/after-cancel.txt', 'utf8'); } catch {}
            response.end(JSON.stringify({ continued, text }));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
      `,
    });
    let arrived;
    const requestArrived = new Promise(resolve => { arrived = resolve; });
    session.services.main.once('request', () => arrived());
    const controller = new AbortController();
    const pending = session.invoke({
      siteId: 'cancel', operation: 'wait', payload: {},
    }, { signal: controller.signal });
    await requestArrived;
    controller.abort();
    await assert.rejects(pending, { code: 'CANCELLED' });
    await new Promise(resolve => setTimeout(resolve, 140));
    assert.deepEqual(await session.invoke({
      siteId: 'cancel', operation: 'state', payload: {},
    }), { continued: true, text: 'continued' });
    assert.equal(session.live, true);
  });

test('Cat instance stop releases background handles, callbacks, contexts, promises and streams',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'stop-background',
      source: String.raw`
        const http = require('http'), fs = require('fs');
        let server, interval, stream, fd;
        module.exports.start = () => {
          fd = fs.openSync('/held-descriptor.txt', 'w');
          stream = fs.createWriteStream('/held-stream.txt');
          stream.write('held');
          interval = setInterval(() => {}, 1000);
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/config' || request.url === '/full-config')
              return response.end(JSON.stringify({ video: { sites: [{
                key: 'stop', api: '/api/stop', actions: ['home'],
              }] } }));
            response.end(JSON.stringify({ ok: true }));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => {
          clearInterval(interval);
          server.closeAllConnections();
          server.close();
        };
      `,
    });
    assert.deepEqual(await session.invoke({
      siteId: 'stop', operation: 'home', payload: {},
    }), { ok: true });
    const volume = session.node.volume;
    await session.stop();
    assert.equal(session.node.handles.size, 0);
    assert.equal(session.node.callbacks.size, 0);
    assert.equal(session.node.contextLeases.size, 0);
    assert.equal(session.node.promiseLeases.size, 0);
    assert.equal(volume.streams.size, 0);
    assert.equal(volume.descriptors.size, 0);
    assert.equal(session.node.networkResources.size, 0);
  });

test('Cat reports a background callback exception as diagnostics without failing the instance',
  { timeout: 30000 }, async t => {
    const diagnostics = [];
    const runtime = createCatRuntime({ dataRoot: rootFor(t), onDiagnostic: value => diagnostics.push(value) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'background-error', enabled: true, source: String.raw`
        const http = require('http');
        let server;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/config' || request.url === '/full-config')
              return response.end(JSON.stringify({ video: { sites: [{
                key: 'error', api: '/api/error', actions: ['schedule', 'state'],
              }] } }));
            if (request.url === '/api/error/schedule') {
              setTimeout(() => { throw new Error('background failure'); }, 30);
              return response.end(JSON.stringify({ scheduled: true }));
            }
            response.end(JSON.stringify({ ok: true }));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
      `,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'background-error', siteId: 'error', operation: 'schedule', payload: {},
    }), { scheduled: true });
    await new Promise(resolve => setTimeout(resolve, 120));
    assert.equal((await runtime.status()).instances[0].state, 'ready');
    assert.ok(diagnostics.some(value => value.kind === 'ScriptCallbackError' &&
      value.reason === 'script' && value.code === 'SCRIPT_EXCEPTION'));
    assert.deepEqual(await runtime.invoke({
      installationId: 'background-error', siteId: 'error', operation: 'state', payload: {},
    }), { ok: true });
  });

test('Cat keeps callback failures on finished inbound requests away from live reads',
  async t => {
    const diagnostics = [];
    const session = new CatSession({
      dataRoot: rootFor(t), installationId: 'callback-scope', generation: 1,
      revision: { source: '', version: '1', config: {} },
      limits: { cleanupTimeoutMs: 100 },
      onDiagnostic: event => diagnostics.push(event),
    });
    t.after(() => session.stop(fault('STALE_SESSION')));

    const liveRead = new ResourceScope(session.root.signal);
    liveRead.siteId = 'site';
    liveRead.requestId = 1;
    const finishedRequest = new ResourceScope(session.root.signal);
    finishedRequest.siteId = 'site';
    finishedRequest.requestId = 2;
    finishedRequest.failureScope = liveRead;
    finishedRequest.close();
    session.callbackFailureScope = finishedRequest;
    try {
      session.contextScope.run(session.resourceScope(), () =>
        session.bridge('nodeCallbackError', ['CANCELLED']));
    } finally { session.callbackFailureScope = undefined; }
    assert.equal(liveRead.signal.aborted, false);
    assert.equal(session.live, true);
    assert.deepEqual(diagnostics, [{
      installationId: 'callback-scope', generation: 1, siteId: 'site', requestId: 2,
      kind: 'ScriptCallbackError', reason: 'script', code: 'CANCELLED',
    }]);

    const runningRead = new ResourceScope(session.root.signal);
    runningRead.siteId = 'site';
    runningRead.requestId = 3;
    const runningRequest = new ResourceScope(session.root.signal);
    runningRequest.siteId = 'site';
    runningRequest.requestId = 4;
    runningRequest.failureScope = session.root;
    session.callbackFailureScope = runningRequest;
    try {
      session.contextScope.run(session.resourceScope(), () =>
        session.bridge('nodeCallbackError', ['UPSTREAM_HTTP_ERROR']));
    } finally { session.callbackFailureScope = undefined; }
    assert.equal(runningRequest.signal.aborted, true);
    assert.equal(runningRequest.reason.code, 'UPSTREAM_HTTP_ERROR');
    assert.equal(runningRead.signal.aborted, false);
    assert.equal(diagnostics.length, 1);
    runningRead.close();
  });

test('Cat M3U8 enrichment creates the root extension when no earlier enhancement exists',
  { timeout: 30000 }, async t => {
    const runtime = await installed(t, rootFor(t), {
      network: async input => {
        assert.equal(input.url, 'https://media.example.invalid/master.m3u8');
        return {
          statusCode: 200,
          headers: { 'content-type': 'application/vnd.apple.mpegurl' },
          body: '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1280x720\nchild.m3u8\n',
        };
      },
    });
    const result = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'externalPlay', payload: {},
    });
    assert.equal(result._meow.version, 1);
    assert.equal(result._meow.media[0].variants[0].quality, '720P');
    assert.equal(result._meow.media[0].variants[0].url,
      'https://media.example.invalid/child.m3u8');
  });

test('Cat accepts a function module entry and gives independent random listeners physical ports',
  { timeout: 30000 }, async t => {
    const functionalSource = String.raw`
      const http = require('http');
      const servers = [];
      const listen = (server, port) => new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, '0.0.0.0', resolve);
      });
      module.exports = async function () {
        const main = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'function-site', name: String(catDartServerPort()),
              api: '/api/function-site', actions: ['home'],
            }] } }));
            return;
          }
          if (request.url === '/api/function-site/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        const one = http.createServer((_request, response) => response.end('one'));
        const two = http.createServer((_request, response) => response.end('two'));
        servers.push(main, one, two);
        await Promise.all([listen(main, Number(process.env.PORT)), listen(one, 0), listen(two, 0)]);
      };
      module.exports.stop = () => {
        for (const server of servers) {
          server.closeAllConnections();
          server.close();
        }
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'function-entry', source: functionalSource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.equal(instance.state, 'ready');
    assert.equal(instance.endpoints.length, 3);
    assert.equal(instance.endpoints.some(endpoint => endpoint.requestedPorts.includes(0)), false);
    assert.equal(instance.sites[0].name, '0');
    assert.deepEqual(await runtime.invoke({
      installationId: 'function-entry', siteId: 'function-site', operation: 'home', payload: {},
    }), { class: [] });
  });

test('Cat aggregate redirects use worker endpoint snapshots for unmapped listeners',
  { timeout: 30000 }, async t => {
    const source = String.raw`
      const http = require('http');
      let main, media;
      const listen = (server, port) => new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, '127.0.0.1', resolve);
      });
      module.exports.start = async () => {
        media = http.createServer((_request, response) => response.end('secondary-media'));
        await listen(media, 0);
        main = http.createServer((request, response) => {
          if (request.url === '/config' || request.url === '/full-config') {
            response.end(JSON.stringify({ video: { sites: [{
              key: 'redirect', api: '/api/redirect', actions: ['play'],
            }] } }));
            return;
          }
          if (request.url === '/api/redirect/play') {
            response.end(JSON.stringify({ parse: 0,
              url: 'http://127.0.0.1:' + main.address().port + '/redirect' }));
            return;
          }
          if (request.url === '/redirect') {
            response.writeHead(302, { location: 'http://127.0.0.1:' +
              media.address().port + '/video' });
            response.end();
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        await listen(main, Number(process.env.PORT));
      };
      module.exports.stop = () => {
        for (const server of [main, media]) {
          server?.closeAllConnections?.();
          server?.close?.();
        }
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'snapshot-routes', source, config: {},
      version: '1', enabled: true });
    const status = await runtime.status();
    const play = await runtime.invoke({ installationId: 'snapshot-routes',
      siteId: 'redirect', operation: 'play', payload: {} });
    const redirect = await request(play.url);
    assert.equal(redirect.status, 302);
    const location = new URL(redirect.headers.location);
    assert.equal(location.origin, status.aggregate.origin);
    assert.match(location.pathname, /^\/proxy\/snapshot-routes\/\d+\/[a-f0-9]{48}\/video$/);
    const media = await request(location.href);
    assert.equal(media.status, 200);
    assert.equal(media.text, 'secondary-media');
  });

test('Cat gateway port publication updates an existing worker without replacing its generation',
  { timeout: 30000 }, async t => {
    const runtime = await installed(t, rootFor(t));
    const initial = await runtime.status();
    const generation = runtime.instance('fixture-a').generation;
    const before = await runtime.invoke({ installationId: 'fixture-a', siteId: 'site-a',
      operation: 'play', payload: { id: 'hls' } });
    const reserved = net.createServer();
    await new Promise((resolve, reject) => {
      reserved.once('error', reject);
      reserved.listen(0, '127.0.0.1', resolve);
    });
    const port = reserved.address().port;
    await new Promise(resolve => reserved.close(resolve));
    for (const nextPort of [port, 0]) {
      writeSettings(initial.settingsPath, value => ({ ...value, port: nextPort }));
      await runtime.refresh();
      const status = await runtime.status();
      assert.equal(runtime.instance('fixture-a').generation, generation);
      const play = await runtime.invoke({ installationId: 'fixture-a', siteId: 'site-a',
        operation: 'play', payload: { id: 'hls' } });
      assert.equal(new URL(play.url).origin, status.aggregate.origin);
      assert.equal(new URL(play.url).pathname, new URL(before.url).pathname);
      assert.equal(new URL(play._meow.media.at(-1).variants[0].url).origin, status.aggregate.origin);
      assert.equal((await request(play.url)).status, 200);
      for (const endpoint of runtime.instance('fixture-a').endpoints)
        assert.equal(Object.hasOwn(endpoint, 'proxyId'), false);
    }
  });

test('Cat listener closure retires its gateway mapping while another owned listener stays usable',
  { timeout: 30000 }, async t => {
    const source = String.raw`
      const http = require('http');
      let main, media;
      const listen = (server, port) => new Promise(resolve => server.listen(port, resolve));
      module.exports.start = async () => {
        media = http.createServer((request, response) => {
          if (request.url !== '/video') { response.statusCode = 404; response.end(); return; }
          response.end('secondary-media');
        });
        await listen(media, 0);
        main = http.createServer(async (request, response) => {
          if (request.url === '/config' || request.url === '/full-config') {
            response.end(JSON.stringify({ video: { sites: [{
              key: 'closure', api: '/api/closure', actions: ['play', 'closeMain'],
            }] } }));
            return;
          }
          const chunks = [];
          for await (const chunk of request) chunks.push(chunk);
          const input = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : {};
          if (request.url === '/api/closure/play') {
            response.end(JSON.stringify({ parse: 0, url: 'http://127.0.0.1:' +
              (input.secondary ? media : main).address().port + '/video' }));
          } else if (request.url === '/api/closure/closeMain') {
            response.end(JSON.stringify({ closed: true }));
            main.close();
          } else response.end('primary-media');
        });
        await listen(main, Number(process.env.PORT));
      };
      module.exports.stop = () => {
        for (const server of [main, media]) {
          server?.closeAllConnections();
          if (server?.listening) server.close();
        }
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'closure', source, enabled: true });
    const play = payload => runtime.invoke({ installationId: 'closure', siteId: 'closure',
      operation: 'play', payload });
    const primary = await play({});
    const secondary = await play({ secondary: true });
    assert.equal((await request(primary.url)).text, 'primary-media');
    assert.equal((await request(secondary.url)).text, 'secondary-media');
    await runtime.invoke({ installationId: 'closure', siteId: 'closure',
      operation: 'closeMain', payload: {} });
    await waitFor(() => runtime.instance('closure').endpoints.length === 1,
      'the closed listener remained in the endpoint snapshot');
    const stale = await request(primary.url);
    assert.equal(JSON.parse(stale.text).error.code, 'STALE_SESSION');
    assert.equal((await request(secondary.url)).text, 'secondary-media');
  });

test('Cat net servers provide instance-local port probes without exposing raw TCP listeners',
  { timeout: 30000 }, async t => {
    const probeSource = String.raw`
      const net = require('net');
      const http = require('http');
      let server;
      const findPort = port => new Promise((resolve, reject) => {
        const probe = net.createServer();
        const isServer = probe instanceof net.Server;
        probe.unref();
        probe.once('error', error => {
          if (error.code === 'EADDRINUSE') resolve(findPort(port + 1));
          else reject(error);
        });
        probe.once('listening', () => {
          const address = probe.address();
          probe.close(error => {
            if (error) reject(error);
            else resolve({ port: address.port, isServer, closed: !probe.listening });
          });
        });
        probe.listen(port, '0.0.0.0');
      });
      module.exports.start = async () => {
        const blocker = net.createServer();
        await new Promise((resolve, reject) => {
          blocker.once('error', reject);
          blocker.listen(32000, '0.0.0.0', resolve);
        });
        const selected = await findPort(32000);
        await new Promise(resolve => blocker.close(resolve));
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'net-probe', name: JSON.stringify(selected),
              api: '/api/net-probe', actions: ['home'],
            }] } }));
            return;
          }
          if (request.url === '/api/net-probe/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        await new Promise(resolve => server.listen(selected.port, '0.0.0.0', resolve));
      };
      module.exports.stop = () => new Promise(resolve => server.close(resolve));
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'net-probe', source: probeSource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.deepEqual(JSON.parse(instance.sites[0].name), {
      port: 32001, isServer: true, closed: true,
    });
    assert.equal(instance.endpoints.length, 1);
    assert.ok(instance.endpoints[0].requestedPorts.includes(32001));
    assert.deepEqual(await runtime.invoke({
      installationId: 'net-probe', siteId: 'net-probe', operation: 'home', payload: {},
    }), { class: [] });
  });

test('Cat discovers a short startup wrapper, pairs its adjacent async stop and preserves stop writes',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const wrappedSource = String.raw`
      const fs = require('fs');
      let server, generation = 0;
      async function Gti(config) {
        generation++;
        return qti(config, generation);
      }
      async function qti(config, token) {
        if (token !== 1) throw new Error('wrapper not selected');
        server = catServerFactory((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'wrapped', name: config.name || 'Wrapped',
              api: '/api/wrapped', actions: ['home'],
            }] } }));
            return;
          }
          if (request.url === '/api/wrapped/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        await new Promise(resolve => server.listen(Number(process.env.PORT), '127.0.0.1', resolve));
        return server;
      }
      async function Wti() {
        fs.writeFileSync('/wrapped-stop.txt', 'done');
        await new Promise(resolve => server.close(resolve));
        server = null;
      }
      0 && (module.exports = { start, stop });
    `;
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'wrapped', source: wrappedSource, config: { name: 'Wrapped' },
      version: '1', enabled: true,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'wrapped', siteId: 'wrapped', operation: 'home', payload: {},
    }), { class: [] });
    await runtime.setEnabled('wrapped', false);
    assert.equal(readFileSync(join(root, 'volumes', 'wrapped', 'wrapped-stop.txt'), 'utf8'), 'done');
  });

test('Cat direct spider fallback exposes homeVod and homeVideo actions',
  { timeout: 30000 }, async t => {
    const spiderSource = String.raw`
      module.exports = {
        homeVod() { return { list: [{ vod_id: 'vod', vod_name: 'Vod' }] }; },
        homeVideo() { return { list: [{ vod_id: 'video', vod_name: 'Video' }] }; },
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'direct-spider', source: spiderSource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.deepEqual(instance.sites[0].actions.sort(), ['homeVideo', 'homeVod']);
    assert.equal((await runtime.invoke({
      installationId: 'direct-spider', siteId: 'default', operation: 'homeVod', payload: {},
    })).list[0].vod_id, 'vod');
    assert.equal((await runtime.invoke({
      installationId: 'direct-spider', siteId: 'default', operation: 'homeVideo', payload: {},
    })).list[0].vod_id, 'video');
  });

test('Cat direct spider fallback accepts extension-only exports and excludes lifecycle controls',
  { timeout: 30000 }, async t => {
    const spiderSource = String.raw`
      module.exports = {
        customSearch(payload) {
          return { list: [{ vod_id: payload.id, vod_name: 'Custom' }] };
        },
        stop() {},
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'direct-extension', source: spiderSource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.deepEqual(instance.sites[0].actions, ['customSearch']);
    assert.deepEqual(await runtime.invoke({
      installationId: 'direct-extension', siteId: 'default', operation: 'customSearch',
      payload: { id: 'extension-1' },
    }), { list: [{ vod_id: 'extension-1', vod_name: 'Custom' }] });
  });

test('Cat ignores a weak named startup decoy and runs the exported direct spider',
  { timeout: 30000 }, async t => {
    const decoySource = String.raw`
      (() => {
        function passthrough(value) { return value.init(); }
        globalThis.Ndr = function Ndr() { return passthrough({}); };
      })();
      module.exports = {
        home() { return { class: [{ type_id: 'direct', type_name: 'Direct' }] }; },
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'named-decoy', source: decoySource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.deepEqual(instance.sites[0].actions, ['home']);
    assert.deepEqual(await runtime.invoke({
      installationId: 'named-decoy', siteId: 'default', operation: 'home', payload: {},
    }), { class: [{ type_id: 'direct', type_name: 'Direct' }] });
  });

test('Cat accepts a short named wrapper only when it calls a strong local startup function',
  { timeout: 30000 }, async t => {
    const wrappedSource = String.raw`
      const http = require('http');
      (() => {
        let server;
        const bootServer = async () => {
          server = http.createServer((request, response) => {
            if (request.url === '/full-config' || request.url === '/config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'named-wrapper', name: 'Named wrapper',
                api: '/api/named-wrapper', actions: ['home'],
              }] } }));
              return;
            }
            if (request.url === '/api/named-wrapper/home') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ class: [] }));
              return;
            }
            response.statusCode = 404;
            response.end();
          });
          await new Promise(resolve =>
            server.listen(Number(process.env.PORT), '127.0.0.1', resolve));
          return server;
        };
        globalThis.Ndr = async function Ndr() { return bootServer(); };
      })();
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'named-wrapper', source: wrappedSource, config: {},
      version: '1', enabled: true,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'named-wrapper', siteId: 'named-wrapper', operation: 'home', payload: {},
    }), { class: [] });
  });

test('Cat discovers config on another owned listener without bypassing the first listener auth',
  { timeout: 30000 }, async t => {
    const multiListenerSource = String.raw`
      const http = require('http');
      const fs = require('fs');
      let auth, cat, media;
      const readBody = request => new Promise((resolve, reject) => {
        const chunks = [];
        request.on('data', chunk => chunks.push(Buffer.from(chunk)));
        request.once('end', () => {
          try { resolve(chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : {}); }
          catch (error) { reject(error); }
        });
        request.once('error', reject);
      });
      module.exports.start = async () => {
        auth = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.statusCode = 401;
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ errorCode: 401, success: false, errorMessage: 'Unauthorized' }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        cat = http.createServer(async (request, response) => {
          if (request.url === '/website/pans/list') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ success: true, data: [{ key: 'quark' }] }));
            return;
          }
          if (request.url === '/website/quark/cookie') {
            response.setHeader('content-type', 'application/json');
            if (request.method === 'GET') {
              let cookie = '';
              try { cookie = fs.readFileSync('/secondary-cookie.txt', 'utf8'); } catch {}
              response.end(JSON.stringify({ success: true, data: cookie }));
              return;
            }
            const body = await readBody(request);
            fs.writeFileSync('/secondary-cookie.txt', String(body.cookie || ''));
            response.end(JSON.stringify({ success: true }));
            return;
          }
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'multi-listener', name: 'Multi listener', api: '/api/multi-listener',
              actions: ['home', 'relative', 'media', 'closeMedia'],
            }] } }));
            return;
          }
          if (request.url === '/api/multi-listener/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [{ type_id: 'ready', type_name: 'Ready' }] }));
            return;
          }
          if (request.url === '/api/multi-listener/relative') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ parse: 0, url: '/relative.bin' }));
            return;
          }
          if (request.url === '/relative.bin') {
            response.end('relative-listener');
            return;
          }
          if (request.url === '/api/multi-listener/media') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ parse: 0, url: 'http://127.0.0.1:32113/media.bin' }));
            return;
          }
          if (request.url === '/api/multi-listener/closeMedia') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ closed: true }));
            const current = media;
            media = null;
            current.closeAllConnections();
            current.close();
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        media = http.createServer((request, response) => {
          if (request.url === '/media.bin') {
            response.end('media-listener');
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        await new Promise(resolve => auth.listen(32111, '127.0.0.1', resolve));
        await new Promise(resolve => cat.listen(32112, '127.0.0.1', resolve));
        await new Promise(resolve => media.listen(32113, '127.0.0.1', resolve));
      };
      module.exports.stop = () => {
        for (const server of [auth, cat, media]) {
          if (!server) continue;
          server.closeAllConnections();
          if (server.listening) server.close();
        }
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'multi-listener', source: multiListenerSource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    const authEndpoint = instance.endpoints.find(endpoint => endpoint.requestedPorts.includes(32111));
    assert(authEndpoint);
    assert.equal((await request(authEndpoint.origin + '/full-config')).status, 403);
    assert.equal((await request(authEndpoint.origin + '/full-config', {
      headers: { [scriptAccessHeader]: runtime.getSession('multi-listener').endpoint().access },
    })).status, 401);
    assert.deepEqual(await runtime.invoke({
      installationId: 'multi-listener', siteId: 'multi-listener', operation: 'home', payload: {},
    }), { class: [{ type_id: 'ready', type_name: 'Ready' }] });
    assert.deepEqual(await runtime.management('multi-listener'),
      { protocol: 'website-v1', fields: {} });
    assert.deepEqual(await runtime.sync('multi-listener', undefined, {
      provider: 'quark', credentials: { cookie: 'secondary-cookie' },
    }), { status: 'saved', protocol: 'website-v1' });
    const relative = await runtime.invoke({
      installationId: 'multi-listener', siteId: 'multi-listener', operation: 'relative', payload: {},
    });
    assert.equal((await request(relative.url)).text, 'relative-listener');

    const media = await runtime.invoke({
      installationId: 'multi-listener', siteId: 'multi-listener', operation: 'media', payload: {},
    });
    assert.equal((await request(media.url)).text, 'media-listener');
    assert.ok((await runtime.status()).instances[0].managementUrl);
    assert.deepEqual(await runtime.invoke({
      installationId: 'multi-listener', siteId: 'multi-listener', operation: 'closeMedia', payload: {},
    }), { closed: true });
    for (let attempt = 0; attempt < 50 && (await request(media.url)).status !== 502; attempt++)
      await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal((await request(media.url)).status, 502);
  });

test('Cat discovers custom actions from startup vm sources',
  { timeout: 30000 }, async t => {
    const dynamic = String.raw`
      const http = require('http');
      let server;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'dynamic-vm', name: 'Dynamic VM', api: '/api/dynamic-vm',
            }] } }));
            return;
          }
          if (request.url === '/api/dynamic-vm/customSearch') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ list: [{ vod_id: 'dynamic', vod_name: 'Dynamic' }] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server.closeAllConnections(); server.close(); };
    `;
    const loader = `
      const vm = require('vm');
      new vm.Script(Buffer.from(${JSON.stringify(Buffer.from(dynamic).toString('base64'))}, 'base64').toString())
        .runInThisContext();
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'dynamic-vm', source: loader, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.deepEqual(instance.sites[0].actions, ['customSearch']);
    assert.equal((await runtime.invoke({
      installationId: 'dynamic-vm', siteId: 'dynamic-vm', operation: 'customSearch', payload: {},
    })).list[0].vod_id, 'dynamic');
  });

test('Cat observes real framework routes, waits for its listener and selects each HTTP method',
  { timeout: 30000 }, async t => {
    const frameworkSource = String.raw`
      const http = require('http'), dc = require('diagnostics_channel');
      let other, app;
      module.exports.start = () => {
        other = http.createServer((_request, response) => {
          response.statusCode = 401; response.end();
        });
        other.listen(9987);
        const hooks = new Map(), routes = new Map();
        let ready;
        const readiness = new Promise(resolve => { ready = resolve; });
        app = {
          addHook(name, callback) {
            if (!hooks.has(name)) hooks.set(name, []);
            hooks.get(name).push(callback);
            return this;
          },
          hasRoute() { return true; },
          ready(callback) { return callback ? readiness.then(() => callback(), callback) : readiness; },
        };
        const json = (response, value) => {
          response.setHeader('content-type', 'application/json');
          response.end(JSON.stringify(value));
        };
        app.server = catServerFactory(async (raw, response) => {
          const url = new URL(raw.url, 'http://127.0.0.1');
          const request = { raw, headers: raw.headers, query: Object.fromEntries(url.searchParams) };
          for (const hook of hooks.get('onRequest') || [])
            await new Promise((resolve, reject) => hook(request, response, error => error ? reject(error) : resolve()));
          if (request.headers['x-internal-auth'] !== app.internalAuthSecret) {
            response.statusCode = 401; json(response, { denied: true }); return;
          }
          if (url.pathname === '/full-config' || url.pathname === '/config') {
            json(response, { video: { sites: [
              { key: 'one', name: 'One', api: '/api/one' },
              { key: 'two', name: 'Two', api: '/api/two' },
              { key: '中文', name: '中文', api: '/api/中文' },
              { key: 'parameter', name: 'Parameter', api: '/api/servers/selected' },
            ] } }); return;
          }
          const methods = routes.get(url.pathname);
          if (!methods?.includes(raw.method)) { response.statusCode = 404; json(response, {}); return; }
          if (url.pathname.endsWith('/init')) { response.end(); return; }
          if (url.pathname.endsWith('/text')) {
            response.setHeader('content-type', 'text/plain'); response.end('plain result'); return;
          }
          if (url.pathname.endsWith('/invalid')) {
            response.setHeader('content-type', 'application/json'); response.end('invalid'); return;
          }
          if (url.pathname.endsWith('/upstreamMissing')) {
            response.statusCode = 404; json(response, { absent: true }); return;
          }
          if (url.pathname.endsWith('/failure')) {
            const error = new TypeError("Cannot read property 'record' of undefined: SYNTHETIC-PRIVATE-VALUE");
            for (const hook of hooks.get('onError') || [])
              await new Promise(resolve => hook(request, response, error, resolve));
            response.statusCode = 500; json(response, { message: error.message }); return;
          }
          const chunks = [];
          for await (const chunk of raw) chunks.push(Buffer.from(chunk));
          json(response, { method: raw.method, query: request.query,
            body: chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null });
        });
        dc.channel('fastify.initialization').publish({ fastify: app });
        app.internalAuthSecret = 'SYNTHETIC-INTERNAL-AUTH';
        const route = (url, method) => {
          routes.set(url, [].concat(method));
          for (const hook of hooks.get('onRoute') || []) hook.call(app, { url, method });
        };
        setTimeout(() => {
          route('/full-config', 'GET'); route('/config', 'GET');
          route('/api/one/home', 'POST');
          route('/api/one/customSearch', ['GET', 'HEAD']);
          route('/api/one/init', 'POST');
          route('/api/one/text', 'GET');
          route('/api/one/invalid', 'POST');
          route('/api/one/upstreamMissing', 'POST');
          route('/api/one/failure', 'POST');
          route('/api/two/category', 'POST');
          route('/api/中文/home', 'POST');
          route('/api/servers/:serverId/customSearch', 'GET');
          route('/api/:site/:operation', 'POST');
          route('/website/accounts', 'POST');
          ready();
          app.server.listen(9988);
        }, 20);
      };
      module.exports.stop = () => {
        for (const server of [other, app?.server]) {
          server?.closeAllConnections();
          if (server?.listening) server.close();
        }
      };
    `;
    const diagnostics = [];
    const runtime = createCatRuntime({ dataRoot: rootFor(t), onDiagnostic: event => diagnostics.push(event) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'framework', source: frameworkSource, config: {}, enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    const one = instance.sites.find(site => site.key === 'one');
    const two = instance.sites.find(site => site.key === 'two');
    assert.equal(one.discovery, 'registered-routes');
    assert.deepEqual(one.actions, ['home', 'customSearch', 'init', 'text', 'invalid',
      'upstreamMissing', 'failure']);
    assert.deepEqual(two.actions, ['category']);
    assert.deepEqual(instance.sites.find(site => site.key === '中文').actions, ['home']);
    assert.deepEqual(instance.sites.find(site => site.key === 'parameter').actions, ['customSearch']);
    assert.equal(one.methods.customSearch, 'GET');
    const invoke = (operation, payload = {}, siteId = 'one') =>
      runtime.invoke({ installationId: 'framework', siteId, operation, payload });
    assert.deepEqual(await invoke('customSearch', { wd: 'space 中文', page: 2, filters: { year: '2026' } }),
      { method: 'GET', query: { wd: 'space 中文', page: '2', filters: '{"year":"2026"}' }, body: null });
    assert.deepEqual((await invoke('category', { id: 'movie', page: 2, filters: {} }, 'two')).body,
      { id: 'movie', page: 2, filters: {} });
    assert.equal(await invoke('init'), null);
    assert.equal(await invoke('text'), 'plain result');
    await assert.rejects(invoke('invalid'), { code: 'INVALID_JSON' });
    await assert.rejects(invoke('upstreamMissing'), { code: 'UPSTREAM_HTTP_ERROR', statusCode: 404 });
    await assert.rejects(invoke('accounts'), { code: 'CAPABILITY_UNAVAILABLE' });
    await assert.rejects(invoke('failure'), { code: 'UPSTREAM_HTTP_ERROR' });
    // The guest's onError hook reports before the HTTP 500 response is
    // observed, so this evidence has no request scope yet.
    assert(diagnostics.some(event => event.kind === 'TypeError' && event.reason === 'input' &&
      event.siteId === null && event.requestId === null));
    assert.equal(JSON.stringify(diagnostics).includes('SYNTHETIC-PRIVATE-VALUE'), false);
    const catEndpoint = instance.endpoints.find(endpoint => endpoint.requestedPorts.includes(9988));
    assert.equal(new URL(instance.managementUrl).origin, catEndpoint.origin);
    assert.equal((await request(catEndpoint.origin + '/api/one/home')).status, 403);
  });

test('Cat discovers constant template, dispatch-map and anchored regex actions without probing endpoints',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'small-service', enabled: true, source: String.raw`
        const SITE_KEY = 'small', SITE_TYPE = 3;
        const SITE_API = '/spider/' + SITE_KEY + '/' + SITE_TYPE;
        let server;
        module.exports.start = async () => {
          const table = new Map([
            ['POST ' + SITE_API + '/search', () => ({ list: [{ vod_id: 'found' }] })],
          ]);
          const app = { get(path, handler) { table.set('GET ' + path, handler); } };
          app.get(SITE_API + '/customSearch', () => ({ extension: true }));
          // This is data, not a route registration.
          const decoy = "app.post('/spider/small/3/deleteAccount', account)";
          function unrelated(SITE_API) { app.get(SITE_API + '/private', () => null); }
          server = catServerFactory(async (request, response) => {
            const url = new URL(request.url, 'http://127.0.0.1');
            const send = value => {
              response.setHeader('content-type', 'application/json'); response.end(JSON.stringify(value));
            };
            if (url.pathname === '/config' || url.pathname === '/full-config') return send({
              video: { sites: [
                { key: 'small', api: SITE_API }, { key: 'pattern', api: '/spider/pattern/3' },
              ] },
            });
            const pattern = /^\/spider\/([a-z]+)\/3\/(customReport|homeVideo)$/.exec(url.pathname);
            if (pattern) return send({ action: pattern[2] });
            const handler = table.get(request.method + ' ' + url.pathname);
            if (handler) return send(handler());
            response.statusCode = 404; response.end();
          });
          await new Promise(resolve => server.listen(9988, resolve));
        };
        module.exports.stop = () => server.close();
      `,
    });
    const sites = (await runtime.status()).instances[0].sites;
    const small = sites.find(site => site.key === 'small');
    assert.deepEqual([...small.actions].sort(), ['customReport', 'customSearch', 'homeVideo', 'search']);
    assert.equal(small.methods.customSearch, 'GET');
    assert.equal(small.discovery, 'source-routes');
    assert.deepEqual(sites.find(site => site.key === 'pattern').actions, ['customReport', 'homeVideo']);
    const invoke = (siteId, operation) => runtime.invoke({
      installationId: 'small-service', siteId, operation, payload: {},
    });
    assert.deepEqual(await invoke('small', 'search'), { list: [{ vod_id: 'found' }] });
    assert.deepEqual(await invoke('small', 'customSearch'), { extension: true });
    assert.deepEqual(await invoke('pattern', 'customReport'), { action: 'customReport' });
    await assert.rejects(invoke('small', 'deleteAccount'), { code: 'CAPABILITY_UNAVAILABLE' });
    await assert.rejects(invoke('small', 'private'), { code: 'CAPABILITY_UNAVAILABLE' });
  });

test('Cat exposes Node process identity and HTTP request options without transporting library internals',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'node-http-client', enabled: true, source: String.raw`
        const http = require('http'), url = require('url');
        const nodeAdapter = Object.prototype.toString.call(process) === '[object process]';
        for (const name of ['fetch', 'Request', 'Response', 'Headers']) delete globalThis[name];
        let server;
        const read = options => new Promise((resolve, reject) => {
          let request;
          try { request = http.get(options, response => {
            const chunks = [];
            response.on('data', chunk => chunks.push(Buffer.from(chunk)));
            response.once('end', () => resolve(Buffer.concat(chunks).toString()));
          }); } catch (error) { reject(error); return; }
          request.once('error', reject);
        });
        module.exports.start = async () => {
          server = http.createServer(async (req, res) => {
            const target = new URL(req.url, 'http://127.0.0.1');
            const send = value => {
              http.ServerResponse.prototype.setHeader.call(res, 'content-type', 'application/json');
              res.end(JSON.stringify(value));
            };
            if (target.pathname === '/config' || target.pathname === '/full-config')
              return send({ video: { sites: [{ key: 'client', api: '/client', actions: ['home'] }] } });
            if (target.pathname === '/echo') return res.end('ok');
            if (!nodeAdapter) { res.statusCode = 500;return res.end(); }
            const parsed = url.parse('http://127.0.0.1:9988/echo?key=one&key=two', true);
            const options = { ...parsed, method: 'GET', agent: false,
              createConnection() { throw Error('Node does not consume this with agent:false'); },
              nativeProtocols: { 'http:': http }, beforeRedirect() { throw Error('library-only'); } };
            options.libraryState = options;
            Object.defineProperty(options, 'internalGetter', {
              enumerable: true, get() { throw Error('library-only'); },
            });
            const formatted = url.format(options);
            const unicode = url.format(new URL('https://fixture.invalid/path?q=1#fragment'),
              { search: false, fragment: false, libraryState: options });
            const body = await read(options);
            let blocked;
            try { await read({ hostname: '127.0.0.1', port: 9988, path: '/echo',
              lookup() { throw Error('must not run'); } }); }
            catch (error) { blocked = error.code; }
            let blockedConnection;
            try { await read({ hostname: '127.0.0.1', port: 9988, path: '/echo',
              createConnection() { throw Error('must not run'); } }); }
            catch (error) { blockedConnection = error.code; }
            let invalid;
            try { new URL('not a URL'); } catch (error) { invalid = error.code; }
            send({ body, blocked, blockedConnection, invalid, query: parsed.query, protocol: parsed.protocol,
              formatted, legacy: parsed.format(), unicode, tag: Object.prototype.toString.call(process) });
          });
          await new Promise(resolve => server.listen(9988, resolve));
        };
        module.exports.stop = () => server.close();
      `,
    });
    const result = await runtime.invoke({
      installationId: 'node-http-client', siteId: 'client', operation: 'home', payload: {},
    });
    assert.deepEqual(result, {
      body: 'ok', blocked: 'PERMISSION_DENIED', blockedConnection: 'PERMISSION_DENIED', invalid: 'ERR_INVALID_URL',
      query: { key: ['one', 'two'] }, protocol: 'http:',
      formatted: 'http://127.0.0.1:9988/echo?key=one&key=two',
      legacy: 'http://127.0.0.1:9988/echo?key=one&key=two',
      unicode: 'https://fixture.invalid/path', tag: '[object process]',
    });
  });

test('Cat native method wrappers remain removable guest callbacks and release listener leases',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'native-method-callback',
      source: `
        const crypto = require('crypto');
        module.exports.home = () => new Promise((resolve, reject) => {
          const hash = crypto.createHash('sha256'), destroy = hash.destroy;
          hash.once('error', reject);
          hash.on('finish', destroy);
          const registered = hash.listenerCount('finish');
          hash.removeListener('finish', destroy);
          const removed = hash.listenerCount('finish');
          hash.once('finish', destroy);
          hash.once('close', () => resolve({ registered, removed, destroyed: hash.destroyed }));
          hash.end('synthetic data');
        });
      `,
    });
    const baseline = session.node.callbacks.size;
    const siteId = [...session.sites.keys()][0];
    for (let index = 0; index < 4; index++) {
      assert.deepEqual(await session.invoke({
        siteId, operation: 'home', payload: {},
      }), { registered: 1, removed: 0, destroyed: true });
      await collectGarbage();
      assert.ok(session.node.callbacks.size <= baseline,
        `callbacks grew from ${baseline} to ${session.node.callbacks.size}`);
      assert.equal(session.node.callbackReturns.size, 0);
    }
  });

test('Cat framework injection uses guest-local HTTP response headers, streams and subclasses',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'local-response', enabled: true, source: String.raw`
        const http = require('node:http'), https = require('node:https');
        const { Writable } = require('node:stream'), { inherits } = require('node:util');
        function InjectResponse(request, resolve) {
          http.ServerResponse.call(this, request);
          this.chunks = [];
          this.setHeader('foo', 'bar'); this.removeHeader('foo');
          this.assignSocket(new Writable({ write(chunk, encoding, done) { setImmediate(done); } }));
          this.once('finish', () => resolve({
            status: this.statusCode, reason: this.statusMessage,
            headers: this.getHeaders(), names: this.getRawHeaderNames(),
            headerReady: this.headersSent, wireDate: /\r\nDate: [^\r]+\r\n/.test(this._header),
            body: Buffer.concat(this.chunks).toString(), shared: https.ServerResponse === http.ServerResponse,
            inherited: this instanceof http.ServerResponse && this instanceof Writable,
          }));
        }
        inherits(InjectResponse, http.ServerResponse);
        InjectResponse.prototype.write = function(chunk, encoding, callback) {
          http.ServerResponse.prototype.write.call(this, chunk, encoding, callback);
          this.chunks.push(Buffer.from(chunk, encoding)); return true;
        };
        InjectResponse.prototype.end = function(chunk, encoding, callback) {
          if (chunk) this.write(chunk, encoding);
          http.ServerResponse.prototype.end.call(this, callback);
          this.emit('finish'); this.destroy();
        };
        module.exports.home = () => new Promise(resolve => {
          const response = new InjectResponse(new http.IncomingMessage(), resolve);
          response.setHeader('Content-Type', 'application/json');
          response.appendHeader('X-Values', 'one'); response.appendHeader('X-Values', 'two');
          response.writeHead(201);
          let blocked;
          try { response.setHeader('Late', 'value'); } catch (error) { blocked = error.code; }
          response.end(JSON.stringify({ blocked }));
        });
        module.exports.search = () => {
          const response = new http.ServerResponse({ method: 'HEAD' });
          let invalidName, invalidValue, forged;
          try { response.setHeader('invalid name', 'x'); } catch (error) { invalidName = error.code; }
          try { response.setHeader('x-value', 'x\r\nInjected: y'); } catch (error) { invalidValue = error.code; }
          try { http.ServerResponse.prototype.setHeader.call({}, 'x', 'y'); }
          catch (error) { forged = error.code; }
          response.writeHead('204', ['Set-Cookie', 'first=one', 'Set-Cookie', 'second=two']);
          response.end();
          return { invalidName, invalidValue, forged, hasBody: response._hasBody,
            finished: response.finished, cookies: response.getHeader('set-cookie') };
        };
      `,
    });
    const siteId = (await runtime.status()).instances[0].sites[0].key;
    const invoke = operation => runtime.invoke({
      installationId: 'local-response', siteId, operation, payload: {},
    });
    assert.deepEqual(await invoke('home'), {
      status: 201, reason: 'Created',
      headers: { 'content-type': 'application/json', 'x-values': ['one', 'two'] },
      names: ['Content-Type', 'X-Values'], headerReady: true, wireDate: true,
      body: '{"blocked":"ERR_HTTP_HEADERS_SENT"}', shared: true, inherited: true,
    });
    assert.deepEqual(await invoke('search'), {
      invalidName: 'ERR_INVALID_HTTP_TOKEN', invalidValue: 'ERR_INVALID_CHAR',
      forged: 'ERR_INVALID_THIS', hasBody: false, finished: true, cookies: ['first=one', 'second=two'],
    });
  });

test('Cat supports ECDH and public X509 certificate keys without exposing native prototypes',
  { timeout: 30000 }, async t => {
    const pem = rootCertificates.find(value =>
      new X509Certificate(value).publicKey.asymmetricKeyType === 'rsa');
    assert(pem);
    const expected = new X509Certificate(pem);
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'certificate-crypto', enabled: true, source: `
        const crypto = require('crypto');
        module.exports.home = () => {
          const certificate = new crypto.X509Certificate(${JSON.stringify(pem)});
          const first = crypto.createECDH('prime256v1'), second = crypto.createECDH('prime256v1');
          first.generateKeys(); second.generateKeys();
          const secret = first.computeSecret(second.getPublicKey());
          const same = secret.equals(second.computeSecret(first.getPublicKey()));
          const restored = crypto.createECDH('prime256v1');
          restored.setPrivateKey(first.getPrivateKey());
          const restoredSame = restored.computeSecret(second.getPublicKey()).equals(secret);
          const encrypted = crypto.publicEncrypt({
            key: certificate.publicKey, padding: crypto.constants.RSA_PKCS1_PADDING,
          }, Buffer.from('synthetic data'));
          let invalid;
          try { first.computeSecret(Buffer.from('invalid public point')); } catch (error) { invalid = error.code; }
          return { same, restoredSame, invalid, serial: certificate.serialNumber, ca: certificate.ca,
            bytes: encrypted.length, instance: certificate instanceof crypto.X509Certificate,
            keyType: certificate.publicKey.type, nativeBinding: typeof certificate.publicKey.binding,
            serialized: certificate.toJSON() === certificate.toString() };
        };
      `,
    });
    const siteId = (await runtime.status()).instances[0].sites[0].key;
    assert.deepEqual(await runtime.invoke({
      installationId: 'certificate-crypto', siteId, operation: 'home', payload: {},
    }), {
      same: true, restoredSame: true, invalid: 'ERR_CRYPTO_ECDH_INVALID_PUBLIC_KEY',
      serial: expected.serialNumber, ca: expected.ca,
      bytes: expected.publicKey.asymmetricKeyDetails.modulusLength / 8,
      instance: true, keyType: 'public', nativeBinding: 'undefined', serialized: true,
    });
  });

test('Cat unsigned BigInt results can be written as Buffer UInt64 values', async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'unsigned-buffer', enabled: true, source: `
      module.exports.home = () => ({
        values: [-1n, -2n, (1n << 80n) - 3n, -(1n << 64n), (1n << 64n) + 1n].map(value => {
          const bytes = Buffer.alloc(8);
          bytes.writeBigUInt64LE(BigInt.asUintN(64, value));
          return bytes.toString('hex');
        }),
      });
    `,
  });
  assert.deepEqual(await runtime.invoke({
    installationId: 'unsigned-buffer', siteId: 'default', operation: 'home', payload: {},
  }), { values: ['ffffffffffffffff', 'feffffffffffffff', 'fdffffffffffffff',
    '0000000000000000', '0100000000000000'] });
});

test('Cat Buffer base64url encoding matches native Node across public entry points', async t => {
  function exercise(Buffer) {
    const vectors = [[], [255], [251, 255], [251, 255, 239],
      Array.from({ length: 256 }, (_, index) => index)];
    const encoded = vectors.map(values => {
      const backing = new Uint8Array(values.length + 2);
      backing.set(values, 1);
      const bytes = Buffer.from(backing.buffer, 1, values.length);
      const text = bytes.toString('base64url');
      return {
        text, upper: bytes.toString('BASE64URL'), locale: bytes.toLocaleString('base64url'),
        slice: bytes.toString('base64url', 1, Math.max(1, bytes.length - 1)),
        hex: Buffer.from(text, 'base64url').toString('hex'),
        ordinary: Buffer.from(bytes.toString('base64'), 'base64url').toString('hex'),
        padded: Buffer.from(text + '='.repeat((4 - text.length % 4) % 4), 'BASE64URL').toString('hex'),
        byteLength: Buffer.byteLength(text, 'base64url'),
      };
    });
    const writes = [
      ['-_8', 'base64url'],
      ['-_8', 1, 'base64url'],
      ['-_8', 1, 1, 'BASE64URL'],
      ['-_8', 1, 2, 'base64url'],
    ].map(args => {
      const bytes = Buffer.alloc(5, 0x21);
      return { count: bytes.write(...args), hex: bytes.toString('hex') };
    });
    const fills = [
      ['-_8', 'base64url'], ['-_8', 1, 'BASE64URL'], ['-_8', 1, 4, 'base64url'],
    ].map(args => Buffer.alloc(6, 0x21).fill(...args).toString('hex'));
    const search = Buffer.from([0, 251, 255, 1, 251, 255]);
    const invalid = [
      () => Buffer.from('hello', 'unknown-encoding'),
      () => search.toString('unknown-encoding'),
      () => search.write('hello', 'unknown-encoding'),
      () => search.fill('hello', 'unknown-encoding'),
    ].map(action => { try { action(); return null; } catch (error) { return error.name; } });
    return { encoded, writes, fills, invalid,
      recognized: ['base64url', 'BASE64URL', 'base64', 'utf8', 'unknown-encoding']
        .map(value => Buffer.isEncoding(value)),
      allocated: Buffer.alloc(5, '-_8', 'base64url').toString('hex'),
      indexes: [search.indexOf('-_8', 'base64url'), search.lastIndexOf('-_8', 'base64url'),
        search.includes('-_8', 'base64url'), search.lastIndexOf('-_8', 2, 'base64url'),
        search.lastIndexOf('+/8=', 'base64'),
        Buffer.from('abcabc').lastIndexOf('bc', 'utf8')],
      ordinary: Buffer.from('hello', 'utf8').toString('base64'),
      utf8: Buffer.from('aGVsbG8=', 'base64').toString(),
    };
  }
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'buffer-encodings', enabled: true, source: `
      module.exports.home = () => ({
        result: (${exercise})(Buffer),
        identity: Buffer === require('buffer').Buffer && Buffer === require('node:buffer').Buffer,
        legacyConstructed: new Buffer('-_8', 'base64url').toString('hex'),
        legacyCalled: Buffer('-_8', 'BASE64URL').toString('hex'),
      });
    `,
  });
  const expected = exercise(Buffer);
  assert.equal(expected.encoded[1].text, '_w');
  assert.equal(expected.encoded[2].text, '-_8');
  assert.equal(expected.encoded[3].text, '-__v');
  assert.deepEqual(await runtime.invoke({
    installationId: 'buffer-encodings', siteId: 'default', operation: 'home', payload: {},
  }), { result: expected, identity: true, legacyConstructed: 'fbff', legacyCalled: 'fbff' });
});

test('Cat cached KeyObjects survive request retirement without retaining host key handles',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'cached-keys',
      source: `
        const crypto = require('crypto'), util = require('util'), types = require('util/types');
        let pair, asyncPair, secret, encrypted;
        const data = Buffer.from('synthetic key lifecycle data');
        module.exports.home = async () => {
          pair = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
          asyncPair = await new Promise((resolve, reject) => {
            crypto.generateKeyPair('ed25519', {}, (error, publicKey, privateKey) => {
              if (error) reject(error); else resolve({ publicKey, privateKey });
            });
          });
          secret = crypto.createSecretKey(Buffer.alloc(32, 7));
          encrypted = crypto.publicEncrypt(pair.publicKey, data);
          return { privateType: pair.privateKey.type, publicType: pair.publicKey.type,
            secretType: secret.type, bits: pair.publicKey.asymmetricKeyDetails.modulusLength,
            exponent: String(pair.publicKey.asymmetricKeyDetails.publicExponent),
            algorithm: pair.publicKey.asymmetricKeyType, secretBytes: secret.symmetricKeySize };
        };
        module.exports.search = () => {
          const pem = pair.privateKey.export({ format: 'pem', type: 'pkcs8' });
          const jwk = pair.privateKey.export({ format: 'jwk' });
          const restored = crypto.createPrivateKey(pem);
          const restoredJwk = crypto.createPrivateKey({ key: jwk, format: 'jwk' });
          const tag = crypto.sign(null, data, asyncPair.privateKey);
          const keyed = crypto.createHmac('sha256', secret).update(data).digest();
          const raw = secret.export();
          const expected = crypto.createHmac('sha256', raw).update(data).digest();
          let invalid = false;
          try { secret.equals({}); } catch (error) { invalid = error.code === 'ERR_INVALID_ARG_TYPE'; }
          return {
            decrypted: crypto.privateDecrypt(pair.privateKey, encrypted).equals(data),
            same: pair.privateKey.equals(restored) && restored.equals(restoredJwk),
            different: !secret.equals(crypto.createSecretKey(Buffer.alloc(32, 8))),
            signed: crypto.verify(null, data, asyncPair.publicKey, tag),
            hmac: keyed.equals(expected),
            exported: Buffer.isBuffer(pair.publicKey.export({ format: 'der', type: 'spki' })),
            detected: types.isKeyObject(pair.privateKey) && util.types.isKeyObject(secret),
            notDetected: !types.isKeyObject({ type: 'private' }),
            invalid, nativeBinding: typeof secret.binding, keys: Object.keys(secret).length,
          };
        };
      `,
    });
    const counts = () => ({
      handles: session.node.handles.size, callbacks: session.node.callbacks.size,
      returns: session.node.callbackReturns.size, promises: session.node.promiseLeases.size,
      contexts: session.node.contextLeases.size,
    });
    await collectGarbage();
    const baseline = counts();
    assert.deepEqual(await session.invoke({
      siteId: 'default', operation: 'home', payload: {},
    }), { privateType: 'private', publicType: 'public', secretType: 'secret',
      bits: 2048, exponent: '65537', algorithm: 'rsa', secretBytes: 32 });
    await collectGarbage();
    const bounded = value => Object.fromEntries(Object.entries(value).map(([key, count]) =>
      [key, count <= baseline[key] * 2 + 16]));
    assert.deepEqual(bounded(counts()), {
      handles: true, callbacks: true, returns: true, promises: true, contexts: true,
    });
    for (let index = 0; index < 8; index++) {
      assert.deepEqual(await session.invoke({
        siteId: 'default', operation: 'search', payload: {},
      }), { decrypted: true, same: true, different: true, signed: true, hmac: true,
        exported: true, detected: true, notDetected: true, invalid: true, nativeBinding: 'undefined', keys: 0 });
      await collectGarbage();
      assert.deepEqual(bounded(counts()), {
        handles: true, callbacks: true, returns: true, promises: true, contexts: true,
      });
    }
  });

test('Cat key transport clears exported temporary bytes and does not export nonextractable CryptoKeys', async t => {
  const scope = new ResourceScope();
  const session = {
    installationId: 'key-transport', revision: { source: '' }, root: scope,
    limits: { cleanupTimeoutMs: 100 }, live: false, scope: () => scope,
    storageWritten() {}, trackAdapter() {},
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  try {
    const secret = createSecretKey(Buffer.alloc(32, 7)), exported = secret.export();
    secret.export = () => exported;
    const wire = node.encode(secret);
    assert.equal(wire.$, 'key');
    assert.equal(exported.every(byte => byte === 0), true);
    assert.equal(node.decode(wire).equals(secret), true);
    assert.equal(node.handles.size, 0);
    assert.throws(() => node.decode({ $: 'key', type: 'unknown', data: '' }), { code: 'INVALID_ARGUMENT' });
    assert.throws(() => node.decode({ $: 'key', type: 'secret', data: {} }), { code: 'INVALID_ARGUMENT' });
    const nonextractable = await webcrypto.subtle.importKey('raw', Buffer.alloc(32, 7),
      { name: 'AES-GCM' }, false, ['encrypt']);
    const opaque = node.encode(nonextractable);
    assert.equal(opaque.$, 'handle');
    assert.equal(opaque.role, 'cryptoKey');
    assert.equal(opaque.data, undefined);
  } finally { await node.close(); scope.close(); }
});

test('Cat incoming header identity preserves decompressor edits and request cleanup',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'header-identity',
      source: `
        const http = require('http'), zlib = require('zlib');
        let server;
        const inspect = () => new Promise((resolve, reject) => {
          http.get('http://127.0.0.1:9988/gzip', response => {
            const headers = response.headers, rawHeaders = response.rawHeaders, parts = [];
            const unzip = zlib.createGunzip();
            headers['x-guest'] = 'retained';
            rawHeaders.push('X-Guest', 'retained');
            delete response.headers['content-encoding'];
            unzip.on('data', part => parts.push(part));
            unzip.once('error', reject);
            response.once('error', reject);
            unzip.once('end', () => {
              try {
                let bytes = Buffer.concat(parts);
                if (response.headers['content-encoding'] === 'gzip') bytes = zlib.gunzipSync(bytes);
                resolve({ same: headers === response.headers, rawSame: rawHeaders === response.rawHeaders,
                  removed: !('content-encoding' in response.headers),
                  retained: response.headers['x-guest'], rawRetained: response.rawHeaders.at(-1),
                  data: JSON.parse(bytes.toString()) });
              } catch (error) { reject(error); }
            });
            response.pipe(unzip);
          }).once('error', reject);
        });
        module.exports.start = () => {
          server = http.createServer(async (request, response) => {
            const send = value => {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify(value));
            };
            if (request.url === '/config' || request.url === '/full-config')
              return send({ video: { sites: [{ key: 'headers', api: '/headers', actions: ['home'] }] } });
            if (request.url === '/gzip') {
              response.setHeader('content-encoding', 'gzip');
              response.end(zlib.gzipSync(Buffer.from('{"value":42}')));
              return;
            }
            try { send(await inspect()); }
            catch (error) { response.statusCode = 500; send({ error: error.code }); }
          });
          server.listen(9988);
        };
        module.exports.stop = () => server.close();
      `,
    });
    const counts = () => ({
      handles: session.node.handles.size, callbacks: session.node.callbacks.size,
      returns: session.node.callbackReturns.size, contexts: session.node.contextLeases.size,
      requests: session.nativeRequests.size, scopeTokens: session.services.scopes.size,
    });
    let baseline, midpoint;
    for (let index = 0; index < 8; index++) {
      assert.deepEqual(await session.invoke({
        siteId: 'headers', operation: 'home', payload: {},
      }), { same: true, rawSame: true, removed: true, retained: 'retained',
        rawRetained: 'retained', data: { value: 42 } });
      await collectGarbage();
      baseline ??= counts();
      if (index === 3) midpoint = counts();
      const current = counts();
      for (const key of ['handles', 'callbacks', 'returns', 'contexts']) {
        assert.ok(current[key] <= baseline[key] * 2 + 16,
          `${key} grew from ${baseline[key]} to ${current[key]}`);
      }
    }
    assert.ok(midpoint.handles <= baseline.handles * 2 + 16);
    assert.ok(midpoint.callbacks <= baseline.callbacks * 2 + 16);
  });

test('Cat HTTP modules preserve protocol mismatch errors without opening a connection', async t => {
  const diagnostics = [], network = [];
  const runtime = createCatRuntime({
    dataRoot: rootFor(t), onDiagnostic: value => diagnostics.push(value), onNetwork: value => network.push(value),
  });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'protocol-mismatch', enabled: true, source: `
      const http = require('http'), https = require('https');
      module.exports.home = () => ({
        codes: [[https, 'http://127.0.0.1:9988/'], [http, 'https://127.0.0.1:9988/']].map(([api, url]) => {
          try { api.get(url); return 'unexpected'; } catch (error) { return error.code; }
        }),
      });
    `,
  });
  assert.deepEqual(await runtime.invoke({
    installationId: 'protocol-mismatch', siteId: 'default', operation: 'home', payload: {},
  }), { codes: ['ERR_INVALID_PROTOCOL', 'ERR_INVALID_PROTOCOL'] });
  assert.equal(network.length, 0);
  assert.deepEqual(diagnostics.filter(value => value.reason === 'host').map(value => value.code),
    ['ERR_INVALID_PROTOCOL', 'ERR_INVALID_PROTOCOL']);
  await runtime.install({
    installationId: 'protocol-guest-visible', enabled: true, source: `
      const http = require('http');
      module.exports.home = () => {
        try { http.get('https://127.0.0.1:9988/'); }
        catch (error) { return { code: error.code, name: error.name, trusted: error.code !== 'SCRIPT_EXCEPTION' }; }
      };
    `,
  });
  assert.deepEqual(await runtime.invoke({
    installationId: 'protocol-guest-visible', siteId: 'default', operation: 'home', payload: {},
  }), { code: 'ERR_INVALID_PROTOCOL', name: 'Error', trusted: true });
});

test('Cat HTTP option projection is one rule and ignores unrelated library metadata', () => {
  const options = {
    protocol: 'http:', hostname: 'example.invalid', port: 80, path: '/',
    agent: false, createConnection() {}, lookup() {}, socketPath: '/tmp/socket',
    unrelated: { cyclic: true },
  };
  options.self = options;
  assert.deepEqual(projectHttpRequestOptions(options), {
    protocol: 'http:', hostname: 'example.invalid', port: 80, path: '/',
    agent: false, lookup: options.lookup, socketPath: '/tmp/socket',
  });
  assert.deepEqual(projectFields({ path: '/x', query: { a: 1 }, unrelated: 1 },
    ['path', 'query']), { path: '/x', query: { a: 1 } });
});

test('Cat startup discovery resolves closure wrappers without treating strings or comments as declarations', () => {
  const source = `
    const embedded = "function decoy() { catServerFactory(() => {}); }";
    // function commented() { catServerFactory(() => {}); }
    const boot = async () => {
      const braces = "} {";
      return catServerFactory(() => {});
    };
    function forward() { return boot(); }
    function business() { boot(); return {}; }
    function cyclic() { return cyclic(); }
  `;
  let indexed = 0;
  const lookup = createLocalStartupLookup(source, {
    tokenizer(...args) { indexed++; return tokenizer(...args); }, parseExpressionAt,
  }, text => text.includes('catServerFactory'), startupWrapperTarget);
  assert.equal(lookup('boot'), true);
  assert.equal(lookup('forward'), true);
  assert.equal(lookup('business'), false);
  assert.equal(lookup('decoy'), false);
  assert.equal(lookup('commented'), false);
  assert.equal(lookup('cyclic'), false);
  assert.equal(lookup('other'), false);
  assert.equal(indexed, 1);
});

test('Cat runtime shares each server termination and retains cleanup failures without retaining server keys', async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  const server = new EventEmitter();
  server.listening = true;
  let attempts = 0;
  const failure = new Error('synthetic server termination failure');
  server.close = () => {
    attempts++;
    server.listening = false;
    server.emit('close');
    throw failure;
  };
  const first = runtime.closeOwnedServer(server);
  const second = runtime.closeOwnedServer(server);
  await assert.rejects(first, error => error === failure);
  await assert.rejects(second, error => error === failure);
  assert.equal(attempts, 1);
  assert.equal(runtime.pendingServerClosures.size, 0);
  await assert.rejects(runtime.drainServerClosures(), { code: 'SCRIPT_EXCEPTION' });
  await assert.rejects(runtime.close(), { code: 'SCRIPT_EXCEPTION' });
});

test('Cat TLS peer certificate chains retain cycles through finite data roles', async t => {
  const scope = new ResourceScope();
  const session = {
    installationId: 'peer-certificate', revision: { source: '' }, root: scope,
    limits: { cleanupTimeoutMs: 100 }, live: false, scope: () => scope,
    storageWritten() {}, trackAdapter() {},
  };
  const node = new NodeCapabilities(session, { close: async () => {} }, rootFor(t));
  const certificate = { subject: { CN: 'fixture.invalid' }, raw: Buffer.from('certificate') };
  certificate.issuerCertificate = certificate;
  const value = node.invoke({ role: 'tlsSocket', value: {
    getPeerCertificate(detailed) { assert.equal(detailed, true); return certificate; },
  } }, 'getPeerCertificate', [true]);
  const encoded = node.encode(value, new Set(), undefined, scope);
  const receipt = node.handles.get(encoded.id);
  assert.equal(receipt.role, 'certificate');
  assert.equal(node.member(receipt, 'issuerCertificate', scope).id, encoded.id);
  assert.deepEqual(node.member(receipt, 'subject', scope), { $: 'object', value: { CN: 'fixture.invalid' } });
  assert.throws(() => node.member(receipt, 'constructor', scope), { code: 'PERMISSION_DENIED' });
  await node.close();
  scope.close();
});

test('Cat direct spider maps the standard HTTP payload to positional category and search arguments',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'positions', enabled: true, source: `
        module.exports = {
          category(tid, pg, filter, extend) { return { tid, pg, filter, extend }; },
          search(wd, quick, pg) { return { wd, quick, pg }; },
        };
      `,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'positions', siteId: 'default', operation: 'category',
      payload: { id: 'movie', page: 2, filter: true, filters: { year: '2026' } },
    }), { tid: 'movie', pg: 2, filter: true, extend: { year: '2026' } });
    assert.deepEqual(await runtime.invoke({
      installationId: 'positions', siteId: 'default', operation: 'search',
      payload: { wd: '电影', quick: false, page: 3 },
    }), { wd: '电影', quick: false, pg: 3 });
  });

test('Cat Web and crypto compatibility stay guest-local',
  { timeout: 30000 }, async t => {
    const compatibilitySource = String.raw`
      const crypto = require('crypto');
      const dns = require('dns');
      class RuntimeEvent extends Event {}
      module.exports = {
        home() {
          dns.setDefaultResultOrder('ipv4first');
          const web = new Uint32Array(2);
          const node = new Uint16Array(4);
          return {
            webSame: crypto.webcrypto.getRandomValues(web) === web,
            nodeSame: crypto.getRandomValues(node) === node,
            web: Array.from(web),
            node: Array.from(node),
            order: dns.getDefaultResultOrder(),
            events: (() => {
              const target = new EventTarget();
              let once = 0;
              let object = 0;
              let aborted = 0;
              let gated = 0;
              target.addEventListener('ready', event => {
                once++;
                event.preventDefault();
              }, { once: true });
              target.addEventListener('ready', { handleEvent() { object++; } });
              const first = new RuntimeEvent('ready', { cancelable: true });
              const dispatched = target.dispatchEvent(first);
              target.dispatchEvent(new RuntimeEvent('ready'));
              const gatedController = new AbortController();
              target.addEventListener('gated', () => gated++, { signal: gatedController.signal });
              gatedController.abort();
              target.dispatchEvent(new Event('gated'));
              const controller = new AbortController();
              controller.signal.onabort = event => {
                if (event instanceof Event && event.target === controller.signal) aborted++;
              };
              controller.abort('stopped');
              const custom = new CustomEvent('custom', { detail: { value: 7 } });
              return {
                constructors: first instanceof Event && target instanceof EventTarget &&
                  controller.signal instanceof EventTarget,
                tags: [first[Symbol.toStringTag], target[Symbol.toStringTag],
                  custom[Symbol.toStringTag], controller.signal[Symbol.toStringTag]],
                dispatched,
                prevented: first.defaultPrevented,
                once,
                object,
                gated,
                aborted,
                reason: controller.signal.reason,
                detail: custom.detail.value,
                phase: Event.AT_TARGET,
              };
            })(),
            base64: btoa(atob('bWVvdw==')),
            resourceTiming: typeof performance.markResourceTiming,
            clone: (() => {
             const buffer = new ArrayBuffer(8);
             const typed = new Uint16Array(buffer);
             const view = new DataView(buffer);
             typed[0] = 513;
             view.setUint8(2, 7);
             const source = {
               date: new Date(123),
               map: new Map([['key', { value: 1 }]]),
               typed,
               view,
             };
             source.self = source;
              const result = structuredClone(source);
              const transferred = new ArrayBuffer(4);
              const transferCopy = structuredClone(transferred, { transfer: [transferred] });
              return {
               distinct: result !== source && result.map.get('key') !== source.map.get('key'),
               cycle: result.self === result,
               date: result.date.getTime(),
               value: result.map.get('key').value,
               typedDistinct: result.typed !== typed && result.typed.buffer !== buffer,
               typedShared: result.typed.buffer === result.view.buffer,
                typedValue: result.typed[0],
                viewValue: result.view.getUint8(2),
                transferDetached: transferred.byteLength === 0,
                transferLength: transferCopy.byteLength,
             };
           })(),
         };
        },
      };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'typed-crypto', source: compatibilitySource, config: {},
      version: '1', enabled: true,
    });
    const result = await runtime.invoke({
      installationId: 'typed-crypto', siteId: 'default', operation: 'home', payload: {},
    });
    assert.equal(result.webSame, true);
    assert.equal(result.nodeSame, true);
    assert.equal(result.order, 'ipv4first');
    assert.equal(result.web.some(value => value !== 0), true);
    assert.equal(result.node.some(value => value !== 0), true);
    assert.deepEqual(result.events, {
      constructors: true, dispatched: false, prevented: true,
      tags: ['Event', 'EventTarget', 'CustomEvent', 'AbortSignal'],
      once: 1, object: 2, gated: 0, aborted: 1, reason: 'stopped', detail: 7, phase: 2,
    });
    assert.equal(result.base64, 'bWVvdw==');
    assert.equal(result.resourceTiming, 'function');
    assert.deepEqual(result.clone, {
      distinct: true, cycle: true, date: 123, value: 1,
      typedDistinct: true, typedShared: true, typedValue: 513, viewValue: 7,
      transferDetached: true, transferLength: 4,
    });
  });

test('Cat preserves complete declared action inventories and never probes native servers for hasRoute',
  { timeout: 30000 }, async t => {
    const diagnostics = [];
    const declaredSource = String.raw`
      const http = require('http');
      let server;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'declared', name: 'Declared', api: '/api/declared',
              actions: Array.from({ length: 1000 }, (_, index) => 'action' + index),
            }] } }));
            return;
          }
          if (request.url === '/api/declared/action999') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
        return server;
      };
      module.exports.stop = () => { server.closeAllConnections(); server.close(); };
    `;
    const runtime = createCatRuntime({
      dataRoot: rootFor(t),
      onDiagnostic: diagnostic => diagnostics.push(diagnostic),
    });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'declared-actions', source: declaredSource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.equal(instance.sites[0].actions.length, 1000);
    assert.equal(instance.sites[0].actions[0], 'action0');
    assert.equal(instance.sites[0].actions.at(-1), 'action999');
    assert.equal(diagnostics.some(event =>
      event.operation === 'node.get' && event.member === 'hasRoute'), false);
    assert.deepEqual(await runtime.invoke({
      installationId: 'declared-actions', siteId: 'declared', operation: 'action999', payload: {},
    }), { class: [] });
  });

test('Cat waits for an asynchronous guest stop before releasing the private volume',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const asyncStopSource = String.raw`
      const fs = require('fs');
      const http = require('http');
      let server;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'async-stop', name: 'Async stop', api: '/api/async-stop', actions: ['home'],
            }] } }));
            return;
          }
          if (request.url === '/api/async-stop/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => new Promise(resolve => {
        setTimeout(() => {
          fs.mkdirSync('/shutdown', { recursive: true });
          fs.chmodSync('/shutdown', 0o700);
          fs.writeFileSync('/shutdown/flushed.txt', 'done');
          fs.utimesSync('/shutdown/flushed.txt', new Date(), new Date());
          server.closeAllConnections();
          server.close(resolve);
        }, 20);
      });
    `;
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'async-stop', source: asyncStopSource, config: {},
      version: '1', enabled: true,
    });
    await runtime.setEnabled('async-stop', false);
    assert.equal(readFileSync(join(root, 'volumes', 'async-stop', 'shutdown', 'flushed.txt'), 'utf8'), 'done');
  });

test('Cat marks a session inactive before destroying residual native requests',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'fixture-a',
      source,
      config: { id: 'site-a', protocol: 'old' },
    });
    const observed = [];
    const request = {
      destroyed: false,
      closed: false,
      destroy() {
        observed.push(session.live);
        this.destroyed = true;
        this.closed = true;
      },
    };
    session.nativeRequests.add(request);
    await session.stop();
    assert.deepEqual(observed, [false]);
  });

test('Cat lifecycle calls detach a failed session and report the cleanup failure',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(async () => {
      try { await runtime.close(); } catch {}
    });
    await runtime.install({
      installationId: 'fixture-a', source, config: { id: 'site-a', protocol: 'old' },
      version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'fixture-b', source, config: { id: 'site-b', protocol: 'old' },
      version: '1', enabled: true,
    });
    const session = runtime.getSession('fixture-a');
    const stop = session.stop.bind(session);
    session.stop = async () => {
      await stop();
      throw fault('SERVICE_ERROR');
    };
    await assert.rejects(runtime.setEnabled('fixture-a', false), { code: 'SERVICE_ERROR' });
    const host = runtime.host;
    assert.equal(host.get('fixture-a').resources.live, false);
    assert.equal(runtime.sessions.has('fixture-a'), false);
    assert.equal(host.stats().liveSessions, 1);
    assert.equal((await runtime.status()).instances.find(instance =>
      instance.installationId === 'fixture-a').state, 'failed');

    const other = runtime.getSession('fixture-b');
    const stopOther = other.stop.bind(other);
    other.stop = async () => {
      await stopOther();
      throw fault('SERVICE_ERROR');
    };
    await assert.rejects(runtime.close(), { code: 'SERVICE_ERROR' });
    assert.equal(host.stats().liveSessions, 0);
    assert.equal(host.stats().installations, 0);
    assert.equal(runtime.host, null);
    assert.equal(runtime.sessions.size, 0);
  });

test('Cat low-level cleanup failures release native ownership and session scope',
  { timeout: 30000 }, async t => {
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'fixture-a',
      source,
      config: { id: 'site-a', protocol: 'old' },
    });
    const close = session.node.close.bind(session.node);
    session.node.close = async () => {
      await close();
      throw Object.assign(new Error('synthetic native cleanup failure'), { code: 'IGNORED' });
    };
    await assert.rejects(session.stop(), { code: 'SERVICE_ERROR' });
    assert.equal(session.live, false);
    assert.equal(session.root.signal.aborted, true);
    assert.equal(session.node.handles.size, 0);
    assert.equal(session.node.callbacks.size, 0);
    assert.equal(session.node.callbackReturns.size, 0);
    assert.equal(session.node.contextLeases.size, 0);
    assert.equal(session.node.promiseLeases.size, 0);
    assert.equal(session.stats().cleanupError, 'SERVICE_ERROR');
  });

test('Cat uninstall publishes the durable removal after cleanup failure', { timeout: 30000 }, async t => {
  const root = rootFor(t);
  const runtime = createCatRuntime({ dataRoot: root });
  t.after(() => runtime.close().catch(() => {}));
  await runtime.install({
    installationId: 'fixture-a',
    source,
    config: { id: 'site-a', protocol: 'old' },
    version: '1',
    enabled: true,
  });
  const sourceFile = join(root, 'sources',
    runtime.host.get('fixture-a').contentHash + '.js');
  const session = runtime.getSession('fixture-a');
  const stop = session.stop.bind(session);
  session.stop = async () => {
    await stop();
    throw fault('SERVICE_ERROR');
  };
  const settingsPath = (await runtime.status()).settingsPath;
  await assert.rejects(runtime.uninstall('fixture-a'), { code: 'SERVICE_ERROR' });
  assert.equal(runtime.host.stats().liveSessions, 0);
  assert.equal(runtime.sessions.has('fixture-a'), false);
  assert.deepEqual(JSON.parse(readFileSync(settingsPath, 'utf8')).scripts, []);
  assert.deepEqual(runtime.settings.value.scripts, []);
  assert.deepEqual((await runtime.status()).instances, []);
  assert.equal(runtime.applied.has('fixture-a'), false);
  assert.equal(runtime.failures.has('fixture-a'), false);
  assert.equal(existsSync(join(root, 'volumes', 'fixture-a')), false);
  assert.equal(existsSync(sourceFile), false);
});

test('Cat uninstall deletes the private volume and unreferenced source but keeps shared source',
  { timeout: 60000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const shared = volumeSource('shared');
    const unique = volumeSource('unique');
    await runtime.install({
      installationId: 'remove-a', source: unique, config: {}, version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'remove-b', source: shared, config: {}, version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'remove-c', source: shared, config: {}, version: '1', enabled: true,
    });
    await runtime.invoke({
      installationId: 'remove-a', siteId: 'unique', operation: 'write', payload: {},
    });
    await runtime.invoke({
      installationId: 'remove-b', siteId: 'shared', operation: 'write', payload: {},
    });
    const uniqueHash = sourceHash(unique);
    const sharedHash = sourceHash(shared);
    assert.equal(existsSync(sourceFile(root, uniqueHash)), true);
    assert.equal(existsSync(sourceFile(root, sharedHash)), true);
    assert.equal(existsSync(join(root, 'volumes', 'remove-a')), true);

    await runtime.uninstall('remove-a');
    assert.equal(existsSync(join(root, 'volumes', 'remove-a')), false);
    assert.equal(existsSync(sourceFile(root, uniqueHash)), false);
    assert.equal(existsSync(sourceFile(root, sharedHash)), true);
    assert.deepEqual(await runtime.invoke({
      installationId: 'remove-b', siteId: 'shared', operation: 'read', payload: {},
    }), { value: 'shared' });

    await runtime.uninstall('remove-b');
    assert.equal(existsSync(join(root, 'volumes', 'remove-b')), false);
    assert.equal(existsSync(sourceFile(root, sharedHash)), true);
    assert.deepEqual(await runtime.invoke({
      installationId: 'remove-c', siteId: 'shared', operation: 'read', payload: {},
    }), { value: null });
    await runtime.uninstall('remove-c');
    assert.equal(existsSync(sourceFile(root, sharedHash)), false);
  });

test('Cat refresh removing a script deletes the same artifacts as uninstall',
  { timeout: 60000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const sourceText = volumeSource('refresh-remove');
    await runtime.install({
      installationId: 'refresh-remove', source: sourceText, config: {},
      version: '1', enabled: true,
    });
    await runtime.invoke({
      installationId: 'refresh-remove', siteId: 'refresh-remove', operation: 'write', payload: {},
    });
    const hash = sourceHash(sourceText);
    writeSettings(runtime.settings.path, value => ({
      ...value,
      scripts: value.scripts.filter(script => script.installationId !== 'refresh-remove'),
    }));
    assert.deepEqual(await runtime.refresh(), { applied: true, failures: [] });
    assert.equal(existsSync(join(root, 'volumes', 'refresh-remove')), false);
    assert.equal(existsSync(sourceFile(root, hash)), false);
    assert.deepEqual((await runtime.status()).instances, []);
  });

test('Cat reinstall starts from an empty private volume after uninstall',
  { timeout: 60000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'reinstall', source: volumeSource('reinstall'), config: {},
      version: '1', enabled: true,
    });
    await runtime.invoke({
      installationId: 'reinstall', siteId: 'reinstall', operation: 'write', payload: {},
    });
    await runtime.uninstall('reinstall');
    await runtime.install({
      installationId: 'reinstall', source: volumeSource('reinstall'), config: {},
      version: '1', enabled: true,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'reinstall', siteId: 'reinstall', operation: 'read', payload: {},
    }), { value: null });
  });

test('Cat stop, disable, update and restart preserve the private volume',
  { timeout: 60000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const original = volumeSource('keep-volume');
    await runtime.install({
      installationId: 'keep-volume', source: original, config: {}, version: '1', enabled: true,
    });
    await runtime.invoke({
      installationId: 'keep-volume', siteId: 'keep-volume', operation: 'write', payload: {},
    });
    const read = () => runtime.invoke({
      installationId: 'keep-volume', siteId: 'keep-volume', operation: 'read', payload: {},
    });
    assert.deepEqual(await read(), { value: 'keep-volume' });
    await runtime.setEnabled('keep-volume', false);
    await runtime.setEnabled('keep-volume', true);
    assert.deepEqual(await read(), { value: 'keep-volume' });
    await runtime.install({
      installationId: 'keep-volume',
      source: volumeSource('keep-volume', '// updated revision'),
      config: {}, version: '2', enabled: true,
    });
    assert.deepEqual(await read(), { value: 'keep-volume' });
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: root });
    t.after(() => reopened.close());
    await reopened.start();
    await waitForState(reopened, 'keep-volume', 'ready');
    assert.deepEqual(await reopened.invoke({
      installationId: 'keep-volume', siteId: 'keep-volume', operation: 'read', payload: {},
    }), { value: 'keep-volume' });
  });

test('Cat restart does not clean a residual volume but reinstall starts it empty',
  { timeout: 60000 }, async t => {
    const root = rootFor(t);
    mkdirSync(join(root, 'volumes', 'residual'), { recursive: true });
    writeFileSync(join(root, 'volumes', 'residual', 'old.txt'), 'old');
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.start();
    assert.equal(readFileSync(join(root, 'volumes', 'residual', 'old.txt'), 'utf8'), 'old');
    await runtime.install({
      installationId: 'residual', source: volumeSource('residual'), config: {},
      version: '1', enabled: true,
    });
    assert.equal(existsSync(join(root, 'volumes', 'residual', 'old.txt')), false);
    assert.deepEqual(await runtime.invoke({
      installationId: 'residual', siteId: 'residual', operation: 'read', payload: {},
    }), { value: null });
  });

test('Cat reports volume deletion failure without affecting another instance',
  { timeout: 60000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'remove-fails', source: volumeSource('remove-fails'), config: {},
      version: '1', enabled: true,
    });
    await runtime.install({
      installationId: 'remove-works', source: volumeSource('remove-works'), config: {},
      version: '1', enabled: true,
    });
    await runtime.invoke({
      installationId: 'remove-fails', siteId: 'remove-fails', operation: 'write', payload: {},
    });
    await runtime.invoke({
      installationId: 'remove-works', siteId: 'remove-works', operation: 'write', payload: {},
    });
    const blocked = resolve(root, 'volumes', 'remove-fails');
    const originals = Object.fromEntries(['rmSync', 'rmdirSync', 'unlinkSync']
      .map(name => [name, fs[name]]));
    const failure = () => Object.assign(
      new Error('synthetic volume deletion failure'), { code: 'EBUSY' });
    for (const name of Object.keys(originals)) {
      fs[name] = (target, ...args) => {
        if (resolve(target) === blocked) throw failure();
        return originals[name](target, ...args);
      };
    }
    syncBuiltinESMExports();
    try {
      const result = await runtime.uninstall('remove-fails');
      assert.equal(result.applied, false);
      assert.ok(result.failures.some(item => item.installationId === 'remove-fails'));
    } finally {
      for (const [name, original] of Object.entries(originals)) fs[name] = original;
      syncBuiltinESMExports();
    }
    assert.equal(runtime.failures.has('remove-fails'), true);
    assert.equal(existsSync(join(root, 'volumes', 'remove-fails')), true);
    assert.deepEqual(await runtime.invoke({
      installationId: 'remove-works', siteId: 'remove-works', operation: 'read', payload: {},
    }), { value: 'remove-works' });
    await runtime.uninstall('remove-works');
    assert.equal(existsSync(join(root, 'volumes', 'remove-works')), false);
  });

test('Cat runtime close rejects incomplete host cleanup after releasing runtime ownership',
  async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    await runtime.start();
    const close = runtime.host.close.bind(runtime.host);
    runtime.host.close = async () => ({
      ...await close(),
      pendingAdapterTasks: 1,
      cleanupComplete: false,
    });
    await assert.rejects(runtime.close(), { code: 'TIMEOUT' });
    assert.equal(runtime.host, null);
    assert.equal(runtime.server, null);
    await assert.rejects(runtime.status(), { code: 'HOST_CLOSED' });
  });

test('Cat site APIs without an explicit HTTP port map default port 80 and never register alias 0',
  { timeout: 30000 }, async t => {
    const defaultPortSource = String.raw`
      const http = require('http');
      let server;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'default-port', name: 'Default port',
              api: 'http://127.0.0.1/api/default-port', actions: ['home'],
            }] } }));
            return;
          }
          if (request.url === '/api/default-port/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'default-port', source: defaultPortSource, config: {},
      version: '1', enabled: true,
    });
    const instance = (await runtime.status()).instances[0];
    assert.ok(instance.endpoints.some(endpoint => endpoint.requestedPorts.includes(80)));
    assert.equal(instance.endpoints.some(endpoint => endpoint.requestedPorts.includes(0)), false);
    assert.deepEqual(await runtime.invoke({
      installationId: 'default-port', siteId: 'default-port', operation: 'home', payload: {},
    }), { class: [] });
  });

test('Cat and LX invocation waits share the 60 second request deadline',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = await installed(t, root);
    assert.equal(runtime.host.limits.requestTimeoutMs, 60000);
    const instance = (await runtime.status()).instances.find(value =>
      value.installationId === 'fixture-a');
    assert.equal(instance.error, null);
    await assert.rejects(runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'home', payload: {},
    }, { timeoutMs: 60001 }), { code: 'INVALID_ARGUMENT' });
    const controller = new AbortController();
    const timed = runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'slow', payload: {},
    }, { signal: controller.signal, timeoutMs: 60000 });
    controller.abort();
    await assert.rejects(timed, { code: 'CANCELLED' });
    const host = createScriptHost();
    t.after(() => host.close());
    assert.equal(host.limits.requestTimeoutMs, 60000);
    await host.install({ installationId: 'wait', enabled: true, source: lxScript() });
    await assert.rejects(host.invoke({ installationId: 'wait', siteId: 'kw',
      operation: 'musicUrl', payload: { quality: '320k', musicInfo: {} } },
    { timeoutMs: 60001 }), { code: 'INVALID_ARGUMENT' });
    assert.equal((await host.invoke({ installationId: 'wait', siteId: 'kw',
      operation: 'musicUrl', payload: { quality: '320k', musicInfo: {} } },
    { timeoutMs: 60000 })).action, 'musicUrl');
  });

test('Cat scripts reach unregistered private, loopback and local-name hosts',
  { timeout: 30000 }, async t => {
    const upstream = http.createServer((request, response) => {
      if (request.url === '/dns') {
        response.end(JSON.stringify({ reached: true }));
        return;
      }
      response.end('reachable');
    });
    await new Promise((resolve, reject) => {
      upstream.once('error', reject);
      upstream.listen(0, '0.0.0.0', resolve);
    });
    t.after(() => new Promise(resolve => {
      upstream.closeAllConnections?.();
      upstream.close(resolve);
    }));
    const port = upstream.address().port;
    const addresses = Object.values(networkInterfaces()).flat()
      .filter(item => item.family === 'IPv4' && !item.internal)
      .map(item => item.address);
    const ten = addresses.filter(address => address.startsWith('10.'));
    const oneNinetyTwo = addresses.filter(address => address.startsWith('192.168.'));
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'network-open', enabled: true, config: {}, version: '1', source: String.raw`
        const http = require('http'), dns = require('dns');
        let server;
        const get = value => new Promise((resolve, reject) => {
          http.get(value, incoming => {
            const chunks = [];
            incoming.on('data', chunk => chunks.push(Buffer.from(chunk)));
            incoming.once('end', () => resolve({ status: incoming.statusCode,
              body: Buffer.concat(chunks).toString() }));
            incoming.once('error', reject);
          }).once('error', reject);
        });
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer(async (request, response) => {
            if (request.url === '/config' || request.url === '/full-config') {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify({ video: { sites: [{
                key: 'open', name: 'Open', api: '/open', actions: ['probe'],
              }] } }));
              return;
            }
            const targets = await new Promise((resolve, reject) => {
              const chunks = [];
              request.on('data', chunk => chunks.push(Buffer.from(chunk)));
              request.once('error', reject);
              request.once('end', () => {
                try { resolve(JSON.parse(Buffer.concat(chunks).toString())); }
                catch (error) { reject(error); }
              });
            });
            const values = {};
            for (const [name, value] of Object.entries(targets)) {
              try { values[name] = await get(value); }
              catch (error) { values[name] = { code: error.code }; }
            }
            values.lookup = await new Promise(resolve => dns.lookup('localhost', (error, address) => {
              resolve(error ? { code: error.code } : { address });
            }));
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify(values));
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
        module.exports.stop = () => server?.close();
      `,
    });
    const targets = {
      loopback: 'http://127.0.0.1:' + port + '/loopback',
      ...Object.fromEntries(ten.map((address, index) =>
        ['ten' + index, 'http://' + address + ':' + port + '/private'])),
      ...Object.fromEntries(oneNinetyTwo.map((address, index) =>
        ['oneNinetyTwo' + index, 'http://' + address + ':' + port + '/private'])),
      localName: 'http://fixture.local:' + port + '/local',
    };
    const result = await runtime.invoke({
      installationId: 'network-open', siteId: 'open', operation: 'probe',
      payload: targets,
    });
    assert.equal(result.loopback.status, 200);
    assert.equal(result.loopback.body, 'reachable');
    for (const [name, value] of Object.entries(result)) {
      if (!/^(?:ten|oneNinetyTwo)\d+$/.test(name)) continue;
      assert.equal(value.status, 200, name);
      assert.equal(value.body, 'reachable', name);
    }
    if (!ten.length && !oneNinetyTwo.length)
      t.diagnostic('test host has no 10.x or 192.168.x IPv4 address; only loopback checked');
    assert.notEqual(result.localName.code, 'PERMISSION_DENIED');
    const lookup = String(result.lookup.address ?? '');
    assert.ok(lookup === '127.0.0.1' || lookup === '::1', lookup);
  });

test('Cat rejects credentialed, routed and unmapped HTTPS site APIs', { timeout: 30000 }, async t => {
  const invalid = [
    'http://user@127.0.0.1/api/invalid',
    'http://127.0.0.1/api/invalid?route=1',
    'http://127.0.0.1/api/invalid#route',
    'https://127.0.0.1/api/invalid',
  ];
  for (const [index, api] of invalid.entries()) {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    const invalidSource = `
      const http = require('http');
      let server;
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ video: { sites: [{
              key: 'invalid', name: 'Invalid', api: ${JSON.stringify(api)}, actions: ['home'],
            }] } }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    `;
    await assert.rejects(runtime.install({
      installationId: `invalid-${index}`, source: invalidSource, config: {},
      version: '1', enabled: true,
    }), { code: 'SCRIPT_PROTOCOL_ERROR' });
  }
});

test('Cat management accepts the public 189 provider name', async () => {
  const calls = [];
  const session = {
    services: {
      endpoint: () => ({ origin: 'http://127.0.0.1:4555' }),
      endpoints: () => [],
    },
    async management(path, body, method, options) {
      calls.push({ path, body, method, options });
      if (path === '/website/api/credentials') {
        return { code: 0, data: { pan189: { account: '', password: '' } } };
      }
      if (path === '/website/api/pan189/account') return { code: 0 };
      return { code: 1 };
    },
  };
  assert.deepEqual(await syncCatManagement(session, {
    provider: '189', credentials: { username: 'user', password: 'pass' },
  }), { status: 'saved', protocol: 'website-api-v1' });
  assert.deepEqual(calls.at(-1), {
    path: '/website/api/pan189/account',
    body: { account: 'user', password: 'pass' },
    method: 'PUT',
    options: { requireWrite: true, origin: 'http://127.0.0.1:4555' },
  });
});

test('Cat rejects a hard-linked settings authority', { timeout: 30000 }, async t => {
  const root = rootFor(t);
  const runtime = await installed(t, root);
  const settings = (await runtime.status()).settingsPath;
  await runtime.close();
  renameSync(settings, settings + '.original');
  linkSync(settings + '.original', settings);
  const reopened = createCatRuntime({ dataRoot: root });
  t.after(() => reopened.close());
  await assert.rejects(reopened.start(), { code: 'CORRUPT_STORAGE' });
});

test('Cat rejects hard-linked immutable source content', { timeout: 30000 }, async t => {
  const root = rootFor(t);
  const runtime = await installed(t, root);
  const status = await runtime.status();
  const hash = JSON.parse(readFileSync(status.settingsPath, 'utf8')).scripts[0].sourceHash;
  const path = join(root, 'sources', hash + '.js');
  await runtime.close();
  renameSync(path, path + '.original');
  linkSync(path + '.original', path);
  const reopened = createCatRuntime({ dataRoot: root });
  t.after(() => reopened.close());
  await assert.rejects(reopened.start(), { code: 'CORRUPT_STORAGE' });
});

test('Cat normalizes a missing live source directory to CORRUPT_STORAGE', { timeout: 30000 }, async t => {
  const root = rootFor(t);
  const runtime = await installed(t, root);
  rmSync(join(root, 'sources'), { recursive: true, force: true });
  await assert.rejects(runtime.install({
    installationId: 'fixture-next', source, config: { id: 'site-next', protocol: 'old' },
    version: '1', enabled: false,
  }), { code: 'CORRUPT_STORAGE' });
});

test('Cat applies the persisted JSON config shape and rejects oversized versions before installation',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'json-shape',
      source,
      config: {
        id: 'json-shape',
        protocol: 'old',
        jsonValue: { when: new Date(0), map: new Map([['private', 'value']]) },
      },
      version: '1',
      enabled: true,
    });
    const result = await runtime.invoke({
      installationId: 'json-shape', siteId: 'json-shape', operation: 'probe', payload: {},
    });
    assert.deepEqual(result.configValue, {
      when: '1970-01-01T00:00:00.000Z',
      map: {},
    });
    const settings = JSON.parse(readFileSync((await runtime.status()).settingsPath, 'utf8'));
    assert.deepEqual(settings.scripts[0].config.jsonValue, result.configValue);
    await assert.rejects(runtime.install({
      installationId: 'oversized-version', source, config: {},
      version: 'x'.repeat(129), enabled: false,
    }), { code: 'INVALID_ARGUMENT' });
    assert.equal((await runtime.status()).instances.some(item =>
      item.installationId === 'oversized-version'), false);
  });

test('Cat runtime cannot be restarted or queried after close', async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  await runtime.start();
  await runtime.close();
  await assert.rejects(runtime.start(), { code: 'HOST_CLOSED' });
  await assert.rejects(runtime.status(), { code: 'HOST_CLOSED' });
});

test('Cat rejects UTF-8 source content with a BOM', async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await assert.rejects(runtime.install({
    installationId: 'bom-source', source: '\uFEFFmodule.exports = {};', config: {},
    version: '1', enabled: false,
  }), { code: 'SCRIPT_PROTOCOL_ERROR' });
});

test('Cat accepts a synchronous guest callback result larger than the external message limit',
  { timeout: 30000 }, async t => {
    const oversizedSource = String.raw`
      const http = require('http');
      let server;
      const config = JSON.stringify({ video: { sites: [{
        key: 'oversized', name: 'Oversized', api: '/api/oversized', actions: ['home'],
      }] } });
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          if (request.url === '/full-config' || request.url === '/config') {
            response.setHeader('content-type', 'application/json');
            response.end(config);
            return 'x'.repeat(9 * 1024 * 1024);
          }
          if (request.url === '/api/oversized/home') {
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ class: [] }));
            return;
          }
          response.statusCode = 404;
          response.end();
        });
        server.listen(Number(process.env.PORT), '127.0.0.1');
      };
      module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
    `;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'oversized-callback', source: oversizedSource, config: {},
      version: '1', enabled: true,
    });
    assert.deepEqual(await runtime.invoke({
      installationId: 'oversized-callback', siteId: 'oversized', operation: 'home', payload: {},
    }), { class: [] });
  });
