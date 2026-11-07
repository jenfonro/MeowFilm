import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { createHttpClient, createHttpServer } from '../src/index.js';
import { pair, credentials, deferred } from './http-fixture.js';

test('connection reuse does not bind session lifetime to one keepalive socket', async t => {
  const { call, client, server, identity } = await pair(t, value => value);
  assert.equal((await call(1)).value, 1);
  await client.close();
  const another = createHttpClient({ ...identity, baseUrl: server.baseUrl });
  t.after(() => another.close());
  assert.equal((await another.request({ path: '/v1/echo', body: 2 })).value, 2);
});

test('invalid endpoint/session/onClose fails before allocation', () => {
  const identity = credentials();
  for (const baseUrl of ['https://127.0.0.1:1', 'http://localhost:1', 'http://example.invalid:1',
    'http://user:secret@127.0.0.1:1', 'http://127.0.0.1:1/callback', 'http://127.0.0.1:1/?key=secret']) {
    assert.throws(() => createHttpClient({ ...identity, baseUrl }), { code: 'INVALID_REQUEST' });
  }
  assert.throws(() => createHttpServer({ ...identity, resolve() {}, onClose: 1 }), { code: 'INVALID_REQUEST' });
});

test('session close callback runs once after terminal state and actual request drain', async t => {
  const gate = deferred(), entered = deferred(); let calls = 0, server;
  const pairResult = await pair(t, async () => { entered.resolve(); await gate.promise; return null; }, {
    onClose: () => { calls++; assert.equal(server.state.closed, true); assert.equal(server.state.active, 0); },
  });
  server = pairResult.server;
  const request = pairResult.call({}).catch(error => error.code);
  await entered.promise;
  const closing = server.close(); assert.equal(calls, 0);
  gate.resolve(); await closing; await request; await server.close();
  assert.equal(calls, 1);
});

for (const scenario of ['truncated', 'redirect', 'wrong-id', 'wrong-contract', 'malformed-json', 'duplicate-key']) {
  test(`client rejects ${scenario} HTTP response without retry`, async t => {
    let requests = 0;
    const identity = credentials();
    const server = http.createServer((request, response) => {
      requests++;
      response.writeHead(scenario === 'redirect' ? 302 : 200, {
        'content-type': 'application/json', 'x-meow-request-id': scenario === 'wrong-id' ? identity.sessionId : request.headers['x-meow-request-id'],
        ...(scenario === 'truncated' ? { 'content-length': 200 } : {}),
      });
      if (scenario === 'truncated') { response.write('{'); setImmediate(() => response.destroy()); }
      else response.end(scenario === 'malformed-json' ? '{' : scenario === 'duplicate-key'
        ? '{"ok":true,"contract":"runtime.http.v1","value":{"a":1,"a":2}}'
        : JSON.stringify({ ok: true, contract: scenario === 'wrong-contract' ? 'runtime.http.v0' : 'runtime.http.v1', value: null }));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const client = createHttpClient({ ...identity, baseUrl: `http://127.0.0.1:${server.address().port}` });
    t.after(async () => { await client.close(); await new Promise(resolve => server.close(resolve)); });
    await assert.rejects(client.request({ path: '/v1/echo', body: {} }), error => ['CLOSED', 'INVALID_RESPONSE'].includes(error.code));
    assert.equal(requests, 1);
  });
}
