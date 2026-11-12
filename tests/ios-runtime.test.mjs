import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { installIOSFailureBoundary, parseIOSConfiguration, parseIOSLaunchArguments } from '../apps/ios/runtime/bootstrap.mjs';
import { connect, contract, load, okay, root, simulatedIOS, spawnRuntime } from './ios-runtime-support.mjs';
import { verifyPanAndDoubanAssembly, verifyTMDBAssembly } from './tmdb-runtime-assembly-support.mjs';
import { seedSourceLibrary, verifyRecoveredSourceLibrary } from './source-library-assembly-support.mjs';

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

test('private configuration carries only lifecycle session and App data root', () => {
  const value = { sessionId: randomUUID(), dataRoot: '/data/猫' };
  assert.deepEqual(parseIOSConfiguration(Buffer.from(JSON.stringify(value))), value);
  for (const bad of [null, [], {}, { ...value, extra: true }, { ...value, sessionId: 'invalid' },
    { ...value, dataRoot: 'relative/data' },
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
  assert.equal(runtime.args.includes(runtime.configuration.dataRoot), false);
  const described = okay(await client.describe(), 'runtime.http.v1');
  assert.equal(described.product, 'aggregate');
  assert.equal(described.protocol, 'runtime.http.v1');
  assert.deepEqual(described.modules.map(module => [module.id, module.version, module.contract]), [
    ['music', '0.8.0', 'music.script.v3'], ['cat', '0.2.0', 'cat.script.v2'], ['source', '0.3.0', 'source.runtime.v2'],
    ['pan', '0.1.0', 'pan.runtime.v1'], ['tmdb', '0.1.0', 'tmdb.data.v1'],
    ['douban', '0.1.0', 'douban.data.v1'], ['film', '0.1.0', 'film.playback.v1'],
    ['netease', '0.1.0', 'netease.token.v1'],
  ]);
  for (const method of ['music.script.sources.list', 'music.script.resolve', 'music.script.invoke'])
    assert.ok(described.modules[0].methods.includes(method), method);
  assert.ok(described.modules[2].methods.includes('source.runtime.invoke'));
  assert.deepEqual(described.modules.find(module => module.id === 'tmdb').methods, [
    'tmdb.data.request', 'tmdb.data.settings.get', 'tmdb.data.settings.update',
  ]);
  assert.deepEqual(described.modules[7].methods, ['netease.token.get']);
  await verifyTMDBAssembly(t, { client, baseUrl });
  await verifyPanAndDoubanAssembly(client);
  for (const method of ['pan.settings.get', 'tmdb.data.settings.get', 'douban.data.settings.get',
    'film.settings.get', 'film.library.get']) assert.equal((await client.call(method)).ok, true, method);
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
  assert.deepEqual(okay(await client.call('source.runtime.rules.process', {
    request: { operation: 'identity', book: { name: '《测试书》', author: '作者：甲' } },
  }), 'source.runtime.v2'),
  { name: '测试书', author: '甲', key: JSON.stringify(['book', '测试书', '甲']) });
  // A single module close never closes the shared listener or another module.
  okay(await client.call('music.script.close'), contract);
  okay(await client.call('source.runtime.status'), 'source.runtime.v2');
  okay(await client.call('cat.script.status'), 'cat.script.v2');
  okay(await client.shutdown(), 'runtime.http.v1');
  assert.equal(okay(await client.describe(), 'runtime.http.v1').modules.length, 8);
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

test('iOS App entry restores its novel source library and private state in a new process', { timeout: 30000 }, async t => {
  const first = spawnRuntime(t, { entry });
  const connection = await connect(first);
  t.after(() => connection.client.close());
  const source = await seedSourceLibrary(connection.client);
  okay(await connection.client.shutdown(), 'runtime.http.v1');
  first.channel.end();
  await connection.closed;
  await connection.client.close();
  assert.deepEqual(await first.exited, { code: 0, signal: null });
  assert.equal(first.output().stderr, '');

  const second = spawnRuntime(t, { entry, configuration: { dataRoot: first.configuration.dataRoot } });
  const recovered = await connect(second);
  try {
    await verifyRecoveredSourceLibrary(recovered.client, source);
    okay(await recovered.client.shutdown(), 'runtime.http.v1');
    second.channel.end();
    await recovered.closed;
    assert.deepEqual(await second.exited, { code: 0, signal: null });
    assert.equal(second.output().stderr, '');
  } finally { await recovered.client.close(); }
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

test('invalid launch and private configuration identify only their static stage and category', { timeout: 30000 }, async t => {
  for (const [stage, options] of [
    ['arguments', { argv: ['--meow-bootstrap-fd', 'invalid-private-argument'] }],
    ['configuration', { configuration: { sessionId: 'invalid-private-session' } }],
  ]) {
    const runtime = spawnRuntime(t, { entry, ...options });
    assert.deepEqual(await runtime.exited, { code: 1, signal: null });
    assert.equal(runtime.output().stderr, `IOS_RUNTIME_START_FAILED stage=${stage} category=INVALID_REQUEST\n`);
    assert.equal(runtime.output().stdout, '');
  }
});

test('startup diagnostics never serialize unknown errors, paths, credentials or arbitrary stages',
  { timeout: 30000 }, async t => {
    const runtime = spawnRuntime(t, { sendConfiguration: false, fixture: `
      import { reportIOSRuntimeStartFailure } from ${JSON.stringify(new URL('../apps/ios/runtime/bootstrap.mjs', import.meta.url).href)};
      const secret = '/private/var/user-data?cookie=fixture-private-token';
      for (const code of ['STORAGE_ERROR', 'CORRUPT_STORAGE', 'ERR_DLOPEN_FAILED', 'EACCES', secret]) {
        reportIOSRuntimeStartFailure(Object.assign(new Error(secret), {
          code, path: secret, stack: secret, cause: { credentials: secret },
        }), 'pan');
      }
      for (const error of [undefined, null, secret, { code: { toString() { throw new Error(secret); } } },
        Object.defineProperty({}, 'code', { get() { throw new Error(secret); } })]) {
        reportIOSRuntimeStartFailure(error, 'pan');
      }
      reportIOSRuntimeStartFailure({ code: 'STORAGE_ERROR' }, secret);
    ` });
    assert.deepEqual(await runtime.exited, { code: 1, signal: null });
    const categories = ['STORAGE_ERROR', 'CORRUPT_STORAGE', 'ERR_DLOPEN_FAILED', 'EACCES',
      'UNKNOWN', 'UNKNOWN', 'UNKNOWN', 'UNKNOWN', 'UNKNOWN', 'UNKNOWN'];
    assert.equal(runtime.output().stderr, categories.map(category =>
      `IOS_RUNTIME_START_FAILED stage=pan category=${category}\n`).join('') +
      'IOS_RUNTIME_START_FAILED stage=unknown category=STORAGE_ERROR\n');
    assert.equal(runtime.output().stdout, '');
  });

test('bootstrap native, channel, listener and endpoint failures retain ownership and report their stage',
  { timeout: 30000 }, async t => {
    for (const [mode, category] of [
      ['native-binding', 'DESCRIPTOR_UNAVAILABLE'], ['bootstrap-channel', 'ERR_INVALID_FD_TYPE'],
      ['listen', 'EADDRINUSE'], ['endpoint', 'EPIPE'],
    ]) {
      const runtime = spawnRuntime(t, {
        sendConfiguration: mode === 'listen' || mode === 'endpoint',
        fixture: `${descriptorProbe}
          import http from 'node:http';
          const { reportIOSRuntimeStartFailure } = await import(${JSON.stringify(new URL('../apps/ios/runtime/bootstrap.mjs', import.meta.url).href)});
          const mode = ${JSON.stringify(mode)}, failure = Object.assign(
            new Error('/private/var/fixture?token=private-startup-error'), { code: ${JSON.stringify(category)} });
          const original = mode === 'bootstrap-channel'
            ? fs.openSync(new URL('./original', import.meta.url), 'w+') : 3;
          if (mode === 'native-binding') {
            binding.duplicateBootstrapDescriptor = () => nativeDuplicate(0x7fffffff);
          }
          if (mode === 'listen') http.Server.prototype.listen = () => { throw failure; };
          const write = net.Socket.prototype.write;
          net.Socket.prototype.write = function(...args) {
            if (mode === 'endpoint' && this === channel) {
              queueMicrotask(() => args.at(-1)(failure));
              return true;
            }
            return Reflect.apply(write, this, args);
          };
          let stage, closes = 0;
          try {
            await startIOSAppRuntime({
              product: 'aggregate', argv: ['--meow-bootstrap-fd', String(original)],
              onStage: value => { stage = value; },
              assemble: () => [{ id: 'music', version: '0.8.0', contract: 'music.script.v3',
                methods: {}, upload() {}, close() { closes++; } }],
            });
            assert.fail('startup must reject');
          } catch (error) {
            if (mode === 'listen' || mode === 'endpoint') assert.equal(error, failure);
            reportIOSRuntimeStartFailure(error, stage);
          }
          if (channelClosed) await channelClosed;
          assert.equal(closes, mode === 'listen' || mode === 'endpoint' ? 1 : 0);
          assert.equal(fs.fstatSync(3).isSocket(), true);
          if (mode === 'bootstrap-channel') nativeClose(original);
          process.stdout.write('BOOTSTRAP_DIAGNOSTIC_OK\\n');
        `,
      });
      await runtime.closed;
      assert.deepEqual(await runtime.exited, { code: 1, signal: null }, JSON.stringify(runtime.output()));
      assert.equal(runtime.output().stderr, `IOS_RUNTIME_START_FAILED stage=${mode} category=${category}\n`);
      assert.equal(runtime.output().stdout, 'BOOTSTRAP_DIAGNOSTIC_OK\n');
    }
  });

test('the iOS entry reports each module initialization failure and closes previously assembled owners',
  { timeout: 30000 }, async t => {
    const stages = ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease'];
    const replacements = [
      ['../apps/runtime/modules/music-script.mjs', [['createMusicScriptModule', 'music']]],
      ['../apps/runtime/modules/cat-script.mjs', [['createCatScriptModule', 'cat']]],
      ['../apps/runtime/modules/source-runtime.mjs', [['createSourceRuntimeModule', 'source']]],
      ['../apps/runtime/modules/pan-runtime.mjs', [['createPanRuntimeModule', 'pan']]],
      ['../apps/runtime/modules/metadata-data.mjs', [['createTMDBDataModule', 'tmdb'], ['createDoubanDataModule', 'douban']]],
      ['../apps/runtime/modules/netease-token.mjs', [['createNeteaseTokenModule', 'netease']]],
    ].map(([path, exports]) => [new URL(path, import.meta.url).href, exports.map(([name, stage]) =>
      `export function ${name}() { return globalThis.fixtureModule(${JSON.stringify(stage)}); }`).join('\n')]);
    replacements.push([new URL('../apps/runtime/entry.mjs', import.meta.url).href,
      `export function connectFilmPlayback(modules) { modules.push(globalThis.fixtureModule('film')); }`]);
    for (const [stage, code, category] of [
      ...stages.map(stage => [stage, 'STORAGE_ERROR', 'STORAGE_ERROR']),
      ['pan', '/private/var?cookie=unknown-private-code', 'UNKNOWN'],
      ['assembly', undefined, 'INVALID_REQUEST'],
    ]) {
      const runtime = spawnRuntime(t, { fixture: `
        import { registerHooks } from 'node:module';
        const replacements = new Map(${JSON.stringify(replacements)});
        globalThis.fixtureModule = stage => {
          if (stage === ${JSON.stringify(stage)}) throw Object.assign(
            new Error('/private/var/accounts?token=private-module-error'), { code: ${JSON.stringify(code)} });
          return { close() { process.stdout.write('CLOSED ' + stage + '\\n'); } };
        };
        registerHooks({ load(url, context, next) {
          if (replacements.has(url)) return { format: 'module', shortCircuit: true, source: replacements.get(url) };
          return next(url, context);
        } });
        await import(${JSON.stringify(new URL('../apps/ios/runtime/entries/aggregate.mjs', import.meta.url).href)});
      ` });
      await runtime.closed;
      assert.deepEqual(await runtime.exited, { code: 1, signal: null }, JSON.stringify(runtime.output()));
      assert.equal(runtime.output().stderr, `IOS_RUNTIME_START_FAILED stage=${stage} category=${category}\n`);
      const previous = stage === 'assembly' ? stages : stages.slice(0, stages.indexOf(stage));
      assert.equal(runtime.output().stdout, previous.map(stage => `CLOSED ${stage}\n`).join(''));
      await assert.rejects(runtime.endpoint, { message: 'BOOTSTRAP_CLOSED' });
    }
  });

test('owner EOF before configuration completes cancels startup and releases the executor', { timeout: 30000 }, async t => {
  const runtime = spawnRuntime(t, { entry, sendConfiguration: false });
  runtime.channel.end();
  await runtime.closed;
  assert.deepEqual(await runtime.exited, { code: 1, signal: null });
  assert.equal(runtime.output().stderr, 'IOS_RUNTIME_START_FAILED stage=configuration category=CLOSED\n');
  assert.equal(runtime.output().stdout, '');
});
