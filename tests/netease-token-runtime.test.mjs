import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { createNeteaseTokenModule } from '../apps/runtime/modules/netease-token.mjs';
import { createNeteaseTokenProvider } from '../packages/netease-token/src/index.js';
import { EventEmitter } from 'node:events';

test('token module has its own exact HTTP contract and sends no account data', async t => {
  let gets = 0, closes = 0;
  const module = createNeteaseTokenModule({ provider: {
    async get({ signal }) { assert.equal(signal.aborted, false); return `synthetic-${++gets}`; },
    async close() { closes++; },
  } });
  const sessionId = randomUUID(), requestKey = randomBytes(32).toString('hex');
  const runtime = createAppRuntime({ sessionId, requestKey, modules: [module] });
  await runtime.start();
  const client = createRuntimeClient({ sessionId, requestKey, baseUrl: runtime.baseUrl });
  t.after(async () => { await client.close(); await runtime.close(); });
  const description = (await client.describe()).value;
  assert.deepEqual(description.modules, [{ id: 'netease', version: '0.1.0',
    contract: 'netease.token.v1', methods: ['netease.token.get'] }]);
  assert.equal(gets, 0);
  for (const input of [null, [], { cookie: 'synthetic-cookie' }, { url: 'https://example.invalid/' }]) {
    assert.deepEqual(await client.call('netease.token.get', input),
      { ok: false, contract: 'netease.token.v1', code: 'INVALID_REQUEST' });
  }
  for (let next = 1; next <= 2; next++) {
    assert.deepEqual(await client.call('netease.token.get'), {
      ok: true, contract: 'netease.token.v1', value: { token: `synthetic-${next}` },
    });
  }
  await client.shutdown();
  assert.equal(closes, 1);
});

test('SDK errors expose only known codes through the module', async () => {
  const module = createNeteaseTokenModule({ provider: {
    async get() { throw new Error('private account/token-like detail'); }, async close() {},
  } });
  assert.deepEqual(await module.methods['netease.token.get']({}), {
    ok: false, contract: 'netease.token.v1', code: 'SDK_FAILURE',
  });
  await module.close();
});

test('HTTP cancellation reaches the SDK owner and runtime close drains a token worker', async t => {
  let started, terminated = 0;
  const entered = () => new Promise(resolve => { started = resolve; });
  class TokenWorker extends EventEmitter {
    postMessage() { started(); }
    async terminate() { terminated++; return 0; }
  }
  const provider = createNeteaseTokenProvider({ workerFactory: () => new TokenWorker() });
  const sessionId = randomUUID(), requestKey = randomBytes(32).toString('hex');
  const runtime = createAppRuntime({ sessionId, requestKey, modules: [createNeteaseTokenModule({ provider })] });
  await runtime.start();
  const client = createRuntimeClient({ sessionId, requestKey, baseUrl: runtime.baseUrl });
  t.after(async () => { await client.close(); await runtime.close(); });
  const abort = new AbortController(), firstEntered = entered();
  const first = client.call('netease.token.get', {}, { signal: abort.signal });
  const firstRejected = assert.rejects(first, { code: 'CANCELLED' });
  await firstEntered; abort.abort(); await firstRejected;
  // A second exchange proves the first worker cleanup completed and the queue advanced.
  const secondEntered = entered(), second = client.call('netease.token.get');
  const secondRejected = assert.rejects(second, error => ['CANCELLED', 'CLOSED'].includes(error.code));
  await secondEntered;
  assert.equal(terminated, 1);
  await client.shutdown(); await secondRejected;
  assert.equal(terminated, 2);
});
