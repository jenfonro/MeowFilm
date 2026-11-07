import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fingerprintInputs, isWindowsBuildInput } from '../scripts/windows/input-fingerprint.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

test('Windows input inventory covers shared packaging, locked configuration and shipped assets', () => {
  for (const path of [
    'scripts/package-runtime.mjs', 'scripts/test-root.mjs', 'scripts/windows/test.ps1',
    'scripts/quickjs-runtime.json', 'apps/runtime/modules/music-script.mjs',
    'scripts/quickjs-native.json', 'native/JavaScript/CMakeLists.txt',
    'native/JavaScript/meowfilm_js.c', 'native/JavaScript/quickjs/quickjs.c',
    'packages/film-rules/film-rules.js', 'LICENSES/quickjs-native-MIT.txt',
    'scripts/quickjs/task-context.patch', 'scripts/quickjs/memory-accounting.patch', 'scripts/quickjs/build.py',
    'packages/script-host/src/quickjs/emscripten-module.mjs',
    'packages/script-host/src/quickjs/emscripten-module.wasm',
    'apps/runtime/modules/cat-script.mjs',
    'apps/runtime/modules/source-runtime.mjs', 'packages/source-runtime/src/index.js',
    'apps/windows/NuGet.Config', 'apps/windows/global.json',
    'LICENSES/node-LICENSE.txt',
    'LICENSES/NOTICE.md',
    'LICENSES/Apache-2.0.txt',
    'LICENSES/quickjs-emscripten-LICENSE.txt',
    'apps/windows/src/MeowFilm.Desktop/Theme.xaml', 'apps/windows/app/app.manifest',
    'apps/windows/app/packages.lock.json', 'apps/windows/assets/logo.ico',
    'tests/windows-runtime.test.mjs', 'packages/script-host/src/index.js',
    'package-lock.json', '.gitignore', '.gitattributes', 'build-windows.ps1'
  ]) assert.equal(isWindowsBuildInput(path), true, path);
  for (const path of [
    'docs/windows/music-implementation.md', 'apps/windows/README.md',
    'apps/windows/src/MeowFilm.Media/IMPLEMENTATION.md',
    'apps/ios/MeowFilm/App.swift', '.local/test-results/native.log',
    'native/MeowKit/Package.swift', 'scripts/generate-ios-project.py',
    'scripts/package-ios-runtime.mjs', 'scripts/prepare-ios-runtime.py',
    'scripts/ios-node-preflight/probe.mjs', 'scripts/ios-node-mobile.json',
    'scripts/tests/test_source_scope.py', 'tests/ios-runtime.test.mjs', 'tests/ios-runtime-support.mjs',
    'LICENSES/nodejs-mobile-LICENSE.txt',
    'build/meowfilm.exe', 'tmp/test-results/native.log', 'tmp/working/项目工作记录.md'
  ]) assert.equal(isWindowsBuildInput(path), false, path);
});

test('Windows fingerprint binds bytes and paths; inventory order and duplicates are irrelevant', () => {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'meowfilm-inputs-'));
  try {
    const files = ['build-windows.ps1', 'scripts/package-runtime.mjs', 'apps/windows/NuGet.Config'];
    for (const path of files) {
      mkdirSync(dirname(join(root, path)), { recursive: true });
      writeFileSync(join(root, path), 'first');
    }
    const original = fingerprintInputs(root, files);
    assert.equal(fingerprintInputs(root, [...files.toReversed(), files[0], 'docs/README.md']), original);
    const ios = 'scripts/ios-node-preflight/probe.mjs';
    mkdirSync(dirname(join(root, ios)), { recursive: true });
    writeFileSync(join(root, ios), "import './app/bootstrap.mjs';");
    assert.equal(fingerprintInputs(root, [...files, ios]), original);
    writeFileSync(join(root, ios), "import './changed-runtime-input.mjs';");
    assert.equal(fingerprintInputs(root, [...files, ios]), original);
    writeFileSync(join(root, files[0]), 'second');
    assert.notEqual(fingerprintInputs(root, files), original);
    writeFileSync(join(root, files[0]), 'first');
    assert.notEqual(fingerprintInputs(root, [files[0]]), original);
    assert.throws(() => fingerprintInputs(root, [...files, 'scripts/windows/missing.mjs']), /ENOENT/);
    assert.throws(() => fingerprintInputs(root, ['README.md']), /No Windows code inputs/);
  } finally {
    assert.equal(dirname(resolve(root)), resolve(temporaryRoot));
    assertOwnedPath(temporaryRoot, root);
    assert.ok(basename(root).startsWith('meowfilm-inputs-'));
    rmSync(root, { recursive: true, force: true });
  }
});
