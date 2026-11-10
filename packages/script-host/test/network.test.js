import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { Gunzip, gzipSync, deflateSync, brotliCompressSync } from 'node:zlib';
import { createNodeNetwork, createScriptHost } from '../src/index.js';
import { lxScript, target, deferred } from './fixtures.js';
import { serverFor } from './http-fixture.js';

function networkFor(servers, adapterOptions) {
  const aliases = new Map(servers.map(server => [server.origin, server.socketOrigin]));
  const sockets = new Map(servers.map(server => [server.socketOrigin, server.origin]));
  const replaceOrigin = (input, replacements) => {
    const url = new URL(input), replacement = replacements.get(url.origin);
    return replacement === undefined
      ? url.href
      : replacement + url.pathname + url.search + url.hash;
  };
  const network = createNodeNetwork(adapterOptions);
  return input => network({
    ...input,
    validateURL(next) {
      const validated = input.validateURL(replaceOrigin(next, sockets));
      return replaceOrigin(validated, aliases);
    },
  });
}

async function networkHost(t, servers, handler, adapterOptions) {
  const host = createScriptHost({ network: networkFor(servers, adapterOptions) });
  t.after(() => host.close());
  await host.install({ installationId: 'a', enabled: true,
    source: lxScript({ handler }) });
  return host;
}

const requestExpression = (url, options = {}) => `new Promise((resolve,reject)=>
  lx.request(${JSON.stringify(url)},${JSON.stringify(options)},(e,r,b)=>e?reject(e):resolve(r)))`;

test('real HTTP callback preserves status/body/raw and desktop JSON semantics', async t => {
  const server = await serverFor(t, (req, res) => {
    if (req.url === '/json') { res.setHeader('Content-Type', 'text/plain'); res.end('{"ok":true}'); }
    else if (req.url === '/binary') { res.end(Buffer.from([0, 255, 128])); }
    else if (req.url === '/error') { res.statusCode = 503; res.end('unavailable'); }
    else { res.setHeader('Content-Type', 'application/json'); res.end('not-json'); }
  });
  const host = await networkHost(t, [server], `async () => {
    const json = await ${requestExpression(server.origin + '/json')};
    const binary = await ${requestExpression(server.origin + '/binary', { encoding: null })};
    const plain = await ${requestExpression(server.origin + '/invalid')};
    const failure = await ${requestExpression(server.origin + '/error')};
    return {json:json.body,raw:json.raw.toString(),binary:binary.body.toString('hex'),
      alias:binary.body === binary.raw,buffer:Buffer.isBuffer(binary.body),plain:plain.body,
      failure:failure.statusCode,failureBody:failure.body};
  }`);
  assert.deepEqual(await host.invoke(target()), { json: { ok: true }, raw: '{"ok":true}',
    binary: '00ff80', alias: true, buffer: true, plain: 'not-json', failure: 503, failureBody: 'unavailable' });
});

test('json:true malformed response reports INVALID_JSON', async t => {
  const server = await serverFor(t, (_req, res) => res.end('not-json'));
  const host = await networkHost(t, [server],
    `async () => ${requestExpression(server.origin, { json: true })}`);
  await assert.rejects(host.invoke(target()), { code: 'INVALID_JSON' });
});

test('LX retry extension is accepted without automatic GET or non-idempotent POST replay', async t => {
  const methods = [];
  const server = await serverFor(t, (req, res) => {
    methods.push(req.method);
    res.statusCode = 503; res.end('unavailable');
  });
  const host = await networkHost(t, [server], `async () => {
    const get = await ${requestExpression(server.origin, { retry: 3 })};
    const post = await ${requestExpression(server.origin, { method: 'POST', body: 'value', retry: 1 })};
    return [get.statusCode,post.statusCode];
  }`);
  assert.deepEqual(await host.invoke(target()), [503, 503]);
  assert.deepEqual(methods, ['GET', 'POST']);
});

test('real POST JSON, urlencoded and binary multipart use exact byte bodies', async t => {
  const server = await serverFor(t, (req, res) => {
    const chunks = [];
    req.on('data', b => chunks.push(b));
    req.on('end', () => res.end(JSON.stringify({ method: req.method, contentType: req.headers['content-type'],
      text: Buffer.concat(chunks).toString('latin1') })));
  });
  const host = await networkHost(t, [server], `async () => {
    const post = await ${requestExpression(server.origin, { method: 'POST', body: { name: 'synthetic' }, json: true })};
    const form = await ${requestExpression(server.origin, { method: 'POST', form: { name: 'a b' } })};
    const multipart = await new Promise((resolve,reject)=>lx.request(${JSON.stringify(server.origin)},{
      method:'POST',formData:{name:'synthetic',file:{value:Buffer.from([0,255,65]),options:{filename:'fixture.bin'}}}
    },(e,r,b)=>e?reject(e):resolve(b)));
    return {post:post.body,form:form.body,multipart};
  }`);
  const result = await host.invoke(target());
  assert.equal(result.post.text, '{"name":"synthetic"}');
  assert.equal(result.post.method, 'POST');
  assert.equal(result.post.contentType, 'application/json');
  assert.equal(result.form.text, 'name=a+b');
  assert.match(result.multipart.contentType, /^multipart\/form-data; boundary=meowfilm-/);
  assert(result.multipart.text.includes('\x00\xffA'));
  assert(result.multipart.text.includes('filename="fixture.bin"'));
});

test('cross-origin redirects preserve explicit caller headers without destination grants', async t => {
  let contacts = 0, received;
  const destination = await serverFor(t, (req, res) => {
    contacts++; received = req.headers; res.end('arrived');
  });
  const redirect = await serverFor(t, (_req, res) => {
    res.writeHead(302, { Location: `${destination.origin}/target` }); res.end();
  });
  const handler = `async () => (await ${requestExpression(redirect.origin, {
    headers: { authorization: 'synthetic-only', cookie: 'fixture=true', host: 'source.virtual.invalid',
      origin: 'https://caller.invalid', 'x-fixture': 'yes' },
  })}).body`;
  const allowed = await networkHost(t, [redirect, destination], handler);
  assert.equal(await allowed.invoke(target()), 'arrived');
  assert.equal(contacts, 1);
  assert.equal(received.authorization, 'synthetic-only');
  assert.equal(received.cookie, 'fixture=true');
  assert.equal(received.host, 'source.virtual.invalid');
  assert.equal(received.origin, 'https://caller.invalid');
  assert.equal(received['x-fixture'], 'yes');
});

test('LX HTTP accepts caller Host, Origin and URL credentials without destination grants', async t => {
  let received;
  const server = await serverFor(t, (request, response) => {
    received = { path: request.url, headers: request.headers };
    response.end('virtual-host-response');
  });
  const host = createScriptHost({ network: createNodeNetwork() });
  t.after(() => host.close());
  const url = new URL('/normal?opaque=a%2Fb', server.socketOrigin);
  url.username = 'fixture-user'; url.password = 'fixture-password';
  await host.install({ installationId: 'a', enabled: true, source: lxScript({
    handler: `async () => (await ${requestExpression(url.href, {
      headers: { Host: 'media.virtual.invalid', Origin: 'https://caller.invalid' },
    })}).body`,
  }) });
  assert.equal(await host.invoke(target()), 'virtual-host-response');
  assert.equal(received.path, '/normal?opaque=a%2Fb');
  assert.equal(received.headers.host, 'media.virtual.invalid');
  assert.equal(received.headers.origin, 'https://caller.invalid');
  assert.equal(received.headers.authorization, 'Basic ' + Buffer.from('fixture-user:fixture-password').toString('base64'));
});

test('followRedirect extension can explicitly stop or follow redirects within the hop budget', async t => {
  let contacts = 0;
  const destination = await serverFor(t, (_req, res) => { contacts++; res.end('arrived'); });
  const redirect = await serverFor(t, (_req, res) => {
    res.writeHead(302, { Location: destination.origin + '/target' }); res.end('redirect');
  });
  const host = await networkHost(t, [redirect, destination], `async ({info}) => {
    const response = await new Promise((resolve,reject) => lx.request(${JSON.stringify(redirect.origin)},
      {followRedirect:info.musicInfo.follow,follow_max:5},(error,response)=>error?reject(error):resolve(response)));
    return {status:response.statusCode,body:response.body};
  }`);
  assert.deepEqual(await host.invoke(target('a', 'kw', { follow: false })), { status: 302, body: 'redirect' });
  assert.equal(contacts, 0);
  assert.deepEqual(await host.invoke(target('a', 'kw', { follow: true })), { status: 200, body: 'arrived' });
  assert.equal(contacts, 1);
  await assert.rejects(host.invoke(target('a', 'kw', { follow: 'yes' })), { code: 'INVALID_ARGUMENT' });
  assert.equal(contacts, 1);
});

test('redirect loops have a finite hop budget', async t => {
  let contacts = 0;
  const server = await serverFor(t, (_req, res) => { contacts++; res.writeHead(302, { Location: '/' }); res.end(); });
  const host = await networkHost(t, [server],
    `async () => ${requestExpression(server.origin)}`, { maxRedirects: 2 });
  await assert.rejects(host.invoke(target()), { code: 'RESOURCE_LIMIT' });
  assert.equal(contacts, 3);
});

test('follow_max above the shared redirect ceiling is truncated instead of rejected', async t => {
  let contacts = 0;
  const destination = await serverFor(t, (_req, res) => { contacts++; res.end('arrived'); });
  const redirect = await serverFor(t, (_req, res) => {
    res.writeHead(302, { Location: destination.origin + '/target' }); res.end();
  });
  const host = await networkHost(t, [redirect, destination], `async () => {
    const response = await new Promise((resolve,reject) => lx.request(${JSON.stringify(redirect.origin)},
      {follow_max:6},(error,response)=>error?reject(error):resolve(response)));
    return {status:response.statusCode,body:response.body};
  }`, { maxRedirects: 5 });
  assert.deepEqual(await host.invoke(target()), { status: 200, body: 'arrived' });
  assert.equal(contacts, 1);
});

test('compressed response returns the complete body after real gzip inflation', async t => {
  const server = await serverFor(t, (_req, res) => {
    res.setHeader('Content-Encoding', 'gzip');
    res.end(gzipSync('x'.repeat(4096)));
  });
  const host = await networkHost(t, [server],
    `async () => (await ${requestExpression(server.origin)}).body`);
  assert.equal(await host.invoke(target()), 'x'.repeat(4096));
});

test('decoded gzip response headers describe the decoded bytes', async t => {
  const body = Buffer.from('compressed-fixture');
  const compressed = gzipSync(body);
  const server = await serverFor(t, (_req, res) => {
    res.writeHead(200, { 'content-encoding': 'gzip', 'content-length': compressed.length });
    res.end(compressed);
  });
  const host = await networkHost(t, [server], `async () => {
    const response = await ${requestExpression(server.origin)};
    return {body:response.raw.toString(), encoding:response.headers['content-encoding'] ?? null,
      length:response.headers['content-length'] ?? null};
  }`);
  assert.deepEqual(await host.invoke(target()), {
    body: body.toString(), encoding: null, length: null,
  });
});

test('complete JSON larger than the image budget survives stacked content decoding', async t => {
  const body = Buffer.from(JSON.stringify({ value: 'x'.repeat(4 * 1024 * 1024 + 1), last: 'complete' }));
  const compressed = brotliCompressSync(gzipSync(body));
  const requests = observeRequests(t);
  const server = await serverFor(t, (_req, res) => {
    res.writeHead(200, { 'content-type': 'application/json', 'content-encoding': 'gzip, br',
      'content-length': compressed.length });
    res.end(compressed);
  });
  const result = await createNodeNetwork()({ url: server.socketOrigin,
    signal: new AbortController().signal, validateURL: value => value });
  assert.deepEqual(result.body, body);
  assert.equal(JSON.parse(result.body).last, 'complete');
  assert.equal(result.headers['content-encoding'], undefined);
  assert.equal(result.headers['content-length'], undefined);
  assertRequestsClosed(requests);
});

test('HEAD and no-content responses retain representation headers without inflating a missing body', async t => {
  const requests = observeRequests(t);
  const server = await serverFor(t, (req, res) => {
    const status = Number(req.url.slice(1));
    res.writeHead(status, { 'content-encoding': 'gzip', ...(status === 200 || status === 304
      ? { 'content-length': '128' } : {}) });
    res.end();
  });
  const network = createNodeNetwork();
  for (const [method, status] of [['HEAD', 200], ['GET', 204], ['GET', 205], ['GET', 304]]) {
    const response = await network({ url: `${server.socketOrigin}/${status}`, options: { method },
      signal: new AbortController().signal, validateURL: value => value });
    assert.equal(response.statusCode, status);
    assert.equal(response.body.length, 0);
    assert.equal(response.headers['content-encoding'], 'gzip');
    assertRequestsClosed(requests);
  }
});

test('corrupt compressed upstream data is a network failure, not a memory limit', async t => {
  const server = await serverFor(t, (_req, res) => {
    res.setHeader('content-encoding', 'gzip');
    res.end('invalid gzip');
  });
  const host = await networkHost(t, [server],
    `async () => ${requestExpression(server.origin)}`);
  await assert.rejects(host.invoke(target()), { code: 'NETWORK_ERROR' });
});

test('complete-response API retains every received binary byte', async t => {
  const server = await serverFor(t, (_req, res) => res.end(Buffer.alloc(2048)));
  const host = await networkHost(t, [server],
    `async () => { const response = await ${requestExpression(server.origin)};
      return {length: response.raw.length, first: response.raw[0], last: response.raw[response.raw.length - 1]}; }`);
  assert.deepEqual(await host.invoke(target()), {length: 2048, first: 0, last: 0});
});

test('explicit LX abort physically closes a session-owned real HTTP request socket', async t => {
  const reached = deferred(), closed = deferred();
  const server = await serverFor(t, (req, _res) => {
    req.socket.once('close', () => closed.resolve());
    reached.resolve();
  });
  const host = await networkHost(t, [server],
    `(() => {
      let abort;
      return async ({info}) => {
        if (info.musicInfo.abort) { abort(); return 'aborted'; }
        return new Promise((resolve, reject) => {
          abort = lx.request(${JSON.stringify(server.origin)}, {},
            (error, response) => error ? reject(error) : resolve(response));
        });
      };
    })()`);
  const controller = new AbortController();
  const rejected = assert.rejects(host.invoke(target(), { signal: controller.signal }), { code: 'CANCELLED' });
  await reached.promise;
  controller.abort();
  await rejected;
  assert.equal(await host.invoke(target('a', 'kw', { abort: true })), 'aborted');
  await closed.promise;
  const deadline = Date.now() + 3000;
  while (host.stats().networkRequests !== 0) {
    assert.ok(Date.now() < deadline, 'aborted network request did not retire from the worker snapshot');
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(host.stats().networkRequests, 0);
});

test('transport framing headers and unsupported request options fail before I/O', async t => {
  let contacts = 0;
  const server = await serverFor(t, (_req, res) => { contacts++; res.end('unexpected'); });
  for (const [options, code] of [
    [{ headers: { 'Content-Length': '10' } }, 'PERMISSION_DENIED'],
    [{ proxy: 'http://other.invalid' }, 'UNSUPPORTED_OPTION'],
  ]) {
    const host = await networkHost(t, [server],
      `async () => ${requestExpression(server.origin, options)}`);
    await assert.rejects(host.invoke(target()), { code });
  }
  assert.equal(contacts, 0);
});

function observeRequests(t, beforeRequest = () => {}, onResponse = () => {}) {
  const requests = [];
  const original = http.request;
  t.mock.method(http, 'request', function(...args) {
    beforeRequest(requests);
    const request = Reflect.apply(original, this, args), resources = [];
    const own = (kind, stream) => {
      const resource = { kind, stream, closed: false };
      resources.push(resource);
      stream.once('close', () => { resource.closed = true; });
    };
    own('request', request);
    request.once('socket', socket => own('socket', socket));
    request.prependOnceListener('response', response => {
      own('response', response);
      onResponse(response);
    });
    requests.push(resources);
    return request;
  });
  return requests;
}

function assertRequestsClosed(requests) {
  assert.ok(requests.length > 0);
  for (const resources of requests) {
    assert.deepEqual(resources.map(value => value.kind).sort(), ['request', 'response', 'socket']);
    for (const { kind, stream, closed } of resources) {
      assert.equal(closed, true, `${kind} close must precede adapter settlement`);
      assert.equal(stream.destroyed, true, `${kind} must have released its transport`);
    }
  }
}

function observeDecodePipeline(t, onDecoded = () => {}) {
  const streams = [];
  const requests = observeRequests(t, undefined, response => {
    function inspect(stream) {
      const original = stream.pipe;
      t.mock.method(stream, 'pipe', function(destination, ...options) {
        const state = { stream: destination, closed: false };
        streams.push(state);
        destination.once('close', () => { state.closed = true; });
        inspect(destination);
        const result = Reflect.apply(original, this, [destination, ...options]);
        if (stream instanceof Gunzip) stream.once('data', onDecoded);
        return result;
      });
    }
    inspect(response);
  });
  return () => {
    assertRequestsClosed(requests);
    assert.ok(streams.some(value => value.stream instanceof Gunzip), 'a real decoder must have run');
    for (const { stream, closed } of streams) {
      assert.equal(closed, true, 'decoder and collector close before adapter settlement');
      assert.equal(stream.destroyed, true);
    }
  };
}

test('successful compressed output closes the decoder and collector before returning the complete body', async t => {
  const closed = observeDecodePipeline(t);
  const body = Buffer.from('complete decoded payload');
  const server = await serverFor(t, (_req, res) => {
    res.writeHead(200, { 'content-encoding': 'gzip' });
    res.end(gzipSync(body));
  });
  const result = await createNodeNetwork()({ url: server.socketOrigin,
    signal: new AbortController().signal, validateURL: value => value });
  assert.deepEqual(result.body, body);
  closed();
});

test('cancellation during decoded output drains the decoder without waiting for the rest of the response',
  { timeout: 5000 }, async t => {
    const decoded = deferred(), controller = new AbortController();
    const closed = observeDecodePipeline(t, () => decoded.resolve());
    const server = await serverFor(t, (_req, res) => {
      res.writeHead(200, { 'content-encoding': 'gzip' });
      res.write(gzipSync(Buffer.alloc(128 * 1024, 97)));
      // This complete compressed member arrives before the HTTP body ends.
      // A full-compressed-response buffer could not reach the decoder yet.
    });
    const failure = assert.rejects(createNodeNetwork()({ url: server.socketOrigin,
      signal: controller.signal, validateURL: value => value }), { code: 'CANCELLED' });
    await decoded.promise;
    controller.abort();
    await failure;
    closed();
  });

test('truncated compressed content drains every decoder and fails instead of publishing partial data', async t => {
  const closed = observeDecodePipeline(t);
  const compressed = gzipSync(Buffer.from('complete-data-only'));
  const server = await serverFor(t, (_req, res) => {
    res.writeHead(200, { 'content-encoding': 'gzip' });
    res.end(compressed.subarray(0, compressed.length - 4));
  });
  await assert.rejects(createNodeNetwork()({ url: server.socketOrigin,
    signal: new AbortController().signal, validateURL: value => value }), { code: 'NETWORK_ERROR' });
  closed();
});

test('complete network adapter settles only after its request, response and socket close', async t => {
  const requests = observeRequests(t);
  const body = Buffer.from([0, 255, 65]);
  const server = await serverFor(t, (_req, res) => res.end(body));
  const result = await createNodeNetwork()({ url: server.socketOrigin,
    signal: new AbortController().signal, validateURL: value => value });
  assert.deepEqual(result.body, body);
  assert.equal(result.statusCode, 200);
  assertRequestsClosed(requests);
});

test('redirect adapter closes an unfinished hop before opening the next request', async t => {
  const requests = observeRequests(t, previous => {
    if (previous.length) assertRequestsClosed(previous);
  });
  const server = await serverFor(t, (req, res) => {
    if (req.url === '/redirect') {
      res.writeHead(302, { location: '/target' });
      res.flushHeaders(); // This body never ends; following must not collect it.
    } else res.end('target');
  });
  const result = await createNodeNetwork()({ url: server.socketOrigin + '/redirect',
    signal: new AbortController().signal, validateURL: value => value });
  assert.equal(result.body.toString(), 'target');
  assert.equal(requests.length, 2);
  assertRequestsClosed(requests);
});

for (const scenario of ['abort', 'truncated']) {
  test(`${scenario} network response closes every transport before rejecting`, async t => {
    const entered = deferred(), release = deferred();
    // A received chunk proves this is cancellation/failure after headers, not
    // cancellation before a connection exists.
    const requests = observeRequests(t, undefined, response => {
      response.once('data', () => entered.resolve());
    });
    const controller = new AbortController();
    const server = await serverFor(t, (_req, res) => {
      res.writeHead(200, { 'content-length': 100 });
      res.write('partial');
      release.promise.then(() => res.destroy());
    });
    t.after(() => release.resolve());
    const failure = assert.rejects(createNodeNetwork()({ url: server.socketOrigin,
      signal: controller.signal, validateURL: value => value }),
    { code: scenario === 'abort' ? 'CANCELLED' : 'NETWORK_ERROR' });
    await entered.promise;
    if (scenario === 'abort') controller.abort();
    else release.resolve();
    await failure;
    assertRequestsClosed(requests);
  });
}

test('network decoding accepts only own encoding names and closes on rejection', async t => {
  const requests = observeRequests(t);
  const body = Buffer.from([0, 255, 65]);
  const encoders = { gzip: gzipSync, deflate: deflateSync, br: brotliCompressSync };
  const server = await serverFor(t, (req, res) => {
    const encoding = req.url.slice(1);
    res.setHeader('content-encoding', encoding);
    res.end(Object.hasOwn(encoders, encoding) ? encoders[encoding](body) : body);
  });
  const network = createNodeNetwork();
  for (const encoding of ['identity', 'gzip', 'deflate', 'br', 'constructor', '__proto__', 'x-unknown']) {
    const operation = network({ url: `${server.socketOrigin}/${encoding}`,
      signal: new AbortController().signal, validateURL: value => value });
    if (encoding === 'identity' || Object.hasOwn(encoders, encoding)) {
      const result = await operation;
      assert.deepEqual(result.body, body);
      assert.equal(result.headers['content-encoding'], encoding === 'identity' ? encoding : undefined);
    } else await assert.rejects(operation, { code: 'UNSUPPORTED_OPTION' });
    assertRequestsClosed(requests);
  }
});

test('LX callbacks receive the unsupported encoding error without a false decoded response', async t => {
  const server = await serverFor(t, (req, res) => {
    res.setHeader('content-encoding', req.url.slice(1));
    res.end('unchanged');
  });
  const host = await networkHost(t, [server], `async ({info}) => new Promise(resolve =>
    lx.request(${JSON.stringify(server.origin)}+'/'+info.musicInfo.encoding,{},
      (error,response,body)=>resolve({code:error?.code??null,response,body})))`);
  for (const encoding of ['constructor', '__proto__', 'x-unknown'])
    assert.deepEqual(await host.invoke(target('a', 'kw', { encoding })),
      { code: 'UNSUPPORTED_OPTION', response: null, body: null });
});
