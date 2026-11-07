import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { BridgeError, HTTP_PROTOCOL, encodeJSONValue } from '../src/index.js';
import { pair, rawRequest, deferred, until } from './http-fixture.js';

for (const direction of ['forward', 'reverse']) {
  test(`${direction}: slow request body cancellation fences dispatch while independent status responds`, async t => {
    let called = 0;
    const { server, identity, call } = await pair(t, input => { called++; return input; }, { direction });
    const slow = rawRequest(server.baseUrl, identity, { body: '{"unfinished":', end: false });
    await until(() => server.state.active === 1);
    assert.equal((await call('status')).value, 'status');
    slow.request.destroy(); await slow.response.catch(() => {});
    await until(() => server.state.active === 0);
    assert.equal(called, 1);
  });
  test(`${direction}: closing a response after complete request body aborts and retains physical ownership`, async t => {
    const entered = deferred(), release = deferred(); let signal;
    const { server, identity } = await pair(t, async (_input, context) => {
      signal = context.signal; entered.resolve(); await release.promise; return 'late';
    }, { direction });
    const raw = rawRequest(server.baseUrl, identity, { body: '{}' });
    await entered.promise; raw.request.destroy(); await raw.response.catch(() => {});
    await until(() => signal.aborted);
    assert.equal(server.state.active, 1);
    release.resolve(); await until(() => server.state.active === 0);
  });
  test(`${direction}: response backpressure owns cancellation and independent errors still return`, async t => {
    let signal;
    const { server, identity } = await pair(t, (_input, context) => {
      signal = context.signal; return 'x'.repeat(32 * 1024 * 1024);
    }, { direction });
    const raw = rawRequest(server.baseUrl, identity, { body: '{}', pauseResponse: true });
    await until(() => signal !== undefined);
    const missing = await rawRequest(server.baseUrl, identity, { path: '/v1/missing', body: '{}' }).response;
    assert.equal(missing.value.code, 'METHOD_NOT_FOUND');
    raw.request.destroy(); await raw.response.catch(() => {});
    await until(() => signal.aborted);
    await until(() => server.state.active === 0);
  });
}

test('encoder rejects unpaired UTF-16 values and keys while preserving legal Unicode', () => {
  for (const value of ['\ud800', '\udfff', { '\ud800': 1 }, { '\udfff': 1 }]) {
    assert.throws(() => encodeJSONValue(value), { code: 'INVALID_REQUEST' });
  }
  assert.equal(encodeJSONValue({ 'é': 1, 'e\u0301': 2 }).toString(), '{"é":1,"é":2}');
});

test('arbitrary handler rejection values return redacted failures and release their HTTP owners', async t => {
  const failures = [new Error('PRIVATE-provider-secret'), null, undefined, 'PRIVATE-provider-secret',
    0, false, 1n, Symbol('PRIVATE-provider-secret')];
  const { call, client, server } = await pair(t, input => {
    if (typeof input === 'number') throw failures[input];
    if (input === 'known') throw new BridgeError('TIMEOUT');
    return input;
  });
  for (let i = 0; i < failures.length; i++) {
    assert.deepEqual(await call(i, { timeout: 1000 }),
      { ok: false, contract: HTTP_PROTOCOL, code: 'INVALID_RESPONSE' });
    await until(() => server.state.active === 0);
    assert.equal(client.state.pending, 0);
  }
  assert.deepEqual(await call('known'), { ok: false, contract: HTTP_PROTOCOL, code: 'TIMEOUT' });
  assert.equal((await call('alive')).value, 'alive');
  await Promise.all([client.close(), server.close()]);
  assert.equal(server.state.active, 0);
  assert.equal(client.state.pending, 0);
});

test('failed error response writes close the exchange instead of waiting for an unsent response', async t => {
  const original = http.ServerResponse.prototype.end;
  let failWrite = true;
  t.mock.method(http.ServerResponse.prototype, 'end', function (...args) {
    if (failWrite) {
      failWrite = false;
      throw new Error('controlled response write failure');
    }
    return Reflect.apply(original, this, args);
  });
  const { call, client, server } = await pair(t, input => {
    if (input === 'fail') throw null;
    return input;
  });
  await assert.rejects(call('fail', { timeout: 1000 }), { code: 'CLOSED' });
  await until(() => server.state.active === 0);
  assert.equal(client.state.pending, 0);
  assert.equal((await call('alive')).value, 'alive');
  await Promise.all([client.close(), server.close()]);
  assert.equal(server.state.active, 0);
});
