import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { randomBytes } from 'node:crypto';
import { Gunzip, gzipSync, deflateSync, brotliCompressSync } from 'node:zlib';
import test from 'node:test';
import { createSourceDownload } from '../src/source-download.js';
import { downloadCatSource } from '../src/cat-source-download.js';
import { deferred } from '../src/errors.js';

const sourceLimit = 16 * 1024 * 1024;
const validateURL = value => {
  const url = new URL(value);
  assert.ok(['http:', 'https:'].includes(url.protocol));
  return url.href;
};
async function serverFor(t, handler) {
  const sockets = new Set();
  const server = http.createServer(handler);
  server.on('connection', socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(async () => {
    for (const socket of sockets) socket.destroy();
    await new Promise(resolve => server.close(resolve));
  });
  return { server, url: `http://127.0.0.1:${server.address().port}` };
}
function request(url, extra = {}) {
  return { url, options: {}, signal: new AbortController().signal, validateURL, ...extra };
}

function observeDownloads(t, beforeRequest) {
  const hops = [], listeners = [];
  const listen = (stream, event, listener) => {
    stream.once(event, listener);
    listeners.push(() => stream.removeListener(event, listener));
  };
  t.after(() => {
    t.mock.restoreAll();
    for (const remove of listeners) remove();
  });
  const originalRequest = http.request;
  t.mock.method(http, 'request', function (...args) {
    beforeRequest?.(hops);
    const outgoing = Reflect.apply(originalRequest, this, args);
    const hop = { resources: new Map(), body: deferred(), decompressed: deferred() };
    hops.push(hop);
    const observe = (name, stream) => {
      assert.equal(hop.resources.has(name), false, `duplicate ${name}`);
      const state = { closed: false };
      hop.resources.set(name, state);
      listen(stream, 'close', () => { state.closed = true; });
      if (!['response', 'wire', 'Gunzip'].includes(name)) return;
      const originalPipe = stream.pipe;
      t.mock.method(stream, 'pipe', function (destination, ...options) {
        observe(name === 'response' ? 'wire' : destination instanceof Gunzip ? 'Gunzip' : 'sink', destination);
        const result = Reflect.apply(originalPipe, this, [destination, ...options]);
        // Observe after the real pipe's consumer; never start an unpiped stream.
        if (name === 'wire' || name === 'Gunzip') {
          listen(stream, 'data', chunk => {
            (name === 'Gunzip' ? hop.decompressed : hop.body).resolve(chunk.length);
          });
        }
        return result;
      });
    };
    observe('request', outgoing);
    listen(outgoing, 'socket', socket => observe('socket', socket));
    listen(outgoing, 'response', response => observe('response', response));
    return outgoing;
  });
  return hops;
}

function assertClosed(hop, expected) {
  assert.ok(hop.resources.has('request'));
  assert.ok(hop.resources.has('socket'));
  if (expected) assert.deepEqual([...hop.resources.keys()].sort(), [...expected].sort());
  for (const [name, state] of hop.resources) assert.equal(state.closed, true, `${name} close before settlement/next hop`);
}

function closedAtSettlement(work, hops) {
  const check = () => {
    assert.ok(hops.length > 0);
    for (const hop of hops) assertClosed(hop);
  };
  return work.then(
    value => { check(); return value; },
    error => { check(); throw error; },
  );
}

test('source downloads accept exactly 16 MiB and reject the next byte', { timeout: 15000 }, async t => {
  const { url } = await serverFor(t, (req, res) => res.end(Buffer.alloc(sourceLimit + Number(req.url === '/over'), 97)));
  const download = createSourceDownload({ maxSourceBytes: sourceLimit });
  const result = await download(request(url));
  assert.equal(result.body.length, sourceLimit);
  assert.ok(result.body.equals(Buffer.alloc(sourceLimit, 97)));
  assert.equal(result.url, `${url}/`);
  await assert.rejects(download(request(`${url}/over`)), { code: 'RESOURCE_LIMIT' });
});

for (const [encoding, encode] of [['gzip', gzipSync], ['deflate', deflateSync], ['br', brotliCompressSync]]) {
  test(`source ${encoding} decoding keeps exact bytes and enforces decoded source size`, async t => {
    const body = Buffer.from('/* 猫源 😀 */'.repeat(1000));
    const { url } = await serverFor(t, (req, res) => {
      const compressed = encode(req.url === '/over' ? Buffer.concat([body, Buffer.from('x')]) : body);
      res.writeHead(200, { 'content-encoding': encoding, 'content-length': compressed.length, 'x-source': 'fixture' });
      res.end(compressed);
    });
    const download = createSourceDownload({ maxSourceBytes: body.length });
    const result = await download(request(url));
    assert.deepEqual(result.body, body);
    assert.equal(result.headers['x-source'], 'fixture');
    assert.equal(result.headers['content-encoding'], undefined);
    assert.equal(result.headers['content-length'], undefined);
    await assert.rejects(download(request(`${url}/over`)), { code: 'RESOURCE_LIMIT' });
  });
}

test('compressed source is also bounded on the wire and malformed encoding fails explicitly', async t => {
  const body = randomBytes(1024), compressed = gzipSync(body);
  assert.ok(compressed.length > body.length);
  const { url } = await serverFor(t, (req, res) => {
    res.writeHead(200, { 'content-encoding': req.url === '/unknown' ? 'unsupported' : 'gzip' });
    res.end(req.url === '/over' ? compressed : 'not-gzip');
  });
  const download = createSourceDownload({ maxSourceBytes: body.length });
  await assert.rejects(download(request(`${url}/over`)), { code: 'RESOURCE_LIMIT' });
  await assert.rejects(download(request(`${url}/broken`)), { code: 'NETWORK_ERROR' });
  await assert.rejects(download(request(`${url}/unknown`)), { code: 'UNSUPPORTED_OPTION' });
});

test('each redirect is validated before contact; policies can return redirects without following', async t => {
  const hits = [], validated = [];
  const { url } = await serverFor(t, (req, res) => {
    hits.push(req.url);
    if (req.url === '/target') res.end('/* valid */');
    else { res.writeHead(302, { location: '/target' }); res.end('redirect'); }
  });
  const validate = value => { validated.push(value); return validateURL(value); };
  const download = createSourceDownload({ maxSourceBytes: 1024 });
  assert.equal((await download(request(url, { validateURL: validate }))).body.toString(), '/* valid */');
  assert.deepEqual(hits, ['/', '/target']);
  assert.deepEqual(validated, [url, `${url}/target`]);
  const result = await download(request(url, { options: { followRedirect: false } }));
  assert.equal(result.statusCode, 302);
  assert.deepEqual(hits, ['/', '/target', '/']);
  assert.equal((await createSourceDownload({ maxSourceBytes: 1024, maxRedirects: 0 })(request(url))).statusCode, 302);
  const before = hits.length;
  await assert.rejects(download(request(url, { validateURL(value) {
    if (value.endsWith('/target')) throw new Error('validation refused');
    return validateURL(value);
  } })), { code: 'NETWORK_ERROR' });
  assert.deepEqual(hits.slice(before), ['/']);
});

test('source redirects close the unfinished hop before opening the next request', { timeout: 10000 }, async t => {
  const body = Buffer.from('/* 猫源 😀 */'), hits = [];
  const { url } = await serverFor(t, (req, res) => {
    hits.push(req.url);
    if (req.url === '/') {
      res.writeHead(302, { location: '/target' });
      res.flushHeaders();
    } else res.end(body);
  });
  let checkedPrevious = false;
  const hops = observeDownloads(t, previous => {
    if (previous.length === 0) return;
    assert.equal(previous.length, 1);
    assertClosed(previous[0], ['request', 'response', 'socket']);
    checkedPrevious = true;
  });
  const result = await closedAtSettlement(createSourceDownload({ maxSourceBytes: 1024 })(request(url)), hops);
  assert.equal(checkedPrevious, true);
  assert.equal(hops.length, 2);
  assert.deepEqual([...hops[1].resources.keys()].sort(), ['request', 'response', 'socket', 'wire', 'sink'].sort());
  assert.deepEqual(hits, ['/', '/target']);
  assert.equal(result.statusCode, 200);
  assert.equal(result.url, `${url}/target`);
  assert.deepEqual(result.body, body);
});

test('Cat source redirects retain the original policy and five-hop maximum', async t => {
  let hits = 0;
  const { url } = await serverFor(t, (req, res) => {
    hits++;
    const count = Number(req.url.slice(1));
    if (count === 5) res.end('/* source */');
    else { res.writeHead(302, { location: `/${count + 1}` }); res.end(); }
  });
  const options = { signal: new AbortController().signal, maxSourceBytes: 1024, timeoutMs: 5000 };
  assert.equal((await downloadCatSource(`${url}/0`, options)).toString(), '/* source */');
  assert.equal(hits, 6);
  await assert.rejects(downloadCatSource(`${url}/-1`, options), { code: 'RESOURCE_LIMIT' });
  assert.equal(hits, 12);
});

test('Cat source HTTP errors do not become executable source bytes', async t => {
  const { url } = await serverFor(t, (_req, res) => { res.writeHead(403); res.end('not-source'); });
  await assert.rejects(downloadCatSource(url, { signal: new AbortController().signal,
    maxSourceBytes: 1024, timeoutMs: 5000 }), { code: 'HTTP_ERROR' });
});

for (const stage of ['headers', 'body', 'compressed']) {
  test(`source cancellation drains the connection during ${stage}`, { timeout: 10000 }, async t => {
    const entered = deferred(), disconnected = deferred();
    const { url } = await serverFor(t, (_req, res) => {
      res.once('close', () => disconnected.resolve(res.writableFinished));
      if (stage === 'body') res.write('/* waiting');
      if (stage === 'compressed') {
        res.writeHead(200, { 'content-encoding': 'gzip' });
        res.write(gzipSync('/* waiting */').subarray(0, 12));
      }
      entered.resolve();
    });
    const controller = new AbortController();
    const download = createSourceDownload({ maxSourceBytes: sourceLimit });
    const hops = observeDownloads(t);
    const work = download(request(url, { signal: controller.signal }));
    const rejected = assert.rejects(closedAtSettlement(work, hops), { code: 'CANCELLED' });
    await entered.promise;
    assert.equal(hops.length, 1);
    const hop = hops[0];
    if (stage === 'body') assert.ok(await hop.body.promise > 0);
    if (stage === 'compressed') assert.ok(await hop.decompressed.promise > 0);
    const expected = stage === 'headers' ? ['request', 'socket'] : ['request', 'response', 'socket', 'wire', 'sink'];
    if (stage === 'compressed') expected.push('Gunzip');
    assert.deepEqual([...hop.resources.keys()].sort(), expected.sort());
    controller.abort();
    await rejected;
    assert.equal(await disconnected.promise, false);
  });
}

test('Cat source timeout closes an unfinished response without waiting for the server', { timeout: 10000 }, async t => {
  const disconnected = deferred();
  const { url } = await serverFor(t, (_req, res) => {
    res.once('close', () => disconnected.resolve(res.writableFinished));
    res.write('partial');
  });
  const hops = observeDownloads(t);
  await assert.rejects(closedAtSettlement(downloadCatSource(url, { signal: new AbortController().signal,
    maxSourceBytes: 1024, timeoutMs: 50 }), hops), { code: 'TIMEOUT' });
  assert.equal(hops.length, 1);
  assert.equal(await disconnected.promise, false);
});

test('source overflow closes the unfinished response rather than buffering to completion', { timeout: 10000 }, async t => {
  const disconnected = deferred();
  const { url } = await serverFor(t, (_req, res) => {
    res.once('close', () => disconnected.resolve(res.writableFinished));
    res.write(Buffer.alloc(1025, 97));
  });
  const hops = observeDownloads(t);
  await assert.rejects(closedAtSettlement(createSourceDownload({ maxSourceBytes: 1024 })(request(url)), hops),
    { code: 'RESOURCE_LIMIT' });
  assert.equal(hops.length, 1);
  assert.deepEqual([...hops[0].resources.keys()].sort(), ['request', 'response', 'socket', 'wire', 'sink'].sort());
  assert.equal(await disconnected.promise, false);
});

test('pre-cancelled source downloads never contact the endpoint and interrupted bodies fail', async t => {
  let hits = 0;
  const { url } = await serverFor(t, (_req, res) => {
    hits++;
    res.writeHead(200, { 'content-length': '20' });
    res.write('partial');
    setImmediate(() => res.destroy());
  });
  const controller = new AbortController();
  controller.abort();
  const download = createSourceDownload({ maxSourceBytes: 1024 });
  await assert.rejects(download(request(url, { signal: controller.signal })), { code: 'CANCELLED' });
  assert.equal(hits, 0);
  await assert.rejects(download(request(url)), { code: 'NETWORK_ERROR' });
  assert.equal(hits, 1);
});
