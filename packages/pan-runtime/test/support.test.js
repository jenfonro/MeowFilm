import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import { createHook } from 'node:async_hooks';
import { getEventListeners } from 'node:events';
import { syncBuiltinESMExports } from 'node:module';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync, deflateSync, brotliCompressSync } from 'node:zlib';
import { PanError, requireValue } from '../src/errors.js';
import { object, text, parseJSON, jsonObject, mergeCookie } from '../src/values.js';
import { CookieJar } from '../src/cookies.js';
import { PanStore } from '../src/store.js';
import { createTransport, requestJSON, requestText, delay } from '../src/http.js';

const code = expected => error => {
  assert.ok(error instanceof PanError);
  assert.equal(error.code, expected);
  assert.equal(error.message, expected);
  assert.equal(error.cause, undefined);
  return true;
};
const deferred = () => {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
};
const nextTurn = () => new Promise(resolve => setImmediate(resolve));

function temporary(t) {
  const path = fs.mkdtempSync(join(fileURLToPath(new URL('../../../tmp/', import.meta.url)), 'pan-support-'));
  t.after(() => fs.rmSync(path, { recursive: true, force: true }));
  return path;
}

function fsFailure(name, action) {
  const original = fs[name];
  fs[name] = () => { throw new Error('synthetic private filesystem detail'); };
  syncBuiltinESMExports();
  try { action(); }
  finally { fs[name] = original; syncBuiltinESMExports(); }
}

async function localHTTP(t, handler) {
  const sockets = new Set();
  const server = http.createServer(handler);
  server.requestTimeout = 0;
  server.on('connection', socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  t.after(async () => {
    for (const socket of sockets) socket.destroy();
    if (server.listening) await new Promise(resolve => server.close(resolve));
  });
  return { url: `http://127.0.0.1:${server.address().port}`, sockets };
}

function transportFor(t, options) {
  const transport = createTransport(options);
  t.after(() => transport.close());
  return transport;
}

test('errors expose only the closed code set', () => {
  for (const value of ['INVALID_REQUEST', 'STORAGE_ERROR', 'CANCELLED', 'CLOSED', 'UPSTREAM_FORMAT']) {
    assert.ok(code(value)(new PanError(value)));
  }
  assert.ok(code('PAN_FAILURE')(new PanError('private upstream body and URL')));
  assert.doesNotThrow(() => requireValue(true));
  assert.throws(() => requireValue(false), code('INVALID_REQUEST'));
  assert.throws(() => requireValue(false, 'AUTH_FAILED'), code('AUTH_FAILED'));
});

test('object and text keep the public value types explicit', () => {
  assert.equal(object({}), true);
  assert.equal(object(Object.create(null)), true);
  for (const value of [null, [], 1, 'x', new Date(), new Map()]) assert.equal(object(value), false);
  assert.equal(text('9007199254740993'), '9007199254740993');
  assert.equal(text(0), '0');
  assert.equal(text(1.5), '1.5');
  for (const value of [undefined, null, {}, Infinity]) assert.equal(text(value), '');
});

test('JSON preserves unsafe decimal integers without altering quoted strings', () => {
  const huge = '9'.repeat(400);
  const value = parseJSON(Buffer.from(`{
    "safe":9007199254740991,"id":9007199254740993,"negative":-9007199254740993,
    "huge":${huge},"list":[18446744073709551615,0,1.5,1e3],
    "quoted":"id=9007199254740993 \\"ok\\"","escaped":"\\\\123"
  }`));
  assert.deepEqual(value, {
    safe: 9007199254740991, id: '9007199254740993', negative: '-9007199254740993',
    huge, list: ['18446744073709551615', 0, 1.5, 1000],
    quoted: 'id=9007199254740993 "ok"', escaped: '\\123',
  });
  assert.equal(parseJSON('9007199254740992'), '9007199254740992');
  assert.equal(parseJSON('-0'), -0);
  assert.deepEqual(parseJSON('{"__proto__":{"safe":true}}'), JSON.parse('{"__proto__":{"safe":true}}'));
});

test('JSON rejects malformed syntax, invalid UTF-8 and executable wrappers', () => {
  for (const source of [
    '', '{"id":01}', '{"id":+1}', '{"id":1.}', '{"id":1e}', '{"id":1e400}',
    '{"id":NaN}', '{"id":Infinity}', '{"id":undefined}', '{"id":1,}',
    '{"id":"bad\nstring"}', '{"id":"\\x41"}', '[1,,2]', 'true false',
    'callback({"id":9007199254740993});', '({"id":1})', '{"id":1};doWork()',
    '{"id":9007199254740993x}', '{"id":09007199254740993}', '{"id":--9007199254740993}',
    '{9007199254740993:1}', '{9007199254740993 \n :1}',
  ]) assert.throws(() => parseJSON(source), code('UPSTREAM_FORMAT'), source);
  assert.throws(() => parseJSON(Buffer.from([0x22, 0xff, 0x22])), code('UPSTREAM_FORMAT'));
});

test('JSONP accepts only a complete identifier call containing JSON', () => {
  const expected = { id: '18446744073709551615', nested: { text: ');' } };
  const body = '{"id":18446744073709551615,"nested":{"text":");"}}';
  assert.deepEqual(jsonObject(body), expected);
  assert.deepEqual(jsonObject(`\n bd__cbs_123 ( ${body} );\n`), expected);
  assert.deepEqual(jsonObject(`bd__cbs_123(${body})`), expected);
  assert.deepEqual(jsonObject(`bd__cbs_123(${body}) \n`), expected);
  for (const source of [
    `window.callback(${body});`, `callback(${body});doWork()`, `callback(${body})\ndoWork()`,
    `callback(${body},{});`, `callback(( ${body} ));`, 'callback({id:1});',
    'callback([1]);', 'callback(null);', '[]', '/*x*/ callback({});', 'new callback({});',
    'callback({"id":(function(){return 1})()});',
  ]) assert.throws(() => jsonObject(source), code('UPSTREAM_FORMAT'), source);
});

test('mergeCookie updates names, preserves equals signs, and removes expired cookies', () => {
  assert.equal(mergeCookie('a=old; b=old; token=part=two', [
    'a=new; Path=/; HttpOnly',
    'b=gone; Max-Age=0',
    'token=updated=part; Secure',
    'expired=no; Expires=Thu, 01 Jan 1970 00:00:00 GMT',
    'live=yes; Max-Age=60; Expires=Thu, 01 Jan 1970 00:00:00 GMT',
    'bad name=ignored',
  ]), 'a=new; token=updated=part; live=yes');
  assert.equal(mergeCookie('a=old', 'a=gone; Max-Age=-1'), '');
  assert.equal(mergeCookie('', ['a=one', 'a=two']), 'a=two');
});

test('CookieJar enforces host, domain, path boundaries and secure delivery', () => {
  const jar = new CookieJar();
  jar.set('https://login.example.test/auth/start', [
    'host=one',
    'wide=two; Domain=.example.test; Path=/',
    'secure=three; Domain=example.test; Path=/; Secure',
    'same=root; Path=/',
    'same=auth; Path=/auth',
    'rejected=bad; Domain=other.test; Path=/',
    'trailing=bad; Domain=example.test.; Path=/',
  ]);
  assert.equal(jar.header('https://login.example.test/auth/next'),
    'host=one; same=auth; wide=two; secure=three; same=root');
  assert.equal(jar.header('https://api.example.test/auth/next'), 'wide=two; secure=three');
  assert.equal(jar.header('http://api.example.test/auth/next'), 'wide=two');
  assert.equal(jar.header('https://login.example.test/authentic'), 'wide=two; secure=three; same=root');
  assert.equal(jar.header('https://notexample.test/'), '');
  assert.equal(jar.header('https://example.test.evil/'), '');
  jar.clear();
  assert.equal(jar.header('https://login.example.test/auth/next'), '');
});

test('CookieJar expiry, replacement identity and default path follow the source URL', () => {
  const original = Date.now;
  let now = 1700000000000;
  Date.now = () => now;
  try {
    const jar = new CookieJar();
    jar.set('https://example.test/a/b', [
      'short=one; Max-Age=1; Path=/',
      'long=two; Max-Age=60; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/',
      'gone=three; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/',
      'default=path',
    ]);
    assert.equal(jar.header('https://example.test/a/c'), 'default=path; short=one; long=two');
    assert.equal(jar.header('https://example.test/abc'), 'short=one; long=two');
    now += 1001;
    assert.equal(jar.header('https://example.test/a/'), 'default=path; long=two');
    jar.set('https://example.test/a/b', ['default=changed; Path=/a', 'long=gone; Max-Age=0; Path=/']);
    assert.equal(jar.header('https://example.test/a'), 'default=changed');
    jar.set('http://127.0.0.1/path', ['ip=one; Domain=0.0.1', 'exact=two; Domain=127.0.0.1']);
    assert.equal(jar.header('http://127.0.0.1/'), 'exact=two');
  } finally { Date.now = original; }
});

test('store persists plaintext JSON atomically with private file and directory modes', t => {
  const root = join(temporary(t), 'new', 'data');
  const store = new PanStore(root);
  const value = { version: 1, accounts: {
    tianyi: { revision: 1, credentials: { cookie: 'synthetic-cookie', password: 'synthetic password' } },
  } };
  assert.deepEqual(store.read(), { version: 1, accounts: {} });
  store.write(value);
  const folder = join(root, 'pan'), path = join(folder, 'accounts.json');
  assert.deepEqual(JSON.parse(fs.readFileSync(path, 'utf8')), value);
  assert.match(fs.readFileSync(path, 'utf8'), /synthetic password/);
  if (process.platform !== 'win32') {
    assert.equal(fs.statSync(folder).mode & 0o777, 0o700);
    assert.equal(fs.statSync(path).mode & 0o777, 0o600);
  }
  store.close();
  assert.deepEqual(new PanStore(root).read(), value);
  assert.deepEqual(fs.readdirSync(folder), ['accounts.json']);
});

test('store refuses corrupt documents even when write was called before read', t => {
  const root = temporary(t), folder = join(root, 'pan'), path = join(folder, 'accounts.json');
  fs.mkdirSync(folder);
  const store = new PanStore(root), next = { version: 1, accounts: {} };
  for (const bad of ['{bad', '', '{"version":2,"accounts":{}}', '{"version":1,"accounts":[]}', 'null']) {
    fs.writeFileSync(path, bad);
    assert.throws(() => store.read(), code('CORRUPT_STORAGE'));
    assert.throws(() => store.write(next), code('CORRUPT_STORAGE'));
    assert.equal(fs.readFileSync(path, 'utf8'), bad);
  }
  fs.writeFileSync(path, Buffer.from([0x7b, 0xff, 0x7d]));
  assert.throws(() => store.read(), code('CORRUPT_STORAGE'));
});

test('store write, flush and rename failures preserve the previous document and clean temporaries', t => {
  const root = temporary(t), store = new PanStore(root);
  const before = { version: 1, accounts: { baidu: { revision: 1, credentials: { cookie: 'synthetic-one' } } } };
  const after = { version: 1, accounts: { baidu: { revision: 2, credentials: { cookie: 'synthetic-two' } } } };
  store.write(before);
  const path = join(root, 'pan', 'accounts.json'), bytes = fs.readFileSync(path);
  for (const method of ['writeFileSync', 'fsyncSync', 'renameSync']) {
    fsFailure(method, () => assert.throws(() => store.write(after), code('STORAGE_ERROR')));
    assert.deepEqual(fs.readFileSync(path), bytes);
    assert.deepEqual(store.read(), before);
    assert.deepEqual(fs.readdirSync(join(root, 'pan')), ['accounts.json']);
  }
});

test('store has no fallible directory sync after the rename commit point', t => {
  const root = temporary(t), store = new PanStore(root);
  const before = { version: 1, accounts: {} };
  const after = { version: 1, accounts: { baidu: { revision: 1, credentials: { cookie: 'synthetic-one' } } } };
  store.write(before);
  const originalRename = fs.renameSync, originalSync = fs.fsyncSync;
  let committed = false, fileSyncs = 0, directorySyncs = 0;
  fs.renameSync = (...args) => {
    const result = originalRename(...args);
    committed = true;
    return result;
  };
  fs.fsyncSync = fd => {
    if (fs.fstatSync(fd).isDirectory()) {
      directorySyncs++;
      throw new Error('synthetic directory sync failure after commit');
    }
    assert.equal(committed, false);
    fileSyncs++;
    return originalSync(fd);
  };
  syncBuiltinESMExports();
  try { assert.doesNotThrow(() => store.write(after)); }
  finally {
    fs.renameSync = originalRename;
    fs.fsyncSync = originalSync;
    syncBuiltinESMExports();
  }
  assert.equal(committed, true);
  assert.equal(fileSyncs, 1);
  assert.equal(directorySyncs, 0);
  assert.deepEqual(new PanStore(root).read(), after);
});

test('store propagates read and directory failures instead of resetting accounts', t => {
  const root = temporary(t), store = new PanStore(root);
  store.write({ version: 1, accounts: {} });
  for (const method of ['statSync', 'readFileSync', 'chmodSync']) fsFailure(method, () => {
    assert.throws(() => store.read(), code('STORAGE_ERROR'));
    assert.throws(() => store.write({ version: 1, accounts: {} }), code('STORAGE_ERROR'));
  });
  assert.deepEqual(store.read(), { version: 1, accounts: {} });
});

test('store reads symbolic and hard links and atomically replaces only its document entry', t => {
  const base = temporary(t), target = join(base, 'target');
  const before = { version: 1, accounts: { baidu: { revision: 1, credentials: { cookie: 'synthetic-one' } } } };
  const after = { version: 1, accounts: { baidu: { revision: 2, credentials: { cookie: 'synthetic-two' } } } };
  fs.writeFileSync(target, JSON.stringify(before));
  const original = fs.readFileSync(target);
  for (const link of ['symbolic', 'hard']) {
    const root = join(base, link), folder = join(root, 'pan'), path = join(folder, 'accounts.json');
    fs.mkdirSync(folder, { recursive: true });
    if (link === 'symbolic') fs.symlinkSync(target, path, 'file');
    else fs.linkSync(target, path);
    const store = new PanStore(root);
    assert.deepEqual(store.read(), before);
    fsFailure('renameSync', () => assert.throws(() => store.write(after), code('STORAGE_ERROR')));
    assert.deepEqual(store.read(), before);
    assert.deepEqual(fs.readFileSync(target), original);
    assert.deepEqual(fs.readdirSync(folder), ['accounts.json']);
    store.write(after);
    store.close();
    assert.deepEqual(new PanStore(root).read(), after);
    assert.deepEqual(JSON.parse(fs.readFileSync(path, 'utf8')), after);
    assert.deepEqual(fs.readFileSync(target), original);
    assert.deepEqual(fs.readdirSync(folder), ['accounts.json']);
  }
});

for (const location of ['ancestor', 'dataRoot', 'pan']) {
  for (const existing of [false, true]) {
    test(`store ${existing ? 'updates existing data' : 'creates new data'} through a ${location} directory alias`, t => {
      const base = temporary(t), target = join(base, 'target'), alias = join(base, 'alias');
      fs.mkdirSync(target);
      const root = location === 'ancestor' ? join(alias, 'nested', 'data') :
        location === 'dataRoot' ? alias : join(base, 'root');
      const folder = location === 'ancestor' ? join(target, 'nested', 'data', 'pan') :
        location === 'dataRoot' ? join(target, 'pan') : target;
      if (location === 'pan') fs.mkdirSync(root);
      fs.symlinkSync(target, location === 'pan' ? join(root, 'pan') : alias,
        process.platform === 'win32' ? 'junction' : 'dir');
      const before = { version: 1, accounts: { baidu: { revision: 1, credentials: { cookie: 'synthetic-one' } } } };
      const after = { version: 1, accounts: { baidu: { revision: 2, credentials: { cookie: 'synthetic-two' } } } };
      const path = join(folder, 'accounts.json');
      if (existing) {
        fs.mkdirSync(folder, { recursive: true });
        fs.writeFileSync(path, JSON.stringify(before));
      }
      const store = new PanStore(root);
      assert.deepEqual(store.read(), existing ? before : { version: 1, accounts: {} });
      store.write(after);
      assert.deepEqual(JSON.parse(fs.readFileSync(path, 'utf8')), after);
      store.close();
      assert.deepEqual(new PanStore(root).read(), after);
      assert.deepEqual(fs.readdirSync(folder), ['accounts.json']);
    });
  }
}

test('store rejects directory documents without changing their contents', t => {
  const base = temporary(t), target = join(base, 'target');
  fs.mkdirSync(target);
  fs.writeFileSync(join(target, 'retained'), 'synthetic existing content');
  for (const link of [false, true]) {
    const root = join(base, link ? 'linked' : 'actual'), folder = join(root, 'pan'), path = join(folder, 'accounts.json');
    fs.mkdirSync(folder, { recursive: true });
    if (link) fs.symlinkSync(target, path, process.platform === 'win32' ? 'junction' : 'dir');
    else {
      fs.mkdirSync(path);
      fs.writeFileSync(join(path, 'retained'), 'synthetic existing content');
    }
    const store = new PanStore(root);
    assert.throws(() => store.read(), code('STORAGE_ERROR'));
    assert.throws(() => store.write({ version: 1, accounts: {} }), code('STORAGE_ERROR'));
    assert.equal(fs.readFileSync(join(path, 'retained'), 'utf8'), 'synthetic existing content');
    assert.deepEqual(fs.readdirSync(path), ['retained']);
    assert.deepEqual(fs.readdirSync(folder), ['accounts.json']);
  }
});

test('store reports real non-directory path failures without changing existing files', t => {
  for (const location of ['ancestor', 'dataRoot', 'pan']) {
    const base = temporary(t), blocked = join(base, location === 'pan' ? 'pan' : 'blocked');
    fs.writeFileSync(blocked, 'synthetic existing file');
    const root = location === 'ancestor' ? join(blocked, 'nested') : location === 'dataRoot' ? blocked : base;
    const store = new PanStore(root);
    // Windows stat reports ENOENT below a file; a subsequent write must still fail.
    if (process.platform === 'win32' && location !== 'pan')
      assert.deepEqual(store.read(), { version: 1, accounts: {} });
    else assert.throws(() => store.read(), code('STORAGE_ERROR'));
    assert.throws(() => store.write({ version: 1, accounts: {} }), code('STORAGE_ERROR'));
    assert.equal(fs.readFileSync(blocked, 'utf8'), 'synthetic existing file');
    assert.deepEqual(fs.readdirSync(base), [location === 'pan' ? 'pan' : 'blocked']);
  }
});

test('store requires an absolute data root without NUL or parent traversal', t => {
  const base = temporary(t);
  assert.throws(() => new PanStore(`${base}/../outside`), code('INVALID_REQUEST'));
  assert.throws(() => new PanStore('relative'), code('INVALID_REQUEST'));
  assert.throws(() => new PanStore(`${base}\0`), code('INVALID_REQUEST'));
});

test('HTTP reuses keepalive sockets and leaves non-2xx protocol decisions to the caller', async t => {
  const seen = [];
  const server = await localHTTP(t, (request, response) => {
    seen.push(request.socket);
    response.writeHead(409, { 'X-Upstream': 'yes', 'Set-Cookie': ['a=one; Path=/', 'b=two; Path=/'] });
    response.end('{"code":123,"id":18446744073709551615}');
  });
  const transport = transportFor(t);
  for (let index = 0; index < 12; index++) {
    const result = await requestJSON({ http: transport }, server.url);
    assert.equal(result.status, 409);
    assert.equal(result.value.id, '18446744073709551615');
    assert.equal(result.headers['x-upstream'], 'yes');
    assert.deepEqual(result.headers['set-cookie'], ['a=one; Path=/', 'b=two; Path=/']);
    assert.equal(result.url, `${server.url}/`);
  }
  assert.equal(new Set(seen).size, 1);
});

test('close releases idle keepalive sockets and leaves no HTTP deadline timers behind', async t => {
  const server = await localHTTP(t, (request, response) => response.end('complete'));
  const transport = transportFor(t);
  const timers = new Set(), socketHandles = new Set();
  const hook = createHook({
    init(id, type) {
      if (type === 'Timeout') timers.add(id);
      if (type === 'TCPWRAP') socketHandles.add(id);
    },
    destroy(id) { timers.delete(id); socketHandles.delete(id); },
  });
  hook.enable();
  try {
    await transport.request({ url: server.url });
    assert.equal(server.sockets.size, 1);
    const remoteClosed = [...server.sockets].map(socket => new Promise(resolve => socket.once('close', resolve)));
    await transport.close();
    await Promise.all(remoteClosed);
    await nextTurn();
    await nextTurn();
    assert.equal(timers.size, 0);
    assert.equal(socketHandles.size, 0);
  } finally { hook.disable(); }
});

test('HTTP sends and returns a large binary body unchanged without a size policy', async t => {
  const bytes = Buffer.alloc(17 * 1024 * 1024 + 19);
  for (let index = 0; index < bytes.length; index++) bytes[index] = index % 256;
  const server = await localHTTP(t, (request, response) => {
    const chunks = [];
    request.on('data', bytes => chunks.push(bytes));
    request.on('end', () => response.end(Buffer.concat(chunks)));
  });
  const transport = transportFor(t);
  const result = await transport.request({ url: server.url, method: 'POST', body: bytes });
  assert.ok(Buffer.isBuffer(result.body));
  assert.deepEqual(result.body, bytes);
});

test('headers mode stops ignored Range infinite bodies without decoding and drains the real connection', async t => {
  const seen = [], closed = [];
  const server = await localHTTP(t, (request, response) => {
    seen.push({ method: request.method, range: request.headers.range });
    const drained = deferred();
    closed.push(drained.promise);
    response.writeHead(200, { 'Content-Encoding': request.url.slice(1), 'X-Media': 'synthetic' });
    response.write('not a compressed representation');
    const timer = setInterval(() => response.write('endless media bytes'), 5);
    response.once('close', () => clearInterval(timer));
    request.socket.once('close', drained.resolve);
  });
  const transport = transportFor(t), controller = new AbortController();
  const timers = new Set(), socketHandles = new Set();
  const hook = createHook({
    init(id, type) {
      if (type === 'Timeout') timers.add(id);
      if (type === 'TCPWRAP') socketHandles.add(id);
    },
    destroy(id) { timers.delete(id); socketHandles.delete(id); },
  });
  hook.enable();
  try {
    for (const encoding of ['gzip', 'br', 'unknown']) {
      const result = await transport.request({ url: server.url + '/' + encoding, responseMode: 'headers',
        headers: { Range: 'bytes=0-0' }, timeout: 1000, signal: controller.signal });
      assert.equal(result.status, 200);
      assert.equal(result.headers['x-media'], 'synthetic');
      assert.equal(result.headers['content-encoding'], encoding);
      assert.equal(result.url, server.url + '/' + encoding);
      assert.ok(Buffer.isBuffer(result.body));
      assert.equal(result.body.length, 0);
    }
    await Promise.all(closed);
    await nextTurn();
    await nextTurn();
    assert.equal(timers.size, 0);
    assert.equal(socketHandles.size, 0);
    assert.equal(server.sockets.size, 0);
    assert.equal(getEventListeners(controller.signal, 'abort').length, 0);
    assert.deepEqual(seen, Array(3).fill({ method: 'GET', range: 'bytes=0-0' }));
  } finally { hook.disable(); }
});

test('headers mode cancellation and close drain requests still waiting for response headers', async t => {
  const arrived = new Map();
  const server = await localHTTP(t, (request, response) => {
    arrived.get(request.url).resolve(request.socket);
  });
  for (const action of ['cancel', 'close']) {
    const ready = deferred();
    arrived.set('/' + action, ready);
    const transport = transportFor(t), controller = new AbortController();
    const result = transport.request({ url: server.url + '/' + action,
      responseMode: 'headers', signal: controller.signal });
    const rejected = assert.rejects(result, code(action === 'cancel' ? 'CANCELLED' : 'CLOSED'));
    const socket = await ready.promise;
    const remoteClosed = new Promise(resolve => socket.once('close', resolve));
    if (action === 'cancel') controller.abort();
    else await transport.close();
    await rejected;
    await remoteClosed;
    assert.equal(getEventListeners(controller.signal, 'abort').length, 0);
    assert.equal(socket.destroyed, true);
  }
});

test('HTTP decodes gzip, deflate, brotli and stacked encodings according to response headers', async t => {
  const bytes = Buffer.from('synthetic response 中文\0'.repeat(10000));
  const responses = new Map([
    ['/gzip', ['gzip', gzipSync(bytes)]],
    ['/deflate', ['deflate', deflateSync(bytes)]],
    ['/br', ['br', brotliCompressSync(bytes)]],
    ['/stack', ['gzip, br', brotliCompressSync(gzipSync(bytes))]],
    ['/identity', ['identity', bytes]],
  ]);
  const server = await localHTTP(t, (request, response) => {
    const [encoding, body] = responses.get(request.url);
    response.writeHead(200, { 'Content-Encoding': encoding });
    response.end(body);
  });
  const transport = transportFor(t);
  for (const path of responses.keys()) {
    const result = await transport.request({ url: server.url + path });
    assert.deepEqual(result.body, bytes);
  }
});

test('HTTP does not decompress absent HEAD or no-content representations', async t => {
  const server = await localHTTP(t, (request, response) => {
    response.writeHead(request.url === '/none' ? 204 : 200, { 'Content-Encoding': 'gzip' });
    response.end();
  });
  const transport = transportFor(t);
  assert.equal((await transport.request({ url: server.url, method: 'HEAD' })).body.length, 0);
  assert.equal((await transport.request({ url: server.url + '/none' })).body.length, 0);
});

test('HTTP closes malformed compressed streams and reports interrupted responses', async t => {
  const server = await localHTTP(t, (request, response) => {
    if (request.url === '/cut') {
      response.writeHead(200, { 'Content-Length': 1000 });
      response.write('partial');
      setImmediate(() => response.destroy());
    } else {
      response.writeHead(200, { 'Content-Encoding': request.url === '/unknown' ? 'unknown' : 'gzip' });
      response.end('invalid encoded data');
    }
  });
  const transport = transportFor(t);
  for (const path of ['/', '/unknown']) {
    await assert.rejects(transport.request({ url: server.url + path }), code('UPSTREAM_FORMAT'));
  }
  await assert.rejects(transport.request({ url: server.url + '/cut' }), code('NETWORK_ERROR'));
});

test('JSON and text helpers serialize only explicit JSON/form inputs and propagate cancellation', async t => {
  const received = [];
  const server = await localHTTP(t, (request, response) => {
    const chunks = [];
    request.on('data', bytes => chunks.push(bytes));
    request.on('end', () => {
      received.push({ headers: request.headers, body: Buffer.concat(chunks).toString('utf8') });
      response.end(request.url === '/json' ? '{"id":9007199254740993}' : 'plain 中文');
    });
  });
  const transport = transportFor(t), controller = new AbortController();
  const ctx = { http: transport, signal: controller.signal };
  assert.deepEqual((await requestJSON(ctx, server.url + '/json', {
    method: 'POST', json: { exact: '9007199254740993', name: '中文' },
  })).value, { id: '9007199254740993' });
  assert.equal((await requestText(ctx, server.url + '/text', {
    method: 'POST', form: { name: 'a b', mark: 'x&y' },
  })).text, 'plain 中文');
  assert.equal(received[0].headers['content-type'], 'application/json');
  assert.equal(received[0].body, '{"exact":"9007199254740993","name":"中文"}');
  assert.equal(received[1].headers['content-type'], 'application/x-www-form-urlencoded');
  assert.equal(received[1].body, 'name=a+b&mark=x%26y');
  assert.equal(getEventListeners(controller.signal, 'abort').length, 0);
  await assert.rejects(requestJSON(ctx, server.url, { json: {}, form: {} }), code('INVALID_REQUEST'));
});

test('helpers preserve explicit byte bodies and do not inject cookies or reinterpret upstream codes', async () => {
  const bytes = Buffer.from([0, 255, 1]), controller = new AbortController();
  let sent;
  const ctx = { signal: controller.signal, http: { async request(options) {
    sent = options;
    return { status: 403, headers: {}, body: Buffer.from('{"code":-1}'), url: options.url };
  } } };
  const result = await requestJSON(ctx, 'https://example.test/', { method: 'POST', body: bytes });
  assert.equal(sent.body, bytes);
  assert.equal(sent.signal, controller.signal);
  assert.equal(sent.headers.cookie, undefined);
  assert.equal(result.status, 403);
  assert.deepEqual(result.value, { code: -1 });
  await assert.rejects(requestJSON(ctx, 'https://example.test/', { json: 1n }), code('INVALID_REQUEST'));
});

test('HTTP redirects are manual by default and same-origin explicit follow retains headers', async t => {
  const received = [];
  const server = await localHTTP(t, (request, response) => {
    received.push(request.headers);
    if (request.url === '/start') {
      response.writeHead(302, { Location: '/final' });
      response.end('redirect body');
    } else response.end('final');
  });
  const transport = transportFor(t);
  const manual = await transport.request({ url: server.url + '/start' });
  assert.equal(manual.status, 302);
  assert.equal(manual.body.toString(), 'redirect body');
  assert.equal(received.length, 1);
  const result = await transport.request({
    url: server.url + '/start', redirect: 'follow', headers: { Cookie: 'synthetic=one', Authorization: 'synthetic' },
  });
  assert.equal(result.url, server.url + '/final');
  assert.equal(result.body.toString(), 'final');
  assert.equal(received[2].cookie, 'synthetic=one');
  assert.equal(received[2].authorization, 'synthetic');
});

test('cross-origin redirects preserve the explicit request headers including Host', async t => {
  let received;
  const destination = await localHTTP(t, (request, response) => {
    received = request.headers;
    response.end('destination');
  });
  const source = await localHTTP(t, (request, response) => {
    response.writeHead(302, { Location: destination.url + '/final' });
    response.end();
  });
  const transport = transportFor(t);
  const result = await transport.request({ url: source.url, redirect: 'follow', headers: {
    Cookie: 'synthetic=one', Authorization: 'synthetic', 'Proxy-Authorization': 'synthetic',
    'X-Private-Token': 'synthetic', Referer: 'https://example.test/?private=synthetic',
    Accept: 'application/json', Host: new URL(source.url).host,
  } });
  assert.equal(result.body.toString(), 'destination');
  assert.equal(received.cookie, 'synthetic=one');
  for (const name of ['authorization', 'proxy-authorization', 'x-private-token']) assert.equal(received[name], 'synthetic');
  assert.equal(received.referer, 'https://example.test/?private=synthetic');
  assert.equal(received.accept, 'application/json');
  assert.equal(received.host, new URL(source.url).host);
});

test('cross-origin 307 and 308 retain the exact POST body and content type', async t => {
  const destinationCalls = [];
  const destination = await localHTTP(t, (request, response) => {
    const chunks = [];
    request.on('data', bytes => chunks.push(bytes));
    request.on('end', () => {
      destinationCalls.push({ method: request.method, body: Buffer.concat(chunks).toString() });
      assert.equal(request.headers['content-type'], 'application/json');
      response.end('received');
    });
  });
  const sourceCalls = [];
  const source = await localHTTP(t, (request, response) => {
    const chunks = [];
    request.on('data', bytes => chunks.push(bytes));
    request.on('end', () => {
      sourceCalls.push({ method: request.method, body: Buffer.concat(chunks).toString() });
      response.writeHead(Number(request.url.slice(1)), { Location: destination.url + '/receiver' });
      response.end();
    });
  });
  const transport = transportFor(t);
  const body = JSON.stringify({ refreshToken: 'synthetic-token', password: 'synthetic-password' });
  for (const status of [307, 308]) {
    const reply = await transport.request({
      url: `${source.url}/${status}`, redirect: 'follow', method: 'POST', body,
      headers: { 'Content-Type': 'application/json' },
    });
    assert.equal(reply.status, 200);
    assert.equal(reply.body.toString(), 'received');
  }
  assert.deepEqual(destinationCalls, [{ method: 'POST', body }, { method: 'POST', body }]);
  assert.deepEqual(sourceCalls, [{ method: 'POST', body }, { method: 'POST', body }]);
});

test('redirects implement POST-to-GET and preserve bodies for 307 without false method-change loops', async t => {
  const received = [];
  const server = await localHTTP(t, (request, response) => {
    const chunks = [];
    request.on('data', bytes => chunks.push(bytes));
    request.on('end', () => {
      received.push({ method: request.method, headers: request.headers, body: Buffer.concat(chunks).toString() });
      if (request.url === '/same' && request.method === 'POST') response.writeHead(303, { Location: '/same' });
      else if (request.url === '/keep') response.writeHead(307, { Location: '/final' });
      response.end('done');
    });
  });
  const transport = transportFor(t);
  await transport.request({ url: server.url + '/same', method: 'POST', body: 'payload',
    headers: { 'Content-Type': 'text/plain' }, redirect: 'follow' });
  assert.equal(received[1].method, 'GET');
  assert.equal(received[1].body, '');
  assert.equal(received[1].headers['content-type'], undefined);
  assert.equal(received[1].headers['content-length'], undefined);
  await transport.request({ url: server.url + '/keep', method: 'POST', body: 'payload', redirect: 'follow' });
  assert.equal(received[3].method, 'POST');
  assert.equal(received[3].body, 'payload');
});

test('redirect loops and malformed locations fail with closed errors', async t => {
  const server = await localHTTP(t, (request, response) => {
    const location = request.url === '/bad' ? 'http://[' : request.url === '/a' ? '/b' : '/a';
    response.writeHead(302, { Location: location });
    response.end();
  });
  const transport = transportFor(t);
  await assert.rejects(transport.request({ url: server.url + '/a', redirect: 'follow' }), code('UPSTREAM_HTTP'));
  await assert.rejects(transport.request({ url: server.url + '/bad', redirect: 'follow' }), code('UPSTREAM_FORMAT'));
});

test('HTTPS redirects reach HTTP destinations without a downgrade access gate', async t => {
  let arrived = 0;
  const server = await localHTTP(t, (request, response) => {
    arrived++;
    if (request.url === '/downgrade') response.end('media');
    else { response.writeHead(302, { Location: server.url + '/downgrade' }); response.end(); }
  });
  // Only the TLS connection is substituted; the redirect is a real local HTTP response.
  const original = https.request;
  https.request = (url, options) => {
    assert.equal(url.protocol, 'https:');
    return http.request(server.url, { ...options, agent: false });
  };
  try {
    const transport = transportFor(t);
    const reply = await transport.request({
      url: 'https://synthetic.example.test/', redirect: 'follow',
    });
    assert.equal(reply.body.toString(), 'media');
    assert.equal(reply.url, server.url + '/downgrade');
    assert.equal(arrived, 2);
  } finally { https.request = original; }
});

test('HTTP URL userinfo reaches the transport as Basic authorization', async t => {
  const received = [];
  const server = await localHTTP(t, (request, response) => {
    received.push(request.headers.authorization);
    response.end('accepted');
  });
  const url = new URL(server.url);
  url.username = 'fixture-user'; url.password = 'fixture-password';
  const reply = await transportFor(t).request({ url: url.href });
  assert.equal(reply.body.toString(), 'accepted');
  assert.deepEqual(received, ['Basic ' + Buffer.from('fixture-user:fixture-password').toString('base64')]);
});
test('cancellation waits for the active socket and does not abort an independent request', async t => {
  const pending = new Map(), both = deferred();
  const server = await localHTTP(t, (request, response) => {
    response.writeHead(200);
    response.write('partial');
    pending.set(request.url, { socket: request.socket, response });
    if (pending.size === 2) both.resolve();
  });
  const transport = transportFor(t), first = new AbortController(), second = new AbortController();
  const one = transport.request({ url: server.url + '/one', signal: first.signal });
  const two = transport.request({ url: server.url + '/two', signal: second.signal });
  const rejected = assert.rejects(one, code('CANCELLED'));
  await both.promise;
  const remoteClosed = new Promise(resolve => pending.get('/one').socket.once('close', resolve));
  first.abort(new Error('private cancellation reason'));
  await rejected;
  await remoteClosed;
  assert.equal(pending.get('/two').socket.destroyed, false);
  pending.get('/two').response.end(' complete');
  assert.equal((await two).body.toString(), 'partial complete');
  assert.equal(getEventListeners(first.signal, 'abort').length, 0);
  assert.equal(getEventListeners(second.signal, 'abort').length, 0);
});

test('timeouts cover the complete response body and release listeners and sockets', async t => {
  const arrived = deferred();
  const server = await localHTTP(t, (request, response) => {
    response.writeHead(200);
    response.write('partial');
    arrived.resolve(request.socket);
  });
  const transport = transportFor(t), controller = new AbortController();
  const result = transport.request({ url: server.url, timeout: 40, signal: controller.signal });
  const rejected = assert.rejects(result, code('TIMEOUT'));
  const socket = await arrived.promise;
  const remoteClosed = new Promise(resolve => socket.once('close', resolve));
  await rejected;
  await remoteClosed;
  assert.equal(getEventListeners(controller.signal, 'abort').length, 0);
});

test('close drains compressed and ordinary requests, idle keepalive sockets and caller listeners', async t => {
  const pending = [], arrived = deferred();
  const server = await localHTTP(t, (request, response) => {
    if (request.url === '/idle') { response.end('idle'); return; }
    if (request.url === '/compressed') {
      response.writeHead(200, { 'Content-Encoding': 'gzip' });
      response.write(gzipSync(Buffer.from('partial')));
    } else response.write('partial');
    pending.push(request.socket);
    if (pending.length === 2) arrived.resolve();
  });
  const transport = transportFor(t);
  await transport.request({ url: server.url + '/idle' });
  const controllers = [new AbortController(), new AbortController()];
  const results = ['/compressed', '/plain'].map((path, index) =>
    assert.rejects(transport.request({ url: server.url + path, signal: controllers[index].signal }), code('CLOSED')));
  await arrived.promise;
  const remoteClosed = pending.map(socket => new Promise(resolve => socket.once('close', resolve)));
  const closing = transport.close();
  assert.equal(transport.close(), closing);
  await closing;
  await Promise.all(results);
  await Promise.all(remoteClosed);
  for (const controller of controllers) assert.equal(getEventListeners(controller.signal, 'abort').length, 0);
  await assert.rejects(transport.request({ url: server.url }), code('CLOSED'));
});

test('pre-aborted and invalid requests make no upstream calls and reveal no caller secrets', async t => {
  let calls = 0;
  const server = await localHTTP(t, (request, response) => { calls++; response.end(); });
  const transport = transportFor(t), controller = new AbortController();
  controller.abort(new Error('synthetic private signal reason'));
  await assert.rejects(transport.request({ url: server.url, signal: controller.signal }), code('CANCELLED'));
  for (const options of [
    { url: 'ftp://example.test' },
    { url: server.url, method: 'bad method' }, { url: server.url, headers: { 'X-Invalid': 'private\nheader' } },
    { url: server.url, timeout: -1 }, { url: server.url, redirect: 'automatic' }, { url: server.url, body: {} },
    { url: server.url, responseMode: 'automatic' },
  ]) await assert.rejects(transport.request(options), code('INVALID_REQUEST'));
  assert.equal(calls, 0);
  assert.equal(getEventListeners(controller.signal, 'abort').length, 0);
});

test('delay cancels long timers, ignores private abort reasons and removes all abort listeners', async () => {
  const timers = new Set();
  const hook = createHook({
    init(id, type) { if (type === 'Timeout') timers.add(id); },
    destroy(id) { timers.delete(id); },
  });
  hook.enable();
  try {
    const controller = new AbortController();
    const waiting = delay(60000, controller.signal);
    assert.equal(getEventListeners(controller.signal, 'abort').length, 1);
    controller.abort(new Error('synthetic private signal reason'));
    await assert.rejects(waiting, code('CANCELLED'));
    assert.equal(getEventListeners(controller.signal, 'abort').length, 0);
    await assert.rejects(delay(60000, controller.signal), code('CANCELLED'));
    const completed = new AbortController();
    await delay(5, completed.signal);
    assert.equal(getEventListeners(completed.signal, 'abort').length, 0);
    await delay(0);
    await nextTurn();
    await nextTurn();
    assert.equal(timers.size, 0);
  } finally { hook.disable(); }
});
