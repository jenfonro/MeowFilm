import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { networkInterfaces } from 'node:os';
import { Readable } from 'node:stream';
import { createHttpClient, createHttpServer, HTTP_PROTOCOL, validateEndpoint } from '../src/index.js';
import { deferred, pair, turn } from './http-fixture.js';

function exchange(baseUrl, { path = '/v1/echo', method = 'POST', body = method === 'POST' ? '{}' : undefined, headers = {} } = {}) {
  return new Promise((resolve, reject) => {
    const request = http.request(new URL(path, baseUrl), { method, headers: {
      ...(body === undefined ? {} : { 'content-type': 'application/json' }), ...headers,
    } }, response => {
      const chunks = [];
      response.on('data', bytes => chunks.push(bytes));
      response.on('error', reject);
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers,
        body: Buffer.concat(chunks) }));
    });
    request.on('error', reject);
    request.end(body);
  });
}

test('external Host and any browser Origin dispatch without credentials or a caller session', async t => {
  let calls = 0;
  const { server, identity } = await pair(t, (value, context) => {
    calls++;
    assert.equal(context.sessionId, identity.sessionId);
    assert.equal(context.isCurrent(), true);
    return value;
  });
  const ids = new Set();
  for (const headers of [
    { host: 'outside.example.invalid', origin: 'https://browser.example.invalid' },
    { host: '192.168.10.50:1234', origin: 'null' },
    { host: '[2001:db8::1]:5678', origin: 'http://localhost:7777' },
    { authorization: 'Bearer unrelated', 'x-meow-session': 'unrelated',
      origin: 'https://another.example.invalid' },
    { authorization: ['Bearer first', 'Bearer second'] },
  ]) {
    const reply = await exchange(server.baseUrl, { headers, body: '"public"' });
    assert.equal(reply.status, 200);
    assert.deepEqual(JSON.parse(reply.body), { ok: true, contract: HTTP_PROTOCOL, value: 'public' });
    assert.equal(reply.headers['access-control-allow-origin'], '*');
    assert.match(reply.headers['x-meow-request-id'], /^[0-9a-f-]{36}$/);
    assert.equal(ids.has(reply.headers['x-meow-request-id']), false);
    ids.add(reply.headers['x-meow-request-id']);
    assert.match(reply.headers['access-control-expose-headers'], /X-Meow-Request-Id/i);
  }
  assert.equal(calls, 5);
});

test('browser preflight permits arbitrary origins and request headers without dispatch', async t => {
  let calls = 0;
  const { server } = await pair(t, () => { calls++; return null; });
  const reply = await exchange(server.baseUrl, { method: 'OPTIONS', body: undefined, headers: {
    host: 'app.example.invalid', origin: 'https://web.example.invalid',
    'access-control-request-method': 'POST',
    'access-control-request-headers': 'content-type,x-meow-request-id,authorization,x-custom',
    'access-control-request-private-network': 'true',
  } });
  assert.equal(reply.status, 204);
  assert.equal(reply.body.length, 0);
  assert.equal(reply.headers['access-control-allow-origin'], '*');
  assert.match(reply.headers['access-control-allow-methods'], /POST/);
  assert.equal(reply.headers['access-control-allow-headers'],
    'content-type,x-meow-request-id,authorization,x-custom');
  assert.equal(reply.headers['access-control-allow-private-network'], 'true');
  assert.equal(calls, 0);
  const error = await exchange(server.baseUrl, { body: '{invalid', headers: {
    origin: 'https://web.example.invalid', host: 'another.example.invalid',
  } });
  assert.equal(error.status, 400);
  assert.equal(error.headers['access-control-allow-origin'], '*');
  assert.equal(JSON.parse(error.body).code, 'INVALID_REQUEST');
});

test('stream responses expose CORS and generated correlation without access credentials', async t => {
  let disposed = false;
  const server = createHttpServer({ resolve: ({ method, url }) => {
    if (method !== 'GET' || url.pathname !== '/v1/bytes') return;
    return { responseType: 'stream', contract: 'bytes.test.v1', codes: new Set(),
      handler: () => ({ status: 200, headers: { 'content-type': 'application/octet-stream' },
        body: Readable.from([Buffer.from([0, 255, 1])]), dispose: async () => { disposed = true; } }) };
  } });
  t.after(() => server.close());
  await server.start();
  const reply = await exchange(server.baseUrl, { method: 'GET', path: '/v1/bytes', body: undefined,
    headers: { host: 'remote.example.invalid', origin: 'https://web.example.invalid' } });
  assert.equal(reply.status, 200);
  assert.deepEqual(reply.body, Buffer.from([0, 255, 1]));
  assert.equal(reply.headers['access-control-allow-origin'], '*');
  assert.match(reply.headers['x-meow-request-id'], /^[0-9a-f-]{36}$/);
  await server.close();
  assert.equal(disposed, true);
  assert.deepEqual(server.state, { closed: true, active: 0 });
});

test('HTTP clients accept DNS, private, IPv6 and default-port HTTP(S) endpoints without credentials', async () => {
  for (const baseUrl of ['http://localhost', 'http://example.invalid:43210', 'https://example.invalid',
    'http://10.0.0.2:43210', 'http://192.168.1.2', 'http://169.254.1.2:43210',
    'http://[2001:db8::1]:43210', 'https://127.0.0.1:43210']) {
    const client = createHttpClient({ baseUrl });
    await client.close();
    assert.deepEqual(client.state, { closed: true, pending: 0 });
  }
});

test('standard URL userinfo preserves the actual HTTP destination without an extra authentication configuration', async t => {
  for (const [baseUrl, hostname, port, username, password] of [
    ['http://fixture-user@external.example.invalid:43210', 'external.example.invalid', '43210', 'fixture-user', ''],
    ['http://fixture-user:fixture-password@external.example.invalid:43210',
      'external.example.invalid', '43210', 'fixture-user', 'fixture-password'],
    ['https://:fixture-password@external.example.invalid', 'external.example.invalid', '', '', 'fixture-password'],
    ['https://fixture%40user:fixture%3Apassword@external.example.invalid',
      'external.example.invalid', '', 'fixture%40user', 'fixture%3Apassword'],
    ['http://127.0.0.1:123@elsewhere:456', 'elsewhere', '456', '127.0.0.1', '123'],
  ]) {
    const endpoint = validateEndpoint(baseUrl);
    assert.equal(endpoint.hostname, hostname);
    assert.equal(endpoint.port, port);
    assert.equal(endpoint.username, username);
    assert.equal(endpoint.password, password);
    const client = createHttpClient({ baseUrl });
    await client.close();
  }
  const { server } = await pair(t, value => value);
  const endpoint = new URL(server.baseUrl);
  endpoint.username = 'fixture-user';
  endpoint.password = 'fixture-password';
  const client = createHttpClient({ baseUrl: endpoint.href });
  t.after(() => client.close());
  assert.equal((await client.request({ path: '/v1/echo', body: 'userinfo' })).value, 'userinfo');
});

test('the common listener and client exchange through a real non-loopback endpoint', async t => {
  const address = Object.values(networkInterfaces()).flat().find(item => item?.family === 'IPv4' && !item.internal)?.address;
  assert.ok(address, 'N5 requires a real non-loopback IPv4 interface');
  const { server } = await pair(t, value => value);
  const baseUrl = `http://${address}:${new URL(server.baseUrl).port}`;
  const raw = await exchange(baseUrl, { headers: { host: 'remote.example.invalid',
    origin: 'https://browser.example.invalid' }, body: '"external"' });
  assert.equal(raw.status, 200);
  assert.equal(JSON.parse(raw.body).value, 'external');
  const client = createHttpClient({ baseUrl });
  t.after(() => client.close());
  assert.equal((await client.request({ path: '/v1/echo', body: 'non-loopback' })).value, 'non-loopback');
});

test('outgoing calls carry request correlation without session or authorization headers', async t => {
  const server = http.createServer((request, response) => {
    assert.equal(request.headers.authorization, undefined);
    assert.equal(request.headers['x-meow-session'], undefined);
    assert.match(request.headers['x-meow-request-id'], /^[0-9a-f-]{36}$/);
    request.resume();
    response.writeHead(200, { 'content-type': 'application/json',
      'x-meow-request-id': request.headers['x-meow-request-id'] });
    response.end(JSON.stringify({ ok: true, contract: HTTP_PROTOCOL, value: null }));
  });
  await new Promise(resolve => server.listen(0, resolve));
  const client = createHttpClient({ baseUrl: `http://localhost:${server.address().port}` });
  t.after(async () => { await client.close(); await new Promise(resolve => server.close(resolve)); });
  assert.equal((await client.request({ path: '/v1/echo', body: {} })).value, null);
});

test('close aborts external requests and waits for their actual handlers before releasing ownership', async t => {
  const entered = deferred(), release = deferred(), completed = deferred();
  t.after(() => release.resolve());
  let signal, closed = false, onClose = 0;
  const { server } = await pair(t, async (_value, context) => {
    signal = context.signal;
    entered.resolve();
    await release.promise;
    completed.resolve();
    return 'late';
  }, { onClose() { onClose++; } });
  const pending = exchange(server.baseUrl, { headers: { host: 'external.example.invalid',
    origin: 'https://browser.example.invalid' } }).catch(error => error);
  await entered.promise;
  const closing = server.close().then(() => { closed = true; });
  await turn();
  assert.equal(signal.aborted, true);
  assert.equal(closed, false);
  assert.equal(onClose, 0);
  assert.deepEqual(server.state, { closed: true, active: 1 });
  release.resolve();
  await completed.promise;
  await closing;
  assert.equal((await pending) instanceof Error, true);
  assert.deepEqual(server.state, { closed: true, active: 0 });
  assert.equal(onClose, 1);
  await assert.rejects(exchange(server.baseUrl));
});
