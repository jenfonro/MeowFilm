import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { PassThrough, Readable } from 'node:stream';
import { BridgeError, HTTP_PROTOCOL, createHttpClient, createHttpServer } from '../src/index.js';
import { credentials, deferred, pair, rawRequest, turn, until } from './http-fixture.js';

const contract = 'bytes.test.v1';
const codes = new Set(['UPSTREAM_FAILED']);
const success = (body, dispose = async () => {}, headers = { 'content-type': 'application/octet-stream' }) =>
  ({ status: 200, headers, body, dispose });
const invalidResponse = { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_RESPONSE' };

function exchange(baseUrl, identity, { path = '/v1/bytes', method = 'GET', body,
  headers = {}, pause = false, requestId = randomUUID() } = {}) {
  const head = deferred(), first = deferred(), ended = deferred(), closed = deferred(), chunks = [];
  const outgoingHeaders = { authorization: `Bearer ${identity.key}`, 'x-meow-session': identity.sessionId,
    'x-meow-request-id': requestId, ...(body === undefined ? {} : { 'content-length': Buffer.byteLength(body) }),
    ...headers };
  for (const name of Object.keys(outgoingHeaders)) if (outgoingHeaders[name] === undefined) delete outgoingHeaders[name];
  const fail = error => { head.reject(error); first.reject(error); ended.reject(error); };
  const request = http.request(baseUrl, { method, path, headers: outgoingHeaders }, response => {
    response.on('data', bytes => { chunks.push(bytes); first.resolve(bytes); });
    response.on('end', () => ended.resolve({ status: response.statusCode, headers: response.headers,
      body: Buffer.concat(chunks) }));
    response.on('error', fail);
    response.on('aborted', () => fail(new Error('CLOSED')));
    response.on('close', closed.resolve);
    if (pause) response.pause();
    head.resolve(response);
  });
  request.on('error', fail);
  request.end(body);
  return { request, requestId, head: head.promise, first: first.promise, ended: ended.promise,
    closed: closed.promise, chunks };
}

async function binaryServer(t, handler, { expectCleanupFailure = false, ...options } = {}) {
  const identity = credentials(), cleanup = [];
  const server = createHttpServer({ ...identity, resolve: ({ method, url }) => {
    if (url.search) return;
    if (method === 'GET' && url.pathname === '/v1/bytes') {
      return { responseType: 'stream', contract, codes, handler };
    }
    if (method === 'POST' && url.pathname === '/v1/json') {
      return { contract: HTTP_PROTOCOL, contentType: 'application/json',
        handler: input => ({ ok: true, contract: HTTP_PROTOCOL, value: input }) };
    }
    if (method === 'PUT' && url.pathname === '/v1/upload') {
      return { contract, codes: new Set(['LIMIT']), contentType: 'application/octet-stream',
        maximumBytes: 8, limitCode: 'LIMIT', status: reply => reply.ok ? 200 : 413,
        handler: bytes => ({ ok: true, contract, value: [...bytes] }) };
    }
  }, ...options });
  await server.start();
  t.after(async () => {
    for (const action of cleanup) await action();
    if (expectCleanupFailure) await assert.rejects(server.close(), { code: 'INVALID_RESPONSE' });
    else await server.close();
  });
  return { server, identity, cleanup: action => cleanup.push(action),
    request: options => exchange(server.baseUrl, identity, options) };
}

function assertJSON(reply, status, value) {
  assert.equal(reply.status, status);
  assert.equal(reply.headers['content-type'], 'application/json');
  assert.deepEqual(JSON.parse(reply.body.toString()), value);
}

test('raw bytes arrive before the tail and successful ownership waits for actual disposal', async t => {
  const source = new PassThrough(), disposing = deferred(), release = deferred();
  const first = Buffer.from([0, 0xff, 0xc0, 0x80]), tail = Buffer.from([0xfe, 1, 2]);
  let disposals = 0, signal;
  const fixture = await binaryServer(t, (_input, context) => {
    signal = context.signal;
    return success(source, async () => { disposals++; disposing.resolve(); await release.promise; }, {
      'Content-Type': 'application/octet-stream', 'Content-Length': first.length + tail.length,
      'Cache-Control': 'public, max-age=60', ETag: '"bytes-v1"',
      'Last-Modified': 'Wed, 01 Jan 2025 00:00:00 GMT',
    });
  });
  fixture.cleanup(() => { release.resolve(); source.destroy(); });
  const raw = fixture.request();
  source.write(first);
  assert.deepEqual(await raw.first, first);
  assert.equal(source.writableEnded, false);
  assert.equal(fixture.server.state.active, 1);
  assert.equal(disposals, 0);
  source.end(tail);
  const reply = await raw.ended;
  await disposing.promise;
  assert.equal(reply.status, 200);
  assert.equal(reply.headers['content-type'], 'application/octet-stream');
  assert.equal(reply.headers['content-length'], String(first.length + tail.length));
  assert.equal(reply.headers['cache-control'], 'public, max-age=60');
  assert.equal(reply.headers.etag, '"bytes-v1"');
  assert.equal(reply.headers['last-modified'], 'Wed, 01 Jan 2025 00:00:00 GMT');
  assert.equal(reply.headers['x-meow-request-id'], raw.requestId);
  assert.deepEqual(reply.body, Buffer.concat([first, tail]));
  assert.equal(signal.aborted, false);
  assert.equal(fixture.server.state.active, 1);
  release.resolve();
  await until(() => fixture.server.state.active === 0);
  assert.equal(disposals, 1);
});

test('empty raw streams finish with status 200 and dispose once', async t => {
  let disposals = 0;
  const fixture = await binaryServer(t, () => success(Readable.from([]), async () => { disposals++; },
    { 'content-type': 'text/plain', 'content-length': 0 }));
  const reply = await fixture.request().ended;
  assert.equal(reply.status, 200);
  assert.equal(reply.headers['content-type'], 'text/plain');
  assert.equal(reply.body.length, 0);
  await until(() => fixture.server.state.active === 0);
  assert.equal(disposals, 1);
});

test('a precise string content-length transfers all raw bytes and closes cleanly', { timeout: 3000 }, async t => {
  const bytes = Buffer.from([0xff, 0xd8, 0, 0xff, 0xd9]);
  let disposals = 0;
  const fixture = await binaryServer(t, () => success(Readable.from([bytes]),
    async () => { disposals++; }, { 'content-type': 'image/jpeg', 'content-length': String(bytes.length) }));
  const reply = await fixture.request().ended;
  assert.equal(reply.status, 200);
  assert.equal(reply.headers['content-length'], String(bytes.length));
  assert.deepEqual(reply.body, bytes);
  await fixture.server.close();
  assert.equal(fixture.server.state.active, 0);
  assert.equal(disposals, 1);
});

for (const [name, declared] of [['short body', 6], ['oversized body', 4]]) {
  test(`content-length ${name} aborts native HTTP without uncaught errors, duplicate JSON or stuck owners`,
    { timeout: 3000 }, async t => {
      const bytes = Buffer.from([0xff, 0xd8, 0, 0xff, 0xd9]);
      const disposing = deferred(), release = deferred();
      let disposals = 0, writes = 0, closed = false;
      const original = http.ServerResponse.prototype.writeHead;
      t.mock.method(http.ServerResponse.prototype, 'writeHead', function (...args) {
        writes++;
        return Reflect.apply(original, this, args);
      });
      const fixture = await binaryServer(t, () => success(Readable.from([bytes]), async () => {
        disposals++; disposing.resolve(); await release.promise;
      }, { 'content-type': 'image/jpeg', 'content-length': String(declared) }));
      fixture.cleanup(() => release.resolve());
      const raw = fixture.request();
      await disposing.promise;
      await assert.rejects(raw.ended);
      assert.equal(writes, 1);
      assert.ok(Buffer.concat(raw.chunks).equals(bytes) || raw.chunks.length === 0);
      assert.equal(fixture.server.state.active, 1);
      const closing = fixture.server.close().then(() => { closed = true; });
      await turn();
      assert.equal(closed, false);
      release.resolve();
      await closing;
      assert.equal(fixture.server.state.active, 0);
      assert.equal(disposals, 1);
    });
}

test('a paused native HTTP consumer backpressures the producer and can resume without losing bytes', async t => {
  const chunkSize = 64 * 1024, total = 32 * 1024 * 1024;
  let produced = 0, disposals = 0;
  const source = new Readable({ highWaterMark: chunkSize, read() {
    if (produced === total) { this.push(null); return; }
    produced += chunkSize;
    this.push(Buffer.alloc(chunkSize, 0xa5));
  } });
  const fixture = await binaryServer(t, () => success(source, async () => { disposals++; }));
  const raw = fixture.request({ pause: true });
  fixture.cleanup(() => { raw.request.destroy(); source.destroy(); });
  const response = await raw.head;
  await until(() => source.isPaused() && source.readableLength >= chunkSize);
  assert.equal(source.readableEnded, false);
  assert.ok(produced < total, `producer ran ahead through ${produced} bytes`);
  assert.equal(fixture.server.state.active, 1);
  assert.equal(disposals, 0);
  response.resume();
  const reply = await raw.ended;
  assert.equal(reply.body.length, total);
  assert.ok(reply.body.every(byte => byte === 0xa5));
  await until(() => fixture.server.state.active === 0);
  assert.equal(disposals, 1);
});

test('abort during downstream backpressure releases the source but awaits its real disposer', async t => {
  const disposing = deferred(), release = deferred();
  let produced = 0, signal, drained = false;
  const source = new Readable({ highWaterMark: 64 * 1024, read() {
    produced += 64 * 1024;
    this.push(Buffer.alloc(64 * 1024));
  } });
  const fixture = await binaryServer(t, (_input, context) => {
    signal = context.signal;
    return success(source, async () => { disposing.resolve(); await release.promise; });
  });
  const raw = fixture.request({ pause: true });
  fixture.cleanup(() => { release.resolve(); raw.request.destroy(); source.destroy(); });
  await raw.head;
  await until(() => source.isPaused() && source.readableLength >= source.readableHighWaterMark);
  const aborting = fixture.server.abortActive().then(() => { drained = true; });
  await disposing.promise;
  await until(() => source.closed);
  const stoppedAt = produced;
  await turn();
  assert.equal(signal.aborted, true);
  assert.equal(produced, stoppedAt);
  assert.equal(drained, false);
  assert.equal(fixture.server.state.active, 1);
  raw.request.destroy();
  release.resolve();
  await aborting;
  assert.equal(fixture.server.state.active, 0);
});

test('authentication and body validation reject malicious stream requests before handler dispatch', async t => {
  let calls = 0;
  const fixture = await binaryServer(t, () => { calls++; return success(Readable.from([Buffer.from('safe')])); });
  const cases = [
    [{ headers: { authorization: undefined } }, 401, 'PERMISSION_DENIED'],
    [{ headers: { authorization: 'Bearer wrong' } }, 401, 'PERMISSION_DENIED'],
    [{ headers: { 'x-meow-session': randomUUID() } }, 401, 'PERMISSION_DENIED'],
    [{ headers: { 'x-meow-session': undefined } }, 401, 'PERMISSION_DENIED'],
    [{ headers: { host: 'example.invalid' } }, 401, 'PERMISSION_DENIED'],
    [{ headers: { authorization: [`Bearer ${fixture.identity.key}`, `Bearer ${fixture.identity.key}`] } },
      401, 'PERMISSION_DENIED'],
    [{ requestId: 'invalid-id' }, 400, 'INVALID_REQUEST'],
    [{ body: '{}' }, 400, 'INVALID_REQUEST'],
    [{ body: Buffer.from([0xff]), headers: { 'content-type': 'application/octet-stream' } }, 400, 'INVALID_REQUEST'],
    [{ path: '//example.invalid/v1/bytes' }, 400, 'INVALID_REQUEST'],
    [{ path: '/v1/bytes?unexpected=true' }, 404, 'METHOD_NOT_FOUND'],
    [{ method: 'POST' }, 404, 'METHOD_NOT_FOUND'],
  ];
  for (const [options, status, code] of cases) {
    const raw = fixture.request(options);
    assertJSON(await raw.ended, status, { ok: false, contract: HTTP_PROTOCOL, code });
  }
  assert.equal(calls, 0);
  await until(() => fixture.server.state.active === 0);
});

for (const [name, handler, expected] of [
  ['declared failure', () => ({ ok: false, contract, code: 'UPSTREAM_FAILED' }),
    { ok: false, contract, code: 'UPSTREAM_FAILED' }],
  ['unknown rejection', () => { throw new Error('PRIVATE-provider-secret'); }, invalidResponse],
  ['known rejection', () => { throw new BridgeError('TIMEOUT'); },
    { ok: false, contract: HTTP_PROTOCOL, code: 'TIMEOUT' }],
  ['undeclared failure', () => ({ ok: false, contract, code: 'PRIVATE-provider-secret' }), invalidResponse],
  ['wrong failure contract', () => ({ ok: false, contract: 'other.v1', code: 'UPSTREAM_FAILED' }), invalidResponse],
  ['JSON success on a stream route', () => ({ ok: true, contract, value: 'wrong shape' }), invalidResponse],
]) {
  test(`stream ${name} returns a redacted HTTP 502 JSON envelope`, async t => {
    const fixture = await binaryServer(t, handler);
    const raw = fixture.request(), reply = await raw.ended;
    assertJSON(reply, 502, expected);
    assert.equal(reply.headers['x-meow-request-id'], raw.requestId);
    assert.equal(reply.body.includes('PRIVATE-provider-secret'), false);
    await until(() => fixture.server.state.active === 0);
  });
}

test('a source error before its first chunk returns JSON only after disposal really completes', async t => {
  const disposing = deferred(), release = deferred();
  let disposals = 0, headersReceived = false;
  const source = new Readable({ read() { this.destroy(new Error('PRIVATE-provider-secret')); } });
  const fixture = await binaryServer(t, () => success(source, async () => {
    disposals++; disposing.resolve(); await release.promise;
  }, { 'content-type': 'image/jpeg' }));
  fixture.cleanup(() => release.resolve());
  const raw = fixture.request();
  raw.head.then(() => { headersReceived = true; }, () => {});
  await disposing.promise;
  await turn();
  assert.equal(headersReceived, false);
  assert.equal(fixture.server.state.active, 1);
  release.resolve();
  assertJSON(await raw.ended, 502, invalidResponse);
  await until(() => fixture.server.state.active === 0);
  assert.equal(source.closed, true);
  assert.equal(disposals, 1);
});

const malformed = [
  ['redirect status', reply => { reply.status = 302; }],
  ['non-200 status', reply => { reply.status = 206; }],
  ['Location', reply => { reply.headers.Location = 'https://example.invalid/private'; }],
  ['set-cookie', reply => { reply.headers['set-cookie'] = 'private=value'; }],
  ['request ID override', reply => { reply.headers['x-meow-request-id'] = randomUUID(); }],
  ['arbitrary header', reply => { reply.headers['x-arbitrary'] = 'private'; }],
  ['hop-by-hop header', reply => { reply.headers.connection = 'close'; }],
  ['transfer encoding', reply => { reply.headers['transfer-encoding'] = 'chunked'; }],
  ['missing content type', reply => { reply.headers = {}; }],
  ['empty content type', reply => { reply.headers['content-type'] = ' '; }],
  ['invalid header bytes', reply => { reply.headers.etag = 'private\r\nx-injected: true'; }],
  ['duplicate normalized header', reply => { reply.headers['Content-Type'] = 'text/plain'; }],
  ['array header', reply => { reply.headers.etag = ['one', 'two']; }],
  ['negative length', reply => { reply.headers['content-length'] = -1; }],
  ['fractional length', reply => { reply.headers['content-length'] = '1.5'; }],
  ['inexact Node HTTP length', reply => { reply.headers['content-length'] = '9007199254740993'; }],
  ['non-stream body', reply => { reply.body = Buffer.from('wrong body'); }],
  ['extra reply field', reply => { reply.extra = 'private'; }],
];
for (const [name, mutate] of malformed) {
  test(`malformed stream ${name} rejects and awaits its disposer without publishing raw headers`, async t => {
    const source = new Readable({ read() { throw new Error('malformed replies must never start reading'); } });
    const disposing = deferred(), release = deferred();
    let disposals = 0;
    const fixture = await binaryServer(t, () => {
      const reply = success(source, async () => { disposals++; disposing.resolve(); await release.promise; });
      mutate(reply);
      return reply;
    });
    fixture.cleanup(() => { release.resolve(); source.destroy(); });
    const raw = fixture.request();
    await disposing.promise;
    assert.equal(fixture.server.state.active, 1);
    release.resolve();
    const reply = await raw.ended;
    assertJSON(reply, 502, invalidResponse);
    assert.equal(reply.headers['x-meow-request-id'], raw.requestId);
    for (const name of ['location', 'set-cookie', 'x-arbitrary', 'x-injected']) {
      assert.equal(reply.headers[name], undefined);
    }
    await until(() => fixture.server.state.active === 0);
    if (name !== 'non-stream body') assert.equal(source.closed, true);
    assert.equal(disposals, 1);
  });
}

test('missing required dispose rejects and still waits for the returned readable to finish destruction', async t => {
  const destroying = deferred(), release = deferred();
  const source = new Readable({ read() {}, destroy(error, done) {
    destroying.resolve(); void release.promise.then(() => done(error));
  } });
  const fixture = await binaryServer(t, () => {
    const reply = success(source);
    delete reply.dispose;
    return reply;
  });
  fixture.cleanup(() => release.resolve());
  const raw = fixture.request();
  await destroying.promise;
  assert.equal(fixture.server.state.active, 1);
  assert.equal(source.closed, false);
  release.resolve();
  assertJSON(await raw.ended, 502, invalidResponse);
  await until(() => fixture.server.state.active === 0);
  assert.equal(source.closed, true);
});

test('abort while the handler is pending retains ownership until the actual handler rejection', async t => {
  const entered = deferred(), release = deferred();
  let signal, aborted = false;
  const fixture = await binaryServer(t, async (_input, context) => {
    signal = context.signal; entered.resolve(); await release.promise; throw new BridgeError('CANCELLED');
  });
  fixture.cleanup(() => release.resolve());
  const raw = fixture.request();
  await entered.promise;
  raw.request.destroy();
  await raw.ended.catch(() => {});
  await until(() => signal.aborted);
  const draining = fixture.server.abortActive().then(() => { aborted = true; });
  await turn();
  assert.equal(aborted, false);
  assert.equal(fixture.server.state.active, 1);
  release.resolve();
  await draining;
  assert.equal(fixture.server.state.active, 0);
});

test('late stream delivery after abort is never read and its disposal remains owned', async t => {
  const entered = deferred(), deliver = deferred(), disposing = deferred(), release = deferred();
  let reads = 0, disposals = 0, current, aborted = false;
  const source = new Readable({ read() { reads++; this.push(Buffer.from('late')); this.push(null); } });
  const fixture = await binaryServer(t, async (_input, context) => {
    current = context.isCurrent; entered.resolve(); await deliver.promise;
    return success(source, async () => { disposals++; disposing.resolve(); await release.promise; });
  });
  fixture.cleanup(() => { deliver.resolve(); release.resolve(); source.destroy(); });
  const raw = fixture.request();
  await entered.promise;
  const draining = fixture.server.abortActive().then(() => { aborted = true; });
  await raw.ended.catch(() => {});
  assert.equal(current(), false);
  assert.equal(fixture.server.state.active, 1);
  deliver.resolve();
  await disposing.promise;
  await turn();
  assert.equal(reads, 0);
  assert.equal(source.destroyed, true);
  assert.equal(disposals, 1);
  assert.equal(aborted, false);
  assert.equal(fixture.server.state.active, 1);
  release.resolve();
  await draining;
  assert.equal(fixture.server.state.active, 0);
});

test('abortActive waits independently for actual readable destruction and async disposal', async t => {
  const destroying = deferred(), finishDestroy = deferred(), disposing = deferred(), finishDispose = deferred();
  let pushed = false, disposals = 0, aborted = false, signal;
  const source = new Readable({
    read() { if (!pushed) { pushed = true; this.push(Buffer.from('first')); } },
    destroy(error, done) { destroying.resolve(); void finishDestroy.promise.then(() => done(error)); },
  });
  const fixture = await binaryServer(t, (_input, context) => {
    signal = context.signal;
    return success(source, async () => { disposals++; disposing.resolve(); await finishDispose.promise; });
  });
  fixture.cleanup(() => { finishDispose.resolve(); finishDestroy.resolve(); });
  const raw = fixture.request();
  await raw.first;
  const draining = fixture.server.abortActive().then(() => { aborted = true; });
  await Promise.all([destroying.promise, disposing.promise]);
  await raw.ended.catch(() => {});
  assert.equal(signal.aborted, true);
  assert.equal(aborted, false);
  assert.equal(fixture.server.state.active, 1);
  finishDispose.resolve();
  await turn();
  assert.equal(aborted, false);
  assert.equal(fixture.server.state.active, 1);
  assert.equal(source.closed, false);
  finishDestroy.resolve();
  await draining;
  assert.equal(source.closed, true);
  assert.equal(fixture.server.state.active, 0);
  assert.equal(disposals, 1);
});

for (const autoDestroy of [false, true]) {
  for (const emitClose of [true, false]) {
    test(`complete bytes retain destruction ownership with autoDestroy=${autoDestroy}, emitClose=${emitClose}`,
      { timeout: 3000 }, async t => {
        const destroying = deferred(), finishDestroy = deferred();
        const bytes = Buffer.from([0, 0xff, 1, 0xfe]);
        let disposals = 0, destroys = 0, closes = 0, sourceCloses = 0, aborted = false, closed = false;
        const source = new Readable({ autoDestroy, emitClose,
          read() { this.push(bytes); this.push(null); },
          destroy(error, done) { destroys++; destroying.resolve(); void finishDestroy.promise.then(() => done(error)); },
        });
        source.on('close', () => { sourceCloses++; });
        const fixture = await binaryServer(t, () => success(source, async () => { disposals++; }), {
          onClose() {
            closes++;
            assert.equal(source.closed, true);
            assert.equal(fixture.server.state.active, 0);
          },
        });
        fixture.cleanup(() => finishDestroy.resolve());
        const reply = await fixture.request().ended;
        await destroying.promise;
        assert.equal(reply.status, 200);
        assert.deepEqual(reply.body, bytes);
        assert.equal(source.closed, false);
        assert.equal(fixture.server.state.active, 1);
        assert.equal(disposals, 1);
        const draining = fixture.server.abortActive().then(() => { aborted = true; });
        const closing = fixture.server.close().then(() => { closed = true; });
        await turn();
        assert.equal(aborted, false);
        assert.equal(closed, false);
        assert.equal(closes, 0);
        assert.equal(fixture.server.state.active, 1);
        finishDestroy.resolve();
        await Promise.all([draining, closing]);
        assert.equal(source.closed, true);
        assert.equal(fixture.server.state.active, 0);
        assert.equal(disposals, 1);
        assert.equal(destroys, 1);
        assert.equal(sourceCloses, Number(emitClose));
        assert.equal(closes, 1);
        await fixture.server.close();
        assert.equal(closes, 1);
        t.diagnostic(JSON.stringify({ autoDestroy, emitClose, disposals, destroys, sourceCloses,
          active: fixture.server.state.active, aborted, closed, onClose: closes }));
      });
  }
}

test('emitClose false destroy errors after complete bytes do not become session cleanup failures',
  { timeout: 3000 }, async t => {
    const destroying = deferred(), finishDestroy = deferred();
    const bytes = Buffer.from([0xff, 0, 0xfe]);
    let disposals = 0;
    const source = new Readable({ autoDestroy: false, emitClose: false,
      read() { this.push(bytes); this.push(null); },
      destroy(_error, done) {
        destroying.resolve();
        void finishDestroy.promise.then(() => done(new Error('PRIVATE-destroy-secret')));
      },
    });
    const fixture = await binaryServer(t, () => success(source, async () => { disposals++; }));
    fixture.cleanup(() => finishDestroy.resolve());
    const reply = await fixture.request().ended;
    await destroying.promise;
    assert.equal(reply.status, 200);
    assert.deepEqual(reply.body, bytes);
    assert.equal(fixture.server.state.active, 1);
    finishDestroy.resolve();
    await fixture.server.abortActive();
    await fixture.server.close();
    assert.equal(source.closed, true);
    assert.equal(fixture.server.state.active, 0);
    assert.equal(disposals, 1);
  });

for (const failure of [false, true]) {
  test(`an already destroying emitClose false invalid reply drains via dispose with destroy failure=${failure}`,
    { timeout: 3000 }, async t => {
      const destroying = deferred(), finishDestroy = deferred(), disposing = deferred();
      let disposals = 0;
      const source = new Readable({ emitClose: false, read() {},
        destroy(error, done) {
          destroying.resolve();
          void finishDestroy.promise.then(() => done(failure ? new Error('PRIVATE-destroy-secret') : error));
        },
      });
      source.destroy();
      await destroying.promise;
      const fixture = await binaryServer(t, () => success(source, async () => {
        disposals++; disposing.resolve(); await finishDestroy.promise;
      }));
      fixture.cleanup(() => finishDestroy.resolve());
      const raw = fixture.request();
      await disposing.promise;
      assert.equal(source.closed, false);
      assert.equal(fixture.server.state.active, 1);
      finishDestroy.resolve();
      assertJSON(await raw.ended, 502, invalidResponse);
      await fixture.server.close();
      assert.equal(source.closed, true);
      assert.equal(fixture.server.state.active, 0);
      assert.equal(disposals, 1);
    });
}

for (const emitClose of [false, true]) {
  for (const shutdown of [false, true]) {
    test(`pre-handoff destruction drains independently of immediate dispose: emitClose=${emitClose}, close=${shutdown}`,
      { timeout: 3000 }, async t => {
        const destroying = deferred(), finishDestroy = deferred(), disposing = deferred();
        let disposals = 0, destroys = 0, sourceCloses = 0, closes = 0, closed = false, answered = false;
        const source = new Readable({ emitClose,
          read() { throw new Error('invalid replies must never start reading'); },
          destroy(error, done) { destroys++; destroying.resolve(); void finishDestroy.promise.then(() => done(error)); },
        });
        source.on('close', () => { sourceCloses++; });
        source.destroy();
        await destroying.promise;
        const fixture = await binaryServer(t, () => success(source, async () => {
          disposals++; disposing.resolve();
        }), { onClose() {
          closes++;
          assert.equal(source.closed, true);
          assert.equal(fixture.server.state.active, 0);
        } });
        fixture.cleanup(() => finishDestroy.resolve());
        const raw = fixture.request();
        raw.head.then(() => { answered = true; }, () => {});
        await disposing.promise;
        assert.equal(source.closed, false);
        assert.equal(fixture.server.state.active, 1);
        let closing;
        if (shutdown) closing = fixture.server.close().then(() => { closed = true; });
        // Keep destruction gated through multiple fallback observations.
        await new Promise(resolve => setTimeout(resolve, 60));
        assert.equal(source.closed, false);
        assert.equal(fixture.server.state.active, 1);
        assert.equal(answered, false);
        assert.equal(closed, false);
        assert.equal(closes, 0);
        assert.equal(disposals, 1);
        assert.equal(sourceCloses, 0);
        finishDestroy.resolve();
        if (shutdown) {
          await closing;
          await assert.rejects(raw.ended);
        } else {
          assertJSON(await raw.ended, 502, invalidResponse);
          await until(() => fixture.server.state.active === 0);
          await fixture.server.abortActive();
          await fixture.server.close();
        }
        assert.equal(source.closed, true);
        assert.equal(fixture.server.state.active, 0);
        assert.equal(disposals, 1);
        assert.equal(destroys, 1);
        assert.equal(sourceCloses, Number(emitClose));
        assert.equal(source.listenerCount('close'), 1);
        assert.equal(closes, 1);
        t.diagnostic(JSON.stringify({ emitClose, shutdown, disposals, destroys, sourceCloses,
          responseStatus: shutdown ? null : 502, active: fixture.server.state.active, onClose: closes }));
      });
  }
}

test('disconnect during a raw response aborts and waits for the disposer', async t => {
  const source = new PassThrough(), disposing = deferred(), release = deferred();
  let signal, disposals = 0;
  const fixture = await binaryServer(t, (_input, context) => {
    signal = context.signal;
    return success(source, async () => { disposals++; disposing.resolve(); await release.promise; });
  });
  fixture.cleanup(() => { release.resolve(); source.destroy(); });
  const raw = fixture.request();
  source.write(Buffer.from([0xff, 0xd8, 0xff]));
  await raw.first;
  raw.request.destroy();
  await raw.ended.catch(() => {});
  await disposing.promise;
  assert.equal(signal.aborted, true);
  assert.equal(fixture.server.state.active, 1);
  release.resolve();
  await until(() => fixture.server.state.active === 0);
  assert.equal(source.closed, true);
  assert.equal(disposals, 1);
});

test('close waits through handler delivery, stream destruction and real disposal before onClose', async t => {
  const entered = deferred(), deliver = deferred(), disposing = deferred(), finishDispose = deferred();
  const destroying = deferred(), finishDestroy = deferred();
  let fixture, signal, closes = 0, calls = 0, closed = false, disposed = false;
  const source = new Readable({ read() { throw new Error('closed sessions must not consume late bytes'); },
    destroy(error, done) { destroying.resolve(); void finishDestroy.promise.then(() => done(error)); } });
  fixture = await binaryServer(t, async (_input, context) => {
    calls++; signal = context.signal; entered.resolve(); await deliver.promise;
    return success(source, async () => {
      disposing.resolve(); await finishDispose.promise; disposed = true;
    });
  }, { onClose() {
    closes++;
    assert.equal(disposed, true);
    assert.equal(source.closed, true);
    assert.deepEqual(fixture.server.state, { closed: true, active: 0 });
  } });
  fixture.cleanup(() => { deliver.resolve(); finishDispose.resolve(); finishDestroy.resolve(); });
  const raw = fixture.request();
  await entered.promise;
  const closing = fixture.server.close().then(() => { closed = true; });
  await raw.ended.catch(() => {});
  assert.equal(signal.aborted, true);
  assert.deepEqual(fixture.server.state, { closed: true, active: 1 });
  assert.equal(closed, false);
  assert.equal(closes, 0);
  await assert.rejects(fixture.server.start(), { code: 'CLOSED' });
  await assert.rejects(fixture.request().ended);
  assert.equal(calls, 1);
  deliver.resolve();
  await Promise.all([disposing.promise, destroying.promise]);
  finishDispose.resolve();
  await turn();
  assert.equal(disposed, true);
  assert.equal(fixture.server.state.active, 1);
  assert.equal(closed, false);
  finishDestroy.resolve();
  await closing;
  assert.equal(fixture.server.state.active, 0);
  await fixture.server.close();
  assert.equal(closes, 1);
});

test('a stream error after headers terminates the response without a second JSON write', async t => {
  const source = new PassThrough(), disposing = deferred(), release = deferred();
  const bytes = Buffer.from([0xff, 0xd8, 0xff, 0xe0]);
  let writes = 0, disposals = 0;
  const original = http.ServerResponse.prototype.writeHead;
  t.mock.method(http.ServerResponse.prototype, 'writeHead', function (...args) {
    writes++;
    return Reflect.apply(original, this, args);
  });
  const fixture = await binaryServer(t, () => success(source, async () => {
    disposals++; disposing.resolve(); await release.promise;
  }, { 'content-type': 'image/jpeg' }));
  fixture.cleanup(() => { release.resolve(); source.destroy(); });
  const raw = fixture.request();
  source.write(bytes);
  assert.deepEqual(await raw.first, bytes);
  source.destroy(new Error('PRIVATE-provider-secret'));
  await disposing.promise;
  await assert.rejects(raw.ended);
  await raw.closed;
  assert.equal(writes, 1);
  assert.deepEqual(Buffer.concat(raw.chunks), bytes);
  assert.equal(fixture.server.state.active, 1);
  release.resolve();
  await until(() => fixture.server.state.active === 0);
  assert.equal(disposals, 1);
});

test('a rejecting disposer is awaited and becomes a redacted failure before any raw headers', async t => {
  const entered = deferred(), release = deferred();
  const source = new Readable({ read() {} });
  const fixture = await binaryServer(t, () => ({ ...success(source, async () => {
    entered.resolve(); await release.promise; throw new Error('PRIVATE-cleanup-secret');
  }), status: 302 }), { expectCleanupFailure: true });
  fixture.cleanup(() => release.resolve());
  const raw = fixture.request();
  await entered.promise;
  assert.equal(fixture.server.state.active, 1);
  release.resolve();
  assertJSON(await raw.ended, 502, invalidResponse);
  await until(() => fixture.server.state.active === 0);
  assert.equal(source.closed, true);
  await assert.rejects(fixture.server.abortActive(), { code: 'INVALID_RESPONSE' });
  await assert.rejects(fixture.server.close(), { code: 'INVALID_RESPONSE' });
});

test('dispose rejection after a complete raw response does not write JSON and close rejects after drain',
  { timeout: 3000 }, async t => {
    const disposing = deferred(), release = deferred();
    const bytes = Buffer.from([0xff, 0xd8, 0, 0xff, 0xd9]);
    let disposals = 0, writes = 0, closed = false, closes = 0;
    const original = http.ServerResponse.prototype.writeHead;
    t.mock.method(http.ServerResponse.prototype, 'writeHead', function (...args) {
      writes++;
      return Reflect.apply(original, this, args);
    });
    const fixture = await binaryServer(t, () => success(Readable.from([bytes]), async () => {
      disposals++; disposing.resolve(); await release.promise; throw new Error('PRIVATE-cleanup-secret');
    }, { 'content-type': 'image/jpeg', 'content-length': bytes.length }), {
      expectCleanupFailure: true,
      onClose() { closes++; assert.equal(fixture.server.state.active, 0); },
    });
    fixture.cleanup(() => release.resolve());
    const reply = await fixture.request().ended;
    await disposing.promise;
    assert.equal(reply.status, 200);
    assert.equal(reply.headers['content-type'], 'image/jpeg');
    assert.deepEqual(reply.body, bytes);
    assert.equal(fixture.server.state.active, 1);
    const closing = assert.rejects(fixture.server.close(), { code: 'INVALID_RESPONSE' }).then(() => { closed = true; });
    await turn();
    assert.equal(closed, false);
    release.resolve();
    await closing;
    await turn();
    assert.equal(fixture.server.state.active, 0);
    assert.equal(disposals, 1);
    assert.equal(writes, 1);
    assert.equal(closes, 1);
    await assert.rejects(fixture.server.close(), { code: 'INVALID_RESPONSE' });
    assert.equal(closes, 1);
  });

test('abortActive remembers cleanup failures and waits for other handlers and disposers before rejecting',
  { timeout: 3000 }, async t => {
    const rejectDispose = deferred(), failed = deferred(), entered = deferred(), deliver = deferred();
    const disposing = deferred(), release = deferred(), closingEntered = deferred(), finishClose = deferred();
    let calls = 0, disposals = 0, aborted = false, closed = false, closes = 0;
    const fixture = await binaryServer(t, async () => {
      if (++calls === 1) return success(Readable.from([Buffer.from('first')]), async () => {
        await rejectDispose.promise; failed.resolve(); throw new Error('PRIVATE-cleanup-secret');
      });
      entered.resolve();
      await deliver.promise;
      return success(Readable.from([Buffer.from('late')]), async () => {
        disposals++; disposing.resolve(); await release.promise;
      });
    }, { expectCleanupFailure: true, async onClose() {
      closes++;
      assert.equal(fixture.server.state.active, 0);
      closingEntered.resolve();
      await finishClose.promise;
    } });
    fixture.cleanup(() => {
      rejectDispose.resolve(); deliver.resolve(); release.resolve(); finishClose.resolve();
    });
    const first = await fixture.request().ended;
    assert.equal(first.body.toString(), 'first');
    const second = fixture.request();
    await entered.promise;
    const draining = assert.rejects(fixture.server.abortActive(), { code: 'INVALID_RESPONSE' })
      .then(() => { aborted = true; });
    await second.ended.catch(() => {});
    rejectDispose.resolve();
    await failed.promise;
    await until(() => fixture.server.state.active === 1);
    assert.equal(aborted, false);
    deliver.resolve();
    await disposing.promise;
    await turn();
    assert.equal(aborted, false);
    assert.equal(fixture.server.state.active, 1);
    release.resolve();
    await draining;
    assert.equal(disposals, 1);
    assert.equal(fixture.server.state.active, 0);
    await assert.rejects(fixture.server.abortActive(), { code: 'INVALID_RESPONSE' });
    const closing = assert.rejects(fixture.server.close(), { code: 'INVALID_RESPONSE' }).then(() => { closed = true; });
    await closingEntered.promise;
    await turn();
    assert.equal(closed, false);
    finishClose.resolve();
    await closing;
    assert.equal(closes, 1);
    t.diagnostic('earlier stream cleanup failure: abortActive=INVALID_RESPONSE, close=INVALID_RESPONSE, active=0, onClose=1');
  });

test('HTTP session close reports an earlier stream cleanup failure instead of cleanupComplete true',
  { timeout: 3000 }, async t => {
    const { createAppRuntime } = await import('../../../apps/runtime/bootstrap.mjs');
    const identity = credentials(), closingModule = deferred(), release = deferred();
    const bytes = Buffer.from([0xff, 0xd8, 0, 0xff, 0xd9]);
    let moduleCloses = 0;
    const module = { id: 'tmdb', version: '0.1.0', contract: 'tmdb.data.v1', methods: {},
      image: () => success(Readable.from([bytes]), async () => { throw new Error('PRIVATE-cleanup-secret'); },
        { 'content-type': 'image/jpeg', 'content-length': bytes.length }),
      async close() { moduleCloses++; closingModule.resolve(); await release.promise; },
    };
    const app = createAppRuntime({ sessionId: identity.sessionId, requestKey: identity.key, modules: [module] });
    await app.start();
    t.after(async () => {
      release.resolve();
      await assert.rejects(app.close(), { code: 'INVALID_RESPONSE' });
    });
    const picture = await exchange(app.baseUrl, identity, { path: '/v1/tmdb/images/original/bytes.jpg' }).ended;
    assert.equal(picture.status, 200);
    assert.deepEqual(picture.body, bytes);
    await until(() => app.state.active === 0);
    const raw = exchange(app.baseUrl, identity, { path: '/v1/close', method: 'POST', body: '{}',
      headers: { 'content-type': 'application/json' } });
    let answered = false;
    raw.ended.then(() => { answered = true; }, () => {});
    await closingModule.promise;
    await turn();
    assert.equal(answered, false);
    assert.equal(app.state.active, 1);
    release.resolve();
    const reply = await raw.ended;
    assertJSON(reply, 200, invalidResponse);
    await until(() => app.state.active === 0);
    await assert.rejects(app.close(), { code: 'INVALID_RESPONSE' });
    assert.equal(moduleCloses, 1);
    t.diagnostic(JSON.stringify({ imageStatus: picture.status, closeStatus: reply.status,
      closeEnvelope: JSON.parse(reply.body.toString()), state: app.state, moduleCloses }));
  });

test('ordinary JSON handler errors preserve HTTP 200 and public error mappings', async t => {
  const { server, identity } = await pair(t, input => {
    if (input === 'timeout') throw new BridgeError('TIMEOUT');
    if (input === 'invalid') throw new BridgeError('INVALID_REQUEST');
    throw new Error('PRIVATE-json-secret');
  });
  for (const [input, code] of [['timeout', 'TIMEOUT'], ['invalid', 'INVALID_RESPONSE'], ['unknown', 'INVALID_RESPONSE']]) {
    const reply = await rawRequest(server.baseUrl, identity, { body: JSON.stringify(input) }).response;
    assertJSON(reply, 200, { ok: false, contract: HTTP_PROTOCOL, code });
  }
  const malformed = await rawRequest(server.baseUrl, identity, { body: '{' }).response;
  assertJSON(malformed, 400, { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_REQUEST' });
  await until(() => server.state.active === 0);
});

test('raw and existing JSON/upload routes coexist without changing their contracts', async t => {
  const fixture = await binaryServer(t, () => success(Readable.from([Buffer.from([0, 0xff])])));
  const client = createHttpClient({ ...fixture.identity, baseUrl: fixture.server.baseUrl });
  fixture.cleanup(() => client.close());
  for (const value of [null, false, 3, '猫😀', [1, 2], { ok: true, value: 'business object' }]) {
    assert.deepEqual(await client.request({ path: '/v1/json', body: value }),
      { ok: true, contract: HTTP_PROTOCOL, value });
  }
  const bytes = Buffer.from([0, 0xff, 0x80]);
  assert.deepEqual(await client.request({ method: 'PUT', path: '/v1/upload', body: bytes, raw: true,
    contract, codes: new Set(['LIMIT']) }), { ok: true, contract, value: [...bytes] });
  assert.deepEqual(await client.request({ method: 'PUT', path: '/v1/upload', body: Buffer.alloc(9), raw: true,
    contract, codes: new Set(['LIMIT']) }), { ok: false, contract, code: 'LIMIT' });
  assertJSON(await fixture.request({ path: '/v1/json', method: 'POST', body: '{"x":1,"x":2}',
    headers: { 'content-type': 'application/json' } }).ended, 400,
    { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_REQUEST' });
  assertJSON(await fixture.request({ path: '/v1/upload', method: 'PUT', body: '{}',
    headers: { 'content-type': 'application/json' } }).ended, 415,
    { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_REQUEST' });
  assert.deepEqual((await fixture.request().ended).body, Buffer.from([0, 0xff]));
  await until(() => fixture.server.state.active === 0);
});
