import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { createHttpClient, createHttpServer, HTTP_PROTOCOL } from '../src/index.js';
import { pair, rawRequest, lifecycle, deferred, until, turn } from './http-fixture.js';

const okay = reply => { assert.equal(reply.ok, true); return reply.value; };

test('HTTP accepts every-byte body writes, Unicode and legal large JSON without a frame quota', async t => {
  const { server, identity, call } = await pair(t, value => value);
  const input = { é: 1, 'e\u0301': 2, text: '猫😀' };
  const raw = rawRequest(server.baseUrl, identity, { end: false });
  for (const byte of Buffer.from(JSON.stringify(input))) raw.request.write(Buffer.from([byte]));
  raw.request.end();
  const result = await raw.response;
  assert.equal(result.status, 200);
  assert.equal(result.headers['x-meow-request-id'], raw.requestId);
  assert.deepEqual(result.value.value, input);
  for (const size of [512 * 1024, 1024 * 1024 + 1, 8 * 1024 * 1024 + 1]) {
    const text = 'x'.repeat(size);
    assert.equal(okay(await call(text)), text);
  }
});

test('HTTP rejects invalid UTF-8, duplicate keys, unsafe integers, depth and truncated bodies before dispatch', async t => {
  let calls = 0;
  const { server, identity } = await pair(t, value => { calls++; return value; });
  for (const body of [Buffer.from([0xff]), Buffer.from([0xed, 0xa0, 0x80]), Buffer.from('{'),
    Buffer.from('{"x":1,"\\u0078":2}'), Buffer.from('9007199254740992'),
    Buffer.from('"\\ud800"'), Buffer.from('['.repeat(65) + 'null' + ']'.repeat(65))]) {
    const result = await rawRequest(server.baseUrl, identity, { body }).response;
    assert.equal(result.status, 400);
    assert.deepEqual(result.value, { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_REQUEST' });
  }
  const raw = rawRequest(server.baseUrl, identity, { body: '{"unfinished":', end: false });
  await until(() => server.state.active === 1);
  raw.request.destroy();
  await raw.response.catch(() => {});
  await until(() => server.state.active === 0);
  assert.equal(calls, 0);
});

test('registered methods round trip all JSON types and isolate malformed results', async t => {
  const { call } = await pair(t, value => value === 'invalid' ? undefined : value);
  for (const input of [null, false, 3, 'text', [1], { value: 'raw' }]) assert.deepEqual(okay(await call(input)), input);
  assert.deepEqual(await call('invalid'), { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_RESPONSE' });
  assert.equal(okay(await call('alive')), 'alive');
  let nested = null;
  for (let i = 0; i < 64; i++) nested = [nested];
  assert.deepEqual(okay(await call(nested)), nested);
});

test('nested reverse HTTP callback runs while the forward request is waiting', async t => {
  const identity = lifecycle();
  const callbacks = createHttpServer({ ...identity, resolve: ({ method, url }) => {
    if (method !== 'POST' || url.pathname !== '/v1/capabilities/test.echo' || url.search) return;
    return { contentType: 'application/json', contract: HTTP_PROTOCOL,
      handler: async input => ({ ok: true, contract: HTTP_PROTOCOL, value: input.key }) };
  } });
  await callbacks.start();
  const reverse = createHttpClient({ baseUrl: callbacks.baseUrl });
  t.after(async () => { await Promise.all([reverse.close(), callbacks.close()]); });
  const { call } = await pair(t, async input => okay(await reverse.request({
    path: '/v1/capabilities/test.echo', body: input,
  })));
  assert.equal(okay(await call({ key: '中文' })), '中文');
});

test('arguments are captured, errors redacted and arbitrary routes are unavailable', async t => {
  const { call, server, identity } = await pair(t, input => {
    if (input === 'fail') throw new Error('PRIVATE-provider-secret');
    return input;
  });
  const input = { original: 1 }, pending = call(input); input.original = 2;
  assert.deepEqual(okay(await pending), { original: 1 });
  assert.deepEqual(await call('fail'), { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_RESPONSE' });
  const missing = await rawRequest(server.baseUrl, identity, { path: '/v1/eval', body: '{}' }).response;
  assert.equal(missing.status, 404); assert.equal(missing.value.code, 'METHOD_NOT_FOUND');
});

test('cancelled noncooperative handlers remain owned while fresh independent calls proceed', async t => {
  const entered = deferred(), released = deferred();
  const signals = [];
  t.after(() => released.resolve());
  const { call, server } = await pair(t, async (input, context) => {
    if (input === 'slow') {
      signals.push(context.signal);
      if (signals.length === 96) entered.resolve();
      await released.promise;
    }
    return input;
  });
  const controller = new AbortController();
  const rejected = Array.from({ length: 96 }, () =>
    assert.rejects(call('slow', { signal: controller.signal }), { code: 'CANCELLED' }));
  await entered.promise;
  assert.equal(server.state.active, 96);
  controller.abort(); await Promise.all(rejected);
  await until(() => signals.every(signal => signal.aborted));
  assert.equal(signals.length, 96);
  assert.equal(server.state.active, 96);
  const values = await Promise.all(Array.from({ length: 130 }, (_, i) => call(i)));
  assert.deepEqual(values.map(okay), Array.from({ length: 130 }, (_, i) => i));
  assert.equal(server.state.active, 96);
  released.resolve(); await until(() => server.state.active === 0);
});

test('timeout aborts HTTP and close waits for actual handler completion without late publication', async t => {
  const entered = deferred(), released = deferred();
  const { call, server } = await pair(t, async () => { entered.resolve(); await released.promise; return 'late'; });
  const timed = assert.rejects(call({}, { timeout: 100 }), { code: 'TIMEOUT' });
  await entered.promise; await timed;
  let closed = false;
  const closing = server.close().then(() => { closed = true; });
  await turn(); assert.equal(closed, false);
  released.resolve(); await closing;
  assert.equal(server.state.active, 0);
});

test('duplicate active request IDs reject before dispatch, completed IDs leave no history', async t => {
  const entered = deferred(), release = deferred(); let calls = 0;
  const { call, server } = await pair(t, async input => {
    calls++; if (input === 'slow') { entered.resolve(); await release.promise; } return input;
  });
  const requestId = randomUUID();
  const pending = call('slow', { requestId }); await entered.promise;
  const duplicate = await call('other', { requestId });
  assert.equal(duplicate.code, 'INVALID_REQUEST'); assert.equal(calls, 1);
  release.resolve(); await pending;
  await until(() => server.state.active === 0);
  assert.equal(okay(await call('again', { requestId })), 'again');
});

test('pre-aborted HTTP never dispatches and normal response finish never cancels its owner', async t => {
  let calls = 0, signal, isCurrent;
  const { call } = await pair(t, (_input, context) => {
    calls++; signal = context.signal; isCurrent = context.isCurrent;
    assert.equal(isCurrent(), true); return null;
  });
  const cancelled = new AbortController(); cancelled.abort();
  await assert.rejects(call({}, { signal: cancelled.signal }), { code: 'CANCELLED' });
  assert.equal(calls, 0);
  assert.equal(okay(await call({})), null);
  await turn(); assert.equal(signal.aborted, false);
  assert.equal(isCurrent(), false);
});
