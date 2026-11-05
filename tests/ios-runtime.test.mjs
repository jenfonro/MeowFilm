import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { installIOSFailureBoundary, parseIOSLaunchArguments } from '../apps/ios/runtime/bootstrap.mjs';
import {
  connect, contract, load, nativeStorage, okay, root, simulatedIOS, spawnRuntime,
} from './ios-runtime-support.mjs';

const entry = join(root, 'apps/ios/runtime/entries/aggregate.mjs');

test('iOS launch accepts only the ordered descriptor/session/data protocol', () => {
  const session = randomUUID();
  assert.deepEqual(parseIOSLaunchArguments(['--meow-fd', '3', '--meow-session', session, '--meow-data', '/data/scripts']),
    { descriptor: 3, session, dataRoot: '/data/scripts' });
  for (const argv of [
    [], ['--meow-fd', '3', '--meow-session', session, '--meow-data'],
    ['--meow-fd', '3', '--meow-session', session, '--meow-data', '/data', 'extra'],
    ['--meow-fd', '0', '--meow-session', session, '--meow-data', '/data'],
    ['--meow-fd', '2', '--meow-session', session, '--meow-data', '/data'],
    ['--meow-fd', '03', '--meow-session', session, '--meow-data', '/data'],
    ['--meow-fd', '3.0', '--meow-session', session, '--meow-data', '/data'],
    ['--meow-fd', '-4', '--meow-session', session, '--meow-data', '/data'],
    ['--meow-fd', '1234567890', '--meow-session', session, '--meow-data', '/data'],
    ['--meow-fd', '3', '--meow-session', session.toUpperCase(), '--meow-data', '/data'],
    ['--meow-fd', '3', '--meow-session', `${session}\n`, '--meow-data', '/data'],
    ['--meow-fd', '3', '--meow-session', 'not-a-uuid', '--meow-data', '/data'],
    ['--meow-fd', '3', '--meow-session', session, '--meow-data', 'relative/data'],
    ['--meow-fd', '3', '--meow-session', session, '--meow-data', '/data\nother'],
    ['--meow-session', session, '--meow-fd', '3', '--meow-data', '/data'],
    ['--meow-fd', 3, '--meow-session', session, '--meow-data', '/data'],
  ]) {
    assert.throws(() => parseIOSLaunchArguments(argv), { code: 'INVALID_MESSAGE' }, JSON.stringify(argv));
  }
});

test('an escaped runtime failure closes the runtime once and never exits the hosting App', async () => {
  const listeners = [];
  const target = { on: (name, listener) => listeners.push({ name, listener }) };
  let closes = 0, reports = 0;
  const fail = installIOSFailureBoundary({ close: async () => { closes++; } },
    { target, report: () => { reports++; throw new Error('reporting must not mask shutdown'); } });
  assert.deepEqual(listeners.map(value => value.name), ['uncaughtException']);
  listeners[0].listener(new Error('first'));
  listeners[0].listener(new Error('second'));
  fail();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(closes, 1);
  assert.equal(reports, 1);
});

test('simulated iOS entry serves LX over the native descriptor with request-bound native storage', { timeout: 60000 }, async t => {
  const runtime = spawnRuntime(t, { entry });
  const storage = nativeStorage();
  const { peer, closed } = connect(runtime, storage.methods);
  const described = await peer.call('runtime.describe', null);
  assert.equal(described.product, 'aggregate');
  assert.equal(described.protocol, 1);
  assert.deepEqual(described.modules.map(module => module.id), ['music', 'cat']);
  assert.ok(described.modules[0].methods.includes('music.script.invoke'));
  assert.equal(described.modules.some(module => module.id === 'source'), false);

  const grant = randomUUID();
  const instance = okay(await load(peer, { installationId: 'fixture', grant })).instance;
  assert.equal(instance.state, 'ready');
  assert.equal(instance.protocol, 'lx');
  assert.deepEqual(instance.capabilities.tx.actions, ['musicUrl', 'lyric']);
  assert.deepEqual(instance.capabilities.tx.qualitys, ['128k', '320k']);
  const invoke = (operation, payload, generation = instance.generation) => peer.call('music.script.invoke', {
    installationId: 'fixture', revision: 1, generation, siteId: 'tx', operation, payload,
  });
  assert.equal(okay(await invoke('musicUrl', { type: '320k', musicInfo: { songmid: 'm' } })).value,
    'http://media.invalid/tx/320k?n=1');
  assert.equal(okay(await invoke('musicUrl', { type: '128k', musicInfo: { songmid: 'm' } })).value,
    'http://media.invalid/tx/128k?n=2');
  assert.deepEqual(okay(await invoke('lyric', { musicInfo: { songmid: 'm' } })).value,
    { lyric: '[00:00.00]tx', tlyric: '', rlyric: '' });
  assert.deepEqual(storage.calls.map(call => call.operation), ['get', 'set', 'get', 'set', 'get', 'set']);
  for (const { message } of storage.calls) {
    assert.equal(message.grant, grant);
    assert.equal(message.installationId, 'fixture');
    assert.equal(message.siteId, 'tx');
    assert.equal(message.generation, instance.generation);
    assert.equal(message.key, 'count');
  }
  const stale = await invoke('musicUrl', { type: '320k', musicInfo: {} }, instance.generation + 1);
  assert.deepEqual(stale, { ok: false, contract, code: 'STALE_SOURCE' });
  const unsupported = await invoke('musicUrl', { type: 'flac', musicInfo: {} });
  assert.deepEqual(unsupported, { ok: false, contract, code: 'CAPABILITY_UNAVAILABLE' });
  assert.deepEqual(okay(await peer.call('music.script.unload',
    { installationId: 'fixture', revision: 1, generation: instance.generation })),
  { ok: true, contract, unloaded: true, cleanupComplete: true });
  const retired = await invoke('musicUrl', { type: '320k', musicInfo: {} });
  assert.deepEqual(retired, { ok: false, contract, code: 'SOURCE_NOT_FOUND' });
  assert.deepEqual(okay(await peer.call('music.script.status', {})).instances, []);

  // A reload uses a new grant and generation; the retired grant cannot be reused.
  const next = okay(await load(peer, { installationId: 'fixture', revision: 2, grant: randomUUID() })).instance;
  assert.ok(next.generation > instance.generation);
  assert.deepEqual(await load(peer, { installationId: 'fixture', revision: 3, expectedRevision: 2, grant }),
    { ok: false, contract, code: 'PERMISSION_DENIED' });
  peer.close();
  await closed;
  const exit = await runtime.exited;
  assert.deepEqual(exit, { code: 0, signal: null }, JSON.stringify(runtime.output()));
  assert.equal(runtime.output().stderr, '');
});

test('an escaped runtime failure closes the descriptor without crashing the host process', { timeout: 30000 }, async t => {
  // The parent triggers the failure only after describe proves the runtime is assembled.
  const runtime = spawnRuntime(t, { entry, preload: `${simulatedIOS}
    process.on('SIGUSR2', () => setImmediate(() => { throw new Error('private escaped failure'); }));` });
  const { peer, closed } = connect(runtime);
  assert.equal((await peer.call('runtime.describe', null)).product, 'aggregate');
  runtime.child.kill('SIGUSR2');
  await closed;
  const exit = await runtime.exited;
  assert.deepEqual(exit, { code: 0, signal: null });
  assert.equal(runtime.output().stderr, 'IOS_RUNTIME_FAILED\n');
  assert.equal(runtime.output().stdout, '');
});

test('invalid launch arguments never assemble modules and emit only a fixed code', { timeout: 30000 }, async t => {
  const runtime = spawnRuntime(t, { entry, argv: ['--meow-fd', '3', '--meow-session', 'invalid', '--meow-data', '/data'] });
  const exit = await runtime.exited;
  assert.deepEqual(exit, { code: 1, signal: null });
  assert.equal(runtime.output().stderr, 'IOS_RUNTIME_START_FAILED\n');
  assert.equal(runtime.output().stdout, '');
});
