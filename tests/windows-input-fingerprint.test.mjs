import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fingerprintInputs, isWindowsBuildInput } from '../scripts/windows/input-fingerprint.mjs';
import { temporaryRoot, assertOwnedPath } from '../scripts/windows/working-paths.mjs';

const metadataFiles = [
  'apps/runtime/modules/metadata-data.mjs', 'apps/runtime/modules/metadata/settings.mjs',
  'apps/runtime/modules/metadata/network.mjs', 'apps/runtime/modules/metadata/tmdb.mjs',
  'apps/runtime/modules/metadata/douban.mjs',
];

test('Windows input inventory covers shared packaging, locked configuration and shipped assets', () => {
  for (const path of [
    'scripts/package-runtime.mjs', 'scripts/test-root.mjs', 'scripts/windows/test.ps1',
    'scripts/script-host-native.json', 'apps/runtime/modules/music-script.mjs',
    'native/JavaScript/patches/regexp-compile-interrupt.patch', 'native/JavaScript/quickjs/quickjs.c',
    'packages/film-rules/film-rules.js', 'packages/film-rules/playback-rules.js', 'packages/film-search/film-search.js',
    'scripts/quickjs/build-native.mjs', 'scripts/quickjs/build-source-sdk.mjs',
    'scripts/windows/ensure-runtime-native.mjs', 'packages/script-host/native/task-context.patch',
    'packages/script-host/native/script_host_native.cc', 'packages/script-host/binding.gyp',
    'apps/runtime/bootstrap.mjs', 'apps/runtime/entry.mjs', 'apps/runtime/client.mjs', 'apps/runtime/contracts.mjs',
    'apps/runtime/modules/source-candidates.mjs', ...metadataFiles,
    'apps/runtime/modules/cat-script.mjs',
    'apps/runtime/modules/source-runtime.mjs', 'packages/source-runtime/src/index.js',
    'apps/windows/NuGet.Config', 'apps/windows/global.json',
    'LICENSES/node-LICENSE.txt',
    'LICENSES/NOTICE.md',
    'LICENSES/Apache-2.0.txt',
    'LICENSES/quickjs-ng-LICENSE.txt', 'LICENSES/openssl-LICENSE.txt', 'LICENSES/zlib-LICENSE.txt',
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
    'scripts/ios/package-runtime.mjs', 'scripts/prepare-ios-runtime.py',
    'scripts/ios-node-preflight/probe.mjs', 'scripts/ios-node-mobile.json',
    'scripts/tests/test_source_scope.py', 'tests/ios-runtime.test.mjs', 'tests/ios-runtime-support.mjs',
    'LICENSES/nodejs-mobile-LICENSE.txt',
    'build/meowfilm.exe', 'tmp/test-results/native.log', 'tmp/working/项目工作记录.md',
    'apps/windows/app/bin/Release/meowfilm.exe', 'apps/windows/app/obj/generated.cs',
    'packages/script-host/build/Release/source-sdk.js', 'packages/script-host/node_modules/example/index.js'
  ]) assert.equal(isWindowsBuildInput(path), false, path);
});

test('new shared Node helpers participate in the fingerprint without registration', () => {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'meowfilm-inputs-'));
  try {
    const entry = 'scripts/windows/tool.mjs', helper = 'scripts/fresh-helper.mjs';
    mkdirSync(dirname(join(root, entry)), { recursive: true });
    writeFileSync(join(root, entry), "import '../fresh-helper.mjs';");
    const before = fingerprintInputs(root, [entry]);
    writeFileSync(join(root, helper), 'export const value = 1;');
    const included = fingerprintInputs(root, [entry, helper]);
    assert.notEqual(included, before);
    writeFileSync(join(root, helper), 'export const value = 2;');
    assert.notEqual(fingerprintInputs(root, [entry, helper]), included);
  } finally {
    assertOwnedPath(temporaryRoot, root);
    rmSync(root, { recursive: true, force: true });
  }
});

test('Windows fingerprint binds bytes and paths; inventory order and duplicates are irrelevant', () => {
  mkdirSync(temporaryRoot, { recursive: true });
  const root = mkdtempSync(join(temporaryRoot, 'meowfilm-inputs-'));
  try {
    const files = ['build-windows.ps1', 'scripts/package-runtime.mjs', 'apps/windows/NuGet.Config', ...metadataFiles];
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
    for (const path of metadataFiles) {
      writeFileSync(join(root, path), 'second');
      assert.notEqual(fingerprintInputs(root, files), original, `${path} bytes affect the fingerprint`);
      writeFileSync(join(root, path), 'first');
      assert.equal(fingerprintInputs(root, files), original);
      assert.notEqual(fingerprintInputs(root, files.filter(file => file !== path)), original,
        `${path} participates in the inventory`);
    }
    assert.throws(() => fingerprintInputs(root, [...files, 'scripts/windows/missing.mjs']), /ENOENT/);
    assert.throws(() => fingerprintInputs(root, ['README.md']), /No Windows code inputs/);
  } finally {
    assert.equal(dirname(resolve(root)), resolve(temporaryRoot));
    assertOwnedPath(temporaryRoot, root);
    assert.ok(basename(root).startsWith('meowfilm-inputs-'));
    rmSync(root, { recursive: true, force: true });
  }
});
