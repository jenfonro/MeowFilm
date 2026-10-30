import test from 'node:test';
import assert from 'node:assert/strict';
import { gzipSync } from 'node:zlib';
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

test('cross-origin redirects do not need domain grants and strip credentials', async t => {
  let contacts = 0, received;
  const destination = await serverFor(t, (req, res) => {
    contacts++; received = req.headers; res.end('arrived');
  });
  const redirect = await serverFor(t, (_req, res) => {
    res.writeHead(302, { Location: `${destination.origin}/target` }); res.end();
  });
  const handler = `async () => (await ${requestExpression(redirect.origin, {
    headers: { authorization: 'synthetic-only', cookie: 'fixture=true', 'x-fixture': 'yes' },
  })}).body`;
  const allowed = await networkHost(t, [redirect, destination], handler);
  assert.equal(await allowed.invoke(target()), 'arrived');
  assert.equal(contacts, 1);
  assert.equal(received.authorization, undefined);
  assert.equal(received.cookie, undefined);
  assert.equal(received['x-fixture'], 'yes');
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

test('compressed response limit applies after real gzip inflation', async t => {
  const server = await serverFor(t, (_req, res) => {
    res.setHeader('Content-Encoding', 'gzip');
    res.end(gzipSync('x'.repeat(4096)));
  });
  const host = await networkHost(t, [server],
    `async () => ${requestExpression(server.origin)}`, { maxResponseBytes: 256 });
  await assert.rejects(host.invoke(target()), { code: 'RESOURCE_LIMIT' });
});

test('stream response size is bounded before full buffering', async t => {
  const server = await serverFor(t, (_req, res) => res.end(Buffer.alloc(2048)));
  const host = await networkHost(t, [server],
    `async () => ${requestExpression(server.origin)}`, { maxResponseBytes: 128 });
  await assert.rejects(host.invoke(target()), { code: 'RESOURCE_LIMIT' });
});

test('cancel physically closes the real HTTP request socket', async t => {
  const reached = deferred(), closed = deferred();
  const server = await serverFor(t, (req, _res) => {
    req.socket.once('close', () => closed.resolve());
    reached.resolve();
  });
  const host = await networkHost(t, [server],
    `async () => ${requestExpression(server.origin)}`);
  const controller = new AbortController();
  const rejected = assert.rejects(host.invoke(target(), { signal: controller.signal }), { code: 'CANCELLED' });
  await reached.promise;
  controller.abort();
  await rejected;
  await closed.promise;
  assert.equal(host.stats().networkRequests, 0);
});

test('restricted headers and unsupported request options fail before I/O', async t => {
  let contacts = 0;
  const server = await serverFor(t, (_req, res) => { contacts++; res.end('unexpected'); });
  for (const [options, code] of [
    [{ headers: { Host: 'other.invalid' } }, 'PERMISSION_DENIED'],
    [{ proxy: 'http://other.invalid' }, 'UNSUPPORTED_OPTION'],
  ]) {
    const host = await networkHost(t, [server],
      `async () => ${requestExpression(server.origin, options)}`);
    await assert.rejects(host.invoke(target()), { code });
  }
  assert.equal(contacts, 0);
});
