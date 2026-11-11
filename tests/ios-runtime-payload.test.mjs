import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fixture } from './ios-native-package-support.mjs';

test('packaged iOS JavaScript resolves only its offline closure on the build host', async t => {
  const f = fixture(t);
  await f.pack();
  // This is import/storage/HTTP evidence only. iOS native invocation and true
  // NodeMobile exit run in the signed preflight and real App host.
  const entry = join(f.output, 'closure-probe.mjs');
  writeFileSync(entry, `
    import assert from 'node:assert/strict';
    import { registerHooks } from 'node:module';
    import { fileURLToPath } from 'node:url';
    import { isAbsolute, relative, sep, join } from 'node:path';
    import { readFileSync } from 'node:fs';
    const payload = process.cwd();
    const unusedProvider = new URL('./app/modules/metadata/douban.mjs', import.meta.url).href;
    registerHooks({ resolve(specifier, context, next) {
      const result = next(specifier, context);
      if (result.url === unusedProvider) throw new SyntaxError('DOUBAN_PROVIDER_UNAVAILABLE');
      if (result.url.startsWith('file:')) {
        const path = relative(payload, fileURLToPath(result.url));
        if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path))
          throw new Error('PAYLOAD_DEPENDENCY_ESCAPED');
      }
      return result;
    } });
    const { createRuntime, createRuntimeClient, createAppRuntime, createMusicScriptModule,
      createCatScriptModule, createSourceRuntimeModule, createPanRuntimeModule,
      createTMDBDataModule, createDoubanDataModule, connectFilmPlayback,
      createNeteaseTokenModule } = await import('./app/entry.mjs');
    const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86';
    const runtime = createRuntime({ sessionId, dataRoot: join(payload, 'data'), source: { platform: 'ios' } });
    let client;
    try {
      await runtime.start();
      client = createRuntimeClient({ baseUrl: runtime.baseUrl });
      const described = await client.describe();
      assert.equal(described.ok, true);
      assert.deepEqual(described.value.modules.map(value => [value.id, value.version]),
        [['music', '0.8.0'], ['cat', '0.2.0'], ['source', '0.2.0']]);
      const listed = await client.call('music.script.sources.list');
      assert.equal(listed.ok, true);
      assert.equal((await client.shutdown()).ok, true);
    } finally { await runtime.close(); await client?.close(); }
    const dataRoot = join(payload, 'app-data');
    const modules = [
      createMusicScriptModule({ dataRoot }), createCatScriptModule({ dataRoot: join(dataRoot, 'cat') }),
      createSourceRuntimeModule({ platform: 'ios' }), createPanRuntimeModule({ dataRoot }),
      createTMDBDataModule({ dataRoot }), createDoubanDataModule({ dataRoot }),
    ];
    connectFilmPlayback(modules, { dataRoot });
    modules.push(createNeteaseTokenModule());
    const app = createAppRuntime({ sessionId, modules });
    try {
      await app.start();
      client = createRuntimeClient({ baseUrl: app.baseUrl });
      const described = await client.describe();
      assert.equal(described.ok, true);
      const manifest = JSON.parse(readFileSync('runtime-manifest.json', 'utf8'));
      assert.deepEqual(described.value.modules.map(value => value.id), manifest.modules);
      assert.deepEqual(described.value.modules.map(value => [value.id, value.version, value.contract]), [
        ['music', '0.8.0', 'music.script.v3'], ['cat', '0.2.0', 'cat.script.v2'],
        ['source', '0.2.0', 'source.runtime.v2'], ['pan', '0.1.0', 'pan.runtime.v1'],
        ['tmdb', '0.1.0', 'tmdb.data.v1'], ['douban', '0.1.0', 'douban.data.v1'],
        ['film', '0.1.0', 'film.playback.v1'], ['netease', '0.1.0', 'netease.token.v1'],
      ]);
      assert.deepEqual(described.value.modules[4].methods, [
        'tmdb.data.request', 'tmdb.data.settings.get', 'tmdb.data.settings.update',
      ]);
      assert.deepEqual(described.value.modules[7].methods, ['netease.token.get']);
      for (const method of ['pan.settings.get', 'tmdb.data.settings.get', 'douban.data.settings.get',
        'film.settings.get', 'film.library.get']) {
        const reply = await client.call(method);
        assert.equal(reply.ok, true, JSON.stringify(reply));
      }
      const settings = await client.call('tmdb.data.settings.get');
      assert.equal(settings.ok, true);
      assert.equal(settings.contract, 'tmdb.data.v1');
      assert.equal(settings.value.apiKey, '');
      assert.equal(settings.value.accessToken, '');
      assert.equal((await client.shutdown()).ok, true);
    } finally { await app.close(); await client?.close(); }
    console.log('IOS_JS_CLOSURE_OK');
  `);
  try {
    const result = spawnSync(process.execPath, [entry], {
      cwd: f.output, env: { ...process.env, NODE_OPTIONS: '', NODE_PATH: '' },
      encoding: 'utf8', timeout: 30_000,
    });
    assert.equal(result.signal, null, result.stderr);
    assert.equal(result.status, 0, result.stderr || result.error?.message);
    assert.match(result.stdout, /IOS_JS_CLOSURE_OK/);
  } finally { rmSync(entry); }
});
