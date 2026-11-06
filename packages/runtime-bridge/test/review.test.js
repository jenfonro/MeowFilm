import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeJSONValue } from '../src/index.js';
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
