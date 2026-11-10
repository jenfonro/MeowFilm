import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { syncBuiltinESMExports } from 'node:module';
import { createHash, randomBytes } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Readable } from 'node:stream';
import { gzipSync, deflateSync, brotliCompressSync } from 'node:zlib';
import { setTimeout as delay } from 'node:timers/promises';
import { createCatRuntime } from '../src/cat-runtime.js';
import { compileDASHTemplate, rewriteDASH } from '../src/media-manifest.js';
import { fault } from '../src/errors.js';
import { serverFor } from './http-fixture.js';

const budget = 8 * 1024 * 1024;
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
function gate() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
}
async function within(promise, label, ms = 5000) {
  let timer;
  try {
    return await Promise.race([promise, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(label)), ms);
    })]);
  } finally { clearTimeout(timer); }
}
async function until(predicate, label) {
  const deadline = Date.now() + 5000;
  while (!predicate()) {
    assert.ok(Date.now() < deadline, label);
    await delay(5);
  }
}
function read(url, options = {}) {
  return new Promise((resolve, reject) => {
    const client = http.request(url, { agent: false, ...options }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.once('error', reject);
      response.once('end', () => resolve({ status: response.statusCode, headers: response.headers,
        bytes: Buffer.concat(chunks), text: Buffer.concat(chunks).toString() }));
    });
    client.once('error', reject);
    client.end();
  });
}
function open(t, url) {
  const ready = gate(), closed = gate();
  const client = http.get(url, { agent: false }, response => {
    response.on('error', () => {});
    response.once('close', closed.resolve);
    ready.resolve(response);
  });
  client.on('error', () => {});
  client.once('close', closed.resolve);
  t.after(() => client.destroy());
  return { client, ready: ready.promise, closed: closed.promise };
}

// Transport tests use real HTTP and a controlled in-memory session. The module
// tests below additionally exercise real Worker publication and revocation.
async function proxyFor(t, handler, limits = {}) {
  const upstream = await serverFor(t, handler);
  const root = mkdtempSync(join(tmpdir(), 'meow-cat-media-'));
  const runtime = createCatRuntime({ dataRoot: root,
    limits: { requestTimeoutMs: 2000, cleanupTimeoutMs: 500, ...limits } });
  t.after(async () => {
    try { if (!runtime.closed) await runtime.close(); }
    finally { rmSync(root, { recursive: true, force: true }); }
  });
  await runtime.start();
  const addOwner = installationId => {
    const session = { installationId, generation: 1, live: true,
      stopped: false, worker: {}, proxyLifecycle: new AbortController(),
      limits: { cleanupTimeoutMs: 500 }, adapterTasks: new Set(),
      async stop() {
        this.live = false; this.stopped = true; this.worker = null;
        this.proxyLifecycle.abort(fault('STALE_SESSION'));
      },
      async drainAdapters() {},
    };
    runtime.workers.add(session);
    const proxyId = randomBytes(24).toString('hex');
    runtime.sessions.set(installationId, session);
    runtime.snapshots.set(installationId, { aggregatePort: runtime.aggregatePort,
      endpoints: [{ proxyId, origin: upstream.socketOrigin }] });
    runtime.registerProxyMappings(session, [{ id: proxyId, origin: upstream.socketOrigin }]);
    const base = `http://127.0.0.1:${runtime.aggregatePort}/proxy/${installationId}/1/${proxyId}`;
    return { session, url: path => base + path };
  };
  return { runtime, upstream, addOwner, ...addOwner('media') };
}

test('Cat media binary body outlives the finite headers deadline; headers and control reads still time out',
  { timeout: 10000 }, async t => {
    const fixture = await proxyFor(t, (request, response) => {
      if (request.url === '/no-headers') return;
      if (request.url === '/slow.m3u8') {
        response.writeHead(200, { 'content-type': 'application/vnd.apple.mpegurl' });
        response.write('#EXTM3U\n');
        return;
      }
      response.writeHead(200, { 'content-type': 'video/mp4' });
      response.write('start');
      const timer = setTimeout(() => response.end('-complete'), 350);
      response.once('close', () => clearTimeout(timer));
    }, { requestTimeoutMs: 75 });
    assert.equal((await read(fixture.url('/long'))).text, 'start-complete');
    for (const path of ['/no-headers', '/slow.m3u8']) {
      const timedOut = await read(fixture.url(path));
      assert.equal(timedOut.status, 502, path);
      assert.deepEqual(JSON.parse(timedOut.text), { error: { code: 'TIMEOUT' } });
    }
  });

test('Cat media rejects empty or malformed advertised manifests before publishing success',
  { timeout: 10000 }, async t => {
    const reads = [];
    const fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      response.setHeader('content-type', request.url.endsWith('.mpd')
        ? 'application/dash+xml' : 'application/vnd.apple.mpegurl');
      response.end(request.url.startsWith('/empty') ? '' : 'not-a-manifest');
    });
    const paths = ['/empty.m3u8', '/invalid.m3u8', '/empty.mpd', '/invalid.mpd'];
    for (const path of paths) {
      const result = await read(fixture.url(path));
      assert.equal(result.status, 502, path);
      assert.deepEqual(JSON.parse(result.text), { error: { code: 'INVALID_RESPONSE' } });
    }
    assert.deepEqual(reads, paths);
  });

test('Cat media validates original UTF-8 before rewriting HLS/DASH and preserves split valid code points without ICU',
  { timeout: 10000 }, async t => {
    const Decoder = globalThis.TextDecoder;
    t.mock.method(globalThis, 'TextDecoder', function(label, options) {
      if (options?.fatal) throw new TypeError('fatal decoding requires ICU');
      return new Decoder(label, options);
    });
    const title = '正片é\uFEFF\uFFFD🎬';
    const documents = [
      { path: '/manifest.m3u8', type: 'application/vnd.apple.mpegurl',
        text: `#EXTM3U\n#EXTINF:3,${title}\nsegment.m4s\n#EXT-X-ENDLIST\n` },
      { path: '/manifest.mpd', type: 'application/dash+xml',
        text: '<MPD><ProgramInformation><Title>' + title + '</Title></ProgramInformation><Period>' +
          '<AdaptationSet><Representation id="v"><BaseURL>movie.mp4</BaseURL></Representation></AdaptationSet></Period></MPD>' },
    ];
    for (const document of documents) {
      await t.test(document.path, async t => {
        const reads = [];
        let body, compressed = false;
        const fixture = await proxyFor(t, (request, response) => {
          reads.push(request.url);
          response.writeHead(200, { 'content-type': document.type,
            ...(compressed ? { 'content-encoding': 'gzip' } : {}) });
          const wire = compressed ? gzipSync(body) : body;
          let offset = 0;
          const write = () => {
            if (response.destroyed) return;
            if (offset === wire.length) { response.end(); return; }
            response.write(wire.subarray(offset, ++offset));
            setImmediate(write);
          };
          write();
        });
        for (compressed of [false, true]) {
          for (const bom of ['', '\uFEFF']) {
            body = Buffer.from(bom + document.text);
            const result = await read(fixture.url(document.path));
            assert.equal(result.status, 200);
            assert.equal(result.text.startsWith('\uFEFF'), false);
            assert.ok(result.text.includes(title));
          }
          for (const invalid of [[0x80], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xe2, 0x82]]) {
            body = Buffer.concat([Buffer.from('\uFEFF' + document.text), Buffer.from(invalid)]);
            const result = await read(fixture.url(document.path));
            assert.equal(result.status, 502);
            assert.deepEqual(JSON.parse(result.text), { error: { code: 'INVALID_RESPONSE' } });
          }
        }
        assert.equal(reads.length, 14);
        assert.ok(reads.every(path => path === document.path), 'rewriting never reads a child resource');
      });
    }
  });

test('Cat media binary backpressure and disconnect do not buffer a 32MiB body',
  { timeout: 10000 }, async t => {
    const size = 32 * 1024 * 1024, chunk = Buffer.alloc(64 * 1024, 0x51);
    let produced = 0, completed = false;
    const aborted = gate();
    const fixture = await proxyFor(t, (_request, response) => {
      produced = 0; completed = false;
      const input = new Readable({ read() {
        while (produced < size) {
          produced += chunk.length;
          if (!this.push(chunk)) return;
        }
        this.push(null);
      } });
      response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': size });
      response.once('finish', () => { completed = true; });
      response.once('close', () => { input.destroy(); if (!completed) aborted.resolve(); });
      input.pipe(response);
    });
    const first = open(t, fixture.url('/video'));
    const response = await first.ready;
    response.pause();
    await delay(80);
    assert.ok(produced > 0 && produced < size, 'paused native reader must stop upstream production');
    assert.equal(completed, false);
    const chunks = [];
    response.on('data', chunk => chunks.push(chunk));
    const finished = once(response, 'end');
    response.resume();
    await finished;
    const bytes = Buffer.concat(chunks);
    assert.equal(bytes.length, size);
    assert.equal(digest(bytes), digest(Buffer.alloc(size, 0x51)));
    const second = open(t, fixture.url('/video'));
    const partial = await second.ready;
    await once(partial, 'data');
    second.client.destroy();
    await within(aborted.promise, 'disconnect did not cancel the actual upstream');
    await until(() => fixture.runtime.proxyTasks.size === 0, 'proxy tasks did not retire');
  });

test('Cat media preserves HEAD, If-Range, 206, 416, caller cookies and hop-header semantics',
  { timeout: 10000 }, async t => {
    const requests = [];
    const fixture = await proxyFor(t, (request, response) => {
      requests.push({ method: request.method, headers: request.headers });
      const body = request.headers.range === 'bytes=2-4' ? '234' : '0123456789';
      if (request.headers.range === 'bytes=90-') {
        response.writeHead(416, { 'content-range': 'bytes */10' });
        return response.end();
      }
      response.writeHead(request.headers.range ? 206 : 200, {
        'content-type': 'application/octet-stream', 'content-length': Buffer.byteLength(body),
        ...(request.headers.range ? { 'content-range': 'bytes 2-4/10' } : {}),
        connection: 'x-hop', 'x-hop': 'must-not-leak',
        'set-cookie': ['__meow_internal=secret', 'media=kept'],
      });
      response.end(body);
    });
    const ranged = await read(fixture.url('/video'), { headers: {
      range: 'bytes=2-4', 'if-range': '"source-etag"', cookie: '__meow_private=blocked; media=ok',
      connection: 'x-remove', 'x-remove': 'blocked', 'x-meow-runtime-access': 'blocked',
      'x-meow-request-scope': 'blocked',
    } });
    assert.equal(ranged.status, 206);
    assert.equal(ranged.text, '234');
    assert.equal(ranged.headers['content-range'], 'bytes 2-4/10');
    assert.equal(ranged.headers['x-hop'], undefined);
    assert.deepEqual(ranged.headers['set-cookie'], ['__meow_internal=secret', 'media=kept']);
    assert.equal(requests[0].headers['if-range'], '"source-etag"');
    assert.equal(requests[0].headers.cookie, '__meow_private=blocked; media=ok');
    assert.equal(requests[0].headers['x-meow-runtime-access'], 'blocked');
    for (const header of ['x-remove', 'x-meow-request-scope'])
      assert.equal(requests[0].headers[header], undefined);
    assert.equal(requests[0].headers['x-meow-script-access'], undefined);
    const head = await read(fixture.url('/video'), { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(head.bytes.length, 0);
    assert.equal(head.headers['content-length'], '10');
    assert.equal(requests[1].method, 'HEAD');
    const unsatisfied = await read(fixture.url('/video'), { headers: { range: 'bytes=90-' } });
    assert.equal(unsatisfied.status, 416);
    assert.equal(unsatisfied.headers['content-range'], 'bytes */10');
    const unsupported = await read(fixture.url('/video'), { method: 'POST' });
    assert.equal(unsupported.status, 405);
    assert.equal(unsupported.headers.allow, 'GET, HEAD, OPTIONS');
    assert.equal(requests.length, 3);
  });

test('Cat media enforces the 8MiB raw playlist budget before success headers, not a video length limit',
  { timeout: 15000 }, async t => {
    const signature = Buffer.from('#EXTM3U\n');
    const exact = Buffer.concat([signature, Buffer.alloc(budget - signature.length, 0x23)]);
    const utf8 = Buffer.concat([signature, Buffer.from('#猫'), Buffer.alloc(budget - signature.length - 3, 0x23)]);
    assert.equal(utf8.length, budget + 1);
    const fixture = await proxyFor(t, (request, response) => {
      response.setHeader('content-type', 'application/vnd.apple.mpegurl');
      if (request.url === '/declared.m3u8') {
        response.writeHead(200, { 'content-length': budget + 1 });
        response.flushHeaders();
        return;
      }
      if (request.url === '/exact.m3u8') return response.end(exact);
      if (request.url === '/binary') {
        response.setHeader('content-type', 'video/mp4');
        return response.end(Buffer.alloc(budget + 1024, 0x42));
      }
      if (request.url === '/oversize-ascii.m3u8') {
        response.write(exact);
        return response.end('#');
      }
      // No Content-Length or final newline; split inside the UTF-8 code point.
      const split = signature.length + 2;
      response.write(utf8.subarray(0, split));
      response.write(utf8.subarray(split, budget - 1));
      response.end(utf8.subarray(budget - 1));
    });
    const accepted = await read(fixture.url('/exact.m3u8'));
    assert.equal(accepted.status, 200);
    assert.equal(accepted.bytes.length, budget);
    assert.equal(digest(accepted.bytes), digest(exact));
    for (const path of ['/oversize.m3u8', '/oversize-ascii.m3u8', '/declared.m3u8']) {
      const rejected = await read(fixture.url(path));
      assert.equal(rejected.status, 413, path);
      assert.deepEqual(JSON.parse(rejected.text), { error: { code: 'RESOURCE_LIMIT' } });
    }
    const binary = await read(fixture.url('/binary'));
    assert.equal(binary.status, 200);
    assert.equal(binary.bytes.length, budget + 1024);
    assert.equal(digest(binary.bytes), digest(Buffer.alloc(budget + 1024, 0x42)));
  });

test('Cat media bounded HLS and DASH rewriting retains queries, references and ranged playlist bypass',
  { timeout: 10000 }, async t => {
    const hls = '#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="key"\nsegment.ts?part=1\n';
    const dash = '<MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><BaseURL>video/</BaseURL><Period><AdaptationSet>' +
      '<Representation id="list"><SegmentList><Initialization sourceURL="init"/><SegmentURL media="segment"/>' +
      '</SegmentList></Representation><Representation id="template"><SegmentTemplate media="v-$Number$.m4s" ' +
      'initialization="start.mp4"/></Representation></AdaptationSet></Period></MPD>';
    const reads = [], bytes = Buffer.from([1, 3, 5, 7]);
    const fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      if (request.url.startsWith('/video/')) {
        response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': bytes.length });
        return response.end(bytes);
      }
      const xml = request.url === '/manifest.mpd';
      const text = request.headers.range ? '#EXTM3U' : xml ? dash : hls;
      response.writeHead(request.headers.range ? 206 : 200, {
        'content-type': xml ? 'application/dash+xml' : 'application/vnd.apple.mpegurl',
        'content-length': Buffer.byteLength(text), etag: '"original"',
        ...(request.headers.range ? { 'content-range': 'bytes 0-6/100' } : {}),
      });
      response.end(text);
    });
    const playlist = await read(fixture.url('/index.m3u8'));
    assert.equal(playlist.headers.etag, undefined);
    assert.equal(playlist.text, '#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="' +
      fixture.url('/key') + '"\n' + fixture.url('/segment.ts?part=1') + '\n');
    const mpd = await read(fixture.url('/manifest.mpd'));
    assert.equal(mpd.status, 200);
    const representations = [...mpd.text.matchAll(/<Representation\b[^>]*>([^]*?)<\/Representation>/g)];
    assert.equal(representations.length, 2);
    for (const [index, representation] of representations.entries()) {
      const base = /<BaseURL>([^<]+)<\/BaseURL>/.exec(representation[1])[1];
      const references = index === 0
        ? [...representation[1].matchAll(/(?:sourceURL|media)="([^"]+)"/g)].map(match => match[1])
        : [...representation[1].matchAll(/(?:initialization|media)="([^"]+)"/g)]
          .map(match => match[1].replace('$Number$', '2'));
      for (const reference of references) {
        assert.deepEqual((await read(new URL(reference, base))).bytes, bytes);
      }
    }
    assert.deepEqual(reads, ['/index.m3u8', '/manifest.mpd', '/video/init', '/video/segment',
      '/video/v-2.m4s', '/video/start.mp4']);
    assert.equal((await read(fixture.url('/manifest.mpd'))).text, mpd.text,
      'an unchanged manifest must retain stable base routing');
    const base = /<BaseURL>([^<]+)<\/BaseURL>/.exec(mpd.text)[1];
    for (const suffix of ['unregistered', 'arbitrary/sub?url=/config&token=x']) {
      assert.deepEqual((await read(new URL(suffix, base))).bytes, bytes);
      assert.equal(reads.at(-1), '/video/' + suffix);
    }
    const ranged = await read(fixture.url('/index.m3u8'), { headers: { range: 'bytes=0-6' } });
    assert.equal(ranged.status, 206);
    assert.equal(ranged.text, '#EXTM3U');
    assert.equal(ranged.headers.etag, '"original"');
  });

test('Cat media HLS free titles stay opaque while real URI attributes and media lines stay scoped',
  { timeout: 10000 }, async t => {
    const title = '  #EXTINF:3,URI="/config",media="/admin",sourceURL=\'/full-config\'  ';
    const text = '#EXTM3U\n#EXT-X-TARGETDURATION:3\n' +
      '#EXT-X-KEY:METHOD=AES-128,URI="key?token=a,b"\n' +
      '#EXT-X-CUSTOM:LABEL="URI=/config,media=/admin",URI="extra"\n' + title +
      '\nsegment.ts\n#EXT-X-ENDLIST\n';
    const reads = [], bytes = Buffer.from([2, 4, 6, 8]);
    const fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      if (request.url === '/list.m3u8') {
        response.writeHead(200, { 'content-type': 'application/vnd.apple.mpegurl' });
        return response.end(text);
      }
      assert.ok(['/key?token=a,b', '/extra', '/segment.ts'].includes(request.url),
        'free title or attribute text must not become a management request');
      response.end(bytes);
    });
    const playlist = await read(fixture.url('/list.m3u8'));
    assert.equal(playlist.status, 200);
    assert.ok(playlist.text.split('\n').includes(title));
    assert.ok(playlist.text.includes('LABEL="URI=/config,media=/admin"'));
    assert.deepEqual(reads, ['/list.m3u8'], 'rewriting cannot issue child/management requests');
    const references = [...playlist.text.matchAll(/(?:^#EXT-X-[^\n]*?[:,])URI="([^"]+)"/gm)]
      .map(match => match[1]);
    references.push(playlist.text.split('\n').find(line => line && !line.trim().startsWith('#')));
    assert.equal(references.length, 3);
    for (const reference of references) {
      assert.ok(reference.startsWith(fixture.url('/')));
      assert.deepEqual((await read(reference)).bytes, bytes);
    }
    assert.deepEqual(reads, ['/list.m3u8', '/key?token=a,b', '/extra', '/segment.ts']);
  });

test('Cat media HLS and DASH content-steering URLs are ordinary references, not access-control failures',
  { timeout: 10000 }, async t => {
    const reads = [], steering = '{"VERSION":1,"TTL":30,"PATHWAY-PRIORITY":["one"]}';
    const fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      if (request.url === '/hls') {
        response.setHeader('content-type', 'text/plain');
        return response.end('#EXTM3U\n#EXT-X-CONTENT-STEERING:SERVER-URI="/steering?sig=a%2Fb",PATHWAY-ID="one"\n' +
          '#EXT-X-STREAM-INF:BANDWIDTH=1\nchild.m3u8\n');
      }
      if (request.url === '/dash') {
        response.setHeader('content-type', 'application/dash+xml');
        return response.end('<MPD><ContentSteering xml:base="/meta/">../steering?sig=a%2Fb</ContentSteering>' +
          '<Period><AdaptationSet><Representation id="one"><SegmentTemplate media="seg-$Number$.m4s"/>' +
          '</Representation></AdaptationSet></Period></MPD>');
      }
      assert.equal(request.url, '/steering?sig=a%2Fb');
      response.setHeader('content-type', 'application/json');
      response.end(steering);
    });
    const hls = await read(fixture.url('/hls')), dash = await read(fixture.url('/dash'));
    assert.equal(hls.status, 200);
    assert.equal(dash.status, 200);
    assert.deepEqual(reads, ['/hls', '/dash'], 'steering declarations do not trigger prefetch');
    const hlsURL = /SERVER-URI="([^"]+)"/.exec(hls.text)[1];
    const dashURL = /<ContentSteering>([^<]+)<\/ContentSteering>/.exec(dash.text)[1];
    for (const url of [hlsURL, dashURL]) {
      assert.equal(url, fixture.url('/steering?sig=a%2Fb'));
      assert.equal((await read(url)).text, steering);
    }
  });

test('Cat media DASH below the document budget is not rejected by a retained resource authorization graph',
  { timeout: 20000 }, async t => {
    const representation = index => `<Representation id="v${index}"><BaseURL>assets/${index}/</BaseURL>` +
      '<SegmentTemplate initialization="init.mp4" media="seg-$Number$.m4s"/></Representation>';
    const document = body => '<MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period><AdaptationSet>' +
      body + '</AdaptationSet></Period></MPD>';
    const large = document(Array.from({ length: 10000 }, (_, index) => representation(index)).join(''));
    assert.ok(Buffer.byteLength(large) < budget);
    const reads = [];
    const fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      if (request.url === '/limit.mpd' || request.url === '/valid.mpd') {
        response.writeHead(200, { 'content-type': 'application/dash+xml' });
        return response.end(request.url === '/limit.mpd' ? large : document(representation('valid')));
      }
      assert.ok(['/assets/0/init.mp4', '/assets/9999/init.mp4', '/assets/valid/init.mp4'].includes(request.url));
      response.end('init-bytes');
    });
    const acceptedLarge = await read(fixture.url('/limit.mpd'));
    assert.equal(acceptedLarge.status, 200);
    const representations = [...acceptedLarge.text.matchAll(/<Representation\b[^>]*>([^]*?)<\/Representation>/g)];
    assert.equal(representations.length, 10000);
    assert.deepEqual(reads, ['/limit.mpd'], 'manifest rewriting cannot prefetch resources');
    for (const index of [0, 9999]) {
      const base = /<BaseURL>([^<]+)<\/BaseURL>/.exec(representations[index][1])[1];
      const init = /initialization="([^"]+)"/.exec(representations[index][1])[1];
      assert.equal((await read(new URL(init, base))).text, 'init-bytes');
    }
    const accepted = await read(fixture.url('/valid.mpd'));
    assert.equal(accepted.status, 200);
    const base = /<BaseURL>([^<]+)<\/BaseURL>/.exec(accepted.text)[1];
    const init = /initialization="([^"]+)"/.exec(accepted.text)[1];
    assert.equal((await read(new URL(init, base))).text, 'init-bytes');
  });

test('Cat media DASH external bases resolve references and arbitrary suffixes with only caller credentials',
  { timeout: 10000 }, async t => {
    const reads = [];
    const external = await serverFor(t, (request, response) => {
      reads.push({ url: request.url, access: request.headers['x-meow-script-access'],
        authorization: request.headers.authorization, cookie: request.headers.cookie });
      response.writeHead(200, { 'content-type': 'video/mp4' });
      response.end('public-media');
    });
    const fixture = await proxyFor(t, (_request, response) => {
      response.writeHead(200, { 'content-type': 'application/dash+xml' });
      response.end('<MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period><AdaptationSet>' +
        `<Representation id="public"><BaseURL>${external.socketOrigin}/assets/</BaseURL>` +
        '<SegmentTemplate initialization="init.mp4?key=1" media="seg-$Number$.m4s?part=2"/>' +
        '</Representation></AdaptationSet></Period></MPD>');
    });
    const mpd = await read(fixture.url('/manifest.mpd'));
    assert.equal(mpd.status, 200);
    assert.deepEqual(reads, [], 'mapping public references cannot prefetch them');
    const base = /<BaseURL>([^<]+)<\/BaseURL>/.exec(mpd.text)[1];
    const initialization = /initialization="([^"]+)"/.exec(mpd.text)[1];
    const media = /\bmedia="([^"]+)"/.exec(mpd.text)[1].replace('$Number$', '3');
    for (const [reference, expected] of [[initialization, '/assets/init.mp4?key=1'], [media, '/assets/seg-3.m4s?part=2']]) {
      const child = await read(new URL(reference, base));
      assert.equal(child.status, 200);
      assert.equal(child.text, 'public-media');
      assert.equal(child.headers['x-meow-script-access'], undefined);
      assert.equal(reads.at(-1).url, expected);
    }
    for (const suffix of ['extra?url=http://synthetic.invalid/config', 'extra/sub']) {
      const child = await read(new URL(suffix, base), { headers: {
        authorization: 'explicit-media-input', cookie: 'media=explicit-input',
      } });
      assert.equal(child.status, 200);
      assert.equal(child.text, 'public-media');
      assert.equal(reads.at(-1).url, '/assets/' + suffix);
    }
    assert.deepEqual(reads.map(item => item.url), ['/assets/init.mp4?key=1', '/assets/seg-3.m4s?part=2',
      '/assets/extra?url=http://synthetic.invalid/config', '/assets/extra/sub']);
    for (const item of reads) {
      assert.equal(item.access, undefined);
      assert.equal(item.authorization, item.url.startsWith('/assets/extra') ? 'explicit-media-input' : undefined);
      assert.equal(item.cookie, item.url.startsWith('/assets/extra') ? 'media=explicit-input' : undefined);
    }
  });

test('Cat media HLS imported and query variables use the original child base with unrestricted query forwarding',
  { timeout: 10000 }, async t => {
    const reads = [], bytes = Buffer.from([7, 14, 21]);
    const fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      assert.equal(request.headers['x-meow-script-access'], undefined);
      if (request.url === '/master.m3u8') {
        response.writeHead(200, { 'content-type': 'application/vnd.apple.mpegurl' });
        return response.end('#EXTM3U\n#EXT-X-DEFINE:NAME="root",VALUE="/assets"\n' +
          '#EXT-X-STREAM-INF:BANDWIDTH=1\nchild.m3u8?token=9&opaque=a%20b%2Fc~\n');
      }
      if (request.url.startsWith('/child.m3u8?')) {
        response.writeHead(200, { 'content-type': 'application/vnd.apple.mpegurl' });
        return response.end('#EXTM3U\n#EXT-X-DEFINE:IMPORT="root"\n#EXT-X-DEFINE:QUERYPARAM="token"\n' +
          '#EXT-X-MAP:URI="{$root}/init.mp4?token={$token}&sig=a%2Fb"\n' +
          '#EXTINF:1,\n{$root}/seg-{$token}.ts?sig=a%2Fb\n');
      }
      assert.ok(['/assets/init.mp4?token=9&sig=a%2Fb', '/assets/seg-9.ts?sig=a%2Fb',
        '/nested/child?query=a%2Fb'].includes(request.url));
      response.end(bytes);
    });
    const master = await read(fixture.url('/master.m3u8'));
    assert.equal(master.status, 200);
    const childURL = master.text.split('\n').find(line => line && !line.startsWith('#'));
    const child = await read(childURL + '&_HLS_msn=3&_HLS_part=1&_HLS_skip=YES');
    assert.equal(child.status, 200);
    assert.doesNotMatch(child.text, /\{\$/);
    const init = /\bURI="([^"]+)"/.exec(child.text)[1];
    const segment = child.text.split('\n').find(line => line && !line.startsWith('#'));
    assert.deepEqual((await read(init)).bytes, bytes);
    assert.deepEqual((await read(segment)).bytes, bytes);
    assert.deepEqual(reads, ['/master.m3u8', '/child.m3u8?token=9&opaque=a%20b%2Fc~&_HLS_msn=3&_HLS_part=1&_HLS_skip=YES',
      '/assets/init.mp4?token=9&sig=a%2Fb', '/assets/seg-9.ts?sig=a%2Fb']);
    for (const query of ['url=/config', '_HLS_msn=/config', '_HLS_msn=1&_HLS_msn=2', '_HLS_skip=NO']) {
      assert.equal((await read(childURL + '&' + query)).status, 200);
      assert.equal(reads.at(-1), '/child.m3u8?token=9&opaque=a%20b%2Fc~&' + query);
    }
    assert.deepEqual((await read(new URL('nested/child?query=a%2Fb', childURL))).bytes, bytes);
    assert.equal(reads.at(-1), '/nested/child?query=a%2Fb');
    assert.equal(reads.length, 9, 'query values cannot trigger extra child or management requests');
  });

test('Cat media HLS variables resolve root and second-origin addresses before URI parsing, including redirected IMPORT',
  { timeout: 10000 }, async t => {
    const reads = [], bytes = Buffer.from([11, 22, 33]);
    const external = await serverFor(t, (request, response) => {
      reads.push({ url: request.url, access: request.headers['x-meow-script-access'] });
      if (request.url === '/relay?token=9') {
        response.writeHead(302, { location: '/child?token=9&sig=a%2Fb' });
        return response.end();
      }
      if (request.url === '/child?token=9&sig=a%2Fb') {
        response.setHeader('content-type', 'text/plain');
        return response.end('#EXTM3U\n#EXT-X-DEFINE:IMPORT="root"\n#EXT-X-DEFINE:IMPORT="absolute"\n' +
          '#EXT-X-DEFINE:QUERYPARAM="token"\n#EXT-X-MAP:URI="{$root}/init?token={$token}&sig=a%2Fb"\n' +
          '#EXT-X-PART:URI="{$absolute}/part?token={$token}"\n' +
          '{$root}/segment?token={$token}\n{$absolute}/segment?token={$token}\n');
      }
      response.end(bytes);
    });
    let masterReads = 0;
    const fixture = await proxyFor(t, (request, response) => {
      masterReads++;
      assert.equal(request.url, '/master');
      response.setHeader('content-type', 'text/plain');
      response.end('#EXTM3U\n#EXT-X-DEFINE:NAME="root",VALUE="/assets"\n' +
        `#EXT-X-DEFINE:NAME="absolute",VALUE="${external.socketOrigin}/global"\n` +
        '#EXT-X-STREAM-INF:BANDWIDTH=1\n' + external.socketOrigin + '/relay?token=9\n');
    });
    const master = await read(fixture.url('/master'));
    assert.equal(master.status, 200);
    assert.equal(masterReads, 1);
    assert.deepEqual(reads, [], 'rewriting cannot fetch the second origin');
    const relay = await read(master.text.split('\n').find(line => line && !line.startsWith('#')));
    assert.equal(relay.status, 302);
    const child = await read(relay.headers.location);
    assert.equal(child.status, 200);
    assert.doesNotMatch(child.text, /\{\$/);
    const references = [...child.text.matchAll(/\bURI="([^"]+)"/g)].map(match => match[1]);
    references.push(...child.text.split('\n').filter(line => line && !line.startsWith('#')));
    const paths = ['/assets/init?token=9&sig=a%2Fb', '/global/part?token=9',
      '/assets/segment?token=9', '/global/segment?token=9'];
    assert.deepEqual(references, paths.map(path => external.socketOrigin + path));
    for (const reference of references) assert.deepEqual((await read(reference)).bytes, bytes);
    assert.deepEqual(reads.map(item => item.url), ['/relay?token=9', '/child?token=9&sig=a%2Fb', ...paths]);
    assert.ok(reads.every(item => item.access === undefined), 'no Worker credential belongs to the second origin');
    assert.equal(masterReads, 1);
  });

test('Cat media DASH resolves multi-base xml:base and root references before numeric template substitution',
  { timeout: 10000 }, async t => {
    const reads = [];
    const fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      if (request.url === '/manifest') {
        response.setHeader('content-type', 'application/dash+xml');
        return response.end('<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xml:base="/catalog/">' +
          '<BaseURL serviceLocation="one">one/</BaseURL><BaseURL serviceLocation="two">two/</BaseURL>' +
          '<Period><AdaptationSet><Representation id="main" xml:base="rep/">' +
          '<SegmentTemplate xml:base="../media/" initialization="/init.mp4?sig=a%2Fb" ' +
          'media="../segments/v-$RepresentationID$-$Number%03d$.m4s?part=$Time$"/>' +
          '</Representation></AdaptationSet></Period></MPD>');
      }
      response.end('segment-bytes');
    });
    const manifest = await read(fixture.url('/manifest'));
    assert.equal(manifest.status, 200);
    assert.deepEqual(reads, ['/manifest']);
    assert.doesNotMatch(manifest.text, /\bxml:base=/);
    const bases = [...manifest.text.matchAll(/<BaseURL serviceLocation="([^"]+)">([^<]+)<\/BaseURL>/g)];
    assert.equal(bases.length, 2);
    const init = /initialization="([^"]+)"/.exec(manifest.text)[1];
    const segment = /\bmedia="([^"]+)"/.exec(manifest.text)[1]
      .replace('$Number%03d$', '002').replace('$Time$', '42');
    for (const [, location, base] of bases) {
      assert.equal((await read(new URL(init, base))).text, 'segment-bytes');
      assert.equal(reads.at(-1), '/init.mp4?sig=a%2Fb');
      assert.equal((await read(new URL(segment, base))).text, 'segment-bytes');
      assert.equal(reads.at(-1), `/catalog/${location}/segments/v-main-002.m4s?part=42`);
    }
  });

test('DASH template destination labels survive URL hostname normalization without a domain access policy', () => {
  const registered = [];
  rewriteDASH('<MPD><Period><AdaptationSet><Representation id="one">' +
    '<SegmentTemplate media="http://$Number$.synthetic.invalid/seg-$Number$.m4s?time=$Time$"/>' +
    '</Representation></AdaptationSet></Period></MPD>', 'https://manifest.invalid/master.mpd',
  specification => { registered.push(specification); return 'http://route.invalid/base/'; });
  const template = registered.flatMap(specification => specification.resources ?? []).find(resource => resource.template);
  assert.equal(template.path, 'r0/$Number$-$Number$-$Time$');
  const resolve = compileDASHTemplate(template.url, template.path);
  assert.equal(resolve('r0/7-7-42'), 'http://7.synthetic.invalid/seg-7.m4s?time=42');
});

test('Cat media compressed binary prefixes replay exact wire bytes while compressed control input remains bounded',
  { timeout: 15000 }, async t => {
    const payload = randomBytes(budget + 1024), encoded = gzipSync(payload);
    const encoders = { gzip: gzipSync, deflate: deflateSync, br: brotliCompressSync };
    const fixture = await proxyFor(t, (request, response) => {
      if (request.url === '/binary') {
        response.writeHead(200, { 'content-type': 'application/octet-stream', 'content-encoding': 'gzip',
          'content-length': encoded.length, etag: '"binary-original"' });
        return response.end(encoded);
      }
      const encoding = request.url.slice(1);
      response.writeHead(200, { 'content-type': 'application/vnd.apple.mpegurl', 'content-encoding': encoding });
      response.end(encoders[encoding](Buffer.from('#EXTM3U\n#' + 'x'.repeat(budget))));
    });
    const binary = await read(fixture.url('/binary'));
    assert.equal(binary.status, 200);
    assert.equal(binary.headers['content-encoding'], 'gzip');
    assert.equal(binary.headers['content-length'], String(encoded.length));
    assert.equal(binary.headers.etag, '"binary-original"');
    assert.equal(digest(binary.bytes), digest(encoded));
    for (const encoding of Object.keys(encoders)) {
      const limited = await read(fixture.url('/' + encoding));
      assert.equal(limited.status, 413, encoding);
      assert.deepEqual(JSON.parse(limited.text), { error: { code: 'RESOURCE_LIMIT' } });
    }
  });

test('Cat media fragmented extensionless control signatures and supported compression retain exact original paths',
  { timeout: 15000 }, async t => {
    const reads = [];
    const encoders = { identity: bytes => bytes, gzip: gzipSync, deflate: deflateSync, br: brotliCompressSync };
    let fixture;
    fixture = await proxyFor(t, (request, response) => {
      reads.push(request.url);
      if (request.url === '/asset') return response.end('asset-bytes');
      const encoding = request.url.slice(1);
      const bytes = encoders[encoding](Buffer.from('#EXTM3U\n#EXTINF:1,\n/asset\n'));
      response.writeHead(200, { 'content-type': 'text/plain', 'content-encoding': encoding });
      response.write(bytes.subarray(0, 2));
      setImmediate(() => { response.write(bytes.subarray(2, 5)); response.end(bytes.subarray(5)); });
    });
    for (const encoding of Object.keys(encoders)) {
      const playlist = await read(fixture.url('/' + encoding));
      assert.equal(playlist.status, 200, encoding);
      assert.equal(playlist.headers['content-encoding'], undefined);
      const child = playlist.text.split('\n').find(line => line && !line.startsWith('#'));
      assert.equal(child, fixture.url('/asset'));
      assert.equal((await read(child)).text, 'asset-bytes');
    }
    assert.deepEqual(reads, Object.keys(encoders).flatMap(encoding => ['/' + encoding, '/asset']));
  });

// Delay the actual close dispatch, outside the resource's observer. This
// reproduces a destroyed resource whose native close has not yet completed.
function holdClose(target) {
  const original = target.emit, observed = gate();
  let held, released = false;
  target.emit = function(event, ...args) {
    if (event !== 'close' || released) return Reflect.apply(original, this, [event, ...args]);
    held = args;
    observed.resolve();
    return true;
  };
  return { observed: observed.promise, release() {
    if (released) return;
    released = true;
    target.emit = original;
    if (held) Reflect.apply(original, target, ['close', ...held]);
  } };
}
function interceptResource(t, kind, origin) {
  const original = http.request, captured = gate();
  t.mock.method(http, 'request', function(url, ...args) {
    const client = Reflect.apply(original, this, [url, ...args]);
    if (new URL(url).origin === origin) {
      const capture = target => queueMicrotask(() => captured.resolve({ target, ...holdClose(target) }));
      if (kind === 'client') capture(client);
      else client.once(kind === 'socket' ? 'socket' : 'response', capture);
    }
    return client;
  });
  syncBuiltinESMExports();
  t.after(() => { t.mock.restoreAll(); syncBuiltinESMExports(); });
  return captured.promise;
}

for (const kind of ['client', 'upstream', 'socket']) {
  test(`Cat media owner close waits for physical ${kind} close, not destroyed`,
    { timeout: 10000 }, async t => {
      const fixture = await proxyFor(t, (_request, response) => {
        response.writeHead(200, { 'content-type': 'video/mp4' });
        response.write('started');
      });
      const captured = interceptResource(t, kind, fixture.upstream.socketOrigin);
      const stream = open(t, fixture.url('/video'));
      await stream.ready;
      const held = await captured;
      let settled = false;
      const closing = fixture.runtime.close().finally(() => { settled = true; });
      closing.catch(() => {});
      try {
        await within(held.observed, 'resource did not begin native close');
        assert.equal(held.target.destroyed, true);
        await delay(25);
        assert.equal(settled, false, 'owner must not finish before the held physical close');
      } finally { held.release(); }
      await closing;
      await fixture.runtime.close();
      assert.equal(fixture.runtime.proxyTasks.size, 0);
    });
}

test('Cat media cleanup timeout remains a failed idempotent close rather than false drain',
  { timeout: 10000 }, async t => {
    const fixture = await proxyFor(t, (_request, response) => {
      response.writeHead(200, { 'content-type': 'video/mp4' });
      response.write('started');
    }, { cleanupTimeoutMs: 80 });
    const captured = interceptResource(t, 'socket', fixture.upstream.socketOrigin);
    const stream = open(t, fixture.url('/video'));
    await stream.ready;
    const held = await captured;
    try {
      await assert.rejects(within(fixture.runtime.close(), 'finite drain did not complete', 1000),
        { code: 'TIMEOUT' });
      await assert.rejects(fixture.runtime.close(), { code: 'TIMEOUT' });
      assert.equal(held.target.destroyed, true);
    } finally { held.release(); }
  });

test('Cat media stopping one session cancels and physically drains only its transfers',
  { timeout: 10000 }, async t => {
    const sockets = new Map();
    const fixture = await proxyFor(t, (request, response) => {
      sockets.set(request.url, response.socket);
      response.writeHead(200, { 'content-type': 'video/mp4' });
      response.write('started');
    });
    const other = fixture.addOwner('other');
    const stream = open(t, fixture.url('/video'));
    await stream.ready;
    const retained = open(t, other.url('/other-video'));
    await retained.ready;
    const closed = once(sockets.get('/video'), 'close');
    await fixture.runtime.stopWorker(fixture.session);
    await within(closed, 'session stop did not close the actual upstream connection');
    assert.equal(sockets.get('/other-video').destroyed, false);
    assert.equal(fixture.runtime.proxyTasks.size, 1);
    assert.equal([...fixture.runtime.proxyTasks][0].session, other.session);
    assert.equal(fixture.session.worker, null);
  });

const ownerSource = String.raw`
const http = require('http');
let main, media, reads = 0;
const listen = server => new Promise(resolve => server.listen(0, resolve));
module.exports.start = async () => {
  const body = (request, response) => {
    reads++;
    if (!request.url.startsWith('/stream')) return response.end('media');
    response.writeHead(200, { 'content-type': 'video/mp4' });
    response.write('start');
    const timer = setInterval(() => response.write('tick'), 20);
    response.once('close', () => clearInterval(timer));
  };
  main = http.createServer(async (request, response) => {
    if (request.url === '/config' || request.url === '/full-config')
      return response.end(JSON.stringify({ video: { sites: [{
        key: 'owner', api: '/api', actions: ['play', 'state', 'closeMedia'],
      }] } }));
    if (!request.url.startsWith('/api/')) return body(request, response);
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const payload = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : {};
    if (request.url === '/api/play')
      response.end(JSON.stringify({ parse: 0, url: 'http://127.0.0.1:' +
        (payload.secondary ? media : main).address().port + (payload.path || '/video?part=1') }));
    else if (request.url === '/api/state') response.end(JSON.stringify({ reads }));
    else { response.end('{}'); media.close(); }
  });
  await listen(main);
  media = http.createServer(body);
  await listen(media);
};
module.exports.stop = () => {
  for (const server of [main, media]) {
    server.closeAllConnections();
    if (server.listening) server.close();
  }
};`;

test('Cat real Worker disable drains its live media while another installation stream remains valid',
  { timeout: 30000 }, async t => {
    const root = mkdtempSync(join(tmpdir(), 'meow-cat-revoke-'));
    const runtime = createCatRuntime({ dataRoot: root });
    const workers = [];
    t.after(async () => {
      try {
        await runtime.close();
        for (const worker of workers) assert.equal(worker.threadId, -1);
      } finally { rmSync(root, { recursive: true, force: true }); }
    });
    for (const installationId of ['alpha', 'beta']) {
      await runtime.install({ installationId, source: ownerSource });
      workers.push(runtime.sessions.get(installationId).worker);
    }
    const play = (installationId, path) => runtime.invoke({ installationId, siteId: 'owner',
      operation: 'play', payload: { path } });
    const alpha = open(t, (await play('alpha', '/stream')).url);
    const beta = open(t, (await play('beta', '/stream')).url);
    await alpha.ready;
    const betaResponse = await beta.ready;
    await runtime.setEnabled('alpha', false);
    await within(alpha.closed, 'disabled installation left the real media connection open');
    assert.equal(workers[0].threadId, -1);
    assert.equal(betaResponse.destroyed, false);
    assert.equal([...runtime.proxyTasks].some(task => task.session.installationId === 'alpha'), false);
    assert.equal([...runtime.proxyTasks].some(task => task.session.installationId === 'beta'), true);
    assert.equal((await read((await play('beta', '/video')).url)).text, 'media');
    const next = once(betaResponse, 'data');
    betaResponse.resume();
    await within(next, 'unrelated installation stream stopped producing');
  });

test('Cat aggregate accepts external Host and Origin on existing routes and opens CORS without a token',
  { timeout: 10000 }, async t => {
    const reads = [];
    const fixture = await proxyFor(t, (request, response) => {
      reads.push({ path: request.url, headers: request.headers });
      response.setHeader('access-control-allow-origin', 'https://upstream-only.invalid');
      if (request.url === '/manifest') {
        response.setHeader('content-type', 'application/dash+xml');
        return response.end('<MPD><Period><AdaptationSet><Representation id="one"><BaseURL>/asset/</BaseURL>' +
          '<SegmentTemplate media="seg-$Number$"/></Representation></AdaptationSet></Period></MPD>');
      }
      response.end('routed-media');
    });
    assert.ok(['::', '0.0.0.0'].includes(fixture.runtime.server.address().address));
    const headers = { host: 'media.virtual.invalid', origin: 'https://external.invalid',
      authorization: 'call-input', cookie: 'media=call-input', 'sec-fetch-site': 'cross-site' };
    const preflight = await read(fixture.url('/config'), { method: 'OPTIONS',
      headers: { ...headers, 'access-control-request-headers': 'authorization,range' } });
    assert.equal(preflight.status, 204);
    assert.equal(preflight.headers['access-control-allow-origin'], headers.origin);
    assert.equal(preflight.headers['access-control-allow-headers'], 'authorization,range');
    assert.deepEqual(reads, []);
    for (const path of ['/config', '/admin/file?token=a%2Fb', '/other/normal', '//normal/double-slash']) {
      const result = await read(fixture.url(path), { headers });
      assert.equal(result.status, 200);
      assert.equal(result.text, 'routed-media');
      assert.equal(result.headers['access-control-allow-origin'], headers.origin);
      assert.equal(result.headers['access-control-allow-credentials'], 'true');
      assert.equal(reads.at(-1).path, path);
      for (const name of ['host', 'origin', 'authorization', 'cookie'])
        assert.equal(reads.at(-1).headers[name], headers[name]);
    }
    assert.equal((await read(fixture.url('/video'))).status, 200);
    assert.equal(reads.at(-1).headers.host, new URL(fixture.upstream.socketOrigin).host);
    const source = new URL('/url-credentials', fixture.upstream.socketOrigin);
    source.username = 'fixture-user'; source.password = 'fixture-password';
    const mapped = fixture.runtime.mapSnapshotProxy(fixture.session,
      fixture.runtime.snapshots.get('media'), source.href);
    assert.equal((await read(mapped)).text, 'routed-media');
    assert.equal(reads.at(-1).path, '/url-credentials');
    assert.equal(reads.at(-1).headers.authorization, 'Basic ' +
      Buffer.from('fixture-user:fixture-password').toString('base64'));
    assert.equal(reads.length, 6);
    const remoteHost = `external.invalid:${fixture.runtime.aggregatePort}`;
    const remote = await read(fixture.url('/manifest'), { headers: { host: remoteHost } });
    const remoteBase = /<BaseURL>([^<]+)<\/BaseURL>/.exec(remote.text)[1];
    assert.equal(new URL(remoteBase).host, remoteHost);
    const local = await read(fixture.url('/manifest'));
    const localBase = /<BaseURL>([^<]+)<\/BaseURL>/.exec(local.text)[1];
    assert.equal(new URL(localBase).host, new URL(fixture.url('/')).host,
      'reused route IDs must not retain the first caller Host');
    const reference = /\bmedia="([^"]+)"/.exec(remote.text)[1].replace('$Number$', '2');
    const child = new URL(reference, remoteBase);
    child.hostname = '127.0.0.1';
    assert.equal((await read(child, { headers: { host: remoteHost } })).text, 'routed-media');
    assert.equal(reads.at(-1).path, '/asset/seg-2');
  });

test('Cat real Worker routes stay passive until read and retire with their endpoint and generation',
  { timeout: 30000 }, async t => {
    const root = mkdtempSync(join(tmpdir(), 'meow-cat-routing-'));
    const runtime = createCatRuntime({ dataRoot: root });
    const workers = [];
    t.after(async () => {
      try {
        await runtime.close();
        for (const worker of workers) assert.equal(worker.threadId, -1);
      } finally { rmSync(root, { recursive: true, force: true }); }
    });
    for (const installationId of ['alpha', 'beta']) {
      await runtime.install({ installationId, source: ownerSource });
      workers.push(runtime.sessions.get(installationId).worker);
    }
    const invoke = (installationId, operation, payload = {}) =>
      runtime.invoke({ installationId, siteId: 'owner', operation, payload });
    const play = await invoke('alpha', 'play'), other = await invoke('beta', 'play');
    const generation = runtime.instance('alpha').generation;
    const secondary = await invoke('alpha', 'play', { secondary: true });
    assert.deepEqual(await invoke('alpha', 'state'), { reads: 0 });
    assert.deepEqual(await invoke('beta', 'state'), { reads: 0 });
    for (const url of [play.url, secondary.url]) assert.equal((await read(url)).text, 'media');
    assert.deepEqual(await invoke('alpha', 'state'), { reads: 2 });
    await invoke('alpha', 'closeMedia');
    await until(() => runtime.snapshots.get('alpha').endpoints.length === 1,
      'closed endpoint remained published');
    assert.deepEqual(JSON.parse((await read(secondary.url)).text), { error: { code: 'STALE_SESSION' } });
    assert.equal((await read(play.url)).text, 'media');
    await runtime.setEnabled('alpha', false);
    assert.deepEqual(JSON.parse((await read(play.url)).text), { error: { code: 'STALE_SESSION' } });
    assert.equal((await read(other.url)).text, 'media');
    await runtime.setEnabled('alpha', true);
    workers.push(runtime.sessions.get('alpha').worker);
    const next = await invoke('alpha', 'play');
    assert.notEqual(runtime.instance('alpha').generation, generation);
    assert.deepEqual(JSON.parse((await read(play.url)).text), { error: { code: 'STALE_SESSION' } });
    assert.deepEqual(await invoke('alpha', 'state'), { reads: 0 });
    assert.equal((await read(next.url)).text, 'media');
    await runtime.restart('alpha');
    workers.push(runtime.sessions.get('alpha').worker);
    assert.deepEqual(JSON.parse((await read(next.url)).text), { error: { code: 'STALE_SESSION' } });
    const current = await invoke('alpha', 'play');
    assert.deepEqual(await invoke('alpha', 'state'), { reads: 0 });
    assert.equal((await read(current.url)).text, 'media');
    assert.equal((await read(other.url)).text, 'media');
  });
