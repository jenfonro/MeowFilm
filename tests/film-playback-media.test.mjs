import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { networkInterfaces } from 'node:os';
import { once } from 'node:events';
import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { createMediaService } from '../packages/film-playback/src/media.js';

const bytes = Buffer.from(Array.from({ length: 65536 }, (_, index) => index % 251));
const credentials = { Cookie: 'synthetic-cookie', Authorization: 'Bearer synthetic-authorization',
  Referer: 'https://synthetic.invalid/player', Origin: 'https://synthetic.invalid', 'User-Agent': 'synthetic-player' };

function deferred() {
  let resolve;
  const promise = new Promise(complete => { resolve = complete; });
  return { promise, resolve };
}

async function source(t, handler) {
  const requests = [], sockets = new Set();
  const server = http.createServer((request, response) => {
    requests.push({ method: request.method, url: request.url, headers: { ...request.headers } });
    handler(request, response);
  });
  server.on('connection', socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => {
    const done = new Promise(resolve => server.close(resolve));
    for (const socket of sockets) socket.destroy();
    await done;
  });
  return { url: `http://127.0.0.1:${server.address().port}`, requests, server, sockets };
}

function service(t) {
  const media = createMediaService();
  t.after(() => media.close());
  return media;
}

function open(url, { method = 'GET', headers = {}, body } = {}) {
  let request;
  const response = new Promise((resolve, reject) => {
    request = http.request(url, { method, headers, agent: false }, resolve);
    request.once('error', reject);
    request.end(body);
  });
  return { request, response };
}

async function read(url, options) {
  const { response } = open(url, options);
  const incoming = await response, chunks = [];
  for await (const chunk of incoming) chunks.push(chunk);
  const body = Buffer.concat(chunks);
  return { status: incoming.statusCode, headers: incoming.headers, body, text: body.toString() };
}

function binary(request, response, data = bytes) {
  response.setHeader('Content-Type', 'video/mp4');
  response.setHeader('Accept-Ranges', 'bytes');
  if (request.headers.range) {
    const match = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range);
    const start = match ? Number(match[1]) : data.length;
    const end = match?.[2] ? Math.min(data.length - 1, Number(match[2])) : data.length - 1;
    if (start >= data.length || end < start) {
      response.writeHead(416, { 'Content-Range': `bytes */${data.length}`, 'Content-Length': 0 });
      response.end();
      return;
    }
    const part = data.subarray(start, end + 1);
    response.writeHead(206, { 'Content-Range': `bytes ${start}-${end}/${data.length}`, 'Content-Length': part.length });
    response.end(request.method === 'HEAD' ? undefined : part);
  } else {
    response.setHeader('Content-Length', data.length);
    response.end(request.method === 'HEAD' ? undefined : data);
  }
}

function manifest(response, text, type = 'application/vnd.apple.mpegurl') {
  response.writeHead(200, { 'Content-Type': type, 'Content-Length': Buffer.byteLength(text) });
  response.end(text);
}

const xmlValue = value => value.replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&apos;/g, "'")
  .replace(/&lt;/g, '<').replace(/&gt;/g, '>');
const uris = text => [...text.matchAll(/\bURI="([^"]+)"/g)].map(match => match[1]);
const hlsLines = text => text.split('\n').filter(line => line && !line.startsWith('#'));
const attributes = text => Object.fromEntries([...text.matchAll(/([\w:]+)="([^"]*)"/g)].map(match => [match[1], xmlValue(match[2])]));
const bases = text => [...text.matchAll(/<(?:\w+:)?BaseURL\b[^>]*>([^<]+)<\/(?:\w+:)?BaseURL>/g)].map(match => xmlValue(match[1]));
const representations = text => [...text.matchAll(/<(?:\w+:)?Representation\b([^>]*)>([^]*?)<\/(?:\w+:)?Representation>/g)]
  .map(match => ({ ...attributes(match[1]), body: match[2] }));
function template(text) {
  return attributes(/<(?:\w+:)?SegmentTemplate\b([^>]*)\/?>/.exec(text)?.[1] ?? '');
}
const expand = (value, number = 3, time = '9007199254740993000') => value
  .replace(/\$Number(?:%0?(\d+)d)?\$/g, (_, width) => String(number).padStart(Number(width ?? 0), '0'))
  .replace(/\$Time\$/g, time);

test('E38/M1-M3: direct prepare does no I/O; required headers use Node on the first request', async t => {
  const upstream = await source(t, binary), media = service(t);
  const direct = await media.prepare({ url: `${upstream.url}/direct.mp4` });
  assert.equal(direct.url, `${upstream.url}/direct.mp4`);
  assert.equal(upstream.requests.length, 0);
  const input = { url: `${upstream.url}/movie.mp4?signature=synthetic-url-secret`, headers: { ...credentials }, format: 'mp4' };
  const prepared = await media.prepare(input, { scope: { sourceKey: 'site-one', accountRevision: 7 } });
  input.headers.Cookie = 'mutated-after-prepare';
  assert.equal(upstream.requests.length, 0, 'address preparation cannot fetch any media/control bytes');
  assert.equal(new URL(prepared.url).hostname, '127.0.0.1');
  assert.equal(new URL(prepared.url).search, '');
  assert.equal(prepared.format, 'mp4');
  assert.doesNotMatch(JSON.stringify(prepared), /synthetic|signature|Cookie|Authorization/);
  const result = await read(prepared.url, { headers: { Authorization: 'client-must-not-override', 'X-Client-Only': 'not-upstream' } });
  assert.equal(result.status, 200);
  assert.deepEqual(result.body, bytes);
  assert.equal(upstream.requests.length, 1);
  assert.equal(upstream.requests[0].url, '/movie.mp4?signature=synthetic-url-secret');
  for (const [key, value] of Object.entries(credentials)) assert.equal(upstream.requests[0].headers[key.toLowerCase()], value);
  assert.equal(upstream.requests[0].headers['x-client-only'], undefined);
  assert.equal(result.headers['cache-control'], 'private, no-store');
  await media.release(direct.id);
  await media.release(prepared.id);
});

test('E38: empty source headers keep player-generated Range requests direct', async t => {
  const upstream = await source(t, binary), media = service(t);
  const prepared = await media.prepare({ url: `${upstream.url}/plain`, headers: {} });
  assert.equal(prepared.url, `${upstream.url}/plain`);
  assert.equal(prepared.mediaOrigin, undefined);
  assert.equal(upstream.requests.length, 0);
  const result = await read(prepared.url, { headers: { Range: 'bytes=64-127' } });
  assert.equal(result.status, 206);
  assert.deepEqual(result.body, bytes.subarray(64, 128));
  assert.equal(upstream.requests.length, 1);
  await media.release(prepared.id);
});

test('E38/E41: source-declared Range and If-Range reach the first request, without making prepare read media', async t => {
  const upstream = await source(t, binary), media = service(t);
  const prepared = await media.prepare({ url: `${upstream.url}/plain`,
    headers: { Range: 'bytes=64-127', 'If-Range': '"synthetic-validator"' } });
  assert.notEqual(prepared.url, `${upstream.url}/plain`);
  assert.equal(new URL(prepared.url).origin + '/', prepared.mediaOrigin);
  assert.equal(upstream.requests.length, 0);
  const result = await read(prepared.url);
  assert.equal(result.status, 206);
  assert.deepEqual(result.body, bytes.subarray(64, 128));
  assert.equal(upstream.requests[0].headers.range, 'bytes=64-127');
  assert.equal(upstream.requests[0].headers['if-range'], '"synthetic-validator"');
  const seek = await read(prepared.url, { headers: { Range: 'bytes=128-255' } });
  assert.equal(seek.status, 206);
  assert.deepEqual(seek.body, bytes.subarray(128, 256));
  assert.equal(upstream.requests.length, 2);
});

test('E41: binary GET, seek ranges, 416 and HEAD retain upstream HTTP semantics', async t => {
  const upstream = await source(t, binary), media = service(t);
  const prepared = await media.prepare({ url: `${upstream.url}/movie`, headers: { Cookie: credentials.Cookie } });
  for (const [start, end] of [[0, 31], [32768, 32895], [64, 127], [65000, 65535]]) {
    const result = await read(prepared.url, { headers: { Range: `bytes=${start}-${end}`, 'If-Range': '"synthetic-validator"' } });
    assert.equal(result.status, 206);
    assert.equal(result.headers['content-range'], `bytes ${start}-${end}/${bytes.length}`);
    assert.equal(result.headers['content-length'], String(end - start + 1));
    assert.deepEqual(result.body, bytes.subarray(start, end + 1));
  }
  const unsatisfied = await read(prepared.url, { headers: { Range: `bytes=${bytes.length}-` } });
  assert.equal(unsatisfied.status, 416);
  assert.equal(unsatisfied.headers['content-range'], `bytes */${bytes.length}`);
  const head = await read(prepared.url, { method: 'HEAD' });
  assert.equal(head.status, 200);
  assert.equal(head.body.length, 0);
  assert.equal(head.headers['content-length'], String(bytes.length));
  assert.equal(head.headers['content-type'], 'video/mp4');
  assert.equal(upstream.requests.at(-1).method, 'HEAD');
  assert.equal(upstream.requests.length, 6);
  assert.equal(upstream.requests[0].headers['if-range'], '"synthetic-validator"');
});

test('E38/E42: local script media follows the same header routing without enabling prefetch', async t => {
  const upstream = await source(t, binary), media = service(t);
  const descriptor = { url: `${upstream.url}/script-resource` };
  const direct = await media.prepare(descriptor);
  assert.equal(direct.url, descriptor.url);
  const prepared = await media.prepare({ ...descriptor, headers: credentials }, { scope: {
    sourceEngine: 'cat', sourceInstallationId: 'script-a', sourceKey: 'cat-a', sourceRevision: 'generation-4',
  } });
  assert.equal(new URL(prepared.url).origin + '/', prepared.mediaOrigin);
  assert.notEqual(prepared.url, descriptor.url);
  assert.equal(upstream.requests.length, 0);
  await assert.rejects(media.prefetch(prepared.id), { code: 'MEDIA_PREFETCH_DISABLED' });
  assert.equal(upstream.requests.length, 0, 'routing does not warm a hidden player');
  assert.deepEqual((await read(prepared.url, { headers: { Range: 'bytes=16-31' } })).body, bytes.subarray(16, 32));
  await media.invalidate(scope => scope.sourceInstallationId === 'script-a');
  assert.equal((await read(prepared.url)).status, 404);
  assert.equal(upstream.requests.length, 1);
});

test('E41: ordinary upstream HTTP errors remain errors, with no upstream cookies/redirect headers', async t => {
  const upstream = await source(t, (_request, response) => {
    response.writeHead(403, { 'Content-Type': 'text/plain', 'Set-Cookie': 'synthetic-hidden',
      Location: 'https://synthetic.invalid/private', 'WWW-Authenticate': 'synthetic-private' });
    response.end('upstream denied');
  });
  const media = service(t), prepared = await media.prepare({ url: upstream.url, headers: credentials });
  const result = await read(prepared.url);
  assert.equal(result.status, 403);
  assert.equal(result.text, 'upstream denied');
  for (const key of ['set-cookie', 'location', 'www-authenticate']) assert.equal(result.headers[key], undefined);
});

test('E42/N3: same-media redirects retain required headers across hosts', async t => {
  const other = await source(t, binary);
  const upstream = await source(t, (request, response) => {
    if (request.url === '/entry') { response.writeHead(302, { Location: '/same' }); response.end(); }
    else if (request.url === '/same') { response.writeHead(307, { Location: `${other.url}/final` }); response.end(); }
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/entry`, headers: credentials });
  assert.deepEqual((await read(prepared.url)).body, bytes);
  assert.equal(upstream.requests.length, 2);
  for (const request of upstream.requests) assert.equal(request.headers.cookie, credentials.Cookie);
  for (const [key, value] of Object.entries(credentials)) assert.equal(other.requests[0].headers[key.toLowerCase()], value);
  assert.equal(other.requests[0].headers.host, new URL(other.url).host);
});

test('E42/N3: each media receipt retains its own headers and source revision', async t => {
  const other = await source(t, binary);
  const upstream = await source(t, (_request, response) => {
    response.writeHead(302, { Location: `${other.url}/media` }); response.end();
  });
  const media = service(t), scope = { accountProvider: 'quark', accountRevision: 2 };
  const headers = { ...credentials, Authorization: 'Bearer synthetic-other', 'X-Required': 'this-media' };
  const prepared = await media.prepare({ url: upstream.url, headers }, { scope });
  headers.Authorization = 'mutated';
  scope.accountRevision = 3;
  assert.equal((await read(prepared.url)).status, 200);
  assert.equal(other.requests[0].headers.authorization, 'Bearer synthetic-other');
  assert.equal(other.requests[0].headers['x-required'], 'this-media');
  assert.equal(other.requests[0].headers.cookie, credentials.Cookie);
  let seen;
  await media.invalidate(identity => { seen = identity; return identity.accountRevision === 2; });
  assert.deepEqual(seen, { accountProvider: 'quark', accountRevision: 2 });
  assert.equal((await read(prepared.url)).status, 404);
});

test('E42/N1/N2: media routes accept external hosts, origins and browser preflight without credentials', async t => {
  const upstream = await source(t, binary), media = service(t);
  const one = await media.prepare({ url: upstream.url, headers: credentials });
  const two = await media.prepare({ url: upstream.url, headers: credentials });
  assert.notEqual(one.url, two.url);
  for (const candidate of [
    `${new URL(one.url).origin}/?url=${encodeURIComponent(upstream.url)}`,
    `${one.url}/arbitrary`,
    one.url.replace(/.$/, one.url.endsWith('a') ? 'b' : 'a'),
  ]) assert.equal((await read(candidate)).status, 404);
  const post = await read(one.url, { method: 'POST' });
  assert.equal(post.status, 405);
  assert.equal(post.headers.allow, 'GET, HEAD, OPTIONS');
  const preflight = await read(one.url, { method: 'OPTIONS', headers: {
    Origin: 'https://viewer.invalid', 'Access-Control-Request-Method': 'GET',
    'Access-Control-Request-Headers': 'Range, X-Viewer',
    'Access-Control-Request-Private-Network': 'true',
  } });
  assert.equal(preflight.status, 204);
  assert.equal(preflight.headers['access-control-allow-origin'], '*');
  assert.equal(preflight.headers['access-control-allow-headers'], 'Range, X-Viewer');
  assert.equal(preflight.headers['access-control-allow-private-network'], 'true');
  assert.equal(upstream.requests.length, 0);
  for (const headers of [{ Host: 'viewer.invalid' }, { Origin: 'https://viewer.invalid' }]) {
    const result = await read(one.url, { headers });
    assert.equal(result.status, 200);
    assert.equal(result.headers['access-control-allow-origin'], '*');
    assert.deepEqual(result.body, bytes);
  }
  assert.equal(upstream.requests.length, 2);
});

test('E42/N2: media and rewritten children are usable through a non-loopback interface', async t => {
  const external = Object.values(networkInterfaces()).flat().find(item => item.family === 'IPv4' && !item.internal);
  assert.ok(external, 'the host must expose a non-loopback interface for this network contract');
  const upstream = await source(t, (request, response) => {
    if (request.url === '/movie.m3u8') manifest(response, '#EXTM3U\n#EXTINF:3,\nsegment.ts\n#EXT-X-ENDLIST\n');
    else binary(request, response);
  });
  const media = service(t), receipt = await media.prepare({
    url: `${upstream.url}/movie.m3u8`, headers: credentials,
  }, { cache: true });
  await media.prefetch(receipt.id);
  const endpoint = new URL(receipt.url);
  endpoint.hostname = external.address;
  const playlist = await read(endpoint, { headers: { Origin: 'https://viewer.invalid' } });
  assert.equal(playlist.status, 200);
  const [child] = hlsLines(playlist.text);
  assert.equal(new URL(child).origin, endpoint.origin, 'cached children must use the endpoint reached by the caller');
  assert.deepEqual((await read(child)).body, bytes);
  assert.equal(upstream.requests.length, 2, 'external playback reuses the same cached media');
  await media.release(receipt.id);
  assert.equal((await read(endpoint)).status, 404, 'network openness does not revive released media');
});

test('E42/N3: source Host and URL user information use ordinary HTTP semantics', async t => {
  const upstream = await source(t, binary), media = service(t);
  const target = new URL(upstream.url);
  target.username = 'synthetic-user'; target.password = 'synthetic-password';
  const receipt = await media.prepare({ url: target.href, headers: { Host: 'media.virtual.invalid' } });
  assert.deepEqual((await read(receipt.url)).body, bytes);
  assert.equal(upstream.requests[0].headers.host, 'media.virtual.invalid');
  assert.equal(upstream.requests[0].headers.authorization,
    `Basic ${Buffer.from('synthetic-user:synthetic-password').toString('base64')}`);
});

test('E42: invalid descriptors fail with closed codes and no credential or URL content', async t => {
  const media = service(t);
  for (const descriptor of [
    { url: 'file:///synthetic-secret' },
    { url: 'https://synthetic.invalid', headers: { Cookie: 'synthetic-secret\r\nInjected: yes' } },
    { url: 'https://synthetic.invalid', headers: { Cookie: 1 } },
  ]) await assert.rejects(media.prepare(descriptor), error => {
    assert.equal(error.code, 'MEDIA_INVALID_DESCRIPTOR');
    assert.equal(error.message, error.code);
    return true;
  });
});

test('E39/M4: HLS master, variant, audio, I-frame, key, map and segments stay inside the resource graph', async t => {
  const key = Buffer.from([0, 255, 9, 7, 0, 1]);
  const upstream = await source(t, (request, response) => {
    const path = new URL(request.url, 'http://local').pathname;
    if (path === '/master.m3u8') manifest(response, [
      '#EXTM3U',
      '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="main, audio",URI="audio/list.m3u8"',
      `#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=12000,URI="${upstream.url}/iframe.m3u8?sig=synthetic-iframe"`,
      '#EXT-X-STREAM-INF:BANDWIDTH=800000,AUDIO="audio"',
      'video/list.m3u8?sig=synthetic-variant', '',
    ].join('\r\n'));
    else if (path.endsWith('list.m3u8') || path === '/iframe.m3u8') manifest(response, [
      '#EXTM3U', '#EXT-X-TARGETDURATION:6',
      '#EXT-X-KEY:METHOD=AES-128,URI="../keys/key.bin?sig=synthetic-key",IV=0x00000000000000000000000000000001',
      '#EXT-X-MAP:URI="../init.mp4?sig=synthetic-init",BYTERANGE="512@0"',
      '#EXTINF:6,', '../chunks/one.m4s?sig=synthetic-segment',
      '#EXTINF:6,', `${upstream.url}/chunks/two.m4s`, '#EXT-X-ENDLIST', '',
    ].join('\n'));
    else if (path === '/keys/key.bin') { response.end(key); }
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/master.m3u8`, headers: credentials });
  assert.equal(upstream.requests.length, 0);
  const master = await read(prepared.url);
  assert.equal(master.status, 200);
  assert.doesNotMatch(master.text, /synthetic-variant|synthetic-iframe/);
  const children = [...uris(master.text), ...hlsLines(master.text)];
  assert.equal(children.length, 3);
  for (const child of children) {
    assert.equal(new URL(child).origin, new URL(prepared.url).origin);
    const variant = await read(child);
    assert.equal(variant.status, 200);
    const [keyURL, mapURL] = uris(variant.text);
    assert.deepEqual((await read(keyURL)).body, key);
    assert.deepEqual((await read(mapURL, { headers: { Range: 'bytes=0-511' } })).body, bytes.subarray(0, 512));
    const segments = hlsLines(variant.text);
    assert.equal(segments.length, 2);
    assert.deepEqual((await read(segments[0], { headers: { Range: 'bytes=128-255' } })).body, bytes.subarray(128, 256));
    assert.deepEqual((await read(segments[1])).body, bytes);
    assert.equal((await read(child)).text, variant.text, 'refresh of an unchanged playlist must retain its registered graph');
  }
  for (const request of upstream.requests) {
    assert.equal(request.headers.cookie, credentials.Cookie);
    assert.equal(request.headers.authorization, credentials.Authorization);
  }
  assert.ok(upstream.requests.some(request => request.url === '/video/list.m3u8?sig=synthetic-variant'));
  assert.ok(upstream.requests.some(request => request.url === '/keys/key.bin?sig=synthetic-key'));
});

test('E39/N3: cross-host HLS keys receive the current media required headers', async t => {
  const other = await source(t, (_request, response) => response.end(Buffer.from([2, 0, 255])));
  const upstream = await source(t, (_request, response) => manifest(response,
    `#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI="${other.url}/key"\n#EXTINF:3,\nsegment.ts\n`));
  const media = service(t);
  for (const authorization of [credentials.Authorization, 'Bearer synthetic-second-media']) {
    const prepared = await media.prepare({ url: `${upstream.url}/list.m3u8`,
      headers: { ...credentials, Authorization: authorization } });
    const playlist = await read(prepared.url), key = await read(uris(playlist.text)[0]);
    assert.deepEqual(key.body, Buffer.from([2, 0, 255]));
    const request = other.requests.at(-1);
    assert.equal(request.headers.cookie, credentials.Cookie);
    assert.equal(request.headers.authorization, authorization);
    await media.release(prepared.id);
  }
});

test('E39: HLS variable definitions resolve signed references without a network probe at prepare', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url.startsWith('/list')) manifest(response,
      '#EXTM3U\n#EXT-X-DEFINE:NAME="folder",VALUE="clips"\n#EXT-X-MAP:URI="{$folder}/init.mp4"\n#EXTINF:1,\n{$folder}/one.ts\n');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/list.m3u8`, headers: credentials });
  assert.equal(upstream.requests.length, 0);
  const playlist = await read(prepared.url);
  assert.equal((await read(uris(playlist.text)[0])).status, 200);
  assert.equal((await read(hlsLines(playlist.text)[0])).status, 200);
  assert.deepEqual(upstream.requests.slice(1).map(request => request.url), ['/clips/init.mp4', '/clips/one.ts']);
});

test('E39/N1: HLS imported variables, reload parameters and hints retain their upstream semantics', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/master.m3u8') manifest(response,
      '#EXTM3U\n#EXT-X-DEFINE:NAME="folder",VALUE="clips"\n#EXT-X-STREAM-INF:BANDWIDTH=1\nchild.m3u8?sig=synthetic-child&opaque=a%20b%2Fc~\n');
    else if (request.url.startsWith('/child.m3u8')) manifest(response,
      '#EXTM3U\n#EXT-X-DEFINE:IMPORT="folder"\n#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES\n' +
      '#EXT-X-PART:DURATION=0.5,URI="{$folder}/part.m4s"\n#EXT-X-PRELOAD-HINT:TYPE=PART,URI="{$folder}/next.m4s"\n');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/master.m3u8`, headers: credentials });
  const master = await read(prepared.url), [child] = hlsLines(master.text);
  const playlist = await read(`${child}?_HLS_msn=3&_HLS_part=1&_HLS_skip=YES`);
  assert.equal(playlist.status, 200);
  assert.equal(uris(playlist.text).length, 2);
  for (const uri of uris(playlist.text)) assert.equal((await read(uri)).status, 200);
  assert.deepEqual(upstream.requests.slice(1).map(request => request.url),
    ['/child.m3u8?sig=synthetic-child&opaque=a%20b%2Fc~&_HLS_msn=3&_HLS_part=1&_HLS_skip=YES', '/clips/part.m4s', '/clips/next.m4s']);
  const count = upstream.requests.length;
  const queries = ['vendor=a%20b%2Fc~', '_HLS_skip=anything', '_HLS_part=1&_HLS_part=2'];
  for (const query of queries) {
    assert.equal((await read(`${child}?${query}`)).status, 200);
    assert.equal(upstream.requests.at(-1).url, `/child.m3u8?sig=synthetic-child&opaque=a%20b%2Fc~&${query}`);
  }
  assert.equal(upstream.requests.length, count + queries.length);
});

test('E39/E41: compressed control documents rewrite correctly and support local Range and HEAD', async t => {
  const original = '#EXTM3U\n#EXTINF:3,\nsegment.ts\n';
  const upstream = await source(t, (request, response) => {
    const body = gzipSync(original);
    if (request.url.startsWith('/playlist')) {
      response.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl', 'Content-Encoding': 'gzip',
        'Content-Length': body.length, ETag: '"upstream-control-document"' });
      response.end(request.method === 'HEAD' ? undefined : body);
    } else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/playlist`, headers: credentials });
  const full = await read(prepared.url);
  assert.equal(full.status, 200);
  assert.equal(full.headers['content-encoding'], undefined);
  assert.equal(full.headers.etag, undefined);
  assert.equal(full.headers['content-length'], String(full.body.length));
  const part = await read(prepared.url, { headers: { Range: 'bytes=0-6' } });
  assert.equal(part.status, 206);
  assert.equal(part.text, '#EXTM3U');
  assert.equal(part.headers['content-range'], `bytes 0-6/${full.body.length}`);
  const beyond = await read(prepared.url, { headers: { Range: 'bytes=999999-' } });
  assert.equal(beyond.status, 416);
  const head = await read(prepared.url, { method: 'HEAD' });
  assert.equal(head.status, 200);
  assert.equal(head.body.length, 0);
  assert.equal(head.headers['content-encoding'], undefined);
  assert.equal(head.headers['content-length'], undefined, 'unrewritten upstream length must not describe a rewritten playlist');
});

test('E39/E40: extensionless octet-stream HLS and MPD are recognized by a bounded prefix without losing binary bytes', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/hls') manifest(response, '#EXTM3U\n#EXTINF:3,\nsegment.ts\n', 'application/octet-stream');
    else if (request.url === '/dash') manifest(response,
      '<MPD><Period><AdaptationSet><SegmentTemplate initialization="init.mp4" media="chunk-$Number$.m4s"/>' +
      '<Representation id="v" bandwidth="1"/></AdaptationSet></Period></MPD>', 'application/octet-stream');
    else binary(request, response);
  });
  const media = service(t);
  for (const kind of ['hls', 'dash']) {
    const before = upstream.requests.length;
    const prepared = await media.prepare({ url: `${upstream.url}/${kind}`, headers: credentials });
    assert.equal(upstream.requests.length, before);
    const result = await read(prepared.url);
    assert.equal(result.status, 200);
    if (kind === 'hls') assert.deepEqual((await read(hlsLines(result.text)[0])).body, bytes);
    else {
      const [track] = representations(result.text), [base] = bases(track.body);
      assert.deepEqual((await read(new URL(template(track.body).initialization, base))).body, bytes);
    }
  }
});

test('E39/E40/E48: manifest UTF-8 validation and BOM handling do not require ICU', async t => {
  const Decoder = globalThis.TextDecoder;
  t.mock.method(globalThis, 'TextDecoder', function (label, options) {
    if (options?.fatal) throw new TypeError('fatal decoding requires ICU');
    return new Decoder(label, options);
  });
  assert.throws(() => new TextDecoder('utf-8', { fatal: true }), /fatal decoding requires ICU/);
  const title = '正片é\uFEFF\uFFFD🎬';
  const documents = [
    { kind: 'hls', path: '/manifest.m3u8', type: 'application/vnd.apple.mpegurl',
      text: `#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,${title}\nsegment.m4s\n#EXT-X-ENDLIST\n`,
      children: ['/segment.m4s'] },
    { kind: 'dash', path: '/manifest.mpd', type: 'application/dash+xml',
      text: '<?xml version="1.0" encoding="UTF-8"?>\n<MPD><ProgramInformation><Title>' + title +
        '</Title></ProgramInformation><Period><AdaptationSet contentType="video"><Representation id="v" bandwidth="1">' +
        '<SegmentList duration="3"><Initialization sourceURL="init.mp4"/><SegmentURL media="segment.m4s"/>' +
        '</SegmentList></Representation></AdaptationSet></Period></MPD>',
      children: ['/init.mp4', '/segment.m4s'] },
  ];
  for (const document of documents) {
    await t.test(`${document.kind}: valid UTF-8 with and without BOM supports GET and prefetch`, async t => {
      let body;
      const upstream = await source(t, (request, response) => {
        if (request.url === document.path) manifest(response, body, document.type);
        else binary(request, response);
      });
      const media = service(t);
      for (const bom of ['', '\uFEFF']) {
        body = Buffer.from(bom + document.text);
        for (const cache of [false, true]) {
          const before = upstream.requests.length;
          const receipt = await media.prepare({ url: upstream.url + document.path, headers: credentials }, { cache });
          assert.equal(upstream.requests.length, before, 'prepare cannot read a manifest');
          if (cache) assert.deepEqual(await media.prefetch(receipt.id), { bytes: document.children.length * bytes.length });
          const result = await read(receipt.url);
          assert.equal(result.status, 200);
          assert.equal(result.headers['content-type'], document.type);
          assert.equal(result.text.startsWith('\uFEFF'), false, 'the leading UTF-8 BOM is consumed');
          assert.ok(result.text.includes(title), 'interior BOM, valid U+FFFD and non-ASCII text must survive decoding');
          let targets;
          if (document.kind === 'hls') targets = hlsLines(result.text);
          else {
            const [track] = representations(result.text), [base] = bases(track.body);
            targets = [
              attributes(/<Initialization\b([^>]*)/.exec(track.body)[1]).sourceURL,
              attributes(/<SegmentURL\b([^>]*)/.exec(track.body)[1]).media,
            ].map(value => new URL(value, base).href);
          }
          assert.equal(targets.length, document.children.length);
          for (const target of targets) {
            assert.equal(new URL(target).origin, new URL(receipt.url).origin);
            const child = await read(target);
            assert.equal(child.status, 200);
            assert.deepEqual(child.body, bytes);
          }
          assert.deepEqual(upstream.requests.slice(before).map(request => request.url), [document.path, ...document.children],
            'prefetch must reuse the same document and binary resource graph');
          await media.release(receipt.id);
        }
      }
    });
    await t.test(`${document.kind}: malformed UTF-8 remains invalid even after a BOM`, async t => {
      let body;
      const upstream = await source(t, (_request, response) => manifest(response, body, document.type));
      const media = service(t);
      const invalid = [
        ['isolated continuation', [0x80]],
        ['overlong encoding', [0xc0, 0xaf]],
        ['UTF-16 surrogate', [0xed, 0xa0, 0x80]],
        ['code point beyond U+10FFFF', [0xf4, 0x90, 0x80, 0x80]],
        ['truncated sequence', [0xe2, 0x82]],
      ].map(([name, value]) => [name, document.kind === 'hls'
        ? Buffer.concat([Buffer.from('\uFEFF' + document.text + '#'), Buffer.from(value)])
        : Buffer.concat([Buffer.from('\uFEFF' + document.text.replace('</MPD>', '<!--')),
          Buffer.from(value), Buffer.from('--></MPD>')])]);
      invalid.push(['incomplete leading BOM', Buffer.concat([Buffer.from([0xef, 0xbb]), Buffer.from(document.text)])]);
      for (const [name, value] of invalid) {
        await t.test(name, async () => {
          body = value;
          const before = upstream.requests.length;
          const receipt = await media.prepare({ url: upstream.url + document.path, headers: credentials }, { cache: true });
          const result = await read(receipt.url);
          assert.equal(result.status, 502);
          assert.equal(result.text, 'MEDIA_MANIFEST_INVALID');
          await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_MANIFEST_INVALID' });
          assert.deepEqual(upstream.requests.slice(before).map(request => request.url), [document.path, document.path],
            'an invalid control document cannot publish or fetch child resources');
          await media.release(receipt.id);
        });
      }
    });
  }
});

test('E41: an extensionless HLS range response is reloaded as a complete control document before rewrite', async t => {
  const text = '#EXTM3U\n#EXTINF:3,\nsegment.ts\n';
  const upstream = await source(t, (request, response) => {
    if (request.headers.range) {
      response.writeHead(206, { 'Content-Type': 'application/vnd.apple.mpegurl', 'Content-Range': `bytes 0-6/${text.length}` });
      response.end('#EXTM3U');
    } else manifest(response, text);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/list`, headers: credentials });
  const result = await read(prepared.url, { headers: { Range: 'bytes=0-6' } });
  assert.equal(result.status, 206);
  assert.equal(result.text, '#EXTM3U');
  assert.equal(upstream.requests.length, 2);
  assert.equal(upstream.requests[0].headers.range, 'bytes=0-6');
  assert.equal(upstream.requests[1].headers.range, undefined);
});

test('E40/M4: hierarchical DASH BaseURL alternatives and inherited templates cover video/audio, init and seek', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url.startsWith('/entry')) {
      response.writeHead(302, { Location: '/manifests/root.mpd?sig=synthetic-mpd' }); response.end();
    } else if (request.url.startsWith('/manifests/root.mpd')) manifest(response, `<?xml version="1.0" encoding="UTF-8"?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT60S">
  <BaseURL serviceLocation="one">../cdn-one/</BaseURL>
  <BaseURL serviceLocation="two">../cdn-two/</BaseURL>
  <Period id="p"><BaseURL>period/</BaseURL>
    <AdaptationSet contentType="video">
      <SegmentTemplate timescale="1000" startNumber="3" initialization="$RepresentationID$/init-$Bandwidth$.mp4?sig=synthetic-init"
        media="$RepresentationID$/chunk-$Number%05d$-$Time$.m4s?sig=synthetic-segment&amp;kind=video">
        <SegmentTimeline><S t="0" d="6000" r="9"/></SegmentTimeline>
      </SegmentTemplate>
      <Representation id="video-high" bandwidth="800000"><BaseURL>high/</BaseURL></Representation>
      <Representation id="video-low" bandwidth="200000"><SegmentTemplate media="low-$Number%05d$-$Time$.m4s"/></Representation>
    </AdaptationSet>
    <AdaptationSet contentType="audio"><BaseURL>audio/</BaseURL>
      <SegmentTemplate timescale="48000" initialization="init-$RepresentationID$.mp4" media="segment-$Number$.m4s"/>
      <Representation id="audio-main" bandwidth="128000"/>
    </AdaptationSet>
  </Period>
</MPD>`, 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/entry`, headers: credentials, format: 'dash' });
  assert.equal(upstream.requests.length, 0);
  const mpd = await read(prepared.url);
  assert.equal(mpd.status, 200);
  assert.doesNotMatch(mpd.text, /synthetic-(mpd|init|segment)|\/cdn-one\/|\/cdn-two\//);
  const tracks = representations(mpd.text);
  assert.equal(tracks.length, 3);
  for (const track of tracks) {
    const trackBases = bases(track.body), segment = template(track.body);
    assert.equal(trackBases.length, 2);
    assert.equal(segment.timescale, track.id === 'audio-main' ? '48000' : '1000');
    if (track.id !== 'audio-main') assert.match(track.body, /SegmentTimeline/);
    for (const base of trackBases) {
      assert.equal(new URL(base).origin, new URL(prepared.url).origin);
      const init = await read(new URL(segment.initialization, base), { headers: { Range: 'bytes=0-127' } });
      assert.equal(init.status, 206);
      assert.deepEqual(init.body, bytes.subarray(0, 128));
      const chunk = await read(new URL(expand(segment.media), base), { headers: { Range: 'bytes=128-255' } });
      assert.equal(chunk.status, 206);
      assert.deepEqual(chunk.body, bytes.subarray(128, 256));
    }
  }
  const paths = upstream.requests.map(request => request.url);
  assert.ok(paths.includes('/cdn-one/period/high/video-high/init-800000.mp4?sig=synthetic-init'));
  assert.ok(paths.includes('/cdn-two/period/high/video-high/chunk-00003-9007199254740993000.m4s?sig=synthetic-segment&kind=video'));
  assert.ok(paths.includes('/cdn-one/period/video-low/init-200000.mp4?sig=synthetic-init'));
  assert.ok(paths.includes('/cdn-two/period/low-00003-9007199254740993000.m4s'));
  assert.ok(paths.includes('/cdn-two/period/audio/init-audio-main.mp4'));
  assert.ok(paths.includes('/cdn-one/period/audio/segment-3.m4s'));
  assert.ok(upstream.requests.every(request => request.headers.cookie === credentials.Cookie));
});

test('E40: DASH SegmentList preserves all declared segments, XML entities and initialization ranges', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/list.mpd') manifest(response, `<d:MPD xmlns:d="urn:mpeg:dash:schema:mpd:2011">
<d:Period><d:AdaptationSet><d:SegmentList timescale="1"><d:Initialization sourceURL="init.mp4" range="0-15"/>
<d:SegmentURL media="old-unselected.m4s"/></d:SegmentList>
<d:Representation id="list" bandwidth="1"><d:BaseURL>files/</d:BaseURL>
<d:SegmentList duration="3">
<d:SegmentURL media="one.m4s?sig=a&amp;b=2"/><d:SegmentURL media="two.m4s" index="two.idx"/></d:SegmentList>
</d:Representation></d:AdaptationSet></d:Period></d:MPD>`, 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/list.mpd`, headers: credentials });
  const result = await read(prepared.url);
  assert.equal(result.status, 200);
  const [track] = representations(result.text), [base] = bases(track.body);
  const init = attributes(/<d:Initialization\b([^>]*)/.exec(track.body)[1]);
  assert.equal(init.range, '0-15');
  assert.deepEqual((await read(new URL(init.sourceURL, base), { headers: { Range: 'bytes=0-15' } })).body, bytes.subarray(0, 16));
  const list = [...track.body.matchAll(/<d:SegmentURL\b([^>]*)/g)].map(match => attributes(match[1]));
  assert.equal(list.length, 2);
  for (const item of list) {
    assert.equal((await read(new URL(item.media, base))).status, 200);
    if (item.index) assert.equal((await read(new URL(item.index, base))).status, 200);
  }
  assert.deepEqual(upstream.requests.slice(1).map(request => request.url),
    ['/files/init.mp4', '/files/one.m4s?sig=a&b=2', '/files/two.m4s', '/files/two.idx']);
});

test('E40: DASH single-file SegmentBase keeps byte-range init/index/seek on the exact declared file', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/base.mpd') manifest(response, `<MPD><Period><AdaptationSet>
<Representation id="single" bandwidth="1"><BaseURL>movie.mp4?sig=synthetic-file</BaseURL>
<SegmentBase indexRange="16-31"><Initialization range="0-15"/></SegmentBase></Representation>
</AdaptationSet></Period></MPD>`, 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/base.mpd`, headers: credentials });
  const result = await read(prepared.url), [track] = representations(result.text), [base] = bases(track.body);
  assert.match(track.body, /indexRange="16-31"/);
  assert.match(track.body, /Initialization range="0-15"/);
  for (const range of ['0-15', '16-31', '32000-32127']) {
    const [start, end] = range.split('-').map(Number);
    const readRange = await read(base, { headers: { Range: `bytes=${range}` } });
    assert.equal(readRange.status, 206);
    assert.deepEqual(readRange.body, bytes.subarray(start, end + 1));
  }
  assert.ok(upstream.requests.slice(1).every(request => request.url === '/movie.mp4?sig=synthetic-file'));
});

test('E40/E42/N3: absolute DASH subrequests retain media headers without origin registration', async t => {
  const other = await source(t, binary);
  const upstream = await source(t, (_request, response) => manifest(response,
    `<MPD><Period><AdaptationSet><SegmentTemplate initialization="${other.url}/init.mp4"
    media="${other.url}/chunks/$Number$.m4s"/><Representation id="v" bandwidth="1"/>
    </AdaptationSet></Period></MPD>`, 'application/dash+xml'));
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/m.mpd`, headers: credentials });
  const result = await read(prepared.url), [track] = representations(result.text), [base] = bases(track.body);
  const value = template(track.body);
  await read(new URL(value.initialization, base));
  await read(new URL(expand(value.media), base));
  assert.equal(other.requests.length, 2);
  for (const request of other.requests) {
    assert.equal(request.headers.authorization, credentials.Authorization);
    assert.equal(request.headers.cookie, credentials.Cookie);
  }
  const count = other.requests.length + upstream.requests.length;
  for (const path of ['anything', 'r1/https%3A%2F%2Fmissing.invalid', 'r1/../../private']) {
    assert.equal((await read(base + path)).status, 404);
  }
  assert.equal(other.requests.length + upstream.requests.length, count);
});

test('E40: xml:base and representation overrides keep the original DASH resolution context', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/dir/root.mpd') manifest(response, `<MPD xml:base="../assets/">
<Period><AdaptationSet><SegmentTemplate initialization="init.mp4" media="chunk-$Number$.m4s"/>
<Representation id="v" bandwidth="1" xml:base="video/"><BaseURL>high/</BaseURL></Representation>
</AdaptationSet></Period></MPD>`, 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/dir/root.mpd`, headers: credentials });
  const result = await read(prepared.url), [track] = representations(result.text), [base] = bases(track.body), value = template(track.body);
  assert.equal(result.status, 200);
  await read(new URL(value.initialization, base));
  await read(new URL(expand(value.media), base));
  assert.deepEqual(upstream.requests.slice(1).map(request => request.url), ['/assets/video/high/init.mp4', '/assets/video/high/chunk-3.m4s']);
});

test('E40: DASH BaseURL/segment xml:base, Location, UTC timing and namespaced metadata retain their contexts', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/doc/root.mpd') manifest(response, `<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013">
<BaseURL xml:base="../">assets/</BaseURL><Location>next.mpd</Location>
<UTCTiming schemeIdUri="urn:mpeg:dash:utc:http-iso:2014" value="clock"/>
<Period><AdaptationSet><ContentProtection><cenc:pssh><![CDATA[synthetic-initialization]]></cenc:pssh></ContentProtection>
<SegmentTemplate xml:base="chunks/" initialization="init.mp4" media="chunk-$Number$.m4s"/>
<Representation id="v" bandwidth="1"/></AdaptationSet></Period></MPD>`, 'application/dash+xml');
    else if (request.url === '/doc/next.mpd') manifest(response,
      '<MPD><Period><AdaptationSet><Representation id="v"><BaseURL>single.mp4</BaseURL></Representation></AdaptationSet></Period></MPD>',
      'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/doc/root.mpd`, headers: credentials });
  const result = await read(prepared.url);
  assert.equal(result.status, 200);
  assert.match(result.text, /<cenc:pssh>synthetic-initialization<\/cenc:pssh>/);
  const [track] = representations(result.text), [base] = bases(track.body), value = template(track.body);
  await read(new URL(value.initialization, base));
  await read(new URL(expand(value.media), base));
  const location = xmlValue(/<Location>([^<]+)<\/Location>/.exec(result.text)[1]);
  assert.equal((await read(location)).status, 200);
  const timing = attributes(/<UTCTiming\b([^>]*)/.exec(result.text)[1]);
  assert.equal((await read(timing.value)).status, 200);
  assert.deepEqual(upstream.requests.slice(1).map(request => request.url),
    ['/assets/chunks/init.mp4', '/assets/chunks/chunk-3.m4s', '/doc/next.mpd', '/doc/clock']);
});

test('E40/E42: DASH template literals, printf substitutions and repeated values cannot become a free URL suffix', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/template.mpd') manifest(response,
      '<MPD><Period><AdaptationSet><SegmentTemplate initialization="init-$$-$RepresentationID$-$Bandwidth%06d$.mp4"' +
      ' media="chunk-$Number%05d$-$Number$-$Time$.m4s"/><Representation id="v$literal" bandwidth="128"/>' +
      '</AdaptationSet></Period></MPD>', 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/template.mpd`, headers: credentials });
  const result = await read(prepared.url), [track] = representations(result.text), [base] = bases(track.body), value = template(track.body);
  assert.equal((await read(new URL(value.initialization, base))).status, 200);
  const mediaURL = new URL(expand(value.media, 7, '1234567890123456789012345'), base).href;
  assert.equal((await read(mediaURL)).status, 200);
  assert.deepEqual(upstream.requests.slice(1).map(request => request.url),
    ['/init-$-v$literal-000128.mp4', '/chunk-00007-7-1234567890123456789012345.m4s']);
  const count = upstream.requests.length;
  assert.equal((await read(mediaURL.replace('00007-7-', '00007-8-'))).status, 404);
  assert.equal(upstream.requests.length, count);
});

test('E39/E40: unsafe or unsupported control structures fail explicitly without an unmediated reference', async t => {
  const documents = [
    ['<!DOCTYPE MPD [<!ENTITY x SYSTEM "file:///synthetic-secret">]><MPD>&x;</MPD>', 'MEDIA_MANIFEST_UNSUPPORTED'],
    ['<MPD xmlns:xlink="http://www.w3.org/1999/xlink"><Period xlink:href="https://synthetic.invalid/external"/></MPD>', 'MEDIA_MANIFEST_UNSUPPORTED'],
    ['<MPD><PatchLocation>https://synthetic.invalid/patch</PatchLocation></MPD>', 'MEDIA_MANIFEST_UNSUPPORTED'],
    ['<MPD><Period><AdaptationSet><Representation id="v"><BaseURL>file:///synthetic-secret</BaseURL></Representation></AdaptationSet></Period></MPD>', 'MEDIA_MANIFEST_INVALID'],
    ['<MPD><Period><AdaptationSet><Representation id="v"><BaseURL>movie.mp4</BaseURL></AdaptationSet></Period></MPD>', 'MEDIA_MANIFEST_INVALID'],
    ['<MPD><Period><AdaptationSet><ContentProtection><Laurl>https://synthetic.invalid/license</Laurl></ContentProtection><Representation id="v"/></AdaptationSet></Period></MPD>', 'MEDIA_DRM_UNSUPPORTED'],
  ];
  let document;
  const upstream = await source(t, (_request, response) => manifest(response, document, 'application/dash+xml'));
  const media = service(t);
  for (const [text, code] of documents) {
    document = text;
    const prepared = await media.prepare({ url: `${upstream.url}/movie.mpd`, headers: credentials });
    const result = await read(prepared.url);
    assert.equal(result.status, 502);
    assert.equal(result.text, code);
    assert.doesNotMatch(result.text, /synthetic-secret|synthetic.invalid|file:/);
    await media.release(prepared.id);
  }
  assert.equal(upstream.requests.length, documents.length);
});

test('M4/N1: DRM metadata is passed to the decoder without URL-based rejection or license requests', async t => {
  const upstream = await source(t, binary), media = service(t);
  const metadata = { scheme: 'cenc', keySystem: 'com.widevine.alpha', keyId: 'synthetic-key-id' };
  const prepared = await media.prepare({ url: upstream.url, headers: credentials, drm: metadata });
  assert.deepEqual(prepared.drm, metadata);
  const drm = { licenseURL: `${upstream.url}/license`, headers: credentials };
  const withLicense = await media.prepare({ url: upstream.url, headers: credentials, drm });
  assert.deepEqual(withLicense.drm, drm);
  assert.notEqual(withLicense.drm, drm);
  assert.equal(upstream.requests.length, 0);
});

function endless(response, state) {
  response.writeHead(200, { 'Content-Type': 'video/mp4' });
  const chunk = Buffer.alloc(65536, 7);
  state.closed = deferred();
  response.once('close', () => { state.stopped = true; state.closed.resolve(); });
  function write() {
    if (response.destroyed) return;
    while ((state.produced ?? 0) < 512 * 1024 * 1024) {
      state.produced = (state.produced ?? 0) + chunk.length;
      if (!response.write(chunk)) { response.once('drain', write); return; }
    }
    response.end();
  }
  write();
}

async function paused(url) {
  const transfer = open(url), response = await transfer.response;
  await new Promise((resolve, reject) => {
    response.once('error', reject);
    response.once('data', () => { response.pause(); resolve(); });
  });
  // Expected abort errors are observable on the held consumer, not unhandled events.
  response.on('error', () => {});
  return { ...transfer, incoming: response };
}

test('E41/M5: a slow reader backpressures the binary stream; release cancels and drains the physical transfer', { timeout: 10000 }, async t => {
  const state = {}, upstream = await source(t, (_request, response) => endless(response, state));
  const media = service(t), prepared = await media.prepare({ url: upstream.url, headers: credentials });
  const transfer = await paused(prepared.url);
  t.after(() => transfer.request.destroy());
  let stable = 0, previous = -1;
  for (let step = 0; step < 40 && stable < 4; step++) {
    await delay(25);
    stable = state.produced === previous ? stable + 1 : 0;
    previous = state.produced;
  }
  assert.ok(stable >= 4, 'the upstream producer must actually stop progressing while the consumer is paused');
  assert.ok(state.produced < 32 * 1024 * 1024, `bounded pipeline must not read the offered 512MiB: received ${state.produced}`);
  const before = state.produced;
  await media.release(prepared.id);
  await state.closed.promise;
  assert.equal(state.stopped, true);
  await delay(30);
  assert.equal(state.produced, before);
  assert.equal((await read(prepared.url)).status, 404);
  await media.release(prepared.id);
});

test('E43/M7: unrelated UI/smart-stop cancellation cannot kill an active owner; explicit release is required', { timeout: 10000 }, async t => {
  const state = {}, upstream = await source(t, (_request, response) => endless(response, state));
  const media = service(t), signal = new AbortController();
  const prepared = await media.prepare({ url: upstream.url, headers: credentials }, {
    scope: { sourceKey: 'site', sourceRevision: 1 }, signal: signal.signal,
  });
  const transfer = await paused(prepared.url);
  t.after(() => transfer.request.destroy());
  signal.abort();
  await delay(50);
  assert.equal(state.stopped, undefined, 'business/UI cancellation is deliberately not a media-owner revoke');
  transfer.incoming.resume();
  await once(transfer.incoming, 'data');
  transfer.incoming.pause();
  await media.release(prepared.id);
  await state.closed.promise;
  assert.equal(state.stopped, true);
});

test('E42/E43: exact account/source invalidation cancels affected owners and leaves other owners playable', { timeout: 10000 }, async t => {
  const streams = new Map();
  const upstream = await source(t, (request, response) => {
    if (request.url === '/finite') binary(request, response);
    else {
      const state = {};
      streams.set(request.url, state);
      endless(response, state);
    }
  });
  const media = service(t);
  const affected = await media.prepare({ url: `${upstream.url}/affected`, headers: credentials },
    { scope: { sourceKey: 'A', sourceRevision: 1, accountProvider: 'quark', accountRevision: 3 } });
  const retained = await media.prepare({ url: `${upstream.url}/retained`, headers: credentials },
    { scope: { sourceKey: 'B', sourceRevision: 1, accountProvider: 'quark', accountRevision: 4 } });
  const first = await paused(affected.url), second = await paused(retained.url);
  t.after(() => { first.request.destroy(); second.request.destroy(); });
  await media.invalidate(scope => scope.accountProvider === 'quark' && scope.accountRevision === 3);
  await streams.get('/affected').closed.promise;
  assert.equal((await read(affected.url)).status, 404);
  assert.equal(streams.get('/retained').stopped, undefined);
  second.incoming.resume();
  await once(second.incoming, 'data');
  second.incoming.pause();
  await media.invalidate(scope => scope.sourceKey === 'B' && scope.sourceRevision === 1);
  await streams.get('/retained').closed.promise;
  assert.equal((await read(retained.url)).status, 404);
  const finite = await media.prepare({ url: `${upstream.url}/finite`, headers: credentials },
    { scope: { sourceKey: 'B', sourceRevision: 2, accountProvider: 'quark', accountRevision: 5 } });
  assert.deepEqual((await read(finite.url)).body, bytes);
});

test('E43/E48: close aborts all active streams, drains sockets and makes old capabilities unusable after restart', { timeout: 10000 }, async t => {
  const states = [];
  const upstream = await source(t, (_request, response) => {
    const state = {};
    states.push(state);
    endless(response, state);
  });
  const media = service(t);
  const one = await media.prepare({ url: `${upstream.url}/one`, headers: credentials });
  const two = await media.prepare({ url: `${upstream.url}/two`, headers: credentials });
  const first = await paused(one.url), second = await paused(two.url);
  t.after(() => { first.request.destroy(); second.request.destroy(); });
  await media.close();
  await Promise.all(states.map(state => state.closed.promise));
  assert.ok(states.every(state => state.stopped));
  await media.close();
  await assert.rejects(media.prepare({ url: upstream.url }), { code: 'MEDIA_CLOSED' });
  await assert.rejects(read(one.url));
  const restarted = service(t), fresh = await restarted.prepare({ url: upstream.url, headers: credentials });
  const oldPath = new URL(one.url).pathname;
  assert.equal((await read(new URL(oldPath, fresh.url))).status, 404);
  assert.notEqual(new URL(one.url).pathname, new URL(fresh.url).pathname);
});

test('E41/E43: release while awaiting upstream headers and a client disconnect during manifest reading both drain', { timeout: 10000 }, async t => {
  const entered = deferred(), stopped = deferred();
  let mode = 'headers';
  const upstream = await source(t, (_request, response) => {
    response.once('close', () => stopped.resolve());
    if (mode === 'manifest') {
      response.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' });
      response.write('#EXTM3U\n#');
    }
    entered.resolve();
  });
  const media = service(t), prepared = await media.prepare({ url: upstream.url, headers: credentials });
  const pending = read(prepared.url);
  const rejected = assert.rejects(pending);
  await entered.promise;
  await media.release(prepared.id);
  await rejected;
  await stopped.promise;
  mode = 'manifest';
  const enteredManifest = deferred(), stoppedManifest = deferred();
  upstream.server.removeAllListeners('request');
  upstream.server.on('request', (_request, response) => {
    response.once('close', () => stoppedManifest.resolve());
    response.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' });
    response.write('#EXTM3U\n#');
    enteredManifest.resolve();
  });
  const manifestResource = await media.prepare({ url: `${upstream.url}/list.m3u8`, headers: credentials });
  const disconnected = open(manifestResource.url);
  const aborted = assert.rejects(disconnected.response);
  await enteredManifest.promise;
  disconnected.request.destroy();
  await aborted;
  await stoppedManifest.promise;
  await media.release(manifestResource.id);
});

test('E41/E48: a failed or truncated upstream never becomes an empty successful media body', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url.startsWith('/headers')) { request.socket.destroy(); return; }
    if (request.url === '/empty') { response.writeHead(200, { 'Content-Length': 0 }); response.end(); return; }
    response.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': 65536 });
    response.write(Buffer.from([1, 2, 3]));
    setImmediate(() => response.destroy());
  });
  const media = service(t);
  const failed = await media.prepare({ url: `${upstream.url}/headers?sig=synthetic-hidden`, headers: credentials });
  const result = await read(failed.url);
  assert.equal(result.status, 502);
  assert.equal(result.text, 'MEDIA_UPSTREAM_ERROR');
  const empty = await media.prepare({ url: `${upstream.url}/empty`, headers: credentials });
  const emptyResult = await read(empty.url);
  assert.equal(emptyResult.status, 502);
  assert.equal(emptyResult.text, 'MEDIA_EMPTY_RESPONSE');
  const truncated = await media.prepare({ url: `${upstream.url}/truncated`, headers: credentials });
  await assert.rejects(read(truncated.url));
  await media.release(truncated.id);
});

test('E39/E40/E48: Cat manifest validation keeps its media error while other HTTP failures retain their exact body', async t => {
  const invalid = Buffer.from('{"error":{"code":"INVALID_RESPONSE"}}');
  const stale = Buffer.from('{"error":{"code":"STALE_SESSION"}}');
  const opaque = Buffer.from([0xff, 0xfe, 0xfd]);
  const upstream = await source(t, (request, response) => {
    response.writeHead(502, { 'content-type': 'application/json' });
    response.end(request.url.startsWith('/stale') ? stale : request.url.startsWith('/opaque') ? opaque : invalid);
  });
  const media = service(t);
  for (const [path, scope, expected] of [
    ['/root.m3u8', { sourceEngine: 'cat' }, Buffer.from('MEDIA_MANIFEST_INVALID')],
    ['/root.mpd', { sourceEngine: 'cat' }, Buffer.from('MEDIA_MANIFEST_INVALID')],
    ['/stale.m3u8', { sourceEngine: 'cat' }, stale],
    ['/opaque.mpd', { sourceEngine: 'cat' }, opaque],
    ['/root.mp4', { sourceEngine: 'cat' }, invalid],
    ['/root.m3u8', { sourceEngine: 'source' }, invalid],
  ]) {
    const before = upstream.requests.length;
    const receipt = await media.prepare({ url: upstream.url + path, headers: credentials }, { scope });
    assert.equal(upstream.requests.length, before);
    const result = await read(receipt.url);
    assert.equal(result.status, 502);
    assert.deepEqual(result.body, expected);
    assert.equal(upstream.requests.length, before + 1);
    await media.release(receipt.id);
  }
});

test('E42/E48: redirect loops and non-HTTP redirect targets fail with a closed error code', async t => {
  const upstream = await source(t, (request, response) => {
    response.writeHead(302, { Location: request.url === '/loop' ? '/loop' : 'file:///synthetic-secret' });
    response.end();
  });
  const media = service(t);
  for (const path of ['/loop', '/scheme']) {
    const prepared = await media.prepare({ url: upstream.url + path, headers: credentials });
    const result = await read(prepared.url);
    assert.equal(result.status, 502);
    assert.equal(result.text, 'MEDIA_REDIRECT_ERROR');
  }
  assert.equal(upstream.requests.length, 2);
});

test('E41: oversized control documents fail as a resource boundary, with no media-size limit', async t => {
  const upstream = await source(t, (_request, response) => {
    response.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' });
    response.write('#EXTM3U\n#');
    const chunk = Buffer.alloc(65536, 97);
    let sent = 0;
    function write() {
      if (response.destroyed) return;
      while (sent < 9 * 1024 * 1024) {
        sent += chunk.length;
        if (!response.write(chunk)) { response.once('drain', write); return; }
      }
      response.end();
    }
    write();
  });
  const media = service(t), prepared = await media.prepare({ url: `${upstream.url}/list.m3u8`, headers: credentials });
  const result = await read(prepared.url);
  assert.equal(result.status, 502);
  assert.equal(result.text, 'MEDIA_MANIFEST_LIMIT');
  await media.release(prepared.id);
});

test('E41/M5: media larger than the control-document budget streams incrementally with exact binary bytes', async t => {
  const size = 12 * 1024 * 1024, chunk = Buffer.alloc(65536, 0xa7);
  const upstream = await source(t, (_request, response) => {
    response.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': size });
    let sent = 0;
    function write() {
      if (response.destroyed) return;
      while (sent < size) {
        sent += chunk.length;
        if (!response.write(chunk)) { response.once('drain', write); return; }
      }
      response.end();
    }
    write();
  });
  const media = service(t), prepared = await media.prepare({ url: upstream.url, headers: credentials });
  const incoming = await open(prepared.url).response, actual = createHash('sha256'), expected = createHash('sha256');
  assert.equal(incoming.statusCode, 200);
  let received = 0;
  for await (const part of incoming) { received += part.length; actual.update(part); }
  for (let sent = 0; sent < size; sent += chunk.length) expected.update(chunk);
  assert.equal(received, size);
  assert.equal(actual.digest('hex'), expected.digest('hex'));
});

test('E43: prepare racing close cannot publish a resource from a closed media service', async () => {
  const media = createMediaService();
  const preparing = media.prepare({ url: 'http://127.0.0.1:1/not-contacted', headers: credentials });
  const closing = media.close();
  await assert.rejects(preparing, { code: 'MEDIA_CLOSED' });
  await closing;
});

test('E34/E38: cache is explicit, prepare stays zero-I/O, and only controlled receipts carry the owned authority', async t => {
  const upstream = await source(t, binary), media = service(t);
  const direct = await media.prepare({ url: `${upstream.url}/ordinary.mp4` }, { cache: false });
  assert.equal(direct.url, `${upstream.url}/ordinary.mp4`);
  assert.equal(Object.hasOwn(direct, 'mediaOrigin'), false);
  await assert.rejects(media.prefetch(direct.id), { code: 'MEDIA_PREFETCH_DISABLED' });
  const buffered = await media.prepare({ url: `${upstream.url}/next.mp4` }, { cache: true });
  const headed = await media.prepare({ url: `${upstream.url}/headed.mp4`, headers: credentials }, { cache: false });
  for (const receipt of [buffered, headed]) {
    assert.match(receipt.id, /^[A-Za-z0-9_-]{32}$/);
    assert.equal(receipt.mediaOrigin, `${new URL(receipt.url).origin}/`);
    assert.match(new URL(receipt.url).pathname, new RegExp(`^/media/${receipt.id}/[A-Za-z0-9_-]{32}$`));
    assert.equal(new URL(receipt.mediaOrigin).hostname, '127.0.0.1');
    assert.equal(new URL(receipt.url).search, '');
  }
  assert.equal(upstream.requests.length, 0, 'ordinary iOS address preparation must never read or prefetch media');
  await assert.rejects(media.prepare({ url: upstream.url }, { cache: 'true' }), { code: 'MEDIA_INVALID_REQUEST' });
  await assert.rejects(media.prefetch('not-a-resource'), { code: 'MEDIA_NOT_FOUND' });
});

test('E39/E41: an extensionless octet-stream HLS range beginning at zero is identified before forwarding a partial control graph', async t => {
  const text = '#EXTM3U\n#EXTINF:3,\nsegment.ts\n#EXT-X-ENDLIST\n';
  const upstream = await source(t, (request, response) => {
    if (request.url === '/list' && request.headers.range) {
      response.writeHead(206, { 'Content-Type': 'application/octet-stream',
        'Content-Range': `bytes 0-6/${text.length}`, 'Content-Length': 7 });
      response.end('#EXTM3U');
    } else if (request.url === '/list') manifest(response, text, 'application/octet-stream');
    else binary(request, response);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/list`, headers: credentials });
  const prefix = await read(receipt.url, { headers: { Range: 'bytes=0-6' } });
  assert.equal(prefix.status, 206);
  assert.equal(prefix.text, '#EXTM3U');
  assert.deepEqual(upstream.requests.map(request => request.headers.range), ['bytes=0-6', undefined]);
  const list = await read(receipt.url);
  assert.deepEqual((await read(hlsLines(list.text)[0])).body, bytes);
  assert.ok(upstream.requests.every(request => request.headers.cookie === credentials.Cookie));
});

test('E34/E41: progressive prefetch merges callers and full playback reads cached prefix plus only the missing tail', async t => {
  const movie = Buffer.alloc(3 * 1024 * 1024, 0x37);
  bytes.copy(movie);
  const upstream = await source(t, (request, response) => {
    response.setHeader('ETag', '"synthetic-stable-media"');
    binary(request, response, movie);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/next.mp4`, headers: credentials }, { cache: true });
  const [one, two] = await Promise.all([media.prefetch(receipt.id), media.prefetch(receipt.id)]);
  assert.deepEqual(one, two);
  assert.ok(one.bytes > 0 && one.bytes < movie.length, 'prefetch must retain real startup bytes, not a whole movie');
  assert.equal(upstream.requests.length, 1);
  assert.equal(upstream.requests[0].headers.range, `bytes=0-${one.bytes - 1}`);
  assert.deepEqual(await media.prefetch(receipt.id), one);
  assert.equal(upstream.requests.length, 1, 'repeated prefetch must reuse the same media cache');
  const head = await read(receipt.url, { method: 'HEAD' });
  assert.equal(head.headers['content-length'], String(movie.length));
  const cached = await read(receipt.url, { headers: { Range: 'bytes=32-127' } });
  assert.equal(cached.status, 206);
  assert.deepEqual(cached.body, movie.subarray(32, 128));
  assert.equal((await read(receipt.url, { headers: { Range: `bytes=${movie.length}-` } })).status, 416);
  assert.equal(upstream.requests.length, 1);
  const whole = await read(receipt.url);
  assert.equal(whole.status, 200);
  assert.equal(whole.headers['content-length'], String(movie.length));
  assert.deepEqual(whole.body, movie);
  assert.equal(upstream.requests.length, 2);
  assert.equal(upstream.requests[1].headers.range, `bytes=${one.bytes}-${movie.length - 1}`);
  assert.equal(upstream.requests[1].headers['if-range'], '"synthetic-stable-media"');
  assert.equal(upstream.requests[1].headers.cookie, credentials.Cookie);
});

test('E34/E41: playback arriving during a prefetch waits for the shared bytes instead of downloading a second prefix', async t => {
  const entered = deferred(), complete = deferred(), data = bytes.subarray(0, 1024);
  const upstream = await source(t, (request, response) => {
    const match = /^bytes=0-(\d+)$/.exec(request.headers.range ?? '');
    assert.ok(match);
    response.writeHead(206, { 'Content-Type': 'video/mp4', 'Content-Range': `bytes 0-${data.length - 1}/${data.length}`,
      'Content-Length': data.length });
    response.write(data.subarray(0, 32)); entered.resolve();
    complete.promise.then(() => response.end(data.subarray(32)));
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/clip.mp4` }, { cache: true });
  const buffering = media.prefetch(receipt.id);
  await entered.promise;
  const playback = read(receipt.url);
  await delay(20);
  assert.equal(upstream.requests.length, 1);
  complete.resolve();
  assert.equal((await buffering).bytes, data.length);
  assert.deepEqual((await playback).body, data);
  assert.equal(upstream.requests.length, 1);
});

test('E34/E39/E42: HLS prefetch fetches required key, init, one video and default audio; cached graph and seek do not refetch', async t => {
  const key = Buffer.alloc(16, 0x72), init = bytes.subarray(0, 128), segment = bytes.subarray(128, 1152);
  const keys = await source(t, (_request, response) => response.end(key));
  const upstream = await source(t, (request, response) => {
    if (request.url === '/master.m3u8') manifest(response, [
      '#EXTM3U', '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",DEFAULT=YES,NAME="main",URI="audio.m3u8"',
      '#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="unselected",URI="unselected-audio.m3u8"',
      '#EXT-X-STREAM-INF:BANDWIDTH=100,AUDIO="a"', 'video.m3u8',
      '#EXT-X-STREAM-INF:BANDWIDTH=10000,AUDIO="a"', 'unselected-video.m3u8', '',
    ].join('\n'));
    else if (['/video.m3u8', '/audio.m3u8'].includes(request.url)) manifest(response, [
      '#EXTM3U', '#EXT-X-TARGETDURATION:6', `#EXT-X-KEY:METHOD=AES-128,URI="${keys.url}/key"`,
      `#EXT-X-MAP:URI="${request.url === '/audio.m3u8' ? 'audio' : 'video'}-init.mp4"`,
      '#EXTINF:6,', `${request.url === '/audio.m3u8' ? 'audio' : 'video'}-one.m4s`,
      '#EXTINF:6,', 'unselected-two.m4s', '#EXT-X-ENDLIST', '',
    ].join('\n'));
    else binary(request, response, request.url.includes('init') ? init : segment);
  });
  const media = service(t), receipt = await media.prepare({
    url: `${upstream.url}/master.m3u8`, headers: credentials,
  }, { cache: true });
  assert.equal(upstream.requests.length + keys.requests.length, 0);
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: key.length + 2 * init.length + 2 * segment.length });
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/master.m3u8', '/video.m3u8', '/video-init.mp4', '/video-one.m4s', '/audio.m3u8', '/audio-init.mp4', '/audio-one.m4s']);
  assert.equal(keys.requests.length, 1, 'a shared key in the same owner must not download twice');
  assert.equal(keys.requests[0].headers.cookie, credentials.Cookie);
  assert.equal(keys.requests[0].headers.authorization, credentials.Authorization);
  const master = await read(receipt.url), video = await read(hlsLines(master.text)[0]), audio = await read(uris(master.text)[0]);
  for (const playlist of [video, audio]) {
    assert.deepEqual((await read(uris(playlist.text)[0])).body, key);
    assert.deepEqual((await read(uris(playlist.text)[1])).body, init);
    assert.deepEqual((await read(hlsLines(playlist.text)[0], { headers: { Range: 'bytes=64-127' } })).body, segment.subarray(64, 128));
  }
  assert.equal(upstream.requests.length, 7);
  assert.equal(keys.requests.length, 1);
  assert.ok(upstream.requests.every(request => request.headers.cookie === credentials.Cookie));
  assert.doesNotMatch(master.text + video.text + audio.text, /synthetic|127\.0\.0\.1:[0-9]+\/key/);
});

test('E34/E39/E41: HLS byte-range init and segment share one asset; playback only fetches holes between cached spans', async t => {
  const data = bytes.subarray(0, 128);
  const upstream = await source(t, (request, response) => {
    if (request.url === '/list.m3u8') manifest(response,
      '#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-MAP:URI="asset.mp4",BYTERANGE="16@0"\n' +
      '#EXT-X-BYTERANGE:32@32\n#EXTINF:3,\nasset.mp4\n#EXT-X-ENDLIST\n');
    else binary(request, response, data);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/list.m3u8` }, { cache: true });
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: 48 });
  const list = await read(receipt.url), asset = hlsLines(list.text)[0];
  assert.deepEqual((await read(asset)).body, data);
  assert.deepEqual(upstream.requests.slice(1).map(request => request.headers.range),
    ['bytes=0-15', 'bytes=32-63', 'bytes=16-31', 'bytes=64-127']);
  assert.deepEqual((await read(asset, { headers: { Range: 'bytes=32-63' } })).body, data.subarray(32, 64));
  assert.equal(upstream.requests.length, 5);
});

test('E34/E39/E48: a map cannot provide the missing offset of the first HLS media segment or part', async t => {
  const upstream = await source(t, (request, response) => {
    const media = request.url === '/segment.m3u8'
      ? '#EXT-X-BYTERANGE:32\n#EXTINF:3,\nasset.mp4\n'
      : '#EXT-X-PART:DURATION=0.5,URI="asset.mp4",BYTERANGE="32"\n';
    manifest(response, '#EXTM3U\n#EXT-X-TARGETDURATION:3\n' +
      '#EXT-X-MAP:URI="asset.mp4",BYTERANGE="16@0"\n' + media + '#EXT-X-ENDLIST\n');
  });
  const media = service(t);
  for (const path of ['/segment.m3u8', '/part.m3u8']) {
    const receipt = await media.prepare({ url: upstream.url + path }, { cache: true });
    await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_MANIFEST_INVALID' });
    const playlist = await read(receipt.url);
    assert.equal(playlist.status, 200, 'prefetch rejection must not rewrite the original byte-range declaration');
    assert.match(playlist.text, /BYTERANGE(?::32\n|="32")/);
    await media.release(receipt.id);
  }
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/segment.m3u8', '/segment.m3u8', '/part.m3u8', '/part.m3u8'],
    'an unresolved startup byte range must not trigger a guessed binary request');
});

test('E34/E39/E42: the first low-latency HLS part retains its own key and map, not later dependencies', async t => {
  const text = '#EXTM3U\n#EXT-X-TARGETDURATION:3\n' +
    '#EXT-X-KEY:METHOD=AES-128,URI="first-key"\n#EXT-X-MAP:URI="first-init.mp4"\n' +
    '#EXT-X-PART:DURATION=0.5,URI="first-part.m4s"\n' +
    '#EXT-X-KEY:METHOD=AES-128,URI="later-key"\n#EXT-X-MAP:URI="later-init.mp4"\n' +
    '#EXT-X-PART:DURATION=0.5,URI="later-part.m4s"\n';
  const upstream = await source(t, (request, response) => {
    if (request.url === '/live.m3u8') manifest(response, text);
    else binary(request, response, bytes.subarray(0,
      request.url === '/first-key' ? 16 : request.url === '/first-init.mp4' ? 32 : 64));
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/live.m3u8`, headers: credentials }, { cache: true });
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: 112 });
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/live.m3u8', '/first-key', '/first-init.mp4', '/first-part.m4s']);
  const list = await read(receipt.url);
  for (const [index, url] of uris(list.text).slice(0, 3).entries()) {
    assert.deepEqual((await read(url)).body, bytes.subarray(0, [16, 32, 64][index]));
  }
  assert.equal(upstream.requests.length, 5, 'only the live playlist refresh may read again, not cached startup media');
});

test('E34/E39/E42: HLS init and first media segment retain both required keys when encryption changes after the map', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/root.m3u8') manifest(response, '#EXTM3U\n#EXT-X-TARGETDURATION:3\n' +
      '#EXT-X-KEY:METHOD=AES-128,URI="init-key",IV=0x00000000000000000000000000000001\n' +
      '#EXT-X-MAP:URI="init.mp4"\n#EXT-X-KEY:METHOD=AES-128,URI="media-key"\n' +
      '#EXTINF:3,\nfirst.m4s\n#EXT-X-ENDLIST\n');
    else binary(request, response, bytes.subarray(0,
      request.url.endsWith('key') ? 16 : request.url === '/init.mp4' ? 32 : 64));
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.m3u8`, headers: credentials }, { cache: true });
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: 128 });
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/root.m3u8', '/init-key', '/media-key', '/init.mp4', '/first.m4s']);
  const list = await read(receipt.url);
  for (const [index, url] of uris(list.text).entries()) {
    assert.deepEqual((await read(url)).body, bytes.subarray(0, [16, 32, 16][index]));
  }
  assert.deepEqual((await read(hlsLines(list.text)[0])).body, bytes.subarray(0, 64));
  assert.equal(upstream.requests.length, 5);
});

test('E34/E39/E41: a declared HLS startup byte range past EOF fails instead of caching a shortened successful segment', async t => {
  const data = bytes.subarray(0, 16);
  const upstream = await source(t, (request, response) => {
    if (request.url === '/root.m3u8') manifest(response,
      '#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-BYTERANGE:32@0\n#EXTINF:3,\nsegment.m4s\n#EXT-X-ENDLIST\n');
    else binary(request, response, data);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.m3u8` }, { cache: true });
  await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_RANGE_UNSUPPORTED' });
  const list = await read(receipt.url), segment = hlsLines(list.text)[0];
  const response = await read(segment, { headers: { Range: 'bytes=0-31' } });
  assert.equal(response.status, 206, 'ordinary HTTP Range retains its valid EOF-clamping semantics');
  assert.deepEqual(response.body, data);
  assert.equal(upstream.requests.length, 3, 'the failed prefetch must not publish partial media as a cache hit');
});

test('E34/E40/E42: DASH startup includes video/audio init and first timeline segments; native range hits reuse that exact graph', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/root.mpd') manifest(response, `<MPD type="static"><Period>
<AdaptationSet contentType="video"><SegmentTemplate startNumber="7" initialization="$RepresentationID$/init.mp4"
media="$RepresentationID$/seg-$Number%05d$-$Time$.m4s"><SegmentTimeline><S t="9007199254740993000" d="6"/></SegmentTimeline></SegmentTemplate>
<Representation id="video-low" bandwidth="10"/><Representation id="video-high" bandwidth="10000"/></AdaptationSet>
<AdaptationSet contentType="audio"><SegmentTemplate startNumber="7" initialization="audio/init.mp4" media="audio/seg-$Number$.m4s"/>
<Representation id="audio" bandwidth="1"/></AdaptationSet></Period></MPD>`, 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.mpd`, headers: credentials }, { cache: true });
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: 4 * bytes.length });
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/root.mpd', '/video-low/init.mp4', '/video-low/seg-00007-9007199254740993000.m4s', '/audio/init.mp4', '/audio/seg-7.m4s']);
  const mpd = await read(receipt.url);
  for (const track of representations(mpd.text).filter(track => track.id !== 'video-high')) {
    const [base] = bases(track.body), value = template(track.body);
    assert.deepEqual((await read(new URL(value.initialization, base))).body, bytes);
    assert.deepEqual((await read(new URL(expand(value.media, 7), base), { headers: { Range: 'bytes=128-255' } })).body, bytes.subarray(128, 256));
  }
  assert.equal(upstream.requests.length, 5);
  assert.ok(upstream.requests.every(request => request.headers.authorization === credentials.Authorization));
  const video = representations(mpd.text).find(track => track.id === 'video-low'), [base] = bases(video.body);
  assert.deepEqual((await read(new URL(expand(template(video.body).media, 8), base))).body, bytes);
  assert.equal(upstream.requests.length, 6, 'the second segment must not have been prefetched');
});

test('E34/E40: DASH SegmentList prefetch retains only its declared init and first media byte ranges', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/root.mpd') manifest(response, `<MPD><Period><AdaptationSet contentType="video">
<Representation id="v"><BaseURL>files/</BaseURL><SegmentList>
<Initialization sourceURL="init.mp4" range="0-15"/><SegmentURL media="one.m4s" mediaRange="32-63"/>
<SegmentURL media="unselected-two.m4s"/></SegmentList></Representation></AdaptationSet></Period></MPD>`, 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.mpd` }, { cache: true });
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: 48 });
  assert.deepEqual(upstream.requests.map(request => [request.url, request.headers.range]),
    [['/root.mpd', undefined], ['/files/init.mp4', 'bytes=0-15'], ['/files/one.m4s', 'bytes=32-63']]);
  const mpd = await read(receipt.url), [track] = representations(mpd.text), [base] = bases(track.body);
  const init = attributes(/<Initialization\b([^>]*)/.exec(track.body)[1]);
  const first = attributes(/<SegmentURL\b([^>]*)/.exec(track.body)[1]);
  assert.deepEqual((await read(new URL(init.sourceURL, base), { headers: { Range: 'bytes=0-15' } })).body, bytes.subarray(0, 16));
  assert.deepEqual((await read(new URL(first.media, base), { headers: { Range: 'bytes=32-63' } })).body, bytes.subarray(32, 64));
  assert.equal(upstream.requests.length, 3);
});

test('E34/E40: DASH SegmentList may address one BaseURL using only byte ranges', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/root.mpd') manifest(response, `<MPD><Period><AdaptationSet contentType="video">
<Representation id="v"><BaseURL>asset.mp4</BaseURL><SegmentList>
<Initialization range="0-15"/><SegmentURL mediaRange="32-63"/><SegmentURL mediaRange="64-95"/>
</SegmentList></Representation></AdaptationSet></Period></MPD>`, 'application/dash+xml');
    else binary(request, response);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.mpd`,
    headers: credentials }, { cache: true });
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: 48 });
  assert.deepEqual(upstream.requests.map(request => [request.url, request.headers.range]),
    [['/root.mpd', undefined], ['/asset.mp4', 'bytes=0-15'], ['/asset.mp4', 'bytes=32-63']]);
  const mpd = await read(receipt.url), [track] = representations(mpd.text), [base] = bases(track.body);
  assert.deepEqual((await read(base, { headers: { Range: 'bytes=0-15' } })).body, bytes.subarray(0, 16));
  assert.deepEqual((await read(base, { headers: { Range: 'bytes=32-63' } })).body, bytes.subarray(32, 64));
  assert.equal(upstream.requests.length, 3, 'init and first segment consume the prefetched owner');
  assert.deepEqual((await read(base, { headers: { Range: 'bytes=64-95' } })).body, bytes.subarray(64, 96));
  assert.equal(upstream.requests.length, 4, 'later media is fetched only when the player asks');
});

function sidx(version = 0, hierarchical = false) {
  const body = Buffer.alloc(version ? 52 : 44);
  body.writeUInt32BE(body.length, 0); body.write('sidx', 4);
  body[8] = version; body.writeUInt32BE(1, 12); body.writeUInt32BE(1000, 16);
  const count = version ? 38 : 30;
  body.writeUInt16BE(1, count);
  body.writeUInt32BE(hierarchical ? 0x80000020 : 32, count + 2);
  body.writeUInt32BE(6000, count + 6);
  return body;
}

test('E34/E40: DASH SegmentBase reads a finite SIDX and the referenced first fragment, not just index bytes', async t => {
  for (const version of [0, 1]) {
    const index = sidx(version), data = Buffer.concat([bytes.subarray(0, 16), index, bytes.subarray(128, 160), bytes.subarray(160, 224)]);
    const upstream = await source(t, (request, response) => {
      if (request.url === '/root.mpd') manifest(response, `<MPD><Period><AdaptationSet contentType="video"><Representation id="v">
<BaseURL>asset.mp4</BaseURL><SegmentBase indexRange="16-${15 + index.length}">
<Initialization range="0-15"/></SegmentBase></Representation></AdaptationSet></Period></MPD>`, 'application/dash+xml');
      else binary(request, response, data);
    });
    const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.mpd` }, { cache: true });
    assert.deepEqual(await media.prefetch(receipt.id), { bytes: 16 + index.length + 32 });
    assert.deepEqual(upstream.requests.slice(1).map(request => request.headers.range),
      ['bytes=0-15', `bytes=16-${15 + index.length}`, `bytes=${16 + index.length}-${47 + index.length}`]);
    const mpd = await read(receipt.url), [track] = representations(mpd.text), [asset] = bases(track.body);
    assert.deepEqual((await read(asset, { headers: { Range: `bytes=0-${47 + index.length}` } })).body,
      data.subarray(0, 48 + index.length));
    assert.equal(upstream.requests.length, 4);
    await media.release(receipt.id);
  }
});

test('E34/E48: unsupported prefetch documents, hierarchical indexes and HTTP failure are explicit closed errors', async t => {
  let mode = 'unknown';
  const upstream = await source(t, (request, response) => {
    if (mode === 'http') { response.writeHead(403); response.end('synthetic-private'); }
    else if (mode === 'unknown') manifest(response, '<SmoothStreamingMedia/>', 'application/xml');
    else if (mode === 'empty') { response.writeHead(200, { 'Content-Length': 0 }); response.end(); }
    else if (mode === 'hls') manifest(response, '#EXTM3U\n#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=1,URI="iframe.m3u8"\n');
    else if (request.url === '/root.mpd') manifest(response, `<MPD><Period><AdaptationSet contentType="video"><Representation id="v">
<BaseURL>asset.mp4</BaseURL><SegmentBase indexRange="0-43"/></Representation></AdaptationSet></Period></MPD>`, 'application/dash+xml');
    else binary(request, response, sidx(0, true));
  });
  const media = service(t);
  for (const [value, path, code] of [
    ['unknown', '/untyped', 'MEDIA_PREFETCH_UNSUPPORTED'], ['http', '/clip.mp4', 'MEDIA_PREFETCH_HTTP_ERROR'],
    ['empty', '/clip.mp4', 'MEDIA_EMPTY_RESPONSE'], ['hls', '/root.m3u8', 'MEDIA_PREFETCH_UNSUPPORTED'],
    ['index', '/root.mpd', 'MEDIA_PREFETCH_UNSUPPORTED'],
  ]) {
    mode = value;
    const receipt = await media.prepare({ url: upstream.url + path, headers: credentials }, { cache: true });
    await assert.rejects(media.prefetch(receipt.id), error => {
      assert.equal(error.code, code); assert.equal(error.message, code); return true;
    });
    await media.release(receipt.id);
  }
  assert.ok(upstream.requests.every(request => request.headers.cookie === credentials.Cookie));
});

test('E34/E40/E48: multiple untyped DASH representations fail prefetch rather than guessing which audio/video track is required', async t => {
  const upstream = await source(t, (request, response) => manifest(response,
    `<MPD><Period><AdaptationSet><Representation id="one"${request.url === '/mixed.mpd' ? ' mimeType="audio/mp4"' : ''}>` +
    '<BaseURL>one.mp4</BaseURL></Representation><Representation id="two"><BaseURL>two.mp4</BaseURL></Representation>' +
    '</AdaptationSet></Period></MPD>', 'application/dash+xml'));
  const media = service(t);
  for (const path of ['/root.mpd', '/mixed.mpd']) {
    const count = upstream.requests.length, receipt = await media.prepare({ url: upstream.url + path }, { cache: true });
    assert.equal(upstream.requests.length, count);
    await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_PREFETCH_UNSUPPORTED' });
  }
  assert.deepEqual(upstream.requests.map(request => request.url), ['/root.mpd', '/mixed.mpd']);
});

test('E34/E41: an ignored progressive prefix Range is not converted into a whole-movie prefetch', async t => {
  let sent = 0;
  const stopped = deferred();
  const upstream = await source(t, (_request, response) => {
    response.once('close', () => stopped.resolve());
    response.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': 128 * 1024 * 1024 });
    response.write(Buffer.alloc(65536, 1)); sent += 65536;
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/movie.mp4` }, { cache: true });
  await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_RANGE_UNSUPPORTED' });
  await stopped.promise;
  assert.equal(sent, 65536);
  await media.release(receipt.id);
});

test('E34/E43: cancelPrefetch drains only prefetch and preserves the same playable media owner until explicit release', { timeout: 10000 }, async t => {
  const entered = deferred(), stopped = deferred(), state = {};
  const upstream = await source(t, (request, response) => {
    if (!request.headers.range) { endless(response, state); return; }
    response.once('close', () => stopped.resolve());
    response.writeHead(206, { 'Content-Type': 'video/mp4', 'Content-Length': 1024, 'Content-Range': 'bytes 0-1023/10485760' });
    response.write(bytes.subarray(0, 32)); entered.resolve();
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/movie.mp4`, headers: credentials }, { cache: true });
  const playing = await paused(receipt.url);
  t.after(() => playing.request.destroy());
  const buffering = media.prefetch(receipt.id), rejected = assert.rejects(buffering, { code: 'MEDIA_CANCELLED' });
  await entered.promise;
  await media.cancelPrefetch(receipt.id);
  await rejected; await stopped.promise;
  assert.equal(state.stopped, undefined);
  playing.incoming.resume(); await once(playing.incoming, 'data'); playing.incoming.pause();
  await media.release(receipt.id); await state.closed.promise;
  assert.equal(state.stopped, true);
  await media.cancelPrefetch(receipt.id); await media.release(receipt.id);
});

test('E34/E42/E43: exact scope invalidation releases prefetched bytes and active prefetch without touching another owner', async t => {
  const entered = deferred(), stopped = deferred();
  const upstream = await source(t, (request, response) => {
    if (request.url === '/pending.mp4') {
      response.once('close', () => stopped.resolve());
      response.writeHead(206, { 'Content-Type': 'video/mp4', 'Content-Length': 1024, 'Content-Range': 'bytes 0-1023/1024' });
      response.write(bytes.subarray(0, 32)); entered.resolve();
    } else binary(request, response);
  });
  const media = service(t);
  const retained = await media.prepare({ url: `${upstream.url}/retained.mp4` },
    { cache: true, scope: { sourceKey: 'A', sourceRevision: 1, accountProvider: 'quark', accountRevision: 2 } });
  await media.prefetch(retained.id);
  const pending = await media.prepare({ url: `${upstream.url}/pending.mp4` },
    { cache: true, scope: { sourceKey: 'B', sourceRevision: 1, accountProvider: 'quark', accountRevision: 1 } });
  const rejected = assert.rejects(media.prefetch(pending.id), { code: 'MEDIA_CANCELLED' });
  await entered.promise;
  await media.invalidate(scope => scope.accountProvider === 'quark' && scope.accountRevision === 1);
  await rejected; await stopped.promise;
  assert.equal((await read(pending.url)).status, 404);
  assert.deepEqual((await read(retained.url)).body, bytes);
  const count = upstream.requests.length;
  await media.prefetch(retained.id);
  assert.equal(upstream.requests.length, count);
  await media.invalidate(scope => scope.sourceKey === 'A' && scope.sourceRevision === 1);
  assert.equal((await read(retained.url)).status, 404);
  await Promise.all([media.release(retained.id), media.release(retained.id)]);
});

test('E34/E43/E48: close drains prefetch awaiting upstream headers and does not leave a usable old authority', async t => {
  const entered = deferred(), stopped = deferred();
  const upstream = await source(t, (_request, response) => {
    response.once('close', () => stopped.resolve()); entered.resolve();
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/movie.mp4` }, { cache: true });
  const rejected = assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_CANCELLED' });
  await entered.promise;
  await media.close(); await rejected; await stopped.promise;
  await assert.rejects(read(receipt.url));
  await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_CLOSED' });
  await media.close();
});

test('E34/E41: oversized fragment prefetch fails within the cache budget; ordinary Range playback is not size-limited', async t => {
  const state = {};
  const upstream = await source(t, (request, response) => {
    if (request.url === '/root.m3u8') {
      manifest(response, '#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nlarge.m4s\n#EXT-X-ENDLIST\n');
    } else if (request.headers.range) binary(request, response);
    else endless(response, state);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.m3u8`, headers: credentials }, { cache: true });
  await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_PREFETCH_LIMIT' });
  await state.closed.promise;
  assert.ok(state.produced < 32 * 1024 * 1024, 'a cache budget must stop the offered 512MiB transfer, not cache the full fragment');
  const list = await read(receipt.url);
  assert.deepEqual((await read(hlsLines(list.text)[0], { headers: { Range: 'bytes=0-63' } })).body, bytes.subarray(0, 64));
  await media.release(receipt.id);
  const ordinary = await media.prepare({ url: `${upstream.url}/large.m4s`, headers: credentials });
  assert.deepEqual((await read(ordinary.url, { headers: { Range: 'bytes=128-255' } })).body, bytes.subarray(128, 256));
});

test('E46/M4: ClearKey and license descriptors reach the trusted receipt unchanged without license requests or transport decryption', async t => {
  const upstream = await source(t, binary), media = service(t);
  const kid = Buffer.alloc(16, 0x31), key = Buffer.alloc(16, 0x42);
  for (const drm of [
    { type: 'clearkey', clearKeys: { [kid.toString('hex')]: key.toString('hex') } },
    { keySystem: 'org.w3.clearkey', keys: [{ kty: 'oct', kid: kid.toString('base64url'), k: key.toString('base64url') }] },
  ]) {
    const expected = structuredClone(drm), before = upstream.requests.length;
    const receipt = await media.prepare({ url: `${upstream.url}/encrypted.mp4`, headers: credentials, drm });
    assert.equal(upstream.requests.length, before, 'DRM preparation must not request media or licenses');
    assert.deepEqual(receipt.drm, expected);
    if (drm.clearKeys) drm.clearKeys[kid.toString('hex')] = 'caller-mutated';
    else drm.keys[0].k = 'caller-mutated';
    assert.deepEqual(receipt.drm, expected);
    assert.deepEqual((await read(receipt.url)).body, bytes, 'transport passes binary samples, not fake decrypted media');
    await media.release(receipt.id);
  }
  assert.equal(upstream.requests.length, 2, 'ClearKey media reads must not trigger license requests');
  for (const drm of [
    { keySystem: 'com.widevine.alpha', licenseUrl: `${upstream.url}/license` },
    { keySystem: 'org.w3.clearkey', licenseHeaders: { 'X-License-Key': 'synthetic-secret' } },
    { license: { url: `${upstream.url}/license`, header: { 'X-License-Key': 'synthetic-secret' } } },
  ]) {
    for (const headers of [undefined, credentials]) {
      const before = upstream.requests.length, expected = structuredClone(drm);
      const receipt = await media.prepare({ url: `${upstream.url}/encrypted.mp4`, headers, drm });
      assert.deepEqual(receipt.drm, expected, 'native consumers decide which DRM schemes they support');
      assert.equal(upstream.requests.length, before, 'a license descriptor must not cause preparation I/O');
      const result = await read(receipt.url);
      assert.equal(result.status, 200);
      assert.deepEqual(result.body, bytes, 'transport must leave the media samples unchanged');
      assert.deepEqual(upstream.requests.slice(before).map(request => ({ method: request.method, url: request.url })),
        [{ method: 'GET', url: '/encrypted.mp4' }], 'reading media must not trigger a license request');
      assert.equal(upstream.requests.at(-1).headers['x-license-key'], undefined);
      await media.release(receipt.id);
    }
  }
});

test('E34/E39: extensionless and compressed prefetch manifests keep the same authorized cache graph', async t => {
  const text = '#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nsegment.m4s\n#EXT-X-ENDLIST\n';
  const upstream = await source(t, (request, response) => {
    if (request.url === '/extensionless') manifest(response, text, 'application/octet-stream');
    else if (request.url === '/compressed.m3u8') {
      const body = gzipSync(text);
      response.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl', 'Content-Encoding': 'gzip',
        'Content-Length': body.length });
      response.end(body);
    } else binary(request, response);
  });
  const media = service(t);
  for (const path of ['/extensionless', '/compressed.m3u8']) {
    const before = upstream.requests.length;
    const receipt = await media.prepare({ url: upstream.url + path, headers: credentials }, { cache: true });
    assert.equal(upstream.requests.length, before);
    assert.deepEqual(await media.prefetch(receipt.id), { bytes: bytes.length });
    const list = await read(receipt.url);
    assert.equal(list.headers['content-encoding'], undefined);
    assert.deepEqual((await read(hlsLines(list.text)[0])).body, bytes);
    assert.equal(upstream.requests.length, before + 2);
    await media.release(receipt.id);
  }
});

test('E34/E48: a truncated startup fragment fails and can be retried without caching partial success', async t => {
  let broken = true;
  const upstream = await source(t, (request, response) => {
    if (request.url === '/root.m3u8') {
      manifest(response, '#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nsegment.m4s\n#EXT-X-ENDLIST\n');
    } else if (broken) {
      response.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': bytes.length });
      response.write(bytes.subarray(0, 3)); setImmediate(() => response.destroy());
    } else binary(request, response);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/root.m3u8`, headers: credentials }, { cache: true });
  await assert.rejects(media.prefetch(receipt.id), { code: 'MEDIA_UPSTREAM_ERROR' });
  broken = false;
  assert.deepEqual(await media.prefetch(receipt.id), { bytes: bytes.length });
  const list = await read(receipt.url);
  assert.deepEqual((await read(hlsLines(list.text)[0])).body, bytes);
  assert.equal(upstream.requests.length, 3, 'retry reuses the valid manifest but must redownload the failed fragment');
});

test('E39/E48: a fragmented short manifest signature is recognized; an unknown XML body is not binary success', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/fragmented') {
      response.writeHead(200, { 'Content-Type': 'application/octet-stream' });
      response.write('#EX'); setImmediate(() => response.end('TM3U\n#EXTINF:3,\nsegment.ts\n'));
    } else if (request.url === '/unknown') manifest(response, '<UnrecognizedMedia/>', 'application/octet-stream');
    else binary(request, response);
  });
  const media = service(t), known = await media.prepare({ url: `${upstream.url}/fragmented`, headers: credentials });
  const list = await read(known.url);
  assert.equal(list.status, 200);
  assert.deepEqual((await read(hlsLines(list.text)[0])).body, bytes);
  const unknown = await media.prepare({ url: `${upstream.url}/unknown`, headers: credentials });
  const result = await read(unknown.url);
  assert.equal(result.status, 502);
  assert.equal(result.text, 'MEDIA_MANIFEST_UNSUPPORTED');
});

test('E34/E48: an expired live playlist is refreshed for playback rather than frozen by prefetch', async t => {
  let sequence = 1;
  const upstream = await source(t, (request, response) => {
    if (request.url === '/live.m3u8') manifest(response,
      `#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:${sequence}\n#EXTINF:3,\nsegment-${sequence}.m4s\n`);
    else binary(request, response);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/live.m3u8`, headers: credentials }, { cache: true });
  await media.prefetch(receipt.id);
  sequence = 2;
  const list = await read(receipt.url);
  assert.match(list.text, /#EXT-X-MEDIA-SEQUENCE:2/);
  assert.deepEqual((await read(hlsLines(list.text)[0])).body, bytes);
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/live.m3u8', '/segment-1.m4s', '/live.m3u8', '/segment-2.m4s']);
});

test('E34/E48: a changed progressive validator fails instead of stitching cached bytes to a different movie', async t => {
  let revision = 1;
  const movie = Buffer.alloc(2 * 1024 * 1024, 7);
  const upstream = await source(t, (request, response) => {
    response.setHeader('ETag', `"synthetic-revision-${revision}"`);
    binary(request, response, movie);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/movie.mp4` }, { cache: true });
  await media.prefetch(receipt.id);
  revision = 2;
  const result = await read(receipt.url);
  assert.equal(result.status, 502);
  assert.equal(result.text, 'MEDIA_RESOURCE_CHANGED');
  assert.equal(upstream.requests.length, 2);
});

test('E34/E41/E48: Last-Modified guards continuation and explicit identity encoding is equivalent to an absent encoding', async t => {
  const data = Buffer.alloc(2 * 1024 * 1024, 0x63), initial = 'Sat, 07 Nov 2026 00:00:00 GMT';
  const upstream = await source(t, (request, response) => {
    const continuation = request.headers['if-range'] !== undefined;
    response.setHeader('Last-Modified', request.url === '/changed.mp4' && continuation
      ? 'Sun, 08 Nov 2026 00:00:00 GMT' : initial);
    if (!continuation) response.setHeader('Content-Encoding', 'identity');
    binary(request, response, data);
  });
  const media = service(t);
  const stable = await media.prepare({ url: `${upstream.url}/stable.mp4` }, { cache: true });
  await media.prefetch(stable.id);
  assert.deepEqual((await read(stable.url)).body, data);
  const changed = await media.prepare({ url: `${upstream.url}/changed.mp4` }, { cache: true });
  await media.prefetch(changed.id);
  const result = await read(changed.url);
  assert.equal(result.status, 502);
  assert.equal(result.text, 'MEDIA_RESOURCE_CHANGED');
  assert.deepEqual(upstream.requests.map(request => request.headers['if-range']),
    [undefined, initial, undefined, initial]);
});

test('E34/E41/E48: weak ETag continuation uses Last-Modified, never redownloads the prefix and still rejects changed media', async t => {
  const data = Buffer.alloc(3 * 1024 * 1024, 0x73), tag = 'W/"synthetic-weak"';
  const modified = 'Sat, 07 Nov 2026 00:00:00 GMT', changed = 'Sun, 08 Nov 2026 00:00:00 GMT';
  const upstream = await source(t, (request, response) => {
    const lastModified = request.url === '/changed.mp4' && request.headers['if-range'] ? changed : modified;
    response.setHeader('ETag', tag);
    response.setHeader('Last-Modified', lastModified);
    if (request.headers['if-range'] && request.headers['if-range'] !== lastModified) {
      response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': data.length });
      return response.end(data);
    }
    binary(request, response, data);
  });
  const media = service(t);
  const stable = await media.prepare({ url: `${upstream.url}/stable.mp4` }, { cache: true });
  const prefix = await media.prefetch(stable.id);
  const cached = await read(stable.url, { headers: { Range: 'bytes=32-63', 'If-Range': modified } });
  assert.equal(cached.status, 206);
  assert.deepEqual(cached.body, data.subarray(32, 64));
  assert.equal(upstream.requests.length, 1, 'a valid date condition can use the cached prefix');
  const whole = await read(stable.url);
  assert.equal(whole.status, 200);
  assert.equal(whole.headers.etag, tag, 'the weak tag remains weak in response metadata');
  assert.deepEqual(whole.body, data);
  assert.deepEqual(upstream.requests.map(request => [request.url, request.headers.range, request.headers['if-range']]),
    [['/stable.mp4', `bytes=0-${prefix.bytes - 1}`, undefined],
      ['/stable.mp4', `bytes=${prefix.bytes}-${data.length - 1}`, modified]]);
  const changing = await media.prepare({ url: `${upstream.url}/changed.mp4` }, { cache: true });
  const changingPrefix = await media.prefetch(changing.id);
  const result = await read(changing.url);
  assert.equal(result.status, 502);
  assert.equal(result.text, 'MEDIA_RESOURCE_CHANGED');
  assert.deepEqual(upstream.requests.slice(2).map(request => [request.headers.range, request.headers['if-range']]),
    [[`bytes=0-${changingPrefix.bytes - 1}`, undefined],
      [`bytes=${changingPrefix.bytes}-${data.length - 1}`, modified]]);
});

test('E34/E41: weak ETag without a valid date never generates If-Range or repeats cached startup bytes', async t => {
  const data = Buffer.alloc(2 * 1024 * 1024, 0x61), tag = 'W/"date-unavailable"';
  const upstream = await source(t, (request, response) => {
    response.setHeader('ETag', tag);
    if (request.url === '/invalid-date.mp4') response.setHeader('Last-Modified', '2026-11-07');
    if (request.headers['if-range']) {
      response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': data.length });
      return response.end(data);
    }
    binary(request, response, data);
  });
  const media = service(t);
  for (const path of ['/no-date.mp4', '/invalid-date.mp4']) {
    const receipt = await media.prepare({ url: upstream.url + path }, { cache: true });
    const prefix = await media.prefetch(receipt.id), result = await read(receipt.url);
    assert.equal(result.status, 200);
    assert.equal(result.headers.etag, tag);
    assert.deepEqual(result.body, data);
    assert.deepEqual(upstream.requests.filter(request => request.url === path)
      .map(request => [request.headers.range, request.headers['if-range']]),
    [[`bytes=0-${prefix.bytes - 1}`, undefined], [`bytes=${prefix.bytes}-${data.length - 1}`, undefined]]);
  }
});

test('E34/E41: inbound weak If-Range cannot yield a cached 206 even when the weak tag is identical', async t => {
  const data = Buffer.alloc(2 * 1024 * 1024, 0x29), tag = 'W/"inbound-weak"';
  const upstream = await source(t, (request, response) => {
    response.setHeader('ETag', tag);
    if (request.headers['if-range']) {
      response.writeHead(200, { 'content-type': 'video/mp4', 'content-length': data.length });
      return response.end(data);
    }
    binary(request, response, data);
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/movie.mp4` }, { cache: true });
  await media.prefetch(receipt.id);
  const result = await read(receipt.url, { headers: { Range: 'bytes=32-63', 'If-Range': tag } });
  assert.equal(result.status, 200);
  assert.equal(result.headers['content-range'], undefined);
  assert.equal(result.headers.etag, tag);
  assert.deepEqual(result.body, data);
  assert.equal(upstream.requests.length, 2);
  assert.equal(upstream.requests[1].headers.range, 'bytes=32-63');
  assert.equal(upstream.requests[1].headers['if-range'], tag, 'the original weak condition is not upgraded or hidden');
});

test('E42/E43: source installation and file-account scope survive prepare and only their exact predicates revoke media', async t => {
  const upstream = await source(t, binary), media = service(t);
  const receipts = [];
  const scopes = [
    { sourceKey: 'A', sourceRevision: 7, sourceEngine: 'cat', sourceInstallationId: 'install-A',
      accountProvider: 'quark_tv', accountRevision: 4, fileProvider: 'quark', fileAccountRevision: 2 },
    { sourceKey: 'B', sourceRevision: 7, sourceEngine: 'cat', sourceInstallationId: 'install-B',
      accountProvider: 'quark_tv', accountRevision: 4, fileProvider: 'quark', fileAccountRevision: 3 },
    { sourceKey: 'C', sourceRevision: 7, sourceEngine: 'source', sourceInstallationId: 'install-A',
      accountProvider: 'uc', accountRevision: 2, fileProvider: 'uc', fileAccountRevision: 2 },
  ];
  for (const scope of scopes) receipts.push(await media.prepare({ url: `${upstream.url}/${scope.sourceKey}.mp4`,
    headers: credentials }, { scope, cache: true }));
  scopes[0].sourceInstallationId = 'caller-mutated';
  const seen = [];
  await media.invalidate(scope => {
    seen.push(scope);
    return scope.sourceEngine === 'cat' && scope.sourceInstallationId === 'install-A';
  });
  assert.deepEqual(seen[0], { ...scopes[0], sourceInstallationId: 'install-A' });
  assert.equal((await read(receipts[0].url)).status, 404);
  assert.deepEqual((await read(receipts[1].url)).body, bytes);
  assert.deepEqual((await read(receipts[2].url)).body, bytes);
  await media.invalidate(scope => scope.fileProvider === 'quark' && scope.fileAccountRevision === 2);
  assert.deepEqual((await read(receipts[1].url)).body, bytes, 'a different ordinary file account must retain its TV media stream');
  await media.invalidate(scope => scope.fileProvider === 'quark' && scope.fileAccountRevision === 3);
  assert.equal((await read(receipts[1].url)).status, 404);
  assert.deepEqual((await read(receipts[2].url)).body, bytes);
  await media.invalidate(scope => scope.accountProvider === 'uc' && scope.accountRevision === 2);
  assert.equal((await read(receipts[2].url)).status, 404);
});

test('MCR-01/E39: EXTINF free titles remain byte-exact while their segments keep the required headers', async t => {
  const titles = [
    '#EXTINF:3,URI=opening',
    '#EXTINF:3,  first,SERVER-URI=display,URI="title only" \t',
    '#EXTINF:3,原始标题,URI=https://synthetic.invalid/display-only  ',
  ];
  const text = ['#EXTM3U', '#EXT-X-TARGETDURATION:3',
    ...titles.flatMap((title, index) => [title, `segment-${index}.ts`]), '#EXT-X-ENDLIST', ''].join('\n');
  const upstream = await source(t, (request, response) => {
    assert.equal(request.headers.cookie, credentials.Cookie);
    if (request.url === '/list.m3u8') manifest(response, text);
    else {
      assert.match(request.url, /^\/segment-[0-2]\.ts$/);
      binary(request, response);
    }
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/list.m3u8`, headers: credentials });
  assert.equal(upstream.requests.length, 0);
  const playlist = await read(receipt.url);
  assert.equal(playlist.status, 200);
  assert.deepEqual(playlist.text.split('\n').filter(line => line.startsWith('#EXTINF:')), titles);
  const segments = hlsLines(playlist.text);
  assert.equal(segments.length, titles.length);
  for (const segment of segments) {
    assert.equal(new URL(segment).origin, new URL(receipt.url).origin);
    assert.deepEqual((await read(segment, { headers: { Range: 'bytes=16-31' } })).body, bytes.subarray(16, 32));
  }
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/list.m3u8', '/segment-0.ts', '/segment-1.ts', '/segment-2.ts']);
});

test('MCR-01/E39: known and unknown attribute lists still register URI resources without rewriting quoted metadata', async t => {
  const metadata = '#EXT-X-DATERANGE:ID="chapter",START-DATE="2026-11-09T00:00:00Z",X-NOTE="chapter,URI=display-only"';
  const comment = '# comment: URI=https://synthetic.invalid/not-a-resource';
  const text = ['#EXTM3U', '#EXT-X-TARGETDURATION:3', metadata, comment,
    '#EXT-X-KEY:METHOD=AES-128,URI="key.bin"',
    '#EXT-X-MAP:URI="init.mp4"',
    '#EXT-X-FUTURE:CLASS="custom",URI="future.bin"',
    '#EXT-X-FUTURE-TIMING:SERVER-URI="clock"',
    '#EXTINF:3,', 'segment.ts', '#EXT-X-ENDLIST', ''].join('\n');
  const upstream = await source(t, (request, response) => {
    assert.equal(request.headers.authorization, credentials.Authorization);
    if (request.url === '/list.m3u8') manifest(response, text);
    else {
      assert.ok(['/key.bin', '/init.mp4', '/future.bin', '/clock'].includes(request.url));
      binary(request, response);
    }
  });
  const media = service(t), receipt = await media.prepare({ url: `${upstream.url}/list.m3u8`, headers: credentials });
  const playlist = await read(receipt.url);
  assert.equal(playlist.status, 200);
  assert.ok(playlist.text.split('\n').includes(metadata));
  assert.ok(playlist.text.split('\n').includes(comment));
  const children = uris(playlist.text);
  assert.equal(children.length, 4);
  for (const child of children) {
    assert.equal(new URL(child).origin, new URL(receipt.url).origin);
    assert.deepEqual((await read(child)).body, bytes);
  }
  assert.deepEqual(upstream.requests.map(request => request.url),
    ['/list.m3u8', '/key.bin', '/init.mp4', '/future.bin', '/clock']);
});

test('MCR-02/E40/E42/N3: standard HTTP UTC timing resources stay binary across hosts', async t => {
  const schemes = ['http-xsdate:2012', 'http-xsdate:2014', 'http-iso:2012', 'http-iso:2014',
    'http-head:2012', 'http-head:2014', 'http-ntp:2014'];
  const clock = Buffer.from([0, 255, 128, 1, 2, 3, 4, 5]), date = 'Wed, 01 Jan 2020 00:00:00 GMT';
  const respond = (request, response) => {
    response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': clock.length, Date: date });
    response.end(request.method === 'HEAD' ? undefined : clock);
  };
  const other = await source(t, respond);
  const upstream = await source(t, (request, response) => {
    if (request.url === '/doc/root.mpd') manifest(response, `<MPD>
${schemes.map((scheme, index) =>
    `<UTCTiming xml:base="clocks/" schemeIdUri="urn:mpeg:dash:utc:${scheme}" value="${index}"/>`).join('\n')}
<UTCTiming schemeIdUri="urn:mpeg:dash:utc:http-ntp:2014" value="${other.url}/clock"/>
<Period><AdaptationSet><Representation id="v"><BaseURL>movie.mp4</BaseURL></Representation></AdaptationSet></Period>
</MPD>`, 'application/dash+xml');
    else {
      assert.match(request.url, /^\/doc\/clocks\/[0-6](?:\?[^]*)?$/);
      respond(request, response);
    }
  });
  const media = service(t);
  for (const authorization of [credentials.Authorization, 'Bearer synthetic-second-media']) {
    const start = upstream.requests.length;
    const receipt = await media.prepare({ url: `${upstream.url}/doc/root.mpd`,
      headers: { ...credentials, Authorization: authorization } });
    assert.equal(upstream.requests.length, start);
    const mpd = await read(receipt.url);
    assert.equal(mpd.status, 200);
    const timings = [...mpd.text.matchAll(/<UTCTiming\b([^>]*)/g)].map(match => attributes(match[1]));
    assert.deepEqual(timings.map(timing => timing.schemeIdUri),
      [...schemes, 'http-ntp:2014'].map(scheme => `urn:mpeg:dash:utc:${scheme}`));
    for (const timing of timings) {
      assert.equal(new URL(timing.value).origin, new URL(receipt.url).origin);
      assert.equal(timing['xml:base'], undefined);
      const head = timing.schemeIdUri.includes(':http-head:');
      const result = await read(timing.value, { method: head ? 'HEAD' : 'GET' });
      assert.equal(result.status, 200);
      assert.equal(result.headers.date, date, 'http-head must expose the upstream clock, not a generated local Date');
      assert.deepEqual(result.body, head ? Buffer.alloc(0) : clock);
    }
    assert.deepEqual(upstream.requests.slice(start).map(request => [request.url, request.method]), [
      ['/doc/root.mpd', 'GET'],
      ...schemes.map((scheme, index) => [`/doc/clocks/${index}`, scheme.startsWith('http-head:') ? 'HEAD' : 'GET']),
    ]);
    assert.ok(upstream.requests.slice(start).every(request => request.headers.cookie === credentials.Cookie));
    assert.equal(other.requests.at(-1).headers.cookie, credentials.Cookie);
    assert.equal(other.requests.at(-1).headers.authorization, authorization);
    const count = upstream.requests.length + other.requests.length;
    const query = 'format=vendor-clock';
    assert.deepEqual((await read(`${timings[0].value}?${query}`)).body, clock);
    assert.equal(upstream.requests.at(-1).url, `/doc/clocks/0?${query}`);
    assert.equal(upstream.requests.length + other.requests.length, count + 1);
    await media.release(receipt.id);
  }
  assert.equal(other.requests.length, 2);
});

test('MCR-02/E40: unsupported network UTC timing fails explicitly while direct timing remains inline', async t => {
  let scheme, value;
  const upstream = await source(t, (request, response) => {
    assert.equal(request.url, '/root.mpd', 'unsupported timing must not make a guessed clock request');
    manifest(response, `<MPD><UTCTiming schemeIdUri="${scheme}" value="${value}"/>
<Period><AdaptationSet><Representation id="v"><BaseURL>movie.mp4</BaseURL></Representation></AdaptationSet></Period>
</MPD>`, 'application/dash+xml');
  });
  const media = service(t);
  for (const version of [2012, 2014]) {
    scheme = `urn:mpeg:dash:utc:direct:${version}`;
    value = '2020-01-01T00:00:00Z';
    const receipt = await media.prepare({ url: `${upstream.url}/root.mpd`, headers: credentials });
    const mpd = await read(receipt.url);
    assert.equal(mpd.status, 200);
    const timing = attributes(/<UTCTiming\b([^>]*)/.exec(mpd.text)[1]);
    assert.equal(timing.schemeIdUri, scheme);
    assert.equal(timing.value, value);
    await media.release(receipt.id);
  }
  for (const unsupported of ['urn:mpeg:dash:utc:ntp:2014', 'urn:mpeg:dash:utc:sntp:2014',
    'urn:mpeg:dash:utc:http-unknown:2014', 'https://synthetic.invalid/timing-scheme']) {
    scheme = unsupported; value = `${upstream.url}/clock`;
    const count = upstream.requests.length;
    const receipt = await media.prepare({ url: `${upstream.url}/root.mpd`, headers: credentials });
    const result = await read(receipt.url);
    assert.equal(result.status, 502);
    assert.equal(result.text, 'MEDIA_MANIFEST_UNSUPPORTED');
    assert.equal(upstream.requests.length, count + 1);
    await media.release(receipt.id);
  }
  assert.equal(upstream.requests.length, 6);
});

test('MCR-03/E41: rewritten HLS and DASH ignore conditional ranges with and without prefetched documents', async t => {
  const upstream = await source(t, (request, response) => {
    if (request.url === '/list.m3u8' || request.url === '/list.mpd') {
      response.setHeader('ETag', '"upstream-control-version"');
      response.setHeader('Last-Modified', 'Wed, 01 Jan 2020 00:00:00 GMT');
      if (request.url === '/list.m3u8') manifest(response,
        '#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nsegment.ts\n#EXT-X-ENDLIST\n');
      else manifest(response, '<MPD type="static"><Period><AdaptationSet contentType="video">' +
        '<SegmentTemplate initialization="init.mp4" media="chunk-$Number$.m4s"/>' +
        '<Representation id="v" bandwidth="1"/></AdaptationSet></Period></MPD>', 'application/dash+xml');
    } else binary(request, response);
  });
  const media = service(t);
  for (const cache of [false, true]) for (const format of ['m3u8', 'mpd']) {
    const receipt = await media.prepare({ url: `${upstream.url}/list.${format}`, headers: credentials }, { cache });
    if (cache) assert.ok((await media.prefetch(receipt.id)).bytes > 0);
    const start = upstream.requests.length;
    const full = await read(receipt.url);
    assert.equal(full.status, 200);
    for (const [range, validator] of [['bytes=0-6', '"old-control-version"'],
      ['bytes=999999-', 'Tue, 01 Jan 2030 00:00:00 GMT'], ['bytes=0-6', '']]) {
      const result = await read(receipt.url, { headers: { Range: range, 'If-Range': validator } });
      assert.equal(result.status, 200);
      assert.deepEqual(result.body, full.body);
      assert.equal(result.headers['content-length'], String(full.body.length));
      for (const name of ['content-range', 'etag', 'last-modified']) assert.equal(result.headers[name], undefined);
    }
    const part = await read(receipt.url, { headers: { Range: 'bytes=0-6' } });
    assert.equal(part.status, 206);
    assert.deepEqual(part.body, full.body.subarray(0, 7));
    assert.equal(part.headers['content-range'], `bytes 0-6/${full.body.length}`);
    assert.equal((await read(receipt.url, { headers: { Range: 'bytes=999999-' } })).status, 416);
    assert.equal(upstream.requests.length, cache ? start : start + 6);
    assert.ok(upstream.requests.slice(start).every(request =>
      request.headers.range === undefined && request.headers['if-range'] === undefined));
    await media.release(receipt.id);
  }
  assert.ok(upstream.requests.every(request => request.headers.cookie === credentials.Cookie));
});

test('MCR-04/E41: extensionless text playlists omit unknown HEAD representation metadata without a body probe', async t => {
  const documents = {
    '/hls': '#EXTM3U\n#EXTINF:3,\nsegment.ts\n',
    '/dash': '<MPD><Period><AdaptationSet><Representation id="v"><BaseURL>movie.mp4</BaseURL>' +
      '</Representation></AdaptationSet></Period></MPD>',
  };
  const upstream = await source(t, (request, response) => {
    assert.equal(request.headers.cookie, credentials.Cookie);
    const text = documents[request.url];
    assert.ok(text, 'HEAD must not probe a media child');
    response.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8', 'Content-Length': Buffer.byteLength(text),
      ETag: '"upstream-text-version"', 'Last-Modified': 'Wed, 01 Jan 2020 00:00:00 GMT',
      'Content-Encoding': 'identity', 'Accept-Ranges': 'bytes' });
    response.end(request.method === 'HEAD' ? undefined : text);
  });
  const media = service(t);
  for (const [path, text] of Object.entries(documents)) {
    const start = upstream.requests.length;
    const receipt = await media.prepare({ url: `${upstream.url}${path}`, headers: credentials });
    assert.equal(upstream.requests.length, start);
    const head = await read(receipt.url, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(head.body.length, 0);
    for (const name of ['content-length', 'content-range', 'etag', 'last-modified', 'content-encoding', 'accept-ranges']) {
      assert.equal(head.headers[name], undefined);
    }
    assert.deepEqual(upstream.requests.slice(start).map(request => [request.method, request.url]), [['HEAD', path]]);
    const full = await read(receipt.url);
    assert.equal(full.status, 200);
    assert.equal(full.headers['content-type'], path === '/hls' ? 'application/vnd.apple.mpegurl' : 'application/dash+xml');
    assert.equal(full.headers['content-length'], String(full.body.length));
    assert.notEqual(full.body.length, Buffer.byteLength(text));
    assert.equal(full.headers.etag, undefined);
    assert.equal(full.headers['last-modified'], undefined);
    assert.deepEqual(upstream.requests.slice(start).map(request => [request.method, request.url]), [['HEAD', path], ['GET', path]]);
  }
});

test('MCR-04/E41: explicit binary HEAD responses retain their exact length and validators', async t => {
  const validator = '"synthetic-binary-version"', modified = 'Wed, 01 Jan 2020 00:00:00 GMT';
  const specifications = [
    { type: 'video/mp4' }, { type: 'Audio/MPEG; charset=binary' }, { type: 'application/mp4' },
    { type: 'application/ogg' }, { type: 'application/webm' },
    { type: 'application/octet-stream', format: 'binary' },
    { format: 'progressive' }, { format: 'mp4' }, { format: 'mkv' }, { format: 'webm' },
  ];
  const upstream = await source(t, (request, response) => {
    assert.equal(request.method, 'HEAD', 'binary HEAD must not trigger a body probe');
    assert.equal(request.headers.cookie, credentials.Cookie);
    const specification = specifications[Number(request.url.slice(1))];
    assert.ok(specification);
    response.writeHead(200, { 'Content-Length': bytes.length, ETag: validator, 'Last-Modified': modified,
      'Accept-Ranges': 'bytes', ...(specification.type ? { 'Content-Type': specification.type } : {}) });
    response.end();
  });
  const media = service(t);
  for (const [index, specification] of specifications.entries()) {
    const receipt = await media.prepare({ url: `${upstream.url}/${index}`, headers: credentials,
      ...(specification.format ? { format: specification.format } : {}) });
    const head = await read(receipt.url, { method: 'HEAD' });
    assert.equal(head.status, 200);
    assert.equal(head.body.length, 0);
    assert.equal(head.headers['content-length'], String(bytes.length));
    assert.equal(head.headers.etag, validator);
    assert.equal(head.headers['last-modified'], modified);
    assert.equal(head.headers['accept-ranges'], 'bytes');
    if (specification.type) assert.equal(head.headers['content-type'], specification.type);
  }
  assert.equal(upstream.requests.length, specifications.length);
});
