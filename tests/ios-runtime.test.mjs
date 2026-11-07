import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
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

test('simulated iOS entry manages and serves LX over the native descriptor with module-owned storage', { timeout: 60000 }, async t => {
  const runtime = spawnRuntime(t, { entry });
  const storage = nativeStorage();
  const { peer, closed } = connect(runtime, storage.methods);
  const described = await peer.call('runtime.describe', null);
  assert.equal(described.product, 'aggregate');
  assert.equal(described.protocol, 1);
  assert.deepEqual(described.modules.map(module => module.id), ['music', 'cat']);
  for (const method of ['music.script.sources.list', 'music.script.resolve', 'music.script.invoke'])
    assert.ok(described.modules[0].methods.includes(method), method);
  assert.equal(described.modules.some(module => module.id === 'source'), false);

  const instance = okay(await load(peer)).item;
  assert.equal(instance.state, 'ready');
  assert.equal(instance.name, 'iOS runtime fixture');
  assert.deepEqual(instance.sites, { tx: { actions: ['musicUrl', 'lyric'], qualitys: ['128k', '320k'] } });
  const resolve = quality => peer.call('music.script.resolve', {
    installationId: instance.installationId, platform: 'tx', quality, musicInfo: { songmid: 'm' },
  });
  assert.equal(okay(await resolve('320k')).url, 'http://media.invalid/tx/320k?n=1');
  assert.equal(okay(await resolve('128k')).url, 'http://media.invalid/tx/128k?n=2');
  assert.deepEqual(okay(await peer.call('music.script.invoke', {
    installationId: instance.installationId, siteId: 'tx', operation: 'lyric', payload: { musicInfo: { songmid: 'm' } },
  })).value, { lyric: '[00:00.00]tx', tlyric: '', rlyric: '' });
  assert.deepEqual(storage.calls, []);
  assert.deepEqual(JSON.parse(readFileSync(join(runtime.area, 'scripts', 'music', 'data',
    `${instance.installationId}.json`), 'utf8')), { schema: 1, sites: { tx: [{ key: 'count', value: 3 }] } });
  assert.deepEqual(await resolve('flac'), { ok: false, contract, code: 'CAPABILITY_UNAVAILABLE' });
  assert.equal(okay(await peer.call('music.script.sources.disable',
    { installationId: instance.installationId })).item.state, 'disabled');
  assert.deepEqual(await resolve('320k'), { ok: false, contract, code: 'SOURCE_DISABLED' });
  const next = okay(await peer.call('music.script.sources.enable', { installationId: instance.installationId })).item;
  assert.ok(next.generation > instance.generation);
  assert.equal(okay(await resolve('320k')).url, 'http://media.invalid/tx/320k?n=4');
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
