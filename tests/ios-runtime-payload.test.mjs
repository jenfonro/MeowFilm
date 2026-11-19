import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { fixture } from './ios-native-package-support.mjs';

test('packaged iOS JavaScript resolves only its offline closure on the build host', async t => {
  const f = fixture(t);
  await f.pack();
  // This is import/HTTP evidence only. iOS storage, native invocation and true
  // NodeMobile exit run in the signed preflight and real App host.
  const entry = join(f.output, 'closure-probe.mjs');
  writeFileSync(entry, `
    import assert from 'node:assert/strict';
    import { registerHooks } from 'node:module';
    import { fileURLToPath } from 'node:url';
    import { isAbsolute, relative, sep, join } from 'node:path';
    const payload = process.cwd();
    const unusedProvider = new URL('./app/modules/metadata/douban.mjs', import.meta.url).href;
    let commonOnly = true;
    registerHooks({ resolve(specifier, context, next) {
      const result = next(specifier, context);
      if (commonOnly && result.url === unusedProvider) throw new SyntaxError('DOUBAN_PROVIDER_UNAVAILABLE');
      if (result.url.startsWith('file:')) {
        const path = relative(payload, fileURLToPath(result.url));
        if (path === '..' || path.startsWith('..' + sep) || isAbsolute(path))
          throw new Error('PAYLOAD_DEPENDENCY_ESCAPED');
      }
      return result;
    } });
    const entryExports = await import('./app/entry.mjs');
    for (const name of ['createRuntime', 'createRuntimeClient', 'createAppRuntime', 'createMusicScriptModule',
      'createCatScriptModule', 'createSourceRuntimeModule', 'createPanRuntimeModule', 'createTMDBDataModule',
      'createDoubanDataModule', 'connectFilmPlayback', 'connectMusicPlatform', 'createNeteaseTokenModule']) {
      assert.equal(typeof entryExports[name], 'function', name);
    }
    commonOnly = false;
    await import('./app/modules/metadata/douban.mjs');
    const { createAppRuntime, createRuntimeClient, createNeteaseTokenModule } = entryExports;
    const sessionId = 'a090d240-1ad0-48ca-8fc8-094ee3864f86';
    // This real owner has no native persistence or SDK work until token.get.
    // Storage-backed owners execute in MeowfilmRuntimeTests, with the signed
    // iOS framework and the App's actual shared database, not a macOS stand-in.
    const app = createAppRuntime({ sessionId, modules: [createNeteaseTokenModule()] });
    let client;
    try {
      await app.start();
      client = createRuntimeClient({ baseUrl: app.baseUrl });
      const described = await client.describe();
      assert.equal(described.ok, true);
      assert.deepEqual(described.value.modules, [{
        id: 'netease', version: '0.1.0', contract: 'netease.token.v1', methods: ['netease.token.get'],
      }]);
      const manifest = JSON.parse(readFileSync('runtime-manifest.json', 'utf8'));
      assert.deepEqual(manifest.modules, ['music', 'cat', 'source', 'pan', 'tmdb', 'douban', 'film', 'netease', 'music-platform']);
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
