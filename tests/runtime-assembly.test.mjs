import test from 'node:test';
import assert from 'node:assert/strict';
import { Duplex } from 'node:stream';
import { attachConnectedStream } from '@meowfilm/runtime-bridge/descriptor';
import { createAppRuntime } from '../apps/runtime/bootstrap.mjs';

function connect(t, product, modules, options = {}) {
  let left, right;
  const side = other => new Duplex({
    read() {},
    write(bytes, _, done) { if (!other().destroyed) other().push(bytes); done(); },
    destroy(error, done) { if (!other().destroyed) other().push(null); done(error); }
  });
  left = side(() => right); right = side(() => left);
  const client = attachConnectedStream(left, { session: 'assembly', timeout: 2000, methods: options.nativeMethods });
  const runtime = createAppRuntime({
    product, modules, assemble: options.assemble,
    connect: options => attachConnectedStream(right, { ...options, session: 'assembly', timeout: 2000 })
  });
  t.after(async () => { client.close(); await runtime.close(); });
  return { client, runtime };
}

test('transport termination disposes each enabled module exactly once', async t => {
  let closes = 0;
  let resolveClosed;
  const closed = new Promise(resolve => { resolveClosed = resolve; });
  const module = { id: 'fixture', version: '0.1.0', methods: { 'fixture.echo': value => value },
    close() { closes++; resolveClosed(); } };
  const { client, runtime } = connect(t, 'fixture', [module]);
  assert.equal(await client.call('fixture.echo', 2), 2);
  client.close();
  await closed;
  await runtime.close();
  await runtime.close();
  assert.equal(closes, 1);
});

test('failed module cleanup is explicit without leaking provider diagnostics', async () => {
  let onClose;
  const runtime = createAppRuntime({
    product: 'fixture',
    modules: [{ id: 'fixture', version: '1.0.0', methods: {}, close() { throw new Error('private provider failure'); } }],
    connect: options => { onClose = options.onClose; return { close: () => onClose() }; }
  });
  await assert.rejects(runtime.close(), { code: 'INTERNAL', message: 'INTERNAL' });
});

test('invalid assembly cannot connect or silently override another module', () => {
  const good = { id: 'fixture', version: '0.1.0', methods: {}, close() {} };
  const bad = [[], [good, good], [{ ...good, id: undefined }], [{ ...good, version: '1.0.0\n' }],
    [{ ...good, methods: { 'other.method': () => null } }]];
  for (const modules of bad) {
    assert.throws(() => createAppRuntime({ product: 'fixture', modules, connect: () => assert.fail('connected') }),
      { code: 'INVALID_MESSAGE' });
  }
  assert.throws(() => createAppRuntime({ modules: [good], connect() {} }), { code: 'INVALID_MESSAGE' });
});

test('App factories inject the same framed native caller without starting disabled modules', async t => {
  let calls = 0, outbound, premature;
  const { client, runtime } = connect(t, 'fixture', undefined, {
    nativeMethods: { 'platform.fixture.echo': value => { calls++; return value; } },
    assemble({ call }) {
      outbound = call;
      premature = call('platform.fixture.echo', 'not connected');
      // Observe the immediate rejection before this synchronous factory returns.
      void premature.catch(() => {});
      return [{
        id: 'fixture', version: '1.0.0', close() {},
        methods: { 'fixture.roundtrip': (value, context) => call('platform.fixture.echo', value, { signal: context.signal }) }
      }];
    }
  });
  await assert.rejects(premature, { code: 'CLOSED' });
  assert.deepEqual(await client.call('fixture.roundtrip', { value: '保留原始身份' }), { value: '保留原始身份' });
  assert.equal(calls, 1);
  await runtime.close();
  await assert.rejects(outbound('platform.fixture.echo', 'closed'), { code: 'CLOSED' });
  assert.equal(calls, 1);
});

test('assembly factory cannot be mixed with prebuilt modules or an async result', () => {
  const module = { id: 'fixture', version: '1.0.0', methods: {}, close() {} };
  for (const input of [
    { modules: [module], assemble: () => [module] },
    { assemble: 'invalid' },
    { assemble: () => Promise.resolve([module]) }
  ]) {
    assert.throws(() => createAppRuntime({
      product: 'fixture', ...input, connect: () => assert.fail('connected')
    }), { code: 'INVALID_MESSAGE' });
  }
});

test('invalid wire identity never allocates module resources and invalid factories dispose once', async () => {
  assert.throws(() => createAppRuntime({
    product: 'bad\n', assemble: () => assert.fail('assembled'), connect() {}
  }), { code: 'INVALID_MESSAGE' });
  let closed = 0, call;
  const module = { id: 'fixture', version: '1.0.0', methods: {}, close() { closed++; } };
  assert.throws(() => createAppRuntime({
    product: 'fixture',
    assemble(dependencies) { call = dependencies.call; return [module, module]; },
    connect: () => assert.fail('connected')
  }), { code: 'INVALID_MESSAGE' });
  await Promise.resolve();
  assert.equal(closed, 1);
  await assert.rejects(call('platform.fixture.echo', null), { code: 'CLOSED' });
});
