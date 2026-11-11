import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { Worker } from 'node:worker_threads';
import { createNeteaseTokenProvider, validToken } from '../src/index.js';

const tick = () => new Promise(resolve => setImmediate(resolve));
class FakeWorker extends EventEmitter {
  requests = [];
  terminated = 0;
  postMessage(message) { this.requests.push(message); }
  terminate() { this.terminated++; return Promise.resolve(0); }
  reply(token, code) { this.emit('message', { id: this.requests.at(-1).id, token, code }); }
}
function fixture(t, options) {
  const workers = [];
  const provider = createNeteaseTokenProvider({
    workerFactory: () => { const worker = new FakeWorker(); workers.push(worker); return worker; },
    ...options,
  });
  t.after(() => provider.close());
  return { provider, workers };
}
test('construction and shutdown do not start the SDK; every request obtains a new token', async t => {
  const { provider, workers } = fixture(t);
  assert.equal(workers.length, 0);
  const first = provider.get();
  assert.equal(workers.length, 1);
  workers[0].reply('synthetic-one');
  assert.equal(await first, 'synthetic-one');
  const second = provider.get();
  assert.equal(workers[0].requests.length, 2);
  workers[0].reply('synthetic-two');
  assert.equal(await second, 'synthetic-two');
  await provider.close(); await provider.close();
  assert.equal(workers[0].terminated, 1);
  await assert.rejects(provider.get(), { code: 'CLOSED' });
});
test('concurrent callers are serialized, queued cancellation does not cancel another token', async t => {
  const { provider, workers } = fixture(t);
  const first = provider.get(), cancelled = new AbortController();
  const second = provider.get({ signal: cancelled.signal }), secondRejected = assert.rejects(second, { code: 'CANCELLED' });
  const third = provider.get();
  cancelled.abort(); await secondRejected;
  assert.equal(workers[0].requests.length, 1);
  assert.equal(workers[0].terminated, 0);
  workers[0].reply('synthetic-one'); assert.equal(await first, 'synthetic-one');
  assert.equal(workers[0].requests.length, 2);
  workers[0].reply('synthetic-three'); assert.equal(await third, 'synthetic-three');
});
test('active cancellation waits for worker termination before starting queued work', async t => {
  const { provider, workers } = fixture(t);
  const abort = new AbortController(), first = provider.get({ signal: abort.signal });
  const rejected = assert.rejects(first, { code: 'CANCELLED' });
  let release;
  workers[0].terminate = () => new Promise(resolve => { release = resolve; });
  const second = provider.get();
  abort.abort(); await rejected;
  assert.equal(workers.length, 1);
  workers[0].reply('late-token-must-not-escape');
  release(0); await tick();
  assert.equal(workers.length, 2);
  workers[1].reply('synthetic-two');
  assert.equal(await second, 'synthetic-two');
});
test('already cancelled callers do not start a worker', async t => {
  const { provider, workers } = fixture(t), abort = new AbortController();
  abort.abort();
  await assert.rejects(provider.get({ signal: abort.signal }), { code: 'CANCELLED' });
  assert.equal(workers.length, 0);
});
test('timeouts terminate the current worker and next request reinitializes', async t => {
  const { provider, workers } = fixture(t, { timeout: 15 });
  await assert.rejects(provider.get(), { code: 'TIMEOUT' });
  await tick();
  assert.equal(workers[0].terminated, 1);
  const next = provider.get();
  workers[1].reply('synthetic-recovered');
  assert.equal(await next, 'synthetic-recovered');
});
test('invalid callback, empty token and SDK failures are sanitized and reset the worker', async t => {
  const { provider, workers } = fixture(t);
  for (const [token, code, expected] of [
    ['', undefined, 'UPSTREAM_FORMAT'], ['synthetic\n', undefined, 'UPSTREAM_FORMAT'],
    [undefined, 'NETWORK_ERROR', 'NETWORK_ERROR'], [undefined, 'private-sdk-error', 'UPSTREAM_FORMAT'],
  ]) {
    const call = provider.get(), rejected = assert.rejects(call, { code: expected, message: expected });
    workers.at(-1).reply(token, code); await rejected; await tick();
    assert.equal(workers.at(-1).terminated, 1);
  }
  const call = provider.get(), rejected = assert.rejects(call, { code: 'SDK_FAILURE' });
  workers.at(-1).emit('message', { id: -1, token: 'wrong-request' });
  await rejected;
});
test('worker crash and startup error settle callers without leaking diagnostics', async t => {
  const { provider, workers } = fixture(t);
  const call = provider.get(), rejected = assert.rejects(call, { code: 'SDK_FAILURE', message: 'SDK_FAILURE' });
  workers[0].emit('error', new Error('private sdk detail'));
  await rejected; await tick();
  const startup = createNeteaseTokenProvider({ workerFactory() { throw new Error('private path'); } });
  await assert.rejects(startup.get(), { code: 'SDK_FAILURE' });
  await startup.close();
});
test('close rejects active and queued requests and awaits resource release', async t => {
  const { provider, workers } = fixture(t);
  const first = assert.rejects(provider.get(), { code: 'CLOSED' });
  const second = assert.rejects(provider.get(), { code: 'CLOSED' });
  let release, closed = false;
  workers[0].terminate = () => new Promise(resolve => { release = resolve; });
  const closing = provider.close().then(() => { closed = true; });
  await first; await second; await tick();
  assert.equal(closed, false);
  release(0); await closing;
});
test('failed termination fails closed and is reported by shutdown', async () => {
  const worker = new FakeWorker();
  worker.terminate = async () => { throw new Error('private cleanup detail'); };
  const provider = createNeteaseTokenProvider({ workerFactory: () => worker });
  const call = provider.get(), rejected = assert.rejects(call, { code: 'UPSTREAM_FORMAT' });
  worker.reply(''); await rejected; await tick();
  await assert.rejects(provider.get(), { code: 'SDK_CLEANUP' });
  await assert.rejects(provider.close(), { code: 'SDK_CLEANUP' });
});
test('token accepts cookie-octets only, including rejecting a trailing newline', () => {
  assert.equal(validToken('synthetic-_+/:=.[]'), true);
  for (const token of ['', 'a\n', 'a\r', 'a;b', 'a,b', 'a"b', 'a\\b', 'a b', '曲', null]) {
    assert.equal(validToken(token), false);
  }
});
test('a real worker blocked in JavaScript is terminated within the owner deadline', async () => {
  const provider = createNeteaseTokenProvider({
    workerFactory: () => new Worker('while(true) {}', { eval: true, stdout: true, stderr: true }),
    timeout: 50,
  });
  await assert.rejects(provider.get(), { code: 'TIMEOUT' });
  await provider.close();
});
