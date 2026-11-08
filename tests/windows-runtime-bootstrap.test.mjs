import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter, once } from 'node:events';
import { Duplex } from 'node:stream';
import { randomBytes, randomUUID } from 'node:crypto';
import { join } from 'node:path';
import {
  connectWindowsNamedPipe, parseWindowsLaunchArguments, startWindowsAppRuntime,
  startWindowsRuntimeOnPipe, windowsScriptDataRoot,
} from '../apps/windows/runtime/bootstrap.mjs';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';
import { createRuntimeClient } from '../apps/runtime/client.mjs';
import { temporaryRoot } from '../scripts/windows/working-paths.mjs';

const windows = { skip: process.platform !== 'win32' ? 'Requires Windows pipe connector' : false };
const pipeArgs = (name = `meowfilm-${randomUUID()}`, session = randomUUID()) =>
  ['--meow-pipe', name, '--meow-session', session];
const identity = () => ({ sessionId: randomUUID(), requestKey: randomBytes(32).toString('hex'),
  dataRoot: join(temporaryRoot, 'script-数据👨‍👩‍👦') });
function bounded(promise) {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error('Test deadline exceeded')), 5000);
  })]).finally(() => clearTimeout(timer));
}
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

class Pipe extends Duplex {
  output = [];
  _read() {}
  _write(bytes, _encoding, callback) { this.output.push(Buffer.from(bytes)); callback(); }
}

test('launch accepts only the ordered product pipe name and session UUID', () => {
  const argv = pipeArgs(), [, name, , session] = argv;
  assert.deepEqual(parseWindowsLaunchArguments(argv), { pipeName: name, session });
  for (const value of [null, {}, 'argv', [], argv.slice(0, 3), [...argv, '--key', 'private'],
    ['--meow-session', session, '--meow-pipe', name],
    ...['', name.toUpperCase(), `${name}\n`, `${name}\0`, ` ${name}`, `${name} `,
      `\\\\.\\pipe\\${name}`, `\\\\server\\pipe\\${name}`, '../x', 'tcp://127.0.0.1:1', null, 1]
      .map(pipe => pipeArgs(pipe, session)),
    ...['', session.toUpperCase(), `${session}\r\n`, `{${session}}`, null, 1].map(id => pipeArgs(name, id)),
  ]) assert.throws(() => parseWindowsLaunchArguments(value), { code: 'INVALID_REQUEST' });
});

test('private data root requires an explicit absolute local path', () => {
  const root = identity().dataRoot;
  assert.equal(windowsScriptDataRoot(root), root);
  for (const value of [undefined, null, '', 1, 'relative/data', '\\\\server\\share', '//server/share', `${root}\0`])
    assert.throws(() => windowsScriptDataRoot(value), { code: 'INVALID_REQUEST' });
});

test('invalid connection options cannot create a socket', async () => {
  const socketFactory = () => assert.fail('must not create a socket');
  for (const pipe of ['tcp://127.0.0.1:1', '\\\\.\\pipe\\arbitrary', '../x', null])
    await assert.rejects(connectWindowsNamedPipe(pipe, { socketFactory }), { code: 'INVALID_REQUEST' });
  for (const timeout of [0, -1, NaN, Infinity, 1.5, '5000'])
    await assert.rejects(connectWindowsNamedPipe(pipeArgs()[1], { timeout, socketFactory }), { code: 'INVALID_REQUEST' });
  if (process.platform !== 'win32')
    await assert.rejects(connectWindowsNamedPipe(pipeArgs()[1], { socketFactory }), { code: 'INVALID_REQUEST' });
});

class ControlledSocket extends EventEmitter {
  destroyed = false;
  destructions = 0;
  connect(options) { this.options = options; return this; }
  destroy() {
    if (this.destroyed) return this;
    this.destroyed = true;
    this.destructions++;
    this.emit('close');
    return this;
  }
}

test('pipe connect timeout and cancellation reclaim the socket and listeners', windows, async () => {
  const aborted = new AbortController();
  aborted.abort();
  await assert.rejects(connectWindowsNamedPipe(pipeArgs()[1], {
    signal: aborted.signal, socketFactory: () => assert.fail('cancelled connect must not allocate'),
  }), { code: 'CANCELLED' });
  for (const cancelled of [false, true]) {
    const socket = new ControlledSocket(), controller = new AbortController(), name = pipeArgs()[1];
    const pending = connectWindowsNamedPipe(name, { timeout: 30, signal: controller.signal, socketFactory: () => socket });
    if (cancelled) controller.abort();
    await assert.rejects(pending, { code: cancelled ? 'CANCELLED' : 'TIMEOUT' });
    assert.deepEqual(socket.options, { path: `\\\\.\\pipe\\${name}` });
    assert.equal(socket.destructions, 1);
    assert.deepEqual(socket.eventNames(), []);
  }
});

test('pipe connection errors, early close and handoff errors stay sanitized and release resources', windows, async () => {
  for (const kind of ['error', 'throw', 'close', 'connected-error']) {
    const socket = new ControlledSocket();
    socket.connect = () => {
      if (kind === 'throw') throw new Error('private');
      queueMicrotask(() => {
        if (kind === 'error') socket.emit('error', new Error('private'));
        else if (kind === 'close') socket.destroy();
        else { socket.emit('connect'); socket.emit('error', new Error('private')); }
      });
      return socket;
    };
    const result = connectWindowsNamedPipe(pipeArgs()[1], { socketFactory: () => socket });
    if (kind === 'connected-error') assert.equal(await result, socket);
    else await assert.rejects(result, { code: 'CLOSED', message: 'CLOSED' });
    assert.equal(socket.destructions, 1);
    assert.deepEqual(socket.eventNames(), []);
  }
});

test('missing pipe and invalid launch never create a runtime', windows, async () => {
  const createRuntime = () => assert.fail('failed launch cannot create a runtime');
  await assert.rejects(startWindowsAppRuntime({ createRuntime, argv: [] }), { code: 'INVALID_REQUEST' });
  await assert.rejects(startWindowsAppRuntime({ createRuntime, argv: pipeArgs(), connectTimeout: 200 }), { code: 'CLOSED' });
});

function fixture(t, { makeRuntime, timeout = 5000, signal } = {}) {
  const pipe = new Pipe(), input = identity();
  const seen = [];
  let closes = 0;
  const invocation = deferred(), release = deferred();
  const createRuntime = config => {
    seen.push(config);
    return makeRuntime ? makeRuntime(config) : createAppRuntime({ ...config, modules: [{
      id: 'music', version: '0.8.0', contract: 'music.script.v3',
      methods: {
        'music.script.invoke': async (value, { signal: requestSignal }) => {
          if (value.wait) {
            invocation.resolve(requestSignal);
            await release.promise;
          }
          return { ok: true, contract: 'music.script.v3', value };
        },
      },
      upload: async (_input, bytes) => ({ ok: true, contract: 'music.script.v3', value: { bytes: bytes.length } }),
      async close() { closes++; release.resolve(); },
    }] });
  };
  const started = startWindowsRuntimeOnPipe(pipe, { sessionId: input.sessionId, createRuntime, timeout, signal });
  started.catch(() => {});
  t.after(async () => {
    release.resolve();
    pipe.destroy();
    const runtime = await started.catch(() => null);
    await runtime?.close();
  });
  return { pipe, input, seen, started, invocation, release, closes: () => closes,
    send(value = input) { pipe.push(Buffer.from(JSON.stringify(value) + '\n')); } };
}

test('fragmented private config returns one ready line and complete business JSON uses HTTP', async t => {
  const f = fixture(t);
  const bytes = Buffer.from(JSON.stringify(f.input) + '\n');
  for (const byte of bytes) f.pipe.push(Buffer.from([byte]));
  const runtime = await f.started;
  assert.deepEqual(f.seen, [f.input]);
  const readyBytes = Buffer.concat(f.pipe.output).toString('utf8');
  assert.equal(readyBytes.split('\n').length, 2);
  assert.deepEqual(JSON.parse(readyBytes), { protocol: 'runtime.http.v1', baseUrl: runtime.baseUrl });
  const client = createRuntimeClient({ ...f.input, baseUrl: runtime.baseUrl });
  t.after(() => client.close());
  const value = { text: '业务👨‍👩‍👦'.repeat(100000) };
  assert.deepEqual(await client.call('music.script.invoke', value), { ok: true, contract: 'music.script.v3', value });
  assert.equal(f.pipe.output.length, 1);
  await client.close(); // Closing an HTTP keepalive is unrelated to the private pipe lifetime.
  assert.equal(runtime.state.closed, false);
  const next = createRuntimeClient({ ...f.input, baseUrl: runtime.baseUrl });
  t.after(() => next.close());
  assert.equal((await next.describe()).ok, true);
  await runtime.close();
  assert.equal(f.closes(), 1);
});

for (const mutation of [
  value => ({ ...value, sessionId: randomUUID() }),
  value => ({ ...value, requestKey: 'private' }),
  value => ({ ...value, dataRoot: 'relative' }),
  value => ({ ...value, extra: true }),
  value => ({ sessionId: value.sessionId, requestKey: value.requestKey }),
  () => null, () => [],
]) {
  test('invalid private config is rejected before runtime creation', async t => {
    const f = fixture(t);
    f.send(mutation(f.input));
    await assert.rejects(f.started, { code: 'INVALID_REQUEST' });
    assert.equal(f.pipe.destroyed, true);
    assert.deepEqual(f.seen, []);
    assert.deepEqual(f.pipe.output, []);
  });
}

for (const bytes of [Buffer.from([0xff, 10]), Buffer.from('{"sessionId":"a","sessionId":"b"}\n'),
  Buffer.from('{}\nextra'), Buffer.alloc(64 * 1024 + 1, 32)]) {
  test('malformed UTF8/JSON, extra bytes and oversized bootstrap messages close without ready', async t => {
    const f = fixture(t);
    f.pipe.push(bytes);
    await assert.rejects(f.started, { code: 'INVALID_REQUEST' });
    assert.deepEqual(f.seen, []);
    assert.deepEqual(f.pipe.output, []);
  });
}

test('64 KiB bootstrap bound includes the newline and permits an exact boundary line', async t => {
  const f = fixture(t), json = JSON.stringify(f.input);
  f.pipe.push(Buffer.from(json + ' '.repeat(64 * 1024 - Buffer.byteLength(json) - 1) + '\n'));
  await f.started;
  assert.equal(f.seen.length, 1);
});

for (const reason of ['timeout', 'cancel', 'end', 'error', 'close']) {
  test(`bootstrap ${reason} releases the pipe before creating modules`, async t => {
    const controller = new AbortController();
    const f = fixture(t, { timeout: 30, signal: controller.signal });
    if (reason === 'cancel') controller.abort();
    if (reason === 'end') f.pipe.push(null);
    if (reason === 'error') f.pipe.emit('error', new Error('private diagnostic'));
    if (reason === 'close') f.pipe.destroy();
    await assert.rejects(f.started, { code: reason === 'timeout' ? 'TIMEOUT' : reason === 'cancel' ? 'CANCELLED' : 'CLOSED' });
    assert.equal(f.pipe.destroyed, true);
    assert.deepEqual(f.seen, []);
  });
}

for (const reason of ['end', 'error', 'close', 'extra-byte', 'cancel']) {
  test(`ready pipe ${reason} drains the actual HTTP runtime and cancels active work`, async t => {
    const controller = new AbortController(), f = fixture(t, { signal: controller.signal });
    f.send();
    const runtime = await f.started, closed = once(f.pipe, 'close');
    const client = createRuntimeClient({ ...f.input, baseUrl: runtime.baseUrl });
    t.after(() => client.close());
    const pending = assert.rejects(client.call('music.script.invoke', { wait: true }), { code: 'CLOSED' });
    const signal = await f.invocation.promise;
    if (reason === 'end') f.pipe.push(null);
    if (reason === 'error') f.pipe.emit('error', new Error('private'));
    if (reason === 'close') f.pipe.destroy();
    if (reason === 'extra-byte') f.pipe.push(Buffer.from(' '));
    if (reason === 'cancel') controller.abort();
    await bounded(closed.catch(error => { if (reason !== 'error') throw error; }));
    await runtime.close();
    await pending;
    assert.equal(signal.aborted, true);
    assert.equal(runtime.state.closed, true);
    assert.equal(f.closes(), 1);
    await runtime.close();
    assert.equal(f.closes(), 1);
  });
}

test('disconnect during HTTP startup waits for the real runtime before closing and never sends ready', async t => {
  const listening = deferred(), entered = deferred();
  let closed = false;
  t.after(() => listening.resolve());
  const f = fixture(t, { makeRuntime: () => ({
    baseUrl: 'http://127.0.0.1:1234',
    start() { entered.resolve(); return listening.promise; },
    close() { closed = true; },
  }) });
  f.send();
  await entered.promise;
  f.pipe.destroy();
  let settled = false;
  f.started.catch(() => { settled = true; });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(settled, false);
  listening.resolve();
  await assert.rejects(f.started, { code: 'CLOSED' });
  assert.equal(closed, true);
  assert.deepEqual(f.pipe.output, []);
});

for (const reason of ['timeout', 'cancel']) {
  test(`HTTP startup ${reason} waits for cleanup and never sends ready`, async t => {
    const controller = new AbortController(), listening = deferred(), entered = deferred();
    let closes = 0;
    t.after(() => listening.resolve());
    const f = fixture(t, { timeout: 30, signal: controller.signal, makeRuntime: () => ({
      baseUrl: 'http://127.0.0.1:1234',
      start() { entered.resolve(); return listening.promise; },
      async close() { closes++; },
    }) });
    f.send();
    await entered.promise;
    if (reason === 'cancel') controller.abort();
    else await new Promise(resolve => setTimeout(resolve, 40));
    let settled = false;
    f.started.catch(() => { settled = true; });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(settled, false);
    assert.deepEqual(f.pipe.output, []);
    listening.resolve();
    await assert.rejects(f.started, { code: reason === 'timeout' ? 'TIMEOUT' : 'CANCELLED' });
    assert.equal(closes, 1);
    assert.equal(f.pipe.destroyed, true);
    assert.deepEqual(f.pipe.output, []);
  });
}

test('cancellation during the ready write closes the HTTP runtime without waiting for a pipe callback', async t => {
  const controller = new AbortController(), writing = deferred();
  const f = fixture(t, { signal: controller.signal });
  f.pipe._write = (_bytes, _encoding, _callback) => writing.resolve();
  f.send();
  await writing.promise;
  controller.abort();
  await assert.rejects(bounded(f.started), { code: 'CANCELLED' });
  assert.equal(f.closes(), 1);
  assert.equal(f.pipe.destroyed, true);
});

test('runtime close waits for actual cleanup and repeated closes share that result', async t => {
  const cleaning = deferred(), releaseCleanup = deferred();
  let closes = 0;
  t.after(() => releaseCleanup.resolve());
  const f = fixture(t, { makeRuntime: () => ({
    baseUrl: 'http://127.0.0.1:1234',
    async start() {},
    async close() { closes++; cleaning.resolve(); await releaseCleanup.promise; },
  }) });
  f.send();
  const runtime = await f.started;
  const closing = runtime.close();
  await cleaning.promise;
  assert.equal(runtime.close(), closing);
  assert.equal(f.pipe.destroyed, false);
  releaseCleanup.resolve();
  await bounded(closing);
  assert.equal(f.pipe.destroyed, true);
  assert.equal(closes, 1);
});

test('startup and explicit cleanup failures remain sanitized after reclaiming the pipe', async t => {
  for (const failStartup of [true, false]) {
    const pipe = new Pipe(), input = identity();
    t.after(() => pipe.destroy());
    const started = startWindowsRuntimeOnPipe(pipe, { sessionId: input.sessionId, createRuntime: () => ({
      baseUrl: 'http://127.0.0.1:1234',
      async start() { if (failStartup) throw new Error('private startup detail'); },
      async close() { throw new Error('private cleanup detail'); },
    }) });
    pipe.push(Buffer.from(JSON.stringify(input) + '\n'));
    if (failStartup) await assert.rejects(started, { code: 'INVALID_RESPONSE', message: 'INVALID_RESPONSE' });
    else {
      const runtime = await started;
      await assert.rejects(runtime.close(), { code: 'INVALID_RESPONSE', message: 'INVALID_RESPONSE' });
    }
    assert.equal(pipe.destroyed, true);
  }
});

test('assembly failure and invalid ready endpoint fail closed without exposing details', async t => {
  for (const failingFactory of [true, false]) {
    let closed = false;
    const f = fixture(t, { makeRuntime: () => {
      if (failingFactory) throw new Error('private assembly detail');
      return { baseUrl: 'https://outside.invalid', async start() {}, async close() { closed = true; } };
    } });
    f.send();
    await assert.rejects(f.started, { code: failingFactory ? 'INVALID_RESPONSE' : 'INVALID_REQUEST' });
    assert.equal(f.pipe.destroyed, true);
    assert.equal(closed, !failingFactory);
    assert.deepEqual(f.pipe.output, []);
  }
});

test('HTTP start failure and a failed ready write close the runtime and sanitize errors', async t => {
  let closes = 0;
  const failedStart = fixture(t, { makeRuntime: () => ({
    async start() { throw new Error('private listen detail'); },
    async close() { closes++; },
  }) });
  failedStart.send();
  await assert.rejects(failedStart.started, { code: 'INVALID_RESPONSE', message: 'INVALID_RESPONSE' });
  assert.equal(closes, 1);
  assert.deepEqual(failedStart.pipe.output, []);

  const failedWrite = fixture(t);
  failedWrite.pipe._write = (_bytes, _encoding, callback) => callback(new Error('private pipe detail'));
  failedWrite.send();
  await assert.rejects(failedWrite.started, { code: 'CLOSED', message: 'CLOSED' });
  assert.equal(failedWrite.closes(), 1);
  assert.equal(failedWrite.pipe.destroyed, true);
});
