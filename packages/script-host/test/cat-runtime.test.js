import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import net from 'node:net';
import { createHash, generateKeyPairSync, sign, X509Certificate } from 'node:crypto';
import { inflateSync } from 'node:zlib';
import { rootCertificates } from 'node:tls';
import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import { existsSync, linkSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, symlinkSync,
  writeFileSync } from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { networkInterfaces, tmpdir, totalmem, cpus, hostname } from 'node:os';
import { PassThrough } from 'node:stream';
import { join, resolve } from 'node:path';
import v8 from 'node:v8';
import { runInNewContext } from 'node:vm';
import { createCatRuntime, createNodeNetwork, createScriptHost, SCRIPT_LIMITS } from '../src/index.js';
import { ScriptHost } from '../src/host.js';
import { CatSession } from '../src/cat-session.js';
import { syncCatManagement } from '../src/cat-management.js';
import { cleanHopByHop } from '../src/cat-runtime.js';
import { decodePanResource, normalizeCatOutput } from '../src/cat-output.js';
import { fault } from '../src/errors.js';
import { NodeCapabilities } from '../src/node-capabilities.js';
import { PrivateVolume } from '../src/private-volume.js';
import { ResourceScope } from '../src/scope.js';
import { ScriptServices } from '../src/script-services.js';
import { projectHttpRequestOptions, projectFields } from '../src/native-roles.js';
import { parse, tokenizer, parseExpressionAt } from 'acorn';
import { createLocalStartupLookup, startupWrapperTarget } from '../src/cat-source-routes.js';
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

function nativeCapabilities(session, services, root) {
  session.timers ??= new Map();
  session.resourceScope ??= () => session.root;
  session.runCallback ??= function(callback, receiver, args) {
    if (this.live) return Reflect.apply(callback, receiver, args);
  };
  return new NodeCapabilities(session, services, root);
}
function resourceCounts(session) {
  return { streams: session.node.streams.size, webResources: session.node.webResources.size,
    sockets: session.node.networkResources.size, descriptors: session.node.volume.descriptors.size,
    files: session.node.volume.streams.size, requests: session.nativeRequests.size,
    timers: session.timers.size, adapters: session.adapterTasks.size };
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
      HOST: 'source.virtual.invalid',
      'X-Source-Header': 'synthetic-only',
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
        'timerBurst', 'parallel', 'pipeLifecycle', 'slow', 'pan', 'panOpaque', 'panLists', 'guestMeow',
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
      hostCorrect: request.headers.host === 'source.virtual.invalid',
      explicit: request.headers['x-source-header'] || null,
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
        contentType: /^multipart\/form-data; boundary=[^\s;]+$/.test(multipart.contentType),
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
          loopback: Object.values(interfaces).flat()
            .find(item => item.family === 'IPv4' && item.internal)?.address,
          totalmem: os.totalmem(),
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
  if (action === 'panLists') {
    const host = input.provider === 'uc' ? 'pc-api.uc.cn' : 'drive.quark.cn';
    const endpoint = 'https://' + host + '/1/clouddrive/share/sharepage/';
    if (input.capture !== false) {
      await external(endpoint + 'token', { pwd_id: 'abcd1234', passcode: 'synthetic-share-password' });
    }
    if (input.secondShare) {
      await external(endpoint + 'token', { pwd_id: 'efgh5678', passcode: 'synthetic-second-password' });
    }
    const upstream = await external(endpoint + 'detail' + (input.query || ''), input.body || {});
    const value = JSON.parse(upstream.body);
    json(response, { upstream: value, upstreamShape: upstream.shape,
      ...(input.inspectMetadata ? { listTotal: value.metadata._total } : {}) });
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

/**
 * These tests exercise ownership of live native objects and guest execution
 * state. They own a real CatSession in this thread; the public CatRuntime
 * facade is reserved for public gateway and persistence tests.
 */
async function createCatSessionForInternalTest(t, {
  dataRoot = rootFor(t), installationId, source, config = {}, version = '1',
  settings, onNetwork, onActivate, onStop, network, onDiagnostic,
} = {}) {
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

test('Cat factory account namespaces support lazy actions and preserve explicit config per installation',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    const source = `
      const http = require('http');
      let server, config;
      module.exports.start = value => {
        config = value;
        return new Promise(resolve => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/config' || request.url === '/full-config') {
              response.end(JSON.stringify({ video: { sites: [{
                key: 'default', name: 'Factory config', api: '/api/default',
                actions: ['home', 'detail']
              }] } }));
              return;
            }
            const cookie = config.quark.cookie;
            response.end(JSON.stringify({
              cookie: cookie ?? null, baidu: config.baidu, server: config.server,
              namespaces: Object.keys(config).sort(), sites: config.sites, pans: config.pans,
              color: config.color, custom: config.custom ?? null
            }));
            if (request.url.endsWith('/detail')) config.quark.cookie = 'installation-local';
          });
          server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
        });
      };
      module.exports.stop = () => server?.close();
    `;
    const configured = {
      quark: { cookie: 'synthetic-explicit-cookie' },
      baidu: { cookie: 'synthetic-baidu-cookie' },
      server: { url: 'https://configured.invalid', authorization: 'synthetic-auth' },
      custom: { value: 7 },
    };
    for (const installationId of ['factory-empty', 'factory-configured', 'factory-independent']) {
      assert.equal((await runtime.install({ installationId, source,
        ...(installationId === 'factory-configured' ? { config: configured } : {}) })).state, 'ready');
    }
    const invoke = (installationId, operation = 'home') => runtime.invoke({
      installationId, siteId: 'default', operation, payload: {},
    });
    const empty = await invoke('factory-empty', 'detail');
    assert.equal(empty.cookie, null);
    assert.deepEqual(empty.baidu, { cookie: '' });
    assert.deepEqual(empty.server, { url: '', authorization: '' });
    assert.deepEqual(empty.sites, { list: [] });
    assert.deepEqual(empty.pans, { list: [] });
    assert.deepEqual(empty.color, []);
    assert.deepEqual(empty.namespaces, ['sites', 'pans', 'color', 'baidu', 'quark', 'uc',
      'y115', 'pan123ziyuan', 'bili', 'wuming', 'ali', 'tgsou', 'tgchannel', 'muou',
      'leijing', 'wogg', 'livetovod', 'server'].sort());
    assert.equal((await invoke('factory-empty')).cookie, 'installation-local');
    assert.equal((await invoke('factory-independent')).cookie, null);
    const explicit = await invoke('factory-configured');
    assert.equal(explicit.cookie, configured.quark.cookie);
    assert.deepEqual(explicit.baidu, configured.baidu);
    assert.deepEqual(explicit.server, configured.server);
    assert.deepEqual(explicit.custom, configured.custom);
    assert.deepEqual(configured.quark, { cookie: 'synthetic-explicit-cookie' });
  });

function gatedSource(name, url) {
  return delayedSource(name, 0) + `
    const start = module.exports.start;
    module.exports.start = async () => {
      await new Promise((resolve, reject) => {
        const request = http.get(${JSON.stringify(url)}, { agent: false }, response => {
          response.once('error', reject);
          response.once('end', resolve);
          response.resume();
        });
        request.once('error', reject);
      });
      return start();
    };
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
  const root = mkdtempSync(join(tmpdir(), 'meowfilm-cat-runtime-'));
  const volumeRoot = join(root, 'volumes', 'bridge');
  const runtime = createCatRuntime({
    dataRoot: root, network: networkForLocalServers([upstream]),
  });
  t.after(async () => {
    try { await runtime.close(); }
    finally { rmSync(root, { recursive: true, force: true }); }
  });
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
  return request(endpoint.origin + path);
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

















test('CatNode cannot access the private native hooks at startup or in dynamic code', async t => {
  const inspect = `({
    type: typeof __meowTaskContext,
    own: Object.hasOwn(globalThis, '__meowTaskContext'),
    dynamic: Function('return typeof __meowTaskContext')(),
    nested: require('vm').runInNewContext('typeof __meowTaskContext'),
  })`;
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({
    installationId: 'private-native-hooks', enabled: true, version: '1',
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
    installationId: 'private-native-hooks', siteId: 'default',
    operation: 'home', payload: {},
  });
  assert.deepEqual(result, { startup: expected, action: expected });
});

test('Cat os and process memory statistics retain native Node meaning', async t => {
  const session = await createCatSessionForInternalTest(t, { installationId: 'memory-statistics',
    source: `module.exports.home = () => ({ total: require('os').totalmem(), free: require('os').freemem(),
      platform: process.platform, arch: process.arch, usage: process.memoryUsage(), count: 1 });` });
  const value = await session.invoke({ siteId: 'default', operation: 'home', payload: {} });
  assert.equal(value.total, totalmem());
  assert.ok(value.free > 0 && value.free <= value.total);
  assert.equal(value.platform, process.platform); assert.equal(value.arch, process.arch);
  for (const key of ['rss', 'heapTotal', 'heapUsed', 'external', 'arrayBuffers'])
    assert.ok(Number.isFinite(value.usage[key]) && value.usage[key] >= 0, key);
  assert.ok(value.usage.rss > value.usage.heapUsed);
});

test('Cat native buffers retain state across calls and report actual external memory', async t => {
  const session = await createCatSessionForInternalTest(t, {
    installationId: 'retained-native-bytes', source: `
      let retained, count = 0;
      module.exports.home = () => {
        retained ??= Buffer.alloc(24 * 1024 * 1024, 0xa5);
        return { count: ++count, bytes: retained.length, first: retained[0],
          last: retained.at(-1), external: process.memoryUsage().external };
      };`,
  });
  for (let count = 1; count <= 3; count++) {
    const value = await session.invoke({ siteId: 'default', operation: 'home', payload: {} });
    assert.equal(value.count, count);
    assert.equal(value.bytes, 24 * 1024 * 1024);
    assert.equal(value.first, 0xa5); assert.equal(value.last, 0xa5);
    assert.ok(value.external >= value.bytes);
    assert.equal(session.generation, 1);
  }
  await session.stop();
  assert.equal(session.stats().live, false);
});

test('Cat independent install and status complete during a slow installation', { timeout: 30000 }, async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.start();
  let slowDone = false;
  const slow = runtime.install({ installationId: 'slow', enabled: true,
    source: delayedSource('slow', 1200) }).then(value => { slowDone = true; return value; });
  await waitFor(() => runtime.host.initializingSessions().some(session =>
    session.installationId === 'slow'), 'slow installation was not admitted');
  const fast = await runtime.install({ installationId: 'fast', enabled: true,
    source: delayedSource('fast', 0) });
  assert.equal(fast.state, 'ready'); assert.equal(slowDone, false);
  const states = (await runtime.status()).instances;
  assert.equal(states.find(value => value.installationId === 'slow').state, 'starting');
  assert.equal(states.find(value => value.installationId === 'fast').state, 'ready');
  assert.deepEqual(await runtime.invoke({ installationId: 'fast', siteId: 'site-fast',
    operation: 'home', payload: {} }), { instance: 'fast' });
  assert.equal((await slow).state, 'ready');
});

test('Cat serializes updates only within the same installation', { timeout: 30000 }, async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  await runtime.install({ installationId: 'ordered', source: volumeSource('ordered'), enabled: true });
  const disabled = runtime.setEnabled('ordered', false);
  const enabled = runtime.setEnabled('ordered', true);
  assert.equal((await disabled).state, 'disabled');
  assert.equal((await enabled).state, 'ready');
  assert.equal((await runtime.status()).instances[0].state, 'ready');
});

test('Cat imports reuse the complete uploaded source identity but enables read edited bytes',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.start();
    const source = 'module.exports.home = () => ({ value: "uploaded" });';
    const contentHash = sourceHash(source);
    const originalRead = runtime.readSource.bind(runtime);
    runtime.readSource = () => { throw new Error('the complete import must not be reread'); };
    const installed = await runtime.install({ installationId: 'uploaded', source, contentHash, enabled: true });
    assert.equal(installed.sourceHash, contentHash);
    assert.equal(runtime.host.get('uploaded').contentHash, contentHash);
    runtime.readSource = originalRead;
    await runtime.setEnabled('uploaded', false);
    const edited = 'module.exports.home = () => ({ value: "edited bytes with a different length" });';
    writeFileSync(sourceFile(runtime.dataRoot, contentHash), edited);
    const enabled = await runtime.setEnabled('uploaded', true);
    assert.equal(enabled.sourceHash, sourceHash(edited));
    assert.deepEqual(await runtime.invoke({ installationId: 'uploaded', siteId: 'default',
      operation: 'home', payload: {} }), { value: 'edited bytes with a different length' });
    assert.equal(runtime.settings.read().scripts[0].sourceHash, contentHash);
  });

test('Cat install cancellation restores accepted source after real exit and cannot cancel a later generation',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    const original = await runtime.install({ installationId: 'cancel-import', enabled: true,
      source: 'let count = 0; module.exports.home = () => ({ count: ++count });' });
    const invoke = () => runtime.invoke({ installationId: 'cancel-import', siteId: 'default',
      operation: 'home', payload: {} });
    assert.deepEqual(await invoke(), { count: 1 });
    const controller = new AbortController();
    const pending = runtime.install({ installationId: 'cancel-import', enabled: true,
      source: delayedSource('cancelled', 10000) }, { signal: controller.signal });
    const rejected = assert.rejects(pending, { code: 'CANCELLED' });
    await waitFor(() => runtime.host.initializingSessions().some(session =>
      session.installationId === 'cancel-import'), 'candidate was not registered');
    const candidate = runtime.host.initializingSessions().find(session =>
      session.installationId === 'cancel-import');
    controller.abort();
    await rejected;
    assert.equal(candidate.worker, null);
    assert.equal(runtime.host.get('cancel-import').generation, original.generation + 2);
    assert.equal(runtime.instance('cancel-import').sourceHash, original.sourceHash);
    assert.deepEqual(await invoke(), { count: 1 });
    const nextSignal = new AbortController();
    const next = await runtime.install({ installationId: 'cancel-import', enabled: true,
      source: 'module.exports.home = () => ({ value: "replacement" });' }, { signal: nextSignal.signal });
    nextSignal.abort();
    assert.equal(runtime.getSession('cancel-import').generation, next.generation);
    assert.deepEqual(await invoke(), { value: 'replacement' });
  });

test('Cat captured initialization stop cannot select a successor with the same installation ID',
  { timeout: 30000 }, async t => {
    const gates = new Map();
    const upstream = await serverFor(t, (request, response) => { gates.set(request.url, response); });
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    const empty = runtime.captureInitialization('captured');
    await runtime.install({ installationId: 'captured', enabled: true,
      source: delayedSource('original', 0) });
    const first = runtime.install({ installationId: 'captured', enabled: true,
      source: gatedSource('first', `${upstream.socketOrigin}/first`) });
    const rejected = assert.rejects(first);
    await waitFor(() => gates.has('/first'), 'first candidate did not reach its I/O barrier');
    const firstOwner = runtime.host.initializingSessions()[0];
    const stopFirst = runtime.captureInitialization('captured');
    empty();
    assert.equal(firstOwner.live, true);
    stopFirst();
    await rejected;
    assert.equal(firstOwner.worker, null);
    const second = runtime.install({ installationId: 'captured', enabled: true,
      source: gatedSource('second', `${upstream.socketOrigin}/second`) });
    second.catch(() => {});
    await waitFor(() => gates.has('/second'), 'successor did not reach its I/O barrier');
    const secondOwner = runtime.host.initializingSessions()[0];
    stopFirst();
    assert.equal(secondOwner.live, true);
    assert.ok(secondOwner.worker);
    assert.notEqual(secondOwner, firstOwner);
    gates.get('/second').end('continue');
    assert.equal((await second).generation, secondOwner.generation);
    stopFirst();
    assert.deepEqual(await runtime.invoke({ installationId: 'captured',
      siteId: 'site-second', operation: 'home', payload: {} }), { instance: 'second' });
  });

test('Cat enable cancellation reaches the initializing worker before returning',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'cancel-enable', source: delayedSource('enable', 10000), enabled: false });
    const controller = new AbortController();
    const pending = runtime.setEnabled('cancel-enable', true, { signal: controller.signal });
    const rejected = assert.rejects(pending, { code: 'CANCELLED' });
    await waitFor(() => runtime.host.initializingSessions().length === 1, 'enable did not start a worker');
    const candidate = runtime.host.initializingSessions()[0];
    controller.abort();
    await rejected;
    assert.equal(candidate.worker, null);
    assert.equal(runtime.host.initializingSessions().length, 0);
    const before = runtime.settings.read();
    await assert.rejects(runtime.setEnabled('cancel-enable', false, { signal: controller.signal }),
      { code: 'CANCELLED' });
    assert.deepEqual(runtime.settings.read(), before);
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





test('Cat start publishes aggregate readiness and starts instances independently',
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

test('Cat management proceeds while another instance starts',
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

test('Cat close stops concurrent startup and waits for every owner',
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
    await waitForState(reopened, 'third', 'ready');
    assert.equal((await reopened.status()).instances.find(instance =>
      instance.installationId === 'second').state, 'starting');
    await reopened.close();
    assert.equal(reopened.closed, true);
    assert.equal(reopened.pending.size, 0);
    assert.equal(reopened.transactions.size, 0);
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

test('Cat cold-start reports disabled instances without loading a host revision',
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
    assert.equal(reopened.hostState('disabled-cold'), null);
    await assert.rejects(reopened.invoke({
      installationId: 'disabled-cold', siteId: 'site-disabled-cold', operation: 'home', payload: {},
    }), { code: 'DISABLED' });
    await assert.rejects(reopened.management('disabled-cold'), { code: 'DISABLED' });
    await assert.rejects(reopened.sync('disabled-cold', undefined, {
      provider: 'quark', credentials: { cookie: 'fixture' },
    }), { code: 'DISABLED' });
  });

test('Cat executes length-changing source edits after re-enable and restart',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const original = 'module.exports = { home() { return { value: "original" }; } };';
    const edited = 'module.exports = { home() { return { value: "edited with a different length" }; } };';
    await runtime.install({ installationId: 'editable', source: original, enabled: true });
    const reference = settingsScript(runtime, 'editable').sourceHash;
    const file = sourceFile(root, reference);
    const target = { installationId: 'editable', siteId: 'default', operation: 'home', payload: {} };
    const initialGeneration = runtime.instance('editable').generation;
    writeFileSync(file, edited);
    assert.notEqual(Buffer.byteLength(edited), Buffer.byteLength(original));
    assert.deepEqual(await runtime.invoke(target), { value: 'original' });
    await runtime.setEnabled('editable', false);
    const enabled = await runtime.setEnabled('editable', true);
    assert.equal(enabled.state, 'ready');
    assert.ok(enabled.generation > initialGeneration);
    assert.equal(enabled.sourceHash, sourceHash(edited));
    assert.equal(settingsScript(runtime, 'editable').sourceHash, reference);
    assert.deepEqual(await runtime.invoke(target), { value: 'edited with a different length' });

    const restartedSource = 'module.exports = { home() { return { value: "restart" }; } }; // current';
    await runtime.close();
    writeFileSync(file, restartedSource);
    const reopened = createCatRuntime({ dataRoot: root });
    t.after(() => reopened.close());
    await reopened.start();
    await waitForState(reopened, 'editable', 'ready');
    assert.deepEqual(await reopened.invoke(target), { value: 'restart' });
    assert.equal(settingsScript(reopened, 'editable').sourceHash, reference);
    assert.equal(reopened.instance('editable').sourceHash, sourceHash(restartedSource));
  });

test('Cat source file failures affect only the enabled instance',
  { timeout: 60000 }, async t => {
    for (const [kind, badBytes, expectedCode] of [
      ['missing', null, 'CORRUPT_STORAGE'],
      ['encoding', Buffer.from([0xc3, 0x28]), 'SCRIPT_PROTOCOL_ERROR'],
      ['bom', Buffer.from('\uFEFFmodule.exports = {};'), 'SCRIPT_PROTOCOL_ERROR'],
      ['syntax', Buffer.from('module.exports = { home( };'), 'SCRIPT_EXCEPTION'],
      ['oversized', Buffer.alloc(SCRIPT_LIMITS.maxSourceBytes + 1, 0x20), 'RESOURCE_LIMIT'],
    ]) {
      await t.test(kind, async t => {
        const root = rootFor(t);
        const runtime = createCatRuntime({ dataRoot: root });
        t.after(() => runtime.close());
        const brokenSource = `module.exports = { home() { return "${kind}"; } };`;
        await runtime.install({ installationId: 'broken-file', source: brokenSource, enabled: false });
        await runtime.install({
          installationId: 'healthy-file', source: delayedSource('healthy-file', 0), enabled: true,
        });
        writeSettings(runtime.settings.path, value => ({
          ...value, scripts: value.scripts.map(script => ({ ...script, enabled: true })),
        }));
        await runtime.close();
        const file = sourceFile(root, sourceHash(brokenSource));
        if (badBytes === null) rmSync(file);
        else writeFileSync(file, badBytes);
        const reopened = createCatRuntime({ dataRoot: root });
        t.after(() => reopened.close());
        const started = await reopened.start();
        assert.ok(reopened.server.listening);
        assert.match(started.aggregate.origin, /^http:\/\/127\.0\.0\.1:\d+$/);
        await waitForState(reopened, 'broken-file', 'failed');
        await waitForState(reopened, 'healthy-file', 'ready');
        assert.equal(reopened.instance('broken-file').error, expectedCode);
        assert.deepEqual(await reopened.invoke({
          installationId: 'healthy-file', siteId: 'site-healthy-file', operation: 'home', payload: {},
        }), { instance: 'healthy-file' });
        assert.deepEqual(reopened.applyResult(), {
          applied: false, failures: [{ installationId: 'broken-file', code: expectedCode }],
        });
      });
    }
  });

test('Cat refresh rejects invalid and pre-aborted signals before startup',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    for (const signal of [null, true, {}, { aborted: false }])
      await assert.rejects(runtime.refresh({ signal }), { code: 'INVALID_ARGUMENT' });
    const controller = new AbortController();
    controller.abort();
    await assert.rejects(runtime.refresh({ signal: controller.signal }), { code: 'CANCELLED' });
    assert.equal(runtime.initialization, null);
    assert.equal(existsSync(join(root, 'settings.json')), false);
    assert.equal(existsSync(join(root, 'sources')), false);
  });

test('Cat refresh cancellation during shared start does not stop background initialization',
  { timeout: 30000 }, async t => {
    let gate;
    const upstream = await serverFor(t, (_request, response) => { gate = response; });
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'background-refresh',
      source: gatedSource('background', upstream.socketOrigin), enabled: false });
    writeSettings(runtime.settings.path, value => ({
      ...value, scripts: value.scripts.map(script => ({ ...script, enabled: true })),
    }));
    await runtime.close();

    const reopened = createCatRuntime({ dataRoot: root });
    t.after(() => reopened.close());
    const controller = new AbortController();
    const started = reopened.start();
    const cancelled = assert.rejects(reopened.refresh({ signal: controller.signal }),
      { code: 'CANCELLED' });
    controller.abort();
    await cancelled;
    await started;
    await waitFor(() => gate, 'background initialization did not reach its I/O barrier');
    const owner = reopened.host.initializingSessions().find(session =>
      session.installationId === 'background-refresh');
    assert.ok(owner?.worker);
    const worker = owner.worker;
    assert.equal(owner.live, true);
    assert.equal(gate.destroyed, false);
    gate.end('continue');
    await waitForState(reopened, 'background-refresh', 'ready');
    assert.equal(reopened.getSession('background-refresh'), owner);
    assert.equal(owner.worker, worker);
    assert.notEqual(worker.threadId, -1);
    assert.deepEqual(await reopened.invoke({ installationId: 'background-refresh',
      siteId: 'site-background', operation: 'home', payload: {} }), { instance: 'background' });
  });

test('Cat refresh cancelled in an installation queue cannot apply after the preceding work',
  { timeout: 30000 }, async t => {
    let release;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => { release?.(); return runtime.close(); });
    const original = await runtime.install({ installationId: 'queued-refresh',
      source: delayedSource('queued', 0), version: '1', enabled: true });
    const owner = runtime.getSession('queued-refresh');
    const barrier = new Promise(resolve => { release = resolve; });
    const preceding = runtime.serial('queued-refresh', () => barrier);
    writeFileSync(sourceFile(runtime.dataRoot, original.sourceHash),
      delayedSource('must-not-publish', 0));
    writeSettings(runtime.settings.path, value => ({
      ...value, scripts: value.scripts.map(script => ({ ...script, version: '2' })),
    }));
    const controller = new AbortController();
    const cancelled = assert.rejects(runtime.refresh({ signal: controller.signal }),
      { code: 'CANCELLED' });
    await waitFor(() => runtime.transactions.get('queued-refresh') !== preceding,
      'refresh did not enter the installation queue');
    controller.abort();
    assert.equal(runtime.getSession('queued-refresh'), owner);
    assert.deepEqual(await runtime.invoke({ installationId: 'queued-refresh',
      siteId: 'site-queued', operation: 'home', payload: {} }), { instance: 'queued' });
    release();
    await preceding;
    await cancelled;
    const retained = runtime.instance('queued-refresh');
    assert.equal(retained.state, 'ready');
    assert.equal(retained.generation, original.generation);
    assert.equal(retained.sourceHash, original.sourceHash);
    assert.equal(retained.version, original.version);
    assert.equal(runtime.getSession('queued-refresh'), owner);
    assert.equal(runtime.host.initializingSessions().length, 0);
    assert.deepEqual(await runtime.refresh(), { applied: true, failures: [] });
    const retried = runtime.instance('queued-refresh');
    assert.equal(retried.state, 'ready');
    assert.equal(retried.version, '2');
    assert.ok(retried.generation > original.generation);
    assert.notEqual(runtime.getSession('queued-refresh'), owner);
    assert.equal(owner.worker, null);
    assert.deepEqual(await runtime.invoke({ installationId: 'queued-refresh',
      siteId: 'site-must-not-publish', operation: 'home', payload: {} }), { instance: 'must-not-publish' });
  });

test('Cat metadata commits after a cancelled refresh do not acknowledge unapplied execution changes',
  { timeout: 60000 }, async t => {
    for (const operation of ['edit', 'unchanged update', 'failed update']) {
      await t.test(operation, async t => {
        let release;
        const runtime = createCatRuntime({ dataRoot: rootFor(t) });
        t.after(() => { release?.(); return runtime.close(); });
        const sourceText = `
          const http = require('http');
          let server;
          module.exports.start = config => new Promise(resolve => {
            server = http.createServer((request, response) => {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify(request.url === '/config' || request.url === '/full-config'
                ? { video: { sites: [{ key: 'default', name: 'Config', api: '/api/default', actions: ['home'] }] } }
                : { value: config.value }));
            });
            server.listen(Number(process.env.PORT), '127.0.0.1', resolve);
          });
          module.exports.stop = () => server?.close();
        `;
        const original = await runtime.install({ installationId: 'metadata-refresh', source: sourceText,
          version: '1', config: { value: 'old' } });
        const owner = runtime.getSession('metadata-refresh');
        const barrier = new Promise(resolve => { release = resolve; });
        const preceding = runtime.serial('metadata-refresh', () => barrier);
        writeSettings(runtime.settings.path, value => ({
          ...value, scripts: value.scripts.map(script => ({
            ...script, version: '2', config: { value: 'new' },
          })),
        }));
        const controller = new AbortController();
        const cancelled = assert.rejects(runtime.refresh({ signal: controller.signal }),
          { code: 'CANCELLED' });
        await waitFor(() => runtime.transactions.get('metadata-refresh') !== preceding,
          'refresh did not enter the installation queue');
        controller.abort();
        release();
        await preceding;
        await cancelled;
        if (operation === 'edit') {
          await runtime.edit({ installationId: 'metadata-refresh', name: 'edited' });
        } else if (operation === 'unchanged update') {
          assert.equal((await runtime.update({ installationId: 'metadata-refresh', source: sourceText }))
            .updateState, 'unchanged');
        } else {
          await assert.rejects(runtime.update({ installationId: 'metadata-refresh',
            source: 'module.exports = { home( };' }), { code: 'SCRIPT_EXCEPTION' });
          assert.equal(runtime.instance('metadata-refresh').updateState, 'failed');
        }
        assert.equal(runtime.getSession('metadata-refresh'), owner);
        assert.equal(runtime.instance('metadata-refresh').version, original.version);
        assert.deepEqual(await runtime.invoke({ installationId: 'metadata-refresh',
          siteId: 'default', operation: 'home', payload: {} }), { value: 'old' });
        assert.deepEqual(await runtime.refresh(), { applied: true, failures: [] });
        const refreshed = runtime.instance('metadata-refresh');
        assert.equal(refreshed.state, 'ready');
        assert.equal(refreshed.version, '2');
        assert.ok(refreshed.generation > original.generation);
        assert.notEqual(runtime.getSession('metadata-refresh'), owner);
        assert.equal(owner.worker, null);
        assert.deepEqual(await runtime.invoke({ installationId: 'metadata-refresh',
          siteId: 'default', operation: 'home', payload: {} }), { value: 'new' });
      });
    }
  });

test('Cat refresh cancellation exits A and reports failed while preserving B and later updates',
  { timeout: 30000 }, async t => {
    const gates = new Map();
    const upstream = await serverFor(t, (request, response) => {
      gates.set(request.url, { response, socket: request.socket });
    });
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const originals = new Map();
    const owners = new Map();
    for (const id of ['a', 'b']) {
      originals.set(id, await runtime.install({ installationId: id,
        source: delayedSource(id, 0), version: '1', enabled: true }));
      owners.set(id, runtime.getSession(id));
      assert.deepEqual(await runtime.invoke({ installationId: id,
        siteId: `site-${id}`, operation: 'home', payload: {} }), { instance: id });
    }
    writeFileSync(sourceFile(root, originals.get('a').sourceHash),
      gatedSource('cancelled-a', `${upstream.socketOrigin}/refresh`));
    writeSettings(runtime.settings.path, value => ({
      ...value, scripts: value.scripts.map(script =>
        script.installationId === 'a' ? { ...script, version: '2' } : script),
    }));
    const controller = new AbortController();
    const cancelled = assert.rejects(runtime.refresh({ signal: controller.signal }),
      { code: 'CANCELLED' });
    await waitFor(() => gates.has('/refresh'), 'refresh candidate did not reach its I/O barrier');
    const candidate = runtime.host.initializingSessions().find(session => session.installationId === 'a');
    assert.ok(candidate?.worker);
    const worker = candidate.worker;
    assert.notEqual(candidate, owners.get('a'));
    let exited = false;
    worker.once('exit', () => { exited = true; });
    const starting = await runtime.status();
    assert.equal(starting.instances.find(instance => instance.installationId === 'a').state, 'starting');
    assert.equal(owners.get('a').worker, null);
    await assert.rejects(runtime.invoke({ installationId: 'a', siteId: 'site-a',
      operation: 'home', payload: {} }), { code: 'STARTING' });
    const runningB = starting.instances.find(instance => instance.installationId === 'b');
    assert.equal(runningB.state, 'ready');
    assert.equal(runningB.generation, originals.get('b').generation);
    assert.deepEqual(await runtime.invoke({ installationId: 'b', siteId: 'site-b',
      operation: 'home', payload: {} }), { instance: 'b' });
    controller.abort();
    await cancelled;
    assert.equal(exited, true);
    assert.equal(worker.threadId, -1);
    assert.equal(candidate.worker, null);
    assert.equal(runtime.host.initializingSessions().length, 0);
    await waitFor(() => gates.get('/refresh').socket.destroyed,
      'cancelled candidate retained its initialization connection');
    assert.equal(gates.get('/refresh').response.writableEnded, false);
    for (const id of ['b']) {
      const retained = runtime.instance(id), original = originals.get(id);
      assert.equal(retained.state, 'ready');
      assert.equal(retained.generation, original.generation);
      assert.equal(retained.version, original.version);
      assert.equal(retained.sourceHash, original.sourceHash);
      assert.equal(retained.managementUrl, original.managementUrl);
      assert.deepEqual(retained.endpoints, original.endpoints);
      assert.equal(runtime.getSession(id), owners.get(id));
      assert.deepEqual(await runtime.invoke({ installationId: id,
        generation: original.generation, siteId: `site-${id}`,
        operation: 'home', payload: {} }), { instance: id });
    }
    assert.equal(runtime.instance('a').state, 'failed');
    assert.equal(runtime.instance('a').generation, candidate.generation);
    assert.equal(runtime.instance('a').managementUrl, undefined);
    assert.equal(runtime.instance('a').endpoints, undefined);
    assert.throws(() => runtime.getSession('a'), { code: 'DISABLED' });
    await assert.rejects(runtime.invoke({ installationId: 'a', siteId: 'site-a',
      operation: 'home', payload: {} }), { code: 'DISABLED' });
    assert.equal(runtime.instance('a').error, 'CANCELLED');
    assert.deepEqual(runtime.applyResult(), {
      applied: false, failures: [{ installationId: 'a', code: 'CANCELLED' }],
    });

    const next = runtime.install({ installationId: 'a', enabled: true, version: '3',
      source: gatedSource('replacement-a', `${upstream.socketOrigin}/replacement`) });
    next.catch(() => {});
    await waitFor(() => gates.has('/replacement'), 'later update did not reach its I/O barrier');
    assert.equal(runtime.instance('b').generation, originals.get('b').generation);
    gates.get('/replacement').response.end('continue');
    const replacement = await next;
    assert.equal(replacement.state, 'ready');
    assert.ok(replacement.generation > candidate.generation);
    assert.equal(runtime.getSession('a').generation, replacement.generation);
    assert.deepEqual(await runtime.invoke({ installationId: 'a',
      generation: replacement.generation, siteId: 'site-replacement-a',
      operation: 'home', payload: {} }), { instance: 'replacement-a' });
    assert.deepEqual(await runtime.invoke({ installationId: 'b',
      generation: originals.get('b').generation, siteId: 'site-b',
      operation: 'home', payload: {} }), { instance: 'b' });
    assert.equal(worker.threadId, -1);
    assert.deepEqual(runtime.applyResult(), { applied: true, failures: [] });
  });

test('Cat leaves disabled bad files unread and refresh loads only changed enabled targets',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    for (const id of ['disabled', 'missing-disabled', 'unchanged', 'changed']) {
      await runtime.install({
        installationId: id, source: delayedSource(id, 0),
        enabled: id === 'unchanged' || id === 'changed',
      });
    }
    await runtime.close();
    writeFileSync(sourceFile(root, sourceHash(delayedSource('disabled', 0))), Buffer.from([0xff]));
    rmSync(sourceFile(root, sourceHash(delayedSource('missing-disabled', 0))));
    const reopened = createCatRuntime({ dataRoot: root });
    t.after(() => reopened.close());
    const reads = [];
    const files = Object.fromEntries(['disabled', 'missing-disabled', 'unchanged', 'changed']
      .map(id => [id, sourceFile(root, sourceHash(delayedSource(id, 0)))]));
    const originalRead = fs.readFileSync;
    fs.readFileSync = (file, ...options) => {
      if (Object.values(files).includes(file)) reads.push(file);
      return originalRead(file, ...options);
    };
    syncBuiltinESMExports();
    try {
      await reopened.start();
      await waitForState(reopened, 'unchanged', 'ready');
      await waitForState(reopened, 'changed', 'ready');
      assert.deepEqual(reads, [files.unchanged, files.changed]);
      assert.equal(reopened.instance('disabled').state, 'disabled');
      assert.equal(reopened.instance('missing-disabled').state, 'disabled');
      const unchangedGeneration = reopened.instance('unchanged').generation;
      const changedGeneration = reopened.instance('changed').generation;
      reads.length = 0;
      rmSync(files.unchanged);
      writeSettings(reopened.settings.path, value => ({
        ...value, providers: { quark: true },
      }));
      assert.deepEqual(await reopened.refresh(), { applied: true, failures: [] });
      assert.deepEqual(reads, []);
      writeSettings(reopened.settings.path, value => ({
        ...value, scripts: value.scripts.map(script =>
          ['changed', 'disabled'].includes(script.installationId) ? { ...script, version: '2' } : script),
      }));
      assert.deepEqual(await reopened.refresh(), { applied: true, failures: [] });
      assert.deepEqual(reads, [files.changed]);
      assert.equal(reopened.instance('unchanged').generation, unchangedGeneration);
      assert.ok(reopened.instance('changed').generation > changedGeneration);
      assert.equal(reopened.instance('disabled').version, '2');
      assert.deepEqual(await reopened.invoke({
        installationId: 'unchanged', siteId: 'site-unchanged', operation: 'home', payload: {},
      }), { instance: 'unchanged' });
      await assert.rejects(reopened.setEnabled('disabled', true), { code: 'SCRIPT_PROTOCOL_ERROR' });
      assert.equal(reopened.instance('disabled').state, 'failed');
      assert.equal((await reopened.setEnabled('disabled', false)).state, 'disabled');
      assert.equal(reopened.instance('changed').state, 'ready');
      const published = reopened.instance('changed');
      const oldOwner = reopened.getSession('changed');
      writeFileSync(files.changed, 'module.exports = { home( };');
      writeSettings(reopened.settings.path, value => ({
        ...value, scripts: value.scripts.map(script =>
          script.installationId === 'changed' ? { ...script, version: '3' } : script),
      }));
      assert.deepEqual(await reopened.refresh(), {
        applied: false, failures: [{ installationId: 'changed', code: 'SCRIPT_EXCEPTION' }],
      });
      const retained = reopened.instance('changed');
      assert.equal(retained.state, 'failed');
      assert.equal(retained.error, 'SCRIPT_EXCEPTION');
      assert.ok(retained.generation > published.generation);
      assert.equal(oldOwner.worker, null);
      assert.equal(retained.version, '3');
      assert.equal(retained.sourceHash, sourceHash('module.exports = { home( };'));
      assert.equal(retained.managementUrl, undefined);
      assert.equal(retained.endpoints, undefined);
      await assert.rejects(reopened.invoke({
        installationId: 'changed', generation: published.generation,
        siteId: 'site-changed', operation: 'home', payload: {},
      }), { code: 'DISABLED' });
      assert.equal(reopened.instance('unchanged').state, 'ready');
      assert.equal(reopened.instance('unchanged').generation, unchangedGeneration);
      writeFileSync(files.changed, delayedSource('changed', 0) + '\n// repaired file');
      assert.deepEqual(await reopened.refresh(), { applied: true, failures: [] });
      assert.equal(reopened.instance('changed').state, 'ready');
    } finally {
      fs.readFileSync = originalRead;
      syncBuiltinESMExports();
    }
  });

test('Cat explicit reimport atomically replaces edited bytes even with unchanged import metadata',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const original = 'module.exports = { home() { return { value: "imported" }; } };';
    const edited = 'module.exports = { home() { return { value: "manual edit" }; } }; // longer';
    const spec = { installationId: 'reimport', source: original, enabled: true };
    await runtime.install(spec);
    const file = sourceFile(root, sourceHash(original));
    writeFileSync(file, edited);
    await runtime.setEnabled('reimport', false);
    await runtime.setEnabled('reimport', true);
    const target = { installationId: 'reimport', siteId: 'default', operation: 'home', payload: {} };
    assert.deepEqual(await runtime.invoke(target), { value: 'manual edit' });
    const generation = runtime.instance('reimport').generation;
    assert.equal((await runtime.install(spec)).state, 'ready');
    assert.ok(runtime.instance('reimport').generation > generation);
    assert.equal(readFileSync(file, 'utf8'), original);
    assert.deepEqual(await runtime.invoke(target), { value: 'imported' });
  });

test('Cat failed atomic reimport preserves the edited file and restores the working revision',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const original = 'module.exports = { home() { return { value: "original" }; } };';
    const edited = 'module.exports = { home() { return { value: "manually edited" }; } };';
    const spec = { installationId: 'atomic-reimport', source: original, enabled: true };
    await runtime.install(spec);
    const file = sourceFile(root, sourceHash(original));
    writeFileSync(file, edited);
    const generation = runtime.instance('atomic-reimport').generation;
    const previousWorker = runtime.getSession('atomic-reimport');
    const settings = readFileSync(runtime.settings.path, 'utf8');
    const rename = fs.renameSync;
    fs.renameSync = (from, to) => {
      if (resolve(to) === file) throw Object.assign(new Error('synthetic rename failure'), { code: 'EBUSY' });
      return rename(from, to);
    };
    syncBuiltinESMExports();
    try {
      await assert.rejects(runtime.install(spec), { code: 'EBUSY' });
    } finally {
      fs.renameSync = rename;
      syncBuiltinESMExports();
    }
    assert.equal(readFileSync(file, 'utf8'), edited);
    assert.equal(readFileSync(runtime.settings.path, 'utf8'), settings);
    assert.equal(runtime.instance('atomic-reimport').generation, generation + 2);
    assert.equal(previousWorker.worker, null);
    assert.equal(runtime.instance('atomic-reimport').sourceHash, sourceHash(original));
    assert.deepEqual(await runtime.invoke({
      installationId: 'atomic-reimport', siteId: 'default', operation: 'home', payload: {},
    }), { value: 'original' });
  });

test('Cat removal and updates clean the import reference rather than the executed content hash',
  { timeout: 60000 }, async t => {
    for (const operation of ['uninstall', 'refresh-remove', 'install-update', 'refresh-update']) {
      await t.test(operation, async t => {
        const root = rootFor(t);
        const runtime = createCatRuntime({ dataRoot: root });
        t.after(() => runtime.close());
        const original = 'module.exports = { home() { return { value: "original" }; } };';
        const edited = 'module.exports = { home() { return { value: "edited source with different bytes" }; } };';
        const replacement = 'module.exports = { home() { return { value: "replacement" }; } };';
        await runtime.install({ installationId: 'cleanup', source: original, enabled: true });
        const importedFile = sourceFile(root, sourceHash(original));
        const unrelatedFile = sourceFile(root, sourceHash(edited));
        writeFileSync(importedFile, edited);
        writeFileSync(unrelatedFile, 'unrelated unreferenced file');
        await runtime.setEnabled('cleanup', false);
        await runtime.setEnabled('cleanup', true);
        assert.equal(runtime.instance('cleanup').sourceHash, sourceHash(edited));
        if (operation === 'uninstall') {
          assert.deepEqual(await runtime.uninstall('cleanup'), { applied: true, failures: [] });
        } else if (operation === 'install-update') {
          assert.equal((await runtime.install({
            installationId: 'cleanup', source: replacement, enabled: true,
          })).state, 'ready');
        } else {
          if (operation === 'refresh-update')
            writeFileSync(sourceFile(root, sourceHash(replacement)), replacement);
          writeSettings(runtime.settings.path, value => ({
            ...value, scripts: operation === 'refresh-remove' ? [] : value.scripts.map(script =>
              ({ ...script, sourceHash: sourceHash(replacement) })),
          }));
          assert.deepEqual(await runtime.refresh(), { applied: true, failures: [] });
        }
        assert.equal(existsSync(importedFile), false);
        assert.equal(readFileSync(unrelatedFile, 'utf8'), 'unrelated unreferenced file');
        if (operation.endsWith('update')) {
          assert.equal(existsSync(sourceFile(root, sourceHash(replacement))), true);
          assert.deepEqual(await runtime.invoke({
            installationId: 'cleanup', siteId: 'default', operation: 'home', payload: {},
          }), { value: 'replacement' });
        }
      });
    }
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

test('Cat native fetch replays 307 and 308 bodies and preserves explicit cross-origin headers',
  { timeout: 30000 }, async t => {
    const received = [];
    const final = await serverFor(t, async (request, response) => {
      const parts = [];
      for await (const part of request) parts.push(part);
      const bytes = Buffer.concat(parts);
      received.push({ method: request.method, length: bytes.length, hash: sha256(bytes),
        authorization: request.headers.authorization, cookie: request.headers.cookie,
        host: request.headers.host, origin: request.headers.origin });
      response.setHeader('content-type', 'application/json');
      response.end(JSON.stringify({ ok: true }));
    });
    const redirect = await serverFor(t, async (request, response) => {
      for await (const _part of request) { /* Consume the first streamed upload. */ }
      response.writeHead(Number(request.url.slice(1)), { location: final.socketOrigin + '/receive' });
      response.end();
    });
    const session = await createCatSessionForInternalTest(t, { installationId: 'fetch-redirect', source: `
      module.exports.home = async () => {
        const bytes = Buffer.alloc(1024 * 1024, 0x8d), result = [];
        for (const status of [307, 308]) {
          const reply = await fetch(${JSON.stringify(redirect.socketOrigin)} + '/' + status, {
            method: 'POST', headers: { authorization: 'synthetic-only', cookie: 'synthetic-only',
              host: 'source.virtual.invalid', origin: 'https://source.invalid' },
            body: bytes,
          });
          result.push({ redirected: reply.redirected, url: reply.url, value: await reply.json() });
        }
        return result;
      };` });
    assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
      [307, 308].map(() => ({ redirected: true, url: final.socketOrigin + '/receive', value: { ok: true } })));
    assert.deepEqual(received, [307, 308].map(() => ({ method: 'POST', length: 1024 * 1024,
      hash: sha256(Buffer.alloc(1024 * 1024, 0x8d)), authorization: 'synthetic-only', cookie: 'synthetic-only',
      host: 'source.virtual.invalid', origin: 'https://source.invalid' })));
    await session.stop();
    assert.equal(session.node.webResources.size, 0);
  });

test('Cat native Blob, Request and Response bodies release retained Web locks on stop', async t => {
  const session = await createCatSessionForInternalTest(t, { installationId: 'web-bodies', source: `
    const blob = new (require('buffer').Blob)(['blob']);
    const file = new File(['file'], 'fixture.txt');
    const bodies = [blob.stream(), file.stream(), new Request('https://fixture.invalid/', {
      method: 'POST', body: 'request',
    }).body, new Response('response').body];
    const readers = bodies.map(body => body.getReader());
    module.exports.home = () => ({ locked: bodies.map(body => body.locked),
      native: bodies.every(body => body instanceof ReadableStream), blob: blob instanceof Blob });
  ` });
  assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
    { locked: [true, true, true, true], native: true, blob: true });
  const bodies = [...session.node.webResources].filter(([, role]) => role === 'readableWeb')
    .map(([body]) => body);
  assert.equal(bodies.length, 4);
  await session.stop();
  assert.ok(bodies.every(body => !body.locked));
  assert.equal(session.node.webResources.size, 0);
});

test('Cat native background pipeTo survives request completion and settles before stop writes',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const session = await createCatSessionForInternalTest(t, { dataRoot: root,
      installationId: 'background-pipe', source: `
        const fs = require('fs');
        let source, destination, pipe, completion;
        const state = { cancelled: 0, aborted: 0, settled: 0 };
        module.exports.home = () => {
          if (!pipe) {
            source = new ReadableStream({ cancel() { state.cancelled++; } });
            destination = new WritableStream({ abort() { state.aborted++; } });
            pipe = source.pipeTo(destination);
            completion = pipe.then(() => state.settled++, () => state.settled++);
          }
          return { ...state, locked: [source.locked, destination.locked] };
        };
        module.exports.stop = async () => {
          await completion;
          fs.writeFileSync('/pipe-stop.json', JSON.stringify({
            ...state, locked: [source.locked, destination.locked],
          }));
        };
      ` });
    const invoke = () => session.invoke({ siteId: 'default', operation: 'home', payload: {} });
    const active = { cancelled: 0, aborted: 0, settled: 0, locked: [true, true] };
    assert.deepEqual(await invoke(), active);
    assert.deepEqual(await invoke(), active);
    const pipes = [...session.node.webPipes.keys()];
    const streams = [...session.node.webResources].map(([stream]) => stream);
    assert.equal(pipes.length, 1);
    const settled = assert.rejects(pipes[0], { code: 'CANCELLED' });
    await session.stop();
    await settled;
    assert.deepEqual(JSON.parse(readFileSync(join(root, 'volumes', 'background-pipe',
      'pipe-stop.json'), 'utf8')), { cancelled: 1, aborted: 1, settled: 1, locked: [false, false] });
    assert.ok(streams.every(stream => !stream.locked));
    assert.equal(session.node.webPipes.size, 0);
    assert.equal(session.node.webResources.size, 0);
  });

test('Cat native pipes preserve caller signals and own pipeThrough with prevented propagation',
  async t => {
    const session = new CatSession({ dataRoot: rootFor(t), installationId: 'pipe-signals', generation: 1,
      revision: { source: '', config: {}, version: '1' }, limits: { cleanupTimeoutMs: 100 } });
    t.after(() => session.stop());
    const { ReadableStream, WritableStream } = session.node.builtins['stream/web'];
    const controller = new AbortController(), reason = new Error('caller cancellation');
    let cancelled, aborted;
    const source = new ReadableStream({ cancel(value) { cancelled = value; } });
    const destination = new WritableStream({ abort(value) { aborted = value; } });
    const pipe = source.pipeTo(destination, { signal: controller.signal });
    const rejected = assert.rejects(pipe, error => error === reason);
    controller.abort(reason);
    await rejected;
    assert.equal(cancelled, reason); assert.equal(aborted, reason);
    assert.equal(source.locked, false); assert.equal(destination.locked, false);
    assert.equal(session.live, true);

    const calls = { input: 0, output: 0, writable: 0 };
    const input = new ReadableStream({ cancel() { calls.input++; } });
    const pair = {
      readable: new ReadableStream({ cancel() { calls.output++; } }),
      writable: new WritableStream({ abort() { calls.writable++; } }),
    };
    await assert.rejects(input.pipeTo({}), TypeError);
    assert.throws(() => input.pipeThrough({}), TypeError);
    assert.equal(input.locked, false);
    assert.equal(input.pipeThrough(pair, {
      preventAbort: true, preventCancel: true, preventClose: true,
    }), pair.readable);
    assert.equal(input.locked, true); assert.equal(pair.writable.locked, true);
    await session.stop();
    assert.deepEqual(calls, { input: 1, output: 1, writable: 1 });
    assert.equal(input.locked, false); assert.equal(pair.writable.locked, false);
    assert.equal(pair.readable.locked, false);
    assert.equal(session.node.webPipes.size, 0);
    assert.equal(session.node.webResources.size, 0);
  });

test('Cat native pipe cleanup reports an unsettled cancel instead of clearing its ownership',
  async t => {
    const session = new CatSession({ dataRoot: rootFor(t), installationId: 'pipe-pending', generation: 1,
      revision: { source: '', config: {}, version: '1' }, limits: { cleanupTimeoutMs: 20 } });
    let release;
    const barrier = new Promise(resolve => { release = resolve; });
    t.after(async () => { release(); await session.stop().catch(() => {}); });
    const { ReadableStream, WritableStream } = session.node.builtins['stream/web'];
    let cancelled = 0, aborted = 0, settled = false;
    const source = new ReadableStream({ cancel() { cancelled++; return barrier; } });
    const destination = new WritableStream({ abort() { aborted++; } });
    const pipe = source.pipeTo(destination);
    pipe.then(() => { settled = true; }, () => { settled = true; });
    const rejected = assert.rejects(pipe, { code: 'CANCELLED' });
    await assert.rejects(session.node.close(), { code: 'TIMEOUT' });
    assert.equal(cancelled, 1); assert.equal(aborted, 1); assert.equal(settled, false);
    assert.equal(source.locked, true); assert.equal(destination.locked, true);
    assert.equal(session.node.webPipes.size, 1);
    assert.ok(session.node.webResources.size > 0);
    release();
    await rejected;
    assert.equal(source.locked, false); assert.equal(destination.locked, false);
    await waitFor(() => session.node.webResources.size === 0, 'settled pipe resources did not retire');
    assert.equal(session.node.webPipes.size, 0);
  });

test('Cat native tee branches terminate cooperatively without an internal-reader cleanup error',
  async t => {
    const session = await createCatSessionForInternalTest(t, { installationId: 'tee-cooperative',
      source: 'module.exports.home = () => ({ alive: true });' });
    const { ReadableStream: ManagedReadableStream, WritableStream: ManagedWritableStream } =
      session.node.builtins['stream/web'];
    const pipes = [];
    const cases = ['unlocked', 'reader', 'iterator', 'pipe', 'nested'].map(kind => {
      const state = { kind, cancelled: 0 };
      state.source = new ManagedReadableStream({ cancel() { state.cancelled++; } });
      state.branches = state.source.tee();
      state.leaves = state.branches;
      if (kind === 'reader') state.reader = state.branches[0].getReader();
      if (kind === 'iterator') state.iterator = state.branches[0].values();
      if (kind === 'pipe') {
        state.aborted = 0;
        state.destination = new ManagedWritableStream({ abort() { state.aborted++; } });
        pipes.push(assert.rejects(state.branches[0].pipeTo(state.destination), { code: 'CANCELLED' }));
      }
      if (kind === 'nested') state.leaves = [...state.branches[0].tee(), state.branches[1]];
      return state;
    });
    for (let index = 0; index < 2; index++) {
      assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
        { alive: true });
      assert.ok(cases.every(state => state.cancelled === 0), 'request completion cancelled a tee');
    }
    await session.stop();
    await Promise.all(pipes);
    assert.equal(session.cleanupError, undefined);
    for (const state of cases) {
      assert.equal(state.cancelled, 1, state.kind);
      if (state.kind === 'pipe') {
        assert.equal(state.aborted, 1);
        assert.equal(state.destination.locked, false);
      }
      for (const branch of state.leaves) {
        const reader = ReadableStream.prototype.getReader.call(branch);
        try { assert.deepEqual(await reader.read(), { value: undefined, done: true }, state.kind); }
        finally { reader.releaseLock(); }
      }
    }
    assert.equal(session.node.webPipes.size, 0);
    assert.equal(session.node.webResources.size, 0);
  });

test('Cat native tee cleanup retains a pending cancellation until its real gate settles',
  async t => {
    const session = new CatSession({ dataRoot: rootFor(t), installationId: 'tee-pending', generation: 1,
      revision: { source: '', config: {}, version: '1' }, limits: { cleanupTimeoutMs: 20 } });
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    t.after(async () => { release(); await session.stop().catch(() => {}); });
    const { ReadableStream: ManagedReadableStream } = session.node.builtins['stream/web'];
    let cancelled = 0, settled = false;
    const source = new ManagedReadableStream({ cancel() { cancelled++; return gate; } });
    const branches = source.tee();
    const first = branches[0].cancel('first caller cancellation');
    assert.equal(cancelled, 0);
    const cancellation = Promise.all([first, branches[1].cancel('second caller cancellation')]);
    cancellation.then(() => { settled = true; });
    await assert.rejects(session.stop(), { code: 'TIMEOUT' });
    assert.equal(session.cleanupError, 'TIMEOUT');
    assert.equal(cancelled, 1);
    assert.equal(settled, false);
    assert.equal(session.node.webResources.has(source), true);
    release();
    await cancellation;
    assert.equal(settled, true);
    await waitFor(() => session.node.webResources.size === 0, 'settled tee resources did not retire');
    assert.equal(cancelled, 1);
    for (const branch of branches) {
      const reader = ReadableStream.prototype.getReader.call(branch);
      try { assert.deepEqual(await reader.read(), { value: undefined, done: true }); }
      finally { reader.releaseLock(); }
    }
  });

test('Cat guest listener removal preserves private stream close, receipts and error ownership',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const session = await createCatSessionForInternalTest(t, { dataRoot: root,
      installationId: 'private-listeners', source: `
        const fs = require('fs');
        let count = 0, removedCalls = 0;
        module.exports.home = async () => {
          const path = '/write-' + (++count) + '.txt';
          const stream = fs.createWriteStream(path);
          let fd;
          stream.once('open', value => { fd = value; });
          stream.on('close', () => removedCalls++);
          stream.on('error', () => removedCalls++);
          stream.removeAllListeners('close');
          stream.removeAllListeners('error');
          const closed = new Promise(resolve => stream.once('close', resolve));
          stream.end('durable-' + count);
          await closed;
          return { path, fd, count, removedCalls, closed: stream.closed };
        };
        module.exports.search = async () => {
          const stream = fs.createWriteStream('/tmp');
          stream.on('error', () => removedCalls++);
          stream.removeAllListeners();
          const closed = new Promise(resolve => stream.once('close', resolve));
          stream.end('cannot write a directory');
          await closed;
          return { code: stream.errored.code, removedCalls, closed: stream.closed };
        };
      ` });
    const baseline = session.resourceScope().cleanups.size;
    const receipts = [], written = session.storageWritten.bind(session);
    session.storageWritten = scope => { receipts.push(scope); written(scope); };
    for (let count = 1; count <= 4; count++) {
      const result = await session.invoke({ siteId: 'default', operation: 'home', payload: {} });
      assert.equal(result.count, count); assert.equal(result.removedCalls, 0);
      assert.equal(result.closed, true);
      assert.equal(readFileSync(join(root, 'volumes', 'private-listeners', result.path.slice(1)),
        'utf8'), `durable-${count}`);
      assert.throws(() => fs.fstatSync(result.fd), { code: 'EBADF' });
      assert.equal(receipts.length, count);
      assert.equal(receipts.at(-1).requestId > 0, true);
      assert.equal(session.node.volume.streams.size, 0);
      assert.equal(session.node.volume.streamCleanup.size, 0);
      assert.equal(session.resourceScope().cleanups.size, baseline);
    }
    assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'search', payload: {} }),
      { code: 'EISDIR', removedCalls: 0, closed: true });
    assert.equal(session.failure, undefined);
    assert.equal(receipts.length, 4);
    assert.equal(session.node.volume.streams.size, 0);
    assert.equal(session.node.volume.streamCleanup.size, 0);
    assert.equal(session.resourceScope().cleanups.size, baseline);
  });

test('Cat guest listener removal preserves HTTP request error and close ownership',
  { timeout: 30000 }, async t => {
    const upstream = await serverFor(t, request => request.socket.destroy());
    const session = await createCatSessionForInternalTest(t, { installationId: 'http-listeners',
      source: `
        const http = require('http');
        module.exports.home = () => new Promise(resolve => {
          const request = http.get(${JSON.stringify(upstream.socketOrigin)}, { agent: false });
          request.removeAllListeners();
          request.once('close', () => resolve({ closed: true }));
        });
      ` });
    for (let index = 0; index < 3; index++) {
      assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
        { closed: true });
      await waitFor(() => session.nativeRequests.size === 0 &&
        session.services.requestCleanup.size === 0, 'HTTP ownership did not retire after native close');
      assert.equal(session.failure, undefined);
    }
  });

test('Cat guest close listener removal cannot retain an owned mapped TCP socket', async t => {
  const session = await createCatSessionForInternalTest(t, { installationId: 'tcp-listeners',
    source: `
      const net = require('net');
      module.exports.home = () => new Promise(resolve => {
        const socket = net.connect({ host: '127.0.0.1', port: Number(process.env.PORT) });
        socket.once('connect', () => {
          socket.removeAllListeners('close');
          socket.removeAllListeners('error');
          socket.once('close', () => resolve({ closed: true }));
          socket.destroy();
        });
      });
    ` });
  const baseline = session.node.resourceCleanup.size;
  for (let index = 0; index < 3; index++) {
    assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
      { closed: true });
    await waitFor(() => session.node.networkResources.size === 0 &&
      session.node.resourceCleanup.size === baseline, 'mapped TCP socket retained an internal owner');
    assert.equal(session.failure, undefined);
  }
});

test('Cat preserves retired request attribution through native async continuations', async t => {
  const session = new CatSession({ dataRoot: rootFor(t), installationId: 'context-fixture', generation: 1,
    revision: { source: '', config: {}, version: '1' }, limits: { cleanupTimeoutMs: 100 } });
  const scope = new ResourceScope(session.root.signal); scope.requestId = 7;
  let resume; const gate = new Promise(resolve => { resume = resolve; });
  const task = session.contextScope.run(scope, async () => { await gate; return session.scope(); });
  scope.close(); resume();
  assert.equal(await task, scope); assert.equal(scope.signal.aborted, true);
  await session.stop();
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
  const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
  const color = node.source('supports-color');
  const flag = node.source('has-flag', color.id);
  assert.equal(node.source('supports-color', flag.id).id, color.id);
  const parent = node.source('@opentelemetry/api');
  assert.throws(() => node.source('@opentelemetry/api/../supports-color', parent.id),
    { code: 'UNSUPPORTED_DEPENDENCY' });
  assert.throws(() => node.source('@opentelemetry/api/node_modules/events', parent.id),
    { code: 'UNSUPPORTED_DEPENDENCY' });
  root.close();
  session.live = false;
  await node.close();
});

test('Cat adapter completion is physically tracked until settlement', async t => {
  const session = new CatSession({ dataRoot: rootFor(t), installationId: 'adapter-settlement', generation: 1,
    revision: { source: '', config: {}, version: '1' }, limits: { cleanupTimeoutMs: 100 } });
  let complete; const task = new Promise(resolve => { complete = resolve; });
  session.trackAdapter(task); assert.equal(session.stats().pendingAdapterTasks, 1);
  const retirement = session.adapterRetirement(); complete('settled');
  await retirement.idle; assert.equal(retirement.pendingAdapterTasks(), 0);
  await session.stop();
});

test('Cat native stream registration failure releases its actual resource', async t => {
  const root = new ResourceScope();
  const session = { installationId: 'registration-rollback', revision: { source: '' }, root,
    limits: { cleanupTimeoutMs: 100 }, live: true, scope: () => root, storageWritten() {} };
  const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
  const failure = new Error('synthetic scope registration failure');
  session.resourceScope = () => ({ assertOpen() {}, add() { throw failure; } });
  const resource = new PassThrough();
  assert.throws(() => node.ownStream(resource), error => error === failure);
  assert.equal(resource.destroyed, true); assert.equal(node.streams.has(resource), false);
  session.live = false; await node.close(); root.close();
});

test('Cat ignores a retained nonterminal callback after session shutdown', async t => {
  const session = new CatSession({ dataRoot: rootFor(t), installationId: 'late-callback', generation: 1,
    revision: { source: '', config: {}, version: '1' }, limits: { cleanupTimeoutMs: 100 } });
  const emitter = session.node.observeEmitter(new EventEmitter());
  let calls = 0; emitter.on('work', () => calls++);
  emitter.emit('work'); assert.equal(calls, 1);
  await session.stop(); emitter.emit('work'); assert.equal(calls, 1);
});

test('Cat removed native listeners release callback references', async t => {
  const session = new CatSession({ dataRoot: rootFor(t), installationId: 'callback-reachability', generation: 1,
    revision: { source: '', config: {}, version: '1' }, limits: { cleanupTimeoutMs: 100 } });
  const emitter = session.node.observeEmitter(new EventEmitter());
  let callback = () => {}; const reference = new WeakRef(callback);
  emitter.on('work', callback); assert.equal(emitter.listenerCount('work'), 1);
  emitter.removeListener('work', callback); callback = null;
  await collectGarbage(); assert.equal(reference.deref(), undefined);
  await session.stop();
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
    };
    const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
    try {
      let emitter = new EventEmitter();
      const reference = new WeakRef(emitter);
      const register = value => node.observeEmitter(value).on('event', () => {});
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

test('Cat ordinary native values do not create resource registrations', async t => {
  const session = await createCatSessionForInternalTest(t, { installationId: 'native-values',
    source: `const retained = new URL('https://example.invalid/');
      module.exports.home = () => ({ href: retained.href, size: require('fs').statSync('/source.js').size });` });
  const baseline = resourceCounts(session);
  for (let index = 0; index < 4; index++) {
    const value = await session.invoke({ siteId: 'default', operation: 'home', payload: {} });
    assert.equal(value.href, 'https://example.invalid/'); assert.ok(value.size > 0);
  }
  await session.whenAdaptersIdle(); assert.deepEqual(resourceCounts(session), baseline);
});

test('Cat reports native stream disposal failure without retrying stop', async t => {
  const root = new ResourceScope();
  const session = { installationId: 'disposal-failure', revision: { source: '' }, root,
    limits: { cleanupTimeoutMs: 100 }, live: true, scope: () => root, storageWritten() {} };
  const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
  const resource = new PassThrough(); let attempts = 0;
  const failure = new Error('synthetic disposal failure');
  resource.destroy = () => { attempts++; resource.emit('close'); throw failure; };
  node.ownStream(resource);
  await assert.rejects(node.close(), error => error === failure);
  await assert.rejects(node.close(), error => error === failure);
  assert.equal(attempts, 1); assert.equal(node.streams.size, 0); root.close();
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
    const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
    let releases = 0;
    const reader = {
      cancel() { return new Promise(() => {}); },
      releaseLock() { releases++; },
    };
    node.ownWeb(reader, 'reader');
    await assert.rejects(node.close(), { code: 'TIMEOUT' });
    assert.equal(releases, 1);
    assert.equal(node.webResources.size, 0);

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
    const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
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
    node.ownWeb(reader, 'reader');
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
  const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
  const stream = new ReadableStream({
    start(controller) {
      controller.error(Object.assign(new Error('synthetic cancellation'), { code: 'ERR_CANCELED' }));
    },
  });
  const reader = stream.getReader();
  reader.closed.catch(() => {});
  node.ownWeb(reader, 'reader');
  await node.close();
  assert.equal(stream.locked, false);
  assert.equal(node.webResources.size, 0);
  scope.close();
});

test('Cat Web stream cleanup distinguishes a stored operation error from a new cancel failure', async t => {
  const scope = new ResourceScope();
  const session = {
    installationId: 'errored-reader', revision: { source: '' }, root: scope,
    limits: { cleanupTimeoutMs: 100 }, live: false, scope: () => scope,
    storageWritten() {}, trackAdapter() {},
  };
  const node = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
  const stored = new Error('synthetic operation error');
  const stream = new ReadableStream({ start(controller) { controller.error(stored); } });
  node.ownWeb(stream, 'readableWeb');
  await node.close();
  assert.equal(stream.locked, false);
  const failing = nativeCapabilities(session, { close: async () => {} }, rootFor(t));
  const failure = new Error('synthetic cancel failure');
  const active = new ReadableStream({ cancel() { throw failure; } });
  failing.ownWeb(active, 'readableWeb');
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
        agent: false, headers: { connection: 'Upgrade', upgrade: 'test' },
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
  const node = nativeCapabilities(session, {
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
  socket.on('error', guest);
  socket.removeAllListeners('error');
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
    const node = nativeCapabilities(session, {
      http: {}, https: {}, close: async () => {},
    }, rootFor(t));
    const socket = new EventEmitter();
    socket.closed = false;
    socket.destroyed = false;
    socket.destroy = () => { socket.destroyed = true; };
    node.ownNetwork(socket);
    const ownedCleanups = scope.cleanups.size;
    assert.ok(ownedCleanups >= 2);
    await assert.rejects(node.close(), { code: 'TIMEOUT' });
    assert.equal(node.networkResources.size, 0);
    assert.equal(node.resourceCleanup.size, 0);
    assert.equal(scope.cleanups.size, 1);
    assert.equal(socket.listenerCount('error'), 1);
    assert.doesNotThrow(() => socket.emit('error', new Error('late socket error')));
    socket.closed = true;
    socket.emit('close');
    assert.equal(socket.listenerCount('close'), 0);
    assert.equal(socket.listenerCount('error'), 1);
    assert.equal(node.streams.size, 0);
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
  assert.equal(endpoint.managementUrl, endpoint.origin + '/website');
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
    if (event === 'error' && attempts++ === 0) throw failure;
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
      selfHeaders: { hostCorrect: true, explicit: 'synthetic-only', scope: null },
      configValue: null,
      cookie: null,
      multipart: { contentType: true, text: true, file: true },
      responseStream: 'stream-body',
      deletedSetCookie: true,
    });
    assert.equal(virtual.dartPort, 0);
    assert.deepEqual(virtual.os, {
      platform: process.platform, arch: process.arch, hostname: hostname(), homedir: '/', tmpdir: '/tmp',
      cpus: cpus().length, loopback: '127.0.0.1', totalmem: totalmem(),
      freemem: virtual.os.freemem,
    });
    assert.ok(virtual.os.freemem >= 0 && virtual.os.freemem <= virtual.os.totalmem);
    assert.equal(virtual.process.stdin, 0);
    assert.equal(virtual.process.stdout, 1);
    assert.equal(virtual.process.stderr, 2);
    assert.equal(virtual.process.title, 'script');
    assert.equal(virtual.process.rawDebug, 'function');
    assert.ok(virtual.process.usage.rss > virtual.process.usage.heapUsed);
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
    assert.deepEqual(Object.getOwnPropertySymbols(search), []);
    assert.match(search.list[0].vod_pic, new RegExp('^' + status.aggregate.origin.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '/proxy/'));
    assert.deepEqual(await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'home',
      payload: { resultField: true },
    }), { result: { value: 'nested-result' }, marker: 'outer-result' });

    const aggregate = await request(`${status.aggregate.origin}/cat/fixture-a/site-a/search`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
      },
      body: JSON.stringify({ wd: 'aggregate' }),
    });
    assert.equal(aggregate.status, 200);
    assert.equal(JSON.parse(aggregate.text).list[0].vod_name, 'aggregate');

    const page = await request(a.managementUrl);
    assert.equal(page.status, 200);
    assert.equal(page.headers['set-cookie'], undefined);
    assert.match(page.text, /site-a/);

    const play = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'play', payload: { id: 'hls' },
    });
    assert.equal(play._meow.routes[0].kind, 'script-proxy');
    assert.deepEqual(Object.getOwnPropertySymbols(play), []);
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
    assert.equal(cookieProxy.headers['x-upstream-cookie'], 'media_auth=client; __meow_private=blocked');
    assert.deepEqual(cookieProxy.headers['set-cookie'],
      ['media_session=kept; Path=/', '__meow_private=blocked; HttpOnly; Path=/']);
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

test('Cat aggregate preflight reflects private-network requests without route dispatch', async t => {
  const runtime = createCatRuntime({ dataRoot: rootFor(t) });
  t.after(() => runtime.close());
  const state = await runtime.start();
  const headers = { Host: 'caller.virtual.invalid', Origin: 'https://caller.invalid',
    'Access-Control-Request-Method': 'GET', 'Access-Control-Request-Headers': 'authorization,x-source-header' };
  for (const path of ['/cat/missing/site/home', '/proxy/missing/1/missing/media.bin']) {
    for (const privateNetwork of [undefined, 'false', 'true']) {
      const preflight = await request(state.aggregate.origin + path, {
        method: 'OPTIONS', headers: { ...headers,
          ...(privateNetwork === undefined ? {} : { 'Access-Control-Request-Private-Network': privateNetwork }) },
      });
      assert.equal(preflight.status, 204, path);
      assert.equal(preflight.bytes.length, 0);
      assert.equal(preflight.headers['access-control-allow-origin'], headers.Origin);
      assert.equal(preflight.headers['access-control-allow-credentials'], 'true');
      assert.equal(preflight.headers['access-control-allow-headers'], headers['Access-Control-Request-Headers']);
      assert.match(preflight.headers['access-control-allow-methods'], /\bGET\b/);
      assert.equal(preflight.headers['access-control-allow-private-network'],
        privateNetwork === 'true' ? 'true' : undefined);
    }
  }
  assert.deepEqual((await runtime.status()).instances, [], 'preflight does not require an installed source or media');
});

test('Cat listeners and aggregate accept external callers while preserving paths, explicit headers and resource ownership',
  { timeout: 30000 }, async t => {
    let captured = 0;
    const other = http.createServer((_request, response) => {
      captured++;
      response.end('other-origin');
    });
    await new Promise((resolve, reject) => {
      other.once('error', reject);
      other.listen(0, '127.0.0.1', resolve);
    });
    t.after(() => new Promise(resolve => {
      other.closeAllConnections();
      other.close(resolve);
    }));
    const address = Object.values(networkInterfaces()).flat()
      .find(item => item.family === 'IPv4' && !item.internal)?.address;
    assert.ok(address, 'this regression requires a real non-loopback IPv4 interface');
    const adapterCalls = [];
    const runtime = createCatRuntime({ dataRoot: rootFor(t), network: async input => {
      adapterCalls.push({ url: input.url, headers: input.options.headers });
      return { statusCode: 200, headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ url: input.url, headers: input.options.headers }) };
    } });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'open', version: '1', enabled: true, config: {}, source: String.raw`
        const http = require('http');
        let server, mediaReads = 0, managementReads = 0;
        const get = (url, headers) => new Promise((resolve, reject) => {
          http.get(url, { headers }, response => {
            const chunks = [];
            response.on('data', chunk => chunks.push(Buffer.from(chunk)));
            response.once('end', () => {
              try { resolve(JSON.parse(Buffer.concat(chunks).toString())); }
              catch (error) { reject(error); }
            });
            response.once('error', reject);
          }).once('error', reject);
        });
        module.exports.start = () => new Promise(resolve => {
          server = http.createServer(async (request, response) => {
            const json = value => {
              response.setHeader('content-type', 'application/json');
              response.end(JSON.stringify(value));
            };
            if (request.url === '/full-config' || request.url === '/config') {
              managementReads++;
              json({ video: { sites: [{ key: 'open', name: 'Open',
                api: 'http://127.0.0.1:9988/api/open', actions: ['home', 'play', 'probe', 'counts'] }] } });
            } else if (request.url === '/website') {
              response.end('<title>Open</title>');
            } else if (request.url === '/api/open/home') {
              json({ opened: true });
            } else if (request.url === '/api/open/counts') {
              json({ mediaReads, managementReads });
            } else if (request.url === '/api/open/play') {
              json({ parse: 0, url: 'http://127.0.0.1:9988/media.bin?raw=a%2Fb%252F' });
            } else if (request.url === '/api/open/probe') {
              const headers = { Host: 'source.virtual.invalid', Origin: 'https://source.invalid',
                Cookie: 'source=explicit' };
              json({
                self: await get('http://reader:synthetic-only@127.0.0.1:9988/echo?raw=a%2Fb', headers),
                external: await get('http://reader:synthetic-only@outside.invalid/echo?raw=a%2Fb', headers),
                override: await get('http://reader:synthetic-only@outside.invalid/override',
                  { ...headers, Authorization: 'source-explicit' }),
              });
            } else if (request.url === '/media.bin?raw=a%2Fb%252F') {
              mediaReads++;
              response.setHeader('content-type', 'application/octet-stream');
              response.end(Buffer.from([0, 1, 127, 128, 255]));
            } else {
              json({ path: request.url, headers: request.headers,
                rawHeaders: request.rawHeaders });
            }
          });
          server.listen(9988, '127.0.0.1', resolve);
        });
        module.exports.stop = () => { server.closeAllConnections(); server.close(); };
      `,
    });
    const state = await runtime.status(), instance = state.instances[0];
    const external = value => {
      const url = new URL(value);
      url.hostname = address;
      return url;
    };
    const headers = { Host: 'caller.virtual.invalid', Origin: 'https://caller.invalid' };
    const page = await request(external(instance.managementUrl), { headers });
    assert.equal(page.status, 200);
    assert.equal(page.text, '<title>Open</title>');
    assert.equal(page.headers['set-cookie'], undefined);
    assert.equal(page.headers['access-control-allow-origin'], headers.Origin);
    const home = await request(external(state.aggregate.origin + '/cat/open/open/home'), { headers });
    assert.equal(home.status, 200);
    assert.deepEqual(JSON.parse(home.text), { opened: true });
    for (const origin of [instance.endpoints[0].origin, state.aggregate.origin]) {
      const preflight = await request(external(origin + '/website'), {
        method: 'OPTIONS', headers: { ...headers, 'Access-Control-Request-Headers': 'x-source-header' },
      });
      assert.equal(preflight.status, 204);
      assert.equal(preflight.headers['access-control-allow-origin'], headers.Origin);
      assert.equal(preflight.headers['access-control-allow-headers'], 'x-source-header');
    }
    const invoke = operation => runtime.invoke({
      installationId: 'open', siteId: 'open', operation, payload: {},
    });
    const before = await invoke('counts');
    const play = await runtime.invoke({
      installationId: 'open', siteId: 'open', operation: 'play', payload: {},
    });
    assert.deepEqual(await invoke('counts'), before, 'address preparation does not read media or management');
    const media = await request(external(play.url), { headers });
    assert.equal(media.status, 200);
    assert.deepEqual(media.bytes, Buffer.from([0, 1, 127, 128, 255]));
    const route = new URL(play.url), prefix = route.pathname.split('/').slice(0, 5).join('/');
    const paths = [
      `//127.0.0.1:${other.address().port}/capture`,
      '/website', '/admin', '/__meow/open',
      '/%2577ebsite', '/%2561dmin', '/%255f%255fmeow/open',
      '/%252e%252e/full-config', '/%255cwebsite', '/%252f%252fexample.invalid/website',
    ];
    for (const path of paths) {
      const reply = await request(external(`${route.origin}${prefix}${path}?raw=a%2Fb%252F`), { headers });
      assert.equal(reply.status, 200, path);
      const echoed = JSON.parse(reply.text);
      assert.equal(echoed.path, path + '?raw=a%2Fb%252F');
      assert.equal(echoed.headers.host, headers.Host);
      assert.equal(echoed.headers.origin, headers.Origin);
      assert.equal(echoed.headers.authorization, undefined);
    }
    assert.equal(captured, 0);
    const probe = await invoke('probe');
    const basic = 'Basic ' + Buffer.from('reader:synthetic-only').toString('base64');
    assert.equal(probe.self.path, '/echo?raw=a%2Fb');
    for (const value of [probe.self, probe.external]) {
      assert.equal(value.headers.authorization, basic);
      assert.equal(value.headers.host, 'source.virtual.invalid');
      assert.equal(value.headers.origin, 'https://source.invalid');
      assert.equal(value.headers.cookie, 'source=explicit');
      assert.equal(value.headers['x-meow-request-scope'], undefined);
    }
    assert.equal(probe.self.rawHeaders.includes('Host'), true);
    assert.equal(probe.self.rawHeaders.some(value => value.toLowerCase() === 'x-meow-request-scope'), false);
    assert.equal(probe.override.headers.authorization, 'source-explicit');
    assert.deepEqual(adapterCalls.map(call => call.url),
      ['http://outside.invalid/echo?raw=a%2Fb', 'http://outside.invalid/override']);
    const session = runtime.getSession('open');
    await runtime.setEnabled('open', false);
    const stale = await request(external(play.url));
    assert.equal(stale.status, 502);
    assert.deepEqual(JSON.parse(stale.text), { error: { code: 'STALE_SESSION' } });
    assert.equal(session.live, false);
    await assert.rejects(request(external(instance.managementUrl)), { code: 'ECONNREFUSED' });
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

test('Cat provider list identity distinguishes missing, invalid and unknown shares after capture',
  { timeout: 30000 }, async t => {
    const calls = [];
    const runtime = await installed(t, rootFor(t), {
      network: async input => {
        calls.push({ url: input.url, method: input.options.method,
          body: Buffer.from(input.options.body).toString() });
        return { statusCode: 200, headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ originalNetwork: true }) };
      },
    });
    const status = await runtime.status();
    writeSettings(status.settingsPath, value => ({
      ...value, providers: { ...value.providers, quark: true, uc: true },
    }));
    await runtime.refresh();
    for (const provider of ['quark', 'uc']) {
      const invoke = async payload => {
        const output = await runtime.invoke({
          installationId: 'fixture-a', siteId: 'site-a', operation: 'panLists',
          payload: { provider, ...payload },
        });
        await waitFor(() => runtime.host.get('fixture-a').resources.networkRequests === 0,
          'provider list interception or fallback retained a native request');
        return output;
      };
      const captures = output => (output._meow?.captures ?? []).map(item => decodePanResource(item.id));
      for (const body of [
        { pwd_id: '' }, { pwd_id: 'x' }, { pwd_id: 'root' }, { pwd_id: 'unknown-share' },
        { pwd_id: null }, { pwd_id: false }, { pwd_id: true }, { pwd_id: ['abcd1234'] },
        { pwd_id: { id: 'abcd1234' } }, { params: { pwd_id: null } },
        { pwd_id: null, surl: 'abcd1234' },
      ]) {
        const before = calls.length;
        const result = await invoke({ body });
        assert.deepEqual(result.upstream, { originalNetwork: true });
        assert.equal(calls.length, before + 1);
        assert.equal(calls.at(-1).method, 'POST');
        assert.equal(calls.at(-1).body, JSON.stringify(body));
        assert.equal(new URL(calls.at(-1).url).pathname, '/1/clouddrive/share/sharepage/detail');
        assert.deepEqual(captures(result).map(item => [item.provider, item.shareId, item.password]),
          [[provider, 'abcd1234', 'synthetic-share-password']]);
      }
      for (const query of ['?pwd_id=', '?pwd_id=x', '?pwd_id=unknown-share']) {
        const before = calls.length;
        const result = await invoke({ query });
        assert.deepEqual(result.upstream, { originalNetwork: true });
        assert.equal(calls.length, before + 1);
        assert.equal(new URL(calls.at(-1).url).search, query);
      }
      const before = calls.length;
      for (const body of [
        { pwd_id: 'abcd1234' }, {}, { pwd_id: 'abcd1234', passcode: '' }, { passcode: '' },
        { pwd_id: 'abcd1234', passcode: 'synthetic-new-password' }, { passcode: 'synthetic-new-password' },
      ]) {
        const result = await invoke({ body, inspectMetadata: true });
        assert.equal(result.upstream.code, 0);
        assert.match(result.upstream.data.list[0].fid, /^meow_/);
        assert.equal(result.listTotal, 1);
        assert.deepEqual(result.upstream.metadata, { _total: 1 });
        assert.deepEqual(result.upstream.data.metadata, result.upstream.metadata);
        assert.equal(calls.length, before);
        assert.deepEqual(captures(result).map(item => [item.provider, item.shareId, item.password]),
          [[provider, 'abcd1234', body.passcode ?? 'synthetic-share-password']]);
        assert.deepEqual(result.upstreamShape, {
          rawHeaders: true, trailers: true, rawTrailers: true,
          httpVersion: '1.1', major: 1, minor: 1, complete: true,
        });
      }
      for (const payload of [{ capture: false }, { secondShare: true }]) {
        const before = calls.length;
        const result = await invoke(payload);
        assert.deepEqual(result.upstream, { originalNetwork: true });
        assert.equal(calls.length, before + 1);
        assert.equal(captures(result).length, payload.secondShare ? 2 : 0);
      }
    }
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
    const pipes = await invoke('pipeLifecycle');
    for (const phase of ['unpipe', 'finish', 'premature']) {
      assert.equal(pipes[phase].data, 0);
      assert.equal(pipes[phase].end, 0);
      assert.equal(pipes[phase].drain, 0);
      assert.equal(pipes[phase].finish, 0);
      assert.ok(pipes[phase].error <= 1);
      assert.ok(pipes[phase].close <= 1);
    }
    assert.equal(pipes.premature.destroyed, true);
    assert.match((await invoke('play')).url, /\/proxy\/fixture-a\//);
    const endpoint = session.endpoint();
    assert.equal((await request(endpoint.origin + '/api/site-a/home', {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
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
        },
        body: '{}',
      });
      assert.equal(reply.status, 200);
    }
    await new Promise(resolve => setImmediate(resolve));
    await collectGarbage();
    const bounded = counts();
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

test('Cat native values and one-shot callbacks do not retain request resources under sustained load',
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
    const counts = () => resourceCounts(session);
    const invoke = () => session.invoke({
      siteId: 'value-handles', operation: 'home', payload: {},
    });
    // Establish the persistent Agent connection before measuring per-request
    // retirement, not the number of intentionally idle keep-alive sockets.
    await invoke();
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
    assert.deepEqual(midpoint, baseline);
    assert.deepEqual(bounded, baseline);
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
    const stream = [...session.node.streams.keys()].find(value => typeof value.flush === 'function');
    assert(stream);
    assert.equal(stream.destroyed, false);
    await session.stop();
    assert.equal(stream.destroyed, true);
    assert.equal(session.live, false);
  });

test('Cat root-scope pipe and unpipe release closed native streams',
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
    assert.equal(pipeRoot.node.streams.size, pipeControl.node.streams.size);
  });

test('Cat native streams pipe complete bytes directly into private files',
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
          destroy = incoming.destroy.bind(incoming);
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
    assert.equal(session.node.resourceCleanup.size, 0);
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
    const values = Object.fromEntries([...session.node.webResources].map(([value, role]) => [role, value]));
    assert.deepEqual(Object.keys(values).sort(), ['readableWeb', 'reader', 'writableWeb', 'writer'].sort());
    assert.equal(values.readableWeb.locked, true);
    assert.equal(values.writableWeb.locked, true);
    await session.stop();
    assert.equal(values.readableWeb.locked, false);
    assert.equal(values.writableWeb.locked, false);
    assert.equal(session.node.resourceCleanup.size, 0);
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
      headers: { host: new URL(endpoint.origin).host },
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
      session.adapterTasks.size === 0 && session.nativeRequests.size === 0,
    'guest request resources were not released after disconnect');
    const next = await scriptRequest(endpoint, '/bridge/ok');
    assert.equal(next.status, 200);
    assert.deepEqual(JSON.parse(next.text), { ok: true });
    assert.equal(session.live, true);
  });

test('Cat aggregate media proxy streams before completion, propagates backpressure, Range and disconnect',
  { timeout: 30000 }, async t => {
    const size = 32 * 1024 * 1024;
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'proxy-flow', enabled: true, source: `
      const http = require('http'), { Readable } = require('stream');
      let server, produced = 0, finished = false, aborted = false;
      const bytes = Buffer.alloc(64 * 1024, 0x51), size = ${size};
      module.exports.start = () => {
        server = http.createServer((request, response) => {
          const json = value => { response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify(value)); };
          if (request.url === '/config' || request.url === '/full-config')
            return json({ video: { sites: [{ key: 'flow', api: '/api', actions: ['play', 'state'] }] } });
          if (request.url === '/api/play')
            return json({ parse: 0, url: 'http://127.0.0.1:9988/media' });
          if (request.url === '/api/state') return json({ produced, finished, aborted });
          if (request.url !== '/media') { response.statusCode = 404; return response.end(); }
          if (request.headers.range) {
            response.writeHead(206, { 'content-type': 'application/octet-stream',
              'content-range': 'bytes 10-19/' + size, 'content-length': '10' });
            return response.end(bytes.subarray(10, 20));
          }
          produced = 0; finished = false; aborted = false;
          const input = new Readable({ read() {
            while (produced < size) {
              produced += bytes.length;
              if (!this.push(bytes)) return;
            }
            this.push(null);
          } });
          response.writeHead(200, { 'content-type': 'application/octet-stream', 'content-length': String(size) });
          response.once('finish', () => { finished = true; });
          response.once('close', () => { aborted = !response.writableFinished; input.destroy(); });
          input.pipe(response);
        });
        server.listen(9988);
      };
      module.exports.stop = () => { server.closeAllConnections(); server.close(); };
    ` });
    const invoke = operation => runtime.invoke({ installationId: 'proxy-flow', siteId: 'flow',
      operation, payload: {} });
    const { url } = await invoke('play');
    let first, resume, total = 0;
    const started = new Promise(resolve => { first = resolve; });
    const digest = createHash('sha256');
    const completed = new Promise((resolve, reject) => {
      const client = http.get(url, response => {
        assert.equal(response.statusCode, 200);
        response.on('data', bytes => {
          total += bytes.length; digest.update(bytes); response.pause();
          if (!resume) { resume = () => response.resume(); first(); }
          else setTimeout(() => response.resume(), 1);
        });
        response.once('end', resolve); response.once('error', reject);
      });
      client.once('error', reject);
      t.after(() => client.destroy());
    });
    await started;
    assert.ok(total > 0 && total < size, 'the first chunk must arrive before the complete body');
    await new Promise(resolve => setTimeout(resolve, 100));
    const paused = await invoke('state');
    assert.equal(paused.finished, false);
    assert.ok(paused.produced > 0 && paused.produced < size,
      'a paused downstream must stop the finite upstream from producing the whole body');
    resume();
    await completed;
    assert.equal(total, size);
    assert.equal(digest.digest('hex'), sha256(Buffer.alloc(size, 0x51)));
    const ranged = await request(url, { headers: { range: 'bytes=10-19' } });
    assert.equal(ranged.status, 206);
    assert.equal(ranged.headers['content-range'], `bytes 10-19/${size}`);
    assert.deepEqual(ranged.bytes, Buffer.alloc(10, 0x51));
    await new Promise((resolve, reject) => {
      const client = http.get(url, response => {
        response.on('error', () => {});
        response.once('data', () => { client.destroy(); resolve(); });
      });
      client.once('error', reject);
    });
    const deadline = Date.now() + 5000;
    let state;
    do {
      state = await invoke('state');
      if (!state.aborted) await new Promise(resolve => setTimeout(resolve, 20));
    } while (!state.aborted && Date.now() < deadline);
    assert.equal(state.aborted, true);
    assert.equal(state.finished, false);
    assert.equal((await runtime.status()).instances[0].state, 'ready');
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

test('Cat listener returns a complete 17 MiB response and stays usable',
  { timeout: 60000 }, async t => {
    const upstream = await streamUpstream(t);
    const root = rootFor(t);
    const runtime = createCatRuntime({
      dataRoot: root, network: networkForLocalServers([upstream]),
    });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'bridge',
      source: streamBridgeSource(upstream.origin, sha256(upstream.values)),
      config: {}, version: '1', enabled: true,
    });
    const size = 17 * 1024 * 1024;
    const expected = upstream.blob(size);
    const value = await request(runtime.getSession('bridge').endpoint().origin + '/bridge/proxy?size=' + size);
    assert.equal(value.status, 200);
    assert.equal(value.bytes.length, size);
    assert.equal(sha256(value.bytes), sha256(expected));
    assert.equal((await runtime.status()).instances[0].state, 'ready');
    assert.deepEqual(await runtime.invoke({
      installationId: 'bridge', siteId: 'bridge', operation: 'ok', payload: {},
    }), { ok: true });
  });

test('Cat aggregate entry accepts complete 12 and 17 MiB request bodies',
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
      },
      body: JSON.stringify({ data: 'z'.repeat(17 * 1024 * 1024) }),
    }).catch(error => ({ status: null, error: error.code }));
    assert.equal(rejected.status, 200);
    assert.equal(rejected.error, undefined);
    assert.deepEqual(JSON.parse(rejected.text), { dataLength: 17 * 1024 * 1024,
      length: Buffer.byteLength(JSON.stringify({ data: 'z'.repeat(17 * 1024 * 1024) })) });
    const next = await request(boundedStatus.aggregate.origin + path, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
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
      },
      body: JSON.stringify({ wd: 'wait' }),
    });
    assert.equal((await pending).status, 200);
    assert.equal((await runtime.status()).instances[0].error, null);
  });

test('Cat native buffers preserve network ownership and byte independence',
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

test('Cat native callbacks and methods preserve bytes and typed-array subviews',
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
    assert.equal(session.adapterTasks.size, 0);
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
  assert.equal(session.adapterTasks.size, 0);
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

test('Cat instance stop releases background timers, descriptors, requests and streams',
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
    assert.equal(session.node.resourceCleanup.size, 0);
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
    session.callbackFailed(fault('CANCELLED'), finishedRequest);
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
    session.callbackFailed(fault('UPSTREAM_HTTP_ERROR'), runningRequest);
    assert.equal(runningRequest.signal.aborted, true);
    assert.equal(runningRequest.reason.code, 'UPSTREAM_HTTP_ERROR');
    assert.equal(runningRead.signal.aborted, false);
    assert.equal(diagnostics.length, 1);
    runningRead.close();
  });

test('Cat M3U8 enrichment creates the root extension when no earlier enhancement exists',
  { timeout: 30000 }, async t => {
    let reads = 0;
    const runtime = await installed(t, rootFor(t), {
      network: async input => {
        reads++;
        assert.equal(input.url, 'https://media.example.invalid/master.m3u8');
        return {
          statusCode: 200,
          headers: { 'content-type': 'application/vnd.apple.mpegurl' },
          body: '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1280x720\nchild.m3u8\n',
        };
      },
    });
    const receipt = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'externalPlay', payload: {},
    }, { mediaPurpose: 'receipt' });
    assert.equal(receipt.url, 'https://media.example.invalid/master.m3u8');
    assert.equal(receipt._meow, undefined);
    assert.deepEqual(Object.getOwnPropertySymbols(receipt), []);
    assert.equal(reads, 0, 'receipt resolution cannot probe a manifest');
    const result = await runtime.invoke({
      installationId: 'fixture-a', siteId: 'site-a', operation: 'externalPlay', payload: {},
    });
    assert.equal(result._meow.version, 1);
    assert.equal(result._meow.media[0].variants[0].quality, '720P');
    assert.deepEqual(Object.getOwnPropertySymbols(result), []);
    assert.equal(result._meow.media[0].variants[0].url,
      'https://media.example.invalid/child.m3u8');
    assert.equal(reads, 1, 'ordinary Cat consumption keeps its optional manifest enhancement');
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

test('Cat stops active I/O before a cooperative stop hook waits and durably writes its volume',
  { timeout: 30000 }, async t => {
    let held;
    const upstream = await serverFor(t, request => { held = request.socket; });
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    await runtime.install({ installationId: 'io-before-stop', enabled: true,
      source: delayedSource('io-before-stop', 0) + `
        const fs = require('fs'), start = module.exports.start;
        let closed;
        module.exports.start = () => {
          const request = http.get(${JSON.stringify(upstream.socketOrigin)}, { agent: false });
          request.on('error', () => {});
          closed = new Promise(resolve => request.once('close', resolve));
          return start();
        };
        module.exports.stop = async () => {
          fs.writeFileSync('/stop-entered.txt', 'entered');
          await closed;
          await new Promise((resolve, reject) =>
            server.close(error => error ? reject(error) : resolve()));
          await new Promise(resolve => setImmediate(resolve));
          fs.writeFileSync('/stop-finished.txt', 'durable');
        };
      ` });
    await waitFor(() => held, 'background connection did not reach the local I/O barrier');
    const owner = runtime.getSession('io-before-stop'), worker = owner.worker;
    let exited = false;
    worker.once('exit', () => { exited = true; });
    const result = await runtime.setEnabled('io-before-stop', false);
    assert.equal(result.state, 'disabled');
    assert.equal(readFileSync(join(root, 'volumes', 'io-before-stop', 'stop-finished.txt'),
      'utf8'), 'durable');
    assert.equal(owner.cleanupError, null);
    assert.equal(owner.worker, null); assert.equal(worker.threadId, -1);
    assert.equal(exited, true);
    await waitFor(() => held.destroyed, 'shutdown retained the upstream connection');
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
    assert.equal((await request(authEndpoint.origin + '/full-config')).status, 401);
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
    const beforeFailure = diagnostics.length;
    await assert.rejects(invoke('failure'), { code: 'UPSTREAM_HTTP_ERROR' });
    // The listener request scope already owns the hook, before HTTP 500 is read.
    const failureDiagnostics = diagnostics.slice(beforeFailure);
    const diagnostic = failureDiagnostics.find(event => event.kind === 'TypeError' && event.reason === 'input');
    const httpError = failureDiagnostics.find(event =>
      event.kind === 'ScriptHTTPError' && event.reason === 'http' && event.statusCode === 500);
    assert.ok(diagnostic); assert.ok(httpError);
    assert.equal(diagnostic.siteId, 'one'); assert.equal(httpError.siteId, 'one');
    assert.equal(diagnostic.installationId, 'framework');
    assert.equal(diagnostic.generation, instance.generation);
    assert.ok(Number.isSafeInteger(diagnostic.requestId) && diagnostic.requestId > 0);
    for (const key of ['installationId', 'generation', 'requestId'])
      assert.equal(diagnostic[key], httpError[key]);
    const errorLine = frameworkSource.split('\n').findIndex(line => line.includes('const error = new TypeError('));
    assert.equal(diagnostic.line, errorLine + 1);
    assert.equal(diagnostic.column, frameworkSource.split('\n')[errorLine].indexOf('new TypeError(') + 1);
    assert.equal(diagnostic.member, 'record');
    assert.equal(JSON.stringify(diagnostics).includes('SYNTHETIC-PRIVATE-VALUE'), false);
    const catEndpoint = instance.endpoints.find(endpoint => endpoint.requestedPorts.includes(9988));
    assert.equal(new URL(instance.managementUrl).origin, catEndpoint.origin);
    const home = await request(catEndpoint.origin + '/api/one/home', { method: 'POST', body: '{}' });
    assert.equal(home.status, 200);
    assert.deepEqual(JSON.parse(home.text), { method: 'POST', query: {}, body: {} });
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

test('Cat native methods remain removable listeners and release their stream',
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
    const baseline = resourceCounts(session);
    const siteId = [...session.sites.keys()][0];
    for (let index = 0; index < 4; index++) {
      assert.deepEqual(await session.invoke({
        siteId, operation: 'home', payload: {},
      }), { registered: 1, removed: 0, destroyed: true });
      await collectGarbage();
      assert.deepEqual(resourceCounts(session), baseline);
    }
  });

test('Cat framework injection uses native HTTP headers, streams and subclasses',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'local-response', enabled: true, source: String.raw`
        const http = require('node:http'), https = require('node:https');
        const { Stream, Writable } = require('node:stream'), { inherits } = require('node:util');
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
            inherited: this instanceof http.ServerResponse && this instanceof http.OutgoingMessage &&
              this instanceof Stream,
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
          let invalidName, invalidValue;
          try { response.setHeader('invalid name', 'x'); } catch (error) { invalidName = error.code; }
          try { response.setHeader('x-value', 'x\r\nInjected: y'); } catch (error) { invalidValue = error.code; }
          response.writeHead('204', ['Set-Cookie', 'first=one', 'Set-Cookie', 'second=two']);
          response.end();
          return { invalidName, invalidValue, hasBody: response._hasBody,
            finished: response.finished, cachedCookie: response.getHeader('set-cookie') ?? null,
            cookies: Array.from(response._header.matchAll(/Set-Cookie: ([^\r]+)\r\n/g), value => value[1]) };
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
      hasBody: false, finished: true, cachedCookie: null, cookies: ['first=one', 'second=two'],
    });
  });

test('Cat supports native ECDH and public X509 certificate keys',
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

test('Cat native cached KeyObjects survive request retirement without retaining resources',
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
    const counts = () => resourceCounts(session);
    await collectGarbage();
    const baseline = counts();
    assert.deepEqual(await session.invoke({
      siteId: 'default', operation: 'home', payload: {},
    }), { privateType: 'private', publicType: 'public', secretType: 'secret',
      bits: 2048, exponent: '65537', algorithm: 'rsa', secretBytes: 32 });
    await collectGarbage();
    assert.deepEqual(counts(), baseline);
    for (let index = 0; index < 8; index++) {
      assert.deepEqual(await session.invoke({
        siteId: 'default', operation: 'search', payload: {},
      }), { decrypted: true, same: true, different: true, signed: true, hmac: true,
        exported: true, detected: true, notDetected: true, invalid: true, nativeBinding: 'undefined', keys: 0 });
      await collectGarbage();
      assert.deepEqual(counts(), baseline);
    }
  });

test('Cat uses native KeyObjects and preserves nonextractable CryptoKeys', async t => {
  const session = await createCatSessionForInternalTest(t, { installationId: 'native-keys', source: `
    const crypto = require('crypto');
    module.exports.home = async () => {
      const key = await crypto.webcrypto.subtle.importKey('raw', Buffer.alloc(32, 7),
        { name: 'AES-GCM' }, false, ['encrypt']);
      const encrypted = await crypto.webcrypto.subtle.encrypt({ name: 'AES-GCM', iv: new Uint8Array(12) },
        key, Buffer.from('fixture'));
      let exportError; try { await crypto.webcrypto.subtle.exportKey('raw', key); }
      catch (error) { exportError = error.name; }
      const secret = crypto.createSecretKey(Buffer.alloc(32, 7));
      return { exportError, bytes: encrypted.byteLength, extractable: key.extractable,
        keyObject: require('util/types').isKeyObject(secret), equal: secret.equals(crypto.createSecretKey(secret.export())) };
    };` });
  assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
    { exportError: 'InvalidAccessError', bytes: 23, extractable: false, keyObject: true, equal: true });
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
      assert.deepEqual(current, baseline);
    }
    assert.deepEqual(midpoint, baseline);
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
  }), { code: 'ERR_INVALID_PROTOCOL', name: 'TypeError', trusted: true });
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

test('Cat native TLS certificates preserve cyclic issuer identity', async t => {
  // Generate a throw-away self-signed leaf in memory; no private key fixture,
  // external certificate service or extra npm/OpenSSL command is required.
  const der = (tag, ...parts) => {
    const body = Buffer.concat(parts.map(part => Buffer.isBuffer(part) ? part : Buffer.from(part)));
    let length = body.length.toString(16);
    if (length.length % 2) length = '0' + length;
    const bytes = Buffer.from(length, 'hex');
    return Buffer.concat([Buffer.from([tag]), body.length < 128 ? Buffer.from([body.length])
      : Buffer.concat([Buffer.from([0x80 | bytes.length]), bytes]), body]);
  };
  const seq = (...values) => der(0x30, ...values);
  const algorithm = seq(der(6, Buffer.from('2a864886f70d01010b', 'hex')), der(5));
  const name = seq(der(0x31, seq(der(6, Buffer.from('550403', 'hex')), der(12, 'Cat fixture'))));
  const { publicKey, privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const tbs = seq(der(2, [1]), algorithm, name,
    seq(der(0x17, '200101000000Z'), der(0x17, '400101000000Z')), name,
    publicKey.export({ format: 'der', type: 'spki' }));
  const bytes = seq(tbs, algorithm, der(3, [0], sign('sha256', tbs, privateKey)));
  const cert = '-----BEGIN CERTIFICATE-----\n' + bytes.toString('base64').match(/.{1,64}/g).join('\n') +
    '\n-----END CERTIFICATE-----\n';
  const session = new CatSession({ dataRoot: rootFor(t), installationId: 'peer-certificate', generation: 1,
    revision: { source: '', version: '1', config: {} }, limits: { cleanupTimeoutMs: 3000 } });
  t.after(() => session.stop());
  const server = session.services.https.createServer({
    key: privateKey.export({ format: 'pem', type: 'pkcs8' }), cert,
  });
  await new Promise(resolve => server.listen(9443, resolve));
  const socket = session.node.builtins.tls.connect({ host: '127.0.0.1', port: 9443 });
  await new Promise((resolve, reject) => {
    socket.once('secureConnect', resolve); socket.once('error', reject);
  });
  const certificate = socket.getPeerCertificate(true);
  assert.equal(certificate.issuerCertificate, certificate);
  assert.equal(certificate.raw.equals(bytes), true);
  const x509 = socket.getPeerX509Certificate();
  assert.ok(x509 instanceof X509Certificate);
  assert.equal(x509.publicKey.type, 'public');
  assert.equal(x509.checkIssued(x509), true);
  socket.destroy();
  await session.stop();
  assert.equal(session.node.networkResources.size, 0);
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

test('Cat Web and crypto compatibility use native Node values',
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

test('Cat inactive stop permits only its own cleanup callbacks and durable writes',
  { timeout: 30000 }, async t => {
    const root = rootFor(t);
    const session = await createCatSessionForInternalTest(t, { dataRoot: root,
      installationId: 'cleanup-callbacks', source: `
        const fs = require('fs'), steps = [];
        process.on('old-work', () => fs.writeFileSync('/old-event.txt', 'unexpected'));
        process.once('close', () =>
          setImmediate(() => fs.writeFileSync('/old-terminal.txt', 'unexpected')));
        module.exports.home = () => ({ alive: true });
        module.exports.stop = async () => {
          process.emit('old-work');
          process.emit('close');
          process.once('cleanup-work', () => steps.push('event'));
          process.emit('cleanup-work');
          await new Promise(resolve => process.nextTick(() => { steps.push('nextTick'); resolve(); }));
          await new Promise(resolve => queueMicrotask(() => { steps.push('microtask'); resolve(); }));
          await new Promise(resolve => setTimeout(() => { steps.push('timer'); resolve(); }, 1));
          await new Promise(resolve => setImmediate(() => { steps.push('immediate'); resolve(); }));
          await new Promise((resolve, reject) => fs.writeFile('/cleanup.txt', 'durable', error => {
            if (error) return reject(error);
            steps.push('write'); resolve();
          }));
          await new Promise((resolve, reject) => fs.readFile('/cleanup.txt', 'utf8', (error, value) => {
            if (error) return reject(error);
            steps.push(value); resolve();
          }));
          fs.writeFileSync('/steps.json', JSON.stringify(steps));
        };
      ` });
    assert.deepEqual(await session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
      { alive: true });
    let background = 0;
    session.node.timers.setImmediate(() => { background++; });
    const stopped = session.stop();
    assert.equal(session.live, false);
    assert.equal(session.root.signal.aborted, false, 'stop still needs its open private volume');
    await assert.rejects(session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
      { code: 'STALE_SESSION' });
    await stopped;
    const volume = join(root, 'volumes', 'cleanup-callbacks');
    assert.deepEqual(JSON.parse(readFileSync(join(volume, 'steps.json'), 'utf8')),
      ['event', 'nextTick', 'microtask', 'timer', 'immediate', 'write', 'durable']);
    assert.equal(readFileSync(join(volume, 'cleanup.txt'), 'utf8'), 'durable');
    assert.equal(background, 0);
    assert.equal(existsSync(join(volume, 'old-event.txt')), false);
    assert.equal(existsSync(join(volume, 'old-terminal.txt')), false);
    assert.equal(session.root.signal.aborted, true);
    assert.equal(session.cleanupError, undefined);
  });

test('Cat inactive stop destroys once and waits for actual native close rather than destroyed',
  async t => {
    let session, finishDestroy;
    const finish = () => { const complete = finishDestroy; finishDestroy = null; complete?.(); };
    t.after(async () => {
      const stopped = session?.stop();
      finish();
      await stopped?.catch(() => {});
    });
    session = await createCatSessionForInternalTest(t, { installationId: 'cleanup-debt',
      source: 'module.exports.home = () => ({ alive: true });' });
    const request = new PassThrough({ destroy(error, done) { finishDestroy = () => done(error); } });
    const observed = [], aborted = [], destroy = request.destroy;
    let reentered;
    request.destroy = function(...args) {
      observed.push(session.live);
      reentered = session.stop();
      return destroy.apply(this, args);
    };
    session.node.webPipeAbort.signal.addEventListener('abort', () => { aborted.push(session.live); },
      { once: true });
    session.services.trackRequest(request, session.services.createOutboundScope(session.root));
    let settled = false;
    const stopped = session.stop();
    stopped.then(() => { settled = true; }, () => { settled = true; });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(reentered, stopped);
    assert.deepEqual(observed, [false]);
    assert.deepEqual(aborted, [false]);
    assert.equal(request.destroyed, true);
    assert.equal(request.closed, false);
    assert.equal(settled, false);
    assert.equal(session.nativeRequests.has(request), true);
    assert.equal(session.services.requestCleanup.has(request), true);
    finish();
    await stopped;
    assert.equal(request.closed, true);
    assert.deepEqual(observed, [false]);
    assert.equal(session.nativeRequests.has(request), false);
    assert.equal(session.services.requestCleanup.has(request), false);
    assert.equal(session.cleanupError, undefined);
  });

test('Cat reports an inactive cleanup callback failure and retires its scope without a timeout',
  async t => {
    const session = await createCatSessionForInternalTest(t, { installationId: 'cleanup-failure',
      source: `
        module.exports.home = () => ({ alive: true });
        module.exports.stop = () => new Promise(() =>
          setImmediate(() => { throw new Error('synthetic cleanup callback failure'); }));
      ` });
    await assert.rejects(session.stop(), { code: 'SCRIPT_EXCEPTION' });
    assert.equal(session.cleanupError, 'SCRIPT_EXCEPTION');
    assert.equal(session.live, false);
    assert.equal(session.root.signal.aborted, true);
    assert.equal(session.timers.size, 0);
    await assert.rejects(session.invoke({ siteId: 'default', operation: 'home', payload: {} }),
      { code: 'STALE_SESSION' });
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
    assert.equal(session.node.resourceCleanup.size, 0);
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
    writeFileSync(sourceFile(root, sharedHash), volumeSource('shared', '// manually edited shared file'));
    await runtime.setEnabled('remove-b', false);
    await runtime.setEnabled('remove-b', true);
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

test('Cat cold disabled removals clean their persisted source and volume references but keep shared and unknown files',
  { timeout: 60000 }, async t => {
    const root = rootFor(t);
    const runtime = createCatRuntime({ dataRoot: root });
    t.after(() => runtime.close());
    const unique = volumeSource('cold-unique'), shared = volumeSource('cold-shared');
    for (const [installationId, source, siteId] of [
      ['cold-remove-unique', unique, 'cold-unique'],
      ['cold-remove-shared', shared, 'cold-shared'],
      ['cold-keep-shared', shared, 'cold-shared'],
    ]) {
      await runtime.install({ installationId, source });
      assert.deepEqual(await runtime.invoke({ installationId, siteId, operation: 'write', payload: {} }),
        { written: true });
      await runtime.setEnabled(installationId, false);
    }
    await runtime.close();
    const unknown = volumeSource('unknown');
    const unknownFile = sourceFile(root, sourceHash(unknown));
    writeFileSync(unknownFile, unknown);
    const unknownVolume = join(root, 'volumes', 'unknown-volume');
    mkdirSync(unknownVolume);
    writeFileSync(join(unknownVolume, 'unknown.txt'), 'leave untouched');

    const reopened = createCatRuntime({ dataRoot: root });
    t.after(() => reopened.close());
    const cold = await reopened.start();
    assert.equal(cold.instances.length, 3);
    for (const instance of cold.instances) {
      assert.equal(instance.state, 'disabled');
      assert.equal(instance.enabled, false);
      assert.equal(reopened.hostState(instance.installationId), null);
      assert.equal(existsSync(join(root, 'volumes', instance.installationId)), true);
    }
    assert.equal(existsSync(sourceFile(root, sourceHash(unique))), true);
    writeSettings(reopened.settings.path, value => ({
      ...value, scripts: value.scripts.filter(script => script.installationId === 'cold-keep-shared'),
    }));
    assert.deepEqual(await reopened.refresh(), { applied: true, failures: [] });
    assert.equal(existsSync(join(root, 'volumes', 'cold-remove-unique')), false);
    assert.equal(existsSync(join(root, 'volumes', 'cold-remove-shared')), false);
    assert.equal(existsSync(sourceFile(root, sourceHash(unique))), false);
    assert.equal(readFileSync(sourceFile(root, sourceHash(shared)), 'utf8'), shared);
    assert.equal(readFileSync(unknownFile, 'utf8'), unknown);
    assert.equal(readFileSync(join(unknownVolume, 'unknown.txt'), 'utf8'), 'leave untouched');
    assert.deepEqual((await reopened.status()).instances.map(instance =>
      [instance.installationId, instance.state]), [['cold-keep-shared', 'disabled']]);
    assert.equal(existsSync(join(root, 'volumes', 'cold-keep-shared')), true);
    await reopened.setEnabled('cold-keep-shared', true);
    assert.deepEqual(await reopened.invoke({ installationId: 'cold-keep-shared',
      siteId: 'cold-shared', operation: 'read', payload: {} }), { value: 'cold-shared' });
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

test('Cat installs credentialed site APIs and sends URL userinfo as Basic authentication',
  { timeout: 30000 }, async t => {
    const runtime = createCatRuntime({ dataRoot: rootFor(t) });
    t.after(() => runtime.close());
    await runtime.install({
      installationId: 'credentialed-api', config: {}, version: '1', enabled: true, source: String.raw`
        const http = require('http');
        let server;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/full-config' || request.url === '/config') {
              response.end(JSON.stringify({ video: { sites: [{
                key: 'credentialed', name: 'Credentialed',
                api: 'http://reader:synthetic-only@127.0.0.1/api/credentialed', actions: ['home'],
              }] } }));
              return;
            }
            if (request.url === '/api/credentialed/home') {
              response.end(JSON.stringify({ path: request.url, method: request.method,
                authorization: request.headers.authorization }));
              return;
            }
            response.statusCode = 404;
            response.end();
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
      `,
    });
    assert.equal((await runtime.status()).instances[0].state, 'ready');
    assert.deepEqual(await runtime.invoke({
      installationId: 'credentialed-api', siteId: 'credentialed', operation: 'home', payload: {},
    }), {
      path: '/api/credentialed/home', method: 'POST',
      authorization: 'Basic ' + Buffer.from('reader:synthetic-only').toString('base64'),
    });
  });

test('Cat preserves unregistered HTTPS site APIs without aliasing the HTTP listener',
  { timeout: 30000 }, async t => {
    const api = 'https://127.0.0.1/api/unmapped';
    const session = await createCatSessionForInternalTest(t, {
      installationId: 'unmapped-https', source: `
        const http = require('http');
        let server;
        module.exports.start = () => {
          server = http.createServer((request, response) => {
            response.setHeader('content-type', 'application/json');
            if (request.url === '/full-config' || request.url === '/config') {
              response.end(JSON.stringify({ video: { sites: [{
                key: 'unmapped', name: 'Unmapped', api: ${JSON.stringify(api)}, actions: ['home'],
              }] } }));
              return;
            }
            response.statusCode = 404;
            response.end();
          });
          server.listen(Number(process.env.PORT), '127.0.0.1');
        };
        module.exports.stop = () => { server?.closeAllConnections(); server?.close(); };
      `,
    });
    assert.equal(session.sites.get('unmapped').api, api);
    assert.equal(session.services.listener(443), null);
    assert.equal(session.services.aliases.has(443), false);
    assert.match(session.endpoint().origin, /^http:\/\/127\.0\.0\.1:\d+$/);
    const calls = [];
    t.mock.method(session, 'read', async (url, payload, _scope, method) => {
      calls.push({ url, payload, method });
      return { reached: true };
    });
    assert.deepEqual(await session.invoke({
      siteId: 'unmapped', operation: 'home', payload: { page: 2 },
    }), { reached: true });
    assert.deepEqual(calls, [{ url: api + '/home', payload: { page: 2 }, method: 'POST' }]);
  });

test('Cat rejects query and fragment site APIs that cannot append an operation', { timeout: 30000 }, async t => {
  const invalid = [
    'http://127.0.0.1/api/invalid?route=1',
    'http://127.0.0.1/api/invalid#route',
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

test('Cat restores a hard-linked settings authority and atomically saves its own file', { timeout: 30000 }, async t => {
  const root = rootFor(t);
  const runtime = await installed(t, root);
  const settings = (await runtime.status()).settingsPath;
  await runtime.close();
  renameSync(settings, settings + '.original');
  linkSync(settings + '.original', settings);
  const reopened = createCatRuntime({ dataRoot: root });
  t.after(() => reopened.close());
  await reopened.start();
  await waitForState(reopened, 'fixture-a', 'ready');
  await reopened.setEnabled('fixture-a', false);
  assert.equal(JSON.parse(readFileSync(settings, 'utf8')).scripts[0].enabled, false);
  assert.equal(JSON.parse(readFileSync(settings + '.original', 'utf8')).scripts[0].enabled, true);
});

test('Cat restores hard-linked source files and uninstalls only its own reference', { timeout: 30000 }, async t => {
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
  await reopened.start();
  await waitForState(reopened, 'fixture-a', 'ready');
  assert.ok(reopened.server.listening);
  await reopened.uninstall('fixture-a');
  assert.equal(existsSync(path), false);
  assert.equal(readFileSync(path + '.original', 'utf8'), source);
});

test('Cat recreates a missing live source directory for a new installation', { timeout: 30000 }, async t => {
  const root = rootFor(t);
  const runtime = await installed(t, root);
  rmSync(join(root, 'sources'), { recursive: true, force: true });
  const item = await runtime.install({
    installationId: 'fixture-next', source, config: { id: 'site-next', protocol: 'old' },
    version: '1', enabled: false,
  });
  assert.equal(item.installationId, 'fixture-next');
  assert.equal(readFileSync(join(root, 'sources', item.sourceHash + '.js'), 'utf8'), source);
});

test('Cat starts and restores scripts through App root and source directory aliases', { timeout: 30000 }, async t => {
  const base = rootFor(t), actual = join(base, 'actual'), alias = join(base, 'alias');
  mkdirSync(actual);
  symlinkSync(actual, alias, process.platform === 'win32' ? 'junction' : 'dir');
  const root = alias;
  const sourceRoot = join(base, 'source-files');
  mkdirSync(sourceRoot);
  symlinkSync(sourceRoot, join(root, 'sources'), process.platform === 'win32' ? 'junction' : 'dir');
  const runtime = await installed(t, root);
  assert.equal((await runtime.status()).settingsPath, join(root, 'settings.json'));
  await runtime.close();
  const reopened = createCatRuntime({ dataRoot: root });
  t.after(() => reopened.close());
  await reopened.start();
  await waitForState(reopened, 'fixture-a', 'ready');
  await reopened.uninstall('fixture-a');
  assert.equal(existsSync(join(root, 'volumes', 'fixture-a')), false);
  assert.equal(fs.readdirSync(sourceRoot).length, 0);
});

test('Cat rejects malformed settings and a non-file source without replacing them', { timeout: 30000 }, async t => {
  const root = rootFor(t), runtime = await installed(t, root);
  const settings = (await runtime.status()).settingsPath;
  const original = readFileSync(settings, 'utf8');
  const sourceHash = JSON.parse(original).scripts[0].sourceHash;
  await runtime.close();
  writeFileSync(settings, '{');
  const corrupt = createCatRuntime({ dataRoot: root });
  t.after(() => corrupt.close());
  await assert.rejects(corrupt.start(), { code: 'CORRUPT_STORAGE' });
  assert.equal(readFileSync(settings, 'utf8'), '{');
  writeFileSync(settings, original);
  const path = join(root, 'sources', sourceHash + '.js');
  rmSync(path);
  mkdirSync(path);
  const reopened = createCatRuntime({ dataRoot: root });
  t.after(() => reopened.close());
  await reopened.start();
  await waitForState(reopened, 'fixture-a', 'failed');
  assert.equal(reopened.instance('fixture-a').error, 'CORRUPT_STORAGE');
  assert.equal(fs.statSync(path).isDirectory(), true);
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
