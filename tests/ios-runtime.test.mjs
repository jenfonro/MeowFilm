import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { installIOSFailureBoundary, parseIOSConfiguration, parseIOSLaunchArguments } from '../apps/ios/runtime/bootstrap.mjs';
import { connect, contract, load, okay, root, simulatedIOS, spawnRuntime } from './ios-runtime-support.mjs';
import { verifyTMDBAssembly } from './tmdb-runtime-assembly-support.mjs';

const entry = join(root, 'apps/ios/runtime/entries/aggregate.mjs');

// Isolated desktop Node probes call the real addon dup and real net.Socket.
// Instrumentation observes ownership without adding production test hooks.
const descriptorProbe = `
  import assert from 'node:assert/strict';
  import fs from 'node:fs';
  import net from 'node:net';
  import { createRequire, syncBuiltinESMExports } from 'node:module';
  const binding = createRequire(import.meta.url)(${JSON.stringify(join(root, 'packages/script-host/build/Release/script_host_native.node'))});
  const nativeDuplicate = binding.duplicateBootstrapDescriptor;
  const originals = [];
  let copied, channel, channelClosed, directCloses = 0;
  binding.duplicateBootstrapDescriptor = descriptor => {
    originals.push(descriptor);
    copied = nativeDuplicate(descriptor);
    return copied;
  };
  const nativeClose = fs.closeSync;
  fs.closeSync = descriptor => {
    if (descriptor === copied) directCloses++;
    return nativeClose(descriptor);
  };
  net.Socket = new Proxy(net.Socket, { construct(target, args) {
    const socket = Reflect.construct(target, args);
    if (Number.isInteger(copied) && args[0]?.fd === copied) {
      channel = socket;
      channelClosed = new Promise(resolve => socket.once('close', resolve));
    }
    return socket;
  } });
  syncBuiltinESMExports();
  const { startIOSAppRuntime } = await import(${JSON.stringify(new URL('../apps/ios/runtime/bootstrap.mjs', import.meta.url).href)});
`;

test('native dup and real Socket construction failures never consume the original fd', { timeout: 30000 }, async t => {
  for (const mode of ['dup-fault', 'invalid-type']) {
    const runtime = spawnRuntime(t, { sendConfiguration: false, fixture: `${descriptorProbe}
      const mode = ${JSON.stringify(mode)};
      const original = mode === 'invalid-type' ? fs.openSync(new URL('./original', import.meta.url), 'w+') : 3;
      if (mode === 'dup-fault') {
        // Inject a real native error by forwarding to a never-opened fd.
        // The App-owned input stays open; no fd is pre-closed by this fixture.
        binding.duplicateBootstrapDescriptor = descriptor => {
          originals.push(descriptor);
          return nativeDuplicate(0x7fffffff);
        };
      }
      await assert.rejects(startIOSAppRuntime({
        argv: ['--meow-bootstrap-fd', String(original)],
        assemble() { throw new Error('must not assemble'); },
      }), { code: mode === 'invalid-type' ? 'ERR_INVALID_FD_TYPE' : 'DESCRIPTOR_UNAVAILABLE' });
      assert.deepEqual(originals, [original]);
      assert.equal(channel, undefined);
      if (mode === 'invalid-type') {
        assert.ok(Number.isInteger(copied));
        assert.notEqual(copied, original);
        assert.equal(directCloses, 1);
        assert.throws(() => fs.fstatSync(copied), { code: 'EBADF' });
        assert.equal(fs.writeSync(original, 'original'), 8);
        const bytes = Buffer.alloc(8);
        assert.equal(fs.readSync(original, bytes, 0, 8, 0), 8);
        assert.equal(bytes.toString(), 'original');
        nativeClose(original);
      } else {
        assert.equal(copied, undefined);
        assert.equal(directCloses, 0);
        assert.equal(fs.fstatSync(original).isSocket(), true);
      }
      process.stdout.write('BOOTSTRAP_FAILURE_OWNERSHIP_OK\\n');
    ` });
    await runtime.closed;
    assert.deepEqual(await runtime.exited, { code: 0, signal: null }, JSON.stringify(runtime.output()));
    assert.equal(runtime.output().stdout, 'BOOTSTRAP_FAILURE_OWNERSHIP_OK\n');
    assert.equal(runtime.output().stderr, '');
  }
});

test('real Socket closure and fd reuse remain safe after startup, stream failure or owner EOF', { timeout: 30000 }, async t => {
  for (const mode of ['assembly-failure', 'socket-error', 'owner-eof']) {
    const runtime = spawnRuntime(t, { fixture: `${descriptorProbe}
      const mode = ${JSON.stringify(mode)}, failure = new Error('fixture assembly failure');
      let moduleClosed = false, runtime;
      const starting = startIOSAppRuntime({ product: 'aggregate', assemble() {
        if (mode === 'assembly-failure') throw failure;
        return [{ id: 'music', version: '0.8.0', contract: 'music.script.v3', methods: {},
          upload() {}, async close() { moduleClosed = true; } }];
      } });
      if (mode === 'assembly-failure') await assert.rejects(starting, error => error === failure);
      else runtime = await starting;
      assert.ok(channel);
      if (mode === 'socket-error') channel.destroy(new Error('fixture socket error'));
      await channelClosed;
      await runtime?.close();
      assert.deepEqual(originals, [3]);
      assert.notEqual(copied, 3);
      assert.equal(directCloses, 0);
      assert.equal(fs.fstatSync(3).isSocket(), true);
      if (mode !== 'assembly-failure') assert.equal(moduleClosed, true);
      const sentinel = fs.openSync(new URL('./sentinel', import.meta.url), 'w+');
      assert.equal(sentinel, copied, 'the closed Socket fd must actually be reused');
      channel.destroy();
      await runtime?.close();
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(directCloses, 0);
      assert.equal(fs.writeSync(sentinel, 'sentinel'), 8);
      const bytes = Buffer.alloc(8);
      assert.equal(fs.readSync(sentinel, bytes, 0, 8, 0), 8);
      assert.equal(bytes.toString(), 'sentinel');
      assert.equal(fs.fstatSync(3).isSocket(), true);
      nativeClose(sentinel);
      process.stdout.write('BOOTSTRAP_REUSED_FD_OK\\n');
    ` });
    if (mode === 'owner-eof') {
      await runtime.endpoint;
      runtime.channel.end();
    }
    await runtime.closed;
    assert.deepEqual(await runtime.exited, { code: 0, signal: null }, JSON.stringify(runtime.output()));
    assert.equal(runtime.output().stdout, 'BOOTSTRAP_REUSED_FD_OK\n');
    assert.equal(runtime.output().stderr, '');
  }
});

test('iOS argv contains only the private bootstrap descriptor', () => {
  assert.deepEqual(parseIOSLaunchArguments(['--meow-bootstrap-fd', '3']), { descriptor: 3 });
  for (const argv of [[], ['--meow-bootstrap-fd'], ['--meow-bootstrap-fd', '3', 'extra'],
    ['--meow-bootstrap-fd', '0'], ['--meow-bootstrap-fd', '2'], ['--meow-bootstrap-fd', '03'],
    ['--meow-bootstrap-fd', '3.0'], ['--meow-bootstrap-fd', '-4'], ['--meow-bootstrap-fd', '2147483648'],
    ['--meow-bootstrap-fd', '3\n'], ['--other-fd', '3'], ['--meow-bootstrap-fd', 3]]) {
    assert.throws(() => parseIOSLaunchArguments(argv), { code: 'INVALID_REQUEST' });
  }
});

test('trusted configuration accepts only session, key and App data root', () => {
  const value = { sessionId: randomUUID(), requestKey: randomBytes(32).toString('hex'), dataRoot: '/data/猫' };
  assert.deepEqual(parseIOSConfiguration(Buffer.from(JSON.stringify(value))), value);
  for (const bad of [null, [], {}, { ...value, extra: true }, { ...value, sessionId: 'invalid' },
    { ...value, requestKey: 'invalid' }, { ...value, dataRoot: 'relative/data' },
    { ...value, dataRoot: '/data\nother' }]) {
    assert.throws(() => parseIOSConfiguration(Buffer.from(JSON.stringify(bad))), { code: 'INVALID_REQUEST' });
  }
});

test('escaped exceptions and rejected promises close the runtime once without exiting the App', async () => {
  const listeners = [];
  const target = { on: (name, listener) => listeners.push({ name, listener }) };
  let closes = 0, reports = 0;
  const fail = installIOSFailureBoundary({ close: async () => { closes++; } },
    { target, report: () => { reports++; throw new Error('reporting must not mask shutdown'); } });
  assert.deepEqual(listeners.map(value => value.name), ['uncaughtException', 'unhandledRejection']);
  for (const { listener } of listeners) listener(new Error('private failure'));
  fail();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(closes, 1);
  assert.equal(reports, 1);
});

test('iOS bootstrap serves the complete HTTP assembly with module-owned storage', { timeout: 60000 }, async t => {
  const runtime = spawnRuntime(t, { entry });
  const { client, baseUrl, closed } = await connect(runtime);
  t.after(() => client.close());
  assert.deepEqual(runtime.args.slice(-2), ['--meow-bootstrap-fd', '3']);
  assert.equal(runtime.args.includes(runtime.configuration.sessionId), false);
  assert.equal(runtime.args.includes(runtime.configuration.requestKey), false);
  assert.equal(runtime.args.includes(runtime.configuration.dataRoot), false);
  const described = okay(await client.describe(), 'runtime.http.v1');
  assert.equal(described.product, 'aggregate');
  assert.equal(described.protocol, 'runtime.http.v1');
  assert.deepEqual(described.modules.map(module => [module.id, module.version, module.contract]), [
    ['music', '0.8.0', 'music.script.v3'], ['cat', '0.2.0', 'cat.script.v2'], ['source', '0.2.0', 'source.runtime.v2'],
    ['tmdb', '0.1.0', 'tmdb.data.v1'],
    ['netease', '0.1.0', 'netease.token.v1'],
  ]);
  for (const method of ['music.script.sources.list', 'music.script.resolve', 'music.script.invoke'])
    assert.ok(described.modules[0].methods.includes(method), method);
  assert.ok(described.modules[2].methods.includes('source.runtime.invoke'));
  assert.deepEqual(described.modules[3].methods, [
    'tmdb.data.request', 'tmdb.data.settings.get', 'tmdb.data.settings.update',
  ]);
  assert.deepEqual(described.modules[4].methods, ['netease.token.get']);
  await verifyTMDBAssembly(t, { client, baseUrl, ...runtime.configuration });
  const instance = okay(await load(client)).item;
  assert.equal(instance.state, 'ready');
  assert.equal(instance.name, 'iOS runtime fixture');
  assert.deepEqual(instance.sites, { tx: { actions: ['musicUrl', 'lyric'], qualitys: ['128k', '320k'] } });
  const resolve = quality => client.call('music.script.resolve', {
    installationId: instance.installationId, platform: 'tx', quality, musicInfo: { songmid: 'm' },
  });
  assert.equal(okay(await resolve('320k')).url, 'http://media.invalid/tx/320k?n=1');
  assert.equal(okay(await resolve('128k')).url, 'http://media.invalid/tx/128k?n=2');
  assert.deepEqual(okay(await client.call('music.script.invoke', {
    installationId: instance.installationId, siteId: 'tx', operation: 'lyric', payload: { musicInfo: { songmid: 'm' } },
  })), { lyric: '[00:00.00]tx', tlyric: '', rlyric: '' });
  assert.deepEqual(JSON.parse(readFileSync(join(runtime.area, 'scripts', 'music', 'data',
    `${instance.installationId}.json`), 'utf8')), { schema: 1, sites: { tx: [{ key: 'count', value: 3 }] } });
  assert.deepEqual(await resolve('flac'), { ok: false, contract, code: 'CAPABILITY_UNAVAILABLE' });
  assert.equal(okay(await client.call('music.script.sources.disable',
    { installationId: instance.installationId })).item.state, 'disabled');
  assert.deepEqual(await resolve('320k'), { ok: false, contract, code: 'SOURCE_DISABLED' });
  const next = okay(await client.call('music.script.sources.enable', { installationId: instance.installationId })).item;
  assert.ok(next.generation > instance.generation);
  assert.equal(okay(await resolve('320k')).url, 'http://media.invalid/tx/320k?n=4');

  const source = 'export default { async searchContent(keyword) { return { list: [{ vod_id: keyword, vod_name: keyword }] }; } };';
  okay(await client.upload('source', 'reader-fixture', 1, Buffer.from(source), { basename: 'source.js' }), 'source.runtime.v2');
  const reader = okay(await client.call('source.runtime.load', { installationId: 'reader-fixture', sourceRevision: 1,
    revision: 1, expectedRevision: 0, protocol: 'cat', version: '1.0.0', metadata: {} }), 'source.runtime.v2').instance;
  const result = okay(await client.call('source.runtime.invoke', { installationId: reader.installationId,
    revision: reader.revision, generation: reader.generation, action: 'search', payload: { keyword: '猫' } }), 'source.runtime.v2');
  assert.deepEqual(result, { list: [{ vod_id: '猫', vod_name: '猫' }] });
  // A single module close never closes the shared listener or another module.
  okay(await client.call('music.script.close'), contract);
  okay(await client.call('source.runtime.status'), 'source.runtime.v2');
  okay(await client.call('cat.script.status'), 'cat.script.v2');
  okay(await client.shutdown(), 'runtime.http.v1');
  assert.equal(okay(await client.describe(), 'runtime.http.v1').modules.length, 4);
  runtime.channel.end();
  await closed;
  await client.close();
  assert.deepEqual(await runtime.exited, { code: 0, signal: null }, JSON.stringify(runtime.output()));
  await assert.rejects(fetch(`${baseUrl}/v1/describe`));
  assert.equal(runtime.output().stderr, '');
});

test('owner EOF drains a running guest before the embedded executor returns', { timeout: 30000 }, async t => {
  const runtime = spawnRuntime(t, { entry });
  const { client, closed } = await connect(runtime);
  t.after(() => client.close());
  const installationId = randomUUID();
  const source = `setInterval(()=>{},1000); lx.on('request',async()=> 'alive');
    lx.send('inited',{sources:{tx:{name:'Fixture',type:'music',actions:['musicUrl'],qualitys:['128k']}}});`;
  okay(await client.upload('music', installationId, 1, Buffer.from(source), { basename: 'timer.js' }));
  okay(await client.call('music.script.sources.add', { installationId, sourceRevision: 1 }));
  assert.equal(okay(await client.call('music.script.sources.enable', { installationId })).item.state, 'ready');
  runtime.channel.end();
  await closed;
  assert.deepEqual(await runtime.exited, { code: 0, signal: null });
  assert.equal(runtime.output().stderr, '');
});

test('an escaped runtime failure closes HTTP and the bootstrap without killing the App host', { timeout: 30000 }, async t => {
  const runtime = spawnRuntime(t, { entry, preload: `${simulatedIOS}
    process.on('SIGUSR2', () => setImmediate(() => { throw new Error('private escaped failure'); }));` });
  const { client, baseUrl, closed } = await connect(runtime);
  t.after(() => client.close());
  assert.equal(okay(await client.describe(), 'runtime.http.v1').product, 'aggregate');
  runtime.child.kill('SIGUSR2');
  await closed;
  assert.deepEqual(await runtime.exited, { code: 0, signal: null });
  await assert.rejects(fetch(`${baseUrl}/v1/describe`));
  assert.equal(runtime.output().stderr, 'IOS_RUNTIME_FAILED\n');
  assert.equal(runtime.output().stdout, '');
});

test('invalid launch and private configuration report only a fixed safe code', { timeout: 30000 }, async t => {
  for (const options of [{ argv: ['--meow-bootstrap-fd', 'invalid'] },
    { configuration: { sessionId: 'invalid' } }]) {
    const runtime = spawnRuntime(t, { entry, ...options });
    assert.deepEqual(await runtime.exited, { code: 1, signal: null });
    assert.equal(runtime.output().stderr, 'IOS_RUNTIME_START_FAILED\n');
    assert.equal(runtime.output().stdout, '');
  }
});

test('owner EOF before configuration completes cancels startup and releases the executor', { timeout: 30000 }, async t => {
  const runtime = spawnRuntime(t, { entry, sendConfiguration: false });
  runtime.channel.end();
  await runtime.closed;
  assert.deepEqual(await runtime.exited, { code: 1, signal: null });
  assert.equal(runtime.output().stderr, 'IOS_RUNTIME_START_FAILED\n');
  assert.equal(runtime.output().stdout, '');
});
